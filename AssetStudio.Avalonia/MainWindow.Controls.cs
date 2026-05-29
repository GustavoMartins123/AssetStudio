using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private const double MinAvatarPreviewBoneScale = 0.1;
    private const double MaxAvatarPreviewBoneScale = 5.0;
    private const double MinAvatarPreviewMeshDensityPercent = 1.0;
    private const double MaxAvatarPreviewMeshDensityPercent = 100.0;
    private bool updatingAvatarPreviewControls;
    private DispatcherTimer? avatarPreviewSettingsSaveTimer;

    private void ResetViewBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.ResetView();
    }

    private void RotateLeftBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateLeft90();
    }

    private void RotateRightBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateRight90();
    }

    private void RotateUpBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateUp90();
    }

    private void RotateDownBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateDown90();
    }

    private void BoneSizeSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (updatingAvatarPreviewControls || BoneSizeSlider == null)
        {
            return;
        }

        var boneScale = Math.Clamp(BoneSizeSlider.Value, MinAvatarPreviewBoneScale, MaxAvatarPreviewBoneScale);
        appSettings.AvatarPreviewBoneScale = boneScale;
        ApplyAvatarPreviewBoneScale(boneScale);
        QueueAvatarPreviewSettingsSave();
    }

    private void AvatarMeshDensitySlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (updatingAvatarPreviewControls || AvatarMeshDensitySlider == null)
        {
            return;
        }

        var densityPercent = Math.Clamp(AvatarMeshDensitySlider.Value, MinAvatarPreviewMeshDensityPercent, MaxAvatarPreviewMeshDensityPercent);
        appSettings.AvatarPreviewMeshDensityPercent = densityPercent;
        ApplyAvatarPreviewMeshDensity(densityPercent);
        QueueAvatarPreviewSettingsSave();
    }

    private void ApplyAvatarPreviewControlSettings()
    {
        var boneScale = Math.Clamp(appSettings.AvatarPreviewBoneScale, MinAvatarPreviewBoneScale, MaxAvatarPreviewBoneScale);
        var densityPercent = Math.Clamp(appSettings.AvatarPreviewMeshDensityPercent, MinAvatarPreviewMeshDensityPercent, MaxAvatarPreviewMeshDensityPercent);

        appSettings.AvatarPreviewBoneScale = boneScale;
        appSettings.AvatarPreviewMeshDensityPercent = densityPercent;

        updatingAvatarPreviewControls = true;
        try
        {
            if (BoneSizeSlider != null)
            {
                BoneSizeSlider.Value = boneScale;
            }
            if (AvatarMeshDensitySlider != null)
            {
                AvatarMeshDensitySlider.Value = densityPercent;
            }
        }
        finally
        {
            updatingAvatarPreviewControls = false;
        }

        ApplyAvatarPreviewBoneScale(boneScale);
        ApplyAvatarPreviewMeshDensity(densityPercent);
    }

    private void ApplyAvatarPreviewBoneScale(double boneScale)
    {
        if (GLPreviewControl != null)
        {
            GLPreviewControl.BoneScale = (float)boneScale;
        }
        if (BoneSizeLabel != null)
        {
            BoneSizeLabel.Text = $"{boneScale:0.0}x";
        }
    }

    private void ApplyAvatarPreviewMeshDensity(double densityPercent)
    {
        if (GLPreviewControl != null)
        {
            GLPreviewControl.AvatarReferenceMeshDensityPercent = (float)densityPercent;
        }
        if (AvatarMeshDensityLabel != null)
        {
            AvatarMeshDensityLabel.Text = densityPercent >= MaxAvatarPreviewMeshDensityPercent
                ? "Original"
                : $"{densityPercent:0}%";
        }
    }

    private void QueueAvatarPreviewSettingsSave()
    {
        avatarPreviewSettingsSaveTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        avatarPreviewSettingsSaveTimer.Tick -= AvatarPreviewSettingsSaveTimer_Tick;
        avatarPreviewSettingsSaveTimer.Tick += AvatarPreviewSettingsSaveTimer_Tick;
        avatarPreviewSettingsSaveTimer.Stop();
        avatarPreviewSettingsSaveTimer.Start();
    }

    private void AvatarPreviewSettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        avatarPreviewSettingsSaveTimer?.Stop();
        appSettings.Save();
    }

    private void FlushAvatarPreviewSettingsSave()
    {
        avatarPreviewSettingsSaveTimer?.Stop();
        appSettings.Save();
    }
}
