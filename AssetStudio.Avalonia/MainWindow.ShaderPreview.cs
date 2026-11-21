using System.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewShader(AssetItem item, Shader shader)
    {
        PreviewSession.ActivateSurface(PreviewSurfaceKind.Shader, item);
        PreviewLabel.IsVisible = false;
        PreviewSession.ShaderPreview.OpenShader(shader, PrepareShaderScene);
    }

    private void OfferMaterialShaderPreview(AssetItem item, Material material)
    {
        PreviewSession.ShaderPreview.SetMaterial(material,
            token => ResolveShaderPreviewReference(material.m_Shader, material.assetsFile, token),
            PrepareShaderScene,
            () =>
            {
                CancelPendingPreview();
                PreviewSession.ActivateSurface(PreviewSurfaceKind.Shader, item);
            });
    }

    private ShaderPreviewScene PrepareShaderScene(Shader shader, Material? material, CancellationToken token)
    {
        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        return ShaderPreviewService.Prepare(shader, material,
            (owner, reference) => ResolveShaderPreviewReference(reference, owner.assetsFile, token) as Texture2D, token);
    }

    private T ResolveShaderPreviewReference<T>(PPtr<T> reference, SerializedFile source, CancellationToken token)
        where T : AssetStudio.Object =>
        ShaderPreviewService.ResolveReference(reference, source, EnsureIndexedExternalSourcesLoaded,
            ResolveSemanticRelationHandleForPreview, token);
}
