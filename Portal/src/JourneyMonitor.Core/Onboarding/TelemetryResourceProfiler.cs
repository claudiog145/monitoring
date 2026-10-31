using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Probes one registered telemetry source and reports what it actually carries.
///
/// Three rules shape every line of this class:
/// - It never throws for a per-table failure. One table that cannot be read degrades that table
///   and the report says so; the other five still get measured, because a report that dies on
///   <c>traces</c> tells an onboarding engineer nothing about <c>requests</c>.
/// - It never promotes or hides a measurement. A key on 4% of rows is reported as a 4% key.
/// - It calls no model. Turning these numbers into a CompanyProfile is a later phase; the value
///   of this one is that its output can be checked against the resource by hand.
/// </summary>
public sealed class TelemetryResourceProfiler(
    ITelemetrySource telemetrySource,
    ITelemetrySourceStore sourceStore,
    IKqlSourceResolver sourceResolver,
    ILogger<TelemetryResourceProfiler> logger,
    TimeProvider? timeProvider = null) : ITelemetryResourceProfiler
{
    /// <summary>Below this, the key cannot carry an attribution on its own - the number behind the "79% unattributed" finding.</summary>
    private const double UsableCoveragePercent = 50.0;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<TelemetryResourceProfile> ProfileAsync(TelemetryProfileRequest request, CancellationToken ct = default)
    {
        var normalized = (request ?? throw new ArgumentNullException(nameof(request))).Normalize();
        var now = _clock.GetUtcNow();
        var to = normalized.WindowTo ?? now;
        var from = to.AddHours(-normalized.WindowHours);

        string environment;
        try
        {
            environment = InsightsEnvironment.Normalize(normalized.Environment);
        }
        catch (InsightsConfigurationException ex)
        {
            return Unresolved(normalized, normalized.Environment, from, to, now, ex.Message);
        }

        if (!TelemetrySourceAlias.IsValid(normalized.Alias))
        {
            return Unresolved(normalized, environment, from, to, now,
                $"\"{normalized.Alias}\" is not a valid telemetry source alias (letters, digits, dot, dash, underscore; 1-64 characters).");
        }

        var tables = ResolveTables(normalized.Tables, out var rejectedTables);
        if (tables.Count == 0)
        {
            return Unresolved(normalized, environment, from, to, now,
                $"No probe-able table requested. Allowed: {string.Join(", ", TelemetryProbeTables.Default)}.");
        }

        string appId;
        string? tenantId;
        try
        {
            (appId, tenantId) = await ResolveQueryTargetAsync(normalized.Alias, environment, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unresolved(normalized, environment, from, to, now, ex.Message);
        }

        var warnings = new List<string>();
        foreach (var rejected in rejectedTables)
        {
            warnings.Add($"Ignored \"{rejected}\": not a probe-able telemetry table.");
        }

        var tableProfiles = new List<TelemetryTableProfile>();
        foreach (var table in tables)
        {
            tableProfiles.Add(await ProfileTableAsync(normalized, environment, table, from, to, appId, tenantId, ct));
        }

        var hosts = await ProfileHostsAsync(normalized, environment, from, to, appId, tenantId, ct);

        var candidates = EntityIdentityCandidateRanker.Rank(
            tableProfiles.SelectMany(t => t.Dimensions.Select(d => (t.Table, Dimension: d))));

        warnings.AddRange(BuildWarnings(normalized, tableProfiles, hosts, candidates));

        return new TelemetryResourceProfile(
            Alias: normalized.Alias,
            Environment: environment,
            WindowFrom: from,
            WindowTo: to,
            WindowHours: normalized.WindowHours,
            MaxSampledRows: normalized.MaxSampledRows,
            Status: DetermineStatus(tableProfiles, hosts),
            Error: null,
            Tables: tableProfiles,
            IdentityCandidates: candidates,
            Hosts: hosts,
            Warnings: warnings,
            GeneratedAt: now);
    }

    private async Task<TelemetryTableProfile> ProfileTableAsync(
        TelemetryProfileRequest request,
        string environment,
        string table,
        DateTimeOffset from,
        DateTimeOffset to,
        string appId,
        string? tenantId,
        CancellationToken ct)
    {
        var rowCountKql = TelemetryProbeKql.RowCount(request.Alias, table, from, to);
        var dimensionKql = TelemetryProbeKql.CustomDimensions(request.Alias, table, from, to, request.MaxSampledRows, request.MaxDimensionKeys);

        var countResult = await RunProbeAsync(rowCountKql, environment, appId, tenantId, table, "row count", ct);
        if (countResult.State != TelemetryProbeState.Ok)
        {
            return new TelemetryTableProfile(
                table, countResult.State, 0, countResult.Error, rowCountKql,
                TelemetryProbeState.Skipped, 0, [], null, dimensionKql);
        }

        // A count query always returns exactly one row; zero rows back means the table exists but
        // the window held nothing, which is a finding rather than a failure.
        var rowCount = GetLong(countResult.Rows.FirstOrDefault(), "RowCount");
        if (rowCount <= 0)
        {
            return new TelemetryTableProfile(
                table, TelemetryProbeState.NoData, 0, null, rowCountKql,
                TelemetryProbeState.Skipped, 0, [], null, dimensionKql);
        }

        var dimensionResult = await RunProbeAsync(dimensionKql, environment, appId, tenantId, table, "customDimensions", ct);
        if (dimensionResult.State != TelemetryProbeState.Ok)
        {
            return new TelemetryTableProfile(
                table, TelemetryProbeState.Ok, rowCount, null, rowCountKql,
                dimensionResult.State, 0, [], dimensionResult.Error, dimensionKql);
        }

        // SampledRows is echoed by the query itself. When the expansion yields no rows at all the
        // column never arrives, so fall back to what the cap would have sampled - the denominator
        // still has to be honest even though there is nothing to divide by it.
        var sampledFromQuery = GetLong(dimensionResult.Rows.FirstOrDefault(), "SampledRows");
        var sampledRows = sampledFromQuery > 0 ? sampledFromQuery : Math.Min(rowCount, request.MaxSampledRows);

        var dimensions = dimensionResult.Rows
            .Select(r => ToDimension(r, sampledRows))
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderByDescending(d => d.NonEmptyCoveragePercent)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

        return new TelemetryTableProfile(
            table,
            TelemetryProbeState.Ok,
            rowCount,
            null,
            rowCountKql,
            // Rows exist but carry no customDimensions at all: not a failure, and emphatically not
            // the same as "we did not look".
            dimensions.Count > 0 ? TelemetryProbeState.Ok : TelemetryProbeState.NoData,
            sampledRows,
            dimensions,
            null,
            dimensionKql);
    }

    private async Task<TelemetryHostProfile> ProfileHostsAsync(
        TelemetryProfileRequest request,
        string environment,
        DateTimeOffset from,
        DateTimeOffset to,
        string appId,
        string? tenantId,
        CancellationToken ct)
    {
        var kql = TelemetryProbeKql.Hosts(request.Alias, from, to, request.MaxSampledRows, request.MaxHostRows);
        var result = await RunProbeAsync(kql, environment, appId, tenantId, "requests+pageViews", "hosts", ct);
        if (result.State != TelemetryProbeState.Ok)
        {
            return new TelemetryHostProfile(result.State, result.Error, kql, [], []);
        }

        var hosts = result.Rows
            .Select(r => (Host: r.GetString("Host").Trim(), Count: GetLong(r, "ObservationCount")))
            .Where(h => h.Host.Length > 0)
            .GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ObservedHost(g.Key, g.Sum(x => x.Count)))
            .OrderByDescending(h => h.ObservationCount)
            .ThenBy(h => h.Host, StringComparer.Ordinal)
            .ToList();

        var roles = result.Rows
            .Select(r => (Role: r.GetString("RoleName").Trim(), Count: GetLong(r, "ObservationCount")))
            .Where(r => r.Role.Length > 0)
            .GroupBy(r => r.Role, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ObservedRoleName(g.Key, g.Sum(x => x.Count)))
            .OrderByDescending(r => r.ObservationCount)
            .ThenBy(r => r.RoleName, StringComparer.Ordinal)
            .ToList();

        return new TelemetryHostProfile(
            hosts.Count > 0 || roles.Count > 0 ? TelemetryProbeState.Ok : TelemetryProbeState.NoData,
            null,
            kql,
            hosts,
            roles);
    }

    private async Task<ProbeResult> RunProbeAsync(
        string kql,
        string environment,
        string appId,
        string? tenantId,
        string table,
        string probe,
        CancellationToken ct)
    {
        try
        {
            var resolved = await sourceResolver.ResolveAsync(kql, environment, ct);
            KqlPlaceholders.AssertNoneLeft(resolved, $"Telemetry discovery {probe} probe");
            var rows = await telemetrySource.QueryAsync(new TelemetryQueryRequest(resolved, appId, tenantId), ct);
            return new ProbeResult(TelemetryProbeState.Ok, rows, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = TelemetryProbeFailureClassifier.Flatten(ex);
            var state = TelemetryProbeFailureClassifier.Classify(message);
            logger.LogWarning(ex, "[TelemetryDiscovery] {Probe} probe on {Table} degraded to {State}.", probe, table, state);
            return new ProbeResult(state, [], message);
        }
    }

    /// <summary>
    /// The app id the Application Insights REST API is addressed with. It lives on the catalog row
    /// (monitoring.TelemetrySource.AppId) rather than in Insights configuration on purpose: a
    /// newly onboarded company is a row, and requiring a redeploy to profile it would defeat the
    /// point of profiling it on day one.
    /// </summary>
    private async Task<(string AppId, string? TenantId)> ResolveQueryTargetAsync(string alias, string environment, CancellationToken ct)
    {
        IReadOnlyList<TelemetrySourceEntry> sources;
        try
        {
            var rows = await sourceStore.GetAllAsync(ct);
            // Same fallback KqlSourceResolver uses: an empty table means the seed never ran.
            sources = rows.Count > 0 ? rows : TelemetrySourceDefaults.All;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The telemetry source catalog could not be read: {ex.Message}");
        }

        var entry = sources.FirstOrDefault(s =>
            s.Alias.Trim().Equals(alias, StringComparison.OrdinalIgnoreCase) &&
            s.Environment.Trim().Equals(environment, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            var known = string.Join(", ", sources
                .Where(s => s.Environment.Trim().Equals(environment, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Alias).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"No telemetry source registered for alias \"{alias}\" in environment \"{environment}\" " +
                $"(registered for that environment: {(known.Length > 0 ? known : "none")}).");
        }
        if (!entry.Enabled)
        {
            throw new InvalidOperationException($"Telemetry source \"{alias}\" is disabled for environment \"{environment}\".");
        }
        if (string.IsNullOrWhiteSpace(entry.AppId))
        {
            throw new InvalidOperationException(
                $"Telemetry source \"{alias}\" ({environment}) has no AppId. Set the Application Insights Application ID " +
                "on the source in Settings before profiling it - the query API is addressed by app id, not by resource id.");
        }

        return (entry.AppId.Trim(), string.IsNullOrWhiteSpace(entry.TenantId) ? null : entry.TenantId.Trim());
    }

    private static IReadOnlyList<string> ResolveTables(IReadOnlyList<string>? requested, out IReadOnlyList<string> rejected)
    {
        if (requested is null || requested.Count == 0)
        {
            rejected = [];
            return TelemetryProbeTables.Default;
        }

        var accepted = new List<string>();
        var bad = new List<string>();
        foreach (var name in requested)
        {
            var canonical = TelemetryProbeTables.Canonical(name);
            if (canonical is null) bad.Add(name ?? "");
            else if (!accepted.Contains(canonical, StringComparer.Ordinal)) accepted.Add(canonical);
        }

        rejected = bad;
        return accepted;
    }

    private static CustomDimensionProfile? ToDimension(TelemetryJourneyRow row, long sampledRows)
    {
        var key = row.GetString("DimensionKey").Trim();
        if (key.Length == 0) return null;

        var occurrences = GetLong(row, "OccurrenceCount");
        var nonEmpty = GetLong(row, "NonEmptyCount");
        var distinct = GetLong(row, "DistinctValueCount");
        var maxLength = (int)Math.Min(int.MaxValue, GetLong(row, "MaxValueLength"));

        return new CustomDimensionProfile(
            Key: key,
            OccurrenceCount: occurrences,
            NonEmptyCount: nonEmpty,
            DistinctValueCount: distinct,
            MaxValueLength: maxLength,
            CoveragePercent: Percent(occurrences, sampledRows),
            NonEmptyCoveragePercent: Percent(nonEmpty, sampledRows));
    }

    /// <summary>Two decimals, because the difference between 3.5% and 4% attribution is not worth pretending to know.</summary>
    private static double Percent(long part, long total) =>
        total <= 0 ? 0.0 : Math.Round(100.0 * part / total, 2);

    private static long GetLong(TelemetryJourneyRow? row, string column)
    {
        var value = row?.GetDouble(column);
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return 0;
        return (long)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    private static TelemetryProfileStatus DetermineStatus(IReadOnlyList<TelemetryTableProfile> tables, TelemetryHostProfile hosts)
    {
        var anyFailure =
            hosts.State == TelemetryProbeState.Failed ||
            tables.Any(t => t.RowCountState == TelemetryProbeState.Failed || t.DimensionState == TelemetryProbeState.Failed);
        if (anyFailure) return TelemetryProfileStatus.Degraded;

        return tables.All(t => t.RowCountState is TelemetryProbeState.NoData or TelemetryProbeState.TableAbsent)
            ? TelemetryProfileStatus.Empty
            : TelemetryProfileStatus.Complete;
    }

    private static IEnumerable<string> BuildWarnings(
        TelemetryProfileRequest request,
        IReadOnlyList<TelemetryTableProfile> tables,
        TelemetryHostProfile hosts,
        IReadOnlyList<EntityIdentityCandidate> candidates)
    {
        foreach (var table in tables)
        {
            if (table.RowCountState == TelemetryProbeState.Failed)
            {
                yield return $"{table.Table}: row count probe failed, so this table is unmeasured - absence of findings here is not evidence of absence. {table.RowCountError}";
            }
            else if (table.RowCountState == TelemetryProbeState.TableAbsent)
            {
                yield return $"{table.Table}: table does not exist in this resource.";
            }
            else if (table.RowCountState == TelemetryProbeState.NoData)
            {
                yield return $"{table.Table}: table exists but held no rows in the window.";
            }

            if (table.DimensionState == TelemetryProbeState.Failed)
            {
                yield return $"{table.Table}: customDimensions probe failed, so no key coverage was measured for it. {table.DimensionError}";
            }
            else if (table.DimensionState == TelemetryProbeState.NoData && table.RowCountState == TelemetryProbeState.Ok)
            {
                yield return $"{table.Table}: {table.RowCount:N0} rows carry no customDimensions keys at all.";
            }

            if (table.RowCountState == TelemetryProbeState.Ok && table.RowCount > request.MaxSampledRows)
            {
                yield return $"{table.Table}: coverage measured on a sample of {table.SampledRows:N0} of {table.RowCount:N0} rows (probe cap).";
            }
        }

        if (hosts.State == TelemetryProbeState.Failed)
        {
            yield return $"Host probe failed, so no HostFilters candidates were measured. {hosts.Error}";
        }
        else if (hosts.State == TelemetryProbeState.NoData)
        {
            yield return "No hosts or cloud_RoleNames observed - this resource offers nothing to populate ServiceDefinition.HostFilters with.";
        }

        var best = candidates.FirstOrDefault();
        if (best is null)
        {
            yield return "No customDimensions key was measured on any table: nothing in this resource can attribute an incident to a customer.";
        }
        else if (best.NonEmptyCoveragePercent < UsableCoveragePercent)
        {
            yield return $"Best identity candidate is \"{best.Key}\" on {best.Table} at {best.NonEmptyCoveragePercent:0.##}% coverage - " +
                $"under {UsableCoveragePercent:0.##}%, so most telemetry in this resource would stay unattributed.";
        }
    }

    private static TelemetryResourceProfile Unresolved(
        TelemetryProfileRequest request,
        string environment,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        string error) =>
        new(
            Alias: request.Alias,
            Environment: environment,
            WindowFrom: from,
            WindowTo: to,
            WindowHours: request.WindowHours,
            MaxSampledRows: request.MaxSampledRows,
            Status: TelemetryProfileStatus.SourceUnresolved,
            Error: error,
            Tables: [],
            IdentityCandidates: [],
            Hosts: new TelemetryHostProfile(TelemetryProbeState.Skipped, null, "", [], []),
            Warnings: [error],
            GeneratedAt: now);

    private sealed record ProbeResult(TelemetryProbeState State, IReadOnlyList<TelemetryJourneyRow> Rows, string? Error);
}
