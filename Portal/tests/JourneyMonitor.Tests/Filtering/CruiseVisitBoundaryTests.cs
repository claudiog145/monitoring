using JourneyMonitor.Infrastructure.Kql;

namespace JourneyMonitor.Tests.Filtering;

public class CruiseVisitBoundaryTests
{
    private static readonly string CruiseKql = new KqlQueryTextProvider().GetJourneyKql("cruises");

    [Fact]
    public void CruiseVisitsAreSplitOnceBeforeSsoIsAttached()
    {
        Assert.Equal(1, Count(CruiseKql, "row_window_session"));
        Assert.Contains("row_rank_dense(VisitStart, prev(SessionId) != SessionId)", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("strcat(SessionId, \"-\", tostring(VisitSeq))", CruiseKql, StringComparison.Ordinal);
        Assert.DoesNotContain("yyyyMMddHHmmss", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("union webCalls, webErrorCalls, clubSsoCalls, coreCalls, coreErrorCalls", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("FrontendStart = SessionStart", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("FrontendEnd = SessionEnd", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("FrontendStart, FrontendEnd, Journey", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("LastTs = max(Ts)", CruiseKql, StringComparison.Ordinal);
        Assert.Contains("LastSeen = max(StepEnd)", CruiseKql, StringComparison.Ordinal);
        var cars = new KqlQueryTextProvider().GetJourneyKql("cars");
        Assert.Contains("LastTs = max(Ts)", cars, StringComparison.Ordinal);
        Assert.Contains("LastSeen = max(StepEnd)", cars, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
