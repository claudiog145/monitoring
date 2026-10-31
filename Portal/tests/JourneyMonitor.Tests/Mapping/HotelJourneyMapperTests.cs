using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Mapping;
using JourneyMonitor.Infrastructure.AppInsights;

namespace JourneyMonitor.Tests.Mapping;

/// <summary>
/// Characterization tests ported from test/map-v6.test.js: same fixture rows and assertions,
/// proving HotelJourneyMapper is behaviorally equivalent to map-v6.js. Not currently wired
/// into GetJourneysService (see the note on TelemetryJourneyRow/JourneysResult) - this mapper
/// is the tested, ready-to-wire foundation for a future server-side DTO contract.
/// </summary>
public class HotelJourneyMapperTests
{
    [Fact]
    public void MaybeParseJsonReturnsElementsIndependentFromSourceDocument()
    {
        var method = typeof(AppInsightsTelemetrySource)
            .GetMethod("MaybeParseJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        JsonElement result;
        using (var document = JsonDocument.Parse("""{"Cell":"plain-text"}"""))
        {
            var cell = document.RootElement.GetProperty("Cell");
            result = (JsonElement)method!.Invoke(null, [cell])!;
        }

        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal("plain-text", result.GetString());
    }

    private static TelemetryJourneyRow RowFromJson(string json)
    {
        var row = new TelemetryJourneyRow();
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            row.Extra[property.Name] = property.Value.Clone();
        }
        return row;
    }

    private static TelemetryJourneyRow CruiseRow(string correlationId, string? outcome = null, int? lastStep = null) => RowFromJson($$"""
        {
          "UserId": "21061035",
          "SessionId": "{{correlationId}}",
          "CorrelationId": "{{correlationId}}",
          "SessionCount": 2,
          "Outcome": "{{outcome ?? "Failed at step 9"}}",
          "LastStep": {{lastStep ?? 9}},
          "FailReason": "Payment rejected",
          "BookOk": 0,
          "BookFail": 1,
          "IsBookSuccessful": false,
          "PassedSearch": true,
          "FailedCalls": 2,
          "Exceptions": 2,
          "JourneyTotalMs": 4200,
          "OverallP95": 4200,
          "FirstSeen": "2026-09-01T10:00:00Z",
          "LastSeen": "2026-09-01T10:01:00Z",
          "Journey": [{
            "stageNo": 9,
            "stage": "09 Payment",
            "stageTotalMs": 4200,
            "callCount": 1,
            "p95Ms": 4200,
            "failureRatePct": 100,
            "status": "YELLOW",
            "calls": [{
              "kind": "backend",
              "source": "Cruise Core",
              "call": "/payment",
              "correlationId": "{{correlationId}}",
              "n": 1,
              "callTotalMs": 4200,
              "p95Ms": 4200,
              "failureRatePct": 100,
              "status": "YELLOW",
              "detail": "HTTP 500",
              "errors": [{ "message": "HTTP 500" }]
            }]
          }]
        }
        """);

    [Fact]
    public void CruiseRowsUseTheFrontendSessionAsTheUniqueJourneyId()
    {
        var rows = new[] { CruiseRow("session-a"), CruiseRow("session-b") };
        var journeys = HotelJourneyMapper.MapRows(rows, "T-Mobile / T Life", "cruises", "production");
        Assert.Equal(["session-a", "session-b"], journeys.Select(j => j.Id));
        Assert.Equal(["21061035", "21061035"], journeys.Select(j => j.Customer));
    }

