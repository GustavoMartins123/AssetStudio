using System;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;

namespace AssetStudio.Avalonia;

public sealed partial class BatchAnimationExportWindow : Window
{
    public BatchAnimationExportWindow()
        : this(new BatchAnimationExportViewModel("3D Model", Array.Empty<BatchAnimationClipItemViewModel>()))
    {
    }

    public BatchAnimationExportWindow(BatchAnimationExportViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public BatchAnimationExportViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnRequestClose(bool confirmed)
    {
        Close(confirmed ? ViewModel.Result : null);
    }
}
