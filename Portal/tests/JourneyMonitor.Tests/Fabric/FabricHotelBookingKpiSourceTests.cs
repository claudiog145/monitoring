using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Fabric;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Tests.Fabric;

public class FabricHotelBookingKpiSourceTests
{
    [Fact]
    public void AppConfigurationKeysMatchTheFabricSecretNames()
    {
        Assert.Equal("platform_reliability_fabric_connectionString", FabricWarehouseOptions.ConnectionStringKey);
        Assert.Equal("platform_reliability_fabric_clientId", FabricWarehouseOptions.ClientIdKey);
        Assert.Equal("platform_reliability_fabric_clientSecret", FabricWarehouseOptions.ClientSecretKey);
        Assert.Equal("platform_reliability_fabric_tenantId", FabricWarehouseOptions.TenantIdKey);
    }

    [Fact]
    public async Task LogsTheAppConfigurationReadAndSkipsTheViewWhenASettingIsMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [FabricWarehouseOptions.ConnectionStringKey] = "Server=tcp:warehouse.example,1433;",
                [FabricWarehouseOptions.ClientIdKey] = "client-id",
                [FabricWarehouseOptions.ClientSecretKey] = "",
                [FabricWarehouseOptions.TenantIdKey] = "tenant-id",
            })
            .Build();
        var logger = new ListLogger();
        var source = new FabricHotelBookingKpiSource(configuration, logger);

        var rows = await source.ReadAlertStatusAsync(CancellationToken.None);

        Assert.Empty(rows);
        Assert.Contains(logger.Messages, message => message.Contains("Read Fabric warehouse settings from App Configuration", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("platform_reliability_fabric_clientSecret=missing", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("App Configuration key platform_reliability_fabric_clientSecret has no value.", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("Querying", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("client-id", StringComparison.Ordinal));
    }

    private sealed class ListLogger : ILogger<FabricHotelBookingKpiSource>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
