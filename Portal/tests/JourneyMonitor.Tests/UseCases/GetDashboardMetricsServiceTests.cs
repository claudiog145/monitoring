using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.UseCases;

namespace JourneyMonitor.Tests.UseCases;

internal sealed class FakeDashboardMetricStore : IDashboardMetricStore
{
    public List<DashboardMetricPoint> Points { get; set; } = [];

    public Task ReplaceChartAsync(
        string domain, string chart, string window, string? environment, string? clubId,
        IReadOnlyList<DashboardMetricPoint> points, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(
        string? domain, string? chart, DateTimeOffset? since, string? environment, string? clubId,
        CancellationToken cancellationToken)
    {
        IEnumerable<DashboardMetricPoint> rows = Points;
        if (domain is not null) rows = rows.Where(p => p.Domain == domain);
        if (chart is not null) rows = rows.Where(p => p.Chart == chart);
        if (since is not null) rows = rows.Where(p => p.BinStart >= since);
        if (environment is not null) rows = rows.Where(p => p.Environment == environment);
        _ = clubId;
        return Task.FromResult<IReadOnlyList<DashboardMetricPoint>>(rows.ToList());
    }
}

public class GetDashboardMetricsServiceTests
{
    [Fact]
    public async Task RejectsUnknownDomain()
    {
        var service = new GetDashboardMetricsService(new FakeDashboardMetricStore());
        var (status, result) = await service.GetAsync("widgets", null, null, null, null, CancellationToken.None);
        Assert.Equal(400, status);
        Assert.Equal("error", result.Source);
    }

    [Fact]
    public async Task ReturnsStoredPoints()
    {
        var store = new FakeDashboardMetricStore
        {
            Points =
            [
                new("hotels", "step_latency_p95", "Overall", DateTimeOffset.UtcNow.AddMinutes(-5), 1200, "15m", "production", DateTimeOffset.UtcNow),
            ],
        };
        var service = new GetDashboardMetricsService(store);
        var (status, result) = await service.GetAsync("hotels", null, "1h", "production", null, CancellationToken.None);
        Assert.Equal(200, status);
        Assert.Equal("sql", result.Source);
        Assert.Equal(1, result.RowCount);
        Assert.Equal("Overall", result.Points[0].Series);
    }

    [Fact]
    public async Task ReturnsPointsInsideTheSelectedTime()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeDashboardMetricStore
        {
            Points =
            [
                new("hotels", "step_latency_p95", "Overall", now.AddMinutes(-10), 1200, "15m", "production", now),
                new("hotels", "step_latency_p95", "Overall", now.AddHours(-3), 800, "15m", "production", now),
            ],
        };
        var service = new GetDashboardMetricsService(store);

        var (hourStatus, hour) = await service.GetAsync("hotels", null, "1h", "production", null, CancellationToken.None);
        Assert.Equal(200, hourStatus);
        Assert.Equal(1, hour.RowCount);

        var (sixStatus, six) = await service.GetAsync("hotels", null, "6h", "production", null, CancellationToken.None);
        Assert.Equal(200, sixStatus);
        Assert.Equal(2, six.RowCount);
    }
}
