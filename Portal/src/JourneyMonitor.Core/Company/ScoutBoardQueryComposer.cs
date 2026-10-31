using System.Globalization;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Core.Company;

/// <summary>Which signal the board ranks on. The machinery either side of the ranking is identical.</summary>
public enum ScoutBoardKind
{
    Latency,
    Exception
}

/// <summary>
/// Everything about one board run that is not a property of the company: the window, the grain,
/// how many rows survive per entity, and an optional narrowing to a single service or entity.
/// </summary>
/// <param name="ServiceFilter">A <see cref="ServiceDefinition.Key"/>, or null/empty for "every service".</param>
/// <param name="EntityFilter">An entity value as the profile's identity sources produce it, or null/empty for "every entity".</param>
public sealed record ScoutBoardQueryRequest(
    ScoutBoardKind Kind,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int BucketMinutes = 5,
    int RowsPerEntity = 10,
    string? ServiceFilter = null,
    string? EntityFilter = null);

/// <summary>
/// Composes a complete, runnable Scout board query for a company from its
/// <see cref="CompanyProfile"/> and one <see cref="ScoutBoardQueryRequest"/>.
///
/// The template carries the invariant half - bucketing, the request/exception join, the
/// bucket-over-bucket comparison, the per-entity partition and the GroupPayload bag - and names
/// nobody. Everything a company decides arrives through a placeholder. That split is the point of
/// the component: onboarding a new enterprise is writing a profile, not editing KQL.
///
/// The composed text still contains <c>{{SOURCE:alias}}</c>, and only that: resolving an alias to
/// an ARM resource id needs an environment and a database read, so <c>IKqlSourceResolver</c> does
/// it later. Every other placeholder is gone by the time <see cref="Compose"/> returns, and
/// <see cref="KqlPlaceholders.AssertNoneLeft"/> is what proves it.
/// </summary>
public static class ScoutBoardQueryComposer
{
    /// <summary>The invariant half, embedded in JourneyMonitor.Infrastructure alongside every other .kql.txt.</summary>
    public const string TemplateResourceName = "JourneyMonitor.Infrastructure.Kql.company-scout-board.kql.template.txt";

    /// <summary>
    /// The ordered decision the composed query encodes, before it is rendered as text.
    ///
    /// Public because the agreement between this composer and
    /// <see cref="ProfileDrivenAdapter.ResolveStep"/> is the thing most worth testing and the one
    /// thing a test cannot check by running the KQL.
    /// </summary>
    public static IReadOnlyList<ComposedRule> ComposeRules(CompanyProfile profile) =>
        CompanyKqlTemplater.BuildRules(profile);

