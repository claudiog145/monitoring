using System.Diagnostics;
using System.Text.Json.Nodes;
using FakeAppInsights.Catalog;
using FakeAppInsights.Kusto;
using FakeAppInsights.Rewrite;

namespace FakeAppInsights.Query;

/// <summary>The outcome of a query: a 200 body or an App Insights error.</summary>
public sealed record QueryOutcome(JsonObject? Body, AppInsightsError? Error);

/// <summary>
/// Runs one App Insights query (contract v5 Â§3.1): appId â†’ database, <c>app()</c> rewrite,
/// <c>timespan</c> filter, execution on Kustainer and mapping to the App Insights shape.
/// </summary>
public sealed class QueryService(
    IApplicationDirectory directory,
    IKustoExecutor kusto,
    TimeProvider time,
    ILogger<QueryService> logger)
{
    public async Task<QueryOutcome> ExecuteAsync(string appId, string? query, string? timespan, CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await directory.GetAsync(cancellationToken);
            var database = catalog.ResolveApp(appId);
            if (database is null)
            {
                catalog = await directory.RefreshAsync(cancellationToken);
                database = catalog.ResolveApp(appId);
            }
            if (database is null) return Fail(AppInsightsError.ApplicationNotFound(appId));

            if (string.IsNullOrWhiteSpace(query))
                return Fail(AppInsightsError.BadArgument("The 'query' parameter is required.", "QueryMissing"));

            TimeRange? range = null;
            if (!string.IsNullOrWhiteSpace(timespan))
            {
                try
                {
                    range = TimespanParser.Parse(timespan, time.GetUtcNow());
                }
                catch (FormatException ex)
                {
                    return Fail(AppInsightsError.BadArgument(ex.Message, "InvalidTimespan"));
                }
            }

            var csl = AppReferenceRewriter.Rewrite(query, catalog);
            if (range is not null)
            {
                var unfilterable = UnfilterableQueryDetector.Find(csl);
                if (unfilterable is not null) return Fail(AppInsightsError.BadArgument(unfilterable, "UnsupportedTimespanQuery"));
                csl = TimespanFilter.Apply(csl, range.From, range.To);
            }

            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<KustoTable> tables;
            try
            {
                tables = await kusto.QueryAsync(database, csl, cancellationToken);
            }
            catch (KustoQueryException ex)
            {
                logger.LogInformation("Query appId={AppId} db={Database} failed in {ElapsedMs} ms: {Code}",
                    appId, database, stopwatch.ElapsedMilliseconds, ex.Code ?? "BadArgument");
                logger.LogDebug("Rejected query text for appId={AppId}: {Query}", appId, csl);
                return new QueryOutcome(null, AppInsightsError.BadArgument(ex.Message, ex.Code));
            }

            logger.LogInformation("Query appId={AppId} db={Database} ms={ElapsedMs} tables={Tables} rows={Rows} timespan={Timespan}",
                appId, database, stopwatch.ElapsedMilliseconds, tables.Count, tables.Sum(t => t.Rows.Count), range is not null);
            logger.LogDebug("Query text for appId={AppId}: {Query}", appId, csl);
            return new QueryOutcome(AppInsightsResponse.Build(tables), null);
        }
        catch (KqlRewriteException ex)
        {
            return Fail(AppInsightsError.BadArgument(ex.Message, ex is UnknownApplicationException ? "UnknownApplication" : "UnsupportedQuery", ex.Message));
        }
        catch (Exception ex) when (ex is KustoUnavailableException or KustoQueryException)
        {
            logger.LogWarning("Query appId={AppId}: Kusto engine unavailable: {Message}", appId, ex.Message);
            return new QueryOutcome(null, AppInsightsError.ServiceUnavailable(ex.Message));
        }

        QueryOutcome Fail(AppInsightsError error)
        {
            logger.LogInformation("Query appId={AppId} rejected: {Status} {Code}", appId, (int)error.Status, error.InnerCode ?? error.Code);
            return new QueryOutcome(null, error);
        }
    }
}
