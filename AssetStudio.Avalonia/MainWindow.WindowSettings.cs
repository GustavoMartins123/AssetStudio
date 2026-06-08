using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void ResetForm()
    {
        BundleFile.CacheDirectory = "";
        lock (preloaderLock)
        {
            preloaderCts?.Cancel();
            preloaderCts?.Dispose();
            preloaderCts = null;
            preloadedUniqueIds.Clear();
        }
        lock (previewCacheLock)
        {
            meshToMaterialsCache = null;
            meshAssociatedRenderersCache = null;
            meshSourceTypesCache = null;
            materialMainTextureCache = null;
            materialPreviewMaterialCache = null;
            materialTextureSlotsCache = null;
            objectToAssetItemCache = null;
            animationClipAvatarCache = null;
            avatarMeshCache = null;
            avatarMeshesCache = null;
            meshAvatarCache = null;
            meshSkinnedRenderersCache = null;
            animationClipTransformBindingsCache = null;
        }
        logger.ClearErrors();
        exportableAssets.Clear();
        visibleAssets.Clear();
        visibleAssetItems.Clear();
        lazyAssetItemsByHandleId.Clear();
        exportableAssetHandleIds.Clear();
        exportableAssetTypes.Clear();
        lazySourcePathBySerializedFile.Clear();
        lazySourceFileSearchCache.Clear();
        lazyAssetItemOrdinal = 0;
        assetClassItems.Clear();
        visibleAssetClassItems.Clear();
        classFilterOverride = null;
        if (ClearClassFilterButton != null)
        {
            ClearClassFilterButton.IsVisible = false;
        }
        AssetListDataGrid.SelectedItem = null;
        AssetListDataGrid.ItemsSource = visibleAssetItems;
        AssetClassesDataGrid.ItemsSource = null;
        sceneTreeNodes.Clear();
        treeSearchResults.Clear();
        listSearchDebounce?.Cancel();
        assetListSortMember = null;
        assetListSortDescending = false;
        assetContextCellText = string.Empty;
        assetContextItem = null;
        assemblyLoader.Clear();
        nextGameObjectSearchIndex = 0;
        SceneTreeView.ItemsSource = null;
        DumpTextBox.Text = string.Empty;
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        ClearTextAssetPreview();
        classSearch.Text = string.Empty;
        PreviewLabel.IsVisible = true;
        PreviewLabel.Text = "[Preview Panel]";
        ViewModel.LoadingService.StopIndexing();
        ViewModel.IsIndexingActive = false;
        ViewModel.LoadingProgress = 0;
        ResetFilterTypeMenu();
        StatusStripUpdate("Ready");

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Title = projectContext == null
            ? $"AssetStudio v{version}"
            : $"AssetStudio v{version} - {projectContext.Project.DisplayName}";

        _currentlySelectedUniqueID = null;
        isRefreshingFilterList = false;
        isRefreshingClassesList = false;
    }

    private static T? FindVisualChild<T>(global::Avalonia.Visual? visual) where T : class
    {
        if (visual == null) return null;
        if (visual is T target) return target;
        foreach (var child in global::Avalonia.VisualTree.VisualExtensions.GetVisualChildren(visual))
        {
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void ApplyUnityVersionOption()
    {
        assetsManager.SpecifyUnityVersion = ViewModel.SpecifyUnityVersion?.Trim() ?? string.Empty;
        if (appSettings.SpecifyUnityVersion != assetsManager.SpecifyUnityVersion)
        {
            appSettings.SpecifyUnityVersion = assetsManager.SpecifyUnityVersion;
            SaveAppSettings();
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            if (projectContext != null)
            {
                projectContext.Store.SaveProjectSettings(projectContext.Project.Id, appSettings);
            }
            else
            {
                ProjectManagerStore.Shared.SaveGlobalSettings(appSettings);
            }
        }
        catch
        {
            appSettings.Save();
        }
    }

    private void ClearPreview(string message = "[Preview Panel]")
    {
        Stop2DAnimation();
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        TextPreviewBox.FontFamily = global::Avalonia.Media.FontFamily.Default;
        TextPreviewBox.FontSize = 14;
        ClearTextAssetPreview();
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.Source = null;
            ImagePreviewBox.IsVisible = false;
        }
        if (GLPreviewControl != null)
        {
            GLPreviewControl.StopAnimation();
            GLPreviewControl.IsVisible = false;
        }
        ApplyAvatarPreviewControlSettings();
        HidePreviewGeometryControls();
        ClearMeshMaterialControls();
        ClearPreviewCandidateControls();
        if (AnimationPlaybackPanel != null)
        {
            AnimationPlaybackPanel.IsVisible = false;
        }
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }
        if (PreviewInfoBorder != null)
        {
            PreviewInfoBorder.IsVisible = false;
        }
        if (PreviewInfoOverlay != null)
        {
            PreviewInfoOverlay.Text = string.Empty;
        }
        if (AudioPanel != null)
        {
            AudioPanel.IsVisible = false;
            AudioReset();
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            VideoReset();
        }
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        currentPreviewAudioClip = null;
        currentPreviewVideoClip = null;
        texturePreviewIdCounter++; // Cancel any running background image decoding task
        previewDebounce?.Cancel();
        previewDebounce?.Dispose();
        previewDebounce = null;
        for (int i = 0; i < 4; i++)
        {
            textureChannels[i] = true;
        }
        PreviewLabel.Text = message;
        PreviewLabel.IsVisible = true;
    }

    private void DisplayAll_Click(object? sender, RoutedEventArgs e)
    {
        appSettings.DisplayAll = displayAll.IsChecked == true;
        SaveAppSettings();
        if (assetsManager.assetsFileList.Count > 0)
        {
            BuildAssetStructures();
        }
    }

    private void EnablePreview_Click(object? sender, RoutedEventArgs e)
    {
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        appSettings.EnablePreview = enablePreview.IsChecked == true;
        SaveAppSettings();
        if (enablePreview.IsChecked != true)
        {
            ClearPreview("Preview disabled");
        }
        else if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            QueuePreviewAsset(selected, immediate: true);
        }
    }

    private void DisplayInfo_Click(object? sender, RoutedEventArgs e)
    {
        appSettings.DisplayInfo = displayInfo.IsChecked == true;
        SaveAppSettings();
        if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            PreviewLabel.Text = displayInfo.IsChecked == true
                ? $"{selected.TypeString}: {selected.Name}"
                : string.Empty;
            PreviewLabel.IsVisible = displayInfo.IsChecked == true && !TextPreviewBox.IsVisible && !TextAssetPreviewPanel.IsVisible && (ImagePreviewBox == null || !ImagePreviewBox.IsVisible);
            if (PreviewInfoBorder != null)
            {
                PreviewInfoBorder.IsVisible = displayInfo.IsChecked == true && (currentPreviewTexture != null || currentPreviewSprite != null);
            }
        }
    }

    private void TogglePreviewInfoBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (PreviewInfoScroll != null && TogglePreviewInfoBtn != null)
        {
            bool isVisible = PreviewInfoScroll.IsVisible;
            PreviewInfoScroll.IsVisible = !isVisible;
            TogglePreviewInfoBtn.Content = !isVisible ? "▼" : "▲";
        }
    }

    private async void SetProjectRoot_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select project root"));
        if (folders == null || folders.Count == 0) return;

        assetsManager.ProjectRoot = folders[0].Path.LocalPath;
        SaveLoadFolder(assetsManager.ProjectRoot);
        appSettings.ProjectRoot = assetsManager.ProjectRoot;
        SaveAppSettings();
        StatusStripUpdate($"Project root set to: {assetsManager.ProjectRoot}");
    }

    private async void ShowExportOptions_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ExportOptionsWindow(exportOptions.Clone());
        var result = await dialog.ShowDialog<ExportOptionsState?>(this);
        if (result == null) return;

        exportOptions.CopyFrom(result);
        appSettings.ExportOptions.CopyFrom(result);
        SaveAppSettings();
        StatusStripUpdate("Export options updated.");
    }

    private void ResetFilterTypeMenu()
    {
        updatingFilterTypeMenu = true;
        while (filterTypeMenu.Items.Count > 1)
        {
            filterTypeMenu.Items.RemoveAt(1);
        }
        filterTypeAll.IsChecked = true;
        updatingFilterTypeMenu = false;
    }

    private void BuildFilterTypeMenu()
    {
        updatingFilterTypeMenu = true;
        var checkedTypes = GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet();
        bool wasAllChecked = filterTypeAll.IsChecked == true;

        while (filterTypeMenu.Items.Count > 1)
        {
            filterTypeMenu.Items.RemoveAt(1);
        }

        var types = exportableAssetTypes
            .OrderBy(x => x.ToString());

        foreach (var type in types)
        {
            var item = new MenuItem
            {
                Header = type.ToString(),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = checkedTypes.Contains(type),
                Tag = type
            };
            item.Click += FilterType_Click;
            filterTypeMenu.Items.Add(item);
        }

        if (wasAllChecked)
        {
            filterTypeAll.IsChecked = true;
        }
        else
        {
            filterTypeAll.IsChecked = !GetFilterTypeItems().Any(x => x.IsChecked == true);
        }
        updatingFilterTypeMenu = false;
    }

    private void FilterTypeAll_Click(object? sender, RoutedEventArgs e)
    {
        if (updatingFilterTypeMenu) return;

        PrioritizeUserInteraction();
        updatingFilterTypeMenu = true;
        if (filterTypeAll.IsChecked == true)
        {
            foreach (var item in GetFilterTypeItems())
            {
                item.IsChecked = false;
            }
        }
        updatingFilterTypeMenu = false;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void FilterType_Click(object? sender, RoutedEventArgs e)
    {
        if (updatingFilterTypeMenu) return;

        PrioritizeUserInteraction();
        updatingFilterTypeMenu = true;
        filterTypeAll.IsChecked = !GetFilterTypeItems().Any(x => x.IsChecked == true);
        updatingFilterTypeMenu = false;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private IEnumerable<MenuItem> GetFilterTypeItems()
    {
        return filterTypeMenu.Items
            .OfType<MenuItem>()
            .Where(x => x.Tag is ClassIDType);
    }

}