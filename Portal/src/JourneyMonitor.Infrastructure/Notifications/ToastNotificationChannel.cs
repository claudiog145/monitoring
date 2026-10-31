using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Notifications;

/// <summary>
/// In-portal toast. Nothing is transported anywhere: the NotificationLog row that
/// <see cref="INotificationService"/> writes for this attempt IS the delivery, and the UI picks it
/// up with <see cref="INotificationLogRepository.GetUndeliveredToastsAsync"/>.
///
/// It therefore always reports success - there is no transport that can fail, and claiming
/// otherwise would make the delivery count lie.
/// </summary>
public sealed class ToastNotificationChannel : INotificationChannel
{
    public string Channel => NotificationChannel.Toast;

    public Task<(bool Delivered, string? Provider, string? Error)> SendAsync(NotificationRequest request, CancellationToken ct = default) =>
        Task.FromResult<(bool, string?, string?)>((true, NotificationChannel.Toast, null));
}
