using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// Profiles for the scout tests. Two of them describe the SAME traffic with different words on
/// purpose: that pair is the only way to prove the pipeline reads its vocabulary from the profile
/// rather than from the query text, which is the whole point of the change.
/// </summary>
public static class ScoutCompanyProfiles
{
    /// <summary>Both services of <see cref="TwoServicesOnOneHost"/> answer on this one host.</summary>
    public const string SharedHost = "example.com";

    private static readonly CompanyLabels Labels = new("Service", "Flow", "Step", "Entity", "Owner");

    private static readonly CompanyGlossary Glossary = new("service", "flow", "step", "entity", "owner");

    /// <summary>
    /// A company serving two product areas from one hostname - the case a host-first design cannot
    /// express, and the reason the matched STEP is what names the service.
    /// </summary>
    public static CompanyProfile TwoServicesOnOneHost(string profileKey = "two-services") => new(
        ProfileKey: profileKey,
        DisplayName: "Two services, one host",
        Labels: Labels,
        Services:
        [
            new ServiceDefinition("retail", "Retail", [], [SharedHost],
            [
                new FlowDefinition("purchase", "Purchase",
                [
                    new StepDefinition("checkout", "Checkout", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                ]),
            ]),
            new ServiceDefinition("support", "Support", [], [SharedHost],
            [
                new FlowDefinition("tickets", "Tickets",
                [
                    new StepDefinition("ticket", "Ticket", 2, [new StepMatcher(StepMatcherKind.UrlContains, "/ticket")]),
                ]),
            ]),
        ],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.Host, "", ClubEntityKind.Host),
        ],
        Glossary: Glossary);

    /// <summary>The same two urls, every single name different. Identical rows must come out speaking this language instead.</summary>
    public static CompanyProfile RenamedVocabulary(string profileKey = "renamed") => new(
        ProfileKey: profileKey,
        DisplayName: "Renamed vocabulary",
        Labels: Labels,
        Services:
        [
            new ServiceDefinition("commerce", "Commerce", [], [SharedHost],
            [
                new FlowDefinition("basket-flow", "Basket flow",
                [
                    new StepDefinition("basket", "Basket", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                ]),
            ]),
            new ServiceDefinition("helpdesk", "Helpdesk", [], [SharedHost],
            [
                new FlowDefinition("cases", "Cases",
                [
                    new StepDefinition("case", "Case", 2, [new StepMatcher(StepMatcherKind.UrlContains, "/ticket")]),
                ]),
            ]),
        ],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "AccountId", "account"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.Host, "", ClubEntityKind.Host),
        ],
        Glossary: Glossary);

    /// <summary>
    /// Identity only ever comes from a dimension the application has to stamp. A row without it
    /// must come back unresolved - there is deliberately no host fallback to paper over the gap.
    /// </summary>
    public static CompanyProfile IdentityOnlyFromAStampedDimension(string profileKey = "stamped-only") => new(
        ProfileKey: profileKey,
        DisplayName: "Stamped identity only",
        Labels: Labels,
        Services:
        [
            new ServiceDefinition("retail", "Retail", [], [],
            [
                new FlowDefinition("purchase", "Purchase",
                [
                    new StepDefinition("checkout", "Checkout", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                ]),
            ]),
        ],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant"),
        ],
        Glossary: Glossary);
}

/// <summary>
/// Hand-written, like every other fake in this suite. Also records what it was asked for, because
/// "the scout ran under the configured profile" is an assertion, not an assumption.
/// </summary>
public sealed class FakeCompanyAdapterProvider : ICompanyAdapterProvider
{
    private readonly ICompanyAdapter? _adapter;
    private readonly Exception? _failure;

    public FakeCompanyAdapterProvider(CompanyProfile profile) => _adapter = new ProfileDrivenAdapter(profile);

    public FakeCompanyAdapterProvider(Exception failure) => _failure = failure;

    public int Calls { get; private set; }

    public string? LastRequestedProfileKey { get; private set; }

