using System;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void HideAnimationPlayback()
    {
        MeshPreview.SetAnimationState(isLoaded: false);
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
    }

    private void SetAnimationPlaybackPaused(bool paused)
    {
        MeshPreview.IsPlaying = !paused;
    }

    private void UpdateAnimationPlaybackFrame(int currentFrame, int totalFrames, double? exactTimeSeconds = null)
    {
        if (exactTimeSeconds.HasValue)
        {
            MeshPreview.SyncFromTimeline(currentFrame, totalFrames, exactTimeSeconds.Value);
        }
        else
        {
            MeshPreview.SeekFrame(currentFrame, totalFrames);
        }

        if (GLPreviewControl?.IsMaterialShaderPreview == true && GLPreviewControl.AnimTotalFrames == totalFrames)
        {
            PreviewSession.ShaderPreview.UpdateTime(currentFrame, GLPreviewControl.AnimFps);
        }
    }

    private void SeekActiveAnimationFrame(int frame)
    {
        if (_is2dAnimationPreviewActive)
        {
            Seek2DAnimationFrame(frame);
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible && GLPreviewControl.AnimTotalFrames > 0)
        {
            GLPreviewControl.SeekAnimationFrame(frame);
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

    private void ExecuteRestartAnimation()
    {
        if (_is2dAnimationPreviewActive)
        {
            Restart2DAnimation();
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            GLPreviewControl.RestartAnimation();
            SetAnimationPlaybackPaused(false);
        }
    }
}
