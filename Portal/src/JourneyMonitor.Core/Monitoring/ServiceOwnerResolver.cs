using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Answers "who fixes this service?" for the Inspector and the Red Alert policy.
///
/// Resolution order is exact stage, then a vertical-wide row, then unresolved. There is no
/// guess below that on purpose: the pilot exit criterion requires every Red Alert RCA to carry an
/// owner, and an owner invented from a naming convention would satisfy the criterion while being
/// worse than an admitted gap - the first alert routed to the wrong team costs the whole routing
/// layer its credibility.
///
/// Never throws. An unreadable map resolves to <see cref="ServiceOwnerResolution.Unavailable"/>,
/// which is kept distinct from "nobody owns it": one is our outage, the other is their gap, and
/// collapsing them would make a database blip look like an organisational problem.
/// </summary>
public sealed class ServiceOwnerResolver(
    IServiceOwnerStore store,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<ServiceOwnerResolver> logger) : IServiceOwnerResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    // Reference assignment is atomic; concurrent readers can at worst both refresh.
    private Snapshot? _snapshot;

    public async Task<ResolvedServiceOwner> ResolveAsync(string vertical, string stage, CancellationToken ct = default)
    {
        var owners = await GetAllAsync(ct);
        if (owners is null)
        {
            return ResolvedServiceOwner.Unresolved(ServiceOwnerResolution.Unavailable);
        }

        var exact = owners.FirstOrDefault(o =>
            Matches(o.Vertical, vertical) && o.Stage.Trim().Equals(stage.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return From(exact, ServiceOwnerResolution.ExactStage);

        var fallback = owners.FirstOrDefault(o => Matches(o.Vertical, vertical) && o.Stage.Trim() == ServiceOwner.AnyStage);
        if (fallback is not null) return From(fallback, ServiceOwnerResolution.VerticalFallback);

        return ResolvedServiceOwner.Unresolved(ServiceOwnerResolution.Unresolved);
    }

    public void Invalidate() => _snapshot = null;

    /// <summary>Null means "could not read", which is not the same as an empty map.</summary>
    private async Task<IReadOnlyList<ServiceOwner>?> GetAllAsync(CancellationToken ct)
    {
        var cached = _snapshot;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Owners;

        try
        {
            var rows = await store.GetAllAsync(ct);
            _snapshot = new Snapshot(rows, DateTimeOffset.UtcNow.Add(CacheTtl));
            return rows;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The service ownership map could not be read; owners will be reported as unavailable.");
            // Not cached: a stale answer would outlive the outage it came from.
            return cached?.Owners;
        }
    }

    /// <summary>The board writes "Hotels" where configuration says "hotels", and sometimes the singular.</summary>
    private static bool Matches(string a, string b) =>
        a.Trim().ToLowerInvariant().TrimEnd('s') == b.Trim().ToLowerInvariant().TrimEnd('s');

    private static ResolvedServiceOwner From(ServiceOwner owner, string resolvedBy) =>
        new(true, resolvedBy, owner.OwnerTeam, owner.PrimaryResponder, owner.Backup, owner.ContactRef);

    private sealed record Snapshot(IReadOnlyList<ServiceOwner> Owners, DateTimeOffset ExpiresAt);
}
