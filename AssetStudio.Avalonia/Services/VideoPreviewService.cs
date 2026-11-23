using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using FFmpegVideoPlayer.Core;

namespace AssetStudio.Avalonia.Services
{
    /// <summary>
    /// Codec, container, dimension, frame-rate, duration, and proxy-transcode inspection result for a VideoClip or raw video payload.
    /// Pure headless model with zero UI framework dependencies.
    /// </summary>
    public sealed class VideoCodecContainerInfo
    {
        public required string ContainerFormat { get; init; }
        public required string VideoCodec { get; init; }
        public required string RecommendedExtension { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int ProxyWidth { get; init; }
        public int ProxyHeight { get; init; }
        public double FrameRate { get; init; }
        public ulong FrameCount { get; init; }
        public double EstimatedDurationSeconds { get; init; }
        public long ByteSize { get; init; }
        public bool HasAudio { get; init; }
        public bool IsNativelySupportedByFFmpeg { get; init; }
        public bool RequiresProxyTranscode { get; init; }
    }

    /// <summary>
    /// Prepared file handle for a video preview session.
    /// <see cref="IsOwnedTempFile"/> is false when FFmpeg can read an existing uncompressed standalone file directly from disk with zero copy.
    /// </summary>
    public sealed class VideoPreviewPreparedFile
    {
        public VideoPreviewPreparedFile(string assetIdKey, string filePath, long byteSize, bool isOwnedTempFile, bool fromCache = false)
        {
            AssetIdKey = assetIdKey ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            ByteSize = Math.Max(0L, byteSize);
            IsOwnedTempFile = isOwnedTempFile;
            FromCache = fromCache;
        }

        public string AssetIdKey { get; }
        public string FilePath { get; }
        public long ByteSize { get; }
        public bool IsOwnedTempFile { get; }
        public bool FromCache { get; }
    }

    /// <summary>
    /// Headless, UI-framework-free timeline tracker that keeps displayed video playback seconds locked to
    /// the frame actually painted on screen, prevents stale pre-seek frames from snapping the scrubber backward,
    /// handles both FFmpegMediaPlayer event orders (playback: PositionChanged-&gt;FrameReady; paused single-frame:
    /// FrameReady-&gt;PositionChanged), and works even when a container omits duration metadata.
    /// </summary>
    public sealed class VideoPlaybackTimelineTracker
    {
        public const long DefaultSeekBarrierTimeoutMs = 1200;

        private readonly object _gate = new();
        private long _durationMs;
        private long _presentedPositionMs;
        private bool _hasPresentedPosition;
        private long _stagedPlaybackPositionMs = -1;
        private long _activeSeekGeneration;
        private long _completedSeekGeneration;
        private long _pendingSeekTargetMs = -1;
        private long _pendingSeekBarrierTickMs;

        public long DurationMs
        {
            get
            {
                lock (_gate)
                {
                    return _durationMs;
                }
            }
        }

        public bool HasPendingSeek
        {
            get
            {
                lock (_gate)
                {
                    return _pendingSeekTargetMs >= 0;
                }
            }
        }

        public void Reset(long durationMs = 0)
        {
            lock (_gate)
            {
                _durationMs = Math.Max(0L, durationMs);
                _presentedPositionMs = 0L;
                _hasPresentedPosition = false;
                _stagedPlaybackPositionMs = -1L;
                _activeSeekGeneration++;
                _completedSeekGeneration = _activeSeekGeneration;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
            }
        }

        public void SetDurationMs(long durationMs)
        {
            if (durationMs <= 0)
            {
                return;
            }

            lock (_gate)
            {
                _durationMs = durationMs;
                if (_presentedPositionMs > _durationMs)
                {
                    _presentedPositionMs = _durationMs;
                }
                if (_pendingSeekTargetMs > _durationMs)
                {
                    _pendingSeekTargetMs = _durationMs;
                }
            }
        }

        public void OnMediaOpened(long durationMs)
        {
            lock (_gate)
            {
                if (durationMs > 0)
                {
                    _durationMs = durationMs;
                }
                _presentedPositionMs = 0L;
                _hasPresentedPosition = true;
                _stagedPlaybackPositionMs = -1L;
                _activeSeekGeneration++;
                _completedSeekGeneration = _activeSeekGeneration;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
            }
        }

        public long BeginSeek(long targetMs)
        {
            lock (_gate)
            {
                var clamped = ClampPositionLocked(targetMs);
                _activeSeekGeneration++;
                _pendingSeekTargetMs = clamped;
                _pendingSeekBarrierTickMs = 0L;
                _stagedPlaybackPositionMs = -1L;
                return _activeSeekGeneration;
            }
        }

        public void MarkSeekBarrierReached(long seekGeneration, long nowTickMs)
        {
            lock (_gate)
            {
                if (seekGeneration != _activeSeekGeneration)
                {
                    return;
                }

                _completedSeekGeneration = seekGeneration;
                _pendingSeekBarrierTickMs = nowTickMs;
            }
        }

        /// <summary>
        /// Handles <c>FFmpegMediaPlayer.PositionChanged</c> on the UI thread.
        /// During active playback (<paramref name="isPlaying"/> == true), <c>ProcessVideoPacket</c> fires
        /// <c>PositionChanged</c> immediately before <c>FrameReady</c>, so the timestamp is staged until the paired
        /// frame is actually painted. While paused/stopped (<c>ProcessSingleFrame</c>/<c>StepBackward</c>),
        /// <c>FrameReady</c> fires before <c>PositionChanged</c>, so the timestamp commits immediately.
        /// </summary>
        public bool OnPositionChangedFromPlayer(
            double positionMs,
            bool isPlaying,
            out long effectivePositionMs)
        {
            lock (_gate)
            {
                if (_activeSeekGeneration != _completedSeekGeneration)
                {
                    effectivePositionMs = GetEffectivePositionLocked(positionMs, 0L, allowBarrierTimeout: false);
                    return false;
                }

                var normalized = NormalizeMsLocked(positionMs);
                if (isPlaying)
                {
                    _stagedPlaybackPositionMs = normalized;
                    effectivePositionMs = GetEffectivePositionLocked(positionMs, 0L, allowBarrierTimeout: false);
                    return false;
                }

                _presentedPositionMs = normalized;
                _hasPresentedPosition = true;
                _stagedPlaybackPositionMs = -1L;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
                effectivePositionMs = _presentedPositionMs;
                return true;
            }
        }

