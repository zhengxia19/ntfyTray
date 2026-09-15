using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using NtfyTray.Infrastructure;
using System.Diagnostics;

namespace NtfyTray.Notifications;

public sealed class WindowsNotificationService : INotificationService, IDisposable
{
    private readonly NotificationProcessor processor;
    private readonly Action invoked;
    private readonly bool registered;
    private bool bound;
    private bool disposed;
    public NotificationFaults Faults => processor.Faults;
    public event Action? Changed;

    public WindowsNotificationService(UiDispatcher dispatcher, bool enabled, Action invoked, Action<int, double>? submittedTiming = null)
    {
        this.invoked = invoked;
        try
        {
            if (!AppNotificationManager.IsSupported()) throw new NotSupportedException();
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            bound = true;
            AppNotificationManager.Default.Register("NtfyTray", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")));
            registered = true;
        }
        catch
        {
            registered = false;
            if (bound)
            {
                AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
                bound = false;
            }
        }
        processor = new NotificationProcessor(enabled, registered,
            token => dispatcher.InvokeAsync(() => AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled, token),
            (notification, token) => dispatcher.InvokeAsync(() =>
            {
                var native = new AppNotificationBuilder().AddText(notification.Title).AddText(notification.Body).BuildNotification();
                var attempt = notification.Timing?.NextAttempt();
                var invokedAt = Stopwatch.GetTimestamp();
                AppNotificationManager.Default.Show(native);
                if (native.Id == 0) throw new InvalidOperationException("Notification was not assigned an ID");
                if (notification.Timing is { } timing && attempt is { } number)
                    submittedTiming?.Invoke(number, Stopwatch.GetElapsedTime(timing.ReceivedTimestamp, invokedAt).TotalMilliseconds);
                return true;
            }, token));
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await processor.InitializeAsync(token).ConfigureAwait(false);
        Changed?.Invoke();
    }
    public async Task<NotificationResult> SubmitAsync(FormattedNotification notification, CancellationToken cancellationToken)
    {
        try { return await processor.SubmitAsync(notification, cancellationToken).ConfigureAwait(false); }
        finally { Changed?.Invoke(); }
    }
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => invoked();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (registered) AppNotificationManager.Default.Unregister(); }
        finally
        {
            if (bound) AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            bound = false;
        }
    }
}
