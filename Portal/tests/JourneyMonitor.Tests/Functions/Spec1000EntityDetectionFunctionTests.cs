using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.SD4;

public sealed class Spec1000EntityDetectionFunctionTests
{
    private readonly List<string> _order = [];

    private ApplicationHealthMonitorFunction CreateFunction(bool enabled, bool pillarThrows = false, bool entityThrows = false) => new(
        new OrderedPillarMonitor(_order, pillarThrows),
        Options.Create(new HealthMonitoringOptions { Enabled = enabled, Pillars = ["Application"] }),
        NullLogger<ApplicationHealthMonitorFunction>.Instance,
        new OrderedEntityMonitor(_order, entityThrows));

    [Fact]
    public async Task ApplicationTimer_RunsEntityDetectionAfterThePillarMonitor()
    {
        await CreateFunction(enabled: true).RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Equal(["pillar:Application", "entity"], _order);
    }

    [Fact]
    public async Task ApplicationTimer_EntityFailureDoesNotBreakThePillarPart()
    {
        await CreateFunction(enabled: true, entityThrows: true).RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Equal(["pillar:Application", "entity"], _order);
    }

    [Fact]
    public async Task ApplicationTimer_PillarFailureStillRunsEntityDetectionAndSurfaces()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFunction(enabled: true, pillarThrows: true).RunAsync(new TimerInfo(), CancellationToken.None));

        Assert.Equal(["pillar:Application", "entity"], _order);
    }

    [Fact]
    public async Task ApplicationTimer_DisabledRunsNothing()
    {
        await CreateFunction(enabled: false).RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Empty(_order);
    }

    private sealed class OrderedPillarMonitor(List<string> order, bool fail) : IPillarHealthMonitor
    {
        public Task<PillarMonitorRunResult> RunAsync(KpiPillar pillar, CancellationToken ct = default)
        {
            order.Add("pillar:" + pillar);
            if (fail) throw new InvalidOperationException("pillar store down");
            return Task.FromResult(new PillarMonitorRunResult(1, 0, 0, 0));
        }
    }

    private sealed class OrderedEntityMonitor(List<string> order, bool fail) : IEntityDetectionMonitor
    {
        public Task<EntityDetectionRunResult> RunAsync(CancellationToken ct = default)
        {
            order.Add("entity");
            if (fail) throw new InvalidOperationException("entity store down");
            return Task.FromResult(new EntityDetectionRunResult(1, 3, 1, 1));
        }
    }
}
