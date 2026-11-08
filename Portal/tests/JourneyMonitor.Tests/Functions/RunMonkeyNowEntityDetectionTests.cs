using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.SD7;

/// <summary>Run Now for application-health-monitor behaves like the timer: pillar monitor, then entity detection, failure-isolated.</summary>
public sealed class RunMonkeyNowEntityDetectionTests
{
    private readonly List<string> _order = [];

    private RunMonkeyNowFunction CreateFunction(bool pillarThrows = false, bool entityThrows = false, bool withEntity = true) => new(
        latencyRunner: null!,
        sloRunner: null!,
        doctorRunner: null!,
        scoutRunner: null!,
        inspectorRunner: null!,
        ingestionRunner: null!,
        new OrderedPillarMonitor(_order, pillarThrows),
        Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret }),
        NullLogger<RunMonkeyNowFunction>.Instance,
        withEntity ? new OrderedEntityMonitor(_order, entityThrows) : null);

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> RunAsync(RunMonkeyNowFunction function, string id) =>
        function.RunAsync(A4Http.Request($"http://localhost/api/monkeys/{id}/run", internalSecret: A4Http.InternalSecret), id, CancellationToken.None);

    [Fact]
    public async Task RunNow_ApplicationHealth_RunsEntityDetectionAfterThePillarMonitor()
    {
        var response = await RunAsync(CreateFunction(), MonkeyKey.ApplicationHealthMonitor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["pillar:Application", "entity"], _order);
        var json = response.ReadJson();
        Assert.Equal(MonkeyKey.ApplicationHealthMonitor, json["monkeyId"]!.GetValue<string>());
        Assert.NotNull(json["result"]);
        Assert.Equal(3, json["entityDetection"]!["seriesEvaluated"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunNow_ApplicationHealth_EntityFailureKeepsThePillarResult()
    {
        var response = await RunAsync(CreateFunction(entityThrows: true), MonkeyKey.ApplicationHealthMonitor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["pillar:Application", "entity"], _order);
        var json = response.ReadJson();
        Assert.NotNull(json["result"]);
        Assert.False(string.IsNullOrEmpty(json["entityDetectionError"]?.GetValue<string>()));
    }

    [Fact]
    public async Task RunNow_ApplicationHealth_PillarFailureStillRunsEntityDetectionAndReturns500()
    {
        var response = await RunAsync(CreateFunction(pillarThrows: true), MonkeyKey.ApplicationHealthMonitor);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(["pillar:Application", "entity"], _order);
    }

    [Fact]
    public async Task RunNow_OtherPillars_DoNotRunEntityDetection()
    {
        Assert.Equal(HttpStatusCode.OK, (await RunAsync(CreateFunction(), MonkeyKey.BusinessHealthMonitor)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RunAsync(CreateFunction(), MonkeyKey.InfrastructureHealthMonitor)).StatusCode);

        Assert.Equal(["pillar:Business", "pillar:Infrastructure"], _order);
    }

    [Fact]
    public async Task RunNow_ApplicationHealth_WithoutEntityMonitorRunsOnlyThePillar()
    {
        var response = await RunAsync(CreateFunction(withEntity: false), MonkeyKey.ApplicationHealthMonitor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["pillar:Application"], _order);
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
