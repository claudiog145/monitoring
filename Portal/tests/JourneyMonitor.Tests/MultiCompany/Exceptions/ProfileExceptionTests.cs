using System.Reflection;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.ProfileExceptions;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.MultiCompany.Exceptions;

/// <summary>Contract v6 §4.3 (MC2): exceptions ingested and read per company.</summary>
public class ProfileExceptionTests
{
    private static readonly DateTimeOffset Seen = DateTimeOffset.Parse("2026-11-07T20:00:00Z");

    [Fact]
    public async Task Exceptions_IngestedPerProfile()
    {
        var source = new FakeExceptionSource();
        source.Add("banco", "banca-web", Signal("bank-1", "B-003", "System.TimeoutException", "GET /banca/cuentas/saldos"), "saldos", "Saldos");
        source.Add("banco", "pagos", Signal("bank-2", "", "System.Net.Http.HttpRequestException", "POST /payments/v1/bill-payments"), "pago-servicios", "Pago de servicios");
        source.Add("mercadolibre", "checkout", Signal("ml-1", "MLB", "System.InvalidOperationException", "POST /api/checkout/orders"), "place-order", "Place order");
        var store = new RecordingIncidentStore();
        var service = SyncService(
            source,
            store,
            Profile("arrivia", ("hotels", "sor-frontend")),
            Profile("banco", ("banca-web", "bank-web-banking"), ("pagos", "bank-payments-svc")),
            Profile("mercadolibre", ("checkout", "retail-checkout-api")),
            Profile("apagada", ("svc", "apagada-app")) with { Enabled = false });

        var passes = await service.SyncAllAsync(CancellationToken.None);

        // The incumbent keeps its own pipeline; a disabled company is not read at all.
        Assert.DoesNotContain(source.Fetched, f => f.Profile is "arrivia" or "apagada");
        Assert.DoesNotContain(store.Calls, c => c.ProfileKey is "arrivia" or "apagada");
        Assert.Equal(["banco/banca-web", "banco/pagos", "mercadolibre/checkout"], source.Fetched.Select(f => $"{f.Profile}/{f.Service}"));
        Assert.All(passes, pass => Assert.True(pass.Stored, pass.SkipReason));

        var saldos = Assert.Single(store.Calls, c => c.ProfileKey == "banco" && c.Request.Domain == "banca-web");
        var incident = Assert.Single(saldos.Request.Incidents);
        Assert.Equal("B-003", incident.ClubId);
        Assert.Equal("Saldos", incident.Component);
        Assert.Equal("GET /banca/cuentas/saldos", incident.Operation);
        Assert.Equal("bank-1", Assert.Single(incident.Occurrences).ItemId);

        var pagos = Assert.Single(store.Calls, c => c.ProfileKey == "banco" && c.Request.Domain == "pagos");
        Assert.Equal(SyncProfileExceptionsService.UnknownEntity, Assert.Single(pagos.Request.Incidents).ClubId);
        Assert.Equal("Pago de servicios", pagos.Request.Incidents[0].Component);

        var ml = Assert.Single(store.Calls, c => c.ProfileKey == "mercadolibre");
        Assert.Equal("checkout", ml.Request.Domain);
        Assert.Equal("MLB", Assert.Single(ml.Request.Incidents).ClubId);

        // The query reads the service's own aliases through the templater, never the incumbent's fixed file.
        var banco = Profile("banco", ("banca-web", "bank-web-banking"), ("pagos", "bank-payments-svc"));
        var kql = AppInsightsProfileExceptionSource.Render(banco, "pagos", TimeSpan.FromHours(1));
        Assert.Contains("app(\"{{SOURCE:bank-payments-svc}}\").exceptions", kql);
        Assert.Contains("app(\"{{SOURCE:bank-payments-svc}}\").requests", kql);
        Assert.DoesNotContain("bank-web-banking", kql);
        Assert.Contains("let serviceKey = \"pagos\";", kql);
        Assert.Contains("tEnd - 60m", kql);
        Assert.Contains("customDimensions.BranchId", kql);

        // The adapter maps the classified columns onto the signal: entity, step and item id.
        var telemetry = new FakeTelemetry(Row(("ItemId", "x-1"), ("Entity", "B-009"), ("ExceptionType", "System.TimeoutException"),
            ("Operation", "POST /payments/v1/ach/transfers"), ("StepKey", "ach"), ("Step", "ACH"), ("OccurredAt", "2026-11-07T20:00:00Z"),
            ("OperationId", "op-1"), ("Message", "timeout"), ("Source", "Server")));
        var adapter = new AppInsightsProfileExceptionSource(
            telemetry,
            new IdentityResolver(),
            new TelemetrySourceOwnershipCheck(new FakeSourceCatalog(
                new TelemetrySourceEntry("bank-payments-svc", "production", TelemetrySourceKind.AppInsights, "/subscriptions/x", "demo-bank-payments-svc", OwnerProfileKey: "banco"),
                new TelemetrySourceEntry("bank-web-banking", "production", TelemetrySourceKind.AppInsights, "/subscriptions/y", "demo-bank-web-banking", OwnerProfileKey: "banco"))));
        var fetch = await adapter.FetchAsync(banco, banco.Services[1], TimeSpan.FromHours(1), "production", CancellationToken.None);
        Assert.Null(fetch.SkipReason);
        var mapped = Assert.Single(fetch.Signals);
        Assert.Equal("B-009", mapped.Signal.ClubId);
        Assert.Equal("x-1", mapped.Signal.ItemId);
        Assert.Equal("ACH", mapped.Step);
        Assert.Equal("demo-bank-payments-svc", Assert.Single(telemetry.Requests).AppInsightsAppId);

        // A service pointing at another company's alias is never read.
        var thief = Profile("mercadolibre", ("checkout", "bank-payments-svc"));
        var refused = await adapter.FetchAsync(thief, thief.Services[0], TimeSpan.FromHours(1), "production", CancellationToken.None);
        Assert.NotNull(refused.SkipReason);
        Assert.Empty(refused.Signals);
        Assert.Single(telemetry.Requests);
    }

