using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private static string GetFolderCacheKey(string folderPath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folderPath)));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private void ClearCurrentProjectCache_Click(object? sender, RoutedEventArgs e)
    {
        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            MessageBox.Show(this, "No loaded project folder was found to clear.", "Clear project cache");
            return;
        }

        try
        {
            _sqliteCache.DeleteIndexCache(folderPath);
            DeleteDecompressedCacheFolder(folderPath);
            DeletePreviewCacheFolder(folderPath);

            lock (previewCacheLock)
            {
                meshToMaterialsCache = null;
                meshAssociatedRenderersCache = null;
                meshSourceTypesCache = null;
                materialMainTextureCache = null;
                materialPreviewMaterialCache = null;
                materialTextureSlotsCache = null;
            }

            StatusStripUpdate($"Cleared project cache for: {folderPath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to clear project cache:\n{ex.Message}", "Clear project cache");
            StatusStripUpdate("Failed to clear project cache.");
        }
    }

    private string GetCurrentCacheFolderPath()
    {
        if (!string.IsNullOrWhiteSpace(appSettings.LoadFolderPath) && Directory.Exists(appSettings.LoadFolderPath))
        {
            return appSettings.LoadFolderPath;
        }

        if (!string.IsNullOrWhiteSpace(assetsManager.ProjectRoot) && Directory.Exists(assetsManager.ProjectRoot))
        {
            return assetsManager.ProjectRoot;
        }

        return string.Empty;
    }

    private static void DeleteDecompressedCacheFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "DecompressedCache");
        var targetDirectory = Path.Combine(cacheRoot, GetFolderCacheKey(folderPath));
        DeleteDirectoryInsideRoot(cacheRoot, targetDirectory);
    }

    private static void DeletePreviewCacheFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "PreviewCache");
        var targetDirectory = Path.Combine(cacheRoot, GetFolderCacheKey(folderPath));
        DeleteDirectoryInsideRoot(cacheRoot, targetDirectory);
    }

    private MeshPreviewGeometryCache? LoadMeshPreviewGeometryCache(Mesh mesh, float densityPercent)
    {
        if (mesh.assetsFile == null || currentScanResult == null)
        {
            return null;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        try
        {
            var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            var parameters = BuildMeshPreviewCacheParameters(mesh, densityPercent);
            var entry = _sqliteCache.LoadPreviewCacheEntry(
                folderPath,
                signature,
                assetId,
                "mesh-geometry",
                MeshPreviewGeometryCache.AlgorithmVersion,
                parameters);

            if (entry == null || string.IsNullOrWhiteSpace(entry.PayloadPath) || !File.Exists(entry.PayloadPath))
            {
                return null;
            }

            if (!IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(entry.PayloadPath);
            if (!string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return MeshPreviewGeometryCacheSerializer.Deserialize(bytes);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load mesh preview geometry cache: {ex.Message}");
            return null;
        }
    }

    private void SaveMeshPreviewGeometryCache(Mesh mesh, float densityPercent, MeshPreviewGeometryCache cache)
    {
        if (mesh.assetsFile == null || currentScanResult == null || cache == null)
        {
            return;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            var bytes = MeshPreviewGeometryCacheSerializer.Serialize(cache);
            var hash = ComputeSha256Hex(bytes);
            var payloadPath = GetPreviewCachePayloadPath(folderPath, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

            if (!File.Exists(payloadPath))
            {
                var tempPath = payloadPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, payloadPath, overwrite: true);
            }

            var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            _sqliteCache.SavePreviewCacheEntry(
                folderPath,
                signature,
                assetId,
                "mesh-geometry",
                MeshPreviewGeometryCache.AlgorithmVersion,
                BuildMeshPreviewCacheParameters(mesh, densityPercent),
                hash,
                payloadPath,
                bytes.LongLength);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save mesh preview geometry cache: {ex.Message}");
        }
    }

    private TexturePreviewImageResult? LoadTexturePreviewThumbnail(Texture2D texture, int maxDimension)
    {
        if (texture == null)
        {
            return null;
        }

        var sourceWidth = texture.m_Width;
        var sourceHeight = texture.m_Height;
        var folderPath = GetCurrentCacheFolderPath();
        var canUsePersistentCache = texture.assetsFile != null
            && currentScanResult != null
            && !string.IsNullOrWhiteSpace(folderPath)
            && Directory.Exists(folderPath);

        if (canUsePersistentCache)
        {
            try
            {
                var assetId = AssetHandle.BuildUniqueID(texture.assetsFile!, texture.m_PathID);
                var signature = _sqliteCache.GetFolderSignature(currentScanResult!);
                var parameters = BuildTexturePreviewCacheParameters(texture, maxDimension);
                var entry = _sqliteCache.LoadPreviewCacheEntry(
                    folderPath,
                    signature,
                    assetId,
                    "texture-thumbnail-png",
                    TexturePreviewThumbnailAlgorithmVersion,
                    parameters);

                if (entry != null
                    && !string.IsNullOrWhiteSpace(entry.PayloadPath)
                    && File.Exists(entry.PayloadPath)
                    && IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath))
                {
                    var bytes = File.ReadAllBytes(entry.PayloadPath);
                    if (string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var cachedImage = SixLabors.ImageSharp.Image.Load<Bgra32>(bytes);
                        return new TexturePreviewImageResult(
                            cachedImage,
                            fromCache: true,
                            downscaled: sourceWidth > cachedImage.Width || sourceHeight > cachedImage.Height,
                            sourceWidth,
                            sourceHeight);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load texture thumbnail cache: {ex.Message}");
            }
        }

        var image = texture.ConvertToImage(true);
        if (image == null)
        {
            return null;
        }

        var downscaled = LimitPreviewImage(image, maxDimension);
        if (canUsePersistentCache)
        {
            try
            {
                using var stream = new MemoryStream();
                image.SaveAsPng(stream);
                var bytes = stream.ToArray();
                var hash = ComputeSha256Hex(bytes);
                var payloadPath = GetPreviewCachePayloadPath(folderPath, hash, ".texturepreview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

                if (!File.Exists(payloadPath))
                {
                    var tempPath = payloadPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(tempPath, bytes);
                    File.Move(tempPath, payloadPath, overwrite: true);
                }

                var assetId = AssetHandle.BuildUniqueID(texture.assetsFile!, texture.m_PathID);
                var signature = _sqliteCache.GetFolderSignature(currentScanResult!);
                _sqliteCache.SavePreviewCacheEntry(
                    folderPath,
                    signature,
                    assetId,
                    "texture-thumbnail-png",
                    TexturePreviewThumbnailAlgorithmVersion,
                    BuildTexturePreviewCacheParameters(texture, maxDimension),
                    hash,
                    payloadPath,
                    bytes.LongLength);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save texture thumbnail cache: {ex.Message}");
            }
        }

        return new TexturePreviewImageResult(image, fromCache: false, downscaled, sourceWidth, sourceHeight);
    }

    private static string BuildMeshPreviewCacheParameters(Mesh mesh, float densityPercent)
    {
        return string.Join("|",
            "density=" + densityPercent.ToString("0.###", CultureInfo.InvariantCulture),
            "vertices=" + mesh.m_VertexCount.ToString(CultureInfo.InvariantCulture),
            "indices=" + (mesh.m_Indices?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            "submeshes=" + (mesh.m_SubMeshes?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
            "bytes=" + mesh.byteSize.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildTexturePreviewCacheParameters(Texture2D texture, int maxDimension)
    {
        return string.Join("|",
            "max=" + maxDimension.ToString(CultureInfo.InvariantCulture),
            "width=" + texture.m_Width.ToString(CultureInfo.InvariantCulture),
            "height=" + texture.m_Height.ToString(CultureInfo.InvariantCulture),
            "format=" + texture.m_TextureFormat,
            "mips=" + texture.m_MipCount.ToString(CultureInfo.InvariantCulture),
            "bytes=" + texture.byteSize.ToString(CultureInfo.InvariantCulture),
            "streamSize=" + (texture.m_StreamData?.size ?? 0).ToString(CultureInfo.InvariantCulture),
            "streamOffset=" + (texture.m_StreamData?.offset ?? 0).ToString(CultureInfo.InvariantCulture));
    }

    private static string GetPreviewCacheRoot(string folderPath)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "PreviewCache",
            GetFolderCacheKey(folderPath));
    }

    private static string GetPreviewCachePayloadPath(string folderPath, string hash)
    {
        return GetPreviewCachePayloadPath(folderPath, hash, ".meshpreview");
    }

    private static string GetPreviewCachePayloadPath(string folderPath, string hash, string extension)
    {
        var safeHash = string.IsNullOrWhiteSpace(hash) ? "empty" : hash.ToLowerInvariant();
        var prefix = safeHash.Length >= 2 ? safeHash.Substring(0, 2) : "00";
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".cache" : extension;
        if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
        {
            safeExtension = "." + safeExtension;
        }

        return Path.Combine(GetPreviewCacheRoot(folderPath), prefix, safeHash + safeExtension);
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static bool IsPathInsideDirectory(string root, string path)
    {
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetFullPath = Path.GetFullPath(path);
        return targetFullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteDirectoryInsideRoot(string root, string targetDirectory)
    {
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetFullPath = Path.GetFullPath(targetDirectory);

        if (!targetFullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a cache directory outside the AssetStudio cache root.");
        }

        if (Directory.Exists(targetFullPath))
        {
            Directory.Delete(targetFullPath, recursive: true);
        }
    }

    private void SaveIndexCache(
        string folderPath,
        ProjectScanResult scanResult,
        bool preserveSemanticRelations = false,
        Action<int, int, string>? progress = null)
    {
        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            var unityVersion = assetsManager.SpecifyUnityVersion;
            var handles = assetsManager.ProjectIndex.GetHandles();
            PublishDatabaseWriteProgress(
                folderPath,
                scanResult,
                "saving_index",
                0,
                1,
                "Waiting for SQLite writer",
                persist: true);

            void ReportSaveProgress(int processed, int total, string stage)
            {
                progress?.Invoke(processed, total, stage);
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "saving_index",
                    processed,
                    total,
                    stage,
                    persist: false);
            }

            var saved = _sqliteCache.SaveIndexCache(folderPath, signature, scanResult, unityVersion, handles, preserveSemanticRelations, ReportSaveProgress);

            if (saved)
            {
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "completed",
                    1,
                    1,
                    "SQLite index cache saved",
                    persist: true);
            }
            else
            {
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "failed",
                    1,
                    1,
                    "SQLite index cache save failed",
                    persist: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save index cache: {ex.Message}");
        }
    }

    private void PublishDatabaseWriteProgress(
        string folderPath,
        ProjectScanResult scanResult,
        string status,
        int processed,
        int total,
        string stage,
        bool persist)
    {
        var safeTotal = Math.Max(1, total);
        var safeProcessed = Math.Clamp(processed, 0, safeTotal);
        var percent = Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal));
        var update = new IndexingProgressUpdate
        {
            Status = status,
            TotalFiles = safeTotal,
            ProcessedFiles = safeProcessed,
            PendingFiles = Math.Max(0, safeTotal - safeProcessed),
            PercentComplete = percent,
            CurrentFile = stage ?? string.Empty,
            LastReadFile = stage ?? string.Empty,
            NewlyReadFiles = Array.Empty<string>()
        };

        if (persist)
        {
            try
            {
                var signature = _sqliteCache.GetFolderSignature(scanResult);
                _sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to persist database write progress: {ex.Message}");
            }
        }

        ShowIndexingProgressPanel(update, 3);
    }

    private bool TrySaveSemanticRelations(SemanticAssetRelations relations)
    {
        if (currentScanResult == null)
        {
            return false;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        return TrySaveSemanticRelations(folderPath, currentScanResult, relations);
    }

    private bool TrySaveSemanticRelations(string folderPath, ProjectScanResult scanResult, SemanticAssetRelations relations, bool replaceExisting = false)
    {
        if (relations == null || (!relations.HasRelations && relations.SourceFiles.Count == 0) || scanResult == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            PublishDatabaseWriteProgress(
                folderPath,
                scanResult,
                "saving_connections",
                0,
                1,
                "Waiting for SQLite writer",
                persist: true);

            var saved = _sqliteCache.SaveSemanticRelations(
                folderPath,
                signature,
                relations,
                replaceExisting,
                (processed, total, stage) => PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "saving_connections",
                    processed,
                    total,
                    stage,
                    persist: false));

            if (saved)
            {
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "saving_connections",
                    1,
                    1,
                    "Semantic relations saved",
                    persist: false);
            }

            return saved;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save semantic relation cache: {ex.Message}");
            return false;
        }
    }

    private void TrySaveIndexingProgress(string[] paths, IndexingProgressUpdate update)
    {
        if (update == null || currentScanResult == null || paths.Length != 1 || !Directory.Exists(paths[0]))
        {
            return;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            _sqliteCache.SaveIndexingProgress(paths[0], signature, currentScanResult, update);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to persist indexing progress: {ex.Message}");
        }
    }

    private ProjectIndexingState? TryLoadIndexingProgress(string folderPath, ProjectScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            return _sqliteCache.LoadIndexingState(folderPath, signature);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load indexing progress: {ex.Message}");
            return null;
        }
    }

    private List<AssetHandle>? LoadIndexCache(string folderPath, ProjectScanResult scanResult)
    {
        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            return _sqliteCache.LoadIndexCache(folderPath, signature);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load index cache: {ex.Message}");
            return null;
        }
    }

    private bool HasCompletedLazyConnectionBuild(string folderPath, ProjectScanResult scanResult)
    {
        var state = TryLoadIndexingProgress(folderPath, scanResult);
        if (state == null || !IsConnectionReadyStatus(state.Status))
        {
            return false;
        }

        return HasSavedSemanticRelations(folderPath, scanResult);
    }

    private bool HasCompletedLazyStructureBuild(string folderPath, ProjectScanResult scanResult)
    {
        var state = TryLoadIndexingProgress(folderPath, scanResult);
        return string.Equals(state?.Status, "structure_completed", StringComparison.OrdinalIgnoreCase)
            && HasSavedSemanticRelations(folderPath, scanResult);
    }

    private bool HasSavedSemanticRelations(string folderPath, ProjectScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            return _sqliteCache.HasSemanticRelations(folderPath, signature);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to inspect saved semantic relations: {ex.Message}");
            return false;
        }
    }

    private static bool IsConnectionReadyStatus(string? status)
    {
        return string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompletedLazyIndexingStatus(string? status)
    {
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanUseLazySemanticRelationCache(string folderPath)
    {
        return currentScanResult != null
            && !string.IsNullOrWhiteSpace(folderPath)
            && Directory.Exists(folderPath)
            && HasCompletedLazyConnectionBuild(folderPath, currentScanResult);
    }

    private async Task BuildLazyConnectionsIfNeededAsync(string[] paths, bool force = false)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || paths.Length != 1 || !Directory.Exists(paths[0]))
        {
            return;
        }

        var folderPath = paths[0];
        if (!force && HasCompletedLazyConnectionBuild(folderPath, currentScanResult))
        {
            StatusStripUpdate("Connections already built for this index.");
            return;
        }

        await BuildLazyConnectionsAsync(folderPath, currentScanResult);
    }

    private bool ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(string[] paths)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || paths.Length != 1 || !Directory.Exists(paths[0]))
        {
            return false;
        }

        var folderPath = paths[0];
        if (HasCompletedLazyConnectionBuild(folderPath, currentScanResult))
        {
            return false;
        }

        var state = TryLoadIndexingProgress(folderPath, currentScanResult);
        if (state != null)
        {
            ShowIndexingProgressPanel(state);
        }

        StatusStripUpdate("Connections did not produce saved relations; asset structure finalization was skipped.");
        return true;
    }

    private async Task BuildLazyConnectionsAsync(string folderPath, ProjectScanResult scanResult)
    {
        if (isBuildingLazyConnections)
        {
            return;
        }

        isBuildingLazyConnections = true;
        ViewModel.IsIndexingActive = true;
        ViewModel.IsPauseEnabled = false;
        ViewModel.IsResumeEnabled = false;
        ViewModel.IsStopEnabled = false;
        ViewModel.LoadingProgress = 0;
        
        // Show connecting progress panel immediately on the UI thread before background scan starts
        ShowIndexingProgressPanel("connecting", 0, 100, 100, 0, string.Empty, string.Empty, null);

        var lastSourcePath = string.Empty;
        var lastProcessedFiles = 0;
        var sourcePaths = new List<string>();

        try
        {
            StatusStripUpdate("Preparing connection build...");
            sourcePaths = await Task.Run(GetLazyConnectionSourcePaths);
            if (sourcePaths.Count == 0)
            {
                Logger.Warning("Unable to build lazy connections: no source files could be resolved from the project index.");
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", 0, 0, string.Empty, string.Empty);
                StatusStripUpdate("Connections build skipped: no source files resolved.");
                return;
            }

            StatusStripUpdate($"Building connections... 0/{sourcePaths.Count:N0} files");
            PublishLazyConnectionProgress(folderPath, scanResult, "connecting", 0, sourcePaths.Count, string.Empty, string.Empty);

            var buildResult = await Task.Run(async () => await BuildLazySemanticRelationsForSourcesAsync(
                sourcePaths,
                (processedFiles, currentSourcePath) =>
                {
                    lastSourcePath = currentSourcePath;
                    lastProcessedFiles = processedFiles;
                    PublishLazyConnectionProgress(
                        folderPath,
                        scanResult,
                        "connecting",
                        processedFiles,
                        sourcePaths.Count,
                        currentSourcePath,
                        currentSourcePath);

                    var percent = sourcePaths.Count == 0
                        ? 100
                        : Math.Min(100, Math.Max(0, processedFiles * 100.0 / sourcePaths.Count));
                    Dispatcher.UIThread.Post(() =>
                    {
                        ViewModel.LoadingProgress = (int)percent;
                        StatusStripUpdate($"Building connections... {processedFiles:N0}/{sourcePaths.Count:N0} files ({percent:0.#}%)");
                    }, DispatcherPriority.Background);
                }));
            var relations = buildResult.Relations;
            var diagnostics = buildResult.Diagnostics;
            var diagnosticSummary = diagnostics.FormatStatusSummary(sourcePaths.Count, relations);
            Logger.Info($"Lazy connection diagnostics: {diagnostics.FormatDetailedSummary(sourcePaths.Count, relations)}");

            if (!relations.HasMaterialRelations)
            {
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", sourcePaths.Count, sourcePaths.Count, diagnosticSummary, lastSourcePath);
                StatusStripUpdate($"Connections build produced no mesh/material/texture relations. {diagnosticSummary}");
                return;
            }

            var saved = await Task.Run(() => TrySaveSemanticRelations(folderPath, scanResult, relations, replaceExisting: true));
            if (!saved)
            {
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", sourcePaths.Count, sourcePaths.Count, diagnosticSummary, lastSourcePath);
                StatusStripUpdate($"Connections build finished, but saving to SQLite failed. {diagnosticSummary}");
                return;
            }

            PublishLazyConnectionProgress(folderPath, scanResult, "connections_completed", sourcePaths.Count, sourcePaths.Count, string.Empty, lastSourcePath);
            ViewModel.LoadingProgress = 100;
            StatusStripUpdate(
                $"Connections complete. Model groups: {relations.ModelGroups.Count:N0}, group meshes: {relations.ModelGroupMeshes.Count:N0}, edges: {relations.AssetEdges.Count:N0}, mesh materials: {relations.MeshMaterials.Count:N0}, material textures: {relations.MaterialTextures.Count:N0}.");
        }
        catch (MemoryPressureException ex)
        {
            PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", lastProcessedFiles, sourcePaths.Count, string.Empty, lastSourcePath);
            ShowMemoryPressureError(ex);
        }
        catch (Exception ex)
        {
            PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", lastProcessedFiles, sourcePaths.Count, string.Empty, lastSourcePath);
            Logger.Error($"Failed to build lazy asset connections: {ex}", ex);
            StatusStripUpdate("Connections build failed.");
        }
        finally
        {
            ViewModel.IsIndexingActive = false;
            ViewModel.IsPauseEnabled = false;
            ViewModel.IsResumeEnabled = false;
            ViewModel.IsStopEnabled = false;
            isBuildingLazyConnections = false;
        }
    }

    private void PublishLazyConnectionProgress(
        string folderPath,
        ProjectScanResult scanResult,
        string status,
        int processedFiles,
        int totalFiles,
        string currentFile,
        string lastReadFile)
    {
        var safeTotal = Math.Max(0, totalFiles);
        var safeProcessed = Math.Clamp(processedFiles, 0, safeTotal == 0 ? processedFiles : safeTotal);
        var update = new IndexingProgressUpdate
        {
            Status = status,
            TotalFiles = safeTotal,
            ProcessedFiles = safeProcessed,
            PendingFiles = Math.Max(0, safeTotal - safeProcessed),
            PercentComplete = safeTotal <= 0 ? 100 : Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal)),
            CurrentFile = currentFile ?? string.Empty,
            LastReadFile = lastReadFile ?? string.Empty,
            NewlyReadFiles = Array.Empty<string>()
        };

        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMs = (nowTicks - lastConnectionDbWriteTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        bool isTerminal = string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "connections_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

        if (processedFiles == 0 || processedFiles == safeTotal || isTerminal || elapsedMs >= 2000)
        {
            lastConnectionDbWriteTicks = nowTicks;
            try
            {
                var signature = _sqliteCache.GetFolderSignature(scanResult);
                _sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to persist connection build progress: {ex.Message}");
            }
        }

        ShowIndexingProgressPanel(update);
    }

    private void PublishStructureBuildProgress(
        string status,
        int processedSteps,
        int totalSteps,
        string currentStage,
        bool persist = true,
        int percentDecimals = 1)
    {
        var safeTotal = Math.Max(1, totalSteps);
        var safeProcessed = Math.Clamp(processedSteps, 0, safeTotal);
        var percentComplete = Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal));
        var update = new IndexingProgressUpdate
        {
            Status = status,
            TotalFiles = safeTotal,
            ProcessedFiles = safeProcessed,
            PendingFiles = Math.Max(0, safeTotal - safeProcessed),
            PercentComplete = percentComplete,
            CurrentFile = currentStage ?? string.Empty,
            LastReadFile = currentStage ?? string.Empty,
            NewlyReadFiles = Array.Empty<string>()
        };

        if (persist && currentScanResult != null)
        {
            var folderPath = GetCurrentCacheFolderPath();
            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                var scanResult = currentScanResult;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var signature = _sqliteCache.GetFolderSignature(scanResult);
                        _sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to persist structure build progress: {ex.Message}");
                    }
                });
            }
        }

        ShowIndexingProgressPanel(update, percentDecimals);
    }

    private List<string> GetLazyConnectionSourcePaths()
    {
        var sourcePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handle in assetsManager.ProjectIndex.GetHandles())
        {
            if (handle == null)
            {
                continue;
            }

            RememberLazyHandleSourcePath(handle);

            if (!LazyConnectionReferenceTypes.Contains(handle.Type))
            {
                continue;
            }

            var sourcePath = ResolveLazyHandleSourcePath(handle);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            if (seen.Contains(sourcePath))
            {
                continue;
            }

            if (failedPaths.Contains(sourcePath))
            {
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                failedPaths.Add(sourcePath);
                continue;
            }

            var fullPath = Path.GetFullPath(sourcePath);
            if (seen.Add(fullPath))
            {
                sourcePaths.Add(fullPath);
            }
        }

        sourcePaths.Sort(StringComparer.OrdinalIgnoreCase);
        return sourcePaths;
    }

    private async Task<LazyConnectionBuildResult> BuildLazySemanticRelationsForSourcesAsync(
        IReadOnlyList<string> sourcePaths,
        Action<int, string> reportProgress)
    {
        var mergedRelations = new SemanticAssetRelations();
        var diagnostics = new LazyConnectionBuildDiagnostics();
        diagnostics.SourceCount = sourcePaths.Count;
        var failedSources = new List<string>();
        const int batchSize = 32;

        for (var i = 0; i < sourcePaths.Count; i += batchSize)
        {
            var batch = sourcePaths.Skip(i).Take(batchSize).ToList();
            diagnostics.BatchCount++;
            try
            {
                await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
                AssetsManager.ThrowIfMemoryPressureTooHigh("building lazy connections");
                await assetsManager.LoadFilesAsync(batch.ToArray());

                for (var j = 0; j < batch.Count; j++)
                {
                    var sourcePath = batch[j];
                    var overallIndex = i + j;
                    try
                    {
                        var filesForSource = GetLoadedFilesForConnectionSource(sourcePath);
                        diagnostics.RecordLoadedFiles(sourcePath, filesForSource.Count);
                        if (filesForSource.Count == 0)
                        {
                            continue;
                        }

                        using (assetsManager.LruCache.SuspendEviction())
                        {
                            var objectsBefore = filesForSource.Sum(file => file.Objects.Count);
                            foreach (var file in filesForSource)
                            {
                                MaterializeReferenceObjects(file, LazyConnectionReferenceTypes, diagnostics);
                            }
                            var objectsAfter = filesForSource.Sum(file => file.Objects.Count);
                            diagnostics.RecordMaterializedSource(sourcePath, objectsBefore, objectsAfter);

                            BuildAssetReferenceIndexesBackground(
                                filesForSource,
                                new List<AssetItem>(),
                                out _,
                                out _,
                                out _,
                                out _,
                                out _,
                                out _,
                                out _,
                                out var semanticRelations);

                            mergedRelations.Merge(semanticRelations);
                            diagnostics.RecordRelationsPass(semanticRelations);
                        }
                        UnloadConnectionBuildObjects(filesForSource);
                    }
                    catch (Exception ex)
                    {
                        failedSources.Add(sourcePath);
                        diagnostics.FailedSources++;
                        Logger.Warning($"Failed to build connections for {Path.GetFileName(sourcePath)}: {ex.Message}");
                    }
                    finally
                    {
                        reportProgress(overallIndex + 1, sourcePath);
                    }
                }
            }
            catch (MemoryPressureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                foreach (var sourcePath in batch)
                {
                    failedSources.Add(sourcePath);
                    diagnostics.FailedSources++;
                    Logger.Warning($"Failed to load/build connections for {Path.GetFileName(sourcePath)} in batch: {ex.Message}");
                }
                for (var j = 0; j < batch.Count; j++)
                {
                    var sourcePath = batch[j];
                    var overallIndex = i + j;
                    reportProgress(overallIndex + 1, sourcePath);
                }
            }
            finally
            {
                await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
                assetsManager.ClearLoadedFilesKeepIndex();
            }
        }

        if (failedSources.Count > 0)
        {
            Logger.Warning($"Connections build completed with {failedSources.Count:N0} failed source file(s). First failure: {Path.GetFileName(failedSources[0])}");
        }

        return new LazyConnectionBuildResult(mergedRelations, diagnostics);
    }

    private List<SerializedFile> GetLoadedFilesForConnectionSource(string sourcePath)
    {
        lock (assetsManager.loadLock)
        {
            var directMatches = assetsManager.assetsFileList
                .Where(file => file != null
                    && (IsSameLazySource(file.originalPath, sourcePath)
                        || IsSameLazySource(file.fullName, sourcePath)))
                .ToList();
            if (directMatches.Count > 0)
            {
                return directMatches;
            }

            var expectedSerializedFileNames = assetsManager.ProjectIndex.GetHandles()
                .Where(handle => handle != null && IsSameLazySource(handle.OriginalPath, sourcePath))
                .Select(handle => handle.SerializedFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expectedSerializedFileNames.Count == 0)
            {
                return directMatches;
            }

            return assetsManager.assetsFileList
                .Where(file => file != null && expectedSerializedFileNames.Contains(file.fileName))
                .ToList();
        }
    }

    private void UnloadConnectionBuildObjects(IReadOnlyList<SerializedFile> files)
    {
        foreach (var file in files)
        {
            if (file == null)
            {
                continue;
            }

            foreach (var handle in assetsManager.ProjectIndex.GetHandlesForFile(file.fileName).ToList())
            {
                if (handle == null
                    || !ReferenceEquals(handle.SourceFile, file)
                    || !LazyConnectionReferenceTypes.Contains(handle.Type)
                    || IsLazyHandlePinnedForPreview(handle))
                {
                    continue;
                }

                UnloadAsset(handle);
            }
        }
    }

    private bool IsLazyHandlePinnedForPreview(AssetHandle handle)
    {
        if (handle == null || string.IsNullOrEmpty(handle.UniqueID))
        {
            return false;
        }

        lock (preloaderLock)
        {
            return preloadedUniqueIds.Contains(handle.UniqueID)
                || string.Equals(_currentlySelectedUniqueID, handle.UniqueID, StringComparison.Ordinal);
        }
    }
}