    public Task<ICompanyAdapter> GetAsync(string? profileKey = null, CancellationToken ct = default)
    {
        Calls++;
        LastRequestedProfileKey = profileKey;
        if (_failure is not null) throw _failure;
        return Task.FromResult(_adapter!);
    }

    public void Invalidate() { }
}

public class ScoutRowMapperTests
{
    [Fact]
    public void RebuildsAUrlFromTheBoardsOwnHostAndItsSlowestPath()
    {
        // A board row is an aggregate, so there is no url column on it. Without this, every
        // url-shaped matcher would miss and a mapped company would look like it had no traffic.
        var context = ScoutRowMapper.ToRowContext(
            "{}",
            """{"Paths":["/catalog"],"SlowPaths":["/checkout"]}""",
            "host:example.com");

        Assert.Equal("https://example.com/checkout", context.Url);
        Assert.Equal("example.com", context.Host);
        Assert.Equal("/checkout", context.Path);
    }

    [Fact]
    public void PrefersAUrlTheBoardActuallyProjectedOverTheReconstruction()
    {
        var context = ScoutRowMapper.ToRowContext(
            """{"Url":"https://real.example.com/checkout?cobrand=7"}""",
            """{"SlowPaths":["/something-else"]}""",
            "host:example.com");

        Assert.Equal("https://real.example.com/checkout?cobrand=7", context.Url);
        // The query string only survives when the board projected the url, which is what makes a
        // query-parameter identity source answerable at all.
        Assert.Equal("7", context.QueryParameter("cobrand"));
    }

    [Fact]
    public void OffersEveryProjectedColumnToTheProfileUnderTheNameTheBoardUsed()
    {
        // This is what lets a profile-composed board hand its identity dimensions through without
        // the mapper knowing a single company-specific column name.
        var context = ScoutRowMapper.ToRowContext("""{"TenantId":"t-7","Requests":174}""", "{}");

        Assert.Equal("t-7", context.CustomDimension("TenantId"));
        Assert.Equal("174", context.CustomDimension("Requests"));
    }

    [Fact]
    public void ReadsAnExplicitDimensionBagWhetherItArrivesAsAnObjectOrAsAString()
    {
        // Kusto emits a dynamic bag as an object, and the telemetry source may hand it back as the
        // JSON string it was. Reading only one shape would drop the identity on the other.
        var asObject = ScoutRowMapper.ToRowContext("""{"CustomDimensions":{"TenantId":"t-7"}}""", "{}");
        var asString = ScoutRowMapper.ToRowContext("""{"CustomDimensions":"{\"TenantId\":\"t-7\"}"}""", "{}");

        Assert.Equal("t-7", asObject.CustomDimension("TenantId"));
        Assert.Equal("t-7", asString.CustomDimension("TenantId"));
    }

    [Fact]
    public void NeverTurnsAnIdentifierEntityKeyIntoTelemetryItCouldNotHaveRead()
    {
        // "club:113" is an identifier the board resolved by a rule this mapper cannot see. Feeding
        // it back would produce an identity whose SourceKey had to be guessed, which is worse than
        // an honest unresolved.
        var context = ScoutRowMapper.ToRowContext("{}", """{"SlowPaths":["/checkout"]}""", "club:113");

        Assert.Null(context.Host);
        Assert.Equal("/checkout", context.Url);
        Assert.DoesNotContain("113", context.CustomDimensions?.Values ?? []);
    }

    [Fact]
    public void SurvivesAnUnreadablePayloadInsteadOfTakingTheCycleDown()
    {
        var context = ScoutRowMapper.ToRowContext("not json at all", "{ broken");

        Assert.Null(context.Url);
        Assert.Null(context.Operation);
    }

