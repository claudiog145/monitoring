using System.Reflection;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Business;

public class HotelBookingKpiResponseTests
{
    /// <summary>
    /// Column order and CLR types for the hotel booking KPI alert query.
    /// Status columns are always populated. Routing hints are populated only when the related status is OUTAGE, RED, or AMBER.
    /// </summary>
    private static readonly (string Name, Type Type)[] ViewColumns =
    [
        ("Grain", typeof(string)),
        ("AlertLevel", typeof(string)),
        ("WindowStartPT", typeof(DateTime?)),
        ("WindowEndPT", typeof(DateTime?)),
        ("VacationClubClientID", typeof(int?)),
        ("ClientName", typeof(string)),
        ("VacationClubID", typeof(long?)),
        ("VacationClubName", typeof(string)),
        ("GrossBookingsCurrent", typeof(int?)),
        ("CancelBookingsCurrent", typeof(int?)),
        ("NetBookingsCurrent", typeof(int?)),
        ("GrossSalesCurrent", typeof(decimal?)),
        ("CancelSalesCurrent", typeof(decimal?)),
        ("NetSalesCurrent", typeof(decimal?)),
        ("MarginSalesCurrent", typeof(decimal?)),
        ("COGSCurrent", typeof(decimal?)),
        ("MarginCurrent", typeof(decimal?)),
        ("MarginPctCurrent", typeof(decimal?)),
        ("MarginPct4wk", typeof(decimal?)),
        ("NegMarginBookingsCurrent", typeof(int?)),
        ("NegMarginBookingsMedian4wk", typeof(decimal?)),
        ("NegMarginAmountCurrent", typeof(decimal?)),
        ("DeepNegMarginBookingsCurrent", typeof(int?)),
        ("MultiRoomNegMarginBookingsCurrent", typeof(int?)),
        ("FreshnessLagMin", typeof(int?)),
        ("GrossBookingsStatus", typeof(string)),
        ("CancelBookingsStatus", typeof(string)),
        ("NetBookingsStatus", typeof(string)),
        ("GrossSalesStatus", typeof(string)),
        ("NetSalesStatus", typeof(string)),
        ("MarginStatus", typeof(string)),
        ("MarginRateStatus", typeof(string)),
        ("NegMarginStatus", typeof(string)),
        ("DeepNegMarginStatus", typeof(string)),
        ("NetBookingsDriver", typeof(string)),
        ("NetSalesDriver", typeof(string)),
        ("MarginDriver", typeof(string)),
        ("ValuePerBookingDrop", typeof(int?)),
        ("DeepNegMarginDriver", typeof(string)),
    ];

    [Fact]
    public void PropertiesMatchViewColumns()
    {
        var properties = typeof(HotelBookingKpiResponse)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.Equal(ViewColumns, properties);
    }

    [Fact]
    public void NamesTheSourceView()
    {
        Assert.Equal("dbo.vw_Alert_HotelBookingKPIs", HotelBookingKpiResponse.ViewName);
        Assert.Equal("agentic_portal_hotel_kpi", HotelBookingKpiResponse.QueryLabel);
    }
}
