namespace NtfyTray.Infrastructure;

public sealed class UiDispatcher : IDisposable
{
    private readonly Control control = new();
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly object gate = new();
    private readonly HashSet<Action> pending = [];
    private bool disposed;
    public UiDispatcher() => _ = control.Handle;

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        lock (gate)
            if (disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(UiDispatcher)));
        if (Environment.CurrentManagedThreadId == threadId)
        {
            try { return Task.FromResult(action()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => completion.TrySetCanceled(token));
        Action abort = () => completion.TrySetException(new ObjectDisposedException(nameof(UiDispatcher)));
        lock (gate)
        {
            if (disposed) abort();
            else
            {
                pending.Add(abort);
                try
                {
                    control.BeginInvoke(() =>
                    {
                        try { if (!completion.Task.IsCompleted) completion.TrySetResult(action()); }
                        catch (Exception ex) { completion.TrySetException(ex); }
                    });
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }
        }
        return ObserveAsync();

        async Task<T> ObserveAsync()
        {
            try { return await completion.Task.ConfigureAwait(false); }
            finally
            {
                registration.Dispose();
                lock (gate) pending.Remove(abort);
            }
        }
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != threadId)
            throw new InvalidOperationException("UI dispatcher must be disposed on its owning thread.");
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var abort in pending) abort();
            pending.Clear();
        }
        control.Dispose();
    }
}
