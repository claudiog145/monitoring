using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Reads incident.Incident plus the latest incident.Occurrence (member and URL) and
/// incident.JourneyLink. Related journey keys are newest occurrence first. Cruise keys
/// prefer the frontend session stored on the occurrence. Hotel keys prefer the correlation
/// id, which is the Journey ID column, including rows previously linked by member id.
/// Activity and ExternalReference stay empty: those are triage and ingestion does not write them.
/// Parameterized SQL, same connection as the journey store, so a database that already
/// has the incident tables can serve the Exceptions tab.
/// </summary>
public sealed class SqlIncidentReader(IOptions<JourneyStoreOptions> storeOptions) : IIncidentReader, IProfileIncidentReader
{
    /// <summary>SQL literal list for an unspecified domain. Matches <see cref="PortalDomains.All"/>.</summary>
    private static readonly string AllDomainSql =
        string.Join(", ", PortalDomains.All.Select(domain => $"N'{domain}'"));

    private static readonly string ListSql = $"""
        SELECT TOP (@Limit)
            i.ExternalId,
            i.Domain,
            i.ClubId,
            i.Environment,
            i.ExceptionType,
            i.Component,
            i.Operation,
            i.Message,
            LEFT(latest.StackTrace, 8000) AS StackTrace,
            i.Severity,
            i.Status,
            i.AssignedTo,
            i.FirstSeen,
            i.LastSeen,
            i.OccurrenceCount,
            i.UsersAffected,
            i.Source,
            latest.MemberId AS LatestMemberId,
            latest.Url AS LatestUrl,
            CASE
                WHEN i.Domain = N'hotels' AND hotelCorrelations.Keys IS NOT NULL THEN hotelCorrelations.Keys
                WHEN i.Domain = N'cruises' AND cruiseSessions.Keys IS NOT NULL THEN cruiseSessions.Keys
                ELSE (
                    SELECT STRING_AGG(CAST(k.JourneyKey AS nvarchar(max)), N'|')
                           WITHIN GROUP (ORDER BY k.LastSeen DESC, k.JourneyLinkId DESC)
                    FROM (
                        SELECT TOP (20)
                            link.JourneyKey,
                            link.JourneyLinkId,
                            COALESCE(MAX(o.OccurredAt), CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) AS LastSeen
                        FROM incident.JourneyLink AS link
                        LEFT JOIN incident.Occurrence AS o
                            ON o.IncidentId = link.IncidentId
                           AND (
                                o.CorrelationId = link.JourneyKey
                                OR o.SessionId = link.JourneyKey
                                OR o.MemberId = link.JourneyKey
                           )
                        WHERE link.IncidentId = i.IncidentId
                        GROUP BY link.JourneyKey, link.JourneyLinkId
                        ORDER BY COALESCE(MAX(o.OccurredAt), CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) DESC,
                                 link.JourneyLinkId DESC
                    ) AS k
                )
            END AS JourneyKeys,
            (
                SELECT o.OccurredAt, o.MemberId, o.Url, o.Message, o.Source
                FROM (
                    SELECT TOP (8) OccurredAt, MemberId, Url, Message, Source, OccurrenceId
                    FROM incident.Occurrence
                    WHERE IncidentId = i.IncidentId
                    ORDER BY OccurredAt DESC, OccurrenceId DESC
                ) AS o
                ORDER BY o.OccurredAt DESC, o.OccurrenceId DESC
                FOR JSON PATH
            ) AS RecentOccurrencesJson
        FROM incident.Incident AS i
        OUTER APPLY (
            SELECT TOP (1) MemberId, Url, StackTrace
            FROM incident.Occurrence
            WHERE IncidentId = i.IncidentId
            ORDER BY OccurredAt DESC, OccurrenceId DESC
        ) AS latest
        OUTER APPLY (
            SELECT STRING_AGG(CAST(s.SessionId AS nvarchar(max)), N'|')
                   WITHIN GROUP (ORDER BY s.LastSeen DESC) AS Keys
            FROM (
                SELECT TOP (20) SessionId, MAX(OccurredAt) AS LastSeen
                FROM incident.Occurrence
                WHERE IncidentId = i.IncidentId
                  AND i.Domain = N'cruises'
                  AND SessionId IS NOT NULL
                  AND SessionId <> N''
                  AND SessionId <> N'00000000-0000-0000-0000-000000000000'
                GROUP BY SessionId
                ORDER BY MAX(OccurredAt) DESC
            ) AS s
        ) AS cruiseSessions
        OUTER APPLY (
            SELECT STRING_AGG(CAST(c.CorrelationId AS nvarchar(max)), N'|')
                   WITHIN GROUP (ORDER BY c.LastSeen DESC) AS Keys
            FROM (
                SELECT TOP (20) CorrelationId, MAX(OccurredAt) AS LastSeen
                FROM incident.Occurrence
                WHERE IncidentId = i.IncidentId
                  AND i.Domain = N'hotels'
                  AND CorrelationId IS NOT NULL
                  AND CorrelationId <> N''
                  AND CorrelationId <> N'00000000-0000-0000-0000-000000000000'
                GROUP BY CorrelationId
                ORDER BY MAX(OccurredAt) DESC
            ) AS c
        ) AS hotelCorrelations
        WHERE i.ProfileKey = @ProfileKey
          AND (
                (@Domain IS NULL AND (@AnyDomain = 1 OR i.Domain IN ({AllDomainSql})))
                OR i.Domain = @Domain
              )
          AND (@ClubId IS NULL OR i.ClubId = @ClubId)
          AND i.Environment = @Environment
          AND (@Since IS NULL OR i.LastSeen >= @Since)
          AND (
                @Search IS NULL
                OR i.ExternalId LIKE @Search
                OR i.ExceptionType LIKE @Search
                OR i.Operation LIKE @Search
                OR EXISTS (
                    SELECT 1
                    FROM incident.Occurrence AS found
                    WHERE found.IncidentId = i.IncidentId
                      AND (
                            found.MemberId LIKE @Search
                         OR found.Url LIKE @Search
                         OR found.CorrelationId LIKE @Search
                         OR found.SessionId LIKE @Search
                      )
                )
                OR EXISTS (
                    SELECT 1
                    FROM incident.JourneyLink AS link
                    WHERE link.IncidentId = i.IncidentId
                      AND link.JourneyKey LIKE @Search
                )
          )
        ORDER BY i.LastSeen DESC, i.IncidentId DESC
        """;

