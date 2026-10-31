using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

public class InspectorVerdictAndGroundingTests
{
    // ---------------------------------------------------------------------------------------------
    // Grounding: three outcomes, never two
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnIdentifierThatIsInTheCaseVerifiesAndOneThatIsNotIsReportedAsFabricated()
    {
        var artifacts = await VerifyAsync("""
            {"citations":[{"kind":"operation-id","value":"op-aaa","citedBy":"LogsAgent"},
                          {"kind":"operation-id","value":"op-invented","citedBy":"moa:model-a"}]}
            """);

        Assert.Equal(ArtifactVerification.Verified, artifacts[0].Verification);
        Assert.Equal(ArtifactVerification.NotFound, artifacts[1].Verification);
        Assert.Contains("made up", artifacts[1].Note);
    }

    [Fact]
    public async Task AFilePathIsNotVerifiableBecauseNoCodeIndexExistsToCheckItAgainst()
    {
        // "Checked and absent" and "we have no index to check against" are opposite statements.
        // Collapsing them either slanders a correct answer or launders a fabricated one.
        var artifacts = await VerifyAsync("""
            {"citations":[{"kind":"file-path","value":"src/Booking/VendorClient.cs","citedBy":"CodeAgent"}]}
            """);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(ArtifactVerification.NotVerifiable, artifact.Verification);
        Assert.NotEqual(ArtifactVerification.Verified, artifact.Verification);
        Assert.NotEqual(ArtifactVerification.NotFound, artifact.Verification);
    }

    [Fact]
    public async Task AKindTheCaseCarriesNoIndexForIsNotVerifiableRatherThanNotFound()
    {
        // This package carries no retrieved knowledge chunks at all, so "absent" would be an
        // accusation the evidence does not support.
        var artifacts = await VerifyAsync("""
            {"citations":[{"kind":"knowledge-chunk","value":"some-chunk","citedBy":"MemoryAgent"}]}
            """);

        Assert.Equal(ArtifactVerification.NotVerifiable, Assert.Single(artifacts).Verification);
    }

    [Fact]
    public async Task ASynthesisThatCitesNothingProducesNoArtifactsRatherThanAnError()
    {
        Assert.Empty(await VerifyAsync("""{"conclusion":"insufficient_evidence","citations":[]}"""));
        Assert.Empty(await VerifyAsync("this is not JSON at all"));
    }

    [Fact]
    public async Task AnIdentifierATookPulledOutOfTelemetryVerifiesEvenThoughThePackageNeverCarriedIt()
    {
        // The package is a 5-minute snapshot; a tool queries a 7-day window. Checking citations
        // against the package alone would accuse the model of inventing a real operation id.
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance);
        var package = InspectorFixtures.Package();
        var context = InspectorFixtures.Context(package);

        var turn = await recorder.RecordDeterministicTurnAsync(
            context.Case, InspectorAgent.Logs, AgentKind.Specialist, "prompt", "response", null, 0, CancellationToken.None);
        await recorder.RecordToolCallAsync(new AgentToolCall
        {
            AgentTurnId = turn,
            ToolName = "club_baseline",
            ArgumentsJson = "{}",
            ResultJson = """{"rows":[{"OperationId":"op-from-the-query","ExceptionType":"System.Net.Http.HttpRequestException"}]}""",
            Success = true,
        }, CancellationToken.None);

        var service = new InspectorGroundingService(transcripts, recorder, NullLogger<InspectorGroundingService>.Instance);
        var artifacts = await service.VerifyAsync(context, """
            {"citations":[{"kind":"operation-id","value":"op-from-the-query","citedBy":"LogsAgent"},
                          {"kind":"exception-type","value":"System.Net.Http.HttpRequestException","citedBy":"LogsAgent"}]}
            """);

