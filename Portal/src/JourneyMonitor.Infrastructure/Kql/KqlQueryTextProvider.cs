using System.Collections.Concurrent;
using System.Reflection;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Kql;

/// <summary>
/// Loads the raw KQL text embedded from the files under Kql/. Embedding avoids any dependency on
/// the process working directory.
///
/// Every accessor resolves through <see cref="GetByKey"/> so <c>DbKqlQueryTextProvider</c> only
/// has to intercept one method for a stored row to override a built-in query.
/// </summary>
public sealed class KqlQueryTextProvider : IKqlQueryTextProvider
{
    private static readonly Assembly Assembly = typeof(KqlQueryTextProvider).Assembly;
    private const string Prefix = "JourneyMonitor.Infrastructure.Kql.";

    private static readonly Dictionary<string, string> ResourceByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        [KqlQueryKeys.Journey("hotels")] = Prefix + "TMobile_HOTEL_Query_KQL_v6d_demo_2h.txt",
        [KqlQueryKeys.Journey("cars")] = Prefix + "TMobile_Cars_Query_KQL_v6.txt",
        [KqlQueryKeys.Journey("cruises")] = Prefix + "TMobile_CRUISE_Query_KQL_v2.txt",
        [KqlQueryKeys.Journey("air")] = Prefix + "TMobile_AIR_Query_KQL_v6_demo_2h.txt",

        [KqlQueryKeys.IncidentExceptions] = Prefix + "incident-exceptions.txt",
        [KqlQueryKeys.ScoutDrillDown] = Prefix + "scout-drilldown.kql.txt",

        [KqlQueryKeys.ScoutBoard("latency")] = Prefix + "scout-latency-priorities.kql.txt",
        [KqlQueryKeys.ScoutBoard("exception")] = Prefix + "scout-exception-priorities.kql.txt",

        [KqlQueryKeys.InspectorLogs("club-baseline")] = Prefix + "inspector-club-baseline.kql.txt",
        [KqlQueryKeys.InspectorLogs("signature-breakdown")] = Prefix + "inspector-signature-breakdown.kql.txt",
        [KqlQueryKeys.InspectorLogs("client-breakdown")] = Prefix + "inspector-client-breakdown.kql.txt",

        [KqlQueryKeys.LatencySpikes("cars")] = Prefix + "cars-latency-spikes.kql.txt",
        [KqlQueryKeys.LatencySpikes("hotels")] = Prefix + "hotel-latency-spikes.kql.txt",
        [KqlQueryKeys.LatencySpikes("cruises")] = Prefix + "cruise-latency-spikes.kql.txt",
        // "air": not authored yet - Air's journey KQL computes per-step timing inside a
        // cross-resource, multi-pass materialize() pipeline (see TMobile_AIR_Query_KQL_v6_demo_2h.txt),
        // not a flat per-request Stage/Ms projection like Cars/Hotel/Cruise. Extracting a correct
        // per-transaction spike query from it needs its own focused authoring/validation pass,
        // not a copy-paste - see the implementation plan's Phase 4 notes.

