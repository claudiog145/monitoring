using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.UseCases;

namespace JourneyMonitor.Tests.Incidents;

internal sealed class FakeIncidentReader : IIncidentReader
{
    public IReadOnlyList<IncidentRow> Rows { get; init; } = [];
    public Exception? Error { get; init; }
    public IncidentReadQuery? LastQuery { get; private set; }

    public Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentReadQuery query, CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (Error is not null) throw Error;
        return Task.FromResult(Rows);
    }
}

public class GetIncidentsServiceTests
{
    private static GetIncidentsService Service(FakeIncidentReader reader, IBrandCatalog? brands = null) =>
        new(reader, brands ?? new EmptyBrandCatalog());

    [Fact]
    public async Task RejectsUnknownDomain()
    {
        var outcome = await Service(new FakeIncidentReader()).GetAsync(
            new IncidentListRequest("widgets", null, "production", "15m", null),
            CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Contains("domain", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsNonNumericClub()
    {
        var outcome = await Service(new FakeIncidentReader()).GetAsync(
            new IncidentListRequest("hotels", "tmobile", "production", "15m", null),
            CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Contains("club", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsInvalidWindow()
    {
        var outcome = await Service(new FakeIncidentReader()).GetAsync(
            new IncidentListRequest("hotels", "15639", "production", "yesterday", null),
            CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Contains("win", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MapsClubMemberUrlAndJourneyKeys()
    {
        var seen = DateTimeOffset.Parse("2026-09-28T18:00:00Z");
        var reader = new FakeIncidentReader
        {
            Rows =
            [
                new IncidentRow(
                    "EXC-2000",
                    "hotels",
                    "15639",
                    "production",
                    "System.TimeoutException",
                    "Angular",
                    "POST /vacationclub/logincheck.aspx",
                    "Supplier timed out",
                    "at Booking.Book()",
                    "High",
                    "Open",
                    null,
                    seen.AddMinutes(-10),
                    seen,
                    7,
                    3,
                    "14001001",
                    "https://stays.example/vacationclub/logincheck.aspx",
                    ["corr-1"],
                    [
                        new IncidentOccurrenceRow(seen, "14001001", "https://stays.example/hotels/book", "Supplier timed out"),
                        new IncidentOccurrenceRow(seen.AddMinutes(-10), "14001002", "https://stays.example/hotels/search", "earlier"),
                    ]),
            ],
        };

        var outcome = await Service(reader).GetAsync(
            new IncidentListRequest("Hotels", "15639", "production", "15m", "14001001"),
            CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("sql", outcome.Result.Source);
        Assert.Equal("hotels", outcome.Result.Domain);
        Assert.Equal("15639", outcome.Result.ClubId);
        Assert.Equal("production", outcome.Result.Environment);
        Assert.Equal("15m", outcome.Result.Win);
        Assert.Equal("%14001001%", reader.LastQuery!.SearchPattern);
        Assert.True(reader.LastQuery.Since <= DateTimeOffset.UtcNow.AddMinutes(-14));

        var exception = Assert.Single(outcome.Result.Exceptions);
        Assert.Equal("EXC-2000", exception.Id);
        Assert.Equal("15639", exception.ClubId);
        Assert.Equal("15639", exception.ClubName);
        Assert.Equal("14001001", exception.MemberId);
        Assert.Equal("https://stays.example/vacationclub/logincheck.aspx", exception.Url);
        Assert.Equal("01 Login", exception.Component);
        Assert.Equal("System.TimeoutException", exception.Type);
        Assert.Equal("at Booking.Book()", exception.StackTrace);
        Assert.Equal(["corr-1"], exception.JourneysAffected);
        Assert.Equal(2, exception.Occurrences.Count);
        Assert.Equal("14001002", exception.Occurrences[1].MemberId);
        Assert.Equal("", exception.AssignedAgent);
    }

    [Fact]
    public async Task AllDomainsOmitsDomainFilter()
    {
        var reader = new FakeIncidentReader();
        await Service(reader).GetAsync(
            new IncidentListRequest("all", "", "staging", null, "100%_["),
            CancellationToken.None);

        Assert.Null(reader.LastQuery!.Domain);
        Assert.Null(reader.LastQuery.ClubId);
        Assert.Equal("staging", reader.LastQuery.Environment);
        Assert.Null(reader.LastQuery.Since);
        Assert.Equal("%100[%][_][[]%", reader.LastQuery.SearchPattern);
    }

    [Fact]
    public async Task MissingSchemaReturnsEmptyWarning()
    {
        var reader = new FakeIncidentReader
        {
            Error = new IncidentStoreUnavailableException("Incident schema is not provisioned."),
        };

        var outcome = await Service(reader).GetAsync(
            new IncidentListRequest(null, null, null, "1h", null),
            CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("unavailable", outcome.Result.Source);
        Assert.Empty(outcome.Result.Exceptions);
        Assert.Contains("not provisioned", outcome.Result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClubColumnUsesVacationClubNameThenPartnerName()
    {
        var seen = DateTimeOffset.Parse("2026-09-28T18:00:00Z");
        var reader = new FakeIncidentReader
        {
            Rows =
            [
                Incident("EXC-club", "15639", seen),
                Incident("EXC-partner", "308", seen),
                Incident("EXC-code", "SOR", seen),
            ],
        };
        var brands = new StaticBrandCatalog(
        [
            new BrandClub("T-Mobile", "15639", "308", "stays.tmobiletravel.com"),
            new BrandClub("AARP Go", "16108", "333", "members.aarparrivia.com"),
        ]);

        var outcome = await Service(reader, brands).GetAsync(
            new IncidentListRequest("all", null, "production", "15m", null),
            CancellationToken.None);

        var exceptions = outcome.Result.Exceptions.ToDictionary(e => e.Id);
        Assert.Equal("T-Mobile", exceptions["EXC-club"].ClubName);
        Assert.Equal("15639", exceptions["EXC-club"].ClubId);
        Assert.Equal("T-Mobile", exceptions["EXC-partner"].ClubName);
        Assert.Equal("SOR", exceptions["EXC-code"].ClubName);
    }

    private static IncidentRow Incident(string id, string clubId, DateTimeOffset seen) =>
        new(id, "hotels", clubId, "production", "System.Exception", "component", "op", "message", null,
            "Low", "Open", null, seen, seen, 1, 1, null, null, [], []);

    private sealed class EmptyBrandCatalog : IBrandCatalog
    {
        public bool IsConfigured => false;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrandClub>>([]);
    }

    private sealed class StaticBrandCatalog(IReadOnlyList<BrandClub> rows) : IBrandCatalog
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }
}
