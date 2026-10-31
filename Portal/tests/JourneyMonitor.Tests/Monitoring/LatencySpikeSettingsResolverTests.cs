using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>In-memory ILatencySpikeSettingsStore double - no SQL, keeps everything in a dictionary.</summary>
internal sealed class FakeLatencySpikeSettingsStore : ILatencySpikeSettingsStore
{
    private readonly Dictionary<string, LatencySpikeSetting> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LatencySpikeSettingAuditEntry> _audit = [];

    public Task<IReadOnlyList<LatencySpikeSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LatencySpikeSetting>>(_settings.Values.ToList());

    public Task UpsertAsync(
        string domain,
        int? spikeThresholdMs,
        int? spikeOccurrenceThreshold,
        int? spikeWindowMinutes,
        string changedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _settings[domain] = new LatencySpikeSetting(domain, spikeThresholdMs, spikeOccurrenceThreshold, spikeWindowMinutes, now, changedBy);
        _audit.Insert(0, new LatencySpikeSettingAuditEntry(domain, spikeThresholdMs, spikeOccurrenceThreshold, spikeWindowMinutes, now, changedBy));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LatencySpikeSettingAuditEntry>> GetAuditAsync(string? domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LatencySpikeSettingAuditEntry>>(
            (domain is null ? _audit : _audit.Where(a => string.Equals(a.Domain, domain, StringComparison.OrdinalIgnoreCase))).ToList());
}

public class LatencySpikeSettingsResolverTests
{
    private static LatencySpikeSettingsResolver BuildResolver(
        ILatencySpikeSettingsStore store,
        bool storeConfigured,
        MonitoringOptions? monitoringOptions = null) =>
        new(
            store,
            Options.Create(new JourneyStoreOptions { ConnectionString = storeConfigured ? "Server=fake;" : null }),
            Options.Create(monitoringOptions ?? new MonitoringOptions()),
            NullLogger<LatencySpikeSettingsResolver>.Instance);

    [Fact]
    public async Task FallsBackToGlobalDefaultsWhenTheStoreIsNotConfigured()
    {
        var resolver = BuildResolver(new FakeLatencySpikeSettingsStore(), storeConfigured: false);

        var resolved = await resolver.ResolveAsync("cars", CancellationToken.None);

        Assert.Equal(MonitoringOptions.DefaultSpikeThresholdMs, resolved.ThresholdMs);
        Assert.Equal(MonitoringOptions.DefaultSpikeOccurrenceThreshold, resolved.OccurrenceThreshold);
        Assert.Equal(MonitoringOptions.DefaultSpikeWindowMinutes, resolved.WindowMinutes);
    }

    [Fact]
    public async Task FallsBackToGlobalOptionsWhenNoOverrideExistsForTheDomain()
    {
        var store = new FakeLatencySpikeSettingsStore();
        var options = new MonitoringOptions { SpikeThresholdMs = 250, SpikeOccurrenceThreshold = 2, SpikeWindowMinutes = 30 };
        var resolver = BuildResolver(store, storeConfigured: true, options);

        var resolved = await resolver.ResolveAsync("hotels", CancellationToken.None);

        Assert.Equal(250, resolved.ThresholdMs);
        Assert.Equal(2, resolved.OccurrenceThreshold);
        Assert.Equal(30, resolved.WindowMinutes);
    }

    [Fact]
    public async Task PerDomainOverrideWinsOverGlobalOptions()
    {
        var store = new FakeLatencySpikeSettingsStore();
        await store.UpsertAsync("cars", 100, 1, 5, "test-user", CancellationToken.None);
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 5, SpikeWindowMinutes = 60 };
        var resolver = BuildResolver(store, storeConfigured: true, options);

        var resolved = await resolver.ResolveAsync("cars", CancellationToken.None);

        Assert.Equal(100, resolved.ThresholdMs);
        Assert.Equal(1, resolved.OccurrenceThreshold);
        Assert.Equal(5, resolved.WindowMinutes);
    }

    [Fact]
    public async Task OverrideDoesNotAffectOtherDomains()
    {
        var store = new FakeLatencySpikeSettingsStore();
        await store.UpsertAsync("cars", 100, 1, 5, "test-user", CancellationToken.None);
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 5, SpikeWindowMinutes = 60 };
        var resolver = BuildResolver(store, storeConfigured: true, options);

        var resolved = await resolver.ResolveAsync("hotels", CancellationToken.None);

        Assert.Equal(5000, resolved.ThresholdMs);
    }

    [Fact]
    public async Task NonPositiveOverrideFieldsFallBackToGlobalOptionsIndividually()
    {
        var store = new FakeLatencySpikeSettingsStore();
        // Only the threshold is overridden (0/negative would mean "no override" per field).
        await store.UpsertAsync("cars", 100, null, null, "test-user", CancellationToken.None);
        var options = new MonitoringOptions { SpikeThresholdMs = 5000, SpikeOccurrenceThreshold = 5, SpikeWindowMinutes = 60 };
        var resolver = BuildResolver(store, storeConfigured: true, options);

        var resolved = await resolver.ResolveAsync("cars", CancellationToken.None);

        Assert.Equal(100, resolved.ThresholdMs);
        Assert.Equal(5, resolved.OccurrenceThreshold);
        Assert.Equal(60, resolved.WindowMinutes);
    }

    [Fact]
    public async Task GenuineCancellationPropagatesInsteadOfFallingBackSilently()
    {
        var store = new CancellingLatencySpikeSettingsStore();
        var resolver = BuildResolver(store, storeConfigured: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync("cars", cts.Token));
    }
}

/// <summary>Simulates a cancelled call to the settings store, e.g. a timer/request shutting down mid-query.</summary>
internal sealed class CancellingLatencySpikeSettingsStore : ILatencySpikeSettingsStore
{
    public Task<IReadOnlyList<LatencySpikeSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        throw new OperationCanceledException(cancellationToken);

    public Task UpsertAsync(string domain, int? spikeThresholdMs, int? spikeOccurrenceThreshold, int? spikeWindowMinutes, string changedBy, CancellationToken cancellationToken) =>
        throw new OperationCanceledException(cancellationToken);

    public Task<IReadOnlyList<LatencySpikeSettingAuditEntry>> GetAuditAsync(string? domain, CancellationToken cancellationToken) =>
        throw new OperationCanceledException(cancellationToken);
}
