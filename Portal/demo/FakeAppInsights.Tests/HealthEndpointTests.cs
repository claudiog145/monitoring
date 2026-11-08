using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FakeAppInsights.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReportsKustoUnreachableWhenNoEngineAnswers()
    {
        using var client = factory
            .WithWebHostBuilder(b => b.UseSetting("FakeAppInsights:KustoUrl", "http://127.0.0.1:1"))
            .CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\",\"kusto\":\"unreachable\"}", body);
    }
}
