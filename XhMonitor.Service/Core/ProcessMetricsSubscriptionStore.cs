namespace XhMonitor.Service.Core;

public enum ProcessMetricsSubscriptionMode
{
    Full,
    Lite
}

public sealed record ProcessMetricsLiteSubscriptionSnapshot(string ConnectionId, IReadOnlyList<int> PinnedProcessIds);

public interface IProcessMetricsSubscriptionStore
{
    void RegisterConnection(string connectionId);
    void RemoveConnection(string connectionId);
    void SetSubscription(string connectionId, ProcessMetricsSubscriptionMode mode, IReadOnlyCollection<int>? pinnedProcessIds = null);
    bool HasFullSubscribers { get; }
    bool HasLiteSubscribers { get; }
    IReadOnlyList<ProcessMetricsLiteSubscriptionSnapshot> GetLiteSubscriptionsSnapshot();
}

public sealed class ProcessMetricsSubscriptionStore : IProcessMetricsSubscriptionStore
{
    private sealed record SubscriptionState(ProcessMetricsSubscriptionMode Mode, IReadOnlyList<int> PinnedProcessIds);

    private readonly object _lock = new();
    private readonly Dictionary<string, SubscriptionState> _items = new();
    private int _fullCount;
    private int _liteCount;

    public void RegisterConnection(string connectionId)
        => SetSubscription(connectionId, ProcessMetricsSubscriptionMode.Full);

    public void RemoveConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        lock (_lock)
        {
            if (!_items.Remove(connectionId, out var state))
            {
                return;
            }

            if (state.Mode == ProcessMetricsSubscriptionMode.Full)
            {
                _fullCount = Math.Max(0, _fullCount - 1);
            }
            else
            {
                _liteCount = Math.Max(0, _liteCount - 1);
            }
        }
    }

    public void SetSubscription(
        string connectionId,
        ProcessMetricsSubscriptionMode mode,
        IReadOnlyCollection<int>? pinnedProcessIds = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        var pinned = mode == ProcessMetricsSubscriptionMode.Lite
            ? NormalizePinnedIds(pinnedProcessIds)
            : Array.Empty<int>();

        lock (_lock)
        {
            if (_items.TryGetValue(connectionId, out var existing))
            {
                if (existing.Mode != mode)
                {
                    if (existing.Mode == ProcessMetricsSubscriptionMode.Full)
                    {
                        _fullCount = Math.Max(0, _fullCount - 1);
                    }
                    else
                    {
                        _liteCount = Math.Max(0, _liteCount - 1);
                    }

                    if (mode == ProcessMetricsSubscriptionMode.Full)
                    {
                        _fullCount++;
                    }
                    else
                    {
                        _liteCount++;
                    }
                }

                _items[connectionId] = existing with { Mode = mode, PinnedProcessIds = pinned };
                return;
            }

            _items[connectionId] = new SubscriptionState(mode, pinned);
            if (mode == ProcessMetricsSubscriptionMode.Full)
            {
                _fullCount++;
            }
            else
            {
                _liteCount++;
            }
        }
    }

    public bool HasFullSubscribers
    {
        get
        {
            lock (_lock)
            {
                return _fullCount > 0;
            }
        }
    }

    public bool HasLiteSubscribers
    {
        get
        {
            lock (_lock)
            {
                return _liteCount > 0;
            }
        }
    }

    public IReadOnlyList<ProcessMetricsLiteSubscriptionSnapshot> GetLiteSubscriptionsSnapshot()
    {
        lock (_lock)
        {
            return _items
                .Where(kvp => kvp.Value.Mode == ProcessMetricsSubscriptionMode.Lite)
                .Select(kvp => new ProcessMetricsLiteSubscriptionSnapshot(kvp.Key, kvp.Value.PinnedProcessIds))
                .ToList();
        }
    }

    private static IReadOnlyList<int> NormalizePinnedIds(IReadOnlyCollection<int>? pinnedProcessIds)
    {
        if (pinnedProcessIds == null || pinnedProcessIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        return pinnedProcessIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }
}

