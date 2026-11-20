using System;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal const string PreviewCacheBadgeBorderControlName = "PreviewCacheBadgeBorder";
    internal const string PreviewCacheBadgeTextControlName = "PreviewCacheBadgeText";

    internal string CurrentPreviewCacheBadgeText => PreviewSession.CacheBadgeText;
    internal bool IsPreviewCacheBadgeVisible => PreviewSession.IsCacheBadgeVisible;
    internal bool LastPreviewWasFromCache => PreviewSession.LastPreviewWasFromCache;
    internal string? LastPreviewSourceKind => PreviewSession.LastPreviewSourceKind;
    internal bool LastPreviewMaterialsLoading => PreviewSession.LastPreviewMaterialsLoading;

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
        PreviewSession.UpdateCacheBadge(fromCache, sourceKind, materialsLoading);
    }

    internal void ClearPreviewCacheBadge()
    {
        PreviewSession.ClearCacheBadge();
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
