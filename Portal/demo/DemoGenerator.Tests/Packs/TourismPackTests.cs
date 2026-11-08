using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoGenerator.Archetypes;
using DemoGenerator.Packs;
using DemoGenerator.Synthesis;
using Xunit.Abstractions;

namespace DemoGenerator.Tests.Packs;

public partial class TourismPackTests(ITestOutputHelper output)
{
    private static readonly string[] ArriviaAliases = ["sor-frontend", "club", "cruise-core", "cruise-api", "air-ui", "air-api"];

    /// <summary>Corpus dates must fall in the 18 months before the pack was written (DT4T, 2026-11-07).</summary>
    private static readonly DateOnly PackWritten = new(2026, 11, 7);

    [Fact]
    public void TourismPack_IsValid()
    {
        var errors = PackValidator.Validate(PackDir);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        var archetype = ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));
        Assert.Equal("tourism", archetype.CompanyKey);
        Assert.Equal("arrivia", archetype.ProfileKey);
        Assert.Equal("Arrivia (demo)", archetype.DisplayName);
        Assert.Equal("VacationClubId", archetype.Entities!.Dimension);
        Assert.Equal("club", archetype.Entities.Kind);

        var sources = Read<List<SourceDocument>>("sources.json");
        Assert.Equal(ArriviaAliases.Order(), sources.Select(s => s.Alias!).Order());

        var profile = Read<ProfileDocument>("profile.json");
        var air = Assert.Single(profile.Services!, s => s!.Key == "air");
        Assert.Equal(new[] { "air-ui", "air-api" }, air!.TelemetrySourceAliases!);
        foreach (var vertical in new[] { "cruises", "cars", "hotels", "shared" })
            Assert.Contains(profile.Services!, s => s!.Key == vertical);

        // The platform classifies a request with the FIRST step (global order across services) whose matcher hits:
        // every archetype step must land on a service that reads the app's alias.
        var rules = profile.Services!
            .SelectMany(s => s!.Flows!.SelectMany(f => f!.Steps!.Select(t => (Service: s, Step: t!))))
            .OrderBy(r => r.Step.Order)
            .ToList();
        foreach (var step in archetype.Steps!)
        {
            var alias = sources.Single(s => s.AppId == step.App).Alias!;
            var url = "https://club9001.travel-demo.example" + step.Url;
            var hit = rules.FirstOrDefault(r => r.Step.Matchers!.Any(m => PackValidator.StepMatches(m!, url, step.Name!)));
            Assert.True(hit.Service is not null, $"step {step.Name} matches no profile stage");
            Assert.True(hit.Service!.TelemetrySourceAliases!.Contains(alias),
                $"step {step.Name} ({alias}) is classified first as {hit.Service.Key}/{hit.Step.Key}, a service that does not read {alias}");
        }
    }

    [Fact]
    public void TourismPack_HasScenarioLibrary()
    {
        var archetype = ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));
        var scenarios = archetype.Scenarios!.ToDictionary(s => s.Key!);
        Assert.True(scenarios.Count >= 4);
        foreach (var key in new[] { "cruise-pool-exhaustion", "air-search-outage", "hotel-prebook-exceptions", "club-degraded", "partner-bookings-failing", "silent-bookings-drop", "holiday-sale-peak" })
            Assert.True(scenarios.ContainsKey(key), $"missing scenario {key}");

        var steps = archetype.Steps!.ToDictionary(s => s.Name!);

        // Cruise Core pool exhaustion: 05 Total Price above 100 s at the median, pool-timeout exceptions.
        var pool = scenarios["cruise-pool-exhaustion"].Effects!;
        var totalPrice = steps["POST /cruisecabin.do"];
        var factor = pool.Single(e => e.Kind == EffectKinds.LatencyMultiplier && e.Step == totalPrice.Name).Factor!.Value;
        Assert.True(totalPrice.P50Ms * factor > 100_000, $"Total Price p50 under the scenario is {totalPrice.P50Ms * factor} ms");
        Assert.Contains(pool, e => e.Kind == EffectKinds.ErrorRate && e.Step == totalPrice.Name && e.ExceptionType == "System.InvalidOperationException");
        Assert.Contains(totalPrice.Exceptions!, x => x.Type == "System.InvalidOperationException" && x.Message!.Contains("pool", StringComparison.Ordinal));

        // NG Air search outage: a partner outage on the air fare provider.
        Assert.Contains(scenarios["air-search-outage"].Effects!, e => e.Kind == EffectKinds.DependencyFailure && e.Partner == "air-gds");

        // Exception storm on exactly the Hotel PreBook step.
        var prebook = scenarios["hotel-prebook-exceptions"].Effects!.Single(e => e.Kind == EffectKinds.ErrorRate);
        Assert.Equal("POST /hotels/book.json", prebook.Step);
        Assert.Contains(steps[prebook.Step!].Exceptions!, x => x.Type == prebook.ExceptionType);

        // One club degraded.
        var club = scenarios["club-degraded"].Effects!;
        var only = Assert.Single(club, e => e.Kind == EffectKinds.EntityOnly);
        Assert.Equal(new[] { "9002" }, only.Entities!);
        Assert.Contains(club, e => e.Kind == EffectKinds.LatencyMultiplier);

        // Business drop: only traffic reductions on steps that emit business events, nothing fails.
        var drop = scenarios["silent-bookings-drop"].Effects!;
        Assert.All(drop, e => Assert.Equal(EffectKinds.TrafficMultiplier, e.Kind));
        Assert.Contains(drop, e => e.Factor < 1 && steps[e.Step!].BusinessEvent is not null);

        // Legitimate peak: one unscoped traffic multiplier above 1 and nothing else.
        var peak = Assert.Single(scenarios["holiday-sale-peak"].Effects!);
        Assert.Equal(EffectKinds.TrafficMultiplier, peak.Kind);
        Assert.Null(peak.Step);
        Assert.True(peak.Factor > 1);

        // Every step and dependency belongs to one of the six Arrivia apps; the Air vertical is present.
        Assert.Equal(6, archetype.Apps!.Count);
        Assert.Contains(archetype.Steps!, s => s.App == "demo-tourism-air-api" && s.BusinessEvent?.Name == "FlightBooked");
        Assert.All(archetype.Scenarios!, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
    }

    [Fact]
    public void TourismPack_HasHistoricalIncidentsAndBusinessKpis()
    {
        var archetype = ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));
        var incidents = archetype.HistoricalIncidents!;
        var recent = incidents.Where(i => i.HoursAgo <= 72).ToList();
        Assert.True(recent.Count >= 3, $"only {recent.Count} historical incidents within the last 3 days");
        Assert.True(recent.Select(i => i.Scenario).Distinct().Count() >= 3);
        Assert.All(incidents, i => Assert.True(i.DurationMinutes >= 15, $"{i.Scenario} lasts {i.DurationMinutes} min: too short for 2 consecutive 5-min buckets"));
        // Historical incidents must not overlap, so each one is its own story in the history.
        var windows = incidents.Select(i => (Start: -i.HoursAgo * 60, End: -i.HoursAgo * 60 + i.DurationMinutes)).OrderBy(w => w.Start).ToList();
        for (var i = 1; i < windows.Count; i++) Assert.True(windows[i].Start >= windows[i - 1].End, "historical incidents overlap");

        var kpis = Read<List<KpiDocument>>("kpis.json");
        Assert.All(kpis, k =>
        {
            Assert.Equal("arrivia", k.ProfileKey);
            Assert.Equal("00:05:00", k.BucketSize);
            Assert.Equal("00:01:00", k.EvaluationFrequency);
            Assert.True(k.Enabled);
        });

        var events = archetype.Steps!.Where(s => s.BusinessEvent is not null).Select(s => s.BusinessEvent!.Name!).ToHashSet();
        var business = kpis.Where(k => k.Pillar == "Business").ToList();
        Assert.True(business.Count >= 2);
        foreach (var kpi in business)
        {
            Assert.StartsWith("kql:kpi:", kpi.SourceKey, StringComparison.Ordinal);
            Assert.Equal("Decrease", kpi.Direction);
            Assert.Contains("customEvents", kpi.KqlText!, StringComparison.Ordinal);
            Assert.Contains(events, e => kpi.KqlText!.Contains($"\"{e}\"", StringComparison.Ordinal));
        }
        foreach (var verticalEvent in new[] { "BookingCompleted", "CruiseBooked", "FlightBooked" })
            Assert.Contains(business, k => k.KqlText!.Contains(verticalEvent, StringComparison.Ordinal));

        foreach (var service in new[] { "cruises", "cars", "hotels", "shared", "air" })
            foreach (var grain in new[] { "domain", "entity" })
            {
                Assert.Contains(kpis, k => k.Domain == service && k.Grain == grain && k.SourceKey == "appinsights:latency" && k.Aggregation == "P95");
                Assert.Contains(kpis, k => k.Domain == service && k.Grain == grain && k.SourceKey == "appinsights:exceptions");
            }
    }

    [Fact]
    public void TourismPack_CorpusMatchesScenarios()
    {
        var archetype = ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));
        var scenarios = archetype.Scenarios!.ToDictionary(s => s.Key!);
        var files = Directory.GetFiles(Path.Combine(PackDir, "corpus"), "*.md", SearchOption.AllDirectories);
        Assert.True(files.Length >= 10, $"{files.Length} RCAs");

        var covered = new HashSet<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            Assert.StartsWith("# Root Cause Analysis - ", text, StringComparison.Ordinal);
            foreach (var section in new[] { "## Bottom Line Up Front", "## What Happened", "## 5 Whys", "## Corrective Actions" })
                Assert.Contains(section, text, StringComparison.Ordinal);
            Assert.Contains("synthetic", text, StringComparison.Ordinal);

            var date = DateOnly.ParseExact(DateLine().Match(text).Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.InRange(date, PackWritten.AddMonths(-18), PackWritten);
            Assert.StartsWith(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), name, StringComparison.Ordinal);

            var key = ScenarioLine().Match(text).Groups["key"].Value;
            Assert.True(scenarios.TryGetValue(key, out var scenario), $"{name}: scenario '{key}' is not in the archetype");
            covered.Add(key);

            // Coherent with its scenario: names a step, partner or exception type that the scenario touches.
            var effects = scenario!.Effects!;
            var touched = effects.Where(e => e.Step is not null).Select(e => e.Step!)
                .Concat(effects.Where(e => e.Partner is not null).SelectMany(e => archetype.Steps!.Where(s => (s.Dependencies ?? []).Any(d => d.Partner == e.Partner)).Select(s => s.Name!)))
                .ToHashSet();
            var mentions = touched.Any(s => text.Contains(s, StringComparison.Ordinal))
                || effects.Any(e => e.Partner is not null && text.Contains(e.Partner, StringComparison.Ordinal))
                || effects.Any(e => e.Entities is not null && e.Entities.All(id => text.Contains(id, StringComparison.Ordinal)))
                || (touched.Count == 0 && effects.All(e => e.Kind == EffectKinds.TrafficMultiplier && e.Step is null));
            Assert.True(mentions, $"{name} does not mention a step, partner or club of scenario '{key}'");
            foreach (var type in effects.Where(e => e.ExceptionType is not null).Select(e => e.ExceptionType!).Distinct())
                Assert.True(text.Contains(type.Split('.')[^1], StringComparison.Ordinal), $"{name} does not name exception {type}");
        }

        foreach (var key in scenarios.Keys) Assert.True(covered.Contains(key), $"no RCA for scenario '{key}'");
    }

    [Fact]
    public void TourismPack_ContainsNoRealIdentifiers()
    {
        foreach (var file in Directory.GetFiles(PackDir, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(EmailPattern(), text);
            foreach (Match guid in GuidPattern().Matches(text))
                Assert.True(guid.Value == "00000000-0000-0000-0000-000000000000", $"{Path.GetFileName(file)} contains a non-placeholder GUID {guid.Value}");
            foreach (Match host in HostPattern().Matches(text))
                Assert.True(host.Value.EndsWith(".example", StringComparison.Ordinal) || host.Value.EndsWith(".internal", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names host {host.Value}; demo hosts end in .example or .internal");
        }
    }

    // ------------------------------------------------------------------ detection replay
    // The pack's KPIs are replayed over 8 days of the generator's in-memory reference model (the same formulas the
    // Kusto synthesis runs) for a sweep of reset times, aggregated the way the platform's KPI ingestion stores them
    // (including the domain-grain latency sampling and the zero-filled business buckets) and evaluated per bucket with
    // a minimal re-implementation of the platform's three detection methods (see ReplayDetector).

    /// <summary>The reset of the iteration-4 sweep whose holiday peak tail used to trigger Shared Latency P95.</summary>
    private static readonly DateTimeOffset PeakTailReset = new(2026, 11, 12, 22, 35, 0, TimeSpan.Zero);

    /// <summary>
    /// The reset sweep: every 7 h 05 min from Saturday 2026-11-07 02:25Z (24 resets, one in each hour of the day, over
    /// 6.8 days) plus Thursday 2026-11-12 01:15Z and Friday 2026-11-13 23:40Z, whose historical air outage falls in
    /// the night trough (05:15Z / 03:40Z), and <see cref="PeakTailReset"/>.
    /// </summary>
    private static readonly DateTimeOffset[] ReplayResets =
    [
        .. Enumerable.Range(0, 24).Select(k => new DateTimeOffset(2026, 11, 7, 2, 25, 0, TimeSpan.Zero).AddMinutes(425 * k)),
        new(2026, 11, 12, 1, 15, 0, TimeSpan.Zero),
        new(2026, 11, 13, 23, 40, 0, TimeSpan.Zero),
        PeakTailReset,
    ];

    /// <summary>The README promises a detection only if it fires at this share of the sweep's resets.</summary>
    private const double PromisedHitRate = 0.95;

    /// <summary>The reset whose per-KPI table the test prints (and the README reproduces).</summary>
    private static readonly DateTimeOffset ReportedReset = new(2026, 11, 12, 1, 15, 0, TimeSpan.Zero);

    /// <summary>
    /// Scenarios that are not historical incidents are measured live, as the README sweep does: each one alone,
    /// started 15 min after the reset for the minutes the demo uses, evaluated with a fresh detection state from
    /// 6 h before its start to 60 min after its end (the 7-day history still serves as baseline).
    /// </summary>
    private static readonly (string Scenario, int Minutes)[] ReplayLiveScenarios =
        [("payment-declines", 30), ("silent-bookings-drop", 40), ("cluster-node-loss", 30)];

    private static readonly TimeSpan LiveStartDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LivePreWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan LiveTail = TimeSpan.FromMinutes(60);

    private const string ClubDegradedEntity = "9002";
    private const string ClubDegradedPromise = "club-grain Exceptions name club 9002 on >= 2 verticals and no other club";

    /// <summary>
    /// KPIs (pillar/kpi/domain[/stage]/grain) the README promises for each non-peak scenario: each must fire at
    /// <see cref="PromisedHitRate"/> of the resets. club-degraded is checked separately (<see cref="ClubDegradedPromise"/>).
    /// </summary>
    private static readonly Dictionary<string, string[]> IntendedDetections = new()
    {
        ["cruise-pool-exhaustion"] = ["Application/Latency/cruises/domain", "Application/Exceptions/cruises/domain"],
        ["air-search-outage"] = ["Application/Latency/air/domain", "Application/Exceptions/air/domain", "Application/Exceptions/air/entity", "Business/Flight bookings/air/domain"],
        ["hotel-prebook-exceptions"] = ["Application/Exceptions/hotels/05-prebook/domain", "Application/Exceptions/hotels/domain", "Business/Hotel pre-books/hotels/domain"],
        ["partner-bookings-failing"] = ["Application/Exceptions/shared/domain", "Business/Hotel and car bookings/shared/domain", "Business/Booking value/shared/domain"],
        ["payment-declines"] = ["Application/Exceptions/shared/domain"],
        ["silent-bookings-drop"] = ["Business/Hotel and car bookings/shared/domain", "Business/Booking value/shared/domain", "Business/Cruise bookings/cruises/domain", "Business/Flight bookings/air/domain"],
        ["cluster-node-loss"] = ["Application/Latency/cruises/domain", "Application/Exceptions/cruises/domain"],
    };

    /// <summary>What one reset produced: promises it kept, hard failures (peak, false rate, silent drop) and its table.</summary>
    private sealed record ReplayOutcome(DateTimeOffset Reset, HashSet<string> Kept, List<string> Failures, string Report);

    [Fact]
    public void TourismPack_ReplayDetectsScenariosAndIgnoresPeak()
    {
        var archetype = ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));
        var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000);
        var kpis = Read<List<KpiDocument>>("kpis.json");
        var model = ReplayModel.Create(plan, Read<ProfileDocument>("profile.json"), Read<List<SourceDocument>>("sources.json"), kpis);

        // One incident-free telemetry for the whole span; each reset regenerates only the hours its incidents touch
        // (the synthesis is deterministic per minute, so every other hour is identical).
        var baseline = model.Generate(ReplayResets.Min().AddDays(-8), ReplayResets.Max().AddHours(3));
        var outcomes = new ReplayOutcome[ReplayResets.Length];
        Parallel.For(0, ReplayResets.Length, n => outcomes[n] = ReplayAt(ReplayResets[n], plan, archetype, model, baseline, kpis));

        var failures = outcomes.SelectMany(o => o.Failures).ToList();
        var report = new StringBuilder();
        var promises = IntendedDetections.SelectMany(kv => kv.Value.Select(label => kv.Key + " -> " + label)).Append("club-degraded -> " + ClubDegradedPromise);
        foreach (var promise in promises)
        {
            var missed = outcomes.Where(o => !o.Kept.Contains(promise)).Select(o => o.Reset.ToString("u", CultureInfo.InvariantCulture)).ToList();
            var rate = 1 - missed.Count / (double)outcomes.Length;
            report.Append(CultureInfo.InvariantCulture, $"{promise}: {outcomes.Length - missed.Count}/{outcomes.Length}")
                .Append(missed.Count == 0 ? "" : " missed at " + string.Join(", ", missed)).Append('\n');
            if (rate < PromisedHitRate) failures.Add($"{promise} fires at {rate:P0} of the resets (< {PromisedHitRate:P0}); missed at {string.Join(", ", missed)}");
        }
        report.Append('\n').Append(outcomes.Single(o => o.Reset == ReportedReset).Report);

        output.WriteLine(report.ToString());
        var peakTriggers = outcomes.SelectMany(o => o.Failures).Count(f => f.Contains("holiday-sale-peak", StringComparison.Ordinal));
        Assert.True(peakTriggers == 0, $"{peakTriggers} triggers during holiday-sale-peak (+2 h) over {outcomes.Length} resets, including {PeakTailReset:u}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures) + Environment.NewLine + report);
    }

    private static ReplayOutcome ReplayAt(DateTimeOffset reset, CompanyPlan plan, Archetype archetype, ReplayModel model, ReplayTelemetry baseline, List<KpiDocument> kpis)
    {
        var incidents = plan.HistoricalIncidents(reset).ToList();
        Assert.Equal(archetype.Scenarios!.Select(s => s.Key!).Order(), incidents.Select(i => i.ScenarioKey).Concat(ReplayLiveScenarios.Select(l => l.Scenario)).Order());

        var telemetry = model.WithIncidents(baseline, incidents);
        var fromBucket = telemetry.BucketOf(reset.AddDays(-8));
        var toBucket = telemetry.BucketOf(reset);

        bool InWindow(int bucket, ActiveIncident i)
        {
            var t = telemetry.From.AddMinutes(5 * bucket);
            var tail = i.ScenarioKey == "holiday-sale-peak" ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(30);
            return t >= i.Start.AddMinutes(-5) && t < i.End + tail;
        }

        string Attribute(int bucket) => incidents.FirstOrDefault(i => InWindow(bucket, i))?.ScenarioKey ?? "";

        // Live scenarios: triggers inside each one's window, per KPI label. Each is applied in place and undone, so
        // the historical telemetry below is unchanged.
        var live = new Dictionary<string, Dictionary<string, List<string>>>();
        foreach (var (scenario, minutes) in ReplayLiveScenarios)
        {
            var start = reset + LiveStartDelay;
            var incident = new ActiveIncident(scenario, start, start.AddMinutes(minutes));
            var undo = model.ApplyInPlace(telemetry, [incident]);
            var evaluateFrom = telemetry.BucketOf(start - LivePreWindow);
            var evaluateTo = telemetry.BucketOf(incident.End + LiveTail);
            live[scenario] = kpis.ToDictionary(ReplayLabel, kpi => ReplayDetector
                .Run(kpi, telemetry.Series(kpi, fromBucket, evaluateTo), evaluateFrom, evaluateTo, fromBucket)
                .Triggers.Where(t => InWindow(t.Bucket, incident)).Select(t => t.Entity).ToList());
            undo();
        }

        var failures = new List<string>();
        var triggers = new Dictionary<string, List<(string Entity, string Scenario)>>();
        var report = new StringBuilder();
        report.Append("reset ").Append(reset.ToString("u", CultureInfo.InvariantCulture)).Append('\n')
            .Append("KPI | evaluated | ").Append(string.Join(" | ", incidents.Select(i => i.ScenarioKey).Concat(live.Keys.Select(k => k + " (live)"))))
            .Append(" | false/day\n");
        foreach (var kpi in kpis)
        {
            var label = ReplayLabel(kpi);
            var result = ReplayDetector.Run(kpi, telemetry.Series(kpi, fromBucket, toBucket), fromBucket, toBucket);
            var attributed = result.Triggers.Select(t => (t.Entity, Scenario: Attribute(t.Bucket))).ToList();
            var falsePerDay = attributed.Count(t => t.Scenario == "") / Math.Max(1.0, result.EvaluatedBuckets / 288.0);
            triggers[label] = [.. attributed, .. live.SelectMany(kv => kv.Value[label].Select(e => (Entity: e, Scenario: kv.Key)))];
            report.Append(label).Append(" | ").Append(result.EvaluatedBuckets);
            foreach (var scenario in incidents.Select(i => i.ScenarioKey).Concat(live.Keys))
            {
                var hits = triggers[label].Where(t => t.Scenario == scenario).ToList();
                report.Append(" | ").Append(hits.Count);
                if (kpi.Grain == "entity" && hits.Count > 0) report.Append('[').Append(string.Join(",", hits.Select(h => h.Entity).Distinct())).Append(']');
            }
            report.Append(" | ").Append(falsePerDay.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');

            if (falsePerDay > 2) failures.Add($"{reset:u} {label}: {falsePerDay:0.00} false triggers/day");
            // Every series counts, so a single club firing during the peak fails too.
            foreach (var hit in attributed.Where(t => t.Scenario == "holiday-sale-peak"))
                failures.Add($"{reset:u} {label} fires during the legitimate holiday-sale-peak{(hit.Entity == "" ? "" : " for club " + hit.Entity)}");
        }

        var kept = new HashSet<string>();
        foreach (var (scenario, expected) in IntendedDetections)
            foreach (var label in expected.Where(l => triggers[l].Any(t => t.Scenario == scenario)))
                kept.Add(scenario + " -> " + label);

        // One club degraded: club-grain Exceptions name that club on at least two verticals, and no club-grain
        // trigger in its window names another club.
        var clubTriggers = triggers.Where(kv => kv.Key.EndsWith("/entity", StringComparison.Ordinal))
            .SelectMany(kv => kv.Value.Where(t => t.Scenario == "club-degraded").Select(t => (Kpi: kv.Key, t.Entity))).ToList();
        if (clubTriggers.All(t => t.Entity == ClubDegradedEntity)
            && clubTriggers.Where(t => t.Kpi.StartsWith("Application/Exceptions/", StringComparison.Ordinal)).Select(t => t.Kpi).Distinct().Count() >= 2)
            kept.Add("club-degraded -> " + ClubDegradedPromise);

        // Silent drop: only the business KPIs move, at every reset.
        failures.AddRange(triggers.Where(kv => kv.Key.StartsWith("Application/", StringComparison.Ordinal) && kv.Value.Any(t => t.Scenario == "silent-bookings-drop"))
            .Select(kv => $"{reset:u} silent-bookings-drop triggers {kv.Key}"));
        return new ReplayOutcome(reset, kept, failures, report.ToString());
    }

    private static string ReplayLabel(KpiDocument k) =>
        $"{k.Pillar}/{k.Kpi}/{k.Domain}{(string.IsNullOrEmpty(k.Stage) ? "" : "/" + k.Stage)}/{k.Grain}";

    /// <summary>One stored KPI bucket as the platform sees it: TotalCount, Value and P95 (null when the source has none).</summary>
    private readonly record struct ReplayBucket(long TotalCount, double? Value, double? P95);

    private sealed record ReplayResult(List<(string Entity, int Bucket)> Triggers, int EvaluatedBuckets);

    /// <summary>
    /// How requests become KPI rows, as the platform's KPI templates group them: service (and stage) from the
    /// profile's step matchers (first match in global step order, only for services that read the app's alias),
    /// entity from VacationClubId, exceptions = failed requests (the generator writes exactly one exception per failed
    /// request, sharing its operation_Id), business events by source alias and event name.
    /// </summary>
    private sealed class ReplayModel
    {
        private CompanyPlan _plan = null!;
        private readonly Dictionary<(string App, int Step), (string Service, string Stage)?> _keys = [];
        private readonly Dictionary<string, string> _alias = [];
        private readonly HashSet<string> _seriesKeys = [];
        private readonly HashSet<string> _eventKeys = [];

        /// <summary>Series keys a latency KPI reads, with its samples per bucket (0 = every request) and sampling seed.</summary>
        private readonly Dictionary<string, (int PerBucket, long Seed)> _p95Plan = [];

        public static ReplayModel Create(CompanyPlan plan, ProfileDocument profile, List<SourceDocument> sources, List<KpiDocument> kpis)
        {
            var m = new ReplayModel { _plan = plan };
            var rules = profile.Services!
                .SelectMany(s => s!.Flows!.SelectMany(f => f!.Steps!.Select(step => (Service: s, Step: step!))))
                .OrderBy(r => r.Step.Order)
                .ToList();
            foreach (var app in plan.Apps)
            {
                var alias = m._alias[app.AppId] = sources.Single(s => s.AppId == app.AppId).Alias!;
                foreach (var step in app.Steps)
                {
                    var url = "https://" + (app.EntityHosts?[0] ?? app.App.Host) + step.Url;
                    var hit = rules.FirstOrDefault(r => r.Step.Matchers!.Any(x => PackValidator.StepMatches(x!, url, step.Name)));
                    var key = hit.Service is not null && hit.Service.TelemetrySourceAliases!.Contains(alias)
                        ? (hit.Service.Key!, hit.Step.Key!) : ((string, string)?)null;
                    m._keys[(app.AppId, step.Index)] = key;
                    if (key is var (service, stage))
                    {
                        m._seriesKeys.Add(service);
                        m._seriesKeys.Add(service + "/" + stage);
                        foreach (var entity in app.EntityIds) m._seriesKeys.Add(service + "#" + entity);
                    }
                    if (step.BusinessEvent is { } name) m._eventKeys.Add(alias + "|" + name);
                }
            }
            foreach (var kpi in kpis.Where(k => k.SourceKey == "appinsights:latency"))
            {
                if (kpi.Grain == "entity")
                {
                    foreach (var entity in plan.Apps[0].EntityIds) m._p95Plan[kpi.Domain + "#" + entity] = (0, 0);
                    continue;
                }
                var key = kpi.Domain + (string.IsNullOrEmpty(kpi.Stage) ? "" : "/" + kpi.Stage);
                if (SamplesPerBucket(kpi) is var perBucket and > 0) m._p95Plan[key] = (perBucket, DeterministicRandom.Fnv(kpi.Domain + "|" + key));
            }
            return m;
        }

        public ReplayTelemetry Generate(DateTimeOffset from, DateTimeOffset to)
        {
            var t = new ReplayTelemetry(from, (int)((to - from).TotalMinutes / 5), _plan.Apps[0].EntityIds, to);
            foreach (var key in _seriesKeys) t.AddSeries(key);
            foreach (var key in _eventKeys) t.AddEvents(key);
            Fill(t, [], Slices(from, to));
            return t;
        }

        /// <summary>
        /// A copy of <paramref name="baseline"/> whose buckets inside <paramref name="incidents"/> are regenerated with
        /// their effects (effects act per minute inside [Start, End) and every row stays in its minute).
        /// </summary>
        public ReplayTelemetry WithIncidents(ReplayTelemetry baseline, IReadOnlyList<ActiveIncident> incidents)
        {
            var t = baseline.Clone();
            ApplyInPlace(t, incidents);
            return t;
        }

        /// <summary>
        /// Regenerates the buckets of <paramref name="t"/> inside <paramref name="incidents"/> with their effects, in
        /// place; the returned action puts the previous buckets back.
        /// </summary>
        public Action ApplyInPlace(ReplayTelemetry t, IReadOnlyList<ActiveIncident> incidents)
        {
            var windows = new List<(int From, int To)>();
            foreach (var (from, to) in incidents
                .Select(i => (From: Math.Max(0, t.BucketOf(i.Start)), To: Math.Min(t.Buckets, t.BucketOf(i.End.AddMinutes(5).AddTicks(-1)) + 1)))
                .Where(w => w.From < w.To).OrderBy(w => w.From))
            {
                if (windows.Count > 0 && from <= windows[^1].To) windows[^1] = (windows[^1].From, Math.Max(windows[^1].To, to));
                else windows.Add((from, to));
            }
            var restore = new List<Action>();
            var slices = new List<(DateTimeOffset, DateTimeOffset)>();
            foreach (var (from, to) in windows)
            {
                restore.Add(t.Snapshot(from, to));
                t.Clear(from, to);
                slices.AddRange(Slices(t.From.AddMinutes(5 * from), t.From.AddMinutes(5 * to)));
            }
            Fill(t, incidents, slices);
            return () => restore.ForEach(r => r());
        }

        /// <summary>[from, to) cut into slices of at most one hour.</summary>
        private static List<(DateTimeOffset Start, DateTimeOffset End)> Slices(DateTimeOffset from, DateTimeOffset to)
        {
            var slices = new List<(DateTimeOffset, DateTimeOffset)>();
            for (var h = from; h < to; h = h.AddHours(1)) slices.Add((h, h.AddHours(1) < to ? h.AddHours(1) : to));
            return slices;
        }

        // Slices are disjoint bucket ranges of the preallocated arrays, so they can be generated in parallel.
        private void Fill(ReplayTelemetry t, IReadOnlyList<ActiveIncident> incidents, List<(DateTimeOffset Start, DateTimeOffset End)> slices)
        {
            var effects = _plan.Apps.ToDictionary(a => a.AppId, a => EffectCompiler.Compile(_plan, a, incidents));
            Parallel.ForEach(slices, slice =>
            {
                var (h, end) = slice;
                var durations = new Dictionary<(string Key, int Bucket), List<double>>();
                void Collect(string key, int b, double duration)
                {
                    if (_p95Plan.ContainsKey(key)) (durations.TryGetValue((key, b), out var l) ? l : durations[(key, b)] = []).Add(duration);
                }
                foreach (var app in _plan.Apps)
                {
                    var alias = _alias[app.AppId];
                    var batch = ReferenceSynthesizer.Generate(app, effects[app.AppId], h, end);
                    foreach (var r in batch.Requests)
                    {
                        if (_keys[(app.AppId, r.StepIndex)] is not { } k) continue;
                        var b = t.BucketOf(r.Timestamp);
                        var entityKey = k.Service + "#" + r.Entity;
                        t.Record(k.Service, b, r.Success);
                        t.Record(k.Service + "/" + k.Stage, b, r.Success);
                        t.Record(entityKey, b, r.Success);
                        Collect(k.Service, b, r.Duration);
                        Collect(entityKey, b, r.Duration);
                    }
                    foreach (var e in batch.CustomEvents)
                        t.RecordEvent(alias + "|" + e.Name, t.BucketOf(e.Timestamp), e.Measurements.TryGetValue("Amount", out var amount) ? amount : 0);
                }
                // Only the P95 is kept, so the telemetry of every reset stays small.
                foreach (var ((key, b), values) in durations)
                {
                    var (perBucket, seed) = _p95Plan[key];
                    t.SetP95(key, b, Percentile(perBucket == 0 ? values : Sample(values, perBucket, seed + b)));
                }
            });
        }
    }

    /// <summary>Per-bucket aggregates of the reference model; each bucket is written by one generation task only.</summary>
    private sealed class ReplayTelemetry(DateTimeOffset from, int buckets, IReadOnlyList<string> entities, DateTimeOffset to)
    {
        public DateTimeOffset From { get; } = from;
        public DateTimeOffset To { get; } = to;
        public int Buckets { get; } = buckets;
        private Dictionary<string, double[]> _p95 = [];
        private Dictionary<string, int[]> _requests = [];
        private Dictionary<string, int[]> _failed = [];
        private Dictionary<string, int[]> _events = [];
        private Dictionary<string, double[]> _amounts = [];

        public void AddSeries(string key)
        {
            _p95[key] = new double[Buckets];
            Array.Fill(_p95[key], double.NaN);
            _requests[key] = new int[Buckets];
            _failed[key] = new int[Buckets];
        }

        public void AddEvents(string key)
        {
            _events[key] = new int[Buckets];
            _amounts[key] = new double[Buckets];
        }

        public ReplayTelemetry Clone() => new(From, Buckets, entities, To)
        {
            _p95 = _p95.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone()),
            _requests = _requests.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone()),
            _failed = _failed.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone()),
            _events = _events.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone()),
            _amounts = _amounts.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone()),
        };

        /// <summary>Copies buckets [fromBucket, toBucket) of every series; the returned action writes them back.</summary>
        public Action Snapshot(int fromBucket, int toBucket)
        {
            static Action Keep<T>(Dictionary<string, T[]> arrays, int from, int to)
            {
                var copies = arrays.Values.Select(a => (Array: a, Copy: a[from..to])).ToList();
                return () => copies.ForEach(c => c.Copy.CopyTo(c.Array, from));
            }
            var all = new[]
            {
                Keep(_p95, fromBucket, toBucket), Keep(_requests, fromBucket, toBucket), Keep(_failed, fromBucket, toBucket),
                Keep(_events, fromBucket, toBucket), Keep(_amounts, fromBucket, toBucket),
            };
            return () => Array.ForEach(all, a => a());
        }

        public void Clear(int fromBucket, int toBucket)
        {
            var n = toBucket - fromBucket;
            foreach (var a in _p95.Values) Array.Fill(a, double.NaN, fromBucket, n);
            foreach (var a in _requests.Values) Array.Clear(a, fromBucket, n);
            foreach (var a in _failed.Values) Array.Clear(a, fromBucket, n);
            foreach (var a in _events.Values) Array.Clear(a, fromBucket, n);
            foreach (var a in _amounts.Values) Array.Clear(a, fromBucket, n);
        }

        public int BucketOf(DateTimeOffset ts) => (int)((ts - From).Ticks / TimeSpan.TicksPerMinute / 5);

        public void Record(string key, int b, bool success)
        {
            _requests[key][b]++;
            if (!success) _failed[key][b]++;
        }

        public void SetP95(string key, int b, double p95) => _p95[key][b] = p95;

        public void RecordEvent(string key, int b, double amount)
        {
            _events[key][b]++;
            _amounts[key][b] += amount;
        }

        /// <summary>
        /// Entity key ("" at domain grain) to the stored buckets of one KPI in [from, to); a bucket with no row (or
        /// outside the range) is null.
        /// </summary>
        public Dictionary<string, Func<int, ReplayBucket?>> Series(KpiDocument kpi, int from, int to)
        {
            var result = new Dictionary<string, Func<int, ReplayBucket?>>();
            bool InRange(int b) => b >= from && b < to;
            if (kpi.SourceKey!.StartsWith("kql:", StringComparison.Ordinal))
            {
                // Business KQL: Value = count or sum(Amount). A zero-filled query (range of bins left-joined with the
                // events) has a row for every bucket, 0 when empty; a plain summarize only for buckets with events.
                var text = kpi.KqlText!;
                var alias = Regex.Match(text, @"\{\{SOURCE:(?<a>[^}]+)\}\}").Groups["a"].Value;
                var name = Regex.Match(text, "name == \"(?<n>[^\"]+)\"").Groups["n"].Value;
                var sum = text.Contains("sum(todouble(customMeasurements.Amount))", StringComparison.Ordinal);
                var zeroFilled = text.Contains("range BucketStart", StringComparison.Ordinal) && text.Contains("join kind=leftouter", StringComparison.Ordinal);
                var (count, amount) = (_events[alias + "|" + name], _amounts[alias + "|" + name]);
                result[""] = b => !InRange(b) || (count[b] == 0 && !zeroFilled) ? null : new ReplayBucket(count[b], sum ? amount[b] : count[b], null);
                return result;
            }

            var latency = kpi.SourceKey == "appinsights:latency";
            var keys = kpi.Grain == "entity"
                ? entities.Select(e => (Entity: e, Key: kpi.Domain + "#" + e))
                : [(Entity: "", Key: kpi.Domain + (string.IsNullOrEmpty(kpi.Stage) ? "" : "/" + kpi.Stage))];
            foreach (var (entity, key) in keys)
            {
                var (requests, failed, p95) = (_requests[key], _failed[key], _p95[key]);
                result[entity] = b => !InRange(b) || requests[b] == 0 ? null
                    // Latency: TotalCount = requests; P95 of every request of the club (entity grain, the template's
                    // percentile()) or of the bucket's stored samples (domain grain), none without samples.
                    : latency ? new ReplayBucket(requests[b], null, double.IsNaN(p95[b]) ? null : p95[b])
                    // Exceptions: TotalCount = exceptions, Value = exceptions as a percentage of requests.
                    : new ReplayBucket(failed[b], 100.0 * failed[b] / requests[b], null);
            }
            return result;
        }
    }

    /// <summary>
    /// Portal/src/JourneyMonitor.Core/Monitoring/Ingestion/KpiSampling.cs SamplesPerBucket (as called by
    /// KpiIngestionService): SampleSize spread over the buckets of the lookback window,
    /// ceil(SampleSize / max(1, LookbackWindow / BucketSize)); 100000 over 1 day of 5-min buckets = 348.
    /// </summary>
    private static int SamplesPerBucket(KpiDocument kpi)
    {
        if ((kpi.SampleSize ?? 0) <= 0) return 0;
        var lookback = TimeSpan.Parse(kpi.LookbackWindow!, CultureInfo.InvariantCulture);
        var bucket = TimeSpan.Parse(kpi.BucketSize!, CultureInfo.InvariantCulture);
        return (int)Math.Ceiling(kpi.SampleSize!.Value / Math.Max(1d, lookback / bucket));
    }

    /// <summary>
    /// Domain-grain latency samples as the platform stores them: Portal/src/JourneyMonitor.Infrastructure/Kql/
    /// kpi-latency.kql.template.txt runs <c>partition hint.strategy=native by BucketStart (sample {{perBucket}})</c>
    /// (every row when the bucket has fewer, otherwise perBucket rows drawn uniformly at random; seeded here so the
    /// test is reproducible), KpiBucketAssembler keeps at most perBucket of them and KpiBucketStatistics.Build takes
    /// the P95 of those samples only.
    /// </summary>
    private static List<double> Sample(List<double> values, int perBucket, long seed)
    {
        if (values.Count <= perBucket) return values;
        var pool = values.ToArray();
        var random = new Random(unchecked((int)DeterministicRandom.Mix(seed)));
        for (var i = 0; i < perBucket; i++)
        {
            var j = random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.Take(perBucket).ToList();
    }

    /// <summary>P95 by PERCENTILE.INC, as JourneyMonitor.Core KpiBucketStatistics.Build/Percentile.</summary>
    private static double Percentile(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var rank = 0.95 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        return sorted[lower] + (rank - lower) * (sorted[(int)Math.Ceiling(rank)] - sorted[lower]);
    }

    /// <summary>
    /// Minimal, faithful re-implementation of the platform's per-series rule (the demo must not reference platform
    /// projects). Mirrors Portal/src/JourneyMonitor.Core/Monitoring/Kpi/KpiEvaluator.cs and Methods/
    /// (StaticThresholdMethod, BaselinePercentMethod, RobustZScoreMethod, KpiStatistics) as run by the live daemons
    /// (PillarHealthMonitorService / EntityDetectionMonitor): every stored bucket is evaluated in order with a
    /// continuous detection state; buckets under minObservationsPerBucket or without a reference are not evaluated
    /// and leave the consecutive counter unchanged; a normal bucket resets it; an anomalous bucket inside the
    /// cooldown is suppressed; the consecutiveCount-th anomalous bucket triggers and starts the cooldown.
    /// </summary>
    private static class ReplayDetector
    {
        private const int BucketsPerDay = 288;

        /// <summary>
        /// Evaluates buckets [from, to) with a fresh state; buckets from <paramref name="historyFrom"/> (default
        /// <paramref name="from"/>) serve as history.
        /// </summary>
        public static ReplayResult Run(KpiDocument kpi, Dictionary<string, Func<int, ReplayBucket?>> series, int from, int to, int? historyFrom = null)
        {
            var oldest = historyFrom ?? from;
            var raw = kpi.MethodParamsJson!.Value;
            using var parameters = JsonDocument.Parse(raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText());
            var p = parameters.RootElement;
            double Param(string name) => p.GetProperty(name).GetDouble();
            var method = kpi.DetectionMethod!;
            var increase = kpi.Direction == "Increase";
            var either = kpi.Direction == "Either";
            var minObs = kpi.MinObservationsPerBucket ?? 0;
            var consecutive = kpi.ConsecutiveCount ?? 1;
            var cooldownBuckets = (kpi.CooldownMinutes ?? 0) / 5.0;

            double? Observed(ReplayBucket? b) => b is not { } x ? null : kpi.Aggregation switch
            {
                "P95" => x.P95,
                "Count" => x.TotalCount,
                "Value" => x.Value,
                _ => throw new NotSupportedException(kpi.Aggregation),
            };

            static double Median(List<double> v)
            {
                v.Sort();
                return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
            }

            var triggers = new List<(string, int)>();
            var evaluated = new bool[to - from];
            foreach (var (entity, s) in series)
            {
                var count = 0;
                var cooldownUntil = double.NegativeInfinity;
                for (var b = from; b < to; b++)
                {
                    if (s(b) is not { } bucket || bucket.TotalCount < minObs || Observed(bucket) is not { } value) continue;

                    double deviation;
                    if (method == "StaticThreshold")
                    {
                        deviation = value - Param("threshold");
                    }
                    else if (method == "BaselinePercent")
                    {
                        var days = (int)Param("lookbackDays");
                        var baseline = new List<double>();
                        for (var k = 1; k <= days; k++)
                            if (b - k * BucketsPerDay >= oldest && Observed(s(b - k * BucketsPerDay)) is { } h) baseline.Add(h);
                        if (baseline.Count < Math.Min(3, days)) continue;
                        var reference = Median(baseline);
                        if (reference <= 0) continue;
                        deviation = (value - reference) / reference * 100.0;
                    }
                    else
                    {
                        var window = (int)Param("windowBuckets");
                        var values = new List<double>();
                        for (var k = 1; k <= window; k++)
                            if (b - k >= oldest && Observed(s(b - k)) is { } h) values.Add(h);
                        if (values.Count < (window + 1) / 2) continue;
                        var median = Median(values);
                        var scale = 1.4826 * Median(values.Select(v => Math.Abs(v - median)).ToList());
                        if (scale == 0) continue;
                        deviation = (value - median) / scale;
                    }

                    evaluated[b - from] = true;
                    var threshold = method switch { "StaticThreshold" => 0, "BaselinePercent" => Param("thresholdPercent"), _ => Param("zThreshold") };
                    var anomalous = method == "StaticThreshold"
                        ? (increase ? deviation > 0 : deviation < 0)
                        : either ? Math.Abs(deviation) > threshold : increase ? deviation > threshold : deviation < -threshold;
                    if (!anomalous) { count = 0; continue; }

                    count++;
                    if (cooldownUntil > b + 1) continue;
                    if (count >= consecutive)
                    {
                        triggers.Add((entity, b));
                        cooldownUntil = b + 1 + cooldownBuckets;
                    }
                }
            }
            return new ReplayResult(triggers, evaluated.Count(e => e));
        }
    }

    private static string PackDir { get; } = FindPackDir();

    private static string FindPackDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Portal", "demo", "packs", "tourism");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "demo", "packs", "tourism");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Portal/demo/packs/tourism not found above " + AppContext.BaseDirectory);
    }

    private static T Read<T>(string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(PackDir, file)), ArchetypeLoader.JsonOptions)!;

    [GeneratedRegex(@"^\*\*Date:\*\* (?<d>\d{4}-\d{2}-\d{2})", RegexOptions.Multiline)]
    private static partial Regex DateLine();

    [GeneratedRegex(@"^\*\*Scenario \(demo\):\*\* `(?<key>[a-z0-9-]+)`", RegexOptions.Multiline)]
    private static partial Regex ScenarioLine();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|net|org|io|example|internal)\b")]
    private static partial Regex HostPattern();
}
