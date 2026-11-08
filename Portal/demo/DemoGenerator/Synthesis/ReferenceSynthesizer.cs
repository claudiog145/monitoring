using DemoGenerator.Archetypes;
using static DemoGenerator.Synthesis.DeterministicRandom;

namespace DemoGenerator.Synthesis;

public sealed record SyntheticRequest(
    DateTimeOffset Timestamp, string Id, string Name, string Url, string ResultCode, double Duration, bool Success,
    string OperationId, string Entity, int StepIndex, int EntityRank, bool OwnFailure, int DependencyFailureMask,
    long SessionHash = 0, long SessionTag = 0)
{
    /// <summary>session_Id, built on read so synthesis allocates no session strings (<see cref="ReferenceSynthesizer.SessionKey"/>).</summary>
    public string SessionId => ReferenceSynthesizer.SessionIdOf(SessionHash, SessionTag);

    public string UserId => ReferenceSynthesizer.UserIdOf(SessionHash);
}

public sealed record SyntheticDependency(
    DateTimeOffset Timestamp, string Id, string Partner, string Target, string Type, string Name, bool Success,
    string ResultCode, double Duration, string OperationId, string ParentId);

public sealed record SyntheticException(DateTimeOffset Timestamp, string Type, string Message, string Method, string OperationId, string ParentId, string Entity);

public sealed record SyntheticTrace(DateTimeOffset Timestamp, string Message, int SeverityLevel, string OperationId);

public sealed record SyntheticPageView(DateTimeOffset Timestamp, string Name, string Url, double Duration, string OperationId);

public sealed record SyntheticEvent(DateTimeOffset Timestamp, string Name, string OperationId, string Entity, IReadOnlyDictionary<string, double> Measurements);

public sealed class SyntheticBatch
{
    public List<SyntheticRequest> Requests { get; } = [];
    public List<SyntheticDependency> Dependencies { get; } = [];
    public List<SyntheticException> Exceptions { get; } = [];
    public List<SyntheticTrace> Traces { get; } = [];
    public List<SyntheticPageView> PageViews { get; } = [];
    public List<SyntheticEvent> CustomEvents { get; } = [];
}

/// <summary>
/// The executable specification of the synthesis. <see cref="KqlSynthesisBuilder"/> emits the same formulas
/// as KQL so the rows are generated inside Kusto; this class computes them in memory, which is what the unit
/// tests measure (determinism, latency percentiles, shared operation_Id, scenario effects, volumes).
/// Channel numbers and formulas must stay in lockstep with the builder.
/// </summary>
public static class ReferenceSynthesizer
{
    public const int ChannelTimestamp = 0, ChannelStep = 1, ChannelEntity = 2, ChannelZ1 = 3, ChannelZ2 = 4, ChannelError = 5,
        ChannelException = 6, ChannelKeep = 7, ChannelTrace = 8, ChannelPageView = 9, ChannelClient = 10, ChannelMeasurement = 20,
        ChannelDependency = 100, ChannelCount = 999;

    public static readonly IReadOnlyList<string> Countries = ["United States", "Argentina", "Brazil", "Mexico", "Chile", "Colombia", "Spain", "Peru"];

    public const string DependencyFailureException = "System.Net.Http.HttpRequestException";

    /// <summary>The expected request rate of one minute (before stochastic rounding and thinning).</summary>
    public static double MinuteRate(AppPlan app, IReadOnlyList<AppliedEffect> effects, DateTimeOffset minute)
    {
        var weekend = minute.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var rate = app.RatePerMinuteByHour[minute.Hour] * (weekend ? app.WeekendFactor : 1.0);
        foreach (var e in effects) if (e.IsGlobalTraffic && e.IsActive(minute)) rate *= e.Factor;
        return rate;
    }

