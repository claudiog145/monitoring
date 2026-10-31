using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Kql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using JourneyMonitor.Tests.Fakes;

namespace JourneyMonitor.Tests.Monitoring;

public class LogsAgentToolsTests
{
    private static readonly DateTimeOffset WindowFrom = DateTimeOffset.Parse("2026-10-07T15:00:00Z");
    private static readonly DateTimeOffset WindowTo = DateTimeOffset.Parse("2026-10-07T15:05:00Z");

    // -----------------------------------------------------------------------------------------
    // The tool contract itself
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void EverySchemaIsValidJsonAndEveryToolBelongsToTheLogsAgent()
    {
        // The schema is passed to the model verbatim; a malformed one fails at the provider with
        // an error that says nothing about which tool produced it.
        foreach (var tool in AllTools(new CountingTelemetrySource([])))
        {
            using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);

            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.True(schema.RootElement.TryGetProperty("properties", out _), tool.Name);
            Assert.Equal(InspectorAgent.Logs, tool.OwnerAgent);
            Assert.NotEqual("", tool.Description);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Guardrail: bounded numeric arguments
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(9999, 7)]
    [InlineData(3, 3)]
    [InlineData(7, 7)]
    public void ClampsLookbackDaysToTheConfiguredCeiling(int requested, int expectedDays)
    {
        // 168h (the default ceiling) is 7 days. A clamp that silently stops working lets a model
        // scan months of three production resources, and that only shows up on the bill.
        var kql = ClubBaselineTool.BuildKql(
            new KqlQueryTextProvider().GetInspectorLogsKql(InspectorLogsQuery.ClubBaseline),
            Package(),
            requested,
            maxLookbackDays: 7,
            bucketMinutes: 5);

        Assert.Contains($"tEnd - ({expectedDays} * 1d)", kql);
        Assert.Contains($"LookbackDays={expectedDays}", kql);
        AssertOnlySourcePlaceholdersRemain(kql);
    }

