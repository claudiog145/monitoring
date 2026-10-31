/**
 * Maps T-Mobile Hotel, Cars and Cruise KQL rows → portal journey objects.
 * Swap the fetch in dataProvider later; keep this mapper.
 */
function mapOutcomeToHealth(outcome) {
  const o = String(outcome || '').toLowerCase();
  if (o === 'success' || o.startsWith('booked')) return 'healthy';
  if (o === 'failed' || o.includes('failed') || o.includes('purchase attempted')) return 'critical';
  if (o === 'degraded' || o === 'in_progress' || o.startsWith('dropped') || o.includes('cancellation')) return 'degraded';
  return 'unknown';
}

const CRUISE_FAILED_STEP_LABELS = {
  1: 'T-Mobile SSO Failed',
  2: 'Cruise Listing Failed',
  3: 'Cruise Detail Failed',
  4: 'Rooms & Rates Failed',
  5: 'Review Total Price Failed',
  6: 'Cabin Selection Failed',
  7: 'Passenger Failed',
  8: 'Options Failed',
  9: 'Payment Failed',
  10: 'Review Failed',
  11: 'Confirmation Failed',
};

/** Cruise Customer Journey Status: "{stage} Failed"; leave Purchase Failed as-is. */
function cruiseStatusLabel(outcome) {
  const o = String(outcome || '');
  if (/^purchase failed/i.test(o)) return o;
  const step = o.match(/^failed at step\s+(\d+)$/i);
  if (step) return CRUISE_FAILED_STEP_LABELS[Number(step[1])] || o;
  const named = o.match(/^failed at (.+)$/i);
  if (named) return `${named[1]} Failed`;
  return o;
}

function firstReservationId(booked) {
  if (Array.isArray(booked) && booked.length) return String(booked[0]);
  if (booked && typeof booked === 'object') {
    const vals = Object.values(booked).filter((v) => v !== null && v !== undefined && v !== '');
    return vals.length ? String(vals[0]) : '';
  }
  return '';
}

function asCallList(calls) {
  if (Array.isArray(calls)) return calls;
  if (calls && typeof calls === 'object') return Object.values(calls);
  return [];
}

function asErrorList(errors) {
  if (Array.isArray(errors)) return errors;
  if (errors && typeof errors === 'object') return Object.values(errors);
  return [];
}

