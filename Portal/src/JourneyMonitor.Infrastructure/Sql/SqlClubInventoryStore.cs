using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Reads Fusion/Hybrid clubs from <c>brand.ClubInventory</c> with per-product maturity
/// from <c>brand.ClubProductMaturityStatus</c> (no stored procedure).
/// </summary>
public sealed class SqlClubInventoryStore(IOptions<JourneyStoreOptions> storeOptions) : IClubInventoryStore
{
    private const string SelectClubsSql = """
        SELECT
            BrandName,
            ClubId,
            ClubName,
            ClientId,
            IcePartnerId,
            Site,
            FusionPlatform,
            Members,
            TelemetryLocation,
            BusinessDataLocation,
            Environment
        FROM brand.ClubInventory
        WHERE FusionPlatform IN (N'Fusion', N'Hybrid')
        ORDER BY ClubId;
        """;

    private const string SelectProductStatusSql = """
        SELECT
            cps.ClubId,
            p.Name,
            p.SortOrder,
            cps.MaturityLevelId AS MaturityStatus,
            ml.DisplayName AS MaturityStatusName
        FROM brand.ClubProductMaturityStatus AS cps
        INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId
        INNER JOIN brand.MaturityLevel AS ml ON ml.MaturityLevelId = cps.MaturityLevelId
        INNER JOIN brand.ClubInventory AS c ON c.ClubId = cps.ClubId
        WHERE c.FusionPlatform IN (N'Fusion', N'Hybrid')
        ORDER BY cps.ClubId, p.SortOrder;
        """;

    public bool IsConfigured => storeOptions.Value.IsConfigured;

    public async Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return Array.Empty<ClubInventoryRow>();

        await using var connection = new SqlConnection(storeOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var clubs = (await connection.QueryAsync<ClubInventorySqlRow>(new CommandDefinition(
            SelectClubsSql,
            cancellationToken: cancellationToken))).ToList();

        var productRows = (await connection.QueryAsync<ClubProductSqlRow>(new CommandDefinition(
            SelectProductStatusSql,
            cancellationToken: cancellationToken))).ToList();

        var byClub = productRows
            .GroupBy(p => p.ClubId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ClubProductStatus>)g
                    .OrderBy(x => x.SortOrder)
                    .Select(x => new ClubProductStatus(
                        x.Name ?? "",
                        x.MaturityStatus,
                        x.MaturityStatusName ?? "Not Observed"))
                    .ToList());

        return clubs
            .Select(r => new ClubInventoryRow(
                byClub.TryGetValue(r.ClubId, out var products) ? products : Array.Empty<ClubProductStatus>(),
                r.BrandName ?? "",
                r.ClubId,
                r.ClubName,
                r.ClientId,
                r.IcePartnerId,
                r.Site,
                r.FusionPlatform,
                r.Members,
                r.TelemetryLocation,
                r.BusinessDataLocation ?? "",
                r.Environment ?? ""))
            .ToList();
    }

    private sealed class ClubInventorySqlRow
    {
        public string? BrandName { get; init; }
        public int ClubId { get; init; }
        public string? ClubName { get; init; }
        public int? ClientId { get; init; }
        public string? IcePartnerId { get; init; }
        public string? Site { get; init; }
        public string? FusionPlatform { get; init; }
        public int Members { get; init; }
        public string? TelemetryLocation { get; init; }
        public string? BusinessDataLocation { get; init; }
        public string? Environment { get; init; }
    }

    private sealed class ClubProductSqlRow
    {
        public int ClubId { get; init; }
        public string? Name { get; init; }
        public int SortOrder { get; init; }
        public int MaturityStatus { get; init; }
        public string? MaturityStatusName { get; init; }
    }
}
