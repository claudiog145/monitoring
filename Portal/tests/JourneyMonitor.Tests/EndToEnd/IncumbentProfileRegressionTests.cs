using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Tests.EndToEnd;

/// <summary>
/// The regression guard for the INCUMBENT. Arrivia is already in production; the platform was just
/// made company-independent, and the vocabulary that used to be C# is now one row in
/// monitoring.CompanyProfile. Nobody is harmed if the "demo" profile is wrong. If the arrivia
/// profile is wrong, the people who depend on this today get confident, wrong answers.
///
/// The expectations here are derived from <c>Infrastructure/Kql/scout-latency-priorities.kql.txt</c>
/// - the board query - and NOT from the seed. The board is the behaviour of record: it is what is
/// rendered, what is screenshotted into incident reviews, and what people already believe. A test
/// written by reading the seed back to itself would be green forever and would prove nothing.
///
/// Where the two disagree, each disagreement is isolated into its own named test with the reasoning
/// written out, and the assertion states what I believe is CORRECT - which is sometimes the KQL,
/// sometimes the seed, and in two cases NEITHER. Those tests are expected to be read, not skipped.
///
/// Like <c>Company/ArriviaProfileSeedTests</c>, the document under test is lifted out of the .sql
/// file that actually reaches the database. The extraction below is that test's approach, repeated
/// because its helpers are private to it: anchor + manual <c>''</c> unescaping, deliberately not a
/// regex, because a pattern that got the escaping subtly wrong would hand this file a corrupted
/// document and then blame the seed for it.
/// </summary>
public class IncumbentProfileRegressionTests
{
    private const string ProfileKey = "arrivia";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<CompanyProfile> Incumbent = new(LoadSeededProfile, isThreadSafe: true);

    /// <summary>Stateless apart from its caches, so one instance across the whole class also proves the caches do not leak between rows.</summary>
    private static readonly Lazy<ProfileDrivenAdapter> Adapter =
        new(() => new ProfileDrivenAdapter(Incumbent.Value), isThreadSafe: true);

    // =====================================================================================
    // The document
    // =====================================================================================