function asSessionList(sessions) {
  if (Array.isArray(sessions)) return sessions;
  if (typeof sessions === 'string') {
    try {
      const parsed = JSON.parse(sessions);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }
  if (sessions && typeof sessions === 'object') return Object.values(sessions);
  return [];
}

function v6StageUiStatus(stage) {
  const calls = asCallList(stage.calls);
  const hasResultFail = calls.some((c) => c.kind === 'result' && Number(c.failureRatePct) > 0);
  if (hasResultFail) return 'failed';
  const s = String(stage.status || '').toUpperCase();
  if (s === 'RED') return 'failed';
  if (s === 'YELLOW') return 'degraded';
  if (s === 'GREEN' || s === 'N/A') return 'success';
  return 'success';
}

function primaryComponent(stage) {
  const calls = asCallList(stage.calls);
  const api = calls.find((c) => c.kind === 'api');
  if (api) return api.call;
  const result = calls.find((c) => c.kind === 'result');
  if (result) return result.call;
  return calls[0] ? calls[0].call : '—';
}

function stageDiagnostic(stage, row) {
  if (Number(stage.stageNo) === 7 && row.FailReason) return row.FailReason;
  const calls = asCallList(stage.calls);
  const withErr = calls.find((c) => asErrorList(c.errors).length);
  if (withErr) {
    const err = asErrorList(withErr.errors)[0];
    return err.message || String(err);
  }
  if (stage.p95Ms != null) return `P95 ${stage.p95Ms} ms · ${stage.status || ''}`;
  return 'No TMO API duration in this stage.';
}

function resolveClubLabel(row, clubLabel, clubLabels) {
  const id = row && row.ClubId != null && row.ClubId !== '' ? String(row.ClubId) : '';
  if (id && clubLabels && clubLabels[id]) return clubLabels[id];
  return clubLabel;
}

function mapV6RowToJourney(row, clubLabel = 'T-Mobile / T Life', domain = 'hotels', environment = 'production', clubLabels) {
  const stagesIn = Array.isArray(row.Journey) ? row.Journey : Object.values(row.Journey || {});
  let offset = 0;
  const stages = stagesIn.map((st) => {
    const durationMs = st.stageTotalMs == null ? 0 : Number(st.stageTotalMs);
    const mapped = {
      name: st.stage,
      component: primaryComponent(st),
      startOffsetMs: offset,
      durationMs,
      status: v6StageUiStatus(st),
      downstream: primaryComponent(st),
      requestCount: st.callCount == null ? asCallList(st.calls).length : Number(st.callCount),
      error: v6StageUiStatus(st) === 'failed',
      diagnostic: stageDiagnostic(st, row),
      p95Ms: st.p95Ms,
      failureRatePct: st.failureRatePct,
      v6Status: st.status,
      calls: asCallList(st.calls).map((c) => ({
        kind: c.kind,
        call: c.call,
        correlationId: c.correlationId || (domain === 'cars' ? String(row.CorrelationId || '') : ''),
        t: c.t || '',
        n: c.n,
        callTotalMs: c.callTotalMs,
        p50Ms: c.p50Ms,
        p95Ms: c.p95Ms,
        maxMs: c.maxMs,
        failureRatePct: c.failureRatePct,
        status: c.status,
        detail: domain === 'cruises' ? (c.source || c.detail || '') : (c.detail || ''),
        errors: asErrorList(c.errors),
      })),
    };
    offset += durationMs;
    return mapped;
  });

  const bookingId = firstReservationId(row.BookedReservations);
  const domainName = domain === 'cars' ? 'Cars'
    : domain === 'cruises' ? 'Cruise'
    : 'Hotel';
  const resolvedClub = resolveClubLabel(row, clubLabel, clubLabels);
  return {
    source: 'v6',
    id: String((domain === 'cruises' || domain === 'cars') ? (row.CorrelationId || row.SessionId || row.UserId) : row.UserId),
    correlationId: String(row.CorrelationId || row.UserId),
    domain,
    customer: String(row.UserId),
    club: resolvedClub,
    origin: `${resolvedClub} ${domainName}`,
    destination: environment === 'staging' ? 'STAGING' : 'PROD',
    startDate: row.FirstSeen,
    adults: null,
    children: null,
    status: domain === 'cruises' ? cruiseStatusLabel(row.Outcome) : row.Outcome,
    outcome: domain === 'cruises' ? cruiseStatusLabel(row.Outcome) : row.Outcome,
    totalDurationMs: row.JourneyTotalMs == null ? 0 : Number(row.JourneyTotalMs),
    bookingId: bookingId || '',
    timestamp: row.LastSeen,
    region: '',
    environment,
    failReason: row.FailReason || '',
    lastStep: row.LastStep,
    sessionCount: Number(row.SessionCount) || 0,
    sessions: (() => {
      const listed = asSessionList(row.Sessions);
      if (listed.length) return listed;
      if (row.CorrelationId) {
        return [{ session: 1, correlationId: String(row.CorrelationId), changeReason: 'Latest session' }];
      }
      return [];
    })(),
    callsNoCorrelation: Number(row.CallsNoCorrelation) || 0,
    callsViaCorrelation: Number(row.CallsViaCorrelation) || 0,
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

function mapV6RowsToJourneys(rows, clubLabel, domain = 'hotels', environment = 'production', clubLabels) {
  return (Array.isArray(rows) ? rows : []).map((row) => mapV6RowToJourney(row, clubLabel, domain, environment, clubLabels));
}

if (typeof module !== 'undefined') {
  module.exports = { mapV6RowToJourney, mapV6RowsToJourneys, cruiseStatusLabel };
}
