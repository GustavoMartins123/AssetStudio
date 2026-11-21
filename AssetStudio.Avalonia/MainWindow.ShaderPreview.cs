using System;
using System.ComponentModel;
using System.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private string _shaderPreviewInfo = string.Empty;

    private void PreviewShader(AssetItem item, Shader shader)
    {
        PreviewSession.ActivateSurface(PreviewSurfaceKind.Shader, item, isLoading: true, loadingStatusText: "Loading shader preview...");
        OpenMaterialShaderPreview($"Shader: {item.Name}", token => PrepareShaderScene(shader, null, token));
    }

    private void OpenMaterialShaderPreview(string info, Func<CancellationToken, ShaderPreviewScene> prepare)
    {
        _shaderPreviewInfo = info;
        TextPreviewBox.IsVisible = false;
        ImagePreviewBox.IsVisible = false;
        PreviewLabel.IsVisible = false;
        HidePreviewGeometryControls();
        PreviewSession.ShaderPreview.Open(prepare);
        GLPreviewControl.IsVisible = true;
        GLPreviewControl.Focus();
    }

    private void OnMaterialShaderPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        var preview = PreviewSession.ShaderPreview;
        if (e.PropertyName == nameof(ShaderPreviewViewModel.IsActive) && !preview.IsActive)
        {
            HideAnimationPlayback();
        }
        if (e.PropertyName == nameof(ShaderPreviewViewModel.Status) && preview.Status is { } shaderName)
        {
            PreviewSession.IsLoading = false;
            SetPreviewInfoOverlay(_shaderPreviewInfo + Environment.NewLine + Environment.NewLine + preview.PassDetails);
            StatusStripUpdate($"Shader preview loaded: {shaderName}");
            if (preview.IsAnimated)
            {
                const int frames = 120;
                GLPreviewControl.StartMaterialShaderAnimation(frames, 30);
                ShowAnimationPlayback(frames);
                if (PreviewSession.IsDumpTabActive)
                {
                    GLPreviewControl.SuspendForHiddenTab();
                }
            }
        }
        else if (e.PropertyName == nameof(ShaderPreviewViewModel.Error) && preview.Error is { } error)
        {
            PreviewSession.IsLoading = false;
            GLPreviewControl.StopAnimation();
            GLPreviewControl.IsVisible = false;
            HideAnimationPlayback();
            string report = _shaderPreviewInfo + Environment.NewLine + Environment.NewLine + error;
            PreviewSession.ShowTextPreview(report, PreviewSession.ActiveKind, PreviewSession.ActiveAsset);
            TextPreviewBox.Text = PreviewSession.TextContent;
            TextPreviewBox.IsVisible = true;
            StatusStripUpdate(error);
        }
    }

    private ShaderPreviewScene PrepareShaderScene(Shader shader, Material? material, CancellationToken token)
    {
        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        return ShaderPreviewService.Prepare(shader, material,
            (owner, reference) => ResolveShaderPreviewReference(reference, owner.assetsFile, token),
            (owner, reference) => ResolveShaderPreviewReference(reference, owner.assetsFile, token), token);
    }

    private T ResolveShaderPreviewReference<T>(PPtr<T> reference, SerializedFile source, CancellationToken token)
        where T : AssetStudio.Object =>
        ShaderPreviewService.ResolveReference(reference, source, EnsureIndexedExternalSourcesLoaded,
            ResolveSemanticRelationHandleForPreview, token);
}
