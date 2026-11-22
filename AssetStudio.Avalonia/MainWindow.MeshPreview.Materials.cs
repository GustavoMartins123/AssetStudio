using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private static readonly string[] PreferredMaterialTextureSlots = MaterialPreviewBuilder.PreferredTextureSlots;

    private static readonly HashSet<string> NonDiffuseSlots = MaterialPreviewBuilder.NonDiffuseTextureSlots;

    internal static bool IsTransparentOrDecalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Contains("Decal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Contains("Transparent", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Cutout", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDecalMeshOrObjectName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && name.Contains("Decal", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldPreserveMaterialTextureAlpha(Material? material, string? shaderName = null)
    {
        if (material == null)
        {
            return IsTransparentOrDecalName(shaderName);
        }

        if (IsDecalMeshOrObjectName(material.m_Name) || IsTransparentOrDecalName(shaderName))
        {
            return true;
        }

        if (material.m_Shader != null
            && !material.m_Shader.IsNull
            && material.m_Shader.TryGet(out var loadedShader)
            && loadedShader != null)
        {
            var resolvedShaderName = loadedShader.m_ParsedForm?.m_Name ?? loadedShader.m_Name;
            if (IsTransparentOrDecalName(resolvedShaderName))
            {
                return true;
            }
        }

        // Only true Transparent / Overlay queues (>= 2900), never AlphaTest / Cutout (2450)
        // whose diffuse alpha channel often stores specular/rim/outline masks on character shaders.
        if (material.m_CustomRenderQueue >= 2900)
        {
            return true;
        }

        if (material.stringTagMap != null)
        {
            foreach (var tag in material.stringTagMap)
            {
                if (string.Equals(tag.Key, "RenderType", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(tag.Value, "Transparent", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (string.Equals(tag.Value, "Opaque", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tag.Value, "TransparentCutout", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else if (string.Equals(tag.Key, "Queue", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(tag.Value)
                    && (tag.Value.Contains("Transparent", StringComparison.OrdinalIgnoreCase)
                        || tag.Value.Contains("Overlay", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        var props = material.m_SavedProperties;
        if (props != null)
        {
            bool isTransparentSurface = TryGetMaterialNumericProperty(props, "_Surface", out var surface) && surface >= 0.5f;
            bool hasTransparentKeyword = material.m_ValidKeywords != null
                && material.m_ValidKeywords.Any(k => !string.IsNullOrWhiteSpace(k)
                    && (k.Contains("_SURFACE_TYPE_TRANSPARENT", StringComparison.OrdinalIgnoreCase)
                        || k.Contains("_ALPHABLEND_ON", StringComparison.OrdinalIgnoreCase)));
            bool zWriteOff = TryGetMaterialNumericProperty(props, "_ZWrite", out var zWrite) && Math.Abs(zWrite) < 0.001f;

            if ((isTransparentSurface || hasTransparentKeyword) && zWriteOff)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldPreserveResolvedSlotTextureAlpha(
        ResolvedMeshSubMeshMaterial? slot,
        string? meshName = null,
        string? gameObjectName = null,
        string? shaderName = null,
        Material? loadedMaterial = null)
    {
        if (loadedMaterial != null)
        {
            return IsDecalMeshOrObjectName(meshName)
                || IsDecalMeshOrObjectName(gameObjectName)
                || ShouldPreserveMaterialTextureAlpha(loadedMaterial, shaderName);
        }

        if (IsDecalMeshOrObjectName(meshName) || IsDecalMeshOrObjectName(gameObjectName))
        {
            return true;
        }

        if (slot == null)
        {
            return IsTransparentOrDecalName(shaderName);
        }

        if (IsDecalMeshOrObjectName(slot.MaterialName) || IsTransparentOrDecalName(shaderName))
        {
            return true;
        }

        return false;
    }

    private bool ShouldPreserveResolvedSlotTextureAlphaForPreview(
        ResolvedMeshSubMeshMaterial? slot,
        string? meshName = null,
        string? gameObjectName = null)
    {
        Material? loadedMat = null;
        string? shaderName = null;
        if (!string.IsNullOrWhiteSpace(slot?.MaterialAssetId))
        {
            var handle = assetsManager.ProjectIndex?.GetHandle(slot!.MaterialAssetId);
            loadedMat = handle?.RealObject as Material;
            if (loadedMat == null && currentScanResult != null)
            {
                var folderPath = GetCurrentCacheFolderPath();
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    var signature = _sqliteCache.GetFolderSignature(currentScanResult);
                    shaderName = _sqliteCache.LoadMaterialCacheEntry(folderPath, signature, slot.MaterialAssetId)?.ShaderName;
                }
            }
        }

        return ShouldPreserveResolvedSlotTextureAlpha(slot, meshName, gameObjectName, shaderName, loadedMat);
    }

    private static bool TryGetMaterialNumericProperty(UnityPropertySheet props, string propertyName, out float value)
    {
        if (props.m_Floats != null)
        {
            foreach (var pair in props.m_Floats)
            {
                if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        if (props.m_Ints != null)
        {
            foreach (var pair in props.m_Ints)
            {
                if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = 0f;
        return false;
    }

    internal static void BuildSubMeshTextureDeduplicationPlan(
        int slotCount,
        IReadOnlyList<string?> subMeshTextureKeys,
        out List<string> uniqueKeys,
        out int[] subMeshToUniqueIndex)
    {
        uniqueKeys = new List<string>();
        var safeCount = Math.Max(0, slotCount);
        subMeshToUniqueIndex = new int[safeCount];
        Array.Fill(subMeshToUniqueIndex, -1);

        var keyToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < safeCount; i++)
        {
            var key = i < subMeshTextureKeys.Count ? subMeshTextureKeys[i] : null;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!keyToIndex.TryGetValue(key, out var uniqueIdx))
            {
                uniqueIdx = uniqueKeys.Count;
                uniqueKeys.Add(key);
                keyToIndex[key] = uniqueIdx;
            }

            subMeshToUniqueIndex[i] = uniqueIdx;
        }
    }

    private List<ResolvedMeshSubMeshMaterial> TryLoadResolvedMeshMaterialsForPreview(Mesh mesh, string? rendererAssetId = null)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return new List<ResolvedMeshSubMeshMaterial>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new List<ResolvedMeshSubMeshMaterial>();
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        if (string.IsNullOrWhiteSpace(meshAssetId))
        {
            return new List<ResolvedMeshSubMeshMaterial>();
        }

        // Read directly via WAL without blocking on full connections_completed so lazy_backfill and partial caches work immediately.
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        if (!string.IsNullOrWhiteSpace(rendererAssetId)
            && !string.IsNullOrWhiteSpace(mesh.assetsFile.fileName)
            && !rendererAssetId.StartsWith(mesh.assetsFile.fileName + "#", StringComparison.OrdinalIgnoreCase))
        {
            var crossBundleRendererSlots = TryBuildResolvedSlotsFromRendererHandleForPreview(
                meshAssetId,
                rendererAssetId,
                folderPath,
                signature);
            if (crossBundleRendererSlots.Count > 0)
            {
                return EnrichResolvedMeshMaterialsForPreview(crossBundleRendererSlots, folderPath, signature);
            }
        }

        var slots = _sqliteCache.LoadResolvedMeshMaterials(folderPath, signature, meshAssetId, rendererAssetId);
        if (slots.Count == 0 && !string.IsNullOrWhiteSpace(rendererAssetId))
        {
            slots = TryBuildResolvedSlotsFromRendererHandleForPreview(
                meshAssetId,
                rendererAssetId,
                folderPath,
                signature);
        }

        if (slots.Count == 0)
        {
            return slots;
        }

        return EnrichResolvedMeshMaterialsForPreview(slots, folderPath, signature);
    }

    private List<ResolvedMeshSubMeshMaterial> TryBuildResolvedSlotsFromRendererHandleForPreview(
        string meshAssetId,
        string rendererAssetId,
        string folderPath,
        string signature)
    {
        var result = new List<ResolvedMeshSubMeshMaterial>();
        var rHandle = assetsManager.ProjectIndex?.GetHandle(rendererAssetId);
        var renderer = (rHandle?.RealObject ?? (rHandle != null ? ResolveSemanticRelationHandleForPreview(rHandle) : null)) as Renderer;
        if (renderer?.m_Materials == null || renderer.m_Materials.Length == 0)
        {
            return result;
        }

        var rendererType = renderer is SkinnedMeshRenderer ? "SkinnedMeshRenderer" : "MeshRenderer";
        for (int subIdx = 0; subIdx < renderer.m_Materials.Length; subIdx++)
        {
            var matPtr = renderer.m_Materials[subIdx];
            if (matPtr == null || matPtr.IsNull)
            {
                continue;
            }

            var matHandle = FindMaterialHandleForPPtr(renderer, matPtr);
            var matAssetId = matHandle?.UniqueID;
            if (string.IsNullOrWhiteSpace(matAssetId))
            {
                continue;
            }

            var cachedMat = _sqliteCache.LoadMaterialCacheEntry(folderPath, signature, matAssetId);
            if (cachedMat != null)
            {
                result.Add(new ResolvedMeshSubMeshMaterial(
                    meshAssetId,
                    subIdx,
                    matAssetId,
                    rendererAssetId,
                    rendererType,
                    !string.IsNullOrWhiteSpace(cachedMat.MaterialName) ? cachedMat.MaterialName : (matHandle?.Name ?? string.Empty),
                    cachedMat.MainTextureAssetId ?? string.Empty,
                    cachedMat.MainTextureSlotName ?? string.Empty,
                    cachedMat.BaseColorR,
                    cachedMat.BaseColorG,
                    cachedMat.BaseColorB,
                    cachedMat.BaseColorA,
                    "cross_bundle_renderer_cache"));
            }
            else
            {
                result.Add(new ResolvedMeshSubMeshMaterial(
                    meshAssetId,
                    subIdx,
                    matAssetId,
                    rendererAssetId,
                    rendererType,
                    matHandle?.Name ?? string.Empty,
                    string.Empty,
                    string.Empty,
                    1f,
                    1f,
                    1f,
                    1f,
                    "cross_bundle_renderer_handle"));
            }
        }

        return result;
    }

    private List<ResolvedMeshSubMeshMaterial> EnrichResolvedMeshMaterialsForPreview(
        List<ResolvedMeshSubMeshMaterial> slots,
        string folderPath,
        string signature)
    {
        if (slots.Count == 0)
        {
            return slots;
        }

        var resolvedMaterialCache = new Dictionary<string, Material?>(StringComparer.Ordinal);
        var enrichedFileNames = new List<string?>();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (string.IsNullOrWhiteSpace(slot.MaterialAssetId))
            {
                continue;
            }

            bool missingTexture = string.IsNullOrWhiteSpace(slot.MainTextureAssetId);
            bool hasNonDiffuseCachedSlot = !string.IsNullOrWhiteSpace(slot.MainTextureSlotName)
                && MaterialPreviewBuilder.IsNonDiffuseTextureSlot(slot.MainTextureSlotName);
            if (!missingTexture && !hasNonDiffuseCachedSlot)
            {
                continue;
            }

            if (missingTexture && !hasNonDiffuseCachedSlot)
            {
                var cachedMatEntry = _sqliteCache.LoadMaterialCacheEntry(folderPath, signature, slot.MaterialAssetId);
                if (cachedMatEntry != null
                    && cachedMatEntry.TextureSlotCount == 0
                    && !string.IsNullOrWhiteSpace(slot.MaterialName))
                {
                    continue;
                }
            }

            if (!resolvedMaterialCache.TryGetValue(slot.MaterialAssetId, out var material))
            {
                var matHandle = ResolveMaterialHandleByAssetIdForPreview(slot.MaterialAssetId);
                material = matHandle != null ? ResolveSemanticRelationHandleForPreview(matHandle) as Material : null;
                resolvedMaterialCache[slot.MaterialAssetId] = material;
            }

            if (material == null)
            {
                continue;
            }

            enrichedFileNames.Add(material.assetsFile?.fileName);
            IndexMaterialTextures(material);
            var mainTex = FindTextureForMaterial(material);
            enrichedFileNames.Add(mainTex?.assetsFile?.fileName);
            var mainTexId = mainTex != null ? GetPreviewObjectKey(mainTex) : string.Empty;
            var displayMat = ResolveMaterialForPreview(material) ?? material;
            var chosenSlotName = MaterialPreviewBuilder.SelectMainTextureSlotName(displayMat) ?? slot.MainTextureSlotName;
            var matName = !string.IsNullOrWhiteSpace(material.m_Name) ? material.m_Name : slot.MaterialName;
            var baseColor = GetMeshMaterialBaseColor(material);

            slots[i] = new ResolvedMeshSubMeshMaterial(
                slot.MeshAssetId,
                slot.SubMeshIndex,
                slot.MaterialAssetId,
                slot.RendererAssetId,
                slot.RendererType,
                matName,
                mainTexId,
                chosenSlotName,
                baseColor.X,
                baseColor.Y,
                baseColor.Z,
                baseColor.W,
                "on_demand_preview");
        }

        if (enrichedFileNames.Count > 0)
        {
            AddActivePreviewDependencyFiles(enrichedFileNames);
        }

        return slots.Any(s => !string.IsNullOrWhiteSpace(s.MaterialAssetId))
            ? slots
            : new List<ResolvedMeshSubMeshMaterial>();
    }

    internal static byte[]? ConvertPreviewImageToRgbaBytes(
        TexturePreviewImageResult? previewImage,
        out int width,
        out int height,
        bool preserveAlpha = false)
    {
        return ConvertPreviewBitmapToRgbaBytes(previewImage?.Image, out width, out height, preserveAlpha);
    }

    internal static byte[]? ConvertPreviewBitmapToRgbaBytes(
        SkiaSharp.SKBitmap? image,
        out int width,
        out int height,
        bool preserveAlpha = false)
    {
        width = 0;
        height = 0;
        if (image == null || image.Width <= 0 || image.Height <= 0)
        {
            return null;
        }

        width = image.Width;
        height = image.Height;
        var rgba = new byte[width * height * 4];
        image.CopyBgraPixelsTo(rgba);
        ConvertBgraToPreviewRgbaInPlace(
            rgba,
            preserveAlpha,
            unpremultiplyAlpha: image.AlphaType == SkiaSharp.SKAlphaType.Premul);
        return rgba;
    }

    internal static void ConvertBgraToPreviewRgbaInPlace(
        byte[] rgba,
        bool preserveAlpha,
        bool unpremultiplyAlpha = false)
    {
        if (rgba == null || rgba.Length < 4)
        {
            return;
        }

        byte maxAlpha = 0;
        for (int p = 0; p + 3 < rgba.Length; p += 4)
        {
            byte b = rgba[p];
            byte g = rgba[p + 1];
            byte r = rgba[p + 2];
            byte a = rgba[p + 3];
            if (unpremultiplyAlpha && a > 0 && a < byte.MaxValue)
            {
                int half = a / 2;
                r = (byte)Math.Min(255, (r * 255 + half) / a);
                g = (byte)Math.Min(255, (g * 255 + half) / a);
                b = (byte)Math.Min(255, (b * 255 + half) / a);
            }

            rgba[p] = r;
            rgba[p + 1] = g;
            rgba[p + 2] = b;
            if (preserveAlpha)
            {
                if (a > maxAlpha)
                {
                    maxAlpha = a;
                }
            }
            else
            {
                rgba[p + 3] = byte.MaxValue;
            }
        }

        if (preserveAlpha && maxAlpha == 0)
        {
            for (int p = 3; p < rgba.Length; p += 4)
            {
                rgba[p] = byte.MaxValue;
            }
        }
    }

    internal static void CompositeEyeIrisOverScleraInPlace(
        byte[] irisRgba,
        int irisWidth,
        int irisHeight,
        byte[]? scleraBgra = null,
        int scleraWidth = 0,
        int scleraHeight = 0)
    {
        if (irisRgba == null || irisWidth <= 0 || irisHeight <= 0 || irisRgba.Length < irisWidth * irisHeight * 4)
        {
            return;
        }

        const byte DefaultScleraR = 236;
        const byte DefaultScleraG = 218;
        const byte DefaultScleraB = 214;

        bool hasScleraMap = scleraBgra != null
            && scleraWidth > 0
            && scleraHeight > 0
            && scleraBgra.Length >= scleraWidth * scleraHeight * 4;
        bool sameSize = hasScleraMap && irisWidth == scleraWidth && irisHeight == scleraHeight;

        for (int y = 0; y < irisHeight; y++)
        {
            int dstRow = y * irisWidth * 4;
            int srcRow = 0;
            if (hasScleraMap)
            {
                int sy = sameSize ? y : Math.Min(scleraHeight - 1, (int)((long)y * scleraHeight / irisHeight));
                srcRow = sy * scleraWidth * 4;
            }

            for (int x = 0; x < irisWidth; x++)
            {
                int dp = dstRow + x * 4;
                byte ia = irisRgba[dp + 3];
                if (ia == byte.MaxValue)
                {
                    continue;
                }

                byte wr = DefaultScleraR;
                byte wg = DefaultScleraG;
                byte wb = DefaultScleraB;
                if (hasScleraMap)
                {
                    int sx = sameSize ? x : Math.Min(scleraWidth - 1, (int)((long)x * scleraWidth / irisWidth));
                    int sp = srcRow + sx * 4;
                    wb = scleraBgra![sp];
                    wg = scleraBgra[sp + 1];
                    wr = scleraBgra[sp + 2];
                }

                if (ia == 0)
                {
                    irisRgba[dp] = wr;
                    irisRgba[dp + 1] = wg;
                    irisRgba[dp + 2] = wb;
                }
                else
                {
                    int invA = 255 - ia;
                    irisRgba[dp] = (byte)((irisRgba[dp] * ia + wr * invA + 127) / 255);
                    irisRgba[dp + 1] = (byte)((irisRgba[dp + 1] * ia + wg * invA + 127) / 255);
                    irisRgba[dp + 2] = (byte)((irisRgba[dp + 2] * ia + wb * invA + 127) / 255);
                }

                irisRgba[dp + 3] = byte.MaxValue;
            }
        }
    }

    internal static void CompositeBgraOverlayOntoRgbaInPlace(
        byte[] dstRgba,
        int dstWidth,
        int dstHeight,
        byte[] overlayBgra,
        int ovWidth,
        int ovHeight,
        bool overlayIsPremul = false)
    {
        if (dstRgba == null || overlayBgra == null || dstWidth <= 0 || dstHeight <= 0 || ovWidth <= 0 || ovHeight <= 0)
        {
            return;
        }

        if (dstRgba.Length < dstWidth * dstHeight * 4 || overlayBgra.Length < ovWidth * ovHeight * 4)
        {
            return;
        }

        bool sameSize = dstWidth == ovWidth && dstHeight == ovHeight;
        for (int y = 0; y < dstHeight; y++)
        {
            int sy = sameSize ? y : Math.Min(ovHeight - 1, (int)((long)y * ovHeight / dstHeight));
            int dstRow = y * dstWidth * 4;
            int srcRow = sy * ovWidth * 4;
            for (int x = 0; x < dstWidth; x++)
            {
                int sx = sameSize ? x : Math.Min(ovWidth - 1, (int)((long)x * ovWidth / dstWidth));
                int sp = srcRow + sx * 4;
                byte sa = overlayBgra[sp + 3];
                if (sa == 0)
                {
                    continue;
                }

                int dp = dstRow + x * 4;
                byte sr = overlayBgra[sp + 2];
                byte sg = overlayBgra[sp + 1];
                byte sb = overlayBgra[sp];
                if (sa == 255)
                {
                    dstRgba[dp] = sr;
                    dstRgba[dp + 1] = sg;
                    dstRgba[dp + 2] = sb;
                }
                else if (overlayIsPremul)
                {
                    int invA = 255 - sa;
                    dstRgba[dp] = (byte)Math.Min(255, sr + (dstRgba[dp] * invA + 127) / 255);
                    dstRgba[dp + 1] = (byte)Math.Min(255, sg + (dstRgba[dp + 1] * invA + 127) / 255);
                    dstRgba[dp + 2] = (byte)Math.Min(255, sb + (dstRgba[dp + 2] * invA + 127) / 255);
                }
                else
                {
                    int invA = 255 - sa;
                    dstRgba[dp] = (byte)((sr * sa + dstRgba[dp] * invA + 127) / 255);
                    dstRgba[dp + 1] = (byte)((sg * sa + dstRgba[dp + 1] * invA + 127) / 255);
                    dstRgba[dp + 2] = (byte)((sb * sa + dstRgba[dp + 2] * invA + 127) / 255);
                }
                dstRgba[dp + 3] = byte.MaxValue;
            }
        }
    }

    private const string PreserveAlphaDedupSuffix = "\u001falpha";

    private static string? BuildPreviewTextureDedupKey(string? texKey, bool preserveAlpha, string? eyeMaterialKey = null)
    {
        if (string.IsNullOrWhiteSpace(texKey))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(eyeMaterialKey))
        {
            return $"{texKey}\u001feye:{eyeMaterialKey}";
        }

        return preserveAlpha ? texKey + PreserveAlphaDedupSuffix : texKey;
    }

    private static bool IsEyeIrisSlotName(string? slotName)
    {
        return !string.IsNullOrWhiteSpace(slotName)
            && (string.Equals(slotName, "_BaseEyeIrisMap", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slotName, "_EyeIrisMap", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slotName, "_IrisMap", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slotName, "_IrisTex", StringComparison.OrdinalIgnoreCase));
    }

    private void TryApplyMultiLayerEyeOverlaysForPreview(
        Material? material,
        string? baseTextureAssetId,
        byte[] baseRgba,
        int baseWidth,
        int baseHeight,
        HashSet<string> seenFileNames,
        List<string> loadedTextureFileNames)
    {
        if (baseRgba == null || baseWidth <= 0 || baseHeight <= 0)
        {
            return;
        }

        if (material == null)
        {
            CompositeEyeIrisOverScleraInPlace(baseRgba, baseWidth, baseHeight);
            return;
        }

        var displayMat = ResolveMaterialForPreview(material) ?? material;
        var texEnvs = displayMat.m_SavedProperties?.m_TexEnvs;
        if (texEnvs == null || texEnvs.Length == 0)
        {
            CompositeEyeIrisOverScleraInPlace(baseRgba, baseWidth, baseHeight);
            return;
        }

        static UnityTexEnv? FindTexEnv(KeyValuePair<string, UnityTexEnv>[] envs, string slotName)
        {
            for (int i = 0; i < envs.Length; i++)
            {
                if (string.Equals(envs[i].Key, slotName, StringComparison.OrdinalIgnoreCase)
                    && envs[i].Value?.m_Texture != null
                    && !envs[i].Value.m_Texture.IsNull)
                {
                    return envs[i].Value;
                }
            }

            return null;
        }

        var appliedTextureIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(baseTextureAssetId))
        {
            appliedTextureIds.Add(baseTextureAssetId);
        }

        // First composite the iris texture over the eye-white (sclera) base layer so transparent regions
        // surrounding the iris disc never render as dark background or premultiplied black rectangles.
        byte[]? scleraBgra = null;
        int scleraWidth = 0;
        int scleraHeight = 0;
        var scleraSlots = new[] { "_BaseEyeWhiteMap", "_EyeWhiteMap", "_EyeWhiteTex", "_ScleraMap" };
        foreach (var scleraSlot in scleraSlots)
        {
            var scleraEnv = FindTexEnv(texEnvs, scleraSlot);
            if (scleraEnv == null)
            {
                continue;
            }

            try
            {
                var scleraTex = ResolveTexturePPtrForPreview(displayMat, scleraEnv.m_Texture);
                if (scleraTex == null)
                {
                    continue;
                }

                var scleraKey = GetPreviewObjectKey(scleraTex);
                if (!string.IsNullOrWhiteSpace(scleraKey) && !appliedTextureIds.Add(scleraKey))
                {
                    continue;
                }

                var fileName = scleraTex.assetsFile?.fileName;
                if (!string.IsNullOrWhiteSpace(fileName) && seenFileNames.Add(fileName))
                {
                    loadedTextureFileNames.Add(fileName);
                }

                using var scleraImg = LoadTexturePreviewThumbnail(scleraTex, MaxCachedPreviewTextureDimension);
                var scleraBmp = scleraImg?.Image;
                if (scleraBmp != null && scleraBmp.Width > 0 && scleraBmp.Height > 0)
                {
                    scleraWidth = scleraBmp.Width;
                    scleraHeight = scleraBmp.Height;
                    scleraBgra = new byte[scleraWidth * scleraHeight * 4];
                    scleraBmp.CopyBgraPixelsTo(scleraBgra);
                    break;
                }
            }
            catch
            {
            }
        }

        CompositeEyeIrisOverScleraInPlace(baseRgba, baseWidth, baseHeight, scleraBgra, scleraWidth, scleraHeight);

        // Next composite pupil then specular highlight layers over the opaque sclera+iris base.
        var overlaySlots = new[] { "_BaseEyePupilMap", "_EyePupilMap", "_PupilMap", "_BaseEyeHighlightMap", "_EyeHighlightMap" };
        foreach (var overlaySlot in overlaySlots)
        {
            var texEnv = FindTexEnv(texEnvs, overlaySlot);
            if (texEnv == null)
            {
                continue;
            }

            try
            {
                var overlayTex = ResolveTexturePPtrForPreview(displayMat, texEnv.m_Texture);
                if (overlayTex == null)
                {
                    continue;
                }

                var overlayKey = GetPreviewObjectKey(overlayTex);
                if (!string.IsNullOrWhiteSpace(overlayKey) && !appliedTextureIds.Add(overlayKey))
                {
                    continue;
                }

                var fileName = overlayTex.assetsFile?.fileName;
                if (!string.IsNullOrWhiteSpace(fileName) && seenFileNames.Add(fileName))
                {
                    loadedTextureFileNames.Add(fileName);
                }

                using var overlayImg = LoadTexturePreviewThumbnail(overlayTex, MaxCachedPreviewTextureDimension);
                var bmp = overlayImg?.Image;
                if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
                {
                    continue;
                }

                var ovBgra = new byte[bmp.Width * bmp.Height * 4];
                bmp.CopyBgraPixelsTo(ovBgra);
                CompositeBgraOverlayOntoRgbaInPlace(
                    baseRgba,
                    baseWidth,
                    baseHeight,
                    ovBgra,
                    bmp.Width,
                    bmp.Height,
                    overlayIsPremul: bmp.AlphaType == SkiaSharp.SKAlphaType.Premul);
            }
            catch
            {
            }
        }
    }

    private void BuildDeduplicatedPreviewTexturesFromResolved(
        int slotCount,
        IReadOnlyList<ResolvedMeshSubMeshMaterial> resolvedSlots,
        Func<bool>? isCancelled,
        out List<byte[]?> uniqueTextures,
        out List<int> uniqueWidths,
        out List<int> uniqueHeights,
        out int[] subMeshToTextureIndex,
        out List<string> loadedTextureFileNames,
        string? meshName = null)
    {
        var bySubMesh = new Dictionary<int, ResolvedMeshSubMeshMaterial>(resolvedSlots.Count);
        foreach (var slot in resolvedSlots)
        {
            if (slot.SubMeshIndex >= 0 && !bySubMesh.ContainsKey(slot.SubMeshIndex))
            {
                bySubMesh[slot.SubMeshIndex] = slot;
            }
        }

        var safeCount = Math.Max(0, slotCount);
        var subMeshTexKeys = new string?[safeCount];
        var subMeshPreserveAlpha = new bool[safeCount];
        var subMeshEyeMaterialIds = new string?[safeCount];
        for (int i = 0; i < safeCount; i++)
        {
            if (bySubMesh.TryGetValue(i, out var resolved))
            {
                subMeshTexKeys[i] = string.IsNullOrWhiteSpace(resolved.MainTextureAssetId)
                    ? null
                    : resolved.MainTextureAssetId;
                subMeshPreserveAlpha[i] = ShouldPreserveResolvedSlotTextureAlphaForPreview(resolved, meshName);
                if (IsEyeIrisSlotName(resolved.MainTextureSlotName) && !string.IsNullOrWhiteSpace(resolved.MaterialAssetId))
                {
                    subMeshEyeMaterialIds[i] = resolved.MaterialAssetId;
                }
            }
        }

        BuildDeduplicatedPreviewTexturesByAssetIds(
            subMeshTexKeys,
            isCancelled,
            out uniqueTextures,
            out uniqueWidths,
            out uniqueHeights,
            out subMeshToTextureIndex,
            out loadedTextureFileNames,
            subMeshPreserveAlpha,
            subMeshEyeMaterialIds);
    }

    private void BuildDeduplicatedPreviewTexturesByAssetIds(
        IReadOnlyList<string?> subMeshTextureIds,
        Func<bool>? isCancelled,
        out List<byte[]?> uniqueTextures,
        out List<int> uniqueWidths,
        out List<int> uniqueHeights,
        out int[] subMeshToTextureIndex,
        out List<string> loadedTextureFileNames,
        IReadOnlyList<bool>? subMeshPreserveAlpha = null,
        IReadOnlyList<string?>? subMeshEyeMaterialIds = null)
    {
        loadedTextureFileNames = new List<string>();
        int slotCount = subMeshTextureIds.Count;
        var dedupKeys = new string?[slotCount];
        var keyToSpec = new Dictionary<string, (string TexId, bool PreserveAlpha, string? EyeMatId)>(StringComparer.Ordinal);
        for (int s = 0; s < slotCount; s++)
        {
            var texId = subMeshTextureIds[s];
            bool preserveAlpha = subMeshPreserveAlpha != null && s < subMeshPreserveAlpha.Count && subMeshPreserveAlpha[s];
            string? eyeMatId = subMeshEyeMaterialIds != null && s < subMeshEyeMaterialIds.Count ? subMeshEyeMaterialIds[s] : null;
            var dKey = BuildPreviewTextureDedupKey(texId, preserveAlpha, eyeMatId);
            dedupKeys[s] = dKey;
            if (dKey != null && !keyToSpec.ContainsKey(dKey))
            {
                keyToSpec[dKey] = (texId!, preserveAlpha, eyeMatId);
            }
        }

        BuildSubMeshTextureDeduplicationPlan(slotCount, dedupKeys, out var uniqueDedupKeys, out subMeshToTextureIndex);

        var uniqueTexIds = new List<string>(uniqueDedupKeys.Count);
        var uniquePreserveAlpha = new bool[uniqueDedupKeys.Count];
        var uniqueEyeMatIds = new string?[uniqueDedupKeys.Count];
        for (int i = 0; i < uniqueDedupKeys.Count; i++)
        {
            var spec = keyToSpec[uniqueDedupKeys[i]];
            uniqueTexIds.Add(spec.TexId);
            uniquePreserveAlpha[i] = spec.PreserveAlpha;
            uniqueEyeMatIds[i] = spec.EyeMatId;
        }

        uniqueTextures = new List<byte[]?>(uniqueTexIds.Count);
        uniqueWidths = new List<int>(uniqueTexIds.Count);
        uniqueHeights = new List<int>(uniqueTexIds.Count);
        for (int i = 0; i < uniqueTexIds.Count; i++)
        {
            uniqueTextures.Add(null);
            uniqueWidths.Add(0);
            uniqueHeights.Add(0);
        }

        if (uniqueTexIds.Count == 0)
        {
            return;
        }

        var coldMisses = new List<(int UniqueIndex, string TexId, AssetHandle Handle, string SourceKey, bool PreserveAlpha)>();
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: warm PNG thumbnail cache hits (0 .assets files loaded!)
        for (int i = 0; i < uniqueTexIds.Count; i++)
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            var texId = uniqueTexIds[i];
            var preserveAlpha = uniquePreserveAlpha[i] || !string.IsNullOrWhiteSpace(uniqueEyeMatIds[i]);
            try
            {
                using var cachedThumb = TryLoadCachedTexturePreviewThumbnailByAssetId(texId, MaxCachedPreviewTextureDimension);
                var rgba = ConvertPreviewImageToRgbaBytes(cachedThumb, out var tw, out var th, preserveAlpha);
                if (rgba != null)
                {
                    uniqueTextures[i] = rgba;
                    uniqueWidths[i] = tw;
                    uniqueHeights[i] = th;
                    continue;
                }
            }
            catch
            {
            }

            var handle = assetsManager.ProjectIndex.GetHandle(texId);
            if (handle != null)
            {
                var sourceKey = ResolveLazyHandleSourcePath(handle) ?? handle.SerializedFileName ?? string.Empty;
                coldMisses.Add((i, texId, handle, sourceKey, preserveAlpha));
            }
        }

        // Pass 2: group cold texture misses by source file so each .assets file loads at most once.
        foreach (var group in coldMisses.GroupBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            foreach (var item in group)
            {
                if (isCancelled?.Invoke() == true)
                {
                    return;
                }

                try
                {
                    var obj = ResolveSemanticRelationHandleForPreview(item.Handle);
                    if (obj is Texture2D tex)
                    {
                        var fileName = tex.assetsFile?.fileName;
                        if (!string.IsNullOrWhiteSpace(fileName) && seenFileNames.Add(fileName))
                        {
                            loadedTextureFileNames.Add(fileName);
                        }

                        using var previewImage = LoadTexturePreviewThumbnail(tex, MaxCachedPreviewTextureDimension);
                        var rgba = ConvertPreviewImageToRgbaBytes(previewImage, out var tw, out var th, item.PreserveAlpha);
                        if (rgba != null)
                        {
                            uniqueTextures[item.UniqueIndex] = rgba;
                            uniqueWidths[item.UniqueIndex] = tw;
                            uniqueHeights[item.UniqueIndex] = th;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        // Pass 3: composite sclera + pupil + highlight overlays onto multi-layer iris eye textures.
        for (int i = 0; i < uniqueEyeMatIds.Length; i++)
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            var eyeMatId = uniqueEyeMatIds[i];
            var baseRgba = uniqueTextures[i];
            if (string.IsNullOrWhiteSpace(eyeMatId) || baseRgba == null || uniqueWidths[i] <= 0 || uniqueHeights[i] <= 0)
            {
                continue;
            }

            Material? eyeMat = null;
            try
            {
                var matHandle = ResolveMaterialHandleByAssetIdForPreview(eyeMatId);
                eyeMat = matHandle != null ? ResolveSemanticRelationHandleForPreview(matHandle) as Material : null;
                if (eyeMat != null)
                {
                    var matFile = eyeMat.assetsFile?.fileName;
                    if (!string.IsNullOrWhiteSpace(matFile) && seenFileNames.Add(matFile))
                    {
                        loadedTextureFileNames.Add(matFile);
                    }
                }
            }
            catch
            {
            }

            TryApplyMultiLayerEyeOverlaysForPreview(
                eyeMat,
                uniqueTexIds[i],
                baseRgba,
                uniqueWidths[i],
                uniqueHeights[i],
                seenFileNames,
                loadedTextureFileNames);
        }
    }

    private void BuildDeduplicatedPreviewTexturesForMaterials(
        int slotCount,
        IReadOnlyList<Material?> materials,
        Func<bool>? isCancelled,
        out List<byte[]?> uniqueTextures,
        out List<int> uniqueWidths,
        out List<int> uniqueHeights,
        out int[] subMeshToTextureIndex,
        out List<string> loadedTextureFileNames,
        string? meshName = null)
    {
        loadedTextureFileNames = new List<string>();
        var safeCount = Math.Max(0, slotCount);
        var resolvedTexByMaterial = new Dictionary<Material, Texture2D?>();
        var subMeshTexKeys = new string?[safeCount];
        var specByDedupKey = new Dictionary<string, (Texture2D Texture, bool PreserveAlpha, Material? EyeMaterial)>(StringComparer.Ordinal);
        bool isDecalMesh = IsDecalMeshOrObjectName(meshName);

        for (int i = 0; i < safeCount; i++)
        {
            if (isCancelled?.Invoke() == true)
            {
                uniqueTextures = new List<byte[]?>();
                uniqueWidths = new List<int>();
                uniqueHeights = new List<int>();
                subMeshToTextureIndex = Array.Empty<int>();
                return;
            }

            var mat = i < materials.Count ? materials[i] : null;
            if (mat == null)
            {
                continue;
            }

            bool preserveAlpha = isDecalMesh || ShouldPreserveMaterialTextureAlpha(mat);

            if (!resolvedTexByMaterial.TryGetValue(mat, out var tex))
            {
                tex = FindTextureForMaterial(mat);
                resolvedTexByMaterial[mat] = tex;
            }

            if (tex != null)
            {
                var key = GetPreviewObjectKey(tex);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var displayMat = ResolveMaterialForPreview(mat) ?? mat;
                    var chosenSlot = MaterialPreviewBuilder.SelectMainTextureSlotName(displayMat);
                    var eyeMatKey = IsEyeIrisSlotName(chosenSlot) ? GetPreviewObjectKey(mat) : null;
                    var dedupKey = BuildPreviewTextureDedupKey(key, preserveAlpha, eyeMatKey);
                    subMeshTexKeys[i] = dedupKey;
                    if (dedupKey != null)
                    {
                        specByDedupKey.TryAdd(dedupKey, (tex, preserveAlpha, eyeMatKey != null ? mat : null));
                    }
                }
            }
        }

        BuildSubMeshTextureDeduplicationPlan(safeCount, subMeshTexKeys, out var uniqueKeys, out subMeshToTextureIndex);

        uniqueTextures = new List<byte[]?>(uniqueKeys.Count);
        uniqueWidths = new List<int>(uniqueKeys.Count);
        uniqueHeights = new List<int>(uniqueKeys.Count);
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < uniqueKeys.Count; i++)
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            byte[]? tb = null;
            int tw = 0, th = 0;
            if (specByDedupKey.TryGetValue(uniqueKeys[i], out var spec))
            {
                var tex = spec.Texture;
                var fileName = tex.assetsFile?.fileName;
                if (!string.IsNullOrWhiteSpace(fileName) && seenFileNames.Add(fileName))
                {
                    loadedTextureFileNames.Add(fileName);
                }

                try
                {
                    using var previewImage = LoadTexturePreviewThumbnail(tex, MaxCachedPreviewTextureDimension);
                    tb = ConvertPreviewImageToRgbaBytes(
                        previewImage,
                        out tw,
                        out th,
                        spec.PreserveAlpha || spec.EyeMaterial != null);
                    if (tb != null && spec.EyeMaterial != null)
                    {
                        TryApplyMultiLayerEyeOverlaysForPreview(
                            spec.EyeMaterial,
                            GetPreviewObjectKey(tex),
                            tb,
                            tw,
                            th,
                            seenFileNames,
                            loadedTextureFileNames);
                    }
                }
                catch
                {
                }
            }

            uniqueTextures.Add(tb);
            uniqueWidths.Add(tw);
            uniqueHeights.Add(th);
        }
    }

    private void AddPreviewTextureSlotsForMesh(
        Mesh mesh,
        IReadOnlyList<Material?> materials,
        List<byte[]?> subMeshTextures,
        List<int> subMeshTexWidths,
        List<int> subMeshTexHeights,
        Func<bool>? isCancelled = null)
    {
        var slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, materials.Count);
        if (slotCount <= 0 && mesh.m_Indices?.Count > 0)
        {
            slotCount = 1;
        }

        BuildDeduplicatedPreviewTexturesForMaterials(
            slotCount,
            materials,
            isCancelled,
            out var uniqueTextures,
            out var uniqueWidths,
            out var uniqueHeights,
            out var subMeshMap,
            out _);

        for (int i = 0; i < slotCount; i++)
        {
            var mapped = i < subMeshMap.Length ? subMeshMap[i] : -1;
            if (mapped >= 0 && mapped < uniqueTextures.Count)
            {
                subMeshTextures.Add(uniqueTextures[mapped]);
                subMeshTexWidths.Add(uniqueWidths[mapped]);
                subMeshTexHeights.Add(uniqueHeights[mapped]);
            }
            else
            {
                subMeshTextures.Add(null);
                subMeshTexWidths.Add(0);
                subMeshTexHeights.Add(0);
            }
        }
    }

    private Material? ResolveMaterialForPreview(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        if (materialPreviewMaterialCache.TryGetValue(material, out var cached) && cached != null)
        {
            return cached;
        }

        var resolved = ResolveMaterialForPreviewUncached(material);
        materialPreviewMaterialCache[material] = resolved;
        return resolved;
    }

    private Material? ResolveMaterialForPreviewUncached(Material material)
    {
        var direct = ResolveMaterialForPreviewUncachedBackground(material);
        if (direct != null || !assetsManager.LazyLoading)
        {
            return direct;
        }

        var visited = new HashSet<Material>();
        var current = material;
        while (current != null && visited.Add(current))
        {
            var hasTex = (current.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                .Any(x => x.Value?.m_Texture != null && !x.Value.m_Texture.IsNull);
            if (hasTex)
            {
                return current;
            }

            if (current.m_Parent == null || current.m_Parent.IsNull)
            {
                break;
            }

            if (current.m_Parent.TryGet(out var loadedParent) && loadedParent != null)
            {
                current = loadedParent;
                continue;
            }

            var parentHandle = FindMaterialHandleForPPtr(current, current.m_Parent);
            var resolvedParent = parentHandle != null ? ResolveSemanticRelationHandleForPreview(parentHandle) as Material : null;
            if (resolvedParent != null)
            {
                current = resolvedParent;
                continue;
            }

            break;
        }

        return null;
    }

    private Material? FindMaterialForMesh(Mesh mesh)
    {
        return FindMaterialsForMesh(mesh).FirstOrDefault(material => material != null);
    }

    private List<Material?> FindMaterialsForMeshPreview(Mesh mesh, string? rendererAssetId = null)
    {
        if (!assetsManager.LazyLoading)
        {
            return FindMaterialsForMesh(mesh);
        }

        if (meshToMaterialsCache != null
            && meshToMaterialsCache.TryGetValue(mesh, out var cachedList)
            && cachedList != null
            && cachedList.Any(m => m != null))
        {
            return new List<Material?>(cachedList);
        }

        var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
        if (semanticMaterials.Any(m => m != null))
        {
            return semanticMaterials;
        }

        // Bounded local-file + on-demand cross-bundle material resolution (resolves only the mesh's renderer materials and chosen diffuse textures).
        if (mesh.assetsFile != null)
        {
            using (assetsManager.LruCache.SuspendEviction())
            {
                BuildLazyPreviewReferenceIndexes(new List<SerializedFile> { mesh.assetsFile });
            }

            if (meshToMaterialsCache != null
                && meshToMaterialsCache.TryGetValue(mesh, out cachedList)
                && cachedList != null
                && cachedList.Any(m => m != null))
            {
                return new List<Material?>(cachedList);
            }

            var resolvedLocalRendererMaterials = TryResolveMaterialsFromLocalRenderersForPreview(mesh, rendererAssetId);
            if (resolvedLocalRendererMaterials.Any(m => m != null))
            {
                return resolvedLocalRendererMaterials;
            }
        }

        return new List<Material?>();
    }

    private List<Material?> TryResolveMaterialsFromLocalRenderersForPreview(Mesh mesh, string? rendererAssetId = null)
    {
        var result = new List<Material?>();
        var sourceFile = mesh.assetsFile;
        if (sourceFile == null || !assetsManager.LazyLoading)
        {
            return result;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        MaterializeReferenceObjects(sourceFile, MeshPreviewReferenceTypes);

        AssetStudio.Object[] fileObjects;
        lock (sourceFile)
        {
            fileObjects = sourceFile.Objects.ToArray();
        }

        Renderer? targetRenderer = null;
        if (!string.IsNullOrWhiteSpace(rendererAssetId))
        {
            var rHandle = assetsManager.ProjectIndex?.GetHandle(rendererAssetId);
            if (rHandle != null)
            {
                targetRenderer = (rHandle.RealObject ?? ResolveSemanticRelationHandleForPreview(rHandle)) as Renderer;
            }
        }

        if (targetRenderer == null)
        {
            foreach (var obj in fileObjects)
            {
                if (obj is SkinnedMeshRenderer smr
                    && smr.m_Mesh != null
                    && !smr.m_Mesh.IsNull
                    && ((smr.m_Mesh.m_FileID == 0 && smr.m_Mesh.m_PathID == mesh.m_PathID)
                        || (smr.m_Mesh.TryGet(out var smrMesh) && AreSamePreviewObject(smrMesh, mesh))))
                {
                    targetRenderer = smr;
                    if (smr.m_Materials is { Length: > 0 })
                    {
                        break;
                    }
                }
            }
        }

        if (targetRenderer == null)
        {
            foreach (var obj in fileObjects)
            {
                if (obj is not MeshRenderer mr || mr.m_GameObject == null || mr.m_GameObject.IsNull)
                {
                    continue;
                }

                var go = Services.SemanticGraphService.ResolveGameObjectBackground(sourceFile, mr.m_GameObject);
                if (go?.m_Components == null)
                {
                    continue;
                }

                bool matchesMesh = false;
                foreach (var compPtr in go.m_Components)
                {
                    if (compPtr != null
                        && !compPtr.IsNull
                        && compPtr.TryGet(out var comp)
                        && comp is MeshFilter mf
                        && mf.m_Mesh != null
                        && !mf.m_Mesh.IsNull
                        && ((mf.m_Mesh.m_FileID == 0 && mf.m_Mesh.m_PathID == mesh.m_PathID)
                            || (mf.m_Mesh.TryGet(out var mfMesh) && AreSamePreviewObject(mfMesh, mesh))))
                    {
                        matchesMesh = true;
                        break;
                    }
                }

                if (matchesMesh)
                {
                    targetRenderer = mr;
                    if (mr.m_Materials is { Length: > 0 })
                    {
                        break;
                    }
                }
            }
        }

        if (targetRenderer?.m_Materials == null || targetRenderer.m_Materials.Length == 0)
        {
            return result;
        }

        var resolvedByKey = new Dictionary<string, Material?>(StringComparer.Ordinal);
        var loadedFiles = new List<string?>();
        foreach (var matPtr in targetRenderer.m_Materials)
        {
            if (matPtr == null || matPtr.IsNull)
            {
                result.Add(null);
                continue;
            }

            if (matPtr.TryGet(out var directMat) && directMat != null)
            {
                IndexMaterialTextures(directMat);
                result.Add(directMat);
                continue;
            }

            var ptrKey = $"{matPtr.m_FileID}:{matPtr.m_PathID}";
            if (!resolvedByKey.TryGetValue(ptrKey, out var resolvedMat))
            {
                var matHandle = FindMaterialHandleForPPtr(targetRenderer, matPtr);
                resolvedMat = matHandle != null ? ResolveSemanticRelationHandleForPreview(matHandle) as Material : null;
                resolvedByKey[ptrKey] = resolvedMat;
                if (resolvedMat != null)
                {
                    loadedFiles.Add(resolvedMat.assetsFile?.fileName);
                    IndexMaterialTextures(resolvedMat);
                    var mainTex = FindTextureForMaterial(resolvedMat);
                    loadedFiles.Add(mainTex?.assetsFile?.fileName);
                }
            }

            result.Add(resolvedMat);
        }

        if (loadedFiles.Count > 0)
        {
            AddActivePreviewDependencyFiles(loadedFiles);
        }

        if (result.Any(m => m != null))
        {
            lock (previewCacheLock)
            {
                meshToMaterialsCache ??= new Dictionary<Mesh, List<Material?>>();
                meshToMaterialsCache[mesh] = result;
            }

            return new List<Material?>(result);
        }

        return new List<Material?>();
    }

    private List<Material?> FindMaterialsForMesh(Mesh mesh)
    {
        if (assetsManager.LazyLoading)
        {
            return FindMaterialsForMeshPreview(mesh);
        }

        if (meshToMaterialsCache == null)
        {
            var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
            if (semanticMaterials.Any(m => m != null))
            {
                return semanticMaterials;
            }

            BuildAssetReferenceIndexes();
        }

        if (meshToMaterialsCache!.TryGetValue(mesh, out var cachedList)
            && cachedList != null
            && cachedList.Any(m => m != null))
        {
            return new List<Material?>(cachedList);
        }

        return new List<Material?>();
    }

    private List<Material?> TryLoadMaterialsForMeshFromSemanticCache(Mesh mesh)
    {
        var materials = new List<Material?>();
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return materials;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return materials;
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var materialIds = _sqliteCache.LoadMeshMaterialAssetIds(folderPath, signature, meshAssetId);
        if (materialIds.Count == 0)
        {
            return materials;
        }

        // Deduplicate material handles before resolving so repeated submesh slots resolve each Material once.
        var resolvedById = new Dictionary<string, Material?>(StringComparer.Ordinal);
        foreach (var materialId in materialIds)
        {
            if (string.IsNullOrWhiteSpace(materialId))
            {
                materials.Add(null);
                continue;
            }

            if (!resolvedById.TryGetValue(materialId, out var resolvedMat))
            {
                var handle = ResolveMaterialHandleByAssetIdForPreview(materialId);
                resolvedMat = handle != null ? ResolveSemanticRelationHandleForPreview(handle) as Material : null;
                resolvedById[materialId] = resolvedMat;
                if (resolvedMat != null)
                {
                    IndexMaterialTextures(resolvedMat);
                }
            }

            materials.Add(resolvedMat);
        }

        if (materials.Any(material => material != null))
        {
            lock (previewCacheLock)
            {
                meshToMaterialsCache ??= new Dictionary<Mesh, List<Material?>>();
                meshToMaterialsCache[mesh] = materials;
            }
            return materials;
        }

        return new List<Material?>();
    }

    private AssetHandle? ResolveMaterialHandleByAssetIdForPreview(string? materialAssetId)
    {
        if (string.IsNullOrWhiteSpace(materialAssetId) || assetsManager.ProjectIndex == null)
        {
            return null;
        }

        var direct = assetsManager.ProjectIndex.GetHandle(materialAssetId);
        if (direct != null)
        {
            return direct;
        }

        int firstHash = materialAssetId.IndexOf('#');
        int lastHash = materialAssetId.LastIndexOf('#');
        if (firstHash > 0 && lastHash >= firstHash)
        {
            var fileName = materialAssetId.Substring(0, firstHash);
            var pathIdSpan = materialAssetId.AsSpan(lastHash + 1);
            if (long.TryParse(pathIdSpan, out var pathId))
            {
                return assetsManager.ProjectIndex
                    .GetHandlesForFile(fileName)
                    .FirstOrDefault(candidate => IsMaterialHandleForPath(candidate, pathId));
            }
        }

        return null;
    }

    private Texture2D? FindTextureForMaterial(Material material)
    {
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialMainTextureCache.TryGetValue(material, out var directCachedTexture) && directCachedTexture != null)
        {
            return directCachedTexture;
        }

        var semanticTexture = TryLoadTextureForMaterialFromSemanticCache(material);
        if (semanticTexture != null)
        {
            return semanticTexture;
        }

        IndexMaterialTextures(material);
        return materialMainTextureCache.TryGetValue(material, out var indexedTexture) ? indexedTexture : null;
    }

    private Texture2D? SelectMainTextureForMaterial(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
    {
        return MaterialPreviewBuilder.SelectMainTexture(displayMaterial, textureSlots);
    }

    private Texture2D? GetMaterialTextureSlot(Material material, string slotName)
    {
        var cachedTexture = materialTextureSlotsCache != null
            && materialTextureSlotsCache.TryGetValue(material, out var slots)
            && slots.TryGetValue(slotName, out var texture)
            ? texture
            : null;

        if (cachedTexture != null)
        {
            return cachedTexture;
        }

        var semanticTexture = TryLoadTextureForMaterialFromSemanticCache(material, slotName);
        if (semanticTexture != null)
        {
            return semanticTexture;
        }

        IndexMaterialTextures(material);
        if (materialTextureSlotsCache != null
            && materialTextureSlotsCache.TryGetValue(material, out slots))
        {
            if (slots.TryGetValue(slotName, out texture) && texture != null)
            {
                return texture;
            }

            if (!slots.ContainsKey(slotName))
            {
                var displayMaterial = ResolveMaterialForPreview(material) ?? material;
                var texEnv = (displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                    .FirstOrDefault(x => string.Equals(x.Key, slotName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(texEnv.Key))
                {
                    var textureRef = texEnv.Value?.m_Texture;
                    texture = textureRef != null && !textureRef.IsNull
                        ? ResolveTexturePPtrForPreview(displayMaterial, textureRef)
                        : null;
                    slots[texEnv.Key] = texture;
                    return texture;
                }
            }
        }

        return null;
    }

    private Texture2D? TryLoadTextureForMaterialFromSemanticCache(Material material, string? slotName = null)
    {
        if (!assetsManager.LazyLoading || material.assetsFile == null || currentScanResult == null)
        {
            return null;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        var materialAssetId = AssetHandle.BuildUniqueID(material.assetsFile, material.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);

        // Fast v15 O(1) MaterialCache PK lookup when looking for main texture (works via WAL even during connections build).
        if (string.IsNullOrEmpty(slotName))
        {
            var cachedEntry = _sqliteCache.LoadMaterialCacheEntry(folderPath, signature, materialAssetId);
            if (cachedEntry != null && !string.IsNullOrWhiteSpace(cachedEntry.MainTextureAssetId))
            {
                // If an older cache stored a non-diffuse slot (e.g. _BaseEyeHighlightMap or _MaskTexture),
                // do not accept it as the main diffuse texture; re-evaluate using the material's slots.
                if (MaterialPreviewBuilder.IsNonDiffuseTextureSlot(cachedEntry.MainTextureSlotName))
                {
                    return null;
                }

                var mainHandle = assetsManager.ProjectIndex.GetHandle(cachedEntry.MainTextureAssetId);
                if (mainHandle != null && ResolveSemanticRelationHandleForPreview(mainHandle) is Texture2D mainTex)
                {
                    materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();
                    materialMainTextureCache[material] = mainTex;
                    return mainTex;
                }
            }
        }

        var texture = TryResolveTextureIdsForMaterialFromSemanticCache(folderPath, signature, material, materialAssetId, slotName);
        if (texture != null)
        {
            return texture;
        }

        var parentMaterialId = GetMaterialParentAssetIdForSemanticCache(material);
        if (!string.IsNullOrEmpty(parentMaterialId)
            && !string.Equals(parentMaterialId, materialAssetId, StringComparison.Ordinal))
        {
            return TryResolveTextureIdsForMaterialFromSemanticCache(folderPath, signature, material, parentMaterialId, slotName);
        }

        return null;
    }

    private Texture2D? TryResolveTextureIdsForMaterialFromSemanticCache(
        string folderPath,
        string signature,
        Material cacheMaterial,
        string materialAssetId,
        string? slotName)
    {
        var textureIds = _sqliteCache.LoadMaterialTextureAssetIds(folderPath, signature, materialAssetId, slotName);
        foreach (var textureId in textureIds)
        {
            var handle = assetsManager.ProjectIndex.GetHandle(textureId);
            if (handle == null)
            {
                continue;
            }

            var asset = ResolveSemanticRelationHandleForPreview(handle);
            if (asset is not Texture2D texture)
            {
                continue;
            }

            if (string.IsNullOrEmpty(slotName))
            {
                materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();
                materialMainTextureCache[cacheMaterial] = texture;
            }
            else
            {
                materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
                if (!materialTextureSlotsCache.TryGetValue(cacheMaterial, out var slots))
                {
                    slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
                    materialTextureSlotsCache[cacheMaterial] = slots;
                }

                slots[slotName] = texture;
            }

            return texture;
        }

        return null;
    }

    private string GetMaterialParentAssetIdForSemanticCache(Material material)
    {
        if (material.assetsFile == null || material.m_Parent == null || material.m_Parent.IsNull)
        {
            return string.Empty;
        }

        if (material.m_Parent.TryGet(out var parentMaterial) && parentMaterial?.assetsFile != null)
        {
            return AssetHandle.BuildUniqueID(parentMaterial.assetsFile, parentMaterial.m_PathID);
        }

        var handle = FindMaterialHandleForPPtr(material, material.m_Parent);
        return handle?.UniqueID ?? string.Empty;
    }

    private void IndexMaterialTextures(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialTextureSlotsCache.ContainsKey(material)
            && materialMainTextureCache.TryGetValue(material, out var existingMain)
            && existingMain != null)
        {
            return;
        }

        var displayMaterial = ResolveMaterialForPreview(material) ?? material;
        if (!materialTextureSlotsCache.TryGetValue(displayMaterial, out var slots)
            || !materialMainTextureCache.TryGetValue(displayMaterial, out var mainTexture)
            || mainTexture == null)
        {
            slots ??= new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
            var texEnvs = displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>();
            mainTexture = null;

            // Resolve candidate diffuse slots in priority order and stop as soon as the main diffuse texture resolves,
            // preventing unnecessary external bundle loads for non-diffuse normal/mask/highlight/matcap maps.
            foreach (var preferredSlot in MaterialPreviewBuilder.PreferredTextureSlots)
            {
                for (int i = 0; i < texEnvs.Length; i++)
                {
                    var texEnv = texEnvs[i];
                    if (!string.Equals(texEnv.Key, preferredSlot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var textureRef = texEnv.Value?.m_Texture;
                    if (textureRef == null || textureRef.IsNull)
                    {
                        slots[texEnv.Key] = null;
                        continue;
                    }

                    if (!slots.TryGetValue(texEnv.Key, out var resolvedTex) || resolvedTex == null)
                    {
                        resolvedTex = ResolveTexturePPtrForPreview(displayMaterial, textureRef);
                        slots[texEnv.Key] = resolvedTex;
                    }

                    if (resolvedTex != null)
                    {
                        mainTexture = resolvedTex;
                        break;
                    }
                }

                if (mainTexture != null)
                {
                    break;
                }
            }

            if (mainTexture == null)
            {
                for (int i = 0; i < texEnvs.Length; i++)
                {
                    var texEnv = texEnvs[i];
                    if (string.IsNullOrWhiteSpace(texEnv.Key) || MaterialPreviewBuilder.IsNonDiffuseTextureSlot(texEnv.Key))
                    {
                        continue;
                    }

                    var textureRef = texEnv.Value?.m_Texture;
                    if (textureRef == null || textureRef.IsNull)
                    {
                        slots[texEnv.Key] = null;
                        continue;
                    }

                    if (!slots.TryGetValue(texEnv.Key, out var resolvedTex) || resolvedTex == null)
                    {
                        resolvedTex = ResolveTexturePPtrForPreview(displayMaterial, textureRef);
                        slots[texEnv.Key] = resolvedTex;
                    }

                    if (resolvedTex != null)
                    {
                        mainTexture = resolvedTex;
                        break;
                    }
                }
            }

            materialTextureSlotsCache[displayMaterial] = slots;
            materialMainTextureCache[displayMaterial] = mainTexture;
        }

        if (!ReferenceEquals(displayMaterial, material))
        {
            materialTextureSlotsCache[material] = slots;
            materialMainTextureCache[material] = materialMainTextureCache[displayMaterial];
        }
    }

    private Texture2D? ResolveTexturePPtr(Material material, PPtr<Texture> textureRef)
    {
        return ResolveTexturePPtrForPreview(material, textureRef);
    }

    private Texture2D? ResolveTexturePPtrForPreview(Material material, PPtr<Texture> textureRef)
    {
        if (textureRef == null || textureRef.IsNull)
        {
            return null;
        }

        var directTexture = ResolveTexturePPtrBackground(material, textureRef);
        if (directTexture != null)
        {
            return directTexture;
        }

        if (!assetsManager.LazyLoading || material.assetsFile == null)
        {
            return null;
        }

        var handle = FindTextureHandleForPPtr(material, textureRef);
        if (handle == null)
        {
            return null;
        }

        return ResolveSemanticRelationHandleForPreview(handle) as Texture2D;
    }

    private AssetHandle? FindTextureHandleForPPtr(Material material, PPtr<Texture> textureRef)
    {
        if (material.assetsFile == null || textureRef == null || textureRef.IsNull)
        {
            return null;
        }

        if (textureRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, textureRef.m_PathID));
            if (IsTexture2DHandleForPath(handle, textureRef.m_PathID))
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (textureRef.m_FileID == 0)
        {
            candidateFileNames.Add(material.assetsFile.fileName);
        }
        else if (textureRef.m_FileID > 0
            && material.assetsFile.m_Externals != null
            && textureRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[textureRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, textureRef.m_PathID));
                if (IsTexture2DHandleForPath(handle, textureRef.m_PathID))
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => IsTexture2DHandleForPath(candidate, textureRef.m_PathID));
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private AssetHandle? FindMaterialHandleForPPtr(AssetStudio.Object owner, PPtr<Material> materialRef)
    {
        if (owner?.assetsFile == null || materialRef == null || materialRef.IsNull)
        {
            return null;
        }

        if (materialRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, materialRef.m_PathID));
            if (IsMaterialHandleForPath(handle, materialRef.m_PathID))
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (materialRef.m_FileID == 0)
        {
            candidateFileNames.Add(owner.assetsFile.fileName);
        }
        else if (materialRef.m_FileID > 0
            && owner.assetsFile.m_Externals != null
            && materialRef.m_FileID - 1 < owner.assetsFile.m_Externals.Count)
        {
            var external = owner.assetsFile.m_Externals[materialRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, materialRef.m_PathID));
                if (IsMaterialHandleForPath(handle, materialRef.m_PathID))
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => IsMaterialHandleForPath(candidate, materialRef.m_PathID));
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private static void AddCandidateFileName(HashSet<string> candidates, string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return;
        }

        candidates.Add(fileNameOrPath);
        var safeName = Path.GetFileName(fileNameOrPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(safeName))
        {
            candidates.Add(safeName);
        }
    }

    private static bool IsTexture2DHandleForPath(AssetHandle? handle, long pathId)
    {
        return handle != null && handle.PathID == pathId && handle.Type == ClassIDType.Texture2D;
    }

    private static bool IsMaterialHandleForPath(AssetHandle? handle, long pathId)
    {
        return handle != null && handle.PathID == pathId && handle.Type == ClassIDType.Material;
    }

    private static int ScoreMaterials(List<Material?> mats)
    {
        return ScoreMaterialsStatic(mats);
    }
}
