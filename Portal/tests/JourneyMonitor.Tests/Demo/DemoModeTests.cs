using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Infrastructure;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Demo;

/// <summary>
/// DT2 (contracts-demo-telemetry §5): the switches that let the platform run against the demo
/// stack - query endpoint, wildcard API key, DEMO banner and per-company corpus import - and the
/// guarantee that with their defaults nothing changes.
/// </summary>
public class DemoModeTests
{
    private const string AppId = "d6751ba7-7607-4254-95db-a50f58eb14f2";
    private const string InternalSecret = "dt2-internal-secret";
    private const string RcaBody = "## Root Cause\nThe core gateway entered a degraded state and every transfer timed out for forty minutes.";

    private static (AppInsightsTelemetrySource Source, CapturingQueryHandler Handler) BuildSource(InsightsOptions options)
    {
        var handler = new CapturingQueryHandler();
        var source = new AppInsightsTelemetrySource(
            new HttpClient(handler), new NoStoredTokens(), Options.Create(options), NullLogger<AppInsightsTelemetrySource>.Instance);
        return (source, handler);
    }

    private static Task QueryAsync(AppInsightsTelemetrySource source, string appId) =>
        source.QueryAsync(new TelemetryQueryRequest("requests | take 1", appId, TenantId: null), CancellationToken.None);

