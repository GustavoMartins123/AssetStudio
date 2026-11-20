using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AssetStudio.Avalonia;

public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChangedArgs = new("Count");
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetChangedArgs = new(NotifyCollectionChangedAction.Reset);

    private bool _suppressNotification;

    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> collection)
        : base(collection)
    {
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnPropertyChanged(e);
        }
    }

    public void BeginUpdate()
    {
        _suppressNotification = true;
    }

    public void EndUpdate()
    {
        _suppressNotification = false;
        OnPropertyChanged(CountChangedArgs);
        OnPropertyChanged(IndexerChangedArgs);
        OnCollectionChanged(ResetChangedArgs);
    }

    public void AddRange(IEnumerable<T> list)
    {
        if (list == null)
        {
            throw new ArgumentNullException(nameof(list));
        }

        _suppressNotification = true;
        try
        {
            foreach (var item in list)
            {
                Add(item);
            }
        }
        finally
        {
            EndUpdate();
        }
    }

    /// <summary>
    /// Replaces the entire contents with <paramref name="items"/> using a single internal list update
    /// and a single <see cref="NotifyCollectionChangedAction.Reset"/> without intermediate list copies.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        _suppressNotification = true;
        try
        {
            if (Items is List<T> backingList)
            {
                if (!ReferenceEquals(backingList, items))
                {
                    backingList.Clear();
                    if (items.Count > 0)
                    {
                        backingList.EnsureCapacity(items.Count);
                        if (items is IEnumerable<T> enumerable && items is ICollection<T>)
                        {
                            backingList.AddRange(enumerable);
                        }
                        else
                        {
                            for (var i = 0; i < items.Count; i++)
                            {
                                backingList.Add(items[i]);
                            }
                        }
                    }
                }
            }
            else if (!ReferenceEquals(Items, items))
            {
                Items.Clear();
                for (var i = 0; i < items.Count; i++)
                {
                    Items.Add(items[i]);
                }
            }
        }
        finally
        {
            EndUpdate();
        }
    }
}
