using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

// ---------------------------------------------------------------------------------------------
// Queue - generic on purpose. See AgentWorkItem.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Hand-off point between monkeys. A producer enqueues; a consumer claims a lease, works, and then
/// reports the outcome.
///
/// The claim must be atomic at the database level, not merely "select then update" in C#: two
/// instances polling the same table would otherwise both pick the same item and pay for the same
/// investigation twice.
/// </summary>
public interface IAgentWorkQueue
{
    /// <summary>
    /// Adds work unless <see cref="AgentWorkItem.DedupKey"/> is already present. Returns false when
    /// it was a duplicate, so a producer can count what it skipped rather than assume it enqueued.
    /// </summary>
    Task<bool> EnqueueAsync(AgentWorkItem item, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims the highest-priority eligible item for <paramref name="leaseDuration"/>, or
    /// null when there is nothing to do. Items whose lease expired are eligible again - that is how
    /// a crashed consumer releases its work without anyone intervening.
    /// </summary>
    Task<AgentWorkItem?> TryLeaseAsync(string payloadType, string leasedBy, TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>
    /// Marks the work done. <paramref name="leasedBy"/> fences the write: a consumer whose lease
    /// already expired has had the item re-claimed by someone else, and must not be able to come
    /// back and overwrite the state of whoever replaced it.
    /// </summary>
    Task CompleteAsync(Guid itemId, string leasedBy, CancellationToken ct = default);

    /// <summary>
    /// Releases the lease and records why. The item returns to the queue unless it has run out of
    /// attempts, in which case it is abandoned rather than retried forever. Fenced by
    /// <paramref name="leasedBy"/> for the same reason as <see cref="CompleteAsync"/>.
    /// </summary>
    Task FailAsync(Guid itemId, string leasedBy, string error, CancellationToken ct = default);

    Task<IReadOnlyList<AgentWorkItem>> GetQueuedAsync(string payloadType, int top, CancellationToken ct = default);

    Task<AgentWorkQueueStats> GetStatsAsync(string payloadType, CancellationToken ct = default);

    /// <summary>
    /// Takes waiting work out of the queue on a person's instruction and returns how many items it
    /// actually moved. Never deletes: the row survives as <see cref="AgentWorkItemStatus.Discarded"/>
    /// carrying who asked and why, because a queue that silently shrinks is indistinguishable from
    /// one that silently lost work.
    ///
    /// Only claimable items are affected. An item with a live lease is being investigated right now,
    /// and discarding it would not stop the worker - it would only make the UI disagree with what is
    /// actually running.
    /// </summary>
    /// <param name="itemIds">The items to discard, or null/empty to discard everything waiting for this payload type.</param>
    Task<int> DiscardQueuedAsync(
        string payloadType,
        IReadOnlyCollection<Guid>? itemIds,
        string reason,
        string discardedBy,
        CancellationToken ct = default);
}

// ---------------------------------------------------------------------------------------------
// Tool-calling LLM access
// ---------------------------------------------------------------------------------------------

public static class LlmRole
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";

    /// <summary>A tool result being fed back; requires <see cref="LlmMessage.ToolCallId"/>.</summary>
    public const string Tool = "tool";
}

/// <summary>One message of a chat exchange, in the OpenAI-shaped form the Copilot endpoint accepts.</summary>
public sealed record LlmMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);

/// <summary>A tool offered to the model. The schema is passed through verbatim, so it must be valid JSON Schema.</summary>
public sealed record LlmToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>A tool the model asked to run. <see cref="ArgumentsJson"/> is model-generated and must be validated before use.</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// Result of one tool-capable completion. Token counts come from the provider's own <c>usage</c>
/// block rather than being estimated from character counts, which is the reason this path uses a
/// non-streaming request.
/// </summary>
public sealed record LlmCompletion(
    string Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string FinishReason,
    int TokensIn,
    int TokensOut,
    string ModelUsed,
    string? Error)
{
    public bool RequestedTools => ToolCalls.Count > 0;

    public bool IsAvailable => Error is null;

    /// <summary>Never throws its way out to a caller - an unreachable provider is a result the orchestrator records and moves past.</summary>
    public static LlmCompletion Unavailable(string model, string error) =>
        new("", [], "error", 0, 0, model, error);
}

/// <summary>
/// Chat completion with tool-calling. Deliberately separate from <see cref="ILlmClient"/> so the
/// Doctor Monkey's single-prompt contract keeps working untouched; the same client implements both.
/// </summary>
public interface IToolCallingLlmClient
{
    /// <summary>
    /// One turn. <paramref name="model"/> is explicit because the Inspector runs a roster of models
    /// against the same evidence, so it cannot rely on a single configured default.
    /// Never throws: a transport/provider failure comes back as <see cref="LlmCompletion.Unavailable"/>.
    /// </summary>
    Task<LlmCompletion> CompleteAsync(
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct = default);
}

// ---------------------------------------------------------------------------------------------
// Specialists and their tools
// ---------------------------------------------------------------------------------------------

/// <summary>Everything a specialist or tool needs about the case it is working on, assembled once per investigation.</summary>
public sealed record InspectorContext(
    ForensicCase Case,
    InvestigationPackage Package,
    ClubIdentity Club,
    IReadOnlyList<BlackboardEntry> Blackboard);

public sealed record AgentToolResult(bool Success, string ResultJson, int RowCount, bool Truncated, string? Error)
{
    public static AgentToolResult Failed(string error) => new(false, "[]", 0, false, error);
}

