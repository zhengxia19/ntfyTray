using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NtfyTray.Ntfy;

public enum RetryKind { Retry, Permission, Configuration, CursorRejected }
public sealed record RetryDecision(RetryKind Kind, TimeSpan Delay);

public sealed class ReconnectPolicy
{
    private readonly TimeProvider _clock;
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly double _initialSeconds;
    private readonly double _maxSeconds;
    private readonly double _jitterRatio;
    private long? _connectedAt;
    public int Attempt { get; private set; }

    public ReconnectPolicy(TimeProvider? clock = null, Func<double>? random = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, double initialSeconds = 1, double maxSeconds = 30, double jitterRatio = 0.2)
    {
        if (!double.IsFinite(initialSeconds) || !double.IsFinite(maxSeconds) || initialSeconds <= 0 || maxSeconds < initialSeconds || !double.IsFinite(jitterRatio) || jitterRatio < 0 || jitterRatio > 1 || maxSeconds > (TimeSpan.MaxValue.TotalSeconds - 1) / (1 + jitterRatio))
            throw new ArgumentOutOfRangeException(nameof(initialSeconds));
        _clock = clock ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _clock, token));
        _initialSeconds = initialSeconds;
        _maxSeconds = maxSeconds;
        _jitterRatio = jitterRatio;
    }

    public void MarkConnected() => _connectedAt ??= _clock.GetTimestamp();
    public void ResetIfStable()
    {
        if (_connectedAt is { } start && _clock.GetElapsedTime(start) >= TimeSpan.FromSeconds(60)) Attempt = 0;
    }
    public void MarkDisconnected() { ResetIfStable(); _connectedAt = null; }
    public TimeSpan NextDelay()
    {
        ResetIfStable();
        _connectedAt = null;
        double basis = Math.Min(_maxSeconds, _initialSeconds * Math.Pow(2, Math.Min(Attempt, 1023)));
        if (Attempt < int.MaxValue) Attempt++;
        var random = _random();
        if (!double.IsFinite(random) || random < 0 || random > 1) throw new InvalidOperationException("Random source must return a value in [0, 1].");
        return TimeSpan.FromSeconds(basis * (1 - _jitterRatio + 2 * _jitterRatio * random));
    }

    /// <summary>TimeSpan.MaxValue denotes an unrepresentably long server delay: wait for shutdown.</summary>
    public async Task WaitAsync(TimeSpan duration, CancellationToken token)
    {
        if (duration == TimeSpan.MaxValue || duration > DateTimeOffset.MaxValue - _clock.GetUtcNow())
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return;
        }
        await WaitUntilAsync(_clock.GetUtcNow() + duration, token).ConfigureAwait(false);
    }

    public async Task WaitUntilAsync(DateTimeOffset deadline, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return;
            // Bounded chunks support arbitrary Retry-After values without timer overflow.
            await _delay(remaining > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : remaining, token).ConfigureAwait(false);
        }
    }

    public RetryDecision ClassifyHttp(HttpStatusCode status, string? retryAfter = null, string? body = null)
    {
        MarkDisconnected();
        if (status == HttpStatusCode.BadRequest && IsCursorRejected(body)) return new(RetryKind.CursorRejected, TimeSpan.Zero);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(RetryKind.Permission, TimeSpan.FromSeconds(300));
        if ((int)status == 429 && TryRetryAfter(retryAfter, _clock.GetUtcNow(), out var wait)) return new(RetryKind.Retry, wait);
        if ((int)status >= 500 || (int)status == 429) return new(RetryKind.Retry, NextDelay());
        return new(RetryKind.Configuration, TimeSpan.Zero);
    }

    public static bool TryRetryAfter(string? value, DateTimeOffset now, out TimeSpan delay)
    {
        delay = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.All(c => c is >= '0' and <= '9'))
        {
            value = value.TrimStart('0');
            if (value.Length == 0) value = "0";
        }
        if (value.All(c => c is >= '0' and <= '9') && !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            delay = TimeSpan.MaxValue;
            return true;
        }
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            delay = seconds > (ulong)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
                ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (!RetryConditionHeaderValue.TryParse(value, out var parsed) || parsed.Date is not { } date) return false;
        delay = date > now ? date - now : TimeSpan.Zero;
        return true;
    }

    // ntfy server/errors.go: errHTTPBadRequestSinceInvalid has code 40008 and HTTP 400.
    // Generic 400 and textual resemblance are deliberately insufficient evidence.
    private static bool IsCursorRejected(string? body)
    {
        if (body is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == 40008
                && root.TryGetProperty("http", out var http) && http.TryGetInt32(out var h) && h == 400;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}






