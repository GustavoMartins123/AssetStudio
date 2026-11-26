using System;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Threading;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private GlobalSearchDialog? _activeGlobalSearchDialog;
    private string? _activeGlobalSearchContextKey;

    internal async Task OpenBuildDiffDialogCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var baselineFolder = GetCurrentCacheFolderPath();
        var vm = new BuildDiffDialogViewModel(
            sqliteCache: _sqliteCache,
            loadedBaselineIndex: assetsManager.ProjectIndex,
            initialBaselineFolder: baselineFolder);
        var dialog = new BuildDiffDialog(vm);

        if (IsVisible)
        {
            await dialog.ShowDialog(this);
        }
        else
        {
            dialog.Show();
        }
    }

    internal Task OpenGlobalSearchDialogCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return Task.CompletedTask;
        }

        var dialog = EnsureGlobalSearchDialog();
        dialog.ViewModel.SelectedMode = GlobalSearchDialogMode.GlobalAssetSearch;
        ShowOrActivateGlobalSearchDialog(dialog);
        return Task.CompletedTask;
    }

    internal async Task QuerySelectedAssetGraphUsageCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var selectedItem = AssetListDataGrid.SelectedItem as AssetItem ?? assetContextItem;
        if (selectedItem == null)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Semantic Usage Graph",
                "Select an asset in the Asset List first to query its semantic usage graph.");
            return;
        }

        var uniqueId = selectedItem.Handle != null && !string.IsNullOrWhiteSpace(selectedItem.Handle.UniqueID)
            ? selectedItem.Handle.UniqueID
            : selectedItem.UniqueID;

        var dialog = EnsureGlobalSearchDialog();
        dialog.ViewModel.ConfigureGraphUsageForAsset(uniqueId, selectedItem.Type, selectedItem.Name);
        ShowOrActivateGlobalSearchDialog(dialog);
        await dialog.ViewModel.ExecuteQueryCommand.ExecuteAsync();
    }

    private GlobalSearchDialog EnsureGlobalSearchDialog()
    {
        var folderPath = GetCurrentCacheFolderPath();
        var signature = currentScanResult != null && !string.IsNullOrWhiteSpace(folderPath)
            ? _sqliteCache.GetFolderSignature(currentScanResult)
            : null;
        var contextKey = $"{folderPath}|{signature}|{assetsManager.ProjectIndex.Count}";

        if (_activeGlobalSearchDialog != null)
        {
            if (string.Equals(_activeGlobalSearchContextKey, contextKey, StringComparison.Ordinal))
            {
                return _activeGlobalSearchDialog;
            }

            try
            {
                _activeGlobalSearchDialog.Close();
            }
            catch
            {
            }

            _activeGlobalSearchDialog = null;
        }

        var vm = new GlobalSearchDialogViewModel(
            projectIndex: assetsManager.ProjectIndex,
            assetsManager: assetsManager,
            sqliteCache: _sqliteCache,
            folderPath: folderPath,
            signature: signature);

        var dialog = new GlobalSearchDialog(vm);

        void OnNavigateResult(GlobalSearchResultItem item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _ = NavigateFromAnalysisResultAsync(item);
            });
        }

        void OnDialogClosed(object? sender, EventArgs args)
        {
            vm.NavigateToResultRequested -= OnNavigateResult;
            dialog.Closed -= OnDialogClosed;
            if (ReferenceEquals(_activeGlobalSearchDialog, dialog))
            {
                _activeGlobalSearchDialog = null;
                _activeGlobalSearchContextKey = null;
            }
        }

        vm.NavigateToResultRequested += OnNavigateResult;
        dialog.Closed += OnDialogClosed;

        _activeGlobalSearchDialog = dialog;
        _activeGlobalSearchContextKey = contextKey;
        return dialog;
    }

    private void ShowOrActivateGlobalSearchDialog(GlobalSearchDialog dialog)
    {
        if (!dialog.IsVisible)
        {
            if (IsVisible)
            {
                dialog.Show(this);
            }
            else
            {
                dialog.Show();
            }
        }
        else
        {
            dialog.Activate();
        }
    }

    private async Task NavigateFromAnalysisResultAsync(GlobalSearchResultItem item)
    {
        if (item == null || !item.IsNavigable || IsWindowClosingOrClosed)
        {
            return;
        }

        var referenceItem = new AssetReferenceItem
        {
            AssetUniqueId = item.AssetUniqueId,
            Name = item.Name,
            AssetType = item.AssetType,
            TypeName = item.TypeName,
            Relation = item.MatchReason,
            PathId = item.PathId,
            SerializedFileName = item.SourceFile,
            Direction = AssetReferenceDirection.ReferencedBy
        };

        await NavigateToReferencedAssetCoreAsync(referenceItem, recordHistory: true);
    }
}