/// <summary>
/// One capability a specialist can invoke. Tools are owned by exactly one agent: handing every tool
/// to every agent is what the specialisation is meant to avoid.
///
/// Implementations are a trust boundary - <paramref name="argumentsJson"/> is written by a model,
/// so bounds (time ranges, row caps, allowed tables) belong in the tool, never in the prompt.
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the arguments, passed to the model verbatim.</summary>
    string ParametersJsonSchema { get; }

    /// <summary>Which <see cref="InspectorAgent"/> is allowed to call this.</summary>
    string OwnerAgent { get; }

    Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default);
}

/// <summary>
/// One domain specialist. <see cref="HasSource"/> is part of the contract because three of the five
/// agents in the design have no data source wired up yet: they must report that explicitly and cost
/// nothing, instead of making an LLM call that can only ever answer "I found nothing" - which reads
/// as evidence of absence and is not.
/// </summary>
public interface IInspectorSpecialist
{
    /// <summary>See <see cref="InspectorAgent"/>.</summary>
    string AgentRole { get; }

    bool HasSource { get; }

    /// <summary>Why there is no source, in words a reader can act on. Only meaningful when <see cref="HasSource"/> is false.</summary>
    string NoSourceReason { get; }

    /// <summary>Initial evidence collection for this agent's domain.</summary>
    Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default);

    /// <summary>Answers one targeted question routed by the Planner on behalf of another agent (the peer-help path).</summary>
    Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------------------------
// Grounding and the decision service
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Verifies every artifact the synthesis cited against the real system before the verdict ships.
/// Must distinguish "checked and absent" from "no index exists to check against" - see
/// <see cref="ArtifactVerification"/>.
/// </summary>
public interface IGroundingService
{
    Task<IReadOnlyList<CitedArtifact>> VerifyAsync(InspectorContext context, string synthesisJson, CancellationToken ct = default);
}

/// <summary>
/// Turns a grounded synthesis into a decision. Deliberately deterministic and separate from the
/// Planner's prompt: resolve-vs-escalate is a policy that has to be auditable, testable and
/// changeable without touching a prompt, and it must not be yet another model opinion.
/// </summary>
public interface IVerdictService
{
    InspectorVerdict Decide(InspectorContext context, string synthesisJson, IReadOnlyList<CitedArtifact> artifacts);
}

/// <summary>
/// Decides the severity level of a finished investigation (WI 239285, AC6). Separate from
/// <see cref="IVerdictService"/> because they answer different questions: the verdict is "what did
/// we conclude", the level is "who does this reach, and how fast". Deterministic for the same
/// reason - who gets paged must be auditable and must not change because a model phrased something
/// more forcefully this week.
/// </summary>
public interface IRedAlertPolicy
{
    RedAlertEvaluation Evaluate(ForensicCase forensicCase);
}

// ---------------------------------------------------------------------------------------------
// Notifications - generic over what produced them
// ---------------------------------------------------------------------------------------------

public static class NotificationSourceType
{
    public const string Finding = "finding";
    public const string ForensicCase = "forensic-case";
}

public static class NotificationSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

public sealed record NotificationLink(string Label, string Url);

/// <summary>
/// What to notify about, independent of what produced it. The previous contract took a
/// <see cref="Finding"/> directly, which made it unusable for anything that is not a Finding.
/// </summary>
public sealed record NotificationRequest(
    string SourceType,
    Guid SourceId,
    string Severity,
    string Subject,
    string Body,
    IReadOnlyList<NotificationLink> Links);

/// <summary>
/// One delivery channel (toast / Teams / email). Must never throw: a channel that is disabled, not
/// configured, or failing is logged and swallowed so one broken channel cannot suppress the others.
/// </summary>
public interface INotificationChannel
{
    /// <summary>See <see cref="NotificationChannel"/>.</summary>
    string Channel { get; }

    /// <summary>Provider is the concrete transport actually used (e.g. which email sender), null when nothing was sent.</summary>
    Task<(bool Delivered, string? Provider, string? Error)> SendAsync(NotificationRequest request, CancellationToken ct = default);
}

/// <summary>Fans a request out to every registered channel and records each attempt. Returns how many actually delivered.</summary>
public interface INotificationService
{
    Task<int> NotifyAsync(NotificationRequest request, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------------------------

public interface IForensicCaseRepository
{
    Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default);

    Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default);

    /// <summary>Cheap mid-investigation write so an in-flight case is visible. Best-effort by contract - failing to record progress must never abort the case it describes.</summary>
    Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default);

    Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default);

    /// <summary>
    /// Most recent cases, optionally scoped the way the rest of the portal scopes everything:
    /// by vertical and by the club id (or partner id) the Club dropdown sends, plus the company
    /// profile the case was investigated under. Null means no filter.
    /// Filtering in the query rather than after it, so "top N" still returns N matching cases.
    /// </summary>
    Task<IReadOnlyList<ForensicCase>> GetRecentAsync(int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default);

    /// <summary><paramref name="profileKey"/> null means every company, matching the unscoped behaviour the monkey card has always had.</summary>
    Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default);
}

/// <summary>
/// The audit trail of one investigation: who said what, in which round, with which evidence. Writes
/// are best-effort - losing a transcript row must never cost the investigation it documents.
/// </summary>
public interface IAgentTranscriptRepository
{
    Task<AgentTurn> AddTurnAsync(AgentTurn turn, CancellationToken ct = default);

    Task AddToolCallAsync(AgentToolCall toolCall, CancellationToken ct = default);

    Task<IReadOnlyList<AgentTurn>> GetTurnsAsync(Guid forensicCaseId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentToolCall>> GetToolCallsAsync(Guid forensicCaseId, CancellationToken ct = default);
}
