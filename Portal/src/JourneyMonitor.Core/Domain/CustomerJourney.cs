namespace JourneyMonitor.Core.Domain;

/// <summary>
/// One correlated customer journey (one row from the domain KQL, grouped by member/UserId,
/// or by CorrelationId for Cars/Cruises). Field-for-field port of the object shape produced
/// by map-v6.js / map-air.js in the original JavaScript application. Keep names aligned with
/// the JSON contract the front end already consumes (camelCase via System.Text.Json).
/// </summary>
public sealed class CustomerJourney
{
    public string Source { get; set; } = "v6";
    public required string Id { get; set; }
    public required string CorrelationId { get; set; }
    public required string Domain { get; set; }
    public required string Customer { get; set; }
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? StartDate { get; set; }
    public int? Adults { get; set; }
    public int? Children { get; set; }
    public string Status { get; set; } = "";
    public string Outcome { get; set; } = "";
    public double TotalDurationMs { get; set; }
    public string BookingId { get; set; } = "";
    public string? Timestamp { get; set; }
    public string Region { get; set; } = "";
    public required string Environment { get; set; }
    public string FailReason { get; set; } = "";
    public string? LastStep { get; set; }
    public int SessionCount { get; set; }
    public List<JourneySessionRef> Sessions { get; set; } = new();
    public int CallsNoCorrelation { get; set; }
    public int CallsViaCorrelation { get; set; }
    public int BookOk { get; set; }
    public int BookFail { get; set; }
    public int FailedCalls { get; set; }
    public bool PassedSearch { get; set; }
    public bool IsBookSuccessful { get; set; }
    public int Exceptions { get; set; }
    public int TotalCalls { get; set; }
    public double? OverallP95Ms { get; set; }
    public List<JourneyStage> Stages { get; set; } = new();
}

public sealed class JourneySessionRef
{
    public int Session { get; set; }
    public string CorrelationId { get; set; } = "";
    public string ChangeReason { get; set; } = "";
}

/// <summary>One stage of a journey (e.g. "02 Hotel Search"), with waterfall timing offsets.</summary>
public sealed class JourneyStage
{
    public required string Name { get; set; }
    public string Component { get; set; } = "\u2014";
    public double StartOffsetMs { get; set; }
    public double DurationMs { get; set; }
    /// <summary>UI status: "success" | "degraded" | "failed" | "skipped".</summary>
    public required string Status { get; set; }
    public string Downstream { get; set; } = "\u2014";
    public int RequestCount { get; set; }
    public bool Error { get; set; }
    public string Diagnostic { get; set; } = "";
    public double? P95Ms { get; set; }
    public double? FailureRatePct { get; set; }
    /// <summary>Raw KQL traffic-light: GREEN | YELLOW | RED | N/A.</summary>
    public string? V6Status { get; set; }
    public List<JourneyCall> Calls { get; set; } = new();
}

/// <summary>One aggregated call group within a stage (an "api", "vendor", "result" or "phase" row).</summary>
public sealed class JourneyCall
{
    public required string Kind { get; set; }
    public required string Call { get; set; }
    public string CorrelationId { get; set; } = "";
    public string? T { get; set; }
    public int N { get; set; }
    public double? CallTotalMs { get; set; }
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }
    public double? MaxMs { get; set; }
    public double? FailureRatePct { get; set; }
    public bool Error { get; set; }
    public string? Status { get; set; }
    public string Detail { get; set; } = "";
    public List<JourneyCallError> Errors { get; set; } = new();
}

public sealed class JourneyCallError
{
    public int? N { get; set; }
    public string? T { get; set; }
    public string Message { get; set; } = "";
}
