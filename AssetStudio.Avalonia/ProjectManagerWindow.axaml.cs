using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using AssetStudio.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AssetStudio.Avalonia;

public partial class ProjectManagerWindow : Window
{
    public ProjectManagerWindow()
        : this(new ProjectManagerViewModel(
            store: ProjectManagerStore.Shared,
            dispatchToUi: action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background)))
    {
    }

    public ProjectManagerWindow(ProjectManagerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://AssetStudio.Avalonia/Assets/as.png"));
            Icon = new WindowIcon(new Bitmap(iconStream));
        }
        catch
        {
        }

        ViewModel.EditProjectAsync ??= ShowProjectEditorAsync;
        ViewModel.ConfirmDeleteAsync ??= ConfirmProjectDeleteAsync;
        ViewModel.PickIl2CppDumperPathAsync ??= PickIl2CppDumperPathAsync;
        ViewModel.ShowError ??= (message, title) => MessageBox.Show(this, message, title);
        ViewModel.LaunchProject ??= LaunchMainWindow;
        ViewModel.RequestClose += OnRequestClose;

        DataContext = ViewModel;
        _ = ViewModel.RefreshProjectsAsync();
    }

    public ProjectManagerViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnRequestClose(bool launched)
    {
        if (!launched)
        {
            Close();
        }
    }

    private void ProjectListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.OpenCommand.CanExecute(null))
        {
            ViewModel.OpenCommand.Execute(null);
        }
    }

    private void Il2CppDumperPathTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SaveAnalysisPreferencesCommand.CanExecute(null))
        {
            ViewModel.SaveAnalysisPreferencesCommand.Execute(null);
        }
    }

    private async Task<ManagedProject?> ShowProjectEditorAsync(ManagedProject? project)
    {
        var editor = new ProjectEditorWindow(ViewModel.Store, project);
        return await editor.ShowDialog<ManagedProject?>(this);
    }

    private async Task<bool> ConfirmProjectDeleteAsync(ProjectEntryViewModel selected)
    {
        var message = ProjectManagerViewModel.BuildDeleteConfirmationMessage(selected);
        return await MessageBox.ShowConfirmationAsync(
            owner: this,
            text: message,
            title: "Delete project",
            confirmLabel: "Delete",
            cancelLabel: "Cancel",
            isDestructive: true);
    }

    private async Task<string?> PickIl2CppDumperPathAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Il2CppDumper executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executables")
                {
                    Patterns = new[] { "*.exe", "*" }
                }
            }
        });

        return files == null || files.Count == 0
            ? null
            : files[0].Path.LocalPath;
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
}

public sealed class ProjectIconPathToBitmapConverter : IValueConverter
{
    private static readonly Lazy<Bitmap?> DefaultIcon = new(LoadDefaultIcon);
    private static readonly ConcurrentDictionary<(string Path, long Ticks), Bitmap?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return DefaultIcon.Value;
        }

        try
        {
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            var bitmap = Cache.GetOrAdd((path, ticks), static key => LoadBitmap(key.Path));
            return bitmap ?? DefaultIcon.Value;
        }
        catch
        {
            return DefaultIcon.Value;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
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

    private static Bitmap? LoadBitmap(string path)
    {
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
}
