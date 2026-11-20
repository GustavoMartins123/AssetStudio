using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private const int ExtractionProgressThrottleMilliseconds = 50;

    internal static int GetExtractionThreadCount(int requestedDegreeOfParallelism = 0)
    {
        if (requestedDegreeOfParallelism > 0)
        {
            return requestedDegreeOfParallelism;
        }

        return Math.Clamp(
            AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_EXTRACT_THREADS", 1.0),
            1,
            16);
    }

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
        StatusStripUpdate("Extracting files...");
        try
        {
            var extractedCount = await Task.Run(() => ExtractFiles(filePaths, savePath));
            StatusStripUpdate($"Finished extracting {extractedCount} files.");
        }
        catch (Exception ex)
        {
            Logger.Error("Error extracting files", ex);
            StatusStripUpdate($"Error extracting files: {ex.Message}");
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
        StatusStripUpdate("Extracting folder...");
        try
        {
            var extractedCount = await Task.Run(() => ExtractFolder(sourcePath, savePath));
            StatusStripUpdate($"Finished extracting {extractedCount} files.");
        }
        catch (Exception ex)
        {
            Logger.Error("Error extracting folder", ex);
            StatusStripUpdate($"Error extracting folder: {ex.Message}");
        }
    }

    internal int ExtractFolder(string path, string savePath, CancellationToken cancellationToken = default)
    {
        return ExtractFolderCore(
            path,
            savePath,
            StatusStripUpdate,
            Progress.Report,
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
        var sourceRoot = Path.GetFullPath(path);
        var saveRoot = Path.GetFullPath(savePath);
        var saveRootWithSeparator = saveRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var files = ImportHelper.GetFilesSafe(sourceRoot, "*.*", true);
        var total = files.Length;
        Progress.Reset();
        if (total == 0)
        {
            return 0;
        }

        var extractedCount = 0;
        var completedCount = 0;
        var lastReportedPercent = -1;
        var lastProgressTicks = 0L;
        var reservedOutputPaths = new ConcurrentDictionary<string, byte>(pathComparer);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(GetExtractionThreadCount(maxDegreeOfParallelism), total),
            CancellationToken = cancellationToken
        };

        Parallel.For(0, total, parallelOptions, i =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            var fileDirectory = Path.GetDirectoryName(file) ?? sourceRoot;
            var relativeDirectory = Path.GetRelativePath(sourceRoot, fileDirectory);
            var fileSavePath = string.IsNullOrEmpty(relativeDirectory) || relativeDirectory == "."
                ? saveRoot
                : Path.GetFullPath(Path.Combine(saveRoot, relativeDirectory));

            if (!string.Equals(fileSavePath, saveRoot, comparison)
                && !fileSavePath.StartsWith(saveRootWithSeparator, comparison))
            {
                Logger.Warning($"Blocked folder extraction target outside destination root: '{file}'");
                var blockedCompleted = Interlocked.Increment(ref completedCount);
                ReportThrottledExtractionProgress(
                    blockedCompleted,
                    total,
                    ref lastReportedPercent,
                    ref lastProgressTicks,
                    progressReport);
                return;
            }

            var count = ExtractFileCore(file, fileSavePath, statusUpdate, reservedOutputPaths);
            if (count > 0)
            {
                Interlocked.Add(ref extractedCount, count);
            }

            var completed = Interlocked.Increment(ref completedCount);
            ReportThrottledExtractionProgress(
                completed,
                total,
                ref lastReportedPercent,
                ref lastProgressTicks,
                progressReport);
        });

        return extractedCount;
    }

    internal int ExtractFiles(string[] fileNames, string savePath, CancellationToken cancellationToken = default)
    {
        return ExtractFilesCore(
            fileNames,
            savePath,
            StatusStripUpdate,
            Progress.Report,
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
        ArgumentNullException.ThrowIfNull(fileNames);
        var total = fileNames.Length;
        Progress.Reset();
        if (total == 0)
        {
            return 0;
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var reservedOutputPaths = new ConcurrentDictionary<string, byte>(pathComparer);
        var extractedCount = 0;
        var completedCount = 0;
        var lastReportedPercent = -1;
        var lastProgressTicks = 0L;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(GetExtractionThreadCount(maxDegreeOfParallelism), total),
            CancellationToken = cancellationToken
        };

        Parallel.For(0, total, parallelOptions, i =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = ExtractFileCore(fileNames[i], savePath, statusUpdate, reservedOutputPaths);
            if (count > 0)
            {
                Interlocked.Add(ref extractedCount, count);
            }

            var completed = Interlocked.Increment(ref completedCount);
            ReportThrottledExtractionProgress(
                completed,
                total,
                ref lastReportedPercent,
                ref lastProgressTicks,
                progressReport);
        });

        return extractedCount;
    }

    private static void ReportThrottledExtractionProgress(
        int completed,
        int total,
        ref int lastReportedPercent,
        ref long lastProgressTicks,
        Action<int, int>? progressReport)
    {
        if (progressReport == null || total <= 0)
        {
            return;
        }

        var percent = Math.Clamp((int)((double)completed / total * 100), 0, 100);
        var nowTicks = Environment.TickCount64;
        var prevPercent = Volatile.Read(ref lastReportedPercent);
        var prevTicks = Interlocked.Read(ref lastProgressTicks);

        var isFinal = completed >= total;
        var percentChanged = percent > prevPercent;
        var timeElapsed = nowTicks - prevTicks >= ExtractionProgressThrottleMilliseconds;

        if (!isFinal && !percentChanged && !timeElapsed)
        {
            return;
        }

        if (percentChanged)
        {
            Interlocked.CompareExchange(ref lastReportedPercent, percent, prevPercent);
        }

        if (isFinal || timeElapsed || percentChanged)
        {
            if (isFinal || Interlocked.CompareExchange(ref lastProgressTicks, nowTicks, prevTicks) == prevTicks)
            {
                progressReport(completed, total);
            }
        }
    }

    private int ExtractFile(string fileName, string savePath)
    {
        return ExtractFileCore(fileName, savePath, StatusStripUpdate, reservedOutputPaths: null);
    }

    private static int ExtractFileCore(
        string fileName,
        string savePath,
        Action<string>? statusUpdate,
        ConcurrentDictionary<string, byte>? reservedOutputPaths)
    {
        using var reader = new FileReader(fileName);
        return reader.FileType switch
        {
            FileType.BundleFile => ExtractBundleFileCore(reader, savePath, statusUpdate, reservedOutputPaths),
            FileType.WebFile => ExtractWebDataFileCore(reader, savePath, statusUpdate, reservedOutputPaths),
            _ => 0
        };
    }

    private static int ExtractBundleFileCore(
        FileReader reader,
        string savePath,
        Action<string>? statusUpdate,
        ConcurrentDictionary<string, byte>? reservedOutputPaths)
    {
        statusUpdate?.Invoke($"Decompressing {reader.FileName} ...");
        var fileName = FixFileName(reader.FileName);
        using var bundleFile = new BundleFile(reader);
        if (bundleFile.fileList.Length == 0) return 0;

        var extractPath = Path.Combine(savePath, fileName + "_unpacked");
        return ExtractStreamFiles(extractPath, bundleFile.fileList, reservedOutputPaths);
    }

    private static int ExtractWebDataFileCore(
        FileReader reader,
        string savePath,
        Action<string>? statusUpdate,
        ConcurrentDictionary<string, byte>? reservedOutputPaths)
    {
        statusUpdate?.Invoke($"Decompressing {reader.FileName} ...");
        var fileName = FixFileName(reader.FileName);
        var webFile = new WebFile(reader);
        if (webFile.fileList.Length == 0) return 0;

        var extractPath = Path.Combine(savePath, fileName + "_unpacked");
        return ExtractStreamFiles(extractPath, webFile.fileList, reservedOutputPaths);
    }

    internal static int ExtractStreamFiles(
        string extractPath,
        StreamFile[] fileList,
        ConcurrentDictionary<string, byte>? reservedOutputPaths = null)
    {
        var extractedCount = 0;
        var rootFullPath = Path.GetFullPath(extractPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var file in fileList)
        {
            try
            {
                var rawPath = file.path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawPath) || file.stream == null)
                {
                    continue;
                }

                if (Path.IsPathRooted(rawPath))
                {
                    Logger.Warning($"Blocked rooted path in bundle entry: '{rawPath}'");
                    continue;
                }

                var rawSegments = rawPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (rawSegments.Length == 0 || rawSegments.Any(part => part.Trim() is "." or ".."))
                {
                    Logger.Warning($"Blocked path traversal attempt in bundle entry: '{rawPath}'");
                    continue;
                }

                var safeSegments = rawSegments
                    .Select(FixFileName)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();
                if (safeSegments.Length == 0)
                {
                    continue;
                }

                var filePath = Path.GetFullPath(Path.Combine(rootFullPath, Path.Combine(safeSegments)));
                if (!filePath.StartsWith(rootFullPath, comparison))
                {
                    Logger.Warning($"Blocked path traversal attempt in bundle entry: '{rawPath}'");
                    continue;
                }

                if (reservedOutputPaths != null && !reservedOutputPaths.TryAdd(filePath, 0))
                {
                    continue;
                }

                var fileDirectory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                if (!File.Exists(filePath))
                {
                    var createdFile = false;
                    try
                    {
                        using var fileStream = new FileStream(
                            filePath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            FileOptions.SequentialScan);
                        createdFile = true;
                        file.stream.CopyTo(fileStream);
                        extractedCount++;
                    }
                    catch (IOException) when (!createdFile && File.Exists(filePath))
                    {
                        // Another worker created the destination file concurrently; preserve existing file.
                    }
                    catch
                    {
                        if (createdFile)
                        {
                            TryDeletePartialExport(filePath);
                        }
                        throw;
                    }
                }
            }
            finally
            {
                file.stream?.Dispose();
            }
        }
        return extractedCount;
    }
}