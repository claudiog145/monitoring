using DemoGenerator.Kusto;
using DemoGenerator.Runtime;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IKustoClient, KustoRestClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<DemoOptions>>().Value;
    http.BaseAddress = new Uri(o.KustoUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(10, o.KustoTimeoutSeconds));
});
builder.Services.AddSingleton<PackCatalog>();
builder.Services.AddSingleton<ScenarioBoard>();
builder.Services.AddSingleton<DemoEngine>();
builder.Services.AddSingleton<KustoHealth>();
builder.Services.AddSingleton<FeederService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FeederService>());

var app = builder.Build();

app.Services.GetRequiredService<PackCatalog>().Load(app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.PacksPath);

app.MapGet("/health", async (KustoHealth kusto, FeederService feeder, DemoEngine engine, CancellationToken ct) => Results.Ok(new
{
    status = "ok",
    kusto = await kusto.IsReachableAsync(ct) ? "reachable" : "unreachable",
    feeder = feeder.IsRunning ? "running" : "stopped",
    lastFedAt = engine.LastFedAt,
}));

app.MapGet("/api/companies", (PackCatalog packs) => Results.Ok(packs.Companies.Values
    .OrderBy(a => a.CompanyKey, StringComparer.Ordinal)
    .Select(a => new
    {
        companyKey = a.CompanyKey,
        profileKey = a.ProfileKey,
        displayName = a.DisplayName,
        apps = a.Apps!.Select(x => new { appId = x.AppId, database = x.Database, roleName = x.RoleName, host = x.Host }),
        scenarios = (a.Scenarios ?? []).Select(s => new { key = s.Key, title = s.Title }),
    })));

app.MapPost("/api/reset", async (HttpRequest request, DemoEngine engine, CancellationToken ct) =>
{
    var (body, invalid) = await OptionalBody.ReadAsync<ResetRequest>(request, ct);
    if (invalid is not null) return invalid;
    try
    {
        var result = await engine.ResetAsync(body?.Companies, body?.Days, body?.Scale, ct);
        return Results.Ok(new
        {
            companies = result.Companies.Select(c => new { companyKey = c.CompanyKey, rows = c.Rows }),
            elapsedSeconds = result.ElapsedSeconds,
        });
    }
    catch (ResetRequestException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is KustoCommandException or HttpRequestException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/scenarios/stop", async (HttpRequest request, PackCatalog packs, ScenarioBoard board, CancellationToken ct) =>
{
    var (body, invalid) = await OptionalBody.ReadAsync<StopScenarioRequest>(request, ct);
    if (invalid is not null) return invalid;
    var company = string.IsNullOrWhiteSpace(body?.CompanyKey) ? null : body.CompanyKey.Trim();
    if (company is not null && packs.Find(company) is null) return Results.NotFound(new { error = $"Unknown company '{company}'." });
    return Results.Ok(new { stopped = board.Stop(company) });
});

app.MapPost("/api/scenarios/{companyKey}/{scenarioKey}/start", async (string companyKey, string scenarioKey, HttpRequest request, PackCatalog packs, ScenarioBoard board, CancellationToken ct) =>
{
    var (body, invalid) = await OptionalBody.ReadAsync<StartScenarioRequest>(request, ct);
    if (invalid is not null) return invalid;
    var archetype = packs.Find(companyKey);
    if (archetype is null) return Results.NotFound(new { error = $"Unknown company '{companyKey}'." });
    var scenario = (archetype.Scenarios ?? []).FirstOrDefault(s => s.Key == scenarioKey);
    if (scenario is null) return Results.NotFound(new { error = $"Company '{companyKey}' has no scenario '{scenarioKey}'." });
    var minutes = body?.DurationMinutes ?? scenario.DefaultDurationMinutes;
    if (minutes is < 1 or > 1440) return Results.BadRequest(new { error = "durationMinutes must be 1..1440." });
    return Results.Ok(board.Start(companyKey, scenarioKey, scenario.Title ?? scenarioKey, minutes));
});

app.MapGet("/api/scenarios/active", (ScenarioBoard board) => Results.Ok(board.Active()));

app.MapPost("/api/feeder/{action}", (string action, FeederService feeder) =>
{
    switch (action)
    {
        case "start": feeder.Start(); break;
        case "stop": feeder.Stop(); break;
        default: return Results.NotFound(new { error = "Use /api/feeder/start or /api/feeder/stop." });
    }
    return Results.Ok(new { feeder = feeder.IsRunning ? "running" : "stopped" });
});

app.Run();

public sealed record ResetRequest(IReadOnlyList<string>? Companies, int? Days, string? Scale);

public sealed record StartScenarioRequest(int? DurationMinutes);

public sealed record StopScenarioRequest(string? CompanyKey);

/// <summary>Every body of this API is optional: no body (with or without a content type) means "use the defaults".</summary>
internal static class OptionalBody
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static async Task<(T? Body, IResult? Invalid)> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<T>(text, Json), null);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (null, Results.BadRequest(new { error = $"The body is not valid JSON: {ex.Message}" }));
        }
    }
}

public partial class Program;
