using System.Net;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.TokenRefresh;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Spec1000.A4;
using JourneyMonitor.Tests.Spec1000.D1;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring.TokenRefresh;

internal sealed class InMemoryTokenRepository : IAppInsightsTokenRepository
{
    public List<AppInsightsToken> Rows { get; } = [];
    public int Updates { get; private set; }

    public Task SaveAsync(AppInsightsToken token)
    {
        token.Id = Rows.Count + 1;
        Rows.Add(Clone(token));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(AppInsightsToken token)
    {
        Updates++;
        var index = Rows.FindIndex(r => r.TenantKey == token.TenantKey);
        if (index >= 0) Rows[index] = Clone(token);
        return Task.CompletedTask;
    }

    public Task<AppInsightsToken> GetByTenantKeyAsync(string tenantKey) =>
        Task.FromResult(Rows.Where(r => r.TenantKey == tenantKey).Select(Clone).FirstOrDefault()!);

    public Task<List<AppInsightsToken>> GetAllAsync() => Task.FromResult(Rows.Select(Clone).ToList());

    public Task DeleteAsync(int id)
    {
        Rows.RemoveAll(r => r.Id == id);
        return Task.CompletedTask;
    }

    public AppInsightsToken Get(string tenantKey) => Rows.Single(r => r.TenantKey == tenantKey);

    /// <summary>Runs just before the optimistic refresh write, to simulate a user edit landing during acquisition.</summary>
    public Action? BeforeRefreshWrite { get; set; }

    public Task<bool> TryUpdateRefreshedTokenAsync(AppInsightsToken refreshed, AppInsightsToken readState)
    {
        BeforeRefreshWrite?.Invoke();
        var row = Rows.SingleOrDefault(r => r.TenantKey == refreshed.TenantKey);
        if (row is null
            || row.EncryptedToken != readState.EncryptedToken
            || row.RefreshMode != readState.RefreshMode
            || row.AadTenantId != readState.AadTenantId
            || row.ClientId != readState.ClientId
            || row.EncryptedClientSecret != readState.EncryptedClientSecret
            || !row.IsActive)
        {
            return Task.FromResult(false);
        }

        Updates++;
        row.EncryptedToken = refreshed.EncryptedToken;
        row.ExpiresAt = refreshed.ExpiresAt;
        row.LastRefreshAt = refreshed.LastRefreshAt;
        row.RefreshCount = refreshed.RefreshCount;
        row.LastRefreshError = null;
        return Task.FromResult(true);
    }

    public Task UpdateRefreshErrorAsync(string tenantKey, string? error)
    {
        Updates++;
        var row = Rows.SingleOrDefault(r => r.TenantKey == tenantKey);
        if (row is not null) row.LastRefreshError = error;
        return Task.CompletedTask;
    }

    private static AppInsightsToken Clone(AppInsightsToken t) => new()
    {
        Id = t.Id,
        TenantKey = t.TenantKey,
        Email = t.Email,
        EncryptedToken = t.EncryptedToken,
        ExpiresAt = t.ExpiresAt,
        CreatedAt = t.CreatedAt,
        LastUsedAt = t.LastUsedAt,
        UsageCount = t.UsageCount,
        IsActive = t.IsActive,
        Notes = t.Notes,
        RefreshMode = t.RefreshMode,
        AadTenantId = t.AadTenantId,
        ClientId = t.ClientId,
        EncryptedClientSecret = t.EncryptedClientSecret,
        LastRefreshAt = t.LastRefreshAt,
        LastRefreshError = t.LastRefreshError,
        RefreshCount = t.RefreshCount,
    };
}

internal sealed class FakeTokenAcquirer : IAppInsightsTokenAcquirer
{
    public List<(string Mode, string Tenant, string? ClientId, string? Secret)> Calls { get; } = [];
    public List<string> Scopes { get; } = [];
    public Func<(string Token, DateTimeOffset ExpiresOn)> Next { get; set; } = () => ("new-token-value", DateTimeOffset.UtcNow.AddHours(1));

    public Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(
        string refreshMode, string aadTenantId, string? clientId, string? clientSecret, string scope, CancellationToken ct)
    {
        Calls.Add((refreshMode, aadTenantId, clientId, clientSecret));
        Scopes.Add(scope);
        return Task.FromResult(Next());
    }
}

public sealed class AppInsightsTokenRefreshServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private const string AadTenant = "11111111-2222-3333-4444-555555555555";
    private const string OldToken = "old-token-aaaa.bbbb.cccc";
    private const string ClientSecret = "sp-secret-value-xyz";

