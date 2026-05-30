using System;
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
        ResourceFile
    }

    public sealed class ScannedFileInfo
    {
        public string Path { get; internal set; }
        public long Size { get; internal set; }
        public ProjectFileKind Kind { get; internal set; }
        public string BundleSignature { get; internal set; }
        public uint BundleVersion { get; internal set; }
        public string BundleUnityVersion { get; internal set; }
        public string AddressablesGroup { get; internal set; }
    }

    public struct ScanProgress
    {
        public int ScannedFiles { get; set; }
        /// <summary>-1 while enumeration is still running.</summary>
        public int TotalFiles { get; set; }
        public long ScannedBytes { get; set; }
        public int UnityBundleCount { get; set; }
    }

    public sealed class ProjectScanResult
    {
        public string RootPath { get; internal set; }
        public int TotalFiles { get; internal set; }
        public long TotalBytes { get; internal set; }
        public int UnityBundleCount { get; internal set; }
        public int SerializedFileCount { get; internal set; }
        public int ResourceFileCount { get; internal set; }
        public int OtherFileCount { get; internal set; }
        public int ErrorCount { get; internal set; }
        public List<string> SampleUnityBundles { get; } = new List<string>();
        public List<ScannedFileInfo> Files { get; } = new List<ScannedFileInfo>();

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
            IProgress<ScanProgress> progress = null)
        {
            var result = new ProjectScanResult
            {
                RootPath = Path.GetFullPath(path)
            };

            var files = ImportHelper.GetFilesSafe(path, "*.*", true);
            int totalFilesCount = files.Length;

            var filesBag = new ConcurrentBag<ScannedFileInfo>();
            var sampleBundles = new ConcurrentBag<string>();

            int totalFiles = 0;
            long totalBytes = 0;
            int unityBundleCount = 0;
            int serializedFileCount = 0;
            int resourceFileCount = 0;
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
                string bundleSig = null;
                uint bundleVer = 0;
                string bundleUnityVer = null;

                try
                {
                    var info = new FileInfo(file);
                    length = info.Length;

                    var detection = DetectFileKindExtended(file, length);
                    kind = detection.Kind;
                    bundleSig = detection.BundleSignature;
                    bundleVer = detection.BundleVersion;
                    bundleUnityVer = detection.BundleUnityVersion;
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
                                UnityBundleCount = Volatile.Read(ref unityBundleCount)
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
            result.OtherFileCount = otherFileCount;
            result.ErrorCount = errorCount;

            result.Files.AddRange(filesBag);
            result.SampleUnityBundles.AddRange(sampleBundles.Take(MaxSamples));

            result.EstimatedMemoryBytes = (long)(bundleBytesOnDisk * MemoryMultiplier);
            result.AvailableMemoryBytes = GetAvailableMemoryBytes();
            result.IsMemoryRisky = result.AvailableMemoryBytes > 0
                                && result.EstimatedMemoryBytes > result.AvailableMemoryBytes;

            progress?.Report(new ScanProgress
            {
                ScannedFiles = totalFiles,
                TotalFiles = totalFiles,
                ScannedBytes = totalBytes,
                UnityBundleCount = unityBundleCount
            });

            return result;
        }

        private static long GetAvailableMemoryBytes()
        {
#if NET6_0_OR_GREATER
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
#endif

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
            public string BundleSignature;
            public uint BundleVersion;
            public string BundleUnityVersion;
        }

        private static FileDetectionResult DetectFileKindExtended(string path, long fileSize)
        {
            var result = new FileDetectionResult { Kind = ProjectFileKind.Other };

            if (fileSize < 4)
                return result;

            var headerSize = (int)Math.Min(128, fileSize);
            var header = new byte[headerSize];
            int read;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = stream.Read(header, 0, header.Length);
            }

            string detectedSignature = null;
            if (StartsWithAscii(header, read, "UnityFS"))
                detectedSignature = "UnityFS";
            else if (StartsWithAscii(header, read, "UnityWeb"))
                detectedSignature = "UnityWeb";
            else if (StartsWithAscii(header, read, "UnityRaw"))
                detectedSignature = "UnityRaw";
            else if (StartsWithAscii(header, read, "UnityArchive"))
                detectedSignature = "UnityArchive";

            if (detectedSignature != null)
            {
                result.Kind = ProjectFileKind.UnityBundle;
                result.BundleSignature = detectedSignature;

                int pos = detectedSignature.Length + 1; // signature + null terminator
                if (pos + 4 <= read)
                {
                    result.BundleVersion = (uint)ReadUInt32BigEndian(header, pos);
                    pos += 4;
                    result.BundleUnityVersion = ReadNullTerminatedAscii(header, read, pos);
                }

                return result;
            }

            if (IsSerializedFileHeader(header, read, fileSize))
            {
                result.Kind = ProjectFileKind.SerializedFile;
                return result;
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

        private static string InferAddressablesGroup(string filePath)
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

        #region Binary helpers

        private static bool StartsWithAscii(byte[] bytes, int length, string value)
        {
            if (length < value.Length)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                if (bytes[i] != (byte)value[i])
                    return false;
            }

            return true;
        }

        private static string ReadNullTerminatedAscii(byte[] bytes, int length, int offset)
        {
            if (offset >= length)
                return null;

            int end = offset;
            while (end < length && bytes[end] != 0)
                end++;

            if (end == offset || end >= length)
                return null;

            return System.Text.Encoding.ASCII.GetString(bytes, offset, end - offset);
        }

        private static bool IsSerializedFileHeader(byte[] header, int length, long actualFileSize)
        {
            if (length < 20)
                return false;

            var metadataSize = ReadUInt32BigEndian(header, 0);
            var fileSize = ReadUInt32BigEndian(header, 4);
            var version = ReadUInt32BigEndian(header, 8);
            var dataOffset = ReadUInt32BigEndian(header, 12);

            if (version >= 22)
            {
                if (length < 48)
                    return false;

                metadataSize = ReadUInt32BigEndian(header, 20);
                fileSize = ReadInt64BigEndian(header, 24);
                dataOffset = ReadInt64BigEndian(header, 32);
            }

            return metadataSize > 0
                && fileSize == actualFileSize
                && dataOffset > 0
                && dataOffset <= actualFileSize;
        }

        private static long ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((long)bytes[offset] << 24)
                | ((long)bytes[offset + 1] << 16)
                | ((long)bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }

        private static long ReadInt64BigEndian(byte[] bytes, int offset)
        {
            return ((long)bytes[offset] << 56)
                | ((long)bytes[offset + 1] << 48)
                | ((long)bytes[offset + 2] << 40)
                | ((long)bytes[offset + 3] << 32)
                | ((long)bytes[offset + 4] << 24)
                | ((long)bytes[offset + 5] << 16)
                | ((long)bytes[offset + 6] << 8)
                | bytes[offset + 7];
        }

        #endregion
    }
}
