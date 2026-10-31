using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Filtering;

namespace JourneyMonitor.Core.UseCases;

public sealed record OverviewRequest(
    string? Domain,
    string? ClubId,
    string? Environment,
    string? Win,
    string? SearchId);

public sealed record OverviewOutcome(OverviewResult Result, int StatusCode);

public sealed class OverviewResult
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
}

/// <summary>
/// Executive Overview. Availability is the last 24 hours of journey calls for Hotels and Cruises.
/// The other tiles follow domain, club, environment, time range, and id.
/// An omitted domain is All, which is Hotels and Cruises.
/// </summary>
public sealed class GetOverviewService(IOverviewReader overviewReader)
{
    private static readonly string[] ValidDomains = ["hotels", "cars", "cruises", "air"];
    private static readonly TimeSpan AvailabilityWindow = TimeSpan.FromHours(24);

    public async Task<OverviewOutcome> GetAsync(OverviewRequest request, CancellationToken cancellationToken)
    {
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
