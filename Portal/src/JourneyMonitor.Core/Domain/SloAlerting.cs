namespace JourneyMonitor.Core.Domain;

/// <summary>
/// One pre-aggregated bucket for a golden signal (see docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md,
/// WI 1.1/1.11). <see cref="Stage"/> is null for the domain-level <c>exception_rate</c> signal -
/// an exception isn't attributed to a single journey stage the way a latency sample is.
/// <see cref="GoodCount"/>/<see cref="BadCount"/> follow the SLI convention (bad = violates the
/// matching <see cref="SloDefinition"/>); <see cref="TotalCount"/> is the sample population for
/// the bucket, not necessarily Good+Bad (kept distinct so a collector can report population
/// without yet knowing which samples are bad, though today's collectors always report both).
/// </summary>
public sealed record GoldenSignalSample(
    string Domain,
    string? Stage,
    string SignalType,
    DateTimeOffset BucketStart,
    int BucketMinutes,
    int GoodCount,
    int BadCount,
    int TotalCount);

/// <summary>
/// Operator-configured SLO target for one (Domain, Stage, SignalType) - Settings tab, same
/// override-with-audit pattern as <see cref="LatencySpikeSetting"/>. <see cref="TargetThresholdMs"/>
/// is only meaningful for <see cref="SignalType.Latency"/> (a single sample's pass/fail line);
/// <see cref="TargetPercentage"/>/<see cref="WindowDays"/> define the error budget itself (e.g.
/// "99% of samples good over 28 days").
/// </summary>
public sealed record SloDefinition(
    string Domain,
    string? Stage,
    string SignalType,
    int? TargetThresholdMs,
    decimal TargetPercentage,
    int WindowDays,
    bool Enabled,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

public sealed record SloDefinitionAuditEntry(
    string Domain,
    string? Stage,
    string SignalType,
    int? TargetThresholdMs,
    decimal TargetPercentage,
    int WindowDays,
    bool Enabled,
    DateTimeOffset ChangedAt,
    string ChangedBy);

public static class SignalType
{
    public const string Latency = "latency";
    public const string ExceptionRate = "exception_rate";
}

/// <summary>
/// One of Google SRE's multiwindow multi-burn-rate combinations (see the Alerting on SLOs
/// chapter): an alert fires only when BOTH the long and short window burn rates exceed
/// <see cref="BurnRateMultiplier"/> - this is what stops a single-cycle transient spike (which
/// would blow past the multiplier in the short window alone) from paging anyone.
/// </summary>
public sealed record BurnRateWindowSpec(
    string AlertSeverity,
    TimeSpan LongWindow,
    TimeSpan ShortWindow,
    double BurnRateMultiplier);

public static class AlertSeverity
{
    /// <summary>Fast-burn: notifies via every registered INotificationChannel (toast/Teams/email).</summary>
    public const string Page = "page";

    /// <summary>Slow-burn: dashboard-only, no outbound notification (see SloBurnRateMonkeyFunction).</summary>
    public const string Ticket = "ticket";
}

/// <summary>
/// The 3 combinations from the plan doc's Google SRE table: page=1h/5m@14.4x + 6h/30m@6x,
/// ticket=3d/6h@1x. Evaluated independently per (Domain, Stage/null, SignalType); the first
/// combo whose both windows exceed its multiplier decides the Finding's severity - page combos
/// are checked before the ticket combo so a genuinely fast burn is never under-reported as a
/// mere ticket.
/// </summary>
public static class BurnRateWindows
{
    public static readonly IReadOnlyList<BurnRateWindowSpec> All =
    [
        new(AlertSeverity.Page, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 14.4),
        new(AlertSeverity.Page, TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), 6.0),
        new(AlertSeverity.Ticket, TimeSpan.FromDays(3), TimeSpan.FromHours(6), 1.0),
    ];
}
