using System.Net;
using System.Reflection;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Infrastructure;

internal sealed class CapturingQueryHandler(string body = """{"tables":[]}""") : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

internal sealed class NoStoredTokens : JourneyMonitor.Core.Monitoring.IAppInsightsTokenService
{
    public Task SaveTokenAsync(string tenantKey, string email, string token, string notes = null!) => Task.CompletedTask;
    public Task SaveTokenAsync(JourneyMonitor.Core.Monitoring.AppInsightsTokenSaveRequest request) => Task.CompletedTask;
    public Task<JourneyMonitor.Core.Monitoring.StoredAppInsightsToken?> GetStoredTokenAsync(string tenantKey) =>
        Task.FromResult<JourneyMonitor.Core.Monitoring.StoredAppInsightsToken?>(null);
    public Task<string> GetTokenAsync(string tenantKey) => Task.FromResult(string.Empty);
    public Task ValidateTokenAsync(string token) => Task.CompletedTask;
    public Task<List<JourneyMonitor.Core.Monitoring.AppInsightsToken>> GetAllTokensAsync() => Task.FromResult(new List<JourneyMonitor.Core.Monitoring.AppInsightsToken>());
    public Task DeactivateTokenAsync(string tenantKey) => Task.CompletedTask;
    public Task<bool> HasValidTokenAsync(string tenantKey) => Task.FromResult(false);
    public Task RecordTokenUsageAsync(string tenantKey) => Task.CompletedTask;
}

/// <summary>
/// Covers the opt-in <see cref="InsightsOptions.QueryApiKeys"/> path.
///
/// The fall-through cases assert on the parser rather than on QueryAsync: with no key configured
/// the source asks Azure.Identity for a token, which succeeds on a developer machine with
/// `az login` and fails on CI - an end-to-end assertion there would be machine-dependent. The
/// parser holds the whole decision, so testing it directly is deterministic and more precise.
/// </summary>
public class AppInsightsTelemetrySourceApiKeyTests
{
    private const string AppId = "d6751ba7-7607-4254-95db-a50f58eb14f2";

    private static (AppInsightsTelemetrySource Source, CapturingQueryHandler Handler) Build(string? queryApiKeys)
    {
        var handler = new CapturingQueryHandler();
        var options = Options.Create(new InsightsOptions { QueryApiKeys = queryApiKeys });
        return (new AppInsightsTelemetrySource(new HttpClient(handler), new NoStoredTokens(), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AppInsightsTelemetrySource>.Instance), handler);
    }

    private static IReadOnlyDictionary<string, string> Parse(string? configured)
    {
        var method = typeof(AppInsightsTelemetrySource)
            .GetMethod("ParseApiKeys", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (IReadOnlyDictionary<string, string>)method!.Invoke(null, [configured])!;
    }

    [Fact]
    public async Task ConfiguredKeyIsSentAsApiKeyHeaderAndNoBearerTokenIsAttached()
    {
        var (source, handler) = Build($"{AppId}=super-secret-key");

        await source.QueryAsync(new TelemetryQueryRequest("requests | take 1", AppId, TenantId: null), CancellationToken.None);

        Assert.Equal("super-secret-key", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Api-Key")));
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Fact]
    public async Task MatchingIsCaseInsensitiveAndIgnoresKeysForOtherAppIds()
    {
        var (source, handler) = Build($"11111111-1111-1111-1111-111111111111=first; {AppId.ToUpperInvariant()}=second ");

        await source.QueryAsync(new TelemetryQueryRequest("requests | take 1", AppId, TenantId: null), CancellationToken.None);

        Assert.Equal("second", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public void KeyConfiguredForAnotherAppIdIsNotOfferedForThisOne()
    {
        Assert.False(Parse("11111111-1111-1111-1111-111111111111=other-app-key").ContainsKey(AppId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-equals-sign")]
    [InlineData("=keyWithoutAppId")]
    [InlineData("appIdWithoutKey=")]
    public void MalformedOrEmptyConfigurationYieldsNoKeysInsteadOfFailing(string? configured)
    {
        Assert.Empty(Parse(configured));
    }

    [Fact]
    public void PairsMayBeSeparatedBySemicolonsOrNewlinesAndAreTrimmed()
    {
        var parsed = Parse($"  {AppId} = first-key \n 11111111-1111-1111-1111-111111111111=second-key ;");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("first-key", parsed[AppId]);
        Assert.Equal("second-key", parsed["11111111-1111-1111-1111-111111111111"]);
    }
}
