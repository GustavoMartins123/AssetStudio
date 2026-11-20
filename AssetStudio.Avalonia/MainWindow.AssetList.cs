using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private const int ArenaViewportLruCapacity = 512;
    private const int ArenaInlineSortedMergeThreshold = 8192;

    private CancellationTokenSource? listSearchDebounce;
    private bool isRefreshingFilterList;
    private bool isSorting;
    private bool _isArenaAssetListMode;
    private readonly List<int> exportableArenaIndices = new();
    private ulong[] _exportableArenaBitset = Array.Empty<ulong>();
    private int[] visibleArenaIndices = Array.Empty<int>();
    private int visibleArenaCount;
    private bool visibleArenaIsIdentity;
    private bool visibleArenaIsStrictlyIncreasing;
    private Dictionary<int, int>? visibleArenaRowByArenaIndex;
    private CancellationTokenSource? _arenaCoalescedSortCts;
    private int _arenaCoalescedSortVersion;

    private int ExportableAssetCount => _isArenaAssetListMode ? exportableArenaIndices.Count : _eagerExportableAssets.Count;
    private int VisibleAssetCount => _isArenaAssetListMode ? visibleArenaCount : _eagerVisibleAssets.Count;

    private async void ListSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        PrioritizeUserInteraction();
        listSearchDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        listSearchDebounce = debounce;

        try
        {
            await Task.Delay(800, debounce.Token);
            if (!debounce.IsCancellationRequested)
            {
                PrioritizeUserInteraction();
                await FilterAssetListAsync(debounce.Token);
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async void AssetListDataGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (isSorting) return;
        PrioritizeUserInteraction();
        isSorting = true;
        try
        {
            var column = e.Column;
            if (column == null) return;

            var sortMember = column.SortMemberPath ?? column.Header?.ToString();
            if (string.IsNullOrEmpty(sortMember)) return;

            if (assetListSortMember == sortMember)
            {
                assetListSortDescending = !assetListSortDescending;
            }
            else
            {
                assetListSortMember = sortMember;
                assetListSortDescending = false;
            }

            await ApplyAssetListSortAsync();
            UpdateAssetListSortHeaderIndicators();
        }
        catch (Exception ex)
        {
            Logger.Error("Error sorting asset list", ex);
            StatusStripUpdate("Error sorting asset list. See error log.");
        }
        finally
        {
            isSorting = false;
        }
    }

    private void AssetListDataGrid_CellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        var row = e.Row;
        if (row?.DataContext is not AssetItem item)
        {
            assetContextItem = null;
            assetContextCellText = string.Empty;
            return;
        }

        assetContextItem = item;
        var column = e.Column;
        assetContextCellText = GetAssetCellText(item, column?.SortMemberPath ?? column?.Header?.ToString());

        var isRightButton = e.PointerPressedEventArgs?
            .GetCurrentPoint(AssetListDataGrid)
            .Properties
            .IsRightButtonPressed == true;
        var selectedItems = AssetListDataGrid.SelectedItems;
        if (isRightButton && selectedItems != null && !selectedItems.Contains(item))
        {
            AssetListDataGrid.SelectedItem = item;
        }
    }

    private void AssetListContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        var selectedAssets = GetSelectedAssets();
        var singleSelected = selectedAssets.Count == 1;
        var hasAnimatorWithClips = selectedAssets.Any(x => x.Type == ClassIDType.Animator)
            && selectedAssets.Any(x => x.Type == ClassIDType.AnimationClip);
        var hasModelGroupTarget = selectedAssets.Any(x => x.Type is ClassIDType.Mesh or ClassIDType.Animator or ClassIDType.Avatar);

        goToSceneHierarchyMenuItem.IsVisible = singleSelected && selectedAssets[0].TreeNode != null;
        showOriginalFileMenuItem.IsVisible = singleSelected;
        exportAnimatorWithSelectedAnimationClipMenuItem.IsVisible = hasAnimatorWithClips;
        exportModelGroupFbxMenuItem.IsVisible = hasModelGroupTarget;
    }

    private async void CopyAssetCellText_Click(object? sender, RoutedEventArgs e)
    {
        var text = assetContextCellText;
        if (string.IsNullOrEmpty(text) && AssetListDataGrid.SelectedItem is AssetItem item)
        {
            text = item.Name;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            StatusStripUpdate("Copied asset cell text.");
        }
    }

    private async void ExportSelectedAssetsContext_Click(object? sender, RoutedEventArgs e)
    {
        await ExportAssets(GetSelectedAssets(), ExportMode.Convert);
    }

    private void ShowOriginalFile_Click(object? sender, RoutedEventArgs e)
    {
        var item = assetContextItem ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (item == null) return;
        ShowOriginalFile(item);
    }

    private async void ExportAnimatorWithSelectedAnimationClip_Click(object? sender, RoutedEventArgs e)
    {
        await ExportAnimatorWithSelectedAnimationClips(GetSelectedAssets());
    }

    private async void ExportModelGroupFbx_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedModelGroupFbxAsync();
    }

    private async Task FilterAssetListAsync(CancellationToken token)
    {
        CancelPendingArenaCoalescedSort();

        var filterText = listSearch?.Text?.Trim();
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;

        // Capture selection before filtering
        var selectedKeys = new HashSet<AssetSelectionKey>();
        AssetSelectionKey oldSelectedKey = default;
        if (AssetListDataGrid.SelectedItems != null)
        {
            foreach (var item in AssetListDataGrid.SelectedItems.OfType<AssetItem>())
            {
                if (TryGetAssetSelectionKey(item, out var key))
                {
                    selectedKeys.Add(key);
                }
            }

            if (AssetListDataGrid.SelectedItem is AssetItem selectedItem)
            {
                TryGetAssetSelectionKey(selectedItem, out oldSelectedKey);
            }
        }

        // Find scroll viewer and save scroll offset
        var scrollViewer = FindVisualChild<ScrollViewer>(AssetListDataGrid);
        var scrollOffset = scrollViewer?.Offset ?? default;

        try
        {
            if (_isArenaAssetListMode)
            {
                var arena = assetsManager.ProjectIndex.Arena;
                var indicesSnapshot = exportableArenaIndices.ToArray();
                var arenaResult = await Task.Run(() =>
                {
                    var matches = AssetListQuery.FilterArena(arena, indicesSnapshot, indicesSnapshot.Length, classFilter, selectedTypes, filterText, token);
                    token.ThrowIfCancellationRequested();
                    AssetListQuery.SortArenaInPlace(arena, matches, matches.Length, sortMember, sortDescending);
                    return matches;
                }, token);

                var isSorted = !string.IsNullOrEmpty(sortMember);
                var isIdentity = !isSorted && IsIdentityArenaIndices(arenaResult, arenaResult.Length);
                var isStrictlyIncreasing = !isSorted && (isIdentity || IsStrictlyIncreasingIndices(arenaResult, arenaResult.Length));

                isRefreshingFilterList = true;
                try
                {
                    ApplyVisibleArenaIndices(arenaResult, arenaResult.Length, isStrictlyIncreasing, isIdentity);
                    StatusStripUpdate($"Showing {VisibleAssetCount:N0} assets");
                    RestoreArenaSelectionIfNeeded(selectedKeys);
                }
                finally
                {
                    isRefreshingFilterList = false;
                }
            }
            else
            {
                var assetsSnapshot = _eagerExportableAssets.ToArray();
                var result = await Task.Run(() =>
                {
                    var matches = AssetListQuery.Filter(assetsSnapshot, classFilter, selectedTypes, filterText, token);
                    token.ThrowIfCancellationRequested();
                    return AssetListQuery.SortInPlace(matches, sortMember, sortDescending);
                }, token);

                isRefreshingFilterList = true;
                try
                {
                    ReplaceVisibleAssetsOwned(result);
                    StatusStripUpdate($"Showing {VisibleAssetCount} assets");

                    // Restore selection if DataGrid Reset did not already retain all matching items (e.g. re-materialized instances).
                    RestoreSelectionIfNeeded(selectedKeys);
                }
                finally
                {
                    isRefreshingFilterList = false;
                }
            }

            // Restore scroll position
            if (scrollViewer != null && scrollOffset != default)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    scrollViewer.Offset = scrollOffset;
                }, DispatcherPriority.Background);
            }

            // Trigger selection changed logic if the selected item actually changed
            var newSelectedItem = AssetListDataGrid.SelectedItem as AssetItem;
            if (newSelectedItem != null)
            {
                UpdatePreloadWindow(newSelectedItem);
            }
            else
            {
                lock (preloaderLock)
                {
                    preloaderCts?.Cancel();
                }
            }
            TryGetAssetSelectionKey(newSelectedItem, out var newSelectedKey);
            if (newSelectedKey != oldSelectedKey)
            {
                _currentlySelectedUniqueID = GetAssetUniqueKey(newSelectedItem);
                if (newSelectedItem != null)
                {
                    if (RightTabControl.SelectedIndex == 1)
                    {
                        _ = UpdateDumpForSelectedAsset();
                    }
                    QueuePreviewAsset(newSelectedItem);
                }
                else
                {
                    DumpTextBox.Text = string.Empty;
                    previewDebounce?.Cancel();
                    ClearPreview("Preview Panel");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Task canceled, ignore
        }
        catch (Exception ex)
        {
            Logger.Error("Error filtering asset list", ex);
            StatusStripUpdate("Error filtering asset list. See error log.");
        }
    }

    private async Task ApplyAssetListSortAsync()
    {
        CancelPendingArenaCoalescedSort();

        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var selectedKeys = new HashSet<AssetSelectionKey>();
        if (AssetListDataGrid.SelectedItems != null)
        {
            foreach (var item in AssetListDataGrid.SelectedItems.OfType<AssetItem>())
            {
                if (TryGetAssetSelectionKey(item, out var key))
                {
                    selectedKeys.Add(key);
                }
            }
        }

        try
        {
            if (_isArenaAssetListMode)
            {
                var arena = assetsManager.ProjectIndex.Arena;
                var currentIndices = new int[visibleArenaCount];
                if (visibleArenaCount > 0)
                {
                    Array.Copy(visibleArenaIndices, currentIndices, visibleArenaCount);
                }

                await Task.Run(() => AssetListQuery.SortArenaInPlace(arena, currentIndices, currentIndices.Length, sortMember, sortDescending));

                isRefreshingFilterList = true;
                try
                {
                    var isSorted = !string.IsNullOrEmpty(sortMember);
                    var isIdentity = !isSorted && IsIdentityArenaIndices(currentIndices, currentIndices.Length);
                    var isStrictlyIncreasing = !isSorted && (isIdentity || IsStrictlyIncreasingIndices(currentIndices, currentIndices.Length));
                    ApplyVisibleArenaIndices(currentIndices, currentIndices.Length, isStrictlyIncreasing, isIdentity);
                    RestoreArenaSelectionIfNeeded(selectedKeys);
                    StatusStripUpdate($"Showing {VisibleAssetCount:N0} assets");
                }
                finally
                {
                    isRefreshingFilterList = false;
                }
                return;
            }

            var currentAssets = new List<AssetItem>(_eagerVisibleAssets);
            var sorted = await Task.Run(() => AssetListQuery.SortInPlace(currentAssets, sortMember, sortDescending));
            isRefreshingFilterList = true;
            try
            {
                ReplaceVisibleAssetsOwned(sorted);
                RestoreSelectionIfNeeded(selectedKeys);
                StatusStripUpdate($"Showing {VisibleAssetCount} assets");
            }
            finally
            {
                isRefreshingFilterList = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error sorting asset list", ex);
            StatusStripUpdate("Error sorting asset list. See error log.");
        }
    }

    private readonly record struct AssetSelectionKey(AssetHandleKey HandleKey, string? FallbackUniqueId);

    private static bool TryGetAssetSelectionKey(AssetItem? item, out AssetSelectionKey key)
    {
        if (item == null)
        {
            key = default;
            return false;
        }

        if (item.Handle != null && item.Handle.TryGetKey(out var handleKey))
        {
            key = new AssetSelectionKey(handleKey, null);
            return true;
        }

        var uid = item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
        if (!string.IsNullOrEmpty(uid))
        {
            key = new AssetSelectionKey(default, uid);
            return true;
        }

        key = default;
        return false;
    }

    private static string? GetAssetUniqueKey(AssetItem? item)
    {
        if (item == null)
        {
            return null;
        }

        return item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
    }

    private void RestoreSelectionIfNeeded(HashSet<AssetSelectionKey> selectedKeys)
    {
        if (selectedKeys.Count == 0)
        {
            return;
        }

        var selectedItems = AssetListDataGrid.SelectedItems;
        if (selectedItems == null)
        {
            return;
        }

        // VirtualAssetList keeps ItemsSource stable across Reset so DataGrid preserves surviving references automatically.
        // Verify whether all surviving items are already selected; only scan visibleAssets if references were replaced.
        if (selectedItems.Count > 0)
        {
            var preservedCount = 0;
            foreach (var item in selectedItems.OfType<AssetItem>())
            {
                if (TryGetAssetSelectionKey(item, out var key) && selectedKeys.Contains(key))
                {
                    preservedCount++;
                }
            }

            if (preservedCount == selectedKeys.Count || preservedCount == selectedItems.Count)
            {
                return;
            }
        }

        var newSelectedItems = new List<AssetItem>();
        foreach (var item in _eagerVisibleAssets)
        {
            if (TryGetAssetSelectionKey(item, out var key) && selectedKeys.Contains(key))
            {
                newSelectedItems.Add(item);
            }
        }

        if (newSelectedItems.Count == 0 && selectedItems.Count == 0)
        {
            return;
        }

        selectedItems.Clear();
        foreach (var item in newSelectedItems)
        {
            selectedItems.Add(item);
        }
    }

    private void RestoreArenaSelectionIfNeeded(HashSet<AssetSelectionKey> selectedKeys)
    {
        if (selectedKeys.Count == 0)
        {
            return;
        }

        var selectedItems = AssetListDataGrid.SelectedItems;
        if (selectedItems == null)
        {
            return;
        }

        var projectIndex = assetsManager.ProjectIndex;
        var newSelectedItems = new List<AssetItem>(selectedKeys.Count);
        foreach (var key in selectedKeys)
        {
            int arenaIdx = -1;
            if (!key.HandleKey.IsEmpty)
            {
                projectIndex.TryGetIndex(key.HandleKey, out arenaIdx);
            }
            else if (!string.IsNullOrEmpty(key.FallbackUniqueId))
            {
                projectIndex.TryGetIndexByUniqueId(key.FallbackUniqueId, out arenaIdx);
            }

            if (arenaIdx < 0)
            {
                continue;
            }

            var visibleRow = LocateVisibleArenaRowByArenaIndex(arenaIdx);
            if ((uint)visibleRow < (uint)visibleAssetItems.Count)
            {
                newSelectedItems.Add(visibleAssetItems[visibleRow]);
            }
        }

        if (newSelectedItems.Count == 0 && selectedItems.Count == 0)
        {
            return;
        }

        selectedItems.Clear();
        foreach (var item in newSelectedItems)
        {
            selectedItems.Add(item);
        }
    }

    private bool TryMarkExportableArenaIndex(int arenaIndex)
    {
        if (arenaIndex < 0)
        {
            return false;
        }

        var wordIndex = arenaIndex >> 6;
        if (wordIndex >= _exportableArenaBitset.Length)
        {
            var newLen = Math.Max(wordIndex + 1, _exportableArenaBitset.Length == 0 ? 16 : _exportableArenaBitset.Length * 2);
            Array.Resize(ref _exportableArenaBitset, newLen);
        }

        var mask = 1UL << (arenaIndex & 63);
        if ((_exportableArenaBitset[wordIndex] & mask) != 0)
        {
            return false;
        }

        _exportableArenaBitset[wordIndex] |= mask;
        return true;
    }

    private void ResetArenaAssetListState()
    {
        CancelPendingArenaCoalescedSort();
        _isArenaAssetListMode = false;
        exportableArenaIndices.Clear();
        exportableArenaIndices.TrimExcess();
        _exportableArenaBitset = Array.Empty<ulong>();
        visibleArenaIndices = Array.Empty<int>();
        visibleArenaCount = 0;
        visibleArenaIsIdentity = false;
        visibleArenaIsStrictlyIncreasing = false;
        visibleArenaRowByArenaIndex = null;
    }

    private void CancelPendingArenaCoalescedSort()
    {
        Interlocked.Increment(ref _arenaCoalescedSortVersion);
        var cts = _arenaCoalescedSortCts;
        _arenaCoalescedSortCts = null;
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }
            cts.Dispose();
        }
    }

    private AssetItem MaterializeArenaAssetItem(int arenaIndex)
    {
        var arena = assetsManager.ProjectIndex.Arena;
        var handle = arena.MaterializeHandle(arenaIndex);
        return new AssetItem(handle, arenaIndex);
    }

    private List<AssetItem> MaterializeArenaAssetItems(IReadOnlyList<int> indices)
    {
        var count = indices.Count;
        var list = new List<AssetItem>(count);
        var arena = assetsManager.ProjectIndex.Arena;
        var arenaCount = arena.Count;
        for (var i = 0; i < count; i++)
        {
            var arenaIndex = indices[i];
            if ((uint)arenaIndex < (uint)arenaCount)
            {
                list.Add(new AssetItem(arena.MaterializeHandle(arenaIndex), arenaIndex));
            }
        }

        return list;
    }

    private int LocateVisibleArenaRowIndex(AssetItem item)
    {
        if (item?.Handle == null)
        {
            return -1;
        }

        var arenaIndex = item.Handle.ArenaIndex;
        if (arenaIndex < 0 && item.Handle.TryGetKey(out var key))
        {
            if (!assetsManager.ProjectIndex.TryGetIndex(key, out arenaIndex))
            {
                return -1;
            }
        }

        return LocateVisibleArenaRowByArenaIndex(arenaIndex);
    }

    private int LocateVisibleArenaRowByArenaIndex(int arenaIndex)
    {
        if (arenaIndex < 0 || visibleArenaCount <= 0)
        {
            return -1;
        }

        if (visibleArenaIsIdentity)
        {
            return (uint)arenaIndex < (uint)visibleArenaCount ? arenaIndex : -1;
        }

        if (visibleArenaIsStrictlyIncreasing)
        {
            var found = Array.BinarySearch(visibleArenaIndices, 0, visibleArenaCount, arenaIndex);
            return found >= 0 ? found : -1;
        }

        var map = visibleArenaRowByArenaIndex;
        if (map == null)
        {
            map = new Dictionary<int, int>(visibleArenaCount);
            for (var i = 0; i < visibleArenaCount; i++)
            {
                map[visibleArenaIndices[i]] = i;
            }
            visibleArenaRowByArenaIndex = map;
        }

        return map.TryGetValue(arenaIndex, out var row) ? row : -1;
    }

    private static bool IsIdentityArenaIndices(int[] indices, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (indices[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStrictlyIncreasingIndices(int[] indices, int count)
    {
        for (var i = 1; i < count; i++)
        {
            if (indices[i] <= indices[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyVisibleArenaIndices(int[] indices, int count, bool isStrictlyIncreasing, bool isIdentity)
    {
        visibleArenaIndices = indices;
        visibleArenaCount = count;
        visibleArenaIsStrictlyIncreasing = isStrictlyIncreasing;
        visibleArenaIsIdentity = isIdentity;
        visibleArenaRowByArenaIndex = null;
        EnsureAssetListItemsSource();
        visibleAssetItems.ReplaceWithProvider(
            count,
            row => MaterializeArenaAssetItem(visibleArenaIndices[row]),
            lruCapacity: ArenaViewportLruCapacity,
            indexLocator: LocateVisibleArenaRowIndex);
    }

    private void QueueCoalescedArenaSort()
    {
        if (!_isArenaAssetListMode || string.IsNullOrEmpty(assetListSortMember) || visibleArenaCount <= 1)
        {
            return;
        }

        CancelPendingArenaCoalescedSort();
        var cts = new CancellationTokenSource();
        _arenaCoalescedSortCts = cts;
        var token = cts.Token;
        var sortVersion = _arenaCoalescedSortVersion;
        var sessionId = lazyIndexingSessionId;
        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var expectedCount = visibleArenaCount;
        var snapshot = new int[expectedCount];
        Array.Copy(visibleArenaIndices, snapshot, expectedCount);
        var arena = assetsManager.ProjectIndex.Arena;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60, token);
                token.ThrowIfCancellationRequested();
                AssetListQuery.SortArenaInPlace(arena, snapshot, snapshot.Length, sortMember, sortDescending);
                token.ThrowIfCancellationRequested();

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested
                        || sortVersion != _arenaCoalescedSortVersion
                        || sessionId != lazyIndexingSessionId
                        || !_isArenaAssetListMode
                        || visibleArenaCount != expectedCount
                        || !string.Equals(assetListSortMember, sortMember, StringComparison.Ordinal)
                        || assetListSortDescending != sortDescending)
                    {
                        return;
                    }

                    var selectedKeys = new HashSet<AssetSelectionKey>();
                    if (AssetListDataGrid.SelectedItems != null)
                    {
                        foreach (var item in AssetListDataGrid.SelectedItems.OfType<AssetItem>())
                        {
                            if (TryGetAssetSelectionKey(item, out var key))
                            {
                                selectedKeys.Add(key);
                            }
                        }
                    }

                    isRefreshingFilterList = true;
                    try
                    {
                        ApplyVisibleArenaIndices(snapshot, snapshot.Length, isStrictlyIncreasing: false, isIdentity: false);
                        RestoreArenaSelectionIfNeeded(selectedKeys);
                    }
                    finally
                    {
                        isRefreshingFilterList = false;
                    }
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warning($"Coalesced arena sort failed: {ex.Message}");
            }
        }, token);
    }

    private static bool AssetMatchesClassFilter(AssetItem item, AssetClassItem classFilter)
    {
        return AssetListQuery.MatchesClassFilter(item, classFilter);
    }

    private static List<AssetItem> SortAssetList(IEnumerable<AssetItem>? assets, string? sortMember, bool descending)
    {
        return AssetListQuery.Sort(assets, sortMember, descending);
    }

    private static int CompareAssetItems(AssetItem? left, AssetItem? right, string sortMember, bool descending)
    {
        return AssetListQuery.Compare(left, right, AssetListQuery.ResolveSortKey(sortMember), descending);
    }
    private static string GetAssetCellText(AssetItem item, string? member)
    {
        return member switch
        {
            "Container" => item.Container ?? string.Empty,
            "DisplayType" or "Type" => item.DisplayType ?? string.Empty,
            "PathID" => item.PathIDString ?? string.Empty,
            "FullSize" or "Size" => item.FullSize.ToString(CultureInfo.InvariantCulture),
            _ => item.Name ?? string.Empty
        };
    }

    private void ReplaceVisibleAssets(IReadOnlyList<AssetItem>? items)
    {
        _isArenaAssetListMode = false;
        _eagerVisibleAssets = items is { Count: > 0 }
            ? items.Where(static x => x != null).ToList()
            : new List<AssetItem>();
        RefreshAssetListItems();
    }

    private void ReplaceVisibleAssetsOwned(List<AssetItem> ownedItems)
    {
        _isArenaAssetListMode = false;
        _eagerVisibleAssets = ownedItems;
        RefreshAssetListItems();
    }

    private void SyncExportableAssetHandleIds()
    {
        lazyAssetItemsByHandleId.Clear();
        exportableAssetHandleIds.Clear();
        exportableAssetTypes.Clear();
        foreach (var item in _eagerExportableAssets)
        {
            if (item.Handle != null && item.Handle.TryGetKey(out var handleKey))
            {
                lazyAssetItemsByHandleId[handleKey] = item;
                exportableAssetHandleIds.Add(handleKey);
            }
            exportableAssetTypes.Add(item.Type);
        }
    }

    private void ApplyExportableAssetIndexes(BuildAssetStructuresResult result)
    {
        lazyAssetItemsByHandleId = result.LazyAssetItemsByHandleId ?? new Dictionary<AssetHandleKey, AssetItem>();
        exportableAssetHandleIds = result.ExportableAssetHandleIds ?? new HashSet<AssetHandleKey>();
        exportableAssetTypes = result.ExportableAssetTypes ?? new HashSet<ClassIDType>();
    }

    private void EnsureAssetListItemsSource()
    {
        if (!ReferenceEquals(AssetListDataGrid.ItemsSource, visibleAssetItems))
        {
            AssetListDataGrid.ItemsSource = visibleAssetItems;
        }
    }

    private void RefreshAssetListItems()
    {
        EnsureAssetListItemsSource();
        visibleAssetItems.ReplaceBacking(_eagerVisibleAssets);
    }

    private void UpdateAssetListSortHeaderIndicators()
    {
        foreach (var column in AssetListDataGrid.Columns)
        {
            var sortMember = column.SortMemberPath ?? column.Header?.ToString();
            var baseHeader = sortMember switch
            {
                "Name" => "Name",
                "Container" => "Container",
                "DisplayType" or "Type" => "Type",
                "PathID" => "PathID",
                "FullSize" or "Size" => "Size",
                _ => column.Header?.ToString() ?? string.Empty
            };

            column.Header = sortMember == assetListSortMember
                ? $"{baseHeader} {(assetListSortDescending ? "(desc)" : "(asc)")}"
                : baseHeader;
        }
    }

    private void AppendFilteredAssetsToVisible(List<AssetItem> newItems)
    {
        if (newItems == null || newItems.Count == 0) return;

        EnsureAssetListItemsSource();
        var filterText = listSearch?.Text?.Trim();
        var textFilter = AssetListQuery.TextFilter.Create(filterText);
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        var matchingBatch = new List<AssetItem>();
        foreach (var x in newItems)
        {
            if (x == null)
            {
                continue;
            }

            if (classFilter != null)
            {
                if (!AssetMatchesClassFilter(x, classFilter))
                {
                    continue;
                }
            }
            else if (selectedTypes != null)
            {
                if (selectedTypes.Count == 0 || !selectedTypes.Contains(x.Type))
                {
                    continue;
                }
            }

            if (textFilter != null && !textFilter.Matches(x))
            {
                continue;
            }

            matchingBatch.Add(x);
        }

        if (matchingBatch.Count == 0)
        {
            return;
        }

        visibleAssetItems.BeginUpdate();
        try
        {
            if (!visibleAssetItems.IsBackedBy(_eagerVisibleAssets))
            {
                visibleAssetItems.ReplaceBacking(_eagerVisibleAssets);
            }

            AssetListQuery.MergeSortedBatch(_eagerVisibleAssets, matchingBatch, assetListSortMember, assetListSortDescending);
        }
        finally
        {
            visibleAssetItems.EndUpdate();
        }

        StatusStripUpdate($"Showing {VisibleAssetCount} assets");
    }

    private void AppendFilteredArenaIndicesToVisible(List<int> newArenaIndices)
    {
        if (newArenaIndices == null || newArenaIndices.Count == 0)
        {
            return;
        }

        var arena = assetsManager.ProjectIndex.Arena;
        var filterText = listSearch?.Text?.Trim();
        var textFilter = AssetListQuery.TextFilter.Create(filterText);
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        var matchingIndices = new List<int>(newArenaIndices.Count);
        for (var i = 0; i < newArenaIndices.Count; i++)
        {
            var idx = newArenaIndices[i];
            if (AssetListQuery.AcceptsArena(arena, idx, classFilter, selectedTypes, textFilter))
            {
                matchingIndices.Add(idx);
            }
        }

        if (matchingIndices.Count == 0)
        {
            return;
        }

        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;

        if (string.IsNullOrEmpty(sortMember))
        {
            var oldCount = visibleArenaCount;
            var newCount = oldCount + matchingIndices.Count;
            if (visibleArenaIndices.Length < newCount)
            {
                var nextCap = Math.Max(newCount, visibleArenaIndices.Length == 0 ? 1024 : visibleArenaIndices.Length * 2);
                Array.Resize(ref visibleArenaIndices, nextCap);
            }

            var strictlyIncreasing = oldCount == 0 || visibleArenaIsStrictlyIncreasing;
            var identity = oldCount == 0 || visibleArenaIsIdentity;
            var prev = oldCount > 0 ? visibleArenaIndices[oldCount - 1] : -1;

            for (var i = 0; i < matchingIndices.Count; i++)
            {
                var idx = matchingIndices[i];
                var pos = oldCount + i;
                visibleArenaIndices[pos] = idx;
                if (strictlyIncreasing && idx <= prev)
                {
                    strictlyIncreasing = false;
                }
                if (identity && idx != pos)
                {
                    identity = false;
                }
                prev = idx;
            }

            ApplyVisibleArenaIndices(visibleArenaIndices, newCount, strictlyIncreasing, identity);
        }
        else if (visibleArenaCount < ArenaInlineSortedMergeThreshold)
        {
            var incomingArray = matchingIndices.ToArray();
            var merged = AssetListQuery.MergeSortedArenaBatch(
                arena,
                visibleArenaIndices,
                visibleArenaCount,
                incomingArray,
                incomingArray.Length,
                sortMember,
                sortDescending);
            ApplyVisibleArenaIndices(merged, merged.Length, isStrictlyIncreasing: false, isIdentity: false);
        }
        else
        {
            var oldCount = visibleArenaCount;
            var newCount = oldCount + matchingIndices.Count;
            if (visibleArenaIndices.Length < newCount)
            {
                var nextCap = Math.Max(newCount, visibleArenaIndices.Length * 2);
                Array.Resize(ref visibleArenaIndices, nextCap);
            }

            matchingIndices.CopyTo(visibleArenaIndices, oldCount);
            ApplyVisibleArenaIndices(visibleArenaIndices, newCount, isStrictlyIncreasing: false, isIdentity: false);
            QueueCoalescedArenaSort();
        }

        StatusStripUpdate($"Showing {VisibleAssetCount:N0} assets");
    }
}

/// <summary>
/// Pure (UI-free) filtering and sorting engine for the asset list. Filtering is order-preserving and runs in parallel
/// over large snapshots; text matching uses a vectorized <see cref="SearchValues{T}"/> needle.
/// </summary>
internal static class AssetListQuery
{
    internal const int ParallelFilterThreshold = 4096;
    internal const int FilterChunkSize = 2048;
    internal const int ParallelSortThreshold = 16384;

    private const int SmallTypeCacheSize = 512;
    private static readonly string?[] SmallNormalDisplayTypes = BuildSmallDisplayTypes(fbx: false);
    private static readonly string?[] SmallFbxDisplayTypes = BuildSmallDisplayTypes(fbx: true);
    private static readonly FrozenDictionary<ClassIDType, (string Normal, string Fbx)> SparseDisplayTypes = BuildSparseDisplayTypes();
    private static readonly ConcurrentDictionary<(int TypeId, bool IsFbx), string> UndefinedDisplayTypes = new();

    internal enum SortKey
    {
        None,
        PathID,
        FullSize,
        Container,
        DisplayType,
        Name,
    }

    internal static string GetCachedDisplayType(ClassIDType type, bool isFbx)
    {
        var id = (int)type;
        if ((uint)id < SmallTypeCacheSize)
        {
            var table = isFbx ? SmallFbxDisplayTypes : SmallNormalDisplayTypes;
            var cached = table[id];
            if (cached != null)
            {
                return cached;
            }
        }
        else if (SparseDisplayTypes.TryGetValue(type, out var pair))
        {
            return isFbx ? pair.Fbx : pair.Normal;
        }

        return UndefinedDisplayTypes.GetOrAdd(
            (id, isFbx),
            static key =>
            {
                var t = (ClassIDType)key.TypeId;
                var typeStr = AssetItem.GetCachedTypeString(t);
                return FormatDisplayType(t, typeStr, key.IsFbx);
            });
    }

    private static string FormatDisplayType(ClassIDType type, string typeString, bool isFbx)
    {
        var display = type switch
        {
            ClassIDType.PrefabInstance => "Prefab (Composite)",
            ClassIDType.GameObject => "GameObject (Hierarchy Node)",
            ClassIDType.MonoBehaviour => "MonoBehaviour (Script Instance)",
            ClassIDType.Mesh => "Mesh (Geometry)",
            ClassIDType.Material => "Material (Shader Settings)",
            _ when IsComponentType(type) => $"{typeString} (Component)",
            _ => typeString
        };

        return isFbx ? $"{display} (FBX sub-asset)" : display;
    }

    private static bool IsComponentType(ClassIDType type)
    {
        return type is ClassIDType.Transform
            or ClassIDType.MeshRenderer
            or ClassIDType.MeshFilter
            or ClassIDType.SkinnedMeshRenderer
            or ClassIDType.Animator
            or ClassIDType.Animation
            or ClassIDType.Component
            or ClassIDType.RectTransform
            or ClassIDType.Behaviour
            or ClassIDType.MonoBehaviour;
    }

    private static string?[] BuildSmallDisplayTypes(bool fbx)
    {
        var table = new string?[SmallTypeCacheSize];
        foreach (ClassIDType value in Enum.GetValues<ClassIDType>())
        {
            var id = (int)value;
            if ((uint)id < SmallTypeCacheSize && table[id] == null)
            {
                var baseTypeStr = AssetItem.GetCachedTypeString(value);
                table[id] = FormatDisplayType(value, baseTypeStr, fbx);
            }
        }

        return table;
    }

    private static FrozenDictionary<ClassIDType, (string Normal, string Fbx)> BuildSparseDisplayTypes()
    {
        var map = new Dictionary<ClassIDType, (string Normal, string Fbx)>();
        foreach (ClassIDType value in Enum.GetValues<ClassIDType>())
        {
            var id = (int)value;
            if ((uint)id >= SmallTypeCacheSize && !map.ContainsKey(value))
            {
                var baseTypeStr = AssetItem.GetCachedTypeString(value);
                map[value] = (
                    FormatDisplayType(value, baseTypeStr, isFbx: false),
                    FormatDisplayType(value, baseTypeStr, isFbx: true));
            }
        }

        return map.ToFrozenDictionary();
    }

    internal sealed class TextFilter
    {
        private readonly SearchValues<string> _needle;
        private readonly ConcurrentDictionary<ClassIDType, bool> _normalTypeMatchCache = new();
        private readonly ConcurrentDictionary<ClassIDType, bool> _fbxTypeMatchCache = new();

        private TextFilter(string text)
        {
            _needle = SearchValues.Create(new[] { text }, StringComparison.OrdinalIgnoreCase);
        }

        public static TextFilter? Create(string? text)
        {
            return string.IsNullOrEmpty(text) ? null : new TextFilter(text);
        }

        public bool Matches(AssetItem item)
        {
            if (Has(item.Name) || Has(item.Container) || Has(item.TypeString))
            {
                return true;
            }

            var displayType = item.DisplayType;
            if (!ReferenceEquals(displayType, item.TypeString) && Has(displayType))
            {
                return true;
            }

            Span<char> pathIdBuffer = stackalloc char[32];
            item.TryGetPathIDSearchSpan(pathIdBuffer, out var pathIdSpan);
            return !pathIdSpan.IsEmpty && pathIdSpan.ContainsAny(_needle);
        }

        public bool Matches(AssetIndexArena arena, int arenaIndex)
        {
            return Matches(arena, arenaIndex, arena.GetType(arenaIndex));
        }

        internal bool Matches(AssetIndexArena arena, int arenaIndex, ClassIDType type)
        {
            if (_normalTypeMatchCache.GetOrAdd(type, EvaluateNormalTypeMatch))
            {
                return true;
            }

            if (Has(arena.GetName(arenaIndex)))
            {
                return true;
            }

            var container = arena.GetContainer(arenaIndex);
            if (Has(container))
            {
                return true;
            }

            if (AssetItem.ContainsFbxSegment(container)
                && _fbxTypeMatchCache.GetOrAdd(type, EvaluateFbxTypeMatch))
            {
                return true;
            }

            Span<char> pathIdBuffer = stackalloc char[32];
            var pathId = arena.GetPathId(arenaIndex);
            if (pathId.TryFormat(pathIdBuffer, out var written, provider: CultureInfo.InvariantCulture))
            {
                return pathIdBuffer.Slice(0, written).ContainsAny(_needle);
            }

            return false;
        }

        private bool EvaluateNormalTypeMatch(ClassIDType type)
        {
            var typeString = AssetItem.GetCachedTypeString(type);
            if (Has(typeString))
            {
                return true;
            }

            var normalDisplayType = GetCachedDisplayType(type, isFbx: false);
            return !ReferenceEquals(normalDisplayType, typeString) && Has(normalDisplayType);
        }

        private bool EvaluateFbxTypeMatch(ClassIDType type)
        {
            return Has(GetCachedDisplayType(type, isFbx: true));
        }

        private bool Has(string? value)
        {
            return !string.IsNullOrEmpty(value) && value.AsSpan().ContainsAny(_needle);
        }
    }

    internal static bool MatchesClassFilter(AssetItem item, AssetClassItem classFilter)
    {
        return (int)item.Type == classFilter.ClassID
            && string.Equals(
                item.SourceFile?.unityVersion ?? string.Empty,
                classFilter.UnityVersion ?? string.Empty,
                StringComparison.Ordinal);
    }

    internal static bool MatchesClassFilter(AssetIndexArena arena, int arenaIndex, AssetClassItem classFilter)
    {
        return MatchesClassFilter(arena, arenaIndex, arena.GetType(arenaIndex), classFilter);
    }

    private static bool MatchesClassFilter(AssetIndexArena arena, int arenaIndex, ClassIDType type, AssetClassItem classFilter)
    {
        if ((int)type != classFilter.ClassID)
        {
            return false;
        }

        return string.Equals(
            arena.GetSourceFile(arenaIndex)?.unityVersion ?? string.Empty,
            classFilter.UnityVersion ?? string.Empty,
            StringComparison.Ordinal);
    }

    internal static bool AcceptsArena(
        AssetIndexArena arena,
        int arenaIndex,
        AssetClassItem? classFilter,
        HashSet<ClassIDType>? selectedTypes,
        TextFilter? textFilter)
    {
        var type = arena.GetType(arenaIndex);
        if (classFilter != null)
        {
            if (!MatchesClassFilter(arena, arenaIndex, type, classFilter))
            {
                return false;
            }
        }
        else if (selectedTypes != null)
        {
            if (selectedTypes.Count == 0 || !selectedTypes.Contains(type))
            {
                return false;
            }
        }

        return textFilter == null || textFilter.Matches(arena, arenaIndex, type);
    }

    internal static int[] FilterArena(
        AssetIndexArena arena,
        ReadOnlySpan<int> sourceIndices,
        AssetClassItem? classFilter,
        HashSet<ClassIDType>? selectedTypes,
        string? filterText,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(arena);
        var count = sourceIndices.Length;
        if (count == 0)
        {
            return Array.Empty<int>();
        }

        var textFilter = TextFilter.Create(filterText);
        if (classFilter == null && selectedTypes == null && textFilter == null)
        {
            token.ThrowIfCancellationRequested();
            return sourceIndices.ToArray();
        }

        if (classFilter == null && selectedTypes != null && selectedTypes.Count == 0)
        {
            token.ThrowIfCancellationRequested();
            return Array.Empty<int>();
        }

        if (count < ParallelFilterThreshold)
        {
            var matches = new List<int>();
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var idx = sourceIndices[i];
                if (AcceptsArena(arena, idx, classFilter, selectedTypes, textFilter))
                {
                    matches.Add(idx);
                }
            }

            return matches.ToArray();
        }

        var rented = ArrayPool<int>.Shared.Rent(count);
        try
        {
            sourceIndices.CopyTo(rented);
            return FilterArena(arena, rented, count, classFilter, selectedTypes, filterText, token);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    internal static int[] FilterArena(
        AssetIndexArena arena,
        int[] sourceIndices,
        int sourceCount,
        AssetClassItem? classFilter,
        HashSet<ClassIDType>? selectedTypes,
        string? filterText,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(sourceIndices);
        if (sourceCount <= 0)
        {
            return Array.Empty<int>();
        }

        var textFilter = TextFilter.Create(filterText);
        if (classFilter == null && selectedTypes == null && textFilter == null)
        {
            token.ThrowIfCancellationRequested();
            var copy = new int[sourceCount];
            Array.Copy(sourceIndices, copy, sourceCount);
            return copy;
        }

        if (classFilter == null && selectedTypes != null && selectedTypes.Count == 0)
        {
            token.ThrowIfCancellationRequested();
            return Array.Empty<int>();
        }

        // AssetIndexArena getters synchronize on _syncRoot: avoid high-thread mutex convoying on pure type filters
        // and bound text-search parallelism so lock contention stays minimal while SearchValues runs concurrently.
        if (sourceCount < ParallelFilterThreshold || textFilter == null)
        {
            var matches = new List<int>(Math.Min(sourceCount, 1024));
            for (var i = 0; i < sourceCount; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var idx = sourceIndices[i];
                if (AcceptsArena(arena, idx, classFilter, selectedTypes, textFilter))
                {
                    matches.Add(idx);
                }
            }

            return matches.ToArray();
        }

        var chunkCount = (sourceCount + FilterChunkSize - 1) / FilterChunkSize;
        var chunkResults = new List<int>[chunkCount];
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };

        Parallel.For(0, chunkCount, options, chunk =>
        {
            token.ThrowIfCancellationRequested();
            var start = chunk * FilterChunkSize;
            var end = Math.Min(sourceCount, start + FilterChunkSize);
            var local = new List<int>();
            for (var i = start; i < end; i++)
            {
                var idx = sourceIndices[i];
                if (AcceptsArena(arena, idx, classFilter, selectedTypes, textFilter))
                {
                    local.Add(idx);
                }
            }

            chunkResults[chunk] = local;
        });

        var total = 0;
        for (var c = 0; c < chunkCount; c++)
        {
            total += chunkResults[c].Count;
        }

        if (total == 0)
        {
            return Array.Empty<int>();
        }

        var result = new int[total];
        var writeOffset = 0;
        for (var c = 0; c < chunkCount; c++)
        {
            var local = chunkResults[c];
            if (local.Count > 0)
            {
                local.CopyTo(result, writeOffset);
                writeOffset += local.Count;
            }
        }

        return result;
    }

    internal static void SortArenaInPlace(AssetIndexArena arena, int[] indices, string? sortMember, bool descending)
    {
        SortArenaInPlace(arena, indices, indices?.Length ?? 0, sortMember, descending);
    }

    internal static void SortArenaInPlace(
        AssetIndexArena arena,
        int[] indices,
        int count,
        string? sortMember,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(indices);
        if (count <= 1 || string.IsNullOrEmpty(sortMember))
        {
            return;
        }

        var key = ResolveSortKey(sortMember);
        var slots = ArrayPool<int>.Shared.Rent(count);
        var scratchSlots = ArrayPool<int>.Shared.Rent(count);
        var reordered = ArrayPool<int>.Shared.Rent(count);
        var pathIds = ArrayPool<long>.Shared.Rent(count);
        long[]? numericKeys = null;
        string[]? textKeys = null;

        try
        {
            for (var i = 0; i < count; i++)
            {
                slots[i] = i;
                pathIds[i] = arena.GetPathId(indices[i]);
            }

            Comparison<int> comparison;
            switch (key)
            {
                case SortKey.PathID:
                    comparison = (l, r) =>
                    {
                        var cmp = pathIds[l].CompareTo(pathIds[r]);
                        if (descending) cmp = -cmp;
                        if (cmp != 0) return cmp;
                        return CompareArenaTieBreak(arena, indices[l], indices[r], pathIds[l], pathIds[r]);
                    };
                    break;

                case SortKey.FullSize:
                    numericKeys = ArrayPool<long>.Shared.Rent(count);
                    for (var i = 0; i < count; i++)
                    {
                        numericKeys[i] = arena.GetByteSize(indices[i]);
                    }
                    var sizesRef = numericKeys;
                    comparison = (l, r) =>
                    {
                        var cmp = sizesRef[l].CompareTo(sizesRef[r]);
                        if (descending) cmp = -cmp;
                        if (cmp != 0) return cmp;
                        return CompareArenaTieBreak(arena, indices[l], indices[r], pathIds[l], pathIds[r]);
                    };
                    break;

                case SortKey.Name:
                    textKeys = ArrayPool<string>.Shared.Rent(count);
                    for (var i = 0; i < count; i++)
                    {
                        textKeys[i] = arena.GetName(indices[i]);
                    }
                    var namesRef = textKeys;
                    comparison = (l, r) =>
                    {
                        var cmp = CompareNullableText(namesRef[l], namesRef[r]);
                        if (descending) cmp = -cmp;
                        if (cmp != 0) return cmp;
                        return CompareArenaTieBreak(arena, indices[l], indices[r], pathIds[l], pathIds[r]);
                    };
                    break;

                case SortKey.Container:
                    textKeys = ArrayPool<string>.Shared.Rent(count);
                    for (var i = 0; i < count; i++)
                    {
                        textKeys[i] = arena.GetContainer(indices[i]);
                    }
                    var containersRef = textKeys;
                    comparison = (l, r) =>
                    {
                        var cmp = CompareNullableText(containersRef[l], containersRef[r]);
                        if (descending) cmp = -cmp;
                        if (cmp != 0) return cmp;
                        return CompareArenaTieBreak(arena, indices[l], indices[r], pathIds[l], pathIds[r]);
                    };
                    break;

                case SortKey.DisplayType:
                    textKeys = ArrayPool<string>.Shared.Rent(count);
                    for (var i = 0; i < count; i++)
                    {
                        var idx = indices[i];
                        var type = arena.GetType(idx);
                        var isFbx = AssetItem.ContainsFbxSegment(arena.GetContainer(idx));
                        textKeys[i] = GetCachedDisplayType(type, isFbx);
                    }
                    var displayRef = textKeys;
                    comparison = (l, r) =>
                    {
                        var cmp = CompareNullableText(displayRef[l], displayRef[r]);
                        if (descending) cmp = -cmp;
                        if (cmp != 0) return cmp;
                        return CompareArenaTieBreak(arena, indices[l], indices[r], pathIds[l], pathIds[r]);
                    };
                    break;

                default:
                    comparison = (l, r) => CompareArenaTieBreak(arena, indices[l], indices[r], pathIds[l], pathIds[r]);
                    break;
            }

            if (count < ParallelSortThreshold)
            {
                new Span<int>(slots, 0, count).Sort(comparison);
            }
            else
            {
                ParallelSortIntCore(slots, scratchSlots, count, comparison);
            }

            for (var i = 0; i < count; i++)
            {
                reordered[i] = indices[slots[i]];
            }

            Array.Copy(reordered, 0, indices, 0, count);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slots);
            ArrayPool<int>.Shared.Return(scratchSlots);
            ArrayPool<int>.Shared.Return(reordered);
            ArrayPool<long>.Shared.Return(pathIds);
            if (numericKeys != null)
            {
                ArrayPool<long>.Shared.Return(numericKeys);
            }
            if (textKeys != null)
            {
                ArrayPool<string>.Shared.Return(textKeys, clearArray: true);
            }
        }
    }

    internal static void MergeSortedBatch(
        List<AssetItem> sortedExisting,
        List<AssetItem> incomingUnsorted,
        string? sortMember,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(sortedExisting);
        if (incomingUnsorted == null || incomingUnsorted.Count == 0)
        {
            return;
        }

        if (string.IsNullOrEmpty(sortMember))
        {
            sortedExisting.AddRange(incomingUnsorted);
            return;
        }

        SortInPlace(incomingUnsorted, sortMember, descending);

        var existingCount = sortedExisting.Count;
        var incomingCount = incomingUnsorted.Count;
        if (existingCount == 0)
        {
            sortedExisting.AddRange(incomingUnsorted);
            return;
        }

        var key = ResolveSortKey(sortMember);
        if (Compare(sortedExisting[existingCount - 1], incomingUnsorted[0], key, descending) <= 0)
        {
            sortedExisting.AddRange(incomingUnsorted);
            return;
        }

        CollectionsMarshal.SetCount(sortedExisting, existingCount + incomingCount);
        var dst = CollectionsMarshal.AsSpan(sortedExisting);
        var batch = CollectionsMarshal.AsSpan(incomingUnsorted);

        var i = existingCount - 1;
        var j = incomingCount - 1;
        var write = existingCount + incomingCount - 1;

        while (i >= 0 && j >= 0)
        {
            if (Compare(dst[i], batch[j], key, descending) > 0)
            {
                dst[write--] = dst[i--];
            }
            else
            {
                dst[write--] = batch[j--];
            }
        }

        while (j >= 0)
        {
            dst[write--] = batch[j--];
        }
    }

    internal static int[] MergeSortedArenaBatch(
        AssetIndexArena arena,
        int[] sortedExisting,
        int existingCount,
        int[] incomingUnsorted,
        int incomingCount,
        string? sortMember,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(sortedExisting);
        ArgumentNullException.ThrowIfNull(incomingUnsorted);

        var totalCount = existingCount + incomingCount;
        if (totalCount == 0)
        {
            return Array.Empty<int>();
        }

        if (incomingCount == 0)
        {
            var copy = new int[existingCount];
            Array.Copy(sortedExisting, copy, existingCount);
            return copy;
        }

        if (string.IsNullOrEmpty(sortMember))
        {
            var mergedUnsorted = new int[totalCount];
            if (existingCount > 0)
            {
                Array.Copy(sortedExisting, 0, mergedUnsorted, 0, existingCount);
            }
            Array.Copy(incomingUnsorted, 0, mergedUnsorted, existingCount, incomingCount);
            return mergedUnsorted;
        }

        var incomingCopy = new int[incomingCount];
        Array.Copy(incomingUnsorted, incomingCopy, incomingCount);
        SortArenaInPlace(arena, incomingCopy, incomingCount, sortMember, descending);

        if (existingCount == 0)
        {
            return incomingCopy;
        }

        var key = ResolveSortKey(sortMember);
        var merged = new int[totalCount];
        var incomingPathIds = ArrayPool<long>.Shared.Rent(incomingCount);
        long[]? incomingNums = null;
        string[]? incomingTexts = null;

        try
        {
            for (var j = 0; j < incomingCount; j++)
            {
                incomingPathIds[j] = arena.GetPathId(incomingCopy[j]);
            }

            if (key == SortKey.FullSize)
            {
                incomingNums = ArrayPool<long>.Shared.Rent(incomingCount);
                for (var j = 0; j < incomingCount; j++) incomingNums[j] = arena.GetByteSize(incomingCopy[j]);
            }
            else if (key is SortKey.Name or SortKey.Container or SortKey.DisplayType)
            {
                incomingTexts = ArrayPool<string>.Shared.Rent(incomingCount);
                for (var j = 0; j < incomingCount; j++) incomingTexts[j] = ExtractArenaTextKey(arena, incomingCopy[j], key);
            }

            // Fast-path when the entire incoming batch appends cleanly after sortedExisting.
            if (CompareExistingToIncoming(
                arena,
                sortedExisting[existingCount - 1],
                incomingCopy[0],
                incomingPathIds[0],
                incomingNums != null ? incomingNums[0] : 0L,
                incomingTexts != null ? incomingTexts[0] : null,
                key,
                descending) <= 0)
            {
                Array.Copy(sortedExisting, 0, merged, 0, existingCount);
                Array.Copy(incomingCopy, 0, merged, existingCount, incomingCount);
                return merged;
            }

            // Galloping + binary-search merge: O(K * log(N / K + 1)) arena key lookups and O(N + K) bulk array copies.
            int ei = 0, w = 0;
            for (var ij = 0; ij < incomingCount; ij++)
            {
                var incIdx = incomingCopy[ij];
                var incPathId = incomingPathIds[ij];
                var incNum = incomingNums != null ? incomingNums[ij] : 0L;
                var incText = incomingTexts != null ? incomingTexts[ij] : null;

                if (ei >= existingCount)
                {
                    var remIncoming = incomingCount - ij;
                    Array.Copy(incomingCopy, ij, merged, w, remIncoming);
                    w += remIncoming;
                    break;
                }

                if (CompareExistingToIncoming(arena, sortedExisting[ei], incIdx, incPathId, incNum, incText, key, descending) <= 0)
                {
                    var maxOffset = existingCount - ei;
                    var prevOffset = 0;
                    var offset = 1;
                    while (offset < maxOffset)
                    {
                        if (CompareExistingToIncoming(arena, sortedExisting[ei + offset], incIdx, incPathId, incNum, incText, key, descending) > 0)
                        {
                            break;
                        }

                        prevOffset = offset;
                        offset = offset > (int.MaxValue - 1) >> 1 ? maxOffset : (offset << 1) + 1;
                    }

                    var lo = ei + prevOffset + 1;
                    var hi = ei + Math.Min(offset, maxOffset);
                    while (lo < hi)
                    {
                        var mid = lo + ((hi - lo) >> 1);
                        if (CompareExistingToIncoming(arena, sortedExisting[mid], incIdx, incPathId, incNum, incText, key, descending) > 0)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid + 1;
                        }
                    }

                    var copyLen = lo - ei;
                    if (copyLen > 0)
                    {
                        Array.Copy(sortedExisting, ei, merged, w, copyLen);
                        w += copyLen;
                        ei = lo;
                    }
                }

                merged[w++] = incIdx;
            }

            if (ei < existingCount)
            {
                Array.Copy(sortedExisting, ei, merged, w, existingCount - ei);
            }

            return merged;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(incomingPathIds);
            if (incomingNums != null) ArrayPool<long>.Shared.Return(incomingNums);
            if (incomingTexts != null) ArrayPool<string>.Shared.Return(incomingTexts, clearArray: true);
        }
    }

    private static int CompareExistingToIncoming(
        AssetIndexArena arena,
        int existingIdx,
        int incomingIdx,
        long incomingPathId,
        long incomingNum,
        string? incomingText,
        SortKey key,
        bool descending)
    {
        long existingPathId;
        int cmp;
        switch (key)
        {
            case SortKey.PathID:
                existingPathId = arena.GetPathId(existingIdx);
                cmp = existingPathId.CompareTo(incomingPathId);
                if (descending) cmp = -cmp;
                if (cmp != 0) return cmp;
                return CompareArenaTieBreak(arena, existingIdx, incomingIdx, existingPathId, incomingPathId);

            case SortKey.FullSize:
                cmp = arena.GetByteSize(existingIdx).CompareTo(incomingNum);
                if (descending) cmp = -cmp;
                if (cmp != 0) return cmp;
                existingPathId = arena.GetPathId(existingIdx);
                return CompareArenaTieBreak(arena, existingIdx, incomingIdx, existingPathId, incomingPathId);

            case SortKey.Name:
            case SortKey.Container:
            case SortKey.DisplayType:
                cmp = CompareNullableText(ExtractArenaTextKey(arena, existingIdx, key), incomingText);
                if (descending) cmp = -cmp;
                if (cmp != 0) return cmp;
                existingPathId = arena.GetPathId(existingIdx);
                return CompareArenaTieBreak(arena, existingIdx, incomingIdx, existingPathId, incomingPathId);

            default:
                existingPathId = arena.GetPathId(existingIdx);
                return CompareArenaTieBreak(arena, existingIdx, incomingIdx, existingPathId, incomingPathId);
        }
    }

    internal static List<AssetClassItem> BuildArenaAssetClassItems(AssetIndexArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        var count = arena.Count;
        if (count == 0)
        {
            return new List<AssetClassItem>();
        }

        var countsByType = new Dictionary<ClassIDType, int>(64);
        for (var i = 0; i < count; i++)
        {
            var type = arena.GetType(i);
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(countsByType, type, out _);
            entry++;
        }

        var items = new List<AssetClassItem>(countsByType.Count);
        foreach (var kvp in countsByType)
        {
            var classId = (int)kvp.Key;
            var name = AssetItem.GetCachedTypeString(kvp.Key);
            items.Add(new AssetClassItem
            {
                ClassID = classId,
                Name = name,
                Namespace = string.Empty,
                Assembly = string.Empty,
                UnityVersion = string.Empty,
                SourceFile = "Project Index",
                ObjectCount = kvp.Value,
                SourceKind = "Indexed",
                SerializedType = new SerializedType
                {
                    classID = classId,
                    m_KlassName = name
                }
            });
        }

        items.Sort(static (a, b) =>
        {
            var cmp = string.Compare(a.UnityVersion, b.UnityVersion, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = a.ClassID.CompareTo(b.ClassID);
            if (cmp != 0) return cmp;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return items;
    }

    private static string ExtractArenaTextKey(AssetIndexArena arena, int arenaIndex, SortKey key)
    {
        return key switch
        {
            SortKey.Name => arena.GetName(arenaIndex),
            SortKey.Container => arena.GetContainer(arenaIndex),
            SortKey.DisplayType => GetCachedDisplayType(arena.GetType(arenaIndex), AssetItem.ContainsFbxSegment(arena.GetContainer(arenaIndex))),
            _ => string.Empty
        };
    }

    private static int CompareArenaTieBreak(AssetIndexArena arena, int leftArenaIndex, int rightArenaIndex, long leftPathId, long rightPathId)
    {
        var cmp = leftPathId.CompareTo(rightPathId);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = CompareArenaOrdinal(leftArenaIndex, rightArenaIndex);
        if (cmp != 0)
        {
            return cmp;
        }

        return CompareNullableText(arena.GetName(leftArenaIndex), arena.GetName(rightArenaIndex));
    }

    private static int CompareArenaOrdinal(int leftOrdinal, int rightOrdinal)
    {
        if (leftOrdinal == rightOrdinal)
        {
            return 0;
        }

        Span<char> leftBuf = stackalloc char[16];
        Span<char> rightBuf = stackalloc char[16];
        leftBuf[0] = ' ';
        leftBuf[1] = '#';
        rightBuf[0] = ' ';
        rightBuf[1] = '#';

        if (leftOrdinal.TryFormat(leftBuf.Slice(2), out var leftLen, provider: CultureInfo.InvariantCulture)
            && rightOrdinal.TryFormat(rightBuf.Slice(2), out var rightLen, provider: CultureInfo.InvariantCulture))
        {
            return leftBuf.Slice(0, 2 + leftLen).CompareTo(rightBuf.Slice(0, 2 + rightLen), StringComparison.OrdinalIgnoreCase);
        }

        return leftOrdinal.CompareTo(rightOrdinal);
    }

    private static void ParallelSortIntCore(int[] items, int[] scratch, int n, Comparison<int> comparison)
    {
        var parts = Math.Min(Environment.ProcessorCount, n / 4096);
        if (parts < 2)
        {
            new Span<int>(items, 0, n).Sort(comparison);
            return;
        }

        var bounds = new int[parts + 1];
        for (var i = 0; i <= parts; i++)
        {
            bounds[i] = (int)((long)n * i / parts);
        }

        Parallel.For(0, parts, i =>
        {
            new Span<int>(items, bounds[i], bounds[i + 1] - bounds[i]).Sort(comparison);
        });

        var src = items;
        var dst = scratch;
        var runs = bounds;
        while (runs.Length > 2)
        {
            var runCount = runs.Length - 1;
            var pairs = (runCount + 1) / 2;
            var nextRuns = new int[pairs + 1];
            var from = src;
            var to = dst;
            var currentRuns = runs;
            Parallel.For(0, pairs, p =>
            {
                var a = currentRuns[2 * p];
                var b = currentRuns[Math.Min(2 * p + 1, runCount)];
                var c = currentRuns[Math.Min(2 * p + 2, runCount)];
                MergeInts(from, a, b, c, to, comparison);
            });

            for (var p = 0; p < pairs; p++)
            {
                nextRuns[p] = currentRuns[2 * p];
            }

            nextRuns[pairs] = n;
            runs = nextRuns;
            (src, dst) = (dst, src);
        }

        if (!ReferenceEquals(src, items))
        {
            Array.Copy(src, items, n);
        }
    }

    private static void MergeInts(int[] source, int start, int mid, int end, int[] target, Comparison<int> comparison)
    {
        int i = start, j = mid, k = start;
        while (i < mid && j < end)
        {
            target[k++] = comparison(source[i], source[j]) <= 0 ? source[i++] : source[j++];
        }

        while (i < mid)
        {
            target[k++] = source[i++];
        }

        while (j < end)
        {
            target[k++] = source[j++];
        }
    }

    private static bool Accepts(AssetItem? x, AssetClassItem? classFilter, HashSet<ClassIDType>? selectedTypes, TextFilter? textFilter)
    {
        if (x == null)
        {
            return false;
        }

        if (classFilter != null)
        {
            if (!MatchesClassFilter(x, classFilter))
            {
                return false;
            }
        }
        else if (selectedTypes != null)
        {
            if (selectedTypes.Count == 0 || !selectedTypes.Contains(x.Type))
            {
                return false;
            }
        }

        return textFilter == null || textFilter.Matches(x);
    }

    /// <summary>Filters <paramref name="snapshot"/> preserving relative order (deterministic).</summary>
    internal static List<AssetItem> Filter(
        IReadOnlyList<AssetItem?> snapshot,
        AssetClassItem? classFilter,
        HashSet<ClassIDType>? selectedTypes,
        string? filterText,
        CancellationToken token)
    {
        var textFilter = TextFilter.Create(filterText);
        var count = snapshot.Count;
        var arraySnapshot = snapshot as AssetItem?[];

        if (count < ParallelFilterThreshold)
        {
            var matches = new List<AssetItem>();
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var x = arraySnapshot != null ? arraySnapshot[i] : snapshot[i];
                if (Accepts(x, classFilter, selectedTypes, textFilter))
                {
                    matches.Add(x!);
                }
            }

            return matches;
        }

        var chunkCount = (count + FilterChunkSize - 1) / FilterChunkSize;
        var chunkResults = new List<AssetItem>[chunkCount];
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        Parallel.For(0, chunkCount, options, chunk =>
        {
            token.ThrowIfCancellationRequested();
            var start = chunk * FilterChunkSize;
            var end = Math.Min(count, start + FilterChunkSize);
            var local = new List<AssetItem>();
            for (var i = start; i < end; i++)
            {
                var x = arraySnapshot != null ? arraySnapshot[i] : snapshot[i];
                if (Accepts(x, classFilter, selectedTypes, textFilter))
                {
                    local.Add(x!);
                }
            }

            chunkResults[chunk] = local;
        });

        var total = 0;
        foreach (var local in chunkResults)
        {
            total += local.Count;
        }

        var result = new List<AssetItem>(total);
        foreach (var local in chunkResults)
        {
            result.AddRange(local);
        }

        return result;
    }

    internal static List<AssetItem> Sort(IEnumerable<AssetItem>? assets, string? sortMember, bool descending)
    {
        var sorted = assets?
            .Where(static x => x != null)
            .ToList() ?? new List<AssetItem>();
        return SortInPlace(sorted, sortMember, descending);
    }

    /// <summary>Sorts a list that is already known to contain no null entries.</summary>
    internal static List<AssetItem> SortInPlace(List<AssetItem> sorted, string? sortMember, bool descending)
    {
        if (sorted.Count <= 1 || string.IsNullOrEmpty(sortMember))
        {
            return sorted;
        }

        var key = ResolveSortKey(sortMember);
        Comparison<AssetItem> comparison = (left, right) => Compare(left, right, key, descending);

        if (sorted.Count < ParallelSortThreshold)
        {
            sorted.Sort(comparison);
            return sorted;
        }

        var count = sorted.Count;
        var work = ArrayPool<AssetItem>.Shared.Rent(count);
        var scratch = ArrayPool<AssetItem>.Shared.Rent(count);
        try
        {
            var listSpan = CollectionsMarshal.AsSpan(sorted);
            listSpan.CopyTo(work);
            ParallelSortCore(work, scratch, count, comparison);
            work.AsSpan(0, count).CopyTo(listSpan);
        }
        finally
        {
            ArrayPool<AssetItem>.Shared.Return(work, clearArray: true);
            ArrayPool<AssetItem>.Shared.Return(scratch, clearArray: true);
        }

        return sorted;
    }

    internal static void ParallelSort(AssetItem[] items, Comparison<AssetItem> comparison)
    {
        var n = items.Length;
        var scratch = ArrayPool<AssetItem>.Shared.Rent(n);
        try
        {
            ParallelSortCore(items, scratch, n, comparison);
        }
        finally
        {
            ArrayPool<AssetItem>.Shared.Return(scratch, clearArray: true);
        }
    }

    private static void ParallelSortCore(AssetItem[] items, AssetItem[] scratch, int n, Comparison<AssetItem> comparison)
    {
        var parts = Math.Min(Environment.ProcessorCount, n / 4096);
        if (parts < 2)
        {
            Array.Sort(items, 0, n, Comparer<AssetItem>.Create(comparison));
            return;
        }

        var bounds = new int[parts + 1];
        for (var i = 0; i <= parts; i++)
        {
            bounds[i] = (int)((long)n * i / parts);
        }

        Parallel.For(0, parts, i =>
        {
            new Span<AssetItem>(items, bounds[i], bounds[i + 1] - bounds[i]).Sort(comparison);
        });

        var src = items;
        var dst = scratch;
        var runs = bounds;
        while (runs.Length > 2)
        {
            var runCount = runs.Length - 1;
            var pairs = (runCount + 1) / 2;
            var nextRuns = new int[pairs + 1];
            var from = src;
            var to = dst;
            var currentRuns = runs;
            Parallel.For(0, pairs, p =>
            {
                var a = currentRuns[2 * p];
                var b = currentRuns[Math.Min(2 * p + 1, runCount)];
                var c = currentRuns[Math.Min(2 * p + 2, runCount)];
                Merge(from, a, b, c, to, comparison);
            });

            for (var p = 0; p < pairs; p++)
            {
                nextRuns[p] = currentRuns[2 * p];
            }

            nextRuns[pairs] = n;
            runs = nextRuns;
            (src, dst) = (dst, src);
        }

        if (!ReferenceEquals(src, items))
        {
            Array.Copy(src, items, n);
        }
    }

    private static void Merge(AssetItem[] source, int start, int mid, int end, AssetItem[] target, Comparison<AssetItem> comparison)
    {
        int i = start, j = mid, k = start;
        while (i < mid && j < end)
        {
            target[k++] = comparison(source[i], source[j]) <= 0 ? source[i++] : source[j++];
        }

        while (i < mid)
        {
            target[k++] = source[i++];
        }

        while (j < end)
        {
            target[k++] = source[j++];
        }
    }

    internal static SortKey ResolveSortKey(string? sortMember)
    {
        return sortMember switch
        {
            "PathID" => SortKey.PathID,
            "FullSize" or "Size" => SortKey.FullSize,
            "Container" => SortKey.Container,
            "DisplayType" or "Type" => SortKey.DisplayType,
            "Name" => SortKey.Name,
            _ => SortKey.None,
        };
    }

    internal static int Compare(AssetItem? left, AssetItem? right, SortKey key, bool descending)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return 1;
        }
        if (right == null)
        {
            return -1;
        }

        var result = key switch
        {
            SortKey.PathID => left.PathID.CompareTo(right.PathID),
            SortKey.FullSize => left.FullSize.CompareTo(right.FullSize),
            SortKey.Container => CompareNullableText(left.Container, right.Container),
            SortKey.DisplayType => CompareNullableText(left.DisplayType, right.DisplayType),
            SortKey.Name => CompareNullableText(left.Name, right.Name),
            _ => 0,
        };

        if (descending)
        {
            result = -result;
        }

        if (result != 0)
        {
            return result;
        }

        result = left.PathID.CompareTo(right.PathID);
        if (result != 0)
        {
            return result;
        }

        result = AssetItem.CompareUniqueId(left, right);
        if (result != 0)
        {
            return result;
        }

        return CompareNullableText(left.Name, right.Name);
    }

    internal static int CompareNullableText(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        var leftMissing = string.IsNullOrEmpty(left);
        var rightMissing = string.IsNullOrEmpty(right);
        if (leftMissing && rightMissing)
        {
            return 0;
        }
        if (leftMissing)
        {
            return 1;
        }
        if (rightMissing)
        {
            return -1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}