using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Tests.Spec1000.A4;

namespace JourneyMonitor.Tests.UseCases;

public sealed class GetPillarHealthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly A4FakeKpiDefinitionStore _definitions = new();
    private readonly A4FakeKpiBucketStore _buckets = new();
    private readonly A4FakeKpiStateStore _states = new();
    private readonly A4FakeKpiAnomalyStore _anomalies = new();

    private GetPillarHealthService Service() => new(_definitions, _buckets, _states, _anomalies, new A4FakeTimeProvider(Now));

    [Fact]
    public async Task PillarHealth_OfflineWhenNoEnabledKpis()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Business, enabled: false));

        var pillars = await Service().GetAsync(null);

        Assert.Equal([KpiPillar.Business, KpiPillar.Application, KpiPillar.Infrastructure], pillars.Select(p => p.Pillar).ToArray());
        Assert.Equal(["Business Health", "Application Health", "Infrastructure Health"], pillars.Select(p => p.DisplayName).ToArray());
        Assert.All(pillars, p => Assert.Equal("offline", p.Status));
        var business = Assert.Single(pillars[0].Kpis);
        Assert.False(business.Enabled);
        Assert.Empty(pillars[1].Kpis);
        Assert.Empty(pillars[2].Kpis);
    }

    [Fact]
    public async Task PillarHealth_AnomalousWhenCounterOrCooldownActive()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application));
        _definitions.Definitions.Add(A4Http.Definition(2, KpiPillar.Business));
        _definitions.Definitions.Add(A4Http.Definition(3, KpiPillar.Infrastructure));
        _states.States[1] = new KpiDetectionState(1, ConsecutiveCount: 1, Now.AddHours(-1), CooldownUntil: null, Now);
        _states.States[2] = new KpiDetectionState(2, ConsecutiveCount: 0, Now.AddHours(-1), CooldownUntil: Now.AddMinutes(30), Now);
        _states.States[3] = new KpiDetectionState(3, ConsecutiveCount: 0, Now.AddHours(-1), CooldownUntil: Now.AddMinutes(-1), Now);

        var pillars = await Service().GetAsync(null);

        Assert.Equal("anomalous", pillars.Single(p => p.Pillar == KpiPillar.Application).Status);
        Assert.Equal("anomalous", pillars.Single(p => p.Pillar == KpiPillar.Business).Status);
        Assert.Equal("ok", pillars.Single(p => p.Pillar == KpiPillar.Infrastructure).Status);
    }

    [Fact]
    public async Task PillarHealth_IncludesLatestBucketWithin48HoursAndLastAnomaly()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application));
        _buckets.Buckets.Add(new KpiBucket(1, Now.AddHours(-50), 10, 10, 1, 2, 1, 0, null));
        _buckets.Buckets.Add(new KpiBucket(1, Now.AddHours(-3), 10, 10, 1, 2, 1, 0, null));
        _buckets.Buckets.Add(new KpiBucket(1, Now.AddHours(-2), 20, 10, 1, 3, 1, 0, null));
        _anomalies.Anomalies.Add(new KpiAnomaly(5, 1, Now.AddHours(-4), Now.AddHours(-5), 1, 1, 1, "old", null));
        _anomalies.Anomalies.Add(new KpiAnomaly(6, 1, Now.AddHours(-1), Now.AddHours(-2), 3, 1, 200, "new", null));
        _anomalies.Anomalies.Add(new KpiAnomaly(7, 99, Now, Now, 3, 1, 200, "other kpi", null));

        var pillars = await Service().GetAsync(null);

        var kpi = Assert.Single(pillars.Single(p => p.Pillar == KpiPillar.Application).Kpis);
        Assert.Equal(Now.AddHours(-2), kpi.LatestBucket!.BucketStart);
        Assert.Equal(6, kpi.LastAnomaly!.AnomalyId);
        Assert.Equal(KpiDetectionState.Initial(1), kpi.State);
        Assert.Equal("ok", pillars.Single(p => p.Pillar == KpiPillar.Application).Status);
    }

    [Fact]
    public async Task PillarHealth_IgnoresEntityGrainKpis()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application));
        _definitions.Definitions.Add(A4Http.Definition(2, KpiPillar.Application) with { Grain = KpiGrain.Entity });
        _states.States[2] = new KpiDetectionState(2, ConsecutiveCount: 1, Now.AddHours(-1), CooldownUntil: Now.AddMinutes(30), Now);
        _anomalies.Anomalies.Add(new KpiAnomaly(8, 2, Now.AddHours(-1), Now.AddHours(-2), 3, 1, 200, "entity", null));

        var pillars = await Service().GetAsync(null);

        var application = pillars.Single(p => p.Pillar == KpiPillar.Application);
        Assert.Equal(1, Assert.Single(application.Kpis).KpiDefinitionId);
        Assert.Null(application.Kpis[0].LastAnomaly);
        Assert.Equal("ok", application.Status);
    }

    [Fact]
    public async Task PillarHealth_OnlyEntityKpis_IsOffline()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application) with { Grain = KpiGrain.Entity });
        _definitions.Definitions.Add(A4Http.Definition(2, KpiPillar.Application, domain: "cars") with { Grain = KpiGrain.Entity });

        var pillars = await Service().GetAsync(null);

        var application = pillars.Single(p => p.Pillar == KpiPillar.Application);
        Assert.Empty(application.Kpis);
        Assert.Equal("offline", application.Status);
    }

    [Fact]
    public async Task PillarHealth_DomainFilterLimitsKpis()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application, domain: "hotels"));
        _definitions.Definitions.Add(A4Http.Definition(2, KpiPillar.Application, domain: "cars"));

        var pillars = await Service().GetAsync("cars");

        var kpi = Assert.Single(pillars.Single(p => p.Pillar == KpiPillar.Application).Kpis);
        Assert.Equal(2, kpi.KpiDefinitionId);
        Assert.Equal(3, pillars.Count);
    }
}
