using System.Globalization;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi.Methods;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>Pure, deterministic implementation of the Specification 1000 §9 rule semantics.</summary>
public sealed class KpiEvaluator : IKpiEvaluator
{
    public TimeSpan GetRequiredHistory(KpiDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var parameters = MethodParameters.Parse(definition);
        if (parameters.Error is not null) return TimeSpan.Zero;
        return definition.DetectionMethod switch
        {
            DetectionMethodKind.BaselinePercent => TimeSpan.FromDays(parameters.LookbackDays),
            DetectionMethodKind.RobustZScore => TimeSpan.FromTicks(definition.BucketSize.Ticks * parameters.WindowBuckets),
            _ => TimeSpan.Zero,
        };
    }

    public IReadOnlyList<KpiEvaluation> Evaluate(
        KpiDefinition definition,
        KpiDetectionState state,
        IReadOnlyList<KpiBucket> history,
        IReadOnlyList<KpiBucket> bucketsToEvaluate)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        history ??= [];
        bucketsToEvaluate ??= [];

        var parameters = MethodParameters.Parse(definition);
        var ordered = bucketsToEvaluate.OrderBy(b => b.BucketStart).ToList();
        var results = new List<KpiEvaluation>(ordered.Count);
        var previous = new List<KpiBucket>(history);
        var current = state;

        foreach (var bucket in ordered)
        {
            var evaluation = EvaluateBucket(definition, parameters, current, history, previous, bucket);
            results.Add(evaluation);
            current = evaluation.StateAfter;
            previous.Add(bucket);
        }

