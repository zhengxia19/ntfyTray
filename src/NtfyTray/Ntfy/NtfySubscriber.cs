using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using NtfyTray.Configuration;
using NtfyTray.Infrastructure;
using NtfyTray.Notifications;
using Serilog.Events;

namespace NtfyTray.Ntfy;

public sealed class NtfySubscriber
{
    private readonly AppConfig config;
    private readonly string topic;
    private readonly HttpClient client;
    private readonly INotificationService notifications;
    private readonly LoggingSetup log;
    private readonly TimeProvider clock;
    private readonly ReconnectPolicy retry;
    private readonly SessionRecovery recovery;
    private readonly object gate = new();
    private readonly SemaphoreSlim reconnectSignal = new(0, 1);
    private CancellationTokenSource? connection;
    private int started;
    private TopicStatus state = new(SubscriptionState.Stopped);
    public TopicStatus State => Volatile.Read(ref state);
    public event Action<TopicStatus>? StateChanged;

    public NtfySubscriber(AppConfig config, string topic, HttpClient client, INotificationService notifications,
        LoggingSetup log, TimeProvider? clock = null, Func<double>? random = null)
    {
        this.config = config; this.topic = topic; this.client = client; this.notifications = notifications; this.log = log;
        this.clock = clock ?? TimeProvider.System;
        recovery = new(topic);
        retry = new(this.clock, random, initialSeconds: config.Reconnect.InitialDelay.TotalSeconds,
            maxSeconds: config.Reconnect.MaxDelay.TotalSeconds, jitterRatio: config.Reconnect.JitterRatio);
    }

