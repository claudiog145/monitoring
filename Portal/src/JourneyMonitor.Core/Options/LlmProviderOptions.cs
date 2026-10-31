namespace JourneyMonitor.Core.Options;

/// <summary>
/// Which hosted service answers an LLM call. Values are persisted in
/// <c>monitoring.LlmClientSetting</c> and shown in the Settings tab, so they are stable strings
/// rather than an enum - an unknown value read back from a newer schema must degrade to the
/// default instead of throwing on deserialization.
/// </summary>
public static class LlmProviders
{
    public const string GitHubCopilot = "github-copilot";
    public const string Foundry = "foundry";

    /// <summary>The provider used when nothing has been configured yet - keeps existing environments working untouched.</summary>
    public const string Default = GitHubCopilot;

    public static readonly IReadOnlyList<string> All = [GitHubCopilot, Foundry];

    public static bool IsKnown(string? value) =>
        value is not null && All.Any(p => string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Unknown/blank collapses to <paramref name="fallback"/>: a typo must not silently route calls nowhere.</summary>
    public static string NormalizeOrDefault(string? value, string fallback) =>
        IsKnown(value) ? value!.Trim().ToLowerInvariant() : fallback;
}

/// <summary>
/// The three LLM consumers in this platform. Each can override the global provider so a migration
/// can move one agent at a time - the Scout costs ~1.3k tokens per package, the Inspector ~198k per
/// investigation, so they do not carry the same risk and should not have to move together.
/// </summary>
public static class LlmAgents
{
    public const string Doctor = "doctor";
    public const string Scout = "scout";
    public const string Inspector = "inspector";

    /// <summary>Used by callers with no agent identity of their own (e.g. the Settings model picker).</summary>
    public const string Unspecified = "default";

    public static readonly IReadOnlyList<string> All = [Doctor, Scout, Inspector];

    public static bool IsKnown(string? value) =>
        value is not null && All.Any(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? value) =>
        IsKnown(value) ? value!.Trim().ToLowerInvariant() : Unspecified;
}

/// <summary>How a Foundry request authenticates.</summary>
public static class FoundryAuthModes
{
    /// <summary>A resource API key, encrypted at rest by the settings store.</summary>
    public const string ApiKey = "api-key";

    /// <summary>
    /// Keyless, via <c>DefaultAzureCredential</c>. No secret is stored anywhere, which also removes
    /// this setting from the Data Protection key ring that has already caused one QA outage.
    /// </summary>
    public const string ManagedIdentity = "managed-identity";

    public const string Default = ApiKey;

    public static readonly IReadOnlyList<string> All = [ApiKey, ManagedIdentity];

    public static bool IsKnown(string? value) =>
        value is not null && All.Any(m => string.Equals(m, value.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeOrDefault(string? value) =>
        IsKnown(value) ? value!.Trim().ToLowerInvariant() : Default;
}

/// <summary>
/// Microsoft Foundry fallback configuration (environment variables / local.settings.json), used
/// only when the Settings tab has no stored override - the same precedence
/// <see cref="AiOptions"/> has for GitHub Copilot.
///
/// <see cref="ApiKey"/> is a secret and must never be committed. Prefer
/// <see cref="FoundryAuthModes.ManagedIdentity"/>, which needs no secret at all.
/// </summary>
public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    /// <summary>
    /// Resource base URL. Both <c>https://{resource}.openai.azure.com</c> and
    /// <c>https://{resource}.services.ai.azure.com</c> are accepted; the <c>/openai/v1</c> route is
    /// appended by the client, so it may be present or omitted here.
    /// </summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>See <see cref="FoundryAuthModes"/>. Defaults to <see cref="FoundryAuthModes.Default"/>.</summary>
    public string? AuthMode { get; set; }

    /// <summary>Deployment name used for single-prompt chat completions (Doctor/Scout).</summary>
    public string? ChatDeployment { get; set; }

    /// <summary>
    /// Deployment name used for embeddings. Deliberately separate from <see cref="ChatDeployment"/>:
    /// swapping the embedding model invalidates every vector already stored in the knowledge base,
    /// so it must be changeable - and auditable - on its own.
    /// </summary>
    public string? EmbeddingDeployment { get; set; }

    /// <summary>Entra ID scope for keyless inference against a Foundry resource.</summary>
    public const string EntraScope = "https://ai.azure.com/.default";

    /// <summary>
    /// Route appended to the resource endpoint. The v1 surface uses implicit versioning, so no
    /// <c>api-version</c> query parameter is sent.
    /// </summary>
    public const string InferenceRoute = "openai/v1";
}

/// <summary>
/// Allow-list for Foundry endpoints. A managed-identity call attaches the host's own Entra token
/// (or a saved API key) and sends it to whatever host passes here, so this is the one gate between
/// a portal user and exfiltrating that credential to an attacker-controlled server. HTTPS alone is
/// not a trust boundary - any attacker-owned host can serve HTTPS too.
/// </summary>
public static class FoundryEndpointValidator
{
    private static readonly string[] TrustedHostSuffixes =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
    ];

    /// <summary>Null when the endpoint is trusted; otherwise the reason to show the caller.</summary>
    public static string? Validate(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "foundryEndpoint is required.";

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return "foundryEndpoint must be an absolute https URL, e.g. https://my-resource.openai.azure.com.";
        }

        var host = uri.Host;
        var isTrusted = TrustedHostSuffixes.Any(suffix =>
            host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length);

        return isTrusted
            ? null
            : $"foundryEndpoint must point at a real Azure OpenAI/Foundry resource ({string.Join(", ", TrustedHostSuffixes)}), not \"{host}\".";
    }
}
