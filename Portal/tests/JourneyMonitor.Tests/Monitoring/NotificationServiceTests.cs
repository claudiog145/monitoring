using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

internal sealed class FakeNotificationChannel(string channel, bool delivered, string? provider = null, string? error = null) : INotificationChannel
{
    public string Channel => channel;

    public int Calls { get; private set; }

    public Task<(bool Delivered, string? Provider, string? Error)> SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult((delivered, provider, error));
    }
}

/// <summary>Breaks the "never throws" contract on purpose - the service has to survive a channel that misbehaves, not just one that reports a failure.</summary>
internal sealed class ThrowingNotificationChannel(string channel) : INotificationChannel
{
    public string Channel => channel;

    public Task<(bool Delivered, string? Provider, string? Error)> SendAsync(NotificationRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("channel exploded");
}

/// <summary>Simulates the audit write itself failing - the one write Toast actually depends on to be seen.</summary>
internal sealed class ThrowingNotificationLogRepository : INotificationLogRepository
{
    public Task<NotificationLogEntry> AddAsync(NotificationLogEntry entry, CancellationToken ct = default) =>
        throw new InvalidOperationException("log write exploded");

    public Task<IReadOnlyList<NotificationLogEntry>> GetBySourceAsync(string sourceType, Guid sourceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationLogEntry>>([]);

    public Task<IReadOnlyList<NotificationLogEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationLogEntry>>([]);

    public Task<IReadOnlyList<NotificationLogEntry>> GetUndeliveredToastsAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationLogEntry>>([]);
}

public class NotificationServiceTests
{
    private static NotificationRequest Request() => new(
        NotificationSourceType.ForensicCase,
        Guid.NewGuid(),
        NotificationSeverity.Critical,
        "[JourneyMonitor] something broke",
        "body",
        []);

    private static NotificationService Build(INotificationLogRepository log, params INotificationChannel[] channels) =>
        new(channels, log, NullLogger<NotificationService>.Instance);

    [Fact]
    public async Task LogsOneAttemptPerChannelIncludingTheOnesThatFailed()
    {
        var log = new InMemoryNotificationLogRepository();
        var request = Request();
        var service = Build(
            log,
            new FakeNotificationChannel(NotificationChannel.Toast, delivered: true, provider: NotificationChannel.Toast),
            new FakeNotificationChannel(NotificationChannel.Email, delivered: false, error: "SmtpEmailSender: timeout"),
            new FakeNotificationChannel(NotificationChannel.Teams, delivered: true, provider: NotificationChannel.Teams));

        await service.NotifyAsync(request, CancellationToken.None);

        var entries = await log.GetBySourceAsync(request.SourceType, request.SourceId, CancellationToken.None);
        Assert.Equal(3, entries.Count);
        Assert.Equal(
            [NotificationChannel.Email, NotificationChannel.Teams, NotificationChannel.Toast],
            entries.Select(e => e.Channel).OrderBy(c => c).ToArray());

        var failed = entries.Single(e => e.Channel == NotificationChannel.Email);
        Assert.False(failed.Success);
        Assert.Equal("SmtpEmailSender: timeout", failed.ErrorMessage);
        Assert.Equal(request.Subject, failed.Subject);
    }

    [Fact]
    public async Task AChannelThatThrowsDoesNotStopTheOthersFromDelivering()
    {
        var log = new InMemoryNotificationLogRepository();
        var after = new FakeNotificationChannel(NotificationChannel.Teams, delivered: true, provider: NotificationChannel.Teams);
        var request = Request();
        var service = Build(log, new ThrowingNotificationChannel(NotificationChannel.Email), after);

        var delivered = await service.NotifyAsync(request, CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.Equal(1, after.Calls);

        var entries = await log.GetBySourceAsync(request.SourceType, request.SourceId, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        var thrown = entries.Single(e => e.Channel == NotificationChannel.Email);
        Assert.False(thrown.Success);
        Assert.Equal("channel exploded", thrown.ErrorMessage);
    }

    [Fact]
    public async Task CountsRealDeliveriesRatherThanAttempts()
    {
        var log = new InMemoryNotificationLogRepository();
        var service = Build(
            log,
            new FakeNotificationChannel(NotificationChannel.Toast, delivered: true, provider: NotificationChannel.Toast),
            new FakeNotificationChannel(NotificationChannel.Email, delivered: false),
            new FakeNotificationChannel(NotificationChannel.Teams, delivered: false));

        var delivered = await service.NotifyAsync(Request(), CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.Equal(3, (await log.GetAllAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ToastAlwaysDeliversAndIsPersistedForThePollingUi()
    {
        var log = new InMemoryNotificationLogRepository();
        var request = Request();
        var service = Build(log, new ToastNotificationChannel());

        var delivered = await service.NotifyAsync(request, CancellationToken.None);

        Assert.Equal(1, delivered);

        var toasts = await log.GetUndeliveredToastsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        var toast = Assert.Single(toasts);
        Assert.Equal(NotificationChannel.Toast, toast.Channel);
        Assert.Equal(NotificationChannel.Toast, toast.ProviderUsed);
        Assert.True(toast.Success);
        Assert.Null(toast.ErrorMessage);
        Assert.Equal(request.Subject, toast.Subject);
        Assert.Equal(request.SourceId, toast.SourceId);

        // A client that already polled past this toast must not be handed it a second time.
        Assert.Empty(await log.GetUndeliveredToastsAsync(toast.SentAt, CancellationToken.None));
    }

    /// <summary>
    /// Toast has no transport of its own - the NotificationLog row IS the delivery. If that write
    /// fails, the channel's own "always succeeds" report must not be trusted: nothing a poller can
    /// find was ever persisted, so counting it as delivered would be a lie.
    /// </summary>
    [Fact]
    public async Task ADroppedToastLogRowIsNotCountedAsDelivered()
    {
        var service = Build(new ThrowingNotificationLogRepository(), new ToastNotificationChannel());

        var delivered = await service.NotifyAsync(Request(), CancellationToken.None);

        Assert.Equal(0, delivered);
    }

    /// <summary>
    /// Email and Teams already reached a real transport by the time they log - a failed audit
    /// write afterwards must not retroactively undo a delivery that already happened.
    /// </summary>
    [Fact]
    public async Task ADroppedLogRowForANonToastChannelStillCountsAsDelivered()
    {
        var service = Build(
            new ThrowingNotificationLogRepository(),
            new FakeNotificationChannel(NotificationChannel.Teams, delivered: true, provider: NotificationChannel.Teams));

        var delivered = await service.NotifyAsync(Request(), CancellationToken.None);

        Assert.Equal(1, delivered);
    }
}
