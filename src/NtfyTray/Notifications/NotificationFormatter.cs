using System.Globalization;

namespace NtfyTray.Notifications;

public static class NotificationFormatter
{
    public const int TitleLimit = 128;
    public const int BodyLimit = 1024;

    public static FormattedNotification Format(
        string? title, string message, string topic, string defaultTitle, bool includeTopic)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(defaultTitle);

        var formattedTitle = Truncate(string.IsNullOrWhiteSpace(title) ? defaultTitle : title, TitleLimit);
        var source = includeTopic ? Truncate("\n来源：" + topic, BodyLimit) : string.Empty;
        var messageBudget = BodyLimit - new StringInfo(source).LengthInTextElements;
        return new FormattedNotification(formattedTitle, Truncate(message, messageBudget) + source);
    }

    private static string Truncate(string text, int limit)
    {
        if (limit == 0)
        {
            return string.Empty;
        }

        var elements = StringInfo.ParseCombiningCharacters(text);
        return elements.Length <= limit ? text : text[..elements[limit - 1]] + "…";
    }
}
