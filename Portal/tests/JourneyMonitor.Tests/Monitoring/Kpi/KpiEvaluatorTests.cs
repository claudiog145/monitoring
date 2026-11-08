using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

public class KpiEvaluatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private readonly KpiEvaluator _evaluator = new();

    internal static KpiDefinition Definition(
        DetectionMethodKind method,
        KpiDirection direction,
        string paramsJson,
        KpiAggregation aggregation = KpiAggregation.P95,
        int consecutive = 2,
        int cooldownMinutes = 120,
        int minObservations = 0,
        string kpi = "Latency") =>
        new(
            KpiDefinitionId: 1,
            Pillar: KpiPillar.Application,
            Kpi: kpi,
            Domain: "hotels",
            Stage: "",
            SourceKey: "appinsights:latency",
            Direction: direction,
            Aggregation: aggregation,
            EvaluationFrequency: TimeSpan.FromMinutes(5),
            BucketSize: TimeSpan.FromHours(1),
            LookbackWindow: TimeSpan.FromDays(1),
            SampleSize: 0,
            MinObservationsPerBucket: minObservations,
            DetectionMethod: method,
            MethodParamsJson: paramsJson,
            ConsecutiveCount: consecutive,
            CooldownMinutes: cooldownMinutes,
            Enabled: true,
            UpdatedAt: Day,
            UpdatedBy: "test");

    private static KpiBucket P95(DateTimeOffset start, double p95, long total = 100) =>
        new(1, start, total, 0, null, p95, null, null, null);

    private static KpiBucket ValueBucket(DateTimeOffset start, double value) =>
        new(1, start, 100, 0, null, null, null, null, value);

    private static KpiDefinition Static3000() =>
        Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}");

    private static DateTimeOffset At(int hour) => Day.AddHours(hour);

    [Fact]
    public void IsolatedSpike_DoesNotTrigger()
    {
        var results = _evaluator.Evaluate(Static3000(), KpiDetectionState.Initial(1), [],
            [P95(At(10), 1000), P95(At(11), 3500), P95(At(12), 1000)]);

        Assert.Equal(
            [KpiEvaluationOutcome.NoChange, KpiEvaluationOutcome.CounterIncremented, KpiEvaluationOutcome.CounterReset],
            results.Select(r => r.Outcome));
        Assert.DoesNotContain(results, r => r.Outcome == KpiEvaluationOutcome.Triggered);
        Assert.Equal(0, results[^1].StateAfter.ConsecutiveCount);
        Assert.Null(results[^1].StateAfter.CooldownUntil);
    }

    [Fact]
    public void TwoConsecutiveAnomalies_Trigger()
    {
        var results = _evaluator.Evaluate(Static3000(), KpiDetectionState.Initial(1), [],
            [P95(At(10), 3500), P95(At(11), 3600)]);

        Assert.Equal([KpiEvaluationOutcome.CounterIncremented, KpiEvaluationOutcome.Triggered], results.Select(r => r.Outcome));
        Assert.Equal(KpiBucketVerdict.Anomalous, results[1].Verdict);
        Assert.Equal(2, results[1].StateAfter.ConsecutiveCount);
        Assert.Equal(At(12) + TimeSpan.FromMinutes(120), results[1].StateAfter.CooldownUntil);
        Assert.Equal(At(11), results[1].StateAfter.LastBucketEvaluated);
    }

    [Fact]
    public void Cooldown_SuppressesDuplicateTrigger()
    {
        var results = _evaluator.Evaluate(Static3000(), KpiDetectionState.Initial(1), [],
            [P95(At(10), 3500), P95(At(11), 3600), P95(At(12), 3700)]);

        Assert.Equal(KpiEvaluationOutcome.Triggered, results[1].Outcome);
        Assert.Equal(KpiEvaluationOutcome.SuppressedByCooldown, results[2].Outcome);
        Assert.Equal(KpiBucketVerdict.Anomalous, results[2].Verdict);
        Assert.Equal(3, results[2].StateAfter.ConsecutiveCount);
        Assert.Equal(At(14), results[2].StateAfter.CooldownUntil);
    }

    [Fact]
    public void OppositeDirection_IsIgnored()
    {
        var definition = Definition(DetectionMethodKind.BaselinePercent, KpiDirection.Increase, "{\"lookbackDays\":7,\"thresholdPercent\":50}");
        var current = At(10);
        var history = Enumerable.Range(1, 7).Select(k => P95(current.AddDays(-k), 1000)).ToList();

        var results = _evaluator.Evaluate(definition, KpiDetectionState.Initial(1), history, [P95(current, 400)]);

        var result = Assert.Single(results);
        Assert.Equal(KpiBucketVerdict.Normal, result.Verdict);
        Assert.Equal(KpiEvaluationOutcome.NoChange, result.Outcome);
        Assert.Equal(-60.0, result.Deviation!.Value, 6);
        Assert.Equal(0, result.StateAfter.ConsecutiveCount);
    }

    [Fact]
    public void InsufficientObservations_SkipsBucketWithoutChangingCounter()
    {
        var definition = Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}", minObservations: 30);
        var state = new KpiDetectionState(1, 1, At(9), null, null);

        var results = _evaluator.Evaluate(definition, state, [], [P95(At(10), 9999, total: 29)]);

        var result = Assert.Single(results);
        Assert.Equal(KpiBucketVerdict.NotEvaluated, result.Verdict);
        Assert.Equal(KpiEvaluationOutcome.Skipped, result.Outcome);
        Assert.Equal(1, result.StateAfter.ConsecutiveCount);
        Assert.Equal(At(10), result.StateAfter.LastBucketEvaluated);
        Assert.Null(result.StateAfter.CooldownUntil);
    }

    [Fact]
    public void AfternoonSimulation_MatchesPresentationTable()
    {
        var definition = Definition(DetectionMethodKind.BaselinePercent, KpiDirection.Increase,
            "{\"lookbackDays\":7,\"thresholdPercent\":50}", consecutive: 2, cooldownMinutes: 120, minObservations: 30);
        var table = new (int Hour, double Normal, double Observed, double Deviation, KpiEvaluationOutcome Outcome, int Counter)[]
        {
            (12, 1800, 1854, 3.0, KpiEvaluationOutcome.NoChange, 0),
            (13, 1820, 2950, 62.1, KpiEvaluationOutcome.CounterIncremented, 1),
            (14, 1880, 1974, 5.0, KpiEvaluationOutcome.CounterReset, 0),
            (15, 1900, 3230, 70.0, KpiEvaluationOutcome.CounterIncremented, 1),
            (16, 1910, 3534, 85.0, KpiEvaluationOutcome.Triggered, 2),
            (17, 1930, 3667, 90.0, KpiEvaluationOutcome.SuppressedByCooldown, 3),
            (18, 1900, 2660, 40.0, KpiEvaluationOutcome.CounterReset, 0),
        };
        var history = table
            .SelectMany(row => Enumerable.Range(1, 7).Select(k => P95(At(row.Hour).AddDays(-k), row.Normal, total: 5000)))
            .OrderBy(b => b.BucketStart)
            .ToList();
        var buckets = table.Select(row => P95(At(row.Hour), row.Observed, total: 5000)).ToList();

        var results = _evaluator.Evaluate(definition, KpiDetectionState.Initial(1), history, buckets);

        Assert.Equal(table.Length, results.Count);
        for (var i = 0; i < table.Length; i++)
        {
            var row = table[i];
            var result = results[i];
            Assert.Equal(At(row.Hour), result.Bucket.BucketStart);
            Assert.Equal(row.Normal, result.ReferenceValue);
            Assert.Equal(row.Observed, result.ObservedValue);
            Assert.Equal(row.Deviation, Math.Round(result.Deviation!.Value, 1));
            Assert.Equal(row.Outcome, result.Outcome);
            Assert.Equal(row.Counter, result.StateAfter.ConsecutiveCount);
        }

        var trigger = results[4];
        Assert.Equal(At(19), trigger.StateAfter.CooldownUntil);
        Assert.Equal("2 consecutive increases: Latency 3534 vs reference 1910 (+85.0%)",
            KpiDetectionText.Format(definition, trigger.StateAfter.ConsecutiveCount, trigger.ObservedValue!.Value, trigger.ReferenceValue!.Value, trigger.Deviation!.Value));
        Assert.Equal("2 consecutive increases: Latency 3534 vs reference 1910 (+85.0%)", trigger.Explanation);
    }

    [Fact]
    public void BaselinePercent_UsesMedianOfSameSlotPreviousDays()
    {
        var definition = Definition(DetectionMethodKind.BaselinePercent, KpiDirection.Increase, "{\"lookbackDays\":7,\"thresholdPercent\":50}");
        var current = At(15);
        double[] values = [100, 200, 300, 400, 10000];
        var history = values.Select((v, i) => P95(current.AddDays(-(i + 1)), v)).ToList();
        // Same day, other hours: must not be part of the baseline.
        history.Add(P95(current.AddHours(-1), 99999));
        history.Add(P95(current.AddDays(-1).AddHours(1), 99999));

        var result = Assert.Single(_evaluator.Evaluate(definition, KpiDetectionState.Initial(1), history, [P95(current, 330)]));

        Assert.Equal(300, result.ReferenceValue);
        Assert.Equal(10.0, result.Deviation!.Value, 6);
        Assert.Equal(KpiBucketVerdict.Normal, result.Verdict);
    }

    [Fact]
    public void BaselinePercent_InsufficientHistory_NotEvaluated()
    {
        var definition = Definition(DetectionMethodKind.BaselinePercent, KpiDirection.Increase, "{\"lookbackDays\":7,\"thresholdPercent\":50}");
        var current = At(15);
        var history = new List<KpiBucket> { P95(current.AddDays(-1), 1000), P95(current.AddDays(-2), 1000) };
        var state = new KpiDetectionState(1, 1, At(14), null, null);

        var result = Assert.Single(_evaluator.Evaluate(definition, state, history, [P95(current, 5000)]));

        Assert.Equal(KpiBucketVerdict.NotEvaluated, result.Verdict);
        Assert.Equal(KpiEvaluationOutcome.Skipped, result.Outcome);
        Assert.Equal(1, result.StateAfter.ConsecutiveCount);
        Assert.Equal(current, result.StateAfter.LastBucketEvaluated);
    }

    [Fact]
    public void RobustZScore_FlagsOutlierBeyondThreshold()
    {
        var definition = Definition(DetectionMethodKind.RobustZScore, KpiDirection.Increase, "{\"windowBuckets\":8,\"zThreshold\":3}",
            aggregation: KpiAggregation.Value, consecutive: 1);
        double[] window = [10, 11, 9, 10, 12, 10, 11, 9];
        var history = window.Select((v, i) => ValueBucket(At(2 + i), v)).ToList();

        Assert.Equal(TimeSpan.FromHours(8), _evaluator.GetRequiredHistory(definition));
        var result = Assert.Single(_evaluator.Evaluate(definition, KpiDetectionState.Initial(1), history, [ValueBucket(At(10), 30)]));

        Assert.Equal(KpiBucketVerdict.Anomalous, result.Verdict);
        Assert.Equal(KpiEvaluationOutcome.Triggered, result.Outcome);
        Assert.Equal(10, result.ReferenceValue);
        Assert.Equal(20 / 1.4826, result.Deviation!.Value, 6);
    }

    [Fact]
    public void RobustZScore_ZeroMad_NotEvaluated()
    {
        var definition = Definition(DetectionMethodKind.RobustZScore, KpiDirection.Either, "{\"windowBuckets\":8,\"zThreshold\":3}",
            aggregation: KpiAggregation.Value, consecutive: 1);
        var history = Enumerable.Range(0, 8).Select(i => ValueBucket(At(2 + i), 10)).ToList();

        var result = Assert.Single(_evaluator.Evaluate(definition, KpiDetectionState.Initial(1), history, [ValueBucket(At(10), 30)]));

        Assert.Equal(KpiBucketVerdict.NotEvaluated, result.Verdict);
        Assert.Equal(KpiEvaluationOutcome.Skipped, result.Outcome);
        Assert.Equal(0, result.StateAfter.ConsecutiveCount);
    }

    [Fact]
    public void StaticThreshold_Decrease_FlagsBelowThreshold()
    {
        var definition = Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Decrease, "{\"threshold\":100}", consecutive: 3);

        var below = Assert.Single(_evaluator.Evaluate(definition, KpiDetectionState.Initial(1), [], [P95(At(10), 80)]));
        var equal = Assert.Single(_evaluator.Evaluate(definition, KpiDetectionState.Initial(1), [], [P95(At(10), 100)]));

        Assert.Equal(KpiBucketVerdict.Anomalous, below.Verdict);
        Assert.Equal(KpiEvaluationOutcome.CounterIncremented, below.Outcome);
        Assert.Equal(100, below.ReferenceValue);
        Assert.Equal(-20, below.Deviation);
        Assert.Equal(KpiBucketVerdict.Normal, equal.Verdict);
        Assert.Equal(KpiEvaluationOutcome.NoChange, equal.Outcome);
    }

    [Fact]
    public void StaticThreshold_Either_IsRejected()
    {
        var definition = Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Either, "{\"threshold\":100}", consecutive: 1);
        var state = new KpiDetectionState(1, 1, At(9), null, null);

        var results = _evaluator.Evaluate(definition, state, [], [P95(At(10), 5), P95(At(11), 500), P95(At(12), 100)]);

        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(KpiBucketVerdict.NotEvaluated, r.Verdict);
            Assert.Equal(KpiEvaluationOutcome.Skipped, r.Outcome);
            Assert.Contains("Either", r.Explanation);
            Assert.Equal(1, r.StateAfter.ConsecutiveCount);
        });
        Assert.Equal(At(12), results[^1].StateAfter.LastBucketEvaluated);
    }

    [Fact]
    public void SustainedAnomaly_RetriggersAfterCooldownWithDefinitionConsecutiveCount()
    {
        var results = _evaluator.Evaluate(Static3000(), KpiDetectionState.Initial(1), [],
            [P95(At(10), 3500), P95(At(11), 3500), P95(At(12), 3500), P95(At(13), 3500)]);

        Assert.Equal(
            [KpiEvaluationOutcome.CounterIncremented, KpiEvaluationOutcome.Triggered, KpiEvaluationOutcome.SuppressedByCooldown, KpiEvaluationOutcome.Triggered],
            results.Select(r => r.Outcome));
        Assert.Equal(4, results[3].StateAfter.ConsecutiveCount);
        Assert.Equal(At(16), results[3].StateAfter.CooldownUntil);
        Assert.Equal("2 consecutive increases: Latency 3500 vs reference 3000 (threshold 3000)", results[1].Explanation);
        Assert.Equal("2 consecutive increases: Latency 3500 vs reference 3000 (threshold 3000)", results[3].Explanation);
    }

    [Fact]
    public void GetRequiredHistory_MatchesMethod()
    {
        Assert.Equal(TimeSpan.Zero, _evaluator.GetRequiredHistory(Static3000()));
        Assert.Equal(TimeSpan.FromDays(7), _evaluator.GetRequiredHistory(
            Definition(DetectionMethodKind.BaselinePercent, KpiDirection.Increase, "{\"lookbackDays\":7,\"thresholdPercent\":50}")));
        Assert.Equal(TimeSpan.FromHours(24), _evaluator.GetRequiredHistory(
            Definition(DetectionMethodKind.RobustZScore, KpiDirection.Increase, "{\"windowBuckets\":24,\"zThreshold\":3}")));
    }

    [Fact]
    public void RobustZScore_UsesBucketsEvaluatedEarlierInTheSameCall()
    {
        var definition = Definition(DetectionMethodKind.RobustZScore, KpiDirection.Increase, "{\"windowBuckets\":4,\"zThreshold\":3}",
            aggregation: KpiAggregation.Value, consecutive: 1);

        // No history at all: the first two buckets lack a window, the later ones use the earlier buckets of this call.
        var results = _evaluator.Evaluate(definition, KpiDetectionState.Initial(1), [],
            [ValueBucket(At(1), 10), ValueBucket(At(2), 12), ValueBucket(At(3), 11), ValueBucket(At(4), 50)]);

        Assert.Equal(KpiBucketVerdict.NotEvaluated, results[0].Verdict);
        Assert.Equal(KpiBucketVerdict.NotEvaluated, results[1].Verdict);
        Assert.Equal(KpiBucketVerdict.Normal, results[2].Verdict);
        Assert.Equal(KpiBucketVerdict.Anomalous, results[3].Verdict);
    }

    [Fact]
    public void DetectionText_FormatsEachMethodWithInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-AR");
            var z = Definition(DetectionMethodKind.RobustZScore, KpiDirection.Either, "{\"windowBuckets\":24,\"zThreshold\":3}", kpi: "Exceptions");
            var st = Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Decrease, "{\"threshold\":100.5}", kpi: "Bookings");

            Assert.Equal("3 consecutive deviations: Exceptions 42.5 vs reference 10.25 (z=+5.0)", KpiDetectionText.Format(z, 3, 42.5, 10.25, 4.98));
            Assert.Equal("2 consecutive decreases: Bookings 80 vs reference 100.5 (threshold 100.5)", KpiDetectionText.Format(st, 2, 80, 100.5, -20.5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
