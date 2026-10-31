using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.UseCases;

/// <summary>Mirrors SqlLatencySpikeSettingsStore.OpenConnection: throws on every call, standing in for "not configured".</summary>
internal sealed class ThrowingLatencySpikeSettingsStore : ILatencySpikeSettingsStore
{
    public Task<IReadOnlyList<LatencySpikeSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");

    public Task UpsertAsync(string domain, int? spikeThresholdMs, int? spikeOccurrenceThreshold, int? spikeWindowMinutes, string changedBy, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");

    public Task<IReadOnlyList<LatencySpikeSettingAuditEntry>> GetAuditAsync(string? domain, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");
}

public class LatencySpikeSettingsServiceTests
{
    private static LatencySpikeSettingsService BuildService(FakeLatencySpikeSettingsStore store, MonitoringOptions? options = null)
    {
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = "Server=fake;" });
        var resolver = new LatencySpikeSettingsResolver(
            store,
            storeOptions,
            Options.Create(options ?? new MonitoringOptions()),
            NullLogger<LatencySpikeSettingsResolver>.Instance);
        return new LatencySpikeSettingsService(store, resolver, storeOptions);
    }

    [Fact]
    public async Task GetAllAsyncReturnsAllFourKnownDomainsEvenWithoutAnyOverride()
    {
        var service = BuildService(new FakeLatencySpikeSettingsStore());

        var views = await service.GetAllAsync(CancellationToken.None);

        Assert.Equal(4, views.Count);
        Assert.Equal(["cars", "hotels", "cruises", "air"], views.Select(v => v.Domain));
        Assert.All(views, v => Assert.Null(v.OverrideSpikeThresholdMs));
        Assert.All(views, v => Assert.Equal(MonitoringOptions.DefaultSpikeThresholdMs, v.EffectiveSpikeThresholdMs));
    }

    [Fact]
    public async Task GetAllAsyncReturnsEffectiveDefaultsWithoutTouchingTheStoreWhenSqlIsNotConfigured()
    {
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = null });
        var resolver = new LatencySpikeSettingsResolver(
            new ThrowingLatencySpikeSettingsStore(),
            storeOptions,
            Options.Create(new MonitoringOptions()),
            NullLogger<LatencySpikeSettingsResolver>.Instance);
        var service = new LatencySpikeSettingsService(new ThrowingLatencySpikeSettingsStore(), resolver, storeOptions);

        var views = await service.GetAllAsync(CancellationToken.None);

        Assert.Equal(4, views.Count);
        Assert.All(views, v => Assert.Equal(MonitoringOptions.DefaultSpikeThresholdMs, v.EffectiveSpikeThresholdMs));
    }

    [Fact]
    public async Task GetAuditAsyncReturnsEmptyWithoutTouchingTheStoreWhenSqlIsNotConfigured()
    {
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = null });
        var resolver = new LatencySpikeSettingsResolver(
            new ThrowingLatencySpikeSettingsStore(),
            storeOptions,
            Options.Create(new MonitoringOptions()),
            NullLogger<LatencySpikeSettingsResolver>.Instance);
        var service = new LatencySpikeSettingsService(new ThrowingLatencySpikeSettingsStore(), resolver, storeOptions);

        var audit = await service.GetAuditAsync(null, CancellationToken.None);

        Assert.Empty(audit);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnUnknownDomain()
    {
        var service = BuildService(new FakeLatencySpikeSettingsStore());

        var (statusCode, _) = await service.UpsertAsync("not-a-domain", 100, 1, 5, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsNonPositiveValues()
    {
        var service = BuildService(new FakeLatencySpikeSettingsStore());

        var (statusCode, _) = await service.UpsertAsync("cars", 0, 1, 5, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnOversizedWindowMinutes()
    {
        // A window this large would make SqlLatencySpikeDetectorService's
        // DateTimeOffset.UtcNow.AddMinutes(-windowMinutes) throw (outside the supported range).
        var service = BuildService(new FakeLatencySpikeSettingsStore());

        var (statusCode, _) = await service.UpsertAsync("cars", 5000, 5, int.MaxValue, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnOversizedThresholdMs()
    {
        var service = BuildService(new FakeLatencySpikeSettingsStore());

        var (statusCode, _) = await service.UpsertAsync("cars", int.MaxValue, 5, 60, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnOversizedOccurrenceThreshold()
    {
        var service = BuildService(new FakeLatencySpikeSettingsStore());

        var (statusCode, _) = await service.UpsertAsync("cars", 5000, int.MaxValue, 60, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task GetAllAsyncFallsBackToDefaultsWhenTheStoreThrowsEvenThoughConfigured()
    {
        // Reproduces "JourneyStore:ConnectionString is set but monitoring.LatencySpikeSetting was
        // never provisioned on that database" (e.g. a fresh QA deployment) - a plain read must
        // degrade to the 4 known domains' effective defaults, not 500.
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = "Server=fake;" });
        var resolver = new LatencySpikeSettingsResolver(
            new ThrowingLatencySpikeSettingsStore(),
            storeOptions,
            Options.Create(new MonitoringOptions()),
            NullLogger<LatencySpikeSettingsResolver>.Instance);
        var service = new LatencySpikeSettingsService(new ThrowingLatencySpikeSettingsStore(), resolver, storeOptions);

        var views = await service.GetAllAsync(CancellationToken.None);

        Assert.Equal(4, views.Count);
        Assert.All(views, v => Assert.Equal(MonitoringOptions.DefaultSpikeThresholdMs, v.EffectiveSpikeThresholdMs));
    }

    [Fact]
    public async Task GetAuditAsyncReturnsEmptyWhenTheStoreThrowsEvenThoughConfigured()
    {
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = "Server=fake;" });
        var resolver = new LatencySpikeSettingsResolver(
            new ThrowingLatencySpikeSettingsStore(),
            storeOptions,
            Options.Create(new MonitoringOptions()),
            NullLogger<LatencySpikeSettingsResolver>.Instance);
        var service = new LatencySpikeSettingsService(new ThrowingLatencySpikeSettingsStore(), resolver, storeOptions);

        var audit = await service.GetAuditAsync(null, CancellationToken.None);

        Assert.Empty(audit);
    }

    [Fact]
    public async Task UpsertAsyncPersistsTheOverrideAndRecordsWhoChangedIt()
    {
        var store = new FakeLatencySpikeSettingsStore();
        var service = BuildService(store);

        var (statusCode, result) = await service.UpsertAsync("cars", 250, 2, 30, "claudio", CancellationToken.None);

        Assert.Equal(200, statusCode);
        var view = Assert.IsType<LatencySpikeSettingView>(result);
        Assert.Equal(250, view.EffectiveSpikeThresholdMs);
        Assert.Equal("claudio", view.UpdatedBy);

        var audit = await service.GetAuditAsync("cars", CancellationToken.None);
        var entry = Assert.Single(audit);
        Assert.Equal(250, entry.SpikeThresholdMs);
        Assert.Equal("claudio", entry.ChangedBy);
    }

    [Fact]
    public async Task UpsertAsyncDefaultsChangedByToAnonymousWhenBlank()
    {
        var store = new FakeLatencySpikeSettingsStore();
        var service = BuildService(store);

        await service.UpsertAsync("cars", 250, 2, 30, changedBy: "  ", CancellationToken.None);

        var audit = await service.GetAuditAsync("cars", CancellationToken.None);
        Assert.Equal("anonymous", Assert.Single(audit).ChangedBy);
    }
}
