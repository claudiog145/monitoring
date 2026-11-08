using Dapper;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Fabric;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.Business;

/// <summary>
/// Live read of <c>dbo.vw_Alert_HotelBookingKPIs</c> through <see cref="HotelBookingKpiConnection"/>.
/// Set <c>RUN_FABRIC_INTEGRATION_TESTS=true</c> to run these tests. Opening the connection uses the Fabric service principal from Key Vault.
/// </summary>
public class HotelBookingKpiQueryTests(ITestOutputHelper output)
{
    [Fact]
    public void AlertStatusSqlComputesStatusesFromTheViewBaselines()
    {
        var sql = Collapse(HotelBookingKpiConnection.AlertStatusSql);
        Assert.Contains("SELECT", sql);
        Assert.DoesNotContain("SELECT *", sql);
        Assert.DoesNotContain("TOP 10", sql);
        Assert.DoesNotContain("CancelRateStatus", sql);
        Assert.DoesNotContain("CROSS APPLY", sql);
        Assert.DoesNotContain("NO_CURRENT_DATA", sql);
        Assert.DoesNotContain("INSUFFICIENT_HISTORY", sql);
        Assert.DoesNotContain("INDETERMINATE", sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.GrossBookingsMedian4wk >= cfg.MinOutageVol AND k.GrossBookingsCurrent <= cfg.OutagePct * k.GrossBookingsMedian4wk THEN 'OUTAGE' WHEN k.GrossBookingsMedian4wk < cfg.MinVol THEN 'LOW_VOLUME' WHEN k.GrossBookingsCurrent <= cfg.RedPct * k.GrossBookingsMedian4wk AND k.GrossBookingsCurrent < k.GrossBookingsMin4wk AND k.GrossBookingsCurrent <= cfg.SuddenPct * k.GrossBookingsW1 THEN 'RED' WHEN k.GrossBookingsCurrent <= cfg.AmberPct * k.GrossBookingsMedian4wk THEN 'AMBER' ELSE 'OK' END AS GrossBookingsStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.CancelBookingsMedian4wk < cfg.MinCancelVol THEN 'LOW_VOLUME' WHEN k.CancelBookingsCurrent >= 2 * k.CancelBookingsMedian4wk AND k.CancelBookingsCurrent > k.CancelBookingsMax4wk AND k.CancelBookingsCurrent >= 1.5 * k.CancelBookingsW1 THEN 'RED' WHEN k.CancelBookingsCurrent >= 1.5 * k.CancelBookingsMedian4wk THEN 'AMBER' ELSE 'OK' END AS CancelBookingsStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.NetBookingsMedian4wk >= cfg.MinOutageVol AND k.NetBookingsCurrent <= cfg.OutagePct * k.NetBookingsMedian4wk THEN 'OUTAGE' WHEN k.NetBookingsMedian4wk < cfg.MinVol THEN 'LOW_VOLUME' WHEN k.NetBookingsCurrent <= cfg.RedPct * k.NetBookingsMedian4wk AND k.NetBookingsCurrent < k.NetBookingsMin4wk AND k.NetBookingsCurrent <= cfg.SuddenPct * k.NetBookingsW1 THEN 'RED' WHEN k.NetBookingsCurrent <= cfg.AmberPct * k.NetBookingsMedian4wk THEN 'AMBER' ELSE 'OK' END AS NetBookingsStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.GrossBookingsMedian4wk < cfg.MinVol OR k.GrossSalesMedian4wk <= 0 THEN 'LOW_VOLUME' WHEN k.GrossSalesCurrent <= cfg.RedPct * k.GrossSalesMedian4wk AND k.GrossSalesCurrent < k.GrossSalesMin4wk AND k.GrossSalesCurrent <= cfg.SuddenPct * k.GrossSalesW1 THEN 'RED' WHEN k.GrossSalesCurrent <= cfg.AmberPct * k.GrossSalesMedian4wk THEN 'AMBER' ELSE 'OK' END AS GrossSalesStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.NetBookingsMedian4wk < cfg.MinVol OR k.NetSalesMedian4wk <= 0 THEN 'LOW_VOLUME' WHEN k.NetSalesCurrent <= cfg.RedPct * k.NetSalesMedian4wk AND k.NetSalesCurrent < k.NetSalesMin4wk AND k.NetSalesCurrent <= cfg.SuddenPct * k.NetSalesW1 THEN 'RED' WHEN k.NetSalesCurrent <= cfg.AmberPct * k.NetSalesMedian4wk THEN 'AMBER' ELSE 'OK' END AS NetSalesStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.GrossBookingsMedian4wk < cfg.MinVol OR k.MarginMedian4wk <= 0 THEN 'LOW_VOLUME' WHEN k.MarginCurrent <= cfg.RedPct * k.MarginMedian4wk AND k.MarginCurrent < k.MarginMin4wk AND k.MarginCurrent <= cfg.SuddenPct * k.MarginW1 THEN 'RED' WHEN k.MarginCurrent <= cfg.AmberPct * k.MarginMedian4wk THEN 'AMBER' ELSE 'OK' END AS MarginStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.GrossBookingsMedian4wk < cfg.MinVol OR k.MarginPctCurrent IS NULL OR k.MarginPct4wk IS NULL THEN 'LOW_VOLUME' WHEN k.MarginPctCurrent <= k.MarginPct4wk - cfg.MarginPtsRed THEN 'RED' WHEN k.MarginPctCurrent <= k.MarginPct4wk - cfg.MarginPtsAmber THEN 'AMBER' ELSE 'OK' END AS MarginRateStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.NegMarginBookingsCurrent < cfg.MinNegVol THEN 'OK' WHEN k.NegMarginBookingsCurrent >= 2 * k.NegMarginBookingsMedian4wk AND k.NegMarginBookingsCurrent > k.NegMarginBookingsMax4wk AND k.NegMarginBookingsCurrent >= 1.5 * k.NegMarginBookingsW1 THEN 'RED' WHEN k.NegMarginBookingsCurrent >= 1.5 * k.NegMarginBookingsMedian4wk THEN 'AMBER' ELSE 'OK' END AS NegMarginStatus",
            sql);
        Assert.Contains(
            "CASE WHEN k.FreshnessLagMin > 10 THEN 'STALE' WHEN k.DeepNegMarginBookingsCurrent >= cfg.MinDeepNeg THEN 'RED' ELSE 'OK' END AS DeepNegMarginStatus",
            sql);
        Assert.Contains(
            "CASE WHEN (k.GrossBookingsMedian4wk - k.GrossBookingsCurrent) >= (k.CancelBookingsCurrent - k.CancelBookingsMedian4wk) THEN 'GROSS_DROP' ELSE 'CANCEL_SPIKE' END AS NetBookingsDriverRaw",
            sql);
        Assert.Contains(
            "CASE WHEN (k.GrossSalesMedian4wk - k.GrossSalesCurrent) >= (k.CancelSalesCurrent - k.CancelSalesMedian4wk) THEN 'GROSS_DROP' ELSE 'CANCEL_SPIKE' END AS NetSalesDriverRaw",
            sql);
        Assert.Contains(
            "CASE WHEN k.MarginPctCurrent <= k.MarginPct4wk - cfg.MarginPtsAmber THEN 'MARGIN_RATE' ELSE 'VOLUME' END AS MarginDriverRaw",
            sql);
        Assert.Contains(
            "CASE WHEN k.GrossBookingsCurrent > cfg.AmberPct * k.GrossBookingsMedian4wk AND k.GrossSalesCurrent <= cfg.AmberPct * k.GrossSalesMedian4wk THEN 1 ELSE 0 END AS ValuePerBookingDropRaw",
            sql);
        Assert.Contains(
            "CASE WHEN k.MultiRoomNegMarginBookingsCurrent > 0 THEN 'MULTI_ROOM' ELSE 'OTHER' END AS DeepNegMarginDriverRaw",
            sql);
        Assert.Contains("CASE WHEN s.NetBookingsStatus IN ('OUTAGE','RED','AMBER') THEN s.NetBookingsDriverRaw END AS NetBookingsDriver", sql);
        Assert.Contains("CASE WHEN s.NetSalesStatus IN ('RED','AMBER') THEN s.NetSalesDriverRaw END AS NetSalesDriver", sql);
        Assert.Contains("CASE WHEN s.MarginStatus IN ('RED','AMBER') THEN s.MarginDriverRaw END AS MarginDriver", sql);
        Assert.Contains("CASE WHEN s.GrossSalesStatus IN ('RED','AMBER') THEN s.ValuePerBookingDropRaw END AS ValuePerBookingDrop", sql);
        Assert.Contains("CASE WHEN s.DeepNegMarginStatus = 'RED' THEN s.DeepNegMarginDriverRaw END AS DeepNegMarginDriver", sql);
        Assert.Contains("CROSS JOIN (VALUES (50, 10, 10, 5, 0.10, 0.50, 0.75, 0.75, 0.05, 0.10, 1))", sql);
        Assert.Contains("AS cfg (MinVol, MinOutageVol, MinCancelVol, MinNegVol, OutagePct, RedPct, AmberPct, SuddenPct, MarginPtsAmber, MarginPtsRed, MinDeepNeg)", sql);
        Assert.Contains("OPTION (LABEL = 'agentic_portal_hotel_kpi')", sql);
        Assert.Contains("FROM dbo.vw_Alert_HotelBookingKPIs AS k", sql);
    }

