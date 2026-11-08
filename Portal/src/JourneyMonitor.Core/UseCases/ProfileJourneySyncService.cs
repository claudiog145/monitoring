using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>What one sync run did for one profile.</summary>
public sealed record ProfileJourneySyncOutcome(string ProfileKey, int Journeys, int Steps, string? Error, IReadOnlyList<string> SkippedServices);

public sealed record ProfileJourneySyncResult(IReadOnlyList<ProfileJourneySyncOutcome> Profiles, int Purged);

/// <summary>
/// Contract v6 §4.1 ProfileJourneySync: for EVERY enabled company profile, reads the classified
/// requests of the last window from that profile's own telemetry sources, builds journeys with
/// <see cref="ProfileJourneyBuilder"/> and writes them under the profile's key. One profile failing
/// never stops the others. Afterwards journeys older than the retention are purged.
/// </summary>
public sealed class ProfileJourneySyncService(
    ICompanyProfileStore profiles,
    ITelemetrySource telemetry,
    IKqlSourceResolver sourceResolver,
    TelemetrySourceOwnershipCheck ownership,
    IProfileJourneyStore store,
    IOptions<ProfileJourneyOptions> options,
    IOptions<JourneySyncOptions> syncOptions,
    IOptions<InsightsOptions>? insights = null,
    TimeProvider? clock = null,
    ILogger<ProfileJourneySyncService>? logger = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger<ProfileJourneySyncService>.Instance;

    public async Task<ProfileJourneySyncResult> SyncAllAsync(CancellationToken ct)
    {
        var all = await profiles.GetAllAsync(ct).ConfigureAwait(false);
        var outcomes = new List<ProfileJourneySyncOutcome>();
        foreach (var profile in all.Where(p => p.Enabled))
        {
            outcomes.Add(await SyncProfileAsync(profile, ct).ConfigureAwait(false));
        }

        var purged = 0;
        try
        {
            var cutoff = _clock.GetUtcNow().AddDays(-Math.Max(1, options.Value.RetentionDays));
            purged = await store.PurgeOlderThanAsync(cutoff, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[ProfileJourneySync] retention purge failed.");
        }

        return new ProfileJourneySyncResult(outcomes, purged);
    }

    public async Task<ProfileJourneySyncOutcome> SyncProfileAsync(CompanyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var opts = options.Value;
        var key = profile.ProfileKey.Trim();
        try
        {
            var scope = await ProfileJourneyTelemetryAccess.ResolveAsync(
                profile, ownership, syncOptions.Value.Environment, insights?.Value, ct).ConfigureAwait(false);

            var to = _clock.GetUtcNow();
            var from = to - opts.GetSyncWindow();
            var cap = Math.Clamp(opts.MaxJourneysPerService, 1, 5000);
            var kql = ProfileJourneyKql.BuildJourneyQuery(scope.Profile, from, to, cap, Math.Clamp(opts.MaxRequestsPerRun, 1, 200_000));
            kql = await sourceResolver.ResolveAsync(kql, syncOptions.Value.Environment, ct).ConfigureAwait(false);

            var rows = await telemetry.QueryAsync(new TelemetryQueryRequest(kql, scope.AppId, scope.TenantId), ct).ConfigureAwait(false);
            var batch = ProfileJourneyBuilder.Build(scope.Profile, rows.Select(ToRequest).OfType<ProfileJourneyRequest>(), cap);
            if (batch.Journeys.Count > 0)
            {
                await store.UpsertAsync(key, batch.Journeys, batch.Steps, ct).ConfigureAwait(false);
            }

            foreach (var skipped in scope.SkippedServices)
            {
                _logger.LogInformation("[ProfileJourneySync] {Profile}: service skipped - {Reason}", key, skipped);
            }
            _logger.LogInformation("[ProfileJourneySync] {Profile}: {Journeys} journeys, {Steps} steps from {Rows} rows.",
                key, batch.Journeys.Count, batch.Steps.Count, rows.Count);
            return new ProfileJourneySyncOutcome(key, batch.Journeys.Count, batch.Steps.Count, null, scope.SkippedServices);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ProfileTelemetryUnavailableException or KqlSourceResolutionException or InvalidOperationException)
        {
            _logger.LogWarning("[ProfileJourneySync] {Profile}: not synced - {Message}", key, ex.Message);
            return new ProfileJourneySyncOutcome(key, 0, 0, ex.Message, []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProfileJourneySync] {Profile}: sync failed.", key);
            return new ProfileJourneySyncOutcome(key, 0, 0, ex.Message, []);
        }
    }

    /// <summary>One telemetry row as a classified request; null when it has no journey key or timestamp.</summary>
    public static ProfileJourneyRequest? ToRequest(TelemetryJourneyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var journeyKey = row.GetString(ProfileJourneyKql.Columns.JourneyKey).Trim();
        var timestamp = row.GetDateTimeOffset(ProfileJourneyKql.Columns.Timestamp);
        if (journeyKey.Length == 0 || timestamp is null) return null;

        return new ProfileJourneyRequest(
            journeyKey,
            timestamp.Value.ToUniversalTime(),
            row.GetString(ProfileJourneyKql.Columns.Name),
            row.GetDouble(ProfileJourneyKql.Columns.DurationMs) ?? 0,
            row.GetBool(ProfileJourneyKql.Columns.Success),
            NullIfEmpty(row.GetString(ProfileJourneyKql.Columns.ResultCode)),
            NullIfEmpty(row.GetString(ProfileJourneyKql.Columns.ServiceKey)),
            NullIfEmpty(row.GetString(ProfileJourneyKql.Columns.StepKey)),
            NullIfEmpty(row.GetString(ProfileJourneyKql.Columns.Entity)),
            NullIfEmpty(row.GetString(ProfileJourneyKql.Columns.OperationId)));
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
