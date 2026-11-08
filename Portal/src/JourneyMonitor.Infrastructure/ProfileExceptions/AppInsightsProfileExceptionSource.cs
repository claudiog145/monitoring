using System.Globalization;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Incidents;
using JourneyMonitor.Core.UseCases;

namespace JourneyMonitor.Infrastructure.ProfileExceptions;

/// <summary>
/// Contract v6 §4.3: the <c>exceptions</c> of one service of a company, read from the service's own
/// telemetry source aliases. <see cref="CompanyKqlTemplater"/> fills the request/exception source unions
/// (scoped to the service), the profile-wide service/step classifier and the entity identity;
/// <see cref="IKqlSourceResolver"/> turns each alias into a resource id. An exception inherits the
/// service, step and entity of the classified request with the same operation in the same source;
/// one whose request is not in the window, or matched no step, is kept with the exception's own
/// entity and no step. Execution goes through the service's first alias (the KPI rule), and every
/// alias must be readable by the profile.
/// </summary>
public sealed class AppInsightsProfileExceptionSource(
    ITelemetrySource telemetry,
    IKqlSourceResolver sourceResolver,
    TelemetrySourceOwnershipCheck ownership) : IProfileExceptionSource
{
    /// <summary>Upper bound of rows one service returns per run; the next run picks up the rest.</summary>
    public const int MaxRows = 5000;

    private const string WindowToken = "{{window}}";
    private const string ServiceToken = "{{service}}";
    private const string MaxRowsToken = "{{maxRows}}";

    public const string Template = """
        let tEnd = now();
        let historyStart = tEnd - {{window}};
        let serviceKey = {{service}};
        let requestMap = materialize(
            union
        {{SOURCE_UNION}}
            | extend Host = tolower(tostring(parse_url(url).Host))
        {{SERVICE_AND_STEP_CLASSIFIER}}
            | extend Entity = tostring({{ENTITY_IDENTITY}})
            | where isnotempty(operation_Id)
            | extend Ref = strcat(Source, ":", operation_Id)
            | summarize arg_max(timestamp, ServiceKey, StepKey, Step, Entity, url, SessionId) by Ref
            | project Ref, RequestService = ServiceKey, RequestStepKey = StepKey, RequestStep = Step,
                      RequestEntity = Entity, RequestUrl = tostring(url), RequestSession = SessionId);
        union
        {{EXCEPTION_SOURCE_UNION}}
        | where tostring(type) !startswith "ErrorEvent"
        | extend Ref = strcat(Source, ":", tostring(operation_Id))
        | join kind=leftouter requestMap on Ref
        | where isempty(RequestService) or RequestService =~ serviceKey
        | extend url = tostring(RequestUrl), Host = tolower(tostring(parse_url(tostring(RequestUrl)).Host))
        | extend ExceptionEntity = tostring({{ENTITY_IDENTITY}})
        | project OccurredAt = timestamp,
                  ItemId = tostring(itemId),
                  OperationId = tostring(operation_Id),
                  ExceptionType = tostring(type),
                  Operation = tostring(operation_Name),
                  Message = coalesce(tostring(column_ifexists("outerMessage", "")), tostring(column_ifexists("innermostMessage", "")), ""),
                  StackTrace = substring(tostring(column_ifexists("details", "")), 0, 8000),
                  Entity = coalesce(tostring(RequestEntity), ExceptionEntity),
                  StepKey = tostring(RequestStepKey),
                  Step = tostring(RequestStep),
                  Url = tostring(RequestUrl),
                  SessionId = coalesce(tostring(column_ifexists("session_Id", "")), tostring(RequestSession)),
                  MemberId = coalesce(tostring(column_ifexists("user_AuthenticatedId", "")), tostring(column_ifexists("user_Id", ""))),
                  Source = iff(tostring(column_ifexists("client_Type", "")) =~ "Browser", "Browser", "Server")
        | top {{maxRows}} by OccurredAt desc
        """;

    public async Task<ProfileExceptionFetch> FetchAsync(
        CompanyProfile profile,
        ServiceDefinition service,
        TimeSpan window,
        string environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(service);

        var aliases = (service.TelemetrySourceAliases ?? [])
            .Select(a => a?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .ToList();
        if (aliases.Count == 0)
        {
            return ProfileExceptionFetch.Skipped(
                $"No telemetry source configured for service '{service.Key}' of profile '{profile.ProfileKey}'.");
        }

        var foreign = await ownership.FindForeignAsync(profile.ProfileKey, aliases, cancellationToken);
        if (foreign.Count > 0)
        {
            return ProfileExceptionFetch.Skipped(string.Join(" ", foreign.Select(f => f.Message)));
        }

        var catalog = await ownership.GetCatalogAsync(cancellationToken);
        var entry = catalog.FirstOrDefault(s =>
            string.Equals(s.Alias?.Trim(), aliases[0], StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Environment?.Trim(), environment, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.AppId))
        {
            var why = entry is null ? "is not registered" : !entry.Enabled ? "is disabled" : "has no AppId";
            return ProfileExceptionFetch.Skipped(
                $"Telemetry source \"{aliases[0]}\" ({environment}) {why}; exceptions of '{profile.ProfileKey}/{service.Key}' are not read.");
        }

        string kql;
        try
        {
            kql = await sourceResolver.ResolveAsync(Render(profile, service.Key, window), environment, cancellationToken);
        }
        catch (KqlSourceResolutionException ex)
        {
            return ProfileExceptionFetch.Skipped(ex.Message);
        }

        var tenantId = string.IsNullOrWhiteSpace(entry.TenantId) ? null : entry.TenantId.Trim();
        var rows = await telemetry.QueryAsync(new TelemetryQueryRequest(kql, entry.AppId.Trim(), tenantId), cancellationToken);
        return new ProfileExceptionFetch(rows.Select(ToSignal).ToList(), null);
    }

    /// <summary>The query for one service, with every company placeholder filled and only <c>{{SOURCE:alias}}</c> left.</summary>
    public static string Render(CompanyProfile profile, string serviceKey, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var minutes = Math.Max(1, (long)Math.Ceiling(window.TotalMinutes));
        var text = CompanyKqlTemplater.RenderForService(Template, profile, serviceKey)
            .Replace(WindowToken, minutes.ToString(CultureInfo.InvariantCulture) + "m", StringComparison.Ordinal)
            .Replace(ServiceToken, CompanyKqlTemplater.Literal(serviceKey.Trim().ToLowerInvariant()), StringComparison.Ordinal)
            .Replace(MaxRowsToken, MaxRows.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        KqlPlaceholders.AssertNoneLeft(text, $"profile exceptions {profile.ProfileKey}/{serviceKey}");
        return text;
    }

    private static ProfileExceptionSignal ToSignal(TelemetryJourneyRow row) => new(
        new ExceptionSignal(
            row.GetString("Entity"),
            row.GetString("ExceptionType"),
            row.GetString("Step"),
            row.GetString("Operation"),
            row.GetString("Message"),
            NullIfBlank(row.GetString("StackTrace")),
            row.GetString("ItemId"),
            row.GetString("OperationId"),
            row.GetDateTimeOffset("OccurredAt") ?? DateTimeOffset.UtcNow,
            NullIfBlank(row.GetString("MemberId")),
            NullIfBlank(row.GetString("SessionId")),
            NullIfBlank(row.GetString("OperationId")),
            NullIfBlank(row.GetString("Url")),
            NullIfBlank(row.GetString("Url")),
            ExceptionSource.Normalize(row.GetString("Source"))),
        NullIfBlank(row.GetString("StepKey")),
        NullIfBlank(row.GetString("Step")));

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
