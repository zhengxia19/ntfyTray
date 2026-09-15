using NtfyTray.Ntfy;
using NtfyTray.Notifications;

namespace NtfyTray.Tests;

public sealed class SessionRecoveryTests
{
    private static NtfyEvent Event(string id = "id", long? time = null, NtfyEventKind kind = NtfyEventKind.Message) =>
        new(kind, id, "t", kind == NtfyEventKind.Message ? "body" : null, null, time);

    [Fact]
    public void NewSessionHasNoHistoryAndUsesFirstAvailableServerTime()
    {
        var state = new SessionRecovery("t");
        Assert.Null(state.Since);
        state.ObserveValidEvent(Event("open-id", kind: NtfyEventKind.Open));
        Assert.Null(state.Since);
        state.ObserveValidEvent(Event("heartbeat-id", 123, NtfyEventKind.Keepalive));
        state.ObserveValidEvent(Event("later", 456, NtfyEventKind.Open));
        Assert.Equal("123", state.Since);
        Assert.Null(state.LastCompletedMessageId);
        Assert.Equal(0, state.RecentCount);
        Assert.Null(new SessionRecovery("t").Since);
    }

    [Theory]
    [InlineData(NotificationResult.Submitted)]
    [InlineData(NotificationResult.Suppressed)]
    [InlineData(NotificationResult.Dropped)]
    public void OnlyCompletedBusinessMessagesAdvance(NotificationResult result)
    {
        var state = new SessionRecovery("t");
        var message = Event();
        state.ObserveValidEvent(message);
        Assert.Null(state.Since);
        Assert.False(state.IsDuplicate(message));
        Assert.True(state.CompleteMessage(message, result));
        Assert.True(state.IsDuplicate(message));
        Assert.Equal("id", state.Since);
        Assert.True(state.CompleteMessage(Event("next"), result));
        Assert.False(state.CompleteMessage(message, result));
        Assert.Equal("next", state.Since);
        Assert.Equal(2, state.RecentCount);
    }

    [Fact]
    public void CancellationAndInvalidResultDoNotAdvance()
    {
        var state = new SessionRecovery("t");
        Assert.ThrowsAny<OperationCanceledException>(() => state.CompleteMessage(Event(), NotificationResult.Submitted, new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.CompleteMessage(Event(), (NotificationResult)99));
        Assert.Throws<ArgumentException>(() => state.CompleteMessage(Event(kind: NtfyEventKind.Open), NotificationResult.Submitted));
        Assert.Null(state.Since);
        Assert.Equal(0, state.RecentCount);
    }

    [Fact]
    public void CursorRejectionFallsBackToTimeThenLiveOnlyOnce()
    {
        var state = new SessionRecovery("t");
        state.ObserveValidEvent(Event(time: 123, kind: NtfyEventKind.Open));
        state.CompleteMessage(Event("done"), NotificationResult.Submitted);
        Assert.Equal("done", state.Since);
        Assert.True(state.RejectCurrentCursor());
        Assert.Equal("123", state.Since);
        Assert.True(state.RejectCurrentCursor());
        Assert.Null(state.Since);
        Assert.False(state.RejectCurrentCursor());
        Assert.False(state.CompleteMessage(Event("done"), NotificationResult.Submitted));
        Assert.Null(state.Since);
        state.CompleteMessage(Event("new"), NotificationResult.Submitted);
        Assert.Equal("new", state.Since);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingIdOrTimeAvoidsRedundantFallback(bool timeOnly)
    {
        var state = new SessionRecovery("t");
        if (timeOnly) state.ObserveValidEvent(Event(time: 123, kind: NtfyEventKind.Open));
        else state.CompleteMessage(Event(), NotificationResult.Dropped);
        Assert.True(state.RejectCurrentCursor());
        Assert.Null(state.Since);
        Assert.False(state.RejectCurrentCursor());
    }

    [Fact]
    public void TruncationPreservesCursorAndDeduplication()
    {
        var state = new SessionRecovery("t");
        state.CompleteMessage(Event(), NotificationResult.Submitted);
        Assert.True(SessionRecovery.ObserveTruncation("1"));
        Assert.False(SessionRecovery.ObserveTruncation(null));
        Assert.Equal("id", state.Since);
        Assert.Equal(1, state.RecentCount);
        Assert.True(state.IsDuplicate(Event()));
        state.CompleteMessage(Event("live"), NotificationResult.Submitted);
        Assert.Equal("live", state.Since);
    }

    [Fact]
    public void DeduplicationEvictsAt2048AndTopicsAreIsolated()
    {
        var state = new SessionRecovery("t");
        for (int i = 0; i <= 2048; i++) state.CompleteMessage(Event(i.ToString()), NotificationResult.Submitted);
        Assert.Equal(2048, state.RecentCount);
        Assert.False(state.IsDuplicate(Event("0")));
        Assert.True(state.IsDuplicate(Event("1")));
        Assert.False(new SessionRecovery("t").IsDuplicate(Event("1")));
        Assert.Throws<ArgumentException>(() => state.ObserveValidEvent(Event() with { Topic = "other" }));
    }
}
