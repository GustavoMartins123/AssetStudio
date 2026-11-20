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

    private async Task ExtractFileCommandHandlerAsync()
    {
        var files = await PickOpenFilesDialogAsync("Select bundle or web file", true);
        if (files.Count == 0) return;

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        var filePaths = files.Where(File.Exists).ToArray();
        SaveLoadFolder(filePaths.FirstOrDefault() ?? string.Empty);
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

    private async Task ExtractFolderCommandHandlerAsync()
    {
        var sourcePath = await PickLoadFolderDialogAsync("Select folder to extract");
        if (string.IsNullOrWhiteSpace(sourcePath)) return;

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveLoadFolder(sourcePath);
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
