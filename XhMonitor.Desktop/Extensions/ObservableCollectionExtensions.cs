using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace XhMonitor.Desktop.Extensions;

public static class ObservableCollectionExtensions
{
    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items.ToList();
        if (itemList.Count == 0)
            return;

        foreach (var item in itemList)
        {
            collection.Add(item);
        }
    }

    public static void ReplaceAll<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(items);

        collection.Clear();
        collection.AddRange(items);
    }
}
