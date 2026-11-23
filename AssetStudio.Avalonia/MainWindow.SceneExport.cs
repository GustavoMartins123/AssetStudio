using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private readonly SceneGlbExportService _sceneGlbExportService = new();

    private async Task ExportSceneGlbCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        // Path 1: Scene Hierarchy already loaded (Eager mode or loaded scene files)
        if (sceneTreeNodes.Count > 0)
        {
            GameObjectNode? targetSceneRoot = null;
            if (SceneTreeView?.SelectedItem is GameObjectNode selectedNode)
            {
                targetSceneRoot = SceneGlbExportService.FindSceneRootNode(selectedNode);
            }
            else
            {
                var checkedNodes = GetSelectedParentNodes();
                if (checkedNodes.Count > 0)
                {
                    targetSceneRoot = SceneGlbExportService.FindSceneRootNode(checkedNodes[0]);
                }
            }

            List<GameObject> rootGameObjects;
            string sceneName;

            if (targetSceneRoot != null)
            {
                sceneName = string.IsNullOrWhiteSpace(targetSceneRoot.Name) ? "Scene" : targetSceneRoot.Name;
                rootGameObjects = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(targetSceneRoot);
            }
            else if (sceneTreeNodes.Count == 1)
            {
                sceneName = string.IsNullOrWhiteSpace(sceneTreeNodes[0].Name) ? "Scene" : sceneTreeNodes[0].Name;
                rootGameObjects = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(sceneTreeNodes[0]);
            }
            else
            {
                var exportAllScenes = await ViewModel.DialogService.ConfirmAsync(
                    "Export Scene to .glb",
                    $"Multiple scenes are loaded ({sceneTreeNodes.Count} scene roots).\n\n" +
                    $"Click Yes to export ALL loaded scenes into one instanced .glb.\n" +
                    $"Click No to export only the first scene ({sceneTreeNodes[0].Name}).\n\n" +
                    $"Tip: Select or right-click a specific scene in Scene Hierarchy to export just that scene.");

                if (exportAllScenes)
                {
                    sceneName = "AllScenes";
                    rootGameObjects = new List<GameObject>();
                    var seen = new HashSet<GameObject>();
                    foreach (var sceneRoot in sceneTreeNodes)
                    {
                        foreach (var go in SceneGlbExportService.CollectRootGameObjectsFromSceneNode(sceneRoot))
                        {
                            if (seen.Add(go))
                            {
                                rootGameObjects.Add(go);
                            }
                        }
                    }
                }
                else
                {
                    sceneName = string.IsNullOrWhiteSpace(sceneTreeNodes[0].Name) ? "Scene" : sceneTreeNodes[0].Name;
                    rootGameObjects = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(sceneTreeNodes[0]);
                }
            }

            if (rootGameObjects.Count == 0)
            {
                StatusStripUpdate("No GameObjects found in the selected scene.");
                return;
            }

            await RunSceneGlbExportAsync(sceneName, rootGameObjects, temporaryManagerToDispose: null);
            return;
        }

        // Path 2: Lazy Mode (no Scene Hierarchy built yet) -> resolve scene file on demand from selection or ProjectIndex
        var candidateSourcePath = ResolveLazySceneSourcePathForExport(out var lazySceneName);
        if (string.IsNullOrWhiteSpace(candidateSourcePath) || !File.Exists(candidateSourcePath))
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Export Scene to .glb",
                "No loaded Scene Hierarchy or scene file (levelN / scene bundle) was detected.\n\n" +
                "Load a scene file (such as level0 or a scene bundle) with 'Load file' or 'Load folder (Full / Scene Hierarchy)', or select an asset from a scene bundle in the Asset List first.");
            return;
        }

        var suggestedName = FixFileName(string.IsNullOrWhiteSpace(lazySceneName)
            ? Path.GetFileNameWithoutExtension(candidateSourcePath)
            : lazySceneName) + ".glb";
        var saveFile = await PickSaveSceneGlbDialogAsync("Export Scene to .glb (Instanced)", suggestedName);
        if (string.IsNullOrWhiteSpace(saveFile))
        {
            return;
        }

        var exportFolder = Path.GetDirectoryName(saveFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var ctsToken = ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            100,
            exportFolder,
            $"Loading scene '{Path.GetFileName(candidateSourcePath)}'...");
        StatusStripUpdate($"Loading scene '{Path.GetFileName(candidateSourcePath)}' for .glb export...");

        SceneGlbExportResult? result = null;
        await Task.Run(() =>
        {
            using var tempManager = new AssetsManager();
            tempManager.LazyLoading = false;
            tempManager.SpecifyUnityVersion = assetsManager.SpecifyUnityVersion;
            tempManager.ProjectRoot = ResolveEffectiveScriptProjectRoot() ?? assetsManager.ProjectRoot;
            tempManager.AutoLoadCatalogDependencies = ViewModel.Options.AutoLoadCatalogDependencies;
            tempManager.AutoLoadExternalDependencies = ViewModel.Options.AutoLoadExternalDependencies;

            tempManager.LoadFiles(candidateSourcePath);
            ctsToken.ThrowIfCancellationRequested();

            var roots = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            foreach (var sf in tempManager.assetsFileList)
            {
                foreach (var go in SceneGlbExportService.CollectRootGameObjectsFromSerializedFile(sf))
                {
                    if (seen.Add(go))
                    {
                        roots.Add(go);
                    }
                }
            }

            if (roots.Count == 0)
            {
                result = new SceneGlbExportResult
                {
                    Success = false,
                    OutputPath = saveFile,
                    SceneName = Path.GetFileNameWithoutExtension(saveFile),
                    ErrorMessage = $"No GameObject hierarchy was found inside '{Path.GetFileName(candidateSourcePath)}'."
                };
                return;
            }

            var options = BuildSceneGlbExportOptions(saveFile, Path.GetFileNameWithoutExtension(saveFile));
            result = _sceneGlbExportService.ExportScene(
                roots,
                options,
                textureResolver: ResolveTextureForModelConverter,
                progressCallback: (pct, _, status) => ReportSceneExportProgress(pct, status),
                cancellationToken: ctsToken);
        }, ctsToken);

        await CompleteSceneGlbExportUiAsync(result, exportFolder);
    }

    private async Task ExportSelectedSubtreeGlbCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        GameObjectNode? targetNode = SceneTreeView?.SelectedItem as GameObjectNode;
        if (targetNode == null)
        {
            var checkedNodes = GetSelectedParentNodes();
            if (checkedNodes.Count > 0)
            {
                targetNode = checkedNodes[0];
            }
        }

        if (targetNode == null)
        {
            StatusStripUpdate("Select a GameObject or Scene node in Scene Hierarchy first.");
            return;
        }

        var roots = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(targetNode);
        if (roots.Count == 0)
        {
            StatusStripUpdate("Selected node contains no GameObjects to export.");
            return;
        }

        var sceneName = string.IsNullOrWhiteSpace(targetNode.Name) ? "Subtree" : targetNode.Name;
        await RunSceneGlbExportAsync(sceneName, roots, temporaryManagerToDispose: null);
    }

    private async Task RunSceneGlbExportAsync(
        string sceneName,
        List<GameObject> rootGameObjects,
        AssetsManager? temporaryManagerToDispose)
    {
        var suggestedFileName = FixFileName(sceneName) + ".glb";
        var exportFile = await PickSaveSceneGlbDialogAsync($"Export Scene '{sceneName}' to .glb (Instanced)", suggestedFileName);
        if (string.IsNullOrWhiteSpace(exportFile))
        {
            temporaryManagerToDispose?.Dispose();
            return;
        }

        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var ctsToken = ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            100,
            exportFolder,
            $"Exporting scene '{sceneName}'...");
        StatusStripUpdate($"Exporting scene '{sceneName}' to {Path.GetFileName(exportFile)}...");

        SceneGlbExportResult? result = null;
        try
        {
            var options = BuildSceneGlbExportOptions(exportFile, sceneName);
            await Task.Run(() =>
            {
                using var textureResolverScope = ModelConverter.PushTextureResolver(ResolveTextureForModelConverter);
                result = _sceneGlbExportService.ExportScene(
                    rootGameObjects,
                    options,
                    textureResolver: ResolveTextureForModelConverter,
                    progressCallback: (pct, _, status) => ReportSceneExportProgress(pct, status),
                    cancellationToken: ctsToken);
            }, ctsToken);
        }
        catch (OperationCanceledException)
        {
            result = new SceneGlbExportResult
            {
                Success = false,
                OutputPath = exportFile,
                SceneName = sceneName,
                ErrorMessage = "Scene export cancelled."
            };
        }
        finally
        {
            temporaryManagerToDispose?.Dispose();
        }

        await CompleteSceneGlbExportUiAsync(result, exportFolder);
    }

    private SceneGlbExportOptions BuildSceneGlbExportOptions(string outputPath, string sceneName)
    {
        var binary = !outputPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
        return new SceneGlbExportOptions
        {
            OutputPath = outputPath,
            SceneName = sceneName,
            IncludeInactive = true,
            IncludeAnimations = exportOptions.ExportAnimations,
            ExportSkins = exportOptions.ExportSkins,
            ExportBlendShape = exportOptions.ExportBlendShape,
            EnableInstancing = true,
            ScaleFactor = (float)exportOptions.ScaleFactor,
            ImageFormat = exportOptions.ConvertTextureFormat,
            Binary = binary
        };
    }

    private void ReportSceneExportProgress(int percent, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsWindowClosingOrClosed)
            {
                return;
            }
            ViewModel.LoadingProgress = Math.Clamp(percent, 0, 100);
            StatusStripUpdate(status);
        }, DispatcherPriority.Background);
    }

    private async Task CompleteSceneGlbExportUiAsync(SceneGlbExportResult? result, string? exportFolder)
    {
        ViewModel.LoadingProgress = 100;
        if (result != null && result.Success)
        {
            ExportState.Complete(1, statusText: result.SummaryMessage, errorLogPath: null);
            StatusStripUpdate(result.SummaryMessage);

            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
            else
            {
                await ViewModel.DialogService.ShowMessageAsync("Scene Export Complete", result.SummaryMessage);
            }
        }
        else
        {
            var errorMsg = result?.ErrorMessage ?? "Scene export failed.";
            ExportState.Fail(errorMsg, null);
            StatusStripUpdate($"Scene export failed: {errorMsg}");
            await ViewModel.DialogService.ShowErrorAsync("Scene Export Failed", errorMsg);
        }
    }

    private async Task<string?> PickSaveSceneGlbDialogAsync(string title, string suggestedFileName)
    {
        var filters = new[]
        {
            new FileTypeFilterSpec("glTF Binary Scene (*.glb)", "*.glb"),
            new FileTypeFilterSpec("glTF Separate Scene (*.gltf)", "*.gltf")
        };

        var result = await ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: title,
            suggestedFileName: suggestedFileName,
            defaultExtension: "glb",
            suggestedStartDirectory: GetSuggestedExportStartDirectory(),
            fileTypeFilters: filters));

        return result;
    }

    private string? GetSuggestedExportStartDirectory()
    {
        if (!string.IsNullOrWhiteSpace(appSettings.ExportFolderPath) && Directory.Exists(appSettings.ExportFolderPath))
        {
            return appSettings.ExportFolderPath;
        }
        return null;
    }

    private string? ResolveLazySceneSourcePathForExport(out string? sceneName)
    {
        sceneName = null;

        // 1. Selected asset in Asset List (e.g., SceneAsset, GameObject, or asset inside a levelN / scene bundle)
        if (AssetListDataGrid?.SelectedItem is AssetItem selected)
        {
            var srcPath = selected.SourceFile?.originalPath
                ?? selected.SourceFile?.fullName
                ?? selected.Handle?.OriginalPath;
            if (!string.IsNullOrWhiteSpace(srcPath) && File.Exists(srcPath))
            {
                sceneName = !string.IsNullOrWhiteSpace(selected.SourceFile?.fileName)
                    ? selected.SourceFile.fileName
                    : Path.GetFileNameWithoutExtension(srcPath);
                return srcPath;
            }
        }

        // 2. Loaded SerializedFiles that look like scenes (levelN / mainData / contain GameObjects)
        foreach (var sf in assetsManager.assetsFileList)
        {
            if (sf == null || string.IsNullOrWhiteSpace(sf.originalPath) || !File.Exists(sf.originalPath))
            {
                continue;
            }

            if (sf.fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sf.fileName, "mainData", StringComparison.OrdinalIgnoreCase)
                || sf.Objects.Any(static o => o is GameObject))
            {
                sceneName = sf.fileName;
                return sf.originalPath;
            }
        }

        // 3. Scan Lazy ProjectIndex Arena for a levelN / scene bundle source file
        var arena = assetsManager.ProjectIndex.Arena;
        for (int i = 0; i < arena.Count; i++)
        {
            if (arena.GetType(i) != ClassIDType.GameObject)
            {
                continue;
            }

            var sfName = arena.GetSerializedFileName(i);
            var origPath = arena.GetOriginalPath(i);
            if (!string.IsNullOrWhiteSpace(origPath)
                && File.Exists(origPath)
                && (sfName.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sfName, "mainData", StringComparison.OrdinalIgnoreCase)
                    || origPath.Contains("scene", StringComparison.OrdinalIgnoreCase)))
            {
                sceneName = !string.IsNullOrWhiteSpace(sfName) ? sfName : Path.GetFileNameWithoutExtension(origPath);
                return origPath;
            }
        }

        return null;
    }
}
