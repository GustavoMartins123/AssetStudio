using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class PlaybackTimelineClockTests
    {
        private const long VirtualTicksPerSecond = 1_000_000L; // 1 tick = 1 microsecond

        [Fact]
        public void PlaybackTimelineClock_PureService_HasZeroAvaloniaImports()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var servicePath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "PlaybackTimelineClock.cs");
            Assert.True(File.Exists(servicePath), $"Expected service file at {servicePath}");

            var lines = File.ReadAllLines(servicePath);
            var avaloniaUsings = lines
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("using Avalonia", StringComparison.Ordinal))
                .ToList();

            Assert.Empty(avaloniaUsings);
        }

        [Fact]
        public void MediaTimelineTracker_VideoMode_LocksPresentedTimestamps_AndHandlesSeekBarrierAndReverseOrder()
        {
            var tracker = new MediaTimelineTracker();
            tracker.OnMediaOpened(durationMs: 12_000);

            Assert.Equal(12_000L, tracker.DurationMs);
            Assert.False(tracker.HasPendingSeek);
            Assert.Equal(0L, tracker.GetEffectivePositionMs(fallbackPlayerPositionMs: 950.0, nowTickMs: 100));

            // Active playback order: PositionChanged stages, FrameReady commits
            Assert.False(tracker.OnPositionChangedFromPlayer(positionMs: 320.0, isPlaying: true, out _));
            Assert.True(tracker.OnFramePresented(fallbackPositionMs: 999.0, out var presentedMs));
            Assert.Equal(320L, presentedMs);
            Assert.Equal(320L, tracker.GetEffectivePositionMs(999.0, nowTickMs: 200));

            // Seek barrier ignores stale pre-seek events until generation completes
            var gen1 = tracker.BeginSeek(8_000L);
            Assert.True(tracker.HasPendingSeek);
            Assert.False(tracker.OnPositionChangedFromPlayer(positionMs: 360.0, isPlaying: true, out var heldMs));
            Assert.Equal(8_000L, heldMs);
            Assert.False(tracker.OnFramePresented(fallbackPositionMs: 360.0, out heldMs));
            Assert.Equal(8_000L, heldMs);

            // Superseded seek generation must not unlock a newer seek
            var gen2 = tracker.BeginSeek(9_500L);
            tracker.MarkSeekBarrierReached(gen1, nowTickMs: 300);
            Assert.False(tracker.OnFramePresented(fallbackPositionMs: 8_010.0, out heldMs));
            Assert.Equal(9_500L, heldMs);

            // Reaching latest generation + paused single-frame reverse order (FrameReady -> PositionChanged)
            tracker.MarkSeekBarrierReached(gen2, nowTickMs: 350);
            Assert.True(tracker.OnFramePresented(fallbackPositionMs: 9_512.0, out var pausedFrameMs));
            Assert.Equal(9_512L, pausedFrameMs);
            Assert.True(tracker.OnPositionChangedFromPlayer(positionMs: 9_512.0, isPlaying: false, out var pausedPosMs));
            Assert.Equal(9_512L, pausedPosMs);
            Assert.False(tracker.HasPendingSeek);
        }

        [Fact]
        public void MediaTimelineTracker_ZeroDurationContainer_TracksAndTimesOutSafely()
        {
            var tracker = new MediaTimelineTracker(seekBarrierTimeoutMs: 500);
            tracker.OnMediaOpened(durationMs: 0);

            Assert.False(tracker.OnPositionChangedFromPlayer(positionMs: 4321.6, isPlaying: true, out _));
            Assert.True(tracker.OnFramePresented(fallbackPositionMs: 4321.6, out var posMs));
            Assert.Equal(4322L, posMs);

            var gen = tracker.BeginSeek(7500L);
            tracker.MarkSeekBarrierReached(gen, nowTickMs: 1000);
            Assert.Equal(7500L, tracker.GetEffectivePositionMs(fallbackPlayerPositionMs: 0.0, nowTickMs: 1200));
            Assert.True(tracker.HasPendingSeek);

            Assert.Equal(7500L, tracker.GetEffectivePositionMs(fallbackPlayerPositionMs: 0.0, nowTickMs: 1600));
            Assert.False(tracker.HasPendingSeek);
        }

        [Fact]
        public void MediaTimelineTracker_MonotonicClockMode_PreservesPauseResume_AndClampsToDuration()
        {
            long ticks = 10_000_000L;
            var tracker = new MediaTimelineTracker(
                tickProvider: () => ticks,
                tickFrequency: VirtualTicksPerSecond);
            tracker.OnMediaOpened(durationMs: 5_000);

            tracker.StartClock(baseMs: 0L);
            Assert.True(tracker.IsClockRunning);
            Assert.Equal(0L, tracker.GetClockPositionMs());

            // Advance 1.25s
            ticks += 1_250_000L;
            Assert.Equal(1_250L, tracker.GetClockPositionMs());

            // Pause and let 10 seconds of wall time elapse -> zero drift
            var pausedAt = tracker.PauseClock();
            Assert.Equal(1_250L, pausedAt);
            Assert.False(tracker.IsClockRunning);

            ticks += 10_000_000L;
            Assert.Equal(1_250L, tracker.GetClockPositionMs());

            // Resume and advance 2.0s -> 3.25s
            Assert.Equal(1_250L, tracker.ResumeClock());
            Assert.True(tracker.IsClockRunning);
            ticks += 2_000_000L;
            Assert.Equal(3_250L, tracker.GetClockPositionMs());

            // Advance far past duration -> strictly clamped to [0, 5000]
            ticks += 50_000_000L;
            Assert.Equal(5_000L, tracker.GetClockPositionMs());

            // Seek clamps negative and overflow targets
            Assert.Equal(0L, tracker.SeekClock(-500L));
            Assert.Equal(5_000L, tracker.SeekClock(99_999L));

            // Stop resets to 0
            tracker.StopClock();
            Assert.False(tracker.IsClockRunning);
            Assert.Equal(0L, tracker.GetClockPositionMs());
        }

        [Fact]
        public void MediaTimelineTracker_ResolvePolledPositionMs_HoldsSeekTargetUntilConvergenceOrTimeout()
        {
            var tracker = new MediaTimelineTracker(seekBarrierTimeoutMs: 600);
            tracker.OnMediaOpened(durationMs: 10_000);

            // Normal polling clamps [0, duration]
            Assert.Equal(0L, tracker.ResolvePolledPositionMs(-250.0, nowTickMs: 100));
            Assert.Equal(10_000L, tracker.ResolvePolledPositionMs(15_000.0, nowTickMs: 110));
            Assert.Equal(2_000L, tracker.ResolvePolledPositionMs(2_000.0, nowTickMs: 120));

            // Active seek holds target against lagging backend position
            var gen = tracker.BeginSeek(6_000L);
            Assert.Equal(6_000L, tracker.ResolvePolledPositionMs(2_050.0, nowTickMs: 150));

            // Even after barrier reached, stale pre-seek position must not snap back
            tracker.MarkSeekBarrierReached(gen, nowTickMs: 200);
            Assert.Equal(6_000L, tracker.ResolvePolledPositionMs(2_100.0, nowTickMs: 350));
            Assert.True(tracker.HasPendingSeek);

            // Once backend converges within tolerance, seek commits and resumes live tracking
            Assert.Equal(6_080L, tracker.ResolvePolledPositionMs(6_080.0, nowTickMs: 400));
            Assert.False(tracker.HasPendingSeek);
            Assert.Equal(6_500L, tracker.ResolvePolledPositionMs(6_500.0, nowTickMs: 500));

            // Timeout fallback releases barrier if backend never converges
            var gen2 = tracker.BeginSeek(8_000L);
            tracker.MarkSeekBarrierReached(gen2, nowTickMs: 1_000);
            Assert.Equal(8_000L, tracker.ResolvePolledPositionMs(3_000.0, nowTickMs: 1_400));
            Assert.True(tracker.HasPendingSeek);
            Assert.Equal(3_100L, tracker.ResolvePolledPositionMs(3_100.0, nowTickMs: 1_700));
            Assert.False(tracker.HasPendingSeek);
        }

        [Fact]
        public void FrameTimelineClock_AdvancesAtExactRealTimeFps_UnderHeavyTimerJitter()
        {
            long ticks = 0L;
            var clock = new FrameTimelineClock(
                tickProvider: () => ticks,
                tickFrequency: VirtualTicksPerSecond);

            // 60 frames @ 30 FPS = 2.0s looping
            clock.Configure(totalFrames: 60, fps: 30.0, durationSeconds: 0.0, isLooping: true, playbackSpeed: 1.0);
            Assert.Equal(2.0, clock.DurationSeconds, precision: 6);
            Assert.Equal(0, clock.CurrentFrame);

            clock.Play(nowTicks: ticks);

            // Simulate jittery UI ticks: 100ms (3 frames), 250ms (7.5 frames -> total 350ms = frame 10),
            // 150ms (total 500ms = frame 15), 1500ms (total 2000ms -> wraps cleanly to frame 0)
            ticks += 100_000L;
            var s1 = clock.Advance(ticks);
            Assert.Equal(3, s1.CurrentFrame);
            Assert.Equal(0.1, s1.CurrentTimeSeconds, precision: 6);
            Assert.False(s1.IsCompleted);

            ticks += 250_000L;
            var s2 = clock.Advance(ticks);
            Assert.Equal(10, s2.CurrentFrame);
            Assert.Equal(0.35, s2.CurrentTimeSeconds, precision: 6);

            ticks += 150_000L;
            var s3 = clock.Advance(ticks);
            Assert.Equal(15, s3.CurrentFrame);
            Assert.Equal(0.5, s3.CurrentTimeSeconds, precision: 6);

            ticks += 1_500_000L;
            var s4 = clock.Advance(ticks);
            Assert.Equal(0, s4.CurrentFrame);
            Assert.Equal(0.0, s4.CurrentTimeSeconds, precision: 6);
            Assert.True(s4.IsPlaying);
            Assert.False(s4.IsCompleted);
        }

        [Fact]
        public void FrameTimelineClock_PauseResumeRestart_PreservesExactSubFrameOffset()
        {
            long ticks = 1_000_000L;
            var clock = new FrameTimelineClock(() => ticks, VirtualTicksPerSecond);
            clock.Configure(totalFrames: 120, fps: 60.0, isLooping: true);

            clock.Play(ticks);
            ticks += 500_000L; // 0.5s = frame 30
            var paused = clock.Pause(ticks);
            Assert.Equal(30, paused.CurrentFrame);
            Assert.Equal(0.5, paused.CurrentTimeSeconds, precision: 6);
            Assert.False(paused.IsPlaying);

            // Long pause must not advance clock
            ticks += 30_000_000L;
            var stillPaused = clock.Advance(ticks);
            Assert.Equal(30, stillPaused.CurrentFrame);
            Assert.Equal(0.5, stillPaused.CurrentTimeSeconds, precision: 6);

            // Resume and advance 0.25s -> 0.75s = frame 45
            clock.Resume(ticks);
            ticks += 250_000L;
            var resumed = clock.Advance(ticks);
            Assert.Equal(45, resumed.CurrentFrame);
            Assert.Equal(0.75, resumed.CurrentTimeSeconds, precision: 6);

            // Restart snaps cleanly to 0 and continues playing
            var restarted = clock.Restart(ticks);
            Assert.Equal(0, restarted.CurrentFrame);
            Assert.Equal(0.0, restarted.CurrentTimeSeconds, precision: 6);
            Assert.True(restarted.IsPlaying);
        }

        [Fact]
        public void FrameTimelineClock_OneShotMode_ClampsAtFinalFrame_AndPlayRewindsFromEnd()
        {
            long ticks = 0L;
            var clock = new FrameTimelineClock(() => ticks, VirtualTicksPerSecond);
            clock.Configure(totalFrames: 30, fps: 30.0, isLooping: false);

            clock.Play(ticks);
            ticks += 500_000L; // 0.5s -> frame 15
            var mid = clock.Advance(ticks);
            Assert.Equal(15, mid.CurrentFrame);
            Assert.False(mid.IsCompleted);
            Assert.True(mid.IsPlaying);

            // Overshoot past 1.0s -> clamps to final frame (29), marks completed, stops playing
            ticks += 2_000_000L;
            var end = clock.Advance(ticks);
            Assert.Equal(29, end.CurrentFrame);
            Assert.Equal(1.0, end.CurrentTimeSeconds, precision: 6);
            Assert.True(end.IsCompleted);
            Assert.False(end.IsPlaying);

            // Calling Play() while parked at completion rewinds to frame 0 and resumes
            var replayed = clock.Play(ticks);
            Assert.Equal(0, replayed.CurrentFrame);
            Assert.Equal(0.0, replayed.CurrentTimeSeconds, precision: 6);
            Assert.True(replayed.IsPlaying);
            Assert.False(replayed.IsCompleted);
        }

        [Fact]
        public void FrameTimelineClock_SeekFrame_SeekTime_StepFrame_AndPlaybackSpeedScaling_WorkAccurately()
        {
            long ticks = 0L;
            var clock = new FrameTimelineClock(() => ticks, VirtualTicksPerSecond);
            clock.Configure(totalFrames: 60, fps: 30.0, isLooping: true, playbackSpeed: 1.0);

            var initialGen = clock.SeekGeneration;

            // SeekFrame clamps to [0, totalFrames - 1]
            var sFrame = clock.SeekFrame(45, ticks);
            Assert.Equal(45, sFrame.CurrentFrame);
            Assert.Equal(1.5, sFrame.CurrentTimeSeconds, precision: 6);
            Assert.True(sFrame.SeekGeneration > initialGen);

            Assert.Equal(0, clock.SeekFrame(-10, ticks).CurrentFrame);
            Assert.Equal(59, clock.SeekFrame(999, ticks).CurrentFrame);

            // StepFrame wraps in looping mode and clamps in one-shot mode
            Assert.Equal(0, clock.StepFrame(1, ticks).CurrentFrame);
            Assert.Equal(59, clock.StepFrame(-1, ticks).CurrentFrame);

            clock.SetLooping(false);
            Assert.Equal(59, clock.StepFrame(5, ticks).CurrentFrame);
            Assert.Equal(0, clock.SeekFrame(0, ticks).CurrentFrame);
            Assert.Equal(0, clock.StepFrame(-5, ticks).CurrentFrame);

            // SeekTime handles NaN, +/-Infinity, and clamping
            Assert.Equal(0.0, clock.SeekTime(double.NaN, ticks).CurrentTimeSeconds, precision: 6);
            Assert.Equal(2.0, clock.SeekTime(double.PositiveInfinity, ticks).CurrentTimeSeconds, precision: 6);
            Assert.Equal(0.0, clock.SeekTime(double.NegativeInfinity, ticks).CurrentTimeSeconds, precision: 6);

            // Playback speed scaling: 2x speed advances 0.5s wall time -> 1.0s clip time (frame 30)
            clock.SetLooping(true);
            clock.SeekFrame(0, ticks);
            clock.SetPlaybackSpeed(2.0, ticks);
            clock.Play(ticks);
            ticks += 500_000L;
            var fast = clock.Advance(ticks);
            Assert.Equal(30, fast.CurrentFrame);
            Assert.Equal(1.0, fast.CurrentTimeSeconds, precision: 6);

            // Changing speed mid-playback preserves accumulated time with zero jump
            clock.SetPlaybackSpeed(0.5, ticks);
            ticks += 1_000_000L; // +0.5s clip time -> 1.5s total = frame 45
            var slow = clock.Advance(ticks);
            Assert.Equal(45, slow.CurrentFrame);
            Assert.Equal(1.5, slow.CurrentTimeSeconds, precision: 6);
        }

        [Fact]
        public void FrameTimelineClock_FindKeyframeIndex_HandlesUneven2DKeyframeTimestamps()
        {
            double[] keyframes = { 0.0, 0.08, 0.25, 0.90, 1.50 };

            Assert.Equal(0, FrameTimelineClock.FindKeyframeIndex(keyframes, -0.5));
            Assert.Equal(0, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.0));
            Assert.Equal(0, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.079));
            Assert.Equal(1, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.08));
            Assert.Equal(1, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.24));
            Assert.Equal(2, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.25));
            Assert.Equal(2, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.89));
            Assert.Equal(3, FrameTimelineClock.FindKeyframeIndex(keyframes, 0.90));
            Assert.Equal(4, FrameTimelineClock.FindKeyframeIndex(keyframes, 1.50));
            Assert.Equal(4, FrameTimelineClock.FindKeyframeIndex(keyframes, 9.99));

            // Float overload parity
            float[] floatKeyframes = { 0.0f, 0.1f, 0.75f };
            Assert.Equal(1, FrameTimelineClock.FindKeyframeIndex(floatKeyframes, 0.4));
            Assert.Equal(2, FrameTimelineClock.FindKeyframeIndex(floatKeyframes, 0.75));
        }

        [Fact]
        public async Task AudioPlaybackService_LaggingBackendSeekBarrier_PreventsSnapBackUntilBackendConverges()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AssetStudio_TimelineSeekTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                long nowTickMs = 1_000L;
                LaggingSeekAudioBackend? backend = null;
                var factory = new DelegateAudioPreviewBackendFactory(
                    "LaggingMock",
                    "Failed",
                    static _ => true,
                    () => backend = new LaggingSeekAudioBackend());

                using var service = new AudioPlaybackService(
                    new[] { factory },
                    tempDirectory: tempDir,
                    nowTickMsProvider: () => nowTickMs);

                var loadResult = await service.LoadAsync("lag-clip", _ => new AudioPreviewSource(new byte[] { 1, 2, 3, 4 }, ".wav", 10_000, "WAV"));
                Assert.Equal(AudioPreviewResultStatus.Ready, loadResult.Status);
                Assert.NotNull(backend);

                var playResult = await service.PlayFromStartAsync();
                Assert.Equal(AudioPreviewResultStatus.Started, playResult.Status);

                backend!.PositionSeconds = 1.5;
                Assert.Equal(1_500L, service.Poll().PositionMs);

                // User starts dragging slider -> BeginUserSeek holds target immediately
                service.BeginUserSeek(6_000L);
                Assert.True(service.HasPendingSeek);
                Assert.Equal(6_000L, service.Poll().PositionMs);

                // User commits Seek(7000); backend is lagging and still reports 1.55s!
                service.Seek(7_000L);
                Assert.Equal(7.0, backend.LastRequestedSeekSeconds, precision: 3);
                Assert.True(service.HasPendingSeek);

                // Poll immediately after seek must NOT snap back to 1550ms
                backend.PositionSeconds = 1.55;
                nowTickMs += 100L;
                var heldSnap = service.Poll();
                Assert.Equal(7_000L, heldSnap.PositionMs);
                Assert.False(heldSnap.IsEnded);

                // Once lagging backend finally finishes seeking and reports 7.05s, barrier clears
                backend.PositionSeconds = 7.05;
                nowTickMs += 100L;
                var convergedSnap = service.Poll();
                Assert.Equal(7_050L, convergedSnap.PositionMs);
                Assert.False(service.HasPendingSeek);

                // Subsequent live playback advances normally and clamps to DurationMs
                backend.PositionSeconds = 12.0;
                nowTickMs += 100L;
                Assert.Equal(10_000L, service.Poll().PositionMs);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void MeshPreviewViewModel_SyncFromTimeline_PreservesExactTimeForUnevenKeyframes()
        {
            var vm = new MeshPreviewViewModel();
            vm.SetAnimationState(
                isLoaded: true,
                clipName: "SpriteAnim",
                durationSeconds: 1.5,
                currentTimeSeconds: 0.0,
                isPlaying: true,
                totalFrames: 5,
                sampleRate: 30.0);

            // Keyframe 3 lives at 0.90s (uneven), NOT 3/30 = 0.10s
            vm.SyncFromTimeline(currentFrame: 3, totalFrames: 5, currentTimeSeconds: 0.90);
            Assert.Equal(3, vm.CurrentFrame);
            Assert.Equal(5, vm.TotalFrames);
            Assert.Equal(0.90, vm.CurrentTimeSeconds, precision: 5);
            Assert.Equal("Frame: 3/5", vm.FormattedFrameText);
            Assert.Equal("00:00.90 / 00:01.50", vm.FormattedTimeText);
        }

        private sealed class LaggingSeekAudioBackend : IAudioPreviewBackend
        {
            public string BackendName => "LaggingMock";
            public string PlaybackFailureMessage => "Failed";
            public bool CanSeek => true;
            public bool IsPlaying { get; private set; }
            public bool IsEnded { get; set; }
            public double PositionSeconds { get; set; }
            public double DurationSeconds { get; private set; }
            public double LastRequestedSeekSeconds { get; private set; } = -1.0;

            public event EventHandler? EndReached
            {
                add { }
                remove { }
            }

            public Task<bool> LoadAsync(AudioPreviewMedia media, CancellationToken cancellationToken)
            {
                DurationSeconds = media.DurationMs / 1000.0;
                return Task.FromResult(true);
            }

            public Task<bool> PlayFromStartAsync(CancellationToken cancellationToken)
            {
                PositionSeconds = 0.0;
                IsPlaying = true;
                IsEnded = false;
                return Task.FromResult(true);
            }

            public void Play() => IsPlaying = true;
            public void Pause() => IsPlaying = false;
            public void Stop() { IsPlaying = false; PositionSeconds = 0.0; }
            public void Restart() { PositionSeconds = 0.0; IsPlaying = true; IsEnded = false; }

            public void Seek(double seconds)
            {
                // Intentionally does NOT update PositionSeconds immediately to simulate async/WinMM restart lag.
                LastRequestedSeekSeconds = seconds;
            }

            public void SetVolume(int volume) { }
            public void Dispose() { IsPlaying = false; }
        }
    }
}
