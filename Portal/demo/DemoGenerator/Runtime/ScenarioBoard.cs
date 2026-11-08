using DemoGenerator.Synthesis;

namespace DemoGenerator.Runtime;

public sealed record ActiveScenario(string CompanyKey, string ScenarioKey, string Title, DateTimeOffset StartedAt, DateTimeOffset EndsAt);

/// <summary>
/// Live scenarios, in memory only. Every run is kept (a restart of the same scenario adds a run, it never
/// overwrites an earlier one) for a day after it ends, so minutes the feeder has not written yet still get the
/// effects that were in force during them.
/// </summary>
public sealed class ScenarioBoard(TimeProvider time)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);
    private readonly object _gate = new();
    private readonly List<ActiveScenario> _runs = [];

    /// <summary>Starts a run at the current minute; a still-running run of the same scenario ends there.</summary>
    public ActiveScenario Start(string companyKey, string scenarioKey, string title, int durationMinutes)
    {
        var start = SynthesisClock.FloorMinute(time.GetUtcNow());
        var scenario = new ActiveScenario(companyKey, scenarioKey, title, start, start + TimeSpan.FromMinutes(durationMinutes));
        lock (_gate)
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (run.CompanyKey == companyKey && run.ScenarioKey == scenarioKey && run.EndsAt > start)
                    _runs[i] = run with { EndsAt = run.StartedAt > start ? run.StartedAt : start };
            }
            _runs.Add(scenario);
            Prune();
        }
        return scenario;
    }

    /// <summary>Ends the running scenarios of one company (or all) at the current minute; returns what was stopped.</summary>
    public IReadOnlyList<ActiveScenario> Stop(string? companyKey)
    {
        var now = time.GetUtcNow();
        var end = SynthesisClock.FloorMinute(now);
        var stopped = new List<ActiveScenario>();
        lock (_gate)
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (companyKey is not null && !string.Equals(run.CompanyKey, companyKey, StringComparison.Ordinal)) continue;
                if (run.EndsAt <= now) continue;
                var ended = run with { EndsAt = end < run.StartedAt ? run.StartedAt : end };
                _runs[i] = ended;
                stopped.Add(ended);
            }
        }
        return stopped;
    }

    public void Clear(string companyKey)
    {
        lock (_gate) _runs.RemoveAll(r => r.CompanyKey == companyKey);
    }

    public IReadOnlyList<ActiveScenario> Active()
    {
        var now = time.GetUtcNow();
        lock (_gate)
            return _runs.Where(s => s.EndsAt > now).OrderBy(s => s.StartedAt).ThenBy(s => s.CompanyKey, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every run of the company still relevant to feeding (running, or ended within the retention).</summary>
    public IReadOnlyList<ActiveIncident> IncidentsFor(string companyKey)
    {
        lock (_gate)
            return _runs
                .Where(s => s.CompanyKey == companyKey && s.EndsAt > s.StartedAt)
                .OrderBy(s => s.StartedAt)
                .Select(s => new ActiveIncident(s.ScenarioKey, s.StartedAt, s.EndsAt))
                .ToList();
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Retention;
        _runs.RemoveAll(r => r.EndsAt < cutoff);
    }
}
