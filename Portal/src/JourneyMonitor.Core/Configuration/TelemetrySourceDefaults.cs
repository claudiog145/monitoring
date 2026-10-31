using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Core.Configuration;

/// <summary>
/// The telemetry sources the platform shipped with, as rows instead of literals scattered through
/// eight KQL files and <see cref="InsightsSourceResolver"/>.
///
/// Unlike <c>monitoring.ServiceOwner</c>, which starts empty because its content is an unmade
/// organisational decision, these are verified facts already proven in production - so both the
/// SQL seed (CreateMonitoringTelemetrySource.sql) and the in-memory fallback start from them. An
/// empty catalog would not be an honest "unknown", it would simply mean no query resolves at all.
///
/// Kept in sync with the MERGE at the bottom of CreateMonitoringTelemetrySource.sql; the fresh
/// clone path (no database) and the seeded database must behave identically.
/// </summary>
public static class TelemetrySourceDefaults
{
    public const string AirUi = "air-ui";
    public const string AirApi = "air-api";
    public const string Club = "club";
    public const string CruiseCore = "cruise-core";
    public const string CruiseApi = "cruise-api";
    public const string SorFrontend = "sor-frontend";

    public static IReadOnlyList<TelemetrySourceEntry> All { get; } =
    [
        Source(AirUi, InsightsEnvironment.Production,
            "/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-Prod-USC/providers/microsoft.insights/components/appInsights-Air-UI-Prod-USC",
            "Air UI, cross-resource half of the Air journey query."),
        Source(AirApi, InsightsEnvironment.Production,
            "/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-Prod-USC/providers/microsoft.insights/components/appInsights-Air-API-Prod-USC",
            "Air API, cross-resource half of the Air journey query."),
        Source(AirUi, InsightsEnvironment.Staging,
            "/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-UAT-USC/providers/microsoft.insights/components/appInsights-Air-UI-UAT-USC",
            "Air UI (UAT)."),
        Source(AirApi, InsightsEnvironment.Staging,
            "/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-UAT-USC/providers/microsoft.insights/components/appInsights-Air-API-UAT-USC",
            "Air API (UAT)."),
        Source(Club, InsightsEnvironment.Production,
            "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/ovs-club-dev/providers/microsoft.insights/components/appInsights-ovs-club",
            "Club front end. Resource group is named -dev but carries production traffic."),
        Source(Club, InsightsEnvironment.Staging,
            "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/club-rg-uat/providers/microsoft.insights/components/club-ai-uat-usc",
            "Club front end (UAT)."),
        Source(CruiseCore, InsightsEnvironment.Production,
            "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-prod",
            "Cruise core services."),
        Source(CruiseCore, InsightsEnvironment.Staging,
            "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-uat-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-uat",
            "Cruise core services (UAT)."),
        Source(CruiseApi, InsightsEnvironment.Production,
            "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-web-api-ai-prod",
            "Cruise web API. No UAT twin registered yet."),
        Source(SorFrontend, InsightsEnvironment.Production,
            "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/sor-appinsights-rg-usw/providers/microsoft.insights/components/sor-frontend-ai-prod-usw",
            "SaveOnResorts front end. No UAT twin registered yet."),
    ];

    private static TelemetrySourceEntry Source(string alias, string environment, string resourceId, string notes) =>
        new(alias, environment, TelemetrySourceKind.AppInsights, resourceId, Notes: notes, UpdatedBy: "seed");
}
