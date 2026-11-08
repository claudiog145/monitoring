using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Data.SqlClient;

namespace JourneyMonitor.Infrastructure.Fabric;

/// <summary>
/// Microsoft Entra service-principal connection to the Fabric warehouse that hosts
/// <c>dbo.vw_Alert_HotelBookingKPIs</c>. The connection string, client id, client secret,
/// and tenant id are read from Key Vault with the Azure CLI session already on this PC.
/// Visual Studio's test host often cannot see <c>az</c>, so the CLI install directory is
/// put on PATH. No browser and no account picker.
/// </summary>
public static class HotelBookingKpiConnection
{
    public static readonly Uri KeyVaultUri = new("https://platform-rel-kv-qa.vault.azure.net/");

    public const string ConnectionStringSecretName = "platform-reliability-fabric-connectionString";

    public const string ClientIdSecretName = "platform-reliability-fabric-clientId";

    public const string ClientSecretSecretName = "platform-reliability-fabric-clientSecret";

    public const string TenantIdSecretName = "platform-reliability-fabric-tenantId";

    public const string AlertStatusSql =
        """
        /* Agentic Portal - hotel booking alert query (run ONCE per poll; every run recomputes the whole view)

           STATUS VALUES
             STALE      data not current (FreshnessLagMin > 10)        -> suppress; raise a data-freshness alert if it persists
             OUTAGE     bookings ~stopped vs a normally active entity  -> investigate immediately (any grain)
             RED        sudden, large change                            -> investigate
             AMBER      worse than normal but not sudden/large          -> dashboard only, do NOT trigger agents
             LOW_VOLUME too little volume to judge                      -> dashboard only
             OK

           NOISE CONTROLS (tune in the cfg row at the bottom)
             - Ratio alerts only when the entity's 4-week median booking count >= MinVol (default 50).
             - RED requires: <= RedPct of the 4-week median AND worse than every one of the last 4 weeks AND
               <= SuddenPct of last week (W1). The W1 test separates sudden breaks from slow multi-week trends.
             - OUTAGE: bookings <= OutagePct of the median where the median >= MinOutageVol.
             - Negative-margin spike: at least MinNegVol negative-margin bookings in the window.
             - Margin-rate drop: margin % down by >= MarginPtsAmber / MarginPtsRed points vs the 4 weeks.
             - Deep-negative margin: ABSOLUTE rule, no baseline. Any booking with margin worse than -25% of its
               sales is RED at any volume, so a bug that persists for weeks cannot become "normal". Page from the
               HOUR rows (each booking appears once per hourly poll); DAY rows give 24h context.
               Back-tested on the Aug-Oct 2026 multi-room underpayment bug: fired from the first case, no false
               positives outside the bug period.
           Margin covers bookings not cancelled (a cancelled booking carries zero margin), so there is no
           cancelled or net margin.
           Routing hints are populated only when the related status is OUTAGE / RED / AMBER; otherwise NULL. */
        SELECT
            s.Grain, s.AlertLevel, s.WindowStartPT, s.WindowEndPT,
            s.VacationClubClientID, s.ClientName, s.VacationClubID, s.VacationClubName,
            s.GrossBookingsCurrent, s.CancelBookingsCurrent, s.NetBookingsCurrent,
            s.GrossSalesCurrent, s.CancelSalesCurrent, s.NetSalesCurrent,
            s.MarginSalesCurrent, s.COGSCurrent, s.MarginCurrent, s.MarginPctCurrent, s.MarginPct4wk,
            s.NegMarginBookingsCurrent, s.NegMarginBookingsMedian4wk, s.NegMarginAmountCurrent,
            s.DeepNegMarginBookingsCurrent, s.MultiRoomNegMarginBookingsCurrent,
            s.FreshnessLagMin,
            s.GrossBookingsStatus, s.CancelBookingsStatus, s.NetBookingsStatus,
            s.GrossSalesStatus, s.NetSalesStatus,
            s.MarginStatus, s.MarginRateStatus, s.NegMarginStatus, s.DeepNegMarginStatus,
            CASE WHEN s.NetBookingsStatus IN ('OUTAGE','RED','AMBER') THEN s.NetBookingsDriverRaw END AS NetBookingsDriver,
            CASE WHEN s.NetSalesStatus    IN ('RED','AMBER')          THEN s.NetSalesDriverRaw END    AS NetSalesDriver,
            CASE WHEN s.MarginStatus      IN ('RED','AMBER')          THEN s.MarginDriverRaw END      AS MarginDriver,
            CASE WHEN s.GrossSalesStatus  IN ('RED','AMBER')          THEN s.ValuePerBookingDropRaw END AS ValuePerBookingDrop,
            CASE WHEN s.DeepNegMarginStatus = 'RED'                   THEN s.DeepNegMarginDriverRaw END AS DeepNegMarginDriver
        FROM (
        SELECT
            k.*,

            -- Gross bookings: drop / outage
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.GrossBookingsMedian4wk >= cfg.MinOutageVol
                  AND k.GrossBookingsCurrent <= cfg.OutagePct * k.GrossBookingsMedian4wk THEN 'OUTAGE'
                 WHEN k.GrossBookingsMedian4wk < cfg.MinVol THEN 'LOW_VOLUME'
                 WHEN k.GrossBookingsCurrent <= cfg.RedPct * k.GrossBookingsMedian4wk
                  AND k.GrossBookingsCurrent <  k.GrossBookingsMin4wk
                  AND k.GrossBookingsCurrent <= cfg.SuddenPct * k.GrossBookingsW1 THEN 'RED'
                 WHEN k.GrossBookingsCurrent <= cfg.AmberPct * k.GrossBookingsMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS GrossBookingsStatus,

            -- Cancellations: spike
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.CancelBookingsMedian4wk < cfg.MinCancelVol THEN 'LOW_VOLUME'
                 WHEN k.CancelBookingsCurrent >= 2 * k.CancelBookingsMedian4wk
                  AND k.CancelBookingsCurrent >  k.CancelBookingsMax4wk
                  AND k.CancelBookingsCurrent >= 1.5 * k.CancelBookingsW1 THEN 'RED'
                 WHEN k.CancelBookingsCurrent >= 1.5 * k.CancelBookingsMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS CancelBookingsStatus,

            -- Net bookings: drop / outage
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.NetBookingsMedian4wk >= cfg.MinOutageVol
                  AND k.NetBookingsCurrent <= cfg.OutagePct * k.NetBookingsMedian4wk THEN 'OUTAGE'
                 WHEN k.NetBookingsMedian4wk < cfg.MinVol THEN 'LOW_VOLUME'
                 WHEN k.NetBookingsCurrent <= cfg.RedPct * k.NetBookingsMedian4wk
                  AND k.NetBookingsCurrent <  k.NetBookingsMin4wk
                  AND k.NetBookingsCurrent <= cfg.SuddenPct * k.NetBookingsW1 THEN 'RED'
                 WHEN k.NetBookingsCurrent <= cfg.AmberPct * k.NetBookingsMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS NetBookingsStatus,

            -- Gross sales: drop (volume floor uses booking COUNT, not dollars)
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.GrossBookingsMedian4wk < cfg.MinVol OR k.GrossSalesMedian4wk <= 0 THEN 'LOW_VOLUME'
                 WHEN k.GrossSalesCurrent <= cfg.RedPct * k.GrossSalesMedian4wk
                  AND k.GrossSalesCurrent <  k.GrossSalesMin4wk
                  AND k.GrossSalesCurrent <= cfg.SuddenPct * k.GrossSalesW1 THEN 'RED'
                 WHEN k.GrossSalesCurrent <= cfg.AmberPct * k.GrossSalesMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS GrossSalesStatus,

            -- Net sales: drop
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.NetBookingsMedian4wk < cfg.MinVol OR k.NetSalesMedian4wk <= 0 THEN 'LOW_VOLUME'
                 WHEN k.NetSalesCurrent <= cfg.RedPct * k.NetSalesMedian4wk
                  AND k.NetSalesCurrent <  k.NetSalesMin4wk
                  AND k.NetSalesCurrent <= cfg.SuddenPct * k.NetSalesW1 THEN 'RED'
                 WHEN k.NetSalesCurrent <= cfg.AmberPct * k.NetSalesMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS NetSalesStatus,

            -- Margin (bookings not cancelled): drop
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.GrossBookingsMedian4wk < cfg.MinVol OR k.MarginMedian4wk <= 0 THEN 'LOW_VOLUME'
                 WHEN k.MarginCurrent <= cfg.RedPct * k.MarginMedian4wk
                  AND k.MarginCurrent <  k.MarginMin4wk
                  AND k.MarginCurrent <= cfg.SuddenPct * k.MarginW1 THEN 'RED'
                 WHEN k.MarginCurrent <= cfg.AmberPct * k.MarginMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS MarginStatus,

            -- Margin rate: margin % down vs the prior 4 weeks (percentage points)
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.GrossBookingsMedian4wk < cfg.MinVol
                   OR k.MarginPctCurrent IS NULL OR k.MarginPct4wk IS NULL THEN 'LOW_VOLUME'
                 WHEN k.MarginPctCurrent <= k.MarginPct4wk - cfg.MarginPtsRed   THEN 'RED'
                 WHEN k.MarginPctCurrent <= k.MarginPct4wk - cfg.MarginPtsAmber THEN 'AMBER'
                 ELSE 'OK' END AS MarginRateStatus,

            -- Negative-margin bookings: spike (count-based floor, applies at any entity size)
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.NegMarginBookingsCurrent < cfg.MinNegVol THEN 'OK'
                 WHEN k.NegMarginBookingsCurrent >= 2 * k.NegMarginBookingsMedian4wk
                  AND k.NegMarginBookingsCurrent >  k.NegMarginBookingsMax4wk
                  AND k.NegMarginBookingsCurrent >= 1.5 * k.NegMarginBookingsW1 THEN 'RED'
                 WHEN k.NegMarginBookingsCurrent >= 1.5 * k.NegMarginBookingsMedian4wk THEN 'AMBER'
                 ELSE 'OK' END AS NegMarginStatus,

            -- Deep-negative margin: absolute rule (no baseline, no volume floor)
            CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE'
                 WHEN k.DeepNegMarginBookingsCurrent >= cfg.MinDeepNeg THEN 'RED'
                 ELSE 'OK' END AS DeepNegMarginStatus,

            -- Routing hints (raw; the outer SELECT keeps them only when an alert fired)
            CASE WHEN (k.GrossBookingsMedian4wk - k.GrossBookingsCurrent) >= (k.CancelBookingsCurrent - k.CancelBookingsMedian4wk)
                 THEN 'GROSS_DROP' ELSE 'CANCEL_SPIKE' END AS NetBookingsDriverRaw,
            CASE WHEN (k.GrossSalesMedian4wk - k.GrossSalesCurrent) >= (k.CancelSalesCurrent - k.CancelSalesMedian4wk)
                 THEN 'GROSS_DROP' ELSE 'CANCEL_SPIKE' END AS NetSalesDriverRaw,
            CASE WHEN k.MarginPctCurrent <= k.MarginPct4wk - cfg.MarginPtsAmber
                 THEN 'MARGIN_RATE' ELSE 'VOLUME' END AS MarginDriverRaw,
            CASE WHEN k.GrossBookingsCurrent > cfg.AmberPct * k.GrossBookingsMedian4wk
                  AND k.GrossSalesCurrent <= cfg.AmberPct * k.GrossSalesMedian4wk
                 THEN 1 ELSE 0 END AS ValuePerBookingDropRaw,
            CASE WHEN k.MultiRoomNegMarginBookingsCurrent > 0 THEN 'MULTI_ROOM' ELSE 'OTHER' END AS DeepNegMarginDriverRaw
        FROM dbo.vw_Alert_HotelBookingKPIs AS k
        CROSS JOIN (VALUES (50, 10, 10, 5, 0.10, 0.50, 0.75, 0.75, 0.05, 0.10, 1))
                AS cfg (MinVol, MinOutageVol, MinCancelVol, MinNegVol, OutagePct, RedPct, AmberPct, SuddenPct,
                        MarginPtsAmber, MarginPtsRed, MinDeepNeg)
        ) AS s
        OPTION (LABEL = 'agentic_portal_hotel_kpi');
        """;

