namespace JourneyMonitor.Core.Options;

/// <summary>
/// App Configuration keys for the Fabric warehouse that hosts
/// <c>dbo.vw_Alert_HotelBookingKPIs</c>. A deployment copies each key into an environment
/// variable with hyphens replaced by underscores, the same way
/// <see cref="JourneyStoreOptions.AppConfigKey"/> is copied from
/// platform-reliability-database-connectionString.
/// </summary>
public static class FabricWarehouseOptions
{
    public const string ConnectionStringKey = "platform_reliability_fabric_connectionString";

    public const string ClientIdKey = "platform_reliability_fabric_clientId";

    public const string ClientSecretKey = "platform_reliability_fabric_clientSecret";

    public const string TenantIdKey = "platform_reliability_fabric_tenantId";
}
