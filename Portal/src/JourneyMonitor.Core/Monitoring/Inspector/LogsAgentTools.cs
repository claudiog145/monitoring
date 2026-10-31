using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Inspector;

/// <summary>Keys of the LogsAgent's embedded KQL, mirroring <c>KqlQueryTextProvider</c>'s map.</summary>
public static class InspectorLogsQuery
{
    public const string ClubBaseline = "club-baseline";
    public const string SignatureBreakdown = "signature-breakdown";
    public const string ClientBreakdown = "client-breakdown";
}

/// <summary>
/// The bounds every LogsAgent tool applies before any text reaches Application Insights.
///
/// The threat model is specific: a model decides WHICH tool runs and with WHAT number, never with
/// what query. No string produced by a model is ever interpolated into KQL - the only arguments
/// accepted are integers, and they are clamped here rather than validated in a prompt.
/// <see cref="SanitizeEntityKey"/> is defence in depth on top of that: the entity key comes from
/// the Scout package today, but it originates in a URL host a caller controls, so it is stripped
/// of anything that could close a literal AND then base64-encoded, which is what actually makes
/// injection impossible.
/// </summary>
public static class InspectorKqlGuards
{
    /// <summary>What the board emits when it could not attribute the traffic at all - not a scopeable entity.</summary>
    public const string UnresolvedEntity = "[host/club unavailable]";

    private const int MaxEntityKeyLength = 200;

    /// <summary>
    /// Removes every character that could terminate or extend a KQL literal (quotes, backslash,
    /// braces, semicolons, control characters). A legitimate club id or host never contains them,
    /// so the worst case is an entity that matches nothing - an honestly empty result, which is
    /// strictly better than a query that runs something it was not asked to run.
    /// </summary>
    public static string SanitizeEntityKey(string? entityKey)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return "";

        var sb = new StringBuilder(Math.Min(entityKey.Length, MaxEntityKeyLength));
        foreach (var c in entityKey)
        {
            if (sb.Length >= MaxEntityKeyLength) break;
            if (char.IsControl(c)) continue;
            if (c is '\'' or '"' or '\\' or '`' or '{' or '}' or ';') continue;
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Base64 is the real anti-injection boundary: its alphabet cannot close a KQL string literal.</summary>
    public static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public static string ToKqlDatetime(DateTimeOffset value) =>
        $"datetime({value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}Z)";

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>
    /// Fails loudly on a placeholder nobody substituted. Without this the query reaches Azure and
    /// comes back as a generic Kusto syntax error that says nothing about which value was missing.
    /// </summary>
    public static string RequireNoPlaceholders(string kql, string label)
    {
        KqlPlaceholders.AssertNoneLeft(kql, $"{label} KQL");
        return kql;
    }

    /// <summary>
    /// Reads one integer argument out of model-generated JSON. Anything unparseable, missing or of
    /// the wrong type falls back to <paramref name="fallback"/>: a malformed argument must cost a
    /// default, not an exception the agent has to recover from.
    /// </summary>
    public static int ReadIntArgument(string? argumentsJson, string name, int fallback)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return fallback;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fallback;
            if (!TryGetProperty(doc.RootElement, name, out var value)) return fallback;

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var i) => i,
                JsonValueKind.Number when value.TryGetDouble(out var d) => (int)Math.Round(d),
                JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
                _ => fallback,
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>JsonDocument lookups are case-sensitive; payloads here mix Kusto PascalCase with web camelCase.</summary>
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…[truncated]";
}

/// <summary>
/// What a tool hands back as <see cref="AgentToolResult.ResultJson"/>. The cap is carried INSIDE
/// the payload, not only on the result record, because the model reads the payload and not the
/// record: a list that was cut short must say so where the reader is looking.
///
/// <c>Truncated</c> and <c>FieldsShortened</c> are two different claims and must stay apart.
/// <c>Truncated</c> means ROWS ARE MISSING; <c>FieldsShortened</c> means every row is here and
/// only long text inside them was abbreviated. Collapsing the second into the first is what
/// produced the blockers this envelope now exists to avoid - "only 2 of 166 exception rows were
/// sampled" - and a model told its list is partial reports 0.10-0.25 confidence no matter how
/// much evidence it is actually holding.
/// </summary>
public sealed record InspectorToolPayload<T>(
    bool Truncated,
    bool FieldsShortened,
    int ReturnedRows,
    int TotalRows,
    string? Note,
    IReadOnlyList<T> Rows);

