using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Reads the latest stored snapshot for a domain and returns it in the same
/// <see cref="JourneysResult"/> envelope as the live endpoint, with the rows in the same raw
/// shape, so the portal (and map-v6.js/map-air.js) consumes it without changes.
///
/// Source is reported as "stored" rather than "live" so the screen never claims the data is
/// current. app.js only uses Source for its refresh toast label; add "stored" to that label map
/// when the screen is switched over, otherwise it falls back to "unknown source".
/// </summary>
public sealed class GetStoredJourneysService(IJourneyStore journeyStore)
{
    public async Task<JourneysOutcome> GetAsync(string domain, CancellationToken cancellationToken)
    {
        var stored = await journeyStore.GetLatestAsync(domain, cancellationToken);

        if (stored is null)
        {
            return new JourneysOutcome(
                new JourneysResult
                {
                    Source = "stored",
                    Win = "",
                    Domain = domain,
                    RowCount = 0,
                    Warning = "No stored snapshot yet; the ingestion timer has not written this domain.",
                },
                200);
        }

        return new JourneysOutcome(
            new JourneysResult
            {
                Source = "stored",
                Win = stored.Window ?? "",
                Domain = domain,
                FetchedAt = stored.FetchedAt?.ToString("O"),
                RowCount = stored.Rows.Count,
                Rows = stored.Rows,
            },
            200);
    }
}
