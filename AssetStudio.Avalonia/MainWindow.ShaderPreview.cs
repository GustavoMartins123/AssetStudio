using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private string _shaderPreviewInfo = string.Empty;
    private string _shaderPreviewShaderName = string.Empty;
    private int[]? _shaderPreviewUnityVersion;
    private bool _shaderPreviewIsMaterial;
    private Action<string>? _shaderPreviewFallbackHandler;

    private void PreviewShader(AssetItem item, Shader shader)
    {
        var shaderName = !string.IsNullOrWhiteSpace(shader.m_ParsedForm?.m_Name)
            ? shader.m_ParsedForm!.m_Name
            : (!string.IsNullOrWhiteSpace(shader.m_Name) ? shader.m_Name : item.Name);

        PreviewSession.ActivateSurface(PreviewSurfaceKind.Shader, item, isLoading: true, loadingStatusText: "Loading shader preview...");

        if (!ShaderPreviewService.UsesShaderPreview(shader.version))
        {
            ShowShaderLabTextFallback(item, shader, shaderName, d3d11FailureReason: null);
            return;
        }

        OpenMaterialShaderPreview(
            $"Shader: {shaderName}",
            token => PrepareShaderScene(shader, null, token),
            shaderName: shaderName,
            unityVersion: shader.version,
            isMaterial: false,
            fallbackOnD3D11Error: error => ShowShaderLabTextFallback(item, shader, shaderName, error));
    }

    private void ShowShaderLabTextFallback(AssetItem item, Shader shader, string shaderName, string? d3d11FailureReason)
    {
        var previewId = ++texturePreviewIdCounter;
        PreviewSession.SyncPreviewVersion(previewId);

        var diagnostic = ShaderConversionClassifier.ClassifyPreview(
            shaderName,
            shader.version,
            OperatingSystem.IsWindows(),
            d3d11Rendered: false,
            hasPreviewTexture: false,
            isMaterialPreview: false,
            d3d11FailureReason: d3d11FailureReason);

        PreviewSession.UpdateShaderPreviewDiagnostic(diagnostic);
        GLPreviewControl?.StopAnimation();
        if (GLPreviewControl != null)
        {
            GLPreviewControl.IsVisible = false;
        }
        ImagePreviewBox.IsVisible = false;
        PreviewLabel.IsVisible = false;
        HideAnimationPlayback();
        HidePreviewGeometryControls();
        ClearPreviewInfoOverlay();

        Task.Run(() =>
        {
            string shaderLab;
            try
            {
                shaderLab = shader.Convert() ?? string.Empty;
            }
            catch (Exception ex)
            {
                shaderLab = $"[ShaderLab conversion error: {ex.Message}]";
            }

            var report = diagnostic.BuildOverlayHeader()
                + Environment.NewLine
                + "--------------------------------------------------"
                + Environment.NewLine
                + Environment.NewLine
                + shaderLab;

            Dispatcher.UIThread.Post(() =>
            {
                if (previewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, item))
                {
                    return;
                }

                PreviewSession.IsLoading = false;
                PreviewSession.ShowTextPreview(report, PreviewSurfaceKind.Shader, item);
                TextPreviewBox.Text = PreviewSession.TextContent;
                TextPreviewBox.IsVisible = true;
                StatusStripUpdate($"Shader loaded ({diagnostic.BadgeLabel}): {shaderName}");
            });
        });
    }

    private void OpenMaterialShaderPreview(
        string info,
        Func<CancellationToken, ShaderPreviewScene> prepare,
        string? shaderName = null,
        int[]? unityVersion = null,
        bool isMaterial = false,
        Action<string>? fallbackOnD3D11Error = null)
    {
        _shaderPreviewInfo = info;
        _shaderPreviewShaderName = shaderName ?? string.Empty;
        _shaderPreviewUnityVersion = unityVersion;
        _shaderPreviewIsMaterial = isMaterial;
        _shaderPreviewFallbackHandler = fallbackOnD3D11Error;
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
            var diagnostic = ShaderConversionClassifier.ClassifyPreview(
                string.IsNullOrWhiteSpace(shaderName) ? _shaderPreviewShaderName : shaderName,
                _shaderPreviewUnityVersion,
                OperatingSystem.IsWindows(),
                d3d11Rendered: true,
                hasPreviewTexture: false,
                isMaterialPreview: _shaderPreviewIsMaterial);
            PreviewSession.UpdateShaderPreviewDiagnostic(diagnostic);
            SetPreviewInfoOverlay(
                diagnostic.BuildOverlayHeader()
                + Environment.NewLine + Environment.NewLine
                + _shaderPreviewInfo
                + Environment.NewLine + Environment.NewLine
                + preview.PassDetails);
            StatusStripUpdate($"Shader preview loaded ({diagnostic.BadgeLabel}): {shaderName}");
            if (preview.IsAnimated)
            {
                const int frames = 120;
                GLPreviewControl.StartMaterialShaderAnimation(frames, ShaderPreviewService.FramesPerSecond);
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

            var fallbackHandler = _shaderPreviewFallbackHandler;
            _shaderPreviewFallbackHandler = null;
            if (fallbackHandler != null)
            {
                fallbackHandler(error);
                return;
            }

            var diagnostic = ShaderConversionClassifier.ClassifyPreview(
                _shaderPreviewShaderName,
                _shaderPreviewUnityVersion,
                OperatingSystem.IsWindows(),
                d3d11Rendered: false,
                hasPreviewTexture: false,
                isMaterialPreview: _shaderPreviewIsMaterial,
                d3d11FailureReason: error);
            PreviewSession.UpdateShaderPreviewDiagnostic(diagnostic);
            string report = diagnostic.BuildOverlayHeader()
                + Environment.NewLine + Environment.NewLine
                + _shaderPreviewInfo
                + Environment.NewLine + Environment.NewLine
                + error;
            PreviewSession.ShowTextPreview(report, PreviewSession.ActiveKind, PreviewSession.ActiveAsset);
            TextPreviewBox.Text = PreviewSession.TextContent;
            TextPreviewBox.IsVisible = true;
            StatusStripUpdate($"{diagnostic.BadgeLabel}: {error}");
        }
    }

    private ShaderPreviewScene PrepareShaderScene(Shader shader, Material? material, CancellationToken token)
    {
        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        return ShaderPreviewService.Prepare(shader, material, EnsureIndexedExternalSourcesLoaded,
            ResolveSemanticRelationHandleForPreview, token);
    }

    private T ResolveShaderPreviewReference<T>(PPtr<T> reference, SerializedFile source, CancellationToken token)
        where T : AssetStudio.Object =>
        ShaderPreviewService.ResolveReference(reference, source, EnsureIndexedExternalSourcesLoaded,
            ResolveSemanticRelationHandleForPreview, token);
}
