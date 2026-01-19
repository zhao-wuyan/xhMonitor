using XhMonitor.Core.Models;

namespace XhMonitor.Service.Core;

public interface IProcessMetadataStore
{
    IReadOnlyList<ProcessMetaSnapshot> GetSnapshot();
    List<ProcessMetaSnapshot> Update(IReadOnlyCollection<ProcessMetrics> metrics);
}

public sealed class ProcessMetadataStore : IProcessMetadataStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ProcessMetaSnapshot> _items = new();
    private readonly Dictionary<int, string> _keys = new();

    public IReadOnlyList<ProcessMetaSnapshot> GetSnapshot()
    {
        lock (_lock)
        {
            return _items.Values.ToList();
        }
    }

    public List<ProcessMetaSnapshot> Update(IReadOnlyCollection<ProcessMetrics> metrics)
    {
        var updates = new List<ProcessMetaSnapshot>();
        var currentPids = new HashSet<int>();

        lock (_lock)
        {
            foreach (var metric in metrics)
            {
                currentPids.Add(metric.Info.ProcessId);

                var metaKey = BuildMetaKey(metric.Info);
                if (_keys.TryGetValue(metric.Info.ProcessId, out var cached) && cached == metaKey)
                {
                    continue;
                }

                var snapshot = new ProcessMetaSnapshot
                {
                    ProcessId = metric.Info.ProcessId,
                    ProcessName = metric.Info.ProcessName,
                    CommandLine = metric.Info.CommandLine ?? string.Empty,
                    DisplayName = metric.Info.DisplayName ?? string.Empty
                };

                _keys[metric.Info.ProcessId] = metaKey;
                _items[metric.Info.ProcessId] = snapshot;
                updates.Add(snapshot);
            }

            foreach (var pid in _keys.Keys.Where(pid => !currentPids.Contains(pid)).ToList())
            {
                _keys.Remove(pid);
                _items.Remove(pid);
            }
        }

        return updates;
    }

    private static string BuildMetaKey(ProcessInfo info)
        => $"{info.ProcessName}\n{info.CommandLine ?? string.Empty}\n{info.DisplayName ?? string.Empty}";
}

public sealed class ProcessMetaSnapshot
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}
