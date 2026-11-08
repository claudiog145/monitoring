using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.UseCases;

/// <param name="ProfileKey">Contract v6 §4.5: the company; blank means the active one.</param>
public sealed record OverviewRequest(
    string? Domain,
    string? ClubId,
    string? Environment,
    string? Win,
    string? SearchId,
    string? ProfileKey = null);

public sealed record OverviewOutcome(OverviewResult Result, int StatusCode);

public sealed record OverviewResult
{
    public required string Source { get; init; }
    public string Win { get; init; } = "";
    public string? Domain { get; init; }
    public string? ClubId { get; init; }
    public string? Environment { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public string PlatformStatus { get; init; } = "unknown";
    public IReadOnlyList<OverviewKpi> Tiles { get; init; } = [];

    /// <summary>Contract v6 §4.5: the company every section below belongs to. Null on the legacy path.</summary>
    public string? ProfileKey { get; init; }
    public OverviewLabels? Labels { get; init; }
    /// <summary>Which widgets apply to this company; incumbent-only widgets are listed as not applicable.</summary>
    public IReadOnlyList<OverviewWidget>? Widgets { get; init; }
    public IReadOnlyList<OverviewPillar>? Pillars { get; init; }
    public IReadOnlyList<OverviewKpiSummary>? Kpis { get; init; }
    public OverviewExceptionSummary? Exceptions { get; init; }
    public IReadOnlyList<OverviewInvestigation>? Investigations { get; init; }
    public IReadOnlyList<OverviewEntity>? TopEntities { get; init; }

    /// <summary>Whether the incumbent-only Fabric business health applies (drives the Fabric configuration warning).</summary>
    public bool FabricApplies =>
        Widgets is null
        || Widgets.Any(w => w.Key == OverviewWidgetKeys.HotelBusinessHealth && w.Applicable);
}

/// <summary>
/// Executive Overview. Availability is the last 24 hours of journey calls for Hotels and Cruises.
/// The other tiles follow domain, club, environment, time range, and id.
/// An omitted domain is All, which is Hotels and Cruises.
///
/// Contract v6 §4.5: with a profile reader the overview is composed for ONE company (the requested one,
/// or the active one) - tiles, pillars, KPIs, exceptions, Triage investigations and top affected entities
/// all come from that company only.
/// </summary>
public sealed class GetOverviewService(
    IOverviewReader overviewReader,
    IProfileOverviewReader? profileReader = null,
    ICompanyProfileStore? profiles = null,
    IPillarHealthReader? pillarReader = null,
    ITriageInvestigationStore? triageStore = null,
    KpiProfileResolver? profileResolver = null,
    TimeProvider? time = null)
{
    private static readonly string[] ValidDomains = ["hotels", "cars", "cruises", "air"];
    private static readonly TimeSpan AvailabilityWindow = TimeSpan.FromHours(24);
    public const int TopEntityCount = 5;
    public const int InvestigationCount = 5;
    public const int MaxEntityKeyLength = 256;

    private readonly KpiProfileResolver _resolver = profileResolver ?? new KpiProfileResolver(profileStore: profiles);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<OverviewOutcome> GetAsync(OverviewRequest request, CancellationToken cancellationToken)
    {
        if (profileReader is not null && profiles is not null)
        {
            return await GetForProfileAsync(request, profileReader, profiles, cancellationToken);
        }

        var domain = NormalizeDomain(request.Domain);
        if (domain is null && !IsAllDomains(request.Domain))
        {
            return Error(400, request, "domain must be hotels, cars, cruises, air or all");
        }

        var clubId = string.IsNullOrWhiteSpace(request.ClubId) ? null : request.ClubId.Trim();
        if (clubId is not null && !ClubFilter.IsValidClubId(clubId))
        {
            return Error(400, request, "club must be a numeric id");
        }

        string environment;
        try
        {
            environment = InsightsEnvironment.Normalize(request.Environment);
        }
        catch (InsightsConfigurationException ex)
        {
            return Error(400, request, ex.Message);
        }

        var win = string.IsNullOrWhiteSpace(request.Win) ? "15m" : request.Win.Trim();
        if (!ClubFilter.TryParseWindow(win, out var window))
        {
            return Error(400, request, "win must look like 15m, 1h or 7d");
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            var counts = await overviewReader.ReadAsync(
                new OverviewReadQuery(domain, clubId, environment, now - window, now - AvailabilityWindow, LikeContains(request.SearchId)),
                cancellationToken);
            var tiles = OverviewComposer.Tiles(counts);
            return new OverviewOutcome(
                new OverviewResult
                {
                    Source = "sql",
                    Win = win,
                    Domain = domain,
                    ClubId = clubId,
                    Environment = environment,
                    PlatformStatus = OverviewComposer.WorstStatus(tiles),
                    Tiles = tiles,
                },
                200);
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable(win, domain, clubId, environment, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(win, domain, clubId, environment, $"Overview read failed: {ex.Message}");
        }
    }

    private async Task<OverviewOutcome> GetForProfileAsync(
        OverviewRequest request,
        IProfileOverviewReader reader,
        ICompanyProfileStore profileStore,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.ProfileKey?.Trim(), KpiProfiles.All, StringComparison.Ordinal))
        {
            return Error(400, request, "profileKey must name one company");
        }

        var scope = await _resolver.ResolveReadAsync(request.ProfileKey, cancellationToken);
        if (!scope.IsValid || scope.ProfileKey is null)
        {
            return Error(400, request, scope.Error ?? "profileKey must name one company");
        }

        var profile = await profileStore.GetAsync(scope.ProfileKey, cancellationToken);
        if (profile is null)
        {
            return Error(400, request, KpiProfileResolver.UnknownProfileMessage(scope.ProfileKey));
        }

        var profileKey = profile.ProfileKey;
        var incumbent = string.Equals(profileKey, KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

        string? domain;
        if (incumbent)
        {
            domain = NormalizeDomain(request.Domain);
            if (domain is null && !IsAllDomains(request.Domain))
            {
                return Error(400, request, "domain must be hotels, cars, cruises, air or all");
            }
        }
        else if (IsAllDomains(request.Domain))
        {
            domain = null;
        }
        else
        {
            domain = profile.FindService(request.Domain)?.Key.Trim();
            if (domain is null)
            {
                return Error(400, request, $"domain must be a service of profile '{profileKey}' or all");
            }
        }

        var clubId = string.IsNullOrWhiteSpace(request.ClubId) ? null : request.ClubId.Trim();
        if (clubId is not null)
        {
            if (incumbent && !ClubFilter.IsValidClubId(clubId))
            {
                return Error(400, request, "club must be a numeric id");
            }
            if (!incumbent && (clubId.Length > MaxEntityKeyLength || clubId.Any(char.IsControl)))
            {
                return Error(400, request, $"entity must be at most {MaxEntityKeyLength} printable characters");
            }
        }

        string environment;
        try
        {
            environment = InsightsEnvironment.Normalize(request.Environment);
        }
        catch (InsightsConfigurationException ex)
        {
            return Error(400, request, ex.Message);
        }

        var win = string.IsNullOrWhiteSpace(request.Win) ? "15m" : request.Win.Trim();
        if (!ClubFilter.TryParseWindow(win, out var window))
        {
            return Error(400, request, "win must look like 15m, 1h or 7d");
        }

        var now = _time.GetUtcNow();
        var labels = ProfileOverviewComposer.Labels(profile);
        var warnings = new List<string>();

        ProfileOverviewCounts counts;
        var source = "sql";
        try
        {
            counts = await reader.ReadAsync(
                new ProfileOverviewQuery(
                    profileKey, incumbent, domain, clubId, environment,
                    now - window, now - AvailabilityWindow, LikeContains(request.SearchId), TopEntityCount),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            source = "unavailable";
            warnings.Add(ex is InvalidOperationException ? ex.Message : $"Overview read failed: {ex.Message}");
            counts = new ProfileOverviewCounts(new OverviewCounts(null, null, null, null, null, null, null, null, null, null, null), [], []);
        }

        IReadOnlyList<PillarHealth> pillars = [];
        if (pillarReader is not null)
        {
            try
            {
                pillars = await pillarReader.GetAsync(domain, profileKey, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Pillar health is unavailable: {ex.Message}");
            }
        }

        IReadOnlyList<TriageInvestigation> investigations = [];
        if (triageStore is not null)
        {
            try
            {
                investigations = await triageStore.GetRecentAsync(InvestigationCount, profileKey, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Triage investigations are unavailable: {ex.Message}");
            }
        }

        var tiles = ProfileOverviewComposer.Tiles(counts.Counts, incumbent, labels);
        return new OverviewOutcome(
            new OverviewResult
            {
                Source = source,
                Win = win,
                Domain = domain,
                ClubId = clubId,
                Environment = environment,
                Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
                PlatformStatus = source == "sql" ? OverviewComposer.WorstStatus(tiles) : "unknown",
                Tiles = tiles,
                ProfileKey = profileKey,
                Labels = labels,
                Widgets = ProfileOverviewComposer.Widgets(incumbent),
                Pillars = ProfileOverviewComposer.Pillars(pillars, now),
                Kpis = ProfileOverviewComposer.Kpis(pillars, profileKey, now),
                Exceptions = new OverviewExceptionSummary(counts.Counts.OpenExceptions, counts.Counts.TotalExceptions, counts.Counts.CriticalIncidents),
                Investigations = ProfileOverviewComposer.Investigations(investigations, profileKey),
                TopEntities = ProfileOverviewComposer.TopEntities(counts.IncidentEntities, counts.JourneyEntities, TopEntityCount),
            },
            200);
    }

    private static OverviewOutcome Unavailable(
        string win,
        string? domain,
        string? clubId,
        string environment,
        string warning)
    {
        var tiles = OverviewComposer.Tiles(new OverviewCounts(null, null, null, null, null, null, null, null, null, null, null));
        return new OverviewOutcome(
            new OverviewResult
            {
                Source = "unavailable",
                Win = win,
                Domain = domain,
                ClubId = clubId,
                Environment = environment,
                Warning = warning,
                PlatformStatus = "unknown",
                Tiles = tiles,
            },
            200);
    }

    private static bool IsAllDomains(string? domain) =>
        string.IsNullOrWhiteSpace(domain) || domain.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeDomain(string? domain)
    {
        if (IsAllDomains(domain)) return null;
        var normalized = domain!.Trim().ToLowerInvariant();
        return ValidDomains.Contains(normalized) ? normalized : null;
    }

    private static string? LikeContains(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 128) trimmed = trimmed[..128];
        var escaped = trimmed.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }

    private static OverviewOutcome Error(int statusCode, OverviewRequest request, string message) =>
        new(
            new OverviewResult
            {
                Source = "error",
                Win = request.Win ?? "",
                Domain = request.Domain,
                ClubId = request.ClubId,
                Environment = request.Environment,
                Error = message,
            },
            statusCode);
}
