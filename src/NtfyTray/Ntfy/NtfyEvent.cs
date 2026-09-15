using System.Text.Json;

namespace NtfyTray.Ntfy;

public enum NtfyEventKind { Open, Keepalive, Message }

public sealed record NtfyEvent(NtfyEventKind Kind, string? Id, string Topic, string? Message, string? Title, long? ServerTime)
{
    public static bool TryParse(string json, string expectedTopic, out NtfyEvent? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var type = GetString(root, "event");
            var kind = type switch { "open" => NtfyEventKind.Open, "keepalive" => NtfyEventKind.Keepalive, "message" => NtfyEventKind.Message, _ => (NtfyEventKind?)null };
            var topic = GetString(root, "topic");
            var id = GetString(root, "id");
            var message = GetString(root, "message");
            if (kind is null || topic != expectedTopic || string.IsNullOrWhiteSpace(id)) return false;
            if (kind == NtfyEventKind.Message && message is null) return false;
            long? time = root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var seconds) && seconds >= 0 ? seconds : null;
            value = new(kind.Value, id, topic!, message, GetString(root, "title"), time);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
