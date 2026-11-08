using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>Builds one <see cref="DaemonStatus"/> per daemon from its latest activity (contracts-daemons §5 "Estado").</summary>
public sealed class DaemonStatusReader(
    IDaemonActivityLog log,
    IOptions<HealthMonitoringOptions> healthOptions,
    IOptions<TriageOptions> triageOptions,
    IOptions<DaemonMonitoringOptions> daemonOptions,
    TimeProvider time,
    IMonkeyRunTracker? runTracker = null,
    IOptions<TokenRefreshOptions>? tokenRefreshOptions = null) : IDaemonStatusReader
{
    public const int ActivityWindow = 200;
    public const string TriageIdleSummary = "Idle — no queued investigations";

    public async Task<IReadOnlyList<DaemonStatus>> GetAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var health = healthOptions.Value;
        var triage = triageOptions.Value;
        var daemon = daemonOptions.Value;
        var tokenRefresh = tokenRefreshOptions?.Value ?? new TokenRefreshOptions();

        var statuses = new List<DaemonStatus>(DaemonKeys.All.Count);
        foreach (var key in DaemonKeys.All)
        {
            var page = await log.GetAsync(0, key, null, ActivityWindow, ct).ConfigureAwait(false);
            var isTriage = key == DaemonKeys.TriageMonkey;
            var enabled = key == DaemonKeys.TokenRefresh ? tokenRefresh.Enabled : IsEnabled(key, health, triage);
            var status = Build(
                key,
                key == DaemonKeys.TokenRefresh ? tokenRefresh.Schedule : Schedule(key, daemon),
                enabled,
                isTriage ? triage.Mode.ToString() : null,
                page.Entries,
                now,
                daemon,
                key == DaemonKeys.TokenRefresh ? Math.Max(daemon.StaleAfterMinutes, tokenRefresh.StaleAfterMinutes) : daemon.StaleAfterMinutes);
            if (isTriage && status.Health is DaemonHealthStates.Stale or DaemonHealthStates.NeverRun
                && await InspectorRanRecentlyAsync(now, daemon, ct).ConfigureAwait(false))
            {
                status = AsIdle(status, page.Entries);
            }
            statuses.Add(status);
        }
        return statuses;
    }

    /// <summary>
    /// The Triage Monkey only runs when the Inspector leases a queued signal, so a quiet period is not staleness
    /// as long as the Inspector itself keeps running (contracts-daemons §5 "Excepción Triage Monkey (idle)").
    /// </summary>
    private async Task<bool> InspectorRanRecentlyAsync(DateTimeOffset now, DaemonMonitoringOptions options, CancellationToken ct)
    {
        if (runTracker is null) return false;
        DateTimeOffset? inspectorLastRun;
        try
        {
            inspectorLastRun = await runTracker.GetLastRunAsync(MonkeyKey.InspectorMonkey, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        return inspectorLastRun is { } at && now - at <= TimeSpan.FromMinutes(options.StaleAfterMinutes);
    }

    private static DaemonStatus AsIdle(DaemonStatus status, IReadOnlyList<DaemonActivityEntry> entries)
    {
        var ordered = entries.OrderBy(e => e.ActivityId).ToList();
        var lastErrorIndex = ordered.FindLastIndex(e => e.Level == DaemonActivityLevel.Error);
        var lastStartedIndex = ordered.FindLastIndex(e => e.Kind == DaemonActivityKinds.RunStarted);
        var hasError = status.LastRunLevel == DaemonActivityLevel.Error || (lastErrorIndex >= 0 && lastErrorIndex > lastStartedIndex);
        return status with
        {
            LastRunSummary = TriageIdleSummary,
            Health = hasError ? DaemonHealthStates.Error : DaemonHealthStates.Ok,
        };
    }

    public static bool IsEnabled(string daemonKey, HealthMonitoringOptions health, TriageOptions triage) => daemonKey switch
    {
        DaemonKeys.TriageMonkey => triage.Mode != TriageMode.Off,
        DaemonKeys.KpiIngestion => health.Enabled,
        DaemonKeys.ApplicationHealthMonitor => IsPillarEnabled(health, KpiPillar.Application),
        DaemonKeys.BusinessHealthMonitor => IsPillarEnabled(health, KpiPillar.Business),
        DaemonKeys.InfrastructureHealthMonitor => IsPillarEnabled(health, KpiPillar.Infrastructure),
        _ => false,
    };

    /// <summary>Same rule PillarHealthMonitorService applies before running a pillar.</summary>
    public static bool IsPillarEnabled(HealthMonitoringOptions health, KpiPillar pillar) =>
        health.Enabled && health.Pillars is not null
        && health.Pillars.Any(p => string.Equals(p?.Trim(), pillar.ToString(), StringComparison.OrdinalIgnoreCase));

    private static string Schedule(string daemonKey, DaemonMonitoringOptions options) => daemonKey switch
    {
        DaemonKeys.KpiIngestion => options.IngestionSchedule,
        DaemonKeys.TriageMonkey => options.TriageSchedule,
        _ => options.MonitorSchedule,
    };

    private static DaemonStatus Build(
        string key, string schedule, bool enabled, string? mode, IReadOnlyList<DaemonActivityEntry> entries,
        DateTimeOffset now, DaemonMonitoringOptions options, int staleAfterMinutes)
    {
        var ordered = entries.OrderBy(e => e.ActivityId).ToList();

        var lastRunIndex = ordered.FindLastIndex(e => e.Kind is DaemonActivityKinds.RunCompleted or DaemonActivityKinds.RunSkipped);
        var lastErrorIndex = ordered.FindLastIndex(e => e.Level == DaemonActivityLevel.Error);
        var lastStartedIndex = ordered.FindLastIndex(e => e.Kind == DaemonActivityKinds.RunStarted);
        var lastCompletedIndex = ordered.FindLastIndex(e => e.Kind == DaemonActivityKinds.RunCompleted);

        var lastRun = lastRunIndex >= 0 ? ordered[lastRunIndex] : null;
        var lastError = lastErrorIndex >= 0 ? ordered[lastErrorIndex] : null;

        string healthState;
        if (!enabled)
        {
            healthState = DaemonHealthStates.Disabled;
        }
        else if (lastStartedIndex >= 0
                 && lastCompletedIndex < lastStartedIndex
                 && now - ordered[lastStartedIndex].At < TimeSpan.FromMinutes(options.RunningWindowMinutes))
        {
            healthState = DaemonHealthStates.Running;
        }
        else if (lastRun is null)
        {
            healthState = DaemonHealthStates.NeverRun;
        }
        else if (now - lastRun.At > TimeSpan.FromMinutes(staleAfterMinutes))
        {
            healthState = DaemonHealthStates.Stale;
        }
        else if (lastRun.Level == DaemonActivityLevel.Error || (lastErrorIndex >= 0 && lastErrorIndex > lastStartedIndex))
        {
            healthState = DaemonHealthStates.Error;
        }
        else
        {
            healthState = DaemonHealthStates.Ok;
        }

        return new DaemonStatus(
            key,
            DaemonKeys.DisplayName(key),
            schedule,
            enabled,
            mode,
            lastRun?.At,
            lastRun?.Message,
            lastRun?.Level,
            lastError?.At,
            lastError?.Message,
            healthState);
    }
}
