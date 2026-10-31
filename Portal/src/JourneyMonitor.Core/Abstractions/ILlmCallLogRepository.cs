namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One LLM call made by the Doctor Monkey while diagnosing a Finding - the exact prompt sent and
/// the raw (pre-JSON-parsing) response text, kept separate from <c>DoctorReport</c> (which only
/// stores the parsed verdict/category/etc.) so this heavier, rarely-needed debug payload never
/// bloats the GET /api/monkeys list response; it is only read on demand via
/// GET /api/findings/{id}/doctor-report.
/// </summary>
public sealed record LlmCallLog(
    Guid Id,
    Guid DoctorReportId,
    Guid FindingId,
    string PromptText,
    string RawResponseText,
    string ModelUsed,
    int TokensIn,
    int TokensOut,
    DateTimeOffset CreatedAt);

/// <summary>Durable log of every LLM call the Doctor Monkey makes - persisted for every diagnosis, not just failures, so any past verdict can be audited.</summary>
public interface ILlmCallLogRepository
{
    Task<LlmCallLog> AddAsync(LlmCallLog log, CancellationToken ct = default);

    /// <summary>One report is diagnosed by exactly one LLM call today - null if none was logged (e.g. a report predating this feature).</summary>
    Task<LlmCallLog?> GetByDoctorReportIdAsync(Guid doctorReportId, CancellationToken ct = default);
}
