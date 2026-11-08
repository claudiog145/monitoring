using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Counts the seven Executive Overview tiles from the journey and incident tables.
/// Platform Availability always uses the last 24 hours and the All domains (Hotels, Cruises, and Cars).
/// The other journey tiles use the filters on <see cref="OverviewReadQuery"/>.
/// A null domain is All, which does not query the air schema.
///
/// Contract v6 §4.5: every read is for ONE company. The incumbent's journeys come from its legacy
/// per-vertical schemas; any other company's from monitoring.ProfileJourney. Incidents are always
/// filtered by ProfileKey (the legacy surface reads the incumbent's).
/// </summary>
public sealed class SqlOverviewReader(IOptions<JourneyStoreOptions> storeOptions) : IOverviewReader, IProfileOverviewReader
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
        var read = await ReadAsync(
            new ProfileOverviewQuery(
                KpiProfiles.Default,
                IsIncumbent: true,
                query.Domain,
                query.ClubId,
                query.Environment,
                query.FilteredSince,
                query.AvailabilitySince,
                query.SearchId,
                TopEntities: 0),
            cancellationToken);
        return read.Counts;
    }

    public async Task<ProfileOverviewCounts> ReadAsync(ProfileOverviewQuery query, CancellationToken cancellationToken)
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        if (string.IsNullOrWhiteSpace(query.ProfileKey))
        {
            throw new ArgumentException("A profile key is required: the overview never spans companies.", nameof(query));
        }

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        JourneySum availability;
        JourneySum filtered;
        if (query.IsIncumbent)
        {
            availability = await SumJourneysAsync(
                connection, query.AvailabilitySince, query.Environment, null, null, null, cancellationToken);
            filtered = await SumJourneysAsync(
                connection,
                query.FilteredSince,
                query.Environment,
                query.ServiceKey,
                query.EntityKey,
                query.SearchId,
                cancellationToken);
        }
        else
        {
            availability = await SumProfileJourneysAsync(
                connection, query.ProfileKey, query.AvailabilitySince, null, null, null, cancellationToken);
            filtered = await SumProfileJourneysAsync(
                connection, query.ProfileKey, query.FilteredSince, query.ServiceKey, query.EntityKey, query.SearchId, cancellationToken);
        }

        var incidents = await ReadIncidentsAsync(connection, query, cancellationToken);
        var diagnoses = query.IsIncumbent
            ? await ReadDoctorReportsAsync(connection, query, cancellationToken)
            // monitoring.Finding carries no ProfileKey: counting another company's findings would mix companies.
            : null;

        var average = filtered.DurationCount > 0 ? filtered.DurationSum / filtered.DurationCount : (double?)null;
        var counts = new OverviewCounts(
            availability.Read ? availability.TotalCalls : null,
            availability.Read ? availability.FailedCalls : null,
            filtered.Read ? filtered.Journeys : null,
            filtered.Read ? filtered.Successful : null,
            filtered.Read && query.IsIncumbent ? filtered.Searched : null,
            filtered.Read && query.IsIncumbent ? filtered.Converted : null,
            filtered.Read ? average : null,
            incidents?.Open,
            incidents?.Total,
            incidents?.Critical,
            diagnoses);

        if (query.TopEntities <= 0)
        {
            return new ProfileOverviewCounts(counts, [], []);
        }

        return new ProfileOverviewCounts(
            counts,
            await ReadIncidentEntitiesAsync(connection, query, cancellationToken),
            await ReadJourneyEntitiesAsync(connection, query, cancellationToken));
    }

    /// <summary>
    /// A non-incumbent company's journeys (monitoring.ProfileJourney, written by the profile journey sync).
    /// Calls are its steps; a failed step is a failed call. The table carries no environment column.
    /// </summary>
    private static async Task<JourneySum> SumProfileJourneysAsync(
        SqlConnection connection,
        string profileKey,
        DateTimeOffset since,
        string? serviceKey,
        string? entityKey,
        string? search,
        CancellationToken cancellationToken)
    {
        var sum = new JourneySum();
        if (!await TableExistsAsync(connection, "monitoring.ProfileJourney", cancellationToken)) return sum;

        var parameters = new { ProfileKey = profileKey, Since = since, Service = serviceKey, Entity = entityKey, Search = search };
        var row = await connection.QuerySingleAsync<AggregateRow>(new CommandDefinition(
            $"""
            SELECT
                COUNT(*) AS Journeys,
                ISNULL(SUM(CASE WHEN j.Status = N'Completed' THEN 1 ELSE 0 END), 0) AS Successful,
                0 AS Searched,
                0 AS Converted,
                CAST(0 AS float) AS DurationSum,
                0 AS DurationCount,
                CAST(0 AS bigint) AS FailedCalls
            FROM monitoring.ProfileJourney AS j
            WHERE {ProfileJourneyFilter}
            """,
            parameters,
            cancellationToken: cancellationToken));
        sum.Read = true;
        sum.Journeys = row.Journeys;
        sum.Successful = row.Successful;

        if (await TableExistsAsync(connection, "monitoring.ProfileJourneyStep", cancellationToken))
        {
            // The response time is the requests' own duration. ProfileJourney.DurationMs of a session spans the
            // user's think time between steps (minutes), so it would read as a latency of tens of seconds.
            var calls = await connection.QuerySingleAsync<CallsRow>(new CommandDefinition(
                $"""
                SELECT
                    COUNT_BIG(*) AS TotalCalls,
                    ISNULL(SUM(CASE WHEN s.Success = 0 THEN CAST(1 AS bigint) ELSE 0 END), 0) AS FailedCalls,
                    CAST(ISNULL(SUM(s.DurationMs), 0) AS float) AS DurationSum,
                    COUNT(*) AS DurationCount
                FROM monitoring.ProfileJourneyStep AS s
                INNER JOIN monitoring.ProfileJourney AS j
                    ON j.ProfileKey = s.ProfileKey AND j.JourneyId = s.JourneyId
                WHERE {ProfileJourneyFilter}
                """,
                parameters,
                cancellationToken: cancellationToken));
            sum.TotalCalls = calls.TotalCalls;
            sum.FailedCalls = calls.FailedCalls;
            sum.DurationSum = calls.DurationSum;
            sum.DurationCount = calls.DurationCount;
        }

        return sum;
    }

    private const string ProfileJourneyFilter = """
        j.ProfileKey = @ProfileKey
          AND j.StartedAt >= @Since
          AND (@Service IS NULL OR j.ServiceKey = @Service)
          AND (@Entity IS NULL OR j.EntityKey = @Entity)
          AND (
                @Search IS NULL
                OR j.JourneyId LIKE @Search
                OR j.SampleOperationId LIKE @Search
          )
        """;

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
        ProfileOverviewQuery query,
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
            WHERE {IncidentFilter(query)}
            """,
            IncidentParameters(query),
            cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<OverviewEntity>> ReadIncidentEntitiesAsync(
        SqlConnection connection,
        ProfileOverviewQuery query,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "incident.Incident", cancellationToken)) return [];

        var rows = await connection.QueryAsync<EntityRow>(new CommandDefinition(
            $"""
            SELECT TOP (@Top)
                ClubId AS [Key],
                COUNT(*) AS Incidents,
                ISNULL(SUM(CAST(OccurrenceCount AS bigint)), 0) AS Occurrences,
                0 AS AffectedJourneys
            FROM incident.Incident
            WHERE {IncidentFilter(query)}
              AND ClubId <> N''
            GROUP BY ClubId
            ORDER BY COUNT(*) DESC, SUM(CAST(OccurrenceCount AS bigint)) DESC, ClubId
            """,
            IncidentParameters(query),
            cancellationToken: cancellationToken));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private static async Task<IReadOnlyList<OverviewEntity>> ReadJourneyEntitiesAsync(
        SqlConnection connection,
        ProfileOverviewQuery query,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "monitoring.ProfileJourney", cancellationToken)) return [];

        var rows = await connection.QueryAsync<EntityRow>(new CommandDefinition(
            $"""
            SELECT TOP (@Top)
                j.EntityKey AS [Key],
                0 AS Incidents,
                CAST(0 AS bigint) AS Occurrences,
                COUNT(*) AS AffectedJourneys
            FROM monitoring.ProfileJourney AS j
            WHERE {ProfileJourneyFilter}
              AND j.EntityKey IS NOT NULL
              AND j.EntityKey <> N''
              AND (j.Failed = 1 OR j.Status LIKE N'Dropped%')
            GROUP BY j.EntityKey
            ORDER BY COUNT(*) DESC, j.EntityKey
            """,
            new
            {
                Top = query.TopEntities,
                ProfileKey = query.ProfileKey,
                Since = query.FilteredSince,
                Service = query.ServiceKey,
                Entity = query.EntityKey,
                Search = query.SearchId,
            },
            cancellationToken: cancellationToken));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <summary>The incumbent's "all" is its vertical list; another company's is every one of its rows.</summary>
    private static string IncidentFilter(ProfileOverviewQuery query) => $"""
        ProfileKey = @ProfileKey
          AND (
                (@Domain IS NULL{(query.IsIncumbent ? $" AND Domain IN ({AllDomainSql})" : "")})
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
        """;

    private static object IncidentParameters(ProfileOverviewQuery query) => new
    {
        Top = Math.Max(0, query.TopEntities),
        ProfileKey = query.ProfileKey,
        Domain = query.ServiceKey,
        ClubId = query.EntityKey,
        Environment = query.Environment,
        Since = query.FilteredSince,
        Search = query.SearchId,
    };

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
        ProfileOverviewQuery query,
        CancellationToken cancellationToken)
    {
        if (query.EntityKey is not null || query.SearchId is not null) return null;
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
            new { Since = query.FilteredSince, Domain = query.ServiceKey },
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
            -- A booked session is a conversion even when its search was an earlier session.
            ISNULL(SUM(CASE WHEN j.IsBookSuccessful = 1 THEN 1 ELSE 0 END), 0) AS Converted,
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

    private sealed class CallsRow
    {
        public long TotalCalls { get; set; }
        public long FailedCalls { get; set; }
        public double DurationSum { get; set; }
        public int DurationCount { get; set; }
    }

    private sealed class EntityRow
    {
        public string Key { get; set; } = "";
        public int Incidents { get; set; }
        public long Occurrences { get; set; }
        public int AffectedJourneys { get; set; }

        public OverviewEntity ToDomain() => new(Key, Incidents, Occurrences, AffectedJourneys);
    }
    private sealed class IncidentSum
    {
        public int Total { get; set; }
        public int Open { get; set; }
        public int Critical { get; set; }
    }
}
