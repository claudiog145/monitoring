namespace JourneyMonitor.Core.Monitoring.TokenRefresh;

/// <summary>
/// Acquires a fresh token for <paramref name="scope"/> (see <see cref="StoredTokenScopes"/>).
/// azure-cli uses the CLI login for <paramref name="aadTenantId"/>; service-principal uses the client
/// id and secret. Implemented in Infrastructure so Core never talks to Azure.
/// </summary>
public interface IAppInsightsTokenAcquirer
{
    Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(
        string refreshMode, string aadTenantId, string? clientId, string? clientSecret, string scope, CancellationToken ct);
}
