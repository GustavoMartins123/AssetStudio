using AssetStudio;
using AssetStudio.Avalonia.Services;
using System.Collections.Generic;
using System.Threading;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private CancellationTokenSource? preloaderCts;
    private readonly object preloaderLock = new();
    private readonly HashSet<string> preloadedUniqueIds = new();
    private const int AssetPreloadWindowSize = ProjectLoadingService.AssetPreloadWindowSize;

    private void PreprocessPreloadedAssetForPreview(AssetItem item, CancellationToken token) =>
        ProjectLoadingService.PreprocessPreloadedAssetForPreview(item, token);

    private void UnloadAsset(AssetHandle handle) =>
        LazyAssetResolutionService.UnloadAsset(handle);

    private void UpdatePreloadWindow(AssetItem currentItem)
    {
        if (ShouldSkipPreloadWindowForVideo(currentItem))
        {
            lock (preloaderLock)
            {
                preloaderCts?.Cancel();
            }
            return;
        }

        ProjectLoadingService.UpdatePreloadWindow(
            assetsManager,
            IsProgressiveIndexingActive(),
            visibleAssetItems,
            currentItem,
            preloaderLock,
            preloadedUniqueIds,
            ref preloaderCts,
            EnsureLazyAssetReadyForPreload,
            AssetPreloadWindowSize,
            shouldPausePreload: () => isBuildingLazyConnections
                || Volatile.Read(ref foregroundLazyLoadCount) > 0
                || _activeMeshMaterialsLoading
                || IsVideoPreviewBusyForPreload()
                || ShouldPauseBackgroundWork());
    }
}
