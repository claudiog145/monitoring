using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>monitoring.IngestionWatermark. The watermark only moves forward: an older value is ignored.</summary>
public sealed class SqlIngestionWatermarkStore(IOptions<JourneyStoreOptions> storeOptions) : IIngestionWatermarkStore
{
    private const int DeadlockErrorNumber = 1205;
    private const int MaxDeadlockRetries = 3;

    public async Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        await using var connection = SqlKpiConnection.Open(storeOptions);
        return await ReadAsync(connection, null, kpiDefinitionId, ct);
    }

    public async Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default)
    {
        // usp_SetIngestionWatermark overwrites unconditionally and overlapping ingestion runs (timer and
        // the on-demand route) can race, so the read-compare-write runs in one SERIALIZABLE transaction:
        // the read's key-range lock blocks any other writer until commit. Two readers that both hold the
        // shared lock and then try to write deadlock; SQL Server kills one and it is retried.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SetOnceAsync(kpiDefinitionId, watermark, ct);
                return;
            }
            catch (SqlException ex) when (ex.Number == DeadlockErrorNumber && attempt < MaxDeadlockRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 80) * (attempt + 1)), ct);
            }
        }
    }

    private async Task SetOnceAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct)
    {
        await using var connection = SqlKpiConnection.Open(storeOptions);
        await connection.OpenAsync(ct);
        try
        {
            await SetInTransactionAsync(connection, kpiDefinitionId, watermark, ct);
        }
        finally
        {
            // A pooled connection keeps the isolation level of its last transaction; left at
            // SERIALIZABLE, the next borrower's READPAST queue claim fails (error 650).
            if (connection.State == ConnectionState.Open)
            {
                await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", cancellationToken: CancellationToken.None));
            }
        }
    }

    private async Task SetInTransactionAsync(SqlConnection connection, int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var current = await ReadAsync(connection, transaction, kpiDefinitionId, ct);
        if (current is { } stored && watermark <= stored)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);
        parameters.Add("Watermark", watermark, DbType.DateTimeOffset);
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_SetIngestionWatermark", parameters, transaction, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    private static async Task<DateTimeOffset?> ReadAsync(SqlConnection connection, SqlTransaction? transaction, int kpiDefinitionId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);
        return await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "monitoring.usp_GetIngestionWatermark", parameters, transaction, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }
}
