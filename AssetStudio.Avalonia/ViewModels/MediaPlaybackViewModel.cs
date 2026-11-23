using System;
using System.Globalization;

namespace AssetStudio.Avalonia.ViewModels
{
    /// <summary>Which preview panel currently owns the shared media transport.</summary>
    public enum ActiveMediaKind
    {
        None,
        Audio,
        Video
    }

    /// <summary>Transport state of the active media preview.</summary>
    public enum MediaPlaybackState
    {
        Stopped,
        Preparing,
        Playing,
        Paused,
        Error
    }

    /// <summary>
    /// Headless, UI-framework-free state machine for the unified AudioClip / VideoClip preview transport.
    /// All mutators are marshalled through the injectable <c>dispatchToUi</c> delegate (inline by default, so
    /// headless tests observe state synchronously) and every observable property raises a deduplicated
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> event only when its value changes.
    /// Volumes use the backend percentage scale (0..100); the audio slider (0..10) maps with a factor of 10 and the
    /// video slider (0..100) maps one-to-one.
    /// </summary>
    public sealed class MediaPlaybackViewModel : ViewModelBase
    {
        /// <summary>Neutral timer text shown while stopped with no active playback position.</summary>
        public const string EmptyTimeRange = "0:00.0 / 0:00.0";
        public const int DefaultVolume = 80;
        public const int MaxVolume = 100;
        private const string PreviewInfoSeparator = "| Preview:";

        private readonly Action<Action> _dispatchToUi;

        private ActiveMediaKind _mediaKind;
        private MediaPlaybackState _playbackState;
        private bool _isReady;
        private bool _isLooping;
        private bool _isMuted;
        private int _audioVolume = DefaultVolume;
        private int _videoVolume = DefaultVolume;
        private double _positionSeconds;
        private double _durationSeconds;
        private string _mediaTitle = string.Empty;
        private string _mediaInfoText = string.Empty;
        private string _backendDisplayName = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorStatusText = "Error";
        private bool _backendCanSeek;

        private bool _isAudioActive;
        private bool _isVideoActive;
        private bool _isPreparing;
        private bool _isPlaying;
        private bool _isPaused;
        private bool _canPlay;
        private bool _canPause;
        private bool _canStop;
        private bool _canSeek;
        private string _statusText = "Stopped";
        private string _formattedPosition = FormatTime(0);
        private string _formattedDuration = FormatTime(0);
        private string _formattedTimeRange = EmptyTimeRange;
        private double _progressPermille;
        private int _volume = DefaultVolume;
        private int _effectiveVolume = DefaultVolume;

        public MediaPlaybackViewModel(Action<Action>? dispatchToUi = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
        }

        public ActiveMediaKind MediaKind
        {
            get => _mediaKind;
            private set => SetProperty(ref _mediaKind, value);
        }

        public MediaPlaybackState PlaybackState
        {
            get => _playbackState;
            private set => SetProperty(ref _playbackState, value);
        }

        /// <summary>True when a clip is selected or loaded and ready to play (before explicit Stop).</summary>
        public bool IsReady
        {
            get => _isReady;
            private set => SetProperty(ref _isReady, value);
        }

        public bool IsLooping
        {
            get => _isLooping;
            set => Dispatch(() => SetProperty(ref _isLooping, value));
        }

        public bool IsMuted
        {
            get => _isMuted;
            private set => SetProperty(ref _isMuted, value);
        }

        /// <summary>Audio transport volume, 0..100 (audio slider value * 10).</summary>
        public int AudioVolume
        {
            get => _audioVolume;
            private set => SetProperty(ref _audioVolume, value);
        }

        /// <summary>Video transport volume, 0..100 (video slider value).</summary>
        public int VideoVolume
        {
            get => _videoVolume;
            private set => SetProperty(ref _videoVolume, value);
        }

        public double PositionSeconds
        {
            get => _positionSeconds;
            private set => SetProperty(ref _positionSeconds, value);
        }

        public double DurationSeconds
        {
            get => _durationSeconds;
            private set => SetProperty(ref _durationSeconds, value);
        }

        public string MediaTitle
        {
            get => _mediaTitle;
            private set => SetProperty(ref _mediaTitle, value);
        }

