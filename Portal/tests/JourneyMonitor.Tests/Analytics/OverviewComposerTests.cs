using JourneyMonitor.Core.Analytics;

namespace JourneyMonitor.Tests.Analytics;

public class OverviewComposerTests
{
    [Fact]
    public void Availability_with_no_calls_is_no_data()
    {
        var tile = Tile("Platform Availability", new OverviewCounts(0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0));

        Assert.Equal("No data", tile.Value);
        Assert.Equal("unknown", tile.Status);
    }

    [Fact]
    public void Availability_below_95_is_critical()
    {
        var tile = Tile("Platform Availability", Counts(totalCalls: 100, failedCalls: 10));

        Assert.Equal("90.00%", tile.Value);
        Assert.Equal("critical", tile.Status);
        Assert.Equal("down", tile.Tone);
    }

    [Fact]
    public void Availability_between_95_and_99_9_is_degraded()
    {
        var tile = Tile("Platform Availability", Counts(totalCalls: 1000, failedCalls: 5));

        Assert.Equal("99.50%", tile.Value);
        Assert.Equal("degraded", tile.Status);
    }

    [Fact]
    public void Zero_journeys_is_no_data_not_zero()
    {
        var successful = Tile("Successful Journeys", Counts());
        var conversion = Tile("Booking Conversion", Counts());

        Assert.Equal("No data", successful.Value);
        Assert.Equal("No data", conversion.Value);
    }

    [Fact]
    public void Successful_and_conversion_use_the_filtered_counts()
    {
        var counts = Counts(journeys: 4, successful: 2, searched: 4, converted: 1, averageMs: 1500);
        var successful = Tile("Successful Journeys", counts);
        var conversion = Tile("Booking Conversion", counts);
        var duration = Tile("Avg. End-to-End Response", counts);

        Assert.Equal("2/4", successful.Value);
        Assert.Equal("25.0%", conversion.Value);
        Assert.Equal("1.5 s", duration.Value);
    }

    [Fact]
    public void Critical_incidents_and_open_exceptions_set_the_badge()
    {
        var critical = OverviewComposer.Tiles(Counts(critical: 2, open: 10, total: 12));
        Assert.Equal("critical", OverviewComposer.WorstStatus(critical));

        var degraded = OverviewComposer.Tiles(Counts(open: 3, total: 3));
        Assert.Equal("degraded", OverviewComposer.WorstStatus(degraded));
    }

    [Fact]
    public void Missing_doctor_report_table_is_no_data_and_an_empty_table_is_zero()
    {
        Assert.Equal("No data", Tile("Doctor Monkey Diagnoses", Counts(diagnoses: null)).Value);
        Assert.Equal("0", Tile("Doctor Monkey Diagnoses", Counts(diagnoses: 0)).Value);
    }

    private static OverviewKpi Tile(string label, OverviewCounts counts) =>
        OverviewComposer.Tiles(counts).Single(tile => tile.Label == label);

    private static OverviewCounts Counts(
        long totalCalls = 100,
        long failedCalls = 0,
        int journeys = 0,
        int successful = 0,
        int searched = 0,
        int converted = 0,
        double? averageMs = null,
        int open = 0,
        int total = 0,
        int critical = 0,
        int? diagnoses = 0) =>
        new(totalCalls, failedCalls, journeys, successful, searched, converted, averageMs, open, total, critical, diagnoses);
}
