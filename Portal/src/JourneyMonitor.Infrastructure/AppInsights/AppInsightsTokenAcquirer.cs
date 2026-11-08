using Azure.Core;
using Azure.Identity;
using JourneyMonitor.Core.Monitoring.TokenRefresh;

namespace JourneyMonitor.Infrastructure.AppInsights;

/// <summary>
/// Azure.Identity adapter for <see cref="IAppInsightsTokenAcquirer"/>: azure-cli uses
/// <see cref="AzureCliCredential"/> for the tenant, service-principal uses <see cref="ClientSecretCredential"/>.
/// The scope comes from <see cref="StoredTokenScopes"/> (App Insights, or Azure DevOps for "azure-devops").
/// </summary>
public sealed class AppInsightsTokenAcquirer : IAppInsightsTokenAcquirer
{
    public const string QueryScope = StoredTokenScopes.AppInsights;

    public async Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(
        string refreshMode, string aadTenantId, string? clientId, string? clientSecret, string scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(aadTenantId))
            throw new InvalidOperationException("aadTenantId is required to acquire a token.");

        TokenCredential credential = AppInsightsTokenRefreshModes.Normalize(refreshMode) switch
        {
            AppInsightsTokenRefreshModes.AzureCli => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = aadTenantId.Trim() }),
            AppInsightsTokenRefreshModes.ServicePrincipal when !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrEmpty(clientSecret)
                => new ClientSecretCredential(aadTenantId.Trim(), clientId.Trim(), clientSecret),
            AppInsightsTokenRefreshModes.ServicePrincipal
                => throw new InvalidOperationException("clientId and clientSecret are required for service-principal."),
            _ => throw new InvalidOperationException($"refreshMode '{refreshMode}' cannot be refreshed automatically."),
        };

        var token = await credential.GetTokenAsync(
            new TokenRequestContext([string.IsNullOrWhiteSpace(scope) ? QueryScope : scope.Trim()]), ct).ConfigureAwait(false);
        return (token.Token, token.ExpiresOn);
    }
}
