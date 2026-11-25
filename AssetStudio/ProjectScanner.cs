using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        public ScannedFileInfo()
        {
        }

        public ScannedFileInfo(
            string path,
            long size,
            ProjectFileKind kind,
            long lastWriteTimeUtcTicks = 0,
            ulong contentSampleHash = 0,
            string? appliedDecryptionProvider = null)
        {
            Path = path ?? string.Empty;
            Size = size;
            Kind = kind;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            ContentSampleHash = contentSampleHash;
            AppliedDecryptionProvider = appliedDecryptionProvider;
        }

        public string Path { get; internal set; } = string.Empty;
        public long Size { get; internal set; }
        public long LastWriteTimeUtcTicks { get; internal set; }
        public ulong ContentSampleHash { get; internal set; }
        public ProjectFileKind Kind { get; internal set; }
        public string? BundleSignature { get; internal set; }
        public uint BundleVersion { get; internal set; }
        public string? BundleUnityVersion { get; internal set; }
        public string? AddressablesGroup { get; internal set; }
        public string? AppliedDecryptionProvider { get; internal set; }
        public StreamEncryptionBarrier? EncryptionBarrier { get; internal set; }
    }

    public sealed class ProjectRevalidationResult
    {
        public bool IsUnchanged { get; internal set; }
        public bool DecryptionContextChanged { get; internal set; }
        public List<string> AddedFiles { get; } = new List<string>();
        public List<string> ModifiedFiles { get; } = new List<string>();
        public List<string> DeletedFiles { get; } = new List<string>();
        public ProjectScanResult CurrentScanResult { get; internal set; } = new ProjectScanResult();
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
        public ProjectScanResult()
        {
        }

        public ProjectScanResult(string rootPath, IEnumerable<ScannedFileInfo> files, string? decryptionContextDigest = null)
        {
            RootPath = rootPath ?? string.Empty;
            DecryptionContextDigest = decryptionContextDigest;
            if (files == null)
            {
                return;
            }

            foreach (var file in files)
            {
                if (file == null)
                {
                    continue;
                }

                Files.Add(file);
                TotalFiles++;
                TotalBytes += file.Size;
                switch (file.Kind)
                {
                    case ProjectFileKind.UnityBundle:
                        UnityBundleCount++;
                        break;
                    case ProjectFileKind.SerializedFile:
                        SerializedFileCount++;
                        break;
                    case ProjectFileKind.ResourceFile:
                        ResourceFileCount++;
                        break;
                    case ProjectFileKind.Encrypted:
                        EncryptedFileCount++;
                        break;
                    default:
                        OtherFileCount++;
                        break;
                }
            }
        }

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
        public string? DecryptionContextDigest { get; internal set; }

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
                long lastWriteUtcTicks = 0;
                ulong contentSampleHash = 0;
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
                    lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;

                    var detection = DetectFileKindExtended(file, length);
                    kind = detection.Kind;
                    contentSampleHash = detection.ContentSampleHash;
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
                fileInfo.LastWriteTimeUtcTicks = lastWriteUtcTicks;
                fileInfo.ContentSampleHash = contentSampleHash;
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
                encryptionBarriers.OrderBy(static b => b.FilePath, OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase));
            result.DecryptionContextDigest = ComputeDecryptionContextDigest();

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

        /// <summary>
        /// Fast O(N) folder revalidation against a prior <see cref="ProjectScanResult"/> snapshot (PERF-11).
        /// Checks file additions, deletions, (Length, LastWriteTimeUtc.Ticks) drift, known in-flight dirty paths,
        /// racy-clean recent writes (bounded &lt;= 12 KiB xxHash64 sample), and active decryption context changes.
        /// Re-probes only added or modified files when constructing <see cref="ProjectRevalidationResult.CurrentScanResult"/>.
        /// </summary>
        public static ProjectRevalidationResult RevalidateFolder(
            string path,
            ProjectScanResult baselineScan,
            IReadOnlyCollection<string>? knownDirtyPaths = null,
            bool verifyAllContentSamples = false,
            CancellationToken cancellationToken = default)
        {
            return RevalidateFolder(
                path,
                baselineScan,
                knownDirtyPaths,
                verifyAllContentSamples,
                caseSensitivePaths: OperatingSystem.IsLinux(),
                cancellationToken);
        }

        public static ProjectRevalidationResult RevalidateFolder(
            string path,
            ProjectScanResult baselineScan,
            IReadOnlyCollection<string>? knownDirtyPaths,
            bool verifyAllContentSamples,
            bool caseSensitivePaths,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(baselineScan);

            var revalidation = new ProjectRevalidationResult();
            var currentDecryptionDigest = ComputeDecryptionContextDigest();
            var decryptionChanged = !string.Equals(
                baselineScan.DecryptionContextDigest ?? string.Empty,
                currentDecryptionDigest ?? string.Empty,
                StringComparison.Ordinal);
            revalidation.DecryptionContextChanged = decryptionChanged;

            if (decryptionChanged)
            {
                var fresh = ScanFolder(path, cancellationToken);
                revalidation.IsUnchanged = false;
                revalidation.CurrentScanResult = fresh;
                foreach (var f in fresh.Files)
                {
                    revalidation.ModifiedFiles.Add(f.Path);
                }
                return revalidation;
            }

            var pathComparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var baselineByKey = new Dictionary<string, ScannedFileInfo>(baselineScan.Files.Count, pathComparer);
            foreach (var item in baselineScan.Files)
            {
                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    baselineByKey[NormalizePathKey(item.Path)] = item;
                }
            }

            HashSet<string>? dirtyKeySet = null;
            if (knownDirtyPaths != null && knownDirtyPaths.Count > 0)
            {
                dirtyKeySet = new HashSet<string>(pathComparer);
                foreach (var dirtyPath in knownDirtyPaths)
                {
                    if (!string.IsNullOrWhiteSpace(dirtyPath))
                    {
                        dirtyKeySet.Add(NormalizePathKey(dirtyPath));
                    }
                }
            }

            var seenBaselineKeys = new HashSet<string>(pathComparer);
            var unchangedFiles = new List<ScannedFileInfo>(baselineScan.Files.Count);
            var filesToReprobe = new List<string>();
            var nowUtcTicks = DateTime.UtcNow.Ticks;
            var racyCleanWindowTicks = TimeSpan.FromSeconds(2).Ticks;

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var fileEntries = new FileSystemEnumerable<(string FullPath, long Length, long LastWriteUtcTicks)>(
                path,
                (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.Ticks),
                enumOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
            };

            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = entry.FullPath;
                var key = NormalizePathKey(file);

                if (!baselineByKey.TryGetValue(key, out var baselineFile))
                {
                    revalidation.AddedFiles.Add(file);
                    filesToReprobe.Add(file);
                    continue;
                }

                seenBaselineKeys.Add(key);

                if (dirtyKeySet != null && dirtyKeySet.Contains(key))
                {
                    revalidation.ModifiedFiles.Add(file);
                    filesToReprobe.Add(file);
                    continue;
                }

                bool changed = false;
                try
                {
                    var currentLen = entry.Length;
                    var currentMtimeTicks = entry.LastWriteUtcTicks;
                    if (currentLen != baselineFile.Size || currentMtimeTicks != baselineFile.LastWriteTimeUtcTicks)
                    {
                        changed = true;
                    }
                    else if (verifyAllContentSamples || Math.Abs(nowUtcTicks - currentMtimeTicks) <= racyCleanWindowTicks)
                    {
                        var liveSampleHash = XxHash64.ComputeHeadFileSampleHash(file, currentLen);
                        if (liveSampleHash != baselineFile.ContentSampleHash)
                        {
                            changed = true;
                        }
                    }
                }
                catch (IOException)
                {
                    changed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    changed = true;
                }

                if (changed)
                {
                    revalidation.ModifiedFiles.Add(file);
                    filesToReprobe.Add(file);
                }
                else
                {
                    unchangedFiles.Add(baselineFile);
                }
            }

            foreach (var kvp in baselineByKey)
            {
                if (!seenBaselineKeys.Contains(kvp.Key))
                {
                    revalidation.DeletedFiles.Add(kvp.Value.Path);
                }
            }

            if (revalidation.AddedFiles.Count == 0
                && revalidation.ModifiedFiles.Count == 0
                && revalidation.DeletedFiles.Count == 0)
            {
                revalidation.IsUnchanged = true;
                revalidation.CurrentScanResult = baselineScan;
                return revalidation;
            }

            revalidation.IsUnchanged = false;
            var updated = new ProjectScanResult
            {
                RootPath = Path.GetFullPath(path),
                DecryptionContextDigest = currentDecryptionDigest
            };

            var reprobedBag = new ConcurrentBag<(ScannedFileInfo Info, bool IsError)>();
            if (filesToReprobe.Count > 0)
            {
                var parallelOptions = new System.Threading.Tasks.ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_SCAN_THREADS", 0.6)
                };

                System.Threading.Tasks.Parallel.ForEach(filesToReprobe, parallelOptions, file =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scanned = ProbeSingleFile(file, out var isError);
                    reprobedBag.Add((scanned, isError));
                });
            }

            long totalBytes = 0;
            long bundleBytesOnDisk = 0;
            int unityBundleCount = 0;
            int serializedFileCount = 0;
            int resourceFileCount = 0;
            int encryptedFileCount = 0;
            int otherFileCount = 0;
            int errorCount = 0;
            var barriers = new List<StreamEncryptionBarrier>();

            void AccumulateFile(ScannedFileInfo fi, bool isError)
            {
                updated.Files.Add(fi);
                if (isError)
                {
                    errorCount++;
                    return;
                }

                totalBytes += fi.Size;
                switch (fi.Kind)
                {
                    case ProjectFileKind.UnityBundle:
                        unityBundleCount++;
                        bundleBytesOnDisk += fi.Size;
                        if (updated.SampleUnityBundles.Count < MaxSamples)
                        {
                            updated.SampleUnityBundles.Add(fi.Path);
                        }
                        break;
                    case ProjectFileKind.SerializedFile:
                        serializedFileCount++;
                        break;
                    case ProjectFileKind.ResourceFile:
                        resourceFileCount++;
                        break;
                    case ProjectFileKind.Encrypted:
                        encryptedFileCount++;
                        if (fi.EncryptionBarrier.HasValue && barriers.Count < 200)
                        {
                            barriers.Add(fi.EncryptionBarrier.Value);
                        }
                        break;
                    default:
                        otherFileCount++;
                        break;
                }
            }

            foreach (var unchanged in unchangedFiles)
            {
                AccumulateFile(unchanged, isError: false);
            }

            foreach (var (reprobed, isError) in reprobedBag)
            {
                AccumulateFile(reprobed, isError);
            }

            updated.TotalFiles = updated.Files.Count;
            updated.TotalBytes = totalBytes;
            updated.UnityBundleCount = unityBundleCount;
            updated.SerializedFileCount = serializedFileCount;
            updated.ResourceFileCount = resourceFileCount;
            updated.EncryptedFileCount = encryptedFileCount;
            updated.OtherFileCount = otherFileCount;
            updated.ErrorCount = errorCount;
            updated.EncryptionBarriers.AddRange(barriers.OrderBy(static b => b.FilePath, pathComparer));
            updated.EstimatedMemoryBytes = (long)(bundleBytesOnDisk * MemoryMultiplier);
            updated.AvailableMemoryBytes = GetAvailableMemoryBytes();
            updated.IsMemoryRisky = updated.AvailableMemoryBytes > 0
                                 && updated.EstimatedMemoryBytes > updated.AvailableMemoryBytes;

            revalidation.CurrentScanResult = updated;
            return revalidation;
        }

        private static ScannedFileInfo ProbeSingleFile(string file, out bool isError)
        {
            var fileInfo = new ScannedFileInfo { Path = file };
            isError = false;
            long length = 0;
            long lastWriteUtcTicks = 0;
            ulong contentSampleHash = 0;
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
                lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;

                var detection = DetectFileKindExtended(file, length);
                kind = detection.Kind;
                contentSampleHash = detection.ContentSampleHash;
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
            fileInfo.LastWriteTimeUtcTicks = lastWriteUtcTicks;
            fileInfo.ContentSampleHash = contentSampleHash;
            fileInfo.Kind = kind;
            fileInfo.BundleSignature = bundleSig;
            fileInfo.BundleVersion = bundleVer;
            fileInfo.BundleUnityVersion = bundleUnityVer;
            fileInfo.AddressablesGroup = InferAddressablesGroup(file);
            fileInfo.AppliedDecryptionProvider = appliedProvider;
            fileInfo.EncryptionBarrier = barrier;
            return fileInfo;
        }

        private static string NormalizePathKey(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        public static string? ComputeDecryptionContextDigest()
        {
            var profiles = DecryptionHelper.GetRegisteredProfiles();
            var customProviders = DecryptionHelper.GetCustomProviders();

            List<DecryptionProfile>? enabledProfiles = null;
            for (var i = 0; i < profiles.Count; i++)
            {
                if (profiles[i].Enabled)
                {
                    enabledProfiles ??= new List<DecryptionProfile>();
                    enabledProfiles.Add(profiles[i]);
                }
            }

            if ((enabledProfiles == null || enabledProfiles.Count == 0) && customProviders.Count == 0)
            {
                return null;
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendUtf8(hasher, "decryption-ctx-v1\0");

            if (enabledProfiles != null && enabledProfiles.Count > 0)
            {
                enabledProfiles.Sort(static (a, b) => string.CompareOrdinal(a.Id ?? string.Empty, b.Id ?? string.Empty));
                foreach (var p in enabledProfiles)
                {
                    AppendUtf8(hasher, "P|");
                    AppendUtf8(hasher, p.Id ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.Algorithm ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.FileNamePattern ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.PathPattern ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.MagicHex ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.MagicOffset.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.MinFileSize.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.SizeMultiple.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.SkipBytes.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.XorKeyHex ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.KeyHex ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.KeyUtf8 ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.IvHex ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.NonceHex ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.IvUtf8 ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.IvFromStreamPrefix ? "1" : "0");
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.IvFromFileNameHash ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.UseFileNameAsSalt ? "1" : "0");
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.UseFileNameWithoutExtension ? "1" : "0");
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.Padding ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.CounterStart.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.CounterLittleEndian ? "1" : "0");
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.KeyDerivation ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.SaltHex ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.SaltUtf8 ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.KdfIterations.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.DerivedKeyLength.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.RangeStart.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.RangeLength.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.CatalogPath ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.Block512Token ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, p.BlockSizeBits.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "\0");
                }
            }

            if (customProviders.Count > 0)
            {
                var orderedProviders = customProviders
                    .OrderBy(static p => p.Id ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static p => p.Priority);
                foreach (var provider in orderedProviders)
                {
                    AppendUtf8(hasher, "C|");
                    AppendUtf8(hasher, provider.Id ?? string.Empty);
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, provider.Priority.ToString(CultureInfo.InvariantCulture));
                    AppendUtf8(hasher, "|");
                    AppendUtf8(hasher, provider.GetType().FullName ?? string.Empty);
                    AppendUtf8(hasher, "\0");
                }
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }

        private static void AppendUtf8(IncrementalHash hasher, string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            hasher.AppendData(bytes);
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
            public ulong ContentSampleHash;
            public string? BundleSignature;
            public uint BundleVersion;
            public string? BundleUnityVersion;
            public string? AppliedDecryptionProvider;
            public StreamEncryptionBarrier? EncryptionBarrier;
        }

        private static FileDetectionResult DetectFileKindExtended(string path, long fileSize)
        {
            var result = new FileDetectionResult { Kind = ProjectFileKind.Other };

            if (fileSize <= 0)
            {
                result.ContentSampleHash = XxHash64.ComputeFileSampleHash(path, 0, out _);
                var zeroExt = Path.GetExtension(path);
                if (zeroExt.Equals(".resS", StringComparison.OrdinalIgnoreCase)
                    || zeroExt.Equals(".resource", StringComparison.OrdinalIgnoreCase))
                {
                    result.Kind = ProjectFileKind.ResourceFile;
                }
                return result;
            }

            var headerSize = (int)Math.Min(UnityHeaderProbe.DefaultProbeBufferSize, fileSize);
            var header = ArrayPool<byte>.Shared.Rent(headerSize);
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read = XxHash64.ReadUpTo(stream, header, headerSize);
                var span = header.AsSpan(0, read);

                // Zero-seek content hash reusing the already-read 4 KiB header window
                // (zero extra seeks / zero extra reads per file during folder scanning).
                result.ContentSampleHash = XxHash64.ComputeHeadSampleHash(fileSize, span);

                if (fileSize < 4)
                {
                    var tinyExt = Path.GetExtension(path);
                    if (tinyExt.Equals(".resS", StringComparison.OrdinalIgnoreCase)
                        || tinyExt.Equals(".resource", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Kind = ProjectFileKind.ResourceFile;
                    }
                    return result;
                }

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
                else if (barrier.HasValue)
                {
                    result.Kind = ProjectFileKind.Encrypted;
                    result.EncryptionBarrier = barrier.Value;
                    return result;
                }
                else if (read >= 26 && UnityHeaderProbe.MatchesAt(span, 0, DecryptionHelper.EncryptionMagicBytes))
                {
                    // Unresolved Block512 container header when catalog could not resolve a token
                    var prefixHex = UnityHeaderProbe.FormatHexPrefix(span, 16);
                    var candidates = UnityHeaderProbe.AnalyzeEncryptionCandidates(span, fileSize, path, "Block512MissingCatalog");
                    result.Kind = ProjectFileKind.Encrypted;
                    result.EncryptionBarrier = new StreamEncryptionBarrier(
                        providerId: "Block512MissingCatalog",
                        filePath: path,
                        reason: $"Custom chained ChaCha12/8/4 512B-block stream XOR ('Encryption' 26B header) detected without a matching catalog (prefix: {prefixHex}), so the decryption token could not be derived.",
                        confidence: 0.98,
                        headerPrefixHex: prefixHex,
                        candidateAlgorithms: candidates);
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
