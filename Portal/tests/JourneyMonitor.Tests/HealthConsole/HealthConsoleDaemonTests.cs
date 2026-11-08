using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.HealthConsole;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.D3;

public class HealthConsoleDaemonTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 2, 0, TimeSpan.Zero);

    private static async Task<(int ExitCode, string Output)> Run(
        IServiceProvider services, Func<TimeSpan, CancellationToken, Task>? delay, params string[] args)
    {
        using var writer = new StringWriter();
        var code = await HealthConsoleApp.RunAsync(args, writer, services, delay ?? D3NoDelay, CancellationToken.None);
        return (code, writer.ToString());
    }

    private static Task D3NoDelay(TimeSpan interval, CancellationToken ct) => Task.CompletedTask;

    private static DaemonActivityEntry D3Entry(string daemon, DateTimeOffset at, DaemonActivityLevel level, string kind, string message, long id = 0) =>
        new(id, daemon, Guid.Empty, at, level, kind, message, null);

    private static List<string> Lines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    private static int Occurrences(string output, string text)
    {
        var count = 0;
        for (var i = output.IndexOf(text, StringComparison.Ordinal); i >= 0; i = output.IndexOf(text, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public async Task Status_PrintsOneLinePerDaemon()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            D3Entry(DaemonKeys.KpiIngestion, Now.AddMinutes(-2), DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, "Run started"),
            D3Entry(DaemonKeys.KpiIngestion, Now.AddSeconds(-115), DaemonActivityLevel.Info, DaemonActivityKinds.RunCompleted, "Up to date — nothing to ingest"),
            D3Entry(DaemonKeys.ApplicationHealthMonitor, Now.AddSeconds(-90), DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, "Run started"),
            D3Entry(DaemonKeys.ApplicationHealthMonitor, Now.AddSeconds(-89), DaemonActivityLevel.Error, DaemonActivityKinds.Error, "Latency/hotels: boom"),
            D3Entry(DaemonKeys.ApplicationHealthMonitor, Now.AddSeconds(-88), DaemonActivityLevel.Warning, DaemonActivityKinds.RunCompleted, "0 evaluated, 0 triggered, 0 suppressed, 1 skipped"),
        ]);
        var services = new D3ServiceProvider(log, Now, new HealthMonitoringOptions { Enabled = true, Pillars = ["Application", "Business"] });

        var (code, output) = await Run(services, null, "status");

        Assert.Equal(0, code);
        var lines = Lines(output);
        var daemonLines = lines.Where(l => !l.StartsWith("  !", StringComparison.Ordinal)).ToList();
        Assert.Equal(DaemonKeys.All.Count, daemonLines.Count);
        Assert.Equal(DaemonKeys.All.Select(DaemonKeys.DisplayName), daemonLines.Select(l => l[..30].TrimEnd()));

        Assert.Equal($"{"KPI Ingestion",-30} {"ok",-9} last 10:00:05 UTC  Up to date — nothing to ingest", daemonLines[0]);
        Assert.Equal($"{"Application Health Monitor",-30} {"error",-9} last 10:00:32 UTC  0 evaluated, 0 triggered, 0 suppressed, 1 skipped", daemonLines[1]);
        Assert.Equal($"{"Business Health Monitor",-30} {"never-run",-9} last never", daemonLines[2]);
        Assert.Equal($"{"Infrastructure Health Monitor",-30} {"disabled",-9} last never", daemonLines[3]);
        Assert.Equal($"{"Triage Monkey",-30} {"never-run",-9} last never", daemonLines[4]);

        // The error line follows its daemon's line.
        Assert.Equal("  ! 10:00:31 Latency/hotels: boom", lines[lines.IndexOf(daemonLines[1]) + 1]);
        Assert.Single(lines, l => l.StartsWith("  !", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_PrintsNewEntriesOnlyOnce()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            D3Entry(DaemonKeys.KpiIngestion, Now.AddSeconds(-30), DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, "seed-1"),
            D3Entry(DaemonKeys.KpiIngestion, Now.AddSeconds(-29), DaemonActivityLevel.Info, DaemonActivityKinds.RunCompleted, "seed-2"),
        ]);
        var services = new D3ServiceProvider(log, Now);
        var polls = 0;
        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            polls++;
            // Poll 3 adds nothing: an idle poll must not reprint the overlap window.
            if (polls <= 2)
            {
                return log.AppendAsync([D3Entry(DaemonKeys.TriageMonkey, Now.AddSeconds(polls), DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, $"poll-{polls}")]);
            }
            return Task.CompletedTask;
        }

        var (code, output) = await Run(services, Delay, "watch", "--max-polls", "3");

        Assert.Equal(0, code);
        foreach (var message in new[] { "seed-1", "seed-2", "poll-1", "poll-2" })
        {
            Assert.Equal(1, Occurrences(output, message));
        }
        Assert.True(output.IndexOf("poll-1", StringComparison.Ordinal) < output.IndexOf("poll-2", StringComparison.Ordinal));
        Assert.Contains(
            $"10:02:01 {"INFO",-7} {DaemonKeys.TriageMonkey,-28} {HealthConsoleApp.NoProfile,-12} {DaemonActivityKinds.RunStarted,-24} poll-1",
            Lines(output));
    }

    [Fact]
    public async Task Watch_PrintsLateCommittedLowerIdsOnce()
    {
        // Concurrent writers: 1002 and 1004 commit after 1003 and 1005 are already visible.
        var log = new D3ScriptedActivityLog();
        D3Add(log, 1000, 1001, 1003);
        var polls = 0;
        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            polls++;
            if (polls == 1) D3Add(log, 1005);
            if (polls == 2) D3Add(log, 1002, 1004, 1006);
            return Task.CompletedTask;
        }

        var (code, output) = await Run(new D3ServiceProvider(log, Now), Delay, "watch", "--max-polls", "3");

        Assert.Equal(0, code);
        Assert.Equal([0L, 903L, 905L, 906L], log.Calls.Select(c => c.AfterId));
        Assert.All(log.Calls.Skip(1), c => Assert.Equal(HealthConsoleApp.WatchPageSize, c.Top));
        foreach (var id in new[] { 1000, 1001, 1002, 1003, 1004, 1005, 1006 })
        {
            Assert.Equal(1, Occurrences(output, $" id-{id}"));
        }
        Assert.Equal(["id-1000", "id-1001", "id-1003", "id-1005", "id-1002", "id-1004", "id-1006"], D3Messages(output));
    }

    [Fact]
    public async Task Watch_SinceZero_PrintsOnlyNewIncludingLateCommits()
    {
        var log = new D3ScriptedActivityLog();
        D3Add(log, 1000, 1001, 1003);
        var polls = 0;
        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            polls++;
            // 1002 is below the maximum visible at startup but was never seen: it must be printed.
            if (polls == 1) D3Add(log, 1002, 1004);
            return Task.CompletedTask;
        }

        var (code, output) = await Run(new D3ServiceProvider(log, Now), Delay, "watch", "--since", "0", "--max-polls", "2");

        Assert.Equal(0, code);
        Assert.Equal([0L, 903L, 904L], log.Calls.Select(c => c.AfterId));
        Assert.Equal(["id-1002", "id-1004"], D3Messages(output));
    }

    [Fact]
    public async Task Watch_SinceTail_DoesNotReprintOlderHistoryInOverlap()
    {
        var log = new D3ScriptedActivityLog();
        D3Add(log, 1000, 1001, 1002, 1003, 1004);

        var (code, output) = await Run(new D3ServiceProvider(log, Now), null, "watch", "--since", "2", "--max-polls", "2");

        Assert.Equal(0, code);
        Assert.Equal(["id-1003", "id-1004"], D3Messages(output));
        Assert.Equal([0L, 904L, 904L], log.Calls.Select(c => c.AfterId));
    }

    private static void D3Add(D3ScriptedActivityLog log, params long[] ids)
    {
        foreach (var id in ids)
        {
            log.Add(D3Entry(DaemonKeys.KpiIngestion, Now, DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi, $"id-{id}", id));
        }
    }

    private static List<string> D3Messages(string output) =>
        Lines(output).Where(l => l.Length > 8 && l[2] == ':' && l[5] == ':').Select(l => l[(l.LastIndexOf(' ') + 1)..]).ToList();

    [Fact]
    public void CtrlC_OnlyWatchInterceptsFirstPress()
    {
        foreach (var command in new[] { "status", "evaluate", "replay", "simulate" })
        {
            using var other = new CancellationTokenSource();
            Assert.False(HealthConsoleApp.HandleCancelKeyPress([command], other));
            Assert.False(other.IsCancellationRequested);
        }

        using var cts = new CancellationTokenSource();
        Assert.True(HealthConsoleApp.HandleCancelKeyPress(["watch", "--max-polls", "3"], cts));
        Assert.True(cts.IsCancellationRequested);
        // A second press terminates the process.
        Assert.False(HealthConsoleApp.HandleCancelKeyPress(["watch", "--max-polls", "3"], cts));
    }

    [Fact]
    public async Task StatusAndWatch_DefaultServices_RunReadOnlyInMemory()
    {
        using var status = new StringWriter();
        Assert.Equal(0, await HealthConsoleApp.RunAsync(["status"], status, null, D3NoDelay));
        Assert.Equal(DaemonKeys.All.Count, Lines(status.ToString()).Count);

        using var watch = new StringWriter();
        Assert.Equal(0, await HealthConsoleApp.RunAsync(["watch", "--max-polls", "1"], watch, null, D3NoDelay));
        Assert.StartsWith("Watching daemon activity", watch.ToString());
    }

    [Fact]
    public async Task Watch_FiltersByDaemonAndLevel()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            D3Entry(DaemonKeys.BusinessHealthMonitor, Now, DaemonActivityLevel.Info, DaemonActivityKinds.KpiEvaluated, "business-info"),
            D3Entry(DaemonKeys.BusinessHealthMonitor, Now, DaemonActivityLevel.Warning, DaemonActivityKinds.Anomaly, "business-warning"),
            D3Entry(DaemonKeys.BusinessHealthMonitor, Now, DaemonActivityLevel.Error, DaemonActivityKinds.Error, "business-error"),
            D3Entry(DaemonKeys.ApplicationHealthMonitor, Now, DaemonActivityLevel.Warning, DaemonActivityKinds.Anomaly, "application-warning"),
            D3Entry(DaemonKeys.ApplicationHealthMonitor, Now, DaemonActivityLevel.Error, DaemonActivityKinds.Error, "application-error"),
        ]);
        var services = new D3ServiceProvider(log, Now);
        Task Delay(TimeSpan interval, CancellationToken ct) => log.AppendAsync(
        [
            D3Entry(DaemonKeys.BusinessHealthMonitor, Now, DaemonActivityLevel.Info, DaemonActivityKinds.RunCompleted, "business-info-later"),
            D3Entry(DaemonKeys.BusinessHealthMonitor, Now, DaemonActivityLevel.Warning, DaemonActivityKinds.RunCompleted, "business-warning-later"),
            D3Entry(DaemonKeys.TriageMonkey, Now, DaemonActivityLevel.Error, DaemonActivityKinds.Error, "triage-error-later"),
        ]);

        var (code, output) = await Run(services, Delay, "watch", "--daemon", "Business-Health-Monitor", "--level", "WARNING", "--max-polls", "1");

        Assert.Equal(0, code);
        var activity = Lines(output).Where(l => l.Length > 8 && l[2] == ':' && l[5] == ':').ToList();
        Assert.Equal(3, activity.Count);
        Assert.Contains(activity, l => l.EndsWith(" business-warning", StringComparison.Ordinal));
        Assert.Contains(activity, l => l.EndsWith(" business-error", StringComparison.Ordinal));
        Assert.Contains(activity, l => l.EndsWith(" business-warning-later", StringComparison.Ordinal));
        Assert.All(activity, l => Assert.Contains($" {DaemonKeys.BusinessHealthMonitor,-28} ", l));
        Assert.DoesNotContain("business-info", output);
        Assert.DoesNotContain("application-", output);
        Assert.DoesNotContain("triage-error-later", output);
    }

    [Fact]
    public async Task Watch_StopsAfterMaxPolls()
    {
        var log = new D3ScriptedActivityLog();
        log.Add(D3Entry(DaemonKeys.KpiIngestion, Now, DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, "only", 1));
        var delays = new List<TimeSpan>();
        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            delays.Add(interval);
            return Task.CompletedTask;
        }

        var (code, _) = await Run(new D3ServiceProvider(log, Now), Delay, "watch", "--max-polls", "4", "--interval", "7", "--since", "10");

        Assert.Equal(0, code);
        Assert.Equal(4, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(7), d));
        // One initial read (at least the overlap window, so its history is seeded as seen) plus one per poll.
        Assert.Equal(5, log.Calls.Count);
        Assert.Equal((0L, HealthConsoleApp.WatchOverlap + 1), (log.Calls[0].AfterId, log.Calls[0].Top));
    }

    [Fact]
    public async Task Watch_CtrlC_ExitsCleanly()
    {
        var log = new D3ScriptedActivityLog();
        using var cts = new CancellationTokenSource();
        var services = new D3ServiceProvider(log, Now);
        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromCanceled(ct);
        }

        using var writer = new StringWriter();
        var code = await HealthConsoleApp.RunAsync(["watch"], writer, services, Delay, cts.Token);

        Assert.Equal(0, code);
        Assert.Contains("Stopped.", writer.ToString());
    }

    [Theory]
    [InlineData("--interval", "0")]
    [InlineData("--interval", "61")]
    [InlineData("--level", "debug")]
    [InlineData("--daemon", "inspector-monkey")]
    [InlineData("--since", "-1")]
    [InlineData("--max-polls", "0")]
    public async Task Watch_InvalidArguments_Return1(string option, string value)
    {
        var log = new D3ScriptedActivityLog();

        var (code, output) = await Run(new D3ServiceProvider(log, Now), null, "watch", option, value);

        Assert.Equal(1, code);
        Assert.StartsWith("Error: ", output);
        Assert.Empty(log.Calls);
    }

    [Fact]
    public async Task StatusAndWatch_NeverWrite()
    {
        var log = new D3ScriptedActivityLog();
        log.Add(D3Entry(DaemonKeys.KpiIngestion, Now, DaemonActivityLevel.Info, DaemonActivityKinds.RunCompleted, "done", 1));
        var services = new D3ServiceProvider(log, Now, tracker: new D3WriteFailingRunTracker());

        Assert.Equal(0, (await Run(services, null, "status")).ExitCode);
        Assert.Equal(0, (await Run(services, null, "watch", "--max-polls", "2")).ExitCode);
        Assert.Equal(0, log.Writes);

        var guarded = new ReadOnlyDaemonActivityLog(log);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.AppendAsync([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.PurgeOlderThanAsync(Now));
        Assert.Equal(0, log.Writes);
    }

    [Fact]
    public async Task Help_ListsStatusAndWatch()
    {
        using var writer = new StringWriter();
        var code = await HealthConsoleApp.RunAsync(["--help"], writer);

        Assert.Equal(0, code);
        Assert.Contains("status [--connection <sql>]", writer.ToString());
        Assert.Contains("watch [--daemon <key>] [--level info|warning|error] [--interval <s>] [--since <n>] [--max-polls <n>]", writer.ToString());
    }
}

