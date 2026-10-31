using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Everything the pipeline needs to know about a company's vocabulary, behind one seam.
///
/// Both questions are answered from a <see cref="TelemetryRowContext"/> and the profile, and both
/// are synchronous and pure. That is the point: classification happens once per row of a board, so
/// a design that needed a database read per row would not survive contact with a real board.
/// </summary>
public interface ICompanyAdapter
{
    /// <summary>Which profile answered. Travels into the case file so a wrong mapping is attributable.</summary>
    string ProfileKey { get; }

    CompanyLabels Labels { get; }

    CompanyGlossary Glossary { get; }

    IReadOnlyList<ServiceDefinition> Services { get; }

    /// <summary>
    /// Which step of which flow this row belongs to, and therefore which service.
    ///
    /// The service is INFERRED FROM THE MATCHED STEP, not decided first and used to dispatch.
    /// A company routinely serves several product areas from one host, so a host (or a resource)
    /// cannot be the discriminator; the rule that actually recognised the row is the only thing
    /// that knows what it was. <see cref="ServiceDefinition.HostFilters"/> stays an optional
    /// narrowing filter, never a requirement.
    ///
    /// Never throws: a row it cannot classify is <see cref="StepMatch.NotResolved"/>, and only an
    /// internal defect yields <see cref="StepMatch.Unavailable"/>.
    /// </summary>
    StepMatch ResolveStep(TelemetryRowContext row);

    /// <summary>
    /// Who is affected, by walking <see cref="CompanyProfile.EntityIdentitySources"/> in order and
    /// taking the first that yields a value from the row.
    ///
    /// Never guesses: a row carrying no configured identity comes back unresolved, because an
    /// invented one attributes an incident to a customer who is not having a problem. The source
    /// that produced the value is reported, so an identity found by the fourth fallback is not
    /// mistaken for one the application stamped explicitly.
    /// </summary>
    EntityIdentity ResolveAffectedEntity(TelemetryRowContext row);
}

/// <summary>
/// Hands out the adapter for a profile, with the caching this repo already standardised on:
/// a short-lived snapshot plus an explicit <see cref="Invalidate"/>, so a profile edited in the
/// portal takes effect without waiting the TTL out - the same reason
/// <c>IKqlSourceResolver</c> and <c>IAgentPauseResolver</c> both expose one.
/// </summary>
public interface ICompanyAdapterProvider
{
    /// <summary>
    /// The adapter for <paramref name="profileKey"/>, or for the configured default when null.
    ///
    /// Throws when a named profile is missing or disabled, matching <c>KqlSourceResolver</c> and
    /// NOT <c>ServiceOwnerResolver</c>: an unresolved owner still ships an investigation, but a
    /// missing profile yields a board where nothing classifies, and a board full of unclassified
    /// rows is indistinguishable from a healthy one. Failing loudly is the only way that stays visible.
    /// </summary>
    Task<ICompanyAdapter> GetAsync(string? profileKey = null, CancellationToken ct = default);

    void Invalidate();
}

/// <summary>Thrown when a company profile cannot be turned into a usable adapter.</summary>
public sealed class CompanyProfileResolutionException : Exception
{
    public CompanyProfileResolutionException(string message) : base(message) { }

    public CompanyProfileResolutionException(string message, Exception innerException) : base(message, innerException) { }
}
