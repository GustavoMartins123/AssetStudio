using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AssetStudio.Avalonia.Services
{
    /// <summary>
    /// Immutable snapshot of a frame-based preview timeline returned by <see cref="FrameTimelineClock"/>.
    /// </summary>
    public readonly record struct FrameTimelineSnapshot(
        int CurrentFrame,
        double CurrentTimeSeconds,
        bool IsCompleted,
        bool IsPlaying,
        long SeekGeneration);

    /// <summary>
    /// Unified, thread-safe, UI-framework-free millisecond timeline tracker for time-based media (Audio &amp; Video).
    /// Supports both:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>Presented-timestamp locking</b> (Video): keeps displayed seconds locked to the frame actually painted
    ///       on screen, supports both <c>PositionChanged -&gt; FrameReady</c> (active playback) and
    ///       <c>FrameReady -&gt; PositionChanged</c> (paused single-frame stepping), and works on zero-duration containers.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Monotonic Stopwatch progression &amp; polled seek barriers</b> (Audio): tracks monotonic elapsed time
    ///       with exact pause/resume offset preservation, strict <c>[0, DurationMs]</c> clamping, and generation-guarded
    ///       seek barriers so async seeks or backend restarts never snap sliders or readouts backward.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public sealed class MediaTimelineTracker
    {
        public const long DefaultSeekBarrierTimeoutMs = 1200;
        public const long DefaultSeekConvergenceToleranceMs = 300;

        private readonly object _gate = new();
        private readonly Func<long> _tickProvider;
        private readonly double _ticksToMs;
        private readonly long _seekBarrierTimeoutMs;

        private long _durationMs;
        private long _presentedPositionMs;
        private bool _hasPresentedPosition;
        private long _stagedPlaybackPositionMs = -1L;

        private long _activeSeekGeneration;
        private long _completedSeekGeneration;
        private long _pendingSeekTargetMs = -1L;
        private long _pendingSeekBarrierTickMs;

        // Monotonic Stopwatch-backed progression state (used when backend does not push per-frame timestamps).
        private long _clockBaseMs;
        private long _clockStartTicks;
        private bool _isClockRunning;

        public MediaTimelineTracker(
            Func<long>? tickProvider = null,
            long tickFrequency = 0,
            long seekBarrierTimeoutMs = DefaultSeekBarrierTimeoutMs)
        {
            _tickProvider = tickProvider ?? Stopwatch.GetTimestamp;
            var freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _ticksToMs = 1000.0 / Math.Max(1L, freq);
            _seekBarrierTimeoutMs = seekBarrierTimeoutMs > 0 ? seekBarrierTimeoutMs : DefaultSeekBarrierTimeoutMs;
        }

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
                    return _pendingSeekTargetMs >= 0L;
                }
            }
        }

        public long ActiveSeekGeneration
        {
            get
            {
                lock (_gate)
                {
                    return _activeSeekGeneration;
                }
            }
        }

        public bool IsClockRunning
        {
            get
            {
                lock (_gate)
                {
                    return _isClockRunning;
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
                _clockBaseMs = 0L;
                _clockStartTicks = 0L;
                _isClockRunning = false;
            }
        }

        public void SetDurationMs(long durationMs)
        {
            if (durationMs <= 0L)
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
                if (_clockBaseMs > _durationMs)
                {
                    _clockBaseMs = _durationMs;
                }
            }
        }

        public void OnMediaOpened(long durationMs)
        {
            lock (_gate)
            {
                if (durationMs > 0L)
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
                _clockBaseMs = 0L;
                _clockStartTicks = 0L;
                _isClockRunning = false;
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
        /// Cancels a pending seek if <paramref name="seekGeneration"/> is still the active seek (e.g. aborted drag).
        /// </summary>
        public void CancelSeek(long seekGeneration)
        {
            lock (_gate)
            {
                if (seekGeneration != _activeSeekGeneration)
                {
                    return;
                }

                _completedSeekGeneration = _activeSeekGeneration;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
            }
        }

        /// <summary>
        /// Checks whether a backend whose seek barrier has already been reached has converged onto the seek target.
        /// If so, commits the reported position and clears the pending seek immediately.
        /// </summary>
        public bool TryCommitReachedSeek(
            double reportedPositionMs,
            long convergenceToleranceMs = DefaultSeekConvergenceToleranceMs)
        {
            lock (_gate)
            {
                if (_pendingSeekTargetMs < 0L || _activeSeekGeneration != _completedSeekGeneration)
                {
                    return false;
                }

                var normalized = NormalizeMsLocked(reportedPositionMs);
                var tolerance = Math.Max(0L, convergenceToleranceMs);
                if (Math.Abs(normalized - _pendingSeekTargetMs) <= tolerance)
                {
                    _presentedPositionMs = normalized;
                    _hasPresentedPosition = true;
                    _pendingSeekTargetMs = -1L;
                    _pendingSeekBarrierTickMs = 0L;
                    return true;
                }

                return false;
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
                if (_stagedPlaybackPositionMs >= 0L)
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

        /// <summary>
        /// Resolves a polled backend position (e.g. WinMM or generic <c>IAudioPreviewBackend</c>) through the
        /// generation-guarded seek barrier and <c>[0, DurationMs]</c> clamping.
        /// While a seek is in-flight or the backend still reports a stale pre-seek timestamp before the barrier
        /// timeout elapses, holds the seek target so sliders and readouts never snap backward.
        /// </summary>
        public long ResolvePolledPositionMs(
            double backendReportedPositionMs,
            long nowTickMs,
            long convergenceToleranceMs = DefaultSeekConvergenceToleranceMs)
        {
            lock (_gate)
            {
                var normalized = NormalizeMsLocked(backendReportedPositionMs);
                if (_pendingSeekTargetMs >= 0L)
                {
                    if (_activeSeekGeneration != _completedSeekGeneration)
                    {
                        return _pendingSeekTargetMs;
                    }

                    var tolerance = Math.Max(0L, convergenceToleranceMs);
                    if (Math.Abs(normalized - _pendingSeekTargetMs) <= tolerance)
                    {
                        _presentedPositionMs = normalized;
                        _hasPresentedPosition = true;
                        _pendingSeekTargetMs = -1L;
                        _pendingSeekBarrierTickMs = 0L;
                        return _presentedPositionMs;
                    }

                    if (_pendingSeekBarrierTickMs == 0L
                        || (nowTickMs - _pendingSeekBarrierTickMs) < _seekBarrierTimeoutMs)
                    {
                        return _pendingSeekTargetMs;
                    }

                    // Barrier timeout elapsed; accept the clamped backend position and release the barrier.
                    _presentedPositionMs = normalized;
                    _hasPresentedPosition = true;
                    _pendingSeekTargetMs = -1L;
                    _pendingSeekBarrierTickMs = 0L;
                    return _presentedPositionMs;
                }

                _presentedPositionMs = normalized;
                _hasPresentedPosition = true;
                return _presentedPositionMs;
            }
        }

        #region Monotonic Stopwatch Progression Mode (Audio Backends)

        public long StartClock(long? baseMs = null, long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                var startMs = baseMs.HasValue
                    ? ClampPositionLocked(baseMs.Value)
                    : GetClockPositionLocked(ticks);

                _clockBaseMs = startMs;
                _clockStartTicks = ticks;
                _isClockRunning = true;
                _presentedPositionMs = startMs;
                _hasPresentedPosition = true;
                return startMs;
            }
        }

        public long PauseClock(long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                var captured = GetClockPositionLocked(ticks);
                _clockBaseMs = captured;
                _clockStartTicks = ticks;
                _isClockRunning = false;
                _presentedPositionMs = captured;
                _hasPresentedPosition = true;
                return captured;
            }
        }

        public long ResumeClock(long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                if (_isClockRunning)
                {
                    return GetClockPositionLocked(ticks);
                }

                _clockStartTicks = ticks;
                _isClockRunning = true;
                return _clockBaseMs;
            }
        }

        public void StopClock()
        {
            lock (_gate)
            {
                _isClockRunning = false;
                _clockBaseMs = 0L;
                _clockStartTicks = 0L;
                _presentedPositionMs = 0L;
                _hasPresentedPosition = true;
                _stagedPlaybackPositionMs = -1L;
                _activeSeekGeneration++;
                _completedSeekGeneration = _activeSeekGeneration;
                _pendingSeekTargetMs = -1L;
                _pendingSeekBarrierTickMs = 0L;
            }
        }

        public long SeekClock(long targetMs, long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                var clamped = ClampPositionLocked(targetMs);
                _clockBaseMs = clamped;
                _clockStartTicks = ticks;
                _presentedPositionMs = clamped;
                _hasPresentedPosition = true;
                return clamped;
            }
        }

        public long GetClockPositionMs(long? nowTicks = null)
        {
            lock (_gate)
            {
                if (_pendingSeekTargetMs >= 0L && _activeSeekGeneration != _completedSeekGeneration)
                {
                    return _pendingSeekTargetMs;
                }

                var ticks = nowTicks ?? _tickProvider();
                return GetClockPositionLocked(ticks);
            }
        }

        private long GetClockPositionLocked(long nowTicks)
        {
            if (!_isClockRunning)
            {
                return ClampPositionLocked(_clockBaseMs);
            }

            var deltaTicks = nowTicks - _clockStartTicks;
            if (deltaTicks <= 0L)
            {
                return ClampPositionLocked(_clockBaseMs);
            }

            var elapsedMs = (long)Math.Round(deltaTicks * _ticksToMs);
            if (elapsedMs < 0L)
            {
                elapsedMs = 0L;
            }

            long total;
            try
            {
                total = checked(_clockBaseMs + elapsedMs);
            }
            catch (OverflowException)
            {
                total = long.MaxValue;
            }

            return ClampPositionLocked(total);
        }

        #endregion

        private long GetEffectivePositionLocked(double fallbackPlayerPositionMs, long nowTickMs, bool allowBarrierTimeout)
        {
            if (_pendingSeekTargetMs >= 0L)
            {
                if (_activeSeekGeneration != _completedSeekGeneration)
                {
                    return _pendingSeekTargetMs;
                }

                if (!allowBarrierTimeout
                    || _pendingSeekBarrierTickMs == 0L
                    || (nowTickMs - _pendingSeekBarrierTickMs) < _seekBarrierTimeoutMs)
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
    /// Unified, thread-safe, monotonic <see cref="Stopwatch"/>-backed real-time frame and seconds counter for
    /// frame-based previews (3D animations, 2D sprite/texture animations, and Material/VFX/Shader frame loops).
    /// Immune to <c>DispatcherTimer</c> jitter, heavy GPU/UI frame drops, and <see cref="DateTime.UtcNow"/> clock jumps.
    /// </summary>
    public sealed class FrameTimelineClock
    {
        public const double DefaultFps = 30.0;
        public const double MinPlaybackSpeed = 0.05;
        public const double MaxPlaybackSpeed = 16.0;
        public const double DefaultPlaybackSpeed = 1.0;

        private const double FrameEpsilon = 1e-9;

        private readonly object _gate = new();
        private readonly Func<long> _tickProvider;
        private readonly double _ticksToSeconds;

        private int _totalFrames;
        private double _fps = DefaultFps;
        private double _durationSeconds;
        private bool _isLooping = true;
        private double _playbackSpeed = DefaultPlaybackSpeed;

        private bool _isPlaying;
        private bool _isCompleted;
        private double _elapsedSeconds;
        private int _currentFrame;
        private long _anchorTicks;
        private long _seekGeneration;

        public FrameTimelineClock(Func<long>? tickProvider = null, long tickFrequency = 0)
        {
            _tickProvider = tickProvider ?? Stopwatch.GetTimestamp;
            var freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _ticksToSeconds = 1.0 / Math.Max(1L, freq);
        }

        public int TotalFrames
        {
            get { lock (_gate) { return _totalFrames; } }
        }

        public double Fps
        {
            get { lock (_gate) { return _fps; } }
        }

        public double DurationSeconds
        {
            get { lock (_gate) { return _durationSeconds; } }
        }

        public bool IsLooping
        {
            get { lock (_gate) { return _isLooping; } }
        }

        public double PlaybackSpeed
        {
            get { lock (_gate) { return _playbackSpeed; } }
        }

        public bool IsPlaying
        {
            get { lock (_gate) { return _isPlaying; } }
        }

        public bool IsCompleted
        {
            get { lock (_gate) { return _isCompleted; } }
        }

        public int CurrentFrame
        {
            get { lock (_gate) { return _currentFrame; } }
        }

        public double CurrentTimeSeconds
        {
            get { lock (_gate) { return _elapsedSeconds; } }
        }

        public long SeekGeneration
        {
            get { lock (_gate) { return _seekGeneration; } }
        }

        public void Configure(
            int totalFrames,
            double fps,
            double durationSeconds = 0.0,
            bool isLooping = true,
            double playbackSpeed = DefaultPlaybackSpeed)
        {
            lock (_gate)
            {
                _totalFrames = Math.Max(0, totalFrames);
                _fps = double.IsFinite(fps) && fps > 0.0 ? fps : DefaultFps;
                _durationSeconds = double.IsFinite(durationSeconds) && durationSeconds > 0.0
                    ? durationSeconds
                    : (_totalFrames > 0 ? _totalFrames / _fps : 0.0);
                _isLooping = isLooping;
                _playbackSpeed = SanitizeSpeed(playbackSpeed);
                _isPlaying = false;
                _isCompleted = false;
                _elapsedSeconds = 0.0;
                _currentFrame = 0;
                _anchorTicks = 0L;
                _seekGeneration++;
            }
        }

        public void SetLooping(bool isLooping)
        {
            lock (_gate)
            {
                _isLooping = isLooping;
                if (isLooping && _isCompleted)
                {
                    _isCompleted = false;
                }
            }
        }

        public void SetPlaybackSpeed(double playbackSpeed, long? nowTicks = null)
        {
            lock (_gate)
            {
                if (_isPlaying)
                {
                    AdvanceLocked(nowTicks ?? _tickProvider());
                }

                _playbackSpeed = SanitizeSpeed(playbackSpeed);
            }
        }

        public FrameTimelineSnapshot Play(long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                if (_totalFrames <= 0 && _durationSeconds <= 0.0)
                {
                    _isPlaying = false;
                    return BuildSnapshotLocked();
                }

                if (_isPlaying)
                {
                    return AdvanceLocked(ticks);
                }

                if (!_isLooping && _isCompleted)
                {
                    _elapsedSeconds = 0.0;
                    _currentFrame = 0;
                    _isCompleted = false;
                    _seekGeneration++;
                }

                _anchorTicks = ticks;
                _isPlaying = true;
                return BuildSnapshotLocked();
            }
        }

        public FrameTimelineSnapshot Pause(long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                if (_isPlaying)
                {
                    AdvanceLocked(ticks);
                    _isPlaying = false;
                }

                return BuildSnapshotLocked();
            }
        }

        public FrameTimelineSnapshot Resume(long? nowTicks = null)
        {
            return Play(nowTicks);
        }

        public FrameTimelineSnapshot Restart(long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                _elapsedSeconds = 0.0;
                _currentFrame = 0;
                _isCompleted = false;
                _seekGeneration++;
                _anchorTicks = ticks;
                _isPlaying = _totalFrames > 0 || _durationSeconds > 0.0;
                return BuildSnapshotLocked();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _isPlaying = false;
                _isCompleted = false;
                _elapsedSeconds = 0.0;
                _currentFrame = 0;
                _anchorTicks = 0L;
                _seekGeneration++;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _totalFrames = 0;
                _fps = DefaultFps;
                _durationSeconds = 0.0;
                _isLooping = true;
                _playbackSpeed = DefaultPlaybackSpeed;
                _isPlaying = false;
                _isCompleted = false;
                _elapsedSeconds = 0.0;
                _currentFrame = 0;
                _anchorTicks = 0L;
                _seekGeneration++;
            }
        }

        public FrameTimelineSnapshot SeekFrame(int frame, long? nowTicks = null)
        {
            lock (_gate)
            {
                return SeekFrameLocked(frame, nowTicks ?? _tickProvider());
            }
        }

        public FrameTimelineSnapshot SeekTime(double seconds, long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                double target;
                if (double.IsNaN(seconds) || double.IsNegativeInfinity(seconds) || seconds <= 0.0)
                {
                    target = 0.0;
                }
                else if (double.IsPositiveInfinity(seconds))
                {
                    target = _durationSeconds;
                }
                else if (_durationSeconds > 0.0)
                {
                    target = Math.Clamp(seconds, 0.0, _durationSeconds);
                }
                else
                {
                    target = seconds;
                }

                _elapsedSeconds = target;
                _isCompleted = !_isLooping && _durationSeconds > 0.0 && _elapsedSeconds >= _durationSeconds;
                if (_isCompleted)
                {
                    _isPlaying = false;
                }
                _currentFrame = ComputeFrameFromTimeLocked(_elapsedSeconds, _isCompleted);
                _anchorTicks = ticks;
                _seekGeneration++;
                return BuildSnapshotLocked();
            }
        }

        public FrameTimelineSnapshot StepFrame(int deltaFrames, long? nowTicks = null)
        {
            lock (_gate)
            {
                var ticks = nowTicks ?? _tickProvider();
                if (_totalFrames <= 0)
                {
                    return BuildSnapshotLocked();
                }

                if (_isPlaying)
                {
                    AdvanceLocked(ticks);
                }

                var target = _currentFrame + deltaFrames;
                if (_isLooping)
                {
                    target = ((target % _totalFrames) + _totalFrames) % _totalFrames;
                }
                else
                {
                    target = Math.Clamp(target, 0, _totalFrames - 1);
                }

                return SeekFrameLocked(target, ticks);
            }
        }

        public FrameTimelineSnapshot Advance()
        {
            return Advance(_tickProvider());
        }

        public FrameTimelineSnapshot Advance(long nowTicks)
        {
            lock (_gate)
            {
                return AdvanceLocked(nowTicks);
            }
        }

        public FrameTimelineSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return BuildSnapshotLocked();
            }
        }

        /// <summary>
        /// Resolves the active keyframe index for a sorted sequence of keyframe timestamps (e.g. uneven 2D sprite keyframes).
        /// Returns the greatest index whose timestamp is &lt;= <paramref name="timeSeconds"/>, or 0 if before the first keyframe.
        /// </summary>
        public static int FindKeyframeIndex(IReadOnlyList<double> sortedKeyframeTimes, double timeSeconds)
        {
            if (sortedKeyframeTimes == null || sortedKeyframeTimes.Count == 0)
            {
                return 0;
            }

            var safeTime = double.IsFinite(timeSeconds) && timeSeconds > 0.0 ? timeSeconds : 0.0;
            if (safeTime < sortedKeyframeTimes[0])
            {
                return 0;
            }

            int lo = 0;
            int hi = sortedKeyframeTimes.Count - 1;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo + 1) >> 1);
                if (sortedKeyframeTimes[mid] <= safeTime + FrameEpsilon)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }

        /// <summary>
        /// Resolves the active keyframe index for a sorted sequence of single-precision keyframe timestamps.
        /// </summary>
        public static int FindKeyframeIndex(IReadOnlyList<float> sortedKeyframeTimes, double timeSeconds)
        {
            if (sortedKeyframeTimes == null || sortedKeyframeTimes.Count == 0)
            {
                return 0;
            }

            var safeTime = double.IsFinite(timeSeconds) && timeSeconds > 0.0 ? timeSeconds : 0.0;
            if (safeTime < sortedKeyframeTimes[0])
            {
                return 0;
            }

            int lo = 0;
            int hi = sortedKeyframeTimes.Count - 1;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo + 1) >> 1);
                if (sortedKeyframeTimes[mid] <= safeTime + FrameEpsilon)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }

        private FrameTimelineSnapshot SeekFrameLocked(int frame, long nowTicks)
        {
            if (_totalFrames <= 0)
            {
                _currentFrame = 0;
                _elapsedSeconds = 0.0;
                _isCompleted = false;
                _anchorTicks = nowTicks;
                _seekGeneration++;
                return BuildSnapshotLocked();
            }

            var clampedFrame = Math.Clamp(frame, 0, _totalFrames - 1);
            _currentFrame = clampedFrame;

            var timeFromFrame = _fps > 0.0 ? clampedFrame / _fps : 0.0;
            if (_durationSeconds > 0.0)
            {
                timeFromFrame = Math.Clamp(timeFromFrame, 0.0, _durationSeconds);
            }

            _elapsedSeconds = timeFromFrame;
            _isCompleted = false;
            _anchorTicks = nowTicks;
            _seekGeneration++;
            return BuildSnapshotLocked();
        }

        private FrameTimelineSnapshot AdvanceLocked(long nowTicks)
        {
            if (!_isPlaying)
            {
                return BuildSnapshotLocked();
            }

            var deltaTicks = nowTicks - _anchorTicks;
            if (deltaTicks < 0L)
            {
                deltaTicks = 0L;
            }

            _anchorTicks = nowTicks;
            if (deltaTicks > 0L)
            {
                var deltaSeconds = deltaTicks * _ticksToSeconds * _playbackSpeed;
                var nextSeconds = _elapsedSeconds + deltaSeconds;

                if (_durationSeconds > 0.0)
                {
                    if (_isLooping)
                    {
                        nextSeconds %= _durationSeconds;
                        if (nextSeconds < 0.0)
                        {
                            nextSeconds += _durationSeconds;
                        }

                        _elapsedSeconds = nextSeconds;
                        _isCompleted = false;
                    }
                    else if (nextSeconds >= _durationSeconds - FrameEpsilon)
                    {
                        _elapsedSeconds = _durationSeconds;
                        _isCompleted = true;
                        _isPlaying = false;
                    }
                    else
                    {
                        _elapsedSeconds = nextSeconds;
                        _isCompleted = false;
                    }
                }
                else
                {
                    _elapsedSeconds = nextSeconds;
                }
            }

            _currentFrame = ComputeFrameFromTimeLocked(_elapsedSeconds, _isCompleted);
            return BuildSnapshotLocked();
        }

        private int ComputeFrameFromTimeLocked(double timeSeconds, bool isCompleted)
        {
            if (_totalFrames <= 1)
            {
                return 0;
            }

            if (!_isLooping && isCompleted)
            {
                return _totalFrames - 1;
            }

            var rawFrame = (long)Math.Floor(timeSeconds * _fps + FrameEpsilon);
            if (_isLooping)
            {
                var wrapped = (int)(rawFrame % _totalFrames);
                return wrapped < 0 ? wrapped + _totalFrames : wrapped;
            }

            if (rawFrame <= 0L)
            {
                return 0;
            }

            return rawFrame >= _totalFrames ? _totalFrames - 1 : (int)rawFrame;
        }

        private FrameTimelineSnapshot BuildSnapshotLocked()
        {
            return new FrameTimelineSnapshot(
                _currentFrame,
                _elapsedSeconds,
                _isCompleted,
                _isPlaying,
                _seekGeneration);
        }

        private static double SanitizeSpeed(double speed)
        {
            if (double.IsNaN(speed))
            {
                return DefaultPlaybackSpeed;
            }

            if (double.IsNegativeInfinity(speed))
            {
                return MinPlaybackSpeed;
            }

            if (double.IsPositiveInfinity(speed))
            {
                return MaxPlaybackSpeed;
            }

            return Math.Clamp(speed, MinPlaybackSpeed, MaxPlaybackSpeed);
        }
    }
}
