using System.Globalization;
using Serilog.Events;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NtfyTray.Configuration;

public static class ConfigLoader
{
    public static string GetConfigPath(string? baseDirectory = null) => Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "config.yaml");

    public static AppConfig Load(string? baseDirectory = null)
    {
        string yaml;
        try { yaml = File.ReadAllText(GetConfigPath(baseDirectory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ConfigException("config.yaml", "file is missing or unreadable"); }
        return Parse(yaml);
    }

    public static AppConfig Parse(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlException ex) { throw new ConfigException("config.yaml", "invalid YAML or duplicate key", ex.Start.Line); }
        if (stream.Documents.Count != 1) throw new ConfigException("config.yaml", "exactly one document is required");
        var root = Map(stream.Documents[0].RootNode, "$", "version", "server", "subscriptions", "notification", "connection", "reconnect", "logging");
        if (Number(Required(root, "version", "version"), "version") != 1) throw new ConfigException("version", "must be 1");
        var server = Map(Required(root, "server", "server"), "server", "url", "token");
        var urlText = Text(Required(server, "url", "server.url"), "server.url");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(urlText, @"\Ahttps://[^/\s?#\\]+/?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ConfigException("server.url", "must be an HTTPS root URL without credentials, path, query or fragment");
        var token = server.TryGetValue("token", out var tokenNode) ? Text(tokenNode, "server.token") : null;
        if (token is not null && token.Any(char.IsControl)) throw new ConfigException("server.token", "must not contain control characters");
        var subscriptionsNode = Required(root, "subscriptions", "subscriptions");
        CheckTag(subscriptionsNode, "subscriptions", "seq");
        if (subscriptionsNode is not YamlSequenceNode sequence || sequence.Children.Count is < 1 or > 20)
            throw new ConfigException("subscriptions", "must contain 1 to 20 topics", subscriptionsNode.Start.Line);
        var topics = new List<string>();
        foreach (var node in sequence.Children)
        {
            var topic = Text(node, $"subscriptions[{topics.Count}]");
            if (string.IsNullOrWhiteSpace(topic) || topic.IndexOfAny([',', '/', '?', '#', '\\']) >= 0 || topic.Any(char.IsControl) || topic is "." or "..")
                throw new ConfigException($"subscriptions[{topics.Count}]", "invalid topic", node.Start.Line);
            if (topics.Contains(topic, StringComparer.Ordinal)) throw new ConfigException("subscriptions", "duplicate topic", node.Start.Line);
            topics.Add(topic);
        }
        var notification = OptionalMap(root, "notification", "enabled", "default_title", "include_topic");
        var connection = OptionalMap(root, "connection", "connect_timeout_seconds", "idle_timeout_seconds");
        var reconnect = OptionalMap(root, "reconnect", "initial_delay_seconds", "max_delay_seconds", "jitter_ratio");
        var logging = OptionalMap(root, "logging", "level", "max_file_size_mb", "retained_file_count");
        var connect = Duration(connection, "connect_timeout_seconds", "connection", 15);
        var idle = Duration(connection, "idle_timeout_seconds", "connection", 150);
        if (idle <= connect) throw new ConfigException("connection.idle_timeout_seconds", "must exceed connect timeout");
        var initial = Duration(reconnect, "initial_delay_seconds", "reconnect", 1);
        var max = Duration(reconnect, "max_delay_seconds", "reconnect", 30);
        if (max < initial) throw new ConfigException("reconnect.max_delay_seconds", "must be at least initial delay");
        var jitter = Numeric(reconnect, "jitter_ratio", "reconnect", .2);
        if (jitter < 0 || jitter > 1) throw new ConfigException("reconnect.jitter_ratio", "must be in [0, 1]");
        var size = Numeric(logging, "max_file_size_mb", "logging", 5) * 1024 * 1024;
        if (size < 1 || size >= long.MaxValue) throw new ConfigException("logging.max_file_size_mb", "must convert to a positive Int64 byte count");
        var count = Numeric(logging, "retained_file_count", "logging", 14);
        if (count < 1 || count > int.MaxValue || count != Math.Truncate(count)) throw new ConfigException("logging.retained_file_count", "must be a positive Int32 integer");
        var levelText = logging.TryGetValue("level", out var levelNode) ? Text(levelNode, "logging.level") : "Information";
        if (!Enum.GetNames<LogEventLevel>().Contains(levelText, StringComparer.OrdinalIgnoreCase)) throw new ConfigException("logging.level", "unsupported log level");
        return new AppConfig(1, new ServerConfig(url, token), topics.AsReadOnly(),
            new NotificationConfig(Boolean(notification, "enabled", true), notification.TryGetValue("default_title", out var title) ? Text(title, "notification.default_title") : "NtfyTray", Boolean(notification, "include_topic", true)),
            new ConnectionConfig(connect, idle), new ReconnectConfig(initial, max, jitter),
            new LoggingConfig(Enum.Parse<LogEventLevel>(levelText, true), (long)size, (int)count));
    }

    private static Dictionary<string, YamlNode> OptionalMap(Dictionary<string, YamlNode> root, string key, params string[] allowed) =>
        root.TryGetValue(key, out var node) ? Map(node, key, allowed) : new();

    private static Dictionary<string, YamlNode> Map(YamlNode node, string field, params string[] allowed)
    {
        CheckTag(node, field, "map");
        if (node is not YamlMappingNode map) throw new ConfigException(field, "must be a mapping", node.Start.Line);
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in map.Children)
        {
            CheckTag(pair.Key, field, "str");
            if (pair.Key is not YamlScalarNode key || key.Value is null || !allowed.Contains(key.Value))
                throw new ConfigException(field, "unknown field", pair.Key.Start.Line);
            if (!result.TryAdd(key.Value, pair.Value)) throw new ConfigException(field, "duplicate key", pair.Key.Start.Line);
        }
        return result;
    }
    private static YamlNode Required(Dictionary<string, YamlNode> map, string key, string field) =>
        map.TryGetValue(key, out var node) ? node : throw new ConfigException(field, "required field is missing");
    private static string Text(YamlNode node, string field)
    {
        CheckTag(node, field, "str");
        if (node is not YamlScalarNode scalar || scalar.Value is null ||
            (scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain && (scalar.Value is "" or "~" or "null" or "Null" or "NULL" || bool.TryParse(scalar.Value, out _) || double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))))
            throw new ConfigException(field, "must be a string", node.Start.Line);
        return scalar.Value;
    }
    private static double Number(YamlNode node, string field)
    {
        CheckTag(node, field, "int", "float");
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain || !double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ConfigException(field, "must be a finite number", node.Start.Line);
        if (!scalar.Tag.IsEmpty && scalar.Tag.Value == "tag:yaml.org,2002:int" && value != Math.Truncate(value))
            throw new ConfigException(field, "integer tag requires an integer", node.Start.Line);
        return value;
    }
    private static double Numeric(Dictionary<string, YamlNode> map, string key, string prefix, double fallback) =>
        map.TryGetValue(key, out var node) ? Number(node, prefix + "." + key) : fallback;
    private static TimeSpan Duration(Dictionary<string, YamlNode> map, string key, string prefix, double fallback)
    {
        var seconds = Numeric(map, key, prefix, fallback);
        // Task.Delay and CancellationTokenSource timers use an unsigned 32-bit millisecond budget.
        if (seconds < .0000001 || seconds * 1000 > uint.MaxValue - 1) throw new ConfigException(prefix + "." + key, "must be positive and fit a timer duration");
        return TimeSpan.FromSeconds(seconds);
    }
    private static bool Boolean(Dictionary<string, YamlNode> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var node)) return fallback;
        CheckTag(node, "notification." + key, "bool");
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain || !bool.TryParse(scalar.Value, out var value))
            throw new ConfigException("notification." + key, "must be a boolean", node.Start.Line);
        return value;
    }

    private static void CheckTag(YamlNode node, string field, params string[] types)
    {
        if (!node.Tag.IsEmpty && !types.Any(type => node.Tag.Value == "tag:yaml.org,2002:" + type))
            throw new ConfigException(field, "YAML tag does not match field type", node.Start.Line);
    }
}
