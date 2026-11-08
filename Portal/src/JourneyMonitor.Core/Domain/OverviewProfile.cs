using JourneyMonitor.Core.Analytics;

namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Contract v6 §4.5. What the overview reads for ONE company. <see cref="ServiceKey"/> and
/// <see cref="EntityKey"/> are the profile's own service and entity keys (the incumbent's are its verticals and clubs).
/// </summary>
public sealed record ProfileOverviewQuery(
    string ProfileKey,
    bool IsIncumbent,
    string? ServiceKey,
    string? EntityKey,
    string Environment,
    DateTimeOffset FilteredSince,
    DateTimeOffset AvailabilitySince,
    string? SearchId,
    int TopEntities);

/// <summary>One entity and how much trouble it had in the window. Either source may be zero.</summary>
public sealed record OverviewEntity(
    string Key,
    int Incidents,
    long Occurrences,
    int AffectedJourneys);

/// <summary>
/// Stored counts of one company. <see cref="OverviewCounts"/> keeps its null-means-unreadable meaning;
/// the entity lists are already limited to that company's rows.
/// </summary>
public sealed record ProfileOverviewCounts(
    OverviewCounts Counts,
    IReadOnlyList<OverviewEntity> IncidentEntities,
    IReadOnlyList<OverviewEntity> JourneyEntities);

/// <summary>Contract v6 §4.5: the SQL half of the per-company overview. Every query is filtered by ProfileKey.</summary>
public interface IProfileOverviewReader
{
    Task<ProfileOverviewCounts> ReadAsync(ProfileOverviewQuery query, CancellationToken cancellationToken);
}

/// <summary>Whether a widget applies to the company; the UI hides non-applicable ones and shows <see cref="Note"/>.</summary>
public sealed record OverviewWidget(string Key, bool Applicable, string? Note = null);

public static class OverviewWidgetKeys
{
    public const string Tiles = "tiles";
    public const string Pillars = "pillars";
    public const string Kpis = "kpis";
    public const string Exceptions = "exceptions";
    public const string Investigations = "investigations";
    public const string TopEntities = "topEntities";

    /// <summary>Incumbent only: the Fabric business-health warehouse.</summary>
    public const string HotelBusinessHealth = "hotelBusinessHealth";

    /// <summary>Incumbent only: the club inventory and maturity table.</summary>
    public const string ClubInventory = "clubInventory";

    public static readonly IReadOnlyList<string> IncumbentOnly = [HotelBusinessHealth, ClubInventory];
}

/// <summary>What the profile calls each role, for overview headings.</summary>
public sealed record OverviewLabels(string Service, string Flow, string Step, string Entity);

/// <summary>One pillar of the company: its status and how many of its KPIs are anomalous.</summary>
public sealed record OverviewPillar(string Pillar, string Status, int KpiCount, int AnomalousKpis);

/// <summary>One KPI of the company with its latest bucket.</summary>
public sealed record OverviewKpiSummary(
    int KpiDefinitionId,
    string Pillar,
    string Kpi,
    string Domain,
    string Stage,
    bool Enabled,
    bool Anomalous,
    double? LatestValue,
    DateTimeOffset? LatestBucketStart);

/// <summary>One Triage Monkey investigation of the company.</summary>
public sealed record OverviewInvestigation(
    Guid InvestigationId,
    string Status,
    string? AlertLevel,
    string Pillar,
    string Kpi,
    string? Domain,
    string? Stage,
    DateTimeOffset CreatedAt,
    string? Summary);

/// <summary>Incidents/exceptions of the company in the window.</summary>
public sealed record OverviewExceptionSummary(int? Open, int? Total, int? Critical);
