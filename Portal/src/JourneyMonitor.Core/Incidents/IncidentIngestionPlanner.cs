namespace JourneyMonitor.Core.Incidents;

/// <summary>One exception event from a KQL sync, before grouping.</summary>
public sealed record ExceptionSignal(
    string ClubId,
    string ExceptionType,
    string Component,
    string OperationName,
    string Message,
    string? StackTrace,
    string ItemId,
    string OperationId,
    DateTimeOffset OccurredAt,
    string? MemberId,
    string? SessionId,
    string? CorrelationId,
    string? Url,
    string? RequestUrl = null);

public sealed record PlannedOccurrence(
    DateTimeOffset OccurredAt,
    string? MemberId,
    string? SessionId,
    string? CorrelationId,
    string OperationId,
    string? Url,
    string Message,
    string? StackTrace,
    string ItemId);

/// <summary>
/// A grouped problem for one club. <see cref="InitialStatus"/> applies only when the fingerprint
/// is new. An existing incident keeps its status and assignee.
/// </summary>
public sealed record PlannedIncident(
    string Fingerprint,
    string ClubId,
    string ExceptionType,
    string Component,
    string Operation,
    string Message,
    string InitialStatus,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<PlannedOccurrence> Occurrences,
    IReadOnlyList<string> JourneyKeys);

public sealed record IncidentIngestPlan(
    IReadOnlyList<PlannedIncident> Incidents,
    int DroppedNoise,
    int DroppedWithoutClub,
    int DroppedWithoutItemId,
    int DroppedDuplicates);

/// <summary>
/// Groups exception events the way WI 238073 describes ingestion.
/// Browser <c>ErrorEvent</c> rows are noise. Fingerprint is
/// Domain + ClubId + Environment + ExceptionType + Operation, so the same error in two clubs
/// is two incidents. Occurrence <c>ItemId</c> is unique inside the batch.
/// </summary>
public static class IncidentIngestionPlanner
{
    public const string InitialStatus = "Open";

    public static IncidentIngestPlan Plan(string domain, string environment, IReadOnlyList<ExceptionSignal> signals)
    {
        var normalizedDomain = domain.Trim().ToLowerInvariant();
        var normalizedEnvironment = environment.Trim().ToLowerInvariant();
        var droppedNoise = 0;
        var droppedWithoutClub = 0;
        var droppedWithoutItemId = 0;
        var droppedDuplicates = 0;
        var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SQL Server's unique index is case-insensitive. Grouping that way here keeps one
        // batch from inserting two incidents that the constraint treats as the same key.
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var signal in signals)
        {
            if (IsBrowserNoise(signal.ExceptionType))
            {
                droppedNoise++;
                continue;
            }

            var clubId = TrimTo(signal.ClubId, 32);
            if (clubId.Length == 0)
            {
                droppedWithoutClub++;
                continue;
            }

            var itemId = TrimTo(signal.ItemId, 128);
            if (itemId.Length == 0)
            {
                droppedWithoutItemId++;
                continue;
            }

            if (!seenItemIds.Add(itemId))
            {
                droppedDuplicates++;
                continue;
            }

            var operation = ResolveOperation(normalizedDomain, signal.Url, signal.OperationName);
            var exceptionType = TrimTo(signal.ExceptionType, 256);
            if (exceptionType.Length == 0) exceptionType = "Exception";
            var fingerprint = BuildFingerprint(normalizedDomain, clubId, normalizedEnvironment, exceptionType, operation);
            if (!groups.TryGetValue(fingerprint, out var group))
            {
                group = new GroupBuilder(
                    fingerprint,
                    clubId,
                    exceptionType,
                    TrimTo(JourneyStageResolver.ForException(normalizedDomain, signal.Url, signal.OperationName), 128),
                    operation,
                    TrimTo(string.IsNullOrWhiteSpace(signal.Message) ? exceptionType : signal.Message, 1024));
                groups.Add(fingerprint, group);
            }

            group.Add(normalizedDomain, signal, itemId);
        }

