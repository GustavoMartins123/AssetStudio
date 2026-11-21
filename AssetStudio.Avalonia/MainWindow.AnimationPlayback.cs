using System;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void HideAnimationPlayback()
    {
        MeshPreview.SetAnimationState(isLoaded: false);
    }

    private void ShowAnimationPlayback(int totalFrames)
    {
        if (!MeshPreview.IsAnimationLoaded || MeshPreview.TotalFrames != totalFrames)
        {
            MeshPreview.SetAnimationState(isLoaded: true, totalFrames: Math.Max(0, totalFrames), isPlaying: true);
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

    private void UpdateAnimationPlaybackFrame(int currentFrame, int totalFrames)
    {
        MeshPreview.SeekFrame(currentFrame, totalFrames);
        if (GLPreviewControl?.IsMaterialShaderPreview == true && GLPreviewControl.AnimTotalFrames == totalFrames)
        {
            PreviewSession.ShaderPreview.UpdateTime(currentFrame, GLPreviewControl.AnimFps);
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
