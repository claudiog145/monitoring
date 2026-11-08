using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dapper;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.ProfileJourneys;
using JourneyMonitor.Infrastructure.Sql;
using JourneyMonitor.Tests.Spec1000.A3;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.MultiCompany.Fix2;

/// <summary>FXM2 (MCRV findings M2, M3, L1): journey completion semantics and the overview's response time.</summary>
public class Fix2Tests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(-20);

    [Fact]
    public void Journeys_SingleStepIsNotCompletedUnlessFlowHasOneStep()
    {
        var batch = ProfileJourneyBuilder.Build(Shop(),
        [
            // A lone request on the last step of a three-step flow: the user was never seen entering the flow.
            Request("lone-last", 0, "pay"),
            // Entered at step 2 and reached the last step: not a completion either.
            Request("entered", 0, "cart"),
            Request("entered", 60, "pay"),
            // The whole flow, one step per minute.
            Request("walked", 0, "search"),
            Request("walked", 60, "cart"),
            Request("walked", 120, "pay"),
            // Skipping a middle step still walks the flow from its start to its end.
            Request("skipped", 0, "search"),
            Request("skipped", 60, "pay"),
            // A one-step flow is completed by its only step.
            Request("one-step", 0, "track"),
            // A single first step of a longer flow is a drop at step 1.
            Request("lone-first", 0, "search"),
        ], 500);

        var lone = Journey(batch, "lone-last");
        Assert.Equal(1, lone.StepCount);
        Assert.Equal(3, lone.LastStepOrder);
        Assert.Equal(ProfileJourneyStatus.DroppedAt(3), lone.Status);

        Assert.Equal(ProfileJourneyStatus.DroppedAt(3), Journey(batch, "entered").Status);
        Assert.Equal(ProfileJourneyStatus.Completed, Journey(batch, "walked").Status);
        Assert.Equal(ProfileJourneyStatus.Completed, Journey(batch, "skipped").Status);
        Assert.Equal(ProfileJourneyStatus.DroppedAt(1), Journey(batch, "lone-first").Status);

        var oneStep = Journey(batch, "one-step");
        Assert.Equal("tracking", oneStep.FlowKey);
        Assert.Equal(1, oneStep.StepCount);
        Assert.Equal(ProfileJourneyStatus.Completed, oneStep.Status);

        // Completion only counts journeys that walked their flow: never a single step of a longer flow.
        Assert.Equal(3, batch.Journeys.Count(j => j.Status == ProfileJourneyStatus.Completed));
        Assert.DoesNotContain(batch.Journeys, j => j.Status == ProfileJourneyStatus.Completed && j.StepCount == 1 && j.FlowKey != "tracking");
    }

    [Fact]
    public async Task Overview_LatencyIsRequestDurationNotSessionSpan()
    {
        // A three-step session: the user spends a minute on each page, the server answers in 200/300/400 ms.
        var batch = ProfileJourneyBuilder.Build(Shop(),
        [
            Request("session-1", 0, "search", 200),
            Request("session-1", 60, "cart", 300),
            Request("session-1", 120, "pay", 400),
            Request("session-2", 0, "track", 500),
        ], 500);

        var session = Journey(batch, "session-1");
        Assert.True((session.EndedAt - session.StartedAt).TotalMilliseconds > 120_000, "the session still spans its think time");
        // The journey duration (what the service cards' "Avg latency" averages) is the requests' time, not the span.
        Assert.Equal(900, session.DurationMs);
        Assert.Equal(500, Journey(batch, "session-2").DurationMs);
        Assert.Equal([200.0, 300.0, 400.0], batch.Steps.Where(s => s.JourneyId == "session-1").OrderBy(s => s.StepOrder).Select(s => s.DurationMs));

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        var name = "FXM2Lat_" + Guid.NewGuid().ToString("N")[..16];
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using (var admin = new SqlConnection(master))
        {
            await admin.ExecuteAsync($"CREATE DATABASE [{name}];");
        }
        try
        {
            var scratch = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name, Pooling = false }.ConnectionString;
            await using (var connection = new SqlConnection(scratch))
            {
                await connection.OpenAsync();
                var script = File.ReadAllText(Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringProfileJourney.sql"));
                foreach (var part in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(part)) await connection.ExecuteAsync(part, commandTimeout: 120);
                }

                // A row written before the fix: its DurationMs is the session span (four minutes).
                await connection.ExecuteAsync(
                    """
                    INSERT INTO monitoring.ProfileJourney
                        (ProfileKey, JourneyId, ServiceKey, EntityKey, StartedAt, EndedAt, DurationMs, Status, StepCount, Failed)
                    VALUES (N'shop', N'legacy-span', N'storefront', N'S-1', @At, DATEADD(minute, 4, @At), 240000, N'Completed', 2, 0);
                    INSERT INTO monitoring.ProfileJourneyStep (ProfileKey, JourneyId, StepOrder, StepKey, Name, [Timestamp], DurationMs, Success)
                    VALUES (N'shop', N'legacy-span', 1, N'search', N'GET /search', @At, 100, 1),
                           (N'shop', N'legacy-span', 2, N'pay', N'POST /pay', DATEADD(minute, 4, @At), 500, 1);
                    """,
                    new { At = Now });
            }

            var options = A3SqlTestDatabase.Options(scratch);
            await new SqlProfileJourneyStore(options).UpsertAsync("shop", batch.Journeys, batch.Steps, CancellationToken.None);

            var overview = await new SqlOverviewReader(options).ReadAsync(
                new ProfileOverviewQuery("shop", false, null, null, "production", Now.AddHours(-1), Now.AddHours(-24), null, 0),
                CancellationToken.None);

            // Six requests: 200, 300, 400, 500, 100 and 500 ms. The session spans (120 s and 240 s) never enter it.
            Assert.Equal(6, overview.Counts.AvailabilityTotalCalls);
            Assert.NotNull(overview.Counts.AverageDurationMs);
            Assert.Equal(2000.0 / 6, overview.Counts.AverageDurationMs!.Value, 6);

            var tile = ProfileOverviewComposer.Tiles(overview.Counts, incumbent: false, new OverviewLabels("Service", "Journey", "Step", "Entity"))
                .Single(t => t.Label == "Avg. End-to-End Response");
            Assert.Equal("333 ms", tile.Value);
        }
        finally
        {
            await using var admin = new SqlConnection(master);
            await admin.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    [Fact]
    public void Overview_NotesOfOtherCompaniesUseNeutralVocabulary()
    {
        var notes = ProfileOverviewComposer.Widgets(incumbent: false)
            .Where(w => !w.Applicable)
            .Select(w => w.Note ?? "")
            .ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, note =>
        {
            Assert.Contains("original company", note, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex("(?i)fabric|club|incumbent|hotel"), note);
        });
        Assert.All(ProfileOverviewComposer.Widgets(incumbent: true), w => Assert.Null(w.Note));
    }

    private static ProfileJourney Journey(ProfileJourneyBatch batch, string id) => Assert.Single(batch.Journeys, j => j.JourneyId == id);

    private static CompanyProfile Shop() => new(
        "shop",
        "Shop (test)",
        new CompanyLabels("Platform", "Journey", "Step", "Site", "Squad"),
        [
            new ServiceDefinition("storefront", "Storefront", ["shop-web"], [],
            [
                new FlowDefinition("purchase", "Purchase", [Step("search", 1, "/search"), Step("cart", 2, "/cart"), Step("pay", 3, "/pay")]),
                new FlowDefinition("tracking", "Tracking", [Step("track", 4, "/track")]),
            ]),
        ],
        [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "SiteId", "site")],
        new CompanyGlossary("platform", "journey", "step", "site", "squad"));

    private static StepDefinition Step(string key, int order, string path) =>
        new(key, key, order, [new StepMatcher(StepMatcherKind.PathContains, path)]);

    private static ProfileJourneyRequest Request(string session, int second, string step, double durationMs = 100) =>
        new(session, Now.AddSeconds(second), $"GET /{step}", durationMs, true, "200", "storefront", step, "S-1", "op-" + session + "-" + step);

    private static string FindPortalDirectory([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
        {
            for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docker", "init-sql.sh"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Portal", "docker", "init-sql.sh"))) return Path.Combine(dir.FullName, "Portal");
            }
        }
        throw new InvalidOperationException("Could not find the Portal directory (docker/init-sql.sh).");
    }
}
