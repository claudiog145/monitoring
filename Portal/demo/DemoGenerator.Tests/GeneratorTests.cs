using DemoGenerator.Archetypes;
using DemoGenerator.Runtime;
using DemoGenerator.Synthesis;

namespace DemoGenerator.Tests;

public class GeneratorTests
{
    private static readonly DateTimeOffset From = Fixtures.Monday;

    [Fact]
    public void Archetype_LoadsAndValidates()
    {
        var archetype = Fixtures.Archetype();

        Assert.Equal("fixture", archetype.CompanyKey);
        Assert.Equal("fixture-demo", archetype.ProfileKey);
        Assert.Equal(2, archetype.Apps!.Count);
        Assert.Equal(3, archetype.Steps!.Count);
        Assert.Equal(4, archetype.Scenarios!.Count);
        Assert.Empty(ArchetypeLoader.Validate(archetype));
        Assert.Equal("S-03", ArchetypeLoader.EntityIds(archetype.Entities)[2]);

        var broken = Fixtures.ArchetypeJson()
            .Replace("\"Fixture Shop (demo)\"", "\"Fixture Shop\"", StringComparison.Ordinal)
            .Replace("\"weekendFactor\": 0.7", "\"weekendFactor\": -1", StringComparison.Ordinal)
            .Replace("\"p95Ms\": 600", "\"p95Ms\": 100", StringComparison.Ordinal);
        var (parsed, errors) = ArchetypeLoader.TryParse(broken);

        Assert.Null(parsed);
        Assert.Contains(errors, e => e.Contains("$.displayName", StringComparison.Ordinal) && e.Contains("(demo)", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("$.traffic.weekendFactor", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("$.steps[0].p95Ms", StringComparison.Ordinal));
        Assert.Throws<ArchetypeValidationException>(() => ArchetypeLoader.Load(Path.Combine(AppContext.BaseDirectory, "missing.json")));
    }

    [Fact]
    public void Archetype_InvalidStepReferenceRejected()
    {
        var json = Fixtures.ArchetypeJson()
            .Replace("{ \"kind\": \"error-rate\", \"step\": \"GET /checkout/cart\", \"rate\": 0.25", "{ \"kind\": \"error-rate\", \"step\": \"GET /checkout/nope\", \"rate\": 0.25", StringComparison.Ordinal)
            .Replace("\"app\": \"demo-fixture-api\"", "\"app\": \"demo-fixture-ghost\"", StringComparison.Ordinal)
            .Replace("\"partner\": \"pay-gw\", \"rate\": 0.8", "\"partner\": \"nobody\", \"rate\": 0.8", StringComparison.Ordinal)
            .Replace("\"entities\": [\"S-03\"]", "\"entities\": [\"S-99\"]", StringComparison.Ordinal);

        var (archetype, errors) = ArchetypeLoader.TryParse(json);

        Assert.Null(archetype);
        Assert.Contains(errors, e => e.Contains("$.scenarios[2].effects[0].step 'GET /checkout/nope' is not the name of a step", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("$.steps[2].app 'demo-fixture-ghost' is not the appId of an app", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("$.scenarios[0].effects[0].partner 'nobody'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'S-99', which is not one of the generated entity ids", StringComparison.Ordinal));
    }

    [Fact]
    public void Synthesis_IsDeterministicForSeed()
    {
        var from = From.AddHours(8);
        var to = from.AddHours(6);
        var first = ReferenceSynthesizer.Generate(Fixtures.WebApp(Fixtures.Plan()), [], from, to);
        var second = ReferenceSynthesizer.Generate(Fixtures.WebApp(Fixtures.Plan()), [], from, to);
        var otherSeed = ReferenceSynthesizer.Generate(Fixtures.WebApp(Fixtures.Plan(seed: 2000)), [], from, to);

        Assert.True(first.Requests.Count > 1000);
        Assert.Equal(first.Requests, second.Requests);
        Assert.Equal(first.Dependencies, second.Dependencies);
        Assert.Equal(first.Exceptions, second.Exceptions);
        Assert.Equal(first.CustomEvents.Select(e => (e.OperationId, e.Measurements["Amount"])), second.CustomEvents.Select(e => (e.OperationId, e.Measurements["Amount"])));
        Assert.NotEqual(first.Requests.Select(r => r.OperationId), otherSeed.Requests.Select(r => r.OperationId));

        // The KQL that generates the same rows inside Kusto is a pure function of the plan as well.
        var plan = Fixtures.Plan();
        Assert.Equal(KqlSynthesisBuilder.Commands(Fixtures.WebApp(plan), [], from, to), KqlSynthesisBuilder.Commands(Fixtures.WebApp(Fixtures.Plan()), [], from, to));
    }

    [Fact]
    public void Synthesis_LatencyMatchesP50P95Within10Percent()
    {
        var plan = Fixtures.Plan();
        var batch = ReferenceSynthesizer.Generate(Fixtures.WebApp(plan), [], From, From.AddDays(2));
        var apiBatch = ReferenceSynthesizer.Generate(Fixtures.ApiApp(plan), [], From, From.AddDays(2));

        AssertPercentiles(batch.Requests.Where(r => r.Name == "GET /checkout/cart").Select(r => r.Duration), 120, 600);
        AssertPercentiles(batch.Requests.Where(r => r.Name == "POST /checkout/pay").Select(r => r.Duration), 300, 1500);
        AssertPercentiles(apiBatch.Requests.Select(r => r.Duration), 40, 200);
        AssertPercentiles(apiBatch.Dependencies.Where(d => d.Type == "SQL").Select(d => d.Duration), 5, 40);
    }

    [Fact]
    public void Synthesis_OperationIdSharedAcrossTables()
    {
        var plan = Fixtures.Plan();
        var incidents = new[] { new ActiveIncident("cart-errors", From.AddHours(10), From.AddHours(11)) };
        var app = Fixtures.WebApp(plan);
        var batch = ReferenceSynthesizer.Generate(app, EffectCompiler.Compile(plan, app, incidents), From, From.AddDays(1));
        var requests = batch.Requests.ToDictionary(r => r.OperationId);

        Assert.Equal(batch.Requests.Count, requests.Count);
        Assert.All(batch.Requests, r => Assert.Matches("^[0-9a-f]{32}$", r.OperationId));
        Assert.NotEmpty(batch.Dependencies);
        Assert.NotEmpty(batch.Exceptions);
        Assert.NotEmpty(batch.Traces);
        Assert.NotEmpty(batch.PageViews);
        Assert.NotEmpty(batch.CustomEvents);
        Assert.All(batch.Dependencies, d => Assert.Equal(requests[d.OperationId].Id, d.ParentId));
        Assert.All(batch.Exceptions, x => Assert.False(requests[x.OperationId].Success));
        Assert.All(batch.Traces, t => Assert.True(requests.ContainsKey(t.OperationId)));
        Assert.All(batch.PageViews, p => Assert.Equal("GET /checkout/cart", requests[p.OperationId].Name));
        Assert.All(batch.CustomEvents, e => Assert.Equal("POST /checkout/pay", requests[e.OperationId].Name));
        Assert.All(batch.CustomEvents, e => Assert.Equal(requests[e.OperationId].Entity, e.Entity));
        Assert.Equal(batch.Requests.Count(r => !r.Success), batch.Exceptions.Count);
        Assert.Equal(batch.Requests.Count(r => r.Name == "POST /checkout/pay" && r.Success), batch.CustomEvents.Count);
    }

    [Fact]
    public void Scenario_EntityOnlyAffectsOnlyThoseEntities()
    {
        var plan = Fixtures.Plan();
        var app = Fixtures.WebApp(plan);
        var incidents = new[] { new ActiveIncident("store-degraded", From, From.AddDays(3)) };
        var effects = EffectCompiler.Compile(plan, app, incidents);
        var batch = ReferenceSynthesizer.Generate(app, effects, From, From.AddDays(3));
        var cart = batch.Requests.Where(r => r.Name == "GET /checkout/cart").ToList();
        var target = cart.Where(r => r.Entity == "S-03").ToList();
        var others = cart.Where(r => r.Entity != "S-03").ToList();

        Assert.True(target.Count > 500, $"only {target.Count} S-03 cart requests");
        Assert.InRange(target.Count(r => !r.Success) / (double)target.Count, 0.25, 0.35);
        Assert.InRange(Median(target.Select(r => r.Duration)), 120 * 8 * 0.9, 120 * 8 * 1.1);
        Assert.All(target.Where(r => !r.Success), r => Assert.Equal("504", r.ResultCode));
        Assert.Contains(batch.Exceptions, x => x.Entity == "S-03" && x.Type == "System.TimeoutException");

        Assert.InRange(others.Count(r => !r.Success) / (double)others.Count, 0, 0.01);
        Assert.InRange(Median(others.Select(r => r.Duration)), 120 * 0.9, 120 * 1.1);
        var byOperation = batch.Requests.ToDictionary(r => r.OperationId);
        Assert.DoesNotContain(batch.Exceptions, x => x.Entity != "S-03" && x.Type == "System.TimeoutException" && byOperation[x.OperationId].Name == "GET /checkout/cart");
        // The step filter holds inside the entity too: S-03 payments keep their normal behaviour.
        Assert.InRange(Median(batch.Requests.Where(r => r.Entity == "S-03" && r.Name == "POST /checkout/pay").Select(r => r.Duration)), 300 * 0.85, 300 * 1.15);
    }

    [Fact]
    public void Scenario_DependencyFailureMarksDependencyAndRequest()
    {
        var plan = Fixtures.Plan();
        var app = Fixtures.WebApp(plan);
        var incident = new ActiveIncident("pay-gw-down", From.AddHours(12), From.AddHours(18));
        var batch = ReferenceSynthesizer.Generate(app, EffectCompiler.Compile(plan, app, [incident]), From.AddHours(6), From.AddHours(24));
        var requests = batch.Requests.ToDictionary(r => r.OperationId);
        bool During(DateTimeOffset t) => t >= incident.Start && t < incident.End;

        var inside = batch.Dependencies.Where(d => During(requests[d.OperationId].Timestamp)).ToList();
        var outside = batch.Dependencies.Where(d => !During(requests[d.OperationId].Timestamp)).ToList();
        Assert.True(inside.Count > 300);
        Assert.InRange(inside.Count(d => !d.Success) / (double)inside.Count, 0.75, 0.85);
        Assert.InRange(outside.Count(d => !d.Success) / (double)outside.Count, 0, 0.01);
        Assert.InRange(Median(inside.Select(d => d.Duration)), 150 * 4 * 0.85, 150 * 4 * 1.15);

        foreach (var dependency in batch.Dependencies.Where(d => !d.Success))
        {
            var request = requests[dependency.OperationId];
            Assert.False(request.Success);
            Assert.Equal(dependency.ParentId, request.Id);
            Assert.Contains(request.ResultCode, new[] { "502", "500", "504" });
            Assert.Contains(batch.Exceptions, x => x.OperationId == request.OperationId);
        }
        Assert.Contains(batch.Exceptions, x => x.Type == ReferenceSynthesizer.DependencyFailureException && x.Message.Contains("gw.paygate.demo", StringComparison.Ordinal));
        Assert.All(batch.Requests.Where(r => r.Name == "GET /checkout/cart" && During(r.Timestamp)), r => Assert.Equal(0, r.DependencyFailureMask));
    }

    [Fact]
    public void Backfill_IncludesHistoricalIncidents()
    {
        var plan = Fixtures.Plan(Scales.Tiny);
        var resetAt = From.AddDays(3).AddMinutes(7).AddSeconds(30);
        var incidents = plan.HistoricalIncidents(resetAt);

        Assert.Equal(2, incidents.Count);
        var payDown = incidents.Single(i => i.ScenarioKey == "pay-gw-down");
        Assert.Equal(From.AddDays(3).AddMinutes(7).AddHours(-30), payDown.Start);
        Assert.Equal(payDown.Start.AddMinutes(45), payDown.End);

        var jobs = BackfillPlanner.PlanBackfill(plan, resetAt, 2, 24);
        var (windowFrom, windowTo) = BackfillPlanner.Window(resetAt, 2);
        Assert.Equal(windowTo - TimeSpan.FromDays(2), windowFrom);
        Assert.All(KqlSynthesisBuilder.Tables, t => Assert.Contains(jobs, j => j.Table == t && j.Database == "fixture-web"));
        Assert.Equal(windowFrom, jobs.Where(j => j.Database == "fixture-web").Min(j => j.From));
        Assert.Equal(windowTo, jobs.Where(j => j.Database == "fixture-web").Max(j => j.To));
        var covering = jobs.Where(j => j.Database == "fixture-web" && j.Table == "requests" && j.From <= payDown.Start && j.To > payDown.Start).ToList();
        Assert.Single(covering);
        Assert.Contains($"mts >= {KqlSynthesisBuilder.Date(payDown.Start)} and mts < {KqlSynthesisBuilder.Date(payDown.End)}", covering[0].Command, StringComparison.Ordinal);
        Assert.Contains(".set-or-append requests <|", covering[0].Command, StringComparison.Ordinal);
        Assert.DoesNotContain(jobs.Where(j => j.To <= payDown.Start || j.From >= payDown.End), j => j.Command.Contains(KqlSynthesisBuilder.Date(payDown.Start), StringComparison.Ordinal));

        var laptop = Fixtures.Plan();
        var app = Fixtures.WebApp(laptop);
        var effects = EffectCompiler.Compile(laptop, app, laptop.HistoricalIncidents(resetAt));
        var during = ReferenceSynthesizer.Generate(app, effects, payDown.Start, payDown.End);
        var before = ReferenceSynthesizer.Generate(app, effects, payDown.Start.AddHours(-2), payDown.Start);
        Assert.InRange(during.Dependencies.Count(d => !d.Success) / (double)during.Dependencies.Count, 0.7, 0.9);
        Assert.InRange(before.Dependencies.Count(d => !d.Success) / (double)before.Dependencies.Count, 0, 0.02);
    }

    [Fact]
    public void Scale_TinyIsOnePercentOfLaptop()
    {
        var laptop = Fixtures.Plan(Scales.Laptop);
        var tiny = Fixtures.Plan(Scales.Tiny);
        var showcase = Fixtures.Plan(Scales.Showcase);

        for (var a = 0; a < laptop.Apps.Count; a++)
        {
            for (var h = 0; h < 24; h++)
            {
                Assert.Equal(laptop.Apps[a].RatePerMinuteByHour[h] * 0.01, tiny.Apps[a].RatePerMinuteByHour[h], 12);
                Assert.Equal(laptop.Apps[a].RatePerMinuteByHour[h] * 4, showcase.Apps[a].RatePerMinuteByHour[h], 9);
            }
        }

        long Count(CompanyPlan plan) => plan.Apps.Sum(app =>
        {
            var m0 = SynthesisClock.EpochMinute(From);
            var total = 0L;
            for (var m = m0; m < m0 + 7 * 1440; m++) total += ReferenceSynthesizer.MinuteCount(app, [], m);
            return total;
        });
        var laptopRows = Count(laptop);
        var tinyRows = Count(tiny);
        Assert.InRange(tinyRows / (double)laptopRows, 0.009, 0.011);
        // A week of the fixture: 5 weekdays + 2 weekend days at 0.7 of 20 000 requests a day.
        Assert.InRange(laptopRows, 20_000 * 6.4 * 0.98, 20_000 * 6.4 * 1.02);
    }

    [Fact]
    public void Scenario_GlobalTrafficMultiplierScalesVolume()
    {
        var plan = Fixtures.Plan();
        var app = Fixtures.WebApp(plan);
        var window = (From.AddHours(10), From.AddHours(12));
        var effects = EffectCompiler.Compile(plan, app, [new ActiveIncident("flash-sale", window.Item1, window.Item2)]);

        var normal = ReferenceSynthesizer.Generate(app, [], window.Item1, window.Item2).Requests.Count;
        var peak = ReferenceSynthesizer.Generate(app, effects, window.Item1, window.Item2).Requests.Count;

        Assert.InRange(peak / (double)normal, 1.9, 2.1);
        Assert.Contains("iff((mts >= ", KqlSynthesisBuilder.BaseQuery(app, effects, window.Item1, window.Item2), StringComparison.Ordinal);
    }

    private static void AssertPercentiles(IEnumerable<double> values, double p50, double p95)
    {
        var sorted = values.Order().ToList();
        Assert.True(sorted.Count > 1500, $"only {sorted.Count} samples");
        var observed50 = sorted[(int)(sorted.Count * 0.50)];
        var observed95 = sorted[(int)(sorted.Count * 0.95)];
        Assert.InRange(observed50, p50 * 0.9, p50 * 1.1);
        Assert.InRange(observed95, p95 * 0.9, p95 * 1.1);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
