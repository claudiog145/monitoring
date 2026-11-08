using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>One classified request of a journey, as returned by <see cref="ProfileJourneyKql.BuildJourneyQuery"/>.</summary>
public sealed record ProfileJourneyRequest(
    string JourneyKey,
    DateTimeOffset Timestamp,
    string Name,
    double DurationMs,
    bool Success,
    string? ResultCode,
    string? ServiceKey,
    string? StepKey,
    string? Entity,
    string? OperationId = null);

/// <summary>The journeys of one sync run and their steps, ready for <see cref="Abstractions.IProfileJourneyStore.UpsertAsync"/>.</summary>
public sealed record ProfileJourneyBatch(
    IReadOnlyList<ProfileJourney> Journeys,
    IReadOnlyList<ProfileJourneyStep> Steps);

/// <summary>
/// Contract v6 §4.1: turns classified requests into journeys of one company. A journey is one
/// session (its <see cref="ProfileJourneyRequest.JourneyKey"/> is the <c>session_Id</c> when the request
/// has one, otherwise the <c>operation_Id</c>); its requests are ordered by timestamp, then by the
/// position of their step in the flow. Its service is the service most of its requests were classified into (ties: the
/// earliest), its flow the flow of that service holding most of its steps, and its status
/// <see cref="ProfileJourneyStatus.Failed"/> when any request failed, <see cref="ProfileJourneyStatus.Completed"/>
/// when it walked the flow from its first step to its last (a journey first seen at a later step is not a
/// completion), <see cref="ProfileJourneyStatus.DroppedAt"/> the furthest step reached otherwise. Its
/// <see cref="ProfileJourney.DurationMs"/> is the time its requests took (the sum of their durations, like the
/// incumbent's JourneyTotalMs), not the wall-clock span of the session, which includes the user's think time
/// between steps and stays available as <c>EndedAt - StartedAt</c>. Step positions are 1-based within the flow,
/// in <see cref="StepDefinition.Order"/>.
/// </summary>
public static class ProfileJourneyBuilder
{
    public const int MaxJourneyIdLength = 128;
    public const int MaxStepsPerJourney = 100;
    private const int MaxNameLength = 512;
    private const int MaxResultCodeLength = 32;
    private const int MaxEntityLength = 256;
    private const int MaxKeyLength = 128;

    public static ProfileJourneyBatch Build(CompanyProfile profile, IEnumerable<ProfileJourneyRequest> requests, int maxJourneysPerService)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(requests);

