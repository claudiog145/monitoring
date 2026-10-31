using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

public sealed class SqlGoldenSignalStore(IOptions<JourneyStoreOptions> storeOptions) : IGoldenSignalStore
{
    public async Task AddOrUpdateAsync(GoldenSignalSample sample, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", sample.Domain, DbType.String, size: 32);
        parameters.Add("Stage", sample.Stage, DbType.String, size: 128);
        parameters.Add("SignalType", sample.SignalType, DbType.String, size: 32);
        parameters.Add("BucketStart", sample.BucketStart, DbType.DateTimeOffset);
        parameters.Add("BucketMinutes", sample.BucketMinutes, DbType.Int32);
        parameters.Add("GoodCount", sample.GoodCount, DbType.Int32);
        parameters.Add("BadCount", sample.BadCount, DbType.Int32);
        parameters.Add("TotalCount", sample.TotalCount, DbType.Int32);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpsertGoldenSignalSample",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<GoldenSignalSample>> GetSinceAsync(string domain, string signalType, string? stage, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);
        parameters.Add("SignalType", signalType, DbType.String, size: 32);
        parameters.Add("Stage", stage, DbType.String, size: 128);
        parameters.Add("Since", since, DbType.DateTimeOffset);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<SampleRow>(new CommandDefinition(
            "monitoring.usp_GetGoldenSignalSamplesSince",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new GoldenSignalSample(domain, r.Stage, signalType, r.BucketStart, r.BucketMinutes, r.GoodCount, r.BadCount, r.TotalCount)).ToList();
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

    private sealed record SampleRow(string? Stage, DateTimeOffset BucketStart, int BucketMinutes, int GoodCount, int BadCount, int TotalCount);
}
