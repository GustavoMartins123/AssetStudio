using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using FFmpegVideoPlayer.Core;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private AudioClip? currentPreviewAudioClip;
    private FFmpegMediaPlayer? _audioMediaPlayer;
    private WinMmAudioPlayer? _pcmWavePreviewPlayer;
    private DispatcherTimer? _audioTimer;
    private AudioPlaybackService? _audioPlaybackService;
    private readonly SemaphoreSlim _pcmWaveLoadGate = new(1, 1);
    private bool _audioProgressHandlersAttached;
    private bool _isUpdatingAudioProgress;
    private bool _isAudioDragging;

    private static bool UseLinuxAudioFallback => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private void InitializeAudio()
    {
        EnsureAudioProgressHandlersAttached();
        EnsureAudioPlaybackService();
        if (_audioTimer == null)
        {
            _audioTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _audioTimer.Tick += AudioTimer_Tick;
        }
    }

    #region Audio Preview
    private void EnsureAudioProgressHandlersAttached()
    {
        if (_audioProgressHandlersAttached || AudioProgressBar == null)
        {
            return;
        }

        AudioProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, AudioProgressBar_DragStarted);
        AudioProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, AudioProgressBar_DragCompleted);
        _audioProgressHandlersAttached = true;
    }

    private AudioPlaybackService EnsureAudioPlaybackService()
    {
        EnsureAudioProgressHandlersAttached();
        if (_audioPlaybackService != null)
        {
            return _audioPlaybackService;
        }

        var factories = AudioPlaybackService.CreateDefaultFactories(
            getOrCreatePcmPlayer: () => _pcmWavePreviewPlayer ??= new WinMmAudioPlayer(),
            pcmLoadGate: _pcmWaveLoadGate,
            createFFmpegPlayer: ConsumeOrCreateAudioMediaPlayer,
            log: message => logger.Log(LoggerEvent.Warning, message));

        var service = new AudioPlaybackService(
            factories,
            dispatchToUi: DispatchMediaPlaybackToUi,
            log: message => logger.Log(LoggerEvent.Warning, message));
        service.SetVolume(MediaPlayback.IsMuted ? 0 : MediaPlayback.AudioVolume);
        service.PlaybackEnded += AudioPlaybackService_PlaybackEnded;
        _audioPlaybackService = service;
        return service;
    }

    private FFmpegMediaPlayer CreateAudioMediaPlayer()
    {
        return new FFmpegMediaPlayer(
            action => Dispatcher.UIThread.Post(action),
            (sr, ch) =>
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        return new WinMmAudioPlayer(sr, ch);
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        return null;
                    }

                    return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                }
                catch
                {
                    try
                    {
                        return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                    }
                    catch
                    {
                        return null;
                    }
                }
            });
    }

    private FFmpegMediaPlayer? ConsumeOrCreateAudioMediaPlayer()
    {
        var warmPlayer = Interlocked.Exchange(ref _audioMediaPlayer, null);
        if (warmPlayer != null)
        {
            return warmPlayer;
        }

        try
        {
            return CreateAudioMediaPlayer();
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to initialize FFmpeg audio player: {ex.Message}");
            return null;
        }
    }

    private void AudioTimer_Tick(object? sender, EventArgs e)
    {
        var service = _audioPlaybackService;
        if (service == null)
        {
            return;
        }

        var snapshot = service.Poll();
        if (!snapshot.HasBackend)
        {
            return;
        }

        if (snapshot.IsEnded)
        {
            HandleAudioPlaybackEnded();
            return;
        }

        if (snapshot.DurationMs > 0)
        {
            MediaPlayback.UpdatePosition(snapshot.PositionMs / 1000.0, snapshot.DurationMs / 1000.0);
        }
        else
        {
            MediaPlayback.UpdatePosition(snapshot.PositionMs / 1000.0);
        }

        if (AudioProgressBar != null && !_isAudioDragging && snapshot.DurationMs > 0)
        {
            _isUpdatingAudioProgress = true;
            if (AudioTimerLabel != null)
            {
                AudioTimerLabel.Text = MediaPlayback.FormattedTimeRange;
            }
            try
            {
                AudioProgressBar.Value = MediaPlayback.ProgressPermille;
            }
            finally
            {
                _isUpdatingAudioProgress = false;
            }
        }

        if (snapshot.IsPlaying && !MediaPlayback.IsPlaying)
        {
            MediaPlayback.SetPlaying();
        }
        else if (!snapshot.IsPlaying && MediaPlayback.IsPlaying)
        {
            MediaPlayback.SetPaused();
        }

        if (AudioStatusLabel != null)
        {
            AudioStatusLabel.Text = snapshot.IsPlaying ? "Playing" : "Paused";
        }
    }

    private void AudioReset(bool recreateAudioPlayer = true)
    {
        try
        {
            _audioTimer?.Stop();
        }
        catch { }

        _audioPlaybackService?.Reset();

        if (!recreateAudioPlayer)
        {
            var unusedPlayer = Interlocked.Exchange(ref _audioMediaPlayer, null);
            FFmpegAudioPreviewBackend.DisposePlayerInBackground(unusedPlayer);

            if (_audioPlaybackService != null)
            {
                _audioPlaybackService.PlaybackEnded -= AudioPlaybackService_PlaybackEnded;
                _audioPlaybackService.Dispose();
                _audioPlaybackService = null;
            }
        }

        MediaPlayback.ResetIfActive(ActiveMediaKind.Audio);

        if (AudioProgressBar != null) AudioProgressBar.Value = 0;
        if (AudioTimerLabel != null) AudioTimerLabel.Text = MediaPlaybackViewModel.EmptyTimeRange;
        if (AudioStatusLabel != null) AudioStatusLabel.Text = "Stopped";
        if (AudioInfoLabel != null) AudioInfoLabel.Text = string.Empty;
        if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
        if (AudioPlayButton != null) AudioPlayButton.IsEnabled = true;
        if (AudioPauseButton != null) AudioPauseButton.IsEnabled = true;
    }

    private async void AudioPlayButton_Click(object? sender, RoutedEventArgs e)
    {
        var audioClip = currentPreviewAudioClip;
        if (audioClip == null)
        {
            return;
        }

        try
        {
            if (!await EnsureAudioPreviewFileAsync(audioClip))
            {
                return;
            }

            if (!ReferenceEquals(currentPreviewAudioClip, audioClip))
            {
                return;
            }

            var service = EnsureAudioPlaybackService();
            var sessionId = service.SessionId;
            MediaPlayback.SetPreparing();
            SetAudioTransportButtonsEnabled(false);
            if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;

            AudioPreviewResult result;
            try
            {
                result = await service.PlayFromStartAsync();
            }
            finally
            {
                if (ReferenceEquals(currentPreviewAudioClip, audioClip) && sessionId == service.SessionId)
                {
                    SetAudioTransportButtonsEnabled(true);
                }
            }

            if (!ReferenceEquals(currentPreviewAudioClip, audioClip)
                || sessionId != service.SessionId
                || result.Status == AudioPreviewResultStatus.Cancelled)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                MediaPlayback.SetError(result.Message, "Unsupported");
                if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
                StatusStripUpdate(result.Message);
                return;
            }

            MediaPlayback.UpdatePosition(0);
            MediaPlayback.SetPlaying();
            if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
            if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
            _audioTimer?.Start();
        }
        catch (Exception ex)
        {
            MediaPlayback.SetError(ex.Message, "Error");
            if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
            StatusStripUpdate($"Failed to play audio: {ex.Message}");
        }
    }

    private void AudioProgressBar_DragStarted(object? sender, EventArgs e)
    {
        _isAudioDragging = true;
    }

    private void AudioProgressBar_DragCompleted(object? sender, EventArgs e)
    {
        _isAudioDragging = false;
        var service = _audioPlaybackService;
        if (service == null || service.DurationMs <= 0 || AudioProgressBar == null)
        {
            return;
        }

        var newMs = (long)(service.DurationMs * (AudioProgressBar.Value / 1000.0));
        MediaPlayback.UpdatePosition(newMs / 1000.0);
        if (AudioTimerLabel != null)
        {
            AudioTimerLabel.Text = MediaPlayback.FormattedTimeRange;
        }

        try
        {
            service.Seek(newMs);
        }
        catch { }
    }

    private async Task<bool> EnsureAudioPreviewFileAsync(AudioClip audioClip)
    {
        var service = EnsureAudioPlaybackService();
        var assetId = GetPreviewAssetId(audioClip);
        if (service.IsLoaded(assetId))
        {
            return true;
        }

        var preservedInfoText = MediaPlayback.IsAudioActive && !string.IsNullOrEmpty(MediaPlayback.MediaInfoText)
            ? MediaPlayback.MediaInfoText
            : (AudioInfoLabel?.Text ?? string.Empty);

        AudioReset();
        currentPreviewAudioClip = audioClip;
        MediaPlayback.BeginAudioLoad(audioClip.m_Name, preservedInfoText);
        if (AudioInfoLabel != null && !string.IsNullOrEmpty(preservedInfoText))
        {
            AudioInfoLabel.Text = preservedInfoText;
        }

        SetAudioTransportButtonsEnabled(false);
        if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;

        var isLinux = UseLinuxAudioFallback;
        var preferTranscode = isLinux || Environment.GetEnvironmentVariable("ASSETSTUDIO_PREFER_TRANSCODE_PREVIEW") == "1";

        AudioPreviewResult result;
        try
        {
            result = await service.LoadAsync(
                assetId,
                token => AudioPreviewPreparer.PrepareAudioClip(audioClip, isLinux, preferTranscode, token));
        }
        finally
        {
            if (ReferenceEquals(currentPreviewAudioClip, audioClip))
            {
                SetAudioTransportButtonsEnabled(true);
            }
        }

        if (!ReferenceEquals(currentPreviewAudioClip, audioClip) || result.Status == AudioPreviewResultStatus.Cancelled)
        {
            return false;
        }

        if (!result.IsSuccess)
        {
            var statusLabel = result.Status switch
            {
                AudioPreviewResultStatus.Missing => "Missing",
                AudioPreviewResultStatus.Unsupported => "Unsupported",
                _ => "Error"
            };
            MediaPlayback.SetError(result.Message, statusLabel);
            if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
            if (!string.IsNullOrEmpty(result.Message))
            {
                StatusStripUpdate(result.Message);
            }
            return false;
        }

        MediaPlayback.MarkAudioReady(
            service.DurationMs / 1000.0,
            service.BackendName,
            service.CanSeek,
            service.SourceDescription);

        if (AudioInfoLabel != null) AudioInfoLabel.Text = MediaPlayback.MediaInfoText;
        if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
        return true;
    }

    private void AudioPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        var audioClip = currentPreviewAudioClip;
        var service = _audioPlaybackService;
        if (audioClip == null || service == null || !service.HasActiveBackend)
        {
            return;
        }

        try
        {
            if (service.IsPlaying)
            {
                service.Pause();
                MediaPlayback.SetPaused();
                if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
                if (AudioPauseButton != null) AudioPauseButton.Content = "Resume";
            }
            else
            {
                service.Resume();
                MediaPlayback.SetPlaying();
                if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
                if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
                _audioTimer?.Start();
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to pause audio: {ex.Message}");
        }
    }

    private void AudioStopButton_Click(object? sender, RoutedEventArgs e)
    {
        AudioStop();
    }

    private void RestartAudioPreviewPlayback()
    {
        var service = _audioPlaybackService;
        if (service == null || !service.HasActiveBackend)
        {
            return;
        }

        try
        {
            service.RestartForLoop();
            MediaPlayback.UpdatePosition(0);
            MediaPlayback.SetPlaying();
            _audioTimer?.Start();
            if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
            if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Warning, $"Failed to restart audio preview: {ex.Message}");
        }
    }

    private void AudioStop()
    {
        try
        {
            _audioPlaybackService?.Stop();
            _audioTimer?.Stop();
            MediaPlayback.SetStopped();
            if (AudioProgressBar != null) AudioProgressBar.Value = 0;
            if (AudioTimerLabel != null) AudioTimerLabel.Text = MediaPlayback.FormattedTimeRange;
            if (AudioStatusLabel != null) AudioStatusLabel.Text = MediaPlayback.StatusText;
            if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
        }
        catch { }
    }

    private void AudioLoopButton_Click(object? sender, RoutedEventArgs e)
    {
        MediaPlayback.SetLooping(AudioloopButton?.IsChecked == true);
    }

    private void AudioVolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var volume = (int)(AudioVolumeBar.Value * 10);
        MediaPlayback.SetAudioVolume(volume);
        _audioPlaybackService?.SetVolume(MediaPlayback.IsMuted ? 0 : MediaPlayback.AudioVolume);
    }

    private void AudioProgressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingAudioProgress)
        {
            return;
        }

        var service = _audioPlaybackService;
        if (service == null || service.DurationMs <= 0)
        {
            return;
        }

        var newMs = (long)(service.DurationMs * (AudioProgressBar.Value / 1000.0));
        MediaPlayback.UpdatePosition(newMs / 1000.0);
        if (AudioTimerLabel != null)
        {
            AudioTimerLabel.Text = MediaPlayback.FormattedTimeRange;
        }

        if (!_isAudioDragging)
        {
            try
            {
                service.Seek(newMs);
            }
            catch { }
        }
    }

    private void AudioPlaybackService_PlaybackEnded(object? sender, EventArgs e)
    {
        if (currentPreviewAudioClip == null)
        {
            return;
        }

        HandleAudioPlaybackEnded();
    }

    private void HandleAudioPlaybackEnded()
    {
        var loop = AudioloopButton?.IsChecked == true;
        MediaPlayback.SetLooping(loop);
        if (loop)
        {
            RestartAudioPreviewPlayback();
        }
        else
        {
            AudioStop();
        }
    }

    private void SetAudioTransportButtonsEnabled(bool enabled)
    {
        if (AudioPlayButton != null) AudioPlayButton.IsEnabled = enabled;
        if (AudioPauseButton != null) AudioPauseButton.IsEnabled = enabled;
    }

    private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
    {
        AudioReset();
        currentPreviewAudioClip = m_AudioClip;
        EnsureAudioPlaybackService();

        var infoText = AudioPreviewPreparer.FormatAudioClipCompressionInfo(m_AudioClip);
        MediaPlayback.SelectAudio(m_AudioClip.m_Name, infoText);
        MediaPlayback.SetLooping(AudioloopButton?.IsChecked == true);

        AudioPanel.IsVisible = true;
        AudioInfoLabel.Text = MediaPlayback.MediaInfoText;
        AudioTimerLabel.Text = MediaPlayback.FormattedTimeRange;
        AudioTitleLabel.Text = MediaPlayback.MediaTitle;
        AudioStatusLabel.Text = MediaPlayback.StatusText;
        AudioPauseButton.Content = "Pause";
        StatusStripUpdate($"Loaded audio metadata: {m_AudioClip.m_Name}");
    }
    #endregion
}
