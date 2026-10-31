namespace JourneyMonitor.Core.Configuration;

/// <summary>
/// The "nothing unsubstituted reaches Azure" check, shared by every caller that fills a KQL
/// template. Getting a placeholder wrong does not fail loudly on its own - Application Insights
/// answers with a generic syntax error, or worse, a valid query over the wrong window.
///
/// <c>{{SOURCE:alias}}</c> is deliberately allowed through here: it is substituted later, by
/// <see cref="IKqlSourceResolver"/>, which needs the environment and a database read that these
/// synchronous builders do not have. That resolver throws on an alias it cannot resolve, so the
/// invariant still holds end to end - it is just enforced in two steps.
/// </summary>
public static class KqlPlaceholders
{
    /// <summary>
    /// The one <c>case()</c> cascade that decides which service AND which step a row belongs to.
    /// </summary>
    /// <remarks>
    /// Deliberately a single placeholder rather than one for the service and one for the step.
    /// Two cascades are two opinions about the same row, and the only thing keeping them equal is
    /// that nobody edits one of them - which is not a property, it is a hope. Here the rule that
    /// recognises the row is what names the service, exactly as
    /// <c>ProfileDrivenAdapter.ResolveStep</c> does.
    ///
    /// None of the placeholders on this type is added to the pass-through in
    /// <see cref="AssertNoneLeft"/>. <c>{{SOURCE:}}</c> earns that exemption because a later, async
    /// component still has to resolve it; these are substituted synchronously, from an in-memory
    /// profile and request, before any caller reaches that check. One left standing means the
    /// profile was missing or the composer was skipped - either way the query must not run.
    /// </remarks>
    public const string ServiceAndStepClassifier = "{{SERVICE_AND_STEP_CLASSIFIER}}";

    /// <inheritdoc cref="ServiceAndStepClassifier"/>
    public const string EntityIdentity = "{{ENTITY_IDENTITY}}";

    /// <inheritdoc cref="ServiceAndStepClassifier"/>
    public const string HostFilter = "{{HOST_FILTER}}";

    /// <summary>The <c>union</c> over the telemetry source aliases the profile declares, on the requests table.</summary>
    public const string SourceUnion = "{{SOURCE_UNION}}";

    /// <summary>The same aliases again, on the exceptions table - the two tables do not share a shape.</summary>
    public const string ExceptionSourceUnion = "{{EXCEPTION_SOURCE_UNION}}";

    /// <inheritdoc cref="ServiceAndStepClassifier"/>
    public static readonly IReadOnlyList<string> CompanyProfilePlaceholders =
        [ServiceAndStepClassifier, EntityIdentity, HostFilter, SourceUnion, ExceptionSourceUnion];

    /// <summary>Which signal the board ranks on: the <c>where</c> gate, the ratios, and the PriorityRank/Reason pair.</summary>
    public const string PriorityModel = "{{PRIORITY_MODEL}}";

    /// <summary>The sort key inside the per-entity partition. Has to agree with the model above.</summary>
    public const string PrioritySort = "{{PRIORITY_SORT}}";

    public const string TimeStart = "{{TIME_START}}";

    public const string TimeEnd = "{{TIME_END}}";

    public const string BucketMinutes = "{{BUCKET_MINUTES}}";

    /// <summary>
    /// How many rows survive per entity. Named for the role rather than for one company's word for
    /// it: the board this replaces called it {{TAKE_PER_CLUB}}, and "club" is Arrivia vocabulary.
    /// </summary>
    public const string RowsPerEntity = "{{ROWS_PER_ENTITY}}";

    /// <summary>Optional narrowing to a single service / entity. An empty value narrows nothing.</summary>
    public const string ServiceFilter = "{{SERVICE_FILTER}}";

    /// <inheritdoc cref="ServiceFilter"/>
    public const string EntityFilter = "{{ENTITY_FILTER}}";

    /// <summary>The placeholders filled from the board request rather than from the company profile.</summary>
    public static readonly IReadOnlyList<string> BoardRequestPlaceholders =
        [PriorityModel, PrioritySort, TimeStart, TimeEnd, BucketMinutes, RowsPerEntity, ServiceFilter, EntityFilter];

    public static void AssertNoneLeft(string kql, string context)
    {
        var index = 0;
        while (true)
        {
            var start = kql.IndexOf("{{", index, StringComparison.Ordinal);
            if (start < 0) return;

            if (kql.AsSpan(start).StartsWith("{{SOURCE:"))
            {
                index = start + 2;
                continue;
            }

            var end = kql.IndexOf("}}", start, StringComparison.Ordinal);
            var name = end > start ? kql[start..(end + 2)] : kql[start..Math.Min(start + 32, kql.Length)];
            throw new InvalidOperationException($"{context} still contains an unsubstituted placeholder: {name}");
        }
    }
}
