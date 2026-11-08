using System.Net;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Tests.Functions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.A4;

public sealed class Spec1000TriageApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private const string ValidBody = """
        {"detectedAt":"2026-11-05T11:40:00Z","pillar":"Application Health","kpi":"Latency","detection":"2 consecutive increases: Latency 900 vs reference 450 (+100.0%)","domain":"hotels","stage":"search"}
        """;

    private readonly A4FakeInvestigationStore _store = new();
    private readonly A4FakeWorkQueue _queue = new();
    private readonly A4FakeKpiDefinitionStore _definitions = new();

    private static IOptions<MonitoringOptions> Monitoring() =>
        Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret });

    private TriageInvestigationsFunction TriageFunction() => new(
        new TriageSignalSubmitter(_store, _queue, new A4FakeTimeProvider(Now), NullLogger<TriageSignalSubmitter>.Instance),
        _store,
        Monitoring(),
        NullLogger<TriageInvestigationsFunction>.Instance);

    private KpiDefinitionAdminFunctions AdminFunction() => new(
        _definitions, Monitoring(), new A4FakeTimeProvider(Now), NullLogger<KpiDefinitionAdminFunctions>.Instance);

    private static string KpiBody(int consecutiveCount = 2, int cooldownMinutes = 120, int sampleSize = 4000) => $$"""
        {"kpiDefinitionId":0,"pillar":"Application","kpi":"Latency","domain":"hotels","stage":"","sourceKey":"appinsights:latency",
         "direction":"increase","aggregation":"p95","evaluationFrequency":"00:05:00","bucketSize":"01:00:00","lookbackWindow":"1.00:00:00",
         "sampleSize":{{sampleSize}},"minObservationsPerBucket":30,"detectionMethod":"baselinePercent",
         "methodParamsJson":"{\"lookbackDays\":7,\"thresholdPercent\":50}","consecutiveCount":{{consecutiveCount}},"cooldownMinutes":{{cooldownMinutes}},
         "enabled":true,"updatedAt":"2000-01-01T00:00:00Z","updatedBy":"spoofed","changedBy":"ana@example.com"}
        """;

    [Fact]
    public async Task PostInvestigation_WithoutSecret_Returns401()
    {
        var function = TriageFunction();

        var missing = await function.SubmitAsync(A4Http.Request("http://localhost/api/triage/investigations", ValidBody), CancellationToken.None);
        var wrong = await function.SubmitAsync(A4Http.Request("http://localhost/api/triage/investigations", ValidBody, internalSecret: "nope"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(_store.Items);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task PostInvestigation_WithoutConfiguredSecret_FailsClosed()
    {
        var function = new TriageInvestigationsFunction(
            new TriageSignalSubmitter(_store, _queue, new A4FakeTimeProvider(Now), NullLogger<TriageSignalSubmitter>.Instance),
            _store,
            Options.Create(new MonitoringOptions { InternalApiSecret = "" }),
            NullLogger<TriageInvestigationsFunction>.Instance);

        var response = await function.SubmitAsync(A4Http.Request("http://localhost/api/triage/investigations", ValidBody, internalSecret: ""), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("42")]
    [InlineData("""{"pillar":"Application Health","kpi":"Latency","detection":"x"}""")]
    [InlineData("""{"detectedAt":"yesterday","pillar":"Application Health","kpi":"Latency","detection":"x"}""")]
    [InlineData("""{"detectedAt":"2026-11-05T11:40:00Z","pillar":"Marketing","kpi":"Latency","detection":"x"}""")]
    [InlineData("""{"detectedAt":"2026-11-05T11:40:00Z","pillar":"Application Health","kpi":"","detection":"x"}""")]
    [InlineData("""{"detectedAt":"2026-11-05T11:40:00Z","pillar":"Application Health","kpi":"Latency","detection":""}""")]
    [InlineData("""{"detectedAt":"2026-11-05T11:40:00Z","pillar":"Application Health","kpi":"Latency","detection":"x","domain":"trains"}""")]
    public async Task PostInvestigation_InvalidBody_Returns400(string body)
    {
        var response = await TriageFunction().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", body, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = response.ReadJson()["errors"]!.AsArray();
        Assert.NotEmpty(errors);
        Assert.Empty(_store.Items);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task PostInvestigation_TooLongFields_Returns400WithEveryError()
    {
        var body = $$"""
            {"detectedAt":"2026-11-05T11:40:00Z","pillar":"Application Health","kpi":"{{new string('k', 65)}}","detection":"{{new string('d', 401)}}","stage":"{{new string('s', 65)}}"}
            """;

        var response = await TriageFunction().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", body, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, response.ReadJson()["errors"]!.AsArray().Count);
    }

    [Fact]
    public async Task PostInvestigation_Valid_Returns202WithInvestigationId()
    {
        var response = await TriageFunction().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", ValidBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = response.ReadJson();
        var id = Guid.Parse(json["investigationId"]!.GetValue<string>());
        Assert.False(json["duplicate"]!.GetValue<bool>());

        var stored = Assert.Single(_store.Items.Values);
        Assert.Equal(id, stored.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Queued, stored.Status);
        Assert.Equal("Application Health", stored.Signal.Pillar);
        Assert.Equal(TriageSignalSources.Manual, stored.Signal.Source);
        Assert.Null(stored.Signal.AnomalyId);
        Assert.Equal("hotels", stored.Signal.Domain);
        Assert.Equal("search", stored.Signal.Stage);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 11, 40, 0, TimeSpan.Zero), stored.Signal.DetectedAt);

        var work = Assert.Single(_queue.Enqueued);
        Assert.Equal("portal", work.SourceMonkey);
        Assert.Equal(id.ToString(), work.PayloadId);
    }

    [Fact]
    public async Task PostInvestigation_PillarEnumName_IsNormalizedToDisplayName()
    {
        var body = """{"detectedAt":"2026-11-05T11:40:00Z","pillar":"business","kpi":"Bookings","detection":"drop"}""";

        var response = await TriageFunction().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", body, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Business Health", Assert.Single(_store.Items.Values).Signal.Pillar);
    }

    [Fact]
    public async Task PostInvestigation_Duplicate_ReturnsSameIdAndDuplicateTrue()
    {
        var function = TriageFunction();

        var first = await function.SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", ValidBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);
        var second = await function.SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", ValidBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJson = first.ReadJson();
        var secondJson = second.ReadJson();
        Assert.Equal(firstJson["investigationId"]!.GetValue<string>(), secondJson["investigationId"]!.GetValue<string>());
        Assert.False(firstJson["duplicate"]!.GetValue<bool>());
        Assert.True(secondJson["duplicate"]!.GetValue<bool>());
        Assert.Single(_store.Items);
        Assert.Single(_queue.Enqueued);
    }

    [Fact]
    public async Task GetInvestigation_Unknown_Returns404()
    {
        var id = Guid.NewGuid();

        var response = await TriageFunction().GetByIdAsync(A4Http.Request($"http://localhost/api/triage/investigations/{id}"), id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetInvestigation_Known_ReturnsCamelCaseWithEnumStrings()
    {
        var function = TriageFunction();
        var post = await function.SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", ValidBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);
        var id = Guid.Parse(post.ReadJson()["investigationId"]!.GetValue<string>());

        var response = await function.GetByIdAsync(A4Http.Request($"http://localhost/api/triage/investigations/{id}"), id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = response.ReadJson();
        Assert.Equal(id.ToString(), json["investigationId"]!.GetValue<string>());
        Assert.Equal("queued", json["status"]!.GetValue<string>());
        Assert.Equal("Latency", json["signal"]!["kpi"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("abc")]
    public async Task GetInvestigations_TopOutOfRange_Returns400(string top)
    {
        var response = await TriageFunction().GetRecentAsync(
            A4Http.Request($"http://localhost/api/triage/investigations?top={top}"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetInvestigations_Default_ReturnsArray()
    {
        var response = await TriageFunction().GetRecentAsync(A4Http.Request("http://localhost/api/triage/investigations"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.ReadJson().AsArray());
    }

    [Fact]
    public async Task UpsertKpiDefinition_WithoutSettingsSecret_Returns401()
    {
        var function = AdminFunction();

        var missing = await function.UpsertAsync(A4Http.Request("http://localhost/api/settings/kpis", KpiBody()), CancellationToken.None);
        // The internal API secret is a different credential and must not unlock a settings write.
        var wrongHeader = await function.UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(), internalSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongHeader.StatusCode);
        Assert.Empty(_definitions.Upserts);
    }

    [Theory]
    [InlineData(0, 120, 4000, "ConsecutiveCount must be 1..48.")]
    [InlineData(49, 120, 4000, "ConsecutiveCount must be 1..48.")]
    [InlineData(2, 10081, 4000, "CooldownMinutes must be 0..10080.")]
    [InlineData(2, 120, 100001, "SampleSize must be 0..100000.")]
    public async Task UpsertKpiDefinition_InvalidRanges_Returns400(int consecutiveCount, int cooldownMinutes, int sampleSize, string expectedError)
    {
        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(consecutiveCount, cooldownMinutes, sampleSize), writeSecret: A4Http.WriteSecret),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains(expectedError, errors);
        Assert.Empty(_definitions.Upserts);
    }

    [Fact]
    public async Task UpsertKpiDefinition_Valid_Returns200WithStoredRowAndServerStampedAudit()
    {
        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(), writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (saved, changedBy) = Assert.Single(_definitions.Upserts);
        Assert.Equal("ana@example.com", changedBy);
        Assert.Equal("ana@example.com", saved.UpdatedBy);
        Assert.Equal(Now, saved.UpdatedAt);
        Assert.Equal(KpiPillar.Application, saved.Pillar);
        Assert.Equal(TimeSpan.FromDays(1), saved.LookbackWindow);

        var json = response.ReadJson();
        Assert.Equal(1, json["kpiDefinitionId"]!.GetValue<int>());
        Assert.Equal("application", json["pillar"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("11/05/2026 11:40")]
    [InlineData("2026-11-05 11:40:00Z")]
    [InlineData("2026-11-05T11:40:00")]
    [InlineData("2026-11-05")]
    [InlineData("Thu, 05 Nov 2026 11:40:00 GMT")]
    public async Task PostInvestigation_NonIsoDetectedAt_Returns400(string detectedAt)
    {
        var body = $$"""{"detectedAt":"{{detectedAt}}","pillar":"Application Health","kpi":"Latency","detection":"x"}""";

        var response = await TriageFunction().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", body, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_store.Items);
    }

    [Theory]
    [InlineData("2026-11-05T11:40:00Z", "2026-11-05T11:40:00+00:00")]
    [InlineData("2026-11-05T08:40:00.123-03:00", "2026-11-05T11:40:00.123+00:00")]
    [InlineData("2026-11-05T11:40Z", "2026-11-05T11:40:00+00:00")]
    public async Task PostInvestigation_IsoDetectedAt_IsAccepted(string detectedAt, string expectedUtc)
    {
        var body = $$"""{"detectedAt":"{{detectedAt}}","pillar":"Application Health","kpi":"Latency","detection":"x"}""";

        var response = await TriageFunction().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", body, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(_store.Items.Values).Signal.DetectedAt;
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), stored);
        Assert.Equal(TimeSpan.Zero, stored.Offset);
    }

    [Fact]
    public async Task UpsertKpiDefinition_ChangedByTooLong_Returns400()
    {
        var body = KpiBody().Replace("ana@example.com", new string('a', 129));

        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", body, writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("changedBy must be at most 128 chars.", response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(_definitions.Upserts);
    }

    [Theory]
    [InlineData("300")]
    [InlineData("5")]
    [InlineData("PT5M")]
    [InlineData("5 minutes")]
    public async Task UpsertKpiDefinition_TimeSpanWithoutClockFormat_Returns400(string frequency)
    {
        var body = KpiBody().Replace("\"evaluationFrequency\":\"00:05:00\"", $"\"evaluationFrequency\":\"{frequency}\"");

        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", body, writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(response.ReadJson()["errors"]!.AsArray(), e => e!.GetValue<string>().StartsWith("EvaluationFrequency"));
        Assert.Empty(_definitions.Upserts);
    }

    [Fact]
    public async Task UpsertKpiDefinition_DuplicateNaturalKey_Returns400()
    {
        _definitions.Definitions.Add(A4Http.Definition(5, KpiPillar.Application));

        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(), writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "A KPI with the same Pillar/Kpi/Domain/Stage already exists (KpiDefinitionId 5).",
            response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(_definitions.Upserts);
    }

    [Fact]
    public async Task UpsertKpiDefinition_UpdateSameRowById_Returns200()
    {
        _definitions.Definitions.Add(A4Http.Definition(5, KpiPillar.Application));
        var body = KpiBody().Replace("\"kpiDefinitionId\":0", "\"kpiDefinitionId\":5");

        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", body, writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, Assert.Single(_definitions.Upserts).Definition.KpiDefinitionId);
    }

    [Fact]
    public async Task UpsertKpiDefinition_UnknownId_Returns400()
    {
        var body = KpiBody().Replace("\"kpiDefinitionId\":0", "\"kpiDefinitionId\":99");

        var response = await AdminFunction().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", body, writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_definitions.Upserts);
    }
}
