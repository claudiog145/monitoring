using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed active-company pointer (monitoring.ActiveCompanyProfile). Same Dapper shape as
/// <see cref="SqlCompanyProfileStore"/>, one single-row table behind it.
///
/// Nothing is validated here: usp_SetActiveCompanyProfile refuses a key that is not a registered,
/// enabled profile, so the rule holds for a write made in SSMS too and cannot drift between the
/// two callers that would otherwise each own a copy of it.
/// </summary>
public sealed class SqlActiveCompanyProfileStore(IOptions<JourneyStoreOptions> storeOptions) : IActiveCompanyProfileStore
{
    public async Task<string?> GetActiveAsync(CancellationToken ct = default) =>
        (await GetActiveDetailAsync(ct))?.ProfileKey;

    public async Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ActiveRow>(new CommandDefinition(
            "monitoring.usp_GetActiveCompanyProfile", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", profileKey, DbType.String, size: 128);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        // ExecuteAsync, not Query: the proc's trailing SELECT exists for a human running it by
        // hand, and the caller re-reads through GetActiveDetailAsync anyway.
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_SetActiveCompanyProfile", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    /// <summary>
    /// Whether monitoring.ActiveCompanyProfile exists on that database. The table ships after
    /// monitoring.CompanyProfile, so an environment upgraded before this migration ran would pass
    /// MonitoringSchemaGate.IsCompanyProfileProvisioned and then fail on a missing table in front
    /// of EVERY classification - the read sits on the unnamed-profile path. Degrading to "no
    /// active company" instead leaves Company:DefaultProfileKey in charge, which is exactly the
    /// behaviour that shipped before this feature.
    ///
    /// Lives here rather than with its siblings in MonitoringSchemaGate only because that file
    /// was out of scope for this change; it belongs there.
    /// </summary>
    internal static bool IsProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.ActiveCompanyProfile', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    /// <summary>Exactly the three columns usp_GetActiveCompanyProfile selects - see the Dapper note on SqlCompanyProfileStore.</summary>
    private sealed record ActiveRow(string ProfileKey, DateTimeOffset UpdatedAt, string? UpdatedBy)
    {
        public ActiveCompanyProfile ToDomain() => new(ProfileKey, UpdatedAt, UpdatedBy);
    }
}
