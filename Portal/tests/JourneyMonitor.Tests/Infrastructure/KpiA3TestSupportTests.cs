using Dapper;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A3;

/// <summary>
/// Real-SQL access for the A3 integration tests. Reads SPEC1000_SQL_CONNECTION; when it is not set the
/// tests return early after writing why (xUnit 2 has no dynamic skip).
/// </summary>
internal static class A3SqlTestDatabase
{
    public const string EnvironmentVariable = "SPEC1000_SQL_CONNECTION";

    public static string? TryGetConnectionString(ITestOutputHelper output)
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            output.WriteLine($"{EnvironmentVariable} is not set - SQL integration test skipped (no assertions ran).");
            return null;
        }
        output.WriteLine($"{EnvironmentVariable} is set - running against real SQL.");
        return connectionString;
    }

    public static IOptions<JourneyStoreOptions> Options(string connectionString) =>
        Microsoft.Extensions.Options.Options.Create(new JourneyStoreOptions { ConnectionString = connectionString });

    public static async Task DeleteKpiDataAsync(string connectionString, int kpiDefinitionId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(
            """
            DELETE FROM monitoring.KpiBucket WHERE KpiDefinitionId = @Id;
            DELETE FROM monitoring.KpiSample WHERE KpiDefinitionId = @Id;
            DELETE FROM monitoring.KpiDetectionState WHERE KpiDefinitionId = @Id;
            DELETE FROM monitoring.KpiAnomaly WHERE KpiDefinitionId = @Id;
            DELETE FROM monitoring.IngestionWatermark WHERE KpiDefinitionId = @Id;
            DELETE FROM monitoring.KpiDefinitionAudit WHERE KpiDefinitionId = @Id;
            DELETE FROM monitoring.KpiDefinition WHERE KpiDefinitionId = @Id;
            """,
            new { Id = kpiDefinitionId });
    }

    public static async Task DeleteTriageDataAsync(string connectionString, string dedupKeyPrefix)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(
            "DELETE FROM monitoring.TriageInvestigation WHERE DedupKey LIKE @Prefix + N'%';",
            new { Prefix = dedupKeyPrefix });
    }
}

/// <summary>Builders for the A3 store tests.</summary>
internal static class A3Kpi
{
    public static KpiDefinition Definition(string kpi, KpiPillar pillar = KpiPillar.Application, string domain = "hotels", bool enabled = true) => new(
        KpiDefinitionId: 0,
        Pillar: pillar,
        Kpi: kpi,
        Domain: domain,
        Stage: "",
        SourceKey: "appinsights:latency",
        Direction: KpiDirection.Increase,
        Aggregation: KpiAggregation.P95,
        EvaluationFrequency: TimeSpan.FromMinutes(5),
        BucketSize: TimeSpan.FromHours(1),
        LookbackWindow: TimeSpan.FromDays(1),
        SampleSize: 4000,
        MinObservationsPerBucket: 30,
        DetectionMethod: DetectionMethodKind.BaselinePercent,
        MethodParamsJson: "{\"lookbackDays\":7,\"thresholdPercent\":50}",
        ConsecutiveCount: 2,
        CooldownMinutes: 120,
        Enabled: enabled,
        UpdatedAt: default,
        UpdatedBy: "");

    public static KpiBucket Bucket(int id, DateTimeOffset start, long total, double? p95) =>
        new(id, start, total, (int)Math.Min(total, 100), p95 / 2, p95, p95 / 3, 1.5, null);

    public static TriageSignal Signal(string kpi, DateTimeOffset detectedAt) =>
        new(detectedAt, "Application Health", kpi, "2 consecutive increases", "hotels", null, null, TriageSignalSources.Manual);

    public static TriageInvestigation Investigation(Guid id, string dedupKey, TriageSignal signal) =>
        new(id, dedupKey, null, signal, TriageInvestigationStatus.Queued, null, null, false, DateTimeOffset.UtcNow, null, null);

    public static TriageResult Result(Guid id, TriageSignal signal)
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"p95\":812.5,\"baseline\":400}");
        return new TriageResult(
            id,
            new TriageTrigger(signal.DetectedAt, signal.Pillar, signal.Kpi, signal.Domain, signal.Stage, signal.Detection),
            new TriageCorrelation(null, doc.RootElement.Clone(), null, null, null, null),
            new TriageAssessment(TriageAlertLevels.Amber, false, 0.7, "Latency doubled on hotels.", ["Check the last deployment."]));
    }
}
