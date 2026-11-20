using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class MediaPlaybackViewModelTests
    {
        #region MediaPlaybackViewModel Tests

        [Fact]
        public void MediaPlaybackViewModel_InitialState_IsCleanAndDisabled()
        {
            var vm = new MediaPlaybackViewModel();

            Assert.Equal(ActiveMediaKind.None, vm.MediaKind);
            Assert.Equal(MediaPlaybackState.Stopped, vm.PlaybackState);
            Assert.False(vm.IsReady);
            Assert.False(vm.IsAudioActive);
            Assert.False(vm.IsVideoActive);
            Assert.False(vm.IsPreparing);
            Assert.False(vm.IsPlaying);
            Assert.False(vm.IsPaused);
            Assert.False(vm.IsLooping);
            Assert.False(vm.IsMuted);
            Assert.Equal(MediaPlaybackViewModel.DefaultVolume, vm.Volume);
            Assert.Equal(MediaPlaybackViewModel.DefaultVolume, vm.AudioVolume);
            Assert.Equal(MediaPlaybackViewModel.DefaultVolume, vm.VideoVolume);
            Assert.Equal(MediaPlaybackViewModel.DefaultVolume, vm.EffectiveVolume);
            Assert.Equal(0.0, vm.PositionSeconds);
            Assert.Equal(0.0, vm.DurationSeconds);
            Assert.Equal("0:00.00", vm.FormattedPosition);
            Assert.Equal("0:00.00", vm.FormattedDuration);
            Assert.Equal(MediaPlaybackViewModel.EmptyTimeRange, vm.FormattedTimeRange);
            Assert.Equal(0.0, vm.ProgressPermille);
            Assert.Equal("Stopped", vm.StatusText);
            Assert.Equal(string.Empty, vm.MediaTitle);
            Assert.Equal(string.Empty, vm.MediaInfoText);
            Assert.Equal(string.Empty, vm.BackendDisplayName);
            Assert.Equal(string.Empty, vm.ErrorMessage);
            Assert.False(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanStop);
            Assert.False(vm.CanSeek);
        }

        [Fact]
        public void MediaPlaybackViewModel_AudioFullLifecycle_TransitionsButtonsAndFormatting()
        {
            var vm = new MediaPlaybackViewModel();

            vm.SelectAudio("BossTheme", "Compression format: Vorbis");
            Assert.Equal(ActiveMediaKind.Audio, vm.MediaKind);
            Assert.True(vm.IsAudioActive);
            Assert.False(vm.IsVideoActive);
            Assert.Equal(MediaPlaybackState.Stopped, vm.PlaybackState);
            Assert.True(vm.IsReady);
            Assert.Equal("Ready", vm.StatusText);
            Assert.Equal("BossTheme", vm.MediaTitle);
            Assert.Equal("Compression format: Vorbis", vm.MediaInfoText);
            Assert.True(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanStop);
            Assert.False(vm.CanSeek);

            vm.BeginAudioLoad("BossTheme", "Compression format: Vorbis");
            Assert.Equal(MediaPlaybackState.Preparing, vm.PlaybackState);
            Assert.True(vm.IsPreparing);
            Assert.Equal("Loading", vm.StatusText);
            Assert.False(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanStop);
            Assert.False(vm.CanSeek);

            vm.MarkAudioReady(125.5, "WinMM PCM", canSeek: true, sourceDescription: "FSB5 -> WAV (PCM)");
            Assert.Equal(MediaPlaybackState.Stopped, vm.PlaybackState);
            Assert.True(vm.IsReady);
            Assert.False(vm.IsPreparing);
            Assert.Equal("Ready", vm.StatusText);
            Assert.Equal("WinMM PCM", vm.BackendDisplayName);
            Assert.Equal("Compression format: Vorbis | Preview: FSB5 -> WAV (PCM)", vm.MediaInfoText);
            Assert.Equal(125.5, vm.DurationSeconds, precision: 3);
            Assert.Equal("0:00.00 / 2:05.50", vm.FormattedTimeRange);
            Assert.True(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanStop);
            Assert.True(vm.CanSeek);

            // Re-marking ready replaces the "| Preview:" suffix instead of duplicating it.
            vm.MarkAudioReady(125.5, "WinMM PCM", canSeek: true, sourceDescription: "WAV");
            Assert.Equal("Compression format: Vorbis | Preview: WAV", vm.MediaInfoText);

            vm.SetPlaying();
            Assert.Equal(MediaPlaybackState.Playing, vm.PlaybackState);
            Assert.True(vm.IsPlaying);
            Assert.False(vm.IsPaused);
            Assert.Equal("Playing", vm.StatusText);
            Assert.False(vm.CanPlay);
            Assert.True(vm.CanPause);
            Assert.True(vm.CanStop);
            Assert.True(vm.CanSeek);

            vm.UpdatePosition(62.75);
            Assert.Equal(62.75, vm.PositionSeconds, precision: 3);
            Assert.Equal("1:02.75", vm.FormattedPosition);
            Assert.Equal("2:05.50", vm.FormattedDuration);
            Assert.Equal("1:02.75 / 2:05.50", vm.FormattedTimeRange);
            Assert.Equal(500.0, vm.ProgressPermille, precision: 2);

            // Seek clamp: below zero, NaN, Infinity, and above duration.
            vm.UpdatePosition(-10.0);
            Assert.Equal(0.0, vm.PositionSeconds);
            vm.UpdatePosition(double.NaN);
            Assert.Equal(0.0, vm.PositionSeconds);
            vm.UpdatePosition(double.PositiveInfinity);
            Assert.Equal(0.0, vm.PositionSeconds);
            vm.UpdatePosition(999.0);
            Assert.Equal(125.5, vm.PositionSeconds, precision: 3);
            Assert.Equal(1000.0, vm.ProgressPermille, precision: 2);

            vm.SetPaused();
            Assert.Equal(MediaPlaybackState.Paused, vm.PlaybackState);
            Assert.False(vm.IsPlaying);
            Assert.True(vm.IsPaused);
            Assert.Equal("Paused", vm.StatusText);
            Assert.True(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.True(vm.CanStop);
            Assert.True(vm.CanSeek);

            vm.SetLooping(true);
            Assert.True(vm.IsLooping);

            vm.SetStopped();
            Assert.Equal(MediaPlaybackState.Stopped, vm.PlaybackState);
            Assert.False(vm.IsReady);
            Assert.Equal("Stopped", vm.StatusText);
            Assert.Equal(0.0, vm.PositionSeconds);
            Assert.Equal(MediaPlaybackViewModel.EmptyTimeRange, vm.FormattedTimeRange);
            Assert.True(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanStop);
            Assert.True(vm.CanSeek);

            vm.SetError("Codec unsupported", statusText: "Unsupported");
            Assert.Equal(MediaPlaybackState.Error, vm.PlaybackState);
            Assert.Equal("Unsupported", vm.StatusText);
            Assert.Equal("Codec unsupported", vm.ErrorMessage);
            Assert.False(vm.CanPlay);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanStop);
            Assert.False(vm.CanSeek);

            vm.SetVolume(45);
            vm.SetMuted(true);
            vm.Reset();

            Assert.Equal(ActiveMediaKind.None, vm.MediaKind);
            Assert.Equal(MediaPlaybackState.Stopped, vm.PlaybackState);
            Assert.Equal(string.Empty, vm.MediaTitle);
            Assert.Equal(string.Empty, vm.ErrorMessage);
            Assert.Equal(string.Empty, vm.BackendDisplayName);
            Assert.Equal(0.0, vm.DurationSeconds);
            // User preferences persist across Reset().
            Assert.True(vm.IsLooping);
            Assert.True(vm.IsMuted);
            Assert.Equal(45, vm.AudioVolume);
            Assert.Equal(0, vm.EffectiveVolume);
        }

        [Fact]
        public void MediaPlaybackViewModel_VideoLifecycleAndAudioVideoSwitch_PreservesPerKindVolumeAndIsolation()
        {
            var vm = new MediaPlaybackViewModel();

            vm.SelectAudio("IntroMusic", "PCM");
            vm.SetVolume(30);
            Assert.Equal(30, vm.AudioVolume);
            Assert.Equal(30, vm.Volume);

            // Switch directly to Video without Reset.
            vm.SelectVideo("Cutscene_01", "Ready. Press Play to prepare embedded preview.");
            Assert.Equal(ActiveMediaKind.Video, vm.MediaKind);
            Assert.True(vm.IsVideoActive);
            Assert.False(vm.IsAudioActive);
            Assert.Equal(MediaPlaybackViewModel.DefaultVolume, vm.Volume);
            Assert.Equal(30, vm.AudioVolume);

            vm.SetVolume(65);
            Assert.Equal(65, vm.VideoVolume);
            Assert.Equal(65, vm.Volume);

            // BeginVideoLoad with null title preserves the current title.
            vm.BeginVideoLoad(null, "Preparing embedded preview...");
            Assert.Equal("Cutscene_01", vm.MediaTitle);
            Assert.Equal(MediaPlaybackState.Preparing, vm.PlaybackState);
            Assert.True(vm.IsPreparing);

            vm.MarkVideoReady(18.25, "FFmpeg", "Thumbnail ready.");
            Assert.Equal(MediaPlaybackState.Stopped, vm.PlaybackState);
            Assert.True(vm.IsReady);
            Assert.Equal("FFmpeg", vm.BackendDisplayName);
            Assert.Equal("0:00.00 / 0:18.25", vm.FormattedTimeRange);
            Assert.True(vm.CanSeek);

            vm.SetPlaying();
            vm.UpdatePosition(4.5, 18.25);
            Assert.Equal("0:04.50 / 0:18.25", vm.FormattedTimeRange);

            // Late thumbnail completion must NOT downgrade active Playing state or rewind position.
            vm.MarkVideoReady(18.25, "FFmpeg", "Thumbnail ready.");
            Assert.Equal(MediaPlaybackState.Playing, vm.PlaybackState);
            Assert.Equal(4.5, vm.PositionSeconds, precision: 3);

            // ResetIfActive(Audio) must not affect active Video.
            vm.ResetIfActive(ActiveMediaKind.Audio);
            Assert.Equal(ActiveMediaKind.Video, vm.MediaKind);
            Assert.True(vm.IsPlaying);

            // ResetIfActive(Video) resets Video.
            vm.ResetIfActive(ActiveMediaKind.Video);
            Assert.Equal(ActiveMediaKind.None, vm.MediaKind);
            Assert.False(vm.IsVideoActive);
            Assert.Equal(30, vm.AudioVolume);
            Assert.Equal(65, vm.VideoVolume);
        }

        [Fact]
        public void MediaPlaybackViewModel_NonSeekableBackend_AndVolumeClamping_AndDispatcherRouting()
        {
            var dispatchCount = 0;
            var vm = new MediaPlaybackViewModel(action =>
            {
                dispatchCount++;
                action();
            });

            vm.BeginAudioLoad("LinuxClip", "WAV");
            vm.MarkAudioReady(10.0, "System audio player", canSeek: false);

            Assert.True(dispatchCount >= 2);
            Assert.False(vm.CanSeek);
            Assert.True(vm.CanPlay);

            vm.SetVolume(250);
            Assert.Equal(100, vm.Volume);
            vm.SetVolume(-50);
            Assert.Equal(0, vm.Volume);
            vm.SetVolume(72);
            vm.SetMuted(true);
            Assert.Equal(72, vm.Volume);
            Assert.Equal(0, vm.EffectiveVolume);
            vm.SetMuted(false);
            Assert.Equal(72, vm.EffectiveVolume);
        }

        [Fact]
        public void MediaPlaybackViewModel_PropertyChangedDeduplication_SuppressesNoOpNotifications()
        {
            var vm = new MediaPlaybackViewModel();
            var raised = new List<string>();
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != null)
                {
                    raised.Add(args.PropertyName);
                }
            };

            // No-op calls on initial state raise zero events.
            vm.Reset();
            vm.SetVolume(MediaPlaybackViewModel.DefaultVolume);
            vm.SetLooping(false);
            vm.SetMuted(false);
            vm.SetPlaying();
            vm.SetPaused();
            vm.SetStopped();
            vm.UpdatePosition(5.0);
            Assert.Empty(raised);

            vm.BeginAudioLoad("Track01", "PCM");
            vm.MarkAudioReady(20.0, "WinMM PCM", canSeek: true);
            vm.SetPlaying();
            vm.UpdatePosition(5.0);
            raised.Clear();

            // Repeating identical calls raises zero events.
            vm.SetPlaying();
            vm.UpdatePosition(5.0);
            vm.SetAudioVolume(MediaPlaybackViewModel.DefaultVolume);
            vm.SetLooping(false);
            vm.SetMuted(false);
            Assert.Empty(raised);

            // Changing only looping raises only IsLooping.
            vm.SetLooping(true);
            Assert.Equal(new[] { nameof(MediaPlaybackViewModel.IsLooping) }, raised);
        }

        [Theory]
        [InlineData(-500, "0:00.00")]
        [InlineData(0, "0:00.00")]
        [InlineData(50, "0:00.05")]
        [InlineData(999, "0:00.99")]
        [InlineData(1000, "0:01.00")]
        [InlineData(65432, "1:05.43")]
        [InlineData(3661250, "61:01.25")]
        public void MediaPlaybackViewModel_FormatTime_FormatsCentisecondsConsistently(long ms, string expected)
        {
            Assert.Equal(expected, MediaPlaybackViewModel.FormatTime(ms));
        }

        #endregion

        #region AudioPlaybackService & Strategy Tests

        [Fact]
        public async Task AudioPlaybackService_PrimaryBackendFails_FallsBackAndDisposesFailedPrimary()
        {
            using var tempScope = new TempDirScope();
            FakeAudioBackend? primary = null;
            FakeAudioBackend? fallback = null;

            var factories = new IAudioPreviewBackendFactory[]
            {
                new DelegateAudioPreviewBackendFactory(
                    "PrimaryPCM",
                    "Primary failed",
                    static m => m.IsWave,
                    () => primary = new FakeAudioBackend("PrimaryPCM") { LoadResult = false }),
                new DelegateAudioPreviewBackendFactory(
                    "FallbackFFmpeg",
                    "Fallback failed",
                    static m => !m.IsRawFsb,
                    () => fallback = new FakeAudioBackend("FallbackFFmpeg") { LoadResult = true })
            };

            using var service = new AudioPlaybackService(factories, tempDirectory: tempScope.Path);
            service.SetVolume(64);

            var wav = BuildPcm16Wav(sampleRate: 8000, channels: 1, durationMs: 2000);
            var loadResult = await service.LoadAsync("asset-1", _ => new AudioPreviewSource(wav, ".wav", 2000, "WAV"));

            Assert.Equal(AudioPreviewResultStatus.Ready, loadResult.Status);
            Assert.NotNull(primary);
            Assert.NotNull(fallback);
            Assert.Equal(1, primary!.DisposeCalls);
            Assert.Same(fallback, service.ActiveBackend);
            Assert.Equal("FallbackFFmpeg", service.BackendName);
            Assert.Equal(64, fallback!.LastVolume);
            Assert.True(service.IsLoaded("asset-1"));
            Assert.NotNull(service.CurrentTempPath);
            Assert.True(File.Exists(service.CurrentTempPath!));
        }

        [Fact]
        public async Task AudioPlaybackService_PrimaryBackendThrows_FallsBackCleanly()
        {
            using var tempScope = new TempDirScope();
            FakeAudioBackend? primary = null;
            FakeAudioBackend? fallback = null;

            var factories = new IAudioPreviewBackendFactory[]
            {
                new DelegateAudioPreviewBackendFactory(
                    "ThrowingPrimary",
                    "Primary threw",
                    static _ => true,
                    () => primary = new FakeAudioBackend("ThrowingPrimary") { ThrowOnLoad = true }),
                new DelegateAudioPreviewBackendFactory(
                    "HealthyFallback",
                    "Fallback failed",
                    static _ => true,
                    () => fallback = new FakeAudioBackend("HealthyFallback") { LoadResult = true })
            };

            var logs = new List<string>();
            using var service = new AudioPlaybackService(factories, tempDirectory: tempScope.Path, log: logs.Add);

            var result = await service.LoadAsync("asset-throw", _ => new AudioPreviewSource(new byte[] { 1, 2, 3, 4 }, ".ogg", 3000, "OGG"));

            Assert.Equal(AudioPreviewResultStatus.Ready, result.Status);
            Assert.Same(fallback, service.ActiveBackend);
            Assert.Equal(1, primary!.DisposeCalls);
            Assert.Contains(logs, l => l.Contains("ThrowingPrimary", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AudioPlaybackService_RapidClipSwitch_CancelsStaleLoadDisposesBackendAndLeaksNoTempFiles()
        {
            using var tempScope = new TempDirScope();
            var clipAEnteredBackendLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClipABackendLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var createdBackends = new List<FakeAudioBackend>();
            var gate = new object();

            var factory = new DelegateAudioPreviewBackendFactory(
                "MockBackend",
                "Failed",
                static _ => true,
                () =>
                {
                    var backend = new FakeAudioBackend("MockBackend");
                    lock (gate)
                    {
                        createdBackends.Add(backend);
                        if (createdBackends.Count == 1)
                        {
                            backend.OnLoadAsync = async (_, token) =>
                            {
                                clipAEnteredBackendLoad.TrySetResult();
                                await releaseClipABackendLoad.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                // Even if a backend ignores token and returns true, service must reject & dispose it.
                                return !token.IsCancellationRequested || true;
                            };
                        }
                    }
                    return backend;
                });

            using var service = new AudioPlaybackService(new[] { factory }, tempDirectory: tempScope.Path);
            var wavA = BuildPcm16Wav(8000, 1, 1000);
            var wavB = BuildPcm16Wav(8000, 1, 2500);

            var taskA = service.LoadAsync("clip-A", _ => new AudioPreviewSource(wavA, ".wav", 1000, "WAV A"));
            await clipAEnteredBackendLoad.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tempPathA = service.CurrentTempPath;
            Assert.NotNull(tempPathA);

            // Switch rapidly to clip B before clip A finishes backend LoadAsync.
            var taskB = service.LoadAsync("clip-B", _ => new AudioPreviewSource(wavB, ".wav", 2500, "WAV B"));
            releaseClipABackendLoad.TrySetResult();

            var resultA = await taskA;
            var resultB = await taskB;

            Assert.Equal(AudioPreviewResultStatus.Cancelled, resultA.Status);
            Assert.Equal(AudioPreviewResultStatus.Ready, resultB.Status);
            Assert.Equal("clip-B", service.CurrentAssetId);
            Assert.Equal(2500, service.DurationMs);
            Assert.Equal(2, createdBackends.Count);
            Assert.Equal(1, createdBackends[0].DisposeCalls);
            Assert.Equal(0, createdBackends[0].PlayFromStartCalls);
            Assert.Same(createdBackends[1], service.ActiveBackend);
            Assert.Equal(0, createdBackends[1].DisposeCalls);

            // Clip A's temp file must be deleted in background; only clip B's temp file remains.
            await WaitUntilAsync(() => !File.Exists(tempPathA!), TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(tempPathA!));

            var tempPathB = service.CurrentTempPath;
            Assert.NotNull(tempPathB);
            Assert.True(File.Exists(tempPathB!));

            // Disposing service removes clip B's temp file too -> zero leaks.
            service.Dispose();
            await WaitUntilAsync(() => Directory.GetFiles(tempScope.Path).Length == 0, TimeSpan.FromSeconds(3));
            Assert.Empty(Directory.GetFiles(tempScope.Path));
            Assert.Equal(1, createdBackends[1].DisposeCalls);
        }

        [Fact]
        public async Task AudioPlaybackService_RapidSwitchDuringDecode_CancelsStalePrepareTokenWithoutWritingFile()
        {
            using var tempScope = new TempDirScope();
            var decodeStarted = new ManualResetEventSlim(false);
            var staleTokenCancelled = false;

            var factory = new DelegateAudioPreviewBackendFactory(
                "Mock",
                "Failed",
                static _ => true,
                () => new FakeAudioBackend("Mock"));

            using var service = new AudioPlaybackService(new[] { factory }, tempDirectory: tempScope.Path);
            var wav = BuildPcm16Wav(8000, 1, 1500);

            var taskSlow = service.LoadAsync("slow", token =>
            {
                decodeStarted.Set();
                try
                {
                    Task.Delay(TimeSpan.FromSeconds(5), token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    staleTokenCancelled = true;
                    throw;
                }
                return new AudioPreviewSource(wav, ".wav", 1500, "Slow");
            });

            Assert.True(decodeStarted.Wait(TimeSpan.FromSeconds(5)));

            var taskFast = service.LoadAsync("fast", _ => new AudioPreviewSource(wav, ".wav", 1500, "Fast"));

            var slowResult = await taskSlow;
            var fastResult = await taskFast;

            Assert.True(staleTokenCancelled);
            Assert.Equal(AudioPreviewResultStatus.Cancelled, slowResult.Status);
            Assert.Equal(AudioPreviewResultStatus.Ready, fastResult.Status);
            Assert.Equal("fast", service.CurrentAssetId);

            service.Reset();
            await WaitUntilAsync(() => Directory.GetFiles(tempScope.Path).Length == 0, TimeSpan.FromSeconds(3));
            Assert.Empty(Directory.GetFiles(tempScope.Path));
        }

        [Fact]
        public async Task AudioPlaybackService_VolumeSeekTransportAndEndReachedGuard_WorkCorrectly()
        {
            using var tempScope = new TempDirScope();
            FakeAudioBackend? backend = null;
            var factory = new DelegateAudioPreviewBackendFactory(
                "Mock",
                "Failed",
                static _ => true,
                () => backend = new FakeAudioBackend("Mock"));

            var uiDispatchCount = 0;
            var endedCount = 0;
            var logs = new List<string>();
            using var service = new AudioPlaybackService(
                new[] { factory },
                dispatchToUi: action =>
                {
                    uiDispatchCount++;
                    action();
                },
                tempDirectory: tempScope.Path,
                log: logs.Add);
            service.PlaybackEnded += (_, _) => endedCount++;

            var wav = BuildPcm16Wav(8000, 1, 10000);
            var loadResult = await service.LoadAsync("track", _ => new AudioPreviewSource(wav, ".wav", 10000, "WAV"));
            Assert.Equal(AudioPreviewResultStatus.Ready, loadResult.Status);
            Assert.NotNull(backend);

            var playResult = await service.PlayFromStartAsync();
            Assert.Equal(AudioPreviewResultStatus.Started, playResult.Status);
            Assert.True(service.IsPlaying);

            // Volume clamping & forwarding.
            service.SetVolume(150);
            Assert.Equal(100, service.Volume);
            Assert.Equal(100, backend!.LastVolume);
            service.SetVolume(-10);
            Assert.Equal(0, service.Volume);
            Assert.Equal(0, backend.LastVolume);
            service.SetVolume(55);
            Assert.Equal(55, backend.LastVolume);

            // Seek clamping & ms->seconds conversion.
            service.Seek(-500);
            Assert.Equal(0.0, backend.LastSeekSeconds, precision: 3);
            service.Seek(4500);
            Assert.Equal(4.5, backend.LastSeekSeconds, precision: 3);
            service.Seek(999999);
            Assert.Equal(10.0, backend.LastSeekSeconds, precision: 3);

            // Pause / Resume / Poll / RestartForLoop / Stop.
            service.Pause();
            Assert.False(service.IsPlaying);
            service.Resume();
            Assert.True(service.IsPlaying);
            Assert.Equal(55, backend.LastVolume);

            backend.PositionSeconds = 2.0;
            var poll = service.Poll();
            Assert.True(poll.HasBackend);
            Assert.Equal(2000, poll.PositionMs);
            Assert.Equal(10000, poll.DurationMs);
            Assert.True(poll.IsPlaying);

            // Premature EndReached at 2s of 10s must be ignored.
            backend.RaiseEndReached();
            Assert.Equal(0, endedCount);
            Assert.Contains(logs, l => l.Contains("Ignored premature audio end", StringComparison.Ordinal));

            // Legitimate EndReached within tolerance of 10s must fire PlaybackEnded via UI dispatcher.
            backend.PositionSeconds = 9.0;
            backend.RaiseEndReached();
            Assert.Equal(1, endedCount);
            Assert.True(uiDispatchCount >= 2);

            service.RestartForLoop();
            Assert.Equal(1, backend.RestartCalls);

            service.Stop();
            Assert.Equal(1, backend.StopCalls);

            // After Reset, late EndReached from old backend is ignored.
            var oldBackend = backend;
            service.Reset();
            oldBackend.RaiseEndReached();
            Assert.Equal(1, endedCount);
        }

        [Fact]
        public async Task AudioPlaybackService_NonSeekableBackend_IgnoresSeekCalls()
        {
            using var tempScope = new TempDirScope();
            FakeAudioBackend? backend = null;
            var factory = new DelegateAudioPreviewBackendFactory(
                "System",
                "Failed",
                static _ => true,
                () => backend = new FakeAudioBackend("System") { CanSeek = false });

            using var service = new AudioPlaybackService(new[] { factory }, tempDirectory: tempScope.Path);
            var wav = BuildPcm16Wav(8000, 1, 5000);
            await service.LoadAsync("linux-clip", _ => new AudioPreviewSource(wav, ".wav", 5000, "WAV"));

            Assert.False(service.CanSeek);
            service.Seek(2500);
            Assert.Equal(0, backend!.SeekCalls);
        }

        [Fact]
        public async Task AudioPlaybackService_UnsupportedOrRawFsbMedia_ReturnsUnsupportedAndDeletesTempFile()
        {
            using var tempScope = new TempDirScope();
            var factories = AudioPlaybackService.CreateDefaultFactories(
                getOrCreatePcmPlayer: () => throw new InvalidOperationException("Should not be created for raw FSB"),
                pcmLoadGate: new SemaphoreSlim(1, 1),
                createFFmpegPlayer: () => throw new InvalidOperationException("Should not hand raw FSB to FFmpeg"),
                isWindowsOverride: true,
                isLinuxOverride: false);

            using var service = new AudioPlaybackService(factories, tempDirectory: tempScope.Path);
            var rawFsb = Encoding.ASCII.GetBytes("FSB5" + new string('\0', 64));

            var result = await service.LoadAsync("raw-fsb", _ => new AudioPreviewSource(rawFsb, ".fsb", 1000, "FSB"));

            Assert.Equal(AudioPreviewResultStatus.Unsupported, result.Status);
            Assert.Contains("FSB", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(service.ActiveBackend);
            Assert.Null(service.CurrentTempPath);

            await WaitUntilAsync(() => Directory.GetFiles(tempScope.Path).Length == 0, TimeSpan.FromSeconds(3));
            Assert.Empty(Directory.GetFiles(tempScope.Path));
        }

        [Fact]
        public void AudioPlaybackService_CreateDefaultFactories_SelectsExpectedPlatformStrategies()
        {
            var gate = new SemaphoreSlim(1, 1);
            var wavMedia = new AudioPreviewMedia("clip.wav", ".wav", 1000, "WAV");
            var mp3Media = new AudioPreviewMedia("clip.mp3", ".mp3", 1000, "MP3");
            var fsbMedia = new AudioPreviewMedia("clip.fsb", ".fsb", 1000, "FSB");

            var winFactories = AudioPlaybackService.CreateDefaultFactories(
                () => throw new NotSupportedException(),
                gate,
                () => null,
                isWindowsOverride: true,
                isLinuxOverride: false);
            Assert.Equal(new[] { "WinMM PCM", "FFmpeg" }, winFactories.Select(f => f.Name));
            Assert.True(winFactories[0].CanHandle(wavMedia));
            Assert.False(winFactories[0].CanHandle(mp3Media));
            Assert.False(winFactories[0].CanHandle(fsbMedia));
            Assert.True(winFactories[1].CanHandle(wavMedia));
            Assert.True(winFactories[1].CanHandle(mp3Media));
            Assert.False(winFactories[1].CanHandle(fsbMedia));

            var linuxFactories = AudioPlaybackService.CreateDefaultFactories(
                () => throw new NotSupportedException(),
                gate,
                () => null,
                isWindowsOverride: false,
                isLinuxOverride: true);
            Assert.Equal(new[] { "System audio player" }, linuxFactories.Select(f => f.Name));
            Assert.True(linuxFactories[0].CanHandle(wavMedia));
            Assert.False(linuxFactories[0].CanHandle(fsbMedia));

            var macFactories = AudioPlaybackService.CreateDefaultFactories(
                () => throw new NotSupportedException(),
                gate,
                () => null,
                isWindowsOverride: false,
                isLinuxOverride: false);
            Assert.Equal(new[] { "FFmpeg" }, macFactories.Select(f => f.Name));
            Assert.True(macFactories[0].CanHandle(wavMedia));
            Assert.True(macFactories[0].CanHandle(mp3Media));
            Assert.False(macFactories[0].CanHandle(fsbMedia));
        }

        [Fact]
        public void AudioPreviewPreparer_Fsb5Pcm16_DecodesDirectlyToWavWithoutInvokingTranscoder()
        {
            using var tempScope = new TempDirScope();
            var fsb5 = BuildMinimalFsb5Pcm16(sampleRateId: 8 /* 44100 Hz */, sampleCount: 4410); // 100ms mono PCM16
            var transcoderCalled = false;

            var prepared = AudioPreviewPreparer.PrepareRawAudioData(
                fsb5,
                fallbackExtension: ".fsb",
                metadataDurationMs: 0,
                isLinux: false,
                preferTranscode: false,
                cancellationToken: CancellationToken.None,
                tempDirectory: tempScope.Path,
                transcoder: (_, _, _) =>
                {
                    transcoderCalled = true;
                    return false;
                });

            Assert.NotNull(prepared);
            Assert.False(transcoderCalled);
            Assert.Equal(".wav", prepared!.Extension);
            Assert.Equal("FSB5 -> WAV (PCM)", prepared.SourceDescription);
            Assert.Equal(100, prepared.DurationMs);
            Assert.True(prepared.Data.Length > 44);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(prepared.Data, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(prepared.Data, 8, 4));
            Assert.Empty(Directory.GetFiles(tempScope.Path));
        }

        [Fact]
        public void AudioPreviewPreparer_UndecodableFsb5_TranscodesAndCleansIntermediateTempFiles()
        {
            using var tempScope = new TempDirScope();
            // Codec 15 = Vorbis (UnityAudioDecoder returns null -> falls back to transcoder).
            var fsb5Vorbis = BuildMinimalFsb5HeaderOnly(UnityAudioCodec.Vorbis);
            var expectedWav = BuildPcm16Wav(8000, 1, 500);
            var transcoderCalled = false;

            var prepared = AudioPreviewPreparer.PrepareRawAudioData(
                fsb5Vorbis,
                fallbackExtension: ".fsb",
                metadataDurationMs: 0,
                isLinux: false,
                preferTranscode: false,
                cancellationToken: CancellationToken.None,
                tempDirectory: tempScope.Path,
                transcoder: (inputPath, outputPath, _) =>
                {
                    transcoderCalled = true;
                    Assert.True(File.Exists(inputPath));
                    Assert.EndsWith(".fsb", inputPath, StringComparison.OrdinalIgnoreCase);
                    File.WriteAllBytes(outputPath, expectedWav);
                    return true;
                });

            Assert.True(transcoderCalled);
            Assert.NotNull(prepared);
            Assert.Equal(".wav", prepared!.Extension);
            Assert.Equal("FSB -> WAV (Transcoded)", prepared.SourceDescription);
            Assert.Equal(500, prepared.DurationMs);
            // Both intermediate transcode files must already be deleted in finally.
            Assert.Empty(Directory.GetFiles(tempScope.Path));
        }

        [Fact]
        public void AudioPreviewPreparer_DetectExtensionAndWaveDuration_HandlesFormatsAndMalformedData()
        {
            Assert.Equal(".fsb", AudioPreviewPreparer.DetectAudioExtension(Encoding.ASCII.GetBytes("FSB5xxxx"), ".bin"));
            Assert.Equal(".mp3", AudioPreviewPreparer.DetectAudioExtension(new byte[] { 0x49, 0x44, 0x33, 0x03 }, ".bin"));
            Assert.Equal(".mp3", AudioPreviewPreparer.DetectAudioExtension(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, ".bin"));
            Assert.Equal(".ogg", AudioPreviewPreparer.DetectAudioExtension(Encoding.ASCII.GetBytes("OggS\0\0"), ".bin"));
            Assert.Equal(".wav", AudioPreviewPreparer.DetectAudioExtension(Encoding.ASCII.GetBytes("RIFF\0\0\0\0"), ".bin"));
            Assert.Equal(".flac", AudioPreviewPreparer.DetectAudioExtension(Encoding.ASCII.GetBytes("fLaC\0\0"), ".bin"));
            Assert.Equal(".bin", AudioPreviewPreparer.DetectAudioExtension(new byte[] { 1, 2 }, ".bin"));

            var wav = BuildPcm16Wav(sampleRate: 16000, channels: 2, durationMs: 1250);
            Assert.Equal(1250, AudioPreviewPreparer.TryGetWaveDurationMs(wav));
            Assert.Equal(0, AudioPreviewPreparer.TryGetWaveDurationMs(Array.Empty<byte>()));
            Assert.Equal(0, AudioPreviewPreparer.TryGetWaveDurationMs(Encoding.ASCII.GetBytes("NOT_A_WAV_FILE_AT_ALL_012345678901234567890123")));

            Assert.False(AudioPreviewPreparer.ShouldTranscodeAudioForPreview(".wav", preferTranscode: false));
            Assert.True(AudioPreviewPreparer.ShouldTranscodeAudioForPreview(".wav", preferTranscode: true));
            Assert.True(AudioPreviewPreparer.ShouldTranscodeAudioForPreview(".fsb", preferTranscode: false));
            Assert.True(AudioPreviewPreparer.ShouldTranscodeAudioForPreview(".ogg", preferTranscode: false));
        }

        [Fact]
        public async Task MediaTempFiles_DeleteInBackground_RetriesLockedFileUntilReleased()
        {
            using var tempScope = new TempDirScope();
            var path = MediaTempFiles.CreateUniquePath(tempScope.Path, "lock_test_", ".wav");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

            Task deleteTask;
            using (var lockedStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                deleteTask = MediaTempFiles.DeleteInBackground(path, attempts: 20, retryDelayMs: 25);
                await Task.Delay(60);
            }

            await deleteTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(path));
        }

        #endregion

        #region Test Helpers

        private sealed class FakeAudioBackend : IAudioPreviewBackend
        {
            public FakeAudioBackend(string name)
            {
                BackendName = name;
            }

            public string BackendName { get; }
            public string PlaybackFailureMessage => $"{BackendName} playback failed";
            public bool CanSeek { get; set; } = true;
            public bool IsPlaying { get; private set; }
            public bool IsEnded { get; set; }
            public double PositionSeconds { get; set; }
            public double DurationSeconds { get; private set; }

            public bool LoadResult { get; set; } = true;
            public bool ThrowOnLoad { get; set; }
            public bool PlayFromStartResult { get; set; } = true;
            public Func<AudioPreviewMedia, CancellationToken, Task<bool>>? OnLoadAsync { get; set; }

            public int LastVolume { get; private set; } = -1;
            public double LastSeekSeconds { get; private set; } = -1;
            public int SeekCalls { get; private set; }
            public int PlayFromStartCalls { get; private set; }
            public int StopCalls { get; private set; }
            public int RestartCalls { get; private set; }
            public int DisposeCalls { get; private set; }

            public event EventHandler? EndReached;

            public async Task<bool> LoadAsync(AudioPreviewMedia media, CancellationToken cancellationToken)
            {
                DurationSeconds = media.DurationMs / 1000.0;
                if (ThrowOnLoad)
                {
                    throw new InvalidOperationException("Simulated backend load failure");
                }

                if (OnLoadAsync != null)
                {
                    return await OnLoadAsync(media, cancellationToken);
                }

                return LoadResult;
            }

            public Task<bool> PlayFromStartAsync(CancellationToken cancellationToken)
            {
                PlayFromStartCalls++;
                if (!PlayFromStartResult)
                {
                    return Task.FromResult(false);
                }

                PositionSeconds = 0;
                IsPlaying = true;
                IsEnded = false;
                return Task.FromResult(true);
            }

            public void Play() => IsPlaying = true;

            public void Pause() => IsPlaying = false;

            public void Stop()
            {
                StopCalls++;
                IsPlaying = false;
                PositionSeconds = 0;
            }

            public void Restart()
            {
                RestartCalls++;
                PositionSeconds = 0;
                IsPlaying = true;
                IsEnded = false;
            }

            public void Seek(double seconds)
            {
                SeekCalls++;
                LastSeekSeconds = seconds;
                PositionSeconds = seconds;
            }

            public void SetVolume(int volume) => LastVolume = volume;

            public void RaiseEndReached() => EndReached?.Invoke(this, EventArgs.Empty);

            public void Dispose()
            {
                DisposeCalls++;
                IsPlaying = false;
            }
        }

        private sealed class TempDirScope : IDisposable
        {
            public TempDirScope()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "AssetStudio_MediaPlaybackTests_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!predicate() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }

        private static byte[] BuildPcm16Wav(int sampleRate, int channels, int durationMs)
        {
            var blockAlign = channels * 2;
            var byteRate = sampleRate * blockAlign;
            var dataSize = (int)(byteRate * (durationMs / 1000.0));
            dataSize -= dataSize % blockAlign;

            using var ms = new MemoryStream(44 + dataSize);
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)blockAlign);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            bw.Write(new byte[dataSize]);
            return ms.ToArray();
        }

        private static byte[] BuildMinimalFsb5Pcm16(uint sampleRateId, uint sampleCount)
        {
            var payload = new byte[sampleCount * 2];
            return BuildFsb5(UnityAudioCodec.Pcm16, sampleRateId, sampleCount, payload);
        }

        private static byte[] BuildMinimalFsb5HeaderOnly(UnityAudioCodec codec)
        {
            return BuildFsb5(codec, sampleRateId: 8, sampleCount: 1024, payload: new byte[64]);
        }

        private static byte[] BuildFsb5(UnityAudioCodec codec, uint sampleRateId, uint sampleCount, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
            // FSB5 header (version 1 -> 60 bytes)
            bw.Write(Encoding.ASCII.GetBytes("FSB5"));
            bw.Write(1u); // version
            bw.Write(1u); // numSamples
            bw.Write(8u); // sizeOfSampleHeaders
            bw.Write(0u); // sizeOfNameTable
            bw.Write((uint)payload.Length); // sizeOfData
            bw.Write((uint)codec);
            bw.Write(new byte[60 - 28]); // padding to 60 bytes

            // Sample header (8 bytes): hasChunks=0, freqId (4 bits), ch=0 (mono, 2 bits), offset=0 (27 bits), sampleCount (30 bits)
            ulong sampleHeader = ((ulong)(sampleRateId & 0xF) << 1) | ((ulong)sampleCount << 34);
            bw.Write(sampleHeader);
            bw.Write(payload);
            return ms.ToArray();
        }

        #endregion
    }
}
