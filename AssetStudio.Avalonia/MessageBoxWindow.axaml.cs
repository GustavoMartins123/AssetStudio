using System;
using System.Threading.Tasks;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace AssetStudio.Avalonia;

public partial class MessageBoxWindow : Window
{
    public MessageBoxWindow()
        : this(MessageBoxViewModel.CreateOk(string.Empty))
    {
    }

    public MessageBoxWindow(MessageBoxViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        Width = ViewModel.DialogWidth;
        Height = ViewModel.DialogHeight;
        MinWidth = ViewModel.DialogMinWidth;
        MinHeight = ViewModel.DialogMinHeight;
        CanResize = ViewModel.CanResize;

        ViewModel.CopyToClipboardAsync ??= CopyTextToClipboardAsync;
        ViewModel.RequestClose += OnRequestClose;
        ViewModel.LinesAppended += OnLinesAppended;

        DataContext = ViewModel;
    }

    public MessageBoxViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ViewModel.LinesAppended -= OnLinesAppended;
        base.OnClosed(e);
    }

    private void OnRequestClose(MessageBoxResult result)
    {
        Close(result);
    }

    private void OnLinesAppended()
    {
        if (ViewModel.Lines.Count > 0)
        {
            LinesListBox.ScrollIntoView(ViewModel.Lines.Count - 1);
        }
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
