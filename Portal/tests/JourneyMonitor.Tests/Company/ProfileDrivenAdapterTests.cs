using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// The fixtures below are two invented companies that share nothing but the telemetry they are
/// given. That is the point of the suite: the same ROW has to come back as two different services,
/// flows, steps and entity kinds purely because two different documents were loaded. If one
/// vocabulary ever leaks into the implementation, exactly one of these two profiles keeps passing.
///
/// Everything is driven from a <see cref="TelemetryRowContext"/> now. No caller names a service
/// and no caller pre-builds an entity key, so a profile is free to read the row however it likes -
/// which is the only way a second company gets onboarded without touching this assembly.
/// </summary>
public sealed class ProfileDrivenAdapterTests
{
    // Identical input for both profiles, deliberately: it is the only honest way to show the
    // answer comes from the document and not from the code.
    private const string SharedUrl = "https://edge.example-one.test/api/v2/checkout?aid=99";
    private const string SharedOperation = "POST /api/v2/checkout";
    private const string SharedPageName = "Checkout";

    private static TelemetryRowContext SharedRow() => new(
        Url: SharedUrl,
        Operation: SharedOperation,
        PageName: SharedPageName,
        RoleName: "edge-frontend");

    // ---------- 1. Profile A classifies into its own vocabulary ----------

    [Fact]
    public void ResolveStep_WithProfileA_UsesProfileAVocabulary()
    {
        var adapter = new ProfileDrivenAdapter(ProfileA());

        var match = adapter.ResolveStep(SharedRow());

        Assert.True(match.IsResolved);
        Assert.Equal("freight", match.Service);
        Assert.Equal("dispatch", match.Flow);
        Assert.Equal("label-printed", match.Step);
        Assert.Equal(CompanyResolution.PatternMatch, match.ResolvedBy);
    }

    // ---------- 2. Same row, Profile B, different vocabulary ----------
    // The most important test in the file: the proof the platform is company-independent.

    [Fact]
    public void ResolveStep_WithProfileB_ClassifiesTheSameRowIntoADifferentVocabulary()
    {
        var row = SharedRow();

        var a = new ProfileDrivenAdapter(ProfileA()).ResolveStep(row);
        var b = new ProfileDrivenAdapter(ProfileB()).ResolveStep(row);

        Assert.True(a.IsResolved);
        Assert.True(b.IsResolved);

        Assert.Equal("playback", b.Service);
        Assert.Equal("viewing", b.Flow);
        Assert.Equal("stream-started", b.Step);

        Assert.NotEqual(a.Service, b.Service);
        Assert.NotEqual(a.Flow, b.Flow);
        Assert.NotEqual(a.Step, b.Step);
    }

    [Fact]
    public void ResolveAffectedEntity_TheSameRowYieldsADifferentEntityPerProfile()
    {
        var row = SharedRow();

        var a = new ProfileDrivenAdapter(ProfileA()).ResolveAffectedEntity(row);
        var b = new ProfileDrivenAdapter(ProfileB()).ResolveAffectedEntity(row);

        Assert.True(a.IsResolved);
        Assert.True(b.IsResolved);

        // A reads the account out of the query string; B has no such source and falls to the host.
        Assert.Equal("99", a.Value);
        Assert.Equal("account", a.Kind);
        Assert.Equal("aid", a.SourceKey);

        Assert.Equal("edge.example-one.test", b.Value);
        Assert.Equal("origin", b.Kind);
        Assert.Equal(EntityIdentitySourceKind.Host, b.SourceKey);

        Assert.NotEqual(a.Kind, b.Kind);
        Assert.NotEqual(a.SourceKey, b.SourceKey);
    }

    [Fact]
    public void LabelsAndGlossary_ComeFromTheDocument()
    {
        var a = new ProfileDrivenAdapter(ProfileA());
        var b = new ProfileDrivenAdapter(ProfileB());

        Assert.Equal("Lane", a.Labels.Service);
        Assert.Equal("Channel", b.Labels.Service);
        Assert.NotEqual(a.Glossary.EntityNoun, b.Glossary.EntityNoun);
        Assert.Equal("profile-a", a.ProfileKey);
        Assert.Equal("profile-b", b.ProfileKey);
        Assert.Equal("freight", Assert.Single(a.Services).Key);
    }