    /// <summary>
    /// {{SOURCE:alias}} survives this stage on purpose - KqlSourceResolver fills it in once the
    /// environment is known. Every other placeholder must already be gone.
    /// </summary>
    private static void AssertOnlySourcePlaceholdersRemain(string kql)
    {
        var remaining = System.Text.RegularExpressions.Regex.Matches(kql, @"\{\{[^}]*\}\}")
            .Select(m => m.Value)
            .Where(p => !p.StartsWith("{{SOURCE:", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(remaining);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(100000, 200)]
    [InlineData(25, 25)]
    public void ClampsTheRowCapOfTheBreakdowns(int requested, int expectedRows)
    {
        var kql = SignatureBreakdownTool.BuildKql(
            new KqlQueryTextProvider().GetInspectorLogsKql(InspectorLogsQuery.SignatureBreakdown),
            Package(),
            requested,
            maxRows: 200,
            label: "test");

        Assert.Contains($"let maxRows = {expectedRows};", kql);
    }

    [Fact]
    public void FallsBackToTheDefaultWhenTheModelSendsRubbishInsteadOfANumber()
    {
        // A malformed argument must cost a default, not an exception the agent has to recover from.
        Assert.Equal(7, InspectorKqlGuards.ReadIntArgument("not json at all", "lookbackDays", 7));
        Assert.Equal(7, InspectorKqlGuards.ReadIntArgument("""{"lookbackDays":"many"}""", "lookbackDays", 7));
        Assert.Equal(7, InspectorKqlGuards.ReadIntArgument("""{"other":3}""", "lookbackDays", 7));
        Assert.Equal(7, InspectorKqlGuards.ReadIntArgument(null, "lookbackDays", 7));
        Assert.Equal(3, InspectorKqlGuards.ReadIntArgument("""{"lookbackDays":"3"}""", "lookbackDays", 7));
    }

    // -----------------------------------------------------------------------------------------
    // Guardrail: nothing a caller controls reaches the query as text
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AnEntityKeyCarryingAQuoteCanNeitherBreakNorExtendTheQuery()
    {
        const string hostile = "host:evil.com' | union withsource=X *; //";

        var kql = ClubBaselineTool.BuildKql(
            new KqlQueryTextProvider().GetInspectorLogsKql(InspectorLogsQuery.ClubBaseline),
            Package(entityKey: hostile),
            lookbackDays: 7,
            maxLookbackDays: 7,
            bucketMinutes: 5);

        // Base64 is the actual boundary: the payload cannot contain a quote, so it cannot close
        // the literal no matter what was in the entity key.
        var encoded = Between(kql, "base64_decode_tostring('", "')");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        Assert.DoesNotContain("'", decoded);
        Assert.DoesNotContain(";", decoded);
        Assert.Equal("host:evil.com | union withsource=X * //", decoded);
        // And the hostile text never appears as query text at all.
        Assert.DoesNotContain("union withsource=X *", kql);
        Assert.DoesNotContain("evil.com", kql);
    }

    [Theory]
    [InlineData("host:stays.tmobiletravel.com", "host:stays.tmobiletravel.com")]
    [InlineData("club:140", "club:140")]
    [InlineData("club:140\r\n| take 1", "club:140| take 1")]
    [InlineData("  host:a.com  ", "host:a.com")]
    public void SanitizesWithoutManglingARealEntityKey(string input, string expected) =>
        Assert.Equal(expected, InspectorKqlGuards.SanitizeEntityKey(input));

    [Fact]
    public async Task RefusesToRunWhenTheScoutCouldNotAttributeTheTrafficToAnyone()
    {
        // A query scoped to "[host/club unavailable]" returns nothing, and an empty baseline reads
        // as "this club never has exceptions" - the opposite of true.
        var source = new CountingTelemetrySource([]);
        var tool = new ClubBaselineTool(
            source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<ClubBaselineTool>.Instance);

        var result = await tool.ExecuteAsync("{}", Context(Package(entityKey: InspectorKqlGuards.UnresolvedEntity)));

        Assert.False(result.Success);
        Assert.Contains("no club or host", result.Error);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public void RejectsATemplateThatStillHasAnUnsubstitutedPlaceholder()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => InspectorKqlGuards.RequireNoPlaceholders("let x = {{SOMETHING_ELSE}};", "test"));

        Assert.Contains("{{SOMETHING_ELSE}}", ex.Message);
    }

    // -----------------------------------------------------------------------------------------
    // Guardrail: a partial result never looks complete
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task MarksAResultThatDidNotFitTheCharacterBudget()
    {
        var package = Package(rawEvidenceRows: 60);
        var tool = new DrillDownEventsTool(
            new CountingTelemetrySource([]), new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(),
            Inspector(maxToolResultChars: 1200), NullLogger<DrillDownEventsTool>.Instance);

        var result = await tool.ExecuteAsync("{}", Context(package));

        Assert.True(result.Success);
        Assert.True(result.Truncated);
        Assert.True(result.RowCount < 60);
        // The warning travels inside the payload, because that is what the model reads.
        using var payload = JsonDocument.Parse(result.ResultJson);
        Assert.True(payload.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(60, payload.RootElement.GetProperty("totalRows").GetInt32());
        Assert.Contains("partial list", payload.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ReportsTheFullSetAsCompleteWhenItFits()
    {
        var tool = new DrillDownEventsTool(
            new CountingTelemetrySource([]), new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(),
            Inspector(), NullLogger<DrillDownEventsTool>.Instance);

        var result = await tool.ExecuteAsync("{}", Context(Package(rawEvidenceRows: 3)));

        Assert.False(result.Truncated);
        Assert.Equal(3, result.RowCount);
    }

    [Fact]
    public async Task CarriesForwardACapTheScoutAlreadyApplied()
    {
        // The Scout caps its own collection (MaxRawEvidenceRows). 200 of 200 rows is still a
        // partial list when the window held 2352 events - reporting it as complete would let the
        // agent conclude "that is everything that happened" from a sample.
        var tool = new DrillDownEventsTool(
            new CountingTelemetrySource([]), new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(),
            Inspector(), NullLogger<DrillDownEventsTool>.Instance);

        var result = await tool.ExecuteAsync("{}", Context(Package(rawEvidenceRows: 3, collectionTruncated: true)));

        Assert.True(result.Truncated);
        Assert.Equal(3, result.RowCount);
        using var payload = JsonDocument.Parse(result.ResultJson);
        Assert.Contains("itself capped", payload.RootElement.GetProperty("note").GetString());
    }

    // -----------------------------------------------------------------------------------------
    // Guardrail: a failing source degrades, it does not throw
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ATelemetrySourceThatThrowsComesBackAsAFailedResult()
    {
        foreach (var tool in AllTools(new ThrowingTelemetrySource()))
        {
            // drill_down_events only queries when the package carries no evidence - that is the
            // path under test here, so the package is deliberately evidence-free.
            var result = await tool.ExecuteAsync("{}", Context(Package(rawEvidenceRows: 0)));

            Assert.False(result.Success, tool.Name);
            Assert.Contains("Application Insights is unreachable", result.Error);
            Assert.Equal(0, result.RowCount);
        }
    }

    // -----------------------------------------------------------------------------------------
    // drill_down_events: the evidence already travels in the package
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task DrillDownReturnsThePackagedEvidenceWithoutQueryingAnything()
    {
        // Re-running the drill-down costs ~4s and one production query to fetch bytes the agent is
        // already holding - and, running later, it can return a different set than the one the
        // rest of the case was built on.
        var source = new CountingTelemetrySource([]);
        var tool = new DrillDownEventsTool(
            source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<DrillDownEventsTool>.Instance);

        var result = await tool.ExecuteAsync("{}", Context(Package(rawEvidenceRows: 5)));

        Assert.Equal(0, source.Calls);
        Assert.True(result.Success);
        Assert.Equal(5, result.RowCount);
    }

    [Fact]
    public async Task DrillDownQueriesOnlyWhenThePackageCarriesNoEvidence()
    {
        var source = new CountingTelemetrySource([Row("""{"Kind":"request","OperationId":"op-1"}""")]);
        var tool = new DrillDownEventsTool(
            source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<DrillDownEventsTool>.Instance);

        var result = await tool.ExecuteAsync("{}", Context(Package(rawEvidenceRows: 0)));

        Assert.Equal(1, source.Calls);
        Assert.True(result.Success);
        Assert.Equal(1, result.RowCount);
        // Scoped to the package's own bucket and operation ids, never to the whole board window.
        Assert.Contains("datetime(2026-10-07T15:00:00.000Z)", source.LastKql);
        Assert.Contains("[\"op-1\",\"op-2\"]", source.LastKql);
    }

    [Fact]
    public async Task DrillDownHonoursTheLimitArgumentWithoutExceedingTheRowCap()
    {
        var tool = new DrillDownEventsTool(
            new CountingTelemetrySource([]), new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(),
            Inspector(maxToolRows: 4), NullLogger<DrillDownEventsTool>.Instance);

        var limited = await tool.ExecuteAsync("""{"limit":2}""", Context(Package(rawEvidenceRows: 10)));
        var overCap = await tool.ExecuteAsync("""{"limit":9999}""", Context(Package(rawEvidenceRows: 10)));

        Assert.Equal(2, limited.RowCount);
        Assert.True(limited.Truncated);
        Assert.Equal(4, overCap.RowCount);
    }

    // -----------------------------------------------------------------------------------------
    // The embedded KQL text
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(InspectorLogsQuery.ClubBaseline)]
    [InlineData(InspectorLogsQuery.SignatureBreakdown)]
    [InlineData(InspectorLogsQuery.ClientBreakdown)]
    public void EveryLogsAgentQueryIsCrossResourceOverTheSameThreeProductionResources(string query)
    {
        var kql = new KqlQueryTextProvider().GetInspectorLogsKql(query);

        // Aliases, not ARM paths: the resource ids live in monitoring.TelemetrySource so pointing
        // the platform at another subscription is a row change, not an edit of eight query files.
        Assert.Contains("{{SOURCE:sor-frontend}}", kql);
        Assert.Contains("{{SOURCE:cruise-core}}", kql);
        Assert.Contains("{{SOURCE:cruise-api}}", kql);
        Assert.DoesNotContain("/subscriptions/", kql);
        // The entity is never interpolated as plain text - that is the whole anti-injection design.
        Assert.Contains("base64_decode_tostring('{{ENTITY_KEY_B64}}')", kql);
    }

    [Fact]
    public void RejectsAnUnknownLogsAgentQueryInsteadOfReturningSomethingElse()
    {
        var ex = Assert.Throws<ArgumentException>(() => new KqlQueryTextProvider().GetInspectorLogsKql("whatever"));

        Assert.Contains("club-baseline", ex.Message);
    }

    [Fact]
    public void TheBaselineCarriesWhereThisCaseFallsInTheClubsOwnDistribution()
    {
        // Five summary numbers leave the comparison to the model. The percentile is the finding.
        var kql = ClubBaselineTool.BuildKql(
            new KqlQueryTextProvider().GetInspectorLogsKql(InspectorLogsQuery.ClubBaseline),
            Package(exceptionCount: 1683),
            lookbackDays: 7,
            maxLookbackDays: 7,
            bucketMinutes: 5);

        Assert.Contains("let currentExceptions = todouble(1683);", kql);
        Assert.Contains("CurrentPercentileOfBaseline", kql);
        Assert.Contains("P95Exceptions", kql);
        Assert.Contains("MedianExceptions", kql);
        // Buckets observed vs possible: a median over 12 busy buckets is not the same claim as a
        // median over 2016.
        Assert.Contains("BucketsPossible", kql);
    }

    // -----------------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------------

    private static IAgentTool[] AllTools(ITelemetrySource source) =>
    [
        new DrillDownEventsTool(source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<DrillDownEventsTool>.Instance),
        new ClubBaselineTool(source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<ClubBaselineTool>.Instance),
        new SignatureBreakdownTool(source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<SignatureBreakdownTool>.Instance),
        new ClientBreakdownTool(source, new KqlQueryTextProvider(), new FakeKqlSourceResolver(), Insights(), Scout(), Inspector(), NullLogger<ClientBreakdownTool>.Instance),
    ];

    private static IOptions<InsightsOptions> Insights() => Options.Create(new InsightsOptions { AppInsightsAppId = "app-id" });

    private static IOptions<ScoutOptions> Scout() => Options.Create(new ScoutOptions());

    private static IOptions<InspectorOptions> Inspector(int? maxToolRows = null, int? maxToolResultChars = null) =>
        Options.Create(new InspectorOptions { MaxToolRows = maxToolRows, MaxToolResultChars = maxToolResultChars });

    private static InspectorContext Context(InvestigationPackage package) =>
        new(
            new ForensicCase
            {
                InvestigationPackageId = package.Id,
                CaseId = package.CaseId,
                EntityKey = package.EntityKey,
                Vertical = package.Vertical,
                Stage = package.Stage,
            },
            package,
            new ClubIdentity(package.EntityKey, package.EntityKind, package.ClubId, null, package.BrandName, package.ClubName, package.ClubResolvedBy),
            []);

    private static InvestigationPackage Package(
        string entityKey = "host:stays.tmobiletravel.com",
        int rawEvidenceRows = 2,
        int exceptionCount = 1683,
        bool collectionTruncated = false)
    {
        var rows = Enumerable.Range(1, rawEvidenceRows).Select(i => new
        {
            Kind = "exception",
            Timestamp = WindowFrom.AddSeconds(i),
            Source = "SOR",
            OperationId = $"op-{i}",
            ExceptionType = "Microsoft.AspNetCore.Http.BadHttpRequestException",
            Message = $"Script error line {i} of a masked browser stack",
        });

        // Matches ScoutRawEvidence as the Scout persists it: the rows travel as a JSON string
        // inside the envelope, not as a nested array.
        var evidence = JsonSerializer.Serialize(
            new { kql = "// drill-down", rowCount = rawEvidenceRows, truncated = collectionTruncated, rowsJson = JsonSerializer.Serialize(rows), error = (string?)null });

        return new InvestigationPackage
        {
            CaseId = "SOR | Hotels | host:stays.tmobiletravel.com | 07 Booking | 2026-10-07T15:00:00Z",
            ProfileKey = "arrivia",
            Board = ScoutBoard.Exception,
            Vertical = "Hotels",
            Stage = "07 Booking",
            Source = "SOR",
            EntityKey = entityKey,
            EntityKind = ClubEntityKind.Host,
            ClubResolvedBy = ClubResolution.Unresolved,
            Priority = "Investigate now",
            PriorityRank = 3,
            Reason = "At least 100 exceptions and >=3x preceding interval",
            WindowFrom = WindowFrom,
            WindowTo = WindowTo,
            DeterministicJson = """{"caseId":"c","groupPayload":{"Ops":["op-1","op-2"]}}""",
            AssessmentJson = "{}",
            RawEvidenceJson = rawEvidenceRows > 0 ? evidence : null,
            ExceptionCount = exceptionCount,
            ScoutRunId = Guid.NewGuid(),
        };
    }

    private static TelemetryJourneyRow Row(string json) => JsonSerializer.Deserialize<TelemetryJourneyRow>(json)!;

    private static string Between(string value, string start, string end)
    {
        var from = value.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var to = value.IndexOf(end, from, StringComparison.Ordinal);
        return value[from..to];
    }

    private sealed class CountingTelemetrySource(IReadOnlyList<TelemetryJourneyRow> rows) : ITelemetrySource
    {
        public int Calls { get; private set; }

        public string LastKql { get; private set; } = "";

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastKql = request.Kql;
            return Task.FromResult(rows);
        }
    }

    private sealed class ThrowingTelemetrySource : ITelemetrySource
    {
        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Application Insights is unreachable");
    }
}
