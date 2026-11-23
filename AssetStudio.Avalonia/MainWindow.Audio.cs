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
    private long _audioDragSeekGeneration;

    private static bool UseLinuxAudioFallback => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private void InitializeAudio()
    {
        EnsureAudioProgressHandlersAttached();
        EnsureAudioPlaybackService();
        if (_audioTimer == null)
        {
            _audioTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _audioTimer.Tick += AudioTimer_Tick;
        }
        else if (_audioTimer.Interval != TimeSpan.FromMilliseconds(50))
        {
            _audioTimer.Interval = TimeSpan.FromMilliseconds(50);
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

        if (!_isAudioDragging)
        {
            if (snapshot.DurationMs > 0)
            {
                MediaPlayback.UpdatePosition(snapshot.PositionMs / 1000.0, snapshot.DurationMs / 1000.0);
            }
            else
            {
                MediaPlayback.UpdatePosition(snapshot.PositionMs / 1000.0);
            }

            if (AudioProgressBar != null && snapshot.DurationMs > 0)
            {
                _isUpdatingAudioProgress = true;
                try
                {
                    AudioProgressBar.Value = MediaPlayback.ProgressPermille;
                }
                finally
                {
                    _isUpdatingAudioProgress = false;
                }
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
    }

    private void AudioReset(bool recreateAudioPlayer = true)
    {
        _isAudioDragging = false;
        _audioDragSeekGeneration = 0L;
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

        if (AudioProgressBar != null)
        {
            _isUpdatingAudioProgress = true;
            try
            {
                AudioProgressBar.Value = 0;
            }
            finally
            {
                _isUpdatingAudioProgress = false;
            }
        }
        if (AudioPlayButton != null) AudioPlayButton.IsEnabled = true;
    }

    private async Task ExecuteAudioPlayPauseAsync()
    {
        var service = _audioPlaybackService;
        if ((MediaPlayback.IsPlaying || MediaPlayback.IsPaused)
            && service != null
            && service.HasActiveBackend)
        {
            ExecuteAudioPause();
            return;
        }

        await ExecuteAudioPlayAsync();
    }

    private async Task ExecuteAudioPlayAsync()
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
                StatusStripUpdate(result.Message);
                return;
            }

            MediaPlayback.UpdatePosition(0);
            MediaPlayback.SetPlaying();
            _audioTimer?.Start();
        }
        catch (Exception ex)
        {
            MediaPlayback.SetError(ex.Message, "Error");
            StatusStripUpdate($"Failed to play audio: {ex.Message}");
        }
    }

    private void AudioProgressBar_DragStarted(object? sender, EventArgs e)
    {
        _isAudioDragging = true;
        var service = _audioPlaybackService;
        if (service != null && service.DurationMs > 0 && AudioProgressBar != null)
        {
            var previewMs = Math.Clamp(
                (long)Math.Round(service.DurationMs * (AudioProgressBar.Value / 1000.0)),
                0L,
                service.DurationMs);
            _audioDragSeekGeneration = service.BeginUserSeek(previewMs);
        }
    }

    private void AudioProgressBar_DragCompleted(object? sender, EventArgs e)
    {
        _isAudioDragging = false;
        var dragGen = _audioDragSeekGeneration;
        _audioDragSeekGeneration = 0L;

        var service = _audioPlaybackService;
        if (service == null || service.DurationMs <= 0 || AudioProgressBar == null)
        {
            if (dragGen != 0L)
            {
                service?.CancelUserSeek(dragGen);
            }
            return;
        }

        var newMs = Math.Clamp(
            (long)Math.Round(service.DurationMs * (AudioProgressBar.Value / 1000.0)),
            0L,
            service.DurationMs);
        MediaPlayback.UpdatePosition(newMs / 1000.0, service.DurationMs / 1000.0);

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
            : string.Empty;

        AudioReset();
        currentPreviewAudioClip = audioClip;
        MediaPlayback.BeginAudioLoad(audioClip.m_Name, preservedInfoText);

        SetAudioTransportButtonsEnabled(false);

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

        return true;
    }

    private void ExecuteAudioPause()
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
            }
            else
            {
                service.Resume();
                MediaPlayback.SetPlaying();
                _audioTimer?.Start();
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to pause audio: {ex.Message}");
        }
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
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Warning, $"Failed to restart audio preview: {ex.Message}");
        }
    }

    private void AudioStop()
    {
        _isAudioDragging = false;
        _audioDragSeekGeneration = 0L;
        try
        {
            _audioPlaybackService?.Stop();
            _audioTimer?.Stop();
            MediaPlayback.SetStopped();
            if (AudioProgressBar != null)
            {
                _isUpdatingAudioProgress = true;
                try
                {
                    AudioProgressBar.Value = 0;
                }
                finally
                {
                    _isUpdatingAudioProgress = false;
                }
            }
        }
        catch { }
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

        var newMs = Math.Clamp(
            (long)Math.Round(service.DurationMs * (AudioProgressBar.Value / 1000.0)),
            0L,
            service.DurationMs);
        MediaPlayback.UpdatePosition(newMs / 1000.0, service.DurationMs / 1000.0);

        if (_isAudioDragging)
        {
            _audioDragSeekGeneration = service.BeginUserSeek(newMs);
        }
        else
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
        var loop = MediaPlayback.IsLooping;
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
    }

    private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
    {
        AudioReset();
        currentPreviewAudioClip = m_AudioClip;
        EnsureAudioPlaybackService();

        var infoText = AudioPreviewPreparer.FormatAudioClipCompressionInfo(m_AudioClip);
        MediaPlayback.SelectAudio(m_AudioClip.m_Name, infoText);

        StatusStripUpdate($"Loaded audio metadata: {m_AudioClip.m_Name}");
    }
    #endregion
}
