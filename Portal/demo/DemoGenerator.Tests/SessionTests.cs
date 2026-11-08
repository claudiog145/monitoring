using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DemoGenerator.Archetypes;
using DemoGenerator.Synthesis;

namespace DemoGenerator.Tests;

// Heavy digests over every repository pack: run outside the parallel phase so the pack replay tests keep their time budget.
[CollectionDefinition(nameof(SessionTests), DisableParallelization = true)]
public sealed class SessionTestsCollection;

[Collection(nameof(SessionTests))]
public class SessionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Digests of the fixture pack written by the generator BEFORE sessions existed (commit 5d4ceb2):
    // every non-session field of every table, and the KQL of every table without its session lines.
    private const string FixtureRowsBeforeSessions = "1105E43B905B392D2AAFA21308DDC885812D5E4DB5F56B8C5B350AC3934AC8B2";
    private const string FixtureKqlBeforeSessions = "641F75961F35BA8C906B4BADE8D3F11C3795BB5CC04DD1CB3B5DE37A29B72706";

    [Fact]
    public void Sessions_DoNotChangeKpiSeries()
    {
        var withSessions = FixturePlan();
        var withoutSessions = Fixtures.Plan();

        // The sessions are really there: several requests share one.
        var requests = withSessions.Apps.SelectMany(a => ReferenceSynthesizer.Generate(a, [], KpiDigest.From, KpiDigest.To).Requests).ToList();
        Assert.True(requests.GroupBy(r => r.SessionId).Count(g => g.Count() > 1) > 20);

        Assert.Equal(FixtureRowsBeforeSessions, KpiDigest.Rows(withSessions));
        Assert.Equal(FixtureRowsBeforeSessions, KpiDigest.Rows(withoutSessions));
        Assert.Equal(FixtureKqlBeforeSessions, KpiDigest.Kql(withSessions));
        Assert.Equal(FixtureKqlBeforeSessions, KpiDigest.Kql(withoutSessions));

        // Every company pack in the repository: the same series with and without its flows.
        foreach (var dir in RepositoryPacks())
        {
            var archetype = ArchetypeLoader.Load(Path.Combine(dir, "archetype.json"));
            var flows = SessionFlows.Load(dir, archetype).Flows;
            var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000, flows);
            var plain = CompanyPlan.Build(archetype, Scales.Laptop, 1000);
            Assert.True(KpiDigest.Rows(plain) == KpiDigest.Rows(plan), $"{archetype.CompanyKey}: rows changed");
            Assert.True(KpiDigest.Kql(plain) == KpiDigest.Kql(plan), $"{archetype.CompanyKey}: KQL changed");
        }

        // Per-minute series spelled out for the web app with every scenario active at some point.
        var app = Fixtures.WebApp(withSessions);
        var before = Fixtures.WebApp(withoutSessions);
        var effects = EffectCompiler.Compile(withSessions, app, KpiDigest.Incidents(withSessions));
        var a = ReferenceSynthesizer.Generate(app, effects, KpiDigest.From, KpiDigest.To);
        var b = ReferenceSynthesizer.Generate(before, EffectCompiler.Compile(withoutSessions, before, KpiDigest.Incidents(withoutSessions)), KpiDigest.From, KpiDigest.To);
        Assert.Equal(Series(b), Series(a));
        Assert.Equal(b.CustomEvents.Select(e => (e.Timestamp, e.Name, e.Entity, e.OperationId)), a.CustomEvents.Select(e => (e.Timestamp, e.Name, e.Entity, e.OperationId)));
    }

    [Fact]
    public void Sessions_OrderedFunnelWithDropOff()
    {
        var plan = FixturePlan();
        var web = Fixtures.WebApp(plan);
        var api = Fixtures.ApiApp(plan);
        Assert.Equal([(1, 0), (1, 1)], web.Steps.Select(s => (s.SessionGroup, s.SessionPosition)));
        Assert.Equal([(2, 0)], api.Steps.Select(s => (s.SessionGroup, s.SessionPosition)));

        var from = Fixtures.Monday.AddHours(9);
        var requests = plan.Apps.SelectMany(a => ReferenceSynthesizer.Generate(a, [], from, from.AddHours(12)).Requests
            .Select(r => (Request: r, Step: a.Steps[r.StepIndex]))).ToList();
        Assert.All(requests, x => Assert.Matches("^[0-9a-f]{16}$", x.Request.SessionId));

        var sessions = requests.GroupBy(x => x.Request.SessionId).Select(g => g.OrderBy(x => x.Request.Timestamp).ToList()).ToList();
        foreach (var session in sessions)
        {
            // One user, one flow, and the steps in flow order, a minute apart (entities may differ: the rank is per flow step).
            Assert.Single(session.Select(x => x.Request.UserId).Distinct());
            Assert.Single(session.Select(x => x.Step.SessionGroup).Distinct());
            var positions = session.Select(x => x.Step.SessionPosition).ToList();
            Assert.Equal(positions.Order(), positions);
            Assert.Equal(positions.Count, positions.Distinct().Count());
            for (var i = 1; i < session.Count; i++)
                Assert.Equal(positions[i] - positions[i - 1],
                    SynthesisClock.EpochMinute(session[i].Request.Timestamp) - SynthesisClock.EpochMinute(session[i - 1].Request.Timestamp));
        }

        var purchase = sessions.Where(s => s[0].Step.SessionGroup == 1).ToList();
        var started = purchase.Count(s => s[0].Step.SessionPosition == 0);
        var completed = purchase.Count(s => s[0].Step.SessionPosition == 0 && s.Count == 2);
        var droppedAtCart = purchase.Count(s => s[0].Step.SessionPosition == 0 && s.Count == 1);
        Assert.True(completed > 100, $"only {completed} cart -> pay sessions");
        Assert.True(droppedAtCart > completed, $"{droppedAtCart} drop-offs for {completed} completions");
        // Pay has 0.2 of the traffic against 0.5 for the cart: most carts do not reach it.
        Assert.InRange(completed / (double)started, 0.15, 0.6);
        Assert.All(sessions.Where(s => s[0].Step.SessionGroup == 2), s => Assert.Single(s));

        // The KQL computes the same session from the same inputs.
        var kql = KqlSynthesisBuilder.BaseQuery(web, [], from, from.AddHours(1));
        Assert.Contains("let sesG = dynamic([1, 1]);", kql, StringComparison.Ordinal);
        Assert.Contains("let sesP = dynamic([0, 1]);", kql, StringComparison.Ordinal);
        Assert.Contains("let sesS = dynamic([0, 1]);", kql, StringComparison.Ordinal);
        Assert.Contains("| extend sesK = tolong(sesS[s])\n| sort by m asc, sesK asc, k asc\n| extend sesRank = row_number(0, m != prev(m) or sesK != prev(sesK))\n", kql, StringComparison.Ordinal);
        Assert.Contains($"| extend sesH = mix(mix(mix({web.SessionSeed.ToString(CultureInfo.InvariantCulture)} + tolong(sesG[s])) + sesAnchor) + (sesRank * tolong(sesM[s]) + tolong(sesO[s])))", kql, StringComparison.Ordinal);
        Assert.Contains("let sesM = dynamic([1, 1]);", kql, StringComparison.Ordinal);
        Assert.Contains("let sesO = dynamic([0, 0]);", kql, StringComparison.Ordinal);
        Assert.Contains($"sessionId = strcat(hex8(sesH), hex8(binary_xor(sesAnchor, {web.SessionXorMinute.ToString(CultureInfo.InvariantCulture)})))", kql, StringComparison.Ordinal);
        Assert.Equal(web.SessionSeed, api.SessionSeed);

        // Hand-computed session of a cart request continued by a pay request one minute later, whatever their entities.
        var cart = web.Steps[0];
        var pay = web.Steps[1];
        Assert.Equal(ReferenceSynthesizer.Session(web, cart, 1000, 2), ReferenceSynthesizer.Session(web, pay, 1001, 2));
        Assert.NotEqual(ReferenceSynthesizer.Session(web, cart, 1000, 2), ReferenceSynthesizer.Session(web, pay, 1001, 3));
        Assert.NotEqual(ReferenceSynthesizer.Session(web, cart, 1000, 2), ReferenceSynthesizer.Session(web, pay, 1002, 2));
        Assert.NotEqual(ReferenceSynthesizer.Session(web, cart, 1000, 2), ReferenceSynthesizer.Session(api, api.Steps[0], 1000, 2));
    }

    [Fact]
    public void Sessions_MultiStepShareAtLeastQuarter()
    {
        // Every repository pack at laptop scale, on a busy weekday morning and in the quietest stretch of a Sunday
        // night: at least a quarter of the sessions of the company's flows walk two or more steps.
        var packs = RepositoryPacks().ToList();
        Assert.NotEmpty(packs);
        var windows = new[] { (Fixtures.Monday.AddHours(10), 2), (Fixtures.Monday.AddDays(-1).AddHours(5), 3) };
        foreach (var dir in packs)
        {
            var archetype = ArchetypeLoader.Load(Path.Combine(dir, "archetype.json"));
            var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000, SessionFlows.Load(dir, archetype).Flows);
            foreach (var (from, hours) in windows)
            {
                var sessions = plan.Apps
                    .SelectMany(a => ReferenceSynthesizer.Generate(a, [], from, from.AddHours(hours)).Requests
                        .Where(r => a.Steps[r.StepIndex].SessionGroup < SessionFlows.UnclaimedGroupBase)
                        .Select(r => (r.SessionId, Position: a.Steps[r.StepIndex].SessionPosition)))
                    .GroupBy(x => x.SessionId)
                    .ToList();
                Assert.True(sessions.Count > 100, $"{archetype.CompanyKey} {from:u}: only {sessions.Count} sessions");
                var share = sessions.Count(s => s.Count() > 1) / (double)sessions.Count;
                output.WriteLine($"{archetype.CompanyKey} {from:u}: {share:P1} of {sessions.Count} sessions walk several steps");
                Assert.True(share >= 0.25, $"{archetype.CompanyKey} {from:u}: {share:P1} of {sessions.Count} sessions walk several steps");
                // A funnel: some sessions go deep, and a session never holds a flow step twice.
                Assert.Contains(sessions, s => s.Count() >= 3);
                Assert.All(sessions, s => Assert.Equal(s.Count(), s.Select(x => x.Position).Distinct().Count()));
            }
        }
    }

    [Fact]
    public void SessionFlows_FollowProfileOrderAndClassifier()
    {
        var archetype = Fixtures.Archetype();
        var flows = SessionFlows.Load(Fixtures.PackDir, archetype).Flows;
        Assert.Equal(new SessionSlot(1, 0, "checkout", "purchase", "cart"), flows.SlotOf(0));
        Assert.Equal(new SessionSlot(1, 1, "checkout", "purchase", "pay"), flows.SlotOf(1));
        Assert.Equal(new SessionSlot(2, 0, "inventory", "stock", "stock-check"), flows.SlotOf(2));

        using var copy = new PackCopy();
        // The checkout service rejects the web host: its steps are unclaimed and become one-request sessions.
        copy.Write("profile.json", copy.Read("profile.json")
            .Replace("\"hostFilters\": [\"fixtureshop.demo\"]", "\"hostFilters\": [\"other.demo\"]", StringComparison.Ordinal));
        var rejected = SessionFlows.Load(copy.Dir, archetype).Flows;
        Assert.Equal(new SessionSlot(SessionFlows.UnclaimedGroupBase, 0, null, null, null), rejected.SlotOf(0));
        Assert.Equal(new SessionSlot(SessionFlows.UnclaimedGroupBase + 1, 0, null, null, null), rejected.SlotOf(1));

        copy.Write("profile.json", "{ not json");
        var (broken, problem) = SessionFlows.Load(copy.Dir, archetype);
        Assert.NotNull(problem);
        Assert.All(broken.Slots, s => Assert.Equal(0, s.Position));
    }

    [Fact]
    public void Sessions_NeverHoldTheSameFlowStepTwice()
    {
        using var copy = new PackCopy();
        // The cart matcher now claims both web steps: cart and pay are the same flow step.
        copy.Write("profile.json", copy.Read("profile.json")
            .Replace("\"value\": \"/checkout/cart\"", "\"value\": \"/checkout/\"", StringComparison.Ordinal));
        var archetype = ArchetypeLoader.Load(Path.Combine(copy.Dir, "archetype.json"));
        var plan = CompanyPlan.Build(archetype, Scales.Laptop, 1000, SessionFlows.Load(copy.Dir, archetype).Flows);
        var web = Fixtures.WebApp(plan);
        Assert.Equal([(1, 0), (1, 0)], web.Steps.Select(s => (s.SessionGroup, s.SessionPosition)));
        Assert.Equal([0, 0], ReferenceSynthesizer.SessionRankSlots(web));
        Assert.Contains("let sesS = dynamic([0, 0]);", KqlSynthesisBuilder.BaseQuery(web, [], KpiDigest.From, KpiDigest.From.AddHours(1)), StringComparison.Ordinal);

        var requests = ReferenceSynthesizer.Generate(web, [], KpiDigest.From, KpiDigest.From.AddHours(2)).Requests;
        Assert.Contains(requests, r => r.StepIndex == 0);
        Assert.Contains(requests, r => r.StepIndex == 1);
        Assert.All(requests.GroupBy(r => r.SessionId), session => Assert.Single(session));
    }

    private static CompanyPlan FixturePlan()
    {
        var archetype = Fixtures.Archetype();
        return CompanyPlan.Build(archetype, Scales.Laptop, 1000, SessionFlows.Load(Fixtures.PackDir, archetype).Flows);
    }

    /// <summary>What a KPI reads per minute: count, failures, sum of durations and result codes, per step and entity.</summary>
    private static List<string> Series(SyntheticBatch batch) =>
        batch.Requests
            .GroupBy(r => (Minute: SynthesisClock.EpochMinute(r.Timestamp), r.Name, r.Entity))
            .OrderBy(g => g.Key.Minute).ThenBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.Entity, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{g.Key.Minute}|{g.Key.Name}|{g.Key.Entity}|{g.Count()}|{g.Count(r => !r.Success)}|{g.Sum(r => r.Duration):R}|{string.Join(",", g.Select(r => r.ResultCode).Order(StringComparer.Ordinal))}"))
            .ToList();

    /// <summary>Portal/demo/packs/* of the checkout the tests run from (none on branches without packs).</summary>
    private static IEnumerable<string> RepositoryPacks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "demo", "packs"))) dir = dir.Parent;
        if (dir is null) return [];
        return Directory.GetDirectories(Path.Combine(dir.FullName, "demo", "packs"))
            .Where(d => File.Exists(Path.Combine(d, "archetype.json")))
            .Order(StringComparer.Ordinal);
    }
}

