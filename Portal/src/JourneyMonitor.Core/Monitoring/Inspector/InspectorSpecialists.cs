using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Inspector;

/// <summary>
/// JSON plumbing shared by every Inspector component. The serializer options are web-cased for
/// the same reason the Scout's are: these payloads are persisted and read back BY PROPERTY NAME
/// (the API and the card look up "summary"/"citations"), and PascalCase would make those lookups
/// silently return null.
/// </summary>
public static class InspectorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Models wrap JSON in ```json fences despite being told not to - ported from
    /// <c>DoctorAgentService.ExtractJsonObject</c> rather than shared, because the Doctor's copy
    /// is part of a contract that must keep working untouched.
    /// </summary>
    public static string? ExtractObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) text = text[..fenceEnd];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    /// <summary>Null for anything that is not a parseable JSON object - a caller must never have to tell "unparseable" from "parsed to something else".</summary>
    public static JsonDocument? TryParseObject(string? content)
    {
        var json = ExtractObject(content);
        if (json is null) return null;

        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ReadString(JsonElement element, string name, string fallback = "")
    {
        if (!InspectorKqlGuards.TryGetProperty(element, name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            _ => fallback,
        };
    }

    public static double ReadDouble(JsonElement element, string name, double fallback = 0)
    {
        if (!InspectorKqlGuards.TryGetProperty(element, name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), out var s) => s,
            _ => fallback,
        };
    }

    /// <summary>Every string of a JSON array property, skipping anything that is not a non-empty string.</summary>
    public static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!InspectorKqlGuards.TryGetProperty(element, name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) return [];

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…[truncated]";
}

/// <summary>
/// The PreProcess output: everything the agents are allowed to read about the case, already run
/// through <see cref="PromptSanitizer"/>.
///
/// Built from the package rather than carried on <see cref="InspectorContext"/> so the contract
/// stays as it is, and deterministic so every agent sees byte-identical facts - two agents
/// disagreeing because they were shown different text would be indistinguishable from two agents
/// genuinely disagreeing.
///
/// <see cref="ScoutReading"/> is kept apart from <see cref="Facts"/> on purpose: the Scout's
/// symptom line is one model's opinion, and merging it into the measurements is exactly the
/// failure the Scout's own two-column design exists to prevent.
/// </summary>
public sealed record InspectorCaseBrief(string Headline, string Facts, string ScoutReading)
{
    /// <summary>What this company calls a service, a step and the party affected. Never null - <see cref="CompanyVocabulary.Default"/> is the neutral, non-travel fallback.</summary>
    public CompanyVocabulary Vocabulary { get; init; } = CompanyVocabulary.Default;

    public string PromptBlock => $"""
        {Vocabulary.PromptBlock}

        MEASURED FACTS (from the detection board and Azure telemetry - these are measurements):
        {Facts}

        THE SCOUT'S READING (inference by another model, NOT a measurement - treat as a hypothesis):
        {ScoutReading}
        """;

    public static InspectorCaseBrief Build(InvestigationPackage package, ClubIdentity club, CompanyVocabulary? vocabulary = null)
    {
        var vocab = vocabulary ?? CompanyVocabulary.Default;
        var service = PromptSanitizer.Sanitize(package.Vertical, 80);
        var step = PromptSanitizer.Sanitize(package.Stage, 160);
        var entity = PromptSanitizer.Sanitize(package.EntityKey, 200);
        var resolvedName = club.IsResolved
            ? PromptSanitizer.Sanitize(club.BrandName, 120)
            : $"(this {vocab.EntityNoun} does not resolve to a known name)";
        var clubName = club.ClubName is { Length: > 0 } ? PromptSanitizer.Sanitize(club.ClubName, 120) : null;

        var facts = new StringBuilder();
        facts.AppendLine($"- Case id: {PromptSanitizer.Sanitize(package.CaseId, 300)}");
        facts.AppendLine($"- Board: {PromptSanitizer.Sanitize(package.Board, 40)} (the deterministic KQL board that flagged this, not a model)");
        facts.AppendLine($"- {vocab.ServiceLabel} / {vocab.StepLabel} / source: {service} / {step} / {PromptSanitizer.Sanitize(package.Source, 80)}");
        facts.AppendLine(clubName is null
            ? $"- {vocab.EntityLabel}: {entity} -> {resolvedName} (resolved by: {PromptSanitizer.Sanitize(package.ClubResolvedBy, 40)})"
            : $"- {vocab.EntityLabel}: {entity} -> {resolvedName} / {clubName} (resolved by: {PromptSanitizer.Sanitize(package.ClubResolvedBy, 40)})");
        facts.AppendLine($"- Priority: {PromptSanitizer.Sanitize(package.Priority, 80)} (rank {package.PriorityRank}) because: {PromptSanitizer.Sanitize(package.Reason, 400)}");
        facts.AppendLine($"- Window (UTC): {package.WindowFrom:O} .. {package.WindowTo:O}");
        facts.AppendLine($"- Counted in that window: {package.RequestCount} requests, {package.FailedRequestCount} failed, {package.ExceptionCount} exceptions, {package.AffectedUserCount} distinct end-user identities attributed");

        AppendPackageDetail(facts, package, vocab);

        return new InspectorCaseBrief(
            Headline: $"{service} {step} {entity} {PromptSanitizer.Sanitize(package.Reason, 200)}",
            Facts: facts.ToString().TrimEnd(),
            ScoutReading: ReadScoutAssessment(package))
        {
            Vocabulary = vocab,
        };
    }

    /// <summary>
    /// The board's own bag (paths, operations, exception types). Missing keys are skipped rather
    /// than printed empty: a label with "(none)" after it reads as "there were none", which is not
    /// the same statement as "the board did not carry this".
    /// </summary>
    private static void AppendPackageDetail(StringBuilder facts, InvestigationPackage package, CompanyVocabulary vocab)
    {
        using var doc = SafeParse(package.DeterministicJson);
        if (doc is null) return;

        if (InspectorKqlGuards.TryGetProperty(doc.RootElement, "groupPayload", out var payload))
        {
            AppendList(facts, "Paths seen", InspectorJson.ReadStringArray(payload, "Paths"));
            AppendList(facts, "Slowest paths", InspectorJson.ReadStringArray(payload, "SlowPaths"));
            AppendList(facts, "Operations", InspectorJson.ReadStringArray(payload, "Operations"));
            AppendList(facts, "Operation ids (usable to drill down)", InspectorJson.ReadStringArray(payload, "Ops"));
        }

        if (InspectorKqlGuards.TryGetProperty(doc.RootElement, "boardRow", out var boardRow))
        {
            AppendList(facts, "Exception types over the whole window", InspectorJson.ReadStringArray(boardRow, "ExceptionTypes"));

            var growth = InspectorJson.ReadDouble(boardRow, "ExceptionGrowthX");
            var previous = InspectorJson.ReadDouble(boardRow, "PreviousExceptions");
            if (growth > 0)
            {
                facts.AppendLine(
                    $"- Exception growth vs the PRECEDING bucket only: {growth:0.##}x (previous bucket: {previous:0}). " +
                    $"This is a measurement against ONE neighbouring bucket, not against this {vocab.EntityNoun}'s own baseline.");
            }
        }
    }

    private static void AppendList(StringBuilder facts, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        facts.AppendLine($"- {label}: {PromptSanitizer.SanitizeList(values, take: 10, maxItemLength: 200)}");
    }

    private static string ReadScoutAssessment(InvestigationPackage package)
    {
        using var doc = SafeParse(package.AssessmentJson);
        if (doc is null) return "(the Scout produced no assessment for this package)";

        var available = InspectorKqlGuards.TryGetProperty(doc.RootElement, "available", out var flag)
            && flag.ValueKind == JsonValueKind.True;
        if (!available)
        {
            var reason = InspectorJson.ReadString(doc.RootElement, "unavailableReason", "no reason recorded");
            return $"(none - the Scout's model was unavailable: {PromptSanitizer.Sanitize(reason, 300)})";
        }

        var symptom = PromptSanitizer.Sanitize(InspectorJson.ReadString(doc.RootElement, "symptom"), 400);
        var interpretation = PromptSanitizer.Sanitize(InspectorJson.ReadString(doc.RootElement, "interpretation"), 800);
        var confidence = InspectorJson.ReadDouble(doc.RootElement, "confidence");
        return $"- Symptom: {symptom}\n- Interpretation: {interpretation}\n- The Scout's own confidence: {confidence:0.##}";
    }

    private static JsonDocument? SafeParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The transcript writer and the cost meter of one investigation, shared by every agent.
///
/// It exists so that "one LLM call produces exactly one persisted turn, and its REAL usage lands
/// on the case" is implemented once instead of in six places where one of them would eventually
/// forget. Transcript writes are best-effort by contract - losing an audit row must never cost the
/// investigation it documents - but the accounting is not: it happens before the write, so a case
/// can never under-report what it spent because a log insert failed.
/// </summary>
public sealed class InspectorRecorder(IAgentTranscriptRepository transcripts, ILogger<InspectorRecorder> logger)
{
    private const int MaxErrorLength = 1024;

    /// <summary>Guards the counters on <see cref="ForensicCase"/>: the five specialists gather in parallel and would otherwise lose increments.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// One tool-capable completion, metered and recorded. <paramref name="classify"/> lets the
    /// caller mark the turn as <see cref="AgentOutcomeStatus.NeedsHelp"/>; a provider that came
    /// back unavailable is always <see cref="AgentOutcomeStatus.Excluded"/> with the reason
    /// visible, so a model that drops out of the debate is explained rather than simply absent.
    /// </summary>
    public async Task<(LlmCompletion Completion, Guid TurnId)> CallAsync(
        ForensicCase forensicCase,
        IToolCallingLlmClient llm,
        string model,
        string agentRole,
        string kind,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct,
        int round = 0,
        int hop = 0,
        string? inputContext = null,
        Func<LlmCompletion, (string Status, string? OutcomeJson)>? classify = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var completion = await llm.CompleteAsync(model, messages, tools, maxTokens, ct);
        stopwatch.Stop();

        var (status, outcomeJson) = completion.IsAvailable
            ? classify?.Invoke(completion) ?? (AgentOutcomeStatus.Final, null)
            : (AgentOutcomeStatus.Excluded, null);

        lock (_gate)
        {
            // Only a completion that actually reached the provider is counted as a call: an
            // unavailable one cost nothing and would otherwise inflate the only spend figure there is.
            if (completion.IsAvailable) forensicCase.LlmCalls++;
            forensicCase.TokensIn += completion.TokensIn;
            forensicCase.TokensOut += completion.TokensOut;
        }

        var turn = new AgentTurn
        {
            ForensicCaseId = forensicCase.Id,
            AgentRole = agentRole,
            Kind = kind,
            Round = round,
            Hop = hop,
            ModelUsed = model,
            InputContext = inputContext,
            PromptText = RenderPrompt(messages),
            RawResponseText = completion.Content,
            OutcomeStatus = status,
            OutcomeJson = outcomeJson,
            TokensIn = completion.TokensIn,
            TokensOut = completion.TokensOut,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ErrorMessage = completion.Error is null ? null : InspectorJson.Truncate(completion.Error, MaxErrorLength),
        };

        await PersistTurnAsync(turn, ct);
        return (completion, turn.Id);
    }

    /// <summary>A phase that reached a conclusion without a model still gets a turn, so the transcript has no gaps a reader has to explain.</summary>
    public async Task<Guid> RecordDeterministicTurnAsync(
        ForensicCase forensicCase,
        string agentRole,
        string kind,
        string promptText,
        string rawResponseText,
        string? outcomeJson,
        long durationMs,
        CancellationToken ct,
        string outcomeStatus = AgentOutcomeStatus.Final,
        string? errorMessage = null)
    {
        var turn = new AgentTurn
        {
            ForensicCaseId = forensicCase.Id,
            AgentRole = agentRole,
            Kind = kind,
            ModelUsed = null,
            PromptText = promptText,
            RawResponseText = rawResponseText,
            OutcomeStatus = outcomeStatus,
            OutcomeJson = outcomeJson,
            DurationMs = durationMs,
            ErrorMessage = errorMessage is null ? null : InspectorJson.Truncate(errorMessage, MaxErrorLength),
        };

        await PersistTurnAsync(turn, ct);
        return turn.Id;
    }

    public async Task RecordToolCallAsync(AgentToolCall toolCall, CancellationToken ct)
    {
        try
        {
            await transcripts.AddToolCallAsync(toolCall, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] could not persist the tool call {Tool}.", toolCall.ToolName);
        }
    }

    private async Task PersistTurnAsync(AgentTurn turn, CancellationToken ct)
    {
        try
        {
            await transcripts.AddTurnAsync(turn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] could not persist the {Role} turn.", turn.AgentRole);
        }
    }

    /// <summary>The whole exchange, not only the last message: a turn that is unreadable without its own context is not an audit record.</summary>
    private static string RenderPrompt(IReadOnlyList<LlmMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            sb.AppendLine($"### {message.Role}");
            if (!string.IsNullOrEmpty(message.Content)) sb.AppendLine(message.Content);
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    sb.AppendLine($"[tool call] {call.Name}({call.ArgumentsJson})");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The LogsAgent: writes nothing itself, but decides WHICH of its four bounded queries to run and
/// pivots on what comes back. The only specialist with a live telemetry source today, which is
/// why it is also the only one that spends tool rounds.
///
/// It shares <see cref="InspectorOptions.PlannerModel"/> rather than having a roster entry of its
/// own: a specialist is a role, not a debate participant, and a fifth model setting is one more
/// thing an operator has to keep in sync for no benefit.
/// </summary>
public sealed class LogsAgentSpecialist(
    IEnumerable<IAgentTool> tools,
    IToolCallingLlmClient llm,
    InspectorRecorder recorder,
    IOptions<InspectorOptions> options,
    ILogger<LogsAgentSpecialist> logger,
    // Trailing and optional so the composition root and the existing tests keep constructing this
    // the way they already do. Null means the neutral vocabulary, never a failed investigation.
    ICompanyAdapterProvider? companyAdapters = null) : IInspectorSpecialist
{
    /// <summary>Peer questions get a tighter budget than the agent's own investigation - answering one question is not a second investigation.</summary>
    private const int HelpToolRounds = 1;

    private readonly List<IAgentTool> _tools = tools.Where(t => t.OwnerAgent == InspectorAgent.Logs).ToList();

    public string AgentRole => InspectorAgent.Logs;

    public bool HasSource => true;

    public string NoSourceReason => "";

    public async Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default)
    {
        var vocab = await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct);
        var brief = InspectorCaseBrief.Build(context.Package, context.Club, vocab);
        var task = $$"""
            Investigate this case against the telemetry. Use your tools: start with drill_down_events
            (free - the raw events were already collected), then club_baseline to find out whether this
            volume is actually abnormal FOR THIS {{vocab.EntityNoun.ToUpperInvariant()}}, then the breakdowns if they add anything.

            Answer with STRICT JSON ONLY, no markdown fences, matching exactly:
            {"summary":"one line a human can act on","findings":[{"observation":"...","measurement":"the number or value you are quoting"}],"artifacts":[{"kind":"operation-id|endpoint|exception-type","value":"..."}],"unknowns":["what you could not determine"]}

            Rules: every observation must quote a value a tool actually returned. If a tool returned a
            partial list, say so. A result whose note says rows are all present and only long text was
            shortened is NOT a partial list - its counts, operations and identifiers are complete, so do
            not report it as a sampling gap. If the tools did not let you conclude anything, return an
            empty "findings" array and say why in "unknowns" - that is the preferred honest answer.
            """;

        return await RunLoopAsync(context, brief, task, options.Value.ResolvedMaxToolRounds, round: 0, hop: 0, inputContext: null, ct);
    }

    public async Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default)
    {
        var vocab = await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct);
        var brief = InspectorCaseBrief.Build(context.Package, context.Club, vocab);
        var sanitized = PromptSanitizer.Sanitize(question, 1000, whenEmpty: "(the peer asked nothing)");
        var task = $"""
            Another agent on this investigation is asking you one question. Answer it from the telemetry,
            using a tool if you need to. Answer in plain prose, at most 6 lines, quoting the values you
            used. If you cannot answer it with the sources you have, say exactly that and why - a wrong
            confident answer is worse than "I cannot tell".

            Their question: {sanitized}
            """;

        var entry = await RunLoopAsync(context, brief, task, Math.Min(HelpToolRounds, options.Value.ResolvedMaxToolRounds), round: 0, hop: 1, inputContext: sanitized, ct);
        return entry.Summary;
    }

    /// <summary>
    /// The bounded tool-calling loop. The last iteration deliberately offers NO tools: a model that
    /// is still asking for data when its budget runs out would otherwise produce no answer at all,
    /// and "the agent went quiet" is indistinguishable from "the agent found nothing".
    /// </summary>
    private async Task<BlackboardEntry> RunLoopAsync(
        InspectorContext context,
        InspectorCaseBrief brief,
        string task,
        int maxToolRounds,
        int round,
        int hop,
        string? inputContext,
        CancellationToken ct)
    {
        var toolDefinitions = _tools
            .Select(t => new LlmToolDefinition(t.Name, t.Description, t.ParametersJsonSchema))
            .ToList();

        var messages = new List<LlmMessage>
        {
            new(LlmRole.System,
                "You are the LogsAgent of an L3 reliability investigation team. You only report what the telemetry " +
                "shows. You never infer a root cause the data does not support, and you never invent an operation id, " +
                "endpoint or exception type - every artifact you name is verified against the real system afterwards, " +
                "and a fabricated one is visible to everyone."),
            new(LlmRole.User, $"{brief.PromptBlock}\n\n{task}"),
        };

        var toolCallsMade = 0;
        var rounds = Math.Max(0, maxToolRounds);

        for (var iteration = 0; iteration <= rounds; iteration++)
        {
            var offerTools = iteration < rounds && toolDefinitions.Count > 0;
            var (completion, turnId) = await recorder.CallAsync(
                context.Case, llm, options.Value.ResolvedPlannerModel, AgentRole, AgentKind.Specialist,
                messages, offerTools ? toolDefinitions : [], options.Value.ResolvedMaxTokensPerCall, ct,
                round, hop, inputContext);

            if (!completion.IsAvailable)
            {
                return new BlackboardEntry(
                    AgentRole,
                    AgentSourceStatus.Failed,
                    $"the model was unavailable, so this agent contributed nothing: {completion.Error}",
                    null);
            }

            if (offerTools && completion.RequestedTools)
            {
                messages.Add(new LlmMessage(LlmRole.Assistant, completion.Content, ToolCalls: completion.ToolCalls));
                foreach (var call in completion.ToolCalls)
                {
                    var result = await ExecuteToolAsync(call, context, turnId, ct);
                    messages.Add(new LlmMessage(LlmRole.Tool, result, ToolCallId: call.Id));
                    toolCallsMade++;
                }
                continue;
            }

            // Measured on the first real production run: the model answered a direct question in
            // prose, and the whole turn - which did contain real evidence - was thrown away purely
            // on format. One corrective round costs far less than losing the findings.
            if (InspectorJson.TryParseObject(completion.Content) is null)
            {
                messages.Add(new LlmMessage(LlmRole.Assistant, completion.Content));
                messages.Add(new LlmMessage(LlmRole.User,
                    "That was prose, not JSON. Re-send the SAME findings as a single JSON object matching the required shape, with no prose and no markdown fences around it."));

                var (retry, _) = await recorder.CallAsync(
                    context.Case, llm, options.Value.ResolvedPlannerModel, AgentRole, AgentKind.Specialist,
                    messages, [], options.Value.ResolvedMaxTokensPerCall, ct,
                    round, hop, inputContext);

                if (retry.IsAvailable && InspectorJson.TryParseObject(retry.Content) is not null)
                {
                    return BuildEntry(retry.Content, toolCallsMade);
                }
            }

            return BuildEntry(completion.Content, toolCallsMade);
        }

        // Unreachable: the final iteration offers no tools and therefore always returns above.
        return new BlackboardEntry(AgentRole, AgentSourceStatus.Failed, "the agent did not converge within its tool budget.", null);
    }

    private BlackboardEntry BuildEntry(string content, int toolCallsMade)
    {
        using var doc = InspectorJson.TryParseObject(content);
        if (doc is null)
        {
            // Keep what it actually said. The status already tells the reader this is unstructured
            // and unverified; discarding the text as well would hide evidence the agent really did
            // gather, which is how the first production run lost a usable finding.
            var said = content.Trim();
            var excerpt = said.Length == 0
                ? "it returned nothing at all."
                : $"it answered in prose instead of JSON, so none of it is machine-checkable: \"{(said.Length > 500 ? said[..500] + "…" : said)}\"";

            return new BlackboardEntry(AgentRole, AgentSourceStatus.Failed, excerpt, null);
        }

        var summary = InspectorJson.ReadString(doc.RootElement, "summary", "(the agent returned no summary)");
        var hasFindings = InspectorKqlGuards.TryGetProperty(doc.RootElement, "findings", out var findings)
            && findings.ValueKind == JsonValueKind.Array
            && findings.GetArrayLength() > 0;

        // "Ran and found nothing" only holds when it actually ran something. With zero tool calls
        // the agent never looked, and reporting that as Empty would read as evidence of absence.
        var status = hasFindings
            ? AgentSourceStatus.Contributed
            : toolCallsMade > 0 ? AgentSourceStatus.Empty : AgentSourceStatus.Failed;

        return new BlackboardEntry(AgentRole, status, summary, doc.RootElement.GetRawText());
    }

    private async Task<string> ExecuteToolAsync(LlmToolCall call, InspectorContext context, Guid turnId, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            var available = string.Join(", ", _tools.Select(t => t.Name));
            var error = $"there is no tool called \"{PromptSanitizer.Sanitize(call.Name, 80)}\". Available: {available}.";
            await PersistToolCallAsync(turnId, call, AgentToolResult.Failed(error), 0, ct);
            return JsonSerializer.Serialize(new { error }, InspectorJson.Options);
        }

        var stopwatch = Stopwatch.StartNew();
        AgentToolResult result;
        try
        {
            result = await tool.ExecuteAsync(call.ArgumentsJson, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tools are contracted never to throw, but one breaking that contract must cost this
            // tool call and not the investigation.
            logger.LogWarning(ex, "[InspectorMonkey] tool {Tool} threw instead of returning a failed result.", tool.Name);
            result = AgentToolResult.Failed(InspectorJson.Truncate(ex.Message, 300));
        }
        stopwatch.Stop();

        await PersistToolCallAsync(turnId, call, result, stopwatch.ElapsedMilliseconds, ct);
        return result.Success
            ? result.ResultJson
            : JsonSerializer.Serialize(new { error = result.Error }, InspectorJson.Options);
    }

    private Task PersistToolCallAsync(Guid turnId, LlmToolCall call, AgentToolResult result, long durationMs, CancellationToken ct) =>
        recorder.RecordToolCallAsync(new AgentToolCall
        {
            AgentTurnId = turnId,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            ResultJson = result.ResultJson,
            Truncated = result.Truncated,
            Success = result.Success,
            ErrorMessage = result.Error is null ? null : InspectorJson.Truncate(result.Error, 1024),
            ResultRowCount = result.RowCount,
            DurationMs = durationMs,
        }, ct);
}

/// <summary>
/// The memory bank: have we concluded anything about this shape of problem before?
///
/// Deliberately spends no LLM call. Retrieval is already a similarity search, and asking a model
/// to summarise what came back would add a second place for a hallucination to enter between the
/// stored text and the blackboard. What it contributes is the chunks themselves, verbatim, so the
/// debate quotes real precedents and grounding can check the references.
/// </summary>
public sealed class MemoryAgentSpecialist(
    IKnowledgeRetriever knowledge,
    InspectorRecorder recorder,
    ILogger<MemoryAgentSpecialist> logger) : IInspectorSpecialist
{
    private const int TopK = 5;

    public string AgentRole => InspectorAgent.Memory;

    public bool HasSource => true;

    public string NoSourceReason => "";

    public async Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default)
    {
        var brief = InspectorCaseBrief.Build(context.Package, context.Club);
        var (entry, _) = await RetrieveAsync(context, brief.Headline, ct);
        return entry;
    }

    public async Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default)
    {
        var sanitized = PromptSanitizer.Sanitize(question, 1000, whenEmpty: "(the peer asked nothing)");
        var (entry, _) = await RetrieveAsync(context, sanitized, ct);
        return entry.Summary;
    }

    private async Task<(BlackboardEntry Entry, int Count)> RetrieveAsync(InspectorContext context, string query, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<KnowledgeChunk> chunks;
        try
        {
            // Scoped to the company the case belongs to. Closing this on the RCA alone would not
            // have been enough: a precedent pulled in here reaches the debate and the blackboard.
            var profileKey = string.IsNullOrWhiteSpace(context.Case.ProfileKey) ? null : context.Case.ProfileKey;
            chunks = await knowledge.RetrieveAsync(query, TopK, ct, profileKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] the knowledge base could not be queried.");
            stopwatch.Stop();
            var failure = new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Failed,
                $"the knowledge base could not be queried, so no precedent was checked: {InspectorJson.Truncate(ex.Message, 300)}",
                null);
            await recorder.RecordDeterministicTurnAsync(
                context.Case, AgentRole, AgentKind.Specialist, query, failure.Summary, null,
                stopwatch.ElapsedMilliseconds, ct, AgentOutcomeStatus.Error, ex.Message);
            return (failure, 0);
        }
        stopwatch.Stop();

        var payload = chunks
            .Select(c => new
            {
                chunkId = c.Id,
                sourceType = c.SourceType,
                sourceRef = c.SourceRef,
                content = InspectorJson.Truncate(c.Content, 1200),
                createdAt = c.CreatedAt,
            })
            .ToList();
        var findingsJson = JsonSerializer.Serialize(new { precedents = payload }, InspectorJson.Options);

        var entry = chunks.Count == 0
            ? new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Empty,
                "the knowledge base was searched and holds no precedent for this shape of problem. " +
                "Note the corpus is self-bootstrapped from this platform's own past diagnoses, so an empty result also means nobody has investigated anything like it here yet.",
                findingsJson)
            : new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Contributed,
                $"{chunks.Count} precedent(s) retrieved from the knowledge base; the text is attached verbatim and nothing was summarised by a model.",
                findingsJson);

        await recorder.RecordDeterministicTurnAsync(
            context.Case, AgentRole, AgentKind.Specialist, query, findingsJson, findingsJson, stopwatch.ElapsedMilliseconds, ct);

        return (entry, chunks.Count);
    }
}

/// <summary>
/// Reconstructs the user flow around the failing stage from the stored journey snapshot. It
/// reconstructs - it does not reproduce: nothing here replays a request, so the correct name for
/// what it produces is "what the last synced snapshot shows", not "what happens when you try it".
///
/// Deterministic for the same reason as the MemoryAgent: the numbers come out of SQL and a model
/// rephrasing them can only add a place to be wrong.
/// </summary>
public sealed class ReproAgentSpecialist(
    IJourneyStore journeys,
    InspectorRecorder recorder,
    ILogger<ReproAgentSpecialist> logger,
    ICompanyAdapterProvider? companyAdapters = null) : IInspectorSpecialist
{
    /// <summary>
    /// The board's vertical labels onto the journey store's domain keys. Anything else - including
    /// the board's very common "Shared / unclassified" - has no snapshot to read and is reported
    /// as such, never silently as "no journeys were affected".
    /// </summary>
    private static readonly Dictionary<string, string> DomainByVertical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hotels"] = "hotels",
        ["Hotel"] = "hotels",
        ["Cars"] = "cars",
        ["Car"] = "cars",
        ["Cruises"] = "cruises",
        ["Cruise"] = "cruises",
        ["Air"] = "air",
    };

    public string AgentRole => InspectorAgent.Repro;

    public bool HasSource => true;

    public string NoSourceReason => "";

    public async Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var vocab = await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct);
        var vertical = context.Package.Vertical ?? "";
        var prompt = $"stored journey snapshot for {vocab.ServiceNoun} \"{vertical}\", {vocab.StepNoun} \"{context.Package.Stage}\"";

        if (!DomainByVertical.TryGetValue(vertical.Trim(), out var domain))
        {
            stopwatch.Stop();
            var unmapped = new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Failed,
                $"the board labelled this case \"{PromptSanitizer.Sanitize(vertical, 80)}\", which does not map to any stored journey domain " +
                $"({string.Join(", ", DomainByVertical.Values.Distinct())}), so no snapshot could be read. This is not evidence that no journeys were affected.",
                null);
            await recorder.RecordDeterministicTurnAsync(
                context.Case, AgentRole, AgentKind.Specialist, prompt, unmapped.Summary, null, stopwatch.ElapsedMilliseconds, ct);
            return unmapped;
        }

        StoredJourneys? snapshot;
        try
        {
            snapshot = await journeys.GetLatestAsync(domain, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] the stored journey snapshot for {Domain} could not be read.", domain);
            stopwatch.Stop();
            var failure = new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Failed,
                $"the stored journey snapshot for \"{domain}\" could not be read: {InspectorJson.Truncate(ex.Message, 300)}",
                null);
            await recorder.RecordDeterministicTurnAsync(
                context.Case, AgentRole, AgentKind.Specialist, prompt, failure.Summary, null,
                stopwatch.ElapsedMilliseconds, ct, AgentOutcomeStatus.Error, ex.Message);
            return failure;
        }

        var reconstruction = Reconstruct(snapshot, domain, context.Package.Stage);
        stopwatch.Stop();

        var findingsJson = JsonSerializer.Serialize(reconstruction, InspectorJson.Options);
        var entry = reconstruction.JourneysExamined == 0
            ? new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Empty,
                $"the \"{domain}\" journey snapshot is empty, so the user flow around this {vocab.StepNoun} could not be reconstructed.",
                findingsJson)
            : new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Contributed,
                $"reconstructed from the last \"{domain}\" snapshot ({reconstruction.SnapshotFetchedAt:O}): {reconstruction.JourneysExamined} journeys, " +
                $"{reconstruction.JourneysReachingStage} of which reached \"{reconstruction.Stage}\". This is the stored snapshot, not a replay of the failing request.",
                findingsJson);

        await recorder.RecordDeterministicTurnAsync(
            context.Case, AgentRole, AgentKind.Specialist, prompt, findingsJson, findingsJson, stopwatch.ElapsedMilliseconds, ct);

        return entry;
    }

    public async Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default)
    {
        // The reconstruction is the whole of what this agent knows, so a peer question is answered
        // with the same deterministic read rather than with a model paraphrasing it.
        var entry = await GatherAsync(context, ct);
        return $"{entry.Summary}\n{entry.FindingsJson ?? "(no detail)"}";
    }

    private static JourneyReconstruction Reconstruct(StoredJourneys? snapshot, string domain, string stage)
    {
        var rows = snapshot?.Rows ?? [];
        var reachedStage = 0;
        var statusBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var furthestStage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        double maxStageMs = 0;
        double totalStageMs = 0;

        foreach (var row in rows)
        {
            string? last = null;
            foreach (var bag in row.GetJourneyStages())
            {
                var bagStage = InspectorJson.ReadString(bag, "stage");
                if (bagStage.Length > 0) last = bagStage;
                if (!MatchesStage(bagStage, stage)) continue;

                reachedStage++;
                var status = InspectorJson.ReadString(bag, "status", "(unreported)");
                statusBreakdown[status] = statusBreakdown.GetValueOrDefault(status) + 1;

                var ms = InspectorJson.ReadDouble(bag, "stageTotalMs");
                totalStageMs += ms;
                if (ms > maxStageMs) maxStageMs = ms;
            }

            if (last is not null) furthestStage[last] = furthestStage.GetValueOrDefault(last) + 1;
        }

        return new JourneyReconstruction(
            Domain: domain,
            Stage: stage,
            SnapshotFetchedAt: snapshot?.FetchedAt,
            JourneysExamined: rows.Count,
            JourneysReachingStage: reachedStage,
            MaxStageMs: maxStageMs,
            MeanStageMs: reachedStage == 0 ? 0 : totalStageMs / reachedStage,
            StageStatusBreakdown: statusBreakdown,
            FurthestStageReached: furthestStage,
            Note: "The journey snapshot is a capped sample refreshed by the sync timer, not a census of traffic in this case's window.");
    }

    /// <summary>The board's stage label and the journey's own stage label are produced by different classifiers, so an exact match would almost always miss.</summary>
    private static bool MatchesStage(string bagStage, string stage)
    {
        if (bagStage.Length == 0 || stage.Length == 0) return false;
        return bagStage.Contains(stage, StringComparison.OrdinalIgnoreCase)
            || stage.Contains(bagStage, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What the ReproAgent could reconstruct, as measurements only.</summary>
public sealed record JourneyReconstruction(
    string Domain,
    string Stage,
    DateTimeOffset? SnapshotFetchedAt,
    int JourneysExamined,
    int JourneysReachingStage,
    double MaxStageMs,
    double MeanStageMs,
    IReadOnlyDictionary<string, int> StageStatusBreakdown,
    IReadOnlyDictionary<string, int> FurthestStageReached,
    string Note);

/// <summary>
/// A specialist with nothing to look in. It costs no LLM call on purpose: a model asked to
/// investigate without a source can only ever answer "I found nothing", which reads as evidence of
/// absence and is not - and it would be charged for saying it.
/// </summary>
public abstract class SourcelessSpecialist(string agentRole, string noSourceReason) : IInspectorSpecialist
{
    public string AgentRole => agentRole;

    public bool HasSource => false;

    public string NoSourceReason => noSourceReason;

    public Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default) =>
        Task.FromResult(BlackboardEntry.NoSource(agentRole, noSourceReason));

    public Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default) =>
        Task.FromResult($"{agentRole} cannot answer: {noSourceReason}");
}

public sealed class CodeAgentSpecialist() : SourcelessSpecialist(
    InspectorAgent.Code,
    "no repository or code index is wired up, so this agent could not have found a file, symbol or commit even if one were relevant. " +
    "Nothing it reports is evidence about the code.");

/// <summary>
/// Where in the source the endpoints and exception types of this case live.
///
/// Returns LOCATIONS, never source: the transcript is persisted in SQL and served over HTTP, so
/// pulling file contents in would put production code in this database as a side effect of
/// diagnosing a latency spike. That is a decision, not a limitation of the index.
///
/// Spends no LLM call. The identifiers worth searching for are already in the case, and the index
/// combines multiple terms with AND, so the queries are built here, one term at a time.
/// </summary>
public sealed class RealCodeAgentSpecialist(
    ICodeSearch search,
    InspectorRecorder recorder,
    ILogger<RealCodeAgentSpecialist> logger) : IInspectorSpecialist
{
    /// <summary>One HTTP call each, inside an investigation that is already ~90 seconds long.</summary>
    private const int MaxTerms = 3;

    public string AgentRole => InspectorAgent.Code;

    public bool HasSource => search.HasSource;

    public string NoSourceReason => search.NoSourceReason;

    public Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default) =>
        SearchAsync(context, BuildTerms(context), ct);

    public async Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default)
    {
        // A peer's question is free-form text; only identifier-shaped words are usable as search
        // terms, and anything else would be a model writing the query after all.
        var terms = ExtractIdentifiers(question).Take(MaxTerms).ToList();
        var entry = await SearchAsync(context, terms.Count > 0 ? terms : BuildTerms(context), ct);
        return entry.Summary;
    }

    /// <summary>
    /// The bare last segment of each path, plus the leaf exception types. Measured against the real
    /// index: "searchrooms" returns the actual call sites with a "def" match, while
    /// "searchrooms.json" and "/rentals/searchrooms.json" degrade to wiki pages that merely mention
    /// the route.
    /// </summary>
    private static IReadOnlyList<string> BuildTerms(InspectorContext context)
    {
        var terms = new List<string>();

        using (var doc = InspectorJson.TryParseObject(context.Package.DeterministicJson))
        {
            if (doc is not null)
            {
                if (InspectorKqlGuards.TryGetProperty(doc.RootElement, "groupPayload", out var payload))
                {
                    foreach (var path in InspectorJson.ReadStringArray(payload, "SlowPaths")
                                 .Concat(InspectorJson.ReadStringArray(payload, "Paths")))
                    {
                        var segment = LastPathSegment(path);
                        if (segment.Length >= 4) terms.Add(segment);
                    }
                }

                if (InspectorKqlGuards.TryGetProperty(doc.RootElement, "boardRow", out var boardRow))
                {
                    terms.AddRange(InspectorJson.ReadStringArray(boardRow, "ExceptionTypes")
                        .Select(t => t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t)
                        .Where(t => t.Length >= 4));
                }
            }
        }

        return terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTerms).ToList();
    }

    /// <summary>"/rentals/searchrooms.json" -&gt; "searchrooms". Query strings and extensions are noise to a code index.</summary>
    private static string LastPathSegment(string path)
    {
        var value = path.Split('?')[0].TrimEnd('/');
        var slash = value.LastIndexOf('/');
        if (slash >= 0) value = value[(slash + 1)..];
        var dot = value.IndexOf('.');
        if (dot > 0) value = value[..dot];
        return value.Trim();
    }

    private static IEnumerable<string> ExtractIdentifiers(string question) =>
        System.Text.RegularExpressions.Regex
            .Matches(question ?? "", @"[A-Za-z][A-Za-z0-9_]{3,}")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private async Task<BlackboardEntry> SearchAsync(InspectorContext context, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (terms.Count == 0)
        {
            // Not "found nothing": there was nothing identifier-shaped in the case to look for, and
            // saying otherwise would read as evidence that the code is fine.
            return new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Empty,
                "the case carries no endpoint path or exception type specific enough to search the code for, so no search was run. " +
                "This says nothing about the code.",
                null);
        }

        var stopwatch = Stopwatch.StartNew();
        var hits = new List<CodeHit>();
        var errors = new List<string>();

        foreach (var term in terms)
        {
            CodeSearchResult result;
            try
            {
                result = await search.SearchAsync(term, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[InspectorMonkey] the code search threw for term {Term}.", term);
                result = CodeSearchResult.Failed(ex.Message);
            }

            if (result.Succeeded) hits.AddRange(result.Hits);
            else errors.Add($"\"{term}\": {result.Error}");
        }
        stopwatch.Stop();

        // Every term failing is an outage; some failing still leaves real evidence on the table.
        if (errors.Count == terms.Count)
        {
            var failure = new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Failed,
                $"the code search could not run, so nothing was checked: {InspectorJson.Truncate(string.Join("; ", errors), 300)}. " +
                "This is not evidence that the code is fine.",
                null);
            await recorder.RecordDeterministicTurnAsync(
                context.Case, AgentRole, AgentKind.Specialist, string.Join(", ", terms), failure.Summary, null,
                stopwatch.ElapsedMilliseconds, ct, AgentOutcomeStatus.Error, string.Join("; ", errors));
            return failure;
        }

        var distinct = hits
            .GroupBy(h => $"{h.Repository}|{h.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var locations = distinct.Select(h => new
        {
            repository = h.Repository,
            path = h.Path,
            fileName = h.FileName,
            // "Defined here" and "mentioned in a comment" are very different answers.
            isDefinition = h.IsDefinition,
            matchKinds = h.MatchKinds,
            matchCount = h.MatchCount,
            repositoryLooksNotLive = h.RepositoryLooksNotLive,
            url = h.Url,
        }).ToList();

        var findingsJson = JsonSerializer.Serialize(
            new
            {
                searchTerms = terms,
                // Stated in the payload the models read, so nobody concludes the file was examined.
                note = "Locations only - no file content was read, so nothing here shows what the code actually does.",
                partialFailures = errors,
                locations,
            },
            InspectorJson.Options);

        var notLive = distinct.Count(h => h.RepositoryLooksNotLive);
        var definitions = distinct.Count(h => h.IsDefinition);

        var entry = distinct.Count == 0
            ? new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Empty,
                $"the code index was searched for {Describe(terms)} and holds no match. " +
                "The search ran against a real index, so this is a result rather than a missing source.",
                findingsJson)
            : new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Contributed,
                $"{distinct.Count} location(s) found for {Describe(terms)}" +
                $"{(definitions > 0 ? $", {definitions} of them a definition" : ", none of them a definition")}" +
                $"{(notLive > 0 ? $"; {notLive} sit in repositories whose name says they are not live code" : "")}. " +
                "Locations only - no file content was read, so this shows WHERE the identifier appears, not what the code does.",
                findingsJson);

        await recorder.RecordDeterministicTurnAsync(
            context.Case, AgentRole, AgentKind.Specialist, string.Join(", ", terms), findingsJson, findingsJson,
            stopwatch.ElapsedMilliseconds, ct);
        return entry;
    }

    private static string Describe(IReadOnlyList<string> terms) => string.Join(", ", terms.Select(t => $"\"{t}\""));
}

/// <summary>Used when documentation search is not configured. Kept as a separate type so the reason is specific rather than generic.</summary>
public sealed class SourcelessDocsAgentSpecialist(string reason) : SourcelessSpecialist(InspectorAgent.Docs, reason);

/// <summary>
/// What the organisation already wrote down about this shape of problem: wiki pages and recorded
/// defects.
///
/// Spends no LLM call, for the same reason the MemoryAgent does not: the search terms are derivable
/// from the case, and asking a model to invent them would add a place for a hallucination to enter
/// between the case and the query while costing a call per investigation. The search terms are
/// built here, in code - the same rule the LogsAgent's tools follow.
///
/// Everything it returns is treated as untrusted text: a wiki page can be edited by anyone, and
/// this output is pasted into a prompt that runs unattended.
/// </summary>
public sealed class DocsAgentSpecialist(
    IDocumentationSearch search,
    InspectorRecorder recorder,
    ILogger<DocsAgentSpecialist> logger) : IInspectorSpecialist
{
    public string AgentRole => InspectorAgent.Docs;

    public bool HasSource => search.HasSource;

    public string NoSourceReason => search.NoSourceReason;

    public Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default) =>
        SearchAsync(context, BuildTerms(context), ct);

    public async Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default)
    {
        var terms = PromptSanitizer.Sanitize(question, 200, whenEmpty: BuildTerms(context));
        var entry = await SearchAsync(context, terms, ct);
        return entry.Summary;
    }

    /// <summary>
    /// What a person would type into the wiki search: the journey stage, the vertical, and the
    /// exception types the board actually recorded. Deliberately not the club or the host - those
    /// are the one thing documentation never mentions, and including them turns every search into
    /// zero hits.
    /// </summary>
    private static string BuildTerms(InspectorContext context)
    {
        var parts = new List<string>();
        var stage = context.Package.Stage;
        // "Unmapped operation" is the classifier admitting it has no name for the traffic; using it
        // as a search term looks for documentation about our own classifier, not about the problem.
        if (stage.Length > 0 && !stage.Equals("Unmapped operation", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(StripLeadingStageNumber(stage));
        }

        var vertical = context.Package.Vertical;
        if (vertical.Length > 0 && !vertical.Contains('/')) parts.Add(vertical);

        parts.AddRange(ReadBoardExceptionTypes(context.Package).Take(2));
        parts.Add(context.Package.Board == ScoutBoard.Exception ? "exception" : "latency");

        return PromptSanitizer.Sanitize(string.Join(' ', parts.Distinct(StringComparer.OrdinalIgnoreCase)), 200, whenEmpty: "latency");
    }

    /// <summary>Stage names arrive as "07 Booking"; the number is ours and means nothing to a wiki.</summary>
    private static string StripLeadingStageNumber(string stage)
    {
        var trimmed = stage.Trim();
        var space = trimmed.IndexOf(' ');
        return space > 0 && trimmed[..space].All(char.IsDigit) ? trimmed[(space + 1)..] : trimmed;
    }

    private static IReadOnlyList<string> ReadBoardExceptionTypes(InvestigationPackage package)
    {
        using var doc = InspectorJson.TryParseObject(package.DeterministicJson);
        if (doc is null) return [];
        if (!InspectorKqlGuards.TryGetProperty(doc.RootElement, "boardRow", out var boardRow)) return [];
        return InspectorJson.ReadStringArray(boardRow, "ExceptionTypes")
            // "System.TimeoutException" searched whole matches nothing; the leaf type is the term.
            .Select(t => t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t)
            .Where(t => t.Length > 0)
            .ToList();
    }

    private async Task<BlackboardEntry> SearchAsync(InspectorContext context, string terms, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        DocumentSearchResult result;
        try
        {
            result = await search.SearchAsync(terms, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] the documentation search threw.");
            result = DocumentSearchResult.Failed(ex.Message);
        }
        stopwatch.Stop();

        if (!result.Succeeded)
        {
            var failure = new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Failed,
                $"the documentation search could not run, so nothing was checked: {InspectorJson.Truncate(result.Error ?? "unknown error", 300)}. " +
                "This is not evidence that nothing is documented.",
                null);
            await recorder.RecordDeterministicTurnAsync(
                context.Case, AgentRole, AgentKind.Specialist, terms, failure.Summary, null,
                stopwatch.ElapsedMilliseconds, ct, AgentOutcomeStatus.Error, result.Error);
            return failure;
        }

        var documents = result.Hits.Select(h => new
        {
            kind = h.Kind,
            // The identifier a model is expected to cite, and the one grounding checks afterwards.
            reference = h.Reference,
            title = PromptSanitizer.Sanitize(h.Title, 300, whenEmpty: "(untitled)"),
            path = PromptSanitizer.Sanitize(h.Path, 400, whenEmpty: ""),
            status = h.Status,
            url = h.Url,
        }).ToList();

        var findingsJson = JsonSerializer.Serialize(
            new { searchTerms = terms, documents }, InspectorJson.Options);

        var entry = result.Hits.Count == 0
            ? new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Empty,
                $"the wiki and the recorded defects were searched for \"{terms}\" and hold nothing about this. " +
                "The search ran against a real index, so this is a result rather than a missing source.",
                findingsJson)
            : new BlackboardEntry(
                AgentRole,
                AgentSourceStatus.Contributed,
                $"{result.Hits.Count} document(s) found for \"{terms}\": titles and references are attached verbatim and nothing was summarised by a model. " +
                "A document describing this shape of problem is a lead, not a diagnosis of this case.",
                findingsJson);

        await recorder.RecordDeterministicTurnAsync(
            context.Case, AgentRole, AgentKind.Specialist, terms, findingsJson, findingsJson, stopwatch.ElapsedMilliseconds, ct);
        return entry;
    }
}
