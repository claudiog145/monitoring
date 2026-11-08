using DemoGenerator.Archetypes;

namespace DemoGenerator.Synthesis;

/// <summary>
/// One scenario effect resolved against one app: which minutes, which step indexes, which entity ranks and
/// which dependency indexes of THAT app it touches. Null sets mean "all".
/// </summary>
public sealed record AppliedEffect(
    string Kind,
    string ScenarioKey,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<int>? Steps,
    IReadOnlyList<int>? Entities,
    IReadOnlyList<int>? DependencyIndexes,
    double Factor,
    double Rate,
    string? ExceptionType)
{
    /// <summary>Unscoped traffic multiplier: changes the per-minute request count.</summary>
    public bool IsGlobalTraffic => Kind == EffectKinds.TrafficMultiplier && Steps is null && Entities is null;

    /// <summary>Scoped traffic multiplier (factor &lt;= 1): drops matching rows.</summary>
    public bool IsThinning => Kind == EffectKinds.TrafficMultiplier && !IsGlobalTraffic;

    public bool IsActive(DateTimeOffset minute) => minute >= Start && minute < End;

    public bool Matches(DateTimeOffset minute, int step, int entity) =>
        IsActive(minute)
        && (Steps is null || Steps.Contains(step))
        && (Entities is null || Entities.Contains(entity));
}

public static class EffectCompiler
{
    /// <summary>The effects of <paramref name="incidents"/> that can touch <paramref name="app"/>, in incident then effect order.</summary>
    public static IReadOnlyList<AppliedEffect> Compile(CompanyPlan company, AppPlan app, IEnumerable<ActiveIncident> incidents)
    {
        var result = new List<AppliedEffect>();
        foreach (var incident in incidents)
        {
            var scenario = company.FindScenario(incident.ScenarioKey);
            if (scenario?.Effects is null) continue;

            var entityOnly = scenario.Effects.FirstOrDefault(e => e?.Kind == EffectKinds.EntityOnly);
            IReadOnlyList<int>? entities = null;
            if (entityOnly?.Entities is { Count: > 0 } ids)
            {
                entities = ids.Select(id => IndexOf(app.EntityIds, id)).Where(i => i >= 0).Distinct().Order().ToList();
                if (entities.Count == 0) continue;
            }

            foreach (var effect in scenario.Effects)
            {
                if (effect is null || effect.Kind == EffectKinds.EntityOnly) continue;

                IReadOnlyList<int>? steps = null;
                IReadOnlyList<int>? deps = null;
                if (effect.Step is not null)
                {
                    var step = app.Steps.FirstOrDefault(s => s.Name == effect.Step);
                    if (step is null) continue;
                    steps = [step.Index];
                }

                if (effect.Partner is not null)
                {
                    deps = app.Dependencies.Where(d => string.Equals(d.Partner, effect.Partner, StringComparison.OrdinalIgnoreCase))
                        .Select(d => d.GlobalIndex).ToList();
                    if (deps.Count == 0) continue;
                    // The requests whose step calls the partner are the ones a slow partner slows down.
                    steps = app.Steps.Where(s => s.Dependencies.Any(d => deps.Contains(d.GlobalIndex))).Select(s => s.Index).ToList();
                }

                result.Add(new AppliedEffect(
                    effect.Kind!, incident.ScenarioKey, incident.Start, incident.End, steps, entities, deps,
                    effect.Factor ?? 1.0, effect.Rate ?? 0.0, string.IsNullOrWhiteSpace(effect.ExceptionType) ? null : effect.ExceptionType));
            }
        }
        return result;
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++) if (string.Equals(ids[i], id, StringComparison.Ordinal)) return i;
        return -1;
    }
}
