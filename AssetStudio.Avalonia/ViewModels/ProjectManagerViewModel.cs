using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class ProjectEntryViewModel : ViewModelBase
    {
        private readonly ProjectIndexingState? _indexingState;
        private readonly string _indexingListDisplay;

        public ProjectEntryViewModel(ManagedProject project)
            : this(project, indexingState: null)
        {
        }

        internal ProjectEntryViewModel(ManagedProject project, ProjectIndexingState? indexingState)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            _indexingState = indexingState;
            _indexingListDisplay = FormatIndexingListDisplay(indexingState);
        }

        public ManagedProject Project { get; }

        public string Id => Project.Id;

        public string IconPath => Project.IconPath;

        public string DisplayName => Project.DisplayName;

        public string RootDisplay => string.IsNullOrWhiteSpace(Project.ProjectRoot)
            ? "No project root"
            : Project.ProjectRoot;

        public string LastAccessedDisplay => Project.LastAccessedAtUtc.HasValue
            ? "Last opened " + FormatDate(Project.LastAccessedAtUtc.Value)
            : "Never opened";

        public string IndexingListDisplay => _indexingListDisplay;

        public bool HasIndexingDisplay => !string.IsNullOrWhiteSpace(_indexingListDisplay);

        public string IndexingDetail => FormatIndexingDetail(_indexingState);

        public string AnalysisListDisplay => FormatAnalysisListDisplay(Project.AnalysisSummary);

        public bool HasAnalysisDisplay => !string.IsNullOrWhiteSpace(Project.ProjectRoot);

        public string AnalysisDetail => FormatAnalysisDetail(Project.AnalysisSummary);

        public string StatsDetail
        {
            get
            {
                var stats = Project.Stats;
                if (stats.TotalFiles == 0 && stats.AssetCount == 0)
                {
                    return "No stats yet.";
                }

                var files = stats.TotalFiles > 0
                    ? $"{stats.TotalFiles:N0} files, {FormatBytes(stats.TotalBytes)}"
                    : "Files not scanned";
                var bundles = stats.UnityBundleCount > 0
                    ? $"{stats.UnityBundleCount:N0} bundles"
                    : "No bundles counted";
                var assets = stats.AssetCount > 0
                    ? $"{stats.AssetCount:N0} assets, {stats.ExportableAssetCount:N0} exportable"
                    : "Assets not counted";
                var scanned = stats.LastScannedAtUtc.HasValue
                    ? "Scanned " + FormatDate(stats.LastScannedAtUtc.Value)
                    : "Not scanned";

                return $"{files} | {bundles} | {assets} | {scanned}";
            }
        }

        public void UpdateAnalysisSummary(ProjectAnalysisSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);
            Project.AnalysisSummary = summary.Clone();
            OnPropertyChanged(nameof(AnalysisListDisplay));
            OnPropertyChanged(nameof(AnalysisDetail));
        }

        public bool MatchesSearch(string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            var terms = searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in terms)
            {
                var matched = DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(Project.Name) && Project.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(Project.AutoDetectedName) && Project.AutoDetectedName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(Project.ProjectRoot) && Project.ProjectRoot.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || AnalysisListDisplay.Contains(term, StringComparison.OrdinalIgnoreCase);

                if (!matched)
                {
                    return false;
                }
            }

            return true;
        }

        internal static string FormatNullableDate(DateTime? date)
        {
            return date.HasValue ? FormatDate(date.Value) : "-";
        }

        internal static string FormatDate(DateTime date)
        {
            return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string FormatAnalysisListDisplay(ProjectAnalysisSummary? summary)
        {
            if (summary == null)
            {
                return "Analysis: Pending";
            }

            return summary.Status switch
            {
                ProjectAnalysisStatus.Pending => "Analysis: Pending",
                ProjectAnalysisStatus.Running => "Analysis: Running...",
                ProjectAnalysisStatus.Failed => "Analysis: Failed",
                ProjectAnalysisStatus.Ready => string.IsNullOrWhiteSpace(summary.RuntimeBadge)
                    ? "Analysis: Ready"
                    : $"Analysis: Ready \u2022 {summary.RuntimeBadge}",
                _ => "Analysis: Pending"
            };
        }

        private static string FormatAnalysisDetail(ProjectAnalysisSummary? summary)
        {
            if (summary == null)
            {
                return "Pending (not analyzed yet).";
            }

            var lines = new List<string>
            {
                summary.Status switch
                {
                    ProjectAnalysisStatus.Pending => "Status: Pending (click Re-run Analysis or open project)",
                    ProjectAnalysisStatus.Running => "Status: Running...",
                    ProjectAnalysisStatus.Failed => string.IsNullOrWhiteSpace(summary.ErrorMessage)
                        ? "Status: Failed"
                        : $"Status: Failed ({summary.ErrorMessage})",
                    ProjectAnalysisStatus.Ready => string.IsNullOrWhiteSpace(summary.RuntimeBadge)
                        ? "Status: Ready"
                        : $"Status: Ready ({summary.RuntimeBadge})",
                    _ => "Status: Pending"
                }
            };

            if (summary.Status == ProjectAnalysisStatus.Ready)
            {
                var metrics = new List<string>();
                if (summary.ManagedAssemblyCount > 0)
                {
                    metrics.Add($"{summary.ManagedAssemblyCount:N0} managed DLLs");
                }

                if (summary.Il2CppScriptCount > 0)
                {
                    metrics.Add($"{summary.Il2CppScriptCount:N0} IL2CPP symbols");
                }
                else if (summary.ScriptingBackend == UnityScriptingBackend.Il2Cpp)
                {
                    metrics.Add(summary.Il2CppDumperArtifactsDetected
                        ? "Il2CppDumper artifacts detected"
                        : "No Il2CppDumper artifacts yet");
                }

                if (summary.HasAddressables)
                {
                    metrics.Add($"{summary.AddressablesBundleCount:N0} Addressables bundles");
                }

                if (summary.DecryptionHintCount > 0)
                {
                    metrics.Add($"{summary.DecryptionHintCount:N0} decryption hint{(summary.DecryptionHintCount == 1 ? string.Empty : "s")}");
                }

                if (metrics.Count > 0)
                {
                    lines.Add(string.Join(" | ", metrics));
                }

                if (summary.AnalyzedAtUtc.HasValue)
                {
                    lines.Add("Analyzed: " + FormatDate(summary.AnalyzedAtUtc.Value));
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string FormatIndexingListDisplay(ProjectIndexingState? state)
        {
            if (state == null)
            {
                return string.Empty;
            }

            var counts = state.TotalFiles > 0
                ? $"{state.ProcessedFiles:N0}/{state.TotalFiles:N0}"
                : $"{state.ProcessedFiles:N0}";
            return $"Indexing: {FormatIndexingStatus(state.Status)} {state.PercentComplete:0.#}% ({counts})";
        }

        private static string FormatIndexingDetail(ProjectIndexingState? state)
        {
            if (state == null)
            {
                return "No indexing state saved yet.";
            }

            var lines = new List<string>
            {
                $"{FormatIndexingStatus(state.Status)}, {state.PercentComplete:0.#}% ({FormatIndexingCounts(state)})"
            };

            if (!string.IsNullOrWhiteSpace(state.CurrentFile))
            {
                lines.Add("Current file: " + Path.GetFileName(state.CurrentFile));
            }

            if (!string.IsNullOrWhiteSpace(state.LastReadFile))
            {
                lines.Add("Last read file: " + Path.GetFileName(state.LastReadFile));
            }

            if (state.UpdatedAt.HasValue)
            {
                lines.Add("Updated: " + FormatDate(state.UpdatedAt.Value));
            }

            if (state.CompletedAt.HasValue)
            {
                lines.Add("Completed: " + FormatDate(state.CompletedAt.Value));
            }

            if (state.EffectiveReadFileCount > 0)
            {
                lines.Add($"Read file list saved: {state.EffectiveReadFileCount:N0}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string FormatIndexingCounts(ProjectIndexingState state)
        {
            var unitLabel = IsStageProgressStatus(state.Status) ? "steps" : "files";
            if (state.TotalFiles <= 0)
            {
                return $"{state.ProcessedFiles:N0} {unitLabel}";
            }

            return $"{state.ProcessedFiles:N0}/{state.TotalFiles:N0} {unitLabel}, {state.PendingFiles:N0} pending";
        }

        private static bool IsStageProgressStatus(string? status)
        {
            return string.Equals(status, "saving_index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "saving_connections", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatIndexingStatus(string status)
        {
            return (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "running" => "Running",
                "paused" => "Paused",
                "cancelling" => "Stopping",
                "cancelled" => "Cancelled",
                "saving_index" => "Saving index cache",
                "saving_connections" => "Saving connections",
                "connecting" => "Building connections",
                "connections_completed" => "Connections complete",
                "connections_failed" => "Connections failed",
                "building_structure" => "Building asset structure",
                "structure_completed" => "Asset structure complete",
                "structure_failed" => "Asset structure failed",
                "completed" => "Complete",
                "failed" => "Failed",
                _ => "Unknown"
            };
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            string[] units = { "KB", "MB", "GB", "TB" };
            var value = bytes / 1024d;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }
    }

    public sealed class ProjectManagerViewModel : ViewModelBase, IDisposable
    {
        private readonly ProjectManagerStore _store;
        private readonly Action<Action> _dispatchToUi;
        private readonly List<ProjectEntryViewModel> _allProjects = new();
        private readonly HashSet<string> _runningAnalysisProjectIds = new(StringComparer.Ordinal);

        private readonly AsyncRelayCommand _openCommand;
        private readonly RelayCommand _openWithoutProjectCommand;
        private readonly AsyncRelayCommand _newProjectCommand;
        private readonly AsyncRelayCommand _editProjectCommand;
        private readonly AsyncRelayCommand _deleteProjectCommand;
        private readonly AsyncRelayCommand _rerunAnalysisCommand;
        private readonly AsyncRelayCommand _browseIl2CppDumperCommand;
        private readonly AsyncRelayCommand _saveAnalysisPreferencesCommand;
        private readonly AsyncRelayCommand _refreshCommand;
        private readonly RelayCommand _closeCommand;

        private ProjectEntryViewModel? _selectedProject;
        private string _searchText = string.Empty;
        private string _statusText = string.Empty;
        private bool _allowIl2CppDumper;
        private string _il2CppDumperPath = string.Empty;
        private int _refreshVersion;
        private bool _isRefreshingProjects;
        private bool _isOpeningProject;
        private bool _isDeletingProject;
        private bool _isSyncingSelection;
        private bool _disposed;

        public ProjectManagerViewModel(
            ProjectManagerStore? store = null,
            Action<Action>? dispatchToUi = null,
            Func<ManagedProject?, Task<ManagedProject?>>? editProjectAsync = null,
            Func<ProjectEntryViewModel, Task<bool>>? confirmDeleteAsync = null,
            Func<Task<string?>>? pickIl2CppDumperPathAsync = null,
            Action<string, string>? showError = null,
            Action<ProjectLaunchContext?>? launchProject = null)
        {
            _store = store ?? ProjectManagerStore.Shared;
            _dispatchToUi = dispatchToUi ?? (static action => action());

            EditProjectAsync = editProjectAsync;
            ConfirmDeleteAsync = confirmDeleteAsync;
            PickIl2CppDumperPathAsync = pickIl2CppDumperPathAsync;
            ShowError = showError;
            LaunchProject = launchProject;

            Projects = new ObservableCollection<ProjectEntryViewModel>();

            _openCommand = new AsyncRelayCommand(OpenSelectedProjectAsync, () => CanOpen);
            _openWithoutProjectCommand = new RelayCommand(OpenWithoutProject, () => CanOpenWithoutProject);
            _newProjectCommand = new AsyncRelayCommand(AddProjectAsync, () => CanAdd);
            _editProjectCommand = new AsyncRelayCommand(EditSelectedProjectAsync, () => CanEdit);
            _deleteProjectCommand = new AsyncRelayCommand(DeleteSelectedProjectAsync, () => CanDelete);
            _rerunAnalysisCommand = new AsyncRelayCommand(RerunSelectedAnalysisAsync, () => CanRerunAnalysis);
            _browseIl2CppDumperCommand = new AsyncRelayCommand(BrowseIl2CppDumperAsync, () => CanEditAnalysisPreferences);
            _saveAnalysisPreferencesCommand = new AsyncRelayCommand(SaveSelectedAnalysisPreferencesAsync, () => CanEditAnalysisPreferences);
            _refreshCommand = new AsyncRelayCommand(() => RefreshProjectsAsync());
            _closeCommand = new RelayCommand(Close);

            _store.AnalysisService.AnalysisUpdated += OnProjectAnalysisUpdated;
        }

        public event Action<bool>? RequestClose;

        public ProjectManagerStore Store => _store;

        public ObservableCollection<ProjectEntryViewModel> Projects { get; }

        public Func<ManagedProject?, Task<ManagedProject?>>? EditProjectAsync { get; set; }

        public Func<ProjectEntryViewModel, Task<bool>>? ConfirmDeleteAsync { get; set; }

        public Func<Task<string?>>? PickIl2CppDumperPathAsync { get; set; }

        public Action<string, string>? ShowError { get; set; }

        public Action<ProjectLaunchContext?>? LaunchProject { get; set; }

        public ProjectLaunchContext? OpenedLaunchContext { get; private set; }

        public bool HasLaunched { get; private set; }

        internal Task? LastBackgroundCleanupTask { get; private set; }

        internal Task? LastSavePreferencesTask { get; private set; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter(preserveSelectionId: SelectedProject?.Id);
                }
            }
        }

        public ProjectEntryViewModel? SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (SetProperty(ref _selectedProject, value))
                {
                    SyncSelectedProjectDetails();
                    NotifyStateAndCommandsChanged();
                }
            }
        }

        public bool IsEmpty => Projects.Count == 0;

        public string EmptyListMessage => _allProjects.Count == 0
            ? "No projects yet."
            : "No matching projects.";

        public bool IsBusy => _isRefreshingProjects || _isOpeningProject || _isDeletingProject;

        public bool CanAdd => !IsBusy;

        public bool CanOpenWithoutProject => !IsBusy && !HasLaunched;

        public bool CanOpen => _selectedProject != null && !IsBusy && !HasLaunched;

        public bool CanEdit => _selectedProject != null && !IsBusy;

        public bool CanDelete => _selectedProject != null && !IsBusy;

        public bool CanRerunAnalysis =>
            _selectedProject != null
            && !IsBusy
            && !string.IsNullOrWhiteSpace(_selectedProject.Project.ProjectRoot)
            && !_runningAnalysisProjectIds.Contains(_selectedProject.Id);

        public bool CanEditAnalysisPreferences =>
            _selectedProject != null
            && !IsBusy
            && !_runningAnalysisProjectIds.Contains(_selectedProject.Id);

        public string? DetailIconPath => _selectedProject?.IconPath;

        public string DetailName => _selectedProject?.DisplayName ?? "Select a project";

        public string DetailSubtitle => _selectedProject == null
            ? "Project metadata appears here."
            : _selectedProject.Project.UseAutoName
                ? "Automatic game name is enabled."
                : "Custom project name.";

        public string DetailRoot => _selectedProject == null ? "-" : Blank(_selectedProject.Project.ProjectRoot);

        public string DetailLastLoad => _selectedProject == null ? "-" : Blank(_selectedProject.Project.LastLoadPath);

        public string DetailLastExport => _selectedProject == null ? "-" : Blank(_selectedProject.Project.LastExportPath);

        public string DetailDates => _selectedProject == null
            ? "-"
            : $"Created: {ProjectEntryViewModel.FormatDate(_selectedProject.Project.CreatedAtUtc)}  |  " +
              $"Updated: {ProjectEntryViewModel.FormatDate(_selectedProject.Project.UpdatedAtUtc)}  |  " +
              $"Last accessed: {ProjectEntryViewModel.FormatNullableDate(_selectedProject.Project.LastAccessedAtUtc)}";

        public string DetailStats => _selectedProject?.StatsDetail ?? "-";

        public string DetailIndexing => _selectedProject?.IndexingDetail ?? "-";

        public string DetailAnalysis => _selectedProject?.AnalysisDetail ?? "-";

        public bool AllowIl2CppDumper
        {
            get => _allowIl2CppDumper;
            set
            {
                if (SetProperty(ref _allowIl2CppDumper, value) && !_isSyncingSelection)
                {
                    LastSavePreferencesTask = SaveSelectedAnalysisPreferencesAsync();
                }
            }
        }

        public string Il2CppDumperPath
        {
            get => _il2CppDumperPath;
            set => SetProperty(ref _il2CppDumperPath, value ?? string.Empty);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value ?? string.Empty);
        }

        public ICommand OpenCommand => _openCommand;

        public ICommand OpenWithoutProjectCommand => _openWithoutProjectCommand;

        public ICommand NewProjectCommand => _newProjectCommand;

        public ICommand EditProjectCommand => _editProjectCommand;

        public ICommand DeleteProjectCommand => _deleteProjectCommand;

        public ICommand RerunAnalysisCommand => _rerunAnalysisCommand;

        public ICommand BrowseIl2CppDumperCommand => _browseIl2CppDumperCommand;

        public ICommand SaveAnalysisPreferencesCommand => _saveAnalysisPreferencesCommand;

        public ICommand RefreshCommand => _refreshCommand;

        public ICommand CloseCommand => _closeCommand;

        public static string BuildDeleteConfirmationMessage(ProjectEntryViewModel selected)
        {
            ArgumentNullException.ThrowIfNull(selected);
            var project = selected.Project;
            var rootText = string.IsNullOrWhiteSpace(project.ProjectRoot)
                ? "Project root: -"
                : $"Project root: {project.ProjectRoot}";

            return
                $"Delete \"{selected.DisplayName}\" from AssetStudio?\n\n" +
                "This removes the project entry now, then cleans saved settings, preview/decompressed cache folders, SQLite index cache, and copied icons in the background.\n\n" +
                "The real game/project folder is not deleted.\n\n" +
                rootText;
        }

        public async Task RefreshProjectsAsync(string? selectProjectId = null)
        {
            var refreshVersion = ++_refreshVersion;
            _isRefreshingProjects = true;
            NotifyStateAndCommandsChanged();
            StatusText = "Loading projects...";

            try
            {
                var loaded = await Task.Run(() => _store.GetProjects()
                    .Select(project =>
                    {
                        if (project.AnalysisSummary.Status == ProjectAnalysisStatus.Pending
                            && !string.IsNullOrWhiteSpace(project.ProjectRoot))
                        {
                            var cached = _store.LoadCachedProjectAnalysisReport(project.ProjectRoot);
                            if (cached != null)
                            {
                                project.AnalysisSummary = ProjectAnalysisSummary.FromReport(cached);
                            }
                        }

                        return (Project: project, IndexingState: _store.LoadLatestIndexingState(project.ProjectRoot));
                    })
                    .ToList());

                if (refreshVersion != _refreshVersion)
                {
                    return;
                }

                var previousSelectionId = selectProjectId ?? SelectedProject?.Id;
                _allProjects.Clear();
                foreach (var entry in loaded)
                {
                    _allProjects.Add(new ProjectEntryViewModel(entry.Project, entry.IndexingState));
                }

                ApplyFilter(previousSelectionId);
                StatusText = $"Project database: {_store.DatabasePath}";
            }
            finally
            {
                if (refreshVersion == _refreshVersion)
                {
                    _isRefreshingProjects = false;
                    NotifyStateAndCommandsChanged();
                }
            }
        }

        public async Task AddProjectAsync()
        {
            if (IsBusy || EditProjectAsync == null)
            {
                return;
            }

            var result = await EditProjectAsync(null);
            if (result == null)
            {
                return;
            }

            try
            {
                await Task.Run(() => _store.SaveProject(result));
                await RefreshProjectsAsync(result.Id);
                StatusText = $"Project added: {result.DisplayName}";
            }
            catch (Exception ex)
            {
                ShowError?.Invoke($"Unable to save project:\n{ex.Message}", "Project manager");
            }
        }

        public async Task EditSelectedProjectAsync()
        {
            if (IsBusy || EditProjectAsync == null)
            {
                return;
            }

            var selected = SelectedProject;
            if (selected == null)
            {
                return;
            }

            var result = await EditProjectAsync(selected.Project);
            if (result == null)
            {
                return;
            }

            try
            {
                await Task.Run(() => _store.SaveProject(result));
                await RefreshProjectsAsync(result.Id);
                StatusText = $"Project updated: {result.DisplayName}";
            }
            catch (Exception ex)
            {
                ShowError?.Invoke($"Unable to update project:\n{ex.Message}", "Project manager");
            }
        }

        public async Task DeleteSelectedProjectAsync()
        {
            if (_isDeletingProject || _isOpeningProject || _isRefreshingProjects)
            {
                return;
            }

            var selected = SelectedProject;
            if (selected == null)
            {
                return;
            }

            if (ConfirmDeleteAsync != null && !await ConfirmDeleteAsync(selected))
            {
                return;
            }

            try
            {
                _isDeletingProject = true;
                NotifyStateAndCommandsChanged();

                var name = selected.DisplayName;
                var projectId = selected.Id;
                var selectedIndex = Projects.IndexOf(selected);
                var cleanup = await Task.Run(() => _store.RemoveProjectEntry(projectId));

                _allProjects.Remove(selected);
                Projects.Remove(selected);
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyListMessage));

                if (Projects.Count > 0)
                {
                    var nextIndex = selectedIndex < 0 ? 0 : Math.Clamp(selectedIndex, 0, Projects.Count - 1);
                    SelectedProject = Projects[nextIndex];
                }
                else
                {
                    SelectedProject = null;
                }

                if (cleanup == null)
                {
                    StatusText = "Project was already removed.";
                    return;
                }

                StatusText = $"Removed project: {name}. Cache cleanup is running in the background.";
                LastBackgroundCleanupTask = CleanupRemovedProjectInBackgroundAsync(cleanup);
            }
            catch (Exception ex)
            {
                ShowError?.Invoke($"Unable to delete project:\n{ex.Message}", "Project manager");
            }
            finally
            {
                _isDeletingProject = false;
                NotifyStateAndCommandsChanged();
            }
        }

        public async Task OpenSelectedProjectAsync()
        {
            if (_isOpeningProject || _isDeletingProject || _isRefreshingProjects || HasLaunched)
            {
                return;
            }

            var selected = SelectedProject;
            if (selected == null)
            {
                return;
            }

            await OpenProjectByIdAsync(selected.Id);
        }

        public async Task OpenProjectByIdAsync(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId) || _isOpeningProject || _isDeletingProject || HasLaunched)
            {
                return;
            }

            var launched = false;
            try
            {
                _isOpeningProject = true;
                NotifyStateAndCommandsChanged();
                StatusText = "Opening project...";

                var project = await Task.Run(() =>
                {
                    _store.TouchProject(projectId);
                    return _store.GetProject(projectId);
                });

                if (project == null)
                {
                    await RefreshProjectsAsync();
                    StatusText = "Project no longer exists.";
                    return;
                }

                var settings = await Task.Run(() => _store.LoadProjectSettings(project));
                var context = new ProjectLaunchContext(_store, project, settings);
                CompleteLaunch(context);
                launched = HasLaunched;
            }
            catch (Exception ex)
            {
                ShowError?.Invoke($"Unable to open project:\n{ex.Message}", "Project manager");
            }
            finally
            {
                if (!launched)
                {
                    _isOpeningProject = false;
                    NotifyStateAndCommandsChanged();
                }
            }
        }

        public void OpenWithoutProject()
        {
            if (IsBusy || HasLaunched)
            {
                return;
            }

            CompleteLaunch(null);
        }

        public async Task BrowseIl2CppDumperAsync()
        {
            if (!CanEditAnalysisPreferences || PickIl2CppDumperPathAsync == null)
            {
                return;
            }

            var path = await PickIl2CppDumperPathAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Il2CppDumperPath = path;
            await SaveSelectedAnalysisPreferencesAsync();
        }

        public async Task SaveSelectedAnalysisPreferencesAsync()
        {
            var selected = SelectedProject;
            if (selected == null)
            {
                return;
            }

            var allow = AllowIl2CppDumper;
            var path = string.IsNullOrWhiteSpace(Il2CppDumperPath)
                ? null
                : Il2CppDumperPath.Trim();

            selected.Project.AllowAutoIl2CppDumper = allow;
            selected.Project.Il2CppDumperPath = path;

            var projectId = selected.Id;
            try
            {
                await Task.Run(() => _store.SaveProjectAnalysisPreferences(projectId, allow, path));
                StatusText = allow
                    ? "Saved optional Il2CppDumper execution preference (enabled)."
                    : "Saved optional Il2CppDumper execution preference (disabled).";
            }
            catch (Exception ex)
            {
                StatusText = $"Unable to save analysis preference: {ex.Message}";
            }
        }

        public async Task RerunSelectedAnalysisAsync()
        {
            var selected = SelectedProject;
            if (selected == null)
            {
                return;
            }

            var project = selected.Project;
            if (string.IsNullOrWhiteSpace(project.ProjectRoot))
            {
                StatusText = "Set a project root before running analysis.";
                return;
            }

            if (!_runningAnalysisProjectIds.Add(project.Id))
            {
                return;
            }

            NotifyStateAndCommandsChanged();
            StatusText = $"Analyzing {selected.DisplayName}...";

            try
            {
                var options = new ProjectAnalysisOptions
                {
                    ForceRefresh = true,
                    AllowExternalIl2CppDumper = project.AllowAutoIl2CppDumper,
                    Il2CppDumperExecutablePath = project.Il2CppDumperPath
                };

                var result = await _store.AnalysisService.AnalyzeProjectAsync(project.ProjectRoot, options);
                await Task.Run(() => _store.SaveProjectAnalysisReport(project.Id, result.Report));

                var summary = ProjectAnalysisSummary.FromReport(result.Report);
                selected.UpdateAnalysisSummary(summary);
                NotifyDetailPropertiesChanged();
                StatusText = result.Report.Status == ProjectAnalysisStatus.Ready
                    ? $"Analysis complete for {selected.DisplayName}: {summary.RuntimeBadge}"
                    : $"Analysis failed for {selected.DisplayName}: {result.Report.ErrorMessage}";
            }
            catch (Exception ex)
            {
                StatusText = $"Analysis failed: {ex.Message}";
            }
            finally
            {
                _runningAnalysisProjectIds.Remove(project.Id);
                NotifyStateAndCommandsChanged();
            }
        }

        public void Close()
        {
            RequestClose?.Invoke(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _store.AnalysisService.AnalysisUpdated -= OnProjectAnalysisUpdated;
        }

        public void CancelLaunch()
        {
            if (!HasLaunched && !_isOpeningProject && OpenedLaunchContext == null)
            {
                return;
            }

            OpenedLaunchContext = null;
            HasLaunched = false;
            _isOpeningProject = false;
            NotifyStateAndCommandsChanged();
        }

        private void CompleteLaunch(ProjectLaunchContext? context)
        {
            OpenedLaunchContext = context;
            HasLaunched = true;
            NotifyStateAndCommandsChanged();
            try
            {
                LaunchProject?.Invoke(context);
            }
            catch
            {
                CancelLaunch();
                throw;
            }

            if (HasLaunched)
            {
                RequestClose?.Invoke(true);
            }
        }

        private async Task CleanupRemovedProjectInBackgroundAsync(ProjectRemovalCleanup cleanup)
        {
            try
            {
                await Task.Run(() => _store.CleanupRemovedProject(cleanup));
                if (!_disposed)
                {
                    _dispatchToUi(() =>
                    {
                        if (!_disposed)
                        {
                            StatusText = $"Finished cache cleanup for: {cleanup.DisplayName}";
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    _dispatchToUi(() =>
                    {
                        if (!_disposed)
                        {
                            StatusText = $"Project removed, but background cleanup failed: {ex.Message}";
                        }
                    });
                }
            }
        }

        private void ApplyFilter(string? preserveSelectionId)
        {
            var filtered = _allProjects.Where(entry => entry.MatchesSearch(_searchText)).ToList();

            Projects.Clear();
            foreach (var entry in filtered)
            {
                Projects.Add(entry);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyListMessage));

            ProjectEntryViewModel? nextSelection = null;
            if (!string.IsNullOrWhiteSpace(preserveSelectionId))
            {
                nextSelection = Projects.FirstOrDefault(x => string.Equals(x.Id, preserveSelectionId, StringComparison.Ordinal));
            }

            if (nextSelection == null && Projects.Count > 0)
            {
                nextSelection = Projects[0];
            }

            if (!ReferenceEquals(_selectedProject, nextSelection))
            {
                SelectedProject = nextSelection;
            }
            else
            {
                SyncSelectedProjectDetails();
                NotifyStateAndCommandsChanged();
            }
        }

        private void OnProjectAnalysisUpdated(string projectRoot, ProjectAnalysisReport report)
        {
            if (_disposed)
            {
                return;
            }

            var summary = ProjectAnalysisSummary.FromReport(report);
            _dispatchToUi(() =>
            {
                if (_disposed)
                {
                    return;
                }

                var updatedSelected = false;
                foreach (var item in _allProjects)
                {
                    if (string.IsNullOrWhiteSpace(item.Project.ProjectRoot))
                    {
                        continue;
                    }

                    if (string.Equals(
                        ProjectRuntimeDetector.ComputeProjectHash(item.Project.ProjectRoot),
                        report.ProjectHash,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        item.UpdateAnalysisSummary(summary);
                        if (ReferenceEquals(item, _selectedProject))
                        {
                            updatedSelected = true;
                        }
                    }
                }

                if (updatedSelected)
                {
                    NotifyDetailPropertiesChanged();
                }
            });
        }

        private void SyncSelectedProjectDetails()
        {
            _isSyncingSelection = true;
            try
            {
                if (_selectedProject == null)
                {
                    AllowIl2CppDumper = false;
                    Il2CppDumperPath = string.Empty;
                }
                else
                {
                    AllowIl2CppDumper = _selectedProject.Project.AllowAutoIl2CppDumper;
                    Il2CppDumperPath = _selectedProject.Project.Il2CppDumperPath ?? string.Empty;
                }
            }
            finally
            {
                _isSyncingSelection = false;
            }

            NotifyDetailPropertiesChanged();
        }

        private void NotifyDetailPropertiesChanged()
        {
            OnPropertyChanged(nameof(DetailIconPath));
            OnPropertyChanged(nameof(DetailName));
            OnPropertyChanged(nameof(DetailSubtitle));
            OnPropertyChanged(nameof(DetailRoot));
            OnPropertyChanged(nameof(DetailLastLoad));
            OnPropertyChanged(nameof(DetailLastExport));
            OnPropertyChanged(nameof(DetailDates));
            OnPropertyChanged(nameof(DetailStats));
            OnPropertyChanged(nameof(DetailIndexing));
            OnPropertyChanged(nameof(DetailAnalysis));
        }

        private void NotifyStateAndCommandsChanged()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(CanOpenWithoutProject));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanRerunAnalysis));
            OnPropertyChanged(nameof(CanEditAnalysisPreferences));

            _openCommand.NotifyCanExecuteChanged();
            _openWithoutProjectCommand.NotifyCanExecuteChanged();
            _newProjectCommand.NotifyCanExecuteChanged();
            _editProjectCommand.NotifyCanExecuteChanged();
            _deleteProjectCommand.NotifyCanExecuteChanged();
            _rerunAnalysisCommand.NotifyCanExecuteChanged();
            _browseIl2CppDumperCommand.NotifyCanExecuteChanged();
            _saveAnalysisPreferencesCommand.NotifyCanExecuteChanged();
        }

        private static string Blank(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }
}
