using JourneyMonitor.Core.Domain;
using JourneyMonitor.HealthConsole;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Spec1000.A6;

public class HealthConsoleTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly (int Hour, double Normal, double Observed)[] Afternoon =
    [
        (12, 1800, 1854), (13, 1820, 2950), (14, 1880, 1974), (15, 1900, 3230),
        (16, 1910, 3534), (17, 1930, 3667), (18, 1900, 2660),
    ];

    private static async Task<(int ExitCode, string Output)> Run(IServiceProvider? services, params string[] args)
    {
        using var writer = new StringWriter();
        var code = await HealthConsoleApp.RunAsync(args, writer, services);
        return (code, writer.ToString());
    }

    private static KpiDefinition AfternoonDefinition() => new(
        0, KpiPillar.Application, "Latency", "hotels", "", "appinsights:latency",
        KpiDirection.Increase, KpiAggregation.P95,
        TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromDays(1),
        0, 30, DetectionMethodKind.BaselinePercent, "{\"lookbackDays\":7,\"thresholdPercent\":50}",
        2, 120, true, Day, "test");

    private static async Task<(A6SpiedStores Stores, int KpiId)> SeedAfternoonAsync()
    {
        var stores = new A6SpiedStores();
        var stored = await stores.Definitions.UpsertAsync(AfternoonDefinition(), "seed");
        var id = stored.KpiDefinitionId;
        var buckets = Afternoon
            .SelectMany(row => Enumerable.Range(1, 7).Select(k => Bucket(id, Day.AddHours(row.Hour).AddDays(-k), row.Normal)))
            .Concat(Afternoon.Select(row => Bucket(id, Day.AddHours(row.Hour), row.Observed)))
            .ToList();
        await stores.Buckets.UpsertAsync(buckets);
        return (stores, id);
    }

    private static KpiBucket Bucket(int id, DateTimeOffset start, double p95) => new(id, start, 5000, 0, null, p95, null, null, null);

    private static List<string> TableRows(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 5 && l[2] == ':' && l.Contains(" | ")).ToList();

    [Fact]
    public async Task Simulate_Afternoon_PrintsPresentationTable()
    {
        var (code, output) = await Run(null, "simulate", "--scenario", "afternoon");

        Assert.Equal(0, code);
        Assert.Contains("Hora | Observado | Referencia | Desvío | Veredicto | Outcome | Contador", output);
        string[] expected =
        [
            "12:00 | 1854 | 1800 | +3.0% | Normal | NoChange | 0",
            "13:00 | 2950 | 1820 | +62.1% | Anomalous | CounterIncremented | 1",
            "14:00 | 1974 | 1880 | +5.0% | Normal | CounterReset | 0",
            "15:00 | 3230 | 1900 | +70.0% | Anomalous | CounterIncremented | 1",
            "16:00 | 3534 | 1910 | +85.0% | Anomalous | Triggered | 2",
            "17:00 | 3667 | 1930 | +90.0% | Anomalous | SuppressedByCooldown | 3",
            "18:00 | 2660 | 1900 | +40.0% | Normal | CounterReset | 0",
        ];
        Assert.Equal(expected, TableRows(output));

        var position = -1;
        foreach (var outcome in new[] { "NoChange", "CounterIncremented", "CounterReset", "CounterIncremented", "Triggered", "SuppressedByCooldown", "CounterReset" })
        {
            var next = output.IndexOf(" | " + outcome + " | ", position + 1, StringComparison.Ordinal);
            Assert.True(next > position, $"Outcome {outcome} not found in order.");
            position = next;
        }
        Assert.Contains("2 consecutive increases: Latency 3534 vs reference 1910 (+85.0%)", output);
        Assert.Contains("Summary: 7 buckets, 1 triggers, 1 suppressed, 0 skipped", output);
    }

    [Fact]
    public async Task Replay_DoesNotWriteToStores()
    {
        var (stores, id) = await SeedAfternoonAsync();
        // Seeded directly on the inner stores, so they are not counted as writes.
        await stores.States.SaveAsync(new KpiDetectionState(id, 1, Day.AddHours(11), null, Day.AddHours(11)));
        await stores.Watermarks.SetAsync(id, Day.AddHours(19));
        var services = stores.BuildServices(Day.AddHours(19).AddMinutes(5));

        var (code, output) = await Run(services, "replay", "--kpi", id.ToString(),
            "--from", "2026-11-04T12:00:00Z", "--to", "2026-11-04T19:00:00Z");
        var (overrideCode, overrideOutput) = await Run(services, "replay", "--kpi", id.ToString(),
            "--from", "2026-11-04T12:00:00Z", "--to", "2026-11-04T19:00:00Z",
            "--param", "thresholdPercent=30", "--consecutive", "1", "--cooldown", "0");
        var (evaluateCode, _) = await Run(services, "evaluate", "--kpi", id.ToString());

        Assert.Equal(0, code);
        Assert.Equal(0, overrideCode);
        Assert.Equal(0, evaluateCode);
        Assert.Empty(stores.Log.Writes);

        Assert.Equal(7, TableRows(output).Count);
        Assert.Contains("16:00 | 3534 | 1910 | +85.0% | Anomalous | Triggered | 2", output);
        Assert.Contains("Summary: 7 buckets, 1 triggers, 1 suppressed, 0 skipped", output);

        // thresholdPercent 30 makes 18:00 (+40%) anomalous too; consecutive 1 + cooldown 0 triggers on every anomaly.
        Assert.Contains("Summary: 7 buckets, 5 triggers, 0 suppressed, 0 skipped", overrideOutput);
        Assert.Contains("\"thresholdPercent\":30", overrideOutput);

        // The real stores are untouched.
        Assert.Equal(new KpiDetectionState(id, 1, Day.AddHours(11), null, Day.AddHours(11)), await stores.States.GetAsync(id));
        Assert.Empty(await stores.Anomalies.GetRecentAsync(null, null, 10));
    }

    [Fact]
    public async Task Evaluate_UnknownKpi_ReturnsExitCode2()
    {
        var stores = new A6SpiedStores();
        var services = stores.BuildServices(Day);

        var (code, output) = await Run(services, "evaluate", "--kpi", "999");
        var (replayCode, _) = await Run(services, "replay", "--kpi", "999", "--from", "2026-11-04T00:00:00Z", "--to", "2026-11-05T00:00:00Z");

        Assert.Equal(2, code);
        Assert.Contains("KPI 999 not found", output);
        Assert.Equal(2, replayCode);
        Assert.Empty(stores.Log.Writes);
    }

    [Fact]
    public async Task Args_InvalidOption_ReturnsExitCode1WithUsage()
    {
        var (code, output) = await Run(null, "simulate", "--bogus", "x");

        Assert.Equal(1, code);
        Assert.Contains("Unknown option '--bogus'", output);
        Assert.Contains("Usage:", output);
    }

    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { "--help" }, 0)]
    [InlineData(new[] { "replay", "--help" }, 0)]
    [InlineData(new[] { "frobnicate" }, 1)]
    [InlineData(new[] { "simulate" }, 1)]
    [InlineData(new[] { "simulate", "--scenario", "morning" }, 1)]
    [InlineData(new[] { "evaluate", "--kpi" }, 1)]
    [InlineData(new[] { "evaluate", "--kpi", "abc" }, 1)]
    [InlineData(new[] { "evaluate", "--kpi", "1", "--hours", "0" }, 1)]
    [InlineData(new[] { "replay", "--kpi", "1", "--from", "2026-11-04T12:00:00Z" }, 1)]
    [InlineData(new[] { "replay", "--kpi", "1", "--from", "2026-11-04T12:00:00Z", "--to", "2026-11-04T10:00:00Z" }, 1)]
    [InlineData(new[] { "replay", "--kpi", "1", "--from", "2026-11-04T12:00:00Z", "--to", "2026-11-04T19:00:00Z", "--method", "Magic" }, 1)]
    [InlineData(new[] { "replay", "--kpi", "1", "--from", "2026-11-04T12:00:00Z", "--to", "2026-11-04T19:00:00Z", "--param", "novalue" }, 1)]
    public async Task Args_UsageAndValidation_ReturnExpectedExitCode(string[] args, int expected)
    {
        var (code, output) = await Run(new A6SpiedStores().BuildServices(Day), args);

        Assert.Equal(expected, code);
        Assert.Contains("Usage:", output);
    }

    [Fact]
    public async Task Replay_InvalidOverride_ReturnsExitCode1()
    {
        var (stores, id) = await SeedAfternoonAsync();

        var (code, output) = await Run(stores.BuildServices(Day), "replay", "--kpi", id.ToString(),
            "--from", "2026-11-04T12:00:00Z", "--to", "2026-11-04T19:00:00Z", "--method", "RobustZScore");

        Assert.Equal(1, code);
        Assert.Contains("RobustZScore requires windowBuckets", output);
        Assert.Empty(stores.Log.Writes);
    }

    [Fact]
    public async Task Evaluate_PreviewsPendingBucketsFromStoredState()
    {
        var (stores, id) = await SeedAfternoonAsync();
        await stores.States.SaveAsync(new KpiDetectionState(id, 1, Day.AddHours(14), null, Day.AddHours(15)));
        await stores.Watermarks.SetAsync(id, Day.AddHours(17));
        var services = stores.BuildServices(Day.AddHours(18).AddMinutes(30));

        var (code, output) = await Run(services, "evaluate", "--kpi", id.ToString(), "--hours", "24");

        Assert.Equal(0, code);
        // Watermark 17:00 -> only 15:00 and 16:00 are closed and ingested after the last evaluated bucket (14:00).
        Assert.Equal(
            ["15:00 | 3230 | 1900 | +70.0% | Anomalous | Triggered | 2", "16:00 | 3534 | 1910 | +85.0% | Anomalous | SuppressedByCooldown | 3"],
            TableRows(output));
        Assert.Contains("Resulting state (not saved): counter 3", output);
        Assert.Equal(1, (await stores.States.GetAsync(id)).ConsecutiveCount);
        Assert.Empty(stores.Log.Writes);
    }

    [Fact]
    public async Task ReadOnlyStores_ThrowOnEveryWrite()
    {
        var state = KpiDetectionState.Initial(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadOnlyKpiDefinitionStore(new InMemoryKpiDefinitionStore()).UpsertAsync(AfternoonDefinition(), "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadOnlyKpiBucketStore(new InMemoryKpiBucketStore()).UpsertAsync([Bucket(1, Day, 1)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadOnlyKpiStateStore(new InMemoryKpiStateStore()).SaveAsync(state));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadOnlyIngestionWatermarkStore(new InMemoryIngestionWatermarkStore()).SetAsync(1, Day));
    }
}
