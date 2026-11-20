using Avalonia.Controls;
using Avalonia.Interactivity;
using AssetStudio;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
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

    private int ExtractFolder(string path, string savePath)
    {
        var sourceRoot = Path.GetFullPath(path);
        var saveRoot = Path.GetFullPath(savePath);
        var saveRootWithSeparator = saveRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var files = ImportHelper.GetFilesSafe(sourceRoot, "*.*", true);
        var extractedCount = 0;
        Progress.Reset();

        for (var i = 0; i < files.Length; i++)
        {
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
                Progress.Report(i + 1, files.Length);
                continue;
            }

            extractedCount += ExtractFile(file, fileSavePath);
            Progress.Report(i + 1, files.Length);
        }

        return extractedCount;
    }

    private int ExtractFiles(string[] fileNames, string savePath)
    {
        var extractedCount = 0;
        Progress.Reset();

        for (var i = 0; i < fileNames.Length; i++)
        {
            extractedCount += ExtractFile(fileNames[i], savePath);
            Progress.Report(i + 1, fileNames.Length);
        }

        return extractedCount;
    }

    private int ExtractFile(string fileName, string savePath)
    {
        using var reader = new FileReader(fileName);
        return reader.FileType switch
        {
            FileType.BundleFile => ExtractBundleFile(reader, savePath),
            FileType.WebFile => ExtractWebDataFile(reader, savePath),
            _ => 0
        };
    }

    private int ExtractBundleFile(FileReader reader, string savePath)
    {
        StatusStripUpdate($"Decompressing {reader.FileName} ...");
        var fileName = FixFileName(reader.FileName);
        using var bundleFile = new BundleFile(reader);
        if (bundleFile.fileList.Length == 0) return 0;

        var extractPath = Path.Combine(savePath, fileName + "_unpacked");
        return ExtractStreamFiles(extractPath, bundleFile.fileList);
    }

    private int ExtractWebDataFile(FileReader reader, string savePath)
    {
        StatusStripUpdate($"Decompressing {reader.FileName} ...");
        var fileName = FixFileName(reader.FileName);
        var webFile = new WebFile(reader);
        if (webFile.fileList.Length == 0) return 0;

        var extractPath = Path.Combine(savePath, fileName + "_unpacked");
        return ExtractStreamFiles(extractPath, webFile.fileList);
    }

    private static int ExtractStreamFiles(string extractPath, StreamFile[] fileList)
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
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                if (Path.IsPathRooted(rawPath))
                {
                    Logger.Warning($"Blocked rooted path in bundle entry: '{rawPath}'");
                    continue;
                }

                var rawSegments = rawPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (rawSegments.Length == 0 || rawSegments.Any(part => part == "." || part == ".."))
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

                var fileDirectory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                if (!File.Exists(filePath))
                {
                    try
                    {
                        using var fileStream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        file.stream.CopyTo(fileStream);
                        extractedCount++;
                    }
                    catch
                    {
                        TryDeletePartialExport(filePath);
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