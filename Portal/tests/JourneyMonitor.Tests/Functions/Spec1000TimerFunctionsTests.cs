using System.Net;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Tests.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.A4;

public sealed class Spec1000TimerFunctionsTests
{
    private readonly A4FakeIngestionRunner _ingestion = new();
    private readonly A4FakePillarMonitor _monitor = new();

    private async Task RunAllTimersAsync(bool enabled)
    {
        var options = Options.Create(new HealthMonitoringOptions { Enabled = enabled, Pillars = ["Application", "Business", "Infrastructure"] });
        var timer = new TimerInfo();

        await new KpiIngestionFunction(_ingestion, options, NullLogger<KpiIngestionFunction>.Instance).RunAsync(timer, CancellationToken.None);
        await new ApplicationHealthMonitorFunction(_monitor, options, NullLogger<ApplicationHealthMonitorFunction>.Instance).RunAsync(timer, CancellationToken.None);
        await new BusinessHealthMonitorFunction(_monitor, options, NullLogger<BusinessHealthMonitorFunction>.Instance).RunAsync(timer, CancellationToken.None);
        await new InfrastructureHealthMonitorFunction(_monitor, options, NullLogger<InfrastructureHealthMonitorFunction>.Instance).RunAsync(timer, CancellationToken.None);
    }

    [Fact]
    public async Task TimerFunctions_DoNothingWhenHealthMonitoringDisabled()
    {
        await RunAllTimersAsync(enabled: false);

        Assert.Equal(0, _ingestion.Calls);
        Assert.Empty(_monitor.Calls);
    }

    [Fact]
    public async Task TimerFunctions_DispatchToTheirPillarWhenEnabled()
    {
        await RunAllTimersAsync(enabled: true);

        Assert.Equal(1, _ingestion.Calls);
        Assert.Equal([KpiPillar.Application, KpiPillar.Business, KpiPillar.Infrastructure], _monitor.Calls);
    }

    [Theory]
    [InlineData(typeof(KpiIngestionFunction), "KpiIngestion", "%spec1000_ingestion_cron%")]
    [InlineData(typeof(ApplicationHealthMonitorFunction), "ApplicationHealthMonitor", "%spec1000_monitor_cron%")]
    [InlineData(typeof(BusinessHealthMonitorFunction), "BusinessHealthMonitor", "%spec1000_monitor_cron%")]
    [InlineData(typeof(InfrastructureHealthMonitorFunction), "InfrastructureHealthMonitor", "%spec1000_monitor_cron%")]
    [InlineData(typeof(TokenRefreshFunction), "TokenRefresh", "%spec1000_token_refresh_cron%")]
    public void TimerFunctions_UseSpec1000CronSettings(Type functionType, string functionName, string schedule)
    {
        var method = functionType.GetMethod("RunAsync")!;
        var function = (FunctionAttribute)Attribute.GetCustomAttribute(method, typeof(FunctionAttribute))!;
        var trigger = (TimerTriggerAttribute)method.GetParameters()[0].GetCustomAttributes(typeof(TimerTriggerAttribute), false).Single();

        Assert.Equal(functionName, function.Name);
        Assert.Equal(schedule, trigger.Schedule);
    }

    [Fact]
    public void LocalSettings_DefineSpec1000CronsAndSafeFlags()
    {
        var path = FindRepoFile("Portal/src/JourneyMonitor.Functions/local.settings.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var values = doc.RootElement.GetProperty("Values");

        Assert.Equal("0 */1 * * * *", values.GetProperty("spec1000_ingestion_cron").GetString());
        Assert.Equal("30 */1 * * * *", values.GetProperty("spec1000_monitor_cron").GetString());
        Assert.Equal("0 */5 * * * *", values.GetProperty("spec1000_token_refresh_cron").GetString());
        Assert.Equal("false", values.GetProperty("HealthMonitoring__Enabled").GetString());
        Assert.Equal("Shadow", values.GetProperty("Triage__Mode").GetString());
    }

    [Theory]
    [InlineData("Portal/docker-compose.yml")]
    [InlineData("Portal/scripts/publish-func-qa.ps1")]
    [InlineData("Portal/scripts/publish-func-qa-test.ps1")]
    [InlineData(".github/workflows/deploy-qa.yml")]
    [InlineData(".github/workflows/deploy-prod.yml")]
    public void DeploymentSettings_DefineTokenRefreshCronWhereverMonitorCronIs(string relative)
    {
        var text = File.ReadAllText(FindRepoFile(relative));

        Assert.Contains("spec1000_monitor_cron", text);
        Assert.Contains("spec1000_token_refresh_cron", text);
        Assert.Contains("0 */5 * * * *", text);
    }

    [Fact]
    public async Task RunMonkeyNow_SupportsSpec1000Ids()
    {
        var function = new RunMonkeyNowFunction(
            latencyRunner: null!,
            sloRunner: null!,
            doctorRunner: null!,
            scoutRunner: null!,
            inspectorRunner: null!,
            _ingestion,
            _monitor,
            Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret }),
            NullLogger<RunMonkeyNowFunction>.Instance);

        var ids = new[] { MonkeyKey.KpiIngestion, MonkeyKey.ApplicationHealthMonitor, MonkeyKey.BusinessHealthMonitor, MonkeyKey.InfrastructureHealthMonitor };
        foreach (var id in ids)
        {
            var response = await function.RunAsync(
                A4Http.Request($"http://localhost/api/monkeys/{id}/run", internalSecret: A4Http.InternalSecret), id, CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = response.ReadJson();
            Assert.Equal(id, json["monkeyId"]!.GetValue<string>());
            Assert.NotNull(json["result"]);
        }

        Assert.Equal(1, _ingestion.Calls);
        Assert.Equal([KpiPillar.Application, KpiPillar.Business, KpiPillar.Infrastructure], _monitor.Calls);

        var unauthorized = await function.RunAsync(
            A4Http.Request("http://localhost/api/monkeys/kpi-ingestion/run"), MonkeyKey.KpiIngestion, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(1, _ingestion.Calls);

        var unknown = await function.RunAsync(
            A4Http.Request("http://localhost/api/monkeys/nope/run", internalSecret: A4Http.InternalSecret), "nope", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
