using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class SceneExportCandidate
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ScenePath { get; init; } = string.Empty;
        public int BuildIndex { get; init; } = -1;
        public string SourceKind { get; init; } = string.Empty;
        public string SerializedFileName { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public string ContainerKey { get; init; } = string.Empty;
        public bool IsPreselected { get; init; }
        public int GameObjectCount { get; init; } = -1;
        public int RendererCount { get; init; } = -1;
    }

    public sealed class SceneExportDialogRequest
    {
        public SceneExportDialogRequest(
            IReadOnlyList<SceneExportCandidate> scenes,
            SceneExportOptions? initialOptions = null,
            string? dialogTitle = null)
        {
            Scenes = scenes ?? Array.Empty<SceneExportCandidate>();
            InitialOptions = initialOptions ?? new SceneExportOptions();
            DialogTitle = string.IsNullOrWhiteSpace(dialogTitle)
                ? "Export Unity Scene to .glb (Instanced)"
                : dialogTitle.Trim();
        }

        public IReadOnlyList<SceneExportCandidate> Scenes { get; }
        public SceneExportOptions InitialOptions { get; }
        public string DialogTitle { get; }
    }

    public sealed class SceneExportDialogResult
    {
        public SceneExportDialogResult(
            IReadOnlyList<SceneExportCandidate> selectedScenes,
            SceneExportOptions options)
        {
            SelectedScenes = selectedScenes ?? Array.Empty<SceneExportCandidate>();
            Options = options ?? new SceneExportOptions();
        }

        public IReadOnlyList<SceneExportCandidate> SelectedScenes { get; }
        public SceneExportOptions Options { get; }
    }

    public sealed class SceneSelectionItemViewModel : ViewModelBase
    {
        private readonly Action _onSelectionChanged;
        private bool _isSelected;

        public SceneSelectionItemViewModel(SceneExportCandidate candidate, bool initialSelected, Action onSelectionChanged)
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            _onSelectionChanged = onSelectionChanged ?? throw new ArgumentNullException(nameof(onSelectionChanged));
            _isSelected = initialSelected;
        }

        public SceneExportCandidate Candidate { get; }

        public string Id => Candidate.Id;
        public string Name => string.IsNullOrWhiteSpace(Candidate.Name) ? "Scene" : Candidate.Name;
        public string ScenePath => Candidate.ScenePath;
        public int BuildIndex => Candidate.BuildIndex;
        public string SourceKind => string.IsNullOrWhiteSpace(Candidate.SourceKind) ? "Scene" : Candidate.SourceKind;
        public string SerializedFileName => Candidate.SerializedFileName;
        public string OriginalPath => Candidate.OriginalPath;
        public string ContainerKey => Candidate.ContainerKey;

        public string BuildIndexDisplay => Candidate.BuildIndex >= 0 ? $"#{Candidate.BuildIndex}" : string.Empty;
        public bool HasBuildIndex => Candidate.BuildIndex >= 0;

        public string SubtitleDisplay
        {
            get
            {
                var parts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(Candidate.ScenePath)
                    && !string.Equals(Candidate.ScenePath, Name, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(Candidate.ScenePath);
                }

                var sourceLeaf = !string.IsNullOrWhiteSpace(Candidate.SerializedFileName)
                    ? Candidate.SerializedFileName
                    : (!string.IsNullOrWhiteSpace(Candidate.OriginalPath) ? Path.GetFileName(Candidate.OriginalPath) : string.Empty);

                if (!string.IsNullOrWhiteSpace(sourceLeaf)
                    && !parts.Any(p => string.Equals(p, sourceLeaf, StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(sourceLeaf);
                }

                if (Candidate.GameObjectCount >= 0 || Candidate.RendererCount >= 0)
                {
                    var goCount = Math.Max(0, Candidate.GameObjectCount);
                    var rendererCount = Math.Max(0, Candidate.RendererCount);
                    var statsText = rendererCount == 0 && Candidate.GameObjectCount >= 0
                        ? $"{goCount:N0} objects, {rendererCount:N0} renderers (logic/UI shell)"
                        : $"{goCount:N0} objects, {rendererCount:N0} renderers";
                    parts.Add(statsText);
                }

                return parts.Count > 0 ? string.Join("  •  ", parts) : Name;
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    _onSelectionChanged();
                }
            }
        }

        internal void SetSelectedQuiet(bool selected)
        {
            if (SetProperty(ref _isSelected, selected, nameof(IsSelected)))
            {
                // Caller batches parent notification.
            }
        }
    }

    public sealed class SceneExportDialogViewModel : ViewModelBase
    {
        private static readonly string[] LodModeChoices =
        {
            "LOD0 Only (Recommended)",
            "All LODs"
        };

        private readonly List<SceneSelectionItemViewModel> _allScenes;
        private readonly RelayCommand _confirmCommand;
        private readonly RelayCommand _cancelCommand;
        private readonly RelayCommand _selectAllFilteredCommand;
        private readonly RelayCommand _clearSelectionCommand;
        private readonly RelayCommand _clearSearchCommand;

        private string _searchText = string.Empty;
        private SceneLodExportMode _lodMode;
        private bool _includeInactive;
        private bool _exportCamerasAndLights;
        private bool _resolveStaticScriptSpawns;
        private bool _writeManifest;

        public SceneExportDialogViewModel()
            : this(new SceneExportDialogRequest(Array.Empty<SceneExportCandidate>()))
        {
        }

        public SceneExportDialogViewModel(SceneExportDialogRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            DialogTitle = request.DialogTitle;

            var initial = request.InitialOptions ?? new SceneExportOptions();
            _lodMode = initial.LodMode;
            _includeInactive = initial.IncludeInactive;
            _exportCamerasAndLights = initial.ExportCamerasAndLights;
            _resolveStaticScriptSpawns = initial.ResolveStaticScriptSpawns;
            _writeManifest = initial.WriteManifest;

            _allScenes = new List<SceneSelectionItemViewModel>(request.Scenes.Count);
            var nonNullCandidates = request.Scenes.Where(static s => s != null).ToList();
            var hasExplicitPreselection = nonNullCandidates.Any(static s => s.IsPreselected);
            var autoSelectSingleScene = !hasExplicitPreselection && nonNullCandidates.Count == 1;

            for (var i = 0; i < nonNullCandidates.Count; i++)
            {
                var candidate = nonNullCandidates[i];
                var preselected = hasExplicitPreselection
                    ? candidate.IsPreselected
                    : autoSelectSingleScene;
                _allScenes.Add(new SceneSelectionItemViewModel(candidate, preselected, OnItemSelectionChanged));
            }

            FilteredScenes = new ObservableCollection<SceneSelectionItemViewModel>(_allScenes);

            _confirmCommand = new RelayCommand(Confirm, () => HasSelection);
            _cancelCommand = new RelayCommand(Cancel);
            _selectAllFilteredCommand = new RelayCommand(SelectAllFiltered, () => FilteredScenes.Count > 0);
            _clearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelection);
            _clearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrEmpty(SearchText));
        }

        public event Action<SceneExportDialogResult?>? RequestClose;

        public string DialogTitle { get; }

        public SceneExportDialogResult? Result { get; private set; }

        public IReadOnlyList<SceneSelectionItemViewModel> AllScenes => _allScenes;

        public ObservableCollection<SceneSelectionItemViewModel> FilteredScenes { get; }

        public IReadOnlyList<string> LodModeOptions => LodModeChoices;

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _searchText, normalized))
                {
                    ApplyFilter();
                    _clearSearchCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public SceneLodExportMode LodMode
        {
            get => _lodMode;
            set
            {
                if (SetProperty(ref _lodMode, value))
                {
                    OnPropertyChanged(nameof(SelectedLodModeIndex));
                }
            }
        }

        public int SelectedLodModeIndex
        {
            get => _lodMode == SceneLodExportMode.AllLods ? 1 : 0;
            set => LodMode = value == 1 ? SceneLodExportMode.AllLods : SceneLodExportMode.Lod0Only;
        }

        public bool IncludeInactive
        {
            get => _includeInactive;
            set => SetProperty(ref _includeInactive, value);
        }

        public bool ExportCamerasAndLights
        {
            get => _exportCamerasAndLights;
            set => SetProperty(ref _exportCamerasAndLights, value);
        }

        public bool ResolveStaticScriptSpawns
        {
            get => _resolveStaticScriptSpawns;
            set => SetProperty(ref _resolveStaticScriptSpawns, value);
        }

        public bool WriteManifest
        {
            get => _writeManifest;
            set => SetProperty(ref _writeManifest, value);
        }

        public int TotalCount => _allScenes.Count;

        public int FilteredCount => FilteredScenes.Count;

        public int SelectedCount => _allScenes.Count(static s => s.IsSelected);

        public bool HasSelection => SelectedCount > 0;

        public bool HasNoFilteredScenes => FilteredScenes.Count == 0;

        public string SelectionSummaryText
        {
            get
            {
                var selected = SelectedCount;
                if (selected == 0)
                {
                    return $"0 of {TotalCount} scene(s) selected — select at least one scene to export.";
                }

                if (selected == 1)
                {
                    var single = _allScenes.First(static s => s.IsSelected);
                    return $"1 scene selected: {single.Name}";
                }

                return $"{selected} scenes selected (will be merged additively into one instanced .glb)";
            }
        }

        public RelayCommand ConfirmCommand => _confirmCommand;
        public RelayCommand CancelCommand => _cancelCommand;
        public RelayCommand SelectAllFilteredCommand => _selectAllFilteredCommand;
        public RelayCommand ClearSelectionCommand => _clearSelectionCommand;
        public RelayCommand ClearSearchCommand => _clearSearchCommand;

        public SceneExportOptions BuildSelectedOptions()
        {
            return new SceneExportOptions
            {
                IncludeInactive = IncludeInactive,
                LodMode = LodMode,
                ExportCamerasAndLights = ExportCamerasAndLights,
                ResolveStaticScriptSpawns = ResolveStaticScriptSpawns,
                WriteManifest = WriteManifest
            };
        }

        public IReadOnlyList<SceneExportCandidate> GetSelectedCandidates()
        {
            return _allScenes
                .Where(static s => s.IsSelected)
                .Select(static s => s.Candidate)
                .ToList();
        }

        private void ApplyFilter()
        {
            var query = _searchText.Trim();
            FilteredScenes.Clear();

            for (var i = 0; i < _allScenes.Count; i++)
            {
                var item = _allScenes[i];
                if (string.IsNullOrEmpty(query) || MatchesSearch(item, query))
                {
                    FilteredScenes.Add(item);
                }
            }

            OnPropertyChanged(nameof(FilteredCount));
            OnPropertyChanged(nameof(HasNoFilteredScenes));
            _selectAllFilteredCommand.NotifyCanExecuteChanged();
        }

        private static bool MatchesSearch(SceneSelectionItemViewModel item, string query)
        {
            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ScenePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.SerializedFileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ContainerKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.SourceKind.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.BuildIndexDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void SelectAllFiltered()
        {
            for (var i = 0; i < FilteredScenes.Count; i++)
            {
                FilteredScenes[i].SetSelectedQuiet(true);
            }

            OnItemSelectionChanged();
        }

        private void ClearSelection()
        {
            for (var i = 0; i < _allScenes.Count; i++)
            {
                _allScenes[i].SetSelectedQuiet(false);
            }

            OnItemSelectionChanged();
        }

        private void OnItemSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionSummaryText));
            _confirmCommand.NotifyCanExecuteChanged();
            _clearSelectionCommand.NotifyCanExecuteChanged();
        }

        private void Confirm()
        {
            var selected = GetSelectedCandidates();
            if (selected.Count == 0)
            {
                return;
            }

            Result = new SceneExportDialogResult(selected, BuildSelectedOptions());
            RequestClose?.Invoke(Result);
        }

        private void Cancel()
        {
            Result = null;
            RequestClose?.Invoke(null);
        }
    }
}
