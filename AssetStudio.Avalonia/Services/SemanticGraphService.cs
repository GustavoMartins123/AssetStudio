using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class SemanticGraphService
    {
        public const int LazyConnectionStagingFlushRowThreshold = 10_000;
        public const int MaxClipBoneHashProbes = 24;
        public const int MaxRigBoneHashProbes = 192;

        public static readonly string[] PreferredMaterialTextureSlots = MaterialPreviewBuilder.PreferredTextureSlots;

        public static readonly HashSet<string> NonDiffuseSlots = new(StringComparer.OrdinalIgnoreCase)
        {
            "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
            "_Normal", "Normal", "normal",
            "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
            "_EmissionMap", "_ParallaxMap", "_DetailMask",
            "_Cubemap", "_ReflectionTex", "_ShadowMap"
        };

        public static readonly HashSet<ClassIDType> LazyConnectionReferenceTypes = new()
        {
            ClassIDType.GameObject,
            ClassIDType.Transform,
            ClassIDType.RectTransform,
            ClassIDType.MeshFilter,
            ClassIDType.MeshRenderer,
            ClassIDType.SkinnedMeshRenderer,
            ClassIDType.Material,
            ClassIDType.Avatar,
            ClassIDType.Animator,
            ClassIDType.Animation,
            ClassIDType.AnimationClip,
            ClassIDType.AnimatorController,
            ClassIDType.AnimatorOverrideController,
            ClassIDType.RuntimeAnimatorController
        };

        internal static int CountBufferedSemanticRelationRows(SemanticAssetRelations? relations)
        {
            if (relations == null)
            {
                return 0;
            }

            return relations.SourceFiles.Count
                + relations.AssetEdges.Count
                + relations.ModelGroups.Count
                + relations.ModelGroupMeshes.Count
                + relations.MeshRenderers.Count
                + relations.MeshMaterials.Count
                + relations.MaterialTextures.Count
                + relations.AnimationClipTargets.Count
                + relations.RigBoneHashes.Count
                + relations.ClipBoneHashes.Count;
        }

        public static int GetSemanticGraphMaxDegreeOfParallelism(int? processorCountOverride = null, string? environmentValueOverride = null)
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

        public static ParallelOptions CreateSemanticGraphParallelOptions(int maxDegreeOfParallelism = 0)
        {
            return new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism > 0
                    ? maxDegreeOfParallelism
                    : GetSemanticGraphMaxDegreeOfParallelism()
            };
        }

        internal static void BuildAssetReferenceIndexesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            IReadOnlyList<AssetItem> localExportableAssets,
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

        internal static AssetReferenceIndexBuildResult BuildAssetReferenceIndexForFile(SerializedFile file)
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

        // --- Semantic Resolution ---

        public static AssetHandle? GetSemanticHandleFromPPtr(SerializedFile sourceFile, PPtr<AssetStudio.Object> pptr)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null || pptr == null || pptr.IsNull)
            {
                return null;
            }

            if (pptr.TryGetAssetsFile(out var targetFile))
            {
                var handle = sourceFile.assetsManager.ProjectIndex.GetHandle(targetFile, pptr.m_PathID);
                if (handle != null)
                {
                    return handle;
                }
            }

            if (pptr.m_FileID == 0)
            {
                return sourceFile.assetsManager.ProjectIndex.GetHandle(sourceFile, pptr.m_PathID);
            }

            return FindSemanticHandleForPPtr(sourceFile, pptr.m_FileID, pptr.m_PathID, expectedType: null);
        }

        public static Transform? ResolveTransformForGameObjectBackground(SerializedFile sourceFile, GameObject gameObject)
        {
            if (gameObject.m_Transform != null)
            {
                return gameObject.m_Transform;
            }

            if (gameObject.m_Components == null)
            {
                return null;
            }

            foreach (var componentPtr in gameObject.m_Components)
            {
                Component? component = null;
                if (componentPtr.TryGet(out var resolvedComponent))
                {
                    component = resolvedComponent;
                }
                else if (componentPtr.m_FileID == 0)
                {
                    component = ResolveObjectBackground(sourceFile, componentPtr.m_PathID) as Component;
                }

                if (component is Transform transform)
                {
                    return transform;
                }
            }

            return null;
        }

        public static Transform? ResolveTransformBackground(SerializedFile sourceFile, PPtr<Transform> pptr)
        {
            if (pptr.TryGet(out var transform))
            {
                return transform;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Transform : null;
        }

        public static Avatar? ResolveAvatarBackground(SerializedFile sourceFile, PPtr<Avatar> pptr)
        {
            if (pptr.TryGet(out var avatar))
            {
                return avatar;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Avatar : null;
        }

        public static RuntimeAnimatorController? ResolveRuntimeAnimatorControllerBackground(
            SerializedFile sourceFile,
            PPtr<RuntimeAnimatorController> pptr)
        {
            if (pptr.TryGet(out var controller))
            {
                return controller;
            }

            return pptr.m_FileID == 0
                ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as RuntimeAnimatorController
                : null;
        }

        public static string GetSemanticGameObjectId(
            SerializedFile sourceFile,
            PPtr<GameObject> gameObjectPtr,
            GameObject? gameObject)
        {
            var gameObjectId = GetSemanticAssetId(gameObject);
            if (!string.IsNullOrEmpty(gameObjectId))
            {
                return gameObjectId;
            }

            return GetSemanticAssetIdFromPPtr(sourceFile, gameObjectPtr, ClassIDType.GameObject);
        }

        public static GameObject? ResolveGameObjectBackground(SerializedFile sourceFile, PPtr<GameObject> pptr)
        {
            if (pptr.TryGet(out var go))
            {
                return go;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as GameObject : null;
        }

        public static Mesh? ResolveMeshBackground(SerializedFile sourceFile, PPtr<Mesh> pptr)
        {
            if (pptr.TryGet(out var mesh))
            {
                return mesh;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Mesh : null;
        }

        public static Material? ResolveRendererMaterialBackground(PPtr<Material> pptr)
        {
            if (pptr.TryGet(out var material))
            {
                return material;
            }

            return null;
        }

        public static AssetStudio.Object? ResolveObjectBackground(SerializedFile sourceFile, long pathID)
        {
            lock (sourceFile)
            {
                if (sourceFile.ObjectsDic.TryGetValue(pathID, out var obj))
                {
                    return obj;
                }
            }

            if (sourceFile.assetsManager?.LazyLoading == true && sourceFile.assetsManager.ProjectIndex != null)
            {
                var handle = sourceFile.assetsManager.ProjectIndex.GetHandle(sourceFile, pathID);
                if (handle != null)
                {
                    return sourceFile.assetsManager.ResolveHandle(handle);
                }
            }

            return null;
        }

        public static string GetSemanticAssetId(AssetStudio.Object? asset)
        {
            return asset?.assetsFile != null
                ? AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID)
                : string.Empty;
        }

        public static string GetSemanticAssetIdFromPPtr<T>(SerializedFile sourceFile, PPtr<T> pptr, ClassIDType expectedType)
            where T : AssetStudio.Object
        {
            if (sourceFile == null || pptr == null || pptr.IsNull)
            {
                return string.Empty;
            }

            if (pptr.TryGetAssetsFile(out var targetFile))
            {
                return AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID);
            }

            if (pptr.m_FileID == 0)
            {
                return AssetHandle.BuildUniqueID(sourceFile, pptr.m_PathID);
            }

            var handle = FindSemanticHandleForPPtr(sourceFile, pptr.m_FileID, pptr.m_PathID, expectedType);
            return handle?.UniqueID ?? string.Empty;
        }

        public static AssetHandle? FindSemanticHandleForPPtr(SerializedFile sourceFile, int fileId, long pathId, ClassIDType? expectedType)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null
                || sourceFile.m_Externals == null
                || fileId <= 0
                || fileId - 1 >= sourceFile.m_Externals.Count)
            {
                return null;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var external = sourceFile.m_Externals[fileId - 1];
            if (external == null)
            {
                return null;
            }

            AddSemanticFileNameCandidate(candidates, external.fileName);
            AddSemanticFileNameCandidate(candidates, external.pathName);

            return candidates
                .SelectMany(fileName => sourceFile.assetsManager.ProjectIndex.GetHandlesForFile(fileName))
                .Where(candidate => candidate.PathID == pathId && (expectedType == null || candidate.Type == expectedType.Value))
                .GroupBy(candidate => candidate.UniqueID, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(candidate => ScoreSemanticHandleMatch(candidate, external, sourceFile))
                .FirstOrDefault();
        }

        private static int ScoreSemanticHandleMatch(AssetHandle handle, FileIdentifier external, SerializedFile sourceFile)
        {
            var score = 0;
            if (IsSameSemanticSource(handle.OriginalPath, external.pathName)
                || IsSameSemanticSource(handle.SourceFile?.originalPath, external.pathName)
                || IsSameSemanticSource(handle.SourceFile?.fullName, external.pathName))
            {
                score += 100;
            }

            if (IsSameSemanticSource(handle.SerializedFileName, external.fileName)
                || IsSameSemanticSource(handle.SourceFile?.fileName, external.fileName))
            {
                score += 50;
            }

            var externalPathFileName = GetSemanticFileName(external.pathName);
            if (!string.IsNullOrEmpty(externalPathFileName)
                && (IsSameSemanticSource(GetSemanticFileName(handle.OriginalPath), externalPathFileName)
                    || IsSameSemanticSource(handle.SerializedFileName, externalPathFileName)
                    || IsSameSemanticSource(handle.SourceFile?.fileName, externalPathFileName)))
            {
                score += 25;
            }

            var sourceDirectory = Path.GetDirectoryName(sourceFile.originalPath ?? sourceFile.fullName ?? string.Empty);
            var handleDirectory = Path.GetDirectoryName(handle.OriginalPath ?? handle.SourceFile?.originalPath ?? string.Empty);
            if (!string.IsNullOrEmpty(sourceDirectory)
                && !string.IsNullOrEmpty(handleDirectory)
                && string.Equals(sourceDirectory, handleDirectory, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            return score;
        }

        private static string GetSemanticFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFileName(normalized);
        }

        public static bool IsSameSemanticSource(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void AddSemanticFileNameCandidate(HashSet<string> candidates, string? fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return;
            }

            candidates.Add(fileNameOrPath);
            var normalized = fileNameOrPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(fileName);
            }
        }

        private static void AddRendererMeshRelation(
            SemanticAssetRelations relations,
            string meshId,
            Component renderer,
            string rendererType,
            GameObject? gameObject,
            string description)
        {
            var rendererId = GetSemanticAssetId(renderer);
            if (string.IsNullOrEmpty(meshId) || string.IsNullOrEmpty(rendererId))
            {
                return;
            }

            relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                meshId,
                rendererId,
                rendererType,
                GetSemanticAssetId(gameObject),
                gameObject?.m_Name ?? string.Empty,
                description ?? string.Empty));
        }

        private static void AddAssetEdge(
            SemanticAssetRelations relations,
            AssetStudio.Object source,
            string edgeKind,
            string slotName,
            int slotIndex,
            string targetId,
            int targetFileId,
            long targetPathId)
        {
            var sourceId = GetSemanticAssetId(source);
            if (string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                sourceId,
                edgeKind,
                slotName,
                slotIndex,
                targetId ?? string.Empty,
                0,
                source.m_PathID,
                targetFileId,
                targetPathId,
                !string.IsNullOrEmpty(targetId)));
        }

        private static void AddAssetEdgeById(
            SemanticAssetRelations relations,
            string sourceId,
            string edgeKind,
            string slotName,
            int slotIndex,
            string targetId,
            int sourceFileId,
            long sourcePathId,
            int targetFileId,
            long targetPathId,
            bool isResolved)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                sourceId,
                edgeKind,
                slotName,
                slotIndex,
                targetId ?? string.Empty,
                sourceFileId,
                sourcePathId,
                targetFileId,
                targetPathId,
                isResolved && !string.IsNullOrEmpty(targetId)));
        }

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
                var rendererMaterial = matPtr != null ? ResolveRendererMaterialBackground(matPtr) : null;
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

                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId,
                    previewMaterialId,
                    texEnv.Key,
                    slotIndex,
                    textureId,
                    textureRef?.m_FileID ?? 0,
                    textureRef?.m_PathID ?? 0,
                    !string.IsNullOrEmpty(textureId),
                    string.Equals(texEnv.Key, mainTextureSlotName, StringComparison.OrdinalIgnoreCase)));

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

            return GetSemanticAssetIdFromPPtr(material.assetsFile, textureRef, ClassIDType.Texture2D);
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

                if (material.m_Parent != null && material.m_Parent.TryGet(out var parent))
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
            if (textureRef.TryGet<Texture2D>(out var directTex))
            {
                return directTex;
            }

            if (material.assetsFile?.ObjectsDic != null
                && textureRef.m_FileID == 0
                && material.assetsFile.ObjectsDic.TryGetValue(textureRef.m_PathID, out var localObj)
                && localObj is Texture2D localTex)
            {
                return localTex;
            }

            return null;
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

        // --- Model Groups ---

        private static Dictionary<string, List<MeshFilterSemanticBinding>> BuildMeshFilterBindingsByGameObject(
            SerializedFile sourceFile,
            IEnumerable<AssetStudio.Object> objects)
        {
            var result = new Dictionary<string, List<MeshFilterSemanticBinding>>(StringComparer.Ordinal);
            foreach (var meshFilter in objects.OfType<MeshFilter>())
            {
                var gameObject = ResolveGameObjectBackground(sourceFile, meshFilter.m_GameObject);
                var gameObjectId = GetSemanticGameObjectId(sourceFile, meshFilter.m_GameObject, gameObject);
                if (string.IsNullOrEmpty(gameObjectId))
                {
                    continue;
                }

                var mesh = ResolveMeshBackground(sourceFile, meshFilter.m_Mesh);
                var meshId = GetSemanticAssetId(mesh);
                if (string.IsNullOrEmpty(meshId))
                {
                    meshId = GetSemanticAssetIdFromPPtr(sourceFile, meshFilter.m_Mesh, ClassIDType.Mesh);
                }

                if (string.IsNullOrEmpty(meshId))
                {
                    continue;
                }

                if (!result.TryGetValue(gameObjectId, out var bindings))
                {
                    bindings = new List<MeshFilterSemanticBinding>();
                    result[gameObjectId] = bindings;
                }

                bindings.Add(new MeshFilterSemanticBinding(meshFilter, mesh, meshId, gameObject));
            }

            return result;
        }

        private static List<SkinnedMeshSemanticBinding> BuildSkinnedMeshBindings(
            SerializedFile sourceFile,
            IEnumerable<AssetStudio.Object> objects)
        {
            var result = new List<SkinnedMeshSemanticBinding>();
            foreach (var renderer in objects.OfType<SkinnedMeshRenderer>())
            {
                var mesh = ResolveMeshBackground(sourceFile, renderer.m_Mesh);
                var meshId = GetSemanticAssetId(mesh);
                if (string.IsNullOrEmpty(meshId))
                {
                    meshId = GetSemanticAssetIdFromPPtr(sourceFile, renderer.m_Mesh, ClassIDType.Mesh);
                }

                if (string.IsNullOrEmpty(meshId))
                {
                    continue;
                }

                result.Add(new SkinnedMeshSemanticBinding(
                    renderer,
                    mesh,
                    meshId,
                    ResolveGameObjectBackground(sourceFile, renderer.m_GameObject),
                    renderer.m_Mesh.m_FileID,
                    renderer.m_Mesh.m_PathID));
            }

            return result;
        }

        private static List<RendererMeshSemanticBinding> BuildMeshRendererBindings(
            SerializedFile sourceFile,
            IEnumerable<AssetStudio.Object> objects,
            IReadOnlyDictionary<string, List<MeshFilterSemanticBinding>> meshFiltersByGameObjectId)
        {
            var result = new List<RendererMeshSemanticBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in objects.OfType<MeshRenderer>())
            {
                var gameObject = ResolveGameObjectBackground(sourceFile, renderer.m_GameObject);
                var gameObjectId = GetSemanticGameObjectId(sourceFile, renderer.m_GameObject, gameObject);
                if (string.IsNullOrEmpty(gameObjectId)
                    || !meshFiltersByGameObjectId.TryGetValue(gameObjectId, out var meshFilterBindings))
                {
                    continue;
                }

                var rendererId = GetSemanticAssetId(renderer);
                if (string.IsNullOrEmpty(rendererId))
                {
                    continue;
                }

                foreach (var binding in meshFilterBindings)
                {
                    if (string.IsNullOrEmpty(binding.MeshId))
                    {
                        continue;
                    }

                    var key = $"{rendererId}\u001f{binding.MeshId}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    result.Add(new RendererMeshSemanticBinding(
                        renderer,
                        rendererId,
                        "MeshRenderer",
                        binding.Mesh,
                        binding.MeshId,
                        binding.GameObject ?? gameObject,
                        binding.MeshFilter.m_Mesh.m_FileID,
                        binding.MeshFilter.m_Mesh.m_PathID,
                        100,
                        "MeshRenderer GameObject MeshFilter"));
                }
            }

            return result;
        }

        private static void AddSceneObjectModelGroupRelations(
            SerializedFile sourceFile,
            SemanticAssetRelations relations,
            IEnumerable<RendererMeshSemanticBinding> rendererBindings)
        {
            var bindings = rendererBindings
                .Where(binding => binding.GameObject != null
                    && !string.IsNullOrEmpty(binding.MeshId)
                    && !string.IsNullOrEmpty(binding.RendererId))
                .ToList();
            if (bindings.Count < 2)
            {
                return;
            }

            var ancestryByGameObjectId = new Dictionary<string, List<GameObjectHierarchyEntry>>(StringComparer.Ordinal);
            var rendererCountByAncestorId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var ancestry = GetGameObjectAncestry(sourceFile, binding.GameObject!);
                if (ancestry.Count == 0)
                {
                    continue;
                }

                var ownId = ancestry[0].GameObjectId;
                ancestryByGameObjectId[ownId] = ancestry;
                foreach (var ancestor in ancestry)
                {
                    rendererCountByAncestorId.TryGetValue(ancestor.GameObjectId, out var count);
                    rendererCountByAncestorId[ancestor.GameObjectId] = count + 1;
                }
            }

            var groups = new Dictionary<string, SceneObjectModelGroupBuilder>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var ownId = binding.GameObject != null ? GetSemanticAssetId(binding.GameObject) : string.Empty;
                if (string.IsNullOrEmpty(ownId)
                    || !ancestryByGameObjectId.TryGetValue(ownId, out var ancestry))
                {
                    continue;
                }

                var groupRoot = ancestry.FirstOrDefault(entry =>
                    rendererCountByAncestorId.TryGetValue(entry.GameObjectId, out var count) && count > 1);
                if (groupRoot.GameObject == null)
                {
                    continue;
                }

                if (!groups.TryGetValue(groupRoot.GameObjectId, out var group))
                {
                    group = new SceneObjectModelGroupBuilder(groupRoot);
                    groups[groupRoot.GameObjectId] = group;
                }

                group.Add(binding);
            }

            foreach (var group in groups.Values)
            {
                if (group.Bindings.Count < 2)
                {
                    continue;
                }

                var groupId = $"modelgroup:sceneobject:{group.Root.GameObjectId}";
                relations.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId,
                    "SceneObject",
                    group.Root.Name,
                    group.Root.GameObjectId,
                    group.Root.Name,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    sourceFile.fileName ?? string.Empty,
                    group.Confidence,
                    group.ConfidenceReason));

                var slotIndex = 0;
                var seenParts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in group.Bindings)
                {
                    var partKey = $"{binding.MeshId}\u001f{binding.RendererId}";
                    if (!seenParts.Add(partKey))
                    {
                        continue;
                    }

                    var gameObjectId = binding.GameObject != null
                        ? GetSemanticAssetId(binding.GameObject)
                        : string.Empty;
                    relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                        groupId,
                        binding.MeshId,
                        binding.RendererId,
                        binding.RendererType,
                        gameObjectId,
                        binding.GameObject?.m_Name ?? string.Empty,
                        slotIndex,
                        BuildLocalToGroupRootMatrix(sourceFile, binding.GameObject, group.Root.GameObjectId),
                        binding.Confidence,
                        binding.ConfidenceReason));
                    slotIndex++;
                }
            }
        }

        private static List<GameObjectHierarchyEntry> GetGameObjectAncestry(SerializedFile sourceFile, GameObject gameObject)
        {
            var result = new List<GameObjectHierarchyEntry>();
            var visited = new HashSet<long>();
            GameObject? current = gameObject;
            for (var depth = 0; current != null && depth < 256; depth++)
            {
                if (!visited.Add(current.m_PathID))
                {
                    break;
                }

                var currentId = GetSemanticAssetId(current);
                if (string.IsNullOrEmpty(currentId))
                {
                    break;
                }

                result.Add(new GameObjectHierarchyEntry(current, currentId, current.m_Name ?? string.Empty, depth));

                var transform = ResolveTransformForGameObjectBackground(sourceFile, current);
                if (transform?.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                var parentTransform = ResolveTransformBackground(sourceFile, transform.m_Father);
                if (parentTransform == null)
                {
                    break;
                }

                current = ResolveGameObjectBackground(sourceFile, parentTransform.m_GameObject);
            }

            return result;
        }

        public static bool IsSameOrDescendantGameObject(SerializedFile sourceFile, GameObject child, GameObject ancestor)
        {
            var ancestorPathId = ancestor.m_PathID;
            GameObject? current = child;
            var visited = new HashSet<long>();

            for (var depth = 0; current != null && depth < 256; depth++)
            {
                if (!visited.Add(current.m_PathID))
                {
                    return false;
                }

                if (current.m_PathID == ancestorPathId)
                {
                    return true;
                }

                var transform = ResolveTransformForGameObjectBackground(sourceFile, current);
                if (transform?.m_Father == null || transform.m_Father.IsNull)
                {
                    return false;
                }

                var parentTransform = ResolveTransformBackground(sourceFile, transform.m_Father);
                if (parentTransform == null)
                {
                    return false;
                }

                current = ResolveGameObjectBackground(sourceFile, parentTransform.m_GameObject);
            }

            return false;
        }

        public static float[]? BuildLocalToWorldMatrix(SerializedFile sourceFile, GameObject? gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var transforms = new List<Transform>();
            var visited = new HashSet<long>();
            var transform = ResolveTransformForGameObjectBackground(sourceFile, gameObject);
            for (var depth = 0; transform != null && depth < 256; depth++)
            {
                if (!visited.Add(transform.m_PathID))
                {
                    break;
                }

                transforms.Add(transform);
                if (transform.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                transform = ResolveTransformBackground(sourceFile, transform.m_Father);
            }

            if (transforms.Count == 0)
            {
                return null;
            }

            var world = CreateIdentityMatrix();
            for (var i = transforms.Count - 1; i >= 0; i--)
            {
                world = world * BuildLocalMatrix(transforms[i]);
            }

            return MatrixToRowMajorArray(world, omitIdentity: true);
        }

        public static float[]? BuildLocalToGroupRootMatrix(
            SerializedFile sourceFile,
            GameObject? gameObject,
            string rootGameObjectId)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(rootGameObjectId))
            {
                return BuildLocalToWorldMatrix(sourceFile, gameObject);
            }

            var transforms = new List<Transform>();
            var visited = new HashSet<long>();
            var transform = ResolveTransformForGameObjectBackground(sourceFile, gameObject);
            var reachedGroupRoot = false;
            for (var depth = 0; transform != null && depth < 256; depth++)
            {
                if (!visited.Add(transform.m_PathID))
                {
                    break;
                }

                var transformGameObject = ResolveGameObjectBackground(sourceFile, transform.m_GameObject);
                var transformGameObjectId = transformGameObject != null
                    ? GetSemanticAssetId(transformGameObject)
                    : string.Empty;
                if (string.Equals(transformGameObjectId, rootGameObjectId, StringComparison.Ordinal))
                {
                    reachedGroupRoot = true;
                    break;
                }

                transforms.Add(transform);
                if (transform.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                transform = ResolveTransformBackground(sourceFile, transform.m_Father);
            }

            if (!reachedGroupRoot)
            {
                return BuildLocalToWorldMatrix(sourceFile, gameObject);
            }

            var localToRoot = CreateIdentityMatrix();
            for (var i = transforms.Count - 1; i >= 0; i--)
            {
                localToRoot = localToRoot * BuildLocalMatrix(transforms[i]);
            }

            return MatrixToRowMajorArray(localToRoot, omitIdentity: true);
        }

        private static Matrix4x4 BuildLocalMatrix(Transform transform)
        {
            return Matrix4x4.Translate(transform.m_LocalPosition)
                * Matrix4x4.Rotate(transform.m_LocalRotation)
                * Matrix4x4.Scale(transform.m_LocalScale);
        }

        private static Matrix4x4 CreateIdentityMatrix()
        {
            var matrix = new Matrix4x4();
            matrix.M00 = 1f;
            matrix.M11 = 1f;
            matrix.M22 = 1f;
            matrix.M33 = 1f;
            return matrix;
        }

        public static float[]? MatrixToRowMajorArray(Matrix4x4 matrix, bool omitIdentity)
        {
            var values = new[]
            {
                matrix.M00, matrix.M01, matrix.M02, matrix.M03,
                matrix.M10, matrix.M11, matrix.M12, matrix.M13,
                matrix.M20, matrix.M21, matrix.M22, matrix.M23,
                matrix.M30, matrix.M31, matrix.M32, matrix.M33
            };

            if (!omitIdentity)
            {
                return values;
            }

            var identity = new[]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f
            };
            for (var i = 0; i < values.Length; i++)
            {
                if (Math.Abs(values[i] - identity[i]) > 0.000001f)
                {
                    return values;
                }
            }

            return null;
        }

        // --- Animation Relations ---

        private static void AddAnimatorAvatarMeshRelations(
            SerializedFile sourceFile,
            Animator animator,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            int animatorCount,
            SemanticAssetRelations relations)
        {
            if (animator == null)
            {
                return;
            }

            var avatarId = string.Empty;
            var avatarFileId = 0;
            var avatarPathId = 0L;
            if (animator.m_Avatar != null && !animator.m_Avatar.IsNull)
            {
                var avatar = ResolveAvatarBackground(sourceFile, animator.m_Avatar);
                avatarId = GetSemanticAssetId(avatar);
                if (string.IsNullOrEmpty(avatarId))
                {
                    avatarId = GetSemanticAssetIdFromPPtr(sourceFile, animator.m_Avatar, ClassIDType.Avatar);
                }

                avatarFileId = animator.m_Avatar.m_FileID;
                avatarPathId = animator.m_Avatar.m_PathID;

                if (!string.IsNullOrEmpty(avatarId))
                {
                    AddAssetEdge(
                        relations,
                        animator,
                        "Avatar",
                        "m_Avatar",
                        0,
                        avatarId,
                        avatarFileId,
                        avatarPathId);
                }
            }

            var animatorGameObject = ResolveGameObjectBackground(sourceFile, animator.m_GameObject);
            var animatorId = GetSemanticAssetId(animator);
            var rootGameObjectId = animatorGameObject != null
                ? GetSemanticGameObjectId(sourceFile, animator.m_GameObject, animatorGameObject)
                : string.Empty;
            var controller = ResolveRuntimeAnimatorControllerBackground(sourceFile, animator.m_Controller);
            var controllerId = GetSemanticAssetId(controller);
            if (string.IsNullOrEmpty(controllerId) && animator.m_Controller != null && !animator.m_Controller.IsNull)
            {
                controllerId = GetSemanticAssetIdFromPPtr(sourceFile, animator.m_Controller, ClassIDType.RuntimeAnimatorController);
            }

            if (!string.IsNullOrEmpty(controllerId) && animator.m_Controller != null && !animator.m_Controller.IsNull)
            {
                AddAssetEdge(
                    relations,
                    animator,
                    "AnimatorController",
                    "m_Controller",
                    0,
                    controllerId,
                    animator.m_Controller.m_FileID,
                    animator.m_Controller.m_PathID);
            }

            var meshMatches = FindSkinnedMeshesForAnimator(sourceFile, animatorGameObject, skinnedMeshBindings, animatorCount).ToList();
            var groupId = AddAnimatorModelGroupRelations(
                sourceFile,
                relations,
                animator,
                animatorId,
                animatorGameObject,
                rootGameObjectId,
                avatarId,
                controllerId,
                meshMatches);

            var meshSlotIndex = 0;
            var primaryMeshId = string.Empty;
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
                if (string.IsNullOrEmpty(binding.MeshId) || !seenMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(primaryMeshId))
                {
                    primaryMeshId = binding.MeshId;
                }

                AddAssetEdge(
                    relations,
                    animator,
                    "Mesh",
                    "AnimatorMesh",
                    meshSlotIndex,
                    binding.MeshId,
                    binding.MeshFileId,
                    binding.MeshPathId);

                if (!string.IsNullOrEmpty(avatarId))
                {
                    AddAssetEdgeById(
                        relations,
                        avatarId,
                        "Mesh",
                        "AnimatorMesh",
                        meshSlotIndex,
                        binding.MeshId,
                        avatarFileId,
                        avatarPathId,
                        binding.MeshFileId,
                        binding.MeshPathId,
                        isResolved: true);
                }

                meshSlotIndex++;
            }

            if (controller != null)
            {
                AddAnimatorControllerClipRelations(
                    sourceFile,
                    relations,
                    controller,
                    animator,
                    animatorId,
                    avatarId,
                    avatarFileId,
                    avatarPathId,
                    primaryMeshId,
                    groupId,
                    depth: 0);
            }
        }

        private static string AddAnimatorModelGroupRelations(
            SerializedFile sourceFile,
            SemanticAssetRelations relations,
            Animator animator,
            string animatorId,
            GameObject? animatorGameObject,
            string rootGameObjectId,
            string avatarId,
            string controllerId,
            IReadOnlyList<SkinnedMeshSemanticMatch> meshMatches)
        {
            if (meshMatches.Count == 0)
            {
                return string.Empty;
            }

            var groupId = BuildAnimatorModelGroupId(sourceFile, animator, animatorId, rootGameObjectId, avatarId, controllerId);
            if (string.IsNullOrEmpty(groupId))
            {
                return string.Empty;
            }

            var groupName = !string.IsNullOrWhiteSpace(animatorGameObject?.m_Name)
                ? animatorGameObject!.m_Name
                : "Animator Model";
            var confidence = meshMatches.Min(match => match.Confidence);
            var confidenceReason = meshMatches.All(match => match.ConfidenceReason == meshMatches[0].ConfidenceReason)
                ? meshMatches[0].ConfidenceReason
                : "Mixed animator mesh evidence";

            relations.ModelGroups.Add(new SemanticModelGroupRelation(
                groupId,
                "Animator",
                groupName,
                rootGameObjectId,
                animatorGameObject?.m_Name ?? string.Empty,
                animatorId,
                avatarId,
                controllerId,
                sourceFile.fileName ?? string.Empty,
                confidence,
                confidenceReason));

            var slotIndex = 0;
            var seenParts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
                var rendererId = GetSemanticAssetId(binding.Renderer);
                if (string.IsNullOrEmpty(binding.MeshId) || string.IsNullOrEmpty(rendererId))
                {
                    continue;
                }

                var partKey = $"{binding.MeshId}\u001f{rendererId}";
                if (!seenParts.Add(partKey))
                {
                    continue;
                }

                var gameObjectId = binding.GameObject != null
                    ? GetSemanticAssetId(binding.GameObject)
                    : string.Empty;
                relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId,
                    binding.MeshId,
                    rendererId,
                    "SkinnedMeshRenderer",
                    gameObjectId,
                    binding.GameObject?.m_Name ?? string.Empty,
                    slotIndex,
                    BuildLocalToGroupRootMatrix(sourceFile, binding.GameObject, rootGameObjectId),
                    match.Confidence,
                    match.ConfidenceReason));
                slotIndex++;
            }

            return groupId;
        }

        public static string BuildAnimatorModelGroupId(
            SerializedFile sourceFile,
            Animator animator,
            string animatorId,
            string rootGameObjectId,
            string avatarId,
            string controllerId)
        {
            if (!string.IsNullOrEmpty(animatorId))
            {
                return $"modelgroup:animator:{animatorId}";
            }

            if (!string.IsNullOrEmpty(rootGameObjectId))
            {
                return $"modelgroup:root:{rootGameObjectId}";
            }

            var fallbackId = !string.IsNullOrEmpty(avatarId)
                ? avatarId
                : !string.IsNullOrEmpty(controllerId)
                    ? controllerId
                    : sourceFile.fileName ?? string.Empty;
            return string.IsNullOrEmpty(fallbackId)
                ? string.Empty
                : $"modelgroup:fallback:{fallbackId}:{animator.m_PathID}";
        }

        private static IEnumerable<SkinnedMeshSemanticMatch> FindSkinnedMeshesForAnimator(
            SerializedFile sourceFile,
            GameObject? animatorGameObject,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            int animatorCount)
        {
            if (skinnedMeshBindings.Count == 0)
            {
                yield break;
            }

            if (animatorGameObject != null)
            {
                var matchedAny = false;
                foreach (var binding in skinnedMeshBindings)
                {
                    if (binding.GameObject != null
                        && IsSameOrDescendantGameObject(sourceFile, binding.GameObject, animatorGameObject))
                    {
                        matchedAny = true;
                        yield return new SkinnedMeshSemanticMatch(binding, 100, "Animator hierarchy descendant");
                    }
                }

                if (matchedAny)
                {
                    yield break;
                }
            }

            if (animatorCount == 1)
            {
                foreach (var binding in skinnedMeshBindings)
                {
                    yield return new SkinnedMeshSemanticMatch(binding, 20, "Single animator file fallback");
                }
            }
        }

        private static void AddAnimatorControllerClipRelations(
            SerializedFile fallbackSourceFile,
            SemanticAssetRelations relations,
            RuntimeAnimatorController controller,
            Animator animator,
            string animatorId,
            string avatarId,
            int avatarFileId,
            long avatarPathId,
            string primaryMeshId,
            string groupId,
            int depth)
        {
            if (depth > 4 || controller == null)
            {
                return;
            }

            var controllerSourceFile = controller.assetsFile ?? fallbackSourceFile;
            var controllerId = GetSemanticAssetId(controller);
            if (string.IsNullOrEmpty(controllerId))
            {
                controllerId = GetSemanticAssetIdFromPPtr(fallbackSourceFile, animator.m_Controller, ClassIDType.RuntimeAnimatorController);
            }

            var relationKind = controller is AnimatorOverrideController
                ? "AnimatorOverrideController"
                : "AnimatorController";

            var slotIndex = 0;
            foreach (var clipRef in EnumerateAnimationClipPointers(controller))
            {
                if (clipRef == null || clipRef.IsNull)
                {
                    slotIndex++;
                    continue;
                }

                var clipId = GetSemanticAssetIdFromPPtr(controllerSourceFile, clipRef, ClassIDType.AnimationClip);
                if (string.IsNullOrEmpty(clipId))
                {
                    slotIndex++;
                    continue;
                }

                AddAssetEdge(
                    relations,
                    animator,
                    "AnimationClip",
                    "AnimatorControllerClip",
                    slotIndex,
                    clipId,
                    clipRef.m_FileID,
                    clipRef.m_PathID);

                if (!string.IsNullOrEmpty(controllerId))
                {
                    AddAssetEdgeById(
                        relations,
                        controllerId,
                        "AnimationClip",
                        "m_AnimationClips",
                        slotIndex,
                        clipId,
                        0,
                        controller.m_PathID,
                        clipRef.m_FileID,
                        clipRef.m_PathID,
                        isResolved: true);
                }

                if (!string.IsNullOrEmpty(avatarId))
                {
                    AddAssetEdgeById(
                        relations,
                        clipId,
                        "Avatar",
                        "AnimatorAvatar",
                        slotIndex,
                        avatarId,
                        clipRef.m_FileID,
                        clipRef.m_PathID,
                        avatarFileId,
                        avatarPathId,
                        isResolved: true);
                }

                if (!string.IsNullOrEmpty(avatarId)
                    || !string.IsNullOrEmpty(primaryMeshId)
                    || !string.IsNullOrEmpty(groupId)
                    || !string.IsNullOrEmpty(animatorId))
                {
                    relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        animatorId,
                        avatarId,
                        primaryMeshId,
                        groupId,
                        relationKind,
                        10000 - Math.Min(slotIndex, 100) - (depth * 100)));
                }

                slotIndex++;
            }

            if (controller is AnimatorOverrideController overrideController
                && overrideController.m_Controller != null
                && !overrideController.m_Controller.IsNull)
            {
                var baseControllerId = GetSemanticAssetIdFromPPtr(controllerSourceFile, overrideController.m_Controller, ClassIDType.RuntimeAnimatorController);
                if (!string.IsNullOrEmpty(controllerId) && !string.IsNullOrEmpty(baseControllerId))
                {
                    AddAssetEdgeById(
                        relations,
                        controllerId,
                        "AnimatorController",
                        "m_Controller",
                        0,
                        baseControllerId,
                        0,
                        overrideController.m_PathID,
                        overrideController.m_Controller.m_FileID,
                        overrideController.m_Controller.m_PathID,
                        isResolved: true);
                }

                var baseController = ResolveRuntimeAnimatorControllerBackground(controllerSourceFile, overrideController.m_Controller);
                if (baseController != null && !ReferenceEquals(baseController, controller))
                {
                    AddAnimatorControllerClipRelations(
                        controllerSourceFile,
                        relations,
                        baseController,
                        animator,
                        animatorId,
                        avatarId,
                        avatarFileId,
                        avatarPathId,
                        primaryMeshId,
                        groupId,
                        depth + 1);
                }
            }
        }

        private static void IndexRuntimeAnimatorControllerRelations(
            SerializedFile sourceFile,
            RuntimeAnimatorController controller,
            SemanticAssetRelations relations)
        {
            if (controller == null)
            {
                return;
            }

            var controllerId = GetSemanticAssetId(controller);
            if (string.IsNullOrEmpty(controllerId))
            {
                return;
            }

            var slotIndex = 0;
            foreach (var clipRef in EnumerateAnimationClipPointers(controller))
            {
                if (clipRef == null || clipRef.IsNull)
                {
                    slotIndex++;
                    continue;
                }

                var clipId = GetSemanticAssetIdFromPPtr(sourceFile, clipRef, ClassIDType.AnimationClip);
                if (!string.IsNullOrEmpty(clipId))
                {
                    AddAssetEdge(
                        relations,
                        controller,
                        "AnimationClip",
                        "m_AnimationClips",
                        slotIndex,
                        clipId,
                        clipRef.m_FileID,
                        clipRef.m_PathID);
                }

                slotIndex++;
            }

            if (controller is AnimatorOverrideController overrideController
                && overrideController.m_Controller != null
                && !overrideController.m_Controller.IsNull)
            {
                var baseControllerId = GetSemanticAssetIdFromPPtr(sourceFile, overrideController.m_Controller, ClassIDType.RuntimeAnimatorController);
                if (!string.IsNullOrEmpty(baseControllerId))
                {
                    AddAssetEdge(
                        relations,
                        overrideController,
                        "AnimatorController",
                        "m_Controller",
                        0,
                        baseControllerId,
                        overrideController.m_Controller.m_FileID,
                        overrideController.m_Controller.m_PathID);
                }
            }
        }

        private static void AddLegacyAnimationRelations(
            SerializedFile sourceFile,
            Animation legacyAnimation,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            int animatorCount,
            SemanticAssetRelations relations)
        {
            if (legacyAnimation?.m_Animations == null || legacyAnimation.m_Animations.Length == 0)
            {
                return;
            }

            var animGameObject = ResolveGameObjectBackground(sourceFile, legacyAnimation.m_GameObject);
            var animationComponentId = GetSemanticAssetId(legacyAnimation);
            var rootGameObjectId = animGameObject != null
                ? GetSemanticGameObjectId(sourceFile, legacyAnimation.m_GameObject, animGameObject)
                : string.Empty;

            var meshMatches = FindSkinnedMeshesForAnimator(sourceFile, animGameObject, skinnedMeshBindings, Math.Max(1, animatorCount)).ToList();
            var primaryMeshId = string.Empty;
            var meshSlotIndex = 0;
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
                if (string.IsNullOrEmpty(binding.MeshId) || !seenMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(primaryMeshId))
                {
                    primaryMeshId = binding.MeshId;
                }

                AddAssetEdge(
                    relations,
                    legacyAnimation,
                    "Mesh",
                    "AnimatorMesh",
                    meshSlotIndex,
                    binding.MeshId,
                    binding.MeshFileId,
                    binding.MeshPathId);
                meshSlotIndex++;
            }

            var groupId = !string.IsNullOrEmpty(rootGameObjectId) && meshMatches.Count > 1
                ? $"modelgroup:sceneobject:{rootGameObjectId}"
                : string.Empty;

            var slotIndex = 0;
            foreach (var clipRef in legacyAnimation.m_Animations)
            {
                if (clipRef == null || clipRef.IsNull)
                {
                    slotIndex++;
                    continue;
                }

                var clipId = GetSemanticAssetIdFromPPtr(sourceFile, clipRef, ClassIDType.AnimationClip);
                if (string.IsNullOrEmpty(clipId))
                {
                    slotIndex++;
                    continue;
                }

                AddAssetEdge(
                    relations,
                    legacyAnimation,
                    "AnimationClip",
                    "LegacyAnimationClip",
                    slotIndex,
                    clipId,
                    clipRef.m_FileID,
                    clipRef.m_PathID);

                if (!string.IsNullOrEmpty(primaryMeshId) || !string.IsNullOrEmpty(groupId) || !string.IsNullOrEmpty(animationComponentId))
                {
                    relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        animationComponentId,
                        string.Empty,
                        primaryMeshId,
                        groupId,
                        "LegacyAnimation",
                        9700 - Math.Min(slotIndex, 100)));
                }

                slotIndex++;
            }
        }

        private static void AddAvatarRigRelations(
            SerializedFile sourceFile,
            Avatar avatar,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            AssetStudio.Object[] objectsSnapshot,
            SemanticAssetRelations relations)
        {
            if (avatar == null)
            {
                return;
            }

            var avatarId = GetSemanticAssetId(avatar);
            if (string.IsNullOrEmpty(avatarId))
            {
                return;
            }

            var rigHashes = ExtractAvatarRigBoneHashes(avatar);
            var avatarCountInFile = objectsSnapshot.OfType<Avatar>().Count();
            var primaryMeshId = string.Empty;
            var groupId = string.Empty;
            var animatorId = string.Empty;

            foreach (var existingGroup in relations.ModelGroups)
            {
                if (string.Equals(existingGroup.AvatarAssetId, avatarId, StringComparison.Ordinal))
                {
                    groupId = existingGroup.GroupId;
                    animatorId = existingGroup.AnimatorAssetId;
                    break;
                }
            }

            if (skinnedMeshBindings.Count > 0)
            {
                var matchedBindings = new List<SkinnedMeshSemanticBinding>();
                foreach (var binding in skinnedMeshBindings)
                {
                    if (string.IsNullOrEmpty(binding.MeshId))
                    {
                        continue;
                    }

                    if (avatarCountInFile == 1 || DoesSkinnedMeshMatchAvatarBones(sourceFile, binding, avatar, rigHashes))
                    {
                        matchedBindings.Add(binding);
                    }
                }

                var slotIndex = 0;
                var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in matchedBindings)
                {
                    if (!seenMeshes.Add(binding.MeshId))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(primaryMeshId))
                    {
                        primaryMeshId = binding.MeshId;
                    }

                    AddAssetEdge(
                        relations,
                        avatar,
                        "Mesh",
                        "AvatarMesh",
                        slotIndex,
                        binding.MeshId,
                        binding.MeshFileId,
                        binding.MeshPathId);
                    slotIndex++;
                }

                if (string.IsNullOrEmpty(groupId) && matchedBindings.Count >= 2)
                {
                    groupId = $"modelgroup:avatar:{avatarId}";
                    var groupName = !string.IsNullOrWhiteSpace(avatar.m_Name)
                        ? avatar.m_Name.Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
                        : "Avatar Model";
                    if (string.IsNullOrWhiteSpace(groupName))
                    {
                        groupName = avatar.m_Name ?? "Avatar Model";
                    }

                    relations.ModelGroups.Add(new SemanticModelGroupRelation(
                        groupId,
                        "Animator",
                        groupName,
                        string.Empty,
                        groupName,
                        string.Empty,
                        avatarId,
                        string.Empty,
                        sourceFile.fileName ?? string.Empty,
                        85,
                        "Avatar skinned mesh co-location"));

                    var partSlot = 0;
                    var seenParts = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var binding in matchedBindings)
                    {
                        var rendererId = GetSemanticAssetId(binding.Renderer);
                        if (string.IsNullOrEmpty(rendererId) || !seenParts.Add($"{binding.MeshId}\u001f{rendererId}"))
                        {
                            continue;
                        }

                        var goId = binding.GameObject != null ? GetSemanticAssetId(binding.GameObject) : string.Empty;
                        relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                            groupId,
                            binding.MeshId,
                            rendererId,
                            "SkinnedMeshRenderer",
                            goId,
                            binding.GameObject?.m_Name ?? string.Empty,
                            partSlot++,
                            BuildLocalToWorldMatrix(sourceFile, binding.GameObject),
                            85,
                            "Avatar skinned mesh co-location"));
                    }
                }
            }

            if (string.IsNullOrEmpty(primaryMeshId))
            {
                foreach (var mesh in objectsSnapshot.OfType<Mesh>())
                {
                    if (mesh.m_BoneNameHashes == null || mesh.m_BoneNameHashes.Length == 0)
                    {
                        continue;
                    }

                    var overlap = mesh.m_BoneNameHashes.Count(rigHashes.Contains);
                    if (IsStrongMeshAvatarMatch(mesh.m_BoneNameHashes.Length, overlap))
                    {
                        var meshId = GetSemanticAssetId(mesh);
                        if (!string.IsNullOrEmpty(meshId))
                        {
                            if (string.IsNullOrEmpty(primaryMeshId))
                            {
                                primaryMeshId = meshId;
                            }

                            AddAssetEdge(
                                relations,
                                avatar,
                                "Mesh",
                                "AvatarMesh",
                                0,
                                meshId,
                                0,
                                mesh.m_PathID);
                        }
                    }
                }
            }

            var emittedCount = 0;
            foreach (var hash in rigHashes)
            {
                if (hash == 0)
                {
                    continue;
                }

                relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                    avatarId,
                    animatorId,
                    primaryMeshId,
                    groupId,
                    hash));

                emittedCount++;
                if (emittedCount >= MaxRigBoneHashProbes)
                {
                    break;
                }
            }
        }

        private static bool DoesSkinnedMeshMatchAvatarBones(
            SerializedFile sourceFile,
            SkinnedMeshSemanticBinding binding,
            Avatar avatar,
            HashSet<uint> avatarBoneHashes)
        {
            if (binding.Mesh?.m_BoneNameHashes != null && binding.Mesh.m_BoneNameHashes.Length > 0)
            {
                var overlap = binding.Mesh.m_BoneNameHashes.Count(avatarBoneHashes.Contains);
                if (IsStrongMeshAvatarMatch(binding.Mesh.m_BoneNameHashes.Length, overlap))
                {
                    return true;
                }
            }

            if (binding.Renderer?.m_Bones != null && binding.Renderer.m_Bones.Length > 0 && avatarBoneHashes.Count > 0)
            {
                var matched = 0;
                var checkedBones = 0;
                var step = Math.Max(1, binding.Renderer.m_Bones.Length / 16);
                for (var i = 0; i < binding.Renderer.m_Bones.Length; i += step)
                {
                    var boneTransform = ResolveTransformBackground(sourceFile, binding.Renderer.m_Bones[i]);
                    var boneGo = boneTransform != null ? ResolveGameObjectBackground(sourceFile, boneTransform.m_GameObject) : null;
                    if (!string.IsNullOrWhiteSpace(boneGo?.m_Name))
                    {
                        checkedBones++;
                        var leafHash = ComputeUnityBonePathHash(boneGo!.m_Name);
                        if (leafHash != 0 && avatarBoneHashes.Contains(leafHash))
                        {
                            matched++;
                        }
                    }
                }

                if (checkedBones > 0 && matched >= Math.Max(2, checkedBones / 3))
                {
                    return true;
                }
            }

            var avatarBaseName = (avatar.m_Name ?? string.Empty).Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            var goName = binding.GameObject?.m_Name ?? string.Empty;
            return avatarBaseName.Length >= 3
                && goName.Length >= 3
                && (goName.Contains(avatarBaseName, StringComparison.OrdinalIgnoreCase)
                    || avatarBaseName.Contains(goName, StringComparison.OrdinalIgnoreCase));
        }

        public static HashSet<uint> ExtractAvatarRigBoneHashes(Avatar avatar)
        {
            var hashes = new HashSet<uint>();
            if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null)
            {
                foreach (var id in avatar.m_Avatar.m_AvatarSkeleton.m_ID)
                {
                    if (id != 0)
                    {
                        hashes.Add(id);
                    }
                }
            }

            if (avatar.m_TOS != null)
            {
                foreach (var pair in avatar.m_TOS)
                {
                    if (pair.Key != 0)
                    {
                        hashes.Add(pair.Key);
                    }

                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        var leafName = GetLeafBoneName(pair.Value);
                        if (!string.IsNullOrEmpty(leafName))
                        {
                            var leafHash = ComputeUnityBonePathHash(leafName);
                            if (leafHash != 0)
                            {
                                hashes.Add(leafHash);
                            }
                        }
                    }
                }
            }

            return hashes;
        }

        private static void AddAnimationClipFingerprintRelations(
            SerializedFile sourceFile,
            AnimationClip clip,
            SemanticAssetRelations relations)
        {
            if (clip == null)
            {
                return;
            }

            if (clip.m_PPtrCurves != null && clip.m_PPtrCurves.Length > 0)
            {
                return;
            }

            var clipId = GetSemanticAssetId(clip);
            if (string.IsNullOrEmpty(clipId))
            {
                clipId = AssetHandle.BuildUniqueID(sourceFile, clip.m_PathID);
            }

            if (string.IsNullOrEmpty(clipId))
            {
                return;
            }

            var bindingPaths = GetTransformBindingPathsBackground(clip);
            if (bindingPaths.Count == 0)
            {
                return;
            }

            var probes = SelectDeterministicBoneHashProbes(bindingPaths, MaxClipBoneHashProbes);
            foreach (var probe in probes)
            {
                relations.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(
                    clipId,
                    probe,
                    bindingPaths.Count));
            }
        }

        public static uint[] SelectDeterministicBoneHashProbes(IReadOnlyCollection<uint> boneHashes, int maxProbes = MaxClipBoneHashProbes)
        {
            if (boneHashes == null || boneHashes.Count == 0 || maxProbes <= 0)
            {
                return Array.Empty<uint>();
            }

            var sorted = boneHashes.Where(h => h != 0).Distinct().OrderBy(h => h).ToArray();
            if (sorted.Length <= maxProbes)
            {
                return sorted;
            }

            var result = new uint[maxProbes];
            for (var i = 0; i < maxProbes; i++)
            {
                var idx = (int)((long)i * (sorted.Length - 1) / Math.Max(1, maxProbes - 1));
                result[i] = sorted[idx];
            }

            return result;
        }

        private static void AddSkinnedMeshFallbackBoneHashes(
            SerializedFile sourceFile,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            SemanticAssetRelations relations)
        {
            if (skinnedMeshBindings.Count == 0)
            {
                return;
            }

            var coveredMeshes = new HashSet<string>(
                relations.RigBoneHashes
                    .Where(r => !string.IsNullOrEmpty(r.MeshAssetId))
                    .Select(r => r.MeshAssetId),
                StringComparer.Ordinal);

            foreach (var binding in skinnedMeshBindings)
            {
                if (string.IsNullOrEmpty(binding.MeshId) || !coveredMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                var boneHashes = new HashSet<uint>();
                if (binding.Mesh?.m_BoneNameHashes != null)
                {
                    foreach (var hash in binding.Mesh.m_BoneNameHashes)
                    {
                        if (hash != 0)
                        {
                            boneHashes.Add(hash);
                        }
                    }
                }

                if (binding.Renderer?.m_Bones != null && binding.Renderer.m_Bones.Length > 0)
                {
                    var maxBonesToInspect = Math.Min(binding.Renderer.m_Bones.Length, 48);
                    for (var i = 0; i < maxBonesToInspect; i++)
                    {
                        var transform = ResolveTransformBackground(sourceFile, binding.Renderer.m_Bones[i]);
                        var go = transform != null ? ResolveGameObjectBackground(sourceFile, transform.m_GameObject) : null;
                        if (!string.IsNullOrWhiteSpace(go?.m_Name))
                        {
                            var hash = ComputeUnityBonePathHash(go!.m_Name);
                            if (hash != 0)
                            {
                                boneHashes.Add(hash);
                            }
                        }
                    }
                }

                var count = 0;
                foreach (var hash in boneHashes)
                {
                    relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                        string.Empty,
                        string.Empty,
                        binding.MeshId,
                        string.Empty,
                        hash));
                    count++;
                    if (count >= 48)
                    {
                        break;
                    }
                }
            }
        }

        private static void AddContainerAnimationRelations(
            SemanticAssetRelations relations,
            SerializedFile sourceFile,
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences)
        {
            if (containerReferences.Count == 0)
            {
                return;
            }

            foreach (var container in containerReferences)
            {
                if (string.IsNullOrWhiteSpace(container.Container))
                {
                    continue;
                }

                var clipIds = new List<string>();
                var avatarIds = new List<string>();
                var animatorIds = new List<string>();
                var meshIds = new List<string>();

                foreach (var reference in container.References)
                {
                    var handle = GetSemanticHandleFromPPtr(sourceFile, reference);
                    if (handle == null || string.IsNullOrWhiteSpace(handle.UniqueID))
                    {
                        continue;
                    }

                    switch (handle.Type)
                    {
                        case ClassIDType.AnimationClip:
                            if (!clipIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                clipIds.Add(handle.UniqueID);
                            }
                            break;
                        case ClassIDType.Avatar:
                            if (!avatarIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                avatarIds.Add(handle.UniqueID);
                            }
                            break;
                        case ClassIDType.Animator:
                            if (!animatorIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                animatorIds.Add(handle.UniqueID);
                            }
                            break;
                        case ClassIDType.Mesh:
                            if (!meshIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                meshIds.Add(handle.UniqueID);
                            }
                            break;
                    }
                }

                if (clipIds.Count == 0 || (avatarIds.Count == 0 && meshIds.Count == 0 && animatorIds.Count == 0))
                {
                    continue;
                }

                var primaryAvatarId = avatarIds.FirstOrDefault() ?? string.Empty;
                var primaryAnimatorId = animatorIds.FirstOrDefault() ?? string.Empty;
                var primaryMeshId = meshIds.FirstOrDefault() ?? string.Empty;
                foreach (var clipId in clipIds)
                {
                    relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        primaryAnimatorId,
                        primaryAvatarId,
                        primaryMeshId,
                        string.Empty,
                        "Container",
                        6000));
                }
            }
        }

        public static uint ComputeUnityBonePathHash(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            var normalized = path.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0)
            {
                return 0;
            }

            var table = SevenZip.CRC.Table;
            uint crc = 0xFFFFFFFFu;
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(normalized);
            if (byteCount <= 256)
            {
                Span<byte> buffer = stackalloc byte[byteCount];
                System.Text.Encoding.UTF8.GetBytes(normalized.AsSpan(), buffer);
                for (var i = 0; i < buffer.Length; i++)
                {
                    crc = table[((byte)crc) ^ buffer[i]] ^ (crc >> 8);
                }
            }
            else
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
                for (var i = 0; i < bytes.Length; i++)
                {
                    crc = table[((byte)crc) ^ bytes[i]] ^ (crc >> 8);
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }

        public static string GetLeafBoneName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/').Trim('/');
            var slashIdx = normalized.LastIndexOf('/');
            return slashIdx >= 0 && slashIdx + 1 < normalized.Length
                ? normalized.Substring(slashIdx + 1)
                : normalized;
        }

        private static IEnumerable<PPtr<AnimationClip>> EnumerateAnimationClipPointers(RuntimeAnimatorController controller)
        {
            if (controller is AnimatorController animatorController)
            {
                foreach (var clip in animatorController.m_AnimationClips ?? Array.Empty<PPtr<AnimationClip>>())
                {
                    yield return clip;
                }
            }
            else if (controller is AnimatorOverrideController overrideController)
            {
                foreach (var clipOverride in overrideController.m_Clips ?? Array.Empty<AnimationClipOverride>())
                {
                    if (clipOverride.m_OverrideClip != null && !clipOverride.m_OverrideClip.IsNull)
                    {
                        yield return clipOverride.m_OverrideClip;
                    }
                    else if (clipOverride.m_OriginalClip != null && !clipOverride.m_OriginalClip.IsNull)
                    {
                        yield return clipOverride.m_OriginalClip;
                    }
                }
            }
        }

        public static void BuildAnimationPreviewIndexesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            out Dictionary<AnimationClip, Avatar?> animationClipAvatarCacheOut,
            out Dictionary<Avatar, Mesh?> avatarMeshCacheOut,
            out Dictionary<Mesh, Avatar?> meshAvatarCacheOut,
            out Dictionary<AnimationClip, HashSet<uint>> animationClipTransformBindingsCacheOut)
        {
            var clips = assetsFileList.SelectMany(f => f.Objects).OfType<AnimationClip>().ToArray();
            var avatars = assetsFileList.SelectMany(f => f.Objects).OfType<Avatar>().ToArray();
            var meshes = assetsFileList.SelectMany(f => f.Objects).OfType<Mesh>()
                .Where(m => m.m_BoneNameHashes != null && m.m_BoneNameHashes.Length > 0
                    && m.m_BindPose != null && m.m_BindPose.Length > 0)
                .ToArray();

            var avatarIndexByTosHash = new Dictionary<uint, List<int>>();
            var avatarIndexBySkelHash = new Dictionary<uint, List<int>>();
            var avatarSkeletonHashes = new HashSet<uint>[avatars.Length];
            var avatarCleanNames = new string[avatars.Length];
            var avatarSearchKeys = new string[avatars.Length];

            for (var a = 0; a < avatars.Length; a++)
            {
                var avatar = avatars[a];
                avatarCleanNames[a] = avatar.m_Name.Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                avatarSearchKeys[a] = NormalizeAnimatorSearchKey(avatarCleanNames[a]);

                if (avatar.m_TOS != null && avatar.m_TOS.Length > 0)
                {
                    var seenTos = new HashSet<uint>(avatar.m_TOS.Length);
                    foreach (var pair in avatar.m_TOS)
                    {
                        if (pair.Key != 0 && seenTos.Add(pair.Key))
                        {
                            if (!avatarIndexByTosHash.TryGetValue(pair.Key, out var list))
                            {
                                list = new List<int>();
                                avatarIndexByTosHash[pair.Key] = list;
                            }

                            list.Add(a);
                        }
                    }
                }

                if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null && avatar.m_Avatar.m_AvatarSkeleton.m_ID.Length > 0)
                {
                    var skelSet = new HashSet<uint>(avatar.m_Avatar.m_AvatarSkeleton.m_ID);
                    avatarSkeletonHashes[a] = skelSet;
                    foreach (var boneId in skelSet)
                    {
                        if (!avatarIndexBySkelHash.TryGetValue(boneId, out var list))
                        {
                            list = new List<int>();
                            avatarIndexBySkelHash[boneId] = list;
                        }

                        list.Add(a);
                    }
                }
                else
                {
                    avatarSkeletonHashes[a] = new HashSet<uint>();
                }
            }

            var animationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache[clip] = GetTransformBindingPathsBackground(clip);
            }

            var avatarMeshCache = new Dictionary<Avatar, Mesh?>(avatars.Length);
            var meshToSkeletonHashes = new HashSet<uint>[meshes.Length];
            for (var m = 0; m < meshes.Length; m++)
            {
                meshToSkeletonHashes[m] = new HashSet<uint>(meshes[m].m_BoneNameHashes);
            }

            var meshIndexByBoneHash = new Dictionary<uint, List<int>>();
            for (var m = 0; m < meshes.Length; m++)
            {
                foreach (var hash in meshToSkeletonHashes[m])
                {
                    if (!meshIndexByBoneHash.TryGetValue(hash, out var list))
                    {
                        list = new List<int>();
                        meshIndexByBoneHash[hash] = list;
                    }

                    list.Add(m);
                }
            }

            var overlapCounts = new Dictionary<int, int>();
            for (var a = 0; a < avatars.Length; a++)
            {
                var avatar = avatars[a];
                var skelHashes = avatarSkeletonHashes[a];
                if (skelHashes.Count == 0)
                {
                    avatarMeshCache[avatar] = null;
                    continue;
                }

                overlapCounts.Clear();
                foreach (var boneId in skelHashes)
                {
                    if (!meshIndexByBoneHash.TryGetValue(boneId, out var matchingMeshes))
                    {
                        continue;
                    }

                    foreach (var mIdx in matchingMeshes)
                    {
                        overlapCounts.TryGetValue(mIdx, out var count);
                        overlapCounts[mIdx] = count + 1;
                    }
                }

                Mesh? bestMesh = null;
                var bestScore = 0;
                var avatarName = avatarCleanNames[a];
                foreach (var kvp in overlapCounts)
                {
                    var mesh = meshes[kvp.Key];
                    var overlap = kvp.Value;
                    if (!IsStrongMeshAvatarMatch(mesh.m_BoneNameHashes.Length, overlap))
                    {
                        continue;
                    }

                    var score = overlap * 100 + Math.Min(mesh.m_BoneNameHashes.Length, 40);
                    if (mesh.assetsFile == avatar.assetsFile) score += 20;
                    if (!string.IsNullOrEmpty(avatarName) && mesh.m_Name.Contains(avatarName, StringComparison.OrdinalIgnoreCase)) score += 15;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMesh = mesh;
                    }
                }

                avatarMeshCache[avatar] = bestMesh;
            }

            var meshAvatarCache = new Dictionary<Mesh, Avatar?>(meshes.Length);
            for (var m = 0; m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                var meshHashes = meshToSkeletonHashes[m];
                overlapCounts.Clear();
                foreach (var hash in meshHashes)
                {
                    if (!avatarIndexBySkelHash.TryGetValue(hash, out var matchingAvatars))
                    {
                        continue;
                    }

                    foreach (var aIdx in matchingAvatars)
                    {
                        overlapCounts.TryGetValue(aIdx, out var count);
                        overlapCounts[aIdx] = count + 1;
                    }
                }

                Avatar? bestAvatar = null;
                var bestScore = 0;
                var meshName = mesh.m_Name.ToLowerInvariant();
                foreach (var kvp in overlapCounts)
                {
                    var avatar = avatars[kvp.Key];
                    var overlap = kvp.Value;
                    if (!IsStrongMeshAvatarMatch(meshHashes.Count, overlap))
                    {
                        continue;
                    }

                    var score = overlap * 100;
                    if (avatar.assetsFile == mesh.assetsFile) score += 20;
                    var avatarNameLower = avatarCleanNames[kvp.Key].ToLowerInvariant();
                    if (!string.IsNullOrEmpty(avatarNameLower) && meshName.Contains(avatarNameLower)) score += 15;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAvatar = avatar;
                    }
                }

                meshAvatarCache[mesh] = bestAvatar;
            }

            var animationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths);
                if (bindingPaths == null || bindingPaths.Count == 0)
                {
                    animationClipAvatarCache[clip] = null;
                    continue;
                }

                overlapCounts.Clear();
                foreach (var pathHash in bindingPaths)
                {
                    if (!avatarIndexByTosHash.TryGetValue(pathHash, out var matchingAvatars))
                    {
                        continue;
                    }

                    foreach (var aIdx in matchingAvatars)
                    {
                        overlapCounts.TryGetValue(aIdx, out var count);
                        overlapCounts[aIdx] = count + 1;
                    }
                }

                Avatar? bestAvatar = null;
                var bestScore = 0;
                var clipName = NormalizeAnimatorSearchKey(clip.m_Name);
                foreach (var kvp in overlapCounts)
                {
                    var overlap = kvp.Value;
                    if (!IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap))
                    {
                        continue;
                    }

                    var avatar = avatars[kvp.Key];
                    var score = overlap * 100;
                    if (avatar.assetsFile == clip.assetsFile) score += 20;
                    var avatarSearchKey = avatarSearchKeys[kvp.Key];
                    if (!string.IsNullOrEmpty(avatarSearchKey) && clipName.Contains(avatarSearchKey, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 15;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAvatar = avatar;
                    }
                }

                animationClipAvatarCache[clip] = bestAvatar;
            }

            animationClipAvatarCacheOut = animationClipAvatarCache;
            avatarMeshCacheOut = avatarMeshCache;
            meshAvatarCacheOut = meshAvatarCache;
            animationClipTransformBindingsCacheOut = animationClipTransformBindingsCache;
        }

        public static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip)
        {
            var result = new HashSet<uint>();
            if (clip == null)
            {
                return result;
            }

            var bindings = clip.m_ClipBindingConstant;
            if (bindings == null && clip.m_MuscleClip?.m_Clip != null)
            {
                bindings = clip.m_MuscleClip.m_Clip.ConvertValueArrayToGenericBinding();
            }

            if (bindings?.genericBindings != null)
            {
                foreach (var binding in bindings.genericBindings)
                {
                    if (binding.typeID == ClassIDType.Transform && binding.path != 0)
                    {
                        result.Add(binding.path);
                    }
                }
            }

            if (result.Count == 0)
            {
                void AddLegacyPath(string? curvePath)
                {
                    if (string.IsNullOrWhiteSpace(curvePath))
                    {
                        return;
                    }

                    var fullHash = ComputeUnityBonePathHash(curvePath);
                    if (fullHash != 0)
                    {
                        result.Add(fullHash);
                    }

                    var leaf = GetLeafBoneName(curvePath);
                    if (!string.IsNullOrEmpty(leaf))
                    {
                        var leafHash = ComputeUnityBonePathHash(leaf);
                        if (leafHash != 0)
                        {
                            result.Add(leafHash);
                        }
                    }
                }

                foreach (var curve in clip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
                {
                    AddLegacyPath(curve?.path);
                }

                foreach (var curve in clip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
                {
                    AddLegacyPath(curve?.m_Path);
                }

                foreach (var curve in clip.m_EulerCurves ?? Array.Empty<Vector3Curve>())
                {
                    AddLegacyPath(curve?.path);
                }

                foreach (var curve in clip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
                {
                    AddLegacyPath(curve?.path);
                }

                foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
                {
                    AddLegacyPath(curve?.path);
                }
            }

            return result;
        }

        public static bool IsStrongAnimationAvatarMatch(int bindingCount, int overlap)
        {
            if (bindingCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = bindingCount < 6 ? bindingCount : Math.Max(6, bindingCount / 20);
            return overlap >= minimum;
        }

        public static bool IsStrongMeshAvatarMatch(int boneCount, int overlap)
        {
            if (boneCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = boneCount < 6 ? boneCount : Math.Max(6, boneCount / 10);
            return overlap >= minimum;
        }

        public static string NormalizeAnimatorSearchKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Path.GetFileNameWithoutExtension(value)
                .Replace("\\", "/", StringComparison.Ordinal)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()
                ?.Trim()
                .ToLowerInvariant() ?? string.Empty;
        }

        // --- Lazy Connections Pass & SQLite Staging ---

        public static List<string> GetLazyConnectionSourcePaths(
            ProjectIndex projectIndex,
            Action<AssetHandle>? rememberHandleSourcePath,
            Func<AssetHandle, string?> resolveHandleSourcePath)
        {
            var sourcePaths = new List<string>();
            if (projectIndex == null)
            {
                return sourcePaths;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var handle in projectIndex.GetHandles())
            {
                if (handle == null)
                {
                    continue;
                }

                rememberHandleSourcePath?.Invoke(handle);

                if (!LazyConnectionReferenceTypes.Contains(handle.Type))
                {
                    continue;
                }

                var sourcePath = resolveHandleSourcePath(handle);
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                if (seen.Contains(sourcePath) || failedPaths.Contains(sourcePath))
                {
                    continue;
                }

                if (!File.Exists(sourcePath))
                {
                    failedPaths.Add(sourcePath);
                    continue;
                }

                var fullPath = Path.GetFullPath(sourcePath);
                if (seen.Add(fullPath))
                {
                    sourcePaths.Add(fullPath);
                }
            }

            sourcePaths.Sort(StringComparer.OrdinalIgnoreCase);
            return sourcePaths;
        }

        internal static string FormatLazyConnectionStatusSummary(int totalSources, LazyConnectionBuildDiagnostics diagnostics)
        {
            return $"sources {totalSources:N0}, matched {diagnostics.SourcesWithLoadedFiles:N0}, no files {diagnostics.SourcesWithoutLoadedFiles:N0}, " +
                $"handles {diagnostics.CandidateHandles:N0}, objects {diagnostics.ResolvedObjects:N0}, " +
                $"model-groups {diagnostics.ModelGroups:N0}, group-meshes {diagnostics.ModelGroupMeshes:N0}, " +
                $"mesh-material {diagnostics.MeshMaterials:N0}, material-texture {diagnostics.MaterialTextures:N0}, edges {diagnostics.AssetEdges:N0}";
        }

        internal static string FormatLazyConnectionDetailedSummary(
            int totalSources,
            LazyConnectionBuildDiagnostics diagnostics,
            SemanticAssetRelations remainingRelations)
        {
            var placeholderPrefix = diagnostics.FormatStatusSummary(totalSources, remainingRelations);
            var fullDetailed = diagnostics.FormatDetailedSummary(totalSources, remainingRelations);
            var actualPrefix = FormatLazyConnectionStatusSummary(totalSources, diagnostics);
            return fullDetailed.StartsWith(placeholderPrefix, StringComparison.Ordinal)
                ? actualPrefix + fullDetailed.Substring(placeholderPrefix.Length)
                : fullDetailed;
        }

        internal static async Task<LazyConnectionBuildResult> BuildLazySemanticRelationsForSourcesAsync(
            AssetsManager assetsManager,
            IReadOnlyList<string> sourcePaths,
            Action<int, string> reportProgress,
            SQLiteProjectIndexCache.SemanticStagingSession? stagingSession = null,
            int stagingFlushRowThreshold = LazyConnectionStagingFlushRowThreshold,
            Func<CancellationToken, Task>? waitForInteractionPriorityAsync = null,
            Func<AssetHandle, bool>? isHandlePinnedForPreview = null)
        {
            var mergedRelations = new SemanticAssetRelations();
            var diagnostics = new LazyConnectionBuildDiagnostics
            {
                SourceCount = sourcePaths.Count
            };
            var failedSources = new List<string>();
            var effectiveFlushThreshold = Math.Max(1, stagingFlushRowThreshold);
            const int batchSize = 32;
            var parallelOptions = CreateSemanticGraphParallelOptions();

            for (var i = 0; i < sourcePaths.Count; i += batchSize)
            {
                var batch = sourcePaths.Skip(i).Take(batchSize).ToList();
                diagnostics.BatchCount++;
                try
                {
                    if (waitForInteractionPriorityAsync != null)
                    {
                        await waitForInteractionPriorityAsync(CancellationToken.None);
                    }
                    AssetsManager.ThrowIfMemoryPressureTooHigh("building lazy connections");
                    await assetsManager.LoadFilesAsync(batch.ToArray());

                    var filesBySource = ResolveLoadedFilesForConnectionBatch(assetsManager, batch);
                    var materializationResults = new SourceConnectionMaterializationResult?[batch.Count];
                    var sourceRelations = new SemanticAssetRelations?[batch.Count];
                    var sourceExceptions = new Exception?[batch.Count];

                    using (assetsManager.LruCache.SuspendEviction())
                    {
                        Parallel.For(0, batch.Count, parallelOptions, j =>
                        {
                            var filesForSource = filesBySource[j];
                            if (filesForSource.Count == 0)
                            {
                                return;
                            }

                            try
                            {
                                materializationResults[j] = MaterializeReferenceObjectsForConnectionFiles(assetsManager, filesForSource);
                            }
                            catch (Exception ex)
                            {
                                sourceExceptions[j] = ex;
                            }
                        });

                        var flattenedFiles = new List<(int SourceIndex, SerializedFile File)>(batch.Count);
                        for (var j = 0; j < batch.Count; j++)
                        {
                            if (sourceExceptions[j] != null)
                            {
                                continue;
                            }

                            var filesForSource = filesBySource[j];
                            for (var f = 0; f < filesForSource.Count; f++)
                            {
                                var file = filesForSource[f];
                                if (file != null)
                                {
                                    flattenedFiles.Add((j, file));
                                }
                            }
                        }

                        var perFileRelations = new SemanticAssetRelations?[flattenedFiles.Count];
                        Parallel.For(0, flattenedFiles.Count, parallelOptions, flatIndex =>
                        {
                            var entry = flattenedFiles[flatIndex];
                            try
                            {
                                var fileRel = new SemanticAssetRelations();
                                BuildSemanticRelationsForFilesBackground(new[] { entry.File }, fileRel, maxDegreeOfParallelism: 1);
                                perFileRelations[flatIndex] = fileRel;
                            }
                            catch (Exception ex)
                            {
                                sourceExceptions[entry.SourceIndex] ??= ex;
                            }
                        });

                        for (var flatIndex = 0; flatIndex < flattenedFiles.Count; flatIndex++)
                        {
                            var entry = flattenedFiles[flatIndex];
                            if (sourceExceptions[entry.SourceIndex] != null)
                            {
                                continue;
                            }

                            var rel = perFileRelations[flatIndex];
                            if (rel != null)
                            {
                                var target = sourceRelations[entry.SourceIndex];
                                if (target == null)
                                {
                                    target = new SemanticAssetRelations();
                                    sourceRelations[entry.SourceIndex] = target;
                                }
                                target.Merge(rel);
                            }
                        }
                    }

                    Parallel.For(0, batch.Count, parallelOptions, j =>
                    {
                        var filesForSource = filesBySource[j];
                        if (filesForSource.Count > 0)
                        {
                            try
                            {
                                UnloadConnectionBuildObjects(assetsManager, filesForSource, isHandlePinnedForPreview);
                            }
                            catch
                            {
                                // Ignore best-effort unload errors.
                            }
                        }
                    });

                    for (var j = 0; j < batch.Count; j++)
                    {
                        var sourcePath = batch[j];
                        var overallIndex = i + j;
                        try
                        {
                            if (sourceExceptions[j] != null)
                            {
                                throw sourceExceptions[j]!;
                            }

                            var filesForSource = filesBySource[j];
                            diagnostics.RecordLoadedFiles(sourcePath, filesForSource.Count);
                            if (filesForSource.Count == 0)
                            {
                                continue;
                            }

                            var mat = materializationResults[j];
                            if (mat != null)
                            {
                                diagnostics.RecordMaterializationCandidateCount(mat.CandidateHandles);
                                for (var f = 0; f < mat.FailedObjects; f++)
                                {
                                    diagnostics.RecordFailedObject();
                                }

                                foreach (var kvp in mat.ResolvedByType)
                                {
                                    for (var c = 0; c < kvp.Value; c++)
                                    {
                                        diagnostics.RecordResolvedObject(kvp.Key);
                                    }
                                }

                                diagnostics.RecordMaterializedSource(sourcePath, mat.ObjectsBefore, mat.ObjectsAfter);
                            }

                            var semanticRelations = sourceRelations[j] ?? new SemanticAssetRelations();
                            mergedRelations.Merge(semanticRelations);
                            diagnostics.RecordRelationsPass(semanticRelations);

                            if (stagingSession != null
                                && CountBufferedSemanticRelationRows(mergedRelations) >= effectiveFlushThreshold)
                            {
                                stagingSession.FlushBatch(mergedRelations, clearBatch: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            failedSources.Add(sourcePath);
                            diagnostics.FailedSources++;
                            Logger.Warning($"Failed to build connections for {Path.GetFileName(sourcePath)}: {ex.Message}");
                        }
                        finally
                        {
                            reportProgress(overallIndex + 1, sourcePath);
                        }
                    }
                }
                catch (MemoryPressureException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var sourcePath in batch)
                    {
                        failedSources.Add(sourcePath);
                        diagnostics.FailedSources++;
                        Logger.Warning($"Failed to load/build connections for {Path.GetFileName(sourcePath)} in batch: {ex.Message}");
                    }
                    for (var j = 0; j < batch.Count; j++)
                    {
                        var sourcePath = batch[j];
                        var overallIndex = i + j;
                        reportProgress(overallIndex + 1, sourcePath);
                    }
                }
                finally
                {
                    if (waitForInteractionPriorityAsync != null)
                    {
                        await waitForInteractionPriorityAsync(CancellationToken.None);
                    }
                    assetsManager.ClearLoadedFilesKeepIndex();
                }
            }

            if (stagingSession != null && CountBufferedSemanticRelationRows(mergedRelations) > 0)
            {
                stagingSession.FlushBatch(mergedRelations, clearBatch: true);
            }

            if (failedSources.Count > 0)
            {
                Logger.Warning($"Connections build completed with {failedSources.Count:N0} failed source file(s). First failure: {Path.GetFileName(failedSources[0])}");
            }

            return new LazyConnectionBuildResult(mergedRelations, diagnostics);
        }

        private static SourceConnectionMaterializationResult MaterializeReferenceObjectsForConnectionFiles(
            AssetsManager assetsManager,
            IReadOnlyList<SerializedFile> filesForSource)
        {
            var stats = new SourceConnectionMaterializationResult
            {
                ObjectsBefore = filesForSource.Sum(file => file.Objects.Count)
            };

            foreach (var sourceFile in filesForSource)
            {
                var handles = assetsManager.ProjectIndex
                    .GetHandlesForFile(sourceFile.fileName)
                    .Where(handle => LazyConnectionReferenceTypes.Contains(handle.Type)
                        && (ReferenceEquals(handle.SourceFile, sourceFile)
                            || IsSameSemanticSource(handle.OriginalPath, sourceFile.originalPath)
                            || string.IsNullOrEmpty(handle.OriginalPath)))
                    .ToList();

                stats.CandidateHandles += handles.Count;
                foreach (var handle in handles)
                {
                    if (handle.SourceFile?.reader == null)
                    {
                        handle.SourceFile = sourceFile;
                    }

                    var obj = assetsManager.ResolveHandle(handle);
                    if (obj == null)
                    {
                        stats.FailedObjects++;
                        continue;
                    }

                    stats.ResolvedByType.TryGetValue(handle.Type, out var count);
                    stats.ResolvedByType[handle.Type] = count + 1;
                    if (handle.Tag is AssetItem item)
                    {
                        LazyAssetResolutionService.UpdateLazyAssetItemHandle(item, handle);
                    }
                }
            }

            stats.ObjectsAfter = filesForSource.Sum(file => file.Objects.Count);
            return stats;
        }

        private static List<SerializedFile>[] ResolveLoadedFilesForConnectionBatch(
            AssetsManager assetsManager,
            IReadOnlyList<string> batch)
        {
            var results = new List<SerializedFile>[batch.Count];
            for (var j = 0; j < batch.Count; j++)
            {
                results[j] = GetLoadedFilesForConnectionSource(assetsManager, batch[j]);
            }

            return results;
        }

        public static List<SerializedFile> GetLoadedFilesForConnectionSource(AssetsManager assetsManager, string sourcePath)
        {
            lock (assetsManager.loadLock)
            {
                var directMatches = assetsManager.assetsFileList
                    .Where(file => file != null
                        && (IsSameSemanticSource(file.originalPath, sourcePath)
                            || IsSameSemanticSource(file.fullName, sourcePath)))
                    .ToList();
                if (directMatches.Count > 0)
                {
                    return directMatches;
                }

                var expectedSerializedFileNames = assetsManager.ProjectIndex.GetHandles()
                    .Where(handle => handle != null && IsSameSemanticSource(handle.OriginalPath, sourcePath))
                    .Select(handle => handle.SerializedFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (expectedSerializedFileNames.Count == 0)
                {
                    return directMatches;
                }

                return assetsManager.assetsFileList
                    .Where(file => file != null && expectedSerializedFileNames.Contains(file.fileName))
                    .ToList();
            }
        }

        public static void UnloadConnectionBuildObjects(
            AssetsManager assetsManager,
            IReadOnlyList<SerializedFile> files,
            Func<AssetHandle, bool>? isHandlePinnedForPreview = null)
        {
            foreach (var file in files)
            {
                if (file == null)
                {
                    continue;
                }

                foreach (var handle in assetsManager.ProjectIndex.GetHandlesForFile(file.fileName).ToList())
                {
                    if (handle == null
                        || !ReferenceEquals(handle.SourceFile, file)
                        || !LazyConnectionReferenceTypes.Contains(handle.Type)
                        || (isHandlePinnedForPreview != null && isHandlePinnedForPreview(handle)))
                    {
                        continue;
                    }

                    LazyAssetResolutionService.UnloadAsset(handle);
                }
            }
        }

        // --- Supporting Internal Types ---

        internal sealed class AssetReferenceIndexBuildResult
        {
            public Dictionary<Mesh, List<Material?>> MeshToMaterialsCache { get; } = new();
            public Dictionary<Mesh, List<string>> MeshAssociatedRenderersCache { get; } = new();
            public Dictionary<Mesh, HashSet<string>> MeshSourceTypesCache { get; } = new();
            public Dictionary<Material, Texture2D?> MaterialMainTextureCache { get; } = new();
            public Dictionary<Material, Material?> MaterialPreviewMaterialCache { get; } = new();
            public Dictionary<Material, Dictionary<string, Texture2D?>> MaterialTextureSlotsCache { get; } = new();
            public SemanticAssetRelations SemanticRelations { get; } = new();
        }

        private sealed class SourceConnectionMaterializationResult
        {
            public int ObjectsBefore;
            public int ObjectsAfter;
            public int CandidateHandles;
            public int FailedObjects;
            public readonly Dictionary<ClassIDType, int> ResolvedByType = new();
        }

        internal readonly struct MeshFilterSemanticBinding
        {
            public MeshFilterSemanticBinding(MeshFilter meshFilter, Mesh? mesh, string meshId, GameObject? gameObject)
            {
                MeshFilter = meshFilter;
                Mesh = mesh;
                MeshId = meshId;
                GameObject = gameObject;
            }

            public MeshFilter MeshFilter { get; }
            public Mesh? Mesh { get; }
            public string MeshId { get; }
            public GameObject? GameObject { get; }
        }

        internal readonly struct SkinnedMeshSemanticBinding
        {
            public SkinnedMeshSemanticBinding(
                SkinnedMeshRenderer renderer,
                Mesh? mesh,
                string meshId,
                GameObject? gameObject,
                int meshFileId,
                long meshPathId)
            {
                Renderer = renderer;
                Mesh = mesh;
                MeshId = meshId;
                GameObject = gameObject;
                MeshFileId = meshFileId;
                MeshPathId = meshPathId;
            }

            public SkinnedMeshRenderer Renderer { get; }
            public Mesh? Mesh { get; }
            public string MeshId { get; }
            public GameObject? GameObject { get; }
            public int MeshFileId { get; }
            public long MeshPathId { get; }
        }

        internal readonly struct SkinnedMeshSemanticMatch
        {
            public SkinnedMeshSemanticMatch(SkinnedMeshSemanticBinding binding, int confidence, string confidenceReason)
            {
                Binding = binding;
                Confidence = confidence;
                ConfidenceReason = confidenceReason;
            }

            public SkinnedMeshSemanticBinding Binding { get; }
            public int Confidence { get; }
            public string ConfidenceReason { get; }
        }

        internal readonly struct RendererMeshSemanticBinding
        {
            public RendererMeshSemanticBinding(
                Renderer renderer,
                string rendererId,
                string rendererType,
                Mesh? mesh,
                string meshId,
                GameObject? gameObject,
                int meshFileId,
                long meshPathId,
                int confidence,
                string confidenceReason)
            {
                Renderer = renderer;
                RendererId = rendererId;
                RendererType = rendererType;
                Mesh = mesh;
                MeshId = meshId;
                GameObject = gameObject;
                MeshFileId = meshFileId;
                MeshPathId = meshPathId;
                Confidence = confidence;
                ConfidenceReason = confidenceReason;
            }

            public Renderer Renderer { get; }
            public string RendererId { get; }
            public string RendererType { get; }
            public Mesh? Mesh { get; }
            public string MeshId { get; }
            public GameObject? GameObject { get; }
            public int MeshFileId { get; }
            public long MeshPathId { get; }
            public int Confidence { get; }
            public string ConfidenceReason { get; }

            public static RendererMeshSemanticBinding FromSkinned(SkinnedMeshSemanticBinding binding)
            {
                return new RendererMeshSemanticBinding(
                    binding.Renderer,
                    GetSemanticAssetId(binding.Renderer),
                    "SkinnedMeshRenderer",
                    binding.Mesh,
                    binding.MeshId,
                    binding.GameObject,
                    binding.MeshFileId,
                    binding.MeshPathId,
                    100,
                    "SkinnedMeshRenderer hierarchy");
            }
        }

        internal readonly struct GameObjectHierarchyEntry
        {
            public GameObjectHierarchyEntry(GameObject gameObject, string gameObjectId, string name, int depth)
            {
                GameObject = gameObject;
                GameObjectId = gameObjectId;
                Name = name;
                Depth = depth;
            }

            public GameObject GameObject { get; }
            public string GameObjectId { get; }
            public string Name { get; }
            public int Depth { get; }
        }

        private sealed class SceneObjectModelGroupBuilder
        {
            public SceneObjectModelGroupBuilder(GameObjectHierarchyEntry root)
            {
                Root = root;
            }

            public GameObjectHierarchyEntry Root { get; }
            public List<RendererMeshSemanticBinding> Bindings { get; } = new();
            public int Confidence => Bindings.Count == 0 ? 0 : Bindings.Min(binding => binding.Confidence);
            public string ConfidenceReason => Bindings.Count == 0
                ? string.Empty
                : Bindings.All(binding => binding.ConfidenceReason == Bindings[0].ConfidenceReason)
                    ? Bindings[0].ConfidenceReason
                    : "Mixed renderer hierarchy evidence";

            public void Add(RendererMeshSemanticBinding binding)
            {
                Bindings.Add(binding);
            }
        }
    }
}
