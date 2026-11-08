namespace JourneyMonitor.Core.Company;

/// <summary>
/// Contract v6 §6 (MC7): agent descriptions in the Simian Army roster speak the active company's vocabulary
/// (its services and what it calls an entity) instead of naming one company's verticals.
/// </summary>
public static class CompanyAgentText
{
    public static string ScoutPurpose(CompanyProfile? profile, bool isLatency)
    {
        var board = isLatency ? "latency" : "exception";
        return $"Watches the per-{EntityNoun(profile)} {board} priority board across {ServiceList(profile)}, "
            + "and hands the L3 team a self-contained investigation package for anything worth a deeper look.";
    }

    public static string LatencySpikePurpose(CompanyProfile? profile) =>
        $"Detects individual latency spikes that repeat within a {StepNoun(profile)} beyond a configurable threshold "
        + "(distinct from the aggregated P95 regression check) and raises a finding for the Doctor Monkey to diagnose.";

    public static string SloBurnRatePurpose(CompanyProfile? profile) =>
        "Applies Google SRE's multiwindow multi-burn-rate method against configured SLO definitions (latency + exception rate golden signals) "
        + $"across every {ServiceNoun(profile)}/{StepNoun(profile)}, raising page/ticket severity findings for the Doctor Monkey to diagnose and, "
        + "for page severity, notifying every enabled alert channel (Teams/Email).";

    /// <summary>What the company calls one entity, lower case ("club", "sucursal"...); "entity" without a profile.</summary>
    public static string EntityNoun(CompanyProfile? profile) => Noun(profile?.Glossary?.EntityNoun, profile?.Labels?.Entity, "entity");

    public static string StepNoun(CompanyProfile? profile) => Noun(profile?.Glossary?.StepNoun, profile?.Labels?.Step, "step");

    public static string ServiceNoun(CompanyProfile? profile) => Noun(profile?.Glossary?.ServiceNoun, profile?.Labels?.Service, "service");

    private static string Noun(string? glossary, string? label, string fallback) =>
        (FirstNonBlank(glossary, label) ?? fallback).ToLowerInvariant();

    /// <summary>"A, B and C" from the profile's service names, in profile order; a neutral phrase without services.</summary>
    public static string ServiceList(CompanyProfile? profile)
    {
        var names = (profile?.Services ?? [])
            .Select(s => FirstNonBlank(s?.DisplayName, s?.Key))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return $"every monitored {ServiceNoun(profile)}";
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