    private readonly InMemoryTokenRepository _repository = new();
    private readonly FakeTokenAcquirer _acquirer = new();
    private readonly ITokenSecretProtector _protector = new DataProtectionTokenSecretProtector(new EphemeralDataProtectionProvider());
    private readonly D1FixedTimeProvider _time = new(Now);
    private readonly D1ActivityHarness _activity;
    private readonly D1ListLogger<AppInsightsTokenRefreshService> _logger = new();
    private readonly TokenRefreshOptions _options = new();

    public AppInsightsTokenRefreshServiceTests()
    {
        _activity = new D1ActivityHarness(_time);
    }

    private ITokenSecretProtector Protector => _protector;

    private AppInsightsTokenRefreshService CreateService() => new(
        _repository, _protector, _acquirer, Options.Create(_options), _time, _logger, _activity.Recorder, _activity.Tracker);

    private void AddToken(string tenantKey, string mode, DateTimeOffset expiresAt, string? clientId = null, string? secret = null) =>
        _repository.SaveAsync(new AppInsightsToken
        {
            TenantKey = tenantKey,
            Email = "ops@example.test",
            EncryptedToken = Protector.Protect(OldToken),
            ExpiresAt = expiresAt.UtcDateTime,
            CreatedAt = Now.UtcDateTime.AddHours(-2),
            IsActive = true,
            RefreshMode = mode,
            AadTenantId = AadTenant,
            ClientId = clientId,
            EncryptedClientSecret = secret is null ? null : Protector.Protect(secret),
        }).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<DaemonActivityEntry>> ActivityAsync() => await _activity.EntriesAsync(DaemonKeys.TokenRefresh);

