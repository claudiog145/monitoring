using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.ProfileJourneys;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.MultiCompany.Journeys;

/// <summary>Contract v6 §4.1/§4.2 (MC1): generic per-company journeys and the entity catalog.</summary>
public class ProfileJourneyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Journeys_ClassifiedByProfileSteps()
    {
        var profile = Bank();

        var kql = ProfileJourneyKql.BuildJourneyQuery(profile, Now.AddMinutes(-30), Now, 500, 20000);
        Assert.Contains(CompanyKqlTemplater.RenderServiceAndStepClassifier(profile, "\n"), kql, StringComparison.Ordinal);
        Assert.Contains("app(\"{{SOURCE:bank-web}}\").requests", kql, StringComparison.Ordinal);
        Assert.Contains("app(\"{{SOURCE:bank-mobile}}\").requests", kql, StringComparison.Ordinal);
        Assert.Contains("extend Entity = tostring(tostring(customDimensions.BranchId))", kql, StringComparison.Ordinal);
        Assert.Contains("let maxPerService = 500;", kql, StringComparison.Ordinal);
        Assert.Contains("operation_Id", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("{{SERVICE_AND_STEP_CLASSIFIER}}", kql, StringComparison.Ordinal);

        var batch = ProfileJourneyBuilder.Build(profile,
        [
            Request("op-1", 0, "web", "login", entity: "B-001"),
            Request("op-1", 1, "web", "balance"),
            Request("op-2", 0, "mobile", "card-lock", entity: "B-002"),
        ], 500);

        var web = Assert.Single(batch.Journeys, j => j.JourneyId == "op-1");
        Assert.Equal("bank", web.ProfileKey);
        Assert.Equal("web", web.ServiceKey);
        Assert.Equal("web-ops", web.FlowKey);
        Assert.Equal("B-001", web.EntityKey);
        Assert.Equal(2, web.LastStepOrder);
        Assert.Equal("balance", web.LastStepKey);
        Assert.Equal(2, web.StepCount);
        Assert.Equal("op-1", web.SampleOperationId);

        var mobile = Assert.Single(batch.Journeys, j => j.JourneyId == "op-2");
        Assert.Equal("mobile", mobile.ServiceKey);
        Assert.Equal("cards", mobile.FlowKey);
        Assert.Equal(1, mobile.LastStepOrder);

        var steps = batch.Steps.Where(s => s.JourneyId == "op-1").OrderBy(s => s.StepOrder).ToList();
        Assert.Equal(["login", "balance"], steps.Select(s => s.StepKey));
        Assert.Equal([1, 2], steps.Select(s => s.StepOrder));
    }

    [Fact]
    public void Journeys_StatusCompletedDroppedFailed()
    {
        var profile = Bank();
        var batch = ProfileJourneyBuilder.Build(profile,
        [
            Request("completed", 0, "web", "login"),
            Request("completed", 1, "web", "balance"),
            Request("completed", 2, "web", "transfer"),
            Request("dropped", 0, "web", "login"),
            Request("dropped", 1, "web", "balance"),
            Request("failed", 0, "web", "login"),
            Request("failed", 1, "web", "transfer", success: false),
            Request("unclassified", 0, null, null),
        ], 500);

        Assert.Equal(ProfileJourneyStatus.Completed, batch.Journeys.Single(j => j.JourneyId == "completed").Status);
        var dropped = batch.Journeys.Single(j => j.JourneyId == "dropped");
        Assert.Equal(ProfileJourneyStatus.DroppedAt(2), dropped.Status);
        Assert.Equal("Dropped at step 2", dropped.Status);
        Assert.False(dropped.Failed);
        var failed = batch.Journeys.Single(j => j.JourneyId == "failed");
        Assert.Equal(ProfileJourneyStatus.Failed, failed.Status);
        Assert.True(failed.Failed);
        Assert.DoesNotContain(batch.Journeys, j => j.JourneyId == "unclassified");

        var completed = batch.Journeys.Single(j => j.JourneyId == "completed");
        Assert.Equal(Now, completed.StartedAt);
        Assert.Equal(Now.AddSeconds(2).AddMilliseconds(100), completed.EndedAt);
        // FXM2: DurationMs is the time the requests took (3 x 100 ms), not the session span (EndedAt - StartedAt
        // = 2100 ms), which includes the user's think time between steps.
        Assert.Equal(300, completed.DurationMs);

        var capped = ProfileJourneyBuilder.Build(profile,
            Enumerable.Range(0, 5).Select(i => Request($"op-{i}", i, "web", "login")).ToList(), 3);
        Assert.Equal(3, capped.Journeys.Count);
        Assert.Equal(["op-4", "op-3", "op-2"], capped.Journeys.Select(j => j.JourneyId));
    }

    [Fact]
    public async Task Journeys_IsolatedByProfile()
    {
        var bank = Bank();
        var retail = Retail();
        var catalog = new Catalog(
            Source("bank-web", "bank"), Source("bank-mobile", "bank"),
            Source("shop-web", "retail"));
        var telemetry = new ScriptedTelemetry(kql =>
            kql.Contains("/res/bank-web", StringComparison.Ordinal)
                ? [Row("bank-op", 0, "web", "login", "B-001")]
                : kql.Contains("/res/shop-web", StringComparison.Ordinal)
                    ? [Row("shop-op", 0, "storefront", "search", "MLA")]
                    : []);
        var store = new InMemoryProfileJourneyStore(new FixedClock(Now));
        var profiles = new Profiles(bank, retail);
        var sync = new ProfileJourneySyncService(
            profiles, telemetry, new AliasResolver(), new TelemetrySourceOwnershipCheck(catalog), store,
            Options.Create(new ProfileJourneyOptions()), Options.Create(new JourneySyncOptions()), clock: new FixedClock(Now));

        var result = await sync.SyncAllAsync(CancellationToken.None);

        Assert.Equal(2, result.Profiles.Count);
        Assert.All(result.Profiles, p => Assert.Null(p.Error));
        Assert.Equal(2, telemetry.Requests.Count);
        var bankQuery = telemetry.Requests.Single(r => r.Kql.Contains("/res/bank-web", StringComparison.Ordinal));
        Assert.Equal("app-bank-web", bankQuery.AppInsightsAppId);
        Assert.DoesNotContain("shop-web", bankQuery.Kql, StringComparison.Ordinal);
        var retailQuery = telemetry.Requests.Single(r => r.Kql.Contains("/res/shop-web", StringComparison.Ordinal));
        Assert.DoesNotContain("bank-", retailQuery.Kql, StringComparison.Ordinal);

        var reader = new ProfileJourneyReader(store, profiles);
        var bankList = await reader.ListAsync(new ProfileJourneyQuery("bank", null, null, null, TimeSpan.FromDays(1), 100), CancellationToken.None);
        var bankJourney = Assert.Single(bankList.Journeys);
        Assert.Equal("bank-op", bankJourney.JourneyId);
        Assert.Equal(new ProfileJourneyLabels("Canal", "Operación", "Paso", "Sucursal"), bankList.Labels);

        var retailList = await reader.ListAsync(new ProfileJourneyQuery("retail", null, null, null, TimeSpan.FromDays(1), 100), CancellationToken.None);
        Assert.Equal("shop-op", Assert.Single(retailList.Journeys).JourneyId);
        Assert.Null(await reader.GetAsync("retail", "bank-op", CancellationToken.None));
        Assert.NotNull(await reader.GetAsync("bank", "bank-op", CancellationToken.None));

        // A service pointing at an alias another company owns is never read for this company.
        var thief = Retail() with
        {
            ProfileKey = "thief",
            Services = [Retail().Services[0] with { TelemetrySourceAliases = ["bank-web"] }],
        };
        var outcome = await sync.SyncProfileAsync(thief, CancellationToken.None);
        Assert.NotNull(outcome.Error);
        Assert.Equal(2, telemetry.Requests.Count);
        Assert.Empty((await reader.ListAsync(new ProfileJourneyQuery("thief", null, null, null, TimeSpan.FromDays(1), 100), CancellationToken.None)).Journeys);

        var unknown = await reader.ResolveProfileAsync("nobody", CancellationToken.None);
        Assert.False(unknown.IsValid);
    }

    [Fact]
    public async Task Entities_FromIdentitySources()
    {
        var bank = Bank();
        var kql = ProfileJourneyKql.BuildEntityQuery(bank, null, Now.AddHours(-24), Now, 10);
        Assert.Contains("extend Entity = tostring(tostring(customDimensions.BranchId))", kql, StringComparison.Ordinal);
        Assert.Contains("where isnotempty(Entity)", kql, StringComparison.Ordinal);
        Assert.Contains("| take 10", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceKey ==", kql, StringComparison.Ordinal);

        var scoped = ProfileJourneyKql.BuildEntityQuery(bank, "mobile", Now.AddHours(-24), Now, 10);
        Assert.Contains("ServiceKey == \"mobile\"", scoped, StringComparison.Ordinal);
        Assert.Contains("{{SOURCE:bank-mobile}}", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("{{SOURCE:bank-web}}", scoped, StringComparison.Ordinal);

        var telemetry = new ScriptedTelemetry(_ =>
        [
            Json("""{"Entity":"B-002","Requests":40,"Failures":1}"""),
            Json("""{"Entity":"B-001","Requests":90,"Failures":3}"""),
            Json("""{"Entity":"","Requests":5,"Failures":0}"""),
        ]);
        var catalog = new Catalog(Source("bank-web", "bank"), Source("bank-mobile", "bank"), Source("shop-web", "retail"));
        var service = new EntityCatalogService(
            new Profiles(bank, Retail()), telemetry, new AliasResolver(), new TelemetrySourceOwnershipCheck(catalog),
            new EntityCatalogCache(new FixedClock(Now)), Options.Create(new ProfileJourneyOptions()),
            Options.Create(new JourneySyncOptions()), clock: new FixedClock(Now));

        var result = await service.GetTopEntitiesAsync(new EntityQuery("bank", null, TimeSpan.FromHours(24), 10), CancellationToken.None);
        Assert.Equal("Sucursal", result.EntityLabel);
        Assert.Equal([new EntityCount("B-001", 90, 3), new EntityCount("B-002", 40, 1)], result.Entities);
        Assert.DoesNotContain("shop-web", telemetry.Requests.Single().Kql, StringComparison.Ordinal);

        await service.GetTopEntitiesAsync(new EntityQuery("bank", null, TimeSpan.FromHours(24), 10), CancellationToken.None);
        Assert.Single(telemetry.Requests);

        var unknownService = await service.GetTopEntitiesAsync(new EntityQuery("bank", "storefront", TimeSpan.FromHours(24), 10), CancellationToken.None);
        Assert.Empty(unknownService.Entities);
        Assert.Single(telemetry.Requests);
    }

    [Theory]
    [InlineData("15m", 15)]
    [InlineData("24h", 1440)]
    [InlineData("8d", 11520)]
    public void RequestParser_AcceptsWindows(string value, int minutes)
    {
        Assert.Null(ProfileJourneyRequestParser.ParseWindow(value, TimeSpan.FromHours(1), out var window));
        Assert.Equal(TimeSpan.FromMinutes(minutes), window);
    }

    [Theory]
    [InlineData("9d")]
    [InlineData("0m")]
    [InlineData("1w")]
    [InlineData("1h;drop")]
    public void RequestParser_RejectsBadWindows(string value) =>
        Assert.NotNull(ProfileJourneyRequestParser.ParseWindow(value, TimeSpan.FromHours(1), out _));

    [Fact]
    public async Task InMemoryStore_FiltersByStatusAndPurges()
    {
        var store = new InMemoryProfileJourneyStore(new FixedClock(Now));
        var batch = ProfileJourneyBuilder.Build(Bank(),
        [
            Request("a", 0, "web", "login"),
            Request("b", 1, "web", "login", success: false),
            Request("c", 2, "web", "login"),
            Request("c", 3, "web", "balance"),
            Request("c", 4, "web", "transfer"),
        ], 500);
        await store.UpsertAsync("bank", batch.Journeys, batch.Steps, CancellationToken.None);
        await store.UpsertAsync("other", batch.Journeys, batch.Steps, CancellationToken.None);

        async Task<IEnumerable<string>> Ids(string? status) =>
            (await store.ListAsync(new ProfileJourneyQuery("bank", null, null, status, TimeSpan.FromDays(1), 10), CancellationToken.None))
            .Select(j => j.JourneyId).OrderBy(x => x);

        Assert.Equal(["a", "b", "c"], await Ids(null));
        Assert.Equal(["a"], await Ids("dropped"));
        Assert.Equal(["b"], await Ids("Failed"));
        Assert.Equal(["c"], await Ids("Completed"));
        Assert.Equal(3, (await store.GetStepsAsync("bank", "c", CancellationToken.None)).Count);
        Assert.Empty(await store.ListAsync(new ProfileJourneyQuery("other", null, null, null, TimeSpan.FromDays(1), 10), CancellationToken.None));

        Assert.Equal(3, await store.PurgeOlderThanAsync(Now.AddDays(1), CancellationToken.None));
        Assert.Empty(await Ids(null));
    }

    [Fact]
    public void Journeys_GroupedBySessionWhenPresent()
    {
        var profile = Bank();

        var kql = ProfileJourneyKql.BuildJourneyQuery(profile, Now.AddMinutes(-30), Now, 500, 20000);
        // The request union projects session_Id as SessionId (empty when the source has none).
        Assert.Contains("SessionId=tostring(column_ifexists(\"session_Id\",\"\"))", kql, StringComparison.Ordinal);
        Assert.Contains("| extend JourneyKey = iff(isnotempty(SessionId), SessionId, operation_Id);", kql, StringComparison.Ordinal);
        Assert.Contains("OperationId = operation_Id", kql, StringComparison.Ordinal);

        // Rows of one session keep their own operation ids; the journey is the session.
        var rows = new[]
        {
            SessionRow("sess-1", "op-c", 120, "web", "transfer"),
            SessionRow("sess-1", "op-a", 0, "web", "login"),
            SessionRow("sess-1", "op-b", 60, "web", "balance"),
            SessionRow("op-solo", "op-solo", 10, "mobile", "card-lock"),
        };
        var requests = rows.Select(ProfileJourneySyncService.ToRequest).ToList();
        Assert.All(requests, Assert.NotNull);
        Assert.Equal("op-c", requests[0]!.OperationId);

        // Same timestamp: the flow order breaks the tie.
        var tie = Now.AddMinutes(-1);
        var batch = ProfileJourneyBuilder.Build(profile,
        [
            .. requests.Select(r => r!),
            new ProfileJourneyRequest("sess-2", tie, "GET /balance", 50, true, "200", "web", "balance", "B-009", "op-y"),
            new ProfileJourneyRequest("sess-2", tie, "POST /auth/login", 50, true, "200", "web", "login", "B-009", "op-x"),
        ], 500);

        var session = batch.Journeys.Single(j => j.JourneyId == "sess-1");
        Assert.Equal(3, session.StepCount);
        Assert.Equal(ProfileJourneyStatus.Completed, session.Status);
        Assert.Equal("web-ops", session.FlowKey);
        Assert.Equal("B-001", session.EntityKey);
        Assert.Equal("op-a", session.SampleOperationId);
        Assert.Equal(Now.AddMinutes(-5), session.StartedAt);
        var steps = batch.Steps.Where(s => s.JourneyId == "sess-1").OrderBy(s => s.StepOrder).ToList();
        Assert.Equal(["login", "balance", "transfer"], steps.Select(s => s.StepKey));
        Assert.Equal([1, 2, 3], steps.Select(s => s.StepOrder));
        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));

        var tied = batch.Steps.Where(s => s.JourneyId == "sess-2").OrderBy(s => s.StepOrder).ToList();
        Assert.Equal(["login", "balance"], tied.Select(s => s.StepKey));
        Assert.Equal("op-x", batch.Journeys.Single(j => j.JourneyId == "sess-2").SampleOperationId);

        // Without a session the journey is still one operation.
        var solo = batch.Journeys.Single(j => j.JourneyId == "op-solo");
        Assert.Equal(1, solo.StepCount);
        Assert.Equal("op-solo", solo.SampleOperationId);
    }

    [Fact]
    public async Task Journeys_SessionStraddlingWindowStartIsNotReplacedByItsTail()
    {
        var profile = Bank();
        // Longest flow has 3 steps: a session can begin up to 4 minutes before the window it reaches into.
        Assert.Equal(TimeSpan.FromMinutes(4), ProfileJourneyKql.SessionLookback(profile));
        var kql = ProfileJourneyKql.BuildJourneyQuery(profile, Now.AddMinutes(-30), Now, 500, 20000);
        Assert.Contains("let windowStart = datetime(2026-11-07T11:30:00Z);", kql, StringComparison.Ordinal);
        Assert.Contains("let historyStart = datetime(2026-11-07T11:26:00Z);", kql, StringComparison.Ordinal);
        // Kusto cuts each source branch where it reads it, so every branch must read from the lookback.
        var branches = SourceBranchBounds(kql);
        Assert.NotEmpty(branches);
        Assert.All(branches, b => Assert.Equal("historyStart", b.From));
        var firstSeen = kql.IndexOf("| summarize arg_min(timestamp, ServiceKey) by JourneyKey", StringComparison.Ordinal);
        var startedInWindow = kql.IndexOf("| where timestamp >= windowStart", StringComparison.Ordinal);
        var capped = kql.IndexOf("| extend Rank = row_number(", StringComparison.Ordinal);
        Assert.True(firstSeen > 0 && firstSeen < startedInWindow && startedInWindow < capped);

        // One session walks login -> balance -> transfer a minute apart, starting at 11:28:30.
        var start = new DateTimeOffset(2026, 11, 7, 11, 28, 30, TimeSpan.Zero);
        var session = new[]
        {
            TimedRow("sess-edge", "op-1", start, "login"),
            TimedRow("sess-edge", "op-2", start.AddMinutes(1), "balance"),
            TimedRow("sess-edge", "op-3", start.AddMinutes(2), "transfer"),
        };
        var telemetry = new ScriptedTelemetry(q => q.Contains("/res/bank-web", StringComparison.Ordinal) ? EvaluateJourneyWindow(q, session) : []);
        var store = new InMemoryProfileJourneyStore(new FixedClock(Now));
        var catalog = new Catalog(Source("bank-web", "bank"), Source("bank-mobile", "bank"));

        ProfileJourneySyncService SyncAt(DateTimeOffset now) => new(
            new Profiles(profile), telemetry, new AliasResolver(), new TelemetrySourceOwnershipCheck(catalog), store,
            Options.Create(new ProfileJourneyOptions()), Options.Create(new JourneySyncOptions()), clock: new FixedClock(now));

        // 11:58: the session began inside [11:28, 11:58) and is written whole.
        Assert.Null((await SyncAt(Now.AddMinutes(-2)).SyncProfileAsync(profile, CancellationToken.None)).Error);
        // 12:00: the window [11:30, 12:00) holds only its last two steps; it must not be rewritten as that tail.
        var outcome = await SyncAt(Now).SyncProfileAsync(profile, CancellationToken.None);
        Assert.Null(outcome.Error);
        Assert.Equal(0, outcome.Journeys);

        var detail = await new ProfileJourneyReader(store, new Profiles(profile)).GetAsync("bank", "sess-edge", CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(start, detail.Journey.StartedAt);
        Assert.Equal(3, detail.Journey.StepCount);
        Assert.Equal(ProfileJourneyStatus.Completed, detail.Journey.Status);
        Assert.Equal(["login", "balance", "transfer"], detail.Steps.OrderBy(s => s.StepOrder).Select(s => s.StepKey));
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Variables each <c>app(...).requests</c> branch of the rendered union is cut at.</summary>
    private static IReadOnlyList<(string From, string To)> SourceBranchBounds(string kql) =>
        System.Text.RegularExpressions.Regex.Matches(kql, @"app\(""[^""]*""\)\.requests \| where timestamp>=(\w+) and timestamp<(\w+)")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

    /// <summary>
    /// What Kusto returns for the journey query over <paramref name="rows"/>, honouring every time filter as
    /// rendered: each source branch's own cut, the filter after the union, and the first-request filter after
    /// <c>arg_min</c>. Bounds are resolved from the query's lets.
    /// </summary>
    private static IReadOnlyList<TelemetryJourneyRow> EvaluateJourneyWindow(string kql, IReadOnlyList<(string Key, DateTimeOffset At, TelemetryJourneyRow Row)> rows)
    {
        DateTimeOffset Let(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(kql, "let " + name + @" = datetime\(([^)]+)\);");
            Assert.True(match.Success, name);
            return DateTimeOffset.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        System.Text.RegularExpressions.Match Find(string pattern)
        {
            var match = System.Text.RegularExpressions.Regex.Match(kql, pattern);
            Assert.True(match.Success, pattern);
            return match;
        }

        var branch = SourceBranchBounds(kql).Distinct().Single();
        var outer = Find(@"\n\s*\| where timestamp >= (\w+) and timestamp < (\w+)\r?\n");
        var started = Find(@"arg_min\(timestamp, ServiceKey\) by JourneyKey\r?\n\s*\| where timestamp >= (\w+)");

        var readFrom = new[] { Let(branch.From), Let(outer.Groups[1].Value) }.Max();
        var readTo = new[] { Let(branch.To), Let(outer.Groups[2].Value) }.Min();
        var startedFrom = Let(started.Groups[1].Value);
        return rows
            .Where(r => r.At >= readFrom && r.At < readTo)
            .GroupBy(r => r.Key)
            .Where(g => g.Min(r => r.At) >= startedFrom)
            .SelectMany(g => g.OrderBy(r => r.At))
            .Select(r => r.Row)
            .ToList();
    }

    private static (string Key, DateTimeOffset At, TelemetryJourneyRow Row) TimedRow(string journeyKey, string operationId, DateTimeOffset at, string step) =>
        (journeyKey, at, Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["JourneyKey"] = journeyKey,
            ["timestamp"] = at.ToString("O"),
            ["name"] = "GET /" + step,
            ["DurationMs"] = 80.0,
            ["success"] = true,
            ["resultCode"] = "200",
            ["ServiceKey"] = "web",
            ["StepKey"] = step,
            ["Entity"] = "B-001",
            ["OperationId"] = operationId,
        })));

    private static TelemetryJourneyRow SessionRow(string journeyKey, string operationId, int second, string service, string step) =>
        Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["JourneyKey"] = journeyKey,
            ["timestamp"] = Now.AddMinutes(-5).AddSeconds(second).ToString("O"),
            ["name"] = "GET /" + step,
            ["DurationMs"] = 80.0,
            ["success"] = true,
            ["resultCode"] = "200",
            ["ServiceKey"] = service,
            ["StepKey"] = step,
            ["Entity"] = "B-001",
            ["OperationId"] = operationId,
        }));
    private static CompanyProfile Bank() => new(
        "bank",
        "Banco (test)",
        new CompanyLabels("Canal", "Operación", "Paso", "Sucursal", "Equipo"),
        [
            new ServiceDefinition("web", "Web", ["bank-web"], [],
            [
                new FlowDefinition("web-ops", "Web ops",
                [
                    Step("login", 1, "/auth/login"),
                    Step("balance", 2, "/balance"),
                    Step("transfer", 3, "/transfer"),
                ]),
            ]),
            new ServiceDefinition("mobile", "Mobile", ["bank-mobile"], [],
            [
                new FlowDefinition("mobile-ops", "Mobile ops", [Step("m-login", 4, "/m/login"), Step("m-balance", 5, "/m/balance")]),
                new FlowDefinition("cards", "Cards", [Step("card-lock", 6, "/cards/lock"), Step("card-list", 7, "/cards/list")]),
            ]),
        ],
        [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "BranchId", "branch")],
        new CompanyGlossary("channel", "operation", "step", "branch", "team"));

    private static CompanyProfile Retail() => new(
        "retail",
        "Retail (test)",
        new CompanyLabels("Platform", "Journey", "Step", "Site", "Squad"),
        [
            new ServiceDefinition("storefront", "Storefront", ["shop-web"], [],
                [new FlowDefinition("browse", "Browse", [Step("search", 1, "/search"), Step("item", 2, "/item/")])]),
        ],
        [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "SiteId", "site")],
        new CompanyGlossary("platform", "journey", "step", "site", "squad"));

    private static StepDefinition Step(string key, int order, string path) =>
        new(key, key, order, [new StepMatcher(StepMatcherKind.PathContains, path)]);

    private static ProfileJourneyRequest Request(string op, int second, string? service, string? step, string? entity = null, bool success = true) =>
        new(op, Now.AddSeconds(second), $"GET /{step}", 100, success, success ? "200" : "500", service, step, entity);

    private static TelemetrySourceEntry Source(string alias, string owner) =>
        new(alias, "production", "app-insights",
            $"/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/microsoft.insights/components/{alias}",
            AppId: "app-" + alias, OwnerProfileKey: owner);

    private static TelemetryJourneyRow Row(string op, int second, string service, string step, string entity) =>
        Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["JourneyKey"] = op,
            ["timestamp"] = Now.AddMinutes(-5).AddSeconds(second).ToString("O"),
            ["name"] = "GET /" + step,
            ["DurationMs"] = 120.5,
            ["success"] = true,
            ["resultCode"] = "200",
            ["ServiceKey"] = service,
            ["StepKey"] = step,
            ["Entity"] = entity,
        }));

    private static TelemetryJourneyRow Json(string json) => JsonSerializer.Deserialize<TelemetryJourneyRow>(json)!;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Profiles(params CompanyProfile[] profiles) : ICompanyProfileStore
    {
        public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default) =>
            Task.FromResult(profiles.FirstOrDefault(p => string.Equals(p.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanyProfile>>(profiles);

        public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Catalog(params TelemetrySourceEntry[] entries) : ITelemetrySourceStore
    {
        public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TelemetrySourceEntry>>(entries);

        public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AliasResolver : IKqlSourceResolver
    {
        public Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default) =>
            Task.FromResult(KqlSourceResolver.PlaceholderPattern.Replace(kql, m => "/res/" + m.Groups["alias"].Value));

        public void Invalidate() { }
    }

    private sealed class ScriptedTelemetry(Func<string, IReadOnlyList<TelemetryJourneyRow>> respond) : ITelemetrySource
    {
        public List<TelemetryQueryRequest> Requests { get; } = [];

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request.Kql));
        }
    }
}