    [Fact]
    public async Task Exceptions_DedupIncludesProfile()
    {
        // The same error, same entity, same operation in two companies.
        var source = new FakeExceptionSource();
        source.Add("banco", "core", Signal("bank-1", "X-1", "System.TimeoutException", "POST /core/v1/transfers"), "transferencia", "Transferencia");
        source.Add("kinectify", "core", Signal("kin-1", "X-1", "System.TimeoutException", "POST /core/v1/transfers"), "transferencia", "Transferencia");
        // A shared alias makes two services of one company see one occurrence; it is kept once.
        source.Add("kinectify", "alerts", Signal("kin-1", "X-1", "System.TimeoutException", "POST /core/v1/transfers"), null, null);
        var store = new RecordingIncidentStore();
        var service = SyncService(
            source,
            store,
            Profile("banco", ("core", "bank-core-gateway")),
            Profile("kinectify", ("core", "aml-core"), ("alerts", "aml-core")));

        await service.SyncAllAsync(CancellationToken.None);

        var banco = Assert.Single(store.Calls, c => c.ProfileKey == "banco");
        var kinectify = Assert.Single(store.Calls, c => c.ProfileKey == "kinectify");
        var bancoIncident = Assert.Single(banco.Request.Incidents);
        var kinectifyIncident = Assert.Single(kinectify.Request.Incidents);
        // Same fingerprint: only ProfileKey tells the two incidents apart, so it must be part of the key.
        Assert.Equal(bancoIncident.Fingerprint, kinectifyIncident.Fingerprint);
        Assert.Equal(bancoIncident.ClubId, kinectifyIncident.ClubId);
        Assert.Equal(banco.Request.Environment, kinectify.Request.Environment);

        var sql = File.ReadAllText(SourcePath("Sql", "incident", "CreateIncidentSchema.sql"));
        Assert.Contains("UNIQUE (ProfileKey, ClubId, Environment, Fingerprint)", sql);
        Assert.Contains("@ProfileKey           nvarchar(128) = N'arrivia'", sql);
        Assert.Contains("existing.ProfileKey = @Profile", sql);

        // The store forwards the company to the procedure; the incumbent path writes the incumbent.
        var storeSource = File.ReadAllText(SourcePath("Sql", "SqlIncidentStore.cs"));
        Assert.Contains("parameters.Add(\"ProfileKey\", profileKey.Trim()", storeSource);
        Assert.Contains("IngestForProfileAsync(KpiProfiles.Default, request, cancellationToken)", storeSource);
    }

