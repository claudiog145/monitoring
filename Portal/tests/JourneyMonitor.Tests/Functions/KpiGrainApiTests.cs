using System.Net;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Functions;

/// <summary>SD6 (contract v4 §1, amendments A1/A3): entity KPI seed, and grain/maxEntitiesPerBucket in the KPI settings API.</summary>
public sealed class KpiGrainApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly A4FakeKpiDefinitionStore _definitions = new();

    private KpiDefinitionAdminFunctions Admin() => new(
        _definitions,
        Options.Create(new MonitoringOptions { SettingsWriteSecret = A4Http.WriteSecret }),
        new A4FakeTimeProvider(Now),
        NullLogger<KpiDefinitionAdminFunctions>.Instance,
        new KpiProfileResolver(
            Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }),
            new FakeActiveStore(),
            new InMemoryCompanyProfileStore([CompanyKqlTemplaterTests.Profile() with { ProfileKey = KpiProfiles.Default }])));

    private static string Body(int id = 0, string? grain = null, int? maxEntities = null, string kpi = "Latency", string domain = "hotels")
    {
        var extra = (grain is null ? "" : $",\"grain\":{grain}") + (maxEntities is null ? "" : $",\"maxEntitiesPerBucket\":{maxEntities}");
        return $$"""
            {"kpiDefinitionId":{{id}},"pillar":"Application","kpi":"{{kpi}}","domain":"{{domain}}","stage":"","sourceKey":"appinsights:latency",
             "direction":"increase","aggregation":"p95","evaluationFrequency":"00:05:00","bucketSize":"01:00:00","lookbackWindow":"1.00:00:00",
             "sampleSize":0,"minObservationsPerBucket":30,"detectionMethod":"robustZScore",
             "methodParamsJson":"{\"windowBuckets\":24,\"zThreshold\":3}","consecutiveCount":2,"cooldownMinutes":120,
             "enabled":true,"changedBy":"ana@example.com","profileKey":"arrivia"{{extra}}}
            """;
    }

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> PostAsync(string body) =>
        Admin().UpsertAsync(A4Http.Request("http://localhost/api/settings/kpis", body, writeSecret: A4Http.WriteSecret), CancellationToken.None);

    private static IReadOnlyList<string[]> SeedRows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JourneyMonitor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "SeedKpiDefinitions.sql");
        var sql = File.ReadAllText(path);

        // A brand-new database must already have the v4 Grain column when the seed runs.
        var initSql = File.ReadAllText(Path.Combine(dir.FullName, "docker", "init-sql.sh"));
        var shadowAt = initSql.IndexOf("monitoring/CreateMonitoringScoutShadow.sql", StringComparison.Ordinal);
        var seedAt = initSql.IndexOf("monitoring/SeedKpiDefinitions.sql", StringComparison.Ordinal);
        Assert.True(shadowAt >= 0 && seedAt > shadowAt, "SeedKpiDefinitions.sql must run after CreateMonitoringScoutShadow.sql.");

        // The v4 MERGE must match on the full natural key and never update tuned rows.
        Assert.Contains("t.Grain = s.Grain", sql);
        Assert.DoesNotContain("WHEN MATCHED", sql);

        return Regex.Matches(sql, @"^\s*\((N'(?:Application|Business)'.*)\),?\s*$", RegexOptions.Multiline)
            .Select(m => Regex.Matches(m.Groups[1].Value, @"N'((?:[^']|'')*)'|(\d+)")
                .Select(v => v.Groups[1].Success ? v.Groups[1].Value : v.Groups[2].Value)
                .ToArray())
            .ToList();
    }

    [Fact]
    public void Seed_DeclaresEntityKpisPerDomain()
    {
        // Columns: Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvalSec, BucketSec, LookbackSec,
        //          SampleSize, MinObs, Method, Params, Consecutive, Cooldown, Enabled, Grain, MaxEntities.
        var rows = SeedRows();
        Assert.All(rows, r => Assert.Equal(19, r.Length));
        Assert.Equal(rows.Count, rows.Select(r => (r[0], r[1], r[2], r[3], r[17])).Distinct().Count());

        var entity = rows.Where(r => r[17] == "entity").ToList();
        Assert.Equal(8, entity.Count);
        foreach (var domain in new[] { "hotels", "cars", "cruises", "shared" })
        {
            var latency = Assert.Single(entity, r => r[1] == "Latency" && r[2] == domain);
            var exceptions = Assert.Single(entity, r => r[1] == "Exceptions" && r[2] == domain);
            Assert.Equal(["appinsights:latency", "P95", "30"], new[] { latency[4], latency[6], latency[11] });
            Assert.Equal(["appinsights:exceptions", "Count", "0"], new[] { exceptions[4], exceptions[6], exceptions[11] });
            foreach (var r in new[] { latency, exceptions })
            {
                Assert.Equal("Application", r[0]);
                Assert.Equal("", r[3]); // A1: empty Stage aggregates every stage of the service per entity.
                Assert.Equal("3600", r[8]);
                Assert.Equal("RobustZScore", r[12]);
                Assert.Equal("{\"windowBuckets\":24,\"zThreshold\":3}", r[13]);
                Assert.Equal(["2", "120", "1", "500"], new[] { r[14], r[15], r[16], r[18] });
            }
        }

        // A3: the incumbent's shared service also gets enabled domain-grain Latency/Exceptions, same methods as hotels.
        var domainRows = rows.Where(r => r[17] == "domain").ToList();
        Assert.Equal(26, domainRows.Count);
        foreach (var kpi in new[] { "Latency", "Exceptions" })
        {
            var hotels = Assert.Single(domainRows, r => r[1] == kpi && r[2] == "hotels");
            var shared = Assert.Single(domainRows, r => r[1] == kpi && r[2] == "shared");
            Assert.Equal(hotels.Where((_, i) => i != 2), shared.Where((_, i) => i != 2));
            Assert.Equal("1", shared[16]);
        }
    }

    [Fact]
    public async Task KpiApi_GrainRoundTrips()
    {
        var created = await PostAsync(Body(grain: "\"entity\"", maxEntities: 250));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var json = created.ReadJson();
        Assert.Equal("entity", json["grain"]!.GetValue<string>());
        Assert.Equal(250, json["maxEntitiesPerBucket"]!.GetValue<int>());
        var stored = Assert.Single(_definitions.Definitions);
        Assert.Equal(KpiGrain.Entity, stored.Grain);
        Assert.Equal(250, stored.MaxEntitiesPerBucket);

        var list = (await Admin().GetAllAsync(A4Http.Request("http://localhost/api/settings/kpis?profileKey=arrivia"), CancellationToken.None)).ReadJson().AsArray();
        var row = Assert.Single(list)!;
        Assert.Equal("entity", row["grain"]!.GetValue<string>());
        Assert.Equal(250, row["maxEntitiesPerBucket"]!.GetValue<int>());

        // Posting the GET row back (the portal's edit flow) keeps the grain; omitting it on update keeps the stored one.
        var id = row["kpiDefinitionId"]!.GetValue<int>();
        var update = await PostAsync(Body(id: id, maxEntities: 1000));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("entity", update.ReadJson()["grain"]!.GetValue<string>());
        Assert.Equal(1000, Assert.Single(_definitions.Definitions).MaxEntitiesPerBucket);

        // A new KPI without grain is domain grain with the default cap, serialized as "domain".
        var domain = await PostAsync(Body(kpi: "Exceptions"));
        Assert.Equal(HttpStatusCode.OK, domain.StatusCode);
        Assert.Equal("domain", domain.ReadJson()["grain"]!.GetValue<string>());
        Assert.Equal(500, domain.ReadJson()["maxEntitiesPerBucket"]!.GetValue<int>());

        foreach (var bad in new[] { "\"partner\"", "1", "\"\"", "true" })
        {
            var response = await PostAsync(Body(grain: bad, kpi: "Bad"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Grain must be domain or entity.", response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        }

        var tooMany = await PostAsync(Body(grain: "\"entity\"", maxEntities: 5001, kpi: "Bad"));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("MaxEntitiesPerBucket must be 1..5000.", tooMany.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal(2, _definitions.Definitions.Count);
    }

    [Fact]
    public async Task KpiApi_SameKeyDifferentGrain_Coexist()
    {
        var domain = await PostAsync(Body(grain: "\"domain\""));
        Assert.Equal(HttpStatusCode.OK, domain.StatusCode);
        var entity = await PostAsync(Body(grain: "\"Entity\""));
        Assert.Equal(HttpStatusCode.OK, entity.StatusCode);

        Assert.Equal(2, _definitions.Definitions.Count);
        Assert.Equal([KpiGrain.Domain, KpiGrain.Entity], _definitions.Definitions.Select(d => d.Grain).OrderBy(g => g).ToArray());
        Assert.NotEqual(domain.ReadJson()["kpiDefinitionId"]!.GetValue<int>(), entity.ReadJson()["kpiDefinitionId"]!.GetValue<int>());

        // The same key and the same grain still clash.
        var duplicate = await PostAsync(Body(grain: "\"entity\""));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(2, _definitions.Definitions.Count);

        // Without profileKey or id, the natural-key lookup only considers the posted grain.
        var withoutProfile = await Admin().UpsertAsync(A4Http.Request("http://localhost/api/settings/kpis",
            Body(grain: "\"entity\"").Replace(",\"profileKey\":\"arrivia\"", ""), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, withoutProfile.StatusCode);
        Assert.Contains($"A KPI with the same Pillar/Kpi/Domain/Stage and grain entity already exists (KpiDefinitionId {entity.ReadJson()["kpiDefinitionId"]!.GetValue<int>()}).",
            withoutProfile.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    private sealed class FakeActiveStore : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
