using System.Net;
using Azure.Core;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Tests.Monitoring;
using JourneyMonitor.Tests.Spec1000.D1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Infrastructure;

/// <summary>Stored-token service whose token and expiry are set by the test; counts reads.</summary>
internal sealed class ScriptedStoredTokens : IAppInsightsTokenService
{
    public StoredAppInsightsToken? Stored { get; set; }
    public int Reads { get; private set; }
    public List<string> RequestedKeys { get; } = [];

    public Task<StoredAppInsightsToken?> GetStoredTokenAsync(string tenantKey)
    {
        Reads++;
        RequestedKeys.Add(tenantKey);
        return Task.FromResult(Stored);
    }

    public Task SaveTokenAsync(string tenantKey, string email, string token, string notes = null!) => Task.CompletedTask;
    public Task SaveTokenAsync(AppInsightsTokenSaveRequest request) => Task.CompletedTask;
    public Task<string> GetTokenAsync(string tenantKey) => Task.FromResult(Stored?.Token ?? string.Empty);
    public Task ValidateTokenAsync(string token) => Task.CompletedTask;
    public Task<List<AppInsightsToken>> GetAllTokensAsync() => Task.FromResult(new List<AppInsightsToken>());
    public Task DeactivateTokenAsync(string tenantKey) => Task.CompletedTask;
    public Task<bool> HasValidTokenAsync(string tenantKey) => Task.FromResult(Stored is not null);
    public Task RecordTokenUsageAsync(string tenantKey) => Task.CompletedTask;
}

