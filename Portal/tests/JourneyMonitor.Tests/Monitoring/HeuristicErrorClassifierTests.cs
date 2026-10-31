using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

public class HeuristicErrorClassifierTests
{
    private static Finding FindingWithEvidence(string evidenceJson) => new()
    {
        Domain = "cars",
        FindingType = FindingType.LatencySpikeCluster,
        Stage = "07 Booking",
        Title = "test",
        Severity = "high",
        EvidenceJson = evidenceJson,
        OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
        CorrelationId = "cars:07 Booking",
    };

    private readonly HeuristicErrorClassifier _classifier = new();

    [Fact]
    public void AllSuccessfulButSlowClassifiesAsServerInfrastructure()
    {
        var finding = FindingWithEvidence("""{"statusCodeBreakdown":{"200":10}}""");
        Assert.Equal("server_infrastructure", _classifier.Classify(finding));
    }

    [Theory]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    public void GatewayErrorsClassifyAsExternalDependency(string code)
    {
        var finding = FindingWithEvidence("{\"statusCodeBreakdown\":{\"200\":5,\"" + code + "\":3}}");
        Assert.Equal("external_dependency", _classifier.Classify(finding));
    }

    [Fact]
    public void ServerErrorClassifiesAsApplicationCode()
    {
        var finding = FindingWithEvidence("""{"statusCodeBreakdown":{"500":4}}""");
        Assert.Equal("application_code", _classifier.Classify(finding));
    }

    [Fact]
    public void MissingEvidenceClassifiesAsUnknownRatherThanGuessing()
    {
        var finding = FindingWithEvidence("""{}""");
        Assert.Equal("unknown", _classifier.Classify(finding));
    }

    [Fact]
    public void MalformedEvidenceJsonClassifiesAsUnknownRatherThanThrowing()
    {
        var finding = FindingWithEvidence("not json");
        Assert.Equal("unknown", _classifier.Classify(finding));
    }

    [Fact]
    public void AllHealthyButSlowClassifiesAsServerInfrastructure()
    {
        var finding = FindingWithEvidence("""{"healthStatusBreakdown":{"GREEN":8,"N/A":2}}""");
        Assert.Equal("server_infrastructure", _classifier.Classify(finding));
    }

    [Theory]
    [InlineData("RED")]
    [InlineData("YELLOW")]
    public void HealthStatusWithoutRealStatusCodesClassifiesAsUnknownRatherThanGuessing(string status)
    {
        var finding = FindingWithEvidence("{\"healthStatusBreakdown\":{\"" + status + "\":3,\"GREEN\":2}}");
        Assert.Equal("unknown", _classifier.Classify(finding));
    }

    [Fact]
    public void EmptyHealthStatusBreakdownClassifiesAsUnknown()
    {
        var finding = FindingWithEvidence("""{"healthStatusBreakdown":{}}""");
        Assert.Equal("unknown", _classifier.Classify(finding));
    }
}
