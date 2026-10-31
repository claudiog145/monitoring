using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

public sealed record DbJourneysRequest(
    string Domain,
    string? ClubId,
    string? Status,
    string? SearchId,
    string? RowCapRaw,
    string? Win = null);

/// <summary>
/// Reads stored journey rows whose LastSeen falls inside the requested time range. The result
/// deliberately contains raw KQL-shaped rows because the existing browser mappers consume that contract.
/// </summary>
public sealed class GetDbJourneysService(
    IJourneySnapshotStore journeyStore,
    IOptions<JourneySyncOptions> syncOptions)
{
    private static readonly string[] ValidDomains = ["hotels", "cars", "cruises", "air"];

    public async Task<JourneysOutcome> GetAsync(DbJourneysRequest request, CancellationToken cancellationToken)
    {
        var domain = request.Domain.ToLowerInvariant();
        var rowCap = syncOptions.Value.NormalizeRequestedRowCap(request.RowCapRaw);
        DateTimeOffset? since = null;

        if (!ValidDomains.Contains(domain))
        {
            return Error(400, domain, request.ClubId, "domain must be hotels, cars, cruises or air");
        }

        if (request.ClubId is not null && !ClubFilter.IsValidClubId(request.ClubId))
        {
            return Error(400, domain, request.ClubId, "club must be a numeric id");
        }

        if (!string.IsNullOrWhiteSpace(request.Win))
        {
            if (!ClubFilter.TryParseWindow(request.Win, out var window))
            {
                return Error(400, domain, request.ClubId, "win must look like 15m, 1h or 7d");
            }

            since = DateTimeOffset.UtcNow - window;
        }

        try
        {
            // Status is intentionally reserved until each domain's UI health value has an agreed
            // database mapping. Accepting it now keeps the HTTP contract stable without filtering
            // unlike values such as Hotel Outcome and Air Status.
            var query = new JourneySnapshotQuery(
                domain,
                request.ClubId,
                Status: null,
                NormalizeSearchId(request.SearchId),
                rowCap,
                since);
            var snapshot = await journeyStore.GetLatestAsync(query, cancellationToken);
            var rows = FilterByWindow(snapshot.Rows, since);

            return new JourneysOutcome(
                new JourneysResult
                {
                    Source = "sql",
                    Win = request.Win ?? snapshot.Window,
                    Domain = snapshot.Domain,
                    Environment = snapshot.Environment,
                    ClubId = snapshot.ClubId ?? request.ClubId,
                    Host = snapshot.Host,
                    RowCap = rowCap,
                    FetchedAt = snapshot.FetchedAt?.ToString("O"),
                    RowCount = rows.Count,
                    Rows = rows,
                },
                200);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(502, domain, request.ClubId, $"Journey store read failed: {ex.Message}");
        }
    }

    private static List<TelemetryJourneyRow> FilterByWindow(
        IReadOnlyList<TelemetryJourneyRow> rows,
        DateTimeOffset? since)
    {
        if (since is null) return rows.ToList();

        return rows
            .Where(row =>
            {
                var lastSeen = row.GetDateTimeOffset("LastSeen");
                return lastSeen is null || lastSeen >= since;
            })
            .ToList();
    }

    private static string? NormalizeSearchId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JourneysOutcome Error(int statusCode, string domain, string? clubId, string message) =>
        new(
            new JourneysResult
            {
                Source = "error",
                Win = "",
                Domain = domain,
                ClubId = clubId,
                Error = message,
            },
            statusCode);
}
