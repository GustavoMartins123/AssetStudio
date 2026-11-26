using System;
using System.IO;
using System.Threading.Tasks;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AssetStudio.Avalonia;

public sealed partial class BuildDiffDialog : Window
{
    public BuildDiffDialog()
        : this(new BuildDiffDialogViewModel())
    {
    }

    public BuildDiffDialog(BuildDiffDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.PickBaselineFolderAsync = () => PickFolderAsync("Select Baseline Build Folder", ViewModel.BaselineFolder);
        ViewModel.PickTargetFolderAsync = () => PickFolderAsync("Select Target Build Folder", ViewModel.TargetFolder);
        ViewModel.PickExportFileAsync = PickExportFileAsync;
        ViewModel.RequestClose += OnRequestClose;
    }

    public BuildDiffDialogViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ViewModel.PickBaselineFolderAsync = null;
        ViewModel.PickTargetFolderAsync = null;
        ViewModel.PickExportFileAsync = null;
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnRequestClose()
    {
        Close();
    }

    private async Task<string?> PickFolderAsync(string title, string? suggestedDirectory)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetStartFolderAsync(topLevel, suggestedDirectory)
        };

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (folders == null || folders.Count == 0)
        {
            return null;
        }

        return folders[0].Path.LocalPath;
    }

    private async Task<string?> PickExportFileAsync(string suggestedFileName, string extension)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var cleanExt = (extension ?? string.Empty).Trim().TrimStart('.');
        var fileType = string.Equals(cleanExt, "json", StringComparison.OrdinalIgnoreCase)
            ? new FilePickerFileType("JSON Report") { Patterns = new[] { "*.json" } }
            : new FilePickerFileType("CSV Report") { Patterns = new[] { "*.csv" } };

        var options = new FilePickerSaveOptions
        {
            Title = $"Export Build Diff ({cleanExt.ToUpperInvariant()})",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = cleanExt,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { fileType }
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        return file?.Path.LocalPath;
    }

    private static async Task<IStorageFolder?> TryGetStartFolderAsync(TopLevel topLevel, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        try
        {
            var absolute = Path.GetFullPath(path).Replace('\\', '/');
            if (!absolute.StartsWith("/", StringComparison.Ordinal))
            {
                absolute = "/" + absolute;
            }

            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri("file://" + absolute));
        }
        catch
        {
            return null;
        }
    }
}
