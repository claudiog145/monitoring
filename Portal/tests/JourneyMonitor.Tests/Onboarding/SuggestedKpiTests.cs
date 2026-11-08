using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Onboarding;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Onboarding;

/// <summary>
/// AG7 (contract v4 Â§5): the onboarding draft proposes disabled KPIs per service and per discovered
/// partner, every one of them accepted as-is by the KPI API - plus the save-path fix that lets a
/// profile using any <see cref="StepMatcherKind"/> be stored.
/// </summary>
public sealed class SuggestedKpiTests
{
    private const string Alias = "contoso-prod";
    private const string WriteSecret = "s3cret";
    private const string InternalSecret = "internal";

    [Fact]
    public void Draft_SuggestsKpisPerService()
    {
        var draft = ProfileDraftBuilder.Build(Report(roleNames: [new("Checkout API", 4_100), new("Search API", 900)]));
        var kpis = draft.SuggestedKpis!;

        Assert.Equal(8, kpis.Count);
        foreach (var service in draft.Profile.Services)
        {
            var mine = kpis.Where(k => k.Definition.Domain == service.Key).Select(k => k.Definition).ToList();
            Assert.Equal(4, mine.Count);

            var latency = Assert.Single(mine, d => d.Kpi == "Latency" && d.Grain == "domain");
            Assert.Equal("P95", latency.Aggregation);
            Assert.Equal("BaselinePercent", latency.DetectionMethod);
            Assert.Equal("{\"lookbackDays\":7,\"thresholdPercent\":50}", latency.MethodParamsJson);
            Assert.Equal("01:00:00", latency.BucketSize);

            var exceptions = Assert.Single(mine, d => d.Kpi == "Exceptions" && d.Grain == "domain");
            Assert.Equal("Count", exceptions.Aggregation);
            Assert.Equal("RobustZScore", exceptions.DetectionMethod);
            Assert.Equal("{\"windowBuckets\":24,\"zThreshold\":3}", exceptions.MethodParamsJson);

            var entity = mine.Where(d => d.Grain == "entity").ToList();
            Assert.Equal(new[] { "Exceptions", "Latency" }, entity.Select(d => d.Kpi).OrderBy(k => k).ToArray());
            Assert.All(entity, d =>
            {
                Assert.Equal("", d.Stage);
                Assert.Equal("RobustZScore", d.DetectionMethod);
                Assert.Equal(500, d.MaxEntitiesPerBucket);
                Assert.Equal("01:00:00", d.BucketSize);
            });

            Assert.All(mine, d => Assert.Equal(draft.Profile.ProfileKey, d.ProfileKey));
            Assert.All(mine, d => Assert.True(KpiDomainPolicy.IsAllowed(draft.Profile, d.ProfileKey, d.Domain)));
        }

        // No partner was discovered, so no dependency KPI - and the draft says why.
        Assert.DoesNotContain(kpis, k => k.Definition.SourceKey == PartnerKpi.SourceKey);
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.NoPartnersProposed);
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.SuggestedKpiNote && w.Message.Contains("declares no partners", StringComparison.Ordinal));
    }

    [Fact]
    public void Draft_SuggestsKpiPerDiscoveredPartner()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            roleNames: [new("Checkout API", 4_100), new("Search API", 900)],
            dependencies: Dependencies(
                new ObservedDependencyTarget("api.watchlist.example", "Http", 6_000),
                new ObservedDependencyTarget("pay.gateway.example", "Http", 3_000),
                new ObservedDependencyTarget("rare.example", "Http", 50),
                new ObservedDependencyTarget("internal-span", "InProc", 900))));

        var partners = draft.Profile.Partners!;
        Assert.Equal(new[] { "api.watchlist.example", "pay.gateway.example" }, partners.Select(p => p.DisplayName).ToArray());
        Assert.All(partners, p =>
        {
            var matcher = Assert.Single(p.Matchers);
            Assert.Equal(PartnerMatcherKind.DependencyTargetContains, matcher.Kind);
            Assert.Equal(p.DisplayName, matcher.Value);
        });
        Assert.DoesNotContain(CompanyProfileValidator.Validate(draft.Profile), f => f.IsError && f.Path.StartsWith("$.partners", StringComparison.Ordinal));

        var rare = Assert.Single(draft.Proposals, p => p.Kind == DraftProposalKind.Partner && p.Value == "rare.example");
        Assert.False(rare.Proposed);
        Assert.Contains("below the 5% floor", rare.Evidence[0], StringComparison.Ordinal);
        var inProc = Assert.Single(draft.Proposals, p => p.Kind == DraftProposalKind.Partner && p.Value == "internal-span");
        Assert.False(inProc.Proposed);
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.PartnersNeedReview);

        // Both services read the same telemetry source: ONE dependency KPI, not one per service.
        var dependency = Assert.Single(draft.SuggestedKpis!, k => k.Definition.SourceKey == PartnerKpi.SourceKey).Definition;
        Assert.Equal(draft.Profile.Services[0].Key, dependency.Domain);
        Assert.Equal("entity", dependency.Grain);
        Assert.Equal("", dependency.Stage);
        Assert.Equal("RobustZScore", dependency.DetectionMethod);
        Assert.False(dependency.Enabled);
    }

    [Fact]
    public void SuggestedKpis_AreDisabled()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            roleNames: [new("Checkout API", 4_100)],
            dependencies: Dependencies(new ObservedDependencyTarget("api.watchlist.example", "Http", 6_000))));

        Assert.NotEmpty(draft.SuggestedKpis!);
        Assert.All(draft.SuggestedKpis!, k => Assert.False(k.Definition.Enabled));
        Assert.All(draft.SuggestedKpis!, k => Assert.False(k.Definition.ToKpiDefinition(DateTimeOffset.UnixEpoch).Enabled));
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.SuggestedKpisDisabled);
    }

    [Fact]
    public void SuggestedKpis_OneDependencyKpiPerDistinctAliasSet()
    {
        var profile = Profile(
            [
                new ServiceDefinition("alpha", "Alpha", ["src-a", "src-b"], [], []),
                new ServiceDefinition("beta", "Beta", ["SRC-B", "src-a"], [], []),
                new ServiceDefinition("gamma", "Gamma", ["src-c"], [], []),
                new ServiceDefinition("delta", "Delta", [], [], []),
            ],
            partners: [new PartnerDefinition("watchlist", "Watchlist", [new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "watchlist")])]);

        var plan = SuggestedKpiBuilder.Build(profile);

        var dependencyDomains = plan.Kpis.Where(k => k.Definition.SourceKey == PartnerKpi.SourceKey).Select(k => k.Definition.Domain).ToArray();
        Assert.Equal(new[] { "alpha", "gamma" }, dependencyDomains);
        Assert.Contains(plan.Notes, n => n.Contains("\"delta\"", StringComparison.Ordinal) && n.Contains("no telemetry source alias", StringComparison.Ordinal));
        Assert.Equal(16, plan.Kpis.Count(k => k.Definition.SourceKey != PartnerKpi.SourceKey));
    }

    [Fact]
    public void SuggestedKpis_SkipServiceKeysLongerThanAKpiDomain_AndDraftKeysFitIt()
    {
        var longKey = new string('a', 40);
        var plan = SuggestedKpiBuilder.Build(Profile([new ServiceDefinition(longKey, "Long", [Alias], [], [])]));
        Assert.Empty(plan.Kpis);
        Assert.Contains(plan.Notes, n => n.Contains("at most 32 characters", StringComparison.Ordinal));

        var draft = ProfileDraftBuilder.Build(Report(roleNames:
        [
            new("Payments Orchestration Service Primary Region West", 10),
            new("Payments Orchestration Service Primary Region East", 9),
        ]));
        Assert.All(draft.Profile.Services, s => Assert.True(s.Key.Length <= KpiDomainPolicy.MaxStoredLength, s.Key));
        Assert.Equal(2, draft.Profile.Services.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(8, draft.SuggestedKpis!.Count);
    }

    /// <summary>
    /// The browser forwards each suggestion verbatim, so the body the draft endpoint returns must be
    /// one the KPI API accepts for that profile: domain is a service key of at most 32 characters,
    /// params are valid, and the KPI is stored disabled.
    /// </summary>
    [Fact]
    public async Task SuggestedKpis_FromTheDraftResponse_PassTheKpiApiValidation()
    {
        var report = Report(
            roleNames: [new("Checkout API", 4_100), new("Payments Orchestration Service Primary Region West", 900)],
            dependencies: Dependencies(new ObservedDependencyTarget("api.watchlist.example", "Http", 6_000)));

        var draftResponse = await Discovery(report).DraftAsync(
            new OnboardingHttpRequest("POST", "https://localhost/api/onboarding/draft",
                    JsonSerializer.Serialize(new { alias = Alias, profileKey = "contoso", displayName = "Contoso" }))
                .WithHeader("X-Internal-Api-Secret", InternalSecret),
            default);
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        var json = draftResponse.ReadJson();

        Assert.Null(json["draft"]!["suggestedKpis"]);
        var suggestions = json["suggestedKpis"]!.AsArray();
        Assert.Equal(9, suggestions.Count);
        Assert.All(suggestions, s => Assert.False(s!["definition"]!["enabled"]!.GetValue<bool>()));

        // The profile as a reviewer would store it: saved, then enabled - the KPI API only accepts a stored, enabled profile.
        var profile = JsonSerializer.Deserialize<CompanyProfile>(json["draft"]!["profile"]!.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            with { Enabled = true };
        var profiles = new InMemoryCompanyProfileStore([profile]);
        var kpiStore = new InMemoryKpiDefinitionStore();
        var admin = new KpiDefinitionAdminFunctions(
            kpiStore,
            Options.Create(new MonitoringOptions { SettingsWriteSecret = WriteSecret }),
            TimeProvider.System,
            NullLogger<KpiDefinitionAdminFunctions>.Instance,
            new KpiProfileResolver(profileStore: profiles),
            profiles);

        foreach (var suggestion in suggestions)
        {
            var body = suggestion!["definition"]!.ToJsonString();
            var response = await admin.UpsertAsync(
                new OnboardingHttpRequest("POST", "https://localhost/api/settings/kpis", body).WithHeader("X-Settings-Write-Secret", WriteSecret),
                default);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{suggestion["key"]}: {response.ReadJson().ToJsonString()}");
        }

        var stored = await kpiStore.GetAllAsync("contoso", default);
        Assert.Equal(9, stored.Count);
        Assert.All(stored, d => Assert.False(d.Enabled));
        Assert.All(stored, d => Assert.Empty(d.Validate()));
        Assert.Single(stored, d => d.SourceKey == PartnerKpi.SourceKey && d.Grain == KpiGrain.Entity);
        Assert.Equal(4, stored.Count(d => d.Grain == KpiGrain.Entity && d.SourceKey != PartnerKpi.SourceKey));
    }

    public static TheoryData<string> EveryStepMatcherKind()
    {
        var data = new TheoryData<string>();
        foreach (var kind in CompanyProfileValidator.StepMatcherKinds) data.Add(kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStepMatcherKind))]
    public void Validator_AcceptsEveryStepMatcherKindTheModelDefines(string kind)
    {
        var result = StructuralProfileWriteValidator.Instance.Validate(ProfileWithStep(new StepMatcher(kind, "/cart")));

        Assert.True(result.IsValid, string.Join(" | ", result.Problems));
    }

    /// <summary>Regression: saving a profile that uses <c>path-contains</c> was refused with a 400.</summary>
    [Fact]
    public async Task Upsert_WithAPathContainsMatcher_IsSaved()
    {
        var store = new InMemoryCompanyProfileStore();
        var admin = new CompanyProfileAdminFunctions(
            store,
            Options.Create(new MonitoringOptions { SettingsWriteSecret = WriteSecret }),
            NullLogger<CompanyProfileAdminFunctions>.Instance);
        var profile = ProfileWithStep(new StepMatcher(StepMatcherKind.PathContains, "/cart"));

        var response = await admin.UpsertAsync(
            new OnboardingHttpRequest("PUT", "https://localhost/api/settings/company-profiles/contoso",
                    JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                .WithHeader("X-Settings-Write-Secret", WriteSecret),
            "contoso",
            default);

        Assert.True(response.StatusCode == HttpStatusCode.OK, response.ReadJson().ToJsonString());
        var saved = await store.GetAsync("contoso");
        Assert.Equal(StepMatcherKind.PathContains, saved!.Services[0].Flows[0].Steps[0].Matchers[0].Kind);
    }

    [Fact]
    public async Task Profiler_MeasuresDependencyTargets_OnlyWhenTheDependenciesTableHasRows()
    {
        var source = new ScriptedTelemetrySource(kql =>
        {
            if (kql.Contains("summarize RowCount = count()", StringComparison.Ordinal)) return ProbeRows.Count(10_000);
            if (kql.Contains("DependencyType", StringComparison.Ordinal))
            {
                return
                [
                    ProbeRows.Row("""{"Target":"api.watchlist.example","DependencyType":"Http","ObservationCount":700,"SampledRows":1000}"""),
                    ProbeRows.Row("""{"Target":"","DependencyType":"InProc","ObservationCount":300,"SampledRows":1000}"""),
                ];
            }
            return [];
        });
        var profiler = Profiler(source);

        var withDependencies = await profiler.ProfileAsync(ProbeRequest(TelemetryProbeTables.Requests, TelemetryProbeTables.Dependencies));

        var dependencies = withDependencies.Dependencies!;
        Assert.Equal(TelemetryProbeState.Ok, dependencies.State);
        Assert.Equal(1000, dependencies.SampledRows);
        var target = Assert.Single(dependencies.Targets);
        Assert.Equal("api.watchlist.example", target.Target);
        Assert.Equal(700, target.ObservationCount);
        Assert.Contains("{{SOURCE:" + TelemetrySourceDefaults.Club + "}}').dependencies", dependencies.Kql, StringComparison.Ordinal);

        var executed = source.ExecutedKql.Count;
        var withoutDependencies = await profiler.ProfileAsync(ProbeRequest(TelemetryProbeTables.Requests));
        Assert.Null(withoutDependencies.Dependencies);
        Assert.DoesNotContain(source.ExecutedKql.Skip(executed), k => k.Contains("DependencyType", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------------------------------

    private static TelemetryResourceProfile Report(
        IReadOnlyList<ObservedRoleName>? roleNames = null,
        TelemetryDependencyProfile? dependencies = null)
    {
        var to = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var candidate = EntityIdentityCandidateRanker.Score("requests", new CustomDimensionProfile("TenantId", 8_800, 8_800, 40, 12, 88.0, 88.0));
        return new TelemetryResourceProfile(
            Alias: Alias,
            Environment: "production",
            WindowFrom: to.AddHours(-24),
            WindowTo: to,
            WindowHours: 24,
            MaxSampledRows: 50_000,
            Status: TelemetryProfileStatus.Complete,
            Error: null,
            Tables: [],
            IdentityCandidates: [candidate],
            Hosts: new TelemetryHostProfile(TelemetryProbeState.Ok, null, "union isfuzzy=true ...", [], roleNames ?? []),
            Warnings: [],
            GeneratedAt: to,
            Dependencies: dependencies);
    }

    private static TelemetryDependencyProfile Dependencies(params ObservedDependencyTarget[] targets) =>
        new(TelemetryProbeState.Ok, null, "dependencies probe", 10_000, targets);

    private static CompanyProfile Profile(IReadOnlyList<ServiceDefinition> services, IReadOnlyList<PartnerDefinition>? partners = null) => new(
        ProfileKey: "contoso",
        DisplayName: "Contoso",
        Labels: new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        Services: services,
        EntityIdentitySources: [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant")],
        Glossary: new CompanyGlossary("service", "flow", "step", "entity", "owner", "Contoso sells widgets."),
        Partners: partners);

    private static CompanyProfile ProfileWithStep(StepMatcher matcher) => Profile(
    [
        new ServiceDefinition("checkout", "Checkout", [Alias], [],
        [
            new FlowDefinition("purchase", "Purchase", [new StepDefinition("01-cart", "01 Cart", 1, [matcher])]),
        ]),
    ]);

    private static TelemetryDiscoveryFunctions Discovery(TelemetryResourceProfile report) =>
        new(new FixedProfiler(report),
            Options.Create(new MonitoringOptions { InternalApiSecret = InternalSecret }),
            NullLogger<TelemetryDiscoveryFunctions>.Instance);

    private static TelemetryProfileRequest ProbeRequest(params string[] tables) =>
        new(TelemetrySourceDefaults.Club, InsightsEnvironment.Production, WindowHours: 24, WindowTo: new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), Tables: tables);

    private static TelemetryResourceProfiler Profiler(ScriptedTelemetrySource source) =>
        new(
            source,
            new FakeTelemetrySourceStore(new TelemetrySourceEntry(
                TelemetrySourceDefaults.Club,
                InsightsEnvironment.Production,
                TelemetrySourceKind.AppInsights,
                "resource-id",
                AppId: "00000000-0000-0000-0000-000000000001",
                TenantId: "00000000-0000-0000-0000-000000000002")),
            new FakeKqlSourceResolver(),
            NullLogger<TelemetryResourceProfiler>.Instance);

    private sealed class FixedProfiler(TelemetryResourceProfile report) : ITelemetryResourceProfiler
    {
        public Task<TelemetryResourceProfile> ProfileAsync(TelemetryProfileRequest request, CancellationToken ct = default) =>
            Task.FromResult(report);
    }
}
