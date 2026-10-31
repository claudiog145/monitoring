using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

public sealed record IncidentSyncPass(
    string Domain,
    bool Stored,
    int IncidentCount,
    int InsertedIncidents,
    int UpdatedIncidents,
    int InsertedOccurrences,
    string? SkipReason);

/// <summary>
/// Runs the exception KQL for each configured domain and stores the grouped incidents.
/// Same timer scope as journey sync (window and environment). Club is taken from each
/// exception row, not from the single default club, because one error in two clubs is two incidents.
/// Only clubs in <c>brand.Information</c> are fetched. Hotels match ClubId. Cruises match
/// IcePartnerId on the exception partner id. Air and cars are not queried. Car-journey rows
/// that share the hotel app are dropped with <see cref="IncidentIngestionPlanner.IsCarException"/>.
/// </summary>
public sealed class SyncIncidentsService(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IIncidentStore incidentStore,
    IBrandCatalog brandCatalog,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<JourneySyncOptions> syncOptions)
{
    private static readonly IReadOnlySet<string> NoClubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly string DropCarExceptions = BuildDropCarExceptions();

    private IReadOnlyList<BrandClub>? _brandRows;
    private string? _brandSkipReason;
    private bool _brandsLoaded;

    public async Task<IReadOnlyList<IncidentSyncPass>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var passes = new List<IncidentSyncPass>();
        foreach (var domain in syncOptions.Value.EnumerateDomains())
        {
            if (SkipsExceptions(domain)) continue;
            passes.Add(await SyncAsync(domain, cancellationToken));
        }

        return passes;
    }

    public async Task<IncidentSyncPass> SyncAsync(string domain, CancellationToken cancellationToken)
    {
        var normalized = domain.Trim().ToLowerInvariant();
        if (SkipsExceptions(normalized))
        {
            return Skip(normalized, $"{normalized} exceptions are not ingested.");
        }

        return await SyncDomainAsync(normalized, cancellationToken);
    }

    private static bool SkipsExceptions(string domain) =>
        domain.Trim().ToLowerInvariant() is "air" or "cars";

    private async Task<IncidentSyncPass> SyncDomainAsync(string domain, CancellationToken cancellationToken)
    {
        var allowed = await BrandClubIdsAsync(domain, cancellationToken);
        if (allowed.SkipReason is not null)
        {
            return Skip(domain, allowed.SkipReason);
        }

        var queried = await QueryAsync(domain, allowed.Ids, cancellationToken);
        if (queried.SkipReason is not null)
        {
            return Skip(domain, queried.SkipReason);
        }

        var signals = queried.Signals.Where(signal => BelongsToBrand(domain, signal, allowed.Ids)).ToList();
        return await IngestAsync(domain, signals, cancellationToken);
    }

    /// <summary>
    /// Hotels use <c>brand.Information.ClubId</c>. Cruises use <c>IcePartnerId</c>.
    /// </summary>
    private async Task<(IReadOnlySet<string> Ids, string? SkipReason)> BrandClubIdsAsync(
        string domain, CancellationToken cancellationToken)
    {
        var (rows, skip) = await LoadBrandsAsync(cancellationToken);
        if (skip is not null) return (NoClubs, skip);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var club in BrandClubOptions.ForDomain(rows, domain))
        {
            if (ClubFilter.IsValidClubId(club.Value)) ids.Add(club.Value);
        }

        if (ids.Count == 0) return (NoClubs, "brand.Information has no clubs for this domain.");
        return (ids, null);
    }

    private async Task<(IReadOnlyList<BrandClub> Rows, string? SkipReason)> LoadBrandsAsync(
        CancellationToken cancellationToken)
    {
        if (_brandsLoaded) return (_brandRows ?? [], _brandSkipReason);

        _brandsLoaded = true;
        if (!brandCatalog.IsConfigured)
        {
            _brandSkipReason = "brand.Information is not configured.";
            return ([], _brandSkipReason);
        }

        try
        {
            _brandRows = await brandCatalog.GetAllAsync(cancellationToken);
            return (_brandRows, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _brandSkipReason = $"brand.Information read failed: {ex.Message}";
            return ([], _brandSkipReason);
        }
    }

    private async Task<(IReadOnlyList<ExceptionSignal> Signals, string? SkipReason)> QueryAsync(
        string insightsDomain, IReadOnlySet<string> clubIds, CancellationToken cancellationToken)
    {
        var options = syncOptions.Value;
        if (!ClubFilter.TryParseWindow(options.Window, out _))
        {
            return ([], $"window '{options.Window}' is not a valid time range");
        }

        InsightsSource source;
        try
        {
            source = InsightsSourceResolver.Resolve(insightsOptions.Value, insightsDomain, options.Environment);
        }
        catch (InsightsConfigurationException ex)
        {
            return ([], ex.Message);
        }

        try
        {
            var kql = Regex.Replace(
                kqlProvider.GetIncidentExceptionsKql(),
                @"let win = \w+;",
                $"let win = {options.Window};");
            if (insightsDomain == "hotels") kql += "\n" + DropCarExceptions;
            kql += "\n| where " + (UsesIcePartnerId(insightsDomain) ? "PartnerId" : "ClubId")
                + " in (" + QuoteIds(clubIds) + ")";
            var rows = await telemetrySource.QueryAsync(
                new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId),
                cancellationToken);
            return (rows.Select(row => ToSignal(row, insightsDomain)).ToList(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], ex.Message);
        }
    }

    private static bool UsesIcePartnerId(string domain) =>
        domain.Trim().ToLowerInvariant() is "cruises" or "cruise";

    private static string QuoteIds(IEnumerable<string> clubIds) =>
        string.Join(", ", clubIds
            .Select(id => id.Trim())
            .Where(ClubFilter.IsValidClubId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"\"{id}\""));

    private static bool BelongsToBrand(string domain, ExceptionSignal signal, IReadOnlySet<string> allowed)
    {
        if (domain == "hotels" && IncidentIngestionPlanner.IsCarException(
            $"{signal.Url} {signal.RequestUrl}", signal.OperationName))
        {
            return false;
        }

        return allowed.Contains(signal.ClubId.Trim());
    }

    private async Task<IncidentSyncPass> IngestAsync(
        string domain, IReadOnlyList<ExceptionSignal> signals, CancellationToken cancellationToken)
    {
        var options = syncOptions.Value;
        var plan = IncidentIngestionPlanner.Plan(domain, options.Environment, signals);
        var result = await incidentStore.IngestAsync(
            new IncidentIngestRequest(
                domain,
                options.Environment.Trim().ToLowerInvariant(),
                options.Window,
                options.ClubId,
                plan.Incidents),
            cancellationToken);

        return new IncidentSyncPass(
            domain,
            true,
            plan.Incidents.Count,
            result.InsertedIncidents,
            result.UpdatedIncidents,
            result.InsertedOccurrences,
            null);
    }

    private static ExceptionSignal ToSignal(TelemetryJourneyRow row, string domain) => new(
        UsesIcePartnerId(domain) ? row.GetString("PartnerId") : row.GetString("ClubId"),
        row.GetString("ExceptionType"),
        row.GetString("Component"),
        row.GetString("Operation"),
        row.GetString("Message"),
        NullIfBlank(row.GetString("StackTrace")),
        row.GetString("ItemId"),
        row.GetString("OperationId"),
        row.GetDateTimeOffset("OccurredAt") ?? DateTimeOffset.UtcNow,
        NullIfBlank(row.GetString("MemberId")),
        NullIfBlank(row.GetString("SessionId")),
        NullIfBlank(row.GetString("CorrelationId")),
        NullIfBlank(row.GetString("Url")),
        NullIfBlank(row.GetString("RequestUrl")));

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Drops a hotel-app row when the exception URL, the joined request URL, or the operation
    /// name is a car journey. customDimensions.url can hide RequestUrl, which is the column
    /// that prefers CarSearch, CarBooking, and /cars/.
    /// </summary>
    private static string BuildDropCarExceptions()
    {
        string[] columns = ["Url", "RequestUrl", "Operation"];
        string[] markers = ["CarSearch", "CarBooking", "CarLocationSearch", "searchlocations", "/cars/"];
        var clauses = columns.SelectMany(column => markers.Select(marker => $"{column} !has \"{marker}\""));
        return "| where " + string.Join(" and ", clauses);
    }

    private static IncidentSyncPass Skip(string domain, string reason) =>
        new(domain, false, 0, 0, 0, 0, reason);
}
