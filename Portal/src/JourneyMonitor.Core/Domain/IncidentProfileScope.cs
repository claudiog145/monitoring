using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Incidents;

namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Contract v6 §4.3: incident reads scoped to one company. Every row of an answer carries the
/// requested <c>ProfileKey</c>; nothing of another company is ever returned. The incumbent
/// (<see cref="KpiProfiles.Default"/>) is held to its own domain list when no domain is named;
/// another company's domains are its own service keys.
/// </summary>
public interface IProfileIncidentReader
{
    Task<IReadOnlyList<IncidentRow>> ListForProfileAsync(
        string profileKey, IncidentReadQuery query, CancellationToken cancellationToken);

    /// <summary>One count per domain of <paramref name="profileKey"/>. A domain with no matching incidents is absent.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByDomainForProfileAsync(
        string profileKey, IncidentCountQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Contract v6 §3/§4.3: one exception sync written under a company. The dedupe key is
/// (ProfileKey, ClubId, Environment, Fingerprint), so the same error in two companies is two incidents.
/// </summary>
public interface IProfileIncidentStore
{
    Task<IncidentIngestResult> IngestForProfileAsync(
        string profileKey, IncidentIngestRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One exception read from a service's telemetry aliases, already classified by the profile:
/// <see cref="ExceptionSignal.ClubId"/> is the entity, <see cref="StepKey"/>/<see cref="Step"/> the
/// step of the request the exception belongs to (empty when no step claimed it).
/// </summary>
public sealed record ProfileExceptionSignal(
    ExceptionSignal Signal,
    string? StepKey,
    string? Step);

/// <summary>What one service's exception query returned, or why nothing was read.</summary>
public sealed record ProfileExceptionFetch(
    IReadOnlyList<ProfileExceptionSignal> Signals,
    string? SkipReason)
{
    public static ProfileExceptionFetch Skipped(string reason) => new([], reason);
}

/// <summary>
/// Reads the <c>exceptions</c> of one service of a company profile (the service's telemetry
/// source aliases, never the incumbent's fixed query) and classifies service, step and entity
/// with the profile's own rules (<see cref="CompanyKqlTemplater"/>).
/// </summary>
public interface IProfileExceptionSource
{
    Task<ProfileExceptionFetch> FetchAsync(
        CompanyProfile profile,
        ServiceDefinition service,
        TimeSpan window,
        string environment,
        CancellationToken cancellationToken);
}
