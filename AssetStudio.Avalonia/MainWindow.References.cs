using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private AsyncPreviewRequestCoordinator _referencesRequestCoordinator => PreviewSession.ReferencesCoordinator;
    private bool _isNavigatingHistory;

    private void RecordCurrentAssetInNavigationHistory(AssetItem? item)
    {
        if (_isNavigatingHistory || item == null)
        {
            return;
        }

        PreviewSession.RecordNavigation(AssetNavigationEntry.FromAssetItem(item));
    }

    private async Task NavigateBackCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (!PreviewSession.TryNavigateBack(out var entry) || entry == null)
        {
            return;
        }

        _isNavigatingHistory = true;
        try
        {
            await NavigateToReferencedAssetCoreAsync(entry.ToReferenceItem(), recordHistory: false);
        }
        finally
        {
            _isNavigatingHistory = false;
        }
    }

    private async Task NavigateForwardCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (!PreviewSession.TryNavigateForward(out var entry) || entry == null)
        {
            return;
        }

        _isNavigatingHistory = true;
        try
        {
            await NavigateToReferencedAssetCoreAsync(entry.ToReferenceItem(), recordHistory: false);
        }
        finally
        {
            _isNavigatingHistory = false;
        }
    }

    private async Task UpdateReferencesForSelectedAssetAsync()
    {
        if (AssetListDataGrid.SelectedItem is not AssetItem assetItem)
        {
            PreviewSession.ClearReferences();
            return;
        }

        var token = PreviewSession.BeginReferencesRequest(out var requestId, assetItem);
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);

        var filesSnapshot = GetLoadedFilesSnapshot();
        var isLazy = assetsManager.LazyLoading;
        var scanResultSnapshot = currentScanResult;
        var folderPath = GetCurrentCacheFolderPath();

        try
        {
            var snapshot = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                // Avoid blocking or loading entire projects: use already-materialized object if available,
                // or materialize ONLY the selected asset handle on demand if needed.
                AssetStudio.Object? resolvedObj = assetItem.Asset;
                if (resolvedObj == null && isLazy && assetItem.Handle != null)
                {
                    if (assetItem.Handle.RealObject != null)
                    {
                        resolvedObj = assetItem.Handle.RealObject;
                    }
                    else
                    {
                        EnsureLazyAssetReadyForPreview(assetItem);
                        resolvedObj = assetItem.Asset;
                    }
                }

                token.ThrowIfCancellationRequested();

                Action<string, List<AssetReferenceItem>, List<AssetReferenceItem>, int>? semanticLoader = null;
                if (scanResultSnapshot != null && !string.IsNullOrWhiteSpace(folderPath))
                {
                    var signature = _sqliteCache.GetFolderSignature(scanResultSnapshot);
                    semanticLoader = (uid, usedByOut, usesOut, limit) =>
                        _sqliteCache.LoadAssetReferencesFromSemanticCache(folderPath, signature, uid, usedByOut, usesOut, limit);
                }

                var context = new AssetReferencesQueryContext
                {
                    LoadedFiles = filesSnapshot,
                    ProjectIndex = assetsManager.ProjectIndex,
                    SemanticCacheLoader = semanticLoader,
                    MaxItemsPerDirection = 500
                };

                return AssetReferencesService.BuildReferences(assetItem, resolvedObj, context, token);
            }, token);

            if (token.IsCancellationRequested
                || !PreviewSession.IsReferencesRequestCurrent(requestId, token)
                || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }

            PreviewSession.CompleteReferencesRequest(requestId, snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Warning, $"Failed to build references for {assetItem.Name}: {ex.Message}");
            if (PreviewSession.IsReferencesRequestCurrent(requestId, token)
                && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                PreviewSession.FailReferencesRequest(requestId, ex.Message);
            }
        }
    }

    private void ReferencesListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is AssetReferenceItem referenceItem && referenceItem.IsNavigable)
        {
            _ = NavigateToReferencedAssetCommandHandlerAsync(referenceItem);
        }
    }

    private Task NavigateToReferencedAssetCommandHandlerAsync(AssetReferenceItem? target)
    {
        return NavigateToReferencedAssetCoreAsync(target, recordHistory: true);
    }

    private async Task NavigateToReferencedAssetCoreAsync(AssetReferenceItem? target, bool recordHistory)
    {
        if (target == null || !target.IsNavigable || IsWindowClosingOrClosed)
        {
            return;
        }

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);

        // Ensure the asset we are jumping FROM is recorded before jumping to the target.
        if (recordHistory && AssetListDataGrid.SelectedItem is AssetItem currentItem)
        {
            RecordCurrentAssetInNavigationHistory(currentItem);
        }

        PreviewSession.ClearGameObjectInspector();

        // Switch left panel to Asset List tab so the target row is visible.
        if (LeftTabControl != null && LeftTabControl.SelectedIndex != 1)
        {
            LeftTabControl.SelectedIndex = 1;
        }

        AssetItem? locatedItem = await LocateAssetItemInAssetListAsync(target);
        if (locatedItem == null)
        {
            // If the referenced asset is a non-exportable component/object (e.g. GameObject, MeshRenderer)
            // and DisplayAll is currently off, enable DisplayAll cleanly so navigation always succeeds.
            if (!ViewModel.Options.DisplayAll && !IsLazyExportableType(target.AssetType))
            {
                ViewModel.Options.DisplayAll = true;
                await BuildAssetStructuresAsync();
                locatedItem = await LocateAssetItemInAssetListAsync(target);
            }
        }

        if (locatedItem == null)
        {
            StatusStripUpdate($"Referenced asset not found in Asset List: {target.Name} (PathID: {target.PathIdText}).");
            return;
        }

        // Select, scroll into view, and switch RightTabControl to Preview tab so it previews immediately.
        var previousHistoryFlag = _isNavigatingHistory;
        if (!recordHistory)
        {
            _isNavigatingHistory = true;
        }
        try
        {
            AssetListDataGrid.SelectedItem = locatedItem;
            AssetListDataGrid.ScrollIntoView(locatedItem, null);
            if (recordHistory)
            {
                RecordCurrentAssetInNavigationHistory(locatedItem);
            }
        }
        finally
        {
            _isNavigatingHistory = previousHistoryFlag;
        }

        if (RightTabControl != null && RightTabControl.SelectedIndex != 0)
        {
            RightTabControl.SelectedIndex = 0;
        }
        else
        {
            QueuePreviewAsset(locatedItem, immediate: true);
        }

        StatusStripUpdate($"Navigated to {locatedItem.DisplayType}: {locatedItem.Name}");
    }

    private async Task<AssetItem?> LocateAssetItemInAssetListAsync(AssetReferenceItem target)
    {
        if (_isArenaAssetListMode)
        {
            int arenaIndex = -1;
            if (!string.IsNullOrWhiteSpace(target.AssetUniqueId))
            {
                assetsManager.ProjectIndex.TryGetIndexByUniqueId(target.AssetUniqueId, out arenaIndex);
            }

            if (arenaIndex < 0 && target.PathId != 0)
            {
                arenaIndex = FindArenaIndexByPathIdAndType(target.PathId, target.AssetType, target.SerializedFileName);
            }

            if (arenaIndex < 0)
            {
                return null;
            }

            var row = ViewModel.AssetList.LocateVisibleArenaRowByArenaIndex(arenaIndex);
            if (row < 0)
            {
                await ClearAssetListFiltersForNavigationAsync();
                row = ViewModel.AssetList.LocateVisibleArenaRowByArenaIndex(arenaIndex);
            }

            if (row >= 0 && row < visibleAssetItems.Count)
            {
                return visibleAssetItems[row];
            }

            return null;
        }

        // Eager mode: check visible list first, then full exportable list (clearing filters if hidden).
        var match = FindMatchingAssetItem(_eagerVisibleAssets, target);
        if (match != null)
        {
            return match;
        }

        var hiddenMatch = FindMatchingAssetItem(_eagerExportableAssets, target);
        if (hiddenMatch == null)
        {
            return null;
        }

        await ClearAssetListFiltersForNavigationAsync();
        return FindMatchingAssetItem(_eagerVisibleAssets, target) ?? hiddenMatch;
    }

    private async Task ClearAssetListFiltersForNavigationAsync()
    {
        listSearchDebounce?.Cancel();
        ViewModel.AssetList.ClearClassFilterOverride();
        ViewModel.AssetList.SelectAllClassTypes();
        ViewModel.AssetList.SearchText = string.Empty;

        updatingFilterTypeMenu = true;
        try
        {
            filterTypeAll.IsChecked = true;
            foreach (var menuItem in GetFilterTypeItems())
            {
                menuItem.IsChecked = false;
            }
        }
        finally
        {
            updatingFilterTypeMenu = false;
        }

        if (listSearch != null && !string.IsNullOrEmpty(listSearch.Text))
        {
            listSearch.Text = string.Empty;
        }

        await FilterAssetListAsync(CancellationToken.None);
    }

    private int FindArenaIndexByPathIdAndType(long pathId, ClassIDType assetType, string serializedFileName)
    {
        var arena = assetsManager.ProjectIndex.Arena;
        var count = arena.Count;
        var fallback = -1;
        for (var i = 0; i < count; i++)
        {
            if (arena.GetPathId(i) != pathId)
            {
                continue;
            }

            if (assetType != ClassIDType.UnknownType && arena.GetType(i) != assetType)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(serializedFileName)
                && string.Equals(arena.GetSerializedFileName(i), serializedFileName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (fallback < 0)
            {
                fallback = i;
            }
        }

        return fallback;
    }

    private static AssetItem? FindMatchingAssetItem(IReadOnlyList<AssetItem> items, AssetReferenceItem target)
    {
        AssetItem? fallbackByPathId = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null)
            {
                continue;
            }

            var uid = item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
            if (!string.IsNullOrWhiteSpace(target.AssetUniqueId)
                && string.Equals(uid, target.AssetUniqueId, StringComparison.Ordinal))
            {
                return item;
            }

            if (target.PathId != 0 && item.PathID == target.PathId)
            {
                if (target.AssetType == ClassIDType.UnknownType || item.Type == target.AssetType)
                {
                    if (!string.IsNullOrWhiteSpace(target.SerializedFileName)
                        && string.Equals(item.SourceFile?.fileName, target.SerializedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }

                    fallbackByPathId ??= item;
                }
            }
        }

        return fallbackByPathId;
    }
}
