using System.Text.Json;
using NtfyTray.Configuration;
using NtfyTray.Infrastructure;
using Serilog.Core;
using Serilog.Events;

namespace NtfyTray.Tests;

public sealed class LoggingSetupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "NtfyTray-logs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsAndFirstWriteAreReady()
    {
        using (var log = new LoggingSetup(directory))
        {
            Assert.True(log.InitialWriteSucceeded);
            Assert.False(log.HasFault);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtfyTray", "logs"), LoggingSetup.DefaultDirectory);
        }
        var file = Assert.Single(Directory.GetFiles(directory, "*.log"));
        Assert.Contains(DateTime.Now.ToString("yyyyMMdd"), Path.GetFileName(file));
        Assert.Contains("LoggingStarted", File.ReadAllText(file));
    }

    [Fact]
    public void SmallLimitRollsAndRetainsFiles()
    {
        using (var log = new LoggingSetup(directory))
        {
            log.ApplyConfiguration(new LoggingConfig(LogEventLevel.Debug, 300, 3));
            for (var i = 0; i < 40; i++) log.Write(LogEventKind.MessageReceived, messageId: "id" + i, topic: "topic");
            Assert.False(log.HasFault);
        }
        var files = Directory.GetFiles(directory, "*.log");
        Assert.Equal(3, files.Length);
        Assert.Contains(files, path => Path.GetFileName(path).Contains('_'));
        Assert.Contains("id39", string.Join("", files.Select(File.ReadAllText)));
        Assert.DoesNotContain("id0\"", string.Join("", files.Select(File.ReadAllText)));
    }

    [Fact]
    public void LevelAndHeartbeatFilteringAndFieldsAreCorrect()
    {
        using (var log = new LoggingSetup(directory))
        {
            log.Write(LogEventKind.Heartbeat);
            log.Write(LogEventKind.RetryScheduled, LogEventLevel.Warning, "topic", "id", 429, 2, 1000, 50);
            log.ApplyConfiguration(new LoggingConfig(LogEventLevel.Error, 10000, 2));
            log.Write(LogEventKind.MessageSubmitted);
            log.Write(LogEventKind.HttpFailure, LogEventLevel.Error);
        }
        var lines = Directory.GetFiles(directory, "*.log").SelectMany(File.ReadAllLines).ToArray();
        var text = string.Join("\n", lines);
        Assert.DoesNotContain("Heartbeat", text);
        Assert.DoesNotContain("MessageSubmitted", text);
        Assert.Contains("HttpFailure", text);
        using var retry = JsonDocument.Parse(lines.Single(line => line.Contains("RetryScheduled")));
        foreach (var field in new[] { "timestamp", "level", "event", "topic", "message_id", "http_status", "retry_attempt", "retry_delay_ms", "duration_ms" })
            Assert.True(retry.RootElement.TryGetProperty(field, out _));
        Assert.Equal(429, retry.RootElement.GetProperty("http_status").GetInt32());
    }

    [Fact]
    public void UnwritablePathLatchesAndNewInstanceStartsCleanAfterRepair()
    {
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "blocks a directory");
        using (var log = new LoggingSetup(blocked))
        {
            Assert.True(log.HasFault);
            Assert.False(log.InitialWriteSucceeded);
            File.Delete(blocked);
            log.ApplyConfiguration(new LoggingConfig(LogEventLevel.Information, 10000, 2));
            log.Write(LogEventKind.TopicConnected);
            Assert.True(log.HasFault);
            Assert.False(log.InitialWriteSucceeded);
        }
        using var restarted = new LoggingSetup(blocked);
        Assert.False(restarted.HasFault);
        Assert.True(restarted.InitialWriteSucceeded);
    }

    [Fact]
    public void ActualSinkOpenFailureIsReportedOnFirstWrite()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ntfytray-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        using var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var log = new LoggingSetup(directory);
        Assert.True(log.HasFault);
        Assert.False(log.InitialWriteSucceeded);
    }

    [Fact]
    public void FailureChannelLatchesOnceAndNeverLeaksFailurePayload()
    {
        using (var log = new LoggingSetup(directory))
        {
            var calls = 0;
            log.Faulted += () => { calls++; throw new IOException("secret callback payload"); };
            var listener = (ILoggingFailureListener)log;
            listener.OnLoggingFailed(this, LoggingFailureKind.Permanent, "token-secret title-secret body-secret", null, new IOException("raw-response-secret"));
            listener.OnLoggingFailed(this, LoggingFailureKind.Permanent, "secret", null, null);
            log.Write(LogEventKind.TopicConnected);
            Assert.Equal(1, calls);
            Assert.True(log.HasFault);
            log.ConfigurationRejected(new ConfigException("server.token", "token-secret title-secret body-secret"));
            log.ConfigurationRejected(new ConfigException("token-secret", "raw-response-secret"));
            log.ConfigurationRejected(new ConfigException("version", "must be 1", 1));
        }
        var text = string.Join("", Directory.GetFiles(directory, "*.log").Select(File.ReadAllText));
        Assert.DoesNotContain("secret", text);
        Assert.Contains("server.token", text);
        Assert.Contains("ConfigurationRejected", text);
        Assert.Contains("must be 1", text);
    }

    [Fact]
    public void StartupRecordsVersionAndConfigurationLocation()
    {
        using (var log = new LoggingSetup(directory)) log.Startup(new Version(1, 0, 0), Path.Combine(directory, "config.yaml"));
        var text = string.Join("", Directory.GetFiles(directory, "*.log").Select(File.ReadAllText));
        Assert.Contains("1.0.0", text);
        Assert.Contains("config.yaml", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