    // ---------- 3. Order is load-bearing ----------

    [Fact]
    public void ResolveStep_WithOverlappingMatchers_LowestOrderWins()
    {
        var row = new TelemetryRowContext(Url: "https://edge.example-one.test/api/v2/checkout");

        var first = new ProfileDrivenAdapter(OverlappingProfile(broadOrder: 10, narrowOrder: 20)).ResolveStep(row);
        var swapped = new ProfileDrivenAdapter(OverlappingProfile(broadOrder: 30, narrowOrder: 20)).ResolveStep(row);

        Assert.Equal("broad", first.Step);
        Assert.Equal("narrow", swapped.Step);
    }

    [Fact]
    public void ResolveStep_MatchersWithinAStepAreAlternatives()
    {
        // Both matchers hit. Which one decided is not expressible in StepMatch, so the only thing
        // observable - and therefore the only thing asserted - is that the step resolves once.
        var profile = SingleStepProfile(
            new StepMatcher(StepMatcherKind.UrlContains, "/api/"),
            new StepMatcher(StepMatcherKind.OperationEquals, SharedOperation));

        var match = new ProfileDrivenAdapter(profile).ResolveStep(SharedRow());

        Assert.True(match.IsResolved);
        Assert.Equal(CompanyResolution.PatternMatch, match.ResolvedBy);
        Assert.Equal("only", match.Step);
    }

    // ---------- 4. A catch-all is reported as a catch-all ----------

    [Fact]
    public void ResolveStep_WhenOnlyTheServiceFallbackApplies_ReportsFallbackWildcard()
    {
        var adapter = new ProfileDrivenAdapter(ProfileA());

        var match = adapter.ResolveStep(new TelemetryRowContext(
            Url: "https://edge.example-one.test/nothing/mapped",
            Operation: "GET /nothing/mapped"));

        Assert.True(match.IsResolved);
        Assert.Equal("freight", match.Service);
        Assert.Equal("unclassified-lane-traffic", match.Step);
        Assert.Equal(CompanyResolution.FallbackWildcard, match.ResolvedBy);
        Assert.NotEqual(CompanyResolution.PatternMatch, match.ResolvedBy);
        Assert.Null(match.Flow);
    }

    // ---------- 5. Nothing matched means nothing matched, never a guess ----------

