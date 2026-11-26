using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum GlobalSearchDialogMode
    {
        GlobalAssetSearch = 0,
        SemanticUsageGraph = 1
    }

    public sealed class GlobalSearchResultItem
    {
        public string AssetUniqueId { get; init; } = string.Empty;
        public ClassIDType AssetType { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Container { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string MatchReason { get; init; } = string.Empty;
        public long PathId { get; init; }
        public long ByteSize { get; init; }
        public long MatchByteOffset { get; init; } = -1;

        public string PathIdText => PathId != 0
            ? PathId.ToString(CultureInfo.InvariantCulture)
            : "-";

        public bool IsNavigable => !string.IsNullOrWhiteSpace(AssetUniqueId) || PathId != 0;
    }

    public sealed class GlobalSearchDialogViewModel : ViewModelBase, IDisposable
    {
        private static readonly string[] TypeFilterChoices =
        {
            "All Types",
            "Texture2D",
            "Sprite",
            "Material",
            "Shader",
            "Mesh",
            "GameObject",
            "AnimationClip",
            "Avatar",
            "Animator",
            "AudioClip",
            "TextAsset",
            "MonoBehaviour",
            "MonoScript",
            "Font",
            "VideoClip",
            "TerrainData"
        };

        private static readonly string[] GraphQueryKindChoices =
        {
            "Auto (by Asset Type)",
            "Shader -> Materials",
            "Texture -> Materials & Models",
            "Material -> Meshes & Models",
            "Avatar / Rig -> AnimationClips"
        };

        private readonly ProjectIndex? _projectIndex;
        private readonly AssetsManager? _assetsManager;
        private readonly SQLiteProjectIndexCache? _sqliteCache;
        private readonly string? _folderPath;
        private readonly string? _signature;
        private readonly AsyncRelayCommand _executeQueryCommand;
        private readonly RelayCommand _clearCommand;
        private readonly RelayCommand _goToSelectedCommand;
        private readonly AsyncRelayCommand _inspectSelectedUsageCommand;
        private readonly RelayCommand _switchToSearchModeCommand;
        private readonly RelayCommand _switchToGraphModeCommand;
        private readonly RelayCommand _closeCommand;

        private GlobalSearchDialogMode _selectedMode = GlobalSearchDialogMode.GlobalAssetSearch;
        private string _queryText = string.Empty;
        private bool _useRegex;
        private bool _searchTextAssetContents;
        private int _selectedTypeFilterIndex;
        private string _graphQueryInput = string.Empty;
        private ClassIDType _graphTargetType = ClassIDType.UnknownType;
        private string _graphTargetName = string.Empty;
        private int _selectedGraphQueryKindIndex;
        private bool _isBusy;
        private string _statusText = "Enter a query (text, pathid:123, guid:hex, type:Material, file:bundle, -exclude).";
        private string _errorText = string.Empty;
        private GlobalSearchResultItem? _selectedResult;
        private CancellationTokenSource? _activeQueryCts;
        private bool _disposed;

        public GlobalSearchDialogViewModel()
            : this(projectIndex: null, assetsManager: null, sqliteCache: null, folderPath: null, signature: null)
        {
        }

        public GlobalSearchDialogViewModel(
            ProjectIndex? projectIndex,
            AssetsManager? assetsManager = null,
            SQLiteProjectIndexCache? sqliteCache = null,
            string? folderPath = null,
            string? signature = null)
        {
            _projectIndex = projectIndex;
            _assetsManager = assetsManager;
            _sqliteCache = sqliteCache;
            _folderPath = folderPath;
            _signature = signature;

            Results = new ObservableCollection<GlobalSearchResultItem>();

            _executeQueryCommand = new AsyncRelayCommand(ExecuteQueryInternalAsync, allowConcurrentExecutions: true);
            _clearCommand = new RelayCommand(Clear);
            _goToSelectedCommand = new RelayCommand(GoToSelected, () => SelectedResult?.IsNavigable == true);
            _inspectSelectedUsageCommand = new AsyncRelayCommand(InspectSelectedUsageAsync, () => SelectedResult != null && !string.IsNullOrWhiteSpace(SelectedResult.AssetUniqueId));
            _switchToSearchModeCommand = new RelayCommand(() => SelectedMode = GlobalSearchDialogMode.GlobalAssetSearch);
            _switchToGraphModeCommand = new RelayCommand(() => SelectedMode = GlobalSearchDialogMode.SemanticUsageGraph);
            _closeCommand = new RelayCommand(Close);
        }

        public event Action<string>? NavigateToAssetRequested;
        public event Action<GlobalSearchResultItem>? NavigateToResultRequested;
        public event Action? RequestClose;

        public string DialogTitle => "Global Project Search & Usage Graph";

        public string QuerySyntaxHint => "Syntax: text, pathid:123, guid:abc123, type:Material, file:sharedassets, -exclude";

        public IReadOnlyList<string> TypeFilterOptions => TypeFilterChoices;

        public IReadOnlyList<string> GraphQueryKindOptions => GraphQueryKindChoices;

        public ObservableCollection<GlobalSearchResultItem> Results { get; }

        public GlobalSearchDialogMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (SetProperty(ref _selectedMode, value))
                {
                    OnPropertyChanged(nameof(SelectedModeIndex));
                    OnPropertyChanged(nameof(IsSearchMode));
                    OnPropertyChanged(nameof(IsGraphMode));
                    OnPropertyChanged(nameof(ExecuteButtonText));
                }
            }
        }

        public int SelectedModeIndex
        {
            get => (int)_selectedMode;
            set => SelectedMode = value == (int)GlobalSearchDialogMode.SemanticUsageGraph
                ? GlobalSearchDialogMode.SemanticUsageGraph
                : GlobalSearchDialogMode.GlobalAssetSearch;
        }

        public bool IsSearchMode => _selectedMode == GlobalSearchDialogMode.GlobalAssetSearch;
        public bool IsGraphMode => _selectedMode == GlobalSearchDialogMode.SemanticUsageGraph;

        public string ExecuteButtonText => IsGraphMode ? "Query Graph" : "Search";

        public string QueryText
        {
            get => _queryText;
            set => SetProperty(ref _queryText, value ?? string.Empty);
        }

        public bool UseRegex
        {
            get => _useRegex;
            set => SetProperty(ref _useRegex, value);
        }

        public bool SearchTextAssetContents
        {
            get => _searchTextAssetContents;
            set => SetProperty(ref _searchTextAssetContents, value);
        }

        public int SelectedTypeFilterIndex
        {
            get => _selectedTypeFilterIndex;
            set
            {
                var clamped = Math.Clamp(value, 0, TypeFilterChoices.Length - 1);
                if (SetProperty(ref _selectedTypeFilterIndex, clamped))
                {
                    OnPropertyChanged(nameof(SelectedTypeFilter));
                }
            }
        }

        public ClassIDType? SelectedTypeFilter
        {
            get
            {
                if (_selectedTypeFilterIndex <= 0 || _selectedTypeFilterIndex >= TypeFilterChoices.Length)
                {
                    return null;
                }

                return Enum.TryParse<ClassIDType>(TypeFilterChoices[_selectedTypeFilterIndex], ignoreCase: true, out var parsed)
                    ? parsed
                    : null;
            }
        }

        public string GraphQueryInput
        {
            get => _graphQueryInput;
            set => SetProperty(ref _graphQueryInput, value ?? string.Empty);
        }

        public ClassIDType GraphTargetType
        {
            get => _graphTargetType;
            set => SetProperty(ref _graphTargetType, value);
        }

        public string GraphTargetName
        {
            get => _graphTargetName;
            set => SetProperty(ref _graphTargetName, value ?? string.Empty);
        }

        public int SelectedGraphQueryKindIndex
        {
            get => _selectedGraphQueryKindIndex;
            set => SetProperty(ref _selectedGraphQueryKindIndex, Math.Clamp(value, 0, GraphQueryKindChoices.Length - 1));
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string ErrorText
        {
            get => _errorText;
            private set
            {
                if (SetProperty(ref _errorText, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

        public bool HasNoResults => Results.Count == 0;

        public GlobalSearchResultItem? SelectedResult
        {
            get => _selectedResult;
            set
            {
                if (SetProperty(ref _selectedResult, value))
                {
                    _goToSelectedCommand.NotifyCanExecuteChanged();
                    _inspectSelectedUsageCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public AsyncRelayCommand ExecuteQueryCommand => _executeQueryCommand;
        public RelayCommand ClearCommand => _clearCommand;
        public RelayCommand GoToSelectedCommand => _goToSelectedCommand;
        public AsyncRelayCommand InspectSelectedUsageCommand => _inspectSelectedUsageCommand;
        public RelayCommand SwitchToSearchModeCommand => _switchToSearchModeCommand;
        public RelayCommand SwitchToGraphModeCommand => _switchToGraphModeCommand;
        public RelayCommand CloseCommand => _closeCommand;

        public void ConfigureGraphUsageForAsset(string assetUniqueId, ClassIDType assetType, string? assetName)
        {
            SelectedMode = GlobalSearchDialogMode.SemanticUsageGraph;
            GraphQueryInput = assetUniqueId?.Trim() ?? string.Empty;
            GraphTargetType = assetType;
            GraphTargetName = string.IsNullOrWhiteSpace(assetName) ? GraphQueryInput : assetName.Trim();
            SelectedGraphQueryKindIndex = 0; // Auto by Asset Type
            ErrorText = string.Empty;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelActiveQueryNoThrow();
        }

        private async Task ExecuteQueryInternalAsync(CancellationToken commandToken)
        {
            CancelActiveQueryNoThrow();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
            _activeQueryCts = cts;
            IsBusy = true;
            ErrorText = string.Empty;

            try
            {
                if (SelectedMode == GlobalSearchDialogMode.GlobalAssetSearch)
                {
                    await ExecuteGlobalSearchAsync(cts.Token);
                }
                else
                {
                    await ExecuteGraphUsageAsync(cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ErrorText = ex.Message;
                StatusText = $"Query failed: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_activeQueryCts, cts))
                {
                    _activeQueryCts = null;
                    IsBusy = false;
                }

                cts.Dispose();
            }
        }

        private async Task ExecuteGlobalSearchAsync(CancellationToken ct)
        {
            var query = new GlobalSearchQuery
            {
                QueryText = _queryText,
                TypeFilter = SelectedTypeFilter,
                UseRegex = _useRegex,
                SearchTextAssetContents = _searchTextAssetContents,
                MaxResults = GlobalSearchQuery.DefaultMaxResults
            };

            StatusText = "Searching project assets...";
            var result = await Task.Run(() =>
                GlobalProjectQueryService.SearchAssets(
                    query,
                    _projectIndex,
                    _assetsManager,
                    _sqliteCache,
                    _folderPath,
                    _signature,
                    ct), ct);

            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            Results.Clear();
            SelectedResult = null;

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorText = result.ErrorMessage;
                StatusText = result.ErrorMessage;
                OnPropertyChanged(nameof(HasNoResults));
                return;
            }

            for (var i = 0; i < result.Hits.Count; i++)
            {
                var hit = result.Hits[i];
                Results.Add(new GlobalSearchResultItem
                {
                    AssetUniqueId = hit.AssetUniqueId,
                    AssetType = hit.AssetType,
                    TypeName = hit.TypeName,
                    Name = hit.Name,
                    Container = hit.Container,
                    SourceFile = hit.SerializedFileName,
                    OriginalPath = hit.OriginalPath,
                    Detail = hit.Detail,
                    MatchReason = hit.MatchReason,
                    PathId = hit.PathId,
                    ByteSize = hit.ByteSize,
                    MatchByteOffset = hit.MatchByteOffset
                });
            }

            StatusText = result.Summary;
            OnPropertyChanged(nameof(HasNoResults));
        }

        private async Task ExecuteGraphUsageAsync(CancellationToken ct)
        {
            var input = _graphQueryInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                Results.Clear();
                SelectedResult = null;
                StatusText = "Enter an Asset UniqueID or Shader name to query the Semantic Usage Graph.";
                OnPropertyChanged(nameof(HasNoResults));
                return;
            }

            var modeIdx = _selectedGraphQueryKindIndex;
            var targetType = _graphTargetType;
            var targetName = _graphTargetName;

            StatusText = "Querying semantic usage graph...";
            var usage = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (_sqliteCache == null || string.IsNullOrWhiteSpace(_folderPath) || string.IsNullOrWhiteSpace(_signature))
                {
                    return GlobalProjectQueryService.FindWhoUsesAsset(_sqliteCache, _folderPath, _signature, input, targetType, targetName);
                }

                return modeIdx switch
                {
                    1 => _sqliteCache.FindMaterialsUsingShader(_folderPath, _signature, input),
                    2 => _sqliteCache.FindUsagesOfTexture(_folderPath, _signature, input),
                    3 => _sqliteCache.FindModelsUsingMaterial(_folderPath, _signature, input),
                    4 => _sqliteCache.FindAnimationsForAvatarOrRig(_folderPath, _signature, input),
                    _ => _sqliteCache.GetAssetUsageSummary(_folderPath, _signature, input, targetType, targetName)
                };
            }, ct);

            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            Results.Clear();
            SelectedResult = null;

            for (var i = 0; i < usage.Items.Count; i++)
            {
                var item = usage.Items[i];
                var detailText = string.IsNullOrWhiteSpace(item.Detail)
                    ? item.Relation
                    : $"{item.Relation} — {item.Detail}";
                Results.Add(new GlobalSearchResultItem
                {
                    AssetUniqueId = item.AssetUniqueId,
                    AssetType = item.AssetType,
                    TypeName = item.TypeName,
                    Name = item.Name,
                    Container = item.Relation,
                    SourceFile = item.SerializedFileName,
                    OriginalPath = item.OriginalPath,
                    Detail = detailText,
                    MatchReason = usage.QueryKind,
                    PathId = item.PathId
                });
            }

            StatusText = usage.Summary;
            OnPropertyChanged(nameof(HasNoResults));
        }

        private async Task InspectSelectedUsageAsync()
        {
            var selected = SelectedResult;
            if (selected == null || string.IsNullOrWhiteSpace(selected.AssetUniqueId))
            {
                return;
            }

            ConfigureGraphUsageForAsset(selected.AssetUniqueId, selected.AssetType, selected.Name);
            await ExecuteQueryCommand.ExecuteAsync();
        }

        private void Clear()
        {
            CancelActiveQueryNoThrow();
            if (IsSearchMode)
            {
                QueryText = string.Empty;
                SelectedTypeFilterIndex = 0;
            }
            else
            {
                GraphQueryInput = string.Empty;
                GraphTargetType = ClassIDType.UnknownType;
                GraphTargetName = string.Empty;
                SelectedGraphQueryKindIndex = 0;
            }

            ErrorText = string.Empty;
            Results.Clear();
            SelectedResult = null;
            StatusText = "Cleared.";
            OnPropertyChanged(nameof(HasNoResults));
        }

        private void GoToSelected()
        {
            var selected = SelectedResult;
            if (selected == null || !selected.IsNavigable)
            {
                return;
            }

            NavigateToResultRequested?.Invoke(selected);
            if (!string.IsNullOrWhiteSpace(selected.AssetUniqueId))
            {
                NavigateToAssetRequested?.Invoke(selected.AssetUniqueId);
            }
        }

        private void Close()
        {
            CancelActiveQueryNoThrow();
            RequestClose?.Invoke();
        }

        private void CancelActiveQueryNoThrow()
        {
            var cts = _activeQueryCts;
            if (cts == null)
            {
                return;
            }

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
