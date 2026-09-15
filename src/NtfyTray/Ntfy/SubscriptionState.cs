namespace NtfyTray.Ntfy;

public enum SubscriptionState { Stopped, Connecting, Connected, Reconnecting, Faulted }

public sealed record TopicStatus(SubscriptionState State, string? Fault = null);
