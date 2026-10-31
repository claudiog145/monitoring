using System.Text.RegularExpressions;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Caps and thresholds for <see cref="ProfileDraftBuilder"/>. A separate type because a record's
/// primary-constructor defaults cannot reference constants declared in its own body - the same
/// reason <see cref="TelemetryProfileDefaults"/> exists.
/// </summary>
public static class ProfileDraftDefaults
{
    /// <summary>
    /// Non-empty coverage below which a key cannot carry an attribution on its own. Same 50% the
    /// profiler warns at, kept in step with it deliberately: a draft that called a 40% key usable
    /// while the discovery report called it unusable would be two answers to one question.
    /// </summary>
    public const double UsableCoveragePercent = 50.0;

    /// <summary>
    /// Below this a key is not even worth putting in front of a reviewer as a proposal. It is NOT
    /// a filter on the measurement: every rejected key still appears in
    /// <see cref="CompanyProfileDraft.Proposals"/> with the number that rejected it.
    /// </summary>
    public const double ProposableCoveragePercent = 1.0;

    public const int MaxIdentitySources = 8;
    public const int MaxServices = 12;
    public const int MaxHostProposals = 25;

    /// <summary>
    /// What <see cref="EntityIdentitySource.EntityKind"/> is set to on every proposal. What a
    /// value identifies - a club, a tenant, a partner - is a judgement about someone else's
    /// business, not something a probe measured, so the draft refuses to name it and makes the
    /// reviewer do it.
    /// </summary>
    public const string UnspecifiedEntityKind = "unspecified";
}

/// <summary>Knobs a caller may set when asking for a draft. Everything else is measured.</summary>
public sealed record ProfileDraftOptions(
    /// <summary>Defaults to a slug of the telemetry alias.</summary>
    string? ProfileKey = null,
    /// <summary>Defaults to the telemetry alias verbatim. The draft will not invent a company name.</summary>
    string? DisplayName = null,
    double MinUsableCoveragePercent = ProfileDraftDefaults.UsableCoveragePercent,
    double MinProposableCoveragePercent = ProfileDraftDefaults.ProposableCoveragePercent,
    int MaxIdentitySources = ProfileDraftDefaults.MaxIdentitySources,
    int MaxServices = ProfileDraftDefaults.MaxServices)
{
    public static ProfileDraftOptions Default { get; } = new();

    public ProfileDraftOptions Normalize() => this with
    {
        ProfileKey = ProfileKey?.Trim(),
        DisplayName = DisplayName?.Trim(),
        MinUsableCoveragePercent = Math.Clamp(MinUsableCoveragePercent, 0.0, 100.0),
        MinProposableCoveragePercent = Math.Clamp(MinProposableCoveragePercent, 0.0, 100.0),
        MaxIdentitySources = Math.Clamp(MaxIdentitySources, 1, 50),
        MaxServices = Math.Clamp(MaxServices, 1, 100),
    };
}

/// <summary>
/// A <see cref="CompanyProfile"/> proposed from measurements, together with everything a reviewer
/// needs in order to disagree with it.
///
/// <see cref="Profile"/> is never a finished profile and is never written anywhere by the thing
/// that built it: it arrives with <c>Enabled = false</c>, with no flows and no steps, and with
/// every <c>entityKind</c> left unspecified. Those gaps are the product. A draft that guessed a
/// funnel would look finished and be wrong, and the cost of that is paid months later by whoever
/// trusts a board built on invented stages.
/// </summary>
public sealed record CompanyProfileDraft(
    CompanyProfile Profile,
    /// <summary>Every candidate the report measured - accepted AND rejected - with the arithmetic behind the decision.</summary>
    IReadOnlyList<DraftProposal> Proposals,
    IReadOnlyList<DraftWarning> Warnings,
    /// <summary>What a human has to do before this document is worth saving. Ordered.</summary>
    IReadOnlyList<string> NextSteps,
    DraftReadiness Readiness,
    /// <summary>False when no measured key reaches <see cref="ProfileDraftOptions.MinUsableCoveragePercent"/>: this enterprise cannot attribute an incident to a customer.</summary>
    bool CanAttributeIncidents,
    string SourceAlias,
    string SourceEnvironment,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    DateTimeOffset GeneratedAt);

/// <summary>
/// There is no <c>Ready</c> member on purpose. Every draft needs a human, because the one thing
/// that makes a profile useful - the step map - is the one thing nothing here measured.
/// </summary>
public enum DraftReadiness
{
    /// <summary>A blocker is present: saving this as-is would produce a profile that cannot do its job.</summary>
    Blocked,

