using System;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private bool _isUpdatingAnimationProgress;
    private bool _isAnimationDragging;

    private void HideAnimationPlayback()
    {
        _isAnimationDragging = false;
        MeshPreview.SetAnimationState(isLoaded: false);
        SyncAnimationProgressBar(0, 0);
    }

    private void ShowAnimationPlayback(int totalFrames)
    {
        var effectiveSampleRate = !_is2dAnimationPreviewActive
            && GLPreviewControl != null
            && GLPreviewControl.AnimFps > 0f
            ? GLPreviewControl.AnimFps
            : MeshPreviewViewModel.DefaultSampleRate;

        if (!MeshPreview.IsAnimationLoaded || MeshPreview.TotalFrames != totalFrames)
        {
            MeshPreview.SetAnimationState(
                isLoaded: true,
                totalFrames: Math.Max(0, totalFrames),
                sampleRate: effectiveSampleRate,
                isPlaying: true);
        }
        else
        {
            MeshPreview.SeekFrame(0, totalFrames);
            MeshPreview.IsPlaying = true;
        }

        SyncAnimationProgressBar(0, totalFrames);
    }

    private void SetAnimationPlaybackPaused(bool paused)
    {
        MeshPreview.IsPlaying = !paused;
    }

    private void UpdateAnimationPlaybackFrame(int currentFrame, int totalFrames, double? exactTimeSeconds = null)
    {
        if (_isAnimationDragging)
        {
            return;
        }

        if (exactTimeSeconds.HasValue)
        {
            MeshPreview.SyncFromTimeline(currentFrame, totalFrames, exactTimeSeconds.Value);
        }
        else
        {
            MeshPreview.SeekFrame(currentFrame, totalFrames);
        }

        SyncAnimationProgressBar(currentFrame, totalFrames);

        if (GLPreviewControl?.IsMaterialShaderPreview == true && GLPreviewControl.AnimTotalFrames == totalFrames)
        {
            PreviewSession.ShaderPreview.UpdateTime(currentFrame, GLPreviewControl.AnimFps);
        }
    }

    private void SyncAnimationProgressBar(int currentFrame, int totalFrames)
    {
        if (AnimationProgressBar == null || _isAnimationDragging)
        {
            return;
        }

        var maxFrame = Math.Max(0, totalFrames - 1);
        _isUpdatingAnimationProgress = true;
        try
        {
            AnimationProgressBar.Maximum = Math.Max(1, maxFrame);
            AnimationProgressBar.IsEnabled = maxFrame > 0;
            AnimationProgressBar.Value = maxFrame > 0 ? Math.Clamp(currentFrame, 0, maxFrame) : 0;
        }
        finally
        {
            _isUpdatingAnimationProgress = false;
        }
    }

    private void AnimationProgressBar_DragStarted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isAnimationDragging = true;
    }

    private void AnimationProgressBar_DragCompleted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isAnimationDragging = false;
        if (AnimationProgressBar == null || !MeshPreview.IsAnimationLoaded)
        {
            return;
        }

        var targetFrame = Math.Max(0, (int)Math.Round(AnimationProgressBar.Value));
        SeekActiveAnimationFrame(targetFrame);
    }

    private void AnimationProgressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingAnimationProgress || !MeshPreview.IsAnimationLoaded || AnimationProgressBar == null)
        {
            return;
        }

        var targetFrame = Math.Max(0, (int)Math.Round(AnimationProgressBar.Value));
        if (_isAnimationDragging)
        {
            MeshPreview.SeekFrame(targetFrame, MeshPreview.TotalFrames);
            return;
        }

        SeekActiveAnimationFrame(targetFrame);
    }

    private void SeekActiveAnimationFrame(int frame)
    {
        if (_is2dAnimationPreviewActive)
        {
            Seek2DAnimationFrame(frame);
            SyncAnimationProgressBar(frame, _2dAnimFrames.Count);
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible && GLPreviewControl.AnimTotalFrames > 0)
        {
            GLPreviewControl.SeekAnimationFrame(frame);
            SyncAnimationProgressBar(frame, GLPreviewControl.AnimTotalFrames);
        }
    }

    private void ExecuteToggleAnimationPlayPause()
    {
        if (_is2dAnimationPreviewActive)
        {
            if (_2dAnimPaused)
            {
                Play2DAnimation();
            }
            else
            {
                Pause2DAnimation();
            }
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            if (GLPreviewControl.IsPlaying)
            {
                GLPreviewControl.PauseAnimation();
                SetAnimationPlaybackPaused(true);
            }
            else
            {
                GLPreviewControl.PlayAnimation();
                SetAnimationPlaybackPaused(false);
            }
        }
    }

    private void ExecuteStopAnimation()
    {
        _isAnimationDragging = false;
        if (_is2dAnimationPreviewActive)
        {
            Pause2DAnimation();
            Seek2DAnimationFrame(0);
            SyncAnimationProgressBar(0, _2dAnimFrames.Count);
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            GLPreviewControl.PauseAnimation();
            if (GLPreviewControl.AnimTotalFrames > 0)
            {
                GLPreviewControl.SeekAnimationFrame(0);
            }
            SetAnimationPlaybackPaused(true);
            SyncAnimationProgressBar(0, GLPreviewControl.AnimTotalFrames);
        }
    }

    private void ExecuteRestartAnimation()
    {
        _isAnimationDragging = false;
        if (_is2dAnimationPreviewActive)
        {
            Restart2DAnimation();
            SyncAnimationProgressBar(0, _2dAnimFrames.Count);
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            GLPreviewControl.RestartAnimation();
            SetAnimationPlaybackPaused(false);
            SyncAnimationProgressBar(0, GLPreviewControl.AnimTotalFrames);
        }
    }
}
