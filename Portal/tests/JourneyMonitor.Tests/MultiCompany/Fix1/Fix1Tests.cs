using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.MultiCompany.Fix1;

/// <summary>
/// FXM1 - MCRV H1 (the Doctor's knowledge belongs to the finding's company; untagged finding history is
/// the incumbent's, not shared), M1 (every company-dependent Web proxy read forwards the query string)
/// and L2 (the in-memory retriever implements the per-company reads).
/// </summary>
public class Fix1Tests
{
    private const string Incumbent = KpiProfiles.Default;
    private const string Banco = "banco";
    private const string Kinectify = "kinectify";

    private const string DoctorResponse = """
        {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
        """;

    // ------------------------------------------------------------------------------------------------
    // H1 - Doctor
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Banco)]
    [InlineData(Kinectify)]
    [InlineData(Incumbent)]
    public async Task Doctor_FindingStoredUnderItsOwnCompany(string activeCompany)
    {
        var retriever = new FakeKnowledgeRetriever([]);
        var doctor = Doctor(retriever, new FixedActiveCompany(activeCompany), Store(Incumbent, Banco, Kinectify));

        await doctor.DiagnoseAsync(HotelFinding());

        // The finding is the incumbent's (legacy hotels pipeline): its precedents come from, and its
        // diagnosis is filed under, the incumbent - whichever company the portal has active.
        Assert.Equal(Incumbent, Assert.Single(retriever.RetrievedProfileKeys));
        Assert.Equal(Incumbent, Assert.Single(retriever.IndexedProfileKeys));
    }

    [Fact]
    public async Task Doctor_FindingStoredUnderItsOwnCompany_EndToEndOverTheInMemoryRetriever()
    {
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: null));
        var doctor = Doctor(retriever, new FixedActiveCompany(Banco), Store(Incumbent, Banco));

        await doctor.DiagnoseAsync(HotelFinding());

        Assert.Empty(await retriever.RetrieveAsync("hotels Booking", 10, default, Banco));
        Assert.Empty(await retriever.GetIndexedDocumentsForProfileAsync(Banco));
        var hit = Assert.Single(await retriever.RetrieveAsync("hotels Booking", 10, default, Incumbent));
        Assert.StartsWith("[hotels/07 Booking]", hit.Content, StringComparison.Ordinal);
        Assert.Equal(KnowledgeSourceType.FindingHistory, hit.SourceType);
    }

    [Fact]
    public async Task Doctor_NoActiveCompanyAndNoRegistry_StaysWithTheIncumbent()
    {
        var retriever = new FakeKnowledgeRetriever([]);
        await Doctor(retriever, activeCompany: null, profiles: null).DiagnoseAsync(HotelFinding());

        Assert.Equal(Incumbent, Assert.Single(retriever.RetrievedProfileKeys));
        Assert.Equal(Incumbent, Assert.Single(retriever.IndexedProfileKeys));
    }

    [Fact]
    public void FindingOwnership_IsTheIncumbentWheneverTheRegistryKnowsIt()
    {
        Assert.Equal(Incumbent, FindingOwnership.OwnerOf(HotelFinding(), incumbentRegistered: true, Banco));
        Assert.Equal(Incumbent, FindingOwnership.OwnerOf(HotelFinding(), incumbentRegistered: true, null));
        Assert.Equal(Incumbent, FindingOwnership.OwnerOf(HotelFinding(), incumbentRegistered: false, null));
        // A registry without the incumbent has no legacy pipeline owner: the finding stays with the active company.
        Assert.Equal("acme", FindingOwnership.OwnerOf(HotelFinding(), incumbentRegistered: false, " acme "));
    }

    // ------------------------------------------------------------------------------------------------
    // H1 - legacy untagged knowledge + L2
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Knowledge_LegacyUntaggedIsIncumbentNotShared()
    {
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: null));
        // Legacy Doctor diagnosis stored before chunks carried a company.
        await retriever.IndexAsync(Chunk(KnowledgeSourceType.FindingHistory, null, "[hotels/07 Booking] timeout budget burning"), default, "");
        // Deliberately shared runbook and each company's own RCA.
        await retriever.IndexAsync(Chunk(KnowledgeSourceType.RcaDocument, "Shared SRE handbook", "timeout handling guidance"), default, "");
        await retriever.IndexAsync(Chunk(KnowledgeSourceType.RcaDocument, "Banco card RCA", "card authorization timeout"), default, Banco);
        await retriever.IndexAsync(Chunk(KnowledgeSourceType.RcaDocument, "Arrivia hotel RCA", "hotel booking timeout"), default, Incumbent);

        // Retrieval: banco never sees the untagged tourism finding; the incumbent does.
        var bancoHits = await retriever.RetrieveAsync("timeout", 10, default, Banco);
        Assert.Equal(["card authorization timeout", "timeout handling guidance"], bancoHits.Select(h => h.Content).Order().ToArray());
        var incumbentHits = await retriever.RetrieveAsync("timeout", 10, default, Incumbent);
        Assert.Contains(incumbentHits, h => h.Content.StartsWith("[hotels/", StringComparison.Ordinal));
        Assert.DoesNotContain(incumbentHits, h => h.Content.StartsWith("card", StringComparison.Ordinal));

        // Memory Bank (L2): the per-company inventory and passages apply the same boundary.
        var bancoDocs = await retriever.GetIndexedDocumentsForProfileAsync(Banco);
        Assert.Equal(["Banco card RCA", "Shared SRE handbook"], bancoDocs.Select(d => d.DocumentTitle).Order().ToArray());
        Assert.DoesNotContain(bancoDocs, d => d.SourceType == KnowledgeSourceType.FindingHistory);
        var incumbentDocs = await retriever.GetIndexedDocumentsForProfileAsync(Incumbent);
        Assert.Equal(["(untitled)", "Arrivia hotel RCA", "Shared SRE handbook"], incumbentDocs.Select(d => d.DocumentTitle).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(4, (await retriever.GetIndexedDocumentsForProfileAsync(null)).Count);

        Assert.Empty(await retriever.GetDocumentSectionsForProfileAsync("(untitled)", KnowledgeSourceType.FindingHistory, Banco));
        Assert.Single(await retriever.GetDocumentSectionsForProfileAsync("(untitled)", KnowledgeSourceType.FindingHistory, Incumbent));
        Assert.Empty(await retriever.GetDocumentSectionsForProfileAsync("Arrivia hotel RCA", null, Banco));
        Assert.Single(await retriever.GetDocumentSectionsForProfileAsync("Shared SRE handbook", null, Banco));
        Assert.Single(await retriever.GetDocumentSectionsAsync("Banco card RCA", null));

        // The rule itself.
        Assert.Equal(Incumbent, KnowledgeScope.OwnerOf(KnowledgeSourceType.FindingHistory, ""));
        Assert.Equal(Incumbent, KnowledgeScope.OwnerOf(KnowledgeSourceType.FindingHistory, null));
        Assert.Equal("", KnowledgeScope.OwnerOf(KnowledgeSourceType.RcaDocument, ""));
        Assert.Equal(Banco, KnowledgeScope.OwnerOf(KnowledgeSourceType.FindingHistory, " banco "));
        Assert.False(KnowledgeScope.IsVisibleTo(KnowledgeSourceType.FindingHistory, "", Banco));
        Assert.True(KnowledgeScope.IsVisibleTo(KnowledgeSourceType.FindingHistory, "", "ARRIVIA"));
        Assert.True(KnowledgeScope.IsVisibleTo(KnowledgeSourceType.FindingHistory, "", null));
        Assert.True(KnowledgeScope.IsVisibleTo(KnowledgeSourceType.RcaDocument, "", Banco));
        Assert.False(KnowledgeScope.IsVisibleTo(KnowledgeSourceType.RcaDocument, Incumbent, Banco));
    }

    [Fact]
    public void Knowledge_LegacyUntaggedIsIncumbentNotShared_InTheSqlProcedures()
    {
        var portal = FindPortalDirectory();
        var sql = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringFindings.sql"));
        const string rule = @"\(@ProfileKey IS NULL OR ProfileKey = @ProfileKey\s+OR \(ProfileKey = N'' AND \(SourceType <> N'finding_history' OR @ProfileKey = @IncumbentProfileKey\)\)\)";

        foreach (var proc in new[] { "usp_GetAllKnowledgeChunks", "usp_GetKnowledgeDocuments", "usp_GetKnowledgeChunksByDocument" })
        {
            var body = ProcedureBody(sql, proc);
            Assert.Matches(rule, body);
            Assert.Contains("@IncumbentProfileKey nvarchar(128) = N'arrivia'", body);
            Assert.DoesNotMatch(@"OR ProfileKey = N''\)", body);
        }

        var retriever = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql", "SqlKnowledgeRetriever.cs"));
        Assert.Equal(3, Regex.Matches(retriever, @"IncumbentProfileKey\W+KpiProfiles\.Default").Count);

        // The data fix (report §4.2) ships in the registered, re-run-on-every-deploy script: it re-tags only
        // finding_history rows not already the incumbent's, so a second run updates nothing.
        var retag = Regex.Match(sql, @"UPDATE monitoring\.KnowledgeChunk\s+SET ProfileKey = N'arrivia'\s+WHERE SourceType = N'finding_history'\s+AND ProfileKey <> N'arrivia'[^;]+;");
        Assert.True(retag.Success, "CreateMonitoringFindings.sql must re-tag the incumbent's finding_history chunks.");
        Assert.Contains("ProfileKey = N''", retag.Value);
        Assert.Contains("[[]hotels/%", retag.Value);
        Assert.DoesNotMatch(@"(?i)\b(DELETE|DROP|TRUNCATE)\b", retag.Value);
    }

    // ------------------------------------------------------------------------------------------------
    // M1 - Web proxy
    // ------------------------------------------------------------------------------------------------

    /// <summary>Contract v6 §4/§5 reads whose answer depends on the company.</summary>
    private static readonly string[] CompanyReads =
    [
        "/api/journeys/db", "/api/exceptions", "/api/overview", "/api/analytics/metrics", "/api/clubs",
        "/api/club-inventory", "/api/profile-journeys", "/api/profile-journeys/{journeyId}", "/api/entities",
        "/api/hotels/business-health", "/api/monkeys", "/api/findings", "/api/findings/{id}/doctor-report",
        "/api/scout/packages", "/api/scout/runs", "/api/scout/stats", "/api/scouts/{board}/shadow", "/api/scout/packages/{id}",
        "/api/inspector/cases", "/api/inspector/queue", "/api/inspector/toasts", "/api/inspector/cases/{id}", "/api/inspector/cases/{id}/rca",
        "/api/knowledge/documents", "/api/knowledge/document", "/api/knowledge/search",
        "/api/health/pillars", "/api/health/anomalies", "/api/health/kpis/{id:int}/buckets",
        "/api/daemons", "/api/daemons/activity", "/api/triage/investigations", "/api/triage/investigations/{id:guid}",
        "/api/settings/kpis", "/api/settings/kpis/{id:int}/audit",
    ];

    [Fact]
    public void Proxy_AllCompanyReadsForwardProfileKey()
    {
        var web = Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Web");
        var program = File.ReadAllText(Path.Combine(web, "Program.cs"));
        var client = File.ReadAllText(Path.Combine(web, "Services", "JourneyMonitorApiClient.cs"));
        var missing = new List<string>();

        foreach (var route in CompanyReads)
        {
            var handler = GetHandler(program, route);
            if (!handler.Contains(".Request.QueryString", StringComparison.Ordinal))
            {
                missing.Add($"{route}: handler drops the query string");
                continue;
            }

            // Routes that call a dedicated client method: that method must put the query string on the URL.
            var call = Regex.Match(handler, @"client\.(\w+Async)\(");
            if (!call.Success) continue;
            var impl = Regex.Match(client, $@"public Task<\(int StatusCode, string Body\)> {call.Groups[1].Value}\((?<args>[^)]*)\) =>\s*(?<body>[^;]+);");
            if (!impl.Success) missing.Add($"{route}: {call.Groups[1].Value} not found");
            else if (!impl.Groups["args"].Value.Contains("string queryString", StringComparison.Ordinal)
                     || !impl.Groups["body"].Value.Contains("{queryString}", StringComparison.Ordinal))
            {
                missing.Add($"{route}: {call.Groups[1].Value} does not forward the query string");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData("/api/findings", "GetFindingsAsync", "/api/findings{queryString}")]
    [InlineData("/api/findings/{id}/doctor-report", "GetFindingDoctorReportAsync", "/doctor-report{queryString}")]
    [InlineData("/api/scout/packages/{id}", "GetScoutPackageAsync", "/api/scout/packages/{Uri.EscapeDataString(id)}{queryString}")]
    [InlineData("/api/inspector/queue", "GetInspectorQueueAsync", "/api/inspector/queue{queryString}")]
    [InlineData("/api/inspector/cases/{id}", "GetInspectorCaseAsync", "/api/inspector/cases/{Uri.EscapeDataString(id)}{queryString}")]
    [InlineData("/api/inspector/cases/{id}/rca", "GetRcaAsync", "/rca{queryString}")]
    [InlineData("/api/knowledge/documents", "GetKnowledgeDocumentsAsync", "/api/knowledge/documents{queryString}")]
    [InlineData("/api/triage/investigations/{id:guid}", "GetTriageInvestigationAsync", "/api/triage/investigations/{id:D}{queryString}")]
    public void Proxy_TheEightRoutesFromTheReviewForwardProfileKey(string route, string method, string forwardedPath)
    {
        var web = Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Web");
        var handler = GetHandler(File.ReadAllText(Path.Combine(web, "Program.cs")), route);
        Assert.Contains($"client.{method}(", handler);
        Assert.Contains("context.Request.QueryString.Value", handler);
        Assert.Contains(forwardedPath, File.ReadAllText(Path.Combine(web, "Services", "JourneyMonitorApiClient.cs")));
    }

    // ------------------------------------------------------------------------------------------------

    private static DoctorAgentService Doctor(IKnowledgeRetriever retriever, IActiveCompanyProfileStore? activeCompany, ICompanyProfileStore? profiles) =>
        new(new HeuristicErrorClassifier(),
            retriever,
            new FakeLlmClient(DoctorResponse, embedding: [0.1f, 0.2f]),
            new InMemoryDoctorReportsRepository(),
            new InMemoryLlmCallLogRepository(),
            new InMemoryMonkeyRunTracker(),
            NullLogger<DoctorAgentService>.Instance,
            activeCompany,
            profiles);

    private static InMemoryCompanyProfileStore Store(params string[] keys) =>
        new(keys.Select(k => new CompanyProfile(
            k, k, new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"), [], [],
            new CompanyGlossary("service", "flow", "step", "entity", "owner"))));

    private static Finding HotelFinding() => new()
    {
        Domain = "hotels",
        FindingType = FindingType.LatencySpikeCluster,
        Stage = "07 Booking",
        Title = "'07 Booking' latency error budget burning (hotels)",
        Severity = "high",
        EvidenceJson = """{"statusCodeBreakdown":{"200":10,"504":2}}""",
        OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
        CorrelationId = "hotels:07 Booking",
    };

    private static KnowledgeChunk Chunk(string sourceType, string? title, string content) => new()
    {
        SourceType = sourceType,
        DocumentTitle = title,
        Content = content,
        Embedding = [],
        EmbeddingModel = KnowledgeChunk.NoEmbeddingModel,
    };

    private static string GetHandler(string program, string route)
    {
        var start = program.IndexOf($"app.MapGet(\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Web Program.cs must map GET {route}.");
        var next = Regex.Match(program[(start + 10)..], @"\bapp\.Map(Get|Post|Put|Delete)\(");
        return next.Success ? program.Substring(start, next.Index + 10) : program[start..];
    }

    private static string ProcedureBody(string sql, string procedure)
    {
        var start = sql.IndexOf($"CREATE OR ALTER PROCEDURE monitoring.{procedure}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{procedure} must exist.");
        var end = sql.IndexOf("\nGO", start, StringComparison.Ordinal);
        return end < 0 ? sql[start..] : sql[start..end];
    }

    private static string FindPortalDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker", "init-sql.sh"))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "Portal", "docker", "init-sql.sh"))) return Path.Combine(dir.FullName, "Portal");
        }
        throw new InvalidOperationException("Could not find the Portal directory (docker/init-sql.sh).");
    }

    private sealed class FixedActiveCompany(string profileKey) : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(profileKey);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) =>
            Task.FromResult<ActiveCompanyProfile?>(new ActiveCompanyProfile(profileKey, DateTimeOffset.UtcNow, null));

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => Task.CompletedTask;
    }
}
