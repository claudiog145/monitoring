/**
 * Builds Domain Comparison rows from mapped KQL journeys.
 * Conversion is search-to-booking for every vertical:
 *   Cruise uses PassedSearch and IsBookSuccessful as projected.
 *   Hotel also infers search from stage 02 Hotel Search and booking from BookOk.
 *   Cars follows Hotel: PassedSearch from SearchAt, or stage 02 Car Search, with BookOk as the booking fallback.
 *   Air uses PassedSearch (StepsCompleted > 0) and infers search from
 *   stage 01 Flight Search, with BookOk as the booking fallback.
 * Cruise P95 is the average of OverallP95 (existing Cruise contract).
 * Hotel, Air and Cars P95 are the 95th percentile of those latencies in the sample.
 */
function hotelReachedSearch(journey) {
  if (journey.passedSearch === true) return true;
  return (Array.isArray(journey.stages) ? journey.stages : [])
    .some((stage) => stage.name === '02 Hotel Search');
}

function hotelBooked(journey) {
  return journey.isBookSuccessful === true || Number(journey.bookOk) > 0;
}

function airReachedSearch(journey) {
  if (journey.passedSearch === true) return true;
  return (Array.isArray(journey.stages) ? journey.stages : [])
    .some((stage) => stage.name === '01 Flight Search');
}

function airBooked(journey) {
  return journey.isBookSuccessful === true || Number(journey.bookOk) > 0;
}

function carsReachedSearch(journey) {
  if (journey.passedSearch === true) return true;
  return (Array.isArray(journey.stages) ? journey.stages : [])
    .some((stage) => stage.name === '02 Car Search');
}

function carsBooked(journey) {
  return journey.isBookSuccessful === true || Number(journey.bookOk) > 0;
}

function samplePercentile(values, p) {
  const nums = (Array.isArray(values) ? values : []).filter(Number.isFinite).sort((a, b) => a - b);
  if (!nums.length) return null;
  const idx = Math.min(nums.length - 1, Math.max(0, Math.ceil((p / 100) * nums.length) - 1));
  return Math.round(nums[idx]);
}

/**
 * The rows are rebuilt from the journeys already on screen, and only while the
 * Analytics tab is the one on screen.
 */
function shouldRefreshDomainComparison({ analyticsVisible, stale } = {}) {
  return analyticsVisible === true && stale === true;
}

/** Verticals that have a Domain Comparison row, in display order. */
const COMPARISON_DOMAINS = ['hotels', 'cruises'];

/**
 * Domain Comparison follows the Domain filter: All compares every vertical, and
 * a single domain compares only itself, so no other vertical is ever queried.
 */
function comparisonDomains(selected) {
  return selected === 'all'
    ? COMPARISON_DOMAINS.slice()
    : COMPARISON_DOMAINS.filter((domain) => domain === selected);
}

function calculateCruiseDomainComparison(journeys) {
  const rows = (Array.isArray(journeys) ? journeys : [])
    .filter((journey) => journey.domain === 'cruises');
  const searched = rows.filter((journey) => journey.passedSearch === true);
  const successful = searched.filter((journey) => journey.isBookSuccessful === true);
  const latencies = rows
    .filter((journey) => journey.overallP95Ms != null)
    .map((journey) => Number(journey.overallP95Ms))
    .filter(Number.isFinite);

  return {
    domain: 'cruises',
    journeys: rows.length,
    conversionRate: searched.length ? successful.length / searched.length : null,
    p95LatencyMs: latencies.length
      ? Math.round(latencies.reduce((sum, latency) => sum + latency, 0) / latencies.length)
      : null,
    exceptions: rows.reduce((sum, journey) => {
      const count = Number(journey.exceptions);
      return sum + (Number.isFinite(count) ? Math.max(0, count) : 0);
    }, 0),
  };
}

function calculateHotelDomainComparison(journeys) {
  const rows = (Array.isArray(journeys) ? journeys : [])
    .filter((journey) => journey.domain === 'hotels');
  const searched = rows.filter(hotelReachedSearch);
  const successful = searched.filter(hotelBooked);
  const latencies = rows
    .filter((journey) => journey.overallP95Ms != null)
    .map((journey) => Number(journey.overallP95Ms))
    .filter(Number.isFinite);

  return {
    domain: 'hotels',
    journeys: rows.length,
    conversionRate: searched.length ? successful.length / searched.length : null,
    p95LatencyMs: samplePercentile(latencies, 95),
    exceptions: rows.reduce((sum, journey) => {
      const count = Number(journey.exceptions);
      return sum + (Number.isFinite(count) ? Math.max(0, count) : 0);
    }, 0),
  };
}

function calculateAirDomainComparison(journeys) {
  const rows = (Array.isArray(journeys) ? journeys : [])
    .filter((journey) => journey.domain === 'air');
  const searched = rows.filter(airReachedSearch);
  const successful = searched.filter(airBooked);
  const latencies = rows
    .filter((journey) => journey.overallP95Ms != null)
    .map((journey) => Number(journey.overallP95Ms))
    .filter(Number.isFinite);

  return {
    domain: 'air',
    journeys: rows.length,
    conversionRate: searched.length ? successful.length / searched.length : null,
    p95LatencyMs: samplePercentile(latencies, 95),
    exceptions: rows.reduce((sum, journey) => {
      const count = Number(journey.exceptions);
      return sum + (Number.isFinite(count) ? Math.max(0, count) : 0);
    }, 0),
  };
}

function calculateCarsDomainComparison(journeys) {
  const rows = (Array.isArray(journeys) ? journeys : [])
    .filter((journey) => journey.domain === 'cars');
  const searched = rows.filter(carsReachedSearch);
  const successful = searched.filter(carsBooked);
  const latencies = rows
    .filter((journey) => journey.overallP95Ms != null)
    .map((journey) => Number(journey.overallP95Ms))
    .filter(Number.isFinite);

  return {
    domain: 'cars',
    journeys: rows.length,
    conversionRate: searched.length ? successful.length / searched.length : null,
    p95LatencyMs: samplePercentile(latencies, 95),
    exceptions: rows.reduce((sum, journey) => {
      const count = Number(journey.exceptions);
      return sum + (Number.isFinite(count) ? Math.max(0, count) : 0);
    }, 0),
  };
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    calculateAirDomainComparison,
    calculateCarsDomainComparison,
    calculateCruiseDomainComparison,
    calculateHotelDomainComparison,
    comparisonDomains,
    shouldRefreshDomainComparison,
  };
}
