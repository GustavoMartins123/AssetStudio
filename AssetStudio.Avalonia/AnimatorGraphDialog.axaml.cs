using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace AssetStudio.Avalonia;

public sealed class AnimatorGraphEdgePointConverter : IValueConverter
{
    public static readonly AnimatorGraphEdgePointConverter Start = new(isStart: true);
    public static readonly AnimatorGraphEdgePointConverter End = new(isStart: false);

    private readonly bool _isStart;

    private AnimatorGraphEdgePointConverter(bool isStart)
    {
        _isStart = isStart;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AnimatorGraphCanvasEdge edge)
        {
            return _isStart
                ? new Point(edge.StartX, edge.StartY)
                : new Point(edge.EndX, edge.EndY);
        }

        return default(Point);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed partial class AnimatorGraphDialog : Window
{
    public AnimatorGraphDialog()
        : this(new AnimatorGraphViewModel())
    {
    }

    public AnimatorGraphDialog(AnimatorGraphViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.SaveJsonAsync = SaveJsonWithPickerAsync;
        ViewModel.SaveDotAsync = SaveDotWithPickerAsync;
        ViewModel.CopyTextAsync = CopyTextToClipboardAsync;
        ViewModel.RequestClose += OnRequestClose;
    }

    public AnimatorGraphViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ViewModel.SaveJsonAsync = null;
        ViewModel.SaveDotAsync = null;
        ViewModel.CopyTextAsync = null;
        ViewModel.NavigateToClipAsync = null;
        base.OnClosed(e);
    }

    private void OnRequestClose()
    {
        Close();
    }

    private Task SaveJsonWithPickerAsync(string jsonContent)
    {
        var stem = AnimationClipMetadataService.SanitizeFileStem(ViewModel.Document.ControllerName);
        return SaveTextWithPickerAsync(
            title: "Export Animator Graph JSON",
            suggestedFileName: $"{stem}_animator_graph.json",
            defaultExtension: "json",
            fileTypeName: "JSON File",
            pattern: "*.json",
            content: jsonContent);
    }

    private Task SaveDotWithPickerAsync(string dotContent)
    {
        var stem = AnimationClipMetadataService.SanitizeFileStem(ViewModel.Document.ControllerName);
        return SaveTextWithPickerAsync(
            title: "Export Animator Graphviz DOT",
            suggestedFileName: $"{stem}_animator_graph.dot",
            defaultExtension: "dot",
            fileTypeName: "Graphviz DOT File",
            pattern: "*.dot",
            content: dotContent);
    }

    private async Task SaveTextWithPickerAsync(
        string title,
        string suggestedFileName,
        string defaultExtension,
        string fileTypeName,
        string pattern,
        string content)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(fileTypeName) { Patterns = new[] { pattern } }
            }
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        var localPath = file?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        await Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(localPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        });
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