        [KqlQueryKeys.Dashboard("hotels", "step_latency_p95")] = Prefix + "dashboard-hotels-step-latency-p95.txt",
        [KqlQueryKeys.Dashboard("hotels", "exceptions_by_step")] = Prefix + "dashboard-hotels-exceptions-by-step.txt",
        [KqlQueryKeys.Dashboard("cruises", "step_latency_p95")] = Prefix + "dashboard-cruises-step-latency-p95.txt",
        [KqlQueryKeys.Dashboard("cruises", "exceptions_by_step")] = Prefix + "dashboard-cruises-exceptions-by-step.txt",
    };

    // Registered as a singleton (see ServiceCollectionExtensions), so this cache is shared across
    // concurrent HTTP requests and timer invocations - ConcurrentDictionary avoids the race/corruption
    // a plain Dictionary would risk under concurrent GetOrAdd-style access. Safe to keep forever:
    // embedded resources cannot change while the process runs. The database override is cached
    // separately, with a TTL, in DbKqlQueryTextProvider.
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public IReadOnlyCollection<string> KnownKeys => ResourceByKey.Keys;

    public string GetByKey(string queryKey)
    {
        if (!ResourceByKey.TryGetValue(queryKey, out var resourceName))
        {
            throw new ArgumentException($"No embedded KQL mapped for key '{queryKey}'.", nameof(queryKey));
        }
        return Load(resourceName);
    }

    public string GetJourneyKql(string domain)
    {
        var d = domain.ToLowerInvariant();
        // Air is excluded here on purpose: its caller injects two cross-resource halves and uses
        // GetAirJourneyKql. Keeping it out preserves the original "no KQL file mapped" contract.
        if (d == "air" || !ResourceByKey.ContainsKey(KqlQueryKeys.Journey(d)))
        {
            throw new ArgumentException($"No KQL file mapped for domain '{domain}'.", nameof(domain));
        }
        return GetByKey(KqlQueryKeys.Journey(d));
    }

    public string GetAirJourneyKql() => GetByKey(KqlQueryKeys.Journey("air"));

    public string GetLatencySpikesKql(string domain)
    {
        var key = KqlQueryKeys.LatencySpikes(domain);
        if (!ResourceByKey.ContainsKey(key))
        {
            var authored = ResourceByKey.Keys
                .Where(k => k.StartsWith("latency-spikes:", StringComparison.Ordinal))
                .Select(k => k["latency-spikes:".Length..]);
            throw new ArgumentException(
                $"No latency-spikes KQL authored yet for domain '{domain}' (only {string.Join(", ", authored)} exist today — see the implementation plan's Phase 4 for the rollout order).",
                nameof(domain));
        }
        return GetByKey(key);
    }

    public string GetDashboardChartKql(string domain, string chart)
    {
        var key = KqlQueryKeys.Dashboard(domain, chart);
        if (!ResourceByKey.ContainsKey(key))
        {
            throw new ArgumentException(
                $"No dashboard KQL mapped for '{domain.ToLowerInvariant()}:{chart.ToLowerInvariant()}'.", nameof(chart));
        }
        return GetByKey(key);
    }

    public string GetIncidentExceptionsKql() => GetByKey(KqlQueryKeys.IncidentExceptions);

    public string GetScoutBoardKql(string board)
    {
        var key = KqlQueryKeys.ScoutBoard(board);
        if (!ResourceByKey.ContainsKey(key))
        {
            var boards = ResourceByKey.Keys
                .Where(k => k.StartsWith("scout:", StringComparison.Ordinal) && k != KqlQueryKeys.ScoutDrillDown)
                .Select(k => k["scout:".Length..]);
            throw new ArgumentException(
                $"No Scout board KQL mapped for '{board}' (known boards: {string.Join(", ", boards)}).",
                nameof(board));
        }
        return GetByKey(key);
    }

    public string GetScoutDrillDownKql() => GetByKey(KqlQueryKeys.ScoutDrillDown);

    public string GetInspectorLogsKql(string query)
    {
        var key = KqlQueryKeys.InspectorLogs(query);
        if (!ResourceByKey.ContainsKey(key))
        {
            var queries = ResourceByKey.Keys
                .Where(k => k.StartsWith("inspector:", StringComparison.Ordinal))
                .Select(k => k["inspector:".Length..]);
            throw new ArgumentException(
                $"No Inspector LogsAgent KQL mapped for '{query}' (known queries: {string.Join(", ", queries)}).",
                nameof(query));
        }
        return GetByKey(key);
    }

    private static string Load(string resourceName) =>
        Cache.GetOrAdd(resourceName, name =>
        {
            using var stream = Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"KQL embedded resource not found: {name}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
}
