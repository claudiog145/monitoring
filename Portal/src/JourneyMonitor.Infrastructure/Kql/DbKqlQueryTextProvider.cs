using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Kql;

/// <summary>
/// Lets a row in <c>monitoring.KqlQuery</c> override the embedded text for the same key, and falls
/// back to the built-in file for every key that has no enabled row. A database that was never
/// seeded therefore behaves exactly like the pre-override build.
///
/// The snapshot is refreshed synchronously rather than in the background. <see cref="IKqlQueryTextProvider"/>
/// is a synchronous interface consulted from timers, and the alternative - serve a stale snapshot
/// while a refresh runs - would mean a query saved in the portal keeps running the previous text
/// for one more tick with no way to tell which one produced a given alert. Blocking here is safe:
/// neither the Functions host nor ASP.NET Core installs a SynchronizationContext, so there is no
/// deadlock path, and the read is a single indexed round trip behind a 30-second TTL.
///
/// A failed read degrades to the built-in text instead of throwing. Unlike an unresolved telemetry
/// source, this cannot silently return nothing: the built-in query is the one that shipped and is
/// known to work, so the worst case is running yesterday's text, which is logged.
/// </summary>
public sealed class DbKqlQueryTextProvider(
    KqlQueryTextProvider builtIn,
    IKqlQueryStore store,
    ILogger<DbKqlQueryTextProvider> logger) : IKqlQueryTextProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly Lock _refreshLock = new();
    private Snapshot? _snapshot;

    /// <summary>Called after a write so the next read picks the new text up immediately.</summary>
    public void Invalidate()
    {
        lock (_refreshLock)
        {
            _snapshot = null;
        }
    }

    public IReadOnlyCollection<string> KnownKeys => builtIn.KnownKeys;

    public string GetByKey(string queryKey)
    {
        var overrides = GetOverrides();
        if (overrides.TryGetValue(queryKey, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        return builtIn.GetByKey(queryKey);
    }

    // The typed accessors keep their own argument validation (which key exists, what the message
    // says) in the built-in provider, then re-resolve through GetByKey so an override still wins.
    public string GetJourneyKql(string domain)
    {
        builtIn.GetJourneyKql(domain);
        return GetByKey(KqlQueryKeys.Journey(domain));
    }

    public string GetAirJourneyKql() => GetByKey(KqlQueryKeys.Journey("air"));

    public string GetLatencySpikesKql(string domain)
    {
        builtIn.GetLatencySpikesKql(domain);
        return GetByKey(KqlQueryKeys.LatencySpikes(domain));
    }

    public string GetDashboardChartKql(string domain, string chart)
    {
        builtIn.GetDashboardChartKql(domain, chart);
        return GetByKey(KqlQueryKeys.Dashboard(domain, chart));
    }

    public string GetIncidentExceptionsKql() => GetByKey(KqlQueryKeys.IncidentExceptions);

    public string GetScoutBoardKql(string board)
    {
        builtIn.GetScoutBoardKql(board);
        return GetByKey(KqlQueryKeys.ScoutBoard(board));
    }

    public string GetScoutDrillDownKql() => GetByKey(KqlQueryKeys.ScoutDrillDown);

    public string GetInspectorLogsKql(string query)
    {
        builtIn.GetInspectorLogsKql(query);
        return GetByKey(KqlQueryKeys.InspectorLogs(query));
    }

    private IReadOnlyDictionary<string, string> GetOverrides()
    {
        var cached = _snapshot;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Texts;

        lock (_refreshLock)
        {
            cached = _snapshot;
            if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Texts;

            IReadOnlyDictionary<string, string> texts;
            try
            {
                texts = store.GetEnabledTextsAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[KqlQuery] stored queries could not be read - using the built-in text for every key.");
                texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            _snapshot = new Snapshot(texts, DateTimeOffset.UtcNow.Add(CacheTtl));
            return texts;
        }
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, string> Texts, DateTimeOffset ExpiresAt);
}
