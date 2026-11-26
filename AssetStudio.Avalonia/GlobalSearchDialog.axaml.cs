using System;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace AssetStudio.Avalonia;

public sealed partial class GlobalSearchDialog : Window
{
    public GlobalSearchDialog()
        : this(new GlobalSearchDialogViewModel())
    {
    }

    public GlobalSearchDialog(GlobalSearchDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.RequestClose += OnRequestClose;
        ResultsListBox.DoubleTapped += OnResultsListBoxDoubleTapped;
    }

    public GlobalSearchDialogViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ResultsListBox.DoubleTapped -= OnResultsListBoxDoubleTapped;
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnResultsListBoxDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.GoToSelectedCommand.CanExecute(null))
        {
            ViewModel.GoToSelectedCommand.Execute(null);
        }
    }

    private void OnRequestClose()
    {
        Close();
    }
}
