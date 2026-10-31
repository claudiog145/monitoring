using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.UseCases;

internal sealed class FakeJourneySnapshotStore : IJourneySnapshotStore
{
    public JourneySnapshot Snapshot { get; set; } = new(
        "hotels", "2h", "production", "15639", "stays.tmobiletravel.com",
        DateTimeOffset.Parse("2026-09-17T18:00:00Z"), []);

    public Exception? Error { get; set; }
    public JourneySnapshotQuery? LastQuery { get; private set; }

    public Task<JourneySnapshot> GetLatestAsync(JourneySnapshotQuery query, CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (Error is not null) throw Error;
        return Task.FromResult(Snapshot);
    }
}

public class GetDbJourneysServiceTests
{
    [Fact]
    public async Task RejectsUnknownDomain()
    {
        var service = Service(new FakeJourneySnapshotStore());
        var outcome = await service.GetAsync(
            new DbJourneysRequest("widgets", "15639", null, null, null), CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal("error", outcome.Result.Source);
        Assert.Contains("domain", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsNonNumericClub()
    {
        var service = Service(new FakeJourneySnapshotStore());
        var outcome = await service.GetAsync(
            new DbJourneysRequest("hotels", "tmobile", null, null, null), CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Contains("club", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsSqlEnvelopeWithRawRows()
    {
        var row = new TelemetryJourneyRow();
        row.Extra["UserId"] = System.Text.Json.JsonSerializer.SerializeToElement("14001001");
        row.Extra["CorrelationId"] = System.Text.Json.JsonSerializer.SerializeToElement("8a1c2d3e-4f56-4789-a012-111111111001");
        row.Extra["Outcome"] = System.Text.Json.JsonSerializer.SerializeToElement("Booked");

        var store = new FakeJourneySnapshotStore
        {
            Snapshot = new JourneySnapshot(
                "hotels", "2h", "production", "15639", "stays.tmobiletravel.com",
                DateTimeOffset.Parse("2026-09-17T18:00:00Z"), [row]),
        };
        var service = Service(store);

        var outcome = await service.GetAsync(
            new DbJourneysRequest("hotels", "15639", "all", "111111111001", "50"),
            CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("sql", outcome.Result.Source);
        Assert.Equal("hotels", outcome.Result.Domain);
        Assert.Equal("15639", outcome.Result.ClubId);
        Assert.Equal("stays.tmobiletravel.com", outcome.Result.Host);
        Assert.Equal(1, outcome.Result.RowCount);
        Assert.Equal("14001001", outcome.Result.Rows[0].GetString("UserId"));
        Assert.Equal("111111111001", store.LastQuery!.SearchId);
        Assert.Null(store.LastQuery.Status);
        Assert.Equal(50, store.LastQuery.RowCap);
    }

    [Fact]
    public async Task RejectsInvalidWindow()
    {
        var service = Service(new FakeJourneySnapshotStore());
        var outcome = await service.GetAsync(
            new DbJourneysRequest("hotels", "15639", null, null, "100", "yesterday"),
            CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Contains("win", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AsksTheStoreForEveryJourneyLastSeenInsideTheWindow()
    {
        var store = new FakeJourneySnapshotStore();
        var service = Service(store);
        var before = DateTimeOffset.UtcNow;

        await service.GetAsync(
            new DbJourneysRequest("hotels", "15827", null, null, "200", "1h"),
            CancellationToken.None);

        var since = store.LastQuery!.Since;
        Assert.NotNull(since);
        Assert.InRange(since.Value, before.AddHours(-1).AddSeconds(-1), DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(1));
    }

    [Fact]
    public async Task FiltersRowsOlderThanRequestedWindow()
    {
        var inside = RowWithLastSeen("inside", DateTimeOffset.UtcNow.AddMinutes(-5));
        var outside = RowWithLastSeen("outside", DateTimeOffset.UtcNow.AddMinutes(-40));
        var store = new FakeJourneySnapshotStore
        {
            Snapshot = new JourneySnapshot(
                "hotels", "2h", "production", "15639", "stays.tmobiletravel.com",
                DateTimeOffset.UtcNow, [inside, outside]),
        };
        var service = Service(store);

        var outcome = await service.GetAsync(
            new DbJourneysRequest("hotels", "15639", null, null, "100", "15m"),
            CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("15m", outcome.Result.Win);
        Assert.Equal(1, outcome.Result.RowCount);
        Assert.Equal("inside", outcome.Result.Rows[0].GetString("UserId"));
    }

    [Fact]
    public async Task MissingStoreConfigurationReturns502()
    {
        var store = new FakeJourneySnapshotStore
        {
            Error = new InvalidOperationException("JourneyStore:ConnectionString is not configured."),
        };
        var service = Service(store);

        var outcome = await service.GetAsync(
            new DbJourneysRequest("hotels", null, null, null, null), CancellationToken.None);

        Assert.Equal(502, outcome.StatusCode);
        Assert.Contains("JourneyStore", outcome.Result.Error);
    }

    private static GetDbJourneysService Service(IJourneySnapshotStore store) =>
        new(store, Options.Create(new JourneySyncOptions()));

    private static TelemetryJourneyRow RowWithLastSeen(string userId, DateTimeOffset lastSeen)
    {
        var row = new TelemetryJourneyRow();
        row.Extra["UserId"] = System.Text.Json.JsonSerializer.SerializeToElement(userId);
        row.Extra["LastSeen"] = System.Text.Json.JsonSerializer.SerializeToElement(lastSeen.ToString("O"));
        return row;
    }
}
