using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.TokenRefresh;

public sealed record TokenRefreshRunResult(int Refreshed, int Failed, int NotDue, bool Skipped = false);

public enum TokenRefreshNowStatus { Refreshed, NotFound, ManualRejected, Inactive, Changed, Failed }

/// <summary>Outcome of an on-demand refresh. The API maps it to 200 / 404 / 409 (manual, inactive, changed) / 502.</summary>
public sealed record TokenRefreshNowResult(TokenRefreshNowStatus Status, DateTimeOffset? ExpiresAt, string? Error);

public interface IAppInsightsTokenRefreshService
{
    /// <summary>Refreshes every active automatic token that expires within TokenRefresh:RefreshAheadMinutes of <paramref name="now"/>.</summary>
    Task<TokenRefreshRunResult> RefreshDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Refreshes one token regardless of its expiry. Manual tokens are rejected.</summary>
    Task<TokenRefreshNowResult> RefreshNowAsync(string tenantKey, CancellationToken ct = default);
}

/// <summary>
/// The token-refresh daemon (contracts-company §2.3). Acquires new tokens for azure-cli and
/// service-principal rows, stores them encrypted and records the activity lines of the contract.
/// The token and the client secret never reach a log, an activity line or a stored error.
/// </summary>
public sealed class AppInsightsTokenRefreshService : IAppInsightsTokenRefreshService
{
    public const string RefreshManualRejectedMessage = "Manual tokens cannot be refreshed automatically; paste a new token.";
    public const string RefreshInactiveMessage = "Token is deactivated.";
    public const string RefreshChangedMessage = "Token settings changed during refresh; nothing was overwritten. Try again.";
    public const string NothingConfiguredMessage = "No tokens configured for automatic refresh";
    public const int MaxErrorLength = 500;
    private const string Redacted = "[redacted]";

    private readonly IAppInsightsTokenRepository _repository;
    private readonly ITokenSecretProtector _protector;
    private readonly IAppInsightsTokenAcquirer _acquirer;
    private readonly IOptions<TokenRefreshOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AppInsightsTokenRefreshService> _logger;
    private readonly IDaemonActivityRecorder? _activity;
    private readonly IMonkeyRunTracker? _runTracker;

    public AppInsightsTokenRefreshService(
        IAppInsightsTokenRepository repository,
        ITokenSecretProtector protector,
        IAppInsightsTokenAcquirer acquirer,
        IOptions<TokenRefreshOptions> options,
        TimeProvider time,
        ILogger<AppInsightsTokenRefreshService> logger,
        IDaemonActivityRecorder? activity = null,
        IMonkeyRunTracker? runTracker = null)
    {
        _repository = repository;
        _protector = protector;
        _acquirer = acquirer;
        _options = options;
        _time = time;
        _logger = logger;
        _activity = activity;
        _runTracker = runTracker;
    }

    public async Task<TokenRefreshRunResult> RefreshDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var options = _options.Value;
        var run = _activity?.BeginRun(DaemonKeys.TokenRefresh) ?? NullDaemonRun.Instance;

        if (!options.Enabled)
        {
            run.Info(DaemonActivityKinds.RunSkipped, "Skipped: TokenRefresh:Enabled is false");
            await DaemonActivitySupport.FinishAsync(run, _runTracker, DaemonKeys.TokenRefresh, _time.GetUtcNow(), _logger);
            return new TokenRefreshRunResult(0, 0, 0, Skipped: true);
        }

