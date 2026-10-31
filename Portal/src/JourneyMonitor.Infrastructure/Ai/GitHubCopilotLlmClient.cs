using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Ai;

/// <summary>
/// Calls the GitHub Copilot chat/embeddings API directly, authenticated with a GitHub OAuth
/// token (must start with "gho_" — a PAT/"ghp_" token is rejected by the Copilot chat endpoint).
/// Ported from the pattern already proven in the ai-reliable-platform-portal reference repo
/// (OpenAIProvider.cs): the gho_ token is exchanged for a short-lived session token via
/// api.github.com/copilot_internal/v2/token when available; for Business-plan accounts that
/// exchange returns 403/404 and the gho_ token is used directly as Bearer instead (both are
/// expected, not errors). The session is cached for 45 min to avoid 2 extra round-trips per call.
/// The <see cref="ILlmClient"/> chat completion uses SSE streaming; the tool-calling path below
/// does not, and a live spike confirmed the endpoint is fine with either.
/// Returns empty/null on any failure (missing token, network error, bad response) rather than
/// throwing — callers (DoctorAgentService) treat that as "unknown", never fabricate a diagnosis.
/// Token/model come from <see cref="ILlmClientSettingsResolver"/> (Settings tab override, falling
/// back to Ai:GitHubToken/Ai:Model) rather than a fixed environment value, resolved fresh on every
/// call so a token saved via the Settings tab takes effect without restarting the process.
///
/// Also serves the Inspector Monkey's tool-calling path. That path is deliberately separate and
/// non-streaming - see <see cref="CompleteAsync(ResolvedLlmClientSettings, string, IReadOnlyList{LlmMessage}, IReadOnlyList{LlmToolDefinition}, int, CancellationToken)"/>.
///
/// This type no longer implements <see cref="ILlmClient"/>/<see cref="IToolCallingLlmClient"/>
/// directly: <see cref="RoutingLlmClient"/> does, and picks a provider per call. Every method here
/// comes in two forms - one that resolves settings itself, and one that accepts already-resolved
/// settings, which is what the router uses so the agent's provider choice is honoured end to end.
/// </summary>
public sealed class GitHubCopilotLlmClient
{
    private const string DefaultApiBase = "https://api.business.githubcopilot.com";

    private readonly HttpClient _http;
    private readonly ILlmClientSettingsResolver _settingsResolver;
    private readonly ILogger<GitHubCopilotLlmClient> _logger;

    private string? _bearerCache;
    private string? _apiBaseCache;
    private string? _cachedTokenUsed;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public GitHubCopilotLlmClient(HttpClient http, ILlmClientSettingsResolver settingsResolver, ILogger<GitHubCopilotLlmClient> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromMinutes(5);
        _settingsResolver = settingsResolver;
        _logger = logger;
    }

