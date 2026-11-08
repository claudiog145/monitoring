using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.A2;

/// <summary>FX2: entity-grain history is bounded by the 8-day entity bucket retention.</summary>
[Collection(A2IngestionRunLockCollection.Name)]
public sealed class EntityRetentionBoundTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);
    private static readonly DateTimeOffset LastClosedBucket = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);
    private const string LongLookbackParams = """{"lookbackDays":28,"thresholdPercent":50}""";

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static KpiIngestionService Service(KpiDefinition definition, IKpiSource source, IKpiBucketStore buckets,
        A2WatermarkStore watermarks, TimeProvider time, int lagMinutes = 5) =>
        new(
            new A2DefinitionStore(definition),
            [source],
            buckets,
            new A2SampleStore(),
            watermarks,
            Options.Create(new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 1_000_000 }),
            time,
            NullLogger<KpiIngestionService>.Instance,
            Options.Create(new KpiIngestionOptions { LagMinutes = lagMinutes }),
            evaluator: new KpiEvaluator());

    private static KpiDefinition LongLookbackEntity(int id) =>
        A2Kpi.Definition(id: id) with { Grain = KpiGrain.Entity, MaxEntitiesPerBucket = 10, MethodParamsJson = LongLookbackParams };

    private static KpiSourceResult EntityHourly(KpiDefinition definition, DateTimeOffset from, DateTimeOffset to)
    {
        var rows = new List<KpiSourceBucket>();
        for (var start = from; start < to; start += definition.BucketSize)
        {
            rows.Add(new KpiSourceBucket(start, 100, null) { EntityKey = "club-a", P50 = 100, P95 = 200, Mean = 110, FailedCount = 1 });
        }
        return new KpiSourceResult(rows, []);
    }

    [Fact]
    public void EntityRetention_MirrorsSqlKpiBucketStore()
    {
        Assert.Equal(SqlKpiBucketStore.EntityRetention, KpiEntityRetention.Retention);
        Assert.Equal(TimeSpan.FromDays(8) - TimeSpan.FromHours(1), KpiEntityRetention.MaxHistorySpan(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromDays(7), KpiEntityRetention.MaxHistorySpan(TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(59)]
    public async Task EntityBackfill_NeverReachesBeyondRetention(int lagMinutes)
    {
        var entity = LongLookbackEntity(9401 + lagMinutes);
        var watermarks = new A2WatermarkStore();
        var to = KpiTime.FloorToBucket(Now - TimeSpan.FromMinutes(lagMinutes), entity.BucketSize);
        watermarks.Values[entity.KpiDefinitionId] = to;
        var source = new A2KpiSource { Respond = (d, f, t, _) => EntityHourly(d, f, t) };
        var time = new MutableTimeProvider(Now);

        await Service(entity, source, new InMemoryKpiBucketStore(time), watermarks, time, lagMinutes).RunAsync();

        var backfill = Assert.Single(source.Calls);
        var cutoff = Now - KpiEntityRetention.Retention;
        Assert.True(backfill.From >= cutoff, $"Backfill from {backfill.From:O} is older than the retention cutoff {cutoff:O}.");
        Assert.True(backfill.From >= to - KpiEntityRetention.MaxHistorySpan(entity.BucketSize));
        Assert.Equal(to, backfill.To);

        // Domain grain is unchanged: the same method still backfills its full 28 days.
        var domain = A2Kpi.Definition(id: 9411 + lagMinutes) with { MethodParamsJson = LongLookbackParams };
        var domainWatermarks = new A2WatermarkStore();
        domainWatermarks.Values[domain.KpiDefinitionId] = to;
        var domainSource = new A2KpiSource();
        await Service(domain, domainSource, new A2BucketStore(), domainWatermarks, time, lagMinutes).RunAsync();
        Assert.Equal(to - TimeSpan.FromDays(28) - TimeSpan.FromHours(1), Assert.Single(domainSource.Calls).From);
    }

    [Fact]
    public async Task EntityKpi_LongLookback_DoesNotRequeryEveryCycle()
    {
        var definition = LongLookbackEntity(9421);
        var time = new MutableTimeProvider(Now);
        var buckets = new InMemoryKpiBucketStore(time);
        var watermarks = new A2WatermarkStore();
        watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var source = new A2KpiSource { Respond = (d, f, t, _) => EntityHourly(d, f, t) };
        var service = Service(definition, source, buckets, watermarks, time);

        await service.RunAsync();
        Assert.Single(source.Calls);
        var history = await buckets.GetEntityRangeAsync(definition.KpiDefinitionId, Now - TimeSpan.FromDays(30), LastClosedBucket);
        Assert.NotEmpty(history);
        Assert.All(history, b => Assert.True(b.BucketStart >= Now - KpiEntityRetention.Retention));

        // Every minute for the rest of the hour: nothing to backfill, nothing new closed.
        for (var minute = 1; minute <= 20; minute++)
        {
            time.Now = Now.AddMinutes(minute);
            await service.RunAsync();
        }
        Assert.Single(source.Calls);

        // Next hours: only the newly closed bucket is fetched - never a history backfill again.
        for (var hour = 1; hour <= 3; hour++)
        {
            time.Now = Now.AddHours(hour);
            await service.RunAsync();
            time.Now = Now.AddHours(hour).AddMinutes(1);
            await service.RunAsync();
        }
        Assert.Equal(4, source.Calls.Count);
        Assert.All(source.Calls.Skip(1), c => Assert.Equal(c.From + definition.BucketSize, c.To));
    }

    [Fact]
    public async Task EntityKpi_LookbackBeyondRetention_Returns400()
    {
        var store = new A4FakeKpiDefinitionStore();
        var admin = new KpiDefinitionAdminFunctions(
            store,
            Options.Create(new MonitoringOptions { SettingsWriteSecret = A4Http.WriteSecret }),
            new A4FakeTimeProvider(Now),
            NullLogger<KpiDefinitionAdminFunctions>.Instance,
            new KpiProfileResolver(
                Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }),
                new NoActiveProfileStore(),
                new InMemoryCompanyProfileStore([CompanyKqlTemplaterTests.Profile() with { ProfileKey = KpiProfiles.Default }])),
            evaluator: new KpiEvaluator());

        Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> Post(string grain, string method, string methodParams, string kpi) =>
            admin.UpsertAsync(A4Http.Request("http://localhost/api/settings/kpis", $$"""
                {"kpiDefinitionId":0,"pillar":"Application","kpi":"{{kpi}}","domain":"hotels","stage":"","sourceKey":"appinsights:latency",
                 "direction":"increase","aggregation":"p95","evaluationFrequency":"00:05:00","bucketSize":"01:00:00","lookbackWindow":"1.00:00:00",
                 "sampleSize":0,"minObservationsPerBucket":30,"detectionMethod":"{{method}}","methodParamsJson":"{{methodParams.Replace("\"", "\\\"")}}",
                 "consecutiveCount":2,"cooldownMinutes":120,"enabled":true,"changedBy":"ana@example.com","profileKey":"arrivia","grain":"{{grain}}"}
                """, writeSecret: A4Http.WriteSecret), CancellationToken.None);

        var rejected = await Post("entity", "baselinePercent", LongLookbackParams, "Latency");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("Entity-grain KPIs keep 8 days; method requires 28 days.",
            rejected.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));

        var tooManyBuckets = await Post("entity", "robustZScore", """{"windowBuckets":240,"zThreshold":3}""", "Latency");
        Assert.Equal(HttpStatusCode.BadRequest, tooManyBuckets.StatusCode);
        Assert.Contains("Entity-grain KPIs keep 8 days; method requires 10 days.",
            tooManyBuckets.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(await store.GetAllAsync());

        Assert.Equal(HttpStatusCode.OK, (await Post("entity", "baselinePercent", """{"lookbackDays":7,"thresholdPercent":50}""", "Latency")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("entity", "robustZScore", """{"windowBuckets":24,"zThreshold":3}""", "Exceptions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("domain", "baselinePercent", LongLookbackParams, "Latency")).StatusCode);
    }

    private sealed class NoActiveProfileStore : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
