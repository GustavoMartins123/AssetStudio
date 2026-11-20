using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AssetStudio.Avalonia.Services
{
    public readonly record struct BulkExportSummary(
        int Exported,
        int Skipped,
        int Failed,
        int BatchCount,
        IReadOnlyList<string> Errors,
        bool WasCancelled = false,
        string? ErrorReportPath = null);

    public readonly record struct SplitObjectsExportSummary(
        int Exported,
        int Failed,
        IReadOnlyList<string> Errors,
        bool WasCancelled = false,
        string? ErrorReportPath = null);

    public readonly record struct AssetExportProgressReport(
        int Completed,
        int Total,
        int Percent,
        int Succeeded,
        int Skipped,
        int Failed,
        string CurrentItemName,
        string? CurrentItemType,
        long PathId,
        string? SourcePath);

    public sealed class LazyExportSourceBatch
    {
        public IReadOnlyList<string> SourcePaths { get; }
        public IReadOnlyList<AssetItem> Items { get; }
        public long EstimatedAssetBytes { get; }

        public LazyExportSourceBatch(
            IReadOnlyList<string> sourcePaths,
            IReadOnlyList<AssetItem> items,
            long estimatedAssetBytes)
        {
            SourcePaths = sourcePaths ?? Array.Empty<string>();
            Items = items ?? Array.Empty<AssetItem>();
            EstimatedAssetBytes = estimatedAssetBytes;
        }
    }

    public sealed class EagerModelGroupEntry
    {
        public required string GroupName { get; init; }
        public GameObject? RootGameObject { get; init; }
        public required List<Mesh> Meshes { get; init; }
        public required List<Renderer> Renderers { get; init; }
        public Avatar? Avatar { get; init; }
    }

    public sealed class AssetBatchExportOptions
    {
        public ExportOptionsState ExportOptions { get; init; } = new();
        public List<SerializedFile>? AssetsFiles { get; init; }
        public object? FilesLock { get; init; }
        public AssemblyLoader? AssemblyLoader { get; init; }
        public bool IsLazyLoading { get; init; }
        public Func<AssetItem, string?>? LazySourceKeyResolver { get; init; }
        public Action<LazyExportSourceBatch, CancellationToken>? EnsureLazyBatchLoaded { get; init; }
        public Action<LazyExportSourceBatch, HashSet<string>?>? ReleaseLazyBatch { get; init; }
        public Func<HashSet<string>>? SnapshotInitiallyLoadedFileNames { get; init; }
        public Func<IDisposable?>? SuspendLruEviction { get; init; }
        public Func<AssetItem, string, ExportConversionContext, bool>? CustomItemExporter { get; init; }
        public Func<AssetItem, string, ExportConversionContext, bool>? ConvertAssetExporter { get; init; }
        public Func<AssetStudio.Object, string>? FormatPrefab { get; init; }
        public ExportViewModel? ViewModel { get; init; }
        public IProgress<AssetExportProgressReport>? Progress { get; init; }
        public Action<int>? UiProgressCallback { get; init; }
        public Action<string>? StatusUpdate { get; init; }
        public int MaxDegreeOfParallelism { get; init; }
        public int MaxSourceFilesPerBatch { get; init; } = AssetExportService.DefaultLazyExportBatchSourceFileLimit;
        public long MaxAssetBytesPerBatch { get; init; } = AssetExportService.DefaultLazyExportBatchByteLimit;
        public int CurrentExportWriteIntervalMs { get; init; } = AssetExportService.DefaultCurrentExportWriteIntervalMs;
        public int UiProgressIntervalMs { get; init; } = AssetExportService.DefaultExportUiProgressIntervalMs;
        public Action<LazyExportSourceBatch, int>? OnBatchLoaded { get; init; }
        public bool WriteErrorReportOnCompletion { get; init; }
        public IReadOnlyList<string>? LoggedErrors { get; init; }
        public bool ThrowOnCancellation { get; init; }
    }

    public class ExportProgressTracker
    {
        private readonly string _currentExportPath;
        private readonly int _totalCount;
        private readonly Action<int>? _uiProgressCallback;
        private readonly int _fileWriteIntervalMs;
        private readonly int _uiProgressIntervalMs;
        private readonly object _fileWriteLock = new();

        private long _lastFileWriteTicks;
        private long _lastUiPostTicks;
        private int _lastReportedPercent = -1;
        private int _fileWriteCount;
        private int _uiPostCount;

        public int FileWriteCount => Volatile.Read(ref _fileWriteCount);
        public int UiPostCount => Volatile.Read(ref _uiPostCount);
        public int LastReportedPercent => Volatile.Read(ref _lastReportedPercent);

        public ExportProgressTracker(
            string currentExportPath,
            int totalCount,
            Action<int>? uiProgressCallback = null,
            int fileWriteIntervalMs = AssetExportService.DefaultCurrentExportWriteIntervalMs,
            int uiProgressIntervalMs = AssetExportService.DefaultExportUiProgressIntervalMs)
        {
            _currentExportPath = currentExportPath ?? string.Empty;
            _totalCount = Math.Max(1, totalCount);
            _uiProgressCallback = uiProgressCallback;
            _fileWriteIntervalMs = Math.Max(0, fileWriteIntervalMs);
            _uiProgressIntervalMs = Math.Max(0, uiProgressIntervalMs);

            var initialTicks = Environment.TickCount64 - Math.Max(_fileWriteIntervalMs, _uiProgressIntervalMs) - 1;
            _lastFileWriteTicks = initialTicks;
            _lastUiPostTicks = initialTicks;

            if (!string.IsNullOrWhiteSpace(_currentExportPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_currentExportPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch
                {
                }
            }
        }

        public void ReportAssetProgress(AssetItem asset, int completedCount, bool forceFlush = false)
        {
            var source = asset.SourceFile?.originalPath
                ?? asset.SourceFile?.fullName
                ?? asset.SourceFile?.fileName
                ?? asset.Handle?.OriginalPath;
            ReportCustomProgress(
                asset.TypeString,
                asset.Name,
                asset.PathID,
                source,
                completedCount,
                forceFlush);
        }

        public void ReportCustomProgress(
            string? typeString,
            string? name,
            long pathId,
            string? source,
            int completedCount,
            bool forceFlush = false)
        {
            bool isFinal = completedCount >= _totalCount;
            bool shouldForceWrite = forceFlush || isFinal || completedCount <= 1;

            TryWriteCurrentExportThrottled(typeString, name, pathId, source, completedCount, shouldForceWrite);
            TryPostUiProgressThrottled(completedCount, isFinal);
        }

        private void TryWriteCurrentExportThrottled(
            string? typeString,
            string? name,
            long pathId,
            string? source,
            int completedCount,
            bool forceFlush)
        {
            if (string.IsNullOrWhiteSpace(_currentExportPath))
            {
                return;
            }

            long nowTicks = Environment.TickCount64;
            if (!forceFlush)
            {
                long prevTicks = Interlocked.Read(ref _lastFileWriteTicks);
                if (nowTicks - prevTicks < _fileWriteIntervalMs)
                {
                    return;
                }
                if (Interlocked.CompareExchange(ref _lastFileWriteTicks, nowTicks, prevTicks) != prevTicks)
                {
                    return;
                }
            }
            else
            {
                Interlocked.Exchange(ref _lastFileWriteTicks, nowTicks);
            }

            lock (_fileWriteLock)
            {
                try
                {
                    File.WriteAllText(
                        _currentExportPath,
                        AssetExportService.FormatCurrentExportContent(typeString, name, pathId, source, completedCount, _totalCount),
                        Encoding.UTF8);
                    Interlocked.Increment(ref _fileWriteCount);
                }
                catch
                {
                }
            }
        }

        private void TryPostUiProgressThrottled(int completedCount, bool isFinal)
        {
            if (_uiProgressCallback == null)
            {
                return;
            }

            int percent = Math.Clamp((int)((double)completedCount / _totalCount * 100), 0, 100);
            int prevPercent = Volatile.Read(ref _lastReportedPercent);
            long nowTicks = Environment.TickCount64;
            long prevTicks = Interlocked.Read(ref _lastUiPostTicks);

            bool percentChanged = percent > prevPercent;
            bool timeElapsed = nowTicks - prevTicks >= _uiProgressIntervalMs;

            if (!isFinal && !percentChanged && !timeElapsed)
            {
                return;
            }

            if (percentChanged)
            {
                int currentPrev = prevPercent;
                while (percent > currentPrev)
                {
                    int exchanged = Interlocked.CompareExchange(ref _lastReportedPercent, percent, currentPrev);
                    if (exchanged == currentPrev)
                    {
                        Interlocked.Exchange(ref _lastUiPostTicks, nowTicks);
                        Interlocked.Increment(ref _uiPostCount);
                        _uiProgressCallback(percent);
                        return;
                    }
                    currentPrev = exchanged;
                }
                return;
            }

            if (isFinal || (timeElapsed && Interlocked.CompareExchange(ref _lastUiPostTicks, nowTicks, prevTicks) == prevTicks))
            {
                if (isFinal && prevPercent == percent)
                {
                    return;
                }
                Interlocked.Exchange(ref _lastReportedPercent, percent);
                Interlocked.Increment(ref _uiPostCount);
                _uiProgressCallback(percent);
            }
        }
    }

    public class ExportConversionContext
    {
        private readonly List<SerializedFile> _assetsFiles;
        private readonly object? _filesLock;
        private readonly object _cacheLock = new();
        private readonly ConcurrentDictionary<string, byte> _reservedOutputPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _collisionCountersByStem = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _exportedModelGroupIds = new(StringComparer.Ordinal);
        private Dictionary<uint, string>? _bonePathHash;
        private Dictionary<uint, string>? _morphChannelNames;
        private Dictionary<Mesh, EagerModelGroupEntry>? _eagerModelGroupsByMesh;

        public ExportConversionContext(List<SerializedFile> assetsFiles, object? filesLock = null)
        {
            _assetsFiles = assetsFiles ?? new List<SerializedFile>();
            _filesLock = filesLock;
        }

        public int ReservedPathCount => _reservedOutputPaths.Count;

        public void InvalidateDynamicCaches()
        {
            lock (_cacheLock)
            {
                _bonePathHash = null;
                _morphChannelNames = null;
                _eagerModelGroupsByMesh = null;
            }
        }

        private List<SerializedFile> SnapshotAssetsFiles()
        {
            if (_filesLock != null)
            {
                lock (_filesLock)
                {
                    return _assetsFiles.ToList();
                }
            }
            return _assetsFiles.ToList();
        }

        public bool IsModelGroupExported(string? groupId)
        {
            return !string.IsNullOrWhiteSpace(groupId) && _exportedModelGroupIds.ContainsKey(groupId);
        }

        public bool TryMarkModelGroupExported(string? groupId)
        {
            return string.IsNullOrWhiteSpace(groupId) || _exportedModelGroupIds.TryAdd(groupId, 0);
        }

        public void UnmarkModelGroupExported(string? groupId)
        {
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                _exportedModelGroupIds.TryRemove(groupId, out _);
            }
        }

        public Dictionary<uint, string> BonePathHash
        {
            get
            {
                var cached = Volatile.Read(ref _bonePathHash);
                if (cached != null)
                {
                    return cached;
                }
                lock (_cacheLock)
                {
                    return _bonePathHash ??= AssetExportHelper.BuildBonePathHash(SnapshotAssetsFiles());
                }
            }
        }

        public Dictionary<uint, string> MorphChannelNames
        {
            get
            {
                var cached = Volatile.Read(ref _morphChannelNames);
                if (cached != null)
                {
                    return cached;
                }
                lock (_cacheLock)
                {
                    return _morphChannelNames ??= AssetExportHelper.BuildMorphChannelNames(SnapshotAssetsFiles());
                }
            }
        }

        public Dictionary<Mesh, EagerModelGroupEntry> EagerModelGroupsByMesh
        {
            get
            {
                var cached = Volatile.Read(ref _eagerModelGroupsByMesh);
                if (cached != null)
                {
                    return cached;
                }
                lock (_cacheLock)
                {
                    return _eagerModelGroupsByMesh ??= AssetExportService.BuildEagerModelGroupLookup(SnapshotAssetsFiles());
                }
            }
        }

        public bool TryGetAssetFilePath(
            string directory,
            string fileName,
            string extension,
            AssetItem asset,
            out string filePath)
        {
            fileName = AssetExportService.FixFileName(fileName);
            filePath = Path.Combine(directory, fileName + extension);
            var normalizedPath = Path.GetFullPath(filePath);
            if (_reservedOutputPaths.TryAdd(normalizedPath, 0))
            {
                return !File.Exists(filePath);
            }

            var uniqueId = asset.Handle?.UniqueID;
            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                uniqueId = asset.UniqueID;
            }
            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                uniqueId = $"{asset.Type}:{asset.PathID.ToString(CultureInfo.InvariantCulture)}:{AssetExportService.GetAssetSourcePath(asset)}";
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uniqueId)))
                .Substring(0, 8)
                .ToLowerInvariant();
            var suffix = $"_{asset.PathID.ToString(CultureInfo.InvariantCulture)}_{hash}";
            var maxBaseLength = Math.Max(1, 120 - suffix.Length);
            var truncatedBase = fileName.Length > maxBaseLength
                ? fileName.Substring(0, maxBaseLength)
                : fileName;

            var fullDirectory = Path.GetFullPath(directory);
            filePath = Path.Combine(fullDirectory, truncatedBase + suffix + extension);
            if (_reservedOutputPaths.TryAdd(filePath, 0))
            {
                return !File.Exists(filePath);
            }

            var stemKey = Path.Combine(fullDirectory, fileName + suffix + extension);
            while (true)
            {
                var collisionIndex = _collisionCountersByStem.AddOrUpdate(stemKey, 1, static (_, prev) => prev + 1);
                var indexSuffix = $"{suffix}_{collisionIndex.ToString(CultureInfo.InvariantCulture)}";
                var indexedMaxBase = Math.Max(1, 120 - indexSuffix.Length);
                var indexedBase = fileName.Length > indexedMaxBase
                    ? fileName.Substring(0, indexedMaxBase)
                    : fileName;
                var candidatePath = Path.Combine(fullDirectory, indexedBase + indexSuffix + extension);
                if (_reservedOutputPaths.TryAdd(candidatePath, 0) && !File.Exists(candidatePath))
                {
                    filePath = candidatePath;
                    return true;
                }
            }
        }
    }

    public sealed class AssetExportService
    {
        public const int DefaultCurrentExportWriteIntervalMs = 250;
        public const int DefaultExportUiProgressIntervalMs = 50;
        public const int DefaultExtractionProgressThrottleMs = 50;
        public const int DefaultLazyExportBatchSourceFileLimit = 16;
        public const long DefaultLazyExportBatchByteLimit = 256L * 1024L * 1024L; // 256 MiB

        public static int GetExportThreadCount(int requestedDegreeOfParallelism = 0)
        {
            if (requestedDegreeOfParallelism > 0)
            {
                return requestedDegreeOfParallelism;
            }

            return Math.Clamp(
                AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_EXPORT_THREADS", 1.0),
                1,
                16);
        }

        public static int GetExtractionThreadCount(int requestedDegreeOfParallelism = 0)
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

        public static bool ShouldSkipConvertedAsset(AssetItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (!item.IsFbxSubAsset())
            {
                return false;
            }

            if (item.Type is ClassIDType.Material or ClassIDType.Shader)
            {
                return true;
            }

            return item.Handle == null && (item.Asset is Material || item.Asset is Shader);
        }

        public static List<AssetItem> OrderConvertedAssetsForExport(IEnumerable<AssetItem> assets)
        {
            ArgumentNullException.ThrowIfNull(assets);

            return assets
                .OrderBy(x => x.Type == ClassIDType.Texture2D ? 0 : x.Type == ClassIDType.Material ? 1 : 2)
                .ToList();
        }

        public static List<AssetItem> FilterAndOrderAssetsForExport(IReadOnlyList<AssetItem> items, ExportMode mode)
        {
            ArgumentNullException.ThrowIfNull(items);

            if (mode != ExportMode.Convert)
            {
                return items.ToList();
            }

            var filtered = new List<AssetItem>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!ShouldSkipConvertedAsset(item))
                {
                    filtered.Add(item);
                }
            }

            return OrderConvertedAssetsForExport(filtered);
        }

        public static List<IReadOnlyList<AssetItem>> BuildBatchExecutionPhases(
            IReadOnlyList<AssetItem> batchItems,
            ExportMode mode)
        {
            ArgumentNullException.ThrowIfNull(batchItems);

            if (mode != ExportMode.Convert || batchItems.Count <= 1)
            {
                return new List<IReadOnlyList<AssetItem>>(1) { batchItems };
            }

            var textures = new List<AssetItem>();
            var materials = new List<AssetItem>();
            var others = new List<AssetItem>();

            for (int i = 0; i < batchItems.Count; i++)
            {
                var item = batchItems[i];
                if (item.Type == ClassIDType.Texture2D)
                {
                    textures.Add(item);
                }
                else if (item.Type == ClassIDType.Material)
                {
                    materials.Add(item);
                }
                else
                {
                    others.Add(item);
                }
            }

            var phases = new List<IReadOnlyList<AssetItem>>(3);
            if (textures.Count > 0) phases.Add(textures);
            if (materials.Count > 0) phases.Add(materials);
            if (others.Count > 0) phases.Add(others);
            return phases;
        }

        public static bool ArenaIndicesContainType(
            AssetIndexArena arena,
            IReadOnlyList<int> arenaIndices,
            ClassIDType targetType)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(arenaIndices);

            var arenaCount = arena.Count;
            for (var i = 0; i < arenaIndices.Count; i++)
            {
                var idx = arenaIndices[i];
                if ((uint)idx < (uint)arenaCount && arena.GetType(idx) == targetType)
                {
                    return true;
                }
            }

            return false;
        }

        public static List<AssetItem> MaterializeArenaItemsForExport(
            AssetIndexArena arena,
            IReadOnlyList<int> arenaIndices,
            ExportMode mode)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(arenaIndices);

            var arenaCount = arena.Count;
            var items = new List<AssetItem>(arenaIndices.Count);
            for (var i = 0; i < arenaIndices.Count; i++)
            {
                var idx = arenaIndices[i];
                if ((uint)idx >= (uint)arenaCount)
                {
                    continue;
                }

                if (mode == ExportMode.Convert)
                {
                    var type = arena.GetType(idx);
                    if (type is ClassIDType.Material or ClassIDType.Shader
                        && AssetItem.ContainsFbxSegment(arena.GetContainer(idx)))
                    {
                        continue;
                    }
                }

                items.Add(new AssetItem(arena.MaterializeHandle(idx), idx));
            }

            return mode == ExportMode.Convert
                ? OrderConvertedAssetsForExport(items)
                : items;
        }

        public static List<LazyExportSourceBatch> BuildLazyExportBatches(
            IReadOnlyList<AssetItem> items,
            Func<AssetItem, string?> sourcePathResolver,
            int maxSourceFilesPerBatch = DefaultLazyExportBatchSourceFileLimit,
            long maxAssetBytesPerBatch = DefaultLazyExportBatchByteLimit)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(sourcePathResolver);

            maxSourceFilesPerBatch = Math.Max(1, maxSourceFilesPerBatch);
            maxAssetBytesPerBatch = Math.Max(1L, maxAssetBytesPerBatch);

            var batches = new List<LazyExportSourceBatch>();
            if (items.Count == 0)
            {
                return batches;
            }

            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var sourceGroupOrder = new List<string>();
            var itemsBySource = new Dictionary<string, List<AssetItem>>(comparer);
            var bytesBySource = new Dictionary<string, long>(comparer);

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                var rawSource = sourcePathResolver(item) ?? string.Empty;
                var groupKey = rawSource.Trim();
                if (!itemsBySource.TryGetValue(groupKey, out var groupItems))
                {
                    groupItems = new List<AssetItem>();
                    itemsBySource[groupKey] = groupItems;
                    bytesBySource[groupKey] = 0L;
                    sourceGroupOrder.Add(groupKey);
                }

                groupItems.Add(item);
                var itemBytes = Math.Max(0L, item.FullSize > 0 ? item.FullSize : item.Size);
                bytesBySource[groupKey] += itemBytes;
            }

            var currentSourcePaths = new List<string>(maxSourceFilesPerBatch);
            var currentItems = new List<AssetItem>();
            long currentBytes = 0L;

            void FlushBatch()
            {
                if (currentItems.Count == 0)
                {
                    return;
                }

                batches.Add(new LazyExportSourceBatch(
                    currentSourcePaths.ToArray(),
                    currentItems.ToArray(),
                    currentBytes));
                currentSourcePaths.Clear();
                currentItems.Clear();
                currentBytes = 0L;
            }

            for (int g = 0; g < sourceGroupOrder.Count; g++)
            {
                var sourceKey = sourceGroupOrder[g];
                var groupItems = itemsBySource[sourceKey];
                var groupBytes = bytesBySource[sourceKey];
                var hasSourcePath = !string.IsNullOrEmpty(sourceKey);

                var exceedsFileLimit = hasSourcePath && currentSourcePaths.Count >= maxSourceFilesPerBatch;
                var exceedsByteLimit = currentItems.Count > 0 && currentBytes + groupBytes > maxAssetBytesPerBatch;
                if (exceedsFileLimit || exceedsByteLimit)
                {
                    FlushBatch();
                }

                if (hasSourcePath)
                {
                    currentSourcePaths.Add(sourceKey);
                }
                currentItems.AddRange(groupItems);
                currentBytes += groupBytes;
            }

            FlushBatch();
            return batches;
        }

        public static string GetUniqueDirectoryPath(string directoryPath)
        {
            var candidate = directoryPath;
            for (var i = 1; Directory.Exists(candidate); i++)
            {
                candidate = $"{directoryPath} ({i})";
            }
            return candidate;
        }

        public static string ReserveUniqueDirectoryPath(
            string directoryPath,
            ConcurrentDictionary<string, byte> reservedPaths)
        {
            ArgumentNullException.ThrowIfNull(reservedPaths);
            var fullBasePath = Path.GetFullPath(directoryPath);
            var candidate = fullBasePath;
            for (var i = 1; ; i++)
            {
                if (reservedPaths.TryAdd(candidate, 0) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
                candidate = $"{fullBasePath} ({i})";
            }
        }

        public static string GetUniqueFilePath(string filePath)
        {
            if (!File.Exists(filePath)) return filePath;

            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);
            for (var i = 1; ; i++)
            {
                var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        public static string ReserveUniqueFilePath(
            string filePath,
            ConcurrentDictionary<string, byte> reservedPaths)
        {
            ArgumentNullException.ThrowIfNull(reservedPaths);
            var fullFilePath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullFilePath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(fullFilePath);
            var extension = Path.GetExtension(fullFilePath);

            var candidate = fullFilePath;
            for (var i = 1; ; i++)
            {
                if (reservedPaths.TryAdd(candidate, 0) && !File.Exists(candidate))
                {
                    return candidate;
                }
                candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            }
        }

        public static bool IsPathWithinRoot(string candidatePath, string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
            {
                return false;
            }

            var rootFull = Path.GetFullPath(rootDirectory);
            var rootWithSeparator = rootFull
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidateFull = Path.GetFullPath(candidatePath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(candidateFull, rootFull, comparison)
                || candidateFull.StartsWith(rootWithSeparator, comparison);
        }

        public static bool TryBuildSafeChildPath(
            string rootDirectory,
            string? relativeEntryPath,
            out string fullPath,
            out string? blockReason)
        {
            fullPath = string.Empty;
            blockReason = null;

            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                blockReason = "Empty root directory.";
                return false;
            }

            var rawPath = relativeEntryPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                blockReason = "Empty entry path.";
                return false;
            }

            if (Path.IsPathRooted(rawPath))
            {
                blockReason = $"Blocked rooted path in bundle entry: '{rawPath}'";
                return false;
            }

            var rawSegments = rawPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawSegments.Length == 0 || rawSegments.Any(part => part.Trim() is "." or ".."))
            {
                blockReason = $"Blocked path traversal attempt in bundle entry: '{rawPath}'";
                return false;
            }

            var safeSegments = rawSegments
                .Select(FixFileName)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            if (safeSegments.Length == 0)
            {
                blockReason = "Entry path contains no valid filename segments.";
                return false;
            }

            var rootFullPath = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var candidatePath = Path.GetFullPath(Path.Combine(rootFullPath, Path.Combine(safeSegments)));
            if (!candidatePath.StartsWith(rootFullPath, comparison))
            {
                blockReason = $"Blocked path traversal attempt in bundle entry: '{rawPath}'";
                return false;
            }

            fullPath = candidatePath;
            return true;
        }

        public static string GetExportDirectory(string savePath, AssetItem asset, AssetGroupOption grouping)
        {
            return grouping switch
            {
                AssetGroupOption.Container when !string.IsNullOrEmpty(asset.Container) => Path.Combine(savePath, GetSafeRelativeExportDirectory(asset.Container)),
                AssetGroupOption.SourceFile => GetSourceFileExportPath(savePath, asset),
                AssetGroupOption.TypeName => Path.Combine(savePath, FixFileName(asset.TypeString)),
                AssetGroupOption.None => savePath,
                _ => savePath,
            };
        }

        public static string GetSourceFileExportPath(string savePath, AssetItem asset)
        {
            var originalPath = !string.IsNullOrWhiteSpace(asset.SourceFile?.originalPath)
                ? asset.SourceFile.originalPath
                : asset.Handle?.OriginalPath;
            var serializedFileName = !string.IsNullOrWhiteSpace(asset.SourceFile?.fileName)
                ? asset.SourceFile.fileName
                : asset.Handle?.SerializedFileName;

            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                var containerFileName = Path.GetFileName(originalPath);
                if (!string.IsNullOrWhiteSpace(containerFileName)
                    && !string.IsNullOrWhiteSpace(serializedFileName)
                    && !string.Equals(containerFileName, serializedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(
                        savePath,
                        FixFileName(containerFileName) + "_export",
                        FixFileName(serializedFileName));
                }

                if (!string.IsNullOrWhiteSpace(containerFileName))
                {
                    return Path.Combine(savePath, FixFileName(containerFileName) + "_export");
                }
            }

            return Path.Combine(savePath, FixFileName(serializedFileName ?? "Unknown") + "_export");
        }

        public static string GetSafeRelativeExportDirectory(string? container)
        {
            if (string.IsNullOrWhiteSpace(container))
            {
                return string.Empty;
            }

            var directory = Path.GetDirectoryName(container.Replace('\\', '/')) ?? string.Empty;
            var safeParts = directory
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Trim() is not "." and not "..")
                .Select(FixFileName)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            return safeParts.Length == 0 ? string.Empty : Path.Combine(safeParts);
        }

        public static string GetAssetSourcePath(AssetItem asset)
        {
            var sourceFile = asset.SourceFile;
            if (!string.IsNullOrWhiteSpace(sourceFile?.originalPath))
            {
                return sourceFile.originalPath;
            }
            if (!string.IsNullOrWhiteSpace(sourceFile?.fullName))
            {
                return sourceFile.fullName;
            }
            return asset.Handle?.OriginalPath ?? string.Empty;
        }

        public static string GetExportFileName(AssetItem item)
        {
            var fbxContainer = GetFbxContainerPath(item.Container);
            if (fbxContainer != null && item.Asset is Mesh or Animator)
            {
                var name = Path.GetFileNameWithoutExtension(fbxContainer);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return item.Name;
        }

        public static string? GetFbxContainerPath(string? container)
        {
            if (string.IsNullOrEmpty(container))
            {
                return null;
            }

            var parts = container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var path = string.Empty;
            foreach (var part in parts)
            {
                path = string.IsNullOrEmpty(path) ? part : Path.Combine(path, part);
                if (string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        }

        public static string GetRawExtension(AssetItem asset, ExportOptionsState options)
        {
            if (!options.RestoreExtensionName) return ".dat";
            return asset.Asset switch
            {
                Texture2D => ".tex",
                TextAsset => GetTextAssetExtension(asset, options),
                Shader => ".shader",
                Font m_Font when m_Font.m_FontData?.Length >= 4 && m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79 => ".otf",
                Font => ".ttf",
                MovieTexture => ".ogv",
                VideoClip m_VideoClip when !string.IsNullOrEmpty(m_VideoClip.m_OriginalPath) => SanitizeExtension(Path.GetExtension(m_VideoClip.m_OriginalPath), ".mp4"),
                AudioClip m_AudioClip => new AudioClipConverter(m_AudioClip).GetExtensionName(),
                _ => ".dat",
            };
        }

        public static string GetTextAssetExtension(AssetItem asset, ExportOptionsState options)
        {
            if (options.RestoreExtensionName && !string.IsNullOrEmpty(asset.Container))
            {
                return SanitizeExtension(Path.GetExtension(asset.Container), ".txt");
            }

            return ".txt";
        }

        public static string SanitizeExtension(string? extension, string fallback)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return fallback;
            }

            var raw = extension.Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw)
            {
                if (character < 32 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '.')
                {
                    continue;
                }

                builder.Append(character);
            }

            var sanitized = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return fallback;
            }

            if (sanitized.Length > 32)
            {
                sanitized = sanitized.Substring(0, 32);
            }

            return "." + sanitized;
        }

        public static string FixFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (var character in name)
            {
                builder.Append(character < 32
                    || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                    || Array.IndexOf(invalidChars, character) >= 0
                    ? '_'
                    : character);
            }

            name = builder.ToString().Trim().TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unnamed";
            }

            while (name.Contains("..", StringComparison.Ordinal))
            {
                name = name.Replace("..", "_", StringComparison.Ordinal);
            }

            name = name.Trim().TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unnamed";
            }

            if (name.Length > 120)
            {
                name = name.Substring(0, 120).TrimEnd(' ', '.');
                if (string.IsNullOrWhiteSpace(name))
                {
                    return "unnamed";
                }
            }

            var dotIndex = name.IndexOf('.');
            var deviceName = (dotIndex >= 0 ? name.Substring(0, dotIndex) : name).Trim().ToUpperInvariant();
            if (deviceName is "CON" or "PRN" or "AUX" or "NUL"
                or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
                or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            {
                name = "_" + name;
                if (name.Length > 120)
                {
                    name = name.Substring(0, 120).TrimEnd(' ', '.');
                }
            }

            return name;
        }

        public static string FormatCurrentExportContent(
            string? typeString,
            string? name,
            long pathId,
            string? source,
            int index,
            int count)
        {
            return $"Exporting {index}/{count}{Environment.NewLine}" +
                   $"Type: {typeString}{Environment.NewLine}" +
                   $"Name: {name}{Environment.NewLine}" +
                   $"PathID: {pathId}{Environment.NewLine}" +
                   $"Source: {source}{Environment.NewLine}";
        }

        public static void WriteCurrentExport(string path, AssetItem asset, int index, int count)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(path,
                    FormatCurrentExportContent(
                        asset.TypeString,
                        asset.Name,
                        asset.PathID,
                        asset.SourceFile?.originalPath ?? asset.SourceFile?.fullName ?? asset.SourceFile?.fileName,
                        index,
                        count),
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        public static void ClearCurrentExport(string savePath)
        {
            try
            {
                var path = Path.Combine(savePath, "export-current.txt");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        public static void TryDeletePartialExport(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }

        public static void WriteBytesExport(string filePath, byte[] data)
        {
            try
            {
                using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                file.Write(data, 0, data.Length);
            }
            catch
            {
                TryDeletePartialExport(filePath);
                throw;
            }
        }

        public static void WriteTextExport(string filePath, string text)
        {
            try
            {
                using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(text);
            }
            catch
            {
                TryDeletePartialExport(filePath);
                throw;
            }
        }

        public static void WriteRawAssetData(AssetStudio.Object asset, string filePath)
        {
            asset.assetsFile.ObjectInfoDic.TryGetValue(asset.m_PathID, out var objectInfo);
            objectInfo ??= new ObjectInfo
            {
                byteStart = asset.reader.byteStart,
                byteSize = asset.byteSize,
                classID = (int)asset.type,
                m_PathID = asset.m_PathID,
                serializedType = asset.serializedType
            };

            var outputCreated = false;
            try
            {
                using var output = new FileStream(
                    filePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                outputCreated = true;
                using var fileReader = asset.assetsFile.reader.Clone();
                using var objectReader = new ObjectReader(fileReader, asset.assetsFile, objectInfo);
                objectReader.Reset();

                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try
                {
                    long remaining = asset.byteSize;
                    while (remaining > 0)
                    {
                        var requested = (int)Math.Min(buffer.Length, remaining);
                        var read = objectReader.BaseStream.Read(buffer, 0, requested);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException($"Unexpected end of asset data with {remaining:N0} bytes remaining.");
                        }

                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch
            {
                if (outputCreated)
                {
                    TryDeletePartialExport(filePath);
                }
                throw;
            }
        }

        public static string? WriteErrorReport(
            string savePath,
            IReadOnlyList<string> exportErrors,
            IReadOnlyList<string>? loggedErrors = null)
        {
            var loadErrors = loggedErrors ?? Array.Empty<string>();
            if (loadErrors.Count == 0 && (exportErrors == null || exportErrors.Count == 0))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(savePath);
                var errorReportPath = Path.Combine(savePath, "errors.txt");
                using (var writer = new StreamWriter(errorReportPath, false, Encoding.UTF8))
                {
                    writer.WriteLine("AssetStudio error report");
                    writer.WriteLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine();

                    if (loadErrors.Count > 0)
                    {
                        writer.WriteLine($"Logged errors ({loadErrors.Count})");
                        writer.WriteLine(new string('=', 80));
                        for (int i = 0; i < loadErrors.Count; i++)
                        {
                            writer.WriteLine($"[{i + 1}]");
                            writer.WriteLine(loadErrors[i]);
                            writer.WriteLine();
                        }
                    }

                    if (exportErrors != null && exportErrors.Count > 0)
                    {
                        writer.WriteLine($"Export errors ({exportErrors.Count})");
                        writer.WriteLine(new string('=', 80));
                        for (int i = 0; i < exportErrors.Count; i++)
                        {
                            writer.WriteLine($"[{i + 1}]");
                            writer.WriteLine(exportErrors[i]);
                            writer.WriteLine();
                        }
                    }
                }
                return errorReportPath;
            }
            catch
            {
                return null;
            }
        }

        public static string FormatBulkExportStatusSummary(BulkExportSummary summary, string? errorReportPath = null)
        {
            var effectiveErrorPath = errorReportPath ?? summary.ErrorReportPath;
            var status = summary.WasCancelled
                ? $"Export cancelled after {summary.Exported} assets."
                : summary.Exported == 0
                    ? "Nothing exported."
                    : $"Finished exporting {summary.Exported} assets.";
            if (summary.Skipped > 0) status += $" {summary.Skipped} skipped (already exported or unsupported).";
            if (summary.Failed > 0) status += $" {summary.Failed} failed.";
            if (!string.IsNullOrWhiteSpace(effectiveErrorPath)) status += $" Error report: {ExtractFileNameCrossPlatform(effectiveErrorPath)}.";
            return status;
        }

        public static string FormatSplitObjectsStatusSummary(
            int exported,
            int failed,
            string? errorReportPath = null,
            bool wasCancelled = false)
        {
            var status = wasCancelled
                ? $"Export cancelled after {exported} objects."
                : exported == 0
                    ? "Nothing exported."
                    : $"Finished exporting {exported} objects.";
            if (failed > 0) status += $" {failed} failed.";
            if (!string.IsNullOrWhiteSpace(errorReportPath)) status += $" Error report: {ExtractFileNameCrossPlatform(errorReportPath)}.";
            return status;
        }

        private static string ExtractFileNameCrossPlatform(string path)
        {
            var span = path.AsSpan().Trim().TrimEnd("/\\");
            if (span.IsEmpty)
            {
                return string.Empty;
            }

            var sepIndex = span.LastIndexOfAny('/', '\\');
            return sepIndex >= 0 ? span[(sepIndex + 1)..].ToString() : span.ToString();
        }

        public static int WriteArenaAssetsXml(
            AssetIndexArena arena,
            IReadOnlyList<int> arenaIndices,
            string filename,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(arenaIndices);

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
                CloseOutput = true
            };

            var writtenCount = 0;
            var arenaCount = arena.Count;
            try
            {
                using (var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                using (var writer = XmlWriter.Create(output, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("Assets");
                    writer.WriteAttributeString("filename", filename);
                    writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    for (var i = 0; i < arenaIndices.Count; i++)
                    {
                        if ((i & 0x3FF) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var idx = arenaIndices[i];
                        if ((uint)idx >= (uint)arenaCount)
                        {
                            continue;
                        }

                        var name = arena.GetName(idx);
                        var container = arena.GetContainer(idx);
                        var type = arena.GetType(idx);
                        var pathId = arena.GetPathId(idx);
                        var byteSize = arena.GetByteSize(idx);
                        var sourceFile = arena.GetSourceFile(idx);
                        var sourcePath = !string.IsNullOrWhiteSpace(sourceFile?.originalPath)
                            ? sourceFile.originalPath
                            : !string.IsNullOrWhiteSpace(sourceFile?.fullName)
                                ? sourceFile.fullName
                                : arena.GetOriginalPath(idx);
                        var displayType = AssetListQuery.GetCachedDisplayType(type, AssetItem.ContainsFbxSegment(container));

                        writer.WriteStartElement("Asset");
                        writer.WriteElementString("Name", name ?? string.Empty);
                        writer.WriteElementString("Container", container ?? string.Empty);
                        writer.WriteStartElement("Type");
                        writer.WriteAttributeString("id", ((int)type).ToString(CultureInfo.InvariantCulture));
                        writer.WriteString(displayType ?? string.Empty);
                        writer.WriteEndElement();
                        writer.WriteElementString("PathID", pathId.ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("Source", sourcePath ?? string.Empty);
                        writer.WriteElementString("Size", byteSize.ToString(CultureInfo.InvariantCulture));
                        writer.WriteEndElement();
                        writtenCount++;
                    }
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                    writer.Flush();
                }
            }
            catch
            {
                TryDeletePartialExport(filename);
                throw;
            }

            return writtenCount;
        }

        public static int WriteAssetsXml(
            IReadOnlyList<AssetItem> toExport,
            string filename,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(toExport);

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
                CloseOutput = true
            };

            var writtenCount = 0;
            try
            {
                using (var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                using (var writer = XmlWriter.Create(output, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("Assets");
                    writer.WriteAttributeString("filename", filename);
                    writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    for (var i = 0; i < toExport.Count; i++)
                    {
                        if ((i & 0x3FF) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var asset = toExport[i];
                        writer.WriteStartElement("Asset");
                        writer.WriteElementString("Name", asset.Name ?? string.Empty);
                        writer.WriteElementString("Container", asset.Container ?? string.Empty);
                        writer.WriteStartElement("Type");
                        writer.WriteAttributeString("id", ((int)asset.Type).ToString(CultureInfo.InvariantCulture));
                        writer.WriteString(asset.DisplayType ?? string.Empty);
                        writer.WriteEndElement();
                        writer.WriteElementString("PathID", asset.PathID.ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("Source", GetAssetSourcePath(asset));
                        writer.WriteElementString("Size", asset.FullSize.ToString(CultureInfo.InvariantCulture));
                        writer.WriteEndElement();
                        writtenCount++;
                    }
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                    writer.Flush();
                }
            }
            catch
            {
                TryDeletePartialExport(filename);
                throw;
            }

            return writtenCount;
        }

        public BulkExportSummary ExportAssetsBatch(
            IReadOnlyList<AssetItem> toExport,
            ExportMode mode,
            string savePath,
            AssetBatchExportOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(toExport);
            options ??= new AssetBatchExportOptions();
            Directory.CreateDirectory(savePath);

            int total = toExport.Count;
            if (total == 0)
            {
                return new BulkExportSummary(0, 0, 0, 0, Array.Empty<string>(), WasCancelled: false, ErrorReportPath: null);
            }

            int exported = 0;
            int skipped = 0;
            int failed = 0;
            int completed = 0;
            bool wasCancelled = false;
            var exportErrors = new ConcurrentQueue<string>();

            var currentExportPath = Path.Combine(savePath, "export-current.txt");
            var progressTracker = new ExportProgressTracker(
                currentExportPath,
                total,
                options.UiProgressCallback,
                options.CurrentExportWriteIntervalMs,
                options.UiProgressIntervalMs);

            var conversionContext = new ExportConversionContext(
                options.AssetsFiles ?? new List<SerializedFile>(),
                options.FilesLock);

            List<LazyExportSourceBatch> batches;
            HashSet<string>? initiallyLoadedFileNames = null;
            if (options.IsLazyLoading)
            {
                initiallyLoadedFileNames = options.SnapshotInitiallyLoadedFileNames?.Invoke();
                var resolver = options.LazySourceKeyResolver ?? GetAssetSourcePath;
                batches = BuildLazyExportBatches(
                    toExport,
                    resolver,
                    options.MaxSourceFilesPerBatch,
                    options.MaxAssetBytesPerBatch);
            }
            else
            {
                batches = new List<LazyExportSourceBatch>(1)
                {
                    new LazyExportSourceBatch(Array.Empty<string>(), toExport, 0L)
                };
            }

            int workerCount = GetExportThreadCount(options.MaxDegreeOfParallelism);

            try
            {
                for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = batches[batchIndex];
                    if (batch.Items.Count == 0)
                    {
                        continue;
                    }

                    if (options.IsLazyLoading)
                    {
                        options.EnsureLazyBatchLoaded?.Invoke(batch, cancellationToken);
                        conversionContext.InvalidateDynamicCaches();
                    }

                    options.OnBatchLoaded?.Invoke(batch, batchIndex);

                    using var lruSuspension = options.IsLazyLoading
                        ? options.SuspendLruEviction?.Invoke()
                        : null;

                    var phases = BuildBatchExecutionPhases(batch.Items, mode);
                    for (int phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
                    {
                        var phaseItems = phases[phaseIndex];
                        if (phaseItems.Count == 0)
                        {
                            continue;
                        }

                        var parallelOptions = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Min(workerCount, phaseItems.Count),
                            CancellationToken = cancellationToken
                        };

                        Parallel.ForEach(phaseItems, parallelOptions, asset =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bool forceFlushCurrent = false;
                            try
                            {
                                var exportPath = GetExportDirectory(savePath, asset, options.ExportOptions.AssetGrouping);
                                Directory.CreateDirectory(exportPath);
                                var fileName = FixFileName(asset.Name);
                                bool itemExported = false;

                                if (options.CustomItemExporter != null)
                                {
                                    itemExported = options.CustomItemExporter(asset, exportPath, conversionContext);
                                }
                                else
                                {
                                    switch (mode)
                                    {
                                        case ExportMode.Raw:
                                            if (conversionContext.TryGetAssetFilePath(exportPath, fileName, GetRawExtension(asset, options.ExportOptions), asset, out var rawFilePath))
                                            {
                                                var assetObj = asset.Asset;
                                                if (assetObj != null)
                                                {
                                                    WriteRawAssetData(assetObj, rawFilePath);
                                                    itemExported = true;
                                                }
                                            }
                                            break;
                                        case ExportMode.Dump:
                                            if (conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", asset, out var dumpFilePath))
                                            {
                                                string? dump = null;
                                                var assetObj = asset.Asset;
                                                if (assetObj is MonoBehaviour m_MonoBehaviour)
                                                {
                                                    dump = m_MonoBehaviour.Dump();
                                                    if (dump == null && options.AssemblyLoader != null && options.AssemblyLoader.Loaded)
                                                    {
                                                        TypeTree? typeTree;
                                                        lock (options.AssemblyLoader)
                                                        {
                                                            typeTree = m_MonoBehaviour.ConvertToTypeTree(options.AssemblyLoader);
                                                        }
                                                        if (typeTree != null)
                                                        {
                                                            dump = m_MonoBehaviour.Dump(typeTree);
                                                        }
                                                    }
                                                }
                                                else if (assetObj != null)
                                                {
                                                    dump = assetObj.Dump();
                                                }
                                                WriteTextExport(dumpFilePath, dump ?? "");
                                                itemExported = true;
                                            }
                                            break;
                                        case ExportMode.Convert:
                                            itemExported = options.ConvertAssetExporter != null
                                                ? options.ConvertAssetExporter(asset, exportPath, conversionContext)
                                                : ExportStandardConvertedAsset(
                                                    asset,
                                                    exportPath,
                                                    conversionContext,
                                                    options.ExportOptions,
                                                    options.AssemblyLoader,
                                                    options.FormatPrefab);
                                            break;
                                    }
                                }

                                if (itemExported)
                                {
                                    Interlocked.Increment(ref exported);
                                }
                                else
                                {
                                    Interlocked.Increment(ref skipped);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                forceFlushCurrent = true;
                                Interlocked.Increment(ref failed);
                                var error = $"Failed to export {asset.TypeString}: {asset.Name} (PathID: {asset.PathID})";
                                exportErrors.Enqueue($"{error}{Environment.NewLine}{ex}");
                                options.StatusUpdate?.Invoke($"Error exporting {asset.Name}: {ex.Message}");
                            }

                            var currentCompleted = Interlocked.Increment(ref completed);
                            var currentExported = Volatile.Read(ref exported);
                            var currentSkipped = Volatile.Read(ref skipped);
                            var currentFailed = Volatile.Read(ref failed);
                            var forceFlush = forceFlushCurrent || currentCompleted == 1 || currentCompleted == total;

                            progressTracker.ReportAssetProgress(
                                asset,
                                currentCompleted,
                                forceFlush: forceFlush);

                            options.ViewModel?.ReportProgress(
                                currentCompleted,
                                total,
                                asset.Name,
                                currentExported,
                                currentSkipped,
                                currentFailed,
                                force: forceFlushCurrent || currentCompleted == total);

                            if (options.Progress != null)
                            {
                                var percent = Math.Clamp((int)((double)currentCompleted / total * 100.0), 0, 100);
                                options.Progress.Report(new AssetExportProgressReport(
                                    Completed: currentCompleted,
                                    Total: total,
                                    Percent: percent,
                                    Succeeded: currentExported,
                                    Skipped: currentSkipped,
                                    Failed: currentFailed,
                                    CurrentItemName: asset.Name ?? string.Empty,
                                    CurrentItemType: asset.TypeString,
                                    PathId: asset.PathID,
                                    SourcePath: GetAssetSourcePath(asset)));
                            }
                        });
                    }

                    if (options.IsLazyLoading && batches.Count > 1)
                    {
                        options.ReleaseLazyBatch?.Invoke(batch, initiallyLoadedFileNames);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
                if (options.ThrowOnCancellation)
                {
                    throw;
                }
            }
            finally
            {
                ClearCurrentExport(savePath);
            }

            var errorsArray = exportErrors.ToArray();
            string? errorReportPath = null;
            if (options.WriteErrorReportOnCompletion)
            {
                errorReportPath = WriteErrorReport(savePath, errorsArray, options.LoggedErrors);
            }

            return new BulkExportSummary(
                exported,
                skipped,
                failed,
                batches.Count,
                errorsArray,
                wasCancelled,
                errorReportPath);
        }

        public static bool ExportStandardConvertedAsset(
            AssetItem item,
            string exportPath,
            ExportConversionContext conversionContext,
            ExportOptionsState exportOptions,
            AssemblyLoader? assemblyLoader = null,
            Func<AssetStudio.Object, string>? formatPrefab = null)
        {
            if (ShouldSkipConvertedAsset(item))
            {
                return false;
            }

            var assetObj = item.Asset;
            if (assetObj == null)
            {
                return false;
            }

            Directory.CreateDirectory(exportPath);
            var fileName = FixFileName(GetExportFileName(item));

            switch (assetObj)
            {
                case AnimatorController m_AnimatorController:
                    return conversionContext.TryGetAssetFilePath(exportPath, fileName, ".controller", item, out var controllerFullPath)
                        && AssetExportHelper.ExportAnimatorController(m_AnimatorController, controllerFullPath);

                case AnimatorOverrideController m_AnimatorOverrideController:
                    return conversionContext.TryGetAssetFilePath(exportPath, fileName, ".overrideController", item, out var overrideFullPath)
                        && AssetExportHelper.ExportAnimatorOverrideController(m_AnimatorOverrideController, overrideFullPath);

                case AnimationClip m_AnimationClip:
                {
                    var clipExportName = string.IsNullOrEmpty(m_AnimationClip.m_Name) ? fileName : m_AnimationClip.m_Name;
                    return conversionContext.TryGetAssetFilePath(exportPath, clipExportName, ".anim", item, out var clipFullPath)
                        && AssetExportHelper.ExportAnimationClipToFile(
                            m_AnimationClip,
                            clipFullPath,
                            conversionContext.BonePathHash,
                            conversionContext.MorphChannelNames);
                }

                case Mesh m_Mesh:
                {
                    m_Mesh.EnsureProcessed();
                    if (m_Mesh.m_VertexCount <= 0 || m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
                    {
                        return false;
                    }
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".obj", item, out var objPath))
                    {
                        return false;
                    }
                    return ExportMeshToObj(m_Mesh, objPath);
                }

                case Texture2D m_Texture2D:
                {
                    if (!exportOptions.ConvertTexture)
                    {
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".tex", item, out var rawPath)) return false;
                        WriteBytesExport(rawPath, m_Texture2D.image_data.GetData());
                        AssetExportHelper.WriteTextureMetaIfMissing(rawPath, m_Texture2D);
                        return true;
                    }

                    using var image = m_Texture2D.ConvertToImage(true);
                    if (image == null) return false;
                    var extension = "." + exportOptions.ConvertTextureFormat.ToString().ToLowerInvariant();
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                    try
                    {
                        using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                        image.WriteToStream(file, exportOptions.ConvertTextureFormat);
                    }
                    catch
                    {
                        TryDeletePartialExport(filePath);
                        throw;
                    }
                    AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Texture2D);
                    return true;
                }

                case AudioClip m_AudioClip:
                {
                    var m_AudioData = m_AudioClip.m_AudioData.GetData();
                    if (m_AudioData == null || m_AudioData.Length == 0) return false;
                    var converter = new AudioClipConverter(m_AudioClip);
                    if (exportOptions.ConvertAudio && converter.IsSupport)
                    {
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".wav", item, out var filePath)) return false;
                        var buffer = converter.ConvertToWav();
                        if (buffer == null) return false;
                        WriteBytesExport(filePath, buffer);
                    }
                    else
                    {
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, converter.GetExtensionName(), item, out var filePath)) return false;
                        WriteBytesExport(filePath, m_AudioData);
                    }
                    return true;
                }

                case Material m_Material:
                {
                    var matExportName = string.IsNullOrEmpty(m_Material.m_Name) ? item.Name : m_Material.m_Name;
                    return conversionContext.TryGetAssetFilePath(exportPath, matExportName, ".mat", item, out var matFullPath)
                        && AssetExportHelper.ExportMaterialToFile(m_Material, item.Name, exportPath, matFullPath, exportOptions.ConvertTextureFormat);
                }

                case TextAsset m_TextAsset:
                {
                    var extension = GetTextAssetExtension(item, exportOptions);
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                    WriteBytesExport(filePath, m_TextAsset.m_Script);
                    return true;
                }

                case MonoScript m_MonoScript:
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var filePath)) return false;
                    var sb = new StringBuilder();
                    sb.AppendLine($"Assembly: {m_MonoScript.m_AssemblyName}");
                    sb.AppendLine($"Namespace: {m_MonoScript.m_Namespace}");
                    sb.AppendLine($"Class: {m_MonoScript.m_ClassName}");
                    WriteTextExport(filePath, sb.ToString());
                    return true;
                }

                case Shader m_Shader:
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".shader", item, out var filePath)) return false;
                    var str = m_Shader.Convert();
                    WriteTextExport(filePath, str);
                    return true;
                }

                case Font m_Font:
                {
                    if (m_Font.m_FontData == null || m_Font.m_FontData.Length == 0) return false;
                    var ext = ".ttf";
                    if (m_Font.m_FontData.Length >= 4
                        && m_Font.m_FontData[0] == 79
                        && m_Font.m_FontData[1] == 84
                        && m_Font.m_FontData[2] == 84
                        && m_Font.m_FontData[3] == 79)
                    {
                        ext = ".otf";
                    }
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ext, item, out var filePath)) return false;
                    WriteBytesExport(filePath, m_Font.m_FontData);
                    return true;
                }

                case Sprite m_Sprite:
                {
                    using var image = m_Sprite.GetImage();
                    if (image == null) return false;
                    var format = exportOptions.ConvertTextureFormat;
                    var extension = "." + format.ToString().ToLowerInvariant();
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                    try
                    {
                        using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                        image.WriteToStream(file, format);
                    }
                    catch
                    {
                        TryDeletePartialExport(filePath);
                        throw;
                    }
                    AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Sprite);
                    return true;
                }

                case VideoClip m_VideoClip:
                {
                    if (m_VideoClip.m_ExternalResources.m_Size <= 0) return false;
                    var extension = SanitizeExtension(Path.GetExtension(m_VideoClip.m_OriginalPath), ".mp4");
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                    try
                    {
                        m_VideoClip.m_VideoData.WriteData(filePath);
                    }
                    catch
                    {
                        TryDeletePartialExport(filePath);
                        throw;
                    }
                    return true;
                }

                case VideoPlayer m_VideoPlayer:
                {
                    if (m_VideoPlayer.m_VideoClip.TryGet(out var resolvedClip) && resolvedClip != null)
                    {
                        if (resolvedClip.m_ExternalResources.m_Size <= 0) return false;
                        var extension = SanitizeExtension(Path.GetExtension(resolvedClip.m_OriginalPath), ".mp4");
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, extension, item, out var filePath)) return false;
                        try
                        {
                            resolvedClip.m_VideoData.WriteData(filePath);
                        }
                        catch
                        {
                            TryDeletePartialExport(filePath);
                            throw;
                        }
                        return true;
                    }
                    else if (m_VideoPlayer.m_Source == 1 && !string.IsNullOrEmpty(m_VideoPlayer.m_Url))
                    {
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName + "_url", ".txt", item, out var filePath)) return false;
                        WriteTextExport(filePath, m_VideoPlayer.m_Url);
                        return true;
                    }
                    return false;
                }

                case MovieTexture m_MovieTexture:
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".ogv", item, out var filePath)) return false;
                    WriteBytesExport(filePath, m_MovieTexture.m_MovieData);
                    return true;
                }

                case MonoBehaviour m_MonoBehaviour:
                {
                    TypeTree? convertedTypeTree = null;
                    bool typeTreeConverted = false;
                    TypeTree? GetOrConvertTypeTree()
                    {
                        if (!typeTreeConverted && assemblyLoader != null && assemblyLoader.Loaded)
                        {
                            typeTreeConverted = true;
                            lock (assemblyLoader)
                            {
                                convertedTypeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                            }
                        }
                        return convertedTypeTree;
                    }

                    object? obj = m_MonoBehaviour.ToType();
                    if (obj == null && assemblyLoader != null && assemblyLoader.Loaded)
                    {
                        var typeTree = GetOrConvertTypeTree();
                        if (typeTree != null)
                        {
                            obj = m_MonoBehaviour.ToType(typeTree);
                        }
                    }

                    if (obj != null)
                    {
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".json", item, out var filePath)) return false;
                        var str = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                        WriteTextExport(filePath, str);
                        return true;
                    }

                    var dumpStr = m_MonoBehaviour.Dump();
                    if (dumpStr == null && assemblyLoader != null && assemblyLoader.Loaded)
                    {
                        var typeTree = GetOrConvertTypeTree();
                        if (typeTree != null)
                        {
                            dumpStr = m_MonoBehaviour.Dump(typeTree);
                        }
                    }

                    if (dumpStr != null)
                    {
                        if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".txt", item, out var dumpPath)) return false;
                        WriteTextExport(dumpPath, dumpStr);
                        return true;
                    }

                    return false;
                }

                case AssetStudio.Object obj when obj.type == ClassIDType.PrefabInstance:
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName + "_prefab_report", ".txt", item, out var filePath)) return false;
                    var report = formatPrefab != null ? formatPrefab(obj) : (obj.Dump() ?? string.Empty);
                    WriteTextExport(filePath, report);
                    return true;
                }

                default:
                {
                    if (!conversionContext.TryGetAssetFilePath(exportPath, fileName, ".dat", item, out var filePath)) return false;
                    WriteRawAssetData(assetObj, filePath);
                    return true;
                }
            }
        }

        public static bool ExportMeshToObj(Mesh mesh, string objPath)
        {
            mesh.EnsureProcessed();
            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
            {
                return false;
            }

            var vertexComponentCount = mesh.m_Vertices.Length / mesh.m_VertexCount;
            if (vertexComponentCount < 3)
            {
                return false;
            }

            vertexComponentCount = Math.Min(vertexComponentCount, 4);
            var uvs = mesh.m_UV0;
            var normals = mesh.m_Normals;
            var uvComponentCount = uvs != null && uvs.Length >= mesh.m_VertexCount * 2
                ? Math.Min(uvs.Length / mesh.m_VertexCount, 4)
                : 0;
            var normalComponentCount = normals != null && normals.Length >= mesh.m_VertexCount * 3
                ? Math.Min(normals.Length / mesh.m_VertexCount, 4)
                : 0;
            var hasUvs = uvComponentCount >= 2;
            var hasNormals = normalComponentCount >= 3;

            try
            {
                using var writer = new StreamWriter(
                    objPath,
                    append: false,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 64 * 1024);
                var groupName = FixObjGroupName(mesh.m_Name);
                writer.WriteLine("g " + groupName);

                for (int vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                {
                    writer.WriteLine(
                        "v {0} {1} {2}",
                        CleanFloat(-mesh.m_Vertices[vertex * vertexComponentCount]),
                        CleanFloat(mesh.m_Vertices[vertex * vertexComponentCount + 1]),
                        CleanFloat(mesh.m_Vertices[vertex * vertexComponentCount + 2]));
                }

                if (hasUvs)
                {
                    for (int vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                    {
                        writer.WriteLine(
                            "vt {0} {1}",
                            CleanFloat(uvs![vertex * uvComponentCount]),
                            CleanFloat(uvs[vertex * uvComponentCount + 1]));
                    }
                }

                if (hasNormals)
                {
                    for (int vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                    {
                        writer.WriteLine(
                            "vn {0} {1} {2}",
                            CleanFloat(-normals![vertex * normalComponentCount]),
                            CleanFloat(normals[vertex * normalComponentCount + 1]),
                            CleanFloat(normals[vertex * normalComponentCount + 2]));
                    }
                }

                var firstIndex = 0;
                for (var subMeshIndex = 0; subMeshIndex < mesh.m_SubMeshes.Length && firstIndex < mesh.m_Indices.Count; subMeshIndex++)
                {
                    writer.WriteLine($"g {groupName}_{subMeshIndex.ToString(CultureInfo.InvariantCulture)}");
                    var requestedIndexCount = (long)mesh.m_SubMeshes[subMeshIndex].indexCount;
                    var availableIndexCount = mesh.m_Indices.Count - firstIndex;
                    var indexCount = (int)Math.Min(requestedIndexCount, availableIndexCount);
                    var faceCount = indexCount / 3;
                    for (var face = 0; face < faceCount; face++)
                    {
                        var faceStart = firstIndex + face * 3;
                        var first = mesh.m_Indices[faceStart + 2];
                        var second = mesh.m_Indices[faceStart + 1];
                        var third = mesh.m_Indices[faceStart];
                        if (first >= mesh.m_VertexCount || second >= mesh.m_VertexCount || third >= mesh.m_VertexCount)
                        {
                            continue;
                        }

                        writer.WriteLine(
                            "f {0} {1} {2}",
                            FormatObjVertexReference(first, hasUvs, hasNormals),
                            FormatObjVertexReference(second, hasUvs, hasNormals),
                            FormatObjVertexReference(third, hasUvs, hasNormals));
                    }
                    firstIndex += indexCount;
                }

                return true;
            }
            catch
            {
                TryDeletePartialExport(objPath);
                throw;
            }
        }

        public static void ExportFbx(
            IImported convert,
            string exportFile,
            ExportOptionsState exportOptions,
            ConcurrentDictionary<string, byte>? reservedPaths = null)
        {
            if (exportOptions.ExportAnimations && exportOptions.ExportAnimationsSplit && convert.AnimationList?.Count > 0)
            {
                var effectiveConvert = exportOptions.CombineMeshes && convert.MeshList?.Count > 1
                    ? ModelConverter.CreateCombinedView(convert)
                    : convert;

                var mainConvert = new ImportedWrapper(effectiveConvert)
                {
                    AnimationList = new List<ImportedKeyframedAnimation>()
                };
                try
                {
                    ModelExporter.ExportFbx(exportFile, mainConvert,
                        exportOptions.ExportSkins,
                        false,
                        exportOptions.ExportBlendShape,
                        exportOptions.CastToBone,
                        (float)exportOptions.BoneSize,
                        (float)exportOptions.ScaleFactor,
                        exportOptions.FbxVersion,
                        exportOptions.FbxFormat == 1,
                        exportOptions.CombineMeshes);
                }
                catch
                {
                    TryDeletePartialExport(exportFile);
                    throw;
                }

                if (exportOptions.ExportUnityMeta)
                {
                    AssetExportHelper.WriteModelMetaIfMissing(exportFile);
                }

                foreach (var anim in effectiveConvert.AnimationList)
                {
                    var candidateAnimFile = Path.Combine(
                        Path.GetDirectoryName(exportFile)!,
                        $"{Path.GetFileNameWithoutExtension(exportFile)}_{FixFileName(anim.Name)}.fbx");
                    var animFile = reservedPaths != null
                        ? ReserveUniqueFilePath(candidateAnimFile, reservedPaths)
                        : GetUniqueFilePath(candidateAnimFile);
                    var animConvert = new ImportedWrapper(effectiveConvert)
                    {
                        AnimationList = new List<ImportedKeyframedAnimation> { anim }
                    };
                    try
                    {
                        ModelExporter.ExportFbx(animFile, animConvert,
                            exportOptions.ExportSkins,
                            true,
                            exportOptions.ExportBlendShape,
                            exportOptions.CastToBone,
                            (float)exportOptions.BoneSize,
                            (float)exportOptions.ScaleFactor,
                            exportOptions.FbxVersion,
                            exportOptions.FbxFormat == 1,
                            exportOptions.CombineMeshes);
                    }
                    catch
                    {
                        TryDeletePartialExport(animFile);
                        throw;
                    }

                    if (exportOptions.ExportUnityMeta)
                    {
                        AssetExportHelper.WriteAnimationMetaIfMissing(animFile);
                    }

                    if (exportOptions.ExportUnityAnimClips)
                    {
                        var animClipPath = Path.Combine(
                            Path.GetDirectoryName(exportFile)!,
                            $"{Path.GetFileNameWithoutExtension(exportFile)}_{FixFileName(anim.Name)}.anim");
                        AssetExportHelper.ExportUnityAnimationClip(anim, animClipPath);
                    }
                }
            }
            else
            {
                try
                {
                    ModelExporter.ExportFbx(exportFile, convert,
                        exportOptions.ExportSkins,
                        exportOptions.ExportAnimations,
                        exportOptions.ExportBlendShape,
                        exportOptions.CastToBone,
                        (float)exportOptions.BoneSize,
                        (float)exportOptions.ScaleFactor,
                        exportOptions.FbxVersion,
                        exportOptions.FbxFormat == 1,
                        exportOptions.CombineMeshes);
                }
                catch
                {
                    TryDeletePartialExport(exportFile);
                    throw;
                }

                if (exportOptions.ExportUnityMeta)
                {
                    AssetExportHelper.WriteModelMetaIfMissing(exportFile);
                }
            }
        }

        public SplitObjectsExportSummary ExportSplitObjects(
            string exportRoot,
            IReadOnlyList<GameObjectNode> exportNodes,
            AnimationClip[]? clips,
            bool createObjectFolders,
            ExportOptionsState exportOptions,
            ExportViewModel? viewModel = null,
            Action<int>? uiProgressCallback = null,
            Action<string>? statusUpdate = null,
            int maxDegreeOfParallelism = 0,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(exportNodes);
            ArgumentNullException.ThrowIfNull(exportOptions);

            var totalNodes = exportNodes.Count;
            if (totalNodes == 0)
            {
                return new SplitObjectsExportSummary(0, 0, Array.Empty<string>());
            }

            Directory.CreateDirectory(exportRoot);

            int exported = 0;
            int failed = 0;
            int completedNodes = 0;
            bool wasCancelled = false;
            var errorQueue = new ConcurrentQueue<string>();
            var reservedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var currentExportPath = Path.Combine(exportRoot, "export-current.txt");
            var progressTracker = new ExportProgressTracker(
                currentExportPath,
                totalNodes,
                uiProgressCallback,
                DefaultCurrentExportWriteIntervalMs,
                DefaultExportUiProgressIntervalMs);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(GetExportThreadCount(maxDegreeOfParallelism), totalNodes),
                CancellationToken = cancellationToken
            };

            try
            {
                Parallel.For(0, totalNodes, parallelOptions, i =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = exportNodes[i];
                    var gameObject = node.GameObject!;
                    var targetFolder = createObjectFolders
                        ? ReserveUniqueDirectoryPath(Path.Combine(exportRoot, FixFileName(gameObject.m_Name)), reservedPaths)
                        : exportRoot;
                    Directory.CreateDirectory(targetFolder);

                    var exportFile = ReserveUniqueFilePath(
                        Path.Combine(targetFolder, FixFileName(gameObject.m_Name) + ".fbx"),
                        reservedPaths);
                    statusUpdate?.Invoke($"Exporting {Path.GetFileName(exportFile)}");

                    bool forceFlushCurrent = false;
                    try
                    {
                        var convert = clips == null
                            ? new ModelConverter(gameObject, exportOptions.ConvertTextureFormat)
                            : new ModelConverter(gameObject, exportOptions.ConvertTextureFormat, clips);
                        ExportFbx(convert, exportFile, exportOptions, reservedPaths);
                        Interlocked.Increment(ref exported);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        forceFlushCurrent = true;
                        Interlocked.Increment(ref failed);
                        var error = $"Export GameObject:{gameObject.m_Name} error";
                        errorQueue.Enqueue($"{error}{Environment.NewLine}{ex}");
                        Logger.Error(error, ex);
                        statusUpdate?.Invoke($"Error exporting {gameObject.m_Name}: {ex.Message}");
                    }

                    var currentCompleted = Interlocked.Increment(ref completedNodes);
                    var source = gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fullName ?? gameObject.assetsFile?.fileName;
                    progressTracker.ReportCustomProgress(
                        typeString: "GameObject",
                        name: gameObject.m_Name,
                        pathId: gameObject.m_PathID,
                        source: source,
                        completedCount: currentCompleted,
                        forceFlush: forceFlushCurrent || currentCompleted == 1 || currentCompleted == totalNodes);

                    viewModel?.ReportProgress(
                        currentCompleted,
                        totalNodes,
                        gameObject.m_Name,
                        Volatile.Read(ref exported),
                        0,
                        Volatile.Read(ref failed),
                        force: forceFlushCurrent || currentCompleted == totalNodes);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
            }
            finally
            {
                ClearCurrentExport(exportRoot);
            }

            return new SplitObjectsExportSummary(exported, failed, errorQueue.ToArray(), wasCancelled);
        }

        public static Dictionary<Mesh, EagerModelGroupEntry> BuildEagerModelGroupLookup(
            IReadOnlyList<SerializedFile> filesSnapshot)
        {
            var result = new Dictionary<Mesh, EagerModelGroupEntry>(ReferenceEqualityComparer.Instance);
            if (filesSnapshot == null || filesSnapshot.Count == 0)
            {
                return result;
            }

            var ownerByMesh = new Dictionary<Mesh, (Renderer OwnerRenderer, GameObject OwnerGo)>(ReferenceEqualityComparer.Instance);
            for (int f = 0; f < filesSnapshot.Count; f++)
            {
                var objects = filesSnapshot[f].Objects;
                for (int i = 0; i < objects.Count; i++)
                {
                    var obj = objects[i];
                    if (obj is SkinnedMeshRenderer smr
                        && smr.m_Mesh != null
                        && smr.m_Mesh.TryGet(out var smrMesh)
                        && smrMesh != null
                        && !ownerByMesh.ContainsKey(smrMesh))
                    {
                        GameObject? ownerGo = null;
                        smr.m_GameObject?.TryGet(out ownerGo);
                        if (ownerGo != null)
                        {
                            ownerByMesh[smrMesh] = (smr, ownerGo);
                        }
                    }
                    else if (obj is MeshFilter mf
                        && mf.m_Mesh != null
                        && mf.m_Mesh.TryGet(out var mfMesh)
                        && mfMesh != null
                        && !ownerByMesh.ContainsKey(mfMesh)
                        && mf.m_GameObject != null
                        && mf.m_GameObject.TryGet(out var mfGo)
                        && mfGo?.m_MeshRenderer != null)
                    {
                        ownerByMesh[mfMesh] = (mfGo.m_MeshRenderer, mfGo);
                    }
                }
            }

            if (ownerByMesh.Count == 0)
            {
                return result;
            }

            var subtreeCacheByRoot = new Dictionary<GameObject, (List<Mesh> Meshes, List<Renderer> Renderers)>(ReferenceEqualityComparer.Instance);
            var visited = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);

            foreach (var kvp in ownerByMesh)
            {
                var mesh = kvp.Key;
                var ownerRenderer = kvp.Value.OwnerRenderer;
                var ownerGo = kvp.Value.OwnerGo;

                GameObject? animatorRoot = null;
                GameObject? multiRendererRoot = null;
                Avatar? avatar = null;
                var current = ownerGo;
                visited.Clear();

                while (current != null && visited.Add(current))
                {
                    if (current.m_Animator != null)
                    {
                        animatorRoot = current;
                        if (current.m_Animator.m_Avatar != null)
                        {
                            current.m_Animator.m_Avatar.TryGet(out avatar);
                        }
                        break;
                    }

                    if (multiRendererRoot == null && CountRenderersInSubtree(current, maxCount: 2) >= 2)
                    {
                        multiRendererRoot = current;
                    }

                    var parentTransform = current.m_Transform?.m_Father != null && current.m_Transform.m_Father.TryGet(out var father)
                        ? father
                        : null;
                    if (parentTransform == null || parentTransform.m_GameObject == null || !parentTransform.m_GameObject.TryGet(out var parentGo))
                    {
                        break;
                    }
                    current = parentGo;
                }

                var rootGameObject = animatorRoot ?? multiRendererRoot ?? ownerGo;
                var groupName = !string.IsNullOrWhiteSpace(rootGameObject.m_Name)
                    ? rootGameObject.m_Name
                    : (mesh.m_Name ?? "Model");

                if (!subtreeCacheByRoot.TryGetValue(rootGameObject, out var subtree))
                {
                    var collectedMeshes = new List<Mesh>();
                    var collectedRenderers = new List<Renderer>();
                    CollectSubtreeMeshesAndRenderers(rootGameObject, collectedMeshes, collectedRenderers);
                    subtree = (collectedMeshes, collectedRenderers);
                    subtreeCacheByRoot[rootGameObject] = subtree;
                }

                List<Mesh> groupMeshes = subtree.Meshes;
                List<Renderer> groupRenderers = subtree.Renderers;
                if (groupMeshes.Count == 0 && ownerRenderer != null)
                {
                    groupMeshes = new List<Mesh>(1) { mesh };
                    groupRenderers = new List<Renderer>(1) { ownerRenderer };
                }

                if (groupMeshes.Count > 0)
                {
                    result[mesh] = new EagerModelGroupEntry
                    {
                        GroupName = groupName,
                        RootGameObject = rootGameObject,
                        Meshes = groupMeshes,
                        Renderers = groupRenderers,
                        Avatar = avatar
                    };
                }
            }

            return result;
        }

        public static int ExtractFolder(
            string path,
            string savePath,
            Action<string>? statusUpdate = null,
            Action<int, int>? progressReport = null,
            ExportViewModel? viewModel = null,
            int maxDegreeOfParallelism = 0,
            CancellationToken cancellationToken = default)
        {
            var sourceRoot = Path.GetFullPath(path);
            var saveRoot = Path.GetFullPath(savePath);
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var files = ImportHelper.GetFilesSafe(sourceRoot, "*.*", true);
            var total = files.Length;
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

                if (!IsPathWithinRoot(fileSavePath, saveRoot))
                {
                    Logger.Warning($"Blocked folder extraction target outside destination root: '{file}'");
                    var blockedCompleted = Interlocked.Increment(ref completedCount);
                    ReportThrottledExtractionProgress(
                        blockedCompleted,
                        total,
                        ref lastReportedPercent,
                        ref lastProgressTicks,
                        progressReport);
                    viewModel?.ReportProgress(
                        blockedCompleted,
                        total,
                        Path.GetFileName(file),
                        Volatile.Read(ref extractedCount),
                        0,
                        0,
                        force: blockedCompleted == total);
                    return;
                }

                var count = ExtractSingleFile(file, fileSavePath, statusUpdate, reservedOutputPaths);
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
                viewModel?.ReportProgress(
                    completed,
                    total,
                    Path.GetFileName(file),
                    Volatile.Read(ref extractedCount),
                    0,
                    0,
                    force: completed == total);
            });

            return extractedCount;
        }

        public static int ExtractFiles(
            string[] fileNames,
            string savePath,
            Action<string>? statusUpdate = null,
            Action<int, int>? progressReport = null,
            ExportViewModel? viewModel = null,
            int maxDegreeOfParallelism = 0,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(fileNames);
            var total = fileNames.Length;
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
                var inputFile = fileNames[i];
                var count = ExtractSingleFile(inputFile, savePath, statusUpdate, reservedOutputPaths);
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
                viewModel?.ReportProgress(
                    completed,
                    total,
                    Path.GetFileName(inputFile),
                    Volatile.Read(ref extractedCount),
                    0,
                    0,
                    force: completed == total);
            });

            return extractedCount;
        }

        public static int ExtractSingleFile(
            string fileName,
            string savePath,
            Action<string>? statusUpdate = null,
            ConcurrentDictionary<string, byte>? reservedOutputPaths = null)
        {
            using var reader = new FileReader(fileName);
            return reader.FileType switch
            {
                FileType.BundleFile => ExtractBundleFile(reader, savePath, statusUpdate, reservedOutputPaths),
                FileType.WebFile => ExtractWebDataFile(reader, savePath, statusUpdate, reservedOutputPaths),
                _ => 0,
            };
        }

        public static int ExtractStreamFiles(
            string extractPath,
            StreamFile[] fileList,
            ConcurrentDictionary<string, byte>? reservedOutputPaths = null)
        {
            ArgumentNullException.ThrowIfNull(fileList);

            var extractedCount = 0;
            foreach (var file in fileList)
            {
                try
                {
                    var rawPath = file.path ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(rawPath) || file.stream == null)
                    {
                        continue;
                    }

                    if (!TryBuildSafeChildPath(extractPath, rawPath, out var filePath, out var blockReason))
                    {
                        if (!string.IsNullOrEmpty(blockReason))
                        {
                            Logger.Warning(blockReason);
                        }
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

        private static int ExtractBundleFile(
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

        private static int ExtractWebDataFile(
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
            var timeElapsed = nowTicks - prevTicks >= DefaultExtractionProgressThrottleMs;

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

        private static int CountRenderersInSubtree(GameObject root, int maxCount)
        {
            int count = 0;
            var visited = new HashSet<Transform>();
            var stack = new Stack<Transform>();
            if (root.m_Transform != null)
            {
                stack.Push(root.m_Transform);
            }
            else if (root.m_MeshRenderer != null || root.m_SkinnedMeshRenderer != null)
            {
                return 1;
            }

            while (stack.Count > 0 && count < maxCount)
            {
                var tr = stack.Pop();
                if (tr == null || !visited.Add(tr)) continue;
                if (tr.m_GameObject != null && tr.m_GameObject.TryGet(out var go) && go != null)
                {
                    if (go.m_SkinnedMeshRenderer != null || (go.m_MeshRenderer != null && go.m_MeshFilter != null))
                    {
                        count++;
                    }
                }

                if (tr.m_Children != null)
                {
                    for (int i = 0; i < tr.m_Children.Length; i++)
                    {
                        if (tr.m_Children[i] != null && tr.m_Children[i].TryGet(out var child) && child != null)
                        {
                            stack.Push(child);
                        }
                    }
                }
            }

            return count;
        }

        internal static void CollectSubtreeMeshesAndRenderers(GameObject root, List<Mesh> meshes, List<Renderer> renderers)
        {
            var visited = new HashSet<Transform>();
            var stack = new Stack<Transform>();
            if (root.m_Transform != null)
            {
                stack.Push(root.m_Transform);
            }

            while (stack.Count > 0)
            {
                var tr = stack.Pop();
                if (tr == null || !visited.Add(tr)) continue;
                if (tr.m_GameObject != null && tr.m_GameObject.TryGet(out var go) && go != null)
                {
                    if (go.m_SkinnedMeshRenderer != null
                        && go.m_SkinnedMeshRenderer.m_Mesh != null
                        && go.m_SkinnedMeshRenderer.m_Mesh.TryGet(out var smrMesh)
                        && smrMesh != null)
                    {
                        meshes.Add(smrMesh);
                        renderers.Add(go.m_SkinnedMeshRenderer);
                    }
                    if (go.m_MeshRenderer != null
                        && go.m_MeshFilter != null
                        && go.m_MeshFilter.m_Mesh != null
                        && go.m_MeshFilter.m_Mesh.TryGet(out var mfMesh)
                        && mfMesh != null)
                    {
                        meshes.Add(mfMesh);
                        renderers.Add(go.m_MeshRenderer);
                    }
                }

                if (tr.m_Children != null)
                {
                    for (int i = tr.m_Children.Length - 1; i >= 0; i--)
                    {
                        if (tr.m_Children[i] != null && tr.m_Children[i].TryGet(out var child) && child != null)
                        {
                            stack.Push(child);
                        }
                    }
                }
            }
        }

        private static string FormatObjVertexReference(uint zeroBasedIndex, bool hasUvs, bool hasNormals)
        {
            var index = ((ulong)zeroBasedIndex + 1UL).ToString(CultureInfo.InvariantCulture);
            if (hasUvs && hasNormals) return $"{index}/{index}/{index}";
            if (hasUvs) return $"{index}/{index}";
            if (hasNormals) return $"{index}//{index}";
            return index;
        }

        private static string FixObjGroupName(string? name)
        {
            return FixFileName(name ?? string.Empty)
                .Replace(' ', '_')
                .Replace('\t', '_');
        }

        private static string CleanFloat(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? "0"
                : value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
