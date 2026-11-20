using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static void BuildAssetReferenceIndexesBackground(
            List<SerializedFile> assetsFileList,
            List<AssetItem> localExportableAssets,
            out Dictionary<AssetStudio.Object, AssetItem> objectToAssetItemCacheOut,
            out Dictionary<Mesh, List<Material?>> meshToMaterialsCacheOut,
            out Dictionary<Mesh, List<string>> meshAssociatedRenderersCacheOut,
            out Dictionary<Mesh, HashSet<string>> meshSourceTypesCacheOut,
            out Dictionary<Material, Texture2D?> materialMainTextureCacheOut,
            out Dictionary<Material, Material?> materialPreviewMaterialCacheOut,
            out Dictionary<Material, Dictionary<string, Texture2D?>> materialTextureSlotsCacheOut,
            out SemanticAssetRelations semanticRelationsOut)
        {
            var localObjectToAssetItemCache = new Dictionary<AssetStudio.Object, AssetItem>(localExportableAssets.Count);
            foreach (var item in localExportableAssets)
            {
                var asset = item.Asset;
                if (asset != null)
                {
                    localObjectToAssetItemCache[asset] = item;
                }
            }

            var localMeshToMaterialsCache = new Dictionary<Mesh, List<Material?>>();
            var localMeshAssociatedRenderersCache = new Dictionary<Mesh, List<string>>();
            var localMeshSourceTypesCache = new Dictionary<Mesh, HashSet<string>>();
            var localMaterialMainTextureCache = new Dictionary<Material, Texture2D?>();
            var localMaterialPreviewMaterialCache = new Dictionary<Material, Material?>();
            var localMaterialTextureSlotsCache = new Dictionary<Material, Dictionary<string, Texture2D?>>();
            var localSemanticRelations = new SemanticAssetRelations();

            var fileResults = new AssetReferenceIndexBuildResult?[assetsFileList.Count];
            Parallel.For(0, assetsFileList.Count, CreateSemanticGraphParallelOptions(), index =>
            {
                fileResults[index] = BuildAssetReferenceIndexForFile(assetsFileList[index]);
            });

            foreach (var fileResult in fileResults)
            {
                if (fileResult == null)
                {
                    continue;
                }

                foreach (var entry in fileResult.MaterialPreviewMaterialCache)
                {
                    localMaterialPreviewMaterialCache[entry.Key] = entry.Value;
                }

                foreach (var entry in fileResult.MaterialTextureSlotsCache)
                {
                    localMaterialTextureSlotsCache[entry.Key] = entry.Value;
                }

                foreach (var entry in fileResult.MaterialMainTextureCache)
                {
                    localMaterialMainTextureCache[entry.Key] = entry.Value;
                }

                localSemanticRelations.Merge(fileResult.SemanticRelations);

                foreach (var entry in fileResult.MeshToMaterialsCache)
                {
                    AddMeshMaterialsToCache(localMeshToMaterialsCache, entry.Key, entry.Value);
                }

                foreach (var entry in fileResult.MeshSourceTypesCache)
                {
                    foreach (var sourceType in entry.Value)
                    {
                        AddMeshAssociationToCache(
                            localMeshSourceTypesCache,
                            localMeshAssociatedRenderersCache,
                            entry.Key,
                            sourceType,
                            null);
                    }
                }

                foreach (var entry in fileResult.MeshAssociatedRenderersCache)
                {
                    if (!localMeshAssociatedRenderersCache.TryGetValue(entry.Key, out var renderers))
                    {
                        renderers = new List<string>();
                        localMeshAssociatedRenderersCache[entry.Key] = renderers;
                    }

                    renderers.AddRange(entry.Value);
                }
            }

            objectToAssetItemCacheOut = localObjectToAssetItemCache;
            meshToMaterialsCacheOut = localMeshToMaterialsCache;
            meshAssociatedRenderersCacheOut = localMeshAssociatedRenderersCache;
            meshSourceTypesCacheOut = localMeshSourceTypesCache;
            materialMainTextureCacheOut = localMaterialMainTextureCache;
            materialPreviewMaterialCacheOut = localMaterialPreviewMaterialCache;
            materialTextureSlotsCacheOut = localMaterialTextureSlotsCache;
            semanticRelationsOut = localSemanticRelations;
        }

        internal static int GetSemanticGraphMaxDegreeOfParallelism(int? processorCountOverride = null, string? environmentValueOverride = null)
        {
            var value = environmentValueOverride ?? Environment.GetEnvironmentVariable("ASSETSTUDIO_STRUCTURE_THREADS");
            if (int.TryParse(value, out var configuredValue) && configuredValue > 0)
            {
                return configuredValue;
            }

            var processorCount = processorCountOverride ?? Environment.ProcessorCount;
            if (processorCount <= 1)
            {
                return 1;
            }

            return Math.Min(
                AssetsManager.MaxDefaultThreadCount,
                Math.Max(2, (int)Math.Ceiling(processorCount * 0.75)));
        }

        internal static ParallelOptions CreateSemanticGraphParallelOptions(int maxDegreeOfParallelism = 0)
        {
            return new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism > 0
                    ? maxDegreeOfParallelism
                    : GetSemanticGraphMaxDegreeOfParallelism()
            };
        }

        internal static void BuildSemanticRelationsForFilesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            SemanticAssetRelations targetRelations)
        {
            BuildSemanticRelationsForFilesBackground(assetsFileList, targetRelations, maxDegreeOfParallelism: 0);
        }

        internal static void BuildSemanticRelationsForFilesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            SemanticAssetRelations targetRelations,
            int maxDegreeOfParallelism)
        {
            if (assetsFileList == null || assetsFileList.Count == 0 || targetRelations == null)
            {
                return;
            }

            if (assetsFileList.Count == 1 || maxDegreeOfParallelism == 1)
            {
                for (int i = 0; i < assetsFileList.Count; i++)
                {
                    var singleFile = assetsFileList[i];
                    if (singleFile != null)
                    {
                        var singleResult = BuildAssetReferenceIndexForFile(singleFile);
                        targetRelations.Merge(singleResult.SemanticRelations);
                    }
                }
                return;
            }

            var fileResults = new SemanticAssetRelations?[assetsFileList.Count];
            var parallelOptions = CreateSemanticGraphParallelOptions(maxDegreeOfParallelism);
            Parallel.For(0, assetsFileList.Count, parallelOptions, index =>
            {
                var file = assetsFileList[index];
                if (file != null)
                {
                    fileResults[index] = BuildAssetReferenceIndexForFile(file).SemanticRelations;
                }
            });

            foreach (var fileRelations in fileResults)
            {
                if (fileRelations != null)
                {
                    targetRelations.Merge(fileRelations);
                }
            }
        }

        private static AssetReferenceIndexBuildResult BuildAssetReferenceIndexForFile(SerializedFile file)
        {
            var result = new AssetReferenceIndexBuildResult();

            AssetStudio.Object[] objectsSnapshot;
            lock (file)
            {
                objectsSnapshot = file.Objects.ToArray();
            }

            var containerReferences = new List<(string Container, List<PPtr<AssetStudio.Object>> References)>();
            var meshFiltersByGameObjectId = BuildMeshFilterBindingsByGameObject(file, objectsSnapshot);
            var skinnedMeshBindings = BuildSkinnedMeshBindings(file, objectsSnapshot);
            var meshRendererBindings = BuildMeshRendererBindings(file, objectsSnapshot, meshFiltersByGameObjectId);
            var animatorCount = objectsSnapshot.OfType<Animator>().Count();
            var seenRendererMeshRelations = new HashSet<string>(StringComparer.Ordinal);

            result.SemanticRelations.SourceFiles.Add(new SemanticSourceFileEntry(
                file.fileName ?? string.Empty,
                file.originalPath ?? string.Empty,
                file.unityVersion ?? string.Empty,
                file.m_Objects?.Count ?? objectsSnapshot.Length));

            foreach (var obj in objectsSnapshot)
            {
                if (obj is AssetBundle assetBundle)
                {
                    AddAssetBundleContainerReferences(containerReferences, assetBundle);
                }
                else if (obj is ResourceManager resourceManager)
                {
                    AddResourceManagerContainerReferences(containerReferences, resourceManager);
                }
                else if (obj is Material material)
                {
                    IndexMaterialTexturesBackground(
                        material,
                        result.MaterialPreviewMaterialCache,
                        result.MaterialTextureSlotsCache,
                        result.MaterialMainTextureCache);
                    AddMaterialTextureRelations(
                        result.SemanticRelations,
                        material,
                        result.MaterialPreviewMaterialCache,
                        result.MaterialTextureSlotsCache,
                        result.MaterialMainTextureCache);
                }
                else if (obj is SkinnedMeshRenderer smr)
                {
                    IndexSkinnedMeshRendererRelations(file, result, smr);
                }
                else if (obj is Animator animator)
                {
                    AddAnimatorAvatarMeshRelations(
                        file,
                        animator,
                        skinnedMeshBindings,
                        animatorCount,
                        result.SemanticRelations);
                }
                else if (obj is RuntimeAnimatorController controller)
                {
                    IndexRuntimeAnimatorControllerRelations(
                        file,
                        controller,
                        result.SemanticRelations);
                }
                else if (obj is Animation legacyAnimation)
                {
                    AddLegacyAnimationRelations(
                        file,
                        legacyAnimation,
                        skinnedMeshBindings,
                        animatorCount,
                        result.SemanticRelations);
                }
                else if (obj is Avatar avatar)
                {
                    AddAvatarRigRelations(
                        file,
                        avatar,
                        skinnedMeshBindings,
                        objectsSnapshot,
                        result.SemanticRelations);
                }
                else if (obj is AnimationClip animationClip)
                {
                    AddAnimationClipFingerprintRelations(
                        file,
                        animationClip,
                        result.SemanticRelations);
                }
                else if (obj is MeshRenderer mr)
                {
                    IndexMeshRendererComponentRelations(
                        file,
                        result,
                        mr,
                        meshFiltersByGameObjectId,
                        seenRendererMeshRelations);
                }
            }

            AddSceneObjectModelGroupRelations(
                file,
                result.SemanticRelations,
                skinnedMeshBindings.Select(binding => RendererMeshSemanticBinding.FromSkinned(binding))
                    .Concat(meshRendererBindings));
            AddSkinnedMeshFallbackBoneHashes(file, skinnedMeshBindings, result.SemanticRelations);
            AddContainerMaterialRelations(result.SemanticRelations, file, containerReferences);
            AddContainerAnimationRelations(result.SemanticRelations, file, containerReferences);
            return result;
        }

        private static void AddMeshMaterialsToCache(
            Dictionary<Mesh, List<Material?>> meshToMaterialsCache,
            Mesh mesh,
            List<Material?> materials)
        {
            meshToMaterialsCache[mesh] = meshToMaterialsCache.TryGetValue(mesh, out var existingList)
                ? MergeMeshMaterialLists(existingList, materials)
                : new List<Material?>(materials);
        }

        private static void AddMeshAssociationToCache(
            Dictionary<Mesh, HashSet<string>> meshSourceTypesCache,
            Dictionary<Mesh, List<string>> meshAssociatedRenderersCache,
            Mesh mesh,
            string sourceType,
            string? description)
        {
            if (!meshSourceTypesCache.TryGetValue(mesh, out var sourceTypes))
            {
                sourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                meshSourceTypesCache[mesh] = sourceTypes;
            }
            sourceTypes.Add(sourceType);

            if (string.IsNullOrEmpty(description))
            {
                return;
            }

            if (!meshAssociatedRenderersCache.TryGetValue(mesh, out var renderers))
            {
                renderers = new List<string>();
                meshAssociatedRenderersCache[mesh] = renderers;
            }

            renderers.Add(description);
        }

        private static void AddMaterialTextureRelationsForBindings(
            AssetReferenceIndexBuildResult result,
            IEnumerable<ResolvedRendererMaterialBinding> bindings)
        {
            var seenMaterials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in bindings.Select(binding => binding.Material).Where(material => material != null))
            {
                var materialId = GetSemanticAssetId(material);
                if (string.IsNullOrEmpty(materialId) || !seenMaterials.Add(materialId))
                {
                    continue;
                }

                IndexMaterialTexturesBackground(
                    material!,
                    result.MaterialPreviewMaterialCache,
                    result.MaterialTextureSlotsCache,
                    result.MaterialMainTextureCache);
                AddMaterialTextureRelations(
                    result.SemanticRelations,
                    material!,
                    result.MaterialPreviewMaterialCache,
                    result.MaterialTextureSlotsCache,
                    result.MaterialMainTextureCache);
            }
        }

        private static void AddRendererMaterialBindingsToIndex(
            SerializedFile file,
            AssetReferenceIndexBuildResult result,
            Renderer renderer,
            string rendererType,
            Mesh? mesh,
            string meshId)
        {
            if (renderer.m_Materials == null)
            {
                return;
            }

            var resolvedBindings = ResolveRendererMaterialBindings(file, renderer);
            AddMaterialTextureRelationsForBindings(result, resolvedBindings);
            var materialIds = resolvedBindings.Select(binding => binding.MaterialId).ToList();
            foreach (var binding in resolvedBindings)
            {
                AddMeshMaterialRelation(
                    result.SemanticRelations,
                    meshId,
                    binding.MaterialId,
                    renderer,
                    rendererType,
                    binding.Binding.SubMeshIndex,
                    binding.Binding.MaterialSlotIndex,
                    materialIds);
                AddAssetEdge(
                    result.SemanticRelations,
                    renderer,
                    "Material",
                    "m_Materials",
                    binding.Binding.MaterialSlotIndex,
                    binding.MaterialId,
                    binding.Binding.MaterialPointer?.m_FileID ?? 0,
                    binding.Binding.MaterialPointer?.m_PathID ?? 0);
            }

            if (mesh != null)
            {
                AddMeshMaterialsToCache(result.MeshToMaterialsCache, mesh, BuildSubMeshMaterialList(resolvedBindings));
            }
        }

        private static void IndexSkinnedMeshRendererRelations(
            SerializedFile file,
            AssetReferenceIndexBuildResult result,
            SkinnedMeshRenderer smr)
        {
            var smrMesh = ResolveMeshBackground(file, smr.m_Mesh);
            var smrMeshId = GetSemanticAssetId(smrMesh);
            if (string.IsNullOrEmpty(smrMeshId))
            {
                smrMeshId = GetSemanticAssetIdFromPPtr(file, smr.m_Mesh, ClassIDType.Mesh);
            }

            if (string.IsNullOrEmpty(smrMeshId))
            {
                return;
            }

            var go = ResolveGameObjectBackground(file, smr.m_GameObject);
            AddRendererMeshRelation(
                result.SemanticRelations,
                smrMeshId,
                smr,
                "SkinnedMeshRenderer",
                go,
                go != null ? $"SkinnedMeshRenderer on GameObject \"{go.m_Name}\" (PathID: {smr.m_PathID})" : string.Empty);
            AddAssetEdge(result.SemanticRelations, smr, "Mesh", "m_Mesh", 0, smrMeshId, smr.m_Mesh.m_FileID, smr.m_Mesh.m_PathID);

            if (smrMesh != null)
            {
                AddMeshAssociationToCache(
                    result.MeshSourceTypesCache,
                    result.MeshAssociatedRenderersCache,
                    smrMesh,
                    "SkinnedMeshRenderer",
                    go != null ? $"SkinnedMeshRenderer on GameObject \"{go.m_Name}\" (PathID: {smr.m_PathID})" : null);
            }

            AddRendererMaterialBindingsToIndex(file, result, smr, "SkinnedMeshRenderer", smrMesh, smrMeshId);
        }

        private static void IndexMeshRendererComponentRelations(
            SerializedFile file,
            AssetReferenceIndexBuildResult result,
            MeshRenderer mr,
            Dictionary<string, List<MeshFilterSemanticBinding>> meshFiltersByGameObjectId,
            HashSet<string> seenRendererMeshRelations)
        {
            var go = ResolveGameObjectBackground(file, mr.m_GameObject);
            var addedMeshRendererRelation = false;

            if (go?.m_Components != null)
            {
                foreach (var compPtr in go.m_Components)
                {
                    Component? comp = null;
                    if (compPtr.TryGet(out var cp))
                    {
                        comp = cp;
                    }
                    else if (compPtr.m_FileID == 0)
                    {
                        comp = ResolveObjectBackground(file, compPtr.m_PathID) as Component;
                    }

                    if (comp is MeshFilter mf)
                    {
                        var mfMesh = ResolveMeshBackground(file, mf.m_Mesh);
                        var mfMeshId = GetSemanticAssetId(mfMesh);
                        if (string.IsNullOrEmpty(mfMeshId))
                        {
                            mfMeshId = GetSemanticAssetIdFromPPtr(file, mf.m_Mesh, ClassIDType.Mesh);
                        }

                        if (!string.IsNullOrEmpty(mfMeshId))
                        {
                            addedMeshRendererRelation |= AddMeshRendererRelations(
                                file,
                                result,
                                mr,
                                mf,
                                mfMesh,
                                mfMeshId,
                                go,
                                seenRendererMeshRelations);
                        }
                    }
                }
            }

            if (!addedMeshRendererRelation)
            {
                var gameObjectId = GetSemanticGameObjectId(file, mr.m_GameObject, go);
                if (!string.IsNullOrEmpty(gameObjectId)
                    && meshFiltersByGameObjectId.TryGetValue(gameObjectId, out var meshFilterBindings))
                {
                    foreach (var binding in meshFilterBindings)
                    {
                        addedMeshRendererRelation |= AddMeshRendererRelations(
                            file,
                            result,
                            mr,
                            binding.MeshFilter,
                            binding.Mesh,
                            binding.MeshId,
                            go ?? binding.GameObject,
                            seenRendererMeshRelations);
                    }
                }
            }
        }

        private static bool AddMeshRendererRelations(
            SerializedFile file,
            AssetReferenceIndexBuildResult result,
            MeshRenderer renderer,
            MeshFilter meshFilter,
            Mesh? mesh,
            string meshId,
            GameObject? gameObject,
            HashSet<string> seenRendererMeshRelations)
        {
            var rendererId = GetSemanticAssetId(renderer);
            if (string.IsNullOrEmpty(meshId) || string.IsNullOrEmpty(rendererId))
            {
                return false;
            }

            if (!seenRendererMeshRelations.Add($"{rendererId}->{meshId}"))
            {
                return false;
            }

            var gameObjectName = gameObject?.m_Name ?? string.Empty;
            var description = !string.IsNullOrEmpty(gameObjectName)
                ? $"MeshRenderer on GameObject \"{gameObjectName}\" (PathID: {renderer.m_PathID}); MeshFilter PathID: {meshFilter.m_PathID}"
                : $"MeshRenderer PathID: {renderer.m_PathID}; MeshFilter PathID: {meshFilter.m_PathID}";

            AddRendererMeshRelation(result.SemanticRelations, meshId, renderer, "MeshRenderer", gameObject, description);
            AddAssetEdge(result.SemanticRelations, meshFilter, "Mesh", "m_Mesh", 0, meshId, meshFilter.m_Mesh.m_FileID, meshFilter.m_Mesh.m_PathID);

            if (mesh != null)
            {
                AddMeshAssociationToCache(
                    result.MeshSourceTypesCache,
                    result.MeshAssociatedRenderersCache,
                    mesh,
                    "MeshFilter",
                    description);
            }

            AddRendererMaterialBindingsToIndex(file, result, renderer, "MeshRenderer", mesh, meshId);
            return true;
        }

        private static void AddAssetBundleContainerReferences(
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences,
            AssetBundle assetBundle)
        {
            if (assetBundle?.m_Container == null || assetBundle.m_PreloadTable == null)
            {
                return;
            }

            foreach (var entry in assetBundle.m_Container)
            {
                var references = new List<PPtr<AssetStudio.Object>>();
                var assetInfo = entry.Value;
                if (assetInfo?.asset != null && !assetInfo.asset.IsNull)
                {
                    references.Add(assetInfo.asset);
                }

                var preloadIndex = assetInfo?.preloadIndex ?? -1;
                var preloadSize = assetInfo?.preloadSize ?? 0;
                if (preloadIndex >= 0 && preloadSize > 0 && preloadIndex < assetBundle.m_PreloadTable.Length)
                {
                    var preloadEnd = (int)Math.Min((long)preloadIndex + preloadSize, assetBundle.m_PreloadTable.Length);
                    for (var i = preloadIndex; i < preloadEnd; i++)
                    {
                        var reference = assetBundle.m_PreloadTable[i];
                        if (reference != null && !reference.IsNull)
                        {
                            references.Add(reference);
                        }
                    }
                }

                if (references.Count > 0)
                {
                    containerReferences.Add((entry.Key ?? string.Empty, references));
                }
            }
        }

        private static void AddResourceManagerContainerReferences(
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences,
            ResourceManager resourceManager)
        {
            if (resourceManager?.m_Container == null)
            {
                return;
            }

            foreach (var entry in resourceManager.m_Container)
            {
                if (entry.Value != null && !entry.Value.IsNull)
                {
                    containerReferences.Add((entry.Key ?? string.Empty, new List<PPtr<AssetStudio.Object>> { entry.Value }));
                }
            }
        }

        private static void AddContainerMaterialRelations(
            SemanticAssetRelations relations,
            SerializedFile sourceFile,
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences)
        {
            if (containerReferences.Count == 0)
            {
                return;
            }

            var meshesWithRendererRelations = relations.MeshMaterials
                .Where(relation => !string.Equals(relation.RendererType, "Container", StringComparison.OrdinalIgnoreCase))
                .Select(relation => relation.MeshAssetId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var container in containerReferences)
            {
                var meshIds = new List<string>();
                var materialIds = new List<string>();

                foreach (var reference in container.References)
                {
                    var handle = GetSemanticHandleFromPPtr(sourceFile, reference);
                    if (handle == null || string.IsNullOrWhiteSpace(handle.UniqueID))
                    {
                        continue;
                    }

                    if (handle.Type == ClassIDType.Mesh && !meshIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                    {
                        meshIds.Add(handle.UniqueID);
                    }
                    else if (handle.Type == ClassIDType.Material && !materialIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                    {
                        materialIds.Add(handle.UniqueID);
                    }
                }

                if (meshIds.Count == 0 || materialIds.Count == 0)
                {
                    continue;
                }

                var rendererId = BuildContainerRelationId(sourceFile, container.Container);
                foreach (var meshId in meshIds)
                {
                    if (meshesWithRendererRelations.Contains(meshId))
                    {
                        continue;
                    }

                    relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                        meshId,
                        rendererId,
                        "Container",
                        string.Empty,
                        string.Empty,
                        string.IsNullOrWhiteSpace(container.Container)
                            ? "AssetBundle container"
                            : $"AssetBundle container \"{container.Container}\""));

                    var currentMaterialIds = new List<string>();
                    for (var index = 0; index < materialIds.Count; index++)
                    {
                        currentMaterialIds.Add(materialIds[index]);
                        relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                            meshId,
                            materialIds[index],
                            rendererId,
                            "Container",
                            index,
                            index,
                            ScoreMaterialIds(currentMaterialIds)));
                    }
                }
            }
        }

        private static string BuildContainerRelationId(SerializedFile sourceFile, string container)
        {
            var sourceHash = AssetHandle.BuildSourceHash(sourceFile?.fileName ?? string.Empty, sourceFile?.originalPath);
            return $"container:{sourceHash}:{container ?? string.Empty}";
        }

        private sealed class AssetReferenceIndexBuildResult
        {
            public Dictionary<Mesh, List<Material?>> MeshToMaterialsCache { get; } = new();
            public Dictionary<Mesh, List<string>> MeshAssociatedRenderersCache { get; } = new();
            public Dictionary<Mesh, HashSet<string>> MeshSourceTypesCache { get; } = new();
            public Dictionary<Material, Texture2D?> MaterialMainTextureCache { get; } = new();
            public Dictionary<Material, Material?> MaterialPreviewMaterialCache { get; } = new();
            public Dictionary<Material, Dictionary<string, Texture2D?>> MaterialTextureSlotsCache { get; } = new();
            public SemanticAssetRelations SemanticRelations { get; } = new();
        }
    }
}
