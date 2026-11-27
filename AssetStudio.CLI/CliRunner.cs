using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AssetStudio.CLI
{
    public static class CliRunner
    {
        public const int ExitSuccess = 0;
        public const int ExitRuntimeError = 1;
        public const int ExitInvalidArguments = 2;

        private static readonly JsonSerializerOptions JsonIndentedOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly JsonSerializerOptions JsonCompactOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private enum OutputFormat
        {
            Text,
            Json,
            Jsonl,
            Csv
        }

        private sealed record ScanFileRow(
            string Path,
            string Kind,
            long Size,
            string? BundleSignature,
            string? BundleUnityVersion);

        private sealed record ListAssetRow(
            long PathId,
            string Type,
            int ClassId,
            string Name,
            string Container,
            string SourceFile,
            long ByteSize,
            string UnityVersion);

        private sealed record ExportPlanEntry(
            AssetHandle Handle,
            ClassIDType AssetType,
            string OutputPath,
            int? SliceIndex);

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            if (stdout == null) throw new ArgumentNullException(nameof(stdout));
            if (stderr == null) throw new ArgumentNullException(nameof(stderr));

            if (args == null || args.Length == 0)
            {
                stderr.WriteLine("Error: missing command.");
                PrintUsage(stderr);
                return ExitInvalidArguments;
            }

            string command = args[0];
            if (IsHelpToken(command) || string.Equals(command, "help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage(stdout);
                return ExitSuccess;
            }

            string[] subArgs = args.Skip(1).ToArray();
            if (subArgs.Any(IsHelpToken))
            {
                PrintUsage(stdout);
                return ExitSuccess;
            }

            if (string.Equals(command, "capture-keys", StringComparison.OrdinalIgnoreCase))
            {
                return CliKeyCapture.Run(subArgs, stdout, stderr);
            }

            if (!CliDecryption.TryExtractOptions(subArgs, stderr, out string[] remaining, out DecryptionOptions decryptionOptions))
            {
                return ExitInvalidArguments;
            }

            subArgs = CliCaching.RemoveOption(remaining, out bool useDecompressionCache);
            CliDecryption.Load(decryptionOptions, subArgs);

            return command.ToLowerInvariant() switch
            {
                "scan" => RunScan(subArgs, stdout, stderr),
                "list" => RunList(subArgs, stdout, stderr, useDecompressionCache),
                "inspect" => RunInspect(subArgs, stdout, stderr, useDecompressionCache),
                "export" => RunExport(subArgs, stdout, stderr, useDecompressionCache),
                _ => InvalidArg(stderr, $"Unknown command '{command}'.")
            };
        }

        private static int RunScan(string[] args, TextWriter stdout, TextWriter stderr)
        {
            string? targetPath = null;
            OutputFormat format = OutputFormat.Text;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--format", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--format", stderr, out string? val)
                        || !TryParseOutputFormat(val!, allowStructuredTables: true, stderr, out format))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (arg.StartsWith('-'))
                {
                    return InvalidArg(stderr, $"Unknown option '{arg}' for 'scan'.");
                }
                else if (targetPath == null)
                {
                    targetPath = arg;
                }
                else
                {
                    return InvalidArg(stderr, $"Unexpected positional argument '{arg}' for 'scan'.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return InvalidArg(stderr, "Missing required <path> argument for 'scan'.");
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                stderr.WriteLine($"Error: path does not exist: {targetPath}");
                return ExitRuntimeError;
            }

            try
            {
                ProjectScanResult scanResult = Directory.Exists(targetPath)
                    ? ProjectScanner.ScanFolder(targetPath)
                    : ScanSingleFile(targetPath);

                var rows = scanResult.Files
                    .OrderBy(f => f.Path, StringComparer.Ordinal)
                    .Select(f => new ScanFileRow(
                        f.Path,
                        f.Kind.ToString(),
                        f.Size,
                        f.BundleSignature,
                        f.BundleUnityVersion))
                    .ToList();

                switch (format)
                {
                    case OutputFormat.Text:
                        stdout.WriteLine($"Root: {scanResult.RootPath}");
                        stdout.WriteLine(
                            $"Files: {scanResult.TotalFiles} | Bytes: {scanResult.TotalBytes} | " +
                            $"Bundles: {scanResult.UnityBundleCount} | Serialized: {scanResult.SerializedFileCount} | " +
                            $"Resources: {scanResult.ResourceFileCount} | Encrypted: {scanResult.EncryptedFileCount} | " +
                            $"Other: {scanResult.OtherFileCount} | Coverage: {scanResult.CoveragePercent.ToString("0.0", CultureInfo.InvariantCulture)}%");
                        foreach (var row in rows)
                        {
                            string extra = string.IsNullOrEmpty(row.BundleSignature)
                                ? string.Empty
                                : $" [{row.BundleSignature} {row.BundleUnityVersion}]";
                            stdout.WriteLine($"{row.Kind,-14} {row.Size,12}  {row.Path}{extra}");
                        }
                        break;
                    case OutputFormat.Json:
                        var payload = new
                        {
                            rootPath = scanResult.RootPath,
                            totalFiles = scanResult.TotalFiles,
                            totalBytes = scanResult.TotalBytes,
                            unityBundleCount = scanResult.UnityBundleCount,
                            serializedFileCount = scanResult.SerializedFileCount,
                            resourceFileCount = scanResult.ResourceFileCount,
                            encryptedFileCount = scanResult.EncryptedFileCount,
                            otherFileCount = scanResult.OtherFileCount,
                            errorCount = scanResult.ErrorCount,
                            coveragePercent = scanResult.CoveragePercent,
                            files = rows
                        };
                        stdout.WriteLine(JsonSerializer.Serialize(payload, JsonIndentedOptions));
                        break;
                    case OutputFormat.Jsonl:
                        foreach (var row in rows)
                        {
                            stdout.WriteLine(JsonSerializer.Serialize(row, JsonCompactOptions));
                        }
                        break;
                    case OutputFormat.Csv:
                        stdout.WriteLine("path,kind,size,bundleSignature,bundleUnityVersion");
                        foreach (var row in rows)
                        {
                            stdout.WriteLine(string.Join(",",
                                EscapeCsv(row.Path),
                                EscapeCsv(row.Kind),
                                row.Size.ToString(CultureInfo.InvariantCulture),
                                EscapeCsv(row.BundleSignature ?? string.Empty),
                                EscapeCsv(row.BundleUnityVersion ?? string.Empty)));
                        }
                        break;
                }

                return ExitSuccess;
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"Error scanning '{targetPath}': {ex.Message}");
                return ExitRuntimeError;
            }
        }

        private static ProjectScanResult ScanSingleFile(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            var info = new FileInfo(fullPath);
            ProjectFileKind kind = ProjectFileKind.Other;
            string? bundleSig = null;
            uint bundleVer = 0;
            string? bundleUnityVer = null;
            string? appliedProvider = null;

            using (var reader = new FileReader(fullPath))
            {
                appliedProvider = reader.AppliedDecryptionProvider;
                switch (reader.FileType)
                {
                    case FileType.BundleFile:
                        kind = ProjectFileKind.UnityBundle;
                        try
                        {
                            using var bundle = new BundleFile(reader);
                            bundleSig = bundle.m_Header.signature;
                            bundleVer = bundle.m_Header.version;
                            bundleUnityVer = bundle.m_Header.unityRevision;
                        }
                        catch
                        {
                            // Keep UnityBundle classification even if header detail probe fails.
                        }
                        break;
                    case FileType.AssetsFile:
                        kind = ProjectFileKind.SerializedFile;
                        break;
                    case FileType.ResourceFile:
                        kind = ProjectFileKind.ResourceFile;
                        break;
                    default:
                        if (reader.EncryptionBarrier != null)
                        {
                            kind = ProjectFileKind.Encrypted;
                        }
                        break;
                }
            }

            var scanned = new ScannedFileInfo(
                fullPath,
                info.Length,
                kind,
                info.LastWriteTimeUtc.Ticks,
                contentSampleHash: 0,
                appliedDecryptionProvider: appliedProvider);

            // ScannedFileInfo setters for bundle metadata are internal to AssetStudio; expose via ProjectScanResult.
            _ = bundleSig;
            _ = bundleVer;
            _ = bundleUnityVer;
            return new ProjectScanResult(Path.GetDirectoryName(fullPath) ?? fullPath, new[] { scanned });
        }

        private static int RunList(string[] args, TextWriter stdout, TextWriter stderr, bool useDecompressionCache)
        {
            string? targetPath = null;
            ClassIDType? typeFilter = null;
            string? nameFilter = null;
            OutputFormat format = OutputFormat.Text;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--type", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--type", stderr, out string? val)
                        || !TryParseClassId(val!, stderr, out var parsedType))
                    {
                        return ExitInvalidArguments;
                    }
                    typeFilter = parsedType;
                }
                else if (string.Equals(arg, "--filter", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--filter", stderr, out nameFilter))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (string.Equals(arg, "--format", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--format", stderr, out string? val)
                        || !TryParseOutputFormat(val!, allowStructuredTables: true, stderr, out format))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (arg.StartsWith('-'))
                {
                    return InvalidArg(stderr, $"Unknown option '{arg}' for 'list'.");
                }
                else if (targetPath == null)
                {
                    targetPath = arg;
                }
                else
                {
                    return InvalidArg(stderr, $"Unexpected positional argument '{arg}' for 'list'.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return InvalidArg(stderr, "Missing required <path> argument for 'list'.");
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                stderr.WriteLine($"Error: path does not exist: {targetPath}");
                return ExitRuntimeError;
            }

            try
            {
                using var assetsManager = CreateHeadlessAssetsManager(useDecompressionCache, targetPath, stderr);
                LoadPath(assetsManager, targetPath);

                var rows = assetsManager.ProjectIndex.GetHandles()
                    .Where(h => !typeFilter.HasValue || h.Type == typeFilter.Value)
                    .Where(h => string.IsNullOrEmpty(nameFilter)
                        || h.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                        || h.Container.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                        || h.SerializedFileName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(h => h.SerializedFileName, StringComparer.Ordinal)
                    .ThenBy(h => h.PathID)
                    .ThenBy(h => h.TypeString, StringComparer.Ordinal)
                    .ThenBy(h => h.Name, StringComparer.Ordinal)
                    .Select(h => new ListAssetRow(
                        h.PathID,
                        h.TypeString,
                        (int)h.Type,
                        h.Name,
                        h.Container,
                        h.SerializedFileName,
                        h.ByteSize,
                        h.UnityVersion))
                    .ToList();

                switch (format)
                {
                    case OutputFormat.Text:
                        stdout.WriteLine($"Assets: {rows.Count}");
                        foreach (var row in rows)
                        {
                            string containerPart = string.IsNullOrEmpty(row.Container) ? string.Empty : $" container={row.Container}";
                            stdout.WriteLine($"[{row.PathId}] {row.Type,-16} {row.Name} ({row.SourceFile}, {row.ByteSize} B){containerPart}");
                        }
                        break;
                    case OutputFormat.Json:
                        stdout.WriteLine(JsonSerializer.Serialize(new { count = rows.Count, assets = rows }, JsonIndentedOptions));
                        break;
                    case OutputFormat.Jsonl:
                        foreach (var row in rows)
                        {
                            stdout.WriteLine(JsonSerializer.Serialize(row, JsonCompactOptions));
                        }
                        break;
                    case OutputFormat.Csv:
                        stdout.WriteLine("pathId,type,classId,name,container,sourceFile,byteSize,unityVersion");
                        foreach (var row in rows)
                        {
                            stdout.WriteLine(string.Join(",",
                                row.PathId.ToString(CultureInfo.InvariantCulture),
                                EscapeCsv(row.Type),
                                row.ClassId.ToString(CultureInfo.InvariantCulture),
                                EscapeCsv(row.Name),
                                EscapeCsv(row.Container),
                                EscapeCsv(row.SourceFile),
                                row.ByteSize.ToString(CultureInfo.InvariantCulture),
                                EscapeCsv(row.UnityVersion)));
                        }
                        break;
                }

                return ExitSuccess;
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"Error listing '{targetPath}': {ex.Message}");
                return ExitRuntimeError;
            }
        }

        private static int RunInspect(string[] args, TextWriter stdout, TextWriter stderr, bool useDecompressionCache)
        {
            string? targetPath = null;
            long? pathId = null;
            string? fileFilter = null;
            OutputFormat format = OutputFormat.Text;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--path-id", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--path-id", stderr, out string? val)
                        || !long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedId))
                    {
                        return InvalidArg(stderr, $"Invalid --path-id value.");
                    }
                    pathId = parsedId;
                }
                else if (string.Equals(arg, "--file", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--file", stderr, out fileFilter))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (string.Equals(arg, "--format", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--format", stderr, out string? val)
                        || !TryParseOutputFormat(val!, allowStructuredTables: false, stderr, out format))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (arg.StartsWith('-'))
                {
                    return InvalidArg(stderr, $"Unknown option '{arg}' for 'inspect'.");
                }
                else if (targetPath == null)
                {
                    targetPath = arg;
                }
                else
                {
                    return InvalidArg(stderr, $"Unexpected positional argument '{arg}' for 'inspect'.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return InvalidArg(stderr, "Missing required <path> argument for 'inspect'.");
            }

            if (!pathId.HasValue)
            {
                return InvalidArg(stderr, "Missing required --path-id <id> option for 'inspect'.");
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                stderr.WriteLine($"Error: path does not exist: {targetPath}");
                return ExitRuntimeError;
            }

            try
            {
                using var assetsManager = CreateHeadlessAssetsManager(useDecompressionCache, targetPath, stderr);
                LoadPath(assetsManager, targetPath);

                var matches = assetsManager.ProjectIndex.GetHandles()
                    .Where(h => h.PathID == pathId.Value)
                    .Where(h => string.IsNullOrEmpty(fileFilter) || MatchesFileFilter(h, fileFilter))
                    .OrderBy(h => h.SerializedFileName, StringComparer.Ordinal)
                    .ThenBy(h => h.OriginalPath, StringComparer.Ordinal)
                    .ToList();

                if (matches.Count == 0)
                {
                    stderr.WriteLine($"Error: asset with PathID {pathId.Value} not found.");
                    return ExitRuntimeError;
                }

                var handle = matches[0];
                var obj = assetsManager.ResolveHandle(handle);
                bool hasTypeTree = obj?.serializedType?.m_Type != null;
                string? typeTreeDump = hasTypeTree ? obj!.Dump() : null;

                if (format == OutputFormat.Json)
                {
                    var payload = new
                    {
                        pathId = handle.PathID,
                        name = handle.Name,
                        type = handle.TypeString,
                        classId = (int)handle.Type,
                        sourceFile = handle.SerializedFileName,
                        originalPath = handle.OriginalPath,
                        container = handle.Container,
                        byteStart = handle.ByteStart,
                        byteSize = handle.ByteSize,
                        unityVersion = handle.UnityVersion,
                        hasTypeTree,
                        typeTreeDump
                    };
                    stdout.WriteLine(JsonSerializer.Serialize(payload, JsonIndentedOptions));
                }
                else
                {
                    stdout.WriteLine($"PathID:       {handle.PathID}");
                    stdout.WriteLine($"Name:         {handle.Name}");
                    stdout.WriteLine($"Type:         {handle.TypeString} ({(int)handle.Type})");
                    stdout.WriteLine($"SourceFile:   {handle.SerializedFileName}");
                    stdout.WriteLine($"OriginalPath: {handle.OriginalPath}");
                    if (!string.IsNullOrEmpty(handle.Container))
                    {
                        stdout.WriteLine($"Container:    {handle.Container}");
                    }
                    stdout.WriteLine($"ByteRange:    {handle.ByteStart}..{handle.ByteStart + handle.ByteSize} ({handle.ByteSize} bytes)");
                    stdout.WriteLine($"UnityVersion: {handle.UnityVersion}");
                    stdout.WriteLine($"HasTypeTree:  {hasTypeTree}");
                    if (!string.IsNullOrEmpty(typeTreeDump))
                    {
                        stdout.WriteLine("--- TypeTree Dump ---");
                        stdout.WriteLine(typeTreeDump.TrimEnd());
                    }
                }

                return ExitSuccess;
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"Error inspecting '{targetPath}': {ex.Message}");
                return ExitRuntimeError;
            }
        }

        private static int RunExport(string[] args, TextWriter stdout, TextWriter stderr, bool useDecompressionCache)
        {
            string? targetPath = null;
            string? outputDir = null;
            var requestedTypes = new HashSet<ClassIDType>();
            ImageFormat imageFormat = ImageFormat.Png;
            bool dryRun = false;
            ModelExportFormat modelFormat = ModelExportFormat.Glb;
            string? assembliesDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--output", StringComparison.Ordinal) || string.Equals(arg, "-o", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, arg, stderr, out outputDir))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (string.Equals(arg, "--type", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--type", stderr, out string? typeVal))
                    {
                        return ExitInvalidArguments;
                    }

                    foreach (var token in typeVal!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            requestedTypes.UnionWith(CliAssetExporter.AllTypes);
                            continue;
                        }

                        if (!TryParseExportableType(token, stderr, out var exportType))
                        {
                            return ExitInvalidArguments;
                        }
                        requestedTypes.Add(exportType);
                    }

                    if (requestedTypes.Count == 0)
                    {
                        return InvalidArg(stderr, "Empty --type filter.");
                    }
                }
                else if (string.Equals(arg, "--image-format", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--image-format", stderr, out string? imgVal)
                        || !TryParseImageFormat(imgVal!, stderr, out imageFormat))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (string.Equals(arg, "--model-format", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--model-format", stderr, out string? modelVal))
                    {
                        return ExitInvalidArguments;
                    }

                    if (!CliAssetExporter.TryParseModelFormat(modelVal!, out modelFormat))
                    {
                        return InvalidArg(stderr, $"Unsupported model format '{modelVal}'. Supported: glb, gltf, fbx.");
                    }
                }
                else if (string.Equals(arg, "--assemblies", StringComparison.Ordinal))
                {
                    if (!TryReadOptionValue(args, ref i, "--assemblies", stderr, out assembliesDir))
                    {
                        return ExitInvalidArguments;
                    }
                }
                else if (string.Equals(arg, "--dry-run", StringComparison.Ordinal))
                {
                    dryRun = true;
                }
                else if (arg.StartsWith('-'))
                {
                    return InvalidArg(stderr, $"Unknown option '{arg}' for 'export'.");
                }
                else if (targetPath == null)
                {
                    targetPath = arg;
                }
                else
                {
                    return InvalidArg(stderr, $"Unexpected positional argument '{arg}' for 'export'.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return InvalidArg(stderr, "Missing required <path> argument for 'export'.");
            }

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return InvalidArg(stderr, "Missing required --output <dir> option for 'export'.");
            }

            if (requestedTypes.Count == 0)
            {
                requestedTypes.Add(ClassIDType.Texture2D);
                requestedTypes.Add(ClassIDType.Texture2DArray);
                requestedTypes.Add(ClassIDType.TextAsset);
                requestedTypes.Add(ClassIDType.Mesh);
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                stderr.WriteLine($"Error: path does not exist: {targetPath}");
                return ExitRuntimeError;
            }

            try
            {
                string fullOutputRoot = Path.GetFullPath(outputDir);
                using var assetsManager = CreateHeadlessAssetsManager(useDecompressionCache, targetPath, stderr);
                LoadPath(assetsManager, targetPath);

                var handles = assetsManager.ProjectIndex.GetHandles()
                    .Where(h => requestedTypes.Contains(h.Type))
                    .OrderBy(h => h.SerializedFileName, StringComparer.Ordinal)
                    .ThenBy(h => h.PathID)
                    .ToList();

                var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var reservedPaths = new HashSet<string>(pathComparer);
                var extraExporter = new CliAssetExporter(
                    assetsManager,
                    new CliExportSettings { ImageFormat = imageFormat, ModelFormat = modelFormat, AssembliesDirectory = assembliesDir, DryRun = dryRun },
                    stdout,
                    stderr);

                int exportedCount = 0;
                int skippedCount = 0;
                int failedCount = 0;

                foreach (var handle in handles)
                {
                    var obj = assetsManager.ResolveHandle(handle);
                    if (obj == null)
                    {
                        skippedCount++;
                        continue;
                    }

                    string baseName = GetExportBaseName(handle, obj);
                    string typeFolder = handle.Type.ToString();

                    switch (handle.Type)
                    {
                        case ClassIDType.Texture2D when obj is Texture2D tex2d:
                        {
                            string outPath = ResolveSafeOutputPath(fullOutputRoot, typeFolder, baseName, imageFormat.GetFileExtension(), reservedPaths);
                            if (dryRun)
                            {
                                stdout.WriteLine($"[dry-run] Texture2D {handle.PathID} -> {outPath}");
                                exportedCount++;
                            }
                            else
                            {
                                using var bmp = tex2d.ConvertToImage(flip: true);
                                if (bmp == null)
                                {
                                    skippedCount++;
                                    break;
                                }
                                EnsureParentDirectory(outPath);
                                using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                bmp.WriteToStream(fs, imageFormat);
                                stdout.WriteLine($"Exported: {outPath}");
                                exportedCount++;
                            }
                            break;
                        }
                        case ClassIDType.Texture2DArray when obj is Texture2DArray texArr:
                        {
                            int depth = Math.Max(0, texArr.m_Depth);
                            if (depth <= 0 || texArr.IsTruncated || !texArr.IsFormatSupported)
                            {
                                skippedCount++;
                                break;
                            }

                            if (dryRun)
                            {
                                for (int slice = 0; slice < depth; slice++)
                                {
                                    string sliceName = $"{baseName}_slice{slice.ToString("D3", CultureInfo.InvariantCulture)}";
                                    string outPath = ResolveSafeOutputPath(fullOutputRoot, typeFolder, sliceName, imageFormat.GetFileExtension(), reservedPaths);
                                    stdout.WriteLine($"[dry-run] Texture2DArray {handle.PathID}[{slice}] -> {outPath}");
                                    exportedCount++;
                                }
                            }
                            else
                            {
                                var converter = new Texture2DArrayConverter(texArr);
                                var slices = converter.ConvertAllSlicesToImages(flip: true);
                                try
                                {
                                    if (slices.Count == 0)
                                    {
                                        skippedCount++;
                                        break;
                                    }

                                    for (int slice = 0; slice < slices.Count; slice++)
                                    {
                                        string sliceName = $"{baseName}_slice{slice.ToString("D3", CultureInfo.InvariantCulture)}";
                                        string outPath = ResolveSafeOutputPath(fullOutputRoot, typeFolder, sliceName, imageFormat.GetFileExtension(), reservedPaths);
                                        EnsureParentDirectory(outPath);
                                        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                        slices[slice].WriteToStream(fs, imageFormat);
                                        stdout.WriteLine($"Exported: {outPath}");
                                        exportedCount++;
                                    }
                                }
                                finally
                                {
                                    foreach (var bmp in slices)
                                    {
                                        bmp.Dispose();
                                    }
                                }
                            }
                            break;
                        }
                        case ClassIDType.TextAsset when obj is TextAsset textAsset:
                        {
                            string outPath = ResolveSafeOutputPath(fullOutputRoot, typeFolder, baseName, ".txt", reservedPaths);
                            if (dryRun)
                            {
                                stdout.WriteLine($"[dry-run] TextAsset {handle.PathID} -> {outPath}");
                                exportedCount++;
                            }
                            else
                            {
                                EnsureParentDirectory(outPath);
                                File.WriteAllBytes(outPath, textAsset.m_Script ?? Array.Empty<byte>());
                                stdout.WriteLine($"Exported: {outPath}");
                                exportedCount++;
                            }
                            break;
                        }
                        case ClassIDType.Mesh when obj is Mesh mesh:
                        {
                            mesh.EnsureProcessed();
                            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
                            {
                                skippedCount++;
                                break;
                            }

                            string outPath = ResolveSafeOutputPath(fullOutputRoot, typeFolder, baseName, ".obj", reservedPaths);
                            if (dryRun)
                            {
                                stdout.WriteLine($"[dry-run] Mesh {handle.PathID} -> {outPath}");
                                exportedCount++;
                            }
                            else
                            {
                                EnsureParentDirectory(outPath);
                                if (WriteMeshToObj(mesh, outPath))
                                {
                                    stdout.WriteLine($"Exported: {outPath}");
                                    exportedCount++;
                                }
                                else
                                {
                                    skippedCount++;
                                }
                            }
                            break;
                        }
                        default:
                        {
                            try
                            {
                                int written = extraExporter.Export(handle, obj, fullOutputRoot, baseName, reservedPaths);
                                if (written > 0)
                                {
                                    exportedCount += written;
                                }
                                else
                                {
                                    skippedCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                failedCount++;
                                stderr.WriteLine($"Failed: {handle.Type} {handle.PathID} ({baseName}): {ex.Message}");
                            }
                            break;
                        }
                    }
                }

                string failedSuffix = failedCount > 0 ? $", {failedCount} failed" : string.Empty;
                stdout.WriteLine(dryRun
                    ? $"Dry-run complete: {exportedCount} planned, {skippedCount} skipped{failedSuffix}."
                    : $"Export complete: {exportedCount} exported, {skippedCount} skipped{failedSuffix}.");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"Error exporting '{targetPath}': {ex.Message}");
                return ExitRuntimeError;
            }
        }

        public static string ResolveSafeOutputPath(
            string outputRoot,
            string subDirectory,
            string rawAssetName,
            string extension,
            ISet<string> reservedPaths)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("Output root must not be empty.", nameof(outputRoot));
            }

            string normalizedRoot = Path.GetFullPath(outputRoot);
            string rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            string safeFolder = SanitizePathSegment(subDirectory, "Assets");
            string safeName = SanitizeFileName(rawAssetName, "unnamed");
            string safeExt = string.IsNullOrWhiteSpace(extension)
                ? string.Empty
                : (extension.StartsWith('.') ? extension : "." + extension);

            string folderPath = Path.GetFullPath(Path.Combine(normalizedRoot, safeFolder));
            EnsurePathInsideRoot(folderPath, rootWithSep);

            string candidate = Path.GetFullPath(Path.Combine(folderPath, safeName + safeExt));
            EnsurePathInsideRoot(candidate, rootWithSep);

            int suffix = 1;
            while (!reservedPaths.Add(candidate))
            {
                candidate = Path.GetFullPath(Path.Combine(folderPath, $"{safeName}_{suffix.ToString(CultureInfo.InvariantCulture)}{safeExt}"));
                EnsurePathInsideRoot(candidate, rootWithSep);
                suffix++;
            }

            return candidate;
        }

        private static void EnsurePathInsideRoot(string candidateFullPath, string rootWithSeparator)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidateFullPath.StartsWith(rootWithSeparator, comparison))
            {
                throw new InvalidOperationException($"Resolved output path escapes the configured output root: {candidateFullPath}");
            }
        }

        private static string SanitizePathSegment(string? segment, string fallback)
        {
            return SanitizeFileName(segment, fallback);
        }

        private static string SanitizeFileName(string? rawName, string fallback)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return fallback;
            }

            // Collapse traversal sequences and directory separators so asset names never escape outputRoot.
            string cleaned = rawName
                .Replace('\\', '_')
                .Replace('/', '_')
                .Replace(':', '_')
                .Replace("..", "_", StringComparison.Ordinal);

            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(cleaned.Length);
            foreach (char c in cleaned)
            {
                if (Array.IndexOf(invalidChars, c) >= 0 || char.IsControl(c))
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string result = sb.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(result) || result == "." || result == "..")
            {
                return fallback;
            }

            return result;
        }

        private static string GetExportBaseName(AssetHandle handle, Object obj)
        {
            string? name = obj is NamedObject named && !string.IsNullOrWhiteSpace(named.m_Name)
                ? named.m_Name
                : handle.Name;

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"{handle.TypeString}_{handle.PathID.ToString(CultureInfo.InvariantCulture)}";
            }

            return name;
        }

        private static void EnsureParentDirectory(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private static bool WriteMeshToObj(Mesh mesh, string objPath)
        {
            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
            {
                return false;
            }

            int vertexComponents = mesh.m_Vertices.Length / mesh.m_VertexCount;
            if (vertexComponents < 3)
            {
                return false;
            }
            vertexComponents = Math.Min(vertexComponents, 4);

            var uvs = mesh.m_UV0;
            var normals = mesh.m_Normals;
            int uvComponents = uvs != null && uvs.Length >= mesh.m_VertexCount * 2
                ? Math.Min(uvs.Length / mesh.m_VertexCount, 4)
                : 0;
            int normalComponents = normals != null && normals.Length >= mesh.m_VertexCount * 3
                ? Math.Min(normals.Length / mesh.m_VertexCount, 4)
                : 0;
            bool hasUvs = uvComponents >= 2;
            bool hasNormals = normalComponents >= 3;

            using var writer = new StreamWriter(objPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string groupName = string.IsNullOrWhiteSpace(mesh.m_Name) ? "mesh" : SanitizeFileName(mesh.m_Name, "mesh");
            writer.WriteLine($"g {groupName}");

            for (int v = 0; v < mesh.m_VertexCount; v++)
            {
                writer.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "v {0} {1} {2}",
                    CleanFloat(-mesh.m_Vertices[v * vertexComponents]),
                    CleanFloat(mesh.m_Vertices[v * vertexComponents + 1]),
                    CleanFloat(mesh.m_Vertices[v * vertexComponents + 2])));
            }

            if (hasUvs)
            {
                for (int v = 0; v < mesh.m_VertexCount; v++)
                {
                    writer.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "vt {0} {1}",
                        CleanFloat(uvs![v * uvComponents]),
                        CleanFloat(uvs[v * uvComponents + 1])));
                }
            }

            if (hasNormals)
            {
                for (int v = 0; v < mesh.m_VertexCount; v++)
                {
                    writer.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "vn {0} {1} {2}",
                        CleanFloat(-normals![v * normalComponents]),
                        CleanFloat(normals[v * normalComponents + 1]),
                        CleanFloat(normals[v * normalComponents + 2])));
                }
            }

            int firstIndex = 0;
            for (int sub = 0; sub < mesh.m_SubMeshes.Length && firstIndex < mesh.m_Indices.Count; sub++)
            {
                writer.WriteLine($"g {groupName}_{sub.ToString(CultureInfo.InvariantCulture)}");
                long requested = mesh.m_SubMeshes[sub].indexCount;
                int available = mesh.m_Indices.Count - firstIndex;
                int count = (int)Math.Min(requested, available);
                int faces = count / 3;
                for (int f = 0; f < faces; f++)
                {
                    int start = firstIndex + f * 3;
                    uint i0 = mesh.m_Indices[start + 2];
                    uint i1 = mesh.m_Indices[start + 1];
                    uint i2 = mesh.m_Indices[start];
                    if (i0 >= mesh.m_VertexCount || i1 >= mesh.m_VertexCount || i2 >= mesh.m_VertexCount)
                    {
                        continue;
                    }

                    writer.WriteLine($"f {FormatObjRef(i0, hasUvs, hasNormals)} {FormatObjRef(i1, hasUvs, hasNormals)} {FormatObjRef(i2, hasUvs, hasNormals)}");
                }
                firstIndex += count;
            }

            return true;
        }

        private static float CleanFloat(float value) => float.IsFinite(value) ? value : 0f;

        private static string FormatObjRef(uint zeroBasedVertex, bool hasUvs, bool hasNormals)
        {
            string idx = (zeroBasedVertex + 1).ToString(CultureInfo.InvariantCulture);
            if (hasUvs && hasNormals) return $"{idx}/{idx}/{idx}";
            if (hasUvs) return $"{idx}/{idx}";
            if (hasNormals) return $"{idx}//{idx}";
            return idx;
        }

        private static AssetsManager CreateHeadlessAssetsManager(bool useDecompressionCache, string targetPath, TextWriter stderr)
        {
            var assetsManager = new AssetsManager
            {
                LazyLoading = true,
                AutoLoadCatalogDependencies = false,
                AutoLoadExternalDependencies = false
            };
            return useDecompressionCache ? CliCaching.Attach(assetsManager, targetPath, stderr) : assetsManager;
        }

        private static void LoadPath(AssetsManager assetsManager, string targetPath)
        {
            if (Directory.Exists(targetPath))
            {
                assetsManager.LoadFolder(targetPath);
            }
            else
            {
                assetsManager.LoadFiles(targetPath);
            }
        }

        private static bool MatchesFileFilter(AssetHandle handle, string fileFilter)
        {
            if (string.Equals(handle.SerializedFileName, fileFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(handle.OriginalPath)
                && handle.OriginalPath.EndsWith(fileFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return handle.SerializedFileName.Contains(fileFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHelpToken(string arg) =>
            string.Equals(arg, "--help", StringComparison.Ordinal)
            || string.Equals(arg, "-h", StringComparison.Ordinal)
            || string.Equals(arg, "-?", StringComparison.Ordinal);

        private static int InvalidArg(TextWriter stderr, string message)
        {
            stderr.WriteLine($"Error: {message}");
            return ExitInvalidArguments;
        }

        private static bool TryReadOptionValue(string[] args, ref int index, string optionName, TextWriter stderr, out string? value)
        {
            if (index + 1 >= args.Length)
            {
                stderr.WriteLine($"Error: missing value for {optionName}.");
                value = null;
                return false;
            }
            index++;
            value = args[index];
            return true;
        }

        private static bool TryParseOutputFormat(string raw, bool allowStructuredTables, TextWriter stderr, out OutputFormat format)
        {
            switch (raw.ToLowerInvariant())
            {
                case "text":
                    format = OutputFormat.Text;
                    return true;
                case "json":
                    format = OutputFormat.Json;
                    return true;
                case "jsonl" when allowStructuredTables:
                    format = OutputFormat.Jsonl;
                    return true;
                case "csv" when allowStructuredTables:
                    format = OutputFormat.Csv;
                    return true;
                default:
                    format = OutputFormat.Text;
                    stderr.WriteLine($"Error: unsupported format '{raw}'.");
                    return false;
            }
        }

        private static bool TryParseClassId(string raw, TextWriter stderr, out ClassIDType classId)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                classId = (ClassIDType)numeric;
                return Enum.IsDefined(typeof(ClassIDType), classId);
            }

            if (Enum.TryParse<ClassIDType>(raw, ignoreCase: true, out classId))
            {
                return true;
            }

            stderr.WriteLine($"Error: unknown ClassID type '{raw}'.");
            return false;
        }

        private static bool TryParseExportableType(string raw, TextWriter stderr, out ClassIDType classId)
        {
            switch (raw.ToLowerInvariant())
            {
                case "texture2d":
                    classId = ClassIDType.Texture2D;
                    return true;
                case "texture2darray":
                    classId = ClassIDType.Texture2DArray;
                    return true;
                case "textasset":
                    classId = ClassIDType.TextAsset;
                    return true;
                case "mesh":
                    classId = ClassIDType.Mesh;
                    return true;
                default:
                    if (CliAssetExporter.TryParseType(raw, out classId))
                    {
                        return true;
                    }

                    stderr.WriteLine($"Error: unsupported export type '{raw}'. Supported: {CliAssetExporter.SupportedTypeList}.");
                    return false;
            }
        }

        private static bool TryParseImageFormat(string raw, TextWriter stderr, out ImageFormat format)
        {
            switch (raw.ToLowerInvariant())
            {
                case "png":
                    format = ImageFormat.Png;
                    return true;
                case "webp":
                    format = ImageFormat.Webp;
                    return true;
                case "tga":
                    format = ImageFormat.Tga;
                    return true;
                case "jpeg":
                case "jpg":
                    format = ImageFormat.Jpeg;
                    return true;
                case "bmp":
                    format = ImageFormat.Bmp;
                    return true;
                default:
                    format = ImageFormat.Png;
                    stderr.WriteLine($"Error: unsupported image format '{raw}'. Supported: png, webp, tga, jpeg, bmp.");
                    return false;
            }
        }

        private static string EscapeCsv(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return value;
            }
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("AssetStudio.CLI - Headless Unity asset inspection & export tool");
            writer.WriteLine("Usage:");
            writer.WriteLine("  AssetStudio.CLI --help | -h");
            writer.WriteLine("  AssetStudio.CLI scan <path> [--format text|json|jsonl|csv]");
            writer.WriteLine("  AssetStudio.CLI list <path> [--type <ClassID>] [--filter <substring>] [--format text|json|jsonl|csv]");
            writer.WriteLine("  AssetStudio.CLI inspect <path> --path-id <id> [--file <name>] [--format text|json]");
            writer.WriteLine("  AssetStudio.CLI export <path> --output <dir> [--type <type>[,<type>...]|all] [--image-format png|webp|tga|jpeg|bmp] [--model-format glb|gltf|fbx] [--assemblies <Managed folder>] [--dry-run]");
            writer.WriteLine($"      export types: {CliAssetExporter.SupportedTypeList} (default: Texture2D, Texture2DArray, TextAsset, Mesh)");
            writer.WriteLine("  AssetStudio.CLI capture-keys <path> [--exe <game executable>] [--timeout <seconds>] [--format text|json]");
            writer.WriteLine();
            writer.WriteLine("Decryption profiles: scan/list/inspect/export load the profiles and plugins of the user folder by default;");
            writer.WriteLine("  --profiles <json file|directory>  load additional profiles (repeatable)");
            writer.WriteLine($"  --no-local-profiles               skip the user and application profile folders (user folder override: {DecryptionHelper.UserDataDirectoryVariable})");
            writer.WriteLine("Decompression cache: list/inspect/export keep decompressed bundles in the same disk cache the GUI uses;");
            writer.WriteLine($"  {CliCaching.DisableOption}  always decompress from scratch (cache folder override: {CliCaching.CacheDirectoryVariable})");
        }
    }
}