        run.Info(DaemonActivityKinds.RunStarted, "Run started");
        int refreshed = 0, failed = 0, notDue = 0;
        try
        {
            var automatic = (await _repository.GetAllAsync())
                .Where(t => t.IsActive && AppInsightsTokenRefreshModes.IsAutomatic(t.RefreshMode))
                .ToList();

            if (automatic.Count == 0)
            {
                run.Info(DaemonActivityKinds.RunCompleted, NothingConfiguredMessage);
                return new TokenRefreshRunResult(0, 0, 0);
            }

            var dueBefore = now.AddMinutes(Math.Max(0, options.RefreshAheadMinutes));
            foreach (var token in automatic)
            {
                ct.ThrowIfCancellationRequested();
                if (AsUtc(token.ExpiresAt) > dueBefore)
                {
                    notDue++;
                    continue;
                }

                var outcome = await RefreshOneAsync(token, run, ct);
                if (outcome.Status == TokenRefreshNowStatus.Refreshed) refreshed++;
                else if (outcome.Status == TokenRefreshNowStatus.Changed) notDue++;
                else failed++;
            }

            var summary = $"{refreshed} refreshed, {failed} failed, {notDue} not due";
            if (failed > 0) run.Warning(DaemonActivityKinds.RunCompleted, summary);
            else run.Info(DaemonActivityKinds.RunCompleted, summary);
            return new TokenRefreshRunResult(refreshed, failed, notDue);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var message = Truncate(ex.Message);
            _logger.LogWarning("Token refresh run failed: {Error}", message);
            run.Error(DaemonActivityKinds.Error, $"Token refresh run failed — {message}");
            run.Error(DaemonActivityKinds.RunCompleted, $"{refreshed} refreshed, {failed + 1} failed, {notDue} not due");
            return new TokenRefreshRunResult(refreshed, failed + 1, notDue);
        }
        finally
        {
            await DaemonActivitySupport.FinishAsync(run, _runTracker, DaemonKeys.TokenRefresh, _time.GetUtcNow(), _logger);
        }
    }

    public async Task<TokenRefreshNowResult> RefreshNowAsync(string tenantKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
            return new TokenRefreshNowResult(TokenRefreshNowStatus.NotFound, null, "Tenant key is required.");

        var token = await _repository.GetByTenantKeyAsync(tenantKey.Trim());
        if (token == null)
            return new TokenRefreshNowResult(TokenRefreshNowStatus.NotFound, null, $"No token stored for tenant '{tenantKey.Trim()}'.");

        if (!AppInsightsTokenRefreshModes.IsAutomatic(token.RefreshMode))
            return new TokenRefreshNowResult(TokenRefreshNowStatus.ManualRejected, null, RefreshManualRejectedMessage);

        if (!token.IsActive)
            return new TokenRefreshNowResult(TokenRefreshNowStatus.Inactive, null, RefreshInactiveMessage);

        var run = _activity?.BeginRun(DaemonKeys.TokenRefresh) ?? NullDaemonRun.Instance;
        run.Info(DaemonActivityKinds.RunStarted, $"Refresh requested for {token.TenantKey}");
        TokenRefreshNowResult result;
        try
        {
            result = await RefreshOneAsync(token, run, ct);
            var ok = result.Status == TokenRefreshNowStatus.Refreshed;
            var failedCount = result.Status == TokenRefreshNowStatus.Failed ? 1 : 0;
            var summary = $"{(ok ? 1 : 0)} refreshed, {failedCount} failed, {(ok || failedCount == 1 ? 0 : 1)} not due";
            if (failedCount == 0) run.Info(DaemonActivityKinds.RunCompleted, summary);
            else run.Warning(DaemonActivityKinds.RunCompleted, summary);
        }
        finally
        {
            await DaemonActivitySupport.FinishAsync(run, _runTracker, DaemonKeys.TokenRefresh, _time.GetUtcNow(), _logger);
        }
        return result;
    }

    private async Task<TokenRefreshNowResult> RefreshOneAsync(AppInsightsToken token, IDaemonRun run, CancellationToken ct)
    {
        var mode = AppInsightsTokenRefreshModes.Normalize(token.RefreshMode) ?? AppInsightsTokenRefreshModes.Manual;
        string? secret = null;
        string? oldToken = null;
        try
        {
            oldToken = TryUnprotect(token.EncryptedToken);
            if (string.IsNullOrWhiteSpace(token.AadTenantId))
                throw new InvalidOperationException("aadTenantId is not configured");

            if (mode == AppInsightsTokenRefreshModes.ServicePrincipal)
            {
                if (string.IsNullOrWhiteSpace(token.ClientId) || string.IsNullOrEmpty(token.EncryptedClientSecret))
                    throw new InvalidOperationException("clientId and clientSecret are required for service-principal");
                secret = _protector.Unprotect(token.EncryptedClientSecret);
            }

            var (newToken, expiresOn) = await _acquirer.AcquireAsync(
                mode, token.AadTenantId!, token.ClientId, secret, StoredTokenScopes.ForTenantKey(token.TenantKey), ct);
            if (string.IsNullOrWhiteSpace(newToken))
                throw new InvalidOperationException("the credential returned an empty token");

            var refreshedRow = new AppInsightsToken
            {
                TenantKey = token.TenantKey,
                EncryptedToken = _protector.Protect(newToken),
                ExpiresAt = expiresOn.UtcDateTime,
                LastRefreshAt = _time.GetUtcNow().UtcDateTime,
                RefreshCount = token.RefreshCount + 1,
            };
            if (!await _repository.TryUpdateRefreshedTokenAsync(refreshedRow, token))
            {
                _logger.LogInformation("App Insights token {TenantKey} changed during refresh, skipped.", token.TenantKey);
                return new TokenRefreshNowResult(TokenRefreshNowStatus.Changed, null, RefreshChangedMessage);
            }

            var message = $"{token.TenantKey}: refreshed via {mode}, expires {DaemonActivitySupport.Hm(expiresOn)} UTC";
            run.Info(DaemonActivityKinds.TokenRefreshed, message);
            _logger.LogInformation("App Insights token {Message}", message);
            return new TokenRefreshNowResult(TokenRefreshNowStatus.Refreshed, expiresOn, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = Truncate(Redact(ex.Message, secret, oldToken));
            try
            {
                // Only LastRefreshError is written: the old token and any concurrent user edit stay intact.
                await _repository.UpdateRefreshErrorAsync(token.TenantKey, error);
            }
            catch (Exception saveEx)
            {
                _logger.LogWarning("Could not record the refresh error for {TenantKey}: {Error}", token.TenantKey, Truncate(Redact(saveEx.Message, secret, oldToken)));
            }

            run.Error(DaemonActivityKinds.Error, $"{token.TenantKey}: refresh via {mode} failed — {error}");
            _logger.LogWarning("App Insights token refresh for {TenantKey} via {Mode} failed: {Error}", token.TenantKey, mode, error);
            return new TokenRefreshNowResult(TokenRefreshNowStatus.Failed, null, error);
        }
    }

    private string? TryUnprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Removes any occurrence of the secret or token values from a message.</summary>
    public static string Redact(string? message, params string?[] secrets)
    {
        var text = message ?? string.Empty;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        }
        return text;
    }

    private static string Truncate(string text) =>
        text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
