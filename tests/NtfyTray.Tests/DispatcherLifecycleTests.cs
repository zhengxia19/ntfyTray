using NtfyTray.Infrastructure;

namespace NtfyTray.Tests;

public class DispatcherLifecycleTests
{
    [Fact]
    public async Task DisposingOnOwnerCompletesUnpumpedCalls()
    {
        var ready = new TaskCompletionSource<UiDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var dispatcher = new UiDispatcher();
                ready.SetResult(dispatcher);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                dispatcher.Dispose();
                finished.SetResult();
            }
            catch (Exception error) { ready.TrySetException(error); finished.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var executed = false;
            var pending = dispatcher.InvokeAsync(() => executed = true);
            Assert.False(pending.IsCompleted);
            release.Set();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(executed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => true));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellationCompletesWithoutPumpingAndDoesNotRequireDisposal()
    {
        var ready = new TaskCompletionSource<UiDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var dispatcher = new UiDispatcher();
                ready.SetResult(dispatcher);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            catch (Exception error) { ready.TrySetException(error); finished.TrySetException(error); }
            finally { finished.TrySetResult(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var pending = dispatcher.InvokeAsync(() => true, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
