namespace NtfyTray.Notifications;

public sealed record FormattedNotification(string Title, string Body)
{
    public DeliveryTiming? Timing { get; init; }
}

public sealed class DeliveryTiming(long receivedTimestamp)
{
    private int attempts;
    public long ReceivedTimestamp { get; } = receivedTimestamp;
    public int NextAttempt() => Interlocked.Increment(ref attempts);
}

public enum NotificationResult
{
    Submitted,
    Suppressed,
    Dropped
}

public interface INotificationService
{
    Task<NotificationResult> SubmitAsync(FormattedNotification notification, CancellationToken cancellationToken);
}
