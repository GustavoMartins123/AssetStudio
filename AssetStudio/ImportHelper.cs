using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio
{
    public static class ImportHelper
    {
        public static long MaxDecompressedSize { get; set; } = 4L * 1024 * 1024 * 1024; // 4 GiB safety cap

        private static readonly StringComparer PathKeyComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        private static readonly StringComparison PathKeyComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        private static readonly ConcurrentDictionary<string, string> MergedSplitFallbackPaths =
            new ConcurrentDictionary<string, string>(PathKeyComparer);

        public static void ClearMergedSplitFallbackPaths() => MergedSplitFallbackPaths.Clear();

        public static bool TryGetMergedSplitFallbackPath(string destFull, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? fallbackPath)
        {
            fallbackPath = null;
            if (string.IsNullOrWhiteSpace(destFull))
            {
                return false;
            }

            var key = NormalizeSplitPathKey(destFull);
            if (MergedSplitFallbackPaths.TryGetValue(key, out var mapped) && File.Exists(mapped))
            {
                fallbackPath = mapped;
                return true;
            }

            return false;
        }

        public static void MergeSplitAssets(string path, bool allDirectories = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var splitFiles = GetFilesSafe(path, "*.split0", allDirectories);
            foreach (var splitFile in splitFiles)
            {
                var destFile = Path.GetFileNameWithoutExtension(splitFile);
                var destPath = Path.GetDirectoryName(splitFile);
                if (string.IsNullOrEmpty(destPath) || string.IsNullOrEmpty(destFile))
                {
                    continue;
                }

                var destFull = Path.Combine(destPath, destFile);
                var destKey = NormalizeSplitPathKey(destFull);
                if (File.Exists(destFull))
                {
                    MergedSplitFallbackPaths.TryRemove(destKey, out _);
                    continue;
                }

                if (!TryCollectOrderedSplitParts(destPath, destFile, destFull, out var orderedParts))
                {
                    MergedSplitFallbackPaths.TryRemove(destKey, out _);
                    continue;
                }

                bool isReadOnlySource = IsReadOnlyDirectoryOrParts(destPath, orderedParts);
                bool destinationWriteFailed = false;
                if (!isReadOnlySource && TryMergePartsToTarget(orderedParts, destFull, overwriteTarget: false, out destinationWriteFailed))
                {
                    MergedSplitFallbackPaths.TryRemove(destKey, out _);
                    continue;
                }

                if (!isReadOnlySource && !destinationWriteFailed)
                {
                    MergedSplitFallbackPaths.TryRemove(destKey, out _);
                    continue;
                }

                var fallbackDest = GetFallbackMergedPath(destPath, destFile);
                try
                {
                    var fallbackDir = Path.GetDirectoryName(fallbackDest);
                    if (!string.IsNullOrEmpty(fallbackDir))
                    {
                        Directory.CreateDirectory(fallbackDir);
                    }
                }
                catch (Exception ex)
                {
                    MergedSplitFallbackPaths.TryRemove(destKey, out _);
                    Logger.Warning($"Failed to create temporary split-merge directory for '{destFull}': {ex.Message}");
                    continue;
                }

                if (TryMergePartsToTarget(orderedParts, fallbackDest, overwriteTarget: true, out _))
                {
                    MergedSplitFallbackPaths[destKey] = fallbackDest;
                    Logger.Info($"Source directory '{destPath}' is not writable; merged split asset '{destFile}' to temporary path '{fallbackDest}'.");
                }
                else
                {
                    MergedSplitFallbackPaths.TryRemove(destKey, out _);
                }
            }
        }

        private static bool TryCollectOrderedSplitParts(string destPath, string destFile, string destFull, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<string>? orderedParts)
        {
            orderedParts = null;
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(destPath, destFile + ".split*");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to enumerate split parts for '{destFull}': {ex.Message}");
                return false;
            }

            var prefix = destFile + ".split";
            var partsByIndex = new Dictionary<int, string>();
            int maxIndex = -1;

            foreach (var candidate in candidates)
            {
                var candidateName = Path.GetFileName(candidate);
                if (string.IsNullOrEmpty(candidateName) || !candidateName.StartsWith(prefix, PathKeyComparison))
                {
                    continue;
                }

                var suffix = candidateName.Substring(prefix.Length);
                if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var partIndex)
                    || !string.Equals(suffix, partIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    continue;
                }

                partsByIndex[partIndex] = candidate;
                if (partIndex > maxIndex)
                {
                    maxIndex = partIndex;
                }
            }

            if (maxIndex < 0)
            {
                return false;
            }

            var result = new List<string>(maxIndex + 1);
            for (int i = 0; i <= maxIndex; i++)
            {
                if (!partsByIndex.TryGetValue(i, out var partPath) || !File.Exists(partPath))
                {
                    Logger.Warning($"Skipping incomplete split asset '{destFull}': missing part '{destFull}.split{i}'.");
                    return false;
                }
                result.Add(partPath);
            }

            orderedParts = result;
            return true;
        }

        private static bool IsReadOnlyDirectoryOrParts(string destPath, IReadOnlyList<string> orderedParts)
        {
            try
            {
                var dirAttr = File.GetAttributes(destPath);
                if ((dirAttr & FileAttributes.ReadOnly) != 0)
                {
                    return true;
                }

                for (int i = 0; i < orderedParts.Count; i++)
                {
                    var partAttr = File.GetAttributes(orderedParts[i]);
                    if ((partAttr & FileAttributes.ReadOnly) != 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        private static bool TryMergePartsToTarget(IReadOnlyList<string> orderedParts, string targetPath, bool overwriteTarget, out bool destinationWriteFailed)
        {
            destinationWriteFailed = false;
            var tempStagingDir = Path.Combine(Path.GetTempPath(), "AssetStudio_SplitStaging");
            try
            {
                Directory.CreateDirectory(tempStagingDir);
            }
            catch
            {
                tempStagingDir = Path.GetTempPath();
            }
            var tmpPath = Path.Combine(tempStagingDir, Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            bool moved = false;

            try
            {
                FileStream destStream;
                try
                {
                    destStream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    destinationWriteFailed = true;
                    return false;
                }

                using (destStream)
                {
                    for (int i = 0; i < orderedParts.Count; i++)
                    {
                        using (var sourceStream = File.OpenRead(orderedParts[i]))
                        {
                            sourceStream.CopyTo(destStream);
                        }
                    }
                    destStream.Flush();
                }

                try
                {
                    File.Move(tmpPath, targetPath, overwriteTarget);
                    moved = true;
                    return true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    if (File.Exists(targetPath))
                    {
                        return true;
                    }
                    destinationWriteFailed = true;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to merge split asset '{targetPath}': {ex.Message}");
                return false;
            }
            finally
            {
                if (!moved)
                {
                    TryDeleteFile(tmpPath);
                }
            }
        }

        private static string GetFallbackMergedPath(string destPath, string destFile)
        {
            var normalizedDir = NormalizeSplitPathKey(destPath);
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDir));
            var dirHash = Convert.ToHexStringLower(hashBytes.AsSpan(0, 8));
            var fallbackDir = Path.Combine(Path.GetTempPath(), "AssetStudio", "SplitMerge", dirHash);
            return Path.Combine(fallbackDir, destFile);
        }

        private static string NormalizeSplitPathKey(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup of temporary merge file.
            }
        }

        public static string[] ProcessingSplitFiles(IEnumerable<string> selectFile)
        {
            var splitFiles = new List<string>();
            var selectFilesList = new List<string>();
            foreach (var x in selectFile)
            {
                var fileName = Path.GetFileName(x);
                if (!string.IsNullOrEmpty(fileName) && fileName.Contains(".split"))
                {
                    splitFiles.Add(Path.Combine(Path.GetDirectoryName(x) ?? string.Empty, Path.GetFileNameWithoutExtension(x)));
                }
                else
                {
                    selectFilesList.Add(x);
                }
            }

            var distinctSplitFiles = splitFiles.Distinct(PathKeyComparer);
            foreach (var file in distinctSplitFiles)
            {
                if (File.Exists(file))
                {
                    selectFilesList.Add(file);
                }
                else if (TryGetMergedSplitFallbackPath(file, out var fallbackPath))
                {
                    selectFilesList.Add(fallbackPath);
                }
            }
            return selectFilesList.Distinct(PathKeyComparer).ToArray();
        }

        public static FileReader DecompressGZip(FileReader reader)
        {
            using (reader)
            {
                using (var gs = new GZipStream(reader.BaseStream, CompressionMode.Decompress))
                {
                    var stream = CopyDecompressedWithLimits(gs, reader.FullPath, "gzip", reader.BaseStream.Length);
                    return new FileReader(reader.FullPath, stream);
                }
            }
        }

        public static FileReader DecompressBrotli(FileReader reader)
        {
            using (reader)
            {
                using (var brotliStream = new BrotliStream(reader.BaseStream, CompressionMode.Decompress))
                {
                    var stream = CopyDecompressedWithLimits(brotliStream, reader.FullPath, "brotli", reader.BaseStream.Length);
                    return new FileReader(reader.FullPath, stream);
                }
            }
        }

        internal static Stream CopyDecompressedWithLimits(Stream source, string sourcePath, string kind, long compressedHintBytes)
        {
            var initialCapacity = (int)Math.Min(Math.Max(compressedHintBytes, 4096L), 16L * 1024 * 1024);
            Stream output = new MemoryStream(initialCapacity);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalWritten = 0;
                int bytesRead;
                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalWritten += bytesRead;
                    if (totalWritten > MaxDecompressedSize)
                    {
                        throw new InvalidDataException($"Decompressed {kind} stream '{Path.GetFileName(sourcePath)}' exceeds safety limit of {MaxDecompressedSize} bytes.");
                    }

                    if (output is MemoryStream && BundleFile.ShouldUseTemporaryStream(totalWritten))
                    {
                        var tempStream = BundleFile.CreateTemporaryStream(sourcePath, kind);
                        try
                        {
                            output.Position = 0;
                            output.CopyTo(tempStream);
                        }
                        catch
                        {
                            tempStream.Dispose();
                            throw;
                        }
                        output.Dispose();
                        output = tempStream;
                    }

                    output.Write(buffer, 0, bytesRead);
                }

                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static string[] GetFilesSafe(string path, string searchPattern, bool recursive)
        {
            var files = new List<string>();
            GetFilesSafeInternal(path, searchPattern, recursive, files);
            return files.ToArray();
        }

        private static void GetFilesSafeInternal(string path, string searchPattern, bool recursive, List<string> result)
        {
            try
            {
                var files = Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
                result.AddRange(files);
            }
            catch
            {
                // ignore
            }

            if (recursive)
            {
                try
                {
                    var subDirs = Directory.GetDirectories(path);
                    foreach (var dir in subDirs)
                    {
                        GetFilesSafeInternal(dir, searchPattern, true, result);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
