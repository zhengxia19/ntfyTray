namespace NtfyTray.Infrastructure;

/// <summary>Owns application background work and observes every registered task.</summary>
public sealed class AsyncLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly List<Task> _tasks = [];
    private readonly Action<Exception>? _reportFailure;
    private TaskCompletionSource? _stopCompletion;
    public CancellationToken Token { get; }
    public bool IsStopping { get { lock (_gate) return _stopCompletion is not null; } }

    public AsyncLifetime(Action<Exception>? reportFailure = null)
    {
        _reportFailure = reportFailure;
        Token = _source.Token;
    }

    public Task Run(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_stopCompletion is not null) throw new InvalidOperationException("Application is stopping.");
            var observed = ObserveAsync(work);
            _tasks.Add(observed);
            return observed;
        }
    }

    public Task StopAsync()
    {
        Task[] tasks;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stopCompletion is not null) return _stopCompletion.Task;
            completion = _stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tasks = _tasks.ToArray();
        }
        _ = StopCoreAsync(tasks, completion);
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task ObserveAsync(Func<CancellationToken, Task> work)
    {
        // Registration completes before user work executes; no user code runs under the gate.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            Token.ThrowIfCancellationRequested();
            await work(Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception error) { Report(error); }
    }

    private async Task StopCoreAsync(Task[] tasks, TaskCompletionSource completion)
    {
        try
        {
            try { await _source.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { Report(error); }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _source.Dispose();
            lock (_gate) _tasks.Clear();
            completion.TrySetResult();
        }
    }

    private void Report(Exception error)
    {
        // A failed diagnostics sink must not create another unobserved background failure.
        try { _reportFailure?.Invoke(error); }
        catch (Exception) { }
    }
}
