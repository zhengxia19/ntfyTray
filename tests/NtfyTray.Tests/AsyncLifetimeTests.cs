using NtfyTray.Infrastructure;

namespace NtfyTray.Tests;

public sealed class AsyncLifetimeTests
{
    [Fact]
    public async Task StopCancelsThenWaitsForEveryTaskAndCleanup()
    {
        var failures = new List<Exception>();
        var lifetime = new AsyncLifetime(failures.Add);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        var work = lifetime.Run(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { canceled.SetResult(); await cleanup.Task; disposed = true; }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = lifetime.StopAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.False(stopping.IsCompleted);
        Assert.Same(stopping, lifetime.StopAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.Run(_ => Task.CompletedTask));
        cleanup.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposed);
        Assert.True(work.IsCompletedSuccessfully);
        Assert.Empty(failures);
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task BackgroundFailuresAreReportedOnceAndObserved()
    {
        var failures = new List<Exception>();
        await using var lifetime = new AsyncLifetime(failures.Add);
        await lifetime.Run(_ => throw new InvalidOperationException("test"));
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        await lifetime.StopAsync();
        Assert.Single(failures);
    }

    [Fact]
    public async Task UnexpectedCancellationIsFailureAndBrokenReporterCannotEscape()
    {
        int reports = 0;
        await using var lifetime = new AsyncLifetime(_ => { Interlocked.Increment(ref reports); throw new IOException(); });
        await lifetime.Run(_ => Task.FromCanceled(new CancellationToken(true)));
        Assert.Equal(1, reports);
        await lifetime.StopAsync();
    }

    [Fact]
    public async Task ConcurrentStopCallsShareCompletion()
    {
        var lifetime = new AsyncLifetime();
        var stops = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => new[] { lifetime.StopAsync() })));
        Assert.All(stops, result => Assert.Same(stops[0][0], result[0]));
        await stops[0][0];
        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotSkipOtherTaskCleanup()
    {
        int reports = 0;
        bool cleaned = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lifetime = new AsyncLifetime(_ => Interlocked.Increment(ref reports));
        _ = lifetime.Run(async token =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException());
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleaned = true; }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lifetime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned);
        Assert.Equal(1, reports);
    }
}