    [Fact]
    public async Task Exceptions_ReadFilteredByProfile()
    {
        var reader = new ProfileReader();
        reader.Rows["banco"] = [Row("EXC-1", "banca-web", "B-003", "Saldos")];
        reader.Rows["mercadolibre"] = [Row("EXC-2", "checkout", "MLB", "Place order")];
        var legacy = new LegacyReader();
        var profiles = new InMemoryCompanyProfileStore(
        [
            Profile("arrivia", ("hotels", "sor-frontend")),
            Profile("banco", ("banca-web", "bank-web-banking")),
            Profile("mercadolibre", ("checkout", "retail-checkout-api")),
        ]);
        var service = new GetIncidentsService(
            legacy,
            new NoBrands(),
            new KpiProfileResolver(activeStore: new Active("banco"), profileStore: profiles),
            reader,
            profiles);

        // No profileKey: the active company.
        var active = await service.GetAsync(new IncidentListRequest(null, null, "production", "1h", null), CancellationToken.None);
        Assert.Equal(200, active.StatusCode);
        Assert.Equal("banco", active.Result.ProfileKey);
        var bancoRow = Assert.Single(active.Result.Exceptions);
        Assert.Equal("EXC-1", bancoRow.Id);
        Assert.Equal("Saldos", bancoRow.Component);
        Assert.Equal("B-003", bancoRow.ClubName);
        Assert.Equal(["banco", "banco", "banco"], reader.Asked);
        Assert.Null(legacy.LastQuery);

        // Explicit profileKey: that company only, never the active one.
        reader.Asked.Clear();
        var ml = await service.GetAsync(
            new IncidentListRequest("checkout", "MLB", "production", "1h", null, "mercadolibre"), CancellationToken.None);
        Assert.Equal(200, ml.StatusCode);
        Assert.Equal("mercadolibre", ml.Result.ProfileKey);
        Assert.Equal("EXC-2", Assert.Single(ml.Result.Exceptions).Id);
        Assert.DoesNotContain(ml.Result.Exceptions, e => e.Id == "EXC-1");
        Assert.All(reader.Asked, key => Assert.Equal("mercadolibre", key));
        Assert.Equal("checkout", reader.LastList!.Domain);
        Assert.Equal("MLB", reader.LastList.ClubId);

        // A domain the company does not have answers empty and honest, never another company's rows.
        reader.Asked.Clear();
        var foreignDomain = await service.GetAsync(
            new IncidentListRequest("hotels", null, "production", "1h", null, "banco"), CancellationToken.None);
        Assert.Equal(200, foreignDomain.StatusCode);
        Assert.Empty(foreignDomain.Result.Exceptions);
        Assert.Contains("not a service", foreignDomain.Result.Warning);
        Assert.Empty(reader.Asked);

        // An unknown company is rejected.
        var unknown = await service.GetAsync(
            new IncidentListRequest(null, null, "production", "1h", null, "nadie"), CancellationToken.None);
        Assert.Equal(400, unknown.StatusCode);

        // SQL: both reads are scoped to one company.
        var listSql = PrivateSql("ListSql");
        var countSql = PrivateSql("CountSql");
        Assert.Contains("WHERE i.ProfileKey = @ProfileKey", listSql);
        Assert.Contains("WHERE i.ProfileKey = @ProfileKey", countSql);
        Assert.Contains("@AnyDomain = 1", listSql);
    }

