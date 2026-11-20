using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal const int DefaultCurrentExportWriteIntervalMs = 250;
    internal const int DefaultExportUiProgressIntervalMs = 50;

    internal static int GetExportThreadCount(int requestedDegreeOfParallelism = 0)
    {
        if (requestedDegreeOfParallelism > 0)
        {
            return requestedDegreeOfParallelism;
        }

        return Math.Clamp(
            AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_EXPORT_THREADS", 1.0),
            1,
            16);
    }
    private async void ExportAllAssets_Click(object? sender, RoutedEventArgs e) => await ExportAllAssetsAsync(ExportMode.Convert);
    private async void ExportSelectedAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Convert);
    private async void ExportFilteredAssets_Click(object? sender, RoutedEventArgs e) => await ExportFilteredAssetsAsync(ExportMode.Convert);
    private async void ExportAllAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAllAssetsAsync(ExportMode.Raw);
    private async void ExportSelectedAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Raw);
    private async void ExportFilteredAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportFilteredAssetsAsync(ExportMode.Raw);
    private async void ExportAllAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAllAssetsAsync(ExportMode.Dump);
    private async void ExportSelectedAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Dump);
    private async void ExportFilteredAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportFilteredAssetsAsync(ExportMode.Dump);
    private async void ExportAllAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAllAssetsListAsync();
    private async void ExportSelectedAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(GetSelectedAssets());
    private async void ExportFilteredAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportFilteredAssetsListAsync();

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

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        var arena = assetsManager.ProjectIndex.Arena;

        StatusStripUpdate("Exporting asset list to XML...");
        await Task.Run(() =>
        {
            var filename = Path.Combine(savePath, "assets.xml");
            try
            {
                var exportedCount = WriteArenaAssetsXml(arena, arenaIndices, filename);
                StatusStripUpdate($"Finished exporting asset list to XML with {exportedCount} items.");
                if (exportOptions.OpenAfterExport)
                {
                    OpenFolder(savePath);
                }
            }
            catch (Exception ex)
            {
                TryDeletePartialExport(filename);
                Logger.Error("Error exporting asset list to XML", ex);
                StatusStripUpdate($"Error exporting asset list to XML: {ex.Message}");
            }
        });
    }

    internal static int WriteArenaAssetsXml(AssetIndexArena arena, IReadOnlyList<int> arenaIndices, string filename)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(arenaIndices);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            CloseOutput = true
        };

        var writtenCount = 0;
        var arenaCount = arena.Count;
        using (var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        using (var writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Assets");
            writer.WriteAttributeString("filename", filename);
            writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            for (var i = 0; i < arenaIndices.Count; i++)
            {
                var idx = arenaIndices[i];
                if ((uint)idx >= (uint)arenaCount)
                {
                    continue;
                }

                var name = arena.GetName(idx);
                var container = arena.GetContainer(idx);
                var type = arena.GetType(idx);
                var pathId = arena.GetPathId(idx);
                var byteSize = arena.GetByteSize(idx);
                var sourceFile = arena.GetSourceFile(idx);
                var sourcePath = !string.IsNullOrWhiteSpace(sourceFile?.originalPath)
                    ? sourceFile.originalPath
                    : !string.IsNullOrWhiteSpace(sourceFile?.fullName)
                        ? sourceFile.fullName
                        : arena.GetOriginalPath(idx);
                var displayType = AssetListQuery.GetCachedDisplayType(type, AssetItem.ContainsFbxSegment(container));

                writer.WriteStartElement("Asset");
                writer.WriteElementString("Name", name ?? string.Empty);
                writer.WriteElementString("Container", container ?? string.Empty);
                writer.WriteStartElement("Type");
                writer.WriteAttributeString("id", ((int)type).ToString(CultureInfo.InvariantCulture));
                writer.WriteString(displayType ?? string.Empty);
                writer.WriteEndElement();
                writer.WriteElementString("PathID", pathId.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("Source", sourcePath ?? string.Empty);
                writer.WriteElementString("Size", byteSize.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
                writtenCount++;
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        return writtenCount;
    }

    internal static bool ArenaIndicesContainType(AssetIndexArena arena, IReadOnlyList<int> arenaIndices, ClassIDType targetType)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(arenaIndices);

        var arenaCount = arena.Count;
        for (var i = 0; i < arenaIndices.Count; i++)
        {
            var idx = arenaIndices[i];
            if ((uint)idx < (uint)arenaCount && arena.GetType(idx) == targetType)
            {
                return true;
            }
        }

        return false;
    }

    internal static List<AssetItem> MaterializeArenaItemsForExport(
        AssetIndexArena arena,
        IReadOnlyList<int> arenaIndices,
        ExportMode mode)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(arenaIndices);

        var arenaCount = arena.Count;
        var items = new List<AssetItem>(arenaIndices.Count);
        for (var i = 0; i < arenaIndices.Count; i++)
        {
            var idx = arenaIndices[i];
            if ((uint)idx >= (uint)arenaCount)
            {
                continue;
            }

            if (mode == ExportMode.Convert)
            {
                var type = arena.GetType(idx);
                if (type is ClassIDType.Material or ClassIDType.Shader
                    && AssetItem.ContainsFbxSegment(arena.GetContainer(idx)))
                {
                    continue;
                }
            }

            items.Add(new AssetItem(arena.MaterializeHandle(idx), idx));
        }

        return mode == ExportMode.Convert
            ? OrderConvertedAssetsForExport(items)
            : items;
    }

    private async Task ExportArenaAssetsAsync(int[] arenaIndices, ExportMode mode)
    {
        if (arenaIndices.Length == 0)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var arena = assetsManager.ProjectIndex.Arena;
        if (!assemblyLoader.Loaded && (mode == ExportMode.Convert || mode == ExportMode.Dump))
        {
            var hasMonoBehaviour = await Task.Run(() => ArenaIndicesContainType(arena, arenaIndices, ClassIDType.MonoBehaviour));
            if (hasMonoBehaviour)
            {
                await EnsureAssemblyLoaderReadyAsync();
            }
        }

        var exportFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (exportFolders == null || exportFolders.Count == 0) return;

        var savePath = exportFolders[0].Path.LocalPath;
        SaveExportFolder(savePath);

        int exported = 0;
        int skipped = 0;
        int failed = 0;
        var exportErrors = new List<string>();
        bool hadAnyItems = false;

        StatusStripUpdate($"Preparing {arenaIndices.Length:N0} assets for export...");

        await Task.Run(() =>
        {
            var toExport = MaterializeArenaItemsForExport(arena, arenaIndices, mode);
            if (toExport.Count == 0)
            {
                return;
            }

            hadAnyItems = true;
            StatusStripUpdate($"Exporting {toExport.Count:N0} assets...");

            var summary = ExportAssetsBatchCore(
                toExport,
                mode,
                savePath,
                progress => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => progressBar.Value = progress));

            exported = summary.Exported;
            skipped = summary.Skipped;
            failed = summary.Failed;
            exportErrors.AddRange(summary.Errors);
        });

        if (!hadAnyItems)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

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
            var filename = Path.Combine(savePath, "assets.xml");
            try
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    Indent = true,
                    CloseOutput = true
                };
                using (var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                using (var writer = XmlWriter.Create(output, settings))
                {
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
                }

                StatusStripUpdate($"Finished exporting asset list to XML with {toExport.Count} items.");
                if (exportOptions.OpenAfterExport)
                {
                    OpenFolder(savePath);
                }
            }
            catch (Exception ex)
            {
                TryDeletePartialExport(filename);
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
            await EnsureAssemblyLoaderReadyAsync();
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
            var summary = ExportAssetsBatchCore(
                toExport,
                mode,
                savePath,
                progress => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => progressBar.Value = progress));

            exported = summary.Exported;
            skipped = summary.Skipped;
            failed = summary.Failed;
            exportErrors.AddRange(summary.Errors);
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

    internal readonly record struct BulkExportSummary(
        int Exported,
        int Skipped,
        int Failed,
        int BatchCount,
        IReadOnlyList<string> Errors);

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
        ArgumentNullException.ThrowIfNull(toExport);
        Directory.CreateDirectory(savePath);

        int total = toExport.Count;
        if (total == 0)
        {
            return new BulkExportSummary(0, 0, 0, 0, Array.Empty<string>());
        }

        int exported = 0;
        int skipped = 0;
        int failed = 0;
        int completed = 0;
        var exportErrors = new ConcurrentQueue<string>();

        var currentExportPath = Path.Combine(savePath, "export-current.txt");
        var progressTracker = new ExportProgressTracker(
            currentExportPath,
            total,
            uiProgressCallback,
            DefaultCurrentExportWriteIntervalMs,
            DefaultExportUiProgressIntervalMs);

        var conversionContext = new ExportConversionContext(assetsManager.assetsFileList, assetsManager.loadLock);

        List<LazyExportSourceBatch> batches;
        HashSet<string>? initiallyLoadedFileNames = null;
        if (assetsManager.LazyLoading)
        {
            lock (assetsManager.loadLock)
            {
                initiallyLoadedFileNames = assetsManager.assetsFileList
                    .Select(f => f.fileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            batches = BuildLazyExportBatches(toExport, maxSourceFilesPerBatch, maxAssetBytesPerBatch);
        }
        else
        {
            batches = new List<LazyExportSourceBatch>(1)
            {
                new LazyExportSourceBatch(Array.Empty<string>(), toExport, 0L)
            };
        }

        int workerCount = GetExportThreadCount(maxDegreeOfParallelism);

        try
        {
            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[batchIndex];
                if (batch.Items.Count == 0)
                {
                    continue;
                }

                if (assetsManager.LazyLoading)
                {
                    EnsureLazyBatchLoadedForExport(batch, cancellationToken);
                    conversionContext.InvalidateDynamicCaches();
                }

                onBatchLoaded?.Invoke(batch, batchIndex);

                using var lruSuspension = assetsManager.LazyLoading
                    ? assetsManager.LruCache.SuspendEviction()
                    : null;

                var phases = BuildBatchExecutionPhases(batch.Items, mode);
                for (int phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
                {
                    var phaseItems = phases[phaseIndex];
                    if (phaseItems.Count == 0)
                    {
                        continue;
                    }

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(workerCount, phaseItems.Count),
                        CancellationToken = cancellationToken
                    };

                    Parallel.ForEach(phaseItems, parallelOptions, asset =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool forceFlushCurrent = false;
                        try
                        {
                            var exportPath = GetExportPath(savePath, asset);
                            Directory.CreateDirectory(exportPath);
                            var fileName = FixFileName(asset.Name);
                            bool itemExported = false;

                            switch (mode)
                            {
                                case ExportMode.Raw:
                                    if (conversionContext.TryGetAssetFilePath(exportPath, fileName, GetRawExtension(asset), asset, out var rawFilePath))
                                    {
                                        var assetObj = asset.Asset;
                                        if (assetObj != null)
                                        {
                                            WriteRawAssetData(assetObj, rawFilePath);
                                            itemExported = true;
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
                                                TypeTree? typeTree;
                                                lock (assemblyLoader)
                                                {
                                                    typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                                                }
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
                                        WriteTextExport(dumpFilePath, dump ?? "");
                                        itemExported = true;
                                    }
                                    break;
                                case ExportMode.Convert:
                                    if (ExportConvertFile(asset, exportPath, conversionContext))
                                    {
                                        itemExported = true;
                                    }
                                    break;
                            }

                            if (itemExported)
                            {
                                Interlocked.Increment(ref exported);
                            }
                            else
                            {
                                Interlocked.Increment(ref skipped);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            forceFlushCurrent = true;
                            Interlocked.Increment(ref failed);
                            var error = $"Failed to export {asset.TypeString}: {asset.Name} (PathID: {asset.PathID})";
                            exportErrors.Enqueue($"{error}{Environment.NewLine}{ex}");
                            StatusStripUpdate($"Error exporting {asset.Name}: {ex.Message}");
                        }

                        var currentCompleted = Interlocked.Increment(ref completed);
                        progressTracker.ReportAssetProgress(
                            asset,
                            currentCompleted,
                            forceFlush: forceFlushCurrent || currentCompleted == 1 || currentCompleted == total);
                    });
                }

                if (assetsManager.LazyLoading && batches.Count > 1)
                {
                    ReleaseLazyExportBatch(batch, initiallyLoadedFileNames);
                }
            }
        }
        finally
        {
            ClearCurrentExport(savePath);
        }

        return new BulkExportSummary(
            exported,
            skipped,
            failed,
            batches.Count,
            exportErrors.ToArray());
    }

    private static List<IReadOnlyList<AssetItem>> BuildBatchExecutionPhases(
        IReadOnlyList<AssetItem> batchItems,
        ExportMode mode)
    {
        if (mode != ExportMode.Convert || batchItems.Count <= 1)
        {
            return new List<IReadOnlyList<AssetItem>>(1) { batchItems };
        }

        var textures = new List<AssetItem>();
        var materials = new List<AssetItem>();
        var others = new List<AssetItem>();

        for (int i = 0; i < batchItems.Count; i++)
        {
            var item = batchItems[i];
            if (item.Type == ClassIDType.Texture2D)
            {
                textures.Add(item);
            }
            else if (item.Type == ClassIDType.Material)
            {
                materials.Add(item);
            }
            else
            {
                others.Add(item);
            }
        }

        var phases = new List<IReadOnlyList<AssetItem>>(3);
        if (textures.Count > 0) phases.Add(textures);
        if (materials.Count > 0) phases.Add(materials);
        if (others.Count > 0) phases.Add(others);
        return phases;
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
            var totalNodes = exportNodes.Count;
            var completedNodes = 0;
            var errorQueue = new ConcurrentQueue<string>();
            var reservedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var progressTracker = new ExportProgressTracker(
                currentExportPath,
                totalNodes,
                progress => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => progressBar.Value = progress),
                DefaultCurrentExportWriteIntervalMs,
                DefaultExportUiProgressIntervalMs);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(GetExportThreadCount(), totalNodes)
            };

            try
            {
                Parallel.For(0, totalNodes, parallelOptions, i =>
                {
                    var node = exportNodes[i];
                    var gameObject = node.GameObject!;
                    var targetFolder = createObjectFolders
                        ? ReserveUniqueDirectoryPath(Path.Combine(exportRoot, FixFileName(gameObject.m_Name)), reservedPaths)
                        : exportRoot;
                    Directory.CreateDirectory(targetFolder);

                    var exportFile = ReserveUniqueFilePath(
                        Path.Combine(targetFolder, FixFileName(gameObject.m_Name) + ".fbx"),
                        reservedPaths);
                    StatusStripUpdate($"Exporting {Path.GetFileName(exportFile)}");

                    bool forceFlushCurrent = false;
                    try
                    {
                        var convert = CreateModelConverter(gameObject, clips);
                        ExportFbx(convert, exportFile, reservedPaths);
                        Interlocked.Increment(ref exported);
                    }
                    catch (Exception ex)
                    {
                        forceFlushCurrent = true;
                        Interlocked.Increment(ref failed);
                        var error = $"Export GameObject:{gameObject.m_Name} error";
                        errorQueue.Enqueue($"{error}{Environment.NewLine}{ex}");
                        Logger.Error(error, ex);
                        StatusStripUpdate($"Error exporting {gameObject.m_Name}: {ex.Message}");
                    }

                    var currentCompleted = Interlocked.Increment(ref completedNodes);
                    var source = gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fullName ?? gameObject.assetsFile?.fileName;
                    progressTracker.ReportCustomProgress(
                        typeString: "GameObject",
                        name: gameObject.m_Name,
                        pathId: gameObject.m_PathID,
                        source: source,
                        completedCount: currentCompleted,
                        forceFlush: forceFlushCurrent || currentCompleted == 1 || currentCompleted == totalNodes);
                });
            }
            finally
            {
                ClearCurrentExport(exportRoot);
                exportErrors.AddRange(errorQueue);
            }
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

    internal static string ReserveUniqueDirectoryPath(
        string directoryPath,
        ConcurrentDictionary<string, byte> reservedPaths)
    {
        ArgumentNullException.ThrowIfNull(reservedPaths);
        var fullBasePath = Path.GetFullPath(directoryPath);
        var candidate = fullBasePath;
        for (var i = 1; ; i++)
        {
            if (reservedPaths.TryAdd(candidate, 0) && !Directory.Exists(candidate))
            {
                return candidate;
            }
            candidate = $"{fullBasePath} ({i})";
        }
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

    internal static string ReserveUniqueFilePath(
        string filePath,
        ConcurrentDictionary<string, byte> reservedPaths)
    {
        ArgumentNullException.ThrowIfNull(reservedPaths);
        var fullFilePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullFilePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(fullFilePath);
        var extension = Path.GetExtension(fullFilePath);

        var candidate = fullFilePath;
        for (var i = 1; ; i++)
        {
            if (reservedPaths.TryAdd(candidate, 0) && !File.Exists(candidate))
            {
                return candidate;
            }
            candidate = Path.Combine(directory, $"{name} ({i}){extension}");
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
            AssetGroupOption.SourceFile => GetSourceFileExportPath(savePath, asset),
            AssetGroupOption.TypeName => Path.Combine(savePath, FixFileName(asset.TypeString)),
            AssetGroupOption.None => savePath,
            _ => savePath
        };
    }

    private static string GetSourceFileExportPath(string savePath, AssetItem asset)
    {
        var originalPath = !string.IsNullOrWhiteSpace(asset.SourceFile?.originalPath)
            ? asset.SourceFile.originalPath
            : asset.Handle?.OriginalPath;
        var serializedFileName = !string.IsNullOrWhiteSpace(asset.SourceFile?.fileName)
            ? asset.SourceFile.fileName
            : asset.Handle?.SerializedFileName;

        if (!string.IsNullOrWhiteSpace(originalPath))
        {
            var containerFileName = Path.GetFileName(originalPath);
            if (!string.IsNullOrWhiteSpace(containerFileName)
                && !string.IsNullOrWhiteSpace(serializedFileName)
                && !string.Equals(containerFileName, serializedFileName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(
                    savePath,
                    FixFileName(containerFileName) + "_export",
                    FixFileName(serializedFileName));
            }

            if (!string.IsNullOrWhiteSpace(containerFileName))
            {
                return Path.Combine(savePath, FixFileName(containerFileName) + "_export");
            }
        }

        return Path.Combine(savePath, FixFileName(serializedFileName ?? "Unknown") + "_export");
    }

    internal static string GetSafeRelativeExportDirectory(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        var directory = Path.GetDirectoryName(container.Replace('\\', '/')) ?? string.Empty;
        var safeParts = directory
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.Trim() is not "." and not "..")
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
            TextAsset => GetTextAssetExtension(asset),
            Shader => ".shader",
            Font m_Font when m_Font.m_FontData?.Length >= 4 && m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79 => ".otf",
            Font => ".ttf",
            MovieTexture => ".ogv",
            VideoClip m_VideoClip when !string.IsNullOrEmpty(m_VideoClip.m_OriginalPath) => SanitizeExtension(Path.GetExtension(m_VideoClip.m_OriginalPath), ".mp4"),
            AudioClip m_AudioClip => new AudioClipConverter(m_AudioClip).GetExtensionName(),
            _ => ".dat"
        };
    }

    private string GetTextAssetExtension(AssetItem asset)
    {
        if (exportOptions.RestoreExtensionName && !string.IsNullOrEmpty(asset.Container))
        {
            return SanitizeExtension(Path.GetExtension(asset.Container), ".txt");
        }

        return ".txt";
    }

    private static string SanitizeExtension(string? extension, string fallback)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return fallback;
        }

        var raw = extension.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            if (character < 32 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '.')
            {
                continue;
            }

            builder.Append(character);
        }

        var sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback;
        }

        if (sanitized.Length > 32)
        {
            sanitized = sanitized.Substring(0, 32);
        }

        return "." + sanitized;
    }

    private static void WriteCurrentExport(string path, AssetItem asset, int index, int count)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path,
                FormatCurrentExportContent(
                    asset.TypeString,
                    asset.Name,
                    asset.PathID,
                    asset.SourceFile?.originalPath ?? asset.SourceFile?.fullName ?? asset.SourceFile?.fileName,
                    index,
                    count),
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    internal static string FormatCurrentExportContent(
        string? typeString,
        string? name,
        long pathId,
        string? source,
        int index,
        int count)
    {
        return $"Exporting {index}/{count}{Environment.NewLine}" +
               $"Type: {typeString}{Environment.NewLine}" +
               $"Name: {name}{Environment.NewLine}" +
               $"PathID: {pathId}{Environment.NewLine}" +
               $"Source: {source}{Environment.NewLine}";
    }

    internal sealed class ExportProgressTracker
    {
        private readonly string currentExportPath;
        private readonly int totalCount;
        private readonly Action<int>? uiProgressCallback;
        private readonly int fileWriteIntervalMs;
        private readonly int uiProgressIntervalMs;
        private readonly object fileWriteLock = new();

        private long lastFileWriteTicks;
        private long lastUiPostTicks;
        private int lastReportedPercent = -1;
        private int fileWriteCount;
        private int uiPostCount;

        public int FileWriteCount => Volatile.Read(ref fileWriteCount);
        public int UiPostCount => Volatile.Read(ref uiPostCount);
        public int LastReportedPercent => Volatile.Read(ref lastReportedPercent);

        public ExportProgressTracker(
            string currentExportPath,
            int totalCount,
            Action<int>? uiProgressCallback = null,
            int fileWriteIntervalMs = DefaultCurrentExportWriteIntervalMs,
            int uiProgressIntervalMs = DefaultExportUiProgressIntervalMs)
        {
            this.currentExportPath = currentExportPath ?? string.Empty;
            this.totalCount = Math.Max(1, totalCount);
            this.uiProgressCallback = uiProgressCallback;
            this.fileWriteIntervalMs = Math.Max(0, fileWriteIntervalMs);
            this.uiProgressIntervalMs = Math.Max(0, uiProgressIntervalMs);

            var initialTicks = Environment.TickCount64 - Math.Max(this.fileWriteIntervalMs, this.uiProgressIntervalMs) - 1;
            lastFileWriteTicks = initialTicks;
            lastUiPostTicks = initialTicks;

            if (!string.IsNullOrWhiteSpace(this.currentExportPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(this.currentExportPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch
                {
                }
            }
        }

        public void ReportAssetProgress(AssetItem asset, int completedCount, bool forceFlush = false)
        {
            var source = asset.SourceFile?.originalPath
                ?? asset.SourceFile?.fullName
                ?? asset.SourceFile?.fileName
                ?? asset.Handle?.OriginalPath;
            ReportCustomProgress(
                asset.TypeString,
                asset.Name,
                asset.PathID,
                source,
                completedCount,
                forceFlush);
        }

        public void ReportCustomProgress(
            string? typeString,
            string? name,
            long pathId,
            string? source,
            int completedCount,
            bool forceFlush = false)
        {
            bool isFinal = completedCount >= totalCount;
            bool shouldForceWrite = forceFlush || isFinal || completedCount <= 1;

            TryWriteCurrentExportThrottled(typeString, name, pathId, source, completedCount, shouldForceWrite);
            TryPostUiProgressThrottled(completedCount, isFinal);
        }

        private void TryWriteCurrentExportThrottled(
            string? typeString,
            string? name,
            long pathId,
            string? source,
            int completedCount,
            bool forceFlush)
        {
            if (string.IsNullOrWhiteSpace(currentExportPath))
            {
                return;
            }

            long nowTicks = Environment.TickCount64;
            if (!forceFlush)
            {
                long prevTicks = Interlocked.Read(ref lastFileWriteTicks);
                if (nowTicks - prevTicks < fileWriteIntervalMs)
                {
                    return;
                }
                if (Interlocked.CompareExchange(ref lastFileWriteTicks, nowTicks, prevTicks) != prevTicks)
                {
                    return;
                }
            }
            else
            {
                Interlocked.Exchange(ref lastFileWriteTicks, nowTicks);
            }

            lock (fileWriteLock)
            {
                try
                {
                    File.WriteAllText(
                        currentExportPath,
                        FormatCurrentExportContent(typeString, name, pathId, source, completedCount, totalCount),
                        Encoding.UTF8);
                    Interlocked.Increment(ref fileWriteCount);
                }
                catch
                {
                }
            }
        }

        private void TryPostUiProgressThrottled(int completedCount, bool isFinal)
        {
            if (uiProgressCallback == null)
            {
                return;
            }

            int percent = Math.Clamp((int)((double)completedCount / totalCount * 100), 0, 100);
            int prevPercent = Volatile.Read(ref lastReportedPercent);
            long nowTicks = Environment.TickCount64;
            long prevTicks = Interlocked.Read(ref lastUiPostTicks);

            bool percentChanged = percent > prevPercent;
            bool timeElapsed = nowTicks - prevTicks >= uiProgressIntervalMs;

            if (!isFinal && !percentChanged && !timeElapsed)
            {
                return;
            }

            if (percentChanged)
            {
                int currentPrev = prevPercent;
                while (percent > currentPrev)
                {
                    int exchanged = Interlocked.CompareExchange(ref lastReportedPercent, percent, currentPrev);
                    if (exchanged == currentPrev)
                    {
                        Interlocked.Exchange(ref lastUiPostTicks, nowTicks);
                        Interlocked.Increment(ref uiPostCount);
                        uiProgressCallback(percent);
                        return;
                    }
                    currentPrev = exchanged;
                }
                return;
            }

            if (isFinal || (timeElapsed && Interlocked.CompareExchange(ref lastUiPostTicks, nowTicks, prevTicks) == prevTicks))
            {
                if (isFinal && prevPercent == percent)
                {
                    return;
                }
                Interlocked.Exchange(ref lastReportedPercent, percent);
                Interlocked.Increment(ref uiPostCount);
                uiProgressCallback(percent);
            }
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

    internal static void TryDeletePartialExport(string filePath)
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

    private static void WriteBytesExport(string filePath, byte[] data)
    {
        try
        {
            using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            file.Write(data, 0, data.Length);
        }
        catch
        {
            TryDeletePartialExport(filePath);
            throw;
        }
    }

    private static void WriteTextExport(string filePath, string text)
    {
        try
        {
            using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(text);
        }
        catch
        {
            TryDeletePartialExport(filePath);
            throw;
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
                bool exported = false;
                if (TryFindModelGroupForAnimator(m_Animator, out var animatorGroup, out var animatorGroupMeshes)
                    && animatorGroup != null
                    && animatorGroupMeshes.Count > 0)
                {
                    var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(animatorGroup.GroupName) ? animatorGroup.GroupName : fileName);
                    if (conversionContext.TryMarkModelGroupExported(animatorGroup.GroupId)
                        && conversionContext.TryGetAssetFilePath(exportPath, groupFileName, ".fbx", item, out var groupFbxPath)
                        && ExportModelGroupFbx(animatorGroup, animatorGroupMeshes, groupFbxPath))
                    {
                        exported = true;
                    }
                }

                if (!exported)
                {
                    var convert = new ModelConverter(m_Animator, exportOptions.ConvertTextureFormat);
                    if (convert.MeshList.Count > 0
                        && conversionContext.TryGetAssetFilePath(exportPath, fileName, ".fbx", item, out var exportFullPath))
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
                        && conversionContext.TryGetAssetFilePath(exportPath, groupFileName, ".fbx", item, out var groupFbxPath)
                        && ExportModelGroupFbx(avatarGroup, avatarGroupMeshes, groupFbxPath))
                    {
                        exported = true;
                    }
                }
                return exported;
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
                var clipExportName = string.IsNullOrEmpty(m_AnimationClip.m_Name) ? fileName : m_AnimationClip.m_Name;
                return conversionContext.TryGetAssetFilePath(exportPath, clipExportName, ".anim", item, out var clipFullPath)
                    && AssetExportHelper.ExportAnimationClipToFile(
                        m_AnimationClip,
                        clipFullPath,
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
                    WriteBytesExport(rawPath, m_Texture2D.image_data.GetData());
                    AssetExportHelper.WriteTextureMetaIfMissing(rawPath, m_Texture2D);
                    return true;
                }

                using var image = m_Texture2D.ConvertToImage(true);
                if (image == null) return false;
                var extension = "." + exportOptions.ConvertTextureFormat.ToString().ToLowerInvariant();
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                try
                {
                    using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    image.WriteToStream(file, exportOptions.ConvertTextureFormat);
                }
                catch
                {
                    TryDeletePartialExport(filePath);
                    throw;
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
                    WriteBytesExport(filePath, buffer);
                }
                else
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, converter.GetExtensionName(), item, out var filePath)) return false;
                    WriteBytesExport(filePath, m_AudioData);
                }
                return true;
            }
            case Material m_Material:
            {
                var matExportName = string.IsNullOrEmpty(m_Material.m_Name) ? item.Name : m_Material.m_Name;
                return conversionContext.TryGetAssetFilePath(exportPath, matExportName, ".mat", item, out var matFullPath)
                    && AssetExportHelper.ExportMaterialToFile(m_Material, item.Name, exportPath, matFullPath, exportOptions.ConvertTextureFormat);
            }
            case TextAsset m_TextAsset:
            {
                var extension = GetTextAssetExtension(item);
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                WriteBytesExport(filePath, m_TextAsset.m_Script);
                return true;
            }
            case MonoScript m_MonoScript:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var filePath)) return false;
                var sb = new StringBuilder();
                sb.AppendLine($"Assembly: {m_MonoScript.m_AssemblyName}");
                sb.AppendLine($"Namespace: {m_MonoScript.m_Namespace}");
                sb.AppendLine($"Class: {m_MonoScript.m_ClassName}");
                WriteTextExport(filePath, sb.ToString());
                return true;
            }
            case Shader m_Shader:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".shader", item, out var filePath)) return false;
                var str = m_Shader.Convert();
                WriteTextExport(filePath, str);
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
                WriteBytesExport(filePath, m_Font.m_FontData);
                return true;
            }
            case Sprite m_Sprite:
            {
                using var image = m_Sprite.GetImage();
                if (image == null) return false;
                var format = exportOptions.ConvertTextureFormat;
                var extension = "." + format.ToString().ToLowerInvariant();
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                try
                {
                    using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    image.WriteToStream(file, format);
                }
                catch
                {
                    TryDeletePartialExport(filePath);
                    throw;
                }
                AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Sprite);
                return true;
            }
            case VideoClip m_VideoClip:
            {
                if (m_VideoClip.m_ExternalResources.m_Size <= 0) return false;
                var extension = SanitizeExtension(Path.GetExtension(m_VideoClip.m_OriginalPath), ".mp4");
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                try
                {
                    m_VideoClip.m_VideoData.WriteData(filePath);
                }
                catch
                {
                    TryDeletePartialExport(filePath);
                    throw;
                }
                return true;
            }
            case VideoPlayer m_VideoPlayer:
            {
                if (m_VideoPlayer.m_VideoClip.TryGet(out var resolvedClip) && resolvedClip != null)
                {
                    if (resolvedClip.m_ExternalResources.m_Size <= 0) return false;
                    var extension = SanitizeExtension(Path.GetExtension(resolvedClip.m_OriginalPath), ".mp4");
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                    try
                    {
                        resolvedClip.m_VideoData.WriteData(filePath);
                    }
                    catch
                    {
                        TryDeletePartialExport(filePath);
                        throw;
                    }
                    return true;
                }
                else if (m_VideoPlayer.m_Source == 1 && !string.IsNullOrEmpty(m_VideoPlayer.m_Url))
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName + "_url", ".txt", item, out var filePath)) return false;
                    WriteTextExport(filePath, m_VideoPlayer.m_Url);
                    return true;
                }
                return false;
            }
            case MovieTexture m_MovieTexture:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".ogv", item, out var filePath)) return false;
                WriteBytesExport(filePath, m_MovieTexture.m_MovieData);
                return true;
            }
            case MonoBehaviour m_MonoBehaviour:
            {
                TypeTree? convertedTypeTree = null;
                bool typeTreeConverted = false;
                TypeTree? GetOrConvertTypeTree()
                {
                    if (!typeTreeConverted && assemblyLoader.Loaded)
                    {
                        typeTreeConverted = true;
                        lock (assemblyLoader)
                        {
                            convertedTypeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                        }
                    }
                    return convertedTypeTree;
                }

                object? obj = m_MonoBehaviour.ToType();
                if (obj == null && assemblyLoader.Loaded)
                {
                    var typeTree = GetOrConvertTypeTree();
                    if (typeTree != null)
                    {
                        obj = m_MonoBehaviour.ToType(typeTree);
                    }
                }

                if (obj != null)
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".json", item, out var filePath)) return false;
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                    WriteTextExport(filePath, str);
                    return true;
                }

                // Fallback to text asset dump
                var dumpStr = m_MonoBehaviour.Dump();
                if (dumpStr == null && assemblyLoader.Loaded)
                {
                    var typeTree = GetOrConvertTypeTree();
                    if (typeTree != null)
                    {
                        dumpStr = m_MonoBehaviour.Dump(typeTree);
                    }
                }

                if (dumpStr != null)
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var dumpPath)) return false;
                    WriteTextExport(dumpPath, dumpStr);
                    return true;
                }

                return false;
            }
            case Object obj when obj.type == ClassIDType.PrefabInstance:
            {
                if (!conversionContext.TryGetAssetFilePath(exportPath, fileName + "_prefab_report", ".txt", item, out var filePath)) return false;
                var report = FormatPrefab(obj);
                WriteTextExport(filePath, report);
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

    internal static bool ShouldSkipConvertedAsset(AssetItem item)
    {
        if (!item.IsFbxSubAsset())
        {
            return false;
        }

        if (item.Type is ClassIDType.Material or ClassIDType.Shader)
        {
            return true;
        }

        return item.Handle == null && (item.Asset is Material || item.Asset is Shader);
    }

    private bool ExportMesh(
        AssetItem item,
        Mesh mesh,
        string exportPath,
        string fileName,
        ExportConversionContext conversionContext)
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

    internal async Task ExportSelectedModelGroupFbxAsync()
    {
        var selectedAssets = GetSelectedAssets();
        var primaryAsset = selectedAssets.FirstOrDefault(x => x.Type is ClassIDType.Mesh or ClassIDType.Animator or ClassIDType.Avatar);
        if (primaryAsset == null)
        {
            StatusStripUpdate("Select a Mesh, Animator, or Avatar to export its Model Group as FBX.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var suggestedName = FixFileName(GetExportFileName(primaryAsset)) + ".fbx";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(await CreateFbxSaveOptions("Export Model Group (FBX)", suggestedName));
        if (file == null) return;

        var exportFile = file.Path.LocalPath;
        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var clips = selectedAssets
            .Where(x => x.Type == ClassIDType.AnimationClip)
            .ToList();
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
            StatusStripUpdate($"Finished exporting Model Group FBX: {Path.GetFileName(exportFile)}");
            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            StatusStripUpdate(errorMessage != null
                ? $"Error exporting Model Group FBX: {errorMessage}"
                : $"No Model Group found for {primaryAsset.Name}.");
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
        ExportConversionContext conversionContext,
        out bool alreadyExportedGroup)
    {
        alreadyExportedGroup = false;

        // 1. SQLite semantic relation ModelGroup (Lazy mode & cached projects)
        if (TryFindModelGroupForMesh(mesh, out var group, out var groupMeshes)
            && group != null
            && groupMeshes.Count > 0)
        {
            if (!conversionContext.TryMarkModelGroupExported(group.GroupId))
            {
                alreadyExportedGroup = true;
                return false;
            }

            var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(group.GroupName) ? group.GroupName : fileName);
            if (conversionContext.TryGetAssetFilePath(exportPath, groupFileName, ".fbx", item, out var fbxPath)
                && ExportModelGroupFbx(group, groupMeshes, fbxPath))
            {
                return true;
            }

            conversionContext.UnmarkModelGroupExported(group.GroupId);
        }

        // 2. Eager mode scene-hierarchy ModelGroup discovery (multi-part characters/props & orphaned meshes with renderers)
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

            var groupFileName = FixFileName(!string.IsNullOrWhiteSpace(eagerName) ? eagerName : fileName);
            if (conversionContext.TryGetAssetFilePath(exportPath, groupFileName, ".fbx", item, out var fbxPath)
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

            // Prefer multi-part groups over single-part groups, then higher confidence, then tighter group
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

    internal sealed class EagerModelGroupEntry
    {
        public required string GroupName { get; init; }
        public GameObject? RootGameObject { get; init; }
        public required List<Mesh> Meshes { get; init; }
        public required List<Renderer> Renderers { get; init; }
        public Avatar? Avatar { get; init; }
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
        ExportConversionContext? conversionContext,
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
    {
        var result = new Dictionary<Mesh, EagerModelGroupEntry>(ReferenceEqualityComparer.Instance);
        if (filesSnapshot == null || filesSnapshot.Count == 0)
        {
            return result;
        }

        var ownerByMesh = new Dictionary<Mesh, (Renderer OwnerRenderer, GameObject OwnerGo)>(ReferenceEqualityComparer.Instance);
        for (int f = 0; f < filesSnapshot.Count; f++)
        {
            var objects = filesSnapshot[f].Objects;
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj is SkinnedMeshRenderer smr
                    && smr.m_Mesh != null
                    && smr.m_Mesh.TryGet(out var smrMesh)
                    && smrMesh != null
                    && !ownerByMesh.ContainsKey(smrMesh))
                {
                    GameObject? ownerGo = null;
                    smr.m_GameObject?.TryGet(out ownerGo);
                    if (ownerGo != null)
                    {
                        ownerByMesh[smrMesh] = (smr, ownerGo);
                    }
                }
                else if (obj is MeshFilter mf
                    && mf.m_Mesh != null
                    && mf.m_Mesh.TryGet(out var mfMesh)
                    && mfMesh != null
                    && !ownerByMesh.ContainsKey(mfMesh)
                    && mf.m_GameObject != null
                    && mf.m_GameObject.TryGet(out var mfGo)
                    && mfGo?.m_MeshRenderer != null)
                {
                    ownerByMesh[mfMesh] = (mfGo.m_MeshRenderer, mfGo);
                }
            }
        }

        if (ownerByMesh.Count == 0)
        {
            return result;
        }

        var subtreeCacheByRoot = new Dictionary<GameObject, (List<Mesh> Meshes, List<Renderer> Renderers)>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);

        foreach (var kvp in ownerByMesh)
        {
            var mesh = kvp.Key;
            var ownerRenderer = kvp.Value.OwnerRenderer;
            var ownerGo = kvp.Value.OwnerGo;

            GameObject? animatorRoot = null;
            GameObject? multiRendererRoot = null;
            Avatar? avatar = null;
            var current = ownerGo;
            visited.Clear();

            while (current != null && visited.Add(current))
            {
                if (current.m_Animator != null)
                {
                    animatorRoot = current;
                    if (current.m_Animator.m_Avatar != null)
                    {
                        current.m_Animator.m_Avatar.TryGet(out avatar);
                    }
                    break;
                }

                if (multiRendererRoot == null && CountRenderersInSubtree(current, maxCount: 2) >= 2)
                {
                    multiRendererRoot = current;
                }

                var parentTransform = current.m_Transform?.m_Father != null && current.m_Transform.m_Father.TryGet(out var father)
                    ? father
                    : null;
                if (parentTransform == null || parentTransform.m_GameObject == null || !parentTransform.m_GameObject.TryGet(out var parentGo))
                {
                    break;
                }
                current = parentGo;
            }

            var rootGameObject = animatorRoot ?? multiRendererRoot ?? ownerGo;
            var groupName = !string.IsNullOrWhiteSpace(rootGameObject.m_Name)
                ? rootGameObject.m_Name
                : (mesh.m_Name ?? "Model");

            if (!subtreeCacheByRoot.TryGetValue(rootGameObject, out var subtree))
            {
                var collectedMeshes = new List<Mesh>();
                var collectedRenderers = new List<Renderer>();
                CollectSubtreeMeshesAndRenderers(rootGameObject, collectedMeshes, collectedRenderers);
                subtree = (collectedMeshes, collectedRenderers);
                subtreeCacheByRoot[rootGameObject] = subtree;
            }

            List<Mesh> groupMeshes = subtree.Meshes;
            List<Renderer> groupRenderers = subtree.Renderers;
            if (groupMeshes.Count == 0 && ownerRenderer != null)
            {
                groupMeshes = new List<Mesh>(1) { mesh };
                groupRenderers = new List<Renderer>(1) { ownerRenderer };
            }

            if (groupMeshes.Count > 0)
            {
                result[mesh] = new EagerModelGroupEntry
                {
                    GroupName = groupName,
                    RootGameObject = rootGameObject,
                    Meshes = groupMeshes,
                    Renderers = groupRenderers,
                    Avatar = avatar
                };
            }
        }

        return result;
    }

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
        CollectSubtreeMeshesAndRenderers(rootGameObject, meshes, renderers);
        return meshes.Count > 0;
    }

    private static int CountRenderersInSubtree(GameObject root, int maxCount)
    {
        int count = 0;
        var visited = new HashSet<Transform>();
        var stack = new Stack<Transform>();
        if (root.m_Transform != null)
        {
            stack.Push(root.m_Transform);
        }
        else if (root.m_MeshRenderer != null || root.m_SkinnedMeshRenderer != null)
        {
            return 1;
        }

        while (stack.Count > 0 && count < maxCount)
        {
            var tr = stack.Pop();
            if (tr == null || !visited.Add(tr)) continue;
            if (tr.m_GameObject != null && tr.m_GameObject.TryGet(out var go) && go != null)
            {
                if (go.m_SkinnedMeshRenderer != null || (go.m_MeshRenderer != null && go.m_MeshFilter != null))
                {
                    count++;
                }
            }

            if (tr.m_Children != null)
            {
                for (int i = 0; i < tr.m_Children.Length; i++)
                {
                    if (tr.m_Children[i] != null && tr.m_Children[i].TryGet(out var child) && child != null)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        return count;
    }

    private static void CollectSubtreeMeshesAndRenderers(GameObject root, List<Mesh> meshes, List<Renderer> renderers)
    {
        var visited = new HashSet<Transform>();
        var stack = new Stack<Transform>();
        if (root.m_Transform != null)
        {
            stack.Push(root.m_Transform);
        }

        while (stack.Count > 0)
        {
            var tr = stack.Pop();
            if (tr == null || !visited.Add(tr)) continue;
            if (tr.m_GameObject != null && tr.m_GameObject.TryGet(out var go) && go != null)
            {
                if (go.m_SkinnedMeshRenderer != null
                    && go.m_SkinnedMeshRenderer.m_Mesh != null
                    && go.m_SkinnedMeshRenderer.m_Mesh.TryGet(out var smrMesh)
                    && smrMesh != null)
                {
                    meshes.Add(smrMesh);
                    renderers.Add(go.m_SkinnedMeshRenderer);
                }
                if (go.m_MeshRenderer != null
                    && go.m_MeshFilter != null
                    && go.m_MeshFilter.m_Mesh != null
                    && go.m_MeshFilter.m_Mesh.TryGet(out var mfMesh)
                    && mfMesh != null)
                {
                    meshes.Add(mfMesh);
                    renderers.Add(go.m_MeshRenderer);
                }
            }

            if (tr.m_Children != null)
            {
                for (int i = tr.m_Children.Length - 1; i >= 0; i--)
                {
                    if (tr.m_Children[i] != null && tr.m_Children[i].TryGet(out var child) && child != null)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }

    private static Mesh? FindFirstMeshUnderGameObject(GameObject root)
    {
        var meshes = new List<Mesh>();
        var renderers = new List<Renderer>();
        CollectSubtreeMeshesAndRenderers(root, meshes, renderers);
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
    {
        if (exportOptions.ExportAnimations && exportOptions.ExportAnimationsSplit && convert.AnimationList?.Count > 0)
        {
            var effectiveConvert = exportOptions.CombineMeshes && convert.MeshList?.Count > 1
                ? ModelConverter.CreateCombinedView(convert)
                : convert;

            // 1. Export main model without animations
            var mainConvert = new ImportedWrapper(effectiveConvert)
            {
                AnimationList = new List<ImportedKeyframedAnimation>()
            };
            try
            {
                ModelExporter.ExportFbx(exportFile, mainConvert,
                    exportOptions.ExportSkins,
                    false, // Disable animation for main export
                    exportOptions.ExportBlendShape,
                    exportOptions.CastToBone,
                    (float)exportOptions.BoneSize,
                    (float)exportOptions.ScaleFactor,
                    exportOptions.FbxVersion,
                    exportOptions.FbxFormat == 1,
                    exportOptions.CombineMeshes);
            }
            catch
            {
                TryDeletePartialExport(exportFile);
                throw;
            }

            if (exportOptions.ExportUnityMeta)
            {
                AssetExportHelper.WriteModelMetaIfMissing(exportFile);
            }

            // 2. Export each animation clip separately
            foreach (var anim in effectiveConvert.AnimationList)
            {
                var candidateAnimFile = Path.Combine(
                    Path.GetDirectoryName(exportFile)!,
                    $"{Path.GetFileNameWithoutExtension(exportFile)}_{FixFileName(anim.Name)}.fbx");
                var animFile = reservedPaths != null
                    ? ReserveUniqueFilePath(candidateAnimFile, reservedPaths)
                    : GetUniqueFilePath(candidateAnimFile);
                var animConvert = new ImportedWrapper(effectiveConvert)
                {
                    AnimationList = new List<ImportedKeyframedAnimation> { anim }
                };
                try
                {
                    ModelExporter.ExportFbx(animFile, animConvert,
                        exportOptions.ExportSkins,
                        true,
                        exportOptions.ExportBlendShape,
                        exportOptions.CastToBone,
                        (float)exportOptions.BoneSize,
                        (float)exportOptions.ScaleFactor,
                        exportOptions.FbxVersion,
                        exportOptions.FbxFormat == 1,
                        exportOptions.CombineMeshes);
                }
                catch
                {
                    TryDeletePartialExport(animFile);
                    throw;
                }

                if (exportOptions.ExportUnityMeta)
                {
                    AssetExportHelper.WriteAnimationMetaIfMissing(animFile);
                }

                if (exportOptions.ExportUnityAnimClips)
                {
                    var animClipPath = Path.Combine(
                        Path.GetDirectoryName(exportFile)!,
                        $"{Path.GetFileNameWithoutExtension(exportFile)}_{FixFileName(anim.Name)}.anim");
                    AssetExportHelper.ExportUnityAnimationClip(anim, animClipPath);
                }
            }
        }
        else
        {
            try
            {
                ModelExporter.ExportFbx(exportFile, convert,
                    exportOptions.ExportSkins,
                    exportOptions.ExportAnimations,
                    exportOptions.ExportBlendShape,
                    exportOptions.CastToBone,
                    (float)exportOptions.BoneSize,
                    (float)exportOptions.ScaleFactor,
                    exportOptions.FbxVersion,
                    exportOptions.FbxFormat == 1,
                    exportOptions.CombineMeshes);
            }
            catch
            {
                TryDeletePartialExport(exportFile);
                throw;
            }

            if (exportOptions.ExportUnityMeta)
            {
                AssetExportHelper.WriteModelMetaIfMissing(exportFile);
            }
        }
    }

    internal sealed class ExportConversionContext
    {
        private readonly List<SerializedFile> assetsFiles;
        private readonly object? filesLock;
        private readonly object cacheLock = new();
        private readonly ConcurrentDictionary<string, byte> reservedOutputPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> collisionCountersByStem = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> exportedModelGroupIds = new(StringComparer.Ordinal);
        private Dictionary<uint, string>? bonePathHash;
        private Dictionary<uint, string>? morphChannelNames;
        private Dictionary<Mesh, EagerModelGroupEntry>? eagerModelGroupsByMesh;

        public ExportConversionContext(List<SerializedFile> assetsFiles, object? filesLock = null)
        {
            this.assetsFiles = assetsFiles ?? new List<SerializedFile>();
            this.filesLock = filesLock;
        }

        public int ReservedPathCount => reservedOutputPaths.Count;

        public void InvalidateDynamicCaches()
        {
            lock (cacheLock)
            {
                bonePathHash = null;
                morphChannelNames = null;
                eagerModelGroupsByMesh = null;
            }
        }

        private List<SerializedFile> SnapshotAssetsFiles()
        {
            if (filesLock != null)
            {
                lock (filesLock)
                {
                    return assetsFiles.ToList();
                }
            }
            return assetsFiles.ToList();
        }

        public bool IsModelGroupExported(string? groupId)
        {
            return !string.IsNullOrWhiteSpace(groupId) && exportedModelGroupIds.ContainsKey(groupId);
        }

        public bool TryMarkModelGroupExported(string? groupId)
        {
            return string.IsNullOrWhiteSpace(groupId) || exportedModelGroupIds.TryAdd(groupId, 0);
        }

        public void UnmarkModelGroupExported(string? groupId)
        {
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                exportedModelGroupIds.TryRemove(groupId, out _);
            }
        }

        public Dictionary<uint, string> BonePathHash
        {
            get
            {
                var cached = Volatile.Read(ref bonePathHash);
                if (cached != null)
                {
                    return cached;
                }
                lock (cacheLock)
                {
                    return bonePathHash ??= AssetExportHelper.BuildBonePathHash(SnapshotAssetsFiles());
                }
            }
        }

        public Dictionary<uint, string> MorphChannelNames
        {
            get
            {
                var cached = Volatile.Read(ref morphChannelNames);
                if (cached != null)
                {
                    return cached;
                }
                lock (cacheLock)
                {
                    return morphChannelNames ??= AssetExportHelper.BuildMorphChannelNames(SnapshotAssetsFiles());
                }
            }
        }

        public Dictionary<Mesh, EagerModelGroupEntry> EagerModelGroupsByMesh
        {
            get
            {
                var cached = Volatile.Read(ref eagerModelGroupsByMesh);
                if (cached != null)
                {
                    return cached;
                }
                lock (cacheLock)
                {
                    return eagerModelGroupsByMesh ??= BuildEagerModelGroupLookup(SnapshotAssetsFiles());
                }
            }
        }

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
            if (reservedOutputPaths.TryAdd(normalizedPath, 0))
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
            var truncatedBase = fileName.Length > maxBaseLength
                ? fileName.Substring(0, maxBaseLength)
                : fileName;

            var fullDirectory = Path.GetFullPath(directory);
            filePath = Path.Combine(fullDirectory, truncatedBase + suffix + extension);
            if (reservedOutputPaths.TryAdd(filePath, 0))
            {
                return !File.Exists(filePath);
            }

            var stemKey = Path.Combine(fullDirectory, fileName + suffix + extension);
            while (true)
            {
                var collisionIndex = collisionCountersByStem.AddOrUpdate(stemKey, 1, static (_, prev) => prev + 1);
                var indexSuffix = $"{suffix}_{collisionIndex.ToString(CultureInfo.InvariantCulture)}";
                var indexedMaxBase = Math.Max(1, 120 - indexSuffix.Length);
                var indexedBase = fileName.Length > indexedMaxBase
                    ? fileName.Substring(0, indexedMaxBase)
                    : fileName;
                var candidatePath = Path.Combine(fullDirectory, indexedBase + indexSuffix + extension);
                if (reservedOutputPaths.TryAdd(candidatePath, 0) && !File.Exists(candidatePath))
                {
                    filePath = candidatePath;
                    return true;
                }
            }
        }
    }

    private static string CleanFloat(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? "0"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    internal static string FixFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(character < 32
                || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                || Array.IndexOf(invalidChars, character) >= 0
                ? '_'
                : character);
        }

        name = builder.ToString().Trim().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        while (name.Contains("..", StringComparison.Ordinal))
        {
            name = name.Replace("..", "_", StringComparison.Ordinal);
        }

        name = name.Trim().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        if (name.Length > 120)
        {
            name = name.Substring(0, 120).TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unnamed";
            }
        }

        var dotIndex = name.IndexOf('.');
        var deviceName = (dotIndex >= 0 ? name.Substring(0, dotIndex) : name).Trim().ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
        {
            name = "_" + name;
            if (name.Length > 120)
            {
                name = name.Substring(0, 120).TrimEnd(' ', '.');
            }
        }

        return name;
    }
}
