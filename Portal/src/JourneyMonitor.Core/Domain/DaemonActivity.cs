namespace JourneyMonitor.Core.Domain;

/// <summary>The Spec 1000 background daemons that report activity. Values equal the existing MonkeyKey constants.</summary>
public static class DaemonKeys
{
    public const string KpiIngestion = MonkeyKey.KpiIngestion;
    public const string ApplicationHealthMonitor = MonkeyKey.ApplicationHealthMonitor;
    public const string BusinessHealthMonitor = MonkeyKey.BusinessHealthMonitor;
    public const string InfrastructureHealthMonitor = MonkeyKey.InfrastructureHealthMonitor;
    public const string TriageMonkey = MonkeyKey.TriageMonkey;
    public const string TokenRefresh = MonkeyKey.TokenRefresh;

    public static readonly IReadOnlyList<string> All =
        [KpiIngestion, ApplicationHealthMonitor, BusinessHealthMonitor, InfrastructureHealthMonitor, TriageMonkey, TokenRefresh];

    public static string DisplayName(string daemonKey) => daemonKey switch
    {
        KpiIngestion => "KPI Ingestion",
        ApplicationHealthMonitor => "Application Health Monitor",
        BusinessHealthMonitor => "Business Health Monitor",
        InfrastructureHealthMonitor => "Infrastructure Health Monitor",
        TriageMonkey => "Triage Monkey",
        TokenRefresh => "Token Refresh",
        _ => daemonKey,
    };

    public static string? ForPillar(KpiPillar pillar) => pillar switch
    {
        KpiPillar.Application => ApplicationHealthMonitor,
        KpiPillar.Business => BusinessHealthMonitor,
        KpiPillar.Infrastructure => InfrastructureHealthMonitor,
        _ => null,
    };
}

public enum DaemonActivityLevel { Info, Warning, Error }

/// <summary>Event kinds written to the activity log. Kept as strings so new kinds never need a schema change.</summary>
public static class DaemonActivityKinds
{
    public const string RunStarted = "run-started";
    public const string RunCompleted = "run-completed";
    public const string RunSkipped = "run-skipped";
    public const string IngestionKpi = "ingestion-kpi";
    public const string KpiEvaluated = "kpi-evaluated";
    public const string Anomaly = "anomaly";
    public const string TriageSubmitted = "triage-submitted";
    public const string InvestigationStarted = "investigation-started";
    public const string InvestigationCompleted = "investigation-completed";
    public const string TokenRefreshed = "token-refreshed";
    public const string Error = "error";
}

/// <summary>One line of the daemon console. ActivityId is assigned by the store (ignored on append). Message max 1000 chars.</summary>
public sealed record DaemonActivityEntry(
    long ActivityId,
    string DaemonKey,
    Guid RunId,
    DateTimeOffset At,
    DaemonActivityLevel Level,
    string Kind,
    string Message,
    string? DataJson);

public sealed record DaemonActivityPage(IReadOnlyList<DaemonActivityEntry> Entries, long LastId);

public static class DaemonHealthStates
{
    public const string Disabled = "disabled";
    public const string Running = "running";
    public const string Ok = "ok";
    public const string Stale = "stale";
    public const string Error = "error";
    public const string NeverRun = "never-run";
}

/// <summary>Card shown per daemon. Health is one of DaemonHealthStates.</summary>
public sealed record DaemonStatus(
    string DaemonKey,
    string DisplayName,
    string Schedule,
    bool Enabled,
    string? Mode,
    DateTimeOffset? LastRunAt,
    string? LastRunSummary,
    DaemonActivityLevel? LastRunLevel,
    DateTimeOffset? LastErrorAt,
    string? LastErrorMessage,
    string Health);
