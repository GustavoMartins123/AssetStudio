using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private CancellationTokenSource? listSearchDebounce;
    private bool isRefreshingFilterList;
    private bool isSorting;

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
        var filterText = listSearch?.Text?.Trim();
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var assetsSnapshot = exportableAssets.ToArray();

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
                StatusStripUpdate($"Showing {visibleAssets.Count} assets");

                // Restore selection if DataGrid Reset did not already retain all matching items (e.g. re-materialized instances).
                RestoreSelectionIfNeeded(selectedKeys);
            }
            finally
            {
                isRefreshingFilterList = false;
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
        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var currentAssets = new List<AssetItem>(visibleAssets);
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
            var sorted = await Task.Run(() => AssetListQuery.SortInPlace(currentAssets, sortMember, sortDescending));
            isRefreshingFilterList = true;
            try
            {
                ReplaceVisibleAssetsOwned(sorted);
                RestoreSelectionIfNeeded(selectedKeys);
                StatusStripUpdate($"Showing {visibleAssets.Count} assets");
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
        foreach (var item in visibleAssets)
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
        visibleAssets = items is { Count: > 0 }
            ? items.Where(static x => x != null).ToList()
            : new List<AssetItem>();
        RefreshAssetListItems();
    }

    private void ReplaceVisibleAssetsOwned(List<AssetItem> ownedItems)
    {
        visibleAssets = ownedItems;
        RefreshAssetListItems();
    }

    private void SyncExportableAssetHandleIds()
    {
        lazyAssetItemsByHandleId.Clear();
        exportableAssetHandleIds.Clear();
        exportableAssetTypes.Clear();
        foreach (var item in exportableAssets)
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
        visibleAssetItems.ReplaceBacking(visibleAssets);
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

        var updated = false;
        try
        {
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

                if (!updated)
                {
                    visibleAssetItems.BeginUpdate();
                    if (!visibleAssetItems.IsBackedBy(visibleAssets))
                    {
                        visibleAssetItems.ReplaceBacking(visibleAssets);
                    }
                    updated = true;
                }

                AddVisibleAssetItem(x);
            }
        }
        finally
        {
            if (updated)
            {
                visibleAssetItems.EndUpdate();
            }
        }

        if (updated)
        {
            StatusStripUpdate($"Showing {visibleAssets.Count} assets");
        }
    }

    private void AddVisibleAssetItem(AssetItem item)
    {
        var sharedBacking = visibleAssetItems.IsBackedBy(visibleAssets);
        if (string.IsNullOrEmpty(assetListSortMember))
        {
            visibleAssets.Add(item);
            if (!sharedBacking)
            {
                visibleAssetItems.Add(item);
            }
            return;
        }

        var insertIndex = FindSortedInsertIndex(visibleAssets, item, assetListSortMember, assetListSortDescending);
        visibleAssets.Insert(insertIndex, item);
        if (!sharedBacking)
        {
            visibleAssetItems.Insert(insertIndex, item);
        }
    }

    private static int FindSortedInsertIndex(List<AssetItem> items, AssetItem item, string sortMember, bool descending)
    {
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (CompareAssetItems(items[mid], item, sortMember, descending) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
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

    internal enum SortKey
    {
        None,
        PathID,
        FullSize,
        Container,
        DisplayType,
        Name,
    }

    internal sealed class TextFilter
    {
        private readonly SearchValues<string> _needle;

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