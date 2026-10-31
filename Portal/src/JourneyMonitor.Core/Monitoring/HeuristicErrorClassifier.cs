using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Deterministic, cheap classification from a Finding's EvidenceJson BEFORE any LLM call —
/// mirrors the "heuristic first, LLM only as tie-breaker" principle from the design doc.
/// Supports two EvidenceJson shapes depending on which detector raised the Finding:
/// <c>statusCodeBreakdown</c> (real HTTP codes, from the KQL-based detector) or
/// <c>healthStatusBreakdown</c> (RED/YELLOW/GREEN traffic-light, from the SQL-journey-store-based
/// detector - see SqlLatencySpikeDetectorService). The health-status shape carries less signal
/// than real HTTP codes (RED could mean a 500, a 503, or a 401 - there is no way to tell), so it
/// intentionally only recognizes the one case it CAN tell apart honestly (all-healthy-but-slow);
/// everything else falls through to "unknown" rather than guessing a specific category.
/// </summary>
public sealed class HeuristicErrorClassifier : IErrorClassifier
{
    public string Classify(Finding finding)
    {
        try
        {
            using var doc = JsonDocument.Parse(finding.EvidenceJson);

            if (doc.RootElement.TryGetProperty("statusCodeBreakdown", out var statusCodes) && statusCodes.ValueKind == JsonValueKind.Object)
            {
                return ClassifyFromStatusCodes(statusCodes);
            }

            if (doc.RootElement.TryGetProperty("healthStatusBreakdown", out var healthStatuses) && healthStatuses.ValueKind == JsonValueKind.Object)
            {
                return ClassifyFromHealthStatus(healthStatuses);
            }

            return "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private static string ClassifyFromStatusCodes(JsonElement breakdown)
    {
        var codes = breakdown.EnumerateObject().Select(p => p.Name).ToList();
        if (codes.Count == 0) return "unknown";

        if (codes.All(c => c == "200" || c.StartsWith('2')))
        {
            // All successful responses that were still slow -> not an error status at all,
            // points at infra/dependency latency rather than application logic.
            return "server_infrastructure";
        }
        if (codes.Any(c => c is "502" or "503" or "504"))
        {
            return "external_dependency";
        }
        if (codes.Any(c => c is "500"))
        {
            return "application_code";
        }
        if (codes.Any(c => c is "400" or "422"))
        {
            return "data_quality";
        }
        if (codes.Any(c => c is "401" or "403"))
        {
            return "configuration";
        }
        return "unknown";
    }

    private static string ClassifyFromHealthStatus(JsonElement breakdown)
    {
        var statuses = breakdown.EnumerateObject().Select(p => p.Name).ToList();
        if (statuses.Count == 0) return "unknown";

        if (statuses.All(s => s is "GREEN" or "N/A"))
        {
            // Every call in the cluster was healthy and still slow -> same call as the all-2xx
            // case above: infra/dependency latency rather than an application error.
            return "server_infrastructure";
        }

        // RED/YELLOW alone cannot distinguish a 500 from a 503 from a 401 - guessing here would
        // contradict the "unknown is a valid, honest outcome" principle, so leave it to the LLM.
        return "unknown";
    }
}
