using System.Text.Json;
using DemoGenerator.Archetypes;
using DemoGenerator.Packs;

namespace DemoGenerator.Synthesis;

/// <summary>Where an archetype step sits in the company's flows: its session group and 0-based position.</summary>
public sealed record SessionSlot(int Group, int Position, string? ServiceKey, string? FlowKey, string? StepKey);

/// <summary>
/// Maps every archetype step to the profile flow step the platform classifies it into, so sessions can walk a
/// flow in order. Classification mirrors the platform's (CompanyKqlTemplater.BuildRules): every step of every
/// flow of every service, stable-sorted by <c>order</c>, blank matchers dropped, first host-accepted match wins.
/// The position is the step's index in its flow ordered by <c>order</c>, as ProfileJourneyBuilder numbers it.
/// Steps no flow claims (or every step when there is no profile) are a group of their own at position 0.
/// </summary>
public sealed class SessionFlows
{
    /// <summary>Group of an unclaimed step: this plus the step's index in the archetype.</summary>
    public const int UnclaimedGroupBase = 10000;

    private readonly IReadOnlyList<SessionSlot> _slots;

    private SessionFlows(IReadOnlyList<SessionSlot> slots) => _slots = slots;

    /// <summary>Slot of the archetype step at <paramref name="archetypeStepIndex"/> (index in <see cref="Archetype.Steps"/>).</summary>
    public SessionSlot SlotOf(int archetypeStepIndex) => _slots[archetypeStepIndex];

    public IReadOnlyList<SessionSlot> Slots => _slots;

    /// <summary>Every step alone: sessions of one request each.</summary>
    public static SessionFlows Unclaimed(Archetype archetype) =>
        new((archetype.Steps ?? []).Select((_, i) => Unclaimed(i)).ToList());

    /// <summary>Reads <c>profile.json</c> next to the archetype; without a readable profile every step is unclaimed.</summary>
    public static (SessionFlows Flows, string? Problem) Load(string packDir, Archetype archetype)
    {
        var path = Path.Combine(packDir, "profile.json");
        if (!File.Exists(path)) return (Unclaimed(archetype), "profile.json is missing; sessions are one request each.");
        try
        {
            var profile = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(path), ArchetypeLoader.JsonOptions);
            return (Resolve(archetype, profile), null);
        }
        catch (JsonException ex)
        {
            return (Unclaimed(archetype), $"profile.json is not readable ({ex.Message}); sessions are one request each.");
        }
    }

    public static SessionFlows Resolve(Archetype archetype, ProfileDocument? profile)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        var steps = archetype.Steps ?? [];
        if (profile is null) return Unclaimed(archetype);

        var flows = (profile.Services ?? [])
            .Where(s => s is not null)
            .SelectMany(s => (s!.Flows ?? []).Where(f => f is not null).Select(f => (Service: s, Flow: f!)))
            .Select((x, i) => (x.Service, x.Flow, Group: i + 1, Ordered: (x.Flow.Steps ?? []).Where(t => t is not null).Select(t => t!).OrderBy(t => t.Order).ToList()))
            .ToList();
        var rules = flows
            .SelectMany(f => f.Ordered.Select((step, position) => (f.Service, f.Flow, f.Group, Step: step, Position: position)))
            .OrderBy(r => r.Step.Order)
            .Select(r => (r.Service, r.Flow, r.Group, r.Step, r.Position,
                Matchers: (r.Step.Matchers ?? []).Where(m => m is not null && !string.IsNullOrEmpty(m.Value)).Select(m => m!).ToList()))
            .Where(r => r.Matchers.Count > 0)
            .ToList();

        var entityIds = ArchetypeLoader.EntityIds(archetype.Entities);
        var slots = new List<SessionSlot>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var app = (archetype.Apps ?? []).FirstOrDefault(a => string.Equals(a.AppId, step.App, StringComparison.OrdinalIgnoreCase));
            var host = archetype.Entities?.HostTemplate is { } template && entityIds.Count > 0
                ? template.Replace("{0}", entityIds[0], StringComparison.Ordinal)
                : app?.Host ?? "";
            var url = "https://" + host + step.Url;
            var hit = rules.FirstOrDefault(r => PackValidator.HostAccepted(r.Service, host) && r.Matchers.Any(m => PackValidator.StepMatches(m, url, step.Name ?? "")));
            slots.Add(hit.Step is null
                ? Unclaimed(i)
                : new SessionSlot(hit.Group, hit.Position, hit.Service.Key, hit.Flow.Key, hit.Step.Key));
        }
        return new SessionFlows(slots);
    }

    private static SessionSlot Unclaimed(int archetypeStepIndex) => new(UnclaimedGroupBase + archetypeStepIndex, 0, null, null, null);
}
