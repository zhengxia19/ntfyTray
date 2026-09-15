namespace NtfyTray.Notifications;

public enum NotificationFailureCode
{
    InitializationFailed,
    SettingsRestricted,
    SettingsReadFailed,
    SubmissionFailed
}

public sealed record NotificationFaults(
    NotificationFailureCode? Initialization,
    NotificationFailureCode? Settings,
    NotificationFailureCode? Submission);

/// <summary>Notification policy independent of Windows APIs. Delegates provide UI dispatch when needed.</summary>
public sealed class NotificationProcessor : INotificationService
{
    private readonly bool enabled;
    private readonly bool initializationSucceeded;
    private readonly Func<CancellationToken, Task<bool>> readSettings;
    private readonly Func<FormattedNotification, CancellationToken, Task> submit;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object stateLock = new();
    private NotificationFaults faults;

    public NotificationProcessor(
        bool enabled,
        bool initializationSucceeded,
        Func<CancellationToken, Task<bool>> readSettings,
        Func<FormattedNotification, CancellationToken, Task> submit,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(submit);
        this.enabled = enabled;
        this.initializationSucceeded = initializationSucceeded;
        this.readSettings = readSettings;
        this.submit = submit;
        this.delay = delay ?? Task.Delay;
        faults = new(initializationSucceeded ? null : NotificationFailureCode.InitializationFailed, null, null);
    }

    public NotificationFaults Faults
    {
        get
        {
            lock (stateLock)
            {
                return faults;
            }
        }
    }

    // The host calls this after its one-time native initialization; it never retries initialization.
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationResult> SubmitAsync(
        FormattedNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var allowed = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
        {
            return NotificationResult.Suppressed;
        }

        if (!initializationSucceeded || allowed is null)
        {
            return NotificationResult.Dropped;
        }

        if (!allowed.Value)
        {
            return NotificationResult.Suppressed;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await submit(notification, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (stateLock)
                {
                    faults = faults with { Submission = null };
                }

                return NotificationResult.Submitted;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Store only a fixed code: exception messages may contain private notification text.
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt == 3)
                {
                    lock (stateLock)
                    {
                        faults = faults with { Submission = NotificationFailureCode.SubmissionFailed };
                    }

                    return NotificationResult.Dropped;
                }
            }

            await delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unreachable notification attempt state.");
    }

    private async Task<bool?> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var allowed = await readSettings(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateLock)
            {
                faults = faults with { Settings = !allowed ? NotificationFailureCode.SettingsRestricted : null };
            }

            return allowed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateLock)
            {
                faults = faults with { Settings = NotificationFailureCode.SettingsReadFailed };
            }

            return null;
        }
    }
}