    [Fact]
    public void ResolveStep_WithNoFallbackAndNoMatch_StaysUnresolved()
    {
        // Profile B declares no catch-all; an honest gap must not be dressed up as an answer.
        var match = new ProfileDrivenAdapter(ProfileB()).ResolveStep(new TelemetryRowContext(
            Url: "https://edge.example-one.test/nothing/mapped",
            Operation: "GET /nothing/mapped"));

        Assert.False(match.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
        Assert.Null(match.Service);
        Assert.Null(match.Flow);
        Assert.Null(match.Step);
    }

    [Fact]
    public void ResolveStep_WhenNoServiceNarrowsToTheHost_StaysUnresolvedEvenThoughAFallbackExists()
    {
        // ProfileA's only service is narrowed to its own host, so this row reaches neither its
        // matchers nor its catch-all.
        var match = new ProfileDrivenAdapter(ProfileA()).ResolveStep(new TelemetryRowContext(
            Url: "https://somewhere.unknown.test/api/v2/checkout",
            Operation: SharedOperation));

        Assert.False(match.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
        Assert.Null(match.Service);
    }

    [Fact]
    public void ResolveStep_WhenTwoServicesBothOfferACatchAll_StaysUnresolved()
    {
        // Picking either would hide the defect in the profile, and a plausible wrong answer is
        // worse than a visible gap.
        var profile = Shell(
        [
            new ServiceDefinition("one", "One", [], [], [], FallbackStep: "one-leftovers"),
            new ServiceDefinition("two", "Two", [], [], [], FallbackStep: "two-leftovers"),
        ]);

        var match = new ProfileDrivenAdapter(profile).ResolveStep(SharedRow());

        Assert.False(match.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
        Assert.Null(match.Step);
    }

    [Fact]
    public void ResolveStep_WithANullRow_StaysUnresolved()
    {
        var match = new ProfileDrivenAdapter(ProfileA()).ResolveStep(null!);

        Assert.False(match.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
    }

    // ---------- 10. Two services behind one host, told apart by the rule that matched ----------

    [Fact]
    public void ResolveStep_TwoServicesSharingOneHost_AreToldApartByTheMatchedStep()
    {
        var adapter = new ProfileDrivenAdapter(SharedHostProfile());

        var cargo = adapter.ResolveStep(new TelemetryRowContext(Url: "https://shared.example-two.test/track/8812"));
        var insurance = adapter.ResolveStep(new TelemetryRowContext(Url: "https://shared.example-two.test/quote/new"));

        Assert.Equal("cargo", cargo.Service);
        Assert.Equal("tracked", cargo.Step);
        Assert.Equal(CompanyResolution.PatternMatch, cargo.ResolvedBy);

        Assert.Equal("insurance", insurance.Service);
        Assert.Equal("quoted", insurance.Step);

        // Same host, same profile, two different services: the host cannot be the discriminator.
        Assert.NotEqual(cargo.Service, insurance.Service);
    }

    // ---------- 11. Host filters narrow, they do not identify ----------

    [Fact]
    public void ResolveStep_ASkippedHostFilterDoesNotBlockAServiceThatFiltersOnNothing()
    {
        // "narrowed" owns the LOWER step order, so it would win on order alone; it is skipped
        // purely because its host filter does not cover this row.
        var profile = Shell(
        [
            new ServiceDefinition("narrowed", "Narrowed", [], ["other.example.test"],
            [
                new FlowDefinition("flow", "Flow", [
                    new StepDefinition("narrowed-step", "Narrowed step", 10, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                ]),
            ]),
            new ServiceDefinition("anywhere", "Anywhere", [], [],
            [
                new FlowDefinition("flow", "Flow", [
                    new StepDefinition("anywhere-step", "Anywhere step", 20, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                ]),
            ]),
        ]);

        var match = new ProfileDrivenAdapter(profile).ResolveStep(SharedRow());

        Assert.True(match.IsResolved);
        Assert.Equal("anywhere", match.Service);
        Assert.Equal("anywhere-step", match.Step);
    }

    [Fact]
    public void ResolveStep_AHostFilterCoversSubdomainsOfTheFilteredDomain()
    {
        var profile = Shell(
        [
            new ServiceDefinition("apex", "Apex", [], ["example-one.test"],
            [
                new FlowDefinition("flow", "Flow", [
                    new StepDefinition("only", "Only", 10, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                ]),
            ]),
        ]);

        Assert.True(new ProfileDrivenAdapter(profile).ResolveStep(SharedRow()).IsResolved);
    }

    // ---------- 7. Every matcher kind is exercised ----------

    [Theory]
    [InlineData(StepMatcherKind.UrlContains, "/api/v2/checkout")]
    [InlineData(StepMatcherKind.UrlContains, "aid=99")]
    [InlineData(StepMatcherKind.OperationEquals, SharedOperation)]
    [InlineData(StepMatcherKind.OperationContains, "checkout")]
    [InlineData(StepMatcherKind.PageNameContains, "checkout")]
    [InlineData(StepMatcherKind.Regex, @"^POST /api/v\d+/checkout$")]
    public void ResolveStep_EveryDeclaredMatcherKindIsEvaluated(string kind, string value)
    {
        var profile = SingleStepProfile(new StepMatcher(kind, value));

        var match = new ProfileDrivenAdapter(profile).ResolveStep(SharedRow());

        Assert.True(match.IsResolved);
        Assert.Equal("only", match.Step);
    }

    [Fact]
    public void ResolveStep_PageNameContains_OnlyLooksAtThePageName()
    {
        // The row carries a page name of its own now, so this kind is genuinely testable instead of
        // being satisfied incidentally by the url.
        var profile = SingleStepProfile(new StepMatcher(StepMatcherKind.PageNameContains, "Shipment board"));
        var adapter = new ProfileDrivenAdapter(profile);

        var hit = adapter.ResolveStep(new TelemetryRowContext(Url: "https://edge.example-one.test/x", PageName: "Shipment board"));
        var miss = adapter.ResolveStep(new TelemetryRowContext(Url: "https://edge.example-one.test/shipment-board", PageName: "Something else"));

        Assert.True(hit.IsResolved);
        Assert.False(miss.IsResolved);
    }

    [Fact]
    public void ResolveStep_WhenTheFieldAMatcherNeedsIsMissing_DoesNotMatchAndDoesNotThrow()
    {
        var profile = SingleStepProfile(new StepMatcher(StepMatcherKind.OperationEquals, SharedOperation));

        var match = new ProfileDrivenAdapter(profile).ResolveStep(new TelemetryRowContext());

        Assert.False(match.IsResolved);
    }

    // ---------- 8. Case sensitivity, and kinds this build cannot evaluate ----------

    [Fact]
    public void ResolveStep_CaseSensitivityIsHonoured()
    {
        var sensitive = SingleStepProfile(new StepMatcher(StepMatcherKind.OperationContains, "CHECKOUT", CaseSensitive: true));
        var insensitive = SingleStepProfile(new StepMatcher(StepMatcherKind.OperationContains, "CHECKOUT"));

        Assert.False(new ProfileDrivenAdapter(sensitive).ResolveStep(SharedRow()).IsResolved);
        Assert.True(new ProfileDrivenAdapter(insensitive).ResolveStep(SharedRow()).IsResolved);
    }

    [Fact]
    public void ResolveStep_WithAnUnknownMatcherKind_DoesNotMatchAndIsReported()
    {
        var adapter = new ProfileDrivenAdapter(SingleStepProfile(new StepMatcher("semantic-similarity", "checkout")));

        var match = adapter.ResolveStep(SharedRow());

        Assert.False(match.IsResolved);
        // Silently never matching is the failure mode that makes a profile look like quiet traffic.
        Assert.Contains("semantic-similarity", adapter.UnknownMatcherKinds);
    }

    // ---------- 9. A pattern an admin typed wrong is a reportable gap, not an exception ----------

    [Fact]
    public void ResolveStep_WithAnUnparseableRegex_DoesNotThrowAndIsReported()
    {
        var adapter = new ProfileDrivenAdapter(SingleStepProfile(new StepMatcher(StepMatcherKind.Regex, "([unclosed")));

        var match = adapter.ResolveStep(SharedRow());

        Assert.False(match.IsResolved);
        Assert.Contains($"{StepMatcherKind.Regex}:invalid-pattern", adapter.UnknownMatcherKinds);
    }

    // ---------- 6 & 12. The identity ladder is walked in Order, and reports who answered ----------

    [Fact]
    public void ResolveAffectedEntity_WhenTheThirdSourceMatches_ReportsThatSourceKey()
    {
        var adapter = new ProfileDrivenAdapter(ProfileA());

        // No ShipperAccountId (order 1) and no aid in the query (order 2), so DepotCode (order 3) answers.
        var identity = adapter.ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://edge.example-one.test/api/v2/checkout",
            CustomDimensions: Dimensions(("DepotCode", "AMS-4"))));

        Assert.True(identity.IsResolved);
        Assert.Equal("AMS-4", identity.Value);
        Assert.Equal("depot", identity.Kind);
        Assert.Equal("DepotCode", identity.SourceKey);
        Assert.Equal(CompanyResolution.ExactMatch, identity.ResolvedBy);
    }

    [Fact]
    public void ResolveAffectedEntity_WalksSourcesByOrderNotByListPosition()
    {
        // ProfileA lists DepotCode (order 3) first; Order has to beat list position.
        var adapter = new ProfileDrivenAdapter(ProfileA());

        var identity = adapter.ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://edge.example-one.test/api/v2/checkout?aid=99",
            CustomDimensions: Dimensions(("ShipperAccountId", "4471"), ("DepotCode", "AMS-4"))));

        Assert.Equal("4471", identity.Value);
        Assert.Equal("ShipperAccountId", identity.SourceKey);
        Assert.Equal("shipper", identity.Kind);
    }

    [Theory]
    [InlineData("tenant", "TenantId")]
    [InlineData("account", "aid")]
    [InlineData("worker", EntityIdentitySourceKind.RoleName)]
    [InlineData("origin", EntityIdentitySourceKind.Host)]
    public void ResolveAffectedEntity_TakesTheFirstSourceInOrderThatYieldsAValue(string expectedKind, string expectedSourceKey)
    {
        var adapter = new ProfileDrivenAdapter(EntityLadderProfile());

        // Each row drops one more rung, forcing the next source down to answer.
        var row = expectedKind switch
        {
            "tenant" => new TelemetryRowContext(
                Url: "https://ladder.example.test/x?aid=99",
                RoleName: "ladder-worker",
                CustomDimensions: Dimensions(("TenantId", "t-1"))),
            "account" => new TelemetryRowContext(
                Url: "https://ladder.example.test/x?aid=99",
                RoleName: "ladder-worker"),
            "worker" => new TelemetryRowContext(
                Url: "https://ladder.example.test/x",
                RoleName: "ladder-worker"),
            _ => new TelemetryRowContext(Url: "https://ladder.example.test/x"),
        };

        var identity = adapter.ResolveAffectedEntity(row);

        Assert.True(identity.IsResolved);
        Assert.Equal(expectedKind, identity.Kind);
        Assert.Equal(expectedSourceKey, identity.SourceKey);
        Assert.Equal(CompanyResolution.ExactMatch, identity.ResolvedBy);
    }

    [Fact]
    public void ResolveAffectedEntity_WhenTheRowCarriesNoConfiguredSource_ReturnsUnresolved()
    {
        // Every source ProfileA declares needs a dimension or a query parameter; this row has neither.
        var identity = new ProfileDrivenAdapter(ProfileA()).ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://edge.example-one.test/api/v2/checkout",
            Operation: SharedOperation,
            CustomDimensions: Dimensions(("WarehouseCode", "12"))));

        Assert.False(identity.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, identity.ResolvedBy);
        Assert.Null(identity.Value);
        Assert.Null(identity.Kind);
        Assert.Null(identity.SourceKey);
    }

    [Fact]
    public void ResolveAffectedEntity_WithAnEmptyRow_ReturnsUnresolvedRatherThanGuessing()
    {
        // An invented identity attributes an incident to a customer who is not having a problem.
        var identity = new ProfileDrivenAdapter(EntityLadderProfile()).ResolveAffectedEntity(new TelemetryRowContext());

        Assert.False(identity.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, identity.ResolvedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAffectedEntity_IgnoresABlankValueAndKeepsWalking(string blank)
    {
        var identity = new ProfileDrivenAdapter(ProfileA()).ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://edge.example-one.test/api/v2/checkout?aid=99",
            CustomDimensions: Dimensions(("ShipperAccountId", blank))));

        Assert.True(identity.IsResolved);
        Assert.Equal("aid", identity.SourceKey);
        Assert.Equal("99", identity.Value);
    }

    [Fact]
    public void ResolveAffectedEntity_WithAnUnknownSourceKind_ResolvesNothingAndIsReported()
    {
        var profile = ProfileA() with
        {
            EntityIdentitySources = [new EntityIdentitySource(1, "astrology", "SunSign", "sign")],
        };
        var adapter = new ProfileDrivenAdapter(profile);

        var identity = adapter.ResolveAffectedEntity(SharedRow());

        Assert.False(identity.IsResolved);
        Assert.Contains("identity:astrology", adapter.UnknownMatcherKinds);
    }

    [Fact]
    public void ResolveAffectedEntity_WithANullRow_ReturnsUnresolved()
    {
        var identity = new ProfileDrivenAdapter(ProfileA()).ResolveAffectedEntity(null!);

        Assert.False(identity.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, identity.ResolvedBy);
    }

    // ---------- 13 & 14. What the row itself is able to answer ----------

    [Fact]
    public void CustomDimension_FindsAKeyWhoseCasingDiffersFromTheProfileSpelling()
    {
        // Telemetry SDKs disagree about casing for the same logical key, and a profile author
        // cannot be expected to guess which spelling was emitted.
        var row = new TelemetryRowContext(CustomDimensions: Dimensions(("shipperaccountid", "4471")));

        Assert.Equal("4471", row.CustomDimension("ShipperAccountId"));
        Assert.Equal("4471", row.CustomDimension("SHIPPERACCOUNTID"));
        Assert.Null(row.CustomDimension("ShipperAccount"));
    }

    [Fact]
    public void ResolveAffectedEntity_MatchesADimensionWhoseCasingDiffersFromTheProfileSpelling()
    {
        var identity = new ProfileDrivenAdapter(ProfileA()).ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://edge.example-one.test/api/v2/checkout",
            CustomDimensions: Dimensions(("SHIPPERACCOUNTID", "4471"))));

