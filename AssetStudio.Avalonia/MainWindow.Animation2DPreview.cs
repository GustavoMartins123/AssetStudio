using Avalonia.Input;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private const double TwoDAnimationMinPreviewFill = 0.025;
    private const double TwoDAnimationDefaultPreviewFill = 0.10;
    private const double TwoDAnimationMaxPreviewFill = 1.0;
    private const double TwoDAnimationZoomFactor = 1.12;

    private DispatcherTimer? _2dAnimTimer;
    private List<(float time, AssetStudio.Object asset)> _2dAnimFrames = new();
    private Dictionary<AssetStudio.Object, global::Avalonia.Media.Imaging.Bitmap> _2dAnimBitmaps = new();
    private global::Avalonia.Media.Imaging.Bitmap? _2dAnimCurrentBitmap;
    private DateTime _2dAnimStartTime;
    private float _2dAnimDuration;
    private float _2dAnimPausedElapsedSeconds;
    private int _2dAnimCurrentFrameIndex;
    private bool _2dAnimPaused;
    private bool _is2dAnimationPreviewActive;
    private double _2dAnimPreviewFill = TwoDAnimationDefaultPreviewFill;

    private void Preview2DAnimationClip(AnimationClip clip)
    {
        var token = _animationPreviewCoordinator.BeginNewRequest(out var requestId);
        Preview2DAnimationClipAsync(clip, requestId, texturePreviewIdCounter, token);
    }

    private void Preview2DAnimationClipAsync(
        AnimationClip clip,
        long requestId,
        long previewId,
        CancellationToken cancellationToken)
    {
        Stop2DAnimation();
        StatusStripUpdate($"Loading 2D animation '{clip.m_Name}'...");

        _ = Task.Run(() =>
        {
            Dictionary<AssetStudio.Object, global::Avalonia.Media.Imaging.Bitmap>? decodedBitmaps = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureAnimationClip2DPreviewDependenciesLoaded(clip);
                cancellationToken.ThrowIfCancellationRequested();

                var pptrCurve = clip.m_PPtrCurves?.FirstOrDefault(c => c.curve != null && c.curve.Length > 0);
                if (pptrCurve == null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested
                            && _animationPreviewCoordinator.IsCurrent(requestId)
                            && previewId == texturePreviewIdCounter)
                        {
                            StatusStripUpdate("AnimationClip: No keyframes found in 2D animation curves.");
                        }
                    });
                    return;
                }

                var frames = new List<(float time, AssetStudio.Object asset)>();
                foreach (var kf in pptrCurve.curve)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (kf.value != null && !kf.value.IsNull)
                    {
                        if (kf.value.TryGet<Sprite>(out var sprite))
                        {
                            frames.Add((kf.time, sprite));
                        }
                        else if (kf.value.TryGet<Texture2D>(out var texture))
                        {
                            frames.Add((kf.time, texture));
                        }
                    }
                }

                if (frames.Count == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested
                            && _animationPreviewCoordinator.IsCurrent(requestId)
                            && previewId == texturePreviewIdCounter)
                        {
                            StatusStripUpdate("AnimationClip: No Sprites or Textures could be loaded for 2D animation.");
                        }
                    });
                    return;
                }

                var orderedFrames = frames.OrderBy(f => f.time).ToList();
                var duration = orderedFrames.Last().time;
                if (duration <= 0)
                {
                    duration = 1.0f;
                }

                decodedBitmaps = new Dictionary<AssetStudio.Object, global::Avalonia.Media.Imaging.Bitmap>();
                foreach (var f in orderedFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!decodedBitmaps.ContainsKey(f.asset))
                    {
                        try
                        {
                            SKBitmap? img = null;
                            if (f.asset is Sprite sprite)
                            {
                                img = sprite.GetImage();
                            }
                            else if (f.asset is Texture2D texture)
                            {
                                img = texture.ConvertToImage(true);
                            }

                            if (img != null)
                            {
                                using (img)
                                using (var ms = new MemoryStream())
                                {
                                    img.WriteToStream(ms, ImageFormat.Png);
                                    ms.Position = 0;
                                    var bmp = new global::Avalonia.Media.Imaging.Bitmap(ms);
                                    decodedBitmaps[f.asset] = bmp;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Log(LoggerEvent.Warning, $"Failed to decode 2D animation frame: {ex.Message}");
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var finalBitmaps = decodedBitmaps;
                decodedBitmaps = null;

                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !_animationPreviewCoordinator.IsCurrent(requestId)
                        || previewId != texturePreviewIdCounter)
                    {
                        foreach (var bmp in finalBitmaps.Values.Distinct())
                        {
                            bmp.Dispose();
                        }
                        return;
                    }

                    if (finalBitmaps.Count == 0)
                    {
                        StatusStripUpdate("AnimationClip: Failed to decode any sprite/texture frames for preview.");
                        return;
                    }

                    Stop2DAnimation();
                    _2dAnimFrames = orderedFrames;
                    _2dAnimDuration = duration;
                    _2dAnimBitmaps = finalBitmaps;

                    if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                    if (TextureGLPreview != null) TextureGLPreview.IsVisible = false;
                    if (TextPreviewBox != null) TextPreviewBox.IsVisible = false;
                    if (PreviewLabel != null) PreviewLabel.IsVisible = false;
                    if (ImagePreviewBox != null)
                    {
                        ImagePreviewBox.IsVisible = true;
                        ImagePreviewBox.Stretch = global::Avalonia.Media.Stretch.Fill;
                        ImagePreviewBox.StretchDirection = global::Avalonia.Media.StretchDirection.Both;
                    }

                    _is2dAnimationPreviewActive = true;
                    _2dAnimPaused = false;
                    _2dAnimPausedElapsedSeconds = 0;
                    _2dAnimCurrentFrameIndex = 0;
                    _2dAnimPreviewFill = TwoDAnimationDefaultPreviewFill;
                    _2dAnimStartTime = DateTime.UtcNow;
                    PreviewSession.ActivateSurface(
                        PreviewSurfaceKind.Animation2D,
                        AssetListDataGrid?.SelectedItem as AssetItem,
                        isLoading: false);
                    MeshPreview.SetAnimationState(
                        isLoaded: true,
                        clipName: clip.m_Name ?? string.Empty,
                        durationSeconds: _2dAnimDuration,
                        currentTimeSeconds: 0.0,
                        isPlaying: true,
                        totalFrames: _2dAnimFrames.Count,
                        sampleRate: clip.m_SampleRate > 0 ? clip.m_SampleRate : 30.0);
                    ShowAnimationPlayback(_2dAnimFrames.Count);
                    Render2DAnimationFrame(0);

                    _2dAnimTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(30)
                    };
                    _2dAnimTimer.Tick += On2DAnimTimerTick;
                    _2dAnimTimer.Start();

                    StatusStripUpdate($"AnimationClip: Playing 2D animation '{clip.m_Name}' ({_2dAnimFrames.Count} frames)...");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"2D animation preview failed for {clip.m_Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cancellationToken.IsCancellationRequested
                        && _animationPreviewCoordinator.IsCurrent(requestId)
                        && previewId == texturePreviewIdCounter)
                    {
                        StatusStripUpdate($"AnimationClip preview error: {ex.Message}");
                    }
                });
            }
            finally
            {
                if (decodedBitmaps != null)
                {
                    foreach (var bmp in decodedBitmaps.Values.Distinct())
                    {
                        bmp.Dispose();
                    }
                }
            }
        }, cancellationToken);
    }

    private void On2DAnimTimerTick(object? sender, EventArgs e)
    {
        if (_2dAnimFrames.Count == 0 || ImagePreviewBox == null)
        {
            Stop2DAnimation();
            return;
        }

        if (_2dAnimPaused)
        {
            return;
        }

        Render2DAnimationFrame(Get2DAnimationLoopTime());
    }

    private float Get2DAnimationLoopTime()
    {
        var elapsedSeconds = _2dAnimPaused
            ? _2dAnimPausedElapsedSeconds
            : (float)(DateTime.UtcNow - _2dAnimStartTime).TotalSeconds;
        return _2dAnimDuration > 0 ? elapsedSeconds % _2dAnimDuration : 0f;
    }

    private void Render2DAnimationFrame(float loopTime)
    {
        if (_2dAnimFrames.Count == 0)
        {
            return;
        }

        var frameIndex = _2dAnimFrames.FindLastIndex(f => f.time <= loopTime);
        if (frameIndex < 0)
        {
            frameIndex = 0;
        }

        var frame = _2dAnimFrames[frameIndex];
        if (_2dAnimBitmaps.TryGetValue(frame.asset, out var bitmap))
        {
            _2dAnimCurrentFrameIndex = frameIndex;
            Set2DAnimationFrame(bitmap);
            MeshPreview.SeekFrame(_2dAnimCurrentFrameIndex, _2dAnimFrames.Count);
            UpdateAnimationPlaybackFrame(_2dAnimCurrentFrameIndex, _2dAnimFrames.Count);
        }
    }

    private void Pause2DAnimation()
    {
        if (!_is2dAnimationPreviewActive || _2dAnimPaused)
        {
            return;
        }

        _2dAnimPausedElapsedSeconds = (float)(DateTime.UtcNow - _2dAnimStartTime).TotalSeconds;
        _2dAnimPaused = true;
        _2dAnimTimer?.Stop();
        MeshPreview.Pause();
        SetAnimationPlaybackPaused(true);
    }

    private void Play2DAnimation()
    {
        if (!_is2dAnimationPreviewActive || !_2dAnimPaused)
        {
            return;
        }

        _2dAnimStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(_2dAnimPausedElapsedSeconds);
        _2dAnimPaused = false;
        _2dAnimTimer?.Start();
        MeshPreview.Play();
        SetAnimationPlaybackPaused(false);
    }

    private void Restart2DAnimation()
    {
        if (!_is2dAnimationPreviewActive)
        {
            return;
        }

        _2dAnimPaused = false;
        _2dAnimPausedElapsedSeconds = 0;
        _2dAnimStartTime = DateTime.UtcNow;
        MeshPreview.Restart();
        Render2DAnimationFrame(0);
        _2dAnimTimer?.Start();
        SetAnimationPlaybackPaused(false);
    }

    private void Set2DAnimationFrame(global::Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (ImagePreviewBox == null)
        {
            return;
        }

        if (!ReferenceEquals(_2dAnimCurrentBitmap, bitmap))
        {
            _2dAnimCurrentBitmap = bitmap;
            ImagePreviewBox.Source = bitmap;
        }

        Update2DAnimationImageLayout();
    }

    private void Update2DAnimationImageLayout()
    {
        if (!_is2dAnimationPreviewActive || ImagePreviewBox == null || _2dAnimCurrentBitmap == null)
        {
            return;
        }

        var hostWidth = PreviewContentHost.Bounds.Width;
        var hostHeight = PreviewContentHost.Bounds.Height;
        var pixelWidth = Math.Max(1, _2dAnimCurrentBitmap.PixelSize.Width);
        var pixelHeight = Math.Max(1, _2dAnimCurrentBitmap.PixelSize.Height);
        if (hostWidth <= 1 || hostHeight <= 1)
        {
            ImagePreviewBox.Width = pixelWidth;
            ImagePreviewBox.Height = pixelHeight;
            return;
        }

        var fitScale = Math.Min(hostWidth / pixelWidth, hostHeight / pixelHeight);
        var fill = Math.Clamp(_2dAnimPreviewFill, TwoDAnimationMinPreviewFill, TwoDAnimationMaxPreviewFill);
        var scale = Math.Max(0.01, fitScale * fill);
        ImagePreviewBox.Width = pixelWidth * scale;
        ImagePreviewBox.Height = pixelHeight * scale;
    }

    private void ImagePreviewBox_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_is2dAnimationPreviewActive)
        {
            return;
        }

        _2dAnimPreviewFill *= e.Delta.Y > 0 ? TwoDAnimationZoomFactor : 1.0 / TwoDAnimationZoomFactor;
        _2dAnimPreviewFill = Math.Clamp(_2dAnimPreviewFill, TwoDAnimationMinPreviewFill, TwoDAnimationMaxPreviewFill);
        Update2DAnimationImageLayout();
        e.Handled = true;
    }

    private void Stop2DAnimation()
    {
        var wasActive = _is2dAnimationPreviewActive;
        _is2dAnimationPreviewActive = false;
        _2dAnimPaused = false;
        _2dAnimPausedElapsedSeconds = 0;
        if (_2dAnimTimer != null)
        {
            _2dAnimTimer.Stop();
            _2dAnimTimer.Tick -= On2DAnimTimerTick;
            _2dAnimTimer = null;
        }
        if (wasActive && ImagePreviewBox != null)
        {
            ImagePreviewBox.Source = null;
        }
        _2dAnimCurrentBitmap = null;
        _2dAnimCurrentFrameIndex = 0;
        _2dAnimFrames.Clear();
        foreach (var bitmap in _2dAnimBitmaps.Values.Distinct())
        {
            bitmap.Dispose();
        }
        _2dAnimBitmaps.Clear();
        _2dAnimPreviewFill = TwoDAnimationDefaultPreviewFill;
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.Width = double.NaN;
            ImagePreviewBox.Height = double.NaN;
            ImagePreviewBox.Stretch = global::Avalonia.Media.Stretch.Uniform;
            ImagePreviewBox.StretchDirection = global::Avalonia.Media.StretchDirection.DownOnly;
        }
    }
}
