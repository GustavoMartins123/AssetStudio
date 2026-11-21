using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private static readonly string[] PreferredMaterialTextureSlots = MaterialPreviewBuilder.PreferredTextureSlots;

    private static readonly HashSet<string> NonDiffuseSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
        "_Normal", "Normal", "normal",
        "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
        "_EmissionMap", "_ParallaxMap", "_DetailMask",
        "_Cubemap", "_ReflectionTex", "_ShadowMap"
    };

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
        return _sqliteCache.LoadResolvedMeshMaterials(folderPath, signature, meshAssetId, rendererAssetId);
    }

    private static byte[]? ConvertPreviewImageToRgbaBytes(TexturePreviewImageResult? previewImage, out int width, out int height)
    {
        width = 0;
        height = 0;
        var image = previewImage?.Image;
        if (image == null || image.Width <= 0 || image.Height <= 0)
        {
            return null;
        }

        width = image.Width;
        height = image.Height;
        var rgba = new byte[width * height * 4];
        image.CopyBgraPixelsTo(rgba);
        for (int p = 0; p < rgba.Length; p += 4)
        {
            byte temp = rgba[p];
            rgba[p] = rgba[p + 2];
            rgba[p + 2] = temp;
            rgba[p + 3] = byte.MaxValue;
        }

        return rgba;
    }

    private void BuildDeduplicatedPreviewTexturesFromResolved(
        int slotCount,
        IReadOnlyList<ResolvedMeshSubMeshMaterial> resolvedSlots,
        Func<bool>? isCancelled,
        out List<byte[]?> uniqueTextures,
        out List<int> uniqueWidths,
        out List<int> uniqueHeights,
        out int[] subMeshToTextureIndex,
        out List<string> loadedTextureFileNames)
    {
        var bySubMesh = new Dictionary<int, string?>(resolvedSlots.Count);
        foreach (var slot in resolvedSlots)
        {
            if (slot.SubMeshIndex >= 0 && !bySubMesh.ContainsKey(slot.SubMeshIndex))
            {
                bySubMesh[slot.SubMeshIndex] = string.IsNullOrWhiteSpace(slot.MainTextureAssetId)
                    ? null
                    : slot.MainTextureAssetId;
            }
        }

        var subMeshTexKeys = new string?[Math.Max(0, slotCount)];
        for (int i = 0; i < subMeshTexKeys.Length; i++)
        {
            if (bySubMesh.TryGetValue(i, out var texId))
            {
                subMeshTexKeys[i] = texId;
            }
        }

        BuildDeduplicatedPreviewTexturesByAssetIds(
            subMeshTexKeys,
            isCancelled,
            out uniqueTextures,
            out uniqueWidths,
            out uniqueHeights,
            out subMeshToTextureIndex,
            out loadedTextureFileNames);
    }

    private void BuildDeduplicatedPreviewTexturesByAssetIds(
        IReadOnlyList<string?> subMeshTextureIds,
        Func<bool>? isCancelled,
        out List<byte[]?> uniqueTextures,
        out List<int> uniqueWidths,
        out List<int> uniqueHeights,
        out int[] subMeshToTextureIndex,
        out List<string> loadedTextureFileNames)
    {
        loadedTextureFileNames = new List<string>();
        BuildSubMeshTextureDeduplicationPlan(subMeshTextureIds.Count, subMeshTextureIds, out var uniqueTexIds, out subMeshToTextureIndex);

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

        var coldMisses = new List<(int UniqueIndex, string TexId, AssetHandle Handle, string SourceKey)>();

        // Pass 1: warm PNG thumbnail cache hits (0 .assets files loaded!)
        for (int i = 0; i < uniqueTexIds.Count; i++)
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            var texId = uniqueTexIds[i];
            try
            {
                using var cachedThumb = TryLoadCachedTexturePreviewThumbnailByAssetId(texId, MaxCachedPreviewTextureDimension);
                var rgba = ConvertPreviewImageToRgbaBytes(cachedThumb, out var tw, out var th);
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
                coldMisses.Add((i, texId, handle, sourceKey));
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
                        var rgba = ConvertPreviewImageToRgbaBytes(previewImage, out var tw, out var th);
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
        out List<string> loadedTextureFileNames)
    {
        loadedTextureFileNames = new List<string>();
        var safeCount = Math.Max(0, slotCount);
        var resolvedTexByMaterial = new Dictionary<Material, Texture2D?>();
        var subMeshTexKeys = new string?[safeCount];
        var texByKey = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

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
                    tb = ConvertPreviewImageToRgbaBytes(previewImage, out tw, out th);
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
        return ResolveMaterialForPreviewBackground(material, materialPreviewMaterialCache);
    }

    private Material? ResolveMaterialForPreviewUncached(Material material)
    {
        return ResolveMaterialForPreviewUncachedBackground(material);
    }

    private Material? FindMaterialForMesh(Mesh mesh)
    {
        return FindMaterialsForMesh(mesh).FirstOrDefault(material => material != null);
    }

    private List<Material?> FindMaterialsForMeshPreview(Mesh mesh)
    {
        if (!assetsManager.LazyLoading)
        {
            return FindMaterialsForMesh(mesh);
        }

        if (meshToMaterialsCache != null && meshToMaterialsCache.TryGetValue(mesh, out var cachedList))
        {
            return new List<Material?>(cachedList);
        }

        var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
        if (semanticMaterials.Count > 0)
        {
            return semanticMaterials;
        }

        if (CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
        {
            return new List<Material?>();
        }

        // Bounded local-file fallback: inspect only mesh.assetsFile (never re-index all 50 preloaded files).
        if (mesh.assetsFile != null)
        {
            using (assetsManager.LruCache.SuspendEviction())
            {
                BuildLazyPreviewReferenceIndexes(new List<SerializedFile> { mesh.assetsFile });
            }

            if (meshToMaterialsCache != null && meshToMaterialsCache.TryGetValue(mesh, out cachedList))
            {
                return new List<Material?>(cachedList);
            }
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
            if (semanticMaterials.Count > 0)
            {
                return semanticMaterials;
            }

            BuildAssetReferenceIndexes();
        }

        if (meshToMaterialsCache!.TryGetValue(mesh, out var cachedList))
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
                var handle = assetsManager.ProjectIndex.GetHandle(materialId);
                resolvedMat = handle != null ? ResolveSemanticRelationHandleForPreview(handle) as Material : null;
                resolvedById[materialId] = resolvedMat;
            }

            materials.Add(resolvedMat);
        }

        if (materials.Any(material => material != null))
        {
            meshToMaterialsCache ??= new Dictionary<Mesh, List<Material?>>();
            meshToMaterialsCache[mesh] = materials;
            return materials;
        }

        return new List<Material?>();
    }

    private Texture2D? FindTextureForMaterial(Material material)
    {
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialMainTextureCache.TryGetValue(material, out var directCachedTexture))
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
        return SelectMainTextureForMaterialBackground(displayMaterial, textureSlots);
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
        return materialTextureSlotsCache != null
               && materialTextureSlotsCache.TryGetValue(material, out slots)
               && slots.TryGetValue(slotName, out texture)
            ? texture
            : null;
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

        if (materialTextureSlotsCache.ContainsKey(material) && materialMainTextureCache.ContainsKey(material))
        {
            return;
        }

        var displayMaterial = ResolveMaterialForPreview(material) ?? material;
        if (!materialTextureSlotsCache.TryGetValue(displayMaterial, out var slots))
        {
            slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
            foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
            {
                var textureRef = texEnv.Value?.m_Texture;
                slots[texEnv.Key] = textureRef != null && !textureRef.IsNull
                    ? ResolveTexturePPtrForPreview(displayMaterial, textureRef)
                    : null;
            }

            materialTextureSlotsCache[displayMaterial] = slots;
            materialMainTextureCache[displayMaterial] = SelectMainTextureForMaterial(displayMaterial, slots);
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

    private AssetHandle? FindMaterialHandleForPPtr(Material material, PPtr<Material> materialRef)
    {
        if (material.assetsFile == null || materialRef == null || materialRef.IsNull)
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
            candidateFileNames.Add(material.assetsFile.fileName);
        }
        else if (materialRef.m_FileID > 0
            && material.assetsFile.m_Externals != null
            && materialRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[materialRef.m_FileID - 1];
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
