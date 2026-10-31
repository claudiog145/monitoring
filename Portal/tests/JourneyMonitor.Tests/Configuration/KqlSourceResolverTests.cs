using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Configuration;

/// <summary>
/// The telemetry source catalog and the {{SOURCE:alias}} substitution that replaced 60 hardcoded
/// Application Insights resource ids.
/// </summary>
public class KqlSourceResolverTests
{
    private static KqlSourceResolver Build(ITelemetrySourceStore? store = null, bool storeConfigured = false) =>
        new(store ?? new InMemoryTelemetrySourceStore(),
            Options.Create(storeConfigured
                ? new JourneyStoreOptions { ConnectionString = "Server=unused;Database=unused;" }
                : new JourneyStoreOptions()),
            NullLogger<KqlSourceResolver>.Instance);

    // -----------------------------------------------------------------------------------------
    // The guardrail: no subscription id may come back into the embedded query text
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Every embedded KQL file, checked as a set. This is the regression this whole change exists
    /// to prevent: a resource id pasted back into a query is invisible until someone switches
    /// subscription and a board silently returns nothing, which looks exactly like "no problems".
    /// </summary>
    [Fact]
    public void NoEmbeddedQueryContainsALiteralSubscriptionId()
    {
        var provider = new KqlQueryTextProvider();
        var every = new[]
        {
            provider.GetJourneyKql("hotels"),
            provider.GetJourneyKql("cars"),
            provider.GetJourneyKql("cruises"),
            provider.GetAirJourneyKql(),
            provider.GetIncidentExceptionsKql(),
            provider.GetScoutDrillDownKql(),
            provider.GetScoutBoardKql("latency"),
            provider.GetScoutBoardKql("exception"),
            provider.GetInspectorLogsKql(InspectorLogsQuery.ClubBaseline),
            provider.GetInspectorLogsKql(InspectorLogsQuery.SignatureBreakdown),
            provider.GetInspectorLogsKql(InspectorLogsQuery.ClientBreakdown),
            provider.GetLatencySpikesKql("hotels"),
            provider.GetLatencySpikesKql("cars"),
            provider.GetLatencySpikesKql("cruises"),
            provider.GetDashboardChartKql("hotels", "step_latency_p95"),
            provider.GetDashboardChartKql("hotels", "exceptions_by_step"),
            provider.GetDashboardChartKql("cruises", "step_latency_p95"),
            provider.GetDashboardChartKql("cruises", "exceptions_by_step"),
        };

        Assert.All(every, kql => Assert.DoesNotContain("/subscriptions/", kql));
    }

    // -----------------------------------------------------------------------------------------
    // Substitution
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SubstitutesAnAliasWithTheResourceIdRegisteredForThatEnvironment()
    {
        var resolver = Build();

        var production = await resolver.ResolveAsync("app('{{SOURCE:cruise-core}}')", "production");
        var staging = await resolver.ResolveAsync("app('{{SOURCE:cruise-core}}')", "staging");

        Assert.Contains("cruise-core-ovs-ai-prod", production);
        Assert.Contains("cruise-core-ovs-ai-uat", staging);
        Assert.DoesNotContain("{{SOURCE:", production);
    }

    [Fact]
    public async Task SubstitutesEveryOccurrenceOfEveryAliasInOnePass()
    {
        var resolver = Build();

        var resolved = await resolver.ResolveAsync(
            "union (app('{{SOURCE:sor-frontend}}').requests), (app('{{SOURCE:cruise-core}}').requests), " +
            "(app('{{SOURCE:cruise-api}}').requests), (app('{{SOURCE:sor-frontend}}').exceptions)",
            "production");

        Assert.DoesNotContain("{{", resolved);
        Assert.Equal(2, resolved.Split("sor-frontend-ai-prod-usw").Length - 1);
    }

    [Fact]
    public async Task LeavesTextWithoutPlaceholdersUntouched()
    {
        var resolver = Build();
        const string kql = "requests | where timestamp > ago(1h) | count";

        Assert.Equal(kql, await resolver.ResolveAsync(kql, "production"));
    }

    // -----------------------------------------------------------------------------------------
    // Failing loudly
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// An unknown alias must not degrade into an empty result. A board that returns nothing is
    /// indistinguishable from a healthy one, so this is the one resolver in Monitoring that throws.
    /// </summary>
    [Fact]
    public async Task ThrowsOnAnUnknownAliasInsteadOfQueryingNothing()
    {
        var resolver = Build();

        var ex = await Assert.ThrowsAsync<KqlSourceResolutionException>(
            () => resolver.ResolveAsync("app('{{SOURCE:does-not-exist}}')", "production"));

        Assert.Contains("does-not-exist", ex.Message);
        // The message lists what IS registered, so the fix does not need a database round trip.
        Assert.Contains("cruise-core", ex.Message);
    }