    /// <summary>Nothing is broken, and it is still incomplete until a human adds the flows and steps.</summary>
    NeedsReview,
}

public enum DraftWarningSeverity
{
    /// <summary>Context a reader needs to read the numbers correctly.</summary>
    Info,

    /// <summary>Something is missing or unmeasured; the draft is still worth reviewing.</summary>
    Warning,

    /// <summary>The draft cannot do its job as it stands. Onboarding has to stop and deal with it.</summary>
    Blocker,
}

public sealed record DraftWarning(DraftWarningSeverity Severity, string Code, string Message);

public static class DraftWarningCode
{
    public const string SourceUnresolved = "source-unresolved";
    public const string DiscoveryDegraded = "discovery-degraded";
    public const string NoTelemetryInWindow = "no-telemetry-in-window";
    public const string CannotAttributeIncidents = "cannot-attribute-incidents";
    public const string EntityKindUnspecified = "entity-kind-unspecified";
    public const string NoStepsProposed = "no-steps-proposed";
    public const string NoServicesProposed = "no-services-proposed";
    public const string HostsNeedAssignment = "hosts-need-assignment";
    public const string ProfileDisabled = "profile-disabled";
    public const string LabelsArePlaceholders = "labels-are-placeholders";

    /// <summary>A warning the discovery report produced, carried into the draft verbatim.</summary>
    public const string DiscoveryNote = "discovery-note";
}

public static class DraftProposalKind
{
    public const string EntityIdentitySource = "entity-identity-source";
    public const string Service = "service";
    public const string HostFilter = "host-filter";
}

/// <summary>
/// One thing the builder considered, whether or not it ended up in <see cref="CompanyProfileDraft.Profile"/>.
///
/// Rejections are carried with the same weight as acceptances. A reviewer who only sees what was
/// proposed cannot tell "there was no better key" from "there was a better key and something
/// quietly dropped it".
/// </summary>
public sealed record DraftProposal(
    string Kind,
    /// <summary>JSON path into the draft profile, e.g. <c>entityIdentitySources[0]</c>. Empty when <see cref="Proposed"/> is false.</summary>
    string Target,
    string Value,
    bool Proposed,
    /// <summary>Non-empty coverage of the measurement behind this proposal, where one exists.</summary>
    double? CoveragePercent,
    /// <summary>Ranker score, where one exists. Null for things that were counted rather than scored.</summary>
    double? Score,
    /// <summary>The measurement. For a rejection, the first line says why it was rejected.</summary>
    IReadOnlyList<string> Evidence);

/// <summary>
/// The seam the company-profile admin endpoint validates a write through.
///
/// Deliberately thin and deliberately NOT the only validator this codebase will have: a richer
/// <c>CompanyProfileValidator</c> is being written separately, and the integrator is expected to
/// register an adapter for it. Until then <see cref="StructuralProfileWriteValidator"/> is the
/// default, so a malformed profile is refused at the boundary today rather than at the next timer
/// run - and the endpoint does not depend on a type that may not exist yet.
/// </summary>
public interface IProfileWriteValidator
{
    ProfileWriteValidationResult Validate(CompanyProfile? profile);
}

public sealed record ProfileWriteValidationResult(IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;

    public static ProfileWriteValidationResult Valid { get; } = new([]);
}

/// <summary>
/// Refuses the profiles that would break something downstream, and nothing else.
///
/// What it rejects is a deliberately short list: blank required text, keys that collide, an
/// identity order or step order that is ambiguous, a matcher kind no adapter can evaluate, a
/// regex that does not compile, a step declared with no matchers. All of those make the profile
/// behave differently from what it says.
///
/// What it allows, on purpose, is an INCOMPLETE profile - a service with no flows, a profile with
/// no services at all. That is exactly the shape a fresh draft has, and an onboarding path where
/// you cannot save your work until the whole map is finished is an onboarding path people do
/// outside the tool. Incompleteness classifies nothing; it does not misclassify anything.
/// </summary>
public sealed class StructuralProfileWriteValidator : IProfileWriteValidator
{
    public static StructuralProfileWriteValidator Instance { get; } = new();

    /// <summary>Same character class as a telemetry alias: the key reaches URLs, SQL keys and dedup keys.</summary>
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Compiling a pattern does not run it; the timeout is what the adapter will enforce at match time.</summary>
    private static readonly TimeSpan RegexCompileTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly string[] KnownMatcherKinds =
    [
        StepMatcherKind.UrlContains,
        StepMatcherKind.OperationEquals,
        StepMatcherKind.OperationContains,
        StepMatcherKind.PageNameContains,
        StepMatcherKind.Regex,
    ];

