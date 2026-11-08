using System.Text.RegularExpressions;
using DemoGenerator.Packs;
using DemoGenerator.Runtime;
using DemoGenerator.Synthesis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DemoGenerator.Tests;

public class RuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 0, 30, TimeSpan.Zero);

    [Fact]
    public async Task Feeder_PartialFailureDoesNotDuplicateTablesThatSucceeded()
    {
        var kusto = new FakeKustoClient();
        var time = new ManualTime(T0);
        var engine = new DemoEngine(kusto, new PackCatalog(NullLogger<PackCatalog>.Instance), new ScenarioBoard(time),
            Options.Create(new DemoOptions { PacksPath = Fixtures.PacksRoot, BatchHours = 6 }), time, NullLogger<DemoEngine>.Instance);
        await engine.ResetAsync(["fixture"], 1, Scales.Laptop, default);
        kusto.ClearLog();

        time.Now = T0.AddMinutes(3);
        kusto.FailWhen = (db, csl) => db == "fixture-web" && csl.StartsWith(".set-or-append dependencies", StringComparison.Ordinal);
        await engine.FeedOnceAsync(default);
        kusto.FailWhen = null;
        time.Now = T0.AddMinutes(5);
        await engine.FeedOnceAsync(default);

        var minute = SynthesisClock.EpochMinute(T0);
        IReadOnlyList<(long From, long To)> Windows(string db, string table) => kusto.Snapshot()
            .Where(c => c.Database == db && c.Csl.StartsWith($".set-or-append {table} <|", StringComparison.Ordinal))
            .Select(c => Regex.Match(c.Csl, @"range m from (\d+) to (\d+) step 1"))
            .Select(m => (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value) + 1))
            .ToList();

        Assert.Single(kusto.Failed);
        // Tables that succeeded on the first tick continue where they stopped: no minute is written twice.
        Assert.Equal([(minute, minute + 3), (minute + 3, minute + 5)], Windows("fixture-web", "requests"));
        Assert.Equal([(minute, minute + 3), (minute + 3, minute + 5)], Windows("fixture-web", "customEvents"));
        Assert.Equal([(minute, minute + 3), (minute + 3, minute + 5)], Windows("fixture-api", "dependencies"));
        // The failed table retries its whole window once, and only that table.
        Assert.Equal([(minute, minute + 5)], Windows("fixture-web", "dependencies"));
    }

    [Fact]
    public void ScenarioBoard_RestartKeepsEveryEarlierRun()
    {
        var time = new ManualTime(T0);
        var board = new ScenarioBoard(time);
        var start = SynthesisClock.FloorMinute(T0);

        board.Start("fixture", "cart-errors", "Cart", 30);
        time.Now = T0.AddMinutes(5);
        board.Stop("fixture");
        time.Now = T0.AddMinutes(10);
        board.Start("fixture", "cart-errors", "Cart", 30);
        time.Now = T0.AddMinutes(20);
        board.Start("fixture", "cart-errors", "Cart", 15);

        Assert.Equal(
            [
                new ActiveIncident("cart-errors", start, start.AddMinutes(5)),
                new ActiveIncident("cart-errors", start.AddMinutes(10), start.AddMinutes(20)),
                new ActiveIncident("cart-errors", start.AddMinutes(20), start.AddMinutes(35)),
            ],
            board.IncidentsFor("fixture"));
        var active = Assert.Single(board.Active());
        Assert.Equal(start.AddMinutes(20), active.StartedAt);
        Assert.Empty(board.IncidentsFor("other"));
    }

    [Theory]
    [InlineData("(?=paygate)paygate", "lookahead (?=")]
    [InlineData("pay(?<!x)gate", "negative lookbehind (?<!")]
    [InlineData(@"(p)\\1?aygate", "backreference \\1")]
    [InlineData("(?>paygate)", "atomic group (?>")]
    [InlineData(@"paygate\\.demo\\Z", "end-of-string anchor \\Z")]
    [InlineData(@"paygate\\.demo", null)]
    public void PackValidator_PartnerRegexRejectedExactlyLikeThePlatform(string regex, string? construct)
    {
        using var pack = new PackCopy();
        pack.Write("profile.json", pack.Read("profile.json").Replace(
            "{ \"kind\": \"dependency-target-contains\", \"value\": \"paygate.demo\" }",
            $"{{ \"kind\": \"regex\", \"value\": \"{regex}\" }}", StringComparison.Ordinal));

        var errors = PackValidator.Validate(pack.Dir);

        if (construct is null)
        {
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
        else
        {
            var error = Assert.Single(errors);
            Assert.StartsWith("profile.json: $.partners[0].matchers[0].value regex", error, StringComparison.Ordinal);
            Assert.Contains(construct, error, StringComparison.Ordinal);
            Assert.Contains("partner-regex-unsupported", error, StringComparison.Ordinal);
        }
    }
}
