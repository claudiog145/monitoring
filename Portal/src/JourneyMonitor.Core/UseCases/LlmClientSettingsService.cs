using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Effective LLM client settings for the Settings tab. Never carries a real credential - only
/// whether one is configured and a masked hint - so the GET response is safe to log/screenshot
/// during a demo.
///
/// Stored values and effective values are both exposed on purpose: an agent whose override is null
/// INHERITS the global provider, and "inherits github-copilot" behaves differently from "pinned to
/// github-copilot" the next time somebody changes the global one.
/// </summary>
public sealed record LlmClientSettingView(
    bool HasToken,
    string? MaskedToken,
    string Model,
    string EffectiveModel,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy)
{
    public string Provider { get; init; } = LlmProviders.Default;

    /// <summary>Null means "inherits <see cref="Provider"/>" - not the same as being pinned to the same value.</summary>
    public string? DoctorProvider { get; init; }

    public string? ScoutProvider { get; init; }

    public string? InspectorProvider { get; init; }

    public string EffectiveDoctorProvider { get; init; } = LlmProviders.Default;

    public string EffectiveScoutProvider { get; init; } = LlmProviders.Default;

    public string EffectiveInspectorProvider { get; init; } = LlmProviders.Default;

    public string? FoundryEndpoint { get; init; }

    public string FoundryAuthMode { get; init; } = FoundryAuthModes.Default;

    public bool HasFoundryApiKey { get; init; }

    public string? MaskedFoundryApiKey { get; init; }

    public string? FoundryChatDeployment { get; init; }

    public string? FoundryEmbeddingDeployment { get; init; }

    /// <summary>
    /// Configuration that is saved but cannot serve a call, plus the one case that does work and
    /// still deserves saying out loud: running on a personal credential. Surfaced here rather than
    /// discovered later as "the agent stopped answering" - a half-configured provider and a dead
    /// agent look identical from the outside.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>One write from the Settings tab. See <see cref="LlmClientSettingUpdate"/> for the null-vs-empty rule on both secrets.</summary>
public sealed record LlmClientSettingsInput
{
    public string? GitHubToken { get; init; }

    public bool ClearToken { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string? DoctorProvider { get; init; }

    public string? ScoutProvider { get; init; }

    public string? InspectorProvider { get; init; }

    public string? FoundryEndpoint { get; init; }

    public string? FoundryAuthMode { get; init; }

    public string? FoundryApiKey { get; init; }

    public bool ClearFoundryApiKey { get; init; }

    public string? FoundryChatDeployment { get; init; }

    public string? FoundryEmbeddingDeployment { get; init; }
}

/// <summary>
/// Backs the Settings tab's "LLM Client" section: read/write which provider each agent talks to and
/// the credentials for both, without depending on environment-specific values (see
/// <see cref="ILlmClientSettingsResolver"/> for the DB-override-then-config precedence). Any portal
/// user may change it (no authorization check, same product decision as the Latency Spike Monkey
/// thresholds) - who/when is recorded in <see cref="ILlmClientSettingsStore.GetAuditAsync"/>, but
/// never a credential value, past or present.
/// </summary>
public sealed class LlmClientSettingsService(
    ILlmClientSettingsStore settingsStore,
    ILlmClientSettingsResolver settingsResolver,
    IOptions<JourneyStoreOptions> storeOptions)
{
    public async Task<LlmClientSettingView> GetAsync(CancellationToken ct)
    {
        // SqlLlmClientSettingsStore throws when JourneyStore:ConnectionString isn't set (see its
        // OpenConnection), or when it IS set but the table/procs haven't been provisioned yet on
        // that database (e.g. a QA environment before the migration scripts have run - see
        // AddJourneyMonitorMonitoring's MonitoringSchemaGate for the same class of gap) - either
        // way, degrade to the config-only effective settings instead of a 500 for a plain read.
        var stored = storeOptions.Value.IsConfigured ? await TryGetStoredAsync(ct) : null;

        var effective = await settingsResolver.ResolveAsync(ct);
        var doctor = await settingsResolver.ResolveAsync(LlmAgents.Doctor, ct);
        var scout = await settingsResolver.ResolveAsync(LlmAgents.Scout, ct);
        var inspector = await settingsResolver.ResolveAsync(LlmAgents.Inspector, ct);

        return new LlmClientSettingView(
            HasToken: !string.IsNullOrWhiteSpace(effective.GitHubToken),
            MaskedToken: Mask(effective.GitHubToken),
            Model: stored?.Model ?? "",
            EffectiveModel: effective.Model,
            UpdatedAt: stored?.UpdatedAt,
            UpdatedBy: stored?.UpdatedBy)
        {
            Provider = effective.Provider,
            DoctorProvider = stored?.DoctorProvider,
            ScoutProvider = stored?.ScoutProvider,
            InspectorProvider = stored?.InspectorProvider,
            EffectiveDoctorProvider = doctor.Provider,
            EffectiveScoutProvider = scout.Provider,
            EffectiveInspectorProvider = inspector.Provider,
            FoundryEndpoint = effective.FoundryEndpoint,
            FoundryAuthMode = effective.FoundryAuthMode,
            HasFoundryApiKey = !string.IsNullOrWhiteSpace(effective.FoundryApiKey),
            MaskedFoundryApiKey = Mask(effective.FoundryApiKey),
            FoundryChatDeployment = effective.FoundryChatDeployment,
            FoundryEmbeddingDeployment = effective.FoundryEmbeddingDeployment,
            Warnings = BuildWarnings(doctor, scout, inspector),
        };
    }

    /// <summary>Configuration that is saved but cannot actually serve a call, plus the personal-credential case.</summary>
    private static IReadOnlyList<string> BuildWarnings(
        ResolvedLlmClientSettings doctor,
        ResolvedLlmClientSettings scout,
        ResolvedLlmClientSettings inspector)
    {
        var warnings = new List<string>();

        foreach (var (agentName, settings) in new[]
                 {
                     ("Doctor", doctor),
                     ("Scout", scout),
                     ("Inspector", inspector),
                 })
        {
            if (settings.UsesFoundry)
            {
                if (string.IsNullOrWhiteSpace(settings.FoundryEndpoint))
                {
                    warnings.Add($"The {agentName} is set to Foundry but no endpoint is configured, so its calls cannot be sent.");
                }
                else if (!settings.UsesManagedIdentity && string.IsNullOrWhiteSpace(settings.FoundryApiKey))
                {
                    warnings.Add($"The {agentName} is set to Foundry with API key authentication but no key is stored, so its calls cannot be authenticated.");
                }

                if (string.IsNullOrWhiteSpace(settings.FoundryChatDeployment))
                {
                    warnings.Add($"The {agentName} is set to Foundry but no chat deployment is named. On Foundry the model field carries a deployment name, not a model id.");
                }
            }
            else if (string.IsNullOrWhiteSpace(settings.GitHubToken))
            {
                warnings.Add($"The {agentName} is set to GitHub Copilot but no token is configured, so it will report \"unknown\" instead of answering.");
            }
            else
            {
                warnings.Add($"The {agentName} runs on a personal GitHub Copilot credential. It stops working when that account's token is rotated or the person leaves.");
            }
        }

        // The Inspector sends its roster verbatim; Copilot model ids pointed at Foundry are a 404 on
        // every single call of every investigation, which is worth saying before it happens.
        if (inspector.UsesFoundry)
        {
            warnings.Add("The Inspector's debate roster (Inspector:DebateModels / Inspector:PlannerModel) must name Foundry deployments while it is on Foundry - model ids from another provider resolve to nothing.");
        }

        if (doctor.UsesFoundry && string.IsNullOrWhiteSpace(doctor.FoundryEmbeddingDeployment))
        {
            warnings.Add("No Foundry embedding deployment is named, so the knowledge base cannot be re-indexed and retrieval falls back to keyword matching.");
        }

        return warnings;
    }

    private async Task<LlmClientSetting?> TryGetStoredAsync(CancellationToken ct)
    {
        try
        {
            return await settingsStore.GetAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<(int StatusCode, object Result)> UpsertAsync(
        LlmClientSettingsInput input,
        string? changedBy,
        CancellationToken ct)
    {
        // null = "leave the stored secret unchanged" (see LlmClientSettingUpdate); the explicit
        // Clear* flags are the only way to wipe one, so a client can never blank a credential out
        // by accident just by omitting the field.
        var normalizedToken = input.ClearToken ? "" : input.GitHubToken;
        var normalizedKey = input.ClearFoundryApiKey ? "" : input.FoundryApiKey;

        if (Validate(input, normalizedToken) is { } error)
        {
            return (400, new { error });
        }

        var who = string.IsNullOrWhiteSpace(changedBy) ? "anonymous" : changedBy.Trim();

        await settingsStore.UpsertAsync(
            new LlmClientSettingUpdate
            {
                GitHubToken = normalizedToken,
                Model = input.Model,
                Provider = input.Provider,
                DoctorProvider = input.DoctorProvider,
                ScoutProvider = input.ScoutProvider,
                InspectorProvider = input.InspectorProvider,
                FoundryEndpoint = input.FoundryEndpoint,
                FoundryAuthMode = input.FoundryAuthMode,
                FoundryApiKey = normalizedKey,
                FoundryChatDeployment = input.FoundryChatDeployment,
                FoundryEmbeddingDeployment = input.FoundryEmbeddingDeployment,
            },
            who,
            ct);

        return (200, await GetAsync(ct));
    }

    /// <summary>
    /// Rejects values that are wrong in themselves. Values that are merely INCOMPLETE (Foundry
    /// selected before its endpoint has been filled in) come back as warnings from
    /// <see cref="GetAsync"/> instead - otherwise the form could never be filled in one field at a time.
    /// </summary>
    private static string? Validate(LlmClientSettingsInput input, string? normalizedToken)
    {
        if (!string.IsNullOrEmpty(normalizedToken) && !normalizedToken.StartsWith("gho_", StringComparison.Ordinal))
        {
            return "gitHubToken must start with 'gho_' (a ghp_ PAT is rejected by the Copilot chat endpoint).";
        }

        foreach (var (field, value) in new[]
                 {
                     ("provider", input.Provider),
                     ("doctorProvider", input.DoctorProvider),
                     ("scoutProvider", input.ScoutProvider),
                     ("inspectorProvider", input.InspectorProvider),
                 })
        {
            // Empty is how the UI clears an agent override back to "inherit"; only a non-empty,
            // unrecognised value is an error.
            if (!string.IsNullOrWhiteSpace(value) && !LlmProviders.IsKnown(value))
            {
                return $"{field} must be one of: {string.Join(", ", LlmProviders.All)}.";
            }
        }

        if (!string.IsNullOrWhiteSpace(input.FoundryAuthMode) && !FoundryAuthModes.IsKnown(input.FoundryAuthMode))
        {
            return $"foundryAuthMode must be one of: {string.Join(", ", FoundryAuthModes.All)}.";
        }

        if (!string.IsNullOrWhiteSpace(input.FoundryEndpoint))
        {
            var endpointError = FoundryEndpointValidator.Validate(input.FoundryEndpoint);
            if (endpointError is not null) return endpointError;
        }

        return null;
    }

    public async Task<IReadOnlyList<LlmClientSettingAuditEntry>> GetAuditAsync(CancellationToken ct)
    {
        if (!storeOptions.Value.IsConfigured) return [];
        try
        {
            return await settingsStore.GetAuditAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>"****ab12" (last 4 chars) - just enough to tell two credentials apart at a glance without exposing either.</summary>
    private static string? Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        return secret.Length <= 4 ? "****" : $"****{secret[^4..]}";
    }
}
