using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using NtfyTray.Configuration;
using NtfyTray.Infrastructure;
using NtfyTray.Ntfy;
using NtfyTray.Notifications;
using Serilog.Events;

namespace NtfyTray.Tests;

public sealed class SubscriptionTests
{
    private static AppConfig Config(params string[] topics) => new(1, new(new Uri("https://example.test/"), "secret"), topics,
        new(), new(TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(5)), new(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100), 0), new(LogEventLevel.Debug, 1024 * 1024, 3));
    private static string Event(string topic = "t", string id = "id", string kind = "message", long time = 123) =>
        $$"""data: {"event":"{{kind}}","id":"{{id}}","topic":"{{topic}}","time":{{time}},"message":"hello"}""" + "\n\n";
    private static HttpResponseMessage Response(int status, string? body = null, string media = "text/event-stream")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body ?? "") };
        response.Content.Headers.ContentType = new(media);
        return response;
    }
    private static HttpResponseMessage StreamResponse(FeedStream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private static LoggingSetup Log() => new(Path.Combine(Path.GetTempPath(), "ntfy-subscription-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task HeadersBudgetEndsBeforeStreamingAndValidEventsConfirmConnection()
    {
        using var log = Log(); using var stream = new FeedStream();
        using var handler = new Handler((_, _) => Task.FromResult(StreamResponse(stream)));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var manager = new SubscriptionManager(Config("t"), new Notifications(), log, client);
        manager.Start(); await Until(() => handler.Calls == 1);
        await Task.Delay(160);
        Assert.Equal(SubscriptionState.Connecting, manager.States["t"].State);
        stream.Push(Event(kind: "open")); await Until(() => manager.States["t"].State == SubscriptionState.Connected);
        await Task.Delay(160);
        Assert.Equal(1, handler.Calls);
        Assert.False(stream.Disposed);
        await manager.StopAsync(); Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SequentialMessagesDeduplicateAndResumeWithCompletedId()
    {
        using var log = Log(); var notification = new Notifications();
        using var stream = new FeedStream();
        var requests = new ConcurrentQueue<string>();
        using var handler = new Handler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("secret", request.Headers.Authorization.Parameter);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "text/event-stream");
            if (requests.Count == 1) return Task.FromResult(Response(200, Event(id: "a") + Event(id: "a") + Event(id: "b")));
            return Task.FromResult(StreamResponse(stream));
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var manager = new SubscriptionManager(Config("t"), notification, log, client);
        manager.Start(); await Until(() => requests.Count == 2);
        Assert.Equal(2, notification.Count);
        Assert.Equal(new[] { "/t/sse", "/t/sse?since=b" }, requests.ToArray());
    }

    [Theory]
    [InlineData(301, "text/event-stream")]
    [InlineData(404, "text/event-stream")]
    [InlineData(400, "application/json")]
    [InlineData(200, "text/html")]
    public async Task ConfigurationErrorsStayFaultedDespiteRecovery(int status, string media)
    {
        using var log = Log();
        using var handler = new Handler((_, _) => Task.FromResult(Response(status, "{}", media)));
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("t"), new Notifications(), log, client);
        manager.Start(); await Until(() => manager.States["t"].State == SubscriptionState.Faulted);
        for (int i = 0; i < 10; i++) manager.RequestReconnectAll();
        await Task.Delay(80); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(403, 300)]
    [InlineData(401, 300)]
    [InlineData(429, 120)]
    [InlineData(503, 30)]
    public async Task RecoverySignalsPreserveDeadlineAndAccountForSleep(int status, int seconds)
    {
        using var log = Log(); var clock = new OffsetClock();
        using var handler = new Handler((_, _) => { var response = Response(status); if (status == 429) response.Headers.TryAddWithoutValidation("Retry-After", "120"); return Task.FromResult(response); });
        using var client = new HttpClient(handler);
        var config = Config("t") with { Reconnect = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 0) };
        await using var manager = new SubscriptionManager(config, new Notifications(), log, client, clock);
        manager.Start(); await Until(() => manager.States["t"].State == SubscriptionState.Reconnecting);
        for (int i = 0; i < 10; i++) manager.RequestReconnectAll();
        await Task.Delay(80); Assert.Equal(1, handler.Calls);
        clock.Advance(seconds - 1); manager.RequestReconnectAll();
        await Task.Delay(80); Assert.Equal(1, handler.Calls);
        clock.Advance(2); manager.RequestReconnectAll();
        await Until(() => handler.Calls == 2);
    }

    [Fact]
    public async Task CursorRejectionIsBoundedAndTruncationContinuesStream()
    {
        using var log = Log(); using var last = new FeedStream(); var requests = new ConcurrentQueue<string>(); var notifications = new Notifications();
        using var handler = new Handler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.Query);
            var n = requests.Count;
            if (n == 1) return Task.FromResult(Response(200, Event(id: "old")));
            if (n is 2 or 3) return Task.FromResult(Response(400, "{\"code\":40008,\"http\":400}", "application/json"));
            var response = StreamResponse(last); response.Headers.Add("X-Messages-Truncated", "1"); return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("t"), notifications, log, client);
        manager.Start(); await Until(() => requests.Count == 4);
        Assert.Equal(new[] { "", "?since=old", "?since=123", "" }, requests.ToArray());
        last.Push(Event(id: "old") + Event(id: "new")); await Until(() => notifications.Count == 2);
        Assert.Equal(4, handler.Calls); Assert.False(last.Disposed);
    }

    [Fact]
    public async Task TopicFailuresAreIsolatedAndRecoveryNeverOverlapsStreams()
    {
        using var log = Log(); var streams = new ConcurrentBag<FeedStream>(); int active = 0, maximum = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/bad/sse") return Task.FromResult(Response(403));
            var count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
            var stream = new FeedStream(() => Interlocked.Decrement(ref active)); streams.Add(stream);
            stream.Push(Event("good", kind: "open")); return Task.FromResult(StreamResponse(stream));
        });
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("bad", "good"), new Notifications(), log, client);
        manager.Start(); manager.Start(); await Until(() => manager.States["good"].State == SubscriptionState.Connected);
        Assert.Equal(SubscriptionState.Reconnecting, manager.States["bad"].State);
        for (int i = 0; i < 20; i++) manager.RequestReconnectAll();
        await Until(() => streams.Count >= 2);
        await manager.StopAsync(); Assert.Equal(1, maximum); Assert.Equal(0, active);
    }

    [Fact]
    public async Task InvalidEventsDoNotResetIdleTimeoutAndHeaderWaitCancels()
    {
        using var log = Log(); using var stream = new FeedStream();
        using var handler = new Handler((_, _) => Task.FromResult(StreamResponse(stream)));
        using var client = new HttpClient(handler);
        var config = Config("t") with { Connection = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(130)), Reconnect = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 0) };
        await using (var manager = new SubscriptionManager(config, new Notifications(), log, client))
        {
            manager.Start(); await Until(() => handler.Calls == 1);
            for (int i = 0; i < 8; i++) { stream.Push("data: garbage\n\n"); await Task.Delay(30); }
            Assert.Equal(SubscriptionState.Reconnecting, manager.States["t"].State);
            Assert.True(stream.Disposed);
        }
        using var blocked = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(200); });
        using var blockedClient = new HttpClient(blocked);
        await using var pending = new SubscriptionManager(config, new Notifications(), log, blockedClient);
        pending.Start(); await Until(() => pending.States["t"].State == SubscriptionState.Reconnecting);
        await pending.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HeartbeatsRefreshIdleBudgetWithoutBusinessMessages()
    {
        using var log = Log(); using var stream = new FeedStream(); var notifications = new Notifications();
        using var handler = new Handler((_, _) => Task.FromResult(StreamResponse(stream)));
        using var client = new HttpClient(handler);
        var config = Config("t") with { Connection = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200)) };
        await using var manager = new SubscriptionManager(config, notifications, log, client);
        manager.Start(); await Until(() => handler.Calls == 1);
        for (int i = 0; i < 6; i++)
        {
            stream.Push(Event(id: i.ToString(), kind: "keepalive"));
            await Task.Delay(60);
        }
        Assert.Equal(1, handler.Calls);
        Assert.Equal(SubscriptionState.Connected, manager.States["t"].State);
        Assert.Equal(0, notifications.Count);
    }

    [Fact]
    public async Task DisabledNotificationsStillInvokeServiceForSettingsRefresh()
    {
        using var log = Log(); var notifications = new Notifications();
        using var handler = new Handler((_, _) => Task.FromResult(Response(200, Event())));
        using var client = new HttpClient(handler);
        var config = Config("t") with { Notification = new(false) };
        await using var manager = new SubscriptionManager(config, notifications, log, client);
        manager.Start(); await Until(() => notifications.Count == 1);
        await manager.StopAsync(); Assert.Equal(1, notifications.Count);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task TransportErrorsRetryWithoutRelaxingSecurity(HttpRequestError error)
    {
        using var log = Log();
        using var handler = new Handler((_, _) => throw new HttpRequestException(error, "sensitive error"));
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("t"), new Notifications(), log, client);
        manager.Start(); await Until(() => handler.Calls >= 2);
        await manager.StopAsync();
        log.Dispose();
        var text = string.Join("", Directory.GetFiles(log.DirectoryPath).Select(File.ReadAllText));
        Assert.DoesNotContain("sensitive error", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task InterruptedNotificationDoesNotAdvanceBusinessCursor()
    {
        using var log = Log(); var requests = new ConcurrentQueue<string>(); using var stream = new FeedStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new BlockingNotifications(entered);
        using var handler = new Handler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.Query);
            return Task.FromResult(requests.Count == 1 ? Response(200, Event(id: "unfinished")) : StreamResponse(stream));
        });
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("t"), notifications, log, client);
        manager.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.RequestReconnectAll(); await Until(() => requests.Count == 2);
        Assert.Equal(new[] { "", "?since=123" }, requests.ToArray());
    }

    [Fact]
    public async Task NoTokenAndUnicodeTopicUseOneEncodedPathSegment()
    {
        using var log = Log(); using var stream = new FeedStream();
        string? uri = null; bool hasAuthorization = true;
        using var handler = new Handler((request, _) =>
        {
            uri = request.RequestUri!.AbsoluteUri;
            hasAuthorization = request.Headers.Contains("Authorization");
            return Task.FromResult(StreamResponse(stream));
        });
        using var client = new HttpClient(handler);
        var config = Config("中文 topic") with { Server = new(new Uri("https://example.test/"), null) };
        await using var manager = new SubscriptionManager(config, new Notifications(), log, client);
        manager.Start(); await Until(() => uri is not null);
        Assert.Equal("https://example.test/" + Uri.EscapeDataString("中文 topic") + "/sse", uri);
        Assert.False(hasAuthorization);
    }

    [Fact]
    public async Task ExitCancelsPendingHeadersWithoutWaitingForHeaderBudget()
    {
        using var log = Log();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) canceled.TrySetResult(); }
            return Response(200);
        });
        using var client = new HttpClient(handler);
        var config = Config("t") with { Connection = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150)) };
        await using var manager = new SubscriptionManager(config, new Notifications(), log, client);
        manager.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(canceled.Task.IsCompletedSuccessfully);
        Assert.Equal(SubscriptionState.Stopped, manager.States["t"].State);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LiveFallbackRejectionBecomesPermanentFault()
    {
        using var log = Log(); var requests = new ConcurrentQueue<string>();
        using var handler = new Handler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.Query);
            return Task.FromResult(requests.Count == 1 ? Response(200, Event(id: "done")) : Response(400, "{\"code\":40008,\"http\":400}", "application/json"));
        });
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("t"), new Notifications(), log, client);
        manager.Start(); await Until(() => manager.States["t"].State == SubscriptionState.Faulted);
        Assert.Equal(new[] { "", "?since=done", "?since=123", "" }, requests.ToArray());
        manager.RequestReconnectAll(); await Task.Delay(80); Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task NotificationExceptionIsDroppedAndCompletedCursorAdvances()
    {
        using var log = Log(); using var stream = new FeedStream(); var requests = new ConcurrentQueue<string>();
        using var handler = new Handler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.Query);
            return Task.FromResult(requests.Count == 1 ? Response(200, Event(id: "dropped")) : StreamResponse(stream));
        });
        using var client = new HttpClient(handler);
        await using var manager = new SubscriptionManager(Config("t"), new ThrowingNotifications(), log, client);
        manager.Start(); await Until(() => requests.Count == 2);
        Assert.Equal(new[] { "", "?since=dropped" }, requests.ToArray());
        await manager.StopAsync(); log.Dispose();
        var text = string.Join("", Directory.GetFiles(log.DirectoryPath).Select(File.ReadAllText));
        Assert.Contains("MessageDropped", text);
        Assert.DoesNotContain("sensitive notification exception", text);
    }

    private sealed class ThrowingNotifications : INotificationService
    {
        public Task<NotificationResult> SubmitAsync(FormattedNotification value, CancellationToken token) => throw new IOException("sensitive notification exception");
    }

    private sealed class BlockingNotifications(TaskCompletionSource entered) : INotificationService
    {
        public async Task<NotificationResult> SubmitAsync(FormattedNotification value, CancellationToken token)
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return NotificationResult.Submitted; }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int calls; public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Interlocked.Increment(ref calls); return send(request, token); }
    }
    private sealed class Notifications : INotificationService
    {
        private int count; public int Count => Volatile.Read(ref count);
        public Task<NotificationResult> SubmitAsync(FormattedNotification notification, CancellationToken token) { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref count); return Task.FromResult(NotificationResult.Submitted); }
    }
    private sealed class OffsetClock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset));
        public void Advance(int seconds) => Interlocked.Add(ref offset, TimeSpan.FromSeconds(seconds).Ticks);
    }
    private sealed class FeedStream(Action? onDispose = null) : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? current; private int position; private int disposed;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public void Push(string text) => chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (current is null || position == current.Length) { current = await chunks.Reader.ReadAsync(token); position = 0; }
            var count = Math.Min(buffer.Length, current.Length - position); current.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref disposed, 1) == 0) { onDispose?.Invoke(); chunks.Writer.TryComplete(); } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}






