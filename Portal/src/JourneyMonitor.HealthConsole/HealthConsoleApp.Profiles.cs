using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.HealthConsole;

/// <summary>Company profiles (contract v3): kpis, and the --profile filter of status and watch.</summary>
public static partial class HealthConsoleApp
{
    /// <summary>Shown in the Profile column for platform-wide activity lines that belong to no profile.</summary>
    public const string NoProfile = "-";

    private const int ProfileWidth = 12;

    // ---------------------------------------------------------------- kpis

    private static async Task<int> KpisAsync(ParsedOptions options, TextWriter output, IServiceProvider services)
    {
        var profile = ProfileFilter(options);
        var store = new ReadOnlyKpiDefinitionStore(services.GetRequiredService<IKpiDefinitionStore>());
        var definitions = (await store.GetAllAsync(profile))
            .OrderBy(d => d.ProfileKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.KpiDefinitionId)
            .ToList();

        await output.WriteLineAsync(string.Create(Inv,
            $"{"Profile",-ProfileWidth} {"Id",4} {"Pillar",-14} {"Kpi/Domain",-28} {"Stage",-12} {"Grain",-6} {"Enabled",-7} Method").TrimEnd());
        foreach (var d in definitions)
        {
            await output.WriteLineAsync(string.Create(Inv,
                $"{d.ProfileKey,-ProfileWidth} {d.KpiDefinitionId,4} {d.Pillar,-14} {d.Kpi + "/" + d.Domain,-28} {(d.Stage.Length == 0 ? "-" : d.Stage),-12} {GrainName(d.Grain),-6} {(d.Enabled ? "yes" : "no"),-7} {d.DetectionMethod} {d.MethodParamsJson}").TrimEnd());
        }

        if (definitions.Count == 0)
        {
            await output.WriteLineAsync(profile is null ? "No KPI definitions." : $"No KPI definitions for profile '{profile}'.");
        }
        return ExitOk;
    }

    /// <summary>Contract v4 §1 wire name: "domain" | "entity".</summary>
    public static string GrainName(KpiGrain grain) => grain == KpiGrain.Entity ? "entity" : "domain";

    // ---------------------------------------------------------------- status (KPIs by profile)

    /// <summary>After the daemon lines: enabled/total KPIs per profile and pillar. Nothing when no KPI store or no KPI exists.</summary>
    private static async Task WriteProfileSummaryAsync(TextWriter output, IServiceProvider services, string? profile, CancellationToken ct)
    {
        if (services.GetService<IKpiDefinitionStore>() is not { } inner) return;

        var definitions = await new ReadOnlyKpiDefinitionStore(inner).GetAllAsync(profile, ct);
        if (definitions.Count == 0) return;

        await output.WriteLineAsync();
        await output.WriteLineAsync(string.Create(Inv, $"{"Profile",-ProfileWidth} {"Pillar",-22} KPIs"));
        var groups = definitions
            .GroupBy(d => (d.ProfileKey, d.Pillar))
            .OrderBy(g => g.Key.ProfileKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Pillar);
        foreach (var group in groups)
        {
            await output.WriteLineAsync(string.Create(Inv,
                $"{group.Key.ProfileKey,-ProfileWidth} {KpiPillarNames.ToDisplay(group.Key.Pillar),-22} {group.Count(d => d.Enabled)}/{group.Count()} enabled"));
        }
    }

    // ---------------------------------------------------------------- watch (profile column and filter)

    /// <summary>The profileKey an activity entry carries in its data, or null for platform-wide lines.</summary>
    public static string? ProfileOf(DaemonActivityEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(entry.DataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("profileKey", out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>With --profile, lines of other profiles are hidden; platform-wide lines (no profile) are always shown.</summary>
    private static bool MatchesProfile(DaemonActivityEntry entry, string? profile) =>
        profile is null
        || ProfileOf(entry) is not { } entryProfile
        || string.Equals(entryProfile, profile, StringComparison.OrdinalIgnoreCase);

    private static string? ProfileFilter(ParsedOptions options) =>
        options.Single("profile") is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
