namespace JourneyMonitor.Core.Analytics;

/// <summary>Raw counts from SQL. Null means that source could not be read.</summary>
public sealed record OverviewCounts(
    long? AvailabilityTotalCalls,
    long? AvailabilityFailedCalls,
    int? JourneyTotal,
    int? SuccessfulJourneys,
    int? SearchedJourneys,
    int? ConvertedJourneys,
    double? AverageDurationMs,
    int? OpenExceptions,
    int? TotalExceptions,
    int? CriticalIncidents,
    int? DoctorMonkeyDiagnoses);

public sealed record OverviewKpi(
    string Label,
    string Value,
    string Meta,
    string Tone,
    string Status);

/// <summary>
/// Turns stored counts into the seven Executive Overview tiles.
/// Platform Availability is always the last 24 hours of calls, across every domain.
/// The other tiles use the counts already filtered by the caller.
/// </summary>
public static class OverviewComposer
{
    public static IReadOnlyList<OverviewKpi> Tiles(OverviewCounts counts)
    {
        var availability = Availability(counts.AvailabilityTotalCalls, counts.AvailabilityFailedCalls);
        return
        [
            availability,
            Successful(counts.JourneyTotal, counts.SuccessfulJourneys),
            Conversion(counts.SearchedJourneys, counts.ConvertedJourneys),
            Duration(counts.AverageDurationMs),
            Critical(counts.CriticalIncidents),
            Exceptions(counts.OpenExceptions, counts.TotalExceptions),
            DoctorMonkeyDiagnosesTile(counts.DoctorMonkeyDiagnoses),
        ];
    }

    /// <summary>Worst tile status. Drives the header badge.</summary>
    public static string WorstStatus(IReadOnlyList<OverviewKpi> tiles)
    {
        if (tiles.Any(tile => tile.Status == "critical")) return "critical";
        if (tiles.Any(tile => tile.Status == "degraded")) return "degraded";
        if (tiles.All(tile => tile.Status == "unknown")) return "unknown";
        return "healthy";
    }

    private static OverviewKpi Availability(long? totalCalls, long? failedCalls)
    {
        if (totalCalls is null or <= 0 || failedCalls is null)
        {
            return Unknown("Platform Availability", "Rolling 24h average");
        }

        var percent = Math.Max(0, 100 - (double)failedCalls.Value / totalCalls.Value * 100);
        var status = percent < 95 ? "critical" : percent < 99.9 ? "degraded" : "healthy";
        return new OverviewKpi(
            "Platform Availability",
            $"{percent:0.00}%",
            "Rolling 24h average",
            status == "healthy" ? "up" : "down",
            status);
    }

    private static OverviewKpi Successful(int? total, int? successful)
    {
        if (total is null || successful is null || total.Value == 0)
        {
            return Unknown("Successful Journeys", "Completed / total in the selected window");
        }

        var failed = total.Value - successful.Value;
        return new OverviewKpi(
            "Successful Journeys",
            $"{successful.Value}/{total.Value}",
            $"{failed} not booked in window",
            failed > 0 ? "down" : "up",
            "healthy");
    }

    private static OverviewKpi Conversion(int? searched, int? converted)
    {
        if (searched is null || converted is null || searched.Value <= 0)
        {
            return Unknown("Booking Conversion", "Search-to-booking");
        }

        var percent = (double)converted.Value / searched.Value * 100;
        return new OverviewKpi(
            "Booking Conversion",
            $"{percent:0.0}%",
            "Search-to-booking",
            percent > 0 ? "up" : "down",
            "healthy");
    }

    private static OverviewKpi Duration(double? averageMs)
    {
        if (averageMs is null)
        {
            return Unknown("Avg. End-to-End Response", "Across the selected journeys");
        }

        return new OverviewKpi(
            "Avg. End-to-End Response",
            FormatDuration(averageMs.Value),
            "Across the selected journeys",
            "",
            "healthy");
    }

    private static OverviewKpi Critical(int? count)
    {
        if (count is null)
        {
            return Unknown("Active Critical Incidents", "Open incidents at Critical severity");
        }

        return new OverviewKpi(
            "Active Critical Incidents",
            count.Value.ToString(),
            "Open incidents at Critical severity",
            count.Value > 0 ? "down" : "up",
            count.Value > 0 ? "critical" : "healthy");
    }

    private static OverviewKpi Exceptions(int? open, int? total)
    {
        if (open is null || total is null)
        {
            return Unknown("Open Exceptions", "Open count, with total tracked");
        }

        return new OverviewKpi(
            "Open Exceptions",
            open.Value.ToString(),
            $"{total.Value} total tracked",
            open.Value > 0 ? "down" : "up",
            open.Value > 0 ? "degraded" : "healthy");
    }

    private static OverviewKpi DoctorMonkeyDiagnosesTile(int? count)
    {
        if (count is null)
        {
            return Unknown("Doctor Monkey Diagnoses", "Diagnoses in window");
        }

        return new OverviewKpi(
            "Doctor Monkey Diagnoses",
            count.Value.ToString(),
            "Diagnoses in window",
            "up",
            "healthy");
    }

    private static OverviewKpi Unknown(string label, string meta) =>
        new(label, "No data", meta, "", "unknown");

    private static string FormatDuration(double ms)
    {
        if (ms < 1000) return $"{Math.Round(ms)} ms";
        var seconds = ms / 1000;
        if (seconds < 60) return $"{seconds:0.0} s";
        var minutes = (int)(seconds / 60);
        var rem = (int)Math.Round(seconds % 60);
        return $"{minutes}m {rem}s";
    }
}
