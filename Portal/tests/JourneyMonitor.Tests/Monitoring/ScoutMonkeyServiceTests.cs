using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// A cycle covers one board since the scouts were split. The pipeline is board-agnostic, so these
/// tests drive it through the latency board and only name the board explicitly where the point of
/// the test is that the two scouts do not interfere with each other.
/// </summary>
internal static class ScoutServiceTestExtensions
{
    public static Task<ScoutRun> RunAsync(this ScoutMonkeyService service, CancellationToken ct = default) =>
        service.RunAsync(ScoutBoard.Latency, ct);
}

public class ScoutMonkeyServiceTests
{
    [Fact]
    public async Task RecordsAClearCycleWhenNothingReachesTheEscalationRank()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 2), Row("case-b", rank: 1)]);
        var (service, packages, runs) = Build(reader);

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Clear, run.Status);
        Assert.Equal(0, run.PackagesCreated);
        // A quiet cycle is still recorded with what it looked at.
        Assert.Equal(2, run.BoardRowsEvaluated);
        Assert.Empty(await packages.GetRecentAsync(10));
        Assert.Single(await runs.GetRecentAsync(10));
    }

    [Fact]
    public async Task PackagesEveryCandidateAtOrAboveTheConfiguredRank()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3), Row("case-b", rank: 2)]);
        var (service, packages, _) = Build(reader);

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Detected, run.Status);
        Assert.Equal(1, run.PackagesCreated);
        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Equal("case-a", created.CaseId);
        Assert.Equal(InvestigationPackageStatus.PendingHandoff, created.Status);
    }

    [Fact]
    public async Task DoesNotPackageTheSameCaseIdTwiceAcrossCycles()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync();
        var second = await service.RunAsync();

        Assert.Equal(0, second.PackagesCreated);
        Assert.Equal(1, second.PackagesSkippedDuplicate);
        Assert.Single(await packages.GetRecentAsync(10));
    }

    [Fact]
    public async Task EachScoutDedupsIndependentlyOfTheOther()
    {
        // The same group and bucket can be flagged by both boards for different reasons. Losing
        // one of them to whichever scout ran first is exactly what the (CaseId, Board) key
        // prevents - a shared key would make this second cycle report a duplicate and package
        // nothing.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync(ScoutBoard.Latency);
        var exceptionRun = await service.RunAsync(ScoutBoard.Exception);

        Assert.Equal(1, exceptionRun.PackagesCreated);
        Assert.Equal(0, exceptionRun.PackagesSkippedDuplicate);
        var created = await packages.GetRecentAsync(10);
        Assert.Equal(2, created.Count);
        Assert.All(created, p => Assert.Equal("case-a", p.CaseId));
        Assert.Equal([ScoutBoard.Exception, ScoutBoard.Latency], created.Select(p => p.Board).OrderBy(b => b));
    }

    [Fact]
    public async Task SkipsTheLlmEntirelyForAlreadyKnownCases()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var assessor = new FakeAssessor();
        var (service, _, _) = Build(reader, assessor: assessor);

        await service.RunAsync();
        var callsAfterFirstCycle = assessor.Calls;
        await service.RunAsync();

        Assert.Equal(callsAfterFirstCycle, assessor.Calls);
    }

    [Fact]
    public async Task StopsAtTheCapAndCountsWhatItDropped()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row($"case-{i}", rank: 3)).ToList();
        var reader = new FakeBoardReader(rows);
        var (service, packages, _) = Build(reader, o => o.MaxNewCasesPerRun = 2);

        var run = await service.RunAsync();

        Assert.Equal(2, run.PackagesCreated);
        Assert.Equal(2, (await packages.GetRecentAsync(50)).Count);
        // Nothing is dropped silently: every candidate is either packaged or counted by the cap.
        Assert.Equal(5, run.CandidatesFound);
        Assert.Equal(5, run.PackagesCreated + run.PackagesSkippedCap + run.PackagesSkippedDuplicate);
        Assert.Equal(3, run.PackagesSkippedCap);
        Assert.Equal(0, run.PackagesSkippedDuplicate);
    }

    [Fact]
    public async Task OrdersCandidatesByRankThenByRequestVolume()
    {
        var reader = new FakeBoardReader(
        [
            Row("low-volume", rank: 3, requests: 5),
            Row("high-volume", rank: 3, requests: 900),
        ]);
        var (service, packages, _) = Build(reader, o => o.MaxNewCasesPerRun = 1);

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Equal("high-volume", created.CaseId);
    }

    [Fact]
    public async Task ShipsThePackageWithFactsOnlyWhenTheLlmIsUnavailable()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var assessor = new FakeAssessor(_ => ScoutAssessment.Unavailable("no provider configured"));
        var (service, packages, _) = Build(reader, assessor: assessor);

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        using var assessment = JsonDocument.Parse(created.AssessmentJson);
        Assert.False(assessment.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("no provider configured", assessment.RootElement.GetProperty("unavailableReason").GetString());
        // The board's own evidence is still there - an unavailable LLM must not cost L3 the facts.
        using var evidence = JsonDocument.Parse(created.DeterministicJson);
        Assert.Equal("case-a", evidence.RootElement.GetProperty("caseId").GetString());
        Assert.True(evidence.RootElement.TryGetProperty("groupPayload", out _));
    }

    [Fact]
    public async Task KeepsTheBoardsVerdictWhenTheScoutDisagreesAndVetoIsOff()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var assessor = new FakeAssessor(_ => Assessed(shouldEscalate: false, why: "looks like a known crawler"));
        var (service, packages, _) = Build(reader, assessor: assessor);

        var run = await service.RunAsync();

        Assert.Equal(1, run.PackagesCreated);
        Assert.Equal(0, run.PackagesVetoedByScout);
        // The disagreement is still recorded on the package, it just does not gate the handoff.
        var created = Assert.Single(await packages.GetRecentAsync(10));
        using var assessment = JsonDocument.Parse(created.AssessmentJson);
        Assert.False(assessment.RootElement.GetProperty("shouldEscalate").GetBoolean());
    }

    [Fact]
    public async Task HonoursTheScoutVetoOnlyWhenItIsExplicitlyEnabled()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var assessor = new FakeAssessor(_ => Assessed(shouldEscalate: false, why: "looks like a known crawler"));
        var (service, packages, _) = Build(reader, o => o.AllowScoutVeto = true, assessor);

        var run = await service.RunAsync();

        Assert.Equal(0, run.PackagesCreated);
        Assert.Equal(1, run.PackagesVetoedByScout);
        Assert.Empty(await packages.GetRecentAsync(10));
    }

    [Fact]
    public async Task NeverVetoesOnAnUnavailableAssessment()
    {
        // ScoutAssessment.Unavailable defaults ShouldEscalate to true precisely so a broken LLM
        // cannot silently suppress detection - assert that even with the veto switched on.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var assessor = new FakeAssessor(_ => ScoutAssessment.Unavailable("call failed"));
        var (service, packages, _) = Build(reader, o => o.AllowScoutVeto = true, assessor);

        var run = await service.RunAsync();

        Assert.Equal(1, run.PackagesCreated);
        Assert.Single(await packages.GetRecentAsync(10));
    }

    [Fact]
    public async Task MarksTheCycleFailedAndKeepsTheErrorWhenABoardCannotBeRead()
    {
        // A board that cannot run - expired credentials, missing RBAC on one of the three
        // Application Insights resources - must never look like a quiet cycle.
        var reader = new FakeBoardReader([], throwOnRead: new InvalidOperationException("403 from Application Insights"));
        var (service, packages, runs) = Build(reader);

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Failed, run.Status);
        Assert.Contains("403", run.ErrorMessage);
        Assert.Empty(await packages.GetRecentAsync(10));
        Assert.Equal(ScoutRunStatus.Failed, (await runs.GetRecentAsync(10))[0].Status);
    }

    [Fact]
    public async Task RecordsADisabledCycleWithoutQueryingTheBoards()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, runs) = Build(reader, o => o.Enabled = false);

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Disabled, run.Status);
        Assert.Equal(0, reader.ReadCount);
        Assert.Empty(await packages.GetRecentAsync(10));
        Assert.Single(await runs.GetRecentAsync(10));
    }

    [Fact]
    public async Task CarriesTheResolvedClubIdentityOntoThePackage()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var club = new ClubIdentity("host:stays.tmobiletravel.com", ClubEntityKind.Host, "15639", "308", "T-Mobile", "T-Mobile Travel", ClubResolution.HostExactMatch);
        var (service, packages, _) = Build(reader, resolver: new FakeClubResolver(club));

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Equal("T-Mobile", created.BrandName);
        Assert.Equal("T-Mobile Travel", created.ClubName);
        Assert.Equal("15639", created.ClubId);
        Assert.Equal("308", created.IcePartnerId);
        Assert.Equal(ClubResolution.HostExactMatch, created.ClubResolvedBy);
    }

    [Fact]
    public async Task LeavesTheBrandNullWhenTheClubDoesNotResolve()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var club = new ClubIdentity("host:members.jifutravel.net", ClubEntityKind.Host, null, null, null, null, ClubResolution.Unresolved);
        var (service, packages, _) = Build(reader, resolver: new FakeClubResolver(club));

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Null(created.BrandName);
        Assert.Equal(ClubResolution.Unresolved, created.ClubResolvedBy);
    }

    [Fact]
    public async Task CopiesTheBoardsImpactCountersOntoThePackage()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3, requests: 174, failed: 9, exceptions: 42, users: 7)]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Equal(174, created.RequestCount);
        Assert.Equal(9, created.FailedRequestCount);
        Assert.Equal(42, created.ExceptionCount);
        Assert.Equal(7, created.AffectedUserCount);
    }

    // -----------------------------------------------------------------------------------------
    // The company profile, not the query text, is what names things
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoServicesSharingOneHostArriveWithTheirOwnServiceAndStep()
    {
        // A host cannot be the discriminator when one company serves several product areas from it.
        // The rule that recognised the row is the only thing that knows which service it was.
        var reader = new FakeBoardReader([
            Row("case-a", rank: 3, path: "/checkout"),
            Row("case-b", rank: 3, path: "/ticket"),
        ]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync();

        var created = (await packages.GetRecentAsync(10)).OrderBy(p => p.CaseId, StringComparer.Ordinal).ToList();
        Assert.Equal(["Retail", "Support"], created.Select(p => p.Vertical));
        Assert.Equal(["Checkout", "Ticket"], created.Select(p => p.Stage));
    }

    [Fact]
    public async Task TheSameBoardRowsUnderADifferentProfileProduceADifferentVocabulary()
    {
        // The proof that the pipeline is company-independent: identical rows in, a different set of
        // words out, with nothing changed but which profile answered.
        ScoutBoardRow[] Rows() => [Row("case-a", rank: 3, path: "/checkout")];

        var (first, firstPackages, _) = Build(new FakeBoardReader(Rows()));
        var (second, secondPackages, _) = Build(
            new FakeBoardReader(Rows()),
            adapters: new FakeCompanyAdapterProvider(ScoutCompanyProfiles.RenamedVocabulary()));

        await first.RunAsync();
        await second.RunAsync();

        var a = Assert.Single(await firstPackages.GetRecentAsync(10));
        var b = Assert.Single(await secondPackages.GetRecentAsync(10));
        Assert.Equal(("Retail", "Checkout"), (a.Vertical, a.Stage));
        Assert.Equal(("Commerce", "Basket"), (b.Vertical, b.Stage));
        // The dedup key is the board's own and does not move with the vocabulary - a package that
        // changed CaseId when a label was renamed could never be found again by the dedup check.
        Assert.Equal(a.CaseId, b.CaseId);
    }

    [Fact]
    public async Task AnIdentityTheProfileDoesNotDescribeIsPackagedAsExplicitlyUnresolved()
    {
        // The profile's only identity source is a dimension the application has to stamp, and this
        // row carries none. Guessing the host instead would attribute an incident to a customer who
        // is not having a problem.
        var reader = new FakeBoardReader([Row("case-a", rank: 3, path: "/checkout")]);
        var (service, packages, _) = Build(
            reader, adapters: new FakeCompanyAdapterProvider(ScoutCompanyProfiles.IdentityOnlyFromAStampedDimension()));

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Equal(ScoutRowClassification.UnresolvedEntityKey, created.EntityKey);

        using var deterministic = JsonDocument.Parse(created.DeterministicJson);
        var classification = deterministic.RootElement.GetProperty("companyClassification");
        Assert.False(classification.GetProperty("entityIsResolved").GetBoolean());
        Assert.Equal(CompanyResolution.Unresolved, classification.GetProperty("entityResolvedBy").GetString());
        Assert.Equal(JsonValueKind.Null, classification.GetProperty("entitySourceKey").ValueKind);
    }

    [Fact]
    public async Task CarriesWhichSourceAnsweredAndHowIntoThePackage()
    {
        // An identity stamped by the application and one found by a later fallback are both "an
        // answer", and downstream has to be able to tell them apart without re-deriving it.
        var reader = new FakeBoardReader([
            Row("case-stamped", rank: 3, path: "/checkout", dimensions: new Dictionary<string, string> { ["TenantId"] = "t-7" }),
            Row("case-fallback", rank: 3, path: "/checkout"),
        ]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync();

        var created = (await packages.GetRecentAsync(10)).ToDictionary(p => p.CaseId);
        Assert.Equal("tenant:t-7", created["case-stamped"].EntityKey);
        Assert.Equal("host:example.com", created["case-fallback"].EntityKey);

        using var stamped = JsonDocument.Parse(created["case-stamped"].DeterministicJson);
        var classification = stamped.RootElement.GetProperty("companyClassification");
        Assert.Equal("TenantId", classification.GetProperty("entitySourceKey").GetString());
        Assert.Equal(CompanyResolution.ExactMatch, classification.GetProperty("entityResolvedBy").GetString());
        Assert.Equal(CompanyResolution.PatternMatch, classification.GetProperty("stepResolvedBy").GetString());

        using var fallback = JsonDocument.Parse(created["case-fallback"].DeterministicJson);
        Assert.Equal(
            EntityIdentitySourceKind.Host,
            fallback.RootElement.GetProperty("companyClassification").GetProperty("entitySourceKey").GetString());
    }

    [Fact]
    public async Task RecordsWhichProfileClassifiedEveryPackage()
    {
        // When a mapping turns out to be wrong, the first question is which profile produced it.
        var reader = new FakeBoardReader([Row("case-a", rank: 3, path: "/checkout")]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        using var deterministic = JsonDocument.Parse(created.DeterministicJson);
        Assert.Equal(
            "two-services",
            deterministic.RootElement.GetProperty("companyClassification").GetProperty("profileKey").GetString());
    }

    [Fact]
    public async Task RunsUnderTheProfileItsOwnBoardIsConfiguredWith()
    {
        var adapters = new FakeCompanyAdapterProvider(ScoutCompanyProfiles.TwoServicesOnOneHost());
        var (service, _, _) = Build(
            new FakeBoardReader([Row("case-a", rank: 3)]), o => o.CompanyProfileKey = " chosen ", adapters: adapters);

        await service.RunAsync();

        Assert.Equal("chosen", adapters.LastRequestedProfileKey);
    }

    [Fact]
    public async Task FailsTheCycleWhenTheProfileCannotBeResolvedRatherThanLookingQuiet()
    {
        // A board nothing can classify is indistinguishable from a board with nothing on it, so an
        // unreachable profile has to read as a failure - never as a clear cycle.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, runs) = Build(
            reader,
            adapters: new FakeCompanyAdapterProvider(
                new CompanyProfileResolutionException("No enabled company profile is registered under \"ghost\".")));

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Failed, run.Status);
        Assert.Contains("ghost", run.ErrorMessage);
        // And the board query is never paid for.
        Assert.Equal(0, reader.ReadCount);
        Assert.Empty(await packages.GetRecentAsync(10));
        Assert.Equal(ScoutRunStatus.Failed, (await runs.GetRecentAsync(10))[0].Status);
    }

    [Fact]
    public async Task RecordsTheBoardQueriesOnTheRunSoTheDetectionIsReproducible()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, _, _) = Build(reader);

        var run = await service.RunAsync();

        using var queries = JsonDocument.Parse(run.BoardQueriesJson!);
        Assert.Equal("// kql for latency", queries.RootElement.GetProperty(ScoutBoard.Latency).GetString());
        // A cycle covers one board, so the other scout's query is not part of this run.
        Assert.False(queries.RootElement.TryGetProperty(ScoutBoard.Exception, out _));
    }

    [Fact]
    public async Task RecordsTheBoardQueriesEvenOnAClearCycle()
    {
        // A quiet cycle is only meaningful if you can see what it actually asked.
        var reader = new FakeBoardReader([Row("case-a", rank: 1)]);
        var (service, _, _) = Build(reader);

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Clear, run.Status);
        Assert.NotNull(run.BoardQueriesJson);
    }

    [Fact]
    public async Task AttachesTheRawEvidenceAndItsQueryToThePackage()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, _) = Build(reader);

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Equal("// drill-down kql", created.DrillDownKql);
        using var raw = JsonDocument.Parse(created.RawEvidenceJson!);
        Assert.Equal(2, raw.RootElement.GetProperty("rowCount").GetInt32());
        Assert.False(raw.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task StillShipsThePackageWhenTheRawEvidenceCannotBeCollected()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var collector = new FakeEvidenceCollector(ScoutRawEvidence.Skipped("Scout:IncludeRawEvidence is false"));
        var (service, packages, _) = Build(reader, collector: collector);

        var run = await service.RunAsync();

        Assert.Equal(1, run.PackagesCreated);
        var created = Assert.Single(await packages.GetRecentAsync(10));
        Assert.Null(created.DrillDownKql);
        using var raw = JsonDocument.Parse(created.RawEvidenceJson!);
        Assert.Equal("Scout:IncludeRawEvidence is false", raw.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PersistsThePromptAndRawResponseBehindEveryPackage()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var logs = new InMemoryScoutLlmCallLogRepository();
        var (service, packages, _) = Build(reader, llmCallLogs: logs);

        await service.RunAsync();

        var created = Assert.Single(await packages.GetRecentAsync(10));
        var call = await logs.GetByPackageIdAsync(created.Id);
        Assert.NotNull(call);
        Assert.Equal("prompt for case-a", call!.PromptText);
        Assert.Contains("symptom", call.RawResponseText);
        Assert.Equal(created.Id, call.InvestigationPackageId);
    }

    [Fact]
    public async Task StillShipsThePackageWhenTheCallLogCannotBeWritten()
    {
        // Losing the audit record must never cost the package it documents.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, _) = Build(reader, llmCallLogs: new ThrowingLlmCallLogRepository());

        var run = await service.RunAsync();

        Assert.Equal(1, run.PackagesCreated);
        Assert.Single(await packages.GetRecentAsync(10));
    }

    [Fact]
    public async Task DoesNotPayForADrillDownQueryOnAVetoedCandidate()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var assessor = new FakeAssessor(_ => Assessed(shouldEscalate: false, why: "known crawler"));
        var collector = new FakeEvidenceCollector();
        var (service, _, _) = Build(reader, o => o.AllowScoutVeto = true, assessor, collector: collector);

        await service.RunAsync();

        Assert.Equal(0, collector.Calls);
    }

    [Fact]
    public async Task CountsEveryClubItLookedAtNotJustTheOnesItEscalated()
    {
        // The clubs figure is the one that answers "how wide is the watch?". Counting only the
        // escalated rows would make a quiet cycle look like the agent had stopped looking.
        var reader = new FakeBoardReader([
            Row("case-a", rank: 3, entity: "host:a.example.com"),
            Row("case-b", rank: 1, entity: "host:b.example.com"),
            Row("case-c", rank: 1, entity: "host:a.example.com"),
        ]);
        var (service, _, _) = Build(reader);

        var run = await service.RunAsync();

        Assert.Equal(2, run.ClubsWatched);
    }

    [Fact]
    public async Task PublishesEveryPhaseOfTheCycleInOrder()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var runs = new RecordingScoutRunRepository();
        var (service, _, _) = Build(reader, runs: runs);

        await service.RunAsync();

        Assert.Equal(
            [ScoutRunPhase.LatencyBoard, ScoutRunPhase.Triage, ScoutRunPhase.Analysis, ScoutRunPhase.Evidence],
            runs.Phases);
    }

    [Fact]
    public async Task TheExceptionScoutReportsItsOwnBoardPhase()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var runs = new RecordingScoutRunRepository();
        var (service, _, _) = Build(reader, runs: runs);

        await service.RunAsync(ScoutBoard.Exception);

        Assert.Equal(
            [ScoutRunPhase.ExceptionBoard, ScoutRunPhase.Triage, ScoutRunPhase.Analysis, ScoutRunPhase.Evidence],
            runs.Phases);
    }

    [Theory]
    [InlineData(ScoutBoard.Latency)]
    [InlineData(ScoutBoard.Exception)]
    public void EveryPhaseHasATypicalWeightForTheProgressBar(string board)
    {
        // The bar sizes each step by its weight; a phase without one would render at zero width.
        var weights = ScoutRunPhase.WeightsFor(board);
        Assert.All(ScoutRunPhase.PhasesFor(board), p => Assert.True(weights.ContainsKey(p), $"'{p}' has no weight."));
        Assert.Equal(ScoutRunPhase.PhasesFor(board).Length, weights.Count);
        Assert.All(weights.Values, w => Assert.True(w > 0));
    }

    [Fact]
    public async Task ClearsThePhaseOnceTheCycleIsOver()
    {
        // A phase left behind would make an idle agent look permanently busy.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, _, _) = Build(reader);

        var run = await service.RunAsync();

        Assert.Null(run.Phase);
    }

    [Fact]
    public async Task StillCompletesTheCycleWhenThePhaseCannotBeReported()
    {
        // Progress reporting is cosmetic - it must never cost a package.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var runs = new RecordingScoutRunRepository(throwOnPhase: true);
        var (service, packages, _) = Build(reader, runs: runs);

        var run = await service.RunAsync();

        Assert.Equal(ScoutRunStatus.Detected, run.Status);
        Assert.Single(await packages.GetRecentAsync(10));
    }

    [Fact]
    public async Task WalksEveryPhaseEvenWhenThereIsNothingNewToAnalyse()
    {
        // The common case: every candidate was already packaged by an earlier cycle. The loop then
        // does no work, and the progress bar used to stall on triage as if the agent had hung.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var runs = new RecordingScoutRunRepository();
        var packages = new InMemoryInvestigationPackageRepository();
        var (service, _, _) = Build(reader, runs: runs, packages: packages);

        await service.RunAsync();
        runs.Phases.Clear();

        var second = await service.RunAsync();

        Assert.Equal(0, second.PackagesCreated);
        Assert.True(second.PackagesSkippedDuplicate > 0);
        Assert.Equal(ScoutRunPhase.PhasesFor(ScoutBoard.Latency), runs.Phases);
    }

    [Fact]
    public async Task NeverReportsAPhaseThatGoesBackwards()
    {
        // Analysis and evidence are reported per candidate; a bar that jumps back reads as a bug.
        var reader = new FakeBoardReader([Row("case-a", rank: 3), Row("case-b", rank: 3)]);
        var runs = new RecordingScoutRunRepository();
        var (service, _, _) = Build(reader, runs: runs);

        await service.RunAsync();

        var indexes = runs.Phases.Select(p => Array.IndexOf(ScoutRunPhase.PhasesFor(ScoutBoard.Latency), p)).ToList();
        Assert.Equal(indexes.OrderBy(i => i), indexes);
        Assert.Equal(indexes.Distinct(), indexes);
    }

    [Fact]
    public async Task RecordsWhichCandidatesWereSkippedAsAlreadyKnownNotJustHowMany()
    {
        // "0 handed to L3" is only meaningful if the reader can see what the cycle recognised.
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var packages = new InMemoryInvestigationPackageRepository();
        var (service, _, _) = Build(reader, packages: packages);

        await service.RunAsync();
        var second = await service.RunAsync();

        Assert.Equal(0, second.PackagesCreated);
        Assert.NotNull(second.SkippedCaseIdsJson);
        Assert.Contains("case-a", second.SkippedCaseIdsJson);
    }

    [Fact]
    public async Task LeavesTheSkippedListNullWhenNothingWasSkipped()
    {
        // Below the escalation rank there are no candidates at all, so nothing can be skipped.
        var reader = new FakeBoardReader([Row("case-a", rank: 1)]);
        var (service, _, _) = Build(reader);

        var run = await service.RunAsync();

        Assert.Equal(0, run.PackagesCreated);
        Assert.Equal(0, run.PackagesSkippedDuplicate);
        Assert.Null(run.SkippedCaseIdsJson);
    }

    [Fact]
    public async Task QueuesEveryPackageItCreatesSoTheL3TeamActuallyReceivesIt()
    {
        // The handoff is the whole point of the detection phase: a package that is persisted but
        // never queued is invisible to L3 and nothing ever investigates it.
        var reader = new FakeBoardReader([Row("case-a", rank: 3), Row("case-b", rank: 3)]);
        var queue = new InMemoryAgentWorkQueue();
        var (service, _, _) = Build(reader, queue: queue);

        var run = await service.RunAsync();

        Assert.Equal(2, run.PackagesCreated);
        var queued = await queue.GetQueuedAsync(AgentWorkPayloadType.InvestigationPackage, 10);
        Assert.Equal(2, queued.Count);
        Assert.All(queued, item => Assert.Equal(MonkeyKey.LatencyScoutMonkey, item.SourceMonkey));
        // The board's own rank drives the queue order, so L3 drains it the way the board triaged.
        Assert.All(queued, item => Assert.Equal(3, item.Priority));
    }

    [Fact]
    public async Task StillCompletesTheCycleWhenTheHandoffQueueIsUnavailable()
    {
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, packages, _) = Build(reader, queue: new ThrowingAgentWorkQueue());

        var run = await service.RunAsync();

        // The package is already persisted by then - losing the queue write must not throw away the
        // cycle's work, it must only be loud about it.
        Assert.Equal(ScoutRunStatus.Detected, run.Status);
        Assert.Equal(1, run.PackagesCreated);
        Assert.Single(await packages.GetRecentAsync(10));
    }

    private sealed class ThrowingAgentWorkQueue : IAgentWorkQueue
    {
        public Task<bool> EnqueueAsync(AgentWorkItem item, CancellationToken ct = default) =>
            throw new InvalidOperationException("queue unavailable");

        public Task<AgentWorkItem?> TryLeaseAsync(string payloadType, string leasedBy, TimeSpan leaseDuration, CancellationToken ct = default) =>
            Task.FromResult<AgentWorkItem?>(null);

        public Task CompleteAsync(Guid itemId, string leasedBy, CancellationToken ct = default) => Task.CompletedTask;

        public Task FailAsync(Guid itemId, string leasedBy, string error, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentWorkItem>> GetQueuedAsync(string payloadType, int top, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentWorkItem>>([]);

        public Task<AgentWorkQueueStats> GetStatsAsync(string payloadType, CancellationToken ct = default) =>
            Task.FromResult(AgentWorkQueueStats.Empty);

        public Task<int> DiscardQueuedAsync(string payloadType, IReadOnlyCollection<Guid>? itemIds, string reason, string discardedBy, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task EachScoutSeesOnlyItsOwnPackagesAndCycles()
    {
        // The two cards read the same tables; without the filter each would report the other's
        // work as its own.
        var reader = new FakeBoardReader([Row("case-a", rank: 3), Row("case-b", rank: 3)]);
        var (service, packages, runs) = Build(reader);

        await service.RunAsync(ScoutBoard.Latency);
        await service.RunAsync(ScoutBoard.Exception);

        Assert.Equal(2, (await packages.GetRecentAsync(50, ScoutBoard.Latency)).Count);
        Assert.Equal(2, (await packages.GetRecentAsync(50, ScoutBoard.Exception)).Count);
        Assert.Equal(4, (await packages.GetRecentAsync(50)).Count);

        Assert.Single(await runs.GetRecentAsync(50, ScoutBoard.Latency));
        Assert.Single(await runs.GetRecentAsync(50, ScoutBoard.Exception));
        Assert.Equal(2, (await runs.GetRecentAsync(50)).Count);

        Assert.Equal(2, (await runs.GetStatsAsync(ScoutBoard.Latency)).PackagesCreated);
        Assert.Equal(4, (await runs.GetStatsAsync()).PackagesCreated);
    }

    [Fact]
    public async Task TheQueueDedupKeyCarriesTheBoardSoBothScoutsCanHandOffTheSameCase()
    {
        // The queue's dedup key is uniquely indexed. Without the board in it, the second scout's
        // handoff would be silently swallowed and its package would never be investigated.
        var queue = new InMemoryAgentWorkQueue();
        var reader = new FakeBoardReader([Row("case-a", rank: 3)]);
        var (service, _, _) = Build(reader, queue: queue);

        await service.RunAsync(ScoutBoard.Latency);
        await service.RunAsync(ScoutBoard.Exception);

        var queued = await queue.GetQueuedAsync(AgentWorkPayloadType.InvestigationPackage, 10);
        Assert.Equal(2, queued.Count);
        Assert.Equal(
            [MonkeyKey.ExceptionScoutMonkey, MonkeyKey.LatencyScoutMonkey],
            queued.Select(i => i.SourceMonkey).OrderBy(k => k));
        Assert.All(queued, i => Assert.EndsWith("case-a", i.DedupKey));
    }

    private static (ScoutMonkeyService Service, IInvestigationPackageRepository Packages, IScoutRunRepository Runs) Build(
        FakeBoardReader reader,
        Action<ScoutOptions>? configure = null,
        IScoutAssessor? assessor = null,
        IClubIdentityResolver? resolver = null,
        IScoutEvidenceCollector? collector = null,
        IScoutLlmCallLogRepository? llmCallLogs = null,
        IScoutRunRepository? runs = null,
        IInvestigationPackageRepository? packages = null,
        IAgentWorkQueue? queue = null,
        ICompanyAdapterProvider? adapters = null)
    {
        var options = new ScoutOptions();
        configure?.Invoke(options);

        packages ??= new InMemoryInvestigationPackageRepository();
        runs ??= new InMemoryScoutRunRepository();
        var service = new ScoutMonkeyService(
            reader,
            adapters ?? new FakeCompanyAdapterProvider(ScoutCompanyProfiles.TwoServicesOnOneHost()),
            resolver ?? new FakeClubResolver(),
            assessor ?? new FakeAssessor(),
            collector ?? new FakeEvidenceCollector(),
            packages,
            queue ?? new InMemoryAgentWorkQueue(),
            runs,
            llmCallLogs ?? new InMemoryScoutLlmCallLogRepository(),
            // The same options for every board: these tests exercise the pipeline, not the
            // per-scout configuration split.
            new StubOptionsMonitor(options),
            NullLogger<ScoutMonkeyService>.Instance);

        return (service, packages, runs);
    }

    private sealed class StubOptionsMonitor(ScoutOptions options) : IOptionsMonitor<ScoutOptions>
    {
        public ScoutOptions CurrentValue => options;

        public ScoutOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<ScoutOptions, string?> listener) => null;
    }

    private static ScoutAssessment Assessed(bool shouldEscalate = true, string? why = null) =>
        new(Available: true, "Checkout is slow", "The /reservations/purchase path is above 30s", 0.6, shouldEscalate, why, "test-model", 100, 50);

    private static ScoutBoardRow Row(
        string caseId, int rank, int requests = 10, int failed = 0, int exceptions = 0, int users = 0,
        string entity = "host:example.com", string path = "/checkout",
        IReadOnlyDictionary<string, string>? dimensions = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            CaseId = caseId,
            GroupKey = $"SOR | Hotels | {entity} | 07 Booking",
            Vertical = "Hotels",
            Entity = entity,
            Stage = "07 Booking",
            Source = "SOR",
            Start = "2026-10-07T15:00:00Z",
            End = "2026-10-07T15:05:00Z",
            Priority = "Investigate now",
            Reason = "Severe latency with multiple failed requests",
            Ops = new[] { "op-1" },
            Users = Array.Empty<string>(),
            Paths = new[] { path },
            SlowPaths = new[] { path },
            Operations = new[] { $"POST {path}" },
            Correlations = new[] { "corr-1" },
        });

        var raw = new Dictionary<string, object>
        {
            ["Priority"] = "Investigate now",
            ["PriorityRank"] = rank,
            ["Requests"] = requests,
            ["FailedRequests"] = failed,
            ["Exceptions"] = exceptions,
            ["UniqueUserIds"] = users,
        };

        // Whatever the board projected is offered to the profile under the board's own column name,
        // which is how a profile-composed board hands its identity dimensions through.
        if (dimensions is not null)
        {
            foreach (var pair in dimensions) raw[pair.Key] = pair.Value;
        }

        var rawRow = JsonSerializer.Serialize(raw);

        return new ScoutBoardRow(
            ScoutBoard.Latency, caseId, $"SOR | Hotels | {entity} | 07 Booking", "[from the board]",
            entity, "[from the board]", "SOR", "Investigate now", rank,
            "Severe latency with multiple failed requests",
            DateTimeOffset.Parse("2026-10-07T15:00:00Z"), DateTimeOffset.Parse("2026-10-07T15:05:00Z"),
            payload, rawRow);
    }

    /// <summary>Records the phase timeline so the tests can assert on the order the UI will show.</summary>
    private sealed class RecordingScoutRunRepository(bool throwOnPhase = false) : IScoutRunRepository
    {
        private readonly InMemoryScoutRunRepository inner = new();

        public List<string> Phases { get; } = [];

        public Task<ScoutRun> AddAsync(ScoutRun run, CancellationToken ct = default) => inner.AddAsync(run, ct);

        public Task<ScoutRun> UpdateAsync(ScoutRun run, CancellationToken ct = default) => inner.UpdateAsync(run, ct);

        public Task UpdatePhaseAsync(Guid runId, string phase, int boardRowsEvaluated, int clubsWatched, CancellationToken ct = default)
        {
            Phases.Add(phase);
            if (throwOnPhase) throw new InvalidOperationException("SQL unavailable");
            return inner.UpdatePhaseAsync(runId, phase, boardRowsEvaluated, clubsWatched, ct);
        }

        public Task<IReadOnlyList<ScoutRun>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default) => inner.GetRecentAsync(top, board, profileKey, ct);

        public Task<ScoutStats> GetStatsAsync(string? board = null, string? profileKey = null, CancellationToken ct = default) => inner.GetStatsAsync(board, profileKey, ct);
    }

    private sealed class FakeBoardReader(IReadOnlyList<ScoutBoardRow> rows, Exception? throwOnRead = null) : IScoutBoardReader
    {
        public int ReadCount { get; private set; }

        public Task<ScoutBoardResult> ReadAsync(ScoutBoardRequest request, CancellationToken ct = default)
        {
            ReadCount++;
            if (throwOnRead is not null) throw throwOnRead;
            return Task.FromResult(new ScoutBoardResult(rows, $"// kql for {request.Board}"));
        }
    }

    private sealed class FakeEvidenceCollector(ScoutRawEvidence? evidence = null) : IScoutEvidenceCollector
    {
        public int Calls { get; private set; }

        public Task<ScoutRawEvidence> CollectAsync(ScoutBoardRow row, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(evidence ?? new ScoutRawEvidence("// drill-down kql", 2, false, """[{"Kind":"request"},{"Kind":"exception"}]""", null));
        }
    }

    private sealed class FakeClubResolver(ClubIdentity? identity = null) : IClubIdentityResolver
    {
        public Task<ClubIdentity> ResolveAsync(string entityKey, CancellationToken ct = default) =>
            Task.FromResult(identity ?? new ClubIdentity(entityKey, ClubEntityKind.Host, null, null, null, null, ClubResolution.Unresolved));
    }

    private sealed class ThrowingLlmCallLogRepository : IScoutLlmCallLogRepository
    {
        public Task<ScoutLlmCall> AddAsync(ScoutLlmCall call, CancellationToken ct = default) =>
            throw new InvalidOperationException("log store unavailable");

        public Task<ScoutLlmCall?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default) =>
            Task.FromResult<ScoutLlmCall?>(null);
    }

    private sealed class FakeAssessor(Func<ScoutBoardRow, ScoutAssessment>? assess = null) : IScoutAssessor
    {
        public int Calls { get; private set; }

        public Task<ScoutAssessmentResult> AssessAsync(ScoutBoardRow row, ClubIdentity club, CancellationToken ct = default)
        {
            Calls++;
            var assessment = assess?.Invoke(row) ?? Assessed();
            var call = new ScoutLlmCall
            {
                PromptText = $"prompt for {row.CaseId}",
                RawResponseText = """{"symptom":"..."}""",
                ModelUsed = assessment.ModelUsed,
                TokensIn = assessment.TokensIn,
                TokensOut = assessment.TokensOut,
                DurationMs = 42,
                ErrorMessage = assessment.Available ? null : assessment.UnavailableReason,
            };
            return Task.FromResult(new ScoutAssessmentResult(assessment, call));
        }
    }
}
