using NtfyTray.Infrastructure;
using NtfyTray.Ntfy;

namespace NtfyTray.Tests;

public class TrayStatusTests
{
    private static readonly TopicStatus[] Connected = [new(SubscriptionState.Connected), new(SubscriptionState.Connected)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllConnectedIsReadyWithEitherNotificationPreference(bool enabled)
    {
        var status = TrayStatus.Summarize(Connected, new(), enabled);
        Assert.True(status.IsReady);
        Assert.Contains("已连接 2/2", status.Text);
        Assert.Equal(!enabled, status.Text.Contains("通知已按配置关闭"));
    }

    [Fact]
    public void TopicRecoveryCannotHidePersistentLoggingFault()
    {
        var faults = new ComponentFaults(Logging: true);
        Assert.False(TrayStatus.Summarize([new(SubscriptionState.Reconnecting)], faults).IsReady);
        var recovered = TrayStatus.Summarize(Connected, faults);
        Assert.False(recovered.IsReady);
        Assert.Contains("日志故障", recovered.Text);
        Assert.Contains("2/2", recovered.Text);
    }

    [Fact]
    public void NotificationRecoveryCannotClearTopicPermissionFault()
    {
        TopicStatus[] topics = [new(SubscriptionState.Connected), new(SubscriptionState.Faulted, "权限错误")];
        var status = TrayStatus.Summarize(topics, new());
        Assert.False(status.IsReady);
        Assert.Contains("权限错误", status.Text);
        Assert.Contains("1/2", status.Text);
    }

    [Theory]
    [InlineData(SubscriptionState.Stopped, "尚未就绪")]
    [InlineData(SubscriptionState.Connecting, "连接中")]
    [InlineData(SubscriptionState.Reconnecting, "正在重连")]
    [InlineData(SubscriptionState.Faulted, "订阅故障")]
    public void TransitionsNeverShowReady(SubscriptionState state, string text)
    {
        var status = TrayStatus.Summarize([new(state)], new());
        Assert.False(status.IsReady);
        Assert.Contains(text, status.Text);
    }

    [Fact]
    public void StoppingAndEmptySubscriptionsAreNotReady()
    {
        Assert.False(TrayStatus.Summarize([], new()).IsReady);
        var stopping = TrayStatus.Summarize(Connected, new(), stopping: true);
        Assert.False(stopping.IsReady);
        Assert.Contains("正在退出", stopping.Text);
    }

    [Fact]
    public void EveryFaultSourceIndependentlyPreventsReady()
    {
        ComponentFaults[] cases = [new(Configuration: true), new(Logging: true),
            new(NotificationInitialization: true), new(NotificationSettings: true), new(NotificationSubmission: true)];
        foreach (var faults in cases) Assert.False(TrayStatus.Summarize(Connected, faults).IsReady);
        Assert.True(TrayStatus.Summarize(Connected, new()).IsReady);
    }

    [Fact]
    public void DisabledPreferenceDoesNotHideSystemRestriction()
    {
        var status = TrayStatus.Summarize(Connected, new(NotificationSettings: true), false);
        Assert.False(status.IsReady);
        Assert.Contains("系统通知受限", status.Text);
    }
}