    /// <summary>
    /// One HOUR row for the Reliability Watch card. A null <c>@VacationClubID</c> is the
    /// TOTAL row (every club). A club id is that club only. Conversion is not in the view.
    /// </summary>
    public const string BusinessHealthSql =
        """
        SELECT
            AlertLevel, Grain, WindowStartPT, WindowEndPT,
            VacationClubClientID, ClientName, VacationClubID, VacationClubName,
            NetBookingsCurrent, NetBookingsW1,
            CancelBookingsCurrent, CancelBookingsW1,
            NetSalesCurrent, NetSalesW1
        FROM dbo.vw_Alert_HotelBookingKPIs
        WHERE Grain = 'HOUR'
          AND (
                (@VacationClubID IS NULL AND AlertLevel = 'TOTAL')
             OR (AlertLevel = 'CLUB' AND VacationClubID = @VacationClubID)
              )
        OPTION (LABEL = 'agentic_portal_hotel_kpi');
        """;

    public static string WithServicePrincipal(string connectionString, string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Fabric connection string is empty.", nameof(connectionString));
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Fabric service principal client id is empty.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("Fabric service principal client secret is empty.", nameof(clientSecret));

        return new SqlConnectionStringBuilder(connectionString)
        {
            UserID = clientId.Trim(),
            Password = clientSecret,
        }.ConnectionString;
    }

