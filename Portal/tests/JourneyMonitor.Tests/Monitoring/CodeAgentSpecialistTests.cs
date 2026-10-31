using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// The CodeAgent returns locations, never source. These tests pin the two things that are easy to
/// erode: that no file content is ever carried, and that "nowhere to look" never becomes
/// "the code is fine".
/// </summary>
public class CodeAgentSpecialistTests
{
    [Fact]
    public async Task FindingLocationsContributesThemWithTheDefinitionCalledOut()
    {
        var search = new FakeCodeSearch(CodeSearchResult.Ok([
            new CodeHit("FrontEnd.Applications", "/Legacy/Library/DataLayer/Rentals/RentalSearch.cs",
                "RentalSearch.cs", IsDefinition: true, ["def", "ref"], 4, RepositoryLooksNotLive: false, "https://x"),
        ]));

        var entry = await GatherAsync(search);

        Assert.Equal(AgentSourceStatus.Contributed, entry.Status);
        Assert.Contains("1 of them a definition", entry.Summary);
        // Nobody must read this as "the file was examined".
        Assert.Contains("no file content was read", entry.Summary);

        var location = ReadLocations(entry)[0];
        Assert.True(location.GetProperty("isDefinition").GetBoolean());
        Assert.Equal("RentalSearch.cs", location.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task NoFileContentIsEverCarried()
    {
        // The transcript is persisted in SQL and served over HTTP. This is the decision on record.
        var search = new FakeCodeSearch(CodeSearchResult.Ok([
            new CodeHit("Repo", "/a/b/C.cs", "C.cs", true, ["def"], 1, false, "https://x"),
        ]));

        var entry = await GatherAsync(search);

        Assert.NotNull(entry.FindingsJson);
        foreach (var forbidden in new[] { "content", "snippet", "codeSnippet", "line" })
        {
            Assert.DoesNotContain($"\"{forbidden}\":", entry.FindingsJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ARepositoryThatLooksArchivedIsReportedRatherThanDropped()
    {
        // Scope is every repository by decision, so a legacy repository stays searchable - but a
        // reader has to be able to tell that the hit may be code that no longer runs. A legacy
        // repository is sometimes exactly where the bug is, so filtering would be worse.
        var search = new FakeCodeSearch(CodeSearchResult.Ok([
            new CodeHit("Legacy.Thing", "/old/X.cs", "X.cs", false, ["ref"], 1, RepositoryLooksNotLive: true, null),
        ]));

        var entry = await GatherAsync(search);

        Assert.Equal(AgentSourceStatus.Contributed, entry.Status);
        Assert.Contains("not live code", entry.Summary);
        Assert.True(ReadLocations(entry)[0].GetProperty("repositoryLooksNotLive").GetBoolean());
    }

    [Fact]
    public async Task FindingNothingIsEmptyAndNotNoSource()
    {
        var entry = await GatherAsync(new FakeCodeSearch(CodeSearchResult.Ok([])));

        Assert.Equal(AgentSourceStatus.Empty, entry.Status);
        Assert.Contains("rather than a missing source", entry.Summary);
    }

    [Fact]
    public async Task EveryTermFailingIsFailedAndSaysItIsNotEvidence()
    {
        var entry = await GatherAsync(new FakeCodeSearch(CodeSearchResult.Failed("HTTP 503")));

        Assert.Equal(AgentSourceStatus.Failed, entry.Status);
        Assert.Contains("not evidence that the code is fine", entry.Summary);
    }

    [Fact]
    public async Task SearchTermsAreTheBarePathSegmentsNotTheWholeRoute()
    {
        // Measured against the real index: "searchrooms" returns the call sites with a "def" match,
        // while "/rentals/searchrooms.json" degrades to wiki pages that merely mention the route.
        var search = new FakeCodeSearch(CodeSearchResult.Ok([]));
        await GatherAsync(search);

        Assert.Contains("confirm", search.Terms);
        Assert.DoesNotContain(search.Terms, t => t.Contains('/'));
        Assert.DoesNotContain(search.Terms, t => t.Contains(".json"));
    }

    [Fact]
    public async Task TheLeafExceptionTypeIsSearchedNotTheFullyQualifiedName()
    {
        var search = new FakeCodeSearch(CodeSearchResult.Ok([]));
        await GatherAsync(search);

        Assert.Contains("TimeoutException", search.Terms);
        Assert.DoesNotContain("System.TimeoutException", search.Terms);
    }

    [Fact]
    public async Task OneTermPerCallBecauseTheIndexCombinesThemWithAnd()
    {
        // "searchrooms TimeoutException" returns 2 results against the real index; either term
        // alone returns the real call sites.
        var search = new FakeCodeSearch(CodeSearchResult.Ok([]));
        await GatherAsync(search);

        Assert.True(search.Calls > 1);
        Assert.All(search.Terms, t => Assert.DoesNotContain(' ', t));
    }

    [Fact]
    public async Task ACaseWithNothingSearchableSaysSoInsteadOfClaimingTheCodeIsFine()
    {
        var search = new FakeCodeSearch(CodeSearchResult.Ok([]));
        var package = InspectorFixtures.Package(deterministicJson: """{"groupPayload":{},"boardRow":{}}""");

        var entry = await Build(search).GatherAsync(InspectorFixtures.Context(package));

        Assert.Equal(0, search.Calls);
        Assert.Equal(AgentSourceStatus.Empty, entry.Status);
        Assert.Contains("says nothing about the code", entry.Summary);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static RealCodeAgentSpecialist Build(ICodeSearch search) => new(
        search,
        new InspectorRecorder(new InMemoryAgentTranscriptRepository(), NullLogger<InspectorRecorder>.Instance),
        NullLogger<RealCodeAgentSpecialist>.Instance);

    private static Task<BlackboardEntry> GatherAsync(ICodeSearch search) =>
        Build(search).GatherAsync(InspectorFixtures.Context(CodeCase()));

    /// <summary>A package carrying a slow path and an exception type, which is what the agent searches for.</summary>
    private static InvestigationPackage CodeCase() => InspectorFixtures.Package(deterministicJson: """
        {"groupPayload":{"SlowPaths":["/booking/confirm?x=1"],"Paths":["/rentals/searchrooms.json"]},
         "boardRow":{"ExceptionTypes":["System.TimeoutException"]}}
        """);

    private static JsonElement ReadLocations(BlackboardEntry entry)
    {
        Assert.NotNull(entry.FindingsJson);
        using var doc = JsonDocument.Parse(entry.FindingsJson);
        return doc.RootElement.GetProperty("locations").Clone();
    }
}

internal sealed class FakeCodeSearch(CodeSearchResult result) : ICodeSearch
{
    public List<string> Terms { get; } = [];

    public int Calls => Terms.Count;

    public bool HasSource => true;

    public string NoSourceReason => "";

    public Task<CodeSearchResult> SearchAsync(string term, CancellationToken ct = default)
    {
        Terms.Add(term);
        return Task.FromResult(result);
    }
}
