namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One row of <c>brand.Information</c> (platformDB): a club id, the brand it belongs to, the
/// ICE partner id used by Air/Cruise telemetry instead of the club id, and the club's web host.
/// </summary>
public sealed record BrandClub(string BrandName, string ClubId, string? IcePartnerId, string? Url);

/// <summary>
/// Source of the Club dropdown (GET /api/clubs) and of club-id -> host resolution: the
/// <c>brand.Information</c> table in the platform SQL database (see Sql/CreateBrandTables.sql).
/// Backed by the same connection string as <see cref="IJourneyStore"/>; when that is not
/// configured, <see cref="IsConfigured"/> is false and callers fall back to the built-in demo
/// club list instead of failing.
/// </summary>
public interface IBrandCatalog
{
    bool IsConfigured { get; }

    /// <summary>Every brand/club row, ordered by brand name then club id.</summary>
    Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken);
}