        public string MediaInfoText
        {
            get => _mediaInfoText;
            private set
            {
                if (SetProperty(ref _mediaInfoText, value))
                {
                    OnPropertyChanged(nameof(EffectiveInfoText));
                }
            }
        }

        public string BackendDisplayName
        {
            get => _backendDisplayName;
            private set => SetProperty(ref _backendDisplayName, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(EffectiveInfoText));
                }
            }
        }

        public string EffectiveInfoText => !string.IsNullOrEmpty(_errorMessage) ? _errorMessage : _mediaInfoText;

        public bool IsAudioActive
        {
            get => _isAudioActive;
            private set => SetProperty(ref _isAudioActive, value);
        }

        public bool IsVideoActive
        {
            get => _isVideoActive;
            private set => SetProperty(ref _isVideoActive, value);
        }

        public bool IsPreparing
        {
            get => _isPreparing;
            private set => SetProperty(ref _isPreparing, value);
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    OnPropertyChanged(nameof(PlayPauseButtonText));
                    OnPropertyChanged(nameof(VideoPlayButtonText));
                }
            }
        }

        public bool IsPaused
        {
            get => _isPaused;
            private set
            {
                if (SetProperty(ref _isPaused, value))
                {
                    OnPropertyChanged(nameof(AudioPauseButtonText));
                }
            }
        }

        /// <summary>Unified Play/Pause toggle caption shared across Audio and Video transports.</summary>
        public string PlayPauseButtonText => _isPlaying ? "Pause" : "Play";

        public string VideoPlayButtonText => PlayPauseButtonText;

        public string AudioPauseButtonText => _isPaused ? "Resume" : "Pause";

        /// <summary>True when a media item is loaded/stopped/paused and ready to start or resume playback.</summary>
        public bool CanPlay
        {
            get => _canPlay;
            private set => SetProperty(ref _canPlay, value);
        }

        /// <summary>True while media is actively playing.</summary>
        public bool CanPause
        {
            get => _canPause;
            private set => SetProperty(ref _canPause, value);
        }

        /// <summary>True while media is playing or paused.</summary>
        public bool CanStop
        {
            get => _canStop;
            private set => SetProperty(ref _canStop, value);
        }

        /// <summary>True when the active backend supports seeking, duration is known, and no load/error is active.</summary>
        public bool CanSeek
        {
            get => _canSeek;
            private set => SetProperty(ref _canSeek, value);
        }

        /// <summary>Display status text: Stopped / Ready / Loading / Playing / Paused / (Error|Unsupported|Missing).</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string FormattedPosition
        {
            get => _formattedPosition;
            private set => SetProperty(ref _formattedPosition, value);
        }

        public string FormattedDuration
        {
            get => _formattedDuration;
            private set => SetProperty(ref _formattedDuration, value);
        }

        /// <summary>"m:ss.cc / m:ss.cc", or <see cref="EmptyTimeRange"/> while stopped at 0 or when duration is unknown.</summary>
        public string FormattedTimeRange
        {
            get => _formattedTimeRange;
            private set => SetProperty(ref _formattedTimeRange, value);
        }

        /// <summary>Progress on the 0..1000 slider scale used by both transport bars.</summary>
        public double ProgressPermille
        {
            get => _progressPermille;
            private set => SetProperty(ref _progressPermille, value);
        }

        /// <summary>Volume of the active media kind (audio volume when nothing is active), 0..100.</summary>
        public int Volume
        {
            get => _volume;
            private set => SetProperty(ref _volume, value);
        }

        /// <summary><see cref="Volume"/>, or 0 while muted.</summary>
        public int EffectiveVolume
        {
            get => _effectiveVolume;
            private set => SetProperty(ref _effectiveVolume, value);
        }

        /// <summary>Formats milliseconds as <c>m:ss.cc</c> (identical to the legacy MainWindow formatter).</summary>
        public static string FormatTime(long milliseconds)
        {
            if (milliseconds < 0)
            {
                milliseconds = 0;
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"{milliseconds / 1000 / 60}:{milliseconds / 1000 % 60:D2}.{milliseconds / 10 % 100:D2}");
        }

        public static string FormatTimeRange(long currentMs, long totalMs)
        {
            return FormatTime(currentMs) + " / " + FormatTime(totalMs);
        }

        private static long ToMilliseconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            {
                return 0;
            }

            return (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        }

        private static double SanitizeSeconds(double seconds)
        {
            return double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 ? 0 : seconds;
        }

        private static int ClampVolume(int volume)
        {
            return Math.Clamp(volume, 0, MaxVolume);
        }

        /// <summary>Shows an AudioClip's metadata; nothing is decoded yet (status "Ready", state Stopped).</summary>
        public void SelectAudio(string title, string infoText = "")
        {
            Dispatch(() => Select(ActiveMediaKind.Audio, title, infoText, MediaPlaybackState.Stopped, isReady: true));
        }

        /// <summary>Shows a VideoClip's metadata; nothing is extracted yet (status "Ready", state Stopped).</summary>
        public void SelectVideo(string title, string infoText = "")
        {
            Dispatch(() => Select(ActiveMediaKind.Video, title, infoText, MediaPlaybackState.Stopped, isReady: true));
        }

        /// <summary>Audio preview preparation (decode / transcode / backend open) started.</summary>
        public void BeginAudioLoad(string? title, string infoText = "")
        {
            Dispatch(() =>
            {
                if (_mediaKind != ActiveMediaKind.Audio)
                {
                    Select(ActiveMediaKind.Audio, title ?? string.Empty, infoText, MediaPlaybackState.Preparing, isReady: false);
                    return;
                }

                if (title != null)
                {
                    MediaTitle = title;
                }

                MediaInfoText = infoText ?? string.Empty;
                BackendDisplayName = string.Empty;
                ErrorMessage = string.Empty;
                _backendCanSeek = false;
                PositionSeconds = 0;
                DurationSeconds = 0;
                IsReady = false;
                PlaybackState = MediaPlaybackState.Preparing;
                RecomputeDerived();
            });
        }

        /// <summary>Video preview preparation (temp file extraction) started; a null title keeps the current one.</summary>
        public void BeginVideoLoad(string? title, string infoText = "")
        {
            Dispatch(() =>
            {
                if (_mediaKind != ActiveMediaKind.Video)
                {
                    Select(ActiveMediaKind.Video, title ?? string.Empty, infoText, MediaPlaybackState.Preparing, isReady: false);
                    return;
                }

                if (title != null)
                {
                    MediaTitle = title;
                }

                MediaInfoText = infoText ?? string.Empty;
                ErrorMessage = string.Empty;
                IsReady = false;
                PlaybackState = MediaPlaybackState.Preparing;
                RecomputeDerived();
            });
        }

        /// <summary>Audio backend opened the prepared clip; optionally appends " | Preview: {source}" to the info text.</summary>
        public void MarkAudioReady(
            double durationSeconds,
            string backendName = "",
            bool canSeek = true,
            string? sourceDescription = null)
        {
            Dispatch(() =>
            {
                if (_mediaKind != ActiveMediaKind.Audio)
                {
                    return;
                }

                DurationSeconds = SanitizeSeconds(durationSeconds);
                PositionSeconds = _durationSeconds > 0
                    ? Math.Min(_positionSeconds, _durationSeconds)
                    : 0;
                BackendDisplayName = backendName ?? string.Empty;
                _backendCanSeek = canSeek;
                ErrorMessage = string.Empty;
                if (!string.IsNullOrEmpty(sourceDescription))
                {
                    MediaInfoText = ComposePreviewInfo(_mediaInfoText, sourceDescription);
                }

                if (_playbackState is not (MediaPlaybackState.Playing or MediaPlaybackState.Paused))
                {
                    IsReady = true;
                    PlaybackState = MediaPlaybackState.Stopped;
                }

                RecomputeDerived();
            });
        }

        /// <summary>Video media opened (thumbnail decoded). Never downgrades an already playing/paused transport.</summary>
        public void MarkVideoReady(
            double durationSeconds,
            string backendName = "FFmpeg",
            string infoText = "",
            bool canSeek = true)
        {
            Dispatch(() =>
            {
                if (_mediaKind != ActiveMediaKind.Video)
                {
                    return;
                }

                DurationSeconds = SanitizeSeconds(durationSeconds);
                BackendDisplayName = backendName ?? string.Empty;
                _backendCanSeek = canSeek;
                if (_playbackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
                {
                    RecomputeDerived();
                    return;
                }

                ErrorMessage = string.Empty;
                if (!string.IsNullOrEmpty(infoText))
                {
                    MediaInfoText = infoText;
                }

                PositionSeconds = 0;
                IsReady = true;
                PlaybackState = MediaPlaybackState.Stopped;
                RecomputeDerived();
            });
        }

        /// <summary>Re-enters the preparing state for the active media (e.g. reopening FFmpeg before playback).</summary>
        public void SetPreparing()
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.None)
                {
                    return;
                }

                IsReady = false;
                PlaybackState = MediaPlaybackState.Preparing;
                RecomputeDerived();
            });
        }

        /// <summary>Leaves <see cref="MediaPlaybackState.Preparing"/> back to Ready; any other state is untouched.</summary>
        public void EndPreparing()
        {
            Dispatch(() =>
            {
                if (_playbackState == MediaPlaybackState.Preparing)
                {
                    IsReady = true;
                    PlaybackState = MediaPlaybackState.Stopped;
                    RecomputeDerived();
                }
            });
        }

        public void SetPlaying()
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.None)
                {
                    return;
                }

                ErrorMessage = string.Empty;
                IsReady = true;
                PlaybackState = MediaPlaybackState.Playing;
                RecomputeDerived();
            });
        }

        public void SetPaused()
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.None)
                {
                    return;
                }

                PlaybackState = MediaPlaybackState.Paused;
                RecomputeDerived();
            });
        }

        /// <summary>Stops the transport and rewinds to zero (duration and backend capabilities are kept for replay/seek).</summary>
        public void SetStopped()
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.None)
                {
                    return;
                }

                PositionSeconds = 0;
                IsReady = false;
                PlaybackState = MediaPlaybackState.Stopped;
                RecomputeDerived();
            });
        }

        /// <summary>Updates the playback position, clamped to [0, duration] once the duration is known.</summary>
        public void UpdatePosition(double seconds)
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.None)
                {
                    return;
                }

                ApplyPosition(seconds);
                RecomputeDerived();
            });
        }

        /// <summary>Updates duration and position together (video players report both on every tick).</summary>
        public void UpdatePosition(double seconds, double durationSeconds)
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.None)
                {
                    return;
                }

                DurationSeconds = SanitizeSeconds(durationSeconds);
                ApplyPosition(seconds);
                RecomputeDerived();
            });
        }

        public void SetLooping(bool isLooping)
        {
            IsLooping = isLooping;
        }

        public void SetMuted(bool isMuted)
        {
            Dispatch(() =>
            {
                IsMuted = isMuted;
                RecomputeDerived();
            });
        }

        /// <summary>Sets the volume (0..100) of the active media kind (audio when nothing is active).</summary>
        public void SetVolume(int volume)
        {
            Dispatch(() =>
            {
                if (_mediaKind == ActiveMediaKind.Video)
                {
                    VideoVolume = ClampVolume(volume);
                }
                else
                {
                    AudioVolume = ClampVolume(volume);
                }

                RecomputeDerived();
            });
        }

        public void SetAudioVolume(int volume)
        {
            Dispatch(() =>
            {
                AudioVolume = ClampVolume(volume);
                RecomputeDerived();
            });
        }

        public void SetVideoVolume(int volume)
        {
            Dispatch(() =>
            {
                VideoVolume = ClampVolume(volume);
                RecomputeDerived();
            });
        }

        public void SetMediaTitle(string title)
        {
            Dispatch(() =>
            {
                if (_mediaKind != ActiveMediaKind.None)
                {
                    MediaTitle = title ?? string.Empty;
                }
            });
        }

        public void SetMediaInfo(string infoText)
        {
            Dispatch(() =>
            {
                if (_mediaKind != ActiveMediaKind.None)
                {
                    MediaInfoText = infoText ?? string.Empty;
                }
            });
        }

        /// <summary>Enters the error state. <paramref name="statusText"/> is the label shown (Error / Unsupported / Missing).</summary>
        public void SetError(string message, string statusText = "Error")
        {
            Dispatch(() =>
            {
                _errorStatusText = string.IsNullOrEmpty(statusText) ? "Error" : statusText;
                ErrorMessage = message ?? string.Empty;
                IsReady = false;
                PlaybackState = MediaPlaybackState.Error;
                RecomputeDerived();
            });
        }

        /// <summary>Clears all media state. Volume, loop and mute preferences survive (they mirror persistent controls).</summary>
        public void Reset()
        {
            Dispatch(ResetCore);
        }

        /// <summary>Resets only when <paramref name="kind"/> owns the transport (or nothing does).</summary>
        public void ResetIfActive(ActiveMediaKind kind)
        {
            Dispatch(() =>
            {
                if (_mediaKind == kind || _mediaKind == ActiveMediaKind.None)
                {
                    ResetCore();
                }
            });
        }

        private void ResetCore()
        {
            MediaKind = ActiveMediaKind.None;
            PlaybackState = MediaPlaybackState.Stopped;
            IsReady = false;
            PositionSeconds = 0;
            DurationSeconds = 0;
            MediaTitle = string.Empty;
            MediaInfoText = string.Empty;
            BackendDisplayName = string.Empty;
            ErrorMessage = string.Empty;
            _errorStatusText = "Error";
            _backendCanSeek = false;
            RecomputeDerived();
        }

        private void Select(ActiveMediaKind kind, string title, string infoText, MediaPlaybackState state, bool isReady)
        {
            MediaKind = kind;
            MediaTitle = title ?? string.Empty;
            MediaInfoText = infoText ?? string.Empty;
            BackendDisplayName = string.Empty;
            ErrorMessage = string.Empty;
            _errorStatusText = "Error";
            _backendCanSeek = false;
            PositionSeconds = 0;
            DurationSeconds = 0;
            IsReady = isReady;
            PlaybackState = state;
            RecomputeDerived();
        }

        private void ApplyPosition(double seconds)
        {
            var position = SanitizeSeconds(seconds);
            if (_durationSeconds > 0)
            {
                position = Math.Min(position, _durationSeconds);
            }

            PositionSeconds = position;
        }

        private static string ComposePreviewInfo(string currentInfo, string sourceDescription)
        {
            var baseText = currentInfo ?? string.Empty;
            var idx = baseText.IndexOf(PreviewInfoSeparator, StringComparison.Ordinal);
            if (idx >= 0)
            {
                baseText = baseText.Substring(0, idx).Trim();
            }

            return string.IsNullOrEmpty(baseText)
                ? $"Preview: {sourceDescription}"
                : $"{baseText} {PreviewInfoSeparator} {sourceDescription}";
        }

        private void RecomputeDerived()
        {
            var kind = _mediaKind;
            var state = _playbackState;
            var hasMedia = kind != ActiveMediaKind.None;

            IsAudioActive = kind == ActiveMediaKind.Audio;
            IsVideoActive = kind == ActiveMediaKind.Video;
            IsPreparing = state == MediaPlaybackState.Preparing;
            IsPlaying = state == MediaPlaybackState.Playing;
            IsPaused = state == MediaPlaybackState.Paused;
            CanPlay = hasMedia && state is MediaPlaybackState.Stopped or MediaPlaybackState.Paused;
            CanPause = hasMedia && state == MediaPlaybackState.Playing;
            CanStop = hasMedia && state is MediaPlaybackState.Playing or MediaPlaybackState.Paused;
            CanSeek = hasMedia
                && _backendCanSeek
                && _durationSeconds > 0
                && state is not (MediaPlaybackState.Preparing or MediaPlaybackState.Error);

            StatusText = state switch
            {
                MediaPlaybackState.Preparing => "Loading",
                MediaPlaybackState.Playing => "Playing",
                MediaPlaybackState.Paused => "Paused",
                MediaPlaybackState.Error => _errorStatusText,
                _ => _isReady && hasMedia ? "Ready" : "Stopped"
            };

            var positionMs = ToMilliseconds(_positionSeconds);
            var durationMs = ToMilliseconds(_durationSeconds);
            FormattedPosition = FormatTime(positionMs);
            FormattedDuration = FormatTime(durationMs);
            FormattedTimeRange = positionMs == 0 && (durationMs == 0 || (state == MediaPlaybackState.Stopped && !_isReady))
                ? EmptyTimeRange
                : FormatTimeRange(positionMs, durationMs);
            ProgressPermille = _durationSeconds > 0
                ? Math.Clamp(_positionSeconds * 1000.0 / _durationSeconds, 0, 1000)
                : 0;

            Volume = kind == ActiveMediaKind.Video ? _videoVolume : _audioVolume;
            EffectiveVolume = _isMuted ? 0 : _volume;
        }

        private void Dispatch(Action action)
        {
            _dispatchToUi(action);
        }
    }
}
