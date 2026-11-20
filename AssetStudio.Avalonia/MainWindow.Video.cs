using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private string? _currentTempVideoPath;
    private string? _currentTempVideoAssetId;
    private CancellationTokenSource? _videoPreviewLoadCts;
    private long _videoSessionCounter;
    private bool _isUpdatingVideoProgress;
    private bool _isVideoDragging;
    private long _videoLengthMs;
    private DispatcherTimer? _ffmpegVideoTimer;

    public void InitializeVideoPlayer()
    {
        _ffmpegVideoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _ffmpegVideoTimer.Tick += FfmpegVideoTimer_Tick;
    }

    private static void DeleteTempVideoFileBackground(string? filePath)
    {
        MediaTempFiles.DeleteInBackground(filePath, attempts: 8, retryDelayMs: 50);
    }

    private void SetInitialVideoAudioLabel(VideoClip videoClip)
    {
        if (VideoAudioLabel == null)
        {
            return;
        }

        if (videoClip.HasAudio)
        {
            var sb = new StringBuilder("Audio: yes");
            if (videoClip.m_AudioChannelCount != null)
            {
                for (int i = 0; i < videoClip.m_AudioChannelCount.Length; i++)
                {
                    var ch = videoClip.m_AudioChannelCount[i];
                    var rate = videoClip.m_AudioSampleRate != null && videoClip.m_AudioSampleRate.Length > i ? videoClip.m_AudioSampleRate[i] : 0;
                    sb.Append($" | Track {i + 1}: {ch}ch");
                    if (rate > 0)
                    {
                        sb.Append($" {rate}Hz");
                    }
                }
            }
            sb.Append(" | Unity metadata");
            VideoAudioLabel.Text = sb.ToString();
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#00e676"));
        }
        else
        {
            VideoAudioLabel.Text = "Audio: no playable track found";
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#ff9800"));
        }
    }

    private void VideoStop()
    {
        try
        {
            _ffmpegVideoTimer?.Stop();
            FfmpegVideoPlayer?.Stop();
        }
        catch { }

        if (MediaPlayback.IsVideoActive)
        {
            MediaPlayback.SetStopped();
        }

        SetVideoStoppedUi();
    }

    private Task VideoStopAsync()
    {
        VideoStop();
        return Task.CompletedTask;
    }

    private void CancelVideoPreviewLoad()
    {
        try
        {
            _videoPreviewLoadCts?.Cancel();
        }
        catch { }
    }

    private void VideoReset()
    {
        CancelVideoPreviewLoad();
        Interlocked.Increment(ref _videoSessionCounter);

        try
        {
            _ffmpegVideoTimer?.Stop();
        }
        catch { }

        var pathToDelete = _currentTempVideoPath;
        _currentTempVideoPath = null;
        _currentTempVideoAssetId = null;
        _videoLengthMs = 0;
        MediaPlayback.ResetIfActive(ActiveMediaKind.Video);
        SetVideoStoppedUi();

        // Detach/stop player non-blockingly (VideoPlayerControl.Stop dispatches stop to ThreadPool)
        // and delete the temp video file asynchronously in background so switching assets never stutters.
        var player = FfmpegVideoPlayer;
        if (player != null)
        {
            try
            {
                player.Stop();
            }
            catch { }
        }

        DeleteTempVideoFileBackground(pathToDelete);
    }

    private Task VideoResetAsync()
    {
        VideoReset();
        return Task.CompletedTask;
    }

    private void SetVideoStoppedUi()
    {
        if (VideoProgressBar != null) VideoProgressBar.Value = 0;
    }

    private void PreviewVideoClip(AssetItem assetItem, VideoClip m_VideoClip)
    {
        var videoAssetId = GetPreviewAssetId(m_VideoClip);
        if (_currentTempVideoAssetId == videoAssetId)
        {
            VideoStop();
            Interlocked.Increment(ref _videoSessionCounter);
        }
        else
        {
            VideoReset();
        }

        var sessionId = Volatile.Read(ref _videoSessionCounter);
        currentPreviewVideoClip = m_VideoClip;

        MediaPlayback.SelectVideo(m_VideoClip.m_Name, "Ready. Press Play to prepare embedded preview.");
        MediaPlayback.SetVideoVolume(MediaPlaybackViewModel.DefaultVolume);

        VideoResolutionLabel.Text = $"Resolution: {m_VideoClip.m_Width}x{m_VideoClip.m_Height} (Proxy: {m_VideoClip.m_ProxyWidth}x{m_VideoClip.m_ProxyHeight})";
        VideoFrameRateLabel.Text = $"Frame Rate: {m_VideoClip.m_FrameRate:F2} FPS | Frames: {m_VideoClip.m_FrameCount}";

        var ext = SanitizeExtension(Path.GetExtension(m_VideoClip.m_OriginalPath), ".mp4");
        VideoFormatLabel.Text = $"Format: {ext.ToUpperInvariant().TrimStart('.')}";

        SetInitialVideoAudioLabel(m_VideoClip);

        VideoPlayButton.IsEnabled = true;
        VideoStopButton.IsEnabled = true;
        VideoExportButton.IsEnabled = true;
        VideoVolumeBar.Value = MediaPlayback.VideoVolume;

        PreviewLabel.IsVisible = false;
        StartVideoThumbnailPreview(m_VideoClip, sessionId);
        StatusStripUpdate($"Loaded video clip: {m_VideoClip.m_Name}");
    }

    private void PreviewVideoPlayer(AssetItem assetItem, VideoPlayer m_VideoPlayer)
    {
        if (m_VideoPlayer.m_VideoClip.TryGet(out var m_VideoClip) && m_VideoClip != null)
        {
            StatusStripUpdate($"VideoPlayer references VideoClip: {m_VideoClip.m_Name}");
            PreviewVideoClip(assetItem, m_VideoClip);
            var sessionId = Volatile.Read(ref _videoSessionCounter);
            string vpName = m_VideoPlayer.m_GameObject.TryGet(out var go) ? go.m_Name : "VideoPlayer";
            var title = $"{vpName} (VideoPlayer)";
            MediaPlayback.SetMediaTitle(title);

            var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
            _ = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var sb = new StringBuilder();
                    sb.AppendLine(m_VideoPlayer.Dump());
                    sb.AppendLine("Ready. Press Play to load embedded native preview.");
                    var infoText = sb.ToString();

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsPreviewRequestCurrent(requestId, previewVersion, token)
                            && sessionId == Volatile.Read(ref _videoSessionCounter))
                        {
                            MediaPlayback.SetMediaInfo(infoText);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }, token);
        }
        else
        {
            var statusMessage = m_VideoPlayer.m_Source == 1 && !string.IsNullOrEmpty(m_VideoPlayer.m_Url)
                ? $"VideoPlayer references URL: {m_VideoPlayer.m_Url}"
                : "VideoPlayer has no loaded VideoClip or URL.";
            StatusStripUpdate(statusMessage);

            var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
            TextPreviewBox.Text = "Loading VideoPlayer preview...";
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;

            _ = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var truncated = TruncatePreviewText(m_VideoPlayer.Dump());
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            TextPreviewBox.Text = truncated;
                            TextPreviewBox.IsVisible = true;
                            PreviewLabel.IsVisible = false;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    var errText = TruncatePreviewText($"Failed to dump VideoPlayer:{Environment.NewLine}{ex.Message}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            TextPreviewBox.Text = errText;
                            TextPreviewBox.IsVisible = true;
                            PreviewLabel.IsVisible = false;
                        }
                    });
                }
            }, token);
        }
    }

    private async Task ExecuteVideoPlayPauseAsync()
    {
        var videoClip = currentPreviewVideoClip;
        if (videoClip == null) return;

        var sessionId = Volatile.Read(ref _videoSessionCounter);

        try
        {
            if (FfmpegVideoPlayer.IsPlaying)
            {
                FfmpegVideoPlayer.Pause();
                _ffmpegVideoTimer?.Stop();
                MediaPlayback.SetPaused();
            }
            else
            {
                var videoAssetId = GetPreviewAssetId(videoClip);
                var hasLoadedFile = FfmpegVideoPlayer.HasMediaLoaded
                    && _currentTempVideoAssetId == videoAssetId
                    && !string.IsNullOrEmpty(_currentTempVideoPath)
                    && File.Exists(_currentTempVideoPath);

                if (!hasLoadedFile)
                {
                    if (!await EnsureVideoPreviewFileAsync(videoClip, sessionId))
                    {
                        return;
                    }

                    if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                        || sessionId != Volatile.Read(ref _videoSessionCounter))
                    {
                        return;
                    }

                    var path = _currentTempVideoPath;
                    if (string.IsNullOrEmpty(path))
                    {
                        return;
                    }

                    bool opened = await Task.Run(() =>
                    {
                        try
                        {
                            FfmpegVideoPlayer.Open(path);
                            return FfmpegVideoPlayer.HasMediaLoaded;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                        || sessionId != Volatile.Read(ref _videoSessionCounter))
                    {
                        return;
                    }

                    if (!opened)
                    {
                        MediaPlayback.SetError("FFmpeg could not open this VideoClip.", "Unsupported");
                        StatusStripUpdate("Video preview could not open this VideoClip.");
                        return;
                    }

                    MediaPlayback.MarkVideoReady(
                        Math.Max(0, FfmpegVideoPlayer.Duration) / 1000.0,
                        "FFmpeg",
                        "Embedded FFmpeg preview loaded.");
                }

                FfmpegVideoPlayer.Volume = MediaPlayback.IsMuted ? 0 : MediaPlayback.VideoVolume;
                if (hasLoadedFile && MediaPlayback.PlaybackState == MediaPlaybackState.Stopped && !MediaPlayback.IsReady)
                {
                    FfmpegVideoPlayer.Seek(0f);
                }

                FfmpegVideoPlayer.Play();
                _ffmpegVideoTimer?.Start();
                MediaPlayback.SetPlaying();
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to toggle FFmpeg playback: {ex.Message}");
        }
    }

    private async Task<bool> EnsureVideoPreviewFileAsync(VideoClip videoClip, long sessionId)
    {
        var assetId = GetPreviewAssetId(videoClip);
        if (_currentTempVideoAssetId == assetId
            && !string.IsNullOrEmpty(_currentTempVideoPath)
            && File.Exists(_currentTempVideoPath))
        {
            return true;
        }

        _videoPreviewLoadCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _videoPreviewLoadCts = loadCts;
        var token = loadCts.Token;

        string? createdTempPath = null;
        try
        {
            MediaPlayback.BeginVideoLoad(null, "Preparing embedded preview...");
            VideoPlayButton.IsEnabled = false;
            VideoStopButton.IsEnabled = false;
            StatusStripUpdate($"Preparing video preview: {videoClip.m_Name}");

            var tempPath = await Task.Run(() => PrepareVideoPreviewFile(videoClip, token), token);
            createdTempPath = tempPath;
            token.ThrowIfCancellationRequested();

            if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                || sessionId != Volatile.Read(ref _videoSessionCounter))
            {
                DeleteTempVideoFileBackground(createdTempPath);
                return false;
            }

            if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
            {
                MediaPlayback.SetError("VideoClip data is empty or invalid.", "Missing");
                StatusStripUpdate(MediaPlayback.ErrorMessage);
                return false;
            }

            var oldPath = _currentTempVideoPath;
            _currentTempVideoPath = tempPath;
            _currentTempVideoAssetId = assetId;
            createdTempPath = null; // Ownership transferred
            if (!string.IsNullOrEmpty(oldPath) && !string.Equals(oldPath, tempPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteTempVideoFileBackground(oldPath);
            }

            _videoLengthMs = 0;
            MediaPlayback.SetMediaInfo("Embedded FFmpeg preview loaded.");
            StatusStripUpdate($"Loaded video clip with FFmpeg backend: {videoClip.m_Name}");
            return true;
        }
        catch (OperationCanceledException)
        {
            DeleteTempVideoFileBackground(createdTempPath);
            return false;
        }
        catch (Exception ex)
        {
            DeleteTempVideoFileBackground(createdTempPath);
            if (ReferenceEquals(currentPreviewVideoClip, videoClip)
                && sessionId == Volatile.Read(ref _videoSessionCounter))
            {
                MediaPlayback.SetError($"Failed to prepare preview: {ex.Message}", "Error");
                StatusStripUpdate($"Failed to prepare video preview: {ex.Message}");
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_videoPreviewLoadCts, loadCts))
            {
                _videoPreviewLoadCts = null;
            }
            loadCts.Dispose();
            if (ReferenceEquals(currentPreviewVideoClip, videoClip)
                && sessionId == Volatile.Read(ref _videoSessionCounter))
            {
                VideoPlayButton.IsEnabled = true;
                VideoStopButton.IsEnabled = true;
                MediaPlayback.EndPreparing();
            }
        }
    }

    private string? PrepareVideoPreviewFile(VideoClip videoClip, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (videoClip.m_ExternalResources.m_Size <= 0)
        {
            return null;
        }

        var ext = SanitizeExtension(Path.GetExtension(videoClip.m_OriginalPath), ".mp4");

        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "Temp");
        Directory.CreateDirectory(tempDir);

        token.ThrowIfCancellationRequested();
        var tempPath = Path.Combine(tempDir, $"temp_video_{FixFileName(videoClip.m_Name)}_{videoClip.m_PathID}_{Guid.NewGuid():N}{ext}");
        try
        {
            videoClip.m_VideoData.WriteData(tempPath);
        }
        catch
        {
            TryDeletePartialExport(tempPath);
            throw;
        }

        if (token.IsCancellationRequested)
        {
            TryDeletePartialExport(tempPath);
            token.ThrowIfCancellationRequested();
        }

        return File.Exists(tempPath) && new FileInfo(tempPath).Length > 0 ? tempPath : null;
    }

    private async void StartVideoThumbnailPreview(VideoClip videoClip, long sessionId)
    {
        try
        {
            await Task.Delay(120);
            if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                || sessionId != Volatile.Read(ref _videoSessionCounter))
            {
                return;
            }

            if (!await EnsureVideoPreviewFileAsync(videoClip, sessionId))
            {
                return;
            }

            if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                || sessionId != Volatile.Read(ref _videoSessionCounter)
                || string.IsNullOrEmpty(_currentTempVideoPath)
                || !File.Exists(_currentTempVideoPath))
            {
                return;
            }

            var path = _currentTempVideoPath;
            bool opened = await Task.Run(() =>
            {
                try
                {
                    FfmpegVideoPlayer.Open(path);
                    return FfmpegVideoPlayer.HasMediaLoaded;
                }
                catch
                {
                    return false;
                }
            });

            if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                || sessionId != Volatile.Read(ref _videoSessionCounter))
            {
                return;
            }

            if (opened)
            {
                var durationMs = Math.Max(0, FfmpegVideoPlayer.Duration);
                _videoLengthMs = durationMs;
                MediaPlayback.MarkVideoReady(durationMs / 1000.0, "FFmpeg", "Thumbnail ready. Press Play to start preview.");
                if (!MediaPlayback.IsPlaying && !MediaPlayback.IsPaused)
                {
                    VideoProgressBar.Value = 0;
                }
            }
            else
            {
                MediaPlayback.SetError("FFmpeg could not open this VideoClip.", "Unsupported");
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(currentPreviewVideoClip, videoClip)
                && sessionId == Volatile.Read(ref _videoSessionCounter))
            {
                MediaPlayback.SetError($"Failed to load thumbnail: {ex.Message}", "Error");
            }
        }
    }

    private void VideoVolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        MediaPlayback.SetVideoVolume((int)VideoVolumeBar.Value);
        FfmpegVideoPlayer.Volume = MediaPlayback.IsMuted ? 0 : MediaPlayback.VideoVolume;
    }

    private void FfmpegVideoTimer_Tick(object? sender, EventArgs e)
    {
        if (_isVideoDragging)
        {
            return;
        }

        try
        {
            var duration = Math.Max(0, FfmpegVideoPlayer.Duration);
            var position = Math.Max(0, FfmpegVideoPlayer.Position);
            _videoLengthMs = duration;

            MediaPlayback.UpdatePosition(position / 1000.0, duration / 1000.0);

            if (duration > 0)
            {
                _isUpdatingVideoProgress = true;
                VideoProgressBar.Value = MediaPlayback.ProgressPermille;
                _isUpdatingVideoProgress = false;
            }

            if (!FfmpegVideoPlayer.IsPlaying && MediaPlayback.IsPlaying)
            {
                MediaPlayback.SetPaused();
                _ffmpegVideoTimer?.Stop();
            }
        }
        catch
        {
        }
    }

    private void FfmpegVideoPlayer_MediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                _ffmpegVideoTimer?.Stop();

                var loopPath = _currentTempVideoPath;
                var sessionId = Volatile.Read(ref _videoSessionCounter);
                var loopEnabled = MediaPlayback.IsLooping;
                if (loopEnabled
                    && !string.IsNullOrEmpty(loopPath)
                    && File.Exists(loopPath))
                {
                    bool reopened = await Task.Run(() =>
                    {
                        try
                        {
                            FfmpegVideoPlayer.Open(loopPath);
                            return FfmpegVideoPlayer.HasMediaLoaded;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (reopened && sessionId == Volatile.Read(ref _videoSessionCounter))
                    {
                        FfmpegVideoPlayer.Volume = MediaPlayback.IsMuted ? 0 : MediaPlayback.VideoVolume;
                        FfmpegVideoPlayer.Play();
                        _ffmpegVideoTimer?.Start();
                        MediaPlayback.UpdatePosition(0);
                        MediaPlayback.SetPlaying();
                        return;
                    }
                }

                if (sessionId == Volatile.Read(ref _videoSessionCounter))
                {
                    if (MediaPlayback.IsVideoActive)
                    {
                        MediaPlayback.SetStopped();
                    }
                    SetVideoStoppedUi();
                }
            }
            catch
            {
                if (MediaPlayback.IsVideoActive)
                {
                    MediaPlayback.SetStopped();
                }
                SetVideoStoppedUi();
            }
        });
    }

    private void VideoProgressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingVideoProgress || !FfmpegVideoPlayer.HasMediaLoaded)
            return;

        var ratio = VideoProgressBar.Value / 1000.0;
        if (_videoLengthMs > 0)
        {
            MediaPlayback.UpdatePosition((_videoLengthMs * ratio) / 1000.0);
        }

        FfmpegVideoPlayer.Seek((float)ratio);
    }

    private void VideoProgressBar_DragStarted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isVideoDragging = true;
    }

    private void VideoProgressBar_DragCompleted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isVideoDragging = false;
        if (!FfmpegVideoPlayer.HasMediaLoaded)
        {
            return;
        }

        var ratio = VideoProgressBar.Value / 1000.0;
        if (_videoLengthMs > 0)
        {
            MediaPlayback.UpdatePosition((_videoLengthMs * ratio) / 1000.0);
        }

        FfmpegVideoPlayer.Seek((float)ratio);
    }

    private async Task ExecuteVideoExportAsync()
    {
        var videoClip = currentPreviewVideoClip;
        if (videoClip == null) return;

        // Pause playback to prevent event loops and potential UI deadlocks while the modal dialog is open
        try
        {
            if (FfmpegVideoPlayer.IsPlaying)
            {
                FfmpegVideoPlayer.Pause();
                _ffmpegVideoTimer?.Stop();
                MediaPlayback.SetPaused();
            }
        }
        catch { }

        var extension = SanitizeExtension(Path.GetExtension(videoClip.m_OriginalPath), ".mp4");

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        var fileName = FixFileName(videoClip.m_Name) + extension;
        var filePath = Path.Combine(savePath, fileName);

        try
        {
            if (videoClip.m_ExternalResources.m_Size <= 0)
            {
                StatusStripUpdate("VideoClip data is empty or invalid.");
                return;
            }

            await Task.Run(() =>
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                videoClip.m_VideoData.WriteData(filePath);
            });
            StatusStripUpdate($"Successfully exported video clip to: {filePath}");
        }
        catch (Exception ex)
        {
            TryDeletePartialExport(filePath);
            StatusStripUpdate($"Failed to export video: {ex.Message}");
        }
    }
}
