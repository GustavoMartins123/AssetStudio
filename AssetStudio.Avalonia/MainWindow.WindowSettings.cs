using Avalonia.Controls;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void ResetForm()
    {
        lazyIndexingSessionId++;
        ViewModel.LoadingService.StopIndexing();
        CancelProjectAnalysis();
        HideProjectAnalysisBanner();
        _previewRequestCoordinator.CancelCurrent();
        _dumpRequestCoordinator.CancelCurrent();
        _referencesRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        _animationRigPreviewCache.Clear();
        treeSearchSession.Invalidate();
        appliedTreeSearchMatches = null;
        ClearPreview();
        ReleaseGlPreviewResources();
        _sqliteCache.ReleaseReadConnections();
        assetsManager.DecompressionCache = null;
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
        Volatile.Write(ref _spriteDependenciesLoadedFor, null);
        SpriteHelper.ClearAtlasCache();
        AssetExportHelper.ClearShaderGuidCache();
        logger.ClearErrors();
        ViewModel.AssetList.Reset();
        lazySourcePathBySerializedFile.Clear();
        lazySourceFileSearchCache.Clear();
        resolvedSourcePathCache.Clear();
        AssetListDataGrid.SelectedItem = null;
        AssetListDataGrid.ItemsSource = visibleAssetItems;
        AssetClassesDataGrid.ItemsSource = visibleAssetClassItems;
        sceneTreeNodes.Clear();
        treeSearchResults.Clear();
        listSearchDebounce?.Cancel();
        listSearchDebounce?.Dispose();
        listSearchDebounce = null;
        assetContextCellText = string.Empty;
        assetContextItem = null;
        lock (assemblyLoader)
        {
            assemblyLoader.Clear();
        }
        ResetScriptDecompilerCaches();
        nextGameObjectSearchIndex = 0;
        SceneTreeView.ItemsSource = null;
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        ClearTextAssetPreview();
        PreviewLabel.IsVisible = true;
        PreviewLabel.Text = "[Preview Panel]";
        ViewModel.ResetSession();
        if (projectContext?.Project != null)
        {
            ViewModel.ProjectSession.SetLoadedProject(projectContext.Project, isLazyMode: assetsManager.LazyLoading);
        }
        HideIndexingProgressPanel();
        ResetFilterTypeMenu();
        StatusStripUpdate("Ready");

        ViewModel.ProjectSession.UpdateWindowTitle(projectContext?.Project.DisplayName);

        _currentlySelectedUniqueID = null;
        isRefreshingFilterList = false;
        isRefreshingClassesList = false;
    }

    private string FormatWindowTitle()
    {
        return ProjectSessionViewModel.FormatWindowTitle(projectContext?.Project.DisplayName);
    }

    private static string FormatLoadedWindowTitle(string? productName, SerializedFile firstFile)
    {
        return ProjectSessionViewModel.FormatLoadedWindowTitle(productName, firstFile);
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
        assetsManager.SpecifyUnityVersion = ViewModel.Options.SpecifyUnityVersion?.Trim() ?? string.Empty;
        assetsManager.AutoLoadCatalogDependencies = ViewModel.Options.AutoLoadCatalogDependencies;
        assetsManager.AutoLoadExternalDependencies = ViewModel.Options.AutoLoadExternalDependencies;
        if (appSettings.SpecifyUnityVersion != assetsManager.SpecifyUnityVersion)
        {
            appSettings.SpecifyUnityVersion = assetsManager.SpecifyUnityVersion;
            SaveAppSettings();
        }
    }

    private void SaveAppSettings()
    {
        ViewModel.Options.ApplyToSettings(appSettings);
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

    private void SetPreviewInfoOverlay(string? text)
    {
        PreviewSession.PreviewMetadataSummary = text ?? string.Empty;
    }

    private void ClearPreviewInfoOverlay()
    {
        PreviewSession.PreviewMetadataSummary = string.Empty;
    }

    private void ClearPreview(string message = "[Preview Panel]")
    {
        _lastPreviewedAssetUniqueId = null;
        PreviewSession.ShaderPreview.Clear();
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
        ClearPreviewCacheBadge();
        HideAnimationPlayback();
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }
        ClearPreviewInfoOverlay();
        AudioReset();
        VideoReset();
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        ResetTextureArrayPreview();
        Volatile.Write(ref _spriteDependenciesLoadedFor, null);
        currentPreviewMesh = null;
        currentPreviewAvatar = null;
        currentPreviewAnimationClip = null;
        currentPreviewAudioClip = null;
        currentPreviewVideoClip = null;
        _previewRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        Interlocked.Increment(ref texturePreviewIdCounter); // Cancel any running background image decoding task
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

    private void ReleaseGlPreviewResources()
    {
        try
        {
            GLPreviewControl?.SetMeshGroup(Array.Empty<Mesh>());
        }
        catch
        {
        }

        try
        {
            if (TextureGLPreview != null)
            {
                using var emptyPixel = new SkiaSharp.SKBitmap(1, 1, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
                TextureGLPreview.SetImage(emptyPixel);
                TextureGLPreview.IsVisible = false;
            }
        }
        catch
        {
        }
    }

    private void OnDisplayAllOptionChanged(bool displayAllEnabled)
    {
        appSettings.DisplayAll = displayAllEnabled;
        SaveAppSettings();
        if (assetsManager.assetsFileList.Count > 0 || (assetsManager.LazyLoading && assetsManager.ProjectIndex.Count > 0))
        {
            BuildAssetStructures();
        }
    }

    private void OnEnablePreviewOptionChanged(bool enablePreviewEnabled)
    {
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        appSettings.EnablePreview = enablePreviewEnabled;
        SaveAppSettings();
        if (!enablePreviewEnabled)
        {
            ClearPreview("Preview disabled");
        }
        else if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            QueuePreviewAsset(selected, immediate: true);
        }
    }

    internal static bool ShouldShowPreviewInfoOverlay(bool displayInfoEnabled, string? previewInfoText)
    {
        return displayInfoEnabled && !string.IsNullOrWhiteSpace(previewInfoText);
    }

    private void OnDisplayInfoOptionChanged(bool showInfo)
    {
        appSettings.DisplayInfo = showInfo;
        SaveAppSettings();
        if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            PreviewLabel.Text = showInfo
                ? $"{selected.TypeString}: {selected.Name}"
                : string.Empty;
            PreviewLabel.IsVisible = showInfo
                && !TextPreviewBox.IsVisible
                && !PreviewSession.HasDialoguePreview
                && (ImagePreviewBox == null || !ImagePreviewBox.IsVisible)
                && (TextureGLPreview == null || !TextureGLPreview.IsVisible)
                && (GLPreviewControl == null || !GLPreviewControl.IsVisible);
        }
    }

    private void OnUseDiskDecompressionCacheOptionChanged(bool enabled)
    {
        appSettings.UseDiskDecompressionCache = enabled;
        SaveAppSettings();

        if (assetsManager.LazyLoading)
        {
            var cacheRoot = GetCurrentCacheFolderPath();
            assetsManager.DecompressionCache = enabled && !string.IsNullOrWhiteSpace(cacheRoot)
                ? new DecompressedBundleCache(gameScopePath: cacheRoot)
                : null;
            BundleFile.CacheDirectory = string.Empty;
        }
        else
        {
            assetsManager.DecompressionCache = null;
        }

        StatusStripUpdate(enabled
            ? "Disk decompression cache (Lazy Mode) enabled."
            : "Disk decompression cache (Lazy Mode) disabled.");
    }

    private void OnAutoLoadCatalogDependenciesOptionChanged(bool enabled)
    {
        appSettings.AutoLoadCatalogDependencies = enabled;
        assetsManager.AutoLoadCatalogDependencies = enabled;
        SaveAppSettings();
        StatusStripUpdate(enabled
            ? "Auto-load Addressables catalog dependencies enabled."
            : "Auto-load Addressables catalog dependencies disabled.");
    }

    private void OnAutoLoadExternalDependenciesOptionChanged(bool enabled)
    {
        appSettings.AutoLoadExternalDependencies = enabled;
        assetsManager.AutoLoadExternalDependencies = enabled;
        SaveAppSettings();
        StatusStripUpdate(enabled
            ? "Auto-load .assets external dependencies (cascade) enabled."
            : "Auto-load .assets external dependencies (cascade) disabled.");
    }

    private void OnShowErrorMessageOptionChanged(bool showErrorMessage)
    {
        logger.ShowErrorMessage = showErrorMessage;
        appSettings.ShowErrorMessage = showErrorMessage;
        SaveAppSettings();
    }

    private async Task SetProjectRootCommandHandlerAsync()
    {
        var selectedFolder = await PickLoadFolderDialogAsync("Select project root");
        if (string.IsNullOrWhiteSpace(selectedFolder)) return;

        assetsManager.ProjectRoot = selectedFolder;
        SaveLoadFolder(assetsManager.ProjectRoot);
        appSettings.ProjectRoot = assetsManager.ProjectRoot;
        SaveAppSettings();
        StatusStripUpdate($"Project root set to: {assetsManager.ProjectRoot}");
    }

    private async Task ShowExportOptionsCommandHandlerAsync()
    {
        appSettings.ExportOptions.CopyFrom(exportOptions);
        var saved = await ViewModel.DialogService.ShowExportOptionsAsync(appSettings);
        if (!saved) return;

        exportOptions.CopyFrom(appSettings.ExportOptions);
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
                Tag = type,
                Command = ViewModel.ToggleFilterTypeCommand,
                CommandParameter = type
            };
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

    private void ExecuteFilterTypeAllFromUi()
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

    private void ExecuteToggleFilterTypeFromUi(ClassIDType type)
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
