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
// Same caps as the per-company journey read: first paint asks for 50, a filter change for 200.
const INIT_ROW_CAP = 50;
const FILTER_ROW_CAP = 200;
let lastOverviewWarningToast = '';

/* --- Active company profile (contract v6 §2, §6) ---------------------------------------------
   Every company word the portal shows - service names, the entity filter, column titles - comes
   from the active profile. Nothing below names a particular company's services. */

const PROFILE_LABEL_DEFAULTS = { service: 'Service', flow: 'Journey', step: 'Step', entity: 'Entity' };

/** What the active company calls each role; the journey read's own labels win when present. */
function profileLabels() {
  const fromProfile = (state.activeProfile && state.activeProfile.labels) || {};
  const fromJourneys = state.journeyLabels || {};
  const pick = (role) => String(fromJourneys[role] || fromProfile[role] || PROFILE_LABEL_DEFAULTS[role]).trim()
    || PROFILE_LABEL_DEFAULTS[role];
  return { service: pick('service'), flow: pick('flow'), step: pick('step'), entity: pick('entity') };
}

const PROFILE_GLOSSARY_NOUNS = { service: 'serviceNoun', flow: 'flowNoun', step: 'stepNoun', entity: 'entityNoun' };

/** The role's word for running text, lower case ("club", "sucursal"): glossary first, then the label. */
function profileNoun(role) {
  const glossary = (state.activeProfile && state.activeProfile.glossary) || {};
  const word = String(glossary[PROFILE_GLOSSARY_NOUNS[role]] || profileLabels()[role] || role).trim();
  return (word || role).toLowerCase();
}

/** "Sucursal: 12" - a count of entities that needs no plural of the company's own word. */
function entityTally(count) {
  return `${profileLabels().entity}: ${formatCount(count || 0)}`;
}

/** The mocked roster (data/monkeys.json) is written with {service}/{flow}/{step}/{entity} placeholders,
 *  filled here with the active company's words so no company's vocabulary is baked into it. */
function localizeMockMonkeys(mock) {
  if (!Array.isArray(mock)) return [];
  const services = profileServices();
  return mock.map((monkey, index) => {
    const words = {
      service: services.length ? services[index % services.length].label : profileNoun('service'),
      flow: profileNoun('flow'),
      step: profileNoun('step'),
      entity: profileNoun('entity'),
    };
    const fill = (value) => {
      if (typeof value === 'string') return value.replace(/\{(service|flow|step|entity)\}/g, (_, role) => words[role]);
      if (Array.isArray(value)) return value.map(fill);
      if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, fill(v)]));
      return value;
    };
    return fill(monkey);
  });
}

/** The active company's services as { key, label, flows }, in profile order. */
function profileServices() {
  const services = (state.activeProfile && Array.isArray(state.activeProfile.services)) ? state.activeProfile.services : [];
  return services
    .filter((s) => s && s.key)
    .map((s) => ({ key: String(s.key), label: String(s.displayName || s.key), flows: Array.isArray(s.flows) ? s.flows : [] }));
}

function profileService(key) {
  const wanted = String(key || '').toLowerCase();
  return profileServices().find((s) => s.key.toLowerCase() === wanted) || null;
}

/** Steps of one service (every flow), ordered, first definition of a key wins. */
function profileServiceSteps(serviceKey) {
  const service = profileService(serviceKey);
  if (!service) return [];
  const seen = new Set();
  return service.flows
    .flatMap((flow) => (Array.isArray(flow.steps) ? flow.steps : []))
    .filter((step) => step && step.key && !seen.has(String(step.key).toLowerCase()) && seen.add(String(step.key).toLowerCase()))
    .sort((a, b) => (Number(a.order) || 0) - (Number(b.order) || 0))
    .map((step) => ({ key: String(step.key), label: String(step.displayName || step.key), order: Number(step.order) || 0 }));
}

function profileFlowLabel(serviceKey, flowKey) {
  if (!flowKey) return '';
  const service = profileService(serviceKey);
  const flow = service && service.flows.find((f) => String(f.key).toLowerCase() === String(flowKey).toLowerCase());
  return flow ? String(flow.displayName || flow.key) : String(flowKey);
}

function profileStepLabel(serviceKey, stepKey) {
  if (!stepKey) return '';
  const step = profileServiceSteps(serviceKey).find((s) => s.key.toLowerCase() === String(stepKey).toLowerCase());
  return step ? step.label : String(stepKey);
}

