namespace JourneyMonitor.Core.Domain;

/// <summary>
/// One unit of work handed from one monkey to another. Deliberately NOT specific to the Inspector:
/// the Scout is the first producer and the Inspector the first consumer, but the table is the
/// generic hand-off point every future monkey uses, so adding a producer is a new
/// <see cref="SourceMonkey"/> value rather than a new queue.
///
/// <see cref="DedupKey"/> carries a unique index, so re-enqueuing the same work is a no-op at the
/// database level and not only in C# - the same guarantee InvestigationPackage.CaseId already has.
/// </summary>
public sealed class AgentWorkItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Which monkey produced this work - see <see cref="MonkeyKey"/>.</summary>
    public required string SourceMonkey { get; init; }

    /// <summary>See <see cref="AgentWorkPayloadType"/>. Decides how <see cref="PayloadId"/> is resolved.</summary>
    public required string PayloadType { get; init; }

    /// <summary>Identifier of the row this work points at, as text so the queue never depends on a payload's key type.</summary>
    public required string PayloadId { get; init; }

    /// <summary>Natural key of the underlying work (for a Scout package, its CaseId). Uniquely indexed.</summary>
    public required string DedupKey { get; init; }

    /// <summary>Higher is claimed first; ties break on <see cref="EnqueuedAt"/>. Defaults to the board's PriorityRank.</summary>
    public int Priority { get; init; }

    /// <summary>See <see cref="AgentWorkItemStatus"/>.</summary>
    public string Status { get; set; } = AgentWorkItemStatus.Queued;

    public int Attempts { get; set; }

    /// <summary>Attempts after which the item is abandoned instead of retried. A poison item must stop costing LLM calls.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Until when the current claim is valid. A consumer that dies mid-investigation leaves the
    /// lease to expire and the item returns to the queue on its own - without this, a crashed
    /// worker would strand its item as permanently "leased", the same zombie-cycle bug the Scout
    /// already hit with never-completed runs.
    /// </summary>
    public DateTimeOffset? LeasedUntil { get; set; }

    /// <summary>Who holds the lease, for diagnosing a stuck item.</summary>
    public string? LeasedBy { get; set; }

    /// <summary>Why the last attempt failed. Kept across retries so a repeating failure is visible, not just the final one.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}

public static class AgentWorkItemStatus
{
    public const string Queued = "queued";

    /// <summary>Claimed by a consumer and being worked on right now.</summary>
    public const string Leased = "leased";

    public const string Done = "done";

    /// <summary>The attempt failed and the item is eligible to be claimed again.</summary>
    public const string Failed = "failed";

    /// <summary>Out of attempts. Never retried automatically - it needs a human to look at it.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>
    /// Taken out of the queue by a person, not by the system. Deliberately distinct from
    /// <see cref="Abandoned"/>: that one means the work defeated the Inspector after every retry,
    /// this one means nobody wanted it investigated. Counting a human's cleanup as a failure would
    /// make the agent look worse the more carefully it is operated.
    /// </summary>
    public const string Discarded = "discarded";
}

public static class AgentWorkPayloadType
{
    /// <summary><see cref="PayloadId"/> is an <see cref="InvestigationPackage"/> id.</summary>
    public const string InvestigationPackage = "investigation-package";
}

/// <summary>Backlog counters for the card - answers "is it falling behind?", which a heartbeat of past cycles cannot.</summary>
public sealed record AgentWorkQueueStats(
    int Queued,
    int Leased,
    int DoneToday,
    int FailedToday,
    int AbandonedTotal,
    DateTimeOffset? OldestQueuedAt)
{
    /// <summary>Items a person took out of the queue. Not part of the positional contract so older callers keep compiling.</summary>
    public int DiscardedTotal { get; init; }

    public static AgentWorkQueueStats Empty => new(0, 0, 0, 0, 0, null);
}