/// <summary>
/// Shared plumbing for the LogsAgent's tools: resolve the Application Insights entry point, run
/// the query, cap the result.
///
/// The entry point is the same one the Scout uses (<see cref="ScoutOptions.ResolvedEntryDomain"/>)
/// because these queries are cross-resource by construction - the ARM paths inside the KQL decide
/// what is read, the entry point only decides which workspace authenticates the call.
///
/// Never throws: every tool returns <see cref="AgentToolResult.Failed"/> instead. An agent that
/// cannot query must not be able to take the investigation down with it. Cancellation is the one
/// exception and propagates, matching <c>ScoutEvidenceCollector</c>.
/// </summary>
public abstract class LogsAgentTool(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<ScoutOptions> scoutOptions,
    IOptions<InspectorOptions> inspectorOptions,
    ILogger logger) : IAgentTool
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Fields that are long in production AND redundant across rows of the same signature - the
    /// stack traces of 166 rows of one exception type are 166 copies of the same frames.
    ///
    /// Identifiers are deliberately absent. OperationId, Path, Url, Name, ExceptionType and
    /// ProblemId are what <c>CaseArtifactIndex</c> verifies citations against, so a shortened one
    /// no longer matches what a model quotes and a real citation is reported as invented.
    /// </summary>
    private static readonly HashSet<string> VerboseRowFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "StackTrace", "Details", "Message", "OuterMessage", "InnermostMessage",
    };

    /// <summary>
    /// The shortening ladder, loosest first: a package that fits with 1200 characters of stack per
    /// row must not be squeezed to 240 just because a tighter cap exists. 240 is the floor because
    /// the exception line plus its first two frames still fit in it, which is the part of a stack
    /// that distinguishes one signature from another; below that the field stops being evidence.
    /// </summary>
    private static readonly int[] VerboseFieldCaps = [1200, 600, 360, 240];

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string ParametersJsonSchema { get; }

    public string OwnerAgent => InspectorAgent.Logs;

    protected IKqlQueryTextProvider KqlText => kqlProvider;

    protected InspectorOptions Limits => inspectorOptions.Value;

    protected ScoutOptions ScoutSettings => scoutOptions.Value;

    public abstract Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default);

    protected async Task<AgentToolResult> RunQueryAsync(string kql, int maxRows, CancellationToken ct)
    {
        try
        {
            var source = InsightsSourceResolver.ResolveForDomain(
                insightsOptions.Value, ScoutSettings.ResolvedEntryDomain, ScoutSettings.ResolvedEntryEnvironment);

            var resolved = await sourceResolver.ResolveAsync(kql, ScoutSettings.ResolvedEntryEnvironment, ct);
            var rows = await telemetrySource.QueryAsync(
                new TelemetryQueryRequest(resolved, source.AppInsightsAppId, source.TenantId), ct);

            return Envelope(rows, maxRows, Limits.ResolvedMaxToolResultChars);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] tool {Tool} could not query telemetry.", Name);
            return AgentToolResult.Failed(InspectorKqlGuards.Truncate(ex.Message, 500));
        }
    }

    /// <summary>
    /// Spends the character budget on breadth before depth: it shortens the verbose fields of
    /// every row first and drops whole rows only when even the tightest shortening does not fit.
    ///
    /// Measured on a live package, the old order (drop rows, never touch fields) kept 3 of 34 rows
    /// because the average row serialized to 2373 characters of which StackTrace alone was 850.
    /// The agents then escalated with "only 2 of 166 exception rows were sampled" - and they were
    /// right. Breadth is what a diagnosis needs (how many, which operations, which hosts); the
    /// stack traces that cost it were 166 near-identical copies of the same frames.
    ///
    /// <paramref name="upstreamTruncated"/> carries a cap that was already applied before these
    /// rows got here (the Scout's own collection limit): 200 of 200 rows is still a partial list
    /// when the window held 2352 events, and reporting it as complete is the exact failure this
    /// envelope exists to prevent.
    /// </summary>
    protected static AgentToolResult Envelope<T>(IReadOnlyList<T> rows, int maxRows, int maxChars, bool upstreamTruncated = false)
    {
        var total = rows.Count;
        var kept = (total > maxRows ? rows.Take(maxRows) : rows)
            .Select(row => JsonSerializer.SerializeToNode(row, JsonOptions))
            .ToList();

        var fieldsShortened = false;
        var json = Serialize(kept, total, maxChars, upstreamTruncated, fieldsShortened);

        foreach (var cap in VerboseFieldCaps)
        {
            if (json.Length <= maxChars) break;
            if (!ShortenVerboseFields(kept, cap)) continue;

            fieldsShortened = true;
            json = Serialize(kept, total, maxChars, upstreamTruncated, fieldsShortened);
        }

        // Last resort, and only for rows whose non-shortenable part (identifiers, timestamps) is
        // itself over budget. Dropping rows stays row-aligned so the payload ends on a parseable
        // token rather than mid-string.
        while (json.Length > maxChars && kept.Count > 1)
        {
            var target = Math.Max(1, (int)(kept.Count * (maxChars / (double)json.Length) * 0.9));
            if (target >= kept.Count) target = kept.Count - 1;
            kept = kept.Take(target).ToList();
            json = Serialize(kept, total, maxChars, upstreamTruncated, fieldsShortened);
        }

        var truncated = kept.Count < total || upstreamTruncated;
        if (json.Length > maxChars)
        {
            json = json[..maxChars] + "…[truncated]";
            truncated = true;
        }

        // AgentToolResult.Truncated stays "rows are missing" only. It is what the transcript and
        // the UI read as "this tool did not return everything it was asked for"; raising it for a
        // row-complete result would put the ambiguity back one layer up.
        return new AgentToolResult(true, json, kept.Count, truncated, null);
    }

    /// <summary>
    /// Shortens every verbose field of every row to <paramref name="cap"/>, in place. False means
    /// nothing was longer than the cap, so the caller moves to the next cap instead of
    /// re-serializing an unchanged payload.
    ///
    /// Goes through <see cref="JsonNode"/> rather than through T because T differs per tool - the
    /// drill-down carries raw <see cref="JsonElement"/> rows out of the package, the query path
    /// carries <c>TelemetryJourneyRow</c> - and both serialize to a flat object of scalars. Field
    /// NAMES are then the only contract the trimming depends on, which is also why a row shape
    /// nobody listed here is simply left alone rather than cut blindly.
    /// </summary>
    private static bool ShortenVerboseFields(List<JsonNode?> rows, int cap)
    {
        var changed = false;

        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;

            foreach (var (name, value) in obj.ToList())
            {
                if (!VerboseRowFields.Contains(name)) continue;
                if (value is not JsonValue scalar) continue;
                if (!scalar.TryGetValue<string>(out var text) || text is null || text.Length <= cap) continue;

                obj[name] = InspectorKqlGuards.Truncate(text, cap);
                changed = true;
            }
        }

        return changed;
    }

    private static string Serialize(
        IReadOnlyList<JsonNode?> page, int totalRows, int maxChars, bool upstreamTruncated, bool fieldsShortened)
    {
        var rowsMissing = page.Count < totalRows;
        var payload = new InspectorToolPayload<JsonNode?>(
            rowsMissing || upstreamTruncated,
            fieldsShortened,
            page.Count,
            totalRows,
            BuildNote(page.Count, totalRows, maxChars, upstreamTruncated, fieldsShortened),
            page);

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// The honesty contract in prose, because that is what the model reads. The two conditions are
    /// reported separately on purpose: "this is a partial list" is reserved for missing rows, and
    /// a complete list whose text was abbreviated says so explicitly, otherwise the model keeps
    /// discounting its own evidence and escalates a case it could have resolved.
    /// </summary>
    private static string? BuildNote(
        int returnedRows, int totalRows, int maxChars, bool upstreamTruncated, bool fieldsShortened)
    {
        var rowsMissing = returnedRows < totalRows;
        if (!rowsMissing && !upstreamTruncated && !fieldsShortened) return null;

        var note = new StringBuilder();

        if (fieldsShortened)
        {
            note.Append($"Long text fields (stack traces, messages) were shortened to fit {maxChars} characters and end with \"…[truncated]\". ");
        }

        if (rowsMissing)
        {
            note.Append($"Returned {returnedRows} of {totalRows} rows (capped at {maxChars} characters). ");
        }

        if (upstreamTruncated)
        {
            note.Append("The collection that produced these rows was itself capped, so rows are missing before this one. ");
        }

        note.Append(rowsMissing || upstreamTruncated
            ? "This is a partial list - do not read it as everything that happened."
            : $"No row is missing: all {returnedRows} rows are here and every identifier in them is intact, so counts, operations, hosts and timings can be relied on as complete.");

        return note.ToString();
    }

    /// <summary>
    /// The club/host this case is about, already sanitized. Null when the Scout could not attribute
    /// the traffic: a query scoped to "[host/club unavailable]" would silently return nothing, and
    /// an empty baseline reads as "this club never has exceptions", which is the opposite of true.
    /// </summary>
    protected static string? ScopeEntity(InvestigationPackage package)
    {
        var entity = InspectorKqlGuards.SanitizeEntityKey(package.EntityKey);
        return entity.Length == 0 || entity == InspectorKqlGuards.UnresolvedEntity ? null : entity;
    }

    protected static AgentToolResult NoEntity(InvestigationPackage package) =>
        AgentToolResult.Failed(
            $"the package has no club or host to scope the query to (EntityKey=\"{package.EntityKey}\"), so any result would be about every club at once");
}

