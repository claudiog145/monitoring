using System.Globalization;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Contract v6 §4.1/§4.2: the KQL behind generic journeys and the entity catalog. Nothing here names
/// a company: <see cref="CompanyKqlTemplater"/> fills the request union over the profile's
/// <c>app("{{SOURCE:alias}}")</c> sources, the host filter, the service → step classifier
/// (first match wins) and the entity identity from <see cref="CompanyProfile.EntityIdentitySources"/>.
/// Only <c>{{SOURCE:alias}}</c> is left for <see cref="IKqlSourceResolver"/>. Every other value is a
/// formatted number, datetime or an escaped literal.
/// </summary>
public static class ProfileJourneyKql
{
    /// <summary>Columns of one row returned by <see cref="BuildJourneyQuery"/>.</summary>
    public static class Columns
    {
        public const string JourneyKey = "JourneyKey";
        public const string Timestamp = "timestamp";
        public const string Name = "name";
        public const string DurationMs = "DurationMs";
        public const string Success = "success";
        public const string ResultCode = "resultCode";
        public const string ServiceKey = "ServiceKey";
        public const string StepKey = "StepKey";
        public const string Entity = "Entity";
        public const string OperationId = "OperationId";
        public const string Requests = "Requests";
        public const string Failures = "Failures";
    }

    /// <summary>
    /// Classified requests of the most recent <paramref name="maxJourneysPerService"/> journeys per
    /// service that began in [from, to), one row per request, ordered by journey then time. A journey is one
    /// <c>session_Id</c> when the request carries one, otherwise one <c>operation_Id</c>; its service
    /// for the cap is the service of its first classified request. Requests are read from
    /// <see cref="SessionLookback"/> before <paramref name="fromInclusive"/> so a session that began
    /// earlier is recognised and left out instead of being returned without its first steps (the run
    /// whose window held its start already wrote it whole).
    /// </summary>
    public static string BuildJourneyQuery(
        CompanyProfile profile,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        int maxJourneysPerService,
        int maxRequests)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (fromInclusive >= toExclusive) throw new ArgumentException("from must be earlier than to.", nameof(fromInclusive));
        if (maxJourneysPerService < 1) throw new ArgumentOutOfRangeException(nameof(maxJourneysPerService));
        if (maxRequests < 1) throw new ArgumentOutOfRangeException(nameof(maxRequests));

        var template = string.Join("\n",
            // historyStart is the bound every source branch of the union is cut at, so it carries the lookback.
            $"let windowStart = {FormatDateTime(fromInclusive)};",
            $"let historyStart = {FormatDateTime(fromInclusive - SessionLookback(profile))};",
            $"let tEnd = {FormatDateTime(toExclusive)};",
            $"let maxPerService = {maxJourneysPerService.ToString(CultureInfo.InvariantCulture)};",
            "let base =",
            "    union",
            KqlPlaceholders.SourceUnion,
            "    | where timestamp >= historyStart and timestamp < tEnd",
            "    | extend Host = tolower(tostring(parse_url(url).Host))",
            $"    | where {KqlPlaceholders.HostFilter}",
            KqlPlaceholders.ServiceAndStepClassifier,
            "    | where isnotempty(ServiceKey) and isnotempty(operation_Id)",
            $"    | extend Entity = tostring({KqlPlaceholders.EntityIdentity})",
            "    | extend JourneyKey = iff(isnotempty(SessionId), SessionId, operation_Id);",
            "let picked =",
            "    base",
            "    | summarize arg_min(timestamp, ServiceKey) by JourneyKey",
            "    | where timestamp >= windowStart",
            "    | order by ServiceKey asc, timestamp desc",
            "    | extend Rank = row_number(1, prev(ServiceKey) != ServiceKey)",
            "    | where Rank <= maxPerService",
            "    | project JourneyKey;",
            "base",
            "| join kind=inner (picked) on JourneyKey",
            "| project JourneyKey, timestamp, name, DurationMs = todouble(duration), success, resultCode = tostring(resultCode),",
            "          ServiceKey, StepKey, Entity, OperationId = operation_Id",
            "| order by JourneyKey asc, timestamp asc",
            $"| take {maxRequests.ToString(CultureInfo.InvariantCulture)}");

        var kql = CompanyKqlTemplater.Render(template, profile);
        KqlPlaceholders.AssertNoneLeft(kql, $"profile journeys [{profile.ProfileKey}]");
        return kql;
    }

    /// <summary>
    /// Top <paramref name="top"/> entities by request volume in [from, to). The entity comes from the
    /// profile's identity sources only; rows without one are not an entity. With a service, only that
    /// service's sources are read and only rows the classifier assigns to it count.
    /// </summary>
    public static string BuildEntityQuery(
        CompanyProfile profile,
        string? serviceKey,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        int top)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (fromInclusive >= toExclusive) throw new ArgumentException("from must be earlier than to.", nameof(fromInclusive));
        if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

        var service = string.IsNullOrWhiteSpace(serviceKey) ? null : profile.FindService(serviceKey)
            ?? throw new InvalidOperationException($"\"{serviceKey}\" is not a service of company profile \"{profile.ProfileKey}\".");

        var lines = new List<string>
        {
            $"let historyStart = {FormatDateTime(fromInclusive)};",
            $"let tEnd = {FormatDateTime(toExclusive)};",
            "union",
            KqlPlaceholders.SourceUnion,
            "| where timestamp >= historyStart and timestamp < tEnd",
            "| extend Host = tolower(tostring(parse_url(url).Host))",
            $"| where {KqlPlaceholders.HostFilter}",
        };
        if (service is not null)
        {
            lines.Add(KqlPlaceholders.ServiceAndStepClassifier);
            lines.Add($"| where ServiceKey == {CompanyKqlTemplater.Literal(service.Key.Trim())}");
        }
        lines.Add($"| extend Entity = tostring({KqlPlaceholders.EntityIdentity})");
        lines.Add("| where isnotempty(Entity)");
        lines.Add("| summarize Requests = sum(itemCount), Failures = sumif(itemCount, success == false) by Entity");
        lines.Add($"| order by Requests desc, Entity asc");
        lines.Add($"| take {top.ToString(CultureInfo.InvariantCulture)}");

        var template = string.Join("\n", lines);
        var kql = service is null
            ? CompanyKqlTemplater.Render(template, profile)
            : CompanyKqlTemplater.RenderForService(template, profile, service.Key);
        KqlPlaceholders.AssertNoneLeft(kql, $"entities [{profile.ProfileKey}]");
        return kql;
    }

    /// <summary>
    /// How far before the window a session that reaches into it can have begun: one minute per step of
    /// the profile's longest flow (a session walks its flow one step per minute), plus one.
    /// </summary>
    public static TimeSpan SessionLookback(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var longest = (profile.Services ?? [])
            .SelectMany(s => s?.Flows ?? [])
            .Select(f => f?.Steps?.Count ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        return TimeSpan.FromMinutes(longest + 1);
    }

    public static string FormatDateTime(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        utc = new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        return "datetime(" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ")";
    }
}
