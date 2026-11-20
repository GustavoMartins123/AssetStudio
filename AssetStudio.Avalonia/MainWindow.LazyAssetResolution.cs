using AssetStudio;
using AssetStudio.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void EnsureLazyAssetReadyForPreview(AssetItem assetItem)
    {
        var handle = assetItem.Handle;
        if (handle == null || handle.RealObject != null)
        {
            return;
        }

        lock (handle)
        {
            if (handle.RealObject != null)
            {
                return;
            }

            if (handle.SourceFile?.reader != null)
            {
                UpdateLazyAssetItemHandle(assetItem, handle);
                return;
            }

            TryAttachLoadedSourceFile(handle);
            if (handle.SourceFile?.reader != null)
            {
                UpdateLazyAssetItemHandle(assetItem, handle);
                return;
            }

            var sourcePath = ResolveLazyHandleSourcePath(handle);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                logger.Log(LoggerEvent.Warning, $"Lazy preview source file not found for {handle.TypeString} PathID={handle.PathID}: {handle.OriginalPath}");
                return;
            }

            RemovePendingFileFromProgressiveQueue(sourcePath);
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusStripUpdate($"Loading preview source: {Path.GetFileName(sourcePath)}"));

            TryAttachLoadedSourceFile(handle);
            if (handle.SourceFile?.reader == null)
            {
                assetsManager.LoadFilesForPreview(sourcePath);
                assetsManager.WaitForAssetsFileLoaded(handle.SerializedFileName, 5000);
                TryAttachLoadedSourceFile(handle);
            }
            UpdateLazyAssetItemHandle(assetItem, handle);

            var preserveSemanticPreviewCaches = CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath());
            lock (previewCacheLock)
            {
                if (!preserveSemanticPreviewCaches)
                {
                    meshToMaterialsCache = null;
                    materialMainTextureCache = null;
                    materialPreviewMaterialCache = null;
                    materialTextureSlotsCache = null;
                    animationClipAvatarCache = null;
                    avatarMeshCache = null;
                    avatarMeshesCache = null;
                    meshAvatarCache = null;
                    meshSkinnedRenderersCache = null;
                    animationClipTransformBindingsCache = null;
                }

                meshAssociatedRenderersCache = null;
                meshSourceTypesCache = null;
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
        }
    }

    private AssetStudio.Object? ResolveSemanticRelationHandleForPreview(AssetHandle handle)
    {
        if (handle == null)
        {
            return null;
        }

        if (handle.Tag is AssetItem assetItem)
        {
            EnsureLazyAssetReadyForPreview(assetItem);
            return assetItem.Asset;
        }

        if (!assetsManager.LazyLoading)
        {
            return assetsManager.ResolveHandle(handle);
        }

        lock (handle)
        {
            if (handle.RealObject != null)
            {
                return handle.RealObject;
            }

            TryAttachLoadedSourceFile(handle);
            if (handle.SourceFile?.reader == null)
            {
                var sourcePath = ResolveLazyHandleSourcePath(handle);
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    try
                    {
                        RemovePendingFileFromProgressiveQueue(sourcePath);
                        assetsManager.LoadFilesForPreview(sourcePath);
                        assetsManager.WaitForAssetsFileLoaded(handle.SerializedFileName, 5000);
                        TryAttachLoadedSourceFile(handle);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to load semantic relation source {Path.GetFileName(sourcePath)}: {ex.Message}");
                    }
                }
            }

            return assetsManager.ResolveHandle(handle);
        }
    }

    private void RememberLazySourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var fileName = GetSafeFileName(sourcePath);
        RememberLazySourcePathForSerializedFile(fileName, sourcePath);
    }

    private void RememberLazyHandleSourcePath(AssetHandle handle)
    {
        if (handle == null || string.IsNullOrWhiteSpace(handle.OriginalPath))
        {
            return;
        }

        RememberLazySourcePathForSerializedFile(handle.SerializedFileName, handle.OriginalPath);
        RememberLazySourcePathForSerializedFile(GetSafeFileName(handle.OriginalPath), handle.OriginalPath);
    }

    private void RememberLazySourcePathForSerializedFile(string? serializedFileName, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(serializedFileName) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        lazySourcePathBySerializedFile[serializedFileName] = sourcePath;
    }

    private static string? GetSafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private string? FindSourceFileByNameInProjectRoot(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(assetsManager.ProjectRoot))
        {
            return null;
        }

        var cached = lazySourceFileSearchCache.GetOrAdd(fileName, key =>
        {
            var direct = TryResolveExistingPath(Path.Combine(assetsManager.ProjectRoot, key));
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }

            try
            {
                return ImportHelper.GetFilesSafe(assetsManager.ProjectRoot, key, true)
                    .Select(TryResolveExistingPath)
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        });

        return string.IsNullOrEmpty(cached) ? null : cached;
    }

    private string? ResolvePathBySuffix(string originalPath, string projectRoot)
    {
        if (string.IsNullOrEmpty(originalPath) || string.IsNullOrEmpty(projectRoot))
        {
            return null;
        }

        string cleanOriginal;
        string cleanRoot;
        try
        {
            cleanOriginal = Path.GetFullPath(originalPath).Replace('/', '\\');
            cleanRoot = Path.GetFullPath(projectRoot).Replace('/', '\\');
        }
        catch
        {
            return null;
        }

        var parts = cleanOriginal.Split('\\');
        for (int i = 1; i < parts.Length; i++)
        {
            var suffix = string.Join("\\", parts.Skip(i));
            var candidate = Path.Combine(cleanRoot, suffix);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private string? ResolveLazyHandleSourcePath(AssetHandle handle)
    {
        if (handle == null)
        {
            return null;
        }
        var cacheKey = $"{handle.SerializedFileName}|{handle.OriginalPath}";
        if (resolvedSourcePathCache.TryGetValue(cacheKey, out var cachedPath))
        {
            return string.IsNullOrEmpty(cachedPath) ? null : cachedPath;
        }

        var resolved = ResolveLazyHandleSourcePathInternal(handle);
        resolvedSourcePathCache[cacheKey] = resolved ?? string.Empty;
        return resolved;
    }

    private string? ResolveLazyHandleSourcePathInternal(AssetHandle handle)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(handle.SourceFile?.originalPath))
        {
            candidates.Add(handle.SourceFile.originalPath);
        }

        if (!string.IsNullOrWhiteSpace(handle.SourceFile?.fullName))
        {
            candidates.Add(handle.SourceFile.fullName);
        }

        if (!string.IsNullOrWhiteSpace(handle.OriginalPath))
        {
            candidates.Add(handle.OriginalPath);
            if (!Path.IsPathRooted(handle.OriginalPath) && !string.IsNullOrEmpty(assetsManager.ProjectRoot))
            {
                candidates.Add(Path.Combine(assetsManager.ProjectRoot, handle.OriginalPath));
            }
        }

        if (!string.IsNullOrWhiteSpace(handle.SerializedFileName)
            && lazySourcePathBySerializedFile.TryGetValue(handle.SerializedFileName, out var mappedSourcePath))
        {
            candidates.Add(mappedSourcePath);
        }

        var originalFileName = GetSafeFileName(handle.OriginalPath);
        if (!string.IsNullOrWhiteSpace(originalFileName)
            && lazySourcePathBySerializedFile.TryGetValue(originalFileName, out var mappedOriginalFilePath))
        {
            candidates.Add(mappedOriginalFilePath);
        }

        if (!string.IsNullOrWhiteSpace(handle.SerializedFileName) && !string.IsNullOrEmpty(assetsManager.ProjectRoot))
        {
            candidates.Add(Path.Combine(assetsManager.ProjectRoot, handle.SerializedFileName));
        }

        foreach (var pending in ViewModel.LoadingService.PendingFilesToIndex)
        {
            var pendingFileName = GetSafeFileName(pending);
            if (string.Equals(pending, handle.OriginalPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pendingFileName, originalFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pendingFileName, handle.SerializedFileName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(pending);
            }
        }

        foreach (var candidate in candidates)
        {
            var resolved = TryResolveExistingPath(candidate);
            if (resolved != null)
            {
                return resolved;
            }
        }

        if (!string.IsNullOrEmpty(handle.OriginalPath) && !string.IsNullOrEmpty(assetsManager.ProjectRoot))
        {
            var suffixMatch = ResolvePathBySuffix(handle.OriginalPath, assetsManager.ProjectRoot);
            if (suffixMatch != null)
            {
                return suffixMatch;
            }
        }

        var foundByOriginalName = FindSourceFileByNameInProjectRoot(originalFileName);
        if (foundByOriginalName != null)
        {
            return foundByOriginalName;
        }

        var foundBySerializedName = FindSourceFileByNameInProjectRoot(handle.SerializedFileName);
        if (foundBySerializedName != null)
        {
            return foundBySerializedName;
        }

        return null;
    }

    private void RemovePendingFileFromProgressiveQueue(string sourcePath)
    {
        ViewModel.LoadingService.RemovePendingFile(sourcePath);
    }

    private void TryAttachLoadedSourceFile(AssetHandle handle)
    {
        if (handle.SourceFile?.reader != null)
        {
            return;
        }

        if (assetsManager.TryFindSerializedFile(handle.SerializedFileName, handle.OriginalPath, out var sourceFile) && sourceFile != null)
        {
            handle.SourceFile = sourceFile;
            if (string.IsNullOrEmpty(handle.OriginalPath) && !string.IsNullOrEmpty(sourceFile.originalPath))
            {
                handle.OriginalPath = sourceFile.originalPath;
            }
            RememberLazyHandleSourcePath(handle);
        }
    }

    internal const int DefaultLazyExportBatchSourceFileLimit = AssetExportService.DefaultLazyExportBatchSourceFileLimit;
    internal const long DefaultLazyExportBatchByteLimit = AssetExportService.DefaultLazyExportBatchByteLimit;

    internal string ResolveLazyExportSourceKey(AssetItem item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        if (item.Handle != null)
        {
            var resolved = ResolveLazyHandleSourcePath(item.Handle);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            if (!string.IsNullOrWhiteSpace(item.Handle.OriginalPath))
            {
                return item.Handle.OriginalPath;
            }

            if (!string.IsNullOrWhiteSpace(item.Handle.SerializedFileName))
            {
                return item.Handle.SerializedFileName;
            }
        }

        return GetAssetSourcePath(item);
    }

    internal List<LazyExportSourceBatch> BuildLazyExportBatches(
        IReadOnlyList<AssetItem> items,
        int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
        long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit)
    {
        return BuildLazyExportBatchesForItems(
            items,
            ResolveLazyExportSourceKey,
            maxSourceFilesPerBatch,
            maxAssetBytesPerBatch);
    }

    internal static List<LazyExportSourceBatch> BuildLazyExportBatchesForItems(
        IReadOnlyList<AssetItem> items,
        Func<AssetItem, string?> sourcePathResolver,
        int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
        long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit)
        => AssetExportService.BuildLazyExportBatches(
            items,
            sourcePathResolver,
            maxSourceFilesPerBatch,
            maxAssetBytesPerBatch);

    internal void EnsureLazyBatchLoadedForExport(
        LazyExportSourceBatch batch,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (!assetsManager.LazyLoading || batch == null || batch.Items.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        for (int i = 0; i < batch.Items.Count; i++)
        {
            var handle = batch.Items[i].Handle;
            if (handle != null && handle.SourceFile?.reader == null)
            {
                TryAttachLoadedSourceFile(handle);
                if (handle.SourceFile?.reader != null)
                {
                    UpdateLazyAssetItemHandle(batch.Items[i], handle);
                }
            }
        }

        var unloadedItems = batch.Items
            .Where(x => x.Handle != null && x.Handle.SourceFile?.reader == null)
            .ToList();

        if (unloadedItems.Count == 0)
        {
            return;
        }

        var uniqueSourcePaths = unloadedItems
            .Select(x => ResolveLazyHandleSourcePath(x.Handle!))
            .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniqueSourcePaths.Count == 0)
        {
            return;
        }

        for (int i = 0; i < uniqueSourcePaths.Count; i++)
        {
            RemovePendingFileFromProgressiveQueue(uniqueSourcePaths[i]);
        }

        global::Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StatusStripUpdate($"Loading {uniqueSourcePaths.Count} source file(s) for export batch..."));

        foreach (var path in uniqueSourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                assetsManager.LoadFilesForPreview(path);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load source file {path} for export: {ex.Message}");
            }
        }

        foreach (var item in unloadedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Handle != null)
            {
                TryAttachLoadedSourceFile(item.Handle);
                if (item.Handle.SourceFile?.reader == null)
                {
                    assetsManager.WaitForAssetsFileLoaded(item.Handle.SerializedFileName, 5000);
                    TryAttachLoadedSourceFile(item.Handle);
                }
                UpdateLazyAssetItemHandle(item, item.Handle);
            }
        }
    }

    internal void ReleaseLazyExportBatch(
        LazyExportSourceBatch batch,
        HashSet<string>? protectedFileNames = null)
    {
        if (!assetsManager.LazyLoading || batch == null)
        {
            return;
        }

        for (int i = 0; i < batch.Items.Count; i++)
        {
            var item = batch.Items[i];
            item.ClearAsset();
            var handle = item.Handle;
            if (handle != null)
            {
                lock (handle)
                {
                    var obj = handle.RealObject;
                    handle.RealObject = null;
                    if (obj != null && handle.SourceFile != null)
                    {
                        handle.SourceFile.RemoveObject(obj);
                    }
                }
            }
        }

        var originalKeepCallback = assetsManager.ShouldKeepFileCallback;
        bool ShouldKeepFile(string fileName)
        {
            if (protectedFileNames != null && protectedFileNames.Contains(fileName))
            {
                return true;
            }
            return originalKeepCallback != null && originalKeepCallback(fileName);
        }

        List<string> fileNamesToEvict;
        lock (assetsManager.loadLock)
        {
            fileNamesToEvict = assetsManager.assetsFileList
                .Where(f => !ShouldKeepFile(f.fileName))
                .Select(f => f.fileName)
                .ToList();
        }

        for (int f = 0; f < fileNamesToEvict.Count; f++)
        {
            foreach (var handle in assetsManager.ProjectIndex.GetHandlesForFile(fileNamesToEvict[f]))
            {
                lock (handle)
                {
                    handle.RealObject = null;
                    if (handle.Tag is AssetItem taggedItem)
                    {
                        taggedItem.ClearAsset();
                        taggedItem.SourceFile = null;
                    }
                    else if (handle.Tag is IAssetHandleTag tag)
                    {
                        tag.ClearAsset();
                    }
                }
            }
        }

        try
        {
            assetsManager.ShouldKeepFileCallback = ShouldKeepFile;
            assetsManager.ClearLoadedFilesKeepIndex();
            assetsManager.LruCache.Clear();
        }
        finally
        {
            assetsManager.ShouldKeepFileCallback = originalKeepCallback;
        }
    }

    private void EnsureLazyAssetsLoadedForExport(List<AssetItem> items)
    {
        if (!assetsManager.LazyLoading || items == null || items.Count == 0)
        {
            return;
        }

        var batches = BuildLazyExportBatches(
            items,
            maxSourceFilesPerBatch: Math.Max(DefaultLazyExportBatchSourceFileLimit, items.Count),
            maxAssetBytesPerBatch: long.MaxValue);
        for (int i = 0; i < batches.Count; i++)
        {
            EnsureLazyBatchLoadedForExport(batches[i]);
        }
    }
}
