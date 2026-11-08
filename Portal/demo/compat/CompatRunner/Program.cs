using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FakeAppInsights.Rewrite;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Kql;

// DT0 compatibility go/no-go (contract v5, prompt DT0 step 5). Renders every built-in KQL query of
// the platform the way production renders it, points it at two synthetic Kustainer databases
// through the demo app() rewriter, runs it, and writes a Markdown report.
//
// Usage: CompatRunner --kusto http://127.0.0.1:8198 --report <path> --repo <Portal dir>

var options = Args.Parse(args);
var now = DateTimeOffset.UtcNow;
now = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
var kusto = new KustoRest(options.KustoUrl);

const string DbA = "compat-a";
const string DbB = "compat-b";
const string AppIdA = "11111111-0000-4000-8000-00000000demo";
const string AppIdB = "22222222-0000-4000-8000-00000000demo";
var catalog = new StaticApplicationCatalog([(AppIdA, DbA), (AppIdB, DbB)]);

var profile = ProfileSeed.LoadArrivia(Path.Combine(options.PortalDir, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringCompanyProfile.sql"));
// The seeded incumbent declares no partners, so the dependency KPI template could not render at
// all. One synthetic partner exercises it; the report says so.
var profileWithPartner = profile with
{
    Partners = [new PartnerDefinition("compat-partner", "Compat partner (synthetic)", [new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "core-gw")])],
};

// ---------------------------------------------------------------- 1. synthetic data
var schemaCommands = File.ReadAllLines(Path.Combine(options.PortalDir, "demo", "kusto", "schema.kql"))
    .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToList();
var paths = profile.Services.SelectMany(s => s.Flows).SelectMany(f => f.Steps).SelectMany(s => s.Matchers)
    .Select(m => m.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
var seeded = new Dictionary<string, Dictionary<string, long>>();
foreach (var db in new[] { DbA, DbB })
{
    await kusto.MgmtAsync("NetDefaultDB", $".drop database ['{db}'] ifexists");
    await kusto.MgmtAsync("NetDefaultDB", $".create database ['{db}'] volatile");
    foreach (var command in schemaCommands) await kusto.MgmtAsync(db, command);
    seeded[db] = new Dictionary<string, long>();
    foreach (var (table, rows) in SyntheticData.RowsPerTable)
    {
        await kusto.MgmtAsync(db, SyntheticData.SetOrAppend(table, rows, db, paths));
        seeded[db][table] = await kusto.ScalarLongAsync(db, $"{table} | count");
    }
}

// ---------------------------------------------------------------- 2. the catalog of built-in queries
var cases = new List<CompatCase>();
var provider = new KqlQueryTextProvider();
foreach (var key in provider.KnownKeys.OrderBy(k => k, StringComparer.Ordinal))
{
    cases.Add(new CompatCase($"builtin {key}", "KqlQueryTextProvider", () => LegacyPlaceholders.Fill(provider.GetByKey(key), now)));
}

var boardTemplate = Resources.Read(ScoutBoardQueryComposer.TemplateResourceName);
foreach (var kind in Enum.GetValues<ScoutBoardKind>())
{
    cases.Add(new CompatCase($"scout-board {kind} (arrivia profile)", "ScoutBoardQueryComposer",
        () => ScoutBoardQueryComposer.Compose(profile, new ScoutBoardQueryRequest(kind, now.AddHours(-4), now), boardTemplate)));
}

foreach (var service in profile.Services)
{
    foreach (var (sourceKey, aggregation) in new[] { (AppInsightsKpiSource.LatencySourceKey, KpiAggregation.P95), (AppInsightsKpiSource.ExceptionsSourceKey, KpiAggregation.Count) })
    {
        foreach (var grain in Enum.GetValues<KpiGrain>())
        {
            var definition = Kpi.Definition(service.Key, sourceKey, aggregation, grain);
            cases.Add(new CompatCase($"kpi {sourceKey} {service.Key} grain={grain}", "AppInsightsKpiSource.RenderKql",
                () => AppInsightsKpiSource.RenderKql(AppInsightsKpiSource.LoadTemplate(sourceKey, service.Key), profile, definition, now.AddDays(-1), now, 20)));
        }
    }
    var dependency = Kpi.Definition(service.Key, AppInsightsKpiSource.DependencySourceKey, KpiAggregation.Count, KpiGrain.Entity);
    cases.Add(new CompatCase($"kpi {AppInsightsKpiSource.DependencySourceKey} {service.Key} grain=Entity (+1 synthetic partner)", "AppInsightsKpiSource.RenderKql",
        () => AppInsightsKpiSource.RenderKql(AppInsightsKpiSource.LoadTemplate(AppInsightsKpiSource.DependencySourceKey, service.Key), profileWithPartner, dependency, now.AddDays(-1), now, 20)));
}

// ---------------------------------------------------------------- 3. render -> resolve -> rewrite -> run
var aliasDatabase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var results = new List<CompatResult>();
foreach (var c in cases)
{
    var result = new CompatResult(c);
    results.Add(result);
    try
    {
        var rendered = c.Render();
        var resolved = KqlSourceResolver.PlaceholderPattern.Replace(rendered, m =>
        {
            var alias = m.Groups["alias"].Value;
            if (!aliasDatabase.TryGetValue(alias, out var db)) aliasDatabase[alias] = db = aliasDatabase.Count % 2 == 0 ? DbA : DbB;
            return $"/subscriptions/00000000-0000-0000-0000-00000000demo/resourceGroups/rg-demo/providers/microsoft.insights/components/{db}";
        });
        var left = Regex.Match(resolved, @"\{\{[^}]{1,64}\}\}");
        if (left.Success) throw new CompatSetupException($"unsubstituted placeholder {left.Value} (no test value for it)");

        result.Kql = AppReferenceRewriter.Rewrite(resolved, catalog);
        (result.Ok, result.Rows, result.Error) = await kusto.TryQueryAsync(DbA, result.Kql);
        if (!result.Ok) result.Cause = Causes.Classify(result.Error);

        var filtered = TimespanFilter.Apply(result.Kql, now.AddDays(-1), now);
        result.FilteredReferences = Regex.Matches(filtered, @"timestamp between \(datetime\(").Count - Regex.Matches(result.Kql, @"timestamp between \(datetime\(").Count;
        (result.TimespanOk, _, result.TimespanError) = await kusto.TryQueryAsync(DbA, filtered);
    }
    catch (Exception ex) when (ex is CompatSetupException or KqlRewriteException or InvalidOperationException or ArgumentException or KpiSourceUnavailableException)
    {
        result.Ok = false;
        result.TimespanOk = false;
        result.Error = $"{ex.GetType().Name}: {ex.Message}";
        result.Cause = ex is CompatSetupException ? "harness: placeholder without a test value" : "render/rewrite failed before reaching the engine";
    }
    Console.WriteLine($"{(result.Ok ? "OK  " : "FAIL")} {c.Name}{(result.Ok ? "" : " -> " + Trim(result.Error, 160))}");
}

// ---------------------------------------------------------------- 4. harness self-checks
// A harness that cannot fail proves nothing: each of these must be reported as a failure.
var selfChecks = new List<(string Name, bool Passed)>();
var (badColumnOk, _, _) = await kusto.TryQueryAsync(DbA, "requests | where noSuchColumn_ > 1 | count");
selfChecks.Add(("an unknown column is reported as a failure by the engine", !badColumnOk));
var (badSyntaxOk, _, _) = await kusto.TryQueryAsync(DbA, "requests | wher success");
selfChecks.Add(("a syntax error is reported as a failure by the engine", !badSyntaxOk));
var unknownAppRejected = false;
try { AppReferenceRewriter.Rewrite("app('cruise-core-ovs-ai-prod').requests | take 1", catalog); }
catch (UnknownApplicationException) { unknownAppRejected = true; }
selfChecks.Add(("an app() the catalog does not know is rejected by the rewriter", unknownAppRejected));
var (crossDbOk, crossDbRows, _) = await kusto.TryQueryAsync(DbA, $"union database('{DbA}').requests, database('{DbB}').requests | count");
selfChecks.Add(("a cross-database union reads both databases", crossDbOk && crossDbRows == 1));

// ---------------------------------------------------------------- 5. report
var ok = results.Count(r => r.Ok);
var rate = results.Count == 0 ? 0 : 100.0 * ok / results.Count;
var report = new StringBuilder();
report.AppendLine("# DT0 compatibility report — platform KQL on Kustainer (go / no-go)");
report.AppendLine();
report.AppendLine($"Generated by `Portal/demo/compat/run-compat.ps1` at {now:yyyy-MM-ddTHH:mm}Z against `{options.KustoUrl}` (Kustainer `mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest`, engine {await kusto.VersionAsync()}).");
report.AppendLine();
report.AppendLine("| Metric | Value |");
report.AppendLine("|---|---|");
report.AppendLine($"| Queries | {results.Count} |");
report.AppendLine($"| OK | {ok} |");
report.AppendLine($"| Failed | {results.Count - ok} |");
report.AppendLine($"| Pass rate | {rate.ToString("0.0", CultureInfo.InvariantCulture)} % |");
report.AppendLine($"| With `timespan` filter (P1D) also OK | {results.Count(r => r.TimespanOk)} / {results.Count} |");
report.AppendLine($"| Harness self-checks passed | {selfChecks.Count(s => s.Passed)} / {selfChecks.Count} |");
report.AppendLine($"| **Verdict** (criterion ≥ 95 %) | **{(rate >= 95 && selfChecks.All(s => s.Passed) ? "GO" : "NO-GO")}** |");
report.AppendLine();
report.AppendLine("## Method");
report.AppendLine();
report.AppendLine("1. Two volatile databases, `compat-a` and `compat-b`, get `Portal/demo/kusto/schema.kql` and synthetic rows (" +
    string.Join(", ", seeded[DbA].Select(kv => $"{kv.Key} {kv.Value}")) + " per database). URLs reuse the step matchers of the seeded `arrivia` profile so classifiers match; everything else is made up.");
report.AppendLine("2. The query catalog is the platform's own: every key of `KqlQueryTextProvider.KnownKeys` (embedded `Kql/*.txt`), the company Scout board (`ScoutBoardQueryComposer`, both kinds) and the KPI templates (`AppInsightsKpiSource.RenderKql`: latency and exceptions, grain Domain and Entity, plus the dependency template) for every service of the seeded `arrivia` profile (read from `CreateMonitoringCompanyProfile.sql`).");
report.AppendLine("3. Placeholders the platform fills at run time get test values in the platform's own format; `{{SOURCE:alias}}` becomes an ARM id `/subscriptions/…/components/compat-a|compat-b` (aliases alternate between the two databases, so cross-database unions are exercised).");
report.AppendLine("4. The text goes through `AppReferenceRewriter` (the demo `app()` → `database()` rewriter) and is executed with `POST /v1/rest/query` in `compat-a`. It is then run again after `TimespanFilter.Apply` with a 1-day window.");
report.AppendLine("5. The seeded profile declares no partners; the dependency KPI runs with one synthetic partner added in memory.");
report.AppendLine("6. **OK means the engine accepted and executed the query** (no semantic or runtime error). It does not assert the rows: several Arrivia-specific queries (journeys, dashboards, legacy Scout boards, latency spikes) filter on hosts, role names or custom dimensions that this generic data set does not reproduce, so they return 0 rows here. Producing data those filters match is the job of the company packs (DT3/DT4), not of the engine.");
report.AppendLine("7. The `alias` entry below comes from a `{{SOURCE:alias}}` inside a comment of `company-scout-board.kql.template.txt`; it is harmless.");
report.AppendLine();
report.AppendLine("Aliases resolved: " + string.Join(", ", aliasDatabase.OrderBy(kv => kv.Key).Select(kv => $"`{kv.Key}`→`{kv.Value}`")) + ".");
report.AppendLine();
report.AppendLine("## Results");
report.AppendLine();
report.AppendLine("| # | Query | Rendered by | Result | Rows | With timespan (table refs filtered) |");
report.AppendLine("|---|---|---|---|---|---|");
var index = 0;
foreach (var r in results)
{
    index++;
    report.AppendLine($"| {index} | `{r.Case.Name}` | {r.Case.Origin} | {(r.Ok ? "OK" : "**FAIL**")} | {(r.Ok ? r.Rows.ToString(CultureInfo.InvariantCulture) : "—")} | {(r.TimespanOk ? "OK" : "FAIL")} ({r.FilteredReferences}) |");
}
report.AppendLine();
    report.AppendLine("## Harness self-checks");
    report.AppendLine();
    foreach (var (name, passed) in selfChecks) report.AppendLine($"- {(passed ? "PASS" : "**FAIL**")}: {name}");
    report.AppendLine();
    report.AppendLine("## Failures");
report.AppendLine();
var failed = results.Where(r => !r.Ok || !r.TimespanOk).ToList();
if (failed.Count == 0) report.AppendLine("None.");
foreach (var r in failed)
{
    report.AppendLine($"### `{r.Case.Name}`");
    report.AppendLine();
    if (!r.Ok)
    {
        report.AppendLine($"- Cause: {r.Cause}");
        report.AppendLine($"- Error: `{Md(Trim(r.Error, 600))}`");
    }
    else
    {
        report.AppendLine($"- Plain query OK; fails only with the timespan filter: `{Md(Trim(r.TimespanError, 600))}`");
    }
    report.AppendLine();
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
await File.WriteAllTextAsync(options.ReportPath, report.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Compat: {ok}/{results.Count} OK ({rate.ToString("0.0", CultureInfo.InvariantCulture)} %). Report: {options.ReportPath}");
return rate >= 95 && selfChecks.All(s => s.Passed) ? 0 : 3;

static string Trim(string? text, int max) => text is null ? "" : text.Length <= max ? text : text[..max] + "…";
static string Md(string text) => text.Replace("`", "'").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

sealed record CompatCase(string Name, string Origin, Func<string> Render);

sealed class CompatResult(CompatCase c)
{
    public CompatCase Case { get; } = c;
    public string Kql { get; set; } = "";
    public bool Ok { get; set; }
    public long Rows { get; set; }
    public string? Error { get; set; }
    public string Cause { get; set; } = "";
    public bool TimespanOk { get; set; }
    public string? TimespanError { get; set; }
    public int FilteredReferences { get; set; }
}

sealed class CompatSetupException(string message) : Exception(message);

sealed record Args(string KustoUrl, string ReportPath, string PortalDir)
{
    public static Args Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        var portal = Get("--repo", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..")));
        return new Args(Get("--kusto", "http://127.0.0.1:8198"), Get("--report", Path.Combine(portal, "demo", "compat", "compat-report.md")), portal);
    }
}

static class Causes
{
    public static string Classify(string? error)
    {
        var e = error ?? "";
        var column = Regex.Match(e, "Failed to resolve (?:scalar expression|column or scalar expression) named '(?<n>[^']+)'");
        if (column.Success) return $"name `{column.Groups["n"].Value}` not found: a column the contract v5 §4.1 schema does not declare, or engine scoping";
        if (e.Contains("Failed to resolve table", StringComparison.OrdinalIgnoreCase)) return "table not found in the demo database";
        if (e.Contains("Unknown function", StringComparison.OrdinalIgnoreCase) || e.Contains("unknown function", StringComparison.Ordinal)) return "function not available in Kustainer";
        if (e.Contains("plugin", StringComparison.OrdinalIgnoreCase)) return "plugin not available in Kustainer";
        if (e.Contains("Syntax error", StringComparison.OrdinalIgnoreCase)) return "syntax error after rendering/rewrite";
        return "engine error (see message)";
    }
}

static class LegacyPlaceholders
{
    // Values in the format the owning service writes them (ScoutBoardService, ScoutEvidenceCollector,
    // LogsAgentTools, LatencySpikeDetectorService).
    public static string Fill(string kql, DateTimeOffset now)
    {
        var values = new Dictionary<string, string>
        {
            ["{{TIME_START}}"] = KqlDate(now.AddHours(-4)),
            ["{{TIME_END}}"] = KqlDate(now),
            ["{{BUCKET_MINUTES}}"] = "5",
            ["{{TAKE_PER_CLUB}}"] = "3",
            ["{{VERTICAL_B64}}"] = B64("cruises"),
            ["{{CLUB_FILTER_B64}}"] = B64(""),
            ["{{ENTITY_KEY_B64}}"] = B64("C-7"),
            ["{{MAX_ROWS}}"] = "50",
            ["{{LOOKBACK_DAYS}}"] = "7",
            ["{{CURRENT_EXCEPTIONS}}"] = "3",
            ["{{OPERATION_IDS}}"] = JsonSerializer.Serialize(new[] { "op-1", "op-2", "op-3" }),
            ["{{WINDOW_MINUTES}}"] = "60",
            ["{{FLOOR_MS}}"] = "1000",
        };
        foreach (var (token, value) in values) kql = kql.Replace(token, value, StringComparison.Ordinal);
        return kql;
    }

    private static string KqlDate(DateTimeOffset value) => "datetime(" + value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + ")";

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}

static class Kpi
{
    public static KpiDefinition Definition(string domain, string sourceKey, KpiAggregation aggregation, KpiGrain grain) =>
        new(0, KpiPillar.Application, $"compat-{sourceKey}", domain, "", sourceKey, KpiDirection.Increase, aggregation,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromDays(1), 20, 1, DetectionMethodKind.RobustZScore, "{}", 3, 30, true,
            DateTimeOffset.UtcNow, "compat")
        { ProfileKey = "arrivia", Grain = grain };
}

static class Resources
{
    public static string Read(string name)
    {
        using var stream = typeof(KqlQueryTextProvider).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

static class ProfileSeed
{
    public static CompanyProfile LoadArrivia(string sqlPath)
    {
        var sql = File.ReadAllText(sqlPath);
        var match = Regex.Match(sql, @"VALUES \(N'arrivia', N'(?<json>\{.*?\n\})',", RegexOptions.Singleline);
        if (!match.Success) throw new InvalidOperationException("arrivia seed not found in " + sqlPath);
        var json = match.Groups["json"].Value.Replace("''", "'");
        return JsonSerializer.Deserialize<CompanyProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("arrivia seed did not deserialize");
    }
}

static class SyntheticData
{
    public static readonly (string Table, int Rows)[] RowsPerTable =
    [
        ("requests", 4000), ("dependencies", 3000), ("exceptions", 1500), ("traces", 1000), ("pageViews", 300), ("customEvents", 200),
    ];

    /// <summary>A <c>.set-or-append</c> that fills every §4.1 column of <paramref name="table"/> from <c>range</c>.</summary>
    public static string SetOrAppend(string table, int rows, string database, IReadOnlyList<string> paths)
    {
        var pathList = string.Join(",", paths.Select(p => "\"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
        var columns = AppInsightsSchema.Tables[table].Select(c => $"{c.Name}={Expression(table, c, rows, database)}");
        return $".set-or-append {table} <| range i from 1 to {rows} step 1" +
               $" | extend p_=tostring(dynamic([{pathList}])[i % {paths.Count}])" +
               " | extend h_=tostring(dynamic([\"club7.demo.example\",\"club12.demo.example\",\"www.demo-travel.example\"])[i % 3])" +
               $" | project {string.Join(", ", columns)}";
    }

    private static string Expression(string table, SchemaColumn c, int rows, string database) => c.Name switch
    {
        "timestamp" => $"datetime_add('second', -toint(i * 691200 / {rows}), now())",
        "url" => "strcat(\"https://\", h_, \"/\", trim_start(\"/\", p_), \"?cobrand=C-\", tostring(i % 40))",
        "name" when table is "requests" or "pageViews" => "strcat(iff(i % 3 == 0, \"POST \", \"GET \"), \"/\", trim_start(\"/\", p_))",
        "name" when table == "dependencies" => "strcat(\"POST /core/\", tostring(i % 5))",
        "name" when table == "customEvents" => "tostring(dynamic([\"BookingCompleted\",\"SearchPerformed\"])[i % 2])",
        "resultCode" => "iff(i % 23 == 0, \"500\", \"200\")",
        "success" => "i % 23 != 0",
        "duration" => "toreal(20 + (i * 7919) % 4000)",
        "operation_Id" => "strcat(\"op-\", tostring(i % 2000))",
        "operation_ParentId" => "strcat(\"op-\", tostring(i % 2000))",
        "operation_Name" => "strcat(\"GET /\", trim_start(\"/\", p_))",
        "id" or "itemId" => "tostring(new_guid())",
        "itemCount" => "toint(1)",
        "severityLevel" => "toint(i % 4)",
        "customDimensions" => "bag_pack(\"VacationClubId\", strcat(\"C-\", tostring(i % 40)), \"ClubId\", strcat(\"C-\", tostring(i % 40)), \"Vertical\", tostring(dynamic([\"cruises\",\"hotels\",\"cars\",\"air\"])[i % 4]), \"Host\", h_)",
        "customMeasurements" => "bag_pack(\"Amount\", toreal(i % 500))",
        "details" => "dynamic([{\"message\":\"synthetic\"}])",
        "type" when table == "exceptions" => "tostring(dynamic([\"System.TimeoutException\",\"System.Net.Http.HttpRequestException\",\"System.NullReferenceException\"])[i % 3])",
        "type" when table == "dependencies" => "tostring(dynamic([\"HTTP\",\"SQL\",\"Azure blob\"])[i % 3])",
        "outerType" or "innermostType" => "tostring(dynamic([\"System.TimeoutException\",\"System.Net.Http.HttpRequestException\",\"System.NullReferenceException\"])[i % 3])",
        "message" or "outerMessage" or "innermostMessage" => "strcat(\"Synthetic failure \", tostring(i % 17))",
        "problemId" => "strcat(\"Demo.Service.Execute \", tostring(i % 5))",
        "method" or "outerMethod" or "innermostMethod" => "\"Demo.Service.Execute\"",
        "assembly" or "outerAssembly" or "innermostAssembly" => "\"Demo.Assembly, Version=1.0.0.0\"",
        "handledAt" => "\"UserCode\"",
        "target" => "tostring(dynamic([\"core-gw.demo.internal\",\"payments.demo.internal\",\"sql-demo\"])[i % 3])",
        "data" => "strcat(\"https://core-gw.demo.internal/api/\", tostring(i % 9))",
        "cloud_RoleName" => "tostring(dynamic([\"cruise-core\",\"cruise-api\",\"sor-frontend\"])[i % 3])",
        "cloud_RoleInstance" => "strcat(\"instance-\", tostring(i % 4))",
        "session_Id" => "strcat(\"s-\", tostring(i % 300))",
        "user_Id" or "user_AuthenticatedId" => "strcat(\"u-\", tostring(i % 500))",
        "client_Type" => "\"PC\"",
        "client_Browser" => "\"Chrome 130.0\"",
        "client_OS" => "\"Windows 10\"",
        "client_IP" => "\"0.0.0.0\"",
        "client_CountryOrRegion" => "\"Demo Land\"",
        "appId" => $"\"{database}\"",
        "appName" => $"\"{database}\"",
        "iKey" => "\"00000000-0000-0000-0000-000000000000\"",
        "sdkVersion" => "\"dotnetc:2.22.0\"",
        "itemType" => $"\"{table.TrimEnd('s')}\"",
        "performanceBucket" => "\"250ms-500ms\"",
        _ when c.Type == "dynamic" => "dynamic({})",
        _ when c.Type == "string" => $"strcat(\"{c.Name}-\", tostring(i % 7))",
        _ => throw new InvalidOperationException($"No generator for {table}.{c.Name}:{c.Type}"),
    };
}

sealed class KustoRest(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };

    public async Task MgmtAsync(string db, string csl)
    {
        using var response = await _http.PostAsJsonAsync("v1/rest/mgmt", new { db, csl });
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kusto mgmt failed ({(int)response.StatusCode}) for '{Short(csl)}': {ErrorText(await response.Content.ReadAsStringAsync())}");
        }
    }

    public async Task<string> VersionAsync()
    {
        using var response = await _http.PostAsJsonAsync("v1/rest/mgmt", new { db = "NetDefaultDB", csl = ".show version" });
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var table = json.RootElement.GetProperty("Tables")[0];
        var columns = table.GetProperty("Columns").EnumerateArray().Select(c => c.GetProperty("ColumnName").GetString()).ToList();
        var row = table.GetProperty("Rows")[0];
        var i = columns.IndexOf("ProductVersion");
        return i >= 0 ? row[i].ToString() : row[0].ToString();
    }

    public async Task<long> ScalarLongAsync(string db, string csl)
    {
        var (ok, _, error, first) = await QueryAsync(db, csl);
        if (!ok) throw new InvalidOperationException(error);
        return first;
    }

    public async Task<(bool Ok, long Rows, string? Error)> TryQueryAsync(string db, string csl)
    {
        var (ok, rows, error, _) = await QueryAsync(db, csl);
        return (ok, rows, error);
    }

    private async Task<(bool Ok, long Rows, string? Error, long FirstValue)> QueryAsync(string db, string csl)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/rest/query", new { db, csl });
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return (false, 0, ErrorText(text), 0);

            using var json = JsonDocument.Parse(text);
            var primary = json.RootElement.GetProperty("Tables")[0];
            var rows = primary.GetProperty("Rows");
            long first = 0;
            if (rows.GetArrayLength() > 0 && rows[0].GetArrayLength() > 0 && rows[0][0].ValueKind == JsonValueKind.Number) first = rows[0][0].GetInt64();
            var exceptions = json.RootElement.GetProperty("Tables").EnumerateArray()
                .SelectMany(t => t.GetProperty("Rows").EnumerateArray())
                .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("Exceptions", out _))
                .Select(r => r.GetProperty("Exceptions").ToString()).ToList();
            return exceptions.Count > 0 ? (false, 0, string.Join(" ", exceptions), 0) : (true, rows.GetArrayLength(), null, first);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, 0, $"{ex.GetType().Name}: {ex.Message}", 0);
        }
    }

    private static string ErrorText(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.GetProperty("error");
            if (error.TryGetProperty("@message", out var m)) return m.GetString() ?? body;
            if (error.TryGetProperty("message", out var m2)) return m2.GetString() ?? body;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        return body;
    }

    private static string Short(string csl) => csl.Length <= 120 ? csl : csl[..120] + "…";
}
