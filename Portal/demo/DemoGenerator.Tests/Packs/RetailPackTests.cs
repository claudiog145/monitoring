using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoGenerator.Archetypes;
using DemoGenerator.Packs;
using DemoGenerator.Synthesis;

namespace DemoGenerator.Tests.Packs;

public sealed class RetailPackTests
{
    private static readonly string[] RequiredScenarios =
        ["search-latency-mlm", "payment-gateway-outage", "checkout-deploy-exceptions", "carrier-b-timeouts", "orders-drop", "hot-sale"];

    /// <summary>A UTC Monday evening: hours 18-21 have similar weights in the retail hourly curve.</summary>
    private static readonly DateTimeOffset From = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    private static string PackDir { get; } = FindPackDir();

    private static string FindPackDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packs", "retail");
            if (File.Exists(Path.Combine(candidate, "archetype.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Portal/demo/packs/retail was not found above " + AppContext.BaseDirectory);
    }

    private static Archetype Archetype() => ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));

    private static List<KpiDocument> Kpis() =>
        JsonSerializer.Deserialize<List<KpiDocument>>(File.ReadAllText(Path.Combine(PackDir, "kpis.json")), ArchetypeLoader.JsonOptions)!;

    private static SyntheticBatch Run(CompanyPlan plan, string database, string? scenario, DateTimeOffset from, DateTimeOffset to)
    {
        var app = plan.Apps.Single(a => a.Database == database);
        var incidents = scenario is null ? Array.Empty<ActiveIncident>() : [new ActiveIncident(scenario, from, to)];
        return ReferenceSynthesizer.Generate(app, EffectCompiler.Compile(plan, app, incidents), from, to);
    }

    private static double FailureRate(SyntheticBatch b) => b.Requests.Count == 0 ? 0 : b.Requests.Count(r => !r.Success) / (double)b.Requests.Count;

    [Fact]
    public void RetailPack_IsValid()
    {
        var errors = PackValidator.Validate(PackDir);

        Assert.True(errors.Count == 0, "Retail pack errors:\n" + string.Join("\n", errors));
        var a = Archetype();
        Assert.Equal("retail", a.CompanyKey);
        Assert.Equal("mercadolibre", a.ProfileKey);
        Assert.Equal("Mercado Libre (demo)", a.DisplayName);
        Assert.Equal(
            ["demo-retail-storefront", "demo-retail-search-api", "demo-retail-checkout-api", "demo-retail-payments", "demo-retail-shipping"],
            a.Apps!.Select(x => x.AppId!).ToArray());
        Assert.All(a.Apps!, x => Assert.StartsWith("retail-", x.Database, StringComparison.Ordinal));
        Assert.Equal("SiteId", a.Entities!.Dimension);
        Assert.Equal(["MCO", "MLA", "MLB", "MLC", "MLM"], a.Entities.Ids!.Order(StringComparer.Ordinal).ToArray());

        // Traffic is skewed to MLB and MLA: Zipf ranks follow the order of entities.ids.
        var plan = CompanyPlan.Build(a, Scales.Laptop, 1000);
        var search = Run(plan, "retail-search-api", null, From, From.AddHours(2));
        var bySite = search.Requests.GroupBy(r => r.Entity).ToDictionary(g => g.Key, g => g.Count() / (double)search.Requests.Count);
        Assert.True(bySite["MLB"] > bySite["MLA"] && bySite["MLA"] > bySite["MLM"] && bySite["MLM"] > bySite["MLC"], string.Join(", ", bySite));
        Assert.True(bySite["MLB"] + bySite["MLA"] > 0.6, $"MLB+MLA share {bySite["MLB"] + bySite["MLA"]:P1}");

        // No real third-party brand anywhere in the pack; the only real name is the demo label itself.
        var forbidden = new Regex(@"\b(Mercado ?Pago|Visa|Mastercard|American Express|Amex|PayPal|Stripe|Adyen|Correios|Andreani|OCA|DHL|FedEx|UPS|Estafeta|Servientrega|Chilexpress|Elasticsearch|Mercado Envios|Mercado Envíos)\b");
        foreach (var file in Directory.GetFiles(PackDir, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.False(forbidden.IsMatch(text), $"{Path.GetRelativePath(PackDir, file)} names a real brand: {forbidden.Match(text).Value}");
            foreach (Match m in Regex.Matches(text, "Mercado Libre"))
                Assert.True(text.AsSpan(m.Index).StartsWith("Mercado Libre (demo)"), $"{Path.GetRelativePath(PackDir, file)} uses 'Mercado Libre' without '(demo)'.");
        }
    }

    [Fact]
    public void RetailPack_HasScenarioLibrary()
    {
        var a = Archetype();
        var scenarios = a.Scenarios!.ToDictionary(s => s.Key!);
        foreach (var key in RequiredScenarios) Assert.True(scenarios.ContainsKey(key), $"missing scenario '{key}'");

        var mlm = scenarios["search-latency-mlm"].Effects!;
        Assert.Contains(mlm, e => e.Kind == EffectKinds.EntityOnly && e.Entities!.SequenceEqual(["MLM"]));
        Assert.Contains(mlm, e => e.Kind == EffectKinds.LatencyMultiplier && e.Partner == "search-cluster");
        Assert.Contains(scenarios["payment-gateway-outage"].Effects!, e => e.Kind == EffectKinds.DependencyFailure && e.Partner == "payment-gateway");
        Assert.Contains(scenarios["checkout-deploy-exceptions"].Effects!, e => e.Kind == EffectKinds.ErrorRate && e.Step == "POST /api/checkout/orders");
        Assert.Contains(scenarios["carrier-b-timeouts"].Effects!, e => e.Kind == EffectKinds.DependencyFailure && e.Partner == "carrier-b");

        // Business drop: only thins the business-event steps, no errors or latency.
        var businessSteps = a.Steps!.Where(s => s.BusinessEvent is not null).Select(s => s.Name).ToHashSet();
        Assert.All(scenarios["orders-drop"].Effects!, e =>
        {
            Assert.Equal(EffectKinds.TrafficMultiplier, e.Kind);
            Assert.True(e.Factor < 1);
            Assert.Contains(e.Step, businessSteps);
        });

        // Hot Sale: one unscoped traffic peak and nothing else.
        var hot = Assert.Single(scenarios["hot-sale"].Effects!);
        Assert.Equal(EffectKinds.TrafficMultiplier, hot.Kind);
        Assert.True(hot.Factor > 1 && hot.Step is null && hot.Entities is null);

        var plan = CompanyPlan.Build(a, Scales.Laptop, 1000);
        var start = From.AddHours(2);
        var end = From.AddHours(4);

        // The peak triples requests on every app. Whether it alerts is decided by the KPI replay
        // (RetailPack_ReplayDetectsScenariosAndIgnoresPeak), not by raw rates here.
        foreach (var app in plan.Apps)
        {
            var normal = Run(plan, app.Database, null, start, end);
            var peak = Run(plan, app.Database, "hot-sale", start, end);
            Assert.InRange(peak.Requests.Count / (double)normal.Requests.Count, 2.6, 3.4);
        }
        var ordersNormal = Run(plan, "retail-checkout-api", null, start, end).CustomEvents.Count(e => e.Name == "OrderPlaced");
        var ordersPeak = Run(plan, "retail-checkout-api", "hot-sale", start, end).CustomEvents.Count(e => e.Name == "OrderPlaced");
        Assert.True(ordersPeak > 2.5 * ordersNormal, $"orders {ordersNormal} -> {ordersPeak}");

        // The business drop cuts OrderPlaced with no extra failures.
        var dropped = Run(plan, "retail-checkout-api", "orders-drop", start, end);
        Assert.True(dropped.CustomEvents.Count(e => e.Name == "OrderPlaced") < 0.5 * ordersNormal);
        Assert.True(FailureRate(dropped) < 0.01);

        // The partner outage breaks payments; the one-site search incident hits MLM only.
        Assert.True(FailureRate(Run(plan, "retail-payments", "payment-gateway-outage", start, end)) > 0.2);
        var mlmBatch = Run(plan, "retail-search-api", "search-latency-mlm", start, end);
        var normalSearch = Run(plan, "retail-search-api", null, start, end);
        static double P95Of(SyntheticBatch b, string site)
        {
            var sorted = b.Requests.Where(r => r.Entity == site && r.Name == "GET /api/search").Select(r => r.Duration).Order().ToList();
            return sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
        }
        Assert.True(P95Of(mlmBatch, "MLM") > 3 * P95Of(normalSearch, "MLM"));
        Assert.InRange(P95Of(mlmBatch, "MLB") / P95Of(normalSearch, "MLB"), 0.9, 1.1);
    }

    [Fact]
    public void RetailPack_HasHistoricalIncidentsAndBusinessKpis()
    {
        var a = Archetype();
        var incidents = a.HistoricalIncidents!;
        Assert.True(incidents.Count >= 3);
        Assert.All(incidents, i => Assert.True(i.HoursAgo > 0 && i.HoursAgo + i.DurationMinutes / 60.0 <= 72, $"{i.Scenario} {i.HoursAgo}h ago is not within the last 3 days"));
        Assert.True(incidents.Count(i => i.Scenario != "hot-sale") >= 3, "at least 3 real incidents besides the legitimate peak");
        Assert.Contains(incidents, i => i.Scenario == "hot-sale");

        var kpis = Kpis();
        Assert.All(kpis, k =>
        {
            Assert.Equal("mercadolibre", k.ProfileKey);
            Assert.Equal("00:05:00", k.BucketSize);
            Assert.Equal("00:01:00", k.EvaluationFrequency);
            Assert.True(k.Enabled == true);
        });

        var business = kpis.Where(k => k.Pillar == "Business").ToList();
        Assert.True(business.Count >= 2);
        Assert.All(business, k =>
        {
            Assert.StartsWith("kql:kpi:", k.SourceKey, StringComparison.Ordinal);
            Assert.Contains("customEvents", k.KqlText, StringComparison.Ordinal);
            Assert.Equal("Decrease", k.Direction);
        });
        var orders = Assert.Single(business, k => k.SourceKey == "kql:kpi:retail-orders-placed");
        Assert.Equal("Count", orders.Aggregation);
        Assert.Contains("\"OrderPlaced\"", orders.KqlText, StringComparison.Ordinal);
        var gmv = Assert.Single(business, k => k.SourceKey == "kql:kpi:retail-gmv-usd");
        Assert.Equal("Value", gmv.Aggregation);
        Assert.Contains("sum(todouble(customMeasurements.AmountUsd))", gmv.KqlText, StringComparison.Ordinal);
        Assert.Contains(a.Steps!, s => s.BusinessEvent?.Name == "OrderPlaced" && s.BusinessEvent.Measurements!.ContainsKey("AmountUsd"));

        // Exceptions KPIs read the percentage of requests, so a legitimate peak does not move them.
        Assert.All(kpis.Where(k => k.SourceKey == "appinsights:exceptions"), k => Assert.Equal("Value", k.Aggregation));
        Assert.All(kpis.Where(k => k.SourceKey == "appinsights:latency"), k => Assert.Equal("P95", k.Aggregation));
    }

    [Fact]
    public void RetailPack_CorpusMatchesScenarios()
    {
        var a = Archetype();
        var scenarioKeys = a.Scenarios!.Select(s => s.Key!).ToHashSet();
        var stepNames = a.Steps!.Select(s => s.Name!).ToList();
        var partners = a.Steps!.SelectMany(s => s.Dependencies ?? []).Select(d => d.Partner!).ToHashSet();
        var files = Directory.GetFiles(Path.Combine(PackDir, "corpus"), "*.md", SearchOption.AllDirectories);
        Assert.True(files.Length >= 10, $"{files.Length} RCAs");

        var covered = new HashSet<string>();
        var oldest = new DateTime(2025, 5, 7);
        var newest = new DateTime(2026, 11, 7);
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(PackDir, file);
            var text = File.ReadAllText(file);
            foreach (var section in new[] { "Root Cause Analysis", "Bottom Line Up Front", "What Happened", "5 Whys", "Corrective Actions" })
                Assert.True(text.Contains(section, StringComparison.Ordinal), $"{name} lacks '{section}'");
            Assert.Contains("synthetic", text, StringComparison.Ordinal);

            var date = Regex.Match(text, @"\*\*Date:\*\* (\d{4}-\d{2}-\d{2})");
            Assert.True(date.Success, $"{name} has no **Date:**");
            var when = DateTime.ParseExact(date.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.InRange(when, oldest, newest);

            var scenario = Regex.Match(text, @"\*\*Demo scenario:\*\* `([a-z0-9-]+)`");
            Assert.True(scenario.Success, $"{name} names no demo scenario");
            Assert.Contains(scenario.Groups[1].Value, scenarioKeys);
            covered.Add(scenario.Groups[1].Value);

            Assert.True(stepNames.Any(s => text.Contains(s, StringComparison.Ordinal)) || scenario.Groups[1].Value == "hot-sale",
                $"{name} mentions no archetype step");
            if (Regex.Match(text, @"\*\*Partners?:\*\* (.+)") is { Success: true } line)
                Assert.True(partners.Any(p => line.Groups[1].Value.Contains(p, StringComparison.Ordinal)), $"{name} names no archetype partner");
        }
        Assert.Equal(scenarioKeys.Order(), covered.Order());
    }

    [Fact]
    public void RetailPack_ProfileClassifiesEveryStepToItsOwnService()
    {
        var profile = Profile();
        var rules = BuildRules(profile);
        var serviceOfApp = ServiceOfApp(profile);
        var plan = CompanyPlan.Build(Archetype(), Scales.Laptop, 1000);

        foreach (var app in plan.Apps)
        {
            foreach (var step in app.Steps)
            {
                var rule = Classify(rules, "https://" + app.App.Host + step.Url, step.Name);
                Assert.True(rule?.Service == serviceOfApp[app.AppId],
                    $"{app.RoleName} '{step.Name}' is classified as {rule?.Service ?? "(unmapped)"}/{rule?.Step}, expected service {serviceOfApp[app.AppId]}");
            }
        }

        // The storefront's search page and the search API both contain "/search": only the host tells them apart.
        Assert.Equal("search-query", Classify(rules, "https://search-api.mercadolibre.demo/api/search?q=tv", "GET /api/search")?.Step);
        Assert.Equal("search-page", Classify(rules, "https://www.mercadolibre.demo/search?q=tv", "GET /search")?.Step);
    }

    [Fact]
    public void RetailPack_ReplayDetectsScenariosAndIgnoresPeak()
    {
        var archetype = Archetype();
        var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000);
        var profile = Profile();
        var kpis = Kpis();
        Assert.Equal(kpis.Count, kpis.Select(Label).Distinct().Count());
        var cleanChunks = new System.Collections.Concurrent.ConcurrentDictionary<long, Lazy<ReplayChunk>>();

        var runs = SweepResets.AsParallel().AsOrdered().Select(resetAt =>
        {
            var incidents = plan.HistoricalIncidents(resetAt).ToList();
            foreach (var (scenario, hoursAgo) in LiveScenarios)
            {
                var start = SynthesisClock.FloorMinute(resetAt - TimeSpan.FromHours(hoursAgo));
                incidents.Add(new ActiveIncident(scenario, start, start.AddMinutes(plan.FindScenario(scenario)!.DefaultDurationMinutes)));
            }
            var model = BuildReplayModel(plan, profile, incidents, resetAt, cleanChunks);
            return (ResetAt: resetAt, Incidents: incidents, Results: kpis.Select(k => (Label: Label(k), Result: Evaluate(k, Series(k, model)))).ToList());
        }).ToList();

        static bool In(DateTimeOffset at, ActiveIncident i, TimeSpan after) => at >= i.Start - Bucket && at < i.End + after;
        var failures = new List<string>();
        var instances = new Dictionary<string, int>();
        var hits = new Dictionary<string, int>();
        foreach (var (resetAt, incidents, results) in runs)
        {
            Assert.Equal(RequiredScenarios.Order(), incidents.Select(i => i.ScenarioKey).Distinct().Order());
            foreach (var (label, result) in results)
            {
                // A legitimate peak must not raise any KPI: no trigger during it nor in the two hours after it.
                foreach (var peak in incidents.Where(i => i.ScenarioKey == "hot-sale"))
                    foreach (var t in result.Triggers.Where(t => In(t.At, peak, PeakQuietAfter)))
                        failures.Add($"reset {resetAt:MM-dd HH:mm}: {label} triggered during hot-sale at {t.At:MM-dd HH:mm} ({t.Entity})");

                var falses = result.Triggers.Where(t => !incidents.Any(i => In(t.At, i, FalseGrace)));
                foreach (var day in falses.GroupBy(t => t.At.UtcDateTime.Date).Where(g => g.Count() > MaxFalseTriggersPerDay))
                    failures.Add($"reset {resetAt:MM-dd HH:mm}: {label}: {day.Count()} false triggers on {day.Key:yyyy-MM-dd} (max {MaxFalseTriggersPerDay})");
            }

            foreach (var incident in incidents.Where(i => i.ScenarioKey != "hot-sale"))
            {
                foreach (var expected in PromisedDetections[incident.ScenarioKey])
                {
                    var key = $"{incident.ScenarioKey} -> {expected}";
                    instances[key] = instances.GetValueOrDefault(key) + 1;
                    var parts = expected.Split('@');
                    var result = results.Single(r => r.Label == parts[0]).Result;
                    if (result.Triggers.Any(t => In(t.At, incident, DetectionGrace) && (parts.Length == 1 || t.Entity == parts[1])))
                        hits[key] = hits.GetValueOrDefault(key) + 1;
                }
            }
        }

        // The README promises a detection only if it fires for at least 95 % of the incidents of the sweep.
        var table = instances.Select(kv => $"{kv.Key,-70} {hits.GetValueOrDefault(kv.Key),3}/{kv.Value,-3} {100.0 * hits.GetValueOrDefault(kv.Key) / kv.Value,5:F1} %").ToList();
        foreach (var (key, n) in instances.Where(kv => hits.GetValueOrDefault(kv.Key) < MinPromisedHitRate * kv.Value))
            failures.Add($"{key} detected {hits.GetValueOrDefault(key)}/{n} (< {MinPromisedHitRate:P0})");

        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n\nHit rates:\n" + string.Join("\n", table));
    }

    // ------------------------------------------------------------------ KPI replay
    //
    // A minimal, faithful re-implementation of what the platform does with kpis.json (the demo never references a
    // platform project, so the semantics are copied here; keep them in step with):
    //   classification  Portal/src/JourneyMonitor.Core/Company/CompanyKqlTemplater.cs (BuildRules, HostPredicate,
    //                   RenderMatcher): every step of every service, stable-sorted by Order, first match wins, a
    //                   service's hostFilters narrow its rules.
    //   bucket rows     Portal/src/JourneyMonitor.Infrastructure/Kql/kpi-latency.kql.template.txt and
    //                   kpi-exceptions.kql.template.txt: a KPI reads only its service's sources; latency domain =
    //                   TotalCount requests + `sample perBucket` durations, latency entity = percentile per site,
    //                   exceptions = TotalCount exceptions and Value = 100 * exceptions / requests.
    //   sampling        Portal/src/JourneyMonitor.Core/Monitoring/Ingestion/KpiSampling.cs (perBucket =
    //                   ceil(SampleSize / (LookbackWindow / BucketSize))) and KpiBucketStatistics.cs (PERCENTILE.INC).
    //   evaluation      Portal/src/JourneyMonitor.Core/Monitoring/Kpi/KpiEvaluator.cs and Methods/*.cs: a bucket with
    //                   TotalCount < MinObservationsPerBucket or no value is skipped without touching the counter;
    //                   StaticThreshold compares the value, BaselinePercent the median of the same slot 1..lookbackDays
    //                   days back (>= min(3, days) values, reference > 0), RobustZScore the median/MAD (x1.4826) of the
    //                   previous windowBuckets (>= (window+1)/2 values, MAD > 0); ConsecutiveCount anomalous buckets
    //                   trigger, CooldownMinutes after a trigger suppress.
    //   business KPIs   the kql: queries zero-fill (range + leftouter join), so a bucket without events is 0, not missing.

    /// <summary>
    /// 24 resets, 7 hours apart: Sat 2026-11-07 21:50 UTC to Sat 11-14 14:50, every hour of the day once, so the
    /// historical and live incidents fall on every hour and day of the week.
    /// </summary>
    private static readonly DateTimeOffset[] SweepResets =
        [.. Enumerable.Range(0, 24).Select(i => new DateTimeOffset(2026, 11, 7, 21, 50, 0, TimeSpan.Zero).AddHours(7 * i))];

    /// <summary>Scenarios a presenter fires during the demo (README script), hours before the reset; default durations.</summary>
    private static readonly (string Scenario, double HoursAgo)[] LiveScenarios =
    [
        ("search-latency-mlm", 22), ("payment-gateway-outage", 19), ("orders-drop", 9), ("checkout-deploy-exceptions", 6), ("hot-sale", 3.5),
    ];

    /// <summary>The bold KPIs of the README (kpi/domain/grain[@entity]) each incident must trigger.</summary>
    private static readonly Dictionary<string, string[]> PromisedDetections = new()
    {
        ["search-latency-mlm"] = ["Latency/search/domain"],
        ["payment-gateway-outage"] = ["Exceptions/payments/domain", "Payments approved/payments/domain"],
        ["checkout-deploy-exceptions"] = ["Exceptions/checkout/domain"],
        ["carrier-b-timeouts"] = ["Exceptions/shipping/domain", "Shipping labels/shipping/domain"],
        ["orders-drop"] = ["Orders placed/checkout/domain", "GMV (USD)/checkout/domain", "Add to cart/storefront/domain", "Payments approved/payments/domain", "Shipping labels/shipping/domain"],
    };

    private const double MinPromisedHitRate = 0.95;
    private const int MaxFalseTriggersPerDay = 2;
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    /// <summary>A detection may land up to 15 minutes after the incident ends (consecutive buckets).</summary>
    private static readonly TimeSpan DetectionGrace = TimeSpan.FromMinutes(15);

    /// <summary>Triggers up to 30 minutes after an incident are its recovery, not false alarms.</summary>
    private static readonly TimeSpan FalseGrace = TimeSpan.FromMinutes(30);

    /// <summary>No KPI may trigger during a legitimate peak nor in the two hours after it ends.</summary>
    private static readonly TimeSpan PeakQuietAfter = TimeSpan.FromHours(2);

    private static string Label(KpiDocument k) => $"{k.Kpi}/{k.Domain}/{k.Grain}";

    private static ProfileDocument Profile() =>
        JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(Path.Combine(PackDir, "profile.json")), ArchetypeLoader.JsonOptions)!;

    private static Dictionary<string, string> ServiceOfApp(ProfileDocument profile)
    {
        var sources = JsonSerializer.Deserialize<List<SourceDocument>>(File.ReadAllText(Path.Combine(PackDir, "sources.json")), ArchetypeLoader.JsonOptions)!;
        return sources.ToDictionary(
            s => s.AppId!,
            s => profile.Services!.Single(svc => svc!.TelemetrySourceAliases!.Contains(s.Alias!))!.Key!);
    }

    private sealed record StepRule(string Service, string Step, IReadOnlyList<string>? HostTerms, IReadOnlyList<ProfileStepMatcher> Matchers);

    private static List<StepRule> BuildRules(ProfileDocument profile) =>
        profile.Services!
            .SelectMany(s => s!.Flows!.SelectMany(f => f!.Steps!.Select(step => (Service: s, Step: step!))))
            .OrderBy(x => x.Step.Order)
            .Select(x => new StepRule(x.Service.Key!, x.Step.Key!, HostTerms(x.Service),
                (x.Step.Matchers ?? []).Where(m => m is not null && !string.IsNullOrEmpty(m.Value)).Select(m => m!).ToList()))
            .Where(r => r.Matchers.Count > 0)
            .ToList();

    private static List<string>? HostTerms(ProfileService service) =>
        service.HostFilters is not { Count: > 0 } filters
            ? null
            : filters.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().TrimStart('*', '.')).Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static StepRule? Classify(List<StepRule> rules, string url, string operation)
    {
        var uri = new Uri(url);
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        return rules.FirstOrDefault(r =>
            (r.HostTerms is null || r.HostTerms.Any(t => host.Equals(t, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + t, StringComparison.OrdinalIgnoreCase)))
            && r.Matchers.Any(m =>
            {
                var cmp = m.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                return m.Kind!.Trim().ToLowerInvariant() switch
                {
                    "url-contains" => url.Contains(m.Value!, cmp),
                    "path-contains" => path.Contains(m.Value!, cmp),
                    "operation-equals" => operation.Trim().Equals(m.Value!.Trim(), cmp),
                    "operation-contains" => operation.Contains(m.Value!, cmp),
                    _ => throw new NotSupportedException(m.Kind),
                };
            }));
    }

    private sealed class Cell
    {
        public readonly List<double> Durations = [];
        public int Requests;
        public int Exceptions;
    }

    private sealed record ReplayModel(
        long FirstBucket,
        long LastBucket,
        Dictionary<string, Dictionary<long, Dictionary<string, Cell>>> RequestsByService,
        Dictionary<(string Alias, string Event), Dictionary<long, (int Count, double AmountUsd)>> Events);

    /// <summary>The aggregated telemetry of one bucket.</summary>
    private sealed record ReplayChunk(
        Dictionary<string, Dictionary<long, Dictionary<string, Cell>>> RequestsByService,
        Dictionary<(string Alias, string Event), Dictionary<long, (int Count, double AmountUsd)>> Events);

    private static readonly TimeSpan Chunk = Bucket;

    private static long BucketOf(DateTimeOffset t) => (t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / Bucket.Ticks;

    private static DateTimeOffset BucketStart(long bucket) => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(bucket * Bucket.Ticks);

    /// <summary>
    /// Eight days of telemetry before <paramref name="resetAt"/>. The reference model draws every minute from its own seed
    /// and an effect only acts inside its incident, so a bucket no incident touches is the same for every reset: those
    /// buckets come from <paramref name="cleanChunks"/>, shared by the whole sweep, and only the others are synthesized.
    /// </summary>
    private static ReplayModel BuildReplayModel(CompanyPlan plan, ProfileDocument profile, IReadOnlyList<ActiveIncident> incidents, DateTimeOffset resetAt,
        System.Collections.Concurrent.ConcurrentDictionary<long, Lazy<ReplayChunk>> cleanChunks)
    {
        var from = SynthesisClock.FloorMinute(resetAt - TimeSpan.FromDays(8));
        var to = SynthesisClock.FloorMinute(resetAt);
        var rules = BuildRules(profile);
        var serviceOfApp = ServiceOfApp(profile);
        var aliasOfApp = JsonSerializer.Deserialize<List<SourceDocument>>(File.ReadAllText(Path.Combine(PackDir, "sources.json")), ArchetypeLoader.JsonOptions)!
            .ToDictionary(s => s.AppId!, s => s.Alias!);
        var stepService = plan.Apps.ToDictionary(a => a.AppId, a => a.Steps.Select(s => Classify(rules, "https://" + a.App.Host + s.Url, s.Name)?.Service).ToArray());
        var effects = plan.Apps.ToDictionary(a => a.AppId, a => EffectCompiler.Compile(plan, a, incidents));
        IReadOnlyList<AppliedEffect> none = [];

        ReplayChunk Synthesize(DateTimeOffset start, DateTimeOffset end, Func<AppPlan, IReadOnlyList<AppliedEffect>> effectsOf)
        {
            var chunk = new ReplayChunk([], []);
            foreach (var app in plan.Apps)
            {
                var service = serviceOfApp[app.AppId];
                if (!chunk.RequestsByService.TryGetValue(service, out var byBucket)) chunk.RequestsByService[service] = byBucket = [];
                var batch = ReferenceSynthesizer.Generate(app, effectsOf(app), start, end);
                foreach (var r in batch.Requests)
                {
                    if (stepService[app.AppId][r.StepIndex] != service) continue;
                    var b = BucketOf(r.Timestamp);
                    if (!byBucket.TryGetValue(b, out var cells)) byBucket[b] = cells = [];
                    foreach (var key in (ReadOnlySpan<string>)["", r.Entity])
                    {
                        if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new Cell();
                        cell.Requests++;
                        // The reference model writes exactly one exception per failed request, at the request's timestamp.
                        if (!r.Success) cell.Exceptions++;
                        cell.Durations.Add(r.Duration);
                    }
                }
                foreach (var e in batch.CustomEvents)
                {
                    var key = (aliasOfApp[app.AppId], e.Name);
                    if (!chunk.Events.TryGetValue(key, out var byB)) chunk.Events[key] = byB = [];
                    var b = BucketOf(e.Timestamp);
                    byB.TryGetValue(b, out var cur);
                    byB[b] = (cur.Count + 1, cur.AmountUsd + e.Measurements.GetValueOrDefault("AmountUsd"));
                }
            }
            return chunk;
        }

        var requests = new Dictionary<string, Dictionary<long, Dictionary<string, Cell>>>();
        var events = new Dictionary<(string, string), Dictionary<long, (int, double)>>();
        for (var cs = new DateTimeOffset(from.UtcTicks - from.UtcTicks % Chunk.Ticks, TimeSpan.Zero); cs < to; cs += Chunk)
        {
            var ce = cs + Chunk;
            var chunkStart = cs;
            var clean = cs >= from && ce <= to && !incidents.Any(i => i.Start < ce && i.End > chunkStart);
            var chunk = clean
                ? cleanChunks.GetOrAdd(cs.UtcTicks / Chunk.Ticks, _ => new Lazy<ReplayChunk>(() => Synthesize(chunkStart, ce, app => none))).Value
                : Synthesize(cs < from ? from : cs, ce > to ? to : ce, app => effects[app.AppId]);
            foreach (var (service, byBucket) in chunk.RequestsByService)
            {
                if (!requests.TryGetValue(service, out var all)) requests[service] = all = [];
                foreach (var (b, cells) in byBucket) all[b] = cells;
            }
            foreach (var (key, byBucket) in chunk.Events)
            {
                if (!events.TryGetValue(key, out var all)) events[key] = all = [];
                foreach (var (b, value) in byBucket) all[b] = value;
            }
        }
        return new ReplayModel(BucketOf(from), BucketOf(to) - 1, requests, events);
    }

    private sealed record ReplayBucket(long Index, long TotalCount, double? Observed);

    private static Dictionary<string, List<ReplayBucket>> Series(KpiDocument k, ReplayModel m)
    {
        var series = new Dictionary<string, List<ReplayBucket>>();
        if (k.SourceKey!.StartsWith("kql:", StringComparison.Ordinal))
        {
            var alias = Regex.Match(k.KqlText!, @"SOURCE:([a-z0-9-]+)").Groups[1].Value;
            var name = Regex.Match(k.KqlText!, "name == \"([A-Za-z]+)\"").Groups[1].Value;
            var isSum = k.KqlText!.Contains("sum(", StringComparison.Ordinal);
            Assert.Contains("| join kind=leftouter", k.KqlText, StringComparison.Ordinal);
            var byBucket = m.Events.GetValueOrDefault((alias, name)) ?? [];
            var list = series[""] = [];
            for (var b = m.FirstBucket; b <= m.LastBucket; b++)
            {
                byBucket.TryGetValue(b, out var c);
                list.Add(new ReplayBucket(b, c.Count, isSum ? c.AmountUsd : c.Count));
            }
            return series;
        }

        var domainGrain = k.Grain == "domain";
        var perBucket = k.SampleSize is > 0
            ? (int)Math.Ceiling(k.SampleSize.Value / Math.Max(1d, TimeSpan.Parse(k.LookbackWindow!, CultureInfo.InvariantCulture) / Bucket))
            : 0;
        foreach (var (b, cells) in m.RequestsByService[k.Domain!].OrderBy(x => x.Key))
        {
            foreach (var (entity, cell) in cells)
            {
                if (domainGrain != (entity == "")) continue;
                ReplayBucket bucket;
                if (k.SourceKey == "appinsights:exceptions")
                    bucket = new ReplayBucket(b, cell.Exceptions, cell.Requests > 0 ? 100.0 * cell.Exceptions / cell.Requests : null);
                else if (domainGrain)
                    bucket = new ReplayBucket(b, cell.Requests, PercentileInc(Sample(cell.Durations, perBucket, b), 0.95));
                else
                    bucket = new ReplayBucket(b, cell.Requests, NearestRank(cell.Durations, 0.95));
                if (!series.TryGetValue(entity, out var list)) series[entity] = list = [];
                list.Add(bucket);
            }
        }
        return series;
    }

    private static double[] Sample(List<double> values, int count, long seed)
    {
        var a = values.ToArray();
        if (a.Length <= count) return a;
        var rnd = new Random((int)(seed % int.MaxValue));
        for (var i = 0; i < count; i++)
        {
            var j = rnd.Next(i, a.Length);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a[..count];
    }

    private static double? PercentileInc(double[] values, double p)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        var rank = p * (values.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return values[lo] + (rank - lo) * (values[hi] - values[lo]);
    }

    private static double? NearestRank(List<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    private sealed record ReplayResult(List<(string Entity, DateTimeOffset At)> Triggers, int Evaluated);

    private static ReplayResult Evaluate(KpiDocument k, Dictionary<string, List<ReplayBucket>> series)
    {
        var raw = k.MethodParamsJson!.Value;
        using var doc = JsonDocument.Parse(raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText());
        var p = doc.RootElement;
        double Param(string name) => p.GetProperty(name).GetDouble();
        var bucketsPerDay = (long)(TimeSpan.FromDays(1) / Bucket);
        var minObs = k.MinObservationsPerBucket ?? 0;
        var consecutive = k.ConsecutiveCount!.Value;
        var cooldown = TimeSpan.FromMinutes(k.CooldownMinutes!.Value);

        var triggers = new List<(string, DateTimeOffset)>();
        var evaluated = 0;
        foreach (var (entity, list) in series)
        {
            var byIndex = list.ToDictionary(b => b.Index);
            var count = 0;
            var cooldownUntil = DateTimeOffset.MinValue;
            foreach (var bucket in list.OrderBy(b => b.Index))
            {
                if (bucket.TotalCount < minObs || bucket.Observed is not { } value) continue;

                double deviation, threshold;
                switch (k.DetectionMethod)
                {
                    case "StaticThreshold":
                        threshold = Param("threshold");
                        deviation = value - threshold;
                        threshold = 0;
                        break;
                    case "BaselinePercent":
                    {
                        var days = (int)Param("lookbackDays");
                        var slots = Enumerable.Range(1, days)
                            .Select(d => byIndex.GetValueOrDefault(bucket.Index - d * bucketsPerDay)?.Observed)
                            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                        if (slots.Count < Math.Min(3, days)) continue;
                        var reference = Median(slots);
                        if (reference <= 0) continue;
                        deviation = (value - reference) / reference * 100.0;
                        threshold = Param("thresholdPercent");
                        break;
                    }
                    case "RobustZScore":
                    {
                        var window = (int)Param("windowBuckets");
                        var values = Enumerable.Range(1, window)
                            .Select(w => byIndex.GetValueOrDefault(bucket.Index - w)?.Observed)
                            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                        if (values.Count < (window + 1) / 2) continue;
                        var median = Median(values);
                        var scale = 1.4826 * Median(values.Select(v => Math.Abs(v - median)).ToList());
                        if (scale == 0) continue;
                        deviation = (value - median) / scale;
                        threshold = Param("zThreshold");
                        break;
                    }
                    default:
                        throw new NotSupportedException(k.DetectionMethod);
                }

                evaluated++;
                var anomalous = k.Direction switch
                {
                    "Increase" => deviation > threshold,
                    "Decrease" => deviation < -threshold,
                    _ => Math.Abs(deviation) > threshold,
                };
                if (!anomalous)
                {
                    count = 0;
                    continue;
                }

                count++;
                var bucketEnd = BucketStart(bucket.Index) + Bucket;
                if (cooldownUntil > bucketEnd) continue;
                if (count >= consecutive)
                {
                    triggers.Add((entity, BucketStart(bucket.Index)));
                    cooldownUntil = bucketEnd + cooldown;
                }
            }
        }
        return new ReplayResult(triggers, evaluated);
    }
}
