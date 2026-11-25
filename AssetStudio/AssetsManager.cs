using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static AssetStudio.ImportHelper;

namespace AssetStudio
{
    public enum MemoryPressureResult
    {
        Cancel,
        Continue,
        StopAndKeep
    }

    public class AssetsManager : IDisposable
    {
        private static readonly List<WeakReference<AssetsManager>> activeManagers = new List<WeakReference<AssetsManager>>();
        private static readonly object activeManagersLock = new object();

        private void RegisterManager()
        {
            lock (activeManagersLock)
            {
                activeManagers.Add(new WeakReference<AssetsManager>(this));
            }
        }

        private static void EvictLruCachesAcrossAllManagers(int count)
        {
            lock (activeManagersLock)
            {
                activeManagers.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (var weakRef in activeManagers)
                {
                    if (weakRef.TryGetTarget(out var manager))
                    {
                        if (manager.LazyLoading)
                        {
                            manager.LruCache.EvictCount(count);
                        }
                    }
                }
            }
        }

        public readonly AssetLruCache LruCache = new AssetLruCache(500);

        public AssetsManager()
        {
            RegisterManager();
        }
        public const double DefaultLoadThreadRatio = 0.75;
        public const double DefaultReadThreadRatio = 0.75;
        public const double DefaultLazyLoadThreadRatio = 0.75;
        public const double DefaultLazyReadThreadRatio = 0.75;
        public const int MaxDefaultThreadCount = 16;
        private const int DefaultMemoryLimitPercent = 90;
        private static readonly ConcurrentDictionary<string, int> ConfiguredThreadCountCache = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, int> ConfiguredThreadCountOrDefaultCache = new ConcurrentDictionary<string, int>();
        private static readonly int DefaultLazyLoadThreadCount = GetConfiguredThreadCount("ASSETSTUDIO_LAZY_LOAD_THREADS", DefaultLazyLoadThreadRatio);
        private static readonly int DefaultLazyReadThreadCount = GetConfiguredThreadCount("ASSETSTUDIO_LAZY_READ_THREADS", DefaultLazyReadThreadRatio);
        private static long lastGCCollectTime = 0;

        public static bool DisableMemoryPressureCheck = false;
        public static Func<string, int, int, MemoryPressureResult>? MemoryPressureCallback;
        public static Func<bool>? ShouldYieldForUserInteraction;
        public Func<string, bool>? ShouldKeepFileCallback;
        public static volatile bool ShouldStopLoading = false;

        public string SpecifyUnityVersion = string.Empty;
        public string? ProjectRoot;
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();
        public ConcurrentBag<Stream> bundleStreams = new ConcurrentBag<Stream>();
        public ProjectIndex ProjectIndex = new ProjectIndex();
        public bool LazyLoading = false;
        public bool AutoLoadCatalogDependencies { get; set; } = true;
        public int MaxAutoLoadCatalogDependencies { get; set; } = AddressablesCatalogDependencyResolver.DefaultMaxDependencyBundles;
        public bool AutoLoadExternalDependencies { get; set; } = true;
        public int MaxAutoLoadExternalDependencies { get; set; } = 0;
        public int ExternalCascadePromptThreshold { get; set; } = 16;
        public Func<int, int, bool>? ConfirmExternalCascadeCallback { get; set; }
        public int LastSkippedExternalDependencyCount { get; private set; }
        public DecompressedBundleCache? DecompressionCache { get; set; }

        private readonly object unityVersionInferenceLock = new object();
        private string inferredUnityVersion = "";

        internal ConcurrentDictionary<string, int> assetsFileIndexCache = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal ConcurrentDictionary<string, BinaryReader> resourceFileReaders = new ConcurrentDictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);
        // Tracks every registered resource BinaryReader so Clear() disposes overwritten duplicates too.
        private readonly ConcurrentBag<BinaryReader> ownedResourceReaders = new ConcurrentBag<BinaryReader>();

        private ConcurrentDictionary<string, byte> assetsFileListHash = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, System.Threading.ManualResetEventSlim> assetsFileLoadEvents = new ConcurrentDictionary<string, System.Threading.ManualResetEventSlim>(StringComparer.OrdinalIgnoreCase);

        public readonly object loadLock = new object();
        private readonly object concurrencyGateLock = new object();
        private System.Threading.SemaphoreSlim lazyLoadGate = new System.Threading.SemaphoreSlim(DefaultLazyLoadThreadCount);
        private System.Threading.SemaphoreSlim lazyReadGate = new System.Threading.SemaphoreSlim(DefaultLazyReadThreadCount);
        private int lazyLoadGateLimit = DefaultLazyLoadThreadCount;
        private int lazyReadGateLimit = DefaultLazyReadThreadCount;

        private sealed class LoadContext
        {
            public readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
            public readonly ConcurrentBag<string> ImportFiles = new ConcurrentBag<string>();
            public readonly ConcurrentBag<SerializedFile> LoadedAssetsFiles = new ConcurrentBag<SerializedFile>();
            public readonly ConcurrentDictionary<string, byte> ImportFilesHash = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            public readonly ConcurrentDictionary<string, byte> NoExistFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            public readonly ConcurrentBag<Stream> BundleStreams = new ConcurrentBag<Stream>();
            public readonly ConcurrentBag<BinaryReader> IsolatedResourceReaders = new ConcurrentBag<BinaryReader>();
            public readonly ConcurrentDictionary<string, byte> SkippedExternalFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            public readonly bool IsolateStreams;
            public readonly bool AllowExternalCascadeGuard;
            public readonly bool SerializedFilesOnly;
            public readonly bool SkipDecompressionCache;
            public readonly object CascadePromptLock = new object();
            public int TotalCount;
            public int EnqueuedExternalCount;
            public int CascadeDecision; // 0 = undecided, 1 = allow, -1 = stop
            private System.Threading.Tasks.TaskCompletionSource<bool> _queueChangedTcs =
                new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            public System.Threading.Tasks.Task GetQueueChangedTask()
            {
                return System.Threading.Volatile.Read(ref _queueChangedTcs).Task;
            }

            public void SignalQueueChanged()
            {
                var next = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                var previous = System.Threading.Interlocked.Exchange(ref _queueChangedTcs, next);
                previous.TrySetResult(true);
            }

            public LoadContext(
                int totalCount,
                bool isolateStreams = false,
                bool allowExternalCascadeGuard = false,
                bool serializedFilesOnly = false,
                bool skipDecompressionCache = false)
            {
                TotalCount = totalCount;
                IsolateStreams = isolateStreams;
                AllowExternalCascadeGuard = allowExternalCascadeGuard;
                SerializedFilesOnly = serializedFilesOnly;
                SkipDecompressionCache = skipDecompressionCache;
            }
        }

        private static string NormalizeResourceKey(string value)
        {
            return value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private static string BuildSafeContainerPath(string? baseDirectory, string? entryPath)
        {
            var baseDir = baseDirectory ?? string.Empty;
            var safeSegments = (entryPath ?? string.Empty)
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(seg => seg != "." && seg != ".." && !seg.Contains(':'))
                .ToArray();
            if (safeSegments.Length == 0)
            {
                return Path.Combine(baseDir, "unnamed");
            }

            return Path.Combine(baseDir, Path.Combine(safeSegments));
        }

        private static bool IsPathWithinDirectory(string? candidatePath, string? directoryPath)
        {
            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(directoryPath))
            {
                return false;
            }

            try
            {
                var fullCandidate = Path.GetFullPath(candidatePath);
                var fullDirectory = Path.GetFullPath(directoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return fullCandidate.StartsWith(fullDirectory, comparison);
            }
            catch
            {
                return false;
            }
        }

        internal void RegisterResourceFileReader(BinaryReader? reader, params string?[] keys)
        {
            RegisterResourceFileReader(reader, context: null, keys);
        }

        private void RegisterResourceFileReader(BinaryReader? reader, LoadContext? context, params string?[] keys)
        {
            if (reader == null)
            {
                return;
            }

            if (context != null && context.IsolateStreams)
            {
                context.IsolatedResourceReaders.Add(reader);
                return;
            }

            ownedResourceReaders.Add(reader);
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                resourceFileReaders[key] = reader;
                var normalizedKey = NormalizeResourceKey(key);
                resourceFileReaders[normalizedKey] = reader;
            }
        }

        private void DisposeResourceReaders()
        {
            var disposed = new HashSet<BinaryReader>(ReferenceEqualityComparer.Instance);
            while (ownedResourceReaders.TryTake(out var ownedReader))
            {
                if (ownedReader != null && disposed.Add(ownedReader))
                {
                    ownedReader.Close();
                }
            }

            foreach (var resourceFileReader in resourceFileReaders)
            {
                if (resourceFileReader.Value != null && disposed.Add(resourceFileReader.Value))
                {
                    resourceFileReader.Value.Close();
                }
            }

            resourceFileReaders.Clear();
        }

        private System.Threading.ManualResetEventSlim GetAssetsFileLoadEvent(string? fileName)
        {
            return assetsFileLoadEvents.GetOrAdd(fileName ?? string.Empty, _ => new System.Threading.ManualResetEventSlim(false));
        }

        public bool WaitForAssetsFileLoaded(string? fileName, int timeoutMilliseconds)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            if (TryFindSerializedFile(fileName, null, out _))
            {
                return true;
            }

            if (assetsFileLoadEvents.TryGetValue(fileName, out var eventHandle))
            {
                return eventHandle.Wait(timeoutMilliseconds);
            }

