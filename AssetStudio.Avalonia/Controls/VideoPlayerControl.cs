using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FFmpegVideoPlayer.Core;
using Avalonia.FFmpegVideoPlayer;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Logger = AssetStudio.Logger;

namespace AssetStudio.Avalonia.Controls
{
    public class VideoPlayerControl : UserControl
    {
        internal static readonly VideoPlayerTeardownQueue SharedTeardownQueue = new();

        private sealed class PlayerHandle
        {
            public PlayerHandle(FFmpegMediaPlayer player, long generation)
            {
                Player = player;
                Generation = generation;
            }

            public FFmpegMediaPlayer Player { get; }
            public long Generation { get; }

            // 0 = Opening, 1 = Open, 2 = ReleaseRequested
            public int State;
            public volatile bool IsPlayTransitionPending;
            public volatile bool IsPauseTransitionPending;
            public volatile bool IsStopTransitionPending;
            public Task? PendingStopTask;
        }

        private readonly VideoPlaybackTimelineTracker _timeline = new();
        private PlayerHandle? _activeHandle;
        private long _playerGeneration;
        private IVideoRenderer? _videoRenderer;
        private Border? _videoBorder;
        private string? _currentMediaPath;
        private volatile bool _hasMediaLoaded;
        private bool _isInitialized;
        private int _previousVolume = 100;
        private int _volumeCached = 100;
        private Func<int, int, IAudioPlayer?>? _audioPlayerFactoryFunc;
        private bool _autoPlayCached;
        private bool _frameRenderErrorLogged;
        private float _pendingSeekPercent = float.NaN;
        private long _pendingSeekGeneration;
        private int _seekWorkerRunning;

        public static readonly StyledProperty<int> VolumeProperty =
            AvaloniaProperty.Register<VideoPlayerControl, int>(nameof(Volume), defaultValue: 100);

        public static readonly StyledProperty<bool> AutoPlayProperty =
            AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(AutoPlay), defaultValue: false);

