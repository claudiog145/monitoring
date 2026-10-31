using System.Globalization;

namespace JourneyMonitor.Core.Options;

/// <summary>
/// What the timer-triggered sync queries on each run. The HTTP journeys endpoint takes these per
/// request; the timer has no caller, so they come from configuration (section "JourneySync").
/// The timer stores <see cref="FilteredRowCap"/> rows. The portal's first read uses
/// <see cref="InitialRowCap"/>, then widens to <see cref="FilteredRowCap"/> after a filter change.
/// </summary>
public sealed class JourneySyncOptions
{
    public const string SectionName = "JourneySync";

    /// <summary>
    /// Comma-separated domains to sync per run. A string rather than a list because configuration
    /// binding APPENDS to a non-empty collection default instead of replacing it, so "hotels" in
    /// config on top of a ["hotels"] default silently syncs the same domain twice.
    /// </summary>
    public string Domains { get; set; } = "hotels,cruises";

    public IEnumerable<string> EnumerateDomains() =>
        Domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public string Window { get; set; } = "15m";

    public string Environment { get; set; } = "production";

    /// <summary>
    /// Fallback club when <c>brand.Information</c> cannot be read. The timer otherwise syncs
    /// every club the dropdown lists. Hotels and cars default to T-Mobile (15639).
    /// </summary>
    public string ClubId { get; set; } = "15639";

    /// <summary>
    /// First Customer Journeys read, and the cap used when a request sends any other value.
    /// </summary>
    public int InitialRowCap { get; set; } = 50;

    /// <summary>
    /// Rows stored by the timer and by refresh, and the read cap after the first filter change.
    /// </summary>
    public int FilteredRowCap { get; set; } = 200;

    /// <summary>Accepts only the two configured caps. Anything else becomes <see cref="InitialRowCap"/>.</summary>
    public int NormalizeRequestedRowCap(string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && (n == InitialRowCap || n == FilteredRowCap))
        {
            return n;
        }

        return InitialRowCap > 0 ? InitialRowCap : 50;
    }
}
