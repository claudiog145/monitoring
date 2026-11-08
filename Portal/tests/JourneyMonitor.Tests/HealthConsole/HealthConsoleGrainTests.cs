using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.HealthConsole;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A2;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Tests.Spec1000.D3;

/// <summary>SD6: the kpis command shows each definition's grain (contract v4 §1 wire names).</summary>
public sealed class HealthConsoleGrainTests
{
    [Fact]
    public async Task Kpis_ShowsGrainColumn()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        await definitions.UpsertAsync(A2Kpi.Definition(0), "seed");
        await definitions.UpsertAsync(A2Kpi.Definition(0) with { Grain = KpiGrain.Entity }, "seed");
        var services = new ServiceCollection()
            .AddSingleton<IKpiDefinitionStore>(definitions)
            .AddSingleton<IDaemonActivityLog>(new InMemoryDaemonActivityLog())
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();

        using var writer = new StringWriter();
        var code = await HealthConsoleApp.RunAsync(["kpis"], writer, services, (_, _) => Task.CompletedTask, CancellationToken.None);
        var lines = writer.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        Assert.Equal(0, code);
        Assert.Equal(3, lines.Count);
        var grainAt = lines[0].IndexOf("Grain ", StringComparison.Ordinal);
        Assert.True(grainAt > 0);
        Assert.Equal(["domain", "entity"], lines.Skip(1).Select(l => l.Substring(grainAt, 6).Trim()).Order().ToArray());
    }
}