        return results;
    }

    private static KpiEvaluation EvaluateBucket(
        KpiDefinition definition,
        MethodParameters parameters,
        KpiDetectionState state,
        IReadOnlyList<KpiBucket> history,
        IReadOnlyList<KpiBucket> previous,
        KpiBucket bucket)
    {
        var observed = KpiStatistics.Observed(bucket, definition.Aggregation);

        if (parameters.Error is not null)
            return NotEvaluated(bucket, state, observed, null, parameters.Error);

        if (bucket.TotalCount < definition.MinObservationsPerBucket)
            return NotEvaluated(bucket, state, observed, null,
                string.Create(CultureInfo.InvariantCulture, $"Insufficient observations: {bucket.TotalCount} < {definition.MinObservationsPerBucket}."));

        if (observed is not { } value)
            return NotEvaluated(bucket, state, null, null, $"No {definition.Aggregation} value in bucket.");

        var (reference, deviation, reason) = definition.DetectionMethod switch
        {
            DetectionMethodKind.StaticThreshold => StaticThresholdMethod.Compute(value, parameters.Threshold),
            DetectionMethodKind.BaselinePercent => BaselinePercentMethod.Compute(value, bucket.BucketStart, history, definition.Aggregation, parameters.LookbackDays),
            DetectionMethodKind.RobustZScore => RobustZScoreMethod.Compute(value, bucket.BucketStart, definition.BucketSize, previous, definition.Aggregation, parameters.WindowBuckets),
            _ => (null, null, $"Unsupported detection method {definition.DetectionMethod}."),
        };

        if (reason is not null || reference is not { } refValue || deviation is not { } dev)
            return NotEvaluated(bucket, state, value, reference, reason ?? "Reference could not be computed.");

        var anomalous = IsAnomalous(definition, parameters, value, dev);

        if (!anomalous)
        {
            var outcome = state.ConsecutiveCount > 0 ? KpiEvaluationOutcome.CounterReset : KpiEvaluationOutcome.NoChange;
            var after = state with { ConsecutiveCount = 0, LastBucketEvaluated = bucket.BucketStart };
            return new KpiEvaluation(bucket, KpiBucketVerdict.Normal, value, refValue, dev, after, outcome,
                $"Normal: {Describe(definition, value, refValue, dev)}.");
        }

        var count = state.ConsecutiveCount + 1;
        var bucketEnd = bucket.BucketStart + definition.BucketSize;
        var detection = KpiDetectionText.Format(definition, definition.ConsecutiveCount, value, refValue, dev);

        if (state.CooldownUntil is { } cooldownUntil && cooldownUntil > bucketEnd)
        {
            var suppressed = state with { ConsecutiveCount = count, LastBucketEvaluated = bucket.BucketStart };
            return new KpiEvaluation(bucket, KpiBucketVerdict.Anomalous, value, refValue, dev, suppressed,
                KpiEvaluationOutcome.SuppressedByCooldown,
                string.Create(CultureInfo.InvariantCulture, $"Suppressed by cooldown until {cooldownUntil:O}: {detection}"));
        }

        if (count >= definition.ConsecutiveCount)
        {
            var triggered = state with
            {
                ConsecutiveCount = count,
                LastBucketEvaluated = bucket.BucketStart,
                CooldownUntil = bucketEnd + TimeSpan.FromMinutes(definition.CooldownMinutes),
            };
            return new KpiEvaluation(bucket, KpiBucketVerdict.Anomalous, value, refValue, dev, triggered,
                KpiEvaluationOutcome.Triggered, detection);
        }

        var incremented = state with { ConsecutiveCount = count, LastBucketEvaluated = bucket.BucketStart };
        return new KpiEvaluation(bucket, KpiBucketVerdict.Anomalous, value, refValue, dev, incremented,
            KpiEvaluationOutcome.CounterIncremented,
            string.Create(CultureInfo.InvariantCulture, $"Anomalous ({count} of {definition.ConsecutiveCount}): {Describe(definition, value, refValue, dev)}."));
    }

    private static bool IsAnomalous(KpiDefinition definition, MethodParameters parameters, double observed, double deviation)
    {
        if (definition.DetectionMethod == DetectionMethodKind.StaticThreshold)
        {
            return definition.Direction switch
            {
                KpiDirection.Increase => observed > parameters.Threshold,
                KpiDirection.Decrease => observed < parameters.Threshold,
                _ => false,
            };
        }

        var t = definition.DetectionMethod == DetectionMethodKind.BaselinePercent ? parameters.ThresholdPercent : parameters.ZThreshold;
        return definition.Direction switch
        {
            KpiDirection.Increase => deviation > t,
            KpiDirection.Decrease => deviation < -t,
            KpiDirection.Either => Math.Abs(deviation) > t,
            _ => false,
        };
    }

    private static string Describe(KpiDefinition definition, double observed, double reference, double deviation)
    {
        var inv = CultureInfo.InvariantCulture;
        var dev = definition.DetectionMethod switch
        {
            DetectionMethodKind.BaselinePercent => deviation.ToString("+0.0;-0.0;+0.0", inv) + "%",
            DetectionMethodKind.RobustZScore => "z=" + deviation.ToString("+0.0;-0.0;+0.0", inv),
            _ => "delta " + deviation.ToString("+0.##;-0.##;+0", inv),
        };
        return $"{definition.Kpi} {observed.ToString("0.##", inv)} vs reference {reference.ToString("0.##", inv)} ({dev})";
    }

    private static KpiEvaluation NotEvaluated(KpiBucket bucket, KpiDetectionState state, double? observed, double? reference, string reason)
        => new(bucket, KpiBucketVerdict.NotEvaluated, observed, reference, null,
            state with { LastBucketEvaluated = bucket.BucketStart }, KpiEvaluationOutcome.Skipped, "Not evaluated: " + reason);

    private sealed record MethodParameters(double Threshold, int LookbackDays, double ThresholdPercent, int WindowBuckets, double ZThreshold, string? Error)
    {
        public static MethodParameters Parse(KpiDefinition definition)
        {
            if (definition.DetectionMethod == DetectionMethodKind.StaticThreshold && definition.Direction == KpiDirection.Either)
                return Invalid("StaticThreshold does not support Direction=Either.");

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(definition.MethodParamsJson) ? "{}" : definition.MethodParamsJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Invalid("MethodParamsJson must be a JSON object.");

                switch (definition.DetectionMethod)
                {
                    case DetectionMethodKind.StaticThreshold:
                        if (!root.TryGetProperty("threshold", out var t) || !t.TryGetDouble(out var threshold))
                            return Invalid("StaticThreshold requires {\"threshold\": number}.");
                        return new MethodParameters(threshold, 0, 0, 0, 0, null);

                    case DetectionMethodKind.BaselinePercent:
                        if (!root.TryGetProperty("lookbackDays", out var d) || !d.TryGetInt32(out var days) || days < 1)
                            return Invalid("BaselinePercent requires lookbackDays >= 1.");
                        if (!root.TryGetProperty("thresholdPercent", out var p) || !p.TryGetDouble(out var pct) || pct <= 0)
                            return Invalid("BaselinePercent requires thresholdPercent > 0.");
                        return new MethodParameters(0, days, pct, 0, 0, null);

                    case DetectionMethodKind.RobustZScore:
                        if (!root.TryGetProperty("windowBuckets", out var w) || !w.TryGetInt32(out var window) || window < 1)
                            return Invalid("RobustZScore requires windowBuckets >= 1.");
                        if (!root.TryGetProperty("zThreshold", out var z) || !z.TryGetDouble(out var zt) || zt <= 0)
                            return Invalid("RobustZScore requires zThreshold > 0.");
                        return new MethodParameters(0, 0, 0, window, zt, null);

                    default:
                        return Invalid($"Unsupported detection method {definition.DetectionMethod}.");
                }
            }
            catch (JsonException)
            {
                return Invalid("MethodParamsJson must be valid JSON.");
            }
        }

        private static MethodParameters Invalid(string error) => new(0, 0, 0, 0, 0, error);
    }
}
