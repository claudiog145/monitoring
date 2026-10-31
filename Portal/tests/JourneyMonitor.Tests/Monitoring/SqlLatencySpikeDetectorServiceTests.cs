using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Returns a fixed, hand-built stored snapshot instead of calling SQL.</summary>
internal sealed class FakeJourneyStore(StoredJourneys? snapshot) : IJourneyStore
{
    public Task<JourneyUpsertResult> UpsertAsync(JourneyUpsertRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not needed by the detector under test.");

    public Task<StoredJourneys?> GetLatestAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult(snapshot);
}

public class SqlLatencySpikeDetectorServiceTests
{
    private static TelemetryJourneyRow Row(string correlationId, params (string Stage, double MaxMs, string Status, double FailureRatePct)[] stages) =>
        Row(correlationId, lastSeen: DateTimeOffset.UtcNow, stages);

    private static TelemetryJourneyRow Row(string correlationId, DateTimeOffset lastSeen, params (string Stage, double MaxMs, string Status, double FailureRatePct)[] stages)
    {
        var stagesJson = string.Join(",", stages.Select(s =>
            $$"""{"stage":"{{s.Stage}}","maxMs":{{s.MaxMs}},"stageTotalMs":{{s.MaxMs}},"status":"{{s.Status}}","failureRatePct":{{s.FailureRatePct}}}"""));
        return JsonSerializer.Deserialize<TelemetryJourneyRow>(
            $$"""{"CorrelationId":"{{correlationId}}","LastSeen":"{{lastSeen:O}}","Journey":[{{stagesJson}}]}""")!;
    }

    private static StoredJourneys Snapshot(params TelemetryJourneyRow[] rows) =>
        new(Guid.NewGuid(), rows.Length, DateTimeOffset.UtcNow, "15m", rows.ToList());

    private static SqlLatencySpikeDetectorService BuildService(
        StoredJourneys? snapshot,
        MonitoringOptions? monitoringOptions = null,
        IFindingsRepository? repository = null) =>
        new(
            new FakeJourneyStore(snapshot),
            new FakeLatencySpikeSettingsResolver(monitoringOptions ?? new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3 }),
            repository ?? new InMemoryFindingsRepository());

