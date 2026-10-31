using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Onboarding;

namespace JourneyMonitor.Tests.Onboarding;

public class EntityIdentityCandidateRankerTests
{
    [Fact]
    public void AKeyThatIsUniquePerRowScoresZeroOnCardinality()
    {
        // 100% coverage, but a correlation id identifies an event, not a customer.
        var candidate = EntityIdentityCandidateRanker.Score("requests", Dimension("OperationId", nonEmpty: 50_000, distinct: 50_000, maxLength: 36, sampled: 50_000));

        Assert.Equal(0.55, candidate.Score, 4);
        Assert.Contains(candidate.Reasons, r => r.Contains("identifies an event", StringComparison.Ordinal));
    }

    [Fact]
    public void AConstantKeyScoresZeroOnCardinality()
    {
        var candidate = EntityIdentityCandidateRanker.Score("requests", Dimension("Environment", nonEmpty: 50_000, distinct: 1, maxLength: 4, sampled: 50_000));

        Assert.Equal(0.55, candidate.Score, 4);
        Assert.Contains(candidate.Reasons, r => r.Contains("Only one distinct value", StringComparison.Ordinal));
    }

    [Fact]
    public void AFreeTextKeyIsFlaggedAndScoresZeroOnCardinality()
    {
        var candidate = EntityIdentityCandidateRanker.Score("exceptions", Dimension("Message", nonEmpty: 50_000, distinct: 400, maxLength: 4_000, sampled: 50_000));

        Assert.True(candidate.LooksFreeText);
        Assert.Equal(0.55, candidate.Score, 4);
        Assert.Contains(candidate.Reasons, r => r.Contains("free text", StringComparison.Ordinal));
    }

    /// <summary>
    /// The core promise: a key almost nobody carries comes back as a candidate with its real
    /// number, because "there is no usable identity key here" is the finding, not an empty list.
    /// </summary>
    [Fact]
    public void ALowCoverageKeyIsReportedWithItsRealCoverage()
    {
        var candidate = EntityIdentityCandidateRanker.Score("requests", Dimension("VacationClubId", nonEmpty: 2_000, distinct: 40, maxLength: 5, sampled: 50_000));

        Assert.Equal(4.0, candidate.NonEmptyCoveragePercent);
        Assert.True(candidate.Score < 0.5, $"A 4%-coverage key must not rank as usable (scored {candidate.Score}).");
        Assert.Contains(candidate.Reasons, r => r.Contains("4% of sampled rows", StringComparison.Ordinal));
    }

    [Fact]
    public void BlankValuesAreCalledOutSeparatelyFromAbsentOnes()
    {
        var dimension = new CustomDimensionProfile("ClubId", OccurrenceCount: 50_000, NonEmptyCount: 10_000,
            DistinctValueCount: 40, MaxValueLength: 5, CoveragePercent: 100.0, NonEmptyCoveragePercent: 20.0);

        var candidate = EntityIdentityCandidateRanker.Score("requests", dimension);

        Assert.Contains(candidate.Reasons, r => r.Contains("blank value identifies nobody", StringComparison.Ordinal));
        Assert.Equal(100.0, candidate.CoveragePercent);
        Assert.Equal(20.0, candidate.NonEmptyCoveragePercent);
    }

    [Fact]
    public void AHighCoverageModerateCardinalityKeyOutranksEverythingElse()
    {
        var ranked = EntityIdentityCandidateRanker.Rank(
        [
            ("requests", Dimension("OperationId", 50_000, 50_000, 36, 50_000)),
            ("requests", Dimension("VacationClubId", 45_000, 40, 5, 50_000)),
            ("requests", Dimension("IsAuthenticated", 50_000, 2, 5, 50_000)),
        ]);

        Assert.Equal("VacationClubId", ranked[0].Key);
        Assert.True(ranked[0].Score > 0.9, $"Expected a strong identity key to score above 0.9, got {ranked[0].Score}.");
        Assert.Equal(EntityIdentitySourceKind.CustomDimension, ranked[0].Kind);
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void RankingIsDeterministicWhenScoresTie()
    {
        var a = ("requests", Dimension("Bbb", 1_000, 10, 4, 10_000));
        var b = ("requests", Dimension("Aaa", 1_000, 10, 4, 10_000));

        Assert.Equal(["Aaa", "Bbb"], EntityIdentityCandidateRanker.Rank([a, b]).Select(c => c.Key).ToArray());
        Assert.Equal(["Aaa", "Bbb"], EntityIdentityCandidateRanker.Rank([b, a]).Select(c => c.Key).ToArray());
    }

    private static CustomDimensionProfile Dimension(string key, long nonEmpty, long distinct, int maxLength, long sampled) =>
        new(key, nonEmpty, nonEmpty, distinct, maxLength,
            Math.Round(100.0 * nonEmpty / sampled, 2), Math.Round(100.0 * nonEmpty / sampled, 2));
}
