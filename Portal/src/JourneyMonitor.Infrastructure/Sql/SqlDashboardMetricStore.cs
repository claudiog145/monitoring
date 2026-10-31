using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

public sealed class SqlDashboardMetricStore(IOptions<JourneyStoreOptions> storeOptions) : IDashboardMetricStore
{
    public async Task ReplaceChartAsync(
        string domain,
        string chart,
        string window,
        string? environment,
        string? clubId,
        IReadOnlyList<DashboardMetricPoint> points,
        CancellationToken cancellationToken)
    {
        var payload = points.Select(p => new
        {
            binStart = p.BinStart,
            series = p.Series,
            value = p.Value,
        });

        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);
        parameters.Add("Chart", chart, DbType.String, size: 64);
        parameters.Add("Window", window, DbType.String, size: 16);
        parameters.Add("Environment", environment, DbType.String, size: 32);
        parameters.Add("ClubId", clubId, DbType.String, size: 32);
        parameters.Add("RowsJson", JsonSerializer.Serialize(payload), DbType.String, size: -1);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "dashboard.usp_ReplaceChartPoints",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(
        string? domain,
        string? chart,
        DateTimeOffset? since,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);
        parameters.Add("Chart", chart, DbType.String, size: 64);
        parameters.Add("Since", since, DbType.DateTimeOffset);
        parameters.Add("Environment", environment, DbType.String, size: 32);
        parameters.Add("ClubId", clubId, DbType.String, size: 32);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<DashboardMetricPoint>(new CommandDefinition(
            "dashboard.usp_GetChartPoints",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }
}
