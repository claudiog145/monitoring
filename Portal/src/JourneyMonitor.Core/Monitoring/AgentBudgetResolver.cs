using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// The Inspector's spend limits after the stored row is applied over the configured defaults.
/// Every consumer reads this instead of <see cref="InspectorOptions"/> directly, so there is one
/// place where "database wins, configuration is the fallback" is decided.
/// </summary>
public sealed record ResolvedInspectorBudget(
    IReadOnlyList<string> DebateModels,
    int DebateRounds,
    int MaxHelpHops,
    int MaxInvestigationsPerDay,
    int SkipDebateWhenContributorsBelow);

/// <summary>
/// Answers "what is this agent allowed to spend right now?".
///
/// Never throws: an unconfigured or failing store resolves to the configured defaults. The
/// alternative - failing the tick - turns a settings-store outage into a monitoring outage, which
/// is the same trade <see cref="AgentPauseResolver"/> makes and for the same reason.
///
/// ⚠️ The failure direction is deliberately different from the pause. A store outage makes the
/// agents keep RUNNING (pause defaults to false) but fall back to the CONFIGURED limits, not to
/// unlimited. A lost connection must never be able to raise a spend ceiling.
///
/// Cached briefly because the Inspector asks on every investigation and the answer only changes
/// when somebody saves the form. <see cref="Invalidate"/> exists so a change takes effect without
/// waiting out the TTL in the process that just wrote it.
/// </summary>
public interface IAgentBudgetResolver
{
    Task<ResolvedInspectorBudget> ResolveInspectorAsync(CancellationToken cancellationToken);

    /// <summary>Stored rows only - what the UI needs to render "overridden" versus "default".</summary>
    Task<IReadOnlyDictionary<string, AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken);

    void Invalidate();
}

public sealed class AgentBudgetResolver(
    IAgentBudgetStore budgetStore,
    IOptions<JourneyStoreOptions> storeOptions,
    IOptions<InspectorOptions> inspectorOptions,
    ILogger<AgentBudgetResolver> logger) : IAgentBudgetResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, AgentBudgetSetting> NothingStored =
        new Dictionary<string, AgentBudgetSetting>(StringComparer.OrdinalIgnoreCase);

    // Reference assignment is atomic; concurrent readers can at worst both refresh.
    private Snapshot? _snapshot;

    public async Task<ResolvedInspectorBudget> ResolveInspectorAsync(CancellationToken cancellationToken)
    {
        var limits = inspectorOptions.Value;
        var stored = (await GetAllAsync(cancellationToken)).TryGetValue(MonkeyKey.InspectorMonkey, out var row)
            ? row
            : null;

        var models = ParseModels(stored?.DebateModels) ?? limits.ResolvedDebateModels;

        return new ResolvedInspectorBudget(
            DebateModels: models,
            // Zero is a meaningful value here ("no peer rounds"), so this cannot use the
            // is-greater-than-zero shape the options themselves use for their defaults.
            DebateRounds: stored?.DebateRounds is >= 0 ? stored.DebateRounds.Value : limits.ResolvedDebateRounds,
            MaxHelpHops: stored?.MaxHelpHops is >= 0 ? stored.MaxHelpHops.Value : limits.ResolvedMaxHelpHops,
            MaxInvestigationsPerDay: stored?.MaxRunsPerDay is > 0 ? stored.MaxRunsPerDay.Value : limits.ResolvedMaxInvestigationsPerDay,
            SkipDebateWhenContributorsBelow: stored?.SkipDebateWhenContributorsBelow is >= 0
                ? stored.SkipDebateWhenContributorsBelow.Value
                : limits.ResolvedSkipDebateWhenContributorsBelow);
    }

    public async Task<IReadOnlyDictionary<string, AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken)
    {
        var cached = _snapshot;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Settings;

        if (!storeOptions.Value.IsConfigured)
        {
            _snapshot = new Snapshot(NothingStored, DateTimeOffset.UtcNow.Add(CacheTtl));
            return NothingStored;
        }

        try
        {
            var rows = await budgetStore.GetAllAsync(cancellationToken);
            var map = rows.ToDictionary(r => r.MonkeyKey, r => r, StringComparer.OrdinalIgnoreCase);
            _snapshot = new Snapshot(map, DateTimeOffset.UtcNow.Add(CacheTtl));
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the agent budget settings; falling back to the configured limits.");
            _snapshot = new Snapshot(NothingStored, DateTimeOffset.UtcNow.Add(CacheTtl));
            return NothingStored;
        }
    }

    public void Invalidate() => _snapshot = null;

    /// <summary>Null when nothing usable is stored, so the caller keeps its configured roster.</summary>
    private static IReadOnlyList<string>? ParseModels(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var models = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return models.Count > 0 ? models : null;
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, AgentBudgetSetting> Settings, DateTimeOffset ExpiresAt);
}
