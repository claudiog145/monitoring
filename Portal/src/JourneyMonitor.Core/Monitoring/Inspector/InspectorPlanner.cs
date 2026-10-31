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
/// One debate participant's position after a round. <see cref="Available"/> is false when the
/// model never produced a usable answer - an unreachable provider, an unparseable response, or a
/// model that spent its whole help budget asking questions. <see cref="Reason"/> then says which,
/// because a participant that quietly disappears from a three-model debate turns it into a
/// two-model debate nobody was told about.
/// </summary>
public sealed record MoaProposal(string Model, bool Available, string Json, string? Reason)
{
    public static MoaProposal Excluded(string model, string reason) => new(model, false, "", reason);
}

/// <summary>The Planner's reconciled output. Unavailable means there is no investigation to ship, not an investigation with an empty answer.</summary>
public sealed record InspectorSynthesis(bool Available, string Json, string? Error)
{
    public static InspectorSynthesis Unavailable(string error) => new(false, "", error);
}

/// <summary>
/// The Planner: collects the specialists, runs the MOA debate, routes peer questions, and
/// reconciles everything into one synthesis.
///
/// Three things here are load-bearing and easy to "simplify" into bugs:
/// - Sourceless specialists never reach a model. They answer <see cref="BlackboardEntry.NoSource"/>
///   for free, because a model asked to investigate without a source can only answer "nothing
///   found", which a reader cannot tell apart from a real negative.
/// - The peer-help hop count is bounded before the first commit, not after the first runaway bill:
///   "A asks B, B asks A" is the natural failure mode of this protocol, not an exotic one.
/// - A single configured model degrades to "no debate" rather than to an error. Rounds 1..N only
///   mean something when a model can see somebody else's proposal.
/// </summary>
public sealed class InspectorPlanner(
    IEnumerable<IInspectorSpecialist> specialists,
    IToolCallingLlmClient llm,
    InspectorRecorder recorder,
    IAgentBudgetResolver budgets,
    IOptions<InspectorOptions> options,
    ILogger<InspectorPlanner> logger,
    // Trailing and optional so the composition root and the existing tests keep constructing this
    // the way they already do. Null means the neutral vocabulary, never a failed investigation.
    ICompanyAdapterProvider? companyAdapters = null)
{
    private const string PlannerRole = "Planner";

    private readonly List<IInspectorSpecialist> _specialists = specialists.ToList();

    /// <summary>
    /// The Scope step, as a sentence rather than a label: which agents have a source for this case
    /// and which do not. Deterministic - deciding what can be looked at is not a model's job.
    /// </summary>
    public string DescribeScope()
    {
        var sb = new StringBuilder();
        foreach (var specialist in _specialists.OrderBy(s => s.AgentRole, StringComparer.Ordinal))
        {
            sb.AppendLine(specialist.HasSource
                ? $"- {specialist.AgentRole}: has a source and will be asked to gather."
                : $"- {specialist.AgentRole}: NO SOURCE - {specialist.NoSourceReason}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Every specialist in parallel. A specialist that throws is recorded as
    /// <see cref="AgentSourceStatus.Failed"/> and the other four carry on - one broken source must
    /// not be able to take the investigation with it.
    /// </summary>
    public async Task<IReadOnlyList<BlackboardEntry>> GatherAsync(InspectorContext context, CancellationToken ct = default)
    {
        var tasks = _specialists.Select(async specialist =>
        {
            if (!specialist.HasSource)
            {
                return BlackboardEntry.NoSource(specialist.AgentRole, specialist.NoSourceReason);
            }

            try
            {
                return await specialist.GatherAsync(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[InspectorMonkey] specialist {Agent} failed while gathering.", specialist.AgentRole);
                return new BlackboardEntry(
                    specialist.AgentRole,
                    AgentSourceStatus.Failed,
                    $"this agent failed while gathering: {InspectorJson.Truncate(ex.Message, 300)}",
                    null);
            }
        });

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Round 0 is independent - every model sees the same evidence and nobody else's opinion, so
    /// agreement there is not an artefact of anchoring. Rounds 1..N show each model its peers'
    /// latest positions.
    /// </summary>
    public async Task<IReadOnlyList<MoaProposal>> DebateAsync(InspectorContext context, CancellationToken ct = default)
    {
        // Resolved once per debate, not per call: the dials must not change underneath a running
        // investigation, or round 2 could be judged against a roster round 0 never saw.
        var budget = await budgets.ResolveInspectorAsync(ct);
        var models = budget.DebateModels;
        var vocabulary = await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct);
        var brief = InspectorCaseBrief.Build(context.Package, context.Club, vocabulary);
        var evidence = RenderBlackboard(context.Blackboard);

        var proposals = new List<MoaProposal>();
        foreach (var model in models)
        {
            proposals.Add(await AskModelAsync(context, brief, evidence, model, peers: null, round: 0, budget, ct));
        }

        if (models.Count < 2)
        {
            logger.LogInformation(
                "[InspectorMonkey] only {Count} debate model configured - running without debate rounds rather than debating with itself.",
                models.Count);
            return proposals;
        }

        // Round 0 has already run, so the synthesis still has three independent readings. What is
        // skipped is only the part where the models read each other - which on an empty blackboard
        // can only propagate the same blind spot, at 43% of this system's token spend.
        var contributors = context.Blackboard.Count(e => e.Status == AgentSourceStatus.Contributed);
        var floor = budget.SkipDebateWhenContributorsBelow;
        if (floor > 0 && contributors < floor)
        {
            logger.LogInformation(
                "[InspectorMonkey] {Contributors} of {Agents} agents contributed evidence (floor {Floor}) - skipping {Rounds} debate round(s); the independent round still stands.",
                contributors, context.Blackboard.Count, floor, budget.DebateRounds);
            return proposals;
        }

        for (var round = 1; round <= budget.DebateRounds; round++)
        {
            var previous = proposals.ToList();
            var next = new List<MoaProposal>();
            foreach (var model in models)
            {
                var peers = RenderPeers(previous, model);
                next.Add(await AskModelAsync(context, brief, evidence, model, peers, round, budget, ct));
            }
            proposals = next;
        }

        return proposals;
    }

    /// <summary>
    /// Reconciles the debate against the specialists' evidence. The two rules spelled out in the
    /// prompt are the ones this design exists for: concrete evidence outranks unanimous inference,
    /// and agreement between models that share the same blind spot is not corroboration.
    /// </summary>
    public async Task<InspectorSynthesis> SynthesizeAsync(
        InspectorContext context,
        IReadOnlyList<MoaProposal> proposals,
        CancellationToken ct = default)
    {
        var limits = options.Value;
        var vocabulary = await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct);
        var brief = InspectorCaseBrief.Build(context.Package, context.Club, vocabulary);

        var messages = new List<LlmMessage>
        {
            new(LlmRole.System,
                "You are the Planner of an L3 reliability investigation team. You reconcile what the specialists measured " +
                "with what the debating models believe, and you ship one answer that a human engineer can act on or escalate."),
            new(LlmRole.User, $$"""
                {{brief.PromptBlock}}

                WHAT THE SPECIALISTS CONTRIBUTED:
                {{RenderBlackboard(context.Blackboard)}}

                WHAT THE DEBATING MODELS CONCLUDED:
                {{RenderProposals(proposals)}}

                Reconcile all of it and answer with STRICT JSON ONLY, no markdown fences, matching exactly:
                {"conclusion":"root_cause|no_issue|insufficient_evidence","summary":"one line a human can act on","rootCause":"string or null","confidence":0.0,"citations":[{"kind":"operation-id|endpoint|exception-type|knowledge-chunk|document|file-path|other","value":"the exact identifier","citedBy":"which agent or model produced it"}],"conflicts":[{"between":"...","resolution":"..."}],"agreementAssessment":"string","unknowns":["..."],"blockedBy":"what is missing to close this, or null","runbook":[{"step":1,"action":"...","why":"..."}]}

                Mandatory rules:
                - A specialist's concrete evidence beats a model's inference, even when every model agrees otherwise - point out the contradiction explicitly in "conflicts".
                - Assess WHETHER the agreement came from independent evidence or from sharing the same limitation; 3 of 3 models reasoning without information is not a strong validation. Say which one it was in "agreementAssessment".
                - An agent reported as "no_source" could not have found anything. Never read its silence as evidence that nothing is there.
                - A document the DocsAgent found describes a SHAPE of problem; it is a lead, not proof that it is what happened in this case. Be explicit about which of the two you are claiming.
                - Every entry in "citations" must be an identifier that appeared in the evidence above. Each one is checked against the real system afterwards, and an invented one is visible to everyone who reads this case.
                - If the evidence does not support a root cause, use "insufficient_evidence" with a LOW confidence and populate "blockedBy". That is the preferred answer over a confident guess.
                - Only propose a "runbook" when the cited evidence supports the steps. Otherwise return an empty array.
                """),
        };

        var (completion, _) = await recorder.CallAsync(
            context.Case, llm, limits.ResolvedPlannerModel, PlannerRole, AgentKind.Planner,
            messages, [], limits.ResolvedMaxSynthesisTokens, ct);

        if (!completion.IsAvailable)
        {
            // Without a synthesis there is no investigation to ship - the case fails visibly
            // instead of being closed as "nothing found", which is a different statement.
            return InspectorSynthesis.Unavailable($"the Planner model ({limits.ResolvedPlannerModel}) was unavailable: {completion.Error}");
        }

        var json = InspectorJson.ExtractObject(completion.Content);
        if (json is null)
        {
            // Truncated and malformed look identical from here, and the difference is the whole
            // diagnosis: one needs a bigger budget, the other needs a better prompt. Saying only
            // "not valid JSON" sends the reader to the wrong one.
            return InspectorSynthesis.Unavailable(WasTruncated(completion)
                ? $"the Planner's answer was cut off at the {limits.ResolvedMaxSynthesisTokens}-token ceiling, so the JSON is incomplete. "
                  + "The investigation ran; only the write-up did not fit. Raise Inspector:MaxSynthesisTokens."
                : "the Planner's response was not valid JSON, so nothing in it can be trusted as a conclusion.");
        }

        return new InspectorSynthesis(true, json, null);
    }

    /// <summary>"length" is the OpenAI-shaped signal for hitting the ceiling; providers differ, so an unknown reason is not reported as truncation.</summary>
    private static bool WasTruncated(LlmCompletion completion) =>
        string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One model's position, including however many peer-help hops it needs - bounded by
    /// <see cref="InspectorOptions.MaxHelpHops"/>. The last hop tells the model help has run out
    /// and that "needs_help" is no longer a valid answer; a model that asks anyway is excluded with
    /// a visible reason rather than left to loop.
    /// </summary>
    private async Task<MoaProposal> AskModelAsync(
        InspectorContext context,
        InspectorCaseBrief brief,
        string evidence,
        string model,
        string? peers,
        int round,
        ResolvedInspectorBudget budget,
        CancellationToken ct)
    {
        var limits = options.Value;
        var maxHops = budget.MaxHelpHops;

        var messages = new List<LlmMessage>
        {
            new(LlmRole.System,
                "You are one of several independent models reviewing the same L3 reliability case. You reason only from the " +
                "evidence given to you. You never invent an identifier, and you never treat an agent that has no source as " +
                "evidence that nothing is there."),
            new(LlmRole.User, BuildDebatePrompt(brief, evidence, peers, round, maxHops)),
        };

        for (var hop = 0; hop <= maxHops; hop++)
        {
            var lastHop = hop == maxHops;
            if (lastHop && hop > 0)
            {
                messages.Add(new LlmMessage(LlmRole.User,
                    $"You have used all {maxHops} of your help requests for this round. No further help is available. " +
                    "\"needs_help\" is no longer a valid status - answer now with what you have, and list what you still " +
                    "could not determine in \"unknowns\"."));
            }

            var (completion, _) = await recorder.CallAsync(
                context.Case, llm, model, $"moa:{model}", AgentKind.Moa,
                messages, [], limits.ResolvedMaxTokensPerCall, ct,
                round, hop, peers is null ? null : "peer proposals from the previous round",
                classify: c => ClassifyDebateTurn(c));

            if (!completion.IsAvailable)
            {
                logger.LogWarning("[InspectorMonkey] model {Model} excluded from round {Round}: {Error}", model, round, completion.Error);
                return MoaProposal.Excluded(model, completion.Error ?? "the provider returned no answer and no error.");
            }

            using var doc = InspectorJson.TryParseObject(completion.Content);
            if (doc is null)
            {
                return MoaProposal.Excluded(model, "its response was not valid JSON, so its position cannot be read.");
            }

            var status = InspectorJson.ReadString(doc.RootElement, "status", "answer");
            if (!string.Equals(status, "needs_help", StringComparison.OrdinalIgnoreCase))
            {
                return new MoaProposal(model, true, doc.RootElement.GetRawText(), null);
            }

            if (lastHop)
            {
                return MoaProposal.Excluded(
                    model,
                    $"it spent all {maxHops} of its peer-help hops without producing a position, so it has no conclusion to contribute.");
            }

            var target = InspectorJson.ReadString(doc.RootElement, "targetAgent");
            var question = InspectorJson.ReadString(doc.RootElement, "question");
            var answer = await RouteHelpAsync(context, target, question, ct);

            messages.Add(new LlmMessage(LlmRole.Assistant, completion.Content));
            messages.Add(new LlmMessage(LlmRole.User, $"{PromptSanitizer.Sanitize(target, 64, whenEmpty: "(unknown agent)")} answered your question:\n{answer}\n\nNow give your position."));
        }

        return MoaProposal.Excluded(model, "it never produced a position.");
    }

    /// <summary>
    /// Hands one model's question to the specialist it named. An unknown target is answered with
    /// the real roster rather than silently dropped: a model that asked the wrong agent should be
    /// able to correct itself on the next hop.
    /// </summary>
    private async Task<string> RouteHelpAsync(InspectorContext context, string targetAgent, string question, CancellationToken ct)
    {
        var specialist = _specialists.FirstOrDefault(s => string.Equals(s.AgentRole, targetAgent, StringComparison.OrdinalIgnoreCase));
        if (specialist is null)
        {
            var roster = string.Join(", ", _specialists.Select(s => s.AgentRole));
            return $"There is no agent called \"{PromptSanitizer.Sanitize(targetAgent, 64, whenEmpty: "(none named)")}\" on this investigation. The agents are: {roster}.";
        }

        if (!specialist.HasSource)
        {
            return $"{specialist.AgentRole} cannot answer: {specialist.NoSourceReason}";
        }

        try
        {
            return await specialist.AnswerAsync(context, question, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] specialist {Agent} failed while answering a peer question.", specialist.AgentRole);
            return $"{specialist.AgentRole} failed while answering: {InspectorJson.Truncate(ex.Message, 300)}";
        }
    }

    private static (string Status, string? OutcomeJson) ClassifyDebateTurn(LlmCompletion completion)
    {
        using var doc = InspectorJson.TryParseObject(completion.Content);
        if (doc is null) return (AgentOutcomeStatus.Error, null);

        var status = InspectorJson.ReadString(doc.RootElement, "status", "answer");
        var outcome = doc.RootElement.GetRawText();
        return string.Equals(status, "needs_help", StringComparison.OrdinalIgnoreCase)
            ? (AgentOutcomeStatus.NeedsHelp, outcome)
            : (AgentOutcomeStatus.Final, outcome);
    }

    private string BuildDebatePrompt(InspectorCaseBrief brief, string evidence, string? peers, int round, int maxHops)
    {
        var peerBlock = peers is null
            ? "This is the independent round: you have not been shown anyone else's opinion on purpose."
            : $"WHAT YOUR PEERS CONCLUDED IN THE PREVIOUS ROUND:\n{peers}";
        var roundLabel = round == 0 ? "round 0, independent" : $"round {round}";

        return $$"""
            {{brief.PromptBlock}}

            WHAT THE SPECIALIST AGENTS CONTRIBUTED:
            {{evidence}}

            {{peerBlock}}

            THE AGENTS YOU MAY ASK FOR HELP: {{string.Join(", ", _specialists.Select(s => s.AgentRole))}}

            Answer with STRICT JSON ONLY, no markdown fences, as ONE of these two shapes:
            {"status":"answer","hypothesis":"what you believe is happening","supportingEvidence":["the exact measurement or quote you are relying on"],"confidence":0.0,"unknowns":["..."]}
            {"status":"needs_help","targetAgent":"one of the agents listed above","question":"one specific question"}

            Rules:
            - You may ask for help at most {{maxHops}} time(s) in this round ({{roundLabel}}). After that you must answer.
            - Every item in "supportingEvidence" must quote something above. Do not invent identifiers.
            - An agent reported as "no_source" had nothing to look in. Its silence is not evidence.
            - If the evidence does not support a hypothesis, say so with a low confidence. That is a better answer than a confident guess.
            """;
    }

    private static string RenderBlackboard(IReadOnlyList<BlackboardEntry> blackboard)
    {
        if (blackboard.Count == 0) return "(no agent contributed anything)";

        var sb = new StringBuilder();
        foreach (var entry in blackboard)
        {
            sb.AppendLine($"[{entry.AgentRole}] status={entry.Status}");
            sb.AppendLine(entry.Summary);
            if (entry.FindingsJson is { Length: > 0 })
            {
                sb.AppendLine(InspectorJson.Truncate(entry.FindingsJson, 6000));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderPeers(IReadOnlyList<MoaProposal> proposals, string selfModel)
    {
        var peers = proposals.Where(p => p.Model != selfModel).ToList();
        if (peers.Count == 0) return "(no peer produced a position)";

        var sb = new StringBuilder();
        foreach (var peer in peers)
        {
            sb.AppendLine(peer.Available
                ? $"[{peer.Model}] {InspectorJson.Truncate(peer.Json, 3000)}"
                : $"[{peer.Model}] NO POSITION - {peer.Reason}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderProposals(IReadOnlyList<MoaProposal> proposals)
    {
        if (proposals.Count == 0) return "(no model took part)";

        var sb = new StringBuilder();
        foreach (var proposal in proposals)
        {
            sb.AppendLine(proposal.Available
                ? $"[{proposal.Model}] {InspectorJson.Truncate(proposal.Json, 4000)}"
                : $"[{proposal.Model}] EXCLUDED from the debate - {proposal.Reason}. Do not count this model as agreeing with anything.");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
