using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia.Services;

public sealed class UnityPackageExportOptions
{
    public required string SourceProjectOrAssetsDirectory { get; init; }
    public required string OutputPackagePath { get; init; }
    public bool IncludeScripts { get; init; } = true;
    public bool OverwriteExisting { get; init; } = true;
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;
}

public sealed class UnityPackageExportResult
{
    public bool Success { get; init; }
    public string OutputPackagePath { get; init; } = string.Empty;
    public int TotalEntriesPacked { get; init; }
    public int AssetEntriesPacked { get; init; }
    public int FolderEntriesPacked { get; init; }
    public int SkippedEntriesCount { get; init; }
    public long TotalBytesWritten { get; init; }
    public long ElapsedMs { get; init; }
    public List<string> Warnings { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

public sealed class UnityPackageInspectionResult
{
    public bool IsValid { get; init; }
    public int AssetCount { get; init; }
    public int FolderCount { get; init; }
    public int DuplicateGuidCount { get; init; }
    public int InvalidEntryCount { get; init; }
    public List<string> Pathnames { get; init; } = new();
    public List<string> Guids { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Packs a Unity project's Assets/ tree into a standard Unity .unitypackage (Tar + GZip) archive
/// and inspects existing .unitypackage archives for structural validity.
/// </summary>
public sealed class UnityPackageExporterService
{
    private const int StreamBufferSize = 65_536;
    private const int ExpectedGuidLength = 32;
    private const UnixFileMode StandardEntryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".asmdef",
        ".asmref"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExportManifest.json"
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProjectSettings",
        "Library",
        "Temp",
        "Logs",
        "Obj",
        "UserSettings",
        ".git"
    };

    private sealed class PackagePackItem
    {
        public required string Guid { get; init; }
        public required string UnityPathname { get; init; }
        public required string MetaFullPath { get; init; }
        public string? AssetFullPath { get; init; }
        public bool IsFolder => AssetFullPath == null;
    }

    private sealed class InspectionGuidState
    {
        public bool HasPathname { get; set; }
        public bool HasMeta { get; set; }
        public bool HasAsset { get; set; }
        public string? Pathname { get; set; }
    }

    public UnityPackageExportResult ExportFromUnityProjectDirectory(
        UnityPackageExportOptions options,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SourceProjectOrAssetsDirectory))
        {
            return BuildFailure(options.OutputPackagePath, "Source project or Assets directory must not be empty.", sw.ElapsedMilliseconds, warnings);
        }

        if (string.IsNullOrWhiteSpace(options.OutputPackagePath))
        {
            return BuildFailure(string.Empty, "Output .unitypackage path must not be empty.", sw.ElapsedMilliseconds, warnings);
        }

        string sourceFullDir;
        string outputFullPath;
        try
        {
            sourceFullDir = Path.GetFullPath(options.SourceProjectOrAssetsDirectory);
            outputFullPath = Path.GetFullPath(options.OutputPackagePath);
        }
        catch (Exception ex)
        {
            return BuildFailure(options.OutputPackagePath, $"Invalid path: {ex.Message}", sw.ElapsedMilliseconds, warnings);
        }

        if (!Directory.Exists(sourceFullDir))
        {
            return BuildFailure(outputFullPath, $"Source directory does not exist: '{sourceFullDir}'.", sw.ElapsedMilliseconds, warnings);
        }

        if (IsReparsePointOrSymlink(sourceFullDir))
        {
            return BuildFailure(outputFullPath, $"Refusing to pack symlinked source directory: '{sourceFullDir}'.", sw.ElapsedMilliseconds, warnings);
        }

        if (!options.OverwriteExisting && File.Exists(outputFullPath))
        {
            return BuildFailure(outputFullPath, $"Output package already exists and OverwriteExisting is false: '{outputFullPath}'.", sw.ElapsedMilliseconds, warnings);
        }

        if (!TryLocateAssetsRoot(sourceFullDir, out var assetsRootDir, out var locateError))
        {
            return BuildFailure(outputFullPath, locateError, sw.ElapsedMilliseconds, warnings);
        }

        var assetsRootWithSep = EnsureTrailingSeparator(assetsRootDir);
        var packItems = new List<PackagePackItem>();
        var seenGuids = new Dictionary<string, string>(StringComparer.Ordinal);
        int skippedCount = 0;

        progress?.Report((5, "Scanning Assets/ tree and validating .meta GUIDs..."));

        try
        {
            CollectPackItems(
                assetsRootDir,
                assetsRootWithSep,
                outputFullPath,
                options.IncludeScripts,
                packItems,
                seenGuids,
                warnings,
                ref skippedCount,
                ct);

            if (packItems.Count == 0)
            {
                return BuildFailure(
                    outputFullPath,
                    "No valid Unity assets or folders with valid .meta files were found to pack.",
                    sw.ElapsedMilliseconds,
                    warnings,
                    skippedCount);
            }

            // Deterministic ordering: folders and files sorted by Unity pathname (Ordinal).
            packItems.Sort(static (a, b) =>
            {
                int cmp = string.Compare(a.UnityPathname, b.UnityPathname, StringComparison.Ordinal);
                return cmp != 0 ? cmp : string.Compare(a.Guid, b.Guid, StringComparison.Ordinal);
            });

            var outputDir = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var tempOutputPath = Path.Combine(
                string.IsNullOrEmpty(outputDir) ? Directory.GetCurrentDirectory() : outputDir,
                $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");

            int assetEntriesPacked = 0;
            int folderEntriesPacked = 0;
            long totalBytesWritten = 0;

            try
            {
                WriteTarGzArchive(
                    tempOutputPath,
                    packItems,
                    options.CompressionLevel,
                    progress,
                    out assetEntriesPacked,
                    out folderEntriesPacked,
                    ct);

                var fileInfo = new FileInfo(tempOutputPath);
                totalBytesWritten = fileInfo.Exists ? fileInfo.Length : 0;

                File.Move(tempOutputPath, outputFullPath, overwrite: options.OverwriteExisting);
            }
            catch
            {
                TryDeleteFileQuietly(tempOutputPath);
                throw;
            }

            sw.Stop();
            progress?.Report((100, $"Packed {packItems.Count:N0} entry(ies) into {Path.GetFileName(outputFullPath)}."));

            return new UnityPackageExportResult
            {
                Success = true,
                OutputPackagePath = outputFullPath,
                TotalEntriesPacked = packItems.Count,
                AssetEntriesPacked = assetEntriesPacked,
                FolderEntriesPacked = folderEntriesPacked,
                SkippedEntriesCount = skippedCount,
                TotalBytesWritten = totalBytesWritten,
                ElapsedMs = sw.ElapsedMilliseconds,
                Warnings = warnings
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildFailure(outputFullPath, ex.Message, sw.ElapsedMilliseconds, warnings, skippedCount);
        }
    }

    public static UnityPackageInspectionResult InspectUnityPackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return new UnityPackageInspectionResult
            {
                IsValid = false,
                ErrorMessage = "Package path must not be empty."
            };
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(packagePath);
        }
        catch (Exception ex)
        {
            return new UnityPackageInspectionResult
            {
                IsValid = false,
                ErrorMessage = $"Invalid package path: {ex.Message}"
            };
        }

        if (!File.Exists(fullPath))
        {
            return new UnityPackageInspectionResult
            {
                IsValid = false,
                ErrorMessage = $"Package file does not exist: '{fullPath}'."
            };
        }

        var guidStates = new Dictionary<string, InspectionGuidState>(StringComparer.Ordinal);
        var seenSubEntries = new HashSet<(string Guid, string SubName)>();
        var orderedGuids = new List<string>();
        int duplicateCount = 0;
        int invalidCount = 0;

        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
            using var tarReader = new TarReader(gzip, leaveOpen: false);

            while (tarReader.GetNextEntry(copyData: false) is { } entry)
            {
                var rawName = (entry.Name ?? string.Empty).Replace('\\', '/').Trim();
                while (rawName.StartsWith("./", StringComparison.Ordinal))
                {
                    rawName = rawName[2..];
                }
                rawName = rawName.TrimStart('/');

                if (string.IsNullOrEmpty(rawName) || rawName == ".")
                {
                    continue;
                }

                if (entry.EntryType == TarEntryType.Directory)
                {
                    var dirToken = rawName.TrimEnd('/');
                    if (dirToken.Length > 0 && !IsValidUnityGuid(dirToken))
                    {
                        invalidCount++;
                    }
                    continue;
                }

                var slashIdx = rawName.IndexOf('/');
                if (slashIdx <= 0 || slashIdx != rawName.LastIndexOf('/'))
                {
                    invalidCount++;
                    continue;
                }

                var guidToken = rawName[..slashIdx];
                var subFile = rawName[(slashIdx + 1)..];
                if (!IsValidUnityGuid(guidToken))
                {
                    invalidCount++;
                    continue;
                }

                var normalizedGuid = guidToken.ToLowerInvariant();
                if (!seenSubEntries.Add((normalizedGuid, subFile)))
                {
                    duplicateCount++;
                    continue;
                }

                if (!guidStates.TryGetValue(normalizedGuid, out var state))
                {
                    state = new InspectionGuidState();
                    guidStates[normalizedGuid] = state;
                    orderedGuids.Add(normalizedGuid);
                }

                switch (subFile)
                {
                    case "pathname":
                        state.HasPathname = true;
                        var pathname = ReadEntryUtf8Text(entry.DataStream);
                        if (string.IsNullOrWhiteSpace(pathname) || ContainsPathTraversal(pathname))
                        {
                            invalidCount++;
                        }
                        else
                        {
                            state.Pathname = pathname;
                        }
                        break;

                    case "asset.meta":
                        state.HasMeta = true;
                        var metaText = ReadEntryUtf8Text(entry.DataStream);
                        if (!TryExtractGuidFromMetaContent(metaText, out var metaGuid)
                            || !string.Equals(metaGuid, normalizedGuid, StringComparison.Ordinal))
                        {
                            invalidCount++;
                        }
                        break;

                    case "asset":
                        state.HasAsset = true;
                        break;

                    case "preview.png":
                        // Optional Unity Asset Store thumbnail entry.
                        break;

                    default:
                        invalidCount++;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            return new UnityPackageInspectionResult
            {
                IsValid = false,
                DuplicateGuidCount = duplicateCount,
                InvalidEntryCount = invalidCount,
                ErrorMessage = $"Failed to read .unitypackage archive: {ex.Message}"
            };
        }

        int assetCount = 0;
        int folderCount = 0;
        var pathnames = new List<string>(orderedGuids.Count);
        var validGuids = new List<string>(orderedGuids.Count);

        for (int i = 0; i < orderedGuids.Count; i++)
        {
            var g = orderedGuids[i];
            var st = guidStates[g];
            if (!st.HasPathname || !st.HasMeta || string.IsNullOrWhiteSpace(st.Pathname))
            {
                invalidCount++;
                continue;
            }

            validGuids.Add(g);
            pathnames.Add(st.Pathname);

            if (st.HasAsset)
            {
                assetCount++;
            }
            else
            {
                folderCount++;
            }
        }

        bool isValid = invalidCount == 0 && duplicateCount == 0 && (assetCount + folderCount) > 0;
        string? errorMsg = null;
        if (!isValid)
        {
            errorMsg = (assetCount + folderCount) == 0
                ? "Archive contains no valid Unity package entries."
                : string.Create(CultureInfo.InvariantCulture, $"Archive validation failed: {invalidCount} invalid entry(ies), {duplicateCount} duplicate GUID entry(ies).");
        }

        return new UnityPackageInspectionResult
        {
            IsValid = isValid,
            AssetCount = assetCount,
            FolderCount = folderCount,
            DuplicateGuidCount = duplicateCount,
            InvalidEntryCount = invalidCount,
            Pathnames = pathnames,
            Guids = validGuids,
            ErrorMessage = errorMsg
        };
    }

    public static bool TryExtractGuidFromMetaFile(string metaFilePath, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrWhiteSpace(metaFilePath) || !File.Exists(metaFilePath))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(metaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);

            string? line;
            int lineCount = 0;
            while ((line = sr.ReadLine()) != null && lineCount < 64)
            {
                lineCount++;
                if (TryParseGuidLine(line, out guid))
                {
                    return true;
                }
            }
        }
        catch
        {
            guid = string.Empty;
        }

        return false;
    }

