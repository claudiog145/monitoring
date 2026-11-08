using System.Globalization;
using System.Text.Json;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Mapping;

/// <summary>
/// Folds hotel sessions that are one visit into a single raw row, after the KQL returns.
/// The query stays one row per inactivity session so it finishes inside the ingest timeout.
/// Same member, the next session starts within <see cref="Gap"/> of the previous session's
/// end, the next session begins at a later funnel stage, and the previous session did not
/// book: those rows are one visit. The CorrelationId kept is the session that reached the
/// furthest stage. The next session's starting stage is the stage of its earliest step,
/// so a login call that lands on a search session afterwards does not keep them apart.
/// The other ids are listed on <see cref="AbsorbedIdsProperty"/> so ingest can drop
/// fragment rows a previous run stored under those keys.
/// </summary>
public static class HotelVisitMerger
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(2);

    /// <summary>JSON array of correlation ids folded into this row. Absent when the row is unchanged.</summary>
    public const string AbsorbedIdsProperty = "AbsorbedCorrelationIds";

    public static List<TelemetryJourneyRow> Merge(IReadOnlyList<TelemetryJourneyRow> rows)
    {
        if (rows.Count <= 1) return rows.ToList();

        var fragments = new List<Fragment>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            fragments.Add(Fragment.Read(rows[i], i));
        }

        var ordered = fragments
            .OrderBy(f => f.UserKey, StringComparer.Ordinal)
            .ThenBy(f => f.Start ?? DateTimeOffset.MaxValue)
            .ThenBy(f => f.CorrelationId, StringComparer.Ordinal)
            .ToList();

        var visits = new List<List<Fragment>>();
        List<Fragment>? current = null;
        Fragment? previous = null;
        foreach (var fragment in ordered)
        {
            if (current is null || IsNewVisit(previous, fragment))
            {
                current = [];
                visits.Add(current);
            }

            current.Add(fragment);
            previous = fragment;
        }

        var folded = false;
        var merged = new List<TelemetryJourneyRow>(visits.Count);
        foreach (var visit in visits)
        {
            if (visit.Count == 1)
            {
                merged.Add(visit[0].Row);
                continue;
            }

            folded = true;
            merged.Add(Combine(visit));
        }

        if (!folded) return rows.ToList();

        return merged
            .OrderByDescending(row => row.GetDateTimeOffset("LastSeen") ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.GetString("CorrelationId"), StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsNewVisit(Fragment? previous, Fragment fragment)
    {
        if (previous is null || fragment.Isolated || previous.Isolated) return true;
        if (!string.Equals(previous.UserKey, fragment.UserKey, StringComparison.Ordinal)) return true;
        if (fragment.Start is null || previous.End is null || fragment.Start.Value - previous.End.Value > Gap) return true;
        // The stage the next session starts on, not the lowest stage number it contains.
        // A search session can pick up a login call a minute later; that call must not
        // hide the fact that the session began at search.
        if (previous.MaxStage >= fragment.StartStage) return true;
        return previous.HasBook;
    }

    private static TelemetryJourneyRow Combine(List<Fragment> visit)
    {
        var keeper = visit
            .OrderByDescending(f => f.MaxStage)
            .ThenByDescending(f => f.Start ?? DateTimeOffset.MinValue)
            .ThenByDescending(f => f.CorrelationId, StringComparer.Ordinal)
            .First();

        var stages = CollapseStages(visit.SelectMany(f => f.Stages));
        var stageNames = stages
            .Select(stage => stage.Str("stage"))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();
        var lastStep = stages.Count == 0
            ? visit.Max(f => f.MaxStage)
            : stages.Max(stage => stage.Int("stageNo"));
        var bookOk = visit.Sum(f => f.Row.GetInt("BookOk"));
        var bookFail = visit.Sum(f => f.Row.GetInt("BookFail"));
        var failedCalls = visit.Sum(f => f.Row.GetInt("FailedCalls"));
        var reservations = visit
            .SelectMany(f => ReservationIds(f.Row))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var distinctOk = reservations.Count > 0
            ? reservations.Count
            : visit.Sum(f => f.Row.GetInt("DistinctOkRes"));
        var hasCancel = stages.Any(stage => stage.Int("stageNo") == 9);
        var hasConfirm = stages.Any(stage => stage.Int("stageNo") == 8);
        var hasPurchase = stages.Any(stage => stage.Int("stageNo") == 7);
        var start = visit.Min(f => f.Start);
        var end = visit.Max(f => f.End);
        var searchAt = StageTime(stages, 2);
        var confirmAt = StageTime(stages, 8);

        var row = new TelemetryJourneyRow();
        foreach (var pair in keeper.Row.Extra)
        {
            row.Extra[pair.Key] = pair.Value;
        }

        row.Extra["CorrelationId"] = Json(keeper.CorrelationId);
        row.Extra["Outcome"] = Json(Outcome(bookOk, bookFail, distinctOk, hasCancel, hasConfirm, hasPurchase, lastStep));
        row.Extra["LastStep"] = Json(lastStep);
        row.Extra["BookOk"] = Json(bookOk);
        row.Extra["BookFail"] = Json(bookFail);
        row.Extra["DistinctOkRes"] = Json(distinctOk);
        row.Extra["BookedReservations"] = Json(reservations);
        row.Extra["FailedCalls"] = Json(failedCalls);
        row.Extra["Exceptions"] = Json(failedCalls);
        row.Extra["IsBookSuccessful"] = Json(bookOk > 0);
        row.Extra["PassedSearch"] = Json(stages.Any(stage => stage.Int("stageNo") == 2) || visit.Any(f => f.Row.GetBool("PassedSearch")));
        row.Extra["CallsNoCorrelation"] = Json(visit.Sum(f => f.Row.GetInt("CallsNoCorrelation")));
        row.Extra["CallsViaCorrelation"] = Json(visit.Sum(f => f.Row.GetInt("CallsViaCorrelation")));
        row.Extra["Journey"] = Json(stages);
        row.Extra["Steps"] = Json(stageNames);
        SetNumber(row, "JourneyTotalMs", Sum(visit, "JourneyTotalMs"));
        SetNumber(row, "SlowestCallMs", Max(visit, "SlowestCallMs"));
        SetNumber(row, "OverallP95", Max(visit, "OverallP95"));
        if (searchAt is not null && confirmAt is not null)
        {
            row.Extra["SearchToConfirm_s"] = Json((int)(confirmAt.Value - searchAt.Value).TotalSeconds);
        }
        else
        {
            row.Extra["SearchToConfirm_s"] = JsonNull;
        }

        if (start is not null) row.Extra["FirstSeen"] = Json(start.Value.ToString("O", CultureInfo.InvariantCulture));
        if (end is not null) row.Extra["LastSeen"] = Json(end.Value.ToString("O", CultureInfo.InvariantCulture));

        var failReason = visit
            .Where(f => !string.IsNullOrEmpty(f.Row.GetString("FailReason")))
            .OrderByDescending(f => f.End ?? DateTimeOffset.MinValue)
            .Select(f => f.Row.GetString("FailReason"))
            .FirstOrDefault();
        row.Extra["FailReason"] = string.IsNullOrEmpty(failReason) ? JsonNull : Json(failReason);

        var absorbed = visit
            .Where(f => !string.Equals(f.CorrelationId, keeper.CorrelationId, StringComparison.Ordinal))
            .OrderBy(f => f.Start ?? DateTimeOffset.MaxValue)
            .Select(f => f.CorrelationId)
            .ToList();
        row.Extra[AbsorbedIdsProperty] = Json(absorbed);
        return row;
    }

    private static string Outcome(int bookOk, int bookFail, int distinctOk, bool cancel, bool confirm, bool purchase, int lastStep)
    {
        var multi = distinctOk > 1 ? $" x{distinctOk}" : "";
        var retry = bookOk > 0 && bookFail > 0
            ? $" after {bookFail} failed attempt{(bookFail > 1 ? "s" : "")}"
            : "";
        if (bookOk > 0 && cancel) return $"Booked{multi}{retry}; cancellation activity";
        if (bookOk > 0 && confirm) return $"Booked{multi}{retry}";
        if (bookOk > 0) return $"Booked{multi}{retry}, no confirmation page";
        if (bookFail > 0) return $"Purchase failed x{bookFail}";
        if (cancel) return "Cancellation activity";
        if (purchase) return "Purchase attempted, no result";
        return $"Dropped at step {lastStep}";
    }

    private static List<JsonElement> CollapseStages(IEnumerable<JsonElement> stages)
    {
        return stages
            .Select((stage, index) => (stage, index))
            .OrderBy(item => item.stage.Int("stageNo"))
            .ThenBy(item => item.index)
            .GroupBy(item => item.stage.Int("stageNo"))
            .Select(group =>
            {
                var items = group.Select(item => item.stage).ToList();
                return items.Count == 1 ? items[0] : MergeStage(items);
            })
            .ToList();
    }

    private static JsonElement MergeStage(List<JsonElement> stages)
    {
        var calls = stages.SelectMany(stage => stage.ArrayOrObjectValues("calls")).ToList();
        var callCount = stages.Sum(stage => stage.Int("callCount"));
        if (callCount == 0) callCount = calls.Count;
        var bag = new Dictionary<string, object?>
        {
            ["stageNo"] = stages[0].Int("stageNo"),
            ["stage"] = stages.Select(stage => stage.Str("stage")).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? "",
            ["t"] = StageTime(stages, stages[0].Int("stageNo"))?.ToString("O", CultureInfo.InvariantCulture),
            ["callCount"] = callCount,
            ["stageTotalMs"] = SumStage(stages, "stageTotalMs"),
            ["p50Ms"] = MaxStage(stages, "p50Ms"),
            ["p90Ms"] = MaxStage(stages, "p90Ms"),
            ["p95Ms"] = MaxStage(stages, "p95Ms"),
            ["p99Ms"] = MaxStage(stages, "p99Ms"),
            ["maxMs"] = MaxStage(stages, "maxMs"),
            ["failureRatePct"] = MaxStage(stages, "failureRatePct"),
            ["status"] = WorstStatus(stages),
            ["calls"] = calls,
        };
        return Json(bag);
    }

    private static string WorstStatus(List<JsonElement> stages)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["RED"] = 3,
            ["YELLOW"] = 2,
            ["GREEN"] = 1,
            ["N/A"] = 0,
        };
        return stages
            .Select(stage => stage.Str("status"))
            .OrderByDescending(status => rank.TryGetValue(status, out var value) ? value : 0)
            .FirstOrDefault() ?? "N/A";
    }

    private static DateTimeOffset? StageTime(IEnumerable<JsonElement> stages, int stageNo)
    {
        DateTimeOffset? best = null;
        foreach (var stage in stages)
        {
            if (stage.Int("stageNo") != stageNo) continue;
            if (!TryParseTime(stage.Str("t"), out var parsed)) continue;
            if (best is null || parsed < best) best = parsed;
        }

        return best;
    }

    private static List<string> ReservationIds(TelemetryJourneyRow row)
    {
        var element = row.Get("BookedReservations");
        if (element is null) return [];
        IEnumerable<JsonElement> values = element.Value.ValueKind switch
        {
            JsonValueKind.Array => element.Value.EnumerateArray(),
            JsonValueKind.Object => element.Value.EnumerateObject().Select(property => property.Value),
            JsonValueKind.String => [element.Value],
            _ => [],
        };
        return values
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToList();
    }

    private static double? Sum(List<Fragment> visit, string name)
    {
        double? sum = null;
        foreach (var fragment in visit)
        {
            var value = fragment.Row.GetDouble(name);
            if (value is null) continue;
            sum = (sum ?? 0) + value.Value;
        }

        return sum is null ? null : Math.Round(sum.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static double? Max(List<Fragment> visit, string name)
    {
        double? max = null;
        foreach (var fragment in visit)
        {
            var value = fragment.Row.GetDouble(name);
            if (value is null) continue;
            max = max is null ? value : Math.Max(max.Value, value.Value);
        }

        return max is null ? null : Math.Round(max.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static double? SumStage(List<JsonElement> stages, string name)
    {
        double? sum = null;
        foreach (var stage in stages)
        {
            var value = stage.Dbl(name);
            if (value is null) continue;
            sum = (sum ?? 0) + value.Value;
        }

        return sum is null ? null : Math.Round(sum.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static double? MaxStage(List<JsonElement> stages, string name)
    {
        double? max = null;
        foreach (var stage in stages)
        {
            var value = stage.Dbl(name);
            if (value is null) continue;
            max = max is null ? value : Math.Max(max.Value, value.Value);
        }

        return max is null ? null : Math.Round(max.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static void SetNumber(TelemetryJourneyRow row, string name, double? value)
    {
        row.Extra[name] = value is null ? JsonNull : Json(value.Value);
    }

    private static bool TryParseTime(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static readonly JsonElement JsonNull = ParseNull();

    private static JsonElement ParseNull()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private sealed class Fragment
    {
        public required TelemetryJourneyRow Row { get; init; }
        public required string UserKey { get; init; }
        public required string CorrelationId { get; init; }
        public DateTimeOffset? Start { get; init; }
        public DateTimeOffset? End { get; init; }
        public int StartStage { get; init; }
        public int MaxStage { get; init; }
        public bool HasBook { get; init; }
        public bool Isolated { get; init; }
        public required List<JsonElement> Stages { get; init; }

        public static Fragment Read(TelemetryJourneyRow row, int index)
        {
            var stages = row.GetJourneyStages();
            var start = row.GetDateTimeOffset("FirstSeen");
            var end = row.GetDateTimeOffset("LastSeen");
            foreach (var stage in stages)
            {
                Note(stage.Str("t"), ref start, ref end);
                foreach (var call in stage.ArrayOrObjectValues("calls"))
                {
                    Note(call.Str("t"), ref start, ref end);
                }
            }

            if (start is null) start = end;
            if (end is null) end = start;

            var stageNos = stages.Select(stage => stage.Int("stageNo")).Where(number => number > 0).ToList();
            var lastStep = row.GetInt("LastStep");
            var maxStage = stageNos.Count == 0 ? lastStep : Math.Max(stageNos.Max(), lastStep);
            var startStage = StartStageOf(stages, stageNos.Count == 0 ? lastStep : stageNos.Min());
            var userId = row.GetString("UserId");
            var correlationId = row.GetString("CorrelationId");
            var isolated = string.IsNullOrEmpty(userId)
                || string.IsNullOrEmpty(correlationId)
                || start is null;

            return new Fragment
            {
                Row = row,
                UserKey = isolated ? $"\u0000{index}" : userId,
                CorrelationId = correlationId,
                Start = start,
                End = end,
                StartStage = startStage,
                MaxStage = maxStage,
                HasBook = row.GetInt("BookOk") > 0 || row.GetBool("IsBookSuccessful"),
                Isolated = isolated,
                Stages = stages,
            };
        }

        private static int StartStageOf(List<JsonElement> stages, int fallback)
        {
            var earliest = stages
                .Select(stage => (Number: stage.Int("stageNo"), At: StageInstant(stage)))
                .Where(stage => stage.Number > 0 && stage.At is not null)
                .OrderBy(stage => stage.At)
                .ThenBy(stage => stage.Number)
                .Select(stage => (int?)stage.Number)
                .FirstOrDefault();
            return earliest ?? fallback;
        }

        private static DateTimeOffset? StageInstant(JsonElement stage)
        {
            if (TryParseTime(stage.Str("t"), out var parsed)) return parsed;
            DateTimeOffset? earliest = null;
            foreach (var call in stage.ArrayOrObjectValues("calls"))
            {
                if (!TryParseTime(call.Str("t"), out var callAt)) continue;
                if (earliest is null || callAt < earliest) earliest = callAt;
            }

            return earliest;
        }

        private static void Note(string text, ref DateTimeOffset? start, ref DateTimeOffset? end)
        {
            if (!TryParseTime(text, out var parsed)) return;
            if (start is null || parsed < start) start = parsed;
            if (end is null || parsed > end) end = parsed;
        }
    }
}
