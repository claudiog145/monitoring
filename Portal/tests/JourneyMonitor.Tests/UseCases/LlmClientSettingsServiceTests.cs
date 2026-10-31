using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.UseCases;

/// <summary>Mirrors SqlLlmClientSettingsStore.OpenConnection: throws on every call, standing in for "not configured".</summary>
internal sealed class ThrowingLlmClientSettingsStore : ILlmClientSettingsStore
{
    public Task<LlmClientSetting?> GetAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");

    public Task UpsertAsync(LlmClientSettingUpdate update, string changedBy, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");

    public Task<IReadOnlyList<LlmClientSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");
}

public class LlmClientSettingsServiceTests
{
    private static LlmClientSettingsService BuildService(
        FakeLlmClientSettingsStore store,
        AiOptions? aiOptions = null,
        FoundryOptions? foundryOptions = null)
    {
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = "Server=fake;" });
        var resolver = new LlmClientSettingsResolver(
            store,
            storeOptions,
            Options.Create(aiOptions ?? new AiOptions()),
            Options.Create(foundryOptions ?? new FoundryOptions()),
            NullLogger<LlmClientSettingsResolver>.Instance);
        return new LlmClientSettingsService(store, resolver, storeOptions);
    }

    /// <summary>A service whose store throws on every call, with SQL either configured or not.</summary>
    private static LlmClientSettingsService BuildThrowingService(bool sqlConfigured, AiOptions? aiOptions = null)
    {
        var storeOptions = Options.Create(new JourneyStoreOptions { ConnectionString = sqlConfigured ? "Server=fake;" : null });
        var resolver = new LlmClientSettingsResolver(
            new ThrowingLlmClientSettingsStore(),
            storeOptions,
            Options.Create(aiOptions ?? new AiOptions()),
            Options.Create(new FoundryOptions()),
            NullLogger<LlmClientSettingsResolver>.Instance);
        return new LlmClientSettingsService(new ThrowingLlmClientSettingsStore(), resolver, storeOptions);
    }

    [Fact]
    public async Task GetAsyncReportsNoTokenWhenNothingIsConfiguredAnywhere()
    {
        var service = BuildService(new FakeLlmClientSettingsStore());

        var view = await service.GetAsync(CancellationToken.None);

        Assert.False(view.HasToken);
        Assert.Null(view.MaskedToken);
        Assert.Equal(AiOptions.DefaultModel, view.EffectiveModel);
    }

    [Fact]
    public async Task GetAsyncNeverReturnsTheRealTokenOnlyAMaskedHint()
    {
        var store = new FakeLlmClientSettingsStore();
        await store.UpsertAsync(new LlmClientSettingUpdate { GitHubToken = "gho_abcdefghijklmnopqrstuvwxyz1234" }, "tester", CancellationToken.None);
        var service = BuildService(store);

        var view = await service.GetAsync(CancellationToken.None);

        Assert.True(view.HasToken);
        Assert.Equal("****1234", view.MaskedToken);
        Assert.DoesNotContain("gho_abcdefghijklmnopqrstuvwxyz1234", view.MaskedToken);
    }

    [Fact]
    public async Task GetAsyncNeverReturnsTheRealFoundryApiKeyOnlyAMaskedHint()
    {
        var store = new FakeLlmClientSettingsStore();
        await store.UpsertAsync(new LlmClientSettingUpdate { FoundryApiKey = "fdy_superSecretKeyValue9876" }, "tester", CancellationToken.None);
        var service = BuildService(store);

        var view = await service.GetAsync(CancellationToken.None);

        Assert.True(view.HasFoundryApiKey);
        Assert.Equal("****9876", view.MaskedFoundryApiKey);
        Assert.DoesNotContain("superSecret", view.MaskedFoundryApiKey);
    }

    [Fact]
    public async Task GetAsyncReturnsEnvOnlyEffectiveSettingsWithoutTouchingTheStoreWhenSqlIsNotConfigured()
    {
        var service = BuildThrowingService(sqlConfigured: false, new AiOptions { GitHubToken = "gho_envToken" });

        // ThrowingLlmClientSettingsStore would throw on any call - reaching this assertion at all
        // proves GetAsync skipped the store entirely when JourneyStore isn't configured.
        var view = await service.GetAsync(CancellationToken.None);

        Assert.True(view.HasToken);
        Assert.Null(view.UpdatedAt);
    }

    [Fact]
    public async Task GetAuditAsyncReturnsEmptyWithoutTouchingTheStoreWhenSqlIsNotConfigured()
    {
        var service = BuildThrowingService(sqlConfigured: false);

        var audit = await service.GetAuditAsync(CancellationToken.None);

        Assert.Empty(audit);
    }

    [Fact]
    public async Task GetAsyncFallsBackToEnvOnlyEffectiveSettingsWhenTheStoreThrowsEvenThoughConfigured()
    {
        // Reproduces the "JourneyStore:ConnectionString is set but monitoring.LlmClientSetting
        // was never provisioned on that database" gap (e.g. a fresh QA deployment) - a plain read
        // must degrade, not 500.
        var service = BuildThrowingService(sqlConfigured: true, new AiOptions { GitHubToken = "gho_envToken" });

        var view = await service.GetAsync(CancellationToken.None);

        Assert.True(view.HasToken);
        Assert.Null(view.UpdatedAt);
    }

    [Fact]
    public async Task GetAuditAsyncReturnsEmptyWhenTheStoreThrowsEvenThoughConfigured()
    {
        var service = BuildThrowingService(sqlConfigured: true);

        var audit = await service.GetAuditAsync(CancellationToken.None);

        Assert.Empty(audit);
    }

    [Fact]
    public async Task UpsertAsyncStillThrowsWhenTheStoreThrows()
    {
        // Unlike the reads above, a write must never silently "succeed" while not actually
        // persisting - the user would believe their credential was saved when it wasn't.
        var service = BuildThrowingService(sqlConfigured: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpsertAsync(new LlmClientSettingsInput { GitHubToken = "gho_someToken1234" }, "tester", CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsyncRejectsATokenThatDoesNotStartWithGho()
    {
        var service = BuildService(new FakeLlmClientSettingsStore());

        var (statusCode, _) = await service.UpsertAsync(
            new LlmClientSettingsInput { GitHubToken = "ghp_notAnOAuthToken" }, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnUnknownProvider()
    {
        var service = BuildService(new FakeLlmClientSettingsStore());

        var (statusCode, _) = await service.UpsertAsync(
            new LlmClientSettingsInput { Provider = "openai-direct" }, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsAnUnknownAgentProviderOverride()
    {
        var service = BuildService(new FakeLlmClientSettingsStore());

        var (statusCode, _) = await service.UpsertAsync(
            new LlmClientSettingsInput { InspectorProvider = "bedrock" }, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncRejectsANonHttpsFoundryEndpoint()
    {
        var service = BuildService(new FakeLlmClientSettingsStore());

        var (statusCode, _) = await service.UpsertAsync(
            new LlmClientSettingsInput { FoundryEndpoint = "http://my-resource.openai.azure.com" }, "tester", CancellationToken.None);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task UpsertAsyncAcceptsFoundrySelectedBeforeItsEndpointIsFilledIn()
    {
        // The form is filled one field at a time; incomplete configuration is reported as a warning
        // by GetAsync, not rejected here - otherwise it could never be saved in the first place.
        var service = BuildService(new FakeLlmClientSettingsStore());

        var (statusCode, result) = await service.UpsertAsync(
            new LlmClientSettingsInput { Provider = LlmProviders.Foundry }, "claudio", CancellationToken.None);

        Assert.Equal(200, statusCode);
        var view = Assert.IsType<LlmClientSettingView>(result);
        Assert.Equal(LlmProviders.Foundry, view.Provider);
        Assert.Contains(view.Warnings, w => w.Contains("no endpoint is configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnAgentOverrideIsReportedSeparatelyFromTheGlobalProvider()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);

        await service.UpsertAsync(
            new LlmClientSettingsInput { Provider = LlmProviders.GitHubCopilot, ScoutProvider = LlmProviders.Foundry },
            "claudio",
            CancellationToken.None);

        var view = await service.GetAsync(CancellationToken.None);

        // The Scout is pinned; the other two inherit - and "inherits" must stay distinguishable
        // from "pinned to the same value", because they diverge the next time the global changes.
        Assert.Equal(LlmProviders.Foundry, view.EffectiveScoutProvider);
        Assert.Equal(LlmProviders.GitHubCopilot, view.EffectiveDoctorProvider);
        Assert.Null(view.DoctorProvider);
        Assert.Equal(LlmProviders.Foundry, view.ScoutProvider);
    }

    [Fact]
    public async Task AnInspectorOnFoundryIsWarnedThatItsRosterMustNameDeployments()
    {
        var service = BuildService(
            new FakeLlmClientSettingsStore(),
            foundryOptions: new FoundryOptions
            {
                Endpoint = "https://fake.openai.azure.com",
                ApiKey = "key",
                ChatDeployment = "gpt-4o-deployment",
            });

        await service.UpsertAsync(new LlmClientSettingsInput { InspectorProvider = LlmProviders.Foundry }, "claudio", CancellationToken.None);
        var view = await service.GetAsync(CancellationToken.None);

        Assert.Contains(view.Warnings, w => w.Contains("roster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnAgentOnGitHubCopilotIsWarnedThatTheCredentialIsPersonal()
    {
        var service = BuildService(new FakeLlmClientSettingsStore(), new AiOptions { GitHubToken = "gho_personalToken" });

        var view = await service.GetAsync(CancellationToken.None);

        Assert.Contains(view.Warnings, w => w.Contains("personal GitHub Copilot credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpsertAsyncPersistsANewTokenAndRecordsWhoChangedIt()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);

        var (statusCode, result) = await service.UpsertAsync(
            new LlmClientSettingsInput { GitHubToken = "gho_brandNewToken1234", Model = "gpt-4o" }, "claudio", CancellationToken.None);

        Assert.Equal(200, statusCode);
        var view = Assert.IsType<LlmClientSettingView>(result);
        Assert.True(view.HasToken);
        Assert.Equal("claudio", view.UpdatedBy);

        var audit = await service.GetAuditAsync(CancellationToken.None);
        var entry = Assert.Single(audit);
        Assert.True(entry.TokenChanged);
        Assert.Equal("claudio", entry.ChangedBy);
    }

    [Fact]
    public async Task UpsertAsyncOnlyChangingTheModelDoesNotTouchTheStoredToken()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);
        await service.UpsertAsync(
            new LlmClientSettingsInput { GitHubToken = "gho_originalToken1234", Model = "gpt-4o-mini" }, "claudio", CancellationToken.None);

        var (statusCode, result) = await service.UpsertAsync(
            new LlmClientSettingsInput { Model = "gpt-4o" }, "claudio", CancellationToken.None);

        Assert.Equal(200, statusCode);
        var view = Assert.IsType<LlmClientSettingView>(result);
        Assert.True(view.HasToken);
        Assert.Equal("****1234", view.MaskedToken);

        var audit = await service.GetAuditAsync(CancellationToken.None);
        Assert.False(audit.First().TokenChanged);
    }

    [Fact]
    public async Task ChangingTheProviderDoesNotTouchEitherStoredCredential()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);
        await service.UpsertAsync(
            new LlmClientSettingsInput { GitHubToken = "gho_originalToken1234", FoundryApiKey = "foundryKey5678" },
            "claudio",
            CancellationToken.None);

        await service.UpsertAsync(new LlmClientSettingsInput { Provider = LlmProviders.Foundry }, "claudio", CancellationToken.None);
        var view = await service.GetAsync(CancellationToken.None);

        Assert.True(view.HasToken);
        Assert.Equal("****1234", view.MaskedToken);
        Assert.True(view.HasFoundryApiKey);
        Assert.Equal("****5678", view.MaskedFoundryApiKey);
    }

    [Fact]
    public async Task UpsertAsyncClearTokenExplicitlyRemovesIt()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);
        await service.UpsertAsync(new LlmClientSettingsInput { GitHubToken = "gho_originalToken1234" }, "claudio", CancellationToken.None);

        var (statusCode, result) = await service.UpsertAsync(
            new LlmClientSettingsInput { ClearToken = true }, "claudio", CancellationToken.None);

        Assert.Equal(200, statusCode);
        var view = Assert.IsType<LlmClientSettingView>(result);
        Assert.False(view.HasToken);
        Assert.Null(view.MaskedToken);
    }

    [Fact]
    public async Task UpsertAsyncClearFoundryApiKeyExplicitlyRemovesIt()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);
        await service.UpsertAsync(new LlmClientSettingsInput { FoundryApiKey = "foundryKey5678" }, "claudio", CancellationToken.None);

        var (statusCode, result) = await service.UpsertAsync(
            new LlmClientSettingsInput { ClearFoundryApiKey = true }, "claudio", CancellationToken.None);

        Assert.Equal(200, statusCode);
        var view = Assert.IsType<LlmClientSettingView>(result);
        Assert.False(view.HasFoundryApiKey);
        Assert.Null(view.MaskedFoundryApiKey);
    }

    [Fact]
    public async Task UpsertAsyncDefaultsChangedByToAnonymousWhenBlank()
    {
        var store = new FakeLlmClientSettingsStore();
        var service = BuildService(store);

        await service.UpsertAsync(new LlmClientSettingsInput { GitHubToken = "gho_someToken1234" }, "  ", CancellationToken.None);

        var audit = await service.GetAuditAsync(CancellationToken.None);
        Assert.Equal("anonymous", Assert.Single(audit).ChangedBy);
    }
}
