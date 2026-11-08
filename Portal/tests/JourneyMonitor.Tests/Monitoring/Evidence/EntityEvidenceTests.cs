using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Monitoring.Privacy;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.DependencyInjection;
using JourneyMonitor.Infrastructure.Fabric;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Evidence;

/// <summary>FX3: Triage evidence carries a compact, budgeted view of the top entities of entity-grain KPIs, redacted like the rest.</summary>
public sealed class EntityEvidenceTests
{
    private const string Acme = "acme";
    private const int TriageSectionCut = 4_000;
    private static readonly DateTimeOffset DetectedAt = A5TriageFixtures.DetectedAt;
    private static readonly DateTimeOffset From = DetectedAt.AddHours(-3);
    private static readonly DateTimeOffset To = DetectedAt.AddMinutes(15);

    [Fact]
    public async Task Evidence_EntityKpi_IncludesTopEntitySeries()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var domainLatency = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels"), "test");
        var entityLatency = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels") with { Grain = KpiGrain.Entity }, "test");
        var entityOrders = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "hotels") with { Grain = KpiGrain.Entity }, "test");

        var buckets = new InMemoryKpiBucketStore();
        await buckets.UpsertAsync([Bucket(domainLatency.KpiDefinitionId, "", DetectedAt.AddMinutes(-5), 300)]);
        // Eight clubs; club-i ends i*10% above a flat 100, plus "club-steady": flat but with an anomaly in the window.
        for (var i = 0; i < 8; i++)
        {
            await buckets.UpsertAsync(Series(entityLatency.KpiDefinitionId, $"club-{i}", 100, 100 + i * 10, 6));
            await buckets.UpsertAsync(Series(entityOrders.KpiDefinitionId, $"club-{i}", 100, 100 + i * 10, 6));
        }
        await buckets.UpsertAsync(Series(entityLatency.KpiDefinitionId, "club-steady", 100, 100, 6));
        await buckets.UpsertAsync([Bucket(entityLatency.KpiDefinitionId, "club-old", DetectedAt.AddDays(-1), 99999)]);

        var anomalies = new InMemoryKpiAnomalyStore(definitions);
        await anomalies.AddAsync(Anomaly(entityLatency.KpiDefinitionId, "club-steady", DetectedAt, 100, 50));
        await anomalies.AddAsync(Anomaly(entityLatency.KpiDefinitionId, "club-1", DetectedAt.AddDays(-2), 500, 100));

        var application = new ApplicationEvidenceProvider(
            definitions, buckets, new A5IncidentReader(), NullLogger<ApplicationEvidenceProvider>.Instance, anomalies);
        var section = await application.CollectAsync(Signal("Latency", "hotels", stage: null), From, To);

        Assert.True(section.Available, section.UnavailableReason);
        var data = section.Data!.Value;
        // Domain KPIs keep their shape; the entity KPI is no longer an empty-bucket entry there.
        var kpi = Assert.Single(data.GetProperty("kpis").EnumerateArray());
        Assert.Equal(domainLatency.KpiDefinitionId, kpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(1, kpi.GetProperty("buckets").GetArrayLength());

        var entityKpi = Assert.Single(data.GetProperty("entityKpis").EnumerateArray());
        Assert.Equal(entityLatency.KpiDefinitionId, entityKpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(9, entityKpi.GetProperty("entitiesInWindow").GetInt32());
        var entities = entityKpi.GetProperty("entities").EnumerateArray().ToList();
        Assert.Equal(EntityEvidence.DefaultTopEntities, entities.Count);
        // Anomalous in the window first, then by largest deviation from the entity's own window mean.
        Assert.Equal(
            new[] { "club-steady", "club-7", "club-6", "club-5", "club-4" },
            entities.Select(e => e.GetProperty("entityKey").GetString()).ToArray());
        Assert.True(entities[0].GetProperty("anomalous").GetBoolean());
        Assert.True(entities[0].GetProperty("focus").GetBoolean());
        Assert.False(entities[1].GetProperty("anomalous").GetBoolean());
        Assert.Equal(170, entities[1].GetProperty("latest").GetDouble());
        Assert.Equal(170, entities[1].GetProperty("max").GetDouble());
        Assert.Equal(111.67, entities[1].GetProperty("mean").GetDouble());
        Assert.Equal(52.24, entities[1].GetProperty("deviationPercent").GetDouble());
        // Summaries only: the raw series belongs to the focus entity (the top anomalous one here).
        Assert.All(entities, e => Assert.False(e.TryGetProperty("buckets", out _)));
        var focus = entityKpi.GetProperty("focus");
        Assert.Equal("club-steady", focus.GetProperty("entityKey").GetString());
        Assert.Equal(6, focus.GetProperty("values").GetArrayLength());
        Assert.Equal(5, focus.GetProperty("bucketMinutes").GetInt32());
        Assert.Equal(100, focus.GetProperty("anomaly").GetProperty("observed").GetDouble());
        Assert.DoesNotContain("club-old", data.GetRawText());

        // Business evidence (non-incumbent) carries its entity KPIs too, after the domain KPI list.
        var business = new KpiBusinessEvidenceProvider(definitions, buckets, anomalies, NullLogger<KpiBusinessEvidenceProvider>.Instance);
        var businessSection = await business.CollectAsync(Signal("Orders", "hotels", stage: null) with { Pillar = "Business Health" }, From, To);
        Assert.True(businessSection.Available, businessSection.UnavailableReason);
        Assert.Equal(0, businessSection.Data!.Value.GetProperty("kpis").GetArrayLength());
        var businessEntity = Assert.Single(businessSection.Data!.Value.GetProperty("entityKpis").EnumerateArray());
        Assert.Equal(entityOrders.KpiDefinitionId, businessEntity.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(
            new[] { "club-7", "club-6", "club-5", "club-4", "club-3" },
            businessEntity.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("entityKey").GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, businessEntity.GetProperty("focus").ValueKind);
    }

    [Fact]
    public async Task Evidence_PartnerSignal_IncludesPartnerSeries()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var partners = await definitions.UpsertAsync(
            Definition(KpiPillar.Application, "PartnerFailures", "screening") with { Grain = KpiGrain.Entity, SourceKey = PartnerKpi.SourceKey },
            "test");
        var buckets = new InMemoryKpiBucketStore();
        for (var i = 0; i < 8; i++)
        {
            await buckets.UpsertAsync(Series(partners.KpiDefinitionId, $"partner-{i}", 10, 10 + i * 20, 20));
        }
        // The partner of the signal is flat: by deviation alone it would never make the top five.
        await buckets.UpsertAsync(Series(partners.KpiDefinitionId, "watchlists", 5, 5, 20));
        var anomalies = new InMemoryKpiAnomalyStore(definitions);
        await anomalies.AddAsync(Anomaly(partners.KpiDefinitionId, "watchlists", DetectedAt, 5, 1));
        var application = new ApplicationEvidenceProvider(
            definitions, buckets, new A5IncidentReader(), NullLogger<ApplicationEvidenceProvider>.Instance, anomalies);

        var plain = await application.CollectAsync(Signal("PartnerFailures", "screening", stage: null) with { Kpi = "Other" }, From, To);
        var plainKpi = Assert.Single(plain.Data!.Value.GetProperty("entityKpis").EnumerateArray());
        // Without the partner stage the anomalous partner still ranks first, but nothing forces it.
        Assert.Equal("watchlists", plainKpi.GetProperty("entities")[0].GetProperty("entityKey").GetString());

        var signal = Signal("PartnerFailures", "screening", stage: PartnerKpi.TriageStage("watchlists"));
        Assert.Equal("watchlists", EntityEvidence.PartnerKeyOf(signal));
        Assert.Null(EntityEvidence.PartnerKeyOf(signal with { Stage = "checkout" }));
        Assert.Null(EntityEvidence.PartnerKeyOf(signal with { Stage = "partner:" }));

        // No anomaly for this partner: the stage alone puts its series in focus.
        var other = Signal("PartnerFailures", "screening", stage: PartnerKpi.TriageStage("partner-0"));
        var otherSection = await application.CollectAsync(other, From, To);
        var otherKpi = Assert.Single(otherSection.Data!.Value.GetProperty("entityKpis").EnumerateArray());
        Assert.Equal("partner-0", otherKpi.GetProperty("entities")[0].GetProperty("entityKey").GetString());
        Assert.True(otherKpi.GetProperty("entities")[0].GetProperty("focus").GetBoolean());
        Assert.Equal("partner-0", otherKpi.GetProperty("focus").GetProperty("entityKey").GetString());
        Assert.Equal(EntityEvidence.MaxFocusPoints, otherKpi.GetProperty("focus").GetProperty("values").GetArrayLength());
        Assert.All(otherKpi.GetProperty("focus").GetProperty("values").EnumerateArray(), v => Assert.Equal(10, v.GetDouble()));
        Assert.Equal(JsonValueKind.Null, otherKpi.GetProperty("focus").GetProperty("anomaly").ValueKind);

        var section = await application.CollectAsync(signal, From, To);

        Assert.True(section.Available, section.UnavailableReason);
        var entityKpi = Assert.Single(section.Data!.Value.GetProperty("entityKpis").EnumerateArray());
        var entities = entityKpi.GetProperty("entities").EnumerateArray().ToList();
        Assert.Equal(EntityEvidence.DefaultTopEntities, entities.Count);
        Assert.Equal("watchlists", entities[0].GetProperty("entityKey").GetString());
        Assert.True(entities[0].GetProperty("focus").GetBoolean());
        Assert.All(entities.Skip(1), e => Assert.False(e.GetProperty("focus").GetBoolean()));
        Assert.Equal(new[] { "partner-7", "partner-6", "partner-5", "partner-4" }, entities.Skip(1).Select(e => e.GetProperty("entityKey").GetString()).ToArray());
        var focus = entityKpi.GetProperty("focus");
        Assert.Equal("watchlists", focus.GetProperty("entityKey").GetString());
        Assert.Equal(EntityEvidence.MaxFocusPoints, focus.GetProperty("values").GetArrayLength());
        Assert.All(focus.GetProperty("values").EnumerateArray(), v => Assert.Equal(5, v.GetDouble()));
        Assert.Equal(DetectedAt.AddMinutes(-5 * EntityEvidence.MaxFocusPoints), focus.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(5, focus.GetProperty("anomaly").GetProperty("observed").GetDouble());
    }

    [Fact]
    public async Task Evidence_EntitySeries_BoundedAndRedacted()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var buckets = new InMemoryKpiBucketStore();
        var ids = new List<int>();
        for (var k = 0; k < 5; k++)
        {
            var definition = await definitions.UpsertAsync(
                Definition(KpiPillar.Application, k == 0 ? "Latency" : $"Latency{k}", "hotels") with { Grain = KpiGrain.Entity }, "test");
            ids.Add(definition.KpiDefinitionId);
            for (var i = 0; i < 12; i++)
            {
                var key = i % 2 == 0 ? $"jane.doe{i}@example.com" : $"MBR-{100000 + i}";
                await buckets.UpsertAsync(Series(definition.KpiDefinitionId, key, 100, 100 + i * 10, 36));
            }
        }
        var anomalies = new InMemoryKpiAnomalyStore(definitions);
        await anomalies.AddAsync(Anomaly(ids[0], "jane.doe0@example.com", DetectedAt, 300, 100));
        var application = new ApplicationEvidenceProvider(
            definitions, buckets, new A5IncidentReader(), NullLogger<ApplicationEvidenceProvider>.Instance, anomalies);

        var signal = Signal("Latency", "hotels", stage: null);
        var section = await application.CollectAsync(signal, From, To);

        Assert.True(section.Available, section.UnavailableReason);
        var entityJson = section.Data!.Value.GetProperty("entityKpis");
        Assert.True(entityJson.GetRawText().Length <= EntityEvidence.MaxPayloadChars, $"entity payload {entityJson.GetRawText().Length} chars");
        var entityKpis = entityJson.EnumerateArray().ToList();
        Assert.InRange(entityKpis.Count, 1, EntityEvidence.MaxEntityKpis);
        Assert.Equal(ids[0], entityKpis[0].GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal("jane.doe0@example.com", entityKpis[0].GetProperty("focus").GetProperty("entityKey").GetString());
        Assert.All(entityKpis, k =>
        {
            Assert.Equal(12, k.GetProperty("entitiesInWindow").GetInt32());
            Assert.InRange(k.GetProperty("entities").GetArrayLength(), 0, EntityEvidence.DefaultTopEntities);
            if (k.GetProperty("focus").ValueKind == JsonValueKind.Object)
            {
                Assert.InRange(k.GetProperty("focus").GetProperty("values").GetArrayLength(), 1, EntityEvidence.MaxFocusPoints);
            }
        });
        // The focus entity is never trimmed by the budget.
        Assert.Contains(entityKpis[0].GetProperty("entities").EnumerateArray(), e => e.GetProperty("focus").GetBoolean());
        Assert.Contains("@example.com", section.Data!.Value.GetRawText());
        Assert.Contains("MBR-1000", section.Data!.Value.GetRawText());

        // Through Triage (AG6): a non-incumbent profile with a PII pattern never sends nor stores the keys.
        var profile = EvidenceRedactor.PlaceholderProfile(Acme) with
        {
            Privacy = new PrivacySettings(AllowRawEvidence: false, Patterns: [@"MBR-\d{6}"]),
        };
        var (result, prompt) = await TriageAsync(application, signal, new InMemoryCompanyProfileStore([profile]));

        Assert.Contains("entityKpis", prompt);
        Assert.DoesNotContain("@example.com", prompt);
        Assert.DoesNotContain("MBR-1000", prompt);
        Assert.Contains("[REDACTED:email]", prompt);

        var stored = result.Correlation.ApplicationHealth!.Value;
        var storedText = stored.GetRawText();
        Assert.DoesNotContain("@example.com", storedText);
        Assert.DoesNotContain("MBR-1000", storedText);
        var storedEntities = stored.GetProperty("entityKpis").EnumerateArray()
            .SelectMany(k => k.GetProperty("entities").EnumerateArray()).ToList();
        Assert.NotEmpty(storedEntities);
        Assert.All(storedEntities, e => Assert.StartsWith("[REDACTED:", e.GetProperty("entityKey").GetString()));
        Assert.StartsWith("[REDACTED:", stored.GetProperty("entityKpis")[0].GetProperty("focus").GetProperty("entityKey").GetString());
        // Values stay numeric after redaction: the summary shape survives.
        Assert.All(storedEntities, e => Assert.Equal(JsonValueKind.Number, e.GetProperty("latest").ValueKind));
    }

    [Fact]
    public async Task Evidence_RealisticSection_FitsTriageCut_KeepsDomainKpisAndIncidents()
    {
        // Incumbent: incidents are read and nothing is redacted, so the prompt line is the raw section.
        var profileKey = KpiProfiles.Default;
        var definitions = new InMemoryKpiDefinitionStore();
        var buckets = new InMemoryKpiBucketStore();
        var anomalies = new InMemoryKpiAnomalyStore(definitions);
        var latency = await definitions.UpsertAsync(
            Definition(KpiPillar.Application, "Latency", "hotels", profileKey) with { BucketSize = TimeSpan.FromMinutes(15) }, "test");
        await buckets.UpsertAsync(Enumerable.Range(0, 12)
            .Select(i => new KpiBucket(latency.KpiDefinitionId, From.AddMinutes(15 * i), 1200 + i, 50, 850.5, 4200.25, 1100.75, 300.5, 4200.25))
            .ToList());
        for (var k = 0; k < 3; k++)
        {
            var entity = await definitions.UpsertAsync(
                Definition(KpiPillar.Application, k == 0 ? "Latency" : $"EntityKpi{k}", "hotels", profileKey) with { Grain = KpiGrain.Entity }, "test");
            for (var i = 0; i < 5; i++)
            {
                var key = $"hotel-supplier-{k}-{i:D4}";
                await buckets.UpsertAsync(Series(entity.KpiDefinitionId, key, 1234.56 + i, 2345.67 + i * 100, 36));
                if (i == 0) await anomalies.AddAsync(Anomaly(entity.KpiDefinitionId, key, DetectedAt, 2345.67, 1234.56));
            }
        }
        var incidents = new A5IncidentReader();
        var application = new ApplicationEvidenceProvider(
            definitions, buckets, incidents, NullLogger<ApplicationEvidenceProvider>.Instance, anomalies);
        var signal = Signal("Latency", "hotels", stage: null) with { ProfileKey = profileKey };

        var section = await application.CollectAsync(signal, From, To);

        Assert.True(section.Available, section.UnavailableReason);
        var raw = section.Data!.Value.GetRawText();
        Assert.True(raw.Length <= TriageSectionCut, $"section {raw.Length} chars");
        Assert.True(section.Data!.Value.GetProperty("entityKpis").GetRawText().Length <= EntityEvidence.MaxPayloadChars);
        // Domain content comes first and is complete; entity content follows it.
        Assert.True(raw.IndexOf("\"kpis\"", StringComparison.Ordinal) < raw.IndexOf("\"entityKpis\"", StringComparison.Ordinal));
        Assert.True(raw.IndexOf("\"incidents\"", StringComparison.Ordinal) < raw.IndexOf("\"entityKpis\"", StringComparison.Ordinal));
        Assert.Equal(12, section.Data!.Value.GetProperty("kpis")[0].GetProperty("buckets").GetArrayLength());
        Assert.Equal(1, section.Data!.Value.GetProperty("incidents").GetArrayLength());
        Assert.NotEmpty(section.Data!.Value.GetProperty("entityKpis").EnumerateArray());

        var (_, prompt) = await TriageAsync(application, signal, profiles: null);

        // The whole section reaches the model: nothing was cut at 4,000 chars.
        Assert.Contains($"[{TriageDimensions.ApplicationHealth}] {raw}", prompt);
        Assert.Contains("System.TimeoutException", prompt);
        Assert.Contains("hotel-supplier-0-0000", prompt);
    }

    [Fact]
    public async Task Evidence_PartnerSignal_WithManyIncidents_KeepsPartnerSeriesInPrompt()
    {
        // Incumbent: the only profile whose incidents are read, and nothing is redacted.
        var profileKey = KpiProfiles.Default;
        var definitions = new InMemoryKpiDefinitionStore();
        var buckets = new InMemoryKpiBucketStore();
        var anomalies = new InMemoryKpiAnomalyStore(definitions);
        for (var d = 0; d < 2; d++)
        {
            var domainKpi = await definitions.UpsertAsync(
                Definition(KpiPillar.Application, d == 0 ? "Latency" : "Failures",                 "screening", profileKey) with { BucketSize = TimeSpan.FromMinutes(15) }, "test");
                            await buckets.UpsertAsync(Enumerable.Range(0, 12)
                                .Select(i => new KpiBucket(domainKpi.KpiDefinitionId, From.AddMinutes(15 * i), 1200 + i, 50, 850.5, 4200.25, 1100.75, 300.5, 4200.25))
                .ToList());
        }
        var partners = await definitions.UpsertAsync(
            Definition(KpiPillar.Application, "PartnerFailures", "screening", profileKey) with { Grain = KpiGrain.Entity, SourceKey = PartnerKpi.SourceKey },
            "test");
        for (var i = 0; i < 30; i++)
        {
            await buckets.UpsertAsync(Series(partners.KpiDefinitionId, $"partner-{i:D2}", 10, 10 + i * 7, 36));
            if (i % 3 == 0) await anomalies.AddAsync(Anomaly(partners.KpiDefinitionId, $"partner-{i:D2}", DetectedAt, 10 + i * 7, 10));
        }
        await buckets.UpsertAsync(Series(partners.KpiDefinitionId, "watchlists", 77.7, 555.5, 36));
        await anomalies.AddAsync(Anomaly(partners.KpiDefinitionId, "watchlists", DetectedAt, 555.5, 77.7));

        var incidents = new ManyIncidentsReader(10);
        var application = new ApplicationEvidenceProvider(
            definitions, buckets, incidents, NullLogger<ApplicationEvidenceProvider>.Instance, anomalies);
        var signal = Signal("PartnerFailures", "screening", stage: PartnerKpi.TriageStage("watchlists")) with { ProfileKey = profileKey };

        var section = await application.CollectAsync(signal, From, To);

        Assert.True(section.Available, section.UnavailableReason);
        var data = section.Data!.Value;
        Assert.Equal(2, data.GetProperty("kpis").GetArrayLength());
        Assert.All(data.GetProperty("kpis").EnumerateArray(), k => Assert.Equal(12, k.GetProperty("buckets").GetArrayLength()));
        var focus = data.GetProperty("focus");
        Assert.Equal("watchlists", focus.GetProperty("partnerKey").GetString());
        Assert.Equal(partners.KpiDefinitionId, focus.GetProperty("kpiDefinitionId").GetInt32());
        var values = focus.GetProperty("series").GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToList();
        Assert.Equal(EntityEvidence.MaxFocusPoints, values.Count);
        Assert.Equal(555.5, values[^1]);
        Assert.Equal(555.5, focus.GetProperty("series").GetProperty("anomaly").GetProperty("observed").GetDouble());
        // Incidents are budgeted: most severe first, the rest counted.
        var kept = data.GetProperty("incidents");
        Assert.True(kept.GetRawText().Length <= ApplicationEvidenceProvider.MaxIncidentChars, $"incidents {kept.GetRawText().Length} chars");
        Assert.InRange(kept.GetArrayLength(), 1, 9);
        Assert.Equal("critical", kept[0].GetProperty("severity").GetString());
        Assert.Equal($"truncated: {10 - kept.GetArrayLength()} more", data.GetProperty("incidentsTruncated").GetString());
        var raw = data.GetRawText();
        Assert.True(raw.IndexOf("\"focus\"", StringComparison.Ordinal) < raw.IndexOf("\"incidents\"", StringComparison.Ordinal));
        // Realistic load: the whole section is over the cut, so the prompt only gets its head.
        Assert.True(raw.Length > TriageSectionCut, $"section {raw.Length} chars");

        var (_, prompt) = await TriageAsync(application, signal, profiles: null);

        var marker = $"[{TriageDimensions.ApplicationHealth}] ";
        var line = prompt.Split('\n').Single(l => l.StartsWith(marker, StringComparison.Ordinal))[marker.Length..].TrimEnd('\r');
        Assert.True(line.Length <= TriageSectionCut + 3, $"prompt line {line.Length} chars");
        Assert.Contains("\"kpis\":[{", line);
        Assert.Contains("\"focus\":{\"partnerKey\":\"watchlists\"", line);
        Assert.Contains("555.5", line);
        Assert.Contains("77.7", line);
        Assert.Contains("\"incidents\":[{", line);
    }

    [Fact]
    public async Task Evidence_EntityReadFailure_KeepsDomainPart()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var appDomain = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels"), "test");
        await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels") with { Grain = KpiGrain.Entity }, "test");
        var bizDomain = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "hotels"), "test");
        await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "hotels") with { Grain = KpiGrain.Entity }, "test");
        var buckets = new EntityFailingBucketStore();
        await buckets.UpsertAsync(
        [
            Bucket(appDomain.KpiDefinitionId, "", DetectedAt.AddMinutes(-5), 300),
            Bucket(bizDomain.KpiDefinitionId, "", DetectedAt.AddMinutes(-5), 40),
        ]);
        var anomalies = new InMemoryKpiAnomalyStore(definitions);

        var application = new ApplicationEvidenceProvider(
            definitions, buckets, new A5IncidentReader(), NullLogger<ApplicationEvidenceProvider>.Instance, anomalies);
        var appSection = await application.CollectAsync(Signal("Latency", "hotels", stage: null), From, To);

        Assert.True(appSection.Available, appSection.UnavailableReason);
        var appData = appSection.Data!.Value;
        Assert.Equal(appDomain.KpiDefinitionId, Assert.Single(appData.GetProperty("kpis").EnumerateArray()).GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(JsonValueKind.Null, appData.GetProperty("entityKpis").ValueKind);
        Assert.Contains(appData.GetProperty("errors").EnumerateArray(), e => e.GetString()!.StartsWith("entityKpis: entity store down", StringComparison.Ordinal));

        var business = new KpiBusinessEvidenceProvider(definitions, buckets, anomalies, NullLogger<KpiBusinessEvidenceProvider>.Instance);
        var bizSection = await business.CollectAsync(Signal("Orders", "hotels", stage: null) with { Pillar = "Business Health" }, From, To);

        Assert.True(bizSection.Available, bizSection.UnavailableReason);
        var bizData = bizSection.Data!.Value;
        var bizKpi = Assert.Single(bizData.GetProperty("kpis").EnumerateArray());
        Assert.Equal(bizDomain.KpiDefinitionId, bizKpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(1, bizKpi.GetProperty("buckets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, bizData.GetProperty("entityKpis").ValueKind);
        Assert.Contains(bizData.GetProperty("errors").EnumerateArray(), e => e.GetString()!.StartsWith("entityKpis: entity store down", StringComparison.Ordinal));
        Assert.Equal(2, buckets.EntityReads);
    }

    [Fact]
    public async Task Evidence_ProductionComposition_InjectsAnomalyStoreIntoApplicationProvider()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton<IDataProtectionProvider>(new PassThroughDataProtectionProvider());
        services.AddJourneyMonitorInfrastructure(configuration);
        services.AddFabricHotelBookingKpis();
        services.AddJourneyMonitorMonitoring(configuration);
        services.AddSpec1000(configuration);
        services.AddSqlJourneyStore(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var definitions = sp.GetRequiredService<IKpiDefinitionStore>();
        var entity = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels") with { Grain = KpiGrain.Entity }, "test");
        var buckets = sp.GetRequiredService<IKpiBucketStore>();
        await buckets.UpsertAsync(Series(entity.KpiDefinitionId, "club-steady", 100, 100, 6));
        await buckets.UpsertAsync(Series(entity.KpiDefinitionId, "club-spiky", 100, 900, 6));
        await sp.GetRequiredService<IKpiAnomalyStore>().AddAsync(Anomaly(entity.KpiDefinitionId, "club-steady", DetectedAt, 100, 50));

        var application = Assert.Single(sp.GetServices<IEvidenceProvider>().OfType<ApplicationEvidenceProvider>());
        var section = await application.CollectAsync(Signal("Latency", "hotels", stage: null), From, To);

        // Only an injected anomaly store can rank the flat club first and flag it.
        var first = section.Data!.Value.GetProperty("entityKpis")[0].GetProperty("entities")[0];
        Assert.Equal("club-steady", first.GetProperty("entityKey").GetString());
        Assert.True(first.GetProperty("anomalous").GetBoolean());
    }

    private static async Task<(TriageResult Result, string Prompt)> TriageAsync(
        IEvidenceProvider application, TriageSignal signal, ICompanyProfileStore? profiles)
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued(signal: signal);
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        var providers = TriageDimensions.All
            .Where(d => d != TriageDimensions.ApplicationHealth)
            .Select(d => (IEvidenceProvider)new A5StubEvidenceProvider(d))
            .Append(application)
            .ToList();
        var triage = new TriageMonkeyService(
            store,
            providers,
            llm,
            new A5PauseResolver(),
            new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions { Mode = TriageMode.Shadow }),
            new A5FixedTimeProvider(A5TriageFixtures.Now),
            NullLogger<TriageMonkeyService>.Instance,
            profiles: profiles);

        var result = await triage.InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        return (result!, Assert.Single(llm.Prompts));
    }

    private static TriageSignal Signal(string kpi, string domain, string? stage) =>
        A5TriageFixtures.Signal(domain) with { Kpi = kpi, Stage = stage, ProfileKey = Acme };

    private static KpiDefinition Definition(KpiPillar pillar, string kpi, string domain, string profileKey = Acme) => new(
        0, pillar, kpi, domain, "", "kql:entity", KpiDirection.Increase, KpiAggregation.P95,
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), 0, 0,
        DetectionMethodKind.BaselinePercent, """{"percent":30}""", 2, 60, true, A5TriageFixtures.Now, "test")
    {
        ProfileKey = profileKey,
    };

    /// <summary><paramref name="count"/> 5-minute buckets ending at DetectedAt, all at <paramref name="baseline"/> but the last at <paramref name="last"/>.</summary>
    private static List<KpiBucket> Series(int id, string entityKey, double baseline, double last, int count) =>
        Enumerable.Range(0, count)
            .Select(i => Bucket(id, entityKey, DetectedAt.AddMinutes(-5 * (count - i)), i == count - 1 ? last : baseline))
            .ToList();

    private static KpiBucket Bucket(int id, string entityKey, DateTimeOffset start, double value) =>
        new(id, start, 10, 0, null, value, null, null, value) { EntityKey = entityKey, FailedCount = 1 };

    private static KpiAnomaly Anomaly(int id, string entityKey, DateTimeOffset at, double observed, double reference) =>
        new(0, id, at, at.AddMinutes(-5), observed, reference, observed - reference, "entity spike", null) { EntityKey = entityKey };

    private sealed class EntityFailingBucketStore : IKpiBucketStore
    {
        private readonly InMemoryKpiBucketStore _inner = new();

        public int EntityReads { get; private set; }

        public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default) => _inner.UpsertAsync(buckets, ct);

        public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
            _inner.GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct);

        public Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
        {
            EntityReads++;
            throw new InvalidOperationException("entity store down");
        }
    }

    private sealed class ManyIncidentsReader(int count) : IIncidentReader
    {
        public Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentReadQuery query, CancellationToken cancellationToken)
        {
            IReadOnlyList<IncidentRow> rows = Enumerable.Range(0, count)
                .Select(i => new IncidentRow(
                    $"ext-{i}", "screening", "watchlists", "production", "System.Net.Http.HttpRequestException", "screening-api",
                    $"POST /watchlists/check/{i}",
                    $"Response status code does not indicate success: 503 (Service Unavailable) calling watchlists partner, attempt {i}. " + new string('x', 250),
                    null, i == 7 ? "critical" : "high", "open", null,
                    DetectedAt.AddMinutes(-60 + i), DetectedAt.AddMinutes(-i), 40 + i, 12, null, null, [], []))
                .ToList();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyDictionary<string, int>> CountByDomainAsync(IncidentCountQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }

    private sealed class PassThroughDataProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
