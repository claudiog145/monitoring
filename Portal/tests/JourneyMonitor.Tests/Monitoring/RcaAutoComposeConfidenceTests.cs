using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Tests.Monitoring;

public class RcaAutoComposeConfidenceTests
{
    private static ForensicCase Case(string? rootCause, double confidence) => new()
    {
        InvestigationPackageId = Guid.NewGuid(), CaseId = "c", EntityKey = "e", Vertical = "v", Stage = "s",
        ProbableRootCause = rootCause, Confidence = confidence,
    };

    [Fact]
    public void NamedRootCause_AlwaysQualifies()
    {
        var c = Case("pool exhausted", 0.1);
        Assert.True(RcaAutoComposer.HasEnoughToCompose(c, new RcaOptions()));
    }

    [Fact]
    public void NoRootCause_WithoutTheSetting_DoesNotQualify()
    {
        var c = Case(null, 0.9);
        Assert.False(RcaAutoComposer.HasEnoughToCompose(c, new RcaOptions()));
    }

    [Theory]
    [InlineData(0.31, true)]
    [InlineData(0.30, false)]
    [InlineData(0.15, false)]
    public void NoRootCause_QualifiesOnlyAboveTheConfiguredConfidence(double confidence, bool expected)
    {
        var c = Case("", confidence);
        Assert.Equal(expected, RcaAutoComposer.HasEnoughToCompose(c, new RcaOptions { AutoComposeMinConfidence = 0.30 }));
    }
}
