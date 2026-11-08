using System.Text.Json;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Monitoring.Triage;

public class TriageContractTests
{
    [Fact]
    public void TriageResult_SerializesToSpecJsonShape()
    {
        using var evidence = JsonDocument.Parse("{\"p95Ms\":4200}");
        var result = new TriageResult(
            Guid.Parse("8f0c2a4e-1d3b-4c5a-9e7f-0a1b2c3d4e5f"),
            new TriageTrigger(
                new DateTimeOffset(2026, 11, 4, 14, 5, 0, TimeSpan.Zero),
                "Application Health",
                "Latency",
                "hotels",
                null,
                "2 consecutive increases: Latency 4200 vs reference 2100 (+100.0%)"),
            new TriageCorrelation(evidence.RootElement.Clone(), null, null, null, null, null),
            new TriageAssessment(TriageAlertLevels.Amber, false, 0.6, "Latency doubled in hotels.", ["Check recent deployments"]));

        var json = JsonSerializer.Serialize(result, TriageJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        AssertProperties(root, "investigationId", "trigger", "correlation", "assessment");
        AssertProperties(root.GetProperty("trigger"), "detectedAt", "pillar", "kpi", "domain", "stage", "condition");
        AssertProperties(
            root.GetProperty("correlation"),
            "businessHealth", "applicationHealth", "infrastructureHealth", "codeReliability", "recentChanges", "knowledgeBase");
        AssertProperties(
            root.GetProperty("assessment"),
            "alertLevel", "possibleRedAlert", "confidence", "summary", "recommendedNextActions");

        Assert.Equal("amber", root.GetProperty("assessment").GetProperty("alertLevel").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("trigger").GetProperty("stage").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("correlation").GetProperty("applicationHealth").ValueKind);
        Assert.Equal(4200, root.GetProperty("correlation").GetProperty("businessHealth").GetProperty("p95Ms").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("assessment").GetProperty("recommendedNextActions").ValueKind);
    }

    [Fact]
    public void TriageSignal_RoundTripsThroughJson()
    {
        var signal = new TriageSignal(
            new DateTimeOffset(2026, 11, 4, 14, 5, 0, TimeSpan.Zero),
            "Application Health",
            "Latency",
            "2 consecutive increases: Latency 4200 vs reference 2100 (+100.0%)",
            "hotels",
            null,
            42,
            TriageSignalSources.Daemon);

        var json = JsonSerializer.Serialize(signal, TriageJson.Options);
        var back = JsonSerializer.Deserialize<TriageSignal>(json, TriageJson.Options);

        Assert.Equal(signal, back);
        Assert.Null(back!.ProfileKey);

        var withProfile = signal with { ProfileKey = "acme" };
        var profileJson = JsonSerializer.Serialize(withProfile, TriageJson.Options);
        using var doc = JsonDocument.Parse(profileJson);
        Assert.Equal("acme", doc.RootElement.GetProperty("profileKey").GetString());
        Assert.Equal(withProfile, JsonSerializer.Deserialize<TriageSignal>(profileJson, TriageJson.Options));
        Assert.Equal(TriageDedupKey.For(signal), TriageDedupKey.For(withProfile));
    }

    [Fact]
    public void TriageDedupKey_UsesAnomalyIdWhenPresent()
    {
        var signal = new TriageSignal(
            new DateTimeOffset(2026, 11, 4, 14, 5, 0, TimeSpan.Zero),
            "Application Health",
            "Latency",
            "detection",
            "hotels",
            null,
            42,
            TriageSignalSources.Daemon);

        Assert.Equal("anomaly:42", TriageDedupKey.For(signal));
    }

    [Fact]
    public void TriageDedupKey_FallsBackToHourBucket()
    {
        var signal = new TriageSignal(
            new DateTimeOffset(2026, 11, 4, 14, 5, 0, TimeSpan.Zero),
            "Application Health",
            "Latency",
            "detection",
            "hotels",
            null,
            null,
            TriageSignalSources.Manual);

        Assert.Equal("application health|latency|hotels||2026110414", TriageDedupKey.For(signal));
    }

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var actual = element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }
}
