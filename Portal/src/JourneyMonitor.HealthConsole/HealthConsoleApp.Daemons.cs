using System.Globalization;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.DependencyInjection;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.HealthConsole;

/// <summary>status and watch: the portal's Daemons tab in the terminal, read-only.</summary>
public static partial class HealthConsoleApp
{
    /// <summary>
    /// Each poll re-reads this many ids below the highest one seen: SQL identity values can commit out of
    /// order when several daemons write at once, so a lower id may become visible after a higher one.
    /// Already seen ids are skipped, so the overlap never duplicates a line.
    /// </summary>
    public const int WatchOverlap = 100;

    public const int WatchPageSize = 1000;

    private sealed record WatchSettings(string? DaemonKey, DaemonActivityLevel? MinLevel, int IntervalSeconds, int Since, int? MaxPolls, string? Profile = null);

    /// <summary>
    /// Ctrl+C policy; returns the value for ConsoleCancelEventArgs.Cancel. Only a running watch intercepts the
    /// first press (it stops cleanly with exit 0); every other command, and a second press, terminate the process.
    /// </summary>
    public static bool HandleCancelKeyPress(string[] args, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var isWatch = args is { Length: > 0 } && args[0] == "watch" && !args.Any(a => a is "--help" or "-h");
        if (!isWatch || cancellation.IsCancellationRequested) return false;
        cancellation.Cancel();
        return true;
    }

    // ---------------------------------------------------------------- status

    private static async Task<int> StatusAsync(TextWriter output, IServiceProvider services, string? profile, CancellationToken ct)
    {
        var daemons = DaemonServices.From(services);
        var statuses = await daemons.Status.GetAsync(ct);
        foreach (var s in statuses)
        {
            var last = s.LastRunAt is { } at ? at.ToUniversalTime().ToString("HH:mm:ss", Inv) + " UTC" : "never";
            await output.WriteLineAsync(string.Create(Inv,
                $"{s.DisplayName,-30} {s.Health,-9} last {last}  {OneLine(s.LastRunSummary)}").TrimEnd());
            if (s.LastErrorAt is { } errorAt)
            {
                await output.WriteLineAsync(string.Create(Inv,
                    $"  ! {errorAt.ToUniversalTime():HH:mm:ss} {OneLine(s.LastErrorMessage)}").TrimEnd());
            }
        }
        await WriteProfileSummaryAsync(output, services, profile, ct);
        return ExitOk;
    }

    // ---------------------------------------------------------------- watch

