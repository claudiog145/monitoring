using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>
/// Best-effort activity writer for the Spec 1000 daemons. Entries are buffered per run and written on
/// <see cref="IDaemonRun.FlushAsync"/> (and every <see cref="AutoFlushThreshold"/> entries). A failing
/// store is logged and the entries are dropped: activity logging must never change a daemon's outcome.
/// </summary>
public sealed class DaemonActivityRecorder : IDaemonActivityRecorder
{
    public const int MaxMessageLength = 1000;
    public const int AutoFlushThreshold = 50;

    private readonly IDaemonActivityLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger<DaemonActivityRecorder> _logger;

    public DaemonActivityRecorder(IDaemonActivityLog log, TimeProvider time, ILogger<DaemonActivityRecorder> logger)
    {
        _log = log;
        _time = time;
        _logger = logger;
    }

    public IDaemonRun BeginRun(string daemonKey) => new Run(this, daemonKey ?? "");

    private async Task WriteAsync(string daemonKey, IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        try
        {
            await _log.AppendAsync(entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daemon activity: could not write {Count} entries for {DaemonKey}; they are dropped.", entries.Count, daemonKey);
        }
    }

    private DaemonActivityEntry CreateEntry(string daemonKey, Guid runId, DaemonActivityLevel level, string kind, string message, object? data)
    {
        var text = message ?? "";
        if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];
        return new DaemonActivityEntry(0, daemonKey, runId, GetNow(), level, kind ?? "", text, SerializeData(data));
    }

    private string? SerializeData(object? data)
    {
        if (data is null) return null;
        try
        {
            return JsonSerializer.Serialize(data, data.GetType(), TriageJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daemon activity: data of type {Type} could not be serialized; the entry is kept without data.", data.GetType().Name);
            return null;
        }
    }

    private DateTimeOffset GetNow()
    {
        try
        {
            return _time.GetUtcNow();
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private sealed class Run(DaemonActivityRecorder owner, string daemonKey) : IDaemonRun
    {
        private readonly object _gate = new();
        private List<DaemonActivityEntry> _buffer = [];
        // Batches are chained so they reach the store in the order they were written.
        private Task _pending = Task.CompletedTask;

        public Guid RunId { get; } = Guid.NewGuid();
        public string DaemonKey { get; } = daemonKey;

        public void Info(string kind, string message, object? data = null) => Add(DaemonActivityLevel.Info, kind, message, data);
        public void Warning(string kind, string message, object? data = null) => Add(DaemonActivityLevel.Warning, kind, message, data);
        public void Error(string kind, string message, object? data = null) => Add(DaemonActivityLevel.Error, kind, message, data);

        private void Add(DaemonActivityLevel level, string kind, string message, object? data)
        {
            try
            {
                var entry = owner.CreateEntry(DaemonKey, RunId, level, kind, message, data);
                lock (_gate)
                {
                    _buffer.Add(entry);
                    if (_buffer.Count >= AutoFlushThreshold)
                    {
                        _pending = Chain(_pending, TakeBuffer(), CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                owner._logger.LogWarning(ex, "Daemon activity: could not record a {Kind} entry for {DaemonKey}.", kind, DaemonKey);
            }
        }

        public async Task FlushAsync(CancellationToken ct = default)
        {
            try
            {
                Task pending;
                lock (_gate)
                {
                    _pending = Chain(_pending, TakeBuffer(), ct);
                    pending = _pending;
                }
                await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                owner._logger.LogWarning(ex, "Daemon activity: flush failed for {DaemonKey}.", DaemonKey);
            }
        }

        private List<DaemonActivityEntry> TakeBuffer()
        {
            var batch = _buffer;
            _buffer = [];
            return batch;
        }

        private Task Chain(Task previous, List<DaemonActivityEntry> batch, CancellationToken ct) =>
            batch.Count == 0 ? previous : ChainAsync(previous, batch, ct);

        private async Task ChainAsync(Task previous, List<DaemonActivityEntry> batch, CancellationToken ct)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                owner._logger.LogWarning(ex, "Daemon activity: an earlier batch for {DaemonKey} failed.", DaemonKey);
            }
            await owner.WriteAsync(DaemonKey, batch, ct).ConfigureAwait(false);
        }
    }
}