    /// <summary>
    /// Fills <paramref name="template"/> for <paramref name="profile"/> and
    /// <paramref name="request"/>.
    /// </summary>
    /// <param name="template">
    /// The board template text. Passed in rather than loaded here because the KQL lives in
    /// JourneyMonitor.Infrastructure, which depends on this assembly and not the other way round -
    /// and because the stored-query admin path already lets an operator edit a template, which this
    /// signature keeps working.
    /// </param>
    public static string Compose(CompanyProfile profile, ScoutBoardQueryRequest request, string template)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("A board template is required; an empty one would compose an empty board.", nameof(template));
        }

        var newLine = CompanyKqlTemplater.DetectNewLine(template);

        var kql = CompanyKqlTemplater.Render(template, profile)
            .Replace(KqlPlaceholders.ServiceFilter, CompanyKqlTemplater.Literal(request.ServiceFilter?.Trim() ?? ""), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.EntityFilter, CompanyKqlTemplater.Literal(request.EntityFilter?.Trim() ?? ""), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.BucketMinutes, Positive(request.BucketMinutes), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.RowsPerEntity, Positive(request.RowsPerEntity), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.TimeStart, ToKqlDatetime(request.WindowFrom), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.TimeEnd, ToKqlDatetime(request.WindowTo), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.PriorityModel, PriorityModel(request.Kind, newLine), StringComparison.Ordinal)
            .Replace(KqlPlaceholders.PrioritySort, PrioritySort(request.Kind), StringComparison.Ordinal);

        KqlPlaceholders.AssertNoneLeft(kql, $"Composed scout board KQL for profile \"{profile.ProfileKey}\"");

        return kql;
    }

    /// <summary>
    /// The gate, the ratios and the rank. Both kinds compute all three ratios: an exception board
    /// that could not say the latency also tripled would hand the next agent half a story.
    /// </summary>
    private static string PriorityModel(ScoutBoardKind kind, string newLine)
    {
        const string ratios =
            "| extend LatencyRatio=iff(PreviousP95>0,P95Ms/PreviousP95,real(null))," +
            "ExceptionRatio=iff(PreviousExceptions>0,todouble(Exceptions)/PreviousExceptions,real(null))," +
            "FailedRate=iff(Requests>0,100.0*FailedRequests/Requests,real(null))";

        return kind switch
        {
            ScoutBoardKind.Latency => string.Join(newLine,
                "| where Requests>0",
                ratios,
                "| extend PriorityRank=case(Requests<5,0,",
                " (P95Ms>=10000 and LatencyRatio>=3 and FailedRequests>=3) or (P95Ms>=30000 and FailedRequests>=3 and FailedRate>=10) or (P95Ms>=60000 and Requests>=20),3,",
                " P95Ms>=10000 or (P95Ms>=2000 and LatencyRatio>=1.5) or FailedRequests>=3,2,1)",
                "| extend Reason=case(PriorityRank==0,\"Fewer than 5 requests: check individual durations\"," +
                "PriorityRank==3 and P95Ms>=60000 and Requests>=20,\"P95 >=60s with >=20 requests\"," +
                "PriorityRank==3,\"Severe latency with multiple failed requests\"," +
                "PriorityRank==2 and LatencyRatio>=1.5,\"Latency increase or repeated failed requests\"," +
                "PriorityRank==2,\"High latency or repeated failed requests\"," +
                "\"Highest observed latency; check the preceding interval\")"),

            ScoutBoardKind.Exception => string.Join(newLine,
                "| where Exceptions>0",
                ratios,
                "| extend PriorityRank=case(Exceptions>=100 and (ExceptionRatio>=3 or PreviousExceptions==0 or (P95Ms>=10000 and Requests>=5)),3,",
                " Exceptions>=20 and (ExceptionRatio>=2 or PreviousExceptions==0 or FailedRequests>=3) or Exceptions>=50,2,1)",
                "| extend Reason=case(PriorityRank==3 and ExceptionRatio>=3,\"At least 100 exceptions and >=3x the preceding interval\"," +
                "PriorityRank==3 and PreviousExceptions==0,\"At least 100 exceptions after a zero-exception interval\"," +
                "PriorityRank==3,\"At least 100 exceptions with P95 >=10s\"," +
                "PriorityRank==2,\"Elevated exception count, growth or failed requests\"," +
                "\"Observed exceptions; inspect type and recurrence\")"),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No priority model for this board kind.")
        };
    }

    private static string PrioritySort(ScoutBoardKind kind) => kind switch
    {
        ScoutBoardKind.Latency => "PriorityRank desc,P95Ms desc",
        ScoutBoardKind.Exception => "PriorityRank desc,Exceptions desc",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No partition sort for this board kind.")
    };

    /// <summary>Zero buckets or zero rows per entity is a board with nothing on it, so the floor is 1.</summary>
    private static string Positive(int value) => Math.Max(1, value).ToString(CultureInfo.InvariantCulture);

    /// <summary>Invariant culture explicitly: the ':' and '.' of the format string are culture-sensitive separators.</summary>
    private static string ToKqlDatetime(DateTimeOffset value) =>
        string.Create(CultureInfo.InvariantCulture, $"datetime({value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}Z)");
}
