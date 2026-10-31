using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Onboarding;
using JourneyMonitor.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Onboarding;

public class TelemetryResourceProfilerTests
{
    private const string Alias = TelemetrySourceDefaults.Club;
    private const long SampledRows = 50_000;

    [Fact]
    public async Task TableWithRowsAndKeysIsOk()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(SampledRows),
            ProbeKind.Dimensions => [ProbeRows.Dimension("VacationClubId", 45_000, 45_000, 40, 5, SampledRows)],
            _ => [],
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));
        var table = Assert.Single(profile.Tables);

        Assert.Equal(TelemetryProbeState.Ok, table.RowCountState);
        Assert.Equal(TelemetryProbeState.Ok, table.DimensionState);
        Assert.Equal(SampledRows, table.RowCount);
        Assert.Equal(SampledRows, table.SampledRows);
        Assert.Null(table.RowCountError);
        Assert.Equal("VacationClubId", Assert.Single(table.Dimensions).Key);
        Assert.Equal(TelemetryProfileStatus.Complete, profile.Status);
    }

    /// <summary>
    /// "No rows in the window" is a finding about the customer's instrumentation, not a failure of
    /// ours, and a caller that cannot tell the two apart will read a broken query as a quiet system.
    /// </summary>
    [Fact]
    public async Task EmptyTableIsNoDataAndSkipsTheDimensionProbe()
    {
        var source = new ScriptedTelemetrySource(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(0),
            _ => [],
        });

        var profile = await Build(source).ProfileAsync(Request(TelemetryProbeTables.Requests));
        var table = Assert.Single(profile.Tables);

        Assert.Equal(TelemetryProbeState.NoData, table.RowCountState);
        Assert.Equal(TelemetryProbeState.Skipped, table.DimensionState);
        Assert.Null(table.RowCountError);
        Assert.Empty(table.Dimensions);
        Assert.Equal(TelemetryProfileStatus.Empty, profile.Status);
        Assert.DoesNotContain(source.ExecutedKql, k => ProbeKind.Of(k) == ProbeKind.Dimensions);
    }

    [Fact]
    public async Task MissingTableIsTableAbsentNotFailed()
    {
        var profiler = Build(kql =>
        {
            if (ProbeKind.TableOf(kql) == TelemetryProbeTables.Traces)
            {
                throw new InvalidOperationException(
                    "App Insights query 400: {\"error\":{\"message\":\"Failed to resolve table or column expression named 'traces'\",\"code\":\"BadArgumentError\"}}");
            }
            return ProbeKind.Of(kql) == ProbeKind.RowCount ? ProbeRows.Count(10) : [];
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Traces));
        var table = Assert.Single(profile.Tables);

        Assert.Equal(TelemetryProbeState.TableAbsent, table.RowCountState);
        Assert.Equal(TelemetryProbeState.Skipped, table.DimensionState);
        Assert.Contains("Failed to resolve table", table.RowCountError);
        Assert.Equal(TelemetryProfileStatus.Empty, profile.Status);
        Assert.Contains(profile.Warnings, w => w.Contains("does not exist in this resource", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryFailureIsFailedAndIsNotMistakenForEmpty()
    {
        var profiler = Build(_ => throw new InvalidOperationException(
            "App Insights query 403: {\"error\":{\"message\":\"The provided credentials have insufficient access\"}}"));

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));
        var table = Assert.Single(profile.Tables);

        Assert.Equal(TelemetryProbeState.Failed, table.RowCountState);
        Assert.Contains("insufficient access", table.RowCountError);
        Assert.Equal(TelemetryProfileStatus.Degraded, profile.Status);
        Assert.Contains(profile.Warnings, w => w.Contains("not evidence of absence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneFailingTableDoesNotFailTheWholeReport()
    {
        var profiler = Build(kql =>
        {
            var table = ProbeKind.TableOf(kql);
            if (table == TelemetryProbeTables.Exceptions) throw new HttpRequestException("504 Gateway Timeout");

            return ProbeKind.Of(kql) switch
            {
                ProbeKind.RowCount => ProbeRows.Count(SampledRows),
                ProbeKind.Dimensions => [ProbeRows.Dimension("VacationClubId", 45_000, 45_000, 40, 5, SampledRows)],
                ProbeKind.Hosts => [ProbeRows.Host("requests", "club-web", "www.example.com", 900)],
                _ => [],
            };
        });

        var profile = await profiler.ProfileAsync(
            Request(TelemetryProbeTables.Requests, TelemetryProbeTables.Exceptions, TelemetryProbeTables.Dependencies));

        Assert.Equal(3, profile.Tables.Count);
        Assert.Equal(TelemetryProbeState.Failed, profile.Tables.Single(t => t.Table == TelemetryProbeTables.Exceptions).RowCountState);
        Assert.Equal(TelemetryProbeState.Ok, profile.Tables.Single(t => t.Table == TelemetryProbeTables.Requests).RowCountState);
        Assert.Equal(TelemetryProbeState.Ok, profile.Tables.Single(t => t.Table == TelemetryProbeTables.Dependencies).RowCountState);

        // The surviving tables were still measured, and the host probe still ran.
        Assert.Contains(profile.IdentityCandidates, c => c.Table == TelemetryProbeTables.Requests && c.Key == "VacationClubId");
        Assert.Equal(TelemetryProbeState.Ok, profile.Hosts.State);
        Assert.Equal(TelemetryProfileStatus.Degraded, profile.Status);
    }

    /// <summary>The 4%-coverage key the whole component exists to surface: reported, not dropped, not promoted.</summary>
    [Fact]
    public async Task CoveragePercentagesAreMeasuredAgainstTheSampledRowCount()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(SampledRows),
            ProbeKind.Dimensions =>
            [
                // Present on 10% of rows, but blank on more than half of those.
                ProbeRows.Dimension("PartnerId", 5_000, 2_000, 30, 6, SampledRows),
            ],
            _ => [],
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));
        var dimension = Assert.Single(Assert.Single(profile.Tables).Dimensions);

        Assert.Equal(10.0, dimension.CoveragePercent);
        Assert.Equal(4.0, dimension.NonEmptyCoveragePercent);
        Assert.Equal(2_000, dimension.NonEmptyCount);
        Assert.Contains(profile.Warnings, w => w.Contains("would stay unattributed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampledRowCountIsCappedAndTheCapIsReported()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(4_000_000),
            ProbeKind.Dimensions => [ProbeRows.Dimension("ClubId", 1_000, 1_000, 40, 5, 1_000)],
            _ => [],
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests) with { MaxSampledRows = 1_000 });
        var table = Assert.Single(profile.Tables);

        Assert.Equal(4_000_000, table.RowCount);
        Assert.Equal(1_000, table.SampledRows);
        Assert.Equal(100.0, Assert.Single(table.Dimensions).NonEmptyCoveragePercent);
        Assert.Contains(profile.Warnings, w => w.Contains("probe cap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CandidatesAreRankedAcrossTablesAndNothingIsDropped()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(SampledRows),
            ProbeKind.Dimensions =>
            [
                ProbeRows.Dimension("VacationClubId", 45_000, 45_000, 40, 5, SampledRows),
                ProbeRows.Dimension("OperationCorrelationId", 50_000, 50_000, 50_000, 36, SampledRows),
                ProbeRows.Dimension("IsAuthenticated", 50_000, 50_000, 2, 5, SampledRows),
                ProbeRows.Dimension("ErrorMessage", 500, 500, 480, 400, SampledRows),
            ],
            _ => [],
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));

        Assert.Equal(
            ["VacationClubId", "IsAuthenticated", "OperationCorrelationId", "ErrorMessage"],
            profile.IdentityCandidates.Select(c => c.Key).ToArray());
        Assert.All(profile.IdentityCandidates, c => Assert.Equal(TelemetryProbeTables.Requests, c.Table));
        Assert.All(profile.IdentityCandidates, c => Assert.NotEmpty(c.Reasons));
    }

    [Fact]
    public async Task RowsWithoutAnyCustomDimensionsAreNoDataNotSkipped()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) == ProbeKind.RowCount ? ProbeRows.Count(120) : []);

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));
        var table = Assert.Single(profile.Tables);

        Assert.Equal(TelemetryProbeState.Ok, table.RowCountState);
        Assert.Equal(TelemetryProbeState.NoData, table.DimensionState);
        Assert.Equal(120, table.SampledRows);
        Assert.Contains(profile.Warnings, w => w.Contains("carry no customDimensions keys at all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostsAreAggregatedAcrossTheUnionedTables()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(10),
            ProbeKind.Hosts =>
            [
                ProbeRows.Host("requests", "club-web", "www.example.com", 900),
                ProbeRows.Host("pageViews", "club-web", "www.example.com", 100),
                ProbeRows.Host("requests", "club-api", "api.example.com", 50),
            ],
            _ => [],
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));

        Assert.Equal(TelemetryProbeState.Ok, profile.Hosts.State);
        Assert.Equal(
            [("www.example.com", 1_000L), ("api.example.com", 50L)],
            profile.Hosts.Hosts.Select(h => (h.Host, h.ObservationCount)).ToArray());
        Assert.Equal(
            [("club-web", 1_000L), ("club-api", 50L)],
            profile.Hosts.RoleNames.Select(r => (r.RoleName, r.ObservationCount)).ToArray());
    }

    [Fact]
    public async Task HostProbeFailureIsReportedWithoutFailingTheReport()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) switch
        {
            ProbeKind.RowCount => ProbeRows.Count(10),
            ProbeKind.Hosts => throw new InvalidOperationException("App Insights query 429: rate limit"),
            _ => [],
        });

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));

        Assert.Equal(TelemetryProbeState.Failed, profile.Hosts.State);
        Assert.Contains("rate limit", profile.Hosts.Error);
        Assert.Empty(profile.Hosts.Hosts);
        Assert.Equal(TelemetryProfileStatus.Degraded, profile.Status);
    }

    [Fact]
    public async Task UnknownAliasIsSourceUnresolvedAndQueriesNothing()
    {
        var source = new ScriptedTelemetrySource(_ => throw new InvalidOperationException("must not be queried"));
        var profile = await Build(source).ProfileAsync(Request(TelemetryProbeTables.Requests) with { Alias = "not-registered" });

        Assert.Equal(TelemetryProfileStatus.SourceUnresolved, profile.Status);
        Assert.Contains("No telemetry source registered", profile.Error);
        Assert.Empty(profile.Tables);
        Assert.Empty(source.ExecutedKql);
    }

    [Fact]
    public async Task SourceWithoutAnAppIdSaysWhichFieldIsMissing()
    {
        var store = new FakeTelemetrySourceStore(
            new TelemetrySourceEntry(Alias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "resource-id", AppId: null));
        var profiler = new TelemetryResourceProfiler(
            new ScriptedTelemetrySource(_ => []), store, new FakeKqlSourceResolver(),
            NullLogger<TelemetryResourceProfiler>.Instance);

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests));

        Assert.Equal(TelemetryProfileStatus.SourceUnresolved, profile.Status);
        Assert.Contains("has no AppId", profile.Error);
    }

    [Fact]
    public async Task UnknownTableNamesAreRejectedRatherThanConcatenatedIntoKql()
    {
        var source = new ScriptedTelemetrySource(kql => ProbeKind.Of(kql) == ProbeKind.RowCount ? ProbeRows.Count(5) : []);

        var profile = await Build(source).ProfileAsync(
            Request(TelemetryProbeTables.Requests) with { Tables = ["requests | project 1 //", "requests"] });

        Assert.Equal(TelemetryProbeTables.Requests, Assert.Single(profile.Tables).Table);
        Assert.Contains(profile.Warnings, w => w.Contains("not a probe-able telemetry table", StringComparison.Ordinal));
        Assert.DoesNotContain(source.ExecutedKql, k => k.Contains("project 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WindowHoursAreClampedSoAProbeCannotBecomeAnExpensiveQuery()
    {
        var profiler = Build(kql => ProbeKind.Of(kql) == ProbeKind.RowCount ? ProbeRows.Count(1) : []);

        var profile = await profiler.ProfileAsync(Request(TelemetryProbeTables.Requests) with { WindowHours = 100_000 });

        Assert.Equal(TelemetryProfileDefaults.MaxWindowHours, profile.WindowHours);
        Assert.Equal(TelemetryProfileDefaults.MaxWindowHours, (profile.WindowTo - profile.WindowFrom).TotalHours);
    }

    private static TelemetryProfileRequest Request(params string[] tables) =>
        new(Alias, InsightsEnvironment.Production, WindowHours: 24, WindowTo: new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), Tables: tables);

    private static TelemetryResourceProfiler Build(Func<string, IReadOnlyList<TelemetryJourneyRow>> respond) =>
        Build(new ScriptedTelemetrySource(respond));

    private static TelemetryResourceProfiler Build(ScriptedTelemetrySource source) =>
        new(
            source,
            new FakeTelemetrySourceStore(new TelemetrySourceEntry(
                Alias,
                InsightsEnvironment.Production,
                TelemetrySourceKind.AppInsights,
                "/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/ovs-club-dev/providers/microsoft.insights/components/appInsights-ovs-club",
                AppId: "8644ddf0-29b2-4283-87c0-7a9a514aefb5",
                TenantId: "ea4d58b6-6980-4312-be9a-ab68edfa574c")),
            new FakeKqlSourceResolver(),
            NullLogger<TelemetryResourceProfiler>.Instance);
}
