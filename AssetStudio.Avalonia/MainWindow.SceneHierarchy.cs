using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Object = AssetStudio.Object;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private readonly SemaphoreSlim _lazySceneLoadGate = new(1, 1);
    private CancellationTokenSource? _lazySceneHierarchyCts;
    private long _containersIndexSessionId = -1;
    private int _containersIndexAssetCount = -1;
    private bool _containersIndexDisplayAll;
    private bool _containersIndexIsLazy;
    private bool _isBuildingContainersIndex;
    private long _containerPreviewRequestId;
    private bool _lazySceneHierarchyPendingRefresh;
    private long _lazySceneHierarchyLoadedSessionId = -1;
    private int _lazySceneHierarchyFullScanIndexCount = -1;

    private void LeftTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsWindowClosingOrClosed || LeftTabControl == null)
        {
            return;
        }

        var selectedIndex = LeftTabControl.SelectedIndex;
        if (SceneHierarchyTabContent != null)
        {
            SceneHierarchyTabContent.IsVisible = selectedIndex == 0;
        }
        if (AssetListTabContent != null)
        {
            AssetListTabContent.IsVisible = selectedIndex == 1;
        }
        if (AssetClassesTabContent != null)
        {
            AssetClassesTabContent.IsVisible = selectedIndex == 2;
        }

        if (selectedIndex == 0
            && assetsManager.LazyLoading
            && (_lazySceneHierarchyPendingRefresh || _lazySceneHierarchyLoadedSessionId != lazyIndexingSessionId))
        {
            RefreshLazySceneHierarchyNowAsync(lazyIndexingSessionId, forceFullScan: false);
        }
    }

    internal SceneHierarchyViewModel SceneHierarchy => ViewModel.SceneHierarchy;
    private SceneTreeSearchSession treeSearchSession => SceneHierarchy.SearchSession;

    private GameObjectNode[]? appliedTreeSearchMatches
    {
        get => SceneHierarchy.AppliedMatchesArray;
        set
        {
            if (value == null)
            {
                SceneHierarchy.InvalidateSearchCache();
            }
        }
    }

    private void WireSceneHierarchyViewModelHandlers()
    {
        SceneHierarchy.ModeActivatedAsync = OnSceneHierarchyModeActivatedAsync;
        SceneHierarchy.ExpandLazySceneHandlerAsync = ExpandLazySceneNodeAsync;
        SceneHierarchy.RefreshHandlerAsync = RefreshActiveSceneHierarchyModeAsync;
        SceneHierarchy.ExportSelectedSceneHandlerAsync = ExportSelectedSceneFromHierarchyAsync;
        SceneHierarchy.ShowContainerAssetInListHandlerAsync = ShowContainerLeafInAssetListAsync;
        SceneHierarchy.CopyContainerPathHandlerAsync = CopyContainerNodePathAsync;
        SceneHierarchy.ExportContainerAssetHandlerAsync = ExportContainerLeafAssetAsync;
    }

    private void CancelLazySceneHierarchyOperations()
    {
        var previous = Interlocked.Exchange(ref _lazySceneHierarchyCts, null);
        if (previous != null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previous.Dispose();
        }

        _containersIndexSessionId = -1;
        _containersIndexAssetCount = -1;
        _lazySceneHierarchyPendingRefresh = false;
        _lazySceneHierarchyLoadedSessionId = -1;
        _lazySceneHierarchyFullScanIndexCount = -1;
        Interlocked.Increment(ref _containerPreviewRequestId);
    }

    private CancellationToken GetOrCreateLazySceneHierarchyToken()
    {
        var existing = Volatile.Read(ref _lazySceneHierarchyCts);
        if (existing != null && !existing.IsCancellationRequested)
        {
            return existing.Token;
        }

        var created = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _lazySceneHierarchyCts, created);
        if (old != null)
        {
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            old.Dispose();
        }

        return created.Token;
    }

    internal void ResetSceneTreeSearchState()
    {
        treeSearchSession.Invalidate();
        SceneHierarchy.InvalidateSearchCache();
        treeSearchResults.Clear();
        nextGameObjectSearchIndex = 0;
    }

    private void ApplyEagerSceneHierarchy(List<GameObjectNode> eagerRoots)
    {
        _containersIndexSessionId = -1;
        _containersIndexAssetCount = -1;
        SceneHierarchy.SetEagerSceneRoots(eagerRoots);
        SceneHierarchy.CanRefresh = eagerRoots.Count > 0 || _eagerExportableAssets.Count > 0;
        SceneTreeView.ItemsSource = SceneHierarchy.Nodes;

        if (SceneHierarchy.IsContainersMode)
        {
            _ = EnsureContainersIndexBuiltAsync(forceRebuild: true);
        }
    }

    private void RefreshLazySceneHierarchyAsync(long expectedSessionId)
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager || !assetsManager.LazyLoading)
        {
            return;
        }

        _containersIndexSessionId = -1;
        _containersIndexAssetCount = -1;

        // Defer scene discovery while the user is browsing Asset List / Asset Classes so project loading
        // pays zero scene-discovery cost until Scene Hierarchy is actually viewed.
        if (LeftTabControl != null && LeftTabControl.SelectedIndex != 0)
        {
            _lazySceneHierarchyPendingRefresh = true;
            return;
        }

        RefreshLazySceneHierarchyNowAsync(expectedSessionId, forceFullScan: true);
    }

    private void RefreshLazySceneHierarchyNowAsync(long expectedSessionId, bool forceFullScan)
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager || !assetsManager.LazyLoading)
        {
            return;
        }

        _lazySceneHierarchyPendingRefresh = false;
        var currentIndexCount = assetsManager.ProjectIndex.Count;
        var cacheFolder = GetCurrentCacheFolderPath();
        var scanResultSnapshot = currentScanResult;
        var projectRoot = !string.IsNullOrWhiteSpace(cacheFolder) ? cacheFolder : assetsManager.ProjectRoot;
        var token = GetOrCreateLazySceneHierarchyToken();

        void ApplyDescriptorsOnUiThread(List<LazySceneDescriptor> descriptors, bool markSessionLoaded)
        {
            var realGeometryCount = 0;
            for (var i = 0; i < descriptors.Count; i++)
            {
                if (descriptors[i].RendererCount > 0)
                {
                    realGeometryCount++;
                }
            }

            var lazyRoots = SceneHierarchyBuilderService.BuildLazySceneRootNodes(
                descriptors,
                onExpanded: OnLazySceneRootExpanded);

            Dispatcher.UIThread.Post(() =>
            {
                if (IsWindowClosingOrClosed
                    || _isReturningToProjectManager
                    || !assetsManager.LazyLoading
                    || expectedSessionId != lazyIndexingSessionId
                    || token.IsCancellationRequested)
                {
                    return;
                }

                // Preserve any already-expanded lazy scene nodes for the same session so refreshes
                // don't collapse user work.
                if (SceneHierarchy.Nodes.Count > 0 && SceneHierarchy.IsLazyMode)
                {
                    var existingLoadedById = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < SceneHierarchy.Nodes.Count; i++)
                    {
                        var existing = SceneHierarchy.Nodes[i];
                        if (existing.IsLazySceneRoot && existing.IsSceneLoaded && existing.LazyScene != null)
                        {
                            var key = !string.IsNullOrWhiteSpace(existing.LazyScene.ScenePath)
                                ? existing.LazyScene.ScenePath
                                : existing.LazyScene.SerializedFileName;
                            if (!string.IsNullOrWhiteSpace(key))
                            {
                                existingLoadedById.TryAdd(key, existing);
                            }
                        }
                    }

                    if (existingLoadedById.Count > 0)
                    {
                        for (var i = 0; i < lazyRoots.Count; i++)
                        {
                            var desc = lazyRoots[i].LazyScene;
                            if (desc == null) continue;
                            var key = !string.IsNullOrWhiteSpace(desc.ScenePath) ? desc.ScenePath : desc.SerializedFileName;
                            if (!string.IsNullOrWhiteSpace(key) && existingLoadedById.TryGetValue(key, out var loadedNode))
                            {
                                lazyRoots[i] = loadedNode;
                            }
                        }
                    }
                }

                SceneHierarchy.SetLazySceneRoots(lazyRoots, realGeometryCount);
                SceneHierarchy.CanRefresh = assetsManager.ProjectIndex.Count > 0 || lazyRoots.Count > 0;
                if (!ReferenceEquals(SceneTreeView.ItemsSource, SceneHierarchy.Nodes))
                {
                    SceneTreeView.ItemsSource = SceneHierarchy.Nodes;
                }

                if (markSessionLoaded)
                {
                    _lazySceneHierarchyLoadedSessionId = expectedSessionId;
                }

                if (SceneHierarchy.IsContainersMode)
                {
                    _ = EnsureContainersIndexBuiltAsync(forceRebuild: false);
                }
            });
        }

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();

                // Fast SQLite first pass (<5 ms): show persisted scenes immediately without waiting for full handle scan.
                List<IndexedSceneRecord>? sqliteScenes = null;
                if (scanResultSnapshot != null && !string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder))
                {
                    var signature = _sqliteCache.GetFolderSignature(scanResultSnapshot);
                    sqliteScenes = _sqliteCache.GetIndexedScenes(cacheFolder, signature);
                }

                if (sqliteScenes is { Count: > 0 } && SceneHierarchy.Nodes.Count == 0)
                {
                    var quickDescriptors = SceneHierarchyBuilderService.BuildLazySceneDescriptors(
                        discoveredScenes: null,
                        sqliteIndexedScenes: sqliteScenes);
                    ApplyDescriptorsOnUiThread(quickDescriptors, markSessionLoaded: false);
                }

                if (!forceFullScan
                    && _lazySceneHierarchyLoadedSessionId == expectedSessionId
                    && _lazySceneHierarchyFullScanIndexCount == currentIndexCount)
                {
                    return;
                }

                token.ThrowIfCancellationRequested();
                var discovered = ProjectLoadingService.DiscoverScenesAndExternals(assetsManager, projectRoot);
                token.ThrowIfCancellationRequested();

                var descriptors = SceneHierarchyBuilderService.BuildLazySceneDescriptors(
                    discovered.Scenes,
                    sqliteScenes,
                    discovered.SceneFileStats,
                    discovered.CompanionStageFilesBySceneSf);

                _lazySceneHierarchyFullScanIndexCount = currentIndexCount;
                ApplyDescriptorsOnUiThread(descriptors, markSessionLoaded: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Warning, $"Failed to populate lazy Scene Hierarchy: {ex.Message}");
            }
        }, token);
    }

    private void OnLazySceneRootExpanded(GameObjectNode sceneRootNode)
    {
        if (IsWindowClosingOrClosed || sceneRootNode == null || !sceneRootNode.IsLazySceneRoot)
        {
            return;
        }

        if (sceneRootNode.IsSceneLoaded || sceneRootNode.IsSceneLoading)
        {
            return;
        }

        _ = ExpandLazySceneNodeAsync(sceneRootNode);
    }

    private async Task ExpandLazySceneNodeAsync(GameObjectNode sceneRootNode)
    {
        if (IsWindowClosingOrClosed
            || _isReturningToProjectManager
            || sceneRootNode == null
            || !sceneRootNode.IsLazySceneRoot
            || sceneRootNode.IsSceneLoaded
            || sceneRootNode.IsSceneLoading
            || sceneRootNode.LazyScene == null)
        {
            return;
        }

        var descriptor = sceneRootNode.LazyScene;
        var sessionId = lazyIndexingSessionId;
        var token = GetOrCreateLazySceneHierarchyToken();

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        sceneRootNode.IsSceneLoading = true;
        sceneRootNode.ReplaceChildren(new[]
        {
            new GameObjectNode
            {
                Name = SceneHierarchyBuilderService.LazyLoadingPlaceholderText,
                IsPlaceholder = true
            }
        });
        SceneHierarchy.NotifySceneNodeStateChanged();
        StatusStripUpdate($"Loading scene hierarchy '{descriptor.Name}'...");

        var gateAcquired = false;
        try
        {
            await _lazySceneLoadGate.WaitAsync(token);
            gateAcquired = true;

            if (IsWindowClosingOrClosed || sessionId != lazyIndexingSessionId || token.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref foregroundLazyLoadCount);
            LazySceneExpansionResult expansionResult;
            try
            {
                expansionResult = await Task.Run(() =>
                    SceneHierarchyBuilderService.LoadLazySceneHierarchy(
                        assetsManager: assetsManager,
                        descriptor: descriptor,
                        assemblyLoader: assemblyLoader,
                        resolveSourcePathByFileName: ResolveSceneDependencyDiskPath,
                        beforeLoadFile: RemovePendingFileFromProgressiveQueue,
                        cancellationToken: token),
                    token);
            }
            finally
            {
                Interlocked.Decrement(ref foregroundLazyLoadCount);
            }

            if (IsWindowClosingOrClosed || sessionId != lazyIndexingSessionId || token.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsWindowClosingOrClosed || sessionId != lazyIndexingSessionId || token.IsCancellationRequested)
                {
                    return;
                }

                sceneRootNode.IsSceneLoading = false;
                if (expansionResult.Success && expansionResult.RootChildren.Count > 0)
                {
                    sceneRootNode.ReplaceChildren(expansionResult.RootChildren);
                    sceneRootNode.IsSceneLoaded = true;

                    var goLabel = expansionResult.TotalGameObjectCount == 1 ? "object" : "objects";
                    var rendLabel = expansionResult.RendererCount == 1 ? "renderer" : "renderers";
                    var companionPart = expansionResult.CompanionAttachmentCount > 0
                        ? string.Create(CultureInfo.InvariantCulture, $" +{expansionResult.CompanionAttachmentCount:N0} preset(s)")
                        : string.Empty;
                    sceneRootNode.Subtitle = string.Create(
                        CultureInfo.InvariantCulture,
                        $"({expansionResult.TotalGameObjectCount:N0} {goLabel}, {expansionResult.RendererCount:N0} {rendLabel}{companionPart})");

                    if (!sceneTreeNodes.Contains(sceneRootNode))
                    {
                        sceneTreeNodes.Add(sceneRootNode);
                    }

                    SceneHierarchy.InvalidateSearchCache();
                    StatusStripUpdate(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Loaded scene '{descriptor.Name}': {expansionResult.TotalGameObjectCount:N0} {goLabel}, {expansionResult.RootChildren.Count:N0} root(s){companionPart}."));
                }
                else
                {
                    var message = !string.IsNullOrWhiteSpace(expansionResult.ErrorMessage)
                        ? $"({expansionResult.ErrorMessage})"
                        : "(No GameObjects in scene)";
                    sceneRootNode.ReplaceChildren(new[]
                    {
                        new GameObjectNode
                        {
                            Name = message,
                            IsPlaceholder = true
                        }
                    });
                    sceneRootNode.IsSceneLoaded = true;
                    StatusStripUpdate($"Scene '{descriptor.Name}': {expansionResult.ErrorMessage}");
                }

                SceneHierarchy.NotifySceneNodeStateChanged();
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsWindowClosingOrClosed && !sceneRootNode.IsSceneLoaded)
                {
                    sceneRootNode.IsSceneLoading = false;
                    sceneRootNode.ReplaceChildren(new[]
                    {
                        new GameObjectNode
                        {
                            Name = SceneHierarchyBuilderService.LazyExpandPlaceholderText,
                            IsPlaceholder = true
                        }
                    });
                    SceneHierarchy.NotifySceneNodeStateChanged();
                }
            });
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Warning, $"Failed to expand lazy scene '{descriptor.Name}': {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsWindowClosingOrClosed)
                {
                    sceneRootNode.IsSceneLoading = false;
                    sceneRootNode.IsSceneLoaded = true;
                    sceneRootNode.ReplaceChildren(new[]
                    {
                        new GameObjectNode
                        {
                            Name = $"(Failed to load: {ex.Message})",
                            IsPlaceholder = true
                        }
                    });
                    SceneHierarchy.NotifySceneNodeStateChanged();
                    StatusStripUpdate($"Failed to load scene '{descriptor.Name}': {ex.Message}");
                }
            });
        }
        finally
        {
            if (gateAcquired)
            {
                _lazySceneLoadGate.Release();
            }
        }
    }

    private async Task OnSceneHierarchyModeActivatedAsync(SceneHierarchyViewMode mode)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (mode == SceneHierarchyViewMode.Containers)
        {
            await EnsureContainersIndexBuiltAsync(forceRebuild: false);
            var query = treeSearch?.Text?.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                await SceneHierarchy.FilterContainersAsync(query);
            }
        }
        else
        {
            SceneTreeView.ItemsSource = SceneHierarchy.Nodes;
            SceneHierarchy.UpdateModeSummaryText();
        }
    }

    private async Task RefreshActiveSceneHierarchyModeAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (SceneHierarchy.IsContainersMode)
        {
            await EnsureContainersIndexBuiltAsync(forceRebuild: true);
            var query = treeSearch?.Text?.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                await SceneHierarchy.FilterContainersAsync(query);
            }
        }
        else if (assetsManager.LazyLoading)
        {
            RefreshLazySceneHierarchyAsync(lazyIndexingSessionId);
        }
        else
        {
            ApplyEagerSceneHierarchy(sceneTreeNodes);
        }
    }

    private async Task EnsureContainersIndexBuiltAsync(bool forceRebuild)
    {
        if (IsWindowClosingOrClosed || _isBuildingContainersIndex)
        {
            return;
        }

        var isLazy = assetsManager.LazyLoading;
        var currentAssetCount = isLazy ? assetsManager.ProjectIndex.Count : _eagerExportableAssets.Count;
        var displayAll = ViewModel.Options.DisplayAll;
        var sessionId = lazyIndexingSessionId;

        if (!forceRebuild
            && SceneHierarchy.ContainerIndex != null
            && _containersIndexSessionId == sessionId
            && _containersIndexAssetCount == currentAssetCount
            && _containersIndexDisplayAll == displayAll
            && _containersIndexIsLazy == isLazy)
        {
            return;
        }

        if (currentAssetCount == 0)
        {
            SceneHierarchy.SetContainerIndex(VirtualContainerTreeIndex.CreateEmpty());
            _containersIndexSessionId = sessionId;
            _containersIndexAssetCount = 0;
            _containersIndexDisplayAll = displayAll;
            _containersIndexIsLazy = isLazy;
            return;
        }

        _isBuildingContainersIndex = true;
        var token = GetOrCreateLazySceneHierarchyToken();
        try
        {
            var eagerSnapshot = !isLazy ? _eagerExportableAssets.ToList() : null;
            var builtIndex = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return isLazy
                    ? VirtualContainerTreeIndex.BuildFromProjectIndex(
                        assetsManager.ProjectIndex,
                        includeAllTypes: displayAll,
                        exportableFilter: IsLazyExportableType,
                        cancellationToken: token)
                    : VirtualContainerTreeIndex.BuildFromAssetItems(
                        eagerSnapshot,
                        cancellationToken: token);
            }, token);

            if (IsWindowClosingOrClosed || sessionId != lazyIndexingSessionId || token.IsCancellationRequested)
            {
                return;
            }

            SceneHierarchy.SetContainerIndex(builtIndex);
            _containersIndexSessionId = sessionId;
            _containersIndexAssetCount = currentAssetCount;
            _containersIndexDisplayAll = displayAll;
            _containersIndexIsLazy = isLazy;
            SceneHierarchy.CanRefresh = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Warning, $"Failed to build Containers virtual tree: {ex.Message}");
            StatusStripUpdate($"Failed to build Containers tree: {ex.Message}");
        }
        finally
        {
            _isBuildingContainersIndex = false;
        }
    }

    private async Task ExportSelectedSceneFromHierarchyAsync(GameObjectNode? selectedNode)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var targetNode = selectedNode ?? SceneTreeView?.SelectedItem as GameObjectNode;
        if (targetNode != null && !targetNode.IsPlaceholder)
        {
            var sceneRoot = SceneGlbExportService.FindSceneRootNode(targetNode);
            if (sceneRoot.IsLazySceneRoot && !sceneRoot.IsSceneLoaded)
            {
                sceneRoot.IsExpanded = true;
                await ExpandLazySceneNodeAsync(sceneRoot);
                if (IsWindowClosingOrClosed)
                {
                    return;
                }
            }

            if (SceneTreeView != null && !ReferenceEquals(SceneTreeView.SelectedItem, targetNode))
            {
                SceneTreeView.SelectedItem = targetNode;
            }
        }

        await ExportSceneGlbCommandHandlerAsync();
    }

    private async Task ExportSelectedSubtreeGlbWithLazySupportAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (SceneTreeView?.SelectedItem is GameObjectNode selectedNode
            && selectedNode.IsLazySceneRoot
            && !selectedNode.IsSceneLoaded)
        {
            selectedNode.IsExpanded = true;
            await ExpandLazySceneNodeAsync(selectedNode);
            if (IsWindowClosingOrClosed)
            {
                return;
            }
        }

        await ExportSelectedSubtreeGlbCommandHandlerAsync();
    }

    private void TreeSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = treeSearch.Text ?? string.Empty;
        SceneHierarchy.SearchQuery = query;
        ResetSceneTreeSearchState();

        if (SceneHierarchy.IsContainersMode && string.IsNullOrWhiteSpace(query))
        {
            SceneHierarchy.ClearContainerFilter();
        }
    }

    private async void TreeSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var searchText = treeSearch.Text?.Trim();

        if (SceneHierarchy.IsContainersMode)
        {
            await EnsureContainersIndexBuiltAsync(forceRebuild: false);
            if (string.IsNullOrEmpty(searchText))
            {
                SceneHierarchy.ClearContainerFilter();
                return;
            }

            var containerResult = await SceneHierarchy.FilterContainersAsync(searchText);
            if (!string.IsNullOrEmpty(SceneHierarchy.SearchStatusText))
            {
                StatusStripUpdate(SceneHierarchy.SearchStatusText);
            }
            else if (containerResult.TotalMatchedAssets == 0)
            {
                StatusStripUpdate($"No container match for '{searchText}'.");
            }
            return;
        }

        if (string.IsNullOrEmpty(searchText))
        {
            return;
        }

        // Reset/reload paths clear treeSearchResults directly; treat that as a cache invalidation.
        if (appliedTreeSearchMatches != null && treeSearchResults.Count != appliedTreeSearchMatches.Length)
        {
            ResetSceneTreeSearchState();
        }

        var roots = SceneHierarchy.Nodes.Count > 0 ? (IReadOnlyList<GameObjectNode>)SceneHierarchy.Nodes : sceneTreeNodes;
        SceneHierarchy.BindRootNodesReference(roots);
        if (!SceneHierarchy.TryGetCachedMatches(searchText, out _) && !SceneHierarchy.IsSearching)
        {
            StatusStripUpdate($"Searching scene hierarchy for '{searchText}'...");
        }

        var selectedNode = await SceneHierarchy.SearchNextAsync(searchText);
        if (SceneHierarchy.WasLastSearchSuperseded
            || !ReferenceEquals(roots, SceneHierarchy.RootNodes)
            || !string.Equals(searchText, treeSearch.Text?.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        var currentMatches = SceneHierarchy.AppliedMatchesArray;
        if (currentMatches != null)
        {
            ApplyTreeSearchMatches(currentMatches);
            nextGameObjectSearchIndex = SceneHierarchy.CurrentMatchIndex;
        }

        if (selectedNode == null)
        {
            if (!string.IsNullOrEmpty(SceneHierarchy.SearchStatusText))
            {
                StatusStripUpdate(SceneHierarchy.SearchStatusText);
            }
            return;
        }

        SceneTreeView.SelectedItem = selectedNode;
        StatusStripUpdate(SceneHierarchy.SearchStatusText);
    }

    private void ApplyTreeSearchMatches(GameObjectNode[] matches)
    {
        if (treeSearchResults.Count == matches.Length
            && (matches.Length == 0 || ReferenceEquals(treeSearchResults[0], matches[0])))
        {
            return;
        }

        treeSearchResults.Clear();
        treeSearchResults.AddRange(matches);
        nextGameObjectSearchIndex = 0;
    }

    private void SceneTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var selectedNode = SceneTreeView?.SelectedItem as GameObjectNode;
        SceneHierarchy.SelectedNode = selectedNode;

        if (selectedNode == null || selectedNode.IsPlaceholder)
        {
            if (PreviewSession.IsGameObjectInspectorVisible)
            {
                PreviewSession.ClearGameObjectInspector();
            }
            return;
        }

        if (selectedNode.IsLazySceneRoot && !selectedNode.IsSceneLoaded)
        {
            var hint = selectedNode.IsSceneLoading
                ? $"Loading scene '{selectedNode.Name}'..."
                : $"Scene '{selectedNode.Name}' {selectedNode.Subtitle} — expand to load hierarchy on demand.";
            StatusStripUpdate(hint);
            if (!ViewModel.Options.EnablePreview)
            {
                return;
            }
        }

        if (!ViewModel.Options.EnablePreview)
        {
            return;
        }

        InspectSceneHierarchyNode(selectedNode);
    }

    private void ContainersTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var selectedNode = ContainersTreeView?.SelectedItem as ContainerTreeNode;
        SceneHierarchy.SelectedContainerNode = selectedNode;

        if (selectedNode == null || selectedNode.IsPlaceholder)
        {
            return;
        }

        if (selectedNode.IsLoadMoreNode)
        {
            SceneHierarchy.TryLoadMoreContainerNodes(selectedNode);
            return;
        }

        if (selectedNode.IsFolder)
        {
            var assetWord = selectedNode.TotalAssetCount == 1 ? "asset" : "assets";
            StatusStripUpdate(string.Create(
                CultureInfo.InvariantCulture,
                $"Container: {selectedNode.FullPath} ({selectedNode.TotalAssetCount:N0} {assetWord})"));
            return;
        }

        PreviewContainerLeafAsset(selectedNode);
    }

    private void PreviewContainerLeafAsset(ContainerTreeNode node)
    {
        if (IsWindowClosingOrClosed || !node.IsAssetLeaf)
        {
            return;
        }

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        PreviewSession.ClearGameObjectInspector();

        // First try selecting the matching row in AssetListDataGrid (without switching LeftTabControl!)
        // so Preview, Dump, References, Script, and Navigation History all synchronize seamlessly.
        var existingListItem = TryLocateExistingAssetItemForContainerNode(node);
        if (existingListItem != null)
        {
            var alreadySelected = ReferenceEquals(AssetListDataGrid.SelectedItem, existingListItem);
            AssetListDataGrid.SelectedItem = existingListItem;
            if (alreadySelected)
            {
                QueuePreviewAsset(existingListItem, immediate: true);
            }
            StatusStripUpdate($"Container asset: {node.FullPath} [{node.TypeBadge}]");
            return;
        }

        // Fallback when the asset is currently filtered out of AssetListDataGrid (or DisplayAll is off):
        // materialize a lightweight AssetItem and preview it directly in the Preview panel.
        var directItem = MaterializeAssetItemForContainerNode(node);
        if (directItem == null)
        {
            StatusStripUpdate($"Could not resolve container asset '{node.Name}'.");
            return;
        }

        if (!ViewModel.Options.EnablePreview)
        {
            StatusStripUpdate($"Selected {directItem.DisplayType}: {directItem.Name} (preview disabled)");
            return;
        }

        var requestId = Interlocked.Increment(ref _containerPreviewRequestId);
        var uniqueId = directItem.Handle != null ? directItem.Handle.UniqueID : directItem.UniqueID;
        if (!string.Equals(_currentlySelectedUniqueID, uniqueId, StringComparison.Ordinal))
        {
            PreviewSession.ShaderPreview.Clear();
        }
        _currentlySelectedUniqueID = uniqueId;
        CancelPendingPreview();

        if (RightTabControl != null && RightTabControl.SelectedIndex != 0)
        {
            _suppressNextRightTabSelectionPreview = true;
            RightTabControl.SelectedIndex = 0;
        }

        StatusStripUpdate($"Loading container asset: {directItem.Name}...");
        _ = Task.Run(() =>
        {
            try
            {
                var resolved = ResolveAssetForPreview(directItem);
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsWindowClosingOrClosed
                        || requestId != Volatile.Read(ref _containerPreviewRequestId)
                        || !ReferenceEquals(ContainersTreeView?.SelectedItem, node))
                    {
                        return;
                    }

                    PreviewAsset(directItem, resolved, assetResolutionAttempted: true);
                    StatusStripUpdate($"Container asset: {node.FullPath} [{node.TypeBadge}]");
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Warning, $"Failed to preview container asset '{node.Name}': {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsWindowClosingOrClosed && requestId == Volatile.Read(ref _containerPreviewRequestId))
                    {
                        StatusStripUpdate($"Preview error: {ex.Message}");
                    }
                });
            }
        });
    }

    private AssetItem? TryLocateExistingAssetItemForContainerNode(ContainerTreeNode node)
    {
        if (node.EagerAssetItem != null && _eagerVisibleAssets.Contains(node.EagerAssetItem))
        {
            return node.EagerAssetItem;
        }

        if (_isArenaAssetListMode && node.ArenaIndex >= 0)
        {
            var row = ViewModel.AssetList.LocateVisibleArenaRowByArenaIndex(node.ArenaIndex);
            if (row >= 0 && row < visibleAssetItems.Count)
            {
                return visibleAssetItems[row];
            }
        }

        return null;
    }

    private AssetItem? MaterializeAssetItemForContainerNode(ContainerTreeNode node)
    {
        if (node.EagerAssetItem != null)
        {
            return node.EagerAssetItem;
        }

        if (node.ArenaIndex >= 0 && node.ArenaIndex < assetsManager.ProjectIndex.Count)
        {
            var handle = assetsManager.ProjectIndex.MaterializeHandle(node.ArenaIndex);
            return new AssetItem(handle, node.ArenaIndex);
        }

        if (node.Handle != null)
        {
            return new AssetItem(node.Handle);
        }

        if (!string.IsNullOrWhiteSpace(node.UniqueId))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(node.UniqueId);
            if (handle != null)
            {
                return new AssetItem(handle);
            }
        }

        return null;
    }

    private async Task ShowContainerLeafInAssetListAsync(ContainerTreeNode? node)
    {
        if (IsWindowClosingOrClosed || node == null || !node.IsAssetLeaf)
        {
            return;
        }

        var reference = new AssetReferenceItem
        {
            AssetUniqueId = node.UniqueId,
            Name = node.Name,
            AssetType = node.AssetType,
            TypeName = node.TypeBadge,
            Relation = "Container",
            PathId = node.PathId,
            SerializedFileName = node.SerializedFileName,
            Direction = AssetReferenceDirection.References
        };

        await NavigateToReferencedAssetCommandHandlerAsync(reference);
    }

    private async Task CopyContainerNodePathAsync(ContainerTreeNode? node)
    {
        if (IsWindowClosingOrClosed || node == null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        if (await ViewModel.DialogService.CopyTextToClipboardAsync(node.FullPath))
        {
            StatusStripUpdate($"Copied container path: {node.FullPath}");
        }
    }

    private async Task ExportContainerLeafAssetAsync(ContainerTreeNode? node)
    {
        if (IsWindowClosingOrClosed || node == null || !node.IsAssetLeaf)
        {
            return;
        }

        var item = TryLocateExistingAssetItemForContainerNode(node) ?? MaterializeAssetItemForContainerNode(node);
        if (item == null)
        {
            StatusStripUpdate($"Could not resolve container asset '{node.Name}' for export.");
            return;
        }

        await ExportAssets(new List<AssetItem> { item }, ExportMode.Convert);
    }

    private void InspectSceneHierarchyNode(GameObjectNode node)
    {
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var token = PreviewSession.BeginGameObjectInspectorRequest(out var requestId);
        var scanResultSnapshot = currentScanResult;
        var folderPath = GetCurrentCacheFolderPath();

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                Dictionary<string, AssetReferenceItem>? semanticLookup = null;
                if (node.GameObject != null)
                {
                    semanticLookup = BuildSemanticOutgoingLookupBackground(
                        node.GameObject.assetsFile,
                        node.GameObject.m_PathID,
                        scanResultSnapshot,
                        folderPath);
                }

                var snapshot = GameObjectInspectorService.BuildFromNode(
                    node,
                    pptrResolver: (srcFile, fileId, pathId, fallbackType, relation) =>
                        ResolvePPtrMetadataForInspector(srcFile, fileId, pathId, fallbackType, relation, semanticLookup),
                    projectIndex: assetsManager.ProjectIndex,
                    cancellationToken: token);

                token.ThrowIfCancellationRequested();

                Dispatcher.UIThread.Post(() =>
                {
                    if (IsWindowClosingOrClosed
                        || !PreviewSession.IsGameObjectInspectorRequestCurrent(requestId, token)
                        || !ReferenceEquals(SceneTreeView?.SelectedItem, node))
                    {
                        return;
                    }

                    if (RightTabControl != null && RightTabControl.SelectedIndex != 0)
                    {
                        _suppressNextRightTabSelectionPreview = true;
                        RightTabControl.SelectedIndex = 0;
                    }

                    TextPreviewBox.IsVisible = false;
                    if (ImagePreviewBox != null)
                    {
                        ImagePreviewBox.IsVisible = false;
                    }
                    if (GLPreviewControl != null)
                    {
                        GLPreviewControl.IsVisible = false;
                    }
                    if (TextureGLPreview != null)
                    {
                        TextureGLPreview.IsVisible = false;
                    }
                    PreviewLabel.IsVisible = false;

                    PreviewSession.CompleteGameObjectInspectorRequest(requestId, snapshot);
                    var compCount = snapshot.Components.Count;
                    StatusStripUpdate($"Inspecting GameObject: {snapshot.Name} ({compCount} {(compCount == 1 ? "component" : "components")})");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Warning, $"Failed to inspect scene node '{node.Name}': {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsWindowClosingOrClosed && PreviewSession.IsGameObjectInspectorRequestCurrent(requestId, token))
                    {
                        PreviewSession.IsGameObjectInspectorLoading = false;
                        StatusStripUpdate($"GameObject inspection error: {ex.Message}");
                    }
                });
            }
        }, token);
    }

    private void ExecuteGoToSceneHierarchy()
    {
        var item = assetContextItem ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (!SceneHierarchy.TryNavigateToAssetNode(item))
        {
            StatusStripUpdate(SceneHierarchy.SearchStatusText);
            return;
        }

        SceneHierarchy.SetActiveMode(SceneHierarchyViewMode.Scenes);
        LeftTabControl.SelectedIndex = 0;
        SceneTreeView.SelectedItem = SceneHierarchy.SelectedNode;
        SceneTreeView.Focus();
    }

    private void LinkAssetItemsToSceneNodes(Dictionary<GameObject, GameObjectNode> treeNodeDictionary, Dictionary<Object, AssetItem> objectAssetItemDic)
    {
        LinkAssetItemsToSceneNodesBackground(assetsManager.assetsFileList, treeNodeDictionary, objectAssetItemDic);
    }
}

