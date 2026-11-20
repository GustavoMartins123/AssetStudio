using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class LazyAssetResolutionService
    {
        public const int DefaultLazyExportBatchSourceFileLimit = AssetExportService.DefaultLazyExportBatchSourceFileLimit;
        public const long DefaultLazyExportBatchByteLimit = AssetExportService.DefaultLazyExportBatchByteLimit;

        public ConcurrentDictionary<string, string> LazySourcePathBySerializedFile { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, string> LazySourceFileSearchCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, string> ResolvedSourcePathCache { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void ClearCaches()
        {
            LazySourcePathBySerializedFile.Clear();
            LazySourceFileSearchCache.Clear();
            ResolvedSourcePathCache.Clear();
        }

        public void RememberSourcePath(string? sourcePath) =>
            RememberLazySourcePath(LazySourcePathBySerializedFile, sourcePath);

        public void RememberHandleSourcePath(AssetHandle? handle) =>
            RememberLazyHandleSourcePath(LazySourcePathBySerializedFile, handle);

        public void RememberSourcePathForSerializedFile(string? serializedFileName, string? sourcePath) =>
            RememberLazySourcePathForSerializedFile(LazySourcePathBySerializedFile, serializedFileName, sourcePath);

        public string? ResolveHandleSourcePath(
            AssetHandle? handle,
            string? projectRoot,
            IEnumerable<string>? pendingFilesToIndex = null,
            Func<string, string, bool, IEnumerable<string>>? enumerateFiles = null) =>
            ResolveLazyHandleSourcePath(
                handle,
                projectRoot,
                LazySourcePathBySerializedFile,
                LazySourceFileSearchCache,
                ResolvedSourcePathCache,
                pendingFilesToIndex,
                enumerateFiles);

        public static void UpdateLazyAssetItemHandle(AssetItem assetItem, AssetHandle handle)
        {
            ArgumentNullException.ThrowIfNull(assetItem);
            ArgumentNullException.ThrowIfNull(handle);

            assetItem.Handle = handle;
            assetItem.SourceFile = handle.SourceFile;
            assetItem.Type = handle.Type;
            assetItem.PathID = handle.PathID;
            assetItem.Size = handle.ByteSize;
            assetItem.FullSize = handle.ByteSize;
            assetItem.Name = handle.Name ?? string.Empty;
            assetItem.Container = handle.Container ?? string.Empty;
        }

        public static void UnloadAsset(AssetHandle? handle)
        {
            if (handle == null)
            {
                return;
            }

            lock (handle)
            {
                var obj = handle.RealObject;
                handle.RealObject = null;

                if (handle.Tag is AssetItem assetItem)
                {
                    assetItem.Asset = null;
                }

                if (obj != null)
                {
                    var assetsFile = handle.SourceFile;
                    if (assetsFile != null)
                    {
                        assetsFile.RemoveObject(obj);
                    }
                }
            }
        }

        public static void RememberLazySourcePath(
            IDictionary<string, string> lazySourcePathBySerializedFile,
            string? sourcePath)
        {
            if (lazySourcePathBySerializedFile == null || string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            var fileName = GetSafeFileName(sourcePath);
            RememberLazySourcePathForSerializedFile(lazySourcePathBySerializedFile, fileName, sourcePath);
        }

        public static void RememberLazyHandleSourcePath(
            IDictionary<string, string> lazySourcePathBySerializedFile,
            AssetHandle? handle)
        {
            if (lazySourcePathBySerializedFile == null || handle == null || string.IsNullOrWhiteSpace(handle.OriginalPath))
            {
                return;
            }

            RememberLazySourcePathForSerializedFile(lazySourcePathBySerializedFile, handle.SerializedFileName, handle.OriginalPath);
            RememberLazySourcePathForSerializedFile(lazySourcePathBySerializedFile, GetSafeFileName(handle.OriginalPath), handle.OriginalPath);
        }

        public static void RememberLazySourcePathForSerializedFile(
            IDictionary<string, string> lazySourcePathBySerializedFile,
            string? serializedFileName,
            string? sourcePath)
        {
            if (lazySourcePathBySerializedFile == null
                || string.IsNullOrWhiteSpace(serializedFileName)
                || string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            lazySourcePathBySerializedFile[serializedFileName] = sourcePath;
        }

        public static string? GetSafeFileName(string? path)
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

        public static string? TryResolveExistingPath(string? path)
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

        public static string? FindSourceFileByNameInProjectRoot(
            ConcurrentDictionary<string, string> lazySourceFileSearchCache,
            string? projectRoot,
            string? fileName,
            Func<string, string, bool, IEnumerable<string>>? enumerateFiles = null)
        {
            if (lazySourceFileSearchCache == null
                || string.IsNullOrWhiteSpace(fileName)
                || string.IsNullOrEmpty(projectRoot))
            {
                return null;
            }

            var enumerate = enumerateFiles ?? (static (root, pattern, recursive) => ImportHelper.GetFilesSafe(root, pattern, recursive));
            var cached = lazySourceFileSearchCache.GetOrAdd(fileName, key =>
            {
                var direct = TryResolveExistingPath(Path.Combine(projectRoot, key));
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }

                try
                {
                    return enumerate(projectRoot, key, true)
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

        public static string? ResolvePathBySuffix(string? originalPath, string? projectRoot)
        {
            if (string.IsNullOrEmpty(originalPath) || string.IsNullOrEmpty(projectRoot))
            {
                return null;
            }

            string cleanOriginal;
            string cleanRoot;
            try
            {
                cleanOriginal = Path.GetFullPath(originalPath).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                cleanRoot = Path.GetFullPath(projectRoot).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            }
            catch
            {
                return null;
            }

            var parts = cleanOriginal.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < parts.Length; i++)
            {
                var suffix = string.Join(Path.DirectorySeparatorChar, parts.Skip(i));
                var candidate = Path.Combine(cleanRoot, suffix);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public static string? ResolveLazyHandleSourcePath(
            AssetHandle? handle,
            string? projectRoot,
            IDictionary<string, string> lazySourcePathBySerializedFile,
            ConcurrentDictionary<string, string> lazySourceFileSearchCache,
            ConcurrentDictionary<string, string> resolvedSourcePathCache,
            IEnumerable<string>? pendingFilesToIndex = null,
            Func<string, string, bool, IEnumerable<string>>? enumerateFiles = null)
        {
            if (handle == null)
            {
                return null;
            }

            var cacheKey = $"{handle.SerializedFileName}|{handle.OriginalPath}";
            if (resolvedSourcePathCache != null && resolvedSourcePathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return string.IsNullOrEmpty(cachedPath) ? null : cachedPath;
            }

            var resolved = ResolveLazyHandleSourcePathInternal(
                handle,
                projectRoot,
                lazySourcePathBySerializedFile,
                lazySourceFileSearchCache,
                pendingFilesToIndex,
                enumerateFiles);

            if (resolvedSourcePathCache != null)
            {
                resolvedSourcePathCache[cacheKey] = resolved ?? string.Empty;
            }

            return resolved;
        }

        public static string? ResolveLazyHandleSourcePathInternal(
            AssetHandle handle,
            string? projectRoot,
            IDictionary<string, string>? lazySourcePathBySerializedFile,
            ConcurrentDictionary<string, string>? lazySourceFileSearchCache,
            IEnumerable<string>? pendingFilesToIndex = null,
            Func<string, string, bool, IEnumerable<string>>? enumerateFiles = null)
        {
            if (handle == null)
            {
                return null;
            }

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
                if (!Path.IsPathRooted(handle.OriginalPath) && !string.IsNullOrEmpty(projectRoot))
                {
                    candidates.Add(Path.Combine(projectRoot, handle.OriginalPath));
                }
            }

            if (!string.IsNullOrWhiteSpace(handle.SerializedFileName)
                && lazySourcePathBySerializedFile != null
                && lazySourcePathBySerializedFile.TryGetValue(handle.SerializedFileName, out var mappedSourcePath))
            {
                candidates.Add(mappedSourcePath);
            }

            var originalFileName = GetSafeFileName(handle.OriginalPath);
            if (!string.IsNullOrWhiteSpace(originalFileName)
                && lazySourcePathBySerializedFile != null
                && lazySourcePathBySerializedFile.TryGetValue(originalFileName, out var mappedOriginalFilePath))
            {
                candidates.Add(mappedOriginalFilePath);
            }

            if (!string.IsNullOrWhiteSpace(handle.SerializedFileName) && !string.IsNullOrEmpty(projectRoot))
            {
                candidates.Add(Path.Combine(projectRoot, handle.SerializedFileName));
            }

            if (pendingFilesToIndex != null)
            {
                foreach (var pending in pendingFilesToIndex)
                {
                    var pendingFileName = GetSafeFileName(pending);
                    if (string.Equals(pending, handle.OriginalPath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pendingFileName, originalFileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pendingFileName, handle.SerializedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(pending);
                    }
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

            if (!string.IsNullOrEmpty(handle.OriginalPath) && !string.IsNullOrEmpty(projectRoot))
            {
                var suffixMatch = ResolvePathBySuffix(handle.OriginalPath, projectRoot);
                if (suffixMatch != null)
                {
                    return suffixMatch;
                }
            }

            if (lazySourceFileSearchCache != null)
            {
                var foundByOriginalName = FindSourceFileByNameInProjectRoot(lazySourceFileSearchCache, projectRoot, originalFileName, enumerateFiles);
                if (foundByOriginalName != null)
                {
                    return foundByOriginalName;
                }

                var foundBySerializedName = FindSourceFileByNameInProjectRoot(lazySourceFileSearchCache, projectRoot, handle.SerializedFileName, enumerateFiles);
                if (foundBySerializedName != null)
                {
                    return foundBySerializedName;
                }
            }

            return null;
        }

        public static void TryAttachLoadedSourceFile(
            AssetsManager assetsManager,
            AssetHandle? handle,
            IDictionary<string, string>? lazySourcePathBySerializedFile = null)
        {
            if (assetsManager == null || handle == null || handle.SourceFile?.reader != null)
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

                if (lazySourcePathBySerializedFile != null)
                {
                    RememberLazyHandleSourcePath(lazySourcePathBySerializedFile, handle);
                }
            }
        }

        public static void EnsureLazyAssetReadyForPreview(
            AssetsManager assetsManager,
            AssetItem? assetItem,
            IDictionary<string, string> lazySourcePathBySerializedFile,
            ConcurrentDictionary<string, string> lazySourceFileSearchCache,
            ConcurrentDictionary<string, string> resolvedSourcePathCache,
            IEnumerable<string>? pendingFilesToIndex = null,
            Action<string>? removePendingFile = null,
            Action<string>? notifyLoadingSource = null,
            Action<LoggerEvent, string>? logWarning = null,
            Action<string>? onSourceLoaded = null)
        {
            if (assetsManager == null || assetItem == null)
            {
                return;
            }

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

                TryAttachLoadedSourceFile(assetsManager, handle, lazySourcePathBySerializedFile);
                if (handle.SourceFile?.reader != null)
                {
                    UpdateLazyAssetItemHandle(assetItem, handle);
                    return;
                }

                var sourcePath = ResolveLazyHandleSourcePath(
                    handle,
                    assetsManager.ProjectRoot,
                    lazySourcePathBySerializedFile,
                    lazySourceFileSearchCache,
                    resolvedSourcePathCache,
                    pendingFilesToIndex);
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    logWarning?.Invoke(
                        LoggerEvent.Warning,
                        $"Lazy preview source file not found for {handle.TypeString} PathID={handle.PathID}: {handle.OriginalPath}");
                    return;
                }

                removePendingFile?.Invoke(sourcePath);
                notifyLoadingSource?.Invoke(sourcePath);

                TryAttachLoadedSourceFile(assetsManager, handle, lazySourcePathBySerializedFile);
                if (handle.SourceFile?.reader == null)
                {
                    assetsManager.LoadFilesForPreview(sourcePath);
                    assetsManager.WaitForAssetsFileLoaded(handle.SerializedFileName, 5000);
                    TryAttachLoadedSourceFile(assetsManager, handle, lazySourcePathBySerializedFile);
                }

                UpdateLazyAssetItemHandle(assetItem, handle);
                onSourceLoaded?.Invoke(sourcePath);
            }
        }

        public static AssetStudio.Object? ResolveSemanticRelationHandleForPreview(
            AssetsManager assetsManager,
            AssetHandle? handle,
            Action<AssetItem>? ensureLazyAssetReadyForPreview,
            Func<AssetHandle, string?> resolveHandleSourcePath,
            Action<AssetHandle> tryAttachLoadedSourceFile,
            Action<string>? removePendingFile = null)
        {
            if (assetsManager == null || handle == null)
            {
                return null;
            }

            if (handle.Tag is AssetItem assetItem)
            {
                ensureLazyAssetReadyForPreview?.Invoke(assetItem);
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

                tryAttachLoadedSourceFile(handle);
                if (handle.SourceFile?.reader == null)
                {
                    var sourcePath = resolveHandleSourcePath(handle);
                    if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                    {
                        try
                        {
                            removePendingFile?.Invoke(sourcePath);
                            assetsManager.LoadFilesForPreview(sourcePath);
                            assetsManager.WaitForAssetsFileLoaded(handle.SerializedFileName, 5000);
                            tryAttachLoadedSourceFile(handle);
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

        public static string ResolveLazyExportSourceKey(
            AssetItem? item,
            Func<AssetHandle, string?> resolveHandleSourcePath,
            Func<AssetItem, string>? fallbackSourcePathResolver = null)
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (item.Handle != null)
            {
                var resolved = resolveHandleSourcePath(item.Handle);
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

            return fallbackSourcePathResolver != null
                ? fallbackSourcePathResolver(item)
                : item.SourceFile?.originalPath ?? item.SourceFile?.fullName ?? item.SourceFile?.fileName ?? string.Empty;
        }

        public static List<LazyExportSourceBatch> BuildLazyExportBatches(
            IReadOnlyList<AssetItem> items,
            Func<AssetItem, string?> sourcePathResolver,
            int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
            long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit) =>
            AssetExportService.BuildLazyExportBatches(
                items,
                sourcePathResolver,
                maxSourceFilesPerBatch,
                maxAssetBytesPerBatch);

        public static void EnsureLazyBatchLoadedForExport(
            AssetsManager assetsManager,
            LazyExportSourceBatch? batch,
            Func<AssetHandle, string?> resolveHandleSourcePath,
            Action<AssetHandle> tryAttachLoadedSourceFile,
            Action<string>? removePendingFile = null,
            Action<int>? notifyLoadingBatch = null,
            CancellationToken cancellationToken = default)
        {
            if (assetsManager == null || !assetsManager.LazyLoading || batch == null || batch.Items.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            for (int i = 0; i < batch.Items.Count; i++)
            {
                var handle = batch.Items[i].Handle;
                if (handle != null && handle.SourceFile?.reader == null)
                {
                    tryAttachLoadedSourceFile(handle);
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
                .Select(x => resolveHandleSourcePath(x.Handle!))
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uniqueSourcePaths.Count == 0)
            {
                return;
            }

            if (removePendingFile != null)
            {
                for (int i = 0; i < uniqueSourcePaths.Count; i++)
                {
                    removePendingFile(uniqueSourcePaths[i]);
                }
            }

            notifyLoadingBatch?.Invoke(uniqueSourcePaths.Count);

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
                    tryAttachLoadedSourceFile(item.Handle);
                    if (item.Handle.SourceFile?.reader == null)
                    {
                        assetsManager.WaitForAssetsFileLoaded(item.Handle.SerializedFileName, 5000);
                        tryAttachLoadedSourceFile(item.Handle);
                    }
                    UpdateLazyAssetItemHandle(item, item.Handle);
                }
            }
        }

        public static void ReleaseLazyExportBatch(
            AssetsManager assetsManager,
            LazyExportSourceBatch? batch,
            HashSet<string>? protectedFileNames = null)
        {
            if (assetsManager == null || !assetsManager.LazyLoading || batch == null)
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

        public static void EnsureLazyAssetsLoadedForExport(
            AssetsManager assetsManager,
            IReadOnlyList<AssetItem>? items,
            Func<AssetItem, string?> sourcePathResolver,
            Action<LazyExportSourceBatch> ensureBatchLoaded)
        {
            if (assetsManager == null || !assetsManager.LazyLoading || items == null || items.Count == 0)
            {
                return;
            }

            var batches = BuildLazyExportBatches(
                items,
                sourcePathResolver,
                maxSourceFilesPerBatch: Math.Max(DefaultLazyExportBatchSourceFileLimit, items.Count),
                maxAssetBytesPerBatch: long.MaxValue);
            for (int i = 0; i < batches.Count; i++)
            {
                ensureBatchLoaded(batches[i]);
            }
        }
    }
}
