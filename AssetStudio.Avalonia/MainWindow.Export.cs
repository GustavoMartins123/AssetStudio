using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal const int DefaultCurrentExportWriteIntervalMs = AssetExportService.DefaultCurrentExportWriteIntervalMs;
    internal const int DefaultExportUiProgressIntervalMs = AssetExportService.DefaultExportUiProgressIntervalMs;

    private readonly AssetExportService _exportService = new();

    internal ExportViewModel ExportState => ViewModel.Export;

    internal static int GetExportThreadCount(int requestedDegreeOfParallelism = 0)
        => AssetExportService.GetExportThreadCount(requestedDegreeOfParallelism);

    private Task ExportAllAssetsAsync(ExportMode mode)
    {
        return _isArenaAssetListMode
            ? ExportArenaAssetsAsync(SnapshotExportableArenaIndices(), mode)
            : ExportAssets(_eagerExportableAssets.ToList(), mode);
    }

    private Task ExportFilteredAssetsAsync(ExportMode mode)
    {
        return _isArenaAssetListMode
            ? ExportArenaAssetsAsync(SnapshotVisibleArenaIndices(), mode)
            : ExportAssets(_eagerVisibleAssets.ToList(), mode);
    }

    private Task ExportAllAssetsListAsync()
    {
        return _isArenaAssetListMode
            ? ExportArenaAssetsListAsync(SnapshotExportableArenaIndices())
            : ExportAssetsList(_eagerExportableAssets.ToList());
    }

    private Task ExportFilteredAssetsListAsync()
    {
        return _isArenaAssetListMode
            ? ExportArenaAssetsListAsync(SnapshotVisibleArenaIndices())
            : ExportAssetsList(_eagerVisibleAssets.ToList());
    }

    private async Task ExportArenaAssetsListAsync(int[] arenaIndices)
    {
        if (arenaIndices.Length == 0)
        {
            StatusStripUpdate("No assets to export.");
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);
        var arena = assetsManager.ProjectIndex.Arena;

        var cancellationToken = ExportState.BeginExport(
            ExportOperationMode.XmlList,
            arenaIndices.Length,
            savePath,
            "Exporting asset list to XML...");
        StatusStripUpdate("Exporting asset list to XML...");

        await Task.Run(() =>
        {
            var filename = Path.Combine(savePath, "assets.xml");
            try
            {
                var exportedCount = WriteArenaAssetsXml(arena, arenaIndices, filename, cancellationToken);
                var status = $"Finished exporting asset list to XML with {exportedCount} items.";
                ExportState.Complete(exportedCount, statusText: status);
                StatusStripUpdate(status);
                if (exportOptions.OpenAfterExport)
                {
                    OpenFolder(savePath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ExportState.Complete(0, statusText: "XML asset list export cancelled.", wasCancelled: true);
                StatusStripUpdate("XML asset list export cancelled.");
            }
            catch (Exception ex)
            {
                Logger.Error("Error exporting asset list to XML", ex);
                var errorStatus = $"Error exporting asset list to XML: {ex.Message}";
                ExportState.Fail(errorStatus);
                StatusStripUpdate(errorStatus);
            }
        });
    }

    internal static int WriteArenaAssetsXml(
        AssetIndexArena arena,
        IReadOnlyList<int> arenaIndices,
        string filename,
        CancellationToken cancellationToken = default)
        => AssetExportService.WriteArenaAssetsXml(arena, arenaIndices, filename, cancellationToken);

    internal static bool ArenaIndicesContainType(AssetIndexArena arena, IReadOnlyList<int> arenaIndices, ClassIDType targetType)
        => AssetExportService.ArenaIndicesContainType(arena, arenaIndices, targetType);

    internal static List<AssetItem> MaterializeArenaItemsForExport(
        AssetIndexArena arena,
        IReadOnlyList<int> arenaIndices,
        ExportMode mode)
        => AssetExportService.MaterializeArenaItemsForExport(arena, arenaIndices, mode);

    private async Task ExportArenaAssetsAsync(int[] arenaIndices, ExportMode mode)
    {
        if (arenaIndices.Length == 0)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        var arena = assetsManager.ProjectIndex.Arena;
        if (!assemblyLoader.Loaded && (mode == ExportMode.Convert || mode == ExportMode.Dump))
        {
            var hasMonoBehaviour = await Task.Run(() => ArenaIndicesContainType(arena, arenaIndices, ClassIDType.MonoBehaviour));
            if (hasMonoBehaviour)
            {
                await EnsureAssemblyLoaderReadyAsync();
            }
        }

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);

        BulkExportSummary summary = default;
        bool hadAnyItems = false;

        var cancellationToken = ExportState.BeginExport(
            mode,
            arenaIndices.Length,
            savePath,
            $"Preparing {arenaIndices.Length:N0} assets for export...");
        StatusStripUpdate($"Preparing {arenaIndices.Length:N0} assets for export...");

        try
        {
            await Task.Run(() =>
            {
                var toExport = MaterializeArenaItemsForExport(arena, arenaIndices, mode);
                if (toExport.Count == 0)
                {
                    return;
                }

                hadAnyItems = true;
                var startStatus = $"Exporting {toExport.Count:N0} assets...";
                ExportState.UpdateStatusText(startStatus);
                StatusStripUpdate(startStatus);

                summary = ExportAssetsBatchCore(
                    toExport,
                    mode,
                    savePath,
                    progress => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewModel.LoadingProgress = progress),
                    cancellationToken: cancellationToken);
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting arena assets", ex);
            var errorStatus = $"Error exporting assets: {ex.Message}";
            ExportState.Fail(errorStatus);
            StatusStripUpdate(errorStatus);
            return;
        }

        if (!hadAnyItems)
        {
            ExportState.Complete(0, statusText: "No exportable assets loaded");
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        var errorReportPath = WriteErrorReport(savePath, summary.Errors, logger);
        var status = AssetExportService.FormatBulkExportStatusSummary(summary, errorReportPath);
        ExportState.Complete(
            summary.Exported,
            summary.Skipped,
            summary.Failed,
            status,
            errorReportPath,
            summary.WasCancelled);
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport && !summary.WasCancelled && summary.Exported > 0)
        {
            OpenFolder(savePath);
        }
    }

    private async Task ExportErrorLogCommandHandlerAsync()
    {
        await ErrorExporter.ExportErrorLog(this, logger, StatusStripUpdate);
    }

    private async Task ExportAssetsList(List<AssetItem> toExport)
    {
        if (toExport.Count == 0)
        {
            StatusStripUpdate("No assets to export.");
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);

        var cancellationToken = ExportState.BeginExport(
            ExportOperationMode.XmlList,
            toExport.Count,
            savePath,
            "Exporting asset list to XML...");
        StatusStripUpdate("Exporting asset list to XML...");

        await Task.Run(() =>
        {
            var filename = Path.Combine(savePath, "assets.xml");
            try
            {
                var exportedCount = AssetExportService.WriteAssetsXml(toExport, filename, cancellationToken);
                var status = $"Finished exporting asset list to XML with {exportedCount} items.";
                ExportState.Complete(exportedCount, statusText: status);
                StatusStripUpdate(status);
                if (exportOptions.OpenAfterExport)
                {
                    OpenFolder(savePath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ExportState.Complete(0, statusText: "XML asset list export cancelled.", wasCancelled: true);
                StatusStripUpdate("XML asset list export cancelled.");
            }
            catch (Exception ex)
            {
                Logger.Error("Error exporting asset list to XML", ex);
                var errorStatus = $"Error exporting asset list to XML: {ex.Message}";
                ExportState.Fail(errorStatus);
                StatusStripUpdate(errorStatus);
            }
        });
    }

    private List<AssetItem> GetSelectedAssets()
    {
        var selected = new List<AssetItem>();
        var selectedItems = AssetListDataGrid.SelectedItems;
        if (selectedItems == null)
        {
            return selected;
        }

        foreach (var item in selectedItems)
        {
            if (item is AssetItem assetItem)
            {
                selected.Add(assetItem);
            }
        }
        return selected;
    }

    private async Task ExportAssets(List<AssetItem> toExport, ExportMode mode)
    {
        toExport = AssetExportService.FilterAndOrderAssetsForExport(toExport, mode);

        if (toExport.Count == 0)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        if (!assemblyLoader.Loaded && (mode == ExportMode.Convert || mode == ExportMode.Dump) && toExport.Any(x => x.Type == ClassIDType.MonoBehaviour))
        {
            await EnsureAssemblyLoaderReadyAsync();
        }

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);

        int total = toExport.Count;
        BulkExportSummary summary = default;

        var cancellationToken = ExportState.BeginExport(
            mode,
            total,
            savePath,
            $"Exporting {total} assets...");
        StatusStripUpdate($"Exporting {total} assets...");

        try
        {
            await Task.Run(() =>
            {
                summary = ExportAssetsBatchCore(
                    toExport,
                    mode,
                    savePath,
                    progress => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewModel.LoadingProgress = progress),
                    cancellationToken: cancellationToken);
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting assets", ex);
            var errorStatus = $"Error exporting assets: {ex.Message}";
            ExportState.Fail(errorStatus);
            StatusStripUpdate(errorStatus);
            return;
        }

        var errorReportPath = WriteErrorReport(savePath, summary.Errors, logger);
        var status = AssetExportService.FormatBulkExportStatusSummary(summary, errorReportPath);
        ExportState.Complete(
            summary.Exported,
            summary.Skipped,
            summary.Failed,
            status,
            errorReportPath,
            summary.WasCancelled);
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport && !summary.WasCancelled && summary.Exported > 0)
        {
            OpenFolder(savePath);
        }
    }

    internal BulkExportSummary ExportAssetsBatchCore(
        IReadOnlyList<AssetItem> toExport,
        ExportMode mode,
        string savePath,
        Action<int>? uiProgressCallback = null,
        int maxDegreeOfParallelism = 0,
        int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
        long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit,
        Action<LazyExportSourceBatch, int>? onBatchLoaded = null,
        CancellationToken cancellationToken = default)
    {
        var batchOptions = new AssetBatchExportOptions
        {
            ExportOptions = exportOptions,
            AssetsFiles = assetsManager.assetsFileList,
            FilesLock = assetsManager.loadLock,
            AssemblyLoader = assemblyLoader,
            IsLazyLoading = assetsManager.LazyLoading,
            LazySourceKeyResolver = ResolveLazyExportSourceKey,
            EnsureLazyBatchLoaded = EnsureLazyBatchLoadedForExport,
            ReleaseLazyBatch = ReleaseLazyExportBatch,
            SnapshotInitiallyLoadedFileNames = () =>
            {
                lock (assetsManager.loadLock)
                {
                    return assetsManager.assetsFileList
                        .Select(f => f.fileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            },
            SuspendLruEviction = () => assetsManager.LruCache.SuspendEviction(),
            ConvertAssetExporter = ExportConvertFile,
            FormatPrefab = FormatPrefab,
            ViewModel = ExportState,
            UiProgressCallback = uiProgressCallback,
            StatusUpdate = StatusStripUpdate,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            MaxSourceFilesPerBatch = maxSourceFilesPerBatch,
            MaxAssetBytesPerBatch = maxAssetBytesPerBatch,
            CurrentExportWriteIntervalMs = DefaultCurrentExportWriteIntervalMs,
            UiProgressIntervalMs = DefaultExportUiProgressIntervalMs,
            OnBatchLoaded = onBatchLoaded,
            ThrowOnCancellation = false
        };

        return _exportService.ExportAssetsBatch(toExport, mode, savePath, batchOptions, cancellationToken);
    }

    private async Task ExportAnimatorWithSelectedAnimationClips(List<AssetItem> selectedAssets)
    {
        if (!BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(selectedAssets))
        {
            StatusStripUpdate("Select a model (Animator, Mesh, Avatar, SkinnedMeshRenderer, or GameObject) and AnimationClips, or select multiple AnimationClips.");
            return;
        }

        var initialResolution = BatchAnimationExportHelper.ResolveAssetListSelection(selectedAssets);
        var selectedExportRoot = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(selectedExportRoot)) return;

        SaveExportFolder(selectedExportRoot);
        var subfolderName = initialResolution.Kind switch
        {
            BatchAnimationSelectionKind.ExplicitAnimator => "Animator",
            BatchAnimationSelectionKind.ExplicitAvatar => "Avatar",
            BatchAnimationSelectionKind.ExplicitMesh => "Mesh",
            BatchAnimationSelectionKind.ExplicitSkinnedMeshRenderer => "Model",
            BatchAnimationSelectionKind.ExplicitGameObject => "GameObject",
            _ => "AnimatedModel"
        };
        var exportPath = Path.Combine(selectedExportRoot, subfolderName);
        Directory.CreateDirectory(exportPath);

        var targetDisplayName = initialResolution.PrimaryModelAsset?.Name ?? initialResolution.SuggestedBaseName;
        ExportState.BeginExport(
            ExportOperationMode.Animator,
            100,
            exportPath,
            $"Exporting {targetDisplayName} with {initialResolution.SelectedClipAssets.Count} AnimationClip(s)...");
        void ReportStage(int percent, string stageText)
        {
            ExportState.ReportStageProgress(percent, stageText);
            StatusStripUpdate(stageText);
        }

        ReportStage(5, $"Exporting {targetDisplayName} with {initialResolution.SelectedClipAssets.Count} AnimationClip(s)...");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportPath, "export-current.txt");
        bool success = false;
        string? exportedPrimaryFile = null;
        var exportedFiles = new List<string>();

        await Task.Run(() =>
        {
            try
            {
                var trackItem = initialResolution.PrimaryModelAsset ?? initialResolution.SelectedClipAssets.FirstOrDefault();
                if (trackItem != null)
                {
                    AssetExportService.WriteCurrentExport(currentExportPath, trackItem, 1, 1);
                }

                success = ExportSelectionWithAnimationClipsCore(
                    selectedAssets,
                    exportPath,
                    exportOptions,
                    ReportStage,
                    out exportedPrimaryFile,
                    out var errorMsg,
                    exportedFiles);
                if (!success && !string.IsNullOrWhiteSpace(errorMsg))
                {
                    exportErrors.Add(errorMsg);
                }
            }
            catch (Exception ex)
            {
                var error = $"Export Model+AnimationClips:{targetDisplayName} error";
                exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                Logger.Error(error, ex);
                StatusStripUpdate($"Error exporting {targetDisplayName}: {ex.Message}");
            }
            finally
            {
                AssetExportService.ClearCurrentExport(exportPath);
            }
        });

        var errorReportPath = WriteErrorReport(exportPath, exportErrors, logger);

        if (success)
        {
            var primaryFileName = !string.IsNullOrWhiteSpace(exportedPrimaryFile)
                ? Path.GetFileName(exportedPrimaryFile)
                : targetDisplayName;
            var splitSuffix = exportedFiles.Count > 1
                ? $" (+ {exportedFiles.Count - 1} split animation file(s))"
                : string.Empty;
            var status = $"Finished exporting {primaryFileName}{splitSuffix}";
            ExportState.ReportStageProgress(100, status);
            ExportState.Complete(Math.Max(1, exportedFiles.Count), statusText: status, errorLogPath: errorReportPath);
            StatusStripUpdate(status);
            if (exportOptions.OpenAfterExport)
            {
                OpenFolder(exportPath);
            }
        }
        else
        {
            var status = "Model + AnimationClips export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
            ExportState.Fail(status, errorReportPath);
            StatusStripUpdate(status);
        }
    }

    internal bool ExportSelectionWithAnimationClipsCore(
        IReadOnlyList<AssetItem> selectedAssets,
        string exportDirectoryOrFilePath,
        ExportOptionsState? optionsOverride,
        Action<int, string>? reportStage,
        out string? exportedPrimaryFilePath,
        out string? errorMessage,
        List<string>? exportedFilePaths = null)
    {
        exportedPrimaryFilePath = null;
        errorMessage = null;

        if (!BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(selectedAssets))
        {
            errorMessage = "Selection does not contain any AnimationClips or valid model targets.";
            return false;
        }

        using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;

        reportStage?.Invoke(10, "Loading selected assets...");
        EnsureLazyAssetsLoadedForExport(selectedAssets.Where(x => x != null).ToList());

        var resolution = BatchAnimationExportHelper.ResolveAssetListSelection(selectedAssets);
        if (!resolution.IsValid || resolution.SelectedClips.Count == 0)
        {
            errorMessage = "Failed to materialize selected AnimationClips.";
            return false;
        }

        var clips = resolution.SelectedClips.ToArray();
        var effectiveOptions = (optionsOverride ?? exportOptions).Clone();
        effectiveOptions.ExportAnimations = true;

        var modelExt = AssetExportService.GetModelFileExtension(effectiveOptions);
        var isExplicitFile = string.Equals(Path.GetExtension(exportDirectoryOrFilePath), ".glb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(exportDirectoryOrFilePath), ".gltf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(exportDirectoryOrFilePath), ".fbx", StringComparison.OrdinalIgnoreCase);

        string ResolveExportFilePath(string baseName)
        {
            if (isExplicitFile)
            {
                var dir = Path.GetDirectoryName(exportDirectoryOrFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return exportDirectoryOrFilePath;
            }

            Directory.CreateDirectory(exportDirectoryOrFilePath);
            var safeName = FixFileName(string.IsNullOrWhiteSpace(baseName) ? resolution.SuggestedBaseName : baseName);
            return AssetExportService.GetUniqueFilePath(Path.Combine(exportDirectoryOrFilePath, safeName + modelExt));
        }

        var selectedTopGameObjects = GetTopLevelSelectedGameObjects(resolution.SelectedGameObjects.ToList());

        switch (resolution.Kind)
        {
            case BatchAnimationSelectionKind.ExplicitAnimator when resolution.Animator != null:
            {
                var animator = resolution.Animator;
                var baseName = resolution.PrimaryModelAsset?.Name ?? resolution.SuggestedBaseName;
                if (TryFindModelGroupForAnimator(animator, out var animGroup, out var animGroupParts)
                    && animGroup != null
                    && animGroupParts.Count > 0)
                {
                    var groupBaseName = !string.IsNullOrWhiteSpace(animGroup.GroupName) ? animGroup.GroupName : baseName;
                    var groupFile = ResolveExportFilePath(groupBaseName);
                    if (ExportResolvedPreviewModelGroup(animGroup, animGroupParts, fallbackAvatar: null, clips, groupFile, reportStage: reportStage, optionsOverride: effectiveOptions, exportedFilePaths: exportedFilePaths))
                    {
                        exportedPrimaryFilePath = groupFile;
                        return true;
                    }
                }

                Avatar? animAvatar = null;
                animator.m_Avatar?.TryGet(out animAvatar);
                if (animAvatar != null
                    && TryBuildEagerModelGroupForAvatar(animAvatar, out var eagerAvName, out var eagerAvRoot, out var eagerAvMeshes, out var eagerAvRenderers)
                    && eagerAvMeshes.Count > 0)
                {
                    var eagerFile = ResolveExportFilePath(!string.IsNullOrWhiteSpace(eagerAvName) ? eagerAvName : baseName);
                    if (ExportEagerModelGroupWithMaterials(eagerAvName, eagerAvRoot, eagerAvMeshes, eagerAvRenderers, animAvatar, clips, eagerFile, effectiveOptions, reportStage, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = eagerFile;
                        return true;
                    }
                }

                var exportFile = ResolveExportFilePath(baseName);
                HydrateAnimatorHierarchyMaterialsForExport(animator, selectedTopGameObjects);
                reportStage?.Invoke(65, $"Converting {baseName} with {clips.Length} animation(s)...");
                IImported convert = selectedTopGameObjects.Count > 0
                    ? new ModelConverter(baseName, selectedTopGameObjects, effectiveOptions.ConvertTextureFormat, clips)
                    : new ModelConverter(animator, effectiveOptions.ConvertTextureFormat, clips);

                if (convert.MeshList.Count == 0 && animAvatar != null)
                {
                    var avatarMeshes = FindMeshesForAvatar(animAvatar);
                    if (avatarMeshes.Count > 0)
                    {
                        var renderers = avatarMeshes.Select(m => FindSkinnedRendererForAvatarMesh(animAvatar, m) ?? FindSingleRendererForMeshForExport(m)).ToList();
                        if (ExportEagerModelGroupWithMaterials(baseName, eagerRoot: null, avatarMeshes, renderers!, animAvatar, clips, exportFile, effectiveOptions, reportStage, exportedFilePaths))
                        {
                            exportedPrimaryFilePath = exportFile;
                            return true;
                        }
                    }
                }

                reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
                AssetExportService.ExportModel(convert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
                exportedPrimaryFilePath = exportFile;
                return true;
            }

            case BatchAnimationSelectionKind.ExplicitAvatar when resolution.Avatar != null:
            {
                var avatar = resolution.Avatar;
                var exportFile = ResolveExportFilePath(avatar.m_Name ?? resolution.SuggestedBaseName);
                if (ExportPreviewAvatarRigCore(avatar, clips, exportFile, reportStage, effectiveOptions, exportedFilePaths))
                {
                    exportedPrimaryFilePath = exportFile;
                    return true;
                }
                errorMessage = $"Could not resolve rigged meshes or skeleton for Avatar '{avatar.m_Name}'.";
                return false;
            }

            case BatchAnimationSelectionKind.ExplicitSkinnedMeshRenderer when resolution.SkinnedMeshRenderer != null:
            case BatchAnimationSelectionKind.ExplicitMesh when resolution.PrimaryMesh != null:
            {
                var primaryMesh = resolution.PrimaryMesh;
                var smr = resolution.SkinnedMeshRenderer;
                if (primaryMesh == null && smr?.m_Mesh != null)
                {
                    smr.m_Mesh.TryGet(out primaryMesh);
                }

                if (primaryMesh == null)
                {
                    errorMessage = "Could not resolve Mesh for export.";
                    return false;
                }

                var avatar = resolution.Avatar ?? FindBestAvatarForMesh(primaryMesh);
                if (avatar == null)
                {
                    avatar = TryResolveAvatarFromClipsOrHierarchy(clips, resolution.PrimaryModelAsset, resolution.RootGameObject);
                }

                if (TryFindModelGroupForMesh(primaryMesh, out var meshGroup, out var meshGroupParts)
                    && meshGroup != null
                    && meshGroupParts.Count > 0)
                {
                    var groupFile = ResolveExportFilePath(!string.IsNullOrWhiteSpace(meshGroup.GroupName) ? meshGroup.GroupName : resolution.SuggestedBaseName);
                    if (ExportResolvedPreviewModelGroup(meshGroup, meshGroupParts, avatar, clips, groupFile, reportStage: reportStage, optionsOverride: effectiveOptions, exportedFilePaths: exportedFilePaths))
                    {
                        exportedPrimaryFilePath = groupFile;
                        return true;
                    }
                }

                if (TryBuildEagerModelGroupForMesh(primaryMesh, out var eagerName, out var eagerRoot, out var eagerMeshes, out var eagerRenderers, out var eagerAvatar)
                    && eagerMeshes.Count > 0)
                {
                    var eagerFile = ResolveExportFilePath(!string.IsNullOrWhiteSpace(eagerName) ? eagerName : resolution.SuggestedBaseName);
                    if (ExportEagerModelGroupWithMaterials(eagerName, eagerRoot, eagerMeshes, eagerRenderers, eagerAvatar ?? avatar, clips, eagerFile, effectiveOptions, reportStage, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = eagerFile;
                        return true;
                    }
                }

                if (resolution.SelectedMeshes.Count > 1)
                {
                    var multiMeshes = resolution.SelectedMeshes.ToList();
                    var multiRenderers = multiMeshes
                        .Select(m => (Renderer?)(FindSkinnedRendererForAvatarMesh(avatar, m) ?? FindSingleRendererForMeshForExport(m)))
                        .ToList();
                    var multiFile = ResolveExportFilePath(resolution.SuggestedBaseName);
                    if (ExportEagerModelGroupWithMaterials(resolution.SuggestedBaseName, resolution.RootGameObject, multiMeshes, multiRenderers!, avatar, clips, multiFile, effectiveOptions, reportStage, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = multiFile;
                        return true;
                    }
                }

                var singleFile = ResolveExportFilePath(primaryMesh.m_Name ?? resolution.SuggestedBaseName);
                if (ExportPreviewSingleMeshCore(primaryMesh, avatar, clips, singleFile, out var actualSingleFile, reportStage, effectiveOptions, exportedFilePaths, explicitRenderer: smr))
                {
                    exportedPrimaryFilePath = actualSingleFile;
                    return true;
                }

                errorMessage = $"Failed to export mesh '{primaryMesh.m_Name}' with selected AnimationClips.";
                return false;
            }

            case BatchAnimationSelectionKind.ExplicitGameObject when resolution.RootGameObject != null || selectedTopGameObjects.Count > 0:
            {
                var rootGo = resolution.RootGameObject ?? selectedTopGameObjects[0];
                var baseName = !string.IsNullOrWhiteSpace(rootGo.m_Name) ? rootGo.m_Name : resolution.SuggestedBaseName;
                var animator = resolution.PrimaryModelAsset != null
                    ? FindAnimatorForModelExport(resolution.PrimaryModelAsset)
                    : rootGo.m_Animator;

                if (animator != null
                    && TryFindModelGroupForAnimator(animator, out var goAnimGroup, out var goAnimGroupParts)
                    && goAnimGroup != null
                    && goAnimGroupParts.Count > 0)
                {
                    var groupFile = ResolveExportFilePath(!string.IsNullOrWhiteSpace(goAnimGroup.GroupName) ? goAnimGroup.GroupName : baseName);
                    if (ExportResolvedPreviewModelGroup(goAnimGroup, goAnimGroupParts, resolution.Avatar, clips, groupFile, reportStage: reportStage, optionsOverride: effectiveOptions, exportedFilePaths: exportedFilePaths))
                    {
                        exportedPrimaryFilePath = groupFile;
                        return true;
                    }
                }

                var subMeshes = new List<Mesh>();
                var subRenderers = new List<Renderer>();
                foreach (var go in selectedTopGameObjects.Count > 0 ? selectedTopGameObjects : new List<GameObject> { rootGo })
                {
                    AssetExportService.CollectSubtreeMeshesAndRenderers(go, subMeshes, subRenderers);
                }

                if (subMeshes.Count > 0)
                {
                    if (TryFindModelGroupForMesh(subMeshes[0], out var goMeshGroup, out var goMeshParts)
                        && goMeshGroup != null
                        && goMeshParts.Count > 0)
                    {
                        var groupFile = ResolveExportFilePath(!string.IsNullOrWhiteSpace(goMeshGroup.GroupName) ? goMeshGroup.GroupName : baseName);
                        if (ExportResolvedPreviewModelGroup(goMeshGroup, goMeshParts, resolution.Avatar, clips, groupFile, reportStage: reportStage, optionsOverride: effectiveOptions, exportedFilePaths: exportedFilePaths))
                        {
                            exportedPrimaryFilePath = groupFile;
                            return true;
                        }
                    }

                    var avatar = resolution.Avatar ?? TryResolveAvatarFromClipsOrHierarchy(clips, resolution.PrimaryModelAsset, rootGo);
                    var goFile = ResolveExportFilePath(baseName);
                    if (ExportEagerModelGroupWithMaterials(baseName, rootGo, subMeshes, subRenderers, avatar, clips, goFile, effectiveOptions, reportStage, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = goFile;
                        return true;
                    }
                }

                var fallbackGoFile = ResolveExportFilePath(baseName);
                IImported goConvert = selectedTopGameObjects.Count > 1
                    ? new ModelConverter(baseName, selectedTopGameObjects, effectiveOptions.ConvertTextureFormat, clips)
                    : new ModelConverter(rootGo, effectiveOptions.ConvertTextureFormat, clips);
                if (goConvert.MeshList.Count > 0)
                {
                    AssetExportService.ExportModel(goConvert, fallbackGoFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
                    exportedPrimaryFilePath = fallbackGoFile;
                    return true;
                }

                errorMessage = $"GameObject '{baseName}' contains no exportable meshes.";
                return false;
            }

            case BatchAnimationSelectionKind.ClipsOnly:
            {
                reportStage?.Invoke(15, $"Resolving character model for {clips.Length} AnimationClip(s)...");
                var sharedCandidate = BatchAnimationExportHelper.SelectSharedPreviewCandidateForClips(
                    clips,
                    clip => BuildAnimationClipPreviewCandidates(clip, currentPreviewAvatar, currentPreviewMesh),
                    IsAnimationClipCompatibleWithAvatar,
                    IsAnimationClipCompatibleWithMesh);

                var resolvedCandidate = ResolveAnimationClipPreviewCandidate(sharedCandidate, cacheRelation: true);
                if (resolvedCandidate != null)
                {
                    var groupMeshes = resolvedCandidate.IsModelGroup
                        ? ResolveModelGroupMeshesForPreview(resolvedCandidate, CancellationToken.None)
                        : resolvedCandidate.ModelGroupMeshes;
                    var groupTransforms = resolvedCandidate.IsModelGroup
                        ? ResolveModelGroupTransformsForPreview(resolvedCandidate, groupMeshes.Count)
                        : resolvedCandidate.ModelGroupTransforms;
                    var primaryMesh = resolvedCandidate.Mesh ?? groupMeshes.FirstOrDefault();
                    var avatar = resolvedCandidate.Avatar ?? (primaryMesh != null ? FindBestAvatarForMesh(primaryMesh) : null);

                    var hasMultiGroup = resolvedCandidate.IsModelGroup
                        || groupMeshes.Count > 1
                        || resolvedCandidate.ModelGroupMeshInfos.Count > 1
                        || resolvedCandidate.ModelGroupMeshIds.Count > 1;

                    var candidateBaseName = !string.IsNullOrWhiteSpace(resolvedCandidate.ModelGroupName)
                        ? resolvedCandidate.ModelGroupName
                        : !string.IsNullOrWhiteSpace(primaryMesh?.m_Name)
                            ? primaryMesh!.m_Name
                            : !string.IsNullOrWhiteSpace(avatar?.m_Name)
                                ? avatar!.m_Name
                                : resolution.SuggestedBaseName;

                    var target = new PreviewModelExportTarget
                    {
                        Kind = hasMultiGroup ? PreviewModelExportKind.AnimatedModelGroup : PreviewModelExportKind.AnimatedSingleMesh,
                        SuggestedBaseName = candidateBaseName,
                        ExportFullModelGroup = true,
                        ModelGroupId = resolvedCandidate.ModelGroupId,
                        ModelGroupName = resolvedCandidate.ModelGroupName,
                        PrimaryMesh = primaryMesh,
                        Avatar = avatar,
                        AnimationClip = clips[0],
                        CandidateMeshes = groupMeshes,
                        CandidateMeshInfos = resolvedCandidate.ModelGroupMeshInfos,
                        CandidateTransforms = groupTransforms
                    };

                    var exportFile = ResolveExportFilePath(candidateBaseName);
                    if (ExportPreviewModelTargetCore(target, clips, preferFullModelGroup: true, exportFile, out var actualFile, reportStage, effectiveOptions, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = actualFile;
                        return true;
                    }
                }

                // Fallback: scan in-memory loaded files for any compatible Avatar or Mesh
                var fallbackAvatar = TryResolveAvatarFromClipsOrHierarchy(clips, null, null);
                if (fallbackAvatar != null)
                {
                    var exportFile = ResolveExportFilePath(fallbackAvatar.m_Name ?? resolution.SuggestedBaseName);
                    if (ExportPreviewAvatarRigCore(fallbackAvatar, clips, exportFile, reportStage, effectiveOptions, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = exportFile;
                        return true;
                    }
                }

                var fallbackMesh = TryFindCompatibleMeshForClipsInLoadedFiles(clips);
                if (fallbackMesh != null)
                {
                    var exportFile = ResolveExportFilePath(fallbackMesh.m_Name ?? resolution.SuggestedBaseName);
                    if (ExportPreviewSingleMeshCore(fallbackMesh, fallbackAvatar, clips, exportFile, out var actualFile, reportStage, effectiveOptions, exportedFilePaths))
                    {
                        exportedPrimaryFilePath = actualFile;
                        return true;
                    }
                }

                errorMessage = "Could not resolve a compatible character ModelGroup, Avatar, or Mesh for the selected AnimationClips.";
                return false;
            }
        }

        errorMessage = "No valid model target resolved for export.";
        return false;
    }

    private void HydrateAnimatorHierarchyMaterialsForExport(Animator animator, IReadOnlyList<GameObject> selectedGameObjects)
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        var visitedMaterials = new HashSet<Material>();
        var visitedTextures = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<GameObject>();
        if (selectedGameObjects.Count > 0)
        {
            roots.AddRange(selectedGameObjects);
        }
        else if (animator.m_GameObject != null && animator.m_GameObject.TryGet(out var animGo) && animGo != null)
        {
            roots.Add(animGo);
        }

        var subMeshes = new List<Mesh>();
        var subRenderers = new List<Renderer>();
        foreach (var root in roots)
        {
            AssetExportService.CollectSubtreeMeshesAndRenderers(root, subMeshes, subRenderers);
        }

        foreach (var renderer in subRenderers)
        {
            EnsureRendererMaterialsLoadedForExport(renderer, visitedMaterials, visitedTextures);
        }
    }

    private Avatar? TryResolveAvatarFromClipsOrHierarchy(
        IReadOnlyList<AnimationClip> clips,
        AssetItem? modelAssetItem,
        GameObject? rootGo)
    {
        if (modelAssetItem != null)
        {
            var anim = FindAnimatorForModelExport(modelAssetItem);
            if (anim?.m_Avatar != null && anim.m_Avatar.TryGet(out var animAv) && animAv != null)
            {
                return animAv;
            }
        }

        if (rootGo?.m_Animator?.m_Avatar != null && rootGo.m_Animator.m_Avatar.TryGet(out var goAv) && goAv != null)
        {
            return goAv;
        }

        for (var i = 0; i < clips.Count; i++)
        {
            var avatars = FindCandidateAvatarsForAnimationClip(clips[i]);
            if (avatars.Count > 0)
            {
                return avatars[0];
            }
        }

        var loadedFiles = GetLoadedFilesSnapshot();
        for (var f = 0; f < loadedFiles.Count; f++)
        {
            var file = loadedFiles[f];
            if (file == null) continue;
            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            foreach (var av in objs.OfType<Avatar>())
            {
                if (clips.Any(c => IsAnimationClipCompatibleWithAvatar(c, av)))
                {
                    return av;
                }
            }
        }

        return null;
    }

    private Mesh? TryFindCompatibleMeshForClipsInLoadedFiles(IReadOnlyList<AnimationClip> clips)
    {
        for (var i = 0; i < clips.Count; i++)
        {
            var cachedMeshes = TryLoadMeshesForAnimationClipFromSemanticCache(clips[i]);
            if (cachedMeshes.Count > 0)
            {
                return cachedMeshes[0];
            }
        }

        var loadedFiles = GetLoadedFilesSnapshot();
        for (var f = 0; f < loadedFiles.Count; f++)
        {
            var file = loadedFiles[f];
            if (file == null) continue;
            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            foreach (var mesh in objs.OfType<Mesh>())
            {
                if (clips.Any(c => IsAnimationClipCompatibleWithMesh(c, mesh)))
                {
                    return mesh;
                }
            }
        }

        return null;
    }

    private bool ExportEagerModelGroupWithMaterials(
        string groupName,
        GameObject? eagerRoot,
        IReadOnlyList<Mesh> meshes,
        IReadOnlyList<Renderer> renderers,
        Avatar? avatar,
        AnimationClip[]? clips,
        string exportFile,
        ExportOptionsState effectiveOptions,
        Action<int, string>? reportStage = null,
        List<string>? exportedFilePaths = null)
    {
        if (meshes.Count == 0 && eagerRoot == null)
        {
            return false;
        }

        var visitedMaterials = new HashSet<Material>();
        var visitedTextures = new HashSet<string>(StringComparer.Ordinal);
        var partMaterials = new List<IReadOnlyList<Material>>(meshes.Count);
        var partNames = new List<string>(meshes.Count);
        var resolvedRenderers = new List<Renderer>(meshes.Count);

        for (var i = 0; i < meshes.Count; i++)
        {
            var pct = 20 + (int)((i + 1) * 35.0 / Math.Max(1, meshes.Count));
            reportStage?.Invoke(pct, $"Preparing part {i + 1}/{meshes.Count}...");

            var mesh = meshes[i];
            var renderer = (renderers != null && i < renderers.Count ? renderers[i] : null)
                ?? FindSkinnedRendererForAvatarMesh(avatar, mesh)
                ?? FindSingleRendererForMeshForExport(mesh);

            var mats = ResolveExportMaterialsForPart(mesh, renderer, visitedMaterials, visitedTextures);
            resolvedRenderers.Add(renderer!);
            partMaterials.Add(mats.Where(m => m != null).Cast<Material>().ToList());

            string? goName = null;
            if (renderer?.m_GameObject != null && renderer.m_GameObject.TryGet(out var rGo))
            {
                goName = rGo?.m_Name;
            }
            partNames.Add(!string.IsNullOrWhiteSpace(goName) ? goName! : (mesh.m_Name ?? $"Part_{i}"));
        }

        reportStage?.Invoke(65, $"Converting {meshes.Count} part(s) & {clips?.Length ?? 0} animation(s)...");
        var convert = ModelConverter.CreateFromModelGroup(
            groupName,
            eagerRoot,
            meshes,
            resolvedRenderers,
            avatar,
            clips,
            effectiveOptions.ConvertTextureFormat,
            combineMeshes: false,
            partTransforms: null,
            partMaterials: partMaterials,
            partNames: partNames);

        if (convert.MeshList.Count == 0)
        {
            return false;
        }

        reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
        AssetExportService.ExportModel(convert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
        return true;
    }

    private async Task ExportAllObjectsSplitCommandHandlerAsync()
    {
        if (sceneTreeNodes.Count == 0)
        {
            StatusStripUpdate("No Objects available for export");
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);
        await ExportSplitObjects(savePath, sceneTreeNodes.SelectMany(x => x.Children).ToList(), null, createObjectFolders: true);
    }

    private async Task ExportSelectedObjectsSplit(bool includeAnimations)
    {
        var selectedNodes = GetSelectedParentNodes();
        if (selectedNodes.Count == 0)
        {
            StatusStripUpdate("No Object selected for export.");
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);
        var exportPath = Path.Combine(savePath, "GameObject");
        Directory.CreateDirectory(exportPath);
        await ExportSplitObjects(exportPath, selectedNodes, includeAnimations ? GetSelectedAnimationClips() : null, createObjectFolders: false);
    }

    private async Task ExportSelectedObjectsMerge(bool includeAnimations)
    {
        var gameObjects = GetSelectedParentNodes()
            .Where(x => x.GameObject != null)
            .Select(x => x.GameObject!)
            .ToList();
        if (gameObjects.Count == 0)
        {
            StatusStripUpdate("No Object selected for export.");
            return;
        }

        var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
        var suggestedName = FixFileName(gameObjects[0].m_Name) + " (merge)" + modelExt;
        var exportFile = await PickSaveModelDialogAsync("Save merged model", suggestedName);
        if (string.IsNullOrWhiteSpace(exportFile)) return;
        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var clips = includeAnimations ? GetSelectedAnimationClips() : null;
        ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            1,
            exportFolder,
            $"Exporting {Path.GetFileName(exportFile)}");
        StatusStripUpdate($"Exporting {Path.GetFileName(exportFile)}");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportFolder ?? "", "export-current.txt");
        bool success = false;
        await Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(exportFolder))
                {
                    Directory.CreateDirectory(exportFolder);
                    Directory.CreateDirectory(Path.GetDirectoryName(currentExportPath)!);
                    File.WriteAllText(currentExportPath,
                        $"Exporting merged model{Environment.NewLine}" +
                        $"Name: {Path.GetFileName(exportFile)}{Environment.NewLine}" +
                        $"Objects: {gameObjects.Count}{Environment.NewLine}",
                        Encoding.UTF8);
                }

                IImported convert = gameObjects.Count == 1
                    ? CreateModelConverter(gameObjects[0], clips)
                    : CreateModelConverter(Path.GetFileNameWithoutExtension(exportFile), gameObjects, clips);
                ExportFbx(convert, exportFile);
                success = true;
            }
            catch (Exception ex)
            {
                var error = $"Export Model:{Path.GetFileName(exportFile)} error";
                exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                Logger.Error(error, ex);
                StatusStripUpdate($"Error exporting merged model: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(exportFolder))
                {
                    AssetExportService.ClearCurrentExport(exportFolder);
                }
            }
        });

        var reportPath = exportFolder ?? "";
        var errorReportPath = WriteErrorReport(reportPath, exportErrors, logger);

        progressBar.Value = 100;
        if (success)
        {
            var status = $"Finished exporting {Path.GetFileName(exportFile)}";
            ExportState.Complete(1, statusText: status, errorLogPath: errorReportPath);
            StatusStripUpdate(status);
            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            var status = "Merged model export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
            ExportState.Fail(status, errorReportPath);
            StatusStripUpdate(status);
        }
    }

    private async Task ExportSplitObjects(string exportRoot, List<GameObjectNode> nodes, AnimationClip[]? clips, bool createObjectFolders)
    {
        var exportNodes = nodes
            .Where(node => node.GameObject != null)
            .Where(HasModelContent)
            .ToList();
        if (exportNodes.Count == 0)
        {
            StatusStripUpdate("No Objects available for export");
            return;
        }

        Directory.CreateDirectory(exportRoot);
        var cancellationToken = ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            exportNodes.Count,
            exportRoot,
            $"Exporting {exportNodes.Count} objects...");
        StatusStripUpdate($"Exporting {exportNodes.Count} objects...");

        SplitObjectsExportSummary summary = default;
        try
        {
            summary = await Task.Run(() => _exportService.ExportSplitObjects(
                exportRoot,
                exportNodes,
                clips,
                createObjectFolders,
                exportOptions,
                viewModel: ExportState,
                uiProgressCallback: progress => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => progressBar.Value = progress),
                statusUpdate: StatusStripUpdate,
                cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting split objects", ex);
            var errorStatus = $"Error exporting objects: {ex.Message}";
            ExportState.Fail(errorStatus);
            StatusStripUpdate(errorStatus);
            return;
        }

        var errorReportPath = WriteErrorReport(exportRoot, summary.Errors, logger);
        var status = AssetExportService.FormatSplitObjectsStatusSummary(
            summary.Exported,
            summary.Failed,
            errorReportPath,
            summary.WasCancelled);
        ExportState.Complete(
            summary.Exported,
            skipped: 0,
            failed: summary.Failed,
            statusText: status,
            errorLogPath: errorReportPath,
            wasCancelled: summary.WasCancelled);
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport && !summary.WasCancelled)
        {
            OpenFolder(exportRoot);
        }
    }

    private AnimationClip[]? GetSelectedAnimationClips()
    {
        var clips = GetSelectedAssets()
            .Where(x => x.Type == ClassIDType.AnimationClip)
            .Select(x => (AnimationClip)x.Asset!)
            .ToArray();
        return clips.Length == 0 ? null : clips;
    }

    private ModelConverter CreateModelConverter(string rootName, List<GameObject> gameObjects, AnimationClip[]? clips)
    {
        return clips == null
            ? new ModelConverter(rootName, gameObjects, exportOptions.ConvertTextureFormat)
            : new ModelConverter(rootName, gameObjects, exportOptions.ConvertTextureFormat, clips);
    }

    private ModelConverter CreateModelConverter(GameObject gameObject, AnimationClip[]? clips)
    {
        return clips == null
            ? new ModelConverter(gameObject, exportOptions.ConvertTextureFormat)
            : new ModelConverter(gameObject, exportOptions.ConvertTextureFormat, clips);
    }

    private List<GameObjectNode> GetSelectedParentNodes()
    {
        var nodes = new List<GameObjectNode>();
        foreach (var root in sceneTreeNodes)
        {
            CollectSelectedParentNodes(root, nodes);
        }
        return nodes;
    }

    private static void CollectSelectedParentNodes(GameObjectNode node, List<GameObjectNode> nodes)
    {
        if (node.GameObject != null && node.IsChecked)
        {
            nodes.Add(node);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectSelectedParentNodes(child, nodes);
        }
    }

    private static bool HasModelContent(GameObjectNode node)
    {
        var gameObjects = new List<GameObject>();
        CollectGameObjects(node, gameObjects);
        return gameObjects.Any(x => x.m_SkinnedMeshRenderer != null || x.m_MeshFilter != null);
    }

    private static void CollectGameObjects(GameObjectNode node, List<GameObject> gameObjects)
    {
        if (node.GameObject != null)
        {
            gameObjects.Add(node.GameObject);
        }

        foreach (var child in node.Children)
        {
            CollectGameObjects(child, gameObjects);
        }
    }

    internal static string ReserveUniqueDirectoryPath(
        string directoryPath,
        ConcurrentDictionary<string, byte> reservedPaths)
        => AssetExportService.ReserveUniqueDirectoryPath(directoryPath, reservedPaths);

    internal static string ReserveUniqueFilePath(
        string filePath,
        ConcurrentDictionary<string, byte> reservedPaths)
        => AssetExportService.ReserveUniqueFilePath(filePath, reservedPaths);

    private void LinkFbxSubAssetsToSceneNodes()
    {
        LinkFbxSubAssetsToSceneNodesBackground(exportableAssets, sceneTreeNodes);
    }

    private static GameObjectNode GetFbxRootNode(GameObjectNode node, string fbxContainer)
    {
        var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
        var current = node;
        while (current.Parent?.GameObject != null)
        {
            current = current.Parent;
        }

        var namedRoot = FindNodeByName(current, fbxName);
        return namedRoot ?? current;
    }

    private static GameObjectNode? FindNodeByName(GameObjectNode node, string name)
    {
        if (node.GameObject != null && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindNodeByName(child, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private GameObjectNode? FindSceneNodeByName(string name)
    {
        foreach (var root in sceneTreeNodes)
        {
            var match = FindNodeByName(root, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static List<GameObject> GetTopLevelSelectedGameObjects(List<GameObject> gameObjects)
    {
        return gameObjects
            .Where(gameObject => !gameObjects.Any(other => other != gameObject && IsDescendantOf(gameObject, other)))
            .ToList();
    }

    private static bool IsDescendantOf(GameObject child, GameObject possibleParent)
    {
        var transform = child.m_Transform;
        while (transform != null && transform.m_Father.TryGet(out var father))
        {
            if (father.m_GameObject.TryGet(out var fatherGameObject) && fatherGameObject == possibleParent)
            {
                return true;
            }
            transform = father;
        }
        return false;
    }

    private void ShowOriginalFile(AssetItem item)
    {
        if (item.SourceFile == null)
        {
            StatusStripUpdate("Original file path is unavailable.");
            return;
        }

        var sourcePath = !string.IsNullOrEmpty(item.SourceFile.originalPath)
            ? item.SourceFile.originalPath
            : item.SourceFile.fullName;
        if (string.IsNullOrEmpty(sourcePath))
        {
            StatusStripUpdate("Original file path is unavailable.");
            return;
        }

        var resolvedPath = sourcePath;
        if (!File.Exists(resolvedPath) && item.Handle != null)
        {
            var resolved = ResolveLazyHandleSourcePath(item.Handle);
            if (resolved != null)
            {
                resolvedPath = resolved;
            }
        }

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            StatusStripUpdate($"Original file does not exist on disk: {resolvedPath}");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var cleanPath = resolvedPath.Replace("\"", "");
                if (cleanPath.EndsWith("\\"))
                {
                    cleanPath += ".";
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cleanPath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                startInfo.ArgumentList.Add("-R");
                startInfo.ArgumentList.Add(resolvedPath);
                Process.Start(startInfo);
            }
            else
            {
                var folder = Directory.Exists(resolvedPath) ? resolvedPath : Path.GetDirectoryName(resolvedPath);
                if (string.IsNullOrEmpty(folder)) return;
                var startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                startInfo.ArgumentList.Add(folder);
                Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Unable to show original file: {ex.Message}");
        }
    }

    internal static string GetSafeRelativeExportDirectory(string? container)
        => AssetExportService.GetSafeRelativeExportDirectory(container);

    internal static string FormatCurrentExportContent(
        string? typeString,
        string? name,
        long pathId,
        string? source,
        int index,
        int count)
        => AssetExportService.FormatCurrentExportContent(typeString, name, pathId, source, index, count);

    internal sealed class ExportProgressTracker : Services.ExportProgressTracker
    {
        public ExportProgressTracker(
            string currentExportPath,
            int totalCount,
            Action<int>? uiProgressCallback = null,
            int fileWriteIntervalMs = DefaultCurrentExportWriteIntervalMs,
            int uiProgressIntervalMs = DefaultExportUiProgressIntervalMs)
            : base(currentExportPath, totalCount, uiProgressCallback, fileWriteIntervalMs, uiProgressIntervalMs)
        {
        }
    }

    private static string GetAssetSourcePath(AssetItem asset)
        => AssetExportService.GetAssetSourcePath(asset);

    internal static void TryDeletePartialExport(string filePath)
        => AssetExportService.TryDeletePartialExport(filePath);

    private static void WriteTextExport(string filePath, string text)
        => AssetExportService.WriteTextExport(filePath, text);

    private static string? GetFbxContainerPath(string? container)
        => AssetExportService.GetFbxContainerPath(container);

    private static string SanitizeExtension(string? extension, string fallback)
        => AssetExportService.SanitizeExtension(extension, fallback);

    private static string? WriteErrorReport(string savePath, IReadOnlyList<string> exportErrors, GUILogger logger)
        => AssetExportService.WriteErrorReport(savePath, exportErrors, logger.GetMessages(LoggerEvent.Error));

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private bool ExportConvertFile(AssetItem item, string exportPath, Services.ExportConversionContext conversionContext)
    {
        if (ShouldSkipConvertedAsset(item))
        {
            return false;
        }

        var assetObj = item.Asset;
        if (assetObj == null)
        {
            return false;
        }

        Directory.CreateDirectory(exportPath);
        var fileName = FixFileName(AssetExportService.GetExportFileName(item));

        var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
        switch (assetObj)
        {
            case Animator m_Animator:
            {
                bool exported = false;
                if (TryFindModelGroupForAnimator(m_Animator, out var animatorGroup, out var animatorGroupMeshes)
                    && animatorGroup != null
                    && animatorGroupMeshes.Count > 0)
                {
                    var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(animatorGroup.GroupName) ? animatorGroup.GroupName : fileName);
                    if (conversionContext.TryMarkModelGroupExported(animatorGroup.GroupId)
                        && conversionContext.TryGetAssetFilePath(exportPath, groupFileName, modelExt, item, out var groupFbxPath)
                        && ExportModelGroupFbx(animatorGroup, animatorGroupMeshes, groupFbxPath))
                    {
                        exported = true;
                    }
                }

                if (!exported)
                {
                    var convert = new ModelConverter(m_Animator, exportOptions.ConvertTextureFormat);
                    if (convert.MeshList.Count > 0
                        && conversionContext.TryGetAssetFilePath(exportPath, fileName, modelExt, item, out var exportFullPath))
                    {
                        ExportFbx(convert, exportFullPath);
                        exported = true;
                    }
                }

                if (m_Animator.m_Avatar.TryGet(out var avatar))
                {
                    var avatarFileName = FixFileName(avatar.m_Name);
                    if (conversionContext.TryGetAssetFilePath(exportPath, avatarFileName, ".asset", item, out var avatarFullPath)
                        && AssetExportHelper.ExportAvatar(avatar, avatarFullPath))
                    {
                        exported = true;
                    }
                }
                return exported;
            }
            case Avatar m_Avatar:
            {
                bool exported = conversionContext.TryGetAssetFilePath(exportPath, fileName, ".asset", item, out var avatarFullPath)
                    && AssetExportHelper.ExportAvatar(m_Avatar, avatarFullPath);

                if (TryFindModelGroupForAvatar(m_Avatar, out var avatarGroup, out var avatarGroupMeshes)
                    && avatarGroup != null
                    && avatarGroupMeshes.Count > 0)
                {
                    var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(avatarGroup.GroupName) ? avatarGroup.GroupName : fileName);
                    if (conversionContext.TryMarkModelGroupExported(avatarGroup.GroupId)
                        && conversionContext.TryGetAssetFilePath(exportPath, groupFileName, modelExt, item, out var groupFbxPath)
                        && ExportModelGroupFbx(avatarGroup, avatarGroupMeshes, groupFbxPath))
                    {
                        exported = true;
                    }
                }
                return exported;
            }
            case Mesh m_Mesh:
            {
                return ExportMesh(item, m_Mesh, exportPath, fileName, conversionContext);
            }
            default:
            {
                return AssetExportService.ExportStandardConvertedAsset(
                    item,
                    exportPath,
                    conversionContext,
                    exportOptions,
                    assemblyLoader,
                    FormatPrefab);
            }
        }
    }

    internal static bool ShouldSkipConvertedAsset(AssetItem item)
        => AssetExportService.ShouldSkipConvertedAsset(item);

    private bool ExportMesh(
        AssetItem item,
        Mesh mesh,
        string exportPath,
        string fileName,
        Services.ExportConversionContext conversionContext)
    {
        if (TryExportMeshModelGroupFbx(item, mesh, exportPath, fileName, conversionContext, out var alreadyExportedGroup))
        {
            return true;
        }
        if (alreadyExportedGroup)
        {
            return false;
        }

        if (item.TreeNode?.GameObject != null)
        {
            var animator = FindAnimatorForModelExport(item);
            var convert = animator != null
                ? new ModelConverter(animator, exportOptions.ConvertTextureFormat)
                : new ModelConverter(item.TreeNode.GameObject, exportOptions.ConvertTextureFormat);
            var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
            if (convert.MeshList.Count > 0
                && conversionContext.TryGetAssetFilePath(exportPath, fileName, modelExt, item, out var fbxPath))
            {
                ExportFbx(convert, fbxPath);
                return true;
            }
        }

        mesh.EnsureProcessed();
        if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
        {
            return false;
        }

        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".obj", item, out var objPath)) return false;
        return AssetExportService.ExportMeshToObj(mesh, objPath);
    }

    private Animator? FindAnimatorForModelExport(AssetItem item)
    {
        for (var node = item.TreeNode; node != null; node = node.Parent)
        {
            if (node.GameObject?.m_Animator != null)
            {
                return node.GameObject.m_Animator;
            }
        }

        return item.TreeNode != null ? FindAnimatorInSceneNode(item.TreeNode) : null;
    }

    private static Animator? FindAnimatorInSceneNode(GameObjectNode node)
    {
        if (node.GameObject?.m_Animator != null)
        {
            return node.GameObject.m_Animator;
        }

        foreach (var child in node.Children)
        {
            var animator = FindAnimatorInSceneNode(child);
            if (animator != null)
            {
                return animator;
            }
        }

        return null;
    }

    internal async Task ExportPreviewModelCommandHandlerAsync()
    {
        if (ExportState.IsExporting)
        {
            return;
        }

        var selectedAsset = AssetListDataGrid?.SelectedItem as AssetItem;
        var selectedCandidate = SelectedPreviewCandidate;
        var target = PreviewModelExportHelper.ResolveExportTarget(
            PreviewSession.ActiveKind,
            selectedAsset,
            selectedCandidate,
            currentPreviewMesh,
            currentPreviewAvatar,
            currentPreviewAnimationClip,
            MeshPreview.ShowFullModelGroup);

        if (!target.IsValid)
        {
            StatusStripUpdate("No 3D model is currently active in Preview to export.");
            return;
        }

        var suggestedName = target.BuildSuggestedFileName(exportOptions);
        var exportFile = await PickSaveModelDialogAsync("Export Model", suggestedName);
        if (string.IsNullOrWhiteSpace(exportFile))
        {
            return;
        }

        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var fileDisplayName = Path.GetFileName(exportFile);
        ExportState.BeginExport(
            ExportOperationMode.Animator,
            100,
            exportFolder,
            $"Preparing export: {fileDisplayName}...");
        void ReportStage(int percent, string stageText)
        {
            ExportState.ReportStageProgress(percent, stageText);
            StatusStripUpdate(stageText);
        }

        ReportStage(5, $"Preparing export: {fileDisplayName}...");

        bool success = false;
        string actualExportFile = exportFile;
        string? errorMessage = null;

        var preferFullModelGroup = target.ExportFullModelGroup || MeshPreview.ShowFullModelGroup;
        await Task.Run(() =>
        {
            using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;
            try
            {
                if (target.SelectedAsset != null && target.SelectedAsset.Asset == null)
                {
                    ReportStage(10, $"Loading asset data for {target.SuggestedBaseName}...");
                    EnsureLazyAssetsLoadedForExport(new List<AssetItem> { target.SelectedAsset });
                }

                AnimationClip[]? clipArg = target.AnimationClip != null
                    ? new[] { target.AnimationClip }
                    : (!exportOptions.ExportAnimations ? Array.Empty<AnimationClip>() : null);

                success = ExportPreviewModelTargetCore(
                    target,
                    clipArg,
                    preferFullModelGroup,
                    exportFile,
                    out actualExportFile,
                    ReportStage,
                    exportOptions);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.Error($"Export 3D preview model failed for {target.SuggestedBaseName}", ex);
            }
        });

        if (success)
        {
            var exportedName = Path.GetFileName(actualExportFile);
            var status = $"Finished exporting {exportedName}";
            ExportState.ReportStageProgress(100, status);
            ExportState.Complete(1, statusText: status);
            StatusStripUpdate(status);
            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            var status = errorMessage != null
                ? $"Error exporting model: {errorMessage}"
                : $"Could not export active 3D preview ({target.SuggestedBaseName}).";
            if (errorMessage != null)
            {
                ExportState.Fail(status);
            }
            else
            {
                ExportState.Complete(0, statusText: status);
            }
            StatusStripUpdate(status);
        }
    }

    internal bool ExportPreviewModelTargetCore(
        PreviewModelExportTarget target,
        AnimationClip[]? clipArg,
        bool preferFullModelGroup,
        string exportFile,
        out string actualExportFile,
        Action<int, string>? reportStage = null,
        ExportOptionsState? optionsOverride = null,
        List<string>? exportedFilePaths = null)
    {
        actualExportFile = exportFile;
        bool success = false;

        if (preferFullModelGroup)
        {
            success = ExportPreviewModelGroupCore(target, clipArg, exportFile, reportStage, optionsOverride, exportedFilePaths);
        }

        if (!success && target.PrimaryMesh != null && !preferFullModelGroup)
        {
            success = ExportPreviewSingleMeshCore(target.PrimaryMesh, target.Avatar, clipArg, exportFile, out actualExportFile, reportStage, optionsOverride, exportedFilePaths);
        }
        else if (!success && preferFullModelGroup && target.PrimaryMesh != null)
        {
            // Graceful fallback to primary mesh if group metadata could not be materialized.
            success = ExportPreviewSingleMeshCore(target.PrimaryMesh, target.Avatar, clipArg, exportFile, out actualExportFile, reportStage, optionsOverride, exportedFilePaths);
        }
        else if (!success && target.Avatar != null)
        {
            success = ExportPreviewAvatarRigCore(target.Avatar, clipArg, exportFile, reportStage, optionsOverride, exportedFilePaths);
        }

        return success;
    }

    internal async Task BatchExportPreviewAnimationsCommandHandlerAsync()
    {
        if (ExportState.IsExporting)
        {
            return;
        }

        var selectedAsset = AssetListDataGrid?.SelectedItem as AssetItem;
        var selectedCandidate = SelectedPreviewCandidate;
        var target = PreviewModelExportHelper.ResolveExportTarget(
            PreviewSession.ActiveKind,
            selectedAsset,
            selectedCandidate,
            currentPreviewMesh,
            currentPreviewAvatar,
            currentPreviewAnimationClip,
            MeshPreview.ShowFullModelGroup);

        if (!target.IsValid)
        {
            StatusStripUpdate("No 3D model is currently active in Preview to batch export animations.");
            return;
        }

        StatusStripUpdate($"Discovering compatible AnimationClips for {target.SuggestedBaseName}...");
        var compatibleClips = await Task.Run(() => DiscoverCompatibleAnimationClipsForTarget(target));
        if (compatibleClips.Count == 0)
        {
            StatusStripUpdate($"No compatible AnimationClips found for {target.SuggestedBaseName}.");
            return;
        }

        var targetDisplayName = BuildBatchExportTargetDisplayName(target);
        var dialogVm = new BatchAnimationExportViewModel(targetDisplayName, compatibleClips, exportOptions);
        var dialogWindow = new BatchAnimationExportWindow(dialogVm);
        var result = await dialogWindow.ShowDialog<BatchAnimationExportDialogResult?>(this);
        if (result == null || result.SelectedClips.Count == 0)
        {
            return;
        }

        var effectiveOptions = exportOptions.Clone();
        effectiveOptions.ExportAnimations = true;
        effectiveOptions.ExportAnimationsSplit = result.SplitAnimations;
        effectiveOptions.ModelFormat = result.ModelFormat;

        string exportFile;
        string? exportFolder;
        var suggestedFileName = target.BuildCharacterBaseFileName(effectiveOptions);

        if (result.SplitAnimations)
        {
            var pickedFolder = await PickExportFolderDialogAsync("Select folder for split animation export");
            if (string.IsNullOrWhiteSpace(pickedFolder))
            {
                return;
            }

            SaveExportFolder(pickedFolder);
            exportFolder = pickedFolder;
            Directory.CreateDirectory(exportFolder);
            exportFile = AssetExportService.GetUniqueFilePath(Path.Combine(exportFolder, suggestedFileName));
        }
        else
        {
            var pickedFile = await PickSaveModelDialogAsync("Batch Export Model with Animations", suggestedFileName);
            if (string.IsNullOrWhiteSpace(pickedFile))
            {
                return;
            }

            exportFile = pickedFile;
            exportFolder = Path.GetDirectoryName(exportFile);
            if (!string.IsNullOrEmpty(exportFolder))
            {
                SaveExportFolder(exportFolder);
            }
        }

        var fileDisplayName = Path.GetFileName(exportFile);
        ExportState.BeginExport(
            ExportOperationMode.Animator,
            100,
            exportFolder,
            $"Preparing batch animation export ({result.SelectedClips.Count} clips): {fileDisplayName}...");

        void ReportStage(int percent, string stageText)
        {
            ExportState.ReportStageProgress(percent, stageText);
            StatusStripUpdate(stageText);
        }

        ReportStage(5, $"Preparing batch animation export ({result.SelectedClips.Count} clips)...");

        bool success = false;
        string actualExportFile = exportFile;
        string? errorMessage = null;
        var exportedFiles = new List<string>();
        var preferFullModelGroup = target.ExportFullModelGroup || MeshPreview.ShowFullModelGroup;

        await Task.Run(() =>
        {
            using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;
            try
            {
                if (target.SelectedAsset != null && target.SelectedAsset.Asset == null)
                {
                    ReportStage(8, $"Loading asset data for {target.SuggestedBaseName}...");
                    EnsureLazyAssetsLoadedForExport(new List<AssetItem> { target.SelectedAsset });
                }

                var resolvedClips = MaterializeSelectedBatchAnimationClips(result.SelectedClips, ReportStage);
                if (resolvedClips.Length == 0)
                {
                    errorMessage = "None of the selected AnimationClips could be loaded.";
                    return;
                }

                success = ExportPreviewModelTargetCore(
                    target,
                    resolvedClips,
                    preferFullModelGroup,
                    exportFile,
                    out actualExportFile,
                    ReportStage,
                    effectiveOptions,
                    exportedFiles);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.Error($"Batch animation export failed for {target.SuggestedBaseName}", ex);
            }
        });

        if (success)
        {
            var exportedName = Path.GetFileName(actualExportFile);
            var modeSummary = result.SplitAnimations
                ? $" ({Math.Max(1, exportedFiles.Count - 1)} split clip files)"
                : $" ({result.SelectedClips.Count} embedded clip(s))";
            var status = $"Finished exporting {exportedName}{modeSummary}";
            ExportState.ReportStageProgress(100, status);
            ExportState.Complete(Math.Max(1, exportedFiles.Count), statusText: status);
            StatusStripUpdate(status);
            if (effectiveOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            var status = errorMessage != null
                ? $"Error batch exporting animations: {errorMessage}"
                : $"Could not batch export animations for {target.SuggestedBaseName}.";
            if (errorMessage != null)
            {
                ExportState.Fail(status);
            }
            else
            {
                ExportState.Complete(0, statusText: status);
            }
            StatusStripUpdate(status);
        }
    }

    private static string BuildBatchExportTargetDisplayName(PreviewModelExportTarget target)
    {
        var baseName = PreviewModelExportHelper.SelectSuggestedBaseName(
            target.ExportFullModelGroup,
            target.ModelGroupName,
            target.PrimaryMesh,
            target.Avatar,
            clip: null,
            target.SelectedAsset);
        var partCount = Math.Max(target.CandidateMeshes.Count, target.CandidateMeshInfos.Count);
        return partCount > 1
            ? $"{baseName} ({partCount} parts)"
            : baseName;
    }

    internal AnimationClip[] MaterializeSelectedBatchAnimationClips(
        IReadOnlyList<BatchAnimationClipItemViewModel> selectedItems,
        Action<int, string>? reportStage = null)
    {
        var resolved = new List<AnimationClip>(selectedItems.Count);
        var seen = new HashSet<AnimationClip>();

        // Batch-load any AssetItems first
        var assetItemsToLoad = selectedItems
            .Where(x => x.Clip == null && x.AssetItem != null && x.AssetItem.Asset == null)
            .Select(x => x.AssetItem!)
            .ToList();
        if (assetItemsToLoad.Count > 0)
        {
            reportStage?.Invoke(10, $"Loading {assetItemsToLoad.Count} selected AnimationClip asset(s)...");
            EnsureLazyAssetsLoadedForExport(assetItemsToLoad);
        }

        // Group any remaining lazy ClipAssetIds by source file path so each bundle/file is loaded once
        var handlesBySource = new List<(BatchAnimationClipItemViewModel Item, AssetHandle Handle, string SourceKey)>();
        foreach (var item in selectedItems)
        {
            if (item.Clip != null)
            {
                continue;
            }
            if (item.AssetItem?.Asset is AnimationClip)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(item.ClipAssetId))
            {
                var handle = assetsManager.ProjectIndex.GetHandle(item.ClipAssetId);
                if (handle != null)
                {
                    if (handle.RealObject is not AnimationClip)
                    {
                        var sourceKey = ResolveLazyHandleSourcePath(handle) ?? handle.SerializedFileName ?? string.Empty;
                        handlesBySource.Add((item, handle, sourceKey));
                    }
                }
            }
        }

        if (handlesBySource.Count > 0)
        {
            var done = 0;
            foreach (var group in handlesBySource.GroupBy(x => x.SourceKey, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var entry in group)
                {
                    done++;
                    reportStage?.Invoke(10 + (int)(done * 10.0 / handlesBySource.Count), $"Loading AnimationClip {done}/{handlesBySource.Count} ({entry.Item.Name})...");
                    _ = ResolveSemanticRelationHandleForPreview(entry.Handle);
                }
            }
        }

        foreach (var item in selectedItems)
        {
            var clip = item.Clip
                ?? (item.AssetItem?.Asset as AnimationClip)
                ?? (!string.IsNullOrWhiteSpace(item.ClipAssetId)
                    ? (assetsManager.ProjectIndex.GetHandle(item.ClipAssetId)?.RealObject as AnimationClip
                        ?? ResolvePreviewCandidateAsset(item.ClipAssetId) as AnimationClip)
                    : null);
            if (clip != null && seen.Add(clip))
            {
                resolved.Add(clip);
            }
        }

        return resolved.ToArray();
    }

    internal List<BatchAnimationClipItemViewModel> DiscoverCompatibleAnimationClipsForTarget(PreviewModelExportTarget target)
    {
        var itemsByKey = new Dictionary<string, BatchAnimationClipItemViewModel>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();

        void AddOrUpgradeCandidate(
            string? clipId,
            string? name,
            long pathId,
            string relation,
            int score,
            AnimationClip? clipObj = null,
            AssetItem? assetItem = null,
            bool isSelected = true)
        {
            if (clipObj == null && !string.IsNullOrWhiteSpace(clipId))
            {
                clipObj = assetsManager.ProjectIndex.GetHandle(clipId!)?.RealObject as AnimationClip;
            }

            var effectiveId = !string.IsNullOrWhiteSpace(clipId)
                ? clipId!
                : clipObj != null
                    ? GetPreviewObjectKey(clipObj)
                    : assetItem?.Handle?.UniqueID ?? assetItem?.UniqueID ?? string.Empty;
            var effectivePathId = pathId != 0
                ? pathId
                : clipObj?.m_PathID ?? assetItem?.PathID ?? 0;
            var key = !string.IsNullOrWhiteSpace(effectiveId)
                ? effectiveId
                : $"pathid:{effectivePathId}";
            if (string.IsNullOrWhiteSpace(key) || key == "pathid:0")
            {
                return;
            }

            var displayName = !string.IsNullOrWhiteSpace(name)
                ? name!
                : !string.IsNullOrWhiteSpace(clipObj?.m_Name)
                    ? clipObj!.m_Name
                    : !string.IsNullOrWhiteSpace(assetItem?.Name)
                        ? assetItem!.Name
                        : !string.IsNullOrWhiteSpace(effectiveId)
                            ? GetPreviewHandleName(effectiveId, "AnimationClip")
                            : $"AnimationClip ({effectivePathId})";

            BatchAnimationExportHelper.ExtractClipTimingMetadata(clipObj, out var duration, out var sampleRate, out var frames);

            var vm = new BatchAnimationClipItemViewModel(
                displayName,
                effectiveId,
                effectivePathId,
                relation,
                score,
                duration,
                sampleRate,
                frames,
                clipObj,
                assetItem,
                isSelected);

            if (!itemsByKey.TryGetValue(key, out var existing))
            {
                itemsByKey[key] = vm;
                orderedKeys.Add(key);
            }
            else if (score > existing.MatchScore || (existing.Clip == null && clipObj != null))
            {
                itemsByKey[key] = new BatchAnimationClipItemViewModel(
                    displayName,
                    effectiveId,
                    effectivePathId,
                    score >= existing.MatchScore && !string.IsNullOrWhiteSpace(relation) ? relation : existing.Relation,
                    Math.Max(score, existing.MatchScore),
                    duration > 0f ? duration : existing.DurationSeconds,
                    sampleRate > 0f ? sampleRate : existing.SampleRate,
                    frames > 0 ? frames : existing.FrameCount,
                    clipObj ?? existing.Clip,
                    assetItem ?? existing.AssetItem,
                    existing.IsSelected || isSelected);
            }
        }

        // 1. Always include the currently active preview AnimationClip if present
        if (target.AnimationClip != null)
        {
            AddOrUpgradeCandidate(
                GetPreviewObjectKey(target.AnimationClip),
                target.AnimationClip.m_Name,
                target.AnimationClip.m_PathID,
                "Active preview",
                20000,
                target.AnimationClip);
        }

        // 2. Include any AnimationClips currently selected in the Asset List
        foreach (var selected in GetSelectedAssets())
        {
            if (selected.Type == ClassIDType.AnimationClip || selected.Asset is AnimationClip)
            {
                AddOrUpgradeCandidate(
                    selected.Handle?.UniqueID ?? selected.UniqueID,
                    selected.Name,
                    selected.PathID,
                    "Selected in Asset List",
                    19000,
                    selected.Asset as AnimationClip,
                    selected);
            }
        }

        // 3. Resolve Avatar, ModelGroupInfo, Animator, Controller, and all Mesh IDs for the target
        var avatar = target.Avatar;
        var primaryMesh = target.PrimaryMesh;
        if (avatar == null && primaryMesh != null)
        {
            avatar = FindBestAvatarForMesh(primaryMesh);
        }

        ModelGroupInfo? group = null;
        var groupParts = new List<ModelGroupMeshInfo>();
        if (!string.IsNullOrWhiteSpace(target.ModelGroupId))
        {
            group = LoadModelGroupByIdForPreview(target.ModelGroupId);
            groupParts = target.CandidateMeshInfos.Count > 0
                ? target.CandidateMeshInfos.ToList()
                : LoadModelGroupMeshesForPreview(target.ModelGroupId);
        }
        if ((group == null || groupParts.Count == 0) && avatar != null)
        {
            TryFindModelGroupForAvatar(avatar, out group, out groupParts);
        }
        if ((group == null || groupParts.Count == 0) && primaryMesh != null)
        {
            TryFindModelGroupForMesh(primaryMesh, out group, out groupParts);
        }

        var avatarId = !string.IsNullOrWhiteSpace(group?.AvatarAssetId)
            ? group!.AvatarAssetId
            : GetPreviewObjectKey(avatar);
        if (avatar == null && !string.IsNullOrWhiteSpace(avatarId))
        {
            avatar = ResolveAvatarPreviewCandidate(avatarId);
        }

        var groupId = !string.IsNullOrWhiteSpace(group?.GroupId) ? group!.GroupId : target.ModelGroupId;
        var animatorId = group?.AnimatorAssetId ?? string.Empty;
        var controllerId = group?.ControllerAssetId ?? string.Empty;

        var meshIds = new HashSet<string>(StringComparer.Ordinal);
        if (primaryMesh != null)
        {
            var pk = GetPreviewObjectKey(primaryMesh);
            if (!string.IsNullOrWhiteSpace(pk)) meshIds.Add(pk);
        }
        foreach (var cm in target.CandidateMeshes)
        {
            var ck = GetPreviewObjectKey(cm);
            if (!string.IsNullOrWhiteSpace(ck)) meshIds.Add(ck);
        }
        foreach (var info in target.CandidateMeshInfos)
        {
            if (!string.IsNullOrWhiteSpace(info.MeshAssetId)) meshIds.Add(info.MeshAssetId);
        }
        foreach (var info in groupParts)
        {
            if (!string.IsNullOrWhiteSpace(info.MeshAssetId)) meshIds.Add(info.MeshAssetId);
        }

        // 4. Query SQLite v15 AnimationClipTargets & AssetEdges
        if (assetsManager.LazyLoading && currentScanResult != null)
        {
            var folderPath = GetCurrentCacheFolderPath();
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                var signature = _sqliteCache.GetFolderSignature(currentScanResult);
                var dbClips = _sqliteCache.LoadCompatibleAnimationClipsForTarget(
                    folderPath,
                    signature,
                    avatarId,
                    groupId,
                    animatorId,
                    controllerId,
                    meshIds.ToList());
                foreach (var dbClip in dbClips)
                {
                    var label = dbClip.RelationKind switch
                    {
                        "BoneHashMatch" => "Bone match",
                        "AnimatorController" => "AnimatorController",
                        "LegacyAnimation" => "Legacy animation",
                        "Container" => "Container",
                        _ => !string.IsNullOrWhiteSpace(dbClip.RelationKind) ? dbClip.RelationKind : "Compatible rig"
                    };
                    AddOrUpgradeCandidate(
                        dbClip.ClipAssetId,
                        dbClip.ClipName,
                        dbClip.PathId,
                        label,
                        dbClip.MatchScore);
                }
            }
        }

        // 5. Inspect in-memory loaded files (covers Eager Mode and local files in Lazy Mode)
        var loadedFiles = GetLoadedFilesSnapshot();
        if (loadedFiles.Count > 0)
        {
            // If lazy mode and SQLite returned 0 additional clips, materialize AnimationClips in the target's own file(s)
            if (assetsManager.LazyLoading && itemsByKey.Count <= 1)
            {
                var targetFiles = new HashSet<SerializedFile>();
                if (avatar?.assetsFile != null) targetFiles.Add(avatar.assetsFile);
                if (primaryMesh?.assetsFile != null) targetFiles.Add(primaryMesh.assetsFile);
                if (target.AnimationClip?.assetsFile != null) targetFiles.Add(target.AnimationClip.assetsFile);
                if (target.SelectedAsset?.SourceFile != null) targetFiles.Add(target.SelectedAsset.SourceFile);

                if (targetFiles.Count > 0)
                {
                    var localRefTypes = new HashSet<ClassIDType>
                    {
                        ClassIDType.AnimationClip,
                        ClassIDType.Animator,
                        ClassIDType.AnimatorController,
                        ClassIDType.AnimatorOverrideController,
                        ClassIDType.Animation
                    };
                    using (assetsManager.LruCache.SuspendEviction())
                    {
                        foreach (var tf in targetFiles)
                        {
                            MaterializeReferenceObjects(tf, localRefTypes);
                        }
                    }
                }
            }

            var inMemoryMeshes = new List<Mesh>();
            if (primaryMesh != null) inMemoryMeshes.Add(primaryMesh);
            foreach (var cm in target.CandidateMeshes)
            {
                if (cm != null && !inMemoryMeshes.Contains(cm))
                {
                    inMemoryMeshes.Add(cm);
                }
            }

            for (var f = 0; f < loadedFiles.Count; f++)
            {
                var file = loadedFiles[f];
                if (file == null) continue;
                AssetStudio.Object[] objs;
                lock (file)
                {
                    objs = file.Objects.ToArray();
                }

                // Check Animators matching our Avatar or RootGameObject
                foreach (var anim in objs.OfType<Animator>())
                {
                    var matchesAvatar = avatar != null
                        && anim.m_Avatar != null
                        && anim.m_Avatar.TryGet(out var animAv)
                        && AreSamePreviewObject(animAv, avatar);
                    var matchesAnimatorId = !string.IsNullOrWhiteSpace(animatorId)
                        && string.Equals(GetPreviewObjectKey(anim), animatorId, StringComparison.Ordinal);

                    if (!matchesAvatar && !matchesAnimatorId)
                    {
                        continue;
                    }

                    if (anim.m_Controller != null && anim.m_Controller.TryGet(out var ctrl))
                    {
                        CollectClipsFromController(ctrl, (clip, rel) =>
                            AddOrUpgradeCandidate(GetPreviewObjectKey(clip), clip.m_Name, clip.m_PathID, rel, 15000, clip));
                    }
                }

                // Check all materialized AnimationClips for bone-hash compatibility with Avatar or Mesh
                foreach (var clip in objs.OfType<AnimationClip>())
                {
                    if (avatar != null && IsAnimationClipCompatibleWithAvatar(clip, avatar))
                    {
                        AddOrUpgradeCandidate(GetPreviewObjectKey(clip), clip.m_Name, clip.m_PathID, "Avatar bone match", 9000, clip);
                    }
                    else if (inMemoryMeshes.Count > 0 && inMemoryMeshes.Any(m => IsAnimationClipCompatibleWithMesh(clip, m)))
                    {
                        AddOrUpgradeCandidate(GetPreviewObjectKey(clip), clip.m_Name, clip.m_PathID, "Mesh bone match", 8500, clip);
                    }
                }
            }
        }

        return orderedKeys
            .Select(k => itemsByKey[k])
            .OrderByDescending(x => x.MatchScore)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectClipsFromController(
        RuntimeAnimatorController? controller,
        Action<AnimationClip, string> onClipFound)
    {
        if (controller == null)
        {
            return;
        }

        switch (controller)
        {
            case AnimatorController ac when ac.m_AnimationClips != null:
                foreach (var clipPtr in ac.m_AnimationClips)
                {
                    if (clipPtr != null && clipPtr.TryGet(out var clip) && clip != null)
                    {
                        onClipFound(clip, "AnimatorController");
                    }
                }
                break;

            case AnimatorOverrideController aoc:
                if (aoc.m_Clips != null)
                {
                    foreach (var pair in aoc.m_Clips)
                    {
                        if (pair?.m_OverrideClip != null && pair.m_OverrideClip.TryGet(out var ovClip) && ovClip != null)
                        {
                            onClipFound(ovClip, "AnimatorOverrideController");
                        }
                        else if (pair?.m_OriginalClip != null && pair.m_OriginalClip.TryGet(out var origClip) && origClip != null)
                        {
                            onClipFound(origClip, "AnimatorOverrideController");
                        }
                    }
                }
                if (aoc.m_Controller != null && aoc.m_Controller.TryGet(out var baseCtrl) && baseCtrl != null && !ReferenceEquals(baseCtrl, aoc))
                {
                    CollectClipsFromController(baseCtrl, onClipFound);
                }
                break;
        }
    }

    private Dictionary<string, Mesh> BuildInMemoryPreviewMeshLookup(PreviewModelExportTarget target)
    {
        var map = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        void AddMesh(Mesh? m)
        {
            if (m == null) return;
            var key = GetPreviewObjectKey(m);
            if (!string.IsNullOrWhiteSpace(key))
            {
                map.TryAdd(key, m);
            }
        }

        AddMesh(target.PrimaryMesh);
        for (var i = 0; i < target.CandidateMeshes.Count; i++)
        {
            AddMesh(target.CandidateMeshes[i]);
        }
        return map;
    }

    private List<Material?> ResolveExportMaterialsForPart(
        Mesh mesh,
        Renderer? renderer,
        HashSet<Material> visitedMaterials,
        HashSet<string> visitedTextures,
        string? rendererAssetId = null)
    {
        var materials = new List<Material?>();

        // Fast path: reuse materials referenced directly by the part's Renderer,
        // materializing lazy cross-bundle Material PPtrs when needed.
        if (renderer?.m_Materials != null && renderer.m_Materials.Length > 0)
        {
            for (var i = 0; i < renderer.m_Materials.Length; i++)
            {
                var matPtr = renderer.m_Materials[i];
                if (matPtr == null || matPtr.IsNull)
                {
                    materials.Add(null);
                    continue;
                }

                Material? mat = null;
                if (!matPtr.TryGet(out mat) && assetsManager.LazyLoading)
                {
                    var matHandle = FindMaterialHandleForPPtr(renderer, matPtr);
                    if (matHandle != null)
                    {
                        mat = ResolveSemanticRelationHandleForPreview(matHandle) as Material;
                    }
                }
                materials.Add(mat);
            }
        }

        // Fill any missing slots from the exact same SQLite v15 per-renderer resolution used by 3D Preview.
        if (assetsManager.LazyLoading && !materials.Any(static m => m != null))
        {
            var effectiveRendererId = !string.IsNullOrWhiteSpace(rendererAssetId)
                ? rendererAssetId
                : (renderer != null ? GetPreviewObjectKey(renderer) : null);
            var resolvedSlots = TryLoadResolvedMeshMaterialsForPreview(mesh, effectiveRendererId);
            if (resolvedSlots.Count > 0)
            {
                var slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, resolvedSlots.Max(static s => s.SubMeshIndex) + 1);
                var bySubMesh = new Dictionary<int, ResolvedMeshSubMeshMaterial>(resolvedSlots.Count);
                for (var s = 0; s < resolvedSlots.Count; s++)
                {
                    var slot = resolvedSlots[s];
                    if (slot.SubMeshIndex >= 0 && !bySubMesh.ContainsKey(slot.SubMeshIndex))
                    {
                        bySubMesh[slot.SubMeshIndex] = slot;
                    }
                }

                materials.Clear();
                for (var s = 0; s < slotCount; s++)
                {
                    if (bySubMesh.TryGetValue(s, out var slot))
                    {
                        var resolvedMat = !string.IsNullOrWhiteSpace(slot.MaterialAssetId)
                            ? ResolvePreviewCandidateAsset(slot.MaterialAssetId) as Material
                            : null;
                        if (!string.IsNullOrWhiteSpace(slot.MainTextureAssetId)
                            && visitedTextures.Add($"resolvedTex|{slot.MainTextureAssetId}"))
                        {
                            _ = ResolvePreviewCandidateAsset(slot.MainTextureAssetId);
                        }
                        materials.Add(resolvedMat);
                    }
                    else
                    {
                        materials.Add(null);
                    }
                }
            }
        }

        if (!materials.Any(static m => m != null))
        {
            materials = FindMaterialsForMesh(mesh);
        }

        if (assetsManager.LazyLoading && materials.Count > 0)
        {
            EnsureMaterialTexturesLoadedForExport(materials, visitedMaterials, visitedTextures);
        }

        return materials;
    }

    private bool ExportPreviewModelGroupCore(
        PreviewModelExportTarget target,
        AnimationClip[]? clipArg,
        string exportFile,
        Action<int, string>? reportStage = null,
        ExportOptionsState? optionsOverride = null,
        List<string>? exportedFilePaths = null)
    {
        var effectiveOptions = optionsOverride ?? exportOptions;
        var inMemoryMeshes = BuildInMemoryPreviewMeshLookup(target);
        ModelGroupInfo? group = null;
        var groupParts = new List<ModelGroupMeshInfo>();

        reportStage?.Invoke(12, $"Resolving model group ({target.SuggestedBaseName})...");
        if (!string.IsNullOrWhiteSpace(target.ModelGroupId))
        {
            group = LoadModelGroupByIdForPreview(target.ModelGroupId);
            groupParts = target.CandidateMeshInfos.Count > 0
                ? target.CandidateMeshInfos.ToList()
                : LoadModelGroupMeshesForPreview(target.ModelGroupId);
        }

        if ((group == null || groupParts.Count == 0) && target.Avatar != null)
        {
            TryFindModelGroupForAvatar(target.Avatar, out group, out groupParts);
        }

        if ((group == null || groupParts.Count == 0) && target.PrimaryMesh != null)
        {
            TryFindModelGroupForMesh(target.PrimaryMesh, out group, out groupParts);
        }

        if (group != null && groupParts.Count > 0)
        {
            if (ExportResolvedPreviewModelGroup(group, groupParts, target.Avatar, clipArg, exportFile, inMemoryMeshes, reportStage, effectiveOptions, exportedFilePaths))
            {
                return true;
            }
        }

        if (target.Avatar != null
            && TryBuildEagerModelGroupForAvatar(target.Avatar, out var eagerAvatarName, out var eagerAvatarRoot, out var eagerAvatarMeshes, out var eagerAvatarRenderers)
            && ExportEagerModelGroupWithMaterials(eagerAvatarName, eagerAvatarRoot, eagerAvatarMeshes, eagerAvatarRenderers, target.Avatar, clipArg, exportFile, effectiveOptions, reportStage, exportedFilePaths))
        {
            return true;
        }

        if (target.PrimaryMesh != null
            && TryBuildEagerModelGroupForMesh(target.PrimaryMesh, out var eagerName, out var eagerRoot, out var eagerMeshes, out var eagerRenderers, out var eagerAvatar)
            && ExportEagerModelGroupWithMaterials(eagerName, eagerRoot, eagerMeshes, eagerRenderers, eagerAvatar ?? target.Avatar, clipArg, exportFile, effectiveOptions, reportStage, exportedFilePaths))
        {
            return true;
        }

        // Direct candidate meshes fallback (e.g. composite parts loaded in memory without a SQLite group row).
        var candidateMeshes = target.CandidateMeshes.Count > 0
            ? target.CandidateMeshes.ToList()
            : target.CandidateMeshInfos
                .Select(info => !string.IsNullOrWhiteSpace(info.MeshAssetId)
                    ? (inMemoryMeshes.TryGetValue(info.MeshAssetId, out var memMesh) ? memMesh : ResolveMeshPreviewCandidate(info.MeshAssetId))
                    : null)
                .Where(m => m != null)
                .Cast<Mesh>()
                .ToList();

        if (candidateMeshes.Count > 0)
        {
            var resolvedRenderers = new List<Renderer>(candidateMeshes.Count);
            var partMaterials = new List<IReadOnlyList<Material>>(candidateMeshes.Count);
            var partNames = new List<string>(candidateMeshes.Count);
            var partTransforms = new List<float[]?>(candidateMeshes.Count);
            var visitedMaterials = new HashSet<Material>();
            var visitedTextures = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < candidateMeshes.Count; i++)
            {
                var pct = 20 + (int)((i + 1) * 35.0 / candidateMeshes.Count);
                reportStage?.Invoke(pct, $"Preparing part {i + 1}/{candidateMeshes.Count}...");

                var mesh = candidateMeshes[i];
                var info = i < target.CandidateMeshInfos.Count ? target.CandidateMeshInfos[i] : null;
                var renderer = !string.IsNullOrWhiteSpace(info?.RendererAssetId)
                    ? ResolvePreviewCandidateAsset(info!.RendererAssetId) as Renderer
                    : FindSingleRendererForMeshForExport(mesh);

                var materials = ResolveExportMaterialsForPart(mesh, renderer, visitedMaterials, visitedTextures, info?.RendererAssetId);
                resolvedRenderers.Add(renderer!);
                partMaterials.Add(materials.Where(m => m != null).Cast<Material>().ToList());
                partNames.Add(!string.IsNullOrWhiteSpace(info?.GameObjectName) ? info!.GameObjectName : (mesh.m_Name ?? $"Part_{i}"));
                partTransforms.Add(i < target.CandidateTransforms.Count ? target.CandidateTransforms[i] : info?.TransformMatrix);
            }

            var groupName = !string.IsNullOrWhiteSpace(target.ModelGroupName)
                ? target.ModelGroupName
                : Path.GetFileNameWithoutExtension(exportFile);

            reportStage?.Invoke(65, $"Converting {candidateMeshes.Count} part(s) & textures...");
            var convert = ModelConverter.CreateFromModelGroup(
                groupName,
                rootGameObject: null,
                candidateMeshes,
                resolvedRenderers,
                target.Avatar,
                clipArg,
                effectiveOptions.ConvertTextureFormat,
                combineMeshes: false,
                partTransforms: partTransforms!,
                partMaterials: partMaterials,
                partNames: partNames);
            if (convert.MeshList.Count > 0)
            {
                reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
                AssetExportService.ExportModel(convert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
                return true;
            }
        }

        return false;
    }

    private bool ExportResolvedPreviewModelGroup(
        ModelGroupInfo group,
        IReadOnlyList<ModelGroupMeshInfo> groupMeshes,
        Avatar? fallbackAvatar,
        AnimationClip[]? animationClips,
        string exportFile,
        Dictionary<string, Mesh>? inMemoryMeshes = null,
        Action<int, string>? reportStage = null,
        ExportOptionsState? optionsOverride = null,
        List<string>? exportedFilePaths = null)
    {
        var effectiveOptions = optionsOverride ?? exportOptions;
        reportStage?.Invoke(18, $"Resolving rig root for {group.GroupName}...");
        var rootGameObject = !string.IsNullOrWhiteSpace(group.RootGameObjectAssetId)
            ? ResolvePreviewCandidateAsset(group.RootGameObjectAssetId) as GameObject
            : null;
        var animator = !string.IsNullOrWhiteSpace(group.AnimatorAssetId)
            ? ResolvePreviewCandidateAsset(group.AnimatorAssetId) as Animator
            : rootGameObject?.m_Animator;
        if (rootGameObject == null && animator?.m_GameObject != null)
        {
            animator.m_GameObject.TryGet(out rootGameObject);
        }

        var visitedMaterials = new HashSet<Material>();
        var visitedTextures = new HashSet<string>(StringComparer.Ordinal);

        // Only run direct ModelConverter(animator) when the subtree actually contains all group parts,
        // avoiding a redundant full conversion + texture decode that would otherwise be discarded.
        if (animator != null && rootGameObject != null)
        {
            var subMeshes = new List<Mesh>();
            var subRenderers = new List<Renderer>();
            AssetExportService.CollectSubtreeMeshesAndRenderers(rootGameObject, subMeshes, subRenderers);

            if (subMeshes.Count >= Math.Max(1, groupMeshes.Count))
            {
                if (assetsManager.LazyLoading && subRenderers.Count > 0)
                {
                    for (var i = 0; i < subRenderers.Count; i++)
                    {
                        var pct = 25 + (int)((i + 1) * 30.0 / subRenderers.Count);
                        reportStage?.Invoke(pct, $"Preparing textures for part {i + 1}/{subRenderers.Count}...");
                        EnsureRendererMaterialsLoadedForExport(subRenderers[i], visitedMaterials, visitedTextures);
                    }
                }

                reportStage?.Invoke(65, $"Converting rig & textures ({subMeshes.Count} parts)...");
                var directConvert = new ModelConverter(animator, effectiveOptions.ConvertTextureFormat, animationClips);
                if (directConvert.MeshList.Count >= Math.Max(1, groupMeshes.Count))
                {
                    reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
                    AssetExportService.ExportModel(directConvert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
                    return true;
                }
            }
        }

        // Composite ModelConverter.CreateFromModelGroup: reuses in-memory preview meshes directly.
        var avatar = !string.IsNullOrWhiteSpace(group.AvatarAssetId)
            ? ResolveAvatarPreviewCandidate(group.AvatarAssetId)
            : null;
        if (avatar == null && animator?.m_Avatar != null)
        {
            animator.m_Avatar.TryGet(out avatar);
        }
        avatar ??= fallbackAvatar;

        var resolvedMeshes = new List<Mesh>(groupMeshes.Count);
        var resolvedRenderers = new List<Renderer>(groupMeshes.Count);
        var partTransforms = new List<float[]?>(groupMeshes.Count);
        var partMaterials = new List<IReadOnlyList<Material>>(groupMeshes.Count);
        var partNames = new List<string>(groupMeshes.Count);

        for (var i = 0; i < groupMeshes.Count; i++)
        {
            var pct = 20 + (int)((i + 1) * 35.0 / groupMeshes.Count);
            reportStage?.Invoke(pct, $"Preparing part {i + 1}/{groupMeshes.Count}...");

            var part = groupMeshes[i];
            if (string.IsNullOrWhiteSpace(part.MeshAssetId))
            {
                continue;
            }

            Mesh? mesh = null;
            if (inMemoryMeshes != null)
            {
                inMemoryMeshes.TryGetValue(part.MeshAssetId, out mesh);
            }
            mesh ??= ResolveMeshPreviewCandidate(part.MeshAssetId);
            if (mesh == null)
            {
                continue;
            }

            var renderer = !string.IsNullOrWhiteSpace(part.RendererAssetId)
                ? ResolvePreviewCandidateAsset(part.RendererAssetId) as Renderer
                : null;

            var materials = ResolveExportMaterialsForPart(mesh, renderer, visitedMaterials, visitedTextures, part.RendererAssetId);

            resolvedMeshes.Add(mesh);
            resolvedRenderers.Add(renderer!);
            partTransforms.Add(part.TransformMatrix);
            partMaterials.Add(materials.Where(m => m != null).Cast<Material>().ToList());
            partNames.Add(!string.IsNullOrWhiteSpace(part.GameObjectName) ? part.GameObjectName : mesh.m_Name ?? $"Part_{i}");
        }

        if (resolvedMeshes.Count == 0 && rootGameObject == null)
        {
            return false;
        }

        var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
            ? group.GroupName
            : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                ? group.RootGameObjectName
                : Path.GetFileNameWithoutExtension(exportFile);

        reportStage?.Invoke(65, $"Converting {resolvedMeshes.Count} part(s) & textures...");
        var convert = ModelConverter.CreateFromModelGroup(
            groupName,
            rootGameObject,
            resolvedMeshes,
            resolvedRenderers,
            avatar,
            animationClips,
            effectiveOptions.ConvertTextureFormat,
            combineMeshes: false,
            partTransforms: partTransforms!,
            partMaterials: partMaterials,
            partNames: partNames);
        if (convert.MeshList.Count == 0)
        {
            return false;
        }

        reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
        AssetExportService.ExportModel(convert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
        return true;
    }

    private bool ExportPreviewSingleMeshCore(
        Mesh mesh,
        Avatar? avatar,
        AnimationClip[]? clipArg,
        string exportFile,
        out string actualExportFile,
        Action<int, string>? reportStage = null,
        ExportOptionsState? optionsOverride = null,
        List<string>? exportedFilePaths = null,
        Renderer? explicitRenderer = null)
    {
        var effectiveOptions = optionsOverride ?? exportOptions;
        actualExportFile = exportFile;
        reportStage?.Invoke(20, $"Preparing mesh {mesh.m_Name}...");
        mesh.EnsureProcessed();

        var visitedMaterials = new HashSet<Material>();
        var visitedTextures = new HashSet<string>(StringComparer.Ordinal);

        // Prefer explicit or O(1) SQLite semantic renderer lookup before falling back to linear file scan.
        Renderer? renderer = explicitRenderer ?? FindSkinnedRendererForAvatarMesh(avatar, mesh);
        if (renderer == null && !assetsManager.LazyLoading)
        {
            renderer = FindSingleRendererForMeshForExport(mesh);
        }

        reportStage?.Invoke(40, $"Loading materials & textures for {mesh.m_Name}...");
        var materials = ResolveExportMaterialsForPart(mesh, renderer, visitedMaterials, visitedTextures);
        var nonNullMaterials = materials.Where(m => m != null).Cast<Material>().ToList();
        var meshName = !string.IsNullOrWhiteSpace(mesh.m_Name)
            ? mesh.m_Name
            : Path.GetFileNameWithoutExtension(exportFile);

        if (!string.Equals(Path.GetExtension(exportFile), ".obj", StringComparison.OrdinalIgnoreCase))
        {
            reportStage?.Invoke(65, $"Converting {meshName} & textures...");
            var convert = ModelConverter.CreateFromModelGroup(
                meshName,
                rootGameObject: null,
                new[] { mesh },
                renderer != null ? new[] { renderer } : null,
                avatar,
                clipArg,
                effectiveOptions.ConvertTextureFormat,
                combineMeshes: false,
                partTransforms: new float[]?[] { null }!,
                partMaterials: new IReadOnlyList<Material>[] { nonNullMaterials },
                partNames: new[] { meshName });
            if (convert.MeshList.Count > 0)
            {
                reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
                AssetExportService.ExportModel(convert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
                return true;
            }
        }

        var objPath = string.Equals(Path.GetExtension(exportFile), ".obj", StringComparison.OrdinalIgnoreCase)
            ? exportFile
            : Path.ChangeExtension(exportFile, ".obj");
        reportStage?.Invoke(85, $"Writing {Path.GetFileName(objPath)}...");
        if (AssetExportService.ExportMeshToObj(mesh, objPath))
        {
            actualExportFile = objPath;
            exportedFilePaths?.Add(objPath);
            return true;
        }

        return false;
    }

    private bool ExportPreviewAvatarRigCore(
        Avatar avatar,
        AnimationClip[]? clipArg,
        string exportFile,
        Action<int, string>? reportStage = null,
        ExportOptionsState? optionsOverride = null,
        List<string>? exportedFilePaths = null)
    {
        var effectiveOptions = optionsOverride ?? exportOptions;
        reportStage?.Invoke(15, $"Resolving Avatar rig {avatar.m_Name}...");
        if (TryFindModelGroupForAvatar(avatar, out var avatarGroup, out var avatarGroupParts)
            && avatarGroup != null
            && avatarGroupParts.Count > 0)
        {
            return ExportResolvedPreviewModelGroup(avatarGroup, avatarGroupParts, avatar, clipArg, exportFile, reportStage: reportStage, optionsOverride: effectiveOptions, exportedFilePaths: exportedFilePaths);
        }

        if (TryBuildEagerModelGroupForAvatar(avatar, out var eagerName, out var eagerRoot, out var eagerMeshes, out var eagerRenderers))
        {
            reportStage?.Invoke(65, $"Converting {eagerMeshes.Count} part(s) for {avatar.m_Name}...");
            return ExportEagerModelGroupWithMaterials(eagerName, eagerRoot, eagerMeshes, eagerRenderers, avatar, clipArg, exportFile, effectiveOptions, reportStage, exportedFilePaths);
        }

        var loadedFiles = GetLoadedFilesSnapshot();
        for (var f = 0; f < loadedFiles.Count; f++)
        {
            var file = loadedFiles[f];
            if (file == null) continue;
            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] is Animator animator
                    && animator.m_Avatar != null
                    && animator.m_Avatar.TryGet(out var animAvatar)
                    && AreSamePreviewObject(animAvatar, avatar))
                {
                    reportStage?.Invoke(65, $"Converting Animator for {avatar.m_Name}...");
                    var convert = new ModelConverter(animator, effectiveOptions.ConvertTextureFormat, clipArg);
                    if (convert.MeshList.Count > 0)
                    {
                        reportStage?.Invoke(85, $"Writing {Path.GetFileName(exportFile)}...");
                        AssetExportService.ExportModel(convert, exportFile, effectiveOptions, exportedFilePaths: exportedFilePaths);
                        return true;
                    }
                }
            }
        }

        var avatarMeshes = FindMeshesForAvatar(avatar);
        if (avatarMeshes.Count > 0)
        {
            var renderers = avatarMeshes
                .Select(m => (Renderer?)(FindSkinnedRendererForAvatarMesh(avatar, m) ?? FindSingleRendererForMeshForExport(m)))
                .ToList();
            return ExportEagerModelGroupWithMaterials(
                avatar.m_Name ?? Path.GetFileNameWithoutExtension(exportFile),
                eagerRoot: null,
                avatarMeshes,
                renderers!,
                avatar,
                clipArg,
                exportFile,
                effectiveOptions,
                reportStage,
                exportedFilePaths);
        }

        return false;
    }

    private Renderer? FindSingleRendererForMeshForExport(Mesh mesh)
    {
        var loadedFiles = GetLoadedFilesSnapshot();
        for (var f = 0; f < loadedFiles.Count; f++)
        {
            var file = loadedFiles[f];
            if (file == null) continue;
            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] is SkinnedMeshRenderer smr
                    && smr.m_Mesh != null
                    && smr.m_Mesh.TryGet(out var smrMesh)
                    && AreSamePreviewObject(smrMesh, mesh))
                {
                    return smr;
                }

                if (objs[i] is MeshFilter mf
                    && mf.m_Mesh != null
                    && mf.m_Mesh.TryGet(out var mfMesh)
                    && AreSamePreviewObject(mfMesh, mesh)
                    && mf.m_GameObject != null
                    && mf.m_GameObject.TryGet(out var mfGo)
                    && mfGo?.m_MeshRenderer != null)
                {
                    return mfGo.m_MeshRenderer;
                }
            }
        }

        return null;
    }

    internal async Task ExportSelectedModelGroupFbxAsync()
    {
        var selectedAssets = GetSelectedAssets();
        var primaryAsset = selectedAssets.FirstOrDefault(x => x.Type is ClassIDType.Mesh or ClassIDType.Animator or ClassIDType.Avatar);
        if (primaryAsset == null)
        {
            StatusStripUpdate("Select a Mesh, Animator, or Avatar to export its Model Group as FBX.");
            return;
        }

        var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
        var suggestedName = FixFileName(AssetExportService.GetExportFileName(primaryAsset)) + modelExt;
        var exportFile = await PickSaveModelDialogAsync("Export Model Group", suggestedName);
        if (string.IsNullOrWhiteSpace(exportFile)) return;
        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var clips = selectedAssets
            .Where(x => x.Type == ClassIDType.AnimationClip)
            .ToList();
        ExportState.BeginExport(
            ExportOperationMode.Animator,
            1,
            exportFolder,
            $"Exporting Model Group FBX: {Path.GetFileName(exportFile)}...");
        StatusStripUpdate($"Exporting Model Group FBX: {Path.GetFileName(exportFile)}...");

        bool success = false;
        string? errorMessage = null;
        await Task.Run(() =>
        {
            try
            {
                EnsureLazyAssetsLoadedForExport(selectedAssets);
                var resolvedClips = clips
                    .Select(x => x.Asset as AnimationClip)
                    .Where(x => x != null)
                    .Cast<AnimationClip>()
                    .ToArray();
                var clipArg = resolvedClips.Length > 0 ? resolvedClips : null;

                switch (primaryAsset.Asset)
                {
                    case Mesh mesh:
                        if (TryFindModelGroupForMesh(mesh, out var meshGroup, out var meshGroupParts)
                            && meshGroup != null
                            && meshGroupParts.Count > 0)
                        {
                            success = ExportModelGroupFbx(meshGroup, meshGroupParts, exportFile, clipArg);
                        }
                        else if (TryBuildEagerModelGroupForMesh(mesh, out var eagerName, out var eagerRoot, out var eagerMeshes, out var eagerRenderers, out var eagerAvatar))
                        {
                            success = ExportModelGroupFbx(eagerName, eagerRoot, eagerMeshes, eagerRenderers, eagerAvatar, clipArg, exportFile);
                        }
                        else if (primaryAsset.TreeNode?.GameObject != null)
                        {
                            var animator = FindAnimatorForModelExport(primaryAsset);
                            var convert = animator != null
                                ? new ModelConverter(animator, exportOptions.ConvertTextureFormat, clipArg)
                                : new ModelConverter(primaryAsset.TreeNode.GameObject, exportOptions.ConvertTextureFormat, clipArg);
                            if (convert.MeshList.Count > 0)
                            {
                                ExportFbx(convert, exportFile);
                                success = true;
                            }
                        }
                        break;
                    case Avatar avatar:
                        if (TryFindModelGroupForAvatar(avatar, out var avatarGroup, out var avatarGroupParts)
                            && avatarGroup != null
                            && avatarGroupParts.Count > 0)
                        {
                            success = ExportModelGroupFbx(avatarGroup, avatarGroupParts, exportFile, clipArg);
                        }
                        else if (TryBuildEagerModelGroupForAvatar(avatar, out var eagerAvatarName, out var eagerAvatarRoot, out var eagerAvatarMeshes, out var eagerAvatarRenderers))
                        {
                            success = ExportModelGroupFbx(eagerAvatarName, eagerAvatarRoot, eagerAvatarMeshes, eagerAvatarRenderers, avatar, clipArg, exportFile);
                        }
                        break;
                    case Animator animator:
                        if (TryFindModelGroupForAnimator(animator, out var animGroup, out var animGroupParts)
                            && animGroup != null
                            && animGroupParts.Count > 0)
                        {
                            success = ExportModelGroupFbx(animGroup, animGroupParts, exportFile, clipArg);
                        }
                        else
                        {
                            var convert = new ModelConverter(animator, exportOptions.ConvertTextureFormat, clipArg);
                            if (convert.MeshList.Count > 0)
                            {
                                ExportFbx(convert, exportFile);
                                success = true;
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.Error($"Export Model Group FBX failed for {primaryAsset.Name}", ex);
            }
        });

        if (success)
        {
            var status = $"Finished exporting Model Group FBX: {Path.GetFileName(exportFile)}";
            ExportState.Complete(1, statusText: status);
            StatusStripUpdate(status);
            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            var status = errorMessage != null
                ? $"Error exporting Model Group FBX: {errorMessage}"
                : $"No Model Group found for {primaryAsset.Name}.";
            if (errorMessage != null)
            {
                ExportState.Fail(status);
            }
            else
            {
                ExportState.Complete(0, statusText: status);
            }
            StatusStripUpdate(status);
        }
    }

    internal bool ExportModelGroupFbx(
        ModelGroupInfo group,
        IReadOnlyList<ModelGroupMeshInfo> groupMeshes,
        string exportFile,
        AnimationClip[]? animationClips = null)
    {
        if (group == null || groupMeshes == null || groupMeshes.Count == 0 || string.IsNullOrWhiteSpace(exportFile))
        {
            return false;
        }

        var rootGameObject = !string.IsNullOrWhiteSpace(group.RootGameObjectAssetId)
            ? ResolvePreviewCandidateAsset(group.RootGameObjectAssetId) as GameObject
            : null;
        var animator = !string.IsNullOrWhiteSpace(group.AnimatorAssetId)
            ? ResolvePreviewCandidateAsset(group.AnimatorAssetId) as Animator
            : null;
        if (rootGameObject == null && animator?.m_GameObject != null)
        {
            animator.m_GameObject.TryGet(out rootGameObject);
        }

        var avatar = !string.IsNullOrWhiteSpace(group.AvatarAssetId)
            ? ResolveAvatarPreviewCandidate(group.AvatarAssetId)
            : null;
        if (avatar == null && animator?.m_Avatar != null)
        {
            animator.m_Avatar.TryGet(out avatar);
        }

        var resolvedMeshes = new List<Mesh>(groupMeshes.Count);
        var resolvedRenderers = new List<Renderer>(groupMeshes.Count);
        var partTransforms = new List<float[]?>(groupMeshes.Count);
        var partMaterials = new List<IReadOnlyList<Material>>(groupMeshes.Count);
        var partNames = new List<string>(groupMeshes.Count);

        for (int i = 0; i < groupMeshes.Count; i++)
        {
            var part = groupMeshes[i];
            if (string.IsNullOrWhiteSpace(part.MeshAssetId))
            {
                continue;
            }

            var mesh = ResolveMeshPreviewCandidate(part.MeshAssetId);
            if (mesh == null)
            {
                continue;
            }

            var renderer = !string.IsNullOrWhiteSpace(part.RendererAssetId)
                ? ResolvePreviewCandidateAsset(part.RendererAssetId) as Renderer
                : null;

            var materials = FindMaterialsForMesh(mesh);
            if (assetsManager.LazyLoading)
            {
                EnsureMaterialTexturesLoadedForExport(materials);
                if (renderer != null)
                {
                    EnsureRendererMaterialsLoadedForExport(renderer);
                }
            }

            var nonNullMaterials = new List<Material>(materials.Count);
            for (int m = 0; m < materials.Count; m++)
            {
                nonNullMaterials.Add(materials[m]!);
            }

            resolvedMeshes.Add(mesh);
            resolvedRenderers.Add(renderer!);
            partTransforms.Add(part.TransformMatrix);
            partMaterials.Add(nonNullMaterials);
            partNames.Add(!string.IsNullOrWhiteSpace(part.GameObjectName) ? part.GameObjectName : mesh.m_Name ?? $"Part_{i}");
        }

        if (resolvedMeshes.Count == 0 && rootGameObject == null)
        {
            return false;
        }

        var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
            ? group.GroupName
            : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                ? group.RootGameObjectName
                : Path.GetFileNameWithoutExtension(exportFile);

        var convert = ModelConverter.CreateFromModelGroup(
            groupName,
            rootGameObject,
            resolvedMeshes,
            resolvedRenderers,
            avatar,
            animationClips,
            exportOptions.ConvertTextureFormat,
            combineMeshes: false,
            partTransforms: partTransforms!,
            partMaterials: partMaterials,
            partNames: partNames);

        if (convert.MeshList.Count == 0)
        {
            return false;
        }

        ExportFbx(convert, exportFile);
        return true;
    }

    internal bool ExportModelGroupFbx(
        string groupName,
        GameObject? rootGameObject,
        IReadOnlyList<Mesh> meshes,
        IReadOnlyList<Renderer>? renderers,
        Avatar? avatar,
        AnimationClip[]? animationClips,
        string exportFile,
        IReadOnlyList<float[]?>? partTransforms = null,
        IReadOnlyList<IReadOnlyList<Material>>? partMaterials = null,
        IReadOnlyList<string>? partNames = null)
    {
        if (string.IsNullOrWhiteSpace(exportFile) || ((meshes == null || meshes.Count == 0) && rootGameObject == null))
        {
            return false;
        }

        if (assetsManager.LazyLoading && partMaterials != null)
        {
            for (int i = 0; i < partMaterials.Count; i++)
            {
                EnsureMaterialTexturesLoadedForExport(partMaterials[i]);
            }
        }

        var convert = ModelConverter.CreateFromModelGroup(
            groupName,
            rootGameObject,
            meshes ?? Array.Empty<Mesh>(),
            renderers,
            avatar,
            animationClips,
            exportOptions.ConvertTextureFormat,
            combineMeshes: false,
            partTransforms: partTransforms!,
            partMaterials: partMaterials,
            partNames: partNames);

        if (convert.MeshList.Count == 0)
        {
            return false;
        }

        ExportFbx(convert, exportFile);
        return true;
    }

    private bool TryExportMeshModelGroupFbx(
        AssetItem item,
        Mesh mesh,
        string exportPath,
        string fileName,
        Services.ExportConversionContext conversionContext,
        out bool alreadyExportedGroup)
    {
        alreadyExportedGroup = false;

        if (TryFindModelGroupForMesh(mesh, out var group, out var groupMeshes)
            && group != null
            && groupMeshes.Count > 0)
        {
            if (!conversionContext.TryMarkModelGroupExported(group.GroupId))
            {
                alreadyExportedGroup = true;
                return false;
            }

            var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
            var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(group.GroupName) ? group.GroupName : fileName);
            if (conversionContext.TryGetAssetFilePath(exportPath, groupFileName, modelExt, item, out var fbxPath)
                && ExportModelGroupFbx(group, groupMeshes, fbxPath))
            {
                return true;
            }

            conversionContext.UnmarkModelGroupExported(group.GroupId);
        }

        if (!assetsManager.LazyLoading
            && TryBuildEagerModelGroupForMesh(mesh, conversionContext, out var eagerName, out var eagerRoot, out var eagerMeshes, out var eagerRenderers, out var eagerAvatar))
        {
            var eagerGroupKey = eagerRoot != null
                ? $"eager:{GetPreviewObjectKey(eagerRoot)}"
                : null;
            if (!string.IsNullOrEmpty(eagerGroupKey) && !conversionContext.TryMarkModelGroupExported(eagerGroupKey))
            {
                alreadyExportedGroup = true;
                return false;
            }

            var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
            var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(eagerName) ? eagerName : fileName);
            if (conversionContext.TryGetAssetFilePath(exportPath, groupFileName, modelExt, item, out var fbxPath)
                && ExportModelGroupFbx(eagerName, eagerRoot, eagerMeshes, eagerRenderers, eagerAvatar, null, fbxPath))
            {
                return true;
            }

            conversionContext.UnmarkModelGroupExported(eagerGroupKey);
        }

        return false;
    }

    private bool TryFindModelGroupForMesh(Mesh mesh, out ModelGroupInfo? group, out List<ModelGroupMeshInfo> groupMeshes)
    {
        group = null;
        groupMeshes = new List<ModelGroupMeshInfo>();
        var meshId = GetPreviewObjectKey(mesh);
        if (string.IsNullOrWhiteSpace(meshId))
        {
            return false;
        }

        var candidates = LoadModelGroupCandidatesForMeshAssetIdForPreview(meshId);
        if (candidates.Count == 0)
        {
            return false;
        }

        ModelGroupCandidateInfo? best = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.Meshes.Count == 0)
            {
                continue;
            }

            if (best == null)
            {
                best = candidate;
                continue;
            }

            bool bestIsMulti = best.Meshes.Count > 1;
            bool candIsMulti = candidate.Meshes.Count > 1;
            if (candIsMulti && !bestIsMulti)
            {
                best = candidate;
            }
            else if (candIsMulti == bestIsMulti && candidate.Group.Confidence > best.Group.Confidence)
            {
                best = candidate;
            }
        }

        if (best == null)
        {
            return false;
        }

        group = best.Group;
        groupMeshes = best.Meshes;
        return true;
    }

    private bool TryFindModelGroupForAvatar(Avatar avatar, out ModelGroupInfo? group, out List<ModelGroupMeshInfo> groupMeshes)
    {
        group = null;
        groupMeshes = new List<ModelGroupMeshInfo>();
        var avatarId = GetPreviewObjectKey(avatar);
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return false;
        }

        var groupsByAvatar = LoadModelGroupsByAvatarIdsForPreview(new[] { avatarId });
        if (!groupsByAvatar.TryGetValue(avatarId, out var groups) || groups.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < groups.Count; i++)
        {
            var candidate = groups[i];
            var meshes = LoadModelGroupMeshesForPreview(candidate.GroupId);
            if (meshes.Count > 0)
            {
                group = candidate;
                groupMeshes = meshes;
                return true;
            }
        }

        return false;
    }

    private bool TryFindModelGroupForAnimator(Animator animator, out ModelGroupInfo? group, out List<ModelGroupMeshInfo> groupMeshes)
    {
        group = null;
        groupMeshes = new List<ModelGroupMeshInfo>();

        if (animator.m_Avatar != null && animator.m_Avatar.TryGet(out var avatar) && avatar != null)
        {
            if (TryFindModelGroupForAvatar(avatar, out group, out groupMeshes))
            {
                return true;
            }
        }

        if (animator.m_GameObject != null && animator.m_GameObject.TryGet(out var go) && go != null)
        {
            var firstMesh = FindFirstMeshUnderGameObject(go);
            if (firstMesh != null && TryFindModelGroupForMesh(firstMesh, out group, out groupMeshes))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBuildEagerModelGroupForMesh(
        Mesh mesh,
        out string groupName,
        out GameObject? rootGameObject,
        out List<Mesh> meshes,
        out List<Renderer> renderers,
        out Avatar? avatar)
    {
        return TryBuildEagerModelGroupForMesh(
            mesh,
            conversionContext: null,
            out groupName,
            out rootGameObject,
            out meshes,
            out renderers,
            out avatar);
    }

    private bool TryBuildEagerModelGroupForMesh(
        Mesh mesh,
        Services.ExportConversionContext? conversionContext,
        out string groupName,
        out GameObject? rootGameObject,
        out List<Mesh> meshes,
        out List<Renderer> renderers,
        out Avatar? avatar)
    {
        groupName = mesh.m_Name ?? "Model";
        rootGameObject = null;
        meshes = new List<Mesh>();
        renderers = new List<Renderer>();
        avatar = null;

        Dictionary<Mesh, EagerModelGroupEntry> lookup;
        if (conversionContext != null)
        {
            lookup = conversionContext.EagerModelGroupsByMesh;
        }
        else
        {
            List<SerializedFile> filesSnapshot;
            lock (assetsManager.loadLock)
            {
                filesSnapshot = assetsManager.assetsFileList.ToList();
            }
            lookup = BuildEagerModelGroupLookup(filesSnapshot);
        }

        if (!lookup.TryGetValue(mesh, out var entry) || entry.Meshes.Count == 0)
        {
            return false;
        }

        groupName = entry.GroupName;
        rootGameObject = entry.RootGameObject;
        meshes = entry.Meshes;
        renderers = entry.Renderers;
        avatar = entry.Avatar;
        return true;
    }

    internal static Dictionary<Mesh, EagerModelGroupEntry> BuildEagerModelGroupLookup(
        IReadOnlyList<SerializedFile> filesSnapshot)
        => AssetExportService.BuildEagerModelGroupLookup(filesSnapshot);

    private bool TryBuildEagerModelGroupForAvatar(
        Avatar avatar,
        out string groupName,
        out GameObject? rootGameObject,
        out List<Mesh> meshes,
        out List<Renderer> renderers)
    {
        groupName = avatar.m_Name ?? "Avatar";
        rootGameObject = null;
        meshes = new List<Mesh>();
        renderers = new List<Renderer>();

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        for (int f = 0; f < filesSnapshot.Count && rootGameObject == null; f++)
        {
            var objects = filesSnapshot[f].Objects;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is Animator anim
                    && anim.m_Avatar != null
                    && anim.m_Avatar.TryGet(out var animAvatar)
                    && ReferenceEquals(animAvatar, avatar)
                    && anim.m_GameObject != null
                    && anim.m_GameObject.TryGet(out var animGo)
                    && animGo != null)
                {
                    rootGameObject = animGo;
                    break;
                }
            }
        }

        if (rootGameObject == null)
        {
            return false;
        }

        groupName = !string.IsNullOrWhiteSpace(rootGameObject.m_Name) ? rootGameObject.m_Name : groupName;
        AssetExportService.CollectSubtreeMeshesAndRenderers(rootGameObject, meshes, renderers);
        return meshes.Count > 0;
    }

    private static Mesh? FindFirstMeshUnderGameObject(GameObject root)
    {
        var meshes = new List<Mesh>();
        var renderers = new List<Renderer>();
        AssetExportService.CollectSubtreeMeshesAndRenderers(root, meshes, renderers);
        return meshes.Count > 0 ? meshes[0] : null;
    }

    private void EnsureRendererMaterialsLoadedForExport(
        Renderer renderer,
        HashSet<Material>? visitedMaterials = null,
        HashSet<string>? visitedTextures = null)
    {
        if (renderer.m_Materials == null)
        {
            return;
        }

        var loadedMaterials = new List<Material?>(renderer.m_Materials.Length);
        for (int i = 0; i < renderer.m_Materials.Length; i++)
        {
            var matPtr = renderer.m_Materials[i];
            if (matPtr == null || matPtr.IsNull) continue;
            Material? mat = null;
            if (!matPtr.TryGet(out mat) && assetsManager.LazyLoading)
            {
                var matHandle = FindMaterialHandleForPPtr(renderer, matPtr);
                if (matHandle != null)
                {
                    mat = ResolveSemanticRelationHandleForPreview(matHandle) as Material;
                }
            }
            if (mat != null)
            {
                loadedMaterials.Add(mat);
            }
        }
        EnsureMaterialTexturesLoadedForExport(loadedMaterials, visitedMaterials, visitedTextures);
    }

    private void EnsureMaterialTexturesLoadedForExport(
        IReadOnlyList<Material?> materials,
        HashSet<Material>? visitedMaterials = null,
        HashSet<string>? visitedTextures = null)
    {
        if (materials == null || materials.Count == 0)
        {
            return;
        }

        for (int i = 0; i < materials.Count; i++)
        {
            var rootMat = materials[i];
            if (rootMat == null)
            {
                continue;
            }

            // Walk m_Parent Material Variant chain so parent materials and their textures are materialized too.
            Material? currentMat = rootMat;
            int depth = 0;
            while (currentMat != null && depth++ < 8)
            {
                if (visitedMaterials != null && !visitedMaterials.Add(currentMat))
                {
                    break;
                }

                var texEnvs = currentMat.m_SavedProperties?.m_TexEnvs;
                bool resolvedAnyTexture = false;
                if (texEnvs != null)
                {
                    var matFile = currentMat.assetsFile?.fileName ?? string.Empty;
                    foreach (var texEnv in texEnvs)
                    {
                        var texRef = texEnv.Value?.m_Texture;
                        if (texRef != null && !texRef.IsNull)
                        {
                            if (visitedTextures != null && !visitedTextures.Add($"{matFile}|{texRef.m_FileID}|{texRef.m_PathID}"))
                            {
                                resolvedAnyTexture = true;
                                continue;
                            }

                            if (ResolveTexturePPtrForPreview(currentMat, texRef) != null)
                            {
                                resolvedAnyTexture = true;
                            }
                        }
                    }
                }

                // Fallback: materialize textures indexed in SQLite MaterialCache / MaterialTextureRelations
                // when cross-bundle m_Externals filenames didn't resolve directly via PPtr.
                if (assetsManager.LazyLoading && !resolvedAnyTexture)
                {
                    _ = TryLoadTextureForMaterialFromSemanticCache(currentMat);
                }

                if (currentMat.m_Parent != null && !currentMat.m_Parent.IsNull)
                {
                    if (!currentMat.m_Parent.TryGet(out var parentMat) && assetsManager.LazyLoading)
                    {
                        var parentHandle = FindMaterialHandleForPPtr(currentMat, currentMat.m_Parent);
                        parentMat = parentHandle != null ? ResolveSemanticRelationHandleForPreview(parentHandle) as Material : null;
                    }
                    currentMat = parentMat;
                }
                else
                {
                    break;
                }
            }
        }
    }

    private void ExportFbx(
        IImported convert,
        string exportFile,
        ConcurrentDictionary<string, byte>? reservedPaths = null)
        => AssetExportService.ExportModel(convert, exportFile, exportOptions, reservedPaths);

    internal sealed class ExportConversionContext : Services.ExportConversionContext
    {
        public ExportConversionContext(List<SerializedFile> assetsFiles, object? filesLock = null)
            : base(assetsFiles, filesLock)
        {
        }
    }

    internal static string FixFileName(string? name)
        => AssetExportService.FixFileName(name);
}
