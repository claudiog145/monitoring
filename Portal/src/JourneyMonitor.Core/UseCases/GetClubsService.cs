using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Use case behind GET /api/clubs. Routing/validation is a direct port of handleClubsApi in
/// server.js, but the club list itself now comes from the platform database's
/// <c>brand.Information</c> table (<see cref="IBrandCatalog"/>) instead of the App Insights
/// discovery KQL. The built-in demo list remains the fallback when the database is not
/// configured, unreachable, or empty.
/// </summary>
public sealed class GetClubsService(IBrandCatalog brandCatalog)
{
    public const string DatabaseSource = "database";

    public async Task<ClubsResult> GetAsync(string domain, string? win, string? environment, bool skipCache, string? forcedMode, CancellationToken ct)
    {
        var d = domain.ToLowerInvariant();
        var effectiveWin = win ?? (ClubFilter.UsesPortalTimeRange(d) ? "15m" : "2h");
        var env = InsightsEnvironment.Normalize(environment);
        var control = ClubFilter.ControlState(d);

        if (control.Disabled || !control.CanList)
        {
            return new ClubsResult
            {
                Source = "none",
                Win = effectiveWin,
                Domain = d,
                Disabled = control.Disabled,
                Placeholder = control.Placeholder,
            };
        }

        string? fallbackReason = null;
        if (forcedMode != "demo")
        {
            if (!brandCatalog.IsConfigured)
            {
                fallbackReason = "Brand catalog database is not configured.";
            }
            else
            {
                try
                {
                    var rows = await brandCatalog.GetAllAsync(ct);
                    var clubs = d == "hotels" && env == InsightsEnvironment.Staging
                        ? BrandClubOptions.ForStagingHotels(rows)
                        : BrandClubOptions.ForDomain(rows, d);
                    if (clubs.Count > 0)
                    {
                        return new ClubsResult
                        {
                            Source = DatabaseSource,
                            Win = effectiveWin,
                            Domain = d,
                            Environment = env,
                            FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
                            Clubs = clubs,
                            Placeholder = control.Placeholder,
                        };
                    }
                    fallbackReason = "brand.Information has no clubs for this domain.";
                }
                catch
                {
                    fallbackReason = "Brand catalog database unavailable.";
                }
            }
        }

        var demo = BuildDemoClubs(d, effectiveWin, env);
        demo.Placeholder = control.Placeholder;
        if (fallbackReason is not null)
        {
            demo.Warning = $"{demo.Warning} {fallbackReason}";
        }
        return demo;
    }

    private static ClubsResult BuildDemoClubs(string domain, string win, string environment)
    {
        (IReadOnlyList<DemoClub> Clubs, string Source, string Warning) pick = domain switch
        {
            "air" => (ClubFilter.DemoAirClubs, "demo", "Demo mode: known Air club only, not a live club list."),
            "cruises" => environment == InsightsEnvironment.Staging
                ? (ClubFilter.DemoCruiseStagingClubs, "configured", "Cruise Staging uses UAT host uat-cruise-web.ovscruise.com and partner 308.")
                : (ClubFilter.DemoCruiseClubs, "configured", "Cruise telemetry identifies T-Mobile by partner 308; club 15639 is not present."),
            "cars" => environment == InsightsEnvironment.Staging
                ? (ClubFilter.DemoCarStagingClubs, "demo", "Demo mode: known Car club only, not a live club list.")
                : (ClubFilter.DemoCarClubs, "demo", "Demo mode: known Car club only, not a live club list."),
            _ => environment == InsightsEnvironment.Staging
                ? (ClubFilter.DemoHotelStagingClubs, "demo", "Demo mode: known Hotel club only, not a live club list.")
                : (ClubFilter.DemoHotelClubs, "demo", "Demo mode: known Hotel club only, not a live club list."),
        };

        return new ClubsResult
        {
            Source = pick.Source,
            Win = win,
            Domain = domain,
            Environment = environment,
            FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
            Clubs = pick.Clubs.Select(ClubFilter.FormatClubOption)
                .Select(c => new ClubOption
                {
                    ClubId = c.ClubId,
                    Host = c.Host,
                    PartnerId = BrandClubOptions.UsesPartnerId(domain) ? c.ClubId : c.ClubId == "15639" ? "308" : "",
                    Value = c.Value,
                    Label = c.Label,
                    Events = c.Events,
                })
                .ToList(),
            Warning = pick.Warning,
        };
    }
}
