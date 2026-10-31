namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Read-only access to <c>brand.ClubInventory</c> (the Club and Fusion application map).
/// </summary>
public interface IClubInventoryStore
{
    /// <summary>False when JourneyStore has no connection string.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>One product link for a club, with maturity status 0-3.</summary>
public sealed record ClubProductStatus(
    string Name,
    int MaturityStatus,
    string MaturityStatusName);

/// <summary>One row of the club application map (club identity + per-product status).</summary>
public sealed record ClubInventoryRow(
    IReadOnlyList<ClubProductStatus> Products,
    string BrandName,
    int ClubId,
    string? ClubName,
    int? ClientId,
    string? IcePartnerId,
    string? Site,
    string? FusionPlatform,
    int Members,
    string? TelemetryLocation,
    string BusinessDataLocation,
    string Environment);
