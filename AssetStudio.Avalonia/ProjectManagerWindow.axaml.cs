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

    private void RefreshProjects(string? selectProjectId = null)
    {
        _projects.Clear();
        foreach (var project in _store.GetProjects())
        {
            _projects.Add(new ProjectListItem(project, _defaultIcon));
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

        UpdateDetails();
        StatusText.Text = $"Project database: {_store.DatabasePath}";
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
            _store.SaveProject(result);
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
            _store.SaveProject(result);
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
            var name = selected.DisplayName;
            _store.RemoveProject(selected.Project.Id);
            RefreshProjects();
            StatusText.Text = $"Deleted AssetStudio project data: {name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to delete project:\n{ex.Message}", "Project manager");
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
                "This removes the project entry, saved settings, SQLite index cache, decompressed cache folder, and copied icons.\n\n" +
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
        var enabled = selected != null;
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
    }

    private void OpenProject(string projectId)
    {
        try
        {
            _store.TouchProject(projectId);
            var project = _store.GetProject(projectId);
            if (project == null)
            {
                RefreshProjects();
                StatusText.Text = "Project no longer exists.";
                return;
            }

            var settings = _store.LoadProjectSettings(project);
            LaunchMainWindow(new ProjectLaunchContext(_store, project, settings));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to open project:\n{ex.Message}", "Project manager");
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
        public ProjectListItem(ManagedProject project, Bitmap? defaultIcon)
        {
            Project = project;
            Icon = LoadBitmap(project.IconPath) ?? defaultIcon;
        }

        public ManagedProject Project { get; }
        public Bitmap? Icon { get; }
        public string DisplayName => Project.DisplayName;
        public string RootDisplay => string.IsNullOrWhiteSpace(Project.ProjectRoot) ? "No project root" : Project.ProjectRoot;
        public string LastAccessedDisplay => Project.LastAccessedAtUtc.HasValue
            ? "Last opened " + FormatDate(Project.LastAccessedAtUtc.Value)
            : "Never opened";

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
