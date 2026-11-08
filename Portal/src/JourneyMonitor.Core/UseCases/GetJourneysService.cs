using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Mapping;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

public sealed record JourneysRequest(
    string Domain,
    string? Win,
    string? ClubId,
    string? Environment,
    string? RowCapRaw,
    bool Fresh,
    string? ForcedMode,
    string? Host = null,
    string? PartnerId = null);

public sealed record JourneysOutcome(JourneysResult Result, int StatusCode);

/// <summary>
/// One live query for every club in a vertical. <see cref="RowsByClub"/> keys are the club id
/// (hotels) or ICE partner id (cruises). Clubs absent from the dictionary had no rows.
/// <see cref="ClubsFromTelemetry"/> is true for the demo telemetry: its keys are the club ids the
/// telemetry carries (the ones the Club filter lists), not brand-catalog ids, so the sync stores
/// every key instead of walking the catalog.
/// </summary>
public sealed record VerticalJourneysOutcome(
    int StatusCode,
    string? Error,
    string Window,
    string Environment,
    IReadOnlyDictionary<string, IReadOnlyList<TelemetryJourneyRow>> RowsByClub,
    bool ClubsFromTelemetry = false);

/// <summary>
/// Use case behind GET /api/journeys. Direct port of handleJourneysApi in server.js,
/// including the live -&gt; snapshot -&gt; demo fallback chain (Hotels/club 15639 only) and the
/// "no offline data" hard failure for every other domain/club combination.
/// </summary>
public sealed class GetJourneysService(
    ITelemetrySource telemetrySource,
    ITelemetryQueryCache cache,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IDemoJourneyFixtureProvider demoProvider,
    ClubHostResolver clubHostResolver,
    IOptions<JourneySyncOptions> syncOptions)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);
    private static readonly string[] ValidDomains = ["hotels", "cars", "cruises", "air"];

    public async Task<JourneysOutcome> GetAsync(JourneysRequest request, CancellationToken ct)
    {
        var domain = request.Domain.ToLowerInvariant();
        var win = request.Win ?? (ClubFilter.UsesPortalTimeRange(domain) ? "15m" : "2h");
        var rowCap = syncOptions.Value.NormalizeRequestedRowCap(request.RowCapRaw);
        var clubId = request.ClubId ?? (domain is "air" or "cruises" ? "308" : "15639");

        string environment;
        try
        {
            environment = InsightsEnvironment.Normalize(request.Environment);
        }
        catch (InsightsConfigurationException ex)
        {
            return Error(400, win, domain, clubId, null, ex.Message);
        }

        if (!ClubFilter.IsValidClubId(clubId))
        {
            return Error(400, win, domain, clubId, environment, "club must be a numeric id");
        }
        if (!string.IsNullOrWhiteSpace(request.PartnerId) && !ClubFilter.IsValidClubId(request.PartnerId))
        {
            return Error(400, win, domain, clubId, environment, "partner must be a numeric id");
        }
        if (!string.IsNullOrWhiteSpace(request.Host) && !ClubFilter.IsValidHost(request.Host))
        {
            return Error(400, win, domain, clubId, environment, "host must be a plain hostname");
        }
        if (!ValidDomains.Contains(domain))
        {
            return Error(400, win, domain, clubId, environment, "domain must be hotels, cars, cruises or air");
        }

        if (domain == "air") return await HandleAirAsync(win, clubId, environment, rowCap, request, ct);
        return await HandleHostFamilyAsync(domain, win, clubId, environment, rowCap, request, ct);
    }

    /// <summary>
    /// Timer path: one KQL call for the vertical, rows split by club. The portal refresh keeps
    /// <see cref="GetAsync"/>, which still queries a single club.
    /// </summary>
    public async Task<VerticalJourneysOutcome> QueryVerticalAsync(
        string domain,
        string window,
        string environment,
        int rowCap,
        IReadOnlyList<ClubOption> clubs,
        CancellationToken ct)
    {
        var normalized = domain.Trim().ToLowerInvariant();
        InsightsSource source;
        try
        {
            source = InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, normalized, environment);
        }
        catch (InsightsConfigurationException ex)
        {
            return new VerticalJourneysOutcome(400, ex.Message, window, environment, new Dictionary<string, IReadOnlyList<TelemetryJourneyRow>>());
        }

        var clubIds = clubs.Select(c => c.ClubId).Where(ClubFilter.IsValidClubId).Distinct(StringComparer.Ordinal).ToList();
        var hosts = clubs.Select(c => c.Host).Where(ClubFilter.IsValidHost).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var partners = clubs.Select(c => c.PartnerId).Where(ClubFilter.IsValidClubId).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            var matchHostDomain = normalized == "hotels"
                && InsightsEnvironment.Normalize(environment) == InsightsEnvironment.Staging;
            var demo = UsesDemoTelemetry(insightsOptions.Value);
            var kql = demo
                ? InjectDemoVerticalJourneyKql(kqlProvider.GetByKey(DemoJourneyKey(normalized)), window, rowCap)
                : ClubFilter.InjectVerticalJourneyKql(
                    normalized,
                    kqlProvider.GetJourneyKql(normalized),
                    window,
                    clubIds,
                    hosts,
                    partners,
                    source.ClubResource,
                    source.CoreResource,
                    rowCap,
                    environment);
            var rawRows = await telemetrySource.QueryAsync(new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);
            var grouped = GroupByClub(rawRows, clubIds, matchHostDomain || demo);
            if (normalized == "hotels")
            {
                foreach (var clubId in grouped.Keys.ToList())
                {
                    grouped[clubId] = HotelVisitMerger.Merge(grouped[clubId]);
                }
            }

            return new VerticalJourneysOutcome(200, null, window, environment, grouped, demo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VerticalJourneysOutcome(502, ex.Message, window, environment, new Dictionary<string, IReadOnlyList<TelemetryJourneyRow>>());
        }
    }

    /// <summary>Built-in key of the demo-shaped journey query for a vertical (hotels, cars, cruises).</summary>
    public static string DemoJourneyKey(string domain) => $"journey-demo:{domain.Trim().ToLowerInvariant()}";

    /// <summary>
    /// True when App Insights queries go to the demo stack's fake endpoint (Insights:QueryEndpoint set
    /// to anything but the public API). The demo packs do not emit the production telemetry shape
    /// the legacy journey queries filter on (T-Mobile hosts, catalog club ids,
    /// customDimensions.UserId/CorrelationId), so the legacy text returns no rows there.
    /// </summary>
    public static bool UsesDemoTelemetry(InsightsOptions options) =>
        !string.IsNullOrWhiteSpace(options.QueryEndpoint)
        && !string.Equals(
            options.QueryEndpoint.Trim().TrimEnd('/'),
            InsightsOptions.DefaultQueryEndpoint,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Fills win and the per-club row cap into a demo journey query.</summary>
    public static string InjectDemoVerticalJourneyKql(string raw, string window, int rowCap)
    {
        if (!ClubFilter.IsValidWin(window) && !ClubFilter.IsValidCruiseWin(window)) throw new ClubFilterException("win must look like 2h, 15m or 7d");

        var text = ReplaceOnce(raw, @"let win = \w+;", $"let win = {window};");
        return ReplaceOnce(text, @"let rowCap = \d+;", $"let rowCap = {(rowCap > 0 ? rowCap : ClubFilter.FilterRowCap)};").Trim();
    }

    private static string ReplaceOnce(string text, string pattern, string replacement)
    {
        var regex = new System.Text.RegularExpressions.Regex(pattern);
        if (!regex.IsMatch(text)) throw new ClubFilterException($"demo journey query has no '{pattern}' to fill");
        return regex.Replace(text, replacement, 1);
    }

    private static Dictionary<string, IReadOnlyList<TelemetryJourneyRow>> GroupByClub(
        IReadOnlyList<TelemetryJourneyRow> rows,
        IReadOnlyList<string> clubIds,
        bool allowUnlistedClubs = false)
    {
        var allowed = new HashSet<string>(clubIds, StringComparer.Ordinal);
        var grouped = new Dictionary<string, List<TelemetryJourneyRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var clubId = row.GetString("ClubId");
            if (!ClubFilter.IsValidClubId(clubId)) continue;
            if (!allowUnlistedClubs && !allowed.Contains(clubId)) continue;
            if (!grouped.TryGetValue(clubId, out var list))
            {
                list = [];
                grouped[clubId] = list;
            }

            var copy = new TelemetryJourneyRow();
            foreach (var pair in row.Extra)
            {
                if (pair.Key.Equals("ClubId", StringComparison.OrdinalIgnoreCase)) continue;
                copy.Extra[pair.Key] = pair.Value;
            }
            list.Add(copy);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TelemetryJourneyRow>)pair.Value, StringComparer.Ordinal);
    }

    private async Task<JourneysOutcome> HandleAirAsync(string win, string clubId, string environment, int rowCap, JourneysRequest request, CancellationToken ct)
    {
        if (request.ForcedMode == "demo")
        {
            return Error(502, win, "air", clubId, environment,
                "Air has no anonymized demo fixture yet. Use live with az login --tenant <Air tenant>.");
        }
        try
        {
            var source = InsightsSourceResolver.Resolve(insightsOptions.Value, "air", environment);
            var key = $"journeys:air:{environment}:{win}:{rowCap}:{clubId}:{source.AppInsightsAppId}";
            if (!request.Fresh)
            {
                var hit = await cache.GetAsync(key, ct);
                if (hit is not null)
                {
                    var cached = JsonSerializer.Deserialize<JourneysResult>(hit.PayloadJson)!;
                    cached.Cached = true;
                    return new JourneysOutcome(cached, 200);
                }
            }

            var partnerId = await ResolvePartnerIdAsync(clubId, "air", request, ct);
            var kql = ClubFilter.InjectAirJourneyKql(kqlProvider.GetAirJourneyKql(), win, clubId, source.UiResource, source.ApiResource, rowCap, partnerId);
            kql = await sourceResolver.ResolveAsync(kql, environment, ct);
            var rawRows = await telemetrySource.QueryAsync(new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);

            // Rows are returned RAW (not mapped) - map-air.js maps them client-side, exactly like
            // the original app. See the note on TelemetryJourneyRow/JourneysResult.
            var result = new JourneysResult
            {
                Source = "live",
                Win = win,
                Domain = "air",
                Environment = environment,
                ClubId = clubId,
                Host = "",
                RowCap = rowCap,
                FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
                RowCount = rawRows.Count,
                Rows = rawRows.ToList(),
            };
            await cache.SetAsync(key, JsonSerializer.Serialize(result), CacheTtl, ct);
            return new JourneysOutcome(result, 200);
        }
        catch (ClubFilterException ex)
        {
            return Error(400, win, "air", clubId, environment, ex.Message);
        }
        catch (InsightsConfigurationException ex)
        {
            return Error(400, win, "air", clubId, environment, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(502, win, "air", clubId, environment, $"Live Air query failed: {ex.Message}");
        }
    }

    private async Task<JourneysOutcome> HandleHostFamilyAsync(string domain, string win, string clubId, string environment, int rowCap, JourneysRequest request, CancellationToken ct)
    {
        if (request.ForcedMode != "demo")
        {
            try
            {
                return await QueryLiveAsync(domain, win, clubId, environment, rowCap, request, ct);
            }
            catch (ClubFilterException ex)
            {
                return Error(400, win, domain, clubId, environment, ex.Message);
            }
            catch (InsightsConfigurationException ex)
            {
                return Error(400, win, domain, clubId, environment, ex.Message);
            }
            catch (Exception ex)
            {
                // Hotel snapshot/fixture is T-Mobile Hotels only; Cars/Cruises have no offline dump.
                if (domain != "hotels" || clubId != "15639")
                {
                    return Error(502, win, domain, clubId, environment,
                        $"Live query failed and no offline data exists for {domain} club {clubId}: {ex.Message}");
                }
                if (demoProvider.HasSnapshot)
                {
                    try
                    {
                        var snapshot = await demoProvider.LoadSnapshotAsync(win, ct);
                        snapshot.Warning = $"{snapshot.Warning} ({ex.Message})";
                        snapshot.ClubId = clubId;
                        snapshot.Domain = "hotels";
                        return new JourneysOutcome(snapshot, 200);
                    }
                    catch
                    {
                        // Snapshot unreadable; fall through to the demo fixture, matching server.js.
                    }
                }
            }
        }

        if (domain != "hotels")
        {
            return Error(502, win, domain, clubId, environment, $"No demo fixture for domain {domain}. Run az login for live journeys.");
        }

        try
        {
            var demo = await demoProvider.LoadDemoAsync(win, ct);
            demo.ClubId = "15639";
            demo.Domain = "hotels";
            return new JourneysOutcome(demo, 200);
        }
        catch (Exception ex)
        {
            return Error(500, win, domain, clubId, environment, $"No data available: {ex.Message}");
        }
    }

    private async Task<JourneysOutcome> QueryLiveAsync(string domain, string win, string clubId, string environment, int rowCap, JourneysRequest request, CancellationToken ct)
    {
        var source = InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, domain, environment);
        var host = await ResolveHostAsync(domain, win, clubId, environment, request.Fresh, request, ct);
        var partnerId = await ResolvePartnerIdAsync(clubId, domain, request, ct);

        var key = $"journeys:{domain}:{environment}:{win}:{rowCap}:{clubId}:{partnerId}:{host}";
        if (!request.Fresh)
        {
            var hit = await cache.GetAsync(key, ct);
            if (hit is not null)
            {
                var cached = JsonSerializer.Deserialize<JourneysResult>(hit.PayloadJson)!;
                cached.Cached = true;
                return new JourneysOutcome(cached, 200);
            }
        }

        var kql = ClubFilter.InjectJourneyKql(
            kqlProvider.GetJourneyKql(domain), win, clubId, host, source.ClubResource, source.CoreResource, rowCap, partnerId, environment);
        kql = await sourceResolver.ResolveAsync(kql, source.Environment, ct);
        var rawRows = await telemetrySource.QueryAsync(new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);
        // Hotel visit folding runs here, on the returned rows, so the KQL stays one session per
        // row and finishes inside the ingest timeout. Other domains are unchanged. The browser
        // still maps the raw row shape.
        var rows = domain == "hotels" ? HotelVisitMerger.Merge(rawRows) : rawRows.ToList();

        var result = new JourneysResult
        {
            Source = "live",
            Win = win,
            Domain = domain,
            Environment = environment,
            ClubId = clubId,
            Host = host,
            RowCap = rowCap,
            FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
            RowCount = rows.Count,
            Rows = rows,
        };
        await cache.SetAsync(key, JsonSerializer.Serialize(result), CacheTtl, ct);
        return new JourneysOutcome(result, 200);
    }

    /// <summary>
    /// Host from the selected club. Known demo hosts (including Cruise's cruise host, which is not
    /// the brand Url) win. The dropdown host is used when nothing else is known. The configured
    /// default host is not a fallback, so a different club cannot silently search T-Mobile.
    /// </summary>
    private async Task<string> ResolveHostAsync(
        string domain, string win, string clubId, string environment, bool fresh, JourneysRequest request, CancellationToken ct)
    {
        var host = await clubHostResolver.ResolveAsync(win, clubId, domain, environment, fresh, ct) ?? "";
        if (string.IsNullOrEmpty(host) && ClubFilter.IsValidHost(request.Host)) host = request.Host!;
        if (string.IsNullOrEmpty(host))
        {
            throw new InvalidOperationException($"No host known for club {clubId}; it had no {domain} traffic in the last {win}.");
        }
        return host;
    }

    private async Task<string?> ResolvePartnerIdAsync(string clubId, string domain, JourneysRequest request, CancellationToken ct)
    {
        var resolved = await clubHostResolver.ResolvePartnerAsync(clubId, domain, ct);
        if (ClubFilter.IsValidClubId(resolved)) return resolved;
        return ClubFilter.IsValidClubId(request.PartnerId) ? request.PartnerId : null;
    }

    private static JourneysOutcome Error(int statusCode, string win, string domain, string? clubId, string? environment, string message) =>
        new(new JourneysResult { Source = "error", Win = win, Domain = domain, ClubId = clubId, Environment = environment, Error = message }, statusCode);
}
