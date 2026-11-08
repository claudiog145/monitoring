namespace DemoGenerator.Tests;

public class GeneratorSkeletonTests(DemoAppFactory factory) : IClassFixture<DemoAppFactory>
{
    [Fact]
    public async Task Health_ReportsOkWithTheContractFields()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"kusto\":", body);
        Assert.Contains("\"feeder\":", body);
        Assert.Contains("\"lastFedAt\":", body);
    }
}