    /// <summary>Requests created in a minute: floor of the rate plus one with probability of its fraction.</summary>
    public static long MinuteCount(AppPlan app, IReadOnlyList<AppliedEffect> effects, long epochMinute)
    {
        var r = MinuteRate(app, effects, SynthesisClock.FromEpochMinute(epochMinute));
        var ms = Mix(app.Seed + epochMinute);
        var floor = Math.Floor(r);
        return (long)floor + (U(ms, ChannelCount) < r - floor ? 1 : 0);
    }

    public static SyntheticBatch Generate(AppPlan app, IReadOnlyList<AppliedEffect> effects, DateTimeOffset from, DateTimeOffset to)
    {
        var batch = new SyntheticBatch();
        var m0 = SynthesisClock.EpochMinute(from);
        var m1 = SynthesisClock.EpochMinute(to);
        var slots = SessionRankSlots(app);
        var ranks = RankBuffer.For(app.Steps.Count);
        for (var m = m0; m < m1; m++)
        {
            var n = MinuteCount(app, effects, m);
            var ms = Mix(app.Seed + m);
            var mts = SynthesisClock.FromEpochMinute(m);
            ranks.NextMinute();
            for (long k = 0; k < n; k++)
            {
                var rk = Mix(ms + k);
                var step = StepOf(app, rk);
                var e = app.EntityRank(U(rk, ChannelEntity));
                // Rank among the minute's requests of the same flow step (every entity), before any thinning.
                var rank = ranks.Take(slots[step.Index]);
                GenerateRequest(app, effects, batch, m, mts, k, rk, step, e, rank);
            }
        }
        return batch;
    }

    public static PlannedStep StepOf(AppPlan app, long rk)
    {
        var u1 = U(rk, ChannelStep);
        var steps = app.Steps;
        var last = steps.Count - 1;
        for (var i = 0; i < last; i++)
        {
            if (u1 < steps[i].CumulativeWeight) return steps[i];
        }
        return steps[last];
    }

    /// <summary>
    /// Per step, the step whose session ranks it shares: the first step with the same flow group and position.
    /// Archetype steps classified into the same flow step share one ranking, so a session never holds a flow step twice.
    /// </summary>
    public static int[] SessionRankSlots(AppPlan app)
    {
        var steps = app.Steps;
        var slots = new int[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            slots[i] = i;
            for (var j = 0; j < i; j++)
            {
                if (steps[j].SessionGroup == steps[i].SessionGroup && steps[j].SessionPosition == steps[i].SessionPosition)
                {
                    slots[i] = j;
                    break;
                }
            }
        }
        return slots;
    }

    /// <summary>Per-thread counters of the current minute; a minute stamp resets them without clearing the array.</summary>
    private sealed class RankBuffer
    {
        [ThreadStatic] private static RankBuffer? _current;

        private int[] _counts = [];
        private int[] _stamps = [];
        private int _minute;

        public static RankBuffer For(int size)
        {
            var buffer = _current ??= new RankBuffer();
            if (buffer._counts.Length < size)
            {
                buffer._counts = new int[size];
                buffer._stamps = new int[size];
                buffer._minute = 0;
            }
            return buffer;
        }

        public void NextMinute()
        {
            if (++_minute == int.MaxValue)
            {
                Array.Clear(_stamps);
                _minute = 1;
            }
        }

        public int Take(int key)
        {
            if (_stamps[key] != _minute)
            {
                _stamps[key] = _minute;
                _counts[key] = 0;
            }
            return _counts[key]++;
        }
    }

    /// <summary>
    /// The session of a request: its step's flow group, the minute the session started
    /// (<paramref name="epochMinute"/> minus the step's position, so step p of a session is p minutes after
    /// its first step) and its rank among the minute's requests of that flow step, across entities. Rank r of
    /// step p continues the session of rank r of step p-1 a minute earlier, which gives an ordered funnel
    /// that thins out where a later step is less frequent. Ranking per flow step (not per flow step and
    /// entity) keeps most later steps inside a session even at night, when a per-entity rank left them
    /// alone; a session's requests can therefore carry different entities. Nothing else of the row depends on it.
    /// </summary>
    public static (string SessionId, string UserId) Session(AppPlan app, PlannedStep step, long epochMinute, long rank)
    {
        var (hash, tag) = SessionKey(app, step, epochMinute, rank);
        return (SessionIdOf(hash, tag), UserIdOf(hash));
    }

