using System;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;

namespace AssetStudio.Avalonia;

public sealed partial class DecryptionKeyDialog : Window
{
    public DecryptionKeyDialog()
        : this(new DecryptionKeyDialogViewModel(
            new DecryptionDialogContext(
                IsInitialLoadPrompt: false,
                ProjectRoot: null,
                ReadableAssetFileCount: 0,
                EncryptedFileCount: 0,
                CoveragePercent: 100.0,
                DetectedBarriers: Array.Empty<StreamEncryptionBarrier>())))
    {
    }

    public DecryptionKeyDialog(DecryptionKeyDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public DecryptionKeyDialogViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnRequestClose(DecryptionKeyDialogResult result)
    {
        Close(result);
    }
}
