using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class ProjectManagerWindow : Window
{
    private readonly ProjectManagerStore _store = ProjectManagerStore.Shared;
    private readonly ObservableCollection<ProjectListItem> _projects = new();
    private Bitmap? _defaultIcon;
    private int _refreshVersion;
    private bool _isRefreshingProjects;
    private bool _isOpeningProject;
    private bool _isDeletingProject;

    public ProjectManagerWindow()
    {
        InitializeComponent();
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://AssetStudio.Avalonia/Assets/as.png"));
            Icon = new WindowIcon(new Bitmap(iconStream));
        }
        catch
        {
        }

        _defaultIcon = LoadDefaultIcon();
        ProjectListBox.ItemsSource = _projects;
        RefreshProjects();
    }

    private async void RefreshProjects(string? selectProjectId = null)
    {
        var refreshVersion = ++_refreshVersion;
        _isRefreshingProjects = true;
        UpdateDetails();
        StatusText.Text = "Loading projects...";

        try
        {
            var projects = await Task.Run(() => _store.GetProjects()
                .Select(project => (Project: project, IndexingState: _store.LoadLatestIndexingState(project.ProjectRoot)))
                .ToList());
            if (refreshVersion != _refreshVersion)
            {
                return;
            }

            _projects.Clear();
            foreach (var project in projects)
            {
                _projects.Add(new ProjectListItem(project.Project, _defaultIcon, project.IndexingState));
            }

            EmptyProjectsText.IsVisible = _projects.Count == 0;

            if (!string.IsNullOrWhiteSpace(selectProjectId))
            {
                ProjectListBox.SelectedItem = _projects.FirstOrDefault(x => x.Project.Id == selectProjectId);
            }
            else if (_projects.Count > 0 && ProjectListBox.SelectedItem == null)
            {
                ProjectListBox.SelectedIndex = 0;
            }

            StatusText.Text = $"Project database: {_store.DatabasePath}";
        }
        finally
        {
            if (refreshVersion == _refreshVersion)
            {
                _isRefreshingProjects = false;
                UpdateDetails();
            }
        }
    }

    private async void AddProject_Click(object? sender, RoutedEventArgs e)
    {
        var editor = new ProjectEditorWindow(_store);
        var result = await editor.ShowDialog<ManagedProject?>(this);
        if (result == null)
        {
            return;
        }

        try
        {
            await Task.Run(() => _store.SaveProject(result));
            RefreshProjects(result.Id);
            StatusText.Text = $"Project added: {result.DisplayName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to save project:\n{ex.Message}", "Project manager");
        }
    }

    private async void EditProject_Click(object? sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProject();
        if (selected == null)
        {
            return;
        }

        var editor = new ProjectEditorWindow(_store, selected.Project);
        var result = await editor.ShowDialog<ManagedProject?>(this);
        if (result == null)
        {
            return;
        }

        try
        {
            await Task.Run(() => _store.SaveProject(result));
            RefreshProjects(result.Id);
            StatusText.Text = $"Project updated: {result.DisplayName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to update project:\n{ex.Message}", "Project manager");
        }
    }

    private async void RemoveProject_Click(object? sender, RoutedEventArgs e)
    {
        if (_isDeletingProject || _isOpeningProject)
        {
            return;
        }

        var selected = GetSelectedProject();
        if (selected == null)
        {
            return;
        }

        if (!await ConfirmProjectDelete(selected))
        {
            return;
        }

        try
        {
            _isDeletingProject = true;
            UpdateDetails();
            var name = selected.DisplayName;
            var projectId = selected.Project.Id;
            var selectedIndex = ProjectListBox.SelectedIndex;
            var cleanup = await Task.Run(() => _store.RemoveProjectEntry(projectId));
            if (cleanup == null)
            {
                _projects.Remove(selected);
                EmptyProjectsText.IsVisible = _projects.Count == 0;
                UpdateDetails();
                StatusText.Text = "Project was already removed.";
                return;
            }

            _projects.Remove(selected);
            EmptyProjectsText.IsVisible = _projects.Count == 0;
            if (_projects.Count > 0)
            {
                ProjectListBox.SelectedIndex = Math.Clamp(selectedIndex, 0, _projects.Count - 1);
            }
            else
            {
                ProjectListBox.SelectedItem = null;
            }

            UpdateDetails();
            StatusText.Text = $"Removed project: {name}. Cache cleanup is running in the background.";
            _ = CleanupRemovedProjectInBackground(cleanup);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to delete project:\n{ex.Message}", "Project manager");
        }
        finally
        {
            _isDeletingProject = false;
            UpdateDetails();
        }
    }

    private async Task<bool> ConfirmProjectDelete(ProjectListItem selected)
    {
        var dialog = new Window
        {
            Title = "Delete project",
            Width = 560,
            Height = 300,
            MinWidth = 460,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Padding = new global::Avalonia.Thickness(18)
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 16
        };

        var project = selected.Project;
        var rootText = string.IsNullOrWhiteSpace(project.ProjectRoot)
            ? "Project root: -"
            : $"Project root: {project.ProjectRoot}";

        var message = new TextBlock
        {
            Text =
                $"Delete \"{selected.DisplayName}\" from AssetStudio?\n\n" +
                "This removes the project entry now, then cleans saved settings, preview/decompressed cache folders, SQLite index cache, and copied icons in the background.\n\n" +
                "The real game/project folder is not deleted.\n\n" +
                rootText,
            TextWrapping = TextWrapping.Wrap
        };
        var messageScroller = new ScrollViewer
        {
            Content = message,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        var deleteButton = new Button
        {
            Content = "Delete",
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.OrangeRed
        };
        deleteButton.Click += (_, _) => dialog.Close(true);

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(deleteButton);

        Grid.SetRow(messageScroller, 0);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(messageScroller);
        grid.Children.Add(buttons);
        dialog.Content = grid;

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task CleanupRemovedProjectInBackground(ProjectRemovalCleanup cleanup)
    {
        try
        {
            await Task.Run(() => _store.CleanupRemovedProject(cleanup));
            if (IsVisible)
            {
                StatusText.Text = $"Finished cache cleanup for: {cleanup.DisplayName}";
            }
        }
        catch (Exception ex)
        {
            if (IsVisible)
            {
                StatusText.Text = $"Project removed, but background cleanup failed: {ex.Message}";
            }
        }
    }

    private void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProject();
        if (selected == null)
        {
            return;
        }

        OpenProject(selected.Project.Id);
    }

    private void OpenWithoutProject_Click(object? sender, RoutedEventArgs e)
    {
        LaunchMainWindow(null);
    }

    private void ProjectListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateDetails();
    }

    private void ProjectListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var selected = GetSelectedProject();
        if (selected != null)
        {
            OpenProject(selected.Project.Id);
        }
    }

    private ProjectListItem? GetSelectedProject()
    {
        return ProjectListBox.SelectedItem as ProjectListItem;
    }

    private void UpdateDetails()
    {
        var selected = GetSelectedProject();
        var busy = _isRefreshingProjects || _isOpeningProject || _isDeletingProject;
        var enabled = selected != null && !busy;
        AddProjectButton.IsEnabled = !busy;
        OpenWithoutProjectButton.IsEnabled = !busy;
        OpenProjectButton.IsEnabled = enabled;
        EditProjectButton.IsEnabled = enabled;
        RemoveProjectButton.IsEnabled = enabled;

        if (selected == null)
        {
            DetailIcon.Source = _defaultIcon;
            DetailName.Text = "Select a project";
            DetailSubtitle.Text = "Project metadata appears here.";
            DetailRoot.Text = "-";
            DetailLastLoad.Text = "-";
            DetailLastExport.Text = "-";
            DetailDates.Text = "-";
            DetailStats.Text = "-";
            DetailIndexing.Text = "-";
            return;
        }

        var project = selected.Project;
        DetailIcon.Source = selected.Icon ?? _defaultIcon;
        DetailName.Text = selected.DisplayName;
        DetailSubtitle.Text = project.UseAutoName
            ? "Automatic game name is enabled."
            : "Custom project name.";
        DetailRoot.Text = Blank(project.ProjectRoot);
        DetailLastLoad.Text = Blank(project.LastLoadPath);
        DetailLastExport.Text = Blank(project.LastExportPath);
        DetailDates.Text =
            $"Created: {FormatDate(project.CreatedAtUtc)}  |  " +
            $"Updated: {FormatDate(project.UpdatedAtUtc)}  |  " +
            $"Last accessed: {FormatNullableDate(project.LastAccessedAtUtc)}";
        DetailStats.Text = selected.StatsDetail;
        DetailIndexing.Text = selected.IndexingDetail;
    }

    private async void OpenProject(string projectId)
    {
        if (_isOpeningProject || _isDeletingProject)
        {
            return;
        }

        var launched = false;
        try
        {
            _isOpeningProject = true;
            UpdateDetails();
            StatusText.Text = "Opening project...";
            var project = await Task.Run(() =>
            {
                _store.TouchProject(projectId);
                return _store.GetProject(projectId);
            });

            if (project == null)
            {
                RefreshProjects();
                StatusText.Text = "Project no longer exists.";
                return;
            }

            var settings = await Task.Run(() => _store.LoadProjectSettings(project));
            launched = true;
            LaunchMainWindow(new ProjectLaunchContext(_store, project, settings));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to open project:\n{ex.Message}", "Project manager");
        }
        finally
        {
            if (!launched)
            {
                _isOpeningProject = false;
                UpdateDetails();
            }
        }
    }

    private void LaunchMainWindow(ProjectLaunchContext? context)
    {
        var mainWindow = context == null ? new MainWindow() : new MainWindow(context);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow;
        }

        mainWindow.Show();
        mainWindow.StartProjectIndexingOnOpen();
        Close();
    }

    private static Bitmap? LoadDefaultIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://AssetStudio.Avalonia/Assets/as.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string FormatNullableDate(DateTime? date)
    {
        return date.HasValue ? FormatDate(date.Value) : "-";
    }

    private static string FormatDate(DateTime date)
    {
        return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private sealed class ProjectListItem
    {
        private readonly ProjectIndexingState? _indexingState;
        private readonly string _indexingListDisplay;

        public ProjectListItem(ManagedProject project, Bitmap? defaultIcon, ProjectIndexingState? indexingState)
        {
            Project = project;
            _indexingState = indexingState;
            _indexingListDisplay = FormatIndexingListDisplay(indexingState);
            Icon = LoadBitmap(project.IconPath) ?? defaultIcon;
        }

        public ManagedProject Project { get; }
        public Bitmap? Icon { get; }
        public string DisplayName => Project.DisplayName;
        public string RootDisplay => string.IsNullOrWhiteSpace(Project.ProjectRoot) ? "No project root" : Project.ProjectRoot;
        public string LastAccessedDisplay => Project.LastAccessedAtUtc.HasValue
            ? "Last opened " + FormatDate(Project.LastAccessedAtUtc.Value)
            : "Never opened";
        public string IndexingListDisplay => _indexingListDisplay;
        public bool HasIndexingDisplay => !string.IsNullOrWhiteSpace(_indexingListDisplay);
        public string IndexingDetail => FormatIndexingDetail(_indexingState);

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

            if (state.ReadFiles.Count > 0)
            {
                lines.Add($"Read file list saved: {state.ReadFiles.Count:N0}");
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

        private static Bitmap? LoadBitmap(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
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
}
