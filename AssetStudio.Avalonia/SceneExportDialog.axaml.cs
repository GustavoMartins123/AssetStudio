using System;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;

namespace AssetStudio.Avalonia;

public sealed partial class SceneExportDialog : Window
{
    public SceneExportDialog()
        : this(new SceneExportDialogViewModel())
    {
    }

    public SceneExportDialog(SceneExportDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public SceneExportDialogViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnRequestClose(SceneExportDialogResult? result)
    {
        Close(result);
    }
}
