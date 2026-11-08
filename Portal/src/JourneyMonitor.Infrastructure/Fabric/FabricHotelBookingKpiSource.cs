using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Fabric;

/// <summary>
/// Reads the Fabric service principal from App Configuration, then runs
/// <see cref="HotelBookingKpiConnection.AlertStatusSql"/> against <c>dbo.vw_Alert_HotelBookingKPIs</c>.
/// </summary>
public sealed class FabricHotelBookingKpiSource(
    IConfiguration configuration,
    ILogger<FabricHotelBookingKpiSource> logger) : IHotelBookingKpiSource
{
    public async Task<IReadOnlyList<HotelBookingKpiResponse>> ReadAlertStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = configuration[FabricWarehouseOptions.ConnectionStringKey]?.Trim();
            var clientId = configuration[FabricWarehouseOptions.ClientIdKey]?.Trim();
            var clientSecret = configuration[FabricWarehouseOptions.ClientSecretKey]?.Trim();
            var tenantId = configuration[FabricWarehouseOptions.TenantIdKey]?.Trim();

            logger.LogInformation(
                "Read Fabric warehouse settings from App Configuration: {ConnectionStringKey}={ConnectionStringState}, {ClientIdKey}={ClientIdState}, {ClientSecretKey}={ClientSecretState}, {TenantIdKey}={TenantIdState}.",
                FabricWarehouseOptions.ConnectionStringKey, State(connectionString),
                FabricWarehouseOptions.ClientIdKey, State(clientId),
                FabricWarehouseOptions.ClientSecretKey, State(clientSecret),
                FabricWarehouseOptions.TenantIdKey, State(tenantId));

            var missing = FirstMissing(
                (FabricWarehouseOptions.ConnectionStringKey, connectionString),
                (FabricWarehouseOptions.ClientIdKey, clientId),
                (FabricWarehouseOptions.ClientSecretKey, clientSecret),
                (FabricWarehouseOptions.TenantIdKey, tenantId));
            if (missing is not null)
            {
                logger.LogWarning(
                    "Read Fabric Hotel warehouse settings: App Configuration key {Key} has no value.",
                    missing);
                return [];
            }

            logger.LogInformation(
                "Read Fabric querying {View} with {Query}.",
                HotelBookingKpiResponse.ViewName,
                nameof(HotelBookingKpiConnection.AlertStatusSql));

            var sqlConnectionString = HotelBookingKpiConnection.CreateConnectionString(
                connectionString!, clientId!, clientSecret!, tenantId!);
            await using var connection = new SqlConnection(sqlConnectionString);
            await connection.OpenAsync(cancellationToken);
            var rows = (await connection.QueryAsync<HotelBookingKpiResponse>(
                new CommandDefinition(
                    HotelBookingKpiConnection.AlertStatusSql,
                    commandTimeout: 180,
                    cancellationToken: cancellationToken))).AsList();

            logger.LogInformation(
                "Read Fabric queried {View} with {Query}: {RowCount} rows. GrossBookingsStatus {GrossBookingsStatus}. CancelBookingsStatus {CancelBookingsStatus}. NetBookingsStatus {NetBookingsStatus}. GrossSalesStatus {GrossSalesStatus}. NetSalesStatus {NetSalesStatus}. MarginStatus {MarginStatus}. MarginRateStatus {MarginRateStatus}. NegMarginStatus {NegMarginStatus}. DeepNegMarginStatus {DeepNegMarginStatus}. NetBookingsDriver {NetBookingsDriver}. NetSalesDriver {NetSalesDriver}. MarginDriver {MarginDriver}. ValuePerBookingDrop {ValuePerBookingDrop}. DeepNegMarginDriver {DeepNegMarginDriver}.",
                HotelBookingKpiResponse.ViewName,
                nameof(HotelBookingKpiConnection.AlertStatusSql),
                rows.Count,
                CountStatuses(rows, row => row.GrossBookingsStatus),
                CountStatuses(rows, row => row.CancelBookingsStatus),
                CountStatuses(rows, row => row.NetBookingsStatus),
                CountStatuses(rows, row => row.GrossSalesStatus),
                CountStatuses(rows, row => row.NetSalesStatus),
                CountStatuses(rows, row => row.MarginStatus),
                CountStatuses(rows, row => row.MarginRateStatus),
                CountStatuses(rows, row => row.NegMarginStatus),
                CountStatuses(rows, row => row.DeepNegMarginStatus),
                CountStatuses(rows, row => row.NetBookingsDriver),
                CountStatuses(rows, row => row.NetSalesDriver),
                CountStatuses(rows, row => row.MarginDriver),
                CountStatuses(rows, row => row.ValuePerBookingDrop?.ToString()),
                CountStatuses(rows, row => row.DeepNegMarginDriver));
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Read Fabric Hotel booking KPI query {Query} failed for {View}: {Error}",
                nameof(HotelBookingKpiConnection.AlertStatusSql),
                HotelBookingKpiResponse.ViewName,
                ex.ToString());
            return [];
        }
    }

    private static string CountStatuses(
        IReadOnlyList<HotelBookingKpiResponse> rows,
        Func<HotelBookingKpiResponse, string?> status) =>
        string.Join(", ", rows
            .GroupBy(row => status(row) ?? "NULL", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}"));

    private static string State(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "missing" : "present";

    private static string? FirstMissing(params (string Key, string? Value)[] settings)
    {
        foreach (var (key, value) in settings)
        {
            if (string.IsNullOrWhiteSpace(value))
                return key;
        }

        return null;
    }
}
