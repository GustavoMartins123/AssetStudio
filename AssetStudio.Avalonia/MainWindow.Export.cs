using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private async void ExportAllAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets.ToList(), ExportMode.Convert);
    private async void ExportSelectedAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Convert);
    private async void ExportFilteredAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Convert);
    private async void ExportAllAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets, ExportMode.Raw);
    private async void ExportSelectedAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Raw);
    private async void ExportFilteredAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Raw);
    private async void ExportAllAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets, ExportMode.Dump);
    private async void ExportSelectedAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Dump);
    private async void ExportFilteredAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Dump);
    private async void ExportAllAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(exportableAssets);
    private async void ExportSelectedAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(GetSelectedAssets());
    private async void ExportFilteredAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(visibleAssets.ToList());

    private async void ExportErrorLog_Click(object? sender, RoutedEventArgs e)
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

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);

        StatusStripUpdate("Exporting asset list to XML...");
        await Task.Run(() =>
        {
            try
            {
                var filename = Path.Combine(savePath, "assets.xml");
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    Indent = true,
                    CloseOutput = true
                };
                using var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                using var writer = XmlWriter.Create(output, settings);
                writer.WriteStartDocument();
                writer.WriteStartElement("Assets");
                writer.WriteAttributeString("filename", filename);
                writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                foreach (var asset in toExport)
                {
                    writer.WriteStartElement("Asset");
                    writer.WriteElementString("Name", asset.Name ?? string.Empty);
                    writer.WriteElementString("Container", asset.Container ?? string.Empty);
                    writer.WriteStartElement("Type");
                    writer.WriteAttributeString("id", ((int)asset.Type).ToString(CultureInfo.InvariantCulture));
                    writer.WriteString(asset.DisplayType ?? string.Empty);
                    writer.WriteEndElement();
                    writer.WriteElementString("PathID", asset.PathID.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString("Source", GetAssetSourcePath(asset));
                    writer.WriteElementString("Size", asset.FullSize.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndDocument();
                writer.Flush();

                StatusStripUpdate($"Finished exporting asset list to XML with {toExport.Count} items.");
                if (exportOptions.OpenAfterExport)
                {
                    OpenFolder(savePath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error exporting asset list to XML", ex);
                StatusStripUpdate($"Error exporting asset list to XML: {ex.Message}");
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
        if (mode == ExportMode.Convert)
        {
            toExport = toExport.Where(x => !ShouldSkipConvertedAsset(x)).ToList();
        }

        if (toExport.Count == 0)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        if (!assemblyLoader.Loaded && (mode == ExportMode.Convert || mode == ExportMode.Dump) && toExport.Any(x => x.Type == ClassIDType.MonoBehaviour))
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select Assembly Folder"));
            if (folders != null && folders.Count > 0)
            {
                SaveLoadFolder(folders[0].Path.LocalPath);
                assemblyLoader.Load(folders[0].Path.LocalPath);
            }
            else
            {
                assemblyLoader.Loaded = true;
            }
        }

        var exportFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));

        if (exportFolders == null || exportFolders.Count == 0) return;

        var savePath = exportFolders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        if (mode == ExportMode.Convert)
        {
            toExport = OrderConvertedAssetsForExport(toExport);
        }

        int total = toExport.Count;
        int exported = 0;
        int skipped = 0;
        int failed = 0;
        var exportErrors = new List<string>();

        StatusStripUpdate($"Exporting {total} assets...");

        await Task.Run(() =>
        {
            EnsureLazyAssetsLoadedForExport(toExport);
            var conversionContext = new ExportConversionContext(assetsManager.assetsFileList);
            var currentExportPath = Path.Combine(savePath, "export-current.txt");
            for (int j = 0; j < total; j++)
            {
                var asset = toExport[j];
                try
                {
                    var exportedBefore = exported;
                    WriteCurrentExport(currentExportPath, asset, j + 1, total);
                    var exportPath = GetExportPath(savePath, asset);
                    Directory.CreateDirectory(exportPath);
                    var fileName = FixFileName(asset.Name);

                    switch (mode)
                    {
                        case ExportMode.Raw:
                            if (conversionContext.TryGetAssetFilePath(exportPath, fileName, GetRawExtension(asset), asset, out var rawFilePath))
                            {
                                var assetObj = asset.Asset;
                                if (assetObj != null)
                                {
                                    WriteRawAssetData(assetObj, rawFilePath);
                                    exported++;
                                }
                            }
                            break;
                        case ExportMode.Dump:
                            if (conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", asset, out var dumpFilePath))
                            {
                                string? dump = null;
                                var assetObj = asset.Asset;
                                if (assetObj is MonoBehaviour m_MonoBehaviour)
                                {
                                    dump = m_MonoBehaviour.Dump();
                                    if (dump == null && assemblyLoader.Loaded)
                                    {
                                        var typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                                        if (typeTree != null)
                                        {
                                            dump = m_MonoBehaviour.Dump(typeTree);
                                        }
                                    }
                                }
                                else if (assetObj != null)
                                {
                                    dump = assetObj.Dump();
                                }
                                File.WriteAllText(dumpFilePath, dump ?? "");
                                exported++;
                            }
                            break;
                        case ExportMode.Convert:
                            if (ExportConvertFile(asset, exportPath, conversionContext))
                                exported++;
                            break;
                    }

                    if (exported == exportedBefore)
                    {
                        skipped++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    var error = $"Failed to export {asset.TypeString}: {asset.Name} (PathID: {asset.PathID})";
                    exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                    StatusStripUpdate($"Error exporting {asset.Name}: {ex.Message}");
                }

                var progress = (int)((j + 1.0) / total * 100);
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => progressBar.Value = progress);
            }
            ClearCurrentExport(savePath);
        });

        var errorReportPath = WriteErrorReport(savePath, exportErrors, logger);

        var status = exported == 0 ? "Nothing exported." : $"Finished exporting {exported} assets.";
        if (skipped > 0) status += $" {skipped} skipped (already exported or unsupported).";
        if (failed > 0) status += $" {failed} failed.";
        if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport && exported > 0)
        {
            OpenFolder(savePath);
        }
    }

    private static List<AssetItem> OrderConvertedAssetsForExport(List<AssetItem> assets)
    {
        return assets
            .OrderBy(x => x.Type == ClassIDType.Texture2D ? 0 : x.Type == ClassIDType.Material ? 1 : 2)
            .ToList();
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

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var selectedExportRoot = folders[0].Path.LocalPath;
        SaveExportFolder(selectedExportRoot);
        var exportPath = Path.Combine(selectedExportRoot, "Animator");
        Directory.CreateDirectory(exportPath);
        var exportFile = GetUniqueFilePath(Path.Combine(exportPath, FixFileName(animator.Name) + ".fbx"));
        var selectedGameObjects = GetTopLevelSelectedGameObjects(selectedAssets
            .Where(x => x.Type != ClassIDType.AnimationClip && x.TreeNode?.GameObject != null)
            .Select(x => x.TreeNode!.GameObject!)
            .Distinct()
            .ToList());

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
                WriteCurrentExport(currentExportPath, animator, 1, 1);
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
                ClearCurrentExport(exportPath);
            }
        });

        var errorReportPath = WriteErrorReport(exportPath, exportErrors, logger);

        if (success)
        {
            StatusStripUpdate($"Finished exporting {Path.GetFileName(exportFile)}");
            if (exportOptions.OpenAfterExport)
            {
                OpenFolder(exportPath);
            }
        }
        else
        {
            var status = "Animator export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
            StatusStripUpdate(status);
        }
    }

    private async void ExportAllObjectsSplit_Click(object? sender, RoutedEventArgs e)
    {
        if (sceneTreeNodes.Count == 0)
        {
            StatusStripUpdate("No Objects available for export");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        await ExportSplitObjects(savePath, sceneTreeNodes.SelectMany(x => x.Children).ToList(), null, createObjectFolders: true);
    }

    private async void ExportSelectedObjectsSplit_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsSplit(false);
    }

    private async void ExportSelectedObjectsSplitWithAnimationClip_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsSplit(true);
    }

    private async Task ExportSelectedObjectsSplit(bool includeAnimations)
    {
        var selectedNodes = GetSelectedParentNodes();
        if (selectedNodes.Count == 0)
        {
            StatusStripUpdate("No Object selected for export.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        var exportPath = Path.Combine(savePath, "GameObject");
        Directory.CreateDirectory(exportPath);
        await ExportSplitObjects(exportPath, selectedNodes, includeAnimations ? GetSelectedAnimationClips() : null, createObjectFolders: false);
    }

    private async void ExportSelectedObjectsMerge_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsMerge(false);
    }

    private async void ExportSelectedObjectsMergeWithAnimationClip_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsMerge(true);
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

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var suggestedName = FixFileName(gameObjects[0].m_Name) + " (merge).fbx";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(await CreateFbxSaveOptions("Save merged FBX", suggestedName));
        if (file == null) return;

        var exportFile = file.Path.LocalPath;
        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var clips = includeAnimations ? GetSelectedAnimationClips() : null;
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
                    ClearCurrentExport(exportFolder);
                }
            }
        });

        var reportPath = exportFolder ?? "";
        var errorReportPath = WriteErrorReport(reportPath, exportErrors, logger);

        progressBar.Value = 100;
        if (success)
        {
            StatusStripUpdate($"Finished exporting {Path.GetFileName(exportFile)}");
            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            var status = "Merged model export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
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
        StatusStripUpdate($"Exporting {exportNodes.Count} objects...");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportRoot, "export-current.txt");
        int exported = 0;
        int failed = 0;

        await Task.Run(() =>
        {
            for (var i = 0; i < exportNodes.Count; i++)
            {
                var node = exportNodes[i];
                var gameObject = node.GameObject!;
                var targetFolder = createObjectFolders
                    ? GetUniqueDirectoryPath(Path.Combine(exportRoot, FixFileName(gameObject.m_Name)))
                    : exportRoot;
                Directory.CreateDirectory(targetFolder);

                var exportFile = GetUniqueFilePath(Path.Combine(targetFolder, FixFileName(gameObject.m_Name) + ".fbx"));
                StatusStripUpdate($"Exporting {Path.GetFileName(exportFile)}");

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(currentExportPath)!);
                    File.WriteAllText(currentExportPath,
                        $"Exporting {i + 1}/{exportNodes.Count}{Environment.NewLine}" +
                        $"Type: GameObject{Environment.NewLine}" +
                        $"Name: {gameObject.m_Name}{Environment.NewLine}" +
                        $"PathID: {gameObject.m_PathID}{Environment.NewLine}" +
                        $"Source: {gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fullName ?? gameObject.assetsFile?.fileName}{Environment.NewLine}",
                        Encoding.UTF8);

                    var convert = CreateModelConverter(gameObject, clips);
                    ExportFbx(convert, exportFile);
                    exported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    var error = $"Export GameObject:{gameObject.m_Name} error";
                    exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                    Logger.Error(error, ex);
                    StatusStripUpdate($"Error exporting {gameObject.m_Name}: {ex.Message}");
                }

                var progress = (int)((i + 1.0) / exportNodes.Count * 100);
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => progressBar.Value = progress);
            }

            ClearCurrentExport(exportRoot);
        });

        var errorReportPath = WriteErrorReport(exportRoot, exportErrors, logger);

        var status = exported == 0 ? "Nothing exported." : $"Finished exporting {exported} objects.";
        if (failed > 0) status += $" {failed} failed.";
        if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport)
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

    private static string GetUniqueDirectoryPath(string directoryPath)
    {
        var candidate = directoryPath;
        for (var i = 1; Directory.Exists(candidate); i++)
        {
            candidate = $"{directoryPath} ({i})";
        }
        return candidate;
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

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

    private string GetExportPath(string savePath, AssetItem asset)
    {
        return exportOptions.AssetGrouping switch
        {
            AssetGroupOption.Container when !string.IsNullOrEmpty(asset.Container) => Path.Combine(savePath, GetSafeRelativeExportDirectory(asset.Container)),
            AssetGroupOption.SourceFile => Path.Combine(savePath, FixFileName(asset.SourceFile?.fileName ?? asset.Handle?.SerializedFileName ?? "Unknown") + "_export"),
            AssetGroupOption.TypeName => Path.Combine(savePath, FixFileName(asset.TypeString)),
            _ => savePath
        };
    }

    private static string GetSafeRelativeExportDirectory(string container)
    {
        var directory = Path.GetDirectoryName(container.Replace('\\', '/')) ?? string.Empty;
        var safeParts = directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != "." && part != "..")
            .Select(FixFileName)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        return safeParts.Length == 0 ? string.Empty : Path.Combine(safeParts);
    }

    private string GetRawExtension(AssetItem asset)
    {
        if (!exportOptions.RestoreExtensionName) return ".dat";
        return asset.Asset switch
        {
            Texture2D => ".tex",
            TextAsset => ".txt",
            Shader => ".shader",
            Font m_Font when m_Font.m_FontData?.Length >= 4 && m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79 => ".otf",
            Font => ".ttf",
            MovieTexture => ".ogv",
            VideoClip m_VideoClip when !string.IsNullOrEmpty(m_VideoClip.m_OriginalPath) => Path.GetExtension(m_VideoClip.m_OriginalPath),
            AudioClip m_AudioClip => new AudioClipConverter(m_AudioClip).GetExtensionName(),
            _ => ".dat"
        };
    }

    private static void WriteCurrentExport(string path, AssetItem asset, int index, int count)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"Exporting {index}/{count}{Environment.NewLine}" +
                $"Type: {asset.TypeString}{Environment.NewLine}" +
                $"Name: {asset.Name}{Environment.NewLine}" +
                $"PathID: {asset.PathID}{Environment.NewLine}" +
                $"Source: {asset.SourceFile?.originalPath ?? asset.SourceFile?.fullName ?? asset.SourceFile?.fileName}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void ClearCurrentExport(string savePath)
    {
        try
        {
            var path = Path.Combine(savePath, "export-current.txt");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string GetAssetSourcePath(AssetItem asset)
    {
        var sourceFile = asset.SourceFile;
        if (!string.IsNullOrWhiteSpace(sourceFile?.originalPath))
        {
            return sourceFile.originalPath;
        }
        if (!string.IsNullOrWhiteSpace(sourceFile?.fullName))
        {
            return sourceFile.fullName;
        }
        return asset.Handle?.OriginalPath ?? string.Empty;
    }

    private static void WriteRawAssetData(AssetStudio.Object asset, string filePath)
    {
        asset.assetsFile.ObjectInfoDic.TryGetValue(asset.m_PathID, out var objectInfo);
        objectInfo ??= new ObjectInfo
        {
            byteStart = asset.reader.byteStart,
            byteSize = asset.byteSize,
            classID = (int)asset.type,
            m_PathID = asset.m_PathID,
            serializedType = asset.serializedType
        };

        var outputCreated = false;
        try
        {
            using var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.SequentialScan);
            outputCreated = true;
            using var fileReader = asset.assetsFile.reader.Clone();
            using var objectReader = new ObjectReader(fileReader, asset.assetsFile, objectInfo);
            objectReader.Reset();

            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                long remaining = asset.byteSize;
                while (remaining > 0)
                {
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = objectReader.BaseStream.Read(buffer, 0, requested);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of asset data with {remaining:N0} bytes remaining.");
                    }

                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            try
            {
                if (outputCreated && File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    private static void TryDeletePartialExport(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private static string? WriteErrorReport(string savePath, List<string> exportErrors, GUILogger logger)
    {
        var loadErrors = logger.GetMessages(LoggerEvent.Error);
        if (loadErrors.Length == 0 && exportErrors.Count == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(savePath);
            var errorReportPath = Path.Combine(savePath, "errors.txt");
            using (var writer = new StreamWriter(errorReportPath, false, Encoding.UTF8))
            {
                writer.WriteLine("AssetStudio error report");
                writer.WriteLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine();

                if (loadErrors.Length > 0)
                {
                    writer.WriteLine($"Logged errors ({loadErrors.Length})");
                    writer.WriteLine(new string('=', 80));
                    for (int i = 0; i < loadErrors.Length; i++)
                    {
                        writer.WriteLine($"[{i + 1}]");
                        writer.WriteLine(loadErrors[i]);
                        writer.WriteLine();
                    }
                }

                if (exportErrors.Count > 0)
                {
                    writer.WriteLine($"Export errors ({exportErrors.Count})");
                    writer.WriteLine(new string('=', 80));
                    for (int i = 0; i < exportErrors.Count; i++)
                    {
                        writer.WriteLine($"[{i + 1}]");
                        writer.WriteLine(exportErrors[i]);
                        writer.WriteLine();
                    }
                }
            }
            return errorReportPath;
        }
        catch
        {
            return null;
        }
    }

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

    private bool ExportConvertFile(AssetItem item, string exportPath, ExportConversionContext conversionContext)
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
        var fileName = FixFileName(GetExportFileName(item));

        switch (assetObj)
        {
            case Animator m_Animator:
            {
                var convert = new ModelConverter(m_Animator, exportOptions.ConvertTextureFormat);
                bool exported = false;
                if (convert.MeshList.Count > 0
                    && conversionContext.TryGetAssetFilePath(exportPath, fileName, ".fbx", item, out var exportFullPath))
                {
                    ExportFbx(convert, exportFullPath);
                    exported = true;
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
                return conversionContext.TryGetAssetFilePath(exportPath, fileName, ".asset", item, out var avatarFullPath)
                    && AssetExportHelper.ExportAvatar(m_Avatar, avatarFullPath);
            }
            case AnimatorController m_AnimatorController:
            {
                return conversionContext.TryGetAssetFilePath(exportPath, fileName, ".controller", item, out var controllerFullPath)
                    && AssetExportHelper.ExportAnimatorController(m_AnimatorController, controllerFullPath);
            }
            case AnimatorOverrideController m_AnimatorOverrideController:
            {
                return conversionContext.TryGetAssetFilePath(exportPath, fileName, ".overrideController", item, out var overrideFullPath)
                    && AssetExportHelper.ExportAnimatorOverrideController(m_AnimatorOverrideController, overrideFullPath);
            }
            case AnimationClip m_AnimationClip:
            {
                return AssetExportHelper.ExportAnimationClip(
                    m_AnimationClip,
                    fileName,
                    exportPath,
                    conversionContext.BonePathHash,
                    conversionContext.MorphChannelNames);
            }
            case Mesh m_Mesh:
            {
                return ExportMesh(item, m_Mesh, exportPath, fileName, conversionContext);
            }
            case Texture2D m_Texture2D:
            {
                if (!exportOptions.ConvertTexture)
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".tex", item, out var rawPath)) return false;
                    File.WriteAllBytes(rawPath, m_Texture2D.image_data.GetData());
                    AssetExportHelper.WriteTextureMetaIfMissing(rawPath, m_Texture2D);
                    return true;
                }

                var image = m_Texture2D.ConvertToImage(true);
                if (image == null) return false;
                var extension = "." + exportOptions.ConvertTextureFormat.ToString().ToLowerInvariant();
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                using (image)
                using (var file = File.OpenWrite(filePath))
                {
                    image.WriteToStream(file, exportOptions.ConvertTextureFormat);
                }
                AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Texture2D);
                return true;
            }
            case AudioClip m_AudioClip:
            {
                var m_AudioData = m_AudioClip.m_AudioData.GetData();
                if (m_AudioData == null || m_AudioData.Length == 0) return false;
                var converter = new AudioClipConverter(m_AudioClip);
                if (exportOptions.ConvertAudio && converter.IsSupport)
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".wav", item, out var filePath)) return false;
                    var buffer = converter.ConvertToWav();
                    if (buffer == null) return false;
                    File.WriteAllBytes(filePath, buffer);
                }
                else
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, converter.GetExtensionName(), item, out var filePath)) return false;
                    File.WriteAllBytes(filePath, m_AudioData);
                }
                return true;
            }
            case Material m_Material:
            {
                return AssetExportHelper.ExportMaterial(m_Material, item.Name, exportPath, exportOptions.ConvertTextureFormat);
            }
            case TextAsset m_TextAsset:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var filePath)) return false;
                File.WriteAllBytes(filePath, m_TextAsset.m_Script);
                return true;
            }
            case MonoScript m_MonoScript:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var filePath)) return false;
                var sb = new StringBuilder();
                sb.AppendLine($"Assembly: {m_MonoScript.m_AssemblyName}");
                sb.AppendLine($"Namespace: {m_MonoScript.m_Namespace}");
                sb.AppendLine($"Class: {m_MonoScript.m_ClassName}");
                File.WriteAllText(filePath, sb.ToString());
                return true;
            }
            case Shader m_Shader:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".shader", item, out var filePath)) return false;
                var str = m_Shader.Convert();
                File.WriteAllText(filePath, str);
                return true;
            }
            case Font m_Font:
            {
                if (m_Font.m_FontData == null || m_Font.m_FontData.Length == 0) return false;
                var ext = ".ttf";
                if (m_Font.m_FontData.Length >= 4
                    && m_Font.m_FontData[0] == 79
                    && m_Font.m_FontData[1] == 84
                    && m_Font.m_FontData[2] == 84
                    && m_Font.m_FontData[3] == 79)
                    ext = ".otf";
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ext, item, out var filePath)) return false;
                File.WriteAllBytes(filePath, m_Font.m_FontData);
                return true;
            }
            case Sprite m_Sprite:
            {
                var image = m_Sprite.GetImage();
                if (image == null) return false;
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".png", item, out var filePath)) return false;
                using (image)
                using (var file = File.OpenWrite(filePath))
                {
                    image.WriteToStream(file, ImageFormat.Png);
                }
                AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Sprite);
                return true;
            }
            case VideoClip m_VideoClip:
            {
                if (m_VideoClip.m_ExternalResources.m_Size <= 0) return false;
                var extension = Path.GetExtension(m_VideoClip.m_OriginalPath);
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                m_VideoClip.m_VideoData.WriteData(filePath);
                return true;
            }
            case VideoPlayer m_VideoPlayer:
            {
                if (m_VideoPlayer.m_VideoClip.TryGet(out var resolvedClip) && resolvedClip != null)
                {
                    if (resolvedClip.m_ExternalResources.m_Size <= 0) return false;
                    var extension = Path.GetExtension(resolvedClip.m_OriginalPath);
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                    resolvedClip.m_VideoData.WriteData(filePath);
                    return true;
                }
                else if (m_VideoPlayer.m_Source == 1 && !string.IsNullOrEmpty(m_VideoPlayer.m_Url))
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName + "_url", ".txt", item, out var filePath)) return false;
                    File.WriteAllText(filePath, m_VideoPlayer.m_Url);
                    return true;
                }
                return false;
            }
            case MovieTexture m_MovieTexture:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".ogv", item, out var filePath)) return false;
                File.WriteAllBytes(filePath, m_MovieTexture.m_MovieData);
                return true;
            }
            case MonoBehaviour m_MonoBehaviour:
            {
                object? obj = m_MonoBehaviour.ToType();
                if (obj == null && assemblyLoader.Loaded)
                {
                    var typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                    if (typeTree != null)
                    {
                        obj = m_MonoBehaviour.ToType(typeTree);
                    }
                }

                if (obj != null)
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".json", item, out var filePath)) return false;
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(filePath, str);
                    return true;
                }

                // Fallback to text asset dump
                var dumpStr = m_MonoBehaviour.Dump();
                if (dumpStr == null && assemblyLoader.Loaded)
                {
                    var typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                    if (typeTree != null)
                    {
                        dumpStr = m_MonoBehaviour.Dump(typeTree);
                    }
                }

                if (dumpStr != null)
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var dumpPath)) return false;
                    File.WriteAllText(dumpPath, dumpStr);
                    return true;
                }

                return false;
            }
            case Object obj when obj.type == ClassIDType.PrefabInstance:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName + "_prefab_report", ".txt", item, out var filePath)) return false;
                var report = FormatPrefab(obj);
                File.WriteAllText(filePath, report);
                return true;
            }
            default:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".dat", item, out var filePath)) return false;
                WriteRawAssetData(assetObj, filePath);
                return true;
            }
        }
    }

    private static bool ShouldSkipConvertedAsset(AssetItem item)
    {
        return (item.IsFbxSubAsset() && (item.Asset is Material || item.Asset is Shader));
    }

    private bool ExportMesh(
        AssetItem item,
        Mesh mesh,
        string exportPath,
        string fileName,
        ExportConversionContext conversionContext)
    {
        if (item.TreeNode?.GameObject != null)
        {
            var animator = FindAnimatorForModelExport(item);
            var convert = animator != null
                ? new ModelConverter(animator, exportOptions.ConvertTextureFormat)
                : new ModelConverter(item.TreeNode.GameObject, exportOptions.ConvertTextureFormat);
            if (convert.MeshList.Count > 0
                && conversionContext.TryGetAssetFilePath(exportPath, fileName, ".fbx", item, out var fbxPath))
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

        var vertexComponentCount = mesh.m_Vertices.Length / mesh.m_VertexCount;
        if (vertexComponentCount < 3)
        {
            return false;
        }

        vertexComponentCount = Math.Min(vertexComponentCount, 4);
        var uvs = mesh.m_UV0;
        var normals = mesh.m_Normals;
        var uvComponentCount = uvs != null && uvs.Length >= mesh.m_VertexCount * 2
            ? Math.Min(uvs.Length / mesh.m_VertexCount, 4)
            : 0;
        var normalComponentCount = normals != null && normals.Length >= mesh.m_VertexCount * 3
            ? Math.Min(normals.Length / mesh.m_VertexCount, 4)
            : 0;
        var hasUvs = uvComponentCount >= 2;
        var hasNormals = normalComponentCount >= 3;

        try
        {
            using var writer = new StreamWriter(
                objPath,
                append: false,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 64 * 1024);
            var groupName = FixObjGroupName(mesh.m_Name);
            writer.WriteLine("g " + groupName);

            for (int vertex = 0; vertex < mesh.m_VertexCount; vertex++)
            {
                writer.WriteLine(
                    "v {0} {1} {2}",
                    CleanFloat(-mesh.m_Vertices[vertex * vertexComponentCount]),
                    CleanFloat(mesh.m_Vertices[vertex * vertexComponentCount + 1]),
                    CleanFloat(mesh.m_Vertices[vertex * vertexComponentCount + 2]));
            }

            if (hasUvs)
            {
                for (int vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                {
                    writer.WriteLine(
                        "vt {0} {1}",
                        CleanFloat(uvs![vertex * uvComponentCount]),
                        CleanFloat(uvs[vertex * uvComponentCount + 1]));
                }
            }

            if (hasNormals)
            {
                for (int vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                {
                    writer.WriteLine(
                        "vn {0} {1} {2}",
                        CleanFloat(-normals![vertex * normalComponentCount]),
                        CleanFloat(normals[vertex * normalComponentCount + 1]),
                        CleanFloat(normals[vertex * normalComponentCount + 2]));
                }
            }

            var firstIndex = 0;
            for (var subMeshIndex = 0; subMeshIndex < mesh.m_SubMeshes.Length && firstIndex < mesh.m_Indices.Count; subMeshIndex++)
            {
                writer.WriteLine($"g {groupName}_{subMeshIndex.ToString(CultureInfo.InvariantCulture)}");
                var requestedIndexCount = (long)mesh.m_SubMeshes[subMeshIndex].indexCount;
                var availableIndexCount = mesh.m_Indices.Count - firstIndex;
                var indexCount = (int)Math.Min(requestedIndexCount, availableIndexCount);
                var faceCount = indexCount / 3;
                for (var face = 0; face < faceCount; face++)
                {
                    var faceStart = firstIndex + face * 3;
                    var first = mesh.m_Indices[faceStart + 2];
                    var second = mesh.m_Indices[faceStart + 1];
                    var third = mesh.m_Indices[faceStart];
                    if (first >= mesh.m_VertexCount || second >= mesh.m_VertexCount || third >= mesh.m_VertexCount)
                    {
                        continue;
                    }

                    writer.WriteLine(
                        "f {0} {1} {2}",
                        FormatObjVertexReference(first, hasUvs, hasNormals),
                        FormatObjVertexReference(second, hasUvs, hasNormals),
                        FormatObjVertexReference(third, hasUvs, hasNormals));
                }
                firstIndex += indexCount;
            }

            return true;
        }
        catch
        {
            TryDeletePartialExport(objPath);
            throw;
        }
    }

    private static string FormatObjVertexReference(uint zeroBasedIndex, bool hasUvs, bool hasNormals)
    {
        var index = ((ulong)zeroBasedIndex + 1UL).ToString(CultureInfo.InvariantCulture);
        if (hasUvs && hasNormals) return $"{index}/{index}/{index}";
        if (hasUvs) return $"{index}/{index}";
        if (hasNormals) return $"{index}//{index}";
        return index;
    }

    private static string FixObjGroupName(string? name)
    {
        return FixFileName(name ?? string.Empty)
            .Replace(' ', '_')
            .Replace('\t', '_');
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

    private static string GetExportFileName(AssetItem item)
    {
        var fbxContainer = GetFbxContainerPath(item.Container);
        if (fbxContainer != null && item.Asset is Mesh or Animator)
        {
            var name = Path.GetFileNameWithoutExtension(fbxContainer);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return item.Name;
    }

    private static string? GetFbxContainerPath(string container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }

        var parts = container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            path = string.IsNullOrEmpty(path) ? part : Path.Combine(path, part);
            if (string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    private void ExportFbx(IImported convert, string exportFile)
    {
        if (exportOptions.ExportAnimations && exportOptions.ExportAnimationsSplit && convert.AnimationList?.Count > 0)
        {
            // 1. Export main model without animations
            var mainConvert = new ImportedWrapper(convert)
            {
                AnimationList = new List<ImportedKeyframedAnimation>()
            };
            ModelExporter.ExportFbx(exportFile, mainConvert,
                exportOptions.EulerFilter,
                (float)exportOptions.FilterPrecision,
                exportOptions.ExportAllNodes,
                exportOptions.ExportSkins,
                false, // Disable animation for main export
                exportOptions.ExportBlendShape,
                exportOptions.CastToBone,
                (float)exportOptions.BoneSize,
                exportOptions.ExportAllUvsAsDiffuseMaps,
                (float)exportOptions.ScaleFactor,
                exportOptions.FbxVersion,
                exportOptions.FbxFormat == 1);

            // 2. Export each animation clip separately
            foreach (var anim in convert.AnimationList)
            {
                var animFile = GetUniqueFilePath(Path.Combine(
                    Path.GetDirectoryName(exportFile)!,
                    $"{Path.GetFileNameWithoutExtension(exportFile)}_{FixFileName(anim.Name)}.fbx"));
                var animConvert = new ImportedWrapper(convert)
                {
                    AnimationList = new List<ImportedKeyframedAnimation> { anim }
                };
                ModelExporter.ExportFbx(animFile, animConvert,
                    exportOptions.EulerFilter,
                    (float)exportOptions.FilterPrecision,
                    exportOptions.ExportAllNodes,
                    exportOptions.ExportSkins,
                    true,
                    exportOptions.ExportBlendShape,
                    exportOptions.CastToBone,
                    (float)exportOptions.BoneSize,
                    exportOptions.ExportAllUvsAsDiffuseMaps,
                    (float)exportOptions.ScaleFactor,
                    exportOptions.FbxVersion,
                    exportOptions.FbxFormat == 1);
            }
        }
        else
        {
            ModelExporter.ExportFbx(exportFile, convert,
                exportOptions.EulerFilter,
                (float)exportOptions.FilterPrecision,
                exportOptions.ExportAllNodes,
                exportOptions.ExportSkins,
                exportOptions.ExportAnimations,
                exportOptions.ExportBlendShape,
                exportOptions.CastToBone,
                (float)exportOptions.BoneSize,
                exportOptions.ExportAllUvsAsDiffuseMaps,
                (float)exportOptions.ScaleFactor,
                exportOptions.FbxVersion,
                exportOptions.FbxFormat == 1);
        }
    }

    private sealed class ExportConversionContext
    {
        private readonly List<SerializedFile> assetsFiles;
        private readonly HashSet<string> reservedOutputPaths = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private Dictionary<uint, string>? bonePathHash;
        private Dictionary<uint, string>? morphChannelNames;

        public ExportConversionContext(List<SerializedFile> assetsFiles)
        {
            this.assetsFiles = assetsFiles;
        }

        public Dictionary<uint, string> BonePathHash =>
            bonePathHash ??= AssetExportHelper.BuildBonePathHash(assetsFiles);

        public Dictionary<uint, string> MorphChannelNames =>
            morphChannelNames ??= AssetExportHelper.BuildMorphChannelNames(assetsFiles);

        public bool TryGetAssetFilePath(
            string directory,
            string fileName,
            string extension,
            AssetItem asset,
            out string filePath)
        {
            fileName = FixFileName(fileName);
            filePath = Path.Combine(directory, fileName + extension);
            var normalizedPath = Path.GetFullPath(filePath);
            if (reservedOutputPaths.Add(normalizedPath))
            {
                return !File.Exists(filePath);
            }

            var uniqueId = asset.Handle?.UniqueID;
            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                uniqueId = asset.UniqueID;
            }
            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                uniqueId = $"{asset.Type}:{asset.PathID.ToString(CultureInfo.InvariantCulture)}:{GetAssetSourcePath(asset)}";
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uniqueId)))
                .Substring(0, 8)
                .ToLowerInvariant();
            var suffix = $"_{asset.PathID.ToString(CultureInfo.InvariantCulture)}_{hash}";
            var maxBaseLength = Math.Max(1, 120 - suffix.Length);
            if (fileName.Length > maxBaseLength)
            {
                fileName = fileName.Substring(0, maxBaseLength);
            }

            filePath = Path.Combine(directory, fileName + suffix + extension);
            normalizedPath = Path.GetFullPath(filePath);
            return reservedOutputPaths.Add(normalizedPath) && !File.Exists(filePath);
        }
    }

    private static string CleanFloat(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? "0"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FixFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(character < 32 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : character);
        }

        name = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var dotIndex = name.IndexOf('.');
        var deviceName = (dotIndex >= 0 ? name.Substring(0, dotIndex) : name).ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
        {
            name = "_" + name;
        }

        return name.Length <= 120 ? name : name.Substring(0, 120).TrimEnd(' ', '.');
    }
}
