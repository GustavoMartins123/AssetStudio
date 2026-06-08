using Avalonia.Input;
using Avalonia.Threading;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        Stop2DAnimation();
        EnsureAnimationClip2DPreviewDependenciesLoaded(clip);

        var pptrCurve = clip.m_PPtrCurves?.FirstOrDefault(c => c.curve != null && c.curve.Length > 0);
        if (pptrCurve == null)
        {
            StatusStripUpdate("AnimationClip: No keyframes found in 2D animation curves.");
            return;
        }

        var frames = new List<(float time, AssetStudio.Object asset)>();
        foreach (var kf in pptrCurve.curve)
        {
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
            StatusStripUpdate("AnimationClip: No Sprites or Textures could be loaded for 2D animation.");
            return;
        }

        _2dAnimFrames = frames.OrderBy(f => f.time).ToList();
        _2dAnimDuration = _2dAnimFrames.Last().time;
        if (_2dAnimDuration <= 0)
        {
            _2dAnimDuration = 1.0f;
        }

        _2dAnimBitmaps.Clear();
        foreach (var f in _2dAnimFrames)
        {
            if (!_2dAnimBitmaps.ContainsKey(f.asset))
            {
                try
                {
                    Image<Bgra32>? img = null;
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
                            img.SaveAsPng(ms);
                            ms.Position = 0;
                            var bmp = new global::Avalonia.Media.Imaging.Bitmap(ms);
                            _2dAnimBitmaps[f.asset] = bmp;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Log(LoggerEvent.Warning, $"Failed to decode 2D animation frame: {ex.Message}");
                }
            }
        }

        if (_2dAnimBitmaps.Count == 0)
        {
            StatusStripUpdate("AnimationClip: Failed to decode any sprite/texture frames for preview.");
            return;
        }

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
        ShowAnimationPlayback(_2dAnimFrames.Count);
        Render2DAnimationFrame(0);

        _2dAnimTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _2dAnimTimer.Tick += On2DAnimTimerTick;
        _2dAnimTimer.Start();

        StatusStripUpdate($"AnimationClip: Playing 2D animation '{clip.m_Name}' ({_2dAnimFrames.Count} frames)...");
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