        /// <summary>
        /// Handles <c>FFmpegMediaPlayer.FrameReady</c> on the UI thread immediately after the frame is painted.
        /// Commits the staged timestamp (or fallback PTS) unless a seek barrier is still draining stale pre-seek frames.
        /// </summary>
        public bool OnFramePresented(
            double fallbackPositionMs,
            out long effectivePositionMs)
        {
            lock (_gate)
            {
                if (_activeSeekGeneration != _completedSeekGeneration)
                {
                    _stagedPlaybackPositionMs = -1L;
                    effectivePositionMs = GetEffectivePositionLocked(fallbackPositionMs, 0L, allowBarrierTimeout: false);
                    return false;
                }

                long frameMs;
                if (_stagedPlaybackPositionMs >= 0)
                {
                    frameMs = _stagedPlaybackPositionMs;
                    _stagedPlaybackPositionMs = -1L;
                }
                else
                {
                    frameMs = NormalizeMsLocked(fallbackPositionMs);
                }

                _presentedPositionMs = frameMs;
                _hasPresentedPosition = true;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
                effectivePositionMs = _presentedPositionMs;
                return true;
            }
        }

        public long GetEffectivePositionMs(double fallbackPlayerPositionMs, long nowTickMs)
        {
            lock (_gate)
            {
                return GetEffectivePositionLocked(fallbackPlayerPositionMs, nowTickMs, allowBarrierTimeout: true);
            }
        }

        private long GetEffectivePositionLocked(double fallbackPlayerPositionMs, long nowTickMs, bool allowBarrierTimeout)
        {
            if (_pendingSeekTargetMs >= 0)
            {
                if (_activeSeekGeneration != _completedSeekGeneration)
                {
                    return _pendingSeekTargetMs;
                }

                if (!allowBarrierTimeout
                    || _pendingSeekBarrierTickMs == 0
                    || (nowTickMs - _pendingSeekBarrierTickMs) < DefaultSeekBarrierTimeoutMs)
                {
                    return _pendingSeekTargetMs;
                }

                // Seek completed and no new frame arrived within timeout (e.g. sought to EOF); commit seek target.
                _presentedPositionMs = _pendingSeekTargetMs;
                _hasPresentedPosition = true;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
                return _presentedPositionMs;
            }

            if (_hasPresentedPosition)
            {
                return _presentedPositionMs;
            }

            return NormalizeMsLocked(fallbackPlayerPositionMs);
        }

        private long NormalizeMsLocked(double rawMs)
        {
            if (double.IsNaN(rawMs) || double.IsInfinity(rawMs) || rawMs <= 0.0)
            {
                return 0L;
            }

            return ClampPositionLocked((long)Math.Round(rawMs));
        }

        private long ClampPositionLocked(long ms)
        {
            if (ms <= 0L)
            {
                return 0L;
            }

            return _durationMs > 0L && ms > _durationMs ? _durationMs : ms;
        }
    }

    /// <summary>
    /// Dedicated background teardown queue for <see cref="FFmpegMediaPlayer"/> and arbitrary disposable video backends.
    /// Never blocks the UI thread, breaks the 1000ms <c>FFmpegMediaPlayer.Stop()</c> self-deadlock by cancelling and joining
    /// <c>_playbackThread</c> outside <c>player._lock</c>, isolates exceptions, and deletes temp files only after the native
    /// file handle is closed.
    /// </summary>
    public sealed class VideoPlayerTeardownQueue : IDisposable
    {
        public const int DefaultPlaybackThreadJoinTimeoutMs = 250;
        public const int DefaultTeardownTimeoutMs = 1500;

        private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
        private readonly Thread _workerThread;
        private int _pendingCount;
        private int _completedCount;
        private int _disposed;

        public VideoPlayerTeardownQueue(string workerName = "AssetStudio.VideoPlayerTeardown")
        {
            _workerThread = new Thread(ProcessQueueLoop)
            {
                Name = workerName,
                IsBackground = true
            };
            _workerThread.Start();
        }

        public int PendingCount => Volatile.Read(ref _pendingCount);
        public int CompletedCount => Volatile.Read(ref _completedCount);

        public Task EnqueueTeardownAsync(
            Action teardownAction,
            Action? onCompleted = null,
            int timeoutMs = DefaultTeardownTimeoutMs)
        {
            ArgumentNullException.ThrowIfNull(teardownAction);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Volatile.Read(ref _disposed) != 0 || _queue.IsAddingCompleted)
            {
                _ = Task.Run(() => ExecuteIsolated(teardownAction, onCompleted, timeoutMs, tcs));
                return tcs.Task;
            }

            Interlocked.Increment(ref _pendingCount);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        ExecuteIsolated(teardownAction, onCompleted, timeoutMs, tcs);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        Interlocked.Increment(ref _completedCount);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pendingCount);
                _ = Task.Run(() => ExecuteIsolated(teardownAction, onCompleted, timeoutMs, tcs));
            }

