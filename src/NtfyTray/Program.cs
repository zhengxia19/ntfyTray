using NtfyTray.Infrastructure;
using NtfyTray.Notifications;
using NtfyTray.Configuration;
using NtfyTray.Ntfy;
using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace NtfyTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var guard = new SingleInstanceGuard();
        if (!guard.IsPrimary) return;
        var dispatcher = new UiDispatcher();
        var log = new LoggingSetup();
        var configPath = ConfigLoader.GetConfigPath();
        log.Startup(typeof(Program).Assembly.GetName().Version!, configPath);
        AppConfig? config = null;
        try
        {
            config = ConfigLoader.Load();
            log.ApplyConfiguration(config.Logging);
            log.Write(LogEventKind.ConfigurationAccepted);
        }
        catch (ConfigException error) { log.ConfigurationRejected(error); }
        var preferences = config?.Notification ?? new NotificationConfig();
        var lifetime = new AsyncLifetime(_ => log.Write(LogEventKind.BackgroundFailure, Serilog.Events.LogEventLevel.Error));
        var notifications = new WindowsNotificationService(dispatcher, preferences.Enabled, () =>
        {
            log.Write(LogEventKind.NotificationActivated);
        }, (attempt, elapsed) => log.Write(LogEventKind.NotificationApiInvoked, retryAttempt: attempt, durationMs: elapsed));
        // No HTTP client or subscription exists until the entire configuration has been accepted.
        var subscriptions = config is null ? null : new SubscriptionManager(config, notifications, log);
        NetworkAddressChangedEventHandler networkChanged = (_, _) => subscriptions?.RequestReconnectAll();
        PowerModeChangedEventHandler powerChanged = (_, args) =>
        {
            if (args.Mode == PowerModes.Resume) subscriptions?.RequestReconnectAll();
        };
        async Task TestNotification(CancellationToken token)
        {
            var text = NotificationFormatter.Format("NtfyTray 本地测试", "原生系统通知测试，无需网络。点击不执行业务操作。", "本地测试", preferences.DefaultTitle, preferences.IncludeTopic);
            var result = await notifications.SubmitAsync(text, token);
            log.Write(result switch { NotificationResult.Submitted => LogEventKind.MessageSubmitted,
                NotificationResult.Suppressed => LogEventKind.MessageSuppressed, _ => LogEventKind.MessageDropped });
        }
        var context = new TrayApplicationContext(dispatcher, TestNotification, configPath, log.DirectoryPath,
        async () =>
        {
            log.Write(LogEventKind.Stopping);
            NetworkChange.NetworkAddressChanged -= networkChanged;
            SystemEvents.PowerModeChanged -= powerChanged;
            var stopped = lifetime.StopAsync();
            try { if (subscriptions is not null) await subscriptions.StopAsync(); }
            finally { await stopped; }
        },
        () =>
        {
            try { notifications.Dispose(); }
            catch { log.Write(LogEventKind.BackgroundFailure, Serilog.Events.LogEventLevel.Error); }
            finally { log.Write(LogEventKind.Exiting); log.Dispose(); }
        });
        void Refresh()
        {
            var faults = notifications.Faults;
            _ = context.UpdateAsync(TrayStatus.Summarize(subscriptions?.States.Values.ToArray() ?? [], new(Configuration: config is null, Logging: log.HasFault,
                NotificationInitialization: faults.Initialization is not null,
                NotificationSettings: faults.Settings is not null, NotificationSubmission: faults.Submission is not null), preferences.Enabled));
        }
        notifications.Changed += Refresh;
        log.Faulted += Refresh;
        if (subscriptions is not null) subscriptions.StateChanged += Refresh;
        NetworkChange.NetworkAddressChanged += networkChanged;
        SystemEvents.PowerModeChanged += powerChanged;
        Refresh();
        log.Write(LogEventKind.TrayStarted);
        _ = lifetime.Run(async token =>
        {
            await notifications.InitializeAsync(token);
            token.ThrowIfCancellationRequested();
            subscriptions?.Start(token);
        });
        using var smokeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
        {
            smokeTimer.Tick += async (_, _) =>
            {
                smokeTimer.Stop();
                try { await TestNotification(lifetime.Token); }
                finally { await context.StopAsync(); }
            };
            smokeTimer.Start();
        }
        Application.Run(context);
    }    

}
