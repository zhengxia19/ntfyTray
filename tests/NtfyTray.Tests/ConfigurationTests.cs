using NtfyTray.Configuration;
using Serilog.Events;

namespace NtfyTray.Tests;

public class ConfigurationTests
{
    private const string Minimal = "version: 1\nserver:\n  url: https://ntfy.example.com\nsubscriptions: [alpha]\n";

    [Fact]
    public void DefaultsMatchContract()
    {
        var config = ConfigLoader.Parse(Minimal);
        Assert.Null(config.Server.Token);
        Assert.Equal(new NotificationConfig(), config.Notification);
        Assert.Equal(TimeSpan.FromSeconds(15), config.Connection.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(150), config.Connection.IdleTimeout);
        Assert.Equal(new ReconnectConfig(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), .2), config.Reconnect);
        Assert.Equal(new LoggingConfig(LogEventLevel.Information, 5 * 1024 * 1024, 14), config.Logging);
    }

    [Theory]
    [InlineData("version: 2\nserver: {url: https://example.com}\nsubscriptions: [a]")]
    [InlineData("version: !!str 1\nserver: {url: https://example.com}\nsubscriptions: [a]")]
    [InlineData("server: {url: https://example.com}\nsubscriptions: [a]")]
    [InlineData("version: 1\nsubscriptions: [a]")]
    [InlineData("version: 1\nserver: {}\nsubscriptions: [a]")]
    [InlineData("version: 1\nserver: {url: https://example.com}")]
    [InlineData("version: 1\nversion: 1")]
    [InlineData("version: [")]
    [InlineData("version: 1\n  server: bad")]
    [InlineData("---\nversion: 1\n---\nversion: 1")]
    [InlineData("")]
    public void RejectsInvalidDocument(string yaml) => Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));

    [Theory]
    [InlineData("transport: sse")]
    [InlineData("notification: {enabled: 'true'}")]
    [InlineData("notification: {enabled: !!str true}")]
    [InlineData("notification: {default_title: !!int abc}")]
    [InlineData("notification: !!str {enabled: true}")]
    [InlineData("connection: {connect_timeout_seconds: !!int 1.5}")]
    [InlineData("logging: {level: !custom Information}")]
    [InlineData("notification: {enabled: 1}")]
    [InlineData("notification: {default_title: 12}")]
    [InlineData("notification: {enabled: true, enabled: false}")]
    [InlineData("notification: null")]
    [InlineData("notification: {secret-token-value: true}")]
    [InlineData("connection: {connect_timeout_seconds: 0}")]
    [InlineData("connection: {connect_timeout_seconds: -1}")]
    [InlineData("connection: {connect_timeout_seconds: '15'}")]
    [InlineData("connection: {connect_timeout_seconds: .nan}")]
    [InlineData("connection: {connect_timeout_seconds: 1e100}")]
    [InlineData("connection: {idle_timeout_seconds: 15}")]
    [InlineData("reconnect: {initial_delay_seconds: 31}")]
    [InlineData("reconnect: {jitter_ratio: -0.1}")]
    [InlineData("reconnect: {jitter_ratio: 1.1}")]
    [InlineData("reconnect: {jitter_ratio: .inf}")]
    [InlineData("logging: {level: banana}")]
    [InlineData("logging: {level: '2'}")]
    [InlineData("logging: {max_file_size_mb: 0}")]
    [InlineData("logging: {max_file_size_mb: 1e30}")]
    [InlineData("logging: {retained_file_count: 0}")]
    [InlineData("logging: {retained_file_count: 1.5}")]
    [InlineData("logging: {retained_file_count: 2147483648}")]
    public void RejectsInvalidOptionalFields(string extra) => Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Minimal + extra));

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://user:secret-token-value@example.com")]
    [InlineData("https://example.com/topic")]
    [InlineData("https://example.com/topic/..")]
    [InlineData("https://example.com/?token=secret-token-value")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("https://example.com?")]
    [InlineData("ftp://example.com")]
    public void RejectsInvalidUrlWithoutLeakingIt(string url)
    {
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Minimal.Replace("https://ntfy.example.com", url)));
        Assert.Equal("server.url", error.Field);
        Assert.DoesNotContain("secret-token-value", error.ToString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[alpha, alpha]")]
    [InlineData("['']")]
    [InlineData("['   ']")]
    [InlineData("[a/b]")]
    [InlineData("['a,b']")]
    [InlineData("['a?b']")]
    [InlineData("[null]")]
    [InlineData("[123]")]
    [InlineData("[{topic: alpha}]")]
    public void RejectsInvalidTopics(string topics) => Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Minimal.Replace("[alpha]", topics)));

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(21)]
    public void TopicCountBoundary(int count)
    {
        var yaml = Minimal.Replace("[alpha]", "[" + string.Join(",", Enumerable.Range(0, count).Select(i => "topic" + i)) + "]");
        if (count > 20) Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
        else Assert.Equal(count, ConfigLoader.Parse(yaml).Subscriptions.Count);
    }

    [Fact]
    public void CustomValuesAndEmptyTokenAreSupported()
    {
        var config = ConfigLoader.Parse(Minimal.Replace("url: https://ntfy.example.com", "url: https://ntfy.example.com/\n  token: ''") +
            "notification: {enabled: false, default_title: '中文', include_topic: false}\nconnection: {connect_timeout_seconds: 0.5, idle_timeout_seconds: 2}\nreconnect: {initial_delay_seconds: 2, max_delay_seconds: 2, jitter_ratio: 1}\nlogging: {level: Debug, max_file_size_mb: 0.5, retained_file_count: 2}");
        Assert.Equal("", config.Server.Token);
        Assert.Equal(new NotificationConfig(false, "中文", false), config.Notification);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.Connection.ConnectTimeout);
        Assert.Equal(new LoggingConfig(LogEventLevel.Debug, 524288, 2), config.Logging);
    }

    [Fact]
    public void LoaderUsesProvidedBaseDirectoryAndMissingFileIsSafe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NtfyTray-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Throws<ConfigException>(() => ConfigLoader.Load(directory));
            File.WriteAllText(Path.Combine(directory, "config.yaml"), Minimal);
            Assert.Single(ConfigLoader.Load(directory).Subscriptions);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config.yaml"), ConfigLoader.GetConfigPath());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DiagnosticsNeverIncludeRawYamlOrUnknownKey()
    {
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Minimal + "logging: {secret-token-value: x}"));
        Assert.DoesNotContain("secret-token-value", error.ToString());
        Assert.NotNull(error.Line);
        Assert.DoesNotContain("secret-token-value", new ServerConfig(new Uri("https://example.com"), "secret-token-value").ToString());
    }

    [Theory]
    [InlineData("!!int abc")]
    [InlineData("!!bool true")]
    [InlineData("!secret-token-value abc")]
    public void TokenTagsMustBeStrings(string token)
    {
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Minimal.Replace("url: https://ntfy.example.com", "url: https://ntfy.example.com\n  token: " + token)));
        Assert.Equal("server.token", error.Field);
        Assert.DoesNotContain("secret-token-value", error.ToString());
    }

    [Fact]
    public void MatchingExplicitTagsAreAccepted()
    {
        var config = ConfigLoader.Parse(Minimal.Replace("version: 1", "version: !!int 1").Replace("[alpha]", "!!seq [!!str 123]") + "notification: !!map {enabled: !!bool true, default_title: !!str 123}");
        Assert.Equal("123", config.Notification.DefaultTitle);
        Assert.Equal("123", config.Subscriptions[0]);
    }
}
