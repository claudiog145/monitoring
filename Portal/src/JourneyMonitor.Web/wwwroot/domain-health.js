/**
 * Aggregates the journey rows returned by the vertical KQL into Domain Health.
 * Keeping this calculation separate makes the card definitions testable and
 * ensures every filter-driven journey reload feeds the same metrics.
 */
function journeyHealth(journey) {
  const outcome = String(journey && (journey.outcome || journey.status) || '').toLowerCase();
  if (outcome === 'success' || outcome.startsWith('booked')) return 'healthy';
  if (outcome === 'failed' || outcome.includes('failed') || outcome.includes('purchase attempted')) return 'critical';
  if (outcome === 'degraded' || outcome === 'in_progress' || outcome.startsWith('dropped') || outcome.includes('cancellation')) {
    return 'degraded';
  }
  return 'unknown';
}

function finiteNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

function journeyCallCount(journey) {
  if (Number.isFinite(Number(journey.totalCalls))) return Math.max(0, Number(journey.totalCalls));
  return (journey.stages || []).reduce((sum, stage) => sum + Math.max(0, finiteNumber(stage.requestCount)), 0);
}

function journeyFailedCalls(journey) {
  if (Number.isFinite(Number(journey.failedCalls))) return Math.max(0, Number(journey.failedCalls));
  return (journey.stages || []).reduce((sum, stage) => {
    const calls = Array.isArray(stage.calls) ? stage.calls : [];
    return sum + calls.filter((call) => call.error === true || finiteNumber(call.failureRatePct) > 0).length;
  }, 0);
}

function availabilityFor(journeys) {
  if (!journeys.length) return null;
  const totalCalls = journeys.reduce((sum, journey) => sum + journeyCallCount(journey), 0);
  if (totalCalls === 0) return null;
  const failedCalls = journeys.reduce((sum, journey) => sum + journeyFailedCalls(journey), 0);
  return Math.max(0, 100 - failedCalls / totalCalls * 100);
}

function domainStatus(availability, errorRate) {
  if (availability == null) return 'unknown';
  if (availability < 95 || errorRate >= 5) return 'critical';
  if (availability < 99.9 || errorRate > 0) return 'degraded';
  return 'healthy';
}

function domainTrend(journeys) {
  if (journeys.length < 4) return { direction: 'stable', label: 'stable' };
  const sorted = [...journeys].sort((a, b) => new Date(a.timestamp || 0) - new Date(b.timestamp || 0));
  const midpoint = Math.floor(sorted.length / 2);
  const older = availabilityFor(sorted.slice(0, midpoint));
  const recent = availabilityFor(sorted.slice(midpoint));
  if (older == null || recent == null) return { direction: 'unknown', label: 'insufficient data' };
  const delta = recent - older;
  if (delta > 0.5) return { direction: 'improving', label: 'improving' };
  if (delta < -0.5) return { direction: 'degrading', label: 'degrading' };
  return { direction: 'stable', label: 'stable' };
}

function calculateDomainHealth(journeys, domain) {
  const rows = (Array.isArray(journeys) ? journeys : []).filter((journey) => journey.domain === domain);
  if (!rows.length) {
    return {
      domain,
      status: 'unknown',
      availability: null,
      errorRate: null,
      latencyMs: null,
      incidents: null,
      trend: { direction: 'unknown', label: 'no KQL data' },
      sampleSize: 0,
    };
  }

  const availability = availabilityFor(rows);
  const errorRate = availability == null ? null : 100 - availability;
  const latencyMs = Math.round(
    rows.reduce((sum, journey) => sum + finiteNumber(journey.totalDurationMs), 0) / rows.length,
  );
  const incidents = rows.filter((journey) => (
    journeyFailedCalls(journey) > 0 || journeyHealth(journey) === 'critical'
  )).length;

  return {
    domain,
    status: domainStatus(availability, errorRate),
    availability,
    errorRate,
    latencyMs,
    incidents,
    trend: domainTrend(rows),
    sampleSize: rows.length,
  };
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    calculateDomainHealth,
    domainStatus,
    domainTrend,
    journeyCallCount,
    journeyFailedCalls,
    journeyHealth,
  };
}
