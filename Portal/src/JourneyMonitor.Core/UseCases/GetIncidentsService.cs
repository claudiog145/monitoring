using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.UseCases;

/// <param name="ProfileKey">Contract v6 §4.3: the company to read; blank means the active company.</param>
public sealed record IncidentListRequest(
    string? Domain,
    string? ClubId,
    string? Environment,
    string? Win,
    string? SearchId,
    string? ProfileKey = null);

public sealed record IncidentListOutcome(IncidentListResult Result, int StatusCode);

/// <summary>
/// Exceptions tab payload. Property names match the existing app.js exception shape,
/// plus club, member, and URL from the incident store.
/// </summary>
public sealed class IncidentListResult
{
    public required string Source { get; init; }
    /// <summary>The company every row belongs to. Null only when the request was rejected before it was resolved.</summary>
    public string? ProfileKey { get; init; }
    public string Win { get; init; } = "";
    public string? Domain { get; init; }
    public string? ClubId { get; init; }
    public string? Environment { get; init; }
    public int RowCount { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StoredException> Exceptions { get; init; } = [];

    /// <summary>
    /// Incident counts for the last 7 days, same domain, club, environment and search as this
    /// list. The Reliability Watch baseline. A domain with none is omitted; the caller reads that as zero.
    /// </summary>
    public IReadOnlyDictionary<string, int>? BaselineCounts { get; init; }

    /// <summary>
    /// Incident counts for the selected window, uncapped, same filters as this list.
    /// <see cref="Exceptions"/> is capped at <see cref="GetIncidentsService.ListCap"/>; this is not.
    /// </summary>
    public IReadOnlyDictionary<string, int>? WindowCounts { get; init; }
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
    /// <summary>Server or Browser. The latest occurrence when an incident has both.</summary>
    public string Source { get; init; } = ExceptionSource.Server;
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
    public string Source { get; init; } = ExceptionSource.Server;
}

/// <summary>
/// Lists incidents for the Exceptions tab. Same filter vocabulary as
/// <see cref="GetDbJourneysService"/>: domain, club, environment, and window.
/// One row is one incident. Member and URL are the latest occurrence.
/// Contract v6 §4.3: every answer is about ONE company - the requested <c>profileKey</c>, else the active
/// one. The incumbent keeps its own domains, club ids and brand names; another company's domains are its
/// service keys, its club is the entity, and a domain it does not have answers empty with a warning.
/// </summary>
public sealed class GetIncidentsService(
    IIncidentReader incidentReader,
    IBrandCatalog brandCatalog,
    KpiProfileResolver? profileResolver = null,
    IProfileIncidentReader? profileIncidentReader = null,
    ICompanyProfileStore? profileStore = null)
{
    public const int ListCap = 200;

    private static readonly string[] ValidDomains = ["hotels", "cars", "cruises", "air"];

    private static readonly Regex EntityPattern = new(@"^[A-Za-z0-9._:@ -]{1,32}$", RegexOptions.Compiled);

    public async Task<IncidentListOutcome> GetAsync(IncidentListRequest request, CancellationToken cancellationToken)
    {
        string profileKey;
        if (profileResolver is not null)
        {
            var scope = await profileResolver.ResolveWriteAsync(request.ProfileKey, cancellationToken);
            if (!scope.IsValid) return Error(400, request, scope.Error!);
            profileKey = scope.ProfileKey!;
        }
        else if (string.IsNullOrWhiteSpace(request.ProfileKey) || IsIncumbent(request.ProfileKey))
        {
            profileKey = KpiProfiles.Default;
        }
        else
        {
            return Error(400, request, KpiProfileResolver.UnknownProfileMessage(request.ProfileKey.Trim()));
        }

        if (!IsIncumbent(profileKey))
        {
            return await GetForProfileAsync(profileKey, request, cancellationToken);
        }

        var outcome = await GetIncumbentAsync(request, cancellationToken);
        return outcome with { Result = WithProfile(outcome.Result, profileKey) };
    }

    private static bool IsIncumbent(string? profileKey) =>
        string.Equals(profileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    private static IncidentListResult WithProfile(IncidentListResult result, string profileKey) => new()
    {
        Source = result.Source,
        ProfileKey = profileKey,
        Win = result.Win,
        Domain = result.Domain,
        ClubId = result.ClubId,
        Environment = result.Environment,
        RowCount = result.RowCount,
        Warning = result.Warning,
        Error = result.Error,
        Exceptions = result.Exceptions,
        BaselineCounts = result.BaselineCounts,
        WindowCounts = result.WindowCounts,
    };

    private async Task<IncidentListOutcome> GetForProfileAsync(
        string profileKey, IncidentListRequest request, CancellationToken cancellationToken)
    {
        var requestedDomain = IsAllDomains(request.Domain) ? null : request.Domain!.Trim().ToLowerInvariant();
        var clubId = string.IsNullOrWhiteSpace(request.ClubId) ? null : request.ClubId.Trim();
        if (clubId is not null && !EntityPattern.IsMatch(clubId))
        {
            return Error(400, request, "club must be an entity key of up to 32 letters, digits, spaces or . _ : @ -");
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

        IncidentListResult Empty(string source, string warning) => new()
        {
            Source = source,
            ProfileKey = profileKey,
            Win = win,
            Domain = requestedDomain,
            ClubId = clubId,
            Environment = environment,
            Warning = warning,
            BaselineCounts = new Dictionary<string, int>(),
            WindowCounts = new Dictionary<string, int>(),
        };

        if (profileIncidentReader is null)
        {
            return new IncidentListOutcome(
                Empty("unavailable", $"Exceptions of company '{profileKey}' cannot be read: no per-company incident reader is configured."),
                200);
        }

        if (requestedDomain is not null && profileStore is not null)
        {
            var profile = await profileStore.GetAsync(profileKey, cancellationToken);
            if (profile?.FindService(requestedDomain) is null)
            {
                return new IncidentListOutcome(
                    Empty("sql", $"'{requestedDomain}' is not a service of company '{profileKey}'."),
                    200);
            }
        }

        try
        {
            var search = LikeContains(request.SearchId);
            var rows = await profileIncidentReader.ListForProfileAsync(
                profileKey,
                new IncidentReadQuery(requestedDomain, clubId, environment, since, search, ListCap),
                cancellationToken);
            var exceptions = rows.Select(row => ToException(row, [], profileVocabulary: true)).ToList();
            var capped = exceptions.Count >= ListCap;
            var windowCounts = await profileIncidentReader.CountByDomainForProfileAsync(
                profileKey,
                new IncidentCountQuery(requestedDomain, clubId, environment, since, search),
                cancellationToken);
            var baselineCounts = await profileIncidentReader.CountByDomainForProfileAsync(
                profileKey,
                new IncidentCountQuery(requestedDomain, clubId, environment, DateTimeOffset.UtcNow.AddDays(-7), search),
                cancellationToken);

            return new IncidentListOutcome(
                new IncidentListResult
                {
                    Source = "sql",
                    ProfileKey = profileKey,
                    Win = win,
                    Domain = requestedDomain,
                    ClubId = clubId,
                    Environment = environment,
                    RowCount = exceptions.Count,
                    Warning = capped ? $"Showing the {ListCap} most recently seen incidents in this window." : null,
                    Exceptions = exceptions,
                    BaselineCounts = baselineCounts,
                    WindowCounts = windowCounts,
                },
                200);
        }
        catch (IncidentStoreUnavailableException ex)
        {
            return new IncidentListOutcome(Empty("unavailable", ex.Message), 200);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(502, request, $"Incident store read failed: {ex.Message}");
        }
    }

    private async Task<IncidentListOutcome> GetIncumbentAsync(IncidentListRequest request, CancellationToken cancellationToken)
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
            var windowCounts = await incidentReader.CountByDomainAsync(
                new IncidentCountQuery(domain, clubId, environment, since, LikeContains(request.SearchId)),
                cancellationToken);
            var baselineCounts = await incidentReader.CountByDomainAsync(
                new IncidentCountQuery(domain, clubId, environment, DateTimeOffset.UtcNow.AddDays(-7), LikeContains(request.SearchId)),
                cancellationToken);

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
                    BaselineCounts = baselineCounts,
                    WindowCounts = windowCounts,
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

    /// <param name="profileVocabulary">Another company's row: the component is the step its profile classified at ingestion.</param>
    private static StoredException ToException(IncidentRow row, IReadOnlyList<BrandClub> brands, bool profileVocabulary = false) => new()
    {
        Id = row.ExternalId,
        Timestamp = row.LastSeen.ToString("O"),
        Severity = row.Severity,
        Domain = row.Domain,
        ClubId = row.ClubId,
        ClubName = BrandClubOptions.DisplayName(brands, row.ClubId) ?? row.ClubId,
        MemberId = row.LatestMemberId,
        Url = row.LatestUrl,
        Component = !profileVocabulary
            ? JourneyStageResolver.ForException(row.Domain, row.LatestUrl, row.Operation)
            : string.IsNullOrWhiteSpace(row.Component) ? JourneyStageResolver.Unclassified : row.Component,
        Operation = row.Operation,
        Type = row.ExceptionType,
        Source = ExceptionSource.Normalize(row.Source),
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
                Source = ExceptionSource.Normalize(occurrence.Source),
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
