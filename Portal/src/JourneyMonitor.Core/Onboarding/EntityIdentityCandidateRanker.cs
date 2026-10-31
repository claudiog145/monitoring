using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Ranks measured <c>customDimensions</c> keys by how well they could answer "who is affected".
///
/// Ranking, never filtering. Every key that was measured comes back as a candidate, including the
/// ones that score zero, because the finding this component exists to deliver is usually that
/// NOTHING scores well - and a list that quietly drops the 4% key looks identical to a list that
/// never saw it. <see cref="EntityIdentityCandidate.Reasons"/> carries the arithmetic so the
/// score can be argued with instead of trusted.
///
/// The name hints are a tie-breaker worth 15% at most, deliberately: they are Arrivia-shaped
/// vocabulary, and a new customer's identity key is just as likely to be called something this
/// list has never heard of. Coverage and cardinality are what actually decide the order.
/// </summary>
public static class EntityIdentityCandidateRanker
{
    /// <summary>A value longer than this is prose (a message, a URL, a stack frame), not an identifier.</summary>
    public const int FreeTextValueLength = 120;

    /// <summary>Distinct-value count at which "rich enough to distinguish customers" saturates.</summary>
    private const double RichnessSaturation = 50.0;

    private const double CoverageWeight = 0.55;
    private const double CardinalityWeight = 0.30;
    private const double NameHintWeight = 0.15;

    private static readonly string[] NameHints =
    [
        "club", "tenant", "partner", "customer", "account", "member", "org", "organization",
        "company", "brand", "site", "affiliate", "merchant", "subscriber", "user",
    ];

    public static IReadOnlyList<EntityIdentityCandidate> Rank(IEnumerable<(string Table, CustomDimensionProfile Dimension)> measured) =>
        measured
            .Select(m => Score(m.Table, m.Dimension))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.NonEmptyCoveragePercent)
            .ThenByDescending(c => c.NonEmptyCount)
            .ThenBy(c => c.Table, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

    public static EntityIdentityCandidate Score(string table, CustomDimensionProfile dimension)
    {
        var reasons = new List<string>();

        var coverage = Math.Clamp(dimension.NonEmptyCoveragePercent / 100.0, 0.0, 1.0);
        reasons.Add($"Present with a non-empty value on {Format(dimension.NonEmptyCoveragePercent)}% of sampled rows ({dimension.NonEmptyCount:N0}).");
        if (dimension.CoveragePercent > dimension.NonEmptyCoveragePercent + 0.01)
        {
            reasons.Add($"Key is present on {Format(dimension.CoveragePercent)}% of rows but blank on {Format(dimension.CoveragePercent - dimension.NonEmptyCoveragePercent)}% - a blank value identifies nobody.");
        }

        var looksFreeText = dimension.MaxValueLength > FreeTextValueLength;
        var cardinality = CardinalityFitness(dimension, looksFreeText, reasons);

        var nameHint = NameHints.Any(h => dimension.Key.Contains(h, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.0;
        if (nameHint > 0) reasons.Add("Key name resembles an entity identifier - a weak hint only, worth 15% of the score.");

        var score = Math.Round(
            (CoverageWeight * coverage) + (CardinalityWeight * cardinality) + (NameHintWeight * nameHint),
            4);

        return new EntityIdentityCandidate(
            Table: table,
            Key: dimension.Key,
            Kind: EntityIdentitySourceKind.CustomDimension,
            Score: score,
            CoveragePercent: dimension.CoveragePercent,
            NonEmptyCoveragePercent: dimension.NonEmptyCoveragePercent,
            NonEmptyCount: dimension.NonEmptyCount,
            DistinctValueCount: dimension.DistinctValueCount,
            MaxValueLength: dimension.MaxValueLength,
            LooksFreeText: looksFreeText,
            Reasons: reasons);
    }

    /// <summary>
    /// How identifier-shaped the values are, in [0,1] - the product of two independent failures:
    /// a key whose value is unique per row (a correlation id) identifies an event rather than a
    /// customer, and a key with two values (a flag) cannot tell customers apart at all.
    /// </summary>
    private static double CardinalityFitness(CustomDimensionProfile dimension, bool looksFreeText, List<string> reasons)
    {
        if (looksFreeText)
        {
            reasons.Add($"Longest observed value is {dimension.MaxValueLength} characters - free text, not an identifier.");
            return 0.0;
        }
        if (dimension.NonEmptyCount <= 0)
        {
            reasons.Add("Never carries a non-empty value in the sampled window.");
            return 0.0;
        }
        if (dimension.DistinctValueCount <= 1)
        {
            reasons.Add("Only one distinct value - constant, so it cannot distinguish one customer from another.");
            return 0.0;
        }

        var uniqueness = 1.0 - Math.Clamp((double)dimension.DistinctValueCount / dimension.NonEmptyCount, 0.0, 1.0);
        var richness = Math.Clamp(Math.Log(dimension.DistinctValueCount) / Math.Log(RichnessSaturation), 0.0, 1.0);

        if (uniqueness < 0.1)
        {
            reasons.Add($"Roughly one distinct value per row ({dimension.DistinctValueCount:N0} over {dimension.NonEmptyCount:N0}) - this identifies an event, not a customer.");
        }
        if (richness < 0.5)
        {
            reasons.Add($"Only {dimension.DistinctValueCount:N0} distinct values - closer to a flag than to a customer identifier.");
        }

        return Math.Clamp(uniqueness * richness, 0.0, 1.0);
    }

    private static string Format(double percent) => percent.ToString("0.##");
}
