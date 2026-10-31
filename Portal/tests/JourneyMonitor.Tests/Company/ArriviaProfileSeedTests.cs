using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// The pilot customer is a ROW now, not a class, so the thing that has to be tested is the row.
///
/// Every assertion here runs against the JSON literal lifted out of
/// <c>CreateMonitoringCompanyProfile.sql</c> - not a C# rebuild of it, not a copy kept in the test
/// project. A copy would pass forever while the seed that actually reaches the database rots, and
/// the failure mode of a rotten seed is silent: <c>{}</c> deserialises happily into a
/// <see cref="CompanyProfile"/> whose every member is null, and then throws at the first use,
/// hours later, inside an agent.
/// </summary>
public class ArriviaProfileSeedTests
{
    private const string ProfileKey = "arrivia";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> SeedSql = new(ReadSeedSql, isThreadSafe: true);

    private static readonly Lazy<string> SeedJson =
        new(() => ExtractSeedJson(SeedSql.Value, ProfileKey), isThreadSafe: true);

    private static readonly Lazy<CompanyProfile> Profile = new(
        () => JsonSerializer.Deserialize<CompanyProfile>(SeedJson.Value, JsonOptions)
              ?? throw new InvalidOperationException("The Arrivia seed deserialised to null."),
        isThreadSafe: true);

