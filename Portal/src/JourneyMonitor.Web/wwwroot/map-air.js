/**
 * Maps Air journey KQL rows → portal journey objects.
 * Hotel mapping stays in map-v6.js.
 */
function asCallList(calls) {
  if (Array.isArray(calls)) return calls;
  if (calls && typeof calls === 'object') return Object.values(calls);
  return [];
}

/**
 * Browser telemetry mixes our own calls with analytics beacons. The beacons are
 * excluded by the portal KQL. This fallback classification also prevents an
 * unfiltered fixture or future query change from blaming a third-party call.
 */
const THIRD_PARTY = new RegExp(
  [
    'google-analytics', 'analytics\\.google\\.com', 'googletagmanager', 'doubleclick', 'gstatic', 'adservice',
    'niceincontact', 'tealium', 'datadoghq', 'loggly', 'clarity\\.ms', 'kampyle',
    'hotjar', 'newrelic', 'facebook', 'segment\\.io', 'optimizely',
  ].join('|'),
  'i'
);
const KIND_RANK = { request: 0, event: 1, page: 2, dependency: 3 };

function callName(c) {
  return String((c && (c.name || c.call)) || '');
}

/** Air call names carry the whole query string and can reach 1 KB. */
function shortLabel(value, max = 80) {
  const text = String(value || '').split('?')[0].trim();
  if (text.length <= max) return text;
  return `${text.slice(0, max - 1)}…`;
}

function callRank(c) {
  const kind = KIND_RANK[c && c.kind] == null ? 4 : KIND_RANK[c.kind];
  return (THIRD_PARTY.test(callName(c)) ? 10 : 0) + kind;
}

function primaryAirCall(calls) {
  const list = asCallList(calls).filter((c) => callName(c));
  if (!list.length) return null;
  return list
    .slice()
    .sort((a, b) => callRank(a) - callRank(b) || callName(a).length - callName(b).length)[0];
}

function latencyBand(p95Ms) {
  if (p95Ms == null) return 'N/A';
  if (Number(p95Ms) < 2000) return 'GREEN';
  if (Number(p95Ms) < 5000) return 'YELLOW';
  return 'RED';
}

function splitRoute(route) {
  const text = String(route || '').trim();
  if (!text || text === '-') return { origin: 'Air', destination: 'PROD' };
  const parts = text.split(/\s*->\s*/);
  if (parts.length >= 2) return { origin: parts[0].trim(), destination: parts[1].trim() };
  return { origin: text, destination: 'PROD' };
}

/**
 * A failed analytics beacon is not a broken booking step, so it degrades a stage
 * instead of failing it. Only our own calls turn a stage red.
 */
function airStageStatus(stage) {
  const calls = asCallList(stage.calls);
  const failed = calls.filter((c) => c.failed === true);
  if (failed.some((c) => !THIRD_PARTY.test(callName(c)))) return 'failed';
  if (failed.length) return 'degraded';
  if (calls.length === 0 && Number(stage.fails) > 0) return 'failed';
  return 'success';
}

function firstFailedCall(stages) {
  let beaconStage = null;
  for (const st of stages) {
    const failed = asCallList(st.calls).filter((c) => c.failed === true);
    const own = failed.find((c) => !THIRD_PARTY.test(callName(c)));
    if (own) {
      const bits = [st.step || st.name || '', shortLabel(callName(own), 70), own.status]
        .filter((v) => v !== undefined && v !== null && v !== '');
      return bits.join(' · ');
    }
    if (!beaconStage && failed.length) beaconStage = st;
    if (!beaconStage && !failed.length && Number(st.fails) > 0) beaconStage = st;
  }
  if (beaconStage) {
    return `${beaconStage.step || beaconStage.name} · only third-party analytics calls failed`;
  }
  return '';
}

function mapAirCall(c) {
  return {
    kind: c.kind,
    call: shortLabel(callName(c), 120),
    correlationId: c.correlationId || '',
    t: c.t || '',
    n: c.n,
    callTotalMs: c.durationMs,
    p50Ms: c.p50Ms,
    p95Ms: c.p95Ms,
    maxMs: c.maxMs,
    failureRatePct: c.failed ? 100 : 0,
    status: c.status,
    detail: shortLabel(c.target || c.path || '', 120),
    errors: c.failed ? [{ message: `${shortLabel(callName(c), 70) || 'call'} ${c.status || 'failed'}`.trim() }] : [],
  };
}

