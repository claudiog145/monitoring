namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Editable, versioned KQL. A row here overrides the file embedded in the assembly for the same
/// key; no row means the built-in text is used, so an unseeded database behaves like the
/// pre-refactor build.
/// </summary>
public interface IKqlQueryStore
{
    /// <summary>Metadata only - the list view renders 18 rows and the bodies are hundreds of lines each.</summary>
    Task<IReadOnlyList<KqlQuerySummary>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Every enabled query with its text, in one round trip. Used to refresh the provider snapshot.</summary>
    Task<IReadOnlyDictionary<string, string>> GetEnabledTextsAsync(CancellationToken ct = default);

    Task<KqlQueryEntry?> GetAsync(string queryKey, CancellationToken ct = default);

    Task<KqlQueryEntry> UpsertAsync(KqlQueryEntry entry, string changedBy, CancellationToken ct = default);

    /// <summary>Removes the override, which reverts that key to its built-in text.</summary>
    Task DeleteAsync(string queryKey, string changedBy, CancellationToken ct = default);

    Task<IReadOnlyList<KqlQueryAuditSummary>> GetAuditAsync(string? queryKey, int top, CancellationToken ct = default);

    /// <summary>One historical version including its full text - what a diff or a restore reads.</summary>
    Task<KqlQueryAuditEntry?> GetAuditEntryAsync(long auditId, CancellationToken ct = default);
}

public sealed record KqlQueryEntry(
    string QueryKey,
    string KqlText,
    int Version = 0,
    string? Description = null,
    string? SourceRef = null,
    bool IsBuiltIn = false,
    bool Enabled = true,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null);

public sealed record KqlQuerySummary(
    string QueryKey,
    int Version,
    string? Description,
    string? SourceRef,
    bool IsBuiltIn,
    bool Enabled,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    int TextLength);

public sealed record KqlQueryAuditSummary(
    long AuditId,
    string QueryKey,
    int Version,
    string? Description,
    string? SourceRef,
    bool? Enabled,
    bool Removed,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    int TextLength);

public sealed record KqlQueryAuditEntry(
    long AuditId,
    string QueryKey,
    int Version,
    string? KqlText,
    string? Description,
    string? SourceRef,
    bool? Enabled,
    bool Removed,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>
/// The stable identity of every query the platform runs. These strings are the contract between
/// the code that asks for a query and the row that may override it, so they are built from the
/// same parts the code already uses (domain, board, chart) rather than from the file names - a
/// file can be renamed, a key cannot.
/// </summary>
public static class KqlQueryKeys
{
    public static string Journey(string domain) => $"journey:{domain.ToLowerInvariant()}";

    public static string LatencySpikes(string domain) => $"latency-spikes:{domain.ToLowerInvariant()}";

    public static string Dashboard(string domain, string chart) =>
        $"dashboard:{domain.ToLowerInvariant()}:{chart.ToLowerInvariant()}";

    public static string ScoutBoard(string board) => $"scout:{board.ToLowerInvariant()}";

    public static string InspectorLogs(string query) => $"inspector:{query.ToLowerInvariant()}";

    public const string ScoutDrillDown = "scout:drilldown";

    public const string IncidentExceptions = "incident:exceptions";
}