/// <summary>
/// Stack-safe scene hierarchy name search (iterative pre-order, same order as the legacy recursive DFS).
/// </summary>
internal static class SceneTreeSearch
{
    private const int CancellationCheckMask = 1023;

    internal static GameObjectNode[] FindMatches(IReadOnlyList<GameObjectNode> roots, string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<GameObjectNode>();
        var stack = new Stack<GameObjectNode>();
        for (int r = roots.Count - 1; r >= 0; r--)
        {
            stack.Push(roots[r]);
        }

        var visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = stack.Pop();
            if (node.IsPlaceholder)
            {
                continue;
            }

            if (node.Name is { } name && name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(node);
            }

            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results.ToArray();
    }
}

/// <summary>
/// Latest-wins background scene search with a per-(query, tree generation) match cache, so
/// cycling through matches with repeated Enter presses never re-traverses the hierarchy.
/// Tree generation = identity of the root list plus its root count (reload swaps the list,
/// reset clears it in place). Callers on the UI thread; internally synchronized for tests.
/// </summary>
internal sealed class SceneTreeSearchSession
{
    private readonly object gate = new();
    private CancellationTokenSource? activeCts;
    private Task<GameObjectNode[]?>? activeTask;
    private string? activeQuery;
    private IReadOnlyList<GameObjectNode>? activeRoots;
    private int activeRootCount;

