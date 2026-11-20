using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

        private string _searchQuery = string.Empty;
        private bool _isSearching;
        private int _matchCount;
        private int _currentMatchIndex;
        private bool _hasMatches;
        private string _searchStatusText = string.Empty;
        private int _totalRootCount;
        private GameObjectNode? _selectedNode;
        private GameObjectNode? _currentMatchNode;
        private int _nextMatchZeroBasedIndex;
        private bool _suppressQueryInvalidation;
        private bool _wasLastSearchSuperseded;

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
        }

        public BulkObservableCollection<GameObjectNode> Nodes { get; }

        public IReadOnlyList<GameObjectNode> RootNodes => _rootNodesSource;

        public IReadOnlyList<GameObjectNode> Matches => _appliedMatches ?? Array.Empty<GameObjectNode>();

        internal GameObjectNode[]? AppliedMatchesArray => _appliedMatches;

        internal SceneTreeSearchSession SearchSession => _searchSession;

        public int TraversalCount => _searchSession.TraversalCount;

        internal bool WasLastSearchSuperseded => _wasLastSearchSuperseded;

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
            set => SetProperty(ref _selectedNode, value);
        }

        public GameObjectNode? CurrentMatchNode
        {
            get => _currentMatchNode;
            private set => SetProperty(ref _currentMatchNode, value);
        }

        public void SetRootNodes(IReadOnlyList<GameObjectNode>? roots)
        {
            var effectiveRoots = roots ?? Array.Empty<GameObjectNode>();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    _rootNodesSource = effectiveRoots;
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
                }
            });
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
            });
        }

        public void Reset()
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

                    _rootNodesSource = Array.Empty<GameObjectNode>();
                    InvalidateSearchStateLocked(clearStatus: true);
                    if (Nodes.Count > 0)
                    {
                        Nodes.Clear();
                    }

                    TotalRootCount = 0;
                    SelectedNode = null;
                }
            });
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
