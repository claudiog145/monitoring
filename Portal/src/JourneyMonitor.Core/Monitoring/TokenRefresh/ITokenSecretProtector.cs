namespace JourneyMonitor.Core.Monitoring.TokenRefresh;

/// <summary>
/// Encrypts stored App Insights tokens and client secrets at rest. Hosts back it with ASP.NET Core
/// DataProtection (purpose <see cref="AppInsightsTokenService.ProtectorPurpose"/>) so Web and Functions
/// decrypt each other's values; Core never references the host stack.
/// </summary>
public interface ITokenSecretProtector
{
    string Protect(string plaintext);

    /// <summary>Throws when the value was not produced by this protector (or its key is gone).</summary>
    string Unprotect(string protectedValue);
}
