using System.Collections.Concurrent;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// In-memory company profiles for the fresh-clone path, with the same versioning and append-only
/// audit shape as <see cref="Sql.SqlCompanyProfileStore"/> - including the audit carrying the
/// WHOLE serialised document, so a test that asserts against the audit here is asserting against
/// the same contract the SQL store implements.
///
/// Starts EMPTY unless seeded, which is the deliberate difference from
/// <see cref="InMemoryTelemetrySourceStore"/>: a source catalog has a built-in default that
/// shipped with the build, whereas "what this company is" is the thing being onboarded. Inventing
/// one here would recreate the hardcoded-vocabulary problem the profile exists to remove. A
/// composition root that wants a default profile passes it to the constructor.
/// </summary>
public sealed class InMemoryCompanyProfileStore : ICompanyProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, CompanyProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CompanyProfileAuditEntry> _audit = [];
    private long _auditId;

    public InMemoryCompanyProfileStore(IEnumerable<CompanyProfile>? seed = null)
    {
        foreach (var profile in seed ?? [])
        {
            _profiles[profile.ProfileKey.Trim()] = profile;
        }
    }

    public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default)
    {
        // Disabled reads as absent, matching usp_GetCompanyProfile's Enabled filter and the
        // documented contract on ICompanyProfileStore.GetAsync.
        var found = _profiles.TryGetValue(profileKey.Trim(), out var profile) && profile.Enabled ? profile : null;
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CompanyProfile>>(
            _profiles.Values.OrderBy(p => p.ProfileKey, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default)
    {
        var key = profile.ProfileKey.Trim();
        var nextVersion = _profiles.TryGetValue(key, out var existing) ? existing.Version + 1 : 1;
        var stored = profile with
        {
            ProfileKey = key,
            Version = nextVersion,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = changedBy
        };

        _profiles[key] = stored;
        Record(key, nextVersion, JsonSerializer.Serialize(stored, JsonOptions), false, changedBy);
        return Task.FromResult(stored);
    }

    public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default)
    {
        var key = profileKey.Trim();
        var removed = _profiles.TryRemove(key, out var last);
        // The last known document is kept rather than nulled, same as usp_DeleteCompanyProfile:
        // off-boarding a company must not destroy the mapping that explains its past alerts.
        // Recorded even when nothing was removed - the attempt is still history.
        Record(
            key,
            removed ? last!.Version : 0,
            removed ? JsonSerializer.Serialize(last, JsonOptions) : "{}",
            true,
            changedBy);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default)
    {
        lock (_audit)
        {
            return Task.FromResult<IReadOnlyList<CompanyProfileAuditEntry>>(
                _audit.OrderByDescending(a => a.AuditId).Take(top).ToList());
        }
    }

    private void Record(string profileKey, int version, string profileJson, bool removed, string changedBy)
    {
        lock (_audit)
        {
            _audit.Add(new CompanyProfileAuditEntry(
                ++_auditId, profileKey, version, profileJson, removed, DateTimeOffset.UtcNow, changedBy));
        }
    }
}