        var journeys = new List<(ProfileJourney Journey, IReadOnlyList<ProfileJourneyStep> Steps)>();
        var groups = requests
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.JourneyKey) && !string.IsNullOrWhiteSpace(r.ServiceKey))
            .GroupBy(r => r.JourneyKey.Trim(), StringComparer.Ordinal);

        foreach (var group in groups)
        {
            if (group.Key.Length > MaxJourneyIdLength) continue;
            var built = BuildOne(profile, group.Key, group.OrderBy(r => r.Timestamp).ToList());
            if (built is not null) journeys.Add(built.Value);
        }

        var cap = Math.Max(1, maxJourneysPerService);
        var kept = journeys
            .GroupBy(j => j.Journey.ServiceKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g
                .OrderByDescending(j => j.Journey.StartedAt)
                .ThenBy(j => j.Journey.JourneyId, StringComparer.Ordinal)
                .Take(cap))
            .OrderByDescending(j => j.Journey.StartedAt)
            .ThenBy(j => j.Journey.JourneyId, StringComparer.Ordinal)
            .ToList();

        return new ProfileJourneyBatch(
            kept.Select(j => j.Journey).ToList(),
            kept.SelectMany(j => j.Steps).ToList());
    }

    private static (ProfileJourney Journey, IReadOnlyList<ProfileJourneyStep> Steps)? BuildOne(
        CompanyProfile profile, string journeyId, IReadOnlyList<ProfileJourneyRequest> requests)
    {
        var classified = requests
            .Select(r => (Request: r, Service: profile.FindService(r.ServiceKey)))
            .Where(x => x.Service is not null)
            .ToList();
        if (classified.Count == 0) return null;

        var service = classified
            .GroupBy(x => x.Service!.Key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(x => x.Request.Timestamp))
            .First()
            .First().Service!;

        var stepKeys = requests
            .Select(r => r.StepKey?.Trim())
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var flow = (service.Flows ?? [])
            .Select((f, index) => (Flow: f, Index: index, Hits: (f.Steps ?? []).Count(s => stepKeys.Contains(s.Key))))
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Index)
            .Select(x => x.Flow)
            .FirstOrDefault();

        var orderedSteps = flow is null ? [] : OrderedSteps(flow);
        requests = requests
            .OrderBy(r => r.Timestamp)
            .ThenBy(r => PositionOf(orderedSteps, r.StepKey) ?? int.MaxValue)
            .ToList();
        int? firstPosition = null;
        int? lastPosition = null;
        string? lastStepKey = null;
        foreach (var request in requests)
        {
            var position = PositionOf(orderedSteps, request.StepKey);
            if (position is null) continue;
            firstPosition ??= position;
            if (lastPosition is null || position > lastPosition)
            {
                lastPosition = position;
                lastStepKey = orderedSteps[position.Value - 1].Key;
            }
        }

        // Completed means the journey walked its flow from the first step to the last. One that entered the
        // flow later (a lone request on the last step, a session whose start was not observed) is not a
        // completion: it is reported as dropped at the furthest step it reached.
        var failed = requests.Any(r => !r.Success);
        var status = failed
            ? ProfileJourneyStatus.Failed
            : lastPosition is not null && lastPosition == orderedSteps.Count && firstPosition == 1
                ? ProfileJourneyStatus.Completed
                : ProfileJourneyStatus.DroppedAt(lastPosition ?? 0);

        var startedAt = requests.Min(r => r.Timestamp);
        var endedAt = requests.Max(r => r.Timestamp.AddMilliseconds(Math.Max(0, r.DurationMs)));
        var entity = requests
            .Select(r => r.Entity?.Trim())
            .FirstOrDefault(e => !string.IsNullOrEmpty(e) && e.Length <= MaxEntityLength);

        var steps = requests
            .Take(MaxStepsPerJourney)
            .Select((r, index) => new ProfileJourneyStep(
                profile.ProfileKey,
                journeyId,
                index + 1,
                Truncate(r.StepKey?.Trim(), MaxKeyLength) is { Length: > 0 } key ? key : null,
                Truncate(r.Name, MaxNameLength) ?? "",
                r.Timestamp,
                Math.Max(0, r.DurationMs),
                r.Success,
                Truncate(r.ResultCode, MaxResultCodeLength)))
            .ToList();

        var journey = new ProfileJourney(
            profile.ProfileKey,
            journeyId,
            service.Key,
            flow?.Key,
            entity,
            startedAt,
            endedAt,
            steps.Sum(s => s.DurationMs),
            status,
            lastPosition,
            lastStepKey,
            steps.Count,
            failed,
            requests
                .Select(r => r.OperationId?.Trim())
                .FirstOrDefault(o => !string.IsNullOrEmpty(o) && o.Length <= MaxJourneyIdLength) ?? journeyId);

        return (journey, steps);
    }

    /// <summary>The steps of a flow in display order (stable on <see cref="StepDefinition.Order"/>).</summary>
    public static IReadOnlyList<StepDefinition> OrderedSteps(FlowDefinition flow) =>
        (flow.Steps ?? []).OrderBy(s => s.Order).ToList();

    private static int? PositionOf(IReadOnlyList<StepDefinition> steps, string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey)) return null;
        for (var i = 0; i < steps.Count; i++)
        {
            if (string.Equals(steps[i].Key, stepKey.Trim(), StringComparison.OrdinalIgnoreCase)) return i + 1;
        }
        return null;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