    /// <summary>Same filters as the list, counted per domain over LastSeen. Not capped at the list size.</summary>
    private static readonly string CountSql = $"""
        SELECT i.Domain, COUNT(*) AS IncidentCount
        FROM incident.Incident AS i
        WHERE i.ProfileKey = @ProfileKey
          AND (
                (@Domain IS NULL AND (@AnyDomain = 1 OR i.Domain IN ({AllDomainSql})))
                OR i.Domain = @Domain
              )
          AND (@ClubId IS NULL OR i.ClubId = @ClubId)
          AND i.Environment = @Environment
          AND (@Since IS NULL OR i.LastSeen >= @Since)
          AND (
                @Search IS NULL
                OR i.ExternalId LIKE @Search
                OR i.ExceptionType LIKE @Search
                OR i.Operation LIKE @Search
                OR EXISTS (
                    SELECT 1
                    FROM incident.Occurrence AS found
                    WHERE found.IncidentId = i.IncidentId
                      AND (
                            found.MemberId LIKE @Search
                         OR found.Url LIKE @Search
                         OR found.CorrelationId LIKE @Search
                         OR found.SessionId LIKE @Search
                      )
                )
                OR EXISTS (
                    SELECT 1
                    FROM incident.JourneyLink AS link
                    WHERE link.IncidentId = i.IncidentId
                      AND link.JourneyKey LIKE @Search
                )
          )
        GROUP BY i.Domain
        """;

