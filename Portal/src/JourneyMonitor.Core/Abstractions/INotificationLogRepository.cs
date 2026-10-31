namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One outbound notification attempt (toast / Teams / email) for whatever produced it - see
/// <see cref="INotificationChannel"/>. Answers "was this already notified, and did it succeed?" -
/// before this existed, that information only lived in ephemeral Function container logs. A source
/// can have more than one row (one per registered channel); failed attempts are kept too, not just
/// successes, so a silent delivery failure is still visible.
///
/// <see cref="SourceType"/>/<see cref="SourceId"/> replaced the original <c>FindingId</c>: the log
/// has to be able to describe a ForensicCase (or anything else) that is not a Finding.
/// </summary>
public sealed record NotificationLogEntry(
    Guid Id,
    string SourceType,
    Guid SourceId,
    string Channel,
    string? ProviderUsed,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset SentAt,
    string? Subject);

public static class NotificationChannel
{
    public const string Teams = "teams";
    public const string Email = "email";

    /// <summary>In-portal toast. Nothing leaves the process: the log row itself is the delivery, and the UI polls for it.</summary>
    public const string Toast = "toast";
}

/// <summary>Durable log of every notification attempt - persisted for every attempt (success or failure), not just failures, so any past delivery can be audited.</summary>
public interface INotificationLogRepository
{
    Task<NotificationLogEntry> AddAsync(NotificationLogEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<NotificationLogEntry>> GetBySourceAsync(string sourceType, Guid sourceId, CancellationToken ct = default);

    /// <summary>Every attempt across every source, newest first - used to build a source lookup in one call (see GetMonkeysFunction) instead of one GetBySourceAsync per item.</summary>
    Task<IReadOnlyList<NotificationLogEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Successful toast attempts newer than <paramref name="since"/>, oldest first. "Undelivered"
    /// is relative to the polling client, not to server state: the UI passes back the timestamp of
    /// the last toast it rendered, which avoids an acknowledgement column and an extra write per
    /// poll just to answer "did this browser already see it?".
    /// </summary>
    Task<IReadOnlyList<NotificationLogEntry>> GetUndeliveredToastsAsync(DateTimeOffset since, CancellationToken ct = default);
}
