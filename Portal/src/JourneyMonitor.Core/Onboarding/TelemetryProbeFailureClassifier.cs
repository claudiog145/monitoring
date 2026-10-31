namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Decides whether a failed probe means "this resource has no such table" or "we could not ask".
///
/// They arrive through the same channel - <c>ITelemetrySource</c> surfaces both as an exception
/// carrying the Application Insights error body - but they are opposite findings: a missing table
/// is a fact about the customer's instrumentation that onboarding must record, while a throttled
/// or unauthorised query is a fact about us and must be retried before anyone draws a conclusion.
/// Collapsing them is how a report ends up asserting "this customer emits no exceptions" when the
/// truth was "our identity could not read the resource".
/// </summary>
public static class TelemetryProbeFailureClassifier
{
    /// <summary>Kusto semantic errors emitted when a table or column name does not exist.</summary>
    private static readonly string[] TableAbsentMarkers =
    [
        "failed to resolve table or column expression",
        "failed to resolve entity",
        "sem0100",
        "could not be resolved to a table",
        "unknown table",
    ];

    public static TelemetryProbeState Classify(Exception exception) => Classify(Flatten(exception));

    public static TelemetryProbeState Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return TelemetryProbeState.Failed;
        return TableAbsentMarkers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase))
            ? TelemetryProbeState.TableAbsent
            : TelemetryProbeState.Failed;
    }

    /// <summary>
    /// The whole message chain, trimmed. The Application Insights error body is often nested one
    /// level down (HttpRequestException wrapping, aggregate faults), and the marker we need to see
    /// lives in the innermost one.
    /// </summary>
    public static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)) parts.Add(current.Message.Trim());
        }
        var joined = string.Join(" | ", parts);
        return joined.Length > 1000 ? joined[..1000] : joined;
    }
}
