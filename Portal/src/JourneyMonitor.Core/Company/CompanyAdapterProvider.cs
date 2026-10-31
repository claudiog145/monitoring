using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Company;

/// <summary>Which profile answers when a caller does not name one.</summary>
public sealed class ProfileDrivenCompanyOptions
{
    public const string SectionName = "Company";

    /// <summary>
    /// The FALLBACK for callers that pass no profile key - consulted only when no company has been
    /// activated in the database. Blank means there is no default and an unnamed request is an error.
    /// </summary>
    public string DefaultProfileKey { get; set; } = "";
}

/// <summary>
/// Hands out a <see cref="ProfileDrivenAdapter"/> per profile key, with the short-lived snapshot
/// plus explicit <see cref="Invalidate"/> that <c>KqlSourceResolver</c> and
/// <c>ServiceOwnerResolver</c> already standardised on: an edit made in the portal has to take
/// effect without waiting a TTL out.
///
/// Throws, unlike <c>ServiceOwnerResolver</c>. An unresolved owner still ships an investigation;
/// a missing profile yields a board on which nothing classifies, and a board full of unclassified
/// rows is indistinguishable from a healthy one. The XML doc on
/// <see cref="ICompanyAdapterProvider.GetAsync"/> is explicit that this is the one place the noisy
/// behaviour is the correct one.
///
/// Failures are never cached: a stale "this is broken" would outlive the fix that repaired it.
/// </summary>
/// <param name="activeStore">
/// Optional so a composition root without the monitoring schema - and the tests and harnesses
/// that build this provider with <c>new</c> - keep today's config-only behaviour. Absent means
/// "nobody can activate a company here", which is a degraded mode, not a failure.
/// </param>
public sealed class CompanyAdapterProvider(
    ICompanyProfileStore store,
    IOptions<ProfileDrivenCompanyOptions> options,
    ILogger<CompanyAdapterProvider> logger,
    IActiveCompanyProfileStore? activeStore = null) : ICompanyAdapterProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private volatile ActiveKeySnapshot? _activeKey;

    public async Task<ICompanyAdapter> GetAsync(string? profileKey = null, CancellationToken ct = default)
    {
        // An explicitly named profile always wins. Everything below is only about answering
        // "which company are we monitoring" when nobody said.
        var key = (profileKey ?? "").Trim();
        if (key.Length == 0)
        {
            key = await ReadActiveKeyAsync(ct) ?? (options.Value.DefaultProfileKey ?? "").Trim();
        }

        if (key.Length == 0)
        {
            throw new CompanyProfileResolutionException(
                $"No company profile was requested, none is active, and no default is configured " +
                $"(activate one with PUT /api/settings/company-profiles-active, or set " +
                $"{ProfileDrivenCompanyOptions.SectionName}:{nameof(ProfileDrivenCompanyOptions.DefaultProfileKey)}).");
        }

        if (_snapshots.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Adapter;
        }

        CompanyProfile? profile;
        try
        {
            profile = await store.GetAsync(key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Company] profile \"{ProfileKey}\" could not be read.", key);
            // CompanyProfileResolutionException carries no inner exception, so the cause has to be
            // folded into the message or it is lost at the first rethrow.
            throw new CompanyProfileResolutionException(
                $"Company profile \"{key}\" could not be read: {ex.GetType().Name}: {ex.Message}");
        }

        // The store already filters disabled rows out, but it is the adapter that would silently
        // classify a whole board against a profile somebody deliberately turned off.
        if (profile is null || !profile.Enabled)
        {
            throw new CompanyProfileResolutionException(
                $"No enabled company profile is registered under \"{key}\".");
        }

        var adapter = new ProfileDrivenAdapter(profile, logger);
        _snapshots[key] = new Snapshot(adapter, DateTimeOffset.UtcNow.Add(CacheTtl));
        return adapter;
    }

    /// <summary>
    /// The activated ProfileKey, or null when nothing is activated, the feature is not wired up,
    /// or the pointer could not be read.
    ///
    /// "Nothing is activated" IS cached - it is a successful read, and leaving it uncached would
    /// put a round trip in front of every classification on a deployment that never activates
    /// anything. A read FAILURE is not cached, for the reason the type doc gives, and is not
    /// fatal either: which company is active is a preference, and a cycle the configured default
    /// can still run must not be killed by a transient read.
    /// </summary>
    private async Task<string?> ReadActiveKeyAsync(CancellationToken ct)
    {
        if (activeStore is null) return null;

        var cached = _activeKey;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.ProfileKey;
        }

        try
        {
            var read = (await activeStore.GetActiveAsync(ct) ?? "").Trim();
            var resolved = read.Length == 0 ? null : read;
            _activeKey = new ActiveKeySnapshot(resolved, DateTimeOffset.UtcNow.Add(CacheTtl));
            return resolved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Company] the active company profile could not be read; falling back to the configured default \"{DefaultProfileKey}\".",
                options.Value.DefaultProfileKey);
            return null;
        }
    }

    public void Invalidate()
    {
        _snapshots.Clear();
        // Cleared too, or switching company would take up to the TTL to be seen - which is the
        // whole reason this method exists.
        _activeKey = null;
    }

    private sealed record Snapshot(ICompanyAdapter Adapter, DateTimeOffset ExpiresAt);

    /// <summary>Null <see cref="ProfileKey"/> means "read, and there is no active company".</summary>
    private sealed record ActiveKeySnapshot(string? ProfileKey, DateTimeOffset ExpiresAt);
}
