using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Effective LLM client configuration for one agent after applying the stored override, if any.
///
/// <see cref="Provider"/> is already agent-resolved: the caller asked for an agent and this is the
/// provider that agent must use, so no consumer has to re-apply the global-versus-override rule.
/// </summary>
public sealed record ResolvedLlmClientSettings(string? GitHubToken, string Model)
{
    public string Provider { get; init; } = LlmProviders.Default;

    /// <summary>Embedding model for the GitHub Copilot path. Foundry uses <see cref="FoundryEmbeddingDeployment"/> instead.</summary>
    public string EmbeddingModel { get; init; } = AiOptions.DefaultEmbeddingModel;

    public string? FoundryEndpoint { get; init; }

    public string FoundryAuthMode { get; init; } = FoundryAuthModes.Default;

    public string? FoundryApiKey { get; init; }

    public string? FoundryChatDeployment { get; init; }

    public string? FoundryEmbeddingDeployment { get; init; }

    public bool UsesFoundry => string.Equals(Provider, LlmProviders.Foundry, StringComparison.Ordinal);

    public bool UsesManagedIdentity =>
        string.Equals(FoundryAuthMode, FoundryAuthModes.ManagedIdentity, StringComparison.Ordinal);

    /// <summary>
    /// What goes in the request's <c>model</c> field. On Foundry that is a *deployment* name, which
    /// is why it is not interchangeable with <see cref="Model"/>: a GitHub Copilot model id sent to
    /// Foundry is simply a deployment that does not exist.
    /// </summary>
    public string ChatModelOrDeployment => UsesFoundry ? FoundryChatDeployment ?? "" : Model;
}

/// <summary>
/// Resolves the effective LLM client settings for a given agent: a stored override in
/// <see cref="ILlmClientSettingsStore"/> wins per-field when set, otherwise falls back to
/// <see cref="AiOptions"/> / <see cref="FoundryOptions"/> (environment variable /
/// local.settings.json). Never throws - a store lookup failure just falls back to the config,
/// same anti-hallucination "never break the agents" pattern as
/// <see cref="LatencySpikeSettingsResolver"/>.
/// </summary>
public interface ILlmClientSettingsResolver
{
    /// <summary>Settings for a caller with no agent identity of its own (Settings model picker, diagnostics).</summary>
    Task<ResolvedLlmClientSettings> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Settings for one of <see cref="LlmAgents"/>. An unknown agent resolves to the global
    /// provider rather than throwing: a typo in configuration must not take an agent offline.
    /// </summary>
    Task<ResolvedLlmClientSettings> ResolveAsync(string agent, CancellationToken cancellationToken);
}

public sealed class LlmClientSettingsResolver(
    ILlmClientSettingsStore settingsStore,
    IOptions<JourneyStoreOptions> storeOptions,
    IOptions<AiOptions> aiOptions,
    IOptions<FoundryOptions> foundryOptions,
    ILogger<LlmClientSettingsResolver> logger) : ILlmClientSettingsResolver
{
    public Task<ResolvedLlmClientSettings> ResolveAsync(CancellationToken cancellationToken) =>
        ResolveAsync(LlmAgents.Unspecified, cancellationToken);

    public async Task<ResolvedLlmClientSettings> ResolveAsync(string agent, CancellationToken cancellationToken)
    {
        var fallback = BuildFallback();

        if (!storeOptions.Value.IsConfigured) return fallback;

        LlmClientSetting? stored;
        try
        {
            stored = await settingsStore.GetAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation must propagate, not be swallowed into a "successful" fallback.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM client setting lookup failed; falling back to Ai:*/Foundry:* config.");
            return fallback;
        }

        if (stored is null) return fallback;

        var globalProvider = LlmProviders.NormalizeOrDefault(stored.Provider, fallback.Provider);

        return new ResolvedLlmClientSettings(
            Coalesce(stored.GitHubToken, fallback.GitHubToken),
            Coalesce(stored.Model, fallback.Model) ?? AiOptions.DefaultModel)
        {
            Provider = ResolveProviderForAgent(agent, stored, globalProvider),
            EmbeddingModel = fallback.EmbeddingModel,
            FoundryEndpoint = Coalesce(stored.FoundryEndpoint, fallback.FoundryEndpoint),
            FoundryAuthMode = string.IsNullOrWhiteSpace(stored.FoundryAuthMode)
                ? fallback.FoundryAuthMode
                : FoundryAuthModes.NormalizeOrDefault(stored.FoundryAuthMode),
            FoundryApiKey = Coalesce(stored.FoundryApiKey, fallback.FoundryApiKey),
            FoundryChatDeployment = Coalesce(stored.FoundryChatDeployment, fallback.FoundryChatDeployment),
            FoundryEmbeddingDeployment = Coalesce(stored.FoundryEmbeddingDeployment, fallback.FoundryEmbeddingDeployment),
        };
    }

    /// <summary>
    /// Per-agent override first, then the global provider. An agent override is only honoured when
    /// it names a provider we actually have a client for - an unrecognised value falls through to
    /// the global one instead of routing that agent nowhere.
    /// </summary>
    private static string ResolveProviderForAgent(string agent, LlmClientSetting stored, string globalProvider)
    {
        var agentOverride = LlmAgents.Normalize(agent) switch
        {
            LlmAgents.Doctor => stored.DoctorProvider,
            LlmAgents.Scout => stored.ScoutProvider,
            LlmAgents.Inspector => stored.InspectorProvider,
            _ => null,
        };

        return LlmProviders.NormalizeOrDefault(agentOverride, globalProvider);
    }

    private ResolvedLlmClientSettings BuildFallback()
    {
        var ai = aiOptions.Value;
        var foundry = foundryOptions.Value;

        return new ResolvedLlmClientSettings(ai.GitHubToken, ai.ResolvedModel)
        {
            Provider = LlmProviders.Default,
            EmbeddingModel = ai.ResolvedEmbeddingModel,
            FoundryEndpoint = NullIfBlank(foundry.Endpoint),
            FoundryAuthMode = FoundryAuthModes.NormalizeOrDefault(foundry.AuthMode),
            FoundryApiKey = NullIfBlank(foundry.ApiKey),
            FoundryChatDeployment = NullIfBlank(foundry.ChatDeployment),
            FoundryEmbeddingDeployment = NullIfBlank(foundry.EmbeddingDeployment),
        };
    }

    private static string? Coalesce(string? stored, string? fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
