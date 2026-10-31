using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Kql;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Incidents;

public class SyncIncidentsServiceTests
{
    [Fact]
    public async Task FifteenMinuteWindowIsAccepted()
    {
        var telemetry = new CapturingTelemetrySource();
        var store = new CapturingIncidentStore();
        var service = new SyncIncidentsService(
            telemetry,
            new KqlQueryTextProvider(),
            store,
            new FakeBrandCatalog([new BrandClub("T-Mobile", "15639", "308", "stays.tmobiletravel.com")]),
            Options.Create(new InsightsOptions { AppInsightsAppId = "app-id" }),
            Options.Create(new JourneySyncOptions
            {
                Domains = "hotels",
                Window = "15m",
                Environment = "production",
            }));

        var pass = await service.SyncAsync("hotels", CancellationToken.None);

        Assert.True(pass.Stored);
        Assert.Null(pass.SkipReason);
        Assert.Contains("let win = 15m;", telemetry.Kql);
        Assert.Equal("15m", store.Request!.Window);
    }

    [Fact]
    public async Task HotelsAndCarsShareOneAppInsightsQuery()
    {
        var telemetry = new CapturingTelemetrySource
        {
            Rows =
            [
                Row("/vacationclub/logincheck.aspx", "hotel-item", "System.Threading.ThreadAbortException"),
                Row("/cars/search.json", "car-item", "System.TimeoutException"),
            ],
        };
        var store = new CapturingIncidentStore();
        var service = new SyncIncidentsService(
            telemetry,
            new KqlQueryTextProvider(),
            store,
            new FakeBrandCatalog([new BrandClub("T-Mobile", "15639", "308", "stays.tmobiletravel.com")]),
            Options.Create(new InsightsOptions { AppInsightsAppId = "hotel-app" }),
            Options.Create(new JourneySyncOptions
            {
                Domains = "hotels,cruises,cars",
                Window = "15m",
                Environment = "production",
            }));

        var passes = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, telemetry.QueryCount);
        Assert.Equal("hotel-app", telemetry.AppId);
        Assert.Contains("| where ClubId in (\"15639\")", telemetry.Kql);
        Assert.Contains("RequestUrl !has \"/cars/\"", telemetry.Kql);
        Assert.Equal(["hotels", "cruises"], passes.Select(pass => pass.Domain).ToArray());
        Assert.True(passes.Single(pass => pass.Domain == "hotels").Stored);
        Assert.False(passes.Single(pass => pass.Domain == "cruises").Stored);

