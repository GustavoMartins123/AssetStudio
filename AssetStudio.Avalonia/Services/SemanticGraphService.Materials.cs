using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed partial class SemanticGraphService
    {
        // --- Materials ---

        internal readonly struct RendererMaterialBinding
        {
            public RendererMaterialBinding(int materialSlotIndex, int subMeshIndex, PPtr<Material>? materialPointer)
            {
                MaterialSlotIndex = materialSlotIndex;
                SubMeshIndex = subMeshIndex;
                MaterialPointer = materialPointer;
            }

            public int MaterialSlotIndex { get; }
            public int SubMeshIndex { get; }
            public PPtr<Material>? MaterialPointer { get; }
        }

        internal readonly struct ResolvedRendererMaterialBinding
        {
            public ResolvedRendererMaterialBinding(RendererMaterialBinding binding, Material? material, string materialId)
            {
                Binding = binding;
                Material = material;
                MaterialId = materialId;
            }

            public RendererMaterialBinding Binding { get; }
            public Material? Material { get; }
            public string MaterialId { get; }
        }

        private static List<ResolvedRendererMaterialBinding> ResolveRendererMaterialBindings(SerializedFile sourceFile, Renderer renderer)
        {
            var bindings = GetRendererMaterialBindings(renderer);
            var result = new List<ResolvedRendererMaterialBinding>(bindings.Count);
            foreach (var binding in bindings)
            {
                var matPtr = binding.MaterialPointer;
                var rendererMaterial = matPtr != null ? TryGetLoadedObjectNoDeserialize(sourceFile, matPtr) : null;
                var materialId = GetSemanticAssetId(rendererMaterial);
                if (string.IsNullOrEmpty(materialId) && matPtr != null)
                {
                    materialId = GetSemanticAssetIdFromPPtr(sourceFile, matPtr, ClassIDType.Material);
                }

                result.Add(new ResolvedRendererMaterialBinding(binding, rendererMaterial, materialId));
            }

            return result;
        }

        private static List<RendererMaterialBinding> GetRendererMaterialBindings(Renderer renderer)
        {
            var materials = renderer.m_Materials;
            var result = new List<RendererMaterialBinding>(materials?.Length ?? 0);
            if (materials == null || materials.Length == 0)
            {
                return result;
            }

            if (renderer.m_StaticBatchInfo?.subMeshCount > 0)
            {
                var count = Math.Min(materials.Length, renderer.m_StaticBatchInfo.subMeshCount);
                for (var slotIndex = 0; slotIndex < count; slotIndex++)
                {
                    result.Add(new RendererMaterialBinding(
                        slotIndex,
                        renderer.m_StaticBatchInfo.firstSubMesh + slotIndex,
                        materials[slotIndex]));
                }

                return result;
            }

            if (renderer.m_SubsetIndices?.Length > 0)
            {
                var count = Math.Min(materials.Length, renderer.m_SubsetIndices.Length);
                for (var slotIndex = 0; slotIndex < count; slotIndex++)
                {
                    if (renderer.m_SubsetIndices[slotIndex] > int.MaxValue)
                    {
                        continue;
                    }

                    result.Add(new RendererMaterialBinding(
                        slotIndex,
                        (int)renderer.m_SubsetIndices[slotIndex],
                        materials[slotIndex]));
                }

                return result;
            }

            for (var slotIndex = 0; slotIndex < materials.Length; slotIndex++)
            {
                result.Add(new RendererMaterialBinding(slotIndex, slotIndex, materials[slotIndex]));
            }

            return result;
        }

        private static List<Material?> BuildSubMeshMaterialList(List<ResolvedRendererMaterialBinding> bindings)
        {
            var result = new List<Material?>();
            foreach (var binding in bindings)
            {
                if (binding.Binding.SubMeshIndex < 0)
                {
                    continue;
                }

                while (result.Count <= binding.Binding.SubMeshIndex)
                {
                    result.Add(null);
                }

                result[binding.Binding.SubMeshIndex] = binding.Material;
            }

            return result;
        }

        private static void AddMeshMaterialRelation(
            SemanticAssetRelations relations,
            string meshId,
            string materialId,
            Component renderer,
            string rendererType,
            int subMeshIndex,
            int materialSlotIndex,
            List<string> currentMaterialIds)
        {
            var rendererId = GetSemanticAssetId(renderer);
            if (string.IsNullOrEmpty(meshId) || string.IsNullOrEmpty(rendererId))
            {
                return;
            }

            relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                meshId,
                materialId ?? string.Empty,
                rendererId,
                rendererType,
                subMeshIndex,
                materialSlotIndex,
                ScoreMaterialIds(currentMaterialIds)));
        }

        public static int ScoreMaterialIds(IReadOnlyList<string> materialIds)
        {
            return materialIds.Count(id => !string.IsNullOrEmpty(id));
        }

        internal static (float R, float G, float B, float A) ExtractMaterialBaseColor(Material? material)
        {
            var colors = material?.m_SavedProperties?.m_Colors;
            if (colors == null || colors.Length == 0)
            {
                return (1f, 1f, 1f, 1f);
            }

            static (float R, float G, float B, float A) ClampColor(Color color)
            {
                static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
                return (Clamp(color.R), Clamp(color.G), Clamp(color.B), Clamp(color.A));
            }

            string[] preferredNames = { "_BaseColor", "_Color", "_TintColor", "_MainColor", "Color" };
            foreach (var preferred in preferredNames)
            {
                foreach (var entry in colors)
                {
                    if (string.Equals(entry.Key, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        return ClampColor(entry.Value);
                    }
                }
            }

            foreach (var entry in colors)
            {
                if (!string.IsNullOrEmpty(entry.Key)
                    && entry.Key.Contains("color", StringComparison.OrdinalIgnoreCase)
                    && !entry.Key.Contains("emission", StringComparison.OrdinalIgnoreCase)
                    && !entry.Key.Contains("spec", StringComparison.OrdinalIgnoreCase))
                {
                    return ClampColor(entry.Value);
                }
            }

            return (1f, 1f, 1f, 1f);
        }

        private static string ExtractMaterialShaderName(Material material)
        {
            if (material.m_Shader == null || material.m_Shader.IsNull)
            {
                return string.Empty;
            }

            var loadedShader = TryGetLoadedObjectNoDeserialize(material.assetsFile, material.m_Shader);
            if (loadedShader != null)
            {
                return loadedShader.m_ParsedForm?.m_Name ?? loadedShader.m_Name ?? string.Empty;
            }

            var sourceFile = material.assetsFile;
            var arena = sourceFile?.assetsManager?.ProjectIndex?.Arena;
            if (sourceFile != null && arena != null)
            {
                if (material.m_Shader.m_FileID == 0)
                {
                    var activeContext = t_activeFileLookupContext;
                    var origin = activeContext != null && ReferenceEquals(activeContext.SourceFile, sourceFile)
                        ? activeContext.LocalOrigin
                        : AssetHandleOrigin.Intern(sourceFile);
                    if (arena.TryGetIndex(new AssetHandleKey(origin.KeyOriginId, material.m_Shader.m_PathID), out var localIdx))
                    {
                        return arena.GetName(localIdx) ?? string.Empty;
                    }
                }
                else if (TryResolveSemanticPPtrInfo(sourceFile, material.m_Shader.m_FileID, material.m_Shader.m_PathID, ClassIDType.Shader, out _, out var shaderArenaIndex)
                    && shaderArenaIndex >= 0)
                {
                    return arena.GetName(shaderArenaIndex) ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static void AddMaterialTextureRelations(
            SemanticAssetRelations relations,
            Material material,
            Dictionary<Material, Material?> materialPreviewMaterialCache,
            Dictionary<Material, Dictionary<string, Texture2D?>> materialTextureSlotsCache,
            Dictionary<Material, Texture2D?> materialMainTextureCache)
        {
            var materialId = GetSemanticAssetId(material);
            if (string.IsNullOrEmpty(materialId))
            {
                return;
            }

            materialPreviewMaterialCache.TryGetValue(material, out var previewMaterial);
            previewMaterial ??= material;

            if (!materialTextureSlotsCache.TryGetValue(material, out var slots)
                && !materialTextureSlotsCache.TryGetValue(previewMaterial, out slots))
            {
                slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
            }

            var previewMaterialId = GetSemanticAssetId(previewMaterial);
            var mainTextureSlotName = SelectMainTextureSlotNameForRelations(previewMaterial);
            var mainTextureAssetId = string.Empty;
            var fallbackTextureAssetId = string.Empty;
            var fallbackTextureSlotName = string.Empty;
            var slotIndex = 0;
            foreach (var texEnv in previewMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
            {
                slots.TryGetValue(texEnv.Key, out var slotTexture);
                var textureRef = texEnv.Value?.m_Texture;
                var textureId = GetSemanticAssetId(slotTexture);
                if (string.IsNullOrEmpty(textureId))
                {
                    textureId = GetSemanticTextureAssetIdFromPPtr(previewMaterial, textureRef);
                }

                var isMainTexture = string.Equals(texEnv.Key, mainTextureSlotName, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(textureId))
                {
                    if (isMainTexture && string.IsNullOrEmpty(mainTextureAssetId))
                    {
                        mainTextureAssetId = textureId;
                    }
                    else if (string.IsNullOrEmpty(fallbackTextureAssetId) && !NonDiffuseSlots.Contains(texEnv.Key))
                    {
                        fallbackTextureAssetId = textureId;
                        fallbackTextureSlotName = texEnv.Key;
                    }
                }

                var scale = texEnv.Value?.m_Scale ?? new Vector2(1f, 1f);
                var offset = texEnv.Value?.m_Offset ?? new Vector2(0f, 0f);
                var scaleX = float.IsFinite(scale.X) ? scale.X : 1f;
                var scaleY = float.IsFinite(scale.Y) ? scale.Y : 1f;
                var offsetX = float.IsFinite(offset.X) ? offset.X : 0f;
                var offsetY = float.IsFinite(offset.Y) ? offset.Y : 0f;

                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId,
                    previewMaterialId,
                    texEnv.Key,
                    slotIndex,
                    textureId,
                    textureRef?.m_FileID ?? 0,
                    textureRef?.m_PathID ?? 0,
                    !string.IsNullOrEmpty(textureId),
                    isMainTexture,
                    scaleX,
                    scaleY,
                    offsetX,
                    offsetY,
                    "preparation"));

                if (textureRef != null && !textureRef.IsNull)
                {
                    relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                        materialId,
                        "Texture",
                        texEnv.Key,
                        slotIndex,
                        textureId,
                        0,
                        material.m_PathID,
                        textureRef.m_FileID,
                        textureRef.m_PathID,
                        !string.IsNullOrEmpty(textureId)));
                }

                slotIndex++;
            }

            if (string.IsNullOrEmpty(mainTextureAssetId) && !string.IsNullOrEmpty(fallbackTextureAssetId))
            {
                mainTextureAssetId = fallbackTextureAssetId;
                mainTextureSlotName = fallbackTextureSlotName;
            }

            var parentMaterialId = !string.Equals(previewMaterialId, materialId, StringComparison.Ordinal)
                ? previewMaterialId
                : (material.assetsFile != null && material.m_Parent != null && !material.m_Parent.IsNull
                    ? GetSemanticAssetIdFromPPtr(material.assetsFile, material.m_Parent, ClassIDType.Material)
                    : string.Empty);

            var (baseR, baseG, baseB, baseA) = ExtractMaterialBaseColor(previewMaterial);
            relations.MaterialCaches.Add(new SemanticMaterialCacheEntry(
                materialId,
                material.m_Name ?? string.Empty,
                parentMaterialId,
                mainTextureAssetId,
                mainTextureSlotName ?? string.Empty,
                baseR,
                baseG,
                baseB,
                baseA,
                ExtractMaterialShaderName(previewMaterial),
                slotIndex,
                "preparation"));
        }

        public static string? SelectMainTextureSlotNameForRelations(Material displayMaterial)
        {
            var texEnvs = displayMaterial.m_SavedProperties?.m_TexEnvs;
            if (texEnvs == null || texEnvs.Length == 0)
            {
                return null;
            }

            foreach (var preferredSlot in PreferredMaterialTextureSlots)
            {
                foreach (var texEnv in texEnvs)
                {
                    if (string.Equals(texEnv.Key, preferredSlot, StringComparison.OrdinalIgnoreCase)
                        && texEnv.Value?.m_Texture != null
                        && !texEnv.Value.m_Texture.IsNull)
                    {
                        return texEnv.Key;
                    }
                }
            }

            foreach (var preferredSlot in AdditionalPreferredMaterialTextureSlots)
            {
                foreach (var texEnv in texEnvs)
                {
                    if (string.Equals(texEnv.Key, preferredSlot, StringComparison.OrdinalIgnoreCase)
                        && texEnv.Value?.m_Texture != null
                        && !texEnv.Value.m_Texture.IsNull)
                    {
                        return texEnv.Key;
                    }
                }
            }

            foreach (var texEnv in texEnvs)
            {
                if (!NonDiffuseSlots.Contains(texEnv.Key)
                    && texEnv.Value?.m_Texture != null
                    && !texEnv.Value.m_Texture.IsNull)
                {
                    return texEnv.Key;
                }
            }

            return texEnvs
                .FirstOrDefault(texEnv => texEnv.Value?.m_Texture != null && !texEnv.Value.m_Texture.IsNull)
                .Key;
        }

        private static string GetSemanticTextureAssetIdFromPPtr(Material material, PPtr<Texture>? textureRef)
        {
            if (material.assetsFile == null || textureRef == null || textureRef.IsNull)
            {
                return string.Empty;
            }

            return GetSemanticAssetIdFromPPtr(material.assetsFile, textureRef, ClassIDType.Texture);
        }

        public static void IndexMaterialTexturesBackground(
            Material material,
            Dictionary<Material, Material?> localMaterialPreviewMaterialCache,
            Dictionary<Material, Dictionary<string, Texture2D?>> localMaterialTextureSlotsCache,
            Dictionary<Material, Texture2D?> localMaterialMainTextureCache)
        {
            if (localMaterialTextureSlotsCache.ContainsKey(material) && localMaterialMainTextureCache.ContainsKey(material))
            {
                return;
            }

            var displayMaterial = ResolveMaterialForPreviewBackground(material, localMaterialPreviewMaterialCache) ?? material;
            if (!localMaterialTextureSlotsCache.TryGetValue(displayMaterial, out var slots))
            {
                slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
                foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                {
                    var textureRef = texEnv.Value?.m_Texture;
                    slots[texEnv.Key] = textureRef != null && !textureRef.IsNull
                        ? ResolveTexturePPtrBackground(displayMaterial, textureRef)
                        : null;
                }

                localMaterialTextureSlotsCache[displayMaterial] = slots;
                localMaterialMainTextureCache[displayMaterial] = SelectMainTextureForMaterialBackground(displayMaterial, slots);
            }

            if (!ReferenceEquals(displayMaterial, material))
            {
                localMaterialTextureSlotsCache[material] = slots;
                localMaterialMainTextureCache[material] = localMaterialMainTextureCache[displayMaterial];
            }
        }

        public static Material? ResolveMaterialForPreviewBackground(
            Material material,
            Dictionary<Material, Material?> localMaterialPreviewMaterialCache)
        {
            if (localMaterialPreviewMaterialCache.TryGetValue(material, out var cachedMaterial))
            {
                return cachedMaterial;
            }

            var resolvedMaterial = ResolveMaterialForPreviewUncachedBackground(material);
            localMaterialPreviewMaterialCache[material] = resolvedMaterial;
            return resolvedMaterial;
        }

        public static Material? ResolveMaterialForPreviewUncachedBackground(Material material)
        {
            var visited = new HashSet<Material>();
            while (material != null && visited.Add(material))
            {
                var hasTextureReference = (material.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                    .Any(x => x.Value?.m_Texture != null && !x.Value.m_Texture.IsNull);
                if (hasTextureReference)
                {
                    return material;
                }

                var parent = material.m_Parent != null
                    ? TryGetLoadedObjectNoDeserialize(material.assetsFile, material.m_Parent)
                    : null;
                if (parent != null)
                {
                    material = parent;
                    continue;
                }

                break;
            }

            return null;
        }

        public static Texture2D? ResolveTexturePPtrBackground(Material material, PPtr<Texture> textureRef)
        {
            return TryGetLoadedObjectNoDeserialize<Texture, Texture2D>(material?.assetsFile, textureRef);
        }

        public static Texture2D? SelectMainTextureForMaterialBackground(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
        {
            if (displayMaterial.m_SavedProperties?.m_TexEnvs == null)
            {
                return null;
            }

            foreach (var slot in PreferredMaterialTextureSlots)
            {
                if (textureSlots.TryGetValue(slot, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            foreach (var slot in AdditionalPreferredMaterialTextureSlots)
            {
                if (textureSlots.TryGetValue(slot, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            foreach (var env in displayMaterial.m_SavedProperties.m_TexEnvs)
            {
                if (NonDiffuseSlots.Contains(env.Key))
                {
                    continue;
                }

                if (textureSlots.TryGetValue(env.Key, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            return null;
        }

        public static int ScoreMaterialsStatic(List<Material?> mats)
        {
            if (mats == null || mats.Count == 0)
            {
                return 0;
            }

            int score = 0;
            foreach (var mat in mats)
            {
                if (mat == null)
                {
                    continue;
                }

                score += 1;
                if (!mat.m_Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase)
                    && !mat.m_Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                }
            }

            return score;
        }

        public static List<Material?> MergeMeshMaterialLists(List<Material?> existing, List<Material?> incoming)
        {
            if (existing == null || existing.Count == 0)
            {
                return new List<Material?>(incoming ?? new List<Material?>());
            }

            if (incoming == null || incoming.Count == 0)
            {
                return new List<Material?>(existing);
            }

            var merged = new List<Material?>(existing);
            while (merged.Count < incoming.Count)
            {
                merged.Add(null);
            }

            var addedNewSubMesh = false;
            for (var i = 0; i < incoming.Count; i++)
            {
                var incomingMaterial = incoming[i];
                if (incomingMaterial == null || merged[i] != null)
                {
                    continue;
                }

                merged[i] = incomingMaterial;
                addedNewSubMesh = true;
            }

            if (addedNewSubMesh)
            {
                return merged;
            }

            return ScoreMaterialsStatic(incoming) > ScoreMaterialsStatic(existing)
                ? new List<Material?>(incoming)
                : new List<Material?>(existing);
        }

    }
}
