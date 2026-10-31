namespace JourneyMonitor.Core.Options;

/// <summary>
/// Global fallback thresholds for the Latency Spike Monkey, all nullable so a missing value falls
/// back to the documented default (same nullable-default pattern used elsewhere in this repo for
/// Insights options) instead of silently using zero. Since 2026-09-22, a domain can override
/// SpikeThresholdMs/SpikeOccurrenceThreshold/SpikeWindowMinutes via the Settings tab (persisted in
/// <see cref="JourneyMonitor.Core.Abstractions.ILatencySpikeSettingsStore"/>) - see
/// <see cref="JourneyMonitor.Core.Monitoring.ILatencySpikeSettingsResolver"/> for how the two are
/// combined. The values here remain the default for any domain without a stored override.
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Individual request duration (ms) above which a request counts as a "spike". Default 5000.</summary>
    public int? SpikeThresholdMs { get; set; }

    /// <summary>How many spikes in the same stage within the window before a Finding is raised. Default 5.</summary>
    public int? SpikeOccurrenceThreshold { get; set; }

    /// <summary>Rolling window (minutes) the detector looks back over each run. Default 60.</summary>
    public int? SpikeWindowMinutes { get; set; }

    /// <summary>Floor (ms) applied inside the KQL itself to keep result volume small before C# does the real threshold/occurrence grouping. Default 1000.</summary>
    public int? SpikeKqlFloorMs { get; set; }

    /// <summary>
    /// CSV of domains that should use the SQL-journey-store-based detector
    /// (<c>SqlLatencySpikeDetectorService</c>) instead of the KQL-based one. Empty/unset means
    /// every domain still uses KQL - this is the gradual per-domain rollout switch from the
    /// KQL-to-DB migration plan (docs/product/latency-spike-doctor-monkey-db-migration-plan-2026-09-21.md).
    /// </summary>
    public string? SqlDetectorDomains { get; set; }

    /// <summary>
    /// CSV of domains where, in addition to running the active detector (KQL or SQL, per
    /// <see cref="SqlDetectorDomains"/>) for real, the OTHER detector also runs a side-by-side dry
    /// run (<c>PreviewAsync</c>, no Finding/repository side effects) purely to log a comparison -
    /// the Fase 3 validation step from the KQL-to-DB migration plan. Empty/unset means no shadow
    /// comparison runs for any domain.
    /// </summary>
    public string? ShadowDomains { get; set; }

    /// <summary>
    /// Shared secret required (via the X-Settings-Write-Secret header) on PUT
    /// /api/settings/latency/{domain} and PUT /api/settings/llm-client - see
    /// UpsertLatencySpikeSettingFunction/UpsertLlmClientSettingFunction. Fails CLOSED, not open:
    /// this must be configured (and match JourneyMonitorApi:SettingsWriteSecret on the Web side)
    /// for either endpoint to accept ANY write - leaving it empty/unset does NOT disable the
    /// check, it makes every write return 401.
    /// </summary>
    public string? SettingsWriteSecret { get; set; }

    /// <summary>
    /// Shared secret required (via the X-Internal-Api-Secret header) on Function endpoints that
    /// expose sensitive internal data or make outbound calls on the caller's behalf but are not
    /// themselves a Settings write - GetFindingDoctorReport (full LLM prompt/response) and
    /// ListLlmClientModels (executes a live GitHub Copilot API call with a caller-supplied
    /// token). These stay AuthorizationLevel.Anonymous like every Function in this app (see
    /// Program.cs's Managed Identity extension-point comment), but unlike the plain reads,
    /// bypassing the Web proxy here would skip its Entra authorization entirely. Fails CLOSED
    /// like <see cref="SettingsWriteSecret"/> - must be configured (and match
    /// JourneyMonitorApi:InternalApiSecret on the Web side) for either endpoint to respond.
    /// </summary>
    public string? InternalApiSecret { get; set; }

    public const int DefaultSpikeThresholdMs = 5000;
    public const int DefaultSpikeOccurrenceThreshold = 5;
    public const int DefaultSpikeWindowMinutes = 60;
    public const int DefaultSpikeKqlFloorMs = 1000;

    // A misconfigured 0 or negative value (e.g. SpikeOccurrenceThreshold=0) falls back to the
    // documented default instead of being clamped to 1 - it would otherwise divide-by-zero in the
    // severity ratio calculation in LatencySpikeDetectorService and produce nonsensical KQL
    // substitutions (a "0m" or negative-duration window).
    public int ResolvedSpikeThresholdMs => SpikeThresholdMs is > 0 ? SpikeThresholdMs.Value : DefaultSpikeThresholdMs;
    public int ResolvedSpikeOccurrenceThreshold => SpikeOccurrenceThreshold is > 0 ? SpikeOccurrenceThreshold.Value : DefaultSpikeOccurrenceThreshold;
    public int ResolvedSpikeWindowMinutes => SpikeWindowMinutes is > 0 ? SpikeWindowMinutes.Value : DefaultSpikeWindowMinutes;
    public int ResolvedSpikeKqlFloorMs => SpikeKqlFloorMs is > 0 ? SpikeKqlFloorMs.Value : DefaultSpikeKqlFloorMs;

    /// <summary>True when <paramref name="domain"/> is listed in <see cref="SqlDetectorDomains"/> (case-insensitive).</summary>
    public bool UsesSqlDetector(string domain) =>
        !string.IsNullOrWhiteSpace(SqlDetectorDomains) &&
        SqlDetectorDomains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="domain"/> is listed in <see cref="ShadowDomains"/> (case-insensitive).</summary>
    public bool IsShadowDomain(string domain) =>
        !string.IsNullOrWhiteSpace(ShadowDomains) &&
        ShadowDomains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// GitHub Copilot chat/embeddings configuration. The token is a secret (OAuth `gho_`, NOT a
/// `ghp_` PAT — PATs are rejected by the Copilot chat endpoint) and must come from
/// local.settings.json / environment variable, never committed.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>GitHub OAuth token, must start with "gho_". See README for how to obtain one (`gh auth login --scopes copilot`).</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Chat model id, e.g. "gpt-4o-mini" for the classifier tie-break, a stronger model for the Doctor's deep diagnosis.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Embedding model id for the knowledge base. Configurable because the hardcoded
    /// "text-embedding-3-small" was rejected by the Copilot embeddings endpoint with HTTP 400,
    /// which went unnoticed for months: the client returned "no embedding" without logging, so
    /// every knowledge chunk silently failed to index.
    /// </summary>
    public string? EmbeddingModel { get; set; }

    public const string DefaultModel = "gpt-4o-mini";

    public const string DefaultEmbeddingModel = "text-embedding-ada-002";

    public string ResolvedModel => string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model;

    public string ResolvedEmbeddingModel => string.IsNullOrWhiteSpace(EmbeddingModel) ? DefaultEmbeddingModel : EmbeddingModel;
}
