using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class SceneHierarchyViewModel : ViewModelBase
    {
        private readonly Action<Action> _dispatchToUi;
        private readonly SceneTreeSearchSession _searchSession;
        private readonly object _stateLock = new();

        private IReadOnlyList<GameObjectNode> _rootNodesSource = Array.Empty<GameObjectNode>();
        private GameObjectNode[]? _appliedMatches;
        private string? _activeOrCachedQuery;

        private VirtualContainerTreeIndex? _containerIndex;
        private CancellationTokenSource? _containerFilterCts;
        private long _containerFilterRequestId;

        private SceneHierarchyViewMode _activeMode = SceneHierarchyViewMode.Scenes;
        private string _searchQuery = string.Empty;
        private bool _isSearching;
        private int _matchCount;
        private int _currentMatchIndex;
        private bool _hasMatches;
        private string _searchStatusText = string.Empty;
        private string _modeSummaryText = string.Empty;
        private bool _isLazyMode;
        private bool _canRefresh;
        private bool _isFilteringContainers;
        private int _totalRootCount;
        private GameObjectNode? _selectedNode;
        private GameObjectNode? _currentMatchNode;
        private ContainerTreeNode? _selectedContainerNode;
        private int _nextMatchZeroBasedIndex;
        private bool _suppressQueryInvalidation;
        private bool _wasLastSearchSuperseded;
        private int _realGeometrySceneCount;

        public SceneHierarchyViewModel(Action<Action>? dispatchToUi = null)
            : this(dispatchToUi, null)
        {
        }

        internal SceneHierarchyViewModel(
            Action<Action>? dispatchToUi,
            SceneTreeSearchSession? searchSession)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            _searchSession = searchSession ?? new SceneTreeSearchSession();
            Nodes = new BulkObservableCollection<GameObjectNode>();
            ContainerNodes = new BulkObservableCollection<ContainerTreeNode>();

            ShowScenesModeCommand = new RelayCommand(() => SetActiveMode(SceneHierarchyViewMode.Scenes));
            ShowContainersModeCommand = new RelayCommand(() => SetActiveMode(SceneHierarchyViewMode.Containers));
            RefreshCurrentModeCommand = new AsyncRelayCommand(ExecuteRefreshAsync, canExecute: () => CanRefresh);
            LoadSelectedSceneCommand = new AsyncRelayCommand(ExecuteLoadSelectedSceneAsync, canExecute: () => CanLoadSelectedScene);
            ExportSelectedSceneGlbCommand = new AsyncRelayCommand(ExecuteExportSelectedSceneAsync);
            ExpandSelectedSubtreeCommand = new RelayCommand(ExpandSelectedSubtree);
            CollapseSelectedSubtreeCommand = new RelayCommand(CollapseSelectedSubtree);
            ShowSelectedContainerInAssetListCommand = new AsyncRelayCommand(
                ExecuteShowSelectedContainerInAssetListAsync,
                canExecute: () => SelectedContainerNode?.IsAssetLeaf == true);
            CopySelectedContainerPathCommand = new AsyncRelayCommand(
                ExecuteCopySelectedContainerPathAsync,
                canExecute: () => SelectedContainerNode != null && !string.IsNullOrWhiteSpace(SelectedContainerNode.FullPath));
            ExportSelectedContainerAssetCommand = new AsyncRelayCommand(
                ExecuteExportSelectedContainerAssetAsync,
                canExecute: () => SelectedContainerNode?.IsAssetLeaf == true);
        }

        public BulkObservableCollection<GameObjectNode> Nodes { get; }

        public BulkObservableCollection<ContainerTreeNode> ContainerNodes { get; }

        public IReadOnlyList<GameObjectNode> RootNodes => _rootNodesSource;

        public IReadOnlyList<GameObjectNode> Matches => _appliedMatches ?? Array.Empty<GameObjectNode>();

        internal GameObjectNode[]? AppliedMatchesArray => _appliedMatches;

        internal SceneTreeSearchSession SearchSession => _searchSession;

        public VirtualContainerTreeIndex? ContainerIndex => _containerIndex;

        public int TraversalCount => _searchSession.TraversalCount;

        internal bool WasLastSearchSuperseded => _wasLastSearchSuperseded;

        public Func<SceneHierarchyViewMode, Task>? ModeActivatedAsync { get; set; }
        public Func<GameObjectNode, Task>? ExpandLazySceneHandlerAsync { get; set; }
        public Func<Task>? RefreshHandlerAsync { get; set; }
        public Func<GameObjectNode?, Task>? ExportSelectedSceneHandlerAsync { get; set; }
        public Func<ContainerTreeNode?, Task>? ShowContainerAssetInListHandlerAsync { get; set; }
        public Func<ContainerTreeNode?, Task>? CopyContainerPathHandlerAsync { get; set; }
        public Func<ContainerTreeNode?, Task>? ExportContainerAssetHandlerAsync { get; set; }

        public RelayCommand ShowScenesModeCommand { get; }
        public RelayCommand ShowContainersModeCommand { get; }
        public AsyncRelayCommand RefreshCurrentModeCommand { get; }
        public AsyncRelayCommand LoadSelectedSceneCommand { get; }
        public AsyncRelayCommand ExportSelectedSceneGlbCommand { get; }
        public RelayCommand ExpandSelectedSubtreeCommand { get; }
        public RelayCommand CollapseSelectedSubtreeCommand { get; }
        public AsyncRelayCommand ShowSelectedContainerInAssetListCommand { get; }
        public AsyncRelayCommand CopySelectedContainerPathCommand { get; }
        public AsyncRelayCommand ExportSelectedContainerAssetCommand { get; }

        public SceneHierarchyViewMode ActiveMode
        {
            get => _activeMode;
            set => SetActiveMode(value);
        }

        public bool IsScenesMode
        {
            get => _activeMode == SceneHierarchyViewMode.Scenes;
            set
            {
                if (value)
                {
                    SetActiveMode(SceneHierarchyViewMode.Scenes);
                }
                else
                {
                    OnPropertyChanged(nameof(IsScenesMode));
                }
            }
        }

        public bool IsContainersMode
        {
            get => _activeMode == SceneHierarchyViewMode.Containers;
            set
            {
                if (value)
                {
                    SetActiveMode(SceneHierarchyViewMode.Containers);
                }
                else
                {
                    OnPropertyChanged(nameof(IsContainersMode));
                }
            }
        }

        public string SearchPlaceholderText => IsContainersMode
            ? " Filter containers & bundles (Enter) "
            : " Search scene hierarchy (Enter) ";

        public string ModeSummaryText
        {
            get => _modeSummaryText;
            private set => SetProperty(ref _modeSummaryText, value ?? string.Empty);
        }

        public bool IsLazyMode
        {
            get => _isLazyMode;
            private set => SetProperty(ref _isLazyMode, value);
        }

        public bool CanRefresh
        {
            get => _canRefresh;
            set
            {
                if (SetProperty(ref _canRefresh, value))
                {
                    RefreshCurrentModeCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsFilteringContainers
        {
            get => _isFilteringContainers;
            private set => SetProperty(ref _isFilteringContainers, value);
        }

        public bool CanLoadSelectedScene =>
            _selectedNode is { IsLazySceneRoot: true, IsSceneLoaded: false, IsSceneLoading: false };

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _searchQuery, normalized))
                {
                    if (!_suppressQueryInvalidation)
                    {
                        var trimmed = normalized.Trim();
                        if (!string.Equals(trimmed, _activeOrCachedQuery, StringComparison.Ordinal))
                        {
                            InvalidateSearchCache();
                        }
                    }
                }
            }
        }

        public bool IsSearching
        {
            get => _isSearching;
            private set => SetProperty(ref _isSearching, value);
        }

        public int MatchCount
        {
            get => _matchCount;
            private set
            {
                if (SetProperty(ref _matchCount, value))
                {
                    HasMatches = value > 0;
                }
            }
        }

        /// <summary>
        /// 1-based index of the currently active match (<c>1..MatchCount</c>), or <c>0</c> when no match is selected.
        /// </summary>
        public int CurrentMatchIndex
        {
            get => _currentMatchIndex;
            private set => SetProperty(ref _currentMatchIndex, value);
        }

        public bool HasMatches
        {
            get => _hasMatches;
            private set => SetProperty(ref _hasMatches, value);
        }

        public string SearchStatusText
        {
            get => _searchStatusText;
            private set => SetProperty(ref _searchStatusText, value ?? string.Empty);
        }

        public int TotalRootCount
        {
            get => _totalRootCount;
            private set => SetProperty(ref _totalRootCount, value);
        }

        public GameObjectNode? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (SetProperty(ref _selectedNode, value))
                {
                    NotifySceneNodeStateChanged();
                }
            }
        }

        public GameObjectNode? CurrentMatchNode
        {
            get => _currentMatchNode;
            private set => SetProperty(ref _currentMatchNode, value);
        }

        public ContainerTreeNode? SelectedContainerNode
        {
            get => _selectedContainerNode;
            set
            {
                if (SetProperty(ref _selectedContainerNode, value))
                {
                    ShowSelectedContainerInAssetListCommand.NotifyCanExecuteChanged();
                    CopySelectedContainerPathCommand.NotifyCanExecuteChanged();
                    ExportSelectedContainerAssetCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public void SetActiveMode(SceneHierarchyViewMode mode)
        {
            var changed = SetProperty(ref _activeMode, mode, nameof(ActiveMode));
            if (!changed)
            {
                return;
            }

            OnPropertyChanged(nameof(IsScenesMode));
            OnPropertyChanged(nameof(IsContainersMode));
            OnPropertyChanged(nameof(SearchPlaceholderText));
            UpdateModeSummaryText();

            if (ModeActivatedAsync != null)
            {
                _ = ModeActivatedAsync(mode);
            }
        }

        public void NotifySceneNodeStateChanged()
        {
            OnPropertyChanged(nameof(CanLoadSelectedScene));
            LoadSelectedSceneCommand.NotifyCanExecuteChanged();
        }

        public void SetRootNodes(IReadOnlyList<GameObjectNode>? roots)
        {
            SetSceneRootsInternal(roots, isLazyMode: false, realGeometrySceneCount: -1, updateSummary: false);
        }

        public void SetEagerSceneRoots(IReadOnlyList<GameObjectNode>? roots)
        {
            SetSceneRootsInternal(roots, isLazyMode: false, realGeometrySceneCount: -1, updateSummary: true);
        }

        public void SetLazySceneRoots(IReadOnlyList<GameObjectNode>? roots, int realGeometrySceneCount = -1)
        {
            SetSceneRootsInternal(roots, isLazyMode: true, realGeometrySceneCount: realGeometrySceneCount, updateSummary: true);
        }

        private void SetSceneRootsInternal(
            IReadOnlyList<GameObjectNode>? roots,
            bool isLazyMode,
            int realGeometrySceneCount,
            bool updateSummary)
        {
            var effectiveRoots = roots ?? Array.Empty<GameObjectNode>();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    _rootNodesSource = effectiveRoots;
                    _realGeometrySceneCount = realGeometrySceneCount;
                    IsLazyMode = isLazyMode;
                    InvalidateSearchStateLocked(clearStatus: true);
                    if (effectiveRoots.Count == 0)
                    {
                        if (Nodes.Count > 0)
                        {
                            Nodes.Clear();
                        }
                    }
                    else
                    {
                        Nodes.ReplaceAll(effectiveRoots);
                    }

                    TotalRootCount = effectiveRoots.Count;
                    SelectedNode = null;
                    if (updateSummary)
                    {
                        UpdateModeSummaryText();
                    }
                }
            });
        }

        public void SetContainerIndex(VirtualContainerTreeIndex? index)
        {
            CancelActiveContainerFilter();
            _dispatchToUi(() =>
            {
                _containerIndex = index;
                IsFilteringContainers = false;
                SelectedContainerNode = null;

                if (index == null || index.RootFolderCount == 0)
                {
                    if (ContainerNodes.Count > 0)
                    {
                        ContainerNodes.Clear();
                    }
                }
                else
                {
                    var roots = index.CreateRootNodes(0, HandleContainerFolderExpanded);
                    ContainerNodes.ReplaceAll(roots);
                }

                UpdateModeSummaryText();
            });
        }

        public void HandleContainerFolderExpanded(ContainerTreeNode folderNode)
        {
            if (folderNode == null || !folderNode.IsFolder || _containerIndex == null)
            {
                return;
            }

            _containerIndex.MaterializeFolderChildren(folderNode, HandleContainerFolderExpanded);
        }

        public bool TryLoadMoreContainerNodes(ContainerTreeNode? loadMoreNode)
        {
            if (loadMoreNode == null || !loadMoreNode.IsLoadMoreNode || _containerIndex == null)
            {
                return false;
            }

            if (loadMoreNode.Parent == null)
            {
                var nextPage = _containerIndex.CreateRootNodes(loadMoreNode.NextPageStartIndex, HandleContainerFolderExpanded);
                if (nextPage.Count == 0)
                {
                    return false;
                }

                var updated = new List<ContainerTreeNode>(ContainerNodes.Count - 1 + nextPage.Count);
                for (var i = 0; i < ContainerNodes.Count; i++)
                {
                    if (!ReferenceEquals(ContainerNodes[i], loadMoreNode))
                    {
                        updated.Add(ContainerNodes[i]);
                    }
                }

                updated.AddRange(nextPage);
                ContainerNodes.ReplaceAll(updated);
                return true;
            }

            var parent = loadMoreNode.Parent;
            var nextChildren = _containerIndex.LoadNextFolderPage(parent, loadMoreNode.NextPageStartIndex, HandleContainerFolderExpanded);
            if (nextChildren.Count == 0)
            {
                return false;
            }

            var mergedChildren = new List<ContainerTreeNode>(parent.Children.Count - 1 + nextChildren.Count);
            for (var i = 0; i < parent.Children.Count; i++)
            {
                if (!ReferenceEquals(parent.Children[i], loadMoreNode))
                {
                    mergedChildren.Add(parent.Children[i]);
                }
            }

            mergedChildren.AddRange(nextChildren);
            parent.ReplaceChildren(mergedChildren);
            return true;
        }

        public async Task<ContainerSearchResult> FilterContainersAsync(
            string? query,
            int maxLeafMatches = SceneHierarchyBuilderService.DefaultContainerSearchCap)
        {
            var indexSnapshot = _containerIndex;
            var trimmed = query?.Trim() ?? string.Empty;

            CancelActiveContainerFilter();

            if (indexSnapshot == null || indexSnapshot.TotalAssetCount == 0)
            {
                return new ContainerSearchResult();
            }

            if (trimmed.Length == 0)
            {
                ClearContainerFilter();
                return new ContainerSearchResult
                {
                    RootNodes = ContainerNodes,
                    TotalMatchedAssets = indexSnapshot.TotalAssetCount,
                    DisplayedMatchedAssets = indexSnapshot.TotalAssetCount
                };
            }

            var cts = new CancellationTokenSource();
            long requestId;
            lock (_stateLock)
            {
                _containerFilterCts = cts;
                requestId = ++_containerFilterRequestId;
            }

            _dispatchToUi(() =>
            {
                IsFilteringContainers = true;
                SearchStatusText = $"Filtering containers for '{trimmed}'...";
            });

            ContainerSearchResult result;
            try
            {
                var token = cts.Token;
                result = await Task.Run(() => indexSnapshot.Search(trimmed, maxLeafMatches, token), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return new ContainerSearchResult();
            }

            bool isCurrent;
            lock (_stateLock)
            {
                isCurrent = requestId == _containerFilterRequestId && ReferenceEquals(indexSnapshot, _containerIndex);
                if (ReferenceEquals(_containerFilterCts, cts))
                {
                    _containerFilterCts = null;
                }
                cts.Dispose();
            }

            if (!isCurrent)
            {
                return new ContainerSearchResult();
            }

            _dispatchToUi(() =>
            {
                IsFilteringContainers = false;
                if (result.RootNodes.Count == 0)
                {
                    if (ContainerNodes.Count > 0)
                    {
                        ContainerNodes.Clear();
                    }
                    SearchStatusText = $"No container match for '{trimmed}'.";
                    ModeSummaryText = $"0 matches for '{trimmed}'";
                }
                else
                {
                    ContainerNodes.ReplaceAll(result.RootNodes);
                    SearchStatusText = result.WasTruncated
                        ? string.Create(CultureInfo.InvariantCulture, $"Showing {result.DisplayedMatchedAssets:N0} of {result.TotalMatchedAssets:N0} container matches for '{trimmed}'.")
                        : string.Create(CultureInfo.InvariantCulture, $"Found {result.TotalMatchedAssets:N0} container {(result.TotalMatchedAssets == 1 ? "match" : "matches")} for '{trimmed}'.");
                    ModeSummaryText = result.WasTruncated
                        ? string.Create(CultureInfo.InvariantCulture, $"{result.DisplayedMatchedAssets:N0}/{result.TotalMatchedAssets:N0} matches")
                        : string.Create(CultureInfo.InvariantCulture, $"{result.TotalMatchedAssets:N0} {(result.TotalMatchedAssets == 1 ? "match" : "matches")}");
                }
            });

            return result;
        }

        public void ClearContainerFilter()
        {
            CancelActiveContainerFilter();
            var indexSnapshot = _containerIndex;
            _dispatchToUi(() =>
            {
                IsFilteringContainers = false;
                if (indexSnapshot == null || indexSnapshot.RootFolderCount == 0)
                {
                    if (ContainerNodes.Count > 0)
                    {
                        ContainerNodes.Clear();
                    }
                }
                else
                {
                    ContainerNodes.ReplaceAll(indexSnapshot.CreateRootNodes(0, HandleContainerFolderExpanded));
                }

                UpdateModeSummaryText();
            });
        }

        public void UpdateModeSummaryText()
        {
            if (IsContainersMode)
            {
                if (_containerIndex == null || _containerIndex.TotalAssetCount == 0)
                {
                    ModeSummaryText = string.Empty;
                    return;
                }

                var rootLabel = _containerIndex.RootFolderCount == 1 ? "root" : "roots";
                var assetLabel = _containerIndex.TotalAssetCount == 1 ? "asset" : "assets";
                ModeSummaryText = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_containerIndex.RootFolderCount:N0} {rootLabel} • {_containerIndex.TotalAssetCount:N0} {assetLabel}");
                return;
            }

            if (_rootNodesSource.Count == 0)
            {
                ModeSummaryText = _isLazyMode ? "No scenes detected — try Containers" : string.Empty;
                return;
            }

            if (_isLazyMode)
            {
                var sceneLabel = _rootNodesSource.Count == 1 ? "scene" : "scenes";
                ModeSummaryText = _realGeometrySceneCount >= 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{_rootNodesSource.Count:N0} {sceneLabel} ({_realGeometrySceneCount:N0} 3D) • Lazy")
                    : string.Create(CultureInfo.InvariantCulture, $"{_rootNodesSource.Count:N0} {sceneLabel} • Lazy");
                return;
            }

            var rootWord = _rootNodesSource.Count == 1 ? "root" : "roots";
            ModeSummaryText = string.Create(CultureInfo.InvariantCulture, $"{_rootNodesSource.Count:N0} {rootWord}");
        }

        /// <summary>
        /// Synchronizes the backing root list reference without rebuilding <see cref="Nodes"/> if unchanged.
        /// Used by <see cref="MainWindow"/> when <c>sceneTreeNodes</c> is managed by code-behind.
        /// </summary>
        internal void BindRootNodesReference(IReadOnlyList<GameObjectNode>? roots)
        {
            var effectiveRoots = roots ?? Array.Empty<GameObjectNode>();
            lock (_stateLock)
            {
                var sameReference = ReferenceEquals(_rootNodesSource, effectiveRoots);
                var sameCount = _totalRootCount == effectiveRoots.Count;
                _rootNodesSource = effectiveRoots;
                TotalRootCount = effectiveRoots.Count;

                if (!sameReference || !sameCount)
                {
                    _appliedMatches = null;
                    _activeOrCachedQuery = null;
                    _nextMatchZeroBasedIndex = 0;
                    MatchCount = 0;
                    CurrentMatchIndex = 0;
                    CurrentMatchNode = null;
                }
            }
        }

        public bool TryGetCachedMatches(string query, out GameObjectNode[] matches)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                matches = Array.Empty<GameObjectNode>();
                return false;
            }

            return _searchSession.TryGetCached(_rootNodesSource, query.Trim(), out matches);
        }

        public Task<GameObjectNode?> SearchNextAsync(string? query = null)
        {
            return SearchAndCycleAsync(query, stepDirection: +1);
        }

        public Task<GameObjectNode?> SearchPreviousAsync(string? query = null)
        {
            return SearchAndCycleAsync(query, stepDirection: -1);
        }

        private async Task<GameObjectNode?> SearchAndCycleAsync(string? query, int stepDirection)
        {
            string trimmedQuery;
            IReadOnlyList<GameObjectNode> rootsSnapshot;

            lock (_stateLock)
            {
                _wasLastSearchSuperseded = false;
                if (query != null)
                {
                    var normalized = query;
                    trimmedQuery = normalized.Trim();
                    if (!string.Equals(trimmedQuery, _activeOrCachedQuery, StringComparison.Ordinal))
                    {
                        _appliedMatches = null;
                        _nextMatchZeroBasedIndex = 0;
                        MatchCount = 0;
                        CurrentMatchIndex = 0;
                        CurrentMatchNode = null;
                    }

                    _activeOrCachedQuery = trimmedQuery;
                    _suppressQueryInvalidation = true;
                    try
                    {
                        SearchQuery = normalized;
                    }
                    finally
                    {
                        _suppressQueryInvalidation = false;
                    }
                }
                else
                {
                    trimmedQuery = _searchQuery.Trim();
                    _activeOrCachedQuery = trimmedQuery;
                }

                rootsSnapshot = _rootNodesSource;
                TotalRootCount = rootsSnapshot.Count;
            }

            if (string.IsNullOrEmpty(trimmedQuery))
            {
                return null;
            }

            if (!_searchSession.TryGetCached(rootsSnapshot, trimmedQuery, out var matches))
            {
                _dispatchToUi(() =>
                {
                    IsSearching = true;
                    SearchStatusText = $"Searching scene hierarchy for '{trimmedQuery}'...";
                });

                GameObjectNode[]? searched;
                try
                {
                    searched = await _searchSession.SearchAsync(rootsSnapshot, trimmedQuery).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _dispatchToUi(() =>
                    {
                        IsSearching = _searchSession.IsSearching;
                        SearchStatusText = $"Scene hierarchy search failed: {ex.Message}";
                    });
                    return null;
                }

                var isStillCurrent = false;
                lock (_stateLock)
                {
                    isStillCurrent = searched != null
                        && ReferenceEquals(rootsSnapshot, _rootNodesSource)
                        && rootsSnapshot.Count == _rootNodesSource.Count
                        && string.Equals(trimmedQuery, _searchQuery.Trim(), StringComparison.Ordinal);

                    if (!isStillCurrent)
                    {
                        _wasLastSearchSuperseded = true;
                        if (!_searchSession.IsSearching)
                        {
                            IsSearching = false;
                        }
                        return null;
                    }
                }

                matches = searched!;
            }

            GameObjectNode? selectedMatch = null;
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    IsSearching = false;
                    ApplyMatchesLocked(matches, trimmedQuery);

                    if (matches.Length == 0)
                    {
                        SearchStatusText = $"No scene hierarchy match for '{trimmedQuery}'.";
                        selectedMatch = null;
                        return;
                    }

                    int targetIndex;
                    if (stepDirection >= 0)
                    {
                        if (_nextMatchZeroBasedIndex >= matches.Length || _nextMatchZeroBasedIndex < 0)
                        {
                            _nextMatchZeroBasedIndex = 0;
                        }

                        targetIndex = _nextMatchZeroBasedIndex;
                        _nextMatchZeroBasedIndex = targetIndex + 1;
                    }
                    else
                    {
                        if (_currentMatchIndex <= 0)
                        {
                            targetIndex = matches.Length - 1;
                        }
                        else
                        {
                            targetIndex = (_currentMatchIndex - 2 + matches.Length) % matches.Length;
                        }

                        _nextMatchZeroBasedIndex = (targetIndex + 1) % matches.Length;
                    }

                    var node = matches[targetIndex];
                    node.ExpandAncestors();
                    CurrentMatchNode = node;
                    SelectedNode = node;
                    CurrentMatchIndex = targetIndex + 1;
                    SearchStatusText = $"Scene hierarchy match {CurrentMatchIndex}/{matches.Length}: {node.Name}";
                    selectedMatch = node;
                }
            });

            return selectedMatch;
        }

        private void ApplyMatchesLocked(GameObjectNode[] matches, string query)
        {
            _activeOrCachedQuery = query;
            if (ReferenceEquals(matches, _appliedMatches))
            {
                return;
            }

            _appliedMatches = matches;
            _nextMatchZeroBasedIndex = 0;
            MatchCount = matches.Length;
            if (matches.Length == 0)
            {
                CurrentMatchIndex = 0;
                CurrentMatchNode = null;
            }
        }

        public bool SelectNode(GameObjectNode? node, bool expandAncestors = true)
        {
            if (node == null)
            {
                SelectedNode = null;
                return false;
            }

            if (expandAncestors)
            {
                node.ExpandAncestors();
            }

            SelectedNode = node;
            return true;
        }

        public bool TryNavigateToAssetNode(AssetItem? item)
        {
            if (item?.TreeNode == null)
            {
                SearchStatusText = "Selected asset has no scene hierarchy node.";
                return false;
            }

            item.TreeNode.ExpandAncestors();
            SelectedNode = item.TreeNode;
            return true;
        }

        public void ExpandSelectedSubtree()
        {
            var root = _selectedNode;
            if (root == null || root.IsPlaceholder)
            {
                return;
            }

            if (root.IsLazySceneRoot && !root.IsSceneLoaded)
            {
                root.IsExpanded = true;
                return;
            }

            const int maxNodesToExpand = 2000;
            var expanded = 0;
            var stack = new Stack<GameObjectNode>();
            stack.Push(root);
            while (stack.Count > 0 && expanded < maxNodesToExpand)
            {
                var current = stack.Pop();
                if (current.IsPlaceholder)
                {
                    continue;
                }

                if (current.ChildCount > 0)
                {
                    current.IsExpanded = true;
                    expanded++;
                    var children = current.Children;
                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        var child = children[i];
                        if (!child.IsPlaceholder && (!child.IsLazySceneRoot || child.IsSceneLoaded))
                        {
                            stack.Push(child);
                        }
                    }
                }
            }
        }

        public void CollapseSelectedSubtree()
        {
            var root = _selectedNode;
            if (root == null || root.IsPlaceholder)
            {
                return;
            }

            var stack = new Stack<GameObjectNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                current.IsExpanded = false;
                var children = current.Children;
                for (var i = 0; i < children.Count; i++)
                {
                    stack.Push(children[i]);
                }
            }
        }

        public void InvalidateSearchCache()
        {
            lock (_stateLock)
            {
                InvalidateSearchStateLocked(clearStatus: false);
            }
        }

        public void ClearSearch()
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    _suppressQueryInvalidation = true;
                    try
                    {
                        SearchQuery = string.Empty;
                    }
                    finally
                    {
                        _suppressQueryInvalidation = false;
                    }

                    InvalidateSearchStateLocked(clearStatus: true);
                }

                if (IsContainersMode && _containerIndex != null)
                {
                    ClearContainerFilter();
                }
            });
        }

        public void Reset()
        {
            CancelActiveContainerFilter();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    _suppressQueryInvalidation = true;
                    try
                    {
                        SearchQuery = string.Empty;
                    }
                    finally
                    {
                        _suppressQueryInvalidation = false;
                    }

                    _rootNodesSource = Array.Empty<GameObjectNode>();
                    _containerIndex = null;
                    _realGeometrySceneCount = 0;
                    InvalidateSearchStateLocked(clearStatus: true);
                    if (Nodes.Count > 0)
                    {
                        Nodes.Clear();
                    }

                    if (ContainerNodes.Count > 0)
                    {
                        ContainerNodes.Clear();
                    }

                    if (SetProperty(ref _activeMode, SceneHierarchyViewMode.Scenes, nameof(ActiveMode)))
                    {
                        OnPropertyChanged(nameof(IsScenesMode));
                        OnPropertyChanged(nameof(IsContainersMode));
                        OnPropertyChanged(nameof(SearchPlaceholderText));
                    }

                    IsLazyMode = false;
                    CanRefresh = false;
                    IsFilteringContainers = false;
                    ModeSummaryText = string.Empty;
                    TotalRootCount = 0;
                    SelectedNode = null;
                    SelectedContainerNode = null;
                }
            });
        }

        private Task ExecuteRefreshAsync()
        {
            return RefreshHandlerAsync != null ? RefreshHandlerAsync() : Task.CompletedTask;
        }

        private Task ExecuteLoadSelectedSceneAsync()
        {
            var target = _selectedNode;
            if (target == null || !target.IsLazySceneRoot || target.IsSceneLoaded || target.IsSceneLoading)
            {
                return Task.CompletedTask;
            }

            if (ExpandLazySceneHandlerAsync != null)
            {
                target.IsExpanded = true;
                return ExpandLazySceneHandlerAsync(target);
            }

            target.IsExpanded = true;
            return Task.CompletedTask;
        }

        private Task ExecuteExportSelectedSceneAsync()
        {
            return ExportSelectedSceneHandlerAsync != null
                ? ExportSelectedSceneHandlerAsync(_selectedNode)
                : Task.CompletedTask;
        }

        private Task ExecuteShowSelectedContainerInAssetListAsync()
        {
            return ShowContainerAssetInListHandlerAsync != null
                ? ShowContainerAssetInListHandlerAsync(_selectedContainerNode)
                : Task.CompletedTask;
        }

        private Task ExecuteCopySelectedContainerPathAsync()
        {
            return CopyContainerPathHandlerAsync != null
                ? CopyContainerPathHandlerAsync(_selectedContainerNode)
                : Task.CompletedTask;
        }

        private Task ExecuteExportSelectedContainerAssetAsync()
        {
            return ExportContainerAssetHandlerAsync != null
                ? ExportContainerAssetHandlerAsync(_selectedContainerNode)
                : Task.CompletedTask;
        }

        private void CancelActiveContainerFilter()
        {
            lock (_stateLock)
            {
                _containerFilterRequestId++;
                var cts = _containerFilterCts;
                _containerFilterCts = null;
                if (cts != null)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        private void InvalidateSearchStateLocked(bool clearStatus)
        {
            _searchSession.Invalidate();
            _appliedMatches = null;
            _activeOrCachedQuery = null;
            _nextMatchZeroBasedIndex = 0;
            _wasLastSearchSuperseded = false;
            IsSearching = false;
            MatchCount = 0;
            CurrentMatchIndex = 0;
            CurrentMatchNode = null;
            if (clearStatus)
            {
                SearchStatusText = string.Empty;
            }
        }
    }
}