    private bool hasCache;
    private string? cachedQuery;
    private IReadOnlyList<GameObjectNode>? cachedRoots;
    private int cachedRootCount;
    private GameObjectNode[] cachedMatches = Array.Empty<GameObjectNode>();

    private int traversalCount;

    /// <summary>Number of full hierarchy traversals started (diagnostics/tests).</summary>
    public int TraversalCount => Volatile.Read(ref traversalCount);

    public bool IsSearching
    {
        get
        {
            lock (gate)
            {
                return activeCts != null;
            }
        }
    }

    public bool TryGetCached(IReadOnlyList<GameObjectNode> roots, string query, out GameObjectNode[] matches)
    {
        lock (gate)
        {
            if (hasCache
                && ReferenceEquals(roots, cachedRoots)
                && roots.Count == cachedRootCount
                && string.Equals(query, cachedQuery, StringComparison.Ordinal))
            {
                matches = cachedMatches;
                return true;
            }
        }

        matches = Array.Empty<GameObjectNode>();
        return false;
    }

    /// <summary>Cancels any in-flight search and drops the cached matches.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            CancelActiveLocked();
            hasCache = false;
            cachedQuery = null;
            cachedRoots = null;
            cachedRootCount = 0;
            cachedMatches = Array.Empty<GameObjectNode>();
        }
    }

    /// <summary>
    /// Returns the matches for <paramref name="query"/> (cached when possible), or <c>null</c> when
    /// the search was superseded by a newer query or invalidated. A concurrent request for the same
    /// (query, tree generation) joins the in-flight search instead of restarting it.
    /// </summary>
    public Task<GameObjectNode[]?> SearchAsync(IReadOnlyList<GameObjectNode> roots, string query)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(query);

        lock (gate)
        {
            if (hasCache
                && ReferenceEquals(roots, cachedRoots)
                && roots.Count == cachedRootCount
                && string.Equals(query, cachedQuery, StringComparison.Ordinal))
            {
                return Task.FromResult<GameObjectNode[]?>(cachedMatches);
            }

            if (activeTask != null
                && ReferenceEquals(roots, activeRoots)
                && roots.Count == activeRootCount
                && string.Equals(query, activeQuery, StringComparison.Ordinal))
            {
                return activeTask;
            }

            CancelActiveLocked();
            var cts = new CancellationTokenSource();
            var snapshot = new GameObjectNode[roots.Count];
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = roots[i];
            }

            activeCts = cts;
            activeQuery = query;
            activeRoots = roots;
            activeRootCount = snapshot.Length;
            var task = RunSearchAsync(cts, snapshot, roots, query);
            if (ReferenceEquals(activeCts, cts))
            {
                activeTask = task;
            }

            return task;
        }
    }

    private async Task<GameObjectNode[]?> RunSearchAsync(
        CancellationTokenSource cts,
        GameObjectNode[] snapshot,
        IReadOnlyList<GameObjectNode> roots,
        string query)
    {
        var token = cts.Token;
        try
        {
            var matches = await Task.Run(() =>
            {
                Interlocked.Increment(ref traversalCount);
                return SceneTreeSearch.FindMatches(snapshot, query, token);
            }, token).ConfigureAwait(true);

            lock (gate)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(activeCts, cts))
                {
                    return null;
                }

                hasCache = true;
                cachedQuery = query;
                cachedRoots = roots;
                cachedRootCount = snapshot.Length;
                cachedMatches = matches;
                return matches;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeCts, cts))
                {
                    activeCts = null;
                    activeTask = null;
                    activeQuery = null;
                    activeRoots = null;
                    activeRootCount = 0;
                }

                // A cancelled source may still be inside Cancel() on another frame; it owns no
                // timer or wait handle, so only dispose sources that completed uncancelled.
                if (!token.IsCancellationRequested)
                {
                    cts.Dispose();
                }
            }
        }
    }

    private void CancelActiveLocked()
    {
        var cts = activeCts;
        activeCts = null;
        activeTask = null;
        activeQuery = null;
        activeRoots = null;
        activeRootCount = 0;
        cts?.Cancel();
    }
}
