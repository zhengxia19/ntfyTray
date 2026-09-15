using NtfyTray.Configuration;
using NtfyTray.Infrastructure;
using NtfyTray.Notifications;

namespace NtfyTray.Ntfy;

public sealed class SubscriptionManager : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, NtfySubscriber> subscribers;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly LoggingSetup log;
    private CancellationTokenSource? stopping;
    private Task[] tasks = [];
    private Task? stopTask;
    private bool started;
    public event Action? StateChanged;
    public IReadOnlyDictionary<string, TopicStatus> States => subscribers.ToDictionary(pair => pair.Key, pair => pair.Value.State, StringComparer.Ordinal);

    public SubscriptionManager(AppConfig config, INotificationService notifications, LoggingSetup log,
        HttpClient? client = null, TimeProvider? clock = null, Func<double>? random = null)
    {
        this.log = log;
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        subscribers = config.Subscriptions.ToDictionary(topic => topic,
            topic => new NtfySubscriber(config, topic, this.client, notifications, log, clock, random), StringComparer.Ordinal);
        foreach (var subscriber in subscribers.Values) subscriber.StateChanged += OnStateChanged;
    }

    public void Start(CancellationToken token = default)
    {
        lock (gate)
        {
            if (stopTask is not null) throw new InvalidOperationException("Manager is stopping.");
            if (started) return;
            started = true;
            stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            tasks = subscribers.Values.Select(subscriber => subscriber.RunAsync(stopping.Token)).ToArray();
        }
    }

    public void RequestReconnectAll()
    {
        foreach (var subscriber in subscribers.Values) subscriber.RequestReconnect();
    }

    public Task StopAsync()
    {
        lock (gate) return stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        // Yield so the shared stop task is published before cancellation can reenter observers.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (stopping is not null)
        {
            try { await stopping.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException) { log.Write(LogEventKind.TransportFailure, Serilog.Events.LogEventLevel.Warning); }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var subscriber in subscribers.Values) subscriber.StateChanged -= OnStateChanged;
        stopping?.Dispose();
        if (ownsClient) client.Dispose();
    }

    private void OnStateChanged(TopicStatus status)
    {
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception) { }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}