    [Fact]
    public void BusinessHealthSqlReadsTheHourPairForOneClubOrTheTotal()
    {
        var sql = Collapse(HotelBookingKpiConnection.BusinessHealthSql);
        Assert.Contains("NetBookingsCurrent, NetBookingsW1", sql);
        Assert.Contains("CancelBookingsCurrent, CancelBookingsW1", sql);
        Assert.Contains("NetSalesCurrent, NetSalesW1", sql);
        Assert.Contains("WHERE Grain = 'HOUR'", sql);
        Assert.Contains("(@VacationClubID IS NULL AND AlertLevel = 'TOTAL')", sql);
        Assert.Contains("(AlertLevel = 'CLUB' AND VacationClubID = @VacationClubID)", sql);
        Assert.Contains("OPTION (LABEL = 'agentic_portal_hotel_kpi')", sql);
        Assert.DoesNotContain("SELECT *", sql);
    }

    [Fact]
    public void AlertExamplesProduceExpectedStatusesAndRoutingHints()
    {
        Assert.Equal("STALE", EvaluateBookingDrop(stale: true, current: 10, median: 100, min4wk: 50, w1: 100));
        Assert.Equal("OUTAGE", EvaluateBookingDrop(stale: false, current: 10, median: 100, min4wk: 50, w1: 100));
        Assert.Equal("LOW_VOLUME", EvaluateBookingDrop(stale: false, current: 30, median: 40, min4wk: 20, w1: 40));
        Assert.Equal("RED", EvaluateBookingDrop(stale: false, current: 49, median: 100, min4wk: 50, w1: 100));
        Assert.Equal("AMBER", EvaluateBookingDrop(stale: false, current: 49, median: 100, min4wk: 50, w1: 40));
        Assert.Equal("AMBER", EvaluateBookingDrop(stale: false, current: 50, median: 100, min4wk: 50, w1: 100));
        Assert.Equal("AMBER", EvaluateBookingDrop(stale: false, current: 75, median: 100, min4wk: 50, w1: 100));
        Assert.Equal("OK", EvaluateBookingDrop(stale: false, current: 76, median: 100, min4wk: 50, w1: 100));

        Assert.Equal("LOW_VOLUME", EvaluateAmountDrop(stale: false, bookingMedian: 100, metricMedian: 0, current: 10, min4wk: 0, w1: 0));
        Assert.Equal("LOW_VOLUME", EvaluateAmountDrop(stale: false, bookingMedian: 40, metricMedian: 1000, current: 100, min4wk: 50, w1: 1000));
        Assert.Equal("RED", EvaluateCancelSpike(stale: false, current: 201, median: 100, max4wk: 200, w1: 100));
        Assert.Equal("AMBER", EvaluateCancelSpike(stale: false, current: 201, median: 100, max4wk: 200, w1: 200));
        Assert.Equal("AMBER", EvaluateCancelSpike(stale: false, current: 200, median: 100, max4wk: 200, w1: 100));
        Assert.Equal("AMBER", EvaluateCancelSpike(stale: false, current: 150, median: 100, max4wk: 200, w1: 100));
        Assert.Equal("OK", EvaluateCancelSpike(stale: false, current: 149, median: 100, max4wk: 200, w1: 100));
        Assert.Equal("LOW_VOLUME", EvaluateCancelSpike(stale: false, current: 20, median: 5, max4wk: 8, w1: 5));

        Assert.Equal("OK", EvaluateNegMargin(stale: false, current: 4, median: 1, max4wk: 2, w1: 1));
        Assert.Equal("RED", EvaluateNegMargin(stale: false, current: 5, median: 2, max4wk: 4, w1: 2));
        Assert.Equal("AMBER", EvaluateNegMargin(stale: false, current: 6, median: 4, max4wk: 10, w1: 2));
        Assert.Equal("STALE", EvaluateDeepNeg(stale: true, current: 3));
        Assert.Equal("RED", EvaluateDeepNeg(stale: false, current: 1));
        Assert.Equal("OK", EvaluateDeepNeg(stale: false, current: 0));
        Assert.Equal("RED", EvaluateMarginRate(stale: false, bookingMedian: 100, current: 0.10m, baseline: 0.21m));
        Assert.Equal("AMBER", EvaluateMarginRate(stale: false, bookingMedian: 100, current: 0.15m, baseline: 0.21m));
        Assert.Equal("OK", EvaluateMarginRate(stale: false, bookingMedian: 100, current: 0.17m, baseline: 0.21m));
        Assert.Equal("LOW_VOLUME", EvaluateMarginRate(stale: false, bookingMedian: 40, current: 0.10m, baseline: 0.21m));

        Assert.Equal("GROSS_DROP", EvaluateDriver(grossMedian: 100, grossCurrent: 40, cancelMedian: 50, cancelCurrent: 50));
        Assert.Equal("CANCEL_SPIKE", EvaluateDriver(grossMedian: 100, grossCurrent: 100, cancelMedian: 50, cancelCurrent: 60));
        Assert.Null(RouteNetBookingsDriver(status: "OK", driver: "GROSS_DROP"));
        Assert.Equal("GROSS_DROP", RouteNetBookingsDriver(status: "OUTAGE", driver: "GROSS_DROP"));
        Assert.Equal("GROSS_DROP", RouteNetBookingsDriver(status: "AMBER", driver: "GROSS_DROP"));
        Assert.Equal("CANCEL_SPIKE", RouteDriver(status: "RED", driver: "CANCEL_SPIKE"));
        Assert.Null(RouteDriver(status: "OUTAGE", driver: "GROSS_DROP"));
        Assert.Equal("MARGIN_RATE", EvaluateMarginDriver(current: 0.15m, baseline: 0.21m));
        Assert.Equal("VOLUME", EvaluateMarginDriver(current: 0.20m, baseline: 0.21m));
        Assert.Equal("MULTI_ROOM", EvaluateDeepNegDriver(multiRoom: 1));
        Assert.Equal("OTHER", EvaluateDeepNegDriver(multiRoom: 0));

        Assert.Equal(1, EvaluateValuePerBookingDrop(grossCurrent: 80, grossMedian: 100, grossSalesCurrent: 70, grossSalesMedian: 100));
        Assert.Equal(0, EvaluateValuePerBookingDrop(grossCurrent: 70, grossMedian: 100, grossSalesCurrent: 70, grossSalesMedian: 100));
        Assert.Null(RouteValuePerBookingDrop(grossSalesStatus: "OK", raw: 0));
        Assert.Equal(1, RouteValuePerBookingDrop(grossSalesStatus: "AMBER", raw: 1));
        Assert.Equal("MULTI_ROOM", RouteDeepNegDriver(status: "RED", driver: "MULTI_ROOM"));
        Assert.Null(RouteDeepNegDriver(status: "AMBER", driver: "MULTI_ROOM"));
    }

