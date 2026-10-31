namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Who fixes a failing service. Separate from the club identity on purpose: the club says who is
/// AFFECTED, the owner says who FIXES IT, and routing a code defect to a brand manager because the
/// two were conflated is the failure this exists to prevent.
/// </summary>
public interface IServiceOwnerStore
{
    Task<IReadOnlyList<ServiceOwner>> GetAllAsync(CancellationToken ct = default);

    Task<ServiceOwner> UpsertAsync(ServiceOwner owner, string changedBy, CancellationToken ct = default);

    Task DeleteAsync(string vertical, string stage, string changedBy, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceOwnerAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default);
}

/// <summary>
/// Resolves the owner for a case. Never throws and never guesses: an unresolved owner comes back as
/// <see cref="ServiceOwnerResolution.Unresolved"/>, because an invented name is worse than an
/// admitted gap - the first alert routed to one would cost the routing layer all its credibility.
/// </summary>
public interface IServiceOwnerResolver
{
    Task<ResolvedServiceOwner> ResolveAsync(string vertical, string stage, CancellationToken ct = default);

    /// <summary>Drops the cached snapshot so an owner assigned in the portal takes effect without waiting it out.</summary>
    void Invalidate();
}

public sealed record ServiceOwner(
    string Vertical,
    /// <summary><see cref="ServiceOwner.AnyStage"/> means "every stage of this vertical".</summary>
    string Stage,
    string OwnerTeam,
    string? PrimaryResponder,
    string? Backup,
    string? ContactRef,
    string? Notes,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null)
{
    public const string AnyStage = "*";
}

public sealed record ServiceOwnerAuditEntry(
    long AuditId,
    string Vertical,
    string Stage,
    string? OwnerTeam,
    string? PrimaryResponder,
    string? Backup,
    string? ContactRef,
    bool Removed,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>
/// The answer, including how it was reached. <see cref="ResolvedBy"/> is part of the contract:
/// a vertical-wide fallback and an exact match for this stage are both "an owner", but only one of
/// them means somebody accepted this specific service.
/// </summary>
public sealed record ResolvedServiceOwner(
    bool IsResolved,
    string ResolvedBy,
    string? OwnerTeam,
    string? PrimaryResponder,
    string? Backup,
    string? ContactRef)
{
    public static ResolvedServiceOwner Unresolved(string reason) =>
        new(false, reason, null, null, null, null);

    /// <summary>One line for the case file and the alert, without the caller re-deriving it.</summary>
    public string Describe() =>
        !IsResolved
            ? "no owner is recorded for this service"
            : string.Join(" · ", new[]
            {
                OwnerTeam,
                PrimaryResponder is { Length: > 0 } ? $"primary {PrimaryResponder}" : null,
                Backup is { Length: > 0 } ? $"backup {Backup}" : null,
            }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

public static class ServiceOwnerResolution
{
    /// <summary>A row for this exact vertical and stage - somebody accepted this specific service.</summary>
    public const string ExactStage = "exact-stage";

    /// <summary>A vertical-wide row. An owner, but nobody named this stage specifically.</summary>
    public const string VerticalFallback = "vertical-fallback";

    public const string Unresolved = "unresolved";

    /// <summary>The ownership map could not be read. Distinct from "nobody owns it" - one is our outage, the other is their gap.</summary>
    public const string Unavailable = "unavailable";
}
