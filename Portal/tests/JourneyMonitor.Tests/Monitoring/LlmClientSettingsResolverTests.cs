using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>In-memory ILlmClientSettingsStore double - no SQL, keeps everything in a single field.</summary>
internal sealed class FakeLlmClientSettingsStore : ILlmClientSettingsStore
{
    private LlmClientSetting? _setting;
    private readonly List<LlmClientSettingAuditEntry> _audit = [];

    public Task<LlmClientSetting?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_setting);

    public Task UpsertAsync(LlmClientSettingUpdate update, string changedBy, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var resolvedToken = update.GitHubToken is null ? _setting?.GitHubToken : (string.IsNullOrEmpty(update.GitHubToken) ? null : update.GitHubToken);
        var resolvedModel = update.Model is null ? _setting?.Model : (string.IsNullOrWhiteSpace(update.Model) ? null : update.Model);

        _setting = new LlmClientSetting(resolvedToken, resolvedModel, now, changedBy)
        {
            Provider = update.Provider ?? _setting?.Provider,
            DoctorProvider = update.DoctorProvider ?? _setting?.DoctorProvider,
            ScoutProvider = update.ScoutProvider ?? _setting?.ScoutProvider,
            InspectorProvider = update.InspectorProvider ?? _setting?.InspectorProvider,
            FoundryEndpoint = update.FoundryEndpoint ?? _setting?.FoundryEndpoint,
            FoundryAuthMode = update.FoundryAuthMode ?? _setting?.FoundryAuthMode,
            FoundryApiKey = update.FoundryApiKey is null
                ? _setting?.FoundryApiKey
                : (string.IsNullOrEmpty(update.FoundryApiKey) ? null : update.FoundryApiKey),
            FoundryChatDeployment = update.FoundryChatDeployment ?? _setting?.FoundryChatDeployment,
            FoundryEmbeddingDeployment = update.FoundryEmbeddingDeployment ?? _setting?.FoundryEmbeddingDeployment,
        };

        _audit.Insert(0, new LlmClientSettingAuditEntry(update.GitHubToken is not null, resolvedModel, now, changedBy)
        {
            FoundryApiKeyChanged = update.FoundryApiKey is not null,
            Provider = _setting.Provider,
            DoctorProvider = _setting.DoctorProvider,
            ScoutProvider = _setting.ScoutProvider,
            InspectorProvider = _setting.InspectorProvider,
            FoundryEndpoint = _setting.FoundryEndpoint,
            FoundryAuthMode = _setting.FoundryAuthMode,
            FoundryChatDeployment = _setting.FoundryChatDeployment,
            FoundryEmbeddingDeployment = _setting.FoundryEmbeddingDeployment,
        });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LlmClientSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LlmClientSettingAuditEntry>>(_audit.ToList());
}

public class LlmClientSettingsResolverTests
{
    private static LlmClientSettingsResolver BuildResolver(
        ILlmClientSettingsStore store,
        bool storeConfigured,
        AiOptions? aiOptions = null,
        FoundryOptions? foundryOptions = null) =>
        new(
            store,
            Options.Create(new JourneyStoreOptions { ConnectionString = storeConfigured ? "Server=fake;" : null }),
            Options.Create(aiOptions ?? new AiOptions()),
            Options.Create(foundryOptions ?? new FoundryOptions()),
            NullLogger<LlmClientSettingsResolver>.Instance);

    [Fact]
    public async Task FallsBackToAiOptionsWhenTheStoreIsNotConfigured()
    {
        var resolver = BuildResolver(new FakeLlmClientSettingsStore(), storeConfigured: false, new AiOptions { GitHubToken = "gho_envToken", Model = "gpt-4o" });

        var resolved = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("gho_envToken", resolved.GitHubToken);
        Assert.Equal("gpt-4o", resolved.Model);
    }

    [Fact]
    public async Task FallsBackToAiOptionsWhenNothingHasEverBeenSaved()
    {
        var resolver = BuildResolver(new FakeLlmClientSettingsStore(), storeConfigured: true, new AiOptions { GitHubToken = "gho_envToken" });

        var resolved = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("gho_envToken", resolved.GitHubToken);
        Assert.Equal(AiOptions.DefaultModel, resolved.Model);
    }

    [Fact]
    public async Task StoredTokenWinsOverAiOptions()
    {
        var store = new FakeLlmClientSettingsStore();
        await store.UpsertAsync(new LlmClientSettingUpdate { GitHubToken = "gho_storedToken" }, "tester", CancellationToken.None);
        var resolver = BuildResolver(store, storeConfigured: true, new AiOptions { GitHubToken = "gho_envToken" });

        var resolved = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("gho_storedToken", resolved.GitHubToken);
    }

    [Fact]
    public async Task StoredModelWinsOverAiOptionsIndependentlyOfTheToken()
    {
        var store = new FakeLlmClientSettingsStore();
        await store.UpsertAsync(new LlmClientSettingUpdate { Model = "gpt-4o" }, "tester", CancellationToken.None);
        var resolver = BuildResolver(store, storeConfigured: true, new AiOptions { GitHubToken = "gho_envToken", Model = "gpt-4o-mini" });

        var resolved = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("gho_envToken", resolved.GitHubToken);
        Assert.Equal("gpt-4o", resolved.Model);
    }

    [Fact]
    public async Task GenuineCancellationPropagatesInsteadOfFallingBackSilently()
    {
        var resolver = new LlmClientSettingsResolver(
            new ThrowingLlmClientSettingsStore(),
            Options.Create(new JourneyStoreOptions { ConnectionString = "Server=fake;" }),
            Options.Create(new AiOptions()),
            Options.Create(new FoundryOptions()),
            NullLogger<LlmClientSettingsResolver>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync(cts.Token));
    }

    [Fact]
    public async Task ANonCancellationStoreFailureFallsBackToAiOptions()
    {
        var resolver = new LlmClientSettingsResolver(
            new ThrowingLlmClientSettingsStore(),
            Options.Create(new JourneyStoreOptions { ConnectionString = "Server=fake;" }),
            Options.Create(new AiOptions { GitHubToken = "gho_envToken" }),
            Options.Create(new FoundryOptions()),
            NullLogger<LlmClientSettingsResolver>.Instance);

        var resolved = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal("gho_envToken", resolved.GitHubToken);
    }

    /// <summary>Mirrors SqlLlmClientSettingsStore.OpenConnection: throws on every call except when explicitly cancelled.</summary>
    private sealed class ThrowingLlmClientSettingsStore : ILlmClientSettingsStore
    {
        public Task<LlmClientSetting?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");
        }

        public Task UpsertAsync(LlmClientSettingUpdate update, string changedBy, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");

        public Task<IReadOnlyList<LlmClientSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("JourneyStore:ConnectionString is not configured.");
    }
}
