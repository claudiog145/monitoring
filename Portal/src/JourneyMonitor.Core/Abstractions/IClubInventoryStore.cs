namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Read-only access to <c>brand.ClubInventory</c> (Application Map inventory).
/// </summary>
public interface IClubInventoryStore
{
    /// <summary>False when JourneyStore has no connection string.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>One product on a club, with maturity status 0–3.</summary>
public sealed record ClubProductStatus(
    string Name,
    int MaturityStatus,
    string MaturityStatusName);

/// <summary>
/// One Application Map club row (identity + per-product maturity).
/// <see cref="FusionPlatform"/> is the platform family (Fusion, Hybrid, SOR, NG);
/// the property/column name is historical.
/// </summary>
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
