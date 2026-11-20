using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal static int GetExtractionThreadCount(int requestedDegreeOfParallelism = 0)
        => AssetExportService.GetExtractionThreadCount(requestedDegreeOfParallelism);

    private async void ExtractFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(await CreateOpenFileOptions("Select bundle or web file", true));
        if (files == null || files.Count == 0) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var filePaths = files.Select(x => x.Path.LocalPath).Where(File.Exists).ToArray();
        SaveLoadFolder(filePaths.FirstOrDefault() ?? string.Empty);
        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);

        var cancellationToken = ExportState.BeginExport(
            ExportOperationMode.ExtractBundle,
            filePaths.Length,
            savePath,
            "Extracting files...");
        StatusStripUpdate("Extracting files...");
        try
        {
            var extractedCount = await Task.Run(() => ExtractFiles(filePaths, savePath, cancellationToken));
            var status = $"Finished extracting {extractedCount} files.";
            ExportState.Complete(extractedCount, statusText: status);
            StatusStripUpdate(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ExportState.Complete(0, statusText: "Extraction cancelled.", wasCancelled: true);
            StatusStripUpdate("Extraction cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error("Error extracting files", ex);
            var errorStatus = $"Error extracting files: {ex.Message}";
            ExportState.Fail(errorStatus);
            StatusStripUpdate(errorStatus);
        }
    }

    private async void ExtractFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var sourceFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select folder to extract"));
        if (sourceFolders == null || sourceFolders.Count == 0) return;

        var saveFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (saveFolders == null || saveFolders.Count == 0) return;

        var sourcePath = sourceFolders[0].Path.LocalPath;
        SaveLoadFolder(sourcePath);
        var savePath = saveFolders[0].Path.LocalPath;
        SaveExportFolder(savePath);

        var cancellationToken = ExportState.BeginExport(
            ExportOperationMode.ExtractBundle,
            totalCount: 0,
            targetFolder: savePath,
            initialStatusText: "Extracting folder...");
        StatusStripUpdate("Extracting folder...");
        try
        {
            var extractedCount = await Task.Run(() => ExtractFolder(sourcePath, savePath, cancellationToken));
            var status = $"Finished extracting {extractedCount} files.";
            ExportState.Complete(extractedCount, statusText: status);
            StatusStripUpdate(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ExportState.Complete(0, statusText: "Folder extraction cancelled.", wasCancelled: true);
            StatusStripUpdate("Folder extraction cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error("Error extracting folder", ex);
            var errorStatus = $"Error extracting folder: {ex.Message}";
            ExportState.Fail(errorStatus);
            StatusStripUpdate(errorStatus);
        }
    }

    internal int ExtractFolder(string path, string savePath, CancellationToken cancellationToken = default)
    {
        Progress.Reset();
        return AssetExportService.ExtractFolder(
            path,
            savePath,
            StatusStripUpdate,
            Progress.Report,
            viewModel: ExportState,
            maxDegreeOfParallelism: 0,
            cancellationToken: cancellationToken);
    }

    internal static int ExtractFolderCore(
        string path,
        string savePath,
        Action<string>? statusUpdate = null,
        Action<int, int>? progressReport = null,
        int maxDegreeOfParallelism = 0,
        CancellationToken cancellationToken = default)
    {
        Progress.Reset();
        return AssetExportService.ExtractFolder(
            path,
            savePath,
            statusUpdate,
            progressReport,
            viewModel: null,
            maxDegreeOfParallelism: maxDegreeOfParallelism,
            cancellationToken: cancellationToken);
    }

    internal int ExtractFiles(string[] fileNames, string savePath, CancellationToken cancellationToken = default)
    {
        Progress.Reset();
        return AssetExportService.ExtractFiles(
            fileNames,
            savePath,
            StatusStripUpdate,
            Progress.Report,
            viewModel: ExportState,
            maxDegreeOfParallelism: 0,
            cancellationToken: cancellationToken);
    }

    internal static int ExtractFilesCore(
        string[] fileNames,
        string savePath,
        Action<string>? statusUpdate = null,
        Action<int, int>? progressReport = null,
        int maxDegreeOfParallelism = 0,
        CancellationToken cancellationToken = default)
    {
        Progress.Reset();
        return AssetExportService.ExtractFiles(
            fileNames,
            savePath,
            statusUpdate,
            progressReport,
            viewModel: null,
            maxDegreeOfParallelism: maxDegreeOfParallelism,
            cancellationToken: cancellationToken);
    }

    private int ExtractFile(string fileName, string savePath)
        => AssetExportService.ExtractSingleFile(fileName, savePath, StatusStripUpdate, reservedOutputPaths: null);

    internal static int ExtractStreamFiles(
        string extractPath,
        StreamFile[] fileList,
        ConcurrentDictionary<string, byte>? reservedOutputPaths = null)
        => AssetExportService.ExtractStreamFiles(extractPath, fileList, reservedOutputPaths);
}