    [Fact]
    public async Task ThrowsWhenTheAliasExistsButNotForThatEnvironment()
    {
        var resolver = Build();

        // sor-frontend has no UAT twin registered.
        await Assert.ThrowsAsync<KqlSourceResolutionException>(
            () => resolver.ResolveAsync("app('{{SOURCE:sor-frontend}}')", "staging"));
    }

    [Fact]
    public async Task ThrowsOnADisabledSource()
    {
        var store = new InMemoryTelemetrySourceStore();
        await store.UpsertAsync(
            new TelemetrySourceEntry("cruise-core", "production", TelemetrySourceKind.AppInsights,
                TelemetrySourceDefaults.All.First(s => s.Alias == "cruise-core" && s.Environment == "production").ResourceId,
                Enabled: false),
            "test");

        var ex = await Assert.ThrowsAsync<KqlSourceResolutionException>(
            () => Build(store, storeConfigured: true).ResolveAsync("app('{{SOURCE:cruise-core}}')", "production"));

        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public async Task ThrowsWhenARegisteredSourceHoldsSomethingThatIsNotAnArmResourceId()
    {
        var store = new InMemoryTelemetrySourceStore();
        await store.UpsertAsync(
            new TelemetrySourceEntry("cruise-core", "production", TelemetrySourceKind.AppInsights, "not-a-resource-id"),
            "test");

        await Assert.ThrowsAsync<KqlSourceResolutionException>(
            () => Build(store, storeConfigured: true).ResolveAsync("app('{{SOURCE:cruise-core}}')", "production"));
    }

    // -----------------------------------------------------------------------------------------
    // Catalog + audit
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheInMemoryCatalogStartsSeededRatherThanEmpty()
    {
        // Deliberately unlike InMemoryServiceOwnerStore: an empty source catalog is not an honest
        // "unknown", it just means nothing resolves.
        var sources = new InMemoryTelemetrySourceStore().GetAllAsync().Result;

        Assert.Equal(TelemetrySourceDefaults.All.Count, sources.Count);
        Assert.All(sources, s => Assert.True(InsightsSourceResolver.IsInsightsResourceId(s.ResourceId)));
    }

    [Fact]
    public async Task EveryChangeIsRecordedWithWhoMadeIt()
    {
        var store = new InMemoryTelemetrySourceStore();

        await store.UpsertAsync(
            new TelemetrySourceEntry("cruise-core", "production", TelemetrySourceKind.AppInsights,
                "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/microsoft.insights/components/new-one"),
            "claudio");
        await store.DeleteAsync("cruise-api", "production", "claudio");

        var audit = await store.GetAuditAsync(10);

        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Equal("claudio", a.ChangedBy));
        Assert.True(audit[0].Removed);
        Assert.False(audit[1].Removed);
        Assert.Contains("new-one", audit[1].ResourceId);
    }

    /// <summary>The defaults in code and the SQL seed must not drift - both are the same contract.</summary>
    [Fact]
    public void TheCodeDefaultsCoverEveryAliasTheEmbeddedQueriesReferTo()
    {
        var provider = new KqlQueryTextProvider();
        var aliases = new[]
        {
            provider.GetAirJourneyKql(),
            provider.GetJourneyKql("cruises"),
            provider.GetScoutBoardKql("latency"),
            provider.GetScoutBoardKql("exception"),
            provider.GetScoutDrillDownKql(),
            provider.GetInspectorLogsKql(InspectorLogsQuery.ClubBaseline),
            provider.GetInspectorLogsKql(InspectorLogsQuery.SignatureBreakdown),
            provider.GetInspectorLogsKql(InspectorLogsQuery.ClientBreakdown),
            provider.GetDashboardChartKql("cruises", "step_latency_p95"),
            provider.GetDashboardChartKql("cruises", "exceptions_by_step"),
        }
        .SelectMany(kql => KqlSourceResolver.PlaceholderPattern.Matches(kql).Select(m => m.Groups["alias"].Value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        Assert.NotEmpty(aliases);
        Assert.All(aliases, alias =>
            Assert.Contains(TelemetrySourceDefaults.All, s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)));
    }
}