/// <summary>Activity log with caller-chosen ids (to simulate out-of-order commits) that records every read.</summary>
public sealed class D3ScriptedActivityLog : IDaemonActivityLog
{
    private readonly List<DaemonActivityEntry> _entries = [];

    public List<(long AfterId, string? DaemonKey, DaemonActivityLevel? MinLevel, int Top)> Calls { get; } = [];

    public int Writes { get; private set; }

    public void Add(DaemonActivityEntry entry) => _entries.Add(entry);

    public Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default)
    {
        Writes++;
        return Task.CompletedTask;
    }

    public Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default)
    {
        Calls.Add((afterId, daemonKey, minLevel, top));
        var matching = _entries
            .Where(e => (daemonKey is null || e.DaemonKey == daemonKey) && (minLevel is null || e.Level >= minLevel))
            .OrderBy(e => e.ActivityId)
            .ToList();
        var page = afterId > 0
            ? matching.Where(e => e.ActivityId > afterId).Take(top).ToList()
            : matching.TakeLast(top).ToList();
        return Task.FromResult(new DaemonActivityPage(page, page.Count > 0 ? page[^1].ActivityId : afterId));
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        Writes++;
        return Task.FromResult(0);
    }
}

public sealed class D3WriteFailingRunTracker : IMonkeyRunTracker
{
    public Task RecordRunAsync(string monkeyKey, DateTimeOffset at, CancellationToken ct = default)
        => throw new InvalidOperationException("The console must not record runs.");

    public Task<DateTimeOffset?> GetLastRunAsync(string monkeyKey, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
}

public sealed class D3FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class D3ServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public D3ServiceProvider(IDaemonActivityLog log, DateTimeOffset now, HealthMonitoringOptions? health = null, IMonkeyRunTracker? tracker = null)
    {
        _services[typeof(IDaemonActivityLog)] = log;
        _services[typeof(TimeProvider)] = new D3FixedTimeProvider(now);
        _services[typeof(IOptions<HealthMonitoringOptions>)] = Options.Create(health ?? new HealthMonitoringOptions { Enabled = true });
        _services[typeof(IOptions<TriageOptions>)] = Options.Create(new TriageOptions());
        _services[typeof(IOptions<DaemonMonitoringOptions>)] = Options.Create(new DaemonMonitoringOptions());
        if (tracker is not null) _services[typeof(IMonkeyRunTracker)] = tracker;
    }

    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
