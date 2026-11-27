using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewMaterial(AssetItem assetItem, Material m_Material)
    {
        HideAnimationPlayback();
        if (GLPreviewControl != null)
        {
            GLPreviewControl.StopAnimation();
            GLPreviewControl.IsVisible = false;
        }

        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }

        currentPreviewTexture = null;
        var currentId = ++texturePreviewIdCounter;
        PreviewSession.SyncPreviewVersion(currentId);
        var initialText = $"Material: {m_Material.m_Name}\n\nLoading material preview...";
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Material,
            assetItem,
            isLoading: true,
            loadingStatusText: "Loading material preview...");
        PreviewSession.TextContent = initialText;
        TextPreviewBox.Text = initialText;
        TextPreviewBox.IsVisible = true;
        ImagePreviewBox.IsVisible = false;
        PreviewLabel.IsVisible = false;
        ClearPreviewInfoOverlay();
        StatusStripUpdate("Loading material preview...");

        Task.Run(() =>
        {
            try
            {
                // Cache-first lookup via v15 MaterialCache / MaterialTextures (WAL read works even during connections build).
                MaterialCacheEntryInfo? cachedEntry = null;
                var folderPath = GetCurrentCacheFolderPath();
                var materialAssetId = m_Material.assetsFile != null
                    ? AssetHandle.BuildUniqueID(m_Material.assetsFile, m_Material.m_PathID)
                    : string.Empty;
                if (assetsManager.LazyLoading
                    && currentScanResult != null
                    && !string.IsNullOrWhiteSpace(materialAssetId)
                    && !string.IsNullOrWhiteSpace(folderPath))
                {
                    var signature = _sqliteCache.GetFolderSignature(currentScanResult);
                    cachedEntry = _sqliteCache.LoadMaterialCacheEntry(folderPath, signature, materialAssetId);
                }

                // Only materialize local material file references on cache miss; never scan all 50 preloaded files.
                if (cachedEntry == null || string.IsNullOrWhiteSpace(cachedEntry.MainTextureAssetId))
                {
                    try
                    {
                        EnsureMaterialPreviewDependenciesLoaded(m_Material);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to prepare material preview dependencies for {m_Material.m_Name}: {ex.Message}");
                    }
                }

                var previewData = BuildMaterialPreviewData(m_Material);
                if (currentId != texturePreviewIdCounter)
                {
                    return;
                }

                // Lazy backfill into v15 MaterialCache + MaterialTextures on cache miss so future material & mesh previews reuse it in O(1).
                if (assetsManager.LazyLoading
                    && currentScanResult != null
                    && !string.IsNullOrWhiteSpace(materialAssetId)
                    && (cachedEntry == null || (string.IsNullOrWhiteSpace(cachedEntry.MainTextureAssetId) && previewData.PreviewTexture != null)))
                {
                    EnqueueMaterialLazyBackfill(m_Material, previewData.PreviewTexture);
                }

                if (ShaderPreviewService.UsesShaderPreview(m_Material.version))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                        {
                            return;
                        }
                        OpenMaterialShaderPreview(
                            previewData.InfoText,
                            token => PrepareShaderScene(
                                ResolveShaderPreviewReference(m_Material.m_Shader, m_Material.assetsFile ??
                                    throw new InvalidOperationException("The material has no serialized source."), token), m_Material, token),
                            shaderName: previewData.ResolvedShaderName,
                            unityVersion: m_Material.version,
                            isMaterial: true,
                            fallbackOnD3D11Error: d3d11Error =>
                            {
                                var fallbackId = ++texturePreviewIdCounter;
                                PreviewSession.SyncPreviewVersion(fallbackId);
                                Task.Run(() => RenderMaterialFallbackPreview(assetItem, m_Material, previewData, cachedEntry, fallbackId, d3d11Error));
                            });
                    });
                    return;
                }

                RenderMaterialFallbackPreview(assetItem, m_Material, previewData, cachedEntry, currentId, d3d11FailureReason: null);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        TextPreviewBox.Text = $"Material: {m_Material.m_Name}\n\n[Error loading material preview: {ex.Message}]";
                        TextPreviewBox.IsVisible = true;
                        ImagePreviewBox.IsVisible = false;
                        ClearPreviewInfoOverlay();
                        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                        ClearPreviewCacheBadge();
                        StatusStripUpdate("Material preview error.");
                    }
                });
            }
        });
    }

    private void RenderMaterialFallbackPreview(
        AssetItem assetItem,
        Material m_Material,
        MaterialPreviewData previewData,
        MaterialCacheEntryInfo? cachedEntry,
        long currentId,
        string? d3d11FailureReason)
    {
        TexturePreviewImageResult? previewImage = null;
        try
        {
            if (previewData.PreviewTexture == null)
            {
                var basicDiag = ShaderConversionClassifier.ClassifyPreview(
                    previewData.ResolvedShaderName,
                    m_Material.version,
                    OperatingSystem.IsWindows(),
                    d3d11Rendered: false,
                    hasPreviewTexture: false,
                    isMaterialPreview: true,
                    d3d11FailureReason: d3d11FailureReason);

                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        return;
                    }

                    PreviewSession.IsLoading = false;
                    PreviewSession.UpdateShaderPreviewDiagnostic(basicDiag);
                    TextPreviewBox.Text = basicDiag.BuildOverlayHeader() + "\n\n" + previewData.InfoText;
                    TextPreviewBox.IsVisible = true;
                    ImagePreviewBox.IsVisible = false;
                    ClearPreviewInfoOverlay();
                    if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                    ClearPreviewCacheBadge();
                    StatusStripUpdate($"Material loaded ({basicDiag.BadgeLabel} — no texture).");
                });
                return;
            }

            SetActivePreviewDependencyFiles(m_Material, previewData.PreviewTexture);

            var cachedTexId = previewData.PreviewTexture.assetsFile != null
                ? AssetHandle.BuildUniqueID(previewData.PreviewTexture.assetsFile, previewData.PreviewTexture.m_PathID)
                : cachedEntry?.MainTextureAssetId ?? string.Empty;
            previewImage = !string.IsNullOrWhiteSpace(cachedTexId)
                ? TryLoadCachedTexturePreviewThumbnailByAssetId(cachedTexId, MaxCachedPreviewTextureDimension)
                : null;
            previewImage ??= LoadTexturePreviewThumbnail(previewData.PreviewTexture, MaxCachedPreviewTextureDimension);
            var loadedPreviewImage = previewImage;
            var image = loadedPreviewImage?.Image;
            if (image == null)
            {
                var basicDiag = ShaderConversionClassifier.ClassifyPreview(
                    previewData.ResolvedShaderName,
                    m_Material.version,
                    OperatingSystem.IsWindows(),
                    d3d11Rendered: false,
                    hasPreviewTexture: false,
                    isMaterialPreview: true,
                    d3d11FailureReason: d3d11FailureReason);

                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        return;
                    }

                    PreviewSession.IsLoading = false;
                    PreviewSession.UpdateShaderPreviewDiagnostic(basicDiag);
                    TextPreviewBox.Text = basicDiag.BuildOverlayHeader() + "\n\n" + previewData.InfoText;
                    TextPreviewBox.IsVisible = true;
                    ImagePreviewBox.IsVisible = false;
                    ClearPreviewInfoOverlay();
                    if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                    ClearPreviewCacheBadge();
                    StatusStripUpdate($"Material loaded ({basicDiag.BadgeLabel} — texture decode unavailable).");
                });
                return;
            }

            var activePreviewImage = loadedPreviewImage!;
            var materialPreviewWasDownscaled = activePreviewImage.Downscaled;
            var materialPreviewFromCache = activePreviewImage.FromCache;
            var materialPreviewWidth = image.Width;
            var materialPreviewHeight = image.Height;

            int validChannel = 0;
            for (int i = 0; i < 4; i++)
            {
                if (textureChannels[i])
                {
                    validChannel++;
                }
            }

            if (validChannel != 4)
            {
                ApplyPreviewChannelMask(image, textureChannels, validChannel);
            }
            else
            {
                MakeAlphaOnlyTextureVisible(image);
            }

            var pbrDiag = ShaderConversionClassifier.ClassifyPreview(
                previewData.ResolvedShaderName,
                m_Material.version,
                OperatingSystem.IsWindows(),
                d3d11Rendered: false,
                hasPreviewTexture: true,
                isMaterialPreview: true,
                d3d11FailureReason: d3d11FailureReason);

            var postedPreviewImage = activePreviewImage;
            var postedPreviewTexture = previewData.PreviewTexture!;
            previewImage = null;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        return;
                    }

                    PreviewSession.IsLoading = false;
                    PreviewSession.UpdateShaderPreviewDiagnostic(pbrDiag);
                    currentPreviewTexture = postedPreviewTexture;
                    if (GLPreviewControl != null)
                    {
                        GLPreviewControl.SetMaterialTexture(postedPreviewImage.Image);
                        GLPreviewControl.IsVisible = true;
                        HidePreviewGeometryControls();
                        GLPreviewControl.Focus();
                    }

                    ImagePreviewBox.IsVisible = false;
                    TextPreviewBox.IsVisible = false;
                    PreviewLabel.IsVisible = false;

                    var previewInfoText = pbrDiag.BuildOverlayHeader() + "\n\n" + previewData.InfoText;
                    if (materialPreviewWasDownscaled)
                    {
                        previewInfoText += $"\nPreview texture downscaled to {materialPreviewWidth}x{materialPreviewHeight}";
                    }
                    if (materialPreviewFromCache)
                    {
                        previewInfoText += "\nPreview texture loaded from cache";
                    }

                    SetPreviewInfoOverlay(previewInfoText);

                    UpdatePreviewCacheBadge(fromCache: materialPreviewFromCache, sourceKind: "material");
                    StatusStripUpdate($"Material preview loaded ({pbrDiag.BadgeLabel}): {postedPreviewTexture.m_Name}");
                }
                catch (Exception ex)
                {
                    logger.Log(LoggerEvent.Error, $"Material preview UI failed for {m_Material.m_Name}: {ex}");
                    if (currentId == texturePreviewIdCounter)
                    {
                        TextPreviewBox.Text = previewData.InfoText + "\n[Error showing preview texture: " + ex.Message + "]";
                        TextPreviewBox.IsVisible = true;
                        ImagePreviewBox.IsVisible = false;
                        ClearPreviewInfoOverlay();
                        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                        ClearPreviewCacheBadge();
                        StatusStripUpdate("Material preview UI error.");
                    }
                }
                finally
                {
                    postedPreviewImage.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (currentId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                {
                    PreviewSession.IsLoading = false;
                    TextPreviewBox.Text = $"Material: {m_Material.m_Name}\n\n[Error loading material preview: {ex.Message}]";
                    TextPreviewBox.IsVisible = true;
                    ImagePreviewBox.IsVisible = false;
                    ClearPreviewInfoOverlay();
                    if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                    ClearPreviewCacheBadge();
                    StatusStripUpdate("Material preview error.");
                }
            });
        }
        finally
        {
            previewImage?.Dispose();
        }
    }

    private MaterialPreviewData BuildMaterialPreviewData(Material material)
    {
        return new MaterialPreviewBuilder(
            assetsManager,
            ResolveMaterialForPreview,
            GetMaterialTextureSlot,
            FindTextureForMaterial).Build(material);
    }

    private void EnqueueMaterialLazyBackfill(Material material, Texture2D? resolvedMainTexture)
    {
        if (material.assetsFile == null || currentScanResult == null)
        {
            return;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var materialId = AssetHandle.BuildUniqueID(material.assetsFile, material.m_PathID);
        var displayMaterial = ResolveMaterialForPreview(material) ?? material;
        var previewMaterialId = displayMaterial.assetsFile != null
            ? AssetHandle.BuildUniqueID(displayMaterial.assetsFile, displayMaterial.m_PathID)
            : materialId;
        var mainSlotName = Services.SemanticGraphService.SelectMainTextureSlotNameForRelations(displayMaterial) ?? string.Empty;
        var mainTexId = resolvedMainTexture?.assetsFile != null
            ? AssetHandle.BuildUniqueID(resolvedMainTexture.assetsFile, resolvedMainTexture.m_PathID)
            : string.Empty;

        var backfill = new SemanticAssetRelations();
        var slotIndex = 0;
        foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<System.Collections.Generic.KeyValuePair<string, UnityTexEnv>>())
        {
            var texRef = texEnv.Value?.m_Texture;
            var texHandle = texRef != null && !texRef.IsNull
                ? FindTextureHandleForPPtr(displayMaterial, texRef)
                : null;
            var texId = texHandle?.UniqueID ?? string.Empty;
            var isMain = string.Equals(texEnv.Key, mainSlotName, StringComparison.OrdinalIgnoreCase);
            if (isMain && string.IsNullOrEmpty(mainTexId) && !string.IsNullOrEmpty(texId))
            {
                mainTexId = texId;
            }

            var scale = texEnv.Value?.m_Scale ?? new Vector2(1f, 1f);
            var offset = texEnv.Value?.m_Offset ?? new Vector2(0f, 0f);
            backfill.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                materialId,
                previewMaterialId,
                texEnv.Key,
                slotIndex++,
                texId,
                texRef?.m_FileID ?? 0,
                texRef?.m_PathID ?? 0,
                !string.IsNullOrEmpty(texId),
                isMain,
                float.IsFinite(scale.X) ? scale.X : 1f,
                float.IsFinite(scale.Y) ? scale.Y : 1f,
                float.IsFinite(offset.X) ? offset.X : 0f,
                float.IsFinite(offset.Y) ? offset.Y : 0f,
                "lazy_backfill"));
        }

        var (r, g, b, a) = Services.SemanticGraphService.ExtractMaterialBaseColor(displayMaterial);
        var shaderName = displayMaterial.m_Shader != null && displayMaterial.m_Shader.TryGet(out var sh) && sh != null
            ? (sh.m_ParsedForm?.m_Name ?? sh.m_Name ?? string.Empty)
            : string.Empty;
        var parentId = !string.Equals(previewMaterialId, materialId, StringComparison.Ordinal)
            ? previewMaterialId
            : GetMaterialParentAssetIdForSemanticCache(material);

        backfill.MaterialCaches.Add(new SemanticMaterialCacheEntry(
            materialId,
            material.m_Name ?? string.Empty,
            parentId,
            mainTexId,
            mainSlotName,
            r,
            g,
            b,
            a,
            shaderName,
            slotIndex,
            "lazy_backfill"));

        EnqueueLazySemanticRelationsBackfill(backfill);
    }
}