internal sealed class FixedTokenCredential(string token) : TokenCredential
{
    public int Calls { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>Answers each request with the next scripted status (the last one repeats) and records the Authorization header.</summary>
internal sealed class ScriptedStatusHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
{
    public List<string?> Authorizations { get; } = [];
    public List<string?> ApiKeys { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Authorizations.Add(request.Headers.Authorization?.ToString());
        ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null);
        var status = statuses.Length == 0 ? HttpStatusCode.OK : statuses[Math.Min(Authorizations.Count - 1, statuses.Length - 1)];
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"tables":[]}""") });
    }
}

public class AppInsightsTelemetrySourceAuthTests
{
    private const string AppId = "d6751ba7-7607-4254-95db-a50f58eb14f2";
    private const string AirTenant = "b0298d22-1db0-4550-8fa6-a116a5ab4208";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly ScriptedStoredTokens _store = new();
    private readonly D1FixedTimeProvider _time = new(Now);
    private readonly FixedTokenCredential _default = new("default-token");
    private int _factoryCalls;

    private AppInsightsTelemetrySource Build(HttpMessageHandler handler, string? apiKeys = null, ITenantResolver? resolver = null, InsightsOptions? insights = null)
    {
        var options = Options.Create(insights ?? new InsightsOptions
        {
            QueryApiKeys = apiKeys,
            Air = new AirInsightsOptions { TenantId = AirTenant },
        });
        return new AppInsightsTelemetrySource(
            new HttpClient(handler), _store, options, NullLogger<AppInsightsTelemetrySource>.Instance,
            _ => { _factoryCalls++; return _default; }, _time,
            resolver ?? TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", [new TelemetryTenant("ice", AirTenant, ["air"])])));
    }

    private static TelemetryQueryRequest Query() => new("requests | take 1", AppId, AirTenant);

    [Fact]
    public async Task TelemetrySource_ApiKeyWins()
    {
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddHours(1));
        var handler = new ScriptedStatusHandler();

        await Build(handler, $"{AppId}=the-api-key").QueryAsync(Query(), CancellationToken.None);

        Assert.Equal("the-api-key", Assert.Single(handler.ApiKeys));
        Assert.Null(handler.Authorizations.Single());
        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _factoryCalls);
        Assert.Equal(0, _default.Calls);
    }

    [Fact]
    public async Task TelemetrySource_ValidStoredToken_IsUsed()
    {
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddMinutes(30));
        var handler = new ScriptedStatusHandler();

        await Build(handler).QueryAsync(Query(), CancellationToken.None);

        Assert.Equal("Bearer stored-token", Assert.Single(handler.Authorizations));
        Assert.Equal(["ice"], _store.RequestedKeys);
        Assert.Equal(0, _factoryCalls);
        Assert.Equal(0, _default.Calls);
    }

    [Fact]
    public async Task TelemetrySource_ExpiredStoredToken_FallsBackToDefaultCredential()
    {
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddMinutes(-5));
        var handler = new ScriptedStatusHandler();

        await Build(handler).QueryAsync(Query(), CancellationToken.None);

        Assert.Equal("Bearer default-token", Assert.Single(handler.Authorizations));
        Assert.Equal(1, _factoryCalls);
        Assert.Equal(1, _default.Calls);
    }

    [Fact]
    public async Task TelemetrySource_StoredTokenNotCachedPastExpiry()
    {
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddMinutes(10));
        var handler = new ScriptedStatusHandler();
        var source = Build(handler);

        await source.QueryAsync(Query(), CancellationToken.None);
        await source.QueryAsync(Query(), CancellationToken.None);
        Assert.Equal(1, _store.Reads);

        // Past ExpiresAt - 2 min the cached entry is gone: the store is read again and, with no
        // fresh token there, the default credential is used.
        _time.Now = Now.AddMinutes(8).AddSeconds(1);
        await source.QueryAsync(Query(), CancellationToken.None);

        Assert.Equal(2, _store.Reads);
        Assert.Equal(["Bearer stored-token", "Bearer stored-token", "Bearer default-token"], handler.Authorizations);
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public async Task TelemetrySource_StoredToken401_RetriesOnceWithDefault()
    {
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddMinutes(30));
        var handler = new ScriptedStatusHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var source = Build(handler);

        await source.QueryAsync(Query(), CancellationToken.None);

        Assert.Equal(["Bearer stored-token", "Bearer default-token"], handler.Authorizations);
        Assert.Equal(1, _default.Calls);

        // When the default credential is rejected too there is no further retry.
        var rejecting = new ScriptedStatusHandler(HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(rejecting).QueryAsync(Query(), CancellationToken.None));
        Assert.Contains("403", ex.Message);
        Assert.Equal(["Bearer stored-token", "Bearer default-token"], rejecting.Authorizations);
    }

    [Fact]
    public async Task TenantResolver_MapsConfiguredTenantsToTokenKeys()
    {
        const string sharedTenant = "aaaaaaaa-0000-0000-0000-000000000001";
        var resolver = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", [
            new TelemetryTenant("arrivia", sharedTenant, ["hotels", "cars"]),
            new TelemetryTenant("cruises", sharedTenant, ["cruises"]),
            new TelemetryTenant("ice", AirTenant, ["air"]),
        ]));

        Assert.Equal("arrivia", await resolver.ResolveTenantKeyAsync("AAAAAAAA-0000-0000-0000-000000000001"));
        Assert.Equal("ice", await resolver.ResolveTenantKeyAsync(AirTenant));
        Assert.Equal("cruises", await resolver.ResolveTenantKeyAsync(sharedTenant, "cruises"));
        Assert.Equal("dddddddd-0000-0000-0000-000000000004", await resolver.ResolveTenantKeyAsync("dddddddd-0000-0000-0000-000000000004"));
    }

    [Fact]
    public async Task TelemetrySource_DomainKeyWithoutToken_FallsBackToTenantKey()
    {
        const string sharedTenant = "aaaaaaaa-0000-0000-0000-000000000001";
        const string hotelsApp = "11111111-0000-0000-0000-000000000001";
        const string cruisesApp = "33333333-0000-0000-0000-000000000003";
        var resolver = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", [
            new TelemetryTenant("storefront", sharedTenant, ["hotels"]),
            new TelemetryTenant("ships", sharedTenant, ["cruises"]),
        ]));
        var insights = new InsightsOptions
        {
            Hotels = new HotelInsightsOptions { TenantId = sharedTenant, AppInsightsAppId = hotelsApp },
            Cruises = new CruiseInsightsOptions { TenantId = sharedTenant, Staging = new CruiseStagingSlot { AppInsightsAppId = cruisesApp } },
        };
        _store.Stored = null;
        var handler = new ScriptedStatusHandler();

        await Build(handler, resolver: resolver, insights: insights)
            .QueryAsync(new TelemetryQueryRequest("requests | take 1", cruisesApp, sharedTenant), CancellationToken.None);
        Assert.Equal(["ships", "storefront"], _store.RequestedKeys);

        _store.RequestedKeys.Clear();
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddMinutes(30));
        await Build(handler, resolver: resolver, insights: insights)
            .QueryAsync(new TelemetryQueryRequest("requests | take 1", hotelsApp, sharedTenant), CancellationToken.None);
        Assert.Equal(["storefront"], _store.RequestedKeys);

        _store.RequestedKeys.Clear();
        await Build(handler, resolver: resolver, insights: insights)
            .QueryAsync(new TelemetryQueryRequest("requests | take 1", cruisesApp, sharedTenant), CancellationToken.None);
        Assert.Equal(["ships"], _store.RequestedKeys);
    }

    [Fact]
    public async Task TelemetrySource_WithoutResolver_UsesTenantIdAsKey()
    {
        _store.Stored = new StoredAppInsightsToken("stored-token", Now.AddMinutes(30));
        var source = new AppInsightsTelemetrySource(
            new HttpClient(new ScriptedStatusHandler()), _store,
            Options.Create(new InsightsOptions()), NullLogger<AppInsightsTelemetrySource>.Instance);

        await source.QueryAsync(Query(), CancellationToken.None);

        Assert.Equal([AirTenant], _store.RequestedKeys);
    }
}