    private static readonly JsonSerializerOptions OccurrenceJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentReadQuery query, CancellationToken cancellationToken) =>
        ListForProfileAsync(KpiProfiles.Default, query, cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> CountByDomainAsync(IncidentCountQuery query, CancellationToken cancellationToken) =>
        CountByDomainForProfileAsync(KpiProfiles.Default, query, cancellationToken);

    public async Task<IReadOnlyList<IncidentRow>> ListForProfileAsync(
        string profileKey, IncidentReadQuery query, CancellationToken cancellationToken)
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured. Callers must check IsConfigured first.");
        }

        var parameters = ProfileParameters(profileKey);
        parameters.Add("Domain", query.Domain);
        parameters.Add("ClubId", query.ClubId);
        parameters.Add("Environment", query.Environment);
        parameters.Add("Since", query.Since);
        parameters.Add("Search", query.SearchPattern);
        parameters.Add("Limit", query.Limit);

        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            var rows = await connection.QueryAsync<IncidentSqlRow>(new CommandDefinition(
                ListSql,
                parameters,
                cancellationToken: cancellationToken));
            return rows.Select(ToRow).ToList();
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            throw new IncidentStoreUnavailableException(
                "Incident schema is not provisioned. Run CreateIncidentSchema.sql before reading exceptions.");
        }
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByDomainForProfileAsync(
        string profileKey, IncidentCountQuery query, CancellationToken cancellationToken)
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured. Callers must check IsConfigured first.");
        }

        var parameters = ProfileParameters(profileKey);
        parameters.Add("Domain", query.Domain);
        parameters.Add("ClubId", query.ClubId);
        parameters.Add("Environment", query.Environment);
        parameters.Add("Since", query.Since);
        parameters.Add("Search", query.SearchPattern);

        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            var rows = await connection.QueryAsync<IncidentCountRow>(new CommandDefinition(
                CountSql,
                parameters,
                cancellationToken: cancellationToken));
            return rows.ToDictionary(row => row.Domain, row => row.IncidentCount);
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            throw new IncidentStoreUnavailableException(
                "Incident schema is not provisioned. Run CreateIncidentSchema.sql before reading exceptions.");
        }
    }

    /// <summary>
    /// Every read is scoped to one company. Only the incumbent is narrowed to its own domain list when no
    /// domain is named; another company's domains are its service keys.
    /// </summary>
    private static DynamicParameters ProfileParameters(string profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) throw new ArgumentException("A profile key is required.", nameof(profileKey));
        var key = profileKey.Trim();
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", key);
        parameters.Add("AnyDomain", string.Equals(key, KpiProfiles.Default, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        return parameters;
    }

    private static IncidentRow ToRow(IncidentSqlRow row) => new(
        row.ExternalId,
        row.Domain,
        row.ClubId,
        row.Environment,
        row.ExceptionType,
        row.Component,
        row.Operation,
        row.Message,
        row.StackTrace,
        row.Severity,
        row.Status,
        row.AssignedTo,
        row.FirstSeen,
        row.LastSeen,
        row.OccurrenceCount,
        row.UsersAffected,
        row.LatestMemberId,
        row.LatestUrl,
        SplitKeys(row.JourneyKeys),
        ParseOccurrences(row.RecentOccurrencesJson),
        ExceptionSource.Normalize(row.Source));

    private static IReadOnlyList<string> SplitKeys(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<IncidentOccurrenceRow> ParseOccurrences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var rows = JsonSerializer.Deserialize<List<OccurrenceJsonRow>>(json, OccurrenceJson) ?? [];
        return rows
            .Select(row => new IncidentOccurrenceRow(
                row.OccurredAt, row.MemberId, row.Url, row.Message, ExceptionSource.Normalize(row.Source)))
            .ToList();
    }

    private sealed class IncidentSqlRow
    {
        public string ExternalId { get; set; } = "";
        public string Domain { get; set; } = "";
        public string ClubId { get; set; } = "";
        public string Environment { get; set; } = "";
        public string ExceptionType { get; set; } = "";
        public string Component { get; set; } = "";
        public string Operation { get; set; } = "";
        public string Message { get; set; } = "";
        public string? StackTrace { get; set; }
        public string Severity { get; set; } = "";
        public string Status { get; set; } = "";
        public string? AssignedTo { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public int OccurrenceCount { get; set; }
        public int UsersAffected { get; set; }
        public string? Source { get; set; }
        public string? LatestMemberId { get; set; }
        public string? LatestUrl { get; set; }
        public string? JourneyKeys { get; set; }
        public string? RecentOccurrencesJson { get; set; }
    }

    private sealed class IncidentCountRow
    {
        public string Domain { get; set; } = "";
        public int IncidentCount { get; set; }
    }

    private sealed class OccurrenceJsonRow
    {
        public DateTimeOffset OccurredAt { get; set; }
        public string? MemberId { get; set; }
        public string? Url { get; set; }
        public string? Message { get; set; }
        public string? Source { get; set; }
    }
}
