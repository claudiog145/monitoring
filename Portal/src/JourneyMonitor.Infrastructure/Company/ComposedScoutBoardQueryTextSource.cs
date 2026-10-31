using System.Reflection;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Monitoring;

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
public sealed class ComposedScoutBoardQueryTextSource(ICompanyProfileStore profiles) : IScoutBoardQueryTextSource
{
    public async Task<string> GetBoardQueryTextAsync(ScoutBoardQueryTextRequest request, CancellationToken ct = default)
    {
        var profile = await profiles.GetAsync(request.ProfileKey, ct)
            ?? throw new CompanyProfileResolutionException(
                $"No enabled company profile is stored under \"{request.ProfileKey}\", so no board query can be composed. " +
                "A board that silently returned nothing would be indistinguishable from a healthy system.");

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