function mapAirRowToJourney(row, clubLabel = 'T-Mobile / T Life', environment = 'production') {
  const stagesIn = Array.isArray(row.Journey) ? row.Journey : Object.values(row.Journey || {});
  const stages = stagesIn.map((st) => {
    const status = airStageStatus(st);
    const primary = primaryAirCall(st.calls);
    const label = primary ? shortLabel(callName(primary)) : '—';
    const requestCount = st.callCount == null ? asCallList(st.calls).length : Number(st.callCount);
    return {
      name: st.step,
      component: label,
      startOffsetMs: st.offsetMs == null ? 0 : Number(st.offsetMs),
      durationMs: st.durationMs == null ? 0 : Number(st.durationMs),
      status,
      downstream: label,
      requestCount,
      error: status === 'failed',
      diagnostic: status === 'failed'
        ? firstFailedCall([st])
        : (st.p95Ms != null ? `P95 ${st.p95Ms} ms · ${latencyBand(st.p95Ms)}` : ''),
      p95Ms: st.p95Ms,
      failureRatePct: Number(st.fails) > 0 && requestCount > 0
        ? Math.round((Number(st.fails) / requestCount) * 10000) / 100
        : 0,
      v6Status: latencyBand(st.p95Ms),
      calls: asCallList(st.calls).map(mapAirCall),
    };
  });

  const route = splitRoute(row.Route);
  const outcome = String(row.Status || '').toUpperCase();
  const hasTrouble = outcome.includes('FAILED') || outcome === 'DEGRADED'
    || stages.some((s) => s.status === 'failed' || s.status === 'degraded');
  let failReason = hasTrouble ? firstFailedCall(stagesIn) : '';
  const hasOwnFailure = stagesIn.some((st) => asCallList(st.calls).some((c) => c.failed === true && !THIRD_PARTY.test(callName(c))));
  if (hasTrouble && Number(row.BookFail) > 0 && !hasOwnFailure) failReason = 'Book Error';

  return {
    source: 'air-v6',
    id: String(row.CorrelationId || row.UserId || ''),
    correlationId: String(row.CorrelationId || ''),
    domain: 'air',
    customer: String(row.UserId || ''),
    club: clubLabel,
    origin: route.origin,
    destination: route.destination,
    startDate: row.FirstSeen,
    adults: null,
    children: null,
    status: row.Status,
    outcome: row.Status,
    totalDurationMs: row.JourneyTotalMs == null ? 0 : Number(row.JourneyTotalMs),
    bookingId: row.BookingId && row.BookingId !== '-' ? String(row.BookingId) : '',
    timestamp: row.LastSeen,
    region: '',
    environment,
    failReason,
    lastStep: row.LastStep,
    sessionCount: 1,
    sessions: [],
    callsNoCorrelation: 0,
    callsViaCorrelation: 0,
    bookOk: Number(row.BookOk) || 0,
    bookFail: Number(row.BookFail) || 0,
    failedCalls: Number(row.FailedCalls) || 0,
    passedSearch: row.PassedSearch === true || String(row.PassedSearch).toLowerCase() === 'true',
    isBookSuccessful: row.IsBookSuccessful === true
      || String(row.IsBookSuccessful).toLowerCase() === 'true',
    exceptions: Number(row.Exceptions) || 0,
    totalCalls: stages.reduce((sum, stage) => sum + (Number(stage.requestCount) || 0), 0),
    overallP95Ms: row.OverallP95 == null ? null : Number(row.OverallP95),
    stages,
  };
}

function mapAirRowsToJourneys(rows, clubLabel, environment = 'production') {
  return (Array.isArray(rows) ? rows : []).map((row) => mapAirRowToJourney(row, clubLabel, environment));
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { mapAirRowToJourney, mapAirRowsToJourneys };
}
