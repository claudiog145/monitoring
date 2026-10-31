using System.Text.RegularExpressions;

namespace JourneyMonitor.Core.Company;

/// <summary>Severity of a <see cref="CompanyProfileFinding"/>. Strings, matching the rest of this namespace.</summary>
public static class CompanyProfileSeverity
{
    /// <summary>The profile is wrong in a way that silently classifies traffic incorrectly, or not at all.</summary>
    public const string Error = "error";

    /// <summary>The profile works, but something in it is dead weight or will surprise the next reader.</summary>
    public const string Warning = "warning";
}

/// <summary>
/// One thing wrong with a profile. <see cref="Path"/> is the document coordinate
/// (<c>services[2].flows[0].steps[4].matchers[1]</c>) so an editor can be pointed straight at it,
/// and <see cref="Message"/> says what to DO - a finding that only names the defect makes whoever
/// onboarded the company guess at the fix.
/// </summary>
public sealed record CompanyProfileFinding(string Severity, string Code, string Path, string Message)
{
    public bool IsError => string.Equals(Severity, CompanyProfileSeverity.Error, StringComparison.Ordinal);
}

/// <summary>
/// Reads a <see cref="CompanyProfile"/> and reports what is wrong with it. Pure: no database, no
/// telemetry, no clock, no logger - the same document always produces the same findings, which is
/// what lets the admin UI, the seed test and a future onboarding wizard all run it.
///
/// Returns findings instead of throwing, on purpose. A profile is edited by whoever onboards a
/// customer; handing them one exception means they fix one defect per save. The whole list, with
/// the coordinate and the remedy, means they fix the document once.
///
/// This validator judges the DOCUMENT, not the telemetry. "No rule matches this url" is not
/// something it can know and is not a finding here - that surfaces at run time as
/// <see cref="CompanyResolution.Unresolved"/>, which is deliberately a different thing.
/// </summary>
public static class CompanyProfileValidator
{
    /// <summary>Same bound <see cref="ProfileDrivenAdapter"/> compiles with, so a pattern that validates here is a pattern that runs there.</summary>
    private static readonly TimeSpan RegexProbeTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> KnownMatcherKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        StepMatcherKind.UrlContains,
        StepMatcherKind.PathContains,
        StepMatcherKind.OperationEquals,
        StepMatcherKind.OperationContains,
        StepMatcherKind.PageNameContains,
        StepMatcherKind.Regex,
    };

    private static readonly HashSet<string> KnownIdentityKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        EntityIdentitySourceKind.CustomDimension,
        EntityIdentitySourceKind.QueryParameter,
        EntityIdentitySourceKind.Host,
        EntityIdentitySourceKind.RoleName,
    };

    /// <summary>Kinds that read a named field off the row, so an empty key makes them read nothing.</summary>
    private static readonly HashSet<string> IdentityKindsNeedingKey = new(StringComparer.OrdinalIgnoreCase)
    {
        EntityIdentitySourceKind.CustomDimension,
        EntityIdentitySourceKind.QueryParameter,
    };

    public static IReadOnlyList<CompanyProfileFinding> Validate(CompanyProfile? profile)
    {
        var findings = new List<CompanyProfileFinding>();

        if (profile is null)
        {
            Error(findings, "profile-missing", "$",
                "The profile is null. A document that failed to deserialise must not be used to classify "
                + "traffic; check that the stored JSON is camelCase and parses under JsonSerializerDefaults.Web.");
            return findings;
        }

        ValidateHeader(profile, findings);
        ValidateServices(profile, findings);
        ValidateIdentitySources(profile, findings);

        return findings;
    }

    /// <summary>Convenience for callers that only need a go/no-go, e.g. a store refusing to publish a broken document.</summary>
    public static bool HasErrors(IEnumerable<CompanyProfileFinding> findings) =>
        findings.Any(f => f.IsError);

    private static void ValidateHeader(CompanyProfile profile, List<CompanyProfileFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileKey))
        {
            Error(findings, "profile-key-missing", "$.profileKey",
                "Give the profile a key. It is the primary key of monitoring.CompanyProfile and the value "
                + "every caller asks for by name, so a blank one cannot be loaded back.");
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            Warn(findings, "profile-display-name-missing", "$.displayName",
                "Give the profile a display name; the admin list shows the key when this is blank.");
        }

        if (profile.Labels is null)
        {
            Error(findings, "labels-missing", "$.labels",
                "Add a labels object naming what this company calls a service, a flow, a step, an entity and "
                + "an owner. Without it every screen falls back to the platform's own words.");
        }
        else
        {
            RequireLabel(findings, "$.labels.service", profile.Labels.Service, "service");
            RequireLabel(findings, "$.labels.flow", profile.Labels.Flow, "flow");
            RequireLabel(findings, "$.labels.step", profile.Labels.Step, "step");
            RequireLabel(findings, "$.labels.entity", profile.Labels.Entity, "entity");
            RequireLabel(findings, "$.labels.owner", profile.Labels.Owner, "owner");
        }

        if (profile.Glossary is null)
        {
            Error(findings, "glossary-missing", "$.glossary",
                "Add a glossary. Its nouns are what the agents are allowed to call things in an unattended "
                + "prompt; with none, a model invents its own vocabulary for this customer.");
        }
        else
        {
            RequireNoun(findings, "$.glossary.serviceNoun", profile.Glossary.ServiceNoun, "service");
            RequireNoun(findings, "$.glossary.flowNoun", profile.Glossary.FlowNoun, "flow");
            RequireNoun(findings, "$.glossary.stepNoun", profile.Glossary.StepNoun, "step");
            RequireNoun(findings, "$.glossary.entityNoun", profile.Glossary.EntityNoun, "entity");
            RequireNoun(findings, "$.glossary.ownerNoun", profile.Glossary.OwnerNoun, "owner");

            if (string.IsNullOrWhiteSpace(profile.Glossary.Description))
            {
                Warn(findings, "glossary-description-missing", "$.glossary.description",
                    "Describe in one or two sentences what this company's product actually is. It is the only "
                    + "context a model gets before it writes a root-cause narrative about it.");
            }
        }
    }

    private static void ValidateServices(CompanyProfile profile, List<CompanyProfileFinding> findings)
    {
        var services = profile.Services;
        if (services is null || services.Count == 0)
        {
            Error(findings, "services-empty", "$.services",
                "Declare at least one service. A profile with none classifies every row as unresolved, which "
                + "looks identical to a customer whose traffic never arrived.");
            return;
        }

        var seenServiceKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var servicesWithFallback = new List<string>();

        for (var s = 0; s < services.Count; s++)
        {
            var service = services[s];
            var servicePath = $"$.services[{s}]";

            if (service is null)
            {
                Error(findings, "service-missing", servicePath,
                    "Remove this null entry from the services array, or replace it with a real service.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(service.Key))
            {
                Error(findings, "service-key-missing", $"{servicePath}.key",
                    "Give this service a key. The key is what the board, the owner map and the dedup key all "
                    + "store, so a blank one cannot be routed or grouped.");
            }
            else if (seenServiceKeys.TryGetValue(service.Key.Trim(), out var firstIndex))
            {
                Error(findings, "service-key-duplicate", $"{servicePath}.key",
                    $"Service key \"{service.Key.Trim()}\" is already used by $.services[{firstIndex}]. Rename one of "
                    + "them: keys are compared case-insensitively, and the second one can never be looked up.");
            }
            else
            {
                seenServiceKeys[service.Key.Trim()] = s;
            }

            if (string.IsNullOrWhiteSpace(service.DisplayName))
            {
                Warn(findings, "service-display-name-missing", $"{servicePath}.displayName",
                    "Give this service a display name; the UI shows the raw key when it is blank.");
            }

            if (service.TelemetrySourceAliases is null || service.TelemetrySourceAliases.Count == 0)
            {
                Warn(findings, "service-telemetry-aliases-empty", $"{servicePath}.telemetrySourceAliases",
                    "Name at least one telemetry source alias registered in monitoring.TelemetrySource, "
                    + "otherwise no query can be pointed at this service.");
            }

            var declaredSteps = ValidateFlows(service, servicePath, findings);

            if (!string.IsNullOrWhiteSpace(service.FallbackStep))
            {
                servicesWithFallback.Add(string.IsNullOrWhiteSpace(service.Key) ? servicePath : service.Key.Trim());

                if (!declaredSteps.Contains(service.FallbackStep.Trim()))
                {
                    Error(findings, "fallback-step-unknown", $"{servicePath}.fallbackStep",
                        $"fallbackStep \"{service.FallbackStep.Trim()}\" is not the key of any step declared by this "
                        + "service. Point it at one of: "
                        + (declaredSteps.Count == 0 ? "(this service declares no steps at all)" : string.Join(", ", declaredSteps.OrderBy(k => k, StringComparer.Ordinal)))
                        + " - or set it to null to leave unmatched traffic honestly unresolved.");
                }
            }
        }

        if (servicesWithFallback.Count > 1)
        {
            Error(findings, "fallback-step-ambiguous", "$.services[*].fallbackStep",
                $"{servicesWithFallback.Count} services declare a fallback step ({string.Join(", ", servicesWithFallback)}). "
                + "At run time two eligible catch-alls cancel out and the row resolves to NOTHING, so keep a fallback on "
                + "at most one service - or narrow the others with hostFilters so only one can ever claim a given row.");
        }
    }

    /// <summary>Returns the step keys this service declares, so the fallback check can name the real options.</summary>
    private static HashSet<string> ValidateFlows(
        ServiceDefinition service, string servicePath, List<CompanyProfileFinding> findings)
    {
        var declaredSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (service.Flows is null || service.Flows.Count == 0)
        {
            Error(findings, "service-flows-empty", $"{servicePath}.flows",
                "Declare at least one flow for this service. A service with no flow has no steps, so nothing "
                + "it is responsible for can ever be measured.");
            return declaredSteps;
        }

        var seenFlowKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stepOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ordersSeen = new Dictionary<int, string>();

        for (var f = 0; f < service.Flows.Count; f++)
        {
            var flow = service.Flows[f];
            var flowPath = $"{servicePath}.flows[{f}]";

            if (flow is null)
            {
                Error(findings, "flow-missing", flowPath,
                    "Remove this null entry from the flows array, or replace it with a real flow.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(flow.Key))
            {
                Error(findings, "flow-key-missing", $"{flowPath}.key",
                    "Give this flow a key; it is reported on every resolved step and stored with the finding.");
            }
            else if (seenFlowKeys.TryGetValue(flow.Key.Trim(), out var firstFlow))
            {
                Error(findings, "flow-key-duplicate", $"{flowPath}.key",
                    $"Flow key \"{flow.Key.Trim()}\" is already used by {servicePath}.flows[{firstFlow}]. Rename one of "
                    + "them so a reported flow identifies exactly one journey.");
            }
            else
            {
                seenFlowKeys[flow.Key.Trim()] = f;
            }

            if (flow.Steps is null || flow.Steps.Count == 0)
            {
                Error(findings, "flow-steps-empty", $"{flowPath}.steps",
                    "Declare at least one step in this flow. An empty flow matches nothing and only adds a name "
                    + "to the document.");
                continue;
            }

            for (var t = 0; t < flow.Steps.Count; t++)
            {
                var step = flow.Steps[t];
                var stepPath = $"{flowPath}.steps[{t}]";

                if (step is null)
                {
                    Error(findings, "step-missing", stepPath,
                        "Remove this null entry from the steps array, or replace it with a real step.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(step.Key))
                {
                    Error(findings, "step-key-missing", $"{stepPath}.key",
                        "Give this step a key. The key is what a finding, an SLO and the dedup key all store; the "
                        + "display name is free to change, the key is not.");
                }
                else if (stepOwners.TryGetValue(step.Key.Trim(), out var owner))
                {
                    Error(findings, "step-key-duplicate", $"{stepPath}.key",
                        $"Step key \"{step.Key.Trim()}\" is already declared by {owner} in this service. Step keys must be "
                        + "unique per service - give this one a distinct key, even when the two steps share a display name.");
                }
                else
                {
                    stepOwners[step.Key.Trim()] = stepPath;
                    declaredSteps.Add(step.Key.Trim());
                }

                if (string.IsNullOrWhiteSpace(step.DisplayName))
                {
                    Warn(findings, "step-display-name-missing", $"{stepPath}.displayName",
                        "Give this step a display name; it is what the board column and the alert text show.");
                }

                if (ordersSeen.TryGetValue(step.Order, out var sameOrder))
                {
                    Warn(findings, "step-order-duplicate", $"{stepPath}.order",
                        $"Order {step.Order} is also used by {sameOrder} in this service. Order decides which step wins when "
                        + "two matchers overlap, and ties are broken only by declaration sequence - give each step a "
                        + "distinct order so the outcome is written down rather than inherited from the array.");
                }
                else
                {
                    ordersSeen[step.Order] = stepPath;
                }

                ValidateMatchers(step, stepPath, findings);
            }
        }

        return declaredSteps;
    }

    private static void ValidateMatchers(StepDefinition step, string stepPath, List<CompanyProfileFinding> findings)
    {
        if (step.Matchers is null || step.Matchers.Count == 0)
        {
            Error(findings, "step-matchers-empty", $"{stepPath}.matchers",
                "Give this step at least one matcher. A step with none can never be reached, so its traffic "
                + "silently lands on a later step or on nothing at all.");
            return;
        }

        for (var m = 0; m < step.Matchers.Count; m++)
        {
            var matcher = step.Matchers[m];
            var matcherPath = $"{stepPath}.matchers[{m}]";

            if (matcher is null)
            {
                Error(findings, "matcher-missing", matcherPath,
                    "Remove this null entry from the matchers array, or replace it with a real matcher.");
                continue;
            }

            var kind = matcher.Kind?.Trim() ?? "";
            if (kind.Length == 0)
            {
                Error(findings, "matcher-kind-missing", $"{matcherPath}.kind",
                    "Set a matcher kind. Use one of: " + string.Join(", ", KnownMatcherKinds.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }
            else if (!KnownMatcherKinds.Contains(kind))
            {
                Error(findings, "matcher-kind-unknown", $"{matcherPath}.kind",
                    $"Matcher kind \"{kind}\" is not one this build can evaluate, so it will never match anything. "
                    + "Use one of: " + string.Join(", ", KnownMatcherKinds.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }

            if (string.IsNullOrWhiteSpace(matcher.Value))
            {
                Error(findings, "matcher-value-missing", $"{matcherPath}.value",
                    "Give this matcher a value to test for. An empty value never matches, which makes the step "
                    + "look like traffic that never happened.");
                continue;
            }

            if (!string.Equals(kind, StepMatcherKind.Regex, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var options = RegexOptions.CultureInvariant
                    | (matcher.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                _ = new Regex(matcher.Value, options, RegexProbeTimeout);
            }
            catch (ArgumentException ex)
            {
                Error(findings, "matcher-regex-invalid", $"{matcherPath}.value",
                    $"The regex \"{matcher.Value}\" does not compile ({ex.Message.Trim()}). Fix the pattern or switch "
                    + $"to \"{StepMatcherKind.UrlContains}\" - at run time an uncompilable pattern is skipped, so the "
                    + "step quietly never fires.");
            }
        }
    }

    private static void ValidateIdentitySources(CompanyProfile profile, List<CompanyProfileFinding> findings)
    {
        var sources = profile.EntityIdentitySources;
        if (sources is null || sources.Count == 0)
        {
            Error(findings, "identity-sources-empty", "$.entityIdentitySources",
                "Declare at least one way to tell who is affected (a custom dimension, a query parameter, the "
                + "host or the role name). With none, every alert says something is broken without saying for whom.");
            return;
        }

        var seenOrders = new Dictionary<int, int>();
        var seenSources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var path = $"$.entityIdentitySources[{i}]";

            if (source is null)
            {
                Error(findings, "identity-source-missing", path,
                    "Remove this null entry from the entityIdentitySources array, or replace it with a real source.");
                continue;
            }

            var kind = source.Kind?.Trim() ?? "";
            if (kind.Length == 0)
            {
                Error(findings, "identity-kind-missing", $"{path}.kind",
                    "Set an identity source kind. Use one of: " + string.Join(", ", KnownIdentityKinds.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }
            else if (!KnownIdentityKinds.Contains(kind))
            {
                Error(findings, "identity-kind-unknown", $"{path}.kind",
                    $"Identity source kind \"{kind}\" is not one this build can read, so it yields nothing and the next "
                    + "source silently answers in its place. Use one of: "
                    + string.Join(", ", KnownIdentityKinds.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }
            else if (IdentityKindsNeedingKey.Contains(kind) && string.IsNullOrWhiteSpace(source.Key))
            {
                Error(findings, "identity-key-missing", $"{path}.key",
                    $"A \"{kind}\" source needs the name of the field to read. Fill in key, or use "
                    + $"\"{EntityIdentitySourceKind.Host}\", which needs none.");
            }

            if (string.IsNullOrWhiteSpace(source.EntityKind))
            {
                Error(findings, "identity-entity-kind-missing", $"{path}.entityKind",
                    "Say what the value identifies (\"club\", \"tenant\", \"partner\"...). It is reported as "
                    + "EntityIdentity.Kind, and a blank one leaves the caller guessing what the id means.");
            }

            if (seenOrders.TryGetValue(source.Order, out var sameOrder))
            {
                Warn(findings, "identity-order-duplicate", $"{path}.order",
                    $"Order {source.Order} is also used by $.entityIdentitySources[{sameOrder}]. Sources are tried in order "
                    + "and the first non-empty one wins, so a tie makes it undefined which field gets the credit.");
            }
            else
            {
                seenOrders[source.Order] = i;
            }

            var signature = $"{kind}|{source.Key?.Trim()}";
            if (seenSources.TryGetValue(signature, out var sameSource))
            {
                Warn(findings, "identity-source-duplicate", path,
                    $"This repeats $.entityIdentitySources[{sameSource}] (field names are compared case-insensitively), so it "
                    + "can never be reached. Delete it, or point it at a genuinely different field.");
            }
            else
            {
                seenSources[signature] = i;
            }
        }
    }

    private static void RequireLabel(List<CompanyProfileFinding> findings, string path, string? value, string role)
    {
        if (!string.IsNullOrWhiteSpace(value)) return;

        Error(findings, "label-missing", path,
            $"Say what this company calls a {role}. Every screen and every prompt renders this word, and a blank "
            + "one shows up as an empty column header.");
    }

    private static void RequireNoun(List<CompanyProfileFinding> findings, string path, string? value, string role)
    {
        if (!string.IsNullOrWhiteSpace(value)) return;

        Error(findings, "glossary-noun-missing", path,
            $"Give the glossary a {role} noun. It is injected into unattended prompts, and a blank one makes the "
            + "model choose its own word for this customer's concept.");
    }

    private static void Error(List<CompanyProfileFinding> findings, string code, string path, string message) =>
        findings.Add(new CompanyProfileFinding(CompanyProfileSeverity.Error, code, path, message));

    private static void Warn(List<CompanyProfileFinding> findings, string code, string path, string message) =>
        findings.Add(new CompanyProfileFinding(CompanyProfileSeverity.Warning, code, path, message));
}
