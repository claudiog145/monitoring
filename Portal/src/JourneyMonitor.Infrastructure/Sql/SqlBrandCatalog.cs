using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Reads <c>brand.Information</c> (Sql/CreateBrandTables.sql) from the platform database that
/// also backs the journey store. Returns an empty list when no connection string is configured
/// so GetClubsService can fall back to the demo club list instead of throwing.
/// </summary>
public sealed class SqlBrandCatalog(IOptions<JourneyStoreOptions> storeOptions) : IBrandCatalog
{
    private const string SelectAllSql =
        "SELECT BrandName, ClubId, IcePartnerId, Url FROM brand.Information ORDER BY BrandName, ClubId;";

    public bool IsConfigured => storeOptions.Value.IsConfigured;

    public async Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return Array.Empty<BrandClub>();

        await using var connection = new SqlConnection(storeOptions.Value.ConnectionString);
        var rows = await connection.QueryAsync<BrandInformationRow>(new CommandDefinition(
            SelectAllSql,
            cancellationToken: cancellationToken));

        return rows
            .Select(r => new BrandClub(r.BrandName ?? "", r.ClubId.ToString(), r.IcePartnerId, r.Url))
            .ToList();
    }

    private sealed record BrandInformationRow(string? BrandName, int ClubId, string? IcePartnerId, string? Url);
}
