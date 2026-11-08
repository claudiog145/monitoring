using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Fabric;

/// <summary>
/// Business Health evidence for a triage. The Fabric hotel booking KPI source
/// (<c>dbo.vw_Alert_HotelBookingKPIs</c>) belongs to the incumbent profile only: for the incumbent,
/// only Hotels has a business source and any other domain is reported as unavailable rather than
/// guessed. Any other profile declines Fabric and is routed to its own Business KPI buckets through
/// <see cref="KpiBusinessEvidenceProvider"/> (contract v4 §5).
/// </summary>
public sealed class FabricBusinessEvidenceProvider(
    IHotelBookingKpiSource source,
    ILogger<FabricBusinessEvidenceProvider> logger,
    KpiBusinessEvidenceProvider? profileEvidence = null) : IEvidenceProvider
{
    public const string HotelsDomain = "hotels";
    public const string IncumbentOnlyReason = "the Fabric business source belongs to the incumbent profile";
    private const int MaxRows = 25;

    public string Dimension => TriageDimensions.BusinessHealth;

    public async Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        if (KpiBusinessEvidenceProvider.AppliesTo(signal))
        {
            return profileEvidence is null
                ? new EvidenceSection(Dimension, false, null, $"{IncumbentOnlyReason}; no KPI business evidence for {EvidenceProfile.KeyOf(signal)}")
                : await profileEvidence.CollectAsync(signal, windowStart, windowEnd, ct);
        }

        if (!string.Equals(signal.Domain?.Trim(), HotelsDomain, StringComparison.OrdinalIgnoreCase))
        {
            return new EvidenceSection(Dimension, false, null, $"no business source for {signal.Domain ?? "(no domain)"}");
        }

        var rows = await source.ReadAlertStatusAsync(ct);
        if (rows.Count == 0)
        {
            return new EvidenceSection(Dimension, false, null, "Fabric business source returned no rows (not configured or unavailable)");
        }

        var pacific = ResolvePacificZone();
        var inWindow = rows
            .Where(row => Overlaps(row, windowStart, windowEnd, pacific))
            .OrderBy(row => row.VacationClubID.HasValue ? 1 : 0)
            .Take(MaxRows)
            .ToList();

        if (inWindow.Count == 0)
        {
            logger.LogInformation("[Triage] Fabric returned {Rows} business row(s), none inside the evidence window.", rows.Count);
            return new EvidenceSection(Dimension, false, null, "no Fabric business rows inside the evidence window");
        }

        var data = EvidenceJson.ToElement(new
        {
            Source = HotelBookingKpiResponse.ViewName,
            Window = new { From = windowStart, To = windowEnd },
            Rows = inWindow,
        });
        return new EvidenceSection(Dimension, true, data, null);
    }

    /// <summary>Rows carry Pacific wall-clock times; a row without them cannot be placed and is kept.</summary>
    private static bool Overlaps(HotelBookingKpiResponse row, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeZoneInfo? pacific)
    {
        if (row.WindowStartPT is null || row.WindowEndPT is null || pacific is null) return true;

        var rowStart = ToUtc(row.WindowStartPT.Value, pacific);
        var rowEnd = ToUtc(row.WindowEndPT.Value, pacific);
        return rowStart < windowEnd && rowEnd > windowStart;
    }

    private static DateTimeOffset ToUtc(DateTime pacificWallClock, TimeZoneInfo pacific)
    {
        var unspecified = DateTime.SpecifyKind(pacificWallClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, pacific.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static TimeZoneInfo? ResolvePacificZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("America/Los_Angeles", out var zone)) return zone;
        return TimeZoneInfo.TryFindSystemTimeZoneById("Pacific Standard Time", out zone) ? zone : null;
    }
}
