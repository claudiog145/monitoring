using System.Net;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Infrastructure.Ai;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Discards everything - just needs to satisfy the ILogger&lt;T&gt; constructor parameter.</summary>
internal sealed class NoOpLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>Always resolves to a fixed token/model, never touching a store.</summary>
internal sealed class FixedLlmClientSettingsResolver(string? token, string model = "gpt-4o-mini") : ILlmClientSettingsResolver
{
    public Task<ResolvedLlmClientSettings> ResolveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedLlmClientSettings(token, model));

    public Task<ResolvedLlmClientSettings> ResolveAsync(string agent, CancellationToken cancellationToken) =>
        ResolveAsync(cancellationToken);
}

/// <summary>
/// Routes by request URL to a fixed response, simulating the GitHub session-exchange calls
/// (both now go through the same injected HttpClient - see RefreshSessionAsync) plus the actual
/// chat/completions call, without any real network access.
/// </summary>
internal sealed class FakeGitHubHandler(string chatCompletionsSseBody) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("copilot_internal/user"))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (url.Contains("copilot_internal/v2/token"))
        {
            // 403/404 are the documented Business-plan outcome: the gho_ token is used directly.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }

        if (url.Contains("chat/completions"))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(chatCompletionsSseBody),
            });
        }

        if (url.Contains("/models"))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"}]}"""),
            });
        }

        throw new InvalidOperationException($"Unexpected request to {url} in test.");
    }
}

public class GitHubCopilotLlmClientTests
{
    private static GitHubCopilotLlmClient BuildClient(string? token) =>
        new(new HttpClient(), new FixedLlmClientSettingsResolver(token), new NoOpLogger<GitHubCopilotLlmClient>());

    private static GitHubCopilotLlmClient BuildClientWithFakeSse(string chatCompletionsSseBody) =>
        new(new HttpClient(new FakeGitHubHandler(chatCompletionsSseBody)),
            new FixedLlmClientSettingsResolver("gho_faketokenfortests1234567890"),
            new NoOpLogger<GitHubCopilotLlmClient>());

    [Fact]
    public async Task CompleteAsyncReturnsEmptyWhenNoTokenIsConfigured()
    {
        var client = BuildClient(token: null);

        var (content, tokensIn, tokensOut, modelUsed) = await client.CompleteAsync("prompt", 100, CancellationToken.None);

        Assert.Equal("", content);
        Assert.Equal(0, tokensIn);
        Assert.Equal(0, tokensOut);
        Assert.Equal("none", modelUsed);
    }

    [Fact]
    public async Task CompleteAsyncReturnsEmptyForAPatInsteadOfAnOAuthToken()
    {
        var client = BuildClient(token: "ghp_thisIsAPersonalAccessTokenNotOAuth");

        var (content, _, _, _) = await client.CompleteAsync("prompt", 100, CancellationToken.None);

        Assert.Equal("", content);
    }

    [Fact]
    public async Task CompleteAsyncReturnsEmptyForATokenThatDoesNotStartWithGho()
    {
        // Neither ghp_ nor gho_ - e.g. a copy-paste mistake or an unrelated secret.
        var client = BuildClient(token: "some_random_value_123");

        var (content, _, _, _) = await client.CompleteAsync("prompt", 100, CancellationToken.None);

        Assert.Equal("", content);
    }

    [Fact]
    public async Task GetEmbeddingAsyncReportsUnavailableWhenNoTokenIsConfigured()
    {
        var client = BuildClient(token: null);

        var embedding = await client.GetEmbeddingAsync("text", CancellationToken.None);

        Assert.False(embedding.IsAvailable);
        Assert.Null(embedding.Vector);
        // "none" rather than a plausible model name: an unavailable embedding must never be
        // recorded against a model, or a chunk could later be compared as if it were comparable.
        Assert.Equal("none", embedding.Model);
    }

    [Fact]
    public async Task CompleteAsyncReportsZeroTokensForAnEmptyPromptAndEmptyCompletion()
    {
        // An empty prompt/completion is genuinely 0 tokens - the Math.Max(1, ...) approximation
        // used to floor this at 1, which looked identical to a real (if tiny) exchange in metrics.
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"\"}}]}\n\ndata: [DONE]\n\n";
        var client = BuildClientWithFakeSse(sse);

        var (content, tokensIn, tokensOut, _) = await client.CompleteAsync("", 100, CancellationToken.None);

        Assert.Equal("", content);
        Assert.Equal(0, tokensIn);
        Assert.Equal(0, tokensOut);
    }

    [Fact]
    public async Task CompleteAsyncApproximatesNonZeroTokensForANonEmptyPromptAndCompletion()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hello world\"}}]}\n\ndata: [DONE]\n\n";
        var client = BuildClientWithFakeSse(sse);

        var (content, tokensIn, tokensOut, modelUsed) = await client.CompleteAsync("a reasonably long prompt", 100, CancellationToken.None);

        Assert.Equal("hello world", content);
        Assert.True(tokensIn > 0);
        Assert.True(tokensOut > 0);
        Assert.Equal("gpt-4o-mini", modelUsed);
    }

    [Fact]
    public async Task ListAvailableModelsAsyncReturnsTheModelsFromTheApi()
    {
        var client = new GitHubCopilotLlmClient(
            new HttpClient(new FakeGitHubHandler("unused")),
            new FixedLlmClientSettingsResolver(token: null),
            new NoOpLogger<GitHubCopilotLlmClient>());

        var (success, models, error) = await client.ListAvailableModelsAsync("gho_someCandidateTokenBeingTested", CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], models);
    }

    [Fact]
    public async Task ListAvailableModelsAsyncFailsForATokenThatDoesNotStartWithGho()
    {
        var client = new GitHubCopilotLlmClient(
            new HttpClient(new FakeGitHubHandler("unused")),
            new FixedLlmClientSettingsResolver(token: null),
            new NoOpLogger<GitHubCopilotLlmClient>());

        var (success, models, error) = await client.ListAvailableModelsAsync("ghp_notAnOAuthToken", CancellationToken.None);

        Assert.False(success);
        Assert.Empty(models);
        Assert.NotNull(error);
    }
}
