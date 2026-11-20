using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Collections;

namespace AssetStudio.Avalonia;

/// <summary>
/// Persistent, zero-copy, Avalonia DataGrid-compatible collection supporting O(1) backing swaps
/// (<see cref="List{T}"/> / <see cref="T:AssetStudio.Avalonia.AssetItem[]"/>) and bounded-LRU
/// virtualized row materialization (<see cref="ReplaceWithProvider(int, Func{int, AssetItem}, int)"/>).
/// Implements <see cref="IDataGridCollectionView"/> directly so <c>DataGrid</c> binds to this instance
/// without wrapping or copying rows into an internal array on <c>Reset</c>.
/// </summary>
public sealed class VirtualAssetList :
    IList<AssetItem>,
    IList,
    IReadOnlyList<AssetItem>,
    INotifyCollectionChanged,
    INotifyPropertyChanged,
    IDataGridCollectionView
{
    private enum BackingMode : byte
    {
        List = 0,
        Array = 1,
        Provider = 2,
    }

    private struct LruEntry
    {
        public int Key;
        public AssetItem Value;
        public int Prev;
        public int Next;
    }

    private sealed class DeferHelper : IDisposable
    {
        private VirtualAssetList? _owner;

        public DeferHelper(VirtualAssetList owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner != null)
            {
                _owner = null;
                owner.EndDeferRefresh();
            }
        }
    }

    private sealed class ReferenceItemEqualityComparer : IEqualityComparer<AssetItem>
    {
        public static readonly ReferenceItemEqualityComparer Instance = new();

        public bool Equals(AssetItem? x, AssetItem? y) => ReferenceEquals(x, y);

        public int GetHashCode(AssetItem obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private const int IndexCacheActivationThreshold = 4;
    private const int IndexCacheMinCount = 256;

    private static readonly PropertyChangedEventArgs CountChangedArgs = new("Count");
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new("Item[]");
    private static readonly PropertyChangedEventArgs CurrentItemChangedArgs = new(nameof(CurrentItem));
    private static readonly PropertyChangedEventArgs CurrentPositionChangedArgs = new(nameof(CurrentPosition));
    private static readonly PropertyChangedEventArgs IsCurrentBeforeFirstChangedArgs = new(nameof(IsCurrentBeforeFirst));
    private static readonly PropertyChangedEventArgs IsCurrentAfterLastChangedArgs = new(nameof(IsCurrentAfterLast));
    private static readonly NotifyCollectionChangedEventArgs ResetChangedArgs = new(NotifyCollectionChangedAction.Reset);
    private static readonly AvaloniaList<object> EmptyGroups = new();

    private BackingMode _mode;
    private List<AssetItem>? _list;
    private AssetItem[]? _array;
    private Func<int, AssetItem>? _provider;
    private Func<AssetItem, int>? _indexLocator;
    private int _count;

    private int _generation;
    private int _lastNotifiedCount;
    private int _updateDepth;
    private bool _pendingReset;

    private Dictionary<AssetItem, int>? _indexCache;
    private int _indexLookupCount;

    // Intrusive array-backed LRU cache for Provider mode (zero per-scroll LinkedListNode allocations).
    private int _lruCapacity;
    private LruEntry[]? _lruSlots;
    private Dictionary<int, int>? _lruIndexToSlot;
    private Dictionary<AssetItem, int>? _lruItemToIndex;
    private int _lruCount;
    private int _lruHead = -1;
    private int _lruTail = -1;

    private CultureInfo _culture = CultureInfo.InvariantCulture;
    private Func<object, bool>? _filter;
    private DataGridSortDescriptionCollection? _sortDescriptions;
    private object? _currentItem;
    private int _currentPosition = -1;
    private bool _isCurrentBeforeFirst = true;
    private bool _isCurrentAfterLast;

    public VirtualAssetList()
    {
        _mode = BackingMode.List;
        _list = new List<AssetItem>();
    }

    public VirtualAssetList(List<AssetItem> initialItems)
    {
        ArgumentNullException.ThrowIfNull(initialItems);
        _mode = BackingMode.List;
        _list = initialItems;
        _lastNotifiedCount = initialItems.Count;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;
    public event EventHandler? CurrentChanged;

    public int Count => _mode == BackingMode.List ? _list!.Count : _count;

    public int CachedItemCount => _mode == BackingMode.Provider ? _lruCount : 0;

    public bool IsReadOnly => _mode != BackingMode.List;

    public bool IsFixedSize => _mode != BackingMode.List;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public AssetItem this[int index]
    {
        get
        {
            return _mode switch
            {
                BackingMode.List => _list![index],
                BackingMode.Array => GetArrayItem(index),
                BackingMode.Provider => GetProviderItem(index),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_mode != BackingMode.List)
            {
                throw new NotSupportedException("Cannot modify items when VirtualAssetList is in fixed array or virtual provider mode.");
            }

            _list![index] = value;
            _generation++;
            InvalidateIndexCache();
            NotifyResetOrDefer();
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set
        {
            if (value is not AssetItem assetItem)
            {
                throw new ArgumentException("Value must be a non-null AssetItem.", nameof(value));
            }

            this[index] = assetItem;
        }
    }

    public CultureInfo Culture
    {
        get => _culture;
        set => _culture = value ?? CultureInfo.InvariantCulture;
    }

    public IEnumerable SourceCollection => this;

    public Func<object, bool> Filter
    {
        get => _filter!;
        set => _filter = value;
    }

    public bool CanFilter => false;

    public DataGridSortDescriptionCollection SortDescriptions => _sortDescriptions ??= new DataGridSortDescriptionCollection();

    public bool CanSort => false;

    public bool CanGroup => false;

    public bool IsGrouping => false;

    public int GroupingDepth => 0;

    public IAvaloniaReadOnlyList<object> Groups => EmptyGroups;

    public bool IsEmpty => Count == 0;

    public object? CurrentItem => _currentItem;

    object IDataGridCollectionView.CurrentItem => _currentItem!;

    public int CurrentPosition => _currentPosition;

    public bool IsCurrentAfterLast => _isCurrentAfterLast;

    public bool IsCurrentBeforeFirst => _isCurrentBeforeFirst;

    internal bool IsBackedBy(List<AssetItem> list) => _mode == BackingMode.List && ReferenceEquals(_list, list);

    /// <summary>
    /// Swaps the backing store to <paramref name="items"/> in O(1) with zero element copies
    /// and emits a single <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// </summary>
    public void ReplaceBacking(List<AssetItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        ClearProviderState();
        _mode = BackingMode.List;
        _list = items;
        _array = null;
        _count = items.Count;
        _generation++;
        InvalidateIndexCache();
        UpdateCurrencyAfterReset();
        NotifyResetOrDefer();
    }

    /// <summary>
    /// Swaps the backing store to the first <paramref name="count"/> elements of <paramref name="items"/>
    /// in O(1) with zero element copies and emits a single <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// </summary>
    public void ReplaceBacking(AssetItem[] items, int count)
    {
        ArgumentNullException.ThrowIfNull(items);
        if ((uint)count > (uint)items.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be between 0 and items.Length.");
        }

        ClearProviderState();
        _mode = BackingMode.Array;
        _list = null;
        _array = items;
        _count = count;
        _generation++;
        InvalidateIndexCache();
        UpdateCurrencyAfterReset();
        NotifyResetOrDefer();
    }

    /// <summary>
    /// Switches to virtual index-backed flyweight mode over <paramref name="count"/> logical rows,
    /// retaining at most <paramref name="lruCapacity"/> materialized <see cref="AssetItem"/> instances.
    /// </summary>
    public void ReplaceWithProvider(int count, Func<int, AssetItem> materializer, int lruCapacity = 512)
    {
        ReplaceWithProvider(count, materializer, lruCapacity, indexLocator: null);
    }

    /// <summary>
    /// Switches to virtual index-backed flyweight mode over <paramref name="count"/> logical rows,
    /// retaining at most <paramref name="lruCapacity"/> materialized <see cref="AssetItem"/> instances
    /// and optionally resolving evicted item indices via <paramref name="indexLocator"/>.
    /// </summary>
    public void ReplaceWithProvider(
        int count,
        Func<int, AssetItem> materializer,
        int lruCapacity,
        Func<AssetItem, int>? indexLocator)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(materializer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lruCapacity);

        ClearProviderState();
        _mode = BackingMode.Provider;
        _list = null;
        _array = null;
        _provider = materializer;
        _indexLocator = indexLocator;
        _count = count;
        _lruCapacity = lruCapacity;
        _lruIndexToSlot ??= new Dictionary<int, int>();
        _lruItemToIndex ??= new Dictionary<AssetItem, int>(ReferenceItemEqualityComparer.Instance);
        _generation++;
        InvalidateIndexCache();
        UpdateCurrencyAfterReset();
        NotifyResetOrDefer();
    }

    public void BeginUpdate()
    {
        _updateDepth++;
    }

    public void EndUpdate()
    {
        if (_updateDepth == 0)
        {
            return;
        }

        _updateDepth--;
        if (_updateDepth == 0)
        {
            _pendingReset = false;
            EmitReset();
        }
    }

    public void Refresh()
    {
        UpdateCurrencyAfterReset();
        NotifyResetOrDefer();
    }

    public IDisposable DeferRefresh()
    {
        BeginUpdate();
        return new DeferHelper(this);
    }

    private void EndDeferRefresh()
    {
        if (_updateDepth == 0)
        {
            return;
        }

        _updateDepth--;
        if (_updateDepth == 0 && _pendingReset)
        {
            _pendingReset = false;
            EmitReset();
        }
        else if (_updateDepth == 0)
        {
            Refresh();
        }
    }

    public void Add(AssetItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureMutableListMode();

        var index = _list!.Count;
        _list.Add(item);
        _generation++;
        InvalidateIndexCache();

        if (_updateDepth > 0)
        {
            _pendingReset = true;
            return;
        }

        _lastNotifiedCount = _list.Count;
        PropertyChanged?.Invoke(this, CountChangedArgs);
        PropertyChanged?.Invoke(this, IndexerChangedArgs);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
    }

    int IList.Add(object? value)
    {
        if (value is not AssetItem assetItem)
        {
            throw new ArgumentException("Value must be a non-null AssetItem.", nameof(value));
        }

        Add(assetItem);
        return Count - 1;
    }

    public void Insert(int index, AssetItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureMutableListMode();

        _list!.Insert(index, item);
        _generation++;
        InvalidateIndexCache();

        if (_updateDepth > 0)
        {
            _pendingReset = true;
            return;
        }

        _lastNotifiedCount = _list.Count;
        PropertyChanged?.Invoke(this, CountChangedArgs);
        PropertyChanged?.Invoke(this, IndexerChangedArgs);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
    }

    void IList.Insert(int index, object? value)
    {
        if (value is not AssetItem assetItem)
        {
            throw new ArgumentException("Value must be a non-null AssetItem.", nameof(value));
        }

        Insert(index, assetItem);
    }

    public bool Remove(AssetItem item)
    {
        EnsureMutableListMode();
        if (item == null)
        {
            return false;
        }

        var index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    void IList.Remove(object? value)
    {
        if (value is AssetItem assetItem)
        {
            Remove(assetItem);
        }
    }

    public void RemoveAt(int index)
    {
        EnsureMutableListMode();

        var removed = _list![index];
        _list.RemoveAt(index);
        _generation++;
        InvalidateIndexCache();
        if (ReferenceEquals(_currentItem, removed))
        {
            UpdateCurrencyAfterReset();
        }

        if (_updateDepth > 0)
        {
            _pendingReset = true;
            return;
        }

        _lastNotifiedCount = _list.Count;
        PropertyChanged?.Invoke(this, CountChangedArgs);
        PropertyChanged?.Invoke(this, IndexerChangedArgs);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    public void Clear()
    {
        if (_mode == BackingMode.List)
        {
            _list?.Clear();
        }
        else
        {
            ClearProviderState();
            _mode = BackingMode.List;
            _list = new List<AssetItem>();
            _array = null;
        }

        _count = 0;
        _generation++;
        InvalidateIndexCache();
        SetCurrentValues(null, -1, 0);
        NotifyResetOrDefer();
    }

    public int IndexOf(AssetItem item)
    {
        if (item == null)
        {
            return -1;
        }

        var count = Count;
        if (count == 0)
        {
            return -1;
        }

        return _mode switch
        {
            BackingMode.List => IndexOfInList(item),
            BackingMode.Array => IndexOfInArray(item),
            BackingMode.Provider => IndexOfInProvider(item),
            _ => -1,
        };
    }

    int IList.IndexOf(object? value)
    {
        return value is AssetItem assetItem ? IndexOf(assetItem) : -1;
    }

    public bool Contains(AssetItem item) => IndexOf(item) >= 0;

    bool IList.Contains(object? value) => value is AssetItem assetItem && IndexOf(assetItem) >= 0;

    bool IDataGridCollectionView.Contains(object item) => item is AssetItem assetItem && IndexOf(assetItem) >= 0;

    public void CopyTo(AssetItem[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

        var count = Count;
        if (array.Length - arrayIndex < count)
        {
            throw new ArgumentException("Destination array is not large enough.", nameof(array));
        }

        if (count == 0)
        {
            return;
        }

        switch (_mode)
        {
            case BackingMode.List:
                _list!.CopyTo(array, arrayIndex);
                break;
            case BackingMode.Array:
                Array.Copy(_array!, 0, array, arrayIndex, count);
                break;
            case BackingMode.Provider:
                for (var i = 0; i < count; i++)
                {
                    array[arrayIndex + i] = GetProviderItem(i);
                }
                break;
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Rank != 1)
        {
            throw new ArgumentException("Only single-dimensional arrays are supported.", nameof(array));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var count = Count;
        if (array.Length - index < count)
        {
            throw new ArgumentException("Destination array is not large enough.", nameof(array));
        }

        if (array is AssetItem[] typed)
        {
            CopyTo(typed, index);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public IEnumerator<AssetItem> GetEnumerator()
    {
        if (_mode == BackingMode.List)
        {
            return _list!.GetEnumerator();
        }

        return EnumerateNonListMode();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public string GetGroupingPropertyNameAtDepth(int level) => string.Empty;

    public bool MoveCurrentToFirst() => MoveCurrentToPosition(Count > 0 ? 0 : -1);

    public bool MoveCurrentToLast() => MoveCurrentToPosition(Count - 1);

    public bool MoveCurrentToNext()
    {
        var next = _currentPosition + 1;
        return next <= Count && MoveCurrentToPosition(next);
    }

    public bool MoveCurrentToPrevious()
    {
        var prev = _currentPosition - 1;
        return prev >= -1 && MoveCurrentToPosition(prev);
    }

    public bool MoveCurrentTo(object? item)
    {
        if (Equals(_currentItem, item) && (item != null || IsCurrentInView(Count)))
        {
            return IsCurrentInView(Count);
        }

        return MoveCurrentToPosition(item is AssetItem assetItem ? IndexOf(assetItem) : -1);
    }

    public bool MoveCurrentToPosition(int position)
    {
        var count = Count;
        if (position < -1 || position > count)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var targetItem = (uint)position < (uint)count ? this[position] : null;
        if (position == _currentPosition && ReferenceEquals(targetItem, _currentItem) && (targetItem != null || IsCurrentInView(count)))
        {
            return IsCurrentInView(count);
        }

        var args = new DataGridCurrentChangingEventArgs();
        CurrentChanging?.Invoke(this, args);
        if (args.Cancel)
        {
            return IsCurrentInView(count);
        }

        var oldAfterLast = _isCurrentAfterLast;
        var oldBeforeFirst = _isCurrentBeforeFirst;
        SetCurrentValues(targetItem, position, count);
        CurrentChanged?.Invoke(this, EventArgs.Empty);

        if (_isCurrentAfterLast != oldAfterLast)
        {
            PropertyChanged?.Invoke(this, IsCurrentAfterLastChangedArgs);
        }
        if (_isCurrentBeforeFirst != oldBeforeFirst)
        {
            PropertyChanged?.Invoke(this, IsCurrentBeforeFirstChangedArgs);
        }
        PropertyChanged?.Invoke(this, CurrentPositionChangedArgs);
        PropertyChanged?.Invoke(this, CurrentItemChangedArgs);

        return IsCurrentInView(count);
    }

    private IEnumerator<AssetItem> EnumerateNonListMode()
    {
        var generation = _generation;
        var count = _count;
        for (var i = 0; i < count; i++)
        {
            if (generation != _generation)
            {
                throw new InvalidOperationException("Collection was modified during enumeration.");
            }

            yield return this[i];
        }
    }

    private AssetItem GetArrayItem(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _array![index];
    }

    private AssetItem GetProviderItem(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_lruIndexToSlot!.TryGetValue(index, out var slot))
        {
            MoveSlotToHead(slot);
            return _lruSlots![slot].Value;
        }

        var created = _provider!(index);
        InsertIntoLru(index, created);
        return created;
    }

    private void InsertIntoLru(int index, AssetItem item)
    {
        if (_lruCount < _lruCapacity)
        {
            EnsureLruCapacityForSlot(_lruCount);
            var slot = _lruCount++;
            _lruSlots![slot] = new LruEntry
            {
                Key = index,
                Value = item,
                Prev = -1,
                Next = _lruHead,
            };
            if (_lruHead != -1)
            {
                _lruSlots[_lruHead].Prev = slot;
            }
            _lruHead = slot;
            if (_lruTail == -1)
            {
                _lruTail = slot;
            }

            _lruIndexToSlot![index] = slot;
            if (item != null)
            {
                _lruItemToIndex![item] = index;
            }
            return;
        }

        var evictSlot = _lruTail;
        ref var entry = ref _lruSlots![evictSlot];
        var oldKey = entry.Key;
        var oldItem = entry.Value;

        _lruIndexToSlot!.Remove(oldKey);
        if (oldItem != null && _lruItemToIndex!.TryGetValue(oldItem, out var mappedIdx) && mappedIdx == oldKey)
        {
            _lruItemToIndex.Remove(oldItem);
        }

        entry.Key = index;
        entry.Value = item;
        _lruIndexToSlot[index] = evictSlot;
        if (item != null)
        {
            _lruItemToIndex![item] = index;
        }

        MoveSlotToHead(evictSlot);
    }

    private void EnsureLruCapacityForSlot(int requiredSlot)
    {
        if (_lruSlots == null)
        {
            var initialLen = Math.Min(_lruCapacity, 64);
            if (initialLen <= requiredSlot)
            {
                initialLen = Math.Min(_lruCapacity, requiredSlot + 1);
            }
            _lruSlots = new LruEntry[initialLen];
            return;
        }

        if (requiredSlot >= _lruSlots.Length)
        {
            var newLen = Math.Min(_lruCapacity, _lruSlots.Length * 2);
            if (newLen <= requiredSlot)
            {
                newLen = _lruCapacity;
            }
            Array.Resize(ref _lruSlots, newLen);
        }
    }

    private void MoveSlotToHead(int slot)
    {
        if (slot == _lruHead)
        {
            return;
        }

        ref var entry = ref _lruSlots![slot];
        var prev = entry.Prev;
        var next = entry.Next;

        if (prev != -1)
        {
            _lruSlots[prev].Next = next;
        }
        if (next != -1)
        {
            _lruSlots[next].Prev = prev;
        }
        if (slot == _lruTail)
        {
            _lruTail = prev;
        }

        entry.Prev = -1;
        entry.Next = _lruHead;
        if (_lruHead != -1)
        {
            _lruSlots[_lruHead].Prev = slot;
        }
        _lruHead = slot;
        if (_lruTail == -1)
        {
            _lruTail = slot;
        }
    }

    private void ClearProviderState()
    {
        if (_lruSlots != null && _lruCount > 0)
        {
            Array.Clear(_lruSlots, 0, _lruCount);
        }

        _lruIndexToSlot?.Clear();
        _lruItemToIndex?.Clear();
        _lruCount = 0;
        _lruHead = -1;
        _lruTail = -1;
        _provider = null;
        _indexLocator = null;
    }

    private int IndexOfInList(AssetItem item)
    {
        var list = _list!;
        if (_indexCache != null)
        {
            return _indexCache.TryGetValue(item, out var idx) ? idx : -1;
        }

        if (++_indexLookupCount >= IndexCacheActivationThreshold && list.Count >= IndexCacheMinCount)
        {
            var cache = new Dictionary<AssetItem, int>(list.Count, ReferenceItemEqualityComparer.Instance);
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry != null)
                {
                    cache.TryAdd(entry, i);
                }
            }

            _indexCache = cache;
            return cache.TryGetValue(item, out var idx) ? idx : -1;
        }

        return list.IndexOf(item);
    }

    private int IndexOfInArray(AssetItem item)
    {
        var array = _array!;
        var count = _count;
        if (_indexCache != null)
        {
            return _indexCache.TryGetValue(item, out var idx) ? idx : -1;
        }

        if (++_indexLookupCount >= IndexCacheActivationThreshold && count >= IndexCacheMinCount)
        {
            var cache = new Dictionary<AssetItem, int>(count, ReferenceItemEqualityComparer.Instance);
            for (var i = 0; i < count; i++)
            {
                var entry = array[i];
                if (entry != null)
                {
                    cache.TryAdd(entry, i);
                }
            }

            _indexCache = cache;
            return cache.TryGetValue(item, out var idx) ? idx : -1;
        }

        return Array.IndexOf(array, item, 0, count);
    }

    private int IndexOfInProvider(AssetItem item)
    {
        if (_lruItemToIndex != null && _lruItemToIndex.TryGetValue(item, out var idx))
        {
            return idx;
        }

        return _indexLocator != null ? _indexLocator(item) : -1;
    }

    private void EnsureMutableListMode()
    {
        if (_mode != BackingMode.List || _list == null)
        {
            throw new NotSupportedException("Collection is read-only in array slice or virtual provider mode.");
        }
    }

    private void InvalidateIndexCache()
    {
        _indexCache = null;
        _indexLookupCount = 0;
    }

    private void NotifyResetOrDefer()
    {
        if (_updateDepth > 0)
        {
            _pendingReset = true;
            return;
        }

        EmitReset();
    }

    private void EmitReset()
    {
        _lastNotifiedCount = Count;
        PropertyChanged?.Invoke(this, CountChangedArgs);
        PropertyChanged?.Invoke(this, IndexerChangedArgs);
        CollectionChanged?.Invoke(this, ResetChangedArgs);
    }

    private void UpdateCurrencyAfterReset()
    {
        var count = Count;
        if (count == 0 || _currentItem is not AssetItem currentAsset)
        {
            SetCurrentValues(null, -1, count);
            return;
        }

        if ((uint)_currentPosition < (uint)count && ReferenceEquals(TryPeekExistingItem(_currentPosition), currentAsset))
        {
            SetCurrentValues(currentAsset, _currentPosition, count);
            return;
        }

        var newIndex = IndexOf(currentAsset);
        if (newIndex >= 0)
        {
            SetCurrentValues(currentAsset, newIndex, count);
        }
        else
        {
            SetCurrentValues(null, -1, count);
        }
    }

    private AssetItem? TryPeekExistingItem(int index)
    {
        return _mode switch
        {
            BackingMode.List => _list![index],
            BackingMode.Array => _array![index],
            BackingMode.Provider => _lruIndexToSlot != null && _lruIndexToSlot.TryGetValue(index, out var slot)
                ? _lruSlots![slot].Value
                : null,
            _ => null,
        };
    }

    private bool IsCurrentInView(int count) => _currentPosition >= 0 && _currentPosition < count;

    private void SetCurrentValues(object? item, int position, int count)
    {
        _currentItem = item;
        _currentPosition = position;
        _isCurrentBeforeFirst = position < 0;
        _isCurrentAfterLast = count == 0 ? position > -1 : position >= count;
    }
}
