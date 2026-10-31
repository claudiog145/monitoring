namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Which company the platform is monitoring RIGHT NOW, as a stored preference rather than a
/// setting bound at startup.
///
/// Kept apart from <see cref="ICompanyProfileStore"/> on purpose: that one owns what a company IS
/// (and is versioned and audited accordingly), this one owns a single pointer that an operator
/// flips. Folding the pointer into the profile document would mean every switch rewrote - and
/// versioned - a document nobody edited.
///
/// The read is on the path of every unnamed <see cref="ICompanyAdapterProvider.GetAsync"/> call,
/// so callers are expected to cache it behind the same short TTL plus explicit invalidation the
/// rest of the settings surface uses.
/// </summary>
public interface IActiveCompanyProfileStore
{
    /// <summary>
    /// The active ProfileKey, or null when no company has been activated - which is not an error:
    /// it means the configured default is still in charge.
    /// </summary>
    Task<string?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// The same pointer with the who/when, for the admin surface. Separate from
    /// <see cref="GetActiveAsync"/> so the hot path is not asked to carry metadata it never reads.
    /// </summary>
    Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default);

    /// <summary>
    /// Points the platform at <paramref name="profileKey"/>. Implementations must REFUSE a key
    /// that is not a registered, enabled profile: activating a company that does not exist would
    /// leave every cycle failing with nothing to connect the failure to.
    /// </summary>
    Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default);
}

/// <summary>
/// The active pointer and its last change.
///
/// There is no audit table behind this, deliberately: monitoring.CompanyProfileAudit exists to
/// answer "what did this mapping SAY when that alert was raised", which is about the CONTENT of a
/// profile. Which profile was selected is a one-field preference, and UpdatedAt/UpdatedBy answer
/// "who switched us last" without a second append-only table to read, trim and back up.
/// </summary>
public sealed record ActiveCompanyProfile(
    string ProfileKey,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);
