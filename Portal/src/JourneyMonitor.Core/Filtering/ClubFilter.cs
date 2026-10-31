using System.Text.RegularExpressions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Filtering;

public sealed class ClubFilterException(string message) : Exception(message);

public sealed record DemoClub(string ClubId, string Host, int Events = 0);

/// <summary>
/// Direct port of club-filter.js: club/host/win validation, row-cap normalization,
/// KQL parameter injection, and the demo club lists used both as UI fallbacks and as
/// "known club" shortcuts that skip a live discovery round-trip.
/// </summary>
public static class ClubFilter
{
    private static readonly Regex ClubIdPattern = new(@"^\d{1,12}$", RegexOptions.Compiled);
    private static readonly Regex HostPattern = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WinPattern = new(@"^\d{1,3}h$", RegexOptions.Compiled);

    public static readonly string[] CruiseAllowedWins = ["15m", "1h", "2h", "6h", "24h", "7d"];
    private static readonly HashSet<string> CruiseWinSet = new(CruiseAllowedWins);

    public const int InitRowCap = 50;
    public const int FilterRowCap = 200;
    private static readonly HashSet<int> AllowedRowCaps = [InitRowCap, FilterRowCap];

    public static readonly IReadOnlyList<DemoClub> DemoHotelClubs = [new("15639", "stays.tmobiletravel.com")];
    public static readonly IReadOnlyList<DemoClub> DemoHotelStagingClubs = [new("15639", "tmobile.saveonuat.com")];
    public static readonly IReadOnlyList<DemoClub> DemoCarClubs = [new("15639", "stays.tmobiletravel.com")];
    public static readonly IReadOnlyList<DemoClub> DemoCarStagingClubs = [new("15639", "tmobile.saveonuat.com")];
    public static readonly IReadOnlyList<DemoClub> DemoAirClubs = [new("308", "")];
    public static readonly IReadOnlyList<DemoClub> DemoCruiseClubs = [new("308", "cruise.tmobiletravel.com")];
    public static readonly IReadOnlyList<DemoClub> DemoCruiseStagingClubs = [new("308", "uat-cruise-web.ovscruise.com")];