    private static readonly string[] KnownIdentityKinds =
    [
        EntityIdentitySourceKind.CustomDimension,
        EntityIdentitySourceKind.QueryParameter,
        EntityIdentitySourceKind.Host,
        EntityIdentitySourceKind.RoleName,
    ];

    public ProfileWriteValidationResult Validate(CompanyProfile? profile)
    {
        var problems = new List<string>();
        if (profile is null)
        {
            problems.Add("Request body is not a company profile document.");
            return new ProfileWriteValidationResult(problems);
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileKey)) problems.Add("profileKey is required.");
        else if (!KeyPattern.IsMatch(profile.ProfileKey.Trim()))
        {
            problems.Add($"profileKey \"{profile.ProfileKey.Trim()}\" must be 1-64 characters of letters, digits, dot, dash or underscore.");
        }

        Require(problems, profile.DisplayName, "displayName");
        if (profile.Version < 1) problems.Add($"version must be 1 or greater (got {profile.Version}).");

        ValidateLabels(problems, profile.Labels);
        ValidateGlossary(problems, profile.Glossary);
        ValidateIdentitySources(problems, profile.EntityIdentitySources);
        ValidateServices(problems, profile.Services);

        return new ProfileWriteValidationResult(problems);
    }

    private static void ValidateLabels(List<string> problems, CompanyLabels? labels)
    {
        if (labels is null) { problems.Add("labels is required."); return; }
        Require(problems, labels.Service, "labels.service");
        Require(problems, labels.Flow, "labels.flow");
        Require(problems, labels.Step, "labels.step");
        Require(problems, labels.Entity, "labels.entity");
        Require(problems, labels.Owner, "labels.owner");
    }

    private static void ValidateGlossary(List<string> problems, CompanyGlossary? glossary)
    {
        if (glossary is null) { problems.Add("glossary is required."); return; }
        Require(problems, glossary.ServiceNoun, "glossary.serviceNoun");
        Require(problems, glossary.FlowNoun, "glossary.flowNoun");
        Require(problems, glossary.StepNoun, "glossary.stepNoun");
        Require(problems, glossary.EntityNoun, "glossary.entityNoun");
        Require(problems, glossary.OwnerNoun, "glossary.ownerNoun");
    }

    private static void ValidateIdentitySources(List<string> problems, IReadOnlyList<EntityIdentitySource>? sources)
    {
        if (sources is null) { problems.Add("entityIdentitySources is required (use [] for none)."); return; }

        var seenOrders = new Dictionary<int, int>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var path = $"entityIdentitySources[{i}]";
            if (source is null) { problems.Add($"{path} is null."); continue; }

            if (string.IsNullOrWhiteSpace(source.Kind) ||
                !KnownIdentityKinds.Contains(source.Kind.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{path}.kind \"{source.Kind}\" is not a known identity kind ({string.Join(", ", KnownIdentityKinds)}).");
            }

            // Host is the one kind that reads no named field off the row, so it is the one kind
            // allowed to carry an empty key.
            var needsKey = !string.Equals(source.Kind?.Trim(), EntityIdentitySourceKind.Host, StringComparison.OrdinalIgnoreCase);
            if (needsKey) Require(problems, source.Key, $"{path}.key");

            Require(problems, source.EntityKind, $"{path}.entityKind");

            // Order decides which identity wins when two sources both resolve. A duplicate makes
            // that outcome depend on list order, which nothing promises to preserve.
            if (seenOrders.TryGetValue(source.Order, out var first))
            {
                problems.Add($"{path}.order {source.Order} duplicates entityIdentitySources[{first}]; the order decides which identity wins and must be unique.");
            }
            else seenOrders[source.Order] = i;
        }
    }

    private static void ValidateServices(List<string> problems, IReadOnlyList<ServiceDefinition>? services)
    {
        if (services is null) { problems.Add("services is required (use [] for none)."); return; }

        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < services.Count; i++)
        {
            var service = services[i];
            var path = $"services[{i}]";
            if (service is null) { problems.Add($"{path} is null."); continue; }

            if (string.IsNullOrWhiteSpace(service.Key)) problems.Add($"{path}.key is required.");
            else if (!KeyPattern.IsMatch(service.Key.Trim()))
            {
                problems.Add($"{path}.key \"{service.Key.Trim()}\" must be 1-64 characters of letters, digits, dot, dash or underscore.");
            }
            else if (seenKeys.TryGetValue(service.Key.Trim(), out var first))
            {
                problems.Add($"{path}.key \"{service.Key.Trim()}\" duplicates services[{first}].key; FindService would only ever return the first.");
            }
            else seenKeys[service.Key.Trim()] = i;

            Require(problems, service.DisplayName, $"{path}.displayName");

            if (service.TelemetrySourceAliases is null) problems.Add($"{path}.telemetrySourceAliases is required (use [] for none).");
            else
            {
                for (var a = 0; a < service.TelemetrySourceAliases.Count; a++)
                {
                    if (!TelemetrySourceAlias.IsValid(service.TelemetrySourceAliases[a]))
                    {
                        problems.Add($"{path}.telemetrySourceAliases[{a}] \"{service.TelemetrySourceAliases[a]}\" is not a valid telemetry source alias (letters, digits, dot, dash, underscore; 1-64 characters).");
                    }
                }
            }

            if (service.HostFilters is null) problems.Add($"{path}.hostFilters is required (use [] for none).");
            else
            {
                for (var h = 0; h < service.HostFilters.Count; h++)
                {
                    Require(problems, service.HostFilters[h], $"{path}.hostFilters[{h}]");
                }
            }

            ValidateFlows(problems, service.Flows, path);
        }
    }

    private static void ValidateFlows(List<string> problems, IReadOnlyList<FlowDefinition>? flows, string servicePath)
    {
        if (flows is null) { problems.Add($"{servicePath}.flows is required (use [] for none)."); return; }

        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var f = 0; f < flows.Count; f++)
        {
            var flow = flows[f];
            var path = $"{servicePath}.flows[{f}]";
            if (flow is null) { problems.Add($"{path} is null."); continue; }

            if (string.IsNullOrWhiteSpace(flow.Key)) problems.Add($"{path}.key is required.");
            else if (seenKeys.TryGetValue(flow.Key.Trim(), out var first))
            {
                problems.Add($"{path}.key \"{flow.Key.Trim()}\" duplicates {servicePath}.flows[{first}].key.");
            }
            else seenKeys[flow.Key.Trim()] = f;

            Require(problems, flow.DisplayName, $"{path}.displayName");
            ValidateSteps(problems, flow.Steps, path);
        }
    }

    private static void ValidateSteps(List<string> problems, IReadOnlyList<StepDefinition>? steps, string flowPath)
    {
        if (steps is null) { problems.Add($"{flowPath}.steps is required (use [] for none)."); return; }

        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenOrders = new Dictionary<int, int>();
        for (var s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            var path = $"{flowPath}.steps[{s}]";
            if (step is null) { problems.Add($"{path} is null."); continue; }

            if (string.IsNullOrWhiteSpace(step.Key)) problems.Add($"{path}.key is required.");
            else if (seenKeys.TryGetValue(step.Key.Trim(), out var first))
            {
                problems.Add($"{path}.key \"{step.Key.Trim()}\" duplicates {flowPath}.steps[{first}].key.");
            }
            else seenKeys[step.Key.Trim()] = s;

            Require(problems, step.DisplayName, $"{path}.displayName");

            // Order is the evaluation order of the generated case(), where the first match wins.
            // Two steps sharing an order means two different stories about the same request.
            if (seenOrders.TryGetValue(step.Order, out var firstOrder))
            {
                problems.Add($"{path}.order {step.Order} duplicates {flowPath}.steps[{firstOrder}]; step order decides which matcher wins and must be unique within a flow.");
            }
            else seenOrders[step.Order] = s;

            if (step.Matchers is null) { problems.Add($"{path}.matchers is required."); continue; }
            if (step.Matchers.Count == 0)
            {
                problems.Add($"{path}.matchers is empty; a step with no matchers can never match and would silently classify nothing.");
                continue;
            }

            for (var m = 0; m < step.Matchers.Count; m++)
            {
                ValidateMatcher(problems, step.Matchers[m], $"{path}.matchers[{m}]");
            }
        }
    }

    private static void ValidateMatcher(List<string> problems, StepMatcher? matcher, string path)
    {
        if (matcher is null) { problems.Add($"{path} is null."); return; }

        var kind = matcher.Kind?.Trim() ?? "";
        if (kind.Length == 0 || !KnownMatcherKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{path}.kind \"{matcher.Kind}\" is not a known matcher kind ({string.Join(", ", KnownMatcherKinds)}); the adapter would never fire it, which looks exactly like traffic that never happened.");
        }

        if (string.IsNullOrWhiteSpace(matcher.Value))
        {
            problems.Add($"{path}.value is required.");
            return;
        }

        if (kind.Equals(StepMatcherKind.Regex, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = new Regex(matcher.Value, RegexOptions.None, RegexCompileTimeout);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{path}.value is not a valid regular expression: {ex.Message}");
            }
        }
    }

    private static void Require(List<string> problems, string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) problems.Add($"{path} is required.");
    }
}
