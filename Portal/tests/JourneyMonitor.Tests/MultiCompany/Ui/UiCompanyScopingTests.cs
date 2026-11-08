using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace JourneyMonitor.Tests.MultiCompany.Ui;

/// <summary>
/// Contract v6 §2 (UI) and §6 as static checks over the portal's browser assets: every
/// company-scoped read goes through <c>withActiveCompany</c>, the HTML carries no fixed incumbent
/// vocabulary (the filters and columns come from the active profile), and the company theme is a
/// set of CSS custom properties consumed by the header, tab nav, primary buttons and banner.
/// </summary>
public class UiCompanyScopingTests
{
    /// <summary>Reads that do not depend on the company (platform-wide settings and plumbing).</summary>
    private static readonly string[] GlobalReads =
    [
        "/api/settings/latency",
        "/api/settings/llm-client",
        "/api/settings/slo",
        "/api/settings/notifications",
        "/api/settings/company-profiles",
        "/api/settings/kql-audit",
        "/api/settings/telemetry-sources-audit",
        "/api/antiforgery-token",
        "/api/appinsights/tokens",
        "/api/agent-budgets",
        "/api/demo-mode",
        "/api/onboarding",
    ];

    /// <summary>Contract v6 §4–§5 reads (and the company-dependent settings) that must carry profileKey.</summary>
    private static readonly string[] ScopedReads =
    [
        "/api/profile-journeys",
        "/api/entities",
        "/api/exceptions",
        "/api/overview",
        "/api/analytics/metrics",
        "/api/health/pillars",
        "/api/health/kpis/",
        "/api/triage/investigations",
        "/api/monkeys",
        "/api/scouts/",
        "/api/scout/packages/",
        "/api/inspector/queue",
        "/api/inspector/toasts",
        "/api/inspector/cases/",
        "/api/findings/",
        "/api/knowledge/documents",
        "/api/knowledge/search",
        "/api/knowledge/document?",
        "/api/daemons/activity",
        "/api/daemons",
        "/api/club-inventory",
        "/api/hotels/business-health",
        "/api/settings/kpis",
        "/api/settings/telemetry-sources",
        "/api/settings/kql",
    ];

    private static readonly string[] WriteHelpers = ["settingsWrite(", "companyWrite(", "postTokenJson("];
    private static readonly string[] RequestCalls = ["fetch(", "fetchJson(", "fetchJsonPayload("];

    private static readonly Regex ApiLiteral = new(@"(?<q>['`])(?<path>/api/[^'`]*)", RegexOptions.CultureInvariant);

