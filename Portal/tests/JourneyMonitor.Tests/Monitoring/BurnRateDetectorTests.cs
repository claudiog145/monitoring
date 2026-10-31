using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

internal sealed class FakeSloDefinitionStore(IReadOnlyList<SloDefinition> definitions) : ISloDefinitionStore
{
    public Task<IReadOnlyList<SloDefinition>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(definitions);
    public Task UpsertAsync(SloDefinition definition, string changedBy, CancellationToken cancellationToken) => throw new NotSupportedException("Not needed by the detector under test.");
    public Task<IReadOnlyList<SloDefinitionAuditEntry>> GetAuditAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SloDefinitionAuditEntry>>([]);
}

/// <summary>Ignores the `since` filter entirely and returns every sample matching (domain, signalType, stage) - BurnRateDetector itself filters by BucketStart per window combo, so the fake only needs to hand back the fixed fixture set.</summary>
internal sealed class FakeGoldenSignalStore(IReadOnlyList<GoldenSignalSample> samples) : IGoldenSignalStore
{
    public Task AddOrUpdateAsync(GoldenSignalSample sample, CancellationToken cancellationToken) => throw new NotSupportedException("Not needed by the detector under test.");

    public Task<IReadOnlyList<GoldenSignalSample>> GetSinceAsync(string domain, string signalType, string? stage, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GoldenSignalSample>>(samples
            .Where(s => string.Equals(s.Domain, domain, StringComparison.OrdinalIgnoreCase) && s.SignalType == signalType && s.Stage == stage)
            .ToList());
}

public class BurnRateDetectorTests
{
    private static GoldenSignalSample Bucket(string domain, string? stage, string signalType, DateTimeOffset bucketStart, int bucketMinutes, int total, int bad) =>
        new(domain, stage, signalType, bucketStart, bucketMinutes, GoodCount: total - bad, BadCount: bad, TotalCount: total);

    private static BurnRateDetector BuildDetector(IReadOnlyList<SloDefinition> definitions, IReadOnlyList<GoldenSignalSample> samples, IFindingsRepository? repository = null) =>
        new(
            new FakeSloDefinitionStore(definitions),
            new FakeGoldenSignalStore(samples),
            repository ?? new InMemoryFindingsRepository(),
            NullLogger<BurnRateDetector>.Instance);

    private static SloDefinition LatencySlo(string domain, string stage, int targetPercentage = 99) =>
        new(domain, stage, SignalType.Latency, TargetThresholdMs: 500, targetPercentage, WindowDays: 28, Enabled: true, UpdatedAt: null, UpdatedBy: null);

