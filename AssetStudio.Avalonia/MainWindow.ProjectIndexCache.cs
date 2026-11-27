using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    internal const int LazyConnectionStagingFlushRowThreshold = SemanticGraphService.LazyConnectionStagingFlushRowThreshold;

    internal static int CountBufferedSemanticRelationRows(SemanticAssetRelations? relations) =>
        SemanticGraphService.CountBufferedSemanticRelationRows(relations);

    private static string GetFolderCacheKey(string folderPath) =>
        ProjectLoadingService.GetFolderCacheKey(folderPath);

    private CacheObservabilityService CreateCacheObservabilityService() =>
        new(_sqliteCache.CacheDirectory);

    internal CacheDiskUsageReport InspectCacheDiskUsageAndHealth() =>
        CreateCacheObservabilityService().InspectDiskUsageAndHealth(_sqliteCache);

    internal CacheRecoveryReport InspectAndRecoverProjectCaches() =>
        CreateCacheObservabilityService().InspectAndRecoverCache(_sqliteCache);

    private async Task ClearCurrentProjectCacheCommandHandlerAsync()
    {
        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            await ViewModel.DialogService.ShowMessageAsync("Clear project cache", "No loaded project folder was found to clear.");
            return;
        }

        try
        {
            var clearResult = CreateCacheObservabilityService().SafeClearProjectCache(_sqliteCache, folderPath);
            SpriteHelper.ClearAtlasCache();
            AssetExportHelper.ClearShaderGuidCache();

            lock (previewCacheLock)
            {
                meshToMaterialsCache = null;
                meshAssociatedRenderersCache = null;
                meshSourceTypesCache = null;
                materialMainTextureCache = null;
                materialPreviewMaterialCache = null;
                materialTextureSlotsCache = null;
            }
            _animationRigPreviewCache.Clear();

            StatusStripUpdate($"Cleared project cache for: {folderPath} ({CacheObservabilityService.FormatBytes(clearResult.ReclaimedBytes)} freed)");
        }
        catch (Exception ex)
        {
            await ViewModel.DialogService.ShowErrorAsync("Clear project cache", $"Failed to clear project cache:\n{ex.Message}");
            StatusStripUpdate("Failed to clear project cache.");
        }
    }

    private string GetCurrentCacheFolderPath() =>
        ProjectLoadingService.ResolveCacheFolderPath(appSettings.LoadFolderPath, assetsManager.ProjectRoot);

    private static void DeleteDecompressedCacheFolder(string folderPath) =>
        ProjectLoadingService.DeleteDecompressedCacheFolder(folderPath);

    internal static string GetDecompressedBundleScopeKey(string folderPath) =>
        ProjectLoadingService.GetDecompressedBundleScopeKey(folderPath);

    private static void DeletePreviewCacheFolder(string folderPath) =>
        ProjectLoadingService.DeletePreviewCacheFolder(folderPath);

    private MeshPreviewGeometryCache? LoadMeshPreviewGeometryCache(Mesh mesh, float densityPercent) =>
        ProjectLoadingService.LoadMeshPreviewGeometryCache(_sqliteCache, GetCurrentCacheFolderPath(), currentScanResult, mesh, densityPercent);

    private void SaveMeshPreviewGeometryCache(Mesh mesh, float densityPercent, MeshPreviewGeometryCache cache) =>
        ProjectLoadingService.EnqueueSaveMeshPreviewGeometryCache(_sqliteCache, GetCurrentCacheFolderPath(), currentScanResult, mesh, densityPercent, cache);

    private TexturePreviewImageResult? TryLoadCachedTexturePreviewThumbnailByAssetId(string textureAssetId, int maxDimension) =>
        ProjectLoadingService.TryLoadCachedTexturePreviewThumbnailByAssetId(
            _sqliteCache,
            GetCurrentCacheFolderPath(),
            currentScanResult,
            textureAssetId,
            maxDimension,
            TexturePreviewThumbnailAlgorithmVersion);

    private TexturePreviewImageResult? LoadTexturePreviewThumbnail(Texture2D texture, int maxDimension) =>
        ProjectLoadingService.LoadTexturePreviewThumbnail(
            _sqliteCache,
            GetCurrentCacheFolderPath(),
            currentScanResult,
            texture,
            maxDimension,
            TexturePreviewThumbnailAlgorithmVersion,
            LimitPreviewImage);

    private static void DeleteDirectoryInsideRoot(string root, string targetDirectory) =>
        ProjectLoadingService.DeleteDirectoryInsideRoot(root, targetDirectory);

    private void SaveIndexCache(
        string folderPath,
        ProjectScanResult scanResult,
        bool preserveSemanticRelations = false,
        Action<int, int, string>? progress = null) =>
        ProjectLoadingService.SaveIndexCache(
            _sqliteCache,
            assetsManager,
            folderPath,
            scanResult,
            preserveSemanticRelations,
            progress,
            ShowIndexingProgressPanel);

    private void PublishDatabaseWriteProgress(
        string folderPath,
        ProjectScanResult scanResult,
        string status,
        int processed,
        int total,
        string stage,
        bool persist) =>
        ProjectLoadingService.PublishDatabaseWriteProgress(
            _sqliteCache,
            folderPath,
            scanResult,
            status,
            processed,
            total,
            stage,
            persist,
            ShowIndexingProgressPanel);

    private bool TrySaveSemanticRelations(SemanticAssetRelations relations) =>
        ProjectLoadingService.TrySaveSemanticRelations(
            _sqliteCache,
            GetCurrentCacheFolderPath(),
            currentScanResult,
            relations,
            replaceExisting: false,
            ShowIndexingProgressPanel);

    private bool TrySaveSemanticRelations(string folderPath, ProjectScanResult scanResult, SemanticAssetRelations relations, bool replaceExisting = false) =>
        ProjectLoadingService.TrySaveSemanticRelations(
            _sqliteCache,
            folderPath,
            scanResult,
            relations,
            replaceExisting,
            ShowIndexingProgressPanel);

    private void TrySaveIndexingProgress(string[] paths, IndexingProgressUpdate update) =>
        ProjectLoadingService.TrySaveIndexingProgress(_sqliteCache, paths, currentScanResult, update);

    private ProjectIndexingState? TryLoadIndexingProgress(string folderPath, ProjectScanResult scanResult) =>
        ProjectLoadingService.TryLoadIndexingProgress(_sqliteCache, folderPath, scanResult);

    private List<AssetHandle>? LoadIndexCache(string folderPath, ProjectScanResult scanResult) =>
        ProjectLoadingService.LoadIndexCache(_sqliteCache, folderPath, scanResult);

    private bool HasCompletedLazyConnectionBuild(string folderPath, ProjectScanResult scanResult) =>
        ProjectLoadingService.HasCompletedLazyConnectionBuild(_sqliteCache, folderPath, scanResult);

    private bool HasCompletedLazyStructureBuild(string folderPath, ProjectScanResult scanResult) =>
        ProjectLoadingService.HasCompletedLazyStructureBuild(_sqliteCache, folderPath, scanResult);

    private bool HasSavedSemanticRelations(string folderPath, ProjectScanResult scanResult) =>
        ProjectLoadingService.HasSavedSemanticRelations(_sqliteCache, folderPath, scanResult);

    private static bool IsConnectionReadyStatus(string? status) =>
        ProjectLoadingService.IsConnectionReadyStatus(status);

    private static bool IsCompletedLazyIndexingStatus(string? status) =>
        ProjectLoadingService.IsCompletedLazyIndexingStatus(status);

    private bool CanUseLazySemanticRelationCache(string folderPath) =>
        ProjectLoadingService.CanUseLazySemanticRelationCache(_sqliteCache, folderPath, currentScanResult);

    private async Task BuildLazyConnectionsIfNeededAsync(
        string[] paths,
        bool force = false,
        IReadOnlyList<string>? incrementalSourcePaths = null,
        bool replaceExisting = true)
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

        await BuildLazyConnectionsAsync(folderPath, currentScanResult, incrementalSourcePaths, replaceExisting);
    }

    private bool ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(string[] paths) =>
        ProjectLoadingService.ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(
            _sqliteCache,
            assetsManager.LazyLoading,
            currentScanResult,
            paths,
            state => ShowIndexingProgressPanel(state),
            StatusStripUpdate);

    private async Task BuildLazyConnectionsAsync(
        string folderPath,
        ProjectScanResult scanResult,
        IReadOnlyList<string>? incrementalSourcePaths = null,
        bool replaceExisting = true)
    {
        if (isBuildingLazyConnections || IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        var connectionSessionId = lazyIndexingSessionId;
        bool IsConnectionAborted() =>
            IsWindowClosingOrClosed
            || _isReturningToProjectManager
            || connectionSessionId != lazyIndexingSessionId;

        isBuildingLazyConnections = true;
        ViewModel.IsIndexingActive = true;
        ViewModel.IsPauseEnabled = false;
        ViewModel.IsResumeEnabled = false;
        ViewModel.IsStopEnabled = false;
        ViewModel.LoadingProgress = 0;

        ShowIndexingProgressPanel("connecting", 0, 100, 100, 0, string.Empty, string.Empty, null);

        var lastSourcePath = string.Empty;
        var lastProcessedFiles = 0;
        var sourcePaths = new List<string>();
        var isIncrementalMerge = incrementalSourcePaths != null && !replaceExisting;

        try
        {
            StatusStripUpdate("Preparing connection build...");
            var allCandidatePaths = await Task.Run(GetLazyConnectionSourcePaths);
            if (IsConnectionAborted())
            {
                return;
            }

            if (isIncrementalMerge)
            {
                var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                var targetSet = new HashSet<string>(comparer);
                for (var idx = 0; idx < incrementalSourcePaths!.Count; idx++)
                {
                    var p = incrementalSourcePaths[idx];
                    if (string.IsNullOrWhiteSpace(p))
                    {
                        continue;
                    }

                    targetSet.Add(p);
                    try
                    {
                        targetSet.Add(Path.GetFullPath(p));
                    }
                    catch
                    {
                        // Keep raw path if normalization fails.
                    }
                }

                for (var idx = 0; idx < allCandidatePaths.Count; idx++)
                {
                    if (targetSet.Contains(allCandidatePaths[idx]))
                    {
                        sourcePaths.Add(allCandidatePaths[idx]);
                    }
                }

                if (sourcePaths.Count == 0)
                {
                    if (HasSavedSemanticRelations(folderPath, scanResult))
                    {
                        PublishLazyConnectionProgress(folderPath, scanResult, "connections_completed", 0, 0, string.Empty, string.Empty);
                        ViewModel.LoadingProgress = ComputeEffectiveLoadingProgress(100);
                        StatusStripUpdate($"Connections up to date (preserved cached relations for unchanged files).{FormatPartialCoverageSuffix()}");
                        TriggerAutoRefreshActivePreviewIfNeeded();
                        return;
                    }

                    // No prior saved relations exist yet; fall back to full connection build.
                    isIncrementalMerge = false;
                    replaceExisting = true;
                    sourcePaths = allCandidatePaths;
                }
            }
            else
            {
                sourcePaths = allCandidatePaths;
            }

            if (sourcePaths.Count == 0)
            {
                Logger.Warning("Unable to build lazy connections: no source files could be resolved from the project index.");
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", 0, 0, string.Empty, string.Empty);
                StatusStripUpdate("Connections build skipped: no source files resolved.");
                return;
            }

            StatusStripUpdate(isIncrementalMerge
                ? $"Updating connections incrementally... 0/{sourcePaths.Count:N0} affected file(s)"
                : $"Building connections... 0/{sourcePaths.Count:N0} files");
            PublishLazyConnectionProgress(folderPath, scanResult, "connecting", 0, sourcePaths.Count, string.Empty, string.Empty);

            var signature = _sqliteCache.GetFolderSignature(scanResult);
            using var stagingSession = await Task.Run(() => _sqliteCache.BeginSemanticStagingSession(folderPath, signature));
            if (IsConnectionAborted())
            {
                stagingSession.Abort();
                return;
            }

            var buildResult = await Task.Run(async () => await BuildLazySemanticRelationsForSourcesAsync(
                sourcePaths,
                (processedFiles, currentSourcePath) =>
                {
                    if (IsConnectionAborted())
                    {
                        return;
                    }

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
                        if (IsConnectionAborted())
                        {
                            return;
                        }
                        ViewModel.LoadingProgress = ComputeEffectiveLoadingProgress(percent);
                        StatusStripUpdate($"Building connections... {processedFiles:N0}/{sourcePaths.Count:N0} files ({ComputeEffectiveLoadingProgress(percent):0.#}%){FormatPartialCoverageSuffix()}");
                    }, DispatcherPriority.Background);
                },
                stagingSession,
                LazyConnectionStagingFlushRowThreshold));
            if (IsConnectionAborted())
            {
                stagingSession.Abort();
                return;
            }

            var relations = buildResult.Relations;
            var diagnostics = buildResult.Diagnostics;
            var diagnosticSummary = SemanticGraphService.FormatLazyConnectionStatusSummary(sourcePaths.Count, diagnostics);
            Logger.Info($"Lazy connection diagnostics: {SemanticGraphService.FormatLazyConnectionDetailedSummary(sourcePaths.Count, diagnostics, relations)}");

            if (!stagingSession.HasStagedMaterialRelations)
            {
                stagingSession.Abort();
                if (isIncrementalMerge && HasSavedSemanticRelations(folderPath, scanResult))
                {
                    PublishLazyConnectionProgress(folderPath, scanResult, "connections_completed", sourcePaths.Count, sourcePaths.Count, string.Empty, lastSourcePath);
                    ViewModel.LoadingProgress = ComputeEffectiveLoadingProgress(100);
                    StatusStripUpdate($"Connections updated incrementally ({sourcePaths.Count:N0} file(s) checked; cached relations preserved).{FormatPartialCoverageSuffix()}");
                    TriggerAutoRefreshActivePreviewIfNeeded();
                    return;
                }

                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", sourcePaths.Count, sourcePaths.Count, diagnosticSummary, lastSourcePath);
                StatusStripUpdate($"Connections build produced no mesh/material/texture relations. {diagnosticSummary}");
                return;
            }

            var saved = await Task.Run(() => TryCommitSemanticStagingSession(folderPath, scanResult, stagingSession, replaceExisting));
            if (IsConnectionAborted())
            {
                return;
            }

            if (!saved)
            {
                stagingSession.Abort();
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", sourcePaths.Count, sourcePaths.Count, diagnosticSummary, lastSourcePath);
                StatusStripUpdate($"Connections build finished, but saving to SQLite failed. {diagnosticSummary}");
                return;
            }

            PublishLazyConnectionProgress(folderPath, scanResult, "connections_completed", sourcePaths.Count, sourcePaths.Count, string.Empty, lastSourcePath);
            ViewModel.LoadingProgress = ComputeEffectiveLoadingProgress(100);
            StatusStripUpdate(
                $"Connections complete. Model groups: {diagnostics.ModelGroups:N0}, group meshes: {diagnostics.ModelGroupMeshes:N0}, edges: {diagnostics.AssetEdges:N0}, mesh materials: {diagnostics.MeshMaterials:N0}, material textures: {diagnostics.MaterialTextures:N0}.{FormatPartialCoverageSuffix()}");
            TriggerAutoRefreshActivePreviewIfNeeded();
        }
        catch (MemoryPressureException ex)
        {
            if (IsConnectionAborted())
            {
                return;
            }
            PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", lastProcessedFiles, sourcePaths.Count, string.Empty, lastSourcePath);
            ShowMemoryPressureError(ex);
        }
        catch (Exception ex)
        {
            if (IsConnectionAborted())
            {
                return;
            }
            PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", lastProcessedFiles, sourcePaths.Count, string.Empty, lastSourcePath);
            Logger.Error($"Failed to build lazy asset connections: {ex}", ex);
            StatusStripUpdate("Connections build failed.");
        }
        finally
        {
            if (!IsWindowClosingOrClosed)
            {
                ViewModel.IsIndexingActive = false;
                ViewModel.IsPauseEnabled = false;
                ViewModel.IsResumeEnabled = false;
                ViewModel.IsStopEnabled = false;
            }
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
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        ProjectLoadingService.PublishLazyConnectionProgress(
            _sqliteCache,
            folderPath,
            scanResult,
            status,
            processedFiles,
            totalFiles,
            currentFile,
            lastReadFile,
            ref lastConnectionDbWriteTicks,
            update => ShowIndexingProgressPanel(update));
    }

    private void PublishStructureBuildProgress(
        string status,
        int processedSteps,
        int totalSteps,
        string currentStage,
        bool persist = true,
        int percentDecimals = 1)
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        ProjectLoadingService.PublishStructureBuildProgress(
            _sqliteCache,
            GetCurrentCacheFolderPath(),
            currentScanResult,
            status,
            processedSteps,
            totalSteps,
            currentStage,
            persist,
            percentDecimals,
            ShowIndexingProgressPanel);
    }

    private List<string> GetLazyConnectionSourcePaths() =>
        SemanticGraphService.GetLazyConnectionSourcePaths(
            assetsManager.ProjectIndex,
            RememberLazyHandleSourcePath,
            ResolveLazyHandleSourcePath);

    private bool TryCommitSemanticStagingSession(
        string folderPath,
        ProjectScanResult scanResult,
        SQLiteProjectIndexCache.SemanticStagingSession stagingSession,
        bool replaceExisting = true) =>
        ProjectLoadingService.TryCommitSemanticStagingSession(
            _sqliteCache,
            folderPath,
            scanResult,
            stagingSession,
            ShowIndexingProgressPanel,
            replaceExisting);

    internal Task<LazyConnectionBuildResult> BuildLazySemanticRelationsForSourcesAsync(
        IReadOnlyList<string> sourcePaths,
        Action<int, string> reportProgress,
        SQLiteProjectIndexCache.SemanticStagingSession? stagingSession = null,
        int stagingFlushRowThreshold = LazyConnectionStagingFlushRowThreshold) =>
        SemanticGraphService.BuildLazySemanticRelationsForSourcesAsync(
            assetsManager,
            sourcePaths,
            reportProgress,
            stagingSession,
            stagingFlushRowThreshold,
            WaitForUserInteractionPriorityToClearAsync,
            IsLazyHandlePinnedForPreview);

    private bool IsLazyHandlePinnedForPreview(AssetHandle handle) =>
        ProjectLoadingService.IsLazyHandlePinnedForPreview(
            handle,
            preloaderLock,
            preloadedUniqueIds,
            _currentlySelectedUniqueID);
}
