using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// Every finding the validator can raise, raised on purpose.
///
/// The documents below are synthetic and anonymous: this file is about the RULES, and a validator
/// test written against the pilot customer would quietly turn into a second description of that
/// customer - which is the coupling the whole effort exists to remove. Arrivia is asserted on in
/// ArriviaProfileSeedTests, exactly once, against the seed that reaches the database.
/// </summary>
public class CompanyProfileValidatorTests
{
    // -------------------------------------------------------------------------------------
    // Building blocks
    // -------------------------------------------------------------------------------------

    private static StepMatcher Url(string value) => new(StepMatcherKind.UrlContains, value);

    private static StepDefinition Step(string key, int order, params StepMatcher[] matchers) =>
        new(key, key.ToUpperInvariant(), order, matchers);

    private static ServiceDefinition Service(
        string key, string? fallbackStep = null, params StepDefinition[] steps) =>
        new(
            key,
            key.ToUpperInvariant(),
            ["source-alias"],
            [],
            [new FlowDefinition("flow", "Flow", steps.Length == 0 ? [Step("step-a", 1, Url("/a"))] : steps)],
            fallbackStep);

    private static CompanyProfile Profile(
        IReadOnlyList<ServiceDefinition>? services = null,
        IReadOnlyList<EntityIdentitySource>? identity = null) =>
        new(
            "subject",
            "Subject",
            new CompanyLabels("Product area", "Journey", "Step", "Account", "Owning team"),
            services ?? [Service("alpha", null, Step("step-a", 1, Url("/a")))],
            identity ?? [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "AccountId", "account")],
            new CompanyGlossary("product area", "journey", "step", "account", "owning team", "A synthetic company."));

    private static IReadOnlyList<string> Codes(CompanyProfile? profile) =>
        CompanyProfileValidator.Validate(profile).Select(f => f.Code).ToList();

    private static CompanyProfileFinding Single(CompanyProfile? profile, string code)
    {
        var findings = CompanyProfileValidator.Validate(profile);
        var hit = findings.SingleOrDefault(f => f.Code == code);

        Assert.True(hit is not null,
            $"Expected exactly one '{code}' finding. Got: " +
            (findings.Count == 0 ? "(none)" : string.Join("; ", findings.Select(f => $"{f.Code}@{f.Path}"))));

        return hit!;
    }

    // -------------------------------------------------------------------------------------
    // A good document raises nothing
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_well_formed_profile_produces_no_findings()
    {
        Assert.Empty(CompanyProfileValidator.Validate(Profile()));
    }

    /// <summary>A document that failed to parse must not be reported as a valid empty one.</summary>
    [Fact]
    public void A_null_profile_is_an_error_rather_than_an_exception()
    {
        var finding = Single(null, "profile-missing");

        Assert.True(finding.IsError);
        Assert.Contains("camelCase", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------
    // Keys
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_service_keys_are_an_error_and_name_the_other_one()
    {
        var finding = Single(
            Profile([Service("alpha", null, Step("s1", 1, Url("/a"))), Service("ALPHA", null, Step("s2", 2, Url("/b")))]),
            "service-key-duplicate");

        Assert.True(finding.IsError);
        Assert.Equal("$.services[1].key", finding.Path);
        Assert.Contains("$.services[0]", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Rename", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>Across flows as well as within one: the step key is unique per SERVICE, which is what every store keys on.</summary>
    [Fact]
    public void Duplicate_step_keys_within_a_service_are_an_error()
    {
        var service = new ServiceDefinition(
            "alpha", "Alpha", ["source-alias"], [],
            [
                new FlowDefinition("first", "First", [Step("shared-key", 1, Url("/a"))]),
                new FlowDefinition("second", "Second", [Step("shared-key", 2, Url("/b"))]),
            ]);

        var finding = Single(Profile([service]), "step-key-duplicate");

        Assert.True(finding.IsError);
        Assert.Contains("unique per service", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>Two services may reuse a step key - the service qualifies it - so this must NOT fire.</summary>
    [Fact]
    public void The_same_step_key_in_two_different_services_is_fine()
    {
        var codes = Codes(Profile(
        [
            Service("alpha", null, Step("05-prebook", 1, Url("/a"))),
            Service("beta", null, Step("05-prebook", 2, Url("/b"))),
        ]));

        Assert.DoesNotContain("step-key-duplicate", codes);
        Assert.Empty(codes);
    }

    [Fact]
    public void Duplicate_flow_keys_within_a_service_are_an_error()
    {
        var service = new ServiceDefinition(
            "alpha", "Alpha", ["source-alias"], [],
            [
                new FlowDefinition("flow", "First", [Step("s1", 1, Url("/a"))]),
                new FlowDefinition("flow", "Second", [Step("s2", 2, Url("/b"))]),
            ]);

        Assert.True(Single(Profile([service]), "flow-key-duplicate").IsError);
    }

    // -------------------------------------------------------------------------------------
    // Fallbacks
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_fallback_naming_no_declared_step_is_an_error_and_lists_the_real_options()
    {
        var finding = Single(
            Profile([Service("alpha", "step-that-does-not-exist", Step("step-a", 1, Url("/a")), Step("step-b", 2, Url("/b")))]),
            "fallback-step-unknown");

        Assert.True(finding.IsError);
        Assert.Contains("step-a, step-b", finding.Message, StringComparison.Ordinal);
        Assert.Contains("set it to null", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fallback_naming_a_declared_step_is_accepted()
    {
        Assert.Empty(CompanyProfileValidator.Validate(
            Profile([Service("alpha", "step-a", Step("step-a", 1, Url("/a")))])));
    }

    /// <summary>
    /// Two eligible catch-alls cancel each other out in ProfileDrivenAdapter.Fallback and the row
    /// resolves to NOTHING - a profile that looks more complete than one with a single fallback,
    /// while classifying strictly less traffic. It has to be caught in the document.
    /// </summary>
    [Fact]
    public void Two_services_that_both_declare_a_fallback_are_an_error()
    {
        var finding = Single(
            Profile(
            [
                Service("alpha", "step-a", Step("step-a", 1, Url("/a"))),
                Service("beta", "step-b", Step("step-b", 2, Url("/b"))),
            ]),
            "fallback-step-ambiguous");

        Assert.True(finding.IsError);
        Assert.Contains("alpha", finding.Message, StringComparison.Ordinal);
        Assert.Contains("beta", finding.Message, StringComparison.Ordinal);
        Assert.Contains("resolves to NOTHING", finding.Message, StringComparison.Ordinal);
        Assert.Contains("hostFilters", finding.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------
    // Matchers
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_step_with_no_matchers_is_an_error()
    {
        var finding = Single(Profile([Service("alpha", null, Step("step-a", 1))]), "step-matchers-empty");

        Assert.True(finding.IsError);
        Assert.Equal("$.services[0].flows[0].steps[0].matchers", finding.Path);
        Assert.Contains("never be reached", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_matcher_kind_is_an_error_and_lists_the_kinds_that_work()
    {
        var finding = Single(
            Profile([Service("alpha", null, Step("step-a", 1, new StepMatcher("url-endswith", "/a")))]),
            "matcher-kind-unknown");

        Assert.True(finding.IsError);
        Assert.Contains(StepMatcherKind.UrlContains, finding.Message, StringComparison.Ordinal);
        Assert.Contains(StepMatcherKind.Regex, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matcher_with_no_value_is_an_error()
    {
        Assert.True(Single(
            Profile([Service("alpha", null, Step("step-a", 1, new StepMatcher(StepMatcherKind.UrlContains, "  ")))]),
            "matcher-value-missing").IsError);
    }

    /// <summary>
    /// ProfileDrivenAdapter swallows an uncompilable pattern and the step simply never fires, which
    /// is indistinguishable from traffic that never happened. The document is where it is visible.
    /// </summary>
    [Fact]
    public void An_invalid_regex_pattern_is_an_error()
    {
        var finding = Single(
            Profile([Service("alpha", null, Step("step-a", 1, new StepMatcher(StepMatcherKind.Regex, "/checkout/(")))]),
            "matcher-regex-invalid");

        Assert.True(finding.IsError);
        Assert.Contains("quietly never fires", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_regex_pattern_is_accepted()
    {
        Assert.Empty(CompanyProfileValidator.Validate(
            Profile([Service("alpha", null, Step("step-a", 1, new StepMatcher(StepMatcherKind.Regex, @"/checkout/\d+")))])));
    }

    // -------------------------------------------------------------------------------------
    // Shape
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_profile_with_no_services_is_an_error()
    {
        Assert.True(Single(Profile([]), "services-empty").IsError);
    }

    [Fact]
    public void A_service_with_no_flows_is_an_error()
    {
        var service = new ServiceDefinition("alpha", "Alpha", ["source-alias"], [], []);

        Assert.True(Single(Profile([service]), "service-flows-empty").IsError);
    }

    [Fact]
    public void A_flow_with_no_steps_is_an_error()
    {
        var service = new ServiceDefinition(
            "alpha", "Alpha", ["source-alias"], [], [new FlowDefinition("flow", "Flow", [])]);

        Assert.True(Single(Profile([service]), "flow-steps-empty").IsError);
    }

    /// <summary>Order decides who wins an overlap, so a tie leaves the outcome to array order - worth saying out loud.</summary>
    [Fact]
    public void Two_steps_sharing_an_order_within_a_service_are_a_warning()
    {
        var finding = Single(
            Profile([Service("alpha", null, Step("step-a", 7, Url("/a")), Step("step-b", 7, Url("/b")))]),
            "step-order-duplicate");

        Assert.False(finding.IsError);
        Assert.Equal(CompanyProfileSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void A_service_with_no_telemetry_source_alias_is_a_warning()
    {
        var service = new ServiceDefinition(
            "alpha", "Alpha", [], [], [new FlowDefinition("flow", "Flow", [Step("step-a", 1, Url("/a"))])]);

        var finding = Single(Profile([service]), "service-telemetry-aliases-empty");

        Assert.False(finding.IsError);
    }

    // -------------------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_profile_with_no_identity_sources_is_an_error()
    {
        var finding = Single(Profile(identity: []), "identity-sources-empty");

        Assert.True(finding.IsError);
        Assert.Contains("for whom", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_identity_source_kind_is_an_error_and_lists_the_kinds_that_work()
    {
        var finding = Single(
            Profile(identity: [new EntityIdentitySource(1, "http-header", "X-Account", "account")]),
            "identity-kind-unknown");

        Assert.True(finding.IsError);
        Assert.Contains(EntityIdentitySourceKind.CustomDimension, finding.Message, StringComparison.Ordinal);
        Assert.Contains(EntityIdentitySourceKind.Host, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_dimension_source_with_no_field_name_is_an_error()
    {
        var finding = Single(
            Profile(identity: [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "", "account")]),
            "identity-key-missing");

        Assert.True(finding.IsError);
        Assert.Contains(EntityIdentitySourceKind.Host, finding.Message, StringComparison.Ordinal);
    }

    /// <summary>The host source carries no field name by design, so the empty-key rule must not fire on it.</summary>
    [Fact]
    public void A_host_source_with_no_field_name_is_fine()
    {
        Assert.Empty(CompanyProfileValidator.Validate(
            Profile(identity: [new EntityIdentitySource(1, EntityIdentitySourceKind.Host, "", "host")])));
    }

    [Fact]
    public void An_identity_source_that_repeats_an_earlier_one_is_a_warning()
    {
        var codes = Codes(Profile(identity:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "AccountId", "account"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.CustomDimension, "accountid", "account"),
        ]));

        Assert.Contains("identity-source-duplicate", codes);
        Assert.DoesNotContain("identity-order-duplicate", codes);
    }

    [Fact]
    public void Two_identity_sources_sharing_an_order_are_a_warning()
    {
        var finding = Single(
            Profile(identity:
            [
                new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "AccountId", "account"),
                new EntityIdentitySource(1, EntityIdentitySourceKind.QueryParameter, "aid", "account"),
            ]),
            "identity-order-duplicate");

        Assert.False(finding.IsError);
    }

    // -------------------------------------------------------------------------------------
    // Vocabulary
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_blank_label_is_an_error()
    {
        var profile = Profile() with { Labels = new CompanyLabels("Product area", "", "Step", "Account", "Owning team") };

        var finding = Single(profile, "label-missing");

        Assert.True(finding.IsError);
        Assert.Equal("$.labels.flow", finding.Path);
    }

    [Fact]
    public void A_blank_glossary_noun_is_an_error()
    {
        var profile = Profile() with
        {
            Glossary = new CompanyGlossary("product area", "journey", "step", "  ", "owning team", "A synthetic company."),
        };

        var finding = Single(profile, "glossary-noun-missing");

        Assert.True(finding.IsError);
        Assert.Equal("$.glossary.entityNoun", finding.Path);
        Assert.Contains("unattended prompts", finding.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------
    // Reporting, not throwing
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The contract that makes this usable by an editor: a thoroughly broken document comes back as
    /// a LIST. Throwing on the first defect would make whoever onboards a customer fix one thing
    /// per save, and they would never see that the document has eight problems, not one.
    /// </summary>
    [Fact]
    public void A_thoroughly_broken_profile_reports_every_defect_at_once()
    {
        var broken = new CompanyProfile(
            "",
            "",
            new CompanyLabels("", "", "", "", ""),
            [
                Service("alpha", "nowhere", Step("step-a", 1), Step("step-a", 1, new StepMatcher("no-such-kind", "/a"))),
                Service("alpha", "step-b", Step("step-b", 2, new StepMatcher(StepMatcherKind.Regex, "(["))),
            ],
            [new EntityIdentitySource(1, "no-such-kind", "", "")],
            new CompanyGlossary("", "", "", "", ""));

        var findings = CompanyProfileValidator.Validate(broken);
        var codes = findings.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("profile-key-missing", codes);
        Assert.Contains("label-missing", codes);
        Assert.Contains("glossary-noun-missing", codes);
        Assert.Contains("service-key-duplicate", codes);
        Assert.Contains("step-key-duplicate", codes);
        Assert.Contains("step-matchers-empty", codes);
        Assert.Contains("matcher-kind-unknown", codes);
        Assert.Contains("matcher-regex-invalid", codes);
        Assert.Contains("fallback-step-unknown", codes);
        Assert.Contains("fallback-step-ambiguous", codes);
        Assert.Contains("identity-kind-unknown", codes);
        Assert.Contains("identity-entity-kind-missing", codes);

        Assert.True(CompanyProfileValidator.HasErrors(findings));

        // Every finding has to be actionable, or the list is just a longer way of saying "no".
        Assert.All(findings, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Path));
            Assert.True(f.Message.Length > 40, $"Finding '{f.Code}' does not say what to do: {f.Message}");
        });
    }

    /// <summary>
    /// System.Text.Json happily fills non-nullable members with null, so the validator is handed
    /// those documents in production. It must report them, never throw on them.
    /// </summary>
    [Fact]
    public void A_profile_whose_members_deserialised_to_null_is_reported_not_thrown()
    {
        var hollow = new CompanyProfile("subject", "Subject", null!, null!, null!, null!);

        var codes = Codes(hollow);

        Assert.Contains("labels-missing", codes);
        Assert.Contains("glossary-missing", codes);
        Assert.Contains("services-empty", codes);
        Assert.Contains("identity-sources-empty", codes);
    }
}