    [Fact]
    public async Task TokenRefresh_DueToken_IsRefreshedAndSaved()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(10));
        var newExpiry = new DateTimeOffset(2026, 11, 6, 13, 5, 0, TimeSpan.Zero);
        _acquirer.Next = () => ("fresh-token-1.2.3", newExpiry);

        var result = await CreateService().RefreshDueAsync(Now);

        Assert.Equal(new TokenRefreshRunResult(1, 0, 0), result);
        var call = Assert.Single(_acquirer.Calls);
        Assert.Equal((AppInsightsTokenRefreshModes.AzureCli, AadTenant, (string?)null, (string?)null), call);

        var row = _repository.Get("tenant-a");
        Assert.Equal("fresh-token-1.2.3", Protector.Unprotect(row.EncryptedToken));
        Assert.Equal(newExpiry.UtcDateTime, row.ExpiresAt);
        Assert.Equal(1, row.RefreshCount);
        Assert.Equal(Now.UtcDateTime, row.LastRefreshAt);
        Assert.Null(row.LastRefreshError);

        var entries = await ActivityAsync();
        Assert.Equal(DaemonActivityKinds.RunStarted, entries[0].Kind);
        var refreshed = Assert.Single(entries, e => e.Kind == DaemonActivityKinds.TokenRefreshed);
        Assert.Equal(DaemonActivityLevel.Info, refreshed.Level);
        Assert.Equal("tenant-a: refreshed via azure-cli, expires 13:05 UTC", refreshed.Message);
        var completed = entries[^1];
        Assert.Equal(DaemonActivityKinds.RunCompleted, completed.Kind);
        Assert.Equal("1 refreshed, 0 failed, 0 not due", completed.Message);
        Assert.Equal(Now, await _activity.Tracker.GetLastRunAsync(DaemonKeys.TokenRefresh));
    }

    [Fact]
    public async Task TokenRefresh_NotDue_IsSkipped()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(50));

        var result = await CreateService().RefreshDueAsync(Now);

        Assert.Equal(new TokenRefreshRunResult(0, 0, 1), result);
        Assert.Empty(_acquirer.Calls);
        Assert.Equal(0, _repository.Updates);
        Assert.Equal("0 refreshed, 0 failed, 1 not due", (await ActivityAsync())[^1].Message);
    }

    [Fact]
    public async Task TokenRefresh_ManualMode_IsNeverRefreshed()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.Manual, Now.AddMinutes(-30));

        var result = await CreateService().RefreshDueAsync(Now);

        Assert.Equal(new TokenRefreshRunResult(0, 0, 0), result);
        Assert.Empty(_acquirer.Calls);
        var completed = (await ActivityAsync())[^1];
        Assert.Equal(DaemonActivityKinds.RunCompleted, completed.Kind);
        Assert.Equal(AppInsightsTokenRefreshService.NothingConfiguredMessage, completed.Message);
    }

    [Fact]
    public async Task TokenRefresh_AcquireFailure_KeepsOldTokenAndRecordsError()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(5));
        var before = _repository.Get("tenant-a");
        _acquirer.Next = () => throw new InvalidOperationException("Please run 'az login' to set up account.");

        var result = await CreateService().RefreshDueAsync(Now);

        Assert.Equal(new TokenRefreshRunResult(0, 1, 0), result);
        var row = _repository.Get("tenant-a");
        Assert.Equal(before.EncryptedToken, row.EncryptedToken);
        Assert.Equal(OldToken, Protector.Unprotect(row.EncryptedToken));
        Assert.Equal(before.ExpiresAt, row.ExpiresAt);
        Assert.Equal(0, row.RefreshCount);
        Assert.Equal("Please run 'az login' to set up account.", row.LastRefreshError);

        var entries = await ActivityAsync();
        var error = Assert.Single(entries, e => e.Level == DaemonActivityLevel.Error);
        Assert.Equal("tenant-a: refresh via azure-cli failed — Please run 'az login' to set up account.", error.Message);
        Assert.Equal("0 refreshed, 1 failed, 0 not due", entries[^1].Message);
    }

    [Fact]
    public async Task TokenRefresh_RefreshNow_ManualMode_IsRejected()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.Manual, Now.AddMinutes(-30));
        var service = CreateService();

        var result = await service.RefreshNowAsync("tenant-a");

        Assert.Equal(TokenRefreshNowStatus.ManualRejected, result.Status);
        Assert.Equal(AppInsightsTokenRefreshService.RefreshManualRejectedMessage, result.Error);
        Assert.Empty(_acquirer.Calls);
        Assert.Equal(TokenRefreshNowStatus.NotFound, (await service.RefreshNowAsync("missing")).Status);

        // The API maps the rejection to 409 and an unknown key to 404.
        var controller = new AppInsightsTokenController(
            new AppInsightsTokenService(_repository, _protector, NullLogger<AppInsightsTokenService>.Instance),
            service, new TenantResolver(),
            Options.Create(new MonitoringOptions { SettingsWriteSecret = A4Http.WriteSecret, InternalApiSecret = A4Http.InternalSecret }),
            NullLogger<AppInsightsTokenController>.Instance);
        var conflict = await controller.RefreshTokenAsync(A4Http.Request("http://localhost/api/appinsights/tokens/tenant-a/refresh", writeSecret: A4Http.WriteSecret), "tenant-a", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(AppInsightsTokenRefreshService.RefreshManualRejectedMessage, conflict.ReadJson()["error"]!.GetValue<string>());
        var notFound = await controller.RefreshTokenAsync(A4Http.Request("http://localhost/api/appinsights/tokens/missing/refresh", writeSecret: A4Http.WriteSecret), "missing", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task TokenRefresh_DeactivatedToken_IsRejectedAndSkippedByDaemon()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(5));
        _repository.Rows.Single().IsActive = false;
        var service = CreateService();

        var now = await service.RefreshNowAsync("tenant-a");
        Assert.Equal(TokenRefreshNowStatus.Inactive, now.Status);
        Assert.Equal("Token is deactivated.", now.Error);

        var run = await service.RefreshDueAsync(Now);
        Assert.Equal(new TokenRefreshRunResult(0, 0, 0), run);
        Assert.Empty(_acquirer.Calls);
        Assert.Equal(AppInsightsTokenRefreshService.NothingConfiguredMessage, (await ActivityAsync())[^1].Message);

        var controller = new AppInsightsTokenController(
            new AppInsightsTokenService(_repository, _protector, NullLogger<AppInsightsTokenService>.Instance),
            service, new TenantResolver(),
            Options.Create(new MonitoringOptions { SettingsWriteSecret = A4Http.WriteSecret, InternalApiSecret = A4Http.InternalSecret }),
            NullLogger<AppInsightsTokenController>.Instance);
        var response = await controller.RefreshTokenAsync(A4Http.Request("http://localhost/api/appinsights/tokens/tenant-a/refresh", writeSecret: A4Http.WriteSecret), "tenant-a", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Token is deactivated.", response.ReadJson()["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TokenRefresh_ConcurrentUserEdit_IsNotOverwritten()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.ServicePrincipal, Now.AddMinutes(5), clientId: "client-old", secret: ClientSecret);
        _acquirer.Next = () => ("fresh-token-9.9.9", Now.AddHours(1));
        // The user saves a new client id and secret while the token is being acquired.
        _repository.BeforeRefreshWrite = () =>
        {
            var row = _repository.Rows.Single();
            row.ClientId = "client-new";
            row.EncryptedClientSecret = Protector.Protect("new-secret-value");
        };

        var result = await CreateService().RefreshDueAsync(Now);

        Assert.Equal(new TokenRefreshRunResult(0, 0, 1), result);
        var row = _repository.Get("tenant-a");
        Assert.Equal("client-new", row.ClientId);
        Assert.Equal("new-secret-value", Protector.Unprotect(row.EncryptedClientSecret!));
        Assert.Equal(OldToken, Protector.Unprotect(row.EncryptedToken));
        Assert.Equal(0, row.RefreshCount);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("changed during refresh, skipped"));

        // Refresh now reports the race instead of overwriting.
        _repository.BeforeRefreshWrite = () => _repository.Rows.Single().AadTenantId = "99999999-0000-0000-0000-000000000000";
        var now = await CreateService().RefreshNowAsync("tenant-a");
        Assert.Equal(TokenRefreshNowStatus.Changed, now.Status);
        Assert.Equal("99999999-0000-0000-0000-000000000000", _repository.Get("tenant-a").AadTenantId);
        Assert.Equal(OldToken, Protector.Unprotect(_repository.Get("tenant-a").EncryptedToken));
    }

    [Fact]
    public async Task TokenRefresh_RefreshNow_AutomaticToken_ReturnsNewExpiry()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(50));
        var newExpiry = Now.AddMinutes(75);
        _acquirer.Next = () => ("fresh-token-2.3.4", newExpiry);

        var result = await CreateService().RefreshNowAsync("tenant-a");

        Assert.Equal(TokenRefreshNowStatus.Refreshed, result.Status);
        Assert.Equal(newExpiry, result.ExpiresAt);
        Assert.Single(_acquirer.Calls);
        Assert.Equal(1, _repository.Get("tenant-a").RefreshCount);
    }

    [Fact]
    public async Task TokenRefresh_AppInsightsTenant_MintsAppInsightsScope()
    {
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(10));

        await CreateService().RefreshDueAsync(Now);

        Assert.Equal(StoredTokenScopes.AppInsights, Assert.Single(_acquirer.Scopes));
    }

    [Theory]
    [InlineData("azure-devops")]
    [InlineData("Azure-DevOps")]
    public async Task TokenRefresh_AzureDevOpsTenant_MintsAzureDevOpsScope(string tenantKey)
    {
        AddToken(tenantKey, AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(50));

        var result = await CreateService().RefreshNowAsync(tenantKey);

        Assert.Equal(TokenRefreshNowStatus.Refreshed, result.Status);
        Assert.Equal(StoredTokenScopes.AzureDevOps, Assert.Single(_acquirer.Scopes));
    }

    [Theory]
    [InlineData("azure-devops", StoredTokenScopes.AzureDevOps)]
    [InlineData(" azure-devops ", StoredTokenScopes.AzureDevOps)]
    [InlineData("arrivia", StoredTokenScopes.AppInsights)]
    [InlineData("ice", StoredTokenScopes.AppInsights)]
    [InlineData(null, StoredTokenScopes.AppInsights)]
    public void StoredTokenScopes_ForTenantKey_PicksScope(string? tenantKey, string expected) =>
        Assert.Equal(expected, StoredTokenScopes.ForTenantKey(tenantKey));

    [Fact]
    public async Task TokenRefresh_Disabled_RecordsRunSkipped()
    {
        _options.Enabled = false;
        AddToken("tenant-a", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(1));

        var result = await CreateService().RefreshDueAsync(Now);

        Assert.True(result.Skipped);
        Assert.Empty(_acquirer.Calls);
        var entry = Assert.Single(await ActivityAsync());
        Assert.Equal(DaemonActivityKinds.RunSkipped, entry.Kind);
    }

    [Fact]
    public async Task TokenRefresh_NeverLogsSecrets()
    {
        AddToken("sp-tenant", AppInsightsTokenRefreshModes.ServicePrincipal, Now.AddMinutes(5), clientId: "client-app-id", secret: ClientSecret);
        AddToken("cli-tenant", AppInsightsTokenRefreshModes.AzureCli, Now.AddMinutes(5));
        const string newToken = "brand-new-token.payload.signature";
        var calls = 0;
        _acquirer.Next = () =>
        {
            calls++;
            // The first acquisition fails with an exception that echoes both the secret and the old token.
            if (calls == 1) throw new InvalidOperationException($"AADSTS7000215: invalid secret '{ClientSecret}' for token {OldToken}");
            return (newToken, Now.AddHours(1));
        };

        await CreateService().RefreshDueAsync(Now);

        Assert.Contains(_acquirer.Calls, c => c.Secret == ClientSecret);
        var texts = (await _activity.EntriesAsync())
            .SelectMany(e => new[] { e.Message, e.DataJson ?? "" })
            .Concat(_logger.Entries.Select(e => e.Message + " " + e.Exception))
            .Concat(_repository.Rows.Select(r => r.LastRefreshError ?? ""))
            .ToList();
        Assert.NotEmpty(texts);
        Assert.Contains(texts, t => t.Contains("[redacted]"));
        foreach (var text in texts)
        {
            Assert.DoesNotContain(ClientSecret, text);
            Assert.DoesNotContain(OldToken, text);
            Assert.DoesNotContain(newToken, text);
        }
    }

    [Fact]
    public async Task TokenRefresh_ServicePrincipal_RequiresClientIdAndSecret()
    {
        var tokenService = new AppInsightsTokenService(_repository, _protector, NullLogger<AppInsightsTokenService>.Instance);

        var noClient = await Assert.ThrowsAsync<InvalidOperationException>(() => tokenService.SaveTokenAsync(
            new AppInsightsTokenSaveRequest("sp", "ops@example.test", null, RefreshMode: "service-principal", AadTenantId: AadTenant, ClientSecret: ClientSecret)));
        Assert.Contains("clientId is required", noClient.Message);

        var noSecret = await Assert.ThrowsAsync<InvalidOperationException>(() => tokenService.SaveTokenAsync(
            new AppInsightsTokenSaveRequest("sp", "ops@example.test", null, RefreshMode: "service-principal", AadTenantId: AadTenant, ClientId: "client-app-id")));
        Assert.Contains("clientSecret is required", noSecret.Message);
        Assert.DoesNotContain(ClientSecret, noClient.Message);

        var noTenant = await Assert.ThrowsAsync<InvalidOperationException>(() => tokenService.SaveTokenAsync(
            new AppInsightsTokenSaveRequest("cli", "ops@example.test", null, RefreshMode: "azure-cli")));
        Assert.Contains("aadTenantId is required", noTenant.Message);
        Assert.Empty(_repository.Rows);

        await tokenService.SaveTokenAsync(new AppInsightsTokenSaveRequest(
            "sp", "ops@example.test", null, RefreshMode: "service-principal", AadTenantId: AadTenant, ClientId: "client-app-id", ClientSecret: ClientSecret));
        var saved = _repository.Get("sp");
        Assert.Equal(AppInsightsTokenRefreshModes.ServicePrincipal, saved.RefreshMode);
        Assert.NotEqual(ClientSecret, saved.EncryptedClientSecret);
        Assert.Equal(ClientSecret, Protector.Unprotect(saved.EncryptedClientSecret!));

        // A blank secret on update keeps the stored one.
        await tokenService.SaveTokenAsync(new AppInsightsTokenSaveRequest(
            "sp", "ops@example.test", null, RefreshMode: "service-principal", AadTenantId: AadTenant, ClientId: "client-app-id", ClientSecret: ""));
        Assert.Equal(ClientSecret, Protector.Unprotect(_repository.Get("sp").EncryptedClientSecret!));

        Assert.Empty(AppInsightsTokenRefreshModes.Validate("service-principal", AadTenant, "client-app-id", hasSecret: true));
        Assert.Equal(2, AppInsightsTokenRefreshModes.Validate("service-principal", AadTenant, null, hasSecret: false).Count);
    }
}
