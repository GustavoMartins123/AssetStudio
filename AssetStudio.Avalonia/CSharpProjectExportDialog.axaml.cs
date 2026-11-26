using System;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;

namespace AssetStudio.Avalonia;

public sealed partial class CSharpProjectExportDialog : Window
{
    public CSharpProjectExportDialog()
        : this(new CSharpProjectExportDialogViewModel())
    {
    }

    public CSharpProjectExportDialog(CSharpProjectExportDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public CSharpProjectExportDialogViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnRequestClose(CSharpProjectExportDialogResult? result)
    {
        Close(result);
    }
}