    // -------------------------------------------------------------------------------------
    // The document itself
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The guard this whole file exists for. A document that is merely well-formed JSON is not a
    /// usable profile: System.Text.Json fills every member it cannot find with null, including the
    /// ones declared non-nullable, and nothing complains until something dereferences them.
    /// </summary>
    [Fact]
    public void Seed_document_deserialises_with_no_null_member_anywhere()
    {
        var profile = Profile.Value;

        var problems = new List<string>();
        AssertNoNulls(profile, "$", problems, new NullabilityInfoContext());

        Assert.True(problems.Count == 0,
            "The Arrivia seed in CreateMonitoringCompanyProfile.sql deserialises with null in member(s) that are " +
            "declared non-nullable. The document is structurally incomplete and will throw at first use:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));

        Assert.Equal(ProfileKey, profile.ProfileKey);
        Assert.Equal("Arrivia", profile.DisplayName);
        Assert.True(profile.Enabled);
        Assert.NotEmpty(profile.Services);
        Assert.NotEmpty(profile.EntityIdentitySources);
    }

    /// <summary>The labels are the whole point of the profile: the platform's roles, this customer's words.</summary>
    [Fact]
    public void Seed_labels_and_glossary_use_the_customer_vocabulary()
    {
        var profile = Profile.Value;

        Assert.Equal("Vertical", profile.Labels.Service);
        Assert.Equal("Journey", profile.Labels.Flow);
        Assert.Equal("Stage", profile.Labels.Step);
        Assert.Equal("Club", profile.Labels.Entity);

        Assert.Equal("vertical", profile.Glossary.ServiceNoun);
        Assert.Equal("stage", profile.Glossary.StepNoun);
        Assert.Equal("club", profile.Glossary.EntityNoun);
        Assert.False(string.IsNullOrWhiteSpace(profile.Glossary.Description));
    }

    /// <summary>
    /// PascalCase in the seed would deserialise to a profile whose every field is null, silently -
    /// the store reads this document with <c>JsonSerializerDefaults.Web</c>. Cheap to assert, and
    /// it names the real cause instead of leaving a reader staring at a null reference.
    /// </summary>
    [Fact]
    public void Seed_json_is_camel_case()
    {
        var json = SeedJson.Value;

        Assert.Contains("\"profileKey\"", json, StringComparison.Ordinal);
        Assert.Contains("\"entityIdentitySources\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ProfileKey\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Services\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The script is applied on every container start and by hand to already-provisioned
    /// databases. An UPDATE or a MERGE in the seed block would overwrite a profile an operator
    /// edited in the portal on the next restart - a data loss that looks like the portal forgetting.
    /// </summary>
    [Fact]
    public void Seed_is_insert_only_and_guarded_by_an_existence_check()
    {
        var sql = SeedSql.Value;

        var guard = $"IF NOT EXISTS (SELECT 1 FROM monitoring.CompanyProfile WITH (UPDLOCK, HOLDLOCK) WHERE ProfileKey = N'{ProfileKey}')";
        var guardIndex = sql.IndexOf(guard, StringComparison.Ordinal);
        Assert.True(guardIndex >= 0,
            $"The {ProfileKey} seed is not wrapped in '{guard}'. Without it, re-running the script overwrites an " +
            "edited profile, and two concurrent applications can both insert.");

        var insertIndex = sql.IndexOf($"VALUES (N'{ProfileKey}', N'", StringComparison.Ordinal);
        Assert.True(insertIndex > guardIndex,
            $"The {ProfileKey} INSERT must sit inside its existence guard.");

        var block = sql[guardIndex..insertIndex];
        Assert.DoesNotContain("MERGE", block, StringComparison.OrdinalIgnoreCase);
        // "UPDATE SET" rather than "UPDATE": the column list right below contains UpdatedBy.
        Assert.DoesNotContain("UPDATE SET", block, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The seed is edited by hand, so the single-quote escaping is edited by hand too. Asserting on
    /// a value that carries an apostrophe proves the <c>''</c> doubling survives extraction, rather
    /// than leaving it to be discovered when T-SQL refuses the script.
    /// </summary>
    [Fact]
    public void Seed_escapes_single_quotes_inside_the_tsql_literal()
    {
        Assert.Contains("KQL''s", SeedSql.Value, StringComparison.Ordinal);
        Assert.Contains("KQL's", SeedJson.Value, StringComparison.Ordinal);
        Assert.Contains("KQL's", Profile.Value.Notes ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Seed_document_has_no_validation_errors()
    {
        var findings = CompanyProfileValidator.Validate(Profile.Value);

        var errors = findings.Where(f => f.IsError).ToList();
        Assert.True(errors.Count == 0,
            "The Arrivia seed does not validate:" + Environment.NewLine + Describe(errors));

        var warnings = findings.Where(f => !f.IsError).ToList();
        Assert.True(warnings.Count == 0,
            "The Arrivia seed validates but raises warnings. Either fix the seed or decide the warning is wrong:" +
            Environment.NewLine + Describe(warnings));
    }

    // -------------------------------------------------------------------------------------
    // What the document actually classifies
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// One row per representative url, run through the real adapter over the real seed. This is the
    /// table that says what the customer's funnel MEANS; if a marker moves in the seed, exactly the
    /// rows it governs fail here and the rest stay green.
    ///
    /// Hostnames are placeholders on purpose: the profile declares no host filters, because Arrivia
    /// is white-label and every club browses on its own hostname.
    /// </summary>
    [Theory]
    // Shared - login is tested before every vertical marker, exactly as each case() block does.
    [InlineData("https://book.example-club.test/Account/Login.json", "shared", "01-login")]
    [InlineData("https://book.example-club.test/Account/LoginCheck", "shared", "01-login")]
    // Hotels. BackgroundHotelSearch.json CONTAINS "search.json", so results must beat search.
    [InlineData("https://book.example-club.test/Hotel/BackgroundHotelSearch.json", "hotels", "03-hotel-results")]
    [InlineData("https://book.example-club.test/Hotel/Search.json", "hotels", "02-hotel-search")]
    [InlineData("https://book.example-club.test/Hotel/SearchRooms", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/GetCancelPolicy", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/ZentrumHubHotelDetailsContent", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/GetReviews", "hotels", "04-hotel-details")]
    [InlineData("https://book.example-club.test/Hotel/Book.json", "hotels", "05-prebook")]
    // Cars. CarSearch.json also contains "search.json": Cars is ordered ahead of Hotels, as the
    // vertical case() orders it, so the car rule wins.
    [InlineData("https://book.example-club.test/Car/BackgroundCarSearch", "cars", "03-car-results")]
    [InlineData("https://book.example-club.test/cars/results?page=2", "cars", "03-car-results")]
    [InlineData("https://book.example-club.test/Car/CarLocationSearch", "cars", "02-car-search")]
    [InlineData("https://book.example-club.test/Car/CarSearch.json", "cars", "02-car-search")]
    [InlineData("https://book.example-club.test/Car/CarBooking", "cars", "05-prebook")]
    // Cruises.
    [InlineData("https://cruise.example-club.test/api/CruiseSearch", "cruises", "02-cruise-listing")]
    [InlineData("https://cruise.example-club.test/api/SearchMatrix", "cruises", "02-cruise-listing")]
    [InlineData("https://cruise.example-club.test/api/CruiseItinerary", "cruises", "03-cruise-detail")]
    [InlineData("https://cruise.example-club.test/api/CruiseCategory", "cruises", "04-rooms-and-rates")]
    // /cruisecabinonhold CONTAINS /cruisecabin once endswith becomes contains, so it is ordered first.
    [InlineData("https://cruise.example-club.test/api/CruiseCabinOnHold", "cruises", "06-cabin-selection")]
    [InlineData("https://cruise.example-club.test/api/CruiseCabin", "cruises", "05-total-price")]
    [InlineData("https://cruise.example-club.test/secure/cruisetravelwith.jsp", "cruises", "07-passenger")]
    [InlineData("https://cruise.example-club.test/api/guest/get-guests", "cruises", "07-passenger")]
    [InlineData("https://cruise.example-club.test/api/FeaturesAndOptions", "cruises", "08-options")]
    // One rule covers /cruisepayment and /cruisepaymentproc, which endswith needed two rules for.
    [InlineData("https://cruise.example-club.test/api/CruisePaymentProc", "cruises", "09-payment")]
    [InlineData("https://cruise.example-club.test/secure/cruisesummary.jsp", "cruises", "10-review")]
    [InlineData("https://cruise.example-club.test/api/CruiseConfirm", "cruises", "11-confirmation")]
    // Shared tail: the endpoints that are the same whatever the member is buying.
    [InlineData("https://book.example-club.test/Booking/PurchaseComplete", "shared", "08-confirmation")]
    [InlineData("https://book.example-club.test/Booking/CompletePurchase", "shared", "07-booking")]
    [InlineData("https://book.example-club.test/api/Determine3dsEligibility", "shared", "07-booking")]
    [InlineData("https://book.example-club.test/api/reservations/Purchase", "shared", "06-checkout")]
    [InlineData("https://book.example-club.test/api/TravelerProfile", "shared", "06-checkout")]
    [InlineData("https://book.example-club.test/api/AddRemoveInsurance", "shared", "06-checkout")]
    [InlineData("https://book.example-club.test/api/reservations/Cancel", "shared", "09-cancellation")]
    public void Seed_classifies_representative_traffic(string url, string expectedService, string expectedStep)
    {
        var match = new ProfileDrivenAdapter(Profile.Value).ResolveStep(new TelemetryRowContext(Url: url));

        Assert.True(match.IsResolved, $"'{url}' was not matched by any rule in the Arrivia seed.");
        Assert.Equal(expectedService, match.Service);
        Assert.Equal(expectedStep, match.Step);
        Assert.Equal(CompanyResolution.PatternMatch, match.ResolvedBy);
        Assert.Equal("booking", match.Flow);
    }

    /// <summary>
    /// The legacy query parks everything it does not recognise in a pseudo-vertical called
    /// "Shared / unclassified" at a pseudo-stage called "Unmapped operation", which makes a mapping
    /// gap indistinguishable from a real classification. The profile declares no fallback step at
    /// all, so the same traffic comes back honestly unresolved.
    /// </summary>
    [Theory]
    [InlineData("https://book.example-club.test/static/app.bundle.css")]
    [InlineData("https://book.example-club.test/api/health")]
    [InlineData("https://book.example-club.test/Member/Preferences")]
    public void Seed_leaves_traffic_it_does_not_describe_unresolved(string url)
    {
        var match = new ProfileDrivenAdapter(Profile.Value).ResolveStep(new TelemetryRowContext(Url: url));

        Assert.False(match.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, match.ResolvedBy);
        Assert.Null(match.Step);
        Assert.Null(match.Service);
    }

    /// <summary>A matcher kind this build cannot evaluate never matches, so it has to be impossible to ship one.</summary>
    [Fact]
    public void Seed_uses_only_matcher_kinds_this_build_can_evaluate()
    {
        var adapter = new ProfileDrivenAdapter(Profile.Value);

        foreach (var url in new[] { "https://book.example-club.test/Hotel/Search.json", "https://x.test/nothing" })
        {
            adapter.ResolveStep(new TelemetryRowContext(Url: url));
        }

        Assert.Empty(adapter.UnknownMatcherKinds);
    }

    // -------------------------------------------------------------------------------------
    // Who is affected
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Identity_resolves_from_the_primary_custom_dimension()
    {
        var identity = new ProfileDrivenAdapter(Profile.Value).ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://book.example-club.test/Hotel/Search.json",
            CustomDimensions: new Dictionary<string, string> { ["VacationClubId"] = "15639" }));

        Assert.True(identity.IsResolved);
        Assert.Equal("15639", identity.Value);
        Assert.Equal("club", identity.Kind);
        Assert.Equal("VacationClubId", identity.SourceKey);
    }

    /// <summary>
    /// An identity found by the fourth fallback is not worth the same as one the application
    /// stamped itself, so the caller has to be told WHICH source answered - not just that one did.
    /// </summary>
    [Fact]
    public void Identity_found_by_the_fourth_ranked_source_reports_that_source()
    {
        var fourth = Profile.Value.EntityIdentitySources.OrderBy(s => s.Order).ElementAt(3);
        Assert.Equal(EntityIdentitySourceKind.QueryParameter, fourth.Kind);
        Assert.Equal("cobrand", fourth.Key);

        var identity = new ProfileDrivenAdapter(Profile.Value).ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://book.example-club.test/Hotel/Search.json?cobrand=308"));

        Assert.True(identity.IsResolved);
        Assert.Equal("308", identity.Value);
        Assert.Equal("cobrand", identity.SourceKey);
    }

    /// <summary>The two query parameters at the tail of the coalesce chain are the last thing that still identifies a club.</summary>
    [Theory]
    [InlineData("https://book.example-club.test/Hotel/Search.json?cobrand=4471", "cobrand", "4471")]
    [InlineData("https://book.example-club.test/Hotel/Search.json?cobrandId=4471", "cobrandId", "4471")]
    public void Identity_resolves_from_a_query_parameter(string url, string expectedSource, string expectedValue)
    {
        var identity = new ProfileDrivenAdapter(Profile.Value).ResolveAffectedEntity(new TelemetryRowContext(Url: url));

        Assert.True(identity.IsResolved);
        Assert.Equal(expectedValue, identity.Value);
        Assert.Equal(expectedSource, identity.SourceKey);
    }

    /// <summary>
    /// The host is the last source, and it reports itself as a HOST - not as a club. That
    /// distinction is the whole reason the fallback is allowed to exist: ClubIdentityResolver maps
    /// "host:..." to a club through brand.ClubInventory.Site and labels how it got there, so no
    /// consumer of EntityIdentity.Kind is ever told a hostname is a club id.
    ///
    /// Measured before this source was added: the stamped club dimensions cover under 5% of the
    /// estate, so a chain without it left ~95% of rows unidentified and the Inspector with no
    /// entity to scope its baseline tools to.
    /// </summary>
    [Fact]
    public void Identity_falls_back_to_the_host_when_the_row_carries_no_club_id()
    {
        var identity = new ProfileDrivenAdapter(Profile.Value).ResolveAffectedEntity(new TelemetryRowContext(
            Url: "https://book.example-club.test/Hotel/Search.json?sort=price",
            CustomDimensions: new Dictionary<string, string> { ["CorrelationId"] = "3f1a" }));

        Assert.True(identity.IsResolved);
        Assert.Equal("book.example-club.test", identity.Value);
        Assert.Equal(EntityIdentitySourceKind.Host, identity.Kind);
    }

    /// <summary>A row with neither a club id nor a url has nothing left to identify it, and is reported as unidentified rather than guessed.</summary>
    [Fact]
    public void Identity_is_unresolved_when_the_row_carries_none_of_the_sources()
    {
        var identity = new ProfileDrivenAdapter(Profile.Value).ResolveAffectedEntity(new TelemetryRowContext(
            CustomDimensions: new Dictionary<string, string> { ["CorrelationId"] = "3f1a" }));

        Assert.False(identity.IsResolved);
        Assert.Equal(CompanyResolution.Unresolved, identity.ResolvedBy);
        Assert.Null(identity.Value);
        Assert.Null(identity.SourceKey);
    }

    // -------------------------------------------------------------------------------------
    // Reading the seed
    // -------------------------------------------------------------------------------------

    private static string Describe(IEnumerable<CompanyProfileFinding> findings) =>
        string.Join(Environment.NewLine, findings.Select(f => $"  [{f.Severity}] {f.Code} at {f.Path}: {f.Message}"));

    /// <summary>
    /// Lifts the JSON out of the T-SQL literal, undoing the <c>''</c> escaping. Deliberately not a
    /// regex: the document contains braces, quotes and brackets, and a pattern that got the
    /// escaping subtly wrong would hand the test a corrupted document and blame the seed for it.
    /// </summary>
    private static string ExtractSeedJson(string sql, string profileKey)
    {
        var anchor = $"VALUES (N'{profileKey}', N'";
        var start = sql.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the '{profileKey}' seed in CreateMonitoringCompanyProfile.sql (looked for \"{anchor}\"). " +
                "If the INSERT was reformatted, update this extractor - the test must read the literal that is " +
                "actually applied, never a copy.");
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
            $"The '{profileKey}' seed literal is never closed. The T-SQL string quoting in " +
            "CreateMonitoringCompanyProfile.sql is broken and the script will not apply.");
    }

    private static string ReadSeedSql()
    {
        var path = Path.Combine(
            FindPortalDirectory(),
            "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringCompanyProfile.sql");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The company profile seed script '{path}' is missing. This test asserts on the document that " +
                "reaches the database, so it fails rather than silently testing nothing.");
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
            "Could not locate the Portal source tree (looked for 'JourneyMonitor.slnx' walking up from " +
            $"'{AppContext.BaseDirectory}' and from '{ThisFilePath()}').");
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

    // -------------------------------------------------------------------------------------
    // The null walk
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Recurses the deserialised document and records every member that came back null while being
    /// declared non-nullable. Driven by <see cref="NullabilityInfoContext"/> rather than a hand
    /// written list of properties, so a member added to CompanyModel.cs tomorrow is covered the
    /// same day instead of being quietly exempt.
    /// </summary>
    private static void AssertNoNulls(object instance, string path, List<string> problems, NullabilityInfoContext nullability)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;

            var value = property.GetValue(instance);
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);

            if (value is null)
            {
                if (nullability.Create(property).ReadState == NullabilityState.NotNull)
                {
                    problems.Add($"{path}.{name} is null but is declared non-nullable");
                }

                continue;
            }

            if (value is string or ValueType) continue;

            if (value is IEnumerable items)
            {
                var index = 0;
                foreach (var item in items)
                {
                    if (item is null)
                    {
                        problems.Add($"{path}.{name}[{index}] is null");
                    }
                    else if (IsProfileType(item.GetType()))
                    {
                        AssertNoNulls(item, $"{path}.{name}[{index}]", problems, nullability);
                    }

                    index++;
                }

                continue;
            }

            if (IsProfileType(value.GetType()))
            {
                AssertNoNulls(value, $"{path}.{name}", problems, nullability);
            }
        }
    }

    private static bool IsProfileType(Type type) =>
        type.Namespace == typeof(CompanyProfile).Namespace && !type.IsPrimitive && type != typeof(string);
}