    public static bool TryExtractGuidFromMetaContent(string? content, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        using var sr = new StringReader(content);
        string? line;
        int lineCount = 0;
        while ((line = sr.ReadLine()) != null && lineCount < 64)
        {
            lineCount++;
            if (TryParseGuidLine(line, out guid))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsValidUnityGuid(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != ExpectedGuidLength)
        {
            return false;
        }

        for (int i = 0; i < ExpectedGuidLength; i++)
        {
            char c = candidate[i];
            bool isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseGuidLine(string line, out string guid)
    {
        guid = string.Empty;
        var span = line.AsSpan().Trim();
        if (!span.StartsWith("guid:", StringComparison.Ordinal))
        {
            return false;
        }

        var value = span[5..].Trim();
        if (value.Length != ExpectedGuidLength)
        {
            return false;
        }

        for (int i = 0; i < ExpectedGuidLength; i++)
        {
            char c = value[i];
            bool isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        guid = value.ToString().ToLowerInvariant();
        return true;
    }

    private static bool TryLocateAssetsRoot(string sourceFullDir, out string assetsRootDir, out string errorMessage)
    {
        assetsRootDir = string.Empty;
        errorMessage = string.Empty;

        var directAssetsDir = Path.Combine(sourceFullDir, "Assets");
        if (Directory.Exists(directAssetsDir))
        {
            if (IsReparsePointOrSymlink(directAssetsDir))
            {
                errorMessage = $"Refusing to follow symlinked Assets directory: '{directAssetsDir}'.";
                return false;
            }

            assetsRootDir = Path.GetFullPath(directAssetsDir);
            return true;
        }

        var leafName = Path.GetFileName(sourceFullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leafName, "Assets", StringComparison.OrdinalIgnoreCase))
        {
            assetsRootDir = sourceFullDir;
            return true;
        }

        // Accept sourceFullDir directly if it already contains Unity .meta files without a parent Assets/ wrapper.
        try
        {
            foreach (var _ in Directory.EnumerateFiles(sourceFullDir, "*.meta", SearchOption.TopDirectoryOnly))
            {
                assetsRootDir = sourceFullDir;
                return true;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Unable to inspect source directory '{sourceFullDir}': {ex.Message}";
            return false;
        }

        errorMessage = $"No Assets/ directory or .meta files were found in '{sourceFullDir}'.";
        return false;
    }

    private static void CollectPackItems(
        string assetsRootDir,
        string assetsRootWithSep,
        string outputFullPath,
        bool includeScripts,
        List<PackagePackItem> packItems,
        Dictionary<string, string> seenGuids,
        List<string> warnings,
        ref int skippedCount,
        CancellationToken ct)
    {
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var dirStack = new Stack<string>();
        dirStack.Push(assetsRootDir);

        while (dirStack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDir = dirStack.Pop();

            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
                files = Directory.GetFiles(currentDir);
            }
            catch (Exception ex)
            {
                warnings.Add($"Unable to enumerate '{currentDir}': {ex.Message}");
                skippedCount++;
                continue;
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            // Process subdirectories (push in reverse so iterative DFS visits in deterministic alphabetical order).
            var dirsToVisit = new List<string>(subDirs.Length);
            for (int i = 0; i < subDirs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var subDir = Path.GetFullPath(subDirs[i]);
                var dirName = Path.GetFileName(subDir);

                if (ExcludedDirectoryNames.Contains(dirName))
                {
                    continue;
                }

                if (!IsPathStrictlyConfined(assetsRootWithSep, subDir) || IsReparsePointOrSymlink(subDir))
                {
                    warnings.Add($"Skipped symlink or out-of-root directory: '{dirName}'.");
                    skippedCount++;
                    continue;
                }

                var relPosix = BuildUnityAssetPathname(assetsRootDir, subDir);
                if (relPosix == null)
                {
                    warnings.Add($"Skipped directory with invalid relative path: '{dirName}'.");
                    skippedCount++;
                    continue;
                }

                if (!includeScripts && IsUnderScriptsSubtree(relPosix))
                {
                    continue;
                }

                var metaPath = subDir + ".meta";
                if (!IsPathStrictlyConfined(assetsRootWithSep, metaPath) || IsReparsePointOrSymlink(metaPath))
                {
                    warnings.Add($"Skipped folder with unsafe .meta path: '{relPosix}'.");
                    skippedCount++;
                }
                else if (!File.Exists(metaPath))
                {
                    warnings.Add($"Skipped folder without .meta file: '{relPosix}'.");
                    skippedCount++;
                }
                else if (!TryExtractGuidFromMetaFile(metaPath, out var folderGuid))
                {
                    warnings.Add($"Skipped folder with invalid GUID in .meta file: '{relPosix}'.");
                    skippedCount++;
                }
                else if (!seenGuids.TryAdd(folderGuid, relPosix))
                {
                    warnings.Add($"Skipped duplicate GUID '{folderGuid}' for folder '{relPosix}' (already bound to '{seenGuids[folderGuid]}').");
                    skippedCount++;
                }
                else
                {
                    packItems.Add(new PackagePackItem
                    {
                        Guid = folderGuid,
                        UnityPathname = relPosix,
                        MetaFullPath = metaPath,
                        AssetFullPath = null
                    });
                }

                // Still traverse into the directory so any valid assets inside aren't lost even if an outer folder lacked a .meta.
                dirsToVisit.Add(subDir);
            }

            for (int i = dirsToVisit.Count - 1; i >= 0; i--)
            {
                dirStack.Push(dirsToVisit[i]);
            }

            // Process files in currentDir.
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var filePath = Path.GetFullPath(files[i]);
                var fileName = Path.GetFileName(filePath);

                // Ignore .meta companion files, ExportManifest.json, and the output package itself if placed inside.
                if (fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    || ExcludedFileNames.Contains(fileName)
                    || string.Equals(filePath, outputFullPath, pathComparison))
                {
                    continue;
                }

                if (!IsPathStrictlyConfined(assetsRootWithSep, filePath) || IsReparsePointOrSymlink(filePath))
                {
                    warnings.Add($"Skipped symlink or out-of-root file: '{fileName}'.");
                    skippedCount++;
                    continue;
                }

                var relPosix = BuildUnityAssetPathname(assetsRootDir, filePath);
                if (relPosix == null)
                {
                    warnings.Add($"Skipped file with invalid relative path: '{fileName}'.");
                    skippedCount++;
                    continue;
                }

                if (!includeScripts && (IsUnderScriptsSubtree(relPosix) || ScriptExtensions.Contains(Path.GetExtension(fileName))))
                {
                    continue;
                }

                var metaPath = filePath + ".meta";
                if (!IsPathStrictlyConfined(assetsRootWithSep, metaPath) || IsReparsePointOrSymlink(metaPath))
                {
                    warnings.Add($"Skipped file with unsafe .meta path: '{relPosix}'.");
                    skippedCount++;
                    continue;
                }

                if (!File.Exists(metaPath))
                {
                    warnings.Add($"Skipped file without .meta: '{relPosix}'.");
                    skippedCount++;
                    continue;
                }

                if (!TryExtractGuidFromMetaFile(metaPath, out var assetGuid))
                {
                    warnings.Add($"Skipped file with invalid GUID in .meta: '{relPosix}'.");
                    skippedCount++;
                    continue;
                }

                if (!seenGuids.TryAdd(assetGuid, relPosix))
                {
                    warnings.Add($"Skipped duplicate GUID '{assetGuid}' for asset '{relPosix}' (already bound to '{seenGuids[assetGuid]}').");
                    skippedCount++;
                    continue;
                }

                packItems.Add(new PackagePackItem
                {
                    Guid = assetGuid,
                    UnityPathname = relPosix,
                    MetaFullPath = metaPath,
                    AssetFullPath = filePath
                });
            }
        }
    }

    private static void WriteTarGzArchive(
        string tempOutputPath,
        List<PackagePackItem> items,
        CompressionLevel compressionLevel,
        IProgress<(int Percent, string Status)>? progress,
        out int assetEntriesPacked,
        out int folderEntriesPacked,
        CancellationToken ct)
    {
        assetEntriesPacked = 0;
        folderEntriesPacked = 0;

        // Prefer classic POSIX USTAR format (0 PAX extended header blocks, universal compatibility
        // with Unity 5.x through Unity 6000.x). Fall back to PAX only if a single asset exceeds the
        // 8 GiB USTAR single-file limit.
        const long maxUstarFileSize = (1L << 33) - 1;
        bool requiresPax = false;
        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].IsFolder && new FileInfo(items[i].AssetFullPath!).Length > maxUstarFileSize)
            {
                requiresPax = true;
                break;
            }
        }

        var tarFormat = requiresPax ? TarEntryFormat.Pax : TarEntryFormat.Ustar;
        using var outStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize);
        using var gzipStream = new GZipStream(outStream, compressionLevel, leaveOpen: false);
        using var tarWriter = new TarWriter(gzipStream, tarFormat, leaveOpen: false);

