using System.Text.RegularExpressions;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Core.Configuration;

/// <summary>Thrown for the same validation failures query-appinsights.js/insights-config.js throw as plain Errors.</summary>
public sealed class InsightsConfigurationException(string message) : Exception(message);

/// <summary>Production | Staging | Development, normalized. Port of insights-config.js normalizeEnvironment.</summary>
public static class InsightsEnvironment
{
    public const string Production = "production";
    public const string Staging = "staging";
    public const string Development = "development";

    public static string Normalize(string? value)
    {
        var raw = string.IsNullOrEmpty(value) ? Production : value.Trim().ToLowerInvariant();
        return raw switch
        {
            "production" or "prod" => Production,
            "staging" or "uat" or "stage" => Staging,
            "development" or "dev" => Development,
            _ => throw new InsightsConfigurationException("environment must be production, staging or development"),
        };
    }
}

/// <summary>
/// Resolves which Application Insights resource/tenant to query for a domain + environment.
/// Direct, behavior-preserving port of insights-config.js (resolveInsightsSource, isTenantId,
/// isInsightsResourceId). Cars reuses the Hotel resource but relabels any thrown message
/// from "Hotel" to "Cars" (sourceForDomain in query-appinsights.js).
/// </summary>
public static class InsightsSourceResolver
{
    private static readonly Regex TenantIdPattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InsightsResourceIdPattern = new(
        @"^/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/resourceGroups/[A-Za-z0-9._()-]+/providers/[Mm]icrosoft\.[Ii]nsights/components/[A-Za-z0-9._-]+$",
        RegexOptions.Compiled);

    // Built from TelemetrySourceDefaults so the resource ids live in exactly one place. These stay
    // as the fallback for Insights config that does not set them explicitly; the catalog row is
    // what a deployment edits to point at a different subscription.
    public static readonly IReadOnlyDictionary<string, (string UiResource, string ApiResource)> AirCrossResource =
        new Dictionary<string, (string, string)>
        {
            [InsightsEnvironment.Production] = (
                DefaultResource(TelemetrySourceDefaults.AirUi, InsightsEnvironment.Production),
                DefaultResource(TelemetrySourceDefaults.AirApi, InsightsEnvironment.Production)),
            [InsightsEnvironment.Staging] = (
                DefaultResource(TelemetrySourceDefaults.AirUi, InsightsEnvironment.Staging),
                DefaultResource(TelemetrySourceDefaults.AirApi, InsightsEnvironment.Staging)),
        };

    public static readonly IReadOnlyDictionary<string, (string ClubResource, string CoreResource, string DefaultHost)> CruiseCrossResource =
        new Dictionary<string, (string, string, string)>
        {
            [InsightsEnvironment.Production] = (
                DefaultResource(TelemetrySourceDefaults.Club, InsightsEnvironment.Production),
                DefaultResource(TelemetrySourceDefaults.CruiseCore, InsightsEnvironment.Production),
                "cruise.tmobiletravel.com"),
            [InsightsEnvironment.Staging] = (
                DefaultResource(TelemetrySourceDefaults.Club, InsightsEnvironment.Staging),
                DefaultResource(TelemetrySourceDefaults.CruiseCore, InsightsEnvironment.Staging),
                "uat-cruise-web.ovscruise.com"),
        };

    private static string DefaultResource(string alias, string environment) =>
        TelemetrySourceDefaults.All.First(s =>
            s.Alias == alias && s.Environment == environment).ResourceId;

    public static bool IsTenantId(string? value) => value is not null && TenantIdPattern.IsMatch(value);

    public static bool IsInsightsResourceId(string? value) => value is not null && InsightsResourceIdPattern.IsMatch(value);

    private static bool Configured(string? value) => !string.IsNullOrEmpty(value) && !value.StartsWith('<');

    private static string RequireTenantId(string? value, string domain)
    {
        if (!IsTenantId(value)) throw new InsightsConfigurationException($"{domain} tenantId must be a GUID (got: \"{value}\").");
        return value!;
    }

    private static string RequireInsightsResourceId(string? value, string label)
    {
        if (!IsInsightsResourceId(value)) throw new InsightsConfigurationException($"{label} must be an Application Insights ARM resource id.");
        return value!;
    }

    /// <summary>Resolves the source for "hotels" or "cars" (Cars is Hotel with relabeled errors).</summary>
    public static InsightsSource ResolveForDomain(InsightsOptions cfg, string domain, string? environment)
    {
        var d = domain.ToLowerInvariant();
        var env = InsightsEnvironment.Normalize(environment);
        if (d == "cars")
        {
            if (env == InsightsEnvironment.Development)
            {
                throw new InsightsConfigurationException("Cars development App Insights is not configured.");
            }
            try
            {
                var hotelSource = Resolve(cfg, "hotels", env);
                return new InsightsSource
                {
                    Domain = "cars",
                    Environment = hotelSource.Environment,
                    TenantId = hotelSource.TenantId,
                    AppInsightsAppId = hotelSource.AppInsightsAppId,
                };
            }
            catch (InsightsConfigurationException ex)
            {
                throw new InsightsConfigurationException(Regex.Replace(ex.Message, "^Hotel ", "Cars "));
            }
        }
        return Resolve(cfg, d, env);
    }