            return false;
        }

        public bool TryFindSerializedFile(string? serializedFileName, string? originalPath, out SerializedFile result)
        {
            lock (loadLock)
            {
                result = null!;
                if (!string.IsNullOrEmpty(originalPath))
                {
                    result = assetsFileList.FirstOrDefault(file => IsSameAssetSource(file.originalPath, originalPath)
                        || IsSameAssetSource(file.fullName, originalPath))!;
                    if (result != null)
                    {
                        return true;
                    }
                }

                if (string.IsNullOrEmpty(originalPath) && !string.IsNullOrEmpty(serializedFileName))
                {
                    result = assetsFileList.FirstOrDefault(file =>
                        string.Equals(file.fileName, serializedFileName, StringComparison.OrdinalIgnoreCase))!;
                }
            }

            return result != null;
        }

        private static bool IsSameAssetSource(string? left, string? right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void LoadFiles(params string?[] files)
        {
            LoadFilesAsync(files).GetAwaiter().GetResult();
        }

        public async System.Threading.Tasks.Task LoadFilesAsync(params string?[] files)
        {
            await LoadFilesAsync(false, files);
        }

        public async System.Threading.Tasks.Task LoadFilesForConnectionsAsync(params string?[] files)
        {
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            if (files == null || files.Length == 0)
            {
                return;
            }

            var nonEmptyFiles = files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToArray();
            if (nonEmptyFiles.Length == 0)
            {
                return;
            }

            var path = Path.GetDirectoryName(Path.GetFullPath(nonEmptyFiles[0]));
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = path;
            }

            var dirComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (var dir in nonEmptyFiles
                .Where(f => f.EndsWith(".split0", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetDirectoryName(Path.GetFullPath(f)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(dirComparer))
            {
                MergeSplitAssets(dir);
            }

            var toReadFile = ProcessingSplitFiles(nonEmptyFiles);
            await LoadAsync(
                toReadFile,
                highPriority: false,
                allowAutoDependencies: false,
                allowExternalCascadeGuard: false,
                serializedFilesOnly: true,
                skipDecompressionCache: true);
        }

        public void LoadFilesForPreview(params string?[] files)
        {
            LoadFilesAsync(true, files).GetAwaiter().GetResult();
        }

        private async System.Threading.Tasks.Task LoadFilesAsync(bool highPriority, params string?[] files)
        {
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            if (files == null || files.Length == 0)
            {
                return;
            }
            var nonEmptyFiles = files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToArray();
            if (nonEmptyFiles.Length == 0)
            {
                return;
            }
            var path = Path.GetDirectoryName(Path.GetFullPath(nonEmptyFiles[0]));
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = path;
            }
            // In Lazy preview loads for non-split files, skip scanning the entire bundle directory for *.split0
            // on every single asset click (cold folder indexing already merges split assets upfront).
            var shouldScanSplitAssets = !LazyLoading
                || !highPriority
                || nonEmptyFiles.Any(static f => f.EndsWith(".split0", StringComparison.OrdinalIgnoreCase));
            if (shouldScanSplitAssets)
            {
                var dirComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                foreach (var dir in nonEmptyFiles
                    .Select(f => Path.GetDirectoryName(Path.GetFullPath(f)))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Select(d => d!)
                    .Distinct(dirComparer))
                {
                    MergeSplitAssets(dir);
                }
            }
            var toReadFile = ProcessingSplitFiles(nonEmptyFiles);
            await LoadAsync(
                toReadFile,
                highPriority,
                allowAutoDependencies: AutoLoadCatalogDependencies && !LazyLoading,
                allowExternalCascadeGuard: !LazyLoading);
        }

        public void LoadFolder(string path)
        {
            LoadFolderAsync(path).GetAwaiter().GetResult();
        }

        public async System.Threading.Tasks.Task LoadFolderAsync(string path)
        {
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = Path.GetFullPath(path);
            }
            MergeSplitAssets(path, true);
            var files = ImportHelper.GetFilesSafe(path, "*.*", true);
            var toReadFile = ProcessingSplitFiles(files);
            await LoadAsync(toReadFile, highPriority: false, allowAutoDependencies: false, allowExternalCascadeGuard: false);
        }

        private async System.Threading.Tasks.Task YieldForUserInteractionIfNeededAsync(bool highPriority = false)
        {
            if (!LazyLoading || highPriority)
            {
                return;
            }

            var shouldYield = ShouldYieldForUserInteraction;
            if (shouldYield == null)
            {
                return;
            }

            while (!ShouldStopLoading && shouldYield())
            {
                await System.Threading.Tasks.Task.Delay(40);
            }
        }

        private async System.Threading.Tasks.Task LoadAsync(
            string[] files,
            bool highPriority,
            bool allowAutoDependencies = false,
            bool allowExternalCascadeGuard = false,
            bool serializedFilesOnly = false,
            bool skipDecompressionCache = false)
        {
            LastSkippedExternalDependencyCount = 0;
            var context = new LoadContext(
                files.Length,
                isolateStreams: false,
                allowExternalCascadeGuard: allowExternalCascadeGuard,
                serializedFilesOnly: serializedFilesOnly,
                skipDecompressionCache: skipDecompressionCache);

            foreach (var file in files)
            {
                context.ImportFiles.Add(file);
                context.ImportFilesHash.TryAdd(Path.GetFileName(file), 0);
                context.Queue.Enqueue(file);
            }

            Progress.Reset();

            int completedCount = 0;
            int configuredThreadCount = LazyLoading
                ? GetConfiguredThreadCountOrDefault("ASSETSTUDIO_LAZY_LOAD_THREADS", DefaultLazyLoadThreadCount)
                : GetConfiguredThreadCount("ASSETSTUDIO_LOAD_THREADS", DefaultLoadThreadRatio);

            await DrainLoadQueueAsync(context, highPriority, configuredThreadCount, () => System.Threading.Interlocked.Increment(ref completedCount));

            if (allowAutoDependencies && AutoLoadCatalogDependencies && !LazyLoading && !ShouldStopLoading)
            {
                await ResolveAndLoadAutoDependenciesAsync(files, context, highPriority, configuredThreadCount, () => System.Threading.Interlocked.Increment(ref completedCount));
            }

            LastSkippedExternalDependencyCount = context.SkippedExternalFiles.Count;
            if (LastSkippedExternalDependencyCount > 0)
            {
                Logger.Info($"Skipped {LastSkippedExternalDependencyCount} external .assets dependency file(s) due to cascade settings.");
            }

            await ReadAssetsAsync(highPriority, context);
            if (!LazyLoading)
            {
                ProcessAssets();
            }
        }

        private async System.Threading.Tasks.Task DrainLoadQueueAsync(
            LoadContext context,
            bool highPriority,
            int configuredThreadCount,
            Func<int> incrementCompletedCount)
        {
            if (context.Queue.IsEmpty)
            {
                return;
            }

            int threadCount = Math.Min(highPriority && LazyLoading ? 1 : configuredThreadCount, Math.Max(1, context.Queue.Count));
            if (threadCount < 1) threadCount = 1;
            var lazyLoadGateForOperation = LazyLoading && !highPriority ? GetLazyLoadGate(configuredThreadCount) : null;

            int activeWorkers = threadCount;
            var tasks = new System.Threading.Tasks.Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = System.Threading.Tasks.Task.Run(async () =>
                {
                    bool isWorkerActive = true;
                    try
                    {
                        while (true)
                        {
                            await YieldForUserInteractionIfNeededAsync(highPriority);

                            if (ShouldStopLoading)
                            {
                                break;
                            }

                            if (context.Queue.TryDequeue(out var file))
                            {
                                ThrowIfMemoryPressureTooHigh("loading files");

                                if (ShouldStopLoading)
                                {
                                    break;
                                }

                                if (!isWorkerActive)
                                {
                                    System.Threading.Interlocked.Increment(ref activeWorkers);
                                    isWorkerActive = true;
                                }

                                try
                                {
                                    if (lazyLoadGateForOperation != null)
                                    {
                                        await lazyLoadGateForOperation.WaitAsync();
                                    }

                                    try
                                    {
                                        LoadFile(file, context);
                                    }
                                    finally
                                    {
                                        lazyLoadGateForOperation?.Release();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Error loading file {file}: {ex.Message}", ex);
                                }
                                var completed = incrementCompletedCount();
                                Progress.Report(completed, System.Threading.Volatile.Read(ref context.TotalCount));
                            }
                            else
                            {
                                var waitTask = context.GetQueueChangedTask();
                                if (isWorkerActive)
                                {
                                    isWorkerActive = false;
                                    if (System.Threading.Interlocked.Decrement(ref activeWorkers) == 0 && context.Queue.IsEmpty)
                                    {
                                        context.SignalQueueChanged();
                                        break;
                                    }
                                }
                                else if (System.Threading.Volatile.Read(ref activeWorkers) == 0 && context.Queue.IsEmpty)
                                {
                                    context.SignalQueueChanged();
                                    break;
                                }

                                if (!context.Queue.IsEmpty || ShouldStopLoading)
                                {
                                    continue;
                                }

                                await waitTask;
                            }
                        }
                    }
                    finally
                    {
                        if (isWorkerActive)
                        {
                            isWorkerActive = false;
                            if (System.Threading.Interlocked.Decrement(ref activeWorkers) == 0)
                            {
                                context.SignalQueueChanged();
                            }
                        }
                    }
                });
            }
            await System.Threading.Tasks.Task.WhenAll(tasks);
        }

        private async System.Threading.Tasks.Task ResolveAndLoadAutoDependenciesAsync(
            string[] initialFiles,
            LoadContext context,
            bool highPriority,
            int configuredThreadCount,
            Func<int> incrementCompletedCount)
        {
            int maxDeps = Math.Max(0, MaxAutoLoadCatalogDependencies);
            if (maxDeps == 0 || initialFiles == null || initialFiles.Length == 0)
            {
                return;
            }

            var existingImports = new HashSet<string>(
                context.ImportFiles.Select(static f =>
                {
                    try { return Path.GetFullPath(f); }
                    catch { return f; }
                }),
                StringComparer.OrdinalIgnoreCase);

            var currentSeedFiles = initialFiles;
            int totalAutoLoaded = 0;
            const int maxWaves = 4;

            for (int wave = 0; wave < maxWaves && totalAutoLoaded < maxDeps && !ShouldStopLoading; wave++)
            {
                int remainingBudget = maxDeps - totalAutoLoaded;
                var loadedFileNames = new HashSet<string>(assetsFileListHash.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var skippedExternal in context.SkippedExternalFiles.Keys)
                {
                    loadedFileNames.Add(skippedExternal);
                }
                var resolvedDeps = AddressablesCatalogDependencyResolver.ResolveDependenciesForLoadedFiles(
                    currentSeedFiles,
                    context.LoadedAssetsFiles.ToArray(),
                    loadedFileNames,
                    remainingBudget,
                    ProjectRoot);

                if (resolvedDeps.Count == 0)
                {
                    break;
                }

                var enqueuedWave = new List<string>(resolvedDeps.Count);
                foreach (var depPath in resolvedDeps)
                {
                    if (totalAutoLoaded + enqueuedWave.Count >= maxDeps)
                    {
                        break;
                    }

                    string fullDepPath;
                    try
                    {
                        fullDepPath = Path.GetFullPath(depPath);
                    }
                    catch
                    {
                        continue;
                    }

                    var depFileName = Path.GetFileName(fullDepPath);
                    if (string.IsNullOrEmpty(depFileName)
                        || context.SkippedExternalFiles.ContainsKey(depFileName)
                        || existingImports.Contains(fullDepPath)
                        || !context.ImportFilesHash.TryAdd(depFileName, 0))
                    {
                        continue;
                    }

                    existingImports.Add(fullDepPath);
                    context.ImportFiles.Add(fullDepPath);
                    context.Queue.Enqueue(fullDepPath);
                    System.Threading.Interlocked.Increment(ref context.TotalCount);
                    enqueuedWave.Add(fullDepPath);
                }

                if (enqueuedWave.Count == 0)
                {
                    break;
                }

                totalAutoLoaded += enqueuedWave.Count;
                await DrainLoadQueueAsync(context, highPriority, configuredThreadCount, incrementCompletedCount);
                currentSeedFiles = enqueuedWave.ToArray();
            }

            if (totalAutoLoaded > 0)
            {
                Logger.Info($"Auto-resolved {totalAutoLoaded} dependency bundle(s) via Addressables catalog/externals.");
            }
        }

        private void LoadFile(string fullName, LoadContext context)
        {
            var reader = new FileReader(fullName);
            LoadFile(reader, context);
        }

        private void LoadFile(FileReader reader, LoadContext context)
        {
            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(reader, context);
                    break;
                case FileType.BundleFile:
                    LoadBundleFile(reader, context);
                    break;
                case FileType.WebFile:
                    LoadWebFile(reader, context);
                    break;
                case FileType.GZipFile:
                    LoadFile(DecompressGZip(reader), context);
                    break;
                case FileType.BrotliFile:
                    LoadFile(DecompressBrotli(reader), context);
                    break;
                case FileType.ZipFile:
                    LoadZipFile(reader, context);
                    break;
                default:
                    reader.Dispose();
                    break;
            }
        }

        private void LoadAssetsFile(FileReader reader, LoadContext context)
        {
            var eventHandle = GetAssetsFileLoadEvent(reader.FileName);
            bool alreadyLoaded = !assetsFileListHash.TryAdd(reader.FileName, 0);

            if (!alreadyLoaded)
            {
                Logger.Info($"Loading {reader.FullPath}");
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    CheckStrippedVersion(assetsFile);
                    
                    lock (loadLock)
                    {
                        assetsFileList.Add(assetsFile);
                    }
                    context.LoadedAssetsFiles.Add(assetsFile);
                    assetsFileListHash.TryAdd(assetsFile.fileName, 0);
                    GetAssetsFileLoadEvent(assetsFile.fileName).Set();

                    var readerDir = Path.GetDirectoryName(reader.FullPath);
                    foreach (var sharedFile in assetsFile.m_Externals)
                    {
                        var sharedFileName = sharedFile.fileName;
                        if (string.IsNullOrEmpty(sharedFileName)
                            || sharedFileName == "."
                            || sharedFileName == ".."
                            || sharedFileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                            || string.IsNullOrEmpty(readerDir))
                        {
                            continue;
                        }

                        bool containsShared = context.ImportFilesHash.ContainsKey(sharedFileName);

                        if (!containsShared)
                        {
                            var sharedFilePath = Path.Combine(readerDir, sharedFileName);
                            if (!IsPathWithinDirectory(sharedFilePath, readerDir))
                            {
                                continue;
                            }

                            if (!AutoLoadExternalDependencies)
                            {
                                if (File.Exists(sharedFilePath))
                                {
                                    context.SkippedExternalFiles.TryAdd(sharedFileName, 0);
                                }
                                continue;
                            }
                            
                            bool containsNoExist = context.NoExistFiles.ContainsKey(sharedFilePath);

                            if (!containsNoExist)
                            {
                                if (!File.Exists(sharedFilePath))
                                {
                                    var findFiles = ImportHelper.GetFilesSafe(readerDir, sharedFileName, true);
                                    if (findFiles.Length > 0 && IsPathWithinDirectory(findFiles[0], readerDir))
                                    {
                                        sharedFilePath = findFiles[0];
                                    }
                                }
                                if (File.Exists(sharedFilePath) && IsPathWithinDirectory(sharedFilePath, readerDir))
                                {
                                    if (TryReserveExternalDependencySlot(context, sharedFileName, assetsFile.m_Externals.Count))
                                    {
                                        context.ImportFiles.Add(sharedFilePath);
                                        context.Queue.Enqueue(sharedFilePath);
                                        System.Threading.Interlocked.Increment(ref context.TotalCount);
                                        context.SignalQueueChanged();
                                    }
                                }
                                else
                                {
                                    context.NoExistFiles.TryAdd(sharedFilePath, 0);
                                }
                            }
                        }
                    }
                    eventHandle.Set();
                }
                catch (Exception e)
                {
                    assetsFileListHash.TryRemove(reader.FileName, out _);
                    eventHandle.Set();
                    Logger.Error($"Error while reading assets file {reader.FullPath}", e);
                    reader.Dispose();
                }
            }
            else
            {
                Logger.Info($"Skipping {reader.FullPath}");
                reader.Dispose();
            }
        }

        private bool TryReserveExternalDependencySlot(LoadContext context, string sharedFileName, int currentFileExternalsCount)
        {
            if (!AutoLoadExternalDependencies)
            {
                context.SkippedExternalFiles.TryAdd(sharedFileName, 0);
                return false;
            }

            lock (context.CascadePromptLock)
            {
                if (context.ImportFilesHash.ContainsKey(sharedFileName))
                {
                    return false;
                }

                if (MaxAutoLoadExternalDependencies > 0 && context.EnqueuedExternalCount >= MaxAutoLoadExternalDependencies)
                {
                    context.SkippedExternalFiles.TryAdd(sharedFileName, 0);
                    return false;
                }

                var callback = ConfirmExternalCascadeCallback;
                if (context.AllowExternalCascadeGuard
                    && callback != null
                    && ExternalCascadePromptThreshold > 0
                    && context.EnqueuedExternalCount >= ExternalCascadePromptThreshold)
                {
                    if (context.CascadeDecision == 0)
                    {
                        bool allow;
                        try
                        {
                            allow = callback(context.EnqueuedExternalCount, currentFileExternalsCount);
                        }
                        catch
                        {
                            allow = false;
                        }

                        context.CascadeDecision = allow ? 1 : -1;
                    }

                    if (context.CascadeDecision < 0)
                    {
                        context.SkippedExternalFiles.TryAdd(sharedFileName, 0);
                        return false;
                    }
                }

                if (!context.ImportFilesHash.TryAdd(sharedFileName, 0))
                {
                    return false;
                }

                context.EnqueuedExternalCount++;
                return true;
            }
        }

        private void LoadAssetsFromMemory(FileReader reader, LoadContext context, string? originalPath, string? unityVersion = null)
        {
            var eventHandle = GetAssetsFileLoadEvent(reader.FileName);
            bool alreadyLoaded = !assetsFileListHash.TryAdd(reader.FileName, 0);

            if (!alreadyLoaded)
            {
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    if (!string.IsNullOrEmpty(originalPath))
                    {
                        assetsFile.originalPath = originalPath;
                    }
                    if (!string.IsNullOrEmpty(unityVersion)
                        && (assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7 || assetsFile.IsVersionStripped))
                    {
                        assetsFile.SetVersion(unityVersion);
                    }
                    CheckStrippedVersion(assetsFile);
                    
                    lock (loadLock)
                    {
                        assetsFileList.Add(assetsFile);
                    }
                    context.LoadedAssetsFiles.Add(assetsFile);
                    assetsFileListHash.TryAdd(assetsFile.fileName, 0);
                    GetAssetsFileLoadEvent(assetsFile.fileName).Set();
                    eventHandle.Set();
                }
                catch (Exception e)
                {
                    assetsFileListHash.TryRemove(reader.FileName, out _);
                    eventHandle.Set();
                    Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                    reader.Dispose();
                }
            }
            else
            {
                Logger.Info($"Skipping {originalPath} ({reader.FileName})");
                reader.Dispose();
            }
        }

        private void LoadBundleFile(FileReader reader, LoadContext context, string? originalPath = null)
        {
            Logger.Info("Loading " + reader.FullPath);
            BundleFile? bundleFile = null;
            int handedOffCount = 0;
            try
            {
                bool skipDecompressionCache = context.SkipDecompressionCache || (LazyLoading && context.IsolateStreams);
                bool serializedFilesOnly = context.SerializedFilesOnly || (LazyLoading && context.IsolateStreams);
                bundleFile = new BundleFile(
                    reader,
                    decompressionCache: skipDecompressionCache ? null : DecompressionCache,
                    allowParallelDecompression: true,
                    lowMemoryThresholdOverride: null,
                    serializedFilesOnly: serializedFilesOnly);
                if (bundleFile.BlocksStream != null)
                {
                    if (context.IsolateStreams)
                    {
                        context.BundleStreams.Add(bundleFile.BlocksStream);
                    }
                    else
                    {
                        bundleStreams.Add(bundleFile.BlocksStream);
                    }
                }
                var baseDir = Path.GetDirectoryName(reader.FullPath);
                for (int i = 0; i < bundleFile.fileList.Length; i++)
                {
                    var file = bundleFile.fileList[i];
                    if (serializedFilesOnly && IsKnownResourceFileName(file.fileName ?? file.path))
                    {
                        file.stream.Dispose();
                        handedOffCount = i + 1;
                        continue;
                    }

                    var dummyPath = BuildSafeContainerPath(baseDir, file.path);
                    FileReader subReader;
                    try
                    {
                        subReader = new FileReader(dummyPath, file.stream);
                    }
                    catch
                    {
                        file.stream.Dispose();
                        handedOffCount = i + 1;
                        throw;
                    }
                    handedOffCount = i + 1;
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(subReader, context, originalPath ?? reader.FullPath, bundleFile.m_Header.unityRevision);
                    }
                    else if (serializedFilesOnly)
                    {
                        subReader.Dispose();
                    }
                    else
                    {
                        RegisterResourceFileReader(subReader, context, file.fileName, file.path, subReader.FullPath); //TODO
                    }
                }

                LoadFollowingBundleSegments(reader, bundleFile, context, originalPath ?? reader.FullPath);
            }
            catch (Exception e)
            {
                if (bundleFile?.fileList != null)
                {
                    for (int i = handedOffCount; i < bundleFile.fileList.Length; i++)
                    {
                        bundleFile.fileList[i]?.stream?.Dispose();
                    }
                }
                var str = $"Error while reading bundle file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        /// <summary>
        /// Loads UnityFS bundles stored back to back in one file (BUNDLE-02). Each following bundle is read
        /// through a window over the same file under a virtual name, so it never shares a decompression cache
        /// entry with the first bundle, while its assets keep the real file path so lazy mode can reopen them.
        /// </summary>
        private void LoadFollowingBundleSegments(FileReader reader, BundleFile firstBundle, LoadContext context, string physicalPath)
        {
            foreach (var segment in BundleFile.FindFollowingSegments(reader.BaseStream, firstBundle))
            {
                Logger.Info($"Found additional bundle {segment.Index} at offset 0x{segment.Offset:X} ({segment.Size} bytes) in {reader.FileName}");
                var segmentReader = BundleFile.OpenSegmentReader(reader, segment);
                if (segmentReader.FileType != FileType.BundleFile)
                {
                    segmentReader.Dispose();
                    return;
                }

                LoadBundleFile(segmentReader, context, physicalPath);
            }
        }

        private void LoadWebFile(FileReader reader, LoadContext context)
        {
            Logger.Info("Loading " + reader.FullPath);
            WebFile? webFile = null;
            int handedOffCount = 0;
            try
            {
                webFile = new WebFile(reader);
                var baseDir = Path.GetDirectoryName(reader.FullPath);
                for (int i = 0; i < webFile.fileList.Length; i++)
                {
                    var file = webFile.fileList[i];
                    var dummyPath = BuildSafeContainerPath(baseDir, file.path);
                    FileReader subReader;
                    try
                    {
                        subReader = new FileReader(dummyPath, file.stream);
                    }
                    catch
                    {
                        file.stream.Dispose();
                        handedOffCount = i + 1;
                        throw;
                    }
                    handedOffCount = i + 1;
                    switch (subReader.FileType)
                    {
                        case FileType.AssetsFile:
                            LoadAssetsFromMemory(subReader, context, reader.FullPath);
                            break;
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, context, reader.FullPath);
                            break;
                        case FileType.WebFile:
                            LoadWebFile(subReader, context);
                            break;
                        case FileType.ResourceFile:
                            RegisterResourceFileReader(subReader, context, file.fileName, file.path, subReader.FullPath); //TODO
                            break;
                        default:
                            subReader.Dispose();
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                if (webFile?.fileList != null)
                {
                    for (int i = handedOffCount; i < webFile.fileList.Length; i++)
                    {
                        webFile.fileList[i]?.stream?.Dispose();
                    }
                }
                Logger.Error($"Error while reading web file {reader.FullPath}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadZipFile(FileReader reader, LoadContext context)
        {
            Logger.Info("Loading " + reader.FileName);
            try
            {
                var readerDir = Path.GetDirectoryName(reader.FullPath) ?? string.Empty;
                var zipEntryBaseDir = Path.Combine(readerDir, reader.FileName);
                using (ZipArchive archive = new ZipArchive(reader.BaseStream, ZipArchiveMode.Read))
                {
                    List<string> splitFiles = new List<string>();
                    // register all files before parsing the assets so that the external references can be found
                    // and find split files
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }
                        if (entry.Name.Contains(".split"))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                            string basePath = Path.Combine(Path.GetDirectoryName(entry.FullName) ?? string.Empty, baseName);
                            if (!splitFiles.Contains(basePath))
                            {
                                splitFiles.Add(basePath);
                                context.ImportFilesHash.TryAdd(baseName, 0);
                            }
                        }
                        else
                        {
                            context.ImportFilesHash.TryAdd(entry.Name, 0);
                        }
                    }

                    // merge split files and load the result
                    foreach (string basePath in splitFiles)
                    {
                        Stream? splitStream = null;
                        try
                        {
                            var safeSplitPath = BuildSafeContainerPath(readerDir, basePath);
                            long totalSize = 0;
                            int i = 0;
                            while (true)
                            {
                                string path = $"{basePath}.split{i++}";
                                ZipArchiveEntry? entry = archive.GetEntry(path);
                                if (entry == null)
                                    break;
                                totalSize += entry.Length;
                                if (totalSize > ImportHelper.MaxDecompressedSize)
                                {
                                    throw new InvalidDataException($"Zip split file '{basePath}' exceeds safety limit of {ImportHelper.MaxDecompressedSize} bytes.");
                                }
                            }

                            if (BundleFile.ShouldUseTemporaryStream(totalSize))
                            {
                                splitStream = BundleFile.CreateTemporaryStream(safeSplitPath, "split");
                            }
                            else
                            {
                                var initialCapacity = (int)Math.Min(Math.Max(totalSize, 0L), 16L * 1024 * 1024);
                                splitStream = new MemoryStream(initialCapacity);
                            }

                            long totalWritten = 0;
                            var copyBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                            try
                            {
                                i = 0;
                                while (true)
                                {
                                    string path = $"{basePath}.split{i++}";
                                    ZipArchiveEntry? entry = archive.GetEntry(path);
                                    if (entry == null)
                                        break;
                                    using (Stream entryStream = entry.Open())
                                    {
                                        int bytesRead;
                                        while ((bytesRead = entryStream.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
                                        {
                                            totalWritten += bytesRead;
                                            if (totalWritten > ImportHelper.MaxDecompressedSize)
                                            {
                                                throw new InvalidDataException($"Zip split file '{basePath}' exceeds safety limit of {ImportHelper.MaxDecompressedSize} bytes.");
                                            }
                                            if (splitStream is MemoryStream && BundleFile.ShouldUseTemporaryStream(totalWritten))
                                            {
                                                var tempStream = BundleFile.CreateTemporaryStream(safeSplitPath, "split");
                                                try
                                                {
                                                    splitStream.Position = 0;
                                                    splitStream.CopyTo(tempStream);
                                                }
                                                catch
                                                {
                                                    tempStream.Dispose();
                                                    throw;
                                                }
                                                splitStream.Dispose();
                                                splitStream = tempStream;
                                            }
                                            splitStream.Write(copyBuffer, 0, bytesRead);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                System.Buffers.ArrayPool<byte>.Shared.Return(copyBuffer);
                            }
                            splitStream.Seek(0, SeekOrigin.Begin);
                            FileReader entryReader = new FileReader(safeSplitPath, splitStream);
                            splitStream = null; // ownership transferred to entryReader
                            LoadFile(entryReader, context);
                        }
                        catch (Exception e)
                        {
                            splitStream?.Dispose();
                            Logger.Error($"Error while reading zip split file {basePath}", e);
                        }
                    }

                    // Load the entries (BUNDLE-08). Android packages keep everything Unity reads under an "assets/"
                    // folder, so classes.dex, lib/*.so, res/ and META-INF/ are skipped instead of being decompressed
                    // and held as resources. Entries stored without compression (how Android builds keep bundles,
                    // serialized files and .resS) are read in place as a window over the package, with no copy.
                    bool androidUnityPackage = IsAndroidUnityPackage(archive);
                    var packagePath = (reader.BaseStream as FileStream)?.Name;
                    var storedEntries = packagePath != null ? ZipStoredEntryIndex.TryCreate(reader.BaseStream) : null;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }
                        if (androidUnityPackage && !IsInAndroidAssetsFolder(entry.FullName))
                        {
                            continue;
                        }
                        Stream? streamReader = null;
                        try
                        {
                            string dummyPath = BuildSafeContainerPath(zipEntryBaseDir, entry.FullName);

                            if (storedEntries != null
                                && storedEntries.TryGetStoredData(entry.FullName, out var dataOffset, out var dataSize))
                            {
                                streamReader = new SubStream(packagePath!, dataOffset, dataSize);
                            }
                            else
                            {
                                using (Stream entryStream = entry.Open())
                                {
                                    streamReader = ImportHelper.CopyDecompressedWithLimits(entryStream, dummyPath, "zipentry", Math.Max(0L, entry.Length));
                                }
                            }

                            FileReader entryReader = new FileReader(dummyPath, streamReader);
                            streamReader = null; // ownership transferred to entryReader
                            if (entryReader.FileType == FileType.ResourceFile)
                            {
                                entryReader.Position = 0;
                                RegisterResourceFileReader(entryReader, context, entry.Name, entry.FullName, entryReader.FullPath);
                            }
                            else
                            {
                                LoadFile(entryReader, context);
                            }
                        }
                        catch (Exception e)
                        {
                            streamReader?.Dispose();
                            Logger.Error($"Error while reading zip entry {entry.FullName}", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading zip file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static bool IsAndroidUnityPackage(ZipArchive archive)
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.StartsWith("assets/bin/Data/", StringComparison.Ordinal)
                    || name.Contains("/assets/bin/Data/", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInAndroidAssetsFolder(string entryFullName)
        {
            return entryFullName.StartsWith("assets/", StringComparison.Ordinal)
                || entryFullName.Contains("/assets/", StringComparison.Ordinal);
        }

        private string GetSpecifiedUnityVersion()
        {
            return string.IsNullOrWhiteSpace(SpecifyUnityVersion) ? "" : SpecifyUnityVersion.Trim();
        }

        private static readonly Regex UnityVersionWithSuffixRegex = new Regex(
            @"^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?<suffix>[abcfpt]\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex UnityVersionNumericRegex = new Regex(
            @"^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?:\.\d+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] IgnoredFallbackExecutablePrefixes =
        {
            "UnityCrashHandler",
            "crashpad_handler",
            "unins",
            "KernelDumpAnalyzer",
            "ZFGameBrowser",
            "createdump"
        };

        private string GetUnityVersionFallback(string sourcePath)
        {
            lock (unityVersionInferenceLock)
            {
                if (!string.IsNullOrEmpty(inferredUnityVersion))
                {
                    return inferredUnityVersion;
                }

                foreach (var dataDirectory in EnumerateUnityDataDirectoryCandidates(sourcePath))
                {
                    var version = TryReadUnityVersionFromDataDirectory(dataDirectory);
                    if (string.IsNullOrEmpty(version))
                    {
                        continue;
                    }

                    inferredUnityVersion = version;
                    Logger.Info($"Inferred Unity version {version} from {dataDirectory}");
                    return inferredUnityVersion;
                }

                // Fallback when no globalgamemanagers/data.unity3d is present: check ancestor
                // game directories directly for UnityPlayer.dll or paired *_Data executables.
                foreach (var directory in EnumerateUnityVersionSearchDirectories(sourcePath))
                {
                    var version = TryReadUnityVersionFromUnityPlayerDirectory(directory);
                    if (string.IsNullOrEmpty(version))
                    {
                        continue;
                    }

                    inferredUnityVersion = version;
                    Logger.Info($"Inferred Unity version {version} from {directory}");
                    return inferredUnityVersion;
                }
            }

            return "";
        }

        private IEnumerable<string> EnumerateUnityVersionSearchDirectories(string sourcePath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in EnumerateUnityVersionSearchRoots(sourcePath))
            {
                var directory = NormalizeUnityVersionSearchRoot(root);
                for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
                {
                    if (seen.Add(directory))
                    {
                        yield return directory;
                    }

                    try
                    {
                        directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private IEnumerable<string> EnumerateUnityDataDirectoryCandidates(string sourcePath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in EnumerateUnityVersionSearchRoots(sourcePath))
            {
                var directory = NormalizeUnityVersionSearchRoot(root);
                for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
                {
                    if (TryAddUnityDataDirectoryCandidate(directory, seen, out var candidate))
                    {
                        yield return candidate;
                    }

                    foreach (var child in EnumerateDirectoriesSafe(directory, "*_Data"))
                    {
                        if (TryAddUnityDataDirectoryCandidate(child, seen, out candidate))
                        {
                            yield return candidate;
                        }
                    }

                    var plainDataChild = Path.Combine(directory, "Data");
                    if (Directory.Exists(plainDataChild) && TryAddUnityDataDirectoryCandidate(plainDataChild, seen, out candidate))
                    {
                        yield return candidate;
                    }

                    // Bounded depth-2 fallback for nested build subfolders (<Root>/<Sub>/<Game>_Data or Data)
                    if (depth == 0)
                    {
                        int scannedSubdirs = 0;
                        foreach (var subDir in EnumerateDirectoriesSafe(directory, "*"))
                        {
                            if (++scannedSubdirs > 64)
                            {
                                break;
                            }

                            foreach (var nestedData in EnumerateDirectoriesSafe(subDir, "*_Data"))
                            {
                                if (TryAddUnityDataDirectoryCandidate(nestedData, seen, out candidate))
                                {
                                    yield return candidate;
                                }
                            }

                            var nestedPlainData = Path.Combine(subDir, "Data");
                            if (Directory.Exists(nestedPlainData) && TryAddUnityDataDirectoryCandidate(nestedPlainData, seen, out candidate))
                            {
                                yield return candidate;
                            }
                        }
                    }

                    try
                    {
                        directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private IEnumerable<string> EnumerateUnityVersionSearchRoots(string sourcePath)
        {
            if (!string.IsNullOrWhiteSpace(ProjectRoot))
            {
                yield return ProjectRoot;
            }
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                yield return sourcePath;
            }
        }

        private static string NormalizeUnityVersionSearchRoot(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                return "";
            }

            if (Directory.Exists(path))
            {
                return path;
            }

            var parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ? "" : parent;
        }

        private static IEnumerable<string> EnumerateDirectoriesSafe(string directory, string searchPattern)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                yield break;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(directory, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                yield break;
            }

            foreach (var child in directories)
            {
                yield return child;
            }
        }

        private static bool TryAddUnityDataDirectoryCandidate(string directory, HashSet<string> seen, out string candidate)
        {
            candidate = "";
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            try
            {
                directory = Path.GetFullPath(directory);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(Path.Combine(directory, "globalgamemanagers"))
                && !File.Exists(Path.Combine(directory, "data.unity3d")))
            {
                return false;
            }

            if (!seen.Add(directory))
            {
                return false;
            }

            candidate = directory;
            return true;
        }

        private string TryReadUnityVersionFromDataDirectory(string dataDirectory)
        {
            var version = TryReadUnityVersionFromSerializedFile(Path.Combine(dataDirectory, "globalgamemanagers"));
            if (!string.IsNullOrEmpty(version))
            {
                return version;
            }

            version = TryReadUnityVersionFromBundleFile(Path.Combine(dataDirectory, "data.unity3d"));
            if (!string.IsNullOrEmpty(version))
            {
                return version;
            }

            return TryReadUnityVersionFromUnityPlayerFallback(dataDirectory);
        }

        private static string TryReadUnityVersionFromUnityPlayerFallback(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory))
            {
                return "";
            }

            string? parentDir = null;
            try
            {
                parentDir = Directory.GetParent(dataDirectory)?.FullName;
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(parentDir))
            {
                var version = TryReadUnityVersionFromFileVersionInfo(Path.Combine(parentDir, "UnityPlayer.dll"));
                if (!string.IsNullOrEmpty(version))
                {
                    return version;
                }
            }

            var dataDirDllVersion = TryReadUnityVersionFromFileVersionInfo(Path.Combine(dataDirectory, "UnityPlayer.dll"));
            if (!string.IsNullOrEmpty(dataDirDllVersion))
            {
                return dataDirDllVersion;
            }

            if (!string.IsNullOrEmpty(parentDir))
            {
                var folderName = Path.GetFileName(dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(folderName) && folderName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && folderName.Length > 5)
                {
                    var matchingExe = Path.Combine(parentDir, folderName.Substring(0, folderName.Length - 5) + ".exe");
                    var exeVersion = TryReadUnityVersionFromFileVersionInfo(matchingExe);
                    if (!string.IsNullOrEmpty(exeVersion))
                    {
                        return exeVersion;
                    }
                }

                try
                {
                    foreach (var exePath in Directory.EnumerateFiles(parentDir, "*.exe", SearchOption.TopDirectoryOnly)
                        .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        if (IsIgnoredFallbackExecutable(Path.GetFileName(exePath)))
                        {
                            continue;
                        }

                        var exeVersion = TryReadUnityVersionFromFileVersionInfo(exePath);
                        if (!string.IsNullOrEmpty(exeVersion))
                        {
                            return exeVersion;
                        }
                    }
                }
                catch
                {
                }
            }

            return "";
        }

        private static string TryReadUnityVersionFromUnityPlayerDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return "";
            }

            var directDllVersion = TryReadUnityVersionFromFileVersionInfo(Path.Combine(directory, "UnityPlayer.dll"));
            if (!string.IsNullOrEmpty(directDllVersion))
            {
                return directDllVersion;
            }

            var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(folderName)
                && (folderName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(folderName, "Data", StringComparison.OrdinalIgnoreCase)))
            {
                var fallback = TryReadUnityVersionFromUnityPlayerFallback(directory);
                if (!string.IsNullOrEmpty(fallback))
                {
                    return fallback;
                }
            }

            foreach (var childDataDir in EnumerateDirectoriesSafe(directory, "*_Data"))
            {
                var fallback = TryReadUnityVersionFromUnityPlayerFallback(childDataDir);
                if (!string.IsNullOrEmpty(fallback))
                {
                    return fallback;
                }
            }

            var plainDataDir = Path.Combine(directory, "Data");
            if (Directory.Exists(plainDataDir))
            {
                var fallback = TryReadUnityVersionFromUnityPlayerFallback(plainDataDir);
                if (!string.IsNullOrEmpty(fallback))
                {
                    return fallback;
                }
            }

            // Bounded depth-2 fallback for nested game builds (<Root>/<Sub>/UnityPlayer.dll)
            int scanned = 0;
            foreach (var subDir in EnumerateDirectoriesSafe(directory, "*"))
            {
                if (++scanned > 64)
                {
                    break;
                }

                var nestedDllVersion = TryReadUnityVersionFromFileVersionInfo(Path.Combine(subDir, "UnityPlayer.dll"));
                if (!string.IsNullOrEmpty(nestedDllVersion))
                {
                    return nestedDllVersion;
                }
            }

            return "";
        }

        private static bool IsIgnoredFallbackExecutable(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return true;
            }

            foreach (var prefix in IgnoredFallbackExecutablePrefixes)
            {
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static string TryReadUnityVersionFromFileVersionInfo(string binaryPath)
        {
            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
            {
                return "";
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(binaryPath);
                var fromNative = TryNormalizeUnityVersionFromFileVersionInfo(
                    info.ProductVersion,
                    info.FileVersion,
                    info.FileMajorPart,
                    info.FileMinorPart,
                    info.FileBuildPart);
                if (!string.IsNullOrEmpty(fromNative))
                {
                    return fromNative;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not infer Unity version via FileVersionInfo from {binaryPath}: {ex.Message}");
            }

            // Cross-platform fallback: on Linux/macOS FileVersionInfo does not parse native Win32 PE
            // VS_VERSION_INFO resources from UnityPlayer.dll / game .exe binaries.
            return TryReadUnityVersionFromPeResource(binaryPath);
        }

        public static string TryReadUnityVersionFromPeResource(string binaryPath)
        {
            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
            {
                return "";
            }

            try
            {
                using var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 0x80)
                {
                    return "";
                }

                Span<byte> dosHeader = stackalloc byte[0x40];
                if (!ReadExact(fs, dosHeader) || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
                {
                    return "";
                }

                uint peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader.Slice(0x3C, 4));
                if (peOffset < 0x40 || peOffset > fs.Length - 24)
                {
                    return "";
                }

                fs.Position = peOffset;
                Span<byte> coffHeader = stackalloc byte[24];
                if (!ReadExact(fs, coffHeader)
                    || coffHeader[0] != (byte)'P'
                    || coffHeader[1] != (byte)'E'
                    || coffHeader[2] != 0
                    || coffHeader[3] != 0)
                {
                    return "";
                }

                ushort numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.Slice(6, 2));
                ushort sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.Slice(20, 2));
                if (numberOfSections == 0 || numberOfSections > 128 || sizeOfOptionalHeader < 0x60 || sizeOfOptionalHeader > 4096)
                {
                    return "";
                }

                var optHeader = new byte[sizeOfOptionalHeader];
                if (!ReadExact(fs, optHeader))
                {
                    return "";
                }

                ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optHeader.AsSpan(0, 2));
                int numRvaOffset;
                int dataDirsOffset;
                if (magic == 0x010B) // PE32
                {
                    numRvaOffset = 0x5C;
                    dataDirsOffset = 0x60;
                }
                else if (magic == 0x020B) // PE32+
                {
                    numRvaOffset = 0x6C;
                    dataDirsOffset = 0x70;
                }
                else
                {
                    return "";
                }

                int resourceDirOffset = dataDirsOffset + 2 * 8;
                if (sizeOfOptionalHeader < resourceDirOffset + 8)
                {
                    return "";
                }

                uint numRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(optHeader.AsSpan(numRvaOffset, 4));
                if (numRvaAndSizes < 3)
                {
                    return "";
                }

                uint rsrcRva = BinaryPrimitives.ReadUInt32LittleEndian(optHeader.AsSpan(resourceDirOffset, 4));
                uint rsrcSize = BinaryPrimitives.ReadUInt32LittleEndian(optHeader.AsSpan(resourceDirOffset + 4, 4));
                if (rsrcRva == 0 || rsrcSize < 16)
                {
                    return "";
                }

                int sectionTableBytes = numberOfSections * 40;
                if (fs.Position + sectionTableBytes > fs.Length)
                {
                    return "";
                }

                var sectionTable = new byte[sectionTableBytes];
                if (!ReadExact(fs, sectionTable))
                {
                    return "";
                }

                var sections = new (uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawOffset)[numberOfSections];
                for (int i = 0; i < numberOfSections; i++)
                {
                    var s = sectionTable.AsSpan(i * 40, 40);
                    sections[i] = (
                        VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12, 4)),
                        VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8, 4)),
                        RawSize: BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16, 4)),
                        RawOffset: BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20, 4)));
                }

                if (!TryMapPeRvaToFileOffset(sections, fs.Length, rsrcRva, out long rsrcFileBase, out _))
                {
                    return "";
                }

                // Walk 3-level Win32 resource directory: RT_VERSION (16) -> first ID -> first Language -> DATA_ENTRY
                if (!TryFindPeResourceEntry(fs, rsrcFileBase, 0, targetId: 16u, expectSubdirectory: true, out uint level2Offset))
                {
                    return "";
                }

                if (!TryFindPeResourceEntry(fs, rsrcFileBase, level2Offset, targetId: null, expectSubdirectory: true, out uint level3Offset))
                {
                    return "";
                }

                if (!TryFindPeResourceEntry(fs, rsrcFileBase, level3Offset, targetId: null, expectSubdirectory: false, out uint dataEntryOffset))
                {
                    return "";
                }

                long dataEntryFileOffset = rsrcFileBase + dataEntryOffset;
                if (dataEntryFileOffset < 0 || dataEntryFileOffset + 16 > fs.Length)
                {
                    return "";
                }

                fs.Position = dataEntryFileOffset;
                Span<byte> dataEntry = stackalloc byte[16];
                if (!ReadExact(fs, dataEntry))
                {
                    return "";
                }

                uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.Slice(0, 4));
                uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.Slice(4, 4));
                if (dataRva == 0 || dataSize < 32 || dataSize > 256 * 1024)
                {
                    return "";
                }

                if (!TryMapPeRvaToFileOffset(sections, fs.Length, dataRva, out long blobFileOffset, out long blobAvailable)
                    || dataSize > blobAvailable)
                {
                    return "";
                }

                fs.Position = blobFileOffset;
                var versionBlob = new byte[dataSize];
                if (!ReadExact(fs, versionBlob))
                {
                    return "";
                }

                int fileMajor = -1;
                int fileMinor = -1;
                int fileBuild = -1;
                int fixedSearchLimit = Math.Min(versionBlob.Length - 52, 256);
                for (int i = 0; i <= fixedSearchLimit; i += 4)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(versionBlob.AsSpan(i, 4)) == 0xFEEF04BDu)
                    {
                        uint msVer = BinaryPrimitives.ReadUInt32LittleEndian(versionBlob.AsSpan(i + 8, 4));
                        uint lsVer = BinaryPrimitives.ReadUInt32LittleEndian(versionBlob.AsSpan(i + 12, 4));
                        fileMajor = (int)(msVer >> 16);
                        fileMinor = (int)(msVer & 0xFFFF);
                        fileBuild = (int)(lsVer >> 16);
                        break;
                    }
                }

                var productVersion = TryExtractVsVersionStringValue(versionBlob, "ProductVersion");
                var fileVersion = TryExtractVsVersionStringValue(versionBlob, "FileVersion");

                return TryNormalizeUnityVersionFromFileVersionInfo(productVersion, fileVersion, fileMajor, fileMinor, fileBuild);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not parse PE version resource from {binaryPath}: {ex.Message}");
                return "";
            }
        }

        private static bool ReadExact(Stream stream, Span<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer.Slice(totalRead));
                if (read <= 0)
                {
                    return false;
                }
                totalRead += read;
            }
            return true;
        }

        private static bool TryMapPeRvaToFileOffset(
            (uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawOffset)[] sections,
            long fileLength,
            uint rva,
            out long fileOffset,
            out long availableBytes)
        {
            for (int i = 0; i < sections.Length; i++)
            {
                var s = sections[i];
                uint span = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.VirtualAddress && (ulong)rva < (ulong)s.VirtualAddress + span)
                {
                    long delta = rva - s.VirtualAddress;
                    if (delta >= s.RawSize)
                    {
                        continue;
                    }

                    long offset = (long)s.RawOffset + delta;
                    long maxEnd = Math.Min(fileLength, (long)s.RawOffset + s.RawSize);
                    if (offset >= 0 && offset < maxEnd)
                    {
                        fileOffset = offset;
                        availableBytes = maxEnd - offset;
                        return true;
                    }
                }
            }

            fileOffset = 0;
            availableBytes = 0;
            return false;
        }

        private static bool TryFindPeResourceEntry(
            Stream fs,
            long rsrcFileBase,
            uint dirRelativeOffset,
            uint? targetId,
            bool expectSubdirectory,
            out uint childOffset)
        {
            childOffset = 0;
            long dirOffset = rsrcFileBase + dirRelativeOffset;
            if (dirOffset < 0 || dirOffset + 16 > fs.Length)
            {
                return false;
            }

            fs.Position = dirOffset;
            Span<byte> dirHeader = stackalloc byte[16];
            if (!ReadExact(fs, dirHeader))
            {
                return false;
            }

            ushort numNamed = BinaryPrimitives.ReadUInt16LittleEndian(dirHeader.Slice(12, 2));
            ushort numId = BinaryPrimitives.ReadUInt16LittleEndian(dirHeader.Slice(14, 2));
            int totalEntries = numNamed + numId;
            if (totalEntries == 0 || totalEntries > 4096)
            {
                return false;
            }

            int startIndex = targetId.HasValue ? numNamed : 0;
            long entriesStart = dirOffset + 16 + (long)startIndex * 8;
            int countToScan = totalEntries - startIndex;
            if (countToScan <= 0 || entriesStart + (long)countToScan * 8 > fs.Length)
            {
                return false;
            }

            fs.Position = entriesStart;
            Span<byte> entry = stackalloc byte[8];
            for (int i = 0; i < countToScan; i++)
            {
                if (!ReadExact(fs, entry))
                {
                    return false;
                }

                uint nameOrId = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0, 4));
                uint offsetToData = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                bool isSubdir = (offsetToData & 0x80000000u) != 0;
                if (isSubdir != expectSubdirectory)
                {
                    continue;
                }

                if (targetId.HasValue && nameOrId != targetId.Value)
                {
                    continue;
                }

                childOffset = offsetToData & 0x7FFFFFFFu;
                return true;
            }

            return false;
        }

        private static string? TryExtractVsVersionStringValue(ReadOnlySpan<byte> blob, string key)
        {
            var keyBytes = Encoding.Unicode.GetBytes(key + "\0");
            int searchStart = 6;
            while (searchStart <= blob.Length - keyBytes.Length)
            {
                int rel = blob.Slice(searchStart).IndexOf(keyBytes);
                if (rel < 0)
                {
                    return null;
                }

                int keyPos = searchStart + rel;
                int headerPos = keyPos - 6;
                if (headerPos >= 0 && (headerPos & 3) == 0)
                {
                    ushort wLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(headerPos, 2));
                    ushort wValueLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(headerPos + 2, 2));
                    ushort wType = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(headerPos + 4, 2));
                    int nodeEnd = Math.Min(blob.Length, headerPos + wLength);
                    if (wType == 1 && wLength >= 6 + keyBytes.Length && wValueLength > 0)
                    {
                        int valueStart = (keyPos + keyBytes.Length + 3) & ~3;
                        if (valueStart < nodeEnd)
                        {
                            int maxValueBytes = Math.Min(nodeEnd - valueStart, wValueLength * 2);
                            if (maxValueBytes >= 2)
                            {
                                var valSpan = blob.Slice(valueStart, maxValueBytes & ~1);
                                var decoded = Encoding.Unicode.GetString(valSpan);
                                int nulIdx = decoded.IndexOf('\0');
                                if (nulIdx >= 0)
                                {
                                    decoded = decoded.Substring(0, nulIdx);
                                }
                                decoded = decoded.Trim();
                                if (!string.IsNullOrEmpty(decoded))
                                {
                                    return decoded;
                                }
                            }
                        }
                    }
                }

                searchStart = keyPos + 2;
            }

            return null;
        }

        public static string TryNormalizeUnityVersionFromFileVersionInfo(
            string? productVersion,
            string? fileVersion,
            int fileMajorPart,
            int fileMinorPart,
            int fileBuildPart)
        {
            var normalized = TryNormalizeUnityVersionToken(productVersion);
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }

            normalized = TryNormalizeUnityVersionToken(fileVersion);
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }

            if (IsPlausibleUnityMajorVersion(fileMajorPart) && fileMinorPart >= 0 && fileBuildPart >= 0)
            {
                return $"{fileMajorPart}.{fileMinorPart}.{fileBuildPart}f1";
            }

            return "";
        }

        private static string TryNormalizeUnityVersionToken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "";
            }

            var trimmed = raw.Trim();
            var splitIdx = trimmed.IndexOfAny(new[] { ' ', '\t', '(', '_' });
            var token = (splitIdx > 0 ? trimmed.Substring(0, splitIdx) : trimmed).Trim();
            if (string.IsNullOrEmpty(token))
            {
                return "";
            }

            var suffixMatch = UnityVersionWithSuffixRegex.Match(token);
            if (suffixMatch.Success
                && int.TryParse(suffixMatch.Groups["major"].Value, out var suffixedMajor)
                && IsPlausibleUnityMajorVersion(suffixedMajor))
            {
                return $"{suffixedMajor}.{suffixMatch.Groups["minor"].Value}.{suffixMatch.Groups["build"].Value}{suffixMatch.Groups["suffix"].Value.ToLowerInvariant()}";
            }

            var numericMatch = UnityVersionNumericRegex.Match(token);
            if (numericMatch.Success
                && int.TryParse(numericMatch.Groups["major"].Value, out var numericMajor)
                && IsPlausibleUnityMajorVersion(numericMajor))
            {
                return $"{numericMajor}.{numericMatch.Groups["minor"].Value}.{numericMatch.Groups["build"].Value}f1";
            }

            return "";
        }

        private static bool IsPlausibleUnityMajorVersion(int major)
        {
            return (major >= 4 && major <= 5)
                || (major >= 2017 && major <= 2030)
                || (major >= 6000 && major <= 9999);
        }

        private string TryReadUnityVersionFromSerializedFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "";
            }

            try
            {
                using (var reader = new FileReader(path))
                {
                    if (reader.FileType != FileType.AssetsFile)
                    {
                        return "";
                    }

                    var assetsFile = new SerializedFile(reader, this);
                    if (!assetsFile.IsVersionStripped && IsConcreteUnityVersion(assetsFile.unityVersion))
                    {
                        return assetsFile.unityVersion.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not infer Unity version from {path}: {ex.Message}");
            }

            return "";
        }

        private string TryReadUnityVersionFromBundleFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "";
            }

            try
            {
                using (var reader = new FileReader(path))
                {
                    if (reader.FileType != FileType.BundleFile)
                    {
                        return "";
                    }

                    reader.Position = 0;
                    _ = reader.ReadStringToNull(); // signature
                    _ = reader.ReadUInt32();       // version
                    var unityVersion = reader.ReadStringToNull();
                    var unityRevision = reader.ReadStringToNull();
                    if (IsConcreteUnityVersion(unityRevision))
                    {
                        return unityRevision.Trim();
                    }
                    if (IsConcreteUnityVersion(unityVersion))
                    {
                        return unityVersion.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not infer Unity version from {path}: {ex.Message}");
            }

            return "";
        }

        private static bool IsConcreteUnityVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            var trimmed = version.Trim();
            if (trimmed.StartsWith("0.0.0", StringComparison.Ordinal)
                || trimmed.Contains('x', StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.IsNullOrEmpty(TryNormalizeUnityVersionToken(trimmed));
        }

        public void CheckStrippedVersion(SerializedFile assetsFile)
        {
            var specifiedUnityVersion = GetSpecifiedUnityVersion();
            if (!string.IsNullOrEmpty(specifiedUnityVersion))
            {
                assetsFile.SetVersion(specifiedUnityVersion);
                return;
            }

            if (assetsFile.IsVersionStripped)
            {
                var sourcePath = string.IsNullOrEmpty(assetsFile.originalPath) ? assetsFile.fullName : assetsFile.originalPath;
                var fallbackUnityVersion = GetUnityVersionFallback(sourcePath);
                if (string.IsNullOrEmpty(fallbackUnityVersion))
                {
                    throw new Exception("The Unity version has been stripped and could not be inferred from globalgamemanagers, data.unity3d, or UnityPlayer.dll; please set the version in the options");
                }
                assetsFile.SetVersion(fallbackUnityVersion);
            }
        }

        public void Clear()
        {
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            LazyLoading = false;
            ProjectRoot = null;
            LruCache.Clear();
            ProjectIndex.Clear();
            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.ObjectsDic.Clear();
                assetsFile.reader?.Close();
            }
            assetsFileList.Clear();
            assetsFileListHash.Clear();
            foreach (var eventHandle in assetsFileLoadEvents.Values)
            {
                eventHandle.Dispose();
            }
            assetsFileLoadEvents.Clear();

            DisposeResourceReaders();

            while (bundleStreams.TryTake(out var stream))
            {
                stream.Dispose();
            }

            assetsFileIndexCache.Clear();
            inferredUnityVersion = "";
            LastSkippedExternalDependencyCount = 0;
            DecryptionHelper.ClearSessionCaches();
            AssetHandle.ClearSourceHashCache();
        }

        public void Dispose()
        {
            Clear();
        }

        public void ClearLoadedFilesKeepIndex()
        {
            var keptAnyFile = false;
            lock (loadLock)
            {
                var filesToRemove = new List<SerializedFile>();
                foreach (var assetsFile in assetsFileList)
                {
                    if (ShouldKeepFileCallback != null && ShouldKeepFileCallback(assetsFile.fileName))
                    {
                        keptAnyFile = true;
                        continue;
                    }

                    ProjectIndex.Arena.DetachSourceFile(assetsFile);

                    assetsFile.Objects.Clear();
                    assetsFile.ObjectsDic.Clear();
                    if (assetsFile.reader != null)
                    {
                        assetsFile.reader.Close();
                    }
                    filesToRemove.Add(assetsFile);
                }

                foreach (var file in filesToRemove)
                {
                    assetsFileList.Remove(file);
                    assetsFileListHash.TryRemove(file.fileName, out _);
                }

                foreach (var eventHandle in assetsFileLoadEvents.Values)
                {
                    eventHandle.Dispose();
                }
                assetsFileLoadEvents.Clear();

                DisposeResourceReaders();

                while (bundleStreams.TryTake(out var stream))
                {
                    stream.Dispose();
                }

                assetsFileIndexCache.Clear();
            }

            if (!keptAnyFile)
            {
                LruCache.Clear();
            }
            else
            {
                LruCache.RemoveUnloaded();
            }
        }

        public async System.Threading.Tasks.Task IndexSingleFileInWorkerAsync(string filePath, bool highPriority = false)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            if (!LazyLoading)
            {
                await LoadFilesAsync(highPriority, filePath);
                return;
            }

            if (string.IsNullOrEmpty(ProjectRoot))
            {
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                    if (!string.IsNullOrEmpty(dir))
                    {
                        lock (loadLock)
                        {
                            if (string.IsNullOrEmpty(ProjectRoot))
                            {
                                ProjectRoot = dir;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore path normalization failures on invalid synthetic paths.
                }
            }

            var toReadFiles = ProcessingSplitFiles(new[] { filePath });
            if (toReadFiles.Length == 0)
            {
                return;
            }

            var context = new LoadContext(toReadFiles.Length, isolateStreams: true);
            foreach (var file in toReadFiles)
            {
                context.ImportFiles.Add(file);
                context.ImportFilesHash.TryAdd(Path.GetFileName(file), 0);
                context.Queue.Enqueue(file);
            }

            try
            {
                while (context.Queue.TryDequeue(out var nextFile))
                {
                    await YieldForUserInteractionIfNeededAsync(highPriority);
                    if (ShouldStopLoading)
                    {
                        break;
                    }

                    ThrowIfMemoryPressureTooHigh("loading files");
                    if (ShouldStopLoading)
                    {
                        break;
                    }

                    try
                    {
                        LoadFile(nextFile, context);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error loading file {nextFile}: {ex.Message}", ex);
                    }
                }

                var unprocessedFiles = ClaimUnprocessedFiles(context.LoadedAssetsFiles.ToArray());
                foreach (var assetsFile in unprocessedFiles)
                {
                    await IndexSingleSerializedFileLazyAsync(assetsFile, highPriority);
                }
            }
            finally
            {
                ReleaseIsolatedContextKeepIndex(context);
            }
        }

        /// <summary>
        /// Optional zero-I/O callback invoked inside <see cref="IndexSingleSerializedFileLazyAsync"/>
        /// using the <see cref="SerializedFile"/>, <see cref="AssetBundle"/>, and <see cref="BuildSettings"/>
        /// already parsed in memory during lazy indexing.
        /// </summary>
        public Action<SerializedFile, AssetBundle?, BuildSettings?, bool>? OnLazySerializedFileIndexed { get; set; }

        private async System.Threading.Tasks.Task IndexSingleSerializedFileLazyAsync(SerializedFile assetsFile, bool highPriority)
        {
            await YieldForUserInteractionIfNeededAsync(highPriority);

            bool keepFile = ShouldKeepFileCallback != null && ShouldKeepFileCallback(assetsFile.fileName);
            if (ProjectIndex.Arena.TryBindCachedSourceFile(assetsFile, attachSourceFile: keepFile, IsSameAssetSource))
            {
                return;
            }

            List<(PPtr<Object> PPtr, string Container)>? localContainers = null;
            AssetBundle? parsedAssetBundle = null;
            BuildSettings? parsedBuildSettings = null;
            bool hasGameObjects = false;
            var onLazyIndexed = OnLazySerializedFileIndexed;
            var fileOrigin = AssetHandleOrigin.Intern(assetsFile);
            using var localReader = assetsFile.reader.Clone();

            for (int objectIndex = 0; objectIndex < assetsFile.m_Objects.Count; objectIndex++)
            {
                var objectInfo = assetsFile.m_Objects[objectIndex];
                if ((objectIndex & 0x3f) == 0)
                {
                    await YieldForUserInteractionIfNeededAsync(highPriority);
                }

                if (ShouldStopLoading)
                {
                    break;
                }

                if ((objectIndex & 0xff) == 0)
                {
                    ThrowIfMemoryPressureTooHigh("reading assets (lazy)");
                }

                if (ShouldStopLoading)
                {
                    break;
                }

                var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                var type = ClassIDTypeHelper.FromClassId(objectInfo.classID);
                if (type == ClassIDType.GameObject || type == ClassIDType.SceneAsset)
                {
                    hasGameObjects = true;
                }

                string? name = null;
                if (type == ClassIDType.AssetBundle || type == ClassIDType.ResourceManager)
                {
                    try
                    {
                        var obj = CreateObjectFromReader(objectReader);
                        if (obj is AssetBundle bundle)
                        {
                            parsedAssetBundle = bundle;
                            name = bundle.m_Name;
                            localContainers ??= new List<(PPtr<Object>, string)>();
                            CollectSafeAssetBundleContainersForLazyIndex(bundle, localContainers);
                        }
                        else if (obj is ResourceManager rm)
                        {
                            name = "ResourceManager";
                            if (rm.m_Container != null && rm.m_Container.Length > 0)
                            {
                                localContainers ??= new List<(PPtr<Object>, string)>(rm.m_Container.Length);
                                foreach (var entry in rm.m_Container)
                                {
                                    if (entry.Value != null && !entry.Value.IsNull && !string.IsNullOrEmpty(entry.Key))
                                    {
                                        localContainers.Add((entry.Value, entry.Key));
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Fallback if container loading fails
                    }
                }
                else if (type == ClassIDType.BuildSettings && onLazyIndexed != null)
                {
                    try
                    {
                        parsedBuildSettings = new BuildSettings(objectReader);
                    }
                    catch
                    {
                        // Ignore malformed BuildSettings during lazy indexing
                    }
                }
                else
                {
                    name = AssetHandle.TryReadObjectName(objectReader);
                }

                AddLazyIndexedRecord(fileOrigin, objectInfo, type, name, sourceFile: keepFile ? assetsFile : null);
            }

            if (localContainers != null && localContainers.Count > 0)
            {
                ApplyLazyContainersToProjectIndex(assetsFile, fileOrigin, localContainers);
            }

            if (onLazyIndexed != null && !ShouldStopLoading)
            {
                try
                {
                    onLazyIndexed(assetsFile, parsedAssetBundle, parsedBuildSettings, hasGameObjects);
                }
                catch
                {
                    // Optional callback must never break lazy indexing.
                }
            }
        }

        private void AddLazyIndexedRecord(
            AssetHandleOrigin fileOrigin,
            ObjectInfo objectInfo,
            ClassIDType type,
            string? name,
            SerializedFile? sourceFile)
        {
            if (!string.IsNullOrEmpty(name))
            {
                ProjectIndex.Arena.AddCanonicalRecord(
                    fileOrigin,
                    objectInfo.m_PathID,
                    type,
                    objectInfo.byteStart,
                    objectInfo.byteSize,
                    name.AsSpan(),
                    sourceFile);
                return;
            }

            Span<char> unnamedBuf = stackalloc char[128];
            if (unnamedBuf.TryWrite($"Unnamed_{type}_{objectInfo.m_PathID}", out int charsWritten))
            {
                ProjectIndex.Arena.AddCanonicalRecord(
                    fileOrigin,
                    objectInfo.m_PathID,
                    type,
                    objectInfo.byteStart,
                    objectInfo.byteSize,
                    unnamedBuf.Slice(0, charsWritten),
                    sourceFile);
            }
            else
            {
                string fallback = $"Unnamed_{type}_{objectInfo.m_PathID}";
                ProjectIndex.Arena.AddCanonicalRecord(
                    fileOrigin,
                    objectInfo.m_PathID,
                    type,
                    objectInfo.byteStart,
                    objectInfo.byteSize,
                    fallback.AsSpan(),
                    sourceFile);
            }
        }

        public static void CollectSafeAssetBundleContainersForLazyIndex(
            AssetBundle? assetBundle,
            List<(PPtr<Object> PPtr, string Container)> containers)
        {
            if (assetBundle?.m_Container == null)
            {
                return;
            }

            var preloadTable = assetBundle.m_PreloadTable;
            foreach (var entry in assetBundle.m_Container)
            {
                var assetInfo = entry.Value;
                if (assetInfo == null || string.IsNullOrEmpty(entry.Key))
                {
                    continue;
                }

                var directAsset = assetInfo.asset;
                bool addedDirectAsset = false;

                if (preloadTable != null && preloadTable.Length > 0)
                {
                    var preloadIndex = assetInfo.preloadIndex;
                    var preloadSize = assetInfo.preloadSize;
                    if (preloadIndex >= 0 && preloadSize > 0 && preloadIndex < preloadTable.Length)
                    {
                        long preloadEnd = Math.Min((long)preloadIndex + preloadSize, preloadTable.Length);
                        for (int k = preloadIndex; k < preloadEnd; k++)
                        {
                            var preloadEntry = preloadTable[k];
                            if (preloadEntry == null || preloadEntry.IsNull)
                            {
                                continue;
                            }

                            containers.Add((preloadEntry, entry.Key));
                            if (directAsset != null
                                && !directAsset.IsNull
                                && preloadEntry.m_FileID == directAsset.m_FileID
                                && preloadEntry.m_PathID == directAsset.m_PathID)
                            {
                                addedDirectAsset = true;
                            }
                        }
                    }
                }

                if (!addedDirectAsset && directAsset != null && !directAsset.IsNull)
                {
                    containers.Add((directAsset, entry.Key));
                }
            }
        }

        private void ApplyLazyContainersToProjectIndex(
            SerializedFile assetsFile,
            AssetHandleOrigin fileOrigin,
            List<(PPtr<Object> PPtr, string Container)> containers)
        {
            var arena = ProjectIndex.Arena;
            for (int i = 0; i < containers.Count; i++)
            {
                var (pptr, container) = containers[i];
                if (pptr == null || pptr.IsNull || string.IsNullOrEmpty(container))
                {
                    continue;
                }

                int targetIdx = -1;
                if (pptr.m_FileID == 0)
                {
                    ProjectIndex.TryGetIndex(new AssetHandleKey(fileOrigin.KeyOriginId, pptr.m_PathID), out targetIdx);
                }
                else
                {
                    if (pptr.TryGetAssetsFile(out var targetFile) && targetFile != null)
                    {
                        var targetKey = AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID);
                        ProjectIndex.TryGetIndexByUniqueId(targetKey, out targetIdx);
                    }

                    if (targetIdx < 0
                        && assetsFile.m_Externals != null
                        && pptr.m_FileID > 0
                        && pptr.m_FileID - 1 < assetsFile.m_Externals.Count)
                    {
                        var external = assetsFile.m_Externals[pptr.m_FileID - 1];
                        if (external != null)
                        {
                            if (!string.IsNullOrEmpty(external.fileName))
                            {
                                arena.TryGetIndexByFileNameAndPathId(external.fileName, pptr.m_PathID, out targetIdx);
                            }

                            if (targetIdx < 0 && !string.IsNullOrEmpty(external.pathName))
                            {
                                arena.TryGetIndexByFileNameAndPathId(Path.GetFileName(external.pathName), pptr.m_PathID, out targetIdx);
                            }
                        }
                    }
                }

                if (targetIdx >= 0)
                {
                    arena.ApplyContainerAndMaterialNameFallback(targetIdx, container);
                }
            }
        }

        private void ReleaseIsolatedContextKeepIndex(LoadContext context)
        {
            var keptAnyFile = false;
            var readersToClose = new List<FileReader>();

            lock (loadLock)
            {
                foreach (var assetsFile in context.LoadedAssetsFiles)
                {
                    if (assetsFile == null)
                    {
                        continue;
                    }

                    if (ShouldKeepFileCallback != null && ShouldKeepFileCallback(assetsFile.fileName))
                    {
                        keptAnyFile = true;
                        continue;
                    }

                    ProjectIndex.Arena.DetachSourceFile(assetsFile);

                    assetsFile.Objects.Clear();
                    assetsFile.ObjectsDic.Clear();
                    if (assetsFile.reader != null)
                    {
                        readersToClose.Add(assetsFile.reader);
                    }

                    assetsFileList.Remove(assetsFile);
                    assetsFileListHash.TryRemove(assetsFile.fileName, out _);
                    if (assetsFileLoadEvents.TryRemove(assetsFile.fileName, out var eventHandle))
                    {
                        eventHandle.Dispose();
                    }
                }
            }

            foreach (var reader in readersToClose)
            {
                try
                {
                    reader.Close();
                }
                catch
                {
                    // Ignore reader close failure
                }
            }

            while (context.IsolatedResourceReaders.TryTake(out var resourceReader))
            {
                try
                {
                    resourceReader?.Close();
                }
                catch
                {
                    // Ignore reader close failure
                }
            }

            while (context.BundleStreams.TryTake(out var stream))
            {
                if (keptAnyFile)
                {
                    bundleStreams.Add(stream);
                }
                else
                {
                    try
                    {
                        stream?.Dispose();
                    }
                    catch
                    {
                        // Ignore stream dispose failure
                    }
                }
            }
        }

        public static Object CreateObjectFromReader(ObjectReader objectReader)
        {
            switch (objectReader.type)
            {
                case ClassIDType.Animation:
                    return new Animation(objectReader);
                case ClassIDType.AnimationClip:
                    return new AnimationClip(objectReader);
                case ClassIDType.Animator:
                    return new Animator(objectReader);
                case ClassIDType.AnimatorController:
                    return new AnimatorController(objectReader);
                case ClassIDType.AnimatorOverrideController:
                    return new AnimatorOverrideController(objectReader);
                case ClassIDType.AssetBundle:
                    return new AssetBundle(objectReader);
                case ClassIDType.AudioClip:
                    return new AudioClip(objectReader);
                case ClassIDType.Avatar:
                    return new Avatar(objectReader);
                case ClassIDType.BuildSettings:
                    return new BuildSettings(objectReader);
                case ClassIDType.Camera:
                    return new Camera(objectReader);
                case ClassIDType.Font:
                    return new Font(objectReader);
                case ClassIDType.GameObject:
                    return new GameObject(objectReader);
                case ClassIDType.Light:
                    return new Light(objectReader);
                case ClassIDType.LODGroup:
                    return new LODGroup(objectReader);
                case ClassIDType.Material:
                    return new Material(objectReader);
                case ClassIDType.Mesh:
                    return new Mesh(objectReader);
                case ClassIDType.MeshFilter:
                    return new MeshFilter(objectReader);
                case ClassIDType.MeshRenderer:
                    return new MeshRenderer(objectReader);
                case ClassIDType.MonoBehaviour:
                    return new MonoBehaviour(objectReader);
                case ClassIDType.MonoScript:
                    return new MonoScript(objectReader);
                case ClassIDType.MovieTexture:
                    return new MovieTexture(objectReader);
                case ClassIDType.PlayerSettings:
                    return new PlayerSettings(objectReader);
                case ClassIDType.RectTransform:
                    return new RectTransform(objectReader);
                case ClassIDType.Shader:
                    return new Shader(objectReader);
                case ClassIDType.SkinnedMeshRenderer:
                    return new SkinnedMeshRenderer(objectReader);
                case ClassIDType.Sprite:
                    return new Sprite(objectReader);
                case ClassIDType.SpriteAtlas:
                    return new SpriteAtlas(objectReader);
                case ClassIDType.TextAsset:
                    return new TextAsset(objectReader);
                case ClassIDType.Texture2D:
                    return new Texture2D(objectReader);
                case ClassIDType.Texture2DArray:
                    return new Texture2DArray(objectReader);
                case ClassIDType.Cubemap when objectReader.version is [5, 3, ..] or [2019, 3, ..]:
                    return new Cubemap(objectReader);
                case ClassIDType.Transform:
                    return new Transform(objectReader);
                case ClassIDType.VideoClip:
                    return new VideoClip(objectReader);
                case ClassIDType.VideoPlayer:
                    return new VideoPlayer(objectReader);
                case ClassIDType.ResourceManager:
                    return new ResourceManager(objectReader);
                default:
                    return new Object(objectReader);
            }
        }

        private static bool IsKnownResourceFileName(string? name)
        {
            return !string.IsNullOrEmpty(name)
                && (name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
        }

        public bool TryFindIndexedAssetByFileNameAndPathId(
            string? fileName,
            long pathId,
            out int arenaIndex,
            out ClassIDType type,
            out string uniqueId,
            out SourceOriginRecord origin)
        {
            var arena = ProjectIndex?.Arena;
            if (arena != null && arena.TryGetIndexByFileNameAndPathId(fileName, pathId, out arenaIndex))
            {
                type = arena.GetType(arenaIndex);
                uniqueId = arena.GetUniqueId(arenaIndex);
                origin = arena.GetOrigin(arenaIndex);
                return true;
            }

            arenaIndex = -1;
            type = ClassIDType.UnknownType;
            uniqueId = string.Empty;
            origin = SourceOriginRecord.Empty;
            return false;
        }

        public Object? ResolveHandle(AssetHandle? handle)
        {
            if (handle == null) return null;
            if (handle.RealObject != null)
            {
                if (LazyLoading)
                {
                    LruCache.RecordAccess(handle);
                }
                return handle.RealObject;
            }

            lock (handle)
            {
                if (handle.RealObject != null)
                {
                    if (LazyLoading)
                    {
                        LruCache.RecordAccess(handle);
                    }
                    return handle.RealObject;
                }

                try
                {
                    var assetsFile = handle.SourceFile;
                    if (assetsFile?.reader == null)
                    {
                        return null;
                    }

                    using (var localReader = assetsFile.reader.Clone())
                    {
                        assetsFile.ObjectInfoDic.TryGetValue(handle.PathID, out var existingObjectInfo);
                        var objectInfo = new ObjectInfo
                        {
                            m_PathID = handle.PathID,
                            byteStart = handle.ByteStart,
                            byteSize = (uint)handle.ByteSize,
                            classID = (int)handle.Type,
                            serializedType = existingObjectInfo?.serializedType
                        };

                        var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                        var obj = CreateObjectFromReader(objectReader);

                        if (assetsFile.TryGetObject(obj.m_PathID, out var existing))
                        {
                            handle.RealObject = existing;
                            if (LazyLoading)
                            {
                                LruCache.RecordAccess(handle);
                            }
                            return existing;
                        }

                        handle.RealObject = obj;
                        assetsFile.AddObject(obj);

                        if (LazyLoading)
                        {
                            LruCache.RecordAccess(handle);
                        }
                        return obj;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error materializing object {handle.TypeString} PathID={handle.PathID}", ex);
                    return null;
                }
            }
        }

        private List<SerializedFile> ClaimUnprocessedFiles(IEnumerable<SerializedFile> files)
        {
            var result = new List<SerializedFile>();
            lock (loadLock)
            {
                foreach (var assetsFile in files)
                {
                    if (assetsFile == null || assetsFile.IsProcessed)
                    {
                        continue;
                    }

                    assetsFile.IsProcessed = true;
                    result.Add(assetsFile);
                }
            }

            return result;
        }

        private async System.Threading.Tasks.Task ReadAssetsAsync(bool highPriority, LoadContext context)
        {
            Logger.Info("Read assets...");

            var filesForRead = context.LoadedAssetsFiles.ToArray();
            var progressCount = filesForRead.Sum(x => x.m_Objects.Count);
            int progressValue = 0;
            int errorCount = 0;
            Progress.Reset();

            if (LazyLoading)
            {
                List<SerializedFile> unprocessedFiles;
                unprocessedFiles = ClaimUnprocessedFiles(filesForRead);
                var configuredReadThreads = GetConfiguredThreadCountOrDefault("ASSETSTUDIO_LAZY_READ_THREADS", DefaultLazyReadThreadCount);
                var maxDegreeOfParallelism = highPriority ? 1 : configuredReadThreads;
                var lazyReadGateForOperation = highPriority ? null : GetLazyReadGate(configuredReadThreads);
                var deferredExternalContainers = new ConcurrentBag<(SerializedFile File, AssetHandleOrigin Origin, List<(PPtr<Object> PPtr, string Container)> Containers)>();
                using (var semaphore = new System.Threading.SemaphoreSlim(maxDegreeOfParallelism))
                {
                    var tasks = unprocessedFiles.Select(async assetsFile =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            if (lazyReadGateForOperation != null)
                            {
                                await lazyReadGateForOperation.WaitAsync();
                            }

                            try
                            {
                            await YieldForUserInteractionIfNeededAsync(highPriority);

                            // Lookup cached handles using O(1) dictionary index
                            if (ProjectIndex.Arena.TryBindCachedSourceFile(assetsFile, attachSourceFile: true, IsSameAssetSource))
                            {
                                return;
                            }
                            else
                            {
                                List<(PPtr<Object> PPtr, string Container)>? localContainers = null;
                                var fileOrigin = AssetHandleOrigin.Intern(assetsFile);
                                using var localReader = assetsFile.reader.Clone();

                                for (int objectIndex = 0; objectIndex < assetsFile.m_Objects.Count; objectIndex++)
                                {
                                    var objectInfo = assetsFile.m_Objects[objectIndex];
                                    if ((System.Threading.Volatile.Read(ref progressValue) & 0x3f) == 0)
                                    {
                                        await YieldForUserInteractionIfNeededAsync(highPriority);
                                    }

                                    if (ShouldStopLoading)
                                    {
                                        break;
                                    }

                                    if ((System.Threading.Volatile.Read(ref progressValue) & 0xff) == 0)
                                    {
                                        ThrowIfMemoryPressureTooHigh("reading assets (lazy)");
                                    }

                                    if (ShouldStopLoading)
                                    {
                                        break;
                                    }

                                    var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                                    
                                    var type = ClassIDTypeHelper.FromClassId(objectInfo.classID);

                                    string? name = null;
                                    if (type == ClassIDType.AssetBundle || type == ClassIDType.ResourceManager)
                                    {
                                        try
                                        {
                                            var obj = CreateObjectFromReader(objectReader);
                                            if (obj is AssetBundle bundle)
                                            {
                                                name = bundle.m_Name;
                                                localContainers ??= new List<(PPtr<Object>, string)>();
                                                CollectSafeAssetBundleContainersForLazyIndex(bundle, localContainers);
                                            }
                                            else if (obj is ResourceManager rm)
                                            {
                                                name = "ResourceManager";
                                                if (rm.m_Container != null && rm.m_Container.Length > 0)
                                                {
                                                    localContainers ??= new List<(PPtr<Object>, string)>(rm.m_Container.Length);
                                                    foreach (var entry in rm.m_Container)
                                                    {
                                                        if (entry.Value != null && !entry.Value.IsNull && !string.IsNullOrEmpty(entry.Key))
                                                        {
                                                            localContainers.Add((entry.Value, entry.Key));
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            // Fallback if container loading fails
                                        }
                                    }
                                    else
                                    {
                                        name = AssetHandle.TryReadObjectName(objectReader);
                                    }

                                    AddLazyIndexedRecord(fileOrigin, objectInfo, type, name, sourceFile: assetsFile);

                                    var currentProgress = System.Threading.Interlocked.Increment(ref progressValue);
                                    Progress.Report(currentProgress, progressCount);
                                }

                                if (localContainers != null && localContainers.Count > 0)
                                {
                                    ApplyLazyContainersToProjectIndex(assetsFile, fileOrigin, localContainers);
                                    for (int c = 0; c < localContainers.Count; c++)
                                    {
                                        if (localContainers[c].PPtr != null && localContainers[c].PPtr.m_FileID != 0)
                                        {
                                            deferredExternalContainers.Add((assetsFile, fileOrigin, localContainers));
                                            break;
                                        }
                                    }
                                }
                            }
                            }
                            finally
                            {
                                lazyReadGateForOperation?.Release();
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    await System.Threading.Tasks.Task.WhenAll(tasks);
                }

                while (deferredExternalContainers.TryTake(out var deferred))
                {
                    ApplyLazyContainersToProjectIndex(deferred.File, deferred.Origin, deferred.Containers);
                }
                return;
            }

            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = GetConfiguredThreadCount("ASSETSTUDIO_READ_THREADS", DefaultReadThreadRatio)
            };

            foreach (var assetsFile in ClaimUnprocessedFiles(filesForRead))
            {
                var localObjects = new Object?[assetsFile.m_Objects.Count];

                System.Threading.Tasks.Parallel.For(
                    0,
                    assetsFile.m_Objects.Count,
                    parallelOptions,
                    () => assetsFile.reader.Clone(),
                    (objectIndex, state, localReader) =>
                    {
                        var objectInfo = assetsFile.m_Objects[objectIndex];
                        if (ShouldStopLoading)
                        {
                            state.Stop();
                            return localReader;
                        }

                        if ((System.Threading.Volatile.Read(ref progressValue) & 0xff) == 0)
                        {
                            ThrowIfMemoryPressureTooHigh("reading assets");
                        }

                        if (ShouldStopLoading)
                        {
                            state.Stop();
                            return localReader;
                        }

                        var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                        try
                        {
                            Object obj = CreateObjectFromReader(objectReader);
                            localObjects[objectIndex] = obj;
                        }
                        catch (Exception e)
                        {
                            var errCount = System.Threading.Interlocked.Increment(ref errorCount);
                            if (errCount <= 100)
                            {
                                var sb = new StringBuilder();
                                sb.AppendLine("Unable to load object")
                                    .AppendLine($"Assets {assetsFile.fileName}")
                                    .AppendLine($"Path {assetsFile.originalPath}")
                                    .AppendLine($"Type {objectReader.type}")
                                    .AppendLine($"PathID {objectInfo.m_PathID}");

                                if (objectInfo.serializedType?.m_Type?.m_Nodes != null)
                                {
                                    sb.AppendLine("TypeTree Dump:");
                                    foreach (var node in objectInfo.serializedType.m_Type.m_Nodes)
                                    {
                                        sb.AppendLine($"[{node.m_Level}] {node.m_Type} {node.m_Name}");
                                    }
                                }

                                if (errCount <= 20)
                                {
                                    // TODO: REMOVE LATER - Hex dump for Unity 6 debugging only
                                    try
                                    {
                                        objectReader.Reset(); // go back to start
                                        int bytesToRead = Math.Min((int)objectInfo.byteSize, 1024);
                                        var rawBytes = objectReader.ReadBytes(bytesToRead);
                                        var hexDump = BitConverter.ToString(rawBytes).Replace("-", " ");
                                        sb.AppendLine($"Raw Object Bytes (first {bytesToRead} bytes):");
                                        sb.AppendLine(hexDump);
                                    }
                                    catch (Exception ex2)
                                    {
                                        sb.AppendLine($"Failed to dump bytes: {ex2.Message}");
                                    }
                                    // END TODO: REMOVE LATER
                                }

                                sb.Append(e);
                                Logger.Error(sb.ToString());
                            }
                            else if (errCount == 101)
                            {
                                Logger.Error("Too many errors encountered. Further error details are suppressed to maintain stability.");
                            }
                        }

                        var currentProgress = System.Threading.Interlocked.Increment(ref progressValue);
                        Progress.Report(currentProgress, progressCount);

                        return localReader;
                    },
                    localReader => localReader.Dispose()
                );

                foreach (var obj in localObjects)
                {
                    if (obj != null)
                    {
                        assetsFile.AddObject(obj);
                    }
                }
            }
        }

        public static int GetConfiguredThreadCount(string environmentVariable, double defaultRatio)
        {
            var envValue = string.IsNullOrEmpty(environmentVariable)
                ? string.Empty
                : (Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty);
            var cacheKey = string.Concat(
                environmentVariable,
                "\u001f",
                defaultRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "\u001f",
                envValue);
            return ConfiguredThreadCountCache.GetOrAdd(
                cacheKey,
                _ => ComputeConfiguredThreadCount(environmentVariable, defaultRatio, Environment.ProcessorCount, envValue));
        }

        public static int ComputeConfiguredThreadCount(
            string environmentVariable,
            double defaultRatio,
            int? processorCountOverride = null,
            string? environmentValueOverride = null)
        {
            var value = environmentValueOverride ?? (string.IsNullOrEmpty(environmentVariable)
                ? null
                : Environment.GetEnvironmentVariable(environmentVariable));
            if (int.TryParse(value, out var configuredValue) && configuredValue > 0)
            {
                return configuredValue;
            }

            var processorCount = processorCountOverride ?? Environment.ProcessorCount;
            if (processorCount <= 1 || defaultRatio <= 0)
            {
                return 1;
            }

            var scaled = (int)Math.Floor(processorCount * defaultRatio);
            var minThreads = processorCount >= 2 ? 2 : 1;
            return Math.Min(MaxDefaultThreadCount, Math.Max(minThreads, scaled));
        }

        private System.Threading.SemaphoreSlim GetLazyLoadGate(int limit)
        {
            return GetLazyGate(ref lazyLoadGate, ref lazyLoadGateLimit, limit);
        }

        private System.Threading.SemaphoreSlim GetLazyReadGate(int limit)
        {
            return GetLazyGate(ref lazyReadGate, ref lazyReadGateLimit, limit);
        }

        private System.Threading.SemaphoreSlim GetLazyGate(ref System.Threading.SemaphoreSlim gate, ref int currentLimit, int requestedLimit)
        {
            requestedLimit = Math.Max(1, requestedLimit);
            lock (concurrencyGateLock)
            {
                if (currentLimit != requestedLimit)
                {
                    gate = new System.Threading.SemaphoreSlim(requestedLimit);
                    currentLimit = requestedLimit;
                }

                return gate;
            }
        }

        private static int GetConfiguredThreadCountOrDefault(string environmentVariable, int defaultValue)
        {
            var envValue = string.IsNullOrEmpty(environmentVariable)
                ? string.Empty
                : (Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty);
            var cacheKey = string.Concat(
                environmentVariable,
                "\u001f",
                defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "\u001f",
                envValue);
            return ConfiguredThreadCountOrDefaultCache.GetOrAdd(
                cacheKey,
                _ => ComputeConfiguredThreadCountOrDefault(environmentVariable, defaultValue, envValue));
        }

        private static int ComputeConfiguredThreadCountOrDefault(string environmentVariable, int defaultValue, string? environmentValueOverride = null)
        {
            var value = environmentValueOverride ?? (string.IsNullOrEmpty(environmentVariable)
                ? null
                : Environment.GetEnvironmentVariable(environmentVariable));
            if (int.TryParse(value, out var configuredValue) && configuredValue > 0)
            {
                return configuredValue;
            }

            return Math.Max(1, defaultValue);
        }

        private static int GetConfiguredMemoryLimitPercent()
        {
            var value = Environment.GetEnvironmentVariable("ASSETSTUDIO_MEMORY_LIMIT_PERCENT");
            if (int.TryParse(value, out var configuredValue) && configuredValue > 0)
            {
                return Math.Min(configuredValue, 100);
            }

            return DefaultMemoryLimitPercent;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private static bool TryGetLinuxAvailableMemory(out ulong availableBytes)
        {
            availableBytes = 0;
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    ulong memAvailable = 0;
                    ulong swapFree = 0;
                    foreach (var line in File.ReadLines("/proc/meminfo"))
                    {
                        if (line.StartsWith("MemAvailable:"))
                        {
                            memAvailable = ParseMeminfoValue(line);
                        }
                        else if (line.StartsWith("SwapFree:"))
                        {
                            swapFree = ParseMeminfoValue(line);
                        }
                    }
                    availableBytes = memAvailable + swapFree;
                    return true;
                }
            }
            catch
            {
                // Ignore and fallback
            }
            return false;
        }

        private static ulong ParseMeminfoValue(string line)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && ulong.TryParse(parts[1], out var kb))
            {
                return kb * 1024;
            }
            return 0;
        }

        public static void ThrowIfMemoryPressureTooHigh(string operation)
        {
            if (DisableMemoryPressureCheck)
            {
                return;
            }

            var limitPercent = GetConfiguredMemoryLimitPercent();
            if (limitPercent >= 100)
            {
                return;
            }

            if (!TryGetMemoryLoadPercent(out var memoryLoadPercent))
            {
                return;
            }

            if (memoryLoadPercent < limitPercent)
            {
                return;
            }

            var currentTime = Environment.TickCount64;
            var prevTime = System.Threading.Interlocked.Read(ref lastGCCollectTime);
            if (currentTime - prevTime >= 5000 &&
                System.Threading.Interlocked.CompareExchange(ref lastGCCollectTime, currentTime, prevTime) == prevTime)
            {
                EvictLruCachesAcrossAllManagers(150);
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }

            if (TryGetMemoryLoadPercent(out memoryLoadPercent) && memoryLoadPercent >= limitPercent)
            {
                bool isRealPressure = true;
                const ulong minAvailableCommitBytes = 1536UL * 1024UL * 1024UL; // 1.5 GB

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus))
                    {
                        if (memStatus.ullAvailPageFile >= minAvailableCommitBytes)
                        {
                            isRealPressure = false;
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (TryGetLinuxAvailableMemory(out var availableBytes))
                    {
                        if (availableBytes >= minAvailableCommitBytes)
                        {
                            isRealPressure = false;
                        }
                    }
                }

                if (isRealPressure)
                {
                    if (MemoryPressureCallback != null)
                    {
                        var result = MemoryPressureCallback(operation, memoryLoadPercent, limitPercent);
                        if (result == MemoryPressureResult.Continue)
                        {
                            DisableMemoryPressureCheck = true;
                            return;
                        }
                        else if (result == MemoryPressureResult.StopAndKeep)
                        {
                            ShouldStopLoading = true;
                            DisableMemoryPressureCheck = true;
                            return;
                        }
                    }
                    throw new MemoryPressureException(operation, memoryLoadPercent, limitPercent);
                }
            }
        }

        private static bool TryGetMemoryLoadPercent(out int memoryLoadPercent)
        {
            memoryLoadPercent = 0;
            var memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.HighMemoryLoadThresholdBytes <= 0 || memoryInfo.MemoryLoadBytes <= 0)
            {
                return false;
            }

            memoryLoadPercent = (int)Math.Ceiling(memoryInfo.MemoryLoadBytes * 100d / memoryInfo.HighMemoryLoadThresholdBytes);
            return true;
        }

        private void ProcessAssets()
        {
            Logger.Info("Process Assets...");

            var processOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = GetConfiguredThreadCount("ASSETSTUDIO_PROCESS_THREADS", DefaultReadThreadRatio)
            };

            var spriteAtlasEntries = new List<KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>>?[assetsFileList.Count];

            System.Threading.Tasks.Parallel.For(0, assetsFileList.Count, processOptions, fileIndex =>
            {
                var assetsFile = assetsFileList[fileIndex];
                List<KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>>? entries = null;
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is SpriteAtlas m_SpriteAtlas && m_SpriteAtlas.m_RenderDataMap != null)
                    {
                        entries ??= new List<KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>>(m_SpriteAtlas.m_RenderDataMap.Count);
                        foreach (var key in m_SpriteAtlas.m_RenderDataMap.Keys)
                        {
                            entries.Add(new KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>(key, m_SpriteAtlas));
                        }
                    }
                }

                spriteAtlasEntries[fileIndex] = entries;
            });

            var spriteAtlasCache = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();
            foreach (var entries in spriteAtlasEntries)
            {
                if (entries == null)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    spriteAtlasCache[entry.Key] = entry.Value;
                }
            }

            var gameObjects = assetsFileList
                .SelectMany(assetsFile => assetsFile.Objects)
                .OfType<GameObject>()
                .ToArray();

            System.Threading.Tasks.Parallel.ForEach(gameObjects, processOptions, m_GameObject =>
            {
                foreach (var pptr in m_GameObject.m_Components)
                {
                    if (pptr.TryGet(out var m_Component))
                    {
                        switch (m_Component)
                        {
                            case Transform m_Transform:
                                m_GameObject.m_Transform = m_Transform;
                                break;
                            case MeshRenderer m_MeshRenderer:
                                m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                break;
                            case MeshFilter m_MeshFilter:
                                m_GameObject.m_MeshFilter = m_MeshFilter;
                                break;
                            case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                break;
                            case Animator m_Animator:
                                m_GameObject.m_Animator = m_Animator;
                                break;
                            case Animation m_Animation:
                                m_GameObject.m_Animation = m_Animation;
                                break;
                        }
                    }
                }
            });

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is SpriteAtlas m_SpriteAtlas)
                    {
                        foreach (var m_PackedSprite in m_SpriteAtlas.m_PackedSprites)
                        {
                            if (m_PackedSprite.TryGet(out var m_Sprite))
                            {
                                if (m_Sprite.m_SpriteAtlas == null)
                                {
                                    m_Sprite.m_SpriteAtlas = new PPtr<SpriteAtlas>(0, 0, m_Sprite.assetsFile);
                                    m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                }
                                else if (m_Sprite.m_SpriteAtlas.IsNull)
                                {
                                    m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                }
                                else
                                {
                                    m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlaOld);
                                    if (m_SpriteAtlaOld != null && m_SpriteAtlaOld.m_IsVariant)
                                    {
                                        m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                    }
                                }
                            }
                        }
                    }
                    else if (obj is Sprite m_Sprite)
                    {
                        if ((m_Sprite.m_SpriteAtlas == null || m_Sprite.m_SpriteAtlas.IsNull) && m_Sprite.m_RenderDataKey.Key != Guid.Empty)
                        {
                            if (spriteAtlasCache.TryGetValue(m_Sprite.m_RenderDataKey, out var atlas))
                            {
                                m_Sprite.m_SpriteAtlas ??= new PPtr<SpriteAtlas>(0, 0, m_Sprite.assetsFile);
                                m_Sprite.m_SpriteAtlas.Set(atlas);
                            }
                        }
                    }
                }
            }
        }

        public class AssetLruCache
        {
            private readonly int maxCapacity;
            private readonly LinkedList<AssetHandle> list = new LinkedList<AssetHandle>();
            private readonly Dictionary<string, LinkedListNode<AssetHandle>> map = new Dictionary<string, LinkedListNode<AssetHandle>>(StringComparer.Ordinal);
            private readonly object cacheLock = new object();
            private int evictionSuspendCount;

            public AssetLruCache(int capacity)
            {
                maxCapacity = capacity;
            }

            public int Count
            {
                get
                {
                    lock (cacheLock)
                    {
                        return list.Count;
                    }
                }
            }

            public void RecordAccess(AssetHandle handle)
            {
                if (handle == null || string.IsNullOrEmpty(handle.UniqueID)) return;

                lock (cacheLock)
                {
                    if (map.TryGetValue(handle.UniqueID, out var node))
                    {
                        list.Remove(node);
                        list.AddFirst(node);
                    }
                    else
                    {
                        if (evictionSuspendCount == 0 && list.Count >= maxCapacity)
                        {
                            EvictLeastRecentlyUsed();
                        }

                        var newNode = new LinkedListNode<AssetHandle>(handle);
                        list.AddFirst(newNode);
                        map[handle.UniqueID] = newNode;
                    }
                }
            }

            public IDisposable SuspendEviction()
            {
                lock (cacheLock)
                {
                    evictionSuspendCount++;
                }

                return new EvictionSuspension(this);
            }

            private void ResumeEviction()
            {
                lock (cacheLock)
                {
                    if (evictionSuspendCount > 0)
                    {
                        evictionSuspendCount--;
                    }

                    while (evictionSuspendCount == 0 && list.Count > maxCapacity)
                    {
                        EvictLeastRecentlyUsed();
                    }
                }
            }

            public void EvictLeastRecentlyUsed()
            {
                lock (cacheLock)
                {
                    if (list.Count == 0) return;
                    var lastNode = list.Last;
                    if (lastNode != null)
                    {
                        var handle = lastNode.Value;
                        EvictHandle(handle);
                        list.RemoveLast();
                        map.Remove(handle.UniqueID);
                    }
                }
            }

            public void EvictCount(int count)
            {
                lock (cacheLock)
                {
                    for (int i = 0; i < count && list.Count > 0; i++)
                    {
                        var lastNode = list.Last;
                        if (lastNode != null)
                        {
                            var handle = lastNode.Value;
                            EvictHandle(handle);
                            list.RemoveLast();
                            map.Remove(handle.UniqueID);
                        }
                    }
                }
            }

            public void Clear()
            {
                lock (cacheLock)
                {
                    list.Clear();
                    map.Clear();
                }
            }

            public void RemoveUnloaded()
            {
                lock (cacheLock)
                {
                    var node = list.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        var handle = node.Value;
                        if (handle.RealObject == null || handle.SourceFile == null)
                        {
                            list.Remove(node);
                            map.Remove(handle.UniqueID);
                        }
                        node = next;
                    }
                }
            }

            private void EvictHandle(AssetHandle handle)
            {
                lock (handle)
                {
                    var obj = handle.RealObject;
                    handle.RealObject = null;

                    if (handle.Tag is IAssetHandleTag tag)
                    {
                        tag.ClearAsset();
                    }

                    if (obj != null)
                    {
                        var assetsFile = handle.SourceFile;
                        if (assetsFile != null)
                        {
                            assetsFile.RemoveObject(obj);
                        }
                    }
                }
            }

            private sealed class EvictionSuspension : IDisposable
            {
                private AssetLruCache? cache;

                public EvictionSuspension(AssetLruCache cache)
                {
                    this.cache = cache;
                }

                public void Dispose()
                {
                    var target = System.Threading.Interlocked.Exchange(ref cache, null);
                    target?.ResumeEviction();
                }
            }
        }
    }
}
