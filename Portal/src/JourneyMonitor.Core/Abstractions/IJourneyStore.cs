using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One upsert pass: the raw telemetry rows for a single domain/window/club plus the metadata
/// the per-vertical usp_IngestKqlRows procedure records on the QueryRun row.
/// </summary>
public sealed record JourneyUpsertRequest(
    string Domain,
    string Window,
    string? Environment,
    string? ClubId,
    string? Host,
    string Source,
    IReadOnlyList<TelemetryJourneyRow> Rows);

/// <summary>
/// Result of one upsert. <see cref="RecordCount"/> is how many journeys the resulting snapshot
/// exposes; the three breakdown counts say how that split across journeys seen for the first
/// time, journeys whose payload advanced, and journeys the overlapping window re-reported
/// unchanged.
/// </summary>
public sealed record JourneyUpsertResult(
    Guid QueryRunId,
    int RecordCount,
    int? Inserted = null,
    int? Updated = null,
    int? Unchanged = null);

/// <summary>
/// The latest stored snapshot for a domain, already projected back into the RAW row shape the
/// browser's map-v6.js/map-air.js expect (see <see cref="TelemetryJourneyRow"/>).
/// </summary>
public sealed record StoredJourneys(
    Guid QueryRunId,
    int RecordCount,
    DateTimeOffset? FetchedAt,
    string? Window,
    List<TelemetryJourneyRow> Rows);

/// <summary>
/// Durable journey store backed by the per-vertical SQL schemas (hotel/car/cruise/air), unlike
/// <see cref="ITelemetryQueryCache"/> which is a disposable 90-second query cache. Writes go
/// through usp_IngestKqlRows and reads through usp_GetLatestJourneys.
/// </summary>
public interface IJourneyStore
{
    Task<JourneyUpsertResult> UpsertAsync(JourneyUpsertRequest request, CancellationToken cancellationToken);
    Task<StoredJourneys?> GetLatestAsync(string domain, CancellationToken cancellationToken);
}

/// <summary>
/// Filters applied while reading stored journeys. <see cref="Since"/> includes every member the
/// club has stored with LastSeen inside the portal time range, not only the latest ingest.
/// </summary>
public sealed record JourneySnapshotQuery(
    string Domain,
    string? ClubId,
    string? Status,
    string? SearchId,
    int RowCap,
    DateTimeOffset? Since = null);

/// <summary>
/// A filtered view of the latest snapshot, with rows restored to the raw KQL contract.
/// </summary>
public sealed record JourneySnapshot(
    string Domain,
    string Window,
    string? Environment,
    string? ClubId,
    string? Host,
    DateTimeOffset? FetchedAt,
    List<TelemetryJourneyRow> Rows);

/// <summary>Read-only database port used by the database-backed journeys use case.</summary>
public interface IJourneySnapshotStore
{
    Task<JourneySnapshot> GetLatestAsync(JourneySnapshotQuery query, CancellationToken cancellationToken);
}
