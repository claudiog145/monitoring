using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Counts the seven Executive Overview tiles from the journey and incident tables.
/// Platform Availability always uses the last 24 hours and the All domains (Hotels and Cruises).
/// The other journey tiles use the filters on <see cref="OverviewReadQuery"/>.
/// A null domain is All, which does not query the car or air schemas.
/// </summary>
public sealed class SqlOverviewReader(IOptions<JourneyStoreOptions> storeOptions) : IOverviewReader
{
    private static readonly (string Domain, string Schema)[] Domains =
    [
        ("hotels", "hotel"),
        ("cars", "car"),
        ("cruises", "cruise"),
        ("air", "air"),
    ];

    /// <summary>SQL literal list for an unspecified domain. Matches <see cref="PortalDomains.All"/>.</summary>
    private static readonly string AllDomainSql =
        string.Join(", ", PortalDomains.All.Select(domain => $"N'{domain}'"));

    public async Task<OverviewCounts> ReadAsync(OverviewReadQuery query, CancellationToken cancellationToken)
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var availability = await SumJourneysAsync(
            connection, query.AvailabilitySince, query.Environment, null, null, null, cancellationToken);
        var filtered = await SumJourneysAsync(
            connection,
            query.FilteredSince,
            query.Environment,
            query.Domain,
            query.ClubId,
            query.SearchId,
            cancellationToken);
        var incidents = await ReadIncidentsAsync(connection, query, cancellationToken);
        var diagnoses = await ReadDoctorReportsAsync(connection, query, cancellationToken);