    [Fact]
    public void ReportsWhichIdentitySourceActuallyAnsweredAndHowStrongItIs()
    {
        var adapter = new ProfileDrivenAdapter(ScoutCompanyProfiles.TwoServicesOnOneHost());

        var stamped = ScoutRowMapper.Classify(adapter, ScoutRowMapper.ToRowContext(
            """{"TenantId":"t-7"}""", """{"SlowPaths":["/checkout"]}""", "host:example.com"));
        var fellBackToTheHost = ScoutRowMapper.Classify(adapter, ScoutRowMapper.ToRowContext(
            "{}", """{"SlowPaths":["/checkout"]}""", "host:example.com"));

        Assert.Equal("tenant:t-7", stamped.EntityKey);
        Assert.Equal("TenantId", stamped.EntitySourceKey);

        // Same row, weaker answer - and the caller can tell without re-deriving it.
        Assert.Equal("host:example.com", fellBackToTheHost.EntityKey);
        Assert.Equal(EntityIdentitySourceKind.Host, fellBackToTheHost.EntitySourceKey);
    }

    [Fact]
    public void LeavesARowTheProfileDoesNotDescribeExplicitlyUnresolved()
    {
        var adapter = new ProfileDrivenAdapter(ScoutCompanyProfiles.IdentityOnlyFromAStampedDimension());

        var classification = ScoutRowMapper.Classify(adapter, ScoutRowMapper.ToRowContext(
            "{}", """{"SlowPaths":["/nothing-maps-here"]}""", "host:example.com"));

        Assert.False(classification.StepIsResolved);
        Assert.False(classification.EntityIsResolved);
        Assert.Equal(CompanyResolution.Unresolved, classification.StepResolvedBy);
        Assert.Equal(CompanyResolution.Unresolved, classification.EntityResolvedBy);
        // Visible, not blank: a package whose stage renders as "" reads as a UI bug, not a gap.
        Assert.Equal(ScoutRowClassification.UnresolvedStepLabel, classification.StepLabel);
        Assert.Equal(ScoutRowClassification.UnresolvedServiceLabel, classification.ServiceLabel);
        Assert.Equal(ScoutRowClassification.UnresolvedEntityKey, classification.EntityKey);
    }

    [Fact]
    public void TheStepThatMatchedIsWhatNamesTheServiceWhenTwoShareAHost()
    {
        var adapter = new ProfileDrivenAdapter(ScoutCompanyProfiles.TwoServicesOnOneHost());

        var retail = ScoutRowMapper.Classify(adapter, ScoutRowMapper.ToRowContext(
            "{}", """{"SlowPaths":["/checkout"]}""", "host:example.com"));
        var support = ScoutRowMapper.Classify(adapter, ScoutRowMapper.ToRowContext(
            "{}", """{"SlowPaths":["/ticket"]}""", "host:example.com"));

        Assert.Equal("Retail", retail.ServiceLabel);
        Assert.Equal("Checkout", retail.StepLabel);
        Assert.Equal("Support", support.ServiceLabel);
        Assert.Equal("Ticket", support.StepLabel);
    }

    [Fact]
    public void ReadsABoardRowStraightOffTheDomainType()
    {
        var row = new ScoutBoardRow(
            ScoutBoard.Latency, "case-a", "group-a", "ignored", "host:example.com", "ignored", "SOR",
            "Investigate now", 3, "reason", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            """{"SlowPaths":["/checkout"]}""", """{"TenantId":"t-7"}""");

        var classification = ScoutRowMapper.Classify(new ProfileDrivenAdapter(ScoutCompanyProfiles.TwoServicesOnOneHost()), row);

        Assert.Equal("Checkout", classification.StepLabel);
        Assert.Equal("tenant:t-7", classification.EntityKey);
    }

    [Fact]
    public void ClassificationIsSerialisedForTheWebSoTheUiCanReadItBack()
    {
        // A real bug in this repo made the whole UI read nulls because a persisted payload went out
        // in PascalCase. Anything written into a package has to round-trip by camelCase name.
        var classification = ScoutRowMapper.Classify(
            new ProfileDrivenAdapter(ScoutCompanyProfiles.TwoServicesOnOneHost()),
            ScoutRowMapper.ToRowContext("""{"TenantId":"t-7"}""", """{"SlowPaths":["/checkout"]}""", "host:example.com"));

        var json = JsonSerializer.Serialize(classification, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("TenantId", document.RootElement.GetProperty("entitySourceKey").GetString());
        Assert.Equal("two-services", document.RootElement.GetProperty("profileKey").GetString());
    }
}
