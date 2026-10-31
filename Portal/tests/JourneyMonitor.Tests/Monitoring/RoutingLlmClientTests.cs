using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Returns a different configuration per agent, which is the whole point of the router.</summary>
internal sealed class ScriptedLlmClientSettingsResolver(Func<string, ResolvedLlmClientSettings> byAgent) : ILlmClientSettingsResolver
{
    public Task<ResolvedLlmClientSettings> ResolveAsync(CancellationToken cancellationToken) =>
        ResolveAsync(LlmAgents.Unspecified, cancellationToken);

    public Task<ResolvedLlmClientSettings> ResolveAsync(string agent, CancellationToken cancellationToken) =>
        Task.FromResult(byAgent(agent));
}

/// <summary>Records every URL it is asked for and replies with a canned body, so a test can assert WHERE a call went.</summary>
internal sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Urls.Add(url);

        // The Copilot path exchanges a session token first; answer those two the way the real API
        // does for a Business plan (403 -> use the gho_ token directly) so the test exercises the
        // actual chat call rather than dying during auth.
        if (url.Contains("copilot_internal/user", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
        if (url.Contains("copilot_internal/v2/token", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") });
        }

        return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) });
    }
}

public class RoutingLlmClientTests
{
    private const string ToolCallingResponse = """
        {"choices":[{"finish_reason":"stop","message":{"content":"done"}}],"usage":{"prompt_tokens":11,"completion_tokens":3}}
        """;

    private static ResolvedLlmClientSettings Copilot() =>
        new("gho_faketokenfortests1234567890", "gpt-4o") { Provider = LlmProviders.GitHubCopilot };

    private static ResolvedLlmClientSettings Foundry(string? endpoint = "https://fake.openai.azure.com") =>
        new("gho_faketokenfortests1234567890", "gpt-4o")
        {
            Provider = LlmProviders.Foundry,
            FoundryEndpoint = endpoint,
            FoundryAuthMode = FoundryAuthModes.ApiKey,
            FoundryApiKey = "fake-key",
            FoundryChatDeployment = "chat-dep",
            FoundryEmbeddingDeployment = "embed-dep",
        };

    private static (RoutingLlmClient Router, RecordingHandler Copilot, RecordingHandler Foundry) BuildRouter(
        string agent,
        Func<string, ResolvedLlmClientSettings> byAgent,
        string responseBody = ToolCallingResponse,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var resolver = new ScriptedLlmClientSettingsResolver(byAgent);
        var copilotHandler = new RecordingHandler(responseBody, statusCode);
        var foundryHandler = new RecordingHandler(responseBody, statusCode);

        var router = new RoutingLlmClient(
            agent,
            resolver,
            new GitHubCopilotLlmClient(new HttpClient(copilotHandler), resolver, NullLogger<GitHubCopilotLlmClient>.Instance),
            new FoundryLlmClient(new HttpClient(foundryHandler), resolver, NullLogger<FoundryLlmClient>.Instance),
            NullLogger<RoutingLlmClient>.Instance);

        return (router, copilotHandler, foundryHandler);
    }

    [Fact]
    public async Task ToolCallingGoesToFoundryWhenThatAgentIsOnFoundry()
    {
        var (router, copilot, foundry) = BuildRouter(LlmAgents.Inspector, _ => Foundry());

        await router.CompleteAsync("chat-dep", [new LlmMessage("user", "hi")], [], 100, CancellationToken.None);

        Assert.Contains(foundry.Urls, u => u.EndsWith("/openai/v1/chat/completions", StringComparison.Ordinal));
        Assert.Empty(copilot.Urls);
    }

    [Fact]
    public async Task ToolCallingGoesToGitHubCopilotWhenThatAgentIsOnCopilot()
    {
        var (router, copilot, foundry) = BuildRouter(LlmAgents.Inspector, _ => Copilot());

        await router.CompleteAsync("gpt-4o", [new LlmMessage("user", "hi")], [], 100, CancellationToken.None);

        Assert.Contains(copilot.Urls, u => u.Contains("githubcopilot.com", StringComparison.Ordinal));
        Assert.Empty(foundry.Urls);
    }