    /// <summary>
    /// A profile that does not validate must never be the one classifying production traffic, and
    /// the incumbent's is the one profile nobody gets to "fix later".
    /// </summary>
    [Fact]
    public void Incumbent_profile_validates_with_zero_errors()
    {
        var findings = CompanyProfileValidator.Validate(Incumbent.Value);
        var errors = findings.Where(f => f.IsError).ToList();

        Assert.True(errors.Count == 0,
            "The seeded 'arrivia' profile does not validate. It is the document already classifying production "
            + "traffic, so this is a live defect, not a backlog item:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(f => $"  [{f.Severity}] {f.Code} at {f.Path}: {f.Message}")));
    }

    /// <summary>
    /// A matcher kind this build cannot evaluate never fires, and traffic that never matches looks
    /// exactly like traffic that never happened. Asserted after driving the whole regression table
    /// through the adapter, because the adapter only records a kind once it has tried to use it.
    /// </summary>
    [Fact]
    public void Incumbent_profile_uses_no_matcher_kind_this_build_cannot_evaluate()
    {
        var adapter = new ProfileDrivenAdapter(Incumbent.Value);

        foreach (var url in AllProbeUrls())
        {
            adapter.ResolveStep(new TelemetryRowContext(Url: url));
            adapter.ResolveAffectedEntity(new TelemetryRowContext(Url: url));
        }

        Assert.Empty(adapter.UnknownMatcherKinds);
    }

    // =====================================================================================
    // TASK A, the core: does the seeded row classify the way the board does?
    // =====================================================================================

    /// <summary>
    /// One row per representative request, with the expected service/step read off the board KQL's
    /// <c>Vertical=case(...)</c> plus the matching per-vertical <c>StageNo</c> block.
    ///
    /// How to read a KQL expectation: the query assigns a Vertical from <c>Path</c> (lowercased,
    /// query string stripped by parse_url) and the telemetry Source, then runs that vertical's
    /// case() top to bottom and takes the first hit. Every expectation below was traced by hand
    /// through both blocks; where the trace and the seed agree, the case is here. Where they do
    /// not, the case is in the divergence section further down instead - nothing is quietly
    /// softened to keep this table green.
    ///
    /// Hostnames are placeholders: the profile declares no host filters, because Arrivia is
    /// white-label and every club browses on its own hostname. The cruise cases use a cruise-shaped
    /// host only so a reader can see which telemetry source the KQL would have had.
    /// </summary>
    [Theory]
    // ---- Shared head. The KQL tests login FIRST inside every one of its four case() blocks, so
    // login has to beat every vertical marker globally here. ----
    [InlineData("https://book.example-club.test/Account/Login.json", "shared", "01-login")]
    [InlineData("https://book.example-club.test/Account/LoginCheck", "shared", "01-login")]
    [InlineData("https://book.example-club.test/Hotel/Login.json", "shared", "01-login")]

    // ---- Cruises. KQL: Source startswith "Cruise" => Vertical "Cruise", then the cruise block. ----
    [InlineData("https://cruise.example-club.test/api/CruiseSearch", "cruises", "02-cruise-listing")]
    [InlineData("https://cruise.example-club.test/api/GetCustomerId", "cruises", "02-cruise-listing")]
    [InlineData("https://cruise.example-club.test/api/SearchMatrix", "cruises", "02-cruise-listing")]
    [InlineData("https://cruise.example-club.test/api/CruiseItinerary", "cruises", "03-cruise-detail")]
    [InlineData("https://cruise.example-club.test/api/CruiseCategory", "cruises", "04-rooms-and-rates")]
    // /cruisecabinonhold ENDS WITH /cruisecabinonhold but CONTAINS /cruisecabin. endswith->contains
    // is why the seed has to order 06 ahead of 05; these two rows are what proves it still works.
    [InlineData("https://cruise.example-club.test/api/CruiseCabin", "cruises", "05-total-price")]
    [InlineData("https://cruise.example-club.test/api/CruiseCabinOnHold", "cruises", "06-cabin-selection")]
    [InlineData("https://cruise.example-club.test/secure/cruisetravelwith.jsp", "cruises", "07-passenger")]
    [InlineData("https://cruise.example-club.test/api/guest/get-guests", "cruises", "07-passenger")]
    [InlineData("https://cruise.example-club.test/api/FeaturesAndOptions", "cruises", "08-options")]
    [InlineData("https://cruise.example-club.test/api/CruisePayment", "cruises", "09-payment")]
    // The KQL needed a second endswith rule for this one; contains covers both. Same outcome.
    [InlineData("https://cruise.example-club.test/api/CruisePaymentProc", "cruises", "09-payment")]
    [InlineData("https://cruise.example-club.test/secure/cruisesummary.jsp", "cruises", "10-review")]
    [InlineData("https://cruise.example-club.test/api/CruiseConfirm", "cruises", "11-confirmation")]

    // ---- Cars. KQL puts Cars ahead of Hotels in the Vertical case(), which is why "carsearch.json"
    // is a car search and not a hotel search even though it contains "search.json". ----
    [InlineData("https://book.example-club.test/Car/BackgroundCarSearch", "cars", "03-car-results")]
    [InlineData("https://book.example-club.test/Car/BackgroundCarSearch.json", "cars", "03-car-results")]
    [InlineData("https://book.example-club.test/cars/results?page=2", "cars", "03-car-results")]
    [InlineData("https://book.example-club.test/Car/CarLocationSearch", "cars", "02-car-search")]
    [InlineData("https://book.example-club.test/Car/CarSearch.json", "cars", "02-car-search")]
    [InlineData("https://book.example-club.test/api/SearchLocations", "cars", "02-car-search")]
    [InlineData("https://book.example-club.test/Car/CarBooking", "cars", "05-prebook")]

    // ---- Hotels. 03 before 02 for the same reason the KQL tests backgroundhotelsearch first:
    // "BackgroundHotelSearch.json" literally contains "search.json". ----
    [InlineData("https://book.example-club.test/Hotel/BackgroundHotelSearch.json", "hotels", "03-hotel-results")]
    [InlineData("https://book.example-club.test/Hotel/Search.json", "hotels", "02-hotel-search")]
    [InlineData("https://book.example-club.test/Hotel/SearchRooms", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/GetCancelPolicy", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/ZentrumHubHotelDetailsContent", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/GetReviews", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/Book.json", "hotels", "05-prebook")]

    // ---- Shared tail: the endpoints the KQL repeats verbatim in all four of its blocks, declared
    // once in the seed. 08 before 07 before 06, matching the case() order. ----
    [InlineData("https://book.example-club.test/Booking/PurchaseComplete", "shared", "08-confirmation")]
    [InlineData("https://book.example-club.test/Booking/CompletePurchase", "shared", "07-booking")]
    [InlineData("https://book.example-club.test/api/Determine3dsEligibility", "shared", "07-booking")]
    [InlineData("https://book.example-club.test/api/reservations/Purchase", "shared", "06-checkout")]
    [InlineData("https://book.example-club.test/api/UserPeople_Read", "shared", "06-checkout")]
    [InlineData("https://book.example-club.test/api/AddRemoveInsurance", "shared", "06-checkout")]
    [InlineData("https://book.example-club.test/api/TravelerProfile", "shared", "06-checkout")]
    public void Seeded_incumbent_classifies_board_traffic_exactly_as_the_board_does(
        string url, string expectedService, string expectedStep)
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(Url: url));

