using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Returns a fixed, hand-built set of per-transaction rows instead of calling Application Insights.</summary>
internal sealed class FakeSpikeTelemetrySource(IReadOnlyList<TelemetryJourneyRow> rows) : ITelemetrySource
{
    public TelemetryQueryRequest? LastRequest { get; private set; }

    public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(rows);
    }
}

/// <summary>Ignores the domain and always returns the global MonitoringOptions-resolved values - no per-domain override in these tests.</summary>
internal sealed class FakeLatencySpikeSettingsResolver(MonitoringOptions options) : ILatencySpikeSettingsResolver
{
    public Task<ResolvedLatencySettings> ResolveAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedLatencySettings(
            options.ResolvedSpikeThresholdMs,
            options.ResolvedSpikeOccurrenceThreshold,
            options.ResolvedSpikeWindowMinutes));
}

public class LatencySpikeDetectorServiceTests
{
    private static TelemetryJourneyRow Row(string stage, double ms, string correlationId, string operationId, string resultCode) =>
        JsonSerializer.Deserialize<TelemetryJourneyRow>(
            $$"""{"Stage":"{{stage}}","Ms":{{ms}},"CorrelationId":"{{correlationId}}","OperationId":"{{operationId}}","ResultCode":"{{resultCode}}"}""")!;

    private static InsightsOptions ValidInsightsOptions() => new()
    {
        Hotels = new HotelInsightsOptions { TenantId = "11111111-1111-1111-1111-111111111111", AppInsightsAppId = "22222222-2222-2222-2222-222222222222" },
    };

    private static LatencySpikeDetectorService BuildService(
        IReadOnlyList<TelemetryJourneyRow> rows,
        MonitoringOptions? monitoringOptions = null,
        JourneyMonitor.Core.Abstractions.IFindingsRepository? repository = null)
    {
        var options = monitoringOptions ?? new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        return new LatencySpikeDetectorService(
            new FakeSpikeTelemetrySource(rows),
            new KqlQueryTextProvider(),
            Options.Create(ValidInsightsOptions()),
            Options.Create(options),
            new FakeLatencySpikeSettingsResolver(options),
            repository ?? new InMemoryFindingsRepository());
    }

    [Fact]
    public async Task KqlFloorNeverExceedsTheDomainsEffectiveThresholdSoALowOverrideStillMatches()
    {
        // Global floor (1000ms, the default) would otherwise be higher than this domain's
        // per-domain-overridden threshold (100ms) and discard matching rows in KQL itself before
        // C# ever sees them.
        var telemetrySource = new FakeSpikeTelemetrySource([]);
        var options = new MonitoringOptions { SpikeThresholdMs = 100, SpikeOccurrenceThreshold = 1, SpikeWindowMinutes = 60 };
        var service = new LatencySpikeDetectorService(
            telemetrySource,
            new KqlQueryTextProvider(),
            Options.Create(ValidInsightsOptions()),
            Options.Create(options),
            new FakeLatencySpikeSettingsResolver(options),
            new InMemoryFindingsRepository());

        await service.RunAsync("cars", "production", CancellationToken.None);

        Assert.Contains("let floorMs = 100;", telemetrySource.LastRequest!.Kql);
        Assert.DoesNotContain("let floorMs = 1000;", telemetrySource.LastRequest.Kql);
    }