        Assert.All(artifacts, a => Assert.Equal(ArtifactVerification.Verified, a.Verification));
    }

    // ---------------------------------------------------------------------------------------------
    // Verdict: deterministic policy
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResolvesOnlyWhenConfidenceAndTheVerifiedCitationRatioAreBothMet()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","confidence":0.9,"unknowns":[]}""",
            [Artifact("op-aaa", ArtifactVerification.Verified)]);

        Assert.Equal(InspectorDecision.Resolve, verdict.Decision);
        Assert.Null(verdict.BlockedBy);
    }

    [Fact]
    public void EscalatesWithABlockerWhenTheConfidenceIsBelowTheBar()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","confidence":0.5,"unknowns":[]}""",
            [Artifact("op-aaa", ArtifactVerification.Verified)]);

        Assert.Equal(InspectorDecision.Escalate, verdict.Decision);
        Assert.Contains("confidence", verdict.BlockedBy);
    }

    [Fact]
    public void EscalatesWithABlockerWhenSomeCitationsCouldNotBeVerified()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","confidence":0.95,"unknowns":[]}""",
            [Artifact("op-aaa", ArtifactVerification.Verified), Artifact("src/X.cs", ArtifactVerification.NotVerifiable)]);

        Assert.Equal(InspectorDecision.Escalate, verdict.Decision);
        // Unverifiable is reported as its own thing: it is a statement about this platform's
        // missing code index, not an accusation that the model invented the path.
        Assert.Contains("could not be checked", verdict.BlockedBy);
        Assert.DoesNotContain("did not exist", verdict.BlockedBy);
    }

    [Fact]
    public void AFabricatedCitationIsCalledOutSeparatelyFromAnUnverifiableOne()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","confidence":0.95,"unknowns":[]}""",
            [
                Artifact("op-aaa", ArtifactVerification.Verified),
                Artifact("op-invented", ArtifactVerification.NotFound),
                Artifact("src/X.cs", ArtifactVerification.NotVerifiable),
            ]);

        Assert.Equal(InspectorDecision.Escalate, verdict.Decision);
        Assert.Contains("1 did not exist", verdict.BlockedBy);
        Assert.Contains("1 could not be checked", verdict.BlockedBy);
    }

    [Fact]
    public void CitingNothingIsInconclusiveRatherThanAConfidentResolution()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"definitely the vendor","confidence":0.99,"unknowns":[]}""",
            []);

        Assert.Equal(InspectorDecision.Inconclusive, verdict.Decision);
        Assert.True(verdict.Confidence <= 0.3);
        Assert.Contains("cited no artifact", verdict.BlockedBy);
    }

    [Fact]
    public void NothingVerifyingAtAllIsInconclusiveEvenWithAConfidentSynthesis()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"definitely the vendor","confidence":0.99,"unknowns":[]}""",
            [Artifact("src/X.cs", ArtifactVerification.NotVerifiable)]);

        Assert.Equal(InspectorDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void AnUnreadableSynthesisIsInconclusiveRatherThanAnException()
    {
        Assert.Equal(InspectorDecision.Inconclusive, Decide("not json", []).Decision);
    }

    // ---------------------------------------------------------------------------------------------
    // Probable root cause: its own field, not buried in the synthesis blob
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheProbableRootCauseIsCarriedOnAResolution()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","rootCause":"the vendor search API stopped responding","confidence":0.9,"unknowns":[]}""",
            [Artifact("op-aaa", ArtifactVerification.Verified)]);

        Assert.Equal(InspectorDecision.Resolve, verdict.Decision);
        Assert.Equal("the vendor search API stopped responding", verdict.ProbableRootCause);
    }

    [Fact]
    public void TheProbableRootCauseIsCarriedOnAnEscalationToo()
    {
        // Naming the likely cause and being able to close the case are different claims. Dropping
        // the first because we cannot make the second makes the investigation look emptier than it
        // was - and this is the common path, because today almost everything escalates.
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","rootCause":"the vendor search API stopped responding","confidence":0.4,"unknowns":[]}""",
            [Artifact("op-aaa", ArtifactVerification.Verified)]);

        Assert.Equal(InspectorDecision.Escalate, verdict.Decision);
        Assert.Equal("the vendor search API stopped responding", verdict.ProbableRootCause);
    }

    [Fact]
    public void NothingVerifiedMeansNoProbableRootCauseHoweverConfidentTheSynthesisSounded()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"definitely the vendor","rootCause":"the vendor is down","confidence":0.99,"unknowns":[]}""",
            [Artifact("src/X.cs", ArtifactVerification.NotVerifiable)]);

        Assert.Equal(InspectorDecision.Inconclusive, verdict.Decision);
        Assert.Null(verdict.ProbableRootCause);
    }

    [Fact]
    public void ABlankRootCauseIsNullRatherThanAFieldThatRendersAsAConclusion()
    {
        var verdict = Decide(
            """{"conclusion":"root_cause","summary":"vendor timeouts","rootCause":"   ","confidence":0.9,"unknowns":[]}""",
            [Artifact("op-aaa", ArtifactVerification.Verified)]);

        Assert.Null(verdict.ProbableRootCause);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static CitedArtifact Artifact(string value, string verification) =>
        new(ArtifactKind.Other, value, "test", verification, null);

    private static InspectorVerdict Decide(string synthesisJson, IReadOnlyList<CitedArtifact> artifacts)
    {
        var package = InspectorFixtures.Package();
        var service = new InspectorVerdictService(Options.Create(new InspectorOptions()));
        return service.Decide(InspectorFixtures.Context(package), synthesisJson, artifacts);
    }

    private static async Task<IReadOnlyList<CitedArtifact>> VerifyAsync(string synthesisJson)
    {
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance);
        var service = new InspectorGroundingService(transcripts, recorder, NullLogger<InspectorGroundingService>.Instance);
        return await service.VerifyAsync(InspectorFixtures.Context(InspectorFixtures.Package()), synthesisJson);
    }
}
