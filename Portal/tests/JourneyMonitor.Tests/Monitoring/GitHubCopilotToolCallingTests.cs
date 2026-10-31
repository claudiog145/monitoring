using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Infrastructure.Ai;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// Same routing idea as FakeGitHubHandler, but it captures the chat/completions body and lets a
/// test choose the status code - both are what the tool-calling path has to be pinned on.
/// </summary>
internal sealed class FakeToolCallingHandler(string chatBody, HttpStatusCode chatStatus = HttpStatusCode.OK) : HttpMessageHandler
{
    public string? LastChatRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("copilot_internal/user"))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (url.Contains("copilot_internal/v2/token"))
        {
            // 403 is the documented Business-plan outcome: the gho_ token is used directly.
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        if (url.Contains("chat/completions"))
        {
            LastChatRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(chatStatus) { Content = new StringContent(chatBody) };
        }

        throw new InvalidOperationException($"Unexpected request to {url} in test.");
    }
}

public class GitHubCopilotToolCallingTests
{
    // Verbatim shape captured from the live spike (docs/sessions/assets/spike-toolcalling), trimmed
    // to the fields this client reads. Note there is no "content" property at all on a tool-call turn.
    private const string ToolCallResponse = """
        {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[
          {"function":{"arguments":"{\"kql\":\"exceptions | count\",\"reason\":\"Count exceptions\"}","name":"run_kql"},
           "id":"toolu_01AYUaZnAzAaja1zZzyvbAAK","type":"function"}]}}],
         "usage":{"completion_tokens":136,"prompt_tokens":552,"total_tokens":688},"model":"claude-sonnet-5"}
        """;

    private static readonly LlmMessage[] Messages =
    [
        new(LlmRole.System, "You are an SRE assistant."),
        new(LlmRole.User, "How many exceptions did hotels log in the last hour?"),
    ];

    private static readonly LlmToolDefinition[] Tools =
    [
        new("run_kql", "Runs a KQL query.", """{"type":"object","properties":{"kql":{"type":"string"}},"required":["kql"]}"""),
    ];

    private static GitHubCopilotLlmClient BuildClient(FakeToolCallingHandler handler, string? token = "gho_faketokenfortests1234567890") =>
        new(new HttpClient(handler), new FixedLlmClientSettingsResolver(token), new NoOpLogger<GitHubCopilotLlmClient>());

    [Fact]
    public async Task CompleteAsyncParsesToolCallsFromANonStreamingResponse()
    {
        var client = BuildClient(new FakeToolCallingHandler(ToolCallResponse));

        var result = await client.CompleteAsync("claude-sonnet-5", Messages, Tools, 1000, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.RequestedTools);
        Assert.Equal("tool_calls", result.FinishReason);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("toolu_01AYUaZnAzAaja1zZzyvbAAK", call.Id);
        Assert.Equal("run_kql", call.Name);
        Assert.Equal("exceptions | count", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("kql").GetString());
        // A tool-call turn carries no content; the record is non-nullable, so it must be empty, not null.
        Assert.Equal("", result.Content);
        Assert.Equal("claude-sonnet-5", result.ModelUsed);
    }

    [Fact]
    public async Task CompleteAsyncUsesTheProvidersOwnUsageBlockRatherThanACharacterEstimate()
    {
        var client = BuildClient(new FakeToolCallingHandler(ToolCallResponse));

        var result = await client.CompleteAsync("claude-sonnet-5", Messages, Tools, 1000, CancellationToken.None);

        Assert.Equal(552, result.TokensIn);
        Assert.Equal(136, result.TokensOut);
    }