    [Fact]
    public async Task EachAgentFollowsItsOwnOverrideRatherThanASharedGlobal()
    {
        // The migration case: the Scout moves to Foundry first because it is the cheap agent,
        // while the Inspector stays where it is. One must not drag the other along.
        Func<string, ResolvedLlmClientSettings> byAgent = agent =>
            agent == LlmAgents.Scout ? Foundry() : Copilot();

        var (scoutRouter, scoutCopilot, scoutFoundry) = BuildRouter(LlmAgents.Scout, byAgent);
        var (doctorRouter, doctorCopilot, doctorFoundry) = BuildRouter(LlmAgents.Doctor, byAgent);

        await scoutRouter.CompleteAsync("prompt", 100, CancellationToken.None);
        await doctorRouter.CompleteAsync("prompt", 100, CancellationToken.None);

        Assert.NotEmpty(scoutFoundry.Urls);
        Assert.Empty(scoutCopilot.Urls);
        Assert.NotEmpty(doctorCopilot.Urls);
        Assert.Empty(doctorFoundry.Urls);
    }

    [Fact]
    public async Task AnEndpointThatAlreadyCarriesTheV1RouteIsNotAppendedTwice()
    {
        // Both forms get copied out of the portal; appending the route twice is a 404 that reads
        // like a missing deployment, sending the reader to the wrong place entirely.
        var (router, _, foundry) = BuildRouter(
            LlmAgents.Inspector, _ => Foundry("https://fake.openai.azure.com/openai/v1"));

        await router.CompleteAsync("chat-dep", [new LlmMessage("user", "hi")], [], 100, CancellationToken.None);

        var url = Assert.Single(foundry.Urls);
        Assert.Equal("https://fake.openai.azure.com/openai/v1/chat/completions", url);
    }

    [Fact]
    public async Task FoundryReportsUnavailableWithoutCallingAnythingWhenNoEndpointIsConfigured()
    {
        var (router, _, foundry) = BuildRouter(LlmAgents.Inspector, _ => Foundry(endpoint: null));

        var completion = await router.CompleteAsync("chat-dep", [new LlmMessage("user", "hi")], [], 100, CancellationToken.None);

        Assert.False(completion.IsAvailable);
        Assert.Contains("endpoint", completion.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(foundry.Urls);
    }

    [Fact]
    public async Task A404FromFoundrySaysTheModelMustBeADeploymentName()
    {
        // The roster is sent verbatim, so the most likely cause of a 404 is a model id left over
        // from the other provider. Naming that is the difference between a five-minute fix and an
        // afternoon of reading transcripts.
        var (router, _, _) = BuildRouter(
            LlmAgents.Inspector, _ => Foundry(), responseBody: "", statusCode: HttpStatusCode.NotFound);

        var completion = await router.CompleteAsync("claude-sonnet-5", [new LlmMessage("user", "hi")], [], 100, CancellationToken.None);

        Assert.False(completion.IsAvailable);
        Assert.Contains("deployment", completion.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbeddingsAreTaggedWithTheProviderAndDeploymentThatProducedThem()
    {
        var (router, _, _) = BuildRouter(
            LlmAgents.Doctor,
            _ => Foundry(),
            responseBody: """{"data":[{"embedding":[0.1,0.2]}]}""");

        var embedding = await router.GetEmbeddingAsync("text", CancellationToken.None);

        Assert.True(embedding.IsAvailable);
        // Provider-qualified: the same deployment name on another provider is a different vector
        // space, and comparing across the two is arithmetic without meaning.
        Assert.Equal("foundry:embed-dep", embedding.Model);
    }

    [Fact]
    public async Task AnEmbeddingFromCopilotNeverCompareEqualToOneFromFoundry()
    {
        var (foundryRouter, _, _) = BuildRouter(
            LlmAgents.Doctor, _ => Foundry(), responseBody: """{"data":[{"embedding":[0.1,0.2]}]}""");
        var (copilotRouter, _, _) = BuildRouter(
            LlmAgents.Doctor, _ => Copilot(), responseBody: """{"data":[{"embedding":[0.1,0.2]}]}""");

        var fromFoundry = await foundryRouter.GetEmbeddingAsync("text", CancellationToken.None);
        var fromCopilot = await copilotRouter.GetEmbeddingAsync("text", CancellationToken.None);

        Assert.NotEqual(fromCopilot.Model, fromFoundry.Model);
    }
}
