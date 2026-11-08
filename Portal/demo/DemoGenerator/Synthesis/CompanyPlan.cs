using DemoGenerator.Archetypes;

namespace DemoGenerator.Synthesis;

/// <summary>One scenario running over [Start, End), both whole UTC minutes.</summary>
public sealed record ActiveIncident(string ScenarioKey, DateTimeOffset Start, DateTimeOffset End)
{
    public bool Overlaps(DateTimeOffset from, DateTimeOffset to) => Start < to && End > from;
}

public sealed record PlannedException(string Type, string Message, string Method);

public sealed record PlannedDependency(int GlobalIndex, string Partner, string Target, string Type, string Name, double Mu, double Sigma, double FailureRate);

public sealed record PlannedMeasurement(string Name, double Min, double Max);

public sealed record PlannedStep(
    int Index,
    string Name,
    string Url,
    double CumulativeWeight,
    double Mu,
    double Sigma,
    double ErrorRate,
    IReadOnlyList<PlannedException> Exceptions,
    IReadOnlyList<double> ExceptionCumulative,
    int FirstDependency,
    IReadOnlyList<PlannedDependency> Dependencies,
    string? BusinessEvent,
    bool BusinessEventOnSuccessOnly,
    IReadOnlyList<PlannedMeasurement> Measurements,
    bool EmitsPageView,
    int SessionGroup = 0,
    int SessionPosition = 0,
    int SessionLaneStride = 1,
    int SessionLaneOffset = 0);

/// <summary>Everything the synthesis needs for one app of one company at one scale, precomputed once.</summary>
public sealed class AppPlan
{
    public required ArchetypeApp App { get; init; }
    public required string CompanyKey { get; init; }
    public required long Seed { get; init; }
    public required IReadOnlyList<PlannedStep> Steps { get; init; }
    public required IReadOnlyList<PlannedDependency> Dependencies { get; init; }

    /// <summary>Expected requests per minute for each UTC hour on a weekday (already scaled and weighted by the app's share).</summary>
    public required IReadOnlyList<double> RatePerMinuteByHour { get; init; }

    public required double WeekendFactor { get; init; }
    public required string EntityDimension { get; init; }
    public required IReadOnlyList<string> EntityIds { get; init; }

    /// <summary>Per entity rank; null when the archetype has no host template.</summary>
    public required IReadOnlyList<string>? EntityHosts { get; init; }

    public required double Zipf { get; init; }
    public required string IKey { get; init; }
    public required long OperationXorMinute { get; init; }
    public required long OperationXorIndex { get; init; }

    /// <summary>The company's seed: session ids hash it, so a session can span the company's apps.</summary>
    public required long SessionSeed { get; init; }

    public required long SessionXorMinute { get; init; }

    public int MaxDependencies => Steps.Count == 0 ? 0 : Steps.Max(s => s.Dependencies.Count);

    public string Database => App.Database!;
    public string AppId => App.AppId!;
    public string RoleName => App.RoleName!;

    /// <summary>0-based entity rank for a uniform draw: inverse CDF of a continuous Zipf (bounded Pareto) on [1, N+1).</summary>
    public int EntityRank(double u)
    {
        var n = EntityIds.Count;
        double x;
        if (Zipf == 0) x = 1 + u * n;
        else if (Zipf == 1) x = Math.Exp(u * Math.Log(n + 1.0));
        else x = Math.Pow(1 + u * (Math.Pow(n + 1.0, 1 - Zipf) - 1), 1 / (1 - Zipf));
        var rank = (long)Math.Floor(x) - 1;
        return (int)Math.Clamp(rank, 0, n - 1);
    }

    /// <summary>The probability that a request belongs to one of <paramref name="ranks"/>.</summary>
    public double EntityShare(IEnumerable<int> ranks)
    {
        var n = EntityIds.Count;
        double Cdf(double x) => Zipf switch
        {
            0 => (x - 1) / n,
            1 => Math.Log(x) / Math.Log(n + 1.0),
            _ => (Math.Pow(x, 1 - Zipf) - 1) / (Math.Pow(n + 1.0, 1 - Zipf) - 1),
        };
        return ranks.Distinct().Sum(r => Cdf(r + 2.0) - Cdf(r + 1.0));
    }
}

/// <summary>A company's archetype compiled for one seed and scale.</summary>
public sealed class CompanyPlan
{
    /// <summary>Probability that a successful GET step also produces a page view.</summary>
    public const double PageViewRate = 0.2;

