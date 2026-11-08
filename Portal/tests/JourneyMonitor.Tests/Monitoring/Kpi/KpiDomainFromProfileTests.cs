using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using JourneyMonitor.Tests.Spec1000.A2;
using JourneyMonitor.Tests.Spec1000.A4;
using JourneyMonitor.Tests.Spec1000.D1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

/// <summary>Contract v4 §5 (AG1): KPI domains come from the company profile services; amendment A4 incumbent exception.</summary>
public sealed class KpiDomainFromProfileTests
{
    private const string Acme = "acme";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    internal static CompanyProfile AcmeProfile() => CompanyKqlTemplaterTests.Profile() with { ProfileKey = Acme };

    internal static CompanyProfile ArriviaSeedProfile() =>
        JsonSerializer.Deserialize<CompanyProfile>(ArriviaProfileSeedTests.SeedJsonFor(KpiProfiles.Default), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private readonly A4FakeKpiDefinitionStore _definitions = new();

    private KpiDefinitionAdminFunctions Admin()
    {
        var profiles = new InMemoryCompanyProfileStore([ArriviaSeedProfile(), AcmeProfile()]);
        var resolver = new KpiProfileResolver(
            Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }), null, profiles);
        return new KpiDefinitionAdminFunctions(
            _definitions,
            Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret }),
            new A4FakeTimeProvider(Now),
            NullLogger<KpiDefinitionAdminFunctions>.Instance,
            resolver,
            profiles);
    }

    private static string Body(string profileKey, string domain) => $$"""
        {"kpiDefinitionId":0,"pillar":"Application","kpi":"Exceptions","domain":"{{domain}}","stage":"","sourceKey":"appinsights:exceptions",
         "direction":"increase","aggregation":"count","evaluationFrequency":"00:05:00","bucketSize":"01:00:00","lookbackWindow":"1.00:00:00",
         "sampleSize":0,"minObservationsPerBucket":0,"detectionMethod":"robustZScore",
         "methodParamsJson":"{\"windowBuckets\":24,\"zThreshold\":3}","consecutiveCount":2,"cooldownMinutes":120,
         "enabled":true,"changedBy":"ana@example.com","profileKey":"{{profileKey}}"}
        """;

    private async Task<(HttpStatusCode Status, string[] Errors)> PostAsync(string profileKey, string domain)
    {
        var response = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", Body(profileKey, domain), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        var errors = response.StatusCode == HttpStatusCode.BadRequest
            ? response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray()
            : [];
        return (response.StatusCode, errors);
    }

    [Fact]
    public async Task KpiDomain_MustBeProfileService()
    {
        // The services of 'acme' (checkout, catalog) are valid domains, case-insensitively.
        var created = await PostAsync(Acme, "checkout");
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("checkout", _definitions.Upserts.Single().Definition.Domain);

        var upper = await PostAsync(Acme, "CATALOG");
        Assert.Equal(HttpStatusCode.OK, upper.Status);

        // The incumbent's seed domains are not services of 'acme'.
        var foreign = await PostAsync(Acme, "hotels");
        Assert.Equal(HttpStatusCode.BadRequest, foreign.Status);

        // And a service of 'acme' is not a domain of 'arrivia'.
        var crossed = await PostAsync(KpiProfiles.Default, "checkout");
        Assert.Equal(HttpStatusCode.BadRequest, crossed.Status);
        Assert.Contains("Domain 'checkout' is not a service of profile 'arrivia'.", crossed.Errors);

        Assert.True(KpiDomainPolicy.IsAllowed(AcmeProfile(), Acme, " Checkout "));
        Assert.False(KpiDomainPolicy.IsAllowed(null, Acme, "checkout"));
        Assert.False(KpiDomainPolicy.IsAllowed(AcmeProfile(), Acme, ""));
        Assert.Equal(2, _definitions.Upserts.Count);
    }

    [Fact]
    public async Task KpiDomain_UnknownReturns400()
    {
        var (status, errors) = await PostAsync(Acme, "payments");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(["Domain 'payments' is not a service of profile 'acme'."], errors);
        Assert.Empty(_definitions.Upserts);
    }

    [Fact]
    public async Task KpiDomain_LongerThan32_Returns400()
    {
        var longKey = "acme-service-with-forty-characters-xyzwv";
        Assert.Equal(40, longKey.Length);
        var profile = AcmeProfile() with
        {
            Services = [.. AcmeProfile().Services, CompanyKqlTemplaterTests.Catalog() with { Key = longKey, FallbackStep = null }],
        };
        var profiles = new RecordingProfileStore(profile);
        var admin = new KpiDefinitionAdminFunctions(
            _definitions,
            Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret }),
            new A4FakeTimeProvider(Now),
            NullLogger<KpiDefinitionAdminFunctions>.Instance,
            new KpiProfileResolver(Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }), null, profiles),
            profiles);

        var response = await admin.UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", Body(Acme, longKey), writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Domain must be at most 32 characters.", response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(_definitions.Upserts);
        Assert.Equal(0, profiles.Reads);

        // Exactly 32 characters is still accepted when it is a service.
        var key32 = longKey[..32];
        var fits = AcmeProfile() with { Services = [.. AcmeProfile().Services, CompanyKqlTemplaterTests.Catalog() with { Key = key32 }] };
        var ok = await new KpiDefinitionAdminFunctions(
            _definitions,
            Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret }),
            new A4FakeTimeProvider(Now),
            NullLogger<KpiDefinitionAdminFunctions>.Instance,
            new KpiProfileResolver(Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }), null, new RecordingProfileStore(fits)),
            new RecordingProfileStore(fits)).UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", Body(Acme, key32), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    private sealed class RecordingProfileStore(params CompanyProfile[] profiles) : ICompanyProfileStore
    {
        public int Reads { get; private set; }

        public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(profiles.FirstOrDefault(p => string.Equals(p.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompanyProfile>>(profiles);
        public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task KpiDomain_IncumbentSeedUnchanged()
    {
        var arrivia = ArriviaSeedProfile();
        var seedDomains = SeedKpiDomains();

        // The incumbent seed covers the 4 fixed domains plus services of 'arrivia' (SD6 added 'shared'), all owned by 'arrivia'.
        Assert.Subset(seedDomains.ToHashSet(StringComparer.OrdinalIgnoreCase), KpiDomains.All.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.All(seedDomains, d => Assert.True(KpiDomainPolicy.IsAllowed(arrivia, KpiProfiles.Default, d), d));

        // A4: the incumbent exception covers exactly the seed domains no service of 'arrivia' declares.
        var services = arrivia.Services.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excepted = KpiDomains.All.Where(d => !services.Contains(d)).ToList();
        Assert.All(excepted, d => Assert.False(KpiDomainPolicy.IsAllowed(arrivia, Acme, d)));

        // The A4 fallback is needed: any 'arrivia' service that passes the validator adds a classifier arm, so
        // declaring the missing seed domain as a service would change the Scout board.
        foreach (var domain in excepted)
        {
            var extra = new ServiceDefinition(domain, domain, [], [],
                [new FlowDefinition("booking", "Booking", [
                    new StepDefinition("search", "Search", 1000, [new StepMatcher(StepMatcherKind.UrlContains, "/" + domain + "/search")])
                ])]);
            var withService = arrivia with { Services = [.. arrivia.Services, extra] };
            Assert.DoesNotContain(CompanyProfileValidator.Validate(withService), f => f.IsError);
            var template = KqlPlaceholders.ServiceAndStepClassifier;
            Assert.NotEqual(CompanyKqlTemplater.Render(template, arrivia), CompanyKqlTemplater.Render(template, withService));
        }

        // Every seed domain still POSTs for 'arrivia'.
        foreach (var domain in KpiDomains.All)
        {
            var (status, errors) = await PostAsync(KpiProfiles.Default, domain);
            Assert.True(status == HttpStatusCode.OK, domain + ": " + string.Join(" ", errors));
        }
        Assert.Equal(KpiDomains.All.Count, _definitions.Upserts.Count);
    }

    private static List<string> SeedKpiDomains()
    {
        var sql = File.ReadAllText(Path.Combine(PortalDirectory(), "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "SeedKpiDefinitions.sql"));
        var rows = Regex.Matches(sql, @"^\s*\(N'(?:Application|Business|Infrastructure)',\s*N'[^']+',\s*N'([^']+)'", RegexOptions.Multiline);
        var domains = rows.Select(m => m.Groups[1].Value).ToList();
        Assert.True(domains.Count >= 24, $"seed rows: {domains.Count}");
        return domains;
    }

    private static string PortalDirectory([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JourneyMonitor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Portal directory not found from " + thisFile);
    }
}

/// <summary>KpiIngestionService holds a static run lock: serial with the A2 ingestion tests.</summary>
[Collection(A2IngestionRunLockCollection.Name)]
public sealed class KpiDomainFromProfileRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);

    private readonly A2BucketStore _buckets = new();
    private readonly A2SampleStore _samples = new();
    private readonly A2WatermarkStore _watermarks = new();
    private readonly D1ActivityHarness _activity = new(new D1FixedTimeProvider(Now));

    [Fact]
    public async Task KpiDomain_UnknownAtRuntime_WarnsAndSkips()
    {
        var profiles = new InMemoryCompanyProfileStore([KpiDomainFromProfileTests.ArriviaSeedProfile(), KpiDomainFromProfileTests.AcmeProfile()]);
        var source = new A2KpiSource { Respond = (d, from, to, per) => A2KpiSource.Hourly(d, from, to, per, 2) };
        var incumbent = KpiProfileTests.Latency(1);
        var incumbentSeedOnly = KpiProfileTests.Latency(2, domain: KpiDomains.All[^1]);
        var acmeUnknown = KpiProfileTests.Latency(3, KpiProfileTests.Acme, domain: "payments");
        var acmeKnown = KpiProfileTests.Latency(4, KpiProfileTests.Acme, domain: "Checkout");

        var service = new KpiIngestionService(
            new A2DefinitionStore([incumbent, incumbentSeedOnly, acmeUnknown, acmeKnown]),
            [source], _buckets, _samples, _watermarks,
            Options.Create(new HealthMonitoringOptions { Enabled = true }),
            new A2FixedTimeProvider(Now), NullLogger<KpiIngestionService>.Instance,
            null, _activity.Recorder, _activity.Tracker, _activity.Log, null, null, profiles);

        var result = await service.RunAsync();

        Assert.Equal(3, result.KpisProcessed);
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain(source.Calls, c => c.KpiDefinitionId == 3);
        Assert.DoesNotContain(_buckets.All, b => b.KpiDefinitionId == 3);
        Assert.False(_watermarks.Values.ContainsKey(3));
        Assert.True(_watermarks.Values.ContainsKey(4));

        var warnings = (await _activity.EntriesAsync(DaemonKeys.KpiIngestion))
            .Where(e => e.Kind == DaemonActivityKinds.IngestionKpi && e.Level == DaemonActivityLevel.Warning)
            .Select(e => e.Message)
            .ToList();
        Assert.Equal(["Latency/payments [acme]: domain is not a service of the profile"], warnings);
    }
}

/// <summary>PillarHealthMonitorService holds a static run lock: serial with the A1 monitor tests.</summary>
[Collection(A1MonitorRunLockCollection.Name)]
public sealed class KpiDomainFromProfileMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);

    [Fact]
    public async Task KpiDomain_UnknownAtRuntime_MonitorWarnsAndSkips()
    {
        var profiles = new InMemoryCompanyProfileStore([KpiDomainFromProfileTests.ArriviaSeedProfile(), KpiDomainFromProfileTests.AcmeProfile()]);
        var definitions = new A1InMemoryDefinitionStore();
        var states = new A1InMemoryStateStore();
        var watermarks = new A1InMemoryWatermarkStore();
        var buckets = new A1InMemoryBucketStore();
        var template = KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}");
        definitions.Definitions.Add(template with
        {
            KpiDefinitionId = 3, Pillar = KpiPillar.Application, Kpi = "Latency", Domain = "payments", ProfileKey = KpiProfileTests.Acme,
        });
        definitions.Definitions.Add(template with
        {
            KpiDefinitionId = 4, Pillar = KpiPillar.Application, Kpi = "Latency", Domain = "checkout", ProfileKey = KpiProfileTests.Acme,
        });
        var hour = KpiTime.FloorToBucket(Now, TimeSpan.FromHours(1));
        states.States[4] = new KpiDetectionState(4, 0, hour.AddHours(-2), null, Now.AddHours(-1));
        watermarks.Watermarks[4] = hour;
        buckets.Buckets.Add(new KpiBucket(4, hour.AddHours(-1), 100, 0, null, 1000, null, null, null));

        var activity = new D1ActivityHarness(new D1FixedTimeProvider(Now));
        // MaxKpisPerCycle = 1: the skipped unknown-domain KPI must not use the only slot.
        var monitor = new PillarHealthMonitorService(
            definitions, buckets, states, new A1InMemoryAnomalyStore(),
            watermarks, new KpiEvaluator(), new A1RecordingTriageSubmitter(),
            Options.Create(new HealthMonitoringOptions { Enabled = true, Pillars = ["application"], MaxKpisPerCycle = 1 }),
            new A1FakeTimeProvider(Now), NullLogger<PillarHealthMonitorService>.Instance,
            activity.Recorder, activity.Tracker, profiles);

        var monitored = await monitor.RunAsync(KpiPillar.Application);

        Assert.Equal(1, monitored.Skipped);
        Assert.Equal(1, monitored.KpisEvaluated);
        Assert.Contains(await activity.EntriesAsync(DaemonKeys.ApplicationHealthMonitor),
            e => e.Level == DaemonActivityLevel.Warning && e.Message == "Latency/payments [acme]: domain is not a service of the profile");
    }
}