    public void RequestReconnect()
    {
        lock (gate)
        {
            if (State.State is SubscriptionState.Stopped or SubscriptionState.Faulted) return;
            if (reconnectSignal.CurrentCount == 0) reconnectSignal.Release();
            try { connection?.Cancel(); }
            catch (AggregateException) { log.Write(LogEventKind.TransportFailure, LogEventLevel.Warning, topic); }
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Subscriber already started.");
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                SetState(SubscriptionState.Connecting, State.Fault);
                RetryDecision decision;
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    lock (gate) connection = attempt;
                    try { decision = await ConnectAsync(attempt.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { decision = new(RetryKind.Retry, retry.NextDelay()); }
                    catch (HttpRequestException error)
                    {
                        var fault = error.HttpRequestError == HttpRequestError.SecureConnectionError ? "TLS证书或安全连接错误" : "网络连接错误";
                        SetState(SubscriptionState.Reconnecting, fault);
                        log.Write(LogEventKind.TransportFailure, LogEventLevel.Warning, topic);
                        decision = new(RetryKind.Retry, retry.NextDelay());
                    }
                    catch (IOException)
                    {
                        log.Write(LogEventKind.TransportFailure, LogEventLevel.Warning, topic);
                        decision = new(RetryKind.Retry, retry.NextDelay());
                    }
                    finally { lock (gate) connection = null; }
                }
                token.ThrowIfCancellationRequested();
                retry.MarkDisconnected();
                if (decision.Kind == RetryKind.CursorRejected)
                {
                    if (recovery.RejectCurrentCursor())
                    {
                        log.Write(LogEventKind.CursorFallback, LogEventLevel.Warning, topic);
                        continue;
                    }
                    decision = new(RetryKind.Configuration, TimeSpan.Zero);
                }
                if (decision.Kind == RetryKind.Configuration)
                {
                    SetState(SubscriptionState.Faulted, "订阅地址或代理配置错误，修改后重启");
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                SetState(SubscriptionState.Reconnecting, decision.Kind == RetryKind.Permission ? "订阅认证或权限错误" : State.Fault);
                log.Write(LogEventKind.RetryScheduled, LogEventLevel.Warning, topic, retryAttempt: retry.Attempt, retryDelayMs: decision.Delay.TotalMilliseconds);
                await WaitRetryAsync(decision.Delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            SetState(SubscriptionState.Faulted, "订阅内部错误，重启后重试");
            log.Write(LogEventKind.TransportFailure, LogEventLevel.Error, topic);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        finally { SetState(SubscriptionState.Stopped); lock (gate) reconnectSignal.Dispose(); }
    }

    private async Task<RetryDecision> ConnectAsync(CancellationToken token)
    {
        var relative = Uri.EscapeDataString(topic) + "/sse";
        if (recovery.Since is { } since) relative += "?since=" + Uri.EscapeDataString(since);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(config.Server.Url, relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(config.Server.Token)) request.Headers.Authorization = new("Bearer", config.Server.Token);
        HttpResponseMessage response;
        using (var budget = new CancellationTokenSource(config.Connection.ConnectTimeout, clock))
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token))
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        using (response)
        using (var idle = new CancellationTokenSource(config.Connection.IdleTimeout, clock))
        using (var reading = CancellationTokenSource.CreateLinkedTokenSource(token, idle.Token))
        {
            if (!response.IsSuccessStatusCode)
            {
                log.Write(LogEventKind.HttpFailure, LogEventLevel.Warning, topic, httpStatus: (int)response.StatusCode);
                string? body = null;
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    using var errorStream = await response.Content.ReadAsStreamAsync(reading.Token).ConfigureAwait(false);
                    var buffer = new byte[4097];
                    int length = 0, count;
                    while (length < buffer.Length && (count = await errorStream.ReadAsync(buffer.AsMemory(length), reading.Token).ConfigureAwait(false)) != 0) length += count;
                    if (length <= 4096) body = Encoding.UTF8.GetString(buffer, 0, length);
                }
                var header = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
                return retry.ClassifyHttp(response.StatusCode, header, body);
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                return new(RetryKind.Configuration, TimeSpan.Zero);
            if (response.Headers.TryGetValues("X-Messages-Truncated", out var truncated) && truncated.Any(SessionRecovery.ObserveTruncation))
                log.Write(LogEventKind.CacheTruncated, LogEventLevel.Warning, topic);
            using var stream = await response.Content.ReadAsStreamAsync(reading.Token).ConfigureAwait(false);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(reading.Token).ConfigureAwait(false))
            {
                var receivedAt = Stopwatch.GetTimestamp();
                if (!NtfyEvent.TryParse(item.Data, topic, out var value))
                {
                    log.Write(LogEventKind.InvalidEvent, LogEventLevel.Warning, topic);
                    continue;
                }
                idle.CancelAfter(config.Connection.IdleTimeout);
                retry.MarkConnected();
                recovery.ObserveValidEvent(value!);
                if (State.State != SubscriptionState.Connected) SetState(SubscriptionState.Connected);
                if (value!.Kind != NtfyEventKind.Message) { log.Write(LogEventKind.Heartbeat, topic: topic); continue; }
                if (recovery.IsDuplicate(value)) { log.Write(LogEventKind.DuplicateMessage, topic: topic, messageId: value.Id); continue; }
                var startedAt = clock.GetTimestamp();
                var formatted = NotificationFormatter.Format(value.Title, value.Message!, topic, config.Notification.DefaultTitle, config.Notification.IncludeTopic) with { Timing = new DeliveryTiming(receivedAt) };
                NotificationResult result;
                try
                {
                    result = await notifications.SubmitAsync(formatted, reading.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested) { throw; }
                catch (Exception) { result = NotificationResult.Dropped; }
                log.Write(result switch { NotificationResult.Submitted => LogEventKind.MessageSubmitted, NotificationResult.Suppressed => LogEventKind.MessageSuppressed, _ => LogEventKind.MessageDropped },
                    topic: topic, messageId: value.Id, durationMs: clock.GetElapsedTime(startedAt).TotalMilliseconds);
                recovery.CompleteMessage(value, result, reading.Token);
            }
        }
        return new(RetryKind.Retry, retry.NextDelay());
    }

    private async Task WaitRetryAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay == TimeSpan.MaxValue || delay > DateTimeOffset.MaxValue - clock.GetUtcNow())
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return;
        }
        var deadline = clock.GetUtcNow() + delay;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timer = Task.Delay(remaining > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : remaining, clock, waiting.Token);
            var signal = reconnectSignal.WaitAsync(waiting.Token);
            await Task.WhenAny(timer, signal).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(timer, signal).ConfigureAwait(false); }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        }
    }

    private void SetState(SubscriptionState next, string? fault = null)
    {
        var current = new TopicStatus(next, fault);
        Volatile.Write(ref state, current);
        log.Write(next switch { SubscriptionState.Connecting => LogEventKind.TopicConnecting, SubscriptionState.Connected => LogEventKind.TopicConnected,
            SubscriptionState.Reconnecting => LogEventKind.TopicReconnecting, SubscriptionState.Faulted => LogEventKind.TopicFaulted, _ => LogEventKind.TopicStopped }, topic: topic);
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
            try { ((Action<TopicStatus>)handler)(current); } catch (Exception) { }
    }
}




