using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private const double MinAvatarPreviewBoneScale = 0.1;
    private const double MaxAvatarPreviewBoneScale = 5.0;
    private const double MinAvatarPreviewMeshDensityPercent = 1.0;
    private const double MaxAvatarPreviewMeshDensityPercent = 100.0;
    private bool updatingAvatarPreviewControls;
    private DispatcherTimer? avatarPreviewSettingsSaveTimer;
    private readonly ObservableCollection<MeshMaterialPreviewSlot> meshMaterialPreviewSlots = new();
    private bool updatingMeshMaterialControls;

    private sealed class MeshMaterialPreviewSlot
    {
        public int SlotIndex { get; init; }
        public string MaterialName { get; init; } = string.Empty;
        public global::OpenTK.Mathematics.Vector4 BaseColor { get; init; } = global::OpenTK.Mathematics.Vector4.One;
        public global::OpenTK.Mathematics.Vector4 CurrentColor { get; set; } = global::OpenTK.Mathematics.Vector4.One;

        public override string ToString()
        {
            return $"{SlotIndex + 1}: {MaterialName}";
        }
    }

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

    private void ToggleMeshControlsBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (MeshViewerControlsContent != null && ToggleMeshControlsBtn != null)
        {
            bool isVisible = MeshViewerControlsContent.IsVisible;
            MeshViewerControlsContent.IsVisible = !isVisible;
            ToggleMeshControlsBtn.Content = !isVisible ? "▲" : "▼";
        }
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
            GLPreviewControl.PreviewMeshDensityPercent = (float)densityPercent;
        }
        if (AvatarMeshDensityLabel != null)
        {
            AvatarMeshDensityLabel.Text = densityPercent >= MaxAvatarPreviewMeshDensityPercent
                ? "Original"
                : $"{densityPercent:0}%";
        }
    }

    private void ShowPreviewGeometryControls(bool showBoneControls)
    {
        if (BoneSizeContainer != null)
        {
            BoneSizeContainer.IsVisible = true;
        }
        if (BoneSizeControls != null)
        {
            BoneSizeControls.IsVisible = showBoneControls;
        }
    }

    private void HidePreviewGeometryControls()
    {
        if (BoneSizeContainer != null)
        {
            BoneSizeContainer.IsVisible = false;
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
        SaveAppSettings();
    }

    private void FlushAvatarPreviewSettingsSave()
    {
        avatarPreviewSettingsSaveTimer?.Stop();
        SaveAppSettings();
    }

    private void ClearMeshMaterialControls()
    {
        updatingMeshMaterialControls = true;
        try
        {
            if (MeshMaterialSelector != null)
            {
                MeshMaterialSelector.SelectedItem = null;
                MeshMaterialSelector.SelectedIndex = -1;
                if (MeshMaterialSelector.ItemsSource != meshMaterialPreviewSlots)
                {
                    MeshMaterialSelector.ItemsSource = meshMaterialPreviewSlots;
                }
            }
            meshMaterialPreviewSlots.Clear();
            if (MeshMaterialControlsContainer != null)
            {
                MeshMaterialControlsContainer.IsVisible = false;
            }
        }
        finally
        {
            updatingMeshMaterialControls = false;
        }

        GLPreviewControl?.SetMaterialOverrides(null);
    }

    private void BuildMeshMaterialControls(AssetStudio.Mesh mesh, IReadOnlyList<AssetStudio.Material?> materials)
    {
        updatingMeshMaterialControls = true;
        try
        {
            if (MeshMaterialSelector != null)
            {
                MeshMaterialSelector.SelectedItem = null;
                MeshMaterialSelector.SelectedIndex = -1;
                if (MeshMaterialSelector.ItemsSource != meshMaterialPreviewSlots)
                {
                    MeshMaterialSelector.ItemsSource = meshMaterialPreviewSlots;
                }
            }

            meshMaterialPreviewSlots.Clear();
            int slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, materials.Count);

            for (int i = 0; i < slotCount; i++)
            {
                var material = i < materials.Count ? materials[i] : null;
                var baseColor = GetMeshMaterialBaseColor(material);
                meshMaterialPreviewSlots.Add(new MeshMaterialPreviewSlot
                {
                    SlotIndex = i,
                    MaterialName = string.IsNullOrEmpty(material?.m_Name) ? "No Material" : material!.m_Name,
                    BaseColor = baseColor,
                    CurrentColor = baseColor
                });
            }

            if (MeshMaterialSelector != null)
            {
                MeshMaterialSelector.SelectedIndex = meshMaterialPreviewSlots.Count > 0 ? 0 : -1;
            }

            if (MeshMaterialControlsContainer != null)
            {
                MeshMaterialControlsContainer.IsVisible = meshMaterialPreviewSlots.Count > 0;
            }
        }
        finally
        {
            updatingMeshMaterialControls = false;
        }

        UpdateMeshMaterialControlValues();
        ApplyMeshMaterialOverrides();
    }

    private global::OpenTK.Mathematics.Vector4 GetMeshMaterialBaseColor(AssetStudio.Material? material)
    {
        var displayMaterial = material == null ? null : ResolveMaterialForPreview(material) ?? material;
        var colors = displayMaterial?.m_SavedProperties?.m_Colors;
        if (colors == null || colors.Length == 0)
        {
            return global::OpenTK.Mathematics.Vector4.One;
        }

        string[] preferredNames = { "_BaseColor", "_Color", "_TintColor", "_MainColor", "Color" };
        foreach (var preferred in preferredNames)
        {
            var match = colors.FirstOrDefault(x => string.Equals(x.Key, preferred, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
            {
                return ToPreviewTint(match.Value);
            }
        }

        var fallback = colors.FirstOrDefault(x =>
            !string.IsNullOrEmpty(x.Key)
            && x.Key.Contains("color", StringComparison.OrdinalIgnoreCase)
            && !x.Key.Contains("emission", StringComparison.OrdinalIgnoreCase)
            && !x.Key.Contains("spec", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrEmpty(fallback.Key)
            ? global::OpenTK.Mathematics.Vector4.One
            : ToPreviewTint(fallback.Value);
    }

    private static global::OpenTK.Mathematics.Vector4 ToPreviewTint(AssetStudio.Color color)
    {
        return new global::OpenTK.Mathematics.Vector4(
            ClampPreviewColor(color.R),
            ClampPreviewColor(color.G),
            ClampPreviewColor(color.B),
            ClampPreviewColor(color.A));
    }

    private static float ClampPreviewColor(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
    }

    private MeshMaterialPreviewSlot? SelectedMeshMaterialSlot =>
        MeshMaterialSelector?.SelectedItem as MeshMaterialPreviewSlot;

    private void MeshMaterialSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingMeshMaterialControls)
        {
            return;
        }

        UpdateMeshMaterialControlValues();
    }

    private void MeshMaterialSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (updatingMeshMaterialControls)
        {
            return;
        }

        var slot = SelectedMeshMaterialSlot;
        if (slot == null)
        {
            return;
        }

        slot.CurrentColor = new global::OpenTK.Mathematics.Vector4(
            (float)((MeshMaterialRedSlider?.Value ?? 100) / 100.0),
            (float)((MeshMaterialGreenSlider?.Value ?? 100) / 100.0),
            (float)((MeshMaterialBlueSlider?.Value ?? 100) / 100.0),
            (float)((MeshMaterialAlphaSlider?.Value ?? 100) / 100.0));

        UpdateMeshMaterialLabels(slot.CurrentColor);
        ApplyMeshMaterialOverrides();
    }

    private void MeshMaterialResetColorButton_Click(object? sender, RoutedEventArgs e)
    {
        var slot = SelectedMeshMaterialSlot;
        if (slot == null)
        {
            return;
        }

        slot.CurrentColor = new global::OpenTK.Mathematics.Vector4(
            slot.BaseColor.X,
            slot.BaseColor.Y,
            slot.BaseColor.Z,
            slot.CurrentColor.W);

        UpdateMeshMaterialControlValues();
        ApplyMeshMaterialOverrides();
    }

    private void UpdateMeshMaterialControlValues()
    {
        var slot = SelectedMeshMaterialSlot;
        if (slot == null)
        {
            UpdateMeshMaterialLabels(global::OpenTK.Mathematics.Vector4.One);
            return;
        }

        updatingMeshMaterialControls = true;
        try
        {
            if (MeshMaterialAlphaSlider != null) MeshMaterialAlphaSlider.Value = slot.CurrentColor.W * 100.0;
            if (MeshMaterialRedSlider != null) MeshMaterialRedSlider.Value = slot.CurrentColor.X * 100.0;
            if (MeshMaterialGreenSlider != null) MeshMaterialGreenSlider.Value = slot.CurrentColor.Y * 100.0;
            if (MeshMaterialBlueSlider != null) MeshMaterialBlueSlider.Value = slot.CurrentColor.Z * 100.0;
        }
        finally
        {
            updatingMeshMaterialControls = false;
        }

        UpdateMeshMaterialLabels(slot.CurrentColor);
    }

    private void UpdateMeshMaterialLabels(global::OpenTK.Mathematics.Vector4 color)
    {
        if (MeshMaterialAlphaLabel != null) MeshMaterialAlphaLabel.Text = $"{color.W * 100f:0}%";
        if (MeshMaterialRedLabel != null) MeshMaterialRedLabel.Text = $"{color.X * 100f:0}%";
        if (MeshMaterialGreenLabel != null) MeshMaterialGreenLabel.Text = $"{color.Y * 100f:0}%";
        if (MeshMaterialBlueLabel != null) MeshMaterialBlueLabel.Text = $"{color.Z * 100f:0}%";
        if (MeshMaterialColorSwatch != null)
        {
            MeshMaterialColorSwatch.Background = new SolidColorBrush(global::Avalonia.Media.Color.FromArgb(
                255,
                (byte)Math.Clamp((int)Math.Round(color.X * 255), 0, 255),
                (byte)Math.Clamp((int)Math.Round(color.Y * 255), 0, 255),
                (byte)Math.Clamp((int)Math.Round(color.Z * 255), 0, 255)));
        }
    }

    private void ApplyMeshMaterialOverrides()
    {
        if (meshMaterialPreviewSlots.Count == 0)
        {
            GLPreviewControl?.SetMaterialOverrides(null);
            return;
        }

        GLPreviewControl?.SetMaterialOverrides(meshMaterialPreviewSlots
            .OrderBy(x => x.SlotIndex)
            .Select(x => x.CurrentColor)
            .ToArray());
    }
}