        Assert.True(match.IsResolved,
            $"'{url}' is classified by scout-latency-priorities.kql.txt as {expectedService}/{expectedStep}, but the "
            + "seeded arrivia profile matched no rule at all. The migration dropped a marker.");

        Assert.Equal(expectedService, match.Service);
        Assert.Equal(expectedStep, match.Step);
        Assert.Equal("booking", match.Flow);
        Assert.Equal(CompanyResolution.PatternMatch, match.ResolvedBy);
    }

    /// <summary>
    /// The legacy query parks everything it does not recognise in a pseudo-vertical called
    /// "Shared / unclassified" at a pseudo-stage called "Unmapped operation" - which makes a mapping
    /// gap indistinguishable from a real classification, and measurably so: the KQL header records
    /// that 100% of the rank=3 rows measured on real data were "Unmapped operation".
    ///
    /// The seed declares no fallbackStep on any service, so the same traffic comes back honestly
    /// unresolved. A wrong-but-plausible step is the failure mode that matters here: an on-call
    /// engineer will act on "06 Checkout is slow" and will not act on "unresolved".
    /// </summary>
    [Theory]
    [InlineData("https://book.example-club.test/static/app.bundle.css")]
    [InlineData("https://book.example-club.test/api/health")]
    [InlineData("https://book.example-club.test/Member/Preferences")]
    [InlineData("https://book.example-club.test/Account/ChangePassword")]
    [InlineData("https://book.example-club.test/api/Content/Banners")]
    [InlineData("https://book.example-club.test/")]
    public void Traffic_the_incumbent_profile_does_not_describe_is_unresolved_not_mislabelled(string url)
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(Url: url));

        Assert.False(match.IsResolved,
            $"'{url}' is 'Unmapped operation' on the board, but the seeded profile claimed it as "
            + $"{match.Service}/{match.Step}. A wrong-but-plausible step is worse than no step.");
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
        Assert.Null(match.Service);
        Assert.Null(match.Step);
    }

    /// <summary>A null row is not a mapping gap and must not be reported as our outage either.</summary>
    [Fact]
    public void A_row_with_nothing_to_read_is_unresolved_not_unavailable()
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext());

        Assert.False(match.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
    }

    // =====================================================================================
    // Identity: who gets blamed
    // =====================================================================================

    /// <summary>
    /// The seeded chain has to be the board's <c>Club=coalesce(...)</c> chain, in the same order.
    /// Reordering it silently re-attributes incidents: a row carrying both VacationClubId and
    /// club_id belongs to the first one, and swapping them blames a different club for the same
    /// outage without anything looking broken.
    ///
    /// The KQL chain also lists the casing variants VacationClubID and cobrandId as separate links;
    /// they are absent here on purpose, because TelemetryRowContext.CustomDimension already falls
    /// back to a case-insensitive lookup. That is asserted below rather than assumed.
    ///
    /// The chain ends in a host source the board's coalesce does not have, and that divergence is
    /// deliberate: the stamped dimensions were measured covering under 5% of the estate, so without
    /// it ~95% of rows carry no entity at all. CobrandId is absent for the opposite reason - zero
    /// occurrences in 23,802 sampled rows.
    /// </summary>
    [Fact]
    public void Identity_chain_is_the_boards_coalesce_chain_in_the_boards_order()
    {
        var ordered = Incumbent.Value.EntityIdentitySources
            .OrderBy(s => s.Order)
            .Select(s => $"{s.Kind}:{s.Key}")
            .ToArray();

        Assert.Equal(
            new[]
            {
                "custom-dimension:VacationClubId",
                "custom-dimension:ClubId",
                "custom-dimension:club_id",
                "query-parameter:cobrand",
                "query-parameter:cobrandId",
                "host:",
            },
            ordered);

        // Every source that names a club id says so; the host says "host" instead, because what it
        // produces is a hostname and ClubIdentityResolver - not this chain - is what turns it into
        // a club. Declaring it "club" would emit "club:members.example.com" and resolve nothing.
        var sources = Incumbent.Value.EntityIdentitySources.OrderBy(s => s.Order).ToArray();
        Assert.All(sources[..^1], s => Assert.Equal("club", s.EntityKind));
        Assert.Equal("host", sources[^1].EntityKind);
    }

    /// <summary>
    /// Highest-priority source wins AND is reported. "Reported" is the part that regresses quietly:
    /// an identity recovered from a query parameter is not worth the same as one the application
    /// stamped, and a caller that cannot tell them apart will trust both equally.
    /// </summary>
    [Fact]
    public void Identity_prefers_the_highest_priority_source_and_names_it()
    {
        var identity = Adapter.Value.ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://book.example-club.test/Hotel/Search.json?cobrand=9999",
            CustomDimensions: new Dictionary<string, string>
            {
                ["CobrandId"] = "308",
                ["club_id"] = "77",
                ["VacationClubId"] = "15639",
            }));

        Assert.True(identity.IsResolved);
        Assert.Equal("15639", identity.Value);
        Assert.Equal("VacationClubId", identity.SourceKey);
        Assert.Equal("club", identity.Kind);
        Assert.Equal(CompanyResolution.ExactMatch, identity.ResolvedBy);
    }

    /// <summary>A row carrying ONLY a lower-priority source still resolves, and reports THAT source.</summary>
    [Theory]
    [InlineData("ClubId", "412", "ClubId")]
    [InlineData("club_id", "77", "club_id")]
    // The SDK's casing is not the profile author's problem: the KQL spells this variant out as its
    // own coalesce link, the seed does not, and the row context is what closes the gap.
    [InlineData("VacationClubID", "15639", "VacationClubId")]
    public void Identity_found_by_a_lower_ranked_source_reports_that_source(
        string dimension, string value, string expectedSourceKey)
    {
        var identity = Adapter.Value.ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://book.example-club.test/Hotel/Search.json",
            CustomDimensions: new Dictionary<string, string> { [dimension] = value }));

        Assert.True(identity.IsResolved,
            $"The board resolves Club from customDimensions.{dimension}; the seeded profile did not.");
        Assert.Equal(value, identity.Value);
        Assert.Equal(expectedSourceKey, identity.SourceKey);
        Assert.Equal("club", identity.Kind);
    }

    /// <summary>The two query parameters at the tail of the chain are the last thing that still names a club.</summary>
    [Theory]
    [InlineData("https://book.example-club.test/Hotel/Search.json?cobrand=4471", "cobrand", "4471")]
    [InlineData("https://book.example-club.test/Hotel/Search.json?page=2&cobrandId=4471", "cobrandId", "4471")]
    public void Identity_falls_back_to_a_query_parameter(string url, string expectedSourceKey, string expectedValue)
    {
        var identity = Adapter.Value.ResolveAffectedEntity(new TelemetryRowContext(Url: url));

        Assert.True(identity.IsResolved);
        Assert.Equal(expectedValue, identity.Value);
        Assert.Equal(expectedSourceKey, identity.SourceKey);
    }

    /// <summary>
    /// A row carrying no club id resolves to its HOST, and says that is what it is.
    ///
    /// This test used to assert the opposite, and the objection it was built on still stands:
    /// "host:book.example-club.test" sitting in a Club column is a fabricated identity, because on
    /// a white-label estate one club owns several hostnames and a CDN hostname owns none. What
    /// changed is that the chain no longer makes that claim - the source declares
    /// <c>entityKind: "host"</c>, so the key is "host:..." and <c>Kind</c> reads "host", and it is
    /// ClubIdentityResolver that maps it to a club through brand.ClubInventory.Site while
    /// reporting whether it got there by host-exact or host-base-domain. The hostname is reported
    /// as a hostname; the catalogue, which can be checked, is what names the club.
    ///
    /// Keeping the old behaviour was measured to cost ~95% of the estate: 158,376 requests and
    /// 25,410 exceptions over 3h carried a url, while the stamped dimensions covered ClubId 852,
    /// club_id 342, VacationClubId 6. Every package reported "[host/club unavailable]" and the
    /// Inspector refused to run its three club-scoped tools for want of an entity.
    /// </summary>
    [Fact]
    public void Identity_falls_back_to_the_host_and_reports_it_as_a_host()
    {
        var identity = Adapter.Value.ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://book.example-club.test/Hotel/Search.json?sort=price",
            CustomDimensions: new Dictionary<string, string> { ["CorrelationId"] = "3f1a" }));

        Assert.True(identity.IsResolved);
        Assert.Equal("book.example-club.test", identity.Value);
        Assert.Equal("host", identity.Kind);
    }

    /// <summary>With no club id AND no url there is nothing left to read, and the row stays explicitly unidentified rather than guessed.</summary>
    [Fact]
    public void Identity_is_unresolved_when_there_is_no_club_id_and_no_url()
    {
        var identity = Adapter.Value.ResolveAffectedEntity(new TelemetryRowContext(
            CustomDimensions: new Dictionary<string, string> { ["CorrelationId"] = "3f1a" }));

        Assert.False(identity.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, identity.ResolvedBy);
        Assert.Null(identity.Value);
        Assert.Null(identity.SourceKey);
        Assert.Null(identity.Kind);
    }

    // =====================================================================================
    // DIVERGENCES. Read these.
    //
    // Everything below is a place where the seeded profile and the board KQL do not agree. Each
    // test asserts what I believe is CORRECT, which is not automatically either of them. The ones
    // that FAIL are failing on purpose and are described in the track report; deleting them or
    // relaxing them to match the current seed would convert a known defect into an unknown one.
    // =====================================================================================

    /// <summary>
    /// DIVERGENCE 1 - RED, and the most serious one. The broad Cruise markers are no longer
    /// partitioned by telemetry source, so they claim SOR traffic.
    ///
    /// In the KQL, Cruise is selected by WHICH APPLICATION INSIGHTS APP the row came from
    /// (<c>Source startswith "Cruise"</c>). Only rows from cruise-core/cruise-api ever reach the
    /// cruise case() block, which is what makes a marker as broad as <c>Path contains "passenger"</c>
    /// safe there. The profile model has no source partition - ProfileDrivenAdapter flattens every
    /// step of every service and sorts by <c>order</c> - and the cruise steps were given orders
    /// 10-19, ahead of Cars (30), Hotels (40) and the shared tail (90+). So those broad markers now
    /// outrank every SOR rule, globally.
    ///
    /// The seed's own comment says the three broadest cruise markers ("payment", "confirm",
    /// "booking") were dropped for exactly this reason. The reasoning is right; it was applied to
    /// three markers and not to the rest. <c>passenger</c>, <c>/insurance</c> and
    /// <c>/getcustomerid</c> are the same hazard and survived.
    ///
    /// I believe the KQL is correct on all three rows below. The fix is not to delete the markers -
    /// they are load-bearing for cruise traffic - it is to give ServiceDefinition a telemetry-source
    /// predicate (the aliases are already declared and unused by the adapter) or, failing that, to
    /// re-order the cruise band after the shared band and accept the smaller loss.
    /// </summary>
    [Theory]
    // A checkout call carrying a passenger count. Board: Path="/api/reservations/purchase" (the
    // query string is stripped by parse_url), Vertical="Shared / unclassified", StageNo=6.
    [InlineData("https://book.example-club.test/api/reservations/Purchase?passengers=2", "shared", "06-checkout")]
    // Board: Path="/hotel/book.json" => Vertical "Hotels" (book.json), StageNo=5 "05 PreBook".
    [InlineData("https://book.example-club.test/Hotel/Book.json?passengers=2&rooms=1", "hotels", "05-prebook")]
    public void Broad_cruise_markers_must_not_claim_storefront_traffic(
        string url, string expectedService, string expectedStep)
    {
        // A real board row always says which resource it came from, and that is what stops a broad
        // marker belonging to one product area from claiming another's traffic.
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(Url: url, SourceAlias: "SOR"));

        Assert.True(match.IsResolved, $"'{url}' resolved to nothing at all.");
        Assert.Equal(expectedService, match.Service);
        Assert.Equal(expectedStep, match.Step);
    }

    /// <summary>
    /// DIVERGENCE 1b - RED, same root cause, different symptom: SOR traffic the board leaves
    /// unmapped is instead given a confident CRUISE stage.
    ///
    /// <c>/getcustomerid</c> and <c>/insurance</c> are generic endpoint names. On the cruise apps
    /// they mean "cruise listing" and "cruise options"; on the storefront they mean neither, and
    /// the board says so by leaving them at StageNo=0 / "Unmapped operation". Here they are claimed
    /// by the cruise service at order 10 and 16, before any storefront rule is even tried.
    ///
    /// This is strictly worse than the mapping gap it replaces: an unmapped row prompts someone to
    /// map it, a row labelled "Cruises / 02 Cruise Listing" prompts the cruise team to investigate
    /// a storefront incident.
    /// </summary>
    [Theory]
    [InlineData("https://book.example-club.test/api/Account/GetCustomerId")]
    [InlineData("https://book.example-club.test/api/Insurance/Offers")]
    public void Generic_endpoint_names_on_the_storefront_must_not_become_cruise_stages(string url)
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(Url: url, SourceAlias: "SOR"));

        Assert.False(match.IsResolved,
            $"'{url}' is 'Unmapped operation' on the board, but the seeded profile labelled it "
            + $"{match.Service}/{match.Step} - a cruise stage on a storefront request.");
    }

    /// <summary>
    /// DIVERGENCE 2 - RED. Matchers read the whole URL; the board reads only the path.
    ///
    /// The board computes <c>Path=tolower(tostring(parse_url(url).Path))</c> and every single
    /// marker is tested against that, so the query string cannot influence the classification.
    /// <c>url-contains</c> tests <c>TelemetryRowContext.UrlText</c>, which is the full url including
    /// scheme, host and query string. TelemetryRowContext documents that as intentional ("a marker
    /// can legitimately sit in the query string") and for some future company it will be; for the
    /// incumbent, every one of whose markers is a path marker, it is purely a source of false
    /// positives.
    ///
    /// The case below is an ordinary ASP.NET MVC login redirect. The board calls it unmapped. The
    /// profile calls it a hotel search, because the escaped return url contains "search.json". I
    /// believe neither label is ideal - this is a login page and ought to be 01 Login - but the one
    /// thing it certainly is not is a hotel search, so the assertion is "not mislabelled".
    ///
    /// The fix belongs in the model, not in the seed: a <c>path-contains</c> matcher kind, or a
    /// per-matcher scope. Rewriting the incumbent's markers to dodge it would leave the trap armed
    /// for the next company.
    /// </summary>
    [Fact]
    public void A_marker_hiding_in_the_query_string_must_not_classify_the_request()
    {
        const string url = "https://book.example-club.test/Account/Login?ReturnUrl=%2fHotel%2fSearch.json";

        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(Url: url));

        Assert.False(match.IsResolved && match.Service == "hotels",
            "A login redirect whose ReturnUrl happens to mention Hotel/Search.json was classified as "
            + $"{match.Service}/{match.Step}. The board classifies on parse_url(url).Path, so the query string "
            + "cannot do this there; url-contains reads the whole url and can.");
    }

    /// <summary>
    /// DIVERGENCE 3 - GREEN, and here the SEED is right and the board is wrong.
    ///
    /// The board matches most cruise stages with <c>endswith</c>, so any sub-route of a stage
    /// endpoint falls off the end of the case() and lands in "Unmapped operation" - which is
    /// precisely the bucket the KQL's own header calls out as useless. <c>contains</c> keeps the
    /// sub-route attached to the stage it belongs to.
    /// </summary>
    [Theory]
    [InlineData("https://cruise.example-club.test/api/CruiseItinerary/Details", "cruises", "03-cruise-detail")]
    [InlineData("https://cruise.example-club.test/api/CruiseCategory?shipCode=NCL", "cruises", "04-rooms-and-rates")]
    public void Cruise_sub_routes_keep_their_stage_instead_of_falling_into_unmapped(
        string url, string expectedService, string expectedStep)
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(Url: url));

        Assert.Equal(expectedService, match.Service);
        Assert.Equal(expectedStep, match.Step);
    }

    /// <summary>
    /// DIVERGENCE 4 - GREEN, seed right, board wrong, and it fixes a live reporting hole.
    ///
    /// The board gives Cars a "09 Cancellation" rule and gives Hotels none. Worse, that rule is
    /// almost unreachable: <c>/api/reservations/cancel</c> carries no car marker, so the Vertical
    /// case() routes it to "Shared / unclassified", whose block has no cancellation rule at all -
    /// the Cars 09 branch is only ever reached when the row happens to be stamped
    /// customDimensions.Vertical="Car". Cancellations are therefore mostly invisible on the board
    /// today. The seed moves the endpoint to the shared service, where it is one rule for everyone.
    /// </summary>
    [Fact]
    public void Cancellation_is_one_shared_stage_rather_than_a_cars_only_rule_that_rarely_fires()
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(
            Url: "https://book.example-club.test/api/reservations/Cancel"));

        Assert.True(match.IsResolved);
        Assert.Equal("shared", match.Service);
        Assert.Equal("09-cancellation", match.Step);
    }

    /// <summary>
    /// DIVERGENCE 5 - GREEN assertion, but it records a real loss the demo should admit to.
    ///
    /// The board routes a shared endpoint into a VERTICAL when the row is stamped
    /// <c>customDimensions.Verticals</c>/<c>.Vertical</c>: the same /reservations/purchase call
    /// appears under Hotels for a hotel booking and under Cars for a car booking. The profile model
    /// has no custom-dimension step matcher, so every checkout, booking and confirmation collapses
    /// into one "shared" bucket.
    ///
    /// I assert "shared" because I believe it is the correct answer for the model as designed - the
    /// endpoint genuinely IS shared, and inventing a vertical from a hint dimension is how one
    /// vertical's latency ends up charged to another. But the consequence is not cosmetic: the
    /// incumbent loses per-vertical visibility of the entire bottom half of the funnel (stages 06,
    /// 07, 08), which is where the money is. If that matters to them, it needs a custom-dimension
    /// matcher kind, not a seed edit.
    /// </summary>
    [Fact]
    public void A_vertical_hint_dimension_no_longer_splits_the_shared_funnel_tail()
    {
        var match = Adapter.Value.ResolveStep(new TelemetryRowContext(
            Url: "https://book.example-club.test/api/reservations/Purchase",
            CustomDimensions: new Dictionary<string, string> { ["Vertical"] = "Hotel" }));

        Assert.Equal("shared", match.Service);
        Assert.Equal("06-checkout", match.Step);
    }

    // =====================================================================================
    // Reading the seed - the approach from Company/ArriviaProfileSeedTests, repeated because
    // its helpers are private to that class.
    // =====================================================================================

    private static IEnumerable<string> AllProbeUrls() =>
    [
        "https://book.example-club.test/Account/Login.json",
        "https://book.example-club.test/Hotel/Search.json?cobrand=4471",
        "https://cruise.example-club.test/api/CruiseCabinOnHold",
        "https://book.example-club.test/api/reservations/Cancel",
        "https://book.example-club.test/api/health",
    ];

    private static CompanyProfile LoadSeededProfile()
    {
        var sql = ReadSeedSql();
        var json = ExtractSeedJson(sql, ProfileKey);

        return JsonSerializer.Deserialize<CompanyProfile>(json, JsonOptions)
               ?? throw new InvalidOperationException(
                   $"The '{ProfileKey}' seed in CreateMonitoringCompanyProfile.sql deserialised to null.");
    }

    /// <summary>
    /// Lifts the JSON out of the T-SQL literal, undoing the <c>''</c> escaping. Deliberately not a
    /// regex: the document contains braces, quotes and brackets, and a pattern that got the escaping
    /// subtly wrong would hand this test a corrupted document and blame the seed for it.
    /// </summary>
    private static string ExtractSeedJson(string sql, string profileKey)
    {
        var anchor = $"VALUES (N'{profileKey}', N'";
        var start = sql.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the '{profileKey}' seed in CreateMonitoringCompanyProfile.sql (looked for \"{anchor}\"). "
                + "If the INSERT was reformatted, update this extractor - the test must read the literal that is "
                + "actually applied, never a copy.");
        }

        var json = new StringBuilder();
        for (var i = start + anchor.Length; i < sql.Length; i++)
        {
            if (sql[i] != '\'')
            {
                json.Append(sql[i]);
                continue;
            }

            if (i + 1 < sql.Length && sql[i + 1] == '\'')
            {
                json.Append('\'');
                i++;
                continue;
            }

            return json.ToString();
        }

        throw new InvalidOperationException(
            $"The '{profileKey}' seed literal is never closed. The T-SQL string quoting in "
            + "CreateMonitoringCompanyProfile.sql is broken and the script will not apply.");
    }

    private static string ReadSeedSql()
    {
        var path = Path.Combine(
            FindPortalDirectory(),
            "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringCompanyProfile.sql");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The company profile seed script '{path}' is missing. This test asserts on the document that "
                + "reaches the database, so it fails rather than silently testing nothing.");
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Walks up from the test assembly, then falls back to the compile-time path of this file. The
    /// fallback is load-bearing: this repo is built with <c>--artifacts-path C:\Temp\...</c>, which
    /// puts the binaries outside the repository entirely.
    /// </summary>
    private static string FindPortalDirectory()
    {
        if (TryFindPortal(AppContext.BaseDirectory, out var fromBinaries)) return fromBinaries;
        if (TryFindPortal(Path.GetDirectoryName(ThisFilePath()), out var fromSource)) return fromSource;

        throw new InvalidOperationException(
            "Could not locate the Portal source tree (looked for 'JourneyMonitor.slnx' walking up from "
            + $"'{AppContext.BaseDirectory}' and from '{ThisFilePath()}').");
    }

    private static bool TryFindPortal(string? startDirectory, out string portal)
    {
        portal = "";
        var dir = startDirectory is null ? null : new DirectoryInfo(startDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JourneyMonitor.slnx")))
            {
                portal = dir.FullName;
                return true;
            }

            var nested = Path.Combine(dir.FullName, "Portal");
            if (File.Exists(Path.Combine(nested, "JourneyMonitor.slnx")))
            {
                portal = nested;
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }

    private static string ThisFilePath([CallerFilePath] string path = "") => path;
}
