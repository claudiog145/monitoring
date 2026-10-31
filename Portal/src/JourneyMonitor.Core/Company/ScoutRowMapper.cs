using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Company;

/// <summary>
/// What one profile said about one board row.
///
/// Every field of <see cref="EntityIdentity"/> that says HOW the answer was reached travels in
/// here - <see cref="EntityResolvedBy"/> and <see cref="EntitySourceKey"/> - because an identity
/// found by the ninth fallback is not worth the same as one the application stamped explicitly,
/// and the package is read by somebody who was not present when the row was classified.
///
/// <see cref="ProfileKey"/> is on the record rather than only in a log line: when a mapping turns
/// out to be wrong, the first question is which profile produced it, and a package that cannot
/// answer that is not attributable.
/// </summary>
public sealed record ScoutRowClassification(
    string ProfileKey,
    string? Service,
    string ServiceLabel,
    string? Flow,
    string? Step,
    string StepLabel,
    bool StepIsResolved,
    string StepResolvedBy,
    string EntityKey,
    string? EntityValue,
    string? EntityKind,
    string? EntitySourceKey,
    bool EntityIsResolved,
    string EntityResolvedBy)
{
    /// <summary>The profile describes no service for this row. THEIR mapping gap.</summary>
    public const string UnresolvedServiceLabel = "[service unresolved]";

    /// <summary>The classifier itself failed. OUR outage - never collapsed into "unresolved", see <see cref="CompanyResolution"/>.</summary>
    public const string UnavailableServiceLabel = "[service classifier unavailable]";

    public const string UnresolvedStepLabel = "[step unresolved]";

    public const string UnavailableStepLabel = "[step classifier unavailable]";

    /// <summary>
    /// Kept byte-identical to the sentinel the boards already emit, so an unidentifiable row still
    /// splits into a kind and a value for <c>ClubIdentityResolver</c> exactly as it does today.
    /// </summary>
    public const string UnresolvedEntityKey = "[host/club unavailable]";

    public const string UnavailableEntityKey = "[entity classifier unavailable]";

    /// <summary>Used when a profile names no kind for an identity source; the key shape must stay "{kind}:{value}".</summary>
    private const string DefaultEntityKind = "entity";

    internal static ScoutRowClassification From(ICompanyAdapter adapter, StepMatch step, EntityIdentity identity)
    {
        var service = adapter.Services.FirstOrDefault(
            s => string.Equals(s.Key, step.Service, StringComparison.OrdinalIgnoreCase));

        return new ScoutRowClassification(
            ProfileKey: adapter.ProfileKey,
            Service: step.Service,
            ServiceLabel: LabelFor(
                step.Service is { Length: > 0 } ? service?.DisplayName ?? step.Service : null,
                step.ResolvedBy, UnresolvedServiceLabel, UnavailableServiceLabel),
            Flow: step.Flow,
            Step: step.Step,
            StepLabel: LabelFor(
                step.IsResolved && step.Step is { Length: > 0 } ? StepDisplayName(service, step) : null,
                step.ResolvedBy, UnresolvedStepLabel, UnavailableStepLabel),
            StepIsResolved: step.IsResolved,
            StepResolvedBy: step.ResolvedBy,
            EntityKey: EntityKeyFor(identity),
            EntityValue: identity.IsResolved ? identity.Value : null,
            EntityKind: identity.Kind,
            EntitySourceKey: identity.SourceKey,
            EntityIsResolved: identity.IsResolved,
            EntityResolvedBy: identity.ResolvedBy);
    }

    private static string LabelFor(string? resolved, string resolvedBy, string unresolved, string unavailable) =>
        resolved is { Length: > 0 }
            ? resolved
            : resolvedBy == CompanyResolution.Unavailable ? unavailable : unresolved;

    /// <summary>A fallback step carries no flow and its key is already a label, so the lookup simply misses and the key stands.</summary>
    private static string StepDisplayName(ServiceDefinition? service, StepMatch step)
    {
        var declared = service?.Flows
            .Where(f => step.Flow is null || string.Equals(f.Key, step.Flow, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Steps)
            .FirstOrDefault(s => string.Equals(s.Key, step.Step, StringComparison.OrdinalIgnoreCase));

        return declared?.DisplayName is { Length: > 0 } name ? name : step.Step!;
    }

    private static string EntityKeyFor(EntityIdentity identity)
    {
        if (!identity.IsResolved || identity.Value is not { Length: > 0 })
        {
            return identity.ResolvedBy == CompanyResolution.Unavailable ? UnavailableEntityKey : UnresolvedEntityKey;
        }

        var kind = identity.Kind is { Length: > 0 } k ? k.Trim().ToLowerInvariant() : DefaultEntityKind;
        return $"{kind}:{identity.Value.Trim()}";
    }
}

/// <summary>
/// Turns one row of a scout board back into the <see cref="TelemetryRowContext"/> a company
/// profile is able to reason about, so the vocabulary of a package comes from the profile instead
/// of from whatever the query text happened to write into its Vertical/Stage columns.
///
/// A board row is an AGGREGATE, not a request, so this is a reconstruction and it is only as good
/// as what the board projected. Nothing here knows a column name that belongs to one company: the
/// generic telemetry shapes (url, operation, page name, role name) are read by their generic
/// names, and everything else the board projected is offered to the profile as a custom dimension
/// under the name the board used - which is what lets a profile-composed board hand its identity
/// dimensions straight through.
///
/// The one liberty taken is the host: when the board projected no url and no host column, the host
/// is recovered from the row's own "host:..." entity key. That is a measurement the board made,
/// not a name it chose, and without it every host filter and every host identity source would miss
/// on today's boards. No such liberty is taken with an identifier: a "club:113" entity key is NOT
/// fed back as a dimension, because the source that produced it is unknown and a
/// <see cref="EntityIdentity.SourceKey"/> that had to be guessed is worse than an honest
/// unresolved.
/// </summary>
public static class ScoutRowMapper
{
    private static readonly string[] UrlColumns = ["Url", "RequestUrl"];
    private static readonly string[] HostColumns = ["Host"];
    private static readonly string[] PathColumns = ["Path", "RequestPath"];
    private static readonly string[] OperationColumns = ["Operation", "OperationName", "Name"];
    private static readonly string[] PageNameColumns = ["PageName"];
    private static readonly string[] RoleNameColumns = ["RoleName", "cloud_RoleName", "Component"];
    private static readonly string[] DimensionBagColumns = ["CustomDimensions", "Dimensions"];

    /// <summary>Where the board said the row came from. Narrows which services may claim it.</summary>
    private static readonly string[] SourceColumns = ["Source", "SourceAlias", "TelemetrySource"];
    private static readonly string[] PathArrayColumns = ["SlowPaths", "Paths"];
    private static readonly string[] OperationArrayColumns = ["Operations"];

    /// <summary>Containers and encodings, not values - offering them as dimensions would be noise at best.</summary>
    private static readonly HashSet<string> NeverADimension =
        new(["GroupPayload", "CustomDimensions", "Dimensions"], StringComparer.OrdinalIgnoreCase);

    public static TelemetryRowContext ToRowContext(ScoutBoardRow row) =>
        row is null
            ? new TelemetryRowContext()
            : ToRowContext(row.RawRowJson, row.GroupPayloadJson, row.EntityKey);

    public static TelemetryRowContext ToRowContext(string? rawRowJson, string? groupPayloadJson, string? boardEntityKey = null)
    {
        using var rawDocument = TryParse(rawRowJson);
        using var payloadDocument = TryParse(groupPayloadJson);

        var raw = rawDocument?.RootElement ?? default;
        var payload = payloadDocument?.RootElement ?? default;

        var rawScalars = Scalars(raw);
        var payloadScalars = Scalars(payload);

        var dimensions = new Dictionary<string, string>(rawScalars, StringComparer.OrdinalIgnoreCase);
        Overlay(dimensions, Bag(payload));
        Overlay(dimensions, Bag(raw));

        return new TelemetryRowContext(
            Url: Url(rawScalars, payloadScalars, payload, boardEntityKey),
            Operation: First(rawScalars, payloadScalars, OperationColumns)
                ?? FirstArrayValue(payload, OperationArrayColumns),
            PageName: First(rawScalars, payloadScalars, PageNameColumns),
            RoleName: First(rawScalars, payloadScalars, RoleNameColumns),
            CustomDimensions: dimensions.Count > 0 ? dimensions : null,
            SourceAlias: First(rawScalars, payloadScalars, SourceColumns));
    }

    /// <summary>
    /// Both answers a profile can give about a row, in one call, so the board reader and the cycle
    /// that packages the row cannot drift apart by asking differently. Pure: the adapter never
    /// touches the database here, which is what makes it affordable per row.
    /// </summary>
    public static ScoutRowClassification Classify(ICompanyAdapter adapter, TelemetryRowContext row)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var context = row ?? new TelemetryRowContext();
        return ScoutRowClassification.From(adapter, adapter.ResolveStep(context), adapter.ResolveAffectedEntity(context));
    }

    public static ScoutRowClassification Classify(ICompanyAdapter adapter, ScoutBoardRow row) =>
        Classify(adapter, ToRowContext(row));

    /// <summary>
    /// A real url when the board projected one; otherwise the best reconstruction available, since
    /// a url-shaped matcher that silently never fires would read as "this company has no traffic".
    /// The query string cannot be reconstructed from an aggregate, so a query-parameter identity
    /// source only answers when the board projected the url itself - reported honestly as
    /// unresolved rather than approximated.
    /// </summary>
    private static string? Url(
        IReadOnlyDictionary<string, string> rawScalars,
        IReadOnlyDictionary<string, string> payloadScalars,
        JsonElement payload,
        string? boardEntityKey)
    {
        if (First(rawScalars, payloadScalars, UrlColumns) is { Length: > 0 } url) return url;

        var host = First(rawScalars, payloadScalars, HostColumns) ?? HostOf(boardEntityKey);
        var path = First(rawScalars, payloadScalars, PathColumns) ?? FirstArrayValue(payload, PathArrayColumns);

        if (host is not { Length: > 0 }) return path;

        if (path is not { Length: > 0 }) return $"https://{host}/";

        return path.StartsWith('/') ? $"https://{host}{path}" : $"https://{host}/{path}";
    }

    /// <summary>Reads the host back out of a "host:..." entity key only - any other kind is an identifier, and guessing one is forbidden.</summary>
    private static string? HostOf(string? entityKey)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return null;

        var separator = entityKey.IndexOf(':');
        if (separator <= 0 || separator == entityKey.Length - 1) return null;

        if (!string.Equals(entityKey[..separator].Trim(), ClubEntityKind.Host, StringComparison.OrdinalIgnoreCase)) return null;

        var host = entityKey[(separator + 1)..].Trim();
        return host.Length > 0 ? host : null;
    }

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // A row whose payload cannot be read still has to classify as "unresolved" rather than
            // take the cycle down - the board already decided this row is worth looking at.
            return null;
        }
    }

    private static Dictionary<string, string> Scalars(JsonElement element)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object) return values;

        foreach (var property in element.EnumerateObject())
        {
            if (NeverADimension.Contains(property.Name)) continue;

            var text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.ToString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text)) values[property.Name] = text;
        }

        return values;
    }

    /// <summary>The explicit dimension bag a profile-composed board can project, in either of the two names it may use.</summary>
    private static Dictionary<string, string>? Bag(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!DimensionBagColumns.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;

            if (property.Value.ValueKind == JsonValueKind.Object) return Scalars(property.Value);

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                using var nested = TryParse(property.Value.GetString());
                if (nested is not null) return Scalars(nested.RootElement);
            }
        }

        return null;
    }

    private static void Overlay(Dictionary<string, string> target, Dictionary<string, string>? source)
    {
        if (source is null) return;
        foreach (var pair in source) target[pair.Key] = pair.Value;
    }

    private static string? First(
        IReadOnlyDictionary<string, string> rawScalars,
        IReadOnlyDictionary<string, string> payloadScalars,
        string[] names)
    {
        foreach (var name in names)
        {
            if (rawScalars.TryGetValue(name, out var fromRow) && fromRow.Length > 0) return fromRow;
            if (payloadScalars.TryGetValue(name, out var fromPayload) && fromPayload.Length > 0) return fromPayload;
        }

        return null;
    }

    /// <summary>
    /// The first entry of the board's own set columns. A board row covers many requests, so this is
    /// one representative of the group rather than "the" request - which is why the step it
    /// classifies to is the group's step, exactly as the board's own case() produced it.
    /// </summary>
    private static string? FirstArrayValue(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) return text;
                }
            }
        }

        return null;
    }
}
