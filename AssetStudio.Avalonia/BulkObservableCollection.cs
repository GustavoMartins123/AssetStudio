using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AssetStudio.Avalonia;

public class BulkObservableCollection<T> : ObservableCollection<T>
{
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
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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
}
