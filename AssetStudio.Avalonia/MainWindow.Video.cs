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
    private sealed class ActiveVideoPreviewSource
    {
        public required string AssetIdKey { get; init; }
        public required string Title { get; init; }
        public required string ExportBaseName { get; init; }
        public required string Extension { get; init; }
        public required string KindDisplayName { get; init; }
        public VideoClip? VideoClip { get; init; }
        public byte[]? RawVideoBytes { get; init; }
        public long PathId { get; init; }
    }

    private readonly VideoPreviewSessionCoordinator _videoPreviewCoordinator =
        new(teardownQueue: Controls.VideoPlayerControl.SharedTeardownQueue);

    private ActiveVideoPreviewSource? _currentVideoPreviewSource;
    private string? _currentTempVideoPath;
    private string? _currentTempVideoAssetId;
    private CancellationTokenSource? _videoPreviewLoadCts;
    private Task<bool>? _activeVideoLoadTask;
    private long _activeVideoLoadSessionId = -1;
    private long _autoPlayRequestedSessionId = -1;
    private long _videoSessionCounter;
    private bool _isUpdatingVideoProgress;
    private bool _isVideoDragging;
    private bool _videoHasExplicitSeekPosition;
    private bool _staleTempCleanupStarted;
    private long _videoLengthMs;
    private DispatcherTimer? _ffmpegVideoTimer;

    public void InitializeVideoPlayer()
    {
        _ffmpegVideoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _ffmpegVideoTimer.Tick += FfmpegVideoTimer_Tick;

        if (!_staleTempCleanupStarted)
        {
            _staleTempCleanupStarted = true;
            _ = VideoPreviewService.CleanupStaleTempVideoFilesInBackground();
        }
    }

    private static void DeleteTempVideoFileBackground(string? filePath)
    {
        MediaTempFiles.DeleteInBackground(filePath, attempts: 8, retryDelayMs: 50);
    }

    private long BeginNextVideoSession(string? assetIdKey = null)
    {
        var nextSession = _videoPreviewCoordinator.BeginSession(assetIdKey);
        Interlocked.Exchange(ref _videoSessionCounter, nextSession);
        return nextSession;
    }

    private bool IsVideoSourceCurrent(ActiveVideoPreviewSource source, long sessionId)
    {
        if (!ReferenceEquals(_currentVideoPreviewSource, source)
            || sessionId != Volatile.Read(ref _videoSessionCounter)
            || !_videoPreviewCoordinator.IsSessionCurrent(sessionId))
        {
            return false;
        }

        if (source.VideoClip != null && !ReferenceEquals(currentPreviewVideoClip, source.VideoClip))
        {
            return false;
        }

        return true;
    }

    internal void OnPreviewAssetQueuedForVideo(AssetItem? assetItem)
    {
        var currentSource = _currentVideoPreviewSource;
        var queuedId = assetItem?.Handle != null ? assetItem.Handle.UniqueID : assetItem?.UniqueID;
        if (currentSource != null && !string.Equals(currentSource.AssetIdKey, queuedId, StringComparison.Ordinal))
        {
            VideoReset();
        }
        else if (currentSource == null)
        {
            CancelVideoPreviewLoad();
        }
    }

    internal static bool ShouldSkipPreloadWindowForVideo(AssetItem? currentItem)
    {
        return currentItem != null
            && (currentItem.Type == ClassIDType.VideoClip
                || currentItem.Type == ClassIDType.VideoPlayer
                || currentItem.Type == ClassIDType.MovieTexture);
    }

    internal bool IsVideoPreviewBusyForPreload()
    {
        return _currentVideoPreviewSource != null
            || _videoPreviewLoadCts != null
            || MediaPlayback.IsPreparing;
    }

    private static string FormatPrefixedVideoLabel(string prefix, string? rawText, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return fallback;
        }

        var trimmed = rawText.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return prefix + trimmed;
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

    private void SetEmbeddedVideoAudioLabel(string? audioText)
    {
        if (VideoAudioLabel == null)
        {
            return;
        }

        var formatted = FormatPrefixedVideoLabel("Audio: ", audioText, "Audio: Probed by FFmpeg on load");
        VideoAudioLabel.Text = formatted;

        bool hasNoAudio = formatted.Contains("no embedded", StringComparison.OrdinalIgnoreCase)
            || formatted.Contains("no playable", StringComparison.OrdinalIgnoreCase)
            || formatted.EndsWith(": none", StringComparison.OrdinalIgnoreCase);

        VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(
            global::Avalonia.Media.Color.Parse(hasNoAudio ? "#ff9800" : "#00e676"));
    }

    private void VideoStop()
    {
        Interlocked.Exchange(ref _autoPlayRequestedSessionId, -1);
        _videoHasExplicitSeekPosition = false;
        CancelVideoPreviewLoad();

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
        Interlocked.Exchange(ref _autoPlayRequestedSessionId, -1);
        _videoPreviewCoordinator.CancelInFlightPreparation();

        try
        {
            _videoPreviewLoadCts?.Cancel();
        }
        catch { }
    }

    private void VideoReset(bool clearTempCache = false)
    {
        CancelVideoPreviewLoad();
        BeginNextVideoSession();
        _currentVideoPreviewSource = null;
        _activeVideoLoadTask = null;
        _activeVideoLoadSessionId = -1;

        try
        {
            _ffmpegVideoTimer?.Stop();
        }
        catch { }

        var pathToRelease = _currentTempVideoPath;
        var assetIdToRelease = _currentTempVideoAssetId;
        _currentTempVideoPath = null;
        _currentTempVideoAssetId = null;
        _videoLengthMs = 0;
        _videoHasExplicitSeekPosition = false;
        MediaPlayback.ResetIfActive(ActiveMediaKind.Video);
        SetVideoStoppedUi();

        // Detach/dispose native FFmpeg player on the dedicated background teardown queue
        // and release/evict the temp video file only after the native file handle is closed.
        var player = FfmpegVideoPlayer;
        if (player != null)
        {
            try
            {
                player.ResetMedia(() =>
                {
                    _videoPreviewCoordinator.NotifyPlayerReleasedFile(
                        assetIdToRelease,
                        pathToRelease,
                        deleteImmediately: clearTempCache);
                });
            }
            catch
            {
                _videoPreviewCoordinator.NotifyPlayerReleasedFile(
                    assetIdToRelease,
                    pathToRelease,
                    deleteImmediately: clearTempCache);
            }
        }
        else
        {
            _videoPreviewCoordinator.NotifyPlayerReleasedFile(
                assetIdToRelease,
                pathToRelease,
                deleteImmediately: clearTempCache);
        }

        if (clearTempCache)
        {
            _videoPreviewCoordinator.ResetAndClearCache();
        }
    }

    private Task VideoResetAsync()
    {
        VideoReset();
        return Task.CompletedTask;
    }

    private void SetVideoStoppedUi()
    {
        SetVideoProgressBarPermilleSafe(0);
    }

    private void SetVideoProgressBarPermilleSafe(double value)
    {
        if (VideoProgressBar == null)
        {
            return;
        }

        _isUpdatingVideoProgress = true;
        try
        {
            VideoProgressBar.Value = value;
        }
        finally
        {
            _isUpdatingVideoProgress = false;
        }
    }

    private void PreviewVideoClip(AssetItem assetItem, VideoClip m_VideoClip)
    {
        var videoAssetId = GetPreviewAssetId(m_VideoClip);
        long sessionId;
        if (_currentTempVideoAssetId == videoAssetId)
        {
            VideoStop();
            sessionId = BeginNextVideoSession(videoAssetId);
        }
        else
        {
            VideoReset();
            sessionId = BeginNextVideoSession(videoAssetId);
        }

        currentPreviewVideoClip = m_VideoClip;

        var codecInfo = VideoPreviewService.InspectVideoClip(m_VideoClip);
        var ext = SanitizeExtension(codecInfo.RecommendedExtension, ".mp4");
        var source = new ActiveVideoPreviewSource
        {
            AssetIdKey = videoAssetId,
            Title = m_VideoClip.m_Name,
            ExportBaseName = m_VideoClip.m_Name,
            Extension = ext,
            KindDisplayName = "VideoClip",
            VideoClip = m_VideoClip,
            PathId = m_VideoClip.m_PathID
        };
        _currentVideoPreviewSource = source;

        PreviewSession.ActivateSurface(PreviewSurfaceKind.Video, assetItem, title: m_VideoClip.m_Name);
        MediaPlayback.SelectVideo(m_VideoClip.m_Name, "Ready. Press Play to prepare embedded preview.");
        MediaPlayback.SetVideoVolume(MediaPlaybackViewModel.DefaultVolume);
        if (codecInfo.EstimatedDurationSeconds > 0)
        {
            _videoLengthMs = (long)Math.Round(codecInfo.EstimatedDurationSeconds * 1000.0);
            FfmpegVideoPlayer?.SetFallbackDuration(_videoLengthMs);
            MediaPlayback.UpdatePosition(0, codecInfo.EstimatedDurationSeconds);
        }

        if (VideoResolutionLabel != null)
        {
            VideoResolutionLabel.Text = $"Resolution: {m_VideoClip.m_Width}x{m_VideoClip.m_Height} (Proxy: {m_VideoClip.m_ProxyWidth}x{m_VideoClip.m_ProxyHeight})";
        }
        if (VideoFrameRateLabel != null)
        {
            VideoFrameRateLabel.Text = $"Frame Rate: {m_VideoClip.m_FrameRate:F2} FPS | Frames: {m_VideoClip.m_FrameCount}";
        }
        if (VideoFormatLabel != null)
        {
            VideoFormatLabel.Text = $"Format: {ext.ToUpperInvariant().TrimStart('.')} ({codecInfo.VideoCodec})";
        }

        SetInitialVideoAudioLabel(m_VideoClip);

        if (VideoPlayButton != null) VideoPlayButton.IsEnabled = true;
        if (VideoStopButton != null) VideoStopButton.IsEnabled = true;
        if (VideoExportButton != null) VideoExportButton.IsEnabled = true;
        if (VideoVolumeBar != null) VideoVolumeBar.Value = MediaPlayback.VideoVolume;

        if (PreviewLabel != null) PreviewLabel.IsVisible = false;
        StartVideoThumbnailPreview(source, sessionId);
        StatusStripUpdate($"Loaded video clip: {m_VideoClip.m_Name}");
    }

    private void PreviewMovieTexture(AssetItem assetItem, MovieTexture m_MovieTexture)
    {
        var metadata = MovieTexturePreviewService.Inspect(m_MovieTexture);
        var movieBytes = m_MovieTexture.m_MovieData;

        if (!metadata.HasMovieData || movieBytes == null || movieBytes.Length == 0)
        {
            VideoReset();
            PreviewSession.ShowTextPreview(metadata.SummaryText, PreviewSurfaceKind.Text, assetItem, truncate: false);
            if (TextPreviewBox != null)
            {
                TextPreviewBox.Text = metadata.SummaryText;
                TextPreviewBox.IsVisible = true;
            }
            if (PreviewLabel != null)
            {
                PreviewLabel.IsVisible = false;
            }
            StatusStripUpdate($"MovieTexture '{metadata.AssetName}' contains no embedded video data.");
            return;
        }

        var assetIdKey = GetPreviewAssetId(m_MovieTexture);
        var title = !string.IsNullOrWhiteSpace(m_MovieTexture.m_Name)
            ? m_MovieTexture.m_Name
            : (!string.IsNullOrWhiteSpace(assetItem.Name) ? assetItem.Name : "MovieTexture");

        PreviewEmbeddedVideoBytes(
            assetItem: assetItem,
            title: title,
            videoBytes: movieBytes,
            assetIdKey: assetIdKey,
            extension: metadata.RecommendedExtension,
            formatLabel: metadata.FormatLabelText,
            resolutionText: metadata.ResolutionText,
            frameRateText: metadata.FrameRateText,
            audioText: metadata.AudioLabelText,
            readyInfoText: metadata.ReadyInfoText,
            defaultLoop: metadata.IsLooping);

        StatusStripUpdate($"Loaded MovieTexture: {title} ({metadata.DetectedFormat}, {MovieTexturePreviewService.FormatByteSize(metadata.ByteSize)})");
    }

    internal void PreviewEmbeddedVideoBytes(
        AssetItem assetItem,
        string title,
        byte[] videoBytes,
        string assetIdKey,
        string extension,
        string formatLabel,
        string resolutionText,
        string frameRateText,
        string audioText,
        string? readyInfoText = null,
        bool defaultLoop = false)
    {
        var resolvedTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : (!string.IsNullOrWhiteSpace(assetItem?.Name) ? assetItem.Name : "Embedded Video");

        if (videoBytes == null || videoBytes.Length == 0)
        {
            VideoReset();
            var fallback = $"Embedded video '{resolvedTitle}' contains no video data (0 bytes).";
            PreviewSession.ShowTextPreview(fallback, PreviewSurfaceKind.Text, assetItem, truncate: false);
            if (TextPreviewBox != null)
            {
                TextPreviewBox.Text = fallback;
                TextPreviewBox.IsVisible = true;
            }
            if (PreviewLabel != null)
            {
                PreviewLabel.IsVisible = false;
            }
            StatusStripUpdate(fallback);
            return;
        }

        var resolvedAssetId = !string.IsNullOrWhiteSpace(assetIdKey)
            ? assetIdKey
            : (!string.IsNullOrWhiteSpace(assetItem?.UniqueID) ? assetItem.UniqueID : resolvedTitle);

        var ext = SanitizeExtension(extension, ".ogv");
        var exportBaseName = !string.IsNullOrWhiteSpace(assetItem?.Name) ? assetItem.Name : resolvedTitle;
        var kindDisplayName = assetItem?.Type == ClassIDType.MovieTexture
            ? "MovieTexture"
            : (!string.IsNullOrWhiteSpace(assetItem?.DisplayType) ? assetItem.DisplayType : "Video");

        long sessionId;
        if (_currentTempVideoAssetId == resolvedAssetId)
        {
            VideoStop();
            sessionId = BeginNextVideoSession(resolvedAssetId);
        }
        else
        {
            VideoReset();
            sessionId = BeginNextVideoSession(resolvedAssetId);
        }

        currentPreviewVideoClip = null;

        var source = new ActiveVideoPreviewSource
        {
            AssetIdKey = resolvedAssetId,
            Title = resolvedTitle,
            ExportBaseName = exportBaseName,
            Extension = ext,
            KindDisplayName = kindDisplayName,
            RawVideoBytes = videoBytes,
            PathId = assetItem?.PathID ?? 0
        };
        _currentVideoPreviewSource = source;

        if (TextPreviewBox != null)
        {
            TextPreviewBox.IsVisible = false;
        }
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.IsVisible = false;
        }

        PreviewSession.ActivateSurface(PreviewSurfaceKind.Video, assetItem, title: resolvedTitle);

        var infoText = !string.IsNullOrWhiteSpace(readyInfoText)
            ? readyInfoText
            : "Ready. Press Play to prepare embedded preview.";

        MediaPlayback.SelectVideo(resolvedTitle, infoText);
        MediaPlayback.SetVideoVolume(MediaPlaybackViewModel.DefaultVolume);
        MediaPlayback.SetLooping(defaultLoop);

        if (VideoResolutionLabel != null)
        {
            VideoResolutionLabel.Text = FormatPrefixedVideoLabel("Resolution: ", resolutionText, "Resolution: Probed by FFmpeg on load");
        }
        if (VideoFrameRateLabel != null)
        {
            VideoFrameRateLabel.Text = FormatPrefixedVideoLabel("Frame Rate: ", frameRateText, "Frame Rate: Probed by FFmpeg on load");
        }
        if (VideoFormatLabel != null)
        {
            VideoFormatLabel.Text = FormatPrefixedVideoLabel("Format: ", formatLabel, $"Format: {ext.ToUpperInvariant().TrimStart('.')}");
        }

        SetEmbeddedVideoAudioLabel(audioText);

        if (VideoPlayButton != null) VideoPlayButton.IsEnabled = true;
        if (VideoStopButton != null) VideoStopButton.IsEnabled = true;
        if (VideoExportButton != null) VideoExportButton.IsEnabled = true;
        if (VideoVolumeBar != null) VideoVolumeBar.Value = MediaPlayback.VideoVolume;
        if (PreviewLabel != null) PreviewLabel.IsVisible = false;

        StartVideoThumbnailPreview(source, sessionId);
        StatusStripUpdate($"Loaded video preview: {resolvedTitle}");
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
        var source = _currentVideoPreviewSource;
        if (source == null)
        {
            return;
        }

        var sessionId = Volatile.Read(ref _videoSessionCounter);

        try
        {
            if (FfmpegVideoPlayer.IsPlaying)
            {
                Interlocked.Exchange(ref _autoPlayRequestedSessionId, -1);
                FfmpegVideoPlayer.Pause();
                _ffmpegVideoTimer?.Stop();
                MediaPlayback.SetPaused();
                return;
            }

            var hasLoadedFile = FfmpegVideoPlayer.HasMediaLoaded
                && _currentTempVideoAssetId == source.AssetIdKey
                && !string.IsNullOrEmpty(_currentTempVideoPath)
                && File.Exists(_currentTempVideoPath);

            if (!hasLoadedFile)
            {
                Interlocked.Exchange(ref _autoPlayRequestedSessionId, sessionId);
                if (!await EnsureVideoPreviewLoadedAsync(source, sessionId, isThumbnailLoad: false))
                {
                    return;
                }

                if (!IsVideoSourceCurrent(source, sessionId))
                {
                    return;
                }
            }

            Interlocked.Exchange(ref _autoPlayRequestedSessionId, -1);
            FfmpegVideoPlayer.Volume = MediaPlayback.IsMuted ? 0 : MediaPlayback.VideoVolume;
            if (hasLoadedFile
                && MediaPlayback.PlaybackState == MediaPlaybackState.Stopped
                && !MediaPlayback.IsReady
                && !_videoHasExplicitSeekPosition
                && FfmpegVideoPlayer.Position > 0)
            {
                FfmpegVideoPlayer.Seek(0f);
            }
            _videoHasExplicitSeekPosition = false;

            if (!FfmpegVideoPlayer.IsPlaying)
            {
                FfmpegVideoPlayer.Play();
            }
            _ffmpegVideoTimer?.Start();
            MediaPlayback.SetPlaying();
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to toggle FFmpeg playback: {ex.Message}");
        }
    }

    private Task<bool> EnsureVideoPreviewLoadedAsync(
        ActiveVideoPreviewSource source,
        long sessionId,
        bool isThumbnailLoad)
    {
        if (_activeVideoLoadTask != null
            && _activeVideoLoadSessionId == sessionId
            && !_activeVideoLoadTask.IsCompleted)
        {
            return _activeVideoLoadTask;
        }

        var task = RunVideoPreviewLoadCoreAsync(source, sessionId, isThumbnailLoad);
        _activeVideoLoadSessionId = sessionId;
        _activeVideoLoadTask = task;
        return task;
    }

    private async Task<bool> RunVideoPreviewLoadCoreAsync(
        ActiveVideoPreviewSource source,
        long sessionId,
        bool isThumbnailLoad)
    {
        if (!IsVideoSourceCurrent(source, sessionId))
        {
            return false;
        }

        if (FfmpegVideoPlayer.HasMediaLoaded
            && _currentTempVideoAssetId == source.AssetIdKey
            && !string.IsNullOrEmpty(_currentTempVideoPath)
            && File.Exists(_currentTempVideoPath))
        {
            return true;
        }

        _videoPreviewLoadCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _videoPreviewLoadCts = loadCts;
        var token = loadCts.Token;

        try
        {
            MediaPlayback.BeginVideoLoad(null, "Preparing embedded preview...");
            if (VideoStopButton != null) VideoStopButton.IsEnabled = true;
            StatusStripUpdate($"Preparing video preview: {source.Title}");

            var prepared = await _videoPreviewCoordinator.PrepareOrReuseFileAsync(
                source.AssetIdKey,
                sessionId,
                ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                    return VideoPreviewService.PrepareVideoFile(
                        source.AssetIdKey,
                        source.ExportBaseName,
                        source.PathId,
                        source.Extension,
                        source.VideoClip,
                        source.RawVideoBytes,
                        _videoPreviewCoordinator.TempDirectory,
                        linked.Token);
                });

            if (token.IsCancellationRequested || !IsVideoSourceCurrent(source, sessionId))
            {
                return false;
            }

            if (prepared == null || string.IsNullOrEmpty(prepared.FilePath) || !File.Exists(prepared.FilePath))
            {
                MediaPlayback.SetError($"{source.KindDisplayName} data is empty or invalid.", "Missing");
                StatusStripUpdate(MediaPlayback.ErrorMessage);
                return false;
            }

            var oldPath = _currentTempVideoPath;
            var oldAssetId = _currentTempVideoAssetId;
            _currentTempVideoPath = prepared.FilePath;
            _currentTempVideoAssetId = source.AssetIdKey;

            if (!string.IsNullOrEmpty(oldPath)
                && !string.Equals(oldPath, prepared.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _videoPreviewCoordinator.NotifyPlayerReleasedFile(oldAssetId, oldPath, deleteImmediately: false);
            }

            bool opened = await FfmpegVideoPlayer.OpenAsync(prepared.FilePath, token);
            if (token.IsCancellationRequested || !IsVideoSourceCurrent(source, sessionId))
            {
                return false;
            }

            if (!opened || !FfmpegVideoPlayer.HasMediaLoaded)
            {
                MediaPlayback.SetError($"FFmpeg could not open this {source.KindDisplayName}.", "Unsupported");
                StatusStripUpdate($"Video preview could not open this {source.KindDisplayName}.");
                return false;
            }

            var durationMs = Math.Max(0, FfmpegVideoPlayer.Duration);
            if (durationMs > 0)
            {
                _videoLengthMs = durationMs;
            }
            else if (_videoLengthMs > 0)
            {
                FfmpegVideoPlayer.SetFallbackDuration(_videoLengthMs);
            }

            var readyDurationSec = (_videoLengthMs > 0 ? _videoLengthMs : durationMs) / 1000.0;
            var readyMessage = isThumbnailLoad
                ? "Thumbnail ready. Press Play to start preview."
                : "Embedded FFmpeg preview loaded.";

            MediaPlayback.MarkVideoReady(readyDurationSec, "FFmpeg", readyMessage);
            if (!MediaPlayback.IsPlaying && !MediaPlayback.IsPaused)
            {
                SetVideoProgressBarPermilleSafe(0);
            }

            StatusStripUpdate($"Loaded video with FFmpeg backend: {source.Title}");

            if (Interlocked.Read(ref _autoPlayRequestedSessionId) == sessionId
                && IsVideoSourceCurrent(source, sessionId))
            {
                Interlocked.Exchange(ref _autoPlayRequestedSessionId, -1);
                FfmpegVideoPlayer.Volume = MediaPlayback.IsMuted ? 0 : MediaPlayback.VideoVolume;
                FfmpegVideoPlayer.Play();
                _ffmpegVideoTimer?.Start();
                MediaPlayback.SetPlaying();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsVideoSourceCurrent(source, sessionId))
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

            if (IsVideoSourceCurrent(source, sessionId))
            {
                if (VideoPlayButton != null) VideoPlayButton.IsEnabled = true;
                if (VideoStopButton != null) VideoStopButton.IsEnabled = true;
                MediaPlayback.EndPreparing();
            }
        }
    }

    private async void StartVideoThumbnailPreview(ActiveVideoPreviewSource source, long sessionId)
    {
        try
        {
            await Task.Delay(80);
            if (!IsVideoSourceCurrent(source, sessionId))
            {
                return;
            }

            await EnsureVideoPreviewLoadedAsync(source, sessionId, isThumbnailLoad: true);
        }
        catch (Exception ex)
        {
            if (IsVideoSourceCurrent(source, sessionId))
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

    private void SyncVideoPositionUi(long positionMs)
    {
        var duration = Math.Max(0, FfmpegVideoPlayer.Duration);
        if (duration > 0)
        {
            _videoLengthMs = duration;
        }
        else if (_videoLengthMs > 0)
        {
            FfmpegVideoPlayer.SetFallbackDuration(_videoLengthMs);
        }

        var effectiveDuration = _videoLengthMs > 0 ? _videoLengthMs : duration;
        var clampedPos = Math.Max(0L, positionMs);
        if (effectiveDuration > 0 && clampedPos > effectiveDuration)
        {
            clampedPos = effectiveDuration;
        }

        MediaPlayback.UpdatePosition(clampedPos / 1000.0, effectiveDuration / 1000.0);

        if (effectiveDuration > 0)
        {
            SetVideoProgressBarPermilleSafe(MediaPlayback.ProgressPermille);
        }
    }

    private void FfmpegVideoPlayer_PositionUpdated(object? sender, long positionMs)
    {
        if (_isVideoDragging || _currentVideoPreviewSource == null || !MediaPlayback.IsVideoActive)
        {
            return;
        }

        if (MediaPlayback.PlaybackState == MediaPlaybackState.Stopped
            && !MediaPlayback.IsReady
            && !_videoHasExplicitSeekPosition)
        {
            return;
        }

        try
        {
            SyncVideoPositionUi(positionMs);
        }
        catch
        {
        }
    }

    private void FfmpegVideoTimer_Tick(object? sender, EventArgs e)
    {
        if (_isVideoDragging)
        {
            return;
        }

        try
        {
            SyncVideoPositionUi(FfmpegVideoPlayer.Position);

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
                _videoHasExplicitSeekPosition = false;

                var loopPath = _currentTempVideoPath;
                var sessionId = Volatile.Read(ref _videoSessionCounter);
                var loopEnabled = MediaPlayback.IsLooping;
                if (loopEnabled
                    && !string.IsNullOrEmpty(loopPath)
                    && File.Exists(loopPath))
                {
                    bool reopened = await FfmpegVideoPlayer.OpenAsync(loopPath);

                    if (reopened && sessionId == Volatile.Read(ref _videoSessionCounter))
                    {
                        var durationMs = Math.Max(0, FfmpegVideoPlayer.Duration);
                        if (durationMs > 0)
                        {
                            _videoLengthMs = durationMs;
                        }
                        else if (_videoLengthMs > 0)
                        {
                            FfmpegVideoPlayer.SetFallbackDuration(_videoLengthMs);
                        }

                        FfmpegVideoPlayer.Volume = MediaPlayback.IsMuted ? 0 : MediaPlayback.VideoVolume;
                        FfmpegVideoPlayer.Play();
                        _ffmpegVideoTimer?.Start();
                        MediaPlayback.UpdatePosition(0, _videoLengthMs / 1000.0);
                        SetVideoProgressBarPermilleSafe(0);
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

        var ratio = Math.Clamp(VideoProgressBar.Value / 1000.0, 0.0, 1.0);
        if (_videoLengthMs > 0)
        {
            MediaPlayback.UpdatePosition((_videoLengthMs * ratio) / 1000.0);
        }

        if (!_isVideoDragging)
        {
            if (!FfmpegVideoPlayer.IsPlaying)
            {
                _videoHasExplicitSeekPosition = true;
            }
            FfmpegVideoPlayer.Seek((float)ratio);
        }
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

        var ratio = Math.Clamp(VideoProgressBar.Value / 1000.0, 0.0, 1.0);
        if (_videoLengthMs > 0)
        {
            MediaPlayback.UpdatePosition((_videoLengthMs * ratio) / 1000.0);
        }

        if (!FfmpegVideoPlayer.IsPlaying)
        {
            _videoHasExplicitSeekPosition = true;
        }
        FfmpegVideoPlayer.Seek((float)ratio);
    }

    private async Task ExecuteVideoExportAsync()
    {
        var source = _currentVideoPreviewSource;
        if (source == null)
        {
            return;
        }

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

        var extension = SanitizeExtension(source.Extension, ".mp4");

        var savePath = await PickExportFolderDialogAsync("Select the save folder");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        var safeBase = FixFileName(source.ExportBaseName);
        var fileName = safeBase.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? safeBase
            : safeBase + extension;
        var filePath = Path.Combine(savePath, fileName);

        try
        {
            if (source.VideoClip != null)
            {
                var extRes = source.VideoClip.m_ExternalResources;
                if (extRes == null || extRes.m_Size <= 0)
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
                    VideoPreviewService.ExtractVideoClipDataToFile(
                        source.VideoClip,
                        filePath,
                        extRes.m_Size,
                        CancellationToken.None);
                });
            }
            else if (source.RawVideoBytes != null && source.RawVideoBytes.Length > 0)
            {
                var rawBytes = source.RawVideoBytes;
                await Task.Run(() =>
                {
                    File.WriteAllBytes(filePath, rawBytes);
                });
            }
            else
            {
                StatusStripUpdate($"{source.KindDisplayName} data is empty or invalid.");
                return;
            }

            StatusStripUpdate($"Successfully exported video clip to: {filePath}");
        }
        catch (Exception ex)
        {
            TryDeletePartialExport(filePath);
            StatusStripUpdate($"Failed to export video: {ex.Message}");
        }
    }
}
