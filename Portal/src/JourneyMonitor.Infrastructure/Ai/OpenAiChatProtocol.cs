using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Ai;

/// <summary>
/// The OpenAI-shaped <c>chat/completions</c> wire format, shared by every provider that speaks it.
///
/// GitHub Copilot and Microsoft Foundry expose the same request/response shape - what differs is
/// the base URL, the authentication header and whether <c>model</c> names a model or a deployment.
/// Keeping the body builder and the response parser here means a bug fixed in one provider is
/// fixed in both, and a new provider is a transport, not a second parser to keep in step.
/// </summary>
internal static class OpenAiChatProtocol
{
    /// <summary>Repo-wide rule: anything serialized to or read back by property name uses web defaults.</summary>
    internal static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the body with <see cref="JsonNode"/> rather than an anonymous type so each tool's
    /// <see cref="LlmToolDefinition.ParametersJsonSchema"/> can be embedded as live JSON; passing it
    /// through a string property would ship the schema escaped and the model would ignore it.
    /// </summary>
    internal static string BuildToolCallingRequestJson(
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens)
    {
        var root = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(ToMessageNode).ToArray()),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
            ["stream"] = false,
        };

        // Some endpoints reject an empty tools array outright, so omit the property entirely instead.
        if (tools.Count > 0)
        {
            root["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersJsonSchema),
                },
            }).ToArray());
        }

        return root.ToJsonString(WebJson);
    }

    private static JsonNode ToMessageNode(LlmMessage message)
    {
        // content stays present even when null: that is how an assistant turn that only asked for
        // tools is replayed back to the provider on the next turn.
        var node = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content,
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            node["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            node["tool_calls"] = new JsonArray(message.ToolCalls.Select(tc => (JsonNode)new JsonObject
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tc.Name,
                    // Arguments travel as a JSON *string* in this protocol, not as an object.
                    ["arguments"] = tc.ArgumentsJson,
                },
            }).ToArray());
        }

        return node;
    }

    /// <summary>
    /// <paramref name="model"/> is echoed back as requested rather than read from the response: a
    /// provider resolves aliases ('gpt-4o' comes back as 'gpt-4o-2024-11-20'), and the Inspector has
    /// to match a result to the roster entry it asked for.
    /// </summary>
    internal static LlmCompletion ParseToolCallingResponse(string model, string body, ILogger logger, string providerTag)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var content = "";
        var finishReason = "unknown";
        var toolCalls = new List<LlmToolCall>();

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String &&
                reason.GetString() is { Length: > 0 } reasonText)
            {
                finishReason = reasonText;
            }

            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                // content is absent (not just null) when the model only asked for tools.
                if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    content = contentEl.GetString() ?? "";
                }

                if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in toolCallsEl.EnumerateArray())
                    {
                        if (TryReadToolCall(element, out var toolCall))
                        {
                            toolCalls.Add(toolCall);
                        }
                        else
                        {
                            logger.LogWarning("[{Provider}] Skipped a malformed tool_call from model {Model}.", providerTag, model);
                        }
                    }
                }
            }
        }

        var tokensIn = 0;
        var tokensOut = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            tokensIn = ReadInt(usage, "prompt_tokens");
            tokensOut = ReadInt(usage, "completion_tokens");
        }

        return new LlmCompletion(content, toolCalls, finishReason, tokensIn, tokensOut, model, Error: null);
    }

    /// <summary>A tool call missing its id or function is unusable - drop it instead of failing the whole turn.</summary>
    private static bool TryReadToolCall(JsonElement element, out LlmToolCall toolCall)
    {
        toolCall = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!element.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
        if (!element.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) return false;
        if (!function.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return false;
        if (!function.TryGetProperty("arguments", out var argsEl) || argsEl.ValueKind != JsonValueKind.String) return false;

        if (idEl.GetString() is not { Length: > 0 } id) return false;
        if (nameEl.GetString() is not { Length: > 0 } name) return false;

        toolCall = new LlmToolCall(id, name, argsEl.GetString() ?? "{}");
        return true;
    }

    internal static int ReadInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
            : 0;

    /// <summary>
    /// Model ids from a <c>/models</c> (or <c>/deployments</c>) listing, sorted for a stable picker.
    /// Returns null when the payload has no usable <c>data</c> array, which the caller reports as an
    /// unexpected response shape rather than as "this credential can reach nothing".
    /// </summary>
    internal static IReadOnlyList<string>? ReadModelIds(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return data.EnumerateArray()
            .Where(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(m => m.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
