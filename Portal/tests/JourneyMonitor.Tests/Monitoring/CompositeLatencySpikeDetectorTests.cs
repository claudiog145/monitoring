using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Captures formatted log messages instead of writing anywhere, so tests can assert on shadow-mode output.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

public class CompositeLatencySpikeDetectorTests
{
    private static TelemetryJourneyRow KqlRow(string stage, double ms, string correlationId, string resultCode) =>
        JsonSerializer.Deserialize<TelemetryJourneyRow>(
            $$"""{"Stage":"{{stage}}","Ms":{{ms}},"CorrelationId":"{{correlationId}}","OperationId":"op","ResultCode":"{{resultCode}}"}""")!;

    private static TelemetryJourneyRow SqlRow(string correlationId, string stage, double maxMs) =>
        JsonSerializer.Deserialize<TelemetryJourneyRow>(
            $$"""{"CorrelationId":"{{correlationId}}","Journey":[{"stage":"{{stage}}","maxMs":{{maxMs}},"status":"GREEN","failureRatePct":0}]}""")!;

    private static InsightsOptions ValidInsightsOptions() => new()
    {
        Hotels = new HotelInsightsOptions { TenantId = "11111111-1111-1111-1111-111111111111", AppInsightsAppId = "22222222-2222-2222-2222-222222222222" },
    };

    private static (CompositeLatencySpikeDetector Composite, RecordingLogger<CompositeLatencySpikeDetector> Logger) BuildComposite(
        IReadOnlyList<TelemetryJourneyRow> kqlRows,
        StoredJourneys? sqlSnapshot,
        MonitoringOptions monitoringOptions)
    {
        var kqlDetector = new LatencySpikeDetectorService(
            new FakeSpikeTelemetrySource(kqlRows),
            new KqlQueryTextProvider(),
            Options.Create(ValidInsightsOptions()),
            Options.Create(monitoringOptions),
            new FakeLatencySpikeSettingsResolver(monitoringOptions),
            new InMemoryFindingsRepository());

        var sqlDetector = new SqlLatencySpikeDetectorService(
            new FakeJourneyStore(sqlSnapshot),
            new FakeLatencySpikeSettingsResolver(monitoringOptions),
            new InMemoryFindingsRepository());

        var logger = new RecordingLogger<CompositeLatencySpikeDetector>();
        return (new CompositeLatencySpikeDetector(kqlDetector, sqlDetector, Options.Create(monitoringOptions), logger), logger);
    }

    [Fact]
    public async Task DomainNotListedInSqlDetectorDomainsUsesTheKqlDetector()
    {
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 2, SqlDetectorDomains = "cars" };
        var kqlRows = new[]
        {
            KqlRow("07 Booking", 6000, "c1", "200"),
            KqlRow("07 Booking", 7000, "c2", "200"),
        };
        var (composite, _) = BuildComposite(kqlRows, sqlSnapshot: null, options);

        var findings = await composite.RunAsync("hotels", "production", CancellationToken.None);

        var finding = Assert.Single(findings);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.True(evidence.RootElement.TryGetProperty("statusCodeBreakdown", out _));
    }

    [Fact]
    public async Task DomainListedInSqlDetectorDomainsUsesTheSqlDetector()
    {
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 2, SqlDetectorDomains = "cars" };
        var sqlSnapshot = new StoredJourneys(Guid.NewGuid(), 2, DateTimeOffset.UtcNow, "15m", new List<TelemetryJourneyRow>
        {
            SqlRow("c1", "07 Booking", 6000),
            SqlRow("c2", "07 Booking", 7000),
        });
        var (composite, _) = BuildComposite(kqlRows: [], sqlSnapshot, options);

        var findings = await composite.RunAsync("cars", "production", CancellationToken.None);

        var finding = Assert.Single(findings);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("sql-journey-store", evidence.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task DomainNotInShadowDomainsDoesNotLogAComparison()
    {
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 2, SqlDetectorDomains = "cars" };
        var kqlRows = new[] { KqlRow("07 Booking", 6000, "c1", "200"), KqlRow("07 Booking", 7000, "c2", "200") };
        var (composite, logger) = BuildComposite(kqlRows, sqlSnapshot: null, options);

        await composite.RunAsync("hotels", "production", CancellationToken.None);

        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task DomainInShadowDomainsLogsBothDetectorsResultsWithoutPersistingTheShadowOne()
    {
        var options = new MonitoringOptions
        {
            SpikeThresholdMs = 5000,
            SpikeOccurrenceThreshold = 2,
            SqlDetectorDomains = "cars",
            ShadowDomains = "cars",
        };
        var kqlRows = new[] { KqlRow("07 Booking", 6000, "c1", "200"), KqlRow("07 Booking", 7000, "c2", "200") };
        var sqlSnapshot = new StoredJourneys(Guid.NewGuid(), 2, DateTimeOffset.UtcNow, "15m", new List<TelemetryJourneyRow>
        {
            SqlRow("c1", "07 Booking", 6000),
            SqlRow("c2", "07 Booking", 7000),
        });
        var (composite, logger) = BuildComposite(kqlRows, sqlSnapshot, options);

        var findings = await composite.RunAsync("cars", "production", CancellationToken.None);

        // The active detector (SQL, per SqlDetectorDomains) still persisted its Finding normally.
        var finding = Assert.Single(findings);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("sql-journey-store", evidence.RootElement.GetProperty("source").GetString());

        // The shadow (KQL) preview ran too and got logged, mentioning the same stage/count, but
        // created no Finding of its own (only one Finding total, asserted above).
        var comparison = Assert.Single(logger.Messages);
        Assert.Contains("active=sql", comparison);
        Assert.Contains("shadow=kql", comparison);
        Assert.Contains("07 Booking=2", comparison);
    }
}
