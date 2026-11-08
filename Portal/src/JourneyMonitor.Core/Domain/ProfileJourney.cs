namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Contract v6 §4.1. One generic journey of one company: a single operation (or a session, when the
/// profile defines one) classified service → flow → step with the company profile. Mirrors a row of
/// monitoring.ProfileJourney; <see cref="Status"/> is one of <see cref="ProfileJourneyStatus"/>.
/// </summary>
public sealed record ProfileJourney(
    string ProfileKey,
    string JourneyId,
    string ServiceKey,
    string? FlowKey,
    string? EntityKey,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMs,
    string Status,
    int? LastStepOrder,
    string? LastStepKey,
    int StepCount,
    bool Failed,
    string? SampleOperationId);

/// <summary>Contract v6 §4.1. One request of a <see cref="ProfileJourney"/>; mirrors monitoring.ProfileJourneyStep.</summary>
public sealed record ProfileJourneyStep(
    string ProfileKey,
    string JourneyId,
    int StepOrder,
    string? StepKey,
    string Name,
    DateTimeOffset Timestamp,
    double DurationMs,
    bool Success,
    string? ResultCode);

/// <summary>Contract v6 §4.1 journey states.</summary>
public static class ProfileJourneyStatus
{
    /// <summary>Reached the last step of its flow.</summary>
    public const string Completed = "Completed";

    /// <summary>At least one request failed.</summary>
    public const string Failed = "Failed";

    public const string DroppedPrefix = "Dropped at step ";

    /// <summary>Stopped before the last step of its flow; <paramref name="stepOrder"/> is the last step reached.</summary>
    public static string DroppedAt(int stepOrder) => DroppedPrefix + stepOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
