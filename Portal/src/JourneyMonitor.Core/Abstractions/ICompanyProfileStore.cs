using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Storage for company profiles. Same shape as <see cref="ITelemetrySourceStore"/> and
/// <c>IKqlQueryStore</c>, which is the point: onboarding a company should be rows in the three
/// tables that already exist for this (where to read from, what to run, what it means), not a
/// branch in the code.
/// </summary>
public interface ICompanyProfileStore
{
    /// <summary>Null when no profile is stored under that key, or when the row is disabled.</summary>
    Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default);

    Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Bumps <see cref="CompanyProfile.Version"/> and writes the full document to the audit.</summary>
    Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default);

    Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default);

    Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default);
}

/// <summary>
/// One revision of a profile, carrying the WHOLE document rather than "the profile changed".
///
/// Same call as monitoring.KqlQueryAudit and the opposite of monitoring.LlmClientSettingAudit:
/// there the value is a secret and must not be kept, here the value is the mapping that decided
/// how every row was classified - so restoring a previous revision has to be a copy, not a
/// reconstruction, and a reader has to be able to see exactly what was in effect.
/// </summary>
public sealed record CompanyProfileAuditEntry(
    long AuditId,
    string ProfileKey,
    int Version,
    string ProfileJson,
    bool Removed,
    DateTimeOffset ChangedAt,
    string ChangedBy);