/// <summary>
/// One L3 investigation of one <see cref="InvestigationPackage"/>: the Inspector Monkey's unit of
/// work and its result. The package says what the Scout saw; this says what the L3 team concluded,
/// how long it took, what it cost, and - importantly - how much of what it cited could actually be
/// verified.
/// </summary>
public sealed class ForensicCase
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid InvestigationPackageId { get; init; }

    /// <summary>
    /// Which company this investigation belongs to. Copied from the package, never re-resolved: the
    /// Scout already decided it, and a second opinion here could disagree with the row the case is
    /// an investigation of. Defaults to "" so existing object initializers still compile, which is
    /// also what the pre-multi-company rows carry - unattributed, not "everyone's".
    /// </summary>
    public string ProfileKey { get; init; } = "";

    /// <summary>Copied from the package so a case is identifiable without a join.</summary>
    public required string CaseId { get; init; }

    public required string EntityKey { get; init; }

    /// <summary>Null when the club does not resolve - carried over verbatim, never guessed here either.</summary>
    public string? BrandName { get; init; }

    /// <summary>The club's own display name, from brand.ClubInventory. Null under the same conditions as <see cref="BrandName"/>.</summary>
    public string? ClubName { get; init; }

    /// <summary>
    /// The id every other filter in the portal keys on (<c>?club=</c>). Carried so a case can be
    /// found by the same club filter as journeys, exceptions and the overview - without it an
    /// investigation is unreachable from the filter an operator is already using.
    /// Present more often than <see cref="BrandName"/>: an entity key of "club:113" yields the id
    /// even when the brand catalogue has no row for it.
    /// </summary>
    public string? ClubId { get; init; }

    /// <summary>Cruise telemetry carries no VacationClubId - its partner id IS the club, so this is the filter key for cruises.</summary>
    public string? IcePartnerId { get; init; }

    /// <summary>See <see cref="Abstractions.ClubResolution"/>. "Resolved to a brand" and "identifiable" are different claims.</summary>
    public string? ClubResolvedBy { get; init; }

    public required string Vertical { get; init; }

    public required string Stage { get; init; }

    /// <summary>See <see cref="ForensicCaseStatus"/>.</summary>
    public string Status { get; set; } = ForensicCaseStatus.Running;

    /// <summary>See <see cref="ForensicCasePhase"/>. Null once finished, so an in-flight case is distinguishable from a finished one.</summary>
    public string? Phase { get; set; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public long DurationMs { get; set; }

    /// <summary>One plain-language line. Null while running, and when the case failed before producing one.</summary>
    public string? Verdict { get; set; }

    /// <summary>See <see cref="InspectorDecision"/> - produced by the decision service, not by a model.</summary>
    public string? Decision { get; set; }

    /// <summary>
    /// The probable root cause, as its own field rather than buried inside
    /// <see cref="SynthesisJson"/>. "Probable" is literal: it is carried only when something the
    /// synthesis cited was actually verified, and an escalated case can legitimately have one -
    /// naming the likely cause and being unable to close it are different statements.
    /// </summary>
    public string? ProbableRootCause { get; set; }

    public double Confidence { get; set; }

    /// <summary>
    /// What was missing to close the case ("no repository access", "vendor-side"). Turns a
    /// limitation into an actionable request instead of leaving the reader to infer it from a low
    /// confidence score.
    /// </summary>
    public string? BlockedBy { get; set; }

    /// <summary>The Planner's reconciled output, including how each conflict was resolved.</summary>
    public string? SynthesisJson { get; set; }

    /// <summary>Accumulated <see cref="BlackboardEntry"/> list - what each agent contributed, including the ones that had no source.</summary>
    public string? BlackboardJson { get; set; }

    /// <summary>Ordered human steps. Null when the evidence did not support any.</summary>
    public string? RunbookJson { get; set; }

    /// <summary>Every <see cref="CitedArtifact"/> and its verification outcome - the input to the card's "citations verified" figure.</summary>
    public string? GroundingJson { get; set; }

    /// <summary>Artifacts the synthesis cited. Zero is a valid, honest outcome.</summary>
    public int CitationsTotal { get; set; }

    /// <summary>
    /// How many of those exist in the real system. This is the one quality number that cannot be
    /// faked by a confident-sounding model, which is why it is on the card rather than buried.
    /// </summary>
    public int CitationsVerified { get; set; }

    public int LlmCalls { get; set; }

    public int TokensIn { get; set; }

    public int TokensOut { get; set; }

    /// <summary>See <see cref="Domain.AlertLevel"/>. Decided by a deterministic policy over the finished case, never by a model.</summary>
    public string AlertLevel { get; set; } = Domain.AlertLevel.Informational;

    /// <summary>
    /// The team accountable for the failing service, and how that was reached. Separate from
    /// <see cref="BrandName"/> on purpose: the club says who is AFFECTED, this says who FIXES IT.
    /// Null when nobody is recorded - never guessed from a naming convention.
    /// </summary>
    public string? OwnerTeam { get; set; }

    public string? OwnerPrimary { get; set; }

    public string? OwnerBackup { get; set; }

    /// <summary>See <see cref="Abstractions.ServiceOwnerResolution"/> - an exact match and a vertical-wide fallback are both "an owner", but only one means somebody accepted this service.</summary>
    public string? OwnerResolvedBy { get; set; }

    /// <summary>Why this case landed on that level. Populated for every level, including the downgrades - an alert that quietly is not one teaches nobody anything.</summary>
    public string? AlertReason { get; set; }

    /// <summary>Populated on failure - an investigation that could not run must be visible, never silently "no findings".</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// The severity ladder from the Red Alert definition (WI 239285, AC6). Three levels on purpose:
/// if everything can be a Red Alert, nothing is. A Red Alert is the only level that may page a
/// named responder, so it is deliberately the hardest to reach.
/// </summary>
public static class AlertLevel
{
    /// <summary>Customer-facing condition on a pilot journey, confirmed against evidence that verified. Pages the owner.</summary>
    public const string RedAlert = "red_alert";

    /// <summary>A real anomaly whose impact, classification or scope does not meet the Red Alert bar. Reviewed, not paged.</summary>
    public const string Amber = "amber";

    /// <summary>Investigated, nothing established. Visible in the portal and nowhere else.</summary>
    public const string Informational = "informational";
}

/// <summary>
/// The outcome of the Red Alert policy: the level, and the sentence explaining it. The reason is
/// not optional and is populated for downgrades too, because "why is this not a Red Alert" is the
/// question an operator actually asks, and it is also how the candidate rate gets measured before
/// the delivery commitment in AC6 becomes binding.
/// </summary>
public sealed record RedAlertEvaluation(string Level, string Reason)
{
    public bool IsRedAlert => Level == AlertLevel.RedAlert;
}

public static class ForensicCaseStatus
{
    public const string Running = "running";

    /// <summary>Real issue, needs someone else - the package of what is missing is on the case.</summary>
    public const string Escalated = "escalated";

    /// <summary>Root cause identified with verified citations and a proposed fix.</summary>
    public const string Resolved = "resolved";

    /// <summary>Investigated and there was no real problem (e.g. synthetic traffic). A result, not an absence of one.</summary>
    public const string ClosedNoIssue = "closed_no_issue";

    /// <summary>The investigation itself broke (tool timeout, LLM unavailable). Distinct from "found nothing".</summary>
    public const string Failed = "failed";
}

/// <summary>Steps of one investigation, in order, persisted as it advances so the UI shows what the team is doing rather than only that it is busy.</summary>
public static class ForensicCasePhase
{
    public const string PreProcess = "preprocess";
    public const string Scope = "scope";
    public const string Agents = "agents";
    public const string Debate = "debate";
    public const string Synthesis = "synthesis";
    public const string Grounding = "grounding";
    public const string Verdict = "verdict";

    /// <summary>In UI order - the card renders one step per entry.</summary>
    public static readonly string[] All = [PreProcess, Scope, Agents, Debate, Synthesis, Grounding, Verdict];

    public static int IndexOf(string? phase) => phase is null ? -1 : Array.IndexOf(All, phase);
}

/// <summary>The five specialists of the diagram. Adding one is an entry here plus its tool list - the orchestrator iterates this.</summary>
public static class InspectorAgent
{
    /// <summary>Writes NEW KQL against Application Insights and pivots on what it finds.</summary>
    public const string Logs = "LogsAgent";

    /// <summary>Repository -&gt; files and symbols involved. No source until a code index exists.</summary>
    public const string Code = "CodeAgent";

    /// <summary>The memory bank: have we solved this before?</summary>
    public const string Memory = "MemoryAgent";

    /// <summary>Runbooks, attachments, extra documentation. No source until a document store exists.</summary>
    public const string Docs = "DocsAgent";

    /// <summary>Reconstructs the user flow from stored journeys. See <see cref="AgentSourceStatus"/> - it reconstructs, it does not reproduce.</summary>
    public const string Repro = "ReproAgent";

    public static readonly string[] All = [Logs, Code, Memory, Docs, Repro];
}

/// <summary>Kinds of transcript author, so the UI can group a 24-turn transcript without parsing role names.</summary>
public static class AgentKind
{
    public const string Specialist = "specialist";
    public const string Moa = "moa";
    public const string Planner = "planner";
    public const string Grounding = "grounding";
}

/// <summary>
/// One agent's contribution to the shared evidence. <see cref="Status"/> exists because
/// "found nothing" and "has no source to look in" are opposite statements and reporting the second
/// as the first is a lie the reader cannot detect.
/// </summary>
public sealed record BlackboardEntry(
    string AgentRole,
    string Status,
    string Summary,
    string? FindingsJson)
{
    public static BlackboardEntry NoSource(string agentRole, string reason) =>
        new(agentRole, AgentSourceStatus.NoSource, reason, null);
}

public static class AgentSourceStatus
{
    /// <summary>Ran and produced evidence.</summary>
    public const string Contributed = "contributed";

    /// <summary>Ran against a real source and legitimately found nothing relevant.</summary>
    public const string Empty = "empty";

    /// <summary>Has no source wired up at all - it could not have found anything. Never collapse this into <see cref="Empty"/>.</summary>
    public const string NoSource = "no_source";

    /// <summary>Had a source but the attempt failed.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One turn of one agent: the exact prompt sent and the raw response before parsing. Same role
/// monitoring.LlmCallLog plays for the Doctor Monkey, except an investigation produces ~24 of these
/// across agents, rounds and help hops rather than one - which is why it is its own table and not a
/// column on the case.
/// </summary>
public sealed class AgentTurn
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid ForensicCaseId { get; init; }

    /// <summary>"LogsAgent", "moa:gpt-4o", "Planner" - the specific author, not the kind.</summary>
    public required string AgentRole { get; init; }

    /// <summary>See <see cref="AgentKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Debate round. 0 for everything that is not an MOA debate turn.</summary>
    public int Round { get; init; }

    /// <summary>Help hop within this agent's own loop - bounded by InspectorOptions.MaxHelpHops.</summary>
    public int Hop { get; init; }

    /// <summary>Null when the turn made no LLM call (a deterministic phase still records a turn so the transcript has no gaps).</summary>
    public string? ModelUsed { get; init; }

    /// <summary>Set when this turn exists because another agent asked a question - that is the only way to read the answer in context.</summary>
    public string? InputContext { get; init; }

    public required string PromptText { get; init; }

    /// <summary>Exactly what came back, before any JSON extraction. Empty when the provider returned nothing.</summary>
    public required string RawResponseText { get; init; }

    /// <summary>See <see cref="AgentOutcomeStatus"/>.</summary>
    public required string OutcomeStatus { get; init; }

    /// <summary>The parsed outcome, or null when the response could not be parsed - in which case the raw text above is all there is.</summary>
    public string? OutcomeJson { get; init; }

    public int TokensIn { get; init; }

    public int TokensOut { get; init; }

    public long DurationMs { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class AgentOutcomeStatus
{
    public const string Final = "final";

    /// <summary>The agent asked for help instead of answering - see the peer-help protocol.</summary>
    public const string NeedsHelp = "needs_help";

    /// <summary>
    /// Skipped before the call was made (prompt over this model's budget, model known not to work).
    /// Recorded with a visible reason rather than letting the agent disappear from the debate
    /// unexplained - a catalog flag claiming tool support does not guarantee the call works.
    /// </summary>
    public const string Excluded = "excluded";

    public const string Error = "error";
}

/// <summary>
/// One tool invocation inside an agent turn. Result text is capped before persisting: a single
/// unbounded KQL result can be larger than everything else in the transcript combined.
/// </summary>
public sealed class AgentToolCall
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid AgentTurnId { get; init; }

    public required string ToolName { get; init; }

    public required string ArgumentsJson { get; init; }

    /// <summary>Truncated with an explicit marker when it exceeds the cap - a partial result must never look complete.</summary>
    public required string ResultJson { get; init; }

    public bool Truncated { get; init; }

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public int ResultRowCount { get; init; }

    public long DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Something the synthesis pointed at as real - a file path, a table, an endpoint, an operation id.
/// Grounding checks each one against the actual system before the verdict ships.
/// </summary>
public sealed record CitedArtifact(
    string Kind,
    string Value,
    string CitedBy,
    string Verification,
    string? Note);

public static class ArtifactKind
{
    public const string FilePath = "file-path";
    public const string Endpoint = "endpoint";
    public const string OperationId = "operation-id";
    public const string ExceptionType = "exception-type";
    public const string KnowledgeChunk = "knowledge-chunk";

    /// <summary>A wiki page or a recorded defect the DocsAgent actually returned.</summary>
    public const string Document = "document";

    public const string Other = "other";
}

public static class ArtifactVerification
{
    /// <summary>Confirmed to exist in the real system.</summary>
    public const string Verified = "verified";

    /// <summary>Checked against a real index and genuinely absent - the model made it up.</summary>
    public const string NotFound = "not_found";

    /// <summary>
    /// No index exists to check it against. Distinct from <see cref="NotFound"/>: one says the
    /// claim is false, the other says we cannot tell. Treating them alike would either slander a
    /// correct answer or launder a fabricated one.
    /// </summary>
    public const string NotVerifiable = "not_verifiable";
}

/// <summary>What the decision service concluded. Deterministic rules over the grounded synthesis, never another model opinion.</summary>
public sealed record InspectorVerdict(
    string Decision,
    string Summary,
    double Confidence,
    string? BlockedBy,
    IReadOnlyList<string> Unknowns,
    string? RunbookJson)
{
    /// <summary>Not positional, so every existing construction site keeps compiling unchanged.</summary>
    public string? ProbableRootCause { get; init; }

    public static InspectorVerdict Inconclusive(string reason) =>
        new(InspectorDecision.Inconclusive, reason, 0, reason, [], null);
}

public static class InspectorDecision
{
    /// <summary>Real issue the Inspector cannot close by itself - <see cref="InspectorVerdict.BlockedBy"/> says what is missing.</summary>
    public const string Escalate = "escalate";

    /// <summary>Root cause identified with verified citations.</summary>
    public const string Resolve = "resolve";

    /// <summary>Investigated and there was no real problem.</summary>
    public const string CloseNoIssue = "close_no_issue";

    /// <summary>Not enough grounded evidence to say anything. The preferred honest outcome over a confident guess.</summary>
    public const string Inconclusive = "inconclusive";
}

/// <summary>
/// Cumulative proof of work for the card. CitationsVerified/CitationsTotal is the headline quality
/// figure on purpose: unlike a "success rate", it degrades visibly the moment the models start
/// citing things that do not exist.
/// </summary>
public sealed record InspectorStats(
    int Investigations,
    int Resolved,
    int Escalated,
    int ClosedNoIssue,
    int Failed,
    int CitationsTotal,
    int CitationsVerified,
    int LlmCalls,
    long TokensIn,
    long TokensOut,
    int InvestigationsToday,
    DateTimeOffset? FirstCaseAt,
    DateTimeOffset? LastCaseAt)
{
    /// <summary>0 when nothing has been cited yet - never 100%, which would read as perfect rather than unmeasured.</summary>
    public double CitationVerificationRate =>
        CitationsTotal == 0 ? 0 : (double)CitationsVerified / CitationsTotal;

    public static InspectorStats Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);
}
