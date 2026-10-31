namespace JourneyMonitor.Core.Abstractions;

/// <summary>Loads the raw (un-injected) KQL text for a domain. Implemented in Infrastructure (reads kql/*.txt).</summary>
public interface IKqlQueryTextProvider
{
    string GetJourneyKql(string domain);

    string GetAirJourneyKql();

    /// <summary>
    /// Per-transaction latency-spike KQL for the Latency Spike Monkey (purpose "latency_spikes",
    /// distinct from the full journey-reconstruction KQL above). Reuses the same stage classifier
    /// already embedded in the domain's journey KQL, scoped down to individual slow requests.
    /// Authored for "cars"/"hotels"/"cruises" today; "air" throws until its file is added (its
    /// journey KQL is a cross-resource multi-pass pipeline, not a flat per-request projection —
    /// see KqlQueryTextProvider.GetLatencySpikesKql's exception message for the current list).
    /// </summary>
    string GetLatencySpikesKql(string domain);

    /// <summary>Dashboard tile KQL for Analytics (copied from Azure, not the journey queries).</summary>
    string GetDashboardChartKql(string domain, string chart);

    /// <summary>
    /// Per-event exception KQL for incident ingestion. <c>let win</c> is replaced with the sync window.
    /// Browser ErrorEvent rows are excluded in the query and again by <c>IncidentIngestionPlanner</c>.
    /// </summary>
    string GetIncidentExceptionsKql();

    /// <summary>
    /// One of the Scout Monkey's two priority boards (see <c>ScoutBoard</c>: "latency" or
    /// "exception"). Derived from the Azure workbook "SRE Reliability Priorities" but partitioned
    /// per club instead of per vertical. Placeholders ({{TIME_START}}, {{TIME_END}},
    /// {{BUCKET_MINUTES}}, {{TAKE_PER_CLUB}}, {{VERTICAL_B64}}, {{CLUB_FILTER_B64}}) are
    /// substituted by <c>ScoutBoardService</c> - the Application Insights REST API does not
    /// understand the workbook's own parameter syntax.
    /// </summary>
    string GetScoutBoardKql(string board);

    /// <summary>
    /// Per-package drill-down: the individual requests/exceptions behind one board row. A plain
    /// filter scoped to a single bucket and a known set of operation ids, not an aggregation
    /// pipeline. Placeholders ({{OPERATION_IDS}}, {{TIME_START}}, {{TIME_END}}, {{MAX_ROWS}}) are
    /// substituted by <c>ScoutEvidenceCollector</c>.
    /// </summary>
    string GetScoutDrillDownKql();

    /// <summary>
    /// One of the Inspector Monkey LogsAgent's investigation queries ("club-baseline",
    /// "signature-breakdown", "client-breakdown"). Keyed rather than one method per query for the
    /// same reason as <see cref="GetScoutBoardKql"/>: the set grows as the agent learns new
    /// pivots, and the caller already knows which pivot it wants.
    ///
    /// These are NOT model-authored: the model picks the tool and a bounded number, the text
    /// comes from here and the placeholders ({{ENTITY_KEY_B64}}, {{TIME_START}}, {{TIME_END}},
    /// {{LOOKBACK_DAYS}}, {{BUCKET_MINUTES}}, {{CURRENT_EXCEPTIONS}}, {{MAX_ROWS}}) are
    /// substituted by the tool in <c>Monitoring/Inspector</c>.
    /// </summary>
    string GetInspectorLogsKql(string query);

    /// <summary>
    /// The text for a <see cref="KqlQueryKeys"/> key. Every method above resolves through this, so
    /// an implementation that overrides queries from the database only has to intercept one place.
    /// Throws for an unknown key rather than returning empty: a query that silently became blank
    /// would be a valid request returning nothing, which reads as "no problems found".
    /// </summary>
    string GetByKey(string queryKey);

    /// <summary>Every key this provider can serve, for the admin list and for "import built-ins".</summary>
    IReadOnlyCollection<string> KnownKeys { get; }
}
