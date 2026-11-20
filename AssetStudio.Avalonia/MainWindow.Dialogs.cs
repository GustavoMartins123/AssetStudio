using AssetStudio;
using AssetStudio.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private Task<IReadOnlyList<string>> PickOpenFilesDialogAsync(string title, bool allowMultiple)
    {
        return ViewModel.DialogService.PickOpenFilesAsync(new FilePickerRequest(
            title: title,
            allowMultiple: allowMultiple,
            suggestedStartDirectory: appSettings.LoadFolderPath));
    }

    private Task<string?> PickLoadFolderDialogAsync(string title)
    {
        return ViewModel.DialogService.PickOpenFolderAsync(new FolderPickerRequest(
            title: title,
            allowMultiple: false,
            suggestedStartDirectory: appSettings.LoadFolderPath));
    }

    private Task<string?> PickExportFolderDialogAsync(string title)
    {
        return ViewModel.DialogService.PickOpenFolderAsync(new FolderPickerRequest(
            title: title,
            allowMultiple: false,
            suggestedStartDirectory: appSettings.ExportFolderPath));
    }

    private Task<string?> PickSaveFbxDialogAsync(string title, string suggestedFileName)
    {
        return ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: title,
            suggestedFileName: suggestedFileName,
            defaultExtension: "fbx",
            suggestedStartDirectory: appSettings.ExportFolderPath,
            fileTypeFilters: new[]
            {
                new FileTypeFilterSpec("Fbx file", "*.fbx")
            }));
    }

    private void SaveLoadFolder(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        appSettings.LoadFolderPath = folder;
        SaveAppSettings();
    }

    private void SaveExportFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        appSettings.ExportFolderPath = path;
        SaveAppSettings();
    }

    private async Task<RiskyLoadChoice> ConfirmFolderLoadIfRisky(string folderPath)
    {
        StatusStripUpdate("Scanning folder...");
        ProjectScanResult scanResult;
        using var scanCts = new CancellationTokenSource();
        var scanProgress = new Progress<ScanProgress>(p =>
        {
            if (p.TotalFiles > 0)
            {
                StatusStripUpdate($"Scanning folder... {p.ScannedFiles:N0}/{p.TotalFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
            else
            {
                StatusStripUpdate($"Scanning folder... {p.ScannedFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
        });
        try
        {
            scanResult = await Task.Run(() => ProjectScanner.ScanFolder(folderPath, scanCts.Token, scanProgress));
        }
        catch (OperationCanceledException)
        {
            StatusStripUpdate("Folder scan cancelled.");
            return RiskyLoadChoice.Cancel;
        }
        catch (Exception ex)
        {
            await ViewModel.DialogService.ShowErrorAsync("Folder scan failed", $"Unable to scan folder before loading:\n{ex.Message}");
            return RiskyLoadChoice.EagerLoad;
        }

        StatusStripUpdate($"Scan complete: {scanResult.TotalFiles:N0} files, {FormatBytes(scanResult.TotalBytes)}, {scanResult.UnityBundleCount:N0} bundles.");
        currentScanResult = scanResult;

        if (!scanResult.IsRisky)
        {
            return RiskyLoadChoice.EagerLoad;
        }

        var message = BuildRiskyProjectMessage(scanResult);
        return await ShowRiskyProjectDialog(message);
    }

    private static string BuildRiskyProjectMessage(ProjectScanResult scanResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This folder contains a very large number of Unity bundles.");
        sb.AppendLine();
        sb.AppendLine($"Files: {scanResult.TotalFiles:N0}");
        sb.AppendLine($"Size on disk: {FormatBytes(scanResult.TotalBytes)}");
        sb.AppendLine($"Unity bundles: {scanResult.UnityBundleCount:N0}");
        sb.AppendLine($"Serialized files: {scanResult.SerializedFileCount:N0}");
        sb.AppendLine($"Resource files: {scanResult.ResourceFileCount:N0}");
        if (scanResult.ErrorCount > 0)
        {
            sb.AppendLine($"Scan errors: {scanResult.ErrorCount:N0}");
        }
        sb.AppendLine();
        sb.AppendLine($"Estimated RAM to load: {FormatBytes(scanResult.EstimatedMemoryBytes)}");
        if (scanResult.AvailableMemoryBytes > 0)
        {
            sb.AppendLine($"Available RAM: {FormatBytes(scanResult.AvailableMemoryBytes)}");
        }
        if (scanResult.IsMemoryRisky)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ The estimated memory exceeds available RAM. Loading may freeze the system or trigger the OOM killer.");
        }
        sb.AppendLine();
        sb.AppendLine("Loading all bundles at once can use far more memory than the project size on disk and may push Linux into swap.");
        sb.AppendLine("The safer alternative is Safe/Lazy Mode, which index-scans all files and only materializes assets on demand.");
        return sb.ToString();
    }

    private Task<RiskyLoadChoice> ShowRiskyProjectDialog(string message)
    {
        return ViewModel.DialogService.ShowRiskyLoadDialogAsync("Large Unity project detected", message);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private Task<MemoryPressureResult> ShowMemoryPressureWarningDialog(string message)
    {
        return ViewModel.DialogService.ShowMemoryPressureDialogAsync("Memory pressure warning", message);
    }

    private void ShowMemoryPressureError(MemoryPressureException ex)
    {
        var msg = $"Loading was stopped because system memory usage reached {ex.MemoryLoadPercent}% (limit: {ex.LimitPercent}%).\n\n" +
                  $"Operation: {ex.Operation}\n\n" +
                  "Options:\n" +
                  "• Load fewer bundles at a time\n" +
                  "• Close other applications to free RAM\n" +
                  "• Raise the limit with ASSETSTUDIO_MEMORY_LIMIT_PERCENT (current: " + ex.LimitPercent + ")";
        StatusStripUpdate($"Loading stopped: memory pressure at {ex.MemoryLoadPercent}%.");
        _ = ViewModel.DialogService.ShowErrorAsync("Memory pressure — loading stopped", msg);
    }
}