/// <summary>
/// The individual requests and exceptions behind the aggregate row.
///
/// This tool normally runs NO query: the Scout already collected exactly this evidence and it
/// travels inside the package (<see cref="InvestigationPackage.RawEvidenceJson"/>). Re-running the
/// drill-down would spend ~4s and one production query per call to retrieve bytes the agent is
/// already holding - and, because it runs later, it can return a different set than the one the
/// rest of the case was built on. The query path exists only for a package whose collection was
/// switched off or failed.
/// </summary>
public sealed class DrillDownEventsTool(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<ScoutOptions> scoutOptions,
    IOptions<InspectorOptions> inspectorOptions,
    ILogger<DrillDownEventsTool> logger)
    : LogsAgentTool(telemetrySource, kqlProvider, sourceResolver, insightsOptions, scoutOptions, inspectorOptions, logger)
{
    public override string Name => "drill_down_events";

    public override string Description =>
        "The individual requests and exceptions behind the aggregate row of this case (timestamp, source, operation id, path, result code, exception type and message). Already collected when the case was opened, so this is normally free. Use it first: it is the raw evidence every other tool is interpreting.";

    public override string ParametersJsonSchema => $$$"""
        {"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":{{{Limits.ResolvedMaxToolRows}}},"description":"How many events to return, newest-first ordering preserved from collection. Defaults to the configured maximum."}},"required":[],"additionalProperties":false}
        """;

    public override async Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default)
    {
        var maxRows = Limits.ResolvedMaxToolRows;
        var limit = InspectorKqlGuards.Clamp(
            InspectorKqlGuards.ReadIntArgument(argumentsJson, "limit", maxRows), 1, maxRows);

        if (TryReadPackagedEvidence(context.Package, out var events, out var collectionTruncated))
        {
            return Envelope(events, limit, Limits.ResolvedMaxToolResultChars, collectionTruncated);
        }

        string kql;
        try
        {
            kql = BuildKql(KqlText.GetScoutDrillDownKql(), context.Package, limit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentToolResult.Failed($"the drill-down query could not be built: {InspectorKqlGuards.Truncate(ex.Message, 300)}");
        }

        return await RunQueryAsync(kql, limit, ct);
    }

    /// <summary>
    /// Exposed for tests. The operation ids and window come from the package, never from the
    /// model; <see cref="JsonSerializer"/> writes the id array so a stray character in an id
    /// cannot escape the dynamic literal.
    /// </summary>
    public static string BuildKql(string template, InvestigationPackage package, int maxRows)
    {
        var kql = template
            .Replace("{{OPERATION_IDS}}", JsonSerializer.Serialize(ReadOperationIds(package)))
            .Replace("{{TIME_START}}", InspectorKqlGuards.ToKqlDatetime(package.WindowFrom))
            .Replace("{{TIME_END}}", InspectorKqlGuards.ToKqlDatetime(package.WindowTo))
            .Replace("{{MAX_ROWS}}", maxRows.ToString());

        return InspectorKqlGuards.RequireNoPlaceholders(kql, "Inspector drill-down");
    }

    /// <summary>
    /// Reads the rows the Scout already fetched. Returns false when collection was skipped, failed
    /// or came back empty - "the package carries no evidence" must fall through to a real query
    /// rather than be reported as "there were no events". <paramref name="collectionTruncated"/> is
    /// the Scout's own cap, carried forward so a sample does not arrive looking like a census.
    /// </summary>
    private static bool TryReadPackagedEvidence(
        InvestigationPackage package,
        out IReadOnlyList<JsonElement> events,
        out bool collectionTruncated)
    {
        events = [];
        collectionTruncated = false;
        if (string.IsNullOrWhiteSpace(package.RawEvidenceJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(package.RawEvidenceJson);
            if (!InspectorKqlGuards.TryGetProperty(doc.RootElement, "rowsJson", out var rowsJson)) return false;
            if (rowsJson.ValueKind != JsonValueKind.String) return false;

            using var rows = JsonDocument.Parse(rowsJson.GetString() ?? "[]");
            if (rows.RootElement.ValueKind != JsonValueKind.Array) return false;

            var parsed = rows.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            if (parsed.Count == 0) return false;

            collectionTruncated = InspectorKqlGuards.TryGetProperty(doc.RootElement, "truncated", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            events = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>An empty list is valid - the drill-down then covers the whole bucket instead of nothing.</summary>
    private static string[] ReadOperationIds(InvestigationPackage package)
    {
        try
        {
            using var doc = JsonDocument.Parse(package.DeterministicJson);
            if (!InspectorKqlGuards.TryGetProperty(doc.RootElement, "groupPayload", out var payload)) return [];
            if (!InspectorKqlGuards.TryGetProperty(payload, "Ops", out var ops)) return [];
            if (ops.ValueKind != JsonValueKind.Array) return [];

            return ops.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// The club's own exception baseline - the tool that decides whether this case is a real anomaly
/// or an artifact of how the Scout detects.
///
/// The exception board compares a bucket against the one immediately before it, so a club coming
/// back from a quiet valley trips the rule at a volume that is normal for it. Measured on
/// stays.tmobiletravel.com: median 358 and P95 2243 over 7 days, against 1683 in the bucket the
/// Scout packaged as "Investigate now" - below its own P95. Nothing else in the package can show
/// that, which is why this tool exists.
/// </summary>
public sealed class ClubBaselineTool(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<ScoutOptions> scoutOptions,
    IOptions<InspectorOptions> inspectorOptions,
    ILogger<ClubBaselineTool> logger)
    : LogsAgentTool(telemetrySource, kqlProvider, sourceResolver, insightsOptions, scoutOptions, inspectorOptions, logger)
{
    public const int DefaultLookbackDays = 7;

    public override string Name => "club_baseline";

    public override string Description =>
        "This club's own exception baseline in 5-minute buckets over the last N days: bucket count, median, P95, maximum and mean, plus where THIS case's exception count falls in that distribution. Answers 'is this volume actually abnormal for this club?' - the detection rule only compares against the preceding bucket, so a recovery from a quiet period can look like a spike.";

    public override string ParametersJsonSchema => $$$"""
        {"type":"object","properties":{"lookbackDays":{"type":"integer","minimum":1,"maximum":{{{MaxLookbackDays}}},"description":"Days of history to build the baseline from. Defaults to 7. Values outside the range are clamped, not rejected."}},"required":[],"additionalProperties":false}
        """;

    /// <summary>The configured hour ceiling expressed in whole days - the unit the tool's argument uses.</summary>
    private int MaxLookbackDays => Math.Max(1, Limits.ResolvedMaxQueryLookbackHours / 24);

    public override async Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default)
    {
        var entity = ScopeEntity(context.Package);
        if (entity is null) return NoEntity(context.Package);

        var lookbackDays = InspectorKqlGuards.ReadIntArgument(argumentsJson, "lookbackDays", DefaultLookbackDays);

        string kql;
        try
        {
            kql = BuildKql(
                KqlText.GetInspectorLogsKql(InspectorLogsQuery.ClubBaseline),
                context.Package,
                lookbackDays,
                MaxLookbackDays,
                ScoutSettings.ResolvedBucketMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentToolResult.Failed($"the baseline query could not be built: {InspectorKqlGuards.Truncate(ex.Message, 300)}");
        }

        return await RunQueryAsync(kql, Limits.ResolvedMaxToolRows, ct);
    }

    /// <summary>
    /// Exposed for tests: a clamp that silently stopped working would let a model scan months of
    /// three production resources, and that only shows up on the bill.
    /// </summary>
    public static string BuildKql(
        string template,
        InvestigationPackage package,
        int lookbackDays,
        int maxLookbackDays,
        int bucketMinutes)
    {
        var entity = InspectorKqlGuards.SanitizeEntityKey(package.EntityKey);
        var kql = template
            .Replace("{{ENTITY_KEY_B64}}", InspectorKqlGuards.ToBase64(entity))
            .Replace("{{TIME_END}}", InspectorKqlGuards.ToKqlDatetime(package.WindowTo))
            .Replace("{{LOOKBACK_DAYS}}", InspectorKqlGuards.Clamp(lookbackDays, 1, Math.Max(1, maxLookbackDays)).ToString())
            .Replace("{{BUCKET_MINUTES}}", InspectorKqlGuards.Clamp(bucketMinutes, 1, 60).ToString())
            .Replace("{{CURRENT_EXCEPTIONS}}", Math.Max(0, package.ExceptionCount).ToString());

        return InspectorKqlGuards.RequireNoPlaceholders(kql, "Inspector club baseline");
    }
}

/// <summary>
/// The exception signatures of the packaged window, by count and share of the total. Answers "one
/// failure repeating, or many different ones at once?" - a single dominant signature points at one
/// broken dependency, a flat distribution at something wider.
/// </summary>
public sealed class SignatureBreakdownTool(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<ScoutOptions> scoutOptions,
    IOptions<InspectorOptions> inspectorOptions,
    ILogger<SignatureBreakdownTool> logger)
    : LogsAgentTool(telemetrySource, kqlProvider, sourceResolver, insightsOptions, scoutOptions, inspectorOptions, logger)
{
    public const int DefaultTop = 10;

    public override string Name => "signature_breakdown";

    public override string Description =>
        "The exception signatures (type, problem id, message) of this case's window for this club, with count, share of the total and example operation ids. Answers whether this is one failure repeating or many different ones at once.";

    public override string ParametersJsonSchema => $$$"""
        {"type":"object","properties":{"top":{"type":"integer","minimum":1,"maximum":{{{Limits.ResolvedMaxToolRows}}},"description":"How many signatures to return, most frequent first. Defaults to 10. Values outside the range are clamped, not rejected."}},"required":[],"additionalProperties":false}
        """;

    public override async Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default)
    {
        var entity = ScopeEntity(context.Package);
        if (entity is null) return NoEntity(context.Package);

        var top = InspectorKqlGuards.ReadIntArgument(argumentsJson, "top", DefaultTop);

        string kql;
        try
        {
            kql = BuildKql(
                KqlText.GetInspectorLogsKql(InspectorLogsQuery.SignatureBreakdown),
                context.Package,
                top,
                Limits.ResolvedMaxToolRows,
                "Inspector signature breakdown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentToolResult.Failed($"the signature query could not be built: {InspectorKqlGuards.Truncate(ex.Message, 300)}");
        }

        return await RunQueryAsync(kql, InspectorKqlGuards.Clamp(top, 1, Limits.ResolvedMaxToolRows), ct);
    }

    /// <summary>Shared by both window-scoped breakdowns - identical placeholders, different aggregation.</summary>
    public static string BuildKql(string template, InvestigationPackage package, int top, int maxRows, string label)
    {
        var entity = InspectorKqlGuards.SanitizeEntityKey(package.EntityKey);
        var kql = template
            .Replace("{{ENTITY_KEY_B64}}", InspectorKqlGuards.ToBase64(entity))
            .Replace("{{TIME_START}}", InspectorKqlGuards.ToKqlDatetime(package.WindowFrom))
            .Replace("{{TIME_END}}", InspectorKqlGuards.ToKqlDatetime(package.WindowTo))
            .Replace("{{MAX_ROWS}}", InspectorKqlGuards.Clamp(top, 1, Math.Max(1, maxRows)).ToString());

        return InspectorKqlGuards.RequireNoPlaceholders(kql, label);
    }
}

/// <summary>
/// Where the failing clients were: browser, OS, country and city.
///
/// This is not a nice-to-have. On the T-Mobile case the stack trace was masked by the browser's
/// same-origin policy, so the exception itself said nothing - and 99.3% of the events (2336 of
/// 2352) turned out to be Mobile Safari UI/WKWebView on iOS 18.7. With the stack gone, the client
/// distribution was the only lead there was. Coverage measured on that case: ~85% of rows carry
/// browser/OS, 100% carry country/city; missing values are reported as "[unknown]" rather than
/// dropped, so a coverage gap cannot be mistaken for a real client.
/// </summary>
public sealed class ClientBreakdownTool(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<ScoutOptions> scoutOptions,
    IOptions<InspectorOptions> inspectorOptions,
    ILogger<ClientBreakdownTool> logger)
    : LogsAgentTool(telemetrySource, kqlProvider, sourceResolver, insightsOptions, scoutOptions, inspectorOptions, logger)
{
    public const int DefaultTop = 10;

    public override string Name => "client_breakdown";

    public override string Description =>
        "The browser, OS, country and city of the failing clients in this case's window, with count and share of the total. The lead of last resort when a browser-side stack trace is masked by same-origin policy and the exception itself says nothing.";

    public override string ParametersJsonSchema => $$$"""
        {"type":"object","properties":{"top":{"type":"integer","minimum":1,"maximum":{{{Limits.ResolvedMaxToolRows}}},"description":"How many client combinations to return, most frequent first. Defaults to 10. Values outside the range are clamped, not rejected."}},"required":[],"additionalProperties":false}
        """;

    public override async Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default)
    {
        var entity = ScopeEntity(context.Package);
        if (entity is null) return NoEntity(context.Package);

        var top = InspectorKqlGuards.ReadIntArgument(argumentsJson, "top", DefaultTop);

        string kql;
        try
        {
            kql = SignatureBreakdownTool.BuildKql(
                KqlText.GetInspectorLogsKql(InspectorLogsQuery.ClientBreakdown),
                context.Package,
                top,
                Limits.ResolvedMaxToolRows,
                "Inspector client breakdown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentToolResult.Failed($"the client query could not be built: {InspectorKqlGuards.Truncate(ex.Message, 300)}");
        }

        return await RunQueryAsync(kql, InspectorKqlGuards.Clamp(top, 1, Limits.ResolvedMaxToolRows), ct);
    }
}
