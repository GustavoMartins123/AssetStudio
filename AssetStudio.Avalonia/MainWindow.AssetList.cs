using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        goToSceneHierarchyMenuItem.IsVisible = singleSelected && selectedAssets[0].TreeNode != null;
        showOriginalFileMenuItem.IsVisible = singleSelected;
        exportAnimatorWithSelectedAnimationClipMenuItem.IsVisible = hasAnimatorWithClips;
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
        var assetsSnapshot = exportableAssets.ToList();

        // Capture selection before filtering
        var selectedUniqueIds = new HashSet<string>();
        string? oldSelectedUniqueId = null;
        if (AssetListDataGrid.SelectedItems != null)
        {
            foreach (var item in AssetListDataGrid.SelectedItems.OfType<AssetItem>())
            {
                var id = item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
                if (!string.IsNullOrEmpty(id))
                {
                    selectedUniqueIds.Add(id);
                }
            }

            var selectedItem = AssetListDataGrid.SelectedItem as AssetItem;
            if (selectedItem != null)
            {
                oldSelectedUniqueId = selectedItem.Handle != null ? selectedItem.Handle.UniqueID : selectedItem.UniqueID;
            }
        }

        // Find scroll viewer and save scroll offset
        var scrollViewer = FindVisualChild<ScrollViewer>(AssetListDataGrid);
        var scrollOffset = scrollViewer?.Offset ?? default;

        try
        {
            var result = await Task.Run(() =>
            {
                var matches = new List<AssetItem>();
                foreach (var x in assetsSnapshot)
                {
                    token.ThrowIfCancellationRequested();
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

                    if (!string.IsNullOrEmpty(filterText) && !AssetMatchesTextFilter(x, filterText))
                    {
                        continue;
                    }

                    matches.Add(x);
                }

                token.ThrowIfCancellationRequested();
                return SortAssetList(matches, sortMember, sortDescending);
            }, token);

            isRefreshingFilterList = true;
            try
            {
                ReplaceVisibleAssets(result);
                StatusStripUpdate($"Showing {visibleAssets.Count} assets");

                // Restore selection
                if (selectedUniqueIds.Count > 0)
                {
                    var newSelectedItems = new List<AssetItem>();
                    foreach (var item in visibleAssets)
                    {
                        var id = item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
                        if (!string.IsNullOrEmpty(id) && selectedUniqueIds.Contains(id))
                        {
                            newSelectedItems.Add(item);
                        }
                    }

                    var selectedItems = AssetListDataGrid.SelectedItems;
                    if (selectedItems != null)
                    {
                        selectedItems.Clear();
                        foreach (var item in newSelectedItems)
                        {
                            selectedItems.Add(item);
                        }
                    }
                }
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
            var newSelectedUniqueId = newSelectedItem != null ? (newSelectedItem.Handle != null ? newSelectedItem.Handle.UniqueID : newSelectedItem.UniqueID) : null;
            if (newSelectedUniqueId != oldSelectedUniqueId)
            {
                _currentlySelectedUniqueID = newSelectedUniqueId;
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
        var currentAssets = visibleAssets.ToList();

        try
        {
            var sorted = await Task.Run(() => SortAssetList(currentAssets, sortMember, sortDescending));
            ReplaceVisibleAssets(sorted);
            StatusStripUpdate($"Showing {visibleAssets.Count} assets");
        }
        catch (Exception ex)
        {
            Logger.Error("Error sorting asset list", ex);
            StatusStripUpdate("Error sorting asset list. See error log.");
        }
    }

    private static bool AssetMatchesClassFilter(AssetItem item, AssetClassItem classFilter)
    {
        return (int)item.Type == classFilter.ClassID
            && string.Equals(
                item.SourceFile?.unityVersion ?? string.Empty,
                classFilter.UnityVersion ?? string.Empty,
                StringComparison.Ordinal);
    }

    private static bool AssetMatchesTextFilter(AssetItem item, string filterText)
    {
        return ContainsIgnoreCase(item.Name, filterText)
            || ContainsIgnoreCase(item.Container, filterText)
            || ContainsIgnoreCase(item.TypeString, filterText)
            || ContainsIgnoreCase(item.DisplayType, filterText)
            || ContainsIgnoreCase(item.PathIDString, filterText);
    }

    private static bool ContainsIgnoreCase(string? value, string filterText)
    {
        return !string.IsNullOrEmpty(value)
            && value.Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    private static List<AssetItem> SortAssetList(IEnumerable<AssetItem>? assets, string? sortMember, bool descending)
    {
        var sorted = assets?
            .Where(static x => x != null)
            .ToList() ?? new List<AssetItem>();

        if (sorted.Count <= 1 || string.IsNullOrEmpty(sortMember))
        {
            return sorted;
        }

        sorted.Sort((left, right) => CompareAssetItems(left, right, sortMember, descending));
        return sorted;
    }

    private static int CompareAssetItems(AssetItem? left, AssetItem? right, string sortMember, bool descending)
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

        int result = sortMember switch
        {
            "PathID" => left.PathID.CompareTo(right.PathID),
            "FullSize" or "Size" => left.FullSize.CompareTo(right.FullSize),
            "Container" => CompareNullableText(left.Container, right.Container),
            "DisplayType" or "Type" => CompareNullableText(left.DisplayType, right.DisplayType),
            "Name" => CompareNullableText(left.Name, right.Name),
            _ => 0
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

        result = CompareNullableText(left.UniqueID, right.UniqueID);
        if (result != 0)
        {
            return result;
        }

        return CompareNullableText(left.Name, right.Name);
    }

    private static int CompareNullableText(string? left, string? right)
    {
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

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
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

    private void SyncExportableAssetHandleIds()
    {
        lazyAssetItemsByHandleId.Clear();
        exportableAssetHandleIds.Clear();
        exportableAssetTypes.Clear();
        foreach (var item in exportableAssets)
        {
            if (!string.IsNullOrEmpty(item.Handle?.UniqueID))
            {
                lazyAssetItemsByHandleId[item.Handle.UniqueID] = item;
                exportableAssetHandleIds.Add(item.Handle.UniqueID);
            }
            exportableAssetTypes.Add(item.Type);
        }
    }

    private void ApplyExportableAssetIndexes(BuildAssetStructuresResult result)
    {
        lazyAssetItemsByHandleId = result.LazyAssetItemsByHandleId ?? new Dictionary<string, AssetItem>(StringComparer.Ordinal);
        exportableAssetHandleIds = result.ExportableAssetHandleIds ?? new HashSet<string>(StringComparer.Ordinal);
        exportableAssetTypes = result.ExportableAssetTypes ?? new HashSet<ClassIDType>();
    }

    private void EnsureAssetListItemsSource()
    {
        if (AssetListDataGrid.ItemsSource != visibleAssetItems)
        {
            AssetListDataGrid.ItemsSource = visibleAssetItems;
        }
    }

    private void RefreshAssetListItems()
    {
        visibleAssetItems = new BulkObservableCollection<AssetItem>(visibleAssets);
        EnsureAssetListItemsSource();
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
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        visibleAssetItems.BeginUpdate();
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

                if (!string.IsNullOrEmpty(filterText) && !AssetMatchesTextFilter(x, filterText))
                {
                    continue;
                }

                AddVisibleAssetItem(x);
            }
        }
        finally
        {
            visibleAssetItems.EndUpdate();
        }

        StatusStripUpdate($"Showing {visibleAssets.Count} assets");
    }

    private void AddVisibleAssetItem(AssetItem item)
    {
        if (string.IsNullOrEmpty(assetListSortMember))
        {
            visibleAssets.Add(item);
            visibleAssetItems.Add(item);
            return;
        }

        var insertIndex = FindSortedInsertIndex(visibleAssets, item, assetListSortMember, assetListSortDescending);
        visibleAssets.Insert(insertIndex, item);
        visibleAssetItems.Insert(insertIndex, item);
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