    [Fact]
    public void CarsRowsUseCorrelationIdAsTheUniqueJourneyId()
    {
        var rows = new[]
        {
            RowFromJson("""{"UserId":"21061035","CorrelationId":"corr-a","Outcome":"Dropped at step 3","PassedSearch":true,"IsBookSuccessful":false,"Exceptions":1,"Journey":[]}"""),
            RowFromJson("""{"UserId":"21061035","CorrelationId":"corr-b","Outcome":"Dropped at step 3","Journey":[]}"""),
        };
        var journeys = HotelJourneyMapper.MapRows(rows, "T-Mobile / T Life", "cars", "production");
        Assert.Equal(["corr-a", "corr-b"], journeys.Select(j => j.Id));
        Assert.Equal(["21061035", "21061035"], journeys.Select(j => j.Customer));
        Assert.Equal("T-Mobile / T Life Cars", journeys[0].Origin);
        Assert.True(journeys[0].PassedSearch);
        Assert.False(journeys[0].IsBookSuccessful);
        Assert.Equal(1, journeys[0].Exceptions);
    }

    [Fact]
    public void CruiseMappingPreservesTheSessionCorrelationAndCoreSource()
    {
        var journey = HotelJourneyMapper.MapRow(CruiseRow("session-a"), "T-Mobile / T Life", "cruises", "production");
        Assert.Equal("cruises", journey.Domain);
        Assert.Equal("session-a", journey.CorrelationId);
        Assert.Equal(2, journey.SessionCount);
        Assert.Equal("T-Mobile / T Life Cruise", journey.Origin);
        Assert.Equal("HTTP 500", journey.Stages[0].Diagnostic);
        Assert.Equal("Cruise Core", journey.Stages[0].Calls[0].Detail);
        Assert.Equal(1, journey.BookFail);
        Assert.Equal(2, journey.FailedCalls);
        Assert.True(journey.PassedSearch);
        Assert.False(journey.IsBookSuccessful);
        Assert.Equal(2, journey.Exceptions);
        Assert.Equal(1, journey.TotalCalls);
        Assert.Equal(4200, journey.OverallP95Ms);
        Assert.Equal("production", journey.Environment);
        Assert.Equal("PROD", journey.Destination);
    }

    [Fact]
    public void CruiseMappingReportsStagingWhenTheEnvironmentIsStaging()
    {
        var journey = HotelJourneyMapper.MapRow(CruiseRow("session-a"), "T-Mobile / T Life", "cruises", "staging");
        Assert.Equal("staging", journey.Environment);
        Assert.Equal("STAGING", journey.Destination);
    }

    [Theory]
    [InlineData(1, "T-Mobile SSO Failed")]
    [InlineData(2, "Cruise Listing Failed")]
    [InlineData(3, "Cruise Detail Failed")]
    [InlineData(4, "Rooms & Rates Failed")]
    [InlineData(5, "Review Total Price Failed")]
    [InlineData(6, "Cabin Selection Failed")]
    [InlineData(7, "Passenger Failed")]
    [InlineData(8, "Options Failed")]
    [InlineData(9, "Payment Failed")]
    [InlineData(10, "Review Failed")]
    [InlineData(11, "Confirmation Failed")]
    public void CruiseStatusNamesEveryFailedAtStep(int step, string label)
    {
        var journey = HotelJourneyMapper.MapRow(CruiseRow($"s{step}", $"Failed at step {step}", step), "T-Mobile / T Life", "cruises", "production");
        Assert.Equal(label, journey.Status);
    }

    [Fact]
    public void PurchaseFailedIsUnchangedAndOtherDomainsAreUnaffected()
    {
        var purchase = HotelJourneyMapper.MapRow(CruiseRow("s-pay", "Purchase Failed", 9), "T-Mobile / T Life", "cruises", "production");
        var dropped = HotelJourneyMapper.MapRow(CruiseRow("s-drop", "Dropped at step 4", 4), "T-Mobile / T Life", "cruises", "production");
        var hotels = HotelJourneyMapper.MapRow(CruiseRow("h4", "Failed at step 4", 4), "T-Mobile / T Life", "hotels", "production");

        Assert.Equal("Purchase Failed", purchase.Status);
        Assert.Equal("Dropped at step 4", dropped.Status);
        Assert.Equal("Failed at step 4", hotels.Status);
    }
}