        int total = items.Count;
        var modTime = DateTimeOffset.UtcNow;

        TarEntry CreateFileEntry(string entryName, Stream dataStream)
        {
            TarEntry entry = requiresPax
                ? new PaxTarEntry(TarEntryType.RegularFile, entryName)
                : new UstarTarEntry(TarEntryType.RegularFile, entryName);
            entry.DataStream = dataStream;
            entry.Mode = StandardEntryMode;
            entry.ModificationTime = modTime;
            return entry;
        }

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            if ((i & 31) == 0 || i == total - 1)
            {
                int pct = 10 + (int)((i + 1) * 88L / Math.Max(1, total));
                progress?.Report((Math.Clamp(pct, 10, 98), $"Packing {item.UnityPathname}..."));
            }

            // 1. <guid>/pathname (UTF-8 no BOM, forward slashes, starting with Assets/, no trailing slash)
            var pathnameBytes = Utf8NoBom.GetBytes(item.UnityPathname);
            using (var pathnameStream = new MemoryStream(pathnameBytes, writable: false))
            {
                tarWriter.WriteEntry(CreateFileEntry($"{item.Guid}/pathname", pathnameStream));
            }

            // 2. <guid>/asset (files ONLY — folders MUST NOT have an asset entry per Unity spec!)
            if (!item.IsFolder)
            {
                using var assetStream = new FileStream(item.AssetFullPath!, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize);
                tarWriter.WriteEntry(CreateFileEntry($"{item.Guid}/asset", assetStream));
                assetEntriesPacked++;
            }
            else
            {
                folderEntriesPacked++;
            }

