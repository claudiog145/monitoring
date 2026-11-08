using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>Process-wide cache of <see cref="EntityCatalogService"/> answers (contract v6 §4.2: 5 minutes).</summary>
public sealed class EntityCatalogCache(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (EntityListResponse Value, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out EntityListResponse value)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > _clock.GetUtcNow())
        {
            value = entry.Value;
            return true;
        }
        value = null!;
        return false;
    }

    public void Set(string key, EntityListResponse value, TimeSpan ttl)
    {
        if (_entries.Count > 1000) _entries.Clear();
        _entries[key] = (value, _clock.GetUtcNow().Add(ttl));
    }
}

/// <summary>
/// Contract v6 §4.2 GET /api/entities: top entities of ONE company by request volume, from that
/// company's own telemetry sources, identified with its entityIdentitySources. A company without a
/// readable source answers an empty list, never another company's entities.
/// </summary>
public sealed class EntityCatalogService(
    ICompanyProfileStore profiles,
    ITelemetrySource telemetry,
    IKqlSourceResolver sourceResolver,
    TelemetrySourceOwnershipCheck ownership,
    EntityCatalogCache cache,
    IOptions<ProfileJourneyOptions> options,
    IOptions<JourneySyncOptions> syncOptions,
    IOptions<InsightsOptions>? insights = null,
    TimeProvider? clock = null) : IEntityCatalog
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<EntityListResponse> GetTopEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var profile = await profiles.GetAsync(query.ProfileKey, cancellationToken).ConfigureAwait(false);
        var label = ProfileJourneyReader.LabelsOf(profile).Entity;
        if (profile is null) return new EntityListResponse(label, []);

        var service = string.IsNullOrWhiteSpace(query.Service) ? null : profile.FindService(query.Service);
        if (!string.IsNullOrWhiteSpace(query.Service) && service is null) return new EntityListResponse(label, []);

        var cacheKey = string.Join("|", profile.ProfileKey.Trim().ToLowerInvariant(), service?.Key.ToLowerInvariant() ?? "",
            ((long)query.Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), query.Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (cache.TryGet(cacheKey, out var cached)) return cached;

        ProfileTelemetryScope scope;
        try
        {
            scope = await ProfileJourneyTelemetryAccess.ResolveAsync(profile, ownership, syncOptions.Value.Environment, insights?.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProfileTelemetryUnavailableException)
        {
            return new EntityListResponse(label, []);
        }
        if (service is not null && scope.Profile.FindService(service.Key) is null) return new EntityListResponse(label, []);

        var to = _clock.GetUtcNow();
        var from = to - query.Window;
        var kql = ProfileJourneyKql.BuildEntityQuery(scope.Profile, service?.Key, from, to, Math.Max(1, query.Top));
        kql = await sourceResolver.ResolveAsync(kql, syncOptions.Value.Environment, cancellationToken).ConfigureAwait(false);
        var rows = await telemetry.QueryAsync(new TelemetryQueryRequest(kql, scope.AppId, scope.TenantId), cancellationToken).ConfigureAwait(false);

        var entities = rows
            .Select(r => new EntityCount(
                r.GetString(ProfileJourneyKql.Columns.Entity).Trim(),
                (long)Math.Round(r.GetDouble(ProfileJourneyKql.Columns.Requests) ?? 0),
                (long)Math.Round(r.GetDouble(ProfileJourneyKql.Columns.Failures) ?? 0)))
            .Where(e => e.Key.Length > 0)
            .OrderByDescending(e => e.Requests)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(Math.Max(1, query.Top))
            .ToList();

        var result = new EntityListResponse(label, entities);
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Clamp(options.Value.EntityCacheMinutes, 0, 60)));
        return result;
    }
}
