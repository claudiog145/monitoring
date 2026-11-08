using JourneyMonitor.Infrastructure.Fabric;
using Microsoft.Data.SqlClient;

namespace JourneyMonitor.Tests.Business;

public class HotelBookingKpiConnectionTests
{
    [Fact]
    public void ReadsTheFabricConnectionFromKeyVault()
    {
        Assert.Equal("platform-rel-kv-qa.vault.azure.net", HotelBookingKpiConnection.KeyVaultUri.Host);
        Assert.Equal("platform-reliability-fabric-connectionString", HotelBookingKpiConnection.ConnectionStringSecretName);
        Assert.Equal("platform-reliability-fabric-clientId", HotelBookingKpiConnection.ClientIdSecretName);
        Assert.Equal("platform-reliability-fabric-clientSecret", HotelBookingKpiConnection.ClientSecretSecretName);
        Assert.Equal("platform-reliability-fabric-tenantId", HotelBookingKpiConnection.TenantIdSecretName);
    }

    [Fact]
    public void AppliesTheServicePrincipalUserAndPassword()
    {
        const string stored =
            "Server=tcp:warehouse.example,1433;Initial Catalog=TP_SaveOn_Real-Time;Encrypt=Optional;TrustServerCertificate=True;Authentication=Active Directory Service Principal;Connection Timeout=30;";

        var connectionString = HotelBookingKpiConnection.WithServicePrincipal(stored, "client-id", "client-secret");
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, builder.Authentication);
        Assert.Equal("client-id", builder.UserID);
        Assert.Equal("client-secret", builder.Password);
        Assert.Equal("TP_SaveOn_Real-Time", builder.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
        Assert.Equal(30, builder.ConnectTimeout);
    }
}