    public async Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default) =>
        await CompleteAsync(await _settingsResolver.ResolveAsync(ct), prompt, maxTokens, ct);

    public async Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(
        ResolvedLlmClientSettings settings, string prompt, int maxTokens, CancellationToken ct = default)
    {
        var token = settings.GitHubToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("[GitHubCopilot] No GitHubToken configured (Settings tab or Ai:GitHubToken) — skipping LLM call.");
            return ("", 0, 0, "none");
        }

        var (bearer, apiBase) = await GetSessionAsync(token, ct);
        if (bearer is null) return ("", 0, 0, "none");

        var requestBody = new
        {
            model = settings.Model,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = maxTokens,
            temperature = 0.2,
            stream = true,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {bearer}");
        request.Headers.Add("editor-version", "vscode/1.95.0");
        request.Headers.Add("editor-plugin-version", "copilot-chat/0.22.0");
        request.Headers.Add("openai-intent", "conversation-panel");

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[GitHubCopilot] HTTP {Code} from chat/completions: {Body}",
                    (int)response.StatusCode, errorBody.Length > 500 ? errorBody[..500] : errorBody);
                return ("", 0, 0, "none");
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var sb = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                // ReadLineAsync returning null is end-of-stream; EndOfStream would block the thread here (CA2024).
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var payload = line["data: ".Length..];
                if (payload == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(contentEl.GetString());
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed SSE chunks — best-effort accumulation.
                }
            }

            var text = sb.ToString();
            // GitHub Copilot's SSE stream does not report token usage per-chunk reliably; approximate
            // from character counts (~4 chars/token) rather than fabricate a precise number. An empty
            // prompt/completion is genuinely 0 tokens, not 1 - only floor non-empty strings at 1.
            var approxIn = prompt.Length == 0 ? 0 : Math.Max(1, prompt.Length / 4);
            var approxOut = text.Length == 0 ? 0 : Math.Max(1, text.Length / 4);
            return (text, approxIn, approxOut, settings.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[GitHubCopilot] chat/completions call failed.");
            return ("", 0, 0, "none");
        }
    }

    /// <summary>
    /// One tool-capable turn. Unlike <see cref="CompleteAsync(string, int, CancellationToken)"/> this
    /// sends <c>stream: false</c>: a verified spike against the enterprise endpoint showed the
    /// non-streaming shape is the only one that returns a real <c>usage</c> block and a whole
    /// <c>tool_calls</c> array, instead of fragments that have to be reassembled and token counts
    /// that have to be guessed from character counts.
    ///
    /// <paramref name="model"/> wins over the configured model (the Inspector runs a roster), but the
    /// token still comes from the settings resolver so a Settings-tab change applies without a restart.
    /// </summary>
    public async Task<LlmCompletion> CompleteAsync(
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct = default) =>
        await CompleteAsync(await _settingsResolver.ResolveAsync(ct), model, messages, tools, maxTokens, ct);

    public async Task<LlmCompletion> CompleteAsync(
        ResolvedLlmClientSettings settings,
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct = default)
    {
        var token = settings.GitHubToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("[GitHubCopilot] No GitHubToken configured (Settings tab or Ai:GitHubToken) — skipping tool-calling LLM call.");
            return LlmCompletion.Unavailable(model, "No GitHub token is configured (Settings tab or Ai:GitHubToken).");
        }

        var (bearer, apiBase) = await GetSessionAsync(token, ct);
        if (bearer is null)
        {
            return LlmCompletion.Unavailable(model, "Could not authenticate with the GitHub Copilot API (the token must start with 'gho_' and have the 'copilot' scope).");
        }

        string requestJson;
        try
        {
            requestJson = OpenAiChatProtocol.BuildToolCallingRequestJson(model, messages, tools, maxTokens);
        }
        catch (JsonException ex)
        {
            // A tool's schema is author-written, not model-written: failing here is a bug in our own
            // tool definition, so name it rather than let the provider reject the whole request.
            return LlmCompletion.Unavailable(model, $"A tool's ParametersJsonSchema is not valid JSON: {ex.Message}");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions")
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Authorization", $"Bearer {bearer}");
            request.Headers.Add("editor-version", "vscode/1.95.0");
            request.Headers.Add("editor-plugin-version", "copilot-chat/0.22.0");
            request.Headers.Add("openai-intent", "conversation-panel");

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // A model advertising supports.tool_calls=true can still reject the request with an
                // empty body (observed on gpt-5.3-codex), so the status code is the only usable signal.
                var detail = string.IsNullOrWhiteSpace(body)
                    ? "empty response body"
                    : (body.Length > 500 ? body[..500] : body);
                _logger.LogWarning("[GitHubCopilot] HTTP {Code} from chat/completions for model {Model}: {Body}",
                    (int)response.StatusCode, model, detail);
                return LlmCompletion.Unavailable(model, $"GitHub Copilot API returned HTTP {(int)response.StatusCode} ({detail}).");
            }

            return OpenAiChatProtocol.ParseToolCallingResponse(model, body, _logger, "GitHubCopilot");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[GitHubCopilot] Could not parse the tool-calling response for model {Model}.", model);
            return LlmCompletion.Unavailable(model, $"Unreadable response from the GitHub Copilot API: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[GitHubCopilot] Tool-calling chat/completions call failed for model {Model}.", model);
            return LlmCompletion.Unavailable(model, $"The request to the GitHub Copilot API failed: {ex.Message}");
        }
    }

    public async Task<EmbeddingResult> GetEmbeddingAsync(string text, CancellationToken ct = default) =>
        await GetEmbeddingAsync(await _settingsResolver.ResolveAsync(ct), text, ct);

    public async Task<EmbeddingResult> GetEmbeddingAsync(ResolvedLlmClientSettings settings, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmbeddingResult.Unavailable;
        var token = settings.GitHubToken;
        if (string.IsNullOrWhiteSpace(token)) return EmbeddingResult.Unavailable;

        var embeddingModel = settings.EmbeddingModel;

        var (bearer, apiBase) = await GetSessionAsync(token, ct);
        if (bearer is null) return EmbeddingResult.Unavailable;

        var input = text.Length > 8000 ? text[..8000] : text;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/embeddings")
            {
                // 'input' as an ARRAY: the endpoint rejects a bare string with HTTP 400 and no
                // detail, which reads as "bad model name" and sends the reader the wrong way.
                Content = new StringContent(JsonSerializer.Serialize(new { model = embeddingModel, input = new[] { input } }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("editor-version", "vscode/1.95.0");
            request.Headers.Add("openai-intent", "conversation-panel");
            request.Headers.Add("Copilot-Integration-Id", "journey-monitor-doctor-monkey");

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GitHubCopilot] HTTP {Code} from embeddings for model {Model}: {Body}",
                    (int)response.StatusCode, embeddingModel, body.Length > 400 ? body[..400] : body);
                return EmbeddingResult.Unavailable;
            }

            using var doc = JsonDocument.Parse(body);

            // A 200 whose shape we cannot read used to return "unavailable" silently, which is
            // indistinguishable from a network failure when thousands of calls come back empty.
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                _logger.LogWarning("[GitHubCopilot] embeddings returned HTTP 200 with no usable 'data' array: {Body}",
                    body.Length > 400 ? body[..400] : body);
                return EmbeddingResult.Unavailable;
            }
            if (!data[0].TryGetProperty("embedding", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("[GitHubCopilot] embeddings returned HTTP 200 with no 'embedding' array: {Body}",
                    body.Length > 400 ? body[..400] : body);
                return EmbeddingResult.Unavailable;
            }

            // Provider-qualified so a chunk embedded here never compares against one from Foundry,
            // even if both providers happen to expose a model with the same name.
            var vector = arr.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            if (vector.Length == 0)
            {
                // A well-formed 200 carrying an empty vector: the endpoint accepted the request but
                // produced nothing usable. Worth naming, because it looks identical to a transport
                // failure from the caller's side.
                _logger.LogWarning("[GitHubCopilot] embeddings returned HTTP 200 with an EMPTY embedding array for model {Model}: {Body}",
                    embeddingModel, body.Length > 400 ? body[..400] : body);
                return EmbeddingResult.Unavailable;
            }

            return new EmbeddingResult(vector, $"github-copilot:{embeddingModel}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[GitHubCopilot] embeddings call failed.");
            return EmbeddingResult.Unavailable;
        }
    }

    /// <summary>
    /// Ad-hoc, uncached lookup for the Settings tab's model picker: exchanges the given token for
    /// a session the same way <see cref="RefreshSessionAsync"/> does but never touches the shared
    /// 45-min cache (this token may just be a candidate the user pasted, not yet saved).
    /// </summary>
    public async Task<(bool Success, IReadOnlyList<string> Models, string? Error)> ListAvailableModelsAsync(string gitHubToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gitHubToken))
        {
            return (false, [], "A GitHub token is required.");
        }

        var (bearer, apiBase) = await RefreshSessionAsync(gitHubToken, ct);
        if (bearer is null)
        {
            return (false, [], "Could not authenticate with the GitHub Copilot API using this token (must start with 'gho_' and have the 'copilot' scope).");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("editor-version", "vscode/1.95.0");
            request.Headers.Add("Copilot-Integration-Id", "journey-monitor-doctor-monkey");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, [], $"GitHub Copilot API returned HTTP {(int)response.StatusCode} from /models.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return (false, [], "Unexpected response shape from /models (no 'data' array).");
            }

            var ids = data.EnumerateArray()
                .Where(m => m.TryGetProperty("id", out _))
                .Select(m => m.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (true, ids, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[GitHubCopilot] /models call failed.");
            return (false, [], "The request to the GitHub Copilot API failed - see server logs.");
        }
    }

    private async Task<(string? Bearer, string ApiBase)> GetSessionAsync(string token, CancellationToken ct)
    {
        if (DateTime.UtcNow < _cacheExpiry && _bearerCache is not null && _apiBaseCache is not null && _cachedTokenUsed == token)
        {
            return (_bearerCache, _apiBaseCache);
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _cacheExpiry && _bearerCache is not null && _apiBaseCache is not null && _cachedTokenUsed == token)
            {
                return (_bearerCache, _apiBaseCache);
            }

            var (bearer, apiBase) = await RefreshSessionAsync(token, ct);
            if (bearer is not null)
            {
                _bearerCache = bearer;
                _apiBaseCache = apiBase;
                _cachedTokenUsed = token;
                _cacheExpiry = DateTime.UtcNow.AddMinutes(45);
            }
            return (bearer, apiBase);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<(string? Bearer, string ApiBase)> RefreshSessionAsync(string token, CancellationToken ct)
    {
        if (token.StartsWith("ghp_", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[GitHubCopilot] A PAT (ghp_) cannot be used for chat API calls. Generate an OAuth token instead: " +
                "gh auth login --scopes copilot && gh auth token.");
            return (null, DefaultApiBase);
        }

        if (!token.StartsWith("gho_", StringComparison.Ordinal))
        {
            // Anything other than the expected gho_ OAuth token (a malformed value, a different
            // token type, a copy-paste mistake) would otherwise reach the API and fail later with
            // a less obvious error - reject it here with a clear, actionable message instead.
            _logger.LogWarning(
                "[GitHubCopilot] Configured token does not start with 'gho_' - it will not work against the Copilot API. " +
                "Generate an OAuth token with: gh auth login --scopes copilot && gh auth token.");
            return (null, DefaultApiBase);
        }

        // Reuses the injected _http (not a fresh HttpClient) so this goes through the same
        // IHttpClientFactory-managed handler/proxy/cert pipeline as every other call in this class;
        // headers are set per-request (not DefaultRequestHeaders) since _http is a shared instance.
        var apiBase = DefaultApiBase;
        try
        {
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
            userRequest.Headers.Add("Authorization", $"token {token}");
            userRequest.Headers.Add("Accept", "application/json");
            userRequest.Headers.Add("editor-version", "vscode/1.95.0");
            userRequest.Headers.UserAgent.ParseAdd("JourneyMonitor-DoctorMonkey/1.0");

            var userResp = await _http.SendAsync(userRequest, ct);
            if (userResp.IsSuccessStatusCode)
            {
                using var userDoc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync(ct));
                if (userDoc.RootElement.TryGetProperty("endpoints", out var ep) &&
                    ep.TryGetProperty("api", out var apiProp) &&
                    apiProp.GetString() is { Length: > 0 } customApi)
                {
                    apiBase = customApi;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "[GitHubCopilot] Could not auto-discover API endpoint — using default {Base}.", apiBase);
        }

        try
        {
            using var exchRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/v2/token");
            exchRequest.Headers.Add("Authorization", $"token {token}");
            exchRequest.Headers.Add("Accept", "application/json");
            exchRequest.Headers.Add("editor-version", "vscode/1.95.0");
            exchRequest.Headers.UserAgent.ParseAdd("JourneyMonitor-DoctorMonkey/1.0");

            var exchResp = await _http.SendAsync(exchRequest, ct);
            if (exchResp.IsSuccessStatusCode)
            {
                using var exchDoc = JsonDocument.Parse(await exchResp.Content.ReadAsStringAsync(ct));
                if (exchDoc.RootElement.TryGetProperty("token", out var tokenEl) && tokenEl.GetString() is { Length: > 0 } sessionToken)
                {
                    return (sessionToken, apiBase);
                }
            }

            // 403/404 are the expected, documented outcomes for gho_ OAuth tokens on Business plans —
            // the token itself is used as the Bearer directly in that case.
            var status = (int)exchResp.StatusCode;
            if (status is 403 or 404)
            {
                return (token, apiBase);
            }

            _logger.LogWarning("[GitHubCopilot] Session token exchange failed (HTTP {Code}). Ensure the token has 'copilot' scope.", status);
            return (null, apiBase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[GitHubCopilot] Session token exchange request failed — falling back to using the token directly.");
            return (token, apiBase);
        }
    }
}