    /// <summary>Probability that a successful request also writes an informational trace.</summary>
    public const double SuccessTraceRate = 0.05;

    public required Archetype Archetype { get; init; }
    public required string Scale { get; init; }
    public required long Seed { get; init; }
    public required IReadOnlyList<AppPlan> Apps { get; init; }

    public string CompanyKey => Archetype.CompanyKey!;

    public ScenarioModel? FindScenario(string key) =>
        (Archetype.Scenarios ?? []).FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// The historical incidents anchored at <paramref name="resetAt"/>: start = whole minute
    /// <c>hoursAgo</c> before it, end = start + duration.
    /// </summary>
    public IReadOnlyList<ActiveIncident> HistoricalIncidents(DateTimeOffset resetAt)
    {
        var anchor = SynthesisClock.FloorMinute(resetAt);
        return (Archetype.HistoricalIncidents ?? [])
            .Select(h =>
            {
                var start = SynthesisClock.FloorMinute(anchor - TimeSpan.FromHours(h.HoursAgo));
                return new ActiveIncident(h.Scenario!, start, start + TimeSpan.FromMinutes(h.DurationMinutes));
            })
            .ToList();
    }

    /// <summary>z of the 95th percentile of the standard normal distribution.</summary>
    public const double Z95 = 1.6448536269514722;

    /// <param name="sessions">Flow positions of the steps (<see cref="SessionFlows.Load"/>); null = every request its own session.</param>
    public static CompanyPlan Build(Archetype archetype, string scale, long baseSeed, SessionFlows? sessions = null)
    {
        sessions ??= SessionFlows.Unclaimed(archetype);
        var factor = Scales.Factor(scale);
        var companySeed = DeterministicRandom.Mix(baseSeed % DeterministicRandom.M + DeterministicRandom.Fnv(archetype.CompanyKey!));
        var steps = archetype.Steps!;
        var totalWeight = steps.Sum(s => s.Weight);
        var curve = archetype.Traffic!.HourlyCurve!;
        var curveSum = curve.Sum();
        var entityIds = ArchetypeLoader.EntityIds(archetype.Entities);
        var hostTemplate = archetype.Entities!.HostTemplate;
        var lanes = SessionLanes(archetype, sessions);

        var apps = new List<AppPlan>();
        foreach (var app in archetype.Apps!)
        {
            var appSteps = steps.Where(s => string.Equals(s.App, app.AppId, StringComparison.OrdinalIgnoreCase)).ToList();
            var appWeight = appSteps.Sum(s => s.Weight);
            var appSeed = DeterministicRandom.Mix(companySeed + DeterministicRandom.Fnv(app.AppId!));

            var planned = new List<PlannedStep>();
            var deps = new List<PlannedDependency>();
            var cumulative = 0.0;
            for (var i = 0; i < appSteps.Count; i++)
            {
                var s = appSteps[i];
                var slot = sessions.SlotOf(IndexOf(steps, s));
                var lane = lanes.TryGetValue((slot.Group, slot.Position), out var sharing)
                    ? (Stride: sharing.Count, Offset: sharing.IndexOf(app.AppId!))
                    : (Stride: 1, Offset: 0);
                cumulative += s.Weight / appWeight;
                var (mu, sigma) = LogNormal(s.P50Ms, s.P95Ms);

                var exceptions = (s.Exceptions ?? []).Select(e => new PlannedException(e.Type!, e.Message!, e.Method ?? "")).ToList();
                var exWeights = (s.Exceptions ?? []).Select(e => e.Weight).ToList();
                var exTotal = exWeights.Sum();
                var exCumulative = new List<double>();
                var acc = 0.0;
                foreach (var w in exWeights) { acc += w / exTotal; exCumulative.Add(acc); }

                var first = deps.Count;
                var stepDeps = new List<PlannedDependency>();
                foreach (var d in s.Dependencies ?? [])
                {
                    var (dmu, dsigma) = LogNormal(d.P50Ms, d.P95Ms);
                    var pd = new PlannedDependency(deps.Count, d.Partner!, d.Target!, d.Type!, d.Name!, dmu, dsigma, d.FailureRate);
                    deps.Add(pd);
                    stepDeps.Add(pd);
                }

                planned.Add(new PlannedStep(
                    i, s.Name!, s.Url!, i == appSteps.Count - 1 ? 1.0 : cumulative, mu, sigma, s.ErrorRate,
                    exceptions, exCumulative, first, stepDeps,
                    s.BusinessEvent?.Name, s.BusinessEvent?.OnSuccessOnly ?? true,
                    (s.BusinessEvent?.Measurements ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new PlannedMeasurement(kv.Key, kv.Value.Min, kv.Value.Max)).ToList(),
                    IsPageStep(s),
                    slot.Group,
                    slot.Position,
                    lane.Stride,
                    lane.Offset));
            }

            var share = totalWeight > 0 ? appWeight / totalWeight : 0;
            var rates = curve.Select(w => archetype.Traffic.RequestsPerDay * factor * share * w / (60.0 * curveSum)).ToList();

            apps.Add(new AppPlan
            {
                App = app,
                CompanyKey = archetype.CompanyKey!,
                Seed = appSeed,
                Steps = planned,
                Dependencies = deps,
                RatePerMinuteByHour = rates,
                WeekendFactor = archetype.Traffic.WeekendFactor,
                EntityDimension = archetype.Entities.Dimension!,
                EntityIds = entityIds,
                EntityHosts = hostTemplate is null ? null : entityIds.Select(id => hostTemplate.Replace("{0}", id, StringComparison.Ordinal)).ToList(),
                Zipf = archetype.Entities.Zipf,
                IKey = DeterministicGuid(appSeed),
                OperationXorMinute = DeterministicRandom.Mix(appSeed + 7),
                OperationXorIndex = DeterministicRandom.Mix(appSeed + 11),
                SessionSeed = companySeed,
                SessionXorMinute = DeterministicRandom.Mix(companySeed + 13),
            });
        }

        return new CompanyPlan { Archetype = archetype, Scale = scale, Seed = companySeed, Apps = apps };
    }