    [Fact]
    public void Ui_EveryReadCarriesActiveCompany()
    {
        var problems = new List<string>();
        var wrapped = new List<string>();

        foreach (var file in new[] { "app.js", "daemons.js" })
        {
            var source = ReadAsset(file);
            foreach (Match match in ApiLiteral.Matches(source))
            {
                var path = match.Groups["path"].Value;
                var lineStart = source.LastIndexOf('\n', match.Index) + 1;
                var linePrefix = source[lineStart..match.Index];
                var trimmedLine = linePrefix.TrimStart();

                // Comments and log messages name endpoints without requesting them.
                if (trimmedLine.StartsWith('*')
                    || trimmedLine.StartsWith("//", StringComparison.Ordinal)
                    || trimmedLine.StartsWith("/*", StringComparison.Ordinal)
                    || linePrefix.Contains("console.", StringComparison.Ordinal))
                {
                    continue;
                }

                var before = source[..match.Index].TrimEnd();
                if (before.EndsWith("withActiveCompany(", StringComparison.Ordinal))
                {
                    wrapped.Add(path);
                    continue;
                }

                if (WriteHelpers.Any(h => before.EndsWith(h, StringComparison.Ordinal))) continue;
                if (RequestCalls.Any(c => before.EndsWith(c, StringComparison.Ordinal))
                    && CallArguments(source, match.Index).Contains("method:", StringComparison.Ordinal))
                {
                    continue; // a write: the body names the company where it matters
                }

                // The most specific prefix decides: /api/settings/kql-audit is global, /api/settings/kql is not.
                var longestGlobal = GlobalReads.Where(g => path.StartsWith(g, StringComparison.Ordinal)).Select(g => g.Length).DefaultIfEmpty(0).Max();
                var longestScoped = ScopedReads.Where(s => path.StartsWith(s, StringComparison.Ordinal)).Select(s => s.Length).DefaultIfEmpty(0).Max();
                if (longestGlobal > longestScoped) continue;

                problems.Add($"{file}:{LineNumber(source, match.Index)} reads '{path}' without withActiveCompany().");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var missing = ScopedReads
            .Where(prefix => !wrapped.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
        Assert.True(missing.Count == 0, "Scoped reads never requested through withActiveCompany(): " + string.Join(", ", missing));

        var app = ReadAsset("app.js");
        Assert.DoesNotContain("/api/clubs", app, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/journeys/db", app, StringComparison.Ordinal);
        Assert.Matches(@"function withActiveCompany\(url\)[\s\S]{0,200}profileKey=\$\{encodeURIComponent\(key\)\}", app);
    }

    [Fact]
    public void Ui_CompanySwitchReloadsEverything()
    {
        var app = ReadAsset("app.js");
        var refresh = FunctionBody(app, "async function refreshForCompanyChange()");
        Assert.Contains("resetCompanyScopedState();", refresh, StringComparison.Ordinal);
        Assert.Contains("loadActiveProfile();", refresh, StringComparison.Ordinal);
        Assert.Contains("loadAllData()", refresh, StringComparison.Ordinal);
        Assert.Contains("loadClubsForDomain(", refresh, StringComparison.Ordinal);
        Assert.Contains("renderAllTabs();", refresh, StringComparison.Ordinal);
        Assert.Contains("reloadVisibleLazyTab();", refresh, StringComparison.Ordinal);

        var reset = FunctionBody(app, "function resetCompanyScopedState()");
        foreach (var cleared in new[]
                 {
                     "state.journeys = []", "state.exceptions = []", "state.overview = null", "state.dashboardMetrics = []",
                     "state.knowledgeCorpus = null", "state.clubInventoryLoaded = false", "state.filters.club = ''",
                     "rwWatch.focus = null", "onCompanyChange",
                 })
        {
            Assert.Contains(cleared, reset, StringComparison.Ordinal);
        }

        var load = FunctionBody(app, "async function loadActiveProfile()");
        Assert.Contains("applyCompanyTheme(", load, StringComparison.Ordinal);
        Assert.Contains("renderDomainFilter();", load, StringComparison.Ordinal);

        // The journeys tab reads the generic per-company journeys for every company (contract v6 §4.1).
        Assert.Contains("withActiveCompany(`/api/profile-journeys?", app, StringComparison.Ordinal);
        Assert.Contains("withActiveCompany(`/api/profile-journeys/${encodeURIComponent(journey.id)}`)", app, StringComparison.Ordinal);
        Assert.Contains("withActiveCompany(`/api/entities?", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_NoHardcodedArriviaOptions()
    {
        var html = ReadAsset("index.html");

        var fixedOptions = Regex.Matches(
            html,
            @"<option\b[^>]*value=""(hotels|cruises|cars|air|flights)""|<option\b[^>]*>\s*(Hotels?|Cruises?|Cars?|Air|Flights?)\s*</option>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(fixedOptions.Count == 0, "Fixed incumbent options in index.html: " + string.Join(", ", fixedOptions.Select(m => m.Value)));

        foreach (var phrase in new[]
                 {
                     "across Hotels and Cruises", "T-Mobile", "Hotels ·", "data-dashboard-chart=\"hotels:",
                     "data-dashboard-chart=\"cruises:", "Brand/Club", "Booking ID", "Select a domain first", "JNY-AIR",
                     "cruise-core",
                 })
        {
            Assert.DoesNotContain(phrase, html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Matches(@"<select id=""filter-domain""[^>]*>\s*<option value=""all"">All</option>\s*</select>", html);
        Assert.Contains("id=\"filter-domain-label\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"filter-club-label\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"journeys-thead\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"analytics-charts\"", html, StringComparison.Ordinal);

        var app = ReadAsset("app.js");
        Assert.DoesNotContain("JOURNEY_DOMAINS", app, StringComparison.Ordinal);
        Assert.Contains("function profileServices()", app, StringComparison.Ordinal);
        Assert.Contains("function profileLabels()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_ThemeVariablesDefined()
    {
        var css = ReadAsset("styles.css");
        var root = Regex.Match(css, @":root\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant).Groups["body"].Value;
        Assert.Matches(@"--company-primary\s*:\s*var\(--navy-900\)", root);
        Assert.Matches(@"--company-accent\s*:\s*var\(--teal-500\)", root);
        Assert.Contains("--company-header-bg", root, StringComparison.Ordinal);
        Assert.Contains("--company-header-fg", root, StringComparison.Ordinal);

        Assert.Matches(@"\.app-header \{[^}]*background: var\(--company-header-bg\)", css);
        Assert.Matches(@"\.tab-nav \{[^}]*background: var\(--company-primary\)", css);
        Assert.Matches(@"\.tab-nav__item\.is-active \{[^}]*var\(--company-accent\)", css);
        Assert.Matches(@"\.btn \{[^}]*background: var\(--company-primary\)", css);
        Assert.Matches(@"\.demo-banner \{[^}]*var\(--company-accent\)", css);

        var app = ReadAsset("app.js");
        var apply = FunctionBody(app, "function applyCompanyTheme(profile)");
        foreach (var expected in new[]
                 {
                     "'--company-primary'", "'--company-accent'", "'--company-header-bg'", "'--company-header-fg'",
                     "theme.primaryColor", "theme.accentColor", "themeTextOn(primary)", "removeProperty", "company-name",
                 })
        {
            Assert.Contains(expected, apply, StringComparison.Ordinal);
        }

        Assert.Contains("shortName", FunctionBody(app, "function companyDisplayName(profile)"), StringComparison.Ordinal);
        Assert.Contains("^#[0-9a-f]{6}$", FunctionBody(app, "function isThemeColor(value)"), StringComparison.OrdinalIgnoreCase);
        var contrast = FunctionBody(app, "function themeTextOn(hex)");
        Assert.Contains("'#ffffff'", contrast, StringComparison.Ordinal);
        Assert.Contains("COMPANY_THEME_NAVY", contrast, StringComparison.Ordinal);

        Assert.Contains("id=\"company-name\"", ReadAsset("index.html"), StringComparison.Ordinal);
    }

    private static string CallArguments(string source, int literalIndex)
    {
        var depth = 1;
        for (var i = literalIndex; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[literalIndex..i];
        }

        return source[literalIndex..];
    }

    private static string FunctionBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        return source[open..];
    }

    private static int LineNumber(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Web", "wwwroot", name));

    private static string FindPortalDirectory()
    {
        if (TryFindPortal(AppContext.BaseDirectory, out var fromBinaries)) return fromBinaries;
        if (TryFindPortal(Path.GetDirectoryName(ThisFilePath()), out var fromSource)) return fromSource;
        throw new InvalidOperationException("Could not locate the Portal source tree (JourneyMonitor.slnx).");
    }

    private static bool TryFindPortal(string? startDirectory, out string portal)
    {
        portal = "";
        for (var dir = startDirectory is null ? null : new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JourneyMonitor.slnx")))
            {
                portal = dir.FullName;
                return true;
            }

            var nested = Path.Combine(dir.FullName, "Portal");
            if (File.Exists(Path.Combine(nested, "JourneyMonitor.slnx")))
            {
                portal = nested;
                return true;
            }
        }

        return false;
    }

    private static string ThisFilePath([CallerFilePath] string path = "") => path;
}