    private static T Bind<T>(IDictionary<string, string?> settings, string section) where T : new()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new T();
        configuration.GetSection(section).Bind(options);
        return options;
    }

    [Fact]
    public async Task QueryEndpoint_DefaultIsAppInsights()
    {
        var options = Bind<InsightsOptions>(new Dictionary<string, string?>(), InsightsOptions.SectionName);
        Assert.Null(options.QueryEndpoint);
        Assert.Equal("https://api.applicationinsights.io", options.ResolvedQueryEndpoint);

        var (source, handler) = BuildSource(new InsightsOptions { QueryApiKeys = $"{AppId}=real-key" });
        await QueryAsync(source, AppId);

        Assert.Equal($"https://api.applicationinsights.io/v1/apps/{AppId}/query", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
    }

    [Fact]
    public async Task QueryEndpoint_OverrideUsedForQuery()
    {
        var options = Bind<InsightsOptions>(new Dictionary<string, string?>
        {
            ["Insights:QueryEndpoint"] = " http://fake-appinsights:8080 ",
            ["Insights:QueryApiKeys"] = "*:jm-demo-key",
        }, InsightsOptions.SectionName);

        var (source, handler) = BuildSource(options);
        await QueryAsync(source, "demo-bank-web-banking");

        Assert.Equal("http://fake-appinsights:8080/v1/apps/demo-bank-web-banking/query", handler.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("*:jm-demo-key")]
    [InlineData("*=jm-demo-key")]
    [InlineData(" * : jm-demo-key ;")]
    public async Task QueryApiKeys_WildcardAppliesToAnyApp(string configured)
    {
        var (source, handler) = BuildSource(new InsightsOptions { QueryApiKeys = configured });

        foreach (var appId in new[] { "demo-bank-web-banking", "demo-retail-checkout-api", AppId })
        {
            await QueryAsync(source, appId);
            Assert.Equal("jm-demo-key", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Api-Key")));
            Assert.Null(handler.LastRequest.Headers.Authorization);
        }
    }

    [Fact]
    public async Task QueryApiKeys_SpecificKeyWinsOverWildcard()
    {
        var (source, handler) = BuildSource(new InsightsOptions { QueryApiKeys = $"*:wildcard-key;{AppId}=specific-key" });

        await QueryAsync(source, AppId.ToUpperInvariant());
        Assert.Equal("specific-key", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Api-Key")));

        await QueryAsync(source, "demo-aml-screening-api");
        Assert.Equal("wildcard-key", Assert.Single(handler.LastRequest!.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public void DemoMode_DisabledByDefault()
    {
        var options = Bind<DemoOptions>(new Dictionary<string, string?>(), DemoOptions.SectionName);

        Assert.False(options.Enabled);
        Assert.False(options.ToInfo().Enabled);
        Assert.Equal("DEMO — synthetic data", options.ResolvedLabel);
        Assert.Equal("Demo", DemoOptions.SectionName);
    }

    [Fact]
    public void DemoMode_EndpointReportsEnabled()
    {
        var options = Bind<DemoOptions>(new Dictionary<string, string?> { ["Demo:Enabled"] = "true" }, DemoOptions.SectionName);

        // Exactly what GET /api/demo-mode serialises (Results.Json uses the web defaults).
        var json = JsonNode.Parse(JsonSerializer.Serialize(options.ToInfo(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();

        Assert.Equal(2, json.Count);
        Assert.True(json["enabled"]!.GetValue<bool>());
        Assert.Equal("DEMO — synthetic data", json["label"]!.GetValue<string>());

        var custom = Bind<DemoOptions>(new Dictionary<string, string?> { ["Demo:Enabled"] = "true", ["Demo:Label"] = "Demo Bank" }, DemoOptions.SectionName);
        Assert.Equal("Demo Bank", custom.ToInfo().Label);
    }

    [Fact]
    public async Task KnowledgeImport_SubPathRestrictsToFolder()
    {
        using var corpus = new TempCorpus();
        corpus.Write("bank/corpus/Payments/core-timeout.md", RcaBody);
        corpus.Write("bank/corpus/Cards/card-network.md", RcaBody.Replace("core gateway", "card network"));
        corpus.Write("retail/corpus/Checkout/hot-sale.md", RcaBody.Replace("core gateway", "checkout"));

        var retriever = new RecordingKnowledgeRetriever();
        var service = BuildService(corpus.Root, retriever);

        var result = await service.ImportAsync(100, null, "banco", CancellationToken.None, "bank/corpus");

        Assert.True(result.SourceAvailable);
        Assert.Equal(2, result.DocumentsRead);
        Assert.Equal(new[] { "Cards", "Payments" }, retriever.Indexed.Select(c => c.Domain ?? "").Distinct().Order().ToArray());
        Assert.DoesNotContain(retriever.Indexed, c => c.Content.Contains("checkout"));
        Assert.All(retriever.IndexedProfileKeys, key => Assert.Equal("banco", key));

        // Through the HTTP endpoint the same restriction holds, with '\' accepted as a separator.
        var viaHttp = new RecordingKnowledgeRetriever();
        var response = await BuildFunction(corpus.Root, viaHttp).RunAsync(Request("subPath=retail%5Ccorpus&profileKey=mercadolibre"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.ReadJson()["documentsRead"]!.GetValue<int>());
        Assert.Equal("Checkout", Assert.Single(viaHttp.Indexed.Select(c => c.Domain).Distinct()));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("bank/../../outside")]
    [InlineData("bank%5C..%5C..%5Coutside")]
    [InlineData("%2Fetc")]
    [InlineData("C:%5Cwindows")]
    [InlineData("does-not-exist")]
    [InlineData("bank/corpus/Payments/core-timeout.md")]
    public async Task KnowledgeImport_SubPathRejectsTraversal(string encodedSubPath)
    {
        using var corpus = new TempCorpus();
        corpus.Write("bank/corpus/Payments/core-timeout.md", RcaBody);

        var retriever = new RecordingKnowledgeRetriever();
        var response = await BuildFunction(corpus.Root, retriever).RunAsync(Request($"subPath={encodedSubPath}"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("subPath", response.ReadJson()["error"]!.GetValue<string>());
        Assert.Empty(retriever.Indexed);

        // The use case refuses it on its own too, so no other caller can bypass the endpoint check.
        var decoded = Uri.UnescapeDataString(encodedSubPath);
        await Assert.ThrowsAsync<InvalidKnowledgeSubPathException>(
            () => BuildService(corpus.Root, retriever).ImportAsync(100, null, null, CancellationToken.None, decoded));
    }

    private static KnowledgeImportService BuildService(string corpusRoot, IKnowledgeRetriever retriever) =>
        new(
            new FileSystemKnowledgeDocumentSource(Options.Create(new KnowledgeImportOptions { CorpusPath = corpusRoot })),
            retriever,
            new FakeLlmClient("", [0.1f, 0.2f]),
            NullLogger<KnowledgeImportService>.Instance);

    private static ImportKnowledgeCorpusFunction BuildFunction(string corpusRoot, IKnowledgeRetriever retriever) =>
        new(
            BuildService(corpusRoot, retriever),
            Options.Create(new KnowledgeImportOptions { CorpusPath = corpusRoot }),
            Options.Create(new MonitoringOptions { InternalApiSecret = InternalSecret }),
            NullLogger<ImportKnowledgeCorpusFunction>.Instance);

    private static FakeHttpRequestData Request(string query) =>
        new FakeHttpRequestData($"https://localhost/api/knowledge/import?{query}")
            .WithHeader("X-Internal-Api-Secret", InternalSecret);

    private sealed class TempCorpus : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "dt2-corpus-" + Guid.NewGuid().ToString("N"), "packs");

        public TempCorpus() => Directory.CreateDirectory(Root);

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
