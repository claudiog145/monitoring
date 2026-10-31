using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// Ownership answers "who fixes this", which is a different question from "who is affected". These
/// tests pin that the two never merge, and that an unknown owner is admitted rather than invented.
/// </summary>
public class ServiceOwnerResolverTests
{
    [Fact]
    public async Task AnExactStageMatchWins()
    {
        var resolver = Build(
            new ServiceOwner("hotels", ServiceOwner.AnyStage, "Hotel Platform", null, null, null, null),
            new ServiceOwner("hotels", "04 Hotel Details", "Hotel Front End", "Ana", "Beto", "teams://hotel-fe", null));

        var owner = await resolver.ResolveAsync("Hotels", "04 Hotel Details");

        Assert.True(owner.IsResolved);
        Assert.Equal(ServiceOwnerResolution.ExactStage, owner.ResolvedBy);
        Assert.Equal("Hotel Front End", owner.OwnerTeam);
        Assert.Contains("primary Ana", owner.Describe());
    }

    [Fact]
    public async Task AVerticalWideRowIsUsedButReportedAsAFallback()
    {
        // Both are "an owner", but only the exact match means somebody accepted THIS service, and
        // the case file has to be able to say which one it was.
        var resolver = Build(new ServiceOwner("hotels", ServiceOwner.AnyStage, "Hotel Platform", null, null, null, null));

        var owner = await resolver.ResolveAsync("Hotels", "04 Hotel Details");

        Assert.True(owner.IsResolved);
        Assert.Equal(ServiceOwnerResolution.VerticalFallback, owner.ResolvedBy);
    }

    [Fact]
    public async Task AnUnknownServiceIsUnresolvedRatherThanGuessed()
    {
        // The pilot exit criterion requires an owner on every Red Alert. An owner invented from a
        // naming convention would satisfy it on paper and destroy trust in the routing the first
        // time an alert reached the wrong team.
        var owner = await Build().ResolveAsync("Cruises", "02 Cruise Listing");

        Assert.False(owner.IsResolved);
        Assert.Equal(ServiceOwnerResolution.Unresolved, owner.ResolvedBy);
        Assert.Null(owner.OwnerTeam);
        Assert.Equal("no owner is recorded for this service", owner.Describe());
    }

    [Theory]
    [InlineData("Hotels")]
    [InlineData("hotels")]
    [InlineData("Hotel")]
    public async Task TheVerticalComparisonToleratesTheBoardsCasingAndPluralisation(string vertical)
    {
        var resolver = Build(new ServiceOwner("hotels", "04 Hotel Details", "Hotel Front End", null, null, null, null));

        Assert.True((await resolver.ResolveAsync(vertical, "04 Hotel Details")).IsResolved);
    }

    [Fact]
    public async Task AnUnreadableMapIsUnavailableAndNotMistakenForNobodyOwningIt()
    {
        // One is our outage, the other is their gap. Collapsing them makes a database blip look
        // like an organisational problem.
        var resolver = new ServiceOwnerResolver(
            new ThrowingServiceOwnerStore(),
            Options.Create(new JourneyStoreOptions { ConnectionString = "Server=nowhere;" }),
            NullLogger<ServiceOwnerResolver>.Instance);

        var owner = await resolver.ResolveAsync("Hotels", "04 Hotel Details");

        Assert.False(owner.IsResolved);
        Assert.Equal(ServiceOwnerResolution.Unavailable, owner.ResolvedBy);
        Assert.NotEqual(ServiceOwnerResolution.Unresolved, owner.ResolvedBy);
    }

    [Fact]
    public async Task InvalidateMakesANewlyAssignedOwnerTakeEffectWithoutWaitingOutTheCache()
    {
        var store = new InMemoryServiceOwnerStore();
        var resolver = new ServiceOwnerResolver(
            store, Options.Create(new JourneyStoreOptions { ConnectionString = "x" }), NullLogger<ServiceOwnerResolver>.Instance);

        Assert.False((await resolver.ResolveAsync("Hotels", "04 Hotel Details")).IsResolved);

        await store.UpsertAsync(new ServiceOwner("hotels", "04 Hotel Details", "Hotel Front End", null, null, null, null), "claudio");
        resolver.Invalidate();

        Assert.True((await resolver.ResolveAsync("Hotels", "04 Hotel Details")).IsResolved);
    }

    // ---------------------------------------------------------------------------------------------
    // Red Alert: the owner is a routing condition, separate from the club
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ACaseWithoutARecordedOwnerCannotBeARedAlert()
    {
        var evaluation = Evaluate(Qualifying(ownerTeam: null));

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
        Assert.Contains("nobody accountable to page", evaluation.Reason);
    }

    [Fact]
    public void AResolvedClubIsNotEnoughOnItsOwn()
    {
        // The club says who is affected; it never says who fixes it. Before this condition existed
        // an attributable club was treated as "routable", which is how a code defect ends up with a
        // brand manager.
        var forensicCase = Qualifying(ownerTeam: null);

        Assert.False(string.IsNullOrEmpty(forensicCase.ClubId));
        Assert.Equal(AlertLevel.Amber, Evaluate(forensicCase).Level);
    }

    [Fact]
    public void WithBothAClubAndAnOwnerTheCaseReachesRedAlertAndNamesTheOwner()
    {
        var evaluation = Evaluate(Qualifying(ownerTeam: "Hotel Front End"));

        Assert.Equal(AlertLevel.RedAlert, evaluation.Level);
        Assert.Contains("owned by Hotel Front End", evaluation.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static IServiceOwnerResolver Build(params ServiceOwner[] owners) =>
        InspectorFixtures.OwnerResolver(owners);

    private static RedAlertEvaluation Evaluate(ForensicCase forensicCase) =>
        new RedAlertPolicy(Options.Create(new RedAlertOptions { RequireBaselineConfirmation = false }))
            .Evaluate(forensicCase);

    private static ForensicCase Qualifying(string? ownerTeam) => new()
    {
        InvestigationPackageId = Guid.NewGuid(),
        CaseId = "SOR | Hotels | club:113 | 04 Hotel Details | 2026-10-09T15:00:00Z",
        EntityKey = "club:113",
        BrandName = "Aloha Hawaii Tours",
        ClubId = "113",
        Vertical = "Hotels",
        Stage = "04 Hotel Details",
        Status = ForensicCaseStatus.Escalated,
        Decision = InspectorDecision.Escalate,
        Confidence = 0.8,
        CitationsTotal = 4,
        CitationsVerified = 4,
        OwnerTeam = ownerTeam,
    };
}

internal sealed class ThrowingServiceOwnerStore : IServiceOwnerStore
{
    public Task<IReadOnlyList<ServiceOwner>> GetAllAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("the ownership map is unreachable");

    public Task<ServiceOwner> UpsertAsync(ServiceOwner owner, string changedBy, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string vertical, string stage, string changedBy, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ServiceOwnerAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
