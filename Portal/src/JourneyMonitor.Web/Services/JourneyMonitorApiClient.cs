namespace JourneyMonitor.Web.Services;

/// <summary>
/// Typed client encapsulating every call from JourneyMonitor.Web to JourneyMonitor.Functions.
/// PageModels/minimal API endpoints must go through this - never call HttpClient directly -
/// so cancellation, correlation and error handling stay in one place (see AGENTS.md #26).
/// </summary>
public interface IJourneyMonitorApiClient
{
    /// <summary>Forwards the latest-snapshot database journey query.</summary>
    Task<(int StatusCode, string Body)> GetDbJourneysAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/exceptions — incidents stored in the incident schema.</summary>
    Task<(int StatusCode, string Body)> GetExceptionsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/overview — the seven Executive Overview tiles.</summary>
    Task<(int StatusCode, string Body)> GetOverviewAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/clubs the same way.</summary>
    Task<(int StatusCode, string Body)> GetClubsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/club-inventory (brand.ClubInventory for the Application Map tab).</summary>
    Task<(int StatusCode, string Body)> GetClubInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/hotels/business-health — the Hotels Reliability Watch card.</summary>
    Task<(int StatusCode, string Body)> GetHotelBusinessHealthAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/monkeys with its query string - the domain/club filters the rest of the portal already uses.</summary>
    Task<(int StatusCode, string Body)> GetMonkeysAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/findings. Carries ?profileKey= through (contract v6 §5).</summary>
    Task<(int StatusCode, string Body)> GetFindingsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/findings/{id}/doctor-report. Carries ?profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetFindingDoctorReportAsync(string id, string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/scout/packages - summaries only, no member/correlation ids. Carries ?top= and ?board= through.</summary>
    Task<(int StatusCode, string Body)> GetScoutPackagesAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/scout/packages/{id} - the full handoff package, gated by the internal secret.</summary>
    Task<(int StatusCode, string Body)> GetScoutPackageAsync(string id, string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/scout/runs. Carries ?top= and ?board= through.</summary>
    Task<(int StatusCode, string Body)> GetScoutRunsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/scout/stats. Carries ?board= through.</summary>
    Task<(int StatusCode, string Body)> GetScoutStatsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/scouts/{board}/shadow (contract v4 §4) - shadow counts and latest rows.</summary>
    Task<(int StatusCode, string Body)> GetScoutShadowAsync(string board, string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/inspector/cases - case summaries only, no transcript.</summary>
    Task<(int StatusCode, string Body)> GetInspectorCasesAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/inspector/cases/{id} - the full case including the raw LLM transcript, gated by the internal secret.</summary>
    Task<(int StatusCode, string Body)> GetInspectorCaseAsync(string id, string queryString, CancellationToken cancellationToken);

    Task<(int StatusCode, string Body)> GetRcaAsync(string caseId, string queryString, CancellationToken cancellationToken);

    /// <summary>What the RCA Agent can draw on: the imported corpus, one row per document. Carries ?profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetKnowledgeDocumentsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>The indexed passages of one document.</summary>
    Task<(int StatusCode, string Body)> GetKnowledgeDocumentAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Meaning-based search over the corpus. Each call embeds the query, so it is not free.</summary>
    Task<(int StatusCode, string Body)> SearchKnowledgeAsync(string queryString, CancellationToken cancellationToken);

    Task<(int StatusCode, string Body)> ComposeRcaAsync(string caseId, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/inspector/queue. Carries ?profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetInspectorQueueAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/inspector/queue/discard - takes waiting work out of the backlog (see DiscardInspectorQueueFunction).</summary>
    Task<(int StatusCode, string Body)> DiscardInspectorQueueAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/inspector/toasts?since={ISO8601}.</summary>
    Task<(int StatusCode, string Body)> GetInspectorToastsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/monkeys/{id}/run - a genuine on-demand monkey run (see RunMonkeyNowFunction), not a simulated toast.</summary>
    Task<(int StatusCode, string Body)> RunMonkeyNowAsync(string id, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/monkeys/{id}/pause - the durable pause switch (see PauseMonkeyFunction), not a browser-side flag.</summary>
    Task<(int StatusCode, string Body)> PauseMonkeyAsync(string id, string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/journeys/refresh so the portal Refresh button can pull latest
    /// App Insights into SQL, then re-read via GET /api/journeys/db.</summary>
    Task<(int StatusCode, string Body)> RefreshJourneysAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/analytics/metrics (dashboard points stored in SQL).</summary>
    Task<(int StatusCode, string Body)> GetDashboardMetricsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/latency (per-domain Latency Spike Monkey thresholds).</summary>
    Task<(int StatusCode, string Body)> GetLatencySpikeSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Forwards PUT /api/settings/latency/{domain} with the raw JSON body from the Settings tab.</summary>
    Task<(int StatusCode, string Body)> UpsertLatencySpikeSettingAsync(string domain, string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/latency/audit.</summary>
    Task<(int StatusCode, string Body)> GetLatencySpikeSettingsAuditAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/llm-client (Doctor Monkey's GitHub Copilot token/model).</summary>
    Task<(int StatusCode, string Body)> GetLlmClientSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Forwards PUT /api/settings/llm-client with the raw JSON body from the Settings tab.</summary>
    Task<(int StatusCode, string Body)> UpsertLlmClientSettingAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/llm-client/audit.</summary>
    Task<(int StatusCode, string Body)> GetLlmClientSettingsAuditAsync(CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/settings/llm-client/models - an ad-hoc token test, nothing is persisted.</summary>
    Task<(int StatusCode, string Body)> ListLlmClientModelsAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/slo (per-domain/stage/signal-type SLO definitions).</summary>
    Task<(int StatusCode, string Body)> GetSloDefinitionsAsync(CancellationToken cancellationToken);

    /// <summary>Forwards PUT /api/settings/slo with the raw JSON body from the Settings tab.</summary>
    Task<(int StatusCode, string Body)> UpsertSloDefinitionAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/slo/audit.</summary>
    Task<(int StatusCode, string Body)> GetSloDefinitionsAuditAsync(CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/notifications (Email/Teams channel configuration).</summary>
    Task<(int StatusCode, string Body)> GetAlertChannelSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Forwards PUT /api/settings/notifications with the raw JSON body from the Settings tab.</summary>
    Task<(int StatusCode, string Body)> UpsertAlertChannelSettingAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/notifications/audit.</summary>
    Task<(int StatusCode, string Body)> GetAlertChannelSettingsAuditAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forwards the editable-KQL and telemetry-source admin calls. One pair of generic methods
    /// instead of fourteen named ones: these routes only differ by path, and the portal is a proxy
    /// here - it has no opinion about the payload beyond stamping the signed-in user.
    /// </summary>
    Task<(int StatusCode, string Body)> GetSettingsCatalogAsync(string path, CancellationToken cancellationToken);

    /// <summary>Forwards a write to the same admin surface, carrying the settings write secret.</summary>
    Task<(int StatusCode, string Body)> WriteSettingsCatalogAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken);

    /// <summary>
    /// Forwards GET /api/onboarding/discover. Not routed through the catalog pair above because
    /// the onboarding endpoints are gated by X-Internal-Api-Secret, not the settings write secret
    /// (see TelemetryDiscoveryFunctions) - they make a real outbound query against a
    /// caller-named resource rather than writing a row.
    /// </summary>
    Task<(int StatusCode, string Body)> DiscoverTelemetryResourceAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/onboarding/draft - same gate, and persists nothing: the draft is returned for a human to edit and save.</summary>
    Task<(int StatusCode, string Body)> BuildCompanyProfileDraftAsync(string jsonBody, CancellationToken cancellationToken);
    /// <summary>Forwards GET /api/health/pillars (Spec 1000 pillar status computed by the backend monitors). Carries ?domain=&amp;profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetPillarHealthAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/health/anomalies. Carries ?pillar=&amp;domain=&amp;top=&amp;profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetKpiAnomaliesAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/health/kpis/{id}/buckets. Carries ?hours= through.</summary>
    Task<(int StatusCode, string Body)> GetKpiBucketsAsync(int id, string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/triage/investigations. Carries ?top=&amp;profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetTriageInvestigationsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/triage/investigations/{id}. Carries ?profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetTriageInvestigationAsync(Guid id, string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/triage/investigations (manual Triage Monkey run), adding X-Internal-Api-Secret server-side.</summary>
    Task<(int StatusCode, string Body)> SubmitTriageInvestigationAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/kpis. Carries ?profileKey= through.</summary>
    Task<(int StatusCode, string Body)> GetKpiDefinitionsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/settings/kpis/{id}/audit. Carries ?top= through.</summary>
    Task<(int StatusCode, string Body)> GetKpiDefinitionAuditAsync(int id, string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/settings/kpis, adding X-Settings-Write-Secret server-side.</summary>
    Task<(int StatusCode, string Body)> UpsertKpiDefinitionAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/daemons (one status card per Spec 1000 daemon).</summary>
    Task<(int StatusCode, string Body)> GetDaemonsAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/daemons/activity. Carries ?afterId=&amp;daemon=&amp;level=&amp;top= through.</summary>
    Task<(int StatusCode, string Body)> GetDaemonActivityAsync(string queryString, CancellationToken cancellationToken);

    /// <summary>Forwards GET /api/appinsights/tokens (metadata only, never a token or secret), adding X-Internal-Api-Secret server-side.</summary>
    Task<(int StatusCode, string Body)> GetAppInsightsTokensAsync(CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/appinsights/tokens (save/update a token and its refresh settings), adding X-Settings-Write-Secret server-side.</summary>
    Task<(int StatusCode, string Body)> SaveAppInsightsTokenAsync(string jsonBody, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/appinsights/tokens/{tenantKey}/deactivate, adding X-Settings-Write-Secret server-side.</summary>
    Task<(int StatusCode, string Body)> DeactivateAppInsightsTokenAsync(string tenantKey, CancellationToken cancellationToken);

    /// <summary>Forwards POST /api/appinsights/tokens/{tenantKey}/refresh (200 / 404 / 409 / 502), adding X-Settings-Write-Secret server-side.</summary>
    Task<(int StatusCode, string Body)> RefreshAppInsightsTokenAsync(string tenantKey, CancellationToken cancellationToken);
}

public sealed class JourneyMonitorApiClient(HttpClient httpClient, Microsoft.Extensions.Options.IOptions<JourneyMonitorApiOptions> apiOptions) : IJourneyMonitorApiClient
{
    public Task<(int StatusCode, string Body)> GetDbJourneysAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/journeys/db{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetExceptionsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/exceptions{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetOverviewAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/overview{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetClubsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/clubs{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetClubInventoryAsync(CancellationToken cancellationToken) =>
        GetAsync("/api/club-inventory", cancellationToken);

    public Task<(int StatusCode, string Body)> GetHotelBusinessHealthAsync(string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/hotels/business-health{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetMonkeysAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/monkeys{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetFindingsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/findings{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetFindingDoctorReportAsync(string id, string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/findings/{Uri.EscapeDataString(id)}/doctor-report{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetScoutPackagesAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/scout/packages{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetScoutPackageAsync(string id, string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/scout/packages/{Uri.EscapeDataString(id)}{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetScoutRunsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/scout/runs{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetScoutStatsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/scout/stats{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetScoutShadowAsync(string board, string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/scouts/{Uri.EscapeDataString(board)}/shadow{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetInspectorCasesAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/inspector/cases{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetInspectorCaseAsync(string id, string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/inspector/cases/{Uri.EscapeDataString(id)}{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    // Both carry the internal secret: the document quotes the investigation's raw synthesis, and
    // composing one spends a provider call.
    public Task<(int StatusCode, string Body)> GetRcaAsync(string caseId, string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/inspector/cases/{Uri.EscapeDataString(caseId)}/rca{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> ComposeRcaAsync(string caseId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/api/inspector/cases/{Uri.EscapeDataString(caseId)}/rca", cancellationToken, extraHeaders: InternalApiSecretHeader());

    // Carries the secret because the document titles name real incidents, even though the call
    // itself reads no content and spends nothing.
    public Task<(int StatusCode, string Body)> GetKnowledgeDocumentsAsync(string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/knowledge/documents{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetKnowledgeDocumentAsync(string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/knowledge/document{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> SearchKnowledgeAsync(string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/knowledge/search{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetInspectorQueueAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/inspector/queue{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> DiscardInspectorQueueAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/inspector/queue/discard", cancellationToken, jsonBody, InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetInspectorToastsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/inspector/toasts{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> RunMonkeyNowAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/api/monkeys/{Uri.EscapeDataString(id)}/run", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> PauseMonkeyAsync(string id, string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/api/monkeys/{Uri.EscapeDataString(id)}/pause", cancellationToken, jsonBody, InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> RefreshJourneysAsync(string queryString, CancellationToken cancellationToken) =>
        PostAsync($"/api/journeys/refresh{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetDashboardMetricsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/analytics/metrics{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetLatencySpikeSettingsAsync(CancellationToken cancellationToken) =>
        GetAsync("/api/settings/latency", cancellationToken);

    public Task<(int StatusCode, string Body)> UpsertLatencySpikeSettingAsync(string domain, string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"/api/settings/latency/{Uri.EscapeDataString(domain)}", cancellationToken, jsonBody, WriteSecretHeader());

    private Dictionary<string, string>? WriteSecretHeader()
    {
        var secret = apiOptions.Value.SettingsWriteSecret;
        return string.IsNullOrWhiteSpace(secret) ? null : new Dictionary<string, string> { ["X-Settings-Write-Secret"] = secret };
    }

    public Task<(int StatusCode, string Body)> GetLatencySpikeSettingsAuditAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/settings/latency/audit{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetLlmClientSettingsAsync(CancellationToken cancellationToken) =>
        GetAsync("/api/settings/llm-client", cancellationToken);

    public Task<(int StatusCode, string Body)> UpsertLlmClientSettingAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, "/api/settings/llm-client", cancellationToken, jsonBody, WriteSecretHeader());

    public Task<(int StatusCode, string Body)> GetLlmClientSettingsAuditAsync(CancellationToken cancellationToken) =>
        GetAsync("/api/settings/llm-client/audit", cancellationToken);

    public Task<(int StatusCode, string Body)> ListLlmClientModelsAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/settings/llm-client/models", cancellationToken, jsonBody, InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetSloDefinitionsAsync(CancellationToken cancellationToken) =>
        GetAsync("/api/settings/slo", cancellationToken);

    public Task<(int StatusCode, string Body)> UpsertSloDefinitionAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, "/api/settings/slo", cancellationToken, jsonBody, WriteSecretHeader());

    public Task<(int StatusCode, string Body)> GetSloDefinitionsAuditAsync(CancellationToken cancellationToken) =>
        GetAsync("/api/settings/slo/audit", cancellationToken);

    public Task<(int StatusCode, string Body)> GetAlertChannelSettingsAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/settings/notifications", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> UpsertAlertChannelSettingAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, "/api/settings/notifications", cancellationToken, jsonBody, WriteSecretHeader());

    public Task<(int StatusCode, string Body)> GetAlertChannelSettingsAuditAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/settings/notifications/audit", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetSettingsCatalogAsync(string path, CancellationToken cancellationToken) =>
        GetAsync(path, cancellationToken);

    public Task<(int StatusCode, string Body)> WriteSettingsCatalogAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken) =>
        SendAsync(method, path, cancellationToken, jsonBody, WriteSecretHeader());

    public Task<(int StatusCode, string Body)> DiscoverTelemetryResourceAsync(string queryString, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/onboarding/discover{queryString}", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> BuildCompanyProfileDraftAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/onboarding/draft", cancellationToken, jsonBody, InternalApiSecretHeader());
    public Task<(int StatusCode, string Body)> GetPillarHealthAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/health/pillars{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetKpiAnomaliesAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/health/anomalies{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetKpiBucketsAsync(int id, string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/health/kpis/{id}/buckets{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetTriageInvestigationsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/triage/investigations{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetTriageInvestigationAsync(Guid id, string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/triage/investigations/{id:D}{queryString}", cancellationToken);

    // Every accepted signal can cost an LLM run, so the Function gates it like RunMonkeyNow.
    public Task<(int StatusCode, string Body)> SubmitTriageInvestigationAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/triage/investigations", cancellationToken, jsonBody, InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> GetKpiDefinitionsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/settings/kpis{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetKpiDefinitionAuditAsync(int id, string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/settings/kpis/{id}/audit{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> UpsertKpiDefinitionAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/settings/kpis", cancellationToken, jsonBody, WriteSecretHeader());

    public Task<(int StatusCode, string Body)> GetDaemonsAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/daemons{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetDaemonActivityAsync(string queryString, CancellationToken cancellationToken) =>
        GetAsync($"/api/daemons/activity{queryString}", cancellationToken);

    public Task<(int StatusCode, string Body)> GetAppInsightsTokensAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/appinsights/tokens", cancellationToken, extraHeaders: InternalApiSecretHeader());

    public Task<(int StatusCode, string Body)> SaveAppInsightsTokenAsync(string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/appinsights/tokens", cancellationToken, jsonBody, WriteSecretHeader());

    public Task<(int StatusCode, string Body)> DeactivateAppInsightsTokenAsync(string tenantKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/api/appinsights/tokens/{Uri.EscapeDataString(tenantKey)}/deactivate", cancellationToken, extraHeaders: WriteSecretHeader());

    public Task<(int StatusCode, string Body)> RefreshAppInsightsTokenAsync(string tenantKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/api/appinsights/tokens/{Uri.EscapeDataString(tenantKey)}/refresh", cancellationToken, extraHeaders: WriteSecretHeader());

    private Dictionary<string, string>? InternalApiSecretHeader()
    {
        var secret = apiOptions.Value.InternalApiSecret;
        return string.IsNullOrWhiteSpace(secret) ? null : new Dictionary<string, string> { ["X-Internal-Api-Secret"] = secret };
    }

    private Task<(int StatusCode, string Body)> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, cancellationToken);

    private Task<(int StatusCode, string Body)> PostAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, cancellationToken);

    private async Task<(int StatusCode, string Body)> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken, string? jsonBody = null, IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(method, ResolveRequestUri(path));
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                request.Headers.Add(key, value);
            }
        }
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ((int)response.StatusCode, body);
    }

    /// <summary>
    /// Staging requests go to <see cref="JourneyMonitorApiOptions.StagingBaseUrl"/> when it is
    /// set. The chosen Functions host selects the database. Every other call stays on
    /// <see cref="JourneyMonitorApiOptions.BaseUrl"/>.
    /// </summary>
    private Uri ResolveRequestUri(string path)
    {
        var options = apiOptions.Value;
        var baseUrl = IsStagingRequest(path) && !string.IsNullOrWhiteSpace(options.StagingBaseUrl)
            ? options.StagingBaseUrl
            : options.BaseUrl;
        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
    }

    private static bool IsStagingRequest(string path)
    {
        var queryIndex = path.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= path.Length - 1) return false;

        var query = System.Web.HttpUtility.ParseQueryString(path[(queryIndex + 1)..]);
        var value = (query["env"] ?? query["environment"])?.Trim();
        return string.Equals(value, "staging", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "uat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "stage", StringComparison.OrdinalIgnoreCase);
    }
}
