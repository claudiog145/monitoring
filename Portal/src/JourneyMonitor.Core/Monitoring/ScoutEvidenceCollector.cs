using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Fetches the individual requests and exceptions behind one board row, so the handoff package
/// carries what actually happened and not only the aggregate the board computed.
///
/// The query is a plain filter scoped to the row's own 5-minute bucket and to the operation ids
/// the board already captured - measured at ~4s against production, against ~40s for a board
/// query - which is what makes one call per package affordable.
///
/// Never throws: a failed or skipped collection is reported through
/// <see cref="ScoutRawEvidence.Error"/> and the package still ships with the aggregate evidence,
/// the same degrade-don't-break contract the rest of Monitoring follows.
/// </summary>
public sealed class ScoutEvidenceCollector(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<ScoutOptions> scoutOptions,
    ILogger<ScoutEvidenceCollector> logger) : IScoutEvidenceCollector
{
    public async Task<ScoutRawEvidence> CollectAsync(ScoutBoardRow row, CancellationToken ct = default)
    {
        var options = scoutOptions.Value;
        if (!options.ResolvedIncludeRawEvidence)
        {
            return ScoutRawEvidence.Skipped("Scout:IncludeRawEvidence is false");
        }

        var maxRows = options.ResolvedMaxRawEvidenceRows;
        var opIds = ReadOperationIds(row.GroupPayloadJson);
        if (opIds.Length == 0)
        {
            // Without operation ids to scope by, the only honest drill-down is none: the board can
            // produce a group with no captured ids, and a query with nothing to filter on would
            // return every request/exception in the bucket across all three resources - other
            // customers' activity, attached as evidence for an investigation that is not theirs.
            return ScoutRawEvidence.Skipped("no operation ids were captured for this bucket, so a scoped drill-down is not possible");
        }

        string kql;
        try
        {
            kql = BuildKql(kqlProvider.GetScoutDrillDownKql(), row, opIds, maxRows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[ScoutMonkey] could not build the drill-down query for {CaseId}.", row.CaseId);
            return ScoutRawEvidence.Skipped("the drill-down query could not be built");
        }

        try
        {
            var source = InsightsSourceResolver.ResolveForDomain(
                insightsOptions.Value, options.ResolvedEntryDomain, options.ResolvedEntryEnvironment);

            kql = await sourceResolver.ResolveAsync(kql, options.ResolvedEntryEnvironment, ct);
            var rows = await telemetrySource.QueryAsync(
                new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);

            // Hitting the cap is reported, not hidden: a truncated list that looks complete would
            // let L3 conclude "that is all that happened" from a partial sample.
            return new ScoutRawEvidence(kql, rows.Count, rows.Count >= maxRows, JsonSerializer.Serialize(rows), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[ScoutMonkey] drill-down query failed for {CaseId}.", row.CaseId);
            return new ScoutRawEvidence(kql, 0, false, "[]", Truncate(ex.Message, 500));
        }
    }

    /// <summary>Exposed for tests: a bad substitution only shows up as a generic Kusto syntax error at runtime.</summary>
    public static string BuildKql(string template, ScoutBoardRow row, string[] operationIds, int maxRows)
    {
        var kql = template
            .Replace("{{OPERATION_IDS}}", JsonSerializer.Serialize(operationIds))
            .Replace("{{TIME_START}}", ToKqlDatetime(row.WindowFrom))
            .Replace("{{TIME_END}}", ToKqlDatetime(row.WindowTo))
            .Replace("{{MAX_ROWS}}", maxRows.ToString());

        KqlPlaceholders.AssertNoneLeft(kql, "Scout drill-down KQL");

        return kql;
    }

    /// <summary>An empty list means the board captured no operation ids for this group - the caller
    /// must treat that as "cannot be scoped" rather than build a query from it (see CollectAsync).</summary>
    private static string[] ReadOperationIds(string groupPayloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(groupPayloadJson);
            if (!doc.RootElement.TryGetProperty("Ops", out var ops) || ops.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return ops.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ToKqlDatetime(DateTimeOffset value) =>
        $"datetime({value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}Z)";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…[truncated]";
}
