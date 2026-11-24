using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Buffers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public class AssetLoadingService
    {
        public const int DefaultHandleFlushBatchSize = 5000;
        public const int DefaultProgressCheckpointBatchSize = 40;
        public const int MaxMidIndexRevalidationRetries = 2;

        private static readonly StringComparer PathKeyComparer =
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        private CancellationTokenSource? _indexingCts;
        private Task? _indexingTask;
        private volatile bool _isIndexingPaused;
        private readonly object _pendingQueueLock = new();
        private string[] _pendingFiles = Array.Empty<string>();
        private bool[] _consumedPendingFlags = Array.Empty<bool>();
        private int _priorityPendingCursor;
        private int _regularPendingCursor;
        private int _remainingPendingCount;
        private Dictionary<string, List<int>> _pendingIndicesByPath = new(PathKeyComparer);
        private System.Diagnostics.Stopwatch? _uiThrottleStopwatch;
        private Action<IndexingProgressUpdate>? _indexingProgressCallback;
        private readonly object _indexingStateLock = new();
        private readonly object _checkpointLock = new();
        private int _indexingTotalFiles;
        private int _indexingProcessedFiles;
        private string _currentIndexingFile = string.Empty;
        private string _lastReadIndexingFile = string.Empty;

        public bool IsIndexingActive => _indexingTask != null && !_indexingTask.IsCompleted;
        public bool IsIndexingPaused => _isIndexingPaused;
        public Task? ActiveIndexingTask => _indexingTask;

        public int PendingCount
        {
            get
            {
                lock (_pendingQueueLock)
                {
                    return _remainingPendingCount;
                }
            }
        }

        public List<string> PendingFilesToIndex
        {
            get
            {
                lock (_pendingQueueLock)
                {
                    if (_remainingPendingCount <= 0)
                    {
                        return new List<string>();
                    }

                    var result = new List<string>(_remainingPendingCount);
                    for (int i = _regularPendingCursor; i < _pendingFiles.Length; i++)
                    {
                        if (!_consumedPendingFlags[i])
                        {
                            result.Add(_pendingFiles[i]);
                        }
                    }

                    return result;
                }
            }
        }

        internal void InitializePendingQueue(IReadOnlyList<string> files)
        {
            lock (_pendingQueueLock)
            {
                if (files == null || files.Count == 0)
                {
                    _pendingFiles = Array.Empty<string>();
                    _consumedPendingFlags = Array.Empty<bool>();
                    _priorityPendingCursor = 0;
                    _regularPendingCursor = 0;
                    _remainingPendingCount = 0;
                    _pendingIndicesByPath = new Dictionary<string, List<int>>(PathKeyComparer);
                    return;
                }

                var count = files.Count;
                var array = new string[count];
                var consumed = new bool[count];
                var map = new Dictionary<string, List<int>>(count, PathKeyComparer);

                for (int i = 0; i < count; i++)
                {
                    var file = files[i] ?? string.Empty;
                    array[i] = file;
                    var normalizedKey = NormalizePendingPathKey(file);
                    if (!map.TryGetValue(normalizedKey, out var list))
                    {
                        list = new List<int>(1);
                        map[normalizedKey] = list;
                    }
                    list.Add(i);
                }

                _pendingFiles = array;
                _consumedPendingFlags = consumed;
                _priorityPendingCursor = 0;
                _regularPendingCursor = 0;
                _remainingPendingCount = count;
                _pendingIndicesByPath = map;
            }
        }

        internal bool TryDequeueNextPendingFile(SearchValues<string>? keywordSearch, out string file)
        {
            lock (_pendingQueueLock)
            {
                if (_remainingPendingCount <= 0)
                {
                    file = string.Empty;
                    return false;
                }

                if (keywordSearch != null)
                {
                    while (_priorityPendingCursor < _pendingFiles.Length)
                    {
                        var idx = _priorityPendingCursor++;
                        if (_consumedPendingFlags[idx])
                        {
                            continue;
                        }

                        var candidate = _pendingFiles[idx];
                        if (Path.GetFileName(candidate.AsSpan()).ContainsAny(keywordSearch))
                        {
                            _consumedPendingFlags[idx] = true;
                            _remainingPendingCount--;
                            file = candidate;
                            return true;
                        }
                    }
                }

                while (_regularPendingCursor < _pendingFiles.Length)
                {
                    var idx = _regularPendingCursor++;
                    if (_consumedPendingFlags[idx])
                    {
                        continue;
                    }

                    _consumedPendingFlags[idx] = true;
                    _remainingPendingCount--;
                    file = _pendingFiles[idx];
                    return true;
                }

                file = string.Empty;
                return false;
            }
        }

        private static string NormalizePendingPathKey(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        public void RemovePendingFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            var removed = false;
            var key = NormalizePendingPathKey(sourcePath);
            lock (_pendingQueueLock)
            {
                if (_pendingIndicesByPath.TryGetValue(key, out var indices))
                {
                    for (int i = 0; i < indices.Count; i++)
                    {
                        var idx = indices[i];
                        if (!_consumedPendingFlags[idx])
                        {
                            _consumedPendingFlags[idx] = true;
                            _remainingPendingCount--;
                            removed = true;
                        }
                    }
                }
            }

            if (removed)
            {
                EmitIndexingProgress(_isIndexingPaused ? "paused" : "running");
            }
        }

        private static readonly string[][] PriorityKeywordGroups =
        {
            new[] { "texture", "sprite", "atlas", "image", "pic" },
            new[] { "audio", "sound", "music", "sfx", "clip" },
            new[] { "mesh", "model", "geom", "3d" },
            new[] { "anim", "motion", "controller" },
            new[] { "shader" },
            new[] { "script", "behavior", "mono" },
        };

        // One lazily-built SearchValues<string> per combination of keyword groups (2^6 combinations).
        private static readonly SearchValues<string>?[] PriorityKeywordSearchCache = new SearchValues<string>?[1 << PriorityKeywordGroups.Length];

        internal static SearchValues<string>? GetPriorityKeywordSearch(ICollection<ClassIDType>? activeFilters)
        {
            if (activeFilters == null || activeFilters.Count == 0)
            {
                return null;
            }

            var mask = 0;
            foreach (var filter in activeFilters)
            {
                switch (filter)
                {
                    case ClassIDType.Texture2D or ClassIDType.Sprite:
                        mask |= 1 << 0;
                        break;
                    case ClassIDType.AudioClip:
                        mask |= 1 << 1;
                        break;
                    case ClassIDType.Mesh:
                        mask |= 1 << 2;
                        break;
                    case ClassIDType.AnimationClip or ClassIDType.Animator:
                        mask |= 1 << 3;
                        break;
                    case ClassIDType.Shader:
                        mask |= 1 << 4;
                        break;
                    case ClassIDType.MonoBehaviour:
                        mask |= 1 << 5;
                        break;
                }

                if (mask == (1 << PriorityKeywordGroups.Length) - 1)
                {
                    break;
                }
            }

            if (mask == 0)
            {
                return null;
            }

            var cached = Volatile.Read(ref PriorityKeywordSearchCache[mask]);
            if (cached != null)
            {
                return cached;
            }

            var keywords = new List<string>();
            for (int g = 0; g < PriorityKeywordGroups.Length; g++)
            {
                if ((mask & (1 << g)) != 0)
                {
                    keywords.AddRange(PriorityKeywordGroups[g]);
                }
            }

            var created = SearchValues.Create(keywords.ToArray(), StringComparison.OrdinalIgnoreCase);
            return Interlocked.CompareExchange(ref PriorityKeywordSearchCache[mask], created, null) ?? created;
        }
        public void StartProgressiveIndexing(
            AssetsManager assetsManager,
            List<string> files,
            string[] paths,
            ProjectScanResult? currentScanResult,
            List<ClassIDType> activeFilters,
            Func<bool> shouldPauseBackgroundWork,
            Func<CancellationToken, Task> waitPriorityAsync,
            Action<int, string> updateProgress,
            Action<MemoryPressureException> onMemoryPressureError,
            Action onBatchLoaded,
            Action<string, ProjectScanResult> saveCacheCallback,
            Action<IndexingProgressUpdate> onIndexingProgressChanged,
            Action<bool, int, int> onFinished,
            Action<IReadOnlyList<AssetHandle>>? onHandleBatchReady = null,
            int handleFlushBatchSize = DefaultHandleFlushBatchSize,
            Action<string, ProjectScanResult, IReadOnlyList<AssetHandle>?>? saveReconciledCacheCallback = null)
        {
            if (_indexingCts != null)
            {
                _indexingCts.Cancel();
                _indexingCts.Dispose();
            }

            _indexingCts = new CancellationTokenSource();
            var token = _indexingCts.Token;
            _isIndexingPaused = false;
            InitializePendingQueue(files);
            _uiThrottleStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var effectiveHandleFlushBatchSize = handleFlushBatchSize > 0
                ? handleFlushBatchSize
                : DefaultHandleFlushBatchSize;
            var originalTotal = PendingCount;
            lock (_indexingStateLock)
            {
                _indexingProgressCallback = onIndexingProgressChanged;
                _indexingTotalFiles = originalTotal;
                _indexingProcessedFiles = 0;
                _currentIndexingFile = string.Empty;
                _lastReadIndexingFile = string.Empty;
            }
            EmitIndexingProgress("running");

            // PERF-11: Snapshot baseline (Length, LastWriteTimeUtc.Ticks) at t0 so workers detect
            // live file mutations during progressive indexing.
            var baselineStatsByPath = BuildBaselineFileStats(currentScanResult);
            var inFlightMutatedPaths = new ConcurrentDictionary<string, byte>(PathKeyComparer);
            var sceneMetadataCollector = ProjectLoadingService.ResetSceneMetadataCollector(assetsManager);

            _indexingTask = Task.Run(async () =>
            {
                int checkpointBatchSize = DefaultProgressCheckpointBatchSize;
                int loadedCount = 0;
                var pendingHandleFlushBuffer = onHandleBatchReady != null
                    ? new List<AssetHandle>(Math.Min(effectiveHandleFlushBatchSize, DefaultHandleFlushBatchSize))
                    : null;
                var newlyReadCheckpointBuffer = new List<string>(checkpointBatchSize);
                var memoryPressurePauseGate = new object();
                var memoryPressureNotifiedForCurrentPause = false;

                // Pre-merge split assets per distinct directory once upfront so continuous workers never scan directories per file.
                try
                {
                    var distinctDirs = new HashSet<string>(PathKeyComparer);
                    for (int i = 0; i < files.Count; i++)
                    {
                        var f = files[i];
                        if (string.IsNullOrWhiteSpace(f))
                        {
                            continue;
                        }

                        try
                        {
                            var dir = Path.GetDirectoryName(Path.GetFullPath(f));
                            if (!string.IsNullOrEmpty(dir) && distinctDirs.Add(dir))
                            {
                                ImportHelper.MergeSplitAssets(dir);
                            }
                        }
                        catch
                        {
                            // Ignore invalid synthetic paths during pre-merge.
                        }
                    }
                }
                catch
                {
                    // Ignore pre-merge errors.
                }

                var configuredWorkers = AssetsManager.GetConfiguredThreadCount(
                    "ASSETSTUDIO_LAZY_LOAD_THREADS",
                    AssetsManager.DefaultLazyLoadThreadRatio);
                var workerCount = Math.Clamp(configuredWorkers, 1, Math.Max(1, originalTotal));

                void FlushCheckpointProgress(bool forceFlushProgress, string? latestCompletedFile)
                {
                    List<string>? newlyReadSnapshot = null;
                    var currentProcessed = Volatile.Read(ref loadedCount);

                    lock (_checkpointLock)
                    {
                        if (onHandleBatchReady != null && pendingHandleFlushBuffer != null)
                        {
                            FlushHandleBatches(
                                assetsManager.ProjectIndex,
                                pendingHandleFlushBuffer,
                                effectiveHandleFlushBatchSize,
                                onHandleBatchReady,
                                flushRemainder: false);
                        }

                        if (!string.IsNullOrEmpty(latestCompletedFile))
                        {
                            newlyReadCheckpointBuffer.Add(latestCompletedFile);
                        }

                        if (forceFlushProgress || newlyReadCheckpointBuffer.Count >= checkpointBatchSize)
                        {
                            if (newlyReadCheckpointBuffer.Count > 0)
                            {
                                newlyReadSnapshot = new List<string>(newlyReadCheckpointBuffer);
                                newlyReadCheckpointBuffer.Clear();
                            }

                            lock (_indexingStateLock)
                            {
                                _indexingProcessedFiles = currentProcessed;
                                if (!string.IsNullOrEmpty(latestCompletedFile))
                                {
                                    _lastReadIndexingFile = latestCompletedFile;
                                }
                                else if (newlyReadSnapshot != null && newlyReadSnapshot.Count > 0)
                                {
                                    _lastReadIndexingFile = newlyReadSnapshot[newlyReadSnapshot.Count - 1];
                                }

                                if (PendingCount == 0)
                                {
                                    _currentIndexingFile = string.Empty;
                                }
                            }
                        }
                    }

                    if (newlyReadSnapshot != null)
                    {
                        EmitIndexingProgress("running", newlyReadSnapshot);

                        var progressPercent = originalTotal <= 0
                            ? 100
                            : (int)((double)currentProcessed / originalTotal * 100);

                        if (currentProcessed < originalTotal && ShouldUpdateUi(shouldPauseBackgroundWork))
                        {
                            updateProgress(progressPercent, $"Indexed: {currentProcessed:N0} / {originalTotal:N0} files ({progressPercent}%)");
                            onBatchLoaded();
                        }
                    }
                }

                async Task RunContinuousWorkerAsync()
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            while (_isIndexingPaused && !token.IsCancellationRequested)
                            {
                                await Task.Delay(50, token);
                            }

                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            await waitPriorityAsync(token);
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            try
                            {
                                AssetsManager.ThrowIfMemoryPressureTooHigh("progressive indexing");
                                lock (memoryPressurePauseGate)
                                {
                                    memoryPressureNotifiedForCurrentPause = false;
                                }
                            }
                            catch (MemoryPressureException ex)
                            {
                                var shouldNotify = false;
                                lock (memoryPressurePauseGate)
                                {
                                    if (!memoryPressureNotifiedForCurrentPause)
                                    {
                                        memoryPressureNotifiedForCurrentPause = true;
                                        _isIndexingPaused = true;
                                        shouldNotify = true;
                                    }
                                }

                                if (shouldNotify)
                                {
                                    onMemoryPressureError(ex);
                                    EmitIndexingProgress("paused");
                                }

                                while (_isIndexingPaused && !token.IsCancellationRequested)
                                {
                                    await Task.Delay(100, token);
                                }

                                if (shouldNotify && !token.IsCancellationRequested)
                                {
                                    EmitIndexingProgress("running");
                                }
                                continue;
                            }

                            var keywordSearch = GetPriorityKeywordSearch(activeFilters);
                            if (!TryDequeueNextPendingFile(keywordSearch, out var file))
                            {
                                break;
                            }

                            var shouldEmitInitialRunningFile = false;
                            lock (_indexingStateLock)
                            {
                                if (string.IsNullOrEmpty(_currentIndexingFile))
                                {
                                    shouldEmitInitialRunningFile = _indexingProcessedFiles == 0;
                                }
                                _currentIndexingFile = file;
                            }

                            if (shouldEmitInitialRunningFile)
                            {
                                EmitIndexingProgress("running");
                            }

                            await waitPriorityAsync(token);
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            try
                            {
                                var preStats = CaptureLiveFileStat(file);
                                if (IsLiveStatMutatedAgainstBaseline(file, preStats, baselineStatsByPath))
                                {
                                    inFlightMutatedPaths[NormalizePendingPathKey(file)] = 0;
                                }

                                await assetsManager.IndexSingleFileInWorkerAsync(file);
                                ProjectLoadingService.TryCollectHeaderExternalsAndScenesFromFile(
                                    file,
                                    assetsManager.DecompressionCache,
                                    assetsManager.SpecifyUnityVersion,
                                    sceneMetadataCollector);

                                var postStats = CaptureLiveFileStat(file);
                                if (preStats != postStats || IsLiveStatMutatedAgainstBaseline(file, postStats, baselineStatsByPath))
                                {
                                    inFlightMutatedPaths[NormalizePendingPathKey(file)] = 0;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error indexing file {file}", ex);
                            }

                            var completedNow = Interlocked.Increment(ref loadedCount);
                            var isCheckpoint = (completedNow % checkpointBatchSize) == 0 || completedNow == originalTotal || PendingCount == 0;
                            FlushCheckpointProgress(forceFlushProgress: isCheckpoint, latestCompletedFile: file);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Worker exited cleanly on cancellation.
                    }
                }

                var workerTasks = new Task[workerCount];
                for (int w = 0; w < workerCount; w++)
                {
                    workerTasks[w] = Task.Run(RunContinuousWorkerAsync);
                }

                await Task.WhenAll(workerTasks);

                // Finalize any remaining buffered progress items and handles.
                FlushCheckpointProgress(forceFlushProgress: true, latestCompletedFile: null);

                if (assetsManager.LazyLoading)
                {
                    assetsManager.ClearLoadedFilesKeepIndex();
                }

                if (onHandleBatchReady != null && pendingHandleFlushBuffer != null)
                {
                    FlushHandleBatches(
                        assetsManager.ProjectIndex,
                        pendingHandleFlushBuffer,
                        effectiveHandleFlushBatchSize,
                        onHandleBatchReady,
                        flushRemainder: true);
                }

                lock (_indexingStateLock)
                {
                    _indexingProcessedFiles = loadedCount;
                    _currentIndexingFile = string.Empty;
                }

                var wasCancelled = token.IsCancellationRequested;
                EmitIndexingProgress(wasCancelled ? "cancelled" : "completed");

                if (currentScanResult != null && paths.Length == 1 && Directory.Exists(paths[0]) && !wasCancelled)
                {
                    try
                    {
                        var reconciliation = await ReconcileMidIndexMutationsAsync(
                            assetsManager,
                            paths[0],
                            currentScanResult,
                            inFlightMutatedPaths.Keys.ToList(),
                            token);

                        if (reconciliation.CanCommit)
                        {
                            if (saveReconciledCacheCallback != null)
                            {
                                saveReconciledCacheCallback(paths[0], reconciliation.ValidatedScanResult, reconciliation.ReconciledHandles);
                            }
                            else if (reconciliation.ReconciledHandles == null)
                            {
                                saveCacheCallback(paths[0], reconciliation.ValidatedScanResult);
                            }
                            else
                            {
                                Logger.Warning($"Mid-index mutation reconciled for '{paths[0]}', but caller did not supply a reconciled cache callback; skipping stale t0 cache stamp.");
                            }
                        }
                        else
                        {
                            Logger.Warning($"Skipping warm index cache commit for '{paths[0]}': {reconciliation.AbortReason}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to save index cache: {ex.Message}");
                    }
                }

                _uiThrottleStopwatch?.Stop();
                _uiThrottleStopwatch = null;

                onFinished(wasCancelled, loadedCount, originalTotal);
            });
        }

        public static int FlushHandleBatches(
            ProjectIndex projectIndex,
            List<AssetHandle> pendingBuffer,
            int batchSize,
            Action<IReadOnlyList<AssetHandle>> onHandleBatchReady,
            bool flushRemainder = false)
        {
            if (projectIndex == null)
            {
                throw new ArgumentNullException(nameof(projectIndex));
            }

            if (pendingBuffer == null)
            {
                throw new ArgumentNullException(nameof(pendingBuffer));
            }

            if (onHandleBatchReady == null)
            {
                throw new ArgumentNullException(nameof(onHandleBatchReady));
            }

            var effectiveBatchSize = batchSize > 0 ? batchSize : DefaultHandleFlushBatchSize;
            var flushedTotal = 0;

            while (true)
            {
                var needed = Math.Max(1, effectiveBatchSize - pendingBuffer.Count);
                var drainedAny = false;
                foreach (var handle in projectIndex.DrainPendingHandles(needed))
                {
                    pendingBuffer.Add(handle);
                    drainedAny = true;
                }

                if (pendingBuffer.Count >= effectiveBatchSize)
                {
                    var chunk = pendingBuffer.ToArray();
                    pendingBuffer.Clear();
                    flushedTotal += chunk.Length;
                    InvokeHandleBatchCallbackSafe(onHandleBatchReady, chunk);
                    continue;
                }

                if (!drainedAny)
                {
                    break;
                }
            }

            if (flushRemainder && pendingBuffer.Count > 0)
            {
                var remainder = pendingBuffer.ToArray();
                pendingBuffer.Clear();
                flushedTotal += remainder.Length;
                InvokeHandleBatchCallbackSafe(onHandleBatchReady, remainder);
            }

            return flushedTotal;
        }

        private static void InvokeHandleBatchCallbackSafe(
            Action<IReadOnlyList<AssetHandle>> onHandleBatchReady,
            IReadOnlyList<AssetHandle> batch)
        {
            try
            {
                onHandleBatchReady(batch);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to flush indexed handle batch ({batch.Count:N0} handles): {ex.Message}");
            }
        }

        internal readonly struct MidIndexReconciliationResult
        {
            public MidIndexReconciliationResult(
                bool canCommit,
                ProjectScanResult validatedScanResult,
                IReadOnlyList<AssetHandle>? reconciledHandles,
                int retryCount,
                string abortReason)
            {
                CanCommit = canCommit;
                ValidatedScanResult = validatedScanResult;
                ReconciledHandles = reconciledHandles;
                RetryCount = retryCount;
                AbortReason = abortReason;
            }

            public bool CanCommit { get; }
            public ProjectScanResult ValidatedScanResult { get; }
            public IReadOnlyList<AssetHandle>? ReconciledHandles { get; }
            public int RetryCount { get; }
            public string AbortReason { get; }
        }

        internal static async Task<MidIndexReconciliationResult> ReconcileMidIndexMutationsAsync(
            AssetsManager assetsManager,
            string folderPath,
            ProjectScanResult baselineScan,
            IReadOnlyCollection<string>? inFlightDirtyPaths,
            CancellationToken cancellationToken,
            int maxRetries = MaxMidIndexRevalidationRetries)
        {
            var effectiveScan = baselineScan;
            var knownDirty = inFlightDirtyPaths;
            List<AssetHandle>? workingHandles = null;
            var retries = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var revalidation = ProjectScanner.RevalidateFolder(
                    folderPath,
                    effectiveScan,
                    knownDirtyPaths: knownDirty,
                    cancellationToken: cancellationToken);

                if (revalidation.IsUnchanged)
                {
                    return new MidIndexReconciliationResult(
                        canCommit: true,
                        validatedScanResult: effectiveScan,
                        reconciledHandles: workingHandles,
                        retryCount: retries,
                        abortReason: string.Empty);
                }

                if (revalidation.DecryptionContextChanged)
                {
                    return new MidIndexReconciliationResult(
                        canCommit: false,
                        validatedScanResult: revalidation.CurrentScanResult,
                        reconciledHandles: null,
                        retryCount: retries,
                        abortReason: "Active decryption context changed mid-index.");
                }

                if (retries >= maxRetries)
                {
                    return new MidIndexReconciliationResult(
                        canCommit: false,
                        validatedScanResult: revalidation.CurrentScanResult,
                        reconciledHandles: null,
                        retryCount: retries,
                        abortReason: $"Files continued mutating mid-index after {maxRetries} revalidation retries ({revalidation.AddedFiles.Count} added, {revalidation.ModifiedFiles.Count} modified, {revalidation.DeletedFiles.Count} deleted).");
                }

                retries++;
                Logger.Warning(
                    $"Mid-index mutation detected in '{folderPath}' ({revalidation.AddedFiles.Count} added, {revalidation.ModifiedFiles.Count} modified, {revalidation.DeletedFiles.Count} deleted); " +
                    $"re-indexing changed files before cache commit (retry {retries}/{maxRetries}).");

                var baseHandles = workingHandles ?? assetsManager.ProjectIndex.GetHandles().ToList();
                var dropPaths = new HashSet<string>(PathKeyComparer);
                foreach (var p in revalidation.ModifiedFiles)
                {
                    dropPaths.Add(NormalizePendingPathKey(p));
                }
                foreach (var p in revalidation.DeletedFiles)
                {
                    dropPaths.Add(NormalizePendingPathKey(p));
                }

                var filtered = new List<AssetHandle>(baseHandles.Count);
                for (var i = 0; i < baseHandles.Count; i++)
                {
                    var h = baseHandles[i];
                    var key = NormalizePendingPathKey(h.OriginalPath);
                    if (!dropPaths.Contains(key))
                    {
                        filtered.Add(h);
                    }
                }

                var filesToReindex = new List<string>(revalidation.AddedFiles.Count + revalidation.ModifiedFiles.Count);
                filesToReindex.AddRange(revalidation.AddedFiles);
                filesToReindex.AddRange(revalidation.ModifiedFiles);

                if (filesToReindex.Count > 0)
                {
                    var collector = ProjectLoadingService.GetOrCreateSceneMetadataCollector(assetsManager);
                    foreach (var dropPath in dropPaths)
                    {
                        collector.RemoveByOriginalPath(dropPath);
                    }

                    var scratchManager = new AssetsManager
                    {
                        LazyLoading = true,
                        SpecifyUnityVersion = assetsManager.SpecifyUnityVersion
                    };
                    try
                    {
                        foreach (var changedFile in filesToReindex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!File.Exists(changedFile))
                            {
                                continue;
                            }

                            try
                            {
                                await scratchManager.IndexSingleFileInWorkerAsync(changedFile);
                                ProjectLoadingService.TryCollectHeaderExternalsAndScenesFromFile(
                                    changedFile,
                                    assetsManager.DecompressionCache,
                                    assetsManager.SpecifyUnityVersion,
                                    collector);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"Error re-indexing mutated file '{changedFile}': {ex.Message}");
                            }
                        }

                        filtered.AddRange(scratchManager.ProjectIndex.GetHandles());
                    }
                    finally
                    {
                        scratchManager.Clear();
                    }
                }

                workingHandles = filtered;
                effectiveScan = revalidation.CurrentScanResult;
                knownDirty = null;
            }
        }

        private static Dictionary<string, (long Size, long LastWriteUtcTicks)>? BuildBaselineFileStats(ProjectScanResult? scanResult)
        {
            if (scanResult == null || scanResult.Files.Count == 0)
            {
                return null;
            }

            var map = new Dictionary<string, (long Size, long LastWriteUtcTicks)>(scanResult.Files.Count, PathKeyComparer);
            for (var i = 0; i < scanResult.Files.Count; i++)
            {
                var f = scanResult.Files[i];
                if (!string.IsNullOrWhiteSpace(f.Path))
                {
                    map[NormalizePendingPathKey(f.Path)] = (f.Size, f.LastWriteTimeUtcTicks);
                }
            }

            return map;
        }

        private static (bool Exists, long Size, long LastWriteUtcTicks) CaptureLiveFileStat(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    return (false, 0L, 0L);
                }

                return (true, info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                return (false, 0L, 0L);
            }
        }

        private static bool IsLiveStatMutatedAgainstBaseline(
            string filePath,
            (bool Exists, long Size, long LastWriteUtcTicks) liveStat,
            Dictionary<string, (long Size, long LastWriteUtcTicks)>? baselineStatsByPath)
        {
            if (baselineStatsByPath == null)
            {
                return false;
            }

            if (!liveStat.Exists)
            {
                return true;
            }

            var key = NormalizePendingPathKey(filePath);
            if (!baselineStatsByPath.TryGetValue(key, out var baseline))
            {
                return true;
            }

            return liveStat.Size != baseline.Size || liveStat.LastWriteUtcTicks != baseline.LastWriteUtcTicks;
        }

        public void PauseIndexing()
        {
            _isIndexingPaused = true;
            EmitIndexingProgress("paused");
        }

        public void ResumeIndexing()
        {
            _isIndexingPaused = false;
            EmitIndexingProgress("running");
        }

        public void StopIndexing()
        {
            EmitIndexingProgress("cancelling");
            _indexingCts?.Cancel();
        }

        private void EmitIndexingProgress(string status, IReadOnlyList<string>? newlyReadFiles = null)
        {
            Action<IndexingProgressUpdate>? callback;
            IndexingProgressUpdate update;
            lock (_indexingStateLock)
            {
                callback = _indexingProgressCallback;
                var pendingCount = PendingCount;

                update = new IndexingProgressUpdate
                {
                    Status = status,
                    TotalFiles = _indexingTotalFiles,
                    ProcessedFiles = _indexingProcessedFiles,
                    PendingFiles = pendingCount,
                    PercentComplete = _indexingTotalFiles <= 0
                        ? 100
                        : Math.Min(100, Math.Max(0, _indexingProcessedFiles * 100.0 / _indexingTotalFiles)),
                    CurrentFile = _currentIndexingFile,
                    LastReadFile = _lastReadIndexingFile,
                    NewlyReadFiles = newlyReadFiles == null ? Array.Empty<string>() : newlyReadFiles.ToList()
                };
            }

            if (callback == null)
            {
                return;
            }

            try
            {
                callback(update);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to publish indexing progress: {ex.Message}");
            }
        }

        private bool ShouldUpdateUi(Func<bool> shouldPauseBackgroundWork)
        {
            if (shouldPauseBackgroundWork())
            {
                return false;
            }

            var stopwatch = _uiThrottleStopwatch;
            if (stopwatch == null)
            {
                return false;
            }

            lock (stopwatch)
            {
                if (stopwatch.ElapsedMilliseconds < 500)
                {
                    return false;
                }
                stopwatch.Restart();
                return true;
            }
        }
    }
}
