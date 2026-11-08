using DemoGenerator.Synthesis;

namespace DemoGenerator.Runtime;

/// <summary>One <c>.set-or-append</c> to run against one app database.</summary>
public sealed record IngestionJob(string CompanyKey, string Database, string Table, DateTimeOffset From, DateTimeOffset To, string Command);

/// <summary>Splits windows into per-app, per-batch ingestion commands with the incidents that overlap them.</summary>
public static class BackfillPlanner
{
    /// <summary>[resetAt - days, resetAt) rounded to whole minutes.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window(DateTimeOffset resetAt, int days)
    {
        var to = SynthesisClock.FloorMinute(resetAt);
        return (to - TimeSpan.FromDays(days), to);
    }

    /// <summary>The backfill of a reset: every app, the whole window, the historical incidents anchored at <paramref name="resetAt"/>.</summary>
    public static IReadOnlyList<IngestionJob> PlanBackfill(CompanyPlan company, DateTimeOffset resetAt, int days, int batchHours, IEnumerable<ActiveIncident>? extraIncidents = null)
    {
        var (from, to) = Window(resetAt, days);
        var incidents = company.HistoricalIncidents(resetAt).Concat(extraIncidents ?? []).ToList();
        return company.Apps.SelectMany(app => PlanApp(company, app, incidents, from, to, batchHours)).ToList();
    }

    public static IEnumerable<IngestionJob> PlanApp(CompanyPlan company, AppPlan app, IReadOnlyList<ActiveIncident> incidents, DateTimeOffset from, DateTimeOffset to, int batchHours, IReadOnlyCollection<string>? tables = null)
    {
        var effects = EffectCompiler.Compile(company, app, incidents);
        var batch = TimeSpan.FromHours(Math.Max(1, batchHours));
        var cursor = SynthesisClock.FloorMinute(from);
        var end = SynthesisClock.FloorMinute(to);
        while (cursor < end)
        {
            // Batches end on multiples of the batch size from the epoch, so the same hours always form the same batch.
            var boundary = DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(((cursor - DateTimeOffset.UnixEpoch).Ticks / batch.Ticks + 1) * batch.Ticks);
            var next = boundary < end ? boundary : end;
            foreach (var (table, command) in KqlSynthesisBuilder.Commands(app, effects, cursor, next).Where(c => tables is null || tables.Contains(c.Table)))
                yield return new IngestionJob(company.CompanyKey, app.Database, table, cursor, next, command);
            cursor = next;
        }
    }
}
