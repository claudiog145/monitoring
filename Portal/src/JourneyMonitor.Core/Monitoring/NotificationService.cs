using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Fans one <see cref="NotificationRequest"/> out to every registered <see cref="INotificationChannel"/>
/// and writes exactly one audit row per channel, successes and failures alike.
///
/// Centralising the log here (instead of inside each channel, where it used to live and was
/// duplicated) is what makes a channel's only responsibility "deliver or report why not", and it
/// guarantees a channel physically cannot forget to leave a trace of an attempt.
/// </summary>
public sealed class NotificationService(
    IEnumerable<INotificationChannel> channels,
    INotificationLogRepository notificationLogRepository,
    ILogger<NotificationService> logger) : INotificationService
{
    /// <summary>Returns how many channels actually delivered - not how many were attempted, so a caller can report "notified: 0" honestly when every channel is disabled or broken.</summary>
    public async Task<int> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        var delivered = 0;

        foreach (var channel in channels)
        {
            var (success, provider, error) = await SendSafelyAsync(channel, request, ct);
            var logged = await LogAttemptAsync(request, channel.Channel, provider, success, error, ct);

            // Toast has no transport of its own - the NotificationLog row IS the delivery (the UI
            // finds it via GetUndeliveredToastsAsync), so losing that write means this toast will
            // never reach anyone even though the channel itself reported success. Every other
            // channel already delivered through a real transport by the time it logs, so a failed
            // audit write there must not retroactively undo a delivery that already happened.
            var actuallyDelivered = success && (channel.Channel != NotificationChannel.Toast || logged);
            if (actuallyDelivered) delivered++;
        }

        return delivered;
    }

    /// <summary>A channel is contractually not supposed to throw, but one that breaks that contract must not stop the channels after it from delivering.</summary>
    private async Task<(bool Success, string? Provider, string? Error)> SendSafelyAsync(
        INotificationChannel channel, NotificationRequest request, CancellationToken ct)
    {
        try
        {
            return await channel.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Genuine caller cancellation - not a channel failure, and swallowing it would hide a shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[NotificationService] channel {Channel} threw for {SourceType} {SourceId}.", channel.Channel, request.SourceType, request.SourceId);
            return (false, null, ex.Message);
        }
    }

    /// <summary>Best-effort audit write - for every channel but Toast, losing the log row must never change whether the notification itself counted as delivered. Returns whether the row was actually persisted.</summary>
    private async Task<bool> LogAttemptAsync(NotificationRequest request, string channel, string? provider, bool success, string? error, CancellationToken ct)
    {
        try
        {
            await notificationLogRepository.AddAsync(new NotificationLogEntry(
                Guid.NewGuid(),
                request.SourceType,
                request.SourceId,
                channel,
                provider,
                success,
                error,
                DateTimeOffset.UtcNow,
                request.Subject), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[NotificationService] failed to persist the NotificationLog row for channel {Channel} / {SourceType} {SourceId}.", channel, request.SourceType, request.SourceId);
            return false;
        }
    }
}
