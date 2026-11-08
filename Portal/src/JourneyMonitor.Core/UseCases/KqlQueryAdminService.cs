using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.UseCases;

public sealed record KqlQueryAdminView(
    string QueryKey,
    int Version,
    string? Description,
    string? SourceRef,
    bool HasOverride,
    bool Enabled,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    int TextLength,
    /// <summary>Which agent or screen runs this query. A key like "scout:latency" does not say that on its own.</summary>
    string UsedBy,
    IReadOnlyList<string> SourceAliases);

public sealed record KqlQueryDetailView(
    string QueryKey,
    string KqlText,
    string BuiltInText,
    int Version,
    string? Description,
    string? SourceRef,
    bool HasOverride,
    bool Enabled,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    IReadOnlyList<string> RequiredPlaceholders,
    IReadOnlyList<string> SourceAliases,
    string UsedBy);

/// <summary>
/// The admin surface over editable KQL: list, read, save, revert, history and restore.
///
/// Validation happens on save, not on run. A query that loses a placeholder or points at an
/// unregistered source does not fail loudly at runtime - Application Insights answers with a
/// generic syntax error inside a timer, or worse, returns zero rows and the board looks healthy.
/// Catching it while someone is still looking at the editor is the only cheap moment.
/// </summary>
public sealed class KqlQueryAdminService(
    IKqlQueryStore store,
    /// <summary>Must be the EMBEDDED provider, not the decorated one: "revert" and the diff need
    /// the text that shipped, and the decorator would hand back the override instead.</summary>
    IKqlQueryTextProvider builtInTextProvider,
    ITelemetrySourceStore sourceStore,
    /// <summary>KPIs that use a custom kpi: query: an edit is dry-run for one of them, a delete is refused while one is enabled. Null = no check.</summary>
    KqlQueryUsage? usage = null)
{
    /// <summary>A stored query longer than this is almost certainly a paste accident, and it would run on a timer.</summary>
    public const int MaxTextLength = 200_000;

    /// <summary>Matches the quoted operand of a Kusto cross-resource call - the one place a query can name a resource directly instead of through the catalog.</summary>
    private static readonly Regex CrossResourceOperand = new(
        @"(?:app|cluster)\(\s*(?<q>['""])(?<operand>(?:(?!\k<q>).)*)\k<q>\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What each key's caller substitutes before the text is sent. Losing one of these is the
    /// failure this validation exists to prevent: the query still parses, it just covers the
    /// wrong window or the wrong club.
    /// </summary>
    private static readonly Dictionary<string, string[]> RequiredByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        [KqlQueryKeys.ScoutBoard("latency")] = ["{{TIME_START}}", "{{TIME_END}}", "{{BUCKET_MINUTES}}", "{{TAKE_PER_CLUB}}", "{{VERTICAL_B64}}", "{{CLUB_FILTER_B64}}"],
        [KqlQueryKeys.ScoutBoard("exception")] = ["{{TIME_START}}", "{{TIME_END}}", "{{BUCKET_MINUTES}}", "{{TAKE_PER_CLUB}}", "{{VERTICAL_B64}}", "{{CLUB_FILTER_B64}}"],
        [KqlQueryKeys.ScoutDrillDown] = ["{{OPERATION_IDS}}", "{{TIME_START}}", "{{TIME_END}}", "{{MAX_ROWS}}"],
        // club-baseline walks back from tEnd over a lookback instead of taking a start/end pair,
        // so it is the one Inspector query without {{TIME_START}}/{{MAX_ROWS}}.
        [KqlQueryKeys.InspectorLogs("club-baseline")] = ["{{ENTITY_KEY_B64}}", "{{TIME_END}}", "{{LOOKBACK_DAYS}}", "{{BUCKET_MINUTES}}", "{{CURRENT_EXCEPTIONS}}"],
        [KqlQueryKeys.InspectorLogs("signature-breakdown")] = ["{{ENTITY_KEY_B64}}", "{{TIME_START}}", "{{TIME_END}}", "{{MAX_ROWS}}"],
        [KqlQueryKeys.InspectorLogs("client-breakdown")] = ["{{ENTITY_KEY_B64}}", "{{TIME_START}}", "{{TIME_END}}", "{{MAX_ROWS}}"],
        [KqlQueryKeys.LatencySpikes("hotels")] = ["{{WINDOW_MINUTES}}", "{{FLOOR_MS}}"],
        [KqlQueryKeys.LatencySpikes("cars")] = ["{{WINDOW_MINUTES}}", "{{FLOOR_MS}}"],
        [KqlQueryKeys.LatencySpikes("cruises")] = ["{{WINDOW_MINUTES}}", "{{FLOOR_MS}}"],
    };

    public static IReadOnlyList<string> RequiredPlaceholdersFor(string queryKey) =>
        RequiredByKey.TryGetValue(queryKey, out var required) ? required : [];

    /// <summary>
    /// Who actually runs each key. Without this the admin screen lists eighteen strings like
    /// "scout:latency" and the only way to find the one an agent uses is to read the source.
    /// </summary>
    private static readonly Dictionary<string, string> UsedByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        [KqlQueryKeys.ScoutBoard("latency")] = "Latency Scout Monkey — priority board",
        [KqlQueryKeys.ScoutBoard("exception")] = "Exception Scout Monkey — priority board",
        [KqlQueryKeys.ScoutDrillDown] = "Both Scouts — raw evidence behind each case",
        [KqlQueryKeys.InspectorLogs("club-baseline")] = "Inspector Monkey — LogsAgent tool",
        [KqlQueryKeys.InspectorLogs("signature-breakdown")] = "Inspector Monkey — LogsAgent tool",
        [KqlQueryKeys.InspectorLogs("client-breakdown")] = "Inspector Monkey — LogsAgent tool",
        [KqlQueryKeys.LatencySpikes("hotels")] = "Latency Spike Monkey",
        [KqlQueryKeys.LatencySpikes("cars")] = "Latency Spike Monkey",
        [KqlQueryKeys.LatencySpikes("cruises")] = "Latency Spike Monkey",
        [KqlQueryKeys.IncidentExceptions] = "Exception ingestion — Exceptions tab",
        [KqlQueryKeys.Journey("hotels")] = "Customer Journeys tab + journey sync timer",
        [KqlQueryKeys.Journey("cars")] = "Customer Journeys tab + journey sync timer",
        [KqlQueryKeys.Journey("cruises")] = "Customer Journeys tab + journey sync timer",
        [KqlQueryKeys.Journey("air")] = "Customer Journeys tab + journey sync timer",
        [KqlQueryKeys.Dashboard("hotels", "step_latency_p95")] = "Analytics tab — dashboard sync",
        [KqlQueryKeys.Dashboard("hotels", "exceptions_by_step")] = "Analytics tab — dashboard sync",
        [KqlQueryKeys.Dashboard("cruises", "step_latency_p95")] = "Analytics tab — dashboard sync",
        [KqlQueryKeys.Dashboard("cruises", "exceptions_by_step")] = "Analytics tab — dashboard sync",
    };

    public static string UsedByFor(string queryKey) =>
        UsedByKey.TryGetValue(queryKey, out var usedBy) ? usedBy
        : KqlQueryKeys.IsCustomKpi(queryKey) ? $"KPI ingestion — SourceKey {KqlKpiContract.SourceKeyPrefix}{queryKey}"
        : "—";

    private bool IsBuiltInKey(string queryKey) =>
        builtInTextProvider.KnownKeys.Contains(queryKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>A built-in key, or a custom <c>kpi:</c> key (contract v4 §5) whose only text is its catalog row.</summary>
    private bool IsEditableKey(string queryKey) => IsBuiltInKey(queryKey) || KqlQueryKeys.IsCustomKpi(queryKey);

    private static string UnknownKeyMessage(string queryKey) =>
        $"Unknown query key \"{queryKey}\". Custom queries must use a lowercase key starting with " +
        $"\"{KqlQueryKeys.CustomKpiPrefix}\" ([a-z0-9:._-], at most {KqlQueryKeys.MaxCustomKeyLength} characters).";

    private static IReadOnlyList<string> AliasesIn(string kqlText) =>
        KqlSourceResolver.PlaceholderPattern.Matches(kqlText)
            .Select(m => m.Groups["alias"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<KqlQueryAdminView>> ListAsync(CancellationToken ct = default)
    {
        var overrides = (await store.GetAllAsync(ct)).ToDictionary(q => q.QueryKey, StringComparer.OrdinalIgnoreCase);
        var storedTexts = await store.GetEnabledTextsAsync(ct);

        // Driven by the built-in key list, not by the table: a key with no row still has to appear,
        // otherwise the admin screen would only show what somebody already edited. Custom kpi: keys
        // exist only as rows, so they are appended from the table.
        var customKeys = overrides.Keys.Where(k => KqlQueryKeys.IsCustomKpi(k) && !IsBuiltInKey(k));
        return builtInTextProvider.KnownKeys
            .Concat(customKeys)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(key =>
            {
                var hasOverride = overrides.TryGetValue(key, out var row);
                // Aliases are read from the text that actually runs, so the column answers
                // "what does this reach today" rather than "what did it ship with".
                var effectiveText = hasOverride && row!.Enabled
                    ? (storedTexts.TryGetValue(key, out var t) ? t : BuiltInTextOrEmpty(key))
                    : BuiltInTextOrEmpty(key);

                return hasOverride
                    ? new KqlQueryAdminView(key, row!.Version, row.Description, row.SourceRef,
                        true, row.Enabled, row.UpdatedAt, row.UpdatedBy, row.TextLength,
                        UsedByFor(key), AliasesIn(effectiveText))
                    : new KqlQueryAdminView(key, 0, null, null, false, true, null, null,
                        effectiveText.Length, UsedByFor(key), AliasesIn(effectiveText));
            })
            .ToList();
    }

    public async Task<KqlQueryDetailView?> GetAsync(string queryKey, CancellationToken ct = default)
    {
        if (!IsEditableKey(queryKey)) return null;

        var builtIn = IsBuiltInKey(queryKey) ? BuiltInTextOrEmpty(queryKey) : "";
        var row = await store.GetAsync(queryKey, ct);
        if (row is null && !IsBuiltInKey(queryKey)) return null;
        var text = row?.KqlText ?? builtIn;

        return new KqlQueryDetailView(
            queryKey, text, builtIn, row?.Version ?? 0, row?.Description, row?.SourceRef,
            row is not null, row?.Enabled ?? true, row?.UpdatedAt, row?.UpdatedBy,
            RequiredPlaceholdersFor(queryKey),
            AliasesIn(text),
            UsedByFor(queryKey));
    }

    /// <summary>Null error means the text is safe to store.</summary>
    public async Task<string?> ValidateAsync(string queryKey, string kqlText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kqlText)) return "The query text cannot be empty.";
        if (kqlText.Length > MaxTextLength) return $"The query text is {kqlText.Length} characters; the limit is {MaxTextLength}.";
        if (!IsEditableKey(queryKey))
        {
            return UnknownKeyMessage(queryKey);
        }
        if (!IsBuiltInKey(queryKey))
        {
            // A kpi: query is only ever run by the kql: KPI source, which fills nothing else.
            var disallowed = KqlKpiContract.FindDisallowedTokens(kqlText);
            if (disallowed.Count > 0)
            {
                return $"A KPI query may only use {KqlKpiContract.AllowedTokensText}; found {string.Join(", ", disallowed)}.";
            }

            var calls = KqlKpiContract.FindCrossResourceCalls(kqlText);
            if (calls.Count > 0) return KqlKpiContract.CrossResourceMessage(calls);
        }

        // The telemetry source catalog is the allow-list; app()/cluster() accepts an ARM path, a
        // bare application id (GUID) or an application name as its literal operand, and all three
        // sidestep the catalog just as effectively - only {{SOURCE:alias}} is allowed through.
        foreach (Match match in CrossResourceOperand.Matches(kqlText))
        {
            var operand = match.Groups["operand"].Value.Trim();
            var placeholder = KqlSourceResolver.PlaceholderPattern.Match(operand);
            if (!placeholder.Success || placeholder.Value != operand)
            {
                return $"The query calls app()/cluster() with a literal resource (\"{operand}\"). " +
                    "Reference a registered source as {{SOURCE:alias}} instead - an ARM path, an " +
                    "application id or an application name all bypass the telemetry source catalog.";
            }
        }

        var missing = RequiredPlaceholdersFor(queryKey)
            .Where(p => !kqlText.Contains(p, StringComparison.Ordinal))
            .ToList();
        if (missing.Count > 0)
        {
            return $"The query is missing {string.Join(", ", missing)}. Without it the caller cannot scope the query, and the result would be a valid query over the wrong data.";
        }

        var known = (await sourceStore.GetAllAsync(ct))
            .Select(s => s.Alias)
            .Concat(TelemetrySourceDefaults.All.Select(s => s.Alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = KqlSourceResolver.PlaceholderPattern.Matches(kqlText)
            .Select(m => m.Groups["alias"].Value)
            .Where(a => !known.Contains(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
        {
            return $"Unknown telemetry source alias: {string.Join(", ", unknown)}. Register it first, or the query will fail when it runs.";
        }

        return null;
    }

    /// <summary>Returns (statusCode, payload) so the Function layer stays a thin HTTP shell.</summary>
    public async Task<(int StatusCode, object Payload)> SaveAsync(
        string queryKey, string kqlText, string? description, string? sourceRef, bool enabled,
        string changedBy, CancellationToken ct = default)
    {
        var error = await ValidateAsync(queryKey, kqlText, ct);
        if (error is not null) return (400, new { error });

        if (usage is not null && !IsBuiltInKey(queryKey))
        {
            // An edit must not silently break the KPIs that already run this query.
            var users = await usage.GetUsersAsync(queryKey, ct);
            var probe = users.FirstOrDefault(d => d.Enabled) ?? users.FirstOrDefault();
            if (probe is not null)
            {
                var dryRunError = await usage.DryRunAsync(probe, kqlText, ct);
                if (dryRunError is not null)
                {
                    return (400, new { error = $"Query is used by KPI {probe.KpiDefinitionId}; the dry run of the new text failed: {dryRunError}" });
                }
            }
        }

        var saved = await store.UpsertAsync(
            new KqlQueryEntry(queryKey, kqlText, Description: description, SourceRef: sourceRef,
                IsBuiltIn: IsBuiltInKey(queryKey),
                Enabled: enabled),
            string.IsNullOrWhiteSpace(changedBy) ? "portal" : changedBy,
            ct);

        return (200, new { saved.QueryKey, saved.Version, saved.UpdatedAt, saved.UpdatedBy });
    }

    /// <summary>
    /// Drops the override so the key goes back to the text embedded in the build. A custom kpi: key
    /// has no built-in text, so reverting it deletes the query (audited as a removal).
    /// </summary>
    public async Task<(int StatusCode, object Payload)> RevertAsync(string queryKey, string changedBy, CancellationToken ct = default)
    {
        if (!IsEditableKey(queryKey))
        {
            return (404, new { error = $"Unknown query key \"{queryKey}\"." });
        }

        var custom = !IsBuiltInKey(queryKey);
        if (custom && await store.GetAsync(queryKey, ct) is null)
        {
            return (404, new { error = $"No custom query \"{queryKey}\"." });
        }
        if (custom && usage is not null)
        {
            var enabled = (await usage.GetUsersAsync(queryKey, ct)).Where(d => d.Enabled).Select(d => d.KpiDefinitionId).ToList();
            if (enabled.Count > 0)
            {
                return (409, new { error = $"Query is used by KPI(s) {string.Join(", ", enabled)}; disable them first." });
            }
        }

        await store.DeleteAsync(queryKey, string.IsNullOrWhiteSpace(changedBy) ? "portal" : changedBy, ct);
        return custom
            ? (200, (object)new { queryKey, reverted = true, deleted = true })
            : (200, new { queryKey, reverted = true });
    }

    /// <summary>Restores a previous version by copying its text forward as a new version.</summary>
    public async Task<(int StatusCode, object Payload)> RestoreAsync(long auditId, string changedBy, CancellationToken ct = default)
    {
        var entry = await store.GetAuditEntryAsync(auditId, ct);
        if (entry is null) return (404, new { error = $"No history entry {auditId}." });
        if (entry.Removed || string.IsNullOrWhiteSpace(entry.KqlText))
        {
            return (400, new { error = "That history entry records a removal, so it has no text to restore." });
        }

        // Forward, never by rewinding the version counter: the history has to stay append-only or
        // "what was running at 03:00" stops being answerable.
        return await SaveAsync(entry.QueryKey, entry.KqlText!, entry.Description,
            entry.SourceRef ?? $"restored from v{entry.Version}", entry.Enabled ?? true, changedBy, ct);
    }

    /// <summary>Seeds the table from the embedded files so the editor starts from the real text.</summary>
    public async Task<(int StatusCode, object Payload)> ImportBuiltInsAsync(bool overwrite, string changedBy, CancellationToken ct = default)
    {
        var existing = (await store.GetAllAsync(ct)).Select(q => q.QueryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<string>();
        var skipped = new List<string>();

        foreach (var key in builtInTextProvider.KnownKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!overwrite && existing.Contains(key)) { skipped.Add(key); continue; }

            var text = BuiltInTextOrEmpty(key);
            if (text.Length == 0) { skipped.Add(key); continue; }

            await store.UpsertAsync(
                new KqlQueryEntry(key, text, Description: "Imported from the built-in query text.",
                    SourceRef: "built-in", IsBuiltIn: true, Enabled: true),
                string.IsNullOrWhiteSpace(changedBy) ? "portal" : changedBy, ct);
            imported.Add(key);
        }

        return (200, new { imported = imported.Count, skipped = skipped.Count, keys = imported });
    }

    public Task<IReadOnlyList<KqlQueryAuditSummary>> GetAuditAsync(string? queryKey, int top, CancellationToken ct = default) =>
        store.GetAuditAsync(queryKey, top <= 0 ? 200 : Math.Min(top, 1000), ct);

    public Task<KqlQueryAuditEntry?> GetAuditEntryAsync(long auditId, CancellationToken ct = default) =>
        store.GetAuditEntryAsync(auditId, ct);

    /// <summary>
    /// Reads the built-in text through the embedded provider. Returns empty rather than throwing:
    /// a key present in KnownKeys always has a file, but the admin list must not die on one bad key.
    /// </summary>
    private string BuiltInTextOrEmpty(string key)
    {
        try
        {
            return builtInTextProvider.GetByKey(key);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
