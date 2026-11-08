using System.Text.Json;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Spec1000.A2;

/// <summary>Shared doubles for the AppInsightsKpiSource tests: a profile, a source resolver and a capturing telemetry source.</summary>
internal static class KpiSourceDoubles
{
    public static readonly DateTimeOffset From = new(2026, 11, 4, 14, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset To = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    public static ServiceDefinition Service(string key, IReadOnlyList<string> aliases, IReadOnlyList<string> hosts, params (string Key, string Display, int Order, string Marker)[] steps) =>
        new(
            key,
            key.ToUpperInvariant(),
            aliases,
            hosts,
            [new FlowDefinition("booking", "Booking", steps.Select(s => new StepDefinition(s.Key, s.Display, s.Order, [new StepMatcher(StepMatcherKind.PathContains, s.Marker)])).ToList())]);

    /// <summary>
    /// The incumbent's shape without its words: four services, two of them sharing one telemetry
    /// source and no host filters, so the classifier alone tells them apart.
    /// </summary>
    public static CompanyProfile Incumbent(string profileKey = KpiProfiles.Default) => new(
        profileKey,
        "Incumbent",
        new CompanyLabels("Vertical", "Journey", "Stage", "Club", "Owner"),
        [
            Service("cruises", ["sea-core", "sea-api"], [], ("04-rooms", "04 Rooms & Rates", 10, "/cabincategory")),
            Service("cars", ["web-front"], [], ("02-car-search", "02 Car Search", 30, "carsearch")),
            Service("hotels", ["web-front"], [],
                ("03-results", "03 Hotel Results", 40, "backgroundsearch"),
                ("02-search", "02 Hotel Search", 41, "search.json")),
            Service("air", ["air-front"], [], ("02-flight-search", "02 Flight Search", 50, "flightsearch")),
        ],
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "MemberGroupId", "club"),
            new EntityIdentitySource(2, EntityIdentitySourceKind.Host, "", "host"),
        ],
        new CompanyGlossary("vertical", "journey", "stage", "club", "owner"));

    /// <summary>A second company whose services DO declare their hosts.</summary>
    public static CompanyProfile HostScoped(string profileKey = "acme") => new(
        profileKey,
        "Acme",
        new CompanyLabels("Product", "Flow", "Step", "Account", "Team"),
        [
            Service("hotels", ["acme-web"], ["rooms.acme.test", "*.rooms-eu.acme.test"], ("10-browse", "10 Browse", 1, "/browse")),
            Service("billing", ["acme-billing"], ["pay.acme.test"], ("20-pay", "20 Pay", 2, "/pay")),
        ],
        [new EntityIdentitySource(1, EntityIdentitySourceKind.QueryParameter, "account", "account")],
        new CompanyGlossary("product", "flow", "step", "account", "team"));

    public static KpiDefinition Definition(
        string sourceKey = AppInsightsKpiSource.LatencySourceKey,
        string domain = "hotels",
        string stage = "",
        KpiGrain grain = KpiGrain.Domain,
        int maxEntities = 500,
        string profileKey = KpiProfiles.Default,
        int id = 1) =>
        A2Kpi.Definition(id: id, sourceKey: sourceKey, domain: domain, stage: stage) with
        {
            Grain = grain,
            MaxEntitiesPerBucket = maxEntities,
            ProfileKey = profileKey,
        };

    /// <summary>
    /// The catalog the doubles' profiles resolve against: only <c>web-front</c> (hotels, cars) and
    /// <c>acme-web</c> carry an app id; <c>sea-core</c> and <c>air-front</c> are not registered.
    /// </summary>
    public static IReadOnlyList<TelemetrySourceEntry> DefaultCatalog() =>
    [
        new("web-front", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/web-front", AppId: "hotel-app"),
        new("acme-web", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/acme-web", AppId: "acme-app", TenantId: "acme-tenant"),
    ];

    public static AppInsightsKpiSource Source(ITelemetrySource telemetry, params CompanyProfile[] profiles) =>
        Source(telemetry, new CatalogStore(DefaultCatalog()), new InsightsOptions
        {
            Hotels = new HotelInsightsOptions { AppInsightsAppId = "hotel-app" },
        }, profiles);

    public static AppInsightsKpiSource Source(ITelemetrySource telemetry, ITelemetrySourceStore catalog, InsightsOptions insights, params CompanyProfile[] profiles) =>
        Source(telemetry, catalog, insights, null, profiles);

    public static AppInsightsKpiSource Source(ITelemetrySource telemetry, ITelemetrySourceStore catalog, InsightsOptions insights,
        Microsoft.Extensions.Logging.ILogger<AppInsightsKpiSource>? logger, params CompanyProfile[] profiles) =>
        new(
            telemetry,
            Microsoft.Extensions.Options.Options.Create(insights),
            Microsoft.Extensions.Options.Options.Create(new JourneySyncOptions()),
            new InMemoryCompanyProfileStore(profiles.Length == 0 ? [Incumbent()] : profiles),
            new AliasResolver(),
            catalog,
            logger);

    /// <summary>A fixed telemetry source catalog that counts reads.</summary>
    public sealed class CatalogStore(IReadOnlyList<TelemetrySourceEntry> entries) : ITelemetrySourceStore
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(entries);
        }

        public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public static TelemetryJourneyRow Row(string json) => JsonSerializer.Deserialize<TelemetryJourneyRow>(json)!;

    /// <summary>Turns <c>{{SOURCE:alias}}</c> into <c>/res/alias</c>, the way KqlSourceResolver turns it into a resource id.</summary>
    public sealed class AliasResolver : IKqlSourceResolver
    {
        public List<string> Resolved { get; } = [];

        public Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default)
        {
            var text = KqlSourceResolver.PlaceholderPattern.Replace(kql, m => "/res/" + m.Groups["alias"].Value);
            Resolved.Add(text);
            return Task.FromResult(text);
        }

        public void Invalidate() { }
    }

    public sealed class CapturingTelemetrySource(IReadOnlyList<TelemetryJourneyRow>? rows = null) : ITelemetrySource
    {
        public List<TelemetryQueryRequest> Requests { get; } = [];

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(rows ?? []);
        }
    }

    /// <summary>KQL with comment lines removed, so assertions read the query and not its documentation.</summary>
    public static string Code(string kql) =>
        string.Join('\n', kql.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>The last non-blank line: the statement whose rows the query returns.</summary>
    public static string FinalStatement(string kql) =>
        kql.Split('\n').Select(l => l.Trim()).Last(l => l.Length > 0);

    public static readonly Regex HostLiteral = new(@"""[a-z0-9-]+(\.[a-z0-9-]+)+\.(com|net|org|io|travel)""", RegexOptions.IgnoreCase);
}
