using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A3;

public class TriageInvestigationStoreTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InMemoryTriageInvestigationStore_FindOpenByDedupKeyIgnoresCompleted()
    {
        var store = new InMemoryTriageInvestigationStore();
        var signal = A3Kpi.Signal("Latency", DateTimeOffset.UtcNow);
        var completedId = Guid.NewGuid();
        await store.CreateAsync(A3Kpi.Investigation(completedId, "a3test:dedup", signal));
        await store.CompleteAsync(completedId, A3Kpi.Result(completedId, signal), notified: true);

        Assert.Null(await store.FindOpenByDedupKeyAsync("a3test:dedup"));

        var queuedId = Guid.NewGuid();
        await store.CreateAsync(A3Kpi.Investigation(queuedId, "a3test:dedup", signal));
        var open = await store.FindOpenByDedupKeyAsync("a3test:dedup");
        Assert.NotNull(open);
        Assert.Equal(queuedId, open.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Queued, open.Status);

        // The filtered unique index allows only one open investigation per dedup key.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(A3Kpi.Investigation(Guid.NewGuid(), "a3test:dedup", signal)));

        await store.UpdateStatusAsync(queuedId, TriageInvestigationStatus.Running, null);
        Assert.Equal(TriageInvestigationStatus.Running, (await store.FindOpenByDedupKeyAsync("a3test:dedup"))!.Status);

        await store.UpdateStatusAsync(queuedId, TriageInvestigationStatus.Failed, "boom");
        Assert.Null(await store.FindOpenByDedupKeyAsync("a3test:dedup"));
        var failed = (await store.GetAsync(queuedId))!;
        Assert.Equal("boom", failed.Error);
        Assert.NotNull(failed.CompletedAt);

        var completed = (await store.GetAsync(completedId))!;
        Assert.Equal(TriageAlertLevels.Amber, completed.AlertLevel);
        Assert.True(completed.Notified);
        Assert.Equal(2, (await store.GetRecentAsync(10)).Count);
    }

    [Fact]
    public async Task SqlTriageInvestigationStore_RoundTrip()
    {
        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        var store = new SqlTriageInvestigationStore(A3SqlTestDatabase.Options(connectionString));
        var prefix = "a3test:" + Guid.NewGuid().ToString("N");
        var dedupKey = prefix + ":latency";
        var id = Guid.NewGuid();
        var signal = A3Kpi.Signal("A3TEST-Latency", new DateTimeOffset(2026, 11, 1, 10, 15, 0, TimeSpan.Zero));
        try
        {
            await store.CreateAsync(A3Kpi.Investigation(id, dedupKey, signal));

            var created = await store.GetAsync(id);
            Assert.NotNull(created);
            Assert.Equal(TriageInvestigationStatus.Queued, created.Status);
            Assert.Equal(signal, created.Signal);
            Assert.Equal(dedupKey, created.DedupKey);
            Assert.Null(created.Result);
            Assert.Null(created.CompletedAt);

            var open = await store.FindOpenByDedupKeyAsync(dedupKey);
            Assert.NotNull(open);
            Assert.Equal(id, open.InvestigationId);

            await store.UpdateStatusAsync(id, TriageInvestigationStatus.Running, null);
            var running = await store.FindOpenByDedupKeyAsync(dedupKey);
            Assert.NotNull(running);
            Assert.Equal(TriageInvestigationStatus.Running, running.Status);
            Assert.Null(running.CompletedAt);

            var result = A3Kpi.Result(id, signal);
            await store.CompleteAsync(id, result, notified: true);

            Assert.Null(await store.FindOpenByDedupKeyAsync(dedupKey));
            var completed = await store.GetAsync(id);
            Assert.NotNull(completed);
            Assert.Equal(TriageInvestigationStatus.Completed, completed.Status);
            Assert.Equal(TriageAlertLevels.Amber, completed.AlertLevel);
            Assert.True(completed.Notified);
            Assert.NotNull(completed.CompletedAt);
            Assert.NotNull(completed.Result);
            Assert.Equal(
                JsonSerializer.Serialize(result, TriageJson.Options),
                JsonSerializer.Serialize(completed.Result, TriageJson.Options));

            Assert.Contains(await store.GetRecentAsync(1000), i => i.InvestigationId == id);
            output.WriteLine($"Triage round trip verified for {id}.");
        }
        finally
        {
            await A3SqlTestDatabase.DeleteTriageDataAsync(connectionString, prefix);
        }
    }
}
