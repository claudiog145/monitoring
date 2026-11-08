using System.Net;
using System.Text.Json.Nodes;

namespace FakeAppInsights.Query;

/// <summary>An App Insights error (contract v5 Â§3.4) with the HTTP status it is answered with.</summary>
public sealed record AppInsightsError(HttpStatusCode Status, string Code, string Message, string? InnerCode = null, string? InnerMessage = null)
{
    public JsonObject ToJson()
    {
        var error = new JsonObject { ["message"] = Message, ["code"] = Code };
        if (InnerCode is not null || InnerMessage is not null)
        {
            error["innererror"] = new JsonObject { ["code"] = InnerCode ?? Code, ["message"] = InnerMessage ?? Message };
        }
        return new JsonObject { ["error"] = error };
    }

    public IResult ToResult() => Results.Json(ToJson(), statusCode: (int)Status);

    public static AppInsightsError InvalidApiKey() =>
        new(HttpStatusCode.Unauthorized, "InvalidApiKeyError", "Valid authentication was not provided",
            "InvalidApiKeyError", "The X-Api-Key header is missing or does not match the demo API key.");

    public static AppInsightsError ApplicationNotFound(string appId) =>
        new(HttpStatusCode.NotFound, "ApplicationNotFoundError", $"Application not found: {appId}",
            "ApplicationNotFoundError", $"No demo database answers to appId '{appId}'.");

    public static AppInsightsError BadArgument(string innerMessage, string? innerCode = null, string? message = null) =>
        new(HttpStatusCode.BadRequest, "BadArgumentError", message ?? "The request had some invalid properties",
            innerCode ?? "BadArgumentError", innerMessage);

    public static AppInsightsError ServiceUnavailable(string innerMessage) =>
        new(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableError", "The demo Kusto engine is not available",
            "ServiceUnavailableError", innerMessage);
}
