using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.TokenRefresh;
using Microsoft.AspNetCore.DataProtection;

namespace JourneyMonitor.Infrastructure.AppInsights;

/// <summary>DataProtection adapter for <see cref="ITokenSecretProtector"/>: same purpose string the token store always used.</summary>
public sealed class DataProtectionTokenSecretProtector(IDataProtectionProvider provider) : ITokenSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector(AppInsightsTokenService.ProtectorPurpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
