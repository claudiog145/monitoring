using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// The Inspector's graph is wide (five specialists, four tools, a recorder shared by all of them),
/// and a missing registration compiles perfectly and only fails on the first timer tick in a
/// deployed environment. This resolves the whole chain the way the Functions host does.
/// </summary>
public class InspectorDependencyInjectionTests
{
    [Fact]
    public void TheWholeInspectorChainResolvesOnAFreshCloneWithNoDatabase()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<InspectorMonkeyRunner>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<InspectorMonkeyService>());
    }

    [Fact]
    public void AllFiveSpecialistsAndAllFourLogsAgentToolsAreRegistered()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var specialists = scope.ServiceProvider.GetServices<Core.Abstractions.IInspectorSpecialist>().ToList();
        var tools = scope.ServiceProvider.GetServices<Core.Abstractions.IAgentTool>().ToList();

        Assert.Equal(
            Core.Domain.InspectorAgent.All.OrderBy(a => a, StringComparer.Ordinal),
            specialists.Select(s => s.AgentRole).OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(4, tools.Count);
        // A tool belongs to exactly one agent; the specialist filters on this.
        Assert.All(tools, t => Assert.Equal(Core.Domain.InspectorAgent.Logs, t.OwnerAgent));
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // The composition root configures the real key ring (blob-backed in Azure); the settings
        // stores only need something to resolve here.
        services.AddSingleton<IDataProtectionProvider>(new PassThroughDataProtectionProvider());
        services.AddJourneyMonitorInfrastructure(configuration);
        services.AddJourneyMonitorMonitoring(configuration);
        services.AddSqlJourneyStore(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class PassThroughDataProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