    public static async Task<string> CreateConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        PutAzureCliOnPath();
        var secrets = new SecretClient(KeyVaultUri, new AzureCliCredential(new AzureCliCredentialOptions
        {
            ProcessTimeout = TimeSpan.FromSeconds(30),
        }));
        var connectionString = (await RequireSecretAsync(secrets, ConnectionStringSecretName, cancellationToken)).Trim();
        var clientId = (await RequireSecretAsync(secrets, ClientIdSecretName, cancellationToken)).Trim();
        var clientSecret = await RequireSecretAsync(secrets, ClientSecretSecretName, cancellationToken);
        var tenantId = (await RequireSecretAsync(secrets, TenantIdSecretName, cancellationToken)).Trim();
        return CreateConnectionString(connectionString, clientId, clientSecret, tenantId);
    }

    public static string CreateConnectionString(string connectionString, string clientId, string clientSecret, string tenantId)
    {
        RegisterServicePrincipal(tenantId);
        return WithServicePrincipal(connectionString, clientId, clientSecret);
    }

    private static async Task<string> RequireSecretAsync(SecretClient secrets, string name, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await secrets.GetSecretAsync(name, cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(secret.Value.Value))
                throw new InvalidOperationException($"Key Vault secret '{name}' in {KeyVaultUri.Host} has no value.");
            return secret.Value.Value;
        }
        catch (AuthenticationFailedException ex)
        {
            throw new InvalidOperationException(
                "Could not read Key Vault platform-rel-kv-qa from the Azure CLI session. Run az login once in a terminal, then rerun the test. " + ex.Message,
                ex);
        }
    }

    private static string? servicePrincipalTenantId;
    private static int providerRegistered;

    private static void RegisterServicePrincipal(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException($"Key Vault secret '{TenantIdSecretName}' in {KeyVaultUri.Host} has no value.");

        Volatile.Write(ref servicePrincipalTenantId, tenantId.Trim());
        if (Interlocked.Exchange(ref providerRegistered, 1) == 1) return;

        SqlAuthenticationProvider.SetProvider(
            SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
            new FabricServicePrincipalProvider());
    }

    private static void PutAzureCliOnPath()
    {
        string? found = null;
        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        string[] candidates =
        [
            string.IsNullOrWhiteSpace(programW6432) ? "" : Path.Combine(programW6432, "Microsoft SDKs", "Azure", "CLI2", "wbin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Azure CLI", "wbin"),
        ];
        foreach (var dir in candidates)
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "az.cmd")))
            {
                found = dir;
                break;
            }
        }

        if (found is null) return;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", found + Path.PathSeparator + path);
    }

    private sealed class FabricServicePrincipalProvider : SqlAuthenticationProvider
    {
        public override bool IsSupported(SqlAuthenticationMethod authentication) =>
            authentication == SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;

        public override async Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
        {
            var tenantId = Volatile.Read(ref servicePrincipalTenantId)
                ?? throw new InvalidOperationException($"Key Vault secret '{TenantIdSecretName}' in {KeyVaultUri.Host} has no value.");
            var resource = parameters.Resource.TrimEnd('/');
            var scope = resource.EndsWith("/.default", StringComparison.Ordinal) ? resource : resource + "/.default";
            var credential = new ClientSecretCredential(tenantId, parameters.UserId, parameters.Password);
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]));
            return new SqlAuthenticationToken(token.Token, token.ExpiresOn.UtcDateTime);
        }
    }
}