        public static readonly StyledProperty<bool> ShowControlsProperty =
            AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(ShowControls), defaultValue: true);

        public static readonly StyledProperty<bool> ShowOpenButtonProperty =
            AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(ShowOpenButton), defaultValue: true);

        public static readonly StyledProperty<string?> SourceProperty =
            AvaloniaProperty.Register<VideoPlayerControl, string?>(nameof(Source), defaultValue: null);

        public static readonly StyledProperty<IBrush?> ControlPanelBackgroundProperty =
            AvaloniaProperty.Register<VideoPlayerControl, IBrush?>(nameof(ControlPanelBackground), defaultValue: null);

        public static readonly StyledProperty<IBrush?> VideoBackgroundProperty =
            AvaloniaProperty.Register<VideoPlayerControl, IBrush?>(nameof(VideoBackground), defaultValue: null);

        public static readonly StyledProperty<Stretch> VideoStretchProperty =
            AvaloniaProperty.Register<VideoPlayerControl, Stretch>(nameof(VideoStretch), defaultValue: Stretch.Uniform);

        public static readonly StyledProperty<bool> EnableKeyboardShortcutsProperty =
            AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(EnableKeyboardShortcuts), defaultValue: true);

        public static readonly StyledProperty<Func<int, int, IAudioPlayer?>?> AudioPlayerFactoryProperty =
            AvaloniaProperty.Register<VideoPlayerControl, Func<int, int, IAudioPlayer?>?>(nameof(AudioPlayerFactory), defaultValue: null);

        public static readonly StyledProperty<IIconProvider?> IconProviderProperty =
            AvaloniaProperty.Register<VideoPlayerControl, IIconProvider?>(nameof(IconProvider), defaultValue: null);

        public static readonly StyledProperty<VideoRenderingMode> RenderingModeProperty =
            AvaloniaProperty.Register<VideoPlayerControl, VideoRenderingMode>(nameof(RenderingMode), defaultValue: VideoRenderingMode.Cpu);

        public static readonly StyledProperty<bool> ShowFullscreenButtonProperty =
            AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(ShowFullscreenButton), defaultValue: false);

        public static readonly StyledProperty<bool> IsFullscreenProperty =
            AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(IsFullscreen), defaultValue: false);

        private FFmpegMediaPlayer? ActivePlayer
        {
            get
            {
                var handle = Volatile.Read(ref _activeHandle);
                return handle != null && Volatile.Read(ref handle.State) == 1 ? handle.Player : null;
            }
        }

        public int Volume
        {
            get => GetValue(VolumeProperty);
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                _volumeCached = clamped;
                SetValue(VolumeProperty, clamped);
                var player = ActivePlayer;
                if (player != null)
                {
                    player.Volume = clamped;
                }
            }
        }

        public bool AutoPlay
        {
            get => GetValue(AutoPlayProperty);
            set => SetValue(AutoPlayProperty, value);
        }

        public bool ShowControls
        {
            get => GetValue(ShowControlsProperty);
            set => SetValue(ShowControlsProperty, value);
        }

        public bool ShowOpenButton
        {
            get => GetValue(ShowOpenButtonProperty);
            set => SetValue(ShowOpenButtonProperty, value);
        }

        public string? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public IBrush? ControlPanelBackground
        {
            get => GetValue(ControlPanelBackgroundProperty);
            set => SetValue(ControlPanelBackgroundProperty, value);
        }

        public IBrush? VideoBackground
        {
            get => GetValue(VideoBackgroundProperty);
            set => SetValue(VideoBackgroundProperty, value);
        }

        public Stretch VideoStretch
        {
            get => GetValue(VideoStretchProperty);
            set => SetValue(VideoStretchProperty, value);
        }

        public bool EnableKeyboardShortcuts
        {
            get => GetValue(EnableKeyboardShortcutsProperty);
            set => SetValue(EnableKeyboardShortcutsProperty, value);
        }

        public Func<int, int, IAudioPlayer?>? AudioPlayerFactory
        {
            get => GetValue(AudioPlayerFactoryProperty);
            set => SetValue(AudioPlayerFactoryProperty, value);
        }

        public IIconProvider? IconProvider
        {
            get => GetValue(IconProviderProperty);
            set => SetValue(IconProviderProperty, value);
        }

        public VideoRenderingMode RenderingMode
        {
            get => GetValue(RenderingModeProperty);
            set => SetValue(RenderingModeProperty, value);
        }

        public bool ShowFullscreenButton
        {
            get => GetValue(ShowFullscreenButtonProperty);
            set => SetValue(ShowFullscreenButtonProperty, value);
        }

        public bool IsFullscreen
        {
            get => GetValue(IsFullscreenProperty);
            set => SetValue(IsFullscreenProperty, value);
        }

        public string? CurrentMediaPath => _currentMediaPath;
        public bool HasMediaLoaded => _hasMediaLoaded;

        public bool IsPlaying
        {
            get
            {
                var handle = Volatile.Read(ref _activeHandle);
                if (handle == null || Volatile.Read(ref handle.State) != 1 || handle.IsPauseTransitionPending)
                {
                    return false;
                }

                return handle.IsPlayTransitionPending || handle.Player.IsPlaying;
            }
        }

        public long Position
        {
            get
            {
                var player = ActivePlayer;
                if (player == null)
                {
                    return 0L;
                }

                var fallbackMs = VideoPreviewService.TryGetPlayerPositionMs(player);
                return _timeline.GetEffectivePositionMs(fallbackMs, Environment.TickCount64);
            }
        }

        public long Duration => ActivePlayer?.Length ?? 0L;

        public void SetFallbackDuration(long durationMs)
        {
            if (durationMs > 0)
            {
                _timeline.SetDurationMs(durationMs);
            }
        }

        public event EventHandler? PlaybackStarted;
        public event EventHandler<MediaOpenedEventArgs>? MediaOpened;
        public event EventHandler? PlaybackPaused;
        public event EventHandler? PlaybackStopped;
        public event EventHandler? MediaEnded;
        public event EventHandler<long>? PositionUpdated;

        public VideoPlayerControl()
        {
            _videoBorder = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Content = _videoBorder;

            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;
            PropertyChanged += OnPropertyChanged;
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            InitializePlayer();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Cleanup();
        }

        private FFmpegMediaPlayer CreateMediaPlayerInstance()
        {
            Func<int, int, IAudioPlayer?> func = _audioPlayerFactoryFunc ??
                ((sr, ch) =>
                {
                    try
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            return new WinMmAudioPlayer(sr, ch);
                        }
                        return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                        }
                        catch (Exception innerEx)
                        {
                            Logger.Warning($"Failed to initialize audio player for video: WinMm failed with ({ex.Message}), OpenTK failed with ({innerEx.Message})");
                            return null;
                        }
                    }
                });

            var mediaPlayer = new FFmpegMediaPlayer(action =>
            {
                Dispatcher.UIThread.Post(action);
            }, func);

            // Disable FFmpegMediaPlayer's built-in disk JSON logger so it never allocates JSON per audio packet
            // or blocks player._lock writing multi-MB debug/player-log-*.json files.
            VideoPreviewService.DisablePlayerDiskLogger(mediaPlayer);

            mediaPlayer.PositionChanged += OnPositionChanged;
            mediaPlayer.LengthChanged += OnLengthChanged;
            mediaPlayer.Playing += OnPlaying;
            mediaPlayer.Paused += OnPaused;
            mediaPlayer.Stopped += OnStopped;
            mediaPlayer.EndReached += OnEndReached;
            mediaPlayer.FrameReady += OnFrameReady;

            return mediaPlayer;
        }

        private void UnsubscribeEvents(FFmpegMediaPlayer player)
        {
            player.PositionChanged -= OnPositionChanged;
            player.LengthChanged -= OnLengthChanged;
            player.Playing -= OnPlaying;
            player.Paused -= OnPaused;
            player.Stopped -= OnStopped;
            player.EndReached -= OnEndReached;
            player.FrameReady -= OnFrameReady;
        }

        private void ReleaseHandle(PlayerHandle? handle, Action? onTeardownCompleted = null)
        {
            if (handle == null)
            {
                if (onTeardownCompleted != null)
                {
                    _ = SharedTeardownQueue.EnqueueTeardownAsync(static () => { }, onTeardownCompleted);
                }
                return;
            }

            handle.IsPlayTransitionPending = false;
            handle.IsPauseTransitionPending = false;

            if (Interlocked.Exchange(ref handle.State, 2) == 2)
            {
                if (onTeardownCompleted != null)
                {
                    _ = SharedTeardownQueue.EnqueueTeardownAsync(static () => { }, onTeardownCompleted);
                }
                return;
            }

            UnsubscribeEvents(handle.Player);
            _ = SharedTeardownQueue.EnqueueTeardownAsync(
                () => VideoPreviewService.SafeDisposePlayer(handle.Player),
                onTeardownCompleted);
        }

        private void InitializePlayer()
        {
            if (_isInitialized)
                return;

            try
            {
                FFmpegRuntime.Initialize();

                _audioPlayerFactoryFunc = AudioPlayerFactory;
                _autoPlayCached = AutoPlay;
                _volumeCached = Math.Clamp(Volume, 0, 100);

                _isInitialized = true;
                SetupVideoRenderer();

                if (_videoBorder != null)
                {
                    _videoBorder.Background = VideoBackground;
                }

                if (!string.IsNullOrEmpty(Source))
                {
                    _ = OpenAsync(Source);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize video player", ex);
            }
        }

        private void SetupVideoRenderer()
        {
            if (_videoBorder == null)
                return;

            if (_videoRenderer != null)
            {
                (_videoRenderer as IDisposable)?.Dispose();
                _videoRenderer = null;
            }

            _videoBorder.Child = null;
            Control? rendererControl = null;

            try
            {
                switch (RenderingMode)
                {
                    case VideoRenderingMode.Cpu:
                        _videoRenderer = new CpuVideoRenderer();
                        rendererControl = (Control)_videoRenderer;
                        break;
                    case VideoRenderingMode.OpenGL:
                        try
                        {
                            _videoRenderer = new OpenGLVideoRenderer();
                            rendererControl = (Control)_videoRenderer;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"OpenGL video renderer failed to initialize, falling back to CPU: {ex.Message}");
                            _videoRenderer = new CpuVideoRenderer();
                            rendererControl = (Control)_videoRenderer;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to setup video renderer, falling back to CPU: {ex.Message}");
                _videoRenderer = new CpuVideoRenderer();
                rendererControl = (Control)_videoRenderer;
            }

            if (rendererControl != null)
            {
                rendererControl.HorizontalAlignment = HorizontalAlignment.Stretch;
                rendererControl.VerticalAlignment = VerticalAlignment.Stretch;
                _videoBorder.Child = rendererControl;

                if (_videoRenderer is CpuVideoRenderer cpuVideoRenderer)
                {
                    cpuVideoRenderer.Stretch = VideoStretch;
                }
                else if (_videoRenderer is OpenGLVideoRenderer openGLVideoRenderer)
                {
                    openGLVideoRenderer.Stretch = VideoStretch;
                }
            }
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == SourceProperty)
            {
                string? text = e.NewValue as string;
                if (!string.IsNullOrEmpty(text) && _isInitialized)
                {
                    _ = OpenAsync(text);
                }
            }
            else if (e.Property == VideoBackgroundProperty)
            {
                if (_videoBorder != null)
                {
                    _videoBorder.Background = e.NewValue as IBrush;
                }
            }
            else if (e.Property == VideoStretchProperty)
            {
                if (e.NewValue is Stretch stretch)
                {
                    if (_videoRenderer is CpuVideoRenderer cpuVideoRenderer)
                    {
                        cpuVideoRenderer.Stretch = stretch;
                    }
                    else if (_videoRenderer is OpenGLVideoRenderer openGLVideoRenderer)
                    {
                        openGLVideoRenderer.Stretch = stretch;
                    }
                }
            }
            else if (e.Property == RenderingModeProperty)
            {
                SetupVideoRenderer();
            }
            else if (e.Property == AutoPlayProperty)
            {
                _autoPlayCached = (bool)(e.NewValue ?? false);
            }
            else if (e.Property == AudioPlayerFactoryProperty)
            {
                _audioPlayerFactoryFunc = e.NewValue as Func<int, int, IAudioPlayer?>;
            }
        }

        private bool IsSenderActivePlayer(object? sender)
        {
            var handle = Volatile.Read(ref _activeHandle);
            return handle != null
                && Volatile.Read(ref handle.State) == 1
                && !handle.IsStopTransitionPending
                && ReferenceEquals(sender, handle.Player);
        }

        private void OnPlaying(object? sender, EventArgs e)
        {
            if (!IsSenderActivePlayer(sender))
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsSenderActivePlayer(sender))
                    {
                        PlaybackStarted?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
        }

        private void OnPaused(object? sender, EventArgs e)
        {
            if (!IsSenderActivePlayer(sender))
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                PlaybackPaused?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsSenderActivePlayer(sender))
                    {
                        PlaybackPaused?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
        }

        private void OnStopped(object? sender, EventArgs e)
        {
            if (!IsSenderActivePlayer(sender))
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
                _videoRenderer?.Clear();
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsSenderActivePlayer(sender))
                    {
                        PlaybackStopped?.Invoke(this, EventArgs.Empty);
                        _videoRenderer?.Clear();
                    }
                });
            }
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            if (!IsSenderActivePlayer(sender))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (IsSenderActivePlayer(sender))
                {
                    MediaEnded?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
        {
            if (!IsSenderActivePlayer(sender) || sender is not FFmpegMediaPlayer player)
            {
                return;
            }

            var lengthMs = player.Length;
            if (lengthMs > 0)
            {
                _timeline.SetDurationMs(lengthMs);
            }

            double reportedMs = lengthMs > 0
                ? Math.Max(0.0, e.Position * (double)lengthMs)
                : VideoPreviewService.TryGetPlayerPositionMs(player);

            if (_timeline.OnPositionChangedFromPlayer(reportedMs, IsPlaying, out var effectiveMs))
            {
                PositionUpdated?.Invoke(this, effectiveMs);
            }
        }

        private void OnLengthChanged(object? sender, LengthChangedEventArgs e)
        {
            if (e.Length > 0 && IsSenderActivePlayer(sender))
            {
                _timeline.SetDurationMs(e.Length);
            }
        }

        private void OnFrameReady(object? sender, FrameEventArgs e)
        {
            try
            {
                var handle = Volatile.Read(ref _activeHandle);
                if (handle == null
                    || Volatile.Read(ref handle.State) == 2
                    || handle.IsStopTransitionPending
                    || !ReferenceEquals(sender, handle.Player))
                {
                    return;
                }

                _videoRenderer?.RenderFrame(e.Data, e.Width, e.Height, e.Stride);

                if (Volatile.Read(ref handle.State) == 1)
                {
                    var fallbackMs = VideoPreviewService.TryGetPlayerPositionMs(handle.Player);
                    if (_timeline.OnFramePresented(fallbackMs, out var effectiveMs))
                    {
                        PositionUpdated?.Invoke(this, effectiveMs);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_frameRenderErrorLogged)
                {
                    _frameRenderErrorLogged = true;
                    Logger.Warning($"Failed to render video frame (suppressing future render errors): {ex.Message}");
                }
            }
            finally
            {
                e.Dispose();
            }
        }

        public Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path) || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            return Task.Run(() => OpenCore(path, cancellationToken), cancellationToken);
        }

        public void Open(string path)
        {
            _ = OpenCore(path, CancellationToken.None);
        }

        private bool OpenCore(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            _hasMediaLoaded = false;
            _currentMediaPath = null;
            _frameRenderErrorLogged = false;
            Volatile.Write(ref _pendingSeekPercent, float.NaN);
            _timeline.Reset(0L);

            var oldHandle = Interlocked.Exchange(ref _activeHandle, null);
            if (oldHandle != null)
            {
                ReleaseHandle(oldHandle);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var player = CreateMediaPlayerInstance();
            var generation = Interlocked.Increment(ref _playerGeneration);
            var handle = new PlayerHandle(player, generation);
            Volatile.Write(ref _activeHandle, handle);

            bool opened = false;
            try
            {
                if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref handle.State) == 0)
                {
                    opened = player.Open(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"FFmpegMediaPlayer.Open failed for '{path}': {ex.Message}");
                opened = false;
            }

            if (!opened || cancellationToken.IsCancellationRequested || Volatile.Read(ref handle.State) != 0)
            {
                Interlocked.CompareExchange(ref _activeHandle, null, handle);
                ReleaseHandle(handle);
                return false;
            }

            player.Volume = _volumeCached;

            try
            {
                if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref handle.State) == 0)
                {
                    player.DecodeFirstFrame();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"FFmpegMediaPlayer.DecodeFirstFrame failed for '{path}': {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested
                || !ReferenceEquals(Volatile.Read(ref _activeHandle), handle)
                || Interlocked.CompareExchange(ref handle.State, 1, 0) != 0)
            {
                Interlocked.CompareExchange(ref _activeHandle, null, handle);
                ReleaseHandle(handle);
                return false;
            }

            _currentMediaPath = path;
            _hasMediaLoaded = true;
            _timeline.OnMediaOpened(player.Length);

            Dispatcher.UIThread.Post(() =>
            {
                var current = Volatile.Read(ref _activeHandle);
                if (ReferenceEquals(current, handle) && Volatile.Read(ref handle.State) == 1)
                {
                    MediaOpened?.Invoke(this, new MediaOpenedEventArgs(path));
                }
            });

            if (_autoPlayCached)
            {
                Play();
            }

            return true;
        }

        public void OpenUri(Uri uri)
        {
            if (uri.IsFile)
            {
                Open(uri.LocalPath);
            }
            else
            {
                Open(uri.ToString());
            }
        }

        public void Play()
        {
            var handle = Volatile.Read(ref _activeHandle);
            if (handle == null || Volatile.Read(ref handle.State) != 1)
            {
                return;
            }

            handle.IsPauseTransitionPending = false;
            handle.IsPlayTransitionPending = true;
            var pendingStop = Volatile.Read(ref handle.PendingStopTask);

            _ = Task.Run(() =>
            {
                try
                {
                    if (pendingStop != null && !pendingStop.IsCompleted)
                    {
                        try
                        {
                            pendingStop.Wait(500);
                        }
                        catch
                        {
                        }
                    }

                    if (Volatile.Read(ref handle.State) == 1)
                    {
                        handle.IsStopTransitionPending = false;
                        handle.Player.Play();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error starting video playback: {ex.Message}");
                }
                finally
                {
                    handle.IsPlayTransitionPending = false;
                }
            });
        }

        public void Pause()
        {
            var handle = Volatile.Read(ref _activeHandle);
            if (handle == null || Volatile.Read(ref handle.State) != 1)
            {
                return;
            }

            handle.IsPlayTransitionPending = false;
            handle.IsPauseTransitionPending = true;

            _ = Task.Run(() =>
            {
                try
                {
                    if (Volatile.Read(ref handle.State) == 1)
                    {
                        handle.Player.Pause();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error pausing video playback: {ex.Message}");
                }
                finally
                {
                    handle.IsPauseTransitionPending = false;
                }
            });
        }

        public void Stop()
        {
            Volatile.Write(ref _pendingSeekPercent, float.NaN);
            var handle = Volatile.Read(ref _activeHandle);
            if (handle != null && Volatile.Read(ref handle.State) == 1)
            {
                handle.IsPlayTransitionPending = false;
                handle.IsPauseTransitionPending = false;
                handle.IsStopTransitionPending = true;
                _timeline.OnMediaOpened(handle.Player.Length);
                var stopTask = SharedTeardownQueue.EnqueueTeardownAsync(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref handle.State) == 1)
                        {
                            VideoPreviewService.SafeStopPlayer(handle.Player);
                        }
                    }
                    finally
                    {
                        handle.IsStopTransitionPending = false;
                    }
                });
                Volatile.Write(ref handle.PendingStopTask, stopTask);
            }
            else
            {
                _timeline.Reset(0L);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                _videoRenderer?.Clear();
            }
            else
            {
                Dispatcher.UIThread.Post(() => _videoRenderer?.Clear());
            }
        }

        /// <summary>
        /// Detaches the active media player immediately without blocking the UI thread, clears the rendered frame,
        /// and disposes the native FFmpeg format/codec contexts on the background teardown queue so the underlying
        /// temp file handle is closed before <paramref name="onTeardownCompleted"/> runs.
        /// </summary>
        public void ResetMedia(Action? onTeardownCompleted = null)
        {
            _hasMediaLoaded = false;
            _currentMediaPath = null;
            Volatile.Write(ref _pendingSeekPercent, float.NaN);
            _timeline.Reset(0L);
            Interlocked.Increment(ref _playerGeneration);

            var oldHandle = Interlocked.Exchange(ref _activeHandle, null);
            ReleaseHandle(oldHandle, onTeardownCompleted);

            if (Dispatcher.UIThread.CheckAccess())
            {
                _videoRenderer?.Clear();
            }
            else
            {
                Dispatcher.UIThread.Post(() => _videoRenderer?.Clear());
            }
        }

        public bool StepForward() => ActivePlayer?.StepForward() ?? false;
        public bool StepBackward() => ActivePlayer?.StepBackward() ?? false;

        public void TogglePlayPause()
        {
            if (IsPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void Seek(float positionPercent)
        {
            var handle = Volatile.Read(ref _activeHandle);
            if (handle == null || Volatile.Read(ref handle.State) != 1)
            {
                return;
            }

            var clamped = Math.Clamp(positionPercent, 0f, 1f);
            var effectiveDurationMs = handle.Player.Length > 0 ? handle.Player.Length : _timeline.DurationMs;
            if (effectiveDurationMs > 0)
            {
                _timeline.SetDurationMs(effectiveDurationMs);
            }
            var targetMs = effectiveDurationMs > 0
                ? (long)Math.Round(clamped * (double)effectiveDurationMs)
                : 0L;
            var seekGen = _timeline.BeginSeek(targetMs);

            Volatile.Write(ref _pendingSeekGeneration, seekGen);
            Volatile.Write(ref _pendingSeekPercent, clamped);
            if (Interlocked.CompareExchange(ref _seekWorkerRunning, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        var target = Interlocked.Exchange(ref _pendingSeekPercent, float.NaN);
                        if (float.IsNaN(target))
                        {
                            break;
                        }

                        var currentSeekGen = Volatile.Read(ref _pendingSeekGeneration);
                        var currentHandle = Volatile.Read(ref _activeHandle);
                        if (currentHandle == null || Volatile.Read(ref currentHandle.State) != 1)
                        {
                            break;
                        }

                        var pendingStop = Volatile.Read(ref currentHandle.PendingStopTask);
                        if (pendingStop != null && !pendingStop.IsCompleted)
                        {
                            try
                            {
                                pendingStop.Wait(500);
                            }
                            catch
                            {
                            }
                        }

                        if (Volatile.Read(ref currentHandle.State) != 1)
                        {
                            break;
                        }

                        try
                        {
                            currentHandle.IsStopTransitionPending = false;
                            VideoPreviewService.SafeSeekPlayer(currentHandle.Player, target);

                            // Post FIFO seek barrier BEFORE ShowFrameAtCurrentPosition so all stale pre-seek frames
                            // already queued on the UI dispatcher are drained before the barrier opens, while the
                            // seeked paused frame (or first post-seek playback frame) arrives after the barrier.
                            Dispatcher.UIThread.Post(() =>
                            {
                                _timeline.MarkSeekBarrierReached(currentSeekGen, Environment.TickCount64);
                            });

                            if (!currentHandle.Player.IsPlaying
                                && !currentHandle.IsPlayTransitionPending
                                && Volatile.Read(ref currentHandle.State) == 1)
                            {
                                currentHandle.Player.ShowFrameAtCurrentPosition();
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Error seeking video player: {ex.Message}");
                            Dispatcher.UIThread.Post(() =>
                            {
                                _timeline.MarkSeekBarrierReached(currentSeekGen, Environment.TickCount64);
                            });
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _seekWorkerRunning, 0);
                }
            });
        }

        public void ToggleMute()
        {
            if (ActivePlayer != null)
            {
                if (Volume > 0)
                {
                    _previousVolume = Volume;
                    Volume = 0;
                }
                else
                {
                    Volume = _previousVolume;
                }
            }
        }

        private void Cleanup()
        {
            ResetMedia();

            if (_videoRenderer != null)
            {
                (_videoRenderer as IDisposable)?.Dispose();
                _videoRenderer = null;
            }

            _isInitialized = false;
        }
    }
}