        var average = filtered.DurationCount > 0 ? filtered.DurationSum / filtered.DurationCount : (double?)null;
        return new OverviewCounts(
            availability.Read ? availability.TotalCalls : null,
            availability.Read ? availability.FailedCalls : null,
            filtered.Read ? filtered.Journeys : null,
            filtered.Read ? filtered.Successful : null,
            filtered.Read ? filtered.Searched : null,
            filtered.Read ? filtered.Converted : null,
            filtered.Read ? average : null,
            incidents?.Open,
            incidents?.Total,
            incidents?.Critical,
            diagnoses);
    }

    private static async Task<JourneySum> SumJourneysAsync(
        SqlConnection connection,
        DateTimeOffset since,
        string environment,
        string? domain,
        string? clubId,
        string? search,
        CancellationToken cancellationToken)
    {
        var sum = new JourneySum();
        foreach (var (name, schema) in Domains)
        {
            if (domain is null)
            {
                if (!PortalDomains.All.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            }
            else if (!name.Equals(domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!await TableExistsAsync(connection, $"{schema}.Journey", cancellationToken)) continue;
            if (!await TableExistsAsync(connection, $"{schema}.QueryRun", cancellationToken)) continue;

            var row = await connection.QuerySingleAsync<AggregateRow>(new CommandDefinition(
                JourneySql(schema),
                new { Since = since, Environment = environment, ClubId = clubId, Search = search },
                cancellationToken: cancellationToken));
            sum.Read = true;
            sum.Journeys += row.Journeys;
            sum.Successful += row.Successful;
            sum.Searched += row.Searched;
            sum.Converted += row.Converted;
            sum.DurationSum += row.DurationSum;
            sum.DurationCount += row.DurationCount;
            sum.FailedCalls += row.FailedCalls;

            if (await TableExistsAsync(connection, $"{schema}.JourneyStep", cancellationToken))
            {
                var calls = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    CallsSql(schema),
                    new { Since = since, Environment = environment, ClubId = clubId, Search = search },
                    cancellationToken: cancellationToken));
                sum.TotalCalls += calls ?? 0;
            }
        }

        return sum;
    }

    private static async Task<IncidentSum?> ReadIncidentsAsync(
        SqlConnection connection,
        OverviewReadQuery query,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "incident.Incident", cancellationToken)) return null;

        return await connection.QuerySingleAsync<IncidentSum>(new CommandDefinition(
            $"""
            SELECT
                COUNT(*) AS Total,
                ISNULL(SUM(CASE WHEN Status <> N'Resolved' THEN 1 ELSE 0 END), 0) AS [Open],
                ISNULL(SUM(CASE WHEN Severity = N'Critical' AND Status <> N'Resolved' THEN 1 ELSE 0 END), 0) AS Critical
            FROM incident.Incident
            WHERE (
                    (@Domain IS NULL AND Domain IN ({AllDomainSql}))
                    OR Domain = @Domain
                  )
              AND (@ClubId IS NULL OR ClubId = @ClubId)
              AND Environment = @Environment
              AND LastSeen >= @Since
              AND (
                    @Search IS NULL
                    OR ExternalId LIKE @Search
                    OR Operation LIKE @Search
              )
            """,
            new
            {
                Domain = query.Domain,
                ClubId = query.ClubId,
                Environment = query.Environment,
                Since = query.FilteredSince,
                Search = query.SearchId,
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Counts the Doctor Monkey's real diagnoses (monitoring.DoctorReport), joined to Finding for
    /// domain scoping. monitoring.MonkeyAction exists as a table but nothing writes to it yet
    /// (shadow-mode only), so it always read as a misleading 0 - DoctorReport is the real signal
    /// of "items the Doctor Monkey has".
    /// monitoring.Finding/DoctorReport carry no Environment or ClubId column today (a monkey
    /// finding isn't scoped to either) and no caller-searchable id comparable to a journey/
    /// incident search. Rather than silently ignore those filters while the neighboring tiles
    /// honor them, this returns "No data" whenever one of those unsupported dimensions is active -
    /// honest degrade over a number that looks filtered but isn't.
    /// </summary>
    private static async Task<int?> ReadDoctorReportsAsync(
        SqlConnection connection,
        OverviewReadQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ClubId is not null || query.SearchId is not null) return null;
        if (!string.Equals(query.Environment, InsightsEnvironment.Production, StringComparison.OrdinalIgnoreCase)) return null;
        if (!await TableExistsAsync(connection, "monitoring.DoctorReport", cancellationToken)) return null;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            SELECT COUNT(*)
            FROM monitoring.DoctorReport AS dr
            INNER JOIN monitoring.Finding AS f ON f.Id = dr.FindingId
            WHERE dr.GeneratedAt >= @Since
              AND (
                    (@Domain IS NULL AND f.Domain IN ({AllDomainSql}))
                    OR f.Domain = @Domain
                  )
            """,
            new { Since = query.FilteredSince, Domain = query.Domain },
            cancellationToken: cancellationToken));
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var id = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT OBJECT_ID(@Name, N'U')",
            new { Name = name },
            cancellationToken: cancellationToken));
        return id is not null;
    }

    private static string JourneySql(string schema) => $"""
        SELECT
            COUNT(*) AS Journeys,
            ISNULL(SUM(CASE WHEN j.IsBookSuccessful = 1 THEN 1 ELSE 0 END), 0) AS Successful,
            ISNULL(SUM(CASE WHEN j.PassedSearch = 1 THEN 1 ELSE 0 END), 0) AS Searched,
            ISNULL(SUM(CASE WHEN j.PassedSearch = 1 AND j.IsBookSuccessful = 1 THEN 1 ELSE 0 END), 0) AS Converted,
            ISNULL(SUM(CAST(j.JourneyTotalMs AS float)), 0) AS DurationSum,
            ISNULL(SUM(CASE WHEN j.JourneyTotalMs IS NULL THEN 0 ELSE 1 END), 0) AS DurationCount,
            ISNULL(SUM(CAST(j.FailedCalls AS bigint)), 0) AS FailedCalls
        FROM {schema}.Journey AS j
        INNER JOIN {schema}.QueryRun AS q ON q.QueryRunId = j.QueryRunId
        WHERE j.LastSeen >= @Since
          AND q.Environment = @Environment
          AND (@ClubId IS NULL OR q.ClubId = @ClubId)
          AND (
                @Search IS NULL
                OR j.CorrelationId LIKE @Search
                OR j.UserId LIKE @Search
          )
        """;

    private static string CallsSql(string schema) => $"""
        SELECT SUM(CAST(s.CallCount AS bigint))
        FROM {schema}.JourneyStep AS s
        INNER JOIN {schema}.Journey AS j ON j.JourneyId = s.JourneyId
        INNER JOIN {schema}.QueryRun AS q ON q.QueryRunId = j.QueryRunId
        WHERE j.LastSeen >= @Since
          AND q.Environment = @Environment
          AND (@ClubId IS NULL OR q.ClubId = @ClubId)
          AND (
                @Search IS NULL
                OR j.CorrelationId LIKE @Search
                OR j.UserId LIKE @Search
          )
        """;

    private sealed class JourneySum
    {
        public bool Read { get; set; }
        public int Journeys { get; set; }
        public int Successful { get; set; }
        public int Searched { get; set; }
        public int Converted { get; set; }
        public double DurationSum { get; set; }
        public int DurationCount { get; set; }
        public long FailedCalls { get; set; }
        public long TotalCalls { get; set; }
    }

    private sealed class AggregateRow
    {
        public int Journeys { get; set; }
        public int Successful { get; set; }
        public int Searched { get; set; }
        public int Converted { get; set; }
        public double DurationSum { get; set; }
        public int DurationCount { get; set; }
        public long FailedCalls { get; set; }
    }

    private sealed class IncidentSum
    {
        public int Total { get; set; }
        public int Open { get; set; }
        public int Critical { get; set; }
    }
}
