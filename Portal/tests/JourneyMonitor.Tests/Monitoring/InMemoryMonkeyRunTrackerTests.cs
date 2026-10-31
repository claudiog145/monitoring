using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

public class InMemoryMonkeyRunTrackerTests
{
    [Fact]
    public async Task GetLastRunReturnsNullWhenTheMonkeyHasNeverRun()
    {
        var tracker = new InMemoryMonkeyRunTracker();

        Assert.Null(await tracker.GetLastRunAsync(MonkeyKey.LatencySpikeMonkey, CancellationToken.None));
    }

    [Fact]
    public async Task GetLastRunReturnsTheMostRecentlyRecordedTimeForThatMonkey()
    {
        var tracker = new InMemoryMonkeyRunTracker();
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        var second = DateTimeOffset.UtcNow;

        await tracker.RecordRunAsync(MonkeyKey.LatencySpikeMonkey, first, CancellationToken.None);
        await tracker.RecordRunAsync(MonkeyKey.LatencySpikeMonkey, second, CancellationToken.None);

        Assert.Equal(second, await tracker.GetLastRunAsync(MonkeyKey.LatencySpikeMonkey, CancellationToken.None));
    }

    [Fact]
    public async Task EachMonkeyKeyTracksItsOwnLastRunIndependently()
    {
        var tracker = new InMemoryMonkeyRunTracker();
        var spikeRun = DateTimeOffset.UtcNow.AddMinutes(-1);
        var doctorRun = DateTimeOffset.UtcNow;

        await tracker.RecordRunAsync(MonkeyKey.LatencySpikeMonkey, spikeRun, CancellationToken.None);
        await tracker.RecordRunAsync(MonkeyKey.DoctorMonkey, doctorRun, CancellationToken.None);

        Assert.Equal(spikeRun, await tracker.GetLastRunAsync(MonkeyKey.LatencySpikeMonkey, CancellationToken.None));
        Assert.Equal(doctorRun, await tracker.GetLastRunAsync(MonkeyKey.DoctorMonkey, CancellationToken.None));
    }
}

public class InMemoryDoctorReportsRepositoryTests
{
    private static DoctorReport Report(DateTimeOffset generatedAt) => new()
    {
        FindingId = Guid.NewGuid(),
        Verdict = "test",
        ErrorCategory = "unknown",
        Confidence = 0,
        ModelUsed = "test-model",
        GeneratedAt = generatedAt,
    };

    [Fact]
    public async Task GetAllAsyncReturnsEveryReportNewestFirst()
    {
        var repository = new InMemoryDoctorReportsRepository();
        var older = Report(DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = Report(DateTimeOffset.UtcNow);
        await repository.AddAsync(older, CancellationToken.None);
        await repository.AddAsync(newer, CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Equal(newer.Id, all[0].Id);
        Assert.Equal(older.Id, all[1].Id);
    }

    [Fact]
    public async Task GetAllAsyncReturnsEmptyWhenNoReportsExist()
    {
        var repository = new InMemoryDoctorReportsRepository();

        var all = await repository.GetAllAsync(CancellationToken.None);

        Assert.Empty(all);
    }
}