    [Fact]
    public async Task Exceptions_IncumbentUnchanged()
    {
        var legacy = new LegacyReader { Rows = [Row("EXC-2000", "hotels", "15639", "component")] };
        var reader = new ProfileReader();
        var profiles = new InMemoryCompanyProfileStore([Profile("arrivia", ("hotels", "sor-frontend")), Profile("banco", ("banca-web", "bank-web-banking"))]);
        var service = new GetIncidentsService(
            legacy,
            new NoBrands(),
            new KpiProfileResolver(activeStore: new Active("arrivia"), profileStore: profiles),
            reader,
            profiles);

        var outcome = await service.GetAsync(new IncidentListRequest("hotels", "15639", "production", "1h", null), CancellationToken.None);

        // The incumbent reads through its original reader with its original filters and vocabulary.
        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("arrivia", outcome.Result.ProfileKey);
        Assert.Equal("EXC-2000", Assert.Single(outcome.Result.Exceptions).Id);
        Assert.Equal("hotels", legacy.LastQuery!.Domain);
        Assert.Equal("15639", legacy.LastQuery.ClubId);
        Assert.Empty(reader.Asked);
        Assert.Equal(400, (await service.GetAsync(new IncidentListRequest("widgets", null, "production", "1h", null), CancellationToken.None)).StatusCode);
        Assert.Equal(400, (await service.GetAsync(new IncidentListRequest("hotels", "tmobile", "production", "1h", null), CancellationToken.None)).StatusCode);

        // The two-argument constructor (no company services wired) still serves the incumbent as before.
        var bare = new GetIncidentsService(legacy, new NoBrands());
        var bareOutcome = await bare.GetAsync(new IncidentListRequest(null, null, "production", "1h", null), CancellationToken.None);
        Assert.Equal(200, bareOutcome.StatusCode);
        Assert.Equal("arrivia", bareOutcome.Result.ProfileKey);
        Assert.Null(legacy.LastQuery!.Domain);

        // Ingestion never writes the incumbent: its exceptions stay with the incumbent pipeline.
        var source = new FakeExceptionSource();
        var store = new RecordingIncidentStore();
        var sync = SyncService(source, store, Profile("arrivia", ("hotels", "sor-frontend")));
        Assert.Empty(await sync.SyncAllAsync(CancellationToken.None));
        var direct = Assert.Single(await sync.SyncProfileAsync(Profile("arrivia", ("hotels", "sor-frontend")), CancellationToken.None));
        Assert.False(direct.Stored);
        Assert.Empty(source.Fetched);
        Assert.Empty(store.Calls);

        // The incumbent's reads keep the incumbent's ProfileKey and its domain list.
        Assert.Contains("ListForProfileAsync(KpiProfiles.Default, query, cancellationToken)", File.ReadAllText(SourcePath("Sql", "SqlIncidentReader.cs")));
        Assert.Contains("(@Domain IS NULL AND (@AnyDomain = 1 OR i.Domain IN (", PrivateSql("ListSql"));
    }

    // ------------------------------------------------------------------ helpers

    private static SyncProfileExceptionsService SyncService(
        FakeExceptionSource source, RecordingIncidentStore store, params CompanyProfile[] profiles) =>
        new(
            new InMemoryCompanyProfileStore(profiles),
            source,
            store,
            Options.Create(new JourneySyncOptions { Environment = "production" }),
            Options.Create(new ProfileExceptionsOptions { Window = "1h" }));

