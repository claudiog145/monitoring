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
/// Only clubs in <c>brand.Information</c> are fetched. Hotels and cars match ClubId. Cruises match
/// IcePartnerId on the exception partner id. Air is not queried. Hotels and cars share the hotel
/// app: the hotel pass drops car-journey rows and the car pass keeps them, both using
/// <see cref="IncidentIngestionPlanner.IsCarException"/>. A car row with no club id is kept only
/// when its URL host matches <c>brand.Information</c>. Anything still without a club is dropped.
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

    private static readonly string DropCarExceptions = BuildCarExceptionFilter(keep: false);
    private static readonly string KeepCarExceptions = BuildCarExceptionFilter(keep: true);

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
        domain.Trim().ToLowerInvariant() is "air";

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

        var signals = queried.Signals.ToList();
        if (domain == "cars") signals = AssignCarClubFromHost(signals);
        signals = signals.Where(signal => BelongsToBrand(domain, signal, allowed.Ids)).ToList();
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
            source = insightsDomain == "cars"
                ? InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, insightsDomain, options.Environment)
                : InsightsSourceResolver.Resolve(insightsOptions.Value, insightsDomain, options.Environment);
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
            if (insightsDomain == "cars") kql += "\n" + KeepCarExceptions;
            var clubColumn = UsesIcePartnerId(insightsDomain) ? "PartnerId" : "ClubId";
            var clubFilter = "| where " + clubColumn + " in (" + QuoteIds(clubIds) + ")";
            // Car server exceptions carry the host on the request and no club id on the exception.
            if (insightsDomain == "cars") clubFilter += " or isempty(ClubId)";
            kql += "\n" + clubFilter;
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

    /// <summary>
    /// Car server exceptions carry the site on the URL and no VacationClubId. The brand table
    /// host is that site, so <c>stays.tmobiletravel.com</c> becomes club 15639. The request URL
    /// is used when the exception URL itself is not a brand host. Two clubs that share a host
    /// keep the lower club id. A row that still has no club is dropped.
    /// </summary>
    private List<ExceptionSignal> AssignCarClubFromHost(List<ExceptionSignal> signals)
    {
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var club in BrandClubOptions.ForDomain(_brandRows ?? [], "cars")
                     .OrderBy(club => club.ClubId, StringComparer.Ordinal))
        {
            if (!string.IsNullOrEmpty(club.Host)) hosts.TryAdd(club.Host, club.Value);
        }

        if (hosts.Count == 0) return signals;
        return signals.Select(signal => WithBrandClub(signal, hosts)).ToList();
    }

    private static ExceptionSignal WithBrandClub(ExceptionSignal signal, IReadOnlyDictionary<string, string> hosts)
    {
        if (!string.IsNullOrWhiteSpace(signal.ClubId)) return signal;
        var clubId = ClubForHost(signal.Url, hosts) ?? ClubForHost(signal.RequestUrl, hosts);
        return clubId is null ? signal : signal with { ClubId = clubId };
    }

    private static string? ClubForHost(string? url, IReadOnlyDictionary<string, string> hosts)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        return hosts.TryGetValue(uri.Host, out var clubId) ? clubId : null;
    }

    private static bool BelongsToBrand(string domain, ExceptionSignal signal, IReadOnlySet<string> allowed)
    {
        var carException = IncidentIngestionPlanner.IsCarException(
            $"{signal.Url} {signal.RequestUrl}", signal.OperationName);
        if (domain == "hotels" && carException) return false;
        if (domain == "cars" && !carException) return false;

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
        NullIfBlank(row.GetString("RequestUrl")),
        ExceptionSource.Normalize(row.GetString("Source")));

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Hotel pass drops a row, and the car pass keeps a row, when the exception URL, the joined
    /// request URL, or the operation name is a car journey. customDimensions.url can hide
    /// RequestUrl, which is the column that prefers CarSearch, CarBooking, and /cars/.
    /// </summary>
    private static string BuildCarExceptionFilter(bool keep)
    {
        string[] columns = ["Url", "RequestUrl", "Operation"];
        string[] markers = ["CarSearch", "CarBooking", "CarLocationSearch", "searchlocations", "/cars/"];
        var comparison = keep ? "has" : "!has";
        var joiner = keep ? " or " : " and ";
        var clauses = columns.SelectMany(column => markers.Select(marker => $"{column} {comparison} \"{marker}\""));
        return "| where " + string.Join(joiner, clauses);
    }

    private static IncidentSyncPass Skip(string domain, string reason) =>
        new(domain, false, 0, 0, 0, 0, reason);
}
