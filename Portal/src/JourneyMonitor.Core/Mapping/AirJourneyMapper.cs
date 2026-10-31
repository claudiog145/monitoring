using System.Text.Json;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Mapping;

/// <summary>
/// Direct, behavior-preserving port of map-air.js: maps Air KQL rows into portal
/// <see cref="CustomerJourney"/> objects. Air telemetry mixes first-party calls with
/// third-party analytics beacons (Google Analytics, Datadog, etc.); a failed beacon must
/// degrade, never fail, a stage - see <see cref="ThirdParty"/>.
/// </summary>
public static class AirJourneyMapper
{
    private static readonly Regex ThirdParty = new(
        string.Join('|',
        [
            "google-analytics", @"analytics\.google\.com", "googletagmanager", "doubleclick", "gstatic", "adservice",
            "niceincontact", "tealium", "datadoghq", "loggly", @"clarity\.ms", "kampyle",
            "hotjar", "newrelic", "facebook", @"segment\.io", "optimizely",
        ]),
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, int> KindRank = new() { ["request"] = 0, ["event"] = 1, ["page"] = 2, ["dependency"] = 3 };

    private static string CallName(JsonElement c)
    {
        var name = c.Str("name");
        return !string.IsNullOrEmpty(name) ? name : c.Str("call");
    }

    /// <summary>Air call names carry the whole query string and can reach 1 KB.</summary>
    private static string ShortLabel(string value, int max = 80)
    {
        var text = value.Split('?')[0].Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "\u2026";
    }

    private static int CallRank(JsonElement c)
    {
        var kind = c.Str("kind");
        var kindRank = KindRank.TryGetValue(kind, out var r) ? r : 4;
        return (ThirdParty.IsMatch(CallName(c)) ? 10 : 0) + kindRank;
    }

    private static JsonElement? PrimaryAirCall(List<JsonElement> calls)
    {
        var list = calls.Where(c => !string.IsNullOrEmpty(CallName(c))).ToList();
        if (list.Count == 0) return null;
        return list.OrderBy(CallRank).ThenBy(c => CallName(c).Length).First();
    }

    private static string LatencyBand(double? p95Ms)
    {
        if (p95Ms is null) return "N/A";
        if (p95Ms < 2000) return "GREEN";
        if (p95Ms < 5000) return "YELLOW";
        return "RED";
    }

    private static (string Origin, string Destination) SplitRoute(string? route)
    {
        var text = (route ?? "").Trim();
        if (string.IsNullOrEmpty(text) || text == "-") return ("Air", "PROD");
        var parts = Regex.Split(text, @"\s*->\s*");
        return parts.Length >= 2 ? (parts[0].Trim(), parts[1].Trim()) : (text, "PROD");
    }

    /// <summary>A failed analytics beacon degrades a stage; only our own calls fail it.</summary>
    private static string AirStageStatus(JsonElement stage)
    {
        var calls = stage.ArrayOrObjectValues("calls");
        var failed = calls.Where(c => c.Bool("failed")).ToList();
        if (failed.Any(c => !ThirdParty.IsMatch(CallName(c)))) return "failed";
        if (failed.Count > 0) return "degraded";
        if (calls.Count == 0 && stage.Int("fails") > 0) return "failed";
        return "success";
    }

    private static string FirstFailedCall(List<JsonElement> stages)
    {
        JsonElement? beaconStage = null;
        foreach (var st in stages)
        {
            var failed = st.ArrayOrObjectValues("calls").Where(c => c.Bool("failed")).ToList();
            var own = failed.FirstOrDefault(c => !ThirdParty.IsMatch(CallName(c)));
            if (own.ValueKind != JsonValueKind.Undefined)
            {
                var step = st.Str("step");
                if (string.IsNullOrEmpty(step)) step = st.Str("name");
                var bits = new[] { step, ShortLabel(CallName(own), 70), own.Str("status") }.Where(v => !string.IsNullOrEmpty(v));
                return string.Join(" \u00b7 ", bits);
            }
            if (beaconStage is null && failed.Count > 0) beaconStage = st;
            if (beaconStage is null && failed.Count == 0 && st.Int("fails") > 0) beaconStage = st;
        }
        if (beaconStage is not null)
        {
            var step = beaconStage.Value.Str("step");
            if (string.IsNullOrEmpty(step)) step = beaconStage.Value.Str("name");
            return $"{step} \u00b7 only third-party analytics calls failed";
        }
        return "";
    }

    private static JourneyCall MapAirCall(JsonElement c)
    {
        var name = CallName(c);
        var failed = c.Bool("failed");
        return new JourneyCall
        {
            Kind = c.Str("kind"),
            Call = ShortLabel(name, 120),
            CorrelationId = c.Str("correlationId"),
            T = c.Str("t"),
            N = c.Int("n"),
            CallTotalMs = c.Dbl("durationMs"),
            P50Ms = c.Dbl("p50Ms"),
            P95Ms = c.Dbl("p95Ms"),
            MaxMs = c.Dbl("maxMs"),
            FailureRatePct = failed ? 100 : 0,
            Status = c.Str("status"),
            Detail = ShortLabel(!string.IsNullOrEmpty(c.Str("target")) ? c.Str("target") : c.Str("path"), 120),
            Errors = failed
                ? [new JourneyCallError { Message = $"{ShortLabel(name, 70)} {c.Str("status", "failed")}".Trim() }]
                : [],
        };
    }

    public static CustomerJourney MapRow(TelemetryJourneyRow row, string clubLabel, string environment)
    {
        var stagesIn = row.GetJourneyStages();
        var stages = new List<JourneyStage>();
        foreach (var st in stagesIn)
        {
            var status = AirStageStatus(st);
            var calls = st.ArrayOrObjectValues("calls");
            var primary = PrimaryAirCall(calls);
            var label = primary is not null ? ShortLabel(CallName(primary.Value)) : "\u2014";
            var requestCount = st.Prop("callCount") is null ? calls.Count : st.Int("callCount");
            var p95 = st.Dbl("p95Ms");
            var fails = st.Int("fails");
            stages.Add(new JourneyStage
            {
                Name = st.Str("step"),
                Component = label,
                StartOffsetMs = st.Dbl("offsetMs") ?? 0,
                DurationMs = st.Dbl("durationMs") ?? 0,
                Status = status,
                Downstream = label,
                RequestCount = requestCount,
                Error = status == "failed",
                Diagnostic = status == "failed" ? FirstFailedCall([st]) : (p95 is not null ? $"P95 {p95} ms \u00b7 {LatencyBand(p95)}" : ""),
                P95Ms = p95,
                FailureRatePct = fails > 0 && requestCount > 0 ? Math.Round((double)fails / requestCount * 10000) / 100 : 0,
                V6Status = LatencyBand(p95),
                Calls = calls.Select(MapAirCall).ToList(),
            });
        }

        var (origin, destination) = SplitRoute(row.GetString("Route"));
        var outcome = row.GetString("Status").ToUpperInvariant();
        var hasTrouble = outcome.Contains("FAILED") || outcome == "DEGRADED" || stages.Any(s => s.Status is "failed" or "degraded");
        var failReason = hasTrouble ? FirstFailedCall(stagesIn) : "";
        var hasOwnFailure = stagesIn.Any(st => st.ArrayOrObjectValues("calls").Any(c => c.Bool("failed") && !ThirdParty.IsMatch(CallName(c))));
        if (hasTrouble && row.GetInt("BookFail") > 0 && !hasOwnFailure) failReason = "Book Error";

        var correlationId = row.GetString("CorrelationId");
        var bookingId = row.GetString("BookingId");
        var statusRaw = row.GetString("Status");

        return new CustomerJourney
        {
            Source = "air-v6",
            Id = correlationId,
            CorrelationId = correlationId,
            Domain = "air",
            Customer = row.GetString("UserId"),
            Origin = origin,
            Destination = destination,
            StartDate = row.GetString("FirstSeen"),
            Status = statusRaw,
            Outcome = statusRaw,
            TotalDurationMs = row.GetDouble("JourneyTotalMs") ?? 0,
            BookingId = bookingId != "-" ? bookingId : "",
            Timestamp = row.GetString("LastSeen"),
            Environment = environment,
            FailReason = failReason,
            LastStep = row.GetString("LastStep"),
            SessionCount = 1,
            BookOk = row.GetInt("BookOk"),
            BookFail = row.GetInt("BookFail"),
            FailedCalls = row.GetInt("FailedCalls"),
            PassedSearch = row.GetBool("PassedSearch"),
            IsBookSuccessful = row.GetBool("IsBookSuccessful"),
            Exceptions = row.GetInt("Exceptions"),
            TotalCalls = stages.Sum(s => s.RequestCount),
            OverallP95Ms = row.GetDouble("OverallP95"),
            Stages = stages,
        };
    }

    public static List<CustomerJourney> MapRows(IEnumerable<TelemetryJourneyRow> rows, string clubLabel, string environment) =>
        rows.Select(r => MapRow(r, clubLabel, environment)).ToList();
}
