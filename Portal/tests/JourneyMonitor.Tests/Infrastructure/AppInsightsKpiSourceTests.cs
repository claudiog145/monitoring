using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;

namespace JourneyMonitor.Tests.Spec1000.A2;

public sealed class AppInsightsKpiSourceTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 4, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    private sealed class A2CapturingTelemetrySource(IReadOnlyList<TelemetryJourneyRow>? rows = null) : ITelemetrySource
    {
        public List<TelemetryQueryRequest> Requests { get; } = [];

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(rows ?? []);
        }
    }

    private static AppInsightsKpiSource Source(ITelemetrySource telemetry) => KpiSourceDoubles.Source(telemetry);

    private static TelemetryJourneyRow Row(string json) => JsonSerializer.Deserialize<TelemetryJourneyRow>(json)!;

    [Fact]
    public async Task Source_ReplacesAllTokensWithValidatedValues()
    {
        var telemetry = new A2CapturingTelemetrySource();
        var source = Source(telemetry);
        var profile = KpiSourceDoubles.Incumbent();

        foreach (var sourceKey in new[] { "appinsights:latency", "appinsights:exceptions" })
        {
            foreach (var domain in KpiDomains.All)
            {
                var template = AppInsightsKpiSource.LoadTemplate(sourceKey, domain);
                foreach (var token in new[] { "{{from}}", "{{to}}", "{{bucket}}", "{{perBucket}}", "{{stage}}", "{{grain}}", "{{domain}}", "{{maxEntities}}" })
                {
                    Assert.Contains(token, template);
                }
                foreach (var grain in new[] { KpiGrain.Domain, KpiGrain.Entity })
                {
                    var definition = KpiSourceDoubles.Definition(sourceKey, domain, "02 Hotel Search", grain);
                    var kql = AppInsightsKpiSource.RenderKql(template, profile, definition, From, To, 167);
                    var resolved = KqlSourceResolver.PlaceholderPattern.Replace(kql, "/res/x");
                    Assert.DoesNotContain("{{", resolved);
                    Assert.DoesNotContain("}}", resolved);
                }
            }
        }

        await source.FetchAsync(A2Kpi.Definition(sourceKey: "appinsights:latency", stage: "02 Hotel Search"), From, To, 167);
        var request = Assert.Single(telemetry.Requests);
        Assert.Equal("hotel-app", request.AppInsightsAppId);
        Assert.DoesNotContain("{{", request.Kql);
        Assert.Contains("let fromTs = datetime(2026-11-04T14:00:00Z);", request.Kql);
        Assert.Contains("let toTs = datetime(2026-11-05T14:00:00Z);", request.Kql);
        Assert.Contains("let bucketSize = 1h;", request.Kql);
        Assert.Contains("(sample 167)", request.Kql);
        Assert.Contains("let stageFilter = \"02 Hotel Search\";", request.Kql);

        Assert.Equal("5m", AppInsightsKpiSource.FormatTimespan(TimeSpan.FromMinutes(5)));
        Assert.Equal("1d", AppInsightsKpiSource.FormatTimespan(TimeSpan.FromDays(1)));
        Assert.Equal("90s", AppInsightsKpiSource.FormatTimespan(TimeSpan.FromSeconds(90)));
        Assert.Equal("\"\"", AppInsightsKpiSource.FormatStage(""));
        Assert.Equal("\"04 Rooms & Rates\"", AppInsightsKpiSource.FormatStage("04 Rooms & Rates"));
        Assert.Equal("\"05 Review & Pay\"", AppInsightsKpiSource.FormatStage("05 Review & Pay"));
        Assert.Contains("let stageFilter = \"04 Rooms & Rates\";",
            AppInsightsKpiSource.BuildKql("let stageFilter = {{stage}};", From, To, TimeSpan.FromHours(1), 10, "04 Rooms & Rates"));

        // Unvalidated values never reach the KQL text.
        var hotelLatency = CompanyKqlTemplater.RenderForService(
            AppInsightsKpiSource.LoadTemplate("appinsights:latency", "hotels"), profile, "hotels");
        string Build(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, int perBucket, string stage) =>
            AppInsightsKpiSource.BuildKql(hotelLatency, from, to, bucket, perBucket, stage, KpiGrain.Domain, "hotels");
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromHours(1), 10, "x' or 1==1"));
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromHours(1), 10, "a; requests | take 1"));
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromHours(1), 10, "a\"b"));
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromHours(1), 10, "abc\n"));
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromHours(1), 10, "a\\b"));
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromHours(1), 10, new string('a', 65)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(From, To, TimeSpan.FromHours(1), 100_001, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(From, To, TimeSpan.FromHours(1), -1, ""));
        Assert.Throws<ArgumentException>(() => Build(From, To, TimeSpan.FromMilliseconds(1500), 10, ""));
        Assert.Throws<ArgumentException>(() => Build(To, From, TimeSpan.FromHours(1), 10, ""));
        Assert.Throws<ArgumentException>(() => AppInsightsKpiSource.BuildKql(hotelLatency, From, To, TimeSpan.FromHours(1), 10, "", KpiGrain.Domain, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppInsightsKpiSource.BuildKql(hotelLatency, From, To, TimeSpan.FromHours(1), 10, "", KpiGrain.Entity, "hotels", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppInsightsKpiSource.BuildKql(hotelLatency, From, To, TimeSpan.FromHours(1), 10, "", KpiGrain.Entity, "hotels", 5001));
        Assert.Throws<InvalidOperationException>(() => AppInsightsKpiSource.BuildKql("{{unknown}}", From, To, TimeSpan.FromHours(1), 10, ""));
        Assert.Throws<InvalidOperationException>(() => AppInsightsKpiSource.BuildKql(
            AppInsightsKpiSource.LoadTemplate("appinsights:latency", "hotels"), From, To, TimeSpan.FromHours(1), 10, "", KpiGrain.Domain, "hotels"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.FetchAsync(A2Kpi.Definition(sourceKey: "appinsights:latency", stage: "x'; drop"), From, To, 10));
        Assert.Single(telemetry.Requests);
    }

    [Fact]
    public void Source_KqlEscapesReservedKindColumn()
    {
        foreach (var sourceKey in new[] { "appinsights:latency", "appinsights:exceptions" })
        {
            foreach (var domain in KpiDomains.All)
            {
                var template = AppInsightsKpiSource.LoadTemplate(sourceKey, domain);
                var code = string.Join('\n', template.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

                Assert.Contains("project ['kind'] = \"bucket\"", code);
                Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(?<!\[')\bkind\s*=\s*""|project\s+kind\s*,"), code);

                // `kind` is a reserved word: outside join kind=..., it may only appear as ['kind'].
                var remaining = System.Text.RegularExpressions.Regex.Replace(code, @"join\s+kind\s*=", "");
                remaining = remaining.Replace("['kind']", "");
                Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\bkind\b"), remaining);
            }
        }

        foreach (var domain in KpiDomains.All)
        {
            var latency = AppInsightsKpiSource.LoadTemplate("appinsights:latency", domain);
            Assert.Contains("project ['kind'] = \"sample\"", latency);
            Assert.Contains("| project ['kind'], BucketStart, TotalCount, Value, Timestamp, OperationId", latency);
        }
    }

    [Fact]
    public async Task Source_MapsBucketAndSampleRows()
    {
        var telemetry = new A2CapturingTelemetrySource(
        [
            Row("""{"kind":"bucket","BucketStart":"2026-11-05T12:00:00Z","TotalCount":1200,"Value":null,"Timestamp":null,"OperationId":""}"""),
            Row("""{"kind":"bucket","BucketStart":"2026-11-05T13:00:00Z","TotalCount":800,"Value":2.5,"Timestamp":null,"OperationId":""}"""),
            Row("""{"kind":"sample","BucketStart":"2026-11-05T12:00:00Z","TotalCount":0,"Value":431.5,"Timestamp":"2026-11-05T12:14:03.123Z","OperationId":"op-1"}"""),
            Row("""{"kind":"sample","BucketStart":"2026-11-05T13:00:00Z","TotalCount":0,"Value":88,"Timestamp":"2026-11-05T13:59:59Z","OperationId":""}"""),
            Row("""{"kind":"sample","BucketStart":"2026-11-05T13:00:00Z","TotalCount":0,"Value":null,"Timestamp":"2026-11-05T13:10:00Z","OperationId":"op-null"}"""),
            Row("""{"kind":"other","BucketStart":"2026-11-05T13:00:00Z","TotalCount":5}"""),
        ]);
        var source = Source(telemetry);

        var result = await source.FetchAsync(A2Kpi.Definition(id: 9, sourceKey: "appinsights:latency"), From, To, 167);

        Assert.Equal(2, result.Buckets.Count);
        Assert.Equal(new KpiSourceBucket(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), 1200, null), result.Buckets[0]);
        Assert.Equal(new KpiSourceBucket(new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero), 800, 2.5), result.Buckets[1]);
        Assert.All(result.Buckets, b => Assert.Equal(TimeSpan.Zero, b.BucketStart.Offset));

        Assert.Equal(2, result.Samples.Count);
        var first = result.Samples[0];
        Assert.Equal(9, first.KpiDefinitionId);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), first.BucketStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 14, 3, 123, TimeSpan.Zero), first.Timestamp);
        Assert.Equal(431.5, first.Value);
        Assert.Equal("op-1", first.OperationId);
        Assert.Null(result.Samples[1].OperationId);
        Assert.Equal(88, result.Samples[1].Value);
    }

    [Fact]
    public async Task Source_UnconfiguredDomain_ThrowsUnavailable()
    {
        var telemetry = new A2CapturingTelemetrySource();
        var source = Source(telemetry);

        Assert.True(source.CanHandle("appinsights:latency"));
        Assert.True(source.CanHandle("appinsights:exceptions"));
        Assert.False(source.CanHandle("fabric:bookings"));

        await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(A2Kpi.Definition(sourceKey: "appinsights:latency", domain: "air"), From, To, 10));
        await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(A2Kpi.Definition(sourceKey: "appinsights:exceptions", domain: "cruises"), From, To, 0));
        Assert.Empty(telemetry.Requests);

        // Cars reuses the Hotel app.
        await source.FetchAsync(A2Kpi.Definition(sourceKey: "appinsights:exceptions", domain: "cars"), From, To, 0);
        Assert.Equal("hotel-app", Assert.Single(telemetry.Requests).AppInsightsAppId);
    }
}
