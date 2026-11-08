using JourneyMonitor.Core.Monitoring.TokenRefresh;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// token-refresh daemon (contracts-company §2.3): renews stored App Insights tokens configured for
/// azure-cli or service-principal before they expire. TokenRefresh:Enabled=false records run-skipped.
/// </summary>
public sealed class TokenRefreshFunction(
    IAppInsightsTokenRefreshService refreshService,
    TimeProvider time,
    ILogger<TokenRefreshFunction> logger)
{
    [Function("TokenRefresh")]
    public async Task RunAsync([TimerTrigger("%spec1000_token_refresh_cron%")] TimerInfo timer, CancellationToken ct)
    {
        var result = await refreshService.RefreshDueAsync(time.GetUtcNow(), ct);
        if (result.Skipped)
        {
            logger.LogDebug("[TokenRefresh] TokenRefresh:Enabled is false - nothing to do.");
            return;
        }
        logger.LogInformation("[TokenRefresh] {Refreshed} refreshed, {Failed} failed, {NotDue} not due.",
            result.Refreshed, result.Failed, result.NotDue);
    }
}
