/* =============================================================================
   arrivia Agentic Operations Portal — app.js
   Vanilla JS. No build step, no framework. Structure:
     1. Data-access layer (dataProvider)              — swap-in point for real APIs
     2. Application state
     3. Utility / formatting helpers
     4. Generic UI primitives (toast, modal, side panel, tabs, sortable tables)
     5. Tab renderers (Overview, Journeys, Health, Analytics, Monkeys, Exceptions)
     6. Demo scenario orchestration
     7. Bootstrap
   ========================================================================== */

/* -----------------------------------------------------------------------
   1. DATA-ACCESS LAYER
   ----------------------------------------------------------------------- */
/**
 * dataProvider is the single seam between the UI and its data sources.
 * Today every function reads a static local JSON file. To connect this
 * portal to real systems, replace the body of each function only —
 * every renderer below consumes the same shape of data either way.
 *
 * Suggested production sources per function are noted inline:
 *   - Azure Application Insights / Azure Monitor (metrics, availability)
 *   - Log Analytics / Kusto (KQL) for journeys, traces and exceptions
 *   - REST APIs owned by each domain team for live component state
 *   - GitHub (or another repository provider) for exception-to-code links
 *   - An incident-management system (e.g. ServiceNow, PagerDuty) for status
 *   - An agent orchestration service for Monkey state and actions
 */
/** Verticals the Domain filter exposes; "All" expands to exactly this list. */
const JOURNEY_DOMAINS = ['hotels', 'cruises'];
// Same values as JourneySync__InitialRowCap and JourneySync__FilteredRowCap on the Functions app.
const INIT_ROW_CAP = 50;
const FILTER_ROW_CAP = 200;

function selectedJourneyDomains() {
  const selectedDomain = state.filters.domain;
  if (selectedDomain === 'all') return JOURNEY_DOMAINS;
  return JOURNEY_DOMAINS.includes(selectedDomain) ? [selectedDomain] : [];
}

/** The global Domain/Club filters, for the endpoints that scope by them. Club is the id the
 *  dropdown sends, which is what every other filtered endpoint keys on - never the brand label. */
function monkeyFilterQuery() {
  const qs = new URLSearchParams();
  const domain = state.filters.domain;
  if (domain && domain !== 'all') qs.set('domain', domain);
  if (state.filters.club) qs.set('club', state.filters.club);
  const query = qs.toString();
  return query ? `?${query}` : '';
}

/** The agent cards are scoped by the same filters as everything else, so a filter change has to
 *  re-read them. Without this the Simian Army tab keeps showing unfiltered data until the next
 *  poll tick - stale and filtered look identical, which is the worst of the two. */
async function reloadMonkeysForFilters() {
  try {
    state.monkeys = await dataProvider.getMonkeys();
    const panel = document.getElementById('tab-monkeys');
    if (panel && !panel.hidden) renderMonkeysTab();
  } catch (err) {
    console.warn('monkeys could not be refreshed for the new filters', err);
  }
}

function journeyQueryParams(domain, { includeReadFilters, rowCap = state.rowCap } = {}) {
  const qs = new URLSearchParams({
    win: headerQueryWin(),
    domain,
    env: state.filters.environment || 'production',
    top: String(rowCap || INIT_ROW_CAP),
  });
  if (state.filters.domain === domain && state.filters.club) {
    qs.set('club', state.filters.club);
    const selected = (state.clubOptions || []).find((c) => c.value === state.filters.club);
    if (selected && selected.host) qs.set('host', selected.host);
    if (selected && selected.partnerId) qs.set('partner', selected.partnerId);
  }
  if (includeReadFilters) {
    if (state.filters.status && state.filters.status !== 'all') {
      qs.set('status', state.filters.status);
    }
    if (state.filters.id) {
      qs.set('id', state.filters.id);
    }
  }
  return qs;
}

async function fetchJsonPayload(url, { method = 'GET', domain } = {}) {
  let response;
  try {
    response = await fetch(url, { method });
  } catch (err) {
    err.domain = domain;
    err.networkFailure = true;
    throw err;
  }

  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const err = new Error(payload.error || `Request failed: ${response.status}`);
    err.domain = domain;
    throw err;
  }
  return payload;
}

async function refreshDomainJourneys(domain) {
  // SQL must retain enough rows for later filtering. The 50-row cap is only a first-paint
  // presentation optimization applied by GET /api/journeys/db.
  const qs = journeyQueryParams(domain, { rowCap: FILTER_ROW_CAP });
  return fetchJsonPayload(`/api/journeys/refresh?${qs.toString()}`, { method: 'POST', domain });
}

async function pullLatestJourneysFromAzure() {
  const domains = selectedJourneyDomains();
  if (domains.length === 0) return [];
  const settled = await Promise.allSettled(domains.map((domain) => refreshDomainJourneys(domain)));
  return settled
    .filter((result) => result.status === 'rejected')
    .map((result) => result.reason);
}

async function fetchDomainJourneys(domain) {
  const qs = journeyQueryParams(domain, { includeReadFilters: true });
  const payload = await fetchJsonPayload(`/api/journeys/db?${qs.toString()}`, { domain });

  const rows = payload.rows || [];
  const environment = payload.environment || state.filters.environment || 'production';
  const label = currentClubLabel();
  const clubLabels = domain === 'cruises' ? await cruiseClubLabels() : null;
  return {
    journeys: domain === 'air'
      ? mapAirRowsToJourneys(rows, label, environment)
      : mapV6RowsToJourneys(rows, label, payload.domain || domain, environment, clubLabels),
    meta: {
      source: payload.source || 'unknown',
      rowCount: payload.rowCount ?? rows.length,
      warning: payload.warning || '',
      cached: !!payload.cached,
      fetchedAt: payload.fetchedAt || '',
      clubId: payload.clubId || '',
      host: payload.host || '',
      domain: payload.domain || domain,
      win: payload.win || headerQueryWin(),
      environment,
      rowCap: payload.rowCap || state.rowCap || INIT_ROW_CAP,
    },
  };
}

const dataProvider = {
  async getJourneys() {
    const selectedDomain = state.filters.domain;
    const domains = selectedJourneyDomains();
    if (domains.length === 0) {
      state.journeysMeta = {
        source: 'none', rowCount: 0, warning: '', cached: false, fetchedAt: '',
      };
      return [];
    }

    const settled = await Promise.allSettled(domains.map((domain) => fetchDomainJourneys(domain)));
    const loaded = settled.filter((result) => result.status === 'fulfilled').map((result) => result.value);
    const failed = settled.filter((result) => result.status === 'rejected').map((result) => result.reason);

    // A direct file:// launch or stopped server can still show the committed
    // Hotel fixture. HTTP errors never fall back to another club's data.
    if (loaded.length === 0 && failed.every((err) => err.networkFailure) && domains.includes('hotels')) {
      console.warn('/api/journeys unreachable, reading demo fixture directly.', failed[0]);
      const fixture = await fetchJson('data/demo-journeys.json');
      const rows = fixture.rows || [];
      state.journeysMeta = {
        source: 'demo',
        rowCount: rows.length,
        warning: 'Demo mode: anonymized Hotel fixture. Start `node server.js` and run `az login` for all live verticals.',
        cached: false,
        fetchedAt: '',
        win: headerQueryWin(),
        environment: state.filters.environment,
      };
      return mapV6RowsToJourneys(rows, 'T-Mobile / T Life', 'hotels');
    }

    const warnings = [
      ...loaded.map(({ meta }) => meta.warning).filter(Boolean),
      ...failed.map((err) => `${domainLabel(err.domain)}: ${err.message || err}`),
    ];
    const metas = loaded.map(({ meta }) => meta);
    const sources = new Set(metas.map((meta) => meta.source));
    state.journeysMeta = {
      source: loaded.length === 0 ? 'error' : failed.length > 0 || sources.size > 1 ? 'mixed' : metas[0].source,
      rowCount: metas.reduce((sum, meta) => sum + meta.rowCount, 0),
      warning: warnings.join(' '),
      cached: loaded.length > 0 && metas.every((meta) => meta.cached),
      fetchedAt: metas.map((meta) => meta.fetchedAt).filter(Boolean).sort().at(-1) || '',
      domain: selectedDomain,
      win: headerQueryWin(),
      environment: state.filters.environment,
      rowCap: state.rowCap || INIT_ROW_CAP,
      domains: metas,
    };
    return loaded.flatMap(({ journeys }) => journeys);
  },
  async getTraces() {
    // Future: Azure Application Insights end-to-end transaction API
    return fetchJson('data/traces.json');
  },
  async getComponents() {
    // Future: Azure Monitor metrics + a service catalog / CMDB
    return fetchJson('data/components.json');
  },
  async getMonkeys() {
    // The real agents come from GetMonkeysFunction; the rest of the roster is still the original
    // mock (data/monkeys.json), merged here so the UI shows both. Never breaks if the API is down.
    const mock = await fetchJson('data/monkeys.json');
    try {
      const response = await fetch(`/api/monkeys${monkeyFilterQuery()}`);
      if (response.ok) {
        const real = await response.json();
        return [...real, ...mock];
      }
    } catch (err) {
      console.warn('/api/monkeys unreachable, showing only the mocked roster.', err);
    }
    return mock;
  },
  async getExceptions() {
    // Grouped incidents from the SQL incident schema (GET /api/exceptions).
    // Club is the incident grain. Member and URL are the latest occurrence.
    const qs = new URLSearchParams({
      win: headerQueryWin(),
      env: state.filters.environment || 'production',
    });
    const domain = state.filters.domain;
    if (domain && domain !== 'all') qs.set('domain', domain);
    if (state.filters.club) qs.set('club', state.filters.club);
    if (state.filters.id) qs.set('id', state.filters.id);
    try {
      const payload = await fetchJsonPayload(`/api/exceptions?${qs.toString()}`);
      const exceptions = payload.exceptions || [];
      state.exceptionsMeta = {
        source: payload.source || 'sql',
        warning: payload.warning || '',
        rowCount: payload.rowCount ?? exceptions.length,
        win: payload.win || headerQueryWin(),
      };
      return exceptions;
    } catch (err) {
      console.warn('/api/exceptions unreachable.', err);
      state.exceptionsMeta = {
        source: 'error',
        warning: err.message || 'Could not load exceptions.',
        rowCount: 0,
        win: headerQueryWin(),
      };
      return [];
    }
  },
  async getOverview() {
    const qs = new URLSearchParams({
      win: headerQueryWin(),
      env: state.filters.environment || 'production',
    });
    const domain = state.filters.domain;
    if (domain && domain !== 'all') qs.set('domain', domain);
    if (state.filters.club) qs.set('club', state.filters.club);
    if (state.filters.id) qs.set('id', state.filters.id);
    try {
      return await fetchJsonPayload(`/api/overview?${qs.toString()}`);
    } catch (err) {
      console.warn('/api/overview unreachable.', err);
      return { source: 'error', platformStatus: 'unknown', tiles: [], warning: err.message || 'Could not load overview.' };
    }
  },
  async getDemoJourneysById() {
    try {
      const journeys = await fetchJson('data/journeys.json');
      return Object.fromEntries(journeys.map((j) => [j.id, j]));
    } catch (err) {
      console.warn('Demo journey index unavailable for exception links.', err);
      return {};
    }
  },
  async getEvents() {
    // Future: Azure Monitor activity log / Log Analytics timeline query
    return fetchJson('data/events.json');
  },
  async getAnalytics() {
    // Remaining mock blocks (supplier table, insights) until those tiles are persisted.
    return fetchJson('data/analytics.json');
  },
  async getDashboardMetrics() {
    try {
      const qs = new URLSearchParams({
        win: headerQueryWin(),
        env: state.filters.environment || 'production',
      });
      if (state.filters.domain && state.filters.domain !== 'all') qs.set('domain', state.filters.domain);
      if (state.filters.club) qs.set('club', state.filters.club);
      const response = await fetch(`/api/analytics/metrics?${qs.toString()}`);
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) return [];
      return payload.points || [];
    } catch {
      return [];
    }
  },
  async getLatencySettings() {
    try {
      const response = await fetch('/api/settings/latency');
      if (!response.ok) return [];
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/latency unreachable.', err);
      return [];
    }
  },
  async getLatencySettingsAudit() {
    try {
      const response = await fetch('/api/settings/latency/audit');
      if (!response.ok) return [];
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/latency/audit unreachable.', err);
      return [];
    }
  },
  async saveLatencySetting(domain, payload) {
    const response = await fetch(`/api/settings/latency/${encodeURIComponent(domain)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(payload),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
  async getLlmClientSettings() {
    try {
      const response = await fetch('/api/settings/llm-client');
      if (!response.ok) return null;
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/llm-client unreachable.', err);
      return null;
    }
  },
  async getLlmClientSettingsAudit() {
    try {
      const response = await fetch('/api/settings/llm-client/audit');
      if (!response.ok) return [];
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/llm-client/audit unreachable.', err);
      return [];
    }
  },
  async saveLlmClientSetting(payload) {
    const response = await fetch('/api/settings/llm-client', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(payload),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
  async listLlmClientModels(probe) {
    const response = await fetch('/api/settings/llm-client/models', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(probe),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body.models || [];
  },
  async getSloDefinitions() {
    try {
      const response = await fetch('/api/settings/slo');
      if (!response.ok) return [];
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/slo unreachable.', err);
      return [];
    }
  },
  async getSloDefinitionsAudit() {
    try {
      const response = await fetch('/api/settings/slo/audit');
      if (!response.ok) return [];
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/slo/audit unreachable.', err);
      return [];
    }
  },
  async saveSloDefinition(payload) {
    const response = await fetch('/api/settings/slo', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(payload),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
  async getAlertChannelSettings() {
    try {
      const response = await fetch('/api/settings/notifications');
      if (!response.ok) return null;
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/notifications unreachable.', err);
      return null;
    }
  },
  async getAlertChannelSettingsAudit() {
    try {
      const response = await fetch('/api/settings/notifications/audit');
      if (!response.ok) return [];
      return await response.json();
    } catch (err) {
      console.warn('/api/settings/notifications/audit unreachable.', err);
      return [];
    }
  },
  async saveAlertChannelSettings(payload) {
    const response = await fetch('/api/settings/notifications', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(payload),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
  /** Triggers a real monkey run (see RunMonkeyNowFunction) - real detection, real LLM diagnoses,
   *  real Teams/Email notifications for the SLO Burn-Rate Monkey. Not a simulated toast. */
  async runMonkeyNow(monkeyId) {
    const response = await fetch(`/api/monkeys/${encodeURIComponent(monkeyId)}/run`, {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': await getAntiforgeryToken() },
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
  /** Really stops/restarts an agent server-side. Returns { id, paused, updatedAt } - the caller
   *  must re-read /api/monkeys rather than trust a locally flipped field. */
  async setMonkeyPaused(monkeyId, paused) {
    const response = await fetch(`/api/monkeys/${encodeURIComponent(monkeyId)}/pause`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify({ paused }),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
  /** Takes waiting packages out of the Inspector's backlog. Passing no ids clears everything that
   *  is waiting; nothing is deleted server-side, the rows become 'discarded' with the reason. */
  async discardInspectorQueue(ids, reason) {
    const response = await fetch('/api/inspector/queue/discard', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(ids && ids.length ? { ids, reason } : { reason }),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
    return body;
  },
};

// Cached per page load - only meaningful when Entra auth is configured (the local anonymous mode
// disables antiforgery validation entirely, see Program.cs), but harmless to always fetch/send.
let cachedAntiforgeryToken = null;
async function getAntiforgeryToken() {
  if (cachedAntiforgeryToken) return cachedAntiforgeryToken;
  try {
    const response = await fetch('/api/antiforgery-token');
    const payload = await response.json().catch(() => ({}));
    cachedAntiforgeryToken = payload.token || '';
  } catch {
    cachedAntiforgeryToken = '';
  }
  return cachedAntiforgeryToken;
}

async function fetchJson(path) {
  const response = await fetch(path);
  if (!response.ok) throw new Error(`Failed to load ${path}: ${response.status}`);
  return response.json();
}

/* -----------------------------------------------------------------------
   2. APPLICATION STATE
   ----------------------------------------------------------------------- */
const state = {
  journeys: [], traces: {}, components: [], monkeys: [], exceptions: [],
  overview: null,
  exceptionsMeta: { source: '', warning: '', rowCount: 0, win: '' },
  /** Mock journey catalog used only to label exception → journey links when live rows are absent. */
  demoJourneysById: {},
  selectedExceptionId: null,
  events: [], analytics: null, dashboardMetrics: [], hotelDomainComparison: null, airDomainComparison: null, cruiseDomainComparison: null, carsDomainComparison: null,
  latencySettings: [], latencySettingsAudit: [],
  llmClientSetting: null, llmClientSettingAudit: [],
  sloDefinitions: [], sloDefinitionsAudit: [],
  alertChannelSetting: null, alertChannelSettingAudit: [],

  /* --- Company profiles (multi-tenant vocabulary) ---------------------- */
  /** Every stored profile, enabled or not - the Settings panel edits disabled ones too. */
  companyProfiles: [],
  /** The SERVER's pointer, re-read after every switch. Never mirrored into localStorage: a
   *  browser-held copy would let two operators disagree about who is being monitored. */
  activeCompany: { profileKey: null, updatedAt: null, updatedBy: null },
  /** Set by a 503 from PUT .../active - monitoring.ActiveCompanyProfile is not provisioned on
   *  this deployment's database, so the switch is refused rather than faked. */
  companySwitchUnavailable: false,
  /** RCA per investigation id. undefined = never asked, null = none written yet, object = the document. */
  rcaByCase: {},
  /** Inventory of the imported corpus. Fetched on demand - it changes only when an import runs. */
  knowledgeCorpus: null,
  /** Memory Bank view: client-side filter, plus the last meaning-based search (which costs a call). */
  memory: { filter: '', type: 'all', search: null },
  activeTab: 'overview',
  domainComparisonStale: true,
  filters: { domain: 'all', status: 'all', region: 'all', club: '', id: '', timeRange: '15m', environment: 'production' },
  /** Club dropdown rows for the current domain, including host and ICE partner id. */
  clubOptions: [],
  sort: { journeys: { key: 'timestamp', dir: 'desc' }, exceptions: { key: 'severity', dir: 'desc' }, clubMap: { key: 'clubId', dir: 'asc' } },
  clubInventory: [],
  clubInventoryMeta: { source: '', warning: '', rowCount: 0 },
  clubInventoryLoaded: false,
  clubMapFilters: { brand: '', product: '', status: '', platform: '', q: '' },
  clubMapFiltersWired: false,
  autoRefreshTimer: null,
  /** Scout cycle whose handoffs the Activity Timeline is pinned to, or null for all of them. */
  scoutCycleFilter: null,
  /** Which scout's evidence modal is open - the two share every renderer, so the id has to be carried. */
  scoutModalMonkeyId: null,
  demoRunning: false,
  demoTick: 0,
  /** 15 on first paint; widens to 100 after the user changes a query filter. */
  rowCap: INIT_ROW_CAP,
  journeysMeta: { source: 'snapshot', rowCount: 0, warning: '' },
  /** True while a filter change is waiting on journeys, exceptions, or overview. */
  tablesLoading: false,
  /** Club dropdown stays disabled for All and when the club list cannot load. */
  clubSelectDisabled: true,

  /* --- Inspector Monkey (L3 analysis) --------------------------------- */
  /** Which half of the modal is up: waiting|running|closed. The queue and the history answer
   *  different questions and stopped sharing a column set. */
  inspectorView: 'waiting',
  /** Outcome filter, only meaningful inside the closed view: all|escalated|resolved|closed_no_issue|failed. */
  inspectorFilter: 'all',
  /** Waiting work is grouped by signature by default - twelve rows of the same problem read as twelve problems. */
  inspectorGrouped: true,
  /** Signature keys whose group is expanded. Survives a re-render, which replaces the whole body. */
  inspectorOpenGroups: [],
  /** True while a discard is in flight; every queue control is disabled so a double click cannot double-send. */
  inspectorQueueBusy: false,
  /** Result of the last discard, shown inline rather than as a toast that outlives the modal. */
  inspectorQueueNotice: null,
  /** Last GET /api/inspector/queue payload; the card's counters cannot name the waiting packages. */
  inspectorQueue: null,
  /** Case id whose detail the modal is showing, or null while the list is up. */
  inspectorDetailCaseId: null,
  /** Lazy-loaded GET /api/inspector/cases/{id} payloads, keyed by case id. */
  inspectorCaseDetails: {},
  /** Graph node selection: only this agent's turns are kept in the rail. Null shows every turn. */
  inspectorAgentFilter: null,
  /** Watermark for GET /api/inspector/toasts; in memory on purpose, a reload replays the short window. */
  inspectorToastSince: null,
  /** Toast ids already rendered, so an overlapping poll window never shows one twice. */
  inspectorSeenToastIds: new Set(),
};

/* -----------------------------------------------------------------------
   3. UTILITY / FORMATTING HELPERS
   ----------------------------------------------------------------------- */
function escapeHtml(str) {
  return String(str ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function formatDuration(ms) {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  const m = Math.floor(s / 60);
  const rem = Math.round(s % 60);
  return `${m}m ${rem}s`;
}

function formatRelativeTime(isoString) {
  const diffMs = Date.now() - new Date(isoString).getTime();
  const mins = Math.round(diffMs / 60000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.round(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.round(hrs / 24);
  return `${days}d ago`;
}

function formatClock(date) {
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function domainLabel(domain) {
  return { hotels: 'Hotels', air: 'Air', cruises: 'Cruises', cars: 'Cars', shared: 'Shared Platform' }[domain] || domain;
}

/** Normalizes a component/domain health status into one of 4 canonical states. */
function normalizeStatus(status) {
  const s = (status || '').toLowerCase();
  if (['healthy', 'degraded', 'critical', 'unknown'].includes(s)) return s;
  return 'unknown';
}

function statusBadge(status) {
  const s = normalizeStatus(status);
  const label = s.charAt(0).toUpperCase() + s.slice(1);
  return `<span class="status-badge status-badge--${s}">${label}</span>`;
}

function severityBadge(sev) {
  const key = String(sev || '').toLowerCase();
  const cls = ['critical', 'high', 'medium', 'low'].includes(key) ? key : 'medium';
  return `<span class="status-badge status-badge--sev-${cls}">${escapeHtml(sev)}</span>`;
}

/* -----------------------------------------------------------------------
   4. GENERIC UI PRIMITIVES
   ----------------------------------------------------------------------- */
function showToast(title, message, tone = 'info') {
  const container = document.getElementById('toast-container');
  const toast = document.createElement('div');
  toast.className = `toast toast--${tone}`;
  toast.innerHTML = `<strong>${escapeHtml(title)}</strong>${escapeHtml(message)}`;
  container.appendChild(toast);
  setTimeout(() => {
    toast.style.opacity = '0';
    toast.style.transition = 'opacity 200ms ease';
    setTimeout(() => toast.remove(), 220);
  }, 4200);
}

function openSidePanel(title, bodyHtml) {
  document.getElementById('side-panel-title').textContent = title;
  document.getElementById('side-panel-body').innerHTML = bodyHtml;
  document.getElementById('side-panel-overlay').hidden = false;
  const panel = document.getElementById('side-panel');
  panel.setAttribute('aria-hidden', 'false');
  requestAnimationFrame(() => panel.classList.add('is-open'));
}

function scrollDetailsToTop() {
  window.scrollTo(0, 0);
  const panelBody = document.getElementById('side-panel-body');
  if (panelBody) panelBody.scrollTop = 0;
}

function closeSidePanel() {
  const panel = document.getElementById('side-panel');
  panel.classList.remove('is-open');
  panel.setAttribute('aria-hidden', 'true');
  state.selectedExceptionId = null;
  document.querySelectorAll('#exceptions-tbody tr.is-selected').forEach((row) => row.classList.remove('is-selected'));
  setTimeout(() => { document.getElementById('side-panel-overlay').hidden = true; }, 200);
}

function openModal(title, bodyHtml, { wide = false } = {}) {
  document.getElementById('modal-title').textContent = title;
  document.getElementById('modal-body').innerHTML = bodyHtml;
  document.querySelector('#modal-overlay .modal').classList.toggle('modal--wide', wide);
  document.getElementById('modal-overlay').hidden = false;
}

function closeModal() {
  document.getElementById('modal-overlay').hidden = true;
}

function wireOverlayDismiss() {
  document.getElementById('side-panel-overlay').addEventListener('click', closeSidePanel);
  document.getElementById('side-panel-close').addEventListener('click', closeSidePanel);
  document.getElementById('modal-overlay').addEventListener('click', (e) => {
    if (e.target.id === 'modal-overlay') closeModal();
  });
  document.getElementById('modal-close').addEventListener('click', closeModal);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { closeSidePanel(); closeModal(); }
  });

  // Delegated so it keeps working no matter how many times #modal-body's innerHTML is replaced.
  const toggleEvidenceDetail = (header) => {
    const detail = header.nextElementSibling;
    if (!detail || !detail.classList.contains('evidence-item__detail')) return;
    detail.hidden = !detail.hidden;
    header.setAttribute('aria-expanded', String(!detail.hidden));
    const chevron = header.querySelector('.evidence-item__chevron');
    if (chevron) chevron.textContent = detail.hidden ? '▾' : '▴';
  };
  const toggleLlmLog = (button) => {
    const logContainer = button.nextElementSibling;
    if (!logContainer || !logContainer.hasAttribute('data-llm-log')) return;
    logContainer.hidden = !logContainer.hidden;
    button.setAttribute('aria-expanded', String(!logContainer.hidden));
    const chevron = button.querySelector('.evidence-item__chevron');
    if (chevron) chevron.textContent = logContainer.hidden ? '▾' : '▴';

    // Lazy-load the first time it's shown, not when the parent item expands.
    if (!logContainer.hidden && !logContainer.dataset.loaded) {
      logContainer.dataset.loaded = 'true';
      const item = button.closest('.evidence-item[data-finding-id]');
      if (item) loadLlmCallLog(item.dataset.findingId, logContainer);
      const pkg = button.closest('.evidence-item[data-package-id]');
      if (pkg) loadScoutPackageDetail(pkg.dataset.packageId, logContainer);
    }
  };
  document.getElementById('modal-body').addEventListener('click', (e) => {
    // Clearing the cycle filter from inside the modal has to refresh both the modal and the card
    // behind it, otherwise the two disagree about what is being shown.
    if (e.target.closest('[data-action="evidence-show-all"]')) {
      state.scoutCycleFilter = null;
      const scout = state.monkeys.find((m) => m.id === state.scoutModalMonkeyId);
      if (scout) openScoutEvidenceModal(scout);
      renderMonkeysTab();
      return;
    }
    if (handleInspectorModalClick(e)) return;
    const llmLogButton = e.target.closest('[data-action="toggle-llm-log"]');
    if (llmLogButton) { toggleLlmLog(llmLogButton); return; }
    const header = e.target.closest('.evidence-item__header[data-expandable]');
    if (header) toggleEvidenceDetail(header);
  });
  // role="button"/tabindex="0" headers advertise keyboard support - Enter/Space must activate
  // them the same way a click does, not just visually focus.
  document.getElementById('modal-body').addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    // Rows and graph nodes are role="button" too, so they answer the keyboard the same way.
    if (e.target.closest('[data-insp-case], [data-insp-agent], [data-insp-group]')) {
      e.preventDefault();
      handleInspectorModalClick(e);
      return;
    }
    const header = e.target.closest('.evidence-item__header[data-expandable]');
    if (!header) return;
    e.preventDefault();
    toggleEvidenceDetail(header);
  });
}

/** Every Inspector control inside the modal routes through here, because the body is replaced with
 *  innerHTML on each filter change and per-render listeners would die with it.
 *  Returns true when the event was handled. */
function handleInspectorModalClick(e) {
  const composeButton = e.target.closest('[data-action="compose-rca"]');
  if (composeButton) {
    composeRca(composeButton.getAttribute('data-case'));
    return true;
  }
  const viewButton = e.target.closest('[data-insp-view]');
  if (viewButton) {
    state.inspectorView = viewButton.dataset.inspView;
    state.inspectorQueueNotice = null;
    refreshInspectorModal();
    return true;
  }
  const groupedButton = e.target.closest('[data-insp-grouped]');
  if (groupedButton) {
    state.inspectorGrouped = groupedButton.dataset.inspGrouped === '1';
    refreshInspectorModal();
    return true;
  }
  // Checked before the group header: the discard buttons live inside it, and expanding a group on
  // the way to deleting it would be a confusing way to destroy work.
  const discardAll = e.target.closest('[data-insp-discard-group]');
  if (discardAll) {
    discardInspectorGroup(discardAll.getAttribute('data-insp-discard-group'));
    return true;
  }
  const discardOne = e.target.closest('[data-insp-discard]');
  if (discardOne) {
    discardInspectorItem(discardOne.getAttribute('data-insp-discard'));
    return true;
  }
  if (e.target.closest('[data-action="inspector-clear-queue"]')) {
    confirmClearInspectorQueue();
    return true;
  }
  const groupHeader = e.target.closest('[data-insp-group]');
  if (groupHeader) {
    const key = groupHeader.getAttribute('data-insp-group');
    const open = new Set(state.inspectorOpenGroups || []);
    if (!open.delete(key)) open.add(key);
    state.inspectorOpenGroups = [...open];
    refreshInspectorModal();
    return true;
  }
  const filterButton = e.target.closest('[data-insp-filter]');
  if (filterButton) {
    state.inspectorFilter = filterButton.dataset.inspFilter;
    refreshInspectorModal();
    return true;
  }
  if (e.target.closest('[data-action="inspector-back"]')) {
    state.inspectorDetailCaseId = null;
    state.inspectorAgentFilter = null;
    refreshInspectorModal();
    return true;
  }
  if (e.target.closest('[data-action="inspector-clear-agent"]')) {
    state.inspectorAgentFilter = null;
    refreshInspectorModal();
    return true;
  }
  const node = e.target.closest('[data-insp-agent]');
  if (node) {
    const agent = node.getAttribute('data-insp-agent');
    state.inspectorAgentFilter = state.inspectorAgentFilter === agent ? null : agent;
    refreshInspectorModal();
    return true;
  }
  const row = e.target.closest('[data-insp-case]');
  if (row) {
    openInspectorCaseDetail(row.getAttribute('data-insp-case'));
    return true;
  }
  return false;
}

function wireTabs() {
  const buttons = document.querySelectorAll('.tab-nav__item');
  buttons.forEach((btn) => {
    btn.addEventListener('click', () => activateTab(btn.dataset.tab));
  });
}

function activateTab(tabName) {
  state.activeTab = tabName;
  document.querySelectorAll('.tab-nav__item').forEach((b) => {
    const active = b.dataset.tab === tabName;
    b.classList.toggle('is-active', active);
    b.setAttribute('aria-selected', String(active));
  });
  document.querySelectorAll('.tab-panel').forEach((panel) => {
    const active = panel.id === `tab-${tabName}`;
    panel.classList.toggle('is-active', active);
    panel.hidden = !active;
  });
  if (ensureDomainComparison()) renderAnalyticsTab();
  // Loaded on first visit only: the corpus changes when an import runs, not while browsing.
  if (tabName === 'memory') renderMemoryBankTab();
  if (tabName === 'club-map') ensureClubMapTab();
}

/** Attaches generic click-to-sort behaviour to a table's <th data-sort>. */
function wireSortableTable(tableId, sortKey, rows, renderFn) {
  const table = document.getElementById(tableId);
  table.querySelectorAll('th[data-sort]').forEach((th) => {
    th.addEventListener('click', () => {
      const key = th.dataset.sort;
      const cur = state.sort[sortKey];
      if (cur.key === key) cur.dir = cur.dir === 'asc' ? 'desc' : 'asc';
      else { cur.key = key; cur.dir = 'asc'; }
      renderFn();
    });
  });
}

function sortRows(rows, key, dir, accessor) {
  const sorted = [...rows].sort((a, b) => {
    const va = accessor(a, key);
    const vb = accessor(b, key);
    if (va < vb) return -1;
    if (va > vb) return 1;
    return 0;
  });
  if (dir === 'desc') sorted.reverse();
  return sorted;
}

/* -----------------------------------------------------------------------
   FILTERING
   ----------------------------------------------------------------------- */
function matchesGlobalFilters(item, options) {
  const f = state.filters;
  const ignoreTime = !!(options && typeof options === 'object' && options.ignoreTime);
  if (f.domain !== 'all' && item.domain && item.domain !== f.domain) return false;
  if (f.region !== 'all' && item.region && item.region !== f.region) return false;
  if (f.status !== 'all') {
    const health = item.outcome
      ? mapOutcomeToHealth(item.outcome)
      : normalizeStatus(item.status);
    if (health !== f.status) return false;
  }
  const ignoreId = !!(options && typeof options === 'object' && options.ignoreId);
  if (!ignoreId && f.id) {
    const needle = f.id.toLowerCase();
    const haystack = [item.id, item.bookingId, item.correlationId].filter(Boolean).join(' ').toLowerCase();
    if (!haystack.includes(needle)) return false;
  }
  if (!ignoreTime) {
    const windowMs = windowToMs(f.timeRange);
    if (windowMs && item.timestamp) {
      const seen = new Date(item.timestamp).getTime();
      if (!Number.isNaN(seen) && Date.now() - seen > windowMs) return false;
    }
  }
  return true;
}

/** T-Mobile stays the default so the demo opens on the club we know. */
const DEFAULT_CLUB_ID = '15639';

function currentClubLabel() {
  const select = document.getElementById('filter-club');
  const opt = select && select.selectedOptions ? select.selectedOptions[0] : null;
  if (opt && opt.value) return opt.textContent;
  return 'T-Mobile / T Life';
}

/** Partner id → dropdown label, so an unfiltered cruise list can name each row's own brand. */
const cruiseClubLabelCache = new Map();

async function cruiseClubLabels() {
  if (state.filters.domain === 'cruises' && state.clubOptions.length > 0) {
    return Object.fromEntries(
      state.clubOptions
        .filter((club) => club && club.value)
        .map((club) => [String(club.value), club.label || String(club.value)]),
    );
  }

  const key = state.filters.environment || 'production';
  if (cruiseClubLabelCache.has(key)) return cruiseClubLabelCache.get(key);

  try {
    const envQuery = key !== 'production' ? `&env=${encodeURIComponent(key)}` : '';
    const payload = await fetchJson(`/api/clubs?domain=cruises&win=2h${envQuery}`);
    const labels = {};
    for (const club of payload.clubs || []) {
      if (club && club.value) labels[String(club.value)] = club.label || String(club.value);
    }
    if (payload.source === 'database' && Object.keys(labels).length > 0) cruiseClubLabelCache.set(key, labels);
    return labels;
  } catch (err) {
    console.warn('/api/clubs cruise label lookup unavailable', err);
    return {};
  }
}

function renderClubSelect(payload) {
  const select = document.getElementById('filter-club');
  if (!select) return;
  const clubs = payload.clubs || [];
  const placeholder = payload.placeholder || 'All hotel clubs';
  state.clubSelectDisabled = !!payload.disabled;
  select.disabled = state.tablesLoading || state.clubSelectDisabled;

  state.clubOptions = clubs;
  if (clubs.length === 0) {
    select.innerHTML = `<option value="">${escapeHtml(placeholder)}</option>`;
    state.filters.club = '';
    return;
  }

  const domainDefault = ['air', 'cruises'].includes(state.filters.domain) ? '308' : DEFAULT_CLUB_ID;
  const wanted = clubs.some((c) => c.value === state.filters.club)
    ? state.filters.club
    : clubs.some((c) => c.value === domainDefault) ? domainDefault : clubs[0].value;

  select.innerHTML = clubs
    .map((c) => `<option value="${escapeHtml(c.value)}">${escapeHtml(c.label)}</option>`)
    .join('');
  select.value = wanted;
  state.filters.club = wanted;
}

async function loadClubsForDomain(domain) {
  try {
    const payload = await fetchJson(`/api/clubs?domain=${encodeURIComponent(domain)}&win=${
      usesHeaderWin(domain) ? encodeURIComponent(headerQueryWin()) : '2h'
    }${
      state.filters.environment && state.filters.environment !== 'production'
        ? `&env=${encodeURIComponent(state.filters.environment)}`
        : ''
    }&fresh=1`);
    renderClubSelect(payload);
  } catch (err) {
    console.warn('/api/clubs unavailable', err);
    renderClubSelect({
      disabled: domain === 'all' || domain === 'shared',
      clubs: [],
      placeholder: 'Could not load clubs',
    });
  }
}

function shouldQueryJourneys(domain = state.filters.domain) {
  return domain === 'all' || JOURNEY_DOMAINS.includes(domain);
}

/** First user-driven filter change switches the KQL sample from 50 → 200. */
function markFiltersApplied() {
  state.rowCap = FILTER_ROW_CAP;
}

/**
 * Rebuilds the rows from the journeys the Domain filter already loaded, so the
 * Analytics tab never runs a KQL query for a vertical the user did not select.
 */
function ensureDomainComparison() {
  const needed = shouldRefreshDomainComparison({
    analyticsVisible: state.activeTab === 'analytics',
    stale: state.domainComparisonStale,
  });
  if (!needed) return false;

  state.domainComparisonStale = false;
  const wanted = comparisonDomains(state.filters.domain);
  state.hotelDomainComparison = wanted.includes('hotels')
    ? calculateHotelDomainComparison(state.journeys) : null;
  state.airDomainComparison = wanted.includes('air')
    ? calculateAirDomainComparison(state.journeys) : null;
  state.cruiseDomainComparison = wanted.includes('cruises')
    ? calculateCruiseDomainComparison(state.journeys) : null;
  state.carsDomainComparison = wanted.includes('cars')
    ? calculateCarsDomainComparison(state.journeys) : null;
  return true;
}

/** A new filter or refresh invalidates the rows; they rebuild on the Analytics tab. */
function invalidateDomainComparison() {
  state.domainComparisonStale = true;
  if (ensureDomainComparison()) renderAnalyticsTab();
}

/**
 * Reloads journeys for the current filters. pull re-runs the domain KQL in Application Insights
 * with the club selected in the dropdown, then reads that snapshot back.
 */
let filterRequestGeneration = 0;
let pullBusyCount = 0;

function loadingTableRow(colspan) {
  return `<tr class="is-loading"><td colspan="${colspan}"><div class="state-block" role="status"><div class="spinner" aria-hidden="true"></div>Loading…</div></td></tr>`;
}

function markTableBusy(table, busy) {
  if (table) table.setAttribute('aria-busy', busy ? 'true' : 'false');
}

const FILTER_CONTROL_IDS = [
  'filter-domain',
  'filter-status',
  'filter-club',
  'filter-region',
  'filter-id',
  'filter-clear',
  'env-select',
  'time-range-select',
];

/** Disables the filter bar for the same interval the table spinner is visible. */
function setFiltersLocked(locked) {
  FILTER_CONTROL_IDS.forEach((id) => {
    const el = document.getElementById(id);
    if (!el) return;
    if (locked) {
      if (el.dataset.lockWasDisabled == null) el.dataset.lockWasDisabled = String(el.disabled);
      el.disabled = true;
      return;
    }
    const wasDisabled = el.dataset.lockWasDisabled === 'true';
    delete el.dataset.lockWasDisabled;
    el.disabled = id === 'filter-club' ? state.clubSelectDisabled : wasDisabled;
  });
  const bar = document.querySelector('.filter-bar');
  if (bar) bar.setAttribute('aria-busy', locked ? 'true' : 'false');
}

/** Replaces filter-driven tables with a spinner until the in-flight request renders. */
function paintDashboardChartsLoading() {
  document.querySelectorAll('[data-dashboard-chart]').forEach((card) => {
    if (card.classList.contains('chart-card--under-construction')) return;
    card.setAttribute('aria-busy', 'true');
    const svg = card.querySelector('svg');
    const legend = card.querySelector('.chart-legend');
    if (svg) svg.innerHTML = '';
    if (legend) {
      legend.innerHTML = '<div class="state-block" role="status"><div class="spinner" aria-hidden="true"></div>Loading…</div>';
    }
  });
}

function showDataTablesLoading() {
  state.tablesLoading = true;
  setFiltersLocked(true);
  markTableBusy(document.getElementById('journeys-table'), true);
  markTableBusy(document.getElementById('exceptions-table'), true);
  markTableBusy(document.getElementById('domain-comparison-table'), true);
  const journeysBody = document.getElementById('journeys-tbody');
  const exceptionsBody = document.getElementById('exceptions-tbody');
  const comparisonBody = document.querySelector('#domain-comparison-table tbody');
  if (journeysBody) journeysBody.innerHTML = loadingTableRow(10);
  if (exceptionsBody) exceptionsBody.innerHTML = loadingTableRow(11);
  if (comparisonBody) comparisonBody.innerHTML = loadingTableRow(5);
  state.dashboardChartsLoading = true;
  paintDashboardChartsLoading();
}

function beginFilterRequest() {
  showDataTablesLoading();
  filterRequestGeneration += 1;
  return filterRequestGeneration;
}

function isCurrentFilterRequest(generation) {
  return generation === filterRequestGeneration;
}

async function reloadJourneysForClub({ pull = false, generation } = {}) {
  const stillCurrent = () => generation == null || generation === filterRequestGeneration;
  if (pull) {
    pullBusyCount += 1;
    setRefreshBusy(true);
  }
  const label = document.getElementById('filter-club');
  const shown = label && label.selectedOptions[0] ? label.selectedOptions[0].textContent : state.filters.club;
  try {
    let pullFailures = [];
    if (pull) {
      pullFailures = await pullLatestJourneysFromAzure();
    }
    if (!stillCurrent()) return;
    const exceptionsPromise = dataProvider.getExceptions();
    const overviewPromise = dataProvider.getOverview();
    const metricsPromise = dataProvider.getDashboardMetrics();
    const journeys = await dataProvider.getJourneys();
    const exceptions = await exceptionsPromise;
    const overview = await overviewPromise;
    const dashboardMetrics = await metricsPromise;
    if (!stillCurrent()) return;
    state.journeys = journeys;
    state.exceptions = exceptions;
    state.overview = overview;
    state.dashboardMetrics = dashboardMetrics;
    invalidateDomainComparison();
    const m = state.journeysMeta || {};
    const win = m.win || headerQueryWin();
    const pullWarn = pullFailures
      .map((err) => `${domainLabel(err.domain) || 'Journeys'}: ${err.message || err}`)
      .join(' ');
    if (pullFailures.length && (m.source === 'error' || m.rowCount === 0)) {
      showToast('Query failed', pullWarn || `Could not load journeys for ${shown || domainLabel(state.filters.domain)}.`, 'critical');
    } else if (m.source === 'error' || m.rowCount === 0) {
      showToast('No journeys', `${shown || domainLabel(state.filters.domain)}: no journeys in the last ${win}.`, 'info');
    } else {
      showToast(
        'Journeys updated',
        `${m.rowCount} journeys (${m.source}) for last ${win}, capped at ${state.rowCap}.${pullWarn ? ` ${pullWarn}` : ''}`,
        pullFailures.length ? 'info' : m.source === 'live' ? 'success' : 'info',
      );
    }
  } catch (err) {
    if (!stillCurrent()) return;
    console.error(err);
    showToast('Query failed', `Could not load journeys for ${shown || domainLabel(state.filters.domain)}.`, 'critical');
  } finally {
    if (pull) {
      pullBusyCount = Math.max(0, pullBusyCount - 1);
      if (pullBusyCount === 0) setRefreshBusy(false);
    }
  }
}

function wireFilterBar() {
  const domain = document.getElementById('filter-domain');
  const status = document.getElementById('filter-status');
  const club = document.getElementById('filter-club');
  const region = document.getElementById('filter-region');
  const id = document.getElementById('filter-id');
  const clear = document.getElementById('filter-clear');

  const apply = () => {
    state.filters.domain = domain.value;
    state.filters.status = status.value;
    state.filters.region = region.value;
    state.filters.id = id.value.trim();
    state.tablesLoading = false;
    setFiltersLocked(false);
    renderAllTabs();
  };

  const finishFilterRequest = async (generation, request) => {
    try {
      await request();
    } catch (err) {
      console.error(err);
    }
    if (!isCurrentFilterRequest(generation)) return false;
    state.dashboardChartsLoading = false;
    apply();
    return true;
  };

  domain.addEventListener('change', async () => {
    state.filters.domain = domain.value;
    markFiltersApplied();
    const generation = beginFilterRequest();
    await finishFilterRequest(generation, async () => {
      await loadClubsForDomain(domain.value);
      if (!isCurrentFilterRequest(generation)) return;
      await reloadMonkeysForFilters();
      if (shouldQueryJourneys(domain.value)) {
        // Refresh already wrote this Environment and Time range into SQL.
        // Switching domain only reads that snapshot, capped at 100 rows.
        await reloadJourneysForClub({ generation });
      }
    });
  });
  status.addEventListener('change', async () => {
    state.filters.status = status.value;
    markFiltersApplied();
    if (!shouldQueryJourneys()) {
      apply();
      return;
    }
    const generation = beginFilterRequest();
    await finishFilterRequest(generation, () => reloadJourneysForClub({ generation }));
  });
  club.addEventListener('change', async () => {
    state.filters.club = club.value;
    markFiltersApplied();
    await reloadMonkeysForFilters();
    if (!shouldQueryJourneys()) {
      apply();
      return;
    }
    const generation = beginFilterRequest();
    // Stored snapshot only. Refresh is what re-queries App Insights.
    await finishFilterRequest(generation, () => reloadJourneysForClub({ generation }));
  });
  region.addEventListener('change', async () => {
    markFiltersApplied();
    if (!shouldQueryJourneys()) {
      apply();
      return;
    }
    const generation = beginFilterRequest();
    await finishFilterRequest(generation, () => reloadJourneysForClub({ generation }));
  });
  id.addEventListener('input', debounce(async () => {
    state.filters.id = id.value.trim();
    markFiltersApplied();
    if (!shouldQueryJourneys()) {
      apply();
      return;
    }
    const generation = beginFilterRequest();
    await finishFilterRequest(generation, () => reloadJourneysForClub({ generation }));
  }, 300));
  clear.addEventListener('click', async () => {
    domain.value = 'all'; status.value = 'all'; region.value = 'all'; id.value = '';
    document.getElementById('env-select').value = 'production';
    document.getElementById('time-range-select').value = '15m';
    state.filters.domain = 'all';
    state.filters.status = 'all';
    state.filters.region = 'all';
    state.filters.club = '';
    state.filters.id = '';
    state.filters.environment = 'production';
    state.filters.timeRange = '15m';
    // Clear is still a user interaction; only the initial page load uses 15 rows.
    markFiltersApplied();
    const generation = beginFilterRequest();
    await finishFilterRequest(generation, async () => {
      await loadClubsForDomain('all');
      if (!isCurrentFilterRequest(generation)) return;
      await reloadJourneysForClub({ generation });
    });
  });

  document.getElementById('env-select').addEventListener('change', async (e) => {
    state.filters.environment = e.target.value;
    markFiltersApplied();
    const product = domainLabel(state.filters.domain);
    const announce = () => showToast(
      'Environment changed',
      `Now viewing ${e.target.value} (${product} ${environmentInsightsLabel(e.target.value)}).`,
      'info',
    );
    if (!shouldQueryJourneys(state.filters.domain)) {
      announce();
      apply();
      return;
    }
    const generation = beginFilterRequest();
    const applied = await finishFilterRequest(generation, async () => {
      await loadClubsForDomain(state.filters.domain);
      if (!isCurrentFilterRequest(generation)) return;
      await reloadJourneysForClub({ generation });
    });
    if (applied) announce();
  });
  document.getElementById('time-range-select').addEventListener('change', async (e) => {
    state.filters.timeRange = e.target.value;
    markFiltersApplied();
    if (!shouldQueryJourneys(state.filters.domain)) {
      apply();
      return;
    }
    const generation = beginFilterRequest();
    await finishFilterRequest(generation, async () => {
      await loadClubsForDomain(state.filters.domain);
      if (!isCurrentFilterRequest(generation)) return;
      await reloadJourneysForClub({ generation });
    });
  });
}

/** Every wired travel vertical queries with the header Time range and Environment. */
function usesHeaderWin(domain) {
  return domain === 'all' || JOURNEY_DOMAINS.includes(domain);
}

function environmentInsightsLabel(environment) {
  const env = String(environment || 'production');
  if (env === 'staging') return 'UAT App Insights';
  return 'production App Insights';
}

function headerQueryWin() {
  const t = String((state.filters && state.filters.timeRange) || '15m');
  if (t === '15m' || t === '1h' || t === '2h' || t === '6h' || t === '24h' || t === '7d') return t;
  return '15m';
}

function windowToMs(win) {
  const token = String(win || '');
  const match = token.match(/^(\d{1,3})(m|h|d)$/i);
  if (!match) return null;
  const amount = Number(match[1]);
  if (match[2].toLowerCase() === 'm') return amount * 60 * 1000;
  if (match[2].toLowerCase() === 'h') return amount * 60 * 60 * 1000;
  return amount * 24 * 60 * 60 * 1000;
}

function cruiseQueryWin() {
  return headerQueryWin();
}

function debounce(fn, wait) {
  let t;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), wait); };
}

/* -----------------------------------------------------------------------
   5a. OVERVIEW TAB
   ----------------------------------------------------------------------- */
function computeDomainHealth(domain) {
  return calculateDomainHealth(state.journeys.filter(matchesGlobalFilters), domain);
}

function renderKpiCards() {
  const cards = state.overview?.tiles?.length ? state.overview.tiles : [
    { label: 'Platform Availability', value: 'No data', meta: 'Rolling 24h average', tone: '' },
    { label: 'Successful Journeys', value: 'No data', meta: 'Completed / total in the selected window', tone: '' },
    { label: 'Booking Conversion', value: 'No data', meta: 'Search-to-booking', tone: '' },
    { label: 'Avg. End-to-End Response', value: 'No data', meta: 'Across the selected journeys', tone: '' },
    { label: 'Active Critical Incidents', value: 'No data', meta: 'Open incidents at Critical severity', tone: '' },
    { label: 'Open Exceptions', value: 'No data', meta: 'Open count, with total tracked', tone: '' },
    { label: 'Doctor Monkey Diagnoses', value: 'No data', meta: 'Diagnoses in window', tone: '' },
  ];

  document.getElementById('overview-kpis').innerHTML = cards.map((c) => `
    <div class="kpi-card">
      <div class="kpi-card__label">${escapeHtml(c.label)}</div>
      <div class="kpi-card__value${c.value === 'No data' ? ' kpi-card__value--empty' : ''}">${escapeHtml(String(c.value))}</div>
      <div class="kpi-card__meta ${c.tone ? `kpi-card__meta--${c.tone}` : ''}">${escapeHtml(c.meta)}</div>
    </div>`).join('');
}

function renderDomainCards(targetId) {
  const health = JOURNEY_DOMAINS.map(computeDomainHealth).filter((d) => state.filters.domain === 'all' || state.filters.domain === d.domain);
  const percent = (value) => value == null ? '—' : `${value.toFixed(2)}%`;
  const latency = (value) => value == null ? '—' : `${value} ms`;
  const count = (value) => value == null ? '—' : String(value);
  const trendIcon = { improving: '▲', degrading: '▼', stable: '→', unknown: '—' };
  document.getElementById(targetId).innerHTML = health.map((d) => `
    <div class="domain-card domain-card--${normalizeStatus(d.status)}">
      <div class="domain-card__head">
        <span class="domain-card__name">${escapeHtml(domainLabel(d.domain))}</span>
        ${d.domain === 'air'
          ? '<span class="construction-tag">Under construction</span>'
          : statusBadge(d.status)}
      </div>
      <div class="domain-card__metrics">
        <span class="domain-card__metric-label">Availability</span><span class="domain-card__metric-value">${percent(d.availability)}</span>
        <span class="domain-card__metric-label">Error rate</span><span class="domain-card__metric-value">${percent(d.errorRate)}</span>
        <span class="domain-card__metric-label">Avg latency</span><span class="domain-card__metric-value">${latency(d.latencyMs)}</span>
        <span class="domain-card__metric-label">Active incidents</span><span class="domain-card__metric-value">${count(d.incidents)}</span>
      </div>
      <div class="domain-card__trend">Trend: ${trendIcon[d.trend.direction]} ${escapeHtml(d.trend.label)}${d.sampleSize ? ` · ${d.sampleSize} journeys` : ''}</div>
    </div>`).join('');
}

function renderEventTimeline() {
  const sorted = [...state.events].sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));
  document.getElementById('overview-timeline').innerHTML = sorted.map((e) => `
    <li class="event--${e.severity}">
      <div class="event-timeline__title">${escapeHtml(e.title)}</div>
      <div class="event-timeline__desc">${escapeHtml(e.description)}</div>
      <div class="event-timeline__time">${formatRelativeTime(e.timestamp)} · ${escapeHtml(domainLabel(e.domain))}</div>
    </li>`).join('');
}

function renderOverviewTab() {
  renderKpiCards();
  renderAgentPipeline();
  renderDomainCards('overview-domains');
  renderEventTimeline();
  startOverviewPoller();
}

/* -----------------------------------------------------------------------
   5a-bis. AGENT PIPELINE (Overview)
   Two halves, deliberately: the assembly line shows the SHAPE of the process
   (detect → hand off → investigate) with work visibly moving between agents,
   and the live story below answers "what is happening right now" with real
   facts. The line alone goes still whenever nothing is in flight; the story
   never does, because it falls back to what happened last.
   ----------------------------------------------------------------------- */

/** Phase labels of an Inspector investigation, in order (see ForensicCasePhase). */
const INSPECTOR_PIPELINE_PHASES = ['preprocess', 'scope', 'agents', 'debate', 'synthesis', 'grounding', 'verdict'];

/** One icon per real phase, used by the Inspector's stepper - decoration only, the data behind it
 *  is still ForensicCasePhase. */
const INSPECTOR_PIPELINE_PHASE_ICONS = {
  preprocess: '🧹', scope: '🔭', agents: '📚', debate: '💬', synthesis: '🧩', grounding: '✅', verdict: '⚖️',
};

/** Cache of the last rendered flow's signature (see renderAgentPipeline) - null forces a rebuild,
 *  which is what every tab load and every "nothing to show" transition needs. */
let pipelineFlowSignature = null;

function renderAgentPipeline() {
  const host = document.getElementById('overview-pipeline');
  if (!host) return;

  const scouts = (state.monkeys || []).filter((m) => isScoutMonkey(m.id));
  const director = (state.monkeys || []).find((m) => m.id === 'inspector-monkey');
  if (!scouts.length && !director) { host.innerHTML = ''; pipelineFlowSignature = null; return; }

  const queue = (director && director.queue) || {};
  const running = director && director.currentCase ? director.currentCase : null;
  const rca = state.monkeys.find((m) => m.id === 'rca-monkey');
  const live = running || scouts.some((s) => s.currentCycle);

  // The shell is built once; every poll after that only touches the pieces whose own data moved.
  // Rebuilding the whole panel on a schedule - rather than because something changed - was
  // restarting the scouts' radar sweep and evidence belt before either animation finished a lap
  // (both run longer than a poll interval), taking the stepper's spin down with them.
  if (!host.querySelector('.pipe-panel')) {
    host.innerHTML = `
      <div class="pipe-panel">
        <div class="pipe-panel__h">
          <span class="pipe-panel__t">Detect · hand off · investigate · write it up</span>
          <span class="pipe-panel__s" id="pipe-status"></span>
        </div>
        <div id="pipe-flow"></div>
        <div id="pipe-specialists"></div>
        <div id="pipe-story"></div>
      </div>`;
    pipelineFlowSignature = null;
  }

  document.getElementById('pipe-status').innerHTML = live
    ? '<span class="pipe-live"></span>live'
    : `<span class="pipe-live pipe-live--idle"></span>${escapeHtml(pipelineIdleReason(scouts, director))}`;

  // Only the fields that actually change the flow's shape or numbers go into this signature - not
  // the queue's aging percentage, which is a function of Date.now() and would otherwise force a
  // full rebuild (and every animation restart that comes with it) on every single poll.
  const flowSignature = JSON.stringify({
    scouts: scouts.map((s) => ({
      id: s.id, findings: s.findings, currentCycle: s.currentCycle,
      rows: s.lastCycle?.boardRowsEvaluated, clubs: s.lastCycle?.clubsWatched,
      activity: (s.activity || []).slice(0, 4).map((a) => a.time || a.text || a.symptom || a.reason),
    })),
    queueWaiting: queue.queued,
    queueOldest: queue.oldestQueuedAt,
    queueCapped: (queue.maxInvestigationsPerDay ?? 0) > 0 && (queue.investigationsToday ?? 0) >= queue.maxInvestigationsPerDay,
    runningPhase: running ? running.phase : null,
    directorPaused: director ? director.paused : null,
    rca: rca ? {
      findings: rca.findings,
      factRate: rca.rcaStats?.statements > 0 ? Math.round((rca.rcaStats.factStatements / rca.rcaStats.statements) * 100) : null,
      paused: rca.paused, auto: rca.autoCompose,
    } : null,
  });

  const flowHost = document.getElementById('pipe-flow');
  if (flowSignature !== pipelineFlowSignature) {
    flowHost.innerHTML = renderPipelineLine(scouts, director, queue, running, rca);
    pipelineFlowSignature = flowSignature;

    // The RCA confidence bar and the queue's aging bar both fill from 0 the moment the flow is
    // actually rewritten - a width set in the same paint it is inserted never animates.
    requestAnimationFrame(() => {
      flowHost.querySelectorAll('.pipe-rca-meter__fill[data-fill], .pipe-queue-age__fill[data-fill]').forEach((el) => {
        el.style.width = `${el.dataset.fill}%`;
      });
    });
  } else {
    // Nothing about the flow's shape changed, but real time still passed - the aging bar is the
    // one piece here allowed to move on a clock, so it is patched in place without touching (and
    // restarting) the radar sweep or the belt sitting right next to it.
    const ageFill = flowHost.querySelector('.pipe-queue-age__fill');
    if (ageFill && queue.oldestQueuedAt) {
      const pct = Math.max(0, Math.min(100, Math.round(((Date.now() - new Date(queue.oldestQueuedAt).getTime()) / PIPE_QUEUE_CLAIM_CYCLE_MS) * 100)));
      ageFill.style.width = `${pct}%`;
    }
  }

  document.getElementById('pipe-specialists').innerHTML = renderPipelineSpecialists();
  // The live story is the one region meant to keep moving on every poll - it falls back to what
  // happened last specifically so it never goes still, unlike the flow above it.
  document.getElementById('pipe-story').innerHTML = renderPipelineStory(scouts, director, running);

  wirePipelineClicks(host);
}

/** One listener on the pipeline host, wired once - same reasoning as wireMonkeyGrid: every stage
 *  here is replaced wholesale on each refresh, so a listener bound per node would be dead within
 *  seconds. Reuses handleMonkeyAction so a click here opens exactly the modal its matching button
 *  on the Monkeys tab already opens - Evidence for a scout, the queue or the case list for the
 *  Inspector, the written documents for the RCA Agent. */
function wirePipelineClicks(host) {
  if (host.dataset.wired === 'true') return;
  host.dataset.wired = 'true';
  host.addEventListener('click', (e) => {
    const target = e.target.closest('[data-pipe-action]');
    if (!target) return;
    const monkey = state.monkeys.find((m) => m.id === target.dataset.monkeyId);
    if (monkey) handleMonkeyAction(monkey, target.dataset.pipeAction);
  });
}

/** Which of the five specialists has no source wired up. Structural, not a per-case outcome:
 *  these two have no repository or documentation index, so they spend zero LLM calls and could
 *  not have found anything even if it were relevant. "Found nothing" would be a lie. */
const INSPECTOR_SPECIALISTS_WITHOUT_SOURCE = new Set(['CodeAgent', 'DocsAgent']);

function renderPipelineSpecialists() {
  return `
    <div class="pipe-agentchips">
      ${INSPECTOR_SPECIALISTS.map((name) => INSPECTOR_SPECIALISTS_WITHOUT_SOURCE.has(name)
        ? `<span class="pipe-achip pipe-achip--off" title="No repository or documentation index is wired up for this agent.">${escapeHtml(name)} \u00b7 no source</span>`
        : `<span class="pipe-achip pipe-achip--on">${escapeHtml(name)} \u2713</span>`).join('')}
    </div>`;
}

/** An idle line must say WHY it is idle - "paused" and "nothing to do" look identical otherwise. */
function pipelineIdleReason(scouts, director) {
  const agents = [...scouts, director].filter(Boolean);
  if (agents.length && agents.every((m) => m.paused)) return 'all agents paused';
  if (director && director.paused) return 'director paused';
  return 'idle between cycles';
}

/** The assembly line: two detection agents → one hand-off queue → the director → the write-up. */
function renderPipelineLine(scouts, director, queue, running, rca) {
  const waiting = queue.queued ?? 0;
  const oldest = queue.oldestQueuedAt;
  const capReached = (queue.maxInvestigationsPerDay ?? 0) > 0
    && (queue.investigationsToday ?? 0) >= queue.maxInvestigationsPerDay;

  const scoutRows = scouts.map((s) => {
    const cycle = s.lastCycle;
    const measured = cycle
      ? `${formatCount(cycle.boardRowsEvaluated || 0)} rows · ${formatCount(cycle.clubsWatched || 0)} clubs`
      : 'no cycle recorded yet';
    return `
      <div class="pipe-mini${s.currentCycle ? ' pipe-mini--live' : ''}" data-monkey-id="${escapeHtml(s.id)}" data-pipe-action="evidence">
        <span class="pipe-hint">Click → Evidence</span>
        <div class="pipe-mini__head">
          <span class="pipe-mini__radar"><span class="pipe-mini__ico" aria-hidden="true">🔍</span></span>
          <span class="pipe-mini__b">
            <span class="pipe-mini__t">${escapeHtml(s.name)}</span>
            <span class="pipe-mini__m">${escapeHtml(measured)}</span>
          </span>
          <span class="pipe-mini__n">${formatCount(s.findings || 0)}</span>
        </div>
        ${renderPipelineBelt(s)}
      </div>`;
  }).join('');

  // Nothing travels the wires when nothing was handed off - a moving dot with an empty queue
  // behind it would be an animation pretending to be data.
  const handedOff = scouts.reduce((n, s) => n + (s.findings || 0), 0);
  const feedWire = handedOff > 0
    ? '<span class="pipe-dot pipe-dot--a" title="Evidence handed off">📦</span><span class="pipe-dot pipe-dot--b" title="Evidence handed off">📦</span>'
    : '';
  const drainWire = waiting > 0 && running
    ? '<span class="pipe-dot pipe-dot--warn pipe-dot--a" title="Case claimed">🧾</span>'
    : '';
  const directorAttrs = director ? ` data-monkey-id="${escapeHtml(director.id)}"` : '';

  return `
    <div class="pipe">
      <div class="pipe-substage">
        ${scoutRows || '<div class="pipe-mini"><span class="pipe-mini__b"><span class="pipe-mini__t">No detection agent</span></span></div>'}
        <div class="pipe-substage__cap">analyze their boards every 5 min</div>
      </div>

      <div class="pipe-wire">${feedWire}</div>

      <div class="pipe-stage${capReached && waiting ? ' pipe-stage--blocked' : ''}"${directorAttrs} data-pipe-action="queue-modal">
        <span class="pipe-hint">Click → Queue</span>
        <span class="pipe-stage__badge">hand-off</span>
        <div class="pipe-stage__top">
          <span class="pipe-stage__ico${waiting > 0 ? ' pipe-stage__ico--pulse' : ''}" aria-hidden="true">📥</span>
          <div>
            <span class="pipe-stage__t">Evidence queue</span>
            <span class="pipe-stage__sub">${oldest ? `oldest ${escapeHtml(formatRelativeTime(oldest))}` : 'nothing waiting'}</span>
          </div>
        </div>
        <span class="pipe-stage__n">${formatCount(waiting)}<small>waiting</small></span>
        ${renderPipelineQueueTray(waiting, oldest)}
      </div>

      <div class="pipe-wire">${drainWire}</div>

      <div class="pipe-stage${running ? ' pipe-stage--live' : ''}"${directorAttrs} data-pipe-action="evidence">
        <span class="pipe-hint">Click → Cases</span>
        ${running ? '<span class="pipe-stage__badge">investigating</span>' : ''}
        <div class="pipe-stage__top">
          <span class="pipe-stage__ico" aria-hidden="true">🧠</span>
          <div>
            <span class="pipe-stage__t">${escapeHtml(director ? director.name : 'Director')}</span>
            <span class="pipe-stage__sub">5 specialists · 3 models</span>
          </div>
        </div>
        <span class="pipe-stage__n">${running ? 1 : 0}<small>in flight</small></span>
        ${running ? renderPipelineStepper(running) : ''}
      </div>

      ${renderPipelineRcaStage(rca)}
    </div>`;
}

/** What a scout has actually handed off recently - real findings, never invented items. A fixed-
 *  height looping marquee (like the mock) rather than a wrapping list, so the card never grows
 *  taller as evidence piles up: every chip still carries real text, only the MOTION repeats. */
function renderPipelineBelt(scout) {
  const items = (scout.activity || []).slice(0, 4);
  if (!items.length) return '<div class="pipe-belt"><span class="pipe-belt__empty">nothing handed off yet</span></div>';
  const chips = items.map((a, i) => {
    const text = a.symptom || a.reason || a.text || 'evidence packaged';
    const club = a.club ? (a.club.clubName || a.club.brandName || a.club.entityKey) : '';
    const label = club ? `${club} — ${text}` : text;
    const style = `animation-duration:${6 + items.length}s;animation-delay:${i * (6 + items.length) / items.length}s`;
    return `<span class="pipe-belt__chip" style="${style}" title="${escapeHtml(label)}">${escapeHtml(label)}</span>`;
  }).join('');
  return `<div class="pipe-belt">${chips}</div>`;
}

/** Real items piling up, capped so the tray cannot grow without bound; the big number above it
 *  still reads the true total even past the cap. */
const PIPE_QUEUE_TRAY_CAP = 6;
// The Inspector's own timer cadence (see startInspectorPoller's comment) - the real reference the
// aging bar below fills against, not an arbitrary number.
const PIPE_QUEUE_CLAIM_CYCLE_MS = 60000;
function renderPipelineQueueTray(waiting, oldest) {
  if (!waiting) return '<div class="pipe-queue-tray"><span class="pipe-queue-tray__empty">queue is empty</span></div>';
  const shown = Math.min(waiting, PIPE_QUEUE_TRAY_CAP);
  const items = Array.from({ length: shown }, (_, i) =>
    `<span class="pipe-queue-tray__item" style="animation-delay:${i * 0.05}s" aria-hidden="true">📦</span>`).join('');
  const more = waiting > shown ? `<span class="pipe-queue-tray__more">+${waiting - shown}</span>` : '';
  // A real elapsed-time fraction, re-filled on every poll - not a fake loop the agent is not
  // actually running. Capped at 100%: an item overdue for more than one cycle just reads "full".
  const ageFill = oldest
    ? Math.max(0, Math.min(100, Math.round(((Date.now() - new Date(oldest).getTime()) / PIPE_QUEUE_CLAIM_CYCLE_MS) * 100)))
    : 0;
  const ageBar = oldest ? `
    <div class="pipe-queue-age">
      <div class="pipe-queue-age__bar"><div class="pipe-queue-age__fill" data-fill="${ageFill}"></div></div>
      <div class="pipe-queue-age__label">oldest waiting ${escapeHtml(formatRelativeTime(oldest))} — the Inspector claims on its next minute-cycle</div>
    </div>` : '';
  return `<div class="pipe-queue-tray">${items}${more}</div>${ageBar}`;
}

/** The wire is live only when the Inspector really hands cases over. With auto-compose off the
 *  stage is dashed and unfed, because then a person has to ask for every write-up. Drawing the
 *  same picture in both states would promise a step that may never happen. */
function renderPipelineRcaStage(rca) {
  if (!rca) return '';

  const written = rca.findings || 0;
  const stats = rca.rcaStats || {};
  const evidence = stats.statements > 0
    ? `${Math.round((stats.factStatements / stats.statements) * 100)}% measured`
    : 'nothing written yet';

  const auto = rca.autoCompose === true;
  // Same rule as the wires above: a dot only moves when something actually travelled it.
  const dots = auto && written > 0 ? '<span class="pipe-dot pipe-dot--a" title="RCA composed">📝</span>' : '';
  const wire = auto
    ? `<div class="pipe-wire" title="A case that escalates with a probable root cause hands over on its own.">${dots}</div>`
    : '<div class="pipe-wire pipe-wire--manual" title="Auto-compose is off: a person asks for the write-up."></div>';

  const factRate = stats.statements > 0 ? Math.round((stats.factStatements / stats.statements) * 100) : null;

  return `
      ${wire}

      <div class="pipe-stage${auto ? '' : ' pipe-stage--ondemand'}${rca.paused ? ' pipe-stage--blocked' : ''}" data-monkey-id="${escapeHtml(rca.id)}" data-pipe-action="evidence">
        <span class="pipe-hint">Click → RCAs</span>
        <span class="pipe-stage__badge">${auto ? 'on escalation' : 'on demand'}</span>
        <div class="pipe-stage__top">
          <span class="pipe-stage__ico" aria-hidden="true">📝</span>
          <div>
            <span class="pipe-stage__t">${escapeHtml(rca.name)}</span>
            <span class="pipe-stage__sub">${escapeHtml(evidence)}</span>
          </div>
        </div>
        <span class="pipe-stage__n">${formatCount(written)}<small>written</small></span>
        <div class="pipe-rca-doc" aria-hidden="true"><i style="width:100%"></i><i style="width:86%"></i><i style="width:62%"></i></div>
        <div class="pipe-rca-meter">
          <div class="pipe-rca-meter__bar"><div class="pipe-rca-meter__fill" data-fill="${factRate ?? 0}"></div></div>
          <div class="pipe-rca-meter__label">${factRate !== null ? `${factRate}% of statements measured, not inferred` : 'no statements written yet'}</div>
        </div>
      </div>`;
}

/** Shows that an investigation has steps rather than being one opaque wait - one icon per real
 *  ForensicCasePhase, and only the current one gets the spinning ring. Only rendered while a case
 *  is actually running (see renderPipelineLine), same as before. */
function renderPipelineStepper(running) {
  const current = INSPECTOR_PIPELINE_PHASES.indexOf(running.phase);
  const steps = INSPECTOR_PIPELINE_PHASES.map((p, i) => {
    const cls = current < 0 ? '' : (i < current ? ' done' : (i === current ? ' now' : ''));
    const icon = INSPECTOR_PIPELINE_PHASE_ICONS[p] || '•';
    return `<div class="pipe-step${cls}"><span class="pipe-step__ring" title="${escapeHtml(p)}">${icon}</span></div>`;
  }).join('');
  const label = running.phase ? `${running.phase}…` : 'starting…';
  return `<div class="pipe-stepper">${steps}</div><div class="pipe-stage__phase">${escapeHtml(label)}</div>`;
}

/** The live story. Built from what the agents actually recorded, newest first. */
function renderPipelineStory(scouts, director, running) {
  const events = [];

  scouts.forEach((s) => {
    const cycle = (s.cycles || []).find((c) => c.status !== 'running');
    if (cycle) {
      events.push({
        at: cycle.startedAt,
        dot: 'scout',
        who: s.board === 'exception' ? 'Exception' : 'Latency',
        whoClass: s.board === 'exception' ? 'exc' : 'lat',
        title: `Analyzed the ${s.board === 'exception' ? 'exception' : 'latency'} board`,
        detail: `${formatCount(cycle.boardRowsEvaluated || 0)} rows across ${formatCount(cycle.clubsWatched || 0)} clubs · ${formatCount(cycle.candidatesFound || 0)} above threshold`,
      });
    }
    (s.activity || []).slice(0, 2).forEach((a) => {
      events.push({
        at: a.time,
        dot: 'pack',
        who: s.board === 'exception' ? 'Exception' : 'Latency',
        whoClass: s.board === 'exception' ? 'exc' : 'lat',
        title: 'Found something and packaged the evidence',
        // The agent's own words when a model was available; the board's measured reason otherwise.
        detail: a.symptom || a.reason || a.text || '',
        strong: a.club ? (a.club.clubName || a.club.brandName || a.club.entityKey) : '',
      });
    });
  });

  if (director) {
    if (running) {
      events.push({
        at: running.startedAt,
        dot: 'dir',
        who: 'Director',
        whoClass: 'dir',
        title: 'Investigating',
        detail: '5 specialists gathered the evidence · 3 models are reviewing it · citations being verified',
        now: true,
      });
    }
    (director.activity || []).slice(0, 2).forEach((a) => {
      events.push({
        at: a.time,
        dot: 'done',
        who: 'Director',
        whoClass: 'dir',
        title: `Closed a case — ${escapeHtml(String(a.stage || '').toLowerCase() || 'done')}`,
        detail: a.text || '',
      });
    });
  }

  const rows = events
    .filter((e) => e.at)
    .sort((a, b) => new Date(b.at) - new Date(a.at))
    .slice(0, 5);

  if (!rows.length) {
    return `<p class="pipe-story__empty">No agent activity recorded yet. The story fills in as soon as a cycle runs.</p>`;
  }

  return `
    <div class="pipe-story">
      ${rows.map((e, i) => `
        <div class="pipe-story__row${e.now ? ' pipe-story__row--now' : ''}" style="animation-delay:${i * 0.12}s">
          <span class="pipe-story__rail">
            <span class="pipe-story__dot pipe-story__dot--${e.dot}"></span>
            ${i < rows.length - 1 ? '<span class="pipe-story__line"></span>' : ''}
          </span>
          <span class="pipe-story__b">
            <span class="pipe-story__t"><span class="pipe-who pipe-who--${e.whoClass}">${escapeHtml(e.who)}</span>${escapeHtml(e.title)}${e.now ? '<span class="pipe-typing"></span>' : ''}</span>
            ${e.detail ? `<span class="pipe-story__m">${e.strong ? `<strong>${escapeHtml(e.strong)}</strong> — ` : ''}${escapeHtml(e.detail)}</span>` : ''}
          </span>
          <span class="pipe-story__when">${escapeHtml(e.now ? 'now' : formatRelativeTime(e.at))}</span>
        </div>`).join('')}
    </div>`;
}

/* -----------------------------------------------------------------------
   5b. CUSTOMER JOURNEYS TAB
   ----------------------------------------------------------------------- */
function journeySortAccessor(j, key) {
  if (key === 'club') return j.club || j.origin;
  if (key === 'origin') return j.origin;
  return j[key] ?? '';
}

function renderJourneysTable() {
  if (state.tablesLoading) return;
  markTableBusy(document.getElementById('journeys-table'), false);
  const filtered = state.journeys.filter(matchesGlobalFilters);
  const { key, dir } = state.sort.journeys;
  const rows = sortRows(filtered, key, dir, journeySortAccessor);
  const tbody = document.getElementById('journeys-tbody');

  if (rows.length === 0) {
    tbody.innerHTML = `<tr><td colspan="10"><div class="state-block">No journeys match the current filters.</div></td></tr>`;
    return;
  }

  tbody.innerHTML = rows.map((j) => `
    <tr data-journey-id="${escapeHtml(j.id)}" tabindex="0" role="button" aria-label="View journey ${escapeHtml(j.correlationId || j.id)} details">
      <td><code>${escapeHtml(j.correlationId || '—')}</code></td>
      <td>${escapeHtml(domainLabel(j.domain))}</td>
      <td>${escapeHtml(j.customer)}</td>
      <td>${escapeHtml(j.club || j.origin)}</td>
      <td>${new Date(j.startDate).toLocaleDateString()}</td>
      <td>${journeyStatusBadge(j.status)}</td>
      <td>${j.sessionCount ? `${j.sessionCount}` : '—'}</td>
      <td>${formatDuration(j.totalDurationMs)}</td>
      <td>${j.bookingId ? escapeHtml(j.bookingId) : '—'}</td>
      <td>${formatRelativeTime(j.timestamp)}</td>
    </tr>`).join('');

  tbody.querySelectorAll('tr[data-journey-id]').forEach((row) => {
    const open = () => openJourneyDetail(row.dataset.journeyId);
    row.addEventListener('click', open);
    row.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
  });
}

function journeyStatusBadge(status) {
  const cls = mapOutcomeToHealth(status);
  return `<span class="status-badge status-badge--${cls}">${escapeHtml(status)}</span>`;
}

function stageBarClass(status) {
  if (status === 'failed') return 'waterfall-row__bar--failed';
  if (status === 'degraded') return 'waterfall-row__bar--degraded';
  if (status === 'skipped') return 'waterfall-row__bar--skipped';
  return '';
}

function openJourneyDetail(journeyId) {
  const journey = state.journeys.find((j) => j.id === journeyId);
  if (!journey) return;
  const totalMs = Math.max(journey.totalDurationMs || 0, 1);

  const waterfallRows = journey.stages.map((s, idx) => {
    const leftPct = (s.startOffsetMs / totalMs) * 100;
    const widthPct = Math.max((s.durationMs / totalMs) * 100, s.durationMs === 0 ? 0 : 0.6);
    return `
      <div class="waterfall-row" data-stage-idx="${idx}">
        <div class="waterfall-row__label">
          <span class="waterfall-row__name">${escapeHtml(s.name)}</span>
          <span class="waterfall-row__component">${escapeHtml(s.component)}</span>
        </div>
        <div class="waterfall-row__track">
          <div class="waterfall-row__bar ${stageBarClass(s.status)}" style="left:${leftPct}%; width:${widthPct}%;"></div>
        </div>
        <div class="waterfall-row__duration">${s.status === 'skipped' ? 'skipped' : formatDuration(s.durationMs)}</div>
      </div>`;
  }).join('');

  const failBanner = journey.failReason
    ? `<div class="fail-reason"><span class="fail-reason__label">Why it failed</span>${escapeHtml(journey.failReason)}</div>`
    : '';

  const sessions = journey.sessions || [];
  const sessionsBlock = sessions.length
    ? `<h3>Telemetry CorrelationId</h3>
       <p class="hint">One GUID: the session of the member's last call (not the first/login session). SessionCount still says if more existed.</p>
       <div class="table-wrap trace-table-wrap">
         <table class="data-table data-table--compact">
           <thead><tr>
             <th>#</th><th>correlationId</th><th>First seen</th><th>Last seen</th><th>Gap</th><th>changeReason</th>
           </tr></thead>
           <tbody>${sessions.map((s) => `
             <tr>
               <td>${s.session ?? '—'}</td>
               <td><code>${escapeHtml(s.correlationId || '')}</code></td>
               <td>${s.firstSeen ? new Date(s.firstSeen).toLocaleString() : '—'}</td>
               <td>${s.lastSeen ? new Date(s.lastSeen).toLocaleString() : '—'}</td>
               <td>${s.gapFromPreviousSec == null || s.gapFromPreviousSec === '' ? '—' : `${s.gapFromPreviousSec}s`}</td>
               <td>${escapeHtml(s.changeReason || '—')}</td>
             </tr>`).join('')}
           </tbody>
         </table>
       </div>`
    : '';

  const isAir = journey.domain === 'air';
  const scope = isAir
    ? { label: 'Route', value: `${journey.origin} → ${journey.destination}` }
    : { label: 'Scope', value: journey.origin };

  const body = `
    <div class="journey-summary">
      <div class="journey-summary__item"><strong>${journeyStatusBadge(journey.status)}</strong>Outcome</div>
      <div class="journey-summary__item"><strong>${formatDuration(journey.totalDurationMs)}</strong>API time (${isAir ? 'Air UI+API' : 'TMO host'})</div>
      <div class="journey-summary__item"><strong>${escapeHtml(scope.value)}</strong>${scope.label}</div>
      <div class="journey-summary__item"><strong>${escapeHtml(journey.customer || journey.id)}</strong>User ID</div>
      <div class="journey-summary__item"><strong>${journey.bookingId ? escapeHtml(journey.bookingId) : '—'}</strong>Booking ID</div>
      <div class="journey-summary__item"><strong>${escapeHtml(journey.correlationId || '—')}</strong>Journey ID</div>
      <div class="journey-summary__item"><strong>${journey.sessionCount || 0}</strong>Session count</div>
    </div>
    ${failBanner}
    ${sessionsBlock}
    <h3>Stage Waterfall</h3>
    <p class="hint">Bars show measured API/dependency time (not user think time). Click a stage for its calls.</p>
    <div class="waterfall" id="journey-waterfall">${waterfallRows}</div>
    <div id="stage-detail-mount"></div>`;

  openSidePanel(`Journey ${journey.correlationId || journey.id}`, body);
  scrollDetailsToTop();

  document.querySelectorAll('#journey-waterfall .waterfall-row').forEach((row) => {
    row.addEventListener('click', () => {
      document.querySelectorAll('#journey-waterfall .waterfall-row').forEach((r) => r.classList.remove('is-selected'));
      row.classList.add('is-selected');
      const stage = journey.stages[Number(row.dataset.stageIdx)];
      renderStageDetail(stage, journey);
    });
  });

  // Auto-select the first failed/degraded stage to spotlight the root cause
  const problemIdx = journey.stages.findIndex((s) => s.status === 'failed' || s.status === 'degraded');
  if (problemIdx >= 0) {
    document.querySelectorAll('#journey-waterfall .waterfall-row')[problemIdx].click();
  }
}

function callErrorMessages(call) {
  return (call.errors || []).map((e) => e.message || JSON.stringify(e));
}

function callHasFailure(call) {
  return callErrorMessages(call).length > 0 || Number(call.failureRatePct) > 0;
}

function renderStageDetail(stage, journey) {
  const calls = stage.calls || [];
  const failing = calls.filter(callHasFailure);

  // The calls table scrolls horizontally in a narrow panel, so errors get their
  // own block above it: the reason a stage is red must never require scrolling.
  const errorBlock = failing.length
    ? `<div class="call-errors">
         <div class="call-errors__title">${failing.length} failing call${failing.length > 1 ? 's' : ''} in this stage</div>
         ${failing.map((c) => {
           const msgs = callErrorMessages(c);
           return `<div class="call-errors__item">
             <span class="call-errors__call">${escapeHtml(c.kind)} · ${escapeHtml(c.call)}</span>
             <span class="call-errors__rate">${c.failureRatePct == null ? '' : `${c.failureRatePct}% of ${c.n ?? '?'}`}</span>
             <span class="call-errors__msg">${msgs.length ? escapeHtml(msgs.join(' · ')) : 'failed without a captured message'}</span>
           </div>`;
         }).join('')}
       </div>`
    : `<div class="call-errors call-errors--clean">No failing calls in this stage.</div>`;

  const callRows = calls.length ? calls.map((c) => {
    const msgs = callErrorMessages(c);
    const failed = callHasFailure(c);
    return `<tr class="${failed ? 'is-failed' : ''}">
      <td>${escapeHtml(c.kind)}</td>
      <td>${escapeHtml(c.call)}${msgs.length ? `<div class="call-inline-error">${escapeHtml(msgs.join(' · '))}</div>` : ''}</td>
      <td><code>${escapeHtml(c.correlationId || '—')}</code></td>
      <td>${c.n ?? '—'}</td>
      <td>${formatDuration(c.callTotalMs)}</td>
      <td>${c.p95Ms == null ? '—' : formatDuration(c.p95Ms)}</td>
      <td>${escapeHtml(c.status || '—')}</td>
      <td>${c.failureRatePct == null ? '—' : `${c.failureRatePct}%`}</td>
      <td>${escapeHtml(c.detail || '—')}</td>
    </tr>`;
  }).join('') : `<tr><td colspan="9">No calls in this stage.</td></tr>`;

  document.getElementById('stage-detail-mount').innerHTML = `
    <div class="stage-detail">
      <dl>
        <dt>Stage</dt><dd>${escapeHtml(stage.name)}</dd>
        <dt>Primary call</dt><dd>${escapeHtml(stage.component)}</dd>
        <dt>Domain</dt><dd>${escapeHtml(domainLabel(journey.domain))}</dd>
        <dt>Latency</dt><dd>${escapeHtml(stage.v6Status || stage.status)}</dd>
        <dt>API time</dt><dd>${formatDuration(stage.durationMs)}</dd>
        <dt>Hits</dt><dd>${stage.requestCount}</dd>
        <dt>Diagnostic</dt><dd>${escapeHtml(stage.diagnostic)}</dd>
      </dl>
    </div>
    <h3>Calls in this stage</h3>
    ${errorBlock}
    <div class="table-wrap trace-table-wrap">
      <table class="data-table data-table--compact">
        <thead><tr>
          <th>Kind</th><th>Call</th><th>correlationId</th><th>n</th><th>Total</th><th>P95</th><th>Status</th><th>Fail %</th><th>Supplier</th>
        </tr></thead>
        <tbody>${callRows}</tbody>
      </table>
    </div>`;
}

/* -----------------------------------------------------------------------
   5c. PLATFORM HEALTH TAB
   ----------------------------------------------------------------------- */
function renderComponentGrid() {
  const filtered = state.components.filter(matchesGlobalFilters);
  const grid = document.getElementById('component-grid');
  if (filtered.length === 0) {
    grid.innerHTML = `<div class="state-block">No components match the current filters.</div>`;
    return;
  }
  grid.innerHTML = filtered.map((c) => `
    <button class="component-card" data-component-id="${escapeHtml(c.id)}" type="button" aria-haspopup="dialog">
      <div class="component-card__head">
        <div>
          <div class="component-card__name">${escapeHtml(c.name)}</div>
          <div class="component-card__domain">${escapeHtml(domainLabel(c.domain))} · ${escapeHtml(c.type)}</div>
        </div>
        ${statusBadge(c.status)}
      </div>
      <div class="component-card__metrics">
        <span>Latency: <strong>${c.latencyMs} ms</strong></span>
        <span>Req/min: <strong>${c.requestsPerMin.toLocaleString()}</strong></span>
        <span>Error rate: <strong>${c.errorRate}%</strong></span>
        <span>Alerts: <strong>${c.activeAlerts}</strong></span>
      </div>
    </button>`).join('');

  grid.querySelectorAll('.component-card').forEach((card) => {
    card.addEventListener('click', () => openComponentDetail(card.dataset.componentId));
  });
}

function openComponentDetail(componentId) {
  const c = state.components.find((x) => x.id === componentId);
  if (!c) return;
  const relatedJourneys = state.journeys.filter((j) => j.stages.some((s) => s.component === c.id)).slice(0, 5);
  const relatedExceptions = state.exceptions.filter((e) => e.component === c.id);

  const body = `
    <dl>
      <dt>Status</dt><dd>${statusBadge(c.status)}</dd>
      <dt>Domain</dt><dd>${escapeHtml(domainLabel(c.domain))}</dd>
      <dt>Type</dt><dd>${escapeHtml(c.type)}</dd>
      <dt>Version</dt><dd>${escapeHtml(c.version)}</dd>
      <dt>Latency</dt><dd>${c.latencyMs} ms</dd>
      <dt>Requests/min</dt><dd>${c.requestsPerMin.toLocaleString()}</dd>
      <dt>Error rate</dt><dd>${c.errorRate}%</dd>
      <dt>Dependencies</dt><dd>${c.dependencyCount}</dd>
      <dt>Last deployment</dt><dd>${formatRelativeTime(c.lastDeployment)}</dd>
      <dt>Active alerts</dt><dd>${c.activeAlerts}</dd>
    </dl>
    <h3>Related Customer Journeys</h3>
    <ul class="simple-list">${relatedJourneys.map((j) => `<li>${escapeHtml(j.id)} — ${journeyStatusBadge(j.status)}</li>`).join('') || '<li>None in current window.</li>'}</ul>
    <h3>Related Exceptions</h3>
    <ul class="simple-list">${relatedExceptions.map((e) => `<li>${escapeHtml(e.id)} — ${escapeHtml(e.message)}</li>`).join('') || '<li>None in current window.</li>'}</ul>
    <h3>Suggested Actions</h3>
    <ul class="simple-list">
      ${c.status === 'critical' ? '<li>Escalate to Reboot Monkey for controlled restart.</li>' : ''}
      ${c.status === 'degraded' ? '<li>Escalate to Dependency Monkey to evaluate supplier failover.</li>' : ''}
      ${normalizeStatus(c.status) === 'healthy' ? '<li>No action required — continue routine monitoring.</li>' : ''}
    </ul>`;

  openSidePanel(c.name, body);
}

function renderHealthTab() {
  renderDomainCards('health-domains');
  renderComponentGrid();
}

/* -----------------------------------------------------------------------
   5d. ANALYTICS TAB — lightweight native-SVG charts (no external chart lib)
   ----------------------------------------------------------------------- */
function buildLinePath(values, width, height, min, max) {
  const range = (max - min) || 1;
  const stepX = width / (values.length - 1 || 1);
  return values.map((v, i) => {
    const x = i * stepX;
    const y = height - ((v - min) / range) * height;
    return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
}

function renderSvgLineChart(svgId, series, colors) {
  const svg = document.getElementById(svgId);
  const width = 600, height = 200, pad = 10;
  const all = series.flat();
  const min = Math.min(...all) * 0.9;
  const max = Math.max(...all) * 1.05;
  const paths = series.map((values, i) => {
    const d = buildLinePath(values, width, height - pad, min, max);
    return `<path d="${d}" fill="none" stroke="${colors[i]}" stroke-width="2.5" />`;
  }).join('');
  svg.innerHTML = `<g transform="translate(0,${pad})">${paths}</g>`;
}

function renderSvgAreaChart(svgId, valuesA, valuesB, colorA, colorB) {
  const svg = document.getElementById(svgId);
  const width = 600, height = 200, pad = 10;
  const max = Math.max(...valuesA, ...valuesB) * 1.1;
  const pathA = buildLinePath(valuesA, width, height - pad, 0, max);
  const pathB = buildLinePath(valuesB, width, height - pad, 0, max);
  svg.innerHTML = `<g transform="translate(0,${pad})">
    <path d="${pathA}" fill="none" stroke="${colorA}" stroke-width="2.5" />
    <path d="${pathB}" fill="none" stroke="${colorB}" stroke-width="2.5" stroke-dasharray="4 3" />
  </g>`;
}

function renderBarList(targetId, rows, labelKey, valueKey, format = (v) => v) {
  const max = Math.max(...rows.map((r) => r[valueKey]), 1);
  document.getElementById(targetId).innerHTML = rows.map((r) => `
    <div class="bar-list__row">
      <span>${escapeHtml(String(r[labelKey]))}</span>
      <div class="bar-list__track"><div class="bar-list__fill" style="width:${(r[valueKey] / max) * 100}%"></div></div>
      <span class="bar-list__value">${format(r[valueKey])}</span>
    </div>`).join('');
}

function renderDashboardTimechart(svgId, legendId, points) {
  const svg = document.getElementById(svgId);
  const legend = document.getElementById(legendId);
  if (!svg) return;
  if (!points.length) {
    svg.innerHTML = '';
    if (legend) legend.innerHTML = '<span>No stored dashboard points yet. Run SyncJourneys.</span>';
    return;
  }
  const seriesNames = [...new Set(points.map((p) => p.series))].sort();
  const bins = [...new Set(points.map((p) => p.binStart))].sort();
  const bySeries = {};
  seriesNames.forEach((name) => {
    const lookup = {};
    points.filter((p) => p.series === name).forEach((p) => { lookup[p.binStart] = Number(p.value); });
    bySeries[name] = bins.map((bin) => (lookup[bin] == null ? 0 : lookup[bin]));
  });
  const colors = ['var(--chart-line-1)', 'var(--chart-line-2)', 'var(--chart-line-3)', 'var(--teal-500)', 'var(--status-critical)', '#8b5cf6', '#f59e0b', '#14b8a6'];
  renderSvgLineChart(svgId, seriesNames.map((name) => bySeries[name]), colors);
  if (legend) {
    legend.innerHTML = seriesNames.map((name, i) =>
      `<span><i class="legend-swatch" style="background:${colors[i % colors.length]}"></i>${escapeHtml(name)}</span>`).join('');
  }
}

function visibleDashboardDomains() {
  const domain = state.filters.domain;
  if (JOURNEY_DOMAINS.includes(domain)) return [domain];
  if (domain === 'all') return JOURNEY_DOMAINS.slice();
  return [];
}

function renderAnalyticsTab() {
  const a = state.analytics;
  if (!a) return;
  const wanted = visibleDashboardDomains();
  document.querySelectorAll('[data-dashboard-chart]').forEach((card) => {
    const [domain] = card.getAttribute('data-dashboard-chart').split(':');
    card.hidden = !wanted.includes(domain);
  });
  if (state.dashboardChartsLoading) {
    paintDashboardChartsLoading();
    return;
  }
  document.querySelectorAll('[data-dashboard-chart]').forEach((card) => card.removeAttribute('aria-busy'));
  const points = state.dashboardMetrics || [];
  renderDashboardTimechart('chart-hotel-latency', 'legend-hotel-latency',
    points.filter((p) => p.domain === 'hotels' && p.chart === 'step_latency_p95'));
  renderDashboardTimechart('chart-hotel-exceptions', 'legend-hotel-exceptions',
    points.filter((p) => p.domain === 'hotels' && p.chart === 'exceptions_by_step'));
  renderDashboardTimechart('chart-cruise-latency', 'legend-cruise-latency',
    points.filter((p) => p.domain === 'cruises' && p.chart === 'step_latency_p95'));
  renderDashboardTimechart('chart-cruise-exceptions', 'legend-cruise-exceptions',
    points.filter((p) => p.domain === 'cruises' && p.chart === 'exceptions_by_step'));

  const comparisonRows = [
    state.hotelDomainComparison,
    state.airDomainComparison,
    state.cruiseDomainComparison,
    state.carsDomainComparison,
  ].filter(Boolean);
  const visibleComparisonDomains = comparisonDomains(state.filters.domain).map(domainLabel);
  const comparisonBody = document.querySelector('#domain-comparison-table tbody');
  if (state.tablesLoading) {
    if (comparisonBody) comparisonBody.innerHTML = loadingTableRow(5);
  } else if (comparisonBody) {
    markTableBusy(document.getElementById('domain-comparison-table'), false);
    comparisonBody.innerHTML = comparisonRows.length
      ? comparisonRows.map((d) => `<tr><td>${escapeHtml(domainLabel(d.domain))}</td><td>${d.journeys.toLocaleString()}</td>
        <td>${d.conversionRate == null ? '—' : `${(d.conversionRate * 100).toFixed(1)}%`}</td>
        <td>${d.p95LatencyMs == null ? '—' : `${d.p95LatencyMs.toLocaleString()} ms`}</td>
        <td>${d.exceptions.toLocaleString()}</td></tr>`).join('')
      : `<tr><td colspan="5"><div class="state-block">${state.domainComparisonStale
        ? 'Loading Domain Comparison…'
        : visibleComparisonDomains.length
          ? `${visibleComparisonDomains.join(', ')} analytics are unavailable for the selected environment and time range.`
          : 'Domain Comparison is available only for Hotels and Cruises.'}</div></td></tr>`;
  }

  renderBarList('chart-exceptions-domain', a.exceptionsByDomain.map((d) => ({ label: domainLabel(d.domain), count: d.count })), 'label', 'count');
  renderBarList('chart-exceptions-component', a.exceptionsByComponent, 'component', 'count');

  document.querySelector('#supplier-table tbody').innerHTML = a.supplierPerformance.map((s) => `
    <tr><td>${escapeHtml(s.supplier)}</td><td>${escapeHtml(domainLabel(s.domain))}</td>
    <td>${s.availability}%</td><td>${s.p95LatencyMs} ms</td><td>${s.errorRate}%</td></tr>`).join('');

  renderBarList('top-failing-ops', a.topFailingOperations.map((o) => ({ label: o.operation, val: o.failureRate })), 'label', 'val', (v) => `${v}%`);
  renderBarList('top-latency-contributors', a.topLatencyContributors.map((o) => ({ label: o.component, val: o.avgMs })), 'label', 'val', (v) => `${v} ms`);
  renderBarList('most-affected-regions', a.mostAffectedRegions.map((r) => ({ label: r.region, val: r.incidents })), 'label', 'val');

  document.getElementById('most-affected-journeys').innerHTML = a.mostAffectedJourneys.map((id) => `<li>${escapeHtml(id)}</li>`).join('') || '<li>None currently.</li>';
  document.getElementById('operational-insights').innerHTML = a.operationalInsights.map((i) => `<li>${escapeHtml(i)}</li>`).join('');
}

/* -----------------------------------------------------------------------
   5e. SIMIAN ARMY (MONKEYS) TAB
   ----------------------------------------------------------------------- */
function modeClass(mode) {
  return { Observe: 'observe', Recommend: 'recommend', 'Approval Required': 'approval', Autonomous: 'autonomous' }[mode] || 'observe';
}

function modeDescription(mode) {
  return {
    Observe: 'Observe only: watches and raises findings/evidence, never proposes or executes an action.',
    Recommend: 'Recommend: suggests an action to a human operator, but never executes it automatically.',
    'Approval Required': 'Approval required: proposes a concrete action and waits for a human to approve it before executing.',
    Autonomous: 'Autonomous: executes actions automatically within its configured guardrails, no human approval needed.',
  }[mode] || 'Unknown mode.';
}

// Only these agents have a real backend (GetMonkeysFunction/RunMonkeyNowFunction) today; the
// rest of the roster is still mocked data with no wiring behind "Run Now"/"Pause"/etc. - their
// cards are marked "Under Construction" and their controls disabled, and their evidence is
// labeled as simulated.
const IMPLEMENTED_MONKEY_IDS = new Set(['latency-scout-monkey', 'exception-scout-monkey', 'inspector-monkey', 'rca-monkey', 'latency-spike-monkey', 'slo-burn-rate-monkey', 'doctor-monkey']);

// The implemented monkeys all support a real "Run Now" via RunMonkeyNowFunction - including the
// Doctor Monkey, which has no Timer trigger of its own (see DoctorMonkeyRunner) but does have its
// own on-demand run case there, so its button must not fall through to the simulated-toast branch.
const RUNNABLE_MONKEY_IDS = new Set(['latency-scout-monkey', 'exception-scout-monkey', 'inspector-monkey', 'latency-spike-monkey', 'slo-burn-rate-monkey', 'doctor-monkey']);

/** The two detection agents share every renderer - they are the same pipeline over a different board. */
const SCOUT_MONKEY_IDS = new Set(['latency-scout-monkey', 'exception-scout-monkey']);
const isScoutMonkey = (id) => SCOUT_MONKEY_IDS.has(id);

/** Kept in the codebase and still served by /api/monkeys, but off the board (decision 2026-10-08):
 *  they are paused rather than deleted, so emptying this set brings them back unchanged. */
const HIDDEN_MONKEY_IDS = new Set(['latency-spike-monkey', 'slo-burn-rate-monkey', 'doctor-monkey']);

function renderMonkeysTab() {
  const grid = document.getElementById('monkeys-grid');
  grid.innerHTML = state.monkeys.filter((m) => !HIDDEN_MONKEY_IDS.has(m.id)).map((m) => {
    const isImplemented = IMPLEMENTED_MONKEY_IDS.has(m.id);
    const isInspector = m.id === 'inspector-monkey';
    // The Inspector's proof of work is a queue plus one bar per investigation, not a fixed-cadence
    // cycle list, so it builds its own liveness block instead of the Scout's.
    const live = !isImplemented
      ? ''
      : (isInspector ? renderInspectorLiveness(m) : (Array.isArray(m.cycles) ? renderAgentLiveness(m) : ''));
    // "Pause" and "Change mode" are roster-era toggles: they move a field in this browser and
    // nothing else. Harmless on a simulated card; on the Inspector - a real agent spending millions
    // of tokens a day - a toast saying "Agent paused" beside a line still counting down to the next
    // claim is simply false. Pause is real now (POST /api/monkeys/{id}/pause); the mode switch is
    // still only a local field, so it stays disabled there.
    const lockedReason = isInspector
      ? 'Not available from the portal. Changing the mode here would only move a field in this browser; nothing would reach the agent. No API exposes it yet.'
      : null;
    const stateButton = (action, label, locked) => {
      const off = !isImplemented || locked;
      return `<button class="btn btn--icon btn--sm" data-action="${action}"${off ? ' disabled aria-disabled="true"' : ''}${locked ? ` title="${escapeHtml(locked)}"` : ''}>${escapeHtml(label)}</button>`;
    };
    // normalizeStatus() collapses anything outside healthy/degraded/critical to "Unknown", which
    // would label a deliberately paused agent as if its state were not known.
    const badge = m.status === 'paused'
      ? '<span class="status-badge status-badge--unknown">Paused</span>'
      : statusBadge(m.status === 'active' ? 'healthy' : m.status);
    return `
    <div class="monkey-card${isImplemented ? (live ? ' ' + liveCardModifier(m) : '') : ' monkey-card--under-construction'}" data-monkey-id="${escapeHtml(m.id)}">
      <div class="monkey-card__head">
        <div>
          <div class="monkey-card__name">${escapeHtml(m.name)}</div>
          <span class="mode-badge mode-badge--${modeClass(m.mode)}" data-tooltip="${escapeHtml(modeDescription(m.mode))}" tabindex="0">${escapeHtml(m.mode)}</span>
          ${isImplemented ? '' : '<span class="simulated-tag">Simulated — no agent running</span>'}
        </div>
        ${isImplemented ? badge : ''}
      </div>
      <p class="monkey-card__purpose">${escapeHtml(m.purpose)}</p>
      ${live}
      ${isImplemented ? '' : `
        <div class="heartbeat">
          <div class="heartbeat__bars">${'<div class="heartbeat__bar heartbeat__bar--none" title="No cycles recorded"></div>'.repeat(24)}</div>
        </div>
        <p class="simulated-note">No cycles recorded — the figures below are demonstration data.</p>`}
      ${isInspector ? '' : `
      <div class="monkey-card__stats">
        <div><span class="monkey-card__stat-value">${m.findings}</span><span class="monkey-card__stat-label">Findings</span></div>
        <div><span class="monkey-card__stat-value">${m.actionsExecuted}/${m.actionsProposed}</span><span class="monkey-card__stat-label">Actions run/proposed</span></div>
        <div><span class="monkey-card__stat-value">${Math.round(m.successRate * 100)}%</span><span class="monkey-card__stat-label">Success rate</span></div>
      </div>`}
      ${live ? '' : `
      <div style="font-size:12px;color:var(--text-secondary);margin-bottom:8px;">
        Last run ${formatRelativeTime(m.lastRun)} · Next run in ${Math.max(0, Math.round((new Date(m.nextRun) - Date.now()) / 60000))}m ·
        Impact prevented: <strong>${escapeHtml(m.impactPrevented)}</strong> · Risk: ${escapeHtml(m.risk)}
      </div>`}
      ${isInspector ? renderInspectorGuardrails(m) : `
      <ul class="guardrail-list">
        <li>Max affected instances: ${m.guardrails.maxAffectedInstances || 'n/a'}</li>
        <li>Allowed environments: ${m.guardrails.allowedEnvironments.join(', ')}</li>
        <li>Maintenance window only: ${m.guardrails.maintenanceWindowOnly ? 'Yes' : 'No'}</li>
        <li>Approval threshold: ${escapeHtml(m.guardrails.approvalThreshold)}</li>
        <li>Rollback available: ${m.guardrails.rollbackAvailable ? 'Yes' : 'No'} · Audit logging: ${m.guardrails.auditLogging ? 'Enabled' : 'Disabled'}</li>
      </ul>`}
      <h3 style="font-size:12px;margin:8px 0 4px;">Activity Timeline</h3>
      ${isScoutMonkey(m.id) ? renderScoutTimeline(m) : (isInspector ? renderInspectorTimeline(m) : `
      <ul class="activity-log">
        ${m.activity.map((a) => `<li><span class="activity-log__stage">${escapeHtml(a.stage)}</span>${escapeHtml(a.text)}</li>`).join('')}
      </ul>`)}
      <div class="monkey-card__actions">
        ${isInspector ? `
        <button class="btn btn--sm" data-action="queue-modal">Open queue</button>
        <button class="btn btn--icon btn--sm" data-action="evidence">View case file</button>
        ${stateButton('pause', m.paused ? 'Resume agent' : 'Pause agent', null)}
        ${stateButton('mode', 'Change mode', lockedReason)}
        <button class="btn btn--ghost btn--sm monkey-card__actions-aside" data-action="run" title="Claims the next package out of turn, without waiting for the agent's own timer. Real run, real tokens.">Run now</button>` : `
        <button class="btn btn--icon btn--sm" data-action="run"${isImplemented ? '' : ' disabled'}>Run Now</button>
        <button class="btn btn--icon btn--sm" data-action="evidence"${isImplemented ? '' : ' disabled'}>View Evidence</button>
        ${m.mode === 'Approval Required' ? `<button class="btn btn--icon btn--sm" data-action="approve"${isImplemented ? '' : ' disabled'}>Approve Action</button><button class="btn btn--icon btn--sm" data-action="reject"${isImplemented ? '' : ' disabled'}>Reject Action</button>` : ''}
        ${stateButton('pause', m.paused ? 'Resume Agent' : 'Pause Agent', null)}
        ${stateButton('mode', 'Change Mode', null)}`}
      </div>
      ${lockedReason ? '<p class="monkey-card__actions-note">Pause really stops the agent. Mode changes are still made with the <code>Inspector</code> configuration, not from this portal.</p>' : ''}
    </div>`;
  }).join('');

  wireMonkeyGrid(grid);

  startCountdownTicker();
  startScoutPoller();
  startInspectorPoller();
}

/** One listener on the grid itself, wired once and never again. Every control on these cards lives
 *  inside a block the Scout and Inspector pollers replace wholesale every 5-20s, so a listener
 *  bound per node is dead by the next tick - which is exactly how the Inspector's queue strip
 *  stopped opening after five seconds. Delegation is the only binding that outlives the refresh.
 *  Covers both the generic data-action buttons and the Scout's cycle filter. */
function wireMonkeyGrid(grid) {
  if (grid.dataset.wired === 'true') return;
  grid.dataset.wired = 'true';

  const monkeyOf = (el) => {
    const card = el.closest('.monkey-card');
    if (!card) return null;
    return state.monkeys.find((m) => m.id === card.dataset.monkeyId) || null;
  };

  const applyCycleFilter = (cycleId) => {
    state.scoutCycleFilter = state.scoutCycleFilter === cycleId ? null : cycleId;
    renderMonkeysTab();
  };

  grid.addEventListener('click', (e) => {
    if (e.target.closest('[data-action="clear-cycle-filter"]')) {
      state.scoutCycleFilter = null;
      renderMonkeysTab();
      return;
    }
    const cycle = e.target.closest('[data-cycle-id]');
    if (cycle && cycle.dataset.cycleId) { applyCycleFilter(cycle.dataset.cycleId); return; }

    const btn = e.target.closest('button[data-action]');
    if (!btn || btn.disabled) return;
    const monkey = monkeyOf(btn);
    if (monkey) handleMonkeyAction(monkey, btn.dataset.action);
  });

  grid.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    const cycle = e.target.closest('[data-cycle-id]');
    if (!cycle || !cycle.dataset.cycleId) return;
    e.preventDefault();
    applyCycleFilter(cycle.dataset.cycleId);
  });
}

/** Matches the backend's abandoned-cycle window: a cycle takes ~100s, so anything older than this
 *  was killed mid-flight and must not keep the card saying "Analyzing now". */
const STALLED_CYCLE_MS = 10 * 60 * 1000;

function liveCardModifier(m) {
  if (m.status === 'error') return 'monkey-card--live-error';
  if (m.status === 'paused') return 'monkey-card--live-paused';
  return 'monkey-card--live';
}

/** Proof of work, in four layers: a pulse with a ticking countdown, the heartbeat of past cycles,
 *  a receipt of what the last one actually did, and cumulative totals. The point of all four is
 *  that a card with no agent behind it cannot produce any of them. */
function renderAgentLiveness(m) {
  const cycles = m.cycles || [];
  if (!cycles.length && !m.lastCycle) return '';

  const running = m.currentCycle && (Date.now() - new Date(m.currentCycle.startedAt)) < STALLED_CYCLE_MS
    ? m.currentCycle
    : null;

  return `
    <div data-agent-liveness>
      ${renderLiveLine(m, running)}
      ${renderCycleSteps(running, m.lastCycle, m.board)}
      ${renderHeartbeat(cycles, m.cycleMinutes || 5)}
      ${renderCycleReceipt(m.lastCycle)}
      ${renderAgentTotals(m.stats)}
    </div>`;
}

function renderLiveLine(m, running) {
  if (running) {
    return `
      <div class="agent-live">
        <span class="agent-pulse agent-pulse--busy"></span>
        <strong style="color:var(--status-info)">Analyzing now</strong>
        <span class="agent-live__sep">·</span>
        <span>started ${formatRelativeTime(running.startedAt)}</span>
        ${running.clubsWatched ? `<span class="agent-live__sep">·</span><span><strong>${formatCount(running.boardRowsEvaluated)}</strong> rows across <strong>${formatCount(running.clubsWatched)}</strong> clubs so far</span>` : ''}
      </div>`;
  }
  if (m.status === 'paused') {
    // Two different causes land on the same status, and saying the wrong one sends whoever reads
    // it to the wrong switch: the portal pause lives in the database, the other in configuration.
    const section = m.board === 'exception' ? 'ScoutException' : 'ScoutLatency';
    return `
      <div class="agent-live">
        <span class="agent-pulse agent-pulse--idle"></span>
        <strong style="color:var(--status-unknown)">${m.paused ? 'Paused from this portal' : 'Paused by configuration'}</strong>
        <span class="agent-live__sep">·</span><span>${m.paused ? 'no board query and no cycle recorded' : `${section}:Enabled = false`}</span>
      </div>`;
  }
  const pulse = m.status === 'error' ? 'agent-pulse--error' : '';
  const label = m.status === 'error'
    ? '<strong style="color:var(--status-critical)">Last cycle failed</strong>'
    : 'Live';
  return `
    <div class="agent-live">
      <span class="agent-pulse ${pulse}"></span>
      <span>${label}</span>
      <span class="agent-live__sep">·</span>
      <span>last cycle <strong>${formatRelativeTime(m.lastRun)}</strong></span>
      <span class="agent-live__sep">·</span>
      <span>next in <strong data-countdown-to="${escapeHtml(m.nextRun)}">--:--</strong></span>
      <span class="agent-live__sep">·</span>
      <span>every <strong>${m.cycleMinutes || 5} min</strong></span>
    </div>`;
}

/** One bar per recorded cycle, oldest to newest, padded on the left so the strip reads as a
 *  timeline rather than as a bar chart that happens to be short. */
function renderHeartbeat(cycles, cycleMinutes) {
  const slots = 24;
  const ordered = [...cycles].reverse().slice(-slots);
  const padding = Array.from({ length: Math.max(0, slots - ordered.length) }, () => null);

  const bars = padding.concat(ordered).map((c) => {
    if (!c) return '<div class="heartbeat__bar heartbeat__bar--none" title="No cycle recorded — the agent was not running"></div>';
    const when = new Date(c.startedAt).toLocaleTimeString();
    const detail = c.status === 'failed'
      ? (c.errorMessage || 'failed')
      : `${formatCount(c.boardRowsEvaluated)} rows · ${formatCount(c.clubsWatched)} clubs · ${c.packagesCreated} new · ${c.packagesSkippedDuplicate} already known`;
    const selected = state.scoutCycleFilter === c.id ? ' heartbeat__bar--selected' : '';
    return `<div class="heartbeat__bar heartbeat__bar--${escapeHtml(c.status)}${selected}" role="button" tabindex="0" data-cycle-id="${escapeHtml(c.id || '')}" title="${escapeHtml(`${when} · ${Math.round((c.durationMs || 0) / 1000)}s · ${detail} — click to filter the timeline`)}"></div>`;
  }).join('');

  return `
    <div class="heartbeat">
      <div class="heartbeat__bars">${bars}</div>
      <div class="heartbeat__axis"><span>${slots * cycleMinutes} min ago</span><span>now</span></div>
      <div class="heartbeat__legend">
        <span><i style="background:var(--status-info)"></i>found something</span>
        <span><i style="background:var(--status-healthy);opacity:.5"></i>checked, nothing new</span>
        <span><i style="background:var(--status-critical)"></i>failed</span>
        <span><i style="background:var(--status-unknown);opacity:.35"></i>paused</span>
        <span><i style="background:var(--card-border)"></i>no data</span>
      </div>
    </div>`;
}

/** "Checked, nothing new" is a result, not an absence of one - and it is the single clearest
 *  signal that an unattended agent is actually working. */
function renderCycleReceipt(c) {
  if (!c) return '';
  if (c.status === 'failed') {
    return `
      <div class="cycle-receipt">
        <div class="cycle-receipt__title">Last cycle · ${formatRelativeTime(c.startedAt)} · ${Math.round((c.durationMs || 0) / 1000)}s</div>
        <div class="cycle-receipt__error">${escapeHtml(c.errorMessage || 'The cycle failed without a recorded reason.')}</div>
      </div>`;
  }

  const verdict = c.packagesCreated > 0
    ? `Analyzed <strong>${c.packagesCreated}</strong> new case${c.packagesCreated === 1 ? '' : 's'} and skipped <strong>${c.packagesSkippedDuplicate}</strong> already-known one${c.packagesSkippedDuplicate === 1 ? '' : 's'}.`
    : `Every candidate had already been handed off — <strong>0</strong> LLM calls, <strong>${c.packagesSkippedDuplicate}</strong> repeat analyses avoided.`;

  return `
    <div class="cycle-receipt">
      <div class="cycle-receipt__title">Last cycle · ${formatRelativeTime(c.startedAt)} · ${Math.round((c.durationMs || 0) / 1000)}s · ${c.packagesCreated > 0 ? 'found something' : 'checked, nothing new'}</div>
      <div class="cycle-receipt__flow">
        <span class="cycle-chip"><strong>${formatCount(c.boardRowsEvaluated)}</strong> rows</span><span class="cycle-arrow">→</span>
        <span class="cycle-chip"><strong>${formatCount(c.clubsWatched)}</strong> clubs</span><span class="cycle-arrow">→</span>
        <span class="cycle-chip cycle-chip--info"><strong>${c.candidatesFound}</strong> candidates</span><span class="cycle-arrow">→</span>
        <span class="cycle-chip cycle-chip--muted"><strong>${c.packagesSkippedDuplicate}</strong> already known</span><span class="cycle-arrow">→</span>
        <span class="cycle-chip cycle-chip--ok cycle-chip--clickable${state.scoutCycleFilter === c.id ? ' cycle-chip--selected' : ''}" role="button" tabindex="0" data-cycle-id="${escapeHtml(c.id || '')}" title="Click to filter the timeline to this cycle"><strong>${c.packagesCreated}</strong> handed to L3</span>
        ${c.packagesSkippedCap ? `<span class="cycle-chip cycle-chip--muted"><strong>${c.packagesSkippedCap}</strong> over cap</span>` : ''}
        ${c.packagesVetoedByScout ? `<span class="cycle-chip cycle-chip--muted"><strong>${c.packagesVetoedByScout}</strong> vetoed</span>` : ''}
      </div>
      <div class="cycle-receipt__verdict">${verdict}</div>
    </div>`;
}

const CYCLE_PHASE_LABELS = {
  'latency-board': 'Latency board',
  'exception-board': 'Exception board',
  'triage': 'Triage',
  'analysis': 'LLM analysis',
  'evidence': 'Raw evidence',
};

/** A cycle reads one board, so the idle bar shows that scout's own steps. Falling back to the
 *  latency board keeps an older payload (no `board` field) rendering something sane. */
function defaultCyclePhases(board) {
  const boardPhase = board === 'exception' ? 'exception-board' : 'latency-board';
  return [boardPhase, 'triage', 'analysis', 'evidence'].map((key) => ({ key, weight: 1 }));
}

/** The bar is always on screen: mid-cycle it shows where the agent is, between cycles it shows the
 *  steps the last cycle completed. A cycle with nothing new still walks every step - the
 *  service reports the remaining phases before closing - so the process never looks half-finished. */
function renderCycleSteps(running, lastCycle, board) {
  const source = running?.phases || defaultCyclePhases(board);
  const phases = source.map((p) => (typeof p === 'string' ? { key: p, weight: 1 } : p));

  const idle = !running;
  const failed = idle && lastCycle?.status === 'failed';
  const current = running ? phases.findIndex((p) => p.key === running.phase) : -1;

  const stepClass = (i) => {
    if (idle) return failed ? ' cycle-step--failed' : ' cycle-step--done';
    if (current < 0) return '';
    return i < current ? ' cycle-step--done' : (i === current ? ' cycle-step--now' : '');
  };

  return `
    <div class="cycle-steps"${idle ? ' data-idle' : ''}>
      ${phases.map((p, i) => {
        const label = CYCLE_PHASE_LABELS[p.key] || p.key;
        const hint = idle
          ? (failed ? `${label} — the last cycle failed` : `${label} — completed in the last cycle`)
          : `${label} — about ${p.weight}% of a typical cycle`;
        return `<div class="cycle-step${stepClass(i)}" title="${escapeHtml(hint)}"><div class="cycle-step__bar"></div>${escapeHtml(label)}</div>`;
      }).join('')}
    </div>`;
}

/** Tokens are in here deliberately: they are the one number that cannot exist unless a model
 *  actually ran. */
function renderAgentTotals(s) {
  if (!s || !s.cycles) return '';
  return `
    <div class="agent-totals">
      <div><span class="agent-totals__value">${formatCount(s.cycles)}</span><span class="agent-totals__label">cycles</span></div>
      <div><span class="agent-totals__value">${formatCount(s.boardRowsEvaluated)}</span><span class="agent-totals__label">rows read</span></div>
      <div><span class="agent-totals__value">${formatCount(s.packagesCreated)}</span><span class="agent-totals__label">handoffs to L3</span></div>
      <div><span class="agent-totals__value">${formatCompact(s.tokensIn + s.tokensOut)}</span><span class="agent-totals__label">tokens analyzed</span></div>
    </div>`;
}

/** The timeline answers "what did this cycle actually do?". Filtering it from the heartbeat is the
 *  only way to connect a bar to its packages - and a cycle that handed off nothing still has a
 *  story: the candidates it recognised as already known. */
function renderScoutTimeline(m) {
  const cycleId = state.scoutCycleFilter;
  const cycle = cycleId ? (m.cycles || []).find((c) => c.id === cycleId) : null;
  const all = m.activity || [];
  const items = cycle ? all.filter((a) => a.scoutRunId === cycleId) : all;

  const header = cycle
    ? `<div class="timeline-filter">
         <span class="timeline-filter__text">
           Cycle of <strong>${new Date(cycle.startedAt).toLocaleTimeString()}</strong> ·
           ${formatCount(cycle.boardRowsEvaluated)} rows · ${formatCount(cycle.clubsWatched)} clubs ·
           <strong>${cycle.candidatesFound}</strong> candidates
         </span>
         <button class="timeline-filter__clear" type="button" data-action="clear-cycle-filter" title="Show every handoff again">Clear ✕</button>
       </div>`
    : '';

  const skipped = cycle ? renderSkippedCandidates(cycle, all) : '';

  const list = items.length
    ? `<ul class="activity-log">
         ${items.map((a) => `
           <li class="activity-log__item--handed">
             <span class="activity-log__stage">${escapeHtml(a.stage)}</span>${escapeHtml(a.text)}
             ${cycle ? '' : `<span class="activity-log__when">${formatRelativeTime(a.time)}</span>`}
           </li>`).join('')}
       </ul>`
    : (cycle
      ? `<p class="timeline-empty">This cycle handed off <strong>nothing new</strong> — every candidate it found was already with L3.</p>`
      : '<p class="timeline-empty">No handoffs yet.</p>');

  return `${header}${list}${skipped}`;
}

/** Names the candidates a cycle recognised instead of only counting them, and links the ones whose
 *  original package is still in the recent list. */
function renderSkippedCandidates(cycle, activity) {
  const ids = Array.isArray(cycle.skippedCaseIds) ? cycle.skippedCaseIds : [];
  if (!ids.length) {
    return cycle.packagesSkippedDuplicate
      ? `<p class="timeline-empty">${cycle.packagesSkippedDuplicate} candidate(s) were skipped as already known, but this cycle ran before the detail was recorded.</p>`
      : '';
  }

  const byCaseId = new Map(activity.map((a) => [a.caseId, a]));
  const rows = ids.map((caseId) => {
    const original = byCaseId.get(caseId);
    const label = original ? original.text : caseId.split('|').slice(0, -1).join('·').trim();
    return `
      <li class="activity-log__item--known">
        <span class="activity-log__stage activity-log__stage--known">already known</span>${escapeHtml(label)}
        ${original ? `<span class="activity-log__when">handed off ${formatRelativeTime(original.time)}</span>` : '<span class="activity-log__when">handed off earlier</span>'}
      </li>`;
  }).join('');

  return `
    <p class="timeline-section">Recognised and skipped — no LLM call was spent on these</p>
    <ul class="activity-log">${rows}</ul>`;
}

function formatCount(n) {
  return typeof n === 'number' ? n.toLocaleString() : '0';
}

function formatCompact(n) {
  if (!n) return '0';
  if (n >= 1000000) return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
  if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
  return String(n);
}

/** The countdown is the cheapest and most convincing signal of all: it moves. One interval drives
 *  every countdown on the page, and it stops itself once none are left on screen. */
let countdownTimer = null;
function startCountdownTicker() {
  if (countdownTimer) return;
  const tick = () => {
    const targets = document.querySelectorAll('[data-countdown-to]');
    if (!targets.length) { clearInterval(countdownTimer); countdownTimer = null; return; }
    targets.forEach((el) => {
      const left = Math.max(0, Math.round((new Date(el.dataset.countdownTo) - Date.now()) / 1000));
      el.textContent = left === 0 ? 'any moment' : `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
    });
  };
  tick();
  countdownTimer = setInterval(tick, 1000);
}

// A cycle lasts ~100s, so the page's 90s refresh can miss it almost entirely. This poller runs
// only while the Simian Army tab is open and replaces just the liveness block, so re-rendering
// every few seconds costs nothing and keeps no stale listeners.
const SCOUT_POLL_BUSY_MS = 5000;
const SCOUT_POLL_IDLE_MS = 20000;
let scoutPollTimer = null;

// Two independent pollers now watch the same endpoint on different cadences. Without this, a busy
// Scout and a busy Inspector would issue two /api/monkeys requests every five seconds for one
// payload that already contains both cards.
let monkeysPollInFlight = null;
function fetchMonkeysForPoll() {
  if (monkeysPollInFlight) return monkeysPollInFlight;
  monkeysPollInFlight = dataProvider.getMonkeys().finally(() => {
    setTimeout(() => { monkeysPollInFlight = null; }, 1500);
  });
  return monkeysPollInFlight;
}

function startScoutPoller() {
  stopScoutPoller();
  const poll = async () => {
    // Both detection cards are refreshed by one poll: they run on the same cadence and share the
    // /api/monkeys payload, so polling them separately would double the requests for nothing.
    const cards = [...SCOUT_MONKEY_IDS].map((id) => ({
      id,
      node: document.querySelector(`.monkey-card[data-monkey-id="${id}"] [data-agent-liveness]`),
    })).filter((c) => c.node);
    if (!cards.length) { stopScoutPoller(); return; }

    // Still mounted but on another tab: keep the timer alive without spending a request.
    const panel = document.getElementById('tab-monkeys');
    if (panel && panel.hidden) { schedule(SCOUT_POLL_IDLE_MS); return; }

    try {
      const monkeys = await fetchMonkeysForPoll();
      let anyBusy = false;

      for (const { id, node } of cards) {
        const scout = monkeys.find((m) => m.id === id);
        if (!scout) continue;

        const index = state.monkeys.findIndex((m) => m.id === id);
        if (index >= 0) state.monkeys[index] = scout;

        const fresh = renderAgentLiveness(scout);
        if (fresh) node.outerHTML = fresh;
        if (scout.currentCycle) anyBusy = true;
      }

      startCountdownTicker();
      schedule(anyBusy ? SCOUT_POLL_BUSY_MS : SCOUT_POLL_IDLE_MS);
    } catch (err) {
      // A failed poll is not worth a toast - the next tick retries.
      schedule(SCOUT_POLL_IDLE_MS);
    }
  };
  const schedule = (ms) => { scoutPollTimer = setTimeout(poll, ms); };
  schedule(SCOUT_POLL_BUSY_MS);
}

function stopScoutPoller() {
  if (scoutPollTimer) { clearTimeout(scoutPollTimer); scoutPollTimer = null; }
}

/* -----------------------------------------------------------------------
   5e-bis. INSPECTOR MONKEY (L3 multi-agent analysis)
   The Scout answers "did anything happen?"; the Inspector answers "what was
   it?". Its card therefore proves three different things: that work is
   queued and being drained, that past investigations really happened, and
   how much of what the models cited could be verified - the one figure a
   confident-sounding model cannot inflate.
   ----------------------------------------------------------------------- */

/** Below this, the card paints the citation rate amber: most of what was cited could not be confirmed. */
const INSPECTOR_CITATION_WARN_RATIO = 0.8;

const INSPECTOR_PHASE_LABELS = {
  preprocess: 'Pre',
  scope: 'Scope',
  agents: 'Agents',
  debate: 'Debate',
  synthesis: 'Synth',
  grounding: 'Ground',
  verdict: 'Verdict',
};

const INSPECTOR_PHASE_ORDER = Object.keys(INSPECTOR_PHASE_LABELS);

/** Whitelisted so a status coming off the wire can never be interpolated into a class name. */
const INSPECTOR_BAR_CLASS = {
  escalated: 'heartbeat__bar--escalated',
  resolved: 'heartbeat__bar--detected',
  closed_no_issue: 'heartbeat__bar--clear',
  failed: 'heartbeat__bar--failed',
  running: 'heartbeat__bar--running',
};

const INSPECTOR_STATUS_LABEL = {
  escalated: 'Escalated',
  resolved: 'Resolved',
  closed_no_issue: 'Closed, no issue',
  failed: 'Failed',
  running: 'Investigating',
  queued: 'Queued',
};

const INSPECTOR_PILL_CLASS = {
  escalated: 'insp-pill--esc',
  resolved: 'insp-pill--res',
  closed_no_issue: 'insp-pill--closed',
  failed: 'insp-pill--fail',
  running: 'insp-pill--live',
  queued: 'insp-pill--queued',
};

/** The five specialists, in the order the graph lays them out. Labels are the backend's AgentRole values. */
const INSPECTOR_SPECIALISTS = ['LogsAgent', 'MemoryAgent', 'ReproAgent', 'CodeAgent', 'DocsAgent'];

function inspectorStatusLabel(status) {
  return INSPECTOR_STATUS_LABEL[status] || 'Unknown';
}

function inspectorPill(status) {
  const cls = INSPECTOR_PILL_CLASS[status] || 'insp-pill--queued';
  return `<span class="insp-pill ${cls}">${escapeHtml(inspectorStatusLabel(status))}</span>`;
}

const INSPECTOR_ALERT_LABEL = { red_alert: 'Red Alert', amber: 'Amber', informational: 'Informational' };

/** The three epistemic states an RCA statement can carry. Same vocabulary the Scout uses on its
 *  evidence - the team has seen it before, and an RCA is read by people who were not in the
 *  investigation. */
const RCA_CONFIDENCE = {
  fact: { label: 'measured', help: 'Measured, or taken from an artifact this investigation verified exists.' },
  derived: { label: 'derived', help: 'Arithmetic over what was measured - durations, counts, percentages.' },
  inference: { label: 'inferred', help: "The model's reading, including anything suggested by a past incident. Not evidence." },
};

const RCA_SOURCE_LABEL = {
  rca_document: 'reviewed RCA',
  incident_ticket: 'ticket',
  incident_document: 'incident doc',
};
const INSPECTOR_ALERT_CLASS = { red_alert: 'insp-alert--red', amber: 'insp-alert--amber', informational: 'insp-alert--info' };

/** The severity the Red Alert policy assigned, with its stated reason in the tooltip.
 *
 *  Renders nothing without a reason: cases investigated before the policy existed carry the
 *  column default, and painting them "Informational" would claim a judgement nobody made. An
 *  honest blank is the same treatment the Tokens column already gets. */
/** Who fixes it. Distinct from the Club column, which is who is affected.
 *
 *  An unrecorded owner is shown as an explicit gap, not as a blank: the ownership map is empty
 *  until the named responders are agreed, and that is a dependency the screen should keep visible
 *  rather than hide. A vertical-wide match is flagged too - "somebody owns this vertical" and
 *  "somebody accepted this service" are different promises. */
function inspectorOwner(team, resolvedBy) {
  if (!team) {
    return '<span class="insp-owner insp-owner--none" title="No team is recorded as owning this service. The ownership map is empty until the named responders are agreed.">Unassigned</span>';
  }
  const fallback = resolvedBy === 'vertical-fallback';
  const hint = fallback
    ? 'Matched the whole vertical, not this stage specifically.'
    : 'This team is recorded as owning this exact journey stage.';
  return `<span class="insp-owner" title="${escapeHtml(hint)}">${escapeHtml(team)}${fallback ? ' <span class="insp-owner__fb">vertical</span>' : ''}</span>`;
}

function inspectorAlertBadge(level, reason) {
  if (!reason) return '<span style="color:var(--text-muted)" title="This case was investigated before the Red Alert policy existed, so no severity was assessed for it.">—</span>';
  const key = INSPECTOR_ALERT_LABEL[level] ? level : 'informational';
  return `<span class="insp-alert ${INSPECTOR_ALERT_CLASS[key]}" title="${escapeHtml(reason)}">${escapeHtml(INSPECTOR_ALERT_LABEL[key])}</span>`;
}

/** A CaseId is "{Source} | {Vertical} | {entity} | {stage} | {hour bucket}". The whole string does
 *  not fit a one-line cell, but dropping the bucket collapses a day of investigations into eight
 *  identical labels - the stage plus the hour is what makes one row distinguishable from the next.
 *  So the verbose head (source/vertical/host) goes, the tail stays. */
function inspectorCaseLabel(caseId) {
  if (!caseId) return '—';
  const parts = String(caseId).split('|').map((p) => p.trim()).filter(Boolean);
  if (parts.length < 2) return String(caseId);

  const bucket = inspectorBucketClock(parts[parts.length - 1]);
  if (!bucket) return parts.slice(-2).join(' · ');
  return `${parts[parts.length - 2]} · ${bucket}`;
}

/** The bucket is an ISO timestamp; anything else (a dedup key that ends in a stage, say) is left
 *  alone rather than guessed at. */
function inspectorBucketClock(value) {
  if (!value || !/^\d{4}-\d{2}-\d{2}T/.test(value)) return null;
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return null;
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/** The club's own name when the catalogue knows it, the brand next, the club id when neither does,
 *  and an honest "unknown" only when nothing exists. The brand is a label shared by several clubs -
 *  the id is the identity. */
function inspectorBrand(entry) {
  if (!entry) return 'Unknown club';
  if (entry.clubName) return entry.clubName;
  if (entry.brandName) return entry.brandName;
  const id = entry.clubId || entry.icePartnerId;
  return id ? `club ${id}` : 'Unknown club';
}

/** A card scoped by the global filters must say so. An empty list under a club filter and a dead
 *  agent look identical otherwise, and that is the kind of silence this portal does not allow. */
function renderInspectorFilterNote(m) {
  const f = m.appliedFilter;
  if (!f || (!f.domain && !f.club)) return '';
  const parts = [];
  if (f.domain) parts.push(escapeHtml(f.domain));
  if (f.club) {
    const option = (state.clubOptions || []).find((c) => c.value === f.club);
    parts.push(escapeHtml(option ? option.label : `club ${f.club}`));
  }
  return `<p class="insp-filternote">Showing only investigations for ${parts.join(' · ')}. The counters below are cumulative and are not filtered.</p>`;
}

/** Proof of work for an agent that runs on demand: the backlog, the case in flight, the history of
 *  finished ones, a receipt for the last, and the three figures the card is judged on. */
function renderInspectorLiveness(m) {
  const cases = Array.isArray(m.cases) ? m.cases : [];
  const stats = m.inspectorStats;
  const filterNote = renderInspectorFilterNote(m);
  // With a filter on, an empty result is a statement about the filter, not about the agent.
  if (!cases.length && !m.currentCase && !m.queue) return filterNote;

  const running = m.currentCase && (Date.now() - new Date(m.currentCase.startedAt)) < STALLED_CYCLE_MS
    ? m.currentCase
    : null;
  const lastClosed = cases.find((c) => c.status !== 'running');

  return `
    <div data-agent-liveness>
      ${filterNote}
      ${renderInspectorQueueStrip(m.queue)}
      ${renderInspectorLiveLine(m, running)}
      ${renderInspectorPhases(running, lastClosed)}
      ${renderInspectorHeartbeat(cases)}
      ${renderInspectorReceipt(lastClosed)}
      ${renderInspectorStats(stats)}
    </div>`;
}

/** A heartbeat of finished cases cannot answer "is it falling behind?" or "has it hit today's cap?". */
function renderInspectorQueueStrip(q) {
  if (!q) return '';
  const waiting = q.queued ?? 0;
  const cap = q.maxInvestigationsPerDay ?? 0;
  const today = q.investigationsToday ?? 0;
  const atCap = cap > 0 && today >= cap;
  const oldest = q.oldestQueuedAt ? `oldest waiting ${formatRelativeTime(q.oldestQueuedAt)}` : 'nothing waiting';

  // A button, not a div: "N waiting" without being able to see WHICH is a counter, not a queue.
  return `
    <button type="button" class="insp-queue${waiting > 0 ? ' insp-queue--busy' : ''}" data-action="queue" aria-expanded="false" title="Show what is waiting">
      <span class="insp-queue__n">${formatCount(waiting)}</span>
      <span class="insp-queue__lbl">waiting</span>
      <span class="agent-live__sep">·</span>
      <span>concurrency <strong>${formatCount(q.concurrency ?? 0)}</strong></span>
      <span class="agent-live__sep">·</span>
      <span class="${atCap ? 'insp-queue__cap--hit' : ''}"><strong>${formatCount(today)}</strong> of ${cap > 0 ? formatCount(cap) : '—'} today</span>
      <span class="insp-queue__right">${escapeHtml(oldest)}${q.failedToday ? ` · ${formatCount(q.failedToday)} failed today` : ''}<span class="insp-queue__chev">▾</span></span>
    </button>
    <div class="insp-queue-panel" data-inspector-queue-panel hidden></div>`;
}

/** Fills the in-card backlog on demand: /api/monkeys carries the counters but not the names of the
 *  packages waiting, and the whole point of opening this is to see which ones they are. */
async function toggleInspectorQueuePanel(card) {
  const button = card.querySelector('.insp-queue[data-action="queue"]');
  const panel = card.querySelector('[data-inspector-queue-panel]');
  if (!button || !panel) return;

  if (!panel.hidden) {
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    return;
  }

  panel.hidden = false;
  button.setAttribute('aria-expanded', 'true');
  panel.innerHTML = '<p class="insp-queue-panel__empty">Loading the backlog…</p>';

  try {
    const response = await fetch('/api/inspector/queue');
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    state.inspectorQueue = await response.json();
  } catch (err) {
    panel.innerHTML = `<p class="insp-queue-panel__empty">Could not load the queue: ${escapeHtml(err.message || 'request failed')}</p>`;
    return;
  }

  panel.innerHTML = renderInspectorQueuePanel(state.inspectorQueue);
}

function renderInspectorQueuePanel(queue) {
  const items = (queue && queue.queued) || [];
  if (!items.length) {
    return '<p class="insp-queue-panel__empty">Nothing waiting. Every package the Scout handed over has been investigated.</p>';
  }

  const rows = items.slice(0, 10).map((item) => {
    const retrying = (item.attempts ?? 0) > 1;
    // A lease that is still in the future means someone is working on it right now.
    const leased = item.leasedUntil && new Date(item.leasedUntil) > new Date();
    return `
      <li class="insp-queue-item">
        <span class="insp-queue-item__dot${leased ? ' insp-queue-item__dot--live' : ''}"></span>
        <span class="insp-queue-item__who">
          <strong>${escapeHtml(inspectorQueueLabel(item.dedupKey))}</strong>
          <span class="insp-queue-item__key">${escapeHtml(item.dedupKey || '')}</span>
        </span>
        <span class="insp-queue-item__meta">
          ${leased ? 'investigating now' : `queued ${formatRelativeTime(item.enqueuedAt)}`}
          ${retrying ? `<span class="insp-queue-item__retry">attempt ${item.attempts} of ${item.maxAttempts}</span>` : ''}
        </span>
      </li>`;
  }).join('');

  const more = items.length > 10
    ? `<li class="insp-queue-panel__empty">+ ${formatCount(items.length - 10)} more — open the queue for the full list.</li>`
    : '';

  const lastError = items.find((i) => i.lastError);

  return `
    <ul class="insp-queue-list">${rows}${more}</ul>
    ${lastError ? `<p class="insp-queue-panel__err">Most recent failure: ${escapeHtml(lastError.lastError)}</p>` : ''}`;
}

/** The club/host is the only part of a CaseId a human reads first - "SOR | Hotels | host:x | stage | bucket". */
function inspectorQueueLabel(dedupKey) {
  if (!dedupKey) return 'Unknown package';
  const parts = String(dedupKey).split('|').map((p) => p.trim());
  const entity = parts.find((p) => p.startsWith('host:') || p.startsWith('club:'));
  const stage = parts.length >= 2 ? parts[parts.length - 2] : '';
  return [entity || parts[0], stage].filter(Boolean).join(' · ');
}

function renderInspectorLiveLine(m, running) {
  const waiting = m.queue ? (m.queue.queued ?? 0) : 0;
  if (running) {
    return `
      <div class="agent-live">
        <span class="agent-pulse agent-pulse--busy"></span>
        <strong style="color:var(--status-info)">Investigating now</strong>
        <span class="agent-live__sep">·</span>
        <span>${escapeHtml(inspectorBrand(running))} · started ${formatRelativeTime(running.startedAt)}</span>
        <span class="agent-live__sep">·</span>
        <span><strong>${formatCount(waiting)}</strong> waiting in queue</span>
      </div>`;
  }
  if (m.status === 'paused') {
    // Two different ways to be stopped, and they are not interchangeable: one was done from here
    // and can be undone from here, the other needs a redeploy.
    return `
      <div class="agent-live">
        <span class="agent-pulse agent-pulse--idle"></span>
        <strong style="color:var(--status-unknown)">Paused</strong>
        <span class="agent-live__sep">·</span><span>${m.paused ? 'paused from this portal — no package is being claimed' : 'Inspector:Enabled = false'}</span>
      </div>`;
  }
  const pulse = m.status === 'error' ? 'agent-pulse--error' : '';
  const label = m.status === 'error'
    ? '<strong style="color:var(--status-critical)">Last investigation failed</strong>'
    : 'Live';
  return `
    <div class="agent-live">
      <span class="agent-pulse ${pulse}"></span>
      <span>${label}</span>
      <span class="agent-live__sep">·</span>
      <span>last claim <strong>${formatRelativeTime(m.lastRun)}</strong></span>
      <span class="agent-live__sep">·</span>
      <span>next claim in <strong data-countdown-to="${escapeHtml(m.nextRun)}">--:--</strong></span>
      <span class="agent-live__sep">·</span>
      <span>checks every <strong>${m.cycleMinutes || 1} min</strong></span>
    </div>`;
}

/** The seven phases of one investigation. Idle, it shows the steps the last one completed, so the
 *  process never looks half-finished between cases. */
function renderInspectorPhases(running, lastClosed) {
  const source = running && Array.isArray(running.phases) && running.phases.length
    ? running.phases
    : INSPECTOR_PHASE_ORDER.map((key) => ({ key, weight: 1 }));
  const phases = source.map((p) => (typeof p === 'string' ? { key: p, weight: 1 } : p));

  const idle = !running;
  const failed = idle && lastClosed && lastClosed.status === 'failed';
  const current = running ? phases.findIndex((p) => p.key === running.phase) : -1;

  const stepClass = (i) => {
    if (idle) return failed ? ' cycle-step--failed' : ' cycle-step--done';
    if (current < 0) return '';
    return i < current ? ' cycle-step--done' : (i === current ? ' cycle-step--now' : '');
  };

  return `
    <div class="cycle-steps"${idle ? ' data-idle' : ''}>
      ${phases.map((p, i) => {
        const label = INSPECTOR_PHASE_LABELS[p.key] || p.key;
        const hint = idle
          ? (failed ? `${label} — the last investigation failed` : `${label} — completed in the last investigation`)
          : `${label} — step ${i + 1} of ${phases.length}`;
        return `<div class="cycle-step${stepClass(i)}" title="${escapeHtml(hint)}"><div class="cycle-step__bar"></div>${escapeHtml(label)}</div>`;
      }).join('')}
    </div>`;
}

/** One bar per investigation, oldest to newest, coloured by outcome. A card with no agent behind it
 *  cannot produce a run of real outcomes. */
function renderInspectorHeartbeat(cases) {
  const slots = 24;
  const ordered = [...cases].reverse().slice(-slots);
  const padding = Array.from({ length: Math.max(0, slots - ordered.length) }, () => null);

  const bars = padding.concat(ordered).map((c) => {
    if (!c) return '<div class="heartbeat__bar heartbeat__bar--none" title="No investigation recorded here"></div>';
    const cls = INSPECTOR_BAR_CLASS[c.status] || 'heartbeat__bar--none';
    const when = new Date(c.startedAt).toLocaleTimeString();
    const citations = c.citationsTotal > 0 ? `${c.citationsVerified} of ${c.citationsTotal} citations verified` : 'nothing cited';
    const detail = c.status === 'failed'
      ? (c.errorMessage || 'failed without a recorded reason')
      : `${inspectorStatusLabel(c.status)} · ${citations}`;
    return `<div class="heartbeat__bar ${cls}" title="${escapeHtml(`${when} · ${inspectorBrand(c)} · ${formatDuration(c.durationMs)} · ${detail}`)}"></div>`;
  }).join('');

  return `
    <div class="heartbeat">
      <div class="heartbeat__bars">${bars}</div>
      <div class="heartbeat__axis"><span>${slots} investigations ago</span><span>now</span></div>
      <div class="heartbeat__legend">
        <span><i style="background:var(--status-degraded)"></i>escalated</span>
        <span><i style="background:var(--status-info)"></i>resolved with fix</span>
        <span><i style="background:var(--status-healthy);opacity:.5"></i>closed, no issue</span>
        <span><i style="background:var(--status-critical)"></i>failed</span>
        <span><i class="heartbeat__legend-running"></i>investigating now</span>
        <span><i style="background:var(--card-border)"></i>no data</span>
      </div>
    </div>`;
}

function renderInspectorReceipt(c) {
  if (!c) return '';
  // The shared .cycle-receipt__title uppercases its content, which is fine for a label but not for
  // the data in it: it turned a club into "T-MOBILE" and a duration into "1M 48S".
  const head = `Last investigation · <strong>${escapeHtml(inspectorBrand(c))}</strong> · ${formatRelativeTime(c.startedAt)} · <strong>${formatDuration(c.durationMs)}</strong>`;
  if (c.status === 'failed') {
    return `
      <div class="cycle-receipt">
        <div class="cycle-receipt__title cycle-receipt__title--data">${head}</div>
        <div class="cycle-receipt__error">${escapeHtml(c.errorMessage || 'The investigation failed without a recorded reason.')}</div>
      </div>`;
  }

  const grounding = c.citationsTotal > 0
    ? `grounding <strong>${c.citationsVerified} of ${c.citationsTotal}</strong> verified`
    : 'grounding <strong>nothing was cited</strong>';
  const confidence = c.confidence != null ? `${Math.round(c.confidence * 100)}% confidence` : 'confidence not recorded';

  return `
    <div class="cycle-receipt">
      <div class="cycle-receipt__title cycle-receipt__title--data">${head}</div>
      <div class="cycle-receipt__flow">
        <span class="cycle-chip cycle-chip--info"><strong>${escapeHtml(inspectorStatusLabel(c.status))}</strong></span>
        ${c.decision ? `<span class="cycle-chip cycle-chip--muted">decision <strong>${escapeHtml(c.decision)}</strong></span>` : ''}
        <span class="cycle-chip cycle-chip--muted">${escapeHtml(confidence)}</span>
      </div>
      <div class="cycle-receipt__verdict">${grounding}.</div>
    </div>`;
}

/** Deliberately not the roster's three figures. "Resolved / escalated" states the uncomfortable
 *  truth instead of hiding it behind a success rate, and "citations verified" is the only number
 *  here that degrades the moment the models start citing things that do not exist. */
function renderInspectorStats(s) {
  if (!s) return '';
  const rate = s.citationsTotal > 0 ? s.citationVerificationRate : null;
  // Rounded to the same precision it is compared at: a 79.5% rate printed as "80%" and coloured
  // amber made the figure and its own warning contradict each other.
  const pct = rate == null ? null : Math.round(rate * 1000) / 10;
  const warn = pct != null && pct < INSPECTOR_CITATION_WARN_RATIO * 100;
  const tip = rate == null
    ? 'No artifact has been cited yet, so there is nothing to verify. Shown as "not measured" rather than as 0% or 100%.'
    : `${formatCount(s.citationsVerified)} of ${formatCount(s.citationsTotal)} cited artifacts were found in the real system. This is the one figure on this card a confident-sounding model cannot inflate.`;

  return `
    <div class="monkey-card__stats">
      <div>
        <span class="monkey-card__stat-value">${formatCount(s.investigations)}</span>
        <span class="monkey-card__stat-label">Investigations</span>
      </div>
      <div>
        <span class="monkey-card__stat-value">${formatCount(s.resolved)}/${formatCount(s.escalated)}</span>
        <span class="monkey-card__stat-label">Resolved / escalated</span>
      </div>
      <div>
        <span class="monkey-card__stat-value"${warn ? ' style="color:var(--status-degraded)"' : ''}>${pct == null ? 'Not measured' : `${pct}%`}</span>
        <span class="monkey-card__stat-label" data-tooltip="${escapeHtml(tip)}" tabindex="0">Citations verified</span>
      </div>
    </div>`;
}

function renderInspectorGuardrails(m) {
  const q = m.queue || {};
  const cap = q.maxInvestigationsPerDay ?? 0;
  return `
    <ul class="guardrail-list">
      <li>Max investigations per day: ${cap > 0 ? formatCount(cap) : 'not configured'} · concurrency: ${formatCount(q.concurrency ?? 0)}</li>
      <li>Allowed environments: ${escapeHtml((m.guardrails && m.guardrails.allowedEnvironments || ['production']).join(', '))} (read-only)</li>
      <li>Agents without a source report it as "no source", never as "found nothing"</li>
      <li>Debate: several models over a capped number of rounds and help hops — this API does not expose the configured limits, so the figures actually used are shown per case on its file rather than invented here</li>
      <li>The verdict is decided by deterministic rules over the grounded synthesis, not by a model</li>
      <li>Unverified citations are dropped · no fix is proposed without one</li>
    </ul>`;
}

/** The verdict, the confidence, what was missing, and how much of it was verified. Compare with the
 *  Doctor Monkey's timeline, which repeats "cannot be determined" with no way to tell why. */
function renderInspectorTimeline(m) {
  const items = Array.isArray(m.activity) ? m.activity : [];
  if (!items.length) {
    return '<p class="timeline-empty">No investigation has closed yet.</p>';
  }

  const stageClass = {
    ESCALATED: 'activity-log__stage--esc',
    RESOLVED: 'activity-log__stage--res',
    CLOSED: 'activity-log__stage--closed',
    FAILED: 'activity-log__stage--fail',
  };

  return `
    <ul class="activity-log">
      ${items.map((a) => {
        const cls = stageClass[a.stage] || '';
        // Never a fabricated sentence: a case that broke before producing a verdict says so.
        const text = a.text || (a.stage === 'FAILED' ? 'The investigation did not complete, so it produced no verdict.' : 'No verdict was recorded.');
        const meta = [];
        meta.push(inspectorBrand(a));
        // A failed case carries confidence 0 because nothing decided it; printing "0%" would read
        // as a measured judgement instead of an absent one.
        if (a.confidence != null && a.decision) meta.push(`confidence ${Math.round(a.confidence * 100)}%`);
        if (a.blockedBy) meta.push(`blocked by: ${a.blockedBy}`);
        // Named even when absent: an unowned service is the gap this line exists to surface.
        meta.push(a.ownerTeam ? `owner: ${a.ownerTeam}` : 'owner: unassigned');
        meta.push(a.citationsTotal > 0 ? `${a.citationsVerified} of ${a.citationsTotal} citations verified` : 'nothing cited');
        if (a.durationMs != null) meta.push(formatDuration(a.durationMs));
        return `
          <li>
            <span class="activity-log__stage ${cls}">${escapeHtml(a.stage)}</span>${escapeHtml(text)}
            ${a.alertReason ? ` ${inspectorAlertBadge(a.alertLevel, a.alertReason)}` : ''}
            ${a.probableRootCause ? `<span class="activity-log__cause"><b>Probable cause:</b> ${escapeHtml(a.probableRootCause)}</span>` : ''}
            <span class="activity-log__meta">${escapeHtml(meta.join(' · '))}</span>
          </li>`;
      }).join('')}
    </ul>`;
}

/* --- Poller + toasts ---------------------------------------------------- */

// The Inspector's timer fires every minute and an investigation takes ~2 minutes, so the page's
// 90s refresh would show a different case each time without ever showing one progressing. Same
// shape as the Scout's poller: only the liveness block is replaced, and a hidden tab costs nothing.
const INSPECTOR_POLL_BUSY_MS = 5000;
const INSPECTOR_POLL_IDLE_MS = 20000;
let inspectorPollTimer = null;

function startInspectorPoller() {
  stopInspectorPoller();
  const schedule = (ms) => { inspectorPollTimer = setTimeout(poll, ms); };

  async function poll() {
    const block = document.querySelector('.monkey-card[data-monkey-id="inspector-monkey"] [data-agent-liveness]');
    if (!block) { stopInspectorPoller(); return; }

    // A browser tab nobody is looking at gets no requests at all. A portal tab that is simply not
    // the one on screen still gets its toasts: an escalation raised while the operator is on
    // Exceptions is exactly the one they need to hear about.
    if (document.hidden) { schedule(INSPECTOR_POLL_IDLE_MS); return; }

    const panel = document.getElementById('tab-monkeys');
    if (panel && panel.hidden) {
      try { await pollInspectorToasts(); } catch (err) { /* the next tick retries */ }
      schedule(INSPECTOR_POLL_IDLE_MS);
      return;
    }

    try {
      const monkeys = await fetchMonkeysForPoll();
      const inspector = monkeys.find((m) => m.id === 'inspector-monkey');
      if (!inspector) { schedule(INSPECTOR_POLL_IDLE_MS); return; }

      const index = state.monkeys.findIndex((m) => m.id === 'inspector-monkey');
      if (index >= 0) state.monkeys[index] = inspector;

      const fresh = renderInspectorLiveness(inspector);
      if (fresh) {
        // The queue panel lives inside the block being replaced, so an open backlog has to be
        // carried across the swap or the refresh closes it under the reader's cursor.
        const card = block.closest('.monkey-card');
        const openPanel = captureInspectorQueuePanel(card);
        block.outerHTML = fresh;
        restoreInspectorQueuePanel(card, openPanel);
        startCountdownTicker();
      }
      await pollInspectorToasts();
      schedule(inspector.currentCase ? INSPECTOR_POLL_BUSY_MS : INSPECTOR_POLL_IDLE_MS);
    } catch (err) {
      // A failed poll is not worth a toast - the next tick retries.
      schedule(INSPECTOR_POLL_IDLE_MS);
    }
  }

  schedule(INSPECTOR_POLL_BUSY_MS);
}

function captureInspectorQueuePanel(card) {
  const panel = card && card.querySelector('[data-inspector-queue-panel]');
  if (!panel || panel.hidden) return null;
  return panel.innerHTML;
}

function restoreInspectorQueuePanel(card, html) {
  if (html == null || !card) return;
  const panel = card.querySelector('[data-inspector-queue-panel]');
  if (!panel) return;
  panel.innerHTML = html;
  panel.hidden = false;
  const button = card.querySelector('.insp-queue[data-action="queue"]');
  if (button) button.setAttribute('aria-expanded', 'true');
}

function stopInspectorPoller() {
  if (inspectorPollTimer) { clearTimeout(inspectorPollTimer); inspectorPollTimer = null; }
}

// The Overview's pipeline widget used to be a snapshot of whatever /api/monkeys returned on page
// load (or on the 90s auto-refresh, itself off by default) - a real investigation could start,
// finish and compose an RCA entirely between visits, and the card would still say "0" until the
// user reloaded the whole page. Same shape as the Scout/Inspector pollers: only #overview-pipeline
// is replaced, and a hidden Overview tab costs nothing.
const OVERVIEW_POLL_BUSY_MS = 5000;
const OVERVIEW_POLL_IDLE_MS = 20000;
let overviewPollTimer = null;

function startOverviewPoller() {
  stopOverviewPoller();
  const schedule = (ms) => { overviewPollTimer = setTimeout(poll, ms); };

  async function poll() {
    const host = document.getElementById('overview-pipeline');
    if (!host) { stopOverviewPoller(); return; }

    if (document.hidden) { schedule(OVERVIEW_POLL_IDLE_MS); return; }

    const panel = document.getElementById('tab-overview');
    if (panel && panel.hidden) { schedule(OVERVIEW_POLL_IDLE_MS); return; }

    try {
      const monkeys = await fetchMonkeysForPoll();
      state.monkeys = monkeys;
      renderAgentPipeline();
      const director = monkeys.find((m) => m.id === 'inspector-monkey');
      const busy = (director && director.currentCase) || monkeys.some((m) => m.currentCycle);
      schedule(busy ? OVERVIEW_POLL_BUSY_MS : OVERVIEW_POLL_IDLE_MS);
    } catch (err) {
      // A failed poll is not worth a toast - the next tick retries.
      schedule(OVERVIEW_POLL_IDLE_MS);
    }
  }

  schedule(OVERVIEW_POLL_BUSY_MS);
}

function stopOverviewPoller() {
  if (overviewPollTimer) { clearTimeout(overviewPollTimer); overviewPollTimer = null; }
}

/** Toast delivery is a poll, not a push. The watermark is kept in memory with a few seconds of
 *  overlap and duplicates are filtered by id, so a toast raised between two ticks is never lost. */
async function pollInspectorToasts() {
  const since = state.inspectorToastSince || new Date(Date.now() - 60000).toISOString();
  let payload;
  try {
    const response = await fetch(`/api/inspector/toasts?since=${encodeURIComponent(since)}`);
    if (!response.ok) return;
    payload = await response.json();
  } catch (err) {
    return;
  }
  state.inspectorToastSince = new Date(Date.now() - 5000).toISOString();

  const fresh = (payload.toasts || []).filter((t) => t && t.id && !state.inspectorSeenToastIds.has(t.id));
  fresh.forEach((t) => state.inspectorSeenToastIds.add(t.id));
  if (!fresh.length) return;

  // Coming back to a tab left open for an hour used to raise one toast per case at once. Only the
  // most recent few are shown; the rest are counted out loud rather than silently dropped.
  const ordered = fresh.slice().sort((a, b) => new Date(a.sentAt || 0) - new Date(b.sentAt || 0));
  const shown = ordered.slice(-INSPECTOR_TOAST_MAX_PER_TICK);
  const suppressed = ordered.length - shown.length;
  shown.forEach(showInspectorToast);
  if (suppressed > 0) {
    showToast('Inspector Monkey', `${suppressed} other notification${suppressed === 1 ? ' was' : 's were'} raised while you were away — open the case file list for all of them.`, 'info');
  }
}

// A toast that never leaves is not a notification, it is a wall: eighteen of them pushed the
// column far above the top of the window, where twelve could not be read or dismissed at all.
const INSPECTOR_TOAST_TTL_MS = 9000;
const INSPECTOR_TOAST_MAX_VISIBLE = 4;
const INSPECTOR_TOAST_MAX_PER_TICK = 3;

const INSPECTOR_DECISION_LABEL = {
  escalate: 'Escalated to a human',
  escalated: 'Escalated to a human',
  close_no_issue: 'Closed — no issue found',
  closed_no_issue: 'Closed — no issue found',
  resolve: 'Resolved with a proposed fix',
  resolved: 'Resolved with a proposed fix',
};

/** The wire subject is "Inspector Monkey: close_no_issue - {CaseId}": the toast title repeated, a
 *  raw backend key, and a timestamp with seven decimals. The club and the verdict a reader needs
 *  are already on the card payload, so they are looked up there and only the remainder is parsed.
 *  The untouched subject stays on the element's title, so nothing is hidden. */
function inspectorToastText(t) {
  const subject = String(t.subject || '');
  const body = subject.replace(/^Inspector Monkey:\s*/i, '');
  const split = body.indexOf(' - ');
  const key = (split > 0 ? body.slice(0, split) : '').trim();
  const caseId = split > 0 ? body.slice(split + 3).trim() : '';

  const inspector = state.monkeys.find((m) => m.id === 'inspector-monkey');
  const cases = inspector && Array.isArray(inspector.cases) ? inspector.cases : [];
  const activity = inspector && Array.isArray(inspector.activity) ? inspector.activity : [];
  const c = cases.find((x) => x.id === t.sourceId) || null;
  const a = activity.find((x) => x.id === t.sourceId) || null;

  const club = (c && (c.clubName || c.brandName)) || (a && (a.clubName || a.brandName)) || inspectorToastEntity(caseId);
  const outcome = INSPECTOR_DECISION_LABEL[key] || (c ? inspectorStatusLabel(c.status) : '');
  const where = caseId ? inspectorCaseLabel(caseId) : '';
  const verdict = a && a.text ? inspectorTruncate(a.text, 150) : '';

  return {
    headline: [club, outcome].filter(Boolean).join(' · ') || subject || 'An investigation closed.',
    detail: [where, verdict].filter(Boolean).join(' — '),
    full: subject,
  };
}

function inspectorToastEntity(caseId) {
  const parts = String(caseId || '').split('|').map((p) => p.trim()).filter(Boolean);
  const entity = parts.find((p) => p.startsWith('host:') || p.startsWith('club:'));
  return entity ? entity.replace(/^(host|club):/, '') : (parts[0] || '');
}

/** Built node by node rather than through innerHTML so the buttons keep their listeners, and
 *  appended rather than assigned so an existing toast is never destroyed. */
function showInspectorToast(t) {
  const container = document.getElementById('toast-container');
  if (!container) return;

  const el = document.createElement('div');
  el.className = 'insp-toast';
  const text = inspectorToastText(t);

  const title = document.createElement('div');
  title.className = 'insp-toast__t';
  // The endpoint lives under /api/inspector but returns every toast-channel notification, findings
  // included. Labelling a burn-rate alert "Inspector Monkey" would credit the wrong agent.
  title.textContent = t.sourceType === 'forensic-case' ? 'Inspector Monkey' : 'Platform alert';
  el.appendChild(title);

  const message = document.createElement('div');
  message.className = 'insp-toast__m';
  message.textContent = t.sourceType === 'forensic-case'
    ? text.headline
    : (t.subject || 'A platform alert was raised.');
  if (text.full) message.title = text.full;
  el.appendChild(message);

  if (t.sourceType === 'forensic-case' && text.detail) {
    const detail = document.createElement('div');
    detail.className = 'insp-toast__d';
    detail.textContent = text.detail;
    el.appendChild(detail);
  }

  const when = document.createElement('div');
  when.className = 'insp-toast__when';
  when.textContent = t.sentAt ? formatRelativeTime(t.sentAt) : '';
  el.appendChild(when);

  let timer = null;
  const dismiss = () => {
    if (timer) { clearTimeout(timer); timer = null; }
    el.remove();
  };
  const arm = () => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(dismiss, INSPECTOR_TOAST_TTL_MS);
  };
  // Reading one should not be a race against its own countdown.
  el.addEventListener('mouseenter', () => { if (timer) { clearTimeout(timer); timer = null; } });
  el.addEventListener('mouseleave', arm);

  const actions = document.createElement('div');
  actions.className = 'insp-toast__a';
  if (t.sourceType === 'forensic-case' && t.sourceId) {
    const open = document.createElement('button');
    open.type = 'button';
    open.className = 'btn btn--sm';
    open.textContent = 'View case file';
    open.addEventListener('click', () => {
      dismiss();
      const inspector = state.monkeys.find((m) => m.id === 'inspector-monkey');
      if (inspector) {
        openInspectorEvidenceModal(inspector);
        openInspectorCaseDetail(t.sourceId);
      }
    });
    actions.appendChild(open);
  }
  const dismissButton = document.createElement('button');
  dismissButton.type = 'button';
  dismissButton.className = 'btn btn--icon btn--sm';
  dismissButton.textContent = 'Dismiss';
  dismissButton.addEventListener('click', dismiss);
  actions.appendChild(dismissButton);
  el.appendChild(actions);

  container.appendChild(el);
  arm();

  // Oldest first out: the ones at the top are the ones that scroll out of reach.
  const live = container.querySelectorAll('.insp-toast');
  for (let i = 0; i < live.length - INSPECTOR_TOAST_MAX_VISIBLE; i += 1) live[i].remove();
}

/* --- Investigations modal: the filterable list and one case file --------- */

/** Opens on the list and fetches the backlog behind it: /api/monkeys carries the queue counters but
 *  not the names of the packages waiting, and "7 waiting" without saying which is not a queue. */
function openInspectorEvidenceModal(monkey, filter) {
  state.inspectorDetailCaseId = null;
  state.inspectorAgentFilter = null;
  state.inspectorQueueNotice = null;
  // Callers still speak in row statuses ('queued' from the backlog counter, 'all' from the card),
  // because that is what they are pointing at; the modal is the one that knows about views.
  if (filter === 'queued') state.inspectorView = 'waiting';
  else if (filter === 'running') state.inspectorView = 'running';
  else if (filter) { state.inspectorView = 'closed'; state.inspectorFilter = filter === 'all' ? 'all' : filter; }
  openModal(`${monkey.name} — Investigations`, renderInspectorModalBody(monkey), { wide: true });
  loadInspectorQueue();
}

async function loadInspectorQueue() {
  try {
    const response = await fetch('/api/inspector/queue');
    if (!response.ok) return;
    state.inspectorQueue = await response.json();
  } catch (err) {
    // The list still renders from the card's own payload; only the waiting packages are missing.
    return;
  }
  refreshInspectorModal();
}

/** Re-rendering #modal-body wholesale is safe here: every Inspector control is wired through the
 *  delegated listener in wireOverlayDismiss, never per render. */
function refreshInspectorModal() {
  const overlay = document.getElementById('modal-overlay');
  if (!overlay || overlay.hidden) return;
  const monkey = state.monkeys.find((m) => m.id === 'inspector-monkey');
  if (!monkey) return;
  const body = document.getElementById('modal-body');
  if (!body || !body.querySelector('[data-inspector-modal]')) return;
  document.getElementById('modal-title').textContent = state.inspectorDetailCaseId
    ? `${monkey.name} — Case file`
    : `${monkey.name} — Investigations`;
  body.innerHTML = renderInspectorModalBody(monkey);
}

function renderInspectorModalBody(monkey) {
  return `<div data-inspector-modal>${state.inspectorDetailCaseId
    ? renderInspectorCaseFile(monkey)
    : renderInspectorList(monkey)}</div>`;
}

/** One row per package still waiting its turn. Carries only what a queued item can honestly
 *  answer - how long it has waited, which attempt is next, why it is not moving. The result
 *  columns (verdict, confidence, citations, tokens) are structurally absent here, not blank. */
function inspectorQueueRows() {
  const queued = state.inspectorQueue && Array.isArray(state.inspectorQueue.queued)
    ? state.inspectorQueue.queued
    : [];
  const now = Date.now();
  // The API returns the claim's own ordering (priority, then arrival), so the index is the real
  // position in line rather than a number invented by the client.
  return queued
    .filter((q) => q.status !== 'done')
    .map((q, i) => ({
      id: q.id,
      position: i + 1,
      caseId: q.dedupKey,
      signature: inspectorSignature(q.dedupKey),
      label: inspectorQueueItemLabel(q.dedupKey),
      attempt: (q.attempts || 0) + 1,
      maxAttempts: q.maxAttempts,
      lastError: q.lastError,
      enqueuedAt: q.enqueuedAt,
      waitedMs: Math.max(0, now - new Date(q.enqueuedAt).getTime()),
    }));
}

/** One row per investigation that was actually opened - the half of the story that has a verdict. */
function inspectorCaseRows(monkey) {
  const cases = Array.isArray(monkey.cases) ? monkey.cases : [];
  return cases.map((c) => {
    const detail = state.inspectorCaseDetails[c.id];
    const activity = (monkey.activity || []).find((a) => a.id === c.id);
    return {
      id: c.id,
      caseId: c.caseId,
      brand: inspectorBrand(c),
      status: c.status,
      verdict: activity ? activity.text : null,
      blockedBy: activity ? activity.blockedBy : null,
      // Level comes from the case, the reason and the root cause only travel on the activity
      // entry - so a running case shows no badge rather than a stale one.
      alertLevel: c.alertLevel,
      alertReason: activity ? activity.alertReason : null,
      probableRootCause: activity ? activity.probableRootCause : null,
      ownerTeam: activity ? activity.ownerTeam : null,
      ownerResolvedBy: activity ? activity.ownerResolvedBy : null,
      errorMessage: c.errorMessage,
      // Confidence only means something once a decision was reached; a failed case stores 0.
      confidence: (c.status === 'running' || !c.decision) ? null : c.confidence,
      citationsVerified: c.citationsVerified,
      citationsTotal: c.citationsTotal,
      // /api/monkeys carries no per-case token count; the case file does. Shown as an honest gap
      // until the detail has been opened rather than as a zero.
      tokens: detail && !detail.error ? (detail.tokensIn || 0) + (detail.tokensOut || 0) : null,
      durationLabel: c.status === 'running' ? `${formatRelativeTime(c.startedAt)}` : formatDuration(c.durationMs),
      startedAt: c.startedAt,
    };
  });
}

/** Group key for waiting work. A CaseId is "{source} | {vertical} | {entity} | {stage} | {bucket}",
 *  and both the bucket and the entity are dropped: nine "Unmapped operation" packages from nine
 *  different hosts are one unmapped stage, not nine problems. Keeping the entity in the key would
 *  produce nine groups all captioned the same way, which reads worse than no grouping at all. */
function inspectorSignature(caseId) {
  if (!caseId) return { key: 'unknown', label: 'Unknown signature', context: '' };
  const parts = String(caseId).split('|').map((p) => p.trim()).filter(Boolean);
  if (parts.length < 2) return { key: String(caseId), label: String(caseId), context: '' };

  const head = inspectorBucketClock(parts[parts.length - 1]) === null ? parts : parts.slice(0, -1);
  const stage = head[head.length - 1];
  // Without an entity segment there is nothing to drop, so the whole head is the key.
  const key = head.length >= 3 ? [...head.slice(0, -2), stage].join('|') : head.join('|');
  return { key, label: stage, context: head.slice(0, head.length >= 3 ? -2 : -1).join(' · ') };
}

/** Row label inside a group: the part the signature deliberately threw away, so two rows of the
 *  same signature are still told apart by the host they came from and the hour they arrived. */
function inspectorQueueItemLabel(caseId) {
  if (!caseId) return '—';
  const parts = String(caseId).split('|').map((p) => p.trim()).filter(Boolean);
  const clock = inspectorBucketClock(parts[parts.length - 1]);
  const head = clock === null ? parts : parts.slice(0, -1);
  const entity = head.length >= 3 ? head[head.length - 2] : head[0];
  return clock ? `${entity} · ${clock}` : entity;
}

/** Waiting longer than this reads as a backlog rather than a queue, and the row says so in colour. */
const INSPECTOR_AGED_WAIT_MS = 3 * 60 * 60 * 1000;

function inspectorWaitLabel(ms) {
  const minutes = Math.floor(ms / 60000);
  if (minutes < 60) return `${minutes}m`;
  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`;
}

/** The one thing the old list never said out loud. Once the day's cap is spent the runner stops
 *  leasing until the UTC day rolls over (see MonkeyRunners), so a queue with items in it is
 *  stopped, not slow - and "stopped until 00:00 UTC" is a different operational decision from
 *  "running behind". Returns null when the queue is genuinely draining. */
function inspectorQueueBlock(monkey) {
  const q = monkey.queue || {};
  const limits = (state.inspectorQueue && state.inspectorQueue.limits) || {};
  const cap = limits.maxInvestigationsPerDay ?? q.maxInvestigationsPerDay ?? 0;
  const today = limits.investigationsToday ?? q.investigationsToday ?? 0;
  if (cap <= 0 || today < cap) return null;

  const now = new Date();
  const rollover = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1);
  return { cap, today, resumesInMs: Math.max(0, rollover - now.getTime()) };
}

const INSPECTOR_VIEWS = [
  { key: 'waiting', label: 'Waiting' },
  { key: 'running', label: 'Investigating' },
  { key: 'closed', label: 'Closed' },
];

const INSPECTOR_CLOSED_FILTERS = [
  { key: 'all', label: 'All' },
  { key: 'escalated', label: 'Escalated' },
  { key: 'resolved', label: 'Resolved' },
  { key: 'closed_no_issue', label: 'Closed, no issue' },
  { key: 'failed', label: 'Failed' },
];

function renderInspectorList(monkey) {
  const queueRows = inspectorQueueRows();
  const caseRows = inspectorCaseRows(monkey);
  const running = caseRows.filter((r) => r.status === 'running');
  const closed = caseRows.filter((r) => r.status !== 'running');
  const view = state.inspectorView || 'waiting';
  const block = inspectorQueueBlock(monkey);

  const counts = { waiting: queueRows.length, running: running.length, closed: closed.length };
  const tabs = INSPECTOR_VIEWS.map((v) => `<button type="button" class="insp-seg__b${view === v.key ? ' is-active' : ''}" data-insp-view="${v.key}">${escapeHtml(v.label)} · ${formatCount(counts[v.key])}</button>`).join('');

  let panel;
  if (view === 'waiting') panel = renderInspectorWaiting(queueRows, block);
  else if (view === 'running') panel = renderInspectorCaseTable(running, 'Nothing is being investigated right now.');
  else panel = renderInspectorClosed(monkey, closed);

  return `
    ${renderInspectorQueueTiles(monkey, running.length, block)}
    ${block ? renderInspectorBlocker(block, queueRows.length) : ''}
    ${renderInspectorNotice()}
    <div class="insp-listhead">
      <div class="insp-seg">${tabs}</div>
      ${view === 'waiting' && queueRows.length ? renderInspectorQueueTools(queueRows.length) : ''}
      ${view === 'closed' ? `<div class="insp-filters">${INSPECTOR_CLOSED_FILTERS.map((f) => `<button type="button" class="insp-filter${(state.inspectorFilter || 'all') === f.key ? ' is-active' : ''}" data-insp-filter="${f.key}">${escapeHtml(f.label)}</button>`).join('')}</div>` : ''}
    </div>
    ${panel}`;
}

/** Says why nothing is moving and when it will move again. Deliberately carries no "raise the cap"
 *  button: the cap is configuration, not a stored value, and a button that cannot do what it says
 *  is the bug this redesign set out to remove. */
function renderInspectorBlocker(block, waiting) {
  return `
    <div class="insp-block" role="status">
      <span class="insp-block__ico" aria-hidden="true">⏸</span>
      <div>
        <div class="insp-block__t">Queue stopped — the daily cap is spent (${formatCount(block.today)} of ${formatCount(block.cap)})</div>
        <div class="insp-block__m">
          No further package is claimed until the UTC day rolls over, in <strong>${escapeHtml(inspectorWaitLabel(block.resumesInMs))}</strong>.
          ${waiting ? `${formatCount(waiting)} ${waiting === 1 ? 'package is' : 'packages are'} waiting on it.` : ''}
          Raising <code>Inspector:MaxInvestigationsPerDay</code> is the only thing that resumes it sooner.
        </div>
      </div>
    </div>`;
}

function renderInspectorNotice() {
  const notice = state.inspectorQueueNotice;
  if (!notice) return '';
  return `<p class="insp-notice insp-notice--${notice.kind === 'err' ? 'err' : 'ok'}" role="status">${escapeHtml(notice.text)}</p>`;
}

function renderInspectorQueueTools(waiting) {
  const busy = state.inspectorQueueBusy ? ' disabled' : '';
  return `
    <div class="insp-tools">
      <div class="insp-seg insp-seg--sm">
        <button type="button" class="insp-seg__b${state.inspectorGrouped ? ' is-active' : ''}" data-insp-grouped="1"${busy}>Grouped</button>
        <button type="button" class="insp-seg__b${state.inspectorGrouped ? '' : ' is-active'}" data-insp-grouped="0"${busy}>Flat</button>
      </div>
      <button type="button" class="insp-danger" data-action="inspector-clear-queue"${busy}>${state.inspectorQueueBusy ? 'Working…' : `Clear queue · ${formatCount(waiting)}`}</button>
    </div>`;
}

function renderInspectorWaiting(rows, block) {
  if (!rows.length) {
    return `<p class="state-block">Nothing is waiting. Every package the Scout handed over has been claimed.</p>`;
  }
  return state.inspectorGrouped ? renderInspectorGroups(rows, block) : renderInspectorWaitingTable(rows, block);
}

/** Why a given row is not moving. The cap stops the whole queue at once, so it is read from the
 *  blocking state rather than from the row; a retry after a failure is the row's own story. */
function inspectorWaitReason(row, block) {
  if (row.attempt > 1) {
    return `<span class="insp-why">Retry after a failure</span>${row.lastError ? `<span class="insp-why__d insp-clamp insp-clamp--2" title="${escapeHtml(row.lastError)}">${escapeHtml(row.lastError)}</span>` : ''}`;
  }
  if (block) return '<span class="insp-why">Daily cap spent</span>';
  return '<span class="insp-why insp-why--ok">Waiting its turn</span>';
}

function renderInspectorWaitingTable(rows, block) {
  const longest = Math.max(...rows.map((r) => r.waitedMs), 1);
  const busy = state.inspectorQueueBusy ? ' disabled' : '';

  const body = rows.map((r) => {
    const aged = r.waitedMs >= INSPECTOR_AGED_WAIT_MS;
    return `
      <tr class="insp-row">
        <td class="num"><strong>${r.position}</strong></td>
        <td>
          <div class="insp-clamp insp-clamp--2"><strong>${escapeHtml(r.signature.label)}</strong></div>
          <code class="insp-sub" title="${escapeHtml(r.caseId || '')}">${escapeHtml(r.label)}</code>
        </td>
        <td>
          <span class="insp-wait"><span class="${aged ? 'is-aged' : ''}" style="width:${Math.round((r.waitedMs / longest) * 100)}%"></span></span>
          <span class="${aged ? 'insp-aged' : ''}">${escapeHtml(inspectorWaitLabel(r.waitedMs))}</span>
        </td>
        <td class="num${r.attempt > 1 ? ' insp-aged' : ''}">${r.attempt} of ${r.maxAttempts}</td>
        <td>${inspectorWaitReason(r, block)}</td>
        <td class="num"><button type="button" class="insp-rowbtn" data-insp-discard="${escapeHtml(r.id)}" title="Take this package out of the queue"${busy}>Discard</button></td>
      </tr>`;
  }).join('');

  return `
    <p class="insp-scope">Columns match what a waiting package can answer: there is no verdict, confidence or citation count until the Inspector claims it.</p>
    <div class="insp-tablewrap">
    <table class="pkg__table insp-table insp-table--wait">
      <thead>
        <tr>
          <th class="num" title="Position in line - the Inspector claims by priority, then by arrival.">#</th>
          <th>Signature</th><th>Waiting for</th>
          <th class="num">Attempt</th><th>Why it is not moving</th><th class="num">Action</th>
        </tr>
      </thead>
      <tbody>${body}</tbody>
    </table>
    </div>`;
}

function renderInspectorGroups(rows, block) {
  const groups = new Map();
  rows.forEach((r) => {
    const existing = groups.get(r.signature.key);
    if (existing) existing.items.push(r);
    else groups.set(r.signature.key, { key: r.signature.key, label: r.signature.label, context: r.signature.context, items: [r] });
  });

  // Biggest repeat first: the signature filling the queue is the one worth acting on, regardless of
  // whether its oldest item happens to be at the front of the line.
  const ordered = [...groups.values()].sort((a, b) =>
    b.items.length - a.items.length || b.items[0].waitedMs - a.items[0].waitedMs);
  const open = new Set(state.inspectorOpenGroups || []);
  const busy = state.inspectorQueueBusy ? ' disabled' : '';

  const blocks = ordered.map((g) => {
    const isOpen = open.has(g.key) || ordered.length === 1;
    const waits = g.items.map((i) => i.waitedMs);
    const oldest = Math.max(...waits);
    const newest = Math.min(...waits);
    const range = g.items.length === 1
      ? inspectorWaitLabel(oldest)
      : `${inspectorWaitLabel(newest)} – ${inspectorWaitLabel(oldest)}`;
    const retries = g.items.filter((i) => i.attempt > 1).length;

    const rowsHtml = g.items.map((i) => `
      <div class="insp-grp__row">
        <strong class="insp-grp__pos">#${i.position}</strong>
        <code title="${escapeHtml(i.caseId || '')}">${escapeHtml(i.label)}</code>
        <span class="${i.waitedMs >= INSPECTOR_AGED_WAIT_MS ? 'insp-aged' : 'insp-muted'}">${escapeHtml(inspectorWaitLabel(i.waitedMs))} · attempt ${i.attempt} of ${i.maxAttempts}</span>
        <button type="button" class="insp-rowbtn" data-insp-discard="${escapeHtml(i.id)}"${busy}>Discard</button>
      </div>`).join('');

    return `
      <div class="insp-grp${isOpen ? ' is-open' : ''}">
        <div class="insp-grp__h" data-insp-group="${escapeHtml(g.key)}" role="button" tabindex="0" aria-expanded="${isOpen}">
          <span class="insp-grp__cnt">${formatCount(g.items.length)}</span>
          <span class="insp-grp__t">${escapeHtml(g.label)}
            <small>${escapeHtml(g.context ? `${g.context} · ` : '')}${g.items.length === 1 ? 'one package waiting' : `${formatCount(g.items.length)} packages of the same signature`}${retries ? ` · ${formatCount(retries)} retrying` : ''}</small></span>
          <span class="insp-grp__m">${escapeHtml(range)}</span>
          ${block ? '<span class="insp-pill insp-pill--esc">Stopped</span>' : ''}
          <button type="button" class="insp-rowbtn insp-rowbtn--danger" data-insp-discard-group="${escapeHtml(g.key)}" title="Take every package of this signature out of the queue"${busy}>${g.items.length === 1 ? 'Discard' : `Discard all ${formatCount(g.items.length)}`}</button>
          <span class="insp-grp__chev" aria-hidden="true">▾</span>
        </div>
        ${isOpen ? `<div class="insp-grp__body">${rowsHtml}</div>` : ''}
      </div>`;
  }).join('');

  const note = ordered.length < rows.length
    ? `${formatCount(rows.length)} waiting packages across ${formatCount(ordered.length)} ${ordered.length === 1 ? 'signature' : 'signatures'} — the repeats are one problem, not many.`
    : `${formatCount(rows.length)} waiting ${rows.length === 1 ? 'package' : 'packages'}, each a different signature.`;

  return `<p class="insp-scope">${escapeHtml(note)}</p>${blocks}`;
}

function renderInspectorClosed(monkey, rows) {
  const filter = state.inspectorFilter || 'all';
  const visible = filter === 'all' ? rows : rows.filter((r) => r.status === filter);

  // "24 closed" beside a card claiming 38 investigations reads as a contradiction unless the list
  // says out loud that it is a window onto the most recent ones, not the whole history.
  const recorded = monkey.inspectorStats ? monkey.inspectorStats.investigations : null;
  const scopeNote = recorded != null && recorded > rows.length
    ? `Showing the ${formatCount(rows.length)} most recent investigations of ${formatCount(recorded)} recorded. Older ones are not in this payload.`
    : '';

  return `${scopeNote ? `<p class="insp-scope">${escapeHtml(scopeNote)}</p>` : ''}${renderInspectorCaseTable(visible, 'Nothing matches this filter.')}`;
}

function renderInspectorCaseTable(rows, emptyText) {
  const body = rows.map((r) => {
    const citations = r.citationsTotal == null
      ? '—'
      : (r.citationsTotal === 0
        ? '<span style="color:var(--text-muted)">none</span>'
        : `<span${r.citationsVerified < r.citationsTotal ? ' style="color:var(--status-critical)"' : ''}>${r.citationsVerified}/${r.citationsTotal}</span>`);
    const confidence = r.confidence == null
      ? '—'
      : `<span class="insp-microbar"><span style="width:${Math.round(r.confidence * 100)}%;${r.confidence >= 0.75 ? 'background:var(--status-healthy);' : ''}"></span></span>${Math.round(r.confidence * 100)}%`;
    const verdict = r.verdict
      ? escapeHtml(r.verdict)
      : (r.errorMessage
        ? `<span style="color:var(--status-critical)">${escapeHtml(r.errorMessage)}</span>`
        : '<span style="color:var(--text-muted)">No verdict yet</span>');

    return `
      <tr class="insp-row insp-row--clickable" data-insp-case="${escapeHtml(r.id)}" role="button" tabindex="0" title="Open the case file">
        <td><div class="insp-clamp insp-clamp--2" title="${escapeHtml(r.brand)}">${escapeHtml(r.brand)}</div></td>
        <td><code class="insp-clamp insp-clamp--2" title="${escapeHtml(r.caseId || '')}">${escapeHtml(inspectorCaseLabel(r.caseId))}</code></td>
        <td>${inspectorPill(r.status)}</td>
        <td>${inspectorAlertBadge(r.alertLevel, r.alertReason)}</td>
        <td>${inspectorOwner(r.ownerTeam, r.ownerResolvedBy)}</td>
        <td>
          <div class="insp-clamp insp-clamp--3" title="${escapeHtml(r.verdict || r.errorMessage || '')}">${verdict}</div>
          ${r.probableRootCause ? `<div class="insp-cause" title="${escapeHtml(r.probableRootCause)}"><span class="insp-cause__lbl">Probable cause</span><span class="insp-clamp insp-clamp--2">${escapeHtml(r.probableRootCause)}</span></div>` : ''}
          ${r.blockedBy ? `<div class="insp-blocked" title="${escapeHtml(r.blockedBy)}"><span class="insp-blocked__lbl">Blocked by</span><span class="insp-clamp insp-clamp--2">${escapeHtml(r.blockedBy)}</span></div>` : ''}
        </td>
        <td class="num">${confidence}</td>
        <td class="num">${citations}</td>
        <td class="num insp-col--tokens">${r.tokens == null ? '<span style="color:var(--text-muted)" title="Only recorded on the case file — open the row to load it.">—</span>' : formatCount(r.tokens)}</td>
        <td class="num insp-col--duration">${escapeHtml(r.durationLabel)}</td>
      </tr>`;
  }).join('');

  return `
    <div class="insp-tablewrap">
    <table class="pkg__table insp-table">
      <thead>
        <tr>
          <th>Club</th><th>Case</th><th>Status</th>
          <th title="The severity the Red Alert policy assigned, and why. Hover a badge for the stated reason.">Alert</th>
          <th title="The team accountable for the failing service - who fixes it, as opposed to the club, which is who is affected.">Owner</th>
          <th>Verdict</th>
          <th class="num">Confidence</th>
          <th class="num" title="How many cited artifacts were found in the real system.">Citations</th>
          <th class="num insp-col--tokens">Tokens</th><th class="num insp-col--duration">Duration</th>
        </tr>
      </thead>
      <tbody>${body || `<tr><td colspan="10" class="state-block">${escapeHtml(emptyText)}</td></tr>`}</tbody>
    </table>
    </div>`;
}

/** In queue / investigating / done today. The three numbers an operator checks before anything else. */
function renderInspectorQueueTiles(monkey, running, block) {
  const q = monkey.queue || {};
  const stats = (state.inspectorQueue && state.inspectorQueue.stats) || {};
  const limits = (state.inspectorQueue && state.inspectorQueue.limits) || {};
  const waiting = stats.queued ?? q.queued ?? 0;
  const doneToday = limits.investigationsToday ?? q.investigationsToday ?? 0;
  const cap = limits.maxInvestigationsPerDay ?? q.maxInvestigationsPerDay ?? 0;
  const oldest = stats.oldestQueuedAt || q.oldestQueuedAt;
  const discarded = stats.discardedTotal ?? 0;

  return `
    <div class="insp-strip">
      <div class="insp-strip__tile${block && waiting ? ' insp-strip__tile--block' : ''}">
        <span class="insp-strip__n">${formatCount(waiting)}</span>
        <span class="insp-strip__lbl">Waiting</span>
        <span class="insp-strip__sub">${block
          ? `stopped — resumes in ${escapeHtml(inspectorWaitLabel(block.resumesInMs))}`
          : (oldest ? `oldest waiting ${escapeHtml(formatRelativeTime(oldest))}` : 'nothing waiting')}</span>
      </div>
      <div class="insp-strip__tile${running ? ' insp-strip__tile--live' : ''}">
        <span class="insp-strip__n">${formatCount(running)}</span>
        <span class="insp-strip__lbl">Investigating</span>
        <span class="insp-strip__sub">concurrency ${formatCount(limits.concurrency ?? q.concurrency ?? 0)} — a cost limit, not a capacity one</span>
      </div>
      <div class="insp-strip__tile${block ? ' insp-strip__tile--block' : ''}">
        <span class="insp-strip__n">${formatCount(doneToday)}${cap > 0 ? ` <small>/ ${formatCount(cap)}</small>` : ''}</span>
        <span class="insp-strip__lbl">Done today</span>
        <span class="insp-strip__sub">${block ? 'cap reached' : ((stats.failedToday ?? q.failedToday ?? 0) ? `${formatCount(stats.failedToday ?? q.failedToday)} failed today` : 'no failures today')}${discarded ? ` · ${formatCount(discarded)} discarded` : ''}</span>
      </div>
    </div>`;
}

/** Discards are never applied to the local list: the server decides what was actually still
 *  waiting (an item claimed a second before the click is not discardable), so the queue is re-read
 *  rather than spliced. */
async function discardInspectorQueue(ids, reason) {
  if (state.inspectorQueueBusy) return;
  state.inspectorQueueBusy = true;
  state.inspectorQueueNotice = null;
  refreshInspectorModal();

  try {
    const body = await dataProvider.discardInspectorQueue(ids, reason);
    const n = body.discarded || 0;
    state.inspectorQueueNotice = {
      kind: 'ok',
      text: n === 0
        ? 'Nothing was discarded — those packages were no longer waiting.'
        : `${formatCount(n)} ${n === 1 ? 'package' : 'packages'} discarded. They are kept as discarded work, not deleted.`,
    };
  } catch (err) {
    state.inspectorQueueNotice = { kind: 'err', text: err.message || 'The queue could not be updated.' };
  } finally {
    state.inspectorQueueBusy = false;
  }

  await loadInspectorQueue();
  refreshInspectorModal();
}

/** Clearing the whole backlog throws away work the Scout decided was worth investigating, so the
 *  confirmation asks for the count rather than for a click: an OK button is too easy to hit twice. */
function confirmClearInspectorQueue() {
  const rows = inspectorQueueRows();
  if (!rows.length || state.inspectorQueueBusy) return;

  const answer = window.prompt(
    `This discards all ${rows.length} waiting packages. They are kept as discarded work and will not be investigated.\n\nType ${rows.length} to confirm.`,
    '');
  if (answer === null) return;
  if (answer.trim() !== String(rows.length)) {
    state.inspectorQueueNotice = { kind: 'err', text: 'Nothing was discarded — the confirmation did not match.' };
    refreshInspectorModal();
    return;
  }
  discardInspectorQueue(null, 'Queue cleared from the portal.');
}

function discardInspectorGroup(key) {
  const items = inspectorQueueRows().filter((r) => r.signature.key === key);
  if (!items.length || state.inspectorQueueBusy) return;
  const label = items[0].signature.label;
  if (!window.confirm(`Discard all ${items.length} waiting packages of "${label}"? They are kept as discarded work and will not be investigated.`)) return;
  discardInspectorQueue(items.map((i) => i.id), `Signature discarded from the portal: ${label}`);
}

function discardInspectorItem(id) {
  const item = inspectorQueueRows().find((r) => r.id === id);
  if (!item || state.inspectorQueueBusy) return;
  if (!window.confirm(`Discard "${item.label}"? It is kept as discarded work and will not be investigated.`)) return;
  discardInspectorQueue([id], 'Discarded from the portal.');
}

/** Fetches GET /api/inspector/cases/{id} the first time a row is opened: the case file carries the
 *  whole transcript (every prompt and raw response), far heavier than the card payload. */
function openInspectorCaseDetail(caseId) {
  state.inspectorDetailCaseId = caseId;
  state.inspectorAgentFilter = null;
  refreshInspectorModal();
  if (state.inspectorCaseDetails[caseId]) return;

  fetch(`/api/inspector/cases/${encodeURIComponent(caseId)}`)
    .then(async (response) => {
      if (!response.ok) {
        state.inspectorCaseDetails[caseId] = { error: `Could not load the case file (HTTP ${response.status}).` };
        return;
      }
      state.inspectorCaseDetails[caseId] = await response.json();
    })
    .catch(() => {
      state.inspectorCaseDetails[caseId] = { error: 'Could not load the case file.' };
    })
    .finally(() => {
      if (state.inspectorDetailCaseId === caseId) refreshInspectorModal();
    });
}

function renderInspectorCaseFile(monkey) {
  const id = state.inspectorDetailCaseId;
  const detail = state.inspectorCaseDetails[id];
  const back = '<button type="button" class="btn btn--icon btn--sm" data-action="inspector-back">← Back to investigations</button>';

  if (!detail) {
    return `${back}<p style="color:var(--text-muted);font-size:12px;margin:12px 0 0;">Loading the case file…</p>`;
  }
  if (detail.error) {
    return `${back}<p style="color:var(--status-critical);font-size:12px;margin:12px 0 0;">${escapeHtml(detail.error)}</p>`;
  }

  return `
    <div class="insp-casehead">${back}</div>
    ${renderInspectorVerdict(detail)}
    ${renderRcaBlock(detail)}
    ${renderInspectorGraph(detail)}
    ${renderInspectorBlackboard(detail)}
    ${renderInspectorSynthesis(detail)}
    ${renderInspectorGrounding(detail)}
    ${renderInspectorRunbook(detail)}
    ${renderInspectorRail(detail)}`;
}

/** Reading an existing RCA is free; composing one spends a provider call. They are deliberately
 *  two different actions so opening a case file can never write a document by accident. */
function loadRca(caseId) {
  if (state.rcaByCase[caseId] !== undefined) return;
  state.rcaByCase[caseId] = { loading: true };

  fetch(`/api/inspector/cases/${encodeURIComponent(caseId)}/rca`)
    .then(async (response) => {
      // 404 means "none written yet", which is an offer, not an error.
      if (response.status === 404) { state.rcaByCase[caseId] = null; return; }
      if (!response.ok) { state.rcaByCase[caseId] = { error: `Could not load the RCA (HTTP ${response.status}).` }; return; }
      state.rcaByCase[caseId] = await response.json();
    })
    .catch(() => { state.rcaByCase[caseId] = { error: 'Could not load the RCA.' }; })
    .finally(() => { if (state.inspectorDetailCaseId === caseId) refreshInspectorModal(); });
}

async function composeRca(caseId) {
  state.rcaByCase[caseId] = { composing: true };
  refreshInspectorModal();

  try {
    const response = await fetch(`/api/inspector/cases/${encodeURIComponent(caseId)}/rca`, {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': await getAntiforgeryToken() },
    });
    const body = await response.json().catch(() => ({}));

    if (!response.ok) {
      state.rcaByCase[caseId] = { error: body.error || `Composing failed (HTTP ${response.status}).` };
      showToast('RCA not written', body.error || 'The agent could not compose the document.', 'critical');
    } else {
      state.rcaByCase[caseId] = body;
      showToast('RCA written', `${body.sections ? body.sections.length : 0} sections composed from this investigation.`, 'success');
    }
  } catch (err) {
    state.rcaByCase[caseId] = { error: err.message || 'Composing failed.' };
  }
  refreshInspectorModal();
}

/** The statement labels are the point of this block: the document is always complete, so the
 *  label is the only thing telling a reader what they can quote to a partner. */
function renderRcaStatement(statement) {
  const level = RCA_CONFIDENCE[statement.confidence] || RCA_CONFIDENCE.inference;
  return `<li class="rca-stmt rca-stmt--${escapeHtml(statement.confidence || 'inference')}">
      <span class="rca-stmt__tag" title="${escapeHtml(level.help)}">${escapeHtml(level.label)}</span>
      <span class="rca-stmt__text">${escapeHtml(statement.text)}</span>
    </li>`;
}

function renderRcaBlock(detail) {
  const caseId = detail.id;
  const rca = state.rcaByCase[caseId];

  if (rca === undefined) {
    loadRca(caseId);
    return '<div class="rca-block"><p class="rca-block__hint">Checking whether an RCA has been written…</p></div>';
  }
  if (rca && rca.loading) {
    return '<div class="rca-block"><p class="rca-block__hint">Checking whether an RCA has been written…</p></div>';
  }
  if (rca && rca.composing) {
    return '<div class="rca-block"><p class="rca-block__hint">The RCA Agent is writing the document — this takes around half a minute…</p></div>';
  }

  const compose = `<button type="button" class="btn btn--sm" data-action="compose-rca" data-case="${escapeHtml(caseId)}">${rca ? 'Rewrite the RCA' : 'Write the RCA'}</button>`;

  if (rca === null) {
    return `
      <div class="rca-block">
        <div class="rca-block__head"><h4 class="rca-block__h">Root Cause Analysis</h4>${compose}</div>
        <p class="rca-block__hint">No RCA has been written for this investigation yet.</p>
      </div>`;
  }
  if (rca.error) {
    return `
      <div class="rca-block">
        <div class="rca-block__head"><h4 class="rca-block__h">Root Cause Analysis</h4>${compose}</div>
        <p class="rca-block__err">${escapeHtml(rca.error)}</p>
      </div>`;
  }

  const sections = (rca.sections || []).map((s) => `
    <div class="rca-section">
      <h5 class="rca-section__h">${escapeHtml(s.title)}</h5>
      <ul class="rca-section__list">${(s.statements || []).map(renderRcaStatement).join('')}</ul>
    </div>`).join('');

  const whys = (rca.fiveWhys || []).map((w) => `
    <li class="rca-why rca-why--${escapeHtml(w.confidence || 'inference')}">
      <span class="rca-why__lvl">Why ${w.level}</span>
      <span class="rca-why__q">${escapeHtml(w.question)}</span>
      <span class="rca-why__a">${escapeHtml(w.answer)}</span>
    </li>`).join('');

  const precedents = (rca.precedents || []).map((p) => `
    <li class="rca-prec">
      <span class="rca-prec__kind rca-prec__kind--${escapeHtml(p.sourceType)}">${escapeHtml(RCA_SOURCE_LABEL[p.sourceType] || p.sourceType)}</span>
      ${escapeHtml(p.document)} <span class="rca-prec__sec">/ ${escapeHtml(p.section)}</span>
    </li>`).join('');

  return `
    <div class="rca-block">
      <div class="rca-block__head">
        <h4 class="rca-block__h">Root Cause Analysis</h4>
        ${compose}
      </div>
      <p class="rca-block__meta">
        Written ${formatRelativeTime(rca.generatedAt)} by ${escapeHtml(rca.modelUsed || 'unknown model')} ·
        ${formatCount((rca.tokensIn || 0) + (rca.tokensOut || 0))} tokens
      </p>
      <p class="rca-block__legend">
        Every statement is labelled: <b>measured</b> comes from this investigation's verified evidence,
        <b>derived</b> is arithmetic over it, <b>inferred</b> is the model's reading. Only the first is quotable as fact.
      </p>
      ${rca.requiresHuman && rca.requiresHuman.length ? `
        <p class="rca-block__human">A person still has to complete or approve: <b>${rca.requiresHuman.map(escapeHtml).join(', ')}</b></p>` : ''}
      ${sections}
      ${whys ? `<div class="rca-section"><h5 class="rca-section__h">5 Whys</h5><ol class="rca-whys">${whys}</ol></div>` : ''}
      ${precedents ? `
        <div class="rca-section">
          <h5 class="rca-section__h">Past incidents the knowledge base surfaced</h5>
          <p class="rca-block__hint">Context only — these describe other incidents, not this one.</p>
          <ul class="rca-precs">${precedents}</ul>
        </div>` : ''}
    </div>`;
}

/** The RCA Agent's evidence is two different things, and conflating them would mislead: the
 *  documents it has written, and the corpus of past incidents it writes from. The second is the
 *  only place in the portal where an import becomes visible at all.
 *
 *  Opens with what /api/monkeys already provided and fills the corpus in afterwards, so the modal
 *  is never blank while a second request is in flight. */
function openRcaEvidenceModal(monkey) {
  openModal(`${monkey.name} — Evidence`, renderRcaEvidence(monkey), { wide: true });

  fetch('/api/knowledge/documents')
    .then(async (response) => {
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.json();
    })
    .then((corpus) => { state.knowledgeCorpus = corpus; })
    .catch((err) => { state.knowledgeCorpus = { error: err.message || 'unavailable' }; })
    .finally(() => {
      const host = document.getElementById('rca-corpus');
      if (host) host.innerHTML = renderKnowledgeCorpus(state.knowledgeCorpus);
    });
}

function renderRcaEvidence(monkey) {
  const stats = monkey.rcaStats || {};
  const written = (monkey.activity || []).filter((a) => a.type === 'rca_composed');

  const docs = written.length ? written.map((a) => `
    <li class="evidence-item">
      <div class="evidence-item__header">
        <span>${escapeHtml(a.text)}</span>
        <span class="evidence-item__header-right">
          <span class="rca-ev__when">${escapeHtml(formatRelativeTime(a.time))}</span>
        </span>
      </div>
      <div class="rca-ev__meta">
        <code title="${escapeHtml(a.forensicCaseId || '')}">${escapeHtml(inspectorCaseLabel(a.forensicCaseId))}</code>
        · ${formatCount(a.sections || 0)} section(s)
        · ${escapeHtml(a.modelUsed || 'unknown model')}
        ${a.requiresHuman && a.requiresHuman.length
          ? ` · <span class="rca-ev__human">awaiting a person: ${a.requiresHuman.map(escapeHtml).join(', ')}</span>`
          : ''}
        ${a.errorMessage ? ` · <span class="rca-ev__err">${escapeHtml(a.errorMessage)}</span>` : ''}
      </div>
    </li>`).join('') : '';

  return `
    <div class="stage-detail" style="margin-bottom:12px;">
      <dl>
        <dt>Documents written</dt><dd>${formatCount(stats.documents ?? 0)}${stats.failed ? ` · ${stats.failed} failed` : ''}</dd>
        <dt>Statements</dt><dd>${formatCount(stats.factStatements ?? 0)} measured · ${formatCount(stats.derivedStatements ?? 0)} derived · ${formatCount(stats.inferenceStatements ?? 0)} inferred</dd>
        <dt>Tokens spent</dt><dd>${formatCount((stats.tokensIn || 0) + (stats.tokensOut || 0))}</dd>
      </dl>
    </div>

    <h3>Documents it has written</h3>
    ${docs
      ? `<ul class="simple-list">${docs}</ul>`
      : `<p style="color:var(--text-muted)">Nothing written yet. This agent has no timer — open a finished investigation and ask for the write-up.</p>`}

    <h3 style="margin-top:18px;">What it writes from</h3>
    <p style="color:var(--text-muted);margin-top:-4px;">
      Past incidents imported into the knowledge base. The agent quotes these only as context — a precedent
      describes a different incident, never this one.
    </p>
    <div id="rca-corpus">${renderKnowledgeCorpus(state.knowledgeCorpus)}</div>`;
}

/** A partially embedded document is reported as such: without a vector it is reachable by keyword
 *  only, so it is in the knowledge base but will not surface on meaning alone. */
function renderKnowledgeCorpus(corpus) {
  if (!corpus) return '<p style="color:var(--text-muted)">Loading the knowledge base…</p>';
  if (corpus.error) return `<p class="rca-ev__err">Could not read the knowledge base (${escapeHtml(corpus.error)}).</p>`;
  if (!corpus.documents) {
    return '<p style="color:var(--text-muted)">The knowledge base is empty — no past incidents have been imported, so the agent writes with no precedent to draw on.</p>';
  }

  const unembedded = (corpus.chunks || 0) - (corpus.chunksWithEmbedding || 0);
  const byType = (corpus.byType || []).map((t) =>
    `<span class="rca-prec__kind rca-prec__kind--${escapeHtml(t.sourceType)}">${escapeHtml(RCA_SOURCE_LABEL[t.sourceType] || t.sourceType)}</span> ${formatCount(t.documents)} doc(s)`
  ).join(' · ');

  const rows = (corpus.items || []).map((d) => `
    <li class="rca-corpus__row">
      <span class="rca-prec__kind rca-prec__kind--${escapeHtml(d.sourceType)}">${escapeHtml(RCA_SOURCE_LABEL[d.sourceType] || d.sourceType)}</span>
      <span class="rca-corpus__t">${escapeHtml(d.title)}</span>
      <span class="rca-corpus__n">${formatCount(d.chunks)} chunk(s)${d.chunksWithEmbedding < d.chunks
        ? `<span class="rca-ev__err" title="Only reachable by keyword — no vector was stored."> · ${d.chunks - d.chunksWithEmbedding} not embedded</span>`
        : ''}</span>
    </li>`).join('');

  return `
    <div class="stage-detail" style="margin:8px 0 12px;">
      <dl>
        <dt>Imported</dt><dd>${formatCount(corpus.documents)} document(s) · ${formatCount(corpus.chunks)} searchable chunk(s)</dd>
        ${byType ? `<dt>By kind</dt><dd>${byType}</dd>` : ''}
        ${unembedded > 0 ? `<dt>Keyword only</dt><dd class="rca-ev__err">${formatCount(unembedded)} chunk(s) stored without an embedding</dd>` : ''}
      </dl>
    </div>
    <ul class="rca-corpus">${rows}</ul>`;
}

/* -----------------------------------------------------------------------
   MEMORY BANK — the imported corpus, made readable
   The agents retrieve from this store on every investigation, so it is shown
   as it was indexed rather than as the original files. Passages overlap by
   design, which is why they stay separate instead of being stitched back
   into a document that would repeat sentences.
   ----------------------------------------------------------------------- */

function renderMemoryBankTab() {
  const host = document.getElementById('memory-bank');
  if (!host) return;

  if (!state.knowledgeCorpus) {
    host.innerHTML = '<p class="mem-note">Loading the knowledge base…</p>';
    fetch('/api/knowledge/documents')
      .then(async (r) => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
      .then((c) => { state.knowledgeCorpus = c; })
      .catch((e) => { state.knowledgeCorpus = { error: e.message || 'unavailable' }; })
      .finally(renderMemoryBankTab);
    return;
  }

  host.innerHTML = renderMemoryBankBody(state.knowledgeCorpus);
  wireMemoryBank();
}

function renderMemoryBankBody(corpus) {
  if (corpus.error) return `<p class="rca-ev__err">Could not read the knowledge base (${escapeHtml(corpus.error)}).</p>`;
  if (!corpus.documents) {
    return `<p class="mem-note">The knowledge base is empty. Nothing has been imported, so the agents write with no precedent to draw on.</p>`;
  }

  const m = state.memory;
  const types = (corpus.byType || []);
  const unembedded = (corpus.chunks || 0) - (corpus.chunksWithEmbedding || 0);

  const options = ['<option value="all">Every kind</option>'].concat(types.map((t) =>
    `<option value="${escapeHtml(t.sourceType)}"${m.type === t.sourceType ? ' selected' : ''}>${escapeHtml(RCA_SOURCE_LABEL[t.sourceType] || t.sourceType)} (${t.documents})</option>`
  )).join('');

  return `
    <div class="mem-head">
      <div class="mem-stat"><span class="mem-stat__n">${formatCount(corpus.documents)}</span><span class="mem-stat__l">documents</span></div>
      <div class="mem-stat"><span class="mem-stat__n">${formatCount(corpus.chunks)}</span><span class="mem-stat__l">searchable passages</span></div>
      <div class="mem-stat"><span class="mem-stat__n">${types.length}</span><span class="mem-stat__l">kind(s) of source</span></div>
      ${unembedded > 0
        ? `<div class="mem-stat mem-stat--warn" title="Stored without a vector — reachable by keyword only, never by meaning."><span class="mem-stat__n">${formatCount(unembedded)}</span><span class="mem-stat__l">keyword only</span></div>`
        : ''}
    </div>

    <div class="mem-tools">
      <input id="mem-filter" class="mem-tools__in" type="search" placeholder="Filter by title…" value="${escapeHtml(m.filter)}" autocomplete="off">
      <select id="mem-type" class="mem-tools__sel">${options}</select>
      <span class="mem-tools__sep"></span>
      <input id="mem-q" class="mem-tools__in" type="search" placeholder="e.g. timeouts at checkout…" value="${escapeHtml((m.search && m.search.query) || '')}" autocomplete="off">
      <button id="mem-search" class="btn btn--sm" type="button">Search by meaning</button>
      ${m.search ? '<button id="mem-clear" class="btn btn--icon btn--sm" type="button">Back to the list ✕</button>' : ''}
    </div>
    <p class="mem-note mem-note--cost">
      The filter is instant and free. Searching by meaning embeds your question first, so each search spends a provider call —
      it is the same retrieval the RCA Agent runs when it looks for a precedent.
    </p>

    ${m.search ? renderMemorySearch(m.search) : `<div id="mem-results">${renderMemoryList(corpus)}</div>`}`;
}

function renderMemoryList(corpus) {
  const m = state.memory;
  const needle = m.filter.trim().toLowerCase();
  const items = (corpus.items || [])
    .filter((d) => m.type === 'all' || d.sourceType === m.type)
    .filter((d) => !needle || d.title.toLowerCase().includes(needle));

  if (!items.length) {
    return `<p class="mem-note">No document matches that filter. ${escapeHtml(String(corpus.documents))} are imported in total.</p>`;
  }

  const rows = items.map((d) => `
    <li class="mem-doc" data-doc="${escapeHtml(d.title)}" data-type="${escapeHtml(d.sourceType)}" role="button" tabindex="0">
      <span class="rca-prec__kind rca-prec__kind--${escapeHtml(d.sourceType)}">${escapeHtml(RCA_SOURCE_LABEL[d.sourceType] || d.sourceType)}</span>
      <span class="mem-doc__t">${escapeHtml(d.title)}</span>
      <span class="mem-doc__m">${formatCount(d.chunks)} passage(s)${d.chunksWithEmbedding < d.chunks
        ? `<span class="rca-ev__err" title="Reachable by keyword only — no vector was stored."> · ${d.chunks - d.chunksWithEmbedding} not embedded</span>`
        : ''} · ${escapeHtml(formatRelativeTime(d.indexedAt))}</span>
    </li>`).join('');

  return `
    <p class="mem-note">${formatCount(items.length)} of ${formatCount(corpus.documents)} document(s). Open one to read what the agent reads.</p>
    <ul class="mem-docs">${rows}</ul>`;
}

/** Search results are ranked passages, not documents: the hit is the paragraph that matched, and
 *  showing the document alone would hide which part of it actually answered the question. */
function renderMemorySearch(search) {
  if (search.loading) return '<p class="mem-note">Embedding the question and comparing it against every passage…</p>';
  if (search.error) return `<p class="rca-ev__err">The search failed (${escapeHtml(search.error)}).</p>`;
  if (!search.results || !search.results.length) {
    return `<p class="mem-note">Nothing in the knowledge base came close to “${escapeHtml(search.query)}”.</p>`;
  }

  const rows = search.results.map((h) => `
    <li class="mem-hit" data-doc="${escapeHtml(h.document || '')}" data-type="${escapeHtml(h.sourceType || '')}" role="button" tabindex="0">
      <div class="mem-hit__h">
        <span class="rca-prec__kind rca-prec__kind--${escapeHtml(h.sourceType || '')}">${escapeHtml(RCA_SOURCE_LABEL[h.sourceType] || h.sourceType || '')}</span>
        <span class="mem-doc__t">${escapeHtml(h.document || '(untitled)')}</span>
        <span class="mem-hit__sec">${escapeHtml(h.section || '')}</span>
      </div>
      <p class="mem-hit__snip">${escapeHtml(h.snippet || '')}…</p>
    </li>`).join('');

  return `
    <p class="mem-note">${search.results.length} passage(s) closest in meaning to “${escapeHtml(search.query)}”. Ranked by the same similarity the agent uses — closeness is not correctness.</p>
    <ul class="mem-hits">${rows}</ul>`;
}

function wireMemoryBank() {
  const filter = document.getElementById('mem-filter');
  if (filter) {
    filter.addEventListener('input', () => {
      state.memory.filter = filter.value;
      // Only the results are redrawn, so the caret never jumps out of the box mid-typing.
      const results = document.getElementById('mem-results');
      if (!results) { renderMemoryBankTab(); return; }
      results.innerHTML = renderMemoryList(state.knowledgeCorpus);
      wireMemoryDocuments();
    });
  }

  const type = document.getElementById('mem-type');
  if (type) type.addEventListener('change', () => { state.memory.type = type.value; renderMemoryBankTab(); });

  const go = document.getElementById('mem-search');
  const q = document.getElementById('mem-q');
  if (go && q) {
    const run = () => runMemorySearch(q.value);
    go.addEventListener('click', run);
    q.addEventListener('keydown', (e) => { if (e.key === 'Enter') run(); });
  }

  const clear = document.getElementById('mem-clear');
  if (clear) clear.addEventListener('click', () => { state.memory.search = null; renderMemoryBankTab(); });

  wireMemoryDocuments();
}

function wireMemoryDocuments() {
  document.querySelectorAll('.mem-doc, .mem-hit').forEach((el) => {
    const open = () => openMemoryDocument(el.dataset.doc, el.dataset.type);
    el.addEventListener('click', open);
    el.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
  });
}

async function runMemorySearch(query) {
  const q = (query || '').trim();
  if (!q) return;

  state.memory.search = { query: q, loading: true };
  renderMemoryBankTab();

  try {
    const response = await fetch(`/api/knowledge/search?q=${encodeURIComponent(q)}&top=10`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();
    state.memory.search = { query: q, results: data.results || [] };
  } catch (err) {
    state.memory.search = { query: q, error: err.message || 'unavailable' };
  }
  renderMemoryBankTab();
}

function openMemoryDocument(title, sourceType) {
  if (!title) return;
  openModal(title, '<p class="mem-note">Loading the indexed text…</p>', { wide: true });

  // A slow response must not overwrite a modal the reader has already moved on from.
  const writeIfStillOpen = (html) => {
    const overlay = document.getElementById('modal-overlay');
    if (overlay.hidden || document.getElementById('modal-title').textContent !== title) return;
    document.getElementById('modal-body').innerHTML = html;
  };

  fetch(`/api/knowledge/document?title=${encodeURIComponent(title)}&type=${encodeURIComponent(sourceType || '')}`)
    .then(async (r) => {
      if (r.status === 404) throw new Error('no indexed passages for this document');
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      return r.json();
    })
    .then((doc) => { writeIfStillOpen(renderMemoryDocument(doc)); })
    .catch((err) => { writeIfStillOpen(`<p class="rca-ev__err">Could not read the document (${escapeHtml(err.message || 'unavailable')}).</p>`); });
}

function renderMemoryDocument(doc) {
  const sections = (doc.items || []).map((s, i) => `
    <div class="mem-sec">
      <h5 class="mem-sec__h">${escapeHtml(s.section || `Passage ${i + 1}`)}</h5>
      <p class="mem-sec__b">${escapeHtml(s.content)}</p>
    </div>`).join('');

  return `
    <div class="stage-detail" style="margin-bottom:12px;">
      <dl>
        <dt>Indexed as</dt><dd>${formatCount(doc.sections)} passage(s) · ${formatCount(doc.characters)} characters</dd>
        ${doc.reference ? `<dt>Source file</dt><dd><code>${escapeHtml(doc.reference)}</code></dd>` : ''}
      </dl>
    </div>
    <p class="mem-note">
      These are the passages as stored, in order. Consecutive passages overlap on purpose — a sentence split across a
      boundary would otherwise be unretrievable — so a few lines appear twice. This is the text the agent reads, not the original file.
    </p>
    ${sections}`;
}

function renderInspectorVerdict(d) {
  const decision = d.decision || d.status || 'unknown';
  const confidence = d.decision && d.confidence != null ? Math.round(d.confidence * 100) : null;
  const citations = d.citationsTotal > 0
    ? `${d.citationsVerified} of ${d.citationsTotal} citations verified`
    : 'nothing was cited';

  return `
    <div class="insp-verdict insp-verdict--${escapeHtml(INSPECTOR_PILL_CLASS[d.status] ? d.status : 'running')}">
      <div class="insp-verdict__kick">L3 verdict · ${escapeHtml(decision)}${d.alertReason ? ` ${inspectorAlertBadge(d.alertLevel, d.alertReason)}` : ''}</div>
      <h3 class="insp-verdict__h">${escapeHtml(d.verdict || (d.status === 'running' ? 'Still investigating — no verdict yet.' : 'No verdict was recorded for this case.'))}</h3>
      <div class="insp-verdict__sub">
        ${escapeHtml(d.clubName || d.brandName || 'Unknown club')} · <code title="${escapeHtml(d.caseId || '')}">${escapeHtml(inspectorCaseLabel(d.caseId))}</code> ·
        ${escapeHtml(d.vertical || '—')} / ${escapeHtml(d.stage || '—')}
      </div>
      <div class="insp-conf">
        <span style="color:var(--text-muted);">Confidence</span>
        <span class="insp-conf__bar"><span style="width:${confidence == null ? 0 : confidence}%"></span></span>
        <b>${confidence == null ? 'not recorded' : `${confidence}%`}</b>
      </div>
      <div class="insp-verdict__meta">
        ${escapeHtml(citations)} · ${formatCount(d.llmCalls ?? 0)} LLM calls ·
        ${formatCount((d.tokensIn ?? 0) + (d.tokensOut ?? 0))} tokens · ${formatDuration(d.durationMs)}
        ${d.errorMessage ? `<div style="color:var(--status-critical);margin-top:6px;">${escapeHtml(d.errorMessage)}</div>` : ''}
      </div>
      ${d.probableRootCause ? `
      <div class="insp-cause insp-cause--block">
        <span class="insp-cause__lbl">Probable root cause</span>
        <p class="insp-cause__text">${escapeHtml(d.probableRootCause)}</p>
      </div>` : ''}
      ${d.blockedBy ? `
      <div class="insp-blockedby">
        <span class="insp-blockedby__lbl">Blocked by</span>
        <p class="insp-blockedby__text">${escapeHtml(d.blockedBy)}</p>
      </div>` : ''}
      ${d.alertReason ? `
      <div class="insp-alertwhy">
        <span class="insp-alertwhy__lbl">Why ${escapeHtml(INSPECTOR_ALERT_LABEL[d.alertLevel] || 'this level')}</span>
        <p class="insp-alertwhy__text">${escapeHtml(d.alertReason)}</p>
      </div>` : ''}
      ${renderInspectorOwnerBlock(d)}
    </div>`;
}

/** Who this hands over to. Shown even when nobody owns it, because an unassigned service is the
 *  single thing blocking a Red Alert today and hiding it would hide the dependency. */
function renderInspectorOwnerBlock(d) {
  if (d.ownerTeam) {
    const people = [
      d.ownerPrimary ? `primary ${escapeHtml(d.ownerPrimary)}` : '',
      d.ownerBackup ? `backup ${escapeHtml(d.ownerBackup)}` : '',
    ].filter(Boolean).join(' · ');
    const scope = d.ownerResolvedBy === 'vertical-fallback'
      ? 'Matched the whole vertical, not this stage specifically.'
      : 'Recorded as owning this exact journey stage.';
    return `
      <div class="insp-owner-block">
        <span class="insp-owner-block__lbl">Accountable team</span>
        <p class="insp-owner-block__text"><b>${escapeHtml(d.ownerTeam)}</b>${people ? ` — ${people}` : ''}</p>
        <p class="insp-owner-block__scope">${escapeHtml(scope)}</p>
      </div>`;
  }
  return `
    <div class="insp-owner-block insp-owner-block--none">
      <span class="insp-owner-block__lbl">Accountable team</span>
      <p class="insp-owner-block__text">No team is recorded as owning ${escapeHtml(d.vertical || '—')} / ${escapeHtml(d.stage || '—')}.</p>
      <p class="insp-owner-block__scope">The ownership map is empty until the named responders are agreed, so this case cannot be routed and cannot be a Red Alert.</p>
    </div>`;
}

function inspectorTruncate(text, max) {
  const s = String(text ?? '').replace(/\s+/g, ' ').trim();
  return s.length > max ? `${s.slice(0, max - 1)}…` : s;
}

/** The graph is the trace's navigator, not decoration: a node is coloured from what the transcript
 *  and the blackboard actually say, and clicking one filters the rail below to that author.
 *  Agents with no source wired up are dashed and dimmed with the reason in the tooltip - rendering
 *  them as "found nothing" would be the opposite statement. */
function renderInspectorGraph(d) {
  const turns = Array.isArray(d.turns) ? d.turns : [];
  const blackboard = Array.isArray(d.blackboard) ? d.blackboard : [];

  const byRole = new Map();
  turns.forEach((t) => {
    if (!t || !t.agentRole) return;
    const list = byRole.get(t.agentRole) || [];
    list.push(t);
    byRole.set(t.agentRole, list);
  });

  const roleState = (role) => {
    const list = byRole.get(role);
    if (!list || !list.length) return null;
    const last = list[list.length - 1];
    if (last.outcomeStatus === 'error') return 'failed';
    if (last.outcomeStatus === 'needs_help') return 'help';
    if (last.outcomeStatus === 'excluded') return 'nosource';
    return 'done';
  };

  const board = new Map(blackboard.filter((e) => e && e.agentRole).map((e) => [e.agentRole, e]));

  const specialistNode = (role, y) => {
    const entry = board.get(role);
    let nodeState = roleState(role) || 'pending';
    let sub = 'pending';
    let title = `${role} — has not run yet.`;
    if (entry) {
      if (entry.status === 'no_source') {
        nodeState = 'nosource';
        sub = 'no source';
        title = `${role} — no source: ${entry.summary || 'no source is wired up for this agent.'} It could not have found anything; this is not "found nothing".`;
      } else if (entry.status === 'failed') {
        nodeState = 'failed';
        sub = 'failed';
        title = `${role} — failed: ${entry.summary || 'no reason recorded.'}`;
      } else if (entry.status === 'empty') {
        nodeState = 'done';
        sub = 'found nothing';
        title = `${role} — ran against a real source and found nothing relevant. ${entry.summary || ''}`;
      } else {
        nodeState = 'done';
        // A node is 104 units wide: a 26-character sentence fragment overflowed the box it sits in
        // and collided with its neighbours. The summary is in the tooltip and, in full, two blocks
        // below in "What each agent contributed" - the node only has room for the outcome.
        sub = 'contributed';
        title = `${role} — ${entry.summary || 'contributed to the blackboard.'}`;
      }
    }
    return { x: 238, y, w: 104, h: 34, label: role, sub, state: nodeState, agent: role, title };
  };

  const moaRoles = [...byRole.keys()].filter((r) => r.startsWith('moa:'));
  const moaNodes = (moaRoles.length ? moaRoles : [null, null, null]).slice(0, 3).map((role, i) => {
    const y = [36, 90, 144][i];
    if (!role) {
      return { x: 492, y, w: 118, h: 40, label: 'model', sub: 'pending', state: 'pending', agent: null, title: 'The debate has not started, so no model has spoken yet.' };
    }
    const list = byRole.get(role) || [];
    const last = list[list.length - 1];
    const st = roleState(role) || 'pending';
    const sub = st === 'help' ? 'asked for help' : (st === 'nosource' ? 'excluded' : (st === 'failed' ? 'error' : `${list.length} turn(s)`));
    return {
      x: 492, y, w: 118, h: 40,
      label: inspectorTruncate(role.slice(4), 18),
      sub,
      state: st,
      agent: role,
      title: `${role} — ${last && last.errorMessage ? last.errorMessage : `outcome: ${last ? last.outcomeStatus : 'unknown'}`}`,
    };
  });

  const contributed = blackboard.filter((e) => e && (e.status === 'contributed' || e.status === 'empty')).length;

  // The mock promised "Debate: 3 models · 2 rounds · max 3 help hops" and the card lost it. The
  // configured ceilings are not on any payload, so these are counted off this case's own trace and
  // labelled as such - an observation, not a setting.
  const moaTurns = turns.filter((t) => t && typeof t.agentRole === 'string' && t.agentRole.startsWith('moa:'));
  const debateModels = new Set(moaTurns.map((t) => t.agentRole)).size;
  const debateRounds = new Set(moaTurns.map((t) => t.round ?? 0)).size;
  const maxHop = turns.reduce((max, t) => Math.max(max, (t && t.hop) || 0), 0);
  const debateLine = moaTurns.length
    ? `Debate in this case: <strong>${debateModels}</strong> model${debateModels === 1 ? '' : 's'} · <strong>${debateRounds}</strong> round${debateRounds === 1 ? '' : 's'} · ${maxHop > 0 ? `<strong>${maxHop}</strong> help hop${maxHop === 1 ? '' : 's'} used` : 'no help hops were needed'}. Counted from the trace below — the configured ceilings are not exposed by the API, so they are not stated here.`
    : '';

  const nodes = [
    { x: 14, y: 84, w: 86, h: 46, label: 'Planner', sub: 'routes · synthesises', state: roleState('Planner') || 'pending', agent: 'Planner', title: 'Planner — dispatches the specialists and reconciles the debate.' },
    { x: 120, y: 30, w: 92, h: 40, label: 'PreProcess', sub: 'no LLM', state: roleState('PreProcess') || 'pending', agent: 'PreProcess', title: 'PreProcessor — normalises the package and screens it for prompt injection. Deterministic, no LLM call.' },
    { x: 120, y: 144, w: 92, h: 40, label: 'ScopeResolver', sub: 'no LLM', state: roleState('Scope') || 'pending', agent: 'Scope', title: 'ScopeResolver — resolves the club and which sources apply. Deterministic, no LLM call.' },
    ...INSPECTOR_SPECIALISTS.map((role, i) => specialistNode(role, [6, 48, 90, 132, 174][i])),
    { x: 372, y: 86, w: 92, h: 46, label: 'Blackboard', sub: `${contributed} of ${INSPECTOR_SPECIALISTS.length} contributed`, state: blackboard.length ? 'done' : 'pending', agent: null, title: 'The shared evidence every model in the debate reads. Agreement across models over an incomplete blackboard is not evidence.' },
    ...moaNodes,
    { x: 644, y: 86, w: 92, h: 46, label: 'Synthesis', sub: d.synthesis ? 'reconciled' : 'pending', state: d.synthesis ? 'done' : 'pending', agent: 'Planner', title: 'The Planner reconciles the debate into one grounded account.' },
    { x: 768, y: 86, w: 92, h: 46, label: 'Grounding', sub: d.citationsTotal > 0 ? `${d.citationsVerified}/${d.citationsTotal}` : 'nothing cited', state: d.grounding ? 'done' : 'pending', agent: 'Grounding', title: 'Every cited artifact is checked against the real system before the verdict ships.' },
    { x: 892, y: 86, w: 92, h: 46, label: 'Verdict', sub: d.decision ? inspectorTruncate(d.decision, 16) : 'pending', state: d.decision ? 'done' : 'pending', agent: null, title: 'Decided by deterministic rules over the grounded synthesis, not by another model opinion.' },
  ];

  const edges = [
    'M100,100 L120,52', 'M100,114 L120,162',
    'M212,50 L238,23', 'M212,50 L238,65', 'M212,50 L238,107',
    'M212,164 L238,149', 'M212,164 L238,191',
    'M342,23 L372,100', 'M342,65 L372,104', 'M342,107 L372,109',
    'M342,149 L372,114', 'M342,191 L372,118',
    'M464,100 L492,56', 'M464,109 L492,110', 'M464,118 L492,164',
    'M610,56 L644,100', 'M610,110 L644,109', 'M610,164 L644,118',
    'M736,109 L768,109', 'M860,109 L892,109',
  ].map((p) => `<path class="insp-edge" d="${p}"></path>`).join('');

  return `
    <div class="insp-graph">
      <svg viewBox="0 0 1000 220" role="img" aria-label="Inspector pipeline">
        ${edges}
        ${nodes.map(inspectorGraphNode).join('')}
      </svg>
      <div class="insp-graph__legend">
        <span><i class="insp-legend--done"></i>completed</span>
        <span><i class="insp-legend--help"></i>asked for help</span>
        <span><i class="insp-legend--failed"></i>failed</span>
        <span><i class="insp-legend--pending"></i>pending</span>
        <span><i class="insp-legend--nosource"></i>no source available</span>
        <span style="margin-left:auto;">Click a node to filter the trace below.</span>
      </div>
      ${debateLine ? `<p class="insp-graph__debate">${debateLine}</p>` : ''}
    </div>`;
}

function inspectorGraphNode(n) {
  const selected = n.agent && state.inspectorAgentFilter === n.agent ? ' is-selected' : '';
  const cls = `insp-node insp-node--${n.state}${n.agent ? ' insp-node--clickable' : ''}${selected}`;
  const attrs = n.agent ? ` data-insp-agent="${escapeHtml(n.agent)}" role="button" tabindex="0"` : '';
  const labelY = n.y + (n.h <= 36 ? 15 : 19);
  return `
    <g class="${cls}"${attrs}>
      <title>${escapeHtml(n.title || n.label)}</title>
      <rect x="${n.x}" y="${n.y}" width="${n.w}" height="${n.h}" rx="7"></rect>
      <text class="insp-node__label" x="${n.x + n.w / 2}" y="${labelY}" text-anchor="middle">${escapeHtml(n.label)}</text>
      ${n.sub ? `<text class="insp-node__sub" x="${n.x + n.w / 2}" y="${labelY + 12}" text-anchor="middle">${escapeHtml(n.sub)}</text>` : ''}
    </g>`;
}

/** The specialists' own words. Until now these summaries lived only inside <title> on SVG nodes:
 *  invisible on touch, unselectable, not findable with Ctrl+F, and cut to 26 characters. This block
 *  is the one place that says in full what each agent contributed - including the two that could
 *  not contribute at all, which is the most important line on the page. */
function renderInspectorBlackboard(d) {
  const entries = Array.isArray(d.blackboard) ? d.blackboard.filter((e) => e && e.agentRole) : [];
  if (!entries.length) {
    return `
      <div class="pkg__block">
        <p class="pkg__block-title"><span>What each agent contributed</span></p>
        <p style="font-size:12px;margin:0;color:var(--text-muted);">No specialist has written to the blackboard for this case.</p>
      </div>`;
  }

  const byRole = new Map(entries.map((e) => [e.agentRole, e]));
  const extras = entries.map((e) => e.agentRole).filter((r) => !INSPECTOR_SPECIALISTS.includes(r));
  const ordered = INSPECTOR_SPECIALISTS.filter((r) => byRole.has(r)).concat(extras);

  const STATUS = {
    contributed: { cls: 'done', text: 'contributed evidence' },
    empty: { cls: 'empty', text: 'ran · found nothing relevant' },
    no_source: { cls: 'nosource', text: 'no source wired up — could not have found anything' },
    failed: { cls: 'failed', text: 'failed' },
  };

  const contributed = entries.filter((e) => e.status === 'contributed').length;

  const lanes = ordered.map((role) => {
    const e = byRole.get(role);
    const s = STATUS[e.status] || { cls: 'pending', text: e.status || 'status not recorded' };
    return `
      <li class="insp-lane insp-lane--${escapeHtml(s.cls)}">
        <div class="insp-lane__head">
          <strong>${escapeHtml(role)}</strong>
          <span class="insp-lane__status">${escapeHtml(s.text)}</span>
        </div>
        <p class="insp-lane__text">${escapeHtml(e.summary || 'No summary was recorded.')}</p>
      </li>`;
  }).join('');

  return `
    <div class="pkg__block">
      <p class="pkg__block-title">
        <span>What each agent contributed</span>
        <span class="pkg__size">${contributed} of ${entries.length} wrote evidence to the blackboard</span>
      </p>
      <ul class="insp-lanes">${lanes}</ul>
    </div>`;
}

/** The hardest sentences in the case file, and until now the only ones discarded: whether the
 *  models' agreement means anything, what they disagreed about, and what nobody could settle.
 *  Three models restating one specialist is not triangulation - this is where that gets said. */
function renderInspectorSynthesis(d) {
  const s = d.synthesis;
  if (!s) return '';
  const conflicts = Array.isArray(s.conflicts) ? s.conflicts.filter(Boolean) : [];
  const unknowns = Array.isArray(s.unknowns) ? s.unknowns.filter(Boolean) : [];
  if (!s.agreementAssessment && !conflicts.length && !unknowns.length) return '';

  const conflictItems = conflicts.map((c) => {
    const between = typeof c === 'string' ? c : (c.between || 'The disagreement was not described.');
    const resolution = typeof c === 'object' && c.resolution ? c.resolution : '';
    return `
      <li>
        <p class="insp-conflict__between">${escapeHtml(between)}</p>
        ${resolution ? `<p class="insp-conflict__res"><span>Resolved as</span> ${escapeHtml(resolution)}</p>` : ''}
      </li>`;
  }).join('');

  return `
    <div class="pkg__block">
      <p class="pkg__block-title">
        <span>How much the agreement is worth</span>
        <span class="pkg__tag pkg__tag--inferred">the models' own account of their consensus</span>
      </p>
      ${s.agreementAssessment ? `<p class="insp-agreement">${escapeHtml(s.agreementAssessment)}</p>` : ''}
      ${conflicts.length ? `
        <h4 class="insp-subhead">What they disagreed about (${conflicts.length})</h4>
        <ul class="insp-conflicts">${conflictItems}</ul>` : ''}
      ${unknowns.length ? `
        <h4 class="insp-subhead">Still unknown after the investigation (${unknowns.length})</h4>
        <ul class="insp-unknowns">${unknowns.map((u) => `<li>${escapeHtml(typeof u === 'string' ? u : JSON.stringify(u))}</li>`).join('')}</ul>` : ''}
    </div>`;
}

/** Three outcomes, never two. "not found" says the claim is false; "not verifiable" says we cannot
 *  tell. Collapsing them would either slander a correct answer or launder a fabricated one. */
function renderInspectorGrounding(d) {
  const artifacts = Array.isArray(d.grounding) ? d.grounding : [];
  if (!artifacts.length) {
    return `
      <div class="pkg__block">
        <p class="pkg__block-title"><span>Grounding — what could be checked against reality</span></p>
        <p style="font-size:12px;margin:0;color:var(--text-muted);">Nothing was cited, so there was nothing to verify. That is a result, not a gap in this view.</p>
      </div>`;
  }

  const cell = (verification, note) => {
    if (verification === 'verified') {
      return `<span class="insp-ground insp-ground--verified">✓ verified${note ? ` — ${escapeHtml(note)}` : ''}</span>`;
    }
    if (verification === 'not_found') {
      return `<span class="insp-ground insp-ground--notfound">✗ not found — checked against a real index and absent${note ? `: ${escapeHtml(note)}` : ''}</span>`;
    }
    return `<span class="insp-ground insp-ground--unverifiable">? not verifiable — no index exists to check it against${note ? `: ${escapeHtml(note)}` : ''}</span>`;
  };

  const verified = artifacts.filter((a) => a.verification === 'verified').length;
  const notFound = artifacts.filter((a) => a.verification === 'not_found').length;
  const notVerifiable = artifacts.length - verified - notFound;

  return `
    <div class="pkg__block pkg__block--measured">
      <p class="pkg__block-title">
        <span>Grounding — what could be checked against reality</span>
        <span class="pkg__size">${verified} verified · ${notFound} not found · ${notVerifiable} not verifiable</span>
      </p>
      <table class="pkg__table">
        <thead><tr><th>Cited artifact</th><th style="width:130px;">Cited by</th><th>Verification</th></tr></thead>
        <tbody>
          ${artifacts.map((a) => `
            <tr>
              <td><code>${escapeHtml(a.value || '—')}</code><div style="color:var(--text-muted);font-size:10.5px;">${escapeHtml(a.kind || '')}</div></td>
              <td>${escapeHtml(a.citedBy || '—')}</td>
              <td>${cell(a.verification, a.note)}</td>
            </tr>`).join('')}
        </tbody>
      </table>
      ${notFound > 0 ? `<div class="pkg__warn pkg__warn--critical" style="margin:10px 0 0;"><span>✕</span><span><strong>${notFound === 1 ? '1 artifact does' : `${notFound} artifacts do`} not exist.</strong> A model asserted ${notFound === 1 ? 'it' : 'them'} and the check disproved it.</span></div>` : ''}
      ${notVerifiable > 0 ? `<div class="pkg__warn" style="margin:10px 0 0;background:var(--status-unknown-bg);border-color:var(--card-border);color:var(--text-secondary);"><span>i</span><span><strong>${notVerifiable === 1 ? '1 artifact could' : `${notVerifiable} artifacts could`} not be checked</strong> because no index covers ${notVerifiable === 1 ? 'it' : 'them'} — that is a missing capability on our side, not a claim the model got wrong.</span></div>` : ''}
    </div>`;
}

function renderInspectorRunbook(d) {
  const steps = Array.isArray(d.runbook) ? d.runbook : [];
  if (!steps.length) return '';

  // `step` is the step NUMBER. Letting a numeric field win a `||` chain printed "1", "2", "3" and
  // threw every instruction away. The ordered list already supplies the numbering.
  const action = (s) => {
    if (typeof s === 'string') return s.trim();
    if (!s || typeof s !== 'object') return String(s ?? '').trim();
    const found = [s.action, s.description, s.text, s.instruction, s.summary]
      .find((v) => typeof v === 'string' && v.trim());
    return found ? found.trim() : '';
  };
  const why = (s) => (s && typeof s === 'object' && typeof s.why === 'string' ? s.why.trim() : '');

  const items = steps.map((s) => {
    const text = action(s);
    const reason = why(s);
    return `<li>${text
      ? escapeHtml(text)
      : `<span style="color:var(--text-muted)" title="${escapeHtml(JSON.stringify(s))}">This step carried no instruction text.</span>`}${reason ? `<span class="insp-runbook__why">${escapeHtml(reason)}</span>` : ''}</li>`;
  }).join('');

  return `
    <div class="pkg__block">
      <p class="pkg__block-title"><span>Proposed runbook</span><span class="pkg__tag pkg__tag--inferred">inference · a model's opinion</span></p>
      <ol class="insp-runbook">${items}</ol>
    </div>`;
}

/** The trace itself: who spoke, in which round, with which model, what it cost, and the verbatim
 *  prompt and response. This is what an L3 actually opens at 3 AM. */
function renderInspectorRail(d) {
  const turns = [...(Array.isArray(d.turns) ? d.turns : [])]
    .sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
  const toolsByTurn = new Map();
  (Array.isArray(d.toolCalls) ? d.toolCalls : []).forEach((c) => {
    const list = toolsByTurn.get(c.agentTurnId) || [];
    list.push(c);
    toolsByTurn.set(c.agentTurnId, list);
  });

  const filter = state.inspectorAgentFilter;
  const visible = filter ? turns.filter((t) => t.agentRole === filter) : turns;

  const header = filter
    ? `<div class="timeline-filter" style="margin-bottom:8px;">
         <span class="timeline-filter__text">Showing only <strong>${escapeHtml(filter)}</strong> — ${visible.length} of ${turns.length} turn(s)</span>
         <button class="timeline-filter__clear" type="button" data-action="inspector-clear-agent">Show all ✕</button>
       </div>`
    : '';

  if (!turns.length) {
    return `
      <div class="pkg__block">
        <p class="pkg__block-title"><span>Agent trace</span></p>
        <p style="font-size:12px;margin:0;color:var(--text-muted);">No turn has been recorded for this case yet.</p>
      </div>`;
  }

  const rows = visible.map((t) => {
    const tools = toolsByTurn.get(t.id) || [];
    const meta = [];
    if (t.round > 0) meta.push(`round ${t.round}`);
    if (t.hop > 0) meta.push(`hop ${t.hop}`);
    if (t.modelUsed) meta.push(t.modelUsed);
    meta.push(`${formatCount((t.tokensIn || 0) + (t.tokensOut || 0))} tok`);
    meta.push(formatDuration(t.durationMs));

    return `
      <li class="evidence-item insp-turn">
        <div class="evidence-item__header" data-expandable role="button" tabindex="0" aria-expanded="false">
          <span>
            <span class="insp-turn__dot insp-turn__dot--${escapeHtml(inspectorOutcomeClass(t.outcomeStatus))}"></span>
            <strong>${escapeHtml(t.agentRole)}</strong>
            <span class="insp-turn__kind">${escapeHtml(t.kind || '')}</span>
            ${tools.length ? `<span class="insp-turn__kind">${tools.length} tool call(s)</span>` : ''}
          </span>
          <span class="evidence-item__header-right">
            <span class="insp-turn__meta">${escapeHtml(meta.join(' · '))}</span>
            <span class="evidence-item__chevron">▾</span>
          </span>
        </div>
        <div class="evidence-item__detail" hidden>
          <div class="stage-detail" style="padding:12px;">
            <dl>
              <dt>Outcome</dt><dd>${escapeHtml(t.outcomeStatus || '—')}</dd>
              <dt>When</dt><dd>${escapeHtml(String(t.createdAt || '').replace('T', ' ').slice(0, 19))} UTC</dd>
              ${t.inputContext ? `<dt>Asked because</dt><dd>${escapeHtml(t.inputContext)}</dd>` : ''}
              ${t.errorMessage ? `<dt>Error</dt><dd style="color:var(--status-critical)">${escapeHtml(t.errorMessage)}</dd>` : ''}
            </dl>
            ${renderInspectorToolCalls(tools)}
            <p style="font-size:11.5px;color:var(--text-muted);margin:12px 0 2px;">Prompt sent</p>
            <pre style="max-height:240px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(t.promptText || '')}</pre>
            <p style="font-size:11.5px;color:var(--text-muted);margin:10px 0 2px;">Raw response, before parsing</p>
            <pre style="max-height:200px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(t.rawResponseText || '')}</pre>
          </div>
        </div>
      </li>`;
  }).join('');

  return `
    <div class="pkg__block">
      <p class="pkg__block-title">
        <span>Agent trace</span>
        <span class="pkg__size">${turns.length} turn(s) · ${formatCount(d.llmCalls ?? 0)} LLM calls · ${formatCount((d.tokensIn ?? 0) + (d.tokensOut ?? 0))} tokens</span>
      </p>
      ${header}
      <ul class="evidence-list insp-rail">${rows || inspectorNoTurnNote(d, filter)}</ul>
    </div>`;
}

/** "No turn by this agent" is the one framing this view exists to avoid: an agent with no source
 *  wired up did not fail to find anything, it was never able to look. Its blackboard entry says so
 *  in its own words, so that is what gets shown instead. */
function inspectorNoTurnNote(d, filter) {
  if (!filter) return '<li class="state-block">No turn has been recorded.</li>';
  const entry = (Array.isArray(d.blackboard) ? d.blackboard : []).find((e) => e && e.agentRole === filter);
  const summary = entry && entry.summary ? `<span class="insp-norun__sum">${escapeHtml(entry.summary)}</span>` : '';

  if (entry && entry.status === 'no_source') {
    return `<li class="insp-norun insp-norun--nosource">
      <strong>${escapeHtml(filter)} has no source wired up.</strong>${summary}
      <em>It produced no turn because it could never run — not because it ran and found nothing.</em>
    </li>`;
  }
  if (entry && entry.status === 'failed') {
    return `<li class="insp-norun insp-norun--failed"><strong>${escapeHtml(filter)} failed before recording a turn.</strong>${summary || '<span class="insp-norun__sum">No reason was recorded.</span>'}</li>`;
  }
  if (entry && entry.status === 'empty') {
    return `<li class="insp-norun"><strong>${escapeHtml(filter)} ran against a real source and found nothing relevant.</strong>${summary}</li>`;
  }
  if (entry) {
    return `<li class="insp-norun"><strong>${escapeHtml(filter)} wrote to the blackboard but no turn was recorded for it.</strong>${summary}</li>`;
  }
  return `<li class="state-block">${escapeHtml(filter)} recorded no turn and left nothing on the blackboard for this case.</li>`;
}

function inspectorOutcomeClass(outcome) {
  return { final: 'done', needs_help: 'help', excluded: 'nosource', error: 'failed' }[outcome] || 'pending';
}

function renderInspectorToolCalls(tools) {
  if (!tools.length) return '';
  return `
    <p style="font-size:11.5px;color:var(--text-muted);margin:10px 0 2px;">Tool calls</p>
    <table class="pkg__table">
      <thead><tr><th>Tool</th><th>Arguments</th><th class="num">Rows</th><th class="num">Duration</th><th>Result</th></tr></thead>
      <tbody>
        ${tools.map((c) => `
          <tr>
            <td><code>${escapeHtml(c.toolName || '')}</code></td>
            <td><code style="font-size:10px;">${escapeHtml(inspectorTruncate(JSON.stringify(c.arguments ?? {}), 90))}</code></td>
            <td class="num">${formatCount(c.resultRowCount ?? 0)}</td>
            <td class="num">${formatDuration(c.durationMs)}</td>
            <td>
              ${c.success
                ? '<span style="color:var(--status-healthy);font-weight:600;font-size:11.5px;">ok</span>'
                : `<span style="color:var(--status-critical);font-weight:600;font-size:11.5px;">failed${c.errorMessage ? ` — ${escapeHtml(c.errorMessage)}` : ''}</span>`}
              ${c.truncated ? '<div style="color:var(--status-degraded);font-size:10.5px;">truncated — this is not the whole result</div>' : ''}
              <pre style="max-height:140px;overflow:auto;white-space:pre-wrap;font-size:9.5px;margin-top:4px;">${escapeHtml(JSON.stringify(c.result ?? null, null, 2))}</pre>
            </td>
          </tr>`).join('')}
      </tbody>
    </table>`;
}


/** The title carries the filter too, so a modal showing one cycle never looks like the full list. */
function openScoutEvidenceModal(monkey) {
  state.scoutModalMonkeyId = monkey.id;
  const cycle = state.scoutCycleFilter
    ? (monkey.cycles || []).find((c) => c.id === state.scoutCycleFilter)
    : null;
  const title = cycle
    ? `${monkey.name} — Evidence · cycle of ${new Date(cycle.startedAt).toLocaleTimeString()}`
    : `${monkey.name} — Evidence`;
  openModal(title, renderScoutEvidence(monkey), { wide: true });
}

/** The Scout Monkey's evidence is not findings but handoffs: one entry per InvestigationPackage
 *  prepared for the L3 team. Each entry keeps the board's measurements and the Scout's own
 *  reading visually separate, so nobody mistakes an inference for a fact. */
function renderScoutEvidence(monkey) {
  const cycleId = state.scoutCycleFilter;
  const cycle = cycleId ? (monkey.cycles || []).find((c) => c.id === cycleId) : null;
  const allHandoffs = monkey.activity.filter((a) => a.stage === 'handoff');
  // The modal honours the timeline's filter: opening evidence right after pinning a cycle and
  // getting every package back would silently drop the question the reader just asked.
  const items = cycle ? allHandoffs.filter((a) => a.scoutRunId === cycleId) : allHandoffs;
  const cycleForReceipt = cycle || monkey.lastCycle;

  const filterBlock = cycle ? `
    <div class="timeline-filter" style="margin-bottom:12px;">
      <span class="timeline-filter__text">
        Showing only the cycle of <strong>${new Date(cycle.startedAt).toLocaleTimeString()}</strong> —
        <strong>${items.length}</strong> of ${allHandoffs.length} handoff(s)
      </span>
      <button class="timeline-filter__clear" type="button" data-action="evidence-show-all">Show all ✕</button>
    </div>` : '';

  const cycleBlock = cycleForReceipt ? `
    <div class="stage-detail" style="margin-bottom:12px;">
      <dl>
        <dt>${cycle ? 'This cycle' : 'Last cycle'}</dt><dd>${escapeHtml(cycleForReceipt.status)}${cycleForReceipt.durationMs != null ? ` · ${Math.round(cycleForReceipt.durationMs / 1000)}s` : ''}</dd>
        <dt>Board rows read</dt><dd>${formatCount(cycleForReceipt.boardRowsEvaluated ?? 0)} · ${cycleForReceipt.candidatesFound ?? 0} candidate(s)</dd>
        <dt>Packaged</dt><dd>${cycleForReceipt.packagesCreated ?? 0} new · ${cycleForReceipt.packagesSkippedDuplicate ?? 0} already known · ${cycleForReceipt.packagesSkippedCap ?? 0} over cap · ${cycleForReceipt.packagesVetoedByScout ?? 0} vetoed</dd>
      </dl>
      ${cycleForReceipt.errorMessage ? `<p style="color:var(--status-critical,#d33);font-size:12px;margin:8px 0 0;">${escapeHtml(cycleForReceipt.errorMessage)}</p>` : ''}
    </div>` : '';

  const rows = items.map((a) => `
    <li class="evidence-item" data-package-id="${escapeHtml(a.packageId || '')}">
      <div class="evidence-item__header" data-expandable role="button" tabindex="0" aria-expanded="false">
        <span>${escapeHtml(a.text)}</span>
        <span class="evidence-item__header-right">
          <span class="evidence-item__chevron">▾</span>
        </span>
      </div>
      <div class="evidence-item__detail" hidden>${renderHandoffDetail(a)}</div>
    </li>`).join('');

  const empty = cycle
    ? 'This cycle handed off nothing new — every candidate it found was already with L3.'
    : 'No handoffs yet - the boards have been clear.';

  return `
    <h3>Investigation Packages Handed Off</h3>
    <p style="color:var(--text-muted);font-size:12px;margin-bottom:10px;">Each item is a self-contained package prepared for the L3 team. Click to see what the board measured and what the Scout made of it.</p>
    ${filterBlock}
    ${cycleBlock}
    <ul class="evidence-list">${rows || `<li class="state-block">${escapeHtml(empty)}</li>`}</ul>
    ${cycle ? renderSkippedCandidates(cycle, allHandoffs) : ''}`;
}

/** One handoff package, in three clearly separated epistemic layers: what Azure measured, what is
 *  arithmetic over those measurements, and what a model thinks. An unlabelled claim reads as a
 *  fact, so every statement here carries its origin. */
function renderHandoffDetail(a) {
  const club = a.club || {};
  const impact = a.impact || {};
  const ev = a.evidence || {};
  const row = ev.boardRow || {};
  const payload = ev.groupPayload || {};

  const exceptions = impact.exceptions ?? 0;
  const requests = impact.requests ?? 0;
  const failed = impact.failedRequests ?? 0;
  const members = impact.affectedUsers ?? 0;

  const brand = club.brandName
    ? `${escapeHtml(club.clubName || club.brandName)} <span style="color:var(--text-muted);">(${escapeHtml(club.clubName ? club.brandName : '')}${club.clubName ? ' · ' : ''}${escapeHtml(club.resolvedBy || '')})</span>`
    : `<span style="color:var(--text-muted);">Unknown brand — ${escapeHtml(club.entityKey || '—')} is not in the catalogue</span>`;

  return `
    <div class="stage-detail" style="padding:14px;">
      ${renderPackageHeadline(a, row, exceptions, requests, failed)}
      ${renderPackageCounters(exceptions, requests, failed, members, row)}

      <div class="pkg__block pkg__block--measured">
        <p class="pkg__block-title"><span>What the board measured</span><span class="pkg__tag pkg__tag--measured">fact · reproducible in Azure</span></p>
        <dl class="pkg__facts">
          <dt>Window</dt><dd>${formatWindow(a.evidence)} <span style="color:var(--text-muted)">· ${escapeHtml(row.IntervalStatus || 'interval status unknown')}</span></dd>
          <dt>Club</dt><dd>${brand}${club.clubId ? ` <span style="color:var(--text-muted)">· club ${escapeHtml(club.clubId)}</span>` : ''}${club.icePartnerId ? ` <span style="color:var(--text-muted)">/ partner ${escapeHtml(club.icePartnerId)}</span>` : ''}</dd>
          <dt>Stage</dt><dd>${escapeHtml(a.evidence?.stage || '—')}${payload.LatencyLimitMs ? ` <span style="color:var(--text-muted)">· budget ${formatMs(payload.LatencyLimitMs)}</span>` : ''}</dd>
          ${renderMemberCoverage(members, row)}
        </dl>
        ${renderSignatureTable(row, exceptions)}
        ${renderPathBars(payload, row)}
        ${renderTriggerRules(a.reason, row, exceptions)}
      </div>

      ${renderScoutReading(a.assessment)}

      <p class="pkg__block-title" style="margin:14px 0 6px;">Reproduce it yourself</p>
      <button class="btn btn--icon btn--sm" type="button" data-action="toggle-llm-log" aria-expanded="false">
        Raw events, queries &amp; analysis log <span class="evidence-item__chevron">▾</span>
      </button>
      <div data-llm-log hidden style="margin-top:8px;"></div>
    </div>`;
}

/** Derived, not measured: a one-line reading of the numbers below it. Labelled as such because it
 *  is the most assertive sentence on the screen. */
function renderPackageHeadline(a, row, exceptions, requests, failed) {
  const growth = row.ExceptionGrowthX ?? row.LatencyGrowthX;
  let text;
  if (exceptions > 0 && requests > 0 && failed === 0) {
    text = `${formatCount(exceptions)} browser errors with no API failures.`;
  } else if (exceptions > 0) {
    text = `${formatCount(exceptions)} exceptions and ${formatCount(failed)} failed request(s).`;
  } else {
    text = `${formatCount(requests)} requests ran slow — ${formatCount(failed)} of them failed.`;
  }
  const slowest = row.MaxSeconds ?? row.P95Seconds;
  if (slowest != null && slowest * 1000 > (a.evidence?.groupPayload?.LatencyLimitMs || 0)) {
    text += ` The slowest call took ${slowest.toFixed(1)}s.`;
  }

  return `
    <div class="pkg__headline">
      <div class="pkg__headline-icon">!</div>
      <div>
        <div class="pkg__headline-text">${escapeHtml(text)}</div>
        <div class="pkg__headline-sub">
          ${growth ? `<strong>${growth}×</strong> the preceding interval · ` : ''}${escapeHtml(a.caseId ? a.caseId.split('|').slice(0, -1).join('·').trim() : '')}
          <span class="pkg__tag pkg__tag--derived">derived · arithmetic over the measurements</span>
        </div>
      </div>
    </div>`;
}

/** The headline numbers side by side. When the backend answered cleanly and the browser did not,
 *  that juxtaposition is the case - leaving the reader to spot it wastes their time. */
function renderPackageCounters(exceptions, requests, failed, members, row) {
  const perMember = members > 0 && exceptions > 0 ? Math.round(exceptions / members) : null;
  const note = perMember && perMember > 20
    ? `That is <strong>≈${formatCount(perMember)} exceptions per member</strong> across the ${members} member(s) telemetry could attribute — a repeating loop, not ${formatCount(exceptions)} people hitting an error.`
    : `${members} member(s) identified in the window.`;

  return `
    <div class="pkg__counters">
      <div class="pkg__counters-side">
        <div class="pkg__counters-num" style="color:var(--status-critical);">${formatCount(exceptions)}</div>
        <div class="pkg__counters-label">browser exceptions</div>
      </div>
      <div class="pkg__counters-vs">${failed === 0 ? 'but' : 'and'}</div>
      <div class="pkg__counters-side">
        <div class="pkg__counters-num" style="color:${failed === 0 ? 'var(--status-healthy)' : 'var(--status-critical)'};">${formatCount(failed)}</div>
        <div class="pkg__counters-label">of ${formatCount(requests)} API calls failed</div>
      </div>
      <div class="pkg__counters-note">${note}</div>
    </div>`;
}

/** UserIdCoveragePct answers "is this the whole blast radius or only the attributable slice?" -
 *  the difference between ignoring a case and paging someone. */
function renderMemberCoverage(members, row) {
  const coverage = row.UserIdCoveragePct;
  const anonymous = row.AnonymousBrowserIds;
  if (coverage == null) return `<dt>Members</dt><dd>${formatCount(members)} identified</dd>`;
  return `
    <dt>Members</dt>
    <dd>
      ${formatCount(members)} identified
      <span style="color:var(--text-muted)">· ${coverage}% of events carry a member id${anonymous ? `, ${formatCount(anonymous)} anonymous browser(s)` : ''}</span>
      ${coverage >= 100 ? '<span style="color:var(--status-healthy)"> — this is the full affected set</span>' : ''}
    </dd>`;
}

/** The board aggregates the signature over the whole window, so this is the one place the reader
 *  gets a statement about all the exceptions rather than about the 200-row sample. */
function renderSignatureTable(row, exceptions) {
  const types = Array.isArray(row.ExceptionTypes) ? row.ExceptionTypes : [];
  if (!types.length) return '';
  return `
    <p class="pkg__block-title" style="margin:14px 0 4px;">Error signatures over the whole window</p>
    <table class="pkg__table">
      <thead><tr><th style="width:90px;">Events</th><th>Signature</th></tr></thead>
      <tbody>
        ${types.map((t, i) => `
          <tr>
            <td class="num">${i === 0 && types.length === 1 ? `<span class="pkg__count">${formatCount(exceptions)}</span>` : '<span class="pkg__count pkg__count--low">—</span>'}</td>
            <td><code>${escapeHtml(t)}</code></td>
          </tr>`).join('')}
      </tbody>
    </table>
    ${types.length === 1
      ? '<p style="font-size:11.5px;color:var(--text-muted);margin:6px 0 0;">A single signature accounts for every exception in the window — one defect repeating.</p>'
      : `<p style="font-size:11.5px;color:var(--text-muted);margin:6px 0 0;">${types.length} distinct signatures. Per-signature counts are not aggregated by the board.</p>`}`;
}

function renderPathBars(payload, row) {
  const slow = Array.isArray(payload.SlowPaths) ? payload.SlowPaths : [];
  const all = Array.isArray(payload.Paths) ? payload.Paths : [];
  if (!all.length) return '';

  const budgetMs = payload.LatencyLimitMs || 1000;
  const worstMs = (row.MaxSeconds ?? row.P95Seconds ?? 0) * 1000;
  const others = all.filter((p) => !slow.includes(p));

  const rows = slow.map((p) => `
    <div class="pkg__path">
      <span class="pkg__path-label"><code>${escapeHtml(p)}</code></span>
      <span class="pkg__path-bar"><span class="pkg__bar"><span style="width:100%"></span></span></span>
      <span class="pkg__path-val">over budget</span>
    </div>`).join('');

  return `
    <p class="pkg__block-title" style="margin:14px 0 4px;">Request paths — budget ${formatMs(budgetMs)} for this stage</p>
    ${rows}
    ${others.length ? `
      <div class="pkg__path">
        <span class="pkg__path-label" style="color:var(--text-muted);">${others.length} more path(s) within budget</span>
        <span class="pkg__path-bar"><span class="pkg__bar pkg__bar--ok"><span style="width:8%"></span></span></span>
        <span class="pkg__path-val">ok</span>
      </div>` : ''}
    ${worstMs ? `<p style="font-size:11.5px;color:var(--text-muted);margin:6px 0 0;">Slowest call in the window: <strong>${(worstMs / 1000).toFixed(1)}s</strong>${row.P95Seconds != null ? ` · p95 ${row.P95Seconds.toFixed(1)}s` : ''}${row.MeanSeconds != null ? ` · mean ${row.MeanSeconds.toFixed(1)}s` : ''}.</p>` : ''}`;
}

/** The board's rule is deterministic, so it is shown as a checklist with the observed values -
 *  "why did this fire?" should never require reading KQL. */
function renderTriggerRules(reason, row, exceptions) {
  const growth = row.ExceptionGrowthX ?? row.LatencyGrowthX;
  const previous = row.PreviousExceptions ?? (row.PreviousP95Seconds != null ? `${row.PreviousP95Seconds.toFixed(1)}s p95` : null);

  return `
    <p class="pkg__block-title" style="margin:14px 0 4px;">Why the board raised it</p>
    <div class="pkg__rule"><span class="pkg__rule-check">✓</span><span>${escapeHtml(reason || 'No reason recorded.')}</span></div>
    ${growth != null ? `
      <div class="pkg__rule">
        <span class="pkg__rule-check">✓</span>
        <span><strong>${growth}×</strong> the preceding interval${previous != null ? ` — ${typeof previous === 'number' ? formatCount(previous) : escapeHtml(String(previous))} before, ${formatCount(exceptions || 0)} now` : ''}.</span>
      </div>` : ''}`;
}

function renderScoutReading(assessment) {
  const a = assessment || {};
  if (!a.available) {
    return `
      <div class="pkg__block pkg__block--inferred">
        <p class="pkg__block-title"><span>The Scout's reading</span><span class="pkg__tag pkg__tag--inferred">unavailable</span></p>
        <p style="font-size:12.5px;margin:0;color:#7a4f06;">
          No reading — ${escapeHtml(a.unavailableReason || 'the model was unavailable')}.
          <strong>Everything above is unaffected</strong> and the package is still a valid handoff.
        </p>
      </div>`;
  }
  const confidence = a.confidence != null ? Math.round(a.confidence * 100) : null;
  return `
    <div class="pkg__block pkg__block--inferred">
      <p class="pkg__block-title"><span>The Scout's reading</span><span class="pkg__tag pkg__tag--inferred">inference · a model's opinion</span></p>
      <dl class="pkg__facts">
        <dt>Symptom</dt><dd>${escapeHtml(a.symptom || '—')}</dd>
        <dt>Reading</dt><dd>${escapeHtml(a.interpretation || '—')}</dd>
        <dt>Escalate?</dt><dd><strong style="color:${a.shouldEscalate === false ? 'var(--text-muted)' : 'var(--status-critical)'}">${a.shouldEscalate === false ? 'No' : 'Yes'}</strong>${a.shouldEscalate === false && a.whyNotEscalate ? ` — ${escapeHtml(a.whyNotEscalate)}` : ''}</dd>
        ${confidence != null ? `<dt>Confidence in this reading</dt><dd>
          <div style="display:flex;align-items:center;gap:8px;max-width:240px;">
            <span class="pkg__bar ${confidence >= 70 ? 'pkg__bar--ok' : 'pkg__bar--warn'}" style="flex:1;"><span style="width:${confidence}%"></span></span><strong>${confidence}%</strong>
          </div></dd>` : ''}
        <dt>Model</dt><dd>${escapeHtml(a.modelUsed || '—')}${a.tokensIn != null ? ` · ${formatCount(a.tokensIn)} in / ${formatCount(a.tokensOut || 0)} out tokens` : ''}</dd>
      </dl>
    </div>`;
}

function formatWindow(evidence) {
  if (!evidence?.windowFrom) return '—';
  const from = new Date(evidence.windowFrom);
  const to = evidence.windowTo ? new Date(evidence.windowTo) : null;
  const date = from.toISOString().slice(0, 10);
  const hm = (d) => d.toISOString().slice(11, 16);
  return `${date} ${hm(from)}${to ? `–${hm(to)}` : ''} UTC`;
}

function formatMs(ms) {
  return ms >= 1000 ? `${(ms / 1000).toFixed(ms % 1000 === 0 ? 0 : 1)} s` : `${Math.round(ms)} ms`;
}

/** Fetches GET /api/scout/packages/{id} on first expand - the full handoff is heavier than the
 *  card payload (it carries the raw rows, both queries and the whole prompt/response), so it is
 *  never part of the /api/monkeys response. */
async function loadScoutPackageDetail(packageId, container) {
  container.innerHTML = '<p style="color:var(--text-muted);font-size:12px;margin:0;">Loading…</p>';
  try {
    const response = await fetch(`/api/scout/packages/${encodeURIComponent(packageId)}`);
    if (!response.ok) {
      container.innerHTML = `<p style="color:var(--text-muted);font-size:12px;margin:0;">Could not load the package detail (HTTP ${response.status}).</p>`;
      return;
    }
    container.innerHTML = renderScoutPackageDetail(await response.json());
  } catch (err) {
    container.innerHTML = '<p style="color:var(--text-muted);font-size:12px;margin:0;">Could not load the package detail.</p>';
  }
}

/** The reproducible half of the handoff: the individual events behind the aggregate, the two
 *  queries that produced them, and the exact prompt/response behind the Scout's opinion. */
function renderScoutPackageDetail(d) {
  const raw = d.rawEvidence || {};
  const events = parseJsonArray(raw.rowsJson);
  const queries = d.queries || {};
  const call = d.llmCall;
  const impact = d.impact || {};
  const total = (impact.requests ?? 0) + (impact.exceptions ?? 0);

  const kinds = events.reduce((acc, e) => { acc[e.Kind || 'other'] = (acc[e.Kind || 'other'] || 0) + 1; return acc; }, {});
  const kindSummary = Object.entries(kinds).map(([k, n]) => `${formatCount(n)} ${k}`).join(', ');

  const eventRows = events.slice(0, 100).map((e) => `
    <tr>
      <td>${escapeHtml(e.Kind || '')}</td>
      <td><code>${escapeHtml(String(e.Timestamp || '').replace('T', ' ').slice(0, 19))}</code></td>
      <td><code>${escapeHtml(e.Path || e.Name || '')}</code></td>
      <td class="num">${e.DurationMs != null ? Math.round(e.DurationMs) + ' ms' : ''}</td>
      <td>${escapeHtml(e.ResultCode || e.ExceptionType || '')}</td>
    </tr>`).join('');

  return `
    ${renderRecurrence(d.recurrence, d.window)}

    <p class="pkg__block-title" style="margin:14px 0 6px;"><span>Individual events</span><span class="pkg__size">${formatCount(raw.rowCount ?? 0)} rows${kindSummary ? ` · ${kindSummary}` : ''}</span></p>
    ${raw.truncated && total > (raw.rowCount ?? 0) ? `
      <div class="pkg__warn">
        <span style="font-size:14px;">⚠</span>
        <span>This is a <strong>${formatCount(raw.rowCount ?? 0)}-event sample of ${formatCount(total)}</strong>, and it is <strong>not proportional</strong> — use it to inspect individual events, not to compute ratios. The counts above the fold come from the board aggregate over the full window.</span>
      </div>` : ''}
    ${raw.error ? `<p style="color:var(--text-muted);font-size:12px;margin:0 0 6px;">${escapeHtml(raw.error)}</p>` : ''}
    ${events.length ? `<div style="max-height:320px;overflow:auto;"><table class="pkg__table">
      <thead><tr><th>Kind</th><th>When (UTC)</th><th>Path / operation</th><th class="num">Duration</th><th>Result</th></tr></thead>
      <tbody>${eventRows}</tbody></table></div>
      ${events.length > 100 ? `<p style="color:var(--text-muted);font-size:11px;margin:4px 0 0;">Showing the first 100 of ${formatCount(events.length)} sampled.</p>` : ''}` : ''}

    <p class="pkg__block-title" style="margin:16px 0 6px;">Queries — run these in Azure to reproduce</p>
    ${queries.board ? `<p style="font-size:11.5px;color:var(--text-muted);margin:0 0 2px;">Board query — scores every club's 5-minute buckets and ranked this one</p><pre style="max-height:180px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(queries.board)}</pre>` : ''}
    ${queries.drillDown ? `<p style="font-size:11.5px;color:var(--text-muted);margin:10px 0 2px;">Drill-down query — pulls these individual events by operation id</p><pre style="max-height:180px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(queries.drillDown)}</pre>` : ''}

    <p class="pkg__block-title" style="margin:16px 0 6px;"><span>Analysis log</span>${call ? `<span class="pkg__size">${escapeHtml(call.modelUsed || '')} · ${formatCount(call.tokensIn ?? 0)} in / ${formatCount(call.tokensOut ?? 0)} out · ${formatCount(call.durationMs ?? 0)} ms</span>` : ''}</p>
    ${call ? `
      ${call.errorMessage ? `<p style="color:var(--status-critical);font-size:12px;margin:0 0 6px;">${escapeHtml(call.errorMessage)}</p>` : ''}
      <p style="font-size:11.5px;color:var(--text-muted);margin:0 0 2px;">Prompt sent</p>
      <pre style="max-height:260px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(call.promptText || '')}</pre>
      <p style="font-size:11.5px;color:var(--text-muted);margin:10px 0 2px;">Raw response, before parsing</p>
      <pre style="max-height:200px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(call.rawResponseText || '')}</pre>`
      : '<p style="color:var(--text-muted);font-size:12px;margin:0;">No analysis log for this package.</p>'}

    <div class="pkg__gaps">
      <strong>What this package still cannot answer.</strong> These are data the detection phase does not capture yet, not omissions in this view:
      <ul>
        <li><strong>Baseline:</strong> only the immediately preceding interval is compared. There is no hour-over-hour or day-over-day normal.</li>
        <li><strong>Browser, OS and geography:</strong> not collected — with the stack trace masked, this is the main remaining way to narrow a front-end defect.</li>
        <li><strong>Blast radius denominator:</strong> how many members were shopping this club overall, not just how many hit the error.</li>
        <li><strong>Business impact:</strong> whether bookings actually dropped.</li>
      </ul>
    </div>`;
}

/** A package is a single 5-minute bucket, so on its own it cannot say whether the problem is over.
 *  Recurrence across cycles is the only thing that can. */
function renderRecurrence(recurrence, window) {
  const r = recurrence || {};
  if (!r.occurrences) return '';

  const lastSeen = r.lastSeen ? new Date(r.lastSeen) : null;
  const thisWindow = window?.from ? new Date(window.from) : null;
  const isLatest = lastSeen && thisWindow && Math.abs(lastSeen - thisWindow) < 60000;
  const minutesSinceLast = lastSeen ? Math.round((Date.now() - lastSeen) / 60000) : null;

  if (r.occurrences === 1) {
    return `
      <div class="pkg__warn" style="background:var(--status-info-bg);border-color:rgba(47,111,237,.3);color:var(--status-info);">
        <span style="font-size:14px;">i</span>
        <span>First and only time this group has been packaged. <strong>No recurrence yet.</strong></span>
      </div>`;
  }

  const stillFiring = minutesSinceLast != null && minutesSinceLast <= 15;
  return `
    <div class="pkg__warn" style="${stillFiring ? '' : 'background:var(--status-info-bg);border-color:rgba(47,111,237,.3);color:var(--status-info);'}">
      <span style="font-size:14px;">${stillFiring ? '⚠' : 'i'}</span>
      <span>
        Packaged <strong>${formatCount(r.occurrences)} times</strong> for this club and stage.
        ${stillFiring
          ? `Still firing — most recently <strong>${minutesSinceLast} min ago</strong>.`
          : `Last seen <strong>${formatRelativeTime(r.lastSeen)}</strong>${isLatest ? ' (this window)' : ''}.`}
        <span style="opacity:.8">First seen ${formatRelativeTime(r.firstSeen)}.</span>
      </span>
    </div>`;
}

/** The raw rows are stored as a JSON string so the package stays one payload; a malformed one must not break the whole detail. */
function parseJsonArray(value) {
  if (Array.isArray(value)) return value;
  if (typeof value !== 'string' || !value.length) return [];
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed : [];
  } catch (err) {
    return [];
  }
}

/** Every observation expands to show the captured event data (stage, threshold, observed count,
 *  affected journeys/correlation ids, status breakdown - see GetMonkeysFunction's "evidence"
 *  field). Ones already diagnosed (a.diagnosed) also get a badge pointing at the Doctor Monkey's
 *  own evidence view, where the actual analysis now lives. */
function renderLatencySpikeEvidence(monkey) {  const items = monkey.activity.filter((a) => a.stage === 'observation');
  const rows = items.map((a) => `
    <li class="evidence-item${a.diagnosed ? ' evidence-item--diagnosed' : ''}">
      <div class="evidence-item__header" data-expandable role="button" tabindex="0" aria-expanded="false">
        <span>${escapeHtml(a.text)}</span>
        <span class="evidence-item__header-right">
          ${a.diagnosed ? '<span class="evidence-item__badge">Diagnosed by Doctor Monkey</span>' : ''}
          <span class="evidence-item__chevron">▾</span>
        </span>
      </div>
      <div class="evidence-item__detail" hidden>${renderEventDetail(a.evidence)}</div>
    </li>`).join('');

  return `
    <h3>Latest Observation Evidence</h3>
    <p style="color:var(--text-muted);font-size:12px;margin-bottom:10px;">Click an item to see the captured event data (affected journeys, thresholds, status breakdown).</p>
    <ul class="evidence-list">${rows || '<li class="state-block">No observations yet.</li>'}</ul>`;
}

/** Renders the raw EvidenceJson captured by the detector at Finding creation time (see
 *  LatencySpikeDetectorService/SqlLatencySpikeDetectorService) - the actual events behind an
 *  observation like "3 journeys degraded (>120ms) at stage '07 Booking'". */
function renderEventDetail(evidence) {
  if (!evidence) {
    return '<div class="stage-detail"><p style="color:var(--text-muted);margin:0;">No structured evidence captured for this observation.</p></div>';
  }
  const correlationIds = Array.isArray(evidence.correlationIds) ? evidence.correlationIds : [];
  const operationIds = Array.isArray(evidence.operationIds) ? evidence.operationIds : [];
  const isHealthStatus = !!evidence.healthStatusBreakdown;
  const breakdown = evidence.statusCodeBreakdown || evidence.healthStatusBreakdown || {};
  const breakdownLabel = isHealthStatus ? 'Health status breakdown' : 'HTTP status code breakdown';

  return `
    <div class="stage-detail">
      <dl>
        <dt>Stage</dt><dd>${escapeHtml(evidence.stage || '—')}</dd>
        <dt>Threshold</dt><dd>&gt;${evidence.spikeThresholdMs ?? '—'} ms, occurrence ≥ ${evidence.occurrenceThreshold ?? '—'}${evidence.windowMinutes ? `, window ${evidence.windowMinutes}m` : ''}</dd>
        <dt>Observed</dt><dd>${evidence.observedCount ?? '—'} ${isHealthStatus ? 'journeys' : 'requests'} · max ${evidence.maxDurationMs ?? '—'} ms</dd>
        ${evidence.avgFailureRatePct != null ? `<dt>Avg failure rate</dt><dd>${evidence.avgFailureRatePct}%</dd>` : ''}
      </dl>
      ${Object.keys(breakdown).length ? `<h4 style="font-size:12px;margin:10px 0 4px;">${breakdownLabel}</h4><ul class="simple-list">${Object.entries(breakdown).map(([k, v]) => `<li>${escapeHtml(k)}: ${v}</li>`).join('')}</ul>` : ''}
      ${correlationIds.length ? `<h4 style="font-size:12px;margin:10px 0 4px;">Affected journeys (Correlation ID)</h4><ul class="simple-list">${correlationIds.map((id) => `<li><code>${escapeHtml(id)}</code></li>`).join('')}</ul>` : ''}
      ${operationIds.length ? `<h4 style="font-size:12px;margin:10px 0 4px;">Operation IDs</h4><ul class="simple-list">${operationIds.map((id) => `<li><code>${escapeHtml(id)}</code></li>`).join('')}</ul>` : ''}
      ${evidence.note ? `<p style="color:var(--text-muted);font-size:11px;margin-top:8px;">${escapeHtml(evidence.note)}</p>` : ''}
    </div>`;
}

/** Same shape as renderLatencySpikeEvidence, for the SLO Burn-Rate Monkey's own findings (its
 *  EvidenceJson shape is different - see renderBurnRateEventDetail). */
function renderSloBurnRateEvidence(monkey) {
  const items = monkey.activity.filter((a) => a.stage === 'observation');
  const rows = items.map((a) => `
    <li class="evidence-item${a.diagnosed ? ' evidence-item--diagnosed' : ''}">
      <div class="evidence-item__header" data-expandable role="button" tabindex="0" aria-expanded="false">
        <span>${escapeHtml(a.text)}</span>
        <span class="evidence-item__header-right">
          ${a.diagnosed ? '<span class="evidence-item__badge">Diagnosed by Doctor Monkey</span>' : ''}
          <span class="evidence-item__chevron">▾</span>
        </span>
      </div>
      <div class="evidence-item__detail" hidden>${renderBurnRateEventDetail(a.evidence)}</div>
    </li>`).join('');

  return `
    <h3>Latest Burn-Rate Findings</h3>
    <p style="color:var(--text-muted);font-size:12px;margin-bottom:10px;">Click an item to see the SLO definition, both burn-rate windows (Google SRE multiwindow method) and their sample counts.</p>
    <ul class="evidence-list">${rows || '<li class="state-block">No burn-rate findings yet.</li>'}</ul>`;
}

/** Renders the EvidenceJson written by BurnRateDetector.EvaluateAsync - domain/stage/signalType,
 *  the configured SLO target, alert severity and both burn-rate windows (long/short) with their
 *  multiplier and sample counts, per Google SRE's multiwindow multi-burn-rate method. */
function renderBurnRateEventDetail(evidence) {
  if (!evidence) {
    return '<div class="stage-detail"><p style="color:var(--text-muted);margin:0;">No structured evidence captured for this finding.</p></div>';
  }
  const severityBadge = evidence.alertSeverity === 'page'
    ? '<span class="status-badge status-badge--sev-critical">Page</span>'
    : '<span class="status-badge status-badge--sev-medium">Ticket</span>';
  const target = evidence.signalType === 'exception_rate'
    ? `${evidence.targetPercentage ?? '—'}% success rate`
    : `${evidence.targetThresholdMs ?? '—'} ms @ ${evidence.targetPercentage ?? '—'}%`;

  return `
    <div class="stage-detail">
      <dl>
        <dt>Domain / Stage</dt><dd>${escapeHtml(evidence.domain || '—')}${evidence.stage ? ` / ${escapeHtml(evidence.stage)}` : ' (domain-wide)'}</dd>
        <dt>Signal</dt><dd>${escapeHtml(evidence.signalType || '—')}</dd>
        <dt>SLO target</dt><dd>${target} over ${evidence.windowDays ?? '—'} days</dd>
        <dt>Severity</dt><dd>${severityBadge}</dd>
      </dl>
      <h4 style="font-size:12px;margin:10px 0 4px;">Burn-rate windows (multiplier ≥ ${evidence.burnRateMultiplier ?? '—'}×)</h4>
      <ul class="simple-list">
        <li>Long window (${evidence.longWindowHours ?? '—'}h): burn rate ${formatBurnRate(evidence.longWindowBurnRate)}× · ${evidence.longWindowSampleCount ?? '—'} samples</li>
        <li>Short window (${evidence.shortWindowMinutes ?? '—'}m): burn rate ${formatBurnRate(evidence.shortWindowBurnRate)}× · ${evidence.shortWindowSampleCount ?? '—'} samples</li>
      </ul>
      ${evidence.note ? `<p style="color:var(--text-muted);font-size:11px;margin-top:8px;">${escapeHtml(evidence.note)}</p>` : ''}
    </div>`;
}

function formatBurnRate(value) {
  return typeof value === 'number' ? value.toFixed(2) : '—';
}

/** Every report expands to the full analysis - verdict, error category, confidence, root cause,
 *  citations and proposed fix - what used to render under the Latency Spike Monkey's evidence. */
function renderDoctorMonkeyEvidence(monkey) {
  const items = monkey.activity.filter((a) => a.stage === 'evidence');
  const rows = items.map((a) => `
    <li class="evidence-item evidence-item--diagnosed" data-finding-id="${escapeHtml(a.findingId)}">
      <div class="evidence-item__header" data-expandable role="button" tabindex="0" aria-expanded="false">
        <span>${escapeHtml(a.findingTitle || a.text)}</span>
        <span class="evidence-item__chevron">▾</span>
      </div>
      <div class="evidence-item__detail" hidden>
        ${renderDoctorAnalysisDetail(a.doctorAnalysis)}
        ${renderNotificationHistory(a.notifications)}
        <button class="btn btn--icon btn--sm" type="button" data-action="toggle-llm-log" aria-expanded="false" style="margin-top:8px;">
          LLM Analysis <span class="evidence-item__chevron">▾</span>
        </button>
        <div class="evidence-item__llm-log" data-llm-log hidden>
          <p style="color:var(--text-muted);font-size:11.5px;margin:8px 0 0;">Loading…</p>
        </div>
      </div>
    </li>`).join('');

  return `
    <h3>Diagnosis History</h3>
    <p style="color:var(--text-muted);font-size:12px;margin-bottom:10px;">Click a diagnosis to expand the full analysis. Use <strong>LLM Analysis</strong> to show or hide the exact prompt sent and the raw response, shown in full.</p>
    <ul class="evidence-list">${rows || '<li class="state-block">No diagnoses yet.</li>'}</ul>`;
}

/** Fetches GET /api/findings/{id}/doctor-report (only on demand, the first time "LLM Analysis" is
 *  toggled open for a diagnosis) and renders the exact prompt sent + raw response text, in full -
 *  any Portal user can see this, there is no truncation by design (see docs/sessions 2026-09-23). */
async function loadLlmCallLog(findingId, container) {
  try {
    const response = await fetch(`/api/findings/${encodeURIComponent(findingId)}/doctor-report`);
    const payload = await response.json().catch(() => ({}));
    if (!response.ok || !payload.llmCallLog) {
      container.innerHTML = '<p style="color:var(--text-muted);font-size:11.5px;margin:0;">No LLM call log recorded for this diagnosis.</p>';
      return;
    }
    const log = payload.llmCallLog;
    container.innerHTML = `
      <h5 style="font-size:11px;margin:8px 0 4px;color:var(--text-muted);">Prompt</h5>
      <pre>${escapeHtml(log.promptText)}</pre>
      <h5 style="font-size:11px;margin:8px 0 4px;color:var(--text-muted);">Raw response</h5>
      <pre>${escapeHtml(log.rawResponseText)}</pre>
      <p style="color:var(--text-muted);font-size:11px;margin:4px 0 0;">${escapeHtml(log.modelUsed)} · ${log.tokensIn} in / ${log.tokensOut} out tokens</p>`;
  } catch (err) {
    container.innerHTML = '<p style="color:var(--text-muted);font-size:11.5px;margin:0;">Could not load the LLM call log.</p>';
  }
}

function renderDoctorAnalysisDetail(analysis) {
  if (!analysis) return '';
  const citations = Array.isArray(analysis.citations) ? analysis.citations : [];
  const fix = analysis.proposedFix;
  return `
    <div class="stage-detail">
      <dl>
        <dt>Verdict</dt><dd>${escapeHtml(analysis.verdict)}</dd>
        <dt>Error category</dt><dd>${escapeHtml(analysis.errorCategory)}</dd>
        <dt>Root cause</dt><dd>${escapeHtml(analysis.rootCauseHypothesis || 'Not established (unknown)')}</dd>
        <dt>Model</dt><dd>${escapeHtml(analysis.modelUsed)} · ${formatRelativeTime(analysis.generatedAt)}</dd>
      </dl>
      <div style="margin-top:8px;font-size:11.5px;color:var(--text-muted);">Confidence</div>
      <div class="confidence-meter"><div class="confidence-meter__fill" style="width:${Math.round(analysis.confidence * 100)}%"></div></div>
      <p style="color:var(--text-muted);font-size:11.5px;margin:2px 0 0;">${Math.round(analysis.confidence * 100)}%</p>
      ${citations.length ? `<h4 style="font-size:12px;margin:10px 0 4px;">Citations</h4><ul class="simple-list">${citations.map((c) => `<li>${escapeHtml(c.why || c.chunkId || JSON.stringify(c))}</li>`).join('')}</ul>` : ''}
      ${fix ? `<h4 style="font-size:12px;margin:10px 0 4px;">Proposed Fix</h4>${fix.description ? `<p>${escapeHtml(fix.description)}</p>` : ''}${fix.codeSnippet ? `<pre>${escapeHtml(fix.codeSnippet)}</pre>` : ''}${fix.filePathHint ? `<p style="color:var(--text-muted);font-size:11px;">${escapeHtml(fix.filePathHint)}</p>` : ''}` : ''}
    </div>`;
}

/** Answers "was this alert already notified (Teams/Email), and did it succeed?" - see
 * monitoring.NotificationLog / GetMonkeysFunction. Empty/missing means either the finding never
 * reached "page" severity (no notification was ever attempted) or it predates this feature. */
function renderNotificationHistory(notifications) {
  if (!Array.isArray(notifications) || notifications.length === 0) {
    return `<h4 style="font-size:12px;margin:10px 0 4px;">Notifications</h4><p style="color:var(--text-muted);font-size:11.5px;margin:0;">Not notified — either this finding never reached "page" severity, or no channel was enabled at the time.</p>`;
  }
  const rows = notifications.map((n) => {
    const badge = n.success
      ? '<span class="status-badge status-badge--sev-low">Sent</span>'
      : '<span class="status-badge status-badge--sev-critical">Failed</span>';
    const provider = n.providerUsed ? ` via ${escapeHtml(n.providerUsed)}` : '';
    const error = n.success ? '' : `<div style="color:var(--status-critical);font-size:11px;margin-top:2px;">${escapeHtml(n.errorMessage || 'Unknown error')}</div>`;
    return `<li>${badge} <strong>${escapeHtml(n.channel)}</strong>${provider} — ${formatRelativeTime(n.sentAt)}${error}</li>`;
  }).join('');
  return `<h4 style="font-size:12px;margin:10px 0 4px;">Notifications</h4><ul class="simple-list">${rows}</ul>`;
}

function handleMonkeyAction(monkey, action) {
  switch (action) {
    case 'queue': {
      const card = document.querySelector(`.monkey-card[data-monkey-id="${monkey.id}"]`);
      if (card) toggleInspectorQueuePanel(card);
      break;
    }
    // The queue strip above only expands the backlog inside the card; this opens the full list
    // already narrowed to what is waiting, which is the question "Open queue" actually asks.
    case 'queue-modal':
      openInspectorEvidenceModal(monkey, 'queued');
      break;
    case 'run':
      if (RUNNABLE_MONKEY_IDS.has(monkey.id)) {
        showToast(`${monkey.name}`, 'Manual run triggered. Re-evaluating…', 'info');
        dataProvider.runMonkeyNow(monkey.id)
          .then(async (result) => {
            // The Inspector reuses the generic summary shape, but "findings/diagnosed" would be a
            // lie there: it claims packages off the queue and investigates them.
            const parts = monkey.id === 'inspector-monkey'
              ? [`${result.newFindings} package(s) claimed`, `${result.diagnosed} investigated`]
              : [`${result.newFindings} new finding(s)`, `${result.diagnosed} diagnosed`];
            if (monkey.id === 'slo-burn-rate-monkey') parts.push(`${result.notified} notified`);
            showToast(`${monkey.name}`, `Run complete — ${parts.join(', ')}.`, 'success');
            state.monkeys = await dataProvider.getMonkeys();
            renderMonkeysTab();
          })
          .catch((err) => {
            showToast(`${monkey.name}`, err.message || 'Run failed.', 'critical');
          });
      } else {
        showToast(`${monkey.name}`, 'Manual run triggered. Re-evaluating observations…', 'info');
        setTimeout(() => showToast(`${monkey.name}`, 'Run complete — no new findings beyond current state.', 'success'), 900);
      }
      break;
    case 'evidence': {
      if (monkey.id === 'latency-spike-monkey') {
        openModal(`${monkey.name} — Evidence`, renderLatencySpikeEvidence(monkey));
      } else if (monkey.id === 'slo-burn-rate-monkey') {
        openModal(`${monkey.name} — Evidence`, renderSloBurnRateEvidence(monkey));
      } else if (monkey.id === 'doctor-monkey') {
        openModal(`${monkey.name} — Evidence`, renderDoctorMonkeyEvidence(monkey));
      } else if (isScoutMonkey(monkey.id)) {
        openScoutEvidenceModal(monkey);
      } else if (monkey.id === 'inspector-monkey') {
        openInspectorEvidenceModal(monkey, 'all');
      } else if (monkey.id === 'rca-monkey') {
        openRcaEvidenceModal(monkey);
      } else {
        const disclaimer = IMPLEMENTED_MONKEY_IDS.has(monkey.id)
          ? ''
          : '<p style="color:var(--text-muted)">This is simulated evidence for demo purposes; a production integration would surface linked Application Insights queries here.</p>';
        openModal(`${monkey.name} — Evidence`, `
          <h3>Latest Observation Evidence</h3>
          <ul class="simple-list">${monkey.activity.filter((a) => a.stage === 'evidence' || a.stage === 'observation').map((a) => `<li>${escapeHtml(a.text)}</li>`).join('')}</ul>
          ${disclaimer}`);
      }
      break;
    }
    case 'approve':
      showToast(`${monkey.name}`, 'Action approved. Executing under guardrails…', 'success');
      monkey.actionsExecuted = Math.min(monkey.actionsExecuted + 1, monkey.actionsProposed);
      monkey.activity.push({ time: new Date().toISOString(), stage: 'action', text: 'Action executed following manual approval via portal.' });
      renderMonkeysTab();
      break;
    case 'reject':
      showToast(`${monkey.name}`, 'Proposed action rejected. No changes made.', 'warning');
      monkey.activity.push({ time: new Date().toISOString(), stage: 'approval', text: 'Proposed action rejected by operator.' });
      renderMonkeysTab();
      break;
    // Keyboard and programmatic paths reach here too, so the refusal lives next to the behaviour
    // rather than only on the disabled attribute.
    case 'pause': {
      // Every implemented monkey is a real agent behind a real timer, so the toggle has to go
      // through the backend. The simulated roster keeps the local-only flip: there is nothing to stop.
      if (IMPLEMENTED_MONKEY_IDS.has(monkey.id)) {
        const next = !monkey.paused;
        showToast(monkey.name, next ? 'Pausing the agent…' : 'Resuming the agent…', 'info');
        dataProvider.setMonkeyPaused(monkey.id, next)
          .then(async (result) => {
            // Re-read from the server instead of flipping the local field: a toast saying "paused"
            // over a card still saying "Live" was exactly the bug this replaces.
            state.monkeys = await dataProvider.getMonkeys();
            renderMonkeysTab();
            showToast(monkey.name, result.paused
              ? 'Agent paused — it will not run again until it is resumed.'
              : 'Agent resumed — it will run again on its usual schedule.', 'success');
          })
          .catch((err) => {
            showToast(monkey.name, `Not ${next ? 'paused' : 'resumed'} — ${err.message || 'the request failed'}. The agent is unchanged.`, 'critical');
          });
        break;
      }
      monkey.status = monkey.status === 'active' ? 'paused' : 'active';
      showToast(`${monkey.name}`, monkey.status === 'paused' ? 'Agent paused.' : 'Agent resumed.', 'info');
      renderMonkeysTab();
      break;
    }
    case 'mode': {
      if (monkey.id === 'inspector-monkey') {
        showToast(`${monkey.name}`, 'Mode unchanged — this portal cannot change the Inspector\'s mode.', 'warning');
        break;
      }
      const modes = ['Observe', 'Recommend', 'Approval Required', 'Autonomous'];
      const next = modes[(modes.indexOf(monkey.mode) + 1) % modes.length];
      monkey.mode = next;
      showToast(`${monkey.name}`, `Mode changed to "${next}".`, 'info');
      renderMonkeysTab();
      break;
    }
  }
}

/* -----------------------------------------------------------------------
   5f. EXCEPTIONS TAB
   ----------------------------------------------------------------------- */
function exceptionClubLabel(exception) {
  return exception.clubName || exception.clubId || '—';
}

function exceptionClubCell(exception) {
  const label = exceptionClubLabel(exception);
  const id = exception.clubId || '';
  const title = id && id !== label ? ` title="${escapeHtml(id)}"` : '';
  return `<span${title}>${escapeHtml(label)}</span>`;
}

/** Short class name for the table; the full type stays in the tooltip and the detail panel. */
function exceptionTypeCell(type) {
  const full = String(type || '').trim();
  if (!full) return '—';
  // Browser rows store "TypeError: message…". The name is the text before the first colon.
  // Dotted .NET types have no colon, so they still shorten to the class after the last dot.
  const head = (full.includes(':') ? full.split(':')[0] : full).trim().replace(/\.+$/, '');
  const short = (head.split('.').pop() || '').trim();
  if (!short) return '—';
  return `<span class="exception-type" title="${escapeHtml(full)}">${escapeHtml(short)}</span>`;
}

const EXCEPTION_URL_MAX_CHARS = 72;

/**
 * Short display text for an exception URL. Query strings (often auth tokens) and
 * fragments are dropped, then the remainder is clipped with an ellipsis. The full
 * URL stays in the link href/title.
 */
function truncateExceptionUrl(url) {
  const full = String(url || '').trim();
  if (!full) return '';
  let display = full;
  const cut = display.search(/[?#]/);
  if (cut >= 0) display = `${display.slice(0, cut)}…`;
  if (display.length > EXCEPTION_URL_MAX_CHARS) {
    display = `${display.slice(0, EXCEPTION_URL_MAX_CHARS - 1).replace(/…$/, '')}…`;
  }
  return display;
}

function exceptionUrlDetail(url) {
  if (!url) return '—';
  const safe = escapeHtml(url);
  const label = escapeHtml(truncateExceptionUrl(url));
  if (/^https?:\/\//i.test(url)) {
    return `<a class="exception-url" href="${safe}" title="${safe}" target="_blank" rel="noopener noreferrer">${label}</a>`;
  }
  return `<span class="exception-url" title="${safe}">${label}</span>`;
}

function renderExceptionsNote(rowCount) {
  const note = document.getElementById('exceptions-note');
  if (!note) return;
  const meta = state.exceptionsMeta || {};
  if (meta.warning) {
    note.textContent = meta.warning;
    return;
  }
  if (meta.source === 'sql') {
    const count = meta.rowCount ?? rowCount;
    note.textContent = `SQL incident store · ${count} incident${count === 1 ? '' : 's'} · last ${meta.win || headerQueryWin()}`;
    return;
  }
  note.textContent = '';
}

function renderExceptionsTable() {
  if (state.tablesLoading) return;
  markTableBusy(document.getElementById('exceptions-table'), false);
  // Window, domain, club, environment, and id are applied by GET /api/exceptions.
  // Status and region stay here because they are journey-health filters.
  const filtered = state.exceptions.filter((e) => matchesGlobalFilters(e, { ignoreTime: true, ignoreId: true }));
  const { key, dir } = state.sort.exceptions;
  const rows = sortExceptionRows(filtered, key, dir);
  renderExceptionsNote(rows.length);
  const tbody = document.getElementById('exceptions-tbody');

  if (rows.length === 0) {
    const message = (state.exceptionsMeta && state.exceptionsMeta.warning) || 'No exceptions match the current filters.';
    tbody.innerHTML = `<tr><td colspan="10"><div class="state-block">${escapeHtml(message)}</div></td></tr>`;
    return;
  }

  tbody.innerHTML = rows.map((e) => `
    <tr class="${state.selectedExceptionId === e.id ? 'is-selected' : ''}" data-exception-id="${escapeHtml(e.id)}" tabindex="0" role="button" aria-label="View exception ${escapeHtml(e.id)} details">
      <td>${escapeHtml(e.id)}</td>
      <td>${formatRelativeTime(e.timestamp)}</td>
      <td>${severityBadge(e.severity)}</td>
      <td>${escapeHtml(domainLabel(e.domain))}</td>
      <td>${exceptionClubCell(e)}</td>
      <td>${escapeHtml(e.component)}</td>
      <td>${exceptionTypeCell(e.type)}</td>
      <td>${(e.occurrenceCount || 0).toLocaleString()}</td>
      <td>${(e.usersAffected || 0).toLocaleString()}</td>
      <td>${escapeHtml(e.status)}</td>
    </tr>`).join('');

  tbody.querySelectorAll('tr[data-exception-id]').forEach((row) => {
    const open = () => openExceptionDetail(row.dataset.exceptionId);
    row.addEventListener('click', open);
    row.addEventListener('keydown', (ev) => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); open(); } });
  });
}

const SEVERITY_RANK = { critical: 4, high: 3, medium: 2, low: 1 };

function severityRank(severity) {
  return SEVERITY_RANK[String(severity || '').toLowerCase()] || 0;
}

/**
 * Sorts exception rows by the chosen column, then newest timestamp first. Severity sorts by
 * rank (Critical > High > Medium > Low) rather than alphabetically.
 */
function sortExceptionRows(rows, key, dir) {
  const sign = dir === 'desc' ? -1 : 1;
  const valueOf = (e) => (key === 'severity' ? severityRank(e.severity) : (e[key] ?? ''));
  return [...rows].sort((a, b) => {
    const va = valueOf(a);
    const vb = valueOf(b);
    if (va < vb) return -sign;
    if (va > vb) return sign;
    const ta = a.timestamp ?? '';
    const tb = b.timestamp ?? '';
    if (ta > tb) return -1;
    if (ta < tb) return 1;
    return 0;
  });
}

function relatedJourneysFor(exception) {
  // journeysAffected arrives newest first. Keep that stored id for the label and
  // for Find Related Journeys. A loaded row is used only for its outcome, so a
  // member-id fallback is not replaced by that row's later correlation.
  return (exception.journeysAffected || []).map((id) => {
    const live = state.journeys.find((j) => j.id === id || j.correlationId === id);
    if (live) return { id, status: live.status || '' };
    const demo = state.demoJourneysById && state.demoJourneysById[id];
    return { id, status: (demo && demo.status) || '' };
  });
}

function openExceptionDetail(exceptionId) {
  const e = state.exceptions.find((x) => x.id === exceptionId);
  if (!e) return;
  state.selectedExceptionId = exceptionId;
  document.querySelectorAll('#exceptions-tbody tr[data-exception-id]').forEach((row) => {
    row.classList.toggle('is-selected', row.dataset.exceptionId === exceptionId);
  });
  const relatedJourneys = relatedJourneysFor(e);
  const occurrences = e.occurrences && e.occurrences.length
    ? e.occurrences
    : [{ timestamp: e.lastSeen, memberId: e.memberId, url: e.url, message: e.message }];
  const occurrenceRows = occurrences.map((o, index) => `
    <tr${index >= 3 ? ' class="exception-collapsed" hidden' : ''}>
      <td>${formatRelativeTime(o.timestamp)}</td>
      <td>${escapeHtml(o.memberId || '—')}</td>
      <td>${exceptionUrlDetail(o.url)}</td>
      <td class="exception-occurrence-message">${escapeHtml(o.message || '—')}</td>
    </tr>`).join('');
  const journeyItems = relatedJourneys.map((j, index) => `
    <li${index >= 3 ? ' class="exception-collapsed" hidden' : ''}>${escapeHtml(j.id)}${j.status ? ` — ${journeyStatusBadge(j.status)}` : ''}</li>`).join('');
  const message = `<h3>Message</h3><pre class="exception-stack">${escapeHtml(e.type)}: ${escapeHtml(e.message)}</pre>`;
  const stack = e.stackTrace
    ? `<h3>Stack trace</h3><pre class="exception-stack">${escapeHtml(e.stackTrace)}</pre>`
    : '';
  const repositoryRow = e.repository
    ? `<dt>Repository</dt><dd>${escapeHtml(e.repository)}</dd>`
    : '';

  const body = `
    <dl class="exception-detail">
      <dt>Severity</dt><dd>${severityBadge(e.severity)}</dd>
      <dt>Domain</dt><dd>${escapeHtml(domainLabel(e.domain))}</dd>
      <dt>Club</dt><dd>${exceptionClubCell(e)}</dd>
      <dt>URL</dt><dd>${exceptionUrlDetail(e.url)}</dd>
      <dt>Stage</dt><dd>${escapeHtml(e.component)}</dd>
      <dt>Operation</dt><dd>${escapeHtml(e.operation)}</dd>
      <dt>Type</dt><dd>${escapeHtml(e.type)}</dd>
      <dt>First seen</dt><dd>${formatRelativeTime(e.firstSeen)}</dd>
      <dt>Last seen</dt><dd>${formatRelativeTime(e.lastSeen)}</dd>
      <dt>Occurrences</dt><dd>${(e.occurrenceCount || 0).toLocaleString()}</dd>
      <dt>Users affected</dt><dd>${(e.usersAffected || 0).toLocaleString()}</dd>
      ${repositoryRow}
    </dl>
    ${message}
    ${stack}
    <h3>Recent occurrences</h3>
    <div data-collapsible>
      <div class="table-wrap">
        <table class="data-table data-table--compact">
          <thead><tr><th>When</th><th>MemberId</th><th>URL</th><th>Message</th></tr></thead>
          <tbody>${occurrenceRows || '<tr><td colspan="4">No occurrence details stored.</td></tr>'}</tbody>
        </table>
      </div>
      ${occurrences.length > 3 ? '<button type="button" class="btn btn--ghost btn--sm exception-see-more" aria-expanded="false">See more</button>' : ''}
    </div>
    <h3>Related Customer Journeys</h3>
    <div data-collapsible>
      <ul class="simple-list">${journeyItems || '<li>None linked in current window.</li>'}</ul>
      ${relatedJourneys.length > 3 ? '<button type="button" class="btn btn--ghost btn--sm exception-see-more" aria-expanded="false">See more</button>' : ''}
    </div>

    <h3>Actions</h3>
    <div class="exception-actions">
      <button class="btn btn--icon btn--sm" id="btn-assign" type="button">Assign to Team</button>
    </div>`;

  openSidePanel(e.id, body);
  scrollDetailsToTop();
  wireExceptionCollapsibles();
  wireExceptionActions(e);
}

function wireExceptionCollapsibles() {
  document.querySelectorAll('#side-panel-body [data-collapsible] .exception-see-more').forEach((button) => {
    button.addEventListener('click', () => {
      const section = button.closest('[data-collapsible]');
      const expanded = button.getAttribute('aria-expanded') === 'true';
      section.querySelectorAll('.exception-collapsed').forEach((el) => { el.hidden = expanded; });
      button.setAttribute('aria-expanded', expanded ? 'false' : 'true');
      button.textContent = expanded ? 'See more' : 'See less';
    });
  });
}

function wireExceptionActions(e) {
  document.getElementById('btn-assign').addEventListener('click', () => {
    e.status = 'Assigned';
    showToast('Assigned', `${e.id} assigned to the ${domainLabel(e.domain)} platform team.`, 'success');
    renderExceptionsTable();
  });
}

/* -----------------------------------------------------------------------
   5g. SETTINGS TAB (Latency Spike Monkey thresholds)
   ----------------------------------------------------------------------- */
function renderSettingsTab() {
  const tbody = document.getElementById('latency-settings-tbody');
  if (!tbody) return;

  tbody.innerHTML = state.latencySettings.map((s) => `
    <tr data-domain="${escapeHtml(s.domain)}">
      <td>${escapeHtml(domainLabel(s.domain))}</td>
      <td><input type="number" min="1" class="settings-input" data-field="spikeThresholdMs" value="${s.overrideSpikeThresholdMs ?? ''}" placeholder="${s.effectiveSpikeThresholdMs}"></td>
      <td><input type="number" min="1" class="settings-input" data-field="spikeOccurrenceThreshold" value="${s.overrideSpikeOccurrenceThreshold ?? ''}" placeholder="${s.effectiveSpikeOccurrenceThreshold}"></td>
      <td><input type="number" min="1" class="settings-input" data-field="spikeWindowMinutes" value="${s.overrideSpikeWindowMinutes ?? ''}" placeholder="${s.effectiveSpikeWindowMinutes}"></td>
      <td>${s.updatedAt ? `${formatRelativeTime(s.updatedAt)} by ${escapeHtml(s.updatedBy || 'unknown')}` : '—'}</td>
      <td><button class="btn btn--icon btn--sm" data-action="save-latency-setting">Save</button></td>
    </tr>`).join('');

  tbody.querySelectorAll('tr[data-domain]').forEach((row) => {
    row.querySelector('button[data-action="save-latency-setting"]').addEventListener('click', () => saveLatencySettingRow(row));
  });

  const auditTbody = document.getElementById('latency-settings-audit-tbody');
  if (!auditTbody) return;
  auditTbody.innerHTML = state.latencySettingsAudit.length === 0
    ? '<tr><td colspan="6"><div class="state-block">No changes recorded yet.</div></td></tr>'
    : state.latencySettingsAudit.map((a) => `
      <tr>
        <td>${escapeHtml(domainLabel(a.domain))}</td>
        <td>${a.spikeThresholdMs ?? '—'}</td>
        <td>${a.spikeOccurrenceThreshold ?? '—'}</td>
        <td>${a.spikeWindowMinutes ?? '—'}</td>
        <td>${formatRelativeTime(a.changedAt)}</td>
        <td>${escapeHtml(a.changedBy || 'unknown')}</td>
      </tr>`).join('');

  renderLlmClientSettings();
  renderSloDefinitions();
  renderNotificationsSettings();
}

const LLM_PROVIDER_LABELS = { 'github-copilot': 'GitHub Copilot', foundry: 'Microsoft Foundry' };

function llmProviderLabel(id) {
  return LLM_PROVIDER_LABELS[id] || id || 'unknown';
}

/** A field the user has touched is never overwritten by the 90s silent auto-refresh (or by
 * renderSettingsTab running after an unrelated save) - losing a half-typed endpoint or a provider
 * the user just picked is the same class of bug the model <select> already had. */
function llmSyncField(id, value) {
  const el = document.getElementById(id);
  if (!el || el.dataset.dirty === '1') return;
  el.value = value ?? '';
}

function llmClearDirty() {
  document.querySelectorAll('#llm-client-settings [data-dirty]').forEach((el) => { delete el.dataset.dirty; });
}

/** Renders the "LLM Client" section. Never touches the two credential inputs (they are write-only
 * by design) or any field the user has edited, so an in-progress paste survives a re-render. */
function renderLlmClientSettings() {
  const statusEl = document.getElementById('llm-client-status');
  if (!statusEl) return;

  const s = state.llmClientSetting;
  if (!s) {
    statusEl.innerHTML = '<span class="llm-client-settings__status-line llm-client-settings__status-line--warning">Could not reach the LLM client settings API.</span>';
  } else {
    const perAgent = [
      `Doctor: <strong>${escapeHtml(llmProviderLabel(s.effectiveDoctorProvider))}</strong>`,
      `Scout: <strong>${escapeHtml(llmProviderLabel(s.effectiveScoutProvider))}</strong>`,
      `Inspector: <strong>${escapeHtml(llmProviderLabel(s.effectiveInspectorProvider))}</strong>`,
    ].join(' · ');

    const credentials = [];
    if (s.hasToken) credentials.push(`Copilot token ${escapeHtml(s.maskedToken)} (model ${escapeHtml(s.effectiveModel)})`);
    if (s.foundryAuthMode === 'managed-identity') credentials.push('Foundry: managed identity, no stored secret');
    else if (s.hasFoundryApiKey) credentials.push(`Foundry key ${escapeHtml(s.maskedFoundryApiKey)}`);

    const changedBy = s.updatedAt ? ` Last changed ${formatRelativeTime(s.updatedAt)} by ${escapeHtml(s.updatedBy || 'unknown')}.` : '';
    statusEl.innerHTML =
      `<span class="llm-client-settings__status-line">${perAgent}</span>` +
      (credentials.length
        ? `<span class="llm-client-settings__status-line">${credentials.join(' · ')}.${changedBy}</span>`
        : `<span class="llm-client-settings__status-line llm-client-settings__status-line--warning">No credential configured for any provider.${changedBy}</span>`);
  }

  // Configuration that is saved but cannot serve a call, stated here rather than discovered later
  // as "the agent stopped answering" - a half-configured provider and a dead agent look identical.
  const warningsEl = document.getElementById('llm-client-warnings');
  if (warningsEl) {
    const warnings = s?.warnings || [];
    warningsEl.innerHTML = warnings.length === 0
      ? ''
      : `<div class="llm-client-settings__status-line llm-client-settings__status-line--warning">
           <ul class="simple-list">${warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join('')}</ul>
         </div>`;
  }

  if (s) {
    llmSyncField('llm-client-provider', s.provider);
    llmSyncField('llm-client-provider-doctor', s.doctorProvider || '');
    llmSyncField('llm-client-provider-scout', s.scoutProvider || '');
    llmSyncField('llm-client-provider-inspector', s.inspectorProvider || '');
    llmSyncField('llm-client-foundry-endpoint', s.foundryEndpoint || '');
    llmSyncField('llm-client-foundry-auth', s.foundryAuthMode || 'api-key');
    llmSyncField('llm-client-foundry-embedding', s.foundryEmbeddingDeployment || '');

    // Keep the saved value selectable before the matching "Fetch" has ever run.
    llmEnsureOption('llm-client-model', s.model);
    llmSyncField('llm-client-model', s.model || '');
    llmEnsureOption('llm-client-foundry-chat', s.foundryChatDeployment);
    llmSyncField('llm-client-foundry-chat', s.foundryChatDeployment || '');
  }

  const auditTbody = document.getElementById('llm-client-audit-tbody');
  if (!auditTbody) return;
  auditTbody.innerHTML = state.llmClientSettingAudit.length === 0
    ? '<tr><td colspan="6"><div class="state-block">No changes recorded yet.</div></td></tr>'
    : state.llmClientSettingAudit.map((a) => `
      <tr>
        <td>${a.tokenChanged ? 'Yes' : 'No'}</td>
        <td>${a.foundryApiKeyChanged ? 'Yes' : 'No'}</td>
        <td>${escapeHtml(a.provider ? llmProviderLabel(a.provider) : '—')}</td>
        <td>${escapeHtml(a.model || '—')}</td>
        <td>${formatRelativeTime(a.changedAt)}</td>
        <td>${escapeHtml(a.changedBy || 'unknown')}</td>
      </tr>`).join('');
}

function llmEnsureOption(selectId, value) {
  const select = document.getElementById(selectId);
  if (!select || !value) return;
  if (!Array.from(select.options).some((o) => o.value === value)) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = value;
    select.appendChild(opt);
  }
}

/** Wired once at startup (see init) - the form itself is never re-created by innerHTML, only its
 * status text/options are updated by renderLlmClientSettings, so listeners stay attached. */
function wireLlmClientSettings() {
  const card = document.getElementById('llm-client-settings');
  const saveBtn = document.getElementById('llm-client-save');
  if (!card || !saveBtn) return;

  // One delegated listener: every field inside the card marks itself dirty, so a re-render leaves
  // it alone until the next successful save.
  card.addEventListener('change', (e) => {
    const field = e.target.closest('select, input');
    if (field && field.type !== 'checkbox') field.dataset.dirty = '1';
  });
  card.addEventListener('input', (e) => {
    const field = e.target.closest('input[type="text"]');
    if (field) field.dataset.dirty = '1';
  });

  document.getElementById('llm-client-fetch-models')?.addEventListener('click', async (e) => {
    const token = document.getElementById('llm-client-token').value.trim();
    if (!token) {
      showToast('Token required', 'Paste a GitHub token above first, then fetch its available models.', 'critical');
      return;
    }
    await llmFetchInto(e.currentTarget, 'llm-client-model', 'Fetch available models', { provider: 'github-copilot', gitHubToken: token });
  });

  document.getElementById('llm-client-fetch-deployments')?.addEventListener('click', async (e) => {
    const endpoint = document.getElementById('llm-client-foundry-endpoint').value.trim();
    const authMode = document.getElementById('llm-client-foundry-auth').value;
    const apiKey = document.getElementById('llm-client-foundry-key').value.trim();

    if (!endpoint) {
      showToast('Endpoint required', 'Fill in the Foundry endpoint first, then fetch its deployments.', 'critical');
      return;
    }
    // The stored key is never sent back to the browser, so it cannot be reused for a probe - say
    // that instead of failing with an opaque 401 from the provider.
    if (authMode !== 'managed-identity' && !apiKey) {
      showToast('API key required', 'Paste the API key to probe this endpoint - the stored one is never returned to the browser.', 'critical');
      return;
    }

    await llmFetchInto(e.currentTarget, 'llm-client-foundry-chat', 'Fetch deployments', {
      provider: 'foundry',
      foundryEndpoint: endpoint,
      foundryAuthMode: authMode,
      foundryApiKey: apiKey || null,
    });
  });

  saveBtn.addEventListener('click', async () => {
    const tokenInput = document.getElementById('llm-client-token');
    const clearTokenBox = document.getElementById('llm-client-clear-token');
    const keyInput = document.getElementById('llm-client-foundry-key');
    const clearKeyBox = document.getElementById('llm-client-clear-foundry-key');

    const token = tokenInput.value.trim();
    const apiKey = keyInput.value.trim();

    if (token && clearTokenBox.checked) {
      showToast('Conflicting input', 'Cannot both paste a new GitHub token and remove the stored one - pick one.', 'critical');
      return;
    }
    if (apiKey && clearKeyBox.checked) {
      showToast('Conflicting input', 'Cannot both paste a new Foundry key and remove the stored one - pick one.', 'critical');
      return;
    }

    const value = (id) => document.getElementById(id).value;
    const payload = {
      gitHubToken: token === '' ? null : token,
      clearToken: clearTokenBox.checked,
      model: value('llm-client-model') === '' ? null : value('llm-client-model'),
      provider: value('llm-client-provider'),
      // Empty string is meaningful here: it clears an agent override back to "inherit".
      doctorProvider: value('llm-client-provider-doctor'),
      scoutProvider: value('llm-client-provider-scout'),
      inspectorProvider: value('llm-client-provider-inspector'),
      foundryEndpoint: value('llm-client-foundry-endpoint'),
      foundryAuthMode: value('llm-client-foundry-auth'),
      foundryApiKey: apiKey === '' ? null : apiKey,
      clearFoundryApiKey: clearKeyBox.checked,
      foundryChatDeployment: value('llm-client-foundry-chat'),
      foundryEmbeddingDeployment: value('llm-client-foundry-embedding'),
    };

    try {
      await dataProvider.saveLlmClientSetting(payload);
      showToast('Settings saved', 'LLM client updated.', 'success');
      tokenInput.value = '';
      keyInput.value = '';
      clearTokenBox.checked = false;
      clearKeyBox.checked = false;
      // What was just saved IS the persisted state now - safe to let renders sync to it again.
      llmClearDirty();
      state.llmClientSetting = await dataProvider.getLlmClientSettings();
      state.llmClientSettingAudit = await dataProvider.getLlmClientSettingsAudit();
      renderLlmClientSettings();
    } catch (err) {
      showToast('Save failed', err.message || 'Could not save the LLM client settings.', 'critical');
    }
  });
}

/** Shared by both "Fetch" buttons: probes a candidate credential and repopulates one picker. */
async function llmFetchInto(button, selectId, idleLabel, probe) {
  button.disabled = true;
  button.textContent = 'Fetching…';
  try {
    const models = await dataProvider.listLlmClientModels(probe);
    const select = document.getElementById(selectId);
    select.innerHTML = '<option value="">(keep current)</option>' +
      models.map((m) => `<option value="${escapeHtml(m)}">${escapeHtml(m)}</option>`).join('');
    showToast('Fetched', `${models.length} option(s) available for this credential.`, 'success');
  } catch (err) {
    showToast('Fetch failed', err.message || 'Could not list anything for this credential.', 'critical');
  } finally {
    button.disabled = false;
    button.textContent = idleLabel;
  }
}

/** Wired once at startup - switches between the Settings tab's sections (Latency Thresholds/LLM
 *  Client/SLO Definitions/Notifications) via the left sub-nav instead of one long scroll. */
function wireSettingsSubnav() {
  const nav = document.querySelector('.settings-subnav');
  if (!nav) return;
  const items = Array.from(nav.querySelectorAll('.settings-subnav__item'));
  const sections = Array.from(document.querySelectorAll('.settings-content .settings-section'));
  items.forEach((btn) => {
    btn.addEventListener('click', () => {
      const target = btn.dataset.settingsSection;
      items.forEach((b) => b.classList.toggle('is-active', b === btn));
      sections.forEach((section) => {
        const isTarget = section.dataset.settingsSection === target;
        section.classList.toggle('is-active', isTarget);
        section.hidden = !isTarget;
      });
      // Loaded on first view rather than at startup: both read the database, and most visits to
      // Settings never open them.
      if (target === 'kql') loadKqlQueries();
      if (target === 'sources') loadTelemetrySources();
      if (target === 'budgets') loadAgentBudgets();
      if (target === 'company') loadCompanyProfiles();
    });
  });
}

/** Renders the "SLO Definitions" table (Settings tab) - one row per (domain, stage, signalType). */
function renderSloDefinitions() {
  const tbody = document.getElementById('slo-definitions-tbody');
  if (!tbody) return;

  tbody.innerHTML = state.sloDefinitions.length === 0
    ? '<tr><td colspan="9"><div class="state-block">No SLO definitions configured yet - use the form above to add one.</div></td></tr>'
    : state.sloDefinitions.map((d) => `
      <tr data-domain="${escapeHtml(d.domain)}" data-stage="${escapeHtml(d.stage || '')}" data-signal-type="${escapeHtml(d.signalType)}">
        <td>${escapeHtml(domainLabel(d.domain))}</td>
        <td>${escapeHtml(d.stage || '(domain-level)')}</td>
        <td>${d.signalType === 'latency' ? 'Latency' : 'Exception rate'}</td>
        <td><input type="number" min="1" class="settings-input" data-field="targetThresholdMs" value="${d.targetThresholdMs ?? ''}" ${d.signalType !== 'latency' ? 'disabled' : ''}></td>
        <td><input type="number" min="0.01" max="99.99" step="0.01" class="settings-input" data-field="targetPercentage" value="${d.targetPercentage}"></td>
        <td><input type="number" min="28" max="31" class="settings-input" data-field="windowDays" value="${d.windowDays}"></td>
        <td><input type="checkbox" data-field="enabled" ${d.enabled ? 'checked' : ''}></td>
        <td>${d.updatedAt ? `${formatRelativeTime(d.updatedAt)} by ${escapeHtml(d.updatedBy || 'unknown')}` : '—'}</td>
        <td><button class="btn btn--icon btn--sm" data-action="save-slo-definition">Save</button></td>
      </tr>`).join('');

  tbody.querySelectorAll('tr[data-domain]').forEach((row) => {
    row.querySelector('button[data-action="save-slo-definition"]').addEventListener('click', () => saveSloDefinitionRow(row));
  });

  renderSloDefinitionsAudit();
}

/** Renders the SLO Definitions "Change History" table (Settings tab) - mirrors renderNotificationsSettings' audit table below. */
function renderSloDefinitionsAudit() {
  const auditTbody = document.getElementById('slo-definitions-audit-tbody');
  if (!auditTbody) return;
  auditTbody.innerHTML = state.sloDefinitionsAudit.length === 0
    ? '<tr><td colspan="9"><div class="state-block">No changes recorded yet.</div></td></tr>'
    : state.sloDefinitionsAudit.map((a) => `
      <tr>
        <td>${escapeHtml(domainLabel(a.domain))}</td>
        <td>${escapeHtml(a.stage || '(domain-level)')}</td>
        <td>${a.signalType === 'latency' ? 'Latency' : 'Exception rate'}</td>
        <td>${a.targetThresholdMs ?? '—'}</td>
        <td>${a.targetPercentage}</td>
        <td>${a.windowDays}</td>
        <td>${a.enabled ? 'On' : 'Off'}</td>
        <td>${formatRelativeTime(a.changedAt)}</td>
        <td>${escapeHtml(a.changedBy || 'unknown')}</td>
      </tr>`).join('');
}

async function saveSloDefinitionRow(row) {
  const payload = {
    domain: row.dataset.domain,
    stage: row.dataset.stage || null,
    signalType: row.dataset.signalType,
    targetThresholdMs: row.dataset.signalType === 'latency' ? Number(row.querySelector('input[data-field="targetThresholdMs"]').value) || null : null,
    targetPercentage: Number(row.querySelector('input[data-field="targetPercentage"]').value),
    windowDays: Number(row.querySelector('input[data-field="windowDays"]').value),
    enabled: row.querySelector('input[data-field="enabled"]').checked,
  };
  try {
    await dataProvider.saveSloDefinition(payload);
    showToast('SLO saved', `${domainLabel(payload.domain)}${payload.stage ? ' / ' + payload.stage : ''} SLO definition updated.`, 'success');
    state.sloDefinitions = await dataProvider.getSloDefinitions();
    state.sloDefinitionsAudit = await dataProvider.getSloDefinitionsAudit();
    renderSloDefinitions();
  } catch (err) {
    showToast('Save failed', err.message || 'Could not save this SLO definition.', 'critical');
  }
}

/** Wired once at startup - the "add new" form for SLO Definitions. */
function wireSloDefinitionAdd() {
  const helpToggle = document.getElementById('slo-help-toggle');
  const helpBody = document.getElementById('slo-help-body');
  if (helpToggle && helpBody) {
    helpToggle.addEventListener('click', () => {
      const expanded = helpToggle.getAttribute('aria-expanded') === 'true';
      helpToggle.setAttribute('aria-expanded', String(!expanded));
      helpBody.hidden = expanded;
    });
  }

  const saveBtn = document.getElementById('slo-add-save');
  if (!saveBtn) return;

  saveBtn.addEventListener('click', async () => {
    const domain = document.getElementById('slo-add-domain').value;
    const signalType = document.getElementById('slo-add-signal-type').value;
    const stage = document.getElementById('slo-add-stage').value.trim();
    const targetThresholdMs = Number(document.getElementById('slo-add-threshold').value) || null;
    const targetPercentage = Number(document.getElementById('slo-add-percentage').value);
    const windowDays = Number(document.getElementById('slo-add-window').value);

    if (signalType === 'latency' && !stage) {
      showToast('Stage required', 'Enter a stage name for a latency SLO definition.', 'critical');
      return;
    }
    if (signalType === 'exception_rate' && stage) {
      showToast('Stage must be blank', 'The exception_rate signal is domain-level - clear the Stage field.', 'critical');
      return;
    }
    if (signalType === 'latency' && !targetThresholdMs) {
      showToast('Threshold required', 'Enter a target threshold (ms) for a latency SLO definition.', 'critical');
      return;
    }

    try {
      await dataProvider.saveSloDefinition({ domain, stage: stage || null, signalType, targetThresholdMs, targetPercentage, windowDays, enabled: true });
      showToast('SLO saved', `${domainLabel(domain)}${stage ? ' / ' + stage : ''} SLO definition added.`, 'success');
      document.getElementById('slo-add-stage').value = '';
      document.getElementById('slo-add-threshold').value = '';
      state.sloDefinitions = await dataProvider.getSloDefinitions();
      state.sloDefinitionsAudit = await dataProvider.getSloDefinitionsAudit();
      renderSloDefinitions();
    } catch (err) {
      showToast('Save failed', err.message || 'Could not add this SLO definition.', 'critical');
    }
  });
}

/** Renders the "Notifications" section status text + audit history (Settings tab). Never touches
 * the secret input values themselves (password/connection-string/API-key/webhook fields) so an
 * in-progress paste survives a silent re-render, same convention as renderLlmClientSettings. */
function renderNotificationsSettings() {
  const statusEl = document.getElementById('notifications-status');
  if (!statusEl) return;

  const s = state.alertChannelSetting;
  const lines = [];
  if (!s) {
    statusEl.innerHTML = '<span class="llm-client-settings__status-line llm-client-settings__status-line--warning">Could not reach the notifications settings API.</span>';
  } else {
    if (s.emailEnabled) {
      const providers = [s.smtpEnabled && 'SMTP', s.acsEnabled && 'Azure Communication Services', s.sendGridEnabled && 'SendGrid'].filter(Boolean);
      lines.push(providers.length
        ? `Email enabled via ${providers.join(', ')}.`
        : 'Email enabled but no provider is configured - no email will be sent.');
    } else {
      lines.push('Email notifications disabled.');
    }
    lines.push(s.teamsEnabled
      ? (s.teamsHasWebhook ? 'Teams notification enabled.' : 'Teams notification enabled but no webhook URL is configured.')
      : 'Teams notification disabled.');
    const changedBy = s.updatedAt ? ` Last changed ${formatRelativeTime(s.updatedAt)} by ${escapeHtml(s.updatedBy || 'unknown')}.` : '';
    statusEl.innerHTML = `<span class="llm-client-settings__status-line">${lines.join(' ')}${changedBy}</span>`;

    document.getElementById('notif-email-enabled').checked = !!s.emailEnabled;
    document.getElementById('notif-email-recipients').value = s.emailRecipientsCsv || '';
    document.getElementById('notif-smtp-enabled').checked = !!s.smtpEnabled;
    document.getElementById('notif-smtp-host').value = s.smtpHost || '';
    document.getElementById('notif-smtp-port').value = s.smtpPort || '';
    document.getElementById('notif-smtp-ssl').checked = s.smtpUseSsl !== false;
    document.getElementById('notif-smtp-username').value = s.smtpUsername || '';
    document.getElementById('notif-smtp-from').value = s.smtpFromAddress || '';
    document.getElementById('notif-acs-enabled').checked = !!s.acsEnabled;
    document.getElementById('notif-acs-from').value = s.acsFromAddress || '';
    document.getElementById('notif-sendgrid-enabled').checked = !!s.sendGridEnabled;
    document.getElementById('notif-sendgrid-from').value = s.sendGridFromAddress || '';
    document.getElementById('notif-teams-enabled').checked = !!s.teamsEnabled;
  }

  const auditTbody = document.getElementById('notifications-audit-tbody');
  if (!auditTbody) return;
  auditTbody.innerHTML = state.alertChannelSettingAudit.length === 0
    ? '<tr><td colspan="7"><div class="state-block">No changes recorded yet.</div></td></tr>'
    : state.alertChannelSettingAudit.map((a) => `
      <tr>
        <td>${a.emailEnabled ? 'On' : 'Off'}</td>
        <td>${a.smtpEnabled ? 'On' : 'Off'}${a.smtpPasswordChanged ? ' (password changed)' : ''}</td>
        <td>${a.acsEnabled ? 'On' : 'Off'}${a.acsConnectionStringChanged ? ' (connection string changed)' : ''}</td>
        <td>${a.sendGridEnabled ? 'On' : 'Off'}${a.sendGridApiKeyChanged ? ' (API key changed)' : ''}</td>
        <td>${a.teamsEnabled ? 'On' : 'Off'}${a.teamsWebhookChanged ? ' (webhook changed)' : ''}</td>
        <td>${formatRelativeTime(a.changedAt)}</td>
        <td>${escapeHtml(a.changedBy || 'unknown')}</td>
      </tr>`).join('');
}

/** Wired once at startup - the Notifications section form is never re-created by innerHTML. */
function wireNotificationsSettings() {
  const saveBtn = document.getElementById('notifications-save');
  if (!saveBtn) return;

  saveBtn.addEventListener('click', async () => {
    const smtpPassword = document.getElementById('notif-smtp-password').value;
    const acsConnectionString = document.getElementById('notif-acs-connection-string').value;
    const sendGridApiKey = document.getElementById('notif-sendgrid-api-key').value;
    const teamsWebhookUrl = document.getElementById('notif-teams-webhook').value;

    const payload = {
      emailEnabled: document.getElementById('notif-email-enabled').checked,
      emailRecipientsCsv: document.getElementById('notif-email-recipients').value.trim() || null,
      smtpEnabled: document.getElementById('notif-smtp-enabled').checked,
      smtpHost: document.getElementById('notif-smtp-host').value.trim() || null,
      smtpPort: Number(document.getElementById('notif-smtp-port').value) || null,
      smtpUsername: document.getElementById('notif-smtp-username').value.trim() || null,
      smtpPassword: smtpPassword === '' ? null : smtpPassword,
      clearSmtpPassword: document.getElementById('notif-smtp-clear-password').checked,
      smtpUseSsl: document.getElementById('notif-smtp-ssl').checked,
      smtpFromAddress: document.getElementById('notif-smtp-from').value.trim() || null,
      acsEnabled: document.getElementById('notif-acs-enabled').checked,
      acsConnectionString: acsConnectionString === '' ? null : acsConnectionString,
      clearAcsConnectionString: document.getElementById('notif-acs-clear-connection-string').checked,
      acsFromAddress: document.getElementById('notif-acs-from').value.trim() || null,
      sendGridEnabled: document.getElementById('notif-sendgrid-enabled').checked,
      sendGridApiKey: sendGridApiKey === '' ? null : sendGridApiKey,
      clearSendGridApiKey: document.getElementById('notif-sendgrid-clear-api-key').checked,
      sendGridFromAddress: document.getElementById('notif-sendgrid-from').value.trim() || null,
      teamsEnabled: document.getElementById('notif-teams-enabled').checked,
      teamsWebhookUrl: teamsWebhookUrl === '' ? null : teamsWebhookUrl,
      clearTeamsWebhookUrl: document.getElementById('notif-teams-clear-webhook').checked,
    };

    try {
      await dataProvider.saveAlertChannelSettings(payload);
      showToast('Settings saved', 'Notification channels updated.', 'success');
      document.getElementById('notif-smtp-password').value = '';
      document.getElementById('notif-acs-connection-string').value = '';
      document.getElementById('notif-sendgrid-api-key').value = '';
      document.getElementById('notif-teams-webhook').value = '';
      document.getElementById('notif-smtp-clear-password').checked = false;
      document.getElementById('notif-acs-clear-connection-string').checked = false;
      document.getElementById('notif-sendgrid-clear-api-key').checked = false;
      document.getElementById('notif-teams-clear-webhook').checked = false;
      state.alertChannelSetting = await dataProvider.getAlertChannelSettings();
      state.alertChannelSettingAudit = await dataProvider.getAlertChannelSettingsAudit();
      renderNotificationsSettings();
    } catch (err) {
      showToast('Save failed', err.message || 'Could not save the notification settings.', 'critical');
    }
  });
}

async function saveLatencySettingRow(row) {
  const domain = row.dataset.domain;
  const readField = (field) => {
    const raw = row.querySelector(`input[data-field="${field}"]`).value.trim();
    return raw === '' ? null : Number(raw);
  };

  const payload = {
    spikeThresholdMs: readField('spikeThresholdMs'),
    spikeOccurrenceThreshold: readField('spikeOccurrenceThreshold'),
    spikeWindowMinutes: readField('spikeWindowMinutes'),
  };

  if (Object.values(payload).some((v) => v !== null && (!Number.isFinite(v) || v <= 0))) {
    showToast('Invalid value', 'Thresholds must be positive numbers, or blank to use the default.', 'critical');
    return;
  }

  try {
    await dataProvider.saveLatencySetting(domain, payload);
    showToast('Settings saved', `${domainLabel(domain)} latency thresholds updated.`, 'success');
    state.latencySettings = await dataProvider.getLatencySettings();
    state.latencySettingsAudit = await dataProvider.getLatencySettingsAudit();
    renderSettingsTab();
  } catch (err) {
    showToast('Save failed', err.message || 'Could not save this setting.', 'critical');
  }
}

/* -----------------------------------------------------------------------
   6. DEMO SCENARIO ORCHESTRATION
   ----------------------------------------------------------------------- */
/**
 * Runs a fixed, deterministic narrative end-to-end so the audience can see
 * agents collaborate on a real incident lifecycle: detection → correlation
 * → recommendation → approval → recovery → verification.
 */
async function runDemoScenario() {
  if (state.demoRunning) return;
  state.demoRunning = true;
  const btn = document.getElementById('run-demo-scenario');
  btn.disabled = true;
  const steps = [
    async () => {
      const air = state.components.find((c) => c.id === 'inventory-air');
      air.status = 'critical'; air.latencyMs = 910; air.errorRate = 12.4; air.activeAlerts += 1;
      showToast('Health Monkey', 'Air Inventory latency spiked to 910ms — flagged as critical.', 'critical');
      renderAllTabs();
    },
    async () => {
      state.events.unshift({ id: `EVT-DEMO-1`, title: 'Health Monkey detected severe degradation', description: 'Air Inventory p95 latency exceeded critical threshold (910ms).', severity: 'critical', domain: 'air', timestamp: new Date().toISOString() });
      showToast('Health Monkey', 'Finding raised and shared with Dependency Monkey.', 'warning');
      renderOverviewTab();
    },
    async () => {
      const dm = state.monkeys.find((m) => m.id === 'dependency-monkey');
      dm.activity.push({ time: new Date().toISOString(), stage: 'decision', text: 'Recommend routing 100% of Air Inventory traffic to AeroConnect API.' });
      showToast('Dependency Monkey', 'Recommending full traffic re-route to AeroConnect API.', 'info');
      renderMonkeysTab();
    },
    async () => {
      const dm = state.monkeys.find((m) => m.id === 'dependency-monkey');
      dm.activity.push({ time: new Date().toISOString(), stage: 'approval', text: 'Re-route approved by on-call SRE via portal.' });
      dm.actionsExecuted += 1;
      showToast('Approval', 'Traffic re-route approved (simulated human-in-the-loop).', 'success');
      renderMonkeysTab();
    },
    async () => {
      const air = state.components.find((c) => c.id === 'inventory-air');
      air.status = 'healthy'; air.latencyMs = 145; air.errorRate = 0.6; air.activeAlerts = Math.max(0, air.activeAlerts - 1);
      state.events.unshift({ id: `EVT-DEMO-2`, title: 'Booking success rate returned to normal', description: 'Air Inventory latency recovered to 145ms after traffic re-route.', severity: 'success', domain: 'air', timestamp: new Date().toISOString() });
      showToast('Dependency Monkey', 'Recovery verified — error rate and latency back to baseline.', 'success');
      renderAllTabs();
    },
  ];

  for (const step of steps) {
    await step();
    await sleep(1600);
  }

  btn.disabled = false;
  state.demoRunning = false;
  showToast('Demo Scenario', 'Scenario complete. Explore Monkeys and Overview to review the full timeline.', 'success');
}

function sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

/* -----------------------------------------------------------------------
   7. BOOTSTRAP
   ----------------------------------------------------------------------- */
function renderAllTabs() {
  renderOverviewTab();
  renderJourneysTable();
  renderHealthTab();
  renderAnalyticsTab();
  renderMonkeysTab();
  renderExceptionsTable();
  renderSettingsTab();
  updateGlobalHealthIndicator();
}

function updateGlobalHealthIndicator() {
  const worst = state.overview?.platformStatus || 'unknown';
  const status = worst === 'critical' || worst === 'degraded' ? worst : 'healthy';
  const el = document.getElementById('global-health');
  const label = document.getElementById('global-health-label');
  el.className = `health-pill health-pill--${status}`;
  const dot = el.querySelector('.status-dot');
  dot.classList.toggle('status-dot--pulse', status !== 'healthy');
  label.textContent = status === 'healthy' ? 'Platform Healthy' : status === 'degraded' ? 'Platform Degraded' : 'Platform Critical';
}

function updateLastRefreshTimestamp() {
  document.getElementById('last-refresh').textContent = formatClock(new Date());
}

async function loadAllData() {
  const [journeys, traces, components, monkeys, exceptions, overview, demoJourneysById, events, analytics, dashboardMetrics, latencySettings, latencySettingsAudit, llmClientSetting, llmClientSettingAudit, sloDefinitions, sloDefinitionsAudit, alertChannelSetting, alertChannelSettingAudit] = await Promise.all([
    dataProvider.getJourneys(), dataProvider.getTraces(), dataProvider.getComponents(),
    dataProvider.getMonkeys(), dataProvider.getExceptions(), dataProvider.getOverview(), dataProvider.getDemoJourneysById(), dataProvider.getEvents(), dataProvider.getAnalytics(),
    dataProvider.getDashboardMetrics(), dataProvider.getLatencySettings(), dataProvider.getLatencySettingsAudit(),
    dataProvider.getLlmClientSettings(), dataProvider.getLlmClientSettingsAudit(),
    dataProvider.getSloDefinitions(), dataProvider.getSloDefinitionsAudit(),
    dataProvider.getAlertChannelSettings(), dataProvider.getAlertChannelSettingsAudit(),
  ]);
  state.journeys = journeys; state.traces = traces; state.components = components;
  state.monkeys = monkeys; state.exceptions = exceptions; state.overview = overview; state.demoJourneysById = demoJourneysById; state.events = events; state.analytics = analytics;
  state.dashboardMetrics = dashboardMetrics;
  state.latencySettings = latencySettings; state.latencySettingsAudit = latencySettingsAudit;
  state.llmClientSetting = llmClientSetting; state.llmClientSettingAudit = llmClientSettingAudit;
  state.sloDefinitions = sloDefinitions; state.sloDefinitionsAudit = sloDefinitionsAudit;
  state.alertChannelSetting = alertChannelSetting; state.alertChannelSettingAudit = alertChannelSettingAudit;
  state.domainComparisonStale = true;
  ensureDomainComparison();
}

function exceptionsSourceLabel() {
  const m = state.exceptionsMeta || {};
  if (m.source === 'sql') {
    return `SQL incident store (${m.rowCount} incidents, ${m.win || headerQueryWin()})`;
  }
  return m.warning || 'unavailable';
}

function journeysSourceLabel(meta) {
  const m = meta || {};
  const win = m.win || headerQueryWin();
  return {
    live: `${m.environment || 'production'} App Insights — live (${m.rowCount} journeys, ${win})`,
    mixed: `App Insights — partial live results (${m.rowCount} journeys, ${win})`,
    sql: `SQL snapshot after Azure pull (${m.rowCount} journeys, ${win})`,
    stored: `stored snapshot (${m.rowCount} journeys, ${win})`,
    snapshot: `exported PROD snapshot (${m.rowCount} users)`,
    demo: `anonymized demo data (${m.rowCount} users)`,
  }[m.source] || `unknown source (${m.rowCount} users)`;
}

function setRefreshBusy(busy) {
  const btn = document.getElementById('refresh-btn');
  if (!btn) return;
  btn.disabled = busy;
  btn.setAttribute('aria-busy', busy ? 'true' : 'false');
}

async function refreshData({ silent } = { silent: false }) {
  if (!silent) {
    setRefreshBusy(true);
    state.dashboardChartsLoading = true;
    paintDashboardChartsLoading();
  }
  try {
    let pullFailures = [];
    if (!silent) {
      pullFailures = await pullLatestJourneysFromAzure();
      if (pullFailures.length) {
        console.warn('RefreshJourneys failed for some domains', pullFailures);
      }
    }
    await loadAllData();
    state.dashboardChartsLoading = false;
    renderAllTabs();
    updateLastRefreshTimestamp();
    if (!silent) {
      const m = state.journeysMeta || {};
      const extra = m.cached ? ' Cached ≤90s.' : '';
      const pullWarn = pullFailures
        .map((err) => `${domainLabel(err.domain) || 'Journeys'}: ${err.message || err}`)
        .join(' ');
      const warn = [m.warning, pullWarn].filter(Boolean).join(' ');
      const kind = pullFailures.length === 0 && (m.source === 'sql' || m.source === 'live' || m.source === 'stored')
        ? 'success'
        : pullFailures.length && !m.rowCount ? 'critical' : 'info';
      showToast(
        pullFailures.length ? 'Refresh incomplete' : 'Refreshed',
        `Journeys: ${journeysSourceLabel(m)}.${extra}${warn ? ` ${warn}` : ''} Exceptions: ${exceptionsSourceLabel()}.`,
        kind,
      );
    }
  } catch (err) {
    console.error(err);
    state.dashboardChartsLoading = false;
    if (!silent) showToast('Refresh failed', 'Could not load one or more data sources.', 'critical');
    renderAnalyticsTab();
  } finally {
    if (!silent) setRefreshBusy(false);
  }
}

// ============================================================================
// Settings — KQL Queries and Telemetry Sources
// ============================================================================

/** Shared by both sections: a write that reports the server's own message rather than "failed". */
async function settingsWrite(url, method, payload) {
  const response = await fetch(url, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
    body: JSON.stringify(payload || {}),
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
  return body;
}

function setStatus(id, message, tone) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = message || '';
  el.className = `llm-client-settings__status${tone ? ` llm-client-settings__status--${tone}` : ''}`;
}

async function loadKqlQueries() {
  const tbody = document.getElementById('kql-queries-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/settings/kql');
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const body = await response.json();
    renderKqlQueries(body.queries || []);
  } catch (err) {
    console.warn('/api/settings/kql unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="7"><div class="state-block state-block--error">The query catalog could not be read.</div></td></tr>';
  }
  loadKqlAudit();
}

function renderKqlQueries(queries) {
  const tbody = document.getElementById('kql-queries-tbody');
  if (!tbody) return;

  tbody.innerHTML = queries.length === 0
    ? '<tr><td colspan="7"><div class="state-block">No queries registered.</div></td></tr>'
    : queries.map((q) => `
      <tr data-key="${escapeHtml(q.queryKey)}">
        <td><code>${escapeHtml(q.queryKey)}</code></td>
        <td>${escapeHtml(q.usedBy || '—')}</td>
        <td>${(q.sourceAliases || []).length === 0
            ? '<span class="kpi-card__value--empty">none</span>'
            : q.sourceAliases.map((a) => `<code>${escapeHtml(a)}</code>`).join(' ')}</td>
        <td>${q.hasOverride
            ? '<span class="status-badge status-badge--sev-medium">Edited</span>'
            : '<span class="status-badge">Built-in</span>'}</td>
        <td>${q.hasOverride ? `v${q.version}` : '—'}</td>
        <td>${q.updatedAt ? `${formatRelativeTime(q.updatedAt)} by ${escapeHtml(q.updatedBy || 'unknown')}` : '—'}</td>
        <td><button class="btn btn--icon btn--sm" data-action="edit-kql">Edit</button></td>
      </tr>`).join('');

  tbody.querySelectorAll('button[data-action="edit-kql"]').forEach((btn) => {
    btn.addEventListener('click', () => openKqlEditor(btn.closest('tr').dataset.key));
  });
}

async function openKqlEditor(key) {
  const card = document.getElementById('kql-editor');
  if (!card) return;
  try {
    const response = await fetch(`/api/settings/kql/${encodeURIComponent(key)}`);
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const detail = await response.json();

    card.hidden = false;
    card.dataset.key = key;
    document.getElementById('kql-editor-title').textContent = `Edit ${key}`;
    document.getElementById('kql-editor-text').value = detail.kqlText || '';
    document.getElementById('kql-editor-description').value = '';

    // Spelling out what must survive an edit: these are substituted by the caller, and a query
    // that loses one still parses - it just silently covers the wrong window or the wrong club.
    const required = (detail.requiredPlaceholders || []).map((p) => `<code>${escapeHtml(p)}</code>`).join(' ');
    const aliases = (detail.sourceAliases || []).map((a) => `<code>${escapeHtml(a)}</code>`).join(' ');
    document.getElementById('kql-editor-hint').innerHTML = [
      detail.usedBy ? `<strong>Run by:</strong> ${escapeHtml(detail.usedBy)}` : '',
      detail.hasOverride ? `Currently overridden (v${detail.version}).` : 'Currently running the built-in text.',
      required ? `Must keep: ${required}` : '',
      aliases ? `Telemetry sources used: ${aliases} — change the subscription under <em>Telemetry Sources</em>, not here.` : '',
    ].filter(Boolean).join(' · ');

    setStatus('kql-editor-status', '', null);
    card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  } catch (err) {
    showToast('KQL', err.message, 'error');
  }
}

function wireKqlSettings() {
  const card = document.getElementById('kql-editor');
  if (!card) return;

  document.getElementById('kql-editor-close').addEventListener('click', () => { card.hidden = true; });

  document.getElementById('kql-editor-save').addEventListener('click', async () => {
    const key = card.dataset.key;
    setStatus('kql-editor-status', 'Saving…', null);
    try {
      const saved = await settingsWrite(`/api/settings/kql/${encodeURIComponent(key)}`, 'PUT', {
        kqlText: document.getElementById('kql-editor-text').value,
        description: document.getElementById('kql-editor-description').value,
        changedBy: document.getElementById('kql-editor-changed-by').value,
        enabled: true,
      });
      setStatus('kql-editor-status', `Saved as v${saved.version}.`, 'ok');
      showToast('KQL', `${key} saved as v${saved.version}.`, 'success');
      loadKqlQueries();
    } catch (err) {
      setStatus('kql-editor-status', err.message, 'error');
    }
  });

  document.getElementById('kql-editor-revert').addEventListener('click', async () => {
    const key = card.dataset.key;
    setStatus('kql-editor-status', 'Reverting…', null);
    try {
      await settingsWrite(`/api/settings/kql/${encodeURIComponent(key)}/revert`, 'POST', {
        changedBy: document.getElementById('kql-editor-changed-by').value,
      });
      setStatus('kql-editor-status', 'Reverted to the built-in text.', 'ok');
      showToast('KQL', `${key} reverted to built-in.`, 'success');
      card.hidden = true;
      loadKqlQueries();
    } catch (err) {
      setStatus('kql-editor-status', err.message, 'error');
    }
  });

  document.getElementById('kql-import-builtins').addEventListener('click', async () => {
    setStatus('kql-status', 'Importing…', null);
    try {
      const result = await settingsWrite('/api/settings/kql/import-builtins', 'POST', { overwrite: false });
      setStatus('kql-status', `Imported ${result.imported}, skipped ${result.skipped} already present.`, 'ok');
      loadKqlQueries();
    } catch (err) {
      setStatus('kql-status', err.message, 'error');
    }
  });
}

async function loadKqlAudit() {
  const tbody = document.getElementById('kql-audit-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/settings/kql-audit');
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const body = await response.json();
    const entries = body.entries || [];
    tbody.innerHTML = entries.length === 0
      ? '<tr><td colspan="7"><div class="state-block">No query has been edited yet.</div></td></tr>'
      : entries.map((e) => `
        <tr data-audit-id="${e.auditId}">
          <td><code>${escapeHtml(e.queryKey)}</code></td>
          <td>${e.removed ? '—' : `v${e.version}`}</td>
          <td>${e.removed ? '<em>reverted to built-in</em>' : escapeHtml(e.description || '—')}</td>
          <td>${e.removed ? '—' : `${Number(e.textLength || 0).toLocaleString()} chars`}</td>
          <td>${formatRelativeTime(e.changedAt)}</td>
          <td>${escapeHtml(e.changedBy || '—')}</td>
          <td>${e.removed ? '' : '<button class="btn btn--icon btn--sm" data-action="restore-kql">Restore</button>'}</td>
        </tr>`).join('');

    tbody.querySelectorAll('button[data-action="restore-kql"]').forEach((btn) => {
      btn.addEventListener('click', async () => {
        const auditId = btn.closest('tr').dataset.auditId;
        try {
          const saved = await settingsWrite(`/api/settings/kql-audit/${auditId}/restore`, 'POST', {
            changedBy: document.getElementById('kql-editor-changed-by').value,
          });
          showToast('KQL', `Restored as v${saved.version}.`, 'success');
          loadKqlQueries();
        } catch (err) {
          showToast('KQL', err.message, 'error');
        }
      });
    });
  } catch (err) {
    console.warn('/api/settings/kql-audit unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="7"><div class="state-block state-block--error">History could not be read.</div></td></tr>';
  }
}

async function loadTelemetrySources() {
  const tbody = document.getElementById('sources-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/settings/telemetry-sources');
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const body = await response.json();
    const sources = body.sources || [];

    // "Which agents break if I change this row" is the question somebody editing a resource id is
    // actually asking, so it is answered here rather than left to be worked out from the queries.
    const usage = await loadSourceUsage();

    tbody.innerHTML = sources.length === 0
      ? '<tr><td colspan="7"><div class="state-block">No telemetry sources registered.</div></td></tr>'
      : sources.map((s) => `
        <tr data-alias="${escapeHtml(s.alias)}" data-environment="${escapeHtml(s.environment)}">
          <td><code>${escapeHtml(s.alias)}</code></td>
          <td>${escapeHtml(s.environment)}</td>
          <td title="${escapeHtml(s.resourceId)}"><code>${escapeHtml(shortResourceId(s.resourceId))}</code></td>
          <td>${(usage[s.alias.toLowerCase()] || []).length === 0
              ? '<span class="kpi-card__value--empty">no query uses it</span>'
              : escapeHtml((usage[s.alias.toLowerCase()] || []).join(', '))}</td>
          <td>${s.enabled ? 'Yes' : 'No'}</td>
          <td>${s.updatedAt ? `${formatRelativeTime(s.updatedAt)} by ${escapeHtml(s.updatedBy || 'unknown')}` : '—'}</td>
          <td>
            <button class="btn btn--icon btn--sm" data-action="edit-source">Edit</button>
            <button class="btn btn--icon btn--sm" data-action="delete-source">Delete</button>
          </td>
        </tr>`).join('');

    tbody.querySelectorAll('button[data-action="edit-source"]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const row = btn.closest('tr');
        const s = sources.find((x) => x.alias === row.dataset.alias && x.environment === row.dataset.environment);
        if (!s) return;
        document.getElementById('source-alias').value = s.alias;
        document.getElementById('source-environment').value = s.environment;
        document.getElementById('source-resource-id').value = s.resourceId;
        document.getElementById('source-app-id').value = s.appId || '';
        document.getElementById('source-tenant-id').value = s.tenantId || '';
        document.getElementById('source-notes').value = s.notes || '';
        document.getElementById('source-enabled').checked = !!s.enabled;
        document.getElementById('source-add').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      });
    });

    tbody.querySelectorAll('button[data-action="delete-source"]').forEach((btn) => {
      btn.addEventListener('click', async () => {
        const row = btn.closest('tr');
        try {
          await settingsWrite(
            `/api/settings/telemetry-sources/${encodeURIComponent(row.dataset.alias)}/${encodeURIComponent(row.dataset.environment)}/delete`,
            'POST',
            { changedBy: document.getElementById('source-changed-by').value });
          showToast('Telemetry sources', `${row.dataset.alias} (${row.dataset.environment}) removed.`, 'success');
          loadTelemetrySources();
        } catch (err) {
          showToast('Telemetry sources', err.message, 'error');
        }
      });
    });
  } catch (err) {
    console.warn('/api/settings/telemetry-sources unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="7"><div class="state-block state-block--error">The source catalog could not be read.</div></td></tr>';
  }
  loadTelemetrySourcesAudit();
}

/** The full ARM path is unreadable in a table cell; the component name is what identifies it. */
function shortResourceId(resourceId) {
  if (!resourceId) return '—';
  const parts = String(resourceId).split('/');
  return parts[parts.length - 1] || resourceId;
}

/** alias -> the agents that read through it, derived from the queries currently in force. */
async function loadSourceUsage() {
  try {
    const response = await fetch('/api/settings/kql');
    if (!response.ok) return {};
    const body = await response.json();
    const usage = {};
    (body.queries || []).forEach((q) => {
      (q.sourceAliases || []).forEach((alias) => {
        const key = String(alias).toLowerCase();
        usage[key] = usage[key] || [];
        if (q.usedBy && !usage[key].includes(q.usedBy)) usage[key].push(q.usedBy);
      });
    });
    return usage;
  } catch (err) {
    console.warn('Could not work out which queries use each source.', err);
    return {};
  }
}

async function loadTelemetrySourcesAudit() {
  const tbody = document.getElementById('sources-audit-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/settings/telemetry-sources-audit');
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const body = await response.json();
    const entries = body.entries || [];
    tbody.innerHTML = entries.length === 0
      ? '<tr><td colspan="6"><div class="state-block">No source has been changed yet.</div></td></tr>'
      : entries.map((e) => `
        <tr>
          <td><code>${escapeHtml(e.alias)}</code></td>
          <td>${escapeHtml(e.environment)}</td>
          <td title="${escapeHtml(e.resourceId || '')}"><code>${escapeHtml(shortResourceId(e.resourceId))}</code></td>
          <td>${e.removed ? 'Yes' : 'No'}</td>
          <td>${formatRelativeTime(e.changedAt)}</td>
          <td>${escapeHtml(e.changedBy || '—')}</td>
        </tr>`).join('');
  } catch (err) {
    console.warn('/api/settings/telemetry-sources-audit unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="6"><div class="state-block state-block--error">History could not be read.</div></td></tr>';
  }
}

function wireTelemetrySourceSettings() {
  const saveBtn = document.getElementById('source-save');
  if (!saveBtn) return;
  saveBtn.addEventListener('click', async () => {
    setStatus('sources-status', 'Saving…', null);
    try {
      await settingsWrite('/api/settings/telemetry-sources', 'PUT', {
        alias: document.getElementById('source-alias').value,
        environment: document.getElementById('source-environment').value,
        resourceId: document.getElementById('source-resource-id').value,
        appId: document.getElementById('source-app-id').value || null,
        tenantId: document.getElementById('source-tenant-id').value || null,
        notes: document.getElementById('source-notes').value || null,
        enabled: document.getElementById('source-enabled').checked,
        changedBy: document.getElementById('source-changed-by').value,
      });
      setStatus('sources-status', 'Saved.', 'ok');
      loadTelemetrySources();
    } catch (err) {
      setStatus('sources-status', err.message, 'error');
    }
  });
}

/** The five dials are empty-means-inherit, so the form distinguishes "" from 0 everywhere. */
const BUDGET_FIELDS = [
  ['budget-debate-models', 'debateModels', 'text'],
  ['budget-debate-rounds', 'debateRounds', 'int'],
  ['budget-skip-below', 'skipDebateWhenContributorsBelow', 'int'],
  ['budget-help-hops', 'maxHelpHops', 'int'],
  ['budget-max-runs', 'maxRunsPerDay', 'int'],
];

async function loadAgentBudgets() {
  const status = 'budgets-status';
  try {
    const response = await fetch('/api/agent-budgets');
    if (!response.ok) {
      setStatus(status, `Budgets could not be read (HTTP ${response.status}).`, 'error');
      return;
    }
    const payload = await response.json();
    const inspector = (payload.agents || []).find((a) => a.id === 'inspector-monkey');
    const stored = inspector && inspector.stored ? inspector.stored : {};

    BUDGET_FIELDS.forEach(([elementId, key]) => {
      const input = document.getElementById(elementId);
      if (!input) return;
      // Null means inherit: the box stays empty and the placeholder says so.
      input.value = stored[key] === null || stored[key] === undefined ? '' : stored[key];
    });

    renderBudgetCost(payload.effective);
    state.budgetEffective = payload.effective;
    setStatus(
      status,
      inspector && inspector.overridden
        ? `Overridden${inspector.updatedBy ? ` by ${inspector.updatedBy}` : ''}.`
        : 'Running entirely on the configured defaults.',
      null);

    loadAgentBudgetHistory();
  } catch (err) {
    console.warn('/api/agent-budgets unreachable.', err);
    setStatus(status, 'Budgets could not be read.', 'error');
  }
}

/** What one investigation costs at the values currently in the form - the number people actually budget against. */
function renderBudgetCost(effective) {
  const el = document.getElementById('budget-cost-preview');
  if (!el) return;
  if (!effective) { el.textContent = '—'; return; }

  const rosterRaw = document.getElementById('budget-debate-models').value.trim();
  const models = rosterRaw
    ? rosterRaw.split(',').map((m) => m.trim()).filter(Boolean).length
    : (effective.debateModels || '').split(',').filter(Boolean).length;

  const roundsRaw = document.getElementById('budget-debate-rounds').value.trim();
  const rounds = roundsRaw === '' ? effective.debateRounds : Number(roundsRaw);

  if (!models || Number.isNaN(rounds)) { el.textContent = '—'; return; }

  const calls = models * (1 + rounds);
  el.innerHTML = `<strong>${calls} debate completion(s) per investigation</strong> — ${models} model(s) × (round 0 + ${rounds} peer round(s)). The specialists and the synthesis are on top of this.`;
}

async function loadAgentBudgetHistory() {
  const tbody = document.getElementById('budgets-audit-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/agent-budgets/inspector-monkey/history');
    if (!response.ok) {
      tbody.innerHTML = '<tr><td colspan="7"><div class="state-block state-block--error">History could not be read.</div></td></tr>';
      return;
    }
    const payload = await response.json();
    const rows = payload.history || [];
    if (rows.length === 0) {
      tbody.innerHTML = '<tr><td colspan="7"><div class="state-block">Nobody has changed these limits yet.</div></td></tr>';
      return;
    }
    const cell = (v) => escapeHtml(v === null || v === undefined ? 'inherit' : String(v));
    tbody.innerHTML = rows.map((h) => `
      <tr>
        <td><code>${cell(h.debateModels)}</code></td>
        <td>${cell(h.debateRounds)}</td>
        <td>${cell(h.skipDebateWhenContributorsBelow)}</td>
        <td>${cell(h.maxHelpHops)}</td>
        <td>${cell(h.maxRunsPerDay)}</td>
        <td>${formatRelativeTime(h.changedAt)}</td>
        <td>${escapeHtml(h.changedBy || '')}</td>
      </tr>`).join('');
  } catch (err) {
    console.warn('/api/agent-budgets history unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="7"><div class="state-block state-block--error">History could not be read.</div></td></tr>';
  }
}

function wireAgentBudgetSettings() {
  const saveBtn = document.getElementById('budget-save');
  if (!saveBtn) return;

  BUDGET_FIELDS.forEach(([elementId]) => {
    const input = document.getElementById(elementId);
    if (input) input.addEventListener('input', () => renderBudgetCost(state.budgetEffective));
  });

  saveBtn.addEventListener('click', async () => {
    setStatus('budgets-status', 'Saving…', null);
    const body = { changedBy: document.getElementById('budget-changed-by').value };
    BUDGET_FIELDS.forEach(([elementId, key, kind]) => {
      const raw = document.getElementById(elementId).value.trim();
      // An empty box is null, never 0: those are different instructions and 0 is a real value
      // for three of these five dials.
      body[key] = raw === '' ? null : (kind === 'int' ? Number(raw) : raw);
    });
    try {
      await settingsWrite('/api/agent-budgets/inspector-monkey', 'PUT', body);
      setStatus('budgets-status', 'Saved.', 'ok');
      loadAgentBudgets();
    } catch (err) {
      setStatus('budgets-status', err.message, 'error');
    }
  });

  document.getElementById('budget-reset').addEventListener('click', () => {
    BUDGET_FIELDS.forEach(([elementId]) => {
      const input = document.getElementById(elementId);
      if (input) input.value = '';
    });
    setStatus('budgets-status', 'Cleared — press Save to go back to the configured defaults.', null);
    renderBudgetCost(state.budgetEffective);
  });
}

// ============================================================================
// Company profiles — the header selector and the Settings panel
// ============================================================================

/** The only failure the selector must not hide: the feature needs a table this database lacks. */
const COMPANY_POINTER_MISSING =
  'This database has no active-company table yet — apply Sql/monitoring/CreateMonitoringCompanyProfile.sql. '
  + 'Until then the monitored company cannot be switched from the portal.';

/** Profile keys are matched case-insensitively server-side; the stored casing is the real one. */
const sameCompanyKey = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();

/** What the SERVER says is active, or '' when nothing is selected. Never read from localStorage. */
function activeCompanyKey() {
  return state.activeCompany?.profileKey || '';
}

/**
 * The single place a list call is scoped to the active company. GET /api/scout/packages, /runs,
 * /stats and /api/inspector/cases all take ?profileKey, and a blank one means every company.
 *
 * Nothing in the browser requests those four today — the Simian Army cards are built from
 * /api/monkeys — so this exists as the one seam to go through when they are added, rather than
 * four places each growing their own query-string code.
 */
function withActiveCompany(url) {
  const key = activeCompanyKey();
  if (!key) return url;
  return `${url}${url.includes('?') ? '&' : '?'}profileKey=${encodeURIComponent(key)}`;
}

/** Unlike settingsWrite, this keeps the status code: 400/401/404/503 need different sentences. */
async function companyWrite(url, method, payload) {
  const response = await fetch(url, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
    body: JSON.stringify(payload || {}),
  });
  const body = await response.json().catch(() => ({}));
  return { ok: response.ok, status: response.status, body };
}

function companyWriteMessage(status, body) {
  if (status === 401) return 'Refused: the portal has no settings write secret configured (Monitoring:SettingsWriteSecret).';
  if (status === 503) return (body && body.error) || COMPANY_POINTER_MISSING;
  if (body && body.error) return body.error;
  if (status === 404) return 'No company profile is stored under that key.';
  return `Request failed: ${status}`;
}

function companyLabel(key) {
  const profile = (state.companyProfiles || []).find((p) => sameCompanyKey(p.profileKey, key));
  return profile ? (profile.displayName || profile.profileKey) : (key || '(none selected)');
}

function setCompanyNote(message, tone) {
  const el = document.getElementById('company-select-note');
  if (!el) return;
  el.textContent = message || '';
  el.className = `company-note${tone ? ` company-note--${tone}` : ''}`;
}

/** Re-reads both the catalog and the server's pointer. Called at startup, after every switch and
 *  whenever the Settings panel is opened, so a switch made by somebody else shows up here. */
async function loadCompanySelector() {
  const select = document.getElementById('company-select');
  if (!select) return;
  try {
    const [listResponse, activeResponse] = await Promise.all([
      fetch('/api/settings/company-profiles'),
      fetch('/api/settings/company-profiles-active'),
    ]);
    if (!listResponse.ok) throw new Error(`Request failed: ${listResponse.status}`);
    state.companyProfiles = (await listResponse.json()).profiles || [];

    // A 200 here does not prove the pointer table exists: the endpoint answers with a null
    // profileKey both when nobody has chosen and when the table is missing. Only the PUT can
    // tell the two apart, which is why 503 is handled there and not here.
    state.activeCompany = activeResponse.ok
      ? await activeResponse.json()
      : { profileKey: null, updatedAt: null, updatedBy: null };
    renderCompanySelector();
  } catch (err) {
    console.warn('/api/settings/company-profiles unreachable.', err);
    select.innerHTML = '<option value="">(unavailable)</option>';
    select.disabled = true;
    setCompanyNote('The company catalog could not be read.', 'error');
  }
}

function renderCompanySelector() {
  const select = document.getElementById('company-select');
  if (!select) return;

  const activeKey = activeCompanyKey();
  const selectable = (state.companyProfiles || []).filter((p) => p.enabled);

  // An active profile that is disabled, or no longer stored, is still listed: dropping it would
  // make the box show some other company and read as if that one were in charge.
  const orphan = activeKey && !selectable.some((p) => sameCompanyKey(p.profileKey, activeKey));
  select.innerHTML = [
    '<option value="">(none selected)</option>',
    orphan ? `<option value="${escapeHtml(activeKey)}">${escapeHtml(activeKey)} — not enabled</option>` : '',
    ...selectable.map((p) => `<option value="${escapeHtml(p.profileKey)}">${escapeHtml(p.displayName || p.profileKey)}</option>`),
  ].filter(Boolean).join('');
  select.value = activeKey;
  select.disabled = state.companySwitchUnavailable;

  if (state.companySwitchUnavailable) {
    setCompanyNote(COMPANY_POINTER_MISSING, 'error');
  } else if (!activeKey) {
    setCompanyNote('Nobody has selected one — agents use Company:DefaultProfileKey.', 'warning');
  } else {
    const when = state.activeCompany.updatedAt ? ` ${formatRelativeTime(state.activeCompany.updatedAt)}` : '';
    const who = state.activeCompany.updatedBy ? ` by ${state.activeCompany.updatedBy}` : '';
    setCompanyNote(when || who ? `Selected${when}${who}.` : '', null);
  }
}

/**
 * Asks the server to switch, then believes only what the server answers back.
 *
 * Every failure is shown to the operator and the box is put back to the company that is really
 * active - a selector that kept the new value after a refused write would be lying about which
 * customer is being monitored, which is the one thing it exists to say.
 */
async function switchActiveCompany(requestedKey) {
  const select = document.getElementById('company-select');
  const previous = activeCompanyKey();
  if (sameCompanyKey(requestedKey, previous)) return;

  // There is no "deactivate": PUT with a blank profileKey is a 400, so the placeholder is a
  // label for "nothing is selected", not a command.
  if (!requestedKey) {
    setCompanyNote('Pick a company — the active one cannot be cleared from here.', 'warning');
    if (select) select.value = previous;
    return;
  }

  if (select) select.disabled = true;
  setCompanyNote('Switching…', null);

  const { ok, status, body } = await companyWrite('/api/settings/company-profiles-active', 'PUT', {
    profileKey: requestedKey,
    updatedBy: document.getElementById('company-editor-changed-by')?.value.trim() || 'portal',
  });

  if (!ok) {
    if (status === 503) state.companySwitchUnavailable = true;
    const message = companyWriteMessage(status, body);
    showToast('Company not switched', message, 'critical');
    setStatus('company-status', message, 'error');
    state.activeCompany = { ...state.activeCompany, profileKey: previous || null };
    renderCompanySelector();
    setCompanyNote(message, 'error');
    return;
  }

  state.companySwitchUnavailable = false;
  state.activeCompany = body || { profileKey: requestedKey, updatedAt: null, updatedBy: null };
  renderCompanySelector();
  showToast('Company switched', `Now monitoring ${companyLabel(activeCompanyKey())} — reloading the current view.`, 'success');
  await refreshForCompanyChange();
}

/** A switch changes what every agent classifies, so the view is re-read instead of left showing
 *  another customer's rows. Goes through the Refresh button's own path - no full page reload. */
async function refreshForCompanyChange() {
  await Promise.all([refreshData({ silent: true }), reloadMonkeysForFilters()]);
  const settingsPanel = document.getElementById('tab-settings');
  if (settingsPanel && !settingsPanel.hidden) loadCompanyProfiles();
}

function wireCompanySelector() {
  const select = document.getElementById('company-select');
  if (!select) return;
  select.addEventListener('change', () => switchActiveCompany(select.value));
}

async function loadCompanyProfiles() {
  await loadCompanySelector();
  renderCompanyProfiles();
  loadCompanyAudit();
}

function renderCompanyProfiles() {
  const tbody = document.getElementById('company-profiles-tbody');
  if (!tbody) return;

  const activeKey = activeCompanyKey();
  const profiles = state.companyProfiles || [];
  tbody.innerHTML = profiles.length === 0
    ? '<tr><td colspan="8"><div class="state-block">No company profile is stored on this database.</div></td></tr>'
    : profiles.map((p) => {
      const isActive = sameCompanyKey(p.profileKey, activeKey);
      return `
      <tr data-key="${escapeHtml(p.profileKey)}">
        <td>${escapeHtml(p.displayName || p.profileKey)}</td>
        <td><code>${escapeHtml(p.profileKey)}</code></td>
        <td>${isActive ? '<span class="status-badge status-badge--sev-medium">Active</span>' : ''}</td>
        <td>${(p.services || []).length}</td>
        <td>${p.version ? `v${p.version}` : '—'}</td>
        <td>${p.enabled ? 'Yes' : 'No'}</td>
        <td>${p.updatedAt ? `${formatRelativeTime(p.updatedAt)} by ${escapeHtml(p.updatedBy || 'unknown')}` : '—'}</td>
        <td>
          <button class="btn btn--icon btn--sm" data-action="edit-company">Edit</button>
          ${p.enabled && !isActive ? '<button class="btn btn--icon btn--sm" data-action="activate-company">Make active</button>' : ''}
        </td>
      </tr>`;
    }).join('');

  tbody.querySelectorAll('button[data-action="edit-company"]').forEach((btn) => {
    btn.addEventListener('click', () => openCompanyEditor(btn.closest('tr').dataset.key));
  });
  tbody.querySelectorAll('button[data-action="activate-company"]').forEach((btn) => {
    btn.addEventListener('click', () => switchActiveCompany(btn.closest('tr').dataset.key));
  });

  if (state.companySwitchUnavailable) setStatus('company-status', COMPANY_POINTER_MISSING, 'error');
  else if (!activeKey) setStatus('company-status', 'No company is active — agents use Company:DefaultProfileKey.', 'warning');
  else setStatus('company-status', `Active: ${companyLabel(activeKey)}.`, 'ok');
}

async function openCompanyEditor(key) {
  const card = document.getElementById('company-editor');
  if (!card) return;
  try {
    const response = await fetch(`/api/settings/company-profiles/${encodeURIComponent(key)}`);
    const detail = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(detail.error || `Request failed: ${response.status}`);

    card.hidden = false;
    card.dataset.key = detail.profileKey || key;
    document.getElementById('company-editor-title').textContent = `Edit ${detail.displayName || key}`;
    document.getElementById('company-editor-text').value = JSON.stringify(detail, null, 2);
    renderCompanyProblems([]);

    document.getElementById('company-editor-hint').innerHTML = [
      `<strong>Key:</strong> <code>${escapeHtml(detail.profileKey || key)}</code> — the route decides it; a body naming another company is refused rather than quietly rewritten.`,
      `${(detail.services || []).length} service(s), ${(detail.entityIdentitySources || []).length} identity source(s).`,
      detail.enabled ? '' : 'This profile is <strong>disabled</strong> — enable it before it can be made active.',
    ].filter(Boolean).join(' · ');

    setStatus('company-editor-status', '', null);
    card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  } catch (err) {
    showToast('Company profiles', err.message, 'critical');
  }
}

/** Every problem the validator returned, not just the first: the server's own error quotes one
 *  and counts the rest, which is exactly the part nobody can act on. */
function renderCompanyProblems(problems) {
  const host = document.getElementById('company-editor-problems');
  if (!host) return;
  const list = Array.isArray(problems) ? problems : [];
  host.innerHTML = list.length === 0 ? '' : `
    <div class="state-block state-block--error">
      <strong>${list.length} problem(s) in this document:</strong>
      <ul class="company-problems">${list.map((p) => `<li>${escapeHtml(p)}</li>`).join('')}</ul>
    </div>`;
}

function wireCompanySettings() {
  const card = document.getElementById('company-editor');
  if (!card) return;

  document.getElementById('company-editor-close').addEventListener('click', () => { card.hidden = true; });

  document.getElementById('company-editor-format').addEventListener('click', () => {
    const textarea = document.getElementById('company-editor-text');
    try {
      textarea.value = JSON.stringify(JSON.parse(textarea.value), null, 2);
      renderCompanyProblems([]);
      setStatus('company-editor-status', 'Valid JSON. The mapping itself is still checked on save.', 'ok');
    } catch (err) {
      setStatus('company-editor-status', `Not valid JSON: ${err.message}`, 'error');
    }
  });

  document.getElementById('company-editor-save').addEventListener('click', async () => {
    const key = card.dataset.key;
    const textarea = document.getElementById('company-editor-text');

    let profile;
    try {
      profile = JSON.parse(textarea.value);
    } catch (err) {
      setStatus('company-editor-status', `Not valid JSON: ${err.message}`, 'error');
      return;
    }

    const changedBy = document.getElementById('company-editor-changed-by').value.trim();
    if (changedBy) profile.updatedBy = changedBy;

    setStatus('company-editor-status', 'Saving…', null);
    renderCompanyProblems([]);
    const { ok, status, body } = await companyWrite(`/api/settings/company-profiles/${encodeURIComponent(key)}`, 'PUT', profile);
    if (!ok) {
      renderCompanyProblems(body.problems);
      setStatus('company-editor-status', companyWriteMessage(status, body), 'error');
      return;
    }

    setStatus('company-editor-status', body.version ? `Saved as v${body.version}.` : 'Saved.', 'ok');
    showToast('Company profiles', `${key} saved.`, 'success');
    loadCompanyProfiles();
  });

  document.getElementById('company-editor-activate').addEventListener('click', () => switchActiveCompany(card.dataset.key));
}

async function loadCompanyAudit() {
  const tbody = document.getElementById('company-audit-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/settings/company-profiles-audit?top=50');
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const entries = (await response.json()).entries || [];

    tbody.innerHTML = entries.length === 0
      ? '<tr><td colspan="6"><div class="state-block">No profile has been edited yet.</div></td></tr>'
      : entries.map((e) => `
        <tr data-audit-id="${e.auditId}">
          <td><code>${escapeHtml(e.profileKey)}</code></td>
          <td>${e.removed ? '—' : `v${e.version}`}</td>
          <td>${e.removed ? '<em>removed</em>' : `${Number((e.profileJson || '').length).toLocaleString()} chars`}</td>
          <td>${formatRelativeTime(e.changedAt)}</td>
          <td>${escapeHtml(e.changedBy || '—')}</td>
          <td>${e.removed ? '' : '<button class="btn btn--icon btn--sm" data-action="load-company-revision">Load into editor</button>'}</td>
        </tr>`).join('');

    tbody.querySelectorAll('button[data-action="load-company-revision"]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const entry = entries.find((e) => String(e.auditId) === btn.closest('tr').dataset.auditId);
        if (entry) loadCompanyRevisionIntoEditor(entry);
      });
    });
  } catch (err) {
    console.warn('/api/settings/company-profiles-audit unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="6"><div class="state-block state-block--error">History could not be read.</div></td></tr>';
  }
}

/** There is no restore endpoint for profiles (unlike KQL): a previous revision goes back through
 *  the same PUT as any other edit, so the store only ever sees one kind of write. */
function loadCompanyRevisionIntoEditor(entry) {
  const card = document.getElementById('company-editor');
  if (!card) return;

  card.hidden = false;
  card.dataset.key = entry.profileKey;
  document.getElementById('company-editor-title').textContent = `Edit ${entry.profileKey} — v${entry.version} from history`;
  try {
    document.getElementById('company-editor-text').value = JSON.stringify(JSON.parse(entry.profileJson), null, 2);
  } catch {
    document.getElementById('company-editor-text').value = entry.profileJson || '';
  }
  renderCompanyProblems([]);
  setStatus('company-editor-status', `Loaded v${entry.version} from ${formatRelativeTime(entry.changedAt)} — press Save to put it back.`, null);
  card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function wireHeaderControls() {
  document.getElementById('refresh-btn').addEventListener('click', () => refreshData());
  document.getElementById('run-demo-scenario').addEventListener('click', runDemoScenario);
  document.getElementById('auto-refresh-toggle').addEventListener('change', (e) => {
    if (e.target.checked) {
      state.autoRefreshTimer = setInterval(() => refreshData({ silent: true }), 90000);
      showToast('Auto refresh', 'Enabled — re-reads the SQL snapshot every 90 seconds. Use Refresh to pull latest from Azure.', 'info');
    } else {
      clearInterval(state.autoRefreshTimer);
      showToast('Auto refresh', 'Disabled.', 'info');
    }
  });
}

function wireClubMapFilters() {
  if (state.clubMapFiltersWired) return;
  state.clubMapFiltersWired = true;
  const brand = document.getElementById('club-map-filter-brand');
  const product = document.getElementById('club-map-filter-product');
  const status = document.getElementById('club-map-filter-status');
  const platform = document.getElementById('club-map-filter-platform');
  const q = document.getElementById('club-map-filter-q');
  const onChange = () => {
    state.clubMapFilters = {
      brand: brand ? brand.value : '',
      product: product ? product.value : '',
      status: status ? status.value : '',
      platform: platform ? platform.value : '',
      q: q ? q.value.trim() : '',
    };
    renderClubMapTable();
  };
  brand?.addEventListener('change', onChange);
  product?.addEventListener('change', onChange);
  status?.addEventListener('change', onChange);
  platform?.addEventListener('change', onChange);
  q?.addEventListener('input', onChange);
}

/** Club Map products (normalized catalog; fixed column order). */
const CLUB_MAP_PRODUCTS = ['Cars', 'Cruises', 'Hotels', 'Flights'];
const CLUB_MAP_PRODUCT_SORT_KEYS = {
  productCars: 'Cars',
  productCruises: 'Cruises',
  productHotels: 'Hotels',
  productFlights: 'Flights',
};

/** Map product name -> { maturityStatus, maturityStatusName } for a club row. */
function clubMapProductStatusMap(row) {
  const map = new Map();
  const list = Array.isArray(row.products) ? row.products : [];
  list.forEach((p) => {
    if (typeof p === 'string') {
      const name = p.trim();
      if (name) map.set(name, { maturityStatus: 0, maturityStatusName: 'Not Observed' });
      return;
    }
    const name = String(p?.name || '').trim();
    if (!name) return;
    map.set(name, {
      maturityStatus: Number.isFinite(p.maturityStatus) ? p.maturityStatus : 0,
      maturityStatusName: p.maturityStatusName || 'Not Observed',
    });
  });
  // Legacy snapshot: domain CSV without statuses
  if (map.size === 0 && row.domain) {
    String(row.domain)
      .split(',')
      .map((x) => x.trim())
      .filter((d) => CLUB_MAP_PRODUCTS.includes(d))
      .forEach((d) => map.set(d, { maturityStatus: 0, maturityStatusName: 'Not Observed' }));
  }
  return map;
}

function clubMapProducts(row) {
  const map = clubMapProductStatusMap(row);
  return CLUB_MAP_PRODUCTS.filter((p) => map.has(p));
}

function fillClubMapFilterOptions(rows) {
  const brandSelect = document.getElementById('club-map-filter-brand');
  const productSelect = document.getElementById('club-map-filter-product');
  if (!brandSelect || !productSelect) return;

  const brands = [...new Set(rows.map((r) => r.brandName).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  const products = new Set();
  rows.forEach((r) => {
    clubMapProducts(r).forEach((p) => products.add(p));
  });
  const productList = CLUB_MAP_PRODUCTS.filter((p) => products.has(p));

  const keepBrand = state.clubMapFilters.brand;
  const keepProduct = state.clubMapFilters.product;
  brandSelect.innerHTML = `<option value="">All brands</option>${brands.map((b) => `<option value="${escapeHtml(b)}">${escapeHtml(b)}</option>`).join('')}`;
  productSelect.innerHTML = `<option value="">All</option>${productList.map((p) => `<option value="${escapeHtml(p)}">${escapeHtml(p)}</option>`).join('')}`;
  if (keepBrand && brands.includes(keepBrand)) brandSelect.value = keepBrand;
  if (keepProduct && products.has(keepProduct)) productSelect.value = keepProduct;
}

function filteredClubInventory() {
  const f = state.clubMapFilters;
  const needle = (f.q || '').toLowerCase();
  const statusFilter = f.status === '' || f.status == null ? null : Number(f.status);
  return state.clubInventory.filter((row) => {
    const platform = row.fusionPlatform || '';
    if (platform !== 'Fusion' && platform !== 'Hybrid') return false;
    if (f.brand && row.brandName !== f.brand) return false;
    if (f.platform && platform !== f.platform) return false;
    const productMap = clubMapProductStatusMap(row);
    if (f.product && !productMap.has(f.product)) return false;
    if (statusFilter != null && Number.isFinite(statusFilter)) {
      // Match clubs that have the selected status on any product (or on the filtered product).
      const entries = f.product
        ? [productMap.get(f.product)].filter(Boolean)
        : [...productMap.values()];
      if (!entries.some((p) => p.maturityStatus === statusFilter)) return false;
    }
    if (needle) {
      const hay = [row.clubId, row.clubName, row.site, row.brandName, row.clientId, row.telemetryLocation]
        .filter((v) => v != null && v !== '')
        .join(' ')
        .toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });
}

function platformPill(platform) {
  const value = platform || 'Unknown';
  const cls = value === 'Fusion' || value === 'Hybrid' ? 'status-badge status-badge--healthy' : 'status-badge status-badge--unknown';
  return `<span class="${cls}">${escapeHtml(value)}</span>`;
}

/** One fixed product column: status circle, or — if club has no that product. */
function productStatusCell(row, productName) {
  const info = clubMapProductStatusMap(row).get(productName);
  if (!info) {
    return '<td class="club-map-cell club-map-cell--muted club-map-cell--product">—</td>';
  }
  const level = info.maturityStatus;
  const name = info.maturityStatusName || 'Not Observed';
  const cls =
    level === 1 ? 'maturity-badge maturity-badge--1'
      : level === 2 ? 'maturity-badge maturity-badge--2'
        : level === 3 ? 'maturity-badge maturity-badge--3'
          : 'maturity-badge maturity-badge--0';
  return `<td class="club-map-cell club-map-cell--product"><span class="${cls}" title="${escapeHtml(productName)}: ${escapeHtml(name)}">${level}</span></td>`;
}

function textCell(value, className = '') {
  const text = value == null || value === '' ? '—' : String(value);
  const muted = text === '—' ? ' club-map-cell--muted' : '';
  return `<td class="club-map-cell club-map-cell--text${muted} ${className}">${escapeHtml(text)}</td>`;
}

/** Telemetry: one resource per line. */
function telemetryCell(value) {
  const parts = String(value || '')
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean);
  if (parts.length === 0) {
    return '<td class="club-map-cell club-map-cell--muted">—</td>';
  }
  const lines = parts.map((p) => `<div class="club-map-telemetry-line">${escapeHtml(p)}</div>`).join('');
  return `<td class="club-map-cell club-map-cell--telemetry">${lines}</td>`;
}

function renderClubMapTable() {
  const tbody = document.getElementById('club-map-tbody');
  const note = document.getElementById('club-map-note');
  if (!tbody) return;

  const rows = filteredClubInventory();
  const sort = state.sort.clubMap || { key: 'clubId', dir: 'asc' };
  const sorted = sortRows(rows, sort.key, sort.dir, (row, key) => {
    const productName = CLUB_MAP_PRODUCT_SORT_KEYS[key];
    if (productName) {
      const info = clubMapProductStatusMap(row).get(productName);
      // Missing product sorts below present ones; then by status level.
      return info ? info.maturityStatus : -1;
    }
    const v = row[key];
    if (typeof v === 'number') return v;
    return (v == null ? '' : String(v)).toLowerCase();
  });

  if (note) {
    const meta = state.clubInventoryMeta;
    if (meta.warning) {
      note.textContent = meta.warning;
    } else {
      note.textContent = `Showing ${sorted.length} of ${meta.rowCount || state.clubInventory.length} clubs` +
        (meta.source ? ` · source: ${meta.source}` : '') + '.';
    }
  }

  if (sorted.length === 0) {
    tbody.innerHTML = `<tr><td colspan="12"><div class="state-block" role="status">${
      state.clubInventory.length === 0
        ? 'No inventory loaded.'
        : 'No clubs match the current filters.'
    }</div></td></tr>`;
    return;
  }

  tbody.innerHTML = sorted.map((row) => `
    <tr>
      ${productStatusCell(row, 'Cars')}
      ${productStatusCell(row, 'Cruises')}
      ${productStatusCell(row, 'Hotels')}
      ${productStatusCell(row, 'Flights')}
      ${textCell(row.brandName, 'club-map-cell--brand')}
      <td class="club-map-cell club-map-cell--num">${escapeHtml(row.clubId)}</td>
      ${textCell(row.clubName, 'club-map-cell--name')}
      <td class="club-map-cell club-map-cell--num">${escapeHtml(row.clientId == null ? '—' : row.clientId)}</td>
      ${textCell(row.site, 'club-map-cell--site')}
      <td class="club-map-cell">${platformPill(row.fusionPlatform)}</td>
      ${telemetryCell(row.telemetryLocation)}
      <td class="club-map-cell">${escapeHtml(row.environment || '—')}</td>
    </tr>`).join('');
}

async function loadClubInventory({ force = false } = {}) {
  if (state.clubInventoryLoaded && !force) return;
  const tbody = document.getElementById('club-map-tbody');
  if (tbody) tbody.innerHTML = loadingTableRow(12);
  try {
    let payload;
    try {
      payload = await fetchJsonPayload('/api/club-inventory');
    } catch (apiErr) {
      // Local preview when Functions is down: snapshot exported from brand.ClubInventory.
      console.warn('/api/club-inventory unavailable, trying data/club-inventory.json', apiErr);
      payload = await fetchJson('data/club-inventory.json');
      if (payload && !payload.warning) {
        payload.warning = 'Loaded from local snapshot (API unavailable).';
      }
    }
    state.clubInventory = payload.clubs || [];
    state.clubInventoryMeta = {
      source: payload.source || '',
      warning: payload.warning || '',
      rowCount: payload.rowCount ?? state.clubInventory.length,
    };
    state.clubInventoryLoaded = true;
    fillClubMapFilterOptions(state.clubInventory);
  } catch (err) {
    console.warn('club inventory unavailable', err);
    state.clubInventory = [];
    state.clubInventoryMeta = {
      source: 'error',
      warning: err.message || 'Could not load club inventory. Start Functions and ensure brand.ClubInventory is seeded.',
      rowCount: 0,
    };
    state.clubInventoryLoaded = false;
  }
  renderClubMapTable();
}

async function ensureClubMapTab() {
  wireClubMapFilters();
  await loadClubInventory();
}

async function init() {
  document.getElementById('env-select').value = state.filters.environment;
  document.getElementById('time-range-select').value = state.filters.timeRange;
  wireOverlayDismiss();
  wireTabs();
  wireFilterBar();
  wireHeaderControls();
  wireLlmClientSettings();
  wireSloDefinitionAdd();
  wireNotificationsSettings();
  wireSettingsSubnav();
  wireKqlSettings();
  wireTelemetrySourceSettings();
  wireAgentBudgetSettings();
  wireCompanySelector();
  wireCompanySettings();

  wireSortableTable('journeys-table', 'journeys', state.journeys, renderJourneysTable);
  wireSortableTable('exceptions-table', 'exceptions', state.exceptions, renderExceptionsTable);
  wireSortableTable('club-map-table', 'clubMap', state.clubInventory, renderClubMapTable);

  // Before the first load so the header never shows a company the server has not confirmed, and
  // so anything scoped by withActiveCompany() already knows the key.
  await loadCompanySelector();

  try {
    await loadAllData();
    renderAllTabs();
    updateLastRefreshTimestamp();
  } catch (err) {
    console.error(err);
    document.getElementById('tab-overview').innerHTML = `<div class="state-block state-block--error">Failed to load demo data. Ensure you are running this via a local server (see README).</div>`;
  }
}

document.addEventListener('DOMContentLoaded', init);
