using System.Text;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Turns a measured <see cref="TelemetryResourceProfile"/> into a <see cref="CompanyProfile"/>
/// DRAFT for a human to edit and approve.
///
/// Pure, no I/O, and no model call. Automatic mapping by an LLM is a later phase and is out of
/// scope here on purpose: everything this class emits can be checked against the discovery report
/// by hand, line for line, which is the only reason a reviewer can be asked to take
/// responsibility for what they approve.
///
/// What it will not do is the point of it:
/// - It never invents a step, a flow, a funnel or a product name. The draft ships with ZERO
///   steps. A generated funnel is worse than an empty one - it looks finished, so nobody checks
///   it, and every board built on it tells a confident wrong story.
/// - It never names what an identity value means. "club", "tenant", "partner" is a judgement
///   about someone else's business; the draft writes
///   <see cref="ProfileDraftDefaults.UnspecifiedEntityKind"/> and makes the reviewer say it.
/// - It never promotes a weak measurement. If nothing in the resource identifies the affected
///   customer, that is said HERE, at onboarding, as a blocker - not discovered months later when
///   an incident cannot be attributed to anyone.
///
/// The draft arrives with <c>Enabled = false</c>. Saving it does not switch it on, which keeps
/// "I saved my work" and "this now classifies production traffic" two separate decisions.
/// </summary>
public static class ProfileDraftBuilder
{
    public static CompanyProfileDraft Build(TelemetryResourceProfile report, ProfileDraftOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var opts = (options ?? ProfileDraftOptions.Default).Normalize();

        var warnings = new List<DraftWarning>();
        var proposals = new List<DraftProposal>();

        var alias = (report.Alias ?? "").Trim();
        var profileKey = ResolveProfileKey(opts.ProfileKey, alias);
        var displayName = string.IsNullOrWhiteSpace(opts.DisplayName) ? (alias.Length > 0 ? alias : profileKey) : opts.DisplayName!;

        AddReportStatusWarnings(report, warnings);

        var identitySources = BuildIdentitySources(report, opts, proposals, out var bestUsableCoverage);
        var services = BuildServices(report, alias, opts, proposals, warnings);

        AddAttributionVerdict(report, opts, identitySources, bestUsableCoverage, warnings);
        AddStandingWarnings(identitySources.Count, services.Count, warnings);

        // Carried verbatim rather than summarised: the draft is what the reviewer reads, and the
        // measurement notes that qualify these numbers belong in the same document as the numbers.
        foreach (var note in report.Warnings ?? [])
        {
            warnings.Add(new DraftWarning(DraftWarningSeverity.Info, DraftWarningCode.DiscoveryNote, note));
        }

        var profile = new CompanyProfile(
            ProfileKey: profileKey,
            DisplayName: displayName,
            Labels: NeutralLabels,
            Services: services,
            EntityIdentitySources: identitySources,
            Glossary: NeutralGlossary,
            Version: 1,
            // A draft must not start classifying production traffic because somebody pressed save.
            Enabled: false,
            Notes: BuildNotes(report, alias),
            UpdatedAt: null,
            UpdatedBy: null);

        var ordered = warnings
            .OrderByDescending(w => w.Severity)
            .ToList();

        return new CompanyProfileDraft(
            Profile: profile,
            Proposals: proposals,
            Warnings: ordered,
            NextSteps: BuildNextSteps(identitySources.Count, services.Count),
            Readiness: ordered.Any(w => w.Severity == DraftWarningSeverity.Blocker)
                ? DraftReadiness.Blocked
                : DraftReadiness.NeedsReview,
            CanAttributeIncidents: bestUsableCoverage >= opts.MinUsableCoveragePercent && identitySources.Count > 0,
            SourceAlias: alias,
            SourceEnvironment: report.Environment ?? "",
            WindowFrom: report.WindowFrom,
            WindowTo: report.WindowTo,
            GeneratedAt: report.GeneratedAt);
    }

    /// <summary>
    /// The role nouns themselves, not a vocabulary. Arrivia calls a service a "Vertical" and an
    /// entity a "Club"; writing either of those into another enterprise's draft would be this
    /// platform's first customer leaking into its second.
    /// </summary>
    private static readonly CompanyLabels NeutralLabels = new(
        Service: "Service",
        Flow: "Flow",
        Step: "Step",
        Entity: "Entity",
        Owner: "Owner");

