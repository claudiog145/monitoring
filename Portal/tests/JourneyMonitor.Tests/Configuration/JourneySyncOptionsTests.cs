using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Tests.Configuration;

public class JourneySyncOptionsTests
{
    [Fact]
    public void DefaultsToFilteredRowCapForDurableSnapshots()
    {
        var options = new JourneySyncOptions();

        Assert.Equal(50, options.InitialRowCap);
        Assert.Equal(200, options.FilteredRowCap);
        Assert.Equal(200, options.NormalizeRequestedRowCap("200"));
        Assert.Equal(50, options.NormalizeRequestedRowCap("15"));
    }

    [Fact]
    public void DefaultsToHotelsAndCruises()
    {
        var options = new JourneySyncOptions();

        Assert.Equal(new[] { "hotels", "cruises" }, options.EnumerateDomains());
    }
}