    [Fact]
    public async Task CompleteAsyncReturnsUnavailableWithTheStatusCodeWhenTheModelRejectsTheRequest()
    {
        // gpt-5.3-codex advertises supports.tool_calls=true and still answers 400 with no body.
        var client = BuildClient(new FakeToolCallingHandler("", HttpStatusCode.BadRequest));

        var result = await client.CompleteAsync("gpt-5.3-codex", Messages, Tools, 1000, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("400", result.Error);
        Assert.Equal("gpt-5.3-codex", result.ModelUsed);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(0, result.TokensIn);
    }

    [Fact]
    public async Task CompleteAsyncReturnsUnavailableWhenNoTokenIsConfigured()
    {
        var client = BuildClient(new FakeToolCallingHandler(ToolCallResponse), token: null);

        var result = await client.CompleteAsync("gpt-4o", Messages, Tools, 1000, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Error);
        Assert.Equal("gpt-4o", result.ModelUsed);
    }

    [Fact]
    public async Task CompleteAsyncEmbedsTheToolSchemaAsLiveJsonNotAnEscapedString()
    {
        var handler = new FakeToolCallingHandler(ToolCallResponse);
        var client = BuildClient(handler);

        await client.CompleteAsync("gpt-4o", Messages, Tools, 1000, CancellationToken.None);

        Assert.NotNull(handler.LastChatRequestBody);
        using var sent = JsonDocument.Parse(handler.LastChatRequestBody!);
        var function = sent.RootElement.GetProperty("tools")[0].GetProperty("function");
        var parameters = function.GetProperty("parameters");
        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("kql").GetProperty("type").GetString());
        Assert.Equal("function", sent.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("gpt-4o", sent.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CompleteAsyncOmitsTheToolsPropertyWhenThereAreNoTools()
    {
        var handler = new FakeToolCallingHandler(ToolCallResponse);
        var client = BuildClient(handler);

        await client.CompleteAsync("gpt-4o", Messages, [], 1000, CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastChatRequestBody!);
        Assert.False(sent.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task CompleteAsyncSkipsAMalformedToolCallInsteadOfFailingTheTurn()
    {
        const string mixed = """
            {"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[
              {"id":"missing-function","type":"function"},
              {"function":{"name":"run_kql","arguments":"{}"},"id":"ok","type":"function"}]}}]}
            """;
        var client = BuildClient(new FakeToolCallingHandler(mixed));

        var result = await client.CompleteAsync("gpt-4o", Messages, Tools, 1000, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("ok", Assert.Single(result.ToolCalls).Id);
    }

    [Fact]
    public async Task CompleteAsyncReportsAnUnknownFinishReasonWhenTheProviderOmitsIt()
    {
        var client = BuildClient(new FakeToolCallingHandler("""{"choices":[{"message":{"content":"hello"}}]}"""));

        var result = await client.CompleteAsync("gpt-4o", Messages, Tools, 1000, CancellationToken.None);

        Assert.Equal("unknown", result.FinishReason);
        Assert.Equal("hello", result.Content);
        Assert.False(result.RequestedTools);
    }

    [Fact]
    public async Task CompleteAsyncSendsAToolResultTurnWithItsToolCallId()
    {
        var handler = new FakeToolCallingHandler("""{"choices":[{"finish_reason":"stop","message":{"content":"59 exceptions."}}],"usage":{"prompt_tokens":749,"completion_tokens":157}}""");
        var client = BuildClient(handler);

        LlmMessage[] secondTurn =
        [
            Messages[0],
            Messages[1],
            new(LlmRole.Assistant, null, ToolCalls: [new LlmToolCall("call_1", "run_kql", """{"kql":"exceptions | count"}""")]),
            new(LlmRole.Tool, """{"count":59}""", ToolCallId: "call_1"),
        ];

        var result = await client.CompleteAsync("claude-sonnet-5", secondTurn, Tools, 1000, CancellationToken.None);

        Assert.Equal("59 exceptions.", result.Content);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(749, result.TokensIn);

        using var sent = JsonDocument.Parse(handler.LastChatRequestBody!);
        var messages = sent.RootElement.GetProperty("messages");
        var assistant = messages[2];
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        var sentCall = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", sentCall.GetProperty("id").GetString());
        // Arguments must stay a JSON string in this protocol, not be expanded into an object.
        Assert.Equal(JsonValueKind.String, sentCall.GetProperty("function").GetProperty("arguments").ValueKind);
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
    }
}