    /// <summary>
    /// <see cref="CompanyGlossary.Description"/> stays null. It is the one field that reaches an
    /// unattended prompt as prose, and nothing in a telemetry probe knows what the product does.
    /// </summary>
    private static readonly CompanyGlossary NeutralGlossary = new(
        ServiceNoun: "service",
        FlowNoun: "flow",
        StepNoun: "step",
        EntityNoun: "entity",
        OwnerNoun: "owner",
        Description: null);

    private static void AddReportStatusWarnings(TelemetryResourceProfile report, List<DraftWarning> warnings)
    {
        switch (report.Status)
        {
            case TelemetryProfileStatus.SourceUnresolved:
                warnings.Add(new DraftWarning(
                    DraftWarningSeverity.Blocker,
                    DraftWarningCode.SourceUnresolved,
                    $"Discovery never reached the telemetry resource, so nothing in this draft was measured: {report.Error}"));
                break;

            case TelemetryProfileStatus.Degraded:
                warnings.Add(new DraftWarning(
                    DraftWarningSeverity.Warning,
                    DraftWarningCode.DiscoveryDegraded,
                    "At least one probe failed, so the absence of a proposal here is not evidence that there was nothing to propose. Re-run discovery before trusting the gaps."));
                break;

            case TelemetryProfileStatus.Empty:
                warnings.Add(new DraftWarning(
                    DraftWarningSeverity.Warning,
                    DraftWarningCode.NoTelemetryInWindow,
                    "Every probed table was empty in the window. Widen the window or pick one that contains real traffic - a draft from an idle window describes nothing."));
                break;
        }
    }

    /// <summary>
    /// Candidates become <see cref="EntityIdentitySource"/> entries in COVERAGE order, so the
    /// reviewer reads them as "this is how much of your telemetry each one can actually speak
    /// for". Every candidate the report measured appears in <paramref name="proposals"/>, including
    /// the ones that were not proposed and the number that disqualified them.
    /// </summary>
    private static IReadOnlyList<EntityIdentitySource> BuildIdentitySources(
        TelemetryResourceProfile report,
        ProfileDraftOptions opts,
        List<DraftProposal> proposals,
        out double bestUsableCoverage)
    {
        bestUsableCoverage = 0.0;

        var ordered = (report.IdentityCandidates ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Key))
            .OrderByDescending(c => c.NonEmptyCoveragePercent)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Table, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        // An EntityIdentitySource names a key, not a table: the same key measured on requests and
        // on pageViews is one rule. Keeping both would produce two identical entries whose only
        // difference - the table - the profile has nowhere to store.
        var alsoOn = new Dictionary<string, List<EntityIdentityCandidate>>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<EntityIdentityCandidate>();
        foreach (var candidate in ordered)
        {
            var key = candidate.Key.Trim();
            if (alsoOn.TryGetValue(key, out var duplicates)) duplicates.Add(candidate);
            else
            {
                alsoOn[key] = [];
                deduped.Add(candidate);
            }
        }

        var sources = new List<EntityIdentitySource>();
        foreach (var candidate in deduped)
        {
            var key = candidate.Key.Trim();
            var evidence = new List<string>();
            var rejection = Disqualify(candidate, opts.MinProposableCoveragePercent);

            if (rejection is not null) evidence.Add(rejection);
            evidence.AddRange(candidate.Reasons ?? []);
            foreach (var duplicate in alsoOn[key])
            {
                evidence.Add($"Also measured on {duplicate.Table} at {duplicate.NonEmptyCoveragePercent:0.##}% - one rule covers both tables.");
            }

            var accepted = rejection is null && sources.Count < opts.MaxIdentitySources;
            if (rejection is null && !accepted)
            {
                evidence.Insert(0, $"Eligible, but the draft already carries {opts.MaxIdentitySources} identity sources; add it by hand if the ones above do not cover your traffic.");
            }

            if (accepted)
            {
                sources.Add(new EntityIdentitySource(
                    Order: sources.Count + 1,
                    Kind: string.IsNullOrWhiteSpace(candidate.Kind) ? EntityIdentitySourceKind.CustomDimension : candidate.Kind.Trim(),
                    Key: key,
                    EntityKind: ProfileDraftDefaults.UnspecifiedEntityKind));

                if (candidate.NonEmptyCoveragePercent > bestUsableCoverage) bestUsableCoverage = candidate.NonEmptyCoveragePercent;
            }

            proposals.Add(new DraftProposal(
                Kind: DraftProposalKind.EntityIdentitySource,
                Target: accepted ? $"entityIdentitySources[{sources.Count - 1}]" : "",
                Value: key,
                Proposed: accepted,
                CoveragePercent: candidate.NonEmptyCoveragePercent,
                Score: candidate.Score,
                Evidence: evidence));
        }