            // 3. <guid>/asset.meta (exact .meta bytes streamed from disk)
            using (var metaStream = new FileStream(item.MetaFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            {
                tarWriter.WriteEntry(CreateFileEntry($"{item.Guid}/asset.meta", metaStream));
            }
        }
    }

    private static string? BuildUnityAssetPathname(string assetsRootDir, string targetFullPath)
    {
        var rel = Path.GetRelativePath(assetsRootDir, targetFullPath);
        if (string.IsNullOrWhiteSpace(rel) || rel == ".")
        {
            return null;
        }

        var posix = rel.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(posix) || ContainsPathTraversal(posix))
        {
            return null;
        }

        return "Assets/" + posix;
    }

    private static bool IsUnderScriptsSubtree(string unityPathname)
    {
        return string.Equals(unityPathname, "Assets/Scripts", StringComparison.OrdinalIgnoreCase)
            || unityPathname.StartsWith("Assets/Scripts/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPathTraversal(string posixPath)
    {
        if (posixPath.Contains('\0'))
        {
            return true;
        }

        var segments = posixPath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static string EnsureTrailingSeparator(string fullDirPath)
    {
        var trimmed = fullDirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + Path.DirectorySeparatorChar;
    }

    private static bool IsPathStrictlyConfined(string rootWithSep, string candidateFullPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidateFullPath.StartsWith(rootWithSep, comparison);
    }

    private static bool IsReparsePointOrSymlink(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return !string.IsNullOrEmpty(info.LinkTarget);
        }
        catch
        {
            return true;
        }
    }

    private static string ReadEntryUtf8Text(Stream? dataStream)
    {
        if (dataStream == null)
        {
            return string.Empty;
        }

        using var sr = new StreamReader(dataStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return sr.ReadToEnd().Trim('\r', '\n');
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static UnityPackageExportResult BuildFailure(
        string outputPath,
        string errorMessage,
        long elapsedMs,
        List<string> warnings,
        int skippedCount = 0)
    {
        return new UnityPackageExportResult
        {
            Success = false,
            OutputPackagePath = outputPath ?? string.Empty,
            SkippedEntriesCount = skippedCount,
            ElapsedMs = elapsedMs,
            Warnings = warnings,
            ErrorMessage = errorMessage
        };
    }
}
