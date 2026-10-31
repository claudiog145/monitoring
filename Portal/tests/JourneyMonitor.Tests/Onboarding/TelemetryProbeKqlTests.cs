using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Onboarding;

namespace JourneyMonitor.Tests.Onboarding;

public class TelemetryProbeKqlTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryProbeAddressesTheResourceThroughThePlaceholder()
    {
        string[] probes =
        [
            TelemetryProbeKql.RowCount("club", TelemetryProbeTables.Requests, From, To),
            TelemetryProbeKql.CustomDimensions("club", TelemetryProbeTables.Requests, From, To, 50_000, 200),
            TelemetryProbeKql.Hosts("club", From, To, 50_000, 200),
        ];

        foreach (var kql in probes)
        {
            Assert.Contains("app('{{SOURCE:club}}')", kql);
            Assert.DoesNotContain("/subscriptions/", kql);
            Assert.Matches(KqlSourceResolver.PlaceholderPattern, kql);
        }
    }

    [Fact]
    public void ProbesCarryNoPlaceholderOtherThanTheSourceOne()
    {
        // KqlPlaceholders lets {{SOURCE:...}} through and throws on anything else left behind.
        KqlPlaceholders.AssertNoneLeft(TelemetryProbeKql.RowCount("club", TelemetryProbeTables.Requests, From, To), "test");
        KqlPlaceholders.AssertNoneLeft(TelemetryProbeKql.CustomDimensions("club", TelemetryProbeTables.Traces, From, To, 10, 10), "test");
        KqlPlaceholders.AssertNoneLeft(TelemetryProbeKql.Hosts("club", From, To, 10, 10), "test");
    }

    [Fact]
    public void TheWindowIsBoundedOnBothEnds()
    {
        var kql = TelemetryProbeKql.RowCount("club", TelemetryProbeTables.Requests, From, To);

        Assert.Contains("timestamp >= datetime(2026-10-15T00:00:00.000Z)", kql);
        Assert.Contains("timestamp < datetime(2026-10-16T00:00:00.000Z)", kql);
        Assert.Contains("summarize RowCount = count()", kql);
    }

    [Fact]
    public void TheDimensionProbeExpandsTheBagAndCapsTheSample()
    {
        var kql = TelemetryProbeKql.CustomDimensions("club", TelemetryProbeTables.CustomEvents, From, To, 1_234, 42);

        Assert.Contains("app('{{SOURCE:club}}').customEvents", kql);
        Assert.Contains("mv-expand DimensionKey = bag_keys(customDimensions) to typeof(string)", kql);
        Assert.Contains("extend DimensionValue = tostring(customDimensions[DimensionKey])", kql);
        Assert.Contains("| take 1234", kql);
        Assert.Contains("| top 42 by OccurrenceCount desc", kql);
        // SampledRows is what every coverage percentage is divided by, so the query has to return it.
        Assert.Contains("extend SampledRows = _sampledRows", kql);
    }

    [Fact]
    public void TheHostProbeUnionsTheHostBearingTablesTolerantly()
    {
        var kql = TelemetryProbeKql.Hosts("club", From, To, 500, 25);

        Assert.Contains("union isfuzzy=true", kql);
        Assert.Contains("app('{{SOURCE:club}}').requests", kql);
        Assert.Contains("app('{{SOURCE:club}}').pageViews", kql);
        Assert.Contains("summarize ObservationCount = count() by TelemetryTable, RoleName, Host", kql);
        Assert.Contains("| top 25 by ObservationCount desc", kql);
        // Capped inside each branch, so the union cannot scan more than cap x branches.
        Assert.Equal(2, Occurrences(kql, "| take 500"));
    }

    [Theory]
    [InlineData("club'); externaldata")]
    [InlineData("club | project 1")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAliasOutsideTheAllowedCharacterSetIsRejected(string alias)
    {
        Assert.False(TelemetrySourceAlias.IsValid(alias));
        Assert.Throws<ArgumentException>(() => TelemetryProbeKql.RowCount(alias, TelemetryProbeTables.Requests, From, To));
        Assert.Throws<ArgumentException>(() => TelemetryProbeKql.Hosts(alias, From, To, 10, 10));
    }

    [Theory]
    [InlineData("requests | project 1")]
    [InlineData("StorageBlobLogs")]
    [InlineData("")]
    public void ATableOutsideTheAllowListIsRejected(string table)
    {
        Assert.Null(TelemetryProbeTables.Canonical(table));
        Assert.Throws<ArgumentException>(() => TelemetryProbeKql.RowCount("club", table, From, To));
    }

    [Theory]
    [InlineData("REQUESTS", "requests")]
    [InlineData("pageviews", "pageViews")]
    [InlineData(" Traces ", "traces")]
    public void TableNamesAreCanonicalisedToTheCasingKustoExpects(string input, string expected) =>
        Assert.Equal(expected, TelemetryProbeTables.Canonical(input));

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}

public class TelemetryProbeFailureClassifierTests
{
    [Theory]
    [InlineData("App Insights query 400: Failed to resolve table or column expression named 'traces'")]
    [InlineData("SEM0100: 'union' operator: Failed to resolve table expression")]
    public void AMissingTableIsNotAQueryFailure(string message) =>
        Assert.Equal(TelemetryProbeState.TableAbsent, TelemetryProbeFailureClassifier.Classify(message));

    [Theory]
    [InlineData("App Insights query 403: The provided credentials have insufficient access")]
    [InlineData("App Insights query 429: Too many requests")]
    [InlineData("The operation was canceled.")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsAQueryFailure(string? message) =>
        Assert.Equal(TelemetryProbeState.Failed, TelemetryProbeFailureClassifier.Classify(message));

    [Fact]
    public void TheInnermostMessageIsWhatGetsClassified()
    {
        var exception = new InvalidOperationException(
            "Probe failed",
            new InvalidOperationException("App Insights query 400: Failed to resolve table or column expression named 'customEvents'"));

        Assert.Equal(TelemetryProbeState.TableAbsent, TelemetryProbeFailureClassifier.Classify(exception));
        Assert.Contains("Probe failed | App Insights query 400", TelemetryProbeFailureClassifier.Flatten(exception));
    }
}
