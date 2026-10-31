using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// The contract for the fragments a company profile contributes to a board query.
///
/// This file used to anchor on reproducing one customer's hand-written KQL byte for byte. That
/// measured the wrong thing - fidelity to a legacy instead of fitness for a new customer - so the
/// anchor is now the two properties that actually matter: the fragments say what
/// <see cref="ProfileDrivenAdapter"/> says (asserted in ScoutBoardQueryComposerTests), and a
/// profile edited by whoever onboards a customer cannot inject KQL.
/// </summary>
public class CompanyKqlTemplaterTests
{
    // ---------------------------------------------------------------- matcher translation

    [Theory]
    [InlineData(StepMatcherKind.UrlContains, false, "url contains \"x\"")]
    [InlineData(StepMatcherKind.UrlContains, true, "url contains_cs \"x\"")]
    [InlineData(StepMatcherKind.OperationEquals, false, "trim(@\"\\s+\",name) =~ \"x\"")]
    [InlineData(StepMatcherKind.OperationEquals, true, "trim(@\"\\s+\",name) == \"x\"")]
    [InlineData(StepMatcherKind.OperationContains, false, "name contains \"x\"")]
    [InlineData(StepMatcherKind.OperationContains, true, "name contains_cs \"x\"")]
    [InlineData(StepMatcherKind.PageNameContains, false, "PageName contains \"x\"")]
    [InlineData(StepMatcherKind.Regex, true, "(url matches regex \"x\" or name matches regex \"x\" or PageName matches regex \"x\")")]
    [InlineData(StepMatcherKind.Regex, false, "(url matches regex \"(?i)x\" or name matches regex \"(?i)x\" or PageName matches regex \"(?i)x\")")]
    public void RenderMatcher_TranslatesEveryKind(string kind, bool caseSensitive, string expected) =>
        Assert.Equal(expected, CompanyKqlTemplater.RenderMatcher(new StepMatcher(kind, "x", caseSensitive)));

    [Fact]
    public void RenderMatcher_MatchesAgainstTheFullUrl_NotJustThePath()
    {
        // TelemetryRowContext.UrlText is the whole url, query string included. Matching the KQL
        // against a path-only column would silently disagree with the adapter for every marker that
        // lives in a query parameter - the exact class of split this seam exists to remove.
        Assert.Equal("url contains \"?step=pay\"",
            CompanyKqlTemplater.RenderMatcher(new StepMatcher(StepMatcherKind.UrlContains, "?step=pay")));
    }

    [Fact]
    public void RenderMatcher_NormalisesTheKind_TheSameWayTheAdapterDoes()
    {
        // The adapter switches on Kind.Trim().ToLowerInvariant(). A renderer that switched on the
        // raw string would reject a profile the adapter happily evaluates.
        Assert.Equal("url contains \"x\"",
            CompanyKqlTemplater.RenderMatcher(new StepMatcher("  URL-Contains ", "x")));
    }

