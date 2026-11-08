using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Contract v6 §4.1 reads: journeys of ONE company from <see cref="IProfileJourneyStore"/>, labelled
/// with that company's vocabulary. A journey id of another company is "not found", never its data.
/// </summary>
public sealed class ProfileJourneyReader(
    IProfileJourneyStore store,
    ICompanyProfileStore profiles,
    KpiProfileResolver? profileResolver = null) : IProfileJourneyReader
{
    public static readonly ProfileJourneyLabels DefaultLabels = new("Service", "Flow", "Step", "Entity");

    /// <summary>
    /// §1: missing → the server's active company; otherwise the named profile, which must exist.
    /// Returns the profile key or an error message.
    /// </summary>
    public async Task<KpiProfileScope> ResolveProfileAsync(string? requested, CancellationToken ct)
    {
        if (profileResolver is not null) return await profileResolver.ResolveWriteAsync(requested, ct).ConfigureAwait(false);

        var key = (requested ?? "").Trim();
        if (key.Length == 0) return new KpiProfileScope(KpiProfiles.Default, null);
        return await profiles.GetAsync(key, ct).ConfigureAwait(false) is not null
            ? new KpiProfileScope(key, null)
            : new KpiProfileScope(null, KpiProfileResolver.UnknownProfileMessage(key));
    }

    public async Task<ProfileJourneyListResponse> ListAsync(ProfileJourneyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var labels = await LabelsAsync(query.ProfileKey, cancellationToken).ConfigureAwait(false);
        var rows = await store.ListAsync(query, cancellationToken).ConfigureAwait(false);
        var journeys = rows
            .Where(j => string.Equals(j.ProfileKey, query.ProfileKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ProfileJourneyListResponse(query.ProfileKey, labels, journeys);
    }

    public async Task<ProfileJourneyDetailResponse?> GetAsync(string profileKey, string journeyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        if (string.IsNullOrWhiteSpace(journeyId)) return null;

        var journey = await store.GetAsync(profileKey, journeyId.Trim(), cancellationToken).ConfigureAwait(false);
        if (journey is null || !string.Equals(journey.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)) return null;

        var steps = await store.GetStepsAsync(profileKey, journey.JourneyId, cancellationToken).ConfigureAwait(false);
        var labels = await LabelsAsync(profileKey, cancellationToken).ConfigureAwait(false);
        return new ProfileJourneyDetailResponse(profileKey, labels, journey, steps.OrderBy(s => s.StepOrder).ToList());
    }

    private async Task<ProfileJourneyLabels> LabelsAsync(string profileKey, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(profileKey, ct).ConfigureAwait(false);
        return LabelsOf(profile);
    }

    public static ProfileJourneyLabels LabelsOf(CompanyProfile? profile)
    {
        var labels = profile?.Labels;
        if (labels is null) return DefaultLabels;
        return new ProfileJourneyLabels(
            Or(labels.Service, DefaultLabels.Service),
            Or(labels.Flow, DefaultLabels.Flow),
            Or(labels.Step, DefaultLabels.Step),
            Or(labels.Entity, DefaultLabels.Entity));
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
