using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Reads start-to-complete hotel conversion from <c>hotel.Journey</c>.
/// A booking starts when the member reaches stage 07 Booking or a CompletePurchase
/// result is stored (<c>BookOk</c> / <c>BookFail</c>). It completes when
/// <c>IsBookSuccessful</c> is set. The journey row is the member's latest state, so a
/// member active again after last week's hour is counted in the later hour only.
/// When that prior hour has no booking starts (the journey sync did not run that day),
/// the same weekday and clock hour 14, 21, or 28 days earlier is used instead.
/// A null club id is every club.
/// </summary>
public sealed class SqlHotelBookingConversionSource(
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<SqlHotelBookingConversionSource> logger) : IHotelBookingConversionSource
{
    public const string SourceName = "sql";

    /// <summary>Stage 07 Booking, or a stored CompletePurchase success or failure.</summary>
    public const string StartedPredicate =
        """
        (
            j.IsBookSuccessful = 1
            OR j.BookFail > 0
            OR EXISTS (
                SELECT 1
                FROM hotel.JourneyStep AS s
                WHERE s.JourneyId = j.JourneyId
                  AND s.StageNo = 7
            )
        )
        """;

    public const string CountSql =
        $"""
        SELECT
            COUNT(*) AS Started,
            ISNULL(SUM(CASE WHEN j.IsBookSuccessful = 1 THEN 1 ELSE 0 END), 0) AS Completed
        FROM hotel.Journey AS j
        INNER JOIN hotel.QueryRun AS q ON q.QueryRunId = j.QueryRunId
        WHERE q.Environment = @Environment
          AND (@ClubId IS NULL OR j.ClubId = @ClubId)
          AND j.LastSeen >= @Since
          AND j.LastSeen < @Until
          AND {StartedPredicate}
        """;

    public const string RecentSql =
        """
        SELECT TOP (10)
            j.LastSeen,
            j.IsBookSuccessful AS Completed,
            j.ClubId
        FROM hotel.Journey AS j
        INNER JOIN hotel.QueryRun AS q ON q.QueryRunId = j.QueryRunId
        WHERE q.Environment = @Environment
          AND (@ClubId IS NULL OR j.ClubId = @ClubId)
          AND j.IsBookSuccessful = 1
        ORDER BY j.LastSeen DESC, j.JourneyId DESC
        """;

    public async Task<HotelBookingConversionSnapshot?> ReadAsync(
        string environment,
        long? vacationClubId,
        CancellationToken cancellationToken)
    {
        var connectionString = storeOptions.Value.ConnectionString?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("Hotel booking conversion skipped: the journey store connection string is missing.");
            return null;
        }

            var current = HotelBookingHours.Containing(DateTimeOffset.UtcNow);
            var clubId = vacationClubId?.ToString();

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                var currentCount = await CountAsync(connection, environment, clubId, current, cancellationToken);
                var (prior, priorCount) = await PriorCountAsync(connection, environment, clubId, current, cancellationToken);
                var recent = await connection.QueryAsync<RecentRow>(new CommandDefinition(
                RecentSql,
                new { Environment = environment, ClubId = clubId },
                cancellationToken: cancellationToken));

            return new HotelBookingConversionSnapshot(
                SourceName,
                current.Start,
                prior.Start,
                HotelBookingConversion.Percent(currentCount.Started, currentCount.Completed),
                HotelBookingConversion.Percent(priorCount.Started, priorCount.Completed),
                HotelBookingConversion.OldestFirst(recent.Select(row => new HotelBookingConversionOperation(
                    row.LastSeen,
                    row.Completed,
                    string.IsNullOrWhiteSpace(row.ClubId) ? null : row.ClubId.Trim()))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Hotel booking conversion query failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The same Pacific hour seven days ago when it has booking starts. Otherwise the nearest
    /// earlier same weekday and hour, up to four weeks back. An empty candidate stays null
    /// rather than being reported as 0%.
    /// </summary>
    private static async Task<(HourWindow Window, CountRow Count)> PriorCountAsync(
        SqlConnection connection,
        string environment,
        string? clubId,
        HourWindow current,
        CancellationToken cancellationToken)
    {
        HourWindow? chosen = null;
        CountRow? chosenCount = null;
        foreach (var candidate in HotelBookingHours.HistoricalCandidates(current))
        {
            var count = await CountAsync(connection, environment, clubId, candidate, cancellationToken);
            chosen ??= candidate;
            chosenCount ??= count;
            if (count.Started > 0)
                return (candidate, count);
        }

        return (chosen!.Value, chosenCount!);
    }

    private static async Task<CountRow> CountAsync(
        SqlConnection connection,
        string environment,
        string? clubId,
        HourWindow window,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleAsync<CountRow>(new CommandDefinition(
            CountSql,
            new { Environment = environment, ClubId = clubId, Since = window.Start, Until = window.End },
            cancellationToken: cancellationToken));
    }

    private sealed class CountRow
    {
        public int Started { get; init; }

        public int Completed { get; init; }
    }

    private sealed class RecentRow
    {
        public DateTimeOffset LastSeen { get; init; }

        public bool Completed { get; init; }

        public string? ClubId { get; init; }
    }
}
