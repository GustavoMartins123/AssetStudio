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

        return name.Contains("Decal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Transparent", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Blend", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Alpha", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cutout", StringComparison.OrdinalIgnoreCase);
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

        if (IsTransparentOrDecalName(material.m_Name) || IsTransparentOrDecalName(shaderName))
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

        if (material.m_CustomRenderQueue >= 2450)
        {
            return true;
        }

        bool explicitlyOpaqueTag = false;
        if (material.stringTagMap != null)
        {
            foreach (var tag in material.stringTagMap)
            {
                if (string.Equals(tag.Key, "RenderType", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tag.Key, "Queue", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsTransparentOrDecalName(tag.Value)
                        || (!string.IsNullOrWhiteSpace(tag.Value)
                            && tag.Value.Contains("Overlay", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    if (string.Equals(tag.Key, "RenderType", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(tag.Value, "Opaque", StringComparison.OrdinalIgnoreCase))
                    {
                        explicitlyOpaqueTag = true;
                    }
                }
            }
        }

        if (material.m_ValidKeywords != null)
        {
            foreach (var keyword in material.m_ValidKeywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword)
                    && (keyword.Contains("TRANSPARENT", StringComparison.OrdinalIgnoreCase)
                        || keyword.Contains("ALPHATEST", StringComparison.OrdinalIgnoreCase)
                        || keyword.Contains("ALPHABLEND", StringComparison.OrdinalIgnoreCase)
                        || keyword.Contains("ALPHAPREMULTIPLY", StringComparison.OrdinalIgnoreCase)
                        || keyword.Contains("ALPHACLIP", StringComparison.OrdinalIgnoreCase)
                        || keyword.Contains("ALPHA_CLIP", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        var props = material.m_SavedProperties;
        if (props != null)
        {
            if (TryGetMaterialNumericProperty(props, "_Surface", out var surface) && surface >= 0.5f)
            {
                return true;
            }

            if ((TryGetMaterialNumericProperty(props, "_AlphaClip", out var alphaClip) && alphaClip > 0f)
                || (TryGetMaterialNumericProperty(props, "_AlphaTest", out var alphaTest) && alphaTest > 0f)
                || (TryGetMaterialNumericProperty(props, "_UseAlphaClip", out var useAlphaClip) && useAlphaClip > 0f))
            {
                return true;
            }

            bool isEyeOpaque = explicitlyOpaqueTag
                && material.m_CustomRenderQueue < 2450
                && (!string.IsNullOrWhiteSpace(material.m_Name) && material.m_Name.Contains("Eye", StringComparison.OrdinalIgnoreCase));
            if (!isEyeOpaque && TryGetMaterialNumericProperty(props, "_ZWrite", out var zWrite) && Math.Abs(zWrite) < 0.001f)
            {
                return true;
            }

            if (props.m_Colors != null)
            {
                foreach (var colorProp in props.m_Colors)
                {
                    if ((string.Equals(colorProp.Key, "_BaseColor", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(colorProp.Key, "_Color", StringComparison.OrdinalIgnoreCase))
                        && colorProp.Value.A > 0f
                        && colorProp.Value.A < 0.99f)
                    {
                        return true;
                    }
                }
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
        if (loadedMaterial != null && ShouldPreserveMaterialTextureAlpha(loadedMaterial, shaderName))
        {
            return true;
        }

        if (IsDecalMeshOrObjectName(meshName) || IsDecalMeshOrObjectName(gameObjectName))
        {
            return true;
        }

        if (slot == null)
        {
            return IsTransparentOrDecalName(shaderName);
        }

        if (IsTransparentOrDecalName(slot.MaterialName) || IsTransparentOrDecalName(shaderName))
        {
            return true;
        }

        if (slot.BaseColorA > 0f && slot.BaseColorA < 0.99f)
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
        var slots = _sqliteCache.LoadResolvedMeshMaterials(folderPath, signature, meshAssetId, rendererAssetId);
        if (slots.Count == 0)
        {
            return slots;
        }

        return EnrichResolvedMeshMaterialsForPreview(slots, folderPath, signature);
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
        ConvertBgraToPreviewRgbaInPlace(rgba, preserveAlpha);
        return rgba;
    }

    internal static void ConvertBgraToPreviewRgbaInPlace(byte[] rgba, bool preserveAlpha)
    {
        if (rgba == null || rgba.Length < 4)
        {
            return;
        }

        byte maxAlpha = 0;
        for (int p = 0; p + 3 < rgba.Length; p += 4)
        {
            byte temp = rgba[p];
            rgba[p] = rgba[p + 2];
            rgba[p + 2] = temp;
            if (preserveAlpha)
            {
                if (rgba[p + 3] > maxAlpha)
                {
                    maxAlpha = rgba[p + 3];
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
        for (int i = 0; i < safeCount; i++)
        {
            if (bySubMesh.TryGetValue(i, out var resolved))
            {
                subMeshTexKeys[i] = string.IsNullOrWhiteSpace(resolved.MainTextureAssetId)
                    ? null
                    : resolved.MainTextureAssetId;
                subMeshPreserveAlpha[i] = ShouldPreserveResolvedSlotTextureAlphaForPreview(resolved, meshName);
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
            subMeshPreserveAlpha);
    }

    private void BuildDeduplicatedPreviewTexturesByAssetIds(
        IReadOnlyList<string?> subMeshTextureIds,
        Func<bool>? isCancelled,
        out List<byte[]?> uniqueTextures,
        out List<int> uniqueWidths,
        out List<int> uniqueHeights,
        out int[] subMeshToTextureIndex,
        out List<string> loadedTextureFileNames,
        IReadOnlyList<bool>? subMeshPreserveAlpha = null)
    {
        loadedTextureFileNames = new List<string>();
        BuildSubMeshTextureDeduplicationPlan(subMeshTextureIds.Count, subMeshTextureIds, out var uniqueTexIds, out subMeshToTextureIndex);

        var uniquePreserveAlpha = new bool[uniqueTexIds.Count];
        if (subMeshPreserveAlpha != null)
        {
            for (int s = 0; s < subMeshToTextureIndex.Length && s < subMeshPreserveAlpha.Count; s++)
            {
                int uIdx = subMeshToTextureIndex[s];
                if (uIdx >= 0 && uIdx < uniquePreserveAlpha.Length && subMeshPreserveAlpha[s])
                {
                    uniquePreserveAlpha[uIdx] = true;
                }
            }
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

        // Pass 1: warm PNG thumbnail cache hits (0 .assets files loaded!)
        for (int i = 0; i < uniqueTexIds.Count; i++)
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            var texId = uniqueTexIds[i];
            var preserveAlpha = uniquePreserveAlpha[i];
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

        if (coldMisses.Count == 0)
        {
            return;
        }

        // Pass 2: group cold texture misses by source file so each .assets file loads at most once.
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        var subMeshPreserveAlpha = new bool[safeCount];
        var texByKey = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
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

            subMeshPreserveAlpha[i] = isDecalMesh || ShouldPreserveMaterialTextureAlpha(mat);

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
                    subMeshTexKeys[i] = key;
                    texByKey.TryAdd(key, tex);
                }
            }
        }

        BuildSubMeshTextureDeduplicationPlan(safeCount, subMeshTexKeys, out var uniqueKeys, out subMeshToTextureIndex);

        var uniquePreserveAlpha = new bool[uniqueKeys.Count];
        for (int s = 0; s < subMeshToTextureIndex.Length; s++)
        {
            int uIdx = subMeshToTextureIndex[s];
            if (uIdx >= 0 && uIdx < uniquePreserveAlpha.Length && subMeshPreserveAlpha[s])
            {
                uniquePreserveAlpha[uIdx] = true;
            }
        }

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
            if (texByKey.TryGetValue(uniqueKeys[i], out var tex))
            {
                var fileName = tex.assetsFile?.fileName;
                if (!string.IsNullOrWhiteSpace(fileName) && seenFileNames.Add(fileName))
                {
                    loadedTextureFileNames.Add(fileName);
                }

                try
                {
                    using var previewImage = LoadTexturePreviewThumbnail(tex, MaxCachedPreviewTextureDimension);
                    tb = ConvertPreviewImageToRgbaBytes(previewImage, out tw, out th, uniquePreserveAlpha[i]);
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
