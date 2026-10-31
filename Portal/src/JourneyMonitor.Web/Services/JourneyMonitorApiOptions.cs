namespace JourneyMonitor.Web.Services;

/// <summary>
/// Options for reaching JourneyMonitor.Functions. Bound from configuration section
/// "JourneyMonitorApi" (appsettings / environment variables), never hardcoded.
/// </summary>
public sealed class JourneyMonitorApiOptions
{
    public const string SectionName = "JourneyMonitorApi";

    /// <summary>Base address of the Functions host, e.g. https://localhost:7071 locally or the
    /// Function App's URL in Azure. In Azure this call should be Managed Identity-authenticated
    /// (see AddJourneyMonitorApiClient in Program.cs) rather than anonymous, once the Function
    /// app enforces auth - anonymous access here matches the Functions' current AuthorizationLevel
    /// during local development only.</summary>
    public string BaseUrl { get; set; } = "http://localhost:7071";

    /// <summary>
    /// Functions host for the staging environment. A request whose query string <c>env</c> is
    /// staging, uat, or stage is sent to this host. That host's own connection string selects
    /// its database. Empty keeps every request on <see cref="BaseUrl"/>.
    /// </summary>
    public string? StagingBaseUrl { get; set; }

    /// <summary>
    /// Shared secret sent as the X-Settings-Write-Secret header on every Settings tab write
    /// (PUT /api/settings/latency/{domain}, PUT /api/settings/llm-client). The Functions side
    /// fails CLOSED when its own copy of this secret is unset (see
    /// UpsertLatencySpikeSettingFunction/UpsertLlmClientSettingFunction) - leaving this blank
    /// does NOT disable the check, it just means the Function side already rejects every write
    /// on its own; both sides must be set to the same value for writes to succeed.
    /// </summary>
    public string? SettingsWriteSecret { get; set; }

    /// <summary>
    /// Shared secret sent as the X-Internal-Api-Secret header on calls that expose more than a
    /// plain read or perform an outbound call on the caller's behalf (GET /api/findings/{id}/doctor-report,
    /// POST /api/settings/llm-client/models). Same fail-closed pairing as
    /// <see cref="SettingsWriteSecret"/> - see MonitoringOptions.InternalApiSecret on the
    /// Functions side.
    /// </summary>
    public string? InternalApiSecret { get; set; }
}