    /// <summary>
    /// Brand catalog URLs are production hosts. Staging timer queries must use the known staging
    /// host for that club, the same shortcut ClubHostResolver uses, or a hotel filter such as
    /// <c>url has stays.tmobiletravel.com</c> drops <c>tmobile.saveonuat.com</c>.
    /// </summary>
    public static IReadOnlyList<ClubOption> WithEnvironmentHosts(
        string domain,
        string? environment,
        IReadOnlyList<ClubOption> clubs)
    {
        string env;
        try
        {
            env = InsightsEnvironment.Normalize(environment);
        }
        catch (InsightsConfigurationException)
        {
            return clubs;
        }

        if (env != InsightsEnvironment.Staging) return clubs;

        var known = StagingHosts(domain);
        if (known.Count == 0) return clubs;

        var rewritten = new List<ClubOption>(clubs.Count);
        var changed = false;
        foreach (var club in clubs)
        {
            if (!known.TryGetValue(club.ClubId, out var host)
                || string.Equals(club.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(club);
                continue;
            }

            changed = true;
            rewritten.Add(new ClubOption
            {
                ClubId = club.ClubId,
                Host = host,
                PartnerId = club.PartnerId,
                Value = club.Value,
                Label = club.Label,
                Events = club.Events,
            });
        }

        return changed ? rewritten : clubs;
    }

    private static Dictionary<string, string> StagingHosts(string domain)
    {
        IReadOnlyList<DemoClub> demo = domain.ToLowerInvariant() switch
        {
            "hotels" => DemoHotelStagingClubs,
            "cars" => DemoCarStagingClubs,
            "cruises" => DemoCruiseStagingClubs,
            _ => [],
        };

        return demo
            .Where(club => IsValidHost(club.Host))
            .GroupBy(club => club.ClubId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Host, StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, string> ClubNames = new()
    {
        ["15639"] = "T-Mobile / T Life",
        ["308"] = "T-Mobile / T Life",
    };

    public static bool UsesPortalTimeRange(string? domain) =>
        domain?.ToLowerInvariant() is "cruises" or "hotels" or "air" or "cars";

    public static bool IsValidClubId(string? value) => value is not null && ClubIdPattern.IsMatch(value);

    public static bool IsValidHost(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 253 && HostPattern.IsMatch(value);

    public static bool IsValidWin(string? value) => value is not null && WinPattern.IsMatch(value);

    public static bool IsValidCruiseWin(string? value) => value is not null && CruiseWinSet.Contains(value);

    /// <summary>
    /// Parses portal window tokens (15m, 1h, 6h, 24h, 7d) into a duration. Rejects anything
    /// the KQL injectors would also reject so the SQL read and the live query agree.
    /// </summary>
    public static bool TryParseWindow(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!IsValidCruiseWin(value) && !IsValidWin(value)) return false;

        var match = Regex.Match(value, @"^(\d{1,3})(m|h|d)$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var amount = int.Parse(match.Groups[1].Value);
        duration = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            _ => default,
        };
        return duration > TimeSpan.Zero;
    }

    public static bool IsValidRowCap(int value) => AllowedRowCaps.Contains(value);

    public static int NormalizeRowCap(string? value)
    {
        if (int.TryParse(value, out var n) && IsValidRowCap(n)) return n;
        return InitRowCap;
    }

    public static int NormalizeRowCap(int value) => IsValidRowCap(value) ? value : InitRowCap;

    /// <summary>Kusto `top`/`make_set` need literal integers, so the cap is rewritten into the query text.</summary>
    public static string ApplyRowCap(string raw, int rowCap = InitRowCap)
    {
        // The request cap is already one of the two JourneySync values. Do not
        // clamp it back to the compiled default.
        var n = rowCap > 0 ? rowCap : InitRowCap;
        var text = Regex.Replace(raw, @"\|\s*top\s+\d+\s+by\s+LastSeen\s+desc", $"| top {n} by LastSeen desc", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\|\s*top\s+\d+\s+by\s+LastHint\s+desc", $"| top {n} by LastHint desc", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"make_set\(\s*CorrelationId\s*,\s*\d+\s*\)", $"make_set(CorrelationId, {n})", RegexOptions.IgnoreCase);
        return text;
    }

    public static string ClubLabel(string clubId, string? host)
    {
        if (ClubNames.TryGetValue(clubId, out var named)) return named;
        return !string.IsNullOrEmpty(host) ? $"{host} ({clubId})" : clubId;
    }

    public sealed record ClubControlState(bool Disabled, bool CanList, string Placeholder);

    public static ClubControlState ControlState(string? domain)
    {
        return domain?.ToLowerInvariant() switch
        {
            "hotels" => new(false, true, "All hotel clubs"),
            "cars" => new(false, true, "All car clubs"),
            "air" => new(false, true, "All air clubs"),
            "cruises" => new(false, true, "All cruise clubs"),
            _ => new(true, false, "Select a domain first"),
        };
    }

    public static ClubOptionDto FormatClubOption(DemoClub club) =>
        new(club.ClubId, club.Host, club.ClubId, ClubLabel(club.ClubId, club.Host), club.Events);

    public static string? ResolveHostFromClubs(IEnumerable<ClubOptionDto> clubs, string clubId) =>
        clubs.FirstOrDefault(c => c.ClubId == clubId)?.Host;

    // The journey texts carry {{SOURCE:alias}} rather than a resource path, so these match the
    // placeholder. Explicit Insights config still wins here; anything left unreplaced is resolved
    // from monitoring.TelemetrySource by KqlSourceResolver before the query is sent.
    private static readonly Regex ClubResourceCall = new(
        @"app\('\{\{SOURCE:club\}\}'\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoreResourceCall = new(
        @"app\('\{\{SOURCE:cruise-core\}\}'\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AirUiResourceCall = new(
        @"app\('\{\{SOURCE:air-ui\}\}'\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AirApiResourceCall = new(
        @"app\('\{\{SOURCE:air-api\}\}'\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string InjectJourneyKql(string raw, string win, string clubId, string host,
        string? clubResource = null, string? coreResource = null, int rowCap = InitRowCap, string? partnerId = null,
        string? environment = null)
    {
        if (!IsValidWin(win) && !IsValidCruiseWin(win)) throw new ClubFilterException("win must look like 2h, 15m or 7d");
        if (!IsValidClubId(clubId)) throw new ClubFilterException("club must be a numeric id");
        if (!IsValidHost(host)) throw new ClubFilterException("host must be a plain hostname");

        var text = Regex.Replace(raw, @"let win = \w+;", $"let win = {win};");
        if (IsStaging(environment)) text = UseHostInsteadOfHotelClubId(text, anyHost: false);
        text = InjectClubScope(text, clubId, host, partnerId);

        if (!string.IsNullOrEmpty(clubResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(clubResource)) throw new ClubFilterException("clubResource must be an Application Insights ARM resource id");
            text = ClubResourceCall.Replace(text, $"app('{clubResource}')");
        }
        if (!string.IsNullOrEmpty(coreResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(coreResource)) throw new ClubFilterException("coreResource must be an Application Insights ARM resource id");
            text = CoreResourceCall.Replace(text, $"app('{coreResource}')");
        }
        return ApplyRowCap(text, rowCap);
    }

    /// <summary>Air journey KQL has tmoClub + win only. The dropdown value is the ICE partner id. No host.</summary>
    public static string InjectAirJourneyKql(string raw, string win, string clubId,
        string? uiResource = null, string? apiResource = null, int rowCap = InitRowCap, string? partnerId = null)
    {
        if (!IsValidWin(win) && !IsValidCruiseWin(win)) throw new ClubFilterException("win must look like 2h, 15m or 7d");
        if (!IsValidClubId(clubId)) throw new ClubFilterException("club must be a numeric id");

        var text = Regex.Replace(raw, @"let win = \w+;", $"let win = {win};");
        text = Regex.Replace(text, "let tmoClub = \"[^\"]*\";", $"let tmoClub = \"{clubId}\";");
        text = InjectPartner(text, partnerId ?? clubId);

        if (!string.IsNullOrEmpty(uiResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(uiResource)) throw new ClubFilterException("uiResource must be an Application Insights ARM resource id");
            text = AirUiResourceCall.Replace(text, $"app('{uiResource}')");
        }
        if (!string.IsNullOrEmpty(apiResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(apiResource)) throw new ClubFilterException("apiResource must be an Application Insights ARM resource id");
            text = AirApiResourceCall.Replace(text, $"app('{apiResource}')");
        }
        return ApplyRowCap(text, rowCap);
    }

    /// <summary>
    /// Injects the portal window/host/cruise resources into a copied dashboard tile query.
    /// Strips <c>render</c> so the App Insights REST API returns a table.
    /// </summary>
    public static string InjectDashboardKql(string raw, string win, string clubId, string host,
        string? clubResource = null, string? coreResource = null, string? partnerId = null)
    {
        if (!IsValidWin(win) && !IsValidCruiseWin(win)) throw new ClubFilterException("win must look like 2h, 15m or 7d");
        if (!IsValidClubId(clubId)) throw new ClubFilterException("club must be a numeric id");
        if (!IsValidHost(host)) throw new ClubFilterException("host must be a plain hostname");

        var text = Regex.Replace(raw, @"\|\s*render\s+\w+\s*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(text, @"let win = \w+;", $"let win = {win};");
        text = InjectClubScope(text, clubId, host, partnerId);

        if (!string.IsNullOrEmpty(clubResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(clubResource)) throw new ClubFilterException("clubResource must be an Application Insights ARM resource id");
            text = ClubResourceCall.Replace(text, $"app('{clubResource}')");
        }
        if (!string.IsNullOrEmpty(coreResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(coreResource)) throw new ClubFilterException("coreResource must be an Application Insights ARM resource id");
            text = CoreResourceCall.Replace(text, $"app('{coreResource}')");
        }
        return text.Trim();
    }

    /// <summary>
    /// One journey query for every club in a vertical. Hotels are attributed by the Club
    /// dimension and cruises by PartnerId, then capped at <paramref name="rowCap"/> rows per id.
    /// Staging hotels match the hosts seeded on <c>brand.Information</c> and do not require the catalog club id.
    /// </summary>
    public static string InjectVerticalJourneyKql(string domain, string raw, string win,
        IReadOnlyList<string> clubIds, IReadOnlyList<string> hosts, IReadOnlyList<string> partnerIds,
        string? clubResource = null, string? coreResource = null, int rowCap = FilterRowCap,
        string? environment = null)
    {
        var d = domain.ToLowerInvariant();
        if (d is not ("hotels" or "cruises")) throw new ClubFilterException("vertical journey query supports hotels or cruises");
        if (!IsValidWin(win) && !IsValidCruiseWin(win)) throw new ClubFilterException("win must look like 2h, 15m or 7d");
        if (clubIds.Count == 0 || clubIds.Any(id => !IsValidClubId(id))) throw new ClubFilterException("club must be a numeric id");
        if (hosts.Any(host => !IsValidHost(host))) throw new ClubFilterException("host must be a plain hostname");
        if (d == "hotels" && hosts.Count == 0) throw new ClubFilterException("host must be a plain hostname");
        if (partnerIds.Any(id => !IsValidClubId(id))) throw new ClubFilterException("partner must be a numeric id");

        var n = rowCap > 0 ? rowCap : FilterRowCap;
        var text = Regex.Replace(raw, @"let win = \w+;", $"let win = {win};");
        text = Regex.Replace(text, "let tmoHost = \"[^\"]*\";", $"let tmoHosts = dynamic([{QuoteDynamic(hosts)}]);");
        text = Regex.Replace(text, "let tmoPartner = \"[^\"]*\";", $"let tmoPartners = dynamic([{QuoteDynamic(partnerIds)}]);");
        text = Regex.Replace(text, "let tmoClub = \"[^\"]*\";", $"let tmoClubs = dynamic([{QuoteDynamic(clubIds)}]);");
        text = ApplyInsightResources(text, clubResource, coreResource);

        if (d == "hotels")
        {
            var matchHostDomain = IsStaging(environment);
            if (matchHostDomain)
            {
                text = text.Replace("| where url has tmoHost", "| where url has_any (tmoHosts)");
                text = UseHostInsteadOfHotelClubId(text, anyHost: true);
                text = ScopeBackgroundCorrelationToHost(text);
            }
            else
            {
                text = text.Replace("| where url has tmoHost", "| where url has_any (tmoHosts)");
                text = text.Replace(
                    "| where PartnerId == tmoPartner or Club == tmoClub",
                    "| where Club in (tmoClubs) or PartnerId in (tmoPartners)");
            }

            text = Regex.Replace(
                text,
                @"(?m)^stageRows\r?$",
                (matchHostDomain ? HotelClubByUserAnyLet : HotelClubByUserLet) + "stageRows");
            text = text.Replace("| project UserId, Outcome,", "| join kind=inner clubByUser on UserId\n| project UserId, Outcome,");
            text = Regex.Replace(
                text,
                @"SlowestCallMs, OverallP95, SearchToConfirm_s,\r?\n\s*FirstSeen, LastSeen, Journey\r?\n\| top \d+ by LastSeen desc",
                "SlowestCallMs, OverallP95, SearchToConfirm_s,\n          FirstSeen, LastSeen, Journey, ClubId\n" + PerClubCap(n));
        }
        else
        {
            text = text.Replace("PartnerId == tmoPartner", "PartnerId in (tmoPartners)");
            text = text.Replace("url has tmoHost", "url has_any (tmoHosts)");
            text = text.Replace(
                "Ms = todouble(duration), Ts = timestamp, operation_Id;",
                "Ms = todouble(duration), Ts = timestamp, operation_Id, PartnerId;");
            text = Regex.Replace(text, @"(?m)^stageRows\r?$", CruisePartnerBySessionLet + "stageRows");
            text = text.Replace(
                "| project UserId, CorrelationId,",
                "| join kind=inner partnerBySession on UserId, SessionId\n| extend ClubId = PartnerId\n| project UserId, CorrelationId,");
            text = Regex.Replace(
                text,
                @"SearchToConfirm_s, FirstSeen, LastSeen, Journey\r?\n\| top \d+ by LastSeen desc",
                "SearchToConfirm_s, FirstSeen, LastSeen, Journey, ClubId\n" + PerClubCap(n));
        }

        if (!text.Contains("_Rank <= " + n, StringComparison.Ordinal))
        {
            throw new ClubFilterException("vertical journey query did not apply the per-club row cap");
        }

        return text.Trim();
    }

    /// <summary>
    /// One dashboard query for the vertical. Hotel charts are grouped by host (the tile does
    /// not filter club id). Cruise charts are grouped by partner id.
    /// </summary>
    public static string InjectVerticalDashboardKql(string domain, string raw, string win,
        IReadOnlyList<string> hosts, IReadOnlyList<string> partnerIds,
        string? clubResource = null, string? coreResource = null)
    {
        var d = domain.ToLowerInvariant();
        if (d is not ("hotels" or "cruises")) throw new ClubFilterException("vertical dashboard query supports hotels or cruises");
        if (!IsValidWin(win) && !IsValidCruiseWin(win)) throw new ClubFilterException("win must look like 2h, 15m or 7d");
        if (hosts.Any(host => !IsValidHost(host))) throw new ClubFilterException("host must be a plain hostname");
        if (d == "hotels" && hosts.Count == 0) throw new ClubFilterException("host must be a plain hostname");
        if (partnerIds.Any(id => !IsValidClubId(id))) throw new ClubFilterException("partner must be a numeric id");

        var text = Regex.Replace(raw, @"\|\s*render\s+\w+\s*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(text, @"let win = \w+;", $"let win = {win};");
        text = Regex.Replace(text, "let tmoHost = \"[^\"]*\";", $"let tmoHosts = dynamic([{QuoteDynamic(hosts)}]);");
        text = Regex.Replace(text, "let tmoPartner = \"[^\"]*\";", $"let tmoPartners = dynamic([{QuoteDynamic(partnerIds)}]);");
        text = text.Replace("Host == tmoHost", "Host in (tmoHosts)");
        text = text.Replace("url has tmoHost", "url has_any (tmoHosts)");
        text = text.Replace("PartnerId == tmoPartner", "PartnerId in (tmoPartners)");
        text = text.Replace("| distinct operation_Id, Stage", "| distinct operation_Id, Stage, Host");
        text = text.Replace(
            "by UserId, SessionId;",
            "by UserId, SessionId, PartnerId;");
        // The exceptions tile projects the staged join down to operation + step, which drops PartnerId.
        text = text.Replace(
            "| project operation_Id, Operation;",
            "| project operation_Id, Operation, PartnerId;");
        text = text.Replace(
            "by bin(timestamp, 5m), Series",
            "by bin(timestamp, 5m), Series, Host");
        text = text.Replace(
            "by bin(timestamp, 5m), Stage",
            "by bin(timestamp, 5m), Stage, Host");
        text = text.Replace(
            "by Time = bin(CoreTs, binSize), Operation;",
            "by Time = bin(CoreTs, binSize), Operation, PartnerId;");
        text = Regex.Replace(
            text,
            @"by Time = bin\(CoreTs, binSize\)\r?\n",
            "by Time = bin(CoreTs, binSize), PartnerId\n");
        text = text.Replace(
            "by bin(timestamp, 1h), Operation",
            "by bin(timestamp, 1h), Operation, PartnerId");
        text = ApplyInsightResources(text, clubResource, coreResource);
        return text.Trim();
    }

    private const string HotelClubByUserLet =
        "let clubByUser =\n    ev\n    | where isnotempty(UserId) and UserId != \"0\" and isnotempty(Club)\n    | where Club in (tmoClubs)\n    | summarize ClubId = take_any(Club) by UserId;\n";

    /// <summary>
    /// Staging keeps whatever club id arrived, and only for a user already seen on a
    /// host-filtered request (<c>hostUser</c>).
    /// </summary>
    private const string HotelClubByUserAnyLet =
        "let clubByUser =\n    ev\n    | where isnotempty(UserId) and UserId != \"0\" and isnotempty(Club)\n    | where UserId in (hostUser)\n    | summarize ClubId = take_any(Club) by UserId;\n";

    /// <summary>
    /// Background ZentrumHub calls join <c>userByCorr</c>, which is built from the whole app.
    /// Keep that join to users who already appeared on a host-filtered request.
    /// </summary>
    private static string ScopeBackgroundCorrelationToHost(string text)
    {
        const string marker = "let vendorViaCorr =";
        const string unscopedJoin = "| join kind=inner userByCorr on Corr";
        if (!text.Contains(marker, StringComparison.Ordinal) || !text.Contains(unscopedJoin, StringComparison.Ordinal))
        {
            throw new ClubFilterException("staging hotel query did not scope background correlations to the requested host");
        }

        text = text.Replace(marker, HostScopedVendorViaCorr);
        return text.Replace(unscopedJoin, "| join kind=inner userByHostCorr on Corr");
    }

    private const string HostScopedVendorViaCorr =
        "let hostUser =\n" +
        "    httpCalls\n" +
        "    | where isnotempty(UserId) and UserId != \"0\"\n" +
        "    | distinct UserId;\n" +
        "let userByHostCorr =\n" +
        "    userByCorr\n" +
        "    | where UserId in (hostUser)\n" +
        "    | where Corr in (httpCalls | project Corr);\n" +
        "let vendorViaCorr =";

    private static string UseHostInsteadOfHotelClubId(string text, bool anyHost)
    {
        var replacement = anyHost
            ? "| where tostring(customDimensions.site) has_any (tmoHosts)"
            : "| where tostring(customDimensions.site) has tmoHost";
        return text.Replace("| where PartnerId == tmoPartner or Club == tmoClub", replacement);
    }

    private static bool IsStaging(string? environment)
    {
        try
        {
            return InsightsEnvironment.Normalize(environment) == InsightsEnvironment.Staging;
        }
        catch (InsightsConfigurationException)
        {
            return false;
        }
    }

    private const string CruisePartnerBySessionLet =
        "let partnerBySession =\n    webBase\n    | summarize PartnerId = take_any(PartnerId) by UserId, SessionId;\n";

    private static string PerClubCap(int rowCap) =>
        "| sort by ClubId asc, LastSeen desc\n| serialize\n| extend _Rank = row_number(1, prev(ClubId) != ClubId)\n| where _Rank <= " + rowCap + "\n| project-away _Rank";

    private static string QuoteDynamic(IReadOnlyList<string> values) =>
        string.Join(",", values.Select(value => "\"" + value + "\""));

    private static string ApplyInsightResources(string text, string? clubResource, string? coreResource)
    {
        if (!string.IsNullOrEmpty(clubResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(clubResource)) throw new ClubFilterException("clubResource must be an Application Insights ARM resource id");
            text = ClubResourceCall.Replace(text, $"app('{clubResource}')");
        }
        if (!string.IsNullOrEmpty(coreResource))
        {
            if (!InsightsSourceResolver.IsInsightsResourceId(coreResource)) throw new ClubFilterException("coreResource must be an Application Insights ARM resource id");
            text = CoreResourceCall.Replace(text, $"app('{coreResource}')");
        }
        return text;
    }

    /// <summary>
    /// Rewrites the file defaults (<c>tmoHost</c>, <c>tmoPartner</c>, <c>tmoClub</c>) to the club
    /// selected in the dropdown. Partner is omitted when the query has no <c>tmoPartner</c> let.
    /// </summary>
    private static string InjectClubScope(string text, string clubId, string host, string? partnerId)
    {
        text = Regex.Replace(text, "let tmoHost = \"[^\"]*\";", $"let tmoHost = \"{host}\";");
        text = Regex.Replace(text, "let tmoClub = \"[^\"]*\";", $"let tmoClub = \"{clubId}\";");
        return InjectPartner(text, partnerId);
    }

    private static string InjectPartner(string text, string? partnerId)
    {
        if (string.IsNullOrEmpty(partnerId))
        {
            text = Regex.Replace(text, "let tmoPartner = \"[^\"]*\";", "let tmoPartner = \"\";");
            return Regex.Replace(text, @"PartnerId\s*==\s*tmoPartner", "false", RegexOptions.IgnoreCase);
        }
        if (!IsValidClubId(partnerId)) throw new ClubFilterException("partner must be a numeric id");
        return Regex.Replace(text, "let tmoPartner = \"[^\"]*\";", $"let tmoPartner = \"{partnerId}\";");
    }
}

public sealed record ClubOptionDto(string ClubId, string Host, string Value, string Label, int Events);