    [FabricIntegrationFact]
    public async Task ReadsAlertStatusesFromTheView()
    {
        var rows = await QueryAsync(HotelBookingKpiConnection.AlertStatusSql);

        foreach (var row in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.AlertLevel));
            Assert.False(string.IsNullOrWhiteSpace(row.Grain));
            Assert.Contains(row.GrossBookingsStatus, Statuses);
            Assert.Contains(row.CancelBookingsStatus, BookingStatuses);
            Assert.Contains(row.NetBookingsStatus, Statuses);
            Assert.Contains(row.GrossSalesStatus, AmountStatuses);
            Assert.Contains(row.NetSalesStatus, AmountStatuses);
            Assert.Contains(row.MarginStatus, AmountStatuses);
            Assert.Contains(row.MarginRateStatus, AmountStatuses);
            Assert.Contains(row.NegMarginStatus, NegMarginStatuses);
            Assert.Contains(row.DeepNegMarginStatus, DeepNegStatuses);
            AssertRouted(row.NetBookingsStatus, row.NetBookingsDriver, Drivers, includeOutage: true);
            AssertRouted(row.NetSalesStatus, row.NetSalesDriver, Drivers, includeOutage: false);
            AssertRouted(row.MarginStatus, row.MarginDriver, MarginDrivers, includeOutage: false);
            if (row.GrossSalesStatus is "RED" or "AMBER")
                Assert.Contains(row.ValuePerBookingDrop, new int?[] { 0, 1 });
            else
                Assert.Null(row.ValuePerBookingDrop);
            if (row.DeepNegMarginStatus == "RED")
                Assert.Contains(row.DeepNegMarginDriver, DeepNegDrivers);
            else
                Assert.Null(row.DeepNegMarginDriver);
            output.WriteLine(
                $"{row.Grain} {row.AlertLevel} club={row.VacationClubID} gross={row.GrossBookingsStatus} cancel={row.CancelBookingsStatus} net={row.NetBookingsStatus} grossSales={row.GrossSalesStatus} netSales={row.NetSalesStatus} margin={row.MarginStatus} marginRate={row.MarginRateStatus} neg={row.NegMarginStatus} deepNeg={row.DeepNegMarginStatus} driver={row.NetBookingsDriver} salesDriver={row.NetSalesDriver} marginDriver={row.MarginDriver} valueDrop={row.ValuePerBookingDrop} deepNegDriver={row.DeepNegMarginDriver}");
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class FabricIntegrationFactAttribute : FactAttribute
    {
        public FabricIntegrationFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("RUN_FABRIC_INTEGRATION_TESTS"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Set RUN_FABRIC_INTEGRATION_TESTS=true to run live Fabric integration tests.";
            }
        }
    }

    private static readonly string[] Statuses = ["STALE", "OUTAGE", "LOW_VOLUME", "RED", "AMBER", "OK"];

    private static readonly string[] BookingStatuses = ["STALE", "LOW_VOLUME", "RED", "AMBER", "OK"];

    private static readonly string[] AmountStatuses = ["STALE", "LOW_VOLUME", "RED", "AMBER", "OK"];

    private static readonly string[] NegMarginStatuses = ["STALE", "RED", "AMBER", "OK"];

    private static readonly string[] DeepNegStatuses = ["STALE", "RED", "OK"];

    private static readonly string[] Drivers = ["GROSS_DROP", "CANCEL_SPIKE"];

    private static readonly string[] MarginDrivers = ["MARGIN_RATE", "VOLUME"];

    private static readonly string[] DeepNegDrivers = ["MULTI_ROOM", "OTHER"];

    private static void AssertRouted(string status, string? driver, string[] allowed, bool includeOutage)
    {
        if (status is "RED" or "AMBER" || (includeOutage && status == "OUTAGE"))
            Assert.Contains(driver, allowed);
        else
            Assert.Null(driver);
    }

    private static string Collapse(string sql)
    {
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(sql, @"--.*?(\r?\n|$)", " ");
        return string.Join(' ', withoutComments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private const decimal MinVol = 50m;
    private const decimal MinOutageVol = 10m;
    private const decimal MinCancelVol = 10m;
    private const decimal MinNegVol = 5m;
    private const decimal OutagePct = 0.10m;
    private const decimal RedPct = 0.50m;
    private const decimal AmberPct = 0.75m;
    private const decimal SuddenPct = 0.75m;
    private const decimal MarginPtsAmber = 0.05m;
    private const decimal MarginPtsRed = 0.10m;
    private const decimal MinDeepNeg = 1m;

    private static string EvaluateBookingDrop(bool stale, decimal current, decimal median, decimal min4wk, decimal w1)
    {
        if (stale) return "STALE";
        if (median >= MinOutageVol && current <= OutagePct * median) return "OUTAGE";
        if (median < MinVol) return "LOW_VOLUME";
        if (current <= RedPct * median && current < min4wk && current <= SuddenPct * w1) return "RED";
        if (current <= AmberPct * median) return "AMBER";
        return "OK";
    }

    private static string EvaluateAmountDrop(bool stale, decimal bookingMedian, decimal metricMedian, decimal current, decimal min4wk, decimal w1)
    {
        if (stale) return "STALE";
        if (bookingMedian < MinVol || metricMedian <= 0) return "LOW_VOLUME";
        if (current <= RedPct * metricMedian && current < min4wk && current <= SuddenPct * w1) return "RED";
        if (current <= AmberPct * metricMedian) return "AMBER";
        return "OK";
    }

    private static string EvaluateCancelSpike(bool stale, decimal current, decimal median, decimal max4wk, decimal w1)
    {
        if (stale) return "STALE";
        if (median < MinCancelVol) return "LOW_VOLUME";
        if (current >= 2m * median && current > max4wk && current >= 1.5m * w1) return "RED";
        if (current >= 1.5m * median) return "AMBER";
        return "OK";
    }

    private static string EvaluateNegMargin(bool stale, decimal current, decimal median, decimal max4wk, decimal w1)
    {
        if (stale) return "STALE";
        if (current < MinNegVol) return "OK";
        if (current >= 2m * median && current > max4wk && current >= 1.5m * w1) return "RED";
        if (current >= 1.5m * median) return "AMBER";
        return "OK";
    }

    private static string EvaluateDeepNeg(bool stale, decimal current)
    {
        if (stale) return "STALE";
        if (current >= MinDeepNeg) return "RED";
        return "OK";
    }

    private static string EvaluateMarginRate(bool stale, decimal bookingMedian, decimal? current, decimal? baseline)
    {
        if (stale) return "STALE";
        if (bookingMedian < MinVol || current is null || baseline is null) return "LOW_VOLUME";
        if (current <= baseline - MarginPtsRed) return "RED";
        if (current <= baseline - MarginPtsAmber) return "AMBER";
        return "OK";
    }

    private static string EvaluateDriver(decimal grossMedian, decimal grossCurrent, decimal cancelMedian, decimal cancelCurrent) =>
        (grossMedian - grossCurrent) >= (cancelCurrent - cancelMedian) ? "GROSS_DROP" : "CANCEL_SPIKE";

    private static string? RouteNetBookingsDriver(string status, string driver) =>
        status is "OUTAGE" or "RED" or "AMBER" ? driver : null;

    private static string? RouteDriver(string status, string driver) =>
        status is "RED" or "AMBER" ? driver : null;

    private static string EvaluateMarginDriver(decimal current, decimal baseline) =>
        current <= baseline - MarginPtsAmber ? "MARGIN_RATE" : "VOLUME";

    private static string EvaluateDeepNegDriver(int multiRoom) =>
        multiRoom > 0 ? "MULTI_ROOM" : "OTHER";

    private static string? RouteDeepNegDriver(string status, string driver) =>
        status == "RED" ? driver : null;

    private static int EvaluateValuePerBookingDrop(decimal grossCurrent, decimal grossMedian, decimal grossSalesCurrent, decimal grossSalesMedian) =>
        grossCurrent > AmberPct * grossMedian && grossSalesCurrent <= AmberPct * grossSalesMedian ? 1 : 0;

    private static int? RouteValuePerBookingDrop(string grossSalesStatus, int raw) =>
        grossSalesStatus is "RED" or "AMBER" ? raw : null;

    private async Task<List<HotelBookingKpiResponse>> QueryAsync(string sql)
    {
        var connectionString = await HotelBookingKpiConnection.CreateConnectionStringAsync();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        return (await connection.QueryAsync<HotelBookingKpiResponse>(
            new CommandDefinition(sql, commandTimeout: 180))).AsList();
    }
}
