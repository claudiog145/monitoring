using System.Text.Json;

namespace DemoGenerator.Packs;

// Minimal re-declarations of the platform's wire shapes, so the demo never references a platform project:
//   profile.json  -> CompanyProfile accepted by PUT/POST /api/settings/company-profiles (JourneyMonitor.Core.Company.CompanyModel)
//   sources.json  -> rows for PUT /api/settings/telemetry-sources (ownerProfileKey is sent as profileKey)
//   kpis.json     -> bodies for POST /api/settings/kpis, plus kqlText/kqlDescription for the kql: catalog query
//                    (PUT /api/settings/kql/{queryKey}) that a kql:kpi:* KPI runs.
// Only the members the validator reads are declared; unknown members are ignored, as the platform does.

public sealed class ProfileDocument
{
    public string? ProfileKey { get; init; }
    public string? DisplayName { get; init; }
    public ProfileLabels? Labels { get; init; }
    public List<ProfileService?>? Services { get; init; }
    public List<ProfileIdentitySource?>? EntityIdentitySources { get; init; }
    public ProfileGlossary? Glossary { get; init; }
    public bool Enabled { get; init; } = true;
    public List<ProfilePartner?>? Partners { get; init; }
    public ProfilePrivacy? Privacy { get; init; }
}

public sealed class ProfileLabels
{
    public string? Service { get; init; }
    public string? Flow { get; init; }
    public string? Step { get; init; }
    public string? Entity { get; init; }
    public string? Owner { get; init; }
}

public sealed class ProfileGlossary
{
    public string? ServiceNoun { get; init; }
    public string? FlowNoun { get; init; }
    public string? StepNoun { get; init; }
    public string? EntityNoun { get; init; }
    public string? OwnerNoun { get; init; }
    public string? Description { get; init; }
}

public sealed class ProfileService
{
    public string? Key { get; init; }
    public string? DisplayName { get; init; }
    public List<string>? TelemetrySourceAliases { get; init; }
    public List<string>? HostFilters { get; init; }
    public List<ProfileFlow?>? Flows { get; init; }
    public string? FallbackStep { get; init; }
}

public sealed class ProfileFlow
{
    public string? Key { get; init; }
    public string? DisplayName { get; init; }
    public List<ProfileStep?>? Steps { get; init; }
}

public sealed class ProfileStep
{
    public string? Key { get; init; }
    public string? DisplayName { get; init; }
    public int Order { get; init; }
    public List<ProfileStepMatcher?>? Matchers { get; init; }
}

public sealed class ProfileStepMatcher
{
    public string? Kind { get; init; }
    public string? Value { get; init; }
    public bool CaseSensitive { get; init; }
}

public sealed class ProfileIdentitySource
{
    public int Order { get; init; }
    public string? Kind { get; init; }
    public string? Key { get; init; }
    public string? EntityKind { get; init; }
}

public sealed class ProfilePartner
{
    public string? Key { get; init; }
    public string? DisplayName { get; init; }
    public List<ProfilePartnerMatcher?>? Matchers { get; init; }
}

public sealed class ProfilePartnerMatcher
{
    public string? Kind { get; init; }
    public string? Value { get; init; }
}

public sealed class ProfilePrivacy
{
    public bool AllowRawEvidence { get; init; }
    public List<string?>? PiiDimensions { get; init; }
    public List<string?>? Patterns { get; init; }
}

public sealed class SourceDocument
{
    public string? Alias { get; init; }
    public string? Environment { get; init; }
    public string? Kind { get; init; }
    public string? ResourceId { get; init; }
    public string? AppId { get; init; }
    public string? TenantId { get; init; }
    public string? OwnerProfileKey { get; init; }
    public bool? Enabled { get; init; }
    public string? Notes { get; init; }
}

public sealed class KpiDocument
{
    public int? KpiDefinitionId { get; init; }
    public string? Pillar { get; init; }
    public string? Kpi { get; init; }
    public string? Domain { get; init; }
    public string? Stage { get; init; }
    public string? SourceKey { get; init; }
    public string? Direction { get; init; }
    public string? Aggregation { get; init; }
    public string? EvaluationFrequency { get; init; }
    public string? BucketSize { get; init; }
    public string? LookbackWindow { get; init; }
    public int? SampleSize { get; init; }
    public int? MinObservationsPerBucket { get; init; }
    public string? DetectionMethod { get; init; }

    /// <summary>A JSON string (as stored) or a JSON object; the platform accepts both.</summary>
    public JsonElement? MethodParamsJson { get; init; }

    public int? ConsecutiveCount { get; init; }
    public int? CooldownMinutes { get; init; }
    public bool? Enabled { get; init; }
    public string? ProfileKey { get; init; }
    public string? Grain { get; init; }
    public int? MaxEntitiesPerBucket { get; init; }

    /// <summary>Pack extension: text of the catalog query a <c>kql:kpi:*</c> KPI runs (seeded with PUT /api/settings/kql/{key}).</summary>
    public string? KqlText { get; init; }

    public string? KqlDescription { get; init; }
}
