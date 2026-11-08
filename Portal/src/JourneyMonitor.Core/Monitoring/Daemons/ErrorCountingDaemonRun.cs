using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>Pass-through run scope that counts Error entries, so run-completed can be raised to Warning.</summary>
public sealed class ErrorCountingDaemonRun(IDaemonRun inner) : IDaemonRun
{
    private int _errors;

    public int Errors => Volatile.Read(ref _errors);
    public Guid RunId => inner.RunId;
    public string DaemonKey => inner.DaemonKey;

    public void Info(string kind, string message, object? data = null) => inner.Info(kind, message, data);
    public void Warning(string kind, string message, object? data = null) => inner.Warning(kind, message, data);

    public void Error(string kind, string message, object? data = null)
    {
        Interlocked.Increment(ref _errors);
        inner.Error(kind, message, data);
    }

    public Task FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
}
