using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using NtfyTray.Ntfy;
using NtfyTray.Infrastructure;

namespace NtfyTray.Tests;

public class ProtocolLogicTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("keepalive")]
    [InlineData("message")]
    public void ParsesKnownEventsAndAdditionalFields(string kind)
    {
        Assert.True(NtfyEvent.TryParse($$"""{"event":"{{kind}}","id":"abc","topic":"t","message":"","time":123,"extra":true}""", "t", out var value));
        Assert.Equal(123, value!.ServerTime);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"event\":\"unknown\"}")]
    [InlineData("{\"event\":\"message\",\"id\":\"x\",\"topic\":\"t\"}")]
    [InlineData("{\"event\":\"message\",\"id\":\"x\",\"topic\":\"other\",\"message\":\"hi\"}")]
    [InlineData("{\"event\":\"message\",\"id\":\"\",\"topic\":\"t\",\"message\":1}")]
    public void InvalidEventsDoNotKeepAlive(string json) => Assert.False(NtfyEvent.TryParse(json, "t", out _));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task FrameworkParserHandlesMergedAndSplitUtf8Events(int chunk)
    {
        var data = "data: {\"event\":\"message\",\"id\":\"a\",\"topic\":\"t\",\"message\":\"中文\"}\r\n\r\ndata: {\"event\":\"keepalive\",\"id\":\"b\",\"topic\":\"t\"}\n\n";
        using var stream = new ChunkStream(Encoding.UTF8.GetBytes(data), chunk);
        var received = new List<NtfyEvent>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            if (NtfyEvent.TryParse(item.Data, "t", out var value)) received.Add(value!);
        Assert.Equal(2, received.Count);
        Assert.Equal("中文", received[0].Message);
        Assert.Equal(NtfyEventKind.Keepalive, received[1].Kind);
    }

    [Fact]
    public void DeduplicationIsBoundedAndDoesNotRefreshOldIds()
    {
        var ids = new RecentMessageIds();
        for (int i = 0; i < 2048; i++) Assert.True(ids.TryAdd(i.ToString()));
        Assert.False(ids.TryAdd("0"));
        Assert.True(ids.TryAdd("2048"));
        Assert.False(ids.Contains("0"));
        Assert.Equal(2048, ids.Count);
    }

    [Fact]
    public void ExponentialDelayCapsBeforeJitterAndResetsOnlyWhenStable()
    {
        var clock = new ManualClock();
        var policy = new ReconnectPolicy(clock, () => 1);
        Assert.Equal(new[] { 1.2, 2.4, 4.8, 9.6, 19.2, 36, 36 }, Enumerable.Range(0, 7).Select(_ => Math.Round(policy.NextDelay().TotalSeconds, 2)));
        policy.MarkConnected(); clock.Advance(59);
        Assert.Equal(36, policy.NextDelay().TotalSeconds);
        policy.MarkConnected(); clock.Advance(60);
        Assert.Equal(1.2, policy.NextDelay().TotalSeconds);
    }

    [Theory]
    [InlineData("000000000000000000001",1)]
    [InlineData("000000000000000000000",0)]
    [InlineData("120",120)]
    [InlineData("0",0)]
    [InlineData("Thu, 01 Jan 1970 00:02:00 GMT",120)]
    [InlineData("Wed, 31 Dec 1969 23:59:00 GMT",0)]
    public void RetryAfterSupportsSecondsAndDates(string header, int seconds)
    {
        Assert.True(ReconnectPolicy.TryRetryAfter(header, DateTimeOffset.UnixEpoch, out var delay));
        Assert.Equal(seconds, delay.TotalSeconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("garbage")]
    [InlineData("1.5")]
    public void InvalidRetryAfterFallsBack(string header) => Assert.Equal(TimeSpan.FromSeconds(1), new ReconnectPolicy(random: () => .5).ClassifyHttp((HttpStatusCode)429, header).Delay);

    [Fact]
    public void HttpClassificationRequiresExplicitCursorError()
    {
        var policy = new ReconnectPolicy();
        Assert.Equal(RetryKind.CursorRejected, policy.ClassifyHttp(HttpStatusCode.BadRequest, body: "{\"code\":40008,\"http\":400,\"error\":\"invalid since parameter\"}").Kind);
        foreach (var body in new[] { "{}", "{\"code\":40000,\"http\":400}", "{\"code\":\"40008\",\"http\":400}" })
            Assert.Equal(RetryKind.Configuration, policy.ClassifyHttp(HttpStatusCode.BadRequest, body: body).Kind);
        foreach (var code in new[] { 401, 403 }) Assert.Equal(TimeSpan.FromSeconds(300), policy.ClassifyHttp((HttpStatusCode)code).Delay);
        foreach (var code in new[] { 301, 302, 404, 407 }) Assert.Equal(RetryKind.Configuration, policy.ClassifyHttp((HttpStatusCode)code).Kind);
        foreach (var code in new[] { 500, 502, 503, 504 }) Assert.Equal(RetryKind.Retry, policy.ClassifyHttp((HttpStatusCode)code).Kind);
    }

    [Fact]
    public async Task WaitUsesOriginalDeadlineAndCancels()
    {
        var clock = new ManualClock();
        var calls = 0;
        var policy = new ReconnectPolicy(clock, delay: (duration, token) => { calls++; clock.Advance(duration.TotalSeconds + 30); return Task.CompletedTask; });
        await policy.WaitUntilAsync(clock.GetUtcNow().AddSeconds(300), CancellationToken.None);
        Assert.Equal(1, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.WaitUntilAsync(clock.GetUtcNow().AddSeconds(30), new CancellationToken(true)));
        Assert.True(ReconnectPolicy.TryRetryAfter(ulong.MaxValue.ToString(), clock.GetUtcNow(), out _));
    }

    [Theory]
    [InlineData(0, 0, 100000)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 200000)]
    public void JitterConfigurationAndLargeValidMaximum(double jitter, double random, double expected)
    {
        var policy = new ReconnectPolicy(random: () => random, initialSeconds: 100000, maxSeconds: 100000, jitterRatio: jitter);
        Assert.Equal(expected, policy.NextDelay().TotalSeconds);
    }

    [Theory]
    [InlineData("18446744073709551615")]
    [InlineData("99999999999999999999")]
    public async Task UnrepresentableRetryAfterWaitsForCancellation(string header)
    {
        Assert.True(ReconnectPolicy.TryRetryAfter(header, DateTimeOffset.UnixEpoch, out var wait));
        Assert.Equal(TimeSpan.MaxValue, wait);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReconnectPolicy().WaitAsync(wait, new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(jitterRatio: 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(maxSeconds: TimeSpan.MaxValue.TotalSeconds));
    }

    private sealed class ManualClock : TimeProvider
    {
        private double _seconds;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (long)(_seconds * TimeSpan.TicksPerSecond);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
        public void Advance(double seconds) => _seconds += seconds;
    }
    private sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], token);
    }
}


