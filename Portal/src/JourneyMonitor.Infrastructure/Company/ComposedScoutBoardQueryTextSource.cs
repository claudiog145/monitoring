using System.Reflection;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.UseCases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Infrastructure.Company;

/// <summary>
/// Builds the Scout board query from the company's <see cref="CompanyProfile"/> instead of reading
/// a board that was written for one customer. This is what makes onboarding an enterprise a row
/// rather than a new KQL file.
///
/// Off by default (<c>Scout:UseComposedBoard</c>). The composed board classifies correctly, but a
/// board row is an AGGREGATE: it carries the resolved entity value and not the dimension that
/// produced it, so <see cref="ScoutRowMapper"/> cannot re-derive WHICH identity source answered.
/// Until the board projects that measurement through its summarise/join stages, turning this on
/// would degrade identity attribution for non-host entities - and that degradation is invisible
/// from the outside, which is exactly the kind of change this repo refuses to make unverified.
/// </summary>
public sealed class ComposedScoutBoardQueryTextSource(
    ICompanyProfileStore profiles,
    TelemetrySourceOwnershipCheck? ownership = null,
    ILogger<ComposedScoutBoardQueryTextSource>? logger = null) : IScoutBoardQueryTextSource
{
    public async Task<string> GetBoardQueryTextAsync(ScoutBoardQueryTextRequest request, CancellationToken ct = default)
    {
        var profile = await profiles.GetAsync(request.ProfileKey, ct)
            ?? throw new CompanyProfileResolutionException(
                $"No enabled company profile is stored under \"{request.ProfileKey}\", so no board query can be composed. " +
                "A board that silently returned nothing would be indistinguishable from a healthy system.");

        profile = await WithoutForeignSourcesAsync(profile, request.Board, ct);

        return ScoutBoardQueryComposer.Compose(
            profile,
            new ScoutBoardQueryRequest(
                KindOf(request.Board),
                request.WindowFrom,
                request.WindowTo,
                request.BucketMinutes,
                request.RowsPerEntity),
            ReadTemplate());
    }

    /// <summary>
    /// FX1 read-time ownership: an alias the catalog gives to another profile (e.g. after a transfer)
    /// is dropped from the board with a Warning, so the rest of the company's sources still run. When
    /// nothing is left the board fails loudly - an empty union would look like a quiet system.
    /// </summary>
    private async Task<CompanyProfile> WithoutForeignSourcesAsync(CompanyProfile profile, string board, CancellationToken ct)
    {
        if (ownership is null) return profile;

        var declared = (profile.Services ?? []).SelectMany(s => s?.TelemetrySourceAliases ?? []).ToList();
        var foreign = await ownership.FindForeignOrBuiltInAsync(profile.ProfileKey, declared, ct);
        if (foreign.Count == 0) return profile;

        var skipped = new HashSet<string>(foreign.Select(f => f.Alias), StringComparer.OrdinalIgnoreCase);
        foreach (var f in foreign)
        {
            (logger ?? (ILogger)NullLogger.Instance).LogWarning(
                "[Scout] The \"{Board}\" board of profile \"{Profile}\" skips telemetry source \"{Alias}\": it belongs to profile \"{Owner}\".",
                board, profile.ProfileKey, f.Alias, f.Owner);
        }

        var services = (profile.Services ?? [])
            .Select(s => s with { TelemetrySourceAliases = (s.TelemetrySourceAliases ?? []).Where(a => !skipped.Contains((a ?? "").Trim())).ToList() })
            .ToList();
        if (!services.SelectMany(s => s.TelemetrySourceAliases).Any(a => !string.IsNullOrWhiteSpace(a)))
        {
            throw new CompanyProfileResolutionException(
                $"The \"{board}\" scout board of profile \"{profile.ProfileKey}\" has no telemetry source it may read: " +
                string.Join(" ", foreign.Select(f => f.Message)));
        }
        return profile with { Services = services };
    }

    private static ScoutBoardKind KindOf(string board) =>
        board?.Trim().StartsWith("exception", StringComparison.OrdinalIgnoreCase) == true
            ? ScoutBoardKind.Exception
            : ScoutBoardKind.Latency;

    private static string ReadTemplate()
    {
        var assembly = typeof(ComposedScoutBoardQueryTextSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ScoutBoardQueryComposer.TemplateResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded board template \"{ScoutBoardQueryComposer.TemplateResourceName}\" is missing from {assembly.GetName().Name}.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
