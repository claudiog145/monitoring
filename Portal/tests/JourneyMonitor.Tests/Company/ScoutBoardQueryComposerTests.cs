using System.Text;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Infrastructure.Kql;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// The contract for composing a whole board query out of a company profile.
///
/// The load-bearing test here is <see cref="Cascade_AndAdapter_AgreeOnEveryRow"/>. The board is
/// rendered from this KQL and the case file is classified by <see cref="ProfileDrivenAdapter"/>;
/// if they disagree, nothing fails - the platform simply tells two different stories about the
/// same request. Read the remark on that test for exactly how strong the guarantee is, because it
/// is not "the KQL was executed".
/// </summary>
public class ScoutBoardQueryComposerTests
{
    // ---------------------------------------------------------------- the composed query

    [Fact]
    public void Template_IsEmbedded_AndStillCarriesEveryPlaceholderTheComposerFills()
    {
        Assert.Contains(ScoutBoardQueryComposer.TemplateResourceName,
            typeof(KqlQueryTextProvider).Assembly.GetManifestResourceNames());

        var template = Template();
        foreach (var placeholder in KqlPlaceholders.CompanyProfilePlaceholders.Concat(KqlPlaceholders.BoardRequestPlaceholders))
        {
            Assert.Contains(placeholder, template, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_LeavesNothingUnsubstituted_ExceptTheSourceAliases()
    {
        var kql = Compose(CompanyKqlTemplaterTests.Profile());

        // The guard the composer itself runs; asserted again here so that weakening it is a failure.
        KqlPlaceholders.AssertNoneLeft(kql, "composed board");
        Assert.Contains("app(\"{{SOURCE:shop-web}}\").requests", kql, StringComparison.Ordinal);
        Assert.Contains("app(\"{{SOURCE:shop-web}}\").exceptions", kql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_IsDeterministic()
    {
        var profile = AgreementProfile();
        var request = Request(ScoutBoardKind.Latency);

        Assert.Equal(
            ScoutBoardQueryComposer.Compose(profile, request, Template()),
            ScoutBoardQueryComposer.Compose(profile, request, Template()));
    }

    [Fact]
    public void Compose_SubstitutesTheWindowTheGrainAndTheRowCap()
    {
        var kql = ScoutBoardQueryComposer.Compose(
            CompanyKqlTemplaterTests.Profile(),
            new ScoutBoardQueryRequest(
                ScoutBoardKind.Latency,
                new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(-3)),
                new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(-3)),
                BucketMinutes: 15,
                RowsPerEntity: 4),
            Template());

        Assert.Contains("let currentStart=datetime(2026-10-17T12:00:00.000Z);", kql, StringComparison.Ordinal);
        Assert.Contains("let tEnd=datetime(2026-10-17T13:00:00.000Z);", kql, StringComparison.Ordinal);
        Assert.Contains("let bucketSize=15*1m;", kql, StringComparison.Ordinal);
        Assert.Contains("| take 4)", kql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_RanksLatencyAndExceptionBoardsDifferently_OnIdenticalMachinery()
    {
        var profile = CompanyKqlTemplaterTests.Profile();
        var latency = Compose(profile, ScoutBoardKind.Latency);
        var exception = Compose(profile, ScoutBoardKind.Exception);

        Assert.Contains("| where Requests>0", latency, StringComparison.Ordinal);
        Assert.Contains("sort by PriorityRank desc,P95Ms desc", latency, StringComparison.Ordinal);
        Assert.Contains("| where Exceptions>0", exception, StringComparison.Ordinal);
        Assert.Contains("sort by PriorityRank desc,Exceptions desc", exception, StringComparison.Ordinal);

        // Everything either side of the ranking is the same query.
        foreach (var invariant in new[] { "partition hint.strategy=native by Entity", "| extend MatchIndex=case(", "bag_pack(\"CaseId\"" })
        {
            Assert.Contains(invariant, latency, StringComparison.Ordinal);
            Assert.Contains(invariant, exception, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_WithNoHostFilters_FiltersNothingOut()
    {
        var profile = CompanyKqlTemplaterTests.Profile() with
        {
            Services =
            [
                CompanyKqlTemplaterTests.Checkout() with { HostFilters = [] },
                CompanyKqlTemplaterTests.Catalog() with { HostFilters = [] }
            ]
        };

        var kql = Compose(profile);

        Assert.Contains("| where true", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("Host =~", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("| where false", kql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_EscapesProfileAndRequestValues_SoNeitherCanBreakOutOfALiteral()
    {
        const string payload = "x\" | take 1 | project Priority=\"owned";
        var profile = CompanyKqlTemplaterTests.Profile() with
        {
            Services =
            [
                CompanyKqlTemplaterTests.Checkout() with
                {
                    DisplayName = payload,
                    FallbackStep = "back\\slash\"ed",
                    Flows = [new FlowDefinition("purchase", "Purchase", [
                        new StepDefinition("cart", payload, 1, [new StepMatcher(StepMatcherKind.UrlContains, payload)])
                    ])]
                }
            ],
            EntityIdentitySources = [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, payload, "tenant")]
        };

        var kql = ScoutBoardQueryComposer.Compose(
            profile,
            Request(ScoutBoardKind.Latency) with { ServiceFilter = payload, EntityFilter = "y\\\" or true" },
            Template());

        // Not once does the payload close its literal early.
        Assert.DoesNotContain(payload, kql, StringComparison.Ordinal);
        Assert.Contains("url contains \"x\\\" | take 1 | project Priority=\\\"owned\"", kql, StringComparison.Ordinal);
        Assert.Contains("let serviceFilter=\"x\\\" | take 1 | project Priority=\\\"owned\";", kql, StringComparison.Ordinal);
        Assert.Contains("let entityFilter=\"y\\\\\\\" or true\";", kql, StringComparison.Ordinal);
        Assert.Contains("\"back\\\\slash\\\"ed\"", kql, StringComparison.Ordinal);
        Assert.Equal(0, CompanyKqlTemplaterTests.UnescapedQuoteCount(StripComments(kql)) % 2);
    }

    [Fact]
    public void Compose_RejectsAnEmptyTemplate() =>
        Assert.Throws<ArgumentException>(() =>
            ScoutBoardQueryComposer.Compose(CompanyKqlTemplaterTests.Profile(), Request(ScoutBoardKind.Latency), "   "));

    // ---------------------------------------------------------------- composer <-> adapter

    /// <summary>
    /// The composed cascade and <see cref="ProfileDrivenAdapter.ResolveStep"/> have to reach the
    /// same verdict for the same row.
    ///
    /// HOW STRONG THIS IS, honestly: no KQL engine runs here. The test walks the ORDERED RULES the
    /// composer emits - the same list the <c>case()</c> is written from - and applies the KQL
    /// operator semantics by hand. So it does prove the parts that actually drift: the evaluation
    /// order across services, which service a matched arm names, host narrowing, the arity rule for
    /// catch-alls, and that an unreachable step is dropped from both. It does NOT prove that
    /// Application Insights' <c>contains</c>, <c>=~</c>, <c>endswith</c> and <c>matches regex</c>
    /// behave like the .NET operations below. <see cref="Cascade_TargetsTheColumnEachMatcherKindClaims"/>
    /// pins the other half - which column each kind is emitted against - so the remaining gap is
    /// the operators themselves and RE2-vs-.NET regex dialect.
    /// </summary>
    [Fact]
    public void Cascade_AndAdapter_AgreeOnEveryRow()
    {
        var profile = AgreementProfile();
        var adapter = new ProfileDrivenAdapter(profile);
        var rules = ScoutBoardQueryComposer.ComposeRules(profile);

        var disagreements = new List<string>();
        foreach (var row in Rows())
        {
            var expected = adapter.ResolveStep(row);
            var actual = Pick(rules, row);

            var expectedPair = (expected.IsResolved ? expected.Service : null, expected.IsResolved ? expected.Step : null);
            var actualPair = (actual?.ServiceKey, actual?.StepKey);

            if (expectedPair != actualPair)
            {
                disagreements.Add($"{row.Url ?? "(no url)"} / {row.Operation ?? "(no operation)"} / {row.PageName ?? "(no page)"}: " +
                                  $"adapter={expectedPair}, cascade={actualPair}");
            }
        }

        Assert.Empty(disagreements);
    }

    [Fact]
    public void Cascade_CoversEveryBranchWorthDisagreeingAbout()
    {
        // Without this, the agreement test above could pass by classifying nothing at all.
        var adapter = new ProfileDrivenAdapter(AgreementProfile());
        var verdicts = Rows().Select(adapter.ResolveStep).ToList();

        Assert.Contains(verdicts, v => v.Service == "alpha" && v.ResolvedBy == CompanyResolution.PatternMatch);
        Assert.Contains(verdicts, v => v.Service == "beta");
        Assert.Contains(verdicts, v => v.ResolvedBy == CompanyResolution.FallbackWildcard);
        Assert.Contains(verdicts, v => !v.IsResolved);
        // Two services behind one hostname, separated only by which step matched.
        Assert.Contains(verdicts, v => v.Service == "beta" && v.Step == "beta-search");
    }

    [Fact]
    public void Cascade_TargetsTheColumnEachMatcherKindClaims()
    {
        var rules = ScoutBoardQueryComposer.ComposeRules(AgreementProfile());

        Assert.Contains("url contains \"/login\"", Predicate(rules, "alpha-login"), StringComparison.Ordinal);
        Assert.Contains("trim(@\"\\s+\",name) =~ \"GET /alpha/pay\"", Predicate(rules, "alpha-pay"), StringComparison.Ordinal);
        Assert.Contains("PageName contains \"Beta Cart\"", Predicate(rules, "beta-cart"), StringComparison.Ordinal);
        Assert.Contains("name contains \"beta-api\"", Predicate(rules, "beta-api"), StringComparison.Ordinal);
        Assert.Contains("url matches regex \"(?i)/alpha/\\\\d+/detail\"", Predicate(rules, "alpha-detail"), StringComparison.Ordinal);

        // alpha is host-scoped and beta is not, exactly as the profile says.
        Assert.StartsWith("(Host =~ \"alpha.example.test\" or Host endswith \".alpha.example.test\") and",
            Predicate(rules, "alpha-login"), StringComparison.Ordinal);
        Assert.StartsWith("PageName contains", Predicate(rules, "beta-cart"), StringComparison.Ordinal);
    }

    // ================================================================ the KQL semantics, by hand

    /// <summary>Which arm of the emitted <c>case()</c> fires for a row, following the generated cascade.</summary>
    private static ComposedRule? Pick(IReadOnlyList<ComposedRule> rules, TelemetryRowContext row)
    {
        var fallbackCandidates = rules.Count(r => r.IsFallback && HostMatches(r.HostTerms, row));

        foreach (var rule in rules)
        {
            if (!HostMatches(rule.HostTerms, row)) continue;

            if (rule.IsFallback)
            {
                if (fallbackCandidates == 1) return rule;
                continue;
            }

            if (rule.Matchers.Any(m => Fires(m, row))) return rule;
        }

        return null;
    }

    /// <summary><c>Host =~ "t" or Host endswith ".t"</c> over the lowercased host the template projects.</summary>
    private static bool HostMatches(IReadOnlyList<string>? terms, TelemetryRowContext row)
    {
        if (terms is null) return true;

        var host = row.Host ?? "";
        return terms.Any(t =>
            host.Equals(t, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Fires(StepMatcher matcher, TelemetryRowContext row)
    {
        var comparison = matcher.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var url = row.Url ?? "";
        var name = row.Operation ?? "";
        var pageName = row.PageName ?? "";

        return matcher.Kind switch
        {
            StepMatcherKind.UrlContains => url.Contains(matcher.Value, comparison),
            StepMatcherKind.OperationEquals => name.Trim().Equals(matcher.Value.Trim(), comparison),
            StepMatcherKind.OperationContains => name.Contains(matcher.Value, comparison),
            StepMatcherKind.PageNameContains => pageName.Contains(matcher.Value, comparison),
            StepMatcherKind.Regex => Matches(matcher, url) || Matches(matcher, name) || Matches(matcher, pageName),
            _ => throw new InvalidOperationException($"The simulation has no rule for \"{matcher.Kind}\".")
        };
    }

    private static bool Matches(StepMatcher matcher, string text) =>
        Regex.IsMatch(text, matcher.Value,
            RegexOptions.CultureInvariant | (matcher.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
            TimeSpan.FromMilliseconds(100));

    // ================================================================ fixture

    /// <summary>
    /// Two services where one is host-scoped and the other is not, so beta's steps are reachable on
    /// alpha's hostname - the shared-host case a per-service discriminator cannot express - and the
    /// step orders interleave across the two services.
    /// </summary>
    private static CompanyProfile AgreementProfile() => new(
        ProfileKey: "agreement",
        DisplayName: "Agreement Fixture",
        Labels: new CompanyLabels("Service", "Journey", "Step", "Tenant", "Owner"),
        Services:
        [
            new ServiceDefinition("alpha", "Alpha", ["alpha-ai"], ["alpha.example.test"],
                [new FlowDefinition("f", "F", [
                    new StepDefinition("alpha-login", "01 Login", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/login")]),
                    new StepDefinition("alpha-pay", "03 Pay", 3, [new StepMatcher(StepMatcherKind.OperationEquals, "GET /alpha/pay")]),
                    new StepDefinition("alpha-detail", "05 Detail", 5, [new StepMatcher(StepMatcherKind.Regex, @"/alpha/\d+/detail")])
                ])],
                FallbackStep: "alpha-other"),
            new ServiceDefinition("beta", "Beta", ["beta-ai"], [],
                [new FlowDefinition("g", "G", [
                    new StepDefinition("beta-search", "02 Search", 2, [new StepMatcher(StepMatcherKind.UrlContains, "/search")]),
                    new StepDefinition("beta-cart", "04 Cart", 4, [new StepMatcher(StepMatcherKind.PageNameContains, "Beta Cart")]),
                    new StepDefinition("beta-api", "06 Api", 6, [new StepMatcher(StepMatcherKind.OperationContains, "beta-api")])
                ])])
        ],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.Host, "", "host")
        ],
        Glossary: new CompanyGlossary("Service", "Journey", "Step", "Tenant", "Owner"));

    private static IReadOnlyList<TelemetryRowContext> Rows() =>
    [
        new("https://alpha.example.test/login"),
        new("https://alpha.example.test/search"),                                   // beta's step, on alpha's host
        new("https://beta.example.test/login"),                                     // alpha is host-scoped: no claim
        new("https://alpha.example.test/x", Operation: "GET /alpha/pay"),
        new("https://alpha.example.test/x", Operation: "  GET /alpha/pay  "),       // the adapter trims, so the KQL must
        new("https://alpha.example.test/alpha/42/detail"),
        new("https://alpha.example.test/x", PageName: "Beta Cart"),
        new("https://beta.example.test/x", PageName: "beta cart"),
        new("https://beta.example.test/x", Operation: "svc-beta-api-call"),
        new("https://alpha.example.test/nothing-matches"),                          // alpha's catch-all
        new("https://beta.example.test/nothing-matches"),                           // beta has none
        new("/relative/login"),                                                     // no host at all
        new("https://eu.alpha.example.test/login"),                                 // subdomain
        new("https://ALPHA.example.test/login"),                                    // host casing
        new("https://alpha.example.test/x?next=/login"),                            // marker in the query string
        new("https://alpha.example.test/login", Operation: "GET /alpha/pay"),       // earlier order wins
        new("https://beta.example.test/search", PageName: "Beta Cart"),             // earlier order wins
        new("https://alpha.example.test/SEARCH"),                                   // contains is case-insensitive
        new("https://alpha.example.test/x", Operation: "get /alpha/pay"),           // =~ is case-insensitive
        new("https://alpha.example.test/alpha/abc/detail"),                         // the regex does not match
        new(null, Operation: "beta-api"),
        new()
    ];

    private static string Predicate(IReadOnlyList<ComposedRule> rules, string stepKey) =>
        rules.Single(r => r.StepKey == stepKey).Predicate;

    // ================================================================ helpers

    private static string Template()
    {
        using var stream = typeof(KqlQueryTextProvider).Assembly
            .GetManifestResourceStream(ScoutBoardQueryComposer.TemplateResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ScoutBoardQueryComposer.TemplateResourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ScoutBoardQueryRequest Request(ScoutBoardKind kind) => new(
        kind,
        new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero));

    private static string Compose(CompanyProfile profile, ScoutBoardKind kind = ScoutBoardKind.Latency) =>
        ScoutBoardQueryComposer.Compose(profile, Request(kind), Template());

    /// <summary>The template's own prose is not KQL, and it is allowed to quote things.</summary>
    private static string StripComments(string kql) =>
        string.Join("\n", kql.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
