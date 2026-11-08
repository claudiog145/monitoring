using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Calls incident.usp_IngestExceptions. The grouping rules stay in Core. The incumbent path writes under
/// <see cref="KpiProfiles.Default"/>; <see cref="IngestForProfileAsync"/> writes under any company (contract v6 §4.3).
/// </summary>
public sealed class SqlIncidentStore(IOptions<JourneyStoreOptions> storeOptions) : IIncidentStore, IProfileIncidentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public Task<IncidentIngestResult> IngestAsync(IncidentIngestRequest request, CancellationToken cancellationToken) =>
        IngestForProfileAsync(KpiProfiles.Default, request, cancellationToken);

    public async Task<IncidentIngestResult> IngestForProfileAsync(
        string profileKey, IncidentIngestRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) throw new ArgumentException("A profile key is required.", nameof(profileKey));
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured. Callers must check IsConfigured first.");
        }

        var parameters = new DynamicParameters();
        parameters.Add("Domain", request.Domain, DbType.String, size: 32);
        parameters.Add("Environment", request.Environment, DbType.String, size: 32);
        parameters.Add("Window", request.Window, DbType.String, size: 16);
        parameters.Add("FallbackClubId", request.FallbackClubId, DbType.String, size: 32);
        parameters.Add("IncidentsJson", JsonSerializer.Serialize(request.Incidents, JsonOptions), DbType.String, size: -1);
        parameters.Add("QueryRunCount", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("InsertedIncidents", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("UpdatedIncidents", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("InsertedOccurrences", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("ProfileKey", profileKey.Trim(), DbType.String, size: 128);

        await using var connection = new SqlConnection(options.ConnectionString);
        var command = new CommandDefinition(
            "incident.usp_IngestExceptions",
            parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: 120,
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command);

        return new IncidentIngestResult(
            parameters.Get<int?>("QueryRunCount") ?? 0,
            parameters.Get<int?>("InsertedIncidents") ?? 0,
            parameters.Get<int?>("UpdatedIncidents") ?? 0,
            parameters.Get<int?>("InsertedOccurrences") ?? 0);
    }
}
