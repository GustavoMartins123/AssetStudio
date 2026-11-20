using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AssetStudio.Avalonia;

public sealed partial class ProjectEditorWindow : Window
{
    private Bitmap? _previewBitmap;

    public ProjectEditorWindow()
        : this(ProjectManagerStore.Shared, null)
    {
    }

    public ProjectEditorWindow(ProjectManagerStore store, ManagedProject? project = null)
        : this(new ProjectEditorViewModel(store, project))
    {
    }

    public ProjectEditorWindow(ProjectEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ViewModel.PickRootFolderAsync ??= PickRootFolderAsync;
        ViewModel.PickIconFileAsync ??= PickIconFileAsync;

        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RequestClose += OnRequestClose;

        UpdateIconPreview(ViewModel.IconPreviewPath);
    }

    public ProjectEditorViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.RequestClose -= OnRequestClose;
        DisposePreviewBitmap();
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectEditorViewModel.IconPreviewPath))
        {
            UpdateIconPreview(ViewModel.IconPreviewPath);
        }
    }

    private void OnRequestClose(bool saved)
    {
        Close(saved ? ViewModel.Result : null);
    }

    private async Task<string?> PickRootFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select project root",
            AllowMultiple = false
        });

        return folders == null || folders.Count == 0
            ? null
            : folders[0].Path.LocalPath;
    }

    private async Task<string?> PickIconFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select project icon",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.ico", "*.bmp", "*.webp", "*.exe" }
                }
            }
        });

        return files == null || files.Count == 0
            ? null
            : files[0].Path.LocalPath;
    }

    private void UpdateIconPreview(string? path)
    {
        DisposePreviewBitmap();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            _previewBitmap = new Bitmap(stream);
            IconPreviewImage.Source = _previewBitmap;
        }
        catch
        {
            DisposePreviewBitmap();
        }
    }

    private void DisposePreviewBitmap()
    {
        IconPreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }
}
