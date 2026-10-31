using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

// --- Authentication (Microsoft Entra ID / SSO) ---------------------------------------------
// Real deployments must configure "AzureAd" (TenantId/ClientId, see appsettings.json) - every
// page then requires authentication by default via the fallback policy below. Local development
// without an App Registration yet can leave AzureAd:TenantId empty, in which case auth is not
// wired at all so a fresh clone still runs (see README.md "Authentication" section for details).
var hasEntraIdConfig = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);
if (hasEntraIdConfig)
{
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = options.DefaultPolicy; // require an authenticated user everywhere by default
    });
    builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
}
else
{
    builder.Services.AddRazorPages();
}

// AddRazorPages() already registers antiforgery services; this just names the header app.js uses
// (default is a form field name, not meaningful for a JSON fetch API) - see the settings PUT
// route below and its GET /api/antiforgery-token companion.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

// --- AppInsights Token Management (Option 1 - External Tokens) ----------------------------
// Configure DataProtection with consistent settings across Web and Functions
// Keys stored in a shared location so both can decrypt tokens
var keysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
builder.Services.AddDataProtection()
    .SetApplicationName("JourneyMonitor")
    .PersistKeysToFileSystem(new DirectoryInfo(
        string.IsNullOrWhiteSpace(keysDirectory)
            ? Path.Combine(Path.GetTempPath(), "JourneyMonitorDataProtectionKeys")
            : keysDirectory));

builder.Services.AddSingleton<IAppInsightsTokenRepository>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var logger = sp.GetRequiredService<ILogger<SqlAppInsightsTokenRepository>>();
    return new SqlAppInsightsTokenRepository(connectionString, logger);
});

builder.Services.AddScoped<IAppInsightsTokenService, AppInsightsTokenService>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();

// --- JourneyMonitor.Functions typed client -------------------------------------------------
builder.Services.Configure<JourneyMonitorApiOptions>(builder.Configuration.GetSection(JourneyMonitorApiOptions.SectionName));
builder.Services.AddHttpClient<IJourneyMonitorApiClient, JourneyMonitorApiClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JourneyMonitorApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    // Refresh re-queries Application Insights and writes SQL; the default 100s HttpClient
    // timeout is too short for a large window or a cold Function.
    client.Timeout = TimeSpan.FromMinutes(5);
    // Extension point: in Azure, attach a Managed Identity-issued token here (service-to-service
    // call, see ARCHITECTURE.md #22-23) instead of calling the Function anonymously.
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
// index.html, app.js and styles.css are served unversioned (no hash in the file name), so a browser
// that heuristically caches app.js can run a stale script against a freshly fetched index.html -
// e.g. rendering 13 <td>s under 12 <th>s after a column is removed. "no-cache" still lets the
// browser reuse its copy, it just has to revalidate first (ETag/Last-Modified -> 304 when unchanged).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.UseRouting();

if (hasEntraIdConfig)
{
    app.UseAuthentication();
}
app.UseAuthorization();
app.UseAntiforgery();

