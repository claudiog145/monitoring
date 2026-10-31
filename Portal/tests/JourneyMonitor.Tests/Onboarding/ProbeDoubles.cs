using System.Text.Json;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Onboarding;

/// <summary>
/// Hand-written doubles for the discovery probes. Deliberately keyed off the KQL the profiler
/// actually generated rather than off a call counter: the point of several of these tests is that
/// the right query went to the right table, and a positional fake would pass even if the profiler
/// probed <c>traces</c> twice and never touched <c>requests</c>.
/// </summary>
internal static class ProbeKind
{
    public const string RowCount = "row-count";
    public const string Dimensions = "dimensions";
    public const string Hosts = "hosts";
    public const string Unknown = "unknown";

    private static readonly Regex TablePattern = new(@"\)\.(?<table>[A-Za-z]+)", RegexOptions.Compiled);

    public static string Of(string kql)
    {
        if (kql.Contains("union isfuzzy=true", StringComparison.Ordinal)) return Hosts;
        if (kql.Contains("bag_keys(", StringComparison.Ordinal)) return Dimensions;
        if (kql.Contains("summarize RowCount = count()", StringComparison.Ordinal)) return RowCount;
        return Unknown;
    }

    public static string TableOf(string kql)
    {
        var match = TablePattern.Match(kql);
        return match.Success ? match.Groups["table"].Value : "";
    }
}

/// <summary>An <see cref="ITelemetrySource"/> whose answer (rows or throw) is decided per query text.</summary>
internal sealed class ScriptedTelemetrySource(Func<string, IReadOnlyList<TelemetryJourneyRow>> respond) : ITelemetrySource
{
    public List<string> ExecutedKql { get; } = [];

    public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        ExecutedKql.Add(request.Kql);
        return Task.FromResult(respond(request.Kql));
    }
}

internal sealed class FakeTelemetrySourceStore(params TelemetrySourceEntry[] entries) : ITelemetrySourceStore
{
    public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TelemetrySourceEntry>>(entries);

    public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default) =>
        throw new NotSupportedException("The profiler is read-only.");

    public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default) =>
        throw new NotSupportedException("The profiler is read-only.");

    public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
        throw new NotSupportedException("The profiler is read-only.");
}

internal static class ProbeRows
{
    public static TelemetryJourneyRow Row(string json) => JsonSerializer.Deserialize<TelemetryJourneyRow>(json)!;

    public static IReadOnlyList<TelemetryJourneyRow> Count(long rowCount) =>
        [Row($$"""{"RowCount": {{rowCount}}}""")];

    public static TelemetryJourneyRow Dimension(
        string key, long occurrences, long nonEmpty, long distinct, int maxValueLength, long sampledRows) =>
        Row($$"""
            {"DimensionKey":"{{key}}","OccurrenceCount":{{occurrences}},"NonEmptyCount":{{nonEmpty}},
             "DistinctValueCount":{{distinct}},"MaxValueLength":{{maxValueLength}},"SampledRows":{{sampledRows}}}
            """);

    public static TelemetryJourneyRow Host(string table, string roleName, string host, long count) =>
        Row($$"""
            {"TelemetryTable":"{{table}}","RoleName":"{{roleName}}","Host":"{{host}}","ObservationCount":{{count}}}
            """);
}
