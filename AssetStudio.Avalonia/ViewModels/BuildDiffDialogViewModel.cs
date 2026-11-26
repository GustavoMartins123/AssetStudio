using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum BuildDiffViewMode
    {
        Assets = 0,
        Files = 1
    }

    public sealed class BuildDiffResultRow
    {
        public BuildDiffChangeKind ChangeKind { get; init; }
        public string ChangeBadge { get; init; } = string.Empty;
        public string TypeName { get; init; } = string.Empty;
        public string NameOrPath { get; init; } = string.Empty;
        public string PathIdText { get; init; } = "-";
        public string SourceFile { get; init; } = string.Empty;
        public string SizeDeltaText { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string AssetUniqueId { get; init; } = string.Empty;
        public bool IsAssetRow { get; init; }
    }

    public sealed class BuildDiffDialogViewModel : ViewModelBase, IDisposable
    {
        private static readonly string[] ChangeFilterChoices =
        {
            "All",
            "Added",
            "Removed",
            "Modified",
            "Moved"
        };

        private static readonly string[] ViewModeChoices =
        {
            "Assets",
            "Files"
        };

        private readonly SQLiteProjectIndexCache? _sqliteCache;
        private readonly ProjectIndex? _loadedBaselineIndex;
        private readonly Func<string, string, CancellationToken, BuildDiffReport>? _diffRunner;
        private readonly AsyncRelayCommand _pickBaselineCommand;
        private readonly AsyncRelayCommand _pickTargetCommand;
        private readonly AsyncRelayCommand _runDiffCommand;
        private readonly RelayCommand _cancelDiffCommand;
        private readonly AsyncRelayCommand _exportJsonCommand;
        private readonly AsyncRelayCommand _exportCsvCommand;
        private readonly RelayCommand _closeCommand;

        private string _baselineFolder = string.Empty;
        private string _targetFolder = string.Empty;
        private string _searchText = string.Empty;
        private int _selectedChangeFilterIndex;
        private BuildDiffViewMode _viewMode = BuildDiffViewMode.Assets;
        private bool _hideUnchanged;
        private bool _isRunning;
        private string _statusText = "Choose a baseline folder and a target folder, then click Run Diff.";
        private string _summaryBadgeText = "No diff run yet";
        private string _diagnosticBannerText = string.Empty;
        private BuildDiffReport? _currentReport;
        private BuildDiffResultRow? _selectedResult;
        private CancellationTokenSource? _activeDiffCts;
        private bool _disposed;

        public BuildDiffDialogViewModel()
            : this(sqliteCache: null, loadedBaselineIndex: null, initialBaselineFolder: null, initialTargetFolder: null, diffRunner: null)
        {
        }

        public BuildDiffDialogViewModel(
            SQLiteProjectIndexCache? sqliteCache,
            ProjectIndex? loadedBaselineIndex = null,
            string? initialBaselineFolder = null,
            string? initialTargetFolder = null,
            Func<string, string, CancellationToken, BuildDiffReport>? diffRunner = null)
        {
            _sqliteCache = sqliteCache;
            _loadedBaselineIndex = loadedBaselineIndex;
            _diffRunner = diffRunner;
            _baselineFolder = initialBaselineFolder?.Trim() ?? string.Empty;
            _targetFolder = initialTargetFolder?.Trim() ?? string.Empty;

            FilteredResults = new ObservableCollection<BuildDiffResultRow>();

            _pickBaselineCommand = new AsyncRelayCommand(PickBaselineAsync, () => !IsRunning);
            _pickTargetCommand = new AsyncRelayCommand(PickTargetAsync, () => !IsRunning);
            _runDiffCommand = new AsyncRelayCommand(RunDiffInternalAsync, CanRunDiff);
            _cancelDiffCommand = new RelayCommand(CancelDiff, () => IsRunning);
            _exportJsonCommand = new AsyncRelayCommand(ExportJsonAsync, () => HasReport && !IsRunning);
            _exportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, () => HasReport && !IsRunning);
            _closeCommand = new RelayCommand(Close);
        }

        public event Action? RequestClose;

        public Func<Task<string?>>? PickBaselineFolderAsync { get; set; }
        public Func<Task<string?>>? PickTargetFolderAsync { get; set; }
        public Func<string, string, Task<string?>>? PickExportFileAsync { get; set; }

        public string DialogTitle => "Build-to-Build Diff";

        public IReadOnlyList<string> ChangeFilterOptions => ChangeFilterChoices;

        public IReadOnlyList<string> ViewModeOptions => ViewModeChoices;

        public ObservableCollection<BuildDiffResultRow> FilteredResults { get; }

        public BuildDiffReport? CurrentReport => _currentReport;

        public bool HasReport => _currentReport != null;

        public bool HasAssetLevelDiff => _currentReport?.HasAssetLevelDiff == true;

        public bool HasDiagnosticBanner => !string.IsNullOrWhiteSpace(_diagnosticBannerText);

        public bool HasNoResults => FilteredResults.Count == 0;

        public string BaselineFolder
        {
            get => _baselineFolder;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _baselineFolder, normalized))
                {
                    _runDiffCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string TargetFolder
        {
            get => _targetFolder;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _targetFolder, normalized))
                {
                    _runDiffCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _searchText, normalized))
                {
                    RebuildFilteredResults();
                }
            }
        }

        public int SelectedChangeFilterIndex
        {
            get => _selectedChangeFilterIndex;
            set
            {
                var clamped = Math.Clamp(value, 0, ChangeFilterChoices.Length - 1);
                if (SetProperty(ref _selectedChangeFilterIndex, clamped))
                {
                    OnPropertyChanged(nameof(SelectedChangeFilter));
                    RebuildFilteredResults();
                }
            }
        }

        public string SelectedChangeFilter
        {
            get => ChangeFilterChoices[_selectedChangeFilterIndex];
            set
            {
                var idx = Array.FindIndex(ChangeFilterChoices, c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    SelectedChangeFilterIndex = idx;
                }
            }
        }

        public BuildDiffViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                if (SetProperty(ref _viewMode, value))
                {
                    OnPropertyChanged(nameof(SelectedViewModeIndex));
                    OnPropertyChanged(nameof(IsAssetsView));
                    OnPropertyChanged(nameof(IsFilesView));
                    RebuildFilteredResults();
                }
            }
        }

        public int SelectedViewModeIndex
        {
            get => (int)_viewMode;
            set => ViewMode = value == (int)BuildDiffViewMode.Files ? BuildDiffViewMode.Files : BuildDiffViewMode.Assets;
        }

        public bool IsAssetsView => _viewMode == BuildDiffViewMode.Assets;
        public bool IsFilesView => _viewMode == BuildDiffViewMode.Files;

        public bool HideUnchanged
        {
            get => _hideUnchanged;
            set
            {
                if (SetProperty(ref _hideUnchanged, value))
                {
                    RebuildFilteredResults();
                }
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    NotifyAllCommands();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string SummaryBadgeText
        {
            get => _summaryBadgeText;
            private set => SetProperty(ref _summaryBadgeText, value);
        }

        public string DiagnosticBannerText
        {
            get => _diagnosticBannerText;
            private set
            {
                if (SetProperty(ref _diagnosticBannerText, value))
                {
                    OnPropertyChanged(nameof(HasDiagnosticBanner));
                }
            }
        }

        public BuildDiffResultRow? SelectedResult
        {
            get => _selectedResult;
            set => SetProperty(ref _selectedResult, value);
        }

        public AsyncRelayCommand PickBaselineCommand => _pickBaselineCommand;
        public AsyncRelayCommand PickTargetCommand => _pickTargetCommand;
        public AsyncRelayCommand RunDiffCommand => _runDiffCommand;
        public RelayCommand CancelDiffCommand => _cancelDiffCommand;
        public AsyncRelayCommand ExportJsonCommand => _exportJsonCommand;
        public AsyncRelayCommand ExportCsvCommand => _exportCsvCommand;
        public RelayCommand CloseCommand => _closeCommand;

        public void LoadReport(BuildDiffReport report)
        {
            ArgumentNullException.ThrowIfNull(report);
            ApplyReport(report);
        }

        public bool ExportJsonToPath(string outputPath)
        {
            if (_currentReport == null || string.IsNullOrWhiteSpace(outputPath))
            {
                return false;
            }

            BuildDiffService.ExportJson(_currentReport, outputPath);
            StatusText = $"Exported JSON report to: {outputPath}";
            return true;
        }

        public bool ExportCsvToPath(string outputPath)
        {
            if (_currentReport == null || string.IsNullOrWhiteSpace(outputPath))
            {
                return false;
            }

            BuildDiffService.ExportCsv(_currentReport, outputPath);
            StatusText = $"Exported CSV report to: {outputPath}";
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelActiveDiffNoThrow();
        }

        private bool CanRunDiff()
        {
            return !IsRunning
                && !string.IsNullOrWhiteSpace(_baselineFolder)
                && !string.IsNullOrWhiteSpace(_targetFolder);
        }

        private async Task PickBaselineAsync()
        {
            if (PickBaselineFolderAsync == null)
            {
                return;
            }

            var picked = await PickBaselineFolderAsync();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                BaselineFolder = picked;
            }
        }

        private async Task PickTargetAsync()
        {
            if (PickTargetFolderAsync == null)
            {
                return;
            }

            var picked = await PickTargetFolderAsync();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                TargetFolder = picked;
            }
        }

        private async Task RunDiffInternalAsync(CancellationToken commandToken)
        {
            var baseline = _baselineFolder.Trim();
            var target = _targetFolder.Trim();
            if (string.IsNullOrWhiteSpace(baseline) || string.IsNullOrWhiteSpace(target))
            {
                StatusText = "Both baseline and target folders are required.";
                return;
            }

            CancelActiveDiffNoThrow();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
            _activeDiffCts = linkedCts;
            IsRunning = true;
            StatusText = "Comparing builds...";

            try
            {
                var ct = linkedCts.Token;
                var report = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (_diffRunner != null)
                    {
                        return _diffRunner(baseline, target, ct);
                    }

                    return BuildDiffService.CompareFolders(
                        baseline,
                        target,
                        _sqliteCache,
                        _loadedBaselineIndex,
                        ct);
                }, ct);

                if (ct.IsCancellationRequested || _disposed)
                {
                    StatusText = "Build diff cancelled.";
                    return;
                }

                ApplyReport(report);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Build diff cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Build diff failed: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_activeDiffCts, linkedCts))
                {
                    _activeDiffCts = null;
                }

                linkedCts.Dispose();
                IsRunning = false;
            }
        }

        private void CancelDiff()
        {
            CancelActiveDiffNoThrow();
            _runDiffCommand.Cancel();
            StatusText = "Cancelling build diff...";
        }

        private async Task ExportJsonAsync()
        {
            if (_currentReport == null || PickExportFileAsync == null)
            {
                return;
            }

            try
            {
                var path = await PickExportFileAsync("build-diff-report.json", "json");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                await Task.Run(() => BuildDiffService.ExportJson(_currentReport, path));
                StatusText = $"Exported JSON report to: {path}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to export JSON report: {ex.Message}";
            }
        }

        private async Task ExportCsvAsync()
        {
            if (_currentReport == null || PickExportFileAsync == null)
            {
                return;
            }

            try
            {
                var path = await PickExportFileAsync("build-diff-report.csv", "csv");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                await Task.Run(() => BuildDiffService.ExportCsv(_currentReport, path));
                StatusText = $"Exported CSV report to: {path}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to export CSV report: {ex.Message}";
            }
        }

        private void Close()
        {
            CancelActiveDiffNoThrow();
            RequestClose?.Invoke();
        }

        private void ApplyReport(BuildDiffReport report)
        {
            _currentReport = report;
            OnPropertyChanged(nameof(CurrentReport));
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(HasAssetLevelDiff));

            DiagnosticBannerText = report.AssetDiffNote ?? string.Empty;

            if (!report.HasAssetLevelDiff && _viewMode == BuildDiffViewMode.Assets)
            {
                _viewMode = BuildDiffViewMode.Files;
                OnPropertyChanged(nameof(ViewMode));
                OnPropertyChanged(nameof(SelectedViewModeIndex));
                OnPropertyChanged(nameof(IsAssetsView));
                OnPropertyChanged(nameof(IsFilesView));
            }

            SummaryBadgeText = report.HasAssetLevelDiff
                ? $"Assets: +{report.AddedAssets:N0} Added • -{report.RemovedAssets:N0} Removed • ~{report.ModifiedAssets:N0} Modified • ->{report.MovedAssets:N0} Moved  |  Files: +{report.AddedFiles:N0} / -{report.RemovedFiles:N0} / ~{report.ModifiedFiles:N0}"
                : $"Files: +{report.AddedFiles:N0} Added • -{report.RemovedFiles:N0} Removed • ~{report.ModifiedFiles:N0} Modified • ={report.UnchangedFiles:N0} Unchanged";

            StatusText = $"Diff completed in {report.ElapsedMs:N0} ms.";
            RebuildFilteredResults();
            NotifyAllCommands();
        }

        private void RebuildFilteredResults()
        {
            FilteredResults.Clear();
            SelectedResult = null;

            if (_currentReport == null)
            {
                OnPropertyChanged(nameof(HasNoResults));
                return;
            }

            var filterKind = ResolveSelectedChangeKind();
            var needle = _searchText.Trim();

            if (_viewMode == BuildDiffViewMode.Assets)
            {
                var assets = _currentReport.AssetEntries;
                for (var i = 0; i < assets.Count; i++)
                {
                    var a = assets[i];
                    if (!MatchesChangeFilter(a.ChangeKind, filterKind))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(needle) && !MatchesAssetSearch(a, needle))
                    {
                        continue;
                    }

                    FilteredResults.Add(new BuildDiffResultRow
                    {
                        ChangeKind = a.ChangeKind,
                        ChangeBadge = FormatChangeBadge(a.ChangeKind),
                        TypeName = a.TypeName,
                        NameOrPath = a.Name,
                        PathIdText = a.PathId.ToString(CultureInfo.InvariantCulture),
                        SourceFile = a.SerializedFileName,
                        SizeDeltaText = FormatSizeDelta(a.SizeDelta),
                        Detail = a.Detail,
                        AssetUniqueId = a.AssetUniqueId,
                        IsAssetRow = true
                    });
                }
            }
            else
            {
                var files = _currentReport.FileEntries;
                for (var i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    if (!MatchesChangeFilter(f.ChangeKind, filterKind))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(needle) && !MatchesFileSearch(f, needle))
                    {
                        continue;
                    }

                    FilteredResults.Add(new BuildDiffResultRow
                    {
                        ChangeKind = f.ChangeKind,
                        ChangeBadge = FormatChangeBadge(f.ChangeKind),
                        TypeName = f.FileKind.ToString(),
                        NameOrPath = f.RelativePath,
                        PathIdText = "-",
                        SourceFile = Path.GetFileName(f.RelativePath),
                        SizeDeltaText = FormatSizeDelta(f.SizeDelta),
                        Detail = f.Detail,
                        AssetUniqueId = string.Empty,
                        IsAssetRow = false
                    });
                }
            }

            OnPropertyChanged(nameof(HasNoResults));
        }

        private BuildDiffChangeKind? ResolveSelectedChangeKind() => _selectedChangeFilterIndex switch
        {
            1 => BuildDiffChangeKind.Added,
            2 => BuildDiffChangeKind.Removed,
            3 => BuildDiffChangeKind.Modified,
            4 => BuildDiffChangeKind.Moved,
            _ => null
        };

        private bool MatchesChangeFilter(BuildDiffChangeKind itemKind, BuildDiffChangeKind? selectedFilter)
        {
            if (selectedFilter.HasValue)
            {
                return itemKind == selectedFilter.Value;
            }

            if (_hideUnchanged && itemKind == BuildDiffChangeKind.Unchanged)
            {
                return false;
            }

            return true;
        }

        private static bool MatchesAssetSearch(BuildDiffAssetEntry entry, string needle)
        {
            return entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.TypeName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.SerializedFileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.BaselineSerializedFileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.TargetContainer.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.BaselineContainer.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.PathId.ToString(CultureInfo.InvariantCulture).Contains(needle, StringComparison.Ordinal);
        }

        private static bool MatchesFileSearch(BuildDiffFileEntry entry, string needle)
        {
            return entry.RelativePath.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.FileKind.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatChangeBadge(BuildDiffChangeKind kind) => kind switch
        {
            BuildDiffChangeKind.Added => "+ Added",
            BuildDiffChangeKind.Removed => "- Removed",
            BuildDiffChangeKind.Modified => "~ Modified",
            BuildDiffChangeKind.Moved => "-> Moved",
            _ => "= Unchanged"
        };

        private static string FormatSizeDelta(long delta)
        {
            if (delta == 0) return "0 B";
            return delta > 0 ? $"+{delta:N0} B" : $"{delta:N0} B";
        }

        private void NotifyAllCommands()
        {
            _pickBaselineCommand.NotifyCanExecuteChanged();
            _pickTargetCommand.NotifyCanExecuteChanged();
            _runDiffCommand.NotifyCanExecuteChanged();
            _cancelDiffCommand.NotifyCanExecuteChanged();
            _exportJsonCommand.NotifyCanExecuteChanged();
            _exportCsvCommand.NotifyCanExecuteChanged();
        }

        private void CancelActiveDiffNoThrow()
        {
            var cts = _activeDiffCts;
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
