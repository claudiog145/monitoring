namespace JourneyMonitor.Core.Options;

public sealed class TokenRefreshOptions
{
    public const string SectionName = "TokenRefresh";
    public const string DefaultSchedule = "0 */5 * * * *";

    /// <summary>When false every timer run records run-skipped and touches nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A token is refreshed once it expires within this many minutes.</summary>
    public int RefreshAheadMinutes { get; set; } = 15;

    /// <summary>Shown on the daemon card; filled from spec1000_token_refresh_cron by DI.</summary>
    public string Schedule { get; set; } = DefaultSchedule;

    /// <summary>The daemon runs every 5 minutes, so the generic 5-minute stale window would flap; this one covers two missed runs.</summary>
    public int StaleAfterMinutes { get; set; } = 11;
}
