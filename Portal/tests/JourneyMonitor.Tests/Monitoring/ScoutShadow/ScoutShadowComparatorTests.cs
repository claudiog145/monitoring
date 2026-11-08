using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.ScoutShadow;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Functions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring.ScoutShadow;

/// <summary>
/// Contract v4 §4 / §6 A1: the Scout cycle compares its fixed board rules with the entity anomalies of the window.
/// The profile <see cref="ScoutCompanyProfiles.TwoServicesOnOneHost"/> names the service "retail" and step "checkout";
/// the company board's own Entity column says "T-7" and the entity KPI's EntityKey is "T-7" too.
/// </summary>
public class ScoutShadowComparatorTests
{
    private static readonly DateTimeOffset Hour = FloorToHour(DateTimeOffset.UtcNow.AddHours(-2));
    private static readonly DateTimeOffset Interval = Hour.AddMinutes(10);

    [Fact]
    public async Task Shadow_BothFlagged_IsBoth()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "checkout");
        var anomaly = await h.AddAnomalyAsync(definition, "T-7", Hour);

        await h.RunAsync([Row("case-a", rank: 3)]);

        var rows = await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null);
        // The rules interval and the anomaly's own bucket are both keys the window touched: both read "both".
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ScoutShadowAgreement.Both, r.Agreement));
        var row = Assert.Single(rows, r => r.BucketStart == Interval);
        Assert.Equal(ScoutShadowMethodVerdict.Anomalous, row.MethodVerdict);
        Assert.Equal(anomaly.AnomalyId, row.KpiAnomalyId);
        Assert.Equal(3, row.RuleRank);
        Assert.Equal("t-7", row.EntityKey);
        Assert.Equal("checkout", row.Stage);
        Assert.Equal("two-services", row.ProfileKey);
        var anomalyRow = Assert.Single(rows, r => r.BucketStart == Hour);
        Assert.Equal((anomaly.AnomalyId, (int?)3, "checkout"), (anomalyRow.KpiAnomalyId!.Value, anomalyRow.RuleRank, anomalyRow.Stage));
    }

    [Fact]
    public async Task Shadow_RulesOnly_IsRecorded()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "checkout");
        // The method evaluated the covering bucket of this entity and did not trigger: "normal".
        await h.Buckets.UpsertAsync([new KpiBucket(definition.KpiDefinitionId, Hour, 100, 0, 1, 2, 1, null, null) { EntityKey = "T-7" }]);
        await h.States.SaveAsync(KpiDetectionState.Initial(definition.KpiDefinitionId, "T-7") with { LastBucketEvaluated = Hour });

        await h.RunAsync([Row("case-a", rank: 3), Row("case-b", rank: 3, tenant: "T-9")]);

        var rows = await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ScoutShadowAgreement.RulesOnly, r.Agreement));
        Assert.All(rows, r => Assert.Null(r.KpiAnomalyId));
        Assert.Equal(ScoutShadowMethodVerdict.Normal, rows.Single(r => r.EntityKey == "t-7").MethodVerdict);
        Assert.Equal(ScoutShadowMethodVerdict.NotEvaluated, rows.Single(r => r.EntityKey == "t-9").MethodVerdict);
        Assert.Equal(new ScoutShadowCounts(0, 2, 0), await h.Shadow.GetCountsAsync(ScoutBoard.Latency, 24, null));
    }

    [Fact]
    public async Task Shadow_MethodOnly_IsRecorded()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "");
        var anomaly = await h.AddAnomalyAsync(definition, "T-8", Hour);

        // The board saw the entity, but below the escalation rank: the rules did not flag it.
        await h.RunAsync([Row("case-a", rank: 2, tenant: "T-8")]);

        var row = Assert.Single(await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null));
        Assert.Equal(ScoutShadowAgreement.MethodOnly, row.Agreement);
        Assert.Equal(ScoutShadowMethodVerdict.Anomalous, row.MethodVerdict);
        Assert.Null(row.RuleRank);
        Assert.Equal(anomaly.AnomalyId, row.KpiAnomalyId);
        Assert.Equal("t-8", row.EntityKey);
        Assert.Equal("", row.Stage);
        Assert.Equal(Hour, row.BucketStart);
    }

    [Fact]
    public async Task Shadow_NeitherFlagged_IsNotStored()
    {
        var h = new Harness();
        await h.AddDefinitionAsync(stage: "checkout");

        var run = await h.RunAsync([Row("case-a", rank: 2), Row("case-b", rank: 1, tenant: "T-9")]);

        Assert.Equal(ScoutRunStatus.Clear, run.Status);
        Assert.Empty(await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null));

        var input = new ScoutShadowInput(Guid.NewGuid(), ScoutBoard.Latency, "p", 3, Hour, Hour.AddHours(4),
            [new ScoutShadowRuleCase("t-7", "retail", "checkout", Interval, 2)],
            [new ScoutShadowMethodDefinition(1, "retail", "checkout", TimeSpan.FromHours(1))], [], []);
        Assert.Empty(ScoutShadowComparator.Compare(input));
    }

    [Fact]
    public async Task Shadow_RulesMode_EscalationUnchanged()
    {
        var rows = new[] { Row("case-a", rank: 3), Row("case-b", rank: 2, tenant: "T-8"), Row("case-c", rank: 3, tenant: "T-9") };

        var without = new Harness(withShadow: false);
        var withShadow = new Harness(o => o.DetectionMode = ScoutDetectionMode.Rules);
        var definition = await withShadow.AddDefinitionAsync(stage: "checkout");
        await withShadow.AddAnomalyAsync(definition, "T-7", Hour);

        var plain = await without.RunAsync(rows);
        var shadowed = await withShadow.RunAsync(rows);

        Assert.Equal(ScoutDetectionMode.Rules, new ScoutOptions().ResolvedDetectionMode);
        Assert.NotEmpty(await withShadow.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null));
        Assert.Equal(
            (plain.Status, plain.CandidatesFound, plain.PackagesCreated, plain.PackagesSkippedDuplicate, plain.PackagesSkippedCap),
            (shadowed.Status, shadowed.CandidatesFound, shadowed.PackagesCreated, shadowed.PackagesSkippedDuplicate, shadowed.PackagesSkippedCap));
        Assert.Equal(await without.EscalationAsync(), await withShadow.EscalationAsync());
    }

    [Fact]
    public async Task Shadow_ApiReturnsCounts()
    {
        var store = new InMemoryScoutShadowStore();
        var runId = Guid.NewGuid();
        await store.AddRangeAsync(
        [
            new(runId, ScoutBoard.Latency, KpiProfiles.Default, "t-1", "checkout", Interval, 3, ScoutShadowMethodVerdict.Anomalous, 11, ScoutShadowAgreement.Both),
            new(runId, ScoutBoard.Latency, KpiProfiles.Default, "t-2", "checkout", Interval, 3, ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly),
            new(runId, ScoutBoard.Latency, KpiProfiles.Default, "t-3", "", Hour, null, ScoutShadowMethodVerdict.Anomalous, 12, ScoutShadowAgreement.MethodOnly),
            new(runId, ScoutBoard.Latency, KpiProfiles.Default, "t-4", "", Hour, null, ScoutShadowMethodVerdict.Anomalous, 13, ScoutShadowAgreement.MethodOnly),
            new(runId, ScoutBoard.Exception, KpiProfiles.Default, "t-5", "", Hour, null, ScoutShadowMethodVerdict.Anomalous, 14, ScoutShadowAgreement.MethodOnly),
            new(runId, ScoutBoard.Latency, "other", "t-6", "", Hour, 3, ScoutShadowMethodVerdict.Normal, null, ScoutShadowAgreement.RulesOnly),
        ]);
        var functions = new ScoutShadowFunctions(store, NullLogger<ScoutShadowFunctions>.Instance);

        var response = await functions.GetShadowAsync(new FakeHttpRequestData("http://localhost/api/scouts/latency/shadow?hours=24&top=2"), "latency", CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var json = response.ReadJson();
        Assert.Equal("latency", json["board"]!.GetValue<string>());
        Assert.Equal(KpiProfiles.Default, json["profileKey"]!.GetValue<string>());
        Assert.Equal(24, json["hours"]!.GetValue<int>());
        Assert.Equal(1, json["counts"]!["both"]!.GetValue<int>());
        Assert.Equal(1, json["counts"]!["rulesOnly"]!.GetValue<int>());
        Assert.Equal(2, json["counts"]!["methodOnly"]!.GetValue<int>());
        Assert.Equal(4, json["counts"]!["total"]!.GetValue<int>());
        var rows = json["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(Interval, r!["bucketStart"]!.GetValue<DateTimeOffset>()));
        Assert.NotNull(rows[0]!["agreement"]);

        var all = (await functions.GetShadowAsync(new FakeHttpRequestData("http://localhost/api/scouts/exception/shadow?profileKey=*"), "exception", CancellationToken.None)).ReadJson();
        Assert.Equal(1, all["counts"]!["methodOnly"]!.GetValue<int>());
        Assert.Equal("*", all["profileKey"]!.GetValue<string>());

        var badBoard = await functions.GetShadowAsync(new FakeHttpRequestData("http://localhost/api/scouts/nope/shadow"), "nope", CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badBoard.StatusCode);
        var badHours = await functions.GetShadowAsync(new FakeHttpRequestData("http://localhost/api/scouts/latency/shadow?hours=0"), "latency", CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badHours.StatusCode);
    }

    [Fact]
    public async Task Shadow_EmptyStageDefinition_MatchesAnyStageOfItsService()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "");
        var anomaly = await h.AddAnomalyAsync(definition, "T-7", Hour);

        await h.RunAsync([Row("case-a", rank: 3)]);

        var rows = await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null);
        var row = Assert.Single(rows, r => r.Stage == "checkout");
        Assert.Equal(ScoutShadowAgreement.Both, row.Agreement);
        Assert.Equal(anomaly.AnomalyId, row.KpiAnomalyId);
        Assert.Equal(ScoutShadowAgreement.Both, Assert.Single(rows, r => r.Stage == "").Agreement);
        Assert.Equal(0, (await h.Shadow.GetCountsAsync(ScoutBoard.Latency, 24, null)).MethodOnly);
    }

    [Fact]
    public async Task Shadow_OtherStageOrServiceOrBoardKind_DoesNotMatch()
    {
        var h = new Harness();
        var otherStage = await h.AddDefinitionAsync(stage: "ticket");
        var otherService = await h.AddDefinitionAsync(stage: "", domain: "support");
        var exceptions = await h.AddDefinitionAsync(stage: "", sourceKey: ScoutShadowRecorder.ExceptionsSourceKey);
        await h.AddAnomalyAsync(otherStage, "T-7", Hour);
        await h.AddAnomalyAsync(otherService, "T-7", Hour);
        await h.AddAnomalyAsync(exceptions, "T-7", Hour);

        await h.RunAsync([Row("case-a", rank: 3)]);

        var rows = await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null);
        Assert.Equal(ScoutShadowAgreement.RulesOnly, rows.Single(r => r.Stage == "checkout").Agreement);
        // The latency board never reads exception KPIs; the other two are method-only rows for their own stage.
        Assert.Equal(2, rows.Count(r => r.Agreement == ScoutShadowAgreement.MethodOnly));
        // Counted per entity-hour incident: the all-stages "support" anomaly and the rules interval share
        // (t-7, "", hour) - both sides flagged that entity in that hour -> both; the "ticket" one stays method-only.
        Assert.Equal(new ScoutShadowCounts(1, 0, 1), await h.Shadow.GetCountsAsync(ScoutBoard.Latency, 24, null));
    }

    [Fact]
    public void Shadow_AnomalyBucketMustCoverTheRulesInterval()
    {
        var definition = new ScoutShadowMethodDefinition(1, "retail", "checkout", TimeSpan.FromHours(1));
        ScoutShadowInput Input(DateTimeOffset anomalyBucket) => new(Guid.NewGuid(), ScoutBoard.Latency, "p", 3, Hour.AddHours(-2), Hour.AddHours(2),
            [new ScoutShadowRuleCase("t-7", "retail", "checkout", Hour.AddMinutes(55), 3)],
            [definition], [new ScoutShadowMethodAnomaly(5, 1, "t-7", anomalyBucket)], []);

        var covered = ScoutShadowComparator.Compare(Input(Hour));
        var next = ScoutShadowComparator.Compare(Input(Hour.AddHours(1)));

        Assert.Equal(2, covered.Count);
        Assert.All(covered, r => Assert.Equal(ScoutShadowAgreement.Both, r.Agreement));
        Assert.Equal([ScoutShadowAgreement.MethodOnly, ScoutShadowAgreement.RulesOnly], next.Select(r => r.Agreement).Order());
    }

    [Fact]
    public async Task Shadow_LaterCycle_UpgradesMethodOnlyToBoth()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "checkout");
        var anomaly = await h.AddAnomalyAsync(definition, "T-7", Hour);

        // Cycle 1: the board saw the club below the escalation rank -> method-only at the anomaly's bucket.
        await h.RunAsync([Row("case-a", rank: 2)]);
        var first = Assert.Single(await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null));
        Assert.Equal((ScoutShadowAgreement.MethodOnly, Hour), (first.Agreement, first.BucketStart));

        // Cycle 2: the board now escalates the same club/stage inside that hour.
        await h.RunAsync([Row("case-a", rank: 3)]);

        var rows = await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null);
        Assert.All(rows, r => Assert.Equal(ScoutShadowAgreement.Both, r.Agreement));
        Assert.Equal(anomaly.AnomalyId, Assert.Single(rows, r => r.BucketStart == Hour).KpiAnomalyId);
        Assert.Equal(new ScoutShadowCounts(1, 0, 0), await h.Shadow.GetCountsAsync(ScoutBoard.Latency, 24, null));
    }

    [Fact]
    public async Task Shadow_LaterCycle_UpgradesRulesOnlyToBoth()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "checkout");

        await h.RunAsync([Row("case-a", rank: 3)]);
        Assert.Equal(ScoutShadowAgreement.RulesOnly, Assert.Single(await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null)).Agreement);

        // The hour closes, the method triggers, and the next cycle sees the same interval again.
        await h.AddAnomalyAsync(definition, "T-7", Hour);
        await h.RunAsync([Row("case-a", rank: 3)]);

        Assert.Equal(new ScoutShadowCounts(1, 0, 0), await h.Shadow.GetCountsAsync(ScoutBoard.Latency, 24, null));
    }

    [Fact]
    public async Task Shadow_SkippedBucket_IsNotEvaluated()
    {
        var h = new Harness();
        var definition = await h.AddDefinitionAsync(stage: "checkout", minObservations: 30);
        // The evaluator moves LastBucketEvaluated past a bucket it skipped for too few observations.
        await h.Buckets.UpsertAsync([
            new KpiBucket(definition.KpiDefinitionId, Hour, 5, 0, 1, 2, 1, null, null) { EntityKey = "T-7" },
            new KpiBucket(definition.KpiDefinitionId, Hour, 50, 0, 1, 2, 1, null, null) { EntityKey = "T-9" },
        ]);
        await h.States.SaveAsync(KpiDetectionState.Initial(definition.KpiDefinitionId, "T-7") with { LastBucketEvaluated = Hour });
        await h.States.SaveAsync(KpiDetectionState.Initial(definition.KpiDefinitionId, "T-9") with { LastBucketEvaluated = Hour });

        await h.RunAsync([Row("case-a", rank: 3), Row("case-b", rank: 3, tenant: "T-9")]);

        var rows = await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null);
        Assert.Equal(ScoutShadowMethodVerdict.NotEvaluated, rows.Single(r => r.EntityKey == "t-7").MethodVerdict);
        Assert.Equal(ScoutShadowMethodVerdict.Normal, rows.Single(r => r.EntityKey == "t-9").MethodVerdict);
        Assert.Equal(1000, ScoutShadowRecorder.MaxSeriesPerDefinition);
    }

    /// <summary>
    /// Live shape (monitoring.InvestigationPackage, profile arrivia): CaseId "Cruise Core | Cruise | club:140 | 02 Cruise Listing | ..."
    /// with ScoutBoardRow.EntityKey "[host/club unavailable]" - the C# reclassification cannot see the club. The board's
    /// own Entity is what the entity KPI KQL emits for the same request.
    /// </summary>
    [Fact]
    public void Shadow_LegacyBoardClubRow_UsesTheBoardIdentityLikeTheEntityKql()
    {
        var adapter = new ProfileDrivenAdapter(Incumbent.Value);
        var row = LegacyRow(adapter, "Cruise Core | Cruise | club:140 | 02 Cruise Listing | 2026-11-05T10:00:00Z", "club:140");

        Assert.Equal(ScoutRowClassification.UnresolvedEntityKey, row.EntityKey);
        var kpiKey = KqlEntityKey(Incumbent.Value, new Dictionary<string, string> { ["VacationClubId"] = "140" }, "https://cruise.club-140.example.test/cruise/search");
        Assert.Equal("140", kpiKey);
        Assert.Equal(ScoutShadowEntityKey.FromKpi(kpiKey), ScoutShadowEntityKey.FromBoardRow(row));

        var other = LegacyRow(adapter, "Cruise Core | Cruise | club:113 | 02 Cruise Listing | 2026-11-05T10:00:00Z", "club:113");
        Assert.Equal("113", ScoutShadowEntityKey.FromBoardRow(other));
    }

    [Fact]
    public void Shadow_LegacyBoardHostRow_MatchesTheLowerCasedKqlHost()
    {
        var adapter = new ProfileDrivenAdapter(Incumbent.Value);
        var row = LegacyRow(adapter, "SOR | Hotels | host:Book.Club-7.Example.test | 03 Hotel Results | x", "host:Book.Club-7.Example.test", "03 Hotel Results", "/hotels/results");

        var kpiKey = KqlEntityKey(Incumbent.Value, new Dictionary<string, string>(), "https://Book.Club-7.Example.test/hotels/results");
        Assert.Equal("book.club-7.example.test", kpiKey);
        Assert.Equal(ScoutShadowEntityKey.FromKpi(kpiKey), ScoutShadowEntityKey.FromBoardRow(row));
    }

    [Fact]
    public void Shadow_CompanyBoardRow_UsesTheBareEntityColumn()
    {
        var row = Row("case-a", rank: 3, tenant: "T-7");

        // The C# classification found no TenantId dimension on the aggregate row; the board's Entity column did.
        Assert.NotEqual("tenant:T-7", row.EntityKey);
        Assert.Equal("t-7", ScoutShadowEntityKey.FromBoardRow(row));
        Assert.Equal("t-7", ScoutShadowEntityKey.FromKpi("T-7"));
        Assert.Null(ScoutShadowEntityKey.FromKpi(ScoutShadowEntityKey.KpiUnknownEntity));
        Assert.Null(ScoutShadowEntityKey.FromKpi(""));
    }

    [Fact]
    public void Shadow_UnidentifiedRows_AreSkippedNotMerged()
    {
        var adapter = new ProfileDrivenAdapter(Incumbent.Value);
        var legacyA = LegacyRow(adapter, "Cruise Core | Cruise | [host/club unavailable] | 02 Cruise Listing | a", ScoutRowClassification.UnresolvedEntityKey);
        var legacyB = LegacyRow(adapter, "Cruise API | Cruise | [host/club unavailable] | 02 Cruise Listing | b", ScoutRowClassification.UnresolvedEntityKey);
        var unknown = Row("case-u", rank: 3, tenant: ScoutShadowEntityKey.KpiUnknownEntity);

        Assert.Null(ScoutShadowRecorder.ToRuleCase(adapter, legacyA));
        Assert.Null(ScoutShadowRecorder.ToRuleCase(adapter, legacyB));
        Assert.Null(ScoutShadowRecorder.ToRuleCase(TwoServices, unknown));

        // Without an identity but with a host, the host stands in - exactly the entity KQL's own fallback.
        var withHost = unknown with { RawRowJson = """{"Entity":"(entity unavailable)","Host":"Shop.Example.com"}""" };
        Assert.Equal("shop.example.com", ScoutShadowRecorder.ToRuleCase(TwoServices, withHost)!.EntityKey);
    }

    [Fact]
    public async Task Shadow_UnidentifiedRows_WriteNoSentinelRow()
    {
        var h = new Harness();
        await h.AddDefinitionAsync(stage: "checkout");

        await h.RunAsync([Row("case-a", rank: 3, tenant: ScoutShadowEntityKey.KpiUnknownEntity), Row("case-b", rank: 3, tenant: "T-9")]);

        var row = Assert.Single(await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null));
        Assert.Equal("t-9", row.EntityKey);
    }

    [Fact]
    public async Task Shadow_TrailingEdge_DoesNotDowngradeBoth()
    {
        var store = new InMemoryScoutShadowStore();
        var hour = FloorToHour(DateTimeOffset.UtcNow.AddHours(-5));
        var definition = new ScoutShadowMethodDefinition(1, "cruises", "", TimeSpan.FromHours(1));
        var anomaly = new ScoutShadowMethodAnomaly(5, 1, "140", hour);
        ScoutShadowInput Cycle(DateTimeOffset from, IReadOnlyList<ScoutShadowRuleCase> rules) =>
            new(Guid.NewGuid(), ScoutBoard.Latency, "arrivia", 3, from, from.AddHours(4), rules, [definition], [anomaly], []);

        // Cycle A (window hour-1h .. hour+3h): the board flags 50' into the anomaly's hour -> both, twice.
        var a = ScoutShadowComparator.Compare(Cycle(hour.AddHours(-1), [new ScoutShadowRuleCase("140", "cruises", "02 cruise listing", hour.AddMinutes(50), 3)]));
        await store.AddRangeAsync(a);
        Assert.Equal(2, a.Count);
        Assert.All(a, r => Assert.Equal(ScoutShadowAgreement.Both, r.Agreement));

        // Cycle B (window hour+55' .. ): the rules interval left the window while the hour still overlaps it.
        var b = ScoutShadowComparator.Compare(Cycle(hour.AddMinutes(55), []));
        Assert.Empty(b);
        // Even if a stale writer sent the downgrade, the store keeps 'both'.
        await store.AddRangeAsync([a.Single(r => r.BucketStart == hour) with { Agreement = ScoutShadowAgreement.MethodOnly, RuleRank = null }]);

        var rows = await store.GetRecentAsync(ScoutBoard.Latency, 24, null);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ScoutShadowAgreement.Both, r.Agreement));
        Assert.Equal(3, rows.Single(r => r.BucketStart == hour).RuleRank);
        Assert.Equal(new ScoutShadowCounts(1, 0, 0), await store.GetCountsAsync(ScoutBoard.Latency, 24, null));
    }

    [Fact]
    public async Task Shadow_FailureDoesNotFailTheCycle()
    {
        var activity = new RecordingActivity();
        var h = new Harness(shadowStore: new ThrowingShadowStore(), activity: activity);
        var definition = await h.AddDefinitionAsync(stage: "checkout");
        await h.AddAnomalyAsync(definition, "T-7", Hour);

        var run = await h.RunAsync([Row("case-a", rank: 3)]);

        Assert.Equal(ScoutRunStatus.Detected, run.Status);
        Assert.Equal(1, run.PackagesCreated);
        var warning = Assert.Single(activity.Entries, e => e.Level == DaemonActivityLevel.Warning);
        Assert.Equal(ScoutMonkeyService.ShadowActivityKind, warning.Kind);
        Assert.Contains("shadow comparison failed", warning.Message);
    }

    [Fact]
    public async Task Shadow_FailedCycle_WritesNothing()
    {
        var h = new Harness(readerFailure: new InvalidOperationException("board down"));
        var definition = await h.AddDefinitionAsync(stage: "checkout");
        await h.AddAnomalyAsync(definition, "T-7", Hour);

        var run = await h.RunAsync([]);

        Assert.Equal(ScoutRunStatus.Failed, run.Status);
        Assert.Empty(await h.Shadow.GetRecentAsync(ScoutBoard.Latency, 24, null));
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);

    private static readonly ICompanyAdapter TwoServices = new ProfileDrivenAdapter(ScoutCompanyProfiles.TwoServicesOnOneHost());

    /// <summary>A company-board row (company-scout-board.kql.template.txt projection), built the way ScoutBoardService.ToBoardRow builds it.</summary>
    private static ScoutBoardRow Row(string caseId, int rank, string tenant = "T-7") =>
        BoardRow(TwoServices, caseId, rank,
            payload: new Dictionary<string, object?> { ["ServiceKey"] = "retail", ["Service"] = "Retail", ["StepKey"] = "checkout", ["Step"] = "Checkout", ["Entity"] = tenant, ["Source"] = "SOR", ["SlowPaths"] = new[] { "/checkout" } },
            raw: new Dictionary<string, object?> { ["Service"] = "Retail", ["ServiceKey"] = "retail", ["Entity"] = tenant, ["Step"] = "Checkout", ["StepKey"] = "checkout", ["Requests"] = 10, ["Source"] = "SOR" });

    /// <summary>Mirrors ScoutBoardService.ToBoardRow: the board's Entity feeds the C# classification, whose EntityKey/labels land on the row.</summary>
    private static ScoutBoardRow BoardRow(ICompanyAdapter adapter, string caseId, int rank, Dictionary<string, object?> payload, Dictionary<string, object?> raw)
    {
        payload["CaseId"] = caseId;
        payload["GroupKey"] = caseId + " group";
        payload["Start"] = Interval;
        payload["End"] = Interval.AddMinutes(5);
        payload["Priority"] = " Investigate now";
        var payloadJson = JsonSerializer.Serialize(payload);
        raw["GroupPayload"] = payloadJson;
        raw["PriorityRank"] = rank;
        raw["Priority"] = " Investigate now";
        raw["Interval"] = Interval;
        var rawJson = JsonSerializer.Serialize(raw);

        var boardEntity = payload.TryGetValue("Entity", out var e) && e is string s && s.Length > 0
            ? s
            : raw.TryGetValue("ClubOrHost", out var c) ? c as string ?? "" : "";
        var classification = ScoutRowMapper.Classify(adapter, ScoutRowMapper.ToRowContext(rawJson, payloadJson, boardEntity));
        return new ScoutBoardRow(
            ScoutBoard.Latency, caseId, caseId + " group", classification.ServiceLabel, classification.EntityKey, classification.StepLabel,
            "SOR", "Investigate now", rank, "Severe latency", Interval, Interval.AddMinutes(5), payloadJson, rawJson);
    }

    /// <summary>A legacy incumbent board row (scout-*-priorities.kql.txt): Entity "club:{id}" | "host:{host}" | "[host/club unavailable]".</summary>
    private static ScoutBoardRow LegacyRow(ICompanyAdapter adapter, string caseId, string entity, string stage = "02 Cruise Listing", string path = "/cruise/search") =>
        BoardRow(adapter, caseId, 3,
            payload: new Dictionary<string, object?> { ["Vertical"] = "Cruise", ["Entity"] = entity, ["Stage"] = stage, ["Source"] = "Cruise Core", ["Paths"] = new[] { path }, ["SlowPaths"] = new[] { path } },
            raw: new Dictionary<string, object?> { ["Vertical"] = "Cruise", ["ClubOrHost"] = entity, ["JourneyStep"] = stage, ["Requests"] = 25, ["FailedRequests"] = 3, ["Source"] = "Cruise Core" });

    private static readonly Lazy<CompanyProfile> Incumbent = new(() =>
        JsonSerializer.Deserialize<CompanyProfile>(ArriviaProfileSeedTests.SeedJsonFor(KpiProfiles.Default), new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

    /// <summary>
    /// What kpi-*.kql.template.txt emits as EntityKey for one request: CompanyKqlTemplater's coalesce over the
    /// profile identity sources in order, then the lower-cased host, then "(entity unavailable)".
    /// </summary>
    private static string KqlEntityKey(CompanyProfile profile, IReadOnlyDictionary<string, string> customDimensions, string url)
    {
        var uri = new Uri(url);
        var host = uri.Host.ToLowerInvariant();
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var source in profile.EntityIdentitySources.OrderBy(s => s.Order))
        {
            var value = source.Kind switch
            {
                EntityIdentitySourceKind.CustomDimension => customDimensions.TryGetValue(source.Key, out var d) ? d : null,
                EntityIdentitySourceKind.QueryParameter => query[source.Key],
                EntityIdentitySourceKind.Host => host,
                _ => null,
            };
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return host.Length > 0 ? host : ScoutShadowEntityKey.KpiUnknownEntity;
    }

    private sealed class Harness
    {
        private readonly ScoutOptions _options = new() { UseLlmAssessment = false };
        private readonly Exception? _readerFailure;
        private readonly bool _withShadow;
        private readonly IScoutShadowStore _shadowStore;
        private readonly RecordingActivity? _activity;

        public Harness(Action<ScoutOptions>? configure = null, bool withShadow = true, IScoutShadowStore? shadowStore = null,
            RecordingActivity? activity = null, Exception? readerFailure = null)
        {
            configure?.Invoke(_options);
            _withShadow = withShadow;
            _shadowStore = shadowStore ?? Shadow;
            _activity = activity;
            _readerFailure = readerFailure;
            Anomalies = new InMemoryKpiAnomalyStore(Definitions);
        }

        public InMemoryKpiDefinitionStore Definitions { get; } = new();
        public InMemoryKpiAnomalyStore Anomalies { get; }
        public InMemoryKpiStateStore States { get; } = new();
        public InMemoryKpiBucketStore Buckets { get; } = new();
        public InMemoryScoutShadowStore Shadow { get; } = new();
        public InMemoryInvestigationPackageRepository Packages { get; } = new();
        public InMemoryAgentWorkQueue Queue { get; } = new();

        public Task<KpiDefinition> AddDefinitionAsync(string stage, string domain = "retail", string sourceKey = ScoutShadowRecorder.LatencySourceKey, int minObservations = 0) =>
            Definitions.UpsertAsync(new KpiDefinition(0, KpiPillar.Application, sourceKey == ScoutShadowRecorder.LatencySourceKey ? "Latency" : "Exceptions",
                domain, stage, sourceKey, KpiDirection.Increase, KpiAggregation.P95, TimeSpan.FromHours(1), TimeSpan.FromHours(1),
                TimeSpan.FromDays(1), 0, minObservations, DetectionMethodKind.RobustZScore, """{"windowBuckets":24,"zThreshold":3}""", 2, 120, true,
                DateTimeOffset.UtcNow, "test")
            {
                ProfileKey = "two-services",
                Grain = KpiGrain.Entity,
            }, "test");

        /// <summary>What EntityDetectionMonitor leaves behind on a trigger: the anomaly and a cooldown past the bucket end.</summary>
        public async Task<KpiAnomaly> AddAnomalyAsync(KpiDefinition definition, string entityKey, DateTimeOffset bucketStart)
        {
            var anomaly = await Anomalies.AddAsync(new KpiAnomaly(0, definition.KpiDefinitionId, DateTimeOffset.UtcNow, bucketStart, 900, 300, 6, "test", null)
            {
                EntityKey = entityKey,
            });
            await States.SaveAsync(KpiDetectionState.Initial(definition.KpiDefinitionId, entityKey) with
            {
                LastBucketEvaluated = bucketStart,
                CooldownUntil = bucketStart + definition.BucketSize + TimeSpan.FromMinutes(definition.CooldownMinutes),
            });
            return anomaly;
        }

        public Task<ScoutRun> RunAsync(IReadOnlyList<ScoutBoardRow> rows)
        {
            var service = new ScoutMonkeyService(
                new BoardReader(rows, _readerFailure),
                new FakeCompanyAdapterProvider(ScoutCompanyProfiles.TwoServicesOnOneHost()),
                new ClubResolver(),
                new NoAssessor(),
                new EvidenceCollector(),
                Packages,
                Queue,
                new InMemoryScoutRunRepository(),
                new InMemoryScoutLlmCallLogRepository(),
                new StubOptionsMonitor(_options),
                NullLogger<ScoutMonkeyService>.Instance,
                _withShadow ? _shadowStore : null,
                _withShadow ? Definitions : null,
                _withShadow ? Anomalies : null,
                _withShadow ? States : null,
                _withShadow ? Buckets : null,
                _activity);
            return service.RunAsync(ScoutBoard.Latency);
        }

        /// <summary>Everything escalation produces, minus the generated ids and timestamps.</summary>
        public async Task<string> EscalationAsync()
        {
            var packages = (await Packages.GetRecentAsync(50)).OrderBy(p => p.CaseId).Select(p => new
            {
                p.CaseId, p.Board, p.ProfileKey, p.Vertical, p.Stage, p.EntityKey, p.Priority, p.PriorityRank, p.Reason,
                p.DeterministicJson, p.AssessmentJson, p.RawEvidenceJson, p.DrillDownKql, p.RequestCount, p.Status,
            });
            var queued = (await Queue.GetQueuedAsync(AgentWorkPayloadType.InvestigationPackage, 50))
                .Select(i => new { i.SourceMonkey, i.DedupKey, i.Priority })
                .OrderBy(i => i.DedupKey);
            return JsonSerializer.Serialize(new { packages, queued });
        }
    }

    private sealed class StubOptionsMonitor(ScoutOptions options) : IOptionsMonitor<ScoutOptions>
    {
        public ScoutOptions CurrentValue => options;
        public ScoutOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<ScoutOptions, string?> listener) => null;
    }

    private sealed class BoardReader(IReadOnlyList<ScoutBoardRow> rows, Exception? failure) : IScoutBoardReader
    {
        public Task<ScoutBoardResult> ReadAsync(ScoutBoardRequest request, CancellationToken ct = default) =>
            failure is not null ? throw failure : Task.FromResult(new ScoutBoardResult(rows, "// kql"));
    }

    private sealed class ClubResolver : IClubIdentityResolver
    {
        public Task<ClubIdentity> ResolveAsync(string entityKey, CancellationToken ct = default) =>
            Task.FromResult(new ClubIdentity(entityKey, ClubEntityKind.Unknown, null, null, null, null, ClubResolution.Unresolved));
    }

    private sealed class NoAssessor : IScoutAssessor
    {
        public Task<ScoutAssessmentResult> AssessAsync(ScoutBoardRow row, ClubIdentity club, CancellationToken ct = default) =>
            throw new InvalidOperationException("UseLlmAssessment is false in these tests.");
    }

    private sealed class EvidenceCollector : IScoutEvidenceCollector
    {
        public Task<ScoutRawEvidence> CollectAsync(ScoutBoardRow row, CancellationToken ct = default) =>
            Task.FromResult(new ScoutRawEvidence("// drill-down", 0, false, "[]", null));
    }

    private sealed class ThrowingShadowStore : IScoutShadowStore
    {
        public Task AddRangeAsync(IReadOnlyList<ScoutShadowComparison> comparisons, CancellationToken ct = default) =>
            throw new InvalidOperationException("shadow table missing");
        public Task<IReadOnlyList<ScoutShadowComparison>> GetRecentAsync(string board, int hours, string? profileKey, CancellationToken ct = default) =>
            throw new InvalidOperationException("shadow table missing");
        public Task<ScoutShadowCounts> GetCountsAsync(string board, int hours, string? profileKey, CancellationToken ct = default) =>
            throw new InvalidOperationException("shadow table missing");
    }

    private sealed class RecordingActivity : IDaemonActivityRecorder
    {
        public List<DaemonActivityEntry> Entries { get; } = [];

        public IDaemonRun BeginRun(string daemonKey) => new Run(this, daemonKey);

        private sealed class Run(RecordingActivity owner, string daemonKey) : IDaemonRun
        {
            public Guid RunId { get; } = Guid.NewGuid();
            public string DaemonKey => daemonKey;
            public void Info(string kind, string message, object? data = null) => Add(DaemonActivityLevel.Info, kind, message);
            public void Warning(string kind, string message, object? data = null) => Add(DaemonActivityLevel.Warning, kind, message);
            public void Error(string kind, string message, object? data = null) => Add(DaemonActivityLevel.Error, kind, message);
            public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

            private void Add(DaemonActivityLevel level, string kind, string message) =>
                owner.Entries.Add(new DaemonActivityEntry(0, daemonKey, RunId, DateTimeOffset.UtcNow, level, kind, message, null));
        }
    }
}
