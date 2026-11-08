using System.Globalization;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.AppInsights;

namespace JourneyMonitor.Infrastructure.ProfileAnalytics;

/// <summary>
/// Contract v6 §4.4: renders <see cref="Template"/> for one service with <see cref="CompanyKqlTemplater"/>
/// (the profile's own first-match-wins step cascade), resolves <c>{{SOURCE:alias}}</c> through the
/// telemetry source catalog and runs it against the service's primary source. Only aliases the profile
/// owns (or shared ones) are read, and the incumbent's <c>InsightsOptions</c> are never a fallback.
/// </summary>
public sealed class AppInsightsProfileDashboardTelemetry(
    ITelemetrySource telemetry,
    IKqlSourceResolver sourceResolver,
    TelemetrySourceOwnershipCheck ownership) : IProfileDashboardTelemetry
{
    /// <summary>
    /// An exception inherits the step of the classified request with the same operation in the same
    /// source, the way the KPI exceptions template attributes it. Browser ErrorEvent noise is dropped.
    /// </summary>
    public const string Template = """
        let historyStart = {{from}};
        let tEnd = {{to}};
        let binSize = {{bin}};
        let serviceKey = {{service}};
        let reqs = materialize(
            union
        {{SOURCE_UNION}}
            | where timestamp >= historyStart and timestamp < tEnd
            | extend Host = tolower(tostring(parse_url(url).Host))
            | where {{HOST_FILTER}}
        {{SERVICE_AND_STEP_CLASSIFIER}}
            | where ServiceKey == serviceKey and isnotempty(StepKey)
            | project timestamp, Ms = todouble(duration), operation_Id, Source, StepKey);
        let latency =
            reqs
            | summarize Value = percentile(Ms, 95) by BinStart = bin_at(timestamp, binSize, datetime(1970-01-01)), Series = StepKey
            | extend Chart = "step_latency_p95";
        let requestMap =
            reqs
            | where isnotempty(operation_Id)
            | extend Ref = strcat(Source, ":", operation_Id)
            | summarize arg_max(timestamp, StepKey) by Ref
            | project Ref, RequestStep = StepKey;
        let exceptionsByStep =
            union
        {{EXCEPTION_SOURCE_UNION}}
            | where timestamp >= historyStart and timestamp < tEnd
            | where type !startswith "ErrorEvent"
            | where isnotempty(operation_Id)
            | extend Ref = strcat(Source, ":", operation_Id)
            | join kind=inner requestMap on Ref
            | summarize Value = todouble(sum(itemCount)) by BinStart = bin_at(timestamp, binSize, datetime(1970-01-01)), Series = RequestStep
            | extend Chart = "exceptions_by_step";
        union latency, exceptionsByStep
        | project Chart, Series, BinStart, Value
        """;

    public async Task<IReadOnlyList<ProfileStepMetric>> QueryServiceAsync(
        CompanyProfile profile,
        ServiceDefinition service,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        TimeSpan binSize,
        string environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(service);

        var aliases = (service.TelemetrySourceAliases ?? [])
            .Select(a => a?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .ToList();
        var primary = aliases.FirstOrDefault()
            ?? throw new InvalidOperationException(AppInsightsKpiSource.NoSourceConfiguredMessage(service.Key, profile.ProfileKey));

        var foreign = await ownership.FindForeignAsync(profile.ProfileKey, aliases, cancellationToken);
        if (foreign.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", foreign.Select(f => f.Message)));
        }

        var normalizedEnvironment = InsightsEnvironment.Normalize(environment);
        var catalog = await ownership.GetCatalogAsync(cancellationToken);
        var entry = catalog.FirstOrDefault(s =>
            string.Equals(s.Alias?.Trim(), primary, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Environment?.Trim(), normalizedEnvironment, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.AppId))
        {
            throw new InvalidOperationException(AppInsightsKpiSource.NoSourceConfiguredMessage(service.Key, profile.ProfileKey));
        }

        var kql = RenderKql(profile, service.Key, fromInclusive, toExclusive, binSize);
        kql = await sourceResolver.ResolveAsync(kql, normalizedEnvironment, cancellationToken);
        var rows = await telemetry.QueryAsync(
            new TelemetryQueryRequest(kql, entry.AppId.Trim(), string.IsNullOrWhiteSpace(entry.TenantId) ? null : entry.TenantId.Trim()),
            cancellationToken);
        return rows.Select(MapRow).Where(m => m is not null).Select(m => m!).ToList();
    }

    /// <summary>The query for one service, with only <c>{{SOURCE:alias}}</c> left for the source resolver.</summary>
    public static string RenderKql(CompanyProfile profile, string serviceKey, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, TimeSpan binSize)
    {
        var rendered = CompanyKqlTemplater.RenderForService(Template, profile, serviceKey)
            .Replace("{{from}}", AppInsightsKpiSource.FormatDateTime(fromInclusive, nameof(fromInclusive)), StringComparison.Ordinal)
            .Replace("{{to}}", AppInsightsKpiSource.FormatDateTime(toExclusive, nameof(toExclusive)), StringComparison.Ordinal)
            .Replace("{{bin}}", AppInsightsKpiSource.FormatTimespan(binSize), StringComparison.Ordinal)
            .Replace("{{service}}", CompanyKqlTemplater.Literal(serviceKey.Trim()), StringComparison.Ordinal);
        KqlPlaceholders.AssertNoneLeft(rendered, $"dashboard metrics {serviceKey} [{profile.ProfileKey}]");
        return rendered;
    }

    public static ProfileStepMetric? MapRow(TelemetryJourneyRow row)
    {
        var chart = row.GetString("Chart");
        var series = row.GetString("Series");
        var value = row.GetDouble("Value");
        var bin = ParseBin(row.Get("BinStart"));
        if (string.IsNullOrWhiteSpace(chart) || string.IsNullOrWhiteSpace(series) || value is null || bin is null
            || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return new ProfileStepMetric(chart, series, bin.Value, Math.Round(Convert.ToDecimal(value.Value), 2));
    }

    private static DateTimeOffset? ParseBin(JsonElement? element)
    {
        if (element is null) return null;
        var el = element.Value;
        if (el.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms) && ms > 10_000_000_000)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        return null;
    }
}
