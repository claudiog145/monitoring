using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>Run scope used when no recorder is configured, or when the daemon is disabled: writes nothing.</summary>
public sealed class NullDaemonRun : IDaemonRun
{
    public static readonly NullDaemonRun Instance = new();

    private NullDaemonRun()
    {
    }

    public Guid RunId => Guid.Empty;
    public string DaemonKey => "";
    public void Info(string kind, string message, object? data = null) { }
    public void Warning(string kind, string message, object? data = null) { }
    public void Error(string kind, string message, object? data = null) { }
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}
