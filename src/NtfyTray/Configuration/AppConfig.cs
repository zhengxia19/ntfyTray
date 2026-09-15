using Serilog.Events;

namespace NtfyTray.Configuration;

public sealed record AppConfig(int Version, ServerConfig Server, IReadOnlyList<string> Subscriptions,
    NotificationConfig Notification, ConnectionConfig Connection, ReconnectConfig Reconnect, LoggingConfig Logging);
public sealed record ServerConfig(Uri Url, string? Token)
{
    public override string ToString() => "ServerConfig { credentials redacted }";
}
public sealed record NotificationConfig(bool Enabled = true, string DefaultTitle = "NtfyTray", bool IncludeTopic = true);
public sealed record ConnectionConfig(TimeSpan ConnectTimeout, TimeSpan IdleTimeout);
public sealed record ReconnectConfig(TimeSpan InitialDelay, TimeSpan MaxDelay, double JitterRatio);
public sealed record LoggingConfig(LogEventLevel Level, long MaxFileSizeBytes, int RetainedFileCount);

public sealed class ConfigException(string field, string reason, long? line = null)
    : Exception($"{field}: {reason}" + (line is null ? "" : $" (line {line})"))
{
    public string Field { get; } = field;
    public long? Line { get; } = line;
}
