using System.Text.Json;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Mapping;

/// <summary>
/// Direct, behavior-preserving port of map-v6.js: maps Hotel/Cars/Cruise KQL rows into
/// portal <see cref="CustomerJourney"/> objects. Do not "clean up" the Outcome/label logic
/// below; it is the exact decision tree the KQL/JS pair produces today.
/// </summary>
public static class HotelJourneyMapper
{
    private static readonly Dictionary<int, string> CruiseFailedStepLabels = new()
    {
        [1] = "T-Mobile SSO Failed",
        [2] = "Cruise Listing Failed",
        [3] = "Cruise Detail Failed",
        [4] = "Rooms & Rates Failed",
        [5] = "Review Total Price Failed",
        [6] = "Cabin Selection Failed",
        [7] = "Passenger Failed",
        [8] = "Options Failed",
        [9] = "Payment Failed",
        [10] = "Review Failed",
        [11] = "Confirmation Failed",
    };

    /// <summary>Cruise Customer Journey Status: "{stage} Failed"; leaves "Purchase Failed" as-is.</summary>
    public static string CruiseStatusLabel(string outcome)
    {
        if (outcome.StartsWith("purchase failed", StringComparison.OrdinalIgnoreCase)) return outcome;
        var stepMatch = System.Text.RegularExpressions.Regex.Match(outcome, @"^failed at step\s+(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (stepMatch.Success && int.TryParse(stepMatch.Groups[1].Value, out var step))
        {
            return CruiseFailedStepLabels.TryGetValue(step, out var label) ? label : outcome;
        }
        var namedMatch = System.Text.RegularExpressions.Regex.Match(outcome, @"^failed at (.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (namedMatch.Success) return $"{namedMatch.Groups[1].Value} Failed";
        return outcome;
    }

    private static string PrimaryComponent(List<JsonElement> calls)
    {
        var api = calls.FirstOrDefault(c => c.Str("kind") == "api");
        if (api.ValueKind != JsonValueKind.Undefined) return api.Str("call");
        var result = calls.FirstOrDefault(c => c.Str("kind") == "result");
        if (result.ValueKind != JsonValueKind.Undefined) return result.Str("call");
        return calls.Count > 0 ? calls[0].Str("call") : "\u2014";
    }

    private static string V6StageUiStatus(JsonElement stage)
    {
        var calls = stage.ArrayOrObjectValues("calls");
        var hasResultFail = calls.Any(c => c.Str("kind") == "result" && (c.Dbl("failureRatePct") ?? 0) > 0);
        if (hasResultFail) return "failed";
        var s = stage.Str("status").ToUpperInvariant();
        return s switch
        {
            "RED" => "failed",
            "YELLOW" => "degraded",
            "GREEN" or "N/A" => "success",
            _ => "success",
        };
    }

    private static string StageDiagnostic(JsonElement stage, TelemetryJourneyRow row)
    {
        if (stage.Int("stageNo") == 7)
        {
            var failReason = row.GetString("FailReason");
            if (!string.IsNullOrEmpty(failReason)) return failReason;
        }
        var calls = stage.ArrayOrObjectValues("calls");
        var withErr = calls.FirstOrDefault(c => c.ArrayOrObjectValues("errors").Count > 0);
        if (withErr.ValueKind != JsonValueKind.Undefined)
        {
            var err = withErr.ArrayOrObjectValues("errors")[0];
            var message = err.Str("message");
            return !string.IsNullOrEmpty(message) ? message : err.ToString();
        }
        var p95 = stage.Dbl("p95Ms");
        if (p95 is not null) return $"P95 {p95} ms \u00b7 {stage.Str("status")}";
        return "No TMO API duration in this stage.";
    }

    private static string FirstReservationId(TelemetryJourneyRow row)
    {
        var el = row.Get("BookedReservations");
        if (el is null) return "";
        if (el.Value.ValueKind == JsonValueKind.Array)
        {
            var first = el.Value.EnumerateArray().FirstOrDefault();
            return first.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : first.ToString();
        }
        if (el.Value.ValueKind == JsonValueKind.Object)
        {
            var first = el.Value.EnumerateObject()
                .Select(p => p.Value)
                .FirstOrDefault(v => v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);
            return first.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : first.ToString();
        }
        return "";
    }

    private static List<JourneySessionRef> ReadSessions(TelemetryJourneyRow row)
    {
        var el = row.Get("Sessions");
        var list = new List<JourneySessionRef>();
        if (el is not null)
        {
            var items = el.Value.ValueKind switch
            {
                JsonValueKind.Array => [.. el.Value.EnumerateArray()],
                JsonValueKind.String => TryParseArray(el.Value.GetString()),
                JsonValueKind.Object => el.Value.EnumerateObject().Select(p => p.Value).ToList(),
                _ => new List<JsonElement>(),
            };
            foreach (var item in items)
            {
                list.Add(new JourneySessionRef
                {
                    Session = item.Int("session"),
                    CorrelationId = item.Str("correlationId"),
                    ChangeReason = item.Str("changeReason"),
                });
            }
        }
        if (list.Count == 0)
        {
            var correlationId = row.GetString("CorrelationId");
            if (!string.IsNullOrEmpty(correlationId))
            {
                list.Add(new JourneySessionRef { Session = 1, CorrelationId = correlationId, ChangeReason = "Latest session" });
            }
        }
        return list;
    }

    private static List<JsonElement> TryParseArray(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray()] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static CustomerJourney MapRow(TelemetryJourneyRow row, string clubLabel, string domain, string environment)
    {
        var stagesIn = row.GetJourneyStages();
        double offset = 0;
        var stages = new List<JourneyStage>();
        foreach (var st in stagesIn)
        {
            var durationMs = st.Dbl("stageTotalMs") ?? 0;
            var calls = st.ArrayOrObjectValues("calls");
            var component = PrimaryComponent(calls);
            var stage = new JourneyStage
            {
                Name = st.Str("stage"),
                Component = component,
                StartOffsetMs = offset,
                DurationMs = durationMs,
                Status = V6StageUiStatus(st),
                Downstream = component,
                RequestCount = st.Prop("callCount") is null ? calls.Count : st.Int("callCount"),
                Diagnostic = StageDiagnostic(st, row),
                P95Ms = st.Dbl("p95Ms"),
                FailureRatePct = st.Dbl("failureRatePct"),
                V6Status = st.Str("status"),
            };
            stage.Error = stage.Status == "failed";
            stage.Calls = calls.Select(c => new JourneyCall
            {
                Kind = c.Str("kind"),
                Call = c.Str("call"),
                CorrelationId = !string.IsNullOrEmpty(c.Str("correlationId")) ? c.Str("correlationId")
                    : (domain == "cars" ? row.GetString("CorrelationId") : ""),
                T = c.Str("t"),
                N = c.Int("n"),
                CallTotalMs = c.Dbl("callTotalMs"),
                P50Ms = c.Dbl("p50Ms"),
                P95Ms = c.Dbl("p95Ms"),
                MaxMs = c.Dbl("maxMs"),
                FailureRatePct = c.Dbl("failureRatePct"),
                Status = c.Str("status"),
                Detail = domain == "cruises" ? (NonEmpty(c.Str("source")) ?? c.Str("detail")) : c.Str("detail"),
                Errors = c.ArrayOrObjectValues("errors").Select(e => new JourneyCallError
                {
                    N = e.Prop("n") is null ? null : e.Int("n"),
                    T = e.Str("t"),
                    Message = e.Str("message"),
                }).ToList(),
            }).ToList();
            offset += durationMs;
            stages.Add(stage);
        }

        var bookingId = FirstReservationId(row);
        var domainName = domain switch { "cars" => "Cars", "cruises" => "Cruise", _ => "Hotel" };
        var rawOutcome = row.GetString("Outcome");
        var outcome = domain == "cruises" ? CruiseStatusLabel(rawOutcome) : rawOutcome;
        var correlationId = row.GetString("CorrelationId");
        var userId = row.GetString("UserId");
        var sessionId = row.GetString("SessionId");

        return new CustomerJourney
        {
            Source = "v6",
            Id = domain is "cruises" or "cars"
                ? (!string.IsNullOrEmpty(correlationId) ? correlationId : (!string.IsNullOrEmpty(sessionId) ? sessionId : userId))
                : userId,
            CorrelationId = !string.IsNullOrEmpty(correlationId) ? correlationId : userId,
            Domain = domain,
            Customer = userId,
            Origin = $"{clubLabel} {domainName}",
            Destination = environment == "staging" ? "STAGING" : "PROD",
            StartDate = row.GetString("FirstSeen"),
            Status = outcome,
            Outcome = outcome,
            TotalDurationMs = row.GetDouble("JourneyTotalMs") ?? 0,
            BookingId = bookingId,
            Timestamp = row.GetString("LastSeen"),
            Environment = environment,
            FailReason = row.GetString("FailReason"),
            LastStep = row.GetString("LastStep"),
            SessionCount = row.GetInt("SessionCount"),
            Sessions = ReadSessions(row),
            CallsNoCorrelation = row.GetInt("CallsNoCorrelation"),
            CallsViaCorrelation = row.GetInt("CallsViaCorrelation"),
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

    private static string? NonEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    public static List<CustomerJourney> MapRows(IEnumerable<TelemetryJourneyRow> rows, string clubLabel, string domain, string environment) =>
        rows.Select(r => MapRow(r, clubLabel, domain, environment)).ToList();
}
