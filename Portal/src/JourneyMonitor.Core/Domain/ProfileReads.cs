namespace JourneyMonitor.Core.Domain;

/// <summary>Contract v6 §4.1. Filters of <c>GET /api/profile-journeys</c>; <see cref="Window"/> is how far back from now.</summary>
public sealed record ProfileJourneyQuery(
    string ProfileKey,
    string? Service,
    string? Entity,
    string? Status,
    TimeSpan Window,
    int Top);

/// <summary>Contract v6 §4.1. What the active company calls each role, so the UI never hard-codes a column name.</summary>
public sealed record ProfileJourneyLabels(
    string Service,
    string Flow,
    string Step,
    string Entity);

/// <summary>Contract v6 §4.1. Body of <c>GET /api/profile-journeys</c>: <c>{ profileKey, labels, journeys }</c>.</summary>
public sealed record ProfileJourneyListResponse(
    string ProfileKey,
    ProfileJourneyLabels Labels,
    IReadOnlyList<ProfileJourney> Journeys);

/// <summary>Contract v6 §4.1. Body of <c>GET /api/profile-journeys/{journeyId}</c>: the journey and its steps.</summary>
public sealed record ProfileJourneyDetailResponse(
    string ProfileKey,
    ProfileJourneyLabels Labels,
    ProfileJourney Journey,
    IReadOnlyList<ProfileJourneyStep> Steps);

/// <summary>Contract v6 §4.2. Filters of <c>GET /api/entities</c>.</summary>
public sealed record EntityQuery(
    string ProfileKey,
    string? Service,
    TimeSpan Window,
    int Top);

/// <summary>Contract v6 §4.2. One entity with its request and failure volume in the window.</summary>
public sealed record EntityCount(
    string Key,
    long Requests,
    long Failures);

/// <summary>Contract v6 §4.2. Body of <c>GET /api/entities</c>: <c>{ entityLabel, entities }</c>, top by volume.</summary>
public sealed record EntityListResponse(
    string EntityLabel,
    IReadOnlyList<EntityCount> Entities);
