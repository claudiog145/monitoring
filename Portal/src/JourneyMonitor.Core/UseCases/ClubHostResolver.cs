using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Resolves a club id to the host that v6 needs (the real KQL discriminator), the way
/// query-appinsights.js resolveClubHost does: known/demo clubs skip discovery entirely;
/// then the brand.Information Url for that Functions host's database is used. Staging reads the
/// QA database, whose hotel rows hold the UAT hosts. Otherwise a small discovery KQL is run
/// (and cached) to find it.
/// </summary>
public sealed class ClubHostResolver(
    ITelemetrySource telemetrySource,
    ITelemetryQueryCache cache,
    IOptions<InsightsOptions> insightsOptions,
    IBrandCatalog brandCatalog)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    public async Task<string?> ResolveAsync(string win, string clubId, string domain, string environment, bool skipCache, CancellationToken ct)
    {
        IReadOnlyList<DemoClub> demo = (domain, environment) switch
        {
            ("cruises", InsightsEnvironment.Staging) => ClubFilter.DemoCruiseStagingClubs,
            ("cruises", _) => ClubFilter.DemoCruiseClubs,
            ("cars", InsightsEnvironment.Staging) => ClubFilter.DemoCarStagingClubs,
            ("cars", _) => ClubFilter.DemoCarClubs,
            (_, InsightsEnvironment.Staging) => ClubFilter.DemoHotelStagingClubs,
            _ => ClubFilter.DemoHotelClubs,
        };
        var known = demo.FirstOrDefault(c => c.ClubId == clubId);
        if (known is not null && !string.IsNullOrEmpty(known.Host)) return known.Host;

        if (brandCatalog.IsConfigured)
        {
            try
            {
                var host = BrandClubOptions.HostForDomain(await brandCatalog.GetAllAsync(ct), domain, clubId);
                if (!string.IsNullOrEmpty(host)) return host;
            }
            catch
            {
                // Brand catalog unavailable; fall through to live discovery below.
            }
        }

        try
        {
            var clubs = await GetClubsAsync(win, domain, environment, skipCache, ct);
            var host = ClubFilter.ResolveHostFromClubs(clubs.Clubs.Select(c => new ClubOptionDto(c.ClubId, c.Host, c.Value, c.Label, c.Events)), clubId);
            if (!string.IsNullOrEmpty(host)) return host;
        }
        catch
        {
            // Host resolution failure falls through to the caller's own default host, same as query-appinsights.js.
        }
        return null;
    }

    /// <summary>
    /// ICE partner id for the selected dropdown value. Air/Cruises already send the partner id as
    /// the club value. Hotels/Cars look it up on the matching brand.Information row.
    /// </summary>
    public async Task<string?> ResolvePartnerAsync(string clubId, string domain, CancellationToken ct)
    {
        if (BrandClubOptions.UsesPartnerId(domain))
        {
            return ClubFilter.IsValidClubId(clubId) ? clubId : null;
        }

        if (!brandCatalog.IsConfigured) return null;
        try
        {
            return BrandClubOptions.PartnerFor(await brandCatalog.GetAllAsync(ct), clubId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ClubsResult> GetClubsAsync(string win, string domain, string environment, bool skipCache, CancellationToken ct)
    {
        var key = $"clubs:{domain}:{environment}:{win}";
        if (!skipCache)
        {
            var hit = await cache.GetAsync(key, ct);
            if (hit is not null)
            {
                var cached = JsonSerializer.Deserialize<ClubsResult>(hit.PayloadJson)!;
                cached.Cached = true;
                return cached;
            }
        }

        var source = InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, domain, environment);
        var kql = domain == "air" ? Filtering.ClubDiscoveryKql.AirClubsDiscoveryKql(win) : Filtering.ClubDiscoveryKql.ClubsDiscoveryKql(win, domain);
        var rows = await telemetrySource.QueryAsync(new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);
        var clubs = rows
            .Select(r => ClubFilter.FormatClubOption(new DemoClub(r.GetString("Club"), r.GetString("Host"), r.GetInt("Events"))))
            .Where(c => !string.IsNullOrEmpty(c.ClubId) && (domain == "air" || !string.IsNullOrEmpty(c.Host)))
            .ToList();

        var result = new ClubsResult
        {
            Source = "live",
            Win = win,
            Domain = domain,
            Environment = environment,
            FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
            Clubs = clubs.Select(c => new ClubOption { ClubId = c.ClubId, Host = c.Host, Value = c.Value, Label = c.Label, Events = c.Events }).ToList(),
        };
        await cache.SetAsync(key, JsonSerializer.Serialize(result), CacheTtl, ct);
        return result;
    }
}
