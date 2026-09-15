using System.Text.Json;
using NtfyTray.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace NtfyTray.Infrastructure;

public enum LogEventKind
{
    LoggingStarted, ConfigurationAccepted, ConfigurationRejected, TopicConnecting, TopicConnected,
    TopicReconnecting, TopicFaulted, TopicStopped, HttpFailure, RetryScheduled, MessageReceived,
    MessageSubmitted, MessageSuppressed, MessageDropped, DuplicateMessage, CacheTruncated,
    CursorFallback, Heartbeat, Stopping, Exiting, TrayStarted, NotificationActivated, BackgroundFailure, InvalidEvent, TransportFailure, NotificationApiInvoked
}

/// <summary>Own one instance for the process lifetime. Faults remain latched until process restart.</summary>
public sealed class LoggingSetup : IDisposable, ILoggingFailureListener
{
    private static readonly string[] SafeConfigurationReasons =
    [
        "file is missing or unreadable", "invalid YAML or duplicate key", "exactly one document is required", "must be 1",
        "must be an HTTPS root URL without credentials, path, query or fragment", "must not contain control characters",
        "must contain 1 to 20 topics", "invalid topic", "duplicate topic", "must exceed connect timeout",
        "must be at least initial delay", "must be in [0, 1]", "must convert to a positive Int64 byte count",
        "must be a positive Int32 integer", "unsupported log level", "must be a mapping", "unknown field", "duplicate key",
        "required field is missing", "must be a string", "must be a finite number", "integer tag requires an integer",
        "must be positive and fit a timer duration", "must be a boolean", "YAML tag does not match field type"
    ];
    private readonly object gate = new();
    private Logger? logger;
    private int fault;
    private bool disposed;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtfyTray", "logs");
    public string DirectoryPath { get; }
    public bool HasFault => Volatile.Read(ref fault) != 0;
    public bool InitialWriteSucceeded { get; private set; }
    public event Action? Faulted;

    public LoggingSetup(string? directory = null)
    {
        DirectoryPath = directory ?? DefaultDirectory;
        ApplyConfiguration(new LoggingConfig(LogEventLevel.Information, 5 * 1024 * 1024, 14));
        Write(LogEventKind.LoggingStarted);
        InitialWriteSucceeded = !HasFault;
    }

    public void ApplyConfiguration(LoggingConfig config)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                logger?.Dispose();
                logger = null;
                Directory.CreateDirectory(DirectoryPath);
                logger = new LoggerConfiguration().MinimumLevel.Is(config.Level)
                    .WriteTo.Fallible(sinks => sinks.File(new SafeJsonFormatter(), Path.Combine(DirectoryPath, "ntfytray-.log"),
                        rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: config.MaxFileSizeBytes,
                        retainedFileCountLimit: config.RetainedFileCount, buffered: false), this)
                    .CreateLogger();
            }
            catch (Exception) { MarkFault(); }
        }
    }

    public void Write(LogEventKind kind, LogEventLevel level = LogEventLevel.Information, string? topic = null,
        string? messageId = null, int? httpStatus = null, int? retryAttempt = null, double? retryDelayMs = null, double? durationMs = null)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == LogEventKind.Heartbeat && level >= LogEventLevel.Information) level = LogEventLevel.Debug;
        lock (gate)
        {
            if (disposed) return;
            try
            {
                logger?.ForContext("event", kind.ToString()).ForContext("topic", topic).ForContext("message_id", messageId)
                    .ForContext("http_status", httpStatus).ForContext("retry_attempt", retryAttempt)
                    .ForContext("retry_delay_ms", retryDelayMs).ForContext("duration_ms", durationMs)
                    .Write(level, "{event}");
            }
            catch (Exception) { MarkFault(); }
        }
    }

    public void Startup(Version version, string configPath)
    {
        lock (gate)
        {
            if (disposed) return;
            try { logger?.ForContext("event", "Startup").ForContext("version", version.ToString()).ForContext("config_path", configPath).Information("{event}"); }
            catch (Exception) { MarkFault(); }
        }
    }

    public void ConfigurationRejected(ConfigException error)
    {
        // Only known field/reason literals are retained, never arbitrary exception text/YAML.
        var field = System.Text.RegularExpressions.Regex.IsMatch(error.Field, @"\A(?:config\.yaml|version|server(?:\.(?:url|token))?|subscriptions(?:\[\d+\])?|notification(?:\.(?:enabled|default_title|include_topic))?|connection(?:\.(?:connect_timeout_seconds|idle_timeout_seconds))?|reconnect(?:\.(?:initial_delay_seconds|max_delay_seconds|jitter_ratio))?|logging(?:\.(?:level|max_file_size_mb|retained_file_count))?|\$)\z") ? error.Field : "config.yaml";
        var reason = SafeConfigurationReasons.FirstOrDefault(candidate => error.Message == $"{error.Field}: {candidate}" + (error.Line is null ? "" : $" (line {error.Line})")) ?? "configuration is invalid";
        lock (gate)
        {
            if (disposed) return;
            try { logger?.ForContext("event", "ConfigurationRejected").ForContext("field", field).ForContext("reason", reason).ForContext("line", error.Line).Warning("{event}"); }
            catch (Exception) { MarkFault(); }
        }
    }

    void ILoggingFailureListener.OnLoggingFailed(object sender, LoggingFailureKind kind, string message, IReadOnlyCollection<LogEvent>? events, Exception? exception) => MarkFault();

    private void MarkFault()
    {
        if (Interlocked.Exchange(ref fault, 1) != 0) return;
        // This channel must never write to the logger or propagate subscriber exceptions into the sink.
        foreach (var handler in Faulted?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { logger?.Dispose(); } catch (Exception) { MarkFault(); }
            logger = null;
        }
    }

    private sealed class SafeJsonFormatter : ITextFormatter
    {
        private static readonly string[] Allowed = ["event", "topic", "message_id", "http_status", "retry_attempt", "retry_delay_ms", "duration_ms", "version", "config_path", "field", "reason", "line"];
        public void Format(LogEvent logEvent, TextWriter output)
        {
            var data = new Dictionary<string, object?> { ["timestamp"] = logEvent.Timestamp, ["level"] = logEvent.Level.ToString() };
            foreach (var key in Allowed)
                if (logEvent.Properties.TryGetValue(key, out var value) && value is ScalarValue scalar && scalar.Value is not null)
                    data[key] = scalar.Value;
            output.WriteLine(JsonSerializer.Serialize(data));
        }
    }
}
