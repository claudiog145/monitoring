using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// A candidate configuration the Settings tab wants to test before saving it. Deliberately not
/// <see cref="Monitoring.ResolvedLlmClientSettings"/>: the whole point is to probe credentials the
/// user has just pasted and which are therefore NOT the ones currently stored.
/// </summary>
public sealed record LlmModelProbe
{
    public string Provider { get; init; } = LlmProviders.Default;

    public string? GitHubToken { get; init; }

    public string? FoundryEndpoint { get; init; }

    public string? FoundryAuthMode { get; init; }

    public string? FoundryApiKey { get; init; }
}

/// <summary>
/// What a catalogue lookup found. <see cref="Models"/> carries GitHub Copilot model ids or Foundry
/// *deployment* names depending on the provider probed - they are not the same namespace, which is
/// exactly why the picker has to be re-populated after switching provider.
/// </summary>
public sealed record LlmModelCatalogResult(bool Success, IReadOnlyList<string> Models, string? Error)
{
    public static LlmModelCatalogResult Ok(IReadOnlyList<string> models) => new(true, models, null);

    public static LlmModelCatalogResult Failed(string error) => new(false, [], error);
}

/// <summary>
/// Lists what a given set of credentials can actually talk to, so the Settings tab offers a real
/// choice instead of a free-text field. Never throws - an invalid credential, an unreachable
/// endpoint or an unexpected response shape all come back as
/// <see cref="LlmModelCatalogResult.Failed"/> with a message a human can act on.
///
/// Nothing is persisted here and the shared session cache is never touched: the credential being
/// probed may be a candidate the user is still evaluating.
/// </summary>
public interface ILlmModelCatalog
{
    Task<LlmModelCatalogResult> ListAsync(LlmModelProbe probe, CancellationToken ct = default);
}
