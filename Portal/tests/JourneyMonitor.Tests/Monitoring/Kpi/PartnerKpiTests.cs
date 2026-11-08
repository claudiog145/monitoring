using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using JourneyMonitor.Tests.Spec1000.A2;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

/// <summary>
/// AG4 (contract v4 §5): partners in the company profile, the <c>appinsights:dependency</c> KPI source with
/// one entity series per partner, and the opt-in Triage path of a partner anomaly (stage <c>partner:{key}</c>).
/// Partners here are synthetic: the rules are under test, not any company's vendors.
/// </summary>
[Collection(A1MonitorRunLockCollection.Name)]
public class PartnerKpiTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(17).AddMinutes(10);

    private static PartnerDefinition Partner(string key, params PartnerMatcher[] matchers) => new(key, key + " Inc.", matchers);

    private static IReadOnlyList<PartnerDefinition> TwoPartners() =>
    [
        Partner("watchlist", new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "lists.example.test")),
        Partner("pay", new PartnerMatcher(PartnerMatcherKind.DependencyNameContains, "POST /charge"),
            new PartnerMatcher(PartnerMatcherKind.Regex, @"gateway-\d+\.example\.test")),
    ];

    private static CompanyProfile WithPartners(IReadOnlyList<PartnerDefinition>? partners) =>
        KpiSourceDoubles.Incumbent() with { Partners = partners };

    private static IReadOnlyList<CompanyProfileFinding> PartnerFindings(CompanyProfile profile) =>
        CompanyProfileValidator.Validate(profile).Where(f => f.Path.StartsWith("$.partners", StringComparison.Ordinal)).ToList();

    // ------------------------------------------------------------------ profile

    [Fact]
    public void Profile_PartnersOptionalAndValidated()
    {
        // Optional: absent and empty are both valid and raise nothing.
        Assert.Empty(PartnerFindings(WithPartners(null)));
        Assert.Empty(PartnerFindings(WithPartners([])));
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(KpiSourceDoubles.Incumbent(), web);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<CompanyProfile>(json.Replace("\"partners\":null,", "", StringComparison.Ordinal), web);
        Assert.Null(parsed!.Partners);
        Assert.False(CompanyProfileValidator.HasErrors(CompanyProfileValidator.Validate(parsed)));

        // Every declared matcher kind is accepted (kinds compare trimmed and case-insensitively).
        var all = WithPartners(
        [
            Partner("a", new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "a.example.test")),
            Partner("b", new PartnerMatcher(PartnerMatcherKind.DependencyNameContains, "GET /b")),
            Partner("c", new PartnerMatcher(PartnerMatcherKind.DependencyTypeEquals, "HTTP")),
            Partner("d", new PartnerMatcher(PartnerMatcherKind.Regex, @"^d-\d+$")),
        ]);
        Assert.Empty(PartnerFindings(all));

        static string Code(IReadOnlyList<PartnerDefinition> partners, string path)
        {
            var findings = PartnerFindings(WithPartners(partners));
            var finding = Assert.Single(findings, f => f.IsError);
            Assert.Equal(path, finding.Path);
            return finding.Code;
        }

        var ok = new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "x.example.test");

        // Unique keys, compared case-insensitively.
        Assert.Equal("partner-key-duplicate", Code([Partner("pay", ok), Partner("PAY", ok)], "$.partners[1].key"));
        Assert.Equal("partner-key-missing", Code([Partner(" ", ok)], "$.partners[0].key"));
        Assert.Equal("partner-key-too-long", Code([Partner(new string('k', CompanyProfileValidator.MaxPartnerKeyLength + 1), ok)], "$.partners[0].key"));

        // At least one matcher, of a known kind, with a value.
        Assert.Equal("partner-matchers-empty", Code([Partner("pay")], "$.partners[0].matchers"));
        Assert.Equal("partner-matchers-empty", Code([new PartnerDefinition("pay", "Pay", null!)], "$.partners[0].matchers"));
        Assert.Equal("partner-matcher-kind-unknown", Code([Partner("pay", new PartnerMatcher("url-contains", "x"))], "$.partners[0].matchers[0].kind"));
        Assert.Equal("partner-matcher-kind-missing", Code([Partner("pay", new PartnerMatcher("", "x"))], "$.partners[0].matchers[0].kind"));
        Assert.Equal("partner-matcher-value-missing", Code([Partner("pay", new PartnerMatcher(PartnerMatcherKind.DependencyNameContains, " "))], "$.partners[0].matchers[0].value"));
        Assert.Equal("partner-matcher-missing", Code([new PartnerDefinition("pay", "Pay", [null!])], "$.partners[0].matchers[0]"));
        Assert.Equal("partner-missing", Code([null!], "$.partners[0]"));

        // A regex compiles (bounded timeout), does not claim every call, and is something KQL (RE2) can run.
        Assert.Equal("partner-regex-invalid", Code([Partner("pay", new PartnerMatcher(PartnerMatcherKind.Regex, "gateway-("))], "$.partners[0].matchers[0].value"));
        Assert.Equal("partner-regex-matches-empty", Code([Partner("pay", new PartnerMatcher(PartnerMatcherKind.Regex, ".*"))], "$.partners[0].matchers[0].value"));
        Assert.Equal("partner-regex-unsupported", Code([Partner("pay", new PartnerMatcher(PartnerMatcherKind.Regex, "gateway(?!-test)"))], "$.partners[0].matchers[0].value"));

        // A blank display name is a warning, never an error.
        var noName = PartnerFindings(WithPartners([new PartnerDefinition("pay", " ", [ok])]));
        var warning = Assert.Single(noName);
        Assert.Equal(CompanyProfileSeverity.Warning, warning.Severity);
        Assert.Equal("partner-display-name-missing", warning.Code);
    }

    // ------------------------------------------------------------------ dependency source

    private static CompanyProfileFinding OnlyError(IReadOnlyList<PartnerDefinition> partners) =>
        Assert.Single(PartnerFindings(WithPartners(partners)), f => f.IsError);

    /// <summary>Every construct .NET compiles and App Insights (RE2) refuses: one bad regex fails every partner's series.</summary>
    [Theory]
    [InlineData(@"(?=gateway)\w+", "lookahead")]
    [InlineData(@"gateway(?!-test)", "negative lookahead")]
    [InlineData(@"(?<=api\.)ean", "lookbehind")]
    [InlineData(@"(?<!x)ean", "negative lookbehind")]
    [InlineData(@"(a)\1", "backreference")]
    [InlineData(@"(?<p>a)\k<p>", "named backreference")]
    [InlineData(@"(?>a)b", "atomic group")]
    [InlineData(@"(?(a)b|c)", "conditional")]
    [InlineData(@"(?#c)a", "inline comment")]
    [InlineData(@"(?n)a(b)", "inline flag n")]
    [InlineData(@"(?in:a)b", "inline flag n")]
    [InlineData(@"zentrumhub\.com\Z", "\\Z")]
    [InlineData(@"\Ga", "\\G")]
    public void Profile_PartnerRegexRejectsRe2UnsupportedConstruct(string pattern, string named)
    {
        var finding = OnlyError([Partner("pay", new PartnerMatcher(PartnerMatcherKind.Regex, pattern))]);

        Assert.Equal("partner-regex-unsupported", finding.Code);
        Assert.Equal("$.partners[0].matchers[0].value", finding.Path);
        Assert.Contains(named, finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"zentrumhub\.com$")]
    [InlineData(@"a\\Z")]
    [InlineData(@"a\\1")]
    [InlineData(@"a\\G")]
    [InlineData(@"(?i)api\.ean\.com")]
    [InlineData(@"(?s:a.b)")]
    [InlineData(@"(?x)a b")]
    [InlineData(@"gateway-\d+\z")]
    public void Profile_PartnerRegexAcceptsRe2SafePatterns(string pattern)
    {
        Assert.Empty(PartnerFindings(WithPartners([Partner("pay", new PartnerMatcher(PartnerMatcherKind.Regex, pattern))])));
        Assert.Null(CompanyProfileValidator.FindUnsupportedRe2Construct(pattern));
    }

    [Fact]
    public void Profile_PartnerKeyAndValueRejectTemplateTokens()
    {
        var ok = new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "x.example.test");

        var key = OnlyError([Partner("pay{{from}}", ok)]);
        Assert.Equal("partner-key-template-token", key.Code);
        Assert.Equal("$.partners[0].key", key.Path);
        Assert.Equal("partner-key-template-token", OnlyError([Partner("pay}}", ok)]).Code);

        foreach (var kind in PartnerMatcherKind.All)
        {
            foreach (var value in new[] { "{{from}}", "api{{x", "api}}x" })
            {
                var finding = OnlyError([Partner("pay", new PartnerMatcher(kind, value))]);
                Assert.Equal("partner-matcher-value-template-token", finding.Code);
                Assert.Equal("$.partners[0].matchers[0].value", finding.Path);
            }
        }

        // A single brace is fine (a regex quantifier, for instance).
        Assert.Empty(PartnerFindings(WithPartners([Partner("pay", new PartnerMatcher(PartnerMatcherKind.Regex, @"gateway-\d{2}\.example"))])));
    }

    private static KpiDefinition DependencyDefinition(KpiGrain grain = KpiGrain.Entity, string stage = "", int id = 31) =>
        KpiSourceDoubles.Definition(sourceKey: AppInsightsKpiSource.DependencySourceKey, domain: "hotels", stage: stage, grain: grain, id: id) with
        {
            Kpi = "PartnerFailures",
            Aggregation = KpiAggregation.Value,
        };

    [Fact]
    public async Task DependencyKpi_OneSeriesPerPartner()
    {
        var b1 = KpiSourceDoubles.From;
        var b2 = KpiSourceDoubles.From.AddHours(1);
        static string Row(DateTimeOffset bucket, string partner, long total, long failed, double p50, double p95, double mean) =>
            $$"""{"kind":"bucket","BucketStart":"{{bucket.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}","EntityKey":"{{partner}}","TotalCount":{{total}},"Value":{{100.0 * failed / total}},"P50":{{p50}},"P95":{{p95}},"Mean":{{mean}},"FailedCount":{{failed}},"Timestamp":null,"OperationId":""}""";
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(
        [
            KpiSourceDoubles.Row(Row(b1, "watchlist", 200, 10, 80, 300, 110)),
            KpiSourceDoubles.Row(Row(b1, "pay", 50, 0, 40, 90, 45)),
            KpiSourceDoubles.Row(Row(b2, "watchlist", 180, 0, 70, 250, 95)),
            KpiSourceDoubles.Row(Row(b2, "pay", 60, 30, 400, 2000, 700)),
        ]);
        var profile = WithPartners(TwoPartners());
        var source = KpiSourceDoubles.Source(telemetry, profile);
        var definition = DependencyDefinition();

        Assert.True(source.CanHandle("APPINSIGHTS:DEPENDENCY"));
        Assert.Equal(PartnerKpi.SourceKey, AppInsightsKpiSource.DependencySourceKey);

        var result = await source.FetchAsync(definition, KpiSourceDoubles.From, KpiSourceDoubles.To, 0);

        // The query: the service's dependencies, classified by the profile's partners in declared order.
        var request = Assert.Single(telemetry.Requests);
        Assert.Equal("hotel-app", request.AppInsightsAppId);
        var kql = KpiSourceDoubles.Code(request.Kql);
        Assert.DoesNotContain("{{", kql, StringComparison.Ordinal);
        Assert.Contains("app(\"/res/web-front\").dependencies", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("/res/sea-core", kql, StringComparison.Ordinal);
        Assert.DoesNotContain(".requests", kql, StringComparison.Ordinal);
        Assert.Contains("| extend Partner=case(", kql, StringComparison.Ordinal);
        var watchlistArm = kql.IndexOf("target contains \"lists.example.test\",\"watchlist\"", StringComparison.Ordinal);
        var payArm = kql.IndexOf(
            "(name contains \"POST /charge\" or (target matches regex \"(?i)gateway-\\\\d+\\\\.example\\\\.test\" or name matches regex \"(?i)gateway-\\\\d+\\\\.example\\\\.test\")),\"pay\"",
            StringComparison.Ordinal);
        Assert.True(watchlistArm > 0, kql);
        Assert.True(payArm > watchlistArm, kql);
        Assert.Contains("| where isnotempty(Partner)", kql, StringComparison.Ordinal);
        Assert.Contains("FailedCount = sumif(itemCount, success == false)", kql, StringComparison.Ordinal);
        Assert.Contains("TotalCount = sum(itemCount)", kql, StringComparison.Ordinal);
        Assert.Contains("EntityKey = Partner", kql, StringComparison.Ordinal);
        Assert.Contains("let maxEntities = 500;", kql, StringComparison.Ordinal);

        // One series per partner, entity grain statistics, no samples.
        Assert.Empty(result.Samples);
        Assert.Equal(["pay", "watchlist"], result.Buckets.Select(b => b.EntityKey).Distinct().Order(StringComparer.Ordinal));
        var pay2 = Assert.Single(result.Buckets, b => b.EntityKey == "pay" && b.BucketStart == b2);
        Assert.Equal(60, pay2.TotalCount);
        Assert.Equal(30, pay2.FailedCount);
        Assert.Equal(50.0, pay2.Value);
        Assert.Equal(400, pay2.P50);
        Assert.Equal(2000, pay2.P95);
        Assert.Equal(700, pay2.Mean);

        var batch = KpiEntityBucketAssembler.Assemble(definition, result, KpiSourceDoubles.From, KpiSourceDoubles.To);
        Assert.Equal(2, batch.Entities);
        Assert.Equal(4, batch.Buckets.Count);
        Assert.All(batch.Buckets, b => Assert.Equal(0, b.SampledCount));

        // Domain grain or a stage: unavailable, with the reason; nothing is queried.
        var domainGrain = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(DependencyDefinition(KpiGrain.Domain), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("appinsights:dependency is entity grain only", domainGrain.Message, StringComparison.Ordinal);
        var staged = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(DependencyDefinition(stage: "02-search"), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("leave Stage empty", staged.Message, StringComparison.Ordinal);
        Assert.Single(telemetry.Requests);

        // A profile without partners cannot render the query: unavailable, not an empty (healthy-looking) KPI.
        var bare = new KpiSourceDoubles.CapturingTelemetrySource();
        var noPartners = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            KpiSourceDoubles.Source(bare, WithPartners(null)).FetchAsync(definition, KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("declares no partner", noPartners.Message, StringComparison.Ordinal);
        Assert.Empty(bare.Requests);

        // The existing templates are untouched by the partner placeholders.
        var latency = AppInsightsKpiSource.LoadTemplate(AppInsightsKpiSource.LatencySourceKey, "hotels");
        Assert.DoesNotContain(CompanyKqlTemplater.PartnerClassifier, latency, StringComparison.Ordinal);
        Assert.EndsWith(".kpi-dependency.kql.template.txt", AppInsightsKpiSource.ResourceName(AppInsightsKpiSource.DependencySourceKey, "hotels"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PartnerMatcherKind.DependencyTargetContains, "api.x.test", "target contains \"api.x.test\"")]
    [InlineData(PartnerMatcherKind.DependencyNameContains, "GET /v1", "name contains \"GET /v1\"")]
    [InlineData(PartnerMatcherKind.DependencyTypeEquals, " Http ", "type =~ \"Http\"")]
    [InlineData(PartnerMatcherKind.Regex, "a\"b", "(target matches regex \"(?i)a\\\"b\" or name matches regex \"(?i)a\\\"b\")")]
    public void DependencyKpi_PartnerMatcherRendersEscapedPredicate(string kind, string value, string expected)
    {
        Assert.Equal(expected, CompanyKqlTemplater.RenderPartnerMatcher(new PartnerMatcher(kind, value)));
    }

    // ------------------------------------------------------------------ Triage

    private readonly A1InMemoryDefinitionStore _definitions = new();
    private readonly EntityBucketStore _buckets = new();
    private readonly EntityStateStore _states = new();
    private readonly EntityAnomalyStore _anomalies = new();
    private readonly A1InMemoryWatermarkStore _watermarks = new();
    private readonly A1RecordingTriageSubmitter _triage = new();
    private readonly HealthMonitoringOptions _options = new() { Enabled = true, Pillars = ["Application"] };

    private EntityDetectionMonitor Monitor() => new(
        _definitions, _buckets, _states, _anomalies, _watermarks, new KpiEvaluator(),
        Microsoft.Extensions.Options.Options.Create(_options), new A1FakeTimeProvider(Now),
        NullLogger<EntityDetectionMonitor>.Instance, null, _triage);

    private static KpiDefinition Entity(int id, string sourceKey, KpiAggregation aggregation, string threshold, string kpi) =>
        KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, threshold, aggregation: aggregation, kpi: kpi) with
        {
            KpiDefinitionId = id,
            Domain = "hotels",
            SourceKey = sourceKey,
            Grain = KpiGrain.Entity,
            ProfileKey = "acme",
        };

    private void Series(int id, string entityKey, double value, double p95)
    {
        _buckets.Buckets.Add(new KpiBucket(id, Day.AddHours(16), 100, 0, null, p95, null, null, value) { EntityKey = entityKey });
        _states.States[(id, entityKey)] = new KpiDetectionState(id, 1, Day.AddHours(15), null, Now.AddHours(-1)) { EntityKey = entityKey };
    }

    [Fact]
    public async Task PartnerAnomaly_TriageStageIsPartnerKey()
    {
        var partner = Entity(41, "AppInsights:Dependency", KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures");
        var latency = Entity(42, AppInsightsKpiSource.LatencySourceKey, KpiAggregation.P95, "{\"threshold\":3000}", "Latency");
        _definitions.Definitions.AddRange([partner, latency]);
        _watermarks.Watermarks[41] = Now;
        _watermarks.Watermarks[42] = Now;
        Series(41, "pay", value: 50, p95: 100);
        Series(41, "watchlist", value: 1, p95: 100);
        Series(42, "club-a", value: 0, p95: 3500);
        var investigation = Guid.NewGuid();
        _triage.NextInvestigationId = investigation;

        var result = await Monitor().RunAsync();

        Assert.Equal(2, result.Triggered);
        Assert.Equal(2, _anomalies.Anomalies.Count);

        // Only the partner anomaly goes to Triage: pillar Application, stage partner:{key}, the anomaly linked.
        var signal = Assert.Single(_triage.Signals);
        var partnerAnomaly = Assert.Single(_anomalies.Anomalies, a => a.KpiDefinitionId == 41);
        Assert.Equal("pay", partnerAnomaly.EntityKey);
        Assert.Equal("partner:pay", signal.Stage);
        Assert.Equal(PartnerKpi.TriageStage("pay"), signal.Stage);
        Assert.Equal(KpiPillarNames.ToDisplay(KpiPillar.Application), signal.Pillar);
        Assert.Equal("PartnerFailures", signal.Kpi);
        Assert.Equal("hotels", signal.Domain);
        Assert.Equal("acme", signal.ProfileKey);
        Assert.Equal(TriageSignalSources.Daemon, signal.Source);
        Assert.Equal(partnerAnomaly.AnomalyId, signal.AnomalyId);
        Assert.Equal(partnerAnomaly.Detection, signal.Detection);
        Assert.Equal(Now, signal.DetectedAt);
        Assert.Equal(investigation, partnerAnomaly.InvestigationId);

        // The no-Triage guarantee holds for every other entity KPI, in the same run.
        var other = Assert.Single(_anomalies.Anomalies, a => a.KpiDefinitionId == 42);
        Assert.Equal("club-a", other.EntityKey);
        Assert.Null(other.InvestigationId);
        Assert.DoesNotContain(_triage.Signals, s => s.Kpi == "Latency");
    }

    [Fact]
    public async Task PartnerAnomaly_SamePartnerTwoDefinitions_SubmitsOnce()
    {
        // Two dependency KPIs of one profile reading the same source (e.g. two services sharing it), a third of
        // another profile: one incident per (profile, partner, bucket).
        _definitions.Definitions.AddRange(
        [
            Entity(41, PartnerKpi.SourceKey, KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures"),
            Entity(43, PartnerKpi.SourceKey, KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures") with { Domain = "cars" },
            Entity(44, PartnerKpi.SourceKey, KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures") with { ProfileKey = "other" },
        ]);
        foreach (var id in new[] { 41, 43, 44 })
        {
            _watermarks.Watermarks[id] = Now;
            Series(id, "pay", value: 50, p95: 100);
        }
        Series(43, "watchlist", value: 50, p95: 100);
        _triage.OnSubmit = () => _triage.NextInvestigationId = Guid.NewGuid();

        var result = await Monitor().RunAsync();

        Assert.Equal(4, result.Triggered);
        Assert.Equal(4, _anomalies.Anomalies.Count);
        Assert.Equal(
            [("acme", "partner:pay"), ("acme", "partner:watchlist"), ("other", "partner:pay")],
            _triage.Signals.Select(s => (s.ProfileKey!, s.Stage!)).Order().ToList());
        var acmePay = _anomalies.Anomalies.Where(a => a.KpiDefinitionId is 41 or 43 && a.EntityKey == "pay").ToList();
        Assert.Equal(2, acmePay.Count);
        Assert.All(acmePay, a => Assert.NotNull(a.InvestigationId));
        Assert.Single(acmePay.Select(a => a.InvestigationId).Distinct());

        // A later run: a sibling definition that triggers on the same partner and bucket joins the open investigation.
        _triage.Signals.Clear();
        _definitions.Definitions.Add(Entity(45, PartnerKpi.SourceKey, KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures") with { Domain = "air" });
        _watermarks.Watermarks[45] = Now;
        Series(45, "pay", value: 50, p95: 100);

        await Monitor().RunAsync();

        Assert.Empty(_triage.Signals);
        var late = Assert.Single(_anomalies.Anomalies, a => a.KpiDefinitionId == 45);
        Assert.Equal(acmePay[0].InvestigationId, late.InvestigationId);
    }

    [Fact]
    public async Task PartnerAnomaly_TriageFailureKeepsAnomalyAndState()
    {
        _definitions.Definitions.Add(Entity(41, PartnerKpi.SourceKey, KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures"));
        _watermarks.Watermarks[41] = Now;
        Series(41, "pay", value: 50, p95: 100);
        _triage.Throw = true;

        var result = await Monitor().RunAsync();

        Assert.Equal(1, result.Triggered);
        var anomaly = Assert.Single(_anomalies.Anomalies);
        Assert.Null(anomaly.InvestigationId);
        Assert.Equal(Day.AddHours(16), _states.States[(41, "pay")].LastBucketEvaluated);
    }

    [Fact]
    public async Task PartnerAnomaly_OnlyEntityGrainDependencyDefinitionsArePartners()
    {
        Assert.False(PartnerKpi.IsPartnerDefinition(DependencyDefinition(KpiGrain.Domain)));
        Assert.True(PartnerKpi.IsPartnerDefinition(DependencyDefinition()));
        Assert.False(PartnerKpi.IsPartnerDefinition(Entity(1, AppInsightsKpiSource.ExceptionsSourceKey, KpiAggregation.Value, "{}", "Exceptions")));

        // Without a registered submitter the partner anomaly is still recorded (no crash, no Triage).
        _definitions.Definitions.Add(Entity(41, PartnerKpi.SourceKey, KpiAggregation.Value, "{\"threshold\":5}", "PartnerFailures"));
        _watermarks.Watermarks[41] = Now;
        Series(41, "pay", value: 50, p95: 100);
        var monitor = new EntityDetectionMonitor(
            _definitions, _buckets, _states, _anomalies, _watermarks, new KpiEvaluator(),
            Microsoft.Extensions.Options.Options.Create(_options), new A1FakeTimeProvider(Now), NullLogger<EntityDetectionMonitor>.Instance);

        var result = await monitor.RunAsync();

        Assert.Equal(1, result.Triggered);
        Assert.Single(_anomalies.Anomalies);
        Assert.Empty(_triage.Signals);
    }
}
