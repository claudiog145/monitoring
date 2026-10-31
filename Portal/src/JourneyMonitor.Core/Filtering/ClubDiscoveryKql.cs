namespace JourneyMonitor.Core.Filtering;

/// <summary>
/// Builds the small "which clubs/hosts have traffic" discovery KQL used by GET /api/clubs.
/// Direct port of clubsDiscoveryKql / airClubsDiscoveryKql from club-filter.js.
/// </summary>
public static class ClubDiscoveryKql
{
    public static string FunnelRequestFilter(string domain)
    {
        if (domain.ToLowerInvariant() == "cars")
        {
            return "    | where url has \"CarSearch\" or url has \"CarBooking\" or url has \"BackgroundCarSearch\"\n"
                 + "         or url has \"CarLocationSearch\" or url has \"searchlocations\" or url has \"/cars/\"";
        }
        return "    | where url has \"search.json\" or url has \"searchrooms\" or url has \"book.json\"\n"
             + "         or url has \"CompletePurchase\" or url has \"logincheck\"";
    }

    public static string ClubsDiscoveryKql(string win, string domain = "hotels")
    {
        var d = domain.ToLowerInvariant();
        if (d != "hotels" && d != "cars") throw new ClubFilterException("discovery domain must be hotels or cars");
        if (!ClubFilter.IsValidCruiseWin(win)) throw new ClubFilterException($"win must be one of: {string.Join(", ", ClubFilter.CruiseAllowedWins)}");

        const string clubExtract =
            "coalesce(tostring(customDimensions.VacationClubID), tostring(customDimensions.VacationClubId), " +
            "tostring(customDimensions.ClubId), tostring(customDimensions.club_id))";
        var reqName = d == "cars" ? "carReq" : "hotelReq";

        return string.Join('\n', new[]
        {
            $"let win = {win};",
            $"let {reqName} =",
            "    requests",
            "    | where timestamp > ago(win)",
            FunnelRequestFilter(d),
            "    | extend Host = tostring(parse_url(url).Host)",
            "    | where isnotempty(Host)",
            "    | project operation_Id, Host;",
            "let clubByOp =",
            "    union",
            $"      (customEvents | where timestamp > ago(win) | extend Club = {clubExtract} | project operation_Id, Club),",
            $"      (traces | where timestamp > ago(win) | extend Club = {clubExtract} | project operation_Id, Club)",
            "    | where isnotempty(Club) and Club != \"0\" and Club != \"-1\"",
            "    | summarize Club = take_any(Club) by operation_Id;",
            reqName,
            "| join kind=leftouter clubByOp on operation_Id",
            "| where isnotempty(Club)",
            "| summarize Requests = count() by Host, Club",
            "| summarize arg_max(Requests, Club), TotalRequests = sum(Requests) by Host",
            "| project Club, Host, Events = TotalRequests",
            "| order by Events desc",
            "| take 50",
        });
    }

    public static string AirClubsDiscoveryKql(string win)
    {
        if (!ClubFilter.IsValidWin(win) && !ClubFilter.IsValidCruiseWin(win))
        {
            throw new ClubFilterException("win must look like 2h, 15m or 7d");
        }
        return string.Join('\n', new[]
        {
            $"let win = {win};",
            "union isfuzzy=true customEvents, pageViews",
            "| where timestamp > ago(win)",
            "| extend ClubId = coalesce(tostring(customDimensions.club_id), tostring(customDimensions.ClubId), tostring(customDimensions.clubId)),",
            "         PartnerId = coalesce(tostring(customDimensions.partnerId), tostring(customDimensions.PartnerId), tostring(customDimensions.partner_id))",
            "| extend Club = coalesce(ClubId, PartnerId)",
            "| where isnotempty(Club) and Club != \"0\" and Club != \"-\"",
            "| summarize Events = count() by Club",
            "| project Club, Host = \"\", Events",
            "| order by Events desc",
            "| take 50",
        });
    }
}
