using Avalonia.Controls;
using Avalonia.Threading;
using System;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal const string PreviewCacheBadgeBorderControlName = "PreviewCacheBadgeBorder";
    internal const string PreviewCacheBadgeTextControlName = "PreviewCacheBadgeText";

    internal string CurrentPreviewCacheBadgeText { get; private set; } = string.Empty;
    internal bool IsPreviewCacheBadgeVisible { get; private set; }
    internal bool LastPreviewWasFromCache { get; private set; }
    internal string? LastPreviewSourceKind { get; private set; }
    internal bool LastPreviewMaterialsLoading { get; private set; }

    internal static string FormatPreviewCacheBadge(
        bool fromCache,
        string? sourceKind,
        bool materialsLoading = false)
    {
        var label = NormalizePreviewSourceKind(sourceKind);

        if (fromCache)
        {
            var cachePrefix = label != null ? $"Cached {label}" : "Cached";
            return materialsLoading
                ? $"{cachePrefix} • Loading materials..."
                : cachePrefix;
        }

        if (materialsLoading)
        {
            return label != null
                ? $"{label} • Loading materials..."
                : "Loading materials...";
        }

        return string.Empty;
    }

    internal static bool ShouldShowPreviewCacheBadge(bool fromCache, bool materialsLoading = false)
    {
        return fromCache || materialsLoading;
    }

    internal void UpdatePreviewCacheBadge(
        bool fromCache,
        string? sourceKind = null,
        bool materialsLoading = false)
    {
        var badgeText = FormatPreviewCacheBadge(fromCache, sourceKind, materialsLoading);
        var isVisible = !string.IsNullOrWhiteSpace(badgeText);

        LastPreviewWasFromCache = fromCache;
        LastPreviewSourceKind = sourceKind;
        LastPreviewMaterialsLoading = materialsLoading;
        CurrentPreviewCacheBadgeText = badgeText;
        IsPreviewCacheBadgeVisible = isVisible;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyPreviewCacheBadgeToControls(badgeText, isVisible);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyPreviewCacheBadgeToControls(badgeText, isVisible));
        }
    }

    internal void ClearPreviewCacheBadge()
    {
        UpdatePreviewCacheBadge(fromCache: false, sourceKind: null, materialsLoading: false);
    }

    private void ApplyPreviewCacheBadgeToControls(string badgeText, bool isVisible)
    {
        var textControl = this.FindControl<TextBlock>(PreviewCacheBadgeTextControlName);
        if (textControl != null)
        {
            textControl.Text = badgeText;
        }

        var borderControl = this.FindControl<Border>(PreviewCacheBadgeBorderControlName);
        if (borderControl != null)
        {
            borderControl.IsVisible = isVisible;
        }
    }

    private static string? NormalizePreviewSourceKind(string? sourceKind)
    {
        if (string.IsNullOrWhiteSpace(sourceKind))
        {
            return null;
        }

        var trimmed = sourceKind.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "mesh" or "mesh-geometry" => "Mesh",
            "texture" or "texture2d" or "texture-thumbnail-png" => "Texture",
            "model-group" or "modelgroup" => "Model Group",
            "material" => "Material",
            "sprite" => "Sprite",
            "avatar" => "Avatar",
            "bundle" => "Bundle",
            _ => trimmed
        };
    }
}