    public static InsightsSource Resolve(InsightsOptions cfg, string domain, string? environment)
    {
        var d = domain.ToLowerInvariant();
        var env = InsightsEnvironment.Normalize(environment);

        if (d == "air")
        {
            var air = cfg.Air ?? new();
            if (env == InsightsEnvironment.Development)
            {
                throw new InsightsConfigurationException("Air development App Insights is not configured.");
            }
            if (!Configured(air.TenantId))
            {
                throw new InsightsConfigurationException("Air App Insights is not configured (set air.tenantId in local.config.json).");
            }
            string? appId = env == InsightsEnvironment.Staging ? air.Staging.AppInsightsAppId : air.AppInsightsAppId;
            if (!Configured(appId))
            {
                throw new InsightsConfigurationException(env == InsightsEnvironment.Staging
                    ? "Air staging App Insights is not configured (set air.staging.appInsightsAppId in local.config.json)."
                    : "Air App Insights is not configured (set air.appInsightsAppId and air.tenantId in local.config.json).");
            }
            var defaults = AirCrossResource[env == InsightsEnvironment.Staging ? "staging" : "production"];
            var uiResource = env == InsightsEnvironment.Staging && Configured(air.Staging.UiResource) ? air.Staging.UiResource
                : Configured(air.UiResource) ? air.UiResource : defaults.UiResource;
            var apiResource = env == InsightsEnvironment.Staging && Configured(air.Staging.ApiResource) ? air.Staging.ApiResource
                : Configured(air.ApiResource) ? air.ApiResource : defaults.ApiResource;
            return new InsightsSource
            {
                Domain = "air",
                Environment = env,
                TenantId = RequireTenantId(air.TenantId, "air"),
                AppInsightsAppId = appId!,
                UiResource = RequireInsightsResourceId(uiResource, "Air UI App Insights"),
                ApiResource = RequireInsightsResourceId(apiResource, "Air API App Insights"),
            };
        }

        if (d == "cruises" || d == "cruise")
        {
            var cruises = cfg.Cruises ?? new();
            var hotels = cfg.Hotels ?? new();
            if (env == InsightsEnvironment.Development)
            {
                throw new InsightsConfigurationException("Cruise development App Insights is not configured.");
            }
            string? appId = env == InsightsEnvironment.Staging ? cruises.Staging.AppInsightsAppId : cruises.AppInsightsAppId;
            if (!Configured(appId))
            {
                throw new InsightsConfigurationException(env == InsightsEnvironment.Staging
                    ? "Cruise staging App Insights is not configured (set cruises.staging.appInsightsAppId in local.config.json)."
                    : "Cruise App Insights is not configured (set cruises.appInsightsAppId in local.config.json).");
            }
            var defaults = CruiseCrossResource[env == InsightsEnvironment.Staging ? "staging" : "production"];
            var clubResource = env == InsightsEnvironment.Staging && Configured(cruises.Staging.ClubResource) ? cruises.Staging.ClubResource
                : Configured(cruises.ClubResource) ? cruises.ClubResource : defaults.ClubResource;
            var coreResource = env == InsightsEnvironment.Staging && Configured(cruises.Staging.CoreResource) ? cruises.Staging.CoreResource
                : Configured(cruises.CoreResource) ? cruises.CoreResource : defaults.CoreResource;
            var defaultHost = env == InsightsEnvironment.Staging && Configured(cruises.Staging.DefaultHost) ? cruises.Staging.DefaultHost
                : Configured(cruises.DefaultHost) ? cruises.DefaultHost : defaults.DefaultHost;
            var tenantId = Configured(cruises.TenantId) ? cruises.TenantId : hotels.TenantId;
            return new InsightsSource
            {
                Domain = "cruises",
                Environment = env,
                TenantId = Configured(tenantId) ? RequireTenantId(tenantId, "cruises") : "",
                AppInsightsAppId = appId!,
                ClubResource = RequireInsightsResourceId(clubResource, "Cruise club App Insights"),
                CoreResource = RequireInsightsResourceId(coreResource, "Cruise Core App Insights"),
                DefaultHost = defaultHost,
            };
        }

        // hotels (default)
        var hotel = cfg.Hotels ?? new();
        if (env == InsightsEnvironment.Development)
        {
            throw new InsightsConfigurationException("Hotel development App Insights is not configured.");
        }
        string? hotelAppId = env == InsightsEnvironment.Staging
            ? hotel.Staging.AppInsightsAppId
            : (Configured(hotel.AppInsightsAppId) ? hotel.AppInsightsAppId : cfg.AppInsightsAppId);
        if (!Configured(hotelAppId))
        {
            throw new InsightsConfigurationException(env == InsightsEnvironment.Staging
                ? "Hotel staging App Insights is not configured (set hotels.staging.appInsightsAppId in local.config.json)."
                : "Missing App Insights App ID. Set AI_APP_ID or copy local.config.example.json to local.config.json.");
        }
        var hotelTenant = Configured(hotel.Staging.TenantId) && env == InsightsEnvironment.Staging ? hotel.Staging.TenantId : hotel.TenantId;
        return new InsightsSource
        {
            Domain = "hotels",
            Environment = env,
            TenantId = Configured(hotelTenant) ? RequireTenantId(hotelTenant, "hotels") : "",
            AppInsightsAppId = hotelAppId!,
        };
    }
}
