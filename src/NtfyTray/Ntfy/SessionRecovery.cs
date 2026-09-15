using System.Globalization;
using NtfyTray.Infrastructure;
using NtfyTray.Notifications;

namespace NtfyTray.Ntfy;

/// <summary>In-memory state owned by one topic's sequential subscription loop.</summary>
public sealed class SessionRecovery
{
    private readonly string _topic;
    private readonly RecentMessageIds _recent = new();
    private RecoveryMode _mode;
    public string? LastCompletedMessageId { get; private set; }
    public long? FirstServerTime { get; private set; }
    public int RecentCount => _recent.Count;
    public string? Since => _mode switch
    {
        RecoveryMode.LiveOnly => null,
        RecoveryMode.ServerTime => FormatTime(),
        _ => LastCompletedMessageId ?? FormatTime()
    };

    public SessionRecovery(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _topic = topic;
    }

    public void ObserveValidEvent(NtfyEvent value)
    {
        Validate(value, requireMessage: false);
        if (FirstServerTime is null && value.ServerTime is >= 0)
            FirstServerTime = value.ServerTime;
    }

    public bool IsDuplicate(NtfyEvent value)
    {
        Validate(value, requireMessage: true);
        return _recent.Contains(value.Id!);
    }

    /// <summary>Call after recording Submitted, Suppressed, or Dropped; never before submission completes.</summary>
    public bool CompleteMessage(NtfyEvent value, NotificationResult result, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Validate(value, requireMessage: true);
        if (!Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(result));
        if (!_recent.TryAdd(value.Id!)) return false;
        LastCompletedMessageId = value.Id;
        // A newly completed message supplies a new usable recovery position.
        _mode = RecoveryMode.Preferred;
        return true;
    }

    /// <summary>Call only for a classified cursor rejection, never for generic HTTP errors or truncation.</summary>
    public bool RejectCurrentCursor()
    {
        if (Since is null) return false;
        if (_mode == RecoveryMode.Preferred && LastCompletedMessageId is not null && FirstServerTime is not null)
            _mode = RecoveryMode.ServerTime;
        else
            _mode = RecoveryMode.LiveOnly;
        return true;
    }

    /// <summary>Signals a safe diagnostic while leaving the successful stream and recovery state intact.</summary>
    public static bool ObserveTruncation(string? header) => header == "1";

    private string? FormatTime() => FirstServerTime?.ToString(CultureInfo.InvariantCulture);
    private void Validate(NtfyEvent value, bool requireMessage)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Kind) || value.Topic != _topic || string.IsNullOrWhiteSpace(value.Id)
            || (value.Kind == NtfyEventKind.Message && value.Message is null)
            || (requireMessage && value.Kind != NtfyEventKind.Message))
            throw new ArgumentException("Event does not satisfy this topic's validated event contract.", nameof(value));
    }

    private enum RecoveryMode { Preferred, ServerTime, LiveOnly }
}
