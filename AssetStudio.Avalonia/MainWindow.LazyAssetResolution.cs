using AssetStudio;
using AssetStudio.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void EnsureLazyAssetReadyForPreview(AssetItem assetItem)
    {
        LazyAssetResolutionService.EnsureLazyAssetReadyForPreview(
            assetsManager,
            assetItem,
            lazySourcePathBySerializedFile,
            lazySourceFileSearchCache,
            resolvedSourcePathCache,
            ViewModel.LoadingService.PendingFilesToIndex,
            RemovePendingFileFromProgressiveQueue,
            sourcePath => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusStripUpdate($"Loading preview source: {Path.GetFileName(sourcePath)}")),
            (evt, msg) => logger.Log(evt, msg),
            sourcePath =>
            {
                // Preserve semantic preview RAM caches across lazy file loads; only clear full-project unload/Clear Project Cache.
                lock (previewCacheLock)
                {
                    objectToAssetItemCache = null;
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var addedAssets = AppendNewLazyAssetsFromProjectIndex();
                    if (addedAssets > 0)
                    {
                        StatusStripUpdate($"Loaded preview source: {Path.GetFileName(sourcePath)} | Showing {visibleAssets.Count:N0} assets (+{addedAssets:N0})");
                    }
                });
            });
    }

    private void EnsureLazyAssetReadyForPreload(AssetItem assetItem)
    {
        // Background preloading must never overwrite the foreground status bar or wipe preview RAM caches.
        LazyAssetResolutionService.EnsureLazyAssetReadyForPreview(
            assetsManager,
            assetItem,
            lazySourcePathBySerializedFile,
            lazySourceFileSearchCache,
            resolvedSourcePathCache,
            ViewModel.LoadingService.PendingFilesToIndex,
            RemovePendingFileFromProgressiveQueue,
            notifyLoadingSource: null,
            logWarning: null,
            onSourceLoaded: _ =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendNewLazyAssetsFromProjectIndex());
            });
    }

    private AssetStudio.Object? ResolveSemanticRelationHandleForPreview(AssetHandle handle) =>
        LazyAssetResolutionService.ResolveSemanticRelationHandleForPreview(
            assetsManager,
            handle,
            EnsureLazyAssetReadyForPreload,
            ResolveLazyHandleSourcePath,
            TryAttachLoadedSourceFile,
            RemovePendingFileFromProgressiveQueue);

    private void RememberLazySourcePath(string? sourcePath) =>
        LazyAssetResolutionService.RememberLazySourcePath(lazySourcePathBySerializedFile, sourcePath);

    private void RememberLazyHandleSourcePath(AssetHandle handle) =>
        LazyAssetResolutionService.RememberLazyHandleSourcePath(lazySourcePathBySerializedFile, handle);

    private void RememberLazySourcePathForSerializedFile(string? serializedFileName, string? sourcePath) =>
        LazyAssetResolutionService.RememberLazySourcePathForSerializedFile(lazySourcePathBySerializedFile, serializedFileName, sourcePath);

    private static string? GetSafeFileName(string? path) =>
        LazyAssetResolutionService.GetSafeFileName(path);

    private static string? TryResolveExistingPath(string? path) =>
        LazyAssetResolutionService.TryResolveExistingPath(path);

    private string? FindSourceFileByNameInProjectRoot(string? fileName) =>
        LazyAssetResolutionService.FindSourceFileByNameInProjectRoot(lazySourceFileSearchCache, assetsManager.ProjectRoot, fileName);

    private string? ResolvePathBySuffix(string originalPath, string projectRoot) =>
        LazyAssetResolutionService.ResolvePathBySuffix(originalPath, projectRoot);

    private string? ResolveLazyHandleSourcePath(AssetHandle handle) =>
        LazyAssetResolutionService.ResolveLazyHandleSourcePath(
            handle,
            assetsManager.ProjectRoot,
            lazySourcePathBySerializedFile,
            lazySourceFileSearchCache,
            resolvedSourcePathCache,
            ViewModel.LoadingService.PendingFilesToIndex);

    private void RemovePendingFileFromProgressiveQueue(string sourcePath) =>
        ViewModel.LoadingService.RemovePendingFile(sourcePath);

    private void TryAttachLoadedSourceFile(AssetHandle handle) =>
        LazyAssetResolutionService.TryAttachLoadedSourceFile(assetsManager, handle, lazySourcePathBySerializedFile);

    internal const int DefaultLazyExportBatchSourceFileLimit = LazyAssetResolutionService.DefaultLazyExportBatchSourceFileLimit;
    internal const long DefaultLazyExportBatchByteLimit = LazyAssetResolutionService.DefaultLazyExportBatchByteLimit;

    internal string ResolveLazyExportSourceKey(AssetItem item) =>
        LazyAssetResolutionService.ResolveLazyExportSourceKey(item, ResolveLazyHandleSourcePath, GetAssetSourcePath);

    internal List<LazyExportSourceBatch> BuildLazyExportBatches(
        IReadOnlyList<AssetItem> items,
        int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
        long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit) =>
        BuildLazyExportBatchesForItems(
            items,
            ResolveLazyExportSourceKey,
            maxSourceFilesPerBatch,
            maxAssetBytesPerBatch);

    internal static List<LazyExportSourceBatch> BuildLazyExportBatchesForItems(
        IReadOnlyList<AssetItem> items,
        Func<AssetItem, string?> sourcePathResolver,
        int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
        long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit) =>
        LazyAssetResolutionService.BuildLazyExportBatches(
            items,
            sourcePathResolver,
            maxSourceFilesPerBatch,
            maxAssetBytesPerBatch);

    internal void EnsureLazyBatchLoadedForExport(
        LazyExportSourceBatch batch,
        CancellationToken cancellationToken = default) =>
        LazyAssetResolutionService.EnsureLazyBatchLoadedForExport(
            assetsManager,
            batch,
            ResolveLazyHandleSourcePath,
            TryAttachLoadedSourceFile,
            RemovePendingFileFromProgressiveQueue,
            count => global::Avalonia.Threading.Dispatcher.UIThread.Post(
                () => StatusStripUpdate($"Loading {count} source file(s) for export batch...")),
            cancellationToken);

    internal void ReleaseLazyExportBatch(
        LazyExportSourceBatch batch,
        HashSet<string>? protectedFileNames = null) =>
        LazyAssetResolutionService.ReleaseLazyExportBatch(assetsManager, batch, protectedFileNames);

    private void EnsureLazyAssetsLoadedForExport(List<AssetItem> items) =>
        LazyAssetResolutionService.EnsureLazyAssetsLoadedForExport(
            assetsManager,
            items,
            ResolveLazyExportSourceKey,
            batch => EnsureLazyBatchLoadedForExport(batch));
}
