namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Minimal read used by <c>GoldenSignalCollector</c> for the exception_rate golden signal - total
/// exception occurrences for a domain since a point in time (see
/// <c>monitoring.usp_GetExceptionOccurrenceCount</c>, a cross-schema query against
/// <c>incident.Occurrence</c>/<c>incident.Incident</c>). Deliberately narrower than
/// <see cref="IIncidentReader"/> (which returns full grouped incident rows for the Exceptions tab
/// UI) - the collector only needs a bucketed count, not incident detail.
/// </summary>
public interface IExceptionOccurrenceCounter
{
    /// <param name="until">Exclusive upper bound - required so a delayed collector run cannot pull
    /// occurrences that belong to a later bucket into this one.</param>
    Task<int> CountSinceAsync(string domain, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}
