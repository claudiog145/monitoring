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
/// dashboard.MetricPoint. Every call passes @ProfileKey (contract v6 §3): the legacy per-club surface is the
/// incumbent's, <see cref="IProfileDashboardMetricStore"/> names the company explicitly.
/// </summary>
public sealed class SqlDashboardMetricStore(IOptions<JourneyStoreOptions> storeOptions) : IDashboardMetricStore, IProfileDashboardMetricStore
{
    public Task ReplaceChartAsync(
        string domain,
        string chart,
        string window,
        string? environment,
        string? clubId,
        IReadOnlyList<DashboardMetricPoint> points,
        CancellationToken cancellationToken) =>
        ReplaceAsync(KpiProfiles.Default, domain, chart, window, environment, clubId, points, cancellationToken);

    public Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(
        string? domain,
        string? chart,
        DateTimeOffset? since,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken) =>
        ReadAsync(KpiProfiles.Default, domain, chart, since, environment, clubId, cancellationToken);

    public Task ReplaceProfileChartAsync(
        string profileKey,
        string serviceKey,
        string chart,
        string window,
        string? environment,
        IReadOnlyList<DashboardMetricPoint> points,
        CancellationToken cancellationToken) =>
        ReplaceAsync(RequireProfile(profileKey), serviceKey, chart, window, environment, null, points, cancellationToken);

    public Task<IReadOnlyList<DashboardMetricPoint>> GetProfilePointsAsync(
        string profileKey,
        string? serviceKey,
        string? chart,
        DateTimeOffset? since,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken) =>
        ReadAsync(RequireProfile(profileKey), serviceKey, chart, since, environment, clubId, cancellationToken);

    private async Task ReplaceAsync(
        string profileKey,
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
        parameters.Add("ProfileKey", profileKey, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "dashboard.usp_ReplaceChartPoints",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<DashboardMetricPoint>> ReadAsync(
        string profileKey,
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
        parameters.Add("ProfileKey", profileKey, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<DashboardMetricPoint>(new CommandDefinition(
            "dashboard.usp_GetChartPoints",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static string RequireProfile(string profileKey) =>
        string.IsNullOrWhiteSpace(profileKey)
            ? throw new ArgumentException("A profile key is required: a dashboard read or write never spans companies.", nameof(profileKey))
            : profileKey.Trim();

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