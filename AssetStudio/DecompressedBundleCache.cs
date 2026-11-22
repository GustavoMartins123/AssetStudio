#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AssetStudio
{
    public sealed class CachedBundleNode
    {
        public string Path { get; set; } = string.Empty;
        public long Offset { get; set; }
        public long Size { get; set; }
        public uint Flags { get; set; }
    }

    public sealed class DecompressedBundleEntry
    {
        public DecompressedBundleEntry(
            string bundleDirectory,
            string blocksFilePath,
            string manifestFilePath,
            long uncompressedSize,
            bool fromCache,
            IReadOnlyList<CachedBundleNode> nodes)
        {
            BundleDirectory = bundleDirectory;
            BlocksFilePath = blocksFilePath;
            ManifestFilePath = manifestFilePath;
            UncompressedSize = uncompressedSize;
            FromCache = fromCache;
            Nodes = nodes;
        }

        public string BundleDirectory { get; }
        public string BlocksFilePath { get; }
        public string ManifestFilePath { get; }
        public long UncompressedSize { get; }
        public bool FromCache { get; }
        public IReadOnlyList<CachedBundleNode> Nodes { get; }

        public StreamFile[] CreateStreamFiles()
        {
            return DecompressedBundleCache.CreateStreamFiles(BlocksFilePath, UncompressedSize, Nodes);
        }
    }

    public sealed class DecompressedBundleCacheStats
    {
        public DecompressedBundleCacheStats(
            int entryCount,
            long totalBytes,
            long cacheHits,
            long cacheMisses,
            long evictionCount,
            long evictedBytes)
        {
            EntryCount = entryCount;
            TotalBytes = totalBytes;
            CacheHits = cacheHits;
            CacheMisses = cacheMisses;
            EvictionCount = evictionCount;
            EvictedBytes = evictedBytes;
        }

        public int EntryCount { get; }
        public long TotalBytes { get; }
        public long CacheHits { get; }
        public long CacheMisses { get; }
        public long EvictionCount { get; }
        public long EvictedBytes { get; }
    }

    public sealed class DecompressedBundleEvictionResult
    {
        public DecompressedBundleEvictionResult(
            int evictedBundleCount,
            long evictedBytes,
            int failedDeleteCount,
            int remainingBundleCount,
            long remainingBytes)
        {
            EvictedBundleCount = evictedBundleCount;
            EvictedBytes = evictedBytes;
            FailedDeleteCount = failedDeleteCount;
            RemainingBundleCount = remainingBundleCount;
            RemainingBytes = remainingBytes;
        }

        public int EvictedBundleCount { get; }
        public long EvictedBytes { get; }
        public int FailedDeleteCount { get; }
        public int RemainingBundleCount { get; }
        public long RemainingBytes { get; }
    }

    public sealed class DecompressedBundleCache
    {
        public const long DefaultMaxTotalBytes = 4L * 1024 * 1024 * 1024; // 4 GiB
        private const int ManifestVersion = 1;
        private const int StreamingBufferSize = 1024 * 1024; // 1 MiB
        private const string BlocksFileName = "blocks.bin";
        private const string ManifestFileName = "manifest.json";
        private const string ReadyMarkerFileName = ".ready";

        private static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            WriteIndented = false
        };

        private static long s_lastUtcTicks;

        private readonly Dictionary<string, RefCountedSemaphore> _keyedLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _keyedLocksGate = new();
        private readonly object _evictionLock = new();
        private readonly ConcurrentDictionary<string, long> _lastAccessTicks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CachedEntryState> _entries = new(StringComparer.OrdinalIgnoreCase);
        private bool _indexInitialized;
        private long _trackedTotalBytes;
        private long _diskEnumerations;

        private long _maxTotalBytes;
        private long _cacheHits;
        private long _cacheMisses;
        private long _evictionCount;
        private long _evictedBytes;

        public DecompressedBundleCache(
            string? cacheBaseDirectory = null,
            long maxTotalBytes = DefaultMaxTotalBytes,
            string? gameScopePath = null)
        {
            if (maxTotalBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Max total bytes must be non-negative.");
            }

            CacheBaseDirectory = string.IsNullOrWhiteSpace(cacheBaseDirectory)
                ? GetDefaultCacheBaseDirectory()
                : Path.GetFullPath(cacheBaseDirectory);
            _maxTotalBytes = maxTotalBytes;
            GameScopePath = string.IsNullOrWhiteSpace(gameScopePath) ? null : NormalizePath(gameScopePath);

            Directory.CreateDirectory(CacheBaseDirectory);
        }

        public string CacheBaseDirectory { get; }
        public string? GameScopePath { get; }

        public long MaxTotalBytes
        {
            get => Interlocked.Read(ref _maxTotalBytes);
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Max total bytes must be non-negative.");
                }

                Interlocked.Exchange(ref _maxTotalBytes, value);
            }
        }

        public long CacheHits => Interlocked.Read(ref _cacheHits);
        public long CacheMisses => Interlocked.Read(ref _cacheMisses);
        public long EvictionCount => Interlocked.Read(ref _evictionCount);
        public long EvictedBytes => Interlocked.Read(ref _evictedBytes);
        public long DiskEnumerationCount => Interlocked.Read(ref _diskEnumerations);

        public static string GetDefaultCacheBaseDirectory()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "AssetStudio", "DecompressedCache");
            }

            return Path.Combine(Path.GetTempPath(), "AssetStudio", "DecompressedCache");
        }

        public string GetBundleCacheDirectory(string sourceBundlePath, string? gameScopePath = null)
        {
            if (string.IsNullOrWhiteSpace(sourceBundlePath))
            {
                throw new ArgumentException("Source bundle path must not be empty.", nameof(sourceBundlePath));
            }

            var normalizedBundlePath = NormalizePath(sourceBundlePath);
            var sourceInfo = new FileInfo(normalizedBundlePath);
            if (!sourceInfo.Exists)
            {
                throw new FileNotFoundException($"Bundle file not found: {sourceBundlePath}", sourceBundlePath);
            }

            return GetBundleCacheDirectory(
                normalizedBundlePath,
                sourceInfo.Length,
                sourceInfo.LastWriteTimeUtc.Ticks,
                gameScopePath);
        }

        public DecompressedBundleEntry GetOrExtractBlocks(
            string sourceBundlePath,
            long expectedUncompressedSize,
            Action<Stream> writeDecompressedBlocks,
            IReadOnlyList<CachedBundleNode>? directoryNodes = null,
            string? gameScopePath = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceBundlePath))
            {
                throw new ArgumentException("Source bundle path must not be empty.", nameof(sourceBundlePath));
            }

            if (writeDecompressedBlocks == null)
            {
                throw new ArgumentNullException(nameof(writeDecompressedBlocks));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var normalizedBundlePath = NormalizePath(sourceBundlePath);
            var sourceInfo = new FileInfo(normalizedBundlePath);
            if (!sourceInfo.Exists)
            {
                throw new FileNotFoundException($"Bundle file not found: {sourceBundlePath}", sourceBundlePath);
            }

            var sourceLength = sourceInfo.Length;
            var sourceLastWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
            var bundleDir = GetBundleCacheDirectory(normalizedBundlePath, sourceLength, sourceLastWriteTicks, gameScopePath);
            var blocksPath = Path.Combine(bundleDir, BlocksFileName);
            var manifestPath = Path.Combine(bundleDir, ManifestFileName);
            var readyPath = Path.Combine(bundleDir, ReadyMarkerFileName);

            DecompressedBundleEntry result;
            var wroteNewEntry = false;
            long writtenEntryTotalBytes = 0;
            long writtenEntryTicks = 0;

            using (AcquireBundleLock(bundleDir, cancellationToken))
            {
                if (TryValidateExistingEntry(
                    blocksPath,
                    manifestPath,
                    readyPath,
                    sourceLength,
                    sourceLastWriteTicks,
                    expectedUncompressedSize,
                    out var existingManifest,
                    out var actualBlocksLength))
                {
                    Interlocked.Increment(ref _cacheHits);
                    TouchAccessTimestamp(bundleDir, readyPath);
                    var hitNodes = existingManifest?.Nodes ?? (directoryNodes != null ? new List<CachedBundleNode>(directoryNodes) : new List<CachedBundleNode>());
                    return new DecompressedBundleEntry(
                        bundleDir,
                        blocksPath,
                        manifestPath,
                        actualBlocksLength,
                        fromCache: true,
                        hitNodes);
                }

                Interlocked.Increment(ref _cacheMisses);
                Directory.CreateDirectory(bundleDir);

                var uniqueSuffix = Guid.NewGuid().ToString("N");
                var tempBlocksPath = Path.Combine(bundleDir, $"{BlocksFileName}.{uniqueSuffix}.tmp");
                var tempManifestPath = Path.Combine(bundleDir, $"{ManifestFileName}.{uniqueSuffix}.tmp");
                var tempReadyPath = Path.Combine(bundleDir, $"{ReadyMarkerFileName}.{uniqueSuffix}.tmp");

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    long writtenLength;
                    using (var tempStream = new FileStream(
                        tempBlocksPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        StreamingBufferSize,
                        FileOptions.SequentialScan))
                    {
                        writeDecompressedBlocks(tempStream);
                        cancellationToken.ThrowIfCancellationRequested();
                        tempStream.Flush();
                        writtenLength = tempStream.Length;
                    }

                    if (expectedUncompressedSize >= 0 && writtenLength != expectedUncompressedSize)
                    {
                        throw new InvalidDataException(
                            $"Decompressed bundle length mismatch for '{sourceBundlePath}': expected {expectedUncompressedSize} bytes, wrote {writtenLength} bytes.");
                    }

                    var nowTicks = NextMonotonicUtcTicks();
                    var nodeList = directoryNodes != null
                        ? new List<CachedBundleNode>(directoryNodes)
                        : new List<CachedBundleNode>();
                    var manifest = new BundleCacheManifest
                    {
                        Version = ManifestVersion,
                        SourcePath = normalizedBundlePath,
                        SourceLength = sourceLength,
                        SourceLastWriteTimeUtcTicks = sourceLastWriteTicks,
                        UncompressedSize = writtenLength,
                        CreatedUtcTicks = nowTicks,
                        LastAccessedUtcTicks = nowTicks,
                        Nodes = nodeList
                    };

                    var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
                    var readyBytes = Encoding.ASCII.GetBytes(nowTicks.ToString(CultureInfo.InvariantCulture));
                    File.WriteAllBytes(tempManifestPath, manifestBytes);
                    File.WriteAllBytes(tempReadyPath, readyBytes);

                    cancellationToken.ThrowIfCancellationRequested();

                    File.Move(tempBlocksPath, blocksPath, overwrite: true);
                    File.Move(tempManifestPath, manifestPath, overwrite: true);
                    File.Move(tempReadyPath, readyPath, overwrite: true);

                    TouchAccessTimestamp(bundleDir, readyPath, nowTicks);
                    wroteNewEntry = true;
                    writtenEntryTotalBytes = writtenLength + manifestBytes.Length + readyBytes.Length;
                    writtenEntryTicks = nowTicks;

                    result = new DecompressedBundleEntry(
                        bundleDir,
                        blocksPath,
                        manifestPath,
                        writtenLength,
                        fromCache: false,
                        nodeList);
                }
                catch
                {
                    TryDeleteFile(tempBlocksPath);
                    TryDeleteFile(tempManifestPath);
                    TryDeleteFile(tempReadyPath);

                    if (!File.Exists(readyPath))
                    {
                        TryDeleteFile(blocksPath);
                        TryDeleteFile(manifestPath);
                        TryDeleteEmptyDirectory(bundleDir);
                    }

                    throw;
                }
            }

            if (wroteNewEntry)
            {
                RecordWrittenEntryAndEvictIfNeeded(bundleDir, writtenEntryTotalBytes, writtenEntryTicks);
            }

            return result;
        }

        public bool TryOpenCachedStreamFiles(
            string sourceBundlePath,
            out StreamFile[] streamFiles,
            string? gameScopePath = null)
        {
            streamFiles = Array.Empty<StreamFile>();
            if (string.IsNullOrWhiteSpace(sourceBundlePath) || !File.Exists(sourceBundlePath))
            {
                return false;
            }

            var normalizedBundlePath = NormalizePath(sourceBundlePath);
            var sourceInfo = new FileInfo(normalizedBundlePath);
            var sourceLength = sourceInfo.Length;
            var sourceLastWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
            var bundleDir = GetBundleCacheDirectory(normalizedBundlePath, sourceLength, sourceLastWriteTicks, gameScopePath);
            var blocksPath = Path.Combine(bundleDir, BlocksFileName);
            var manifestPath = Path.Combine(bundleDir, ManifestFileName);
            var readyPath = Path.Combine(bundleDir, ReadyMarkerFileName);

            using (AcquireBundleLock(bundleDir))
            {
                if (!TryValidateExistingEntry(
                    blocksPath,
                    manifestPath,
                    readyPath,
                    sourceLength,
                    sourceLastWriteTicks,
                    expectedUncompressedSize: -1,
                    out var manifest,
                    out var actualBlocksLength)
                    || manifest == null)
                {
                    return false;
                }

                Interlocked.Increment(ref _cacheHits);
                TouchAccessTimestamp(bundleDir, readyPath);
                streamFiles = CreateStreamFiles(blocksPath, actualBlocksLength, manifest.Nodes);
                return true;
            }
        }

        public static StreamFile[] CreateStreamFiles(
            string cachedBlocksPath,
            long blocksLength,
            IReadOnlyList<BundleFile.Node> nodes)
        {
            if (string.IsNullOrWhiteSpace(cachedBlocksPath))
            {
                throw new ArgumentException("Cached blocks path must not be empty.", nameof(cachedBlocksPath));
            }

            if (nodes == null)
            {
                throw new ArgumentNullException(nameof(nodes));
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.offset < 0 || node.size < 0 || node.offset > blocksLength - node.size)
                {
                    throw new InvalidDataException(
                        $"Invalid bundle node '{node.path}' range (offset {node.offset}, size {node.size}, stream length {blocksLength}).");
                }
            }

            var files = new StreamFile[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                files[i] = new StreamFile
                {
                    path = node.path,
                    fileName = Path.GetFileName(node.path.Replace('\\', '/')),
                    stream = new SubStream(cachedBlocksPath, node.offset, node.size)
                };
            }

            return files;
        }

        public static StreamFile[] CreateStreamFiles(
            string cachedBlocksPath,
            long blocksLength,
            IReadOnlyList<CachedBundleNode> nodes)
        {
            if (string.IsNullOrWhiteSpace(cachedBlocksPath))
            {
                throw new ArgumentException("Cached blocks path must not be empty.", nameof(cachedBlocksPath));
            }

            if (nodes == null)
            {
                throw new ArgumentNullException(nameof(nodes));
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Offset < 0 || node.Size < 0 || node.Offset > blocksLength - node.Size)
                {
                    throw new InvalidDataException(
                        $"Invalid bundle node '{node.Path}' range (offset {node.Offset}, size {node.Size}, stream length {blocksLength}).");
                }
            }

            var files = new StreamFile[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                files[i] = new StreamFile
                {
                    path = node.Path,
                    fileName = Path.GetFileName(node.Path.Replace('\\', '/')),
                    stream = new SubStream(cachedBlocksPath, node.Offset, node.Size)
                };
            }

            return files;
        }

        public DecompressedBundleCacheStats GetStats()
        {
            var entries = EnumerateCachedBundles();
            long totalBytes = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                totalBytes += entries[i].TotalBytes;
            }

            return new DecompressedBundleCacheStats(
                entries.Count,
                totalBytes,
                CacheHits,
                CacheMisses,
                EvictionCount,
                EvictedBytes);
        }

        public DecompressedBundleEvictionResult EvictToBudget(long? maxTotalBytesOverride = null)
        {
            var budget = maxTotalBytesOverride ?? MaxTotalBytes;
            if (budget < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalBytesOverride), "Max total bytes must be non-negative.");
            }

            return EvictInternal(budget, protectedBundleDirectory: null, refreshFromDisk: true);
        }

        public void Clear()
        {
            EvictInternal(0, protectedBundleDirectory: null, refreshFromDisk: true);
        }

        private void RecordWrittenEntryAndEvictIfNeeded(string bundleDir, long entryBytes, long accessTicks)
        {
            var maxBytes = MaxTotalBytes;
            lock (_evictionLock)
            {
                EnsureIndexLoadedLocked();
                if (_entries.TryGetValue(bundleDir, out var existing))
                {
                    _trackedTotalBytes -= existing.TotalBytes;
                }

                _entries[bundleDir] = new CachedEntryState(entryBytes, accessTicks);
                _trackedTotalBytes += entryBytes;

                if (_trackedTotalBytes <= maxBytes)
                {
                    return;
                }

                EvictLocked(maxBytes, protectedBundleDirectory: bundleDir);
            }
        }

        private DecompressedBundleEvictionResult EvictInternal(long maxBytes, string? protectedBundleDirectory, bool refreshFromDisk)
        {
            lock (_evictionLock)
            {
                if (refreshFromDisk || !_indexInitialized)
                {
                    ReloadIndexFromDiskLocked();
                }

                return EvictLocked(maxBytes, protectedBundleDirectory);
            }
        }

        private void EnsureIndexLoadedLocked()
        {
            if (!_indexInitialized)
            {
                ReloadIndexFromDiskLocked();
            }
        }

        private void ReloadIndexFromDiskLocked()
        {
            var diskEntries = EnumerateCachedBundles();
            _entries.Clear();
            long total = 0;
            for (int i = 0; i < diskEntries.Count; i++)
            {
                var entry = diskEntries[i];
                _entries[entry.DirectoryPath] = new CachedEntryState(entry.TotalBytes, entry.LastAccessTicks);
                total += entry.TotalBytes;
            }

            _trackedTotalBytes = total;
            _indexInitialized = true;
        }

        private DecompressedBundleEvictionResult EvictLocked(long maxBytes, string? protectedBundleDirectory)
        {
            if (_trackedTotalBytes <= maxBytes)
            {
                return new DecompressedBundleEvictionResult(
                    evictedBundleCount: 0,
                    evictedBytes: 0,
                    failedDeleteCount: 0,
                    remainingBundleCount: _entries.Count,
                    remainingBytes: Math.Max(0L, _trackedTotalBytes));
            }

            var entries = new List<CachedBundleDiskEntry>(_entries.Count);
            foreach (var kv in _entries)
            {
                var accessTicks = _lastAccessTicks.TryGetValue(kv.Key, out var memTicks)
                    ? Math.Max(kv.Value.LastAccessTicks, memTicks)
                    : kv.Value.LastAccessTicks;
                entries.Add(new CachedBundleDiskEntry(kv.Key, kv.Value.TotalBytes, accessTicks));
            }

            entries.Sort(static (a, b) =>
            {
                var cmp = a.LastAccessTicks.CompareTo(b.LastAccessTicks);
                return cmp != 0 ? cmp : string.Compare(a.DirectoryPath, b.DirectoryPath, StringComparison.OrdinalIgnoreCase);
            });

            var evictedCount = 0;
            long evictedBytes = 0;
            var failedDeletes = 0;
            var remainingCount = entries.Count;
            var remainingBytes = _trackedTotalBytes;

            for (int i = 0; i < entries.Count && remainingBytes > maxBytes; i++)
            {
                var candidate = entries[i];
                if (maxBytes > 0
                    && !string.IsNullOrEmpty(protectedBundleDirectory)
                    && string.Equals(candidate.DirectoryPath, protectedBundleDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var lease = TryAcquireBundleLockImmediate(candidate.DirectoryPath);
                if (lease == null)
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(candidate.DirectoryPath))
                    {
                        Directory.Delete(candidate.DirectoryPath, recursive: true);
                    }

                    _entries.Remove(candidate.DirectoryPath);
                    _lastAccessTicks.TryRemove(candidate.DirectoryPath, out _);
                    evictedCount++;
                    evictedBytes += candidate.TotalBytes;
                    remainingCount--;
                    remainingBytes -= candidate.TotalBytes;
                    _trackedTotalBytes = Math.Max(0L, _trackedTotalBytes - candidate.TotalBytes);

                    var parentDir = Path.GetDirectoryName(candidate.DirectoryPath);
                    if (!string.IsNullOrEmpty(parentDir)
                        && !string.Equals(parentDir, CacheBaseDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteEmptyDirectory(parentDir);
                    }
                }
                catch (Exception ex)
                {
                    failedDeletes++;
                    Logger.Warning($"Failed to evict cached bundle directory '{candidate.DirectoryPath}': {ex.Message}");
                }
            }

            if (evictedCount > 0)
            {
                Interlocked.Add(ref _evictionCount, evictedCount);
                Interlocked.Add(ref _evictedBytes, evictedBytes);
            }

            return new DecompressedBundleEvictionResult(
                evictedCount,
                evictedBytes,
                failedDeletes,
                Math.Max(0, remainingCount),
                Math.Max(0L, remainingBytes));
        }

        private List<CachedBundleDiskEntry> EnumerateCachedBundles()
        {
            Interlocked.Increment(ref _diskEnumerations);
            var result = new List<CachedBundleDiskEntry>();
            if (!Directory.Exists(CacheBaseDirectory))
            {
                return result;
            }

            string[] gameDirs;
            try
            {
                gameDirs = Directory.GetDirectories(CacheBaseDirectory);
            }
            catch
            {
                return result;
            }

            foreach (var gameDir in gameDirs)
            {
                string[] bundleDirs;
                try
                {
                    bundleDirs = Directory.GetDirectories(gameDir);
                }
                catch
                {
                    continue;
                }

                foreach (var bundleDir in bundleDirs)
                {
                    try
                    {
                        var readyPath = Path.Combine(bundleDir, ReadyMarkerFileName);
                        if (!File.Exists(readyPath))
                        {
                            continue;
                        }

                        long bytes = 0;
                        foreach (var file in Directory.EnumerateFiles(bundleDir))
                        {
                            try
                            {
                                bytes += new FileInfo(file).Length;
                            }
                            catch
                            {
                                // Ignore racing/transient file
                            }
                        }

                        var diskTicks = ReadAccessTicksFromDisk(readyPath, bundleDir);
                        var accessTicks = _lastAccessTicks.TryGetValue(bundleDir, out var memTicks)
                            ? Math.Max(memTicks, diskTicks)
                            : diskTicks;

                        result.Add(new CachedBundleDiskEntry(bundleDir, bytes, accessTicks));
                    }
                    catch
                    {
                        // Ignore disappearing directory
                    }
                }
            }

            return result;
        }

        private static long ReadAccessTicksFromDisk(string readyPath, string bundleDir)
        {
            long ticks = 0;
            try
            {
                if (File.Exists(readyPath))
                {
                    ticks = File.GetLastWriteTimeUtc(readyPath).Ticks;
                }
            }
            catch
            {
                // Ignore
            }

            if (ticks == 0)
            {
                try
                {
                    ticks = Directory.GetLastWriteTimeUtc(bundleDir).Ticks;
                }
                catch
                {
                    // Ignore
                }
            }

            return ticks;
        }

        private bool TryValidateExistingEntry(
            string blocksPath,
            string manifestPath,
            string readyPath,
            long expectedSourceLength,
            long expectedSourceLastWriteTicks,
            long expectedUncompressedSize,
            out BundleCacheManifest? manifest,
            out long actualBlocksLength)
        {
            manifest = null;
            actualBlocksLength = 0;

            if (!File.Exists(readyPath) || !File.Exists(blocksPath) || !File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                var blocksInfo = new FileInfo(blocksPath);
                if (!blocksInfo.Exists)
                {
                    return false;
                }

                actualBlocksLength = blocksInfo.Length;
                if (expectedUncompressedSize >= 0 && actualBlocksLength != expectedUncompressedSize)
                {
                    return false;
                }

                var manifestText = File.ReadAllText(manifestPath, Encoding.UTF8);
                manifest = JsonSerializer.Deserialize<BundleCacheManifest>(manifestText, ManifestJsonOptions);
                if (manifest == null || manifest.Version != ManifestVersion)
                {
                    return false;
                }

                if (manifest.SourceLength != expectedSourceLength
                    || manifest.SourceLastWriteTimeUtcTicks != expectedSourceLastWriteTicks
                    || manifest.UncompressedSize != actualBlocksLength)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                manifest = null;
                actualBlocksLength = 0;
                return false;
            }
        }

        private void TouchAccessTimestamp(string bundleDir, string readyPath, long? explicitTicks = null)
        {
            var ticks = explicitTicks ?? NextMonotonicUtcTicks();
            _lastAccessTicks.AddOrUpdate(
                bundleDir,
                ticks,
                (_, current) => ticks > current ? ticks : current);

            try
            {
                if (File.Exists(readyPath))
                {
                    File.SetLastWriteTimeUtc(readyPath, new DateTime(ticks, DateTimeKind.Utc));
                }
            }
            catch
            {
                // Best-effort touch; ignore contention/read-only filesystem errors
            }
        }

        private string GetBundleCacheDirectory(
            string normalizedBundlePath,
            long sourceLength,
            long sourceLastWriteTicks,
            string? gameScopePath)
        {
            var effectiveScope = !string.IsNullOrWhiteSpace(gameScopePath)
                ? NormalizePath(gameScopePath)
                : !string.IsNullOrWhiteSpace(GameScopePath)
                    ? GameScopePath!
                    : Path.GetDirectoryName(normalizedBundlePath) ?? normalizedBundlePath;

            var gameHash = ComputeShortSha256Hex(NormalizeKeyPath(effectiveScope), 16);
            var keyMaterial = string.Concat(
                NormalizeKeyPath(normalizedBundlePath),
                "|",
                sourceLength.ToString(CultureInfo.InvariantCulture),
                "|",
                sourceLastWriteTicks.ToString(CultureInfo.InvariantCulture));
            var bundleKey = ComputeShortSha256Hex(keyMaterial, 32);

            return Path.Combine(CacheBaseDirectory, gameHash, bundleKey);
        }

        private static string ComputeShortSha256Hex(string value, int hexChars)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return hexChars < hex.Length ? hex.Substring(0, hexChars) : hex;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static string NormalizeKeyPath(string normalizedPath)
        {
            return OperatingSystem.IsWindows()
                ? normalizedPath.ToLowerInvariant()
                : normalizedPath;
        }

        private static long NextMonotonicUtcTicks()
        {
            while (true)
            {
                var last = Interlocked.Read(ref s_lastUtcTicks);
                var now = DateTime.UtcNow.Ticks;
                var next = now > last ? now : last + 1;
                if (Interlocked.CompareExchange(ref s_lastUtcTicks, next, last) == last)
                {
                    return next;
                }
            }
        }

        private static void TryDeleteFile(string path)
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
                // Ignore cleanup failure
            }
        }

        private static void TryDeleteEmptyDirectory(string dirPath)
        {
            try
            {
                if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
                {
                    Directory.Delete(dirPath, recursive: false);
                }
            }
            catch
            {
                // Ignore cleanup failure
            }
        }

        private IDisposable AcquireBundleLock(string lockKey, CancellationToken cancellationToken = default)
        {
            RefCountedSemaphore entry;
            lock (_keyedLocksGate)
            {
                if (!_keyedLocks.TryGetValue(lockKey, out entry!))
                {
                    entry = new RefCountedSemaphore();
                    _keyedLocks[lockKey] = entry;
                }

                entry.RefCount++;
            }

            try
            {
                entry.Semaphore.Wait(cancellationToken);
            }
            catch
            {
                ReleaseRef(lockKey, entry);
                throw;
            }

            return new BundleLockLease(this, lockKey, entry);
        }

        private IDisposable? TryAcquireBundleLockImmediate(string lockKey)
        {
            RefCountedSemaphore entry;
            lock (_keyedLocksGate)
            {
                if (!_keyedLocks.TryGetValue(lockKey, out entry!))
                {
                    entry = new RefCountedSemaphore();
                    _keyedLocks[lockKey] = entry;
                }

                entry.RefCount++;
            }

            if (!entry.Semaphore.Wait(0))
            {
                ReleaseRef(lockKey, entry);
                return null;
            }

            return new BundleLockLease(this, lockKey, entry);
        }

        private void ReleaseBundleLock(string lockKey, RefCountedSemaphore entry)
        {
            entry.Semaphore.Release();
            ReleaseRef(lockKey, entry);
        }

        private void ReleaseRef(string lockKey, RefCountedSemaphore entry)
        {
            lock (_keyedLocksGate)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    _keyedLocks.Remove(lockKey);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class RefCountedSemaphore
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int RefCount;
        }

        private sealed class BundleLockLease : IDisposable
        {
            private DecompressedBundleCache? _owner;
            private readonly string _lockKey;
            private readonly RefCountedSemaphore _entry;

            public BundleLockLease(DecompressedBundleCache owner, string lockKey, RefCountedSemaphore entry)
            {
                _owner = owner;
                _lockKey = lockKey;
                _entry = entry;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseBundleLock(_lockKey, _entry);
            }
        }

        private sealed class BundleCacheManifest
        {
            public int Version { get; set; }
            public string SourcePath { get; set; } = string.Empty;
            public long SourceLength { get; set; }
            public long SourceLastWriteTimeUtcTicks { get; set; }
            public long UncompressedSize { get; set; }
            public long CreatedUtcTicks { get; set; }
            public long LastAccessedUtcTicks { get; set; }
            public List<CachedBundleNode> Nodes { get; set; } = new();
        }

        private readonly struct CachedEntryState
        {
            public CachedEntryState(long totalBytes, long lastAccessTicks)
            {
                TotalBytes = totalBytes;
                LastAccessTicks = lastAccessTicks;
            }

            public long TotalBytes { get; }
            public long LastAccessTicks { get; }
        }

        private readonly struct CachedBundleDiskEntry
        {
            public CachedBundleDiskEntry(string directoryPath, long totalBytes, long lastAccessTicks)
            {
                DirectoryPath = directoryPath;
                TotalBytes = totalBytes;
                LastAccessTicks = lastAccessTicks;
            }

            public string DirectoryPath { get; }
            public long TotalBytes { get; }
            public long LastAccessTicks { get; }
        }
    }
}
