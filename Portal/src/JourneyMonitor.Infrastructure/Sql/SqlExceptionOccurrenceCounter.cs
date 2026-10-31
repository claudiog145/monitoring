using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>Cross-schema read against incident.Occurrence/incident.Incident - see monitoring.usp_GetExceptionOccurrenceCount.</summary>
public sealed class SqlExceptionOccurrenceCounter(IOptions<JourneyStoreOptions> storeOptions) : IExceptionOccurrenceCounter
{
    public async Task<int> CountSinceAsync(string domain, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);
        parameters.Add("Since", since, DbType.DateTimeOffset);
        parameters.Add("Until", until, DbType.DateTimeOffset);

        await using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "monitoring.usp_GetExceptionOccurrenceCount",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
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
}
