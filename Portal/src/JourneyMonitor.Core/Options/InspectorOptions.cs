namespace JourneyMonitor.Core.Options;

/// <summary>
/// Inspector Monkey (L3 analysis phase) configuration. All values nullable so a missing setting
/// falls back to the documented default instead of silently using zero, matching
/// <see cref="ScoutOptions"/>.
///
/// The cost controls here are the real throttle on this agent: one investigation is roughly 20-25
/// LLM calls against much larger prompts than the Scout's, so what bounds spend is this
/// configuration and not how many packages the Scout happens to produce.
/// </summary>
public sealed class InspectorOptions
{
    public const string SectionName = "Inspector";

    /// <summary>
    /// Master switch for the consumer timer. Default true. When false the queue still fills - the
    /// Scout keeps handing off - so turning the Inspector back on picks up the backlog rather than
    /// losing it.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// How many investigations may run at once. Default 1, by cost rather than by capacity. Raising
    /// it multiplies spend linearly and is also the point at which the queue's atomic claim starts
    /// mattering in practice.
    /// </summary>
    public int? Concurrency { get; set; }

    /// <summary>
    /// Hard cap on investigations started per UTC day. Default 40. Deliberately below the Scout's
    /// observed ~84 packages/day so the first weeks run under a known ceiling; raise it with
    /// measured cost, the same way the Scout's priority rank was decided.
    /// </summary>
    public int? MaxInvestigationsPerDay { get; set; }

    /// <summary>
    /// How long a claimed item stays claimed. Default 15 minutes - comfortably longer than a normal
    /// investigation (~2-3 min) so a slow case is not stolen, short enough that a crashed consumer
    /// returns its work promptly.
    /// </summary>
    public int? LeaseMinutes { get; set; }

    /// <summary>Attempts before an item is abandoned instead of retried. Default 3. A poison package must stop costing LLM calls.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>
    /// The MOA roster, comma-separated. Default "claude-sonnet-5.5,gpt-4o,claude-opus-5.5".
    /// Re-probed live 2026-10-30: the Copilot catalogue no longer lists claude-sonnet-5 or
    /// claude-opus-5 - both now answer HTTP 400 "The requested model is not supported"
    /// (model_not_supported), which took the Planner and two thirds of the roster down at once.
    /// Their successors claude-sonnet-5.5 and claude-opus-5.5 were verified against
    /// /chat/completions and are the defaults now. Still true from the 2026-10-08 probe: the whole
    /// grok family, mai-code-1.1-flash and gpt-5.3-codex appear in the catalogue but are rejected
    /// by /chat/completions, so only Anthropic and OpenAI are actually reachable. The third slot is
    /// therefore a second, larger Anthropic model rather than a third vendor - real weight
    /// diversity, but two families rather than three. Being catalogue-listed is not proof a model
    /// is callable; probe /chat/completions before changing these.
    /// With a single entry the debate degrades to one opinion rather than breaking.
    /// </summary>
    public string? DebateModels { get; set; }

    /// <summary>
    /// Debate rounds after the independent round 0. Default 2. Round 0 always runs; 0 here means
    /// "no debate", which is the honest degraded mode when only one model is configured.
    /// </summary>
    public int? DebateRounds { get; set; }

    /// <summary>
    /// Maximum peer-help hops per agent. Default 3. This is the only thing preventing an
    /// "A asks B, B asks A" loop from running until the budget is gone.
    /// </summary>
    public int? MaxHelpHops { get; set; }

    /// <summary>Maximum tool-calling rounds inside one specialist. Default 5 - an agent that still has no answer by then is not converging.</summary>
    public int? MaxToolRounds { get; set; }

    /// <summary>
    /// Skip the debate when fewer than this many specialists actually contributed evidence.
    /// Default 0, which never skips.
    ///
    /// The debate is 64% of this system's token spend, and rounds 1..N are 43% of the total on
    /// their own. Asking three models to reconcile an empty blackboard is the case the synthesis
    /// prompt already warns about - "three models reasoning without information is not a strong
    /// validation" - except it costs nine completions to arrive there.
    ///
    /// Default 0 on purpose: measured over 32 real cases, the number of contributing agents
    /// correlates with finding a root cause but does not separate cleanly (3 contributors still
    /// produced one 20% of the time), so a non-zero value here is a budget decision, not a
    /// conclusion the data supports. Round 0 still runs when this triggers, so the synthesis is
    /// never left with nothing.
    /// </summary>
    public int? SkipDebateWhenContributorsBelow { get; set; }

    /// <summary>Token ceiling per completion. Default 1500.</summary>
    public int? MaxTokensPerCall { get; set; }

    /// <summary>
    /// Token ceiling for the Planner's final synthesis. Default 3000 - double a normal turn, and
    /// separate from it on purpose.
    ///
    /// Measured: with the CodeAgent and DocsAgent wired up the blackboard carries ten documents and
    /// fourteen source locations, and the synthesis hit 1500 output tokens exactly and was cut
    /// mid-sentence. An unparseable synthesis costs the whole investigation - thirteen LLM calls -
    /// and lands as "inconclusive", so this is the cheapest call in the run to be generous with.
    /// </summary>
    public int? MaxSynthesisTokens { get; set; }

    /// <summary>
    /// Model used by the Planner to route and synthesise. Default "claude-sonnet-5.5". Kept separate
    /// from the roster: the synthesiser is a role, not a debate participant.
    /// </summary>
    public string? PlannerModel { get; set; }

