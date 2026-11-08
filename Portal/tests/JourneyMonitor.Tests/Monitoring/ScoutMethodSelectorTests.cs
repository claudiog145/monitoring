using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Tests.Company;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// Scout Method mode (contract v4 §4 SD8): the daemons' anomalies decide the cases, the board only supplies the evidence row.
/// Profile <see cref="ScoutCompanyProfiles.TwoServicesOnOneHost"/>: service "retail", step "checkout", entities from the board's Entity column.
/// </summary>
public class ScoutMethodSelectorTests
{
    private static readonly ICompanyAdapter Adapter = new ProfileDrivenAdapter(ScoutCompanyProfiles.TwoServicesOnOneHost());
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 13, 40, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    [Fact]
    public void DomainAnomaly_MergesEveryRowOfTheServiceNearItsBucket_IntoOneServiceWideCase()
    {
        var rows = new[]
        {
            Row("old", rank: 3, tenant: "T-1", start: T0.AddHours(-2), ops: ["x"]),
            Row("near-low", rank: 1, tenant: "T-2", start: T0, requests: 3, ops: ["a", "b"]),
            Row("near-high", rank: 2, tenant: "T-3", start: T0, requests: 4, ops: ["c", "a"]),
        };
        var signal = new ScoutDaemonSignal(42, "retail", "", T0.AddMinutes(1), TimeSpan.FromMinutes(1), "Exceptions · demo 1m", "1 consecutive increases");

        var selected = Assert.Single(ScoutMethodSelector.Select(Adapter, rows, [signal], FiveMinutes));

        Assert.Contains("service-wide", selected.CaseId);
        Assert.Contains("#42", selected.CaseId);
        Assert.Equal(3, selected.PriorityRank);
        Assert.Equal("Investigate now", selected.Priority);
        Assert.Contains("Daemon anomaly #42", selected.Reason);
        using var payload = JsonDocument.Parse(selected.GroupPayloadJson);
        Assert.Equal(new[] { "a", "b", "c" }, payload.RootElement.GetProperty("Ops").EnumerateArray().Select(e => e.GetString()).OrderBy(s => s).ToArray());
        using var raw = JsonDocument.Parse(selected.RawRowJson);
        Assert.Equal(7, raw.RootElement.GetProperty("Requests").GetInt64());
    }

    [Fact]
    public void DomainAnomaly_WithNoRowNearItsBucket_FallsBackToTheBestRowOfTheService()
    {
        var rows = new[] { Row("quiet", rank: 1, tenant: "T-1", start: T0.AddHours(-1), requests: 1), Row("busy", rank: 1, tenant: "T-2", start: T0.AddHours(-1), requests: 40) };
        var signal = new ScoutDaemonSignal(3, "retail", "", T0, FiveMinutes, "Latency", "x");

        Assert.Equal("busy", Assert.Single(ScoutMethodSelector.Select(Adapter, rows, [signal], FiveMinutes)).CaseId);
    }

    [Fact]
    public void EntityAnomaly_OnlyMatchesItsOwnEntity()
    {
        var rows = new[] { Row("t7", rank: 3, tenant: "T-7", start: T0), Row("t9", rank: 1, tenant: "T-9", start: T0) };
        var signal = new ScoutDaemonSignal(7, "retail", "T-9", T0, FiveMinutes, "Latency", "2 consecutive increases");

        Assert.Equal("t9", Assert.Single(ScoutMethodSelector.Select(Adapter, rows, [signal], FiveMinutes)).CaseId);
    }

    [Fact]
    public void NoAnomaly_OrAnotherService_SelectsNothing()
    {
        var rows = new[] { Row("a", rank: 3, tenant: "T-7", start: T0) };

        Assert.Empty(ScoutMethodSelector.Select(Adapter, rows, [], FiveMinutes));
        Assert.Empty(ScoutMethodSelector.Select(Adapter, rows,
            [new ScoutDaemonSignal(1, "shipping", "", T0, FiveMinutes, "Latency", "x")], FiveMinutes));
    }

    [Fact]
    public void TwoEntityAnomaliesOnTheSameRow_YieldOneCase()
    {
        var rows = new[] { Row("a", rank: 2, tenant: "T-7", start: T0) };
        var signals = new[]
        {
            new ScoutDaemonSignal(1, "retail", "T-7", T0, TimeSpan.FromMinutes(1), "Latency", "x"),
            new ScoutDaemonSignal(2, "retail", "T-7", T0.AddMinutes(2), TimeSpan.FromMinutes(1), "Latency", "y"),
        };

        Assert.Single(ScoutMethodSelector.Select(Adapter, rows, signals, FiveMinutes));
    }
    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(null, 3, false)]
    [InlineData(null, 5, true)]
    [InlineData(1, 3, true)]
    [InlineData(1, 7, true)]
    public void IntervalMinutes_DefaultKeepsTheFiveMinuteCadence(int? interval, int minute, bool due)
    {
        var options = new ScoutOptions { IntervalMinutes = interval };
        Assert.Equal(due, options.IsDueAt(new DateTimeOffset(2026, 11, 8, 13, minute, 0, TimeSpan.Zero)));
    }

    private static ScoutBoardRow Row(string caseId, int rank, string tenant, DateTimeOffset start, int requests = 10, string[]? ops = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["CaseId"] = caseId, ["GroupKey"] = caseId + " group", ["ServiceKey"] = "retail", ["Service"] = "Retail",
            ["StepKey"] = "checkout", ["Step"] = "Checkout", ["Entity"] = tenant, ["Source"] = "SOR",
            ["Start"] = start, ["End"] = start.AddMinutes(5), ["Priority"] = "Review", ["Ops"] = ops ?? [caseId + "-op"],
        };
        var payloadJson = JsonSerializer.Serialize(payload);
        var rawJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Service"] = "Retail", ["ServiceKey"] = "retail", ["Entity"] = tenant, ["Step"] = "Checkout", ["StepKey"] = "checkout",
            ["Requests"] = requests, ["Source"] = "SOR", ["GroupPayload"] = payloadJson, ["PriorityRank"] = rank, ["Interval"] = start,
        });
        var classification = ScoutRowMapper.Classify(Adapter, ScoutRowMapper.ToRowContext(rawJson, payloadJson, tenant));
        return new ScoutBoardRow(
            ScoutBoard.Latency, caseId, caseId + " group", classification.ServiceLabel, classification.EntityKey, classification.StepLabel,
            "SOR", "Review", rank, "Observed", start, start.AddMinutes(5), payloadJson, rawJson);
    }
}