    [Fact]
    public void RenderMatcher_RejectsAKindItCannotEmit()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CompanyKqlTemplater.RenderMatcher(new StepMatcher("header-equals", "x")));

        Assert.Contains("header-equals", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- entity identity

    [Fact]
    public void EntityIdentity_FollowsOrder_AndTranslatesEveryKind()
    {
        var profile = Profile() with
        {
            EntityIdentitySources =
            [
                new EntityIdentitySource(4, EntityIdentitySourceKind.RoleName, "", "tenant"),
                new EntityIdentitySource(2, EntityIdentitySourceKind.QueryParameter, "tenant", "tenant"),
                new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant"),
                new EntityIdentitySource(3, EntityIdentitySourceKind.Host, "", "host")
            ]
        };

        Assert.Equal(
            "coalesce(tostring(customDimensions.TenantId),tostring(parse_url(url)[\"Query Parameters\"][\"tenant\"]),Host,cloud_RoleName)",
            CompanyKqlTemplater.RenderEntityIdentity(profile));
    }

    [Fact]
    public void EntityIdentity_SkipsCoalesceForASingleSource_AndIsAnEmptyLiteralWithNone()
    {
        var one = Profile() with
        {
            EntityIdentitySources = [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant")]
        };
        Assert.Equal("tostring(customDimensions.TenantId)", CompanyKqlTemplater.RenderEntityIdentity(one));

        Assert.Equal("\"\"", CompanyKqlTemplater.RenderEntityIdentity(Profile() with { EntityIdentitySources = [] }));
    }

    [Fact]
    public void EntityIdentity_EscapesQueryParameterAndNonIdentifierDimensionNames()
    {
        var profile = Profile() with
        {
            EntityIdentitySources =
            [
                new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "tenant id\"]) or true //", "tenant"),
                new EntityIdentitySource(2, EntityIdentitySourceKind.QueryParameter, "ten\"ant", "tenant")
            ]
        };

        var rendered = CompanyKqlTemplater.RenderEntityIdentity(profile);

        Assert.Equal(
            "coalesce(tostring(customDimensions[\"tenant id\\\"]) or true //\"]),tostring(parse_url(url)[\"Query Parameters\"][\"ten\\\"ant\"]))",
            rendered);
        Assert.Equal(0, UnescapedQuoteCount(rendered) % 2);
    }

    [Fact]
    public void EntityIdentity_RejectsAKindItCannotEmit()
    {
        var profile = Profile() with
        {
            EntityIdentitySources = [new EntityIdentitySource(1, "jwt-claim", "sub", "tenant")]
        };

        Assert.Throws<InvalidOperationException>(() => CompanyKqlTemplater.RenderEntityIdentity(profile));
    }

    // ---------------------------------------------------------------- host filter

    [Fact]
    public void HostFilter_WithNoHostsAnywhere_IsANoOpThatFiltersNothingOut()
    {
        var profile = Profile() with
        {
            Services = [Checkout() with { HostFilters = [] }, Catalog() with { HostFilters = [] }]
        };

        Assert.Equal("true", CompanyKqlTemplater.RenderHostFilter(profile));
    }

    [Fact]
    public void HostFilter_IsTrueAsSoonAsOneServiceAcceptsAnyHost()
    {
        // A service with no host filters serves every host. Narrowing the board on behalf of the
        // other services would delete that service's traffic from the board entirely.
        var profile = Profile() with
        {
            Services = [Checkout() with { HostFilters = ["shop.example.com"] }, Catalog() with { HostFilters = [] }]
        };

        Assert.Equal("true", CompanyKqlTemplater.RenderHostFilter(profile));
    }

    [Fact]
    public void HostFilter_UnionsTheHostsOfEveryConstrainedService_AndAcceptsSubdomains()
    {
        var profile = Profile() with
        {
            Services =
            [
                Checkout() with { HostFilters = ["shop.example.com", " *.shop.example.com "] },
                Catalog() with { HostFilters = ["catalog.example.com"] }
            ]
        };

        Assert.Equal(
            "(Host =~ \"shop.example.com\" or Host endswith \".shop.example.com\") or " +
            "(Host =~ \"catalog.example.com\" or Host endswith \".catalog.example.com\")",
            CompanyKqlTemplater.RenderHostFilter(profile));
    }

    [Fact]
    public void HostFilter_NeverRendersFalse_EvenWhenEveryDeclaredHostIsBlank()
    {
        // Those services can claim no row, which the classifier already reflects. Letting that
        // reach the board's WHERE would empty it, and an empty board reads as a healthy platform.
        var profile = Profile() with { Services = [Checkout() with { HostFilters = ["   ", "*"] }] };

        Assert.Equal("true", CompanyKqlTemplater.RenderHostFilter(profile));
    }

    // ---------------------------------------------------------------- telemetry sources

    [Fact]
    public void SourceAliases_KeepDeclarationOrder_AndDeduplicateCaseInsensitively()
    {
        var profile = Profile() with
        {
            Services =
            [
                Checkout() with { TelemetrySourceAliases = ["shop-web", " SHOP-WEB "] },
                Catalog() with { TelemetrySourceAliases = ["catalog-api", "shop-web"] }
            ]
        };

        Assert.Equal(["shop-web", "catalog-api"], CompanyKqlTemplater.SourceAliases(profile));
    }

    [Fact]
    public void SourceUnion_EmitsOneBranchPerAlias_AndLeavesTheAliasForTheResolver()
    {
        var profile = Profile() with
        {
            Services = [Checkout() with { TelemetrySourceAliases = ["shop-web"] }, Catalog() with { TelemetrySourceAliases = ["catalog-api"] }]
        };

        var requests = CompanyKqlTemplater.RenderRequestSourceUnion(profile, "\n");
        var exceptions = CompanyKqlTemplater.RenderExceptionSourceUnion(profile, "\n");

        Assert.Contains("app(\"{{SOURCE:shop-web}}\").requests", requests, StringComparison.Ordinal);
        Assert.Contains("app(\"{{SOURCE:catalog-api}}\").requests", requests, StringComparison.Ordinal);
        Assert.Equal(2, requests.Split("\n").Length);
        Assert.Contains("app(\"{{SOURCE:shop-web}}\").exceptions", exceptions, StringComparison.Ordinal);

        // The resolver is the only component allowed to turn an alias into a resource id.
        KqlPlaceholders.AssertNoneLeft(requests, "source union");
        KqlPlaceholders.AssertNoneLeft(exceptions, "exception source union");
    }

    [Fact]
    public void SourceAliases_RejectAnAliasTheResolverCouldNeverMatch()
    {
        // AssertNoneLeft waves anything beginning with {{SOURCE: through, so a malformed alias
        // would otherwise reach Azure as literal text instead of failing here.
        var profile = Profile() with { Services = [Checkout() with { TelemetrySourceAliases = ["shop web"] }] };

        var ex = Assert.Throws<InvalidOperationException>(() => CompanyKqlTemplater.SourceAliases(profile));
        Assert.Contains("shop web", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAliases_RejectAProfileWithNoTelemetrySourceAtAll()
    {
        var profile = Profile() with { Services = [Checkout() with { TelemetrySourceAliases = [] }] };

        Assert.Throws<InvalidOperationException>(() => CompanyKqlTemplater.SourceAliases(profile));
    }

    // ---------------------------------------------------------------- classifier

    [Fact]
    public void Classifier_EmitsOneCascade_OrderedByStepOrderAcrossServices()
    {
        var rules = CompanyKqlTemplater.BuildRules(Profile());

        Assert.Equal(
            ["cart", "search", "pay", "product", "00 Other checkout traffic"],
            rules.Select(r => r.StepKey));
        Assert.Equal(
            ["checkout", "catalog", "checkout", "catalog", "checkout"],
            rules.Select(r => r.ServiceKey));
        Assert.True(rules[^1].IsFallback, "the fallback arm has to be last");
    }

    [Fact]
    public void Classifier_DropsAStepWhoseMatchersAreAllBlank()
    {
        // The adapter treats a blank matcher value as "never matches", so an arm for it would be a
        // line a reader has to re-derive is unreachable.
        var profile = OneService(Checkout() with
        {
            Flows = [new FlowDefinition("purchase", "Purchase", [new StepDefinition("ghost", "Ghost", 1, [new StepMatcher(StepMatcherKind.UrlContains, "")])])],
            FallbackStep = null
        });

        Assert.Empty(CompanyKqlTemplater.BuildRules(profile));
        Assert.Contains("| extend MatchIndex=-1", CompanyKqlTemplater.RenderServiceAndStepClassifier(profile, "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_HasNoServiceDiscriminator_OnlyLookupsOffTheMatchedArm()
    {
        var rendered = CompanyKqlTemplater.RenderServiceAndStepClassifier(Profile(), "\n");

        // Exactly one cascade carries predicates; ServiceKey/Service/StepKey/Step are projections
        // of the arm it chose and cannot disagree with it.
        Assert.Equal(1, Occurrences(rendered, "url contains \"/cart\""));
        Assert.Contains("| extend ServiceKey=case(MatchIndex==0,\"checkout\",", rendered, StringComparison.Ordinal);
        Assert.Contains("| extend StepKey=case(MatchIndex==0,\"cart\",", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("| extend ServiceKey=case(url", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_GuardsTheFallbackArmsWhenMoreThanOneServiceOffersOne()
    {
        // Two eligible catch-alls resolve to none: picking either would hide a defect in the
        // profile, which is the judgement ProfileDrivenAdapter.Fallback makes.
        var profile = Profile() with
        {
            Services =
            [
                Checkout() with { HostFilters = [] },
                Catalog() with { HostFilters = [], FallbackStep = "00 Other catalog traffic" }
            ]
        };

        var rendered = CompanyKqlTemplater.RenderServiceAndStepClassifier(profile, "\n");

        Assert.StartsWith("| extend FallbackCandidates=1+1\n", rendered, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(rendered, "FallbackCandidates==1,"));
    }

    [Fact]
    public void Classifier_CountsFallbackCandidatesByHost_WhenTheServicesAreHostScoped()
    {
        var profile = Profile() with
        {
            Services =
            [
                Checkout() with { HostFilters = ["shop.example.com"] },
                Catalog() with { HostFilters = ["catalog.example.com"], FallbackStep = "00 Other catalog traffic" }
            ]
        };

        Assert.StartsWith(
            "| extend FallbackCandidates=iff((Host =~ \"shop.example.com\" or Host endswith \".shop.example.com\"),1,0)" +
            "+iff((Host =~ \"catalog.example.com\" or Host endswith \".catalog.example.com\"),1,0)\n",
            CompanyKqlTemplater.RenderServiceAndStepClassifier(profile, "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_TellsTwoServicesApartWhenTheyShareOneHost()
    {
        // The whole point of a single cascade: nothing decides the service up front, so two product
        // areas behind one hostname are separated by WHICH step recognised the row.
        var profile = Profile(); // both services declare shop.example.com
        Assert.Equal(["shop.example.com"], profile.Services[0].HostFilters);
        Assert.Equal(["shop.example.com"], profile.Services[1].HostFilters);

        var rules = CompanyKqlTemplater.BuildRules(profile);
        var cart = rules.Single(r => r.StepKey == "cart");
        var search = rules.Single(r => r.StepKey == "search");

        Assert.Equal("checkout", cart.ServiceKey);
        Assert.Equal("catalog", search.ServiceKey);
        Assert.True(cart.HostTerms is ["shop.example.com"] && search.HostTerms is ["shop.example.com"]);
        Assert.NotEqual(cart.Predicate, search.Predicate);
    }

    // ---------------------------------------------------------------- injection

    [Fact]
    public void Render_EscapesProfileValues_SoAProfileCannotInjectKql()
    {
        const string payload = "a\" or 1==1 or url contains \"b";
        var service = Checkout() with
        {
            DisplayName = "Check\"out",
            FallbackStep = "fall\\back",
            Flows = [new FlowDefinition("purchase", "Purchase", [
                new StepDefinition("cart", "01 \"Cart\"", 1, [new StepMatcher(StepMatcherKind.UrlContains, payload)])
            ])]
        };

        var rendered = CompanyKqlTemplater.RenderServiceAndStepClassifier(OneService(service), "\n");

        Assert.Contains("url contains \"a\\\" or 1==1 or url contains \\\"b\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"01 \\\"Cart\\\"\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"Check\\\"out\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"fall\\\\back\"", rendered, StringComparison.Ordinal);
        // The payload never appears as a second, live clause...
        Assert.DoesNotContain("\"a\" or 1==1", rendered, StringComparison.Ordinal);
        // ...and every literal still closes.
        Assert.Equal(0, UnescapedQuoteCount(rendered) % 2);
    }

    // ---------------------------------------------------------------- placeholder guard

    [Fact]
    public void AssertNoneLeft_StillFailsForEveryPlaceholderThisBuildFills()
    {
        foreach (var placeholder in KqlPlaceholders.CompanyProfilePlaceholders.Concat(KqlPlaceholders.BoardRequestPlaceholders))
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => KqlPlaceholders.AssertNoneLeft($"requests | where x=={placeholder}", "B1"));
            Assert.Contains(placeholder, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AssertNoneLeft_StillLetsSourcePlaceholdersThrough() =>
        KqlPlaceholders.AssertNoneLeft("app(\"{{SOURCE:shop-web}}\").requests", "B1");

    [Fact]
    public void Render_FillsEveryCompanyPlaceholder_AndOnlyThose()
    {
        var template = string.Join("\n", KqlPlaceholders.CompanyProfilePlaceholders) + "\n" + KqlPlaceholders.TimeStart;

        var rendered = CompanyKqlTemplater.Render(template, Profile());

        foreach (var placeholder in KqlPlaceholders.CompanyProfilePlaceholders)
        {
            Assert.DoesNotContain(placeholder, rendered, StringComparison.Ordinal);
        }
        Assert.Contains(KqlPlaceholders.TimeStart, rendered, StringComparison.Ordinal);
    }

    // ================================================================ fixture
    // A made-up two-service company whose services SHARE one hostname - the arrangement the
    // abandoned "one case() per service" approach could not express at all.

    internal static CompanyProfile Profile() => new(
        ProfileKey: "northwind",
        DisplayName: "Northwind Retail",
        Labels: new CompanyLabels("Service", "Journey", "Step", "Tenant", "Owner"),
        Services: [Checkout(), Catalog()],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.QueryParameter, "tenant", "tenant"),
            new EntityIdentitySource(3, EntityIdentitySourceKind.Host, "", "host")
        ],
        Glossary: new CompanyGlossary("Service", "Journey", "Step", "Tenant", "Owner", "Northwind sells things online."));

    internal static ServiceDefinition Checkout() => new(
        "checkout", "Checkout", ["shop-web"], ["shop.example.com"],
        [new FlowDefinition("purchase", "Purchase", [
            new StepDefinition("cart", "01 Cart", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/cart")]),
            new StepDefinition("pay", "02 Payment", 2, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout/pay")])
        ])],
        FallbackStep: "00 Other checkout traffic");

    internal static ServiceDefinition Catalog() => new(
        "catalog", "Catalog", ["shop-web"], ["shop.example.com"],
        [new FlowDefinition("browse", "Browse", [
            new StepDefinition("search", "01 Search", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/search")]),
            new StepDefinition("product", "02 Product", 2, [new StepMatcher(StepMatcherKind.OperationEquals, "GET /product")])
        ])]);

    private static CompanyProfile OneService(ServiceDefinition service) => Profile() with { Services = [service] };

    // ================================================================ helpers

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    /// <summary>Double quotes that actually open or close a KQL literal, i.e. not preceded by an odd run of backslashes.</summary>
    internal static int UnescapedQuoteCount(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;

            var backslashes = 0;
            for (var j = i - 1; j >= 0 && text[j] == '\\'; j--) backslashes++;
            if (backslashes % 2 == 0) count++;
        }
        return count;
    }
}