    /// <summary>
    /// Per flow step, the apps (in archetype order) with a step classified into it. Each app ranks its own
    /// requests, so rank r of app i becomes session lane r * apps + i: two apps never put a request of the
    /// same flow step into one session.
    /// </summary>
    private static Dictionary<(int Group, int Position), List<string>> SessionLanes(Archetype archetype, SessionFlows sessions)
    {
        var steps = archetype.Steps!;
        var lanes = new Dictionary<(int, int), List<string>>();
        foreach (var app in archetype.Apps!)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (!string.Equals(steps[i].App, app.AppId, StringComparison.OrdinalIgnoreCase)) continue;
                var slot = sessions.SlotOf(i);
                if (!lanes.TryGetValue((slot.Group, slot.Position), out var apps)) lanes[(slot.Group, slot.Position)] = apps = [];
                if (!apps.Contains(app.AppId!, StringComparer.Ordinal)) apps.Add(app.AppId!);
            }
        }
        return lanes;
    }

    /// <summary>mu and sigma of a log-normal whose median is p50 and whose 95th percentile is p95.</summary>
    public static (double Mu, double Sigma) LogNormal(double p50, double p95) =>
        (Math.Log(p50), Math.Log(p95 / p50) / Z95);

    /// <summary>
    /// A step a browser navigates to, so it also produces page views: a GET whose path is not an API call
    /// (<c>/api/</c>) nor a data file (<c>.json</c>, <c>.xml</c>).
    /// </summary>
    public static bool IsPageStep(StepModel step)
    {
        if (step.Name is null || !step.Name.StartsWith("GET ", StringComparison.OrdinalIgnoreCase)) return false;
        var path = (step.Url ?? "").Split('?')[0];
        return !path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOf(IReadOnlyList<StepModel> steps, StepModel step)
    {
        for (var i = 0; i < steps.Count; i++) if (ReferenceEquals(steps[i], step)) return i;
        throw new InvalidOperationException($"Step '{step.Name}' is not a step of the archetype.");
    }

    private static string DeterministicGuid(long seed)
    {
        var h = string.Concat(Enumerable.Range(1, 4).Select(i => DeterministicRandom.Hex8(DeterministicRandom.Mix(seed + i))));
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..32]}";
    }
}

public static class SynthesisClock
{
    public static DateTimeOffset FloorMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    public static long EpochMinute(DateTimeOffset value) => (long)(FloorMinute(value) - DateTimeOffset.UnixEpoch).TotalMinutes;

    public static DateTimeOffset FromEpochMinute(long minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);
}
