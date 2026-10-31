using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Ai;

/// <summary>
/// The single <see cref="ILlmClient"/>/<see cref="IToolCallingLlmClient"/> every consumer sees.
/// It resolves the provider for <paramref name="agent"/> on EVERY call and delegates to the
/// matching transport.
///
/// Resolving per call rather than once in the DI container is deliberate and matches how the token
/// has always been resolved here: switching provider from the Settings tab has to take effect
/// without restarting the Functions host, and a container-time choice could not do that.
///
/// One instance exists per agent (see ServiceCollectionExtensions), which is what lets the Doctor,
/// the Scout and the Inspector sit on different providers during a gradual migration - they do not
/// carry the same cost or the same risk, so they should not be forced to move together.
/// </summary>
public sealed class RoutingLlmClient(
    string agent,
    ILlmClientSettingsResolver settingsResolver,
    GitHubCopilotLlmClient copilot,
    FoundryLlmClient foundry,
    ILogger<RoutingLlmClient> logger) : ILlmClient, IToolCallingLlmClient
{
    public async Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(
        string prompt, int maxTokens, CancellationToken ct = default)
    {
        var settings = await settingsResolver.ResolveAsync(agent, ct);

        return settings.UsesFoundry
            ? await foundry.CompleteAsync(settings, prompt, maxTokens, ct)
            : await copilot.CompleteAsync(settings, prompt, maxTokens, ct);
    }

    public async Task<EmbeddingResult> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var settings = await settingsResolver.ResolveAsync(agent, ct);

        return settings.UsesFoundry
            ? await foundry.GetEmbeddingAsync(settings, text, ct)
            : await copilot.GetEmbeddingAsync(settings, text, ct);
    }

    /// <summary>
    /// <paramref name="model"/> comes from the Inspector's roster and is passed through untouched.
    /// It is a model id on GitHub Copilot and a deployment name on Foundry - the router does not
    /// translate between them, because there is no correct automatic mapping: inventing one would
    /// silently run a debate against a model nobody chose.
    /// </summary>
    public async Task<LlmCompletion> CompleteAsync(
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct = default)
    {
        var settings = await settingsResolver.ResolveAsync(agent, ct);

        if (!settings.UsesFoundry)
        {
            return await copilot.CompleteAsync(settings, model, messages, tools, maxTokens, ct);
        }

        logger.LogDebug("[Routing] Agent {Agent} is on {Provider}; sending deployment {Model}.", agent, settings.Provider, model);
        return await foundry.CompleteAsync(settings, model, messages, tools, maxTokens, ct);
    }
}

/// <summary>
/// Settings-tab model picker across both providers. Each result belongs to the provider that was
/// probed: GitHub Copilot returns model ids, Foundry returns deployment names, and they are not
/// interchangeable - which is precisely why the picker must be re-populated after a switch rather
/// than keeping whatever was selected before.
/// </summary>
public sealed class RoutingLlmModelCatalog(GitHubCopilotLlmClient copilot, FoundryLlmClient foundry) : ILlmModelCatalog
{
    public async Task<LlmModelCatalogResult> ListAsync(LlmModelProbe probe, CancellationToken ct = default)
    {
        var provider = LlmProviders.NormalizeOrDefault(probe.Provider, LlmProviders.Default);

        if (string.Equals(provider, LlmProviders.Foundry, StringComparison.Ordinal))
        {
            return await foundry.ListDeploymentsAsync(probe, ct);
        }

        var (success, models, error) = await copilot.ListAvailableModelsAsync(probe.GitHubToken ?? "", ct);
        return success ? LlmModelCatalogResult.Ok(models) : LlmModelCatalogResult.Failed(error ?? "Unknown error.");
    }
}
