using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Privacy;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Tests.Monitoring.ContractV4;

/// <summary>Contract v4 (contracts-scout-detection.md §1-§5) as code: shapes, defaults and the v3-preserving interface defaults.</summary>
public class ContractV4Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void V4_KpiDefinition_GrainDefaultsToDomain()
    {
        var definition = Valid();

        Assert.Equal(KpiGrain.Domain, definition.Grain);
        Assert.Equal(500, definition.MaxEntitiesPerBucket);
        Assert.Equal(KpiGrain.Entity, (definition with { Grain = KpiGrain.Entity }).Grain);
        Assert.Equal(["Domain", "Entity"], Enum.GetNames<KpiGrain>());
        Assert.Empty((definition with { Grain = KpiGrain.Entity }).Validate());
        Assert.Contains("Grain", Assert.Single((definition with { Grain = (KpiGrain)7 }).Validate()));
        Assert.Equal("entity", JsonNamingPolicy.CamelCase.ConvertName(nameof(KpiGrain.Entity)));
        Assert.Equal("domain", JsonNamingPolicy.CamelCase.ConvertName(nameof(KpiGrain.Domain)));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(5000, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(5001, false)]
    public void V4_KpiDefinition_MaxEntitiesRange(int maxEntities, bool valid)
    {
        var errors = (Valid() with { MaxEntitiesPerBucket = maxEntities }).Validate();

        if (valid)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Equal("MaxEntitiesPerBucket must be 1..5000.", Assert.Single(errors));
        }
    }

    [Fact]
    public void V4_KpiBucket_EntityKeyDefaultsEmpty()
    {
        var bucket = new KpiBucket(1, T0, 10, 10, 1, 2, 1.5, 0.1, null);
        var state = new KpiDetectionState(1, 0, null, null, null);
        var anomaly = new KpiAnomaly(0, 1, T0, T0, 2, 1, 1, "x", null);

        Assert.Equal("", bucket.EntityKey);
        Assert.Null(bucket.FailedCount);
        Assert.Equal("", state.EntityKey);
        Assert.Equal("", anomaly.EntityKey);
        Assert.Equal("", KpiDetectionState.Initial(1).EntityKey);
        Assert.Equal(KpiDetectionState.Initial(1), KpiDetectionState.Initial(1, null));
        Assert.Equal("club:7", KpiDetectionState.Initial(1, "club:7").EntityKey);

        // EntityKey is part of record equality, so a domain row never equals an entity row.
        Assert.NotEqual(bucket, bucket with { EntityKey = "club:7" });
        Assert.Equal(3, (bucket with { FailedCount = 3 }).FailedCount);
    }

    [Fact]
    public void V4_KpiSourceBucket_OptionalStats()
    {
        var v1 = new KpiSourceBucket(T0, 1200, null);

        Assert.Equal("", v1.EntityKey);
        Assert.Null(v1.P50);
        Assert.Null(v1.P95);
        Assert.Null(v1.Mean);
        Assert.Null(v1.FailedCount);
        Assert.Equal(new KpiSourceBucket(T0, 1200, null), v1);

        var entity = v1 with { EntityKey = "club:7", P50 = 100, P95 = 900, Mean = 210, FailedCount = 4 };

        Assert.Equal(("club:7", 100d, 900d, 210d, 4L), (entity.EntityKey, entity.P50!.Value, entity.P95!.Value, entity.Mean!.Value, entity.FailedCount!.Value));
        Assert.NotEqual(v1, entity);
    }

    [Theory]
    [InlineData("hotels")]
    [InlineData("transaction-monitoring")]
    [InlineData("screening")]
    public void V4_KpiDefinition_DomainNotRestrictedToFixedList(string domain)
    {
        Assert.Empty((Valid() with { Domain = domain }).Validate());
        Assert.Equal(["hotels", "cars", "cruises", "air"], KpiDomains.All);
        Assert.Equal("Domain is required (max 64).", Assert.Single((Valid() with { Domain = "" }).Validate()));
        Assert.Equal("Domain is required (max 64).", Assert.Single((Valid() with { Domain = "  " }).Validate()));
        Assert.Equal("Domain is required (max 64).", Assert.Single((Valid() with { Domain = new string('d', 65) }).Validate()));
        Assert.Empty((Valid() with { Domain = new string('d', 64) }).Validate());
    }

    [Fact]
    public void V4_CompanyProfile_PartnersAndPrivacyOptional()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var v3 = JsonSerializer.Deserialize<CompanyProfile>(MinimalProfileJson, options)!;

        Assert.Null(v3.Partners);
        Assert.Null(v3.Privacy);
        Assert.False(CompanyProfileValidator.HasErrors(CompanyProfileValidator.Validate(v3)));

        var v4 = v3 with
        {
            Partners = [new PartnerDefinition("watchlists", "Watchlist provider", [new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "watchlist.example")])],
            Privacy = new PrivacySettings(PiiDimensions: ["PlayerName"], Patterns: [@"\d{9}"]),
        };
        var roundTrip = JsonSerializer.Deserialize<CompanyProfile>(JsonSerializer.Serialize(v4, options), options)!;

        var partner = Assert.Single(roundTrip.Partners!);
        Assert.Equal(("watchlists", "Watchlist provider"), (partner.Key, partner.DisplayName));
        Assert.Equal(new PartnerMatcher("dependency-target-contains", "watchlist.example"), Assert.Single(partner.Matchers));
        Assert.False(roundTrip.Privacy!.AllowRawEvidence);
        Assert.Equal(["PlayerName"], roundTrip.Privacy.PiiDimensions!);
        Assert.Equal([@"\d{9}"], roundTrip.Privacy.Patterns!);

        var closed = new PrivacySettings();
        Assert.False(closed.AllowRawEvidence);
        Assert.Null(closed.PiiDimensions);
        Assert.Null(closed.Patterns);

        Assert.Equal(
            ["dependency-target-contains", "dependency-name-contains", "dependency-type-equals", "regex"],
            PartnerMatcherKind.All);

        IEvidenceRedactor redactor = new PassThroughRedactor();
        var result = redactor.Redact("text", v4);
        Assert.Equal("text", result.Text);
        Assert.Empty(result.CountsByPattern);
    }

    [Fact]
    public void V4_ScoutShadowComparison_Shape()
    {
        var runId = Guid.NewGuid();
        var row = new ScoutShadowComparison(
            RunId: runId,
            Board: ScoutBoard.Latency,
            ProfileKey: "arrivia",
            EntityKey: "club:7",
            Stage: "search",
            BucketStart: T0,
            RuleRank: 3,
            MethodVerdict: ScoutShadowMethodVerdict.Anomalous,
            KpiAnomalyId: 42,
            Agreement: ScoutShadowAgreement.Both);

        Assert.Equal(
            ["RunId", "Board", "ProfileKey", "EntityKey", "Stage", "BucketStart", "RuleRank", "MethodVerdict", "KpiAnomalyId", "Agreement"],
            typeof(ScoutShadowComparison).GetConstructors().Single().GetParameters().Select(p => p.Name!));
        Assert.Equal(runId, row.RunId);
        Assert.Equal(["both", "rules-only", "method-only"], ScoutShadowAgreement.All);
        Assert.Equal(["anomalous", "normal", "not-evaluated"], ScoutShadowMethodVerdict.All);

        var rulesOnly = row with { RuleRank = 3, MethodVerdict = ScoutShadowMethodVerdict.NotEvaluated, KpiAnomalyId = null, Agreement = ScoutShadowAgreement.RulesOnly };
        var methodOnly = row with { RuleRank = null, Agreement = ScoutShadowAgreement.MethodOnly };
        Assert.Null(rulesOnly.KpiAnomalyId);
        Assert.Null(methodOnly.RuleRank);

        Assert.Equal(6, new ScoutShadowCounts(Both: 1, RulesOnly: 2, MethodOnly: 3).Total);

        Assert.Equal(ScoutDetectionMode.Rules, new ScoutOptions().ResolvedDetectionMode);
        Assert.Equal(ScoutDetectionMode.Method, new ScoutOptions { DetectionMode = ScoutDetectionMode.Method }.ResolvedDetectionMode);
        Assert.Equal(["Rules", "Method"], Enum.GetNames<ScoutDetectionMode>());

        var store = typeof(IScoutShadowStore);
        Assert.NotNull(store.GetMethod(nameof(IScoutShadowStore.AddRangeAsync)));
        Assert.NotNull(store.GetMethod(nameof(IScoutShadowStore.GetRecentAsync)));
        Assert.NotNull(store.GetMethod(nameof(IScoutShadowStore.GetCountsAsync)));
    }

    [Fact]
    public async Task V4_StoreInterfaces_DefaultsKeepV3Behaviour()
    {
        var domainBucket = new KpiBucket(1, T0, 10, 10, 1, 2, 1.5, 0.1, null);
        var domainState = new KpiDetectionState(1, 2, T0, null, T0);
        var domainAnomaly = new KpiAnomaly(5, 1, T0, T0, 2, 1, 1, "x", null);

        IKpiBucketStore buckets = new V3BucketStore([domainBucket]);
        IKpiStateStore states = new V3StateStore(domainState);
        IKpiAnomalyStore anomalies = new V3AnomalyStore([domainAnomaly]);

        Assert.Equal([domainBucket], await buckets.GetRangeAsync(1, T0, T0.AddHours(1), (string?)null));
        Assert.Equal([domainBucket], await buckets.GetRangeAsync(1, T0, T0.AddHours(1), ""));
        Assert.Empty(await buckets.GetRangeAsync(1, T0, T0.AddHours(1), "club:7"));
        Assert.Empty(await buckets.GetEntityRangeAsync(1, T0, T0.AddHours(1)));

        Assert.Equal(domainState, await states.GetAsync(1, (string?)null));
        Assert.Equal(domainState, await states.GetAsync(1, ""));
        Assert.Equal(KpiDetectionState.Initial(1, "club:7"), await states.GetAsync(1, "club:7"));
        Assert.Empty(await states.GetEntityStatesAsync(1));

        Assert.Equal([domainAnomaly], await anomalies.GetRecentAsync(null, null, 10, null, (string?)null));
        Assert.Equal([domainAnomaly], await anomalies.GetRecentAsync(null, null, 10, null, ""));
        Assert.Empty(await anomalies.GetRecentAsync(null, null, 10, null, "club:7"));
    }

    private const string MinimalProfileJson = """
        {
          "profileKey": "acme",
          "displayName": "Acme",
          "labels": { "service": "Product", "flow": "Flow", "step": "Step", "entity": "Operator", "owner": "Team" },
          "services": [
            {
              "key": "screening",
              "displayName": "Screening",
              "telemetrySourceAliases": ["acme-screening"],
              "hostFilters": [],
              "flows": [
                {
                  "key": "main",
                  "displayName": "Main",
                  "steps": [
                    { "key": "check", "displayName": "Check", "order": 1, "matchers": [ { "kind": "path-contains", "value": "/check" } ] }
                  ]
                }
              ]
            }
          ],
          "entityIdentitySources": [ { "order": 1, "kind": "custom-dimension", "key": "OperatorId", "entityKind": "operator" } ],
          "glossary": { "serviceNoun": "product", "flowNoun": "flow", "stepNoun": "step", "entityNoun": "operator", "ownerNoun": "team" }
        }
        """;

    private static KpiDefinition Valid() => new(
        KpiDefinitionId: 0,
        Pillar: KpiPillar.Application,
        Kpi: "Latency",
        Domain: "hotels",
        Stage: "",
        SourceKey: "appinsights:latency",
        Direction: KpiDirection.Increase,
        Aggregation: KpiAggregation.P95,
        EvaluationFrequency: TimeSpan.FromMinutes(5),
        BucketSize: TimeSpan.FromHours(1),
        LookbackWindow: TimeSpan.FromDays(1),
        SampleSize: 4000,
        MinObservationsPerBucket: 30,
        DetectionMethod: DetectionMethodKind.RobustZScore,
        MethodParamsJson: "{\"windowBuckets\":24,\"zThreshold\":3}",
        ConsecutiveCount: 2,
        CooldownMinutes: 120,
        Enabled: true,
        UpdatedAt: DateTimeOffset.UnixEpoch,
        UpdatedBy: "test");

    // v3-only implementations: they implement none of the v4 members, which is the point.
    private sealed class V3BucketStore(IReadOnlyList<KpiBucket> rows) : IKpiBucketStore
    {
        public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KpiBucket>>(rows.Where(b => b.KpiDefinitionId == kpiDefinitionId && b.BucketStart >= fromInclusive && b.BucketStart < toExclusive).ToList());
    }

    private sealed class V3StateStore(KpiDetectionState state) : IKpiStateStore
    {
        public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
            Task.FromResult(state.KpiDefinitionId == kpiDefinitionId ? state : KpiDetectionState.Initial(kpiDefinitionId));

        public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class V3AnomalyStore(IReadOnlyList<KpiAnomaly> rows) : IKpiAnomalyStore
    {
        public Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default) => Task.FromResult(anomaly);

        public Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default) =>
            Task.FromResult(rows);
    }

    private sealed class PassThroughRedactor : IEvidenceRedactor
    {
        public RedactionResult Redact(string text, CompanyProfile profile) => new(text, new Dictionary<string, int>());
    }
}