    private static async Task<int> WatchAsync(
        WatchSettings settings, TextWriter output, IServiceProvider services, Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct)
    {
        var daemons = DaemonServices.From(services);
        delay ??= (interval, token) => Task.Delay(interval, daemons.Time, token);
        var interval = TimeSpan.FromSeconds(settings.IntervalSeconds);

        await output.WriteLineAsync(string.Create(Inv,
            $"Watching daemon activity (daemon: {settings.DaemonKey ?? "all"}, profile: {settings.Profile ?? "all"}, level: {(settings.MinLevel ?? DaemonActivityLevel.Info).ToString().ToLowerInvariant()}+, every {settings.IntervalSeconds} s). Ctrl+C to stop."));

        // Ids already shown or already present at startup; the overlapping poll window skips them, so only
        // entries that became visible after startup (including late-committed lower ids) are printed.
        var seen = new HashSet<long>();
        long lastId = 0;
        try
        {
            // The startup page covers at least the first poll's overlap window, so history that was not
            // part of the --since tail is seeded as seen instead of being printed by the first poll.
            var initial = await daemons.Log.GetAsync(0, settings.DaemonKey, settings.MinLevel, Math.Max(settings.Since, WatchOverlap + 1), ct);
            var ordered = initial.Entries.OrderBy(e => e.ActivityId).ToList();
            foreach (var e in ordered)
            {
                seen.Add(e.ActivityId);
                lastId = Math.Max(lastId, e.ActivityId);
            }
            foreach (var e in ordered.Skip(Math.Max(0, ordered.Count - settings.Since)))
            {
                if (!MatchesProfile(e, settings.Profile)) continue;
                await output.WriteLineAsync(FormatActivity(e));
            }

            for (var poll = 1; settings.MaxPolls is not { } max || poll <= max; poll++)
            {
                await delay(interval, ct);
                ct.ThrowIfCancellationRequested();

                var afterId = Math.Max(0, lastId - WatchOverlap);
                var page = await daemons.Log.GetAsync(afterId, settings.DaemonKey, settings.MinLevel, WatchPageSize, ct);
                foreach (var e in page.Entries.OrderBy(e => e.ActivityId))
                {
                    if (!seen.Add(e.ActivityId)) continue;
                    lastId = Math.Max(lastId, e.ActivityId);
                    if (!MatchesProfile(e, settings.Profile)) continue;
                    await output.WriteLineAsync(FormatActivity(e));
                }
                var keepFrom = lastId - WatchPageSize;
                seen.RemoveWhere(id => id <= keepFrom);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            await output.WriteLineAsync("Stopped.");
        }
        return ExitOk;
    }

    public static string FormatActivity(DaemonActivityEntry e) =>
        string.Create(Inv,
            $"{e.At.ToUniversalTime():HH:mm:ss} {e.Level.ToString().ToUpperInvariant(),-7} {e.DaemonKey,-28} {ProfileOf(e) ?? NoProfile,-ProfileWidth} {e.Kind,-24} {OneLine(e.Message)}").TrimEnd();

    private static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static bool TryGetWatchSettings(ParsedOptions options, out WatchSettings? settings, out string error)
    {
        settings = null;
        error = "";

        string? daemonKey = null;
        if (options.Single("daemon") is { } daemonText)
        {
            daemonKey = DaemonKeys.All.FirstOrDefault(k => string.Equals(k, daemonText.Trim(), StringComparison.OrdinalIgnoreCase));
            if (daemonKey is null)
            {
                error = $"--daemon must be one of {string.Join(", ", DaemonKeys.All)}, got '{daemonText}'.";
                return false;
            }
        }

        DaemonActivityLevel? level = null;
        if (options.Single("level") is { } levelText)
        {
            level = levelText.Trim().ToLowerInvariant() switch
            {
                "info" => DaemonActivityLevel.Info,
                "warning" => DaemonActivityLevel.Warning,
                "error" => DaemonActivityLevel.Error,
                _ => null,
            };
            if (level is null)
            {
                error = $"--level must be info, warning or error, got '{levelText}'.";
                return false;
            }
        }

        if (!TryGetBoundedInt(options, "interval", 3, 1, 60, out var interval, out error)) return false;
        if (!TryGetBoundedInt(options, "since", 50, 0, WatchPageSize, out var since, out error)) return false;
        int? maxPolls = null;
        if (options.Single("max-polls") is not null)
        {
            if (!TryGetBoundedInt(options, "max-polls", 1, 1, int.MaxValue, out var polls, out error)) return false;
            maxPolls = polls;
        }

        settings = new WatchSettings(daemonKey, level, interval, since, maxPolls, ProfileFilter(options));
        return true;
    }

    private static bool TryGetBoundedInt(ParsedOptions options, string name, int fallback, int min, int max, out int value, out string error)
    {
        value = fallback;
        error = "";
        if (options.Single(name) is not { } text) return true;
        if (!int.TryParse(text, NumberStyles.Integer, Inv, out value) || value < min || value > max)
        {
            error = max == int.MaxValue
                ? $"--{name} must be an integer >= {min}, got '{text}'."
                : $"--{name} must be an integer between {min} and {max}, got '{text}'.";
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- services

    /// <summary>
    /// Same store resolution as the Functions host (AddSpec1000: SQL when monitoring.DaemonActivity exists,
    /// otherwise in memory). Only the activity log, the run tracker and the options are used from it.
    /// </summary>
    private static ServiceProvider BuildDaemonServices(string? connection)
    {
        var configuration = BuildConfiguration(connection);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSpec1000(configuration);
        // The Triage idle rule reads the Inspector's last run; registered here because the console does not
        // call AddJourneyMonitorMonitoring. A missing MonkeyRun table only makes that rule not apply.
        services.AddSingleton<IMonkeyRunTracker>(sp => sp.GetRequiredService<IDaemonActivityLog>() is SqlDaemonActivityLog
            ? ActivatorUtilities.CreateInstance<SqlMonkeyRunTracker>(sp)
            : new InMemoryMonkeyRunTracker());

        var provider = services.BuildServiceProvider();
        if (provider.GetRequiredService<IDaemonActivityLog>() is InMemoryDaemonActivityLog)
        {
            var hasConnection = !string.IsNullOrWhiteSpace(configuration[JourneyStoreOptions.AppConfigKey])
                || !string.IsNullOrWhiteSpace(configuration[$"{JourneyStoreOptions.SectionName}:ConnectionString"]);
            Console.Error.WriteLine(hasConnection
                ? "[HealthConsole] monitoring.DaemonActivity was not found on the configured database: reading an empty in-memory log."
                : "[HealthConsole] No JourneyStore connection is configured (use --connection or JourneyStore__ConnectionString): " +
                  "reading an empty in-memory log.");
        }
        return provider;
    }

    /// <summary>The Core status reader built over the read-only log, so nothing reachable from here can write.</summary>
    private sealed record DaemonServices(IDaemonActivityLog Log, IDaemonStatusReader Status, TimeProvider Time)
    {
        public static DaemonServices From(IServiceProvider services)
        {
            var log = new ReadOnlyDaemonActivityLog(services.GetRequiredService<IDaemonActivityLog>());
            var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var tracker = services.GetService<IMonkeyRunTracker>();
            var status = new DaemonStatusReader(
                log,
                OptionsOf<HealthMonitoringOptions>(services),
                OptionsOf<TriageOptions>(services),
                OptionsOf<DaemonMonitoringOptions>(services),
                time,
                tracker is null ? null : new ReadOnlyMonkeyRunTracker(tracker));
            return new DaemonServices(log, status, time);
        }

        private static IOptions<T> OptionsOf<T>(IServiceProvider services) where T : class, new() =>
            services.GetService<IOptions<T>>() ?? Options.Create(new T());
    }
}
