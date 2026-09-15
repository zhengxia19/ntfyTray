namespace NtfyTray.Infrastructure;

/// <summary>Owned by one topic's sequential processing loop; add only after processing finishes.</summary>
public sealed class RecentMessageIds(int capacity = 2048)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    public int Count => _ids.Count;
    public bool Contains(string id) => _ids.Contains(id);
    public bool TryAdd(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_ids.Add(id)) return false;
        _order.Enqueue(id);
        if (_order.Count > _capacity) _ids.Remove(_order.Dequeue());
        return true;
    }
}
