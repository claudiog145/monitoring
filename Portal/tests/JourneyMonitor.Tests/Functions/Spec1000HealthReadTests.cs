using System.Net;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Tests.Functions;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A4;

public sealed class Spec1000HealthReadTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly A4FakeKpiDefinitionStore _definitions = new();
    private readonly A4FakeKpiBucketStore _buckets = new();
    private readonly A4FakeKpiStateStore _states = new();
    private readonly A4FakeKpiAnomalyStore _anomalies = new();

    private HealthReadFunctions Function()
    {
        var time = new A4FakeTimeProvider(Now);
        var reader = new GetPillarHealthService(_definitions, _buckets, _states, _anomalies, time);
        return new HealthReadFunctions(reader, _anomalies, _buckets, _definitions, time, NullLogger<HealthReadFunctions>.Instance);
    }

    [Fact]
    public async Task GetPillarHealth_ReturnsThreePillarsWithDisplayNames()
    {
        var response = await Function().GetPillarHealthAsync(A4Http.Request("http://localhost/api/health/pillars"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pillars = response.ReadJson().AsArray();
        Assert.Equal(
            ["Business Health", "Application Health", "Infrastructure Health"],
            pillars.Select(p => p!["displayName"]!.GetValue<string>()).ToArray());
        Assert.All(pillars, p => Assert.Equal("offline", p!["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task GetKpiBuckets_UnknownKpi_Returns404()
    {
        var response = await Function().GetBucketsAsync(A4Http.Request("http://localhost/api/health/kpis/7/buckets"), 7, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    public async Task GetKpiBuckets_HoursOutOfRange_Returns400(string hours)
    {
        _definitions.Definitions.Add(A4Http.Definition(7, KpiPillar.Application));

        var response = await Function().GetBucketsAsync(A4Http.Request($"http://localhost/api/health/kpis/7/buckets?hours={hours}"), 7, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetKpiBuckets_ReturnsBucketsInsideTheWindow()
    {
        _definitions.Definitions.Add(A4Http.Definition(7, KpiPillar.Application));
        _buckets.Buckets.Add(new KpiBucket(7, Now.AddHours(-3), 10, 10, 1, 2, 1.5, 0.1, null));
        _buckets.Buckets.Add(new KpiBucket(7, Now.AddHours(-30), 10, 10, 1, 2, 1.5, 0.1, null));

        var response = await Function().GetBucketsAsync(A4Http.Request("http://localhost/api/health/kpis/7/buckets?hours=24"), 7, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(response.ReadJson().AsArray());
    }

    [Theory]
    [InlineData("?pillar=Marketing")]
    [InlineData("?top=0")]
    [InlineData("?top=501")]
    public async Task GetKpiAnomalies_InvalidQuery_Returns400(string query)
    {
        var response = await Function().GetAnomaliesAsync(A4Http.Request($"http://localhost/api/health/anomalies{query}"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetKpiAnomalies_AcceptsDisplayNamePillar()
    {
        _anomalies.Anomalies.Add(new KpiAnomaly(1, 7, Now, Now.AddHours(-1), 900, 450, 100, "detection", null));

        var response = await Function().GetAnomaliesAsync(
            A4Http.Request("http://localhost/api/health/anomalies?pillar=Application%20Health&domain=hotels"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(response.ReadJson().AsArray());
    }
}
