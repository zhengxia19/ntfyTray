using System.Diagnostics;
using NtfyTray.Infrastructure;

namespace NtfyTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly Icon connected;
    private readonly Icon disconnected;
    private readonly ToolStripMenuItem status;
    private readonly ToolStripMenuItem test;
    private readonly ToolStripMenuItem exit;
    private readonly CancellationTokenSource stopping = new();
    private readonly Func<Task> stop;
    private readonly Action dispose;
    private readonly UiDispatcher dispatcher;
    private bool isStopping;
    private Task activeTest = Task.CompletedTask;
    private Task? stopTask;

    public TrayApplicationContext(UiDispatcher dispatcher, Func<CancellationToken, Task> testNotification,
        string configPath, string logDirectory, Func<Task> stop, Action dispose)
    {
        this.dispatcher = dispatcher; this.stop = stop; this.dispose = dispose;
        connected = LoadIcon("connected"); disconnected = LoadIcon("disconnected");
        var menu = new ContextMenuStrip();
        status = new ToolStripMenuItem("正在启动") { Enabled = false };
        test = new ToolStripMenuItem("测试系统通知");
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            try { activeTest = testNotification(stopping.Token); await activeTest; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch { status.Text = "测试通知失败"; icon!.Icon = disconnected; }
            finally { if (!isStopping) test.Enabled = true; }
        };
        exit = new ToolStripMenuItem("退出");
        exit.Click += async (_, _) =>
        {
            try { await StopAsync(); }
            catch (Exception error) { Debug.WriteLine(error.GetType().Name); }
        };
        menu.Items.Add(status); menu.Items.Add(test);
        menu.Items.Add("打开配置文件", null, (_, _) => Open(configPath));
        menu.Items.Add("打开日志目录", null, (_, _) => Open(logDirectory));
        menu.Items.Add(exit);
        icon = new NotifyIcon { Icon = disconnected, Text = "NtfyTray · 正在启动", ContextMenuStrip = menu, Visible = true };
    }

    public Task UpdateAsync(TrayStatus value) => dispatcher.InvokeAsync(() =>
    {
        if (isStopping) return false;
        status.Text = value.Text;
        var tooltip = "NtfyTray · " + value.Text;
        icon.Text = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";
        icon.Icon = value.IsReady ? connected : disconnected;
        return true;
    });

    public Task StopAsync()
    {
        if (stopTask is not null) return stopTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopTask = completion.Task;
        _ = CompleteStopAsync(completion);
        return stopTask;
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try { await StopCoreAsync(); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task StopCoreAsync()
    {
        isStopping = true; test.Enabled = false; exit.Enabled = false;
        status.Text = "正在退出"; icon.Text = "NtfyTray · 正在退出"; icon.Icon = disconnected;
        try
        {
            stopping.Cancel();
            await stop();
            try { await activeTest; } catch (OperationCanceledException) { } catch { }
        }
        finally
        {
            try { dispose(); }
            finally
            {
                icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose();
                connected.Dispose(); disconnected.Dispose(); stopping.Dispose();
                dispatcher.Dispose(); base.ExitThreadCore();
            }
        }
    }

    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { status.Text = "无法打开文件或目录，请检查路径"; }
    }

    private static Icon LoadIcon(string name)
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream($"NtfyTray.Assets.{name}.ico")
            ?? throw new InvalidOperationException("Missing tray icon resource");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