    [Fact]
    public async Task BelowOccurrenceThresholdDoesNotCreateAFinding()
    {
        var rows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 6000, "c2", "op2", "200"),
        };
        var service = BuildService(rows);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task AtOrAboveOccurrenceThresholdCreatesAFindingWithEvidencePopulated()
    {
        var rows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 7000, "c2", "op2", "200"),
            Row("07 Booking", 41200, "c3", "op3", "504"),
        };
        var service = BuildService(rows);

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal(FindingType.LatencySpikeCluster, finding.FindingType);
        Assert.Equal("07 Booking", finding.Stage);
        Assert.Equal("cars:07 Booking", finding.CorrelationId);
        Assert.Equal(MonkeyKey.LatencySpikeMonkey, finding.OriginMonkeyKey);
        Assert.False(string.IsNullOrWhiteSpace(finding.EvidenceJson));

        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(3, evidence.RootElement.GetProperty("observedCount").GetInt32());
        Assert.Equal(41200, evidence.RootElement.GetProperty("maxDurationMs").GetDouble());
        Assert.True(evidence.RootElement.GetProperty("statusCodeBreakdown").TryGetProperty("504", out _));
    }

    [Fact]
    public async Task DoesNotCreateADuplicateFindingWhileOneIsAlreadyOpenForTheSameStage()
    {
        var rows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 7000, "c2", "op2", "200"),
            Row("07 Booking", 8000, "c3", "op3", "200"),
        };
        var repository = new InMemoryFindingsRepository();
        var service = BuildService(rows, repository: repository);

        var first = await service.RunAsync("cars", "production", CancellationToken.None);
        var second = await service.RunAsync("cars", "production", CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task RefreshesTheOpenFindingsTitleAndEvidenceInPlaceWithoutReturningItForRediagnosis()
    {
        var repository = new InMemoryFindingsRepository();
        var firstRows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 7000, "c2", "op2", "200"),
            Row("07 Booking", 8000, "c3", "op3", "200"),
        };
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var first = await BuildService(firstRows, options, repository).RunAsync("cars", "production", CancellationToken.None);
        var findingId = Assert.Single(first).Id;

        // Simulates a Settings change: a lower threshold now, so the same stage looks worse.
        var tighterOptions = new MonitoringOptions { SpikeThresholdMs = 1000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var second = await BuildService(firstRows, tighterOptions, repository).RunAsync("cars", "production", CancellationToken.None);

        Assert.Empty(second); // not re-added - must not trigger another Doctor Monkey diagnosis
        var refreshed = await repository.GetByIdAsync(findingId, CancellationToken.None);
        Assert.Contains(">1000ms", refreshed!.Title);
        using var evidence = JsonDocument.Parse(refreshed.EvidenceJson);
        Assert.Equal(1000, evidence.RootElement.GetProperty("spikeThresholdMs").GetInt32());
    }

    [Fact]
    public async Task ResolvesAnOpenFindingWhoseStageNoLongerQualifiesAfterASettingsChange()
    {
        var repository = new InMemoryFindingsRepository();
        var rows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 7000, "c2", "op2", "200"),
            Row("07 Booking", 8000, "c3", "op3", "200"),
        };
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var first = await BuildService(rows, options, repository).RunAsync("cars", "production", CancellationToken.None);
        var findingId = Assert.Single(first).Id;

        // Threshold raised well above every row's Ms - the stage no longer qualifies at all
        // (unlike RefreshesTheOpenFindingsTitleAndEvidenceInPlaceWithoutReturningItForRediagnosis,
        // where it still qualifies just with different numbers).
        var raisedOptions = new MonitoringOptions { SpikeThresholdMs = 50000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var second = await BuildService(rows, raisedOptions, repository).RunAsync("cars", "production", CancellationToken.None);

        Assert.Empty(second);
        var resolved = await repository.GetByIdAsync(findingId, CancellationToken.None);
        Assert.Equal("resolved", resolved!.Status);
        Assert.Null(await repository.GetOpenByCorrelationIdAsync("cars:07 Booking", CancellationToken.None));
    }

    [Fact]
    public async Task DoesNotResolveAnOpenFindingForAStageThatStillQualifies()
    {
        var repository = new InMemoryFindingsRepository();
        var rows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 7000, "c2", "op2", "200"),
            Row("07 Booking", 8000, "c3", "op3", "200"),
        };
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 };
        var first = await BuildService(rows, options, repository).RunAsync("cars", "production", CancellationToken.None);
        var findingId = Assert.Single(first).Id;

        // Same rows queried again with the same options - the stage still qualifies, so the
        // finding must stay open (refreshed in place), not be resolved.
        await BuildService(rows, options, repository).RunAsync("cars", "production", CancellationToken.None);

        var stillOpen = await repository.GetByIdAsync(findingId, CancellationToken.None);
        Assert.Equal("open", stillOpen!.Status);
    }

    [Fact]
    public async Task SeverityEscalatesWithHowFarPastTheThresholdTheCountIs()
    {
        var rows = Enumerable.Range(0, 9)
            .Select(i => Row("07 Booking", 6000 + i, $"c{i}", $"op{i}", "200"))
            .ToArray();
        var service = BuildService(rows, new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 60 });

        var findings = await service.RunAsync("cars", "production", CancellationToken.None);

        // 9 observed / 3 threshold = 3x -> "high" per the >=3 ratio rule.
        Assert.Equal("high", Assert.Single(findings).Severity);
    }

    [Fact]
    public async Task PreviewAsyncReturnsCandidateGroupsWithoutCreatingAFindingOrTouchingDedup()
    {
        var rows = new[]
        {
            Row("07 Booking", 6000, "c1", "op1", "200"),
            Row("07 Booking", 7000, "c2", "op2", "200"),
            Row("07 Booking", 41200, "c3", "op3", "504"),
        };
        var repository = new InMemoryFindingsRepository();
        var service = BuildService(rows, repository: repository);

        var preview = await service.PreviewAsync("cars", "production", CancellationToken.None);

        var group = Assert.Single(preview);
        Assert.Equal("07 Booking", group.Stage);
        Assert.Equal(3, group.ObservedCount);
        Assert.Equal(41200, group.MaxDurationMs);
        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
        Assert.Null(await repository.GetOpenByCorrelationIdAsync("cars:07 Booking", CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAsyncOmitsStagesBelowTheOccurrenceThreshold()
    {
        var rows = new[] { Row("07 Booking", 6000, "c1", "op1", "200") };
        var service = BuildService(rows, new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 2 });

        var preview = await service.PreviewAsync("cars", "production", CancellationToken.None);

        Assert.Empty(preview);
    }
}