    [Fact]
    public async Task ReturnsNoFindingsWhenTheDomainHasNoSyncedSnapshotYet()
    {
        var service = BuildService(snapshot: null);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task BelowOccurrenceThresholdDoesNotCreateAFinding()
    {
        var snapshot = Snapshot(
            Row("c1", ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("07 Booking", 6000, "GREEN", 0)));
        var service = BuildService(snapshot);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task AtOrAboveOccurrenceThresholdCreatesAFindingWithHealthStatusEvidence()
    {
        var snapshot = Snapshot(
            Row("c1", ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("07 Booking", 7000, "GREEN", 0)),
            Row("c3", ("07 Booking", 41200, "RED", 40)));
        var service = BuildService(snapshot);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal(FindingType.LatencySpikeCluster, finding.FindingType);
        Assert.Equal("07 Booking", finding.Stage);
        Assert.Equal("cars:07 Booking", finding.CorrelationId);
        Assert.Equal(MonkeyKey.LatencySpikeMonkey, finding.OriginMonkeyKey);

        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("sql-journey-store", evidence.RootElement.GetProperty("source").GetString());
        Assert.Equal(3, evidence.RootElement.GetProperty("observedCount").GetInt32());
        Assert.Equal(41200, evidence.RootElement.GetProperty("maxDurationMs").GetDouble());
        Assert.True(evidence.RootElement.GetProperty("healthStatusBreakdown").TryGetProperty("RED", out var redCount));
        Assert.Equal(1, redCount.GetInt32());
        Assert.False(evidence.RootElement.TryGetProperty("statusCodeBreakdown", out _));
    }

    [Fact]
    public async Task DoesNotCreateADuplicateFindingWhileOneIsAlreadyOpenForTheSameStage()
    {
        var snapshot = Snapshot(
            Row("c1", ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("07 Booking", 7000, "GREEN", 0)),
            Row("c3", ("07 Booking", 8000, "GREEN", 0)));
        var repository = new InMemoryFindingsRepository();
        var service = BuildService(snapshot, repository: repository);

        var first = await service.RunAsync("cars", "production", CancellationToken.None);
        var second = await service.RunAsync("cars", "production", CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task StagesBelowThresholdAreIgnoredEvenWhenOtherStagesInTheSameJourneySpike()
    {
        var snapshot = Snapshot(
            Row("c1", ("01 Search", 500, "GREEN", 0), ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("01 Search", 500, "GREEN", 0), ("07 Booking", 7000, "GREEN", 0)),
            Row("c3", ("01 Search", 500, "GREEN", 0), ("07 Booking", 8000, "GREEN", 0)));
        var service = BuildService(snapshot);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal("07 Booking", finding.Stage);
    }

    [Fact]
    public async Task PreviewAsyncReturnsCandidateGroupsWithoutCreatingAFindingOrTouchingDedup()
    {
        var snapshot = Snapshot(
            Row("c1", ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("07 Booking", 7000, "GREEN", 0)),
            Row("c3", ("07 Booking", 41200, "RED", 40)));
        var repository = new InMemoryFindingsRepository();
        var service = BuildService(snapshot, repository: repository);

        var preview = await service.PreviewAsync("cars", "production", CancellationToken.None);

        var group = Assert.Single(preview);
        Assert.Equal("07 Booking", group.Stage);
        Assert.Equal(3, group.ObservedCount);
        Assert.Equal(41200, group.MaxDurationMs);
        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAsyncReturnsEmptyWhenTheDomainHasNoSyncedSnapshotYet()
    {
        var service = BuildService(snapshot: null);

        var preview = await service.PreviewAsync("cars", "production", CancellationToken.None);

        Assert.Empty(preview);
    }

    [Fact]
    public async Task RowsLastSeenOutsideTheWindowAreExcludedFromTheOccurrenceCount()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(
            Row("c1", now, ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", now, ("07 Booking", 7000, "GREEN", 0)),
            // Last synced well before a 15-minute window - stale data, must not count toward
            // "currently observed" even though it's still above threshold.
            Row("c3", now.AddHours(-2), ("07 Booking", 8000, "GREEN", 0)));
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 15 };
        var service = BuildService(snapshot, options);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        // Only 2 rows are within the window - below the occurrence threshold of 3.
        Assert.Empty(findings);
    }

    [Fact]
    public async Task ResolvesAnOpenFindingWhoseStageNoLongerQualifiesAfterASettingsChange()
    {
        var repository = new InMemoryFindingsRepository();
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var snapshot = Snapshot(
            Row("c1", ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("07 Booking", 7000, "GREEN", 0)),
            Row("c3", ("07 Booking", 8000, "GREEN", 0)));
        var first = await BuildService(snapshot, options, repository).RunAsync("cars", "production", CancellationToken.None);
        var findingId = Assert.Single(first).Id;

        // Threshold raised well above every row's MaxMs - the stage no longer qualifies at all.
        var raisedOptions = new MonitoringOptions { SpikeThresholdMs = 50000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var second = await BuildService(snapshot, raisedOptions, repository).RunAsync("cars", "production", CancellationToken.None);

        Assert.Empty(second);
        var resolved = await repository.GetByIdAsync(findingId, CancellationToken.None);
        Assert.Equal("resolved", resolved!.Status);
    }

    [Fact]
    public async Task DoesNotResolveOpenFindingsWhenTheSnapshotIsTemporarilyUnavailable()
    {
        var repository = new InMemoryFindingsRepository();
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var snapshot = Snapshot(
            Row("c1", ("07 Booking", 6000, "GREEN", 0)),
            Row("c2", ("07 Booking", 7000, "GREEN", 0)),
            Row("c3", ("07 Booking", 8000, "GREEN", 0)));
        var first = await BuildService(snapshot, options, repository).RunAsync("cars", "production", CancellationToken.None);
        var findingId = Assert.Single(first).Id;

        // A missing snapshot (e.g. the sync timer hasn't run yet this cycle) must not be treated
        // as "nothing qualifies anymore" - that would resolve every open finding on a fluke.
        await BuildService(snapshot: null, options, repository).RunAsync("cars", "production", CancellationToken.None);

        var stillOpen = await repository.GetByIdAsync(findingId, CancellationToken.None);
        Assert.Equal("open", stillOpen!.Status);
    }
}
