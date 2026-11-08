using System.Net;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.TokenRefresh;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Spec1000.A4;
using JourneyMonitor.Tests.Spec1000.D1;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring.TokenRefresh;

/// <summary>
/// The Functions token endpoints fail closed: writes need X-Settings-Write-Secret, reads need
/// X-Internal-Api-Secret, and a request without (or with a wrong) header changes nothing.
/// </summary>
public sealed class AppInsightsTokenApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private const string AadTenant = "11111111-2222-3333-4444-555555555555";
    private const string Base = "http://localhost/api/appinsights/tokens";

    private readonly InMemoryTokenRepository _repository = new();
    private readonly FakeTokenAcquirer _acquirer = new();
    private readonly ITokenSecretProtector _protector = new DataProtectionTokenSecretProtector(new EphemeralDataProtectionProvider());
    private readonly AppInsightsTokenController _controller;

    public AppInsightsTokenApiTests()
    {
        var time = new D1FixedTimeProvider(Now);
        var refresh = new AppInsightsTokenRefreshService(
            _repository, _protector, _acquirer, Options.Create(new TokenRefreshOptions()), time,
            NullLogger<AppInsightsTokenRefreshService>.Instance);
        _controller = new AppInsightsTokenController(
            new AppInsightsTokenService(_repository, _protector, NullLogger<AppInsightsTokenService>.Instance),
            refresh,
            new TenantResolver(),
            Options.Create(new MonitoringOptions { SettingsWriteSecret = A4Http.WriteSecret, InternalApiSecret = A4Http.InternalSecret }),
            NullLogger<AppInsightsTokenController>.Instance);

        _repository.SaveAsync(new AppInsightsToken
        {
            TenantKey = "cli",
            Email = "ops@example.test",
            EncryptedToken = _protector.Protect("old-token-value"),
            ExpiresAt = Now.AddMinutes(5).UtcDateTime,
            CreatedAt = Now.UtcDateTime,
            IsActive = true,
            RefreshMode = AppInsightsTokenRefreshModes.AzureCli,
            AadTenantId = AadTenant,
        }).GetAwaiter().GetResult();
    }

    private const string SaveBody = """{"tenantKey":"new-key","email":"ops@example.test","refreshMode":"azure-cli","aadTenantId":"11111111-2222-3333-4444-555555555555"}""";

    [Fact]
    public async Task TokenApi_SaveWithoutSecret_Returns401()
    {
        var missing = await _controller.SaveTokenAsync(A4Http.Request(Base, SaveBody), CancellationToken.None);
        var wrong = await _controller.SaveTokenAsync(A4Http.Request(Base, SaveBody, writeSecret: "wrong"), CancellationToken.None);
        var internalOnly = await _controller.SaveTokenAsync(A4Http.Request(Base, SaveBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, internalOnly.StatusCode);
        Assert.DoesNotContain(_repository.Rows, r => r.TenantKey == "new-key");
    }

    [Fact]
    public async Task TokenApi_RefreshWithoutSecret_Returns401()
    {
        var missing = await _controller.RefreshTokenAsync(A4Http.Request($"{Base}/cli/refresh"), "cli", CancellationToken.None);
        var wrong = await _controller.RefreshTokenAsync(A4Http.Request($"{Base}/cli/refresh", writeSecret: "wrong"), "cli", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(_acquirer.Calls);
    }

    [Fact]
    public async Task TokenApi_DeactivateWithoutSecret_Returns401()
    {
        var missing = await _controller.DeactivateTokenAsync(A4Http.Request($"{Base}/cli/deactivate"), "cli");
        var wrong = await _controller.DeactivateTokenAsync(A4Http.Request($"{Base}/cli/deactivate", writeSecret: "wrong"), "cli");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.True(_repository.Get("cli").IsActive);
    }

    [Fact]
    public async Task TokenApi_ListWithoutInternalSecret_Returns401()
    {
        var missing = await _controller.GetTokensAsync(A4Http.Request(Base));
        var wrong = await _controller.GetTokensAsync(A4Http.Request(Base, internalSecret: "wrong"));
        var writeOnly = await _controller.GetTokensAsync(A4Http.Request(Base, writeSecret: A4Http.WriteSecret));
        var status = await _controller.GetTokenStatusAsync(A4Http.Request($"{Base}/cli/status"), "cli");
        var tenants = await _controller.GetTenantsAsync(A4Http.Request("http://localhost/api/appinsights/tenants"));

        Assert.All([missing, wrong, writeOnly, status, tenants], r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.DoesNotContain("ops@example.test", missing.ReadJson().ToJsonString());
    }

    [Fact]
    public async Task TokenApi_WithSecrets_Succeeds()
    {
        var list = await _controller.GetTokensAsync(A4Http.Request(Base, internalSecret: A4Http.InternalSecret));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listJson = list.ReadJson().ToJsonString();
        Assert.Contains("\"tenantKey\":\"cli\"", listJson);
        Assert.Contains("\"refreshMode\":\"azure-cli\"", listJson);
        Assert.DoesNotContain("encryptedToken", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old-token-value", listJson);

        var status = await _controller.GetTokenStatusAsync(A4Http.Request($"{Base}/cli/status", internalSecret: A4Http.InternalSecret), "cli");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var save = await _controller.SaveTokenAsync(A4Http.Request(Base, SaveBody, writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(AppInsightsTokenRefreshModes.AzureCli, _repository.Get("new-key").RefreshMode);

        _acquirer.Next = () => ("fresh-token-value", Now.AddHours(1));
        var refresh = await _controller.RefreshTokenAsync(A4Http.Request($"{Base}/cli/refresh", writeSecret: A4Http.WriteSecret), "cli", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal("fresh-token-value", _protector.Unprotect(_repository.Get("cli").EncryptedToken));

        var deactivate = await _controller.DeactivateTokenAsync(A4Http.Request($"{Base}/cli/deactivate", writeSecret: A4Http.WriteSecret), "cli");
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.False(_repository.Get("cli").IsActive);

        var refreshInactive = await _controller.RefreshTokenAsync(A4Http.Request($"{Base}/cli/refresh", writeSecret: A4Http.WriteSecret), "cli", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, refreshInactive.StatusCode);
    }

    [Fact]
    public async Task TokenApi_UnconfiguredSecrets_FailClosed()
    {
        var controller = new AppInsightsTokenController(
            new AppInsightsTokenService(_repository, _protector, NullLogger<AppInsightsTokenService>.Instance),
            new AppInsightsTokenRefreshService(_repository, _protector, _acquirer, Options.Create(new TokenRefreshOptions()),
                new D1FixedTimeProvider(Now), NullLogger<AppInsightsTokenRefreshService>.Instance),
            new TenantResolver(),
            Options.Create(new MonitoringOptions()),
            NullLogger<AppInsightsTokenController>.Instance);

        var list = await controller.GetTokensAsync(A4Http.Request(Base, internalSecret: ""));
        var save = await controller.SaveTokenAsync(A4Http.Request(Base, SaveBody, writeSecret: ""), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, save.StatusCode);
    }
}
