using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.HealthConsole;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A2;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Tests.Spec1000.D3;

/// <summary>Contract v3: kpis, status and watch accept --profile; without it every profile is shown with a Profile column.</summary>
public sealed class HealthConsoleProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 5, 0, TimeSpan.Zero);

    private static async Task<IServiceProvider> ServicesAsync(InMemoryDaemonActivityLog? log = null)
    {
        var definitions = new InMemoryKpiDefinitionStore();
        await definitions.UpsertAsync(A2Kpi.Definition(0), "seed");
        await definitions.UpsertAsync(A2Kpi.Definition(0, domain: "cars") with { ProfileKey = "acme", Enabled = false }, "seed");
        return new ServiceCollection()
            .AddSingleton<IKpiDefinitionStore>(definitions)
            .AddSingleton<IDaemonActivityLog>(log ?? new InMemoryDaemonActivityLog())
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
    }

    private static async Task<(int ExitCode, List<string> Lines)> Run(IServiceProvider services, params string[] args)
    {
        using var writer = new StringWriter();
        var code = await HealthConsoleApp.RunAsync(args, writer, services, (_, _) => Task.CompletedTask, CancellationToken.None);
        return (code, writer.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList());
    }

    [Fact]
    public async Task Kpis_ListsEveryProfileOrOnlyTheRequestedOne()
    {
        var services = await ServicesAsync();

        var (code, lines) = await Run(services, "kpis");
        Assert.Equal(0, code);
        Assert.StartsWith("Profile ", lines[0]);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("acme ", lines[1]);
        Assert.StartsWith(KpiProfiles.Default + " ", lines[2]);

        var (_, acme) = await Run(services, "kpis", "--profile", "acme");
        Assert.Equal(2, acme.Count);
        Assert.Contains("Latency/cars", acme[1]);

        var (_, none) = await Run(services, "kpis", "--profile", "ice");
        Assert.Equal("No KPI definitions for profile 'ice'.", none[^1]);
    }

    [Fact]
    public async Task Status_ShowsKpisPerProfileAndFilters()
    {
        var services = await ServicesAsync();

        var (code, lines) = await Run(services, "status");
        Assert.Equal(0, code);
        Assert.Contains(lines, l => l.StartsWith("Profile ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("acme ", StringComparison.Ordinal) && l.EndsWith("0/1 enabled", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith(KpiProfiles.Default + " ", StringComparison.Ordinal) && l.EndsWith("1/1 enabled", StringComparison.Ordinal));

        var (_, filtered) = await Run(services, "status", "--profile", "acme");
        Assert.DoesNotContain(filtered, l => l.StartsWith(KpiProfiles.Default + " ", StringComparison.Ordinal));
        Assert.Contains(filtered, l => l.StartsWith("acme ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_ProfileColumnAndFilter()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            new DaemonActivityEntry(0, DaemonKeys.KpiIngestion, Guid.Empty, Now, DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, "run-start", null),
            new DaemonActivityEntry(0, DaemonKeys.KpiIngestion, Guid.Empty, Now, DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi, "line-arrivia", """{"kpiDefinitionId":1,"profileKey":"arrivia"}"""),
            new DaemonActivityEntry(0, DaemonKeys.KpiIngestion, Guid.Empty, Now, DaemonActivityLevel.Warning, DaemonActivityKinds.IngestionKpi, "line-acme", """{"kpiDefinitionId":2,"profileKey":"acme"}"""),
        ]);
        var services = await ServicesAsync(log);

        var (code, all) = await Run(services, "watch", "--max-polls", "1");
        Assert.Equal(0, code);
        Assert.Contains(all, l => l.Contains($" {HealthConsoleApp.NoProfile,-12} ", StringComparison.Ordinal) && l.EndsWith(" run-start", StringComparison.Ordinal));
        Assert.Contains(all, l => l.Contains($" {"arrivia",-12} ", StringComparison.Ordinal) && l.EndsWith(" line-arrivia", StringComparison.Ordinal));
        Assert.Contains(all, l => l.Contains($" {"acme",-12} ", StringComparison.Ordinal) && l.EndsWith(" line-acme", StringComparison.Ordinal));

        var (_, acme) = await Run(services, "watch", "--max-polls", "1", "--profile", "acme");
        Assert.Contains(acme, l => l.EndsWith(" run-start", StringComparison.Ordinal));
        Assert.Contains(acme, l => l.EndsWith(" line-acme", StringComparison.Ordinal));
        Assert.DoesNotContain(acme, l => l.EndsWith(" line-arrivia", StringComparison.Ordinal));
    }
}
