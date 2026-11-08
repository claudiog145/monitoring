using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.ScoutShadow;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>A daemon anomaly as the Scout's Method mode sees it: which service (KPI domain), which series, which bucket.</summary>
public sealed record ScoutDaemonSignal(
    long AnomalyId,
    string Domain,
    string EntityKey,
    DateTimeOffset BucketStart,
    TimeSpan BucketSize,
    string Kpi,
    string Detection);

/// <summary>
/// Contract v4 §4 Method mode (SD8): the daemons decide WHAT is anomalous - the same KPI pipeline that feeds Platform
/// Health and Triage - and the board only supplies the evidence for it (entity, operations, raw requests). For each
/// anomaly the best board row of the same service (and the same entity, for an entity-grain anomaly) around the
/// anomaly's bucket becomes the case, re-labelled "Investigate now" with the daemon's detection as the reason.
/// </summary>
public static class ScoutMethodSelector
{
    public static IReadOnlyList<ScoutBoardRow> Select(
        ICompanyAdapter adapter,
        IReadOnlyList<ScoutBoardRow> rows,
        IReadOnlyList<ScoutDaemonSignal> signals,
        TimeSpan rowBucket)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (rows.Count == 0 || signals.Count == 0) return [];

        var described = rows
            .Select(r => (Row: r, Case: ScoutShadowRecorder.ToRuleCase(adapter, r), Service: ServiceOf(adapter, r)))
            .ToList();

        var chosen = new Dictionary<string, ScoutBoardRow>(StringComparer.Ordinal);
        foreach (var signal in signals.OrderByDescending(s => s.BucketStart))
        {
            var entity = string.IsNullOrEmpty(signal.EntityKey) ? null : ScoutShadowEntityKey.FromKpi(signal.EntityKey);
            var sameService = described
                .Where(d => string.Equals(d.Service, signal.Domain, StringComparison.OrdinalIgnoreCase))
                .Where(d => entity is null || string.Equals(d.Case?.EntityKey, entity, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameService.Count == 0) continue;

            var from = signal.BucketStart - rowBucket;
            var to = signal.BucketStart + signal.BucketSize + rowBucket;
            var near = sameService.Where(d => d.Row.WindowFrom < to && d.Row.WindowTo > from).ToList();
            var pool = near.Count > 0 ? near : sameService;

            var best = pool
                .OrderByDescending(d => d.Row.PriorityRank)
                .ThenByDescending(d => Requests(d.Row))
                .ThenByDescending(d => d.Row.WindowFrom)
                .First().Row;

            var reason = $"Daemon anomaly #{signal.AnomalyId} ({signal.Kpi}/{signal.Domain}): {signal.Detection}";
            if (entity is null && near.Count > 0)
            {
                // A domain anomaly is service-wide: one entity's row (often a handful of requests) would hand the
                // Inspector a single slow request and no cause. The case covers every row of the service around the
                // anomaly instead - their operation ids scope the drill-down, their counts are the impact.
                var merged = MergeServiceWide(best, near.Select(d => d.Row).ToList(), signal);
                chosen[merged.CaseId] = merged with { PriorityRank = 3, Priority = "Investigate now", Reason = reason };
                continue;
            }

            if (chosen.ContainsKey(best.CaseId)) continue;

            chosen[best.CaseId] = best with
            {
                PriorityRank = 3,
                Priority = "Investigate now",
                Reason = reason,
            };
        }

        return chosen.Values.ToList();
    }

    private const int MaxMergedOperationIds = 200;

    /// <summary>One case for the whole service around the anomaly: operation ids unioned, counts summed, window widened.</summary>
    public static ScoutBoardRow MergeServiceWide(ScoutBoardRow best, IReadOnlyList<ScoutBoardRow> rows, ScoutDaemonSignal signal)
    {
        var from = rows.Min(r => r.WindowFrom);
        var to = rows.Max(r => r.WindowTo);
        var ops = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long requests = 0, failed = 0, exceptions = 0;
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderByDescending(r => r.PriorityRank).ThenByDescending(Requests))
        {
            var payload = TryParse(row.GroupPayloadJson);
            if (payload?["Ops"] is JsonArray arr)
            {
                foreach (var op in arr)
                {
                    if (op?.GetValueKind() == JsonValueKind.String && op.GetValue<string>() is { Length: > 0 } id
                        && ops.Count < MaxMergedOperationIds && seen.Add(id)) ops.Add(id);
                }
            }
            if (payload?["Entity"]?.GetValueKind() == JsonValueKind.String) entities.Add(payload["Entity"]!.GetValue<string>());
            var raw = TryParse(row.RawRowJson);
            requests += Number(raw, "Requests");
            failed += Number(raw, "FailedRequests");
            exceptions += Number(raw, "Exceptions");
        }

        var caseId = $"{signal.Domain} | service-wide | daemon anomaly #{signal.AnomalyId} | {signal.BucketStart.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        var mergedPayload = TryParse(best.GroupPayloadJson) ?? new JsonObject();
        mergedPayload["CaseId"] = caseId;
        mergedPayload["Ops"] = new JsonArray(ops.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
        mergedPayload["Start"] = from;
        mergedPayload["End"] = to;
        mergedPayload["Scope"] = $"service-wide: {entities.Count} entities of {signal.Domain}";
        mergedPayload["Entities"] = new JsonArray(entities.Take(50).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        var payloadJson = mergedPayload.ToJsonString();

        var mergedRaw = TryParse(best.RawRowJson) ?? new JsonObject();
        mergedRaw["Requests"] = requests;
        mergedRaw["FailedRequests"] = failed;
        mergedRaw["Exceptions"] = exceptions;
        mergedRaw["FailurePercent"] = requests > 0 ? Math.Round(100.0 * failed / requests, 1) : 0;
        mergedRaw["GroupPayload"] = payloadJson;

        return best with
        {
            CaseId = caseId,
            WindowFrom = from,
            WindowTo = to,
            GroupPayloadJson = payloadJson,
            RawRowJson = mergedRaw.ToJsonString(),
        };
    }

    private static JsonObject? TryParse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static long Number(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n
        : obj?[name] is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x : 0;

    private static long Requests(ScoutBoardRow row)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(row.RawRowJson);
            return doc.RootElement.TryGetProperty("Requests", out var r) && r.TryGetInt64(out var n) ? n : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }

    private static string ServiceOf(ICompanyAdapter adapter, ScoutBoardRow row) =>
        ScoutShadowRecorder.ToRuleCase(adapter, row)?.Service is { Length: > 0 } service
            ? service
            : ScoutRowMapper.Classify(adapter, row).Service ?? "";
}
