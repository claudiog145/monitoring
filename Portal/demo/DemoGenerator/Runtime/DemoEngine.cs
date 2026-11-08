using System.Diagnostics;
using DemoGenerator.Kusto;
using DemoGenerator.Synthesis;
using Microsoft.Extensions.Options;

namespace DemoGenerator.Runtime;

public sealed class ResetRequestException(string message) : Exception(message);

public sealed record CompanyResetResult(string CompanyKey, IReadOnlyDictionary<string, long> Rows);

public sealed record ResetResult(IReadOnlyList<CompanyResetResult> Companies, double ElapsedSeconds);

/// <summary>Feeding state of a company: its plan, reset anchor and, per app database, the first minute not written yet.</summary>
public sealed class CompanyState
{
    public required CompanyPlan Plan { get; init; }
    public required DateTimeOffset ResetAt { get; init; }
    public required int Days { get; init; }
    /// <summary>First minute not written yet, per (app database, table).</summary>
    public required Dictionary<(string Database, string Table), DateTimeOffset> NextMinute { get; init; }
}

/// <summary>Resets (backfill), live feeding and startup recovery. One ingestion at a time.</summary>
public sealed class DemoEngine(
    IKustoClient kusto,
    PackCatalog packs,
    ScenarioBoard scenarios,
    IOptions<DemoOptions> options,
    TimeProvider time,
    ILogger<DemoEngine> logger)
{
    private readonly SemaphoreSlim _ingestionLock = new(1, 1);
    private readonly Dictionary<string, CompanyState> _states = new(StringComparer.Ordinal);
    private readonly DemoKustoAdmin _admin = new(kusto);

    public bool Restored { get; private set; }

    /// <summary>Latest minute written by the feeder or a reset (exclusive), across companies.</summary>
    public DateTimeOffset? LastFedAt { get; private set; }

    public IReadOnlyCollection<string> FedCompanies
    {
        get { lock (_states) return _states.Keys.ToList(); }
    }

    public async Task<ResetResult> ResetAsync(IReadOnlyList<string>? companies, int? days, string? scale, CancellationToken ct)
    {
        var o = options.Value;
        packs.Load(o.PacksPath);
        var keys = companies is { Count: > 0 } ? companies.Distinct(StringComparer.Ordinal).ToList() : packs.Companies.Keys.ToList();
        var unknown = keys.Where(k => packs.Find(k) is null).ToList();
        if (unknown.Count > 0)
            throw new ResetRequestException($"Unknown company '{string.Join("', '", unknown)}'. Loaded: {string.Join(", ", packs.Companies.Keys)}.");
        if (keys.Count == 0) throw new ResetRequestException($"No company pack is loaded from '{o.PacksPath}'.");
        var effectiveDays = days ?? o.Days;
        if (effectiveDays is < 1 or > 30) throw new ResetRequestException("days must be 1..30.");
        var effectiveScale = string.IsNullOrWhiteSpace(scale) ? o.Scale : scale.Trim().ToLowerInvariant();
        if (!Scales.TryGetFactor(effectiveScale, out _)) throw new ResetRequestException($"Unknown scale '{scale}'. Use tiny, laptop or showcase.");

        await _ingestionLock.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            var resetAt = time.GetUtcNow();
            await _admin.EnsureRegistryAsync(o.DatabasePersistence, o.KustoDataPath, ct);

            var plans = keys.Select(k => packs.Plan(packs.Find(k)!, effectiveScale, o.Seed)).ToList();
            // Until the new backfill is complete the feeder must not append to half-rebuilt databases.
            lock (_states) foreach (var key in keys) _states.Remove(key);
            var jobs = new List<IngestionJob>();
            foreach (var plan in plans)
            {
                scenarios.Clear(plan.CompanyKey);
                foreach (var app in plan.Apps)
                    await _admin.RecreateAppDatabaseAsync(app.Database, o.DatabasePersistence, o.KustoDataPath, ct);
                await _admin.RegisterAppsAsync(plan.Apps.Select(a => new AppRegistration(a.AppId, a.Database)).ToList(), ct);
                jobs.AddRange(BackfillPlanner.PlanBackfill(plan, resetAt, effectiveDays, o.BatchHours));
            }

            logger.LogInformation("Reset of {Companies} ({Scale}, {Days} days): {Jobs} ingestion commands", string.Join(", ", keys), effectiveScale, effectiveDays, jobs.Count);
            var failed = await RunJobsAsync(jobs, ct);
            if (failed.Count > 0)
                throw new KustoCommandException($"{failed.Count} of {jobs.Count} ingestion command(s) failed; first: {failed[0].Error.Message}", 502);

            var (_, windowEnd) = BackfillPlanner.Window(resetAt, effectiveDays);
            var results = new List<CompanyResetResult>();
            foreach (var plan in plans)
            {
                await _admin.RegisterCompanyAsync(new CompanyRegistration(plan.CompanyKey, resetAt, effectiveScale, effectiveDays), ct);
                var totals = KqlSynthesisBuilder.Tables.ToDictionary(t => t, _ => 0L, StringComparer.Ordinal);
                foreach (var app in plan.Apps)
                    foreach (var (table, count) in await _admin.CountRowsAsync(app.Database, ct)) totals[table] += count;
                results.Add(new CompanyResetResult(plan.CompanyKey, totals));
                lock (_states)
                {
                    _states[plan.CompanyKey] = new CompanyState
                    {
                        Plan = plan,
                        ResetAt = resetAt,
                        Days = effectiveDays,
                        NextMinute = plan.Apps.SelectMany(a => KqlSynthesisBuilder.Tables.Select(t => (a.Database, t))).ToDictionary(k => k, _ => windowEnd),
                    };
                }
            }

            LastFedAt = windowEnd;
            var elapsed = Math.Round(watch.Elapsed.TotalSeconds, 1);
            logger.LogInformation("Reset done in {Seconds}s: {Rows}", elapsed,
                string.Join("; ", results.Select(r => $"{r.CompanyKey} " + string.Join(", ", r.Rows.Select(kv => $"{kv.Key}={kv.Value}")))));
            return new ResetResult(results, elapsed);
        }
        finally
        {
            _ingestionLock.Release();
        }
    }

    /// <summary>Writes every whole minute between each app's next minute and now, with the scenarios in force.</summary>
    public async Task<int> FeedOnceAsync(CancellationToken ct)
    {
        await _ingestionLock.WaitAsync(ct);
        try
        {
            var now = SynthesisClock.FloorMinute(time.GetUtcNow());
            List<CompanyState> states;
            lock (_states) states = _states.Values.ToList();

            var jobs = new List<IngestionJob>();
            var planned = new List<(CompanyState State, string Database, string Table)>();
            foreach (var state in states)
            {
                var incidents = state.Plan.HistoricalIncidents(state.ResetAt).Concat(scenarios.IncidentsFor(state.Plan.CompanyKey)).ToList();
                foreach (var app in state.Plan.Apps)
                {
                    // Each table keeps its own progress, so a failed command never makes another table repeat minutes.
                    var byStart = new Dictionary<DateTimeOffset, List<string>>();
                    foreach (var table in KqlSynthesisBuilder.Tables)
                    {
                        var from = state.NextMinute.GetValueOrDefault((app.Database, table), now);
                        // After a long outage, do not write more history than a reset would.
                        if (from < now - TimeSpan.FromDays(state.Days)) from = now - TimeSpan.FromDays(state.Days);
                        if (from >= now) continue;
                        if (!byStart.TryGetValue(from, out var tables)) byStart[from] = tables = [];
                        tables.Add(table);
                        planned.Add((state, app.Database, table));
                    }
                    foreach (var (from, tables) in byStart)
                        jobs.AddRange(BackfillPlanner.PlanApp(state.Plan, app, incidents, from, now, options.Value.BatchHours, tables));
                }
            }

            if (jobs.Count == 0) return 0;
            var failures = await RunJobsAsync(jobs, ct);
            var failedJobs = failures.Select(f => f.Job).ToHashSet();
            foreach (var (state, database, table) in planned)
            {
                // Advance to the end of the batches written without a gap; a failed batch and later ones are retried next tick.
                var next = state.NextMinute.GetValueOrDefault((database, table), now);
                foreach (var job in jobs.Where(j => j.Database == database && j.Table == table && j.CompanyKey == state.Plan.CompanyKey).OrderBy(j => j.From))
                {
                    if (failedJobs.Contains(job)) break;
                    next = job.To;
                }
                state.NextMinute[(database, table)] = next;
            }
            LastFedAt = now;
            return jobs.Count;
        }
        finally
        {
            _ingestionLock.Release();
        }
    }

    /// <summary>
    /// Rebuilds the feeding state after a restart from the registry (reset time and scale per company) and the
    /// latest request of each app database. Companies without a registry row or a database wait for a reset.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        var o = options.Value;
        packs.Load(o.PacksPath);
        var registered = await _admin.ReadCompaniesAsync(ct);
        var databases = await _admin.ListDatabasesAsync(ct);
        foreach (var company in registered)
        {
            var archetype = packs.Find(company.CompanyKey);
            if (archetype is null || !Scales.TryGetFactor(company.Scale, out _)) continue;
            var plan = packs.Plan(archetype, company.Scale, o.Seed);
            if (plan.Apps.Any(a => !databases.Contains(a.Database))) continue;

            var next = new Dictionary<(string Database, string Table), DateTimeOffset>();
            foreach (var app in plan.Apps)
            {
                var latest = await _admin.LatestRequestAsync(app.Database, ct);
                var resume = latest is { } l ? SynthesisClock.FloorMinute(l) + TimeSpan.FromMinutes(1) : BackfillPlanner.Window(company.ResetAt, company.Days).To;
                // Every table derives from requests, so the latest request marks the end of all six.
                foreach (var table in KqlSynthesisBuilder.Tables) next[(app.Database, table)] = resume;
            }

            lock (_states)
            {
                if (_states.ContainsKey(company.CompanyKey)) continue;
                _states[company.CompanyKey] = new CompanyState { Plan = plan, ResetAt = company.ResetAt, Days = company.Days, NextMinute = next };
            }
            var resumeAt = next.Values.Min();
            if (LastFedAt is null || resumeAt > LastFedAt) LastFedAt = resumeAt;
            logger.LogInformation("Resumed feeding {Company} ({Scale}) from {From:o}", company.CompanyKey, company.Scale, resumeAt);
        }
        Restored = true;
    }

    private async Task<IReadOnlyList<(IngestionJob Job, Exception Error)>> RunJobsAsync(IReadOnlyList<IngestionJob> jobs, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.IngestParallelism));
        var failures = new List<(IngestionJob Job, Exception Error)>();
        var tasks = jobs.Select(async job =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await kusto.ExecuteCommandAsync(job.Database, job.Command, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ingestion of {Table} in {Database} [{From:o}, {To:o}) failed", job.Table, job.Database, job.From, job.To);
                lock (failures) failures.Add((job, ex));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return failures;
    }
}
