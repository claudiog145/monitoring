namespace DemoGenerator.Archetypes;

// The shape of packs/<companyKey>/archetype.json (contract v5 §4.2). Read with JsonSerializerDefaults.Web
// (camelCase). Classes with init setters rather than positional records so a missing member is a null the
// validator can name, not a deserialisation exception.

public sealed class Archetype
{
    public string? CompanyKey { get; init; }
    public string? ProfileKey { get; init; }
    public string? DisplayName { get; init; }
    public List<ArchetypeApp>? Apps { get; init; }
    public EntityModel? Entities { get; init; }
    public TrafficModel? Traffic { get; init; }
    public List<StepModel>? Steps { get; init; }
    public List<ScenarioModel>? Scenarios { get; init; }
    public List<HistoricalIncidentModel>? HistoricalIncidents { get; init; }
}

public sealed class ArchetypeApp
{
    public string? AppId { get; init; }
    public string? Database { get; init; }
    public string? RoleName { get; init; }
    public string? Host { get; init; }
}

public sealed class EntityModel
{
    public string? Kind { get; init; }

    /// <summary>customDimensions key that carries the entity id on every row.</summary>
    public string? Dimension { get; init; }

    public int Count { get; init; }

    /// <summary>Zipf exponent; 0 means uniform.</summary>
    public double Zipf { get; init; }

    /// <summary>.NET composite format applied to the 1-based entity number, e.g. <c>B-{0:000}</c>.</summary>
    public string? IdFormat { get; init; }

    /// <summary>Optional: <c>{0}</c> is replaced by the entity id to build the request host.</summary>
    public string? HostTemplate { get; init; }

    /// <summary>
    /// Optional extension: explicit entity ids (e.g. site codes MLA, MLB...). When present it replaces
    /// <see cref="IdFormat"/>; <see cref="Count"/> must be 0 or equal to its length.
    /// </summary>
    public List<string>? Ids { get; init; }
}

public sealed class TrafficModel
{
    /// <summary>Requests per weekday for the whole company at scale <c>laptop</c>.</summary>
    public double RequestsPerDay { get; init; }

    /// <summary>24 relative weights, UTC hour 0..23.</summary>
    public List<double>? HourlyCurve { get; init; }

    public double WeekendFactor { get; init; } = 1.0;
}

public sealed class StepModel
{
    /// <summary>appId of the archetype app that serves this step.</summary>
    public string? App { get; init; }

    /// <summary>Operation name (<c>requests.name</c>), unique in the archetype; scenarios reference it.</summary>
    public string? Name { get; init; }

    /// <summary>Path (and optional query) appended to the host.</summary>
    public string? Url { get; init; }

    public double Weight { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double ErrorRate { get; init; }
    public List<ExceptionModel>? Exceptions { get; init; }
    public List<DependencyModel>? Dependencies { get; init; }
    public BusinessEventModel? BusinessEvent { get; init; }
}

public sealed class ExceptionModel
{
    public string? Type { get; init; }
    public string? Message { get; init; }
    public string? Method { get; init; }
    public double Weight { get; init; } = 1.0;
}

public sealed class DependencyModel
{
    public string? Partner { get; init; }
    public string? Target { get; init; }
    public string? Type { get; init; }
    public string? Name { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double FailureRate { get; init; }
}

public sealed class BusinessEventModel
{
    public string? Name { get; init; }
    public bool OnSuccessOnly { get; init; } = true;
    public Dictionary<string, RangeModel>? Measurements { get; init; }
}

public sealed class RangeModel
{
    public double Min { get; init; }
    public double Max { get; init; }
}

public sealed class ScenarioModel
{
    public string? Key { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int DefaultDurationMinutes { get; init; }
    public List<EffectModel>? Effects { get; init; }
}

/// <summary>One effect of a scenario. Which members apply depends on <see cref="Kind"/> (see <see cref="EffectKinds"/>).</summary>
public sealed class EffectModel
{
    public string? Kind { get; init; }

    /// <summary>Step name; null means every step.</summary>
    public string? Step { get; init; }

    public string? Partner { get; init; }
    public double? Factor { get; init; }
    public double? Rate { get; init; }
    public string? ExceptionType { get; init; }
    public List<string>? Entities { get; init; }
}

public static class EffectKinds
{
    /// <summary>Multiplies request latency (<c>step</c> optional) or, with <c>partner</c>, the dependency latency of that partner and the requests that call it.</summary>
    public const string LatencyMultiplier = "latency-multiplier";

    /// <summary>Extra request failure probability <c>rate</c> (<c>step</c> optional), optionally raising <c>exceptionType</c>.</summary>
    public const string ErrorRate = "error-rate";

    /// <summary>Extra failure probability <c>rate</c> of every dependency call to <c>partner</c>; a failed call fails its request.</summary>
    public const string DependencyFailure = "dependency-failure";

    /// <summary>Restricts every other effect of the scenario to these entity ids.</summary>
    public const string EntityOnly = "entity-only";

    /// <summary>Multiplies traffic by <c>factor</c>. Scoped by <c>step</c> or entity-only it can only reduce (factor &lt;= 1).</summary>
    public const string TrafficMultiplier = "traffic-multiplier";

    public static readonly IReadOnlyList<string> All = [LatencyMultiplier, ErrorRate, DependencyFailure, EntityOnly, TrafficMultiplier];
}

public sealed class HistoricalIncidentModel
{
    public string? Scenario { get; init; }
    public double HoursAgo { get; init; }
    public int DurationMinutes { get; init; }
}
