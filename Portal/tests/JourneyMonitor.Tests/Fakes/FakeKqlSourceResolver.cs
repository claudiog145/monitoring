using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Tests.Fakes;

/// <summary>
/// Resolves {{SOURCE:alias}} from <see cref="TelemetrySourceDefaults"/> instead of the database,
/// so a test asserting on query text sees the same resource ids production ships with. Uses the
/// real substitution rules rather than a no-op: a resolver that silently left placeholders in
/// place would make every "no literal subscription id" assertion pass for the wrong reason.
/// </summary>
public sealed class FakeKqlSourceResolver : IKqlSourceResolver
{
    public Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default)
    {
        var env = InsightsEnvironment.Normalize(environment);
        var resolved = KqlSourceResolver.PlaceholderPattern.Replace(kql ?? "", match =>
        {
            var alias = match.Groups["alias"].Value;
            var entry = TelemetrySourceDefaults.All.FirstOrDefault(s =>
                s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                s.Environment.Equals(env, StringComparison.OrdinalIgnoreCase))
                ?? TelemetrySourceDefaults.All.FirstOrDefault(s =>
                    s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

            return entry?.ResourceId
                ?? throw new KqlSourceResolutionException($"No telemetry source registered for alias \"{alias}\".");
        });
        return Task.FromResult(resolved);
    }

    public void Invalidate() { }
}
