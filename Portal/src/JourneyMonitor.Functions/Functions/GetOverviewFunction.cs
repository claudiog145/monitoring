using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/overview — the seven Executive Overview tiles.</summary>
public sealed class GetOverviewFunction(
    GetOverviewService getOverviewService,
    IConfiguration configuration,
    ILogger<GetOverviewFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetOverview")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "overview")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var request = new OverviewRequest(
            query["domain"],
            query["club"] ?? query["entity"],
            query["env"] ?? query["environment"],
            query["win"],
            query["id"],
            query["profileKey"]);

        try
        {
            var outcome = await getOverviewService.GetAsync(request, cancellationToken);
            var result = AppendFabricConfigWarning(outcome.Result, configuration);
            return await WriteJsonAsync(req, outcome.StatusCode, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetOverview failed unexpectedly");
            return await WriteJsonAsync(
                req,
                (int)HttpStatusCode.InternalServerError,
                new { source = "error", error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(
        HttpRequestData req,
        int statusCode,
        object payload)
    {
        var response = req.CreateResponse((HttpStatusCode)statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    private static OverviewResult AppendFabricConfigWarning(OverviewResult result, IConfiguration configuration)
    {
        // Contract v6 §4.5: Fabric business health is incumbent-only; another company is not told to configure it.
        if (!result.FabricApplies)
            return result;

        var missing = new List<string>(capacity: 4);
        AddMissing(missing, configuration, FabricWarehouseOptions.ConnectionStringKey);
        AddMissing(missing, configuration, FabricWarehouseOptions.ClientIdKey);
        AddMissing(missing, configuration, FabricWarehouseOptions.ClientSecretKey);
        AddMissing(missing, configuration, FabricWarehouseOptions.TenantIdKey);

        if (missing.Count == 0)
            return result;

        var fabricWarning = "Fabric warehouse configuration is missing: " + string.Join(", ", missing) + ".";
        var warning = string.IsNullOrWhiteSpace(result.Warning)
            ? fabricWarning
            : result.Warning + " " + fabricWarning;

        return result with { Warning = warning };
    }

    private static void AddMissing(List<string> missing, IConfiguration configuration, string key)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
            missing.Add(key);
    }
}