// --- API proxy: Razor stays thin, all business logic lives behind these Function calls ----
// (see AGENTS.md - PageModels/endpoints must never reconstruct journeys, only forward requests).
app.MapGet("/api/journeys/db", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetDbJourneysAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/exceptions", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetExceptionsAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/overview", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetOverviewAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/analytics/metrics", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetDashboardMetricsAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/clubs", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetClubsAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/club-inventory", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetClubInventoryAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/monkeys", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetMonkeysAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

// Triggers a real monkey run (real LLM diagnoses, real Teams/Email notifications for the SLO
// Burn-Rate Monkey) - antiforgery-gated like the other mutating settings routes below, since this
// has a real cost/side effect and isn't a plain read.
var runMonkeyNowRoute = app.MapPost("/api/monkeys/{id}/run", async (HttpContext context, string id, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    var (statusCode, body) = await client.RunMonkeyNowAsync(id, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
if (!hasEntraIdConfig)
{
    runMonkeyNowRoute.AllowAnonymous();
    runMonkeyNowRoute.DisableAntiforgery();
}

// Durably pauses/resumes an agent (see PauseMonkeyFunction) - a mutation with a real operational
// effect, so it gets the same antiforgery/auth treatment as the run route above.
var pauseMonkeyRoute = app.MapPost("/api/monkeys/{id}/pause", async (HttpContext context, string id, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);
    var (statusCode, body) = await client.PauseMonkeyAsync(id, rawBody, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
if (!hasEntraIdConfig)
{
    pauseMonkeyRoute.AllowAnonymous();
    pauseMonkeyRoute.DisableAntiforgery();
}

app.MapGet("/api/findings", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetFindingsAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

var doctorReportRoute = app.MapGet("/api/findings/{id}/doctor-report", async (HttpContext context, string id, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetFindingDoctorReportAsync(id, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
// This detail includes the full LLM prompt (evidence + retrieved KB chunks) and raw response -
// heavier and more internal than the findings list, so once real auth exists it must require it,
// unlike every other read endpoint here which stays anonymous in every mode.
if (!hasEntraIdConfig)
{
    doctorReportRoute.AllowAnonymous();
}

app.MapGet("/api/scout/packages", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetScoutPackagesAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/scout/runs", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetScoutRunsAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/scout/stats", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetScoutStatsAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

var scoutPackageRoute = app.MapGet("/api/scout/packages/{id}", async (HttpContext context, string id, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetScoutPackageAsync(id, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
// Same reasoning as the doctor-report route: the full package carries the board's verbatim
// evidence, including member and correlation ids, so it must require auth once auth exists.
if (!hasEntraIdConfig)
{
    scoutPackageRoute.AllowAnonymous();
}

var inspectorCasesRoute = app.MapGet("/api/inspector/cases", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetInspectorCasesAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

var inspectorQueueRoute = app.MapGet("/api/inspector/queue", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetInspectorQueueAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

// Drops work the Scout already decided was worth investigating - a mutation with a real
// operational effect, so it gets the same antiforgery/auth treatment as pause and run.
var inspectorQueueDiscardRoute = app.MapPost("/api/inspector/queue/discard", async (HttpContext context, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);
    var (statusCode, body) = await client.DiscardInspectorQueueAsync(rawBody, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
if (!hasEntraIdConfig)
{
    inspectorQueueDiscardRoute.AllowAnonymous();
    inspectorQueueDiscardRoute.DisableAntiforgery();
}

var inspectorToastsRoute = app.MapGet("/api/inspector/toasts", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetInspectorToastsAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

// Only this one forwards X-Internal-Api-Secret (see GetInspectorCaseAsync): the case detail
// carries the verbatim LLM transcript - raw prompts and responses - exactly like the Scout's
// full package and the doctor-report, whereas the list/queue/toasts are summaries.
var inspectorCaseRoute = app.MapGet("/api/inspector/cases/{id}", async (HttpContext context, string id, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetInspectorCaseAsync(id, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
var rcaGetRoute = app.MapGet("/api/inspector/cases/{id}/rca", async (HttpContext context, string id, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetRcaAsync(id, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

// Composing is a WRITE in every sense that matters: it spends a provider call and replaces the
// document of record for an incident, so it gets the same antiforgery treatment as the settings
// PUTs rather than the lighter treatment of a read.
var rcaComposeRoute = app.MapPost("/api/inspector/cases/{id}/rca", async (HttpContext context, string id, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    var (statusCode, body) = await client.ComposeRcaAsync(id, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

// The corpus behind the RCA Agent. Read-only and content-free - titles and counts, never chunk
// text - so the evidence modal can show what was imported without becoming a document viewer.
var knowledgeDocumentsRoute = app.MapGet("/api/knowledge/documents", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetKnowledgeDocumentsAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

var knowledgeDocumentRoute = app.MapGet("/api/knowledge/document", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetKnowledgeDocumentAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

// Stays a GET despite costing a provider call: it changes nothing, and treating a read as a write
// would push it behind antiforgery for no benefit. The UI is what must make the cost visible.
var knowledgeSearchRoute = app.MapGet("/api/knowledge/search", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.SearchKnowledgeAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});

// Anonymous only while running locally without an App Registration; once Entra is configured
// every Inspector read requires an authenticated session (stricter than the Scout list routes,
// never laxer).
if (!hasEntraIdConfig)
{
    inspectorCasesRoute.AllowAnonymous();
    inspectorQueueRoute.AllowAnonymous();
    inspectorToastsRoute.AllowAnonymous();
    inspectorCaseRoute.AllowAnonymous();
    rcaGetRoute.AllowAnonymous();
    rcaComposeRoute.AllowAnonymous();
    rcaComposeRoute.DisableAntiforgery();
    knowledgeDocumentsRoute.AllowAnonymous();
    knowledgeDocumentRoute.AllowAnonymous();
    knowledgeSearchRoute.AllowAnonymous();
}

app.MapPost("/api/journeys/refresh", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.RefreshJourneysAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/api/settings/latency", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetLatencySpikeSettingsAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/settings/latency/audit", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetLatencySpikeSettingsAuditAsync(context.Request.QueryString.Value ?? "", cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/settings/llm-client", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetLlmClientSettingsAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/settings/llm-client/audit", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetLlmClientSettingsAuditAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/settings/slo", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetSloDefinitionsAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

app.MapGet("/api/settings/slo/audit", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetSloDefinitionsAuditAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}).AllowAnonymous();

// Editable KQL + the telemetry source catalog. Grouped rather than one named client method per
// route: the portal is a pure proxy for these, the only thing it adds is stamping the signed-in
// user onto a write so the audit trail records a person and not just "portal".
static async Task<IResult> ForwardCatalogGetAsync(HttpContext context, IJourneyMonitorApiClient client, string path, CancellationToken ct)
{
    var (statusCode, body) = await client.GetSettingsCatalogAsync(path, ct);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}

async Task<IResult> ForwardCatalogWriteAsync(
    HttpContext context, IJourneyMonitorApiClient client, IAntiforgery antiforgery,
    HttpMethod method, string path, CancellationToken ct)
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(ct);

    // An empty body is valid for revert/restore/import, so a missing one is not an error.
    System.Text.Json.Nodes.JsonNode? parsed = null;
    if (!string.IsNullOrWhiteSpace(rawBody))
    {
        try { parsed = System.Text.Json.Nodes.JsonNode.Parse(rawBody); }
        catch (System.Text.Json.JsonException)
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { error = "Request body must be a valid JSON object." }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    var node = parsed as System.Text.Json.Nodes.JsonObject ?? [];
    if (context.User.Identity?.IsAuthenticated == true)
    {
        node["changedBy"] = context.User.Identity.Name;
    }

    var (statusCode, body) = await client.WriteSettingsCatalogAsync(method, path, node.ToJsonString(), ct);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
}

var catalogRoutes = new List<RouteHandlerBuilder>
{
    app.MapGet("/api/settings/kql", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, "/api/settings/kql", ct)),
    app.MapGet("/api/settings/kql/{key}", (HttpContext c, IJourneyMonitorApiClient cl, string key, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/settings/kql/{Uri.EscapeDataString(key)}", ct)),
    app.MapGet("/api/settings/kql-audit", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/settings/kql-audit{c.Request.QueryString}", ct)),
    app.MapGet("/api/settings/kql-audit/{auditId:long}", (HttpContext c, IJourneyMonitorApiClient cl, long auditId, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/settings/kql-audit/{auditId}", ct)),
    app.MapGet("/api/settings/telemetry-sources", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, "/api/settings/telemetry-sources", ct)),
    app.MapGet("/api/settings/telemetry-sources-audit", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/settings/telemetry-sources-audit{c.Request.QueryString}", ct)),
    app.MapGet("/api/agent-budgets", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, "/api/agent-budgets", ct)),
    app.MapGet("/api/agent-budgets/{id}/history", (HttpContext c, IJourneyMonitorApiClient cl, string id, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/agent-budgets/{Uri.EscapeDataString(id)}/history", ct)),

    app.MapPut("/api/agent-budgets/{id}", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, string id, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Put, $"/api/agent-budgets/{Uri.EscapeDataString(id)}", ct)),

    app.MapPut("/api/settings/kql/{key}", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, string key, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Put, $"/api/settings/kql/{Uri.EscapeDataString(key)}", ct)),
    app.MapPost("/api/settings/kql/{key}/revert", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, string key, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Post, $"/api/settings/kql/{Uri.EscapeDataString(key)}/revert", ct)),
    app.MapPost("/api/settings/kql/import-builtins", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Post, "/api/settings/kql/import-builtins", ct)),
    app.MapPost("/api/settings/kql-audit/{auditId:long}/restore", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, long auditId, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Post, $"/api/settings/kql-audit/{auditId}/restore", ct)),
    app.MapPut("/api/settings/telemetry-sources", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Put, "/api/settings/telemetry-sources", ct)),
    app.MapPost("/api/settings/telemetry-sources/{alias}/{environment}/delete", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, string alias, string environment, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Post, $"/api/settings/telemetry-sources/{Uri.EscapeDataString(alias)}/{Uri.EscapeDataString(environment)}/delete", ct)),

    // "/active" and "/audit" are declared before "/{key}" so the reading order matches the
    // precedence routing already gives a literal segment over a parameter one - the same
    // reservation CompanyProfileAdminFunctions relies on, which is why no company may be keyed
    // "active" or "audit".
    app.MapGet("/api/settings/company-profiles", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, "/api/settings/company-profiles", ct)),
    app.MapGet("/api/settings/company-profiles-active", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, "/api/settings/company-profiles-active", ct)),
    app.MapGet("/api/settings/company-profiles-audit", (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/settings/company-profiles-audit{c.Request.QueryString}", ct)),
    app.MapGet("/api/settings/company-profiles/{key}", (HttpContext c, IJourneyMonitorApiClient cl, string key, CancellationToken ct) =>
        ForwardCatalogGetAsync(c, cl, $"/api/settings/company-profiles/{Uri.EscapeDataString(key)}", ct)),

    app.MapPut("/api/settings/company-profiles-active", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Put, "/api/settings/company-profiles-active", ct)),
    app.MapPut("/api/settings/company-profiles/{key}", (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, string key, CancellationToken ct) =>
        ForwardCatalogWriteAsync(c, cl, af, HttpMethod.Put, $"/api/settings/company-profiles/{Uri.EscapeDataString(key)}", ct)),

    // Onboarding is deliberately NOT forwarded through ForwardCatalog*: those carry the settings
    // write secret, and these two are gated by X-Internal-Api-Secret instead, so routing them
    // through the catalog pair would answer 401 with no hint why.
    app.MapGet("/api/onboarding/discover", async (HttpContext c, IJourneyMonitorApiClient cl, CancellationToken ct) =>
    {
        var (statusCode, body) = await cl.DiscoverTelemetryResourceAsync(c.Request.QueryString.Value ?? "", ct);
        c.Response.Headers.CacheControl = "no-store";
        return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
    }),
    // POST because it carries a body, not because it writes - but it still spends a real outbound
    // query against a caller-named resource, so it gets the same antiforgery gate as the writes.
    app.MapPost("/api/onboarding/draft", async (HttpContext c, IJourneyMonitorApiClient cl, IAntiforgery af, CancellationToken ct) =>
    {
        var antiforgeryError = await ValidateAntiforgeryAsync(c, af);
        if (antiforgeryError is not null) return antiforgeryError;

        using var reader = new StreamReader(c.Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);
        var (statusCode, body) = await cl.BuildCompanyProfileDraftAsync(rawBody, ct);
        c.Response.Headers.CacheControl = "no-store";
        return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
    }),
};
if (!hasEntraIdConfig)
{
    foreach (var route in catalogRoutes)
    {
        route.AllowAnonymous();
        route.DisableAntiforgery();
    }
}

var alertChannelSettingsRoute = app.MapGet("/api/settings/notifications", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetAlertChannelSettingsAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
var alertChannelSettingsAuditRoute = app.MapGet("/api/settings/notifications/audit", async (HttpContext context, IJourneyMonitorApiClient client, CancellationToken cancellationToken) =>
{
    var (statusCode, body) = await client.GetAlertChannelSettingsAuditAsync(cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
// Unlike the other Settings reads (thresholds/model names), this response carries recipient email
// addresses plus SMTP host/username/sender metadata - once real auth exists it must require it,
// same reasoning as doctorReportRoute above.
if (!hasEntraIdConfig)
{
    alertChannelSettingsRoute.AllowAnonymous();
    alertChannelSettingsAuditRoute.AllowAnonymous();
}

// Ad-hoc token test for the Settings tab's model picker - nothing is persisted, but it still
// forwards a caller-supplied token to a real outbound Copilot API call, so it gets the same
// antiforgery validation as the settings PUTs below (still requires an authenticated session once
// Entra is configured, like every other route that isn't explicitly .AllowAnonymous()'d in every
// mode).
var listLlmClientModelsRoute = app.MapPost("/api/settings/llm-client/models", async (HttpContext context, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);
    var (statusCode, body) = await client.ListLlmClientModelsAsync(rawBody, cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
if (!hasEntraIdConfig)
{
    listLlmClientModelsRoute.AllowAnonymous();
    listLlmClientModelsRoute.DisableAntiforgery();
}

// app.js fetches this once and sends it back as X-CSRF-TOKEN on the settings PUT below - a plain
// GET needs no antiforgery token itself, it only issues one for later use.
app.MapGet("/api/antiforgery-token", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { token = tokens.RequestToken });
}).AllowAnonymous();

// UseAntiforgery() only validates endpoints that bind a [FromForm] parameter - these routes read
// the body manually as raw JSON, so the middleware never actually checks anything for them on its
// own. Call IAntiforgery.ValidateRequestAsync explicitly instead (the documented pattern for
// Minimal API endpoints that don't bind form data - see "Antiforgery with Minimal APIs"). Only
// enforced when Entra is configured; the local no-Entra branch already calls
// .DisableAntiforgery() below and has no session cookie for a cross-site request to ride on.
async Task<IResult?> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
{
    if (!hasEntraIdConfig) return null;
    try
    {
        await antiforgery.ValidateRequestAsync(context);
        return null;
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Json(new { error = "Antiforgery token validation failed." }, statusCode: StatusCodes.Status400BadRequest);
    }
}

var upsertLatencySettingRoute = app.MapPut("/api/settings/latency/{domain}", async (HttpContext context, string domain, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);

    System.Text.Json.Nodes.JsonNode? parsed;
    try
    {
        parsed = System.Text.Json.Nodes.JsonNode.Parse(rawBody);
    }
    catch (System.Text.Json.JsonException)
    {
        parsed = null;
    }

    // No special-casing an empty/"null" body into "{}" - that would silently clear every field
    // of an existing override (all 3 become null). Anything that isn't a real JSON object
    // (missing body, literal "null", an array, a number, ...) is a 400, matching what the
    // direct Function already rejects.
    if (parsed is not System.Text.Json.Nodes.JsonObject node)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { error = "Request body must be a valid JSON object." }, statusCode: StatusCodes.Status400BadRequest);
    }

    // Any signed-in user may change a setting (no authorization check), but when Entra ID auth
    // is configured the authenticated identity always wins over whatever "changedBy" the client
    // sent, so the audit trail cannot be spoofed from the browser.
    if (context.User.Identity?.IsAuthenticated == true)
    {
        node["changedBy"] = context.User.Identity.Name;
    }

    var (statusCode, body) = await client.UpsertLatencySpikeSettingAsync(domain, node.ToJsonString(), cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
// A mutating write to the audit trail must not be reachable anonymously once real auth exists -
// only the local no-Entra dev mode (where every other endpoint is already anonymous) allows it.
if (!hasEntraIdConfig)
{
    upsertLatencySettingRoute.AllowAnonymous();
    // No session cookie exists in this mode, so there is nothing for a cross-site request to
    // ride on - antiforgery validation would just be friction with no CSRF risk to mitigate.
    upsertLatencySettingRoute.DisableAntiforgery();
}
// When Entra IS configured, antiforgery validation stays ON (the UseAntiforgery() middleware's
// default for PUT) - app.js must fetch GET /api/antiforgery-token and send it as X-CSRF-TOKEN,
// or this write is rejected even from an authenticated session (that is the point: a real
// session cookie alone must not be enough to drive this mutation from a third-party page).

var upsertLlmClientSettingRoute = app.MapPut("/api/settings/llm-client", async (HttpContext context, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);

    System.Text.Json.Nodes.JsonNode? parsed;
    try
    {
        parsed = System.Text.Json.Nodes.JsonNode.Parse(rawBody);
    }
    catch (System.Text.Json.JsonException)
    {
        parsed = null;
    }

    if (parsed is not System.Text.Json.Nodes.JsonObject node)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { error = "Request body must be a valid JSON object." }, statusCode: StatusCodes.Status400BadRequest);
    }

    // Same spoofing protection as the latency settings PUT above.
    if (context.User.Identity?.IsAuthenticated == true)
    {
        node["changedBy"] = context.User.Identity.Name;
    }

    var (statusCode, body) = await client.UpsertLlmClientSettingAsync(node.ToJsonString(), cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
// Same reasoning as upsertLatencySettingRoute: this is, if anything, a MORE sensitive write (a
// real credential, not just a threshold), so it gets the same anonymous-only-without-Entra +
// antiforgery-required-with-Entra treatment, never looser.
if (!hasEntraIdConfig)
{
    upsertLlmClientSettingRoute.AllowAnonymous();
    upsertLlmClientSettingRoute.DisableAntiforgery();
}

var upsertSloDefinitionRoute = app.MapPut("/api/settings/slo", async (HttpContext context, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);

    System.Text.Json.Nodes.JsonNode? parsed;
    try
    {
        parsed = System.Text.Json.Nodes.JsonNode.Parse(rawBody);
    }
    catch (System.Text.Json.JsonException)
    {
        parsed = null;
    }

    if (parsed is not System.Text.Json.Nodes.JsonObject node)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { error = "Request body must be a valid JSON object." }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        node["changedBy"] = context.User.Identity.Name;
    }

    var (statusCode, body) = await client.UpsertSloDefinitionAsync(node.ToJsonString(), cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
if (!hasEntraIdConfig)
{
    upsertSloDefinitionRoute.AllowAnonymous();
    upsertSloDefinitionRoute.DisableAntiforgery();
}

// Same "if anything, MORE sensitive than a plain threshold" treatment as upsertLlmClientSettingRoute -
// this write can carry real credentials (SMTP password / ACS connection string / SendGrid API key /
// Teams webhook URL).
var upsertAlertChannelSettingRoute = app.MapPut("/api/settings/notifications", async (HttpContext context, IJourneyMonitorApiClient client, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);

    System.Text.Json.Nodes.JsonNode? parsed;
    try
    {
        parsed = System.Text.Json.Nodes.JsonNode.Parse(rawBody);
    }
    catch (System.Text.Json.JsonException)
    {
        parsed = null;
    }

    if (parsed is not System.Text.Json.Nodes.JsonObject node)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { error = "Request body must be a valid JSON object." }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        node["changedBy"] = context.User.Identity.Name;
    }

    var (statusCode, body) = await client.UpsertAlertChannelSettingAsync(node.ToJsonString(), cancellationToken);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(body, "application/json; charset=utf-8", statusCode: statusCode);
});
if (!hasEntraIdConfig)
{
    upsertAlertChannelSettingRoute.AllowAnonymous();
    upsertAlertChannelSettingRoute.DisableAntiforgery();
}

// --- AppInsights Token Management Proxy Routes -----------------------------------------------
// Forward AppInsights token requests from Web to Functions via JourneyMonitorApiClient

var addTokenRoute = app.MapPost("/api/appinsights/tokens", async (HttpContext context, IAppInsightsTokenService tokenService, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync(cancellationToken);
    
    try
    {
        var request = System.Text.Json.JsonDocument.Parse(body);
        var root = request.RootElement;
        
        var tenantKey = root.GetProperty("tenantKey").GetString();
        var email = root.TryGetProperty("email", out var emailEl) 
            ? emailEl.GetString() 
            : root.TryGetProperty("userEmail", out var userEmailEl) 
                ? userEmailEl.GetString() 
                : null;
        var token = root.GetProperty("token").GetString();
        var notes = root.TryGetProperty("notes", out var notesEl) ? notesEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(tenantKey) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            return Results.Json(new { success = false, error = "Missing required fields (tenantKey, email/userEmail, token)" }, statusCode: StatusCodes.Status400BadRequest);
        }

        await tokenService.SaveTokenAsync(tenantKey, email, token, notes);
        
        return Results.Json(new
        {
            success = true,
            message = $"Token saved successfully for tenant '{tenantKey}'",
            tenant = tenantKey,
            email = email,
            expiresIn = "~1 hour"
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

if (!hasEntraIdConfig)
{
    addTokenRoute.AllowAnonymous().DisableAntiforgery();
}

var getTokensRoute = app.MapGet("/api/appinsights/tokens", async (IAppInsightsTokenService tokenService) =>
{
    try
    {
        var tokens = await tokenService.GetAllTokensAsync();
        var tokenMetadata = tokens.Select(t => new
        {
            id = t.Id,
            tenantKey = t.TenantKey,
            email = t.Email,
            createdAt = t.CreatedAt,
            expiresAt = t.ExpiresAt,
            isActive = t.IsActive,
            isExpired = t.IsExpired,
            isValid = t.IsValid,
            usageCount = t.UsageCount,
            lastUsedAt = t.LastUsedAt,
            notes = t.Notes
        }).ToList();

        return Results.Json(new
        {
            success = true,
            count = tokenMetadata.Count,
            tokens = tokenMetadata
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

if (!hasEntraIdConfig)
{
    getTokensRoute.AllowAnonymous();
}

var deactivateTokenRoute = app.MapPost("/api/appinsights/tokens/{tenantKey}/deactivate", async (string tenantKey, IAppInsightsTokenService tokenService, IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
{
    var antiforgeryError = await ValidateAntiforgeryAsync(context, antiforgery);
    if (antiforgeryError is not null) return antiforgeryError;

    try
    {
        await tokenService.DeactivateTokenAsync(tenantKey);
        return Results.Json(new { success = true, message = $"Token deactivated for {tenantKey}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

if (!hasEntraIdConfig)
{
    deactivateTokenRoute.AllowAnonymous().DisableAntiforgery();
}

var healthRoute = app.MapGet("/api/appinsights/health", () => Results.Json(new
{
    status = "healthy",
    service = "AppInsights Token Manager",
    timestamp = DateTime.UtcNow
}));

if (!hasEntraIdConfig)
{
    healthRoute.AllowAnonymous();
}

app.MapRazorPages();

app.Run();
