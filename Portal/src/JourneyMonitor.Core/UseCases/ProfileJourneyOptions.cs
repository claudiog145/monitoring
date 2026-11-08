using System.Globalization;
using System.Text.RegularExpressions;

namespace JourneyMonitor.Core.UseCases;

/// <summary>Contract v6 §4.1 sync and read limits (configuration section "ProfileJourneys").</summary>
public sealed class ProfileJourneyOptions
{
    public const string SectionName = "ProfileJourneys";

    /// <summary>Turns the per-profile sync timer off without removing the read endpoints.</summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>How far back each sync run reads telemetry (m/h/d token).</summary>
    public string SyncWindow { get; set; } = "30m";

    /// <summary>Per-run cap: journeys kept per service of a profile.</summary>
    public int MaxJourneysPerService { get; set; } = 500;

    /// <summary>Per-run cap on telemetry rows read for one profile.</summary>
    public int MaxRequestsPerRun { get; set; } = 20000;

    public int RetentionDays { get; set; } = 8;

    /// <summary>How long a GET /api/entities answer is reused.</summary>
    public int EntityCacheMinutes { get; set; } = 5;

    public TimeSpan GetSyncWindow() =>
        ProfileJourneyRequestParser.TryParseWindow(SyncWindow, out var window) ? window : TimeSpan.FromMinutes(30);
}

/// <summary>Validation of the query string of the §4.1/§4.2 endpoints, so Functions only validate, dispatch and map.</summary>
public static class ProfileJourneyRequestParser
{
    public static readonly TimeSpan DefaultJourneyWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultEntityWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(8);
    public const int DefaultJourneyTop = 200;
    public const int MaxJourneyTop = 1000;
    public const int DefaultEntityTop = 50;
    public const int MaxEntityTop = 500;

    private static readonly Regex WindowPattern = new(@"^(\d{1,4})(m|h|d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new(@"^[^\x00-\x1F<>""]{1,256}$", RegexOptions.CultureInvariant);

    /// <summary>Window tokens such as 15m, 1h, 24h, 7d; zero and anything above <see cref="MaxWindow"/> are rejected.</summary>
    public static bool TryParseWindow(string? value, out TimeSpan window)
    {
        window = default;
        var match = WindowPattern.Match((value ?? "").Trim());
        if (!match.Success) return false;
        var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        window = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
        return window > TimeSpan.Zero && window <= MaxWindow;
    }

    /// <summary>Missing → <paramref name="fallback"/>; present but invalid → error message.</summary>
    public static string? ParseWindow(string? value, TimeSpan fallback, out TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(value)) { window = fallback; return null; }
        return TryParseWindow(value, out window) ? null : "win must look like 15m, 1h, 24h or 7d (at most 8d).";
    }

    /// <summary>Missing → <paramref name="fallback"/>; present but invalid → error message.</summary>
    public static string? ParseTop(string? value, int fallback, int max, out int top)
    {
        top = fallback;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= max)
        {
            top = n;
            return null;
        }
        return $"top must be an integer between 1 and {max}.";
    }

    /// <summary>Trimmed filter value, or null when absent; an error when it has characters a key never has.</summary>
    public static string? ParseKey(string? value, string name, out string? key)
    {
        key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (key is null || KeyPattern.IsMatch(key)) return null;
        key = null;
        return $"{name} has invalid characters.";
    }
}
