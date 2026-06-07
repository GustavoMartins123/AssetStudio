using System;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void HideAnimationPlayback()
    {
        if (AnimationPlaybackPanel != null)
        {
            AnimationPlaybackPanel.IsVisible = false;
        }

        if (AnimFrameLabel != null)
        {
            AnimFrameLabel.Text = "Frame: 0/0";
        }

        if (AnimPlayPauseBtn != null)
        {
            AnimPlayPauseBtn.Content = "Pause";
        }
    }

    private void ShowAnimationPlayback(int totalFrames)
    {
        if (AnimationPlaybackPanel != null)
        {
            AnimationPlaybackPanel.IsVisible = true;
        }

        SetAnimationPlaybackPaused(false);
        UpdateAnimationPlaybackFrame(0, totalFrames);
    }

    private void SetAnimationPlaybackPaused(bool paused)
    {
        if (AnimPlayPauseBtn != null)
        {
            AnimPlayPauseBtn.Content = paused ? "Play" : "Pause";
        }
    }

    private void UpdateAnimationPlaybackFrame(int currentFrame, int totalFrames)
    {
        if (AnimFrameLabel != null)
        {
            AnimFrameLabel.Text = $"Frame: {currentFrame}/{Math.Max(0, totalFrames)}";
        }
    }

    private void AnimPlayPauseBtn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
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

    private void AnimRestartBtn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
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
