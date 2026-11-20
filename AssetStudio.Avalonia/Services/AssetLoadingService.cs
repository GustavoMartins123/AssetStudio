using System;
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

        private CancellationTokenSource? _indexingCts;
        private Task? _indexingTask;
        private volatile bool _isIndexingPaused;
        private readonly object _pendingQueueLock = new();
        private string[] _pendingFiles = Array.Empty<string>();
        private bool[] _consumedPendingFlags = Array.Empty<bool>();
        private int _priorityPendingCursor;
        private int _regularPendingCursor;
        private int _remainingPendingCount;
        private Dictionary<string, List<int>> _pendingIndicesByPath = new(StringComparer.OrdinalIgnoreCase);
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
                    _pendingIndicesByPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                var count = files.Count;
                var array = new string[count];
                var consumed = new bool[count];
                var map = new Dictionary<string, List<int>>(count, StringComparer.OrdinalIgnoreCase);

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
            int handleFlushBatchSize = DefaultHandleFlushBatchSize)
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
                    var dirComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    var distinctDirs = new HashSet<string>(dirComparer);
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
                                await assetsManager.IndexSingleFileInWorkerAsync(file);
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
                        saveCacheCallback(paths[0], currentScanResult);
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
