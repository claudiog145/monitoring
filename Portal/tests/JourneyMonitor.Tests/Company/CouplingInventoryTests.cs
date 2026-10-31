using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// Architecture guardrail for the company-decoupling work.
///
/// The refactor moves Arrivia's vocabulary (verticals, stage names, club/partner identifiers,
/// T-Mobile hostnames) out of code and into a configuration profile. The failure mode of that kind
/// of refactor is not a bad design, it is erosion: two sprints later somebody writes
/// <c>if (domain == "hotels")</c> in a brand new file and nobody notices until the next customer
/// onboarding stalls. This test walks the Core and Infrastructure source trees and fails when a
/// company-specific literal appears anywhere it is not explicitly allowed.
///
/// <para><b>What counts as an offender.</b> Only company-specific <i>values</i> - brand names,
/// hostnames, subscription ids, vertical keys, stage labels, club ids. Company-flavoured
/// <i>identifiers</i> (the <c>Vertical</c> property, the <c>Club</c> noun) are inventoried in
/// coupling-allowlist.txt's header but not enforced here: they are a mechanical rename owned by
/// another track, and enforcing them would bury the behavioural coupling - the part that actually
/// breaks a second customer - under 150 cosmetic entries. <c>IcePartnerId</c> is enforced because
/// it is a customer-identity concept, not a naming preference.</para>
///
/// <para><b>Comment-only lines are skipped.</b> A comment saying "Arrivia maps Service to Vertical"
/// is documentation, not coupling. Code is checked, prose is not.</para>
/// </summary>
public sealed class CouplingInventoryTests
{
    private const string AllowListFileName = "coupling-allowlist.txt";

    private static readonly string[] ScannedProjects =
        ["JourneyMonitor.Core", "JourneyMonitor.Infrastructure"];

    private static readonly string[] ScannedExtensions = [".cs", ".txt"];

    private sealed record Rule(string Literal, Regex Pattern, string Why);

    private sealed record Finding(string Path, int Line, string Literal, string Text);

    private sealed record AllowEntry(string Path, string Literal, string Reason, int SourceLine);

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The forbidden literals. Each one is a value that is true of Arrivia and false of the next
    /// customer, so leaving it in code is a bug the day a second profile exists.
    /// </summary>
    private static readonly Rule[] Rules =
    [
        new("t-mobile", Rx(@"t-?mobile"),
            "the pilot customer's brand and its hostnames"),
        new("tmo", Rx(@"\btmo\b|AuthenticateTmo"),
            "the pilot customer's short code, including its SSO operation name"),
        new("saveonuat", Rx(@"saveonuat"),
            "a customer-specific staging hostname"),
        new("synchronytravel", Rx(@"synchronytravel"),
            "a customer-specific hostname"),
        new("travorium", Rx(@"travorium"),
            "a customer-specific hostname"),
        new("arrivia", Rx(@"arrivia"),
            "the operating company's own name"),
        new("sor-frontend", Rx(@"sor-frontend|sor-appinsights|SorFrontend|SaveOnResorts"),
            "an Arrivia-specific telemetry source alias"),
        new("vacationclub", Rx(@"vacationclub"),
            "an Arrivia-specific telemetry custom dimension"),
        new("icepartner", Rx(@"icepartner"),
            "an Arrivia (ICE) customer-identity field; belongs in EntityIdentitySource"),
        new("cobrand", Rx(@"co-?brand"),
            "Arrivia partner vocabulary"),
        new("azure-subscription-id", Rx(@"/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-"),
            "a hardcoded Azure subscription; belongs in TelemetrySource configuration"),
        new("service-name-literal", Rx("\"(hotels?|cruises?|cars?|air|flights?)\""),
            "a hardcoded vertical key; belongs in CompanyProfile.Services"),
        new("step-name-literal", Rx("\"\\d\\d [A-Z][^\"]*\""),
            "a hardcoded funnel stage label; belongs in StepDefinition"),
        new("entity-id-literal", Rx("\"(15639|308)\""),
            "a hardcoded club/partner id; belongs in configuration, never in code"),
    ];

    private static readonly Lazy<ScanResult> Scan = new(RunScan, isThreadSafe: true);

