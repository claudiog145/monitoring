namespace JourneyMonitor.Core.Company;

/// <summary>
/// Everything about one company that today is spelled out in code or baked into KQL: which
/// services exist, which steps a request can belong to, how to tell who is affected, and what to
/// call all of it.
///
/// The shape is FIXED and the meaning is configuration. That is the whole design: a free-form bag
/// of fields would take Red Alert, the dedup key, the owner map, the SQL indexes and the L2 table
/// down with it, because every one of them keys on a stable notion of service/step/entity.
///
/// Persisted as a single JSON column (see monitoring.CompanyProfile), never column-per-property -
/// so the Dapper positional-record trap that has bitten this repo three times does not apply here.
/// Whoever serialises it must use <c>JsonSerializerDefaults.Web</c>: this document is written and
/// read back by property name, and PascalCase would silently produce nulls.
/// </summary>
public sealed record CompanyProfile(
    string ProfileKey,
    string DisplayName,
    CompanyLabels Labels,
    IReadOnlyList<ServiceDefinition> Services,
    /// <summary>Tried in <see cref="EntityIdentitySource.Order"/> order. See the remark on that type.</summary>
    IReadOnlyList<EntityIdentitySource> EntityIdentitySources,
    CompanyGlossary Glossary,
    int Version = 1,
    bool Enabled = true,
    string? Notes = null,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null)
{
    public ServiceDefinition? FindService(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : Services.FirstOrDefault(s => string.Equals(s.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What this company calls each role, for the UI and for prompts. The roles themselves never
/// change name in code or in the database - only their labels do. Arrivia maps Service to
/// "Vertical", Step to "Stage" and Entity to "Club"; another company maps them to something else
/// without a schema migration.
/// </summary>
public sealed record CompanyLabels(
    string Service,
    string Flow,
    string Step,
    string Entity,
    string Owner);

/// <summary>One addressable part of the product (Arrivia: a vertical such as Hotels or Cruises).</summary>
public sealed record ServiceDefinition(
    string Key,
    string DisplayName,
    /// <summary>Aliases registered in monitoring.TelemetrySource. Never a subscription path - KqlSourceResolver owns that substitution.</summary>
    IReadOnlyList<string> TelemetrySourceAliases,
    /// <summary>Hosts that belong to this service. Fills {{HOST_FILTER}}; empty means "do not filter by host".</summary>
    IReadOnlyList<string> HostFilters,
    IReadOnlyList<FlowDefinition> Flows,
    /// <summary>Label for traffic that belongs to this service but matches no step. Null means leave it unresolved, which is the honest default.</summary>
    string? FallbackStep = null);

/// <summary>One measured journey through a service (Arrivia: the booking waterfall).</summary>
public sealed record FlowDefinition(
    string Key,
    string DisplayName,
    IReadOnlyList<StepDefinition> Steps);

/// <summary>
/// One point in a flow. <see cref="Order"/> is load-bearing twice over: it is the display order,
/// and it is the evaluation order of the generated KQL <c>case()</c>, where the FIRST match wins.
/// Two steps whose matchers overlap resolve differently if the order changes, so it is part of the
/// profile rather than something a renderer decides.
/// </summary>
public sealed record StepDefinition(
    string Key,
    string DisplayName,
    int Order,
    /// <summary>Evaluated in list order; the first hit resolves the step.</summary>
    IReadOnlyList<StepMatcher> Matchers);

/// <summary>
/// One test against a telemetry row. Kept to a closed, KQL-expressible set on purpose: every
/// matcher has to survive translation into the generated <c>case()</c>, and a predicate that only
/// C# can evaluate would silently split the board from the exception classifier - which is exactly
/// the double source of truth this refactor exists to remove.
/// </summary>
public sealed record StepMatcher(
    string Kind,
    string Value,
    bool CaseSensitive = false);

public static class StepMatcherKind
{
    public const string UrlContains = "url-contains";

    /// <summary>
    /// Tests the PATH only - what the board's own case() does, since it classifies on
    /// parse_url(url).Path. Prefer this for a marker that names an endpoint: with
    /// <see cref="UrlContains"/> a login redirect carrying ?ReturnUrl=%2fHotel%2fSearch.json
    /// classifies as a hotel search, and the board would never agree.
    /// </summary>
    public const string PathContains = "path-contains";

    public const string OperationEquals = "operation-equals";
    public const string OperationContains = "operation-contains";
    public const string PageNameContains = "page-name-contains";
    public const string Regex = "regex";
}

/// <summary>
/// One way to learn who is affected, tried in <see cref="Order"/>. A list rather than a field
/// because that is what the telemetry actually demands and the current KQL already proves it:
/// Hotels stamp VacationClubId, Cruise carries no club id at all and its ICE partner id IS the
/// club, and some traffic only identifies itself through a query parameter or the host.
/// </summary>
public sealed record EntityIdentitySource(
    int Order,
    string Kind,
    /// <summary>The custom dimension name, query parameter name, or empty for <see cref="EntityIdentitySourceKind.Host"/>.</summary>
    string Key,
    /// <summary>What the value identifies, e.g. "club", "partner", "tenant". Surfaces as EntityIdentity.Kind.</summary>
    string EntityKind);

public static class EntityIdentitySourceKind
{
    public const string CustomDimension = "custom-dimension";
    public const string QueryParameter = "query-parameter";
    public const string Host = "host";
    public const string RoleName = "role-name";
}

/// <summary>
/// The words the agents are allowed to use when they talk about this company.
///
/// Every value here reaches an unattended prompt, so it must pass through
/// <c>PromptSanitizer</c> before it is used - a profile is edited by whoever onboards a customer,
/// which makes it the same trust level as a wiki page, and wiki content is already sanitised for
/// exactly this reason.
/// </summary>
public sealed record CompanyGlossary(
    string ServiceNoun,
    string FlowNoun,
    string StepNoun,
    string EntityNoun,
    string OwnerNoun,
    /// <summary>One or two sentences telling a model what this company's product actually is.</summary>
    string? Description = null);
