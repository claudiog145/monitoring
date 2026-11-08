using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.AppInsights;

/// <summary>
/// <see cref="IKpiSource"/> for <c>kql:{queryKey}</c> (contract v4 §5): the query text comes from the
/// audited <c>monitoring.KqlQuery</c> catalog through <see cref="IKqlQueryStore"/>. Only the tokens of
/// <see cref="KqlKpiContract.AllowedTokens"/> are filled (with the same validated formatting as the
/// built-in KPI templates) and <c>{{SOURCE:alias}}</c> is resolved by <see cref="IKqlSourceResolver"/>.
/// The query runs against the target app of the definition's service, resolved exactly as for the
/// App Insights KPIs (<see cref="AppInsightsKpiSource.ResolveQueryTargetAsync"/>: first alias → catalog,
/// <see cref="InsightsOptions"/> fallback for the incumbent profile only), and its rows must respect
/// <see cref="KqlKpiContract.ValidateOutput"/>. On every run the text may only reach telemetry as
/// <c>app('{{SOURCE:alias}}')</c> (<see cref="KqlKpiContract.FindCrossResourceCalls"/>) with aliases of the
/// KPI's own profile (<see cref="KqlKpiContract.FindForeignAliases"/>) that the catalog does not give to
/// another profile (<see cref="AppInsightsKpiSource.EnsureAliasesReadableAsync"/>). Every failure is <see cref="KpiSourceUnavailableException"/>.
/// <see cref="FetchWithTextAsync"/> runs a candidate text, for the dry run of a catalog edit.
/// </summary>
public sealed class CatalogKqlKpiSource(
    ITelemetrySource telemetry,
    IKqlQueryStore queries,
    ICompanyProfileStore profiles,
    IKqlSourceResolver sourceResolver,
    IOptions<JourneySyncOptions> sync,
    AppInsightsKpiSource targetResolver) : IKqlKpiTextSource
{
    public bool CanHandle(string sourceKey) => KqlKpiContract.IsKqlSourceKey(sourceKey);

    public Task<KpiSourceResult> FetchAsync(KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default) =>
        RunAsync(definition, null, fromInclusive, toExclusive, samplesPerBucket, ct);

    public Task<KpiSourceResult> FetchWithTextAsync(KpiDefinition definition, string kqlText, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kqlText);
        return RunAsync(definition, kqlText, fromInclusive, toExclusive, samplesPerBucket, ct);
    }

    private async Task<KpiSourceResult> RunAsync(KpiDefinition definition, string? candidateText, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var queryKey = KqlKpiContract.QueryKeyOf(definition.SourceKey)
            ?? throw new ArgumentException($"CatalogKqlKpiSource does not handle source key '{definition.SourceKey}'.", nameof(definition));
        var label = $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]";

        var profile = await profiles.GetAsync(definition.ProfileKey, ct)
            ?? throw new KpiSourceUnavailableException($"{label}: no enabled company profile is stored under that key.");
        var service = profile.FindService(definition.Domain)
            ?? throw new KpiSourceUnavailableException($"{label}: domain is not a service of the profile");

        var text = candidateText ?? (await LoadQueryAsync(queryKey, label, ct)).KqlText;
        var disallowed = KqlKpiContract.FindDisallowedTokens(text);
        if (disallowed.Count > 0)
        {
            throw new KpiSourceUnavailableException(
                $"{label}: query '{queryKey}' uses tokens that a KPI query may not use: {string.Join(", ", disallowed)}. " +
                $"Allowed: {KqlKpiContract.AllowedTokensText}.");
        }

        // Checked on every run, not only on save: the text may come from a restore or a direct edit.
        var calls = KqlKpiContract.FindCrossResourceCalls(text);
        if (calls.Count > 0)
        {
            throw new KpiSourceUnavailableException($"{label}: query '{queryKey}': {KqlKpiContract.CrossResourceMessage(calls)}");
        }
        var foreign = KqlKpiContract.FindForeignAliases(text, profile);
        if (foreign.Count > 0)
        {
            throw new KpiSourceUnavailableException(
                $"{label}: query '{queryKey}': " + string.Join(" ", foreign.Select(a => KqlKpiContract.ForeignAliasMessage(a, profile.ProfileKey))));
        }
        // FX1: declared by the profile is not enough - a transfer may have given the alias to another profile since.
        await targetResolver.EnsureAliasesReadableAsync(
            definition, KqlSourceResolver.PlaceholderPattern.Matches(text).Select(m => m.Groups["alias"].Value), ct);

        string kql;
        try
        {
            kql = AppInsightsKpiSource.BuildKql(text, fromInclusive, toExclusive, definition.BucketSize, samplesPerBucket,
                definition.Stage, definition.Grain);
            kql = await sourceResolver.ResolveAsync(kql, sync.Value.Environment, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KqlSourceResolutionException)
        {
            throw new KpiSourceUnavailableException($"{label}: query '{queryKey}' cannot be rendered: {ex.Message}");
        }

        var target = await targetResolver.ResolveQueryTargetAsync(definition, service, ct);
        var rows = await telemetry.QueryAsync(new TelemetryQueryRequest(kql, target.AppId, target.TenantId), ct);

        var outputError = KqlKpiContract.ValidateOutput(rows, definition.Grain);
        if (outputError is not null)
        {
            throw new KpiSourceUnavailableException($"{label}: query '{queryKey}': {outputError}");
        }
        return AppInsightsKpiSource.MapRows(definition, rows);
    }

    private async Task<KqlQueryEntry> LoadQueryAsync(string queryKey, string label, CancellationToken ct)
    {
        KqlQueryEntry? entry;
        try
        {
            entry = await queries.GetAsync(queryKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new KpiSourceUnavailableException($"{label}: the KQL query catalog could not be read: {ex.Message}");
        }

        if (entry is null)
        {
            throw new KpiSourceUnavailableException($"{label}: query '{queryKey}' does not exist in the KQL query catalog.");
        }
        if (!entry.Enabled)
        {
            throw new KpiSourceUnavailableException($"{label}: query '{queryKey}' is disabled in the KQL query catalog.");
        }
        if (string.IsNullOrWhiteSpace(entry.KqlText))
        {
            throw new KpiSourceUnavailableException($"{label}: query '{queryKey}' has no text.");
        }
        return entry;
    }
}
