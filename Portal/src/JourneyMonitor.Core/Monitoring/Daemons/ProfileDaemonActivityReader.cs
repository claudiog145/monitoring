using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>
/// Contract v6 §5: the daemon console for one company. An entry belongs to the company named in its
/// <c>DataJson.profileKey</c> (every company-owned entry is tagged through <see cref="ProfileTaggedDaemonRun"/>).
/// An entry about a KPI or an investigation that carries no profile (written before the daemons tagged
/// their entries) belongs to the KPI's company when <c>kpiOwners</c> knows it and is hidden otherwise, so
/// it can never surface in another company's console. Every other untagged entry (run started/completed,
/// token refresh...) is global and is shown to every company. The activity log itself is company-agnostic
/// (its schema is frozen), so the scope is applied over its pages here.
/// </summary>
public static class ProfileDaemonActivityReader
{
    /// <summary>How many log pages a forward read may scan looking for this company's entries.</summary>
    public const int MaxPagesScanned = 10;

    public static async Task<DaemonActivityPage> GetAsync(
        IDaemonActivityLog log,
        long afterId,
        string? daemonKey,
        DaemonActivityLevel? minLevel,
        int top,
        ProfileReadScope scope,
        int pageSize = 1000,
        IReadOnlyDictionary<int, string>? kpiOwners = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsEverything) return await log.GetAsync(afterId, daemonKey, minLevel, top, ct).ConfigureAwait(false);

        if (afterId <= 0)
        {
            // "Latest" read: the widest page, filtered, keeping the newest matches. LastId is the newest id
            // scanned so the next poll does not re-read what was already filtered out.
            var latest = await log.GetAsync(0, daemonKey, minLevel, pageSize, ct).ConfigureAwait(false);
            var kept = latest.Entries.Where(e => Includes(scope, e, kpiOwners)).ToList();
            if (kept.Count > top) kept = kept.GetRange(kept.Count - top, top);
            return new DaemonActivityPage(kept, latest.LastId);
        }

        var entries = new List<DaemonActivityEntry>();
        var cursor = afterId;
        for (var page = 0; page < MaxPagesScanned; page++)
        {
            var next = await log.GetAsync(cursor, daemonKey, minLevel, pageSize, ct).ConfigureAwait(false);
            foreach (var entry in next.Entries)
            {
                if (!Includes(scope, entry, kpiOwners)) continue;
                entries.Add(entry);
                // Stop on the last returned entry so the caller's next afterId does not skip unread matches.
                if (entries.Count == top) return new DaemonActivityPage(entries, entry.ActivityId);
            }

            if (next.Entries.Count == 0) break;
            cursor = next.LastId;
            if (next.Entries.Count < pageSize) break;
        }

        return new DaemonActivityPage(entries, Math.Max(cursor, afterId));
    }

    /// <summary>KPI id → owning company, for the untagged KPI entries written before the daemons tagged theirs.</summary>
    public static async Task<IReadOnlyDictionary<int, string>?> LoadKpiOwnersAsync(IKpiDefinitionStore? definitions, CancellationToken ct = default)
    {
        if (definitions is null) return null;
        var all = await definitions.GetAllAsync(ct).ConfigureAwait(false);
        var owners = new Dictionary<int, string>(all.Count);
        foreach (var definition in all) owners[definition.KpiDefinitionId] = ProfileReadScope.OwnerOf(definition.ProfileKey);
        return owners;
    }

    public static bool Includes(ProfileReadScope scope, DaemonActivityEntry entry, IReadOnlyDictionary<int, string>? kpiOwners = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsEverything) return true;
        var ownership = OwnershipOf(entry, kpiOwners);
        if (ownership.IsGlobal) return true;
        return ownership.ProfileKey is not null && scope.Includes(ownership.ProfileKey);
    }

    /// <summary>The <c>profileKey</c> written in the entry's data, or null when the entry carries none.</summary>
    public static string? ProfileKeyOf(DaemonActivityEntry entry) => OwnershipOf(entry, null).Tagged;

    /// <summary>Who an entry belongs to: global, one company, or an untagged company entry whose owner is unknown (both null).</summary>
    public static DaemonEntryOwnership OwnershipOf(DaemonActivityEntry entry, IReadOnlyDictionary<int, string>? kpiOwners)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.DataJson)) return DaemonEntryOwnership.Global;
        try
        {
            using var doc = JsonDocument.Parse(entry.DataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return DaemonEntryOwnership.Global;

            if (root.TryGetProperty(ProfileTaggedDaemonRun.ProfileKeyProperty, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                var key = value.GetString()!.Trim();
                return new DaemonEntryOwnership(key, key, IsGlobal: false);
            }

            if (root.TryGetProperty("kpiDefinitionId", out var kpi))
            {
                var owner = kpi.ValueKind == JsonValueKind.Number && kpi.TryGetInt32(out var id)
                    && kpiOwners is not null && kpiOwners.TryGetValue(id, out var profile)
                        ? profile
                        : null;
                return new DaemonEntryOwnership(owner, null, IsGlobal: false);
            }

            return root.TryGetProperty("investigationId", out _)
                ? new DaemonEntryOwnership(null, null, IsGlobal: false)
                : DaemonEntryOwnership.Global;
        }
        catch (JsonException)
        {
            return DaemonEntryOwnership.Global;
        }
    }
}

/// <param name="ProfileKey">The owning company, when known.</param>
/// <param name="Tagged">The <c>profileKey</c> written in the entry itself.</param>
/// <param name="IsGlobal">True when the entry is about the daemon as a whole and every company sees it.</param>
public sealed record DaemonEntryOwnership(string? ProfileKey, string? Tagged, bool IsGlobal)
{
    public static readonly DaemonEntryOwnership Global = new(null, null, IsGlobal: true);
}
