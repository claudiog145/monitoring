using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.AppInsights;
using static JourneyMonitor.Tests.Spec1000.A2.KpiSourceDoubles;

namespace JourneyMonitor.Tests.Spec1000.A2;

/// <summary>
/// Contract v4 §2 (task SD2): the App Insights KPI queries are rendered from the company profile of
/// the definition, cover every host of the service, and have an entity variant that returns
/// aggregated statistics instead of samples.
/// </summary>
public sealed class AppInsightsKpiSourceProfileTests
{
    private static readonly string[] SourceKeys = [AppInsightsKpiSource.LatencySourceKey, AppInsightsKpiSource.ExceptionsSourceKey];

    [Fact]
    public async Task KpiSource_RendersFromProfile_NoHostLiteral()
    {
        foreach (var sourceKey in SourceKeys)
        {
            // The shipped template carries no company: no host, no step, no stage name.
            var template = AppInsightsKpiSource.LoadTemplate(sourceKey, "hotels");
            var code = Code(template);
            Assert.DoesNotMatch(HostLiteral, template);
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"""\d\d [A-Z]"), code);
            Assert.DoesNotContain("url has", code, StringComparison.Ordinal);
            foreach (var placeholder in new[] { KqlPlaceholders.SourceUnion, KqlPlaceholders.HostFilter, KqlPlaceholders.EntityIdentity, KqlPlaceholders.ServiceAndStepClassifier })
            {
                Assert.Contains(placeholder, code, StringComparison.Ordinal);
            }
            // One template serves every service of every profile.
            Assert.Equal(template, AppInsightsKpiSource.LoadTemplate(sourceKey, "a-service-no-file-names"));

            var telemetry = new CapturingTelemetrySource();
            await Source(telemetry).FetchAsync(Definition(sourceKey, "hotels", "02 Hotel Search"), From, To, 50);
            var kql = Assert.Single(telemetry.Requests).Kql;

            // Read through the profile's telemetry source for the service, resolved by IKqlSourceResolver.
            Assert.Contains("app(\"/res/web-front\").requests", kql, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", kql, StringComparison.Ordinal);
            Assert.DoesNotContain("sea-core", kql, StringComparison.Ordinal);
            // The profile's classifier decides the stage, and the domain is the service key.
            Assert.Contains("tolower(tostring(parse_url(url).Path)) contains \"search.json\"", kql, StringComparison.Ordinal);
            Assert.Contains("let serviceKey = \"hotels\";", kql, StringComparison.Ordinal);
            Assert.Contains("| where ServiceKey == serviceKey", kql, StringComparison.Ordinal);
            // The profile's identity chain names the entity.
            Assert.Contains("tostring(customDimensions.MemberGroupId)", kql, StringComparison.Ordinal);
            if (sourceKey == AppInsightsKpiSource.ExceptionsSourceKey)
            {
                Assert.Contains("app(\"/res/web-front\").exceptions", kql, StringComparison.Ordinal);
            }
            Assert.DoesNotMatch(HostLiteral, kql);
        }
    }

    [Fact]
    public async Task KpiSource_DomainGrain_CoversAllProfileHosts()
    {
        foreach (var sourceKey in SourceKeys)
        {
            // A service without host filters accepts every host: nothing narrows the domain.
            var incumbent = AppInsightsKpiSource.RenderKql(
                AppInsightsKpiSource.LoadTemplate(sourceKey, "hotels"), Incumbent(), Definition(sourceKey), From, To, 10);
            Assert.Contains("| where true", incumbent, StringComparison.Ordinal);
            Assert.DoesNotContain("Host =~", incumbent, StringComparison.Ordinal);

            // A service that declares hosts covers all of them (and their subdomains), and only its own.
            var acme = HostScoped();
            var telemetry = new CapturingTelemetrySource();
            await Source(telemetry, acme).FetchAsync(Definition(sourceKey, profileKey: acme.ProfileKey), From, To, 10);
            var kql = Assert.Single(telemetry.Requests).Kql;
            Assert.Contains("| where (Host =~ \"rooms.acme.test\" or Host endswith \".rooms.acme.test\" or Host =~ \"rooms-eu.acme.test\" or Host endswith \".rooms-eu.acme.test\")", kql, StringComparison.Ordinal);
            var hostFilterLine = kql.Split('\n').Single(l => l.TrimStart().StartsWith("| where (Host", StringComparison.Ordinal));
            Assert.DoesNotContain("pay.acme.test", hostFilterLine, StringComparison.Ordinal);
            Assert.Contains("app(\"/res/acme-web\")", kql, StringComparison.Ordinal);
            Assert.DoesNotContain("app(\"/res/acme-billing\")", kql, StringComparison.Ordinal);

            // Domain grain keeps the v1 output contract: the domain rows, never the entity rows.
            Assert.Equal("domainRows", FinalStatement(kql));
            var domainRows = Code(kql).Split("let entityRows")[0];
            Assert.Contains("['kind'] = \"bucket\", BucketStart, TotalCount = tolong(", domainRows, StringComparison.Ordinal);
            Assert.DoesNotContain("EntityKey", domainRows, StringComparison.Ordinal);
            Assert.DoesNotContain("P95", domainRows, StringComparison.Ordinal);
        }

        var latency = AppInsightsKpiSource.RenderKql(
            AppInsightsKpiSource.LoadTemplate(AppInsightsKpiSource.LatencySourceKey, "hotels"), Incumbent(), Definition(), From, To, 10);
        Assert.Contains("| project ['kind'], BucketStart, TotalCount, Value, Timestamp, OperationId;", latency, StringComparison.Ordinal);
        Assert.Contains("(sample 10)", latency, StringComparison.Ordinal);

        // v1 mapping is unchanged: entity columns on a domain row are ignored, samples are kept.
        var rows = new[]
        {
            Row("""{"kind":"bucket","BucketStart":"2026-11-05T12:00:00Z","TotalCount":1200,"Value":null,"Timestamp":null,"OperationId":""}"""),
            Row("""{"kind":"sample","BucketStart":"2026-11-05T12:00:00Z","TotalCount":0,"Value":431.5,"Timestamp":"2026-11-05T12:14:03Z","OperationId":"op-1"}"""),
        };
        var result = await Source(new CapturingTelemetrySource(rows)).FetchAsync(Definition(), From, To, 10);
        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(new KpiSourceBucket(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), 1200, null), bucket);
        Assert.Equal("", bucket.EntityKey);
        Assert.Equal(431.5, Assert.Single(result.Samples).Value);
    }

    [Fact]
    public async Task KpiSource_EntityGrain_ReturnsEntityStats()
    {
        foreach (var sourceKey in SourceKeys)
        {
            var kql = AppInsightsKpiSource.RenderKql(
                AppInsightsKpiSource.LoadTemplate(sourceKey, "hotels"), Incumbent(), Definition(sourceKey, grain: KpiGrain.Entity), From, To, 10);
            Assert.Equal("entityRows", FinalStatement(kql));
            var entityRows = Code(kql).Split("let entityRows =")[1];
            Assert.Contains("sumif(itemCount, success == false)", Code(kql), StringComparison.Ordinal);
            Assert.Contains("by BucketStart, Entity", Code(kql), StringComparison.Ordinal);
            Assert.Contains("EntityKey = Entity,", entityRows, StringComparison.Ordinal);
            foreach (var column in new[] { "P50", "P95", "Mean", "FailedCount", "TotalCount", "Value" })
            {
                Assert.Contains(column, entityRows, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("\"sample\"", entityRows, StringComparison.Ordinal);
        }

        var latency = Code(AppInsightsKpiSource.RenderKql(
            AppInsightsKpiSource.LoadTemplate(AppInsightsKpiSource.LatencySourceKey, "hotels"), Incumbent(), Definition(grain: KpiGrain.Entity), From, To, 10));
        Assert.Contains("P50 = percentile(Ms, 50), P95 = percentile(Ms, 95), Mean = avg(Ms)", latency, StringComparison.Ordinal);

        var rows = new[]
        {
            Row("""{"kind":"bucket","BucketStart":"2026-11-05T12:00:00Z","EntityKey":"club-7","TotalCount":120,"Value":null,"P50":210.5,"P95":1900,"Mean":340.25,"FailedCount":4,"Timestamp":null,"OperationId":""}"""),
            Row("""{"kind":"bucket","BucketStart":"2026-11-05T12:00:00Z","EntityKey":"rooms.example.test","TotalCount":80,"Value":2.5,"P50":null,"P95":null,"Mean":null,"FailedCount":0,"Timestamp":null,"OperationId":""}"""),
            Row("""{"kind":"bucket","BucketStart":"2026-11-05T12:00:00Z","EntityKey":"","TotalCount":999,"Value":null}"""),
            Row($$"""{"kind":"bucket","BucketStart":"2026-11-05T12:00:00Z","EntityKey":"{{new string('x', 257)}}","TotalCount":5000,"Value":null}"""),
            Row("""{"kind":"sample","BucketStart":"2026-11-05T12:00:00Z","TotalCount":0,"Value":431.5,"Timestamp":"2026-11-05T12:14:03Z","OperationId":"op-1"}"""),
        };
        var telemetry = new CapturingTelemetrySource(rows);
        var result = await Source(telemetry).FetchAsync(Definition(grain: KpiGrain.Entity, id: 9), From, To, 10);

        Assert.Empty(result.Samples);
        Assert.Equal(2, result.Buckets.Count);
        var first = result.Buckets[0];
        Assert.Equal("club-7", first.EntityKey);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), first.BucketStart);
        Assert.Equal(120, first.TotalCount);
        Assert.Null(first.Value);
        Assert.Equal(210.5, first.P50);
        Assert.Equal(1900, first.P95);
        Assert.Equal(340.25, first.Mean);
        Assert.Equal(4, first.FailedCount);
        var second = result.Buckets[1];
        Assert.Equal("rooms.example.test", second.EntityKey);
        Assert.Equal(2.5, second.Value);
        Assert.Equal(0, second.FailedCount);
        Assert.Null(second.P95);
        Assert.Contains("entityRows", Assert.Single(telemetry.Requests).Kql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KpiSource_EntityGrain_RespectsMaxEntities()
    {
        foreach (var sourceKey in SourceKeys)
        {
            var kql = Code(AppInsightsKpiSource.RenderKql(
                AppInsightsKpiSource.LoadTemplate(sourceKey, "hotels"), Incumbent(), Definition(sourceKey, grain: KpiGrain.Entity, maxEntities: 2), From, To, 10));
            Assert.Contains("let maxEntities = 2;", kql, StringComparison.Ordinal);
            Assert.Contains("row_number(1, prev(BucketStart) != BucketStart)", kql, StringComparison.Ordinal);
            Assert.Contains("| where EntityRank <= maxEntities", kql, StringComparison.Ordinal);
            Assert.Matches(new System.Text.RegularExpressions.Regex(@"sort by BucketStart asc, (TotalCount|Requests) desc"), kql);
        }

        static string EntityRow(string hour, string key, int total) =>
            $$"""{"kind":"bucket","BucketStart":"2026-11-05T{{hour}}:00:00Z","EntityKey":"{{key}}","TotalCount":{{total}},"Value":1.0,"FailedCount":0}""";

        // The source trims an over-delivering query too: highest traffic first, per bucket.
        var rows = new[]
        {
            Row(EntityRow("12", "a", 5)),
            Row(EntityRow("12", "b", 50)),
            Row(EntityRow("12", "c", 500)),
            Row(EntityRow("12", "d", 1)),
            Row(EntityRow("13", "a", 7)),
        };
        var result = await Source(new CapturingTelemetrySource(rows))
            .FetchAsync(Definition(grain: KpiGrain.Entity, maxEntities: 2), From, To, 10);

        var noon = result.Buckets.Where(b => b.BucketStart.Hour == 12).ToList();
        Assert.Equal(["c", "b"], noon.Select(b => b.EntityKey));
        Assert.Equal("a", Assert.Single(result.Buckets, b => b.BucketStart.Hour == 13).EntityKey);
        Assert.Equal(3, result.Buckets.Count);
    }

    /// <summary>Contract v4 §6 A1: the key is the club/host alone; the stage belongs to the definition.</summary>
    [Fact]
    public void KpiSource_EntityGrain_KeyIsEntityOnly()
    {
        foreach (var sourceKey in SourceKeys)
        {
            foreach (var stage in new[] { "", "02 Hotel Search" })
            {
                var kql = Code(AppInsightsKpiSource.RenderKql(
                    AppInsightsKpiSource.LoadTemplate(sourceKey, "hotels"), Incumbent(), Definition(sourceKey, stage: stage, grain: KpiGrain.Entity), From, To, 10));
                var entityRows = kql.Split("let entityRows =")[1];

                Assert.Contains("EntityKey = Entity,", entityRows, StringComparison.Ordinal);
                Assert.DoesNotContain("strcat(Entity", kql, StringComparison.Ordinal);
                Assert.DoesNotContain("Stage", entityRows, StringComparison.Ordinal);
                Assert.DoesNotContain("|\"", entityRows, StringComparison.Ordinal);
                Assert.Contains("| extend Entity = case(isnotempty(Entity), Entity, isnotempty(Host), Host, unknownEntity)", kql, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Contract v4 §6 A1: an entity KPI with an empty Stage is one series per entity over every stage of the service.</summary>
    [Fact]
    public void KpiSource_EntityGrain_EmptyStageAggregatesAllStages()
    {
        foreach (var sourceKey in SourceKeys)
        {
            var kql = Code(AppInsightsKpiSource.RenderKql(
                AppInsightsKpiSource.LoadTemplate(sourceKey, "hotels"), Incumbent(), Definition(sourceKey, stage: "", grain: KpiGrain.Entity), From, To, 10));

            Assert.Contains("let stageFilter = \"\";", kql, StringComparison.Ordinal);
            Assert.Contains("| where isempty(stageFilter) or Stage == stageFilter", kql, StringComparison.Ordinal);
            // Every step of the service is kept, and the entity summaries never split by stage.
            Assert.Contains("| where ServiceKey == serviceKey and isnotempty(Step)", kql, StringComparison.Ordinal);
            var summaries = kql.Split('\n').Where(l => l.Contains(" by BucketStart", StringComparison.Ordinal) && !l.Contains("bin_at", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(summaries);
            Assert.All(summaries, line => Assert.DoesNotContain("Stage", line, StringComparison.Ordinal));
            Assert.Contains(summaries, line => line.TrimEnd().EndsWith("by BucketStart, Entity", StringComparison.Ordinal)
                || line.TrimEnd().EndsWith("by BucketStart, Entity;", StringComparison.Ordinal));
        }
    }

    /// <summary>Contract v4 §6 A1: a definition with a Stage only measures that stage, per entity.</summary>
    [Fact]
    public async Task KpiSource_EntityGrain_StageFilterApplied()
    {
        foreach (var sourceKey in SourceKeys)
        {
            var telemetry = new CapturingTelemetrySource();
            await Source(telemetry).FetchAsync(Definition(sourceKey, stage: "03 Hotel Results", grain: KpiGrain.Entity), From, To, 10);
            var kql = Code(Assert.Single(telemetry.Requests).Kql);

            Assert.Contains("let stageFilter = \"03 Hotel Results\";", kql, StringComparison.Ordinal);
            // The filter sits before any entity aggregation, so every entity series is that stage only.
            var filterAt = kql.IndexOf("| where isempty(stageFilter) or Stage == stageFilter", StringComparison.Ordinal);
            Assert.True(filterAt > 0);
            Assert.True(filterAt < kql.IndexOf("let entityRows", StringComparison.Ordinal));
            Assert.True(filterAt < kql.IndexOf("by BucketStart, Entity", StringComparison.Ordinal));
            Assert.Equal("entityRows", FinalStatement(kql));
        }

        Assert.Throws<ArgumentException>(() => AppInsightsKpiSource.RenderKql(
            AppInsightsKpiSource.LoadTemplate(AppInsightsKpiSource.LatencySourceKey, "hotels"), Incumbent(),
            Definition(stage: "x\" or true", grain: KpiGrain.Entity), From, To, 10));
    }

    [Fact]
    public async Task KpiSource_UnknownProfile_IsUnavailable()
    {
        var telemetry = new CapturingTelemetrySource();
        var source = Source(telemetry, Incumbent());

        foreach (var sourceKey in SourceKeys)
        {
            // No profile stored under the key.
            var missing = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                source.FetchAsync(Definition(sourceKey, profileKey: "nobody"), From, To, 10));
            Assert.Contains("nobody", missing.Message, StringComparison.Ordinal);

            // A disabled profile reads as absent.
            var disabled = Source(telemetry, Incumbent() with { Enabled = false });
            await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                disabled.FetchAsync(Definition(sourceKey), From, To, 10));

            // The domain is not a service of the profile (contract v4 §5 wording).
            var notAService = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                source.FetchAsync(Definition(sourceKey, domain: "billing"), From, To, 10));
            Assert.Contains("billing [arrivia]: domain is not a service of the profile", notAService.Message, StringComparison.Ordinal);

            // A profile that cannot render the query (no telemetry source for the service).
            var sourceless = Incumbent() with
            {
                Services = [Service("hotels", [], [], ("02-search", "02 Hotel Search", 1, "search.json"))],
            };
            await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, sourceless).FetchAsync(Definition(sourceKey), From, To, 10));
        }

        Assert.Empty(telemetry.Requests);
    }
}
