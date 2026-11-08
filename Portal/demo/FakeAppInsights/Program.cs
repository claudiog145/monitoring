using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FakeAppInsights;
using FakeAppInsights.Catalog;
using FakeAppInsights.Kusto;
using FakeAppInsights.Query;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FakeAppInsightsOptions>(builder.Configuration.GetSection(FakeAppInsightsOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<KustoProbe>();
builder.Services.AddHttpClient<IKustoExecutor, HttpKustoExecutor>(c => c.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddSingleton<IApplicationDirectory, KustoApplicationDirectory>();
builder.Services.AddScoped<QueryService>();
var app = builder.Build();

app.MapGet("/health", async (KustoProbe probe, IOptions<FakeAppInsightsOptions> options, CancellationToken ct) =>
    Results.Ok(new
    {
        status = "ok",
        kusto = await probe.IsReachableAsync(options.Value.KustoUrl, ct) ? "reachable" : "unreachable",
    }));

var api = app.MapGroup("/v1/apps").AddEndpointFilter(async (context, next) =>
{
    var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<FakeAppInsightsOptions>>().Value.ApiKey;
    var supplied = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
    return ApiKeyMatches(expected, supplied) ? await next(context) : AppInsightsError.InvalidApiKey().ToResult();
});

api.MapGet("", async (IApplicationDirectory directory, CancellationToken ct) =>
{
    try
    {
        var catalog = await directory.GetAsync(ct);
        return Results.Ok(catalog.Applications.Select(a => new { appId = a.AppId, database = a.Database }));
    }
    catch (Exception ex) when (ex is KustoUnavailableException or KustoQueryException)
    {
        return AppInsightsError.ServiceUnavailable(ex.Message).ToResult();
    }
});

api.MapGet("/{appId}/query", async (string appId, string? query, string? timespan, QueryService service, CancellationToken ct) =>
    ToResult(await service.ExecuteAsync(appId, query, timespan, ct)));

api.MapPost("/{appId}/query", async (string appId, HttpRequest request, QueryService service, CancellationToken ct) =>
{
    string? query = null;
    var timespan = request.Query["timespan"].ToString();
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        if (body.RootElement.ValueKind != JsonValueKind.Object)
            return AppInsightsError.BadArgument("The request body must be a JSON object.", "InvalidBody").ToResult();
        foreach (var property in body.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            if (property.Name.Equals("query", StringComparison.OrdinalIgnoreCase))
                query = property.Value.GetString();
            else if (property.Name.Equals("timespan", StringComparison.OrdinalIgnoreCase))
                timespan = property.Value.GetString();
        }
    }
    catch (JsonException ex)
    {
        return AppInsightsError.BadArgument($"The request body is not valid JSON: {ex.Message}", "InvalidBody").ToResult();
    }
    return ToResult(await service.ExecuteAsync(appId, query, timespan, ct));
});

app.Run();

static IResult ToResult(QueryOutcome outcome) =>
    outcome.Error is not null ? outcome.Error.ToResult() : Results.Json(outcome.Body);

static bool ApiKeyMatches(string? expected, string supplied)
{
    if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied)) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
}

public partial class Program;
