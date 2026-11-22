using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class BatchAnimationClipItemViewModel : ViewModelBase
    {
        private bool _isSelected = true;

        public BatchAnimationClipItemViewModel(
            string name,
            string clipAssetId = "",
            long pathId = 0,
            string relation = "",
            int matchScore = 0,
            float durationSeconds = 0f,
            float sampleRate = 0f,
            int frameCount = 0,
            AnimationClip? clip = null,
            AssetItem? assetItem = null,
            bool isSelected = true)
        {
            if (clip != null && durationSeconds <= 0f && frameCount <= 0)
            {
                BatchAnimationExportHelper.ExtractClipTimingMetadata(clip, out durationSeconds, out sampleRate, out frameCount);
            }

            Name = !string.IsNullOrWhiteSpace(name)
                ? name
                : !string.IsNullOrWhiteSpace(clip?.m_Name)
                    ? clip!.m_Name
                    : $"AnimationClip ({pathId.ToString(CultureInfo.InvariantCulture)})";
            ClipAssetId = clipAssetId ?? string.Empty;
            PathId = pathId != 0 ? pathId : (clip?.m_PathID ?? assetItem?.PathID ?? 0);
            Relation = relation ?? string.Empty;
            MatchScore = matchScore;
            DurationSeconds = durationSeconds;
            SampleRate = sampleRate;
            FrameCount = frameCount;
            Clip = clip;
            AssetItem = assetItem;
            _isSelected = isSelected;
        }

        public string Name { get; }
        public string ClipAssetId { get; }
        public long PathId { get; }
        public string Relation { get; }
        public int MatchScore { get; }
        public float DurationSeconds { get; }
        public float SampleRate { get; }
        public int FrameCount { get; }
        public AnimationClip? Clip { get; }
        public AssetItem? AssetItem { get; }

        public event Action? SelectionChanged;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    SelectionChanged?.Invoke();
                }
            }
        }

        public string PathIdText => PathId != 0
            ? PathId.ToString(CultureInfo.InvariantCulture)
            : "-";

        public string DurationAndFramesText =>
            BatchAnimationExportHelper.FormatClipTimingSummary(DurationSeconds, SampleRate, FrameCount);

        public bool HasTimingInfo => !string.IsNullOrWhiteSpace(DurationAndFramesText);

        public string DetailText
        {
            get
            {
                var segments = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(DurationAndFramesText))
                {
                    segments.Add(DurationAndFramesText);
                }
                if (!string.IsNullOrWhiteSpace(Relation))
                {
                    segments.Add(Relation);
                }
                if (PathId != 0)
                {
                    segments.Add($"PathID: {PathIdText}");
                }
                return string.Join(" • ", segments);
            }
        }

        public bool MatchesFilter(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var needle = query.Trim();
            return (!string.IsNullOrEmpty(Name) && Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(Relation) && Relation.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || PathIdText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(ClipAssetId) && ClipAssetId.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class BatchAnimationExportDialogResult
    {
        public required IReadOnlyList<BatchAnimationClipItemViewModel> SelectedClips { get; init; }
        public required bool SplitAnimations { get; init; }
        public required ModelExportFormat ModelFormat { get; init; }
    }

    public sealed class BatchAnimationExportViewModel : ViewModelBase
    {
        private static readonly string[] ModelFormatChoices =
        {
            "FBX (.fbx)",
            "glTF Binary (.glb)",
            "glTF Separate (.gltf)"
        };

        private readonly List<BatchAnimationClipItemViewModel> _allClips;
        private readonly RelayCommand _selectAllCommand;
        private readonly RelayCommand _deselectAllCommand;
        private readonly RelayCommand _confirmCommand;
        private readonly RelayCommand _cancelCommand;

        private string _searchText = string.Empty;
        private bool _splitAnimations;
        private ModelExportFormat _modelFormat;

        public BatchAnimationExportViewModel(
            string targetDisplayName,
            IEnumerable<BatchAnimationClipItemViewModel> clips,
            ExportOptionsState? exportOptions = null)
        {
            TargetDisplayName = string.IsNullOrWhiteSpace(targetDisplayName) ? "3D Model" : targetDisplayName;
            _allClips = (clips ?? Enumerable.Empty<BatchAnimationClipItemViewModel>())
                .Where(c => c != null)
                .ToList();

            foreach (var item in _allClips)
            {
                item.SelectionChanged += OnClipItemSelectionChanged;
            }

            _splitAnimations = exportOptions?.ExportAnimationsSplit ?? false;
            _modelFormat = exportOptions?.ModelFormat ?? ModelExportFormat.Glb;

            FilteredClips = new BulkObservableCollection<BatchAnimationClipItemViewModel>(_allClips);

            _selectAllCommand = new RelayCommand(SelectAllVisible);
            _deselectAllCommand = new RelayCommand(DeselectAllVisible);
            _confirmCommand = new RelayCommand(Confirm, () => CanConfirm);
            _cancelCommand = new RelayCommand(Cancel);
        }

        public event Action<bool>? RequestClose;

        public string TargetDisplayName { get; }

        public IReadOnlyList<BatchAnimationClipItemViewModel> AllClips => _allClips;

        public BulkObservableCollection<BatchAnimationClipItemViewModel> FilteredClips { get; }

        public IReadOnlyList<string> ModelFormatOptions => ModelFormatChoices;

        public BatchAnimationExportDialogResult? Result { get; private set; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _searchText, normalized))
                {
                    ApplyFilter();
                }
            }
        }

        public bool SplitAnimations
        {
            get => _splitAnimations;
            set
            {
                if (SetProperty(ref _splitAnimations, value))
                {
                    OnPropertyChanged(nameof(IsSingleFileMode));
                    OnPropertyChanged(nameof(IsSplitFolderMode));
                    OnPropertyChanged(nameof(SingleFileModeLabel));
                }
            }
        }

        public bool IsSingleFileMode
        {
            get => !_splitAnimations;
            set
            {
                if (value)
                {
                    SplitAnimations = false;
                }
            }
        }

        public bool IsSplitFolderMode
        {
            get => _splitAnimations;
            set
            {
                if (value)
                {
                    SplitAnimations = true;
                }
            }
        }

        public ModelExportFormat ModelFormat
        {
            get => _modelFormat;
            set
            {
                var clamped = Enum.IsDefined(value) ? value : ModelExportFormat.Glb;
                if (SetProperty(ref _modelFormat, clamped))
                {
                    OnPropertyChanged(nameof(ModelFormatIndex));
                    OnPropertyChanged(nameof(ModelExtensionText));
                    OnPropertyChanged(nameof(SingleFileModeLabel));
                }
            }
        }

        public int ModelFormatIndex
        {
            get => Math.Clamp((int)_modelFormat, 0, ModelFormatChoices.Length - 1);
            set => ModelFormat = (ModelExportFormat)Math.Clamp(value, 0, ModelFormatChoices.Length - 1);
        }

        public string ModelExtensionText => ModelExporter.GetModelFileExtension(_modelFormat);

        public string SingleFileModeLabel =>
            $"Single file (embed all selected clips in one {ModelExtensionText})";

        public string SplitFolderModeLabel =>
            $"Split into folder (base model + 1 {ModelExtensionText} file per clip)";

        public int TotalCount => _allClips.Count;

        public int VisibleCount => FilteredClips.Count;

        public int SelectedCount => _allClips.Count(c => c.IsSelected);

        public bool CanConfirm => SelectedCount > 0;

        public string SelectionSummaryText
        {
            get
            {
                var selected = SelectedCount;
                var total = TotalCount;
                var visible = VisibleCount;
                return visible < total
                    ? string.Create(CultureInfo.InvariantCulture, $"{selected} of {total} clips selected ({visible} shown)")
                    : string.Create(CultureInfo.InvariantCulture, $"{selected} of {total} clips selected");
            }
        }

        public string ConfirmButtonText
        {
            get
            {
                var selected = SelectedCount;
                return selected > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Export ({selected} clip{(selected == 1 ? "" : "s")})...")
                    : "Export...";
            }
        }

        public ICommand SelectAllCommand => _selectAllCommand;
        public ICommand DeselectAllCommand => _deselectAllCommand;
        public ICommand ConfirmCommand => _confirmCommand;
        public ICommand CancelCommand => _cancelCommand;

        public void SelectAllVisible()
        {
            foreach (var item in FilteredClips)
            {
                item.IsSelected = true;
            }
            NotifySelectionSummaryChanged();
        }

        public void DeselectAllVisible()
        {
            foreach (var item in FilteredClips)
            {
                item.IsSelected = false;
            }
            NotifySelectionSummaryChanged();
        }

        public IReadOnlyList<BatchAnimationClipItemViewModel> GetSelectedClips()
        {
            return _allClips.Where(c => c.IsSelected).ToList();
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                FilteredClips.ReplaceAll(_allClips);
            }
            else
            {
                var matches = _allClips.Where(c => c.MatchesFilter(_searchText)).ToList();
                FilteredClips.ReplaceAll(matches);
            }

            OnPropertyChanged(nameof(VisibleCount));
            OnPropertyChanged(nameof(SelectionSummaryText));
        }

        private void OnClipItemSelectionChanged()
        {
            NotifySelectionSummaryChanged();
        }

        private void NotifySelectionSummaryChanged()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(ConfirmButtonText));
            _confirmCommand.NotifyCanExecuteChanged();
        }

        private void Confirm()
        {
            var selected = GetSelectedClips();
            if (selected.Count == 0)
            {
                return;
            }

            Result = new BatchAnimationExportDialogResult
            {
                SelectedClips = selected,
                SplitAnimations = _splitAnimations,
                ModelFormat = _modelFormat
            };
            RequestClose?.Invoke(true);
        }

        private void Cancel()
        {
            Result = null;
            RequestClose?.Invoke(false);
        }
    }
}
