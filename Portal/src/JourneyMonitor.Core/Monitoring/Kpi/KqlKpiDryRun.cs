using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>
/// Save-time check of a <c>kql:{queryKey}</c> KPI (contract v4 §5): runs the definition once
/// through the <see cref="IKpiSource"/> that handles it, over the last hour and with a bounded
/// timeout. The source enforces that the catalog query exists, uses only the allowed tokens and
/// returns the output contract, so a successful fetch is the whole validation - except that a fetch
/// with no rows proves nothing about the contract and is rejected too.
/// </summary>
public sealed class KqlKpiDryRun(IEnumerable<IKpiSource> sources, TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private const int MaxSamplesPerBucket = 10;
    private const int MaxMessageLength = 600;

    public const string NoRowsMessage =
        "Dry run returned no rows; cannot validate the output contract — widen the window or check the query.";

    private readonly IReadOnlyList<IKpiSource> _sources = sources.ToList();

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Null when the definition is not a <c>kql:</c> KPI or the dry run succeeded; otherwise the 400 message.
    /// With <paramref name="kqlText"/>, that candidate text is run instead of the stored query (an edit
    /// of the catalog query, checked before it is saved).
    /// </summary>
    public async Task<string?> ValidateAsync(KpiDefinition definition, CancellationToken ct = default, string? kqlText = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!KqlKpiContract.IsKqlSourceKey(definition.SourceKey))
        {
            return KqlKpiContract.HasKqlPrefix(definition.SourceKey)
                ? KqlKpiContract.InvalidSourceKeyMessage(definition.SourceKey)
                : null;
        }

        var label = $"SourceKey '{definition.SourceKey}'";
        var source = _sources.FirstOrDefault(s => s.CanHandle(definition.SourceKey));
        if (source is null) return $"{label}: no KPI source handles kql: queries on this host.";
        if (kqlText is not null && source is not IKqlKpiTextSource)
        {
            return $"{label}: the KPI source cannot dry-run an unsaved query text on this host.";
        }

        var now = time.GetUtcNow();
        var to = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        var from = to - Window;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var perBucket = Math.Clamp(definition.SampleSize, 0, MaxSamplesPerBucket);
            var result = kqlText is null
                ? await source.FetchAsync(definition, from, to, perBucket, timeout.Token).ConfigureAwait(false)
                : await ((IKqlKpiTextSource)source).FetchWithTextAsync(definition, kqlText, from, to, perBucket, timeout.Token).ConfigureAwait(false);
            return result.Buckets.Count == 0 && result.Samples.Count == 0 ? $"{label}: {NoRowsMessage}" : null;
        }
        catch (KpiSourceUnavailableException ex)
        {
            return Trim($"{label}: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"{label}: the one-hour dry run did not finish within {Timeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Trim($"{label}: the one-hour dry run failed: {ex.Message}");
        }
    }

    private static string Trim(string message) =>
        message.Length <= MaxMessageLength ? message : message[..MaxMessageLength] + "…";
}
