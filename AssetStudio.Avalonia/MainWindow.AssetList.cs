using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
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
    private CancellationTokenSource? listSearchDebounce;
    private bool isSorting;

    private bool isRefreshingFilterList
    {
        get => ViewModel.AssetList.IsRefreshing;
        set => ViewModel.AssetList.IsRefreshing = value;
    }

    private bool _isArenaAssetListMode
    {
        get => ViewModel.AssetList.IsArenaMode;
        set
        {
            if (value)
            {
                ViewModel.AssetList.BeginArenaMode(assetsManager.ProjectIndex);
            }
            else
            {
                ViewModel.AssetList.ResetArenaState();
            }
        }
    }

    private int ExportableAssetCount => ViewModel.AssetList.TotalAssetCount;
    private int VisibleAssetCount => ViewModel.AssetList.VisibleAssetCount;

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

    private async Task CopyAssetCellTextCommandHandlerAsync()
    {
        var text = assetContextCellText;
        if (string.IsNullOrEmpty(text) && AssetListDataGrid.SelectedItem is AssetItem item)
        {
            text = item.Name;
        }

        if (await ViewModel.DialogService.CopyTextToClipboardAsync(text))
        {
            StatusStripUpdate("Copied asset cell text.");
        }
    }

    private Task ShowOriginalFileCommandHandlerAsync()
    {
        var item = assetContextItem ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (item == null) return Task.CompletedTask;
        ShowOriginalFile(item);
        return Task.CompletedTask;
    }

    private void SyncFilterInputsToAssetListViewModel()
    {
        ViewModel.AssetList.SearchText = listSearch?.Text?.Trim() ?? string.Empty;
        if (filterTypeAll.IsChecked == true)
        {
            ViewModel.AssetList.SelectAllClassTypes();
        }
        else
        {
            var selectedTypes = GetFilterTypeItems()
                .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
                .Select(x => (ClassIDType)x.Tag!);
            ViewModel.AssetList.SetSelectedClassTypes(selectedTypes);
        }
    }

    private AssetSelectionKey CaptureDataGridSelectionToViewModel()
    {
        AssetSelectionKey oldSelectedKey = default;
        if (AssetListDataGrid.SelectedItems != null)
        {
            var primary = AssetListDataGrid.SelectedItem as AssetItem;
            AssetListViewModel.TryGetAssetSelectionKey(primary, out oldSelectedKey);
            ViewModel.AssetList.SetSelection(AssetListDataGrid.SelectedItems.OfType<AssetItem>(), primary);
        }
        else
        {
            ViewModel.AssetList.ClearSelection();
        }

        return oldSelectedKey;
    }

    private void RestoreDataGridSelectionFromViewModel()
    {
        var selectedItems = AssetListDataGrid.SelectedItems;
        if (selectedItems == null)
        {
            return;
        }

        var vmSelected = ViewModel.AssetList.SelectedItems;
        var vmKeys = ViewModel.AssetList.SelectedKeys;
        if (vmKeys.Count == 0)
        {
            return;
        }

        if (selectedItems.Count > 0 && !_isArenaAssetListMode)
        {
            var preservedCount = 0;
            foreach (var item in selectedItems.OfType<AssetItem>())
            {
                if (AssetListViewModel.TryGetAssetSelectionKey(item, out var key) && vmKeys.Contains(key))
                {
                    preservedCount++;
                }
            }

            if (preservedCount == vmKeys.Count || preservedCount == selectedItems.Count)
            {
                return;
            }
        }

        if (vmSelected.Count == 0 && selectedItems.Count == 0)
        {
            return;
        }

        selectedItems.Clear();
        for (var i = 0; i < vmSelected.Count; i++)
        {
            selectedItems.Add(vmSelected[i]);
        }
    }

    private async Task FilterAssetListAsync(CancellationToken token)
    {
        SyncFilterInputsToAssetListViewModel();
        var oldSelectedKey = CaptureDataGridSelectionToViewModel();

        // Find scroll viewer and save scroll offset
        var scrollViewer = FindVisualChild<ScrollViewer>(AssetListDataGrid);
        var scrollOffset = scrollViewer?.Offset ?? default;

        try
        {
            var applied = await ViewModel.AssetList.FilterAsync(token);
            if (!applied)
            {
                return;
            }

            isRefreshingFilterList = true;
            try
            {
                EnsureAssetListItemsSource();
                RestoreDataGridSelectionFromViewModel();
                StatusStripUpdate(ViewModel.AssetList.StatusSummary);
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
            AssetListViewModel.TryGetAssetSelectionKey(newSelectedItem, out var newSelectedKey);
            if (newSelectedKey != oldSelectedKey)
            {
                _currentlySelectedUniqueID = AssetListViewModel.GetAssetUniqueKey(newSelectedItem);
                if (newSelectedItem != null)
                {
                    if (RightTabControl.SelectedIndex == 1)
                    {
                        _ = UpdateDumpForSelectedAsset();
                    }
                    else if (RightTabControl.SelectedIndex == 2)
                    {
                        _ = UpdateReferencesForSelectedAssetAsync();
                    }
                    QueuePreviewAsset(newSelectedItem);
                }
                else
                {
                    _dumpRequestCoordinator.CancelCurrent();
                    _referencesRequestCoordinator.CancelCurrent();
                    PreviewSession.DumpTextContent = string.Empty;
                    PreviewSession.ClearReferences();
                    CancelPendingPreview();
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
        CaptureDataGridSelectionToViewModel();

        try
        {
            var applied = await ViewModel.AssetList.ApplySortAsync();
            if (!applied)
            {
                return;
            }

            isRefreshingFilterList = true;
            try
            {
                EnsureAssetListItemsSource();
                RestoreDataGridSelectionFromViewModel();
                StatusStripUpdate(ViewModel.AssetList.StatusSummary);
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

    private bool TryMarkExportableArenaIndex(int arenaIndex) => ViewModel.AssetList.TryMarkExportableArenaIndex(arenaIndex);

    private void ResetArenaAssetListState() => ViewModel.AssetList.ResetArenaState();

    private void CancelPendingArenaCoalescedSort() => ViewModel.AssetList.CancelPendingArenaCoalescedSort();

    private int[] SnapshotExportableArenaIndices() => ViewModel.AssetList.SnapshotExportableArenaIndices();

    private int[] SnapshotVisibleArenaIndices() => ViewModel.AssetList.SnapshotVisibleArenaIndices();

    private AssetItem MaterializeArenaAssetItem(int arenaIndex) => ViewModel.AssetList.MaterializeArenaAssetItem(arenaIndex);

    private List<AssetItem> MaterializeArenaAssetItems(IReadOnlyList<int> indices) => ViewModel.AssetList.MaterializeArenaAssetItems(indices);

    private static bool IsIdentityArenaIndices(int[] indices, int count) => AssetListViewModel.IsIdentityArenaIndices(indices, count);

    private static bool IsStrictlyIncreasingIndices(int[] indices, int count) => AssetListViewModel.IsStrictlyIncreasingIndices(indices, count);

    private void ApplyVisibleArenaIndices(int[] indices, int count, bool isStrictlyIncreasing, bool isIdentity)
    {
        EnsureAssetListItemsSource();
        ViewModel.AssetList.ApplyVisibleArenaIndices(indices, count, isStrictlyIncreasing, isIdentity);
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
        EnsureAssetListItemsSource();
        ViewModel.AssetList.ReplaceVisibleEagerAssets(items);
    }

    private void ReplaceVisibleAssetsOwned(List<AssetItem> ownedItems)
    {
        EnsureAssetListItemsSource();
        ViewModel.AssetList.ReplaceVisibleEagerAssetsOwned(ownedItems);
    }

    private void SyncExportableAssetHandleIds()
    {
        ViewModel.AssetList.SyncExportableAssetHandleIds();
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
        SyncFilterInputsToAssetListViewModel();
        ViewModel.AssetList.AppendFilteredEagerAssetsToVisible(newItems);
        StatusStripUpdate(ViewModel.AssetList.StatusSummary);
    }

    private void AppendFilteredArenaIndicesToVisible(List<int> newArenaIndices)
    {
        if (newArenaIndices == null || newArenaIndices.Count == 0)
        {
            return;
        }

        EnsureAssetListItemsSource();
        SyncFilterInputsToAssetListViewModel();
        ViewModel.AssetList.AppendFilteredArenaIndicesToVisible(newArenaIndices);
        StatusStripUpdate(ViewModel.AssetList.StatusSummary);
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
        private readonly TextFilter[]? _subTerms;
        private readonly ConcurrentDictionary<ClassIDType, bool> _normalTypeMatchCache = new();
        private readonly ConcurrentDictionary<ClassIDType, bool> _fbxTypeMatchCache = new();

        private TextFilter(string text, TextFilter[]? subTerms = null)
        {
            _needle = SearchValues.Create(new[] { text }, StringComparison.OrdinalIgnoreCase);
            _subTerms = subTerms;
        }

        public static TextFilter? Create(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            TextFilter[]? subTerms = null;
            if (text.AsSpan().IndexOfAny(' ', '\t') >= 0)
            {
                var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    subTerms = new TextFilter[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                    {
                        subTerms[i] = new TextFilter(parts[i]);
                    }
                }
            }

            return new TextFilter(text, subTerms);
        }

        public bool Matches(AssetItem item)
        {
            if (MatchesSingleNeedle(item))
            {
                return true;
            }

            if (_subTerms == null)
            {
                return false;
            }

            for (var i = 0; i < _subTerms.Length; i++)
            {
                if (!_subTerms[i].MatchesSingleNeedle(item))
                {
                    return false;
                }
            }

            return true;
        }

        private bool MatchesSingleNeedle(AssetItem item)
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
            if (MatchesSingleNeedle(arena, arenaIndex, type))
            {
                return true;
            }

            if (_subTerms == null)
            {
                return false;
            }

            for (var i = 0; i < _subTerms.Length; i++)
            {
                if (!_subTerms[i].MatchesSingleNeedle(arena, arenaIndex, type))
                {
                    return false;
                }
            }

            return true;
        }

        private bool MatchesSingleNeedle(AssetIndexArena arena, int arenaIndex, ClassIDType type)
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