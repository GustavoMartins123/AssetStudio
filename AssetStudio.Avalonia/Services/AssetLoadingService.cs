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
        private CancellationTokenSource? _indexingCts;
        private Task? _indexingTask;
        private bool _isIndexingPaused;
        private List<string> _pendingFilesToIndex = new();
        private System.Diagnostics.Stopwatch? _uiThrottleStopwatch;

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
            lock (_pendingFilesToIndex)
            {
                for (int i = _pendingFilesToIndex.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(Path.GetFullPath(_pendingFilesToIndex[i]), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingFilesToIndex.RemoveAt(i);
                    }
                }
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
            Action<bool, int, int> onFinished)
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

            var originalTotal = _pendingFilesToIndex.Count;

            _indexingTask = Task.Run(async () =>
            {
                int batchSize = 40;
                int loadedCount = 0;

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

                        while (_isIndexingPaused && !token.IsCancellationRequested)
                        {
                            await Task.Delay(500);
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

                    loadedCount += batch.Count;
                    var progressPercent = (int)((double)loadedCount / originalTotal * 100);

                    if (loadedCount < originalTotal && ShouldUpdateUi(shouldPauseBackgroundWork))
                    {
                        updateProgress(progressPercent, $"Indexed: {loadedCount:N0} / {originalTotal:N0} files ({progressPercent}%)");
                        onBatchLoaded();
                    }
                }

                var wasCancelled = token.IsCancellationRequested;

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

        public void PauseIndexing()
        {
            _isIndexingPaused = true;
        }

        public void ResumeIndexing()
        {
            _isIndexingPaused = false;
        }

        public void StopIndexing()
        {
            _indexingCts?.Cancel();
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
