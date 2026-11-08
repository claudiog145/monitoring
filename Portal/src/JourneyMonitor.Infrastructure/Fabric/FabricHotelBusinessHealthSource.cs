using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Fabric;

/// <summary>
/// Reads one HOUR row from <c>dbo.vw_Alert_HotelBookingKPIs</c> for the Reliability Watch card.
/// A null club id is the TOTAL row. The portal SQL store is not used.
/// </summary>
public sealed class FabricHotelBusinessHealthSource(
    IConfiguration configuration,
    ILogger<FabricHotelBusinessHealthSource> logger) : IHotelBusinessHealthSource
{
    public const string SourceName = "fabric";

    private const string PacificZoneId = "America/Los_Angeles";

    public async Task<HotelBusinessHealth?> ReadAsync(
        string environment,
        long? vacationClubId,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = configuration[FabricWarehouseOptions.ConnectionStringKey]?.Trim();
            var clientId = configuration[FabricWarehouseOptions.ClientIdKey]?.Trim();
            var clientSecret = configuration[FabricWarehouseOptions.ClientSecretKey]?.Trim();
            var tenantId = configuration[FabricWarehouseOptions.TenantIdKey]?.Trim();
            if (string.IsNullOrWhiteSpace(connectionString)
                || string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(clientSecret)
                || string.IsNullOrWhiteSpace(tenantId))
            {
                logger.LogWarning("Hotel business health skipped: a Fabric warehouse setting is missing.");
                return null;
            }

            var sqlConnectionString = HotelBookingKpiConnection.CreateConnectionString(
                connectionString, clientId, clientSecret, tenantId);
            await using var connection = new SqlConnection(sqlConnectionString);
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<BusinessHealthRow>(
                new CommandDefinition(
                    HotelBookingKpiConnection.BusinessHealthSql,
                    new { VacationClubID = vacationClubId },
                    commandTimeout: 180,
                    cancellationToken: cancellationToken));
            if (row is null)
            {
                logger.LogInformation(
                    "Hotel business health query returned no HOUR row. Club {VacationClubId}.",
                    vacationClubId?.ToString() ?? "TOTAL");
                return null;
            }

            var wall = DateTime.SpecifyKind(row.WindowStartPT ?? DateTime.UtcNow, DateTimeKind.Unspecified);
            var windowStart = PacificTime(wall);
            return new HotelBusinessHealth(
                SourceName,
                "hotel",
                environment,
                "Hour",
                row.VacationClubID ?? 0,
                Current: new HotelBusinessHealthReading(
                    windowStart,
                    ConversionPercent: null,
                    row.NetBookingsCurrent ?? 0,
                    row.NetSalesCurrent ?? 0,
                    row.CancelBookingsCurrent ?? 0),
                PriorWeek: new HotelBusinessHealthReading(
                    PacificTime(wall.AddDays(-7)),
                    ConversionPercent: null,
                    row.NetBookingsW1 ?? 0,
                    row.NetSalesW1 ?? 0,
                    row.CancelBookingsW1 ?? 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Hotel business health query failed: {Error}", ex.Message);
            return null;
        }
    }

    private static DateTimeOffset PacificTime(DateTime wall)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(PacificZoneId);
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private sealed class BusinessHealthRow
    {
        public DateTime? WindowStartPT { get; init; }

        public long? VacationClubID { get; init; }

        public int? NetBookingsCurrent { get; init; }

        public int? NetBookingsW1 { get; init; }

        public int? CancelBookingsCurrent { get; init; }

        public int? CancelBookingsW1 { get; init; }

        public decimal? NetSalesCurrent { get; init; }

        public decimal? NetSalesW1 { get; init; }
    }
}
