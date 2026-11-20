using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.ViewModels
{
    public readonly record struct AssetSelectionKey(AssetHandleKey HandleKey, string? FallbackUniqueId)
    {
        public bool IsEmpty => HandleKey.IsEmpty && string.IsNullOrEmpty(FallbackUniqueId);
    }

    public sealed class AssetListViewModel : ViewModelBase
    {
        public const int ArenaViewportLruCapacity = 512;
        public const int ArenaInlineSortedMergeThreshold = 8192;

        private readonly Action<Action> _dispatchToUi;
        private readonly object _uiApplyLock = new();

        private string _searchText = string.Empty;
        private string _classSearchText = string.Empty;
        private string? _sortColumn;
        private bool _sortAscending = true;
        private bool _isArenaMode;
        private int _totalAssetCount;
        private int _visibleAssetCount;
        private int _selectedAssetCount;
        private string _statusSummary = "Ready";
        private bool _isAllTypesSelected = true;
        private AssetClassItem? _classFilterOverride;
        private bool _isRefreshing;
        private bool _isRefreshingClasses;

        private readonly HashSet<ClassIDType> _selectedClassTypes = new();
        private HashSet<ClassIDType> _exportableAssetTypes = new();
        private Dictionary<AssetHandleKey, AssetItem> _lazyAssetItemsByHandleId = new();
        private HashSet<AssetHandleKey> _exportableAssetHandleIds = new();

        private List<AssetItem> _eagerExportableAssets = new();
        private List<AssetItem> _eagerVisibleAssets = new();
        private List<AssetClassItem> _allAssetClasses = new();

        private ProjectIndex? _projectIndex;
        private AssetIndexArena? _arena;
        private Func<int, AssetItem>? _customArenaMaterializer;
        private readonly List<int> _exportableArenaIndices = new();
        private ulong[] _exportableArenaBitset = Array.Empty<ulong>();
        private int[] _visibleArenaIndices = Array.Empty<int>();
        private int _visibleArenaCount;
        private bool _visibleArenaIsIdentity;
        private bool _visibleArenaIsStrictlyIncreasing;
        private Dictionary<int, int>? _visibleArenaRowByArenaIndex;

        private readonly List<AssetItem> _selectedItems = new();
        private readonly HashSet<AssetSelectionKey> _selectedKeys = new();
        private AssetItem? _primarySelectedItem;
        private AssetSelectionKey _primarySelectedKey;

        private CancellationTokenSource? _activeQueryCts;
        private int _queryVersion;
        private CancellationTokenSource? _arenaCoalescedSortCts;
        private int _arenaCoalescedSortVersion;

        public AssetListViewModel(Action<Action>? dispatchToUi = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            VisibleItems = new VirtualAssetList();
            VisibleItems.ReplaceBacking(_eagerVisibleAssets);
            AssetClasses = new BulkObservableCollection<AssetClassItem>();
        }

        public VirtualAssetList VisibleItems { get; }

        public BulkObservableCollection<AssetClassItem> AssetClasses { get; }

        public IReadOnlyList<AssetClassItem> AllAssetClasses => _allAssetClasses;

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value ?? string.Empty);
        }

        public string ClassSearchText
        {
            get => _classSearchText;
            set
            {
                if (SetProperty(ref _classSearchText, value ?? string.Empty))
                {
                    FilterAssetClasses();
                }
            }
        }

        public string? SortColumn
        {
            get => _sortColumn;
            set => SetProperty(ref _sortColumn, string.IsNullOrWhiteSpace(value) ? null : value);
        }

        public bool SortAscending
        {
            get => _sortAscending;
            set
            {
                if (SetProperty(ref _sortAscending, value))
                {
                    OnPropertyChanged(nameof(SortDescending));
                }
            }
        }

        public bool SortDescending
        {
            get => !_sortAscending;
            set => SortAscending = !value;
        }

        public bool IsArenaMode
        {
            get => _isArenaMode;
            private set => SetProperty(ref _isArenaMode, value);
        }

        public int TotalAssetCount
        {
            get => _totalAssetCount;
            private set
            {
                if (SetProperty(ref _totalAssetCount, value))
                {
                    OnPropertyChanged(nameof(ExportableAssetCount));
                }
            }
        }

        public int ExportableAssetCount => _totalAssetCount;

        public int VisibleAssetCount
        {
            get => _visibleAssetCount;
            private set => SetProperty(ref _visibleAssetCount, value);
        }

        public int SelectedAssetCount
        {
            get => _selectedAssetCount;
            private set => SetProperty(ref _selectedAssetCount, value);
        }

        public string StatusSummary
        {
            get => _statusSummary;
            private set => SetProperty(ref _statusSummary, value ?? string.Empty);
        }

        public bool IsAllTypesSelected
        {
            get => _isAllTypesSelected;
            private set => SetProperty(ref _isAllTypesSelected, value);
        }

        public IReadOnlySet<ClassIDType> SelectedClassTypes => _selectedClassTypes;

        public IReadOnlySet<ClassIDType> AvailableClassTypes => _exportableAssetTypes;

        public AssetClassItem? ClassFilterOverride
        {
            get => _classFilterOverride;
            set
            {
                if (SetProperty(ref _classFilterOverride, value))
                {
                    OnPropertyChanged(nameof(HasClassFilterOverride));
                    OnPropertyChanged(nameof(ClassFilterButtonText));
                }
            }
        }

        public bool HasClassFilterOverride => _classFilterOverride != null;

        public string ClassFilterButtonText => _classFilterOverride != null
            ? $"Clear Class Filter ({_classFilterOverride.Name} v{_classFilterOverride.UnityVersion})"
            : "Clear Class Filter";

        public bool IsRefreshing
        {
            get => _isRefreshing;
            internal set => SetProperty(ref _isRefreshing, value);
        }

        public bool IsRefreshingClasses
        {
            get => _isRefreshingClasses;
            internal set => SetProperty(ref _isRefreshingClasses, value);
        }

        public IReadOnlyList<AssetItem> SelectedItems => _selectedItems;

        public IReadOnlySet<AssetSelectionKey> SelectedKeys => _selectedKeys;

        public AssetItem? PrimarySelectedItem => _primarySelectedItem;

        public AssetSelectionKey PrimarySelectedKey => _primarySelectedKey;

        internal List<AssetItem> EagerExportableAssets
        {
            get => _eagerExportableAssets;
            set
            {
                _eagerExportableAssets = value ?? new List<AssetItem>();
                UpdateCountsAndSummary();
            }
        }

        internal List<AssetItem> EagerVisibleAssets => _eagerVisibleAssets;

        internal List<int> ExportableArenaIndices => _exportableArenaIndices;

        internal int VisibleArenaCount => _visibleArenaCount;

        internal bool HasPendingArenaCoalescedSort => _arenaCoalescedSortCts != null;

        internal Dictionary<AssetHandleKey, AssetItem> LazyAssetItemsByHandleId
        {
            get => _lazyAssetItemsByHandleId;
            set => _lazyAssetItemsByHandleId = value ?? new Dictionary<AssetHandleKey, AssetItem>();
        }

        internal HashSet<AssetHandleKey> ExportableAssetHandleIds
        {
            get => _exportableAssetHandleIds;
            set => _exportableAssetHandleIds = value ?? new HashSet<AssetHandleKey>();
        }

        internal HashSet<ClassIDType> ExportableAssetTypes
        {
            get => _exportableAssetTypes;
            set
            {
                _exportableAssetTypes = value ?? new HashSet<ClassIDType>();
                OnPropertyChanged(nameof(AvailableClassTypes));
            }
        }

        public AssetIndexArena? Arena => _arena ?? _projectIndex?.Arena;

        public ProjectIndex? ProjectIndex => _projectIndex;

        public static bool TryGetAssetSelectionKey(AssetItem? item, out AssetSelectionKey key)
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

        public static string? GetAssetUniqueKey(AssetItem? item)
        {
            if (item == null)
            {
                return null;
            }

            return item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
        }

        public void SelectAllClassTypes()
        {
            lock (_uiApplyLock)
            {
                _selectedClassTypes.Clear();
                IsAllTypesSelected = true;
                OnPropertyChanged(nameof(SelectedClassTypes));
            }
        }

        public void SetSelectedClassTypes(IEnumerable<ClassIDType>? types)
        {
            lock (_uiApplyLock)
            {
                _selectedClassTypes.Clear();
                if (types == null)
                {
                    IsAllTypesSelected = true;
                }
                else
                {
                    foreach (var t in types)
                    {
                        _selectedClassTypes.Add(t);
                    }
                    IsAllTypesSelected = false;
                }

                OnPropertyChanged(nameof(SelectedClassTypes));
            }
        }

        public void SetClassTypeSelected(ClassIDType type, bool isSelected)
        {
            lock (_uiApplyLock)
            {
                if (isSelected)
                {
                    _selectedClassTypes.Add(type);
                    IsAllTypesSelected = false;
                }
                else
                {
                    _selectedClassTypes.Remove(type);
                    if (_selectedClassTypes.Count == 0)
                    {
                        IsAllTypesSelected = true;
                    }
                }

                OnPropertyChanged(nameof(SelectedClassTypes));
            }
        }

        public void ToggleClassType(ClassIDType type)
        {
            lock (_uiApplyLock)
            {
                var currentlySelected = !_isAllTypesSelected && _selectedClassTypes.Contains(type);
                SetClassTypeSelected(type, !currentlySelected);
            }
        }

        public void SetClassFilterOverride(AssetClassItem? classFilter)
        {
            ClassFilterOverride = classFilter;
        }

        public void ClearClassFilterOverride()
        {
            ClassFilterOverride = null;
        }

        public void SetSelection(IEnumerable<AssetItem>? items, AssetItem? primaryItem = null)
        {
            lock (_uiApplyLock)
            {
                _selectedItems.Clear();
                _selectedKeys.Clear();
                _primarySelectedItem = null;
                _primarySelectedKey = default;

                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item == null)
                        {
                            continue;
                        }

                        if (TryGetAssetSelectionKey(item, out var key))
                        {
                            if (_selectedKeys.Add(key))
                            {
                                _selectedItems.Add(item);
                            }
                        }
                        else if (!_selectedItems.Contains(item))
                        {
                            _selectedItems.Add(item);
                        }
                    }
                }

                var effectivePrimary = primaryItem ?? (_selectedItems.Count > 0 ? _selectedItems[0] : null);
                if (effectivePrimary != null)
                {
                    _primarySelectedItem = effectivePrimary;
                    if (TryGetAssetSelectionKey(effectivePrimary, out var pKey))
                    {
                        _primarySelectedKey = pKey;
                        if (_selectedKeys.Add(pKey))
                        {
                            _selectedItems.Add(effectivePrimary);
                        }
                    }
                }

                SelectedAssetCount = _selectedItems.Count;
                OnPropertyChanged(nameof(SelectedItems));
                OnPropertyChanged(nameof(SelectedKeys));
                OnPropertyChanged(nameof(PrimarySelectedItem));
                OnPropertyChanged(nameof(PrimarySelectedKey));
            }
        }

        public void SelectItem(AssetItem? item)
        {
            if (item == null)
            {
                ClearSelection();
                return;
            }

            SetSelection(new[] { item }, item);
        }

        public void ClearSelection()
        {
            SetSelection(null, null);
        }

        public void SetEagerAssets(
            IReadOnlyList<AssetItem>? exportableAssets,
            IReadOnlyList<AssetClassItem>? classItems = null,
            Dictionary<AssetHandleKey, AssetItem>? lazyItemsByHandleId = null,
            HashSet<AssetHandleKey>? exportableHandleIds = null,
            HashSet<ClassIDType>? exportableTypes = null,
            bool applyFilterImmediately = true)
        {
            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
                ResetArenaStateCore();
                IsArenaMode = false;

                _eagerExportableAssets = exportableAssets is { Count: > 0 }
                    ? exportableAssets.Where(static x => x != null).ToList()
                    : new List<AssetItem>();

                if (lazyItemsByHandleId != null && exportableHandleIds != null && exportableTypes != null)
                {
                    _lazyAssetItemsByHandleId = lazyItemsByHandleId;
                    _exportableAssetHandleIds = exportableHandleIds;
                    _exportableAssetTypes = exportableTypes;
                }
                else
                {
                    SyncExportableAssetHandleIdsCore();
                }

                OnPropertyChanged(nameof(AvailableClassTypes));

                var effectiveClasses = classItems ?? BuildEagerAssetClassItems(_eagerExportableAssets);
                SetAssetClassesCore(effectiveClasses);

                if (applyFilterImmediately)
                {
                    var selectedTypes = GetEffectiveSelectedClassTypesSnapshot();
                    var filtered = AssetListQuery.Filter(_eagerExportableAssets, _classFilterOverride, selectedTypes, _searchText?.Trim(), CancellationToken.None);
                    var sorted = AssetListQuery.SortInPlace(filtered, _sortColumn, SortDescending);
                    ReplaceVisibleEagerAssetsOwnedCore(sorted);
                }
                else
                {
                    UpdateCountsAndSummary();
                }
            }
        }

        public void ReplaceVisibleEagerAssets(IReadOnlyList<AssetItem>? items)
        {
            lock (_uiApplyLock)
            {
                IsArenaMode = false;
                var list = items is { Count: > 0 }
                    ? items.Where(static x => x != null).ToList()
                    : new List<AssetItem>();
                ReplaceVisibleEagerAssetsOwnedCore(list);
            }
        }

        public void ReplaceVisibleEagerAssetsOwned(List<AssetItem> ownedItems)
        {
            ArgumentNullException.ThrowIfNull(ownedItems);
            lock (_uiApplyLock)
            {
                IsArenaMode = false;
                ReplaceVisibleEagerAssetsOwnedCore(ownedItems);
            }
        }

        private void ReplaceVisibleEagerAssetsOwnedCore(List<AssetItem> ownedItems)
        {
            var capturedKeys = new HashSet<AssetSelectionKey>(_selectedKeys);
            var capturedPrimaryKey = _primarySelectedKey;

            _eagerVisibleAssets = ownedItems;
            VisibleItems.ReplaceBacking(_eagerVisibleAssets);
            RestoreEagerSelectionCore(capturedKeys, capturedPrimaryKey);
            UpdateCountsAndSummary(useFormattedThousands: false);
        }

        public void SyncExportableAssetHandleIds()
        {
            lock (_uiApplyLock)
            {
                SyncExportableAssetHandleIdsCore();
                OnPropertyChanged(nameof(AvailableClassTypes));
            }
        }

        private void SyncExportableAssetHandleIdsCore()
        {
            _lazyAssetItemsByHandleId.Clear();
            _exportableAssetHandleIds.Clear();
            _exportableAssetTypes.Clear();
            foreach (var item in _eagerExportableAssets)
            {
                if (item.Handle != null && item.Handle.TryGetKey(out var handleKey))
                {
                    _lazyAssetItemsByHandleId[handleKey] = item;
                    _exportableAssetHandleIds.Add(handleKey);
                }
                _exportableAssetTypes.Add(item.Type);
            }
        }

        public void BeginArenaMode(
            ProjectIndex? projectIndex = null,
            AssetIndexArena? arena = null,
            Func<int, AssetItem>? materializer = null)
        {
            lock (_uiApplyLock)
            {
                CancelPendingArenaCoalescedSort();
                _projectIndex = projectIndex;
                _arena = arena ?? projectIndex?.Arena;
                _customArenaMaterializer = materializer;
                IsArenaMode = true;
                UpdateCountsAndSummary(useFormattedThousands: true);
            }
        }

        public void SetArenaAssets(
            ProjectIndex projectIndex,
            IReadOnlyList<int>? exportableIndices = null,
            ulong[]? exportableBitset = null,
            HashSet<ClassIDType>? exportableTypes = null,
            IReadOnlyList<AssetClassItem>? classItems = null,
            Func<int, AssetItem>? materializer = null,
            bool applyFilterImmediately = true)
        {
            ArgumentNullException.ThrowIfNull(projectIndex);
            SetArenaAssetsCore(
                projectIndex,
                projectIndex.Arena,
                exportableIndices,
                exportableBitset,
                exportableTypes,
                classItems,
                materializer,
                applyFilterImmediately);
        }

        public void SetArenaAssets(
            AssetIndexArena arena,
            IReadOnlyList<int>? exportableIndices = null,
            Func<int, AssetItem>? materializer = null,
            IReadOnlyList<AssetClassItem>? classItems = null,
            bool applyFilterImmediately = true)
        {
            ArgumentNullException.ThrowIfNull(arena);
            SetArenaAssetsCore(
                projectIndex: null,
                arena: arena,
                exportableIndices: exportableIndices,
                exportableBitset: null,
                exportableTypes: null,
                classItems: classItems,
                materializer: materializer,
                applyFilterImmediately: applyFilterImmediately);
        }

        private void SetArenaAssetsCore(
            ProjectIndex? projectIndex,
            AssetIndexArena arena,
            IReadOnlyList<int>? exportableIndices,
            ulong[]? exportableBitset,
            HashSet<ClassIDType>? exportableTypes,
            IReadOnlyList<AssetClassItem>? classItems,
            Func<int, AssetItem>? materializer,
            bool applyFilterImmediately)
        {
            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
                _projectIndex = projectIndex;
                _arena = arena;
                _customArenaMaterializer = materializer;
                IsArenaMode = true;

                _eagerExportableAssets.Clear();
                _eagerVisibleAssets.Clear();
                _lazyAssetItemsByHandleId.Clear();
                _exportableAssetHandleIds.Clear();

                _exportableArenaIndices.Clear();
                var arenaCount = arena.Count;
                if (exportableIndices != null)
                {
                    _exportableArenaIndices.EnsureCapacity(exportableIndices.Count);
                    for (var i = 0; i < exportableIndices.Count; i++)
                    {
                        var idx = exportableIndices[i];
                        if ((uint)idx < (uint)arenaCount)
                        {
                            _exportableArenaIndices.Add(idx);
                        }
                    }
                }
                else
                {
                    _exportableArenaIndices.EnsureCapacity(arenaCount);
                    for (var i = 0; i < arenaCount; i++)
                    {
                        _exportableArenaIndices.Add(i);
                    }
                }

                if (exportableBitset != null)
                {
                    _exportableArenaBitset = exportableBitset;
                }
                else
                {
                    _exportableArenaBitset = new ulong[(arenaCount + 63) >> 6];
                    for (var i = 0; i < _exportableArenaIndices.Count; i++)
                    {
                        var idx = _exportableArenaIndices[i];
                        _exportableArenaBitset[idx >> 6] |= 1UL << (idx & 63);
                    }
                }

                if (exportableTypes != null)
                {
                    _exportableAssetTypes = exportableTypes;
                }
                else
                {
                    _exportableAssetTypes = new HashSet<ClassIDType>();
                    for (var i = 0; i < _exportableArenaIndices.Count; i++)
                    {
                        _exportableAssetTypes.Add(arena.GetType(_exportableArenaIndices[i]));
                    }
                }
                OnPropertyChanged(nameof(AvailableClassTypes));

                var effectiveClasses = classItems ?? BuildArenaAssetClassItemsForIndices(arena, _exportableArenaIndices);
                SetAssetClassesCore(effectiveClasses);

                if (applyFilterImmediately)
                {
                    var selectedTypes = GetEffectiveSelectedClassTypesSnapshot();
                    var sourceSnapshot = _exportableArenaIndices.ToArray();
                    var visible = AssetListQuery.FilterArena(
                        arena,
                        sourceSnapshot,
                        sourceSnapshot.Length,
                        _classFilterOverride,
                        selectedTypes,
                        _searchText?.Trim(),
                        CancellationToken.None);
                    AssetListQuery.SortArenaInPlace(arena, visible, visible.Length, _sortColumn, SortDescending);

                    var isSorted = !string.IsNullOrEmpty(_sortColumn);
                    var isIdentity = !isSorted && IsIdentityArenaIndices(visible, visible.Length);
                    var isStrictlyIncreasing = !isSorted && (isIdentity || IsStrictlyIncreasingIndices(visible, visible.Length));
                    ApplyVisibleArenaIndicesCore(visible, visible.Length, isStrictlyIncreasing, isIdentity);
                }
                else
                {
                    UpdateCountsAndSummary(useFormattedThousands: true);
                }
            }
        }

        public void ApplyWarmArenaState(
            ProjectIndex projectIndex,
            IReadOnlyList<int> exportableIndices,
            ulong[]? exportableBitset,
            HashSet<ClassIDType>? exportableTypes,
            IReadOnlyList<AssetClassItem>? classItems,
            int[] visibleIndices,
            bool isStrictlyIncreasing,
            bool isIdentity)
        {
            ArgumentNullException.ThrowIfNull(projectIndex);
            ArgumentNullException.ThrowIfNull(exportableIndices);
            ArgumentNullException.ThrowIfNull(visibleIndices);

            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
                _projectIndex = projectIndex;
                _arena = projectIndex.Arena;
                IsArenaMode = true;

                _exportableArenaIndices.Clear();
                _exportableArenaIndices.AddRange(exportableIndices);
                _exportableArenaBitset = exportableBitset ?? Array.Empty<ulong>();
                _exportableAssetTypes = exportableTypes ?? new HashSet<ClassIDType>();
                OnPropertyChanged(nameof(AvailableClassTypes));

                SetAssetClassesCore(classItems ?? new List<AssetClassItem>());
                ApplyVisibleArenaIndicesCore(visibleIndices, visibleIndices.Length, isStrictlyIncreasing, isIdentity);
            }
        }

        public void ApplyVisibleArenaIndices(int[] indices, int count, bool isStrictlyIncreasing, bool isIdentity)
        {
            ArgumentNullException.ThrowIfNull(indices);
            lock (_uiApplyLock)
            {
                ApplyVisibleArenaIndicesCore(indices, count, isStrictlyIncreasing, isIdentity);
            }
        }

        private void ApplyVisibleArenaIndicesCore(int[] indices, int count, bool isStrictlyIncreasing, bool isIdentity)
        {
            var capturedKeys = new HashSet<AssetSelectionKey>(_selectedKeys);
            var capturedPrimaryKey = _primarySelectedKey;

            _visibleArenaIndices = indices;
            _visibleArenaCount = count;
            _visibleArenaIsStrictlyIncreasing = isStrictlyIncreasing;
            _visibleArenaIsIdentity = isIdentity;
            _visibleArenaRowByArenaIndex = null;

            VisibleItems.ReplaceWithProvider(
                count,
                row => MaterializeArenaAssetItem(_visibleArenaIndices[row]),
                lruCapacity: ArenaViewportLruCapacity,
                indexLocator: LocateVisibleArenaRowIndex);

            RestoreArenaSelectionCore(capturedKeys, capturedPrimaryKey);
            UpdateCountsAndSummary(useFormattedThousands: true);
        }

        public bool TryMarkExportableArenaIndex(int arenaIndex)
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

        public int[] SnapshotExportableArenaIndices()
        {
            lock (_uiApplyLock)
            {
                return _exportableArenaIndices.Count == 0
                    ? Array.Empty<int>()
                    : _exportableArenaIndices.ToArray();
            }
        }

        public int[] SnapshotVisibleArenaIndices()
        {
            lock (_uiApplyLock)
            {
                var count = _visibleArenaCount;
                if (count <= 0)
                {
                    return Array.Empty<int>();
                }

                var copy = new int[count];
                Array.Copy(_visibleArenaIndices, copy, count);
                return copy;
            }
        }

        public AssetItem MaterializeArenaAssetItem(int arenaIndex)
        {
            if (_customArenaMaterializer != null)
            {
                return _customArenaMaterializer(arenaIndex);
            }

            var arena = Arena ?? throw new InvalidOperationException("Arena is not configured on AssetListViewModel.");
            var handle = arena.MaterializeHandle(arenaIndex);
            return new AssetItem(handle, arenaIndex);
        }

        public List<AssetItem> MaterializeArenaAssetItems(IReadOnlyList<int> indices)
        {
            ArgumentNullException.ThrowIfNull(indices);
            var arena = Arena;
            if (arena == null)
            {
                return new List<AssetItem>();
            }

            var count = indices.Count;
            var list = new List<AssetItem>(count);
            var arenaCount = arena.Count;
            for (var i = 0; i < count; i++)
            {
                var arenaIndex = indices[i];
                if ((uint)arenaIndex < (uint)arenaCount)
                {
                    list.Add(MaterializeArenaAssetItem(arenaIndex));
                }
            }

            return list;
        }

        public List<AssetItem> GetExportableAssetsSnapshot()
        {
            lock (_uiApplyLock)
            {
                return _isArenaMode
                    ? MaterializeArenaAssetItems(_exportableArenaIndices)
                    : _eagerExportableAssets;
            }
        }

        public IReadOnlyList<AssetItem> GetVisibleAssetsView()
        {
            return _isArenaMode ? VisibleItems : _eagerVisibleAssets;
        }

        public int LocateVisibleArenaRowIndex(AssetItem item)
        {
            if (item?.Handle == null)
            {
                return -1;
            }

            var arenaIndex = item.Handle.ArenaIndex;
            if (arenaIndex < 0 && item.Handle.TryGetKey(out var key))
            {
                if (_projectIndex == null || !_projectIndex.TryGetIndex(key, out arenaIndex))
                {
                    return -1;
                }
            }

            return LocateVisibleArenaRowByArenaIndex(arenaIndex);
        }

        public int LocateVisibleArenaRowByArenaIndex(int arenaIndex)
        {
            if (arenaIndex < 0 || _visibleArenaCount <= 0)
            {
                return -1;
            }

            if (_visibleArenaIsIdentity)
            {
                return (uint)arenaIndex < (uint)_visibleArenaCount ? arenaIndex : -1;
            }

            if (_visibleArenaIsStrictlyIncreasing)
            {
                var found = Array.BinarySearch(_visibleArenaIndices, 0, _visibleArenaCount, arenaIndex);
                return found >= 0 ? found : -1;
            }

            var map = _visibleArenaRowByArenaIndex;
            if (map == null)
            {
                map = new Dictionary<int, int>(_visibleArenaCount);
                for (var i = 0; i < _visibleArenaCount; i++)
                {
                    map[_visibleArenaIndices[i]] = i;
                }
                _visibleArenaRowByArenaIndex = map;
            }

            return map.TryGetValue(arenaIndex, out var row) ? row : -1;
        }

        public static bool IsIdentityArenaIndices(int[] indices, int count)
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

        public static bool IsStrictlyIncreasingIndices(int[] indices, int count)
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

        public void AppendFilteredEagerAssetsToVisible(IReadOnlyList<AssetItem>? newItems, bool addToExportable = false)
        {
            if (newItems == null || newItems.Count == 0)
            {
                return;
            }

            lock (_uiApplyLock)
            {
                IsArenaMode = false;
                var filterText = _searchText?.Trim();
                var textFilter = AssetListQuery.TextFilter.Create(filterText);
                var classFilter = _classFilterOverride;
                var selectedTypes = GetEffectiveSelectedClassTypesSnapshot();

                var matchingBatch = new List<AssetItem>(newItems.Count);
                var typesChanged = false;
                for (var i = 0; i < newItems.Count; i++)
                {
                    var x = newItems[i];
                    if (x == null)
                    {
                        continue;
                    }

                    if (addToExportable)
                    {
                        _eagerExportableAssets.Add(x);
                        if (x.Handle != null && x.Handle.TryGetKey(out var handleKey))
                        {
                            _lazyAssetItemsByHandleId[handleKey] = x;
                            _exportableAssetHandleIds.Add(handleKey);
                        }
                        if (_exportableAssetTypes.Add(x.Type))
                        {
                            typesChanged = true;
                        }
                    }

                    if (classFilter != null)
                    {
                        if (!AssetListQuery.MatchesClassFilter(x, classFilter))
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

                if (typesChanged)
                {
                    OnPropertyChanged(nameof(AvailableClassTypes));
                }

                if (addToExportable)
                {
                    SetAssetClassesCore(BuildEagerAssetClassItems(_eagerExportableAssets));
                }

                if (matchingBatch.Count > 0)
                {
                    VisibleItems.BeginUpdate();
                    try
                    {
                        if (!VisibleItems.IsBackedBy(_eagerVisibleAssets))
                        {
                            VisibleItems.ReplaceBacking(_eagerVisibleAssets);
                        }

                        AssetListQuery.MergeSortedBatch(_eagerVisibleAssets, matchingBatch, _sortColumn, SortDescending);
                    }
                    finally
                    {
                        VisibleItems.EndUpdate();
                    }
                }

                UpdateCountsAndSummary(useFormattedThousands: false);
            }
        }

        public int AppendLazyArenaIndices(
            ReadOnlySpan<int> pendingIndices,
            bool displayAllChecked,
            Func<ClassIDType, bool> isLazyExportableType,
            Action<string, string>? rememberOriginPaths,
            out bool filterTypesChanged)
        {
            filterTypesChanged = false;
            var arena = Arena;
            if (arena == null || pendingIndices.IsEmpty)
            {
                return 0;
            }

            lock (_uiApplyLock)
            {
                IsArenaMode = true;
                var arenaCount = arena.Count;
                var newExportableIndices = new List<int>(pendingIndices.Length);
                string? lastRememberedOriginalPath = null;
                string? lastRememberedSerializedFile = null;

                for (var i = 0; i < pendingIndices.Length; i++)
                {
                    var idx = pendingIndices[i];
                    if ((uint)idx >= (uint)arenaCount)
                    {
                        continue;
                    }

                    if (rememberOriginPaths != null)
                    {
                        var origin = arena.GetOrigin(idx);
                        var originalPath = origin.OriginalPath;
                        var serializedFileName = origin.SerializedFileName;
                        if (!string.IsNullOrWhiteSpace(originalPath)
                            && (!string.Equals(originalPath, lastRememberedOriginalPath, StringComparison.Ordinal)
                                || !string.Equals(serializedFileName, lastRememberedSerializedFile, StringComparison.Ordinal)))
                        {
                            rememberOriginPaths(serializedFileName, originalPath);
                            lastRememberedOriginalPath = originalPath;
                            lastRememberedSerializedFile = serializedFileName;
                        }
                    }

                    var type = arena.GetType(idx);
                    if (!displayAllChecked && !isLazyExportableType(type))
                    {
                        continue;
                    }

                    if (!TryMarkExportableArenaIndex(idx))
                    {
                        continue;
                    }

                    _exportableArenaIndices.Add(idx);
                    newExportableIndices.Add(idx);
                    if (_exportableAssetTypes.Add(type))
                    {
                        filterTypesChanged = true;
                    }
                }

                if (filterTypesChanged)
                {
                    OnPropertyChanged(nameof(AvailableClassTypes));
                }

                AppendFilteredArenaIndicesToVisibleCore(newExportableIndices, updateClasses: false);
                return newExportableIndices.Count;
            }
        }

        public void AppendFilteredArenaIndicesToVisible(IReadOnlyList<int>? newArenaIndices, bool addToExportable = false)
        {
            if (newArenaIndices == null || newArenaIndices.Count == 0)
            {
                return;
            }

            var arena = Arena;
            if (arena == null)
            {
                return;
            }

            lock (_uiApplyLock)
            {
                IsArenaMode = true;
                IReadOnlyList<int> effectiveNewIndices = newArenaIndices;
                if (addToExportable)
                {
                    var added = new List<int>(newArenaIndices.Count);
                    var arenaCount = arena.Count;
                    var typesChanged = false;
                    for (var i = 0; i < newArenaIndices.Count; i++)
                    {
                        var idx = newArenaIndices[i];
                        if ((uint)idx >= (uint)arenaCount || !TryMarkExportableArenaIndex(idx))
                        {
                            continue;
                        }

                        _exportableArenaIndices.Add(idx);
                        added.Add(idx);
                        if (_exportableAssetTypes.Add(arena.GetType(idx)))
                        {
                            typesChanged = true;
                        }
                    }

                    if (typesChanged)
                    {
                        OnPropertyChanged(nameof(AvailableClassTypes));
                    }

                    effectiveNewIndices = added;
                }

                AppendFilteredArenaIndicesToVisibleCore(effectiveNewIndices, updateClasses: addToExportable);
            }
        }

        private void AppendFilteredArenaIndicesToVisibleCore(IReadOnlyList<int> newArenaIndices, bool updateClasses)
        {
            var arena = Arena;
            if (arena == null)
            {
                return;
            }

            if (updateClasses)
            {
                SetAssetClassesCore(BuildArenaAssetClassItemsForIndices(arena, _exportableArenaIndices));
            }

            if (newArenaIndices.Count == 0)
            {
                UpdateCountsAndSummary(useFormattedThousands: true);
                return;
            }

            var filterText = _searchText?.Trim();
            var textFilter = AssetListQuery.TextFilter.Create(filterText);
            var classFilter = _classFilterOverride;
            var selectedTypes = GetEffectiveSelectedClassTypesSnapshot();

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
                UpdateCountsAndSummary(useFormattedThousands: true);
                return;
            }

            var sortMember = _sortColumn;
            var sortDescending = SortDescending;

            if (string.IsNullOrEmpty(sortMember))
            {
                var oldCount = _visibleArenaCount;
                var newCount = oldCount + matchingIndices.Count;
                if (_visibleArenaIndices.Length < newCount)
                {
                    var nextCap = Math.Max(newCount, _visibleArenaIndices.Length == 0 ? 1024 : _visibleArenaIndices.Length * 2);
                    Array.Resize(ref _visibleArenaIndices, nextCap);
                }

                var strictlyIncreasing = oldCount == 0 || _visibleArenaIsStrictlyIncreasing;
                var identity = oldCount == 0 || _visibleArenaIsIdentity;
                var prev = oldCount > 0 ? _visibleArenaIndices[oldCount - 1] : -1;

                for (var i = 0; i < matchingIndices.Count; i++)
                {
                    var idx = matchingIndices[i];
                    var pos = oldCount + i;
                    _visibleArenaIndices[pos] = idx;
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

                ApplyVisibleArenaIndicesCore(_visibleArenaIndices, newCount, strictlyIncreasing, identity);
            }
            else if (_visibleArenaCount < ArenaInlineSortedMergeThreshold)
            {
                var incomingArray = matchingIndices.ToArray();
                var merged = AssetListQuery.MergeSortedArenaBatch(
                    arena,
                    _visibleArenaIndices,
                    _visibleArenaCount,
                    incomingArray,
                    incomingArray.Length,
                    sortMember,
                    sortDescending);
                ApplyVisibleArenaIndicesCore(merged, merged.Length, isStrictlyIncreasing: false, isIdentity: false);
            }
            else
            {
                var oldCount = _visibleArenaCount;
                var newCount = oldCount + matchingIndices.Count;
                if (_visibleArenaIndices.Length < newCount)
                {
                    var nextCap = Math.Max(newCount, _visibleArenaIndices.Length * 2);
                    Array.Resize(ref _visibleArenaIndices, nextCap);
                }

                matchingIndices.CopyTo(_visibleArenaIndices, oldCount);
                ApplyVisibleArenaIndicesCore(_visibleArenaIndices, newCount, isStrictlyIncreasing: false, isIdentity: false);
                QueueCoalescedArenaSort();
            }
        }

        private void QueueCoalescedArenaSort()
        {
            var arena = Arena;
            if (!_isArenaMode || arena == null || string.IsNullOrEmpty(_sortColumn) || _visibleArenaCount <= 1)
            {
                return;
            }

            CancelPendingArenaCoalescedSort();
            var cts = new CancellationTokenSource();
            _arenaCoalescedSortCts = cts;
            var token = cts.Token;
            var sortVersion = _arenaCoalescedSortVersion;
            var queryVersion = Volatile.Read(ref _queryVersion);
            var sortMember = _sortColumn;
            var sortDescending = SortDescending;
            var expectedCount = _visibleArenaCount;
            var snapshot = new int[expectedCount];
            Array.Copy(_visibleArenaIndices, snapshot, expectedCount);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(60, token);
                    token.ThrowIfCancellationRequested();
                    AssetListQuery.SortArenaInPlace(arena, snapshot, snapshot.Length, sortMember, sortDescending);
                    token.ThrowIfCancellationRequested();

                    _dispatchToUi(() =>
                    {
                        lock (_uiApplyLock)
                        {
                            if (token.IsCancellationRequested
                                || sortVersion != _arenaCoalescedSortVersion
                                || queryVersion != Volatile.Read(ref _queryVersion)
                                || !_isArenaMode
                                || _visibleArenaCount != expectedCount
                                || !string.Equals(_sortColumn, sortMember, StringComparison.Ordinal)
                                || SortDescending != sortDescending)
                            {
                                return;
                            }

                            IsRefreshing = true;
                            try
                            {
                                ApplyVisibleArenaIndicesCore(snapshot, snapshot.Length, isStrictlyIncreasing: false, isIdentity: false);
                            }
                            finally
                            {
                                IsRefreshing = false;
                            }
                        }
                    });
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

        public Task<bool> FilterAsync(string? searchText, CancellationToken cancellationToken = default)
        {
            SearchText = searchText ?? string.Empty;
            return FilterAsync(cancellationToken);
        }

        public async Task<bool> FilterAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource localCts;
            CancellationToken localToken;
            int queryVersion;
            bool isArena;
            AssetIndexArena? arena;
            int[]? arenaSourceSnapshot = null;
            AssetItem[]? eagerSourceSnapshot = null;
            string? filterText;
            AssetClassItem? classFilter;
            HashSet<ClassIDType>? selectedTypes;
            string? sortMember;
            bool sortDescending;

            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
                queryVersion = Volatile.Read(ref _queryVersion);
                localCts = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();
                _activeQueryCts = localCts;
                localToken = localCts.Token;

                isArena = _isArenaMode;
                arena = Arena;
                if (isArena)
                {
                    arenaSourceSnapshot = _exportableArenaIndices.ToArray();
                }
                else
                {
                    eagerSourceSnapshot = _eagerExportableAssets.ToArray();
                }

                filterText = _searchText?.Trim();
                classFilter = _classFilterOverride;
                selectedTypes = GetEffectiveSelectedClassTypesSnapshot();
                sortMember = _sortColumn;
                sortDescending = SortDescending;
            }

            try
            {
                if (isArena)
                {
                    if (arena == null || arenaSourceSnapshot == null)
                    {
                        return false;
                    }

                    var arenaResult = await Task.Run(() =>
                    {
                        localToken.ThrowIfCancellationRequested();
                        var matches = AssetListQuery.FilterArena(
                            arena,
                            arenaSourceSnapshot,
                            arenaSourceSnapshot.Length,
                            classFilter,
                            selectedTypes,
                            filterText,
                            localToken);
                        localToken.ThrowIfCancellationRequested();
                        AssetListQuery.SortArenaInPlace(arena, matches, matches.Length, sortMember, sortDescending);
                        localToken.ThrowIfCancellationRequested();
                        return matches;
                    }, localToken);

                    var applied = false;
                    _dispatchToUi(() =>
                    {
                        lock (_uiApplyLock)
                        {
                            if (localToken.IsCancellationRequested || queryVersion != Volatile.Read(ref _queryVersion) || !_isArenaMode)
                            {
                                return;
                            }

                            var isSorted = !string.IsNullOrEmpty(sortMember);
                            var isIdentity = !isSorted && IsIdentityArenaIndices(arenaResult, arenaResult.Length);
                            var isStrictlyIncreasing = !isSorted && (isIdentity || IsStrictlyIncreasingIndices(arenaResult, arenaResult.Length));

                            IsRefreshing = true;
                            try
                            {
                                ApplyVisibleArenaIndicesCore(arenaResult, arenaResult.Length, isStrictlyIncreasing, isIdentity);
                                applied = true;
                            }
                            finally
                            {
                                IsRefreshing = false;
                            }
                        }
                    });

                    return applied;
                }
                else
                {
                    if (eagerSourceSnapshot == null)
                    {
                        return false;
                    }

                    var result = await Task.Run(() =>
                    {
                        localToken.ThrowIfCancellationRequested();
                        var matches = AssetListQuery.Filter(eagerSourceSnapshot, classFilter, selectedTypes, filterText, localToken);
                        localToken.ThrowIfCancellationRequested();
                        var sorted = AssetListQuery.SortInPlace(matches, sortMember, sortDescending);
                        localToken.ThrowIfCancellationRequested();
                        return sorted;
                    }, localToken);

                    var applied = false;
                    _dispatchToUi(() =>
                    {
                        lock (_uiApplyLock)
                        {
                            if (localToken.IsCancellationRequested || queryVersion != Volatile.Read(ref _queryVersion) || _isArenaMode)
                            {
                                return;
                            }

                            IsRefreshing = true;
                            try
                            {
                                ReplaceVisibleEagerAssetsOwnedCore(result);
                                applied = true;
                            }
                            finally
                            {
                                IsRefreshing = false;
                            }
                        }
                    });

                    return applied;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public Task<bool> SortByColumnAsync(string? sortColumn, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sortColumn))
            {
                return Task.FromResult(false);
            }

            lock (_uiApplyLock)
            {
                if (string.Equals(_sortColumn, sortColumn, StringComparison.Ordinal))
                {
                    SortAscending = !_sortAscending;
                }
                else
                {
                    SortColumn = sortColumn;
                    SortAscending = true;
                }
            }

            return ApplySortAsync(cancellationToken);
        }

        public Task<bool> SortByColumnAsync(string? sortColumn, bool ascending, CancellationToken cancellationToken = default)
        {
            lock (_uiApplyLock)
            {
                SortColumn = sortColumn;
                SortAscending = ascending;
            }

            return ApplySortAsync(cancellationToken);
        }

        public async Task<bool> ApplySortAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource localCts;
            CancellationToken localToken;
            int queryVersion;
            bool isArena;
            AssetIndexArena? arena;
            int[]? currentArenaIndices = null;
            List<AssetItem>? currentEagerAssets = null;
            string? sortMember;
            bool sortDescending;

            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
                queryVersion = Volatile.Read(ref _queryVersion);
                localCts = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();
                _activeQueryCts = localCts;
                localToken = localCts.Token;

                isArena = _isArenaMode;
                arena = Arena;
                sortMember = _sortColumn;
                sortDescending = SortDescending;

                if (isArena)
                {
                    currentArenaIndices = new int[_visibleArenaCount];
                    if (_visibleArenaCount > 0)
                    {
                        Array.Copy(_visibleArenaIndices, currentArenaIndices, _visibleArenaCount);
                    }
                }
                else
                {
                    currentEagerAssets = new List<AssetItem>(_eagerVisibleAssets);
                }
            }

            try
            {
                if (isArena)
                {
                    if (arena == null || currentArenaIndices == null)
                    {
                        return false;
                    }

                    await Task.Run(() =>
                    {
                        localToken.ThrowIfCancellationRequested();
                        AssetListQuery.SortArenaInPlace(arena, currentArenaIndices, currentArenaIndices.Length, sortMember, sortDescending);
                        localToken.ThrowIfCancellationRequested();
                    }, localToken);

                    var applied = false;
                    _dispatchToUi(() =>
                    {
                        lock (_uiApplyLock)
                        {
                            if (localToken.IsCancellationRequested || queryVersion != Volatile.Read(ref _queryVersion) || !_isArenaMode)
                            {
                                return;
                            }

                            var isSorted = !string.IsNullOrEmpty(sortMember);
                            var isIdentity = !isSorted && IsIdentityArenaIndices(currentArenaIndices, currentArenaIndices.Length);
                            var isStrictlyIncreasing = !isSorted && (isIdentity || IsStrictlyIncreasingIndices(currentArenaIndices, currentArenaIndices.Length));

                            IsRefreshing = true;
                            try
                            {
                                ApplyVisibleArenaIndicesCore(currentArenaIndices, currentArenaIndices.Length, isStrictlyIncreasing, isIdentity);
                                applied = true;
                            }
                            finally
                            {
                                IsRefreshing = false;
                            }
                        }
                    });

                    return applied;
                }
                else
                {
                    if (currentEagerAssets == null)
                    {
                        return false;
                    }

                    var sorted = await Task.Run(() =>
                    {
                        localToken.ThrowIfCancellationRequested();
                        var res = AssetListQuery.SortInPlace(currentEagerAssets, sortMember, sortDescending);
                        localToken.ThrowIfCancellationRequested();
                        return res;
                    }, localToken);

                    var applied = false;
                    _dispatchToUi(() =>
                    {
                        lock (_uiApplyLock)
                        {
                            if (localToken.IsCancellationRequested || queryVersion != Volatile.Read(ref _queryVersion) || _isArenaMode)
                            {
                                return;
                            }

                            IsRefreshing = true;
                            try
                            {
                                ReplaceVisibleEagerAssetsOwnedCore(sorted);
                                applied = true;
                            }
                            finally
                            {
                                IsRefreshing = false;
                            }
                        }
                    });

                    return applied;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void SetAssetClasses(IReadOnlyList<AssetClassItem>? classItems)
        {
            lock (_uiApplyLock)
            {
                SetAssetClassesCore(classItems);
            }
        }

        private void SetAssetClassesCore(IReadOnlyList<AssetClassItem>? classItems)
        {
            _allAssetClasses = classItems is { Count: > 0 }
                ? classItems.Where(static x => x != null).ToList()
                : new List<AssetClassItem>();
            OnPropertyChanged(nameof(AllAssetClasses));
            FilterAssetClassesCore();
        }

        public void FilterAssetClasses()
        {
            lock (_uiApplyLock)
            {
                FilterAssetClassesCore();
            }
        }

        private void FilterAssetClassesCore()
        {
            var filter = _classSearchText?.Trim();
            IReadOnlyList<AssetClassItem> filtered;
            if (string.IsNullOrEmpty(filter))
            {
                filtered = _allAssetClasses;
            }
            else
            {
                var list = new List<AssetClassItem>();
                for (var i = 0; i < _allAssetClasses.Count; i++)
                {
                    var x = _allAssetClasses[i];
                    if (MatchesClassSearchFilter(x, filter))
                    {
                        list.Add(x);
                    }
                }
                filtered = list;
            }

            IsRefreshingClasses = true;
            try
            {
                AssetClasses.ReplaceAll(filtered);
            }
            finally
            {
                IsRefreshingClasses = false;
            }
        }

        public void UpdateAssetClassesIncremental(IReadOnlyList<AssetClassItem>? updatedClassItems)
        {
            if (updatedClassItems == null)
            {
                return;
            }

            lock (_uiApplyLock)
            {
                _allAssetClasses = updatedClassItems.Where(static x => x != null).ToList();
                OnPropertyChanged(nameof(AllAssetClasses));

                var updatedLookup = new Dictionary<(int ClassID, string Name, string Namespace, string Assembly, string UnityVersion, string SourceFile, string SourceKind), AssetClassItem>();
                foreach (var item in _allAssetClasses)
                {
                    var key = (item.ClassID, item.Name, item.Namespace, item.Assembly, item.UnityVersion, item.SourceFile, item.SourceKind);
                    updatedLookup[key] = item;
                }

                foreach (var existing in AssetClasses)
                {
                    var key = (existing.ClassID, existing.Name, existing.Namespace, existing.Assembly, existing.UnityVersion, existing.SourceFile, existing.SourceKind);
                    if (updatedLookup.TryGetValue(key, out var updated))
                    {
                        existing.CopyFrom(updated);
                        updatedLookup.Remove(key);
                    }
                }

                var filter = _classSearchText?.Trim();
                foreach (var newItem in updatedLookup.Values)
                {
                    if (!string.IsNullOrEmpty(filter) && !MatchesClassSearchFilter(newItem, filter))
                    {
                        continue;
                    }

                    AssetClasses.Add(newItem);
                }
            }
        }

        private static bool MatchesClassSearchFilter(AssetClassItem x, string filter)
        {
            return x.ClassID.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Namespace.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Assembly.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.SourceKind.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        public static List<AssetClassItem> BuildEagerAssetClassItems(IReadOnlyList<AssetItem>? items)
        {
            if (items == null || items.Count == 0)
            {
                return new List<AssetClassItem>();
            }

            var countsByTypeAndVersion = new Dictionary<(ClassIDType Type, string UnityVersion), int>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                var version = item.SourceFile?.unityVersion ?? string.Empty;
                var key = (item.Type, version);
                ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(countsByTypeAndVersion, key, out _);
                count++;
            }

            var classItems = new List<AssetClassItem>(countsByTypeAndVersion.Count);
            foreach (var kvp in countsByTypeAndVersion)
            {
                var classId = (int)kvp.Key.Type;
                var name = AssetItem.GetCachedTypeString(kvp.Key.Type);
                classItems.Add(new AssetClassItem
                {
                    ClassID = classId,
                    Name = name,
                    Namespace = string.Empty,
                    Assembly = string.Empty,
                    UnityVersion = kvp.Key.UnityVersion,
                    SourceFile = "Loaded Assets",
                    ObjectCount = kvp.Value,
                    SourceKind = "Native",
                    SerializedType = new SerializedType
                    {
                        classID = classId,
                        m_KlassName = name,
                    },
                });
            }

            classItems.Sort(static (a, b) =>
            {
                var cmp = string.Compare(a.UnityVersion, b.UnityVersion, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                cmp = a.ClassID.CompareTo(b.ClassID);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return classItems;
        }

        private static List<AssetClassItem> BuildArenaAssetClassItemsForIndices(AssetIndexArena arena, List<int> exportableIndices)
        {
            if (exportableIndices.Count == arena.Count)
            {
                return AssetListQuery.BuildArenaAssetClassItems(arena);
            }

            if (exportableIndices.Count == 0)
            {
                return new List<AssetClassItem>();
            }

            var countsByType = new Dictionary<ClassIDType, int>(64);
            var arenaCount = arena.Count;
            for (var i = 0; i < exportableIndices.Count; i++)
            {
                var idx = exportableIndices[i];
                if ((uint)idx >= (uint)arenaCount)
                {
                    continue;
                }

                var type = arena.GetType(idx);
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
                        m_KlassName = name,
                    },
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

        private HashSet<ClassIDType>? GetEffectiveSelectedClassTypesSnapshot()
        {
            if (_isAllTypesSelected)
            {
                return null;
            }

            return new HashSet<ClassIDType>(_selectedClassTypes);
        }

        private void RestoreEagerSelectionCore(HashSet<AssetSelectionKey> selectedKeys, AssetSelectionKey primaryKey)
        {
            if (selectedKeys.Count == 0)
            {
                return;
            }

            _selectedItems.Clear();
            _selectedKeys.Clear();
            _primarySelectedItem = null;
            _primarySelectedKey = default;

            for (var i = 0; i < _eagerVisibleAssets.Count; i++)
            {
                var item = _eagerVisibleAssets[i];
                if (TryGetAssetSelectionKey(item, out var key) && selectedKeys.Contains(key))
                {
                    if (_selectedKeys.Add(key))
                    {
                        _selectedItems.Add(item);
                    }

                    if (!primaryKey.IsEmpty && key == primaryKey)
                    {
                        _primarySelectedItem = item;
                        _primarySelectedKey = key;
                    }
                }
            }

            if (_primarySelectedItem == null && _selectedItems.Count > 0)
            {
                _primarySelectedItem = _selectedItems[0];
                TryGetAssetSelectionKey(_primarySelectedItem, out _primarySelectedKey);
            }

            SelectedAssetCount = _selectedItems.Count;
            OnPropertyChanged(nameof(SelectedItems));
            OnPropertyChanged(nameof(SelectedKeys));
            OnPropertyChanged(nameof(PrimarySelectedItem));
            OnPropertyChanged(nameof(PrimarySelectedKey));
        }

        private void RestoreArenaSelectionCore(HashSet<AssetSelectionKey> selectedKeys, AssetSelectionKey primaryKey)
        {
            if (selectedKeys.Count == 0)
            {
                return;
            }

            _selectedItems.Clear();
            _selectedKeys.Clear();
            _primarySelectedItem = null;
            _primarySelectedKey = default;

            foreach (var key in selectedKeys)
            {
                var arenaIdx = ResolveArenaIndexForSelectionKey(key);
                if (arenaIdx < 0)
                {
                    continue;
                }

                var visibleRow = LocateVisibleArenaRowByArenaIndex(arenaIdx);
                if ((uint)visibleRow < (uint)VisibleItems.Count)
                {
                    var item = VisibleItems[visibleRow];
                    if (_selectedKeys.Add(key))
                    {
                        _selectedItems.Add(item);
                    }

                    if (!primaryKey.IsEmpty && key == primaryKey)
                    {
                        _primarySelectedItem = item;
                        _primarySelectedKey = key;
                    }
                }
            }

            if (_primarySelectedItem == null && _selectedItems.Count > 0)
            {
                _primarySelectedItem = _selectedItems[0];
                TryGetAssetSelectionKey(_primarySelectedItem, out _primarySelectedKey);
            }

            SelectedAssetCount = _selectedItems.Count;
            OnPropertyChanged(nameof(SelectedItems));
            OnPropertyChanged(nameof(SelectedKeys));
            OnPropertyChanged(nameof(PrimarySelectedItem));
            OnPropertyChanged(nameof(PrimarySelectedKey));
        }

        private int ResolveArenaIndexForSelectionKey(AssetSelectionKey key)
        {
            if (_projectIndex != null)
            {
                if (!key.HandleKey.IsEmpty && _projectIndex.TryGetIndex(key.HandleKey, out var idx))
                {
                    return idx;
                }

                if (!string.IsNullOrEmpty(key.FallbackUniqueId) && _projectIndex.TryGetIndexByUniqueId(key.FallbackUniqueId, out idx))
                {
                    return idx;
                }
            }

            var arena = Arena;
            if (arena != null)
            {
                for (var i = 0; i < _visibleArenaCount; i++)
                {
                    var candidateIdx = _visibleArenaIndices[i];
                    var candidateKey = arena.GetKey(candidateIdx);
                    if (!key.HandleKey.IsEmpty && candidateKey == key.HandleKey)
                    {
                        return candidateIdx;
                    }

                    if (!string.IsNullOrEmpty(key.FallbackUniqueId)
                        && (string.Equals(arena.GetUniqueId(candidateIdx), key.FallbackUniqueId, StringComparison.Ordinal)
                            || string.Equals(" #" + candidateIdx.ToString(CultureInfo.InvariantCulture), key.FallbackUniqueId, StringComparison.Ordinal)))
                    {
                        return candidateIdx;
                    }
                }
            }

            return -1;
        }

        private void UpdateCountsAndSummary(bool useFormattedThousands = true)
        {
            var total = _isArenaMode ? _exportableArenaIndices.Count : _eagerExportableAssets.Count;
            var visible = _isArenaMode ? _visibleArenaCount : _eagerVisibleAssets.Count;
            TotalAssetCount = total;
            VisibleAssetCount = visible;
            StatusSummary = useFormattedThousands || _isArenaMode
                ? $"Showing {visible:N0} assets"
                : $"Showing {visible} assets";
        }

        public void CancelPendingOperations()
        {
            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
            }
        }

        public void CancelPendingArenaCoalescedSort()
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

        private void CancelPendingOperationsCore()
        {
            Interlocked.Increment(ref _queryVersion);
            CancelPendingArenaCoalescedSort();

            var queryCts = _activeQueryCts;
            _activeQueryCts = null;
            if (queryCts != null)
            {
                try
                {
                    queryCts.Cancel();
                }
                catch
                {
                }
                queryCts.Dispose();
            }
        }

        public void ResetArenaState()
        {
            lock (_uiApplyLock)
            {
                ResetArenaStateCore();
                IsArenaMode = false;
                UpdateCountsAndSummary();
            }
        }

        private void ResetArenaStateCore()
        {
            CancelPendingArenaCoalescedSort();
            _projectIndex = null;
            _arena = null;
            _customArenaMaterializer = null;
            _exportableArenaIndices.Clear();
            _exportableArenaIndices.TrimExcess();
            _exportableArenaBitset = Array.Empty<ulong>();
            _visibleArenaIndices = Array.Empty<int>();
            _visibleArenaCount = 0;
            _visibleArenaIsIdentity = false;
            _visibleArenaIsStrictlyIncreasing = false;
            _visibleArenaRowByArenaIndex = null;
        }

        public void Clear()
        {
            Reset();
        }

        public void Reset()
        {
            lock (_uiApplyLock)
            {
                CancelPendingOperationsCore();
                ResetArenaStateCore();
                IsArenaMode = false;

                _eagerExportableAssets.Clear();
                _eagerVisibleAssets.Clear();
                VisibleItems.Clear();
                VisibleItems.ReplaceBacking(_eagerVisibleAssets);

                _lazyAssetItemsByHandleId.Clear();
                _exportableAssetHandleIds.Clear();
                _exportableAssetTypes.Clear();
                OnPropertyChanged(nameof(AvailableClassTypes));

                _allAssetClasses.Clear();
                OnPropertyChanged(nameof(AllAssetClasses));
                AssetClasses.Clear();

                _selectedClassTypes.Clear();
                IsAllTypesSelected = true;
                OnPropertyChanged(nameof(SelectedClassTypes));

                ClassFilterOverride = null;
                SearchText = string.Empty;
                ClassSearchText = string.Empty;
                SortColumn = null;
                SortAscending = true;

                _selectedItems.Clear();
                _selectedKeys.Clear();
                _primarySelectedItem = null;
                _primarySelectedKey = default;
                SelectedAssetCount = 0;
                OnPropertyChanged(nameof(SelectedItems));
                OnPropertyChanged(nameof(SelectedKeys));
                OnPropertyChanged(nameof(PrimarySelectedItem));
                OnPropertyChanged(nameof(PrimarySelectedKey));

                IsRefreshing = false;
                IsRefreshingClasses = false;
                TotalAssetCount = 0;
                VisibleAssetCount = 0;
                StatusSummary = "Ready";
            }
        }
    }
}
