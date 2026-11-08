using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoGenerator.Archetypes;
using DemoGenerator.Packs;
using DemoGenerator.Synthesis;
using Xunit.Abstractions;

namespace DemoGenerator.Tests.Packs;

public class BankPackTests(ITestOutputHelper output)
{
    private static readonly string[] AppIds = ["demo-bank-web-banking", "demo-bank-mobile-api", "demo-bank-payments-svc", "demo-bank-core-gateway"];

    /// <summary>A Monday hour on the lunch peak of the Andean curve (18:00 UTC is 13:00 in UTC-5).</summary>
    private static readonly DateTimeOffset PeakHour = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    // The 18-month window of contract v5 §6, anchored at the phase date (2026-11-07).
    private static readonly DateOnly CorpusFrom = new(2025, 5, 7);
    private static readonly DateOnly CorpusTo = new(2026, 11, 7);

    private static string PackDir { get; } = FindPackDir();

    private static Archetype Archetype() => ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));

    [Fact]
    public void BankPack_IsValid()
    {
        var errors = PackValidator.Validate(PackDir);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        var archetype = Archetype();
        Assert.Equal("bank", archetype.CompanyKey);
        Assert.Equal("banco", archetype.ProfileKey);
        Assert.Equal("Banco Andino (demo)", archetype.DisplayName);
        Assert.Equal(AppIds, archetype.Apps!.Select(a => a.AppId));
        Assert.All(archetype.Apps!, a => Assert.Equal(a.AppId!["demo-".Length..], a.Database));
        Assert.Equal("BranchId", archetype.Entities!.Dimension);
        Assert.Equal(120, ArchetypeLoader.EntityIds(archetype.Entities).Count);

        // Realism: low error rates, p95 in the hundreds of ms (ACH a bit above 2 s), every partner used.
        Assert.All(archetype.Steps!, s =>
        {
            Assert.InRange(s.ErrorRate, 0.001, 0.01);
            Assert.InRange(s.P95Ms, 300, 2500);
            Assert.All(s.Dependencies ?? [], d => Assert.InRange(d.FailureRate, 0.0001, 0.005));
        });
        var partners = archetype.Steps!.SelectMany(s => s.Dependencies ?? []).Select(d => d.Partner).Distinct().Order().ToList();
        Assert.Equal(["ach-clearing", "antifraud", "biller-hub", "card-network", "core-banking", "otp-sms"], partners);

        using var sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir, "sources.json")));
        Assert.All(sources.RootElement.EnumerateArray(), s => Assert.StartsWith("bank-", s.GetProperty("alias").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public void BankPack_HasScenarioLibrary()
    {
        var archetype = Archetype();
        var scenarios = archetype.Scenarios!.ToDictionary(s => s.Key!, StringComparer.Ordinal);
        Assert.True(scenarios.Count >= 6, $"Only {scenarios.Count} scenarios.");

        static bool Has(ScenarioModel s, string kind) => s.Effects!.Any(e => e.Kind == kind);

        // Latency after a deploy, exception storm on one step, partner outage, one degraded entity.
        Assert.True(Has(scenarios["core-gw-slow-deploy"], EffectKinds.LatencyMultiplier));
        Assert.Contains(scenarios["mobile-transfer-exceptions"].Effects!, e => e.Kind == EffectKinds.ErrorRate && e.Step is not null && e.Rate >= 0.2);
        Assert.Contains(scenarios["ach-outage"].Effects!, e => e.Kind == EffectKinds.DependencyFailure && e.Partner == "ach-clearing");
        Assert.Contains(scenarios["branch-degraded"].Effects!, e => e.Kind == EffectKinds.EntityOnly && e.Entities!.SequenceEqual(["B-003"]));

        // Business drop: only scoped traffic reductions, nothing that raises errors or latency.
        Assert.All(scenarios["transfers-drop"].Effects!, e => Assert.True(e.Kind == EffectKinds.TrafficMultiplier && e.Step is not null && e.Factor < 1));

        // Legitimate peak: one unscoped traffic increase and nothing else.
        var peak = Assert.Single(scenarios["payday-peak"].Effects!);
        Assert.Equal(EffectKinds.TrafficMultiplier, peak.Kind);
        Assert.Null(peak.Step);
        Assert.InRange(peak.Factor!.Value, 1.2, 2.0);

        var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000);
        var baseline = Synthesize(plan, null);

        // Payday: more traffic, same failure rate and latency, so no rate or latency KPI has a reason to fire.
        var payday = Synthesize(plan, "payday-peak");
        Assert.InRange(payday.Requests.Count / (double)baseline.Requests.Count, 1.35, 1.65);
        Assert.InRange(FailureRate(payday.Requests) - FailureRate(baseline.Requests), -0.003, 0.003);
        foreach (var app in AppIds)
            Assert.InRange(P95(payday.Requests.Where(r => r.App == app)) / P95(baseline.Requests.Where(r => r.App == app)), 0.85, 1.15);

        // Business drop: transfers fall, logins and failures do not.
        var drop = Synthesize(plan, "transfers-drop");
        Assert.True(Events(drop, "TransferCompleted") < 0.4 * Events(baseline, "TransferCompleted"),
            $"TransferCompleted {Events(drop, "TransferCompleted")} vs {Events(baseline, "TransferCompleted")}");
        Assert.InRange(Events(drop, "LoginSucceeded") / (double)Events(baseline, "LoginSucceeded"), 0.95, 1.05);
        Assert.InRange(FailureRate(drop.Requests) - FailureRate(baseline.Requests), -0.003, 0.003);

        // ACH outage: most interbank transfers are rejected; card authorizations are untouched.
        var ach = Synthesize(plan, "ach-outage");
        Assert.True(RejectionRate(ach) > 0.6, $"Interbank rejection {RejectionRate(ach):P1}");
        Assert.True(RejectionRate(baseline) < 0.03, $"Baseline interbank rejection {RejectionRate(baseline):P1}");
        Assert.InRange(FailureRate(ach.Requests.Where(r => r.Request.Name == "POST /payments/v1/card-authorizations")), 0, 0.02);

        // One branch: B-003 degrades, the others stay as they were.
        var branch = Synthesize(plan, "branch-degraded");
        var b003 = branch.Requests.Where(r => r.Request.Entity == "B-003").ToList();
        var others = branch.Requests.Where(r => r.Request.Entity != "B-003").ToList();
        Assert.True(b003.Count > 300, $"B-003 has {b003.Count} requests in the peak hour.");
        Assert.True(FailureRate(b003) > 0.1, $"B-003 failure rate {FailureRate(b003):P1}");
        Assert.True(FailureRate(others) < 0.012, $"Other branches failure rate {FailureRate(others):P1}");
        Assert.True(P95(b003) > 4 * P95(others), $"B-003 p95 {P95(b003):0} ms vs others {P95(others):0} ms");

        // Exception storm: mobile transfers throw the new type, web transfers are fine.
        var storm = Synthesize(plan, "mobile-transfer-exceptions");
        var stormNre = storm.Exceptions.Count(x => x.Type == "System.NullReferenceException");
        var baselineNre = baseline.Exceptions.Count(x => x.Type == "System.NullReferenceException");
        Assert.True(stormNre > 50 && stormNre > 5 * baselineNre, $"NullReferenceException {stormNre} vs baseline {baselineNre}");
        Assert.InRange(FailureRate(storm.Requests.Where(r => r.Request.Name == "POST /banca/transferencias")), 0, 0.03);
    }

    [Fact]
    public void BankPack_HasHistoricalIncidentsAndBusinessKpis()
    {
        var archetype = Archetype();
        var scenarios = archetype.Scenarios!.ToDictionary(s => s.Key!, StringComparer.Ordinal);
        var incidents = archetype.HistoricalIncidents!;

        // Every incident lies within the last 3 days, and at least 3 are real incidents (not the legitimate peak).
        Assert.All(incidents, i =>
        {
            Assert.True(scenarios.ContainsKey(i.Scenario!), i.Scenario);
            Assert.True(i.HoursAgo - i.DurationMinutes / 60.0 > 0, $"{i.Scenario} ends after the reset.");
            Assert.True(i.HoursAgo <= 72, $"{i.Scenario} starts {i.HoursAgo} h ago.");
        });
        var real = incidents.Where(i => i.Scenario != "payday-peak").Select(i => i.Scenario).Distinct().ToList();
        Assert.True(real.Count >= 3, $"Only {real.Count} historical incidents.");
        Assert.Contains(incidents, i => i.Scenario == "payday-peak");

        var events = archetype.Steps!.Select(s => s.BusinessEvent?.Name).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string?>(["TransferCompleted", "BillPaid", "LoginSucceeded", "InterbankTransferSubmitted"]), events);

        using var kpis = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir, "kpis.json")));
        var rows = kpis.RootElement.EnumerateArray().ToList();
        Assert.All(rows, k =>
        {
            Assert.Equal("banco", k.GetProperty("profileKey").GetString());
            Assert.Equal("00:05:00", k.GetProperty("bucketSize").GetString());
            Assert.Equal("00:01:00", k.GetProperty("evaluationFrequency").GetString());
            Assert.True(k.GetProperty("enabled").GetBoolean());
        });

        var business = rows.Where(k => k.GetProperty("pillar").GetString() == "Business").ToList();
        Assert.True(business.Count >= 2, $"Only {business.Count} business KPIs.");
        Assert.All(business, k =>
        {
            Assert.StartsWith("kql:kpi:bank-", k.GetProperty("sourceKey").GetString(), StringComparison.Ordinal);
            var text = k.GetProperty("kqlText").GetString()!;
            Assert.Contains(".customEvents", text, StringComparison.Ordinal);
            Assert.Contains(events, e => text.Contains($"\"{e}\"", StringComparison.Ordinal));
            // A count KPI that watched increases would page on payday; counts only watch drops.
            if (k.GetProperty("aggregation").GetString() == "Count") Assert.Equal("Decrease", k.GetProperty("direction").GetString());
        });
        Assert.Contains(business, k => k.GetProperty("kqlText").GetString()!.Contains("\"TransferCompleted\"", StringComparison.Ordinal));
        Assert.Contains(business, k => k.GetProperty("aggregation").GetString() == "Value"
            && k.GetProperty("kqlText").GetString()!.Contains("\"InterbankTransferSubmitted\"", StringComparison.Ordinal));

        // Partner outages reach Triage through the entity-grain dependency KPI.
        Assert.Contains(rows, k => k.GetProperty("sourceKey").GetString() == "appinsights:dependency" && k.GetProperty("domain").GetString() == "pagos");
    }

    [Fact]
    public void BankPack_CorpusMatchesScenarios()
    {
        var archetype = Archetype();
        var scenarios = archetype.Scenarios!.ToDictionary(s => s.Key!, StringComparer.Ordinal);
        var exceptionTypes = archetype.Steps!.SelectMany(s => s.Exceptions ?? []).Select(e => e.Type!)
            .Concat(archetype.Scenarios!.SelectMany(s => s.Effects!).Select(e => e.ExceptionType).OfType<string>())
            .Append(ReferenceSynthesizer.DependencyFailureException)
            .ToHashSet(StringComparer.Ordinal);
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir, "profile.json")));
        var serviceKeys = profile.RootElement.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);

        var corpus = Path.Combine(PackDir, "corpus");
        var files = Directory.GetFiles(corpus, "*.md", SearchOption.AllDirectories);
        Assert.True(files.Length >= 10, $"Only {files.Length} RCAs.");
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var dates = new HashSet<DateOnly>();

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(corpus, file);
            var text = File.ReadAllText(file);
            Assert.Contains(serviceKeys, k => name.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            foreach (var section in new[] { "Root Cause Analysis", "Bottom Line Up Front", "What Happened", "5 Whys", "Corrective Actions" })
                Assert.True(text.Contains(section, StringComparison.Ordinal), $"{name} has no '{section}'.");
            Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);

            var key = Field(text, "Scenario");
            Assert.True(scenarios.TryGetValue(key, out var scenario), $"{name}: unknown scenario '{key}'.");
            covered.Add(key);

            var date = DateOnly.ParseExact(Field(text, "Date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.InRange(date, CorpusFrom, CorpusTo);
            Assert.True(dates.Add(date), $"{name}: date {date} is repeated.");

            // The RCA names what the scenario touches: its steps, partners and entities.
            foreach (var effect in scenario!.Effects!)
            {
                if (effect.Step is not null) Assert.True(text.Contains(effect.Step, StringComparison.Ordinal), $"{name} does not mention step '{effect.Step}'.");
                if (effect.Partner is not null) Assert.True(text.Contains(effect.Partner, StringComparison.Ordinal), $"{name} does not mention partner '{effect.Partner}'.");
                foreach (var entity in effect.Entities ?? []) Assert.True(text.Contains(entity, StringComparison.Ordinal), $"{name} does not mention entity '{entity}'.");
            }
            foreach (var app in Field(text, "Apps").Split(',', StringSplitOptions.TrimEntries))
                Assert.True(archetype.Apps!.Any(a => a.RoleName == app), $"{name}: app '{app}' is not an archetype app.");
            if (Regex.Match(text, @"^Exceptions: (.+)$", RegexOptions.Multiline) is { Success: true } exceptions)
                foreach (var type in exceptions.Groups[1].Value.Trim().Split(',', StringSplitOptions.TrimEntries))
                    Assert.True(exceptionTypes.Contains(type), $"{name}: exception '{type}' is not produced by the archetype.");
        }

        Assert.Equal(scenarios.Keys.Order(), covered.Order());
    }

    /// <summary>
    /// The pack's text is UTF-8 without BOM and has no mojibake (accents re-encoded as two Latin-1 characters, the
    /// U+00C3/U+00C2 lead bytes): scenario titles and descriptions are shown in the scenarios UI and API.
    /// </summary>
    [Fact]
    public void BankPack_FilesAreCleanUtf8()
    {
        var strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var files = Directory.GetFiles(PackDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".md", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count >= 16, $"Only {files.Count} pack files.");
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(PackDir, file);
            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{name} starts with a UTF-8 BOM.");
            var text = strict.GetString(bytes);
            Assert.False(text.Contains('\u00C3') || text.Contains('\u00C2') || text.Contains('\uFFFD'), $"{name} has mojibake.");
        }
        var archetype = File.ReadAllText(Path.Combine(PackDir, "archetype.json"));
        Assert.Contains("Ca\u00EDda de la c\u00E1mara de compensaci\u00F3n ACH", archetype, StringComparison.Ordinal);
        Assert.Contains("transferencias m\u00F3viles", archetype, StringComparison.Ordinal);
        Assert.Contains("(leg\u00EDtimo)", archetype, StringComparison.Ordinal);
    }

    /// <summary>The documented reset window (README): 09:00-20:30 local time (UTC-5), both edges included.</summary>
    private static bool InResetWindow(DateTimeOffset reset)
    {
        var local = reset.ToOffset(TimeSpan.FromHours(-5)).TimeOfDay;
        return local >= TimeSpan.FromHours(9) && local <= new TimeSpan(20, 30, 0);
    }

    private static DateTimeOffset Local(int day, int hour, int minute) => new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-5)).ToUniversalTime();

    /// <summary>
    /// The README's sweep 1 (the same 36 resets the real-evaluator console replays): one reset every 7 hours for a
    /// week from Sun 08-Nov 00:30 local (24 resets, every hour of the day and every day of the week), plus 12 resets
    /// inside the documented window (its edges, 09:00 and 20:30, included). 24 of the 36 fall inside the window.
    /// </summary>
    private static IReadOnlyList<DateTimeOffset> SweepResets()
    {
        var resets = Enumerable.Range(0, 24).Select(k => Local(8, 0, 30) + TimeSpan.FromHours(7 * k)).ToList();
        resets.AddRange([
            Local(8, 9, 0), Local(9, 20, 15), Local(10, 9, 40), Local(11, 20, 30), Local(12, 10, 15), Local(13, 20, 0),
            Local(14, 9, 0), Local(14, 15, 20), Local(8, 17, 45), Local(10, 19, 10), Local(12, 14, 35), Local(13, 11, 50),
        ]);
        return resets;
    }

    /// <summary>
    /// The README's unqualified detection promises: what each scenario fires (while it runs or in the 15 minutes after
    /// it ends) in at least 95 % of the replays of the resets inside the documented window, historical and live;
    /// entity null = any series.
    /// </summary>
    private static readonly (string Scenario, string Kpi, string? Entity)[] Promises =
    [
        ("core-gw-slow-deploy", "Latency/core/domain", null),
        ("core-gw-slow-deploy", "Latency/banca-web/domain", null),
        ("core-gw-slow-deploy", "Latency/banca-movil/domain", null),
        ("core-gw-slow-deploy", "Latency/pagos/domain", null),
        ("core-gw-slow-deploy", "Exceptions/core/domain", null),
        ("core-gw-slow-deploy", "Latency/banca-web/entity", null),
        ("core-gw-slow-deploy", "Latency/banca-movil/entity", null),
        ("core-gw-slow-deploy", "Latency/core/entity", null),
        ("mobile-transfer-exceptions", "Exceptions/banca-movil/domain", null),
        ("ach-outage", "PartnerFailures/pagos/entity", "ach-clearing"),
        ("ach-outage", "Exceptions/pagos/domain", null),
        ("ach-outage", "Rechazo interbancario (%)/pagos/domain", null),
        ("branch-degraded", "Latency/banca-web/entity", "B-003"),
        ("branch-degraded", "Latency/banca-movil/entity", "B-003"),
        ("branch-degraded", "Exceptions/banca-web/entity", "B-003"),
        ("card-network-degraded", "PartnerFailures/banca-movil/entity", "card-network"),
        ("card-network-degraded", "Exceptions/banca-movil/domain", null),
        ("otp-delays", "PartnerFailures/banca-web/entity", "otp-sms"),
        ("otp-delays", "PartnerFailures/banca-movil/entity", "otp-sms"),
        ("otp-delays", "Latency/banca-web/domain", null),
        ("otp-delays", "Latency/banca-movil/domain", null),
        ("otp-delays", "Exceptions/banca-web/domain", null),
        ("otp-delays", "Exceptions/banca-movil/domain", null),
        ("otp-delays", "Logins exitosos/banca-web/domain", null),
        ("transfers-drop", "Transferencias completadas/banca-movil/domain", null),
    ];

    /// <summary>
    /// Replays the sweep: for each of its 36 resets, the 8-day laptop backfill (the generator's in-memory reference
    /// model, with the 5 historical incidents) and then each of the 8 scenarios started live 5 minutes after the
    /// reset (evaluated until 2 hours after its end), through a faithful copy of the platform's KPI evaluation.
    /// Inside the documented window (24 resets) every README promise fires in at least 95 % of the historical and live
    /// incidents; on all 36 resets (night included) the legitimate payday peak (historical 240 min, live 180 min)
    /// fires nothing on any KPI or entity from its start to 2 hours after its end, branch-degraded fires no other
    /// branch, transfers-drop fires nothing but the business KPI <c>Transferencias completadas</c> and no KPI raises
    /// any anomaly outside the incidents (and the 2 hours after each).
    /// </summary>
    [Fact]
    public void BankPack_ReplayDetectsScenariosAndIgnoresPeak()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var archetype = Archetype();
        var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000);
        var scenarios = archetype.Scenarios!.Select(s => s.Key!).ToList();
        var resets = SweepResets();
        var window = resets.Where(InResetWindow).ToList();
        Assert.Equal(36, resets.Count);
        Assert.Equal(24, window.Count);
        Assert.Equal(24, resets.Select(r => r.ToOffset(TimeSpan.FromHours(-5)).Hour).Distinct().Count());
        Assert.Equal(7, resets.Select(r => r.ToOffset(TimeSpan.FromHours(-5)).DayOfWeek).Distinct().Count());

        var longest = TimeSpan.FromMinutes(archetype.Scenarios!.Max(s => s.DefaultDurationMinutes));
        var replay = new Replay(plan, PackDir, resets.Min() - TimeSpan.FromDays(8), resets.Max() + Replay.LiveDelay + longest + Replay.LiveTail);
        Assert.All(Promises, p => Assert.Contains(replay.Kpis, k => k.Label == p.Kpi));
        output.WriteLine($"baseline of {resets.Count} resets took {watch.Elapsed.TotalSeconds:0.0} s");

        var runs = resets.AsParallel().AsOrdered().Select(r => (Reset: r, InWindow: InResetWindow(r), Variants: replay.Run(r, scenarios))).ToList();
        output.WriteLine($"replay of {runs.Count} resets took {watch.Elapsed.TotalSeconds:0.0} s");

        static bool Within(Replay.Trigger t, ActiveIncident i, TimeSpan after) => t.BucketStart >= Replay.FloorBucket(i.Start) && t.BucketStart < i.End + after;
        var misses = new List<string>();
        var failures = new List<string>();
        var hits = Promises.ToDictionary(p => p, _ => (Hit: 0, Total: 0));
        var fired = new Dictionary<(string Scenario, string Series), int>();
        var judged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (reset, inWindow, variants) in runs)
        {
            foreach (var variant in variants)
            {
                foreach (var incident in variant.Judged)
                {
                    var label = $"{incident.ScenarioKey} {variant.Kind} at {incident.Start:MM-dd HH:mm}Z (reset {reset:MM-dd HH:mm}Z)";
                    var during = variant.Triggers.Where(t => Within(t, incident, TimeSpan.FromMinutes(15))).ToList();
                    if (inWindow)
                    {
                        judged[incident.ScenarioKey] = judged.GetValueOrDefault(incident.ScenarioKey) + 1;
                        foreach (var series in during.Select(t => t.Kpi + (t.Entity is null ? "" : t.Entity == "B-003" || !t.Entity.StartsWith("B-", StringComparison.Ordinal) ? $"[{t.Entity}]" : "[other branch]")).Distinct())
                            fired[(incident.ScenarioKey, series)] = fired.GetValueOrDefault((incident.ScenarioKey, series)) + 1;
                        foreach (var promise in Promises.Where(p => p.Scenario == incident.ScenarioKey))
                        {
                            var hit = during.Any(t => t.Kpi == promise.Kpi && (promise.Entity is null || t.Entity == promise.Entity));
                            hits[promise] = (hits[promise].Hit + (hit ? 1 : 0), hits[promise].Total + 1);
                            if (!hit) misses.Add($"{label} did not trigger {promise.Kpi}{(promise.Entity is null ? "" : " for " + promise.Entity)}");
                        }
                    }

                    // The branch incident stays on B-003: no other branch fires an entity KPI while it runs.
                    if (incident.ScenarioKey == "branch-degraded")
                    {
                        var others = during.Where(t => t.Kpi.EndsWith("/entity", StringComparison.Ordinal) && !t.Kpi.StartsWith("PartnerFailures/", StringComparison.Ordinal) && t.Entity != "B-003").ToList();
                        if (others.Count > 0) failures.Add($"{label} also fired {string.Join("; ", others)}");
                    }

                    // A pure business drop moves the business KPI only, after a reset at any hour.
                    if (incident.ScenarioKey == "transfers-drop")
                    {
                        var others = during.Where(t => t.Kpi != "Transferencias completadas/banca-movil/domain").ToList();
                        if (others.Count > 0) failures.Add($"{label} also fired {string.Join("; ", others)}");
                    }

                    // The legitimate peak fires nothing, during it or in the two hours after it ends, at any hour.
                    if (incident.ScenarioKey == "payday-peak")
                    {
                        var peak = variant.Triggers.Where(t => Within(t, incident, TimeSpan.FromHours(2))).ToList();
                        if (peak.Count > 0) failures.Add($"{label} triggered: {string.Join("; ", peak)}");
                    }
                }

                // Outside the incidents (and the two hours after each) no KPI fires at all: the README promises 0 false
                // anomalies (the acceptance bound is 2 per KPI per day).
                var noisy = variant.Triggers
                    .Where(t => !variant.Judged.Any(i => Within(t, i, TimeSpan.FromHours(2))))
                    .Select(t => t.ToString())
                    .ToList();
                if (noisy.Count > 0) failures.Add($"reset {reset:MM-dd HH:mm}Z {variant.Kind}: false anomalies: {string.Join("; ", noisy)}");
            }
        }

        output.WriteLine("promise | hits/incidents in the window");
        foreach (var (promise, (hit, total)) in hits)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{promise.Scenario} | {promise.Kpi}{(promise.Entity is null ? "" : " [" + promise.Entity + "]")} | {hit}/{total} ({100.0 * hit / total:0}%)"));
        output.WriteLine("everything that fired in the window (scenario | series | incidents)");
        foreach (var ((scenario, series), count) in fired.OrderBy(f => f.Key.Scenario, StringComparer.Ordinal).ThenBy(f => f.Key.Series, StringComparer.Ordinal))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{scenario} | {series} | {count}/{judged[scenario]} ({100.0 * count / judged[scenario]:0}%)"));
        foreach (var line in misses) output.WriteLine("MISS " + line);
        foreach (var line in failures) output.WriteLine("FAIL " + line);
        output.WriteLine($"replay and checks took {watch.Elapsed.TotalSeconds:0.0} s");

        Assert.All(hits, h => Assert.True(h.Value.Total >= window.Count, $"{h.Key}: only {h.Value.Total} incidents."));
        var below = hits.Where(h => h.Value.Hit < 0.95 * h.Value.Total).Select(h => $"{h.Key}: {h.Value.Hit}/{h.Value.Total}").ToList();
        Assert.True(below.Count == 0, "Promises below 95 %:" + Environment.NewLine + string.Join(Environment.NewLine, below.Concat(misses)));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(180), $"replay took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Row(string App, SyntheticRequest Request);

    private sealed record Hour(List<Row> Requests, List<SyntheticEvent> Events, List<SyntheticException> Exceptions);

    private static Hour Synthesize(CompanyPlan plan, string? scenario)
    {
        IReadOnlyList<ActiveIncident> incidents = scenario is null ? [] : [new ActiveIncident(scenario, PeakHour, PeakHour.AddHours(1))];
        var hour = new Hour([], [], []);
        foreach (var app in plan.Apps)
        {
            var batch = ReferenceSynthesizer.Generate(app, EffectCompiler.Compile(plan, app, incidents), PeakHour, PeakHour.AddHours(1));
            hour.Requests.AddRange(batch.Requests.Select(r => new Row(app.AppId, r)));
            hour.Events.AddRange(batch.CustomEvents);
            hour.Exceptions.AddRange(batch.Exceptions);
        }
        return hour;
    }

    private static int Events(Hour hour, string name) => hour.Events.Count(e => e.Name == name);

    private static double RejectionRate(Hour hour)
    {
        var failed = hour.Requests.Where(r => !r.Request.Success).Select(r => r.Request.OperationId).ToHashSet(StringComparer.Ordinal);
        var submitted = hour.Events.Where(e => e.Name == "InterbankTransferSubmitted").ToList();
        return submitted.Count(e => failed.Contains(e.OperationId)) / (double)submitted.Count;
    }

    private static double FailureRate(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        return list.Count == 0 ? 0 : list.Count(r => !r.Request.Success) / (double)list.Count;
    }

    private static double P95(IEnumerable<Row> rows)
    {
        var sorted = rows.Select(r => r.Request.Duration).Order().ToList();
        return sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
    }

    private static string Field(string text, string name)
    {
        var match = Regex.Match(text, $@"^{name}: (.+)$", RegexOptions.Multiline);
        Assert.True(match.Success, $"No '{name}:' line.");
        return match.Groups[1].Value.Trim();
    }

    /// <summary>Portal/demo/packs/bank, found by walking up from the test output folder.</summary>
    private static string FindPackDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packs", "bank");
            if (File.Exists(Path.Combine(candidate, "archetype.json"))) return candidate;
            candidate = Path.Combine(dir.FullName, "demo", "packs", "bank");
            if (File.Exists(Path.Combine(candidate, "archetype.json"))) return candidate;
        }
        throw new DirectoryNotFoundException($"Portal/demo/packs/bank not found above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// Replays the bank pack through its KPI rules the way the platform evaluates them, without referencing the
    /// platform. Series come from the generator's in-memory reference model (<see cref="ReferenceSynthesizer"/>),
    /// aggregated the way the KPI templates do it (Portal/src/JourneyMonitor.Infrastructure/Kql/kpi-latency,
    /// kpi-exceptions and kpi-dependency .kql.template.txt: domain or top-N entity per bucket, the domain latency P95 over
    /// a per-bucket sample of KpiSampling.SamplesPerBucket requests, a failed request writes one exception, dependencies
    /// classified by the profile's partner matchers, first declared partner wins) and the pack's own kqlText for the
    /// business KPIs (zero-filled; TotalCount = the sources' business events). Each series is evaluated bucket by bucket
    /// by a minimal copy of Portal/src/JourneyMonitor.Core/Monitoring/Kpi/KpiEvaluator.cs and
    /// Methods/StaticThresholdMethod.cs, BaselinePercentMethod.cs, RobustZScoreMethod.cs, KpiStatistics.cs:
    /// minObservationsPerBucket on TotalCount, "not evaluated" leaving the counter untouched, consecutiveCount,
    /// cooldown (an anomalous bucket inside it counts but does not trigger), a same-slot median of the previous
    /// lookbackDays (at least min(3, days) slots, reference &gt; 0) and a robust z over the previous windowBuckets
    /// (at least half of them, MAD = 0 not evaluated).
    /// </summary>
    /// <remarks>
    /// The synthesis is deterministic per minute and an effect only acts inside its incident, so a bucket no incident
    /// touches is the same in every replay: the incident-free series are computed once and each replay only
    /// regenerates the buckets of its incidents. A live scenario continues from the evaluator state the backfill left
    /// at the reset, as the platform does.
    /// </remarks>
    private sealed class Replay
    {
        private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);
        private static readonly long BucketsPerDay = TimeSpan.FromDays(1).Ticks / Bucket.Ticks;
        private const string AchStep = "POST /payments/v1/ach/transfers";

        /// <summary>A live scenario starts this long after the reset.</summary>
        public static readonly TimeSpan LiveDelay = TimeSpan.FromMinutes(5);

        /// <summary>A live scenario is evaluated until this long after its end.</summary>
        public static readonly TimeSpan LiveTail = TimeSpan.FromHours(2);

        public sealed record Kpi(
            int Id, string Label, string Name, string Domain, string Grain, string SourceKey, string Direction, string Aggregation, string Method,
            double Threshold, int LookbackDays, double ThresholdPercent, int WindowBuckets, double ZThreshold,
            int MinObservations, int ConsecutiveCount, int CooldownMinutes, int MaxEntities, int SamplesPerBucket, string? KqlText);

        public sealed record Point(DateTimeOffset BucketStart, long TotalCount, double? P95, double? Value);

        public sealed record Trigger(string Kpi, string? Entity, DateTimeOffset BucketStart, double Observed, double Reference)
        {
            public override string ToString() => string.Create(CultureInfo.InvariantCulture,
                $"{Kpi}{(Entity is null ? "" : "[" + Entity + "]")} @ {BucketStart:MM-dd HH:mm} obs {Observed:0.##} ref {Reference:0.##}");
        }

        /// <summary>One replay of a reset: the backfill ("historical") or one live scenario, and the incidents it judges.</summary>
        public sealed record Variant(string Kind, IReadOnlyList<ActiveIncident> Judged, IReadOnlyList<Trigger> Triggers);

        private sealed class EntityAgg
        {
            public List<double> Durations { get; } = [];
            public int Failed { get; set; }
        }

        /// <summary>One 5-minute bucket of one app: what the templates and the business queries read.</summary>
        private sealed class AppBucket
        {
            public List<double> Durations { get; } = [];
            public int Failed { get; set; }
            public Dictionary<string, EntityAgg> Entities { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, (int Calls, int Failed)> Partners { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, int> Events { get; } = new(StringComparer.Ordinal);
            public int AchRejected { get; set; }
        }

        /// <summary>The points of one KPI in one bucket: one per series (entity "" = domain).</summary>
        private sealed class BucketPoints(List<(string Entity, Point Point)> points)
        {
            public List<(string Entity, Point Point)> Points { get; } = points;

            /// <summary>A static-threshold bucket with no anomalous point (it only resets running counts).</summary>
            public bool Quiet { get; set; }
        }

        /// <summary>The evaluator state of one series; history lookups fall back to the state it was forked from.</summary>
        private sealed class SeriesState(SeriesState? parent)
        {
            private readonly Dictionary<long, Point> seen = [];

            public int Consecutive { get; set; } = parent?.Consecutive ?? 0;

            public DateTimeOffset? CooldownUntil { get; set; } = parent?.CooldownUntil;

            public Point? At(long bucket) => seen.TryGetValue(bucket, out var p) ? p : parent?.At(bucket);

            public void Add(long bucket, Point point) => seen[bucket] = point;
        }

        private readonly CompanyPlan plan;
        private readonly Dictionary<string, List<string>> aliasesByService;
        private readonly List<(string Key, List<string> Values)> partners;
        private readonly Dictionary<long, BucketPoints>[] baseline;

        public IReadOnlyList<Kpi> Kpis { get; }

        /// <summary>Loads the pack and computes the incident-free series of every KPI over [from, to).</summary>
        public Replay(CompanyPlan plan, string packDir, DateTimeOffset from, DateTimeOffset to)
        {
            this.plan = plan;
            Kpis = LoadKpis(packDir);
            using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(packDir, "profile.json")));
            var root = profile.RootElement;
            aliasesByService = root.GetProperty("services").EnumerateArray().ToDictionary(
                s => s.GetProperty("key").GetString()!,
                s => s.GetProperty("telemetrySourceAliases").EnumerateArray().Select(a => a.GetString()!).ToList(),
                StringComparer.Ordinal);
            partners = root.GetProperty("partners").EnumerateArray()
                .Select(p => (Key: p.GetProperty("key").GetString()!, Values: p.GetProperty("matchers").EnumerateArray()
                    .Where(m => m.GetProperty("kind").GetString() == "dependency-target-contains")
                    .Select(m => m.GetProperty("value").GetString()!).ToList()))
                .ToList();

            var b0 = BucketIndex(FloorBucket(from));
            var b1 = BucketIndex(FloorBucket(to)) + 1;
            var telemetry = Telemetry([], [(b0, b1)], parallel: true);
            baseline = Kpis.AsParallel().AsOrdered().Select(kpi => Points(kpi, telemetry, b0, b1)).ToArray();
            for (var k = 0; k < Kpis.Count; k++)
            {
                var kpi = Kpis[k];
                if (kpi.Method != "StaticThreshold") continue;
                foreach (var (b, bucket) in baseline[k])
                    bucket.Quiet = bucket.Points.All(p => EvaluateBucket(kpi, b, p.Point, new SeriesState(null))?.Anomalous != true);
            }
        }

        public static DateTimeOffset FloorBucket(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % Bucket.Ticks, TimeSpan.Zero);

        /// <summary>
        /// The backfill of <paramref name="reset"/> (8 days with the historical incidents) and, continuing from its
        /// evaluator state, each of <paramref name="liveScenarios"/> started <see cref="LiveDelay"/> after the reset.
        /// </summary>
        public IReadOnlyList<Variant> Run(DateTimeOffset reset, IReadOnlyList<string> liveScenarios)
        {
            Assert.Equal(reset, FloorBucket(reset));
            var historical = plan.HistoricalIncidents(reset);
            Assert.All(historical, i => Assert.True(i.End <= reset, $"{i.ScenarioKey} ends after the reset."));
            var bFrom = BucketIndex(reset - TimeSpan.FromDays(8));
            var bReset = BucketIndex(reset);
            var histOverlay = Overlay(historical, historical);

            var states = new Dictionary<string, SeriesState>[Kpis.Count];
            var histTriggers = new List<Trigger>();
            for (var k = 0; k < Kpis.Count; k++)
            {
                states[k] = new Dictionary<string, SeriesState>(StringComparer.Ordinal);
                histTriggers.AddRange(Evaluate(k, histOverlay[k], states[k], bFrom, bReset));
            }
            var variants = new List<Variant> { new("historical", historical, Sorted(histTriggers)) };

            foreach (var scenario in liveScenarios)
            {
                var start = SynthesisClock.FloorMinute(reset + LiveDelay);
                var live = new ActiveIncident(scenario, start, start + TimeSpan.FromMinutes(plan.FindScenario(scenario)!.DefaultDurationMinutes));
                var overlay = Overlay([.. historical, live], [live]);
                var bEnd = BucketIndex(FloorBucket(live.End + LiveTail));
                var triggers = new List<Trigger>();
                for (var k = 0; k < Kpis.Count; k++)
                {
                    var forked = states[k].ToDictionary(s => s.Key, s => new SeriesState(s.Value), StringComparer.Ordinal);
                    triggers.AddRange(Evaluate(k, overlay[k], forked, bReset, bEnd));
                }
                variants.Add(new Variant("live", [live], Sorted(triggers)));
            }
            return variants;
        }

        private static List<Trigger> Sorted(List<Trigger> triggers) =>
            triggers.OrderBy(t => t.BucketStart).ThenBy(t => t.Kpi, StringComparer.Ordinal).ThenBy(t => t.Entity, StringComparer.Ordinal).ToList();

        /// <summary>The points of the buckets that <paramref name="regenerate"/> touch, with the effects of all <paramref name="incidents"/>.</summary>
        private Dictionary<long, BucketPoints>[] Overlay(IReadOnlyList<ActiveIncident> incidents, IReadOnlyList<ActiveIncident> regenerate)
        {
            var ranges = regenerate.Select(i => (From: BucketIndex(FloorBucket(i.Start)), To: BucketIndex(FloorBucket(i.End - TimeSpan.FromTicks(1))) + 1))
                .OrderBy(r => r.From).ToList();
            var merged = new List<(long From, long To)>();
            foreach (var r in ranges)
            {
                if (merged.Count > 0 && r.From <= merged[^1].To) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
                else merged.Add(r);
            }
            // An app no regenerated incident touches keeps its incident-free buckets, and so does every KPI that reads only such apps.
            var affected = plan.Apps.Where(a => EffectCompiler.Compile(plan, a, regenerate).Count > 0).Select(a => a.Database).ToHashSet(StringComparer.Ordinal);
            var touched = Kpis.Select(kpi => Sources(kpi).Any(affected.Contains)).ToArray();
            var needed = Kpis.Where((_, k) => touched[k]).SelectMany(Sources).ToHashSet(StringComparer.Ordinal);
            var telemetry = Telemetry(incidents, merged, parallel: false, needed);
            return Kpis.Select((kpi, k) =>
            {
                var points = new Dictionary<long, BucketPoints>();
                if (!touched[k]) return points;
                foreach (var (from, to) in merged)
                    foreach (var kv in Points(kpi, telemetry, from, to)) points[kv.Key] = kv.Value;
                return points;
            }).ToArray();
        }

        /// <summary>The app databases a KPI reads.</summary>
        private IEnumerable<string> Sources(Kpi kpi) => kpi.KqlText is { } kql
            ? Regex.Matches(kql, @"\{\{SOURCE:(?<a>[a-z0-9-]+)\}\}").Select(m => m.Groups["a"].Value).Distinct()
            : aliasesByService[kpi.Domain];

        /// <summary>KpiEvaluator.Evaluate over the buckets [from, to) of one KPI, carrying the per-series state.</summary>
        private List<Trigger> Evaluate(int k, Dictionary<long, BucketPoints> overlay, Dictionary<string, SeriesState> states, long from, long to)
        {
            var kpi = Kpis[k];
            var triggers = new List<Trigger>();
            // Series with a running count of anomalous buckets: while there are none, a quiet incident-free bucket changes nothing.
            var hot = states.Values.Count(s => s.Consecutive > 0);
            for (var b = from; b < to; b++)
            {
                if (!overlay.TryGetValue(b, out var bucket))
                {
                    if (!baseline[k].TryGetValue(b, out bucket) || (bucket.Quiet && hot == 0)) continue;
                }
                foreach (var (entity, point) in bucket.Points)
                {
                    if (!states.TryGetValue(entity, out var state)) states[entity] = state = new SeriesState(null);
                    var wasHot = state.Consecutive > 0;
                    var verdict = EvaluateBucket(kpi, b, point, state);
                    if (kpi.Method != "StaticThreshold") state.Add(b, point);
                    if (verdict is { } v)
                    {
                        if (!v.Anomalous) state.Consecutive = 0;
                        else
                        {
                            state.Consecutive++;
                            var bucketEnd = point.BucketStart + Bucket;
                            if (state.Consecutive >= kpi.ConsecutiveCount && !(state.CooldownUntil is { } until && until > bucketEnd))
                            {
                                state.CooldownUntil = bucketEnd + TimeSpan.FromMinutes(kpi.CooldownMinutes);
                                triggers.Add(new Trigger(kpi.Label, entity.Length == 0 ? null : entity, point.BucketStart, v.Observed, v.Reference));
                            }
                        }
                    }
                    hot += (state.Consecutive > 0 ? 1 : 0) - (wasHot ? 1 : 0);
                }
            }
            return triggers;
        }

        /// <summary>Null when the platform answers NotEvaluated.</summary>
        private static (bool Anomalous, double Observed, double Reference)? EvaluateBucket(Kpi kpi, long b, Point point, SeriesState previous)
        {
            if (point.TotalCount < kpi.MinObservations || Observed(point, kpi.Aggregation) is not { } observed) return null;
            switch (kpi.Method)
            {
                case "StaticThreshold":
                    return (kpi.Direction == "Increase" ? observed > kpi.Threshold : kpi.Direction == "Decrease" && observed < kpi.Threshold, observed, kpi.Threshold);
                case "BaselinePercent":
                {
                    var values = Enumerable.Range(1, kpi.LookbackDays)
                        .Select(d => previous.At(b - d * BucketsPerDay) is { } p ? Observed(p, kpi.Aggregation) : null)
                        .OfType<double>().ToList();
                    if (values.Count < Math.Min(3, kpi.LookbackDays)) return null;
                    var reference = Median(values);
                    if (reference <= 0) return null;
                    return (Breaches(kpi.Direction, (observed - reference) / reference * 100.0, kpi.ThresholdPercent), observed, reference);
                }
                case "RobustZScore":
                {
                    var values = Enumerable.Range(1, kpi.WindowBuckets)
                        .Select(d => previous.At(b - d) is { } p ? Observed(p, kpi.Aggregation) : null)
                        .OfType<double>().ToList();
                    if (values.Count < (kpi.WindowBuckets + 1) / 2) return null;
                    var reference = Median(values);
                    var scale = 1.4826 * Median(values.Select(v => Math.Abs(v - reference)));
                    if (scale == 0) return null;
                    return (Breaches(kpi.Direction, (observed - reference) / scale, kpi.ZThreshold), observed, reference);
                }
                default:
                    throw new InvalidOperationException($"{kpi.Label}: method {kpi.Method} is not replayed.");
            }
        }

        private static IReadOnlyList<Kpi> LoadKpis(string packDir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packDir, "kpis.json")));
            var result = new List<Kpi>();
            foreach (var k in doc.RootElement.EnumerateArray())
            {
                using var p = JsonDocument.Parse(k.GetProperty("methodParamsJson").GetString()!);
                double D(string name) => p.RootElement.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
                string S(string name) => k.GetProperty(name).GetString()!;
                Assert.Equal("00:05:00", S("bucketSize"));
                // Portal/src/JourneyMonitor.Core/Monitoring/Ingestion/KpiSampling.cs SamplesPerBucket (used by
                // UseCases/KpiIngestionService.cs as the template's perBucket): ceil(SampleSize / max(1, LookbackWindow / BucketSize)).
                // The platform spreads SampleSize over the whole lookback, so 100000 over 288 buckets = 348 samples per bucket.
                var buckets = Math.Max(1d, TimeSpan.Parse(S("lookbackWindow"), CultureInfo.InvariantCulture) / Bucket);
                var sampleSize = k.GetProperty("sampleSize").GetInt32();
                var perBucket = sampleSize <= 0 ? 0 : (int)Math.Ceiling(sampleSize / buckets);
                // The id is the KPI's position in kpis.json (1-based), as the console replay numbers the definitions.
                result.Add(new Kpi(
                    result.Count + 1, $"{S("kpi")}/{S("domain")}/{S("grain")}", S("kpi"), S("domain"), S("grain"), S("sourceKey"), S("direction"), S("aggregation"),
                    S("detectionMethod"), D("threshold"), (int)D("lookbackDays"), D("thresholdPercent"), (int)D("windowBuckets"), D("zThreshold"),
                    k.GetProperty("minObservationsPerBucket").GetInt32(), k.GetProperty("consecutiveCount").GetInt32(),
                    k.GetProperty("cooldownMinutes").GetInt32(), k.GetProperty("maxEntitiesPerBucket").GetInt32(), perBucket,
                    k.TryGetProperty("kqlText", out var kql) ? kql.GetString() : null));
            }
            return result;
        }

        private string? Partner(string target) => partners.FirstOrDefault(p => p.Values.Any(v => target.Contains(v, StringComparison.OrdinalIgnoreCase))).Key;

        /// <summary>The buckets [from, to) of every range, per app database (all, or only <paramref name="only"/>), with the effects of <paramref name="incidents"/>.</summary>
        private Dictionary<string, Dictionary<long, AppBucket>> Telemetry(IReadOnlyList<ActiveIncident> incidents, IReadOnlyList<(long From, long To)> ranges, bool parallel, IReadOnlySet<string>? only = null)
        {
            var buckets = ranges.SelectMany(r => Enumerable.Range(0, (int)(r.To - r.From)).Select(i => r.From + i)).ToList();
            return plan.Apps.Where(a => only is null || only.Contains(a.Database)).ToDictionary(a => a.Database, app =>
            {
                var effects = EffectCompiler.Compile(plan, app, incidents);
                // One 5-minute bucket per batch: the synthetic rows die young (cheap for the GC). The replays of the
                // resets already run in parallel, so only the baseline parallelizes its buckets.
                IEnumerable<KeyValuePair<long, AppBucket>> Batch(long b) => Aggregate(ReferenceSynthesizer.Generate(app, effects, BucketStart(b), BucketStart(b + 1)));
                var parts = parallel ? buckets.AsParallel().SelectMany(Batch).ToList() : buckets.SelectMany(Batch).ToList();
                return parts.ToLookup(kv => kv.Key, kv => kv.Value)
                    .ToDictionary(g => g.Key, g => g.Aggregate(Merge));
            }, StringComparer.Ordinal);
        }

        /// <summary>
        /// Two partial aggregates of one bucket: a dependency is stamped a few milliseconds after its request, so the
        /// last calls of a bucket's batch can land in the next bucket.
        /// </summary>
        private static AppBucket Merge(AppBucket a, AppBucket b)
        {
            a.Durations.AddRange(b.Durations);
            a.Failed += b.Failed;
            a.AchRejected += b.AchRejected;
            foreach (var (key, e) in b.Entities)
            {
                if (!a.Entities.TryGetValue(key, out var into)) a.Entities[key] = into = new EntityAgg();
                into.Durations.AddRange(e.Durations);
                into.Failed += e.Failed;
            }
            foreach (var (key, c) in b.Partners)
            {
                var c0 = a.Partners.GetValueOrDefault(key);
                a.Partners[key] = (c0.Calls + c.Calls, c0.Failed + c.Failed);
            }
            foreach (var (key, n) in b.Events) a.Events[key] = a.Events.GetValueOrDefault(key) + n;
            return a;
        }

        private Dictionary<long, AppBucket> Aggregate(SyntheticBatch batch)
        {
            var buckets = new Dictionary<long, AppBucket>();
            AppBucket At(DateTimeOffset ts)
            {
                var b = BucketIndex(ts);
                if (!buckets.TryGetValue(b, out var x)) buckets[b] = x = new AppBucket();
                return x;
            }
            var achFailed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in batch.Requests)
            {
                var x = At(r.Timestamp);
                x.Durations.Add(r.Duration);
                if (!x.Entities.TryGetValue(r.Entity, out var e)) x.Entities[r.Entity] = e = new EntityAgg();
                e.Durations.Add(r.Duration);
                if (!r.Success)
                {
                    x.Failed++;
                    e.Failed++;
                    if (r.Name == AchStep) achFailed.Add(r.OperationId);
                }
            }
            foreach (var d in batch.Dependencies)
            {
                if (Partner(d.Target) is not { } key) continue;
                var x = At(d.Timestamp);
                var c0 = x.Partners.GetValueOrDefault(key);
                x.Partners[key] = (c0.Calls + 1, c0.Failed + (d.Success ? 0 : 1));
            }
            foreach (var ev in batch.CustomEvents)
            {
                var x = At(ev.Timestamp);
                x.Events[ev.Name] = x.Events.GetValueOrDefault(ev.Name) + 1;
                if (ev.Name == "InterbankTransferSubmitted" && achFailed.Contains(ev.OperationId)) x.AchRejected++;
            }
            return buckets;
        }

        /// <summary>The points of one KPI in the buckets [first, last) of <paramref name="telemetry"/>.</summary>
        private Dictionary<long, BucketPoints> Points(Kpi kpi, IReadOnlyDictionary<string, Dictionary<long, AppBucket>> telemetry, long first, long last)
        {
            var result = new Dictionary<long, BucketPoints>();
            void Add(long b, string key, Point p)
            {
                if (!result.TryGetValue(b, out var list)) result[b] = list = new BucketPoints([]);
                list.Points.Add((key, p));
            }

            if (kpi.KqlText is { } kql)
            {
                // The sources and event names the pack's query reads.
                var sources = Regex.Matches(kql, @"\{\{SOURCE:(?<a>[a-z0-9-]+)\}\}").Select(m => m.Groups["a"].Value).Distinct().ToList();
                var target = Regex.Match(kql, "name == \"(?<n>[A-Za-z]+)\"").Groups["n"].Value;
                var rejection = kpi.SourceKey == "kql:kpi:bank-interbank-rejection-rate";
                for (var b = first; b < last; b++)
                {
                    var t = BucketStart(b);
                    var buckets = sources.Select(s => telemetry[s].GetValueOrDefault(b)).OfType<AppBucket>().ToList();
                    if (rejection)
                    {
                        var submitted = buckets.Sum(x => x.Events.GetValueOrDefault(target));
                        var rejected = buckets.Sum(x => x.AchRejected);
                        Add(b, "", new Point(t, submitted, null, submitted > 0 ? Math.Round(100.0 * rejected / submitted, 2) : null));
                    }
                    else
                    {
                        var targetCount = buckets.Sum(x => x.Events.GetValueOrDefault(target));
                        Add(b, "", new Point(t, buckets.Sum(x => x.Events.Values.Sum()) - targetCount, null, targetCount));
                    }
                }
                return result;
            }

            var apps = aliasesByService[kpi.Domain].Select(a => telemetry[a]).ToList();
            for (var b = first; b < last; b++)
            {
                var t = BucketStart(b);
                var buckets = apps.Select(a => a.GetValueOrDefault(b)).OfType<AppBucket>().ToList();
                if (buckets.Count == 0) continue;
                var requests = buckets.Sum(x => x.Durations.Count);
                switch (kpi.SourceKey, kpi.Grain)
                {
                    case ("appinsights:latency", "domain"):
                        // Domain grain: P95 of a random sample of SamplesPerBucket requests (kpi-latency.kql.template.txt
                        // "partition by BucketStart (sample perBucket)", then KpiBucketStatistics.Build); TotalCount counts every request.
                        // The sample is seeded like the console replay (bucket * 31 + KPI id), so both pick the same requests.
                        Add(b, "", new Point(t, requests, Percentile(Sample(buckets.SelectMany(x => x.Durations).ToArray(), kpi.SamplesPerBucket, b * 31 + kpi.Id), 0.95), null));
                        break;
                    case ("appinsights:exceptions", "domain"):
                        var failed = buckets.Sum(x => x.Failed);
                        Add(b, "", new Point(t, failed, null, requests > 0 ? 100.0 * failed / requests : null));
                        break;
                    case ("appinsights:latency", "entity"):
                    case ("appinsights:exceptions", "entity"):
                        var entities = buckets.SelectMany(x => x.Entities)
                            .GroupBy(e => e.Key, StringComparer.Ordinal)
                            .Select(g => (Key: g.Key, Durations: g.SelectMany(e => e.Value.Durations).ToArray(), Failed: g.Sum(e => e.Value.Failed)))
                            .OrderByDescending(e => e.Durations.Length).ThenByDescending(e => kpi.SourceKey == "appinsights:exceptions" ? e.Failed : 0)
                            .ThenBy(e => e.Key, StringComparer.Ordinal)
                            .Take(kpi.MaxEntities);
                        foreach (var e in entities)
                            Add(b, e.Key, kpi.SourceKey == "appinsights:latency"
                                ? new Point(t, e.Durations.Length, Percentile(e.Durations, 0.95), null)
                                : new Point(t, e.Failed, null, 100.0 * e.Failed / e.Durations.Length));
                        break;
                    case ("appinsights:dependency", "entity"):
                        var calls = buckets.SelectMany(x => x.Partners)
                            .GroupBy(p => p.Key, StringComparer.Ordinal)
                            .Select(g => (Key: g.Key, Calls: g.Sum(p => p.Value.Calls), Failed: g.Sum(p => p.Value.Failed)))
                            .OrderByDescending(p => p.Calls).ThenBy(p => p.Key, StringComparer.Ordinal)
                            .Take(kpi.MaxEntities);
                        foreach (var p in calls) Add(b, p.Key, new Point(t, p.Calls, null, 100.0 * p.Failed / p.Calls));
                        break;
                    default:
                        throw new InvalidOperationException($"{kpi.Label}: source {kpi.SourceKey} is not replayed.");
                }
            }
            return result;
        }

        private static bool Breaches(string direction, double deviation, double t) => direction switch
        {
            "Increase" => deviation > t,
            "Decrease" => deviation < -t,
            "Either" => Math.Abs(deviation) > t,
            _ => false,
        };

        private static double? Observed(Point p, string aggregation) => aggregation switch
        {
            "P95" => p.P95,
            "Count" => p.TotalCount,
            "Value" => p.Value,
            _ => throw new InvalidOperationException($"Aggregation {aggregation} is not replayed."),
        };

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        /// <summary>KpiBucketStatistics.Percentile (linear interpolation); Kusto's percentile() is close to it.</summary>
        private static double? Percentile(double[] values, double p)
        {
            if (values.Length == 0) return null;
            Array.Sort(values);
            var rank = p * (values.Length - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            return values[lower] + (rank - lower) * (values[upper] - values[lower]);
        }

        /// <summary>Kusto's <c>sample n</c>: n rows picked at random (seeded per bucket here), all of them when there are fewer.</summary>
        private static double[] Sample(double[] values, int n, long seed)
        {
            if (values.Length <= n) return values;
            var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
            for (var i = 0; i < n; i++)
            {
                var j = random.Next(i, values.Length);
                (values[i], values[j]) = (values[j], values[i]);
            }
            return values[..n];
        }

        private static long BucketIndex(DateTimeOffset t) => (t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / Bucket.Ticks;

        private static DateTimeOffset BucketStart(long b) => new(DateTimeOffset.UnixEpoch.UtcTicks + b * Bucket.Ticks, TimeSpan.Zero);
    }
}