    // ---------------------------------------------------------------------------------------
    // The guardrail
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SourceTreesAreDiscoverableAndNonEmpty()
    {
        var scan = Scan.Value;

        // A guardrail that cannot find the code it guards must shout, not shrug. Silently
        // scanning zero files is the one outcome that is worse than not having this test.
        Assert.True(scan.ScannedFileCount >= 100,
            $"Only {scan.ScannedFileCount} source file(s) were scanned under '{scan.SourceRoot}'. " +
            "The guardrail failed to locate the production trees, so it is not guarding anything. " +
            "Fix the root discovery in CouplingInventoryTests before trusting any other result here.");

        foreach (var project in ScannedProjects)
        {
            Assert.True(scan.FilesByProject.TryGetValue(project, out var count) && count > 20,
                $"Expected to scan the {project} source tree, found {(scan.FilesByProject.TryGetValue(project, out var c) ? c : 0)} file(s). " +
                $"Resolved source root: '{scan.SourceRoot}'.");
        }
    }

    [Fact]
    public void NoCompanySpecificLiteralOutsideTheCompanyProfile()
    {
        var scan = Scan.Value;
        var allowed = LoadAllowList()
            .Select(e => Key(e.Path, e.Literal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = scan.Findings
            .Where(f => !allowed.Contains(Key(f.Path, f.Literal)))
            .GroupBy(f => Key(f.Path, f.Literal))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (offenders.Count == 0) return;

        var message = new StringBuilder();
        message.AppendLine($"{offenders.Count} company-specific literal(s) found outside the places where they are allowed.");
        message.AppendLine();
        message.AppendLine("Each line below is file:line, the rule that fired, and the offending source line:");
        message.AppendLine();

        foreach (var group in offenders)
        {
            var first = group.OrderBy(f => f.Line).First();
            var rule = Rules.First(r => r.Literal == first.Literal);
            var extra = group.Count() > 1 ? $" (+{group.Count() - 1} more occurrence(s) in this file)" : "";
            message.AppendLine($"  {first.Path}:{first.Line}  [{first.Literal}]{extra}");
            message.AppendLine($"      {Truncate(first.Text, 160)}");
            message.AppendLine($"      why it is forbidden: {rule.Why}");
        }

        message.AppendLine();
        message.AppendLine("Do ONE of these:");
        message.AppendLine("  1. PREFERRED - move the value into the company profile (JourneyMonitor.Core/Company/CompanyModel.cs)");
        message.AppendLine("     and read it from CompanyProfile at run time, so a second customer only needs a new profile row.");
        message.AppendLine("  2. If the literal genuinely belongs in Core/Company/, in a file whose name contains 'Arrivia',");
        message.AppendLine("     or in a .txt under Infrastructure/Kql/, move it there - those locations are exempt by design.");
        message.AppendLine("  3. LAST RESORT - add a justified line to");
        message.AppendLine($"     Portal/tests/JourneyMonitor.Tests/Company/{AllowListFileName}");
        message.AppendLine("     using exactly this format (path|literal|reason). Copy-paste ready:");
        message.AppendLine();
        foreach (var group in offenders)
        {
            var first = group.OrderBy(f => f.Line).First();
            message.AppendLine($"{first.Path}|{first.Literal}|TODO justify - first seen at line {first.Line}");
        }
        message.AppendLine();
        message.AppendLine("Adding an allow-list line without a real reason is how this refactor dies. The list is");
        message.AppendLine("meant to shrink, and every entry is a customer-onboarding blocker someone still owes.");

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// The allow-list is an inventory of debt, so it has to stay truthful. An entry whose literal is
    /// no longer in that file means the debt was paid and nobody deleted the line - which quietly
    /// re-opens a hole, because the entry keeps covering the path for any future offender.
    /// </summary>
    [Fact]
    public void AllowListHasNoStaleEntries()
    {
        var scan = Scan.Value;
        var live = scan.Findings
            .Select(f => Key(f.Path, f.Literal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = LoadAllowList()
            .Where(e => !live.Contains(Key(e.Path, e.Literal)))
            .ToList();

        if (stale.Count == 0) return;

        var message = new StringBuilder();
        message.AppendLine($"{stale.Count} stale entry/entries in {AllowListFileName}: the literal is no longer in that file.");
        message.AppendLine("Delete the line - the debt is paid and the allow-list must shrink to show it.");
        message.AppendLine("(If the file was renamed or the literal moved, update the entry instead of deleting it.)");
        message.AppendLine();
        foreach (var entry in stale.OrderBy(e => e.SourceLine))
        {
            message.AppendLine($"  {AllowListFileName}:{entry.SourceLine}  ->  {entry.Path}|{entry.Literal}|{entry.Reason}");
        }

        Assert.Fail(message.ToString());
    }

    [Fact]
    public void AllowListEntriesAreWellFormed()
    {
        var entries = LoadAllowList();
        Assert.NotEmpty(entries);

        var duplicates = entries
            .GroupBy(e => Key(e.Path, e.Literal), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate allow-list entries: {string.Join(", ", duplicates)}");

        var knownLiterals = Rules.Select(r => r.Literal).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = entries.Where(e => !knownLiterals.Contains(e.Literal)).ToList();
        Assert.True(unknown.Count == 0,
            "Allow-list entries naming a rule that does not exist (typo, or the rule was removed): " +
            string.Join(", ", unknown.Select(e => $"{AllowListFileName}:{e.SourceLine} '{e.Literal}'")));

        var unjustified = entries.Where(e => e.Reason.Trim().Length < 10).ToList();
        Assert.True(unjustified.Count == 0,
            "Allow-list entries without a real reason: " +
            string.Join(", ", unjustified.Select(e => $"{AllowListFileName}:{e.SourceLine}")));
    }

    // ---------------------------------------------------------------------------------------
    // Proof that the detector actually detects. A guardrail nobody has seen fail is a decoration.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ScannerCatchesAFreshlyIntroducedLiteralInANewFile()
    {
        const string injected = """
            public static class Regression
            {
                public static bool IsPilot(string domain, string host) =>
                    domain == "hotels" && host.EndsWith("stays.tmobiletravel.com");

                public const string Stage = "04 Hotel Details";
                public const string Club = "15639";
            }
            """;

        var findings = ScanText("JourneyMonitor.Core/Somewhere/Regression.cs", injected).ToList();

        Assert.Contains(findings, f => f.Literal == "service-name-literal");
        Assert.Contains(findings, f => f.Literal == "t-mobile");
        Assert.Contains(findings, f => f.Literal == "step-name-literal");
        Assert.Contains(findings, f => f.Literal == "entity-id-literal");
    }

    [Fact]
    public void ScannerIgnoresCommentaryAndExemptLocations()
    {
        const string prose = """
            /// <summary>Arrivia maps Service to "Vertical" and Entity to "Club".</summary>
            // stays.tmobiletravel.com is the pilot host; see the profile for the real list.
            """;
        Assert.Empty(ScanText("JourneyMonitor.Core/Somewhere/Prose.cs", prose));

        Assert.True(IsExemptLocation("JourneyMonitor.Core/Company/ArrivaProfileSeed.cs"));
        Assert.True(IsExemptLocation("JourneyMonitor.Core/Company/CompanyModel.cs"));
        Assert.True(IsExemptLocation("JourneyMonitor.Infrastructure/Company/ArriviaCompanyAdapter.cs"));
        Assert.True(IsExemptLocation("JourneyMonitor.Infrastructure/Kql/hotel-latency-spikes.kql.txt"));
        Assert.False(IsExemptLocation("JourneyMonitor.Infrastructure/Kql/KqlQueryTextProvider.cs"));
        Assert.False(IsExemptLocation("JourneyMonitor.Core/Filtering/ClubFilter.cs"));
    }

    // ---------------------------------------------------------------------------------------
    // Engine
    // ---------------------------------------------------------------------------------------

    private sealed record ScanResult(
        string SourceRoot,
        int ScannedFileCount,
        IReadOnlyDictionary<string, int> FilesByProject,
        IReadOnlyList<Finding> Findings);

    private static ScanResult RunScan()
    {
        var portal = FindPortalDirectory();
        var sourceRoot = Path.Combine(portal, "src");
        if (!Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException(
                $"Resolved Portal directory '{portal}' has no 'src' folder. The coupling guardrail " +
                "cannot scan anything and refuses to pass silently.");
        }

        var findings = new List<Finding>();
        var byProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        foreach (var project in ScannedProjects)
        {
            var projectRoot = Path.Combine(sourceRoot, project);
            if (!Directory.Exists(projectRoot))
            {
                throw new InvalidOperationException(
                    $"Expected source tree '{projectRoot}' does not exist. The coupling guardrail " +
                    "refuses to pass without scanning it.");
            }

            var count = 0;
            foreach (var file in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
            {
                if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                if (IsIgnored(relative) || IsExemptLocation(relative)) continue;

                count++;
                total++;
                findings.AddRange(ScanText(relative, File.ReadAllText(file)));
            }

            byProject[project] = count;
        }

        return new ScanResult(sourceRoot, total, byProject, findings);
    }

    private static IEnumerable<Finding> ScanText(string relativePath, string text)
    {
        int[]? lineStarts = null;

        foreach (var rule in Rules)
        {
            foreach (Match match in rule.Pattern.Matches(text))
            {
                lineStarts ??= BuildLineStarts(text);
                var lineIndex = LineIndexOf(lineStarts, match.Index);
                var lineText = LineTextAt(text, lineStarts, lineIndex);
                if (IsCommentOnly(lineText)) continue;

                yield return new Finding(relativePath, lineIndex + 1, rule.Literal, lineText.Trim());
            }
        }
    }

    /// <summary>Directories and files that are build output or generated, never authored coupling.</summary>
    private static bool IsIgnored(string relativePath)
    {
        if (relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) return true;
        if (relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)) return true;

        var name = Path.GetFileName(relativePath);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Places a company-specific literal is allowed to live: the profile model and the Arrivia
    /// adapter (that is their job), and the raw KQL text, which a different track is migrating.
    /// </summary>
    private static bool IsExemptLocation(string relativePath)
    {
        if (relativePath.StartsWith("JourneyMonitor.Core/Company/", StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetFileName(relativePath).Contains("Arrivia", StringComparison.OrdinalIgnoreCase)) return true;

        return relativePath.StartsWith("JourneyMonitor.Infrastructure/Kql/", StringComparison.OrdinalIgnoreCase)
            && relativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommentOnly(string lineText)
    {
        var trimmed = lineText.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal);
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int>(text.Length / 40 + 2) { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        return [.. starts];
    }

    private static int LineIndexOf(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    private static string LineTextAt(string text, int[] lineStarts, int lineIndex)
    {
        var start = lineStarts[lineIndex];
        var end = lineIndex + 1 < lineStarts.Length ? lineStarts[lineIndex + 1] : text.Length;
        return text[start..end].TrimEnd('\n', '\r');
    }

    private static string Key(string path, string literal) => $"{path}|{literal}";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + " ...";

    // ---------------------------------------------------------------------------------------
    // Allow-list
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyList<AllowEntry> LoadAllowList()
    {
        var path = Path.Combine(
            FindPortalDirectory(), "tests", "JourneyMonitor.Tests", "Company", AllowListFileName);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The coupling allow-list '{path}' is missing. It is the inventory of current debt; " +
                "without it this guardrail cannot distinguish known debt from a new regression, so it fails.");
        }

        var entries = new List<AllowEntry>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('|', 3);
            if (parts.Length < 3)
            {
                throw new InvalidOperationException(
                    $"{AllowListFileName}:{i + 1} is malformed. Expected 'path|literal|reason', got: {line}");
            }

            entries.Add(new AllowEntry(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), i + 1));
        }

        return entries;
    }

    // ---------------------------------------------------------------------------------------
    // Locating the repository
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test assembly looking for Portal/JourneyMonitor.slnx, then falls back to
    /// the compile-time path of this file. The fallback is not belt-and-braces: this repo is built
    /// with <c>--artifacts-path C:\Temp\...</c>, which puts the binaries outside the repository
    /// entirely, so walking up from <see cref="AppContext.BaseDirectory"/> finds nothing. Throws if
    /// both fail - a guardrail that cannot find the source must fail, never silently pass.
    /// </summary>
    private static string FindPortalDirectory()
    {
        if (TryFindPortal(AppContext.BaseDirectory, out var fromBinaries)) return fromBinaries;
        if (TryFindPortal(Path.GetDirectoryName(ThisFilePath()), out var fromSource)) return fromSource;

        throw new InvalidOperationException(
            "Could not locate the Portal source tree (looked for 'JourneyMonitor.slnx' walking up from " +
            $"'{AppContext.BaseDirectory}' and from '{ThisFilePath()}'). The coupling guardrail is not " +
            "scanning anything, so it fails rather than reporting a false all-clear.");
    }

    private static bool TryFindPortal(string? startDirectory, out string portal)
    {
        portal = "";
        var dir = startDirectory is null ? null : new DirectoryInfo(startDirectory);

        while (dir is not null)
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

            dir = dir.Parent;
        }

        return false;
    }

    private static string ThisFilePath([CallerFilePath] string path = "") => path;
}