        return sources;
    }

    /// <summary>
    /// Why a measured key is not worth putting in front of a reviewer. Each of these is a
    /// measurement, not a preference - which is what makes dropping the key defensible, and why
    /// the number that dropped it travels with the rejection.
    /// </summary>
    private static string? Disqualify(EntityIdentityCandidate candidate, double minProposableCoverage)
    {
        if (candidate.LooksFreeText)
        {
            return $"Not proposed: values run to {candidate.MaxValueLength} characters, which is prose rather than an identifier.";
        }
        if (candidate.NonEmptyCount <= 0)
        {
            return "Not proposed: the key never carries a non-empty value, so it identifies nobody.";
        }
        if (candidate.DistinctValueCount <= 1)
        {
            return "Not proposed: one distinct value only - a constant cannot tell one customer from another.";
        }
        if (candidate.NonEmptyCoveragePercent < minProposableCoverage)
        {
            return $"Not proposed: {candidate.NonEmptyCoveragePercent:0.##}% coverage is below the {minProposableCoverage:0.##}% floor for a proposal.";
        }
        return null;
    }

    /// <summary>
    /// Services come from what the resource was OBSERVED to run: cloud_RoleNames when there are
    /// any, hosts otherwise. Role names win because a role name is a deployable thing and a host
    /// is often a brand - but both are copied verbatim, so no name in the draft is one this code
    /// made up.
    ///
    /// When services came from role names the observed hosts are reported as unassigned
    /// candidates rather than distributed across them: which role serves which host is not in
    /// this report, and a wrong host filter silently empties a board.
    /// </summary>
    private static IReadOnlyList<ServiceDefinition> BuildServices(
        TelemetryResourceProfile report,
        string alias,
        ProfileDraftOptions opts,
        List<DraftProposal> proposals,
        List<DraftWarning> warnings)
    {
        var roleNames = (report.Hosts?.RoleNames ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.RoleName))
            .ToList();
        var hosts = (report.Hosts?.Hosts ?? [])
            .Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Host))
            .ToList();

        string[] aliases = TelemetrySourceAlias.IsValid(alias) ? [alias] : [];
        var services = new List<ServiceDefinition>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (roleNames.Count > 0)
        {
            foreach (var role in roleNames.Take(opts.MaxServices))
            {
                var name = role.RoleName.Trim();
                services.Add(new ServiceDefinition(
                    Key: UniqueKey(name, services.Count, usedKeys),
                    DisplayName: name,
                    TelemetrySourceAliases: aliases,
                    // Left empty: "do not filter by host" is the honest default when nothing
                    // measured says which host this role answers on.
                    HostFilters: [],
                    Flows: [],
                    FallbackStep: null));

                proposals.Add(new DraftProposal(
                    Kind: DraftProposalKind.Service,
                    Target: $"services[{services.Count - 1}]",
                    Value: name,
                    Proposed: true,
                    CoveragePercent: null,
                    Score: null,
                    Evidence:
                    [
                        $"Observed as cloud_RoleName on {role.ObservationCount:N0} sampled rows in {alias}.",
                        "Name copied from the telemetry verbatim; rename it to whatever the business calls it.",
                        "No flows or steps were proposed - see the draft warnings.",
                    ]));
            }

            foreach (var host in hosts.Take(ProfileDraftDefaults.MaxHostProposals))
            {
                proposals.Add(new DraftProposal(
                    Kind: DraftProposalKind.HostFilter,
                    Target: "",
                    Value: host.Host.Trim(),
                    Proposed: false,
                    CoveragePercent: null,
                    Score: null,
                    Evidence:
                    [
                        "Not proposed: which service answers on this host is not measurable from this report, and a wrong host filter empties a board without erroring.",
                        $"Observed on {host.ObservationCount:N0} sampled rows.",
                    ]));
            }

            if (hosts.Count > 0)
            {
                warnings.Add(new DraftWarning(
                    DraftWarningSeverity.Warning,
                    DraftWarningCode.HostsNeedAssignment,
                    $"{hosts.Count} host(s) were observed but none were assigned to a service, because nothing measured says which role answers on which host. Add them to the right service's hostFilters by hand, or leave hostFilters empty to accept traffic from every host."));
            }
        }
        else if (hosts.Count > 0)
        {
            foreach (var host in hosts.Take(opts.MaxServices))
            {
                var name = host.Host.Trim();
                services.Add(new ServiceDefinition(
                    Key: UniqueKey(name, services.Count, usedKeys),
                    DisplayName: name,
                    TelemetrySourceAliases: aliases,
                    HostFilters: [name],
                    Flows: [],
                    FallbackStep: null));

                proposals.Add(new DraftProposal(
                    Kind: DraftProposalKind.Service,
                    Target: $"services[{services.Count - 1}]",
                    Value: name,
                    Proposed: true,
                    CoveragePercent: null,
                    Score: null,
                    Evidence:
                    [
                        $"Observed as a request/pageView host on {host.ObservationCount:N0} sampled rows in {alias}; no cloud_RoleName was observed at all, so the host is all this resource offers.",
                        "One service per host is a starting point, not a finding - merge or split them to match how the business is actually divided.",
                        "No flows or steps were proposed - see the draft warnings.",
                    ]));
            }
        }
        else
        {
            warnings.Add(new DraftWarning(
                DraftWarningSeverity.Warning,
                DraftWarningCode.NoServicesProposed,
                "No cloud_RoleName and no host was observed, so no service could be proposed. Add services by hand, or re-run discovery over a window that contains traffic."));
        }

        return services;
    }

    private static void AddAttributionVerdict(
        TelemetryResourceProfile report,
        ProfileDraftOptions opts,
        IReadOnlyList<EntityIdentitySource> sources,
        double bestUsableCoverage,
        List<DraftWarning> warnings)
    {
        if (report.Status == TelemetryProfileStatus.SourceUnresolved) return;

        var best = (report.IdentityCandidates ?? [])
            .OrderByDescending(c => c.NonEmptyCoveragePercent)
            .FirstOrDefault();

        if (sources.Count == 0)
        {
            var detail = best is null
                ? "no customDimensions key was measured on any probed table"
                : $"the best key measured, \"{best.Key}\" on {best.Table}, is not usable as an identifier ({best.NonEmptyCoveragePercent:0.##}% coverage, {best.DistinctValueCount:N0} distinct values, longest value {best.MaxValueLength} characters)";

            warnings.Add(new DraftWarning(
                DraftWarningSeverity.Blocker,
                DraftWarningCode.CannotAttributeIncidents,
                $"This enterprise cannot attribute incidents to a customer from this telemetry resource: {detail}. " +
                "Nothing downstream - ownership, Red Alert, the entity breakdown - can work until the application stamps an identifier on its telemetry. " +
                "Fix that first; mapping steps on top of unattributable telemetry buys nothing."));
            return;
        }

        if (bestUsableCoverage < opts.MinUsableCoveragePercent)
        {
            warnings.Add(new DraftWarning(
                DraftWarningSeverity.Blocker,
                DraftWarningCode.CannotAttributeIncidents,
                $"This enterprise cannot attribute most incidents to a customer: the best proposed identity source covers only {bestUsableCoverage:0.##}% of sampled rows, under the {opts.MinUsableCoveragePercent:0.##}% usable floor. " +
                $"Roughly {Math.Max(0.0, 100.0 - bestUsableCoverage):0.##}% of this resource's telemetry would stay unattributed, which is what makes an incident impossible to blame on anyone. " +
                "Accepting this draft means accepting that number."));
        }
    }

    private static void AddStandingWarnings(int identitySourceCount, int serviceCount, List<DraftWarning> warnings)
    {
        warnings.Add(new DraftWarning(
            DraftWarningSeverity.Warning,
            DraftWarningCode.NoStepsProposed,
            $"This draft contains no flows and no steps - {serviceCount} service(s) with empty step lists. Nothing is classified until a human writes them. " +
            "That is deliberate: a guessed funnel looks finished, so nobody checks it, and every board built on it is confidently wrong."));

        if (identitySourceCount > 0)
        {
            warnings.Add(new DraftWarning(
                DraftWarningSeverity.Warning,
                DraftWarningCode.EntityKindUnspecified,
                $"Every proposed identity source carries entityKind \"{ProfileDraftDefaults.UnspecifiedEntityKind}\". What the value identifies - a club, a tenant, a partner, a merchant - is a judgement about your business, not something a probe can measure. Name it before you enable the profile."));
        }

        warnings.Add(new DraftWarning(
            DraftWarningSeverity.Warning,
            DraftWarningCode.LabelsArePlaceholders,
            "labels and glossary are role nouns (\"Service\", \"Step\", \"Entity\"), not your vocabulary, and glossary.description is empty. They reach the UI and the agent prompts; set them to the words your teams actually use."));

        warnings.Add(new DraftWarning(
            DraftWarningSeverity.Info,
            DraftWarningCode.ProfileDisabled,
            "enabled=false. Saving this draft does not switch it on - set enabled=true in a second, separate PUT once the mapping has been reviewed."));
    }

    private static IReadOnlyList<string> BuildNextSteps(int identitySourceCount, int serviceCount) =>
    [
        serviceCount > 0
            ? "1. Review the proposed services: rename them to the business's own names, merge or split them, and assign hostFilters if one service answers on a specific host."
            : "1. Add the services by hand - discovery observed no role name and no host to propose one from.",
        identitySourceCount > 0
            ? $"2. Review every entityIdentitySource: confirm the key really identifies a customer, replace entityKind \"{ProfileDraftDefaults.UnspecifiedEntityKind}\" with what the value means, and reorder them so the most reliable one is order 1."
            : "2. There is no entityIdentitySource to review, because nothing measured can identify an affected customer. Get the application to stamp one before going further.",
        "3. Write the flows and steps. Each step needs a unique order within its flow (first match wins) and at least one matcher. Nothing in this draft guessed them.",
        "4. Set labels and glossary to the enterprise's vocabulary, and write glossary.description - it is what tells the agents what this product is.",
        "5. PUT the reviewed profile to /api/settings/company-profiles/{key} with the X-Settings-Write-Secret header.",
        "6. Verify with GET /api/settings/company-profiles/{key}, then enable it with a second PUT carrying enabled=true.",
    ];

    private static string BuildNotes(TelemetryResourceProfile report, string alias) =>
        $"Draft generated from telemetry discovery of \"{alias}\" ({report.Environment}) over the " +
        $"{report.WindowHours}h window ending {report.WindowTo:u}, status {report.Status}. " +
        "Measurement only: no flow, step, business name or entity kind was inferred, and no model was called. " +
        "Every proposal carries the measurement behind it in the draft response. Review before enabling.";

    private static string ResolveProfileKey(string? requested, string alias)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested!.Trim();
        var slug = Slug(alias);
        return slug.Length > 0 ? slug : "draft";
    }

    private static string UniqueKey(string name, int index, HashSet<string> used)
    {
        var slug = Slug(name);
        if (slug.Length == 0) slug = $"service-{index + 1}";

        var candidate = slug;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{slug}-{suffix++}";
        }
        return candidate;
    }

    /// <summary>
    /// Keys reach URLs, SQL keys and dedup keys, so they are reduced to the character class every
    /// one of those accepts. The DISPLAY name keeps the observed string untouched - the slug is a
    /// handle, not a rename.
    /// </summary>
    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length <= 64 ? slug : slug[..64].Trim('-');
    }
}
