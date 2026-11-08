using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoGenerator.Archetypes;
using DemoGenerator.Packs;
using DemoGenerator.Synthesis;
using Xunit.Abstractions;

namespace DemoGenerator.Tests.Packs;

public class AmlPackTests(ITestOutputHelper output)
{
    private static readonly DateTime PackDate = new(2026, 11, 7);

    /// <summary>
    /// Replays an 8-day laptop backfill plus 30 h of live feed (the generator's in-memory reference model) through a
    /// faithful copy of the platform's KPI evaluator (<see cref="KpiReplay"/>), with the series built the way the
    /// platform stores them (domain latency from the KpiSampling sample, entity latency over every row): every
    /// non-peak scenario fires the KPI meant to catch it, the legitimate month-end peak (historical 90 min and a
    /// live-default 60 min run) fires nothing until 2 h after it ends, and no KPI raises more than 2 anomalies a day
    /// outside the incidents.
    /// </summary>
    /// <remarks>
    /// The README's sweep A: 24 resets, one in every hour of the day, spread over all 7 weekdays (2026-11-07 is a
    /// Saturday, 11-08 a Sunday), so every historical and live incident lands at many different hours; tenant-degraded
    /// runs live for 20, 30 and 60 minutes. Only the README's "always" detections are asserted in every run.
    /// </remarks>
    [Theory]
    [InlineData("2026-11-05T00:00:00Z")]
    [InlineData("2026-11-08T01:13:00Z")]
    [InlineData("2026-11-11T02:26:00Z")]
    [InlineData("2026-11-07T03:39:00Z")]
    [InlineData("2026-11-10T04:52:00Z")]
    [InlineData("2026-11-06T05:05:00Z")]
    [InlineData("2026-11-09T06:18:00Z")]
    [InlineData("2026-11-05T07:31:00Z")]
    [InlineData("2026-11-08T08:44:00Z")]
    [InlineData("2026-11-11T09:57:00Z")]
    [InlineData("2026-11-07T10:10:00Z")]
    [InlineData("2026-11-10T11:23:00Z")]
    [InlineData("2026-11-06T12:36:00Z")]
    [InlineData("2026-11-09T13:49:00Z")]
    [InlineData("2026-11-05T14:02:00Z")]
    [InlineData("2026-11-08T15:15:00Z")]
    [InlineData("2026-11-11T16:28:00Z")]
    [InlineData("2026-11-07T17:41:00Z")]
    [InlineData("2026-11-10T18:54:00Z")]
    [InlineData("2026-11-06T19:07:00Z")]
    [InlineData("2026-11-09T20:20:00Z")]
    [InlineData("2026-11-05T21:33:00Z")]
    [InlineData("2026-11-08T22:46:00Z")]
    [InlineData("2026-11-11T23:59:00Z")]
    public void AmlPack_ReplayDetectsScenariosAndIgnoresPeak(string resetAt)
    {
        var replayResetAt = DateTimeOffset.Parse(resetAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var archetype = Load();
        var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000);
        var kpis = KpiReplay.LoadKpis(PackDir());

        // The historical incidents, then live runs of every scenario as a demo would start them after the reset while
        // the feeder keeps writing (minutes = 0: the scenario's default duration).
        ActiveIncident Live(string key, double hoursAfterReset, int minutes = 0)
        {
            var start = SynthesisClock.FloorMinute(replayResetAt + TimeSpan.FromHours(hoursAfterReset));
            return new ActiveIncident(key, start, start + TimeSpan.FromMinutes(minutes > 0 ? minutes : plan.FindScenario(key)!.DefaultDurationMinutes));
        }
        var incidents = plan.HistoricalIncidents(replayResetAt)
            .Concat([
                Live("sanctions-provider-outage", 0.5, 20), Live("tenant-degraded", 3, 20), Live("screening-volume-drop", 5.5),
                Live("idv-provider-degraded", 8.5), Live("month-end-regulatory-peak", 11.5), Live("alert-queue-backlog", 15),
                Live("scoring-model-deploy", 18), Live("tenant-degraded", 21), Live("tenant-degraded", 24, 20), Live("tenant-degraded", 27),
            ])
            .OrderBy(i => i.Start)
            .ToList();

        var replay = KpiReplay.Run(plan, PackDir(), kpis, incidents, replayResetAt - TimeSpan.FromDays(8), replayResetAt + TimeSpan.FromHours(30));
        output.WriteLine(replay.Describe());
        output.WriteLine($"replay took {watch.Elapsed.TotalSeconds:0.0} s");

        // Each scenario fires the KPI meant to catch it while it runs (or within 15 minutes of its end): exactly the
        // README's "always" detections (Latency/Exceptions onboarding under idv-provider-degraded are only "usually").
        var expected = new (string Scenario, string Kpi, string? Entity)[]
        {
            ("scoring-model-deploy", "Latency/screening/domain", null),
            ("sanctions-provider-outage", "Latency/screening/domain", null),
            ("sanctions-provider-outage", "Exceptions/screening/domain", null),
            ("sanctions-provider-outage", "Exceptions/screening/entity", null),
            ("sanctions-provider-outage", "Transactions screened/screening/domain", null),
            ("alert-queue-backlog", "Latency/alerts/domain", null),
            ("alert-queue-backlog", "Exceptions/alerts/domain", null),
            ("alert-queue-backlog", "Alerts raised/alerts/domain", null),
            ("tenant-degraded", "Median latency/screening/entity", "T-003"),
            ("idv-provider-degraded", "Clients approved/onboarding/domain", null),
            ("screening-volume-drop", "Transactions screened/screening/domain", null),
        };
        foreach (var (scenario, kpi, entity) in expected)
        {
            Assert.Contains(kpis, k => k.Label == kpi);
            Assert.All(incidents.Where(i => i.ScenarioKey == scenario), incident =>
                Assert.True(replay.Triggers.Any(t => t.Kpi == kpi && (entity is null || t.Entity == entity)
                        && t.BucketStart >= KpiReplay.FloorBucket(incident.Start) && t.BucketStart < incident.End + TimeSpan.FromMinutes(15)),
                    $"{scenario} at {incident.Start:O} did not trigger {kpi}{(entity is null ? "" : " for " + entity)}"));
        }

        // A pure business drop moves the Business KPI only.
        foreach (var drop in incidents.Where(i => i.ScenarioKey == "screening-volume-drop"))
        {
            var other = replay.Triggers.Where(t => t.Kpi != "Transactions screened/screening/domain"
                && t.BucketStart >= KpiReplay.FloorBucket(drop.Start) && t.BucketStart < drop.End + TimeSpan.FromMinutes(15)).ToList();
            Assert.True(other.Count == 0, $"screening-volume-drop at {drop.Start:O} also fired: {string.Join("; ", other)}");
        }

        // One degraded customer is told apart from the platform: no other customer series fires while it lasts.
        foreach (var tenant in incidents.Where(i => i.ScenarioKey == "tenant-degraded"))
        {
            var others = replay.Triggers.Where(t => t.Entity is not null && t.Entity != "T-003"
                && t.BucketStart >= KpiReplay.FloorBucket(tenant.Start) && t.BucketStart < tenant.End + TimeSpan.FromMinutes(15)).ToList();
            Assert.True(others.Count == 0, $"tenant-degraded at {tenant.Start:O} also fired other customers: {string.Join("; ", others)}");
            var first = replay.Triggers.FirstOrDefault(t => t.Kpi == "Median latency/screening/entity" && t.Entity == "T-003" && t.BucketStart >= KpiReplay.FloorBucket(tenant.Start));
            Assert.True(first is not null && first.BucketStart + TimeSpan.FromMinutes(5) - tenant.Start <= TimeSpan.FromMinutes(30),
                $"tenant-degraded at {tenant.Start:O}: T-003 did not fire within 30 minutes (first: {first})");
            output.WriteLine($"tenant-degraded {tenant.Start:MM-dd HH:mm} ({(tenant.End - tenant.Start).TotalMinutes:0} min): T-003 fired when bucket {first!.BucketStart:HH:mm} closed, {(first.BucketStart + TimeSpan.FromMinutes(5) - tenant.Start).TotalMinutes:0} min after the start");
        }

        // The legitimate peak fires nothing, during it or in the two hours after it ends.
        foreach (var peak in incidents.Where(i => i.ScenarioKey == "month-end-regulatory-peak"))
        {
            var during = replay.Triggers.Where(t => t.BucketStart >= KpiReplay.FloorBucket(peak.Start) && t.BucketStart < peak.End + TimeSpan.FromHours(2)).ToList();
            Assert.True(during.Count == 0, $"the legitimate peak at {peak.Start:O} triggered: {string.Join("; ", during)}");
        }

        // Outside the incidents (and the two hours after each), at most 2 anomalies per KPI per UTC day.
        var outside = replay.Triggers
            .Where(t => !incidents.Any(i => t.BucketStart >= KpiReplay.FloorBucket(i.Start) && t.BucketStart < i.End + TimeSpan.FromHours(2)))
            .GroupBy(t => (t.Kpi, Day: t.BucketStart.UtcDateTime.Date))
            .Where(g => g.Count() > 2)
            .Select(g => $"{g.Key.Kpi} {g.Key.Day:yyyy-MM-dd}: {g.Count()}")
            .ToList();
        Assert.True(outside.Count == 0, "false anomalies above 2/day: " + string.Join("; ", outside));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(180), $"replay took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void AmlPack_IsValid()
    {
        var dir = PackDir();

        var errors = PackValidator.Validate(dir);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        var a = Load();
        Assert.Equal("aml", a.CompanyKey);
        Assert.Equal("kinectify", a.ProfileKey);
        Assert.Equal("Kinectify (demo)", a.DisplayName);
        Assert.Equal(
            ["demo-aml-alerts-worker", "demo-aml-case-mgmt", "demo-aml-onboarding-kyc", "demo-aml-screening-api"],
            a.Apps!.Select(x => x.AppId!).Order(StringComparer.Ordinal));
        Assert.All(a.Apps!, app => Assert.Equal(app.AppId!["demo-".Length..], app.Database));
        Assert.Equal("CustomerTenantId", a.Entities!.Dimension);

        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "profile.json")));
        var partners = profile.RootElement.GetProperty("partners").EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        Assert.Contains("sanctions-provider", partners);
        Assert.Contains("idv-provider", partners);
        Assert.Contains("chain-analytics", partners);
        Assert.Contains("doc-ocr", partners);

        var privacy = profile.RootElement.GetProperty("privacy");
        Assert.False(privacy.GetProperty("allowRawEvidence").GetBoolean());
        var pii = privacy.GetProperty("piiDimensions").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("CustomerName", pii);
        Assert.Contains("AccountNumber", pii);
        Assert.Contains("DateOfBirth", pii);
        Assert.NotEmpty(privacy.GetProperty("patterns").EnumerateArray());

        // Only fictional hosts: the demo company itself, .demo partners or internal names.
        var hosts = a.Steps!.SelectMany(s => s.Dependencies ?? []).Select(d => d.Target!).Concat(a.Apps!.Select(x => x.Host!));
        Assert.All(hosts, h => Assert.True(h.EndsWith(".demo", StringComparison.Ordinal) || h.EndsWith(".kinectify.internal", StringComparison.Ordinal), h));
    }

    [Fact]
    public void AmlPack_HasScenarioLibrary()
    {
        var a = Load();
        var scenarios = a.Scenarios!;
        bool Has(ScenarioModel s, string kind) => s.Effects!.Any(e => e.Kind == kind);

        Assert.True(scenarios.Count >= 6, $"expected at least 6 scenarios, got {scenarios.Count}");
        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());

        // Partner outage on the sanctions list provider.
        var outage = scenarios.Single(s => s.Key == "sanctions-provider-outage");
        Assert.Contains(outage.Effects!, e => e.Kind == EffectKinds.DependencyFailure && e.Partner == "sanctions-provider" && e.Rate >= 0.5);

        // Latency after a model deploy.
        var deploy = scenarios.Single(s => s.Key == "scoring-model-deploy");
        Assert.Contains(deploy.Effects!, e => e.Kind == EffectKinds.LatencyMultiplier && e.Factor >= 3);

        // Exception storm on one step (alert queue backlog).
        var storm = scenarios.Single(s => s.Key == "alert-queue-backlog");
        Assert.Contains(storm.Effects!, e => e.Kind == EffectKinds.ErrorRate && e.Step == "PROCESS alert-generation" && e.Rate >= 0.2);

        // One customer tenant degraded.
        var tenant = scenarios.Single(s => s.Key == "tenant-degraded");
        var only = Assert.Single(tenant.Effects!, e => e.Kind == EffectKinds.EntityOnly);
        Assert.All(only.Entities!, id => Assert.Contains(id, ArchetypeLoader.EntityIds(a.Entities)));

        // Business drop: screened transactions fall with no errors at all.
        var drop = scenarios.Single(s => s.Key == "screening-volume-drop");
        Assert.All(drop.Effects!, e => Assert.Equal(EffectKinds.TrafficMultiplier, e.Kind));
        Assert.Contains(drop.Effects!, e => e.Step is { } step && e.Factor < 1
            && a.Steps!.Single(x => x.Name == step).BusinessEvent?.Name == "TransactionScreened");

        // Legitimate month-end peak: more traffic, nothing broken.
        var peak = scenarios.Single(s => s.Key == "month-end-regulatory-peak");
        var peakEffect = Assert.Single(peak.Effects!);
        Assert.Equal(EffectKinds.TrafficMultiplier, peakEffect.Kind);
        Assert.Null(peakEffect.Step);
        Assert.True(peakEffect.Factor > 1);
        Assert.False(Has(peak, EffectKinds.ErrorRate) || Has(peak, EffectKinds.LatencyMultiplier) || Has(peak, EffectKinds.DependencyFailure));

        // A scenario exception type is one the step really throws, so the generated message is credible.
        foreach (var e in scenarios.SelectMany(s => s.Effects!).Where(e => e.Kind == EffectKinds.ErrorRate && e.ExceptionType is not null))
            Assert.Contains(a.Steps!.Single(x => x.Name == e.Step).Exceptions!, x => x.Type == e.ExceptionType);
    }

    [Fact]
    public void AmlPack_HasHistoricalIncidentsAndBusinessKpis()
    {
        var a = Load();
        var incidents = a.HistoricalIncidents!;

        var alerting = incidents.Where(i => i.Scenario != "month-end-regulatory-peak").ToList();
        Assert.True(alerting.Count >= 3, $"expected at least 3 alerting historical incidents, got {alerting.Count}");
        Assert.All(incidents, i =>
        {
            Assert.InRange(i.HoursAgo, 1, 72);
            Assert.True(i.HoursAgo * 60 > i.DurationMinutes, $"{i.Scenario} must end before the reset");
        });
        var windows = incidents.Select(i => (Start: -i.HoursAgo * 60, End: -i.HoursAgo * 60 + i.DurationMinutes)).OrderBy(w => w.Start).ToList();
        for (var i = 1; i < windows.Count; i++)
            Assert.True(windows[i].Start >= windows[i - 1].End + 60, "historical incidents must be at least an hour apart");

        using var kpis = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir(), "kpis.json")));
        var business = kpis.RootElement.EnumerateArray()
            .Where(k => k.GetProperty("pillar").GetString() == "Business" && k.GetProperty("sourceKey").GetString()!.StartsWith("kql:kpi:", StringComparison.Ordinal))
            .ToList();
        foreach (var ev in new[] { "TransactionScreened", "AlertRaised" })
        {
            var kpi = Assert.Single(business, k => k.GetProperty("kqlText").GetString()!.Contains($"\"{ev}\"", StringComparison.Ordinal));
            Assert.Contains(".customEvents", kpi.GetProperty("kqlText").GetString(), StringComparison.Ordinal);
            // Zero-filled: a bucket without events is a 0, so a total stop is evaluated instead of being a missing bucket.
            Assert.Contains("range BucketStart from", kpi.GetProperty("kqlText").GetString(), StringComparison.Ordinal);
            Assert.Contains("join kind=leftouter", kpi.GetProperty("kqlText").GetString(), StringComparison.Ordinal);
            Assert.Equal("Decrease", kpi.GetProperty("direction").GetString());
            Assert.Equal("kinectify", kpi.GetProperty("profileKey").GetString());
        }

        // Privacy patterns redact the fake PII the generator writes into exception messages, not technical ids.
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir(), "profile.json")));
        var patterns = profile.RootElement.GetProperty("privacy").GetProperty("patterns").EnumerateArray()
            .Select(p => new Regex(p.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToList();
        bool Redacts(string text) => patterns.Any(p => p.IsMatch(text));
        var messages = a.Steps!.SelectMany(s => s.Exceptions ?? []).Select(x => x.Message!).ToList();
        Assert.Contains(messages, m => m.Contains("account 0000", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("@example.test", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("wallet 0x", StringComparison.Ordinal));
        Assert.All(messages.Where(m => m.Contains("account 0000", StringComparison.Ordinal) || m.Contains('@') || m.Contains("wallet 0x", StringComparison.Ordinal)),
            m => Assert.True(Redacts(m), m));
        Assert.False(Redacts("Transaction TX-0000000042 is unknown for this customer"));
        Assert.False(Redacts("Analyst is not assigned to case CASE-000123"));
    }

    [Fact]
    public void AmlPack_CorpusMatchesScenarios()
    {
        var a = Load();
        var corpus = Path.Combine(PackDir(), "corpus");
        var files = Directory.GetFiles(corpus, "*.md", SearchOption.AllDirectories);
        Assert.True(files.Length >= 10, $"expected at least 10 RCAs, got {files.Length}");

        var scenarios = a.Scenarios!.ToDictionary(s => s.Key!, StringComparer.Ordinal);
        var stepNames = a.Steps!.Select(s => s.Name!).ToList();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(corpus, file);
            var text = File.ReadAllText(file);
            foreach (var section in new[] { "Root Cause Analysis", "Bottom Line Up Front", "What Happened", "5 Whys", "Corrective Actions" })
                Assert.True(text.Contains(section, StringComparison.Ordinal), $"{name} lacks '{section}'");
            Assert.Contains("Kinectify (demo)", text, StringComparison.Ordinal);

            var date = Regex.Match(text, @"\*\*Date:\*\*\s*(?<d>\d{4}-\d{2}-\d{2})");
            Assert.True(date.Success, $"{name} has no **Date:** line");
            var when = DateTime.ParseExact(date.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.InRange(when, PackDate.AddMonths(-18), PackDate);

            var key = Regex.Match(text, @"\*\*Scenario:\*\*\s*(?<key>[a-z0-9-]+)").Groups["key"].Value;
            Assert.True(scenarios.TryGetValue(key, out var scenario), $"{name} names no known scenario ('{key}')");
            covered.Add(key);

            // Coherent with its scenario: same steps and the same partners.
            var effectSteps = scenario.Effects!.Where(e => e.Step is not null).Select(e => e.Step!).ToList();
            if (effectSteps.Count > 0)
                Assert.True(effectSteps.Any(s => text.Contains(s, StringComparison.Ordinal)), $"{name} does not name a step of '{key}'");
            else
                Assert.True(stepNames.Any(s => text.Contains(s, StringComparison.Ordinal)) || text.Contains("all stages", StringComparison.Ordinal), $"{name} names no archetype step");
            foreach (var partner in scenario.Effects!.Where(e => e.Partner is not null).Select(e => e.Partner!).Distinct())
                Assert.True(text.Contains(partner, StringComparison.Ordinal), $"{name} does not name partner '{partner}' of '{key}'");

            foreach (Match email in Regex.Matches(text, @"[\w.+-]+@[\w.-]+"))
                Assert.EndsWith("@example.test", email.Value, StringComparison.Ordinal);
        }

        Assert.Empty(scenarios.Keys.Except(covered));
    }

    private static Archetype Load() => ArchetypeLoader.Load(Path.Combine(PackDir(), "archetype.json"));

    /// <summary>The real pack under Portal/demo/packs/aml, found by walking up from the test output folder.</summary>
    private static string PackDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "packs", "aml"), Path.Combine(dir.FullName, "demo", "packs", "aml") })
                if (File.Exists(Path.Combine(candidate, "archetype.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Portal/demo/packs/aml not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>One KPI of a pack's kpis.json, as the replay needs it.</summary>
public sealed record ReplayKpi(
    string Kpi, string Domain, string Grain, string SourceKey, string Direction, string Aggregation, string Method,
    double Threshold, int LookbackDays, double ThresholdPercent, int WindowBuckets, double ZThreshold,
    int MinObservations, int ConsecutiveCount, int CooldownMinutes, TimeSpan BucketSize, string? EventName, string? EventSource,
    int SampleSize = 0, TimeSpan LookbackWindow = default, int MaxEntitiesPerBucket = 100)
{
    /// <summary>
    /// Portal/src/JourneyMonitor.Core/Monitoring/Ingestion/KpiSampling.cs SamplesPerBucket:
    /// SampleSize == 0 ? 0 : ceil(SampleSize / max(1, LookbackWindow / BucketSize)).
    /// </summary>
    public int SamplesPerBucket => SampleSize <= 0 ? 0 : (int)Math.Ceiling(SampleSize / Math.Max(1d, LookbackWindow / BucketSize));

    public string Label => $"{Kpi}/{Domain}/{Grain}";
}

/// <summary>One bucket of one series: what the platform stores in monitoring.KpiBucket.</summary>
public sealed record ReplayBucket(DateTimeOffset BucketStart, long TotalCount, double? P95, double? Value, double? P50 = null);

public sealed record ReplayTrigger(string Kpi, string? Entity, DateTimeOffset BucketStart, double Observed, double Reference, double Deviation)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kpi}{(Entity is null ? "" : "[" + Entity + "]")} @ {BucketStart:MM-dd HH:mm} obs {Observed:0.##} ref {Reference:0.##} dev {Deviation:0.##}");
}

public sealed record ReplayResult(IReadOnlyList<ReplayKpi> Kpis, IReadOnlyList<ActiveIncident> Incidents, IReadOnlyList<ReplayTrigger> Triggers)
{
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        bool During(ReplayTrigger t, ActiveIncident i) => t.BucketStart >= KpiReplay.FloorBucket(i.Start) && t.BucketStart < i.End + TimeSpan.FromMinutes(15);
        bool Near(ReplayTrigger t, ActiveIncident i) => t.BucketStart >= KpiReplay.FloorBucket(i.Start) && t.BucketStart < i.End + TimeSpan.FromHours(2);
        sb.AppendLine("KPI | " + string.Join(" | ", Incidents.Select(i => $"{i.ScenarioKey}@{i.Start:dd HH:mm}")) + " | outside incidents +2 h (max/day, total)");
        foreach (var k in Kpis)
        {
            var mine = Triggers.Where(t => t.Kpi == k.Label).ToList();
            var cells = Incidents.Select(i => mine.Count(t => During(t, i)).ToString(CultureInfo.InvariantCulture));
            var outside = mine.Where(t => !Incidents.Any(i => Near(t, i))).ToList();
            var max = outside.GroupBy(t => t.BucketStart.UtcDateTime.Date).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            sb.AppendLine($"{k.Label} | {string.Join(" | ", cells)} | {max}, {outside.Count}");
        }
        foreach (var t in Triggers) sb.AppendLine("  " + t);
        return sb.ToString();
    }
}

/// <summary>
/// Replays a pack through the KPI rules the way the platform evaluates them, without referencing the platform:
/// series are built from the generator's in-memory reference model (<see cref="ReferenceSynthesizer"/>) the way the
/// platform ingests them (Portal/src/JourneyMonitor.Infrastructure/Kql/kpi-latency.kql.template.txt and
/// kpi-exceptions.kql.template.txt; the pack's own kqlText for business KPIs): domain latency from
/// KpiSampling.SamplesPerBucket random rows per bucket (KpiBucketAssembler + KpiBucketStatistics.Build), entity
/// latency from every row of the top maxEntitiesPerBucket entities. Each series is evaluated by a
/// minimal faithful copy of Portal/src/JourneyMonitor.Core/Monitoring/Kpi/KpiEvaluator.cs and its methods
/// (Methods/StaticThresholdMethod.cs, BaselinePercentMethod.cs, RobustZScoreMethod.cs, KpiStatistics.cs):
/// minObservationsPerBucket, consecutiveCount, cooldown, "not evaluated" leaving the counter untouched, a
/// same-slot median over lookbackDays (at least min(3, days) slots) and a robust z with MAD = 0 not evaluated.
/// </summary>
public static class KpiReplay
{
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    public static DateTimeOffset FloorBucket(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % Bucket.Ticks, TimeSpan.Zero);

    public static IReadOnlyList<ReplayKpi> LoadKpis(string packDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packDir, "kpis.json")));
        var result = new List<ReplayKpi>();
        foreach (var k in doc.RootElement.EnumerateArray())
        {
            using var p = JsonDocument.Parse(k.GetProperty("methodParamsJson").GetString()!);
            double D(string name) => p.RootElement.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
            string? eventName = null, eventSource = null;
            if (k.TryGetProperty("kqlText", out var kql))
            {
                var text = kql.GetString()!;
                eventName = Regex.Match(text, "name == \"(?<n>[A-Za-z]+)\"").Groups["n"].Value;
                eventSource = Regex.Match(text, @"\{\{SOURCE:(?<a>[a-z0-9-]+)\}\}").Groups["a"].Value;
            }
            result.Add(new ReplayKpi(
                k.GetProperty("kpi").GetString()!, k.GetProperty("domain").GetString()!, k.GetProperty("grain").GetString()!,
                k.GetProperty("sourceKey").GetString()!, k.GetProperty("direction").GetString()!, k.GetProperty("aggregation").GetString()!,
                k.GetProperty("detectionMethod").GetString()!, D("threshold"), (int)D("lookbackDays"), D("thresholdPercent"),
                (int)D("windowBuckets"), D("zThreshold"), k.GetProperty("minObservationsPerBucket").GetInt32(),
                k.GetProperty("consecutiveCount").GetInt32(), k.GetProperty("cooldownMinutes").GetInt32(),
                TimeSpan.Parse(k.GetProperty("bucketSize").GetString()!, CultureInfo.InvariantCulture), eventName, eventSource,
                k.GetProperty("sampleSize").GetInt32(),
                TimeSpan.Parse(k.GetProperty("lookbackWindow").GetString()!, CultureInfo.InvariantCulture),
                k.GetProperty("maxEntitiesPerBucket").GetInt32()));
        }
        return result;
    }

    public static ReplayResult Run(CompanyPlan plan, string packDir, IReadOnlyList<ReplayKpi> kpis, IReadOnlyList<ActiveIncident> incidents, DateTimeOffset from, DateTimeOffset to)
    {
        // Service key -> source aliases (= app databases), from the profile the KPI templates are rendered with.
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(packDir, "profile.json")));
        var aliasesByService = profile.RootElement.GetProperty("services").EnumerateArray().ToDictionary(
            s => s.GetProperty("key").GetString()!,
            s => s.GetProperty("telemetrySourceAliases").EnumerateArray().Select(a => a.GetString()!).ToList(),
            StringComparer.Ordinal);

        var telemetry = plan.Apps.AsParallel().Select(app => (app.Database, Rows: AppTelemetry.Generate(plan, app, incidents, from, to)))
            .ToDictionary(x => x.Database, x => x.Rows, StringComparer.Ordinal);

        // Only whole buckets inside the backfill are evaluated (the first and last ones are partial).
        var firstBucket = FloorBucket(from) == from ? from : FloorBucket(from) + Bucket;
        var lastBucketEnd = FloorBucket(to);

        // Request buckets of each product (one source alias per service in this pack).
        bool Inside(long b) => BucketTime(b) >= firstBucket && BucketTime(b) < lastBucketEnd;
        var bucketsByDomain = kpis.Where(k => k.EventName is null).Select(k => k.Domain).Distinct().ToDictionary(
            d => d,
            d => aliasesByService[d] is [var alias]
                ? telemetry[alias].Buckets.Where(x => Inside(x.Index)).ToList()
                : throw new NotSupportedException($"service {d} has {aliasesByService[d].Count} source aliases; the replay expects one"),
            StringComparer.Ordinal);

        var triggers = kpis.AsParallel().SelectMany(kpi =>
        {
            var series = BuildSeries(kpi, bucketsByDomain, telemetry, firstBucket, lastBucketEnd);
            return series.SelectMany(s => Evaluate(kpi, s.Key.Length == 0 ? null : s.Key, s.Value));
        }).OrderBy(t => t.BucketStart).ThenBy(t => t.Kpi, StringComparer.Ordinal).ThenBy(t => t.Entity, StringComparer.Ordinal).ToList();

        return new ReplayResult(kpis, incidents, triggers);
    }

    private static Dictionary<string, List<ReplayBucket>> BuildSeries(
        ReplayKpi kpi, IReadOnlyDictionary<string, List<RequestBucket>> bucketsByDomain, IReadOnlyDictionary<string, AppTelemetry> telemetry,
        DateTimeOffset firstBucket, DateTimeOffset lastBucketEnd)
    {
        var result = new Dictionary<string, List<ReplayBucket>>(StringComparer.Ordinal);

        if (kpi.EventName is not null)
        {
            // Business KPI: zero-filled count of one customEvent per bucket.
            var events = telemetry[kpi.EventSource!].Events;
            var list = new List<ReplayBucket>();
            for (var t = firstBucket; t < lastBucketEnd; t += Bucket)
            {
                var n = events.TryGetValue((BucketIndex(t), kpi.EventName), out var c) ? c : 0;
                list.Add(new ReplayBucket(t, n, null, n));
            }
            result[""] = list;
            return result;
        }

        var latency = kpi.SourceKey == "appinsights:latency";
        var samplesPerBucket = kpi.SamplesPerBucket;
        void Add(string key, ReplayBucket bucket)
        {
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(bucket);
        }
        foreach (var b in bucketsByDomain[kpi.Domain])
        {
            var start = BucketTime(b.Index);
            if (kpi.Grain == "entity")
            {
                // Entity grain keeps the maxEntitiesPerBucket entities with the most requests (kpi-latency/-exceptions
                // templates: sort by TotalCount desc, Entity asc | where EntityRank <= maxEntities). Entity rows:
                // Kusto percentile() over every row of the entity, nearest rank.
                foreach (var e in b.Entities.Take(kpi.MaxEntitiesPerBucket))
                {
                    Add(e.Entity, latency
                        ? new ReplayBucket(start, e.Count, NearestRank(e.SortedDurations, 0.95), null, NearestRank(e.SortedDurations, 0.5))
                        : new ReplayBucket(start, e.Failed, null, 100.0 * e.Failed / e.Count));
                }
            }
            else if (!latency)
            {
                Add("", new ReplayBucket(start, b.Failed, null, 100.0 * b.Failed / b.Count));
            }
            else
            {
                // Domain rows: the platform never sees every duration. kpi-latency.kql.template.txt returns
                // `sample {{perBucket}}` rows per bucket (perBucket = KpiSampling.SamplesPerBucket),
                // KpiBucketAssembler keeps at most that many and KpiBucketStatistics.Build takes PERCENTILE.INC
                // of them; TotalCount still counts every request.
                var sample = Sample((double[])b.Durations.Clone(), samplesPerBucket, StableSeed(kpi.Label, b.Index));
                Add("", new ReplayBucket(start, b.Count, Percentile(sample, 0.95), null, Percentile(sample, 0.5)));
            }
        }
        foreach (var list in result.Values) list.Sort((a, b) => a.BucketStart.CompareTo(b.BucketStart));
        return result;
    }

    /// <summary>KpiEvaluator.Evaluate over a whole series in order, from the initial state.</summary>
    public static IEnumerable<ReplayTrigger> Evaluate(ReplayKpi kpi, string? entity, IReadOnlyList<ReplayBucket> series)
    {
        var seen = new Dictionary<DateTimeOffset, ReplayBucket>();
        var consecutive = 0;
        DateTimeOffset? cooldownUntil = null;
        var triggers = new List<ReplayTrigger>();

        foreach (var bucket in series)
        {
            var verdict = EvaluateBucket(kpi, bucket, seen);
            seen[bucket.BucketStart] = bucket;
            if (verdict is not { } v) continue;
            if (!v.Anomalous)
            {
                consecutive = 0;
                continue;
            }

            var count = consecutive + 1;
            var bucketEnd = bucket.BucketStart + kpi.BucketSize;
            consecutive = count;
            if (cooldownUntil is { } until && until > bucketEnd) continue;
            if (count >= kpi.ConsecutiveCount)
            {
                cooldownUntil = bucketEnd + TimeSpan.FromMinutes(kpi.CooldownMinutes);
                triggers.Add(new ReplayTrigger(kpi.Label, entity, bucket.BucketStart, v.Observed, v.Reference, v.Deviation));
            }
        }
        return triggers;
    }

    /// <summary>Null when the platform would answer NotEvaluated (state unchanged but for the last bucket).</summary>
    private static (bool Anomalous, double Observed, double Reference, double Deviation)? EvaluateBucket(
        ReplayKpi kpi, ReplayBucket bucket, IReadOnlyDictionary<DateTimeOffset, ReplayBucket> previous)
    {
        if (bucket.TotalCount < kpi.MinObservations) return null;
        if (Observed(bucket, kpi.Aggregation) is not { } observed) return null;

        double reference, deviation;
        switch (kpi.Method)
        {
            case "StaticThreshold":
                (reference, deviation) = (kpi.Threshold, observed - kpi.Threshold);
                var breached = kpi.Direction switch
                {
                    "Increase" => observed > kpi.Threshold,
                    "Decrease" => observed < kpi.Threshold,
                    _ => false,
                };
                return (breached, observed, reference, deviation);

            case "BaselinePercent":
            {
                var values = Enumerable.Range(1, kpi.LookbackDays)
                    .Select(k => previous.TryGetValue(bucket.BucketStart.AddDays(-k), out var b) ? Observed(b, kpi.Aggregation) : null)
                    .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (values.Count < Math.Min(3, kpi.LookbackDays)) return null;
                reference = Median(values);
                if (reference <= 0) return null;
                deviation = (observed - reference) / reference * 100.0;
                return (Breaches(kpi.Direction, deviation, kpi.ThresholdPercent), observed, reference, deviation);
            }

            case "RobustZScore":
            {
                var values = Enumerable.Range(1, kpi.WindowBuckets)
                    .Select(k => previous.TryGetValue(bucket.BucketStart - kpi.BucketSize * k, out var b) ? Observed(b, kpi.Aggregation) : null)
                    .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (values.Count < (kpi.WindowBuckets + 1) / 2) return null;
                reference = Median(values);
                var scale = 1.4826 * Median(values.Select(v => Math.Abs(v - reference)));
                if (scale == 0) return null;
                deviation = (observed - reference) / scale;
                return (Breaches(kpi.Direction, deviation, kpi.ZThreshold), observed, reference, deviation);
            }

            default:
                throw new InvalidOperationException($"Unsupported detection method {kpi.Method}.");
        }
    }

    private static bool Breaches(string direction, double deviation, double t) => direction switch
    {
        "Increase" => deviation > t,
        "Decrease" => deviation < -t,
        "Either" => Math.Abs(deviation) > t,
        _ => false,
    };

    private static double? Observed(ReplayBucket b, string aggregation) => aggregation switch
    {
        "P95" => b.P95,
        "P50" => b.P50,
        "Count" => b.TotalCount,
        "Value" => b.Value,
        _ => throw new InvalidOperationException($"Aggregation {aggregation} is not replayed."),
    };

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>KpiBucketStatistics.Percentile (PERCENTILE.INC, linear interpolation over the sorted values).</summary>
    private static double? Percentile(double[] values, double p)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        var rank = p * (values.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return values[lower] + (rank - lower) * (values[upper] - values[lower]);
    }

    /// <summary>Kusto percentile() on the stored rows of a small group: the nearest-rank value.</summary>
    private static double? NearestRank(double[] sorted, double p) =>
        sorted.Length == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];

    /// <summary>Kusto `sample n`: n rows picked at random (deterministic here), or every row when there are fewer. 0 = none.</summary>
    private static double[] Sample(double[] values, int n, int seed)
    {
        if (n <= 0) return [];
        if (values.Length <= n) return values;
        var random = new Random(seed);
        for (var i = 0; i < n; i++)
        {
            var j = random.Next(i, values.Length);
            (values[i], values[j]) = (values[j], values[i]);
        }
        return values[..n];
    }

    private static int StableSeed(string label, long bucket)
    {
        var h = (int)(bucket % int.MaxValue);
        foreach (var c in label) h = unchecked(h * 31 + c);
        return h;
    }

    private static long BucketIndex(DateTimeOffset t) => (t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / Bucket.Ticks;

    private static DateTimeOffset BucketTime(long index) => DateTimeOffset.UnixEpoch + Bucket * index;

    /// <summary>One entity's requests in a bucket: count, failed requests and sorted durations.</summary>
    public sealed record EntityBucket(string Entity, int Count, int Failed, double[] SortedDurations);

    /// <summary>
    /// One bucket of an app's requests: count, failed requests (a failed request writes exactly one exception), every
    /// duration in generation order and the entities ranked by request count desc, entity asc.
    /// </summary>
    public sealed record RequestBucket(long Index, int Count, int Failed, double[] Durations, EntityBucket[] Entities);

    /// <summary>What the KPI templates read from one app: request buckets and customEvent counts.</summary>
    public sealed class AppTelemetry
    {
        public List<RequestBucket> Buckets { get; } = [];
        public Dictionary<(long Bucket, string Name), int> Events { get; } = [];

        // ReferenceSynthesizer seeds every minute on (app seed, epoch minute) and an effect only changes the minutes
        // inside [Start, End): an hour no effect touches is the same rows at every reset, so the sweep builds it once.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long Seed, string App, DateTimeOffset Hour), Hour> QuietHours = new();

        private sealed record Hour(List<RequestBucket> Buckets, Dictionary<(long Bucket, string Name), int> Events);

        public static AppTelemetry Generate(CompanyPlan plan, AppPlan app, IReadOnlyList<ActiveIncident> incidents, DateTimeOffset from, DateTimeOffset to)
        {
            var effects = EffectCompiler.Compile(plan, app, incidents);
            var hour = TimeSpan.FromHours(1);
            var chunks = new List<(DateTimeOffset From, DateTimeOffset To)>();
            for (var t = new DateTimeOffset(from.UtcTicks - from.UtcTicks % hour.Ticks, TimeSpan.Zero); t < to; t += hour)
                chunks.Add((t < from ? from : t, t + hour < to ? t + hour : to));

            Hour Synthesize(DateTimeOffset chunkFrom, DateTimeOffset chunkTo)
            {
                var batch = ReferenceSynthesizer.Generate(app, effects, chunkFrom, chunkTo);
                var buckets = batch.Requests.GroupBy(r => BucketIndex(r.Timestamp)).Select(g =>
                {
                    var rows = g.ToArray();
                    var entities = rows.GroupBy(r => r.Entity)
                        .Select(e => new EntityBucket(e.Key, e.Count(), e.Count(r => !r.Success), e.Select(r => r.Duration).Order().ToArray()))
                        .OrderByDescending(e => e.Count).ThenBy(e => e.Entity, StringComparer.Ordinal).ToArray();
                    return new RequestBucket(g.Key, rows.Length, rows.Count(r => !r.Success), rows.Select(r => r.Duration).ToArray(), entities);
                }).ToList();
                var events = new Dictionary<(long Bucket, string Name), int>();
                foreach (var e in batch.CustomEvents)
                {
                    var key = (BucketIndex(e.Timestamp), e.Name);
                    events[key] = events.TryGetValue(key, out var n) ? n + 1 : 1;
                }
                return new Hour(buckets, events);
            }

            var hours = chunks.AsParallel().AsOrdered().Select(c =>
                c.To - c.From == hour && !effects.Any(e => e.Start < c.To && e.End > c.From)
                    ? QuietHours.GetOrAdd((app.Seed, app.Database, c.From), _ => Synthesize(c.From, c.To))
                    : Synthesize(c.From, c.To)).ToList();

            // Chunks are whole hours (but the first and last), so no bucket spans two chunks.
            var result = new AppTelemetry();
            foreach (var h in hours)
            {
                result.Buckets.AddRange(h.Buckets);
                foreach (var (key, n) in h.Events) result.Events[key] = result.Events.TryGetValue(key, out var m) ? m + n : n;
            }
            return result;
        }
    }
}
