using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AssetStudio
{
    public enum ProjectFileKind
    {
        Other,
        UnityBundle,
        SerializedFile,
        ResourceFile,
        Encrypted
    }

    public sealed class ScannedFileInfo
    {
        public string Path { get; internal set; } = string.Empty;
        public long Size { get; internal set; }
        public ProjectFileKind Kind { get; internal set; }
        public string? BundleSignature { get; internal set; }
        public uint BundleVersion { get; internal set; }
        public string? BundleUnityVersion { get; internal set; }
        public string? AddressablesGroup { get; internal set; }
        public string? AppliedDecryptionProvider { get; internal set; }
        public StreamEncryptionBarrier? EncryptionBarrier { get; internal set; }
    }

    public struct ScanProgress
    {
        public int ScannedFiles { get; set; }
        /// <summary>-1 while enumeration is still running.</summary>
        public int TotalFiles { get; set; }
        public long ScannedBytes { get; set; }
        public int UnityBundleCount { get; set; }
        public int EncryptedFileCount { get; set; }
    }

    public sealed class ProjectScanResult
    {
        public string RootPath { get; internal set; } = string.Empty;
        public int TotalFiles { get; internal set; }
        public long TotalBytes { get; internal set; }
        public int UnityBundleCount { get; internal set; }
        public int SerializedFileCount { get; internal set; }
        public int ResourceFileCount { get; internal set; }
        public int EncryptedFileCount { get; internal set; }
        public int OtherFileCount { get; internal set; }
        public int ErrorCount { get; internal set; }
        public List<string> SampleUnityBundles { get; } = new List<string>();
        public List<StreamEncryptionBarrier> EncryptionBarriers { get; } = new List<StreamEncryptionBarrier>();
        public List<ScannedFileInfo> Files { get; } = new List<ScannedFileInfo>();

        /// <summary>
        /// Number of Unity asset container files (bundles + serialized files) that can be read.
        /// </summary>
        public int ReadableAssetFileCount => UnityBundleCount + SerializedFileCount;

        /// <summary>
        /// Total Unity asset container files discovered, including unreadable encrypted files.
        /// </summary>
        public int TotalAssetFileCount => ReadableAssetFileCount + EncryptedFileCount;

        public bool HasEncryptedFiles => EncryptedFileCount > 0;

        /// <summary>
        /// Percentage of discovered asset files that are readable.
        /// Guaranteed to be strictly less than 100.0 whenever any encrypted files remain unreadable.
        /// </summary>
        public double CoveragePercent => ComputeCoveragePercent(ReadableAssetFileCount, EncryptedFileCount);

        public static double ComputeCoveragePercent(int readableAssetFiles, int encryptedFiles)
        {
            int safeReadable = Math.Max(0, readableAssetFiles);
            int safeEncrypted = Math.Max(0, encryptedFiles);
            if (safeEncrypted <= 0)
            {
                return 100.0;
            }

            int total = safeReadable + safeEncrypted;
            if (total <= 0 || safeReadable <= 0)
            {
                return 0.0;
            }

            double raw = safeReadable * 100.0 / total;
            // Never round up to 100% while encrypted files remain locked.
            double flooredToTenth = Math.Floor(raw * 10.0) / 10.0;
            return Math.Clamp(flooredToTenth, 0.0, 99.9);
        }

        public static double ScaleProgressToCoverage(double stagePercent, int readableAssetFiles, int encryptedFiles)
        {
            double clampedStage = Math.Clamp(stagePercent, 0.0, 100.0);
            if (encryptedFiles <= 0)
            {
                return clampedStage;
            }

            double cap = ComputeCoveragePercent(readableAssetFiles, encryptedFiles);
            if (cap <= 0.0)
            {
                return 0.0;
            }

            double scaled = (clampedStage / 100.0) * cap;
            double flooredToTenth = Math.Floor(scaled * 10.0) / 10.0;
            return Math.Clamp(flooredToTenth, 0.0, Math.Min(99.9, cap));
        }

        /// <summary>
        /// Estimated RAM needed to load all Unity bundles in this project.
        /// Uses a conservative multiplier over the sum of bundle file sizes on disk,
        /// because decompressed data + C# object graphs typically cost 5–10x the compressed size.
        /// </summary>
        public long EstimatedMemoryBytes { get; internal set; }

        public bool IsRisky => UnityBundleCount >= ProjectScanner.RiskyUnityBundleCount
                            || IsMemoryRisky;

        /// <summary>
        /// True when estimated memory exceeds available physical RAM.
        /// </summary>
        public bool IsMemoryRisky { get; internal set; }

        public long AvailableMemoryBytes { get; internal set; }
    }

    public static class ProjectScanner
    {
        public const int RiskyUnityBundleCount = 1000;
        private const int MaxSamples = 20;
        private const int ProgressReportInterval = 200;

        // Decompressed RAM usage is estimated at 6x size on disk.
        private const double MemoryMultiplier = 6.0;

        // Matches "group_name_<32hex>.bundle" or pure "<32hex>"
        private static readonly Regex AddressablesHashRegex = new Regex(
            @"^(?:(?<group>.+?)_)?[0-9a-f]{32}(?:\..+)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AddressablesPlatformSegment = new Regex(
            @"(?:^|[\\/])(?:Standalone(?:Linux|Windows|OSX)\d*|Android|iOS|WebGL|Switch|PS[45]|XboxOne)(?:[\\/]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ProjectScanResult ScanFolder(
            string path,
            CancellationToken cancellationToken = default,
            IProgress<ScanProgress>? progress = null)
        {
            var result = new ProjectScanResult
            {
                RootPath = Path.GetFullPath(path)
            };

            var files = ImportHelper.GetFilesSafe(path, "*.*", true);
            int totalFilesCount = files.Length;

            var filesBag = new ConcurrentBag<ScannedFileInfo>();
            var sampleBundles = new ConcurrentBag<string>();
            var encryptionBarriers = new ConcurrentBag<StreamEncryptionBarrier>();

            int totalFiles = 0;
            long totalBytes = 0;
            int unityBundleCount = 0;
            int serializedFileCount = 0;
            int resourceFileCount = 0;
            int encryptedFileCount = 0;
            int otherFileCount = 0;
            int errorCount = 0;
            long bundleBytesOnDisk = 0;

            int scannedCount = 0;
            int lastReportedCount = 0;

            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_SCAN_THREADS", 0.6)
            };

            System.Threading.Tasks.Parallel.ForEach(files, parallelOptions, file =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new ScannedFileInfo { Path = file };
                bool isError = false;
                long length = 0;
                ProjectFileKind kind = ProjectFileKind.Other;
                string? bundleSig = null;
                uint bundleVer = 0;
                string? bundleUnityVer = null;
                string? appliedProvider = null;
                StreamEncryptionBarrier? barrier = null;

                try
                {
                    var info = new FileInfo(file);
                    length = info.Length;

                    var detection = DetectFileKindExtended(file, length);
                    kind = detection.Kind;
                    bundleSig = detection.BundleSignature;
                    bundleVer = detection.BundleVersion;
                    bundleUnityVer = detection.BundleUnityVersion;
                    appliedProvider = detection.AppliedDecryptionProvider;
                    barrier = detection.EncryptionBarrier;
                }
                catch (IOException)
                {
                    isError = true;
                }
                catch (UnauthorizedAccessException)
                {
                    isError = true;
                }

                fileInfo.Size = length;
                fileInfo.Kind = kind;
                fileInfo.BundleSignature = bundleSig;
                fileInfo.BundleVersion = bundleVer;
                fileInfo.BundleUnityVersion = bundleUnityVer;
                fileInfo.AddressablesGroup = InferAddressablesGroup(file);
                fileInfo.AppliedDecryptionProvider = appliedProvider;
                fileInfo.EncryptionBarrier = barrier;

                filesBag.Add(fileInfo);

                Interlocked.Increment(ref totalFiles);
                if (isError)
                {
                    Interlocked.Increment(ref errorCount);
                }
                else
                {
                    Interlocked.Add(ref totalBytes, length);
                    switch (kind)
                    {
                        case ProjectFileKind.UnityBundle:
                            Interlocked.Increment(ref unityBundleCount);
                            Interlocked.Add(ref bundleBytesOnDisk, length);
                            if (sampleBundles.Count < MaxSamples)
                            {
                                sampleBundles.Add(file);
                            }
                            break;
                        case ProjectFileKind.SerializedFile:
                            Interlocked.Increment(ref serializedFileCount);
                            break;
                        case ProjectFileKind.ResourceFile:
                            Interlocked.Increment(ref resourceFileCount);
                            break;
                        case ProjectFileKind.Encrypted:
                            Interlocked.Increment(ref encryptedFileCount);
                            if (barrier.HasValue && encryptionBarriers.Count < 200)
                            {
                                encryptionBarriers.Add(barrier.Value);
                            }
                            break;
                        default:
                            Interlocked.Increment(ref otherFileCount);
                            break;
                    }
                }

                var currentScanned = Interlocked.Increment(ref scannedCount);
                if (progress != null)
                {
                    var lastReported = Volatile.Read(ref lastReportedCount);
                    if (currentScanned - lastReported >= ProgressReportInterval)
                    {
                        if (Interlocked.CompareExchange(ref lastReportedCount, currentScanned, lastReported) == lastReported)
                        {
                            progress.Report(new ScanProgress
                            {
                                ScannedFiles = currentScanned,
                                TotalFiles = totalFilesCount,
                                ScannedBytes = Volatile.Read(ref totalBytes),
                                UnityBundleCount = Volatile.Read(ref unityBundleCount),
                                EncryptedFileCount = Volatile.Read(ref encryptedFileCount)
                            });
                        }
                    }
                }
            });

            result.TotalFiles = totalFiles;
            result.TotalBytes = totalBytes;
            result.UnityBundleCount = unityBundleCount;
            result.SerializedFileCount = serializedFileCount;
            result.ResourceFileCount = resourceFileCount;
            result.EncryptedFileCount = encryptedFileCount;
            result.OtherFileCount = otherFileCount;
            result.ErrorCount = errorCount;

            result.Files.AddRange(filesBag);
            result.SampleUnityBundles.AddRange(sampleBundles.Take(MaxSamples));
            result.EncryptionBarriers.AddRange(
                encryptionBarriers.OrderBy(static b => b.FilePath, StringComparer.OrdinalIgnoreCase));

            result.EstimatedMemoryBytes = (long)(bundleBytesOnDisk * MemoryMultiplier);
            result.AvailableMemoryBytes = GetAvailableMemoryBytes();
            result.IsMemoryRisky = result.AvailableMemoryBytes > 0
                                && result.EstimatedMemoryBytes > result.AvailableMemoryBytes;

            progress?.Report(new ScanProgress
            {
                ScannedFiles = totalFiles,
                TotalFiles = totalFiles,
                ScannedBytes = totalBytes,
                UnityBundleCount = unityBundleCount,
                EncryptedFileCount = encryptedFileCount
            });

            return result;
        }

        private static long GetAvailableMemoryBytes()
        {
            try
            {
                var memInfo = GC.GetGCMemoryInfo();
                if (memInfo.TotalAvailableMemoryBytes > 0)
                    return memInfo.TotalAvailableMemoryBytes;
            }
            catch
            {
                // Silent fallback
            }

            // Linux fallback: read MemAvailable from /proc/meminfo
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    foreach (var line in File.ReadLines("/proc/meminfo"))
                    {
                        if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                        {
                            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                                return kb * 1024;
                        }
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        #region File detection

        private struct FileDetectionResult
        {
            public ProjectFileKind Kind;
            public string? BundleSignature;
            public uint BundleVersion;
            public string? BundleUnityVersion;
            public string? AppliedDecryptionProvider;
            public StreamEncryptionBarrier? EncryptionBarrier;
        }

        private static FileDetectionResult DetectFileKindExtended(string path, long fileSize)
        {
            var result = new FileDetectionResult { Kind = ProjectFileKind.Other };

            if (fileSize < 4)
                return result;

            var headerSize = (int)Math.Min(UnityHeaderProbe.DefaultProbeBufferSize, fileSize);
            var header = ArrayPool<byte>.Shared.Rent(headerSize);
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read = stream.Read(header, 0, headerSize);

                var span = header.AsSpan(0, read);
                if (UnityHeaderProbe.TryParseUnityBundleHeader(span, 0, fileSize, out var sig, out var ver, out var unityVer, out _))
                {
                    result.Kind = ProjectFileKind.UnityBundle;
                    result.BundleSignature = sig;
                    result.BundleVersion = ver;
                    result.BundleUnityVersion = unityVer;
                    return result;
                }

                if (UnityHeaderProbe.IsSerializedFileHeader(span, fileSize))
                {
                    result.Kind = ProjectFileKind.SerializedFile;
                    return result;
                }

                // Check active decryption pipeline (Block512 catalog, declarative profiles, offset skip, repeating XOR, local plugins)
                // and barrier detection using the already-open stream.
                stream.Position = 0;
                var transformed = DecryptionHelper.CheckAndDecryptStream(stream, path, out var providerId, out var barrier);
                if (transformed != null && !ReferenceEquals(transformed, stream))
                {
                    try
                    {
                        transformed.Position = 0;
                        int decProbeLen = (int)Math.Min(headerSize, transformed.Length);
                        int decRead = 0;
                        while (decRead < decProbeLen)
                        {
                            int n = transformed.Read(header, decRead, decProbeLen - decRead);
                            if (n <= 0) break;
                            decRead += n;
                        }

                        var decSpan = header.AsSpan(0, decRead);
                        if (UnityHeaderProbe.TryParseUnityBundleHeader(decSpan, 0, transformed.Length, out sig, out ver, out unityVer, out _))
                        {
                            result.Kind = ProjectFileKind.UnityBundle;
                            result.BundleSignature = sig;
                            result.BundleVersion = ver;
                            result.BundleUnityVersion = unityVer;
                            result.AppliedDecryptionProvider = providerId;
                            return result;
                        }

                        if (UnityHeaderProbe.IsSerializedFileHeader(decSpan, transformed.Length))
                        {
                            result.Kind = ProjectFileKind.SerializedFile;
                            result.AppliedDecryptionProvider = providerId;
                            return result;
                        }
                    }
                    finally
                    {
                        transformed.Dispose();
                    }
                }
                else if (read >= 26 && UnityHeaderProbe.MatchesAt(span, 0, DecryptionHelper.EncryptionMagicBytes))
                {
                    // Legacy Block512 container header when catalog.json is not adjacent
                    result.Kind = ProjectFileKind.UnityBundle;
                    result.BundleSignature = "Encryption";
                    return result;
                }
                else if (barrier.HasValue)
                {
                    result.Kind = ProjectFileKind.Encrypted;
                    result.EncryptionBarrier = barrier.Value;
                    return result;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }

            var extension = Path.GetExtension(path);
            if (extension.Equals(".resS", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".resource", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = ProjectFileKind.ResourceFile;
            }

            return result;
        }

        #endregion

        #region Addressables group inference

        private static string? InferAddressablesGroup(string filePath)
        {
            if (!AddressablesPlatformSegment.IsMatch(filePath))
                return null;

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
                return null;

            var match = AddressablesHashRegex.Match(fileName);
            if (!match.Success)
                return null;

            var group = match.Groups["group"];
            if (group.Success && group.Length > 0)
                return group.Value;

            // Pure hash - use parent directory name as a group hint
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var dirName = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(dirName)
                    && !AddressablesPlatformSegment.IsMatch(dirName))
                {
                    return dirName;
                }
            }

            return null;
        }

        #endregion
    }
}
