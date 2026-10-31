using System.Globalization;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Builds the three probe queries. Static and free of I/O so the generated text can be asserted
/// directly: a wrong probe does not fail loudly, it comes back as a generic Kusto syntax error or
/// - worse - as a valid query over the wrong window, which reads exactly like "no data".
///
/// Every query references the resource as <c>app('{{SOURCE:alias}}')</c> and never a literal
/// subscription path; <see cref="IKqlSourceResolver"/> owns that substitution.
/// </summary>
public static class TelemetryProbeKql
{
    /// <summary>Rows in the window. Answers "does this table carry data", which is a different question from "which keys does it carry".</summary>
    public static string RowCount(string alias, string table, DateTimeOffset from, DateTimeOffset to)
    {
        Guard(alias, table);
        return $"""
            app('{Placeholder(alias)}').{table}
            | where timestamp >= {Datetime(from)} and timestamp < {Datetime(to)}
            | summarize RowCount = count()
            """;
    }

    /// <summary>
    /// Expands the property bag and aggregates by key. <c>mv-expand</c> over <c>bag_keys()</c>
    /// rather than <c>bagexpansion=array</c> so the key is a real string column (and can be used
    /// to index back into the bag for the value) instead of a positional element of a dynamic
    /// pair, which several Kusto versions alias inconsistently.
    ///
    /// The denominator is the SAMPLED row count, not the table's full row count: taking the
    /// coverage of a 50k sample and dividing it by a 4M-row table would under-report every key by
    /// two orders of magnitude.
    /// </summary>
    public static string CustomDimensions(string alias, string table, DateTimeOffset from, DateTimeOffset to, int maxSampledRows, int maxKeys)
    {
        Guard(alias, table);
        return $"""
            let _sampled = materialize(
                app('{Placeholder(alias)}').{table}
                | where timestamp >= {Datetime(from)} and timestamp < {Datetime(to)}
                | project customDimensions
                | take {Count(maxSampledRows)});
            let _sampledRows = toscalar(_sampled | count);
            _sampled
            | mv-expand DimensionKey = bag_keys(customDimensions) to typeof(string)
            | extend DimensionValue = tostring(customDimensions[DimensionKey])
            | summarize
                OccurrenceCount = count(),
                NonEmptyCount = countif(isnotempty(DimensionValue)),
                DistinctValueCount = dcount(DimensionValue, 1),
                MaxValueLength = max(strlen(DimensionValue))
                by DimensionKey
            | extend SampledRows = _sampledRows
            | top {Count(maxKeys)} by OccurrenceCount desc
            """;
    }

    /// <summary>
    /// Distinct hosts and cloud_RoleNames - the candidates for ServiceDefinition.HostFilters.
    /// <c>isfuzzy=true</c> because a resource with no pageViews must still yield the hosts that
    /// requests saw; which tables actually exist is already reported by the row-count probes, so
    /// swallowing it here loses nothing.
    /// </summary>
    public static string Hosts(string alias, DateTimeOffset from, DateTimeOffset to, int maxSampledRows, int maxRows)
    {
        GuardAlias(alias);

        var branches = TelemetryProbeTables.HostBearing.Select(table => $"""
                (app('{Placeholder(alias)}').{table}
                    | where timestamp >= {Datetime(from)} and timestamp < {Datetime(to)}
                    | project timestamp, cloud_RoleName, url, TelemetryTable = "{table}"
                    | take {Count(maxSampledRows)})
            """.TrimEnd());

        return $"""
            union isfuzzy=true
            {string.Join(",\n", branches)}
            | extend _parsedUrl = parse_url(tostring(url))
            | extend RoleName = tostring(cloud_RoleName), Host = tolower(tostring(_parsedUrl.Host))
            | summarize ObservationCount = count() by TelemetryTable, RoleName, Host
            | top {Count(maxRows)} by ObservationCount desc
            """;
    }

    private static string Placeholder(string alias) => "{{SOURCE:" + alias + "}}";

    private static string Datetime(DateTimeOffset value) =>
        $"datetime({value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}Z)";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Both values are concatenated straight into KQL, so neither may reach here unvalidated.
    /// This throws rather than sanitising: silently rewriting a caller's alias into something that
    /// resolves is how a probe ends up profiling the wrong customer's resource.
    /// </summary>
    private static void Guard(string alias, string table)
    {
        GuardAlias(alias);
        if (TelemetryProbeTables.Canonical(table) is null)
        {
            throw new ArgumentException($"\"{table}\" is not a probe-able telemetry table.", nameof(table));
        }
    }

    private static void GuardAlias(string alias)
    {
        if (!TelemetrySourceAlias.IsValid(alias))
        {
            throw new ArgumentException($"Telemetry source alias \"{alias}\" is not a valid alias.", nameof(alias));
        }
    }
}
