using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FFmpegVideoPlayer.Core;
using Avalonia.FFmpegVideoPlayer;

namespace AssetStudio.Avalonia.Controls
{
    public class VideoPlayerControl : UserControl
    {
        private FFmpegMediaPlayer? _mediaPlayer;
        private IVideoRenderer? _videoRenderer;
        private Border? _videoBorder;
        private string? _currentMediaPath;
        private bool _hasMediaLoaded;
        private bool _isInitialized;
        private int _previousVolume = 100;

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

        public int Volume
        {
            get => GetValue(VolumeProperty);
            set
            {
                SetValue(VolumeProperty, Math.Clamp(value, 0, 100));
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Volume = value;
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

        public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

        public long Position
        {
            get
            {
                if (_mediaPlayer == null)
                    return 0L;
                return (long)(_mediaPlayer.Position * (float)_mediaPlayer.Length);
            }
        }

        public long Duration => _mediaPlayer?.Length ?? 0L;

        public event EventHandler? PlaybackStarted;
        public event EventHandler<MediaOpenedEventArgs>? MediaOpened;
        public event EventHandler? PlaybackPaused;
        public event EventHandler? PlaybackStopped;
        public event EventHandler? MediaEnded;

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

        private void InitializePlayer()
        {
            if (_isInitialized)
                return;

            try
            {
                if (!FFmpegInitializer.IsInitialized)
                {
                    FFmpegInitializer.Initialize(null, true, true);
                }

                Func<int, int, IAudioPlayer?> func = AudioPlayerFactory ?? 
                    ((sr, ch) => {
                        try
                        {
                            return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                        }
                        catch
                        {
                            return null;
                        }
                    });

                _mediaPlayer = new FFmpegMediaPlayer(action =>
                {
                    Dispatcher.UIThread.Post(action);
                }, func);

                _mediaPlayer.PositionChanged += OnPositionChanged;
                _mediaPlayer.LengthChanged += OnLengthChanged;
                _mediaPlayer.Playing += OnPlaying;
                _mediaPlayer.Paused += OnPaused;
                _mediaPlayer.Stopped += OnStopped;
                _mediaPlayer.EndReached += OnEndReached;
                _mediaPlayer.FrameReady += OnFrameReady;

                _isInitialized = true;
                SetupVideoRenderer();

                if (_videoBorder != null)
                {
                    _videoBorder.Background = VideoBackground;
                }

                if (!string.IsNullOrEmpty(Source))
                {
                    Open(Source);
                }
            }
            catch (Exception)
            {
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
                        catch
                        {
                            _videoRenderer = new CpuVideoRenderer();
                            rendererControl = (Control)_videoRenderer;
                        }
                        break;
                }
            }
            catch
            {
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
                    Open(text);
                    if (AutoPlay)
                    {
                        Play();
                    }
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
        }

        private void OnPlaying(object? sender, EventArgs e)
        {
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }

        private void OnPaused(object? sender, EventArgs e)
        {
            PlaybackPaused?.Invoke(this, EventArgs.Empty);
        }

        private void OnStopped(object? sender, EventArgs e)
        {
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
            _videoRenderer?.Clear();
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                MediaEnded?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
        {
        }

        private void OnLengthChanged(object? sender, LengthChangedEventArgs e)
        {
        }

        private void OnFrameReady(object? sender, FrameEventArgs e)
        {
            try
            {
                _videoRenderer?.RenderFrame(e.Data, e.Width, e.Height, e.Stride);
            }
            catch
            {
            }
            finally
            {
                e.Dispose();
            }
        }

        public void Open(string path)
        {
            if (_mediaPlayer == null)
                return;

            _hasMediaLoaded = false;
            if (_mediaPlayer.Open(path))
            {
                _currentMediaPath = path;
                _hasMediaLoaded = true;
                MediaOpened?.Invoke(this, new MediaOpenedEventArgs(path));
                _mediaPlayer.DecodeFirstFrame();
                if (AutoPlay)
                {
                    _mediaPlayer.Play();
                }
            }
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

        public void Play() => _mediaPlayer?.Play();
        public void Pause() => _mediaPlayer?.Pause();
        
        public void Stop()
        {
            _mediaPlayer?.Stop();
            _videoRenderer?.Clear();
        }

        public bool StepForward() => _mediaPlayer?.StepForward() ?? false;
        public bool StepBackward() => _mediaPlayer?.StepBackward() ?? false;

        public void TogglePlayPause()
        {
            if (_mediaPlayer != null)
            {
                if (_mediaPlayer.IsPlaying)
                    _mediaPlayer.Pause();
                else
                    _mediaPlayer.Play();
            }
        }

        public void Seek(float positionPercent) => _mediaPlayer?.Seek(positionPercent);

        public void ToggleMute()
        {
            if (_mediaPlayer != null)
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
            if (_mediaPlayer != null)
            {
                _mediaPlayer.PositionChanged -= OnPositionChanged;
                _mediaPlayer.LengthChanged -= OnLengthChanged;
                _mediaPlayer.Playing -= OnPlaying;
                _mediaPlayer.Paused -= OnPaused;
                _mediaPlayer.Stopped -= OnStopped;
                _mediaPlayer.EndReached -= OnEndReached;
                _mediaPlayer.FrameReady -= OnFrameReady;

                _mediaPlayer.Close();
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            if (_videoRenderer != null)
            {
                (_videoRenderer as IDisposable)?.Dispose();
                _videoRenderer = null;
            }

            _isInitialized = false;
        }
    }
}
