using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public class AssetLoadingService
    {
        public const int DefaultHandleFlushBatchSize = 5000;

        private CancellationTokenSource? _indexingCts;
        private Task? _indexingTask;
        private bool _isIndexingPaused;
        private List<string> _pendingFilesToIndex = new();
        private System.Diagnostics.Stopwatch? _uiThrottleStopwatch;
        private Action<IndexingProgressUpdate>? _indexingProgressCallback;
        private readonly object _indexingStateLock = new();
        private int _indexingTotalFiles;
        private int _indexingProcessedFiles;
        private string _currentIndexingFile = string.Empty;
        private string _lastReadIndexingFile = string.Empty;

        public bool IsIndexingActive => _indexingTask != null && !_indexingTask.IsCompleted;
        public bool IsIndexingPaused => _isIndexingPaused;

        public List<string> PendingFilesToIndex
        {
            get
            {
                lock (_pendingFilesToIndex)
                {
                    return _pendingFilesToIndex.ToList();
                }
            }
        }

        public void RemovePendingFile(string sourcePath)
        {
            var removed = false;
            lock (_pendingFilesToIndex)
            {
                for (int i = _pendingFilesToIndex.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(Path.GetFullPath(_pendingFilesToIndex[i]), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingFilesToIndex.RemoveAt(i);
                        removed = true;
                    }
                }
            }

            if (removed)
            {
                EmitIndexingProgress(_isIndexingPaused ? "paused" : "running");
            }
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
            _pendingFilesToIndex = files.ToList();
            _uiThrottleStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var effectiveHandleFlushBatchSize = handleFlushBatchSize > 0
                ? handleFlushBatchSize
                : DefaultHandleFlushBatchSize;
            var originalTotal = _pendingFilesToIndex.Count;
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
                int batchSize = 40;
                int loadedCount = 0;
                var pendingHandleFlushBuffer = onHandleBatchReady != null
                    ? new List<AssetHandle>(Math.Min(effectiveHandleFlushBatchSize, DefaultHandleFlushBatchSize))
                    : null;

                while (_pendingFilesToIndex.Count > 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    while (_isIndexingPaused && !token.IsCancellationRequested)
                    {
                        await Task.Delay(200);
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
                    }
                    catch (MemoryPressureException ex)
                    {
                        onMemoryPressureError(ex);
                        _isIndexingPaused = true;
                        EmitIndexingProgress("paused");

                        while (_isIndexingPaused && !token.IsCancellationRequested)
                        {
                            await Task.Delay(500);
                        }

                        if (!token.IsCancellationRequested)
                        {
                            EmitIndexingProgress("running");
                        }
                        continue;
                    }

                    var batch = new List<string>();
                    lock (_pendingFilesToIndex)
                    {
                        var keywords = new List<string>();
                        if (activeFilters.Contains(ClassIDType.Texture2D) || activeFilters.Contains(ClassIDType.Sprite))
                            keywords.AddRange(new[] { "texture", "sprite", "atlas", "image", "pic" });
                        if (activeFilters.Contains(ClassIDType.AudioClip))
                            keywords.AddRange(new[] { "audio", "sound", "music", "sfx", "clip" });
                        if (activeFilters.Contains(ClassIDType.Mesh))
                            keywords.AddRange(new[] { "mesh", "model", "geom", "3d" });
                        if (activeFilters.Contains(ClassIDType.AnimationClip) || activeFilters.Contains(ClassIDType.Animator))
                            keywords.AddRange(new[] { "anim", "motion", "controller" });
                        if (activeFilters.Contains(ClassIDType.Shader))
                            keywords.Add("shader");
                        if (activeFilters.Contains(ClassIDType.MonoBehaviour))
                            keywords.AddRange(new[] { "script", "behavior", "mono" });

                        if (keywords.Count > 0)
                        {
                            for (int i = 0; i < _pendingFilesToIndex.Count && batch.Count < batchSize; i++)
                            {
                                var file = _pendingFilesToIndex[i];
                                var fileName = Path.GetFileName(file).ToLowerInvariant();
                                if (keywords.Any(k => fileName.Contains(k)))
                                {
                                    batch.Add(file);
                                    _pendingFilesToIndex.RemoveAt(i);
                                    i--;
                                }
                            }
                        }

                        while (_pendingFilesToIndex.Count > 0 && batch.Count < batchSize)
                        {
                            batch.Add(_pendingFilesToIndex[0]);
                            _pendingFilesToIndex.RemoveAt(0);
                        }
                    }

                    if (batch.Count == 0)
                    {
                        break;
                    }

                    lock (_indexingStateLock)
                    {
                        _currentIndexingFile = batch[0];
                    }
                    EmitIndexingProgress("running");

                    await waitPriorityAsync(token);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        assetsManager.LoadFiles(batch.ToArray());
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error loading batch of {batch.Count} files", ex);
                    }

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
                            flushRemainder: false);
                    }

                    loadedCount += batch.Count;
                    lock (_indexingStateLock)
                    {
                        _indexingProcessedFiles = loadedCount;
                        _lastReadIndexingFile = batch[batch.Count - 1];
                        _currentIndexingFile = string.Empty;
                    }
                    EmitIndexingProgress("running", batch);

                    var progressPercent = (int)((double)loadedCount / originalTotal * 100);

                    if (loadedCount < originalTotal && ShouldUpdateUi(shouldPauseBackgroundWork))
                    {
                        updateProgress(progressPercent, $"Indexed: {loadedCount:N0} / {originalTotal:N0} files ({progressPercent}%)");
                        onBatchLoaded();
                    }
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
            }, token);
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
                var pendingCount = 0;
                lock (_pendingFilesToIndex)
                {
                    pendingCount = _pendingFilesToIndex.Count;
                }

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