    /// <summary>The numbers <see cref="Session"/> formats: the session hash and its anchor minute tag.</summary>
    public static (long Hash, long Tag) SessionKey(AppPlan app, PlannedStep step, long epochMinute, long rank)
    {
        var anchor = epochMinute - step.SessionPosition;
        var lane = rank * step.SessionLaneStride + step.SessionLaneOffset;
        return (Mix(Mix(Mix(app.SessionSeed + step.SessionGroup) + anchor) + lane), anchor ^ app.SessionXorMinute);
    }

    public static string SessionIdOf(long hash, long tag) => Hex8(hash) + Hex8(tag);

    public static string UserIdOf(long hash) => "u" + (hash % SessionUsers).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public const long SessionUsers = 50000;

    private static void GenerateRequest(AppPlan app, IReadOnlyList<AppliedEffect> effects, SyntheticBatch batch, long m, DateTimeOffset mts, long k, long rk,
        PlannedStep step, int e, long sessionRank)
    {
        var s = step.Index;

        var keep = 1.0;
        foreach (var fx in effects) if (fx.IsThinning && fx.Matches(mts, s, e)) keep *= fx.Factor;
        if (!(U(rk, ChannelKeep) < keep)) return;

        var ts = mts.AddTicks((long)(U(rk, ChannelTimestamp) * 600_000_000.0));
        var z = Math.Sqrt(-2.0 * Math.Log(U(rk, ChannelZ1))) * Math.Cos(2.0 * Math.PI * U(rk, ChannelZ2));
        var latency = 1.0;
        foreach (var fx in effects) if (fx.Kind == EffectKinds.LatencyMultiplier && fx.Matches(mts, s, e)) latency *= fx.Factor;
        var duration = Math.Exp(step.Mu + step.Sigma * z) * latency;

        var mask = 0;
        var firstFailed = -1;
        for (var j = 0; j < step.Dependencies.Count; j++)
        {
            var d = step.Dependencies[j];
            var survive = 1.0 - d.FailureRate;
            foreach (var fx in effects)
                if (fx.Kind == EffectKinds.DependencyFailure && fx.Matches(mts, s, e) && fx.DependencyIndexes!.Contains(d.GlobalIndex)) survive *= 1.0 - fx.Rate;
            if (U(rk, ChannelDependency + 10 * j) < 1.0 - survive)
            {
                mask |= 1 << j;
                if (firstFailed < 0) firstFailed = d.GlobalIndex;
            }
        }

        var ok = 1.0 - step.ErrorRate;
        string? scenarioType = null;
        foreach (var fx in effects)
        {
            if (fx.Kind != EffectKinds.ErrorRate || !fx.Matches(mts, s, e)) continue;
            ok *= 1.0 - fx.Rate;
            scenarioType ??= fx.ExceptionType;
        }
        var own = U(rk, ChannelError) < 1.0 - ok;
        var success = !own && mask == 0;

        string exType, exMessage, exMethod;
        if (own && scenarioType is not null)
        {
            var known = step.Exceptions.FirstOrDefault(x => x.Type == scenarioType);
            exType = scenarioType;
            exMessage = known?.Message ?? ScenarioMessage(scenarioType, step.Name);
            exMethod = known?.Method ?? "";
        }
        else if (own)
        {
            var u6 = U(rk, ChannelException);
            var xi = -1;
            for (var x = 0; x < step.Exceptions.Count; x++) if (u6 < step.ExceptionCumulative[x] || x == step.Exceptions.Count - 1) { xi = x; break; }
            exType = xi < 0 ? "System.Exception" : step.Exceptions[xi].Type;
            exMessage = xi < 0 ? DefaultMessage(step.Name) : step.Exceptions[xi].Message;
            exMethod = xi < 0 ? "" : step.Exceptions[xi].Method;
        }
        else if (mask != 0)
        {
            exType = DependencyFailureException;
            exMessage = DependencyMessage(app.Dependencies[firstFailed].Target);
            exMethod = "";
        }
        else
        {
            exType = exMessage = exMethod = "";
        }

        var resultCode = success ? "200" : own ? (exType.Contains("Timeout", StringComparison.Ordinal) ? "504" : "500") : "502";
        var operationId = OperationId(app, m, k, rk);
        var requestId = Hex8(Mix(rk + 2)) + Hex8(Mix(rk + 3));
        var entity = app.EntityIds[e];
        var host = app.EntityHosts?[e] ?? app.App.Host!;
        var url = "https://" + host + step.Url;
        var (sessionHash, sessionTag) = SessionKey(app, step, m, sessionRank);

        batch.Requests.Add(new SyntheticRequest(ts, requestId, step.Name, url, resultCode, duration, success, operationId, entity, s, e, own, mask, sessionHash, sessionTag));

        for (var j = 0; j < step.Dependencies.Count; j++)
        {
            var d = step.Dependencies[j];
            var dz = Math.Sqrt(-2.0 * Math.Log(U(rk, ChannelDependency + 10 * j + 1))) * Math.Cos(2.0 * Math.PI * U(rk, ChannelDependency + 10 * j + 2));
            var dLatency = 1.0;
            foreach (var fx in effects)
                if (fx.Kind == EffectKinds.LatencyMultiplier && fx.DependencyIndexes is not null && fx.Matches(mts, s, e) && fx.DependencyIndexes.Contains(d.GlobalIndex)) dLatency *= fx.Factor;
            var dSuccess = (mask & (1 << j)) == 0;
            batch.Dependencies.Add(new SyntheticDependency(
                ts.AddMilliseconds(j), Hex8(Mix(rk + 200 + j)) + Hex8(Mix(rk + 300 + j)), d.Partner, d.Target, d.Type, d.Name,
                dSuccess, dSuccess ? "200" : "500", Math.Exp(d.Mu + d.Sigma * dz) * dLatency, operationId, requestId));
        }

        if (!success)
        {
            batch.Exceptions.Add(new SyntheticException(ts, exType, exMessage, exMethod, operationId, requestId, entity));
            batch.Traces.Add(new SyntheticTrace(ts, $"{step.Name} failed: {exType}: {exMessage}", 3, operationId));
        }
        else if (U(rk, ChannelTrace) < CompanyPlan.SuccessTraceRate)
        {
            batch.Traces.Add(new SyntheticTrace(ts, $"Handled {step.Name} in {(long)duration} ms", 1, operationId));
        }

        if (step.EmitsPageView && success && U(rk, ChannelPageView) < CompanyPlan.PageViewRate)
            batch.PageViews.Add(new SyntheticPageView(ts, step.Url, url, duration * 1.6 + 300.0, operationId));

        if (step.BusinessEvent is not null && (success || !step.BusinessEventOnSuccessOnly))
        {
            var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < step.Measurements.Count; i++)
            {
                var pm = step.Measurements[i];
                measurements[pm.Name] = pm.Min + U(rk, ChannelMeasurement + i) * (pm.Max - pm.Min);
            }
            batch.CustomEvents.Add(new SyntheticEvent(ts, step.BusinessEvent, operationId, entity, measurements));
        }
    }

    public static string OperationId(AppPlan app, long m, long k, long rk) =>
        Hex8(Mix(rk + 1)) + Hex8(m ^ app.OperationXorMinute) + Hex8(k ^ app.OperationXorIndex) + Hex8(app.Seed);

    public static string ScenarioMessage(string type, string step) => $"{type} raised while handling {step}";

    public static string DefaultMessage(string step) => $"Unhandled error while handling {step}";

    public static string DependencyMessage(string target) => $"Dependency call to {target} failed";
}
