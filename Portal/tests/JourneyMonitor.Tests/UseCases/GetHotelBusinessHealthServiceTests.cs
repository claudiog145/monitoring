using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Hotel;

namespace JourneyMonitor.Tests.UseCases;

public class GetHotelBusinessHealthServiceTests
{
    [Fact]
    public async Task HotelsWithNoClubReadsTheTotalRow()
    {
        var source = new RecordingSource();
        var service = new GetHotelBusinessHealthService(source);

        var result = await service.GetAsync("production", "hotels", clubId: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(source.VacationClubId);
        Assert.Equal("production", source.Environment);
        Assert.Equal(0, result.VacationClubId);
        Assert.True(result.Available);
        Assert.Null(result.Current.ConversionPercent);
        Assert.Equal(7, result.Current.Bookings);
        Assert.Equal(2672.80m, result.Current.Revenue);
        Assert.Equal(3, result.PriorWeek.Bookings);
        Assert.Equal(357.70m, result.PriorWeek.Revenue);
    }

    [Fact]
    public async Task ASelectedClubIsPassedToTheView()
    {
        var source = new RecordingSource();
        var service = new GetHotelBusinessHealthService(source);

        var result = await service.GetAsync("production", "Hotel", "15639", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(15639L, source.VacationClubId);
        Assert.Equal("production", result.Environment);
        Assert.Equal(15639L, result.VacationClubId);
    }

    [Fact]
    public async Task NonProductionEnvironmentsDoNotReadTheBookingView()
    {
        var source = new RecordingSource();
        var service = new GetHotelBusinessHealthService(source);

        Assert.Null(await service.GetAsync("qa", "hotels", null, CancellationToken.None));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task ASelectedClubConversionIsThatClubOnly()
    {
        var source = new RecordingSource();
        var conversion = new RecordingConversion(40m, 10m);
        var service = new GetHotelBusinessHealthService(source, conversion);

        var result = await service.GetAsync("production", "hotels", "15639", CancellationToken.None);

        Assert.Equal(15639L, source.VacationClubId);
        Assert.Equal(15639L, conversion.VacationClubId);
        Assert.Equal("production", conversion.Environment);
        Assert.Equal(7, result!.Current.Bookings);
        Assert.Equal(40m, result.Current.ConversionPercent);
        Assert.Equal(10m, result.PriorWeek.ConversionPercent);
        Assert.True(result.BookingsAvailable);
        Assert.Equal(2, result.RecentOperations!.Count);
    }

    [Fact]
    public async Task AllClubsConversionIsUnscoped()
    {
        var conversion = new RecordingConversion(12.5m, null);
        var service = new GetHotelBusinessHealthService(new RecordingSource(), conversion);

        var result = await service.GetAsync("production", "hotels", "  ", CancellationToken.None);

        Assert.Null(conversion.VacationClubId);
        Assert.Equal(0, result!.VacationClubId);
        Assert.Equal(12.5m, result.Current.ConversionPercent);
        Assert.Null(result.PriorWeek.ConversionPercent);
    }

    [Fact]
    public async Task NonProductionStillReadsConversionForTheSelectedClub()
    {
        var source = new RecordingSource();
        var conversion = new RecordingConversion(40m, null);
        var service = new GetHotelBusinessHealthService(source, conversion);

        var result = await service.GetAsync("qa", "Hotel", "15639", CancellationToken.None);

        Assert.Equal(0, source.Reads);
        Assert.Equal("qa", conversion.Environment);
        Assert.Equal(15639L, conversion.VacationClubId);
        Assert.False(result!.BookingsAvailable);
        Assert.Equal(40m, result.Current.ConversionPercent);
        Assert.Equal(0, result.Current.Bookings);
    }

    [Fact]
    public async Task OtherDomainsDoNotReadTheHotelView()
    {
        var source = new RecordingSource();
        var service = new GetHotelBusinessHealthService(source);

        Assert.Null(await service.GetAsync("production", "air", null, CancellationToken.None));
        Assert.Null(await service.GetAsync("production", "all", "15639", CancellationToken.None));
        Assert.Null(await service.GetAsync("production", null, null, CancellationToken.None));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task ANonNumericClubDoesNotFallBackToEveryClub()
    {
        var source = new RecordingSource();
        var service = new GetHotelBusinessHealthService(source);

        Assert.Null(await service.GetAsync("production", "hotels", "stays.tmobiletravel.com", CancellationToken.None));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task BlankEnvironmentDefaultsToProduction()
    {
        var source = new RecordingSource();
        var service = new GetHotelBusinessHealthService(source);

        var result = await service.GetAsync("  ", "hotels", "", CancellationToken.None);

        Assert.Equal(GetHotelBusinessHealthService.DefaultEnvironment, result!.Environment);
        Assert.Null(source.VacationClubId);
    }

    [Fact]
    public async Task SampleKeepsTheCardFiguresForTests()
    {
        var result = await new SampleHotelBusinessHealthSource().ReadAsync("production", null, CancellationToken.None);

        Assert.Equal(SampleHotelBusinessHealthSource.SourceName, result!.Source);
        Assert.Equal(340, result.Current.Bookings);
        Assert.Equal(72500m, result.Current.Revenue);
        Assert.Equal(96, result.PriorWeek.Bookings);
        Assert.Equal(19400m, result.PriorWeek.Revenue);
        Assert.Equal(result.Current.WindowStart.AddDays(-7), result.PriorWeek.WindowStart);
    }

    private sealed class RecordingConversion(decimal? current, decimal? prior) : IHotelBookingConversionSource
    {
        public string? Environment { get; private set; }

        public long? VacationClubId { get; private set; }

        public Task<HotelBookingConversionSnapshot?> ReadAsync(
            string environment,
            long? vacationClubId,
            CancellationToken cancellationToken)
        {
            Environment = environment;
            VacationClubId = vacationClubId;
            var window = new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero);
            return Task.FromResult<HotelBookingConversionSnapshot?>(new HotelBookingConversionSnapshot(
                "sql",
                window,
                window.AddDays(-7),
                current,
                prior,
                [
                    new HotelBookingConversionOperation(window, true, vacationClubId?.ToString()),
                    new HotelBookingConversionOperation(window.AddMinutes(1), false, vacationClubId?.ToString()),
                ]));
        }
    }

    private sealed class RecordingSource : IHotelBusinessHealthSource
    {
        public int Reads { get; private set; }

        public string? Environment { get; private set; }

        public long? VacationClubId { get; private set; }

        public Task<HotelBusinessHealth?> ReadAsync(string environment, long? vacationClubId, CancellationToken cancellationToken)
        {
            Reads++;
            Environment = environment;
            VacationClubId = vacationClubId;
            var window = new DateTimeOffset(2026, 11, 2, 12, 12, 31, TimeSpan.Zero);
            return Task.FromResult<HotelBusinessHealth?>(new HotelBusinessHealth(
                "fabric",
                "hotel",
                environment,
                "Hour",
                vacationClubId ?? 0,
                new HotelBusinessHealthReading(window, null, 7, 2672.80m, 1),
                new HotelBusinessHealthReading(window.AddDays(-7), null, 3, 357.70m, 0)));
        }
    }
}
