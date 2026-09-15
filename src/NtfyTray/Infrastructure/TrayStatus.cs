using NtfyTray.Ntfy;

namespace NtfyTray.Infrastructure;

public sealed record ComponentFaults(
    bool Configuration = false,
    bool Logging = false,
    bool NotificationInitialization = false,
    bool NotificationSettings = false,
    bool NotificationSubmission = false);

public sealed record TrayStatus(bool IsReady, int ConnectedCount, int TotalCount, string Text)
{
    // A pure snapshot: each component retains ownership of its own fault/recovery state.
    public static TrayStatus Summarize(IReadOnlyCollection<TopicStatus> topics, ComponentFaults faults,
        bool notificationsEnabled = true, bool stopping = false)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(faults);
        var count = topics.Count(t => t.State == SubscriptionState.Connected);
        var total = topics.Count;
        var connection = $"已连接 {count}/{total}";
        if (stopping) return new(false, count, total, $"正在退出 · {connection}");

        var errors = new List<string>();
        if (faults.Configuration) errors.Add("配置错误，修改后重启");
        if (faults.Logging) errors.Add("日志故障，修复后重启");
        if (faults.NotificationInitialization) errors.Add("通知初始化故障，修复后重启");
        if (faults.NotificationSettings) errors.Add("系统通知受限");
        if (faults.NotificationSubmission) errors.Add("通知提交故障");
        foreach (var topic in topics)
        {
            if (!string.IsNullOrEmpty(topic.Fault)) errors.Add(topic.Fault);
            else if (topic.State == SubscriptionState.Faulted) errors.Add("订阅故障");
        }
        if (errors.Count > 0)
            return new(false, count, total, $"{string.Join("；", errors.Distinct(StringComparer.Ordinal))} · {connection}");
        if (total > 0 && count == total)
            return new(true, count, total, $"就绪 · {connection}" + (notificationsEnabled ? "" : " · 通知已按配置关闭"));
        var stage = count > 0 ? $"部分连接 {count}/{total}"
            : topics.Any(t => t.State == SubscriptionState.Reconnecting) ? "正在重连"
            : topics.Any(t => t.State == SubscriptionState.Connecting) ? "连接中" : "尚未就绪";
        return new(false, count, total, $"{stage} · {connection}");
    }
}
