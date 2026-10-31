using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Incidents;

namespace JourneyMonitor.Core.UseCases;

public sealed record IncidentListRequest(
    string? Domain,
    string? ClubId,
    string? Environment,
    string? Win,
    string? SearchId);

public sealed record IncidentListOutcome(IncidentListResult Result, int StatusCode);

/// <summary>
/// Exceptions tab payload. Property names match the existing app.js exception shape,
/// plus club, member, and URL from the incident store.
/// </summary>
public sealed class IncidentListResult
{
    public required string Source { get; init; }
    public string Win { get; init; } = "";
    public string? Domain { get; init; }
    public string? ClubId { get; init; }
    public string? Environment { get; init; }
    public int RowCount { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StoredException> Exceptions { get; init; } = [];
}

public sealed class StoredException
{
    public required string Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Severity { get; init; }
    public required string Domain { get; init; }
    public required string ClubId { get; init; }
    /// <summary>Brand name for <see cref="ClubId"/> when brand.Information knows it; otherwise the raw token.</summary>
    public string ClubName { get; init; } = "";
    public string? MemberId { get; init; }
    public string? Url { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? StackTrace { get; init; }
    public int OccurrenceCount { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
    public int UsersAffected { get; init; }
    public IReadOnlyList<string> JourneysAffected { get; init; } = [];
    public bool DeploymentCorrelation { get; init; }
    public string Repository { get; init; } = "";
    public required string Status { get; init; }
    public string AssignedAgent { get; init; } = "";
    public IReadOnlyList<StoredExceptionOccurrence> Occurrences { get; init; } = [];
}

public sealed class StoredExceptionOccurrence
{
    public required string Timestamp { get; init; }
    public string? MemberId { get; init; }
    public string? Url { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Lists incidents for the Exceptions tab. Same filter vocabulary as
/// <see cref="GetDbJourneysService"/>: domain, club, environment, and window.
/// One row is one incident. Member and URL are the latest occurrence.
/// </summary>
public sealed class GetIncidentsService(IIncidentReader incidentReader, IBrandCatalog brandCatalog)
{
    public const int ListCap = 200;

    private static readonly string[] ValidDomains = ["hotels", "cars", "cruises", "air"];

    public async Task<IncidentListOutcome> GetAsync(IncidentListRequest request, CancellationToken cancellationToken)
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

        DateTimeOffset? since = null;
        var win = "";
        if (!string.IsNullOrWhiteSpace(request.Win))
        {
            if (!ClubFilter.TryParseWindow(request.Win, out var window))
            {
                return Error(400, request, "win must look like 15m, 1h or 7d");
            }

            since = DateTimeOffset.UtcNow - window;
            win = request.Win.Trim();
        }

        try
        {
            var rows = await incidentReader.ListAsync(
                new IncidentReadQuery(domain, clubId, environment, since, LikeContains(request.SearchId), ListCap),
                cancellationToken);
            var brands = await LoadBrandsAsync(cancellationToken);
            var exceptions = rows.Select(row => ToException(row, brands)).ToList();
            var capped = exceptions.Count >= ListCap;

            return new IncidentListOutcome(
                new IncidentListResult
                {
                    Source = "sql",
                    Win = win,
                    Domain = domain,
                    ClubId = clubId,
                    Environment = environment,
                    RowCount = exceptions.Count,
                    Warning = capped ? $"Showing the {ListCap} most recently seen incidents in this window." : null,
                    Exceptions = exceptions,
                },
                200);
        }
        catch (IncidentStoreUnavailableException ex)
        {
            return new IncidentListOutcome(
                new IncidentListResult
                {
                    Source = "unavailable",
                    Win = win,
                    Domain = domain,
                    ClubId = clubId,
                    Environment = environment,
                    Warning = ex.Message,
                },
                200);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(502, request, $"Incident store read failed: {ex.Message}");
        }
    }

    private static StoredException ToException(IncidentRow row, IReadOnlyList<BrandClub> brands) => new()
    {
        Id = row.ExternalId,
        Timestamp = row.LastSeen.ToString("O"),
        Severity = row.Severity,
        Domain = row.Domain,
        ClubId = row.ClubId,
        ClubName = BrandClubOptions.DisplayName(brands, row.ClubId) ?? row.ClubId,
        MemberId = row.LatestMemberId,
        Url = row.LatestUrl,
        Component = JourneyStageResolver.ForException(row.Domain, row.LatestUrl, row.Operation),
        Operation = row.Operation,
        Type = row.ExceptionType,
        Message = row.Message,
        StackTrace = string.IsNullOrWhiteSpace(row.StackTrace) ? null : row.StackTrace,
        OccurrenceCount = row.OccurrenceCount,
        FirstSeen = row.FirstSeen.ToString("O"),
        LastSeen = row.LastSeen.ToString("O"),
        UsersAffected = row.UsersAffected,
        JourneysAffected = row.JourneyKeys,
        Status = row.Status,
        AssignedAgent = row.AssignedTo ?? "",
        Occurrences = row.RecentOccurrences
            .Select(occurrence => new StoredExceptionOccurrence
            {
                Timestamp = occurrence.OccurredAt.ToString("O"),
                MemberId = occurrence.MemberId,
                Url = occurrence.Url,
                Message = occurrence.Message,
            })
            .ToList(),
    };

    private async Task<IReadOnlyList<BrandClub>> LoadBrandsAsync(CancellationToken cancellationToken)
    {
        if (!brandCatalog.IsConfigured) return [];
        try
        {
            return await brandCatalog.GetAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static bool IsAllDomains(string? domain) =>
        string.IsNullOrWhiteSpace(domain) || domain.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeDomain(string? domain)
    {
        if (IsAllDomains(domain)) return null;
        var normalized = domain!.Trim().ToLowerInvariant();
        return ValidDomains.Contains(normalized) ? normalized : null;
    }

    /// <summary>Contains-match that treats % _ and [ as literals.</summary>
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

    private static IncidentListOutcome Error(int statusCode, IncidentListRequest request, string message) =>
        new(
            new IncidentListResult
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
