using JourneyMonitor.Core.Incidents;

namespace JourneyMonitor.Tests.Incidents;

public class ExceptionSourceTests
{
    [Theory]
    [InlineData("Browser", ExceptionSource.Browser)]
    [InlineData("browser", ExceptionSource.Browser)]
    [InlineData(" Browser ", ExceptionSource.Browser)]
    [InlineData("PC", ExceptionSource.Server)]
    [InlineData("Server", ExceptionSource.Server)]
    [InlineData("", ExceptionSource.Server)]
    [InlineData(null, ExceptionSource.Server)]
    public void OnlyBrowserIsStoredAsBrowser(string? clientType, string source)
    {
        Assert.Equal(source, ExceptionSource.Normalize(clientType));
    }
}
