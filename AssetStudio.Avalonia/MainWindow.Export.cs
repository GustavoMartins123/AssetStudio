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
        var animator = selectedAssets.FirstOrDefault(x => x.Type == ClassIDType.Animator);
        var animationList = selectedAssets.Where(x => x.Type == ClassIDType.AnimationClip).ToList();
        if (animator == null || animationList.Count == 0)
        {
            StatusStripUpdate("Select one Animator and one or more AnimationClips.");
            return;
        }

        var selectedExportRoot = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(selectedExportRoot)) return;

        SaveExportFolder(selectedExportRoot);
        var exportPath = Path.Combine(selectedExportRoot, "Animator");
        Directory.CreateDirectory(exportPath);
        var modelExt = AssetExportService.GetModelFileExtension(exportOptions);
        var exportFile = AssetExportService.GetUniqueFilePath(Path.Combine(exportPath, FixFileName(animator.Name) + modelExt));
        var selectedGameObjects = GetTopLevelSelectedGameObjects(selectedAssets
            .Where(x => x.Type != ClassIDType.AnimationClip && x.TreeNode?.GameObject != null)
            .Select(x => x.TreeNode!.GameObject!)
            .Distinct()
            .ToList());

        ExportState.BeginExport(
            ExportOperationMode.Animator,
            1,
            exportPath,
            $"Exporting {animator.Name}...");
        StatusStripUpdate($"Exporting {animator.Name}...");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportPath, "export-current.txt");
        bool success = false;
        await Task.Run(() =>
        {
            try
            {
                EnsureLazyAssetsLoadedForExport(selectedAssets);
                var clips = animationList.Select(x => (AnimationClip)x.Asset!).ToArray();
                AssetExportService.WriteCurrentExport(currentExportPath, animator, 1, 1);
                IImported convert = selectedGameObjects.Count > 0
                    ? new ModelConverter(animator.Name, selectedGameObjects, exportOptions.ConvertTextureFormat, clips)
                    : new ModelConverter((Animator)animator.Asset!, exportOptions.ConvertTextureFormat, clips);
                ExportFbx(convert, exportFile);
                success = true;
            }
            catch (Exception ex)
            {
                var error = $"Export Animator:{animator.Name} error";
                exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                Logger.Error(error, ex);
                StatusStripUpdate($"Error exporting {animator.Name}: {ex.Message}");
            }
            finally
            {
                AssetExportService.ClearCurrentExport(exportPath);
            }
        });

        var errorReportPath = WriteErrorReport(exportPath, exportErrors, logger);

        if (success)
        {
            var status = $"Finished exporting {Path.GetFileName(exportFile)}";
            ExportState.Complete(1, statusText: status, errorLogPath: errorReportPath);
            StatusStripUpdate(status);
            if (exportOptions.OpenAfterExport)
            {
                OpenFolder(exportPath);
            }
        }
        else
        {
            var status = "Animator export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
            ExportState.Fail(status, errorReportPath);
            StatusStripUpdate(status);
        }
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

    private void EnsureRendererMaterialsLoadedForExport(Renderer renderer)
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
            if (matPtr.TryGet(out var mat) && mat != null)
            {
                loadedMaterials.Add(mat);
            }
        }
        EnsureMaterialTexturesLoadedForExport(loadedMaterials);
    }

    private void EnsureMaterialTexturesLoadedForExport(IReadOnlyList<Material?> materials)
    {
        if (materials == null || materials.Count == 0)
        {
            return;
        }

        for (int i = 0; i < materials.Count; i++)
        {
            var mat = materials[i];
            var texEnvs = mat?.m_SavedProperties?.m_TexEnvs;
            if (mat == null || texEnvs == null)
            {
                continue;
            }

            foreach (var texEnv in texEnvs)
            {
                var texRef = texEnv.Value?.m_Texture;
                if (texRef != null && !texRef.IsNull)
                {
                    _ = ResolveTexturePPtrForPreview(mat, texRef);
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
