namespace JourneyMonitor.Core.Domain;

/// <summary>
/// One row from the hotel booking KPI alert query against <c>dbo.vw_Alert_HotelBookingKPIs</c>.
/// Property names and order match the result columns, including the computed status columns, so a query can map each row directly.
/// </summary>
public sealed class HotelBookingKpiResponse
{
    public const string ViewName = "dbo.vw_Alert_HotelBookingKPIs";

    public const string QueryLabel = "agentic_portal_hotel_kpi";

    public required string Grain { get; init; }

    public required string AlertLevel { get; init; }

    public DateTime? WindowStartPT { get; init; }

    public DateTime? WindowEndPT { get; init; }

    public int? VacationClubClientID { get; init; }

    public string? ClientName { get; init; }

    public long? VacationClubID { get; init; }

    public string? VacationClubName { get; init; }

    public int? GrossBookingsCurrent { get; init; }

    public int? CancelBookingsCurrent { get; init; }

    public int? NetBookingsCurrent { get; init; }

    public decimal? GrossSalesCurrent { get; init; }

    public decimal? CancelSalesCurrent { get; init; }

    public decimal? NetSalesCurrent { get; init; }

    public decimal? MarginSalesCurrent { get; init; }

    public decimal? COGSCurrent { get; init; }

    public decimal? MarginCurrent { get; init; }

    public decimal? MarginPctCurrent { get; init; }

    public decimal? MarginPct4wk { get; init; }

    public int? NegMarginBookingsCurrent { get; init; }

    public decimal? NegMarginBookingsMedian4wk { get; init; }

    public decimal? NegMarginAmountCurrent { get; init; }

    public int? DeepNegMarginBookingsCurrent { get; init; }

    public int? MultiRoomNegMarginBookingsCurrent { get; init; }

    public int? FreshnessLagMin { get; init; }

    /// <summary>STALE, OUTAGE, LOW_VOLUME, RED, AMBER, or OK. Gross bookings compared with the prior-4-week median.</summary>
    public required string GrossBookingsStatus { get; init; }

    /// <summary>STALE, LOW_VOLUME, RED, AMBER, or OK. Cancellations compared with the prior-4-week median and maximum.</summary>
    public required string CancelBookingsStatus { get; init; }

    /// <summary>STALE, OUTAGE, LOW_VOLUME, RED, AMBER, or OK. Net bookings compared with the prior-4-week median.</summary>
    public required string NetBookingsStatus { get; init; }

    /// <summary>STALE, LOW_VOLUME, RED, AMBER, or OK. Gross sales compared with the prior-4-week median and minimum.</summary>
    public required string GrossSalesStatus { get; init; }

    /// <summary>STALE, LOW_VOLUME, RED, AMBER, or OK. Net sales compared with the prior-4-week median and minimum.</summary>
    public required string NetSalesStatus { get; init; }

    /// <summary>STALE, LOW_VOLUME, RED, AMBER, or OK. Margin dollars compared with the prior-4-week median and minimum.</summary>
    public required string MarginStatus { get; init; }

    /// <summary>STALE, LOW_VOLUME, RED, AMBER, or OK. Margin percent compared with the prior-4-week margin percent.</summary>
    public required string MarginRateStatus { get; init; }

    /// <summary>STALE, RED, AMBER, or OK. Negative-margin booking count compared with the prior-4-week median and maximum.</summary>
    public required string NegMarginStatus { get; init; }

    /// <summary>STALE, RED, or OK. Absolute deep-negative margin count, with no baseline.</summary>
    public required string DeepNegMarginStatus { get; init; }

    /// <summary>GROSS_DROP or CANCEL_SPIKE when net bookings is OUTAGE, RED, or AMBER; otherwise null.</summary>
    public string? NetBookingsDriver { get; init; }

    /// <summary>GROSS_DROP or CANCEL_SPIKE when net sales is RED or AMBER; otherwise null.</summary>
    public string? NetSalesDriver { get; init; }

    /// <summary>MARGIN_RATE or VOLUME when margin is RED or AMBER; otherwise null.</summary>
    public string? MarginDriver { get; init; }

    /// <summary>1 when gross sales is RED or AMBER and value per booking dropped; 0 when gross sales is RED or AMBER for another reason; otherwise null.</summary>
    public int? ValuePerBookingDrop { get; init; }

    /// <summary>MULTI_ROOM or OTHER when deep-negative margin is RED; otherwise null.</summary>
    public string? DeepNegMarginDriver { get; init; }
}