        Assert.True(identity.IsResolved);
        Assert.Equal("4471", identity.Value);
        // The profile's spelling is reported, not the telemetry's, so two SDK spellings do not
        // become two different sources in the case file.
        Assert.Equal("ShipperAccountId", identity.SourceKey);
    }

    [Fact]
    public void QueryParameter_ReadsAValueOutOfTheUrlQueryString()
    {
        var row = new TelemetryRowContext(Url: "https://edge.example-one.test/api/v2/checkout?aid=99&ref=email%20blast");

        Assert.Equal("99", row.QueryParameter("aid"));
        Assert.Equal("99", row.QueryParameter("AID"));
        Assert.Equal("email blast", row.QueryParameter("ref"));
        Assert.Null(row.QueryParameter("missing"));
    }

    [Fact]
    public void QueryParameter_WithNoQueryString_ReturnsNothing()
    {
        Assert.Null(new TelemetryRowContext(Url: "https://edge.example-one.test/api/v2/checkout").QueryParameter("aid"));
        Assert.Null(new TelemetryRowContext().QueryParameter("aid"));
    }

    [Fact]
    public void QueryParameter_IsReadableFromAUrlThatCarriesNoScheme()
    {
        // Telemetry url columns are routinely relative or scheme-less; refusing to read them would
        // make an identity source miss without anything saying so.
        Assert.Equal("99", new TelemetryRowContext(Url: "/api/v2/checkout?aid=99").QueryParameter("aid"));
        Assert.Equal("99", new TelemetryRowContext(Url: "edge.example-one.test/api/v2/checkout?aid=99").QueryParameter("aid"));
    }

    [Fact]
    public void Host_IsReadableFromAUrlThatCarriesNoScheme()
    {
        Assert.Equal("edge.example-one.test", new TelemetryRowContext(Url: "edge.example-one.test/api/v2/checkout").Host);
        Assert.Equal("edge.example-one.test", new TelemetryRowContext(Url: "//edge.example-one.test/api").Host);
        Assert.Equal("edge.example-one.test", new TelemetryRowContext(Url: "edge.example-one.test:8443/api").Host);
        Assert.Null(new TelemetryRowContext(Url: "/api/v2/checkout").Host);
    }

    [Fact]
    public void DerivedValues_FollowTheUrlWhenTheRowIsCopiedWithWith()
    {
        // A record copies fields verbatim, so anything cached would keep answering about the previous
        // row - a silent cross-row misattribution.
        var original = new TelemetryRowContext(Url: "https://first.example-one.test/a?aid=1");
        var copy = original with { Url = "https://second.example-one.test/b?aid=2" };

        Assert.Equal("second.example-one.test", copy.Host);
        Assert.Equal("/b", copy.Path);
        Assert.Equal("2", copy.QueryParameter("aid"));
    }

    [Fact]
    public void HostAndPath_AreLowercasedAndThePathExcludesTheQuery()
    {
        var row = new TelemetryRowContext(Url: "https://EDGE.Example-One.test/API/v2/Checkout?aid=99");

        Assert.Equal("edge.example-one.test", row.Host);
        Assert.Equal("/api/v2/checkout", row.Path);
    }

    // ---------- Provider: loud failure, caching, invalidation ----------

    [Fact]
    public async Task Provider_WhenTheProfileIsMissing_Throws()
    {
        var provider = Provider(new FakeCompanyProfileStore());

        var ex = await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => provider.GetAsync("profile-a"));
        Assert.Contains("profile-a", ex.Message);
    }

    [Fact]
    public async Task Provider_WhenTheProfileIsDisabled_Throws()
    {
        var store = new FakeCompanyProfileStore(ProfileA() with { Enabled = false });
        var provider = Provider(store);

        await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => provider.GetAsync("profile-a"));
    }

    [Fact]
    public async Task Provider_DoesNotCacheFailures()
    {
        var store = new FakeCompanyProfileStore();
        var provider = Provider(store);

        await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => provider.GetAsync("profile-a"));
        store.Add(ProfileA());

        // A stale "broken" must not outlive the fix that repaired it.
        var adapter = await provider.GetAsync("profile-a");
        Assert.Equal("profile-a", adapter.ProfileKey);
        Assert.Equal(2, store.GetCalls);
    }

    [Fact]
    public async Task Provider_WhenTheStoreThrows_SurfacesACompanyProfileResolutionException()
    {
        var store = new FakeCompanyProfileStore { Fault = new InvalidOperationException("catalogue offline") };

        var ex = await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => Provider(store).GetAsync("profile-a"));
        // The contract's exception has no inner-exception constructor, so the cause has to survive in the text.
        Assert.Contains("catalogue offline", ex.Message);
    }

    [Fact]
    public async Task Provider_CachesTheAdapterAndInvalidateForcesAReload()
    {
        var store = new FakeCompanyProfileStore(ProfileA());
        var provider = Provider(store);

        var first = await provider.GetAsync("profile-a");
        var second = await provider.GetAsync("profile-a");

        Assert.Same(first, second);
        Assert.Equal(1, store.GetCalls);

        provider.Invalidate();

        var third = await provider.GetAsync("profile-a");
        Assert.Equal(2, store.GetCalls);
        Assert.NotSame(first, third);
    }

    [Fact]
    public async Task Provider_ServesEachProfileFromItsOwnEntry()
    {
        var store = new FakeCompanyProfileStore(ProfileA(), ProfileB());
        var provider = Provider(store);

        var a = await provider.GetAsync("profile-a");
        var b = await provider.GetAsync("profile-b");

        Assert.Equal("profile-a", a.ProfileKey);
        Assert.Equal("profile-b", b.ProfileKey);
        Assert.Equal("freight", a.ResolveStep(SharedRow()).Service);
        Assert.Equal("playback", b.ResolveStep(SharedRow()).Service);
    }

    [Fact]
    public async Task Provider_WithNoKeyAndNoDefault_Throws()
    {
        var provider = Provider(new FakeCompanyProfileStore(ProfileA()), defaultProfileKey: "");

        await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => provider.GetAsync());
    }

    [Fact]
    public async Task Provider_WithNoKeyFallsBackToTheConfiguredDefault()
    {
        var provider = Provider(new FakeCompanyProfileStore(ProfileB()), defaultProfileKey: "profile-b");

        var adapter = await provider.GetAsync();

        Assert.Equal("profile-b", adapter.ProfileKey);
    }

    // ---------- fixtures ----------

    private static CompanyAdapterProvider Provider(FakeCompanyProfileStore store, string defaultProfileKey = "profile-a") =>
        new(store,
            Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = defaultProfileKey }),
            NullLogger<CompanyAdapterProvider>.Instance);

    /// <summary>Default (case-sensitive) comparer on purpose: the adapter must not be rescued by the dictionary.</summary>
    private static IReadOnlyDictionary<string, string> Dimensions(params (string Key, string Value)[] pairs)
    {
        var bag = new Dictionary<string, string>();
        foreach (var (key, value) in pairs) bag[key] = value;
        return bag;
    }

    /// <summary>A freight company: lanes, dispatch waterfall, shippers and depots.</summary>
    private static CompanyProfile ProfileA() => new(
        ProfileKey: "profile-a",
        DisplayName: "Meridian Freight",
        Labels: new CompanyLabels("Lane", "Waterfall", "Checkpoint", "Shipper", "Crew"),
        Services:
        [
            new ServiceDefinition(
                Key: "freight",
                DisplayName: "Freight",
                TelemetrySourceAliases: ["freight-core"],
                HostFilters: ["edge.example-one.test"],
                Flows:
                [
                    new FlowDefinition("dispatch", "Dispatch", [
                        new StepDefinition("label-printed", "Label printed", 20, [
                            new StepMatcher(StepMatcherKind.UrlContains, "/api/v2/checkout"),
                        ]),
                        new StepDefinition("quoted", "Quoted", 10, [
                            new StepMatcher(StepMatcherKind.UrlContains, "/api/v2/quote"),
                        ]),
                    ]),
                ],
                FallbackStep: "unclassified-lane-traffic"),
        ],
        // Declared out of Order on purpose: the walk must sort, not trust the list.
        EntityIdentitySources:
        [
            new EntityIdentitySource(3, EntityIdentitySourceKind.CustomDimension, "DepotCode", "depot"),
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "ShipperAccountId", "shipper"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.QueryParameter, "aid", "account"),
        ],
        Glossary: new CompanyGlossary("lane", "waterfall", "checkpoint", "shipper", "crew", "Moves pallets between depots."));

    /// <summary>A streaming company, given the identical row and expected to read it differently.</summary>
    private static CompanyProfile ProfileB() => new(
        ProfileKey: "profile-b",
        DisplayName: "Helios Streaming",
        Labels: new CompanyLabels("Channel", "Session", "Beat", "Subscriber", "Squad"),
        Services:
        [
            new ServiceDefinition(
                Key: "playback",
                DisplayName: "Playback",
                TelemetrySourceAliases: ["playback-core"],
                HostFilters: ["example-one.test"],
                Flows:
                [
                    new FlowDefinition("viewing", "Viewing", [
                        new StepDefinition("stream-started", "Stream started", 10, [
                            new StepMatcher(StepMatcherKind.OperationContains, "checkout"),
                        ]),
                    ]),
                ]),
        ],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "SubscriberId", "subscriber"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.Host, "", "origin"),
        ],
        Glossary: new CompanyGlossary("channel", "session", "beat", "subscriber", "squad"));

    /// <summary>Two unrelated product areas served from one host - the case the old contract could not express.</summary>
    private static CompanyProfile SharedHostProfile() => Shell(
    [
        new ServiceDefinition("cargo", "Cargo", [], ["shared.example-two.test"],
        [
            new FlowDefinition("movement", "Movement", [
                new StepDefinition("tracked", "Tracked", 10, [new StepMatcher(StepMatcherKind.UrlContains, "/track/")]),
            ]),
        ]),
        new ServiceDefinition("insurance", "Insurance", [], ["shared.example-two.test"],
        [
            new FlowDefinition("cover", "Cover", [
                new StepDefinition("quoted", "Quoted", 20, [new StepMatcher(StepMatcherKind.UrlContains, "/quote/")]),
            ]),
        ]),
    ]);

    /// <summary>Every identity kind, declared shuffled, so the ladder has to be walked by Order.</summary>
    private static CompanyProfile EntityLadderProfile() => Shell([]) with
    {
        EntityIdentitySources =
        [
            new EntityIdentitySource(4, EntityIdentitySourceKind.Host, "", "origin"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.QueryParameter, "aid", "account"),
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant"),
            new EntityIdentitySource(3, EntityIdentitySourceKind.RoleName, "", "worker"),
        ],
    };

    private static CompanyProfile OverlappingProfile(int broadOrder, int narrowOrder) => Shell(
    [
        new ServiceDefinition("one", "One", [], [],
        [
            new FlowDefinition("flow", "Flow", [
                new StepDefinition("broad", "Broad", broadOrder, [new StepMatcher(StepMatcherKind.UrlContains, "/api/")]),
                new StepDefinition("narrow", "Narrow", narrowOrder, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
            ]),
        ]),
    ]);

    private static CompanyProfile SingleStepProfile(params StepMatcher[] matchers) => Shell(
    [
        new ServiceDefinition("one", "One", [], [],
        [
            new FlowDefinition("flow", "Flow", [new StepDefinition("only", "Only", 10, matchers)]),
        ]),
    ]);

    private static CompanyProfile Shell(IReadOnlyList<ServiceDefinition> services) => new(
        ProfileKey: "fixture",
        DisplayName: "Fixture",
        Labels: new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        Services: services,
        EntityIdentitySources: [],
        Glossary: new CompanyGlossary("service", "flow", "step", "entity", "owner"));

    private sealed class FakeCompanyProfileStore(params CompanyProfile[] profiles) : ICompanyProfileStore
    {
        private readonly List<CompanyProfile> _profiles = [.. profiles];

        public int GetCalls { get; private set; }

        public Exception? Fault { get; set; }

        public void Add(CompanyProfile profile) => _profiles.Add(profile);

        public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default)
        {
            GetCalls++;
            if (Fault is not null) throw Fault;

            var hit = _profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<CompanyProfile?>(hit);
        }

        public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanyProfile>>(_profiles);

        public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default)
        {
            _profiles.RemoveAll(p => string.Equals(p.ProfileKey, profile.ProfileKey, StringComparison.OrdinalIgnoreCase));
            _profiles.Add(profile);
            return Task.FromResult(profile);
        }

        public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default)
        {
            _profiles.RemoveAll(p => string.Equals(p.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanyProfileAuditEntry>>([]);
    }
}