        return new IncidentIngestPlan(
            groups.Values.Select(group => group.ToIncident()).ToList(),
            droppedNoise,
            droppedWithoutClub,
            droppedWithoutItemId,
            droppedDuplicates);
    }

    /// <summary>Same bands as the WI 238073 example: 224 Critical, 41 High, 12 Medium.</summary>
    public static string SeverityForOccurrenceCount(int occurrenceCount)
    {
        if (occurrenceCount >= 100) return "Critical";
        if (occurrenceCount >= 25) return "High";
        if (occurrenceCount >= 5) return "Medium";
        return "Low";
    }

    /// <summary>
    /// Full grouping key. Each part is capped at its column width (32, 32, 32, 256, 256), so the
    /// result is at most 612 characters and matches incident.Incident.Fingerprint nvarchar(612).
    /// The assembled key is not truncated: two exceptions that differ only near the end stay distinct.
    /// </summary>
    public static string BuildFingerprint(string domain, string clubId, string environment, string exceptionType, string operation) =>
        string.Join('|',
            TrimTo(domain, 32),
            TrimTo(clubId, 32),
            TrimTo(environment, 32),
            TrimTo(exceptionType, 256),
            TrimTo(operation, 256));

    public static bool IsBrowserNoise(string? exceptionType) =>
        exceptionType is not null && exceptionType.Trim().StartsWith("ErrorEvent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cars has no App Insights app. Exception rows are read from the hotel app, then split with the
    /// same URL markers the car journey query uses: CarSearch, CarBooking, CarLocationSearch,
    /// searchlocations, and /cars/. BackgroundCarSearch matches through CarSearch. Anything else
    /// from that app is a hotel exception. Air and cruises are separate apps and are not split here.
    /// </summary>
    public static bool IsCarException(string? url, string? operationName)
    {
        var haystack = $"{url} {operationName}";
        return haystack.Contains("CarSearch", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("CarBooking", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("CarLocationSearch", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("searchlocations", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("/cars/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stage names from the journey KQL when the URL is recognizable. Otherwise the telemetry operation name.
    /// </summary>
    public static string ResolveOperation(string domain, string? url, string? operationName)
    {
        var path = url ?? "";
        if (domain == "cars" && (path.Contains("CarSearch", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/cars/search", StringComparison.OrdinalIgnoreCase)))
        {
            return "02 Car Search";
        }

        if (domain == "hotels" && path.Contains("Purchase", StringComparison.OrdinalIgnoreCase))
        {
            return "05 Purchase";
        }

        var name = TrimTo(operationName, 256);
        return name.Length == 0 ? "unknown" : name;
    }

    private static string TrimTo(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static string? NullIfBlank(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Id shown in the Customer Journeys "Journey ID" column. For cruises that column is the
    /// frontend <c>session_id</c>, so the session wins over the backend correlation. For hotels
    /// and other domains the column is <c>CorrelationId</c>, not the member id.
    /// </summary>
    private static string? JourneyKey(string domain, ExceptionSignal signal)
    {
        if (domain is "cruises")
        {
            var session = UsableJourneyId(signal.SessionId);
            if (session is not null) return session;
        }

        var correlation = UsableJourneyId(signal.CorrelationId);
        if (correlation is not null) return correlation;

        return UsableMemberId(signal.MemberId);
    }

    private static string? UsableMemberId(string? value)
    {
        var text = NullIfBlank(value);
        if (text is null || text == "0") return null;
        return TrimTo(text, 128);
    }

    private static string? UsableJourneyId(string? value)
    {
        var text = NullIfBlank(value);
        if (text is null || text == "00000000-0000-0000-0000-000000000000") return null;
        return TrimTo(text, 128);
    }

    private sealed class GroupBuilder(
        string fingerprint,
        string clubId,
        string exceptionType,
        string component,
        string operation,
        string message)
    {
        private readonly List<PlannedOccurrence> _occurrences = new();
        private readonly List<string> _journeyKeys = new();
        private DateTimeOffset _first = DateTimeOffset.MaxValue;
        private DateTimeOffset _last = DateTimeOffset.MinValue;

        public void Add(string domain, ExceptionSignal signal, string itemId)
        {
            if (signal.OccurredAt < _first) _first = signal.OccurredAt;
            if (signal.OccurredAt > _last) _last = signal.OccurredAt;

            var journeyKey = JourneyKey(domain, signal);
            if (journeyKey is not null && !_journeyKeys.Contains(journeyKey, StringComparer.OrdinalIgnoreCase))
            {
                _journeyKeys.Add(journeyKey);
            }

            _occurrences.Add(new PlannedOccurrence(
                signal.OccurredAt,
                NullIfBlank(TrimTo(signal.MemberId, 128)),
                NullIfBlank(TrimTo(signal.SessionId, 128)),
                NullIfBlank(TrimTo(signal.CorrelationId, 128)),
                TrimTo(string.IsNullOrWhiteSpace(signal.OperationId) ? itemId : signal.OperationId, 128),
                NullIfBlank(TrimTo(signal.Url, 2048)),
                TrimTo(string.IsNullOrWhiteSpace(signal.Message) ? message : signal.Message, 1024),
                string.IsNullOrWhiteSpace(signal.StackTrace) ? null : signal.StackTrace,
                itemId));
        }

        public PlannedIncident ToIncident() => new(
            fingerprint,
            clubId,
            exceptionType,
            component,
            operation,
            message,
            IncidentIngestionPlanner.InitialStatus,
            _first,
            _last,
            _occurrences,
            _journeyKeys);
    }
}