            return tcs.Task;
        }

        private static void ExecuteIsolated(
            Action teardownAction,
            Action? onCompleted,
            int timeoutMs,
            TaskCompletionSource tcs)
        {
            try
            {
                if (timeoutMs > 0)
                {
                    using var doneGate = new ManualResetEventSlim(false);
                    var dedicatedThread = new Thread(() =>
                    {
                        try
                        {
                            teardownAction();
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Suppressed exception in video teardown action: {ex.Message}");
                        }
                        finally
                        {
                            try
                            {
                                doneGate.Set();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }
                    })
                    {
                        Name = "AssetStudio.VideoTeardownAction",
                        IsBackground = true
                    };
                    dedicatedThread.Start();
                    doneGate.Wait(timeoutMs);
                }
                else
                {
                    teardownAction();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Suppressed exception in video teardown wrapper: {ex.Message}");
            }
            finally
            {
                try
                {
                    onCompleted?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Suppressed exception in video teardown completion callback: {ex.Message}");
                }

                tcs.TrySetResult();
            }
        }

        private void ProcessQueueLoop()
        {
            try
            {
                foreach (var workItem in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        workItem();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _queue.CompleteAdding();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Headless coordinator for VideoClip preview preparation, rapid sequential switching, cancellation,
    /// in-flight task coalescing, and bounded LRU temp-file reuse.
    /// </summary>
    public sealed class VideoPreviewSessionCoordinator : IDisposable
    {
        public const int DefaultMaxCachedTempFiles = 2;
        public const int DefaultTempDeleteAttempts = 12;
        public const int DefaultTempDeleteRetryDelayMs = 60;

        private sealed class CachedTempEntry
        {
            public required string AssetIdKey { get; init; }
            public required string FilePath { get; init; }
            public required long ByteSize { get; init; }
            public required bool IsOwnedTempFile { get; init; }
            public long LastAccessOrder { get; set; }
            public bool IsInUse { get; set; }
        }

        private sealed class InFlightPreparation
        {
            public required string AssetIdKey { get; init; }
            public required long SessionId { get; init; }
            public required CancellationTokenSource Cts { get; init; }
            public required Task<VideoPreviewPreparedFile?> Task { get; init; }
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, CachedTempEntry> _cachedFiles = new(StringComparer.Ordinal);
        private readonly string _tempDirectory;
        private readonly int _maxCachedTempFiles;
        private readonly VideoPlayerTeardownQueue _teardownQueue;
        private readonly bool _ownsTeardownQueue;

        private InFlightPreparation? _inFlight;
        private long _sessionId;
        private long _cancelledSessionCount;
        private long _accessCounter;
        private bool _disposed;

        public VideoPreviewSessionCoordinator(
            string? tempDirectory = null,
            int maxCachedTempFiles = DefaultMaxCachedTempFiles,
            VideoPlayerTeardownQueue? teardownQueue = null)
        {
            _tempDirectory = !string.IsNullOrWhiteSpace(tempDirectory)
                ? tempDirectory
                : VideoPreviewService.GetDefaultTempDirectory();
            _maxCachedTempFiles = Math.Max(0, maxCachedTempFiles);
            _teardownQueue = teardownQueue ?? new VideoPlayerTeardownQueue();
            _ownsTeardownQueue = teardownQueue == null;
        }

        public long CurrentSessionId => Interlocked.Read(ref _sessionId);
        public long CancelledSessionCount => Interlocked.Read(ref _cancelledSessionCount);
        public string TempDirectory => _tempDirectory;
        public VideoPlayerTeardownQueue TeardownQueue => _teardownQueue;

        public int CachedTempFileCount
        {
            get
            {
                lock (_gate)
                {
                    return _cachedFiles.Count;
                }
            }
        }

        /// <summary>
        /// Begins a new video preview session for <paramref name="assetIdKey"/>, immediately cancelling any in-flight preparation
        /// for a previous session.
        /// </summary>
        public long BeginSession(string? assetIdKey = null)
        {
            CancellationTokenSource? previousCts = null;
            long newSessionId;

            lock (_gate)
            {
                newSessionId = Interlocked.Increment(ref _sessionId);
                if (_inFlight != null)
                {
                    previousCts = _inFlight.Cts;
                    _inFlight = null;
                    Interlocked.Increment(ref _cancelledSessionCount);
                }

                foreach (var entry in _cachedFiles.Values)
                {
                    entry.IsInUse = !string.IsNullOrEmpty(assetIdKey)
                        && string.Equals(entry.AssetIdKey, assetIdKey, StringComparison.Ordinal);
                }
            }

            TryCancelQuietly(previousCts);
            return newSessionId;
        }

        /// <summary>
        /// Cancels any in-flight preparation without advancing the session ID (e.g. user pressed Stop during preparation).
        /// </summary>
        public void CancelInFlightPreparation()
        {
            CancellationTokenSource? previousCts = null;
            lock (_gate)
            {
                if (_inFlight != null)
                {
                    previousCts = _inFlight.Cts;
                    _inFlight = null;
                    Interlocked.Increment(ref _cancelledSessionCount);
                }
            }

            TryCancelQuietly(previousCts);
        }

        public bool IsSessionCurrent(long sessionId)
        {
            return sessionId == Interlocked.Read(ref _sessionId);
        }

        /// <summary>
        /// Prepares or reuses the preview file for <paramref name="assetIdKey"/> under <paramref name="sessionId"/>.
        /// Concurrent callers for the SAME <c>(assetIdKey, sessionId)</c> coalesce onto the single in-flight task
        /// instead of cancelling and restarting the extraction.
        /// </summary>
        public async Task<VideoPreviewPreparedFile?> PrepareOrReuseFileAsync(
            string assetIdKey,
            long sessionId,
            Func<CancellationToken, VideoPreviewPreparedFile?> prepareWorker)
        {
            ArgumentNullException.ThrowIfNull(prepareWorker);
            if (string.IsNullOrEmpty(assetIdKey) || _disposed)
            {
                return null;
            }

            Task<VideoPreviewPreparedFile?> taskToAwait;
            CancellationTokenSource? ownedCts = null;

            lock (_gate)
            {
                if (_disposed || sessionId != Interlocked.Read(ref _sessionId))
                {
                    return null;
                }

                // 1) Check if an existing prepared file for this assetIdKey is already in cache and valid on disk.
                if (_cachedFiles.TryGetValue(assetIdKey, out var cached))
                {
                    if (IsPreparedFileValidOnDisk(cached.FilePath, cached.ByteSize))
                    {
                        cached.LastAccessOrder = ++_accessCounter;
                        cached.IsInUse = true;
                        return new VideoPreviewPreparedFile(
                            cached.AssetIdKey,
                            cached.FilePath,
                            cached.ByteSize,
                            cached.IsOwnedTempFile,
                            fromCache: true);
                    }

                    _cachedFiles.Remove(assetIdKey);
                }

                // 2) If an in-flight preparation is already running for the exact same (assetIdKey, sessionId), coalesce onto it!
                if (_inFlight != null
                    && _inFlight.SessionId == sessionId
                    && string.Equals(_inFlight.AssetIdKey, assetIdKey, StringComparison.Ordinal)
                    && !_inFlight.Cts.IsCancellationRequested)
                {
                    taskToAwait = _inFlight.Task;
                }
                else
                {
                    if (_inFlight != null)
                    {
                        TryCancelQuietly(_inFlight.Cts);
                        Interlocked.Increment(ref _cancelledSessionCount);
                        _inFlight = null;
                    }

                    ownedCts = new CancellationTokenSource();
                    var token = ownedCts.Token;
                    taskToAwait = Task.Run(() => RunPreparationWorker(assetIdKey, sessionId, prepareWorker, token), token);
                    _inFlight = new InFlightPreparation
                    {
                        AssetIdKey = assetIdKey,
                        SessionId = sessionId,
                        Cts = ownedCts,
                        Task = taskToAwait
                    };
                }
            }

            try
            {
                return await taskToAwait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                if (ownedCts != null)
                {
                    lock (_gate)
                    {
                        if (_inFlight != null && ReferenceEquals(_inFlight.Cts, ownedCts))
                        {
                            _inFlight = null;
                        }
                    }
                }
            }
        }

        private VideoPreviewPreparedFile? RunPreparationWorker(
            string assetIdKey,
            long sessionId,
            Func<CancellationToken, VideoPreviewPreparedFile?> prepareWorker,
            CancellationToken token)
        {
            VideoPreviewPreparedFile? prepared = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsSessionCurrent(sessionId))
                {
                    return null;
                }

                prepared = prepareWorker(token);
                if (prepared == null)
                {
                    return null;
                }

                if (token.IsCancellationRequested || !IsSessionCurrent(sessionId))
                {
                    if (prepared.IsOwnedTempFile)
                    {
                        MediaTempFiles.DeleteInBackground(prepared.FilePath, DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                    }
                    return null;
                }

                List<string>? evictedPaths = null;
                lock (_gate)
                {
                    if (_disposed || token.IsCancellationRequested || sessionId != Interlocked.Read(ref _sessionId))
                    {
                        if (prepared.IsOwnedTempFile)
                        {
                            MediaTempFiles.DeleteInBackground(prepared.FilePath, DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                        }
                        return null;
                    }

                    if (_maxCachedTempFiles > 0)
                    {
                        _cachedFiles[assetIdKey] = new CachedTempEntry
                        {
                            AssetIdKey = assetIdKey,
                            FilePath = prepared.FilePath,
                            ByteSize = prepared.ByteSize,
                            IsOwnedTempFile = prepared.IsOwnedTempFile,
                            LastAccessOrder = ++_accessCounter,
                            IsInUse = true
                        };

                        evictedPaths = CollectEvictedTempFilesLocked(protectedAssetIdKey: assetIdKey);
                    }
                }

                if (evictedPaths != null)
                {
                    for (int i = 0; i < evictedPaths.Count; i++)
                    {
                        MediaTempFiles.DeleteInBackground(evictedPaths[i], DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                    }
                }

                return prepared;
            }
            catch (OperationCanceledException)
            {
                if (prepared is { IsOwnedTempFile: true })
                {
                    MediaTempFiles.DeleteInBackground(prepared.FilePath, DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                }
                return null;
            }
            catch
            {
                if (prepared is { IsOwnedTempFile: true })
                {
                    MediaTempFiles.DeleteInBackground(prepared.FilePath, DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                }
                throw;
            }
        }

        /// <summary>
        /// Marks the file for <paramref name="assetIdKey"/> as no longer actively open by the player.
        /// Call this from the background teardown completion callback AFTER the player has closed its file handle.
        /// If <paramref name="deleteImmediately"/> is true (or cache capacity is 0), deletes the owned temp file immediately.
        /// </summary>
        public void NotifyPlayerReleasedFile(string? assetIdKey, string? filePath, bool deleteImmediately = false)
        {
            List<string>? pathsToDelete = null;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(assetIdKey) && _cachedFiles.TryGetValue(assetIdKey, out var entry))
                {
                    entry.IsInUse = false;
                    if (deleteImmediately || _maxCachedTempFiles <= 0 || _disposed)
                    {
                        _cachedFiles.Remove(assetIdKey);
                        if (entry.IsOwnedTempFile && !string.IsNullOrEmpty(entry.FilePath))
                        {
                            (pathsToDelete ??= new List<string>(2)).Add(entry.FilePath);
                        }
                    }
                    else
                    {
                        pathsToDelete = CollectEvictedTempFilesLocked(protectedAssetIdKey: null);
                    }
                }
                else if (!string.IsNullOrEmpty(filePath) && (deleteImmediately || _maxCachedTempFiles <= 0 || _disposed))
                {
                    (pathsToDelete ??= new List<string>(1)).Add(filePath);
                }
            }

            if (pathsToDelete != null)
            {
                for (int i = 0; i < pathsToDelete.Count; i++)
                {
                    MediaTempFiles.DeleteInBackground(pathsToDelete[i], DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                }
            }
        }

        /// <summary>
        /// Cancels any active session and schedules deletion of all cached temp files.
        /// </summary>
        public void ResetAndClearCache()
        {
            CancellationTokenSource? previousCts = null;
            List<string>? pathsToDelete = null;

            lock (_gate)
            {
                Interlocked.Increment(ref _sessionId);
                if (_inFlight != null)
                {
                    previousCts = _inFlight.Cts;
                    _inFlight = null;
                    Interlocked.Increment(ref _cancelledSessionCount);
                }

                if (_cachedFiles.Count > 0)
                {
                    pathsToDelete = new List<string>(_cachedFiles.Count);
                    foreach (var entry in _cachedFiles.Values)
                    {
                        if (entry.IsOwnedTempFile && !string.IsNullOrEmpty(entry.FilePath))
                        {
                            pathsToDelete.Add(entry.FilePath);
                        }
                    }
                    _cachedFiles.Clear();
                }
            }

            TryCancelQuietly(previousCts);

            if (pathsToDelete != null)
            {
                for (int i = 0; i < pathsToDelete.Count; i++)
                {
                    MediaTempFiles.DeleteInBackground(pathsToDelete[i], DefaultTempDeleteAttempts, DefaultTempDeleteRetryDelayMs);
                }
            }
        }

        private List<string>? CollectEvictedTempFilesLocked(string? protectedAssetIdKey)
        {
            if (_cachedFiles.Count <= _maxCachedTempFiles)
            {
                return null;
            }

            var candidates = new List<CachedTempEntry>(_cachedFiles.Count);
            foreach (var entry in _cachedFiles.Values)
            {
                if (entry.IsInUse)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(protectedAssetIdKey)
                    && string.Equals(entry.AssetIdKey, protectedAssetIdKey, StringComparison.Ordinal))
                {
                    continue;
                }
                candidates.Add(entry);
            }

            candidates.Sort(static (a, b) => a.LastAccessOrder.CompareTo(b.LastAccessOrder));

            List<string>? evicted = null;
            int index = 0;
            while (_cachedFiles.Count > _maxCachedTempFiles && index < candidates.Count)
            {
                var victim = candidates[index++];
                if (_cachedFiles.Remove(victim.AssetIdKey) && victim.IsOwnedTempFile && !string.IsNullOrEmpty(victim.FilePath))
                {
                    (evicted ??= new List<string>()).Add(victim.FilePath);
                }
            }

            return evicted;
        }

        private static bool IsPreparedFileValidOnDisk(string? filePath, long expectedSize)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(filePath);
                return info.Exists && info.Length > 0 && (expectedSize <= 0 || info.Length == expectedSize);
            }
            catch
            {
                return false;
            }
        }

        private static void TryCancelQuietly(CancellationTokenSource? cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResetAndClearCache();
            if (_ownsTeardownQueue)
            {
                _teardownQueue.Dispose();
            }
        }
    }

    /// <summary>
    /// Stateless helper routines for VideoClip preview: codec/container/proxy inspection, cancellable chunked stream extraction,
    /// <see cref="DecompressedBundleCache"/> resource fallback, and <see cref="FFmpegMediaPlayer"/> deadlock-free teardown.
    /// </summary>
    public static class VideoPreviewService
    {
        public const int ExtractionChunkBufferSize = 1024 * 1024; // 1 MiB chunks for fast I/O + responsive sub-2ms cancellation
        public const string TempVideoFilePrefix = "temp_video_";

        private static readonly FieldInfo? PlayerCtsField =
            typeof(FFmpegMediaPlayer).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerPlaybackThreadField =
            typeof(FFmpegMediaPlayer).GetField("_playbackThread", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerLockField =
            typeof(FFmpegMediaPlayer).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerPositionField =
            typeof(FFmpegMediaPlayer).GetField("_position", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerDurationField =
            typeof(FFmpegMediaPlayer).GetField("_duration", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerStartTimeField =
            typeof(FFmpegMediaPlayer).GetField("_startTime", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerPlaybackStartWallTimeField =
            typeof(FFmpegMediaPlayer).GetField("_playbackStartWallTime", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerTotalPauseTimeField =
            typeof(FFmpegMediaPlayer).GetField("_totalPauseTime", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlayerLoggerField =
            typeof(FFmpegMediaPlayer).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? LoggerDisposedField =
            typeof(PlayerLogger).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? LoggerEntriesField =
            typeof(PlayerLogger).GetField("_logEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? LoggerLockField =
            typeof(PlayerLogger).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? LoggerFilePathField =
            typeof(PlayerLogger).GetField("_logFilePath", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly HashSet<string> NativeFFmpegExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".webm",
            ".mp4",
            ".m4v",
            ".mov",
            ".ogv",
            ".ogg",
            ".avi",
            ".mkv",
            ".mpg",
            ".mpeg",
            ".flv",
            ".wmv",
            ".asf",
            ".ts",
            ".m2ts"
        };

        public static string GetDefaultTempDirectory()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "AssetStudio", "Temp");
            }

            return Path.Combine(Path.GetTempPath(), "AssetStudio", "Temp");
        }

        public static string SanitizeExtension(string? extension, string fallback = ".mp4")
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return fallback;
            }

            var trimmed = extension.Trim();
            if (!trimmed.StartsWith(".", StringComparison.Ordinal))
            {
                trimmed = "." + trimmed;
            }

            foreach (var ch in Path.GetInvalidFileNameChars())
            {
                if (trimmed.IndexOf(ch) >= 0)
                {
                    return fallback;
                }
            }

            return trimmed.Length > 1 ? trimmed.ToLowerInvariant() : fallback;
        }

        /// <summary>
        /// Determines whether a container/codec requires an external proxy transcode or can be played directly by FFmpeg.
        /// Standard game video containers (WEBM VP8/VP9, MP4 H.264/HEVC, MOV, OGV Theora, AVI, MKV, etc.) are natively
        /// decoded by bundled FFmpeg libavformat/libavcodec and must NEVER be re-transcoded for preview.
        /// </summary>
        public static bool ShouldUseProxyTranscode(string? extension, ReadOnlySpan<byte> headerBytes = default)
        {
            var info = InspectBytesAndExtension(headerBytes, extension);
            return info.RequiresProxyTranscode;
        }

        public static VideoCodecContainerInfo InspectVideoClip(VideoClip? videoClip, ReadOnlySpan<byte> headerBytes = default)
        {
            if (videoClip == null)
            {
                return new VideoCodecContainerInfo
                {
                    ContainerFormat = "Unknown",
                    VideoCodec = "Unknown",
                    RecommendedExtension = ".mp4",
                    IsNativelySupportedByFFmpeg = false,
                    RequiresProxyTranscode = false
                };
            }

            var rawExt = Path.GetExtension(videoClip.m_OriginalPath);
            var baseInfo = InspectBytesAndExtension(headerBytes, rawExt, videoClip.m_ExternalResources?.m_Size ?? 0L);
            double fps = videoClip.m_FrameRate > 0 && !double.IsNaN(videoClip.m_FrameRate) && !double.IsInfinity(videoClip.m_FrameRate)
                ? videoClip.m_FrameRate
                : baseInfo.FrameRate;
            ulong frames = videoClip.m_FrameCount;
            double durationSeconds = fps > 0 && frames > 0 ? frames / fps : 0.0;

            return new VideoCodecContainerInfo
            {
                ContainerFormat = baseInfo.ContainerFormat,
                VideoCodec = baseInfo.VideoCodec,
                RecommendedExtension = baseInfo.RecommendedExtension,
                Width = (int)videoClip.m_Width,
                Height = (int)videoClip.m_Height,
                ProxyWidth = (int)videoClip.m_ProxyWidth,
                ProxyHeight = (int)videoClip.m_ProxyHeight,
                FrameRate = fps,
                FrameCount = frames,
                EstimatedDurationSeconds = durationSeconds,
                ByteSize = videoClip.m_ExternalResources?.m_Size ?? 0L,
                HasAudio = videoClip.HasAudio,
                IsNativelySupportedByFFmpeg = baseInfo.IsNativelySupportedByFFmpeg,
                RequiresProxyTranscode = baseInfo.RequiresProxyTranscode
            };
        }

        public static VideoCodecContainerInfo InspectBytesAndExtension(
            ReadOnlySpan<byte> headerBytes,
            string? fallbackExtension,
            long byteSize = 0)
        {
            var ext = SanitizeExtension(fallbackExtension, ".mp4");

            if (!headerBytes.IsEmpty && headerBytes.Length >= 4)
            {
                var sniffed = MovieTexturePreviewService.InspectBytes(headerBytes, "VideoProbe");
                if (sniffed.HasMovieData && !string.Equals(sniffed.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    var sniffedExt = SanitizeExtension(sniffed.RecommendedExtension, ext);
                    bool nativeSupported = NativeFFmpegExtensions.Contains(sniffedExt);
                    return new VideoCodecContainerInfo
                    {
                        ContainerFormat = sniffed.DetectedFormat,
                        VideoCodec = !string.IsNullOrWhiteSpace(sniffed.VideoCodec) ? sniffed.VideoCodec : InferCodecFromExtension(sniffedExt),
                        RecommendedExtension = sniffedExt,
                        Width = sniffed.Width,
                        Height = sniffed.Height,
                        FrameRate = sniffed.FrameRate,
                        ByteSize = byteSize > 0 ? byteSize : headerBytes.Length,
                        HasAudio = sniffed.HasEmbeddedAudio,
                        IsNativelySupportedByFFmpeg = nativeSupported,
                        RequiresProxyTranscode = false
                    };
                }
            }

            var container = InferContainerFromExtension(ext);
            var codec = InferCodecFromExtension(ext);
            bool isNative = NativeFFmpegExtensions.Contains(ext);

            return new VideoCodecContainerInfo
            {
                ContainerFormat = container,
                VideoCodec = codec,
                RecommendedExtension = ext,
                ByteSize = Math.Max(0L, byteSize),
                IsNativelySupportedByFFmpeg = isNative,
                RequiresProxyTranscode = false
            };
        }

        private static string InferContainerFromExtension(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".webm" => "WebM",
                ".mp4" or ".m4v" => "MP4",
                ".mov" => "QuickTime MOV",
                ".ogv" or ".ogg" => "Ogg Theora",
                ".avi" => "AVI",
                ".mkv" => "Matroska (MKV)",
                ".mpg" or ".mpeg" => "MPEG",
                ".flv" => "FLV",
                ".wmv" or ".asf" => "WMV/ASF",
                _ => ext.TrimStart('.').ToUpperInvariant()
            };
        }

        private static string InferCodecFromExtension(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".webm" => "VP8/VP9",
                ".mp4" or ".m4v" or ".mov" => "H.264/HEVC",
                ".ogv" or ".ogg" => "Theora",
                ".avi" => "AVI Video",
                ".mkv" => "Matroska Video",
                ".mpg" or ".mpeg" => "MPEG-1/2",
                ".flv" => "FLV Video",
                ".wmv" or ".asf" => "WMV",
                _ => "Native"
            };
        }

        /// <summary>
        /// Neutralizes <see cref="FFmpegMediaPlayer"/>'s internal <see cref="PlayerLogger"/> so it never allocates JSON
        /// strings on every audio packet or blocks <c>lock (_lock)</c> flushing multi-megabyte <c>debug/player-log-*.json</c> files.
        /// </summary>
        public static void DisablePlayerDiskLogger(FFmpegMediaPlayer? player)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                var loggerObj = PlayerLoggerField?.GetValue(player);
                if (loggerObj == null)
                {
                    return;
                }

                var lockObj = LoggerLockField?.GetValue(loggerObj);
                var logFilePath = LoggerFilePathField?.GetValue(loggerObj) as string;

                if (lockObj != null)
                {
                    lock (lockObj)
                    {
                        LoggerDisposedField?.SetValue(loggerObj, true);
                        if (LoggerEntriesField?.GetValue(loggerObj) is IList list)
                        {
                            list.Clear();
                        }
                    }
                }
                else
                {
                    LoggerDisposedField?.SetValue(loggerObj, true);
                    if (LoggerEntriesField?.GetValue(loggerObj) is IList list)
                    {
                        list.Clear();
                    }
                }

                if (!string.IsNullOrEmpty(logFilePath) && File.Exists(logFilePath))
                {
                    MediaTempFiles.TryDeleteNow(logFilePath);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Cancels <see cref="FFmpegMediaPlayer"/>'s internal playback loop token and joins <c>_playbackThread</c>
        /// OUTSIDE <c>player._lock</c> so <c>PlaybackLoop</c> can acquire <c>player._lock</c> and exit in &lt;5ms
        /// instead of hitting the 1000ms self-deadlock inside <c>FFmpegMediaPlayer.Stop()</c>.
        /// </summary>
        public static bool SignalAndJoinPlaybackThread(
            FFmpegMediaPlayer? player,
            int joinTimeoutMs = VideoPlayerTeardownQueue.DefaultPlaybackThreadJoinTimeoutMs)
        {
            if (player == null)
            {
                return true;
            }

            try
            {
                var cts = PlayerCtsField?.GetValue(player) as CancellationTokenSource;
                if (cts != null)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                }

                try
                {
                    player.AudioPlayer?.Stop();
                }
                catch
                {
                }

                var playbackThread = PlayerPlaybackThreadField?.GetValue(player) as Thread;
                if (playbackThread != null
                    && playbackThread.IsAlive
                    && !ReferenceEquals(Thread.CurrentThread, playbackThread))
                {
                    return playbackThread.Join(Math.Max(10, joinTimeoutMs));
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads <see cref="FFmpegMediaPlayer"/>'s exact double-precision <c>_position</c> in milliseconds.
        /// Unlike <c>player.Position * player.Length</c>, this avoids float truncation and still reports accurate
        /// timestamps when a container omits duration metadata (<c>player.Length == 0</c>).
        /// </summary>
        public static double TryGetPlayerPositionMs(FFmpegMediaPlayer? player)
        {
            if (player == null)
            {
                return 0.0;
            }

            try
            {
                if (PlayerPositionField?.GetValue(player) is double posSec
                    && !double.IsNaN(posSec)
                    && !double.IsInfinity(posSec)
                    && posSec >= 0.0)
                {
                    return posSec * 1000.0;
                }
            }
            catch
            {
            }

            return player.Length > 0 ? Math.Max(0.0, player.Position * (double)player.Length) : 0.0;
        }

        /// <summary>
        /// Seeks <see cref="FFmpegMediaPlayer"/> and guards against its small-seek audio resync bug:
        /// <c>Seek()</c> zeroes <c>_startTime</c> and <c>_playbackStartWallTime</c> expecting <c>ProcessVideoPacket</c>
        /// to observe <c>_needsResync == true</c>, but <c>ProcessAudioPacket</c> can clear <c>_needsResync</c> first;
        /// when the seek delta is &lt;= 1s, <c>ProcessVideoPacket</c> then skips its discontinuity reset and paces
        /// against a zeroed baseline (causing runaway fast-forward). Shifting <c>_startTime</c> by the seek delta while
        /// keeping <c>_needsResync = true</c> guarantees exact wall-clock pacing regardless of packet arrival order.
        /// </summary>
        public static void SafeSeekPlayer(FFmpegMediaPlayer? player, float positionPercent)
        {
            if (player == null)
            {
                return;
            }

            var clamped = Math.Clamp(positionPercent, 0f, 1f);
            var playerLock = PlayerLockField?.GetValue(player);
            if (playerLock == null)
            {
                player.Seek(clamped);
                return;
            }

            lock (playerLock)
            {
                bool wasPlaying = player.IsPlaying;
                double oldPos = PlayerPositionField?.GetValue(player) is double p ? p : 0.0;
                double duration = PlayerDurationField?.GetValue(player) is double d ? d : 0.0;
                double oldStart = PlayerStartTimeField?.GetValue(player) is double s ? s : 0.0;
                double oldWall = PlayerPlaybackStartWallTimeField?.GetValue(player) is double w ? w : 0.0;
                double oldPause = PlayerTotalPauseTimeField?.GetValue(player) is double pt ? pt : 0.0;

                player.Seek(clamped);

                if (wasPlaying && duration > 0.0 && oldWall > 0.0)
                {
                    double targetSec = duration * clamped;
                    double delta = targetSec - oldPos;
                    if (Math.Abs(delta) <= 1.25)
                    {
                        try
                        {
                            PlayerStartTimeField?.SetValue(player, oldStart + delta);
                            PlayerPlaybackStartWallTimeField?.SetValue(player, oldWall);
                            PlayerTotalPauseTimeField?.SetValue(player, oldPause);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        public static void SafeStopPlayer(
            FFmpegMediaPlayer? player,
            int joinTimeoutMs = VideoPlayerTeardownQueue.DefaultPlaybackThreadJoinTimeoutMs)
        {
            if (player == null)
            {
                return;
            }

            SignalAndJoinPlaybackThread(player, joinTimeoutMs);
            try
            {
                player.Stop();
            }
            catch
            {
            }
        }

        public static void SafeDisposePlayer(
            FFmpegMediaPlayer? player,
            int joinTimeoutMs = VideoPlayerTeardownQueue.DefaultPlaybackThreadJoinTimeoutMs)
        {
            if (player == null)
            {
                return;
            }

            DisablePlayerDiskLogger(player);
            SignalAndJoinPlaybackThread(player, joinTimeoutMs);
            try
            {
                player.Stop();
            }
            catch
            {
            }
            try
            {
                player.Close();
            }
            catch
            {
            }
            try
            {
                player.Dispose();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Prepares a playable file on disk for a <see cref="VideoClip"/> or raw byte array with per-chunk cancellation.
        /// </summary>
        public static VideoPreviewPreparedFile? PrepareVideoFile(
            string assetIdKey,
            string exportBaseName,
            long pathId,
            string extension,
            VideoClip? videoClip,
            byte[]? rawVideoBytes,
            string tempDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = SanitizeExtension(extension, ".mp4");
            if (videoClip != null)
            {
                var extRes = videoClip.m_ExternalResources;
                if (extRes == null || extRes.m_Size <= 0 || videoClip.m_VideoData == null)
                {
                    return null;
                }

                long expectedSize = extRes.m_Size;

                // 1) Try zero-copy direct file path if the video occupies an entire uncompressed standalone file on disk at offset 0.
                if (TryResolveDirectStandaloneVideoFile(videoClip, expectedSize, out var directPath))
                {
                    return new VideoPreviewPreparedFile(assetIdKey, directPath, expectedSize, isOwnedTempFile: false);
                }

                // 2) Stream-extract with 1 MiB chunks and responsive cancellation.
                var safeName = SanitizeFileNamePart(exportBaseName);
                var prefix = $"{TempVideoFilePrefix}{safeName}_{pathId.ToString(CultureInfo.InvariantCulture)}_";
                var tempPath = MediaTempFiles.CreateUniquePath(tempDirectory, prefix, ext);

                try
                {
                    ExtractVideoClipDataToFile(videoClip, tempPath, expectedSize, cancellationToken);
                }
                catch
                {
                    MediaTempFiles.TryDeleteNow(tempPath);
                    throw;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    MediaTempFiles.TryDeleteNow(tempPath);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length <= 0)
                {
                    MediaTempFiles.TryDeleteNow(tempPath);
                    return null;
                }

                return new VideoPreviewPreparedFile(assetIdKey, tempPath, info.Length, isOwnedTempFile: true);
            }

            if (rawVideoBytes == null || rawVideoBytes.Length == 0)
            {
                return null;
            }

            var rawSafeName = SanitizeFileNamePart(exportBaseName);
            var rawPrefix = $"{TempVideoFilePrefix}{rawSafeName}_{pathId.ToString(CultureInfo.InvariantCulture)}_";
            var rawTempPath = MediaTempFiles.CreateUniquePath(tempDirectory, rawPrefix, ext);
            try
            {
                WriteBytesWithCancellation(rawTempPath, rawVideoBytes, cancellationToken);
            }
            catch
            {
                MediaTempFiles.TryDeleteNow(rawTempPath);
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                MediaTempFiles.TryDeleteNow(rawTempPath);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var rawInfo = new FileInfo(rawTempPath);
            if (!rawInfo.Exists || rawInfo.Length <= 0)
            {
                MediaTempFiles.TryDeleteNow(rawTempPath);
                return null;
            }

            return new VideoPreviewPreparedFile(assetIdKey, rawTempPath, rawInfo.Length, isOwnedTempFile: true);
        }

        public static void ExtractVideoClipDataToFile(
            VideoClip videoClip,
            string destinationPath,
            long expectedSize,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(videoClip);
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("Destination path must not be empty.", nameof(destinationPath));
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Primary path: open a thread-safe ReadSession on m_VideoData and copy in 1 MiB chunks with cancellation checks.
            try
            {
                using var session = videoClip.m_VideoData.OpenReadSession();
                CopyReadSessionToFile(session, destinationPath, expectedSize > 0 ? expectedSize : session.Size, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is FileNotFoundException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                // Fallback: if background connection building (ClearLoadedFilesKeepIndex) disposed resourceFileReaders
                // or loaded the SerializedFile with serializedFilesOnly=true, recover the .resource node directly from
                // DecompressedBundleCache or reload the bundle for preview.
                if (TryExtractFromDecompressedBundleCache(videoClip, destinationPath, expectedSize, cancellationToken))
                {
                    return;
                }

                throw;
            }
        }

        public static void CopyReadSessionToFile(
            ResourceReader.ReadSession session,
            string destinationPath,
            long totalSize,
            CancellationToken cancellationToken,
            int chunkBufferSize = ExtractionChunkBufferSize)
        {
            ArgumentNullException.ThrowIfNull(session);
            cancellationToken.ThrowIfCancellationRequested();

            long bytesToCopy = Math.Min(totalSize, session.Size);
            if (bytesToCopy <= 0)
            {
                throw new InvalidDataException("Video resource stream is empty.");
            }

            int bufferSize = (int)Math.Min(Math.Max(16 * 1024, chunkBufferSize), bytesToCopy);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                using var fs = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize,
                    FileOptions.SequentialScan);

                try
                {
                    fs.SetLength(bytesToCopy);
                }
                catch
                {
                }

                long offset = 0;
                while (offset < bytesToCopy)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(bufferSize, bytesToCopy - offset);
                    int read = session.ReadSegment(offset, buffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of video resource at offset {offset} of {bytesToCopy}.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    fs.Write(buffer, 0, read);
                    offset += read;
                }

                fs.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static void ExtractStreamWithCancellation(
            Stream source,
            long sourceOffset,
            long byteCount,
            string destinationPath,
            CancellationToken cancellationToken,
            int chunkBufferSize = ExtractionChunkBufferSize)
        {
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();

            if (byteCount <= 0)
            {
                throw new InvalidDataException("Video stream size must be positive.");
            }

            int bufferSize = (int)Math.Min(Math.Max(16 * 1024, chunkBufferSize), byteCount);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                if (source.CanSeek && source.Position != sourceOffset)
                {
                    source.Position = sourceOffset;
                }

                using var fs = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize,
                    FileOptions.SequentialScan);

                try
                {
                    fs.SetLength(byteCount);
                }
                catch
                {
                }

                long remaining = byteCount;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(bufferSize, remaining);
                    int read = source.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of video stream with {remaining} bytes remaining.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    fs.Write(buffer, 0, read);
                    remaining -= read;
                }

                fs.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static void WriteBytesWithCancellation(
            string destinationPath,
            byte[] bytes,
            CancellationToken cancellationToken,
            int chunkBufferSize = ExtractionChunkBufferSize)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            cancellationToken.ThrowIfCancellationRequested();

            int chunkSize = Math.Min(Math.Max(16 * 1024, chunkBufferSize), Math.Max(16 * 1024, bytes.Length));
            using var fs = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                chunkSize,
                FileOptions.SequentialScan);

            try
            {
                fs.SetLength(bytes.Length);
            }
            catch
            {
            }

            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(chunkSize, bytes.Length - offset);
                fs.Write(bytes, offset, count);
                offset += count;
            }

            fs.Flush();
        }

        private static bool TryResolveDirectStandaloneVideoFile(
            VideoClip videoClip,
            long expectedSize,
            out string directFilePath)
        {
            directFilePath = string.Empty;
            if (ResourceReader.EnableXorInversionHeuristic || expectedSize <= 0)
            {
                return false;
            }

            var extRes = videoClip.m_ExternalResources;
            if (extRes == null
                || extRes.m_Offset != 0
                || string.IsNullOrWhiteSpace(extRes.m_Source)
                || extRes.m_Source.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var assetsFile = videoClip.assetsFile;
            if (assetsFile == null || assetsFile.reader?.AppliedDecryptionProvider != null)
            {
                return false;
            }

            try
            {
                var normalizedSource = NormalizeArchiveResourcePath(extRes.m_Source);
                var assetsDir = Path.GetDirectoryName(assetsFile.fullName);
                if (!string.IsNullOrEmpty(assetsDir))
                {
                    var candidatePath = Path.Combine(assetsDir, Path.GetFileName(normalizedSource));
                    var info = new FileInfo(candidatePath);
                    if (info.Exists && info.Length == expectedSize)
                    {
                        directFilePath = info.FullName;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryExtractFromDecompressedBundleCache(
            VideoClip videoClip,
            string destinationPath,
            long expectedSize,
            CancellationToken cancellationToken)
        {
            var assetsFile = videoClip.assetsFile;
            var assetsManager = assetsFile?.assetsManager;
            var extRes = videoClip.m_ExternalResources;
            if (assetsFile == null || assetsManager == null || extRes == null || string.IsNullOrWhiteSpace(extRes.m_Source))
            {
                return false;
            }

            var bundlePath = !string.IsNullOrWhiteSpace(assetsFile.originalPath) && File.Exists(assetsFile.originalPath)
                ? assetsFile.originalPath
                : (!string.IsNullOrWhiteSpace(assetsFile.fullName) && File.Exists(assetsFile.fullName) ? assetsFile.fullName : null);
            if (string.IsNullOrEmpty(bundlePath))
            {
                return false;
            }

            var normalizedResource = NormalizeArchiveResourcePath(extRes.m_Source);
            var resourceFileName = Path.GetFileName(normalizedResource);

            // 1) Try reading directly from DecompressedBundleCache without reloading the SerializedFile.
            if (assetsManager.DecompressionCache != null
                && assetsManager.DecompressionCache.TryOpenCachedStreamFiles(bundlePath, out var streamFiles))
            {
                try
                {
                    StreamFile? matched = null;
                    for (int i = 0; i < streamFiles.Length; i++)
                    {
                        var candidate = streamFiles[i];
                        if (string.Equals(candidate.fileName, resourceFileName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidate.path, normalizedResource, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = candidate;
                            break;
                        }
                    }

                    if (matched?.stream != null)
                    {
                        long copySize = expectedSize > 0 ? expectedSize : (matched.stream.Length - extRes.m_Offset);
                        if (extRes.m_Offset >= 0 && copySize > 0 && extRes.m_Offset + copySize <= matched.stream.Length)
                        {
                            ExtractStreamWithCancellation(matched.stream, extRes.m_Offset, copySize, destinationPath, cancellationToken);
                            return true;
                        }
                    }
                }
                finally
                {
                    for (int i = 0; i < streamFiles.Length; i++)
                    {
                        try { streamFiles[i]?.stream?.Dispose(); } catch { }
                    }
                }
            }

            // 2) If the bundle was loaded with serializedFilesOnly=true and skipDecompressionCache=true,
            // reload the bundle for preview so resourceFileReaders is populated, then extract with cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            assetsManager.LoadFilesForPreview(bundlePath);
            cancellationToken.ThrowIfCancellationRequested();

            var refreshedReader = new ResourceReader(extRes.m_Source, assetsFile, extRes.m_Offset, extRes.m_Size);
            videoClip.m_VideoData = refreshedReader;
            using var session = refreshedReader.OpenReadSession();
            CopyReadSessionToFile(session, destinationPath, expectedSize > 0 ? expectedSize : session.Size, cancellationToken);
            return true;
        }

        private static string NormalizeArchiveResourcePath(string path)
        {
            const string archivePrefix = "archive:/";
            if (path.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(archivePrefix.Length);
                int slash = path.IndexOf('/');
                if (slash >= 0)
                {
                    path = path.Substring(slash + 1);
                }
            }

            return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private static string SanitizeFileNamePart(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "video";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        /// <summary>
        /// Best-effort background cleanup of stale orphaned <c>temp_video_*</c> files left behind in the temp folder
        /// from older sessions.
        /// </summary>
        public static Task CleanupStaleTempVideoFilesInBackground(string? tempDirectory = null, TimeSpan? minAge = null)
        {
            var dir = !string.IsNullOrWhiteSpace(tempDirectory) ? tempDirectory : GetDefaultTempDirectory();
            var threshold = minAge ?? TimeSpan.FromMinutes(15);

            return Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        return;
                    }

                    var cutoffUtc = DateTime.UtcNow - threshold;
                    foreach (var file in Directory.EnumerateFiles(dir, TempVideoFilePrefix + "*"))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Exists && info.LastWriteTimeUtc < cutoffUtc)
                            {
                                info.Delete();
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            });
        }
    }
}
