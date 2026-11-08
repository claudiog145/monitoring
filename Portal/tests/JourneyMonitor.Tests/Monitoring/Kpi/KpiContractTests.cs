using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

public class KpiContractTests
{
    [Fact]
    public void FloorToBucket_AlignsToUtcEpoch()
    {
        var value = DateTimeOffset.Parse("2026-11-04T14:37:12+02:00", System.Globalization.CultureInfo.InvariantCulture);

        var hour = KpiTime.FloorToBucket(value, TimeSpan.FromHours(1));
        var fiveMinutes = KpiTime.FloorToBucket(value, TimeSpan.FromMinutes(5));

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), hour);
        Assert.Equal(TimeSpan.Zero, hour.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 35, 0, TimeSpan.Zero), fiveMinutes);
        Assert.Equal(TimeSpan.Zero, fiveMinutes.Offset);
    }

    [Fact]
    public void FloorToBucket_RejectsNonPositiveBucket()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KpiTime.FloorToBucket(DateTimeOffset.UnixEpoch, TimeSpan.Zero));
    }

    [Fact]
    public void KpiPillarNames_MapToSpecDisplayNames()
    {
        Assert.Equal("Business Health", KpiPillarNames.ToDisplay(KpiPillar.Business));
        Assert.Equal("Application Health", KpiPillarNames.ToDisplay(KpiPillar.Application));
        Assert.Equal("Infrastructure Health", KpiPillarNames.ToDisplay(KpiPillar.Infrastructure));

        Assert.True(KpiPillarNames.TryParseDisplay("application health", out var application));
        Assert.Equal(KpiPillar.Application, application);

        Assert.True(KpiPillarNames.TryParseDisplay("Business", out var business));
        Assert.Equal(KpiPillar.Business, business);

        Assert.False(KpiPillarNames.TryParseDisplay("x", out _));
    }

    [Theory]
    [InlineData(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":100}")]
    [InlineData(DetectionMethodKind.StaticThreshold, KpiDirection.Decrease, "{\"threshold\":0.5}")]
    [InlineData(DetectionMethodKind.BaselinePercent, KpiDirection.Either, "{\"lookbackDays\":7,\"thresholdPercent\":50}")]
    [InlineData(DetectionMethodKind.RobustZScore, KpiDirection.Increase, "{\"windowBuckets\":24,\"zThreshold\":3}")]
    public void KpiDefinition_Validate_ValidRangesHaveNoErrors(DetectionMethodKind method, KpiDirection direction, string paramsJson)
    {
        var definition = Valid() with { DetectionMethod = method, Direction = direction, MethodParamsJson = paramsJson };

        Assert.Empty(definition.Validate());
    }

    [Fact]
    public void KpiDefinition_Validate_RejectsStaticThresholdWithEither()
    {
        var definition = Valid() with
        {
            DetectionMethod = DetectionMethodKind.StaticThreshold,
            Direction = KpiDirection.Either,
            MethodParamsJson = "{\"threshold\":100}",
        };

        var error = Assert.Single(definition.Validate());
        Assert.Contains("Either", error);
    }

    [Fact]
    public void KpiDefinition_Validate_RejectsInvalidJson()
    {
        var definition = Valid() with { MethodParamsJson = "{not json" };

        var error = Assert.Single(definition.Validate());
        Assert.Contains("valid JSON", error);
    }

    [Fact]
    public void KpiDefinition_Validate_RejectsBaselinePercentWithoutLookbackDays()
    {
        var definition = Valid() with
        {
            DetectionMethod = DetectionMethodKind.BaselinePercent,
            MethodParamsJson = "{\"thresholdPercent\":50}",
        };

        var error = Assert.Single(definition.Validate());
        Assert.Contains("lookbackDays", error);
    }

    [Fact]
    public void KpiDefinition_Validate_ReportsEveryOutOfRangeField()
    {
        var definition = Valid() with
        {
            Kpi = "",
            Domain = "",
            SourceKey = " ",
            EvaluationFrequency = TimeSpan.FromSeconds(30),
            BucketSize = TimeSpan.FromSeconds(30),
            LookbackWindow = TimeSpan.FromSeconds(10),
            SampleSize = 100_001,
            MinObservationsPerBucket = -1,
            ConsecutiveCount = 49,
            CooldownMinutes = 10_081,
        };

        Assert.Equal(10, definition.Validate().Count);
        Assert.Equal(11, (definition with { ProfileKey = "" }).Validate().Count);
    }

    [Fact]
    public void KpiDefinition_ProfileKey_ContractV3()
    {
        var definition = Valid();

        Assert.Equal(JourneyMonitor.Core.Company.KpiProfiles.Default, definition.ProfileKey);
        Assert.Equal("acme", (definition with { ProfileKey = "acme" }).ProfileKey);
        Assert.Equal(["ProfileKey is required."], (definition with { ProfileKey = " " }).Validate());
        Assert.Equal("*", JourneyMonitor.Core.Company.KpiProfiles.All);
    }

    private static KpiDefinition Valid() => new(
        KpiDefinitionId: 0,
        Pillar: KpiPillar.Application,
        Kpi: "Latency",
        Domain: "hotels",
        Stage: "",
        SourceKey: "appinsights:latency",
        Direction: KpiDirection.Increase,
        Aggregation: KpiAggregation.P95,
        EvaluationFrequency: TimeSpan.FromMinutes(5),
        BucketSize: TimeSpan.FromHours(1),
        LookbackWindow: TimeSpan.FromDays(1),
        SampleSize: 4000,
        MinObservationsPerBucket: 30,
        DetectionMethod: DetectionMethodKind.BaselinePercent,
        MethodParamsJson: "{\"lookbackDays\":7,\"thresholdPercent\":50}",
        ConsecutiveCount: 2,
        CooldownMinutes: 120,
        Enabled: true,
        UpdatedAt: DateTimeOffset.UnixEpoch,
        UpdatedBy: "test");
}
