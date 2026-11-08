using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DemoGenerator.Archetypes;

public sealed class ArchetypeValidationException(string source, IReadOnlyList<string> errors)
    : Exception($"{source} is not a valid archetype: " + string.Join(" | ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Reads and validates <c>archetype.json</c>. Every rule names the JSON path and says what to fix.</summary>
public static partial class ArchetypeLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Bit width of the per-request dependency failure mask in the synthesis.</summary>
    public const int MaxDependenciesPerStep = 8;

    public const int MaxEntities = 10_000;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex CompanyKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ProfileKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,260}$")]
    private static partial Regex DatabasePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ScenarioKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9.-]{1,253}$")]
    private static partial Regex HostPattern();

    /// <summary>Parses and validates; throws <see cref="ArchetypeValidationException"/> with every problem found.</summary>
    public static Archetype Load(string path)
    {
        var (archetype, errors) = TryLoad(path);
        if (errors.Count > 0) throw new ArchetypeValidationException(path, errors);
        return archetype!;
    }

    public static (Archetype? Archetype, IReadOnlyList<string> Errors) TryLoad(string path)
    {
        if (!File.Exists(path)) return (null, [$"{path} does not exist."]);
        return TryParse(File.ReadAllText(path));
    }

    public static (Archetype? Archetype, IReadOnlyList<string> Errors) TryParse(string json)
    {
        Archetype? archetype;
        try
        {
            archetype = JsonSerializer.Deserialize<Archetype>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, [$"archetype is not valid JSON: {ex.Message}"]);
        }

        if (archetype is null) return (null, ["archetype is empty (null)."]);
        var errors = Validate(archetype);
        return (errors.Count == 0 ? archetype : null, errors);
    }

    /// <summary>The 0-based ordered entity ids of the archetype (empty when the entity model is invalid).</summary>
    public static IReadOnlyList<string> EntityIds(EntityModel? entities)
    {
        if (entities is null) return [];
        if (entities.Ids is { Count: > 0 } ids) return ids;
        if (entities.Count < 1 || entities.Count > MaxEntities || string.IsNullOrWhiteSpace(entities.IdFormat)) return [];
        var result = new string[entities.Count];
        try
        {
            for (var i = 0; i < result.Length; i++) result[i] = string.Format(CultureInfo.InvariantCulture, entities.IdFormat, i + 1);
        }
        catch (FormatException)
        {
            return [];
        }
        return result;
    }

    public static IReadOnlyList<string> Validate(Archetype a)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(a.CompanyKey) || !CompanyKeyPattern().IsMatch(a.CompanyKey))
            errors.Add($"$.companyKey '{a.CompanyKey}' must be 1-32 lowercase letters, digits or '-' (e.g. \"bank\").");
        if (string.IsNullOrWhiteSpace(a.ProfileKey) || !ProfileKeyPattern().IsMatch(a.ProfileKey))
            errors.Add($"$.profileKey '{a.ProfileKey}' must be 1-64 letters, digits, '.', '-' or '_' (the CompanyProfile key).");
        if (string.IsNullOrWhiteSpace(a.DisplayName))
            errors.Add("$.displayName is required.");
        else if (!a.DisplayName.TrimEnd().EndsWith("(demo)", StringComparison.Ordinal))
            errors.Add($"$.displayName '{a.DisplayName}' must end with \"(demo)\": every demo company is labelled as synthetic.");

        var appIds = ValidateApps(a, errors);
        var entityIds = ValidateEntities(a.Entities, errors);
        ValidateTraffic(a.Traffic, errors);
        var (stepNames, partners) = ValidateSteps(a, appIds, errors);
        foreach (var app in (a.Apps ?? []).Where(x => x?.AppId is not null))
            if (!(a.Steps ?? []).Any(s => string.Equals(s?.App, app.AppId, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"$.apps '{app.AppId}' has no step in $.steps, so its database would stay empty; add a step or remove the app.");
        var scenarioKeys = ValidateScenarios(a, stepNames, partners, entityIds, errors);

        var incidents = a.HistoricalIncidents ?? [];
        for (var i = 0; i < incidents.Count; i++)
        {
            var incident = incidents[i];
            var path = $"$.historicalIncidents[{i}]";
            if (incident is null) { errors.Add($"{path} is null; remove it."); continue; }
            if (string.IsNullOrWhiteSpace(incident.Scenario) || !scenarioKeys.Contains(incident.Scenario))
                errors.Add($"{path}.scenario '{incident.Scenario}' is not the key of a scenario of this archetype.");
            if (!(incident.HoursAgo > 0) || double.IsInfinity(incident.HoursAgo))
                errors.Add($"{path}.hoursAgo must be > 0 (how long before the reset the incident started).");
            if (incident.DurationMinutes < 1)
                errors.Add($"{path}.durationMinutes must be >= 1.");
        }

        return errors;
    }

    private static HashSet<string> ValidateApps(Archetype a, List<string> errors)
    {
        var appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (a.Apps is null || a.Apps.Count == 0)
        {
            errors.Add("$.apps must list at least one application.");
            return appIds;
        }

        for (var i = 0; i < a.Apps.Count; i++)
        {
            var app = a.Apps[i];
            var path = $"$.apps[{i}]";
            if (app is null) { errors.Add($"{path} is null; remove it."); continue; }

            if (string.IsNullOrWhiteSpace(app.AppId))
                errors.Add($"{path}.appId is required.");
            else if (!appIds.Add(app.AppId))
                errors.Add($"{path}.appId '{app.AppId}' is repeated; every app needs its own appId.");
            else if (a.CompanyKey is { Length: > 0 } ck && !app.AppId.StartsWith($"demo-{ck}-", StringComparison.Ordinal))
                errors.Add($"{path}.appId '{app.AppId}' must start with 'demo-{ck}-' (contract §6 prefixes).");

            if (string.IsNullOrWhiteSpace(app.Database) || !DatabasePattern().IsMatch(app.Database) || app.Database.StartsWith('_'))
                errors.Add($"{path}.database '{app.Database}' must be 1-260 letters, digits, '.', '-' or '_' and must not start with '_'.");
            else if (!databases.Add(app.Database))
                errors.Add($"{path}.database '{app.Database}' is repeated; every app has its own database.");
            else if (a.CompanyKey is { Length: > 0 } ck2 && !app.Database.StartsWith($"{ck2}-", StringComparison.Ordinal))
                errors.Add($"{path}.database '{app.Database}' must start with '{ck2}-' (contract §6 prefixes).");

            if (string.IsNullOrWhiteSpace(app.RoleName)) errors.Add($"{path}.roleName is required (cloud_RoleName).");
            if (string.IsNullOrWhiteSpace(app.Host) || !HostPattern().IsMatch(app.Host))
                errors.Add($"{path}.host '{app.Host}' must be a host name such as web.example.demo.");
        }
        return appIds;
    }

    private static IReadOnlyList<string> ValidateEntities(EntityModel? e, List<string> errors)
    {
        if (e is null)
        {
            errors.Add("$.entities is required.");
            return [];
        }

        if (string.IsNullOrWhiteSpace(e.Kind)) errors.Add("$.entities.kind is required (e.g. \"branch\").");
        if (string.IsNullOrWhiteSpace(e.Dimension)) errors.Add("$.entities.dimension is required (the customDimensions key).");
        if (e.Zipf < 0 || double.IsNaN(e.Zipf) || e.Zipf > 5) errors.Add("$.entities.zipf must be between 0 (uniform) and 5.");

        if (e.Ids is { Count: > 0 } ids)
        {
            if (e.Count != 0 && e.Count != ids.Count) errors.Add($"$.entities.count {e.Count} must be 0 or equal to the {ids.Count} ids listed.");
            if (ids.Count > MaxEntities) errors.Add($"$.entities.ids lists more than {MaxEntities} ids.");
            if (ids.Any(string.IsNullOrWhiteSpace)) errors.Add("$.entities.ids must not contain blank ids.");
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) errors.Add("$.entities.ids must be unique.");
        }
        else
        {
            if (e.Count < 1 || e.Count > MaxEntities) errors.Add($"$.entities.count must be 1..{MaxEntities}.");
            if (string.IsNullOrWhiteSpace(e.IdFormat))
            {
                errors.Add("$.entities.idFormat is required (e.g. \"B-{0:000}\") unless $.entities.ids is given.");
            }
            else
            {
                try
                {
                    var first = string.Format(CultureInfo.InvariantCulture, e.IdFormat, 1);
                    var second = string.Format(CultureInfo.InvariantCulture, e.IdFormat, 2);
                    if (first == second) errors.Add($"$.entities.idFormat '{e.IdFormat}' must use {{0}} so every entity gets a different id.");
                }
                catch (FormatException)
                {
                    errors.Add($"$.entities.idFormat '{e.IdFormat}' is not a valid .NET format string (use {{0}} or {{0:000}}).");
                }
            }
        }

        if (e.HostTemplate is not null && !e.HostTemplate.Contains("{0}", StringComparison.Ordinal))
            errors.Add("$.entities.hostTemplate must contain {0} (the entity id) or be null.");

        var built = EntityIds(e);
        if (e.HostTemplate is not null && built.Count > 0)
        {
            var sample = e.HostTemplate.Replace("{0}", built[0], StringComparison.Ordinal);
            if (!HostPattern().IsMatch(sample)) errors.Add($"$.entities.hostTemplate produces '{sample}', which is not a host name.");
        }
        return built;
    }

    private static void ValidateTraffic(TrafficModel? t, List<string> errors)
    {
        if (t is null)
        {
            errors.Add("$.traffic is required.");
            return;
        }

        if (!(t.RequestsPerDay > 0) || double.IsInfinity(t.RequestsPerDay)) errors.Add("$.traffic.requestsPerDay must be > 0.");
        if (t.HourlyCurve is null || t.HourlyCurve.Count != 24)
            errors.Add("$.traffic.hourlyCurve must have exactly 24 relative weights (UTC hours 0..23).");
        else if (t.HourlyCurve.Any(w => w < 0 || double.IsNaN(w) || double.IsInfinity(w)) || !(t.HourlyCurve.Sum() > 0))
            errors.Add("$.traffic.hourlyCurve weights must be >= 0 and not all 0.");
        if (t.WeekendFactor < 0 || double.IsNaN(t.WeekendFactor) || double.IsInfinity(t.WeekendFactor))
            errors.Add("$.traffic.weekendFactor must be >= 0.");
    }

    private static (HashSet<string> StepNames, HashSet<string> Partners) ValidateSteps(Archetype a, HashSet<string> appIds, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var partners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (a.Steps is null || a.Steps.Count == 0)
        {
            errors.Add("$.steps must list at least one step.");
            return (names, partners);
        }

        for (var i = 0; i < a.Steps.Count; i++)
        {
            var s = a.Steps[i];
            var path = $"$.steps[{i}]";
            if (s is null) { errors.Add($"{path} is null; remove it."); continue; }

            if (string.IsNullOrWhiteSpace(s.App) || !appIds.Contains(s.App))
                errors.Add($"{path}.app '{s.App}' is not the appId of an app in $.apps.");
            if (string.IsNullOrWhiteSpace(s.Name))
                errors.Add($"{path}.name is required (the operation name, e.g. \"POST /api/transfers\").");
            else if (!names.Add(s.Name))
                errors.Add($"{path}.name '{s.Name}' is repeated; scenarios reference steps by name, so names must be unique.");
            if (string.IsNullOrWhiteSpace(s.Url) || !s.Url.StartsWith('/'))
                errors.Add($"{path}.url '{s.Url}' must be a path starting with '/'.");
            if (!(s.Weight > 0) || double.IsInfinity(s.Weight)) errors.Add($"{path}.weight must be > 0.");
            if (!(s.P50Ms > 0)) errors.Add($"{path}.p50Ms must be > 0.");
            if (!(s.P95Ms > s.P50Ms)) errors.Add($"{path}.p95Ms must be greater than p50Ms.");
            if (s.ErrorRate is < 0 or > 1 || double.IsNaN(s.ErrorRate)) errors.Add($"{path}.errorRate must be 0..1.");

            var exceptions = s.Exceptions ?? [];
            for (var x = 0; x < exceptions.Count; x++)
            {
                var ex = exceptions[x];
                var xp = $"{path}.exceptions[{x}]";
                if (ex is null) { errors.Add($"{xp} is null; remove it."); continue; }
                if (string.IsNullOrWhiteSpace(ex.Type)) errors.Add($"{xp}.type is required (e.g. System.TimeoutException).");
                if (string.IsNullOrWhiteSpace(ex.Message)) errors.Add($"{xp}.message is required.");
                if (!(ex.Weight > 0)) errors.Add($"{xp}.weight must be > 0.");
            }

            var deps = s.Dependencies ?? [];
            if (deps.Count > MaxDependenciesPerStep) errors.Add($"{path}.dependencies has {deps.Count} entries; at most {MaxDependenciesPerStep} per step.");
            for (var d = 0; d < deps.Count; d++)
            {
                var dep = deps[d];
                var dp = $"{path}.dependencies[{d}]";
                if (dep is null) { errors.Add($"{dp} is null; remove it."); continue; }
                if (string.IsNullOrWhiteSpace(dep.Partner)) errors.Add($"{dp}.partner is required (a partner key of the profile).");
                else partners.Add(dep.Partner);
                if (string.IsNullOrWhiteSpace(dep.Target)) errors.Add($"{dp}.target is required.");
                if (string.IsNullOrWhiteSpace(dep.Type)) errors.Add($"{dp}.type is required (e.g. HTTP, SQL).");
                if (string.IsNullOrWhiteSpace(dep.Name)) errors.Add($"{dp}.name is required.");
                if (!(dep.P50Ms > 0)) errors.Add($"{dp}.p50Ms must be > 0.");
                if (!(dep.P95Ms > dep.P50Ms)) errors.Add($"{dp}.p95Ms must be greater than p50Ms.");
                if (dep.FailureRate is < 0 or > 1 || double.IsNaN(dep.FailureRate)) errors.Add($"{dp}.failureRate must be 0..1.");
            }

            if (s.BusinessEvent is { } be)
            {
                if (string.IsNullOrWhiteSpace(be.Name)) errors.Add($"{path}.businessEvent.name is required.");
                foreach (var (key, range) in be.Measurements ?? [])
                {
                    if (string.IsNullOrWhiteSpace(key)) errors.Add($"{path}.businessEvent.measurements has a blank name.");
                    if (range is null || range.Min > range.Max || double.IsNaN(range.Min) || double.IsNaN(range.Max))
                        errors.Add($"{path}.businessEvent.measurements.{key} needs min <= max.");
                }
            }
        }
        return (names, partners);
    }

    private static HashSet<string> ValidateScenarios(Archetype a, HashSet<string> stepNames, HashSet<string> partners,
        IReadOnlyList<string> entityIds, List<string> errors)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var scenarios = a.Scenarios ?? [];
        var knownEntities = new HashSet<string>(entityIds, StringComparer.Ordinal);
        for (var i = 0; i < scenarios.Count; i++)
        {
            var sc = scenarios[i];
            var path = $"$.scenarios[{i}]";
            if (sc is null) { errors.Add($"{path} is null; remove it."); continue; }
            if (string.IsNullOrWhiteSpace(sc.Key) || !ScenarioKeyPattern().IsMatch(sc.Key))
                errors.Add($"{path}.key '{sc.Key}' must be 1-64 lowercase letters, digits or '-'.");
            else if (!keys.Add(sc.Key))
                errors.Add($"{path}.key '{sc.Key}' is repeated.");
            if (string.IsNullOrWhiteSpace(sc.Title)) errors.Add($"{path}.title is required.");
            if (sc.DefaultDurationMinutes < 1) errors.Add($"{path}.defaultDurationMinutes must be >= 1.");
            if (sc.Effects is null || sc.Effects.Count == 0)
            {
                errors.Add($"{path}.effects must list at least one effect.");
                continue;
            }

            var scoped = sc.Effects.Any(e => e is not null && e.Kind == EffectKinds.EntityOnly);
            if (sc.Effects.Count(e => e is not null && e.Kind == EffectKinds.EntityOnly) > 1)
                errors.Add($"{path}.effects has more than one entity-only effect; list every entity in one.");
            if (sc.Effects.All(e => e is null || e.Kind == EffectKinds.EntityOnly))
                errors.Add($"{path}.effects only restricts entities; add the effect to apply to them.");

            for (var f = 0; f < sc.Effects.Count; f++)
            {
                var e = sc.Effects[f];
                var ep = $"{path}.effects[{f}]";
                if (e is null) { errors.Add($"{ep} is null; remove it."); continue; }
                if (e.Step is not null && !stepNames.Contains(e.Step))
                    errors.Add($"{ep}.step '{e.Step}' is not the name of a step in $.steps.");
                switch (e.Kind)
                {
                    case EffectKinds.LatencyMultiplier:
                        if (!(e.Factor > 0)) errors.Add($"{ep}.factor must be > 0.");
                        if (e.Partner is not null && !partners.Contains(e.Partner))
                            errors.Add($"{ep}.partner '{e.Partner}' is not the partner of any dependency in $.steps.");
                        if (e.Partner is not null && e.Step is not null)
                            errors.Add($"{ep} sets both step and partner; use one of them.");
                        break;
                    case EffectKinds.ErrorRate:
                        if (e.Rate is not (>= 0 and <= 1)) errors.Add($"{ep}.rate must be 0..1.");
                        break;
                    case EffectKinds.DependencyFailure:
                        if (e.Rate is not (>= 0 and <= 1)) errors.Add($"{ep}.rate must be 0..1.");
                        if (string.IsNullOrWhiteSpace(e.Partner) || !partners.Contains(e.Partner))
                            errors.Add($"{ep}.partner '{e.Partner}' is not the partner of any dependency in $.steps.");
                        break;
                    case EffectKinds.EntityOnly:
                        if (e.Entities is null || e.Entities.Count == 0)
                            errors.Add($"{ep}.entities must list at least one entity id.");
                        else
                            foreach (var id in e.Entities.Where(id => !knownEntities.Contains(id ?? "")))
                                errors.Add($"{ep}.entities contains '{id}', which is not one of the generated entity ids (e.g. '{entityIds.FirstOrDefault()}').");
                        break;
                    case EffectKinds.TrafficMultiplier:
                        if (!(e.Factor > 0)) errors.Add($"{ep}.factor must be > 0.");
                        else if ((scoped || e.Step is not null) && e.Factor > 1)
                            errors.Add($"{ep}: a traffic-multiplier limited by step or entity-only can only reduce traffic (factor <= 1); use an unscoped multiplier for a peak.");
                        break;
                    default:
                        errors.Add($"{ep}.kind '{e.Kind}' is unknown. Use one of: {string.Join(", ", EffectKinds.All)}.");
                        break;
                }
            }
        }
        return keys;
    }
}
