using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>Contract v6 §4.3: settings of the per-company exception ingestion (section <c>ProfileExceptions</c>).</summary>
public sealed class ProfileExceptionsOptions
{
    public const string SectionName = "ProfileExceptions";

    public bool Enabled { get; set; } = true;

    /// <summary>How far back each run reads. Runs overlap on purpose: an occurrence is stored once by its item id.</summary>
    public string Window { get; set; } = "1h";
}

/// <summary>One service of one company in one ingestion run.</summary>
public sealed record ProfileExceptionSyncPass(
    string ProfileKey,
    string Service,
    bool Stored,
    int IncidentCount,
    int InsertedIncidents,
    int UpdatedIncidents,
    int InsertedOccurrences,
    string? SkipReason);

/// <summary>
/// Contract v6 §4.3: exception ingestion for every enabled company other than the incumbent. For each
/// service it reads the <c>exceptions</c> of the service's own telemetry aliases (see
/// <see cref="IProfileExceptionSource"/>), groups them with <see cref="IncidentIngestionPlanner"/>
/// (Domain = service key, club = entity) and stores them under the company's ProfileKey.
/// The incumbent keeps its own pipeline (<see cref="SyncIncidentsService"/> and
/// <c>incident-exceptions.txt</c>), so its incidents and their grouping do not change.
/// An occurrence seen by two services of one company (a shared alias) is kept by the first.
/// </summary>
public sealed class SyncProfileExceptionsService(
    ICompanyProfileStore profiles,
    IProfileExceptionSource exceptionSource,
    IProfileIncidentStore incidentStore,
    IOptions<JourneySyncOptions> syncOptions,
    IOptions<ProfileExceptionsOptions> options)
{
    public const string UnknownEntity = "unknown";

    public static bool IsIncumbent(string? profileKey) =>
        string.Equals(profileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ProfileExceptionSyncPass>> SyncAllAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return [];

        var passes = new List<ProfileExceptionSyncPass>();
        foreach (var profile in await profiles.GetAllAsync(cancellationToken))
        {
            if (!profile.Enabled || IsIncumbent(profile.ProfileKey)) continue;
            passes.AddRange(await SyncProfileAsync(profile, cancellationToken));
        }

        return passes;
    }

    public async Task<IReadOnlyList<ProfileExceptionSyncPass>> SyncProfileAsync(
        CompanyProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var profileKey = profile.ProfileKey.Trim();
        if (IsIncumbent(profileKey))
        {
            return [Skip(profileKey, "*", "The incumbent's exceptions are ingested by the incumbent pipeline.")];
        }

        var windowText = (options.Value.Window ?? "").Trim();
        if (!ClubFilter.TryParseWindow(windowText, out var window))
        {
            return [Skip(profileKey, "*", $"window '{windowText}' is not a valid time range")];
        }

        var environment = (syncOptions.Value.Environment ?? "production").Trim().ToLowerInvariant();
        var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var passes = new List<ProfileExceptionSyncPass>();
        foreach (var service in profile.Services)
        {
            var serviceKey = (service.Key ?? "").Trim().ToLowerInvariant();
            if (serviceKey.Length == 0) continue;

            ProfileExceptionFetch fetch;
            try
            {
                fetch = await exceptionSource.FetchAsync(profile, service, window, environment, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                passes.Add(Skip(profileKey, serviceKey, ex.Message));
                continue;
            }

            if (fetch.SkipReason is not null)
            {
                passes.Add(Skip(profileKey, serviceKey, fetch.SkipReason));
                continue;
            }

            var signals = fetch.Signals
                .Where(s => !string.IsNullOrWhiteSpace(s.Signal.ItemId) && seenItemIds.Add(s.Signal.ItemId.Trim()))
                .ToList();
            passes.Add(await IngestAsync(profileKey, serviceKey, environment, windowText, signals, cancellationToken));
        }

        return passes;
    }

    /// <summary>
    /// The plan of one service: the incumbent's grouping rules with the profile's vocabulary - the club is
    /// the entity (blank becomes <see cref="UnknownEntity"/>) and the component is the step the profile
    /// classified, instead of the incumbent's fixed stage list.
    /// </summary>
    public static IncidentIngestPlan Plan(
        string serviceKey, string environment, IReadOnlyList<ProfileExceptionSignal> signals)
    {
        var steps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ExceptionSignal>(signals.Count);
        foreach (var item in signals)
        {
            var signal = string.IsNullOrWhiteSpace(item.Signal.ClubId)
                ? item.Signal with { ClubId = UnknownEntity }
                : item.Signal;
            normalized.Add(signal);
            var step = string.IsNullOrWhiteSpace(item.Step) ? item.StepKey : item.Step;
            if (!string.IsNullOrWhiteSpace(step) && !string.IsNullOrWhiteSpace(signal.ItemId))
            {
                steps.TryAdd(signal.ItemId.Trim(), Truncate(step.Trim(), 128));
            }
        }

        var plan = IncidentIngestionPlanner.Plan(serviceKey, environment, normalized);
        var incidents = plan.Incidents
            .Select(incident => incident with
            {
                Component = incident.Occurrences
                    .Select(o => steps.TryGetValue(o.ItemId, out var step) ? step : null)
                    .FirstOrDefault(step => step is not null) ?? JourneyStageResolver.Unclassified,
            })
            .ToList();
        return plan with { Incidents = incidents };
    }

    private async Task<ProfileExceptionSyncPass> IngestAsync(
        string profileKey,
        string serviceKey,
        string environment,
        string window,
        IReadOnlyList<ProfileExceptionSignal> signals,
        CancellationToken cancellationToken)
    {
        var plan = Plan(serviceKey, environment, signals);
        if (plan.Incidents.Count == 0)
        {
            return new ProfileExceptionSyncPass(profileKey, serviceKey, true, 0, 0, 0, 0, null);
        }

        var result = await incidentStore.IngestForProfileAsync(
            profileKey,
            new IncidentIngestRequest(serviceKey, environment, window, UnknownEntity, plan.Incidents),
            cancellationToken);

        return new ProfileExceptionSyncPass(
            profileKey,
            serviceKey,
            true,
            plan.Incidents.Count,
            result.InsertedIncidents,
            result.UpdatedIncidents,
            result.InsertedOccurrences,
            null);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static ProfileExceptionSyncPass Skip(string profileKey, string service, string reason) =>
        new(profileKey, service, false, 0, 0, 0, 0, reason);
}
