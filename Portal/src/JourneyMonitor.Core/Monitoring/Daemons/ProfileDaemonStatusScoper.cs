using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>
/// Contract v6 §5 (MC7): a daemon card read for one company shows that company's latest run summary and
/// error. Health, schedule and the run time stay the daemon's own (it is one process for every company);
/// only the texts change. When the latest line belongs to another company and this one has none in the
/// recent window, a neutral text replaces it so no other company's vocabulary leaks into the card.
/// </summary>
public static class ProfileDaemonStatusScoper
{
    public const string OtherCompanyRunSummary = "Latest run worked on another company; nothing for this company in the recent activity.";
    public const string OtherCompanyError = "The latest error was raised for another company.";

    public static async Task<IReadOnlyList<DaemonStatus>> ScopeAsync(
        IReadOnlyList<DaemonStatus> statuses,
        IDaemonActivityLog log,
        ProfileReadScope scope,
        IReadOnlyDictionary<int, string>? kpiOwners = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsEverything) return statuses;

        var scoped = new List<DaemonStatus>(statuses.Count);
        foreach (var status in statuses)
        {
            if (status.LastRunSummary is null && status.LastErrorMessage is null)
            {
                scoped.Add(status);
                continue;
            }
            var page = await log.GetAsync(0, status.DaemonKey, null, DaemonStatusReader.ActivityWindow, ct).ConfigureAwait(false);
            scoped.Add(Scope(status, page.Entries, scope, kpiOwners));
        }
        return scoped;
    }

    public static DaemonStatus Scope(
        DaemonStatus status,
        IReadOnlyList<DaemonActivityEntry> entries,
        ProfileReadScope scope,
        IReadOnlyDictionary<int, string>? kpiOwners = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsEverything) return status;

        var ordered = entries.OrderBy(e => e.ActivityId).ToList();
        bool Mine(DaemonActivityEntry e) => ProfileDaemonActivityReader.Includes(scope, e, kpiOwners);
        static bool IsRun(DaemonActivityEntry e) => e.Kind is DaemonActivityKinds.RunCompleted or DaemonActivityKinds.RunSkipped;
        static bool IsError(DaemonActivityEntry e) => e.Level == DaemonActivityLevel.Error;

        var result = status;
        // The triage idle text is the reader's own wording, not a log line: it names no company.
        if (status.LastRunSummary is not null && status.LastRunSummary != DaemonStatusReader.TriageIdleSummary)
        {
            var latest = ordered.LastOrDefault(IsRun);
            if (latest is not null && !Mine(latest))
            {
                var own = ordered.LastOrDefault(e => IsRun(e) && Mine(e));
                result = own is null
                    ? result with { LastRunSummary = OtherCompanyRunSummary }
                    : result with { LastRunSummary = own.Message, LastRunLevel = own.Level, LastRunAt = own.At };
            }
        }

        if (status.LastErrorMessage is not null)
        {
            var latest = ordered.LastOrDefault(IsError);
            if (latest is not null && !Mine(latest))
            {
                var own = ordered.LastOrDefault(e => IsError(e) && Mine(e));
                result = own is null
                    ? result with { LastErrorMessage = OtherCompanyError }
                    : result with { LastErrorMessage = own.Message, LastErrorAt = own.At };
            }
        }
        return result;
    }
}
