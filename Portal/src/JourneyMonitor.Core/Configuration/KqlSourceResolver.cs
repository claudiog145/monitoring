using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Configuration;

/// <summary>Thrown when KQL text cannot be pointed at a real telemetry resource.</summary>
public sealed class KqlSourceResolutionException(string message) : Exception(message);

/// <summary>
/// Turns <c>app('{{SOURCE:cruise-core}}')</c> into <c>app('/subscriptions/.../cruise-core-ovs-ai-prod')</c>
/// using the registered catalog for the requested environment.
///
/// Unlike <see cref="Monitoring.ServiceOwnerResolver"/>, this one throws. An unresolved owner can
/// be reported as "nobody owns this" and the investigation still ships; an unresolved resource id
/// produces a query that returns nothing, and an empty board is indistinguishable from a healthy
/// one. Failing loudly here is the only way that stays visible.
///
/// Does NOT reject a literal subscription path in the text here: some callers (Air/Cruise journeys
/// and dashboards - see ClubFilter.InjectAirJourneyKql/InjectDashboardKql) substitute a real,
/// already-trusted ARM path into the KQL themselves before calling ResolveAsync, and rejecting that
/// would fail every one of their queries. The allow-list is enforced once, earlier, on the raw
/// template before any substitution - see KqlQueryAdminService.ValidateAsync (save time for stored
/// queries), the only point the "trusted or not" distinction is still knowable.
/// </summary>
public sealed class KqlSourceResolver(
    ITelemetrySourceStore store,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<KqlSourceResolver> logger) : IKqlSourceResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static readonly Regex PlaceholderPattern =
        new(@"\{\{SOURCE:(?<alias>[A-Za-z0-9._-]{1,64})\}\}", RegexOptions.Compiled);

    // Reference assignment is atomic; concurrent readers can at worst both refresh.
    private Snapshot? _snapshot;

    public async Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default)
    {
        var text = kql ?? "";

        if (!PlaceholderPattern.IsMatch(text)) return text;

        var env = InsightsEnvironment.Normalize(environment);
        var sources = await GetAllAsync(ct);

        return PlaceholderPattern.Replace(text, match =>
        {
            var alias = match.Groups["alias"].Value;
            var entry = sources.FirstOrDefault(s =>
                s.Alias.Trim().Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                s.Environment.Trim().Equals(env, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                var known = string.Join(", ", sources.Where(s => s.Environment.Equals(env, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Alias).Distinct().OrderBy(a => a));
                throw new KqlSourceResolutionException(
                    $"No telemetry source registered for alias \"{alias}\" in environment \"{env}\" " +
                    $"(registered for that environment: {(known.Length > 0 ? known : "none")}).");
            }
            if (!entry.Enabled)
            {
                throw new KqlSourceResolutionException($"Telemetry source \"{alias}\" is disabled for environment \"{env}\".");
            }
            if (!InsightsSourceResolver.IsInsightsResourceId(entry.ResourceId))
            {
                throw new KqlSourceResolutionException(
                    $"Telemetry source \"{alias}\" ({env}) does not hold an Application Insights ARM resource id.");
            }

            return entry.ResourceId;
        });
    }

    public void Invalidate() => _snapshot = null;

    private async Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct)
    {
        var cached = _snapshot;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Sources;

        try
        {
            var rows = await store.GetAllAsync(ct);
            // An empty table means the seed never ran, not that every source was deleted on purpose.
            var effective = rows.Count > 0 ? rows : TelemetrySourceDefaults.All;
            _snapshot = new Snapshot(effective, DateTimeOffset.UtcNow.Add(CacheTtl));
            return effective;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TelemetrySource] catalog could not be read - falling back to the built-in sources.");
            _snapshot = new Snapshot(TelemetrySourceDefaults.All, DateTimeOffset.UtcNow.Add(CacheTtl));
            return TelemetrySourceDefaults.All;
        }
    }

    private sealed record Snapshot(IReadOnlyList<TelemetrySourceEntry> Sources, DateTimeOffset ExpiresAt);
}