        var hotel = Assert.Single(store.Requests.Single().Incidents);
        Assert.Equal("System.Threading.ThreadAbortException", hotel.ExceptionType);
        Assert.Equal("01 Login", hotel.Component);
    }

    [Fact]
    public async Task CruiseExceptionsFilterOnIcePartnerId()
    {
        var telemetry = new CapturingTelemetrySource
        {
            Rows =
            [
                Row("/cruise/search", "keep", "System.TimeoutException", "15639", "308"),
                Row("/cruise/search", "vacation-only", "System.TimeoutException", "15639", ""),
                Row("/cruise/search", "other", "System.TimeoutException", "999", "999"),
            ],
        };
        var store = new CapturingIncidentStore();
        var service = new SyncIncidentsService(
            telemetry,
            new KqlQueryTextProvider(),
            store,
            new FakeBrandCatalog(
            [
                new BrandClub("T-Mobile", "15639", "308", "cruise.tmobiletravel.com"),
                new BrandClub("Smarter Getaways", "15662", "239", "members.smartergetaways.com"),
            ]),
            Options.Create(new InsightsOptions { Cruises = new CruiseInsightsOptions { AppInsightsAppId = "cruise-app" } }),
            Options.Create(new JourneySyncOptions
            {
                Domains = "cruises",
                Window = "15m",
                Environment = "production",
            }));

        var pass = await service.SyncAsync("cruises", CancellationToken.None);

        Assert.True(pass.Stored);
        Assert.Contains("| where PartnerId in (\"239\", \"308\")", telemetry.Kql);
        var incident = Assert.Single(store.Request!.Incidents);
        Assert.Equal("308", incident.ClubId);
    }

    [Fact]
    public async Task AirAndCarsAreNotQueried()
    {
        var telemetry = new CapturingTelemetrySource();
        var store = new CapturingIncidentStore();
        var service = new SyncIncidentsService(
            telemetry,
            new KqlQueryTextProvider(),
            store,
            new FakeBrandCatalog([new BrandClub("T-Mobile", "15639", "308", "stays.tmobiletravel.com")]),
            Options.Create(new InsightsOptions { AppInsightsAppId = "hotel-app" }),
            Options.Create(new JourneySyncOptions { Domains = "cars,air", Window = "15m", Environment = "production" }));

        var passes = await service.SyncAllAsync(CancellationToken.None);

        Assert.Empty(passes);
        Assert.Equal(0, telemetry.QueryCount);
        Assert.Null(store.Request);
    }

    [Fact]
    public async Task ExceptionUrlDoesNotHideAJoinedCarRequest()
    {
        var telemetry = new CapturingTelemetrySource
        {
            Rows =
            [
                Row("/vacationclub/logincheck.aspx", "hotel-item", "System.NullReferenceException"),
                Row("/vacationclub/home.aspx", "hidden-car", "System.TimeoutException", requestUrl: "https://stays.example/cars/search.json"),
            ],
        };
        var store = new CapturingIncidentStore();
        var service = new SyncIncidentsService(
            telemetry,
            new KqlQueryTextProvider(),
            store,
            new FakeBrandCatalog([new BrandClub("T-Mobile", "15639", "308", "stays.tmobiletravel.com")]),
            Options.Create(new InsightsOptions { AppInsightsAppId = "hotel-app" }),
            Options.Create(new JourneySyncOptions
            {
                Domains = "hotels",
                Window = "15m",
                Environment = "production",
            }));

        var pass = await service.SyncAsync("hotels", CancellationToken.None);

        Assert.True(pass.Stored);
        Assert.Contains("RequestUrl !has \"/cars/\"", telemetry.Kql);
        var hotel = Assert.Single(store.Request!.Incidents);
        Assert.Equal("System.NullReferenceException", hotel.ExceptionType);
        Assert.Equal("/vacationclub/logincheck.aspx", Assert.Single(hotel.Occurrences).Url);
    }

    private static TelemetryJourneyRow Row(string url, string itemId, string exceptionType, string clubId = "15639", string partnerId = "", string requestUrl = "")
    {
        var row = new TelemetryJourneyRow();
        using var document = JsonDocument.Parse($$"""
            {
              "ClubId": "{{clubId}}",
              "PartnerId": "{{partnerId}}",
              "ExceptionType": "{{exceptionType}}",
              "Component": "frontend",
              "Operation": "GET",
              "Message": "failed",
              "ItemId": "{{itemId}}",
              "OperationId": "{{itemId}}",
              "OccurredAt": "2026-09-28T16:16:00Z",
              "MemberId": "8812345",
              "Url": "{{url}}",
              "RequestUrl": "{{requestUrl}}"
            }
            """);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            row.Extra[property.Name] = property.Value.Clone();
        }

        return row;
    }

    private sealed class CapturingTelemetrySource : ITelemetrySource
    {
        public string? Kql { get; private set; }
        public string? AppId { get; private set; }
        public int QueryCount { get; private set; }
        public IReadOnlyList<TelemetryJourneyRow> Rows { get; init; } = [];

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(
            TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            QueryCount++;
            Kql = request.Kql;
            AppId = request.AppInsightsAppId;
            return Task.FromResult(Rows);
        }
    }

    private sealed class CapturingIncidentStore : IIncidentStore
    {
        public IncidentIngestRequest? Request { get; private set; }
        public List<IncidentIngestRequest> Requests { get; } = [];

        public Task<IncidentIngestResult> IngestAsync(
            IncidentIngestRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Requests.Add(request);
            return Task.FromResult(new IncidentIngestResult(1, 0, 0, 0));
        }
    }

    private sealed class FakeBrandCatalog(IReadOnlyList<BrandClub> rows) : IBrandCatalog
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }
}
