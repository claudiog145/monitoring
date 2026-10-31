using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Maps the board's Entity key to a brand.ClubInventory row.
///
/// Reads the full 368-club application map instead of the much smaller brand.Information table:
/// measured against brand.Information only ~10% of entities resolved (see docs/sessions -
/// 2026-10-15 club-name mapping analysis), because that table is a hand-maintained Club dropdown
/// seed, not the inventory of every club actually observed. brand.Information stays the source for
/// the Club dropdown and for Journey/Incident club resolution (deliberately unchanged here) - this
/// resolver is the one consumer that needed the fuller catalogue.
///
/// Two things make this non-obvious and both are load-bearing:
/// (1) Cruise telemetry carries no VacationClubId - its ICE partner id IS the club - so
///     "club:140" has to be tried against IcePartnerId as well as ClubId, or Cruise never
///     resolves at all;
/// (2) the same brand appears under different hosts than the catalog records (the catalog has
///     travel.mysynchronytravel.com, the board emits travel.synchronytravel.com), so an exact
///     host match alone is not enough and a base-domain fallback is needed.
///
/// Even with all four strategies the catalog only covers a fraction of the clubs that actually
/// appear - which is why an unresolved entity is returned explicitly rather than guessed.
/// Never throws: an unavailable catalog degrades to unresolved, same as every other Monitoring
/// component in this repo.
/// </summary>
public sealed class ClubIdentityResolver(IClubInventoryStore clubInventoryStore, ILogger<ClubIdentityResolver> logger) : IClubIdentityResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private IReadOnlyList<ClubInventoryRow>? _cache;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public async Task<ClubIdentity> ResolveAsync(string entityKey, CancellationToken ct = default)
    {
        var key = entityKey ?? "";
        var (kind, value) = Split(key);
        if (kind == ClubEntityKind.Unknown)
        {
            return new ClubIdentity(key, kind, null, null, null, null, ClubResolution.Unresolved);
        }

        var catalog = await GetCatalogAsync(ct);
        if (catalog.Count == 0)
        {
            return new ClubIdentity(key, kind, kind == ClubEntityKind.Club ? value : null, null, null, null, ClubResolution.Unresolved);
        }

        return kind == ClubEntityKind.Club
            ? ResolveClub(key, value, catalog)
            : ResolveHost(key, value, catalog);
    }

    private static ClubIdentity ResolveClub(string key, string value, IReadOnlyList<ClubInventoryRow> catalog)
    {
        var byClubId = catalog.FirstOrDefault(c => string.Equals(c.ClubId.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (byClubId is not null)
        {
            return new ClubIdentity(key, ClubEntityKind.Club, byClubId.ClubId.ToString(), byClubId.IcePartnerId, byClubId.BrandName, byClubId.ClubName, ClubResolution.ClubIdMatch);
        }

        var byPartner = catalog.FirstOrDefault(c => string.Equals(c.IcePartnerId, value, StringComparison.OrdinalIgnoreCase));
        if (byPartner is not null)
        {
            return new ClubIdentity(key, ClubEntityKind.Club, byPartner.ClubId.ToString(), byPartner.IcePartnerId, byPartner.BrandName, byPartner.ClubName, ClubResolution.IcePartnerIdMatch);
        }

        return new ClubIdentity(key, ClubEntityKind.Club, value, null, null, null, ClubResolution.Unresolved);
    }

    private static ClubIdentity ResolveHost(string key, string value, IReadOnlyList<ClubInventoryRow> catalog)
    {
        var exact = catalog.FirstOrDefault(c => string.Equals(CatalogHost(c.Site), value, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new ClubIdentity(key, ClubEntityKind.Host, exact.ClubId.ToString(), exact.IcePartnerId, exact.BrandName, exact.ClubName, ClubResolution.HostExactMatch);
        }

        var baseDomain = BaseDomain(value);
        if (baseDomain.Length > 0)
        {
            var byDomain = catalog.FirstOrDefault(c =>
                CatalogHost(c.Site) is { Length: > 0 } site &&
                (string.Equals(site, baseDomain, StringComparison.OrdinalIgnoreCase) ||
                 site.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase)));
            if (byDomain is not null)
            {
                return new ClubIdentity(key, ClubEntityKind.Host, byDomain.ClubId.ToString(), byDomain.IcePartnerId, byDomain.BrandName, byDomain.ClubName, ClubResolution.HostBaseDomainMatch);
            }
        }

        return new ClubIdentity(key, ClubEntityKind.Host, null, null, null, null, ClubResolution.Unresolved);
    }

    /// <summary>
    /// The catalogue is hand-maintained, so Site is not guaranteed to be a bare hostname. Measured
    /// over the 1,370 distinct values in brand.ClubInventory: none carries a scheme or a port, but
    /// 8 end in a trailing slash. Those match neither test above - "example.com/" is not equal to
    /// "example.com", and EndsWith(".example.com") is false for it too - so the club silently never
    /// resolves by host. Trimming here rather than in the store keeps the catalogue row as it was
    /// entered while still letting it join.
    /// </summary>
    private static string CatalogHost(string? site) => site is null ? "" : site.Trim().TrimEnd('/');

    private static (string Kind, string Value) Split(string entityKey)
    {
        var idx = entityKey.IndexOf(':');
        if (idx <= 0 || idx == entityKey.Length - 1) return (ClubEntityKind.Unknown, "");

        var kind = entityKey[..idx].Trim().ToLowerInvariant();
        var value = entityKey[(idx + 1)..].Trim();
        return kind switch
        {
            ClubEntityKind.Club => (ClubEntityKind.Club, value),
            ClubEntityKind.Host => (ClubEntityKind.Host, value),
            _ => (ClubEntityKind.Unknown, ""),
        };
    }

    /// <summary>"members.foo.co.uk" -> "foo.co.uk" is wrong, but two-label is right for every host in this catalog; keep it simple and let the exact match carry the rest.</summary>
    private static string BaseDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Join('.', parts[^2..]) : "";
    }

    private async Task<IReadOnlyList<ClubInventoryRow>> GetCatalogAsync(CancellationToken ct)
    {
        if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry) return _cache;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry) return _cache;

            if (!clubInventoryStore.IsConfigured)
            {
                _cache = Array.Empty<ClubInventoryRow>();
                _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheTtl);
                return _cache;
            }

            _cache = await clubInventoryStore.GetAllAsync(ct);
            _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheTtl);
            return _cache;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[ScoutMonkey] brand.ClubInventory could not be read - club identities will be reported as unresolved.");
            _cache = Array.Empty<ClubInventoryRow>();
            _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheTtl);
            return _cache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}

