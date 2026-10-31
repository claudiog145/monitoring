using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.UseCases;

/// <summary>
/// Save-time validation is where a literal resource bypassing the telemetry source catalog has to
/// be caught - see KqlSourceResolver's class comment for why it can no longer catch this itself
/// (Air/Cruise substitute a trusted ARM path before ResolveAsync ever runs).
/// </summary>
public class KqlQueryAdminServiceTests
{
    private const string RequiredForScoutDrillDown =
        " | where operation_Id in ({{OPERATION_IDS}}) | where timestamp >= {{TIME_START}} and timestamp < {{TIME_END}} | take {{MAX_ROWS}}";

    private static KqlQueryAdminService Build() =>
        new(new InMemoryKqlQueryStore(), new KqlQueryTextProvider(), new InMemoryTelemetrySourceStore());

    [Fact]
    public async Task RejectsALiteralArmResourcePath()
    {
        var service = Build();

        var error = await service.ValidateAsync(
            KqlQueryKeys.ScoutDrillDown,
            "app('/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/x/providers/microsoft.insights/components/y')" + RequiredForScoutDrillDown);

        Assert.NotNull(error);
        Assert.Contains("{{SOURCE:alias}}", error);
    }

    /// <summary>app() also accepts a bare application id - no /subscriptions/ substring, so the old
    /// check missed it entirely, and it reaches an unregistered resource just as effectively.</summary>
    [Fact]
    public async Task RejectsABareApplicationIdOperand()
    {
        var service = Build();

        var error = await service.ValidateAsync(
            KqlQueryKeys.ScoutDrillDown,
            "app('a1b2c3d4-0000-0000-0000-000000000000')" + RequiredForScoutDrillDown);

        Assert.NotNull(error);
        Assert.Contains("a1b2c3d4-0000-0000-0000-000000000000", error);
    }

    /// <summary>...and so does a bare application name.</summary>
    [Fact]
    public async Task RejectsABareApplicationNameOperand()
    {
        var service = Build();

        var error = await service.ValidateAsync(
            KqlQueryKeys.ScoutDrillDown,
            "app('SomeOtherAppInsightsResource')" + RequiredForScoutDrillDown);

        Assert.NotNull(error);
        Assert.Contains("SomeOtherAppInsightsResource", error);
    }

    [Fact]
    public async Task AcceptsARegisteredSourceAlias()
    {
        var service = Build();

        var error = await service.ValidateAsync(
            KqlQueryKeys.ScoutDrillDown,
            "app('{{SOURCE:cruise-core}}')" + RequiredForScoutDrillDown);

        Assert.Null(error);
    }
}