/** Every service of the active company; "All" in the filter means exactly these. */
function journeyDomains() {
  return profileServices().map((s) => s.key);
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

/** Contract v6 §4.1 state → the health vocabulary the shared helpers (domain-health.js) read. */
function profileJourneyOutcome(status, failed) {
  const text = String(status || '');
  if (failed || text === 'Failed') return 'failed';
  if (text === 'Completed') return 'success';
  if (text.startsWith('Dropped')) return 'dropped';
  return 'unknown';
}

/** One row of GET /api/profile-journeys in the shape every journey renderer reads. */
function mapProfileJourney(row) {
  const stepCount = Number(row.stepCount) || 0;
  return {
    id: String(row.journeyId),
    correlationId: row.sampleOperationId || String(row.journeyId),
    profileKey: row.profileKey || '',
    domain: row.serviceKey || '',
    flow: row.flowKey || '',
    entity: row.entityKey || '',
    club: row.entityKey || '',
    status: row.status || 'Unknown',
    outcome: profileJourneyOutcome(row.status, row.failed),
    startDate: row.startedAt,
    timestamp: row.endedAt || row.startedAt,
    totalDurationMs: Math.round(Number(row.durationMs) || 0),
    stepCount,
    lastStepOrder: row.lastStepOrder ?? null,
    lastStepKey: row.lastStepKey || '',
    totalCalls: stepCount,
    failedCalls: row.failed ? 1 : 0,
    failed: !!row.failed,
    stages: [],
  };
}

function profileJourneyQuery() {
  const qs = new URLSearchParams({
    win: headerQueryWin(),
    top: String(state.rowCap || INIT_ROW_CAP),
  });
  if (state.filters.domain && state.filters.domain !== 'all') qs.set('service', state.filters.domain);
  if (state.filters.club) qs.set('entity', state.filters.club);
  return qs;
}

const dataProvider = {
  /** Contract v6 §4.1: the same generic journeys for every company, the incumbent included. */
  async getJourneys() {
    const qs = profileJourneyQuery();
    try {
      const payload = await fetchJsonPayload(withActiveCompany(`/api/profile-journeys?${qs.toString()}`));
      state.journeyLabels = payload.labels || null;
      const rows = Array.isArray(payload.journeys) ? payload.journeys : [];
      state.journeysMeta = {
        source: 'sql',
        rowCount: rows.length,
        warning: '',
        cached: false,
        fetchedAt: '',
        domain: state.filters.domain,
        win: headerQueryWin(),
        environment: state.filters.environment,
        rowCap: state.rowCap || INIT_ROW_CAP,
        profileKey: payload.profileKey || activeCompanyKey(),
      };
      return rows.map(mapProfileJourney);
    } catch (err) {
      console.warn('/api/profile-journeys unreachable.', err);
      state.journeysMeta = {
        source: 'error',
        rowCount: 0,
        warning: err.message || 'Could not load journeys.',
        cached: false,
        fetchedAt: '',
        domain: state.filters.domain,
        win: headerQueryWin(),
        environment: state.filters.environment,
        rowCap: state.rowCap || INIT_ROW_CAP,
      };
      return [];
    }
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
    const mock = localizeMockMonkeys(await fetchJson('data/monkeys.json'));
    try {
      const response = await fetch(withActiveCompany(`/api/monkeys${monkeyFilterQuery()}`));
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
      const payload = await fetchJsonPayload(withActiveCompany(`/api/exceptions?${qs.toString()}`));
      const exceptions = payload.exceptions || [];
      state.exceptionsMeta = {
        source: payload.source || 'sql',
        warning: payload.warning || '',
        rowCount: exceptions.length,
        win: payload.win || headerQueryWin(),
        baselineCounts: payload.baselineCounts || null,
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
      return await fetchJsonPayload(withActiveCompany(`/api/overview?${qs.toString()}`));
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
      const response = await fetch(withActiveCompany(`/api/analytics/metrics?${qs.toString()}`));
      const payload = await response.json().catch(() => ({}));
      // Contract v6 §4.4: the response names the company's services, their charts and steps.
      state.dashboardMeta = {
        profileKey: payload.profileKey || '',
        labels: payload.labels || null,
        services: Array.isArray(payload.services) ? payload.services : null,
        warning: payload.warning || (response.ok ? '' : payload.error || `Request failed: ${response.status}`),
      };
      if (!response.ok) return [];
      return payload.points || [];
    } catch (err) {
      state.dashboardMeta = { profileKey: '', labels: null, services: null, warning: err.message || 'Analytics unavailable.' };
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
  events: [], analytics: null, dashboardMetrics: [],
  /** GET /api/analytics/metrics description of the company's services, charts and steps (contract v6 §4.4). */
  dashboardMeta: null,
  /** One comparison row per service of the active company, rebuilt on the Analytics tab. */
  serviceComparison: [],
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
  /** Full profile of the active company: theme, labels and services drive every company word on screen. */
  activeProfile: null,
  /** labels of the last GET /api/profile-journeys (service, flow, step, entity). */
  journeyLabels: null,
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
  sort: { journeys: { key: 'timestamp', dir: 'desc' }, exceptions: { key: 'severity', dir: 'desc' }, clubMap: { key: 'clientId', dir: 'asc' } },
  clubInventory: [],
  clubInventoryMeta: { source: '', warning: '', rowCount: 0 },
  clubInventoryLoaded: false,
  clubMapFilters: { brand: '', product: '', status: '', platform: '', q: '' },
  clubMapFiltersWired: false,
  /** Client-side table paging for Application Map (full filtered set stays in memory). */
  clubMapPagination: { page: 1, pageSize: 50 },
  clubMapPaginationWired: false,
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

/** A service key named the way the active company names it; unknown keys are shown as they are. */
function domainLabel(domain) {
  if (!domain || domain === 'all') return `All (${profileLabels().service})`;
  const service = profileService(domain);
  return service ? service.label : domain;
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

function showOverviewWarningToast(overview) {
  const warning = String(overview?.warning || '').trim();
  if (!warning) {
    lastOverviewWarningToast = '';
    return;
  }

  if (warning === lastOverviewWarningToast) return;
  lastOverviewWarningToast = warning;

  const fabricOnlyWarning = overview?.source === 'sql' && warning.startsWith('Fabric warehouse configuration is missing:');
  const tone = fabricOnlyWarning ? 'warning' : 'critical';
  const title = fabricOnlyWarning ? 'Fabric configuration warning' : 'Overview warning';
  showToast(title, warning, tone);
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
  // Application Map has its own filters. Platform Health does not use Domain / Club / Journey.
  document.body.classList.toggle('is-club-map-tab', tabName === 'club-map');
  document.body.classList.toggle('is-health-tab', tabName === 'health');
  document.body.classList.toggle('is-daemons-tab', tabName === 'daemons');
  if (ensureDomainComparison()) renderAnalyticsTab();
  // Loaded on first visit only: the corpus changes when an import runs, not while browsing.
  if (tabName === 'memory') renderMemoryBankTab();
  if (tabName === 'club-map') ensureClubMapTab();
  // A fetch, not just a re-render: all three agent pollers skip the request while this tab is
  // hidden, so state.monkeys can be minutes old by the time someone comes back to it.
  if (tabName === 'monkeys') reloadMonkeysForFilters();
  // Pillar health and Triage are only polled while this tab is visible, so re-read on entry.
  if (tabName === 'health' && rwWatch.ready) rwRefreshBackend();
  // daemons.js polls only while its panel is visible; entering the tab re-reads immediately.
  if (tabName === 'daemons' && window.DaemonsTab) window.DaemonsTab.onActivate();
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
    const haystack = [item.id, item.customer, item.bookingId, item.correlationId, item.entity].filter(Boolean).join(' ').toLowerCase();
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

function currentClubLabel() {
  const select = document.getElementById('filter-club');
  const opt = select && select.selectedOptions ? select.selectedOptions[0] : null;
  if (opt && opt.value) return opt.textContent;
  return '';
}

/** Contract v6 §6: the Domain filter lists the active company's services, named with labels.service. */
function renderDomainFilter() {
  const select = document.getElementById('filter-domain');
  const label = document.getElementById('filter-domain-label');
  const labels = profileLabels();
  if (label) label.textContent = labels.service;
  if (!select) return;
  const services = profileServices();
  const keep = services.some((s) => s.key === state.filters.domain) ? state.filters.domain : 'all';
  select.innerHTML = [
    '<option value="all">All</option>',
    ...services.map((s) => `<option value="${escapeHtml(s.key)}">${escapeHtml(s.label)}</option>`),
  ].join('');
  select.value = keep;
  state.filters.domain = keep;
}

/** Entity dropdown rows: { value, label } from GET /api/entities. */
function renderClubSelect({ entities, disabled, placeholder }) {
  const select = document.getElementById('filter-club');
  const label = document.getElementById('filter-club-label');
  const entityLabel = profileLabels().entity;
  if (label) label.textContent = entityLabel;
  if (!select) return;
  const rows = Array.isArray(entities) ? entities : [];
  state.clubSelectDisabled = !!disabled;
  select.disabled = state.tablesLoading || state.clubSelectDisabled;
  state.clubOptions = rows;
  const blank = `<option value="">${escapeHtml(placeholder || 'All')}</option>`;
  const keep = rows.some((c) => c.value === state.filters.club) ? state.filters.club : '';
  select.innerHTML = blank + rows
    .map((c) => `<option value="${escapeHtml(c.value)}">${escapeHtml(c.label)}</option>`)
    .join('');
  select.value = keep;
  state.filters.club = keep;
}

/** Contract v6 §4.2: the entity filter is the active company's busiest entities in the window. */
async function loadClubsForDomain(domain) {
  const qs = new URLSearchParams({ win: headerQueryWin(), top: '100' });
  if (domain && domain !== 'all') qs.set('service', domain);
  const company = activeCompanyKey();
  try {
    const payload = await fetchJsonPayload(withActiveCompany(`/api/entities?${qs.toString()}`));
    if (activeCompanyKey() !== company) return;
    if (payload.entityLabel) {
      state.journeyLabels = { ...(state.journeyLabels || {}), entity: payload.entityLabel };
    }
    const entities = (payload.entities || [])
      .filter((e) => e && e.key)
      .map((e) => ({
        value: String(e.key),
        label: e.failures ? `${e.key} (${Number(e.requests || 0).toLocaleString()} req, ${Number(e.failures).toLocaleString()} failed)` : `${e.key} (${Number(e.requests || 0).toLocaleString()} req)`,
      }));
    renderClubSelect({ entities, disabled: entities.length === 0 });
  } catch (err) {
    console.warn('/api/entities unavailable', err);
    renderClubSelect({ entities: [], disabled: true, placeholder: `${profileLabels().entity}: unavailable` });
  }
}

function shouldQueryJourneys(domain = state.filters.domain) {
  return domain === 'all' || journeyDomains().includes(domain);
}

/** First user-driven filter change switches the KQL sample from 50 → 200. */
function markFiltersApplied() {
  state.rowCap = FILTER_ROW_CAP;
}

/**
 * Rebuilds the comparison rows from the journeys the Domain filter already loaded, so the
 * Analytics tab never reads a service the user did not select.
 */
function ensureDomainComparison() {
  const needed = shouldRefreshDomainComparison({
    analyticsVisible: state.activeTab === 'analytics',
    stale: state.domainComparisonStale,
  });
  if (!needed) return false;

  state.domainComparisonStale = false;
  const wanted = state.filters.domain === 'all' ? journeyDomains() : journeyDomains().filter((d) => d === state.filters.domain);
  state.serviceComparison = calculateServiceComparison(state.journeys, wanted, state.exceptions);
  return true;
}

/** A new filter or refresh invalidates the rows; they rebuild on the Analytics tab. */
function invalidateDomainComparison() {
  state.domainComparisonStale = true;
  if (ensureDomainComparison()) renderAnalyticsTab();
}

/**
 * Reloads journeys, exceptions, overview and metrics for the current filters, all scoped to the
 * active company.
 */
let filterRequestGeneration = 0;

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
  if (comparisonBody) comparisonBody.innerHTML = loadingTableRow(6);
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

async function reloadJourneysForClub({ generation } = {}) {
  const stillCurrent = () => generation == null || generation === filterRequestGeneration;
  const label = document.getElementById('filter-club');
  const shown = label && label.selectedOptions[0] ? label.selectedOptions[0].textContent : state.filters.club;
  const scope = shown || domainLabel(state.filters.domain);
  try {
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
    showOverviewWarningToast(overview);
    state.dashboardMetrics = dashboardMetrics;
    invalidateDomainComparison();
    const m = state.journeysMeta || {};
    const win = m.win || headerQueryWin();
    const flow = profileLabels().flow;
    if (m.source === 'error') {
      showToast('Query failed', m.warning || `${flow}: could not load data for ${scope}.`, 'critical');
    } else if (m.rowCount === 0) {
      showToast(`${flow}: no data`, `${scope}: nothing recorded in the last ${win}.`, 'info');
    } else {
      showToast(`${flow}: updated`, `${m.rowCount} rows for last ${win}, capped at ${state.rowCap}.`, 'info');
    }
  } catch (err) {
    if (!stillCurrent()) return;
    console.error(err);
    showToast('Query failed', `Could not load data for ${scope}.`, 'critical');
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
    state.filters.club = '';
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
  const flow = profileLabels().flow;
  const cards = state.overview?.tiles?.length ? state.overview.tiles : [
    { label: 'Platform Availability', value: 'No data', meta: 'Rolling 24h average', tone: '' },
    { label: `${flow} completion`, value: 'No data', meta: 'Completed / total in the selected window', tone: '' },
    { label: 'Avg. End-to-End Response', value: 'No data', meta: `Across the selected window (${flow})`, tone: '' },
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
  const heading = document.getElementById('overview-domains-heading');
  const labels = profileLabels();
  if (heading) heading.textContent = `${labels.service} Health`;
  const health = journeyDomains().map(computeDomainHealth).filter((d) => state.filters.domain === 'all' || state.filters.domain === d.domain);
  const percent = (value) => value == null ? '—' : `${value.toFixed(2)}%`;
  const latency = (value) => value == null ? '—' : `${value} ms`;
  const count = (value) => value == null ? '—' : String(value);
  const trendIcon = { improving: '▲', degrading: '▼', stable: '→', unknown: '—' };
  const target = document.getElementById(targetId);
  if (!health.length) {
    target.innerHTML = `<div class="state-block">No ${escapeHtml(labels.service)} is defined for this company.</div>`;
    return;
  }
  target.innerHTML = health.map((d) => `
    <div class="domain-card domain-card--${normalizeStatus(d.status)}">
      <div class="domain-card__head">
        <span class="domain-card__name">${escapeHtml(domainLabel(d.domain))}</span>
        ${statusBadge(d.status)}
      </div>
      <div class="domain-card__metrics">
        <span class="domain-card__metric-label">Availability</span><span class="domain-card__metric-value">${percent(d.availability)}</span>
        <span class="domain-card__metric-label">Error rate</span><span class="domain-card__metric-value">${percent(d.errorRate)}</span>
        <span class="domain-card__metric-label">Avg latency</span><span class="domain-card__metric-value">${latency(d.latencyMs)}</span>
        <span class="domain-card__metric-label">Failed</span><span class="domain-card__metric-value">${count(d.incidents)}</span>
      </div>
      <div class="domain-card__trend">Trend: ${trendIcon[d.trend.direction]} ${escapeHtml(d.trend.direction === 'unknown' && !d.sampleSize ? 'no data' : d.trend.label)}${d.sampleSize ? ` · ${d.sampleSize} × ${escapeHtml(labels.flow)}` : ''}</div>
    </div>`).join('');
}

/** Contract v6 §4.5: widgets the overview says do not apply to this company. */
function overviewWidget(key) {
  const widgets = state.overview && Array.isArray(state.overview.widgets) ? state.overview.widgets : null;
  return widgets ? widgets.find((w) => w && w.key === key) || null : null;
}

/** A widget applies only when the per-company overview lists it as applicable. Without the list
 *  (overview unreadable) the incumbent-only widgets stay hidden rather than show another
 *  company's figures. */
function overviewWidgetApplies(key) {
  const widget = overviewWidget(key);
  return !!(widget && widget.applicable !== false);
}

/** The incumbent keeps its legacy-only blocks (Fabric business health, club inventory, mock tiles). */
function companyHasLegacyWidgets() {
  return overviewWidgetApplies('hotelBusinessHealth');
}

function renderOverviewWidgetNotes() {
  const host = document.getElementById('overview-widget-notes');
  if (!host) return;
  const widgets = state.overview && Array.isArray(state.overview.widgets) ? state.overview.widgets : [];
  const notes = widgets.filter((w) => w && w.applicable === false).map((w) => w.note || `${w.key} does not apply to this company.`);
  host.hidden = notes.length === 0;
  host.innerHTML = notes.map((note) => `<li>${escapeHtml(note)}</li>`).join('');
}

function renderOverviewProfileSections() {
  const host = document.getElementById('overview-profile');
  if (!host) return;
  const o = state.overview || {};
  const labels = profileLabels();
  const sections = [];
  if (Array.isArray(o.pillars) && o.pillars.length) {
    sections.push(`<div class="overview-block">
      <h2 class="section-heading">Observability Pillars</h2>
      <div class="overview-pillars">${o.pillars.map((p) => `
        <div class="overview-pillar overview-pillar--${escapeHtml(String(p.status || 'offline').toLowerCase())}">
          <span class="overview-pillar__name">${escapeHtml(p.pillar)}</span>
          <span class="overview-pillar__meta">${escapeHtml(String(p.status || '—'))} · ${Number(p.anomalousKpis || 0)} of ${Number(p.kpiCount || 0)} KPIs anomalous</span>
        </div>`).join('')}</div>
    </div>`);
  }
  if (Array.isArray(o.topEntities) && o.topEntities.length) {
    sections.push(`<div class="overview-block">
      <h2 class="section-heading">Most affected · ${escapeHtml(labels.entity)}</h2>
      <div class="table-wrap"><table class="data-table data-table--compact">
        <thead><tr><th>${escapeHtml(labels.entity)}</th><th>Incidents</th><th>Occurrences</th><th>Affected (${escapeHtml(labels.flow)})</th></tr></thead>
        <tbody>${o.topEntities.map((e) => `<tr><td>${escapeHtml(e.key)}</td><td>${Number(e.incidents || 0).toLocaleString()}</td><td>${Number(e.occurrences || 0).toLocaleString()}</td><td>${Number(e.affectedJourneys || 0).toLocaleString()}</td></tr>`).join('')}</tbody>
      </table></div>
    </div>`);
  }
  if (Array.isArray(o.investigations) && o.investigations.length) {
    sections.push(`<div class="overview-block">
      <h2 class="section-heading">Triage investigations</h2>
      <ul class="simple-list">${o.investigations.slice(0, 5).map((i) => `<li><strong>${escapeHtml(i.pillar)} · ${escapeHtml(i.kpi)}</strong> — ${escapeHtml(String(i.status || ''))}${i.domain ? ` · ${escapeHtml(domainLabel(i.domain))}` : ''} · ${escapeHtml(formatRelativeTime(i.createdAt))}${i.summary ? `<div class="hint">${escapeHtml(i.summary)}</div>` : ''}</li>`).join('')}</ul>
    </div>`);
  }
  host.innerHTML = sections.join('');
}

function renderEventTimeline() {
  const section = document.getElementById('overview-timeline-section');
  // The timeline is still the incumbent's sample feed (data/events.json); another company never sees it.
  const applies = companyHasLegacyWidgets();
  if (section) section.hidden = !applies;
  if (!applies) return;
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
  renderDomainCards('overview-domains');
  renderOverviewProfileSections();
  renderEventTimeline();
  renderOverviewWidgetNotes();
}

/* -----------------------------------------------------------------------
   5a-bis. AGENT PIPELINE (Simian Army)
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
  const host = document.getElementById('agents-pipeline');
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
      ? `${formatCount(cycle.boardRowsEvaluated || 0)} rows · ${entityTally(cycle.clubsWatched)}`
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
        detail: `${formatCount(cycle.boardRowsEvaluated || 0)} rows · ${entityTally(cycle.clubsWatched)} · ${formatCount(cycle.candidatesFound || 0)} above threshold`,
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
  return j[key] ?? '';
}

/** Column titles keep the company's own word intact ("# Paso", not "Paso count"/"Last paso"). */
function countHeading(label) {
  return `# ${label}`;
}

/** Contract v6 §6: column titles come from the active company's labels. */
function journeyColumns() {
  const l = profileLabels();
  return [
    { key: 'correlationId', label: `${l.flow} ID` },
    { key: 'domain', label: l.service },
    { key: 'flow', label: l.flow },
    { key: 'entity', label: l.entity },
    { key: 'startDate', label: 'Started' },
    { key: 'status', label: 'Status' },
    { key: 'stepCount', label: countHeading(l.step), title: `How many ${l.step} entries the journey recorded` },
    { key: 'lastStepKey', label: l.step, title: `The last ${l.step} the journey reached` },
    { key: 'totalDurationMs', label: 'Duration' },
    { key: 'timestamp', label: 'Last seen' },
  ];
}

function renderJourneysHead() {
  const head = document.getElementById('journeys-thead');
  if (!head) return;
  head.innerHTML = `<tr>${journeyColumns().map((c) => `<th data-sort="${escapeHtml(c.key)}"${c.title ? ` title="${escapeHtml(c.title)}"` : ''}>${escapeHtml(c.label)}</th>`).join('')}</tr>`;
  const desc = document.getElementById('journeys-desc');
  if (desc) {
    const names = profileServices().map((s) => s.label);
    desc.textContent = names.length
      ? `End-to-end transaction explorer across ${names.join(', ')}.`
      : 'End-to-end transaction explorer.';
  }
  const idLabel = document.getElementById('filter-id-label');
  if (idLabel) idLabel.textContent = `${profileLabels().flow} ID`;
}

/** The header is rebuilt per company, so sorting is delegated to the table rather than bound per cell. */
function wireJourneysSort() {
  const table = document.getElementById('journeys-table');
  if (!table) return;
  table.addEventListener('click', (e) => {
    const th = e.target.closest('thead th[data-sort]');
    if (!th) return;
    const key = th.dataset.sort;
    const cur = state.sort.journeys;
    if (cur.key === key) cur.dir = cur.dir === 'asc' ? 'desc' : 'asc';
    else { cur.key = key; cur.dir = 'asc'; }
    renderJourneysTable();
  });
}

function renderJourneysTable() {
  renderJourneysHead();
  if (state.tablesLoading) return;
  markTableBusy(document.getElementById('journeys-table'), false);
  const filtered = state.journeys.filter(matchesGlobalFilters);
  const { key, dir } = state.sort.journeys;
  const rows = sortRows(filtered, key, dir, journeySortAccessor);
  const tbody = document.getElementById('journeys-tbody');

  if (rows.length === 0) {
    const meta = state.journeysMeta || {};
    const message = meta.source === 'error' && meta.warning ? meta.warning : `${profileLabels().flow}: nothing matches the current filters.`;
    tbody.innerHTML = `<tr><td colspan="10"><div class="state-block">${escapeHtml(message)}</div></td></tr>`;
    return;
  }

  tbody.innerHTML = rows.map((j) => `
    <tr data-journey-id="${escapeHtml(j.id)}" tabindex="0" role="button" aria-label="View ${escapeHtml(profileLabels().flow.toLowerCase())} ${escapeHtml(j.correlationId || j.id)} details">
      <td><code>${escapeHtml(j.correlationId || '—')}</code></td>
      <td>${escapeHtml(domainLabel(j.domain))}</td>
      <td>${escapeHtml(profileFlowLabel(j.domain, j.flow) || '—')}</td>
      <td>${escapeHtml(j.entity || '—')}</td>
      <td>${j.startDate ? new Date(j.startDate).toLocaleString() : '—'}</td>
      <td>${journeyStatusBadge(j.status, j.outcome)}</td>
      <td>${j.stepCount || '—'}</td>
      <td>${escapeHtml(profileStepLabel(j.domain, j.lastStepKey) || '—')}</td>
      <td>${formatDuration(j.totalDurationMs)}</td>
      <td>${formatRelativeTime(j.timestamp)}</td>
    </tr>`).join('');

  tbody.querySelectorAll('tr[data-journey-id]').forEach((row) => {
    const open = () => openJourneyDetail(row.dataset.journeyId);
    row.addEventListener('click', open);
    row.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
  });
}

function journeyStatusBadge(status, outcome) {
  const cls = mapOutcomeToHealth(outcome || status);
  return `<span class="status-badge status-badge--${cls}">${escapeHtml(status)}</span>`;
}

/** Contract v6 §4.1: the side panel reads the journey and its steps from GET /api/profile-journeys/{id}. */
async function openJourneyDetail(journeyId) {
  const journey = state.journeys.find((j) => j.id === journeyId);
  if (!journey) return;
  const labels = profileLabels();
  const title = `${labels.flow} ${journey.correlationId || journey.id}`;
  const company = activeCompanyKey();
  openSidePanel(title, '<div class="state-block" role="status"><div class="spinner" aria-hidden="true"></div>Loading…</div>');
  let detail;
  try {
    detail = await fetchJsonPayload(withActiveCompany(`/api/profile-journeys/${encodeURIComponent(journey.id)}`));
  } catch (err) {
    if (activeCompanyKey() !== company) return;
    openSidePanel(title, `<div class="state-block state-block--error">${escapeHtml(err.message || `${labels.flow} could not be read.`)}</div>`);
    return;
  }
  if (activeCompanyKey() !== company) return;
  openSidePanel(title, journeyDetailHtml(detail ? mapProfileJourney(detail.journey || {}) : journey, (detail && detail.steps) || [], labels));
  scrollDetailsToTop();
}

function journeyDetailHtml(journey, rawSteps, labels) {
  const steps = [...rawSteps].sort((a, b) => (Number(a.stepOrder) || 0) - (Number(b.stepOrder) || 0)
    || new Date(a.timestamp) - new Date(b.timestamp));
  const times = steps.map((s) => new Date(s.timestamp).getTime()).filter((t) => !Number.isNaN(t));
  const t0 = times.length ? Math.min(...times) : 0;
  const ends = steps.map((s) => (new Date(s.timestamp).getTime() - t0) + (Number(s.durationMs) || 0));
  const totalMs = Math.max(1, ...ends, journey.totalDurationMs || 0);

  const waterfallRows = steps.map((s) => {
    const offset = Math.max(0, new Date(s.timestamp).getTime() - t0);
    const leftPct = (offset / totalMs) * 100;
    const widthPct = Math.max(((Number(s.durationMs) || 0) / totalMs) * 100, 0.6);
    return `
      <div class="waterfall-row">
        <div class="waterfall-row__label">
          <span class="waterfall-row__name">${escapeHtml(profileStepLabel(journey.domain, s.stepKey) || s.name)}</span>
          <span class="waterfall-row__component">${escapeHtml(s.name || '')}</span>
        </div>
        <div class="waterfall-row__track">
          <div class="waterfall-row__bar ${s.success === false ? 'waterfall-row__bar--failed' : ''}" style="left:${leftPct}%; width:${widthPct}%;"></div>
        </div>
        <div class="waterfall-row__duration">${formatDuration(Math.round(Number(s.durationMs) || 0))}</div>
      </div>`;
  }).join('');

  const stepRows = steps.map((s) => `
    <tr class="${s.success === false ? 'is-failed' : ''}">
      <td>${escapeHtml(String(s.stepOrder ?? '—'))}</td>
      <td>${escapeHtml(profileStepLabel(journey.domain, s.stepKey) || '—')}</td>
      <td>${escapeHtml(s.name || '—')}</td>
      <td>${s.timestamp ? new Date(s.timestamp).toLocaleString() : '—'}</td>
      <td>${formatDuration(Math.round(Number(s.durationMs) || 0))}</td>
      <td>${escapeHtml(s.resultCode || '—')}</td>
      <td>${s.success === false ? 'Failed' : 'OK'}</td>
    </tr>`).join('');

  return `
    <div class="journey-summary">
      <div class="journey-summary__item"><strong>${journeyStatusBadge(journey.status, journey.outcome)}</strong>Outcome</div>
      <div class="journey-summary__item"><strong>${escapeHtml(domainLabel(journey.domain))}</strong>${escapeHtml(labels.service)}</div>
      <div class="journey-summary__item"><strong>${escapeHtml(profileFlowLabel(journey.domain, journey.flow) || '—')}</strong>${escapeHtml(labels.flow)}</div>
      <div class="journey-summary__item"><strong>${escapeHtml(journey.entity || '—')}</strong>${escapeHtml(labels.entity)}</div>
      <div class="journey-summary__item"><strong>${formatDuration(journey.totalDurationMs)}</strong>Duration</div>
      <div class="journey-summary__item"><strong>${journey.stepCount || steps.length}</strong>${escapeHtml(countHeading(labels.step))}</div>
      <div class="journey-summary__item"><strong>${escapeHtml(journey.correlationId || journey.id)}</strong>Operation</div>
    </div>
    <h3>Waterfall · ${escapeHtml(labels.step)}</h3>
    <p class="hint">Bars show each request's measured duration from the start of the first request.</p>
    <div class="waterfall" id="journey-waterfall">${waterfallRows || `<div class="state-block">${escapeHtml(labels.step)}: none recorded.</div>`}</div>
    <h3>Requests</h3>
    <div class="table-wrap trace-table-wrap">
      <table class="data-table data-table--compact">
        <thead><tr><th>#</th><th>${escapeHtml(labels.step)}</th><th>Request</th><th>Time</th><th>Duration</th><th>Result</th><th>Outcome</th></tr></thead>
        <tbody>${stepRows || `<tr><td colspan="7">${escapeHtml(labels.step)}: none recorded.</td></tr>`}</tbody>
      </table>
    </div>`;
}

/* -----------------------------------------------------------------------
   5c. PLATFORM HEALTH TAB
   ----------------------------------------------------------------------- */
function renderComponentGrid() {
  const grid = document.getElementById('component-grid');
  if (!grid) return;
  const filtered = state.components.filter(matchesGlobalFilters);
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

/* Sample figures and click rules copied from arrivia_reliability_watch_v6.html
   at observation 0. Vertical click selects that product and returns the journey
   to Reserve. Stage click only replaces the component cards. */
const rwWatch = {
  ready: false,
  /** Hour row from dbo.vw_Alert_HotelBookingKPIs, plus start-to-complete conversion from hotel.Journey. The Domain and Club filters do not scope this. Null when that read is unavailable. */
  hotelBusiness: null,
  /** Hotels journeys and incidents for Application Health. Domain and Club filters do not scope this. */
  application: null,
  applicationRequest: 0,
  /** GET /api/health/pillars for the selected card's domain: { domain, status, pillars }. */
  health: null,
  healthRequest: 0,
  /** The pillar load currently running: { domain, promise }. */
  healthInFlight: null,
  /** GET /api/settings/kpis - names each KPI's aggregation and its "n of m" consecutive rule. */
  kpiDefs: null,
  /** GET /api/health/kpis/{id}/buckets by KpiDefinitionId: { status, items }. */
  buckets: {},
  /** GET /api/triage/investigations: { status, items }. */
  triage: null,
  triageRequest: 0,
  triageRunning: false,
  healthTimer: null,
  /** Service key of the selected card. The cards are the active company's services. */
  focus: null,
  stage: 1,
  /** Incumbent-only sample figures by service key (shown only while its legacy widgets apply).
   *  Its live business service replaces these with the live hour row and journey conversion. */
  base: {
    hotels: { conversion: [11.3, 11.1], bookings: [128, 127], revenue: [28600, 28300], latency: [420, 425], exceptions: [2, 2], cpu: [42, 43], memory: [61, 60], releases: 3, known: 1 },
    cruises: { conversion: [7.8, 7.9], bookings: [46, 47], revenue: [73400, 74600], latency: [600, 615], exceptions: [1, 1], cpu: [43, 44], memory: [59, 58], releases: 2, known: 0 },
    air: { conversion: [6.5, 6.4], bookings: [186, 181], revenue: [38700, 38100], latency: [410, 416], exceptions: [3, 3], cpu: [51, 51], memory: [68, 69], releases: 5, known: 2 },
    cars: { conversion: [9.2, 9.3], bookings: [94, 95], revenue: [12600, 12750], latency: [350, 349], exceptions: [1, 1], cpu: [38, 39], memory: [48, 49], releases: 1, known: 0 },
  },
  /** The incumbent's live business-health service and its inert one. */
  liveBusinessService: 'hotels',
  offlineService: 'air',
  stageLabels: ['Search & Quote', 'Reserve', 'Pay', 'Confirm', 'Modify / Cancel'],
  components: [
    ['Web / Mobile', 'Search API', 'Supplier Search · Expedia / Hotelbeds', 'Search cache'],
    ['Booking App', 'Booking API', 'Reservation Service', 'Partner API · Expedia / Hotelbeds'],
    ['Checkout App', 'Payment API', 'Payment gateway', 'Booking database'],
    ['Confirmation API', 'Notification Service', 'Messaging queue', 'Supplier adapter'],
    ['Manage Booking API', 'Cancellation Service', 'Supplier API', 'Booking database'],
  ],
  icons: {
    hotels: '<path d="M4 21V6l8-4 8 4v15M9 21v-6h6v6M8 9h1m6 0h1m-8 3h1m6 0h1"/>',
    cruises: '<path d="M3 14h18l-3 7H6l-3-7Zm5 0V5h8v9M12 5V2M3 10h18"/>',
    air: '<path d="M2 20 22 10l-8-2-3-6-3 11-6 7ZM8 13l5 7"/>',
    cars: '<path d="M5 16 7 9h10l2 7M4 16h16v5h-3v-2H7v2H4v-5ZM7 13h2m6 0h2"/>',
    default: '<path d="M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z"/>',
  },
};

/** The selected card's service name, from the active profile. */
function rwFocusLabel() {
  return rwWatch.focus ? domainLabel(rwWatch.focus) : '—';
}

/** Keeps the selected card on a service of the active company (the first one after a switch). */
function rwEnsureFocus() {
  // Until the company's overview says which widgets apply, the live business card is unknown.
  if (!state.overview) return;
  const services = profileServices().filter((s) => !rwVerticalOffline(s.key));
  if (services.some((s) => s.key === rwWatch.focus)) return;
  const live = companyHasLegacyWidgets() && services.find((s) => s.key === rwWatch.liveBusinessService);
  rwWatch.focus = live ? live.key : (services[0] ? services[0].key : null);
  rwWatch.stage = 1;
}

function rwFmt(key, value) {
  if (key === 'conversion') return `${value.toFixed(1)}%`;
  if (key === 'revenue') return `$${Math.round(value).toLocaleString()}`;
  if (key === 'latency') return `${Math.round(value)} ms`;
  if (key === 'cpu' || key === 'memory') return `${Math.round(value)}%`;
  return Math.round(value).toLocaleString();
}

function rwPercent(current, previous) {
  return previous ? ((current - previous) / previous) * 100 : 0;
}

function rwTrend(current, previous, higherBetter) {
  const pct = rwPercent(current, previous);
  if (Math.abs(pct) < 1) return { cls: 'flat', glyph: '→', label: 'Unchanged' };
  const up = pct > 0;
  const good = up === higherBetter;
  if (up) return { cls: good ? 'up-good' : 'up', glyph: '▲', label: 'Up' };
  return { cls: good ? 'down-good' : 'down', glyph: '▼', label: 'Down' };
}

function rwIcon(key) {
  return `<span class="rw-ico" aria-hidden="true"><svg viewBox="0 0 24 24">${rwWatch.icons[key] || rwWatch.icons.default}</svg></span>`;
}

/** Live Business Health exists only for the incumbent's business service. Domain and Club do not choose it. */
function rwHotelsQueryMode() {
  return companyHasLegacyWidgets() && rwWatch.focus === rwWatch.liveBusinessService;
}

/** Every other service has no business-health source yet. */
function rwPlaceholderDomain() {
  return !rwHotelsQueryMode();
}

function rwUnavailableMetric(name) {
  return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><strong class="rw-muted">— Unavailable</strong></span></li>`;
}

function rwLivePair(key) {
  const live = rwWatch.hotelBusiness;
  if (!live || !Array.isArray(live[key])) return null;
  const pair = live[key];
  if (pair[0] == null && pair[1] == null) return null;
  return pair;
}

function rwShown(key, value) {
  return value == null ? '—' : rwFmt(key, Number(value));
}

/** Last week when that hour had booking starts. An earlier same weekday is named by its date. */
function rwConversionBaselinePhrase() {
  const lastWeek = { short: 'same hour last week', relation: "last week's same hour" };
  const start = rwWatch.hotelBusiness && rwWatch.hotelBusiness.priorConversionWindowStart;
  if (!start) return lastWeek;
  const prior = new Date(start);
  if (Number.isNaN(prior.getTime())) return lastWeek;
  const days = Math.round((Date.now() - prior.getTime()) / 86400000);
  if (days >= 6 && days <= 8) return lastWeek;
  const date = prior.toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'America/Los_Angeles' });
  return { short: `same hour on ${date}`, relation: `the same hour on ${date}` };
}

function rwConversionDelta(now, old, live) {
  const baseline = live
    ? rwConversionBaselinePhrase()
    : { short: 'same hour last week', relation: "last week's same hour" };
  if (now == null || old == null) {
    return `<p class="rw-delta rw-delta--flat">Conversion · ${baseline.short}</p>`;
  }
  const current = Number(now);
  const previous = Number(old);
  if (previous === 0) {
    return `<p class="rw-delta rw-delta--flat">Conversion · ${baseline.short}</p>`;
  }
  const trend = rwTrend(current, previous, true);
  const relation = current < previous ? 'below' : current > previous ? 'above' : 'at';
  return `<p class="rw-delta rw-delta--${trend.cls}">Conversion · ${Math.abs(rwPercent(current, previous)).toFixed(1)}% ${relation} ${baseline.relation}</p>`;
}

function rwConversionPair(now, old, live) {
  return `<div class="rw-pair">
        <div><span class="rw-pair__label">Current</span><span class="rw-pair__value">${rwShown('conversion', now)}</span></div>
        <div><span class="rw-pair__label">Historical</span><span class="rw-pair__value">${rwShown('conversion', old)}</span></div>
      </div>
      ${rwConversionDelta(now, old, live)}`;
}

/** Cancellations is not wired to a business-health read yet. */
function rwCancellationMetric() {
  return rwUnavailableMetric('Cancellations');
}

/** The incumbent's inert service keeps its card visible but not selectable. */
function rwVerticalOffline(key) {
  return companyHasLegacyWidgets() && key === rwWatch.offlineService;
}

const RW_UNAVAILABLE_PAIR = `<div class="rw-pair">
        <div><span class="rw-pair__label">Current</span><span class="rw-pair__value">—</span></div>
        <div><span class="rw-pair__label">Historical</span><span class="rw-pair__value">—</span></div>
      </div>
      <p class="rw-delta rw-delta--flat">Conversion unavailable</p>`;

/** One card per service of the active company. */
function rwRenderVerticals() {
  const root = document.getElementById('rw-verticals');
  const services = profileServices();
  if (!services.length) {
    root.innerHTML = `<div class="state-block">No ${escapeHtml(profileLabels().service)} is defined for this company.</div>`;
    return;
  }
  const legacy = companyHasLegacyWidgets();
  root.innerHTML = services.map(({ key, label }) => {
    const offline = rwVerticalOffline(key);
    const selected = !offline && key === rwWatch.focus;
    const hotelLive = legacy && key === rwWatch.liveBusinessService;
    const sample = legacy ? rwWatch.base[key] : null;
    const live = hotelLive ? rwLivePair('conversion') : null;
    let pair;
    if (live) pair = rwConversionPair(live[1], live[0], true);
    else if (!hotelLive && sample) pair = rwConversionPair(sample.conversion[1], sample.conversion[0], false);
    else pair = RW_UNAVAILABLE_PAIR;
    const status = offline
      ? '<span class="rw-pill rw-pill--off">Offline</span>'
      : '<span class="rw-pill rw-pill--ok">Monitoring</span>';
    const tag = offline ? 'div' : 'button';
    const attrs = offline ? '' : ` type="button" data-rw-vertical="${escapeHtml(key)}" aria-pressed="${selected}"`;
    return `<${tag} class="rw-vertical${selected ? ' is-selected' : ''}${offline ? ' rw-vertical--offline' : ''}"${attrs}>
      <header class="rw-vertical__head">
        <h3 class="rw-vertical__name">${rwIcon(key)} ${escapeHtml(label)}</h3>
        ${status}
      </header>
      ${pair}
    </${tag}>`;
  }).join('');
}

/** The selected card is a service key of the active company. Domain and Club filters do not choose this. */
function rwApplicationDomain() {
  return rwWatch.focus || null;
}

function rwApplicationKey() {
  return `${activeCompanyKey()}|${rwApplicationDomain() || 'none'}|${state.filters.environment || 'production'}|${headerQueryWin()}`;
}

/** Incidents for the selected service, every entity. The muted number is the last 7 days. The window is a subset of it, so no health direction is shown.
 *  A failed read is Unavailable. Zero is a successful read that found none. */
function rwExceptionsMetric(name) {
  const app = rwWatch.application;
  const scope = rwFocusLabel();
  const every = `every ${profileLabels().entity.toLowerCase()}`;
  if (!app || app.status === 'loading') {
    return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><strong class="rw-muted">—</strong></span></li>`;
  }
  if (app.status !== 'ready' || !app.exceptionsOk) return rwUnavailableMetric(name);
  const count = app.count;
  const baseline = app.baseline;
  if (baseline == null) {
    return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><span class="rw-was">—</span> <strong>${count.toLocaleString()}</strong></span></li>`;
  }
  return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><span class="rw-was" title="${escapeHtml(scope)} incidents in the last 7 days, ${escapeHtml(every)}">${baseline.toLocaleString()}</span> <strong title="${escapeHtml(scope)} incidents in the selected window, ${escapeHtml(every)}">${count.toLocaleString()}</strong></span></li>`;
}

/** Incidents for the selected service, with no entity filter.
 *  Time range and environment still apply. Latency is not read here:
 *  the Application Health KPIs come from the backend monitors (GET /api/health/pillars). */
async function loadApplicationHealth() {
  const domain = rwApplicationDomain();
  // No selectable service: leave the readings in place.
  if (!domain) return;
  const key = rwApplicationKey();
  if (rwWatch.application && rwWatch.application.key === key && rwWatch.application.status === 'loading') return;
  const request = (rwWatch.applicationRequest || 0) + 1;
  rwWatch.applicationRequest = request;
  const env = state.filters.environment || 'production';
  const win = headerQueryWin();
  rwWatch.application = { key, status: 'loading', exceptions: [], count: 0, baseline: null, exceptionsOk: false };
  const exceptionQs = new URLSearchParams({ win, domain, env });
  const [exceptionsResult] = await Promise.allSettled([
    fetchJsonPayload(withActiveCompany(`/api/exceptions?${exceptionQs}`)),
  ]);
  if (rwWatch.applicationRequest !== request) return;
  if (rwApplicationKey() !== key) {
    rwWatch.application = null;
    return;
  }

  const sameEnv = (payload) => String(payload.environment || '').toLowerCase() === env.toLowerCase();
  let exceptions = [];
  let count = 0;
  let baseline = null;
  let exceptionsOk = false;
  if (exceptionsResult.status === 'fulfilled' && (exceptionsResult.value.source || '') === 'sql'
    && sameEnv(exceptionsResult.value)) {
    exceptionsOk = true;
    exceptions = exceptionsResult.value.exceptions || [];
    const windowCounts = exceptionsResult.value.windowCounts;
    count = windowCounts && windowCounts[domain] != null ? Number(windowCounts[domain]) : 0;
    const counts = exceptionsResult.value.baselineCounts;
    baseline = counts && typeof counts === 'object'
      ? (counts[domain] == null ? 0 : Number(counts[domain]))
      : null;
  }
  rwWatch.application = { key, status: 'ready', exceptions, count, baseline, exceptionsOk };
  if (rwWatch.ready && rwWatch.applicationRequest === request) rwRender();
}

function rwMetric(key, name, higherBetter) {
  if (key === 'exceptions') return rwExceptionsMetric(name);
  const businessKey = key === 'conversion' || key === 'bookings' || key === 'revenue';
  if (businessKey && rwPlaceholderDomain()) return rwUnavailableMetric(name);
  if (rwHotelsQueryMode() && businessKey) {
    const pair = rwLivePair(key);
    if (!pair) return rwUnavailableMetric(name);
    const now = pair[1];
    const old = pair[0];
    if (now == null || old == null) {
      return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><span class="rw-was">${escapeHtml(rwShown(key, old))}</span> <strong>${escapeHtml(rwShown(key, now))}</strong></span></li>`;
    }
    const trend = rwTrend(Number(now), Number(old), higherBetter);
    return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><span class="rw-was">${escapeHtml(rwShown(key, old))}</span> <strong>${escapeHtml(rwShown(key, now))}</strong> <span class="rw-dir rw-dir--${trend.cls}" aria-label="${trend.label}">${trend.glyph}</span></span></li>`;
  }
  const sample = rwWatch.base[rwWatch.focus];
  if (!sample || !sample[key]) return rwUnavailableMetric(name);
  const now = sample[key][1];
  const old = sample[key][0];
  const trend = rwTrend(now, old, higherBetter);
  return `<li><span>${escapeHtml(name)}</span><span class="rw-metrics__vals"><span class="rw-was">${escapeHtml(rwFmt(key, old))}</span> <strong>${escapeHtml(rwFmt(key, now))}</strong> <span class="rw-dir rw-dir--${trend.cls}" aria-label="${trend.label}">${trend.glyph}</span></span></li>`;
}

/** How many recent booking starts the Business Health conversion chart draws. */
const RW_CONVERSION_SAMPLE = 10;

/* --- Backend pillar health (Spec 1000) ---------------------------------------------------
   The health monitors run in JourneyMonitor.Functions on timers; the portal only renders what
   they stored. Nothing below decides whether a reading is anomalous. */

/** Pillars the backend monitors evaluate, in card order (KpiPillar, camelCase on the wire). */
const RW_BACKEND_PILLARS = ['business', 'application', 'infrastructure'];
const RW_PILLAR_DISPLAY = { business: 'Business Health', application: 'Application Health', infrastructure: 'Infrastructure Health' };
/** Pillar cards that have no backend monitor and always render as offline. */
const RW_STATIC_OFFLINE_PILLARS = 3;
const RW_HEALTH_REFRESH_MS = 60000;
const RW_BUCKET_HOURS = 24;
const RW_TRIAGE_TOP = 20;
/** The stored bucket field each KpiAggregation is evaluated on. */
const RW_BUCKET_FIELD = { p95: 'p95', p50: 'p50', mean: 'mean', count: 'totalCount', value: 'value' };

/** The selected service card picks the domain the pillars are read for. */
function rwHealthDomain() {
  return rwApplicationDomain() || journeyDomains()[0] || '';
}

function rwBackendPillar(key) {
  const health = rwWatch.health;
  // A response for the previously selected vertical is never shown under the new one.
  if (!health || health.status !== 'ready' || health.domain !== rwHealthDomain()) return null;
  return health.pillars.find((pillar) => String(pillar.pillar || '').toLowerCase() === key) || null;
}

/** True while the pillars on screen belong to another domain or have not loaded yet. */
function rwHealthPending() {
  const health = rwWatch.health;
  return !health || health.status === 'loading' || health.domain !== rwHealthDomain();
}

function rwKpiDefinition(id) {
  return (rwWatch.kpiDefs || []).find((definition) => definition.kpiDefinitionId === id) || null;
}

function rwBucketValue(bucket, aggregation) {
  if (!bucket) return null;
  const field = RW_BUCKET_FIELD[String(aggregation || '').toLowerCase()] || 'value';
  const raw = bucket[field];
  return raw == null ? null : Number(raw);
}

/** Percentile and mean buckets are latencies in milliseconds; seconds read better past 1000 ms. */
function rwKpiFmt(value, aggregation) {
  if (value == null || !Number.isFinite(value)) return '—';
  const agg = String(aggregation || '').toLowerCase();
  if (agg === 'p95' || agg === 'p50' || agg === 'mean') {
    return value >= 1000 ? `${(value / 1000).toFixed(1)} s` : `${Math.round(value)} ms`;
  }
  if (agg === 'count') return Math.round(value).toLocaleString();
  return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

function rwClock(iso) {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function rwKpiName(kpi) {
  return kpi.stage ? `${kpi.kpi} · ${kpi.stage}` : kpi.kpi;
}

function rwPillarStatusPill(key) {
  if (rwHealthPending()) return '<span class="rw-pill rw-pill--idle">Loading</span>';
  const pillar = rwBackendPillar(key);
  if (!pillar) return '<span class="rw-pill rw-pill--off">Unavailable</span>';
  if (pillar.status === 'anomalous') return '<span class="rw-pill rw-pill--alert">Anomalous</span>';
  if (pillar.status === 'ok') return '<span class="rw-pill rw-pill--ok">OK</span>';
  return '<span class="rw-pill rw-pill--off">Offline</span>';
}

/** One KPI as the backend left it: latest bucket value and its consecutive-bucket counter "n of m". */
function rwKpiRow(kpi) {
  const name = escapeHtml(rwKpiName(kpi));
  if (!kpi.enabled) {
    return `<li><span>${name}</span><span class="rw-metrics__vals"><strong class="rw-muted">— Disabled</strong></span></li>`;
  }
  const definition = rwKpiDefinition(kpi.kpiDefinitionId);
  const aggregation = definition ? definition.aggregation : null;
  const bucket = kpi.latestBucket;
  const value = rwKpiFmt(rwBucketValue(bucket, aggregation), aggregation);
  const count = Number((kpi.state && kpi.state.consecutiveCount) || 0);
  const counter = definition ? `${count} of ${definition.consecutiveCount}` : String(count);
  const valueTitle = bucket
    ? `Latest bucket ${new Date(bucket.bucketStart).toLocaleString()} · ${Number(bucket.totalCount).toLocaleString()} observations`
    : 'No bucket ingested yet';
  const tag = aggregation ? ` <span class="rw-scope">${escapeHtml(String(aggregation).toUpperCase())}</span>` : '';
  return `<li class="rw-kpi${count > 0 ? ' rw-kpi--flagged' : ''}"><span>${name}${tag}</span><span class="rw-metrics__vals"><span class="rw-was" title="Consecutive anomalous buckets counted by the backend monitor">${escapeHtml(counter)}</span> <strong title="${escapeHtml(valueTitle)}">${escapeHtml(value)}</strong></span></li>`;
}

function rwKpiNotes(kpi) {
  const notes = [];
  const kpiState = kpi.state || {};
  if (kpiState.cooldownUntil && new Date(kpiState.cooldownUntil).getTime() > Date.now()) {
    notes.push(`${rwKpiName(kpi)} cooldown until ${rwClock(kpiState.cooldownUntil)}`);
  }
  if (kpi.lastAnomaly) {
    notes.push(`${rwKpiName(kpi)} last anomaly ${formatRelativeTime(kpi.lastAnomaly.detectedAt)}: ${kpi.lastAnomaly.detection}`);
  }
  return notes;
}

/** Bars are the stored bucket values. The reference line and the flagged bar are drawn only from
 *  the backend's last anomaly for this KPI. */
function rwKpiChart(kpi) {
  const definition = rwKpiDefinition(kpi.kpiDefinitionId);
  const aggregation = definition ? definition.aggregation : null;
  const heading = `${rwKpiName(kpi)} · last ${RW_BUCKET_HOURS} h`;
  const entry = rwWatch.buckets[kpi.kpiDefinitionId];
  const empty = (text) => `<p class="rw-chart__label"><span>${escapeHtml(heading)}</span><span class="rw-muted">—</span></p>
      <div class="rw-chart-empty"><p>${escapeHtml(text)}</p></div>`;
  if (!entry || entry.status === 'loading') return empty('Loading buckets');
  if (entry.status !== 'ready') return empty('Buckets unavailable');
  const points = entry.items.map((bucket) => ({ start: bucket.bucketStart, value: rwBucketValue(bucket, aggregation) }));
  const values = points.map((point) => point.value).filter((value) => value != null && Number.isFinite(value));
  if (values.length === 0) return empty('No buckets in this window');

  const anomaly = kpi.lastAnomaly;
  const reference = anomaly && Number.isFinite(Number(anomaly.referenceValue)) ? Number(anomaly.referenceValue) : null;
  const anomalyStart = anomaly ? new Date(anomaly.bucketStart).getTime() : null;
  const top = Math.max(...values, reference ?? 0) || 1;
  const bars = points.map((point) => {
    if (point.value == null || !Number.isFinite(point.value)) return '<span class="is-empty" style="height:100%"></span>';
    const height = Math.max(4, (point.value / top) * 100);
    const flagged = anomalyStart != null && new Date(point.start).getTime() === anomalyStart;
    const title = `${new Date(point.start).toLocaleString()} · ${rwKpiFmt(point.value, aggregation)}`;
    return `<span${flagged ? ' class="is-spike"' : ''} style="height:${height.toFixed(1)}%" title="${escapeHtml(title)}"></span>`;
  }).join('');
  const line = reference != null
    ? `<i class="rw-bars__line" style="bottom:${Math.min(100, (reference / top) * 100).toFixed(1)}%"><b>ref ${escapeHtml(rwKpiFmt(reference, aggregation))}</b></i>`
    : '';
  const latest = points[points.length - 1].value;
  return `<p class="rw-chart__label"><span>${escapeHtml(heading)}</span><span>${escapeHtml(rwKpiFmt(latest, aggregation))}</span></p>
    <div class="rw-bars rw-bars--dense" role="img" aria-label="${escapeHtml(`${heading}, ${values.length} buckets`)}">${line}${bars}</div>`;
}

function rwBackendKpiRows(key) {
  const health = rwWatch.health;
  if (rwHealthPending()) {
    return '<li><span>Monitored KPIs</span><span class="rw-metrics__vals"><strong class="rw-muted">—</strong></span></li>';
  }
  if (health.status !== 'ready') return rwUnavailableMetric('Monitored KPIs');
  const pillar = rwBackendPillar(key);
  const kpis = pillar ? pillar.kpis || [] : [];
  if (kpis.length === 0) {
    return '<li><span>Monitored KPIs</span><span class="rw-metrics__vals"><strong class="rw-muted">— None configured</strong></span></li>';
  }
  return kpis.map(rwKpiRow).join('');
}

function rwBackendKpiCharts(key) {
  const pillar = rwBackendPillar(key);
  return pillar ? (pillar.kpis || []).filter((kpi) => kpi.enabled).map(rwKpiChart).join('') : '';
}

function rwBackendNote(key, lead) {
  const pillar = rwBackendPillar(key);
  const notes = pillar ? (pillar.kpis || []).flatMap(rwKpiNotes) : [];
  return [lead, ...notes].filter(Boolean).map(escapeHtml).join(' · ');
}

function rwRenderPillarCount() {
  const el = document.getElementById('rw-pillars-count');
  if (!el) return;
  const health = rwWatch.health;
  if (rwHealthPending() || health.status !== 'ready') {
    el.innerHTML = `<span class="rw-muted">— active</span> · ${RW_STATIC_OFFLINE_PILLARS} offline`;
    return;
  }
  const statuses = RW_BACKEND_PILLARS.map((key) => (rwBackendPillar(key) || {}).status || 'offline');
  const active = statuses.filter((status) => status !== 'offline').length;
  const anomalous = statuses.filter((status) => status === 'anomalous').length;
  const offline = RW_BACKEND_PILLARS.length - active + RW_STATIC_OFFLINE_PILLARS;
  el.innerHTML = `<span class="rw-count--ok">${active} active</span>${anomalous ? ` · <span class="rw-count--alert">${anomalous} anomalous</span>` : ''} · ${offline} offline`;
}

/** One load per domain at a time: a call while the same domain is already loading joins it, so
 *  the interval, visibilitychange and a card click can never stack duplicate reads. */
function loadPillarHealth() {
  const domain = rwHealthDomain();
  const inFlight = rwWatch.healthInFlight;
  if (inFlight && inFlight.domain === domain) return inFlight.promise;
  const promise = rwLoadPillarHealth(domain).finally(() => {
    if (rwWatch.healthInFlight && rwWatch.healthInFlight.promise === promise) rwWatch.healthInFlight = null;
  });
  rwWatch.healthInFlight = { domain, promise };
  return promise;
}

/** Called on every Platform Health redraw: reads the backend only when the visible tab shows a
 *  domain that was never loaded (nothing is fetched while another tab is active). */
function rwEnsureBackendDomain() {
  if (!rwHealthTabVisible()) return;
  const health = rwWatch.health;
  if (!health || health.domain !== rwHealthDomain()) loadPillarHealth();
}

async function rwLoadPillarHealth(domain) {
  const request = rwWatch.healthRequest + 1;
  rwWatch.healthRequest = request;
  if (!rwWatch.health || rwWatch.health.domain !== domain) rwWatch.health = { domain, status: 'loading', pillars: [] };
  if (rwWatch.ready) rwRenderTriageKpiOptions();
  const [pillarsResult, definitionsResult] = await Promise.allSettled([
    fetchJsonPayload(withActiveCompany(`/api/health/pillars?${new URLSearchParams({ domain })}`)),
    fetchJsonPayload(withActiveCompany('/api/settings/kpis')),
  ]);
  if (rwWatch.healthRequest !== request) return;
  if (definitionsResult.status === 'fulfilled' && Array.isArray(definitionsResult.value)) {
    rwWatch.kpiDefs = definitionsResult.value;
  }
  if (pillarsResult.status === 'fulfilled' && Array.isArray(pillarsResult.value)) {
    rwWatch.health = { domain, status: 'ready', pillars: pillarsResult.value };
  } else {
    if (pillarsResult.status === 'rejected') console.warn('/api/health/pillars unreachable.', pillarsResult.reason);
    rwWatch.health = { domain, status: 'error', pillars: [] };
  }
  if (rwWatch.ready) {
    rwRender();
    rwRenderTriageKpiOptions();
  }
  await loadKpiBuckets(request);
}

async function loadKpiBuckets(request) {
  const kpis = RW_BACKEND_PILLARS.flatMap((key) => {
    const pillar = rwBackendPillar(key);
    return pillar ? (pillar.kpis || []).filter((kpi) => kpi.enabled) : [];
  });
  if (kpis.length === 0) return;
  const results = await Promise.allSettled(kpis.map((kpi) =>
    fetchJsonPayload(withActiveCompany(`/api/health/kpis/${encodeURIComponent(kpi.kpiDefinitionId)}/buckets?hours=${RW_BUCKET_HOURS}`))));
  if (rwWatch.healthRequest !== request) return;
  kpis.forEach((kpi, index) => {
    const result = results[index];
    rwWatch.buckets[kpi.kpiDefinitionId] = result.status === 'fulfilled' && Array.isArray(result.value)
      ? { status: 'ready', items: [...result.value].sort((a, b) => new Date(a.bucketStart) - new Date(b.bucketStart)) }
      : { status: 'error', items: [] };
  });
  if (rwWatch.ready) rwRender();
}

/* --- Triage Monkey (Spec 1000) ----------------------------------------------------------- */

const RW_ALERT_LABELS = {
  red_alert: ['Red alert', 'critical'],
  amber: ['Amber', 'degraded'],
  informational: ['Informational', 'healthy'],
};

const RW_TRIAGE_DIMENSIONS = [
  ['businessHealth', 'Business health'],
  ['applicationHealth', 'Application health'],
  ['infrastructureHealth', 'Infrastructure health'],
  ['codeReliability', 'Code reliability'],
  ['recentChanges', 'Recent changes'],
  ['knowledgeBase', 'Knowledge base'],
];

function rwAlertBadge(level) {
  if (!level) return '';
  const [label, cls] = RW_ALERT_LABELS[level] || [level, 'unknown'];
  return `<span class="status-badge status-badge--${cls}">${escapeHtml(label)}</span>`;
}

function rwTriageStatusLabel(status) {
  const text = String(status || 'unknown');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

async function loadTriageInvestigations() {
  const request = rwWatch.triageRequest + 1;
  rwWatch.triageRequest = request;
  if (!rwWatch.triage) rwWatch.triage = { status: 'loading', items: [] };
  let next;
  try {
    const items = await fetchJsonPayload(withActiveCompany(`/api/triage/investigations?top=${RW_TRIAGE_TOP}`));
    next = { status: 'ready', items: Array.isArray(items) ? items : [] };
  } catch (err) {
    console.warn('/api/triage/investigations unreachable.', err);
    next = { status: 'error', items: [] };
  }
  if (rwWatch.triageRequest !== request) return;
  rwWatch.triage = next;
  if (rwWatch.ready) rwRenderTriage();
}

function rwTriageItem(investigation) {
  const signal = investigation.signal || {};
  const assessment = investigation.result && investigation.result.assessment;
  const scope = [signal.domain ? domainLabel(signal.domain) : '', signal.stage].filter(Boolean).join(' / ') || 'All domains';
  const summary = assessment
    ? assessment.summary
    : investigation.error ? `Error: ${investigation.error}` : signal.detection;
  const actions = assessment && Array.isArray(assessment.recommendedNextActions)
    ? assessment.recommendedNextActions.slice(0, 3)
    : [];
  return `<article class="rw-investigation rw-investigation--item">
    <div class="rw-investigation__head">
      <h3>${escapeHtml(signal.pillar || '—')} · ${escapeHtml(signal.kpi || '—')}</h3>
      <span class="rw-investigation__badges">${rwAlertBadge(investigation.alertLevel)} <span class="rw-investigation__status">${escapeHtml(rwTriageStatusLabel(investigation.status))}</span></span>
    </div>
    <p class="rw-investigation__meta">${escapeHtml(scope)} · ${escapeHtml(signal.source || '—')} · ${escapeHtml(formatRelativeTime(investigation.createdAt))}</p>
    <p>${escapeHtml(summary || '—')}</p>
    ${actions.length ? `<ul class="rw-investigation__actions">${actions.map((action) => `<li>${escapeHtml(action)}</li>`).join('')}</ul>` : ''}
    <button class="btn btn--sm btn--ghost" type="button" data-triage-id="${escapeHtml(investigation.investigationId)}">Details</button>
  </article>`;
}

function rwRenderTriage() {
  const pill = document.getElementById('rw-triage-pill');
  const meta = document.getElementById('rw-triage-meta');
  const list = document.getElementById('rw-triage-list');
  if (!pill || !meta || !list) return;
  const triage = rwWatch.triage;
  if (!triage || triage.status === 'loading') {
    pill.className = 'rw-pill rw-pill--idle';
    pill.textContent = 'Loading';
    meta.textContent = 'Loading investigations…';
    list.innerHTML = '';
    return;
  }
  if (triage.status !== 'ready') {
    pill.className = 'rw-pill rw-pill--off';
    pill.textContent = 'Unavailable';
    meta.textContent = 'Investigations unavailable.';
    list.innerHTML = '<div class="rw-investigation"><h3>Investigations unavailable</h3><p>The Triage Monkey API could not be read.</p></div>';
    return;
  }
  const items = triage.items;
  const inProgress = items.filter((item) => item.status === 'queued' || item.status === 'running').length;
  pill.className = `rw-pill ${inProgress ? 'rw-pill--warn' : 'rw-pill--idle'}`;
  pill.textContent = inProgress ? 'Investigating' : 'Idle';
  if (items.length === 0) {
    meta.textContent = '0 investigations · Only one Triage Monkey';
    list.innerHTML = '<div class="rw-investigation"><h3>Investigation not started</h3><p>Triage Monkey is standing by. No Red Alert decision has been made yet.</p></div>';
    return;
  }
  meta.textContent = `${items.length} recent investigation${items.length === 1 ? '' : 's'} · latest ${formatRelativeTime(items[0].createdAt)}${inProgress ? ` · ${inProgress} in progress` : ''}`;
  list.innerHTML = items.map(rwTriageItem).join('');
}

function rwTriageDetailHtml(investigation) {
  const signal = investigation.signal || {};
  const result = investigation.result;
  const assessment = result && result.assessment;
  const correlation = result && result.correlation;
  const row = (label, value) => `<li><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value == null || value === '' ? '—' : String(value))}</li>`;
  const dimensions = RW_TRIAGE_DIMENSIONS.map(([key, label]) => {
    const data = correlation ? correlation[key] : null;
    const available = data != null;
    return `<div class="rw-dimension${available ? '' : ' rw-dimension--off'}">
      <h4>${escapeHtml(label)} <span class="rw-pill ${available ? 'rw-pill--ok' : 'rw-pill--off'}">${available ? 'Available' : 'Unavailable'}</span></h4>
      ${available ? `<pre class="rw-json">${escapeHtml(JSON.stringify(data, null, 2))}</pre>` : ''}
    </div>`;
  }).join('');
  const assessmentHtml = assessment
    ? `<p>${rwAlertBadge(assessment.alertLevel)}${assessment.possibleRedAlert ? ' <span class="status-badge status-badge--critical">Possible red alert</span>' : ''}</p>
      <p>${escapeHtml(assessment.summary || '—')}</p>
      <ul class="simple-list">${row('Confidence', assessment.confidence == null ? null : assessment.confidence)}</ul>
      <h3>Recommended next actions</h3>
      <ul class="simple-list">${(assessment.recommendedNextActions || []).map((action) => `<li>${escapeHtml(action)}</li>`).join('') || '<li>None.</li>'}</ul>`
    : `<p>${escapeHtml(investigation.status === 'failed' || investigation.status === 'discarded' ? 'No assessment was produced.' : 'Not assessed yet.')}</p>`;
  return `<h3>Signal</h3>
    <ul class="simple-list">
      ${row('Pillar', signal.pillar)}${row('KPI', signal.kpi)}${row('Domain', signal.domain)}${row('Stage', signal.stage)}
      ${row('Detection', signal.detection)}${row('Detected at', signal.detectedAt ? new Date(signal.detectedAt).toLocaleString() : null)}
      ${row('Source', signal.source)}${row('Anomaly', signal.anomalyId)}
    </ul>
    <h3>Investigation</h3>
    <ul class="simple-list">
      ${row('Id', investigation.investigationId)}${row('Status', rwTriageStatusLabel(investigation.status))}
      ${row('Created', investigation.createdAt ? new Date(investigation.createdAt).toLocaleString() : null)}
      ${row('Completed', investigation.completedAt ? new Date(investigation.completedAt).toLocaleString() : null)}
      ${row('Notified', investigation.notified ? 'Yes' : 'No')}${investigation.error ? row('Error', investigation.error) : ''}
    </ul>
    <h3>Assessment</h3>
    ${assessmentHtml}
    <h3>Correlation</h3>
    ${dimensions}`;
}

async function openTriageInvestigation(id) {
  openSidePanel('Triage investigation', '<div class="state-block">Loading…</div>');
  try {
    const investigation = await fetchJsonPayload(withActiveCompany(`/api/triage/investigations/${encodeURIComponent(id)}`));
    const signal = investigation.signal || {};
    openSidePanel(`Triage · ${signal.pillar || ''} · ${signal.kpi || ''}`, rwTriageDetailHtml(investigation));
  } catch (err) {
    openSidePanel('Triage investigation', `<div class="state-block state-block--error">${escapeHtml(err.message || 'Investigation could not be read.')}</div>`);
  }
}

/** KPI names for the manual trigger: the configured definitions for the selected card's domain. */
function rwRenderTriageKpiOptions() {
  const pillarSelect = document.getElementById('rw-triage-pillar');
  const kpiSelect = document.getElementById('rw-triage-kpi');
  const run = document.getElementById('rw-triage-run');
  if (!pillarSelect || !kpiSelect || !run) return;
  const scope = document.getElementById('rw-triage-scope');
  if (scope) scope.textContent = rwFocusLabel();
  // Until the selected vertical's KPIs have loaded, a KPI of the previous domain must not be sent.
  if (rwHealthPending()) {
    kpiSelect.innerHTML = '<option value="">Loading KPIs…</option>';
    run.disabled = true;
    return;
  }
  const domain = rwHealthDomain();
  const pillarKey = pillarSelect.value;
  const fromDefinitions = (rwWatch.kpiDefs || [])
    .filter((definition) => String(definition.pillar || '').toLowerCase() === pillarKey && definition.domain === domain)
    .map((definition) => definition.kpi);
  const pillar = rwBackendPillar(pillarKey);
  const fromHealth = pillar ? (pillar.kpis || []).map((kpi) => kpi.kpi) : [];
  const names = [...new Set([...fromDefinitions, ...fromHealth])];
  const previous = kpiSelect.value;
  kpiSelect.innerHTML = names.length
    ? names.map((name) => `<option value="${escapeHtml(name)}">${escapeHtml(name)}</option>`).join('')
    : '<option value="">No KPI configured</option>';
  if (names.includes(previous)) kpiSelect.value = previous;
  run.disabled = rwWatch.triageRunning || names.length === 0;
}

async function rwRunTriage() {
  const pillarKey = document.getElementById('rw-triage-pillar').value;
  const kpi = document.getElementById('rw-triage-kpi').value;
  const statusEl = document.getElementById('rw-triage-run-status');
  if (!kpi || rwWatch.triageRunning || rwHealthPending()) return;
  rwWatch.triageRunning = true;
  document.getElementById('rw-triage-run').disabled = true;
  statusEl.textContent = 'Submitting…';
  try {
    const response = await fetch('/api/triage/investigations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify({
        detectedAt: new Date().toISOString(),
        pillar: RW_PILLAR_DISPLAY[pillarKey],
        kpi,
        // The web server replaces this with "manual: <signed-in user>" when Entra is configured.
        detection: 'manual: portal',
        domain: rwHealthDomain(),
      }),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      const errors = Array.isArray(body.errors) ? body.errors.join(' ') : body.error;
      throw new Error(errors || `Request failed: ${response.status}`);
    }
    const message = body.duplicate
      ? `Duplicate: investigation ${body.investigationId} already covers this KPI this hour.`
      : `Investigation ${body.investigationId} queued.`;
    statusEl.textContent = message;
    showToast('Triage Monkey', message, body.duplicate ? 'info' : 'success');
    loadTriageInvestigations();
  } catch (err) {
    statusEl.textContent = err.message;
    showToast('Triage Monkey', err.message, 'critical');
  } finally {
    rwWatch.triageRunning = false;
    rwRenderTriageKpiOptions();
  }
}

function rwHealthTabVisible() {
  return document.visibilityState === 'visible' && state.activeTab === 'health';
}

function rwRefreshBackend() {
  loadPillarHealth();
  loadTriageInvestigations();
}

/** Re-reads the backend every 60 s, only while the Platform Health tab is on screen. */
function rwStartHealthRefresh() {
  if (rwWatch.healthTimer) return;
  rwWatch.healthTimer = setInterval(() => {
    if (rwHealthTabVisible()) rwRefreshBackend();
  }, RW_HEALTH_REFRESH_MS);
  document.addEventListener('visibilitychange', () => {
    if (rwHealthTabVisible()) rwRefreshBackend();
  });
}

function wireTriageCard() {
  document.getElementById('rw-triage-list')?.addEventListener('click', (event) => {
    const button = event.target.closest('[data-triage-id]');
    if (button) openTriageInvestigation(button.dataset.triageId);
  });
  document.getElementById('rw-triage-pillar')?.addEventListener('change', rwRenderTriageKpiOptions);
  document.getElementById('rw-triage-run')?.addEventListener('click', rwRunTriage);
}

/** Last ten completed purchases, oldest first. Each bar is a full blue bar. Empty slots pad
 *  the left when fewer than ten completed purchases are stored. */
function rwConversionBars(operations) {
  const missing = Math.max(0, RW_CONVERSION_SAMPLE - operations.length);
  const placeholders = Array.from(
    { length: missing },
    () => '<span class="is-empty" style="height:100%"></span>',
  ).join('');
  const bars = operations.map(() => '<span style="height:100%"></span>').join('');
  return placeholders + bars;
}

function rwConversionChart() {
  const live = rwWatch.hotelBusiness;
  const current = live && Array.isArray(live.conversion) ? live.conversion[1] : null;
  const operations = live && Array.isArray(live.operations)
    ? live.operations.filter((operation) => operation.completed === true).slice(0, RW_CONVERSION_SAMPLE)
    : [];
  const value = current == null ? '—' : rwFmt('conversion', Number(current));
  const heading = `Conversion · last ${RW_CONVERSION_SAMPLE} operations`;
  if (!live) {
    return `<p class="rw-chart__label"><span>${heading}</span><span class="rw-muted">—</span></p>
      <div class="rw-chart-empty"><p>Conversion unavailable</p></div>`;
  }
  if (!live.currentConversionWindowStart) {
    return `<p class="rw-chart__label"><span>${heading}</span><span class="rw-muted">—</span></p>
      <div class="rw-chart-empty"><p>Conversion unavailable</p></div>`;
  }
  if (operations.length === 0) {
    return `<p class="rw-chart__label"><span>${heading}</span><span class="rw-muted">${escapeHtml(value)}</span></p>
      <div class="rw-chart-empty"><p>No completed purchases</p></div>`;
  }
  return `<p class="rw-chart__label"><span>${heading}</span><span>${escapeHtml(value)}</span></p>
    <div class="rw-bars" role="img" aria-label="${escapeHtml(heading)}">${rwConversionBars(operations)}</div>`;
}

function rwCountBars(count, label) {
  const bars = Array.from({ length: 10 }, (_, index) => {
    const recorded = index < count;
    return `<span class="${recorded ? 'is-warning' : ''}" style="height:${recorded ? 70 : 12}%"></span>`;
  }).join('');
  return `<p class="rw-chart__label"><span>${escapeHtml(label)}</span><strong>${count} total</strong></p>
    <div class="rw-bars rw-bars--short" role="img" aria-label="${escapeHtml(label)}, ${count} recorded">${bars}</div>`;
}

function rwPillar(modifier, title, pillHtml, body, note, muted = false) {
  return `<article class="rw-pillar rw-pillar--${modifier}${muted ? ' rw-pillar--muted' : ''}">
    <header class="rw-pillar__head"><h3>${title}</h3>${pillHtml}</header>
    ${body}
    ${note ? `<p class="rw-chart__note">${note}</p>` : ''}
  </article>`;
}

const RW_OFFLINE_PILL = '<span class="rw-pill rw-pill--off">Offline</span>';
const RW_OFFLINE_BODY = '<ul class="rw-metrics"><li><span>Repository telemetry</span><span class="rw-metrics__vals"><strong class="rw-muted">— Offline</strong></span></li></ul><div class="rw-chart-empty"><p>No operations · pillar offline</p></div>';

/** Bookings and revenue always compare with 7 days ago. Live conversion names its own baseline. */
function rwBusinessNote() {
  const base = 'Current vs same weekday/hour 7 days ago';
  if (!rwHotelsQueryMode() || !rwWatch.hotelBusiness || !rwWatch.hotelBusiness.priorConversionWindowStart) return base;
  return `${base} · conversion vs ${rwConversionBaselinePhrase().short}`;
}

function rwRenderPillars() {
  document.getElementById('rw-pillars-title').textContent = `${rwFocusLabel()} · Observability Pillars`;
  rwRenderPillarCount();

  const infrastructure = rwBackendPillar('infrastructure');
  const infrastructureLive = !!infrastructure && (infrastructure.kpis || []).length > 0;
  const infrastructureOffline = !infrastructure || infrastructure.status === 'offline';

  document.getElementById('rw-pillars').innerHTML = [
    rwPillar(
      'business',
      'Business Health',
      rwPillarStatusPill('business'),
      `<ul class="rw-metrics">${rwMetric('conversion', 'Conversion', true)}${rwMetric('bookings', 'Bookings', true)}${rwMetric('revenue', 'Revenue', true)}${rwCancellationMetric()}</ul>${rwPlaceholderDomain() ? '<p class="rw-chart__label"><span>Conversion</span><span class="rw-muted">Unavailable</span></p><div class="rw-chart-empty"><p>No operations in this window</p></div>' : rwConversionChart()}
      <p class="rw-chart__label"><span>Monitored KPIs · ${escapeHtml(domainLabel(rwHealthDomain()))}</span></p>
      <ul class="rw-metrics">${rwBackendKpiRows('business')}</ul>${rwBackendKpiCharts('business')}`,
      rwBackendNote('business', rwBusinessNote()),
    ),
    rwPillar(
      'application',
      'Application Health',
      rwPillarStatusPill('application'),
      `<ul class="rw-metrics">${rwBackendKpiRows('application')}${rwMetric('exceptions', 'Exceptions', false)}</ul>${rwBackendKpiCharts('application')}`,
      rwBackendNote('application', `${rwFocusLabel()} · every ${profileLabels().entity.toLowerCase()} · evaluated by the Application Health monitor`),
    ),
    infrastructureLive
      ? rwPillar(
        'infrastructure',
        'Infrastructure Health',
        rwPillarStatusPill('infrastructure'),
        `<ul class="rw-metrics">${rwBackendKpiRows('infrastructure')}</ul>${rwBackendKpiCharts('infrastructure')}`,
        rwBackendNote('infrastructure', infrastructureOffline ? 'Excluded from Triage Monkey investigation' : ''),
        infrastructureOffline,
      )
      : rwPillar('infrastructure', 'Infrastructure Health', rwPillarStatusPill('infrastructure'), RW_OFFLINE_BODY, 'Excluded from Triage Monkey investigation', true),
    rwPillar('change', 'Change Intelligence', RW_OFFLINE_PILL, RW_OFFLINE_BODY, 'Excluded from Triage Monkey investigation', true),
    rwPillar('reliability', 'Reliability Knowledge', RW_OFFLINE_PILL, RW_OFFLINE_BODY, 'Excluded from Triage Monkey investigation', true),
    rwPillar('code', 'Code Intelligence', RW_OFFLINE_PILL, RW_OFFLINE_BODY, 'Excluded from Triage Monkey investigation', true),
  ].join('');
}

function rwComponentLatency(stage, componentIndex) {
  const historical = [258, 410, 345, 508][componentIndex] + stage * 22;
  return [historical, historical + (componentIndex === 1 ? 4 : -2)];
}

function rwRenderJourney() {
  const labels = profileLabels();
  document.getElementById('rw-journey-title').textContent = `${rwFocusLabel()} · ${labels.flow}`;
  // The component breakdown is the incumbent's sample model; every other company sees its own
  // steps from the profile and no invented components.
  const legacy = companyHasLegacyWidgets();
  const stageLabels = legacy
    ? rwWatch.stageLabels
    : profileServiceSteps(rwWatch.focus).map((s) => s.label);
  if (rwWatch.stage >= stageLabels.length) rwWatch.stage = 0;
  document.getElementById('rw-stages').innerHTML = stageLabels.length
    ? stageLabels.map((label, index) => {
      const selected = index === rwWatch.stage;
      return `<button class="rw-stage${selected ? ' is-selected' : ''}" type="button" data-rw-stage="${index}" aria-pressed="${selected}"><span>${index + 1}. ${escapeHtml(label)}</span>${legacy ? '<small>→ stable</small>' : ''}</button>`;
    }).join('')
    : `<div class="state-block">${escapeHtml(labels.step)}: none defined for this ${escapeHtml(labels.service)}.</div>`;
  if (!legacy) {
    document.getElementById('rw-components').innerHTML = `<p class="rw-panel__note">Component breakdown is not available for this company yet.</p>`;
    return;
  }
  const accents = ['app', 'api', 'service', 'partner'];
  document.getElementById('rw-components').innerHTML = rwWatch.components[rwWatch.stage].map((name, index) => {
    const [oldMs, nowMs] = rwComponentLatency(rwWatch.stage, index);
    const trend = rwTrend(nowMs, oldMs, false);
    return `<article class="rw-component rw-component--${accents[index]}">
      <h3>${escapeHtml(name)}</h3>
      <div class="rw-pair rw-pair--compact">
        <div><span class="rw-pair__label">Historical P90</span><span class="rw-pair__value">${oldMs} ms</span></div>
        <div><span class="rw-pair__label">Current P90</span><span class="rw-pair__value">${nowMs} ms</span></div>
      </div>
      <p class="rw-delta rw-delta--${trend.cls}">${Math.abs(rwPercent(nowMs, oldMs)).toFixed(1)}% vs reference</p>
    </article>`;
  }).join('');
}

function rwRender() {
  // Every redraw replaces the card buttons. Keep keyboard focus on the same card, but only when
  // focus is still on a card at the moment this redraw starts.
  const active = document.activeElement;
  const focusedCard = active && active.matches && active.matches('#rw-verticals [data-rw-vertical]')
    ? active.dataset.rwVertical
    : null;
  rwRenderVerticals();
  if (focusedCard) document.querySelector(`#rw-verticals [data-rw-vertical="${focusedCard}"]`)?.focus();
  rwRenderPillars();
  rwRenderJourney();
}

function applyHotelBusinessHealth(payload) {
  if (!payload || payload.available === false || !payload.current || !payload.priorWeek) {
    rwWatch.hotelBusiness = null;
    return;
  }
  const prior = payload.priorWeek;
  const current = payload.current;
  const bookingsLive = payload.bookingsAvailable !== false;
  rwWatch.hotelBusiness = {
    bookings: bookingsLive ? [Number(prior.bookings), Number(current.bookings)] : null,
    revenue: bookingsLive ? [Number(prior.revenue), Number(current.revenue)] : null,
    conversion: [prior.conversionPercent ?? null, current.conversionPercent ?? null],
    operations: Array.isArray(payload.recentOperations) ? payload.recentOperations : [],
    priorConversionWindowStart: payload.priorConversionWindowStart || null,
    currentConversionWindowStart: payload.currentConversionWindowStart || null,
    environment: state.filters.environment || 'production',
  };
}

async function loadHotelBusinessHealth() {
  const request = (rwWatch.businessRequest || 0) + 1;
  rwWatch.businessRequest = request;
  // Contract v6 §4.5: Fabric business health is incumbent-only; another company never reads it.
  if (!companyHasLegacyWidgets()) {
    rwWatch.hotelBusiness = null;
    return;
  }
  const params = new URLSearchParams();
  params.set('environment', state.filters.environment || 'production');
  params.set('domain', rwWatch.liveBusinessService);
  try {
    const response = await fetch(withActiveCompany(`/api/hotels/business-health?${params}`));
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const payload = await response.json();
    if (rwWatch.businessRequest !== request) return;
    applyHotelBusinessHealth(payload);
  } catch {
    if (rwWatch.businessRequest !== request) return;
    rwWatch.hotelBusiness = null;
  }
  if (rwWatch.ready && rwWatch.businessRequest === request) rwRender();
}

/** A card click drives the Domain filter through its own change handler, so entities, monkeys and
 *  journeys reload exactly as if the user had picked the service by hand. */
function rwSyncDomainFilter(domain) {
  const select = document.getElementById('filter-domain');
  if (!select || !domain || select.value === domain) return;
  if (![...select.options].some((o) => o.value === domain)) return;
  select.value = domain;
  select.dispatchEvent(new Event('change'));
}

function renderHealthTab() {
  const root = document.getElementById('rw-verticals');
  if (!root) return;
  // Application Health now reads live journeys, so a reload has to redraw. The listeners below are
  // delegated to the containers, which lets the markup be replaced without rewiring them.
  if (rwWatch.hotelBusiness && rwWatch.hotelBusiness.environment !== (state.filters.environment || 'production')) {
    rwWatch.hotelBusiness = null;
  }
  rwEnsureFocus();
  // renderAllTabs runs on every auto-refresh and filter change from any tab. The backend health
  // reads are not repeated here: they run on entering the tab (activateTab), every 60 s and on
  // visibilitychange while it is visible, and on a vertical change (the card click below).
  if (rwWatch.ready) { loadApplicationHealth(); rwRender(); loadHotelBusinessHealth(); rwEnsureBackendDomain(); return; }
  rwWatch.ready = true;
  root.addEventListener('click', (event) => {
    const button = event.target.closest('[data-rw-vertical]');
    if (!button || rwVerticalOffline(button.dataset.rwVertical)) return;
    // While a filter request is in flight the Domain control is locked; a card click must respect
    // the same lock, or overlapping club loads could leave another domain's clubs in the list.
    if (document.getElementById('filter-domain')?.disabled) return;
    rwWatch.focus = button.dataset.rwVertical;
    rwWatch.stage = 1;
    // Marks the pillars as loading for the new domain before the redraw below, and is the only
    // backend load for this click: the Domain change it dispatches finds the domain already loaded.
    loadPillarHealth();
    loadApplicationHealth();
    rwRender();
    rwRenderTriageKpiOptions();
    rwSyncDomainFilter(rwWatch.focus);
  });
  document.getElementById('rw-stages').addEventListener('click', (event) => {
    const button = event.target.closest('[data-rw-stage]');
    if (!button) return;
    rwWatch.stage = Number(button.dataset.rwStage);
    const hadFocus = document.activeElement === button;
    rwRenderJourney();
    if (hadFocus) {
      document.querySelector('#rw-stages .rw-stage.is-selected').focus();
    }
  });
  wireTriageCard();
  rwStartHealthRefresh();
  rwRender();
  rwRenderTriage();
  rwRenderTriageKpiOptions();
  loadHotelBusinessHealth();
  loadApplicationHealth();
  if (rwHealthTabVisible()) rwRefreshBackend();
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

function renderDashboardTimechart(svgId, legendId, points, seriesLabel = (name) => name) {
  const svg = document.getElementById(svgId);
  const legend = document.getElementById(legendId);
  if (!svg) return;
  if (!points.length) {
    svg.innerHTML = '';
    if (legend) legend.innerHTML = '<span>No stored points for this window yet.</span>';
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
  renderSvgLineChart(svgId, seriesNames.map((name) => bySeries[name]), seriesNames.map((_, i) => colors[i % colors.length]));
  if (legend) {
    legend.innerHTML = seriesNames.map((name, i) =>
      `<span><i class="legend-swatch" style="background:${colors[i % colors.length]}"></i>${escapeHtml(seriesLabel(name))}</span>`).join('');
  }
}

const DASHBOARD_CHART_TITLES = {
  step_latency_p95: (step) => `P95 latency by ${step.toLowerCase()}`,
  exceptions_by_step: (step) => `exceptions by ${step.toLowerCase()}`,
};

/** Contract v6 §4.4: one card per (service, chart) the metrics response names - no fixed names.
 *  The series are drawn from the returned points, so a service whose series use other keys than
 *  its profile steps (the incumbent's legacy sync) still draws. */
function dashboardChartCards() {
  const meta = state.dashboardMeta || {};
  const services = Array.isArray(meta.services) && meta.services.length
    ? meta.services
    : profileServices().map((s) => ({ key: s.key, displayName: s.label, charts: Object.keys(DASHBOARD_CHART_TITLES), steps: [] }));
  return services
    .filter((s) => s && s.key && (state.filters.domain === 'all' || s.key === state.filters.domain))
    .flatMap((service) => (Array.isArray(service.charts) ? service.charts : []).map((chart) => ({ service, chart })));
}

function renderAnalyticsCharts() {
  const host = document.getElementById('analytics-charts');
  if (!host) return;
  const stepLabel = (state.dashboardMeta && state.dashboardMeta.labels && state.dashboardMeta.labels.step) || profileLabels().step;
  const cards = dashboardChartCards();
  const signature = JSON.stringify([activeCompanyKey(), stepLabel, cards.map(({ service, chart }) => `${service.key}:${chart}`)]);
  if (host.dataset.signature !== signature) {
    host.dataset.signature = signature;
    host.innerHTML = cards.length
      ? cards.map(({ service, chart }, i) => {
        const title = (DASHBOARD_CHART_TITLES[chart] || ((step) => `${chart} by ${step.toLowerCase()}`))(stepLabel);
        return `<div class="chart-card" data-dashboard-chart="${escapeHtml(`${service.key}:${chart}`)}">
          <h2 class="chart-card__title">${escapeHtml(service.displayName || domainLabel(service.key))} — ${escapeHtml(title)}</h2>
          <svg id="dashboard-chart-${i}" class="chart-svg" viewBox="0 0 600 220" preserveAspectRatio="none"></svg>
          <div class="chart-legend" id="dashboard-legend-${i}"></div>
        </div>`;
      }).join('')
      : `<div class="state-block">No analytics are defined for this company.</div>`;
  }
  const note = document.getElementById('analytics-note');
  if (note) {
    const warning = state.dashboardMeta && state.dashboardMeta.warning;
    note.textContent = warning || '';
    note.hidden = !warning;
  }
  if (state.dashboardChartsLoading) {
    paintDashboardChartsLoading();
    return;
  }
  host.querySelectorAll('[data-dashboard-chart]').forEach((card) => card.removeAttribute('aria-busy'));
  const points = state.dashboardMetrics || [];
  cards.forEach(({ service, chart }, i) => {
    const steps = Array.isArray(service.steps) ? service.steps : [];
    const seriesLabel = (name) => {
      const step = steps.find((s) => String(s.key).toLowerCase() === String(name).toLowerCase());
      return step ? (step.displayName || step.key) : profileStepLabel(service.key, name) || name;
    };
    renderDashboardTimechart(`dashboard-chart-${i}`, `dashboard-legend-${i}`,
      points.filter((p) => p.domain === service.key && p.chart === chart), seriesLabel);
  });
}

function renderServiceComparison() {
  const labels = profileLabels();
  const heading = document.getElementById('domain-comparison-heading');
  if (heading) heading.textContent = `${labels.service} Comparison`;
  const head = document.getElementById('domain-comparison-thead');
  if (head) {
    head.innerHTML = `<tr><th>${escapeHtml(labels.service)}</th><th>${escapeHtml(countHeading(labels.flow))}</th><th>Completion rate</th><th>P95 duration</th><th>Failed</th><th>Exceptions</th></tr>`;
  }
  const comparisonBody = document.querySelector('#domain-comparison-table tbody');
  if (!comparisonBody) return;
  if (state.tablesLoading) {
    comparisonBody.innerHTML = loadingTableRow(6);
    return;
  }
  markTableBusy(document.getElementById('domain-comparison-table'), false);
  const rows = state.serviceComparison || [];
  comparisonBody.innerHTML = rows.length
    ? rows.map((d) => `<tr><td>${escapeHtml(domainLabel(d.domain))}</td><td>${d.journeys.toLocaleString()}</td>
      <td>${d.completionRate == null ? '—' : `${(d.completionRate * 100).toFixed(1)}%`}</td>
      <td>${d.p95LatencyMs == null ? '—' : formatDuration(d.p95LatencyMs)}</td>
      <td>${d.failed.toLocaleString()}</td>
      <td>${d.exceptions.toLocaleString()}</td></tr>`).join('')
    : `<tr><td colspan="6"><div class="state-block">${state.domainComparisonStale
      ? `Loading ${escapeHtml(labels.service.toLowerCase())} comparison…`
      : `${escapeHtml(labels.flow)}: nothing in the selected window.`}</div></td></tr>`;
}

/** The remaining blocks are the incumbent's sample data (data/analytics.json): another company
 *  gets a note instead of someone else's figures. */
function renderAnalyticsLegacyBlocks() {
  const legacy = document.getElementById('analytics-legacy');
  const note = document.getElementById('analytics-legacy-note');
  const applies = companyHasLegacyWidgets();
  if (legacy) legacy.hidden = !applies;
  if (note) note.hidden = applies;
  const a = state.analytics;
  if (!applies || !a) return;
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

function renderAnalyticsTab() {
  renderAnalyticsCharts();
  renderServiceComparison();
  renderAnalyticsLegacyBlocks();
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
  renderAgentPipeline();
  startAgentPipelinePoller();

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
      ${isScoutMonkey(m.id) ? renderScoutShadow(m) : ''}
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
  loadScoutShadow();
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
        ${running.clubsWatched ? `<span class="agent-live__sep">·</span><span><strong>${formatCount(running.boardRowsEvaluated)}</strong> rows so far · <strong>${escapeHtml(entityTally(running.clubsWatched))}</strong></span>` : ''}
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
      : `${formatCount(c.boardRowsEvaluated)} rows · ${entityTally(c.clubsWatched)} · ${c.packagesCreated} new · ${c.packagesSkippedDuplicate} already known`;
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
        <span class="cycle-chip">${escapeHtml(profileLabels().entity)}: <strong>${formatCount(c.clubsWatched)}</strong></span><span class="cycle-arrow">→</span>
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

/* Scout shadow (contract v4 §4): the Scout escalates on its fixed rules; the entity-grain KPI method
   runs beside it and is only compared. Every figure here comes from GET /api/scouts/{board}/shadow -
   the browser counts nothing and decides nothing. */
const SCOUT_SHADOW_BOARDS = { 'latency-scout-monkey': 'latency', 'exception-scout-monkey': 'exception' };
const SCOUT_SHADOW_HOURS = 24;
const SCOUT_SHADOW_TOP = 8;
const SCOUT_SHADOW_TTL_MS = 60000;
const SCOUT_SHADOW_AGREEMENT = {
  both: { label: 'Both', hint: () => `The rules and the method both flagged this ${scoutShadowGrain()} × hour.` },
  'rules-only': { label: 'Rules only', hint: () => `The rules flagged it; the method saw nothing anomalous for that ${scoutShadowGrain()} × hour.` },
  'method-only': { label: 'Method only', hint: () => 'The method flagged it; the rules did not put it on the board.' },
};

/** The shadow's case grain in the active company's words ("Club × Stage", "Sucursal × Paso"). */
function scoutShadowGrain() {
  const l = profileLabels();
  return `${l.entity} × ${l.step}`;
}
const scoutShadow = { profileKey: null, fetchedAt: 0, boards: {} };
let scoutShadowInFlight = null;

function renderScoutShadow(m) {
  const board = SCOUT_SHADOW_BOARDS[m.id];
  if (!board) return '';
  return `<section class="scout-shadow" data-scout-shadow="${escapeHtml(board)}" aria-label="Shadow — rules vs method">${renderScoutShadowBody(m, scoutShadow.boards[board])}</section>`;
}

function renderScoutShadowBody(m, data) {
  const mode = (data && data.detectionMode) || 'Rules';
  const head = `
    <div class="scout-shadow__head">
      <h3 class="scout-shadow__title">Shadow — rules vs method</h3>
      <span class="scout-shadow__mode" title="Scout:DetectionMode. Rules: the fixed rules decide what reaches the Inspector; the method is only compared.">Detection mode: <strong>${escapeHtml(mode)}</strong></span>
    </div>
    <p class="scout-shadow__note">Last ${SCOUT_SHADOW_HOURS} h. Counted once per ${escapeHtml(scoutShadowGrain())} × hour — repeated 5-minute rows of the same hour are one case.</p>`;

  if (!data) return `${head}<p class="timeline-empty">Loading the shadow comparison…</p>`;
  if (data.error) return `${head}<p class="timeline-empty">Shadow comparison unavailable: ${escapeHtml(data.error)}</p>`;

  const counts = data.counts || {};
  const rows = Array.isArray(data.rows) ? data.rows : [];
  const stat = (key, value) => `
    <div class="scout-shadow__count scout-shadow__count--${key}" title="${escapeHtml(SCOUT_SHADOW_AGREEMENT[key].hint())}">
      <span class="agent-totals__value">${formatCount(value || 0)}</span>
      <span class="agent-totals__label">${escapeHtml(SCOUT_SHADOW_AGREEMENT[key].label)}</span>
    </div>`;
  const totals = `
    <div class="scout-shadow__counts">
      ${stat('both', counts.both)}${stat('rules-only', counts.rulesOnly)}${stat('method-only', counts.methodOnly)}
    </div>`;

  if (!rows.length && !counts.total) {
    return `${head}${totals}<p class="timeline-empty">No cases in the last ${SCOUT_SHADOW_HOURS} h — neither the rules nor the method flagged anything for this company.</p>`;
  }

  const cycleIds = new Set((m.cycles || []).map((c) => c.id));
  const list = rows.map((r) => {
    const agreement = SCOUT_SHADOW_AGREEMENT[r.agreement] || { label: r.agreement || '?', hint: () => '' };
    const hour = r.bucketStart ? new Date(r.bucketStart) : null;
    const hourText = hour && !Number.isNaN(hour.getTime())
      ? `${hour.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} ${String(hour.getHours()).padStart(2, '0')}:00`
      : '—';
    const links = [
      r.runId && cycleIds.has(r.runId)
        ? `<button type="button" class="scout-shadow__link" data-cycle-id="${escapeHtml(r.runId)}" title="Show the packages this Scout cycle handed off">cycle packages</button>`
        : '',
      r.kpiAnomalyId != null ? `<span class="scout-shadow__ref" title="KPI anomaly id">anomaly #${escapeHtml(String(r.kpiAnomalyId))}</span>` : '',
    ].filter(Boolean).join(' ');
    return `
      <tr>
        <td class="scout-shadow__entity" title="${escapeHtml(r.entityKey || '')}">${escapeHtml(r.entityKey || '—')}</td>
        <td>${escapeHtml(r.stage || '—')}</td>
        <td>${escapeHtml(hourText)}</td>
        <td><span class="shadow-pill shadow-pill--${escapeHtml(r.agreement || 'unknown')}" title="${escapeHtml(agreement.hint())}">${escapeHtml(agreement.label)}</span></td>
        <td>${r.ruleRank != null ? `#${escapeHtml(String(r.ruleRank))}` : '—'}</td>
        <td>${escapeHtml(r.methodVerdict || '—')}</td>
        <td>${links || '—'}</td>
      </tr>`;
  }).join('');

  return `${head}${totals}
    <p class="timeline-section">Latest cases</p>
    <div class="scout-shadow__table-wrap">
      <table class="scout-shadow__table">
        <thead><tr><th>Entity</th><th>Stage</th><th>Hour</th><th>Agreement</th><th>Rule rank</th><th>Method</th><th></th></tr></thead>
        <tbody>${list}</tbody>
      </table>
    </div>`;
}

function paintScoutShadow() {
  document.querySelectorAll('.monkey-card [data-scout-shadow]').forEach((node) => {
    const card = node.closest('.monkey-card');
    const m = card ? state.monkeys.find((x) => x.id === card.dataset.monkeyId) : null;
    if (m) node.innerHTML = renderScoutShadowBody(m, scoutShadow.boards[node.dataset.scoutShadow]);
  });
}

/** Reads both boards for the active company, at most once a minute; a company switch discards the cache. */
function loadScoutShadow(force) {
  if (!document.querySelector('.monkey-card [data-scout-shadow]')) return Promise.resolve();
  const key = activeCompanyKey();
  if (scoutShadow.profileKey !== key) {
    scoutShadow.profileKey = key;
    scoutShadow.boards = {};
    scoutShadow.fetchedAt = 0;
    paintScoutShadow();
  }
  if (!force && Date.now() - scoutShadow.fetchedAt < SCOUT_SHADOW_TTL_MS) return Promise.resolve();
  if (scoutShadowInFlight) return scoutShadowInFlight;

  scoutShadowInFlight = (async () => {
    const results = await Promise.all(Object.values(SCOUT_SHADOW_BOARDS).map(async (board) => {
      try {
        const response = await fetch(withActiveCompany(`/api/scouts/${encodeURIComponent(board)}/shadow?hours=${SCOUT_SHADOW_HOURS}&top=${SCOUT_SHADOW_TOP}`), { headers: { Accept: 'application/json' } });
        const body = await response.json().catch(() => null);
        if (response.ok && body) return [board, body];
        const message = (body && (body.error || (Array.isArray(body.errors) ? body.errors.join(' ') : ''))) || `HTTP ${response.status}`;
        return [board, { error: message }];
      } catch (err) {
        return [board, { error: 'the portal could not reach the API' }];
      }
    }));
    // A switch made while the request was in flight: these rows belong to the previous company.
    if (activeCompanyKey() !== key) return;
    results.forEach(([board, data]) => { scoutShadow.boards[board] = data; });
    scoutShadow.fetchedAt = Date.now();
    paintScoutShadow();
  })().finally(() => {
    scoutShadowInFlight = null;
    if (activeCompanyKey() !== key) loadScoutShadow();
  });
  return scoutShadowInFlight;
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
      loadScoutShadow();
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
    ? `Matched the whole ${profileNoun('service')}, not this ${profileNoun('step')} specifically.`
    : `This team is recorded as owning this exact ${profileNoun('step')}.`;
  return `<span class="insp-owner" title="${escapeHtml(hint)}">${escapeHtml(team)}${fallback ? ` <span class="insp-owner__fb">${escapeHtml(profileNoun('service'))}</span>` : ''}</span>`;
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
  if (!entry) return `Unknown ${profileNoun('entity')}`;
  if (entry.clubName) return entry.clubName;
  if (entry.brandName) return entry.brandName;
  const id = entry.clubId || entry.icePartnerId;
  return id ? `${profileNoun('entity')} ${id}` : `Unknown ${profileNoun('entity')}`;
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
    parts.push(escapeHtml(option ? option.label : `${profileNoun('entity')} ${f.club}`));
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
    const response = await fetch(withActiveCompany('/api/inspector/queue'));
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

// The pipeline widget used to be a snapshot of whatever /api/monkeys returned on page
// load (or on the 90s auto-refresh, itself off by default) - a real investigation could start,
// finish and compose an RCA entirely between visits, and the card would still say "0" until the
// user reloaded the whole page. Same shape as the Scout/Inspector pollers: only #agents-pipeline
// is replaced, and a hidden Simian Army tab costs nothing.
const PIPELINE_POLL_BUSY_MS = 5000;
const PIPELINE_POLL_IDLE_MS = 20000;
let pipelinePollTimer = null;

// Idempotent, unlike the Scout/Inspector pollers: renderMonkeysTab() is itself called from those
// pollers every few seconds, and a restart-on-every-call poller would reset its own timer before
// it ever fired.
function startAgentPipelinePoller() {
  if (pipelinePollTimer) return;
  const schedule = (ms) => { pipelinePollTimer = setTimeout(poll, ms); };

  async function poll() {
    const host = document.getElementById('agents-pipeline');
    if (!host) { stopAgentPipelinePoller(); return; }

    if (document.hidden) { schedule(PIPELINE_POLL_IDLE_MS); return; }

    const panel = document.getElementById('tab-monkeys');
    if (panel && panel.hidden) { schedule(PIPELINE_POLL_IDLE_MS); return; }

    try {
      const monkeys = await fetchMonkeysForPoll();
      state.monkeys = monkeys;
      renderAgentPipeline();
      const director = monkeys.find((m) => m.id === 'inspector-monkey');
      const busy = (director && director.currentCase) || monkeys.some((m) => m.currentCycle);
      schedule(busy ? PIPELINE_POLL_BUSY_MS : PIPELINE_POLL_IDLE_MS);
    } catch (err) {
      // A failed poll is not worth a toast - the next tick retries.
      schedule(PIPELINE_POLL_IDLE_MS);
    }
  }

  schedule(PIPELINE_POLL_BUSY_MS);
}

function stopAgentPipelinePoller() {
  if (pipelinePollTimer) { clearTimeout(pipelinePollTimer); pipelinePollTimer = null; }
}

/** Toast delivery is a poll, not a push. The watermark is kept in memory with a few seconds of
 *  overlap and duplicates are filtered by id, so a toast raised between two ticks is never lost. */
async function pollInspectorToasts() {
  const since = state.inspectorToastSince || new Date(Date.now() - 60000).toISOString();
  let payload;
  try {
    const response = await fetch(withActiveCompany(`/api/inspector/toasts?since=${encodeURIComponent(since)}`));
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
    const response = await fetch(withActiveCompany('/api/inspector/queue'));
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
          <th>${escapeHtml(profileLabels().entity)}</th><th>Case</th><th>Status</th>
          <th title="The severity the Red Alert policy assigned, and why. Hover a badge for the stated reason.">Alert</th>
          <th title="The team accountable for the failing service - who fixes it, as opposed to the ${escapeHtml(profileNoun('entity'))}, which is who is affected.">Owner</th>
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

  fetch(withActiveCompany(`/api/inspector/cases/${encodeURIComponent(caseId)}`))
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

  fetch(withActiveCompany(`/api/inspector/cases/${encodeURIComponent(caseId)}/rca`))
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

  fetch(withActiveCompany('/api/knowledge/documents'))
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
    const company = activeCompanyKey();
    fetch(withActiveCompany('/api/knowledge/documents'))
      .then(async (r) => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
      .then((c) => { if (activeCompanyKey() === company) state.knowledgeCorpus = c; })
      .catch((e) => { if (activeCompanyKey() === company) state.knowledgeCorpus = { error: e.message || 'unavailable' }; })
      .finally(() => { if (activeCompanyKey() === company) renderMemoryBankTab(); });
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
    const response = await fetch(withActiveCompany(`/api/knowledge/search?q=${encodeURIComponent(q)}&top=10`));
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

  fetch(withActiveCompany(`/api/knowledge/document?title=${encodeURIComponent(title)}&type=${encodeURIComponent(sourceType || '')}`))
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
        ${escapeHtml(d.clubName || d.brandName || `Unknown ${profileNoun('entity')}`)} · <code title="${escapeHtml(d.caseId || '')}">${escapeHtml(inspectorCaseLabel(d.caseId))}</code> ·
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
      ? `Matched the whole ${profileNoun('service')}, not this ${profileNoun('step')} specifically.`
      : `Recorded as owning this exact ${profileNoun('step')}.`;
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
    { x: 120, y: 144, w: 92, h: 40, label: 'ScopeResolver', sub: 'no LLM', state: roleState('Scope') || 'pending', agent: 'Scope', title: 'ScopeResolver — resolves the affected entity and which sources apply. Deterministic, no LLM call.' },
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
    : `<span style="color:var(--text-muted);">Unknown ${escapeHtml(profileNoun('entity'))} — ${escapeHtml(club.entityKey || '—')} is not in the catalogue</span>`;

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
    ? `That is <strong>≈${formatCount(perMember)} exceptions per user</strong> across the ${members} user(s) telemetry could attribute — a repeating loop, not ${formatCount(exceptions)} people hitting an error.`
    : `${members} user(s) identified in the window.`;

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
      <span style="color:var(--text-muted)">· ${coverage}% of events carry a user id${anonymous ? `, ${formatCount(anonymous)} anonymous browser(s)` : ''}</span>
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
    <p class="pkg__block-title" style="margin:14px 0 4px;">Request paths — budget ${formatMs(budgetMs)} for this ${escapeHtml(profileNoun('step'))}</p>
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
    const response = await fetch(withActiveCompany(`/api/scout/packages/${encodeURIComponent(packageId)}`));
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
    ${queries.board ? `<p style="font-size:11.5px;color:var(--text-muted);margin:0 0 2px;">Board query — scores every ${escapeHtml(profileNoun('entity'))}'s 5-minute buckets and ranked this one</p><pre style="max-height:180px;overflow:auto;white-space:pre-wrap;font-size:10px;">${escapeHtml(queries.board)}</pre>` : ''}
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
        <li><strong>Blast radius denominator:</strong> how many users were active on this ${escapeHtml(profileNoun('entity'))} overall, not just how many hit the error.</li>
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
        <dt>${escapeHtml(profileLabels().service)} / ${escapeHtml(profileLabels().step)}</dt><dd>${escapeHtml(evidence.domain || '—')}${evidence.stage ? ` / ${escapeHtml(evidence.stage)}` : ' (domain-wide)'}</dd>
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
    const response = await fetch(withActiveCompany(`/api/findings/${encodeURIComponent(findingId)}/doctor-report`));
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

function renderExceptionsHead() {
  const labels = profileLabels();
  const set = (id, text) => { const el = document.getElementById(id); if (el) el.textContent = text; };
  set('exceptions-th-domain', labels.service);
  set('exceptions-th-entity', labels.entity);
  set('exceptions-th-stage', labels.step);
}

function renderExceptionsTable() {
  renderExceptionsHead();
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
    tbody.innerHTML = `<tr><td colspan="11"><div class="state-block">${escapeHtml(message)}</div></td></tr>`;
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
      <td>${escapeHtml(e.source || 'Server')}</td>
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

function affectedKeyMatchesJourney(journey, key) {
  const ids = [journey.id, journey.correlationId].filter(Boolean).map(String);
  if (ids.some((id) => id === key)) return true;
  // Session keys are rawId-seq (ABC123-1). A stored raw id matches every session of that id,
  // and not a longer id that merely shares a prefix.
  const sessionKey = new RegExp('^' + key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '-\\d+$');
  if (ids.some((id) => sessionKey.test(id))) return true;
  const customer = String(journey.customer || '');
  return customer !== '' && customer !== '0' && customer !== '-' && customer === key;
}

function relatedJourneysFor(exception) {
  // journeysAffected arrives newest first. Keep each stored key as the label and for
  // Find Related Journeys. Loaded rows in the exception's domain supply only the outcome;
  // when several sessions match, the latest one decides it.
  const domain = String(exception.domain || '').toLowerCase();
  return (exception.journeysAffected || []).map((id) => {
    const key = String(id);
    const live = (state.journeys || [])
      .filter((j) => !(domain && j.domain && String(j.domain).toLowerCase() !== domain)
        && affectedKeyMatchesJourney(j, key))
      .sort((a, b) => String(b.timestamp || '').localeCompare(String(a.timestamp || '')))[0];
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
    : [{ timestamp: e.lastSeen, memberId: e.memberId, url: e.url, message: e.message, source: e.source }];
  const occurrenceRows = occurrences.map((o, index) => `
    <tr${index >= 3 ? ' class="exception-collapsed" hidden' : ''}>
      <td>${formatRelativeTime(o.timestamp)}</td>
      <td>${escapeHtml(o.memberId || '—')}</td>
      <td>${escapeHtml(o.source || 'Server')}</td>
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
      <dt>Source</dt><dd>${escapeHtml(e.source || 'Server')}</dd>
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
          <thead><tr><th>When</th><th>MemberId</th><th>Source</th><th>URL</th><th>Message</th></tr></thead>
          <tbody>${occurrenceRows || '<tr><td colspan="5">No occurrence details stored.</td></tr>'}</tbody>
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
/** The SLO form's service list is the active company's services, never a fixed set. */
function renderSloDomainOptions() {
  const select = document.getElementById('slo-add-domain');
  if (!select) return;
  const label = document.getElementById('slo-add-domain-label');
  if (label) label.textContent = profileLabels().service;
  const services = profileServices();
  const keep = select.value;
  select.innerHTML = services.length
    ? services.map((s) => `<option value="${escapeHtml(s.key)}">${escapeHtml(s.label)}</option>`).join('')
    : '<option value="">No services defined</option>';
  if (services.some((s) => s.key === keep)) select.value = keep;
}

function renderSettingsTab() {
  renderSloDomainOptions();
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
      if (target === 'kpis') loadKpiSettings();
      if (target === 'tokens') loadAccessTokens();
    });
  });
}

/* Access Tokens (moved here from the standalone AppInsightsTokens.html page). The Web host proxies
   /api/appinsights/tokens to Functions and adds the internal/write secrets server-side. */
const accessTokens = { list: [], timer: null };

function tokenToDate(value) {
  if (!value) return null;
  // Timestamps without an offset are UTC.
  return new Date(/[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`);
}

function tokenCountdown(value) {
  const date = tokenToDate(value);
  if (!date) return '';
  const minutes = Math.round((date - new Date()) / 60000);
  if (minutes <= 0) return `expired ${Math.abs(minutes)} min ago`;
  if (minutes < 120) return `in ${minutes} min`;
  return `in ${Math.round(minutes / 60)} h`;
}

function tokenFormatDate(value) {
  const date = tokenToDate(value);
  return date ? date.toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—';
}

function setTokenStatus(id, ok, text) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = text;
  el.classList.toggle('is-error', !ok);
}

async function tokenErrorText(response) {
  const text = await response.text();
  try {
    const json = JSON.parse(text);
    return json.error || (json.errors && json.errors.join(' ')) || text;
  } catch {
    return text || `HTTP ${response.status}`;
  }
}

async function postTokenJson(url, body) {
  const headers = { 'Content-Type': 'application/json' };
  const csrf = await getAntiforgeryToken();
  if (csrf) headers['X-CSRF-TOKEN'] = csrf;
  return fetch(url, { method: 'POST', headers, body: body === undefined ? undefined : JSON.stringify(body) });
}

function updateTokenModeFields() {
  const mode = document.getElementById('token-refresh-mode').value;
  const auto = mode !== 'manual';
  document.getElementById('token-client-id-field').hidden = mode !== 'service-principal';
  document.getElementById('token-client-secret-field').hidden = mode !== 'service-principal';
  document.getElementById('token-value-required').hidden = auto;
  document.getElementById('token-value-hint').textContent = auto
    ? 'Optional: the token-refresh daemon acquires one on its next run (or use Refresh now).'
    : 'Paste only the accessToken value, not the entire JSON output.';
}

async function loadAccessTokens() {
  const tbody = document.getElementById('tokens-tbody');
  if (!tbody) return;
  try {
    const response = await fetch('/api/appinsights/tokens');
    if (!response.ok) throw new Error(await tokenErrorText(response));
    const data = await response.json();
    accessTokens.list = Array.isArray(data) ? data : (data.tokens || []);
    document.getElementById('token-tenant-keys').innerHTML = accessTokens.list
      .map((t) => `<option value="${escapeHtml(t.tenantKey)}"></option>`).join('');
    renderAccessTokens();
  } catch (err) {
    tbody.innerHTML = `<tr><td colspan="6"><div class="state-block state-block--error">Tokens could not be read: ${escapeHtml(err.message)}</div></td></tr>`;
  }
  // Keeps the countdowns honest while the section is open.
  if (!accessTokens.timer) accessTokens.timer = setInterval(renderAccessTokens, 30000);
}

function renderAccessTokens() {
  const tbody = document.getElementById('tokens-tbody');
  if (!tbody) return;
  if (accessTokens.list.length === 0) {
    tbody.innerHTML = '<tr><td colspan="6"><div class="state-block">No tokens stored yet.</div></td></tr>';
    return;
  }
  tbody.innerHTML = accessTokens.list.map((t) => {
    const mode = t.refreshMode || 'manual';
    const auto = mode !== 'manual';
    const key = escapeHtml(t.tenantKey);
    const status = !t.isActive
      ? '<span class="status-badge">Inactive</span>'
      : t.hasToken === false
        ? '<span class="status-badge status-badge--degraded">Waiting for first refresh</span>'
        : t.isExpired
          ? '<span class="status-badge status-badge--critical">Expired</span>'
          : '<span class="status-badge status-badge--healthy">Active</span>';
    const details = `<div class="form-field__hint">tenant ${escapeHtml(t.aadTenantId || '—')}${mode === 'service-principal' ? ` · client ${escapeHtml(t.clientId || '—')} · secret ${t.hasClientSecret ? 'stored' : 'missing'}` : ''}</div>`;
    const lastError = t.lastRefreshError ? `<div class="form-field__hint is-error">${escapeHtml(t.lastRefreshError)}</div>` : '';
    return `
      <tr>
        <td><strong>${key}</strong><div class="form-field__hint">${escapeHtml(t.email || '')}</div>${t.notes ? `<div class="form-field__hint">${escapeHtml(t.notes)}</div>` : ''}</td>
        <td><code>${escapeHtml(mode)}</code>${details}</td>
        <td>${tokenFormatDate(t.expiresAt)}<div class="form-field__hint">${tokenCountdown(t.expiresAt)}</div></td>
        <td>${tokenFormatDate(t.lastRefreshAt)}<div class="form-field__hint">${t.refreshCount || 0} refresh(es)</div>${lastError}</td>
        <td>${status}</td>
        <td class="kpi-settings__actions">
          <button class="btn btn--icon btn--sm" type="button" data-action="edit-token" data-key="${key}">Edit</button>
          ${auto ? `<button class="btn btn--icon btn--sm" type="button" data-action="refresh-token" data-key="${key}">Refresh now</button>` : ''}
          <button class="btn btn--icon btn--sm btn--ghost" type="button" data-action="deactivate-token" data-key="${key}">Deactivate</button>
        </td>
      </tr>`;
  }).join('');
}

function fillTokenForm(tenantKey) {
  const t = accessTokens.list.find((x) => x.tenantKey === tenantKey);
  if (!t) return;
  document.getElementById('token-tenant-key').value = t.tenantKey;
  document.getElementById('token-email').value = t.email || '';
  document.getElementById('token-refresh-mode').value = t.refreshMode || 'manual';
  document.getElementById('token-aad-tenant').value = t.aadTenantId || '';
  document.getElementById('token-client-id').value = t.clientId || '';
  document.getElementById('token-value').value = '';
  updateTokenModeFields();
  document.getElementById('token-value').focus();
}

function wireAccessTokenSettings() {
  const mode = document.getElementById('token-refresh-mode');
  if (!mode) return;
  mode.addEventListener('change', updateTokenModeFields);
  updateTokenModeFields();

  document.getElementById('token-save').addEventListener('click', async () => {
    const body = {
      tenantKey: document.getElementById('token-tenant-key').value.trim(),
      userEmail: document.getElementById('token-email').value.trim(),
      token: document.getElementById('token-value').value.trim(),
      refreshMode: mode.value,
      aadTenantId: document.getElementById('token-aad-tenant').value.trim(),
      clientId: document.getElementById('token-client-id').value.trim(),
      clientSecret: document.getElementById('token-client-secret').value,
    };
    if (!body.tenantKey || !body.userEmail) { setTokenStatus('token-save-status', false, 'Tenant key and email are required.'); return; }
    if (body.refreshMode === 'manual' && !body.token) { setTokenStatus('token-save-status', false, 'A manual token needs the access token value.'); return; }
    try {
      const response = await postTokenJson('/api/appinsights/tokens', body);
      if (!response.ok) { setTokenStatus('token-save-status', false, `Error: ${await tokenErrorText(response)}`); return; }
      setTokenStatus('token-save-status', true, `Token saved for '${body.tenantKey}'.`);
      document.getElementById('token-value').value = '';
      loadAccessTokens();
    } catch (err) {
      setTokenStatus('token-save-status', false, `Error: ${err.message}`);
    } finally {
      document.getElementById('token-client-secret').value = '';
    }
  });

  document.getElementById('tokens-tbody').addEventListener('click', async (event) => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;
    const key = button.dataset.key;
    if (button.dataset.action === 'edit-token') { fillTokenForm(key); return; }
    if (button.dataset.action === 'deactivate-token' && !confirm(`Deactivate the token for ${key}?`)) return;
    const verb = button.dataset.action === 'refresh-token' ? 'refresh' : 'deactivate';
    button.disabled = true;
    try {
      const response = await postTokenJson(`/api/appinsights/tokens/${encodeURIComponent(key)}/${verb}`);
      if (!response.ok) {
        setTokenStatus('token-table-status', false, `${key}: ${await tokenErrorText(response)}`);
      } else if (verb === 'refresh') {
        const data = await response.json().catch(() => ({}));
        setTokenStatus('token-table-status', true, `${key}: refreshed, expires ${tokenFormatDate(data.expiresAt)}.`);
      } else {
        setTokenStatus('token-table-status', true, `${key}: deactivated.`);
      }
    } catch (err) {
      setTokenStatus('token-table-status', false, `${key}: ${err.message}`);
    }
    loadAccessTokens();
  });
}

/** Deep link (?settings=tokens) used by the retired standalone token pages. */
function openSettingsSectionFromUrl() {
  const section = new URLSearchParams(window.location.search).get('settings');
  if (!section) return;
  const button = document.querySelector(`.settings-subnav__item[data-settings-section="${CSS.escape(section)}"]`);
  if (!button) return;
  activateTab('settings');
  button.click();
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
  showOverviewWarningToast(overview);
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
    sql: `stored per-company records (${m.rowCount}, ${win})`,
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
    await loadAllData();
    state.dashboardChartsLoading = false;
    renderAllTabs();
    updateLastRefreshTimestamp();
    if (!silent) {
      const m = state.journeysMeta || {};
      const kind = m.source === 'sql' ? 'success' : m.source === 'error' ? 'critical' : 'info';
      showToast(
        m.source === 'error' ? 'Refresh incomplete' : 'Refreshed',
        `${profileLabels().flow}: ${journeysSourceLabel(m)}.${m.warning ? ` ${m.warning}` : ''} Exceptions: ${exceptionsSourceLabel()}.`,
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
    const response = await fetch(withActiveCompany('/api/settings/kql'));
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
    const response = await fetch(withActiveCompany(`/api/settings/kql/${encodeURIComponent(key)}`));
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
    const response = await fetch(withActiveCompany('/api/settings/telemetry-sources'));
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
    const response = await fetch(withActiveCompany('/api/settings/kql'));
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
 * Contract v6 §1/§6: the single place a read is scoped to the active company. Every company-scoped
 * read in the portal (journeys, entities, exceptions, overview, analytics, health, triage, monkeys,
 * scouts, inspector, findings, knowledge, daemons, club inventory and the company-dependent
 * settings) goes through here, so a switch can never leave one call reading another company.
 */
function withActiveCompany(url) {
  const key = activeCompanyKey();
  if (!key) return url;
  return `${url}${url.includes('?') ? '&' : '?'}profileKey=${encodeURIComponent(key)}`;
}

/* --- Company theme (contract v6 §2) --------------------------------------------------------- */

const COMPANY_THEME_NAVY = '#0b1e45';
const COMPANY_THEME_PROPERTIES = ['--company-primary', '--company-accent', '--company-on-primary', '--company-header-bg', '--company-header-fg'];

function isThemeColor(value) {
  return /^#[0-9a-f]{6}$/i.test(String(value || ''));
}

/** WCAG relative luminance of a #rrggbb colour. */
function themeLuminance(hex) {
  const channel = (i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
}

/** White or navy, whichever contrasts more with the background. */
function themeTextOn(hex) {
  const bg = themeLuminance(hex);
  const contrast = (fg) => (Math.max(bg, fg) + 0.05) / (Math.min(bg, fg) + 0.05);
  return contrast(1) >= contrast(themeLuminance(COMPANY_THEME_NAVY)) ? '#ffffff' : COMPANY_THEME_NAVY;
}

/** The name shown next to the title: the theme's short name, else the display name. */
function companyDisplayName(profile) {
  if (!profile) return '';
  const short = profile.theme && typeof profile.theme.shortName === 'string' ? profile.theme.shortName.trim() : '';
  return short || profile.displayName || profile.profileKey || '';
}

/**
 * Applies the active company's colours as CSS custom properties consumed by the header, tab nav,
 * primary buttons and banner. No theme (or an invalid colour) falls back to the default palette.
 */
function applyCompanyTheme(profile) {
  const root = document.documentElement;
  const theme = (profile && profile.theme) || {};
  const primary = isThemeColor(theme.primaryColor) ? theme.primaryColor : null;
  const accent = isThemeColor(theme.accentColor) ? theme.accentColor : null;
  COMPANY_THEME_PROPERTIES.forEach((name) => root.style.removeProperty(name));
  if (primary) {
    const text = themeTextOn(primary);
    root.style.setProperty('--company-primary', primary);
    root.style.setProperty('--company-on-primary', text);
    root.style.setProperty('--company-header-bg', primary);
    root.style.setProperty('--company-header-fg', text);
  }
  if (accent) root.style.setProperty('--company-accent', accent);
  document.body.classList.toggle('has-company-theme', !!primary);
  document.body.classList.toggle('company-theme--light-text', !!primary && themeTextOn(primary) === '#ffffff');
  document.body.dataset.company = (profile && profile.profileKey) || '';

  const name = companyDisplayName(profile);
  const badge = document.getElementById('company-name');
  if (badge) {
    badge.textContent = name;
    badge.hidden = !name;
    badge.title = profile && profile.displayName ? profile.displayName : name;
  }
  document.title = name ? `${name} · Agentic Operations Portal` : 'Agentic Operations Portal';
}

/** Reads the active company's full profile (theme, labels, services) and applies it everywhere. */
async function loadActiveProfile() {
  const key = activeCompanyKey();
  let profile = null;
  if (key) {
    try {
      const response = await fetch(`/api/settings/company-profiles/${encodeURIComponent(key)}`);
      if (response.ok) profile = await response.json();
    } catch (err) {
      console.warn('active company profile unreadable.', err);
    }
  }
  if (activeCompanyKey() !== key) return;
  state.activeProfile = profile && profile.profile ? profile.profile : profile;
  applyCompanyTheme(state.activeProfile);
  renderDomainFilter();
  renderClubSelect({ entities: [], disabled: true });
  renderJourneysHead();
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

/** Contract v6 §6: a switch applies the new theme, drops everything read for the previous company
 *  and reloads. The visible tab is redrawn as soon as the reads land; the lazily loaded tabs
 *  (Memory Bank, Daemons, Simian Army, Platform Health backend, Application Map) start empty and
 *  read the new company the next time they are opened. No full page reload. */
async function refreshForCompanyChange() {
  resetCompanyScopedState();
  await loadActiveProfile();
  showDataTablesLoading();
  renderAllTabs();
  reloadKpiViewsForCompany();
  const generation = filterRequestGeneration;
  try {
    await Promise.all([loadClubsForDomain(state.filters.domain), loadAllData()]);
  } catch (err) {
    console.error(err);
  }
  if (generation !== filterRequestGeneration) return;
  state.tablesLoading = false;
  state.dashboardChartsLoading = false;
  setFiltersLocked(false);
  renderAllTabs();
  updateLastRefreshTimestamp();
  reloadVisibleLazyTab();
  const settingsPanel = document.getElementById('tab-settings');
  if (settingsPanel && !settingsPanel.hidden) {
    loadCompanyProfiles();
    reloadVisibleSettingsSection();
  }
}

/** Everything held in memory that belongs to one company. */
function resetCompanyScopedState() {
  filterRequestGeneration += 1;
  state.journeys = [];
  state.exceptions = [];
  state.overview = null;
  state.dashboardMetrics = [];
  state.dashboardMeta = null;
  state.serviceComparison = [];
  state.domainComparisonStale = true;
  state.journeyLabels = null;
  state.journeysMeta = { source: 'snapshot', rowCount: 0, warning: '' };
  state.exceptionsMeta = { source: '', warning: '', rowCount: 0, win: '' };
  state.selectedExceptionId = null;
  state.rcaByCase = {};
  state.knowledgeCorpus = null;
  state.memory = { filter: '', type: 'all', search: null };
  state.clubOptions = [];
  state.filters.club = '';
  state.rowCap = INIT_ROW_CAP;
  state.clubInventory = [];
  state.clubInventoryLoaded = false;
  state.scoutCycleFilter = null;
  state.inspectorQueue = null;
  state.inspectorCaseDetails = {};
  state.inspectorDetailCaseId = null;
  state.inspectorAgentFilter = null;
  state.inspectorOpenGroups = [];
  rwWatch.focus = null;
  rwWatch.stage = 1;
  rwWatch.application = null;
  rwWatch.applicationRequest = (rwWatch.applicationRequest || 0) + 1;
  rwWatch.hotelBusiness = null;
  rwWatch.businessRequest = (rwWatch.businessRequest || 0) + 1;
  pipelineFlowSignature = null;
  const charts = document.getElementById('analytics-charts');
  if (charts) charts.dataset.signature = '';
  closeSidePanel();
  closeModal();
  if (window.DaemonsTab && typeof window.DaemonsTab.onCompanyChange === 'function') window.DaemonsTab.onCompanyChange();
}

/** The tabs that load on entry are re-entered so the one on screen reads the new company now. */
function reloadVisibleLazyTab() {
  const tab = state.activeTab;
  if (tab === 'memory') renderMemoryBankTab();
  if (tab === 'monkeys') reloadMonkeysForFilters();
  if (tab === 'club-map') loadClubInventory({ force: true });
}

function reloadVisibleSettingsSection() {
  const section = document.querySelector('.settings-content .settings-section.is-active');
  const target = section ? section.dataset.settingsSection : '';
  if (target === 'kql') loadKqlQueries();
  if (target === 'sources') loadTelemetrySources();
}

/** Platform Health, KPI settings and the triage list are scoped to the active company (contract v3
 *  §1.6), so a switch drops what was read for the previous one and reads again. Daemons is
 *  platform-wide and is left alone. */
function reloadKpiViewsForCompany() {
  rwWatch.healthRequest += 1;
  rwWatch.healthInFlight = null;
  rwWatch.health = null;
  rwWatch.kpiDefs = null;
  rwWatch.buckets = {};
  rwWatch.triageRequest += 1;
  rwWatch.triage = null;
  if (rwWatch.ready) {
    loadPillarHealth();
    loadTriageInvestigations();
  }
  kpiSettings.selectedId = null;
  loadKpiSettings();
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

// Settings — Onboarding draft + suggested KPIs (Spec 1000 contract v4 §5, AG7)
// ============================================================================
// The server measures the telemetry and decides every suggestion; this view only shows them and
// forwards each suggested definition verbatim to POST /api/settings/kpis (it stays disabled).

const onboardingDraft = { profile: null, suggestions: [], created: {} };

function wireOnboardingDraft() {
  const editor = document.getElementById('company-editor');
  if (!editor || document.getElementById('onboarding-draft-card')) return;

  const card = document.createElement('div');
  card.className = 'settings-card';
  card.id = 'onboarding-draft-card';
  card.innerHTML = `
    <h3 class="settings-card__group-title">Onboard a company from telemetry</h3>
    <p class="tab-panel__note">Measures one registered telemetry source and drafts a <strong>disabled</strong> profile plus <strong>disabled</strong> KPIs. Nothing is saved until you press a save or create button.</p>
    <div class="form-grid">
      <div class="form-field"><label for="onboarding-alias">Telemetry source alias</label><input type="text" id="onboarding-alias" class="settings-input" placeholder="e.g. contoso-prod"></div>
      <div class="form-field"><label for="onboarding-environment">Environment</label><input type="text" id="onboarding-environment" class="settings-input" value="production"></div>
      <div class="form-field"><label for="onboarding-profile-key">Profile key</label><input type="text" id="onboarding-profile-key" class="settings-input" placeholder="defaults to the alias"></div>
      <div class="form-field"><label for="onboarding-display-name">Display name</label><input type="text" id="onboarding-display-name" class="settings-input" placeholder="defaults to the alias"></div>
      <div class="form-field"><label for="onboarding-hours">Window (hours)</label><input type="number" id="onboarding-hours" class="settings-input" min="1" max="168" value="24"></div>
    </div>
    <div class="llm-client-settings__actions">
      <button class="btn btn--sm" id="onboarding-draft-run" type="button">Build draft</button>
      <button class="btn btn--sm" id="onboarding-draft-open" type="button" hidden>Open draft profile in editor</button>
      <span class="llm-client-settings__status" id="onboarding-draft-status" aria-live="polite"></span>
    </div>
    <div id="onboarding-draft-summary"></div>
    <div id="onboarding-suggested-kpis"></div>`;
  editor.after(card);

  document.getElementById('onboarding-draft-run').addEventListener('click', buildOnboardingDraft);
  document.getElementById('onboarding-draft-open').addEventListener('click', openOnboardingDraftInEditor);
}

async function buildOnboardingDraft() {
  const alias = document.getElementById('onboarding-alias').value.trim();
  if (!alias) {
    setStatus('onboarding-draft-status', 'Enter the telemetry source alias registered in Settings > Telemetry sources.', 'error');
    return;
  }
  const hours = Number.parseInt(document.getElementById('onboarding-hours').value, 10);
  const payload = {
    alias,
    environment: document.getElementById('onboarding-environment').value.trim() || undefined,
    profileKey: document.getElementById('onboarding-profile-key').value.trim() || undefined,
    displayName: document.getElementById('onboarding-display-name').value.trim() || undefined,
    hours: Number.isFinite(hours) ? hours : undefined,
  };

  const runButton = document.getElementById('onboarding-draft-run');
  runButton.disabled = true;
  setStatus('onboarding-draft-status', 'Measuring the telemetry source…', null);
  try {
    const { ok, status, body } = await companyWrite('/api/onboarding/draft', 'POST', payload);
    if (!ok) {
      setStatus('onboarding-draft-status', (body && body.error) || `Draft failed (HTTP ${status}).`, 'error');
      return;
    }
    onboardingDraft.profile = (body.draft && body.draft.profile) || null;
    onboardingDraft.suggestions = Array.isArray(body.suggestedKpis) ? body.suggestedKpis : [];
    onboardingDraft.created = {};
    document.getElementById('onboarding-draft-open').hidden = !onboardingDraft.profile;
    renderOnboardingDraftSummary(body.draft || {});
    renderSuggestedKpis();
    setStatus('onboarding-draft-status', `Draft built: ${onboardingDraft.suggestions.length} suggested KPI(s).`, 'ok');
  } catch (err) {
    setStatus('onboarding-draft-status', err.message, 'error');
  } finally {
    runButton.disabled = false;
  }
}

function renderOnboardingDraftSummary(draft) {
  const host = document.getElementById('onboarding-draft-summary');
  if (!host) return;
  const profile = draft.profile || {};
  const warnings = Array.isArray(draft.warnings) ? draft.warnings : [];
  host.innerHTML = `
    <p class="tab-panel__note">
      <strong>${escapeHtml(profile.displayName || profile.profileKey || '')}</strong> (<code>${escapeHtml(profile.profileKey || '')}</code>) ·
      readiness <strong>${escapeHtml(String(draft.readiness || '—'))}</strong> ·
      ${(profile.services || []).length} service(s) · ${(profile.partners || []).length} partner(s) ·
      ${(profile.entityIdentitySources || []).length} identity source(s)
    </p>
    ${warnings.length === 0 ? '' : `<ul class="company-problems">${warnings.map((w) => `<li><strong>${escapeHtml(String(w.severity || ''))}</strong> ${escapeHtml(w.message || '')}</li>`).join('')}</ul>`}`;
}

function renderSuggestedKpis() {
  const host = document.getElementById('onboarding-suggested-kpis');
  if (!host) return;
  const suggestions = onboardingDraft.suggestions;
  if (suggestions.length === 0) {
    host.innerHTML = '<div class="state-block">No KPI was suggested for this draft - see the notes above.</div>';
    return;
  }
  const pending = suggestions.filter((s) => !onboardingDraft.created[s.key]).length;
  host.innerHTML = `
    <h4 class="settings-card__group-title">Suggested KPIs (created disabled)</h4>
    <p class="tab-panel__note">The KPI API only accepts a profile that is saved <em>and enabled</em>: save the draft, enable it, then create these. Each one is stored with enabled=false; switch it on from Settings > KPIs after review.</p>
    <div class="llm-client-settings__actions">
      <button class="btn btn--sm" id="onboarding-kpis-create-all" type="button" ${pending === 0 ? 'disabled' : ''}>Create all (disabled)</button>
    </div>
    <div class="table-wrap">
      <table class="data-table" id="onboarding-suggested-kpis-table">
        <thead><tr><th>KPI</th><th>Domain</th><th>Grain</th><th>Source</th><th>Detection</th><th>Why</th><th>Actions</th></tr></thead>
        <tbody>${suggestions.map((s) => {
          const d = s.definition || {};
          const created = onboardingDraft.created[s.key];
          return `
          <tr data-suggestion="${escapeHtml(s.key)}">
            <td>${escapeHtml(s.title || d.kpi || '')}</td>
            <td><code>${escapeHtml(d.domain || '')}</code></td>
            <td>${escapeHtml(d.grain || '')}</td>
            <td><code>${escapeHtml(d.sourceKey || '')}</code></td>
            <td>${escapeHtml(d.detectionMethod || '')} <code>${escapeHtml(d.methodParamsJson || '')}</code></td>
            <td>${escapeHtml(s.reason || '')}</td>
            <td>${created
              ? `<span class="status-badge status-badge--sev-low">${escapeHtml(created)}</span>`
              : '<button class="btn btn--icon btn--sm" data-action="create-suggested-kpi">Create (disabled)</button>'}</td>
          </tr>`;
        }).join('')}</tbody>
      </table>
    </div>
    <div id="onboarding-kpis-errors"></div>`;

  host.querySelectorAll('button[data-action="create-suggested-kpi"]').forEach((btn) => {
    btn.addEventListener('click', () => createSuggestedKpis([btn.closest('tr').dataset.suggestion]));
  });
  const createAll = document.getElementById('onboarding-kpis-create-all');
  if (createAll) {
    createAll.addEventListener('click', () => createSuggestedKpis(suggestions.filter((s) => !onboardingDraft.created[s.key]).map((s) => s.key)));
  }
}

/** Sequential on purpose: the KPI API refuses a duplicate key with a 400, and parallel writes of
 *  one profile's KPIs would race that check. */
async function createSuggestedKpis(keys) {
  const errors = [];
  setStatus('onboarding-draft-status', `Creating ${keys.length} KPI(s)…`, null);
  for (const key of keys) {
    const suggestion = onboardingDraft.suggestions.find((s) => s.key === key);
    if (!suggestion || onboardingDraft.created[key]) continue;
    try {
      const response = await fetch('/api/settings/kpis', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
        body: JSON.stringify(suggestion.definition),
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) {
        const reasons = Array.isArray(body.errors) ? body.errors : [body.error || `HTTP ${response.status}`];
        errors.push(`${suggestion.title || key}: ${reasons.join(' ')}`);
        continue;
      }
      onboardingDraft.created[key] = body.kpiDefinitionId ? `Created #${body.kpiDefinitionId} (disabled)` : 'Created (disabled)';
    } catch (err) {
      errors.push(`${suggestion.title || key}: ${err.message}`);
    }
  }
  renderSuggestedKpis();
  const errorHost = document.getElementById('onboarding-kpis-errors');
  if (errorHost) {
    errorHost.innerHTML = errors.length === 0 ? '' : `
      <div class="state-block state-block--error">
        <strong>${errors.length} KPI(s) not created:</strong>
        <ul class="company-problems">${errors.map((e) => `<li>${escapeHtml(e)}</li>`).join('')}</ul>
      </div>`;
  }
  setStatus('onboarding-draft-status', errors.length === 0 ? 'Suggested KPI(s) created, all disabled.' : `${errors.length} KPI(s) not created.`, errors.length === 0 ? 'ok' : 'error');
  if (errors.length < keys.length) loadKpiSettings();
}

/** The draft goes through the same editor and the same PUT as any other profile edit. */
function openOnboardingDraftInEditor() {
  const card = document.getElementById('company-editor');
  const profile = onboardingDraft.profile;
  if (!card || !profile) return;
  card.hidden = false;
  card.dataset.key = profile.profileKey;
  document.getElementById('company-editor-title').textContent = `New profile ${profile.profileKey} — draft`;
  document.getElementById('company-editor-text').value = JSON.stringify(profile, null, 2);
  document.getElementById('company-editor-hint').innerHTML =
    `<strong>Draft</strong> — <code>${escapeHtml(profile.profileKey)}</code> is saved <strong>disabled</strong>. Review it, save, then set enabled=true and save again before creating the suggested KPIs.`;
  renderCompanyProblems([]);
  setStatus('company-editor-status', 'Draft loaded — nothing is saved yet.', null);
  card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

// Settings — KPI definitions (Spec 1000)
// ============================================================================

const KPI_METHOD_LABELS = { staticThreshold: 'Static threshold', baselinePercent: 'Baseline percent', robustZScore: 'Robust z-score' };
const KPI_METHOD_HINTS = {
  staticThreshold: 'Requires {"threshold": number}. Direction must be Increase or Decrease.',
  baselinePercent: 'Requires {"lookbackDays": 1..28, "thresholdPercent": > 0}.',
  robustZScore: 'Requires {"windowBuckets": 4..1000, "zThreshold": > 0}.',
};
const kpiSettings = { definitions: [], selectedId: null, auditRequest: 0 };

function kpiPillarLabel(pillar) {
  return RW_PILLAR_DISPLAY[String(pillar || '').toLowerCase()] || pillar || '—';
}

function kpiTitle(definition) {
  return `${kpiPillarLabel(definition.pillar)} · ${definition.kpi}${definition.stage ? ` · ${definition.stage}` : ''} · ${domainLabel(definition.domain)}${kpiGrain(definition) === 'entity' ? ' · per entity' : ''}`;
}

/** Contract v4 §1: grain is 'domain' | 'entity'; rows from an older API have none and are domain. */
function kpiGrain(definition) {
  return String(definition.grain || 'domain').toLowerCase() === 'entity' ? 'entity' : 'domain';
}

function kpiGrainCell(definition) {
  return kpiGrain(definition) === 'entity'
    ? `Entity <span class="rw-scope">max ${escapeHtml(definition.maxEntitiesPerBucket ?? 500)}</span>`
    : 'Domain';
}

function kpiCap(value) {
  const text = String(value ?? '');
  return text ? text.charAt(0).toUpperCase() + text.slice(1) : '—';
}

async function loadKpiSettings() {
  const tbody = document.getElementById('kpi-settings-tbody');
  if (!tbody) return;
  try {
    const response = await fetch(withActiveCompany('/api/settings/kpis'));
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const body = await response.json();
    kpiSettings.definitions = Array.isArray(body) ? body : [];
    renderKpiSettings();
  } catch (err) {
    console.warn('/api/settings/kpis unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="12"><div class="state-block state-block--error">KPI definitions could not be read.</div></td></tr>';
  }
  if (kpiSettings.selectedId != null) loadKpiAudit(kpiSettings.selectedId);
}

function renderKpiSettings() {
  const tbody = document.getElementById('kpi-settings-tbody');
  if (!tbody) return;
  const definitions = kpiSettings.definitions;
  tbody.innerHTML = definitions.length === 0
    ? '<tr><td colspan="12"><div class="state-block">No KPI definitions configured.</div></td></tr>'
    : definitions.map((d) => `
      <tr data-kpi-id="${escapeHtml(d.kpiDefinitionId)}"${d.kpiDefinitionId === kpiSettings.selectedId ? ' class="is-selected"' : ''}>
        <td>${escapeHtml(kpiPillarLabel(d.pillar))}</td>
        <td>${escapeHtml(d.kpi)}${d.stage ? ` <span class="rw-scope">${escapeHtml(d.stage)}</span>` : ''}</td>
        <td>${escapeHtml(domainLabel(d.domain))}</td>
        <td>${kpiGrainCell(d)}</td>
        <td>${escapeHtml(kpiCap(d.direction))}</td>
        <td>${escapeHtml(String(d.aggregation || '').toUpperCase())}</td>
        <td>${escapeHtml(KPI_METHOD_LABELS[d.detectionMethod] || d.detectionMethod)} <code>${escapeHtml(d.methodParamsJson)}</code></td>
        <td>${escapeHtml(d.consecutiveCount)}</td>
        <td>${escapeHtml(d.cooldownMinutes)}</td>
        <td>${d.enabled ? '<span class="status-badge status-badge--healthy">Enabled</span>' : '<span class="status-badge">Disabled</span>'}</td>
        <td>${d.updatedAt ? `${formatRelativeTime(d.updatedAt)} by ${escapeHtml(d.updatedBy || 'unknown')}` : '—'}</td>
        <td class="kpi-settings__actions">
          <button class="btn btn--icon btn--sm" type="button" data-action="edit-kpi">Edit</button>
          <button class="btn btn--icon btn--sm btn--ghost" type="button" data-action="kpi-history">History</button>
        </td>
      </tr>`).join('');
}

function kpiSetErrors(errors) {
  const list = document.getElementById('kpi-editor-errors');
  if (!list) return;
  list.hidden = errors.length === 0;
  list.innerHTML = errors.map((error) => `<li>${escapeHtml(error)}</li>`).join('');
}

function kpiRenderMethodHint() {
  const method = document.getElementById('kpi-edit-method').value;
  document.getElementById('kpi-edit-params-hint').textContent = KPI_METHOD_HINTS[method] || '';
}

function openKpiEditor(id) {
  const definition = kpiSettings.definitions.find((d) => d.kpiDefinitionId === id);
  const card = document.getElementById('kpi-editor');
  if (!definition || !card) return;
  kpiSettings.selectedId = id;
  card.hidden = false;
  card.dataset.kpiId = String(id);
  document.getElementById('kpi-editor-title').textContent = `Edit ${kpiTitle(definition)}`;
  document.getElementById('kpi-edit-direction').value = String(definition.direction || '').toLowerCase();
  document.getElementById('kpi-edit-frequency').value = definition.evaluationFrequency || '';
  document.getElementById('kpi-edit-bucket').value = definition.bucketSize || '';
  document.getElementById('kpi-edit-lookback').value = definition.lookbackWindow || '';
  document.getElementById('kpi-edit-sample').value = definition.sampleSize ?? '';
  document.getElementById('kpi-edit-min-obs').value = definition.minObservationsPerBucket ?? '';
  document.getElementById('kpi-edit-grain').value = kpiGrain(definition);
  document.getElementById('kpi-edit-max-entities').value = definition.maxEntitiesPerBucket ?? 500;
  document.getElementById('kpi-edit-method').value = definition.detectionMethod || 'baselinePercent';
  document.getElementById('kpi-edit-params').value = definition.methodParamsJson || '';
  document.getElementById('kpi-edit-consecutive').value = definition.consecutiveCount ?? '';
  document.getElementById('kpi-edit-cooldown').value = definition.cooldownMinutes ?? '';
  document.getElementById('kpi-edit-enabled').checked = !!definition.enabled;
  kpiRenderMethodHint();
  kpiSetErrors([]);
  setStatus('kpi-editor-status', '', null);
  renderKpiSettings();
  loadKpiAudit(id);
  card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

/** Starts from the row exactly as GET returned it and overwrites only the edited fields; the
 *  backend validates every range and its 400 errors are shown verbatim. */
async function saveKpiEditor() {
  const card = document.getElementById('kpi-editor');
  const id = Number(card.dataset.kpiId);
  const original = kpiSettings.definitions.find((d) => d.kpiDefinitionId === id);
  if (!original) return;
  // An empty box would reach the backend as null and be stored as 0, so it is refused here instead.
  const missing = [];
  const requiredInt = (elementId, label) => {
    const raw = document.getElementById(elementId).value.trim();
    const value = Number(raw);
    if (raw === '' || !Number.isInteger(value)) {
      missing.push(`${label} must be a whole number.`);
      return null;
    }
    return value;
  };
  const payload = {
    ...original,
    direction: document.getElementById('kpi-edit-direction').value,
    evaluationFrequency: document.getElementById('kpi-edit-frequency').value.trim(),
    bucketSize: document.getElementById('kpi-edit-bucket').value.trim(),
    lookbackWindow: document.getElementById('kpi-edit-lookback').value.trim(),
    sampleSize: requiredInt('kpi-edit-sample', 'Sample size'),
    minObservationsPerBucket: requiredInt('kpi-edit-min-obs', 'Minimum observations'),
    grain: document.getElementById('kpi-edit-grain').value,
    maxEntitiesPerBucket: requiredInt('kpi-edit-max-entities', 'Max entities per bucket'),
    detectionMethod: document.getElementById('kpi-edit-method').value,
    methodParamsJson: document.getElementById('kpi-edit-params').value.trim(),
    consecutiveCount: requiredInt('kpi-edit-consecutive', 'Consecutive buckets'),
    cooldownMinutes: requiredInt('kpi-edit-cooldown', 'Cooldown (minutes)'),
    enabled: document.getElementById('kpi-edit-enabled').checked,
    changedBy: document.getElementById('kpi-edit-changed-by').value.trim(),
  };
  if (missing.length > 0) {
    kpiSetErrors(missing);
    setStatus('kpi-editor-status', 'Not saved.', 'error');
    return;
  }
  setStatus('kpi-editor-status', 'Saving…', null);
  kpiSetErrors([]);
  const saveButton = document.getElementById('kpi-editor-save');
  saveButton.disabled = true;
  try {
    const response = await fetch('/api/settings/kpis', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await getAntiforgeryToken() },
      body: JSON.stringify(payload),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      const errors = Array.isArray(body.errors) ? body.errors : [body.error || `Request failed: ${response.status}`];
      kpiSetErrors(errors);
      setStatus('kpi-editor-status', `Not saved (HTTP ${response.status}).`, 'error');
      return;
    }
    setStatus('kpi-editor-status', 'Saved.', 'ok');
    showToast('KPIs', `${kpiTitle(body.kpi ? body : original)} saved.`, 'success');
    await loadKpiSettings();
    if (rwWatch.ready) loadPillarHealth();
  } catch (err) {
    setStatus('kpi-editor-status', err.message, 'error');
  } finally {
    saveButton.disabled = false;
  }
}

function kpiSnapshotField(snapshot, name) {
  if (!snapshot) return null;
  const key = Object.keys(snapshot).find((k) => k.toLowerCase() === name.toLowerCase());
  return key === undefined ? null : snapshot[key];
}

async function loadKpiAudit(id) {
  const tbody = document.getElementById('kpi-audit-tbody');
  const caption = document.getElementById('kpi-audit-caption');
  if (!tbody) return;
  const definition = kpiSettings.definitions.find((d) => d.kpiDefinitionId === id);
  if (caption) caption.textContent = definition ? kpiTitle(definition) : `KPI ${id}`;
  // A slow answer for a KPI selected earlier must not overwrite the history of the current one.
  const request = kpiSettings.auditRequest + 1;
  kpiSettings.auditRequest = request;
  tbody.innerHTML = '<tr><td colspan="8"><div class="state-block">Loading history…</div></td></tr>';
  try {
    const response = await fetch(withActiveCompany(`/api/settings/kpis/${encodeURIComponent(id)}/audit`));
    if (!response.ok) throw new Error(`Request failed: ${response.status}`);
    const entries = await response.json();
    if (kpiSettings.auditRequest !== request) return;
    if (!Array.isArray(entries) || entries.length === 0) {
      tbody.innerHTML = '<tr><td colspan="8"><div class="state-block">No changes recorded yet.</div></td></tr>';
      return;
    }
    const cell = (value) => escapeHtml(value == null || value === '' ? '—' : String(value));
    tbody.innerHTML = entries.map((entry) => {
      let snapshot = null;
      try { snapshot = JSON.parse(entry.snapshotJson); } catch { snapshot = null; }
      if (!snapshot) {
        return `<tr><td>${formatRelativeTime(entry.changedAt)}</td><td>${cell(entry.changedBy)}</td><td colspan="6"><code>${cell(entry.snapshotJson)}</code></td></tr>`;
      }
      const enabled = kpiSnapshotField(snapshot, 'Enabled');
      return `<tr>
        <td title="${escapeHtml(new Date(entry.changedAt).toLocaleString())}">${formatRelativeTime(entry.changedAt)}</td>
        <td>${cell(entry.changedBy)}</td>
        <td>${cell(kpiSnapshotField(snapshot, 'Direction'))}</td>
        <td>${cell(kpiSnapshotField(snapshot, 'DetectionMethod'))}</td>
        <td><code>${cell(kpiSnapshotField(snapshot, 'MethodParamsJson'))}</code></td>
        <td>${cell(kpiSnapshotField(snapshot, 'ConsecutiveCount'))}</td>
        <td>${cell(kpiSnapshotField(snapshot, 'CooldownMinutes'))}</td>
        <td>${enabled == null ? '—' : (enabled === true || enabled === 1 ? 'Yes' : 'No')}</td>
      </tr>`;
    }).join('');
  } catch (err) {
    if (kpiSettings.auditRequest !== request) return;
    console.warn('/api/settings/kpis audit unreachable.', err);
    tbody.innerHTML = '<tr><td colspan="8"><div class="state-block state-block--error">History could not be read.</div></td></tr>';
  }
}

function wireKpiSettings() {
  const tbody = document.getElementById('kpi-settings-tbody');
  if (!tbody) return;
  tbody.addEventListener('click', (event) => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;
    const id = Number(button.closest('tr').dataset.kpiId);
    if (button.dataset.action === 'edit-kpi') openKpiEditor(id);
    if (button.dataset.action === 'kpi-history') {
      kpiSettings.selectedId = id;
      renderKpiSettings();
      loadKpiAudit(id);
    }
  });
  document.getElementById('kpi-edit-method').addEventListener('change', kpiRenderMethodHint);
  document.getElementById('kpi-editor-save').addEventListener('click', saveKpiEditor);
  document.getElementById('kpi-editor-close').addEventListener('click', () => {
    document.getElementById('kpi-editor').hidden = true;
  });
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
    resetClubMapPage();
    renderClubMapTable();
    renderClubMapOverview();
  };
  brand?.addEventListener('change', onChange);
  product?.addEventListener('change', onChange);
  status?.addEventListener('change', onChange);
  platform?.addEventListener('change', onChange);
  q?.addEventListener('input', onChange);
}

/** Application Map product columns (fixed order). */
const CLUB_MAP_PRODUCTS = ['Cars', 'Cruises', 'Hotels', 'Flights'];

/** The inventory's product columns are its own data model, so they are drawn here, not in the HTML. */
function renderClubMapProductHeaders() {
  const row = document.getElementById('club-map-product-headers');
  if (!row || row.children.length) return;
  row.innerHTML = CLUB_MAP_PRODUCTS
    .map((product) => `<th data-sort="product${escapeHtml(product)}" class="club-map-th--product">${escapeHtml(product)}</th>`)
    .join('');
}

/** Known platform families shown on the Application Map overview. */
const CLUB_MAP_PLATFORMS = ['Fusion', 'Hybrid', 'SOR', 'NG'];

/**
 * NG rows store ClubId = this offset + Members.MemberClassId so they never
 * collide with SaveOn VacationClub IDs. The UI subtracts it for display.
 */
const NG_MEMBER_CLASS_CLUB_ID_OFFSET = 1000000;

/** Club ID shown in the table (MemberClassId for NG; otherwise inventory ClubId). */
function clubMapVisibleClubId(row) {
  const id = Number(row.clubId);
  if ((row.fusionPlatform || '') === 'NG' && Number.isFinite(id) && id >= NG_MEMBER_CLASS_CLUB_ID_OFFSET) {
    return id - NG_MEMBER_CLASS_CLUB_ID_OFFSET;
  }
  return row.clubId;
}

/**
 * Club-level observability bucket for overview charts (US 240388):
 * - observed: every listed product has maturity >= 1
 * - partial: mix of maturity >= 1 and 0 / missing
 * - notObserved: no product has maturity >= 1 (or no products)
 */
function clubMapObservabilityBucket(row) {
  const map = clubMapProductStatusMap(row);
  const levels = CLUB_MAP_PRODUCTS
    .filter((name) => map.has(name))
    .map((name) => Number(map.get(name).maturityStatus) || 0);
  if (levels.length === 0) return 'notObserved';
  const anyObserved = levels.some((n) => n >= 1);
  const allObserved = levels.every((n) => n >= 1);
  if (allObserved) return 'observed';
  if (anyObserved) return 'partial';
  return 'notObserved';
}

/** Platform key for overview cards; null if the row is not a known family. */
function clubMapOverviewPlatformKey(row) {
  const platform = row.fusionPlatform || '';
  return CLUB_MAP_PLATFORMS.includes(platform) ? platform : null;
}

function emptyObservabilityCounts() {
  return { observed: 0, notObserved: 0, partial: 0 };
}

function renderClubMapOverview() {
  const root = document.getElementById('club-map-overview');
  const totalEl = document.getElementById('club-map-overview-total');
  const errEl = document.getElementById('club-map-overview-error');
  if (!root || !totalEl) return;

  const meta = state.clubInventoryMeta || {};
  const unavailable = Boolean(meta.warning && (!state.clubInventory || state.clubInventory.length === 0));
  root.classList.toggle('is-unavailable', unavailable);
  if (errEl) errEl.hidden = !unavailable;
  if (unavailable) {
    totalEl.textContent = '';
    return;
  }

  const rows = filteredClubInventory();
  const countsByPlatform = Object.fromEntries(
    CLUB_MAP_PLATFORMS.map((platform) => [platform, emptyObservabilityCounts()]),
  );
  rows.forEach((row) => {
    const platform = clubMapOverviewPlatformKey(row);
    if (!platform) return;
    const bucket = clubMapObservabilityBucket(row);
    countsByPlatform[platform][bucket] += 1;
  });

  totalEl.textContent = `${rows.length.toLocaleString()} clubs`;
  root.querySelectorAll('.club-map-overview__card').forEach((card) => {
    const platform = card.getAttribute('data-platform');
    const stats = countsByPlatform[platform] || emptyObservabilityCounts();
    const total = stats.observed + stats.notObserved + stats.partial;
    const observedPct = total > 0 ? Math.round((100 * stats.observed) / total) : 0;
    const valueEl = card.querySelector('[data-role="value"]');
    const metaEl = card.querySelector('[data-role="meta"]');
    const chartEl = card.querySelector('[data-role="chart"]');
    const partialLegend = card.querySelector('.club-map-chart__legend-partial');
    if (valueEl) valueEl.textContent = String(total);
    if (metaEl) {
      let text = `${stats.observed} observed / ${stats.notObserved} not observed (${observedPct}%)`;
      if (stats.partial > 0) text += ` · ${stats.partial} partial`;
      metaEl.textContent = text;
    }
    if (chartEl) {
      const observedWidth = total > 0 ? (100 * stats.observed) / total : 0;
      const notObservedWidth = total > 0 ? (100 * stats.notObserved) / total : 0;
      const partialWidth = total > 0 ? (100 * stats.partial) / total : 0;
      chartEl.innerHTML = `
        <span class="club-map-chart__seg club-map-chart__seg--observed" style="width:${observedWidth}%" title="Observed: ${stats.observed}"></span>
        <span class="club-map-chart__seg club-map-chart__seg--partial" style="width:${partialWidth}%" title="Partial: ${stats.partial}"></span>
        <span class="club-map-chart__seg club-map-chart__seg--not-observed" style="width:${notObservedWidth || (total === 0 ? 100 : 0)}%" title="Not observed: ${stats.notObserved}"></span>`;
    }
    if (partialLegend) partialLegend.hidden = !(stats.partial > 0);
    card.dataset.hasPartial = stats.partial > 0 ? 'true' : 'false';
  });
}
const CLUB_MAP_PRODUCT_SORT_KEYS = {
  productCars: 'Cars',
  productCruises: 'Cruises',
  productHotels: 'Hotels',
  productFlights: 'Flights',
};

/**
 * US 241135 — same catalog as journeys / KqlSourceResolver / TelemetrySourceDefaults.
 * Prefer loading production rows from GET /api/settings/telemetry-sources; fallback = defaults below.
 */
const CLUB_MAP_TELEMETRY_ENV = 'production';
/** Default Azure Portal directory (Hotels/Cars/Cruises / ICE). */
const CLUB_MAP_AI_PORTAL_TENANT = 'iceenterprise.com';
/**
 * Air App Insights lives in a different Entra tenant. Opening Air resources under
 * #@iceenterprise.com causes "You do not have authorization" on cross-resource queries.
 */
const CLUB_MAP_AIR_PORTAL_TENANT = 'carnivalvacationclub.onmicrosoft.com';
const CLUB_MAP_PORTAL_TENANT_BY_SOURCE = {
  'air-ui': CLUB_MAP_AIR_PORTAL_TENANT,
  'air-api': CLUB_MAP_AIR_PORTAL_TENANT,
};

function clubMapPortalTenantForSource(sourceAlias) {
  if (!sourceAlias) return CLUB_MAP_AI_PORTAL_TENANT;
  return CLUB_MAP_PORTAL_TENANT_BY_SOURCE[String(sourceAlias).toLowerCase()] || CLUB_MAP_AI_PORTAL_TENANT;
}

function clubMapPortalTenantForProduct(productName) {
  const sourceAlias = CLUB_MAP_PRODUCT_WORKBOOK_COMPONENT[productName]
    || (CLUB_MAP_PRODUCT_SOURCES[productName] || [])[0];
  return clubMapPortalTenantForSource(sourceAlias);
}

/** Fallback ARM ids (mirror TelemetrySourceDefaults production). Overridden by API catalog when loaded. */
const CLUB_MAP_TELEMETRY_DEFAULTS = {
  'sor-frontend': '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/sor-appinsights-rg-usw/providers/microsoft.insights/components/sor-frontend-ai-prod-usw',
  club: '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/ovs-club-dev/providers/microsoft.insights/components/appInsights-ovs-club',
  'cruise-core': '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-prod',
  'cruise-api': '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-web-api-ai-prod',
  'air-ui': '/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-Prod-USC/providers/microsoft.insights/components/appInsights-Air-UI-Prod-USC',
  'air-api': '/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-Prod-USC/providers/microsoft.insights/components/appInsights-Air-API-Prod-USC',
};

/** ClubInventory.TelemetryLocation component name → catalog alias ({{SOURCE:*}}). */
const CLUB_MAP_COMPONENT_TO_SOURCE = {
  'sor-frontend-ai-prod-usw': 'sor-frontend',
  'appInsights-ovs-club': 'club',
  'cruise-core-ovs-ai-prod': 'cruise-core',
  'cruise-web-api-ai-prod': 'cruise-api',
  'appInsights-Air-UI-Prod-USC': 'air-ui',
  'appInsights-Air-API-Prod-USC': 'air-api',
};

/** Map product → preferred catalog aliases (same verticals journeys use). */
const CLUB_MAP_PRODUCT_SOURCES = {
  Cars: ['sor-frontend', 'club'],
  Hotels: ['sor-frontend', 'club'],
  Cruises: ['cruise-core', 'cruise-api'],
  Flights: ['air-ui', 'air-api'],
};

/**
 * Per-product workbooks (ClubId / PartnerId params).
 * Flights workbook is hosted on ICE (write access) but KQL targets Air UI (cross-resource).
 */
const CLUB_MAP_HOTELS_WORKBOOK_ID =
  '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/sor-appinsights-rg-usw/providers/Microsoft.Insights/workbooks/87e5efd7-2760-4487-96af-fe9b51077736';
const CLUB_MAP_CARS_WORKBOOK_ID =
  '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/sor-appinsights-rg-usw/providers/Microsoft.Insights/workbooks/b2c4e8f0-1a3d-4c5e-9f67-88e5d4c3b2a1';
const CLUB_MAP_CRUISES_WORKBOOK_ID =
  '/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/Microsoft.Insights/workbooks/c7d8e9f0-2a3b-4c5d-9e6f-77a8b9c0d1e2';
/**
 * Flights parked (Air other tenant / workbook write). Demo focus: Hotels, Cars, Cruises.
 */
const CLUB_MAP_PRODUCT_WORKBOOKS = {
  Hotels: CLUB_MAP_HOTELS_WORKBOOK_ID,
  Cars: CLUB_MAP_CARS_WORKBOOK_ID,
  Cruises: CLUB_MAP_CRUISES_WORKBOOK_ID,
  Flights: '',
};

/** Workbook blade ComponentId (owner AI) per Map product. */
const CLUB_MAP_PRODUCT_WORKBOOK_COMPONENT = {
  Hotels: 'sor-frontend',
  Cars: 'sor-frontend',
  Cruises: 'cruise-core',
  Flights: 'air-ui',
};

function clubMapTelemetryCatalog() {
  return state.clubMapTelemetrySources || CLUB_MAP_TELEMETRY_DEFAULTS;
}

function clubMapResolveSourceResourceId(sourceAlias) {
  if (!sourceAlias) return null;
  const key = String(sourceAlias).trim().toLowerCase();
  const catalog = clubMapTelemetryCatalog();
  for (const [alias, resourceId] of Object.entries(catalog)) {
    if (alias.toLowerCase() !== key) continue;
    // null = explicitly disabled in Settings (do not fall back to production defaults).
    return resourceId || null;
  }
  return CLUB_MAP_TELEMETRY_DEFAULTS[key] || null;
}

/** TelemetryLocation token → catalog alias, or null. */
function clubMapComponentToSourceAlias(componentName) {
  const raw = String(componentName || '').trim();
  if (!raw) return null;
  if (CLUB_MAP_COMPONENT_TO_SOURCE[raw]) return CLUB_MAP_COMPONENT_TO_SOURCE[raw];
  const lower = raw.toLowerCase();
  const hit = Object.keys(CLUB_MAP_COMPONENT_TO_SOURCE).find((k) => k.toLowerCase() === lower);
  if (hit) return CLUB_MAP_COMPONENT_TO_SOURCE[hit];
  // Already a catalog alias?
  if (clubMapResolveSourceResourceId(raw)) return raw.toLowerCase();
  return null;
}

async function loadClubMapTelemetrySources() {
  try {
    const response = await fetch(withActiveCompany('/api/settings/telemetry-sources'));
    if (!response.ok) return;
    const body = await response.json();
    const sources = Array.isArray(body.sources) ? body.sources : [];
    const map = { ...CLUB_MAP_TELEMETRY_DEFAULTS };
    sources.forEach((s) => {
      if (!s?.alias) return;
      if (String(s.environment || '').toLowerCase() !== CLUB_MAP_TELEMETRY_ENV) return;
      const alias = String(s.alias).trim();
      // Match KqlSourceResolver: disabled sources must not resolve to production defaults.
      if (s.enabled === false) {
        map[alias] = null;
        return;
      }
      if (!s.resourceId) return;
      map[alias] = s.resourceId;
    });
    state.clubMapTelemetrySources = map;
  } catch (err) {
    console.warn('Club Map: telemetry-sources catalog unavailable; using TelemetrySourceDefaults mirror.', err);
  }
}

/**
 * Azure Portal deep-link that opens a saved workbook with NotebookParams
 * (plain ?ClubId= on /resource does NOT bind workbook parameters).
 * Format: UsageNotebookBlade + ComponentId + ConfigurationId + NotebookParams.
 */
function clubMapWorkbookDeepLink(workbookResourceId, row, productName) {
  if (!workbookResourceId) return null;
  const clubId = String(clubMapVisibleClubId(row) ?? '').trim();
  const icePartner = row?.icePartnerId == null ? '' : String(row.icePartnerId).trim();
  // Cruises events are keyed by PartnerId only. Hotels and Cars are keyed by ClubId.
  const partnerRaw = productName === 'Cruises' ? icePartner : '';
  if (productName === 'Cruises') {
    if (!partnerRaw) return null;
  } else if (!clubId) {
    return null;
  }
  // Keep NotebookParams minimal — extra keys (e.g. RowLimit) can break UsageNotebookBlade open.
  // Opens at 24 hours. Time range and table rows stay editable in the workbook.
  const notebookParams = {
    ClubId: clubId,
    TimeRange: { durationMs: 86400000 },
  };
  if (productName === 'Cruises') notebookParams.PartnerId = partnerRaw;
  const sourceAlias = CLUB_MAP_PRODUCT_WORKBOOK_COMPONENT[productName] || 'sor-frontend';
  const componentId = clubMapResolveSourceResourceId(sourceAlias);
  if (!componentId) return null;
  const portalTenant = clubMapPortalTenantForProduct(productName);
  const enc = encodeURIComponent;
  return `https://portal.azure.com/#@${portalTenant}/blade/AppInsightsExtension/UsageNotebookBlade`
    + `/ComponentId/${enc(componentId)}`
    + `/ConfigurationId/${enc(workbookResourceId)}`
    + `/Type/workbook`
    + `/NotebookParams/${enc(JSON.stringify(notebookParams))}`;
}

function clubMapTelemetryAliases(row) {
  return String(row?.telemetryLocation || '')
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean);
}

function clubMapAppInsightsResourceUrl(resourceId, sourceAlias) {
  if (!resourceId) return null;
  const portalTenant = clubMapPortalTenantForSource(sourceAlias);
  return `https://portal.azure.com/#@${portalTenant}/resource${resourceId}`;
}

/**
 * Resolve TelemetryLocation component name or catalog alias → App Insights Logs blade.
 * Telemetry column must NOT open product workbooks — free-form query for operators.
 */
function clubMapAppInsightsPortalUrl(componentOrAlias) {
  const sourceAlias = clubMapComponentToSourceAlias(componentOrAlias) || componentOrAlias;
  const resourceId = clubMapResolveSourceResourceId(sourceAlias);
  if (!resourceId) return null;
  return clubMapAppInsightsLogsBladeUrl(resourceId, sourceAlias)
    || clubMapAppInsightsResourceUrl(resourceId, sourceAlias);
}

/**
 * Short starter KQL for Logs blade (no blank lines). Filters by ClubId and optional IcePartnerId.
 * POC for US 241135 phase-2 — not a full journey workbook.
 */
function clubMapProductFilterQuery(row, productName) {
  const clubId = String(row?.clubId ?? '').replace(/"/g, '');
  if (!clubId) return null;
  const partnerRaw = row?.icePartnerId == null ? '' : String(row.icePartnerId).trim();
  const partner = partnerRaw.replace(/"/g, '');
  const partnerClause = partner
    ? ` or tostring(customDimensions.PartnerId) == "${partner}"`
    : '';
  const productHint = productName ? ` // Map product: ${productName}` : '';
  return [
    `union isfuzzy=true customEvents, traces, pageViews, requests${productHint}`,
    `| where timestamp > ago(24h)`,
    `| where tostring(customDimensions.ClubId) == "${clubId}"${partnerClause}`,
    `| take 100`,
  ].join('\n');
}

/** App Insights Logs blade (empty editor) for a resource. */
function clubMapAppInsightsLogsBladeUrl(resourceId, sourceAlias) {
  if (!resourceId) return null;
  const portalTenant = clubMapPortalTenantForSource(sourceAlias);
  return `https://portal.azure.com/#@${portalTenant}/resource${resourceId}/logs`;
}

/** Azure "Copy link to query" expects GZip + Base64 (plain btoa does not preload the editor). */
async function clubMapGzipBase64Query(query) {
  if (!query || typeof CompressionStream === 'undefined') return null;
  try {
    const bytes = new TextEncoder().encode(query);
    const stream = new Blob([bytes]).stream().pipeThrough(new CompressionStream('gzip'));
    const compressed = new Uint8Array(await new Response(stream).arrayBuffer());
    let binary = '';
    compressed.forEach((b) => { binary += String.fromCharCode(b); });
    return btoa(binary);
  } catch {
    return null;
  }
}

async function clubMapAppInsightsLogsShareUrl(resourceId, query, sourceAlias) {
  if (!resourceId || !query) return null;
  const gzipB64 = await clubMapGzipBase64Query(query);
  if (!gzipB64) return null;
  const portalTenant = clubMapPortalTenantForSource(sourceAlias);
  return `https://portal.azure.com/#@${portalTenant}/blade/Microsoft_OperationsManagementSuite_Workspace/Logs.ReactView/resourceId/${encodeURIComponent(resourceId)}/source/LogsBlade.AnalyticsShareLinkToQuery/q/${gzipB64}`;
}

/**
 * Resolve product → link when the club has that product.
 * Prefer workbook/dashboard with ClubId (Cristiano 241135); else Logs+ClubId; else AI overview.
 */
function clubMapProductAppInsightsUrl(row, productName) {
  // Only link when the Map shows this product (badge), never for "—".
  if (!clubMapProductStatusMap(row).has(productName)) return null;
  // Flights stays on the map. The badge does not open Air until that US lands.
  if (productName === 'Flights') return null;
  // Cruises workbook needs PartnerId; without it panels return no events — leave unlinked.
  if (productName === 'Cruises') {
    const partner = row?.icePartnerId == null ? '' : String(row.icePartnerId).trim();
    if (!partner) return null;
  }

  const workbookId = CLUB_MAP_PRODUCT_WORKBOOKS[productName] || '';
  const workbookUrl = clubMapWorkbookDeepLink(workbookId, row, productName);
  if (workbookUrl) {
    return { url: workbookUrl, alias: productName, mode: 'workbook-clubid', resourceId: workbookId };
  }

  const preferred = CLUB_MAP_PRODUCT_SOURCES[productName] || [];
  if (preferred.length === 0) return null;

  const onRow = new Set(
    clubMapTelemetryAliases(row)
      .map((c) => clubMapComponentToSourceAlias(c))
      .filter(Boolean)
      .map((a) => a.toLowerCase()),
  );
  const sourceOrder = [
    ...preferred.filter((a) => onRow.has(a.toLowerCase())),
    ...preferred.filter((a) => !onRow.has(a.toLowerCase())),
  ];

  for (const sourceAlias of sourceOrder) {
    const resourceId = clubMapResolveSourceResourceId(sourceAlias);
    if (!resourceId) continue;
    const query = clubMapProductFilterQuery(row, productName);
    const logsBlade = clubMapAppInsightsLogsBladeUrl(resourceId, sourceAlias);
    if (query && logsBlade) {
      return { url: logsBlade, alias: sourceAlias, mode: 'logs-club-filter', resourceId, query };
    }
    const overview = clubMapAppInsightsResourceUrl(resourceId, sourceAlias);
    if (overview) return { url: overview, alias: sourceAlias, mode: 'resource', resourceId };
  }
  return null;
}

async function clubMapOpenAppInsightsLink(link) {
  if (!link) return;
  if (link.mode === 'logs-club-filter' && link.resourceId && link.query) {
    try {
      await navigator.clipboard.writeText(link.query);
    } catch {
      /* clipboard may be blocked */
    }
    const shareUrl = await clubMapAppInsightsLogsShareUrl(link.resourceId, link.query, link.alias);
    window.open(shareUrl || link.url, '_blank', 'noopener,noreferrer');
    return;
  }
  if (link.url) window.open(link.url, '_blank', 'noopener,noreferrer');
}

function wireClubMapAppInsightsLinks() {
  if (state.clubMapAppInsightsWired) return;
  state.clubMapAppInsightsWired = true;
  const tbody = document.getElementById('club-map-tbody');
  tbody?.addEventListener('click', (ev) => {
    const anchor = ev.target.closest('a.club-map-product-link[data-ai-mode]');
    if (!anchor) return;
    const mode = anchor.getAttribute('data-ai-mode');
    if (mode !== 'logs-club-filter') return;
    ev.preventDefault();
    const resourceId = anchor.getAttribute('data-ai-resource-id') || '';
    let query = '';
    try {
      query = decodeURIComponent(anchor.getAttribute('data-ai-query') || '');
    } catch {
      query = anchor.getAttribute('data-ai-query') || '';
    }
    const alias = anchor.getAttribute('data-ai-alias') || '';
    const url = anchor.getAttribute('href') || '';
    clubMapOpenAppInsightsLink({ mode, resourceId, query, alias, url });
  });
}

/**
 * When false: product badges ignore seeded maturity and always show 0 (Not Observed).
 * Flip to true when observability maturity is live.
 */
const CLUB_MAP_USE_LIVE_MATURITY = true;

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
    const raw = Number.isFinite(p.maturityStatus) ? p.maturityStatus : 0;
    const level = CLUB_MAP_USE_LIVE_MATURITY ? raw : 0;
    map.set(name, {
      maturityStatus: level,
      maturityStatusName: CLUB_MAP_USE_LIVE_MATURITY
        ? (p.maturityStatusName || 'Not Observed')
        : 'Not Observed',
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
    const platform = row.fusionPlatform || 'Unknown';
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
      const hay = [clubMapVisibleClubId(row), row.clubId, row.clubName, row.site, row.brandName, row.clientId, row.telemetryLocation, row.fusionPlatform]
        .filter((v) => v != null && v !== '')
        .join(' ')
        .toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });
}

/** Platform badge for an Application Map row (Fusion / Hybrid / SOR / NG). */
function clubMapPlatformBadge(platform) {
  const value = platform || 'Unknown';
  const known = CLUB_MAP_PLATFORMS.includes(value);
  const cls = known ? 'status-badge status-badge--healthy' : 'status-badge status-badge--unknown';
  return `<span class="${cls}">${escapeHtml(value)}</span>`;
}

/** One fixed product column: status circle, or — if club has no that product.
 *  US 241135: Hotels, Cars, and Cruises open the club workbook. Flights stays visible with no link. */
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
  const link = clubMapProductAppInsightsUrl(row, productName);
  const title = link
    ? (link.mode === 'workbook-clubid'
      ? `${productName}: ${name} · Open App Insights workbook (ClubId)`
      : link.mode === 'logs-club-filter'
        ? `${productName}: ${name} · Open App Insights Logs (ClubId filter)`
        : `${productName}: ${name} · Open App Insights (${link.alias})`)
    : `${productName}: ${name}`;
  const badge = `<span class="${cls}">${level}</span>`;
  let inner;
  if (link?.mode === 'logs-club-filter') {
    const qEnc = encodeURIComponent(link.query || '');
    inner = `<a class="club-map-product-link" href="${escapeHtml(link.url)}" target="_blank" rel="noopener noreferrer" title="${escapeHtml(title)}" data-ai-mode="logs-club-filter" data-ai-alias="${escapeHtml(link.alias || '')}" data-ai-resource-id="${escapeHtml(link.resourceId || '')}" data-ai-query="${qEnc}">${badge}</a>`;
  } else if (link) {
    inner = `<a class="club-map-product-link" href="${escapeHtml(link.url)}" target="_blank" rel="noopener noreferrer" title="${escapeHtml(title)}">${badge}</a>`;
  } else {
    inner = `<span title="${escapeHtml(title)}">${badge}</span>`;
  }
  return `<td class="club-map-cell club-map-cell--product">${inner}</td>`;
}

function textCell(value, className = '') {
  const text = value == null || value === '' ? '—' : String(value);
  const muted = text === '—' ? ' club-map-cell--muted' : '';
  return `<td class="club-map-cell club-map-cell--text${muted} ${className}">${escapeHtml(text)}</td>`;
}

/** Telemetry: one resource alias per line. Opens that App Insights Logs blade, not the product workbook. */
function telemetryCell(value) {
  const parts = String(value || '')
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean);
  if (parts.length === 0) {
    return '<td class="club-map-cell club-map-cell--muted">—</td>';
  }
  const lines = parts.map((p) => {
    const url = clubMapAppInsightsPortalUrl(p);
    if (!url) {
      return `<div class="club-map-telemetry-line">${escapeHtml(p)}</div>`;
    }
    return `<div class="club-map-telemetry-line"><a class="club-map-telemetry-link" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer" title="Open App Insights Logs (free query)">${escapeHtml(p)}</a></div>`;
  }).join('');
  return `<td class="club-map-cell club-map-cell--telemetry">${lines}</td>`;
}

function resetClubMapPage() {
  state.clubMapPagination.page = 1;
}

function clubMapPageCount(totalRows, pageSize) {
  return Math.max(1, Math.ceil(totalRows / pageSize) || 1);
}

function wireClubMapPagination() {
  if (state.clubMapPaginationWired) return;
  state.clubMapPaginationWired = true;
  const prev = document.getElementById('club-map-page-prev');
  const next = document.getElementById('club-map-page-next');
  const size = document.getElementById('club-map-page-size');
  prev?.addEventListener('click', () => {
    if (state.clubMapPagination.page <= 1) return;
    state.clubMapPagination.page -= 1;
    renderClubMapTable();
  });
  next?.addEventListener('click', () => {
    state.clubMapPagination.page += 1;
    renderClubMapTable();
  });
  size?.addEventListener('change', () => {
    const nextSize = Number(size.value) || 50;
    state.clubMapPagination.pageSize = nextSize;
    resetClubMapPage();
    renderClubMapTable();
  });
}

function updateClubMapPaginationControls(totalRows) {
  const pager = document.getElementById('club-map-pagination');
  const label = document.getElementById('club-map-page-label');
  const prev = document.getElementById('club-map-page-prev');
  const next = document.getElementById('club-map-page-next');
  const size = document.getElementById('club-map-page-size');
  if (!pager) return;

  const pageSize = state.clubMapPagination.pageSize || 50;
  const pageCount = clubMapPageCount(totalRows, pageSize);
  if (state.clubMapPagination.page > pageCount) state.clubMapPagination.page = pageCount;
  if (state.clubMapPagination.page < 1) state.clubMapPagination.page = 1;
  const page = state.clubMapPagination.page;

  pager.hidden = totalRows === 0;
  if (size && String(size.value) !== String(pageSize)) size.value = String(pageSize);
  if (label) label.textContent = `Page ${page} of ${pageCount}`;
  if (prev) prev.disabled = page <= 1;
  if (next) next.disabled = page >= pageCount;
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
    if (key === 'clubId') {
      const visibleClubId = clubMapVisibleClubId(row);
      return typeof visibleClubId === 'number' ? visibleClubId : Number(visibleClubId) || 0;
    }
    const v = row[key];
    if (typeof v === 'number') return v;
    return (v == null ? '' : String(v)).toLowerCase();
  });

  const pageSize = state.clubMapPagination.pageSize || 50;
  const pageCount = clubMapPageCount(sorted.length, pageSize);
  if (state.clubMapPagination.page > pageCount) state.clubMapPagination.page = pageCount;
  const page = state.clubMapPagination.page;
  const start = sorted.length === 0 ? 0 : (page - 1) * pageSize;
  const end = Math.min(start + pageSize, sorted.length);
  const pageRows = sorted.slice(start, end);

  if (note) {
    const meta = state.clubInventoryMeta;
    if (meta.warning) {
      note.textContent = meta.warning;
    } else if (sorted.length === 0) {
      note.textContent = `Showing 0 of ${meta.rowCount || state.clubInventory.length} clubs` +
        (meta.source ? ` · source: ${meta.source}` : '') + '.';
    } else {
      note.textContent = `Showing ${start + 1}–${end} of ${sorted.length} matching` +
        ` (${meta.rowCount || state.clubInventory.length} total)` +
        (meta.source ? ` · source: ${meta.source}` : '') + '.';
    }
  }

  updateClubMapPaginationControls(sorted.length);

  if (sorted.length === 0) {
    tbody.innerHTML = `<tr><td colspan="11"><div class="state-block" role="status">${
      state.clubInventory.length === 0
        ? 'No inventory loaded.'
        : 'No clubs match the current filters.'
    }</div></td></tr>`;
    return;
  }

  tbody.innerHTML = pageRows.map((row) => `
    <tr>
      <td class="club-map-cell club-map-cell--num">${escapeHtml(row.clientId == null ? '—' : row.clientId)}</td>
      ${textCell(row.brandName, 'club-map-cell--brand')}
      <td class="club-map-cell club-map-cell--num">${escapeHtml(clubMapVisibleClubId(row))}</td>
      ${textCell(row.clubName, 'club-map-cell--name')}
      ${textCell(row.site, 'club-map-cell--site')}
      <td class="club-map-cell">${clubMapPlatformBadge(row.fusionPlatform)}</td>
      ${telemetryCell(row.telemetryLocation)}
      ${productStatusCell(row, 'Cars')}
      ${productStatusCell(row, 'Cruises')}
      ${productStatusCell(row, 'Hotels')}
      ${productStatusCell(row, 'Flights')}
    </tr>`).join('');
}

async function loadClubInventory({ force = false } = {}) {
  if (state.clubInventoryLoaded && !force) return;
  const tbody = document.getElementById('club-map-tbody');
  if (tbody) tbody.innerHTML = loadingTableRow(11);
  try {
    let payload;
    try {
      payload = await fetchJsonPayload(withActiveCompany('/api/club-inventory'));
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
  renderClubMapOverview();
}

async function ensureClubMapTab() {
  wireClubMapFilters();
  wireClubMapPagination();
  wireClubMapAppInsightsLinks();
  await loadClubMapTelemetrySources();
  await loadClubInventory();
}

// Demo stack only (Demo:Enabled): a permanent banner above the header saying the data is synthetic.
// When demo mode is off - or the endpoint is missing/unreachable - nothing is added to the DOM.
async function loadDemoBanner() {
  let info;
  try {
    const response = await fetch('/api/demo-mode', { headers: { Accept: 'application/json' } });
    if (!response.ok) return;
    info = await response.json();
  } catch (err) {
    return;
  }
  if (!info || info.enabled !== true || document.querySelector('.demo-banner')) return;

  const banner = document.createElement('div');
  banner.className = 'demo-banner';
  banner.setAttribute('role', 'status');
  banner.textContent = typeof info.label === 'string' && info.label.trim() ? info.label : 'DEMO — synthetic data';
  const header = document.querySelector('.app-header');
  if (header && header.parentNode) {
    header.parentNode.insertBefore(banner, header);
  } else {
    document.body.insertBefore(banner, document.body.firstChild);
  }
  document.body.classList.add('has-demo-banner');
}

async function init() {
  loadDemoBanner();
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
  wireOnboardingDraft();
  wireKpiSettings();
  wireAccessTokenSettings();
  openSettingsSectionFromUrl();
  wireJourneysSort();
  wireSortableTable('exceptions-table', 'exceptions', state.exceptions, renderExceptionsTable);
  renderClubMapProductHeaders();
  wireSortableTable('club-map-table', 'clubMap', state.clubInventory, () => {
    resetClubMapPage();
    renderClubMapTable();
  });

  // Before the first load so the header never shows a company the server has not confirmed, and
  // so anything scoped by withActiveCompany() already knows the key.
  await loadCompanySelector();
  // Theme, filters and column titles come from the active profile before anything is drawn.
  await loadActiveProfile();
  loadClubsForDomain(state.filters.domain);

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