    private static CompanyProfile Profile(string key, params (string Service, string Alias)[] services) => new(
        key,
        key,
        new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        services.Select(s => new ServiceDefinition(
            s.Service,
            s.Service,
            [s.Alias],
            [],
            [new FlowDefinition("flow", "Flow", [new StepDefinition("step-" + s.Service, "Step", 1, [new StepMatcher(StepMatcherKind.PathContains, "/" + s.Service)])])]))
            .ToList(),
        [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, key == "banco" ? "BranchId" : "SiteId", "entity")],
        new CompanyGlossary("service", "flow", "step", "entity", "owner"));

    private static ExceptionSignal Signal(string itemId, string entity, string type, string operation) => new(
        entity, type, "", operation, "boom", null, itemId, "op-" + itemId, Seen, "u1", null, "op-" + itemId, null);

    private static IncidentRow Row(string id, string domain, string clubId, string component) => new(
        id, domain, clubId, "production", "System.Exception", component, "op", "message", null,
        "Low", "Open", null, Seen, Seen, 1, 1, null, null, [], []);

    private static TelemetryJourneyRow Row(params (string Name, string Value)[] columns)
    {
        var row = new TelemetryJourneyRow();
        foreach (var (name, value) in columns)
        {
            row.Extra[name] = JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
        }
        return row;
    }

    private static string PrivateSql(string field) =>
        (string)typeof(SqlIncidentReader).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string SourcePath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JourneyMonitor.Infrastructure")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, "src", "JourneyMonitor.Infrastructure", .. parts]);
    }

    private sealed class FakeExceptionSource : IProfileExceptionSource
    {
        private readonly Dictionary<(string, string), List<ProfileExceptionSignal>> _signals = [];

        public List<(string Profile, string Service)> Fetched { get; } = [];

        public void Add(string profile, string service, ExceptionSignal signal, string? stepKey, string? step)
        {
            if (!_signals.TryGetValue((profile, service), out var list)) _signals[(profile, service)] = list = [];
            list.Add(new ProfileExceptionSignal(signal, stepKey, step));
        }

        public Task<ProfileExceptionFetch> FetchAsync(
            CompanyProfile profile, ServiceDefinition service, TimeSpan window, string environment, CancellationToken cancellationToken)
        {
            Fetched.Add((profile.ProfileKey, service.Key));
            IReadOnlyList<ProfileExceptionSignal> signals = _signals.TryGetValue((profile.ProfileKey, service.Key), out var list) ? list : [];
            return Task.FromResult(new ProfileExceptionFetch(signals, null));
        }
    }

    private sealed class RecordingIncidentStore : IProfileIncidentStore
    {
        public List<(string ProfileKey, IncidentIngestRequest Request)> Calls { get; } = [];

        public Task<IncidentIngestResult> IngestForProfileAsync(
            string profileKey, IncidentIngestRequest request, CancellationToken cancellationToken)
        {
            Calls.Add((profileKey, request));
            return Task.FromResult(new IncidentIngestResult(1, request.Incidents.Count, 0, request.Incidents.Sum(i => i.Occurrences.Count)));
        }
    }

    private sealed class ProfileReader : IProfileIncidentReader
    {
        public Dictionary<string, IReadOnlyList<IncidentRow>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = [];
        public IncidentReadQuery? LastList { get; private set; }

        public Task<IReadOnlyList<IncidentRow>> ListForProfileAsync(
            string profileKey, IncidentReadQuery query, CancellationToken cancellationToken)
        {
            Asked.Add(profileKey);
            LastList = query;
            return Task.FromResult(RowsOf(profileKey));
        }

        public Task<IReadOnlyDictionary<string, int>> CountByDomainForProfileAsync(
            string profileKey, IncidentCountQuery query, CancellationToken cancellationToken)
        {
            Asked.Add(profileKey);
            return Task.FromResult<IReadOnlyDictionary<string, int>>(
                RowsOf(profileKey).GroupBy(row => row.Domain).ToDictionary(g => g.Key, g => g.Count()));
        }

        private IReadOnlyList<IncidentRow> RowsOf(string profileKey) =>
            Rows.TryGetValue(profileKey, out var rows) ? rows : [];
    }

    private sealed class LegacyReader : IIncidentReader
    {
        public IReadOnlyList<IncidentRow> Rows { get; init; } = [];
        public IncidentReadQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentReadQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(Rows);
        }

        public Task<IReadOnlyDictionary<string, int>> CountByDomainAsync(IncidentCountQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }

    private sealed class NoBrands : IBrandCatalog
    {
        public bool IsConfigured => false;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrandClub>>([]);
    }

    private sealed class Active(string key) : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(key);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) =>
            Task.FromResult<ActiveCompanyProfile?>(new ActiveCompanyProfile(key, Seen, "test"));

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTelemetry(params TelemetryJourneyRow[] rows) : ITelemetrySource
    {
        public List<TelemetryQueryRequest> Requests { get; } = [];

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>(rows);
        }
    }

    private sealed class IdentityResolver : IKqlSourceResolver
    {
        public Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default) => Task.FromResult(kql);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeSourceCatalog(params TelemetrySourceEntry[] entries) : ITelemetrySourceStore
    {
        public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TelemetrySourceEntry>>(entries);

        public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TelemetrySourceAuditEntry>>([]);
    }
}