/// <summary>Byte-level digests of everything a KPI can read: every field of every table except session_Id/user_Id.</summary>
internal static class KpiDigest
{
    public static readonly DateTimeOffset From = Fixtures.Monday.AddHours(6);
    public static readonly DateTimeOffset To = From.AddHours(6);

    /// <summary>Each scenario of the pack active for a while inside the window, so every effect kind is exercised.</summary>
    public static IReadOnlyList<ActiveIncident> Incidents(CompanyPlan plan) =>
        (plan.Archetype.Scenarios ?? []).Select((s, i) => new ActiveIncident(s.Key!, From.AddMinutes(30 + 40 * i), From.AddMinutes(60 + 40 * i))).ToList();

    public static string Rows(CompanyPlan plan)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        foreach (var app in plan.Apps)
        {
            var b = ReferenceSynthesizer.Generate(app, EffectCompiler.Compile(plan, app, Incidents(plan)), From, To);
            sb.Append("app ").Append(app.Database).Append('\n');
            foreach (var r in b.Requests)
                sb.Append(c, $"R|{r.Timestamp.UtcTicks}|{r.Id}|{r.Name}|{r.Url}|{r.ResultCode}|{r.Duration:R}|{r.Success}|{r.OperationId}|{r.Entity}|{r.StepIndex}|{r.EntityRank}|{r.OwnFailure}|{r.DependencyFailureMask}\n");
            foreach (var d in b.Dependencies)
                sb.Append(c, $"D|{d.Timestamp.UtcTicks}|{d.Id}|{d.Partner}|{d.Target}|{d.Type}|{d.Name}|{d.Success}|{d.ResultCode}|{d.Duration:R}|{d.OperationId}|{d.ParentId}\n");
            foreach (var x in b.Exceptions)
                sb.Append(c, $"X|{x.Timestamp.UtcTicks}|{x.Type}|{x.Message}|{x.Method}|{x.OperationId}|{x.ParentId}|{x.Entity}\n");
            foreach (var t in b.Traces)
                sb.Append(c, $"T|{t.Timestamp.UtcTicks}|{t.Message}|{t.SeverityLevel}|{t.OperationId}\n");
            foreach (var p in b.PageViews)
                sb.Append(c, $"P|{p.Timestamp.UtcTicks}|{p.Name}|{p.Url}|{p.Duration:R}|{p.OperationId}\n");
            foreach (var e in b.CustomEvents)
                sb.Append(c, $"E|{e.Timestamp.UtcTicks}|{e.Name}|{e.OperationId}|{e.Entity}|{string.Join(",", e.Measurements.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value.ToString("R", c)))}\n");
        }
        return Sha(sb.ToString());
    }

    /// <summary>The KQL of every table with the session/user lines removed: what decides every other column.</summary>
    public static string Kql(CompanyPlan plan)
    {
        var sb = new StringBuilder();
        foreach (var app in plan.Apps)
        foreach (var (table, command) in KqlSynthesisBuilder.Commands(app, EffectCompiler.Compile(plan, app, Incidents(plan)), From, To))
        {
            sb.Append("table ").Append(table).Append('\n');
            foreach (var line in command.Split('\n').Where(l => !IsSessionLine(l))) sb.Append(line).Append('\n');
        }
        return Sha(sb.ToString());
    }

    private static bool IsSessionLine(string line) =>
        line.StartsWith("let ses", StringComparison.Ordinal) || SessionStatement().IsMatch(line);

    private static readonly Regex SessionStatementPattern = new(@"^\| (extend (un|userId|sessionId|ses\w*) =|sort by m asc, sesK asc, k asc$)", RegexOptions.CultureInvariant);

    private static Regex SessionStatement() => SessionStatementPattern;

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