    [Fact]
    public async Task NoSamplesYet_ReturnsNoFindings()
    {
        var detector = BuildDetector([LatencySlo("cars", "07 Booking")], samples: []);

        var findings = await detector.RunAsync("cars", CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task SustainedFastBurn_TriggersAPageFinding()
    {
        var now = DateTimeOffset.UtcNow;
        // 12 five-minute buckets spanning the last hour, each 15% bad (burn rate 15x @ 99% target,
        // above the page combo's 14.4x threshold) - both the 1h long window and the 5m short
        // window see the same 15% ratio, so both breach and the page combo fires.
        var samples = Enumerable.Range(0, 12)
            .Select(i => Bucket("cars", "07 Booking", SignalType.Latency, now.AddMinutes(-5 * i), 5, total: 100, bad: 15))
            .ToList();
        var detector = BuildDetector([LatencySlo("cars", "07 Booking")], samples);

        var findings = await detector.RunAsync("cars", CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal(FindingType.SloBurnRate, finding.FindingType);
        Assert.Equal("critical", finding.Severity);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(AlertSeverity.Page, evidence.RootElement.GetProperty("alertSeverity").GetString());
    }

    [Fact]
    public async Task TransientSingleBucketSpike_DoesNotTrigger()
    {
        var now = DateTimeOffset.UtcNow;
        // One fully-bad 5-minute bucket "now", diluted by: 5 good buckets comfortably inside the
        // 1h/6h windows (10/20/30/40/50 minutes ago - clear of the exact 60-minute boundary so a
        // few milliseconds of clock drift between building the fixture and the detector's own
        // DateTimeOffset.UtcNow can never flip which buckets are in/out of a window), plus 2 large
        // good buckets comfortably inside the 3-day window (1 and 2 days ago, clear of the 72h
        // boundary). The long-window average never gets close to any combo's multiplier, so a
        // single transient blip must not page anyone.
        var samples = new List<GoldenSignalSample> { Bucket("cars", "07 Booking", SignalType.Latency, now, 5, total: 10, bad: 10) };
        samples.AddRange(new[] { 10, 20, 30, 40, 50 }.Select(minutesAgo =>
            Bucket("cars", "07 Booking", SignalType.Latency, now.AddMinutes(-minutesAgo), 5, total: 100, bad: 0)));
        samples.AddRange(new[] { 1, 2 }.Select(daysAgo =>
            Bucket("cars", "07 Booking", SignalType.Latency, now.AddDays(-daysAgo), 60, total: 5000, bad: 0)));
        var detector = BuildDetector([LatencySlo("cars", "07 Booking")], samples);

        var findings = await detector.RunAsync("cars", CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task UniformSlowBurn_TriggersOnlyTheTicketCombo()
    {
        var now = DateTimeOffset.UtcNow;
        // 72 hourly buckets over 3 days at a steady 2% bad rate: burn rate 2x at 99% target -
        // above the ticket combo's 1x but below the page combos' 6x/14.4x, so only a ticket fires.
        var samples = Enumerable.Range(0, 72)
            .Select(i => Bucket("cars", "07 Booking", SignalType.Latency, now.AddHours(-i), 60, total: 1000, bad: 20))
            .ToList();
        var detector = BuildDetector([LatencySlo("cars", "07 Booking")], samples);

        var findings = await detector.RunAsync("cars", CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal("medium", finding.Severity);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(AlertSeverity.Ticket, evidence.RootElement.GetProperty("alertSeverity").GetString());
    }

    [Fact]
    public async Task AlreadyOpenFinding_IsRefreshedInPlaceNotReturnedForRediagnosis()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 12)
            .Select(i => Bucket("cars", "07 Booking", SignalType.Latency, now.AddMinutes(-5 * i), 5, total: 100, bad: 15))
            .ToList();
        var repository = new InMemoryFindingsRepository();
        var detector = BuildDetector([LatencySlo("cars", "07 Booking")], samples, repository);

        var first = await detector.RunAsync("cars", CancellationToken.None);
        var second = await detector.RunAsync("cars", CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second); // Still breaching, but already open - refreshed, not re-returned/re-diagnosed.
    }

    [Fact]
    public async Task FindingIsResolvedOnceItStopsBreaching()
    {
        var now = DateTimeOffset.UtcNow;
        var breachingSamples = Enumerable.Range(0, 12)
            .Select(i => Bucket("cars", "07 Booking", SignalType.Latency, now.AddMinutes(-5 * i), 5, total: 100, bad: 15))
            .ToList();
        var repository = new InMemoryFindingsRepository();
        var definitions = new List<SloDefinition> { LatencySlo("cars", "07 Booking") };

        var firstDetector = BuildDetector(definitions, breachingSamples, repository);
        var first = await firstDetector.RunAsync("cars", CancellationToken.None);
        Assert.Single(first);

        var healthySamples = Enumerable.Range(0, 12)
            .Select(i => Bucket("cars", "07 Booking", SignalType.Latency, now.AddMinutes(-5 * i), 5, total: 100, bad: 0))
            .ToList();
        var secondDetector = BuildDetector(definitions, healthySamples, repository);
        var second = await secondDetector.RunAsync("cars", CancellationToken.None);
        Assert.Empty(second);

        var stored = await repository.GetOpenByCorrelationIdAsync("slo:cars:07 Booking:latency", CancellationToken.None);
        Assert.Null(stored); // No longer open - StaleFindingReconciler resolved it.
    }
}
