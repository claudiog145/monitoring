using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>
/// Contract v4 §5 rules for KPIs whose <see cref="KpiDefinition.SourceKey"/> is <c>kql:{queryKey}</c>:
/// the query comes from the audited <c>monitoring.KqlQuery</c> catalog, may only use the allowed
/// tokens (plus <c>{{SOURCE:alias}}</c>, resolved by the telemetry source catalog) and must return
/// the v1 output contract, plus the optional entity columns of §2 for <see cref="KpiGrain.Entity"/>.
/// </summary>
public static class KqlKpiContract
{
    public const string SourceKeyPrefix = "kql:";

    public static readonly IReadOnlyList<string> AllowedTokens = ["from", "to", "bucket", "perBucket", "stage", "grain"];

    private const int MaxEntityKeyLength = 256;
    private const int MaxReportedErrors = 5;

    private static readonly Regex TokenPattern = new(@"\{\{(?<name>[^{}\r\n]{0,128})\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsKqlSourceKey(string? sourceKey) => QueryKeyOf(sourceKey) is not null;

    /// <summary>True for anything that starts with <c>kql:</c>, valid or not.</summary>
    public static bool HasKqlPrefix(string? sourceKey) =>
        (sourceKey?.Trim() ?? "").StartsWith(SourceKeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The catalog key of a <c>kql:{queryKey}</c> source key; null when it is not one. Only custom
    /// <c>kpi:</c> catalog keys qualify (<see cref="KqlQueryKeys.IsCustomKpi"/>): a KPI must
    /// not run a built-in query, whose text is owned by another agent.
    /// </summary>
    public static string? QueryKeyOf(string? sourceKey)
    {
        if (!HasKqlPrefix(sourceKey)) return null;
        var key = sourceKey!.Trim()[SourceKeyPrefix.Length..].Trim();
        return KqlQueryKeys.IsCustomKpi(key) ? key : null;
    }

    public static string InvalidSourceKeyMessage(string sourceKey) =>
        $"SourceKey '{sourceKey}' must reference a custom catalog query: kql:{KqlQueryKeys.CustomKpiPrefix}{{name}} (lowercase [a-z0-9:._-]).";

    public static string ForeignAliasMessage(string alias, string profileKey) =>
        $"Alias '{alias}' is not a telemetry source of profile '{profileKey}'.";

    /// <summary>
    /// The <c>{{SOURCE:alias}}</c> aliases of the text that are not a telemetry source alias of any
    /// service of <paramref name="profile"/> (case-insensitive), distinct, in order of appearance. The
    /// telemetry source catalog is global, so without this a KPI of one company could read another's.
    /// </summary>
    public static IReadOnlyList<string> FindForeignAliases(string? kql, CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var own = (profile.Services ?? [])
            .Where(s => s is not null)
            .SelectMany(s => s.TelemetrySourceAliases ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return KqlSourceResolver.PlaceholderPattern.Matches(kql ?? "")
            .Select(m => m.Groups["alias"].Value)
            .Where(a => !own.Contains(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Functions and plugins that read outside the resource the query runs against. The only
    /// cross-resource call a KPI query may make is <c>app('{{SOURCE:alias}}')</c>, with a plain
    /// quoted placeholder as its whole operand; that exact form is removed before the search.
    /// </summary>
    public static readonly IReadOnlyList<string> CrossResourceFunctions =
    [
        "app", "workspace", "cluster", "database", "resource", "adx", "external_table", "externaldata",
        "http_request", "http_request_post", "sql_request", "cosmosdb_sql_request", "mysql_request",
        "postgresql_request", "azure_digital_twins_query_request",
    ];

    private static readonly Regex AllowedSourceCall = new(
        @"(?<![\w])app\s*\(\s*(?<q>['""])\{\{SOURCE:[A-Za-z0-9._-]{1,64}\}\}\k<q>\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CrossResourceCall = new(
        @"(?<![\w])(?<name>" + string.Join("|", CrossResourceFunctions.Select(Regex.Escape)) + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The cross-resource calls of the text (lowercase, distinct), other than <c>app('{{SOURCE:alias}}')</c>.
    /// String literals of every form ('', "", @'', @"", h'', h"", ```...```) and // comments are
    /// blanked first, so a call is found whatever its operand looks like and a word inside a string
    /// is never mistaken for a call.
    /// </summary>
    public static IReadOnlyList<string> FindCrossResourceCalls(string? kql)
    {
        var withoutAllowed = AllowedSourceCall.Replace(kql ?? "", " ");
        var code = StripLiteralsAndComments(withoutAllowed);
        return CrossResourceCall.Matches(code)
            .Select(m => m.Groups["name"].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string CrossResourceMessage(IReadOnlyList<string> calls) =>
        $"A KPI query may not call {string.Join(", ", calls.Select(c => c + "()"))}; reference telemetry only as app('{{{{SOURCE:alias}}}}') with an alias of the KPI's profile.";

    /// <summary>Blanks string literals (every Kusto form) and // comments, keeping the code between them.</summary>
    public static string StripLiteralsAndComments(string kql)
    {
        var sb = new System.Text.StringBuilder(kql.Length);
        var i = 0;
        while (i < kql.Length)
        {
            var c = kql[i];
            if (c == '/' && i + 1 < kql.Length && kql[i + 1] == '/')
            {
                while (i < kql.Length && kql[i] != '\n') i++;
                sb.Append(' ');
                continue;
            }
            if (c == '`' && string.CompareOrdinal(kql, i, "```", 0, 3) == 0)
            {
                var end = kql.IndexOf("```", i + 3, StringComparison.Ordinal);
                i = end < 0 ? kql.Length : end + 3;
                sb.Append(" \"\" ");
                continue;
            }
            if (c is '\'' or '"')
            {
                // Verbatim when an '@' precedes the quote (also h@'...'): a doubled quote escapes, '\' does not.
                var verbatim = i > 0 && kql[i - 1] == '@';
                var j = i + 1;
                while (j < kql.Length)
                {
                    if (!verbatim && kql[j] == '\\') { j += 2; continue; }
                    if (kql[j] == c)
                    {
                        if (verbatim && j + 1 < kql.Length && kql[j + 1] == c) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                i = Math.Min(j + 1, kql.Length);
                sb.Append(" \"\" ");
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    public static string AllowedTokensText =>
        string.Join(", ", AllowedTokens.Select(t => "{{" + t + "}}")) + " and {{SOURCE:alias}}";

    /// <summary>Every placeholder of the text that a <c>kql:</c> KPI may not use, in order of appearance (distinct).</summary>
    public static IReadOnlyList<string> FindDisallowedTokens(string? kql)
    {
        var text = kql ?? "";
        var disallowed = new List<string>();
        foreach (Match match in TokenPattern.Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (AllowedTokens.Contains(name, StringComparer.Ordinal)) continue;
            if (KqlSourceResolver.PlaceholderPattern.Match(match.Value) is { Success: true } source && source.Length == match.Length) continue;
            if (!disallowed.Contains(match.Value, StringComparer.Ordinal)) disallowed.Add(match.Value);
        }

        // "{{" without a matching "}}" (or spanning lines) is a placeholder nobody will ever fill.
        if (TokenPattern.Replace(text, "").Contains("{{", StringComparison.Ordinal) && !disallowed.Contains("{{"))
        {
            disallowed.Add("{{");
        }
        return disallowed;
    }

    /// <summary>
    /// Null when the rows respect the KPI output contract for the grain, otherwise a message naming
    /// the first problems. Domain grain is v1: <c>kind</c> "bucket" rows (<c>BucketStart</c>,
    /// numeric <c>TotalCount</c>, optional numeric <c>Value</c>, one per bucket, no <c>EntityKey</c>)
    /// and optional "sample" rows (numeric <c>Value</c>). Entity grain: only "bucket" rows, each with
    /// a non-empty <c>EntityKey</c> (max 256), one per entity and bucket, and optional numeric
    /// <c>P50</c>, <c>P95</c>, <c>Mean</c> and <c>FailedCount</c>. No rows is valid (no traffic).
    /// </summary>
    public static string? ValidateOutput(IReadOnlyList<TelemetryJourneyRow> rows, KpiGrain grain)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var errors = new List<string>();
        var seen = new HashSet<(DateTimeOffset, string)>();

        for (var i = 0; i < rows.Count && errors.Count < MaxReportedErrors; i++)
        {
            var row = rows[i];
            var at = $"row {i + 1}";
            var kind = row.GetString("kind").Trim();
            var isBucket = string.Equals(kind, "bucket", StringComparison.OrdinalIgnoreCase);
            var isSample = string.Equals(kind, "sample", StringComparison.OrdinalIgnoreCase);
            if (!isBucket && !isSample)
            {
                errors.Add($"{at}: column 'kind' must be \"bucket\" or \"sample\".");
                continue;
            }

            var bucketStart = ReadDateTime(row, "BucketStart");
            if (bucketStart is null)
            {
                errors.Add($"{at}: column 'BucketStart' must be a datetime.");
                continue;
            }

            if (isSample)
            {
                if (grain == KpiGrain.Entity) errors.Add($"{at}: an entity-grain KPI returns no \"sample\" rows.");
                else if (!IsNumber(row, "Value")) errors.Add($"{at}: a \"sample\" row needs a numeric 'Value'.");
                continue;
            }

            if (!IsNumber(row, "TotalCount")) errors.Add($"{at}: a \"bucket\" row needs a numeric 'TotalCount'.");
            if (!IsNullOrNumber(row, "Value")) errors.Add($"{at}: column 'Value' must be a number or null.");

            var entityKey = row.GetString("EntityKey").Trim();
            if (grain == KpiGrain.Entity)
            {
                if (entityKey.Length == 0 || entityKey.Length > MaxEntityKeyLength)
                    errors.Add($"{at}: an entity-grain \"bucket\" row needs an 'EntityKey' of 1-{MaxEntityKeyLength} characters.");
                foreach (var column in new[] { "P50", "P95", "Mean", "FailedCount" })
                {
                    if (!IsNullOrNumber(row, column)) errors.Add($"{at}: column '{column}' must be a number or null.");
                }
            }
            else if (entityKey.Length > 0)
            {
                errors.Add($"{at}: a domain-grain KPI must not return 'EntityKey' (set Grain = entity for per-entity series).");
                continue;
            }

            if (!seen.Add((bucketStart.Value, entityKey)))
            {
                errors.Add(grain == KpiGrain.Entity
                    ? $"{at}: more than one \"bucket\" row for entity '{entityKey}' at {bucketStart.Value:O}."
                    : $"{at}: more than one \"bucket\" row for {bucketStart.Value:O}.");
            }
        }

        return errors.Count == 0
            ? null
            : "the query output does not match the KPI output contract: " + string.Join(" ", errors);
    }

    private static bool IsMissing(TelemetryJourneyRow row, string column) =>
        row.Get(column) is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static bool IsNumber(TelemetryJourneyRow row, string column) =>
        !IsMissing(row, column) && row.GetDouble(column) is { } d && double.IsFinite(d);

    private static bool IsNullOrNumber(TelemetryJourneyRow row, string column) =>
        IsMissing(row, column) || IsNumber(row, column);

    private static DateTimeOffset? ReadDateTime(TelemetryJourneyRow row, string column)
    {
        var text = row.GetString(column);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}