    /// <summary>
    /// Rows a single agent tool may return. Default 200. A model-authored query against production
    /// telemetry must be bounded in code - the prompt is not a boundary.
    /// </summary>
    public int? MaxToolRows { get; set; }

    /// <summary>
    /// Characters of a tool result handed to the model and persisted in the transcript. Default
    /// 48000. Over it the envelope first shortens verbose fields and only then drops rows, marking
    /// both in the payload.
    ///
    /// Derived, not chosen. Measured on a live drill-down package: 34 rows, 2373 characters per
    /// serialized row, of which StackTrace alone averaged 850 (the KQL already caps StackTrace at
    /// 2000 and Message at 600). At the previous 8000 the envelope could keep 3 of those 34 rows,
    /// and every investigation escalated at 0.10-0.25 confidence with blockers like "only 2 of 166
    /// exception rows were sampled" - the models were reasoning about a 9% sample and said so.
    /// With the verbose fields squeezed to their 240-character floor a row costs
    /// 2373 - (850 - 240) - (600 - 240) = 1403 characters, so the measured package needs
    /// 34 x 1403 = 47702, and 48000 leaves room for the envelope's own note. That is ~12000 tokens
    /// of prompt per tool call: the price of the breadth a diagnosis is actually made of.
    ///
    /// What it does NOT buy: a full 200-row package still does not fit (200 x 1403 = 280600),
    /// because ~900 characters of every row are identifiers the envelope must not shorten - they
    /// are what citations get verified against. Such a package now returns ~34 complete rows and
    /// says they are 34 of 200, instead of returning 3 and saying the same thing.
    /// </summary>
    public int? MaxToolResultChars { get; set; }

    /// <summary>
    /// Furthest back a model-authored KQL query may look. Default 168 (7 days) - enough for the
    /// club baseline the LogsAgent needs, bounded so a generated query cannot scan months of
    /// production telemetry across three resources.
    /// </summary>
    public int? MaxQueryLookbackHours { get; set; }

    /// <summary>
    /// Minimum confidence for a <c>resolve</c> verdict. Default 0.75. Below it the case escalates:
    /// the decision service prefers an honest escalation over a weakly-supported resolution.
    /// </summary>
    public double? MinConfidenceToResolve { get; set; }

    /// <summary>
    /// Share of cited artifacts that must verify for a <c>resolve</c> verdict. Default 1.0 - every
    /// cited artifact. A proposed fix that points at something nobody can confirm exists is worse
    /// than no fix at all.
    /// </summary>
    public double? MinVerifiedCitationRatioToResolve { get; set; }

    /// <summary>Whether a finished investigation notifies. Default true; which channels actually fire is the notification settings' job.</summary>
    public bool? NotifyOnCompletion { get; set; }

    public bool ResolvedEnabled => Enabled ?? true;

    public int ResolvedConcurrency => Concurrency is > 0 ? Concurrency.Value : 1;

    public int ResolvedMaxInvestigationsPerDay => MaxInvestigationsPerDay is > 0 ? MaxInvestigationsPerDay.Value : 40;

    public int ResolvedLeaseMinutes => LeaseMinutes is > 0 ? LeaseMinutes.Value : 15;

    public int ResolvedMaxAttempts => MaxAttempts is > 0 ? MaxAttempts.Value : 3;

    public IReadOnlyList<string> ResolvedDebateModels =>
        string.IsNullOrWhiteSpace(DebateModels)
            ? ["claude-sonnet-5.5", "gpt-4o", "claude-opus-5.5"]
            : DebateModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int ResolvedDebateRounds => DebateRounds is >= 0 ? DebateRounds.Value : 2;

    public int ResolvedMaxHelpHops => MaxHelpHops is > 0 ? MaxHelpHops.Value : 3;

    public int ResolvedMaxToolRounds => MaxToolRounds is > 0 ? MaxToolRounds.Value : 5;

    public int ResolvedSkipDebateWhenContributorsBelow => SkipDebateWhenContributorsBelow is > 0 ? SkipDebateWhenContributorsBelow.Value : 0;

    public int ResolvedMaxTokensPerCall => MaxTokensPerCall is > 0 ? MaxTokensPerCall.Value : 1500;

    public int ResolvedMaxSynthesisTokens => MaxSynthesisTokens is > 0 ? MaxSynthesisTokens.Value : 3000;

    public string ResolvedPlannerModel => string.IsNullOrWhiteSpace(PlannerModel) ? "claude-sonnet-5.5" : PlannerModel.Trim();

    public int ResolvedMaxToolRows => MaxToolRows is > 0 ? MaxToolRows.Value : 200;

    public int ResolvedMaxToolResultChars => MaxToolResultChars is > 0 ? MaxToolResultChars.Value : 48000;

    public int ResolvedMaxQueryLookbackHours => MaxQueryLookbackHours is > 0 ? MaxQueryLookbackHours.Value : 168;

    public double ResolvedMinConfidenceToResolve => MinConfidenceToResolve is > 0 ? MinConfidenceToResolve.Value : 0.75;

    public double ResolvedMinVerifiedCitationRatioToResolve =>
        MinVerifiedCitationRatioToResolve is >= 0 ? MinVerifiedCitationRatioToResolve.Value : 1.0;

    public bool ResolvedNotifyOnCompletion => NotifyOnCompletion ?? true;
}
