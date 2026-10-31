using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Outcome of one domain's sync pass. <see cref="SkipReason"/> is null when stored.
/// <see cref="SourceStatusCode"/> carries the upstream query's status so an HTTP caller can tell a
/// bad request apart from an upstream failure; it is null when the pass never reached the query.
/// </summary>
public sealed record JourneySyncPass(
    string Domain,
    bool Stored,
    int RecordCount,
    Guid? QueryRunId,
    string? SkipReason,
    int? SourceStatusCode = null,
    int? Inserted = null,
    int? Updated = null,
    int? Unchanged = null,
    IReadOnlyList<DashboardMetricSyncPass>? DashboardMetrics = null,
    string? ClubId = null);

/// <summary>
/// What a single pass queries. The timer builds these from configuration; the refresh endpoint
/// builds them from the request so the screen's Environment and Time Range selectors are honored
/// instead of silently re-querying the timer's defaults.
/// </summary>
public sealed record JourneySyncScope(
    string Domain,
    string Window,
    string Environment,
    string ClubId,
    string? RowCap,
    string? Host = null,
    string? PartnerId = null);

/// <summary>
/// Persists the current journeys into the per-vertical SQL schemas. Reuses
/// <see cref="GetJourneysService"/> rather than re-issuing the KQL so there is a single query
/// path (club/host resolution, KQL injection and row shape stay identical to the live endpoint).
/// Shared by the per-vertical timer triggers and the refresh endpoint.
/// Each timer syncs its vertical in one KQL call (hotels and cruises), then upserts per club.
/// Hotels and cars use ClubId; air and cruises use IcePartnerId, matching
/// <see cref="BrandClubOptions"/>.
/// </summary>
public sealed class SyncJourneysService(
    GetJourneysService getJourneysService,
    IJourneyStore journeyStore,
    SyncDashboardMetricsService dashboardMetrics,
    IBrandCatalog brandCatalog,
    IOptions<JourneySyncOptions> syncOptions)
{
    public async Task<IReadOnlyList<JourneySyncPass>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var passes = new List<JourneySyncPass>();
        var brands = await LoadBrandsAsync(cancellationToken);
        foreach (var domain in syncOptions.Value.EnumerateDomains())
        {
            passes.AddRange(await SyncConfiguredDomainAsync(domain, brands, cancellationToken));
        }

        return passes;
    }

    /// <summary>
    /// Syncs one vertical. The hotel and cruise timers call this so a long hotel club loop
    /// cannot hold the cruise pass. A domain absent from <c>JourneySync:Domains</c> is skipped.
    /// </summary>
    public async Task<IReadOnlyList<JourneySyncPass>> SyncDomainAsync(string domain, CancellationToken cancellationToken)
    {
        var normalized = domain.Trim().ToLowerInvariant();
        if (!syncOptions.Value.EnumerateDomains().Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return [new JourneySyncPass(normalized, false, 0, null, $"JourneySync:Domains does not include '{normalized}'.")];
        }

        return await SyncConfiguredDomainAsync(normalized, await LoadBrandsAsync(cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<JourneySyncPass>> SyncConfiguredDomainAsync(
        string domain,
        (IReadOnlyList<BrandClub> Rows, string? SkipReason) brands,
        CancellationToken cancellationToken)
    {
        var passes = new List<JourneySyncPass>();
        if (brands.SkipReason is not null)
        {
            passes.Add(new JourneySyncPass(domain, false, 0, null, brands.SkipReason));
            passes.Add(await SyncOneAsync(ScopeFor(domain), cancellationToken));
            return passes;
        }

        var clubs = BrandClubOptions.ForDomain(brands.Rows, domain);
        if (clubs.Count == 0)
        {
            passes.Add(new JourneySyncPass(domain, false, 0, null, "brand.Information has no clubs for this domain."));
            passes.Add(await SyncOneAsync(ScopeFor(domain), cancellationToken));
            return passes;
        }

        if (domain is "hotels" or "cruises")
        {
            return await SyncVerticalAsync(domain, clubs, cancellationToken);
        }

        foreach (var club in clubs)
        {
            var scope = ScopeFor(domain) with
            {
                ClubId = club.ClubId,
                Host = string.IsNullOrEmpty(club.Host) ? null : club.Host,
                PartnerId = string.IsNullOrEmpty(club.PartnerId) ? null : club.PartnerId,
            };
            passes.Add(await SyncOneAsync(scope, cancellationToken));
        }

        return passes;
    }

    private async Task<IReadOnlyList<JourneySyncPass>> SyncVerticalAsync(
        string domain,
        IReadOnlyList<ClubOption> clubs,
        CancellationToken cancellationToken)
    {
        var scope = ScopeFor(domain);
        var rowCap = syncOptions.Value.NormalizeRequestedRowCap(scope.RowCap);
        var scopedClubs = ClubFilter.WithEnvironmentHosts(domain, scope.Environment, clubs);
        if (IsStagingHotels(domain, scope.Environment))
        {
            return await SyncStagingHotelsByHostAsync(
                domain, scope, rowCap,
                scopedClubs.Where(club => BrandClubOptions.IsUatHotelHost(club.Host)).ToList(),
                cancellationToken);
        }

        var journeys = await getJourneysService.QueryVerticalAsync(
            domain, scope.Window, scope.Environment, rowCap, scopedClubs, cancellationToken);
        var dashboards = await dashboardMetrics.SyncVerticalAsync(
            domain, scope.Window, scope.Environment, scopedClubs, cancellationToken);

        var passes = new List<JourneySyncPass>();
        foreach (var club in scopedClubs)
        {
            dashboards.TryGetValue(club.ClubId, out var metrics);
            if (journeys.StatusCode != 200)
            {
                passes.Add(new JourneySyncPass(
                    domain, false, 0, null, journeys.Error, journeys.StatusCode, DashboardMetrics: metrics, ClubId: club.ClubId));
                continue;
            }

            if (!journeys.RowsByClub.TryGetValue(club.ClubId, out var rows) || rows.Count == 0)
            {
                passes.Add(new JourneySyncPass(domain, false, 0, null, "query returned no rows", DashboardMetrics: metrics, ClubId: club.ClubId));
                continue;
            }

            try
            {
                var result = await journeyStore.UpsertAsync(
                    new JourneyUpsertRequest(
                        Domain: domain,
                        Window: journeys.Window,
                        Environment: journeys.Environment,
                        ClubId: club.ClubId,
                        Host: string.IsNullOrEmpty(club.Host) ? null : club.Host,
                        Source: "live",
                        Rows: rows),
                    cancellationToken);
                passes.Add(new JourneySyncPass(
                    domain,
                    true,
                    result.RecordCount,
                    result.QueryRunId,
                    null,
                    SourceStatusCode: journeys.StatusCode,
                    Inserted: result.Inserted,
                    Updated: result.Updated,
                    Unchanged: result.Unchanged,
                    DashboardMetrics: metrics,
                    ClubId: club.ClubId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                passes.Add(new JourneySyncPass(domain, false, 0, null, ex.Message, DashboardMetrics: metrics, ClubId: club.ClubId));
            }
        }

        return passes;
    }

    /// <summary>
    /// One query per seeded host. Every catalog club on that host stores the rows, so the
    /// dropdown id does not have to match the club id on the UAT call.
    /// </summary>
    private async Task<IReadOnlyList<JourneySyncPass>> SyncStagingHotelsByHostAsync(
        string domain,
        JourneySyncScope scope,
        int rowCap,
        IReadOnlyList<ClubOption> clubs,
        CancellationToken cancellationToken)
    {
        var dashboards = await dashboardMetrics.SyncVerticalAsync(
            domain, scope.Window, scope.Environment, clubs, cancellationToken);
        var passes = new List<JourneySyncPass>();
        foreach (var group in clubs.GroupBy(club => club.Host ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var members = group.Take(1).ToList();
            if (!ClubFilter.IsValidHost(group.Key))
            {
                foreach (var club in members)
                {
                    dashboards.TryGetValue(club.ClubId, out var missingHostMetrics);
                    passes.Add(new JourneySyncPass(domain, false, 0, null, "query returned no rows", DashboardMetrics: missingHostMetrics, ClubId: club.ClubId));
                }
                continue;
            }

            var journeys = await getJourneysService.QueryVerticalAsync(
                domain, scope.Window, scope.Environment, rowCap, members, cancellationToken);
            var rows = journeys.StatusCode == 200
                ? journeys.RowsByClub.SelectMany(pair => pair.Value).ToList()
                : [];
            foreach (var club in members)
            {
                dashboards.TryGetValue(club.ClubId, out var metrics);
                if (journeys.StatusCode != 200)
                {
                    passes.Add(new JourneySyncPass(
                        domain, false, 0, null, journeys.Error, journeys.StatusCode, DashboardMetrics: metrics, ClubId: club.ClubId));
                    continue;
                }

                if (rows.Count == 0)
                {
                    passes.Add(new JourneySyncPass(domain, false, 0, null, "query returned no rows", DashboardMetrics: metrics, ClubId: club.ClubId));
                    continue;
                }

                try
                {
                    var result = await journeyStore.UpsertAsync(
                        new JourneyUpsertRequest(
                            Domain: domain,
                            Window: journeys.Window,
                            Environment: journeys.Environment,
                            ClubId: club.ClubId,
                            Host: club.Host,
                            Source: "live",
                            Rows: rows),
                        cancellationToken);
                    passes.Add(new JourneySyncPass(
                        domain,
                        true,
                        result.RecordCount,
                        result.QueryRunId,
                        null,
                        SourceStatusCode: journeys.StatusCode,
                        Inserted: result.Inserted,
                        Updated: result.Updated,
                        Unchanged: result.Unchanged,
                        DashboardMetrics: metrics,
                        ClubId: club.ClubId));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    passes.Add(new JourneySyncPass(domain, false, 0, null, ex.Message, DashboardMetrics: metrics, ClubId: club.ClubId));
                }
            }
        }

        return passes;
    }

    private static bool IsStagingHotels(string domain, string? environment)
    {
        try
        {
            return string.Equals(domain, "hotels", StringComparison.Ordinal)
                && InsightsEnvironment.Normalize(environment) == InsightsEnvironment.Staging;
        }
        catch (InsightsConfigurationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Configured defaults for a domain. The timer uses this only when <c>brand.Information</c>
    /// cannot be read; a refresh request overrides the club the caller selected.
    /// </summary>
    public JourneySyncScope ScopeFor(string domain)
    {
        var options = syncOptions.Value;
        var clubId = domain.ToLowerInvariant() is "air" or "cruises" ? "308" : options.ClubId;
        return new JourneySyncScope(
            domain, options.Window, options.Environment, clubId, options.FilteredRowCap.ToString());
    }

    private async Task<(IReadOnlyList<BrandClub> Rows, string? SkipReason)> LoadBrandsAsync(
        CancellationToken cancellationToken)
    {
        if (!brandCatalog.IsConfigured)
        {
            return ([], "brand.Information is not configured.");
        }

        try
        {
            return (await brandCatalog.GetAllAsync(cancellationToken), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], $"brand.Information read failed: {ex.Message}");
        }
    }

    private async Task<JourneySyncPass> SyncOneAsync(JourneySyncScope scope, CancellationToken cancellationToken)
    {
        try
        {
            return await SyncAsync(scope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JourneySyncPass(scope.Domain, false, 0, null, ex.Message, ClubId: scope.ClubId);
        }
    }

    public async Task<JourneySyncPass> SyncAsync(JourneySyncScope scope, CancellationToken cancellationToken)
    {
        var domain = scope.Domain;

        // Fresh: the 90s query cache would otherwise let a timer re-store the payload it
        // stored last time whenever the schedule is shorter than that cache.
        var request = new JourneysRequest(
            Domain: domain,
            Win: scope.Window,
            ClubId: scope.ClubId,
            Environment: scope.Environment,
            RowCapRaw: scope.RowCap,
            Fresh: true,
            ForcedMode: null,
            Host: scope.Host,
            PartnerId: scope.PartnerId);

        var outcome = await getJourneysService.GetAsync(request, cancellationToken);

        var metrics = await dashboardMetrics.SyncDomainAsync(scope, cancellationToken);

        if (outcome.StatusCode != 200)
        {
            return new JourneySyncPass(
                domain, false, 0, null, outcome.Result.Error, outcome.StatusCode, DashboardMetrics: metrics, ClubId: scope.ClubId);
        }

        // Only live telemetry is worth storing. The demo fixture and the offline snapshot are
        // fallbacks for display; writing them would make the store indistinguishable from real data.
        if (outcome.Result.Source != "live")
        {
            return new JourneySyncPass(domain, false, 0, null, $"source was '{outcome.Result.Source}', not live", DashboardMetrics: metrics, ClubId: scope.ClubId);
        }

        // An empty result would still create a QueryRun and move this club's snapshot to it,
        // leaving the portal reading an empty club. Keep the previous snapshot for that club.
        if (outcome.Result.Rows.Count == 0)
        {
            return new JourneySyncPass(domain, false, 0, null, "query returned no rows", DashboardMetrics: metrics, ClubId: scope.ClubId);
        }

        var result = await journeyStore.UpsertAsync(
            new JourneyUpsertRequest(
                Domain: domain,
                Window: outcome.Result.Win,
                Environment: outcome.Result.Environment,
                ClubId: outcome.Result.ClubId,
                Host: outcome.Result.Host,
                Source: outcome.Result.Source,
                Rows: outcome.Result.Rows),
            cancellationToken);

        return new JourneySyncPass(
            domain,
            true,
            result.RecordCount,
            result.QueryRunId,
            null,
            SourceStatusCode: outcome.StatusCode,
            Inserted: result.Inserted,
            Updated: result.Updated,
            Unchanged: result.Unchanged,
            DashboardMetrics: metrics,
            ClubId: scope.ClubId);
    }
}
