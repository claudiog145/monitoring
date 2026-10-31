using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker.Http;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Shared fixed-time secret header check, reused by every Function endpoint that needs to fail
/// CLOSED when its configured secret is missing/blank rather than skip the check - Settings tab
/// mutations (X-Settings-Write-Secret) and internal-only reads/outbound-call endpoints
/// (X-Internal-Api-Secret, see MonitoringOptions.InternalApiSecret).
/// </summary>
internal static class SettingsWriteSecretGuard
{
    internal const string WriteSecretHeaderName = "X-Settings-Write-Secret";
    internal const string InternalApiSecretHeaderName = "X-Internal-Api-Secret";

    internal static bool HasMatchingWriteSecret(HttpRequestData req, string? requiredSecret) =>
        HasMatchingSecret(req, requiredSecret, WriteSecretHeaderName);

    internal static bool HasMatchingSecret(HttpRequestData req, string? requiredSecret, string headerName)
    {
        if (string.IsNullOrWhiteSpace(requiredSecret)) return false;
        if (!req.Headers.TryGetValues(headerName, out var values)) return false;
        var provided = values.FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;

        // Fixed-time comparison - this guards a write to an audit trail, not just a read.
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var requiredBytes = Encoding.UTF8.GetBytes(requiredSecret);
        return providedBytes.Length == requiredBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, requiredBytes);
    }
}
