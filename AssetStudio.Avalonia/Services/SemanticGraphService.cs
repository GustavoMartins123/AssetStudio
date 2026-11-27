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
        public const int LazyConnectionStagingFlushRowThreshold = 25_000;
        public const int MaxClipBoneHashProbes = 24;
        public const int MaxRigBoneHashProbes = 512;
        public const int MaxContainerMaterialMeshes = 4;
        public const int MaxContainerMaterialCount = 16;

        [ThreadStatic]
        private static FileSemanticLookupContext? t_activeFileLookupContext;

        public static readonly string[] PreferredMaterialTextureSlots = MaterialPreviewBuilder.PreferredTextureSlots;

        private static readonly string[] AdditionalPreferredMaterialTextureSlots =
        {
            "_MainTexture",
            "_BaseColorTex",
            "_AlbedoTex",
            "_DiffuseTex",
            "_DiffuseMap",
            "_FaceTex",
            "_BaseEyeIrisMap",
            "_EyeIrisMap",
            "_IrisMap",
            "_IrisTex",
            "_EyeTex",
            "_EyeMap",
            "_BaseEyeWhiteMap"
        };

        public static readonly HashSet<string> NonDiffuseSlots = new(StringComparer.OrdinalIgnoreCase)
        {
            "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
            "_Normal", "Normal", "normal",
            "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
            "_EmissionMap", "_ParallaxMap", "_DetailMask",
            "_Cubemap", "_ReflectionTex", "_ShadowMap",
            "_MaskTexture", "_MaskMap", "_Mask", "_WeatheringMask", "_EmissionMask",
            "_MatcapMap", "_Matcap", "_MatcapTex", "_MatcapMaskMap",
            "_WetTexture",
            "_BaseEyeHighlightMap", "_BaseEyeHighlightMapBackup",
            "_BaseEyePupilMap", "_BaseEyePupilMapBackup",
            "_UVScrollTexture1", "_UVScrollTexture2"
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
            ClassIDType.RuntimeAnimatorController,
            ClassIDType.AssetBundle,
            ClassIDType.ResourceManager
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
                + relations.MaterialCaches.Count
                + relations.CompositeAssets.Count
                + relations.CompositeAssetParts.Count
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
            var previousLookupContext = t_activeFileLookupContext;
            var lookupContext = new FileSemanticLookupContext(file);
            t_activeFileLookupContext = lookupContext;
            try
            {
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
                            meshRendererBindings,
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
                            meshRendererBindings,
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
                    skinnedMeshBindings.Select(binding => RendererMeshSemanticBinding.FromSkinned(file, binding))
                        .Concat(meshRendererBindings));
                AddSkinnedMeshFallbackBoneHashes(file, skinnedMeshBindings, result.SemanticRelations);
                AddContainerSemanticRelations(result.SemanticRelations, file, containerReferences);
                return result;
            }
            finally
            {
                t_activeFileLookupContext = previousLookupContext;
            }
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
            var smrMesh = TryGetLoadedMeshBackground(file, smr.m_Mesh);
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
                    if (compPtr == null || compPtr.IsNull)
                    {
                        continue;
                    }

                    MeshFilter? mf = null;
                    if (compPtr.m_FileID == 0)
                    {
                        if (file.ObjectInfoDic != null
                            && file.ObjectInfoDic.TryGetValue(compPtr.m_PathID, out var compInfo)
                            && ClassIDTypeHelper.FromClassId(compInfo.classID) != ClassIDType.MeshFilter)
                        {
                            continue;
                        }

                        if (TryGetLoadedObjectNoDeserialize<Component, MeshFilter>(file, compPtr) is MeshFilter loadedMf)
                        {
                            mf = loadedMf;
                        }
                        else if (file.ObjectInfoDic == null || file.ObjectInfoDic.ContainsKey(compPtr.m_PathID))
                        {
                            mf = ResolveObjectBackground(file, compPtr.m_PathID) as MeshFilter;
                        }
                    }
                    else
                    {
                        mf = TryGetLoadedObjectNoDeserialize<Component, MeshFilter>(file, compPtr);
                    }

                    if (mf != null)
                    {
                        var mfMesh = TryGetLoadedMeshBackground(file, mf.m_Mesh);
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

        internal static void AddContainerMaterialRelations(
            SemanticAssetRelations relations,
            SerializedFile sourceFile,
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences)
        {
            AddContainerSemanticRelations(relations, sourceFile, containerReferences, includeMaterialRelations: true, includeAnimationRelations: false);
        }

        private static void AddContainerSemanticRelations(
            SemanticAssetRelations relations,
            SerializedFile sourceFile,
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences,
            bool includeMaterialRelations = true,
            bool includeAnimationRelations = true)
        {
            if (containerReferences.Count == 0)
            {
                return;
            }

            HashSet<string>? meshesWithRendererRelations = null;
            if (includeMaterialRelations)
            {
                meshesWithRendererRelations = new HashSet<string>(StringComparer.Ordinal);
                foreach (var relation in relations.MeshMaterials)
                {
                    if (!string.Equals(relation.RendererType, "Container", StringComparison.OrdinalIgnoreCase))
                    {
                        meshesWithRendererRelations.Add(relation.MeshAssetId);
                    }
                }
            }

            var resolvedCache = new Dictionary<(int FileId, long PathId), ResolvedSemanticPPtr>();
            var meshIds = new List<string>();
            var meshIdSet = new HashSet<string>(StringComparer.Ordinal);
            var materialIds = new List<string>();
            var materialIdSet = new HashSet<string>(StringComparer.Ordinal);
            var clipIds = new List<string>();
            var clipIdSet = new HashSet<string>(StringComparer.Ordinal);
            var avatarIds = new List<string>();
            var avatarIdSet = new HashSet<string>(StringComparer.Ordinal);
            var animatorIds = new List<string>();
            var animatorIdSet = new HashSet<string>(StringComparer.Ordinal);
            var localObjectInfoDic = sourceFile.ObjectInfoDic;

            foreach (var container in containerReferences)
            {
                meshIds.Clear();
                meshIdSet.Clear();
                materialIds.Clear();
                materialIdSet.Clear();
                clipIds.Clear();
                clipIdSet.Clear();
                avatarIds.Clear();
                avatarIdSet.Clear();
                animatorIds.Clear();
                animatorIdSet.Clear();

                var hasContainerName = !string.IsNullOrWhiteSpace(container.Container);
                var needAnimationTypes = includeAnimationRelations && hasContainerName;

                foreach (var reference in container.References)
                {
                    if (reference == null || reference.IsNull)
                    {
                        continue;
                    }

                    // Fast-skip local objects whose ClassID cannot participate in container material or animation relations.
                    if (reference.m_FileID == 0
                        && localObjectInfoDic != null
                        && localObjectInfoDic.TryGetValue(reference.m_PathID, out var localInfo))
                    {
                        var localType = ClassIDTypeHelper.FromClassId(localInfo.classID);
                        if (localType != ClassIDType.Mesh
                            && localType != ClassIDType.Material
                            && (!needAnimationTypes
                                || (localType != ClassIDType.AnimationClip
                                    && localType != ClassIDType.Avatar
                                    && localType != ClassIDType.Animator)))
                        {
                            continue;
                        }
                    }

                    var key = (reference.m_FileID, reference.m_PathID);
                    if (!resolvedCache.TryGetValue(key, out var resolved))
                    {
                        TryResolveSemanticPPtrInfo(sourceFile, reference.m_FileID, reference.m_PathID, expectedType: null, out resolved);
                        resolvedCache[key] = resolved;
                    }

                    if (!resolved.IsValid)
                    {
                        continue;
                    }

                    switch (resolved.Type)
                    {
                        case ClassIDType.Mesh:
                            if (meshIdSet.Add(resolved.UniqueId))
                            {
                                meshIds.Add(resolved.UniqueId);
                            }
                            break;
                        case ClassIDType.Material:
                            if (includeMaterialRelations && materialIdSet.Add(resolved.UniqueId))
                            {
                                materialIds.Add(resolved.UniqueId);
                            }
                            break;
                        case ClassIDType.AnimationClip:
                            if (needAnimationTypes && clipIdSet.Add(resolved.UniqueId))
                            {
                                clipIds.Add(resolved.UniqueId);
                            }
                            break;
                        case ClassIDType.Avatar:
                            if (needAnimationTypes && avatarIdSet.Add(resolved.UniqueId))
                            {
                                avatarIds.Add(resolved.UniqueId);
                            }
                            break;
                        case ClassIDType.Animator:
                            if (needAnimationTypes && animatorIdSet.Add(resolved.UniqueId))
                            {
                                animatorIds.Add(resolved.UniqueId);
                            }
                            break;
                    }
                }

                // Emit synthetic Container material bindings only for narrow asset containers to prevent
                // scene-bundle Cartesian-product explosions (e.g. hundreds of meshes x hundreds of materials).
                if (includeMaterialRelations
                    && meshIds.Count > 0
                    && materialIds.Count > 0
                    && meshIds.Count <= MaxContainerMaterialMeshes
                    && materialIds.Count <= MaxContainerMaterialCount)
                {
                    var rendererId = BuildContainerRelationId(sourceFile, container.Container);
                    var description = string.IsNullOrWhiteSpace(container.Container)
                        ? "AssetBundle container"
                        : $"AssetBundle container \"{container.Container}\"";

                    foreach (var meshId in meshIds)
                    {
                        if (meshesWithRendererRelations != null && meshesWithRendererRelations.Contains(meshId))
                        {
                            continue;
                        }

                        relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                            meshId,
                            rendererId,
                            "Container",
                            string.Empty,
                            string.Empty,
                            description));

                        for (var index = 0; index < materialIds.Count; index++)
                        {
                            relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                                meshId,
                                materialIds[index],
                                rendererId,
                                "Container",
                                index,
                                index,
                                index + 1));
                        }
                    }
                }

                if (needAnimationTypes && clipIds.Count > 0 && (avatarIds.Count > 0 || meshIds.Count > 0))
                {
                    var primaryAvatarId = avatarIds.Count > 0 ? avatarIds[0] : string.Empty;
                    var primaryAnimatorId = animatorIds.Count > 0 ? animatorIds[0] : string.Empty;
                    var primaryMeshId = meshIds.Count > 0 ? meshIds[0] : string.Empty;
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
        }

        private static string BuildContainerRelationId(SerializedFile sourceFile, string container)
        {
            var sourceHash = AssetHandle.BuildSourceHash(sourceFile?.fileName ?? string.Empty, sourceFile?.originalPath);
            return $"container:{sourceHash}:{container ?? string.Empty}";
        }

        // --- Semantic Resolution ---

        private static SerializedFile? TryGetLoadedTargetFileForPPtr(SerializedFile? sourceFile, int fileId)
        {
            if (sourceFile == null)
            {
                return null;
            }

            if (fileId == 0)
            {
                return sourceFile;
            }

            if (fileId < 0 || sourceFile.m_Externals == null || fileId - 1 >= sourceFile.m_Externals.Count)
            {
                return null;
            }

            var activeContext = t_activeFileLookupContext;
            if (activeContext != null && ReferenceEquals(activeContext.SourceFile, sourceFile))
            {
                return activeContext.GetLoadedExternalFile(fileId);
            }

            var assetsManager = sourceFile.assetsManager;
            if (assetsManager == null)
            {
                return null;
            }

            var external = sourceFile.m_Externals[fileId - 1];
            if (external == null)
            {
                return null;
            }

            lock (assetsManager.loadLock)
            {
                var assetsFileList = assetsManager.assetsFileList;
                for (var i = 0; i < assetsFileList.Count; i++)
                {
                    var candidate = assetsFileList[i];
                    if (candidate != null && IsExternalSerializedFileMatch(candidate, external))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static bool IsExternalSerializedFileMatch(SerializedFile candidate, FileIdentifier external)
        {
            var extFile = GetSemanticFileName(external.fileName);
            if (!string.IsNullOrEmpty(extFile)
                && (string.Equals(GetSemanticFileName(candidate.fileName), extFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetSemanticFileName(candidate.originalPath), extFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetSemanticFileName(candidate.fullName), extFile, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var extPathFile = GetSemanticFileName(external.pathName);
            return !string.IsNullOrEmpty(extPathFile)
                && (string.Equals(GetSemanticFileName(candidate.fileName), extPathFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetSemanticFileName(candidate.originalPath), extPathFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetSemanticFileName(candidate.fullName), extPathFile, StringComparison.OrdinalIgnoreCase));
        }

        private static T? TryGetLoadedObjectNoDeserialize<T>(SerializedFile? sourceFile, PPtr<T>? pptr)
            where T : AssetStudio.Object
        {
            return TryGetLoadedObjectNoDeserialize<T, T>(sourceFile, pptr);
        }

        private static TResult? TryGetLoadedObjectNoDeserialize<TPtr, TResult>(SerializedFile? sourceFile, PPtr<TPtr>? pptr)
            where TPtr : AssetStudio.Object
            where TResult : AssetStudio.Object
        {
            if (pptr == null || pptr.IsNull)
            {
                return null;
            }

            SerializedFile? targetFile;
            if (pptr.m_FileID == 0)
            {
                targetFile = sourceFile;
            }
            else if (sourceFile != null)
            {
                targetFile = TryGetLoadedTargetFileForPPtr(sourceFile, pptr.m_FileID);
            }
            else
            {
                pptr.TryGetAssetsFile(out targetFile);
            }

            if (targetFile == null)
            {
                return null;
            }

            if (targetFile.TryGetObject(pptr.m_PathID, out var existing))
            {
                return existing as TResult;
            }

            return null;
        }

        private static bool TryResolveSemanticPPtrInfo(
            SerializedFile sourceFile,
            int fileId,
            long pathId,
            ClassIDType? expectedType,
            out ResolvedSemanticPPtr resolved)
        {
            return TryResolveSemanticPPtrInfo(sourceFile, fileId, pathId, expectedType, out resolved, out _);
        }

        private static bool TryResolveSemanticPPtrInfo(
            SerializedFile sourceFile,
            int fileId,
            long pathId,
            ClassIDType? expectedType,
            out ResolvedSemanticPPtr resolved,
            out int arenaIndex)
        {
            resolved = default;
            arenaIndex = -1;
            if (sourceFile == null || pathId == 0)
            {
                return false;
            }

            var activeContext = t_activeFileLookupContext;
            var hasActiveContext = activeContext != null && ReferenceEquals(activeContext.SourceFile, sourceFile);

            if (fileId == 0)
            {
                if (sourceFile.ObjectInfoDic != null && sourceFile.ObjectInfoDic.TryGetValue(pathId, out var objInfo))
                {
                    var actualType = ClassIDTypeHelper.FromClassId(objInfo.classID);
                    if (expectedType != null && !IsCompatibleSemanticClassId(actualType, expectedType.Value))
                    {
                        return false;
                    }

                    var localUniqueId = hasActiveContext
                        ? activeContext!.BuildLocalUniqueId(pathId)
                        : AssetHandle.BuildUniqueID(sourceFile, pathId);
                    resolved = new ResolvedSemanticPPtr(actualType, localUniqueId);
                    return true;
                }

                var localProjectIndex = sourceFile.assetsManager?.ProjectIndex;
                if (localProjectIndex != null)
                {
                    var localOrigin = hasActiveContext
                        ? activeContext!.LocalOrigin
                        : AssetHandleOrigin.Intern(sourceFile);
                    if (localProjectIndex.TryGetIndex(new AssetHandleKey(localOrigin.KeyOriginId, pathId), out var idx))
                    {
                        var actualType = localProjectIndex.Arena.GetType(idx);
                        if (expectedType != null && !IsCompatibleSemanticClassId(actualType, expectedType.Value))
                        {
                            return false;
                        }

                        arenaIndex = idx;
                        resolved = new ResolvedSemanticPPtr(actualType, localProjectIndex.Arena.GetUniqueId(idx));
                        return true;
                    }
                }

                if (expectedType != null)
                {
                    var fallbackUniqueId = hasActiveContext
                        ? activeContext!.BuildLocalUniqueId(pathId)
                        : AssetHandle.BuildUniqueID(sourceFile, pathId);
                    resolved = new ResolvedSemanticPPtr(expectedType.Value, fallbackUniqueId);
                    return true;
                }

                return false;
            }

            if (hasActiveContext && activeContext!.TryGetCachedExternalPPtr(fileId, pathId, out var cachedEntry))
            {
                if (cachedEntry.Resolved.IsValid
                    && (expectedType == null || IsCompatibleSemanticClassId(cachedEntry.Resolved.Type, expectedType.Value)))
                {
                    resolved = cachedEntry.Resolved;
                    arenaIndex = cachedEntry.ArenaIndex;
                    return true;
                }

                if (!cachedEntry.Resolved.IsValid)
                {
                    return false;
                }
            }

            var loadedTarget = TryGetLoadedTargetFileForPPtr(sourceFile, fileId);
            if (loadedTarget != null)
            {
                if (loadedTarget.ObjectInfoDic != null && loadedTarget.ObjectInfoDic.TryGetValue(pathId, out var targetInfo))
                {
                    var actualType = ClassIDTypeHelper.FromClassId(targetInfo.classID);
                    var uid = AssetHandle.BuildUniqueID(loadedTarget, pathId);
                    var candidateResolved = new ResolvedSemanticPPtr(actualType, uid);
                    if (hasActiveContext)
                    {
                        activeContext!.CacheExternalPPtr(fileId, pathId, candidateResolved, -1);
                    }

                    if (expectedType == null || IsCompatibleSemanticClassId(actualType, expectedType.Value))
                    {
                        resolved = candidateResolved;
                        return true;
                    }

                    return false;
                }

                var loadedProjectIndex = sourceFile.assetsManager?.ProjectIndex;
                if (loadedProjectIndex != null)
                {
                    var targetOrigin = AssetHandleOrigin.Intern(loadedTarget);
                    if (loadedProjectIndex.TryGetIndex(new AssetHandleKey(targetOrigin.KeyOriginId, pathId), out var idx))
                    {
                        var actualType = loadedProjectIndex.Arena.GetType(idx);
                        var candidateResolved = new ResolvedSemanticPPtr(actualType, loadedProjectIndex.Arena.GetUniqueId(idx));
                        if (hasActiveContext)
                        {
                            activeContext!.CacheExternalPPtr(fileId, pathId, candidateResolved, idx);
                        }

                        if (expectedType == null || IsCompatibleSemanticClassId(actualType, expectedType.Value))
                        {
                            resolved = candidateResolved;
                            arenaIndex = idx;
                            return true;
                        }

                        return false;
                    }
                }

                if (expectedType != null)
                {
                    resolved = new ResolvedSemanticPPtr(expectedType.Value, AssetHandle.BuildUniqueID(loadedTarget, pathId));
                    return true;
                }
            }

            if (TryFindSemanticArenaMatchForPPtr(sourceFile, fileId, pathId, expectedType, out var matchedIndex, out var matchedType, out var matchedUniqueId))
            {
                var matchedResolved = new ResolvedSemanticPPtr(matchedType, matchedUniqueId);
                if (hasActiveContext && expectedType == null)
                {
                    activeContext!.CacheExternalPPtr(fileId, pathId, matchedResolved, matchedIndex);
                }

                resolved = matchedResolved;
                arenaIndex = matchedIndex;
                return true;
            }

            if (hasActiveContext && expectedType == null)
            {
                activeContext!.CacheExternalPPtr(fileId, pathId, default, -1);
            }

            return false;
        }

        private static bool TryFindSemanticArenaMatchForPPtr(
            SerializedFile sourceFile,
            int fileId,
            long pathId,
            ClassIDType? expectedType,
            out int arenaIndex,
            out ClassIDType actualType,
            out string uniqueId)
        {
            arenaIndex = -1;
            actualType = ClassIDType.UnknownType;
            uniqueId = string.Empty;

            var assetsManager = sourceFile?.assetsManager;
            var projectIndex = assetsManager?.ProjectIndex;
            if (assetsManager == null
                || projectIndex == null
                || sourceFile?.m_Externals == null
                || fileId <= 0
                || fileId - 1 >= sourceFile.m_Externals.Count)
            {
                return false;
            }

            var external = sourceFile.m_Externals[fileId - 1];
            if (external == null)
            {
                return false;
            }

            var activeContext = t_activeFileLookupContext;
            string? cand1;
            string? cand2;
            string? cand3;
            if (activeContext != null && ReferenceEquals(activeContext.SourceFile, sourceFile))
            {
                (cand1, cand2, cand3) = activeContext.GetExternalNameCandidates(fileId);
            }
            else
            {
                GetExternalFileNameCandidates(external, out cand1, out cand2, out cand3);
            }

            // Fast O(1) path via AssetIndexArena._originIdsByFileName + _indexByKey (zero handle allocations).
            var typeMismatchCandidate = false;
            if (TryProbeExternalCandidate(assetsManager, cand1, pathId, expectedType, out arenaIndex, out actualType, out uniqueId, ref typeMismatchCandidate)
                || TryProbeExternalCandidate(assetsManager, cand2, pathId, expectedType, out arenaIndex, out actualType, out uniqueId, ref typeMismatchCandidate)
                || TryProbeExternalCandidate(assetsManager, cand3, pathId, expectedType, out arenaIndex, out actualType, out uniqueId, ref typeMismatchCandidate))
            {
                return true;
            }

            if (!typeMismatchCandidate)
            {
                return false;
            }

            // Rare fallback: multiple origins share the same fileName and PathID with different ClassIDTypes.
            var arena = projectIndex.Arena;
            var bestIndex = -1;
            var bestScore = int.MinValue;
            var bestType = ClassIDType.UnknownType;
            var bestUniqueId = string.Empty;
            ProbeFallbackIndicesForCandidate(arena, cand1, pathId, expectedType, external, sourceFile, ref bestIndex, ref bestScore, ref bestType, ref bestUniqueId);
            ProbeFallbackIndicesForCandidate(arena, cand2, pathId, expectedType, external, sourceFile, ref bestIndex, ref bestScore, ref bestType, ref bestUniqueId);
            ProbeFallbackIndicesForCandidate(arena, cand3, pathId, expectedType, external, sourceFile, ref bestIndex, ref bestScore, ref bestType, ref bestUniqueId);

            if (bestIndex >= 0)
            {
                arenaIndex = bestIndex;
                actualType = bestType;
                uniqueId = bestUniqueId;
                return true;
            }

            return false;
        }

        private static bool TryProbeExternalCandidate(
            AssetsManager assetsManager,
            string? candidateFileName,
            long pathId,
            ClassIDType? expectedType,
            out int arenaIndex,
            out ClassIDType actualType,
            out string uniqueId,
            ref bool typeMismatchCandidate)
        {
            arenaIndex = -1;
            actualType = ClassIDType.UnknownType;
            uniqueId = string.Empty;
            if (string.IsNullOrEmpty(candidateFileName))
            {
                return false;
            }

            if (!assetsManager.TryFindIndexedAssetByFileNameAndPathId(candidateFileName, pathId, out var idx, out var foundType, out var foundUniqueId, out _))
            {
                return false;
            }

            if (expectedType == null || IsCompatibleSemanticClassId(foundType, expectedType.Value))
            {
                arenaIndex = idx;
                actualType = foundType;
                uniqueId = foundUniqueId;
                return true;
            }

            typeMismatchCandidate = true;
            return false;
        }

        private static void ProbeFallbackIndicesForCandidate(
            AssetIndexArena arena,
            string? candidateFileName,
            long pathId,
            ClassIDType? expectedType,
            FileIdentifier external,
            SerializedFile sourceFile,
            ref int bestIndex,
            ref int bestScore,
            ref ClassIDType bestType,
            ref string bestUniqueId)
        {
            if (string.IsNullOrEmpty(candidateFileName))
            {
                return;
            }

            var indices = arena.GetIndicesForFile(candidateFileName);
            for (var i = 0; i < indices.Length; i++)
            {
                var idx = indices[i];
                if (arena.GetPathId(idx) != pathId)
                {
                    continue;
                }

                var foundType = arena.GetType(idx);
                if (expectedType != null && !IsCompatibleSemanticClassId(foundType, expectedType.Value))
                {
                    continue;
                }

                var origin = arena.GetOrigin(idx);
                var score = ScoreSemanticOriginMatch(origin, arena.GetSourceFile(idx), external, sourceFile);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = idx;
                    bestType = foundType;
                    bestUniqueId = arena.GetUniqueId(idx);
                }
            }
        }

        private static void GetExternalFileNameCandidates(FileIdentifier external, out string? cand1, out string? cand2, out string? cand3)
        {
            cand1 = !string.IsNullOrWhiteSpace(external.fileName) ? external.fileName : null;
            cand2 = null;
            cand3 = null;

            if (!string.IsNullOrWhiteSpace(external.fileName))
            {
                var fileLeaf = GetSemanticFileName(external.fileName);
                if (!string.IsNullOrEmpty(fileLeaf) && !string.Equals(fileLeaf, cand1, StringComparison.OrdinalIgnoreCase))
                {
                    cand2 = fileLeaf;
                }
            }

            if (!string.IsNullOrWhiteSpace(external.pathName))
            {
                var pathLeaf = GetSemanticFileName(external.pathName);
                if (!string.IsNullOrEmpty(pathLeaf)
                    && !string.Equals(pathLeaf, cand1, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pathLeaf, cand2, StringComparison.OrdinalIgnoreCase))
                {
                    if (cand1 == null) cand1 = pathLeaf;
                    else if (cand2 == null) cand2 = pathLeaf;
                    else cand3 = pathLeaf;
                }

                if (!string.Equals(external.pathName, cand1, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(external.pathName, cand2, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(external.pathName, cand3, StringComparison.OrdinalIgnoreCase))
                {
                    if (cand1 == null) cand1 = external.pathName;
                    else if (cand2 == null) cand2 = external.pathName;
                    else if (cand3 == null) cand3 = external.pathName;
                }
            }
        }

        public static AssetHandle? GetSemanticHandleFromPPtr(SerializedFile sourceFile, PPtr<AssetStudio.Object> pptr)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null || pptr == null || pptr.IsNull)
            {
                return null;
            }

            if (pptr.m_FileID == 0)
            {
                return sourceFile.assetsManager.ProjectIndex.GetHandle(sourceFile, pptr.m_PathID);
            }

            var targetFile = TryGetLoadedTargetFileForPPtr(sourceFile, pptr.m_FileID);
            if (targetFile != null)
            {
                var handle = sourceFile.assetsManager.ProjectIndex.GetHandle(targetFile, pptr.m_PathID);
                if (handle != null)
                {
                    return handle;
                }
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

            var objectInfoDic = sourceFile?.ObjectInfoDic;
            foreach (var componentPtr in gameObject.m_Components)
            {
                if (componentPtr == null || componentPtr.IsNull)
                {
                    continue;
                }

                if (componentPtr.m_FileID == 0)
                {
                    if (objectInfoDic != null && objectInfoDic.TryGetValue(componentPtr.m_PathID, out var compInfo))
                    {
                        var compClass = ClassIDTypeHelper.FromClassId(compInfo.classID);
                        if (compClass != ClassIDType.Transform && compClass != ClassIDType.RectTransform)
                        {
                            continue;
                        }
                    }

                    if (TryGetLoadedObjectNoDeserialize<Component, Transform>(sourceFile, componentPtr) is Transform loadedTransform)
                    {
                        return loadedTransform;
                    }

                    if (sourceFile != null && (objectInfoDic == null || objectInfoDic.ContainsKey(componentPtr.m_PathID)))
                    {
                        if (ResolveObjectBackground(sourceFile, componentPtr.m_PathID) is Transform resolvedTransform)
                        {
                            return resolvedTransform;
                        }
                    }
                }
                else
                {
                    if (TryGetLoadedObjectNoDeserialize<Component, Transform>(sourceFile, componentPtr) is Transform externalTransform)
                    {
                        return externalTransform;
                    }
                }
            }

            return null;
        }

        public static Transform? ResolveTransformBackground(SerializedFile sourceFile, PPtr<Transform> pptr)
        {
            return ResolveReferenceObjectBackground(sourceFile, pptr);
        }

        public static Avatar? ResolveAvatarBackground(SerializedFile sourceFile, PPtr<Avatar> pptr)
        {
            return ResolveReferenceObjectBackground(sourceFile, pptr);
        }

        public static RuntimeAnimatorController? ResolveRuntimeAnimatorControllerBackground(
            SerializedFile sourceFile,
            PPtr<RuntimeAnimatorController> pptr)
        {
            return ResolveReferenceObjectBackground(sourceFile, pptr);
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
            return ResolveReferenceObjectBackground(sourceFile, pptr);
        }

        public static Mesh? ResolveMeshBackground(SerializedFile sourceFile, PPtr<Mesh> pptr)
        {
            return ResolveReferenceObjectBackground(sourceFile, pptr);
        }

        private static T? ResolveReferenceObjectBackground<T>(SerializedFile sourceFile, PPtr<T>? pptr)
            where T : AssetStudio.Object
        {
            if (pptr == null || pptr.IsNull)
            {
                return null;
            }

            var loaded = TryGetLoadedObjectNoDeserialize(sourceFile, pptr);
            if (loaded != null)
            {
                return loaded;
            }

            var targetFile = pptr.m_FileID == 0
                ? sourceFile
                : TryGetLoadedTargetFileForPPtr(sourceFile, pptr.m_FileID);
            if (targetFile == null)
            {
                return null;
            }

            if (targetFile.ObjectInfoDic != null && !targetFile.ObjectInfoDic.ContainsKey(pptr.m_PathID))
            {
                return null;
            }

            return ResolveObjectBackground(targetFile, pptr.m_PathID) as T;
        }

        private static Mesh? TryGetLoadedMeshBackground(SerializedFile sourceFile, PPtr<Mesh> pptr)
        {
            return TryGetLoadedObjectNoDeserialize(sourceFile, pptr);
        }

        public static Material? ResolveRendererMaterialBackground(PPtr<Material> pptr)
        {
            return TryGetLoadedObjectNoDeserialize<Material, Material>(sourceFile: null, pptr);
        }

        public static AssetStudio.Object? ResolveObjectBackground(SerializedFile sourceFile, long pathID)
        {
            if (sourceFile.TryGetObject(pathID, out var obj))
            {
                return obj;
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
            if (asset?.assetsFile == null)
            {
                return string.Empty;
            }

            var activeContext = t_activeFileLookupContext;
            if (activeContext != null && ReferenceEquals(activeContext.SourceFile, asset.assetsFile))
            {
                return activeContext.BuildLocalUniqueId(asset.m_PathID);
            }

            return AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID);
        }

        public static string GetSemanticAssetIdFromPPtr<T>(SerializedFile sourceFile, PPtr<T> pptr, ClassIDType expectedType)
            where T : AssetStudio.Object
        {
            if (sourceFile == null || pptr == null || pptr.IsNull)
            {
                return string.Empty;
            }

            return TryResolveSemanticPPtrInfo(sourceFile, pptr.m_FileID, pptr.m_PathID, expectedType, out var resolved)
                ? resolved.UniqueId
                : string.Empty;
        }

        public static bool IsCompatibleSemanticClassId(ClassIDType actual, ClassIDType expected)
        {
            if (actual == expected)
            {
                return true;
            }

            return expected switch
            {
                ClassIDType.RuntimeAnimatorController =>
                    actual == ClassIDType.AnimatorController
                    || actual == ClassIDType.AnimatorOverrideController,
                ClassIDType.Transform =>
                    actual == ClassIDType.RectTransform,
                ClassIDType.Texture =>
                    actual == ClassIDType.Texture2D
                    || actual == ClassIDType.Texture3D
                    || actual == ClassIDType.Cubemap
                    || actual == ClassIDType.RenderTexture
                    || actual == ClassIDType.Texture2DArray,
                ClassIDType.Renderer =>
                    actual == ClassIDType.MeshRenderer
                    || actual == ClassIDType.SkinnedMeshRenderer
                    || actual == ClassIDType.SpriteRenderer
                    || actual == ClassIDType.ParticleSystemRenderer
                    || actual == ClassIDType.TrailRenderer
                    || actual == ClassIDType.LineRenderer,
                _ => false
            };
        }

        public static AssetHandle? FindSemanticHandleForPPtr(SerializedFile sourceFile, int fileId, long pathId, ClassIDType? expectedType)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null)
            {
                return null;
            }

            if (TryFindSemanticArenaMatchForPPtr(sourceFile, fileId, pathId, expectedType, out var arenaIndex, out _, out _)
                && arenaIndex >= 0)
            {
                return sourceFile.assetsManager.ProjectIndex.Arena.MaterializeHandle(arenaIndex);
            }

            return null;
        }

        private static int ScoreSemanticOriginMatch(SourceOriginRecord origin, SerializedFile? originSourceFile, FileIdentifier external, SerializedFile sourceFile)
        {
            var score = 0;
            if (IsSameSemanticSource(origin.OriginalPath, external.pathName)
                || IsSameSemanticSource(originSourceFile?.originalPath, external.pathName)
                || IsSameSemanticSource(originSourceFile?.fullName, external.pathName))
            {
                score += 100;
            }

            if (IsSameSemanticSource(origin.SerializedFileName, external.fileName)
                || IsSameSemanticSource(originSourceFile?.fileName, external.fileName))
            {
                score += 50;
            }

            var externalPathFileName = GetSemanticFileName(external.pathName);
            if (!string.IsNullOrEmpty(externalPathFileName)
                && (IsSameSemanticSource(GetSemanticFileName(origin.OriginalPath), externalPathFileName)
                    || IsSameSemanticSource(origin.SerializedFileName, externalPathFileName)
                    || IsSameSemanticSource(originSourceFile?.fileName, externalPathFileName)))
            {
                score += 25;
            }

            var sourceDirectory = Path.GetDirectoryName(sourceFile.originalPath ?? sourceFile.fullName ?? string.Empty);
            var handleDirectory = Path.GetDirectoryName(origin.OriginalPath ?? originSourceFile?.originalPath ?? string.Empty);
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

                var mesh = TryGetLoadedMeshBackground(sourceFile, meshFilter.m_Mesh);
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
                var mesh = TryGetLoadedMeshBackground(sourceFile, renderer.m_Mesh);
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

                    var boundGameObject = binding.GameObject ?? gameObject;
                    var isActive = IsRendererActiveInHierarchy(sourceFile, renderer, boundGameObject);
                    result.Add(new RendererMeshSemanticBinding(
                        renderer,
                        rendererId,
                        "MeshRenderer",
                        binding.Mesh,
                        binding.MeshId,
                        boundGameObject,
                        binding.MeshFilter.m_Mesh.m_FileID,
                        binding.MeshFilter.m_Mesh.m_PathID,
                        isActive ? 100 : 40,
                        isActive ? "MeshRenderer GameObject MeshFilter" : "MeshRenderer GameObject MeshFilter (inactive)"));
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

        // --- Lazy Connections Pass & SQLite Staging ---

        private static bool IsMeaningfulLazyConnectionHandleType(ClassIDType type)
        {
            return (LazyConnectionReferenceTypes.Contains(type) && type != ClassIDType.AssetBundle)
                || type == ClassIDType.Mesh
                || type == ClassIDType.MonoBehaviour;
        }

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
            var arena = projectIndex.Arena;
            var count = arena.Count;
            var rememberedOriginIds = rememberHandleSourcePath != null ? new HashSet<int>() : null;
            var resolvedOriginIds = new HashSet<int>();

            for (var i = 0; i < count; i++)
            {
                var origin = arena.GetOrigin(i);
                var originId = origin.OriginId;

                var alreadyResolved = resolvedOriginIds.Contains(originId);
                if (alreadyResolved && (rememberedOriginIds == null || rememberedOriginIds.Contains(originId)))
                {
                    continue;
                }

                AssetHandle? materializedHandle = null;
                if (rememberedOriginIds != null && rememberedOriginIds.Add(originId))
                {
                    materializedHandle = arena.MaterializeHandle(i);
                    rememberHandleSourcePath!(materializedHandle);
                }

                if (alreadyResolved)
                {
                    continue;
                }

                // Skip pure media/leaf bundles containing only AssetBundle + Texture/Audio/Text/Shader/Sprite
                // with zero reference types, zero Mesh, and zero MonoBehaviour (guaranteed zero semantic rows).
                if (!IsMeaningfulLazyConnectionHandleType(arena.GetType(i)))
                {
                    continue;
                }

                materializedHandle ??= arena.MaterializeHandle(i);
                var sourcePath = resolveHandleSourcePath(materializedHandle);
                if (!string.IsNullOrWhiteSpace(origin.OriginalPath) || !string.IsNullOrWhiteSpace(sourcePath))
                {
                    resolvedOriginIds.Add(originId);
                }

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
            const int batchSize = 24;
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
                    await assetsManager.LoadFilesForConnectionsAsync(batch.ToArray());

                    var filesBySource = ResolveLoadedFilesForConnectionBatch(assetsManager, batch);
                    var materializationResults = new SourceConnectionMaterializationResult?[batch.Count];
                    var sourceRelations = new SemanticAssetRelations?[batch.Count];
                    var sourceExceptions = new Exception?[batch.Count];

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

                    // ClearLoadedFilesKeepIndex() in finally unloads all non-kept files immediately.
                    // Only run per-object unload for files pinned alive by ShouldKeepFileCallback.
                    var keepFileCallback = assetsManager.ShouldKeepFileCallback;
                    if (keepFileCallback != null)
                    {
                        for (var j = 0; j < batch.Count; j++)
                        {
                            var filesForSource = filesBySource[j];
                            if (filesForSource.Count == 0)
                            {
                                continue;
                            }

                            List<SerializedFile>? keptFiles = null;
                            for (var f = 0; f < filesForSource.Count; f++)
                            {
                                var file = filesForSource[f];
                                if (file != null && keepFileCallback(file.fileName))
                                {
                                    (keptFiles ??= new List<SerializedFile>()).Add(file);
                                }
                            }

                            if (keptFiles != null)
                            {
                                try
                                {
                                    UnloadConnectionBuildObjects(assetsManager, keptFiles, isHandlePinnedForPreview);
                                }
                                catch
                                {
                                    // Ignore best-effort unload errors.
                                }
                            }
                        }
                    }

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
                if (sourceFile?.reader == null || sourceFile.m_Objects == null || sourceFile.m_Objects.Count == 0)
                {
                    continue;
                }

                FileReader? localReader = null;
                try
                {
                    var objectInfos = sourceFile.m_Objects;
                    for (var idx = 0; idx < objectInfos.Count; idx++)
                    {
                        var objectInfo = objectInfos[idx];
                        var classId = ClassIDTypeHelper.FromClassId(objectInfo.classID);
                        if (!LazyConnectionReferenceTypes.Contains(classId))
                        {
                            continue;
                        }

                        stats.CandidateHandles++;
                        if (sourceFile.TryGetObject(objectInfo.m_PathID, out var existing) && existing != null)
                        {
                            stats.ResolvedByType.TryGetValue(classId, out var existingCount);
                            stats.ResolvedByType[classId] = existingCount + 1;
                            continue;
                        }

                        localReader ??= sourceFile.reader.Clone();
                        try
                        {
                            var objectReader = new ObjectReader(localReader, sourceFile, objectInfo);
                            var obj = AssetsManager.CreateObjectFromReader(objectReader);
                            sourceFile.AddObject(obj);

                            stats.ResolvedByType.TryGetValue(classId, out var count);
                            stats.ResolvedByType[classId] = count + 1;
                        }
                        catch (Exception ex)
                        {
                            stats.FailedObjects++;
                            Logger.Error($"Error materializing object {classId} PathID={objectInfo.m_PathID}", ex);
                        }
                    }
                }
                finally
                {
                    localReader?.Dispose();
                }
            }

            stats.ObjectsAfter = filesForSource.Sum(file => file.Objects.Count);
            return stats;
        }

        private static string NormalizeSemanticFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static List<SerializedFile>[] ResolveLoadedFilesForConnectionBatch(
            AssetsManager assetsManager,
            IReadOnlyList<string> batch)
        {
            var results = new List<SerializedFile>[batch.Count];
            var byNormalizedPath = new Dictionary<string, List<SerializedFile>>(StringComparer.OrdinalIgnoreCase);
            List<SerializedFile> loadedSnapshot;
            lock (assetsManager.loadLock)
            {
                loadedSnapshot = new List<SerializedFile>(assetsManager.assetsFileList);
            }

            for (var f = 0; f < loadedSnapshot.Count; f++)
            {
                var file = loadedSnapshot[f];
                if (file == null)
                {
                    continue;
                }

                var normOrig = NormalizeSemanticFullPath(file.originalPath);
                if (!string.IsNullOrEmpty(normOrig))
                {
                    if (!byNormalizedPath.TryGetValue(normOrig, out var list))
                    {
                        list = new List<SerializedFile>(1);
                        byNormalizedPath[normOrig] = list;
                    }
                    list.Add(file);
                }

                var normFull = NormalizeSemanticFullPath(file.fullName);
                if (!string.IsNullOrEmpty(normFull)
                    && !string.Equals(normFull, normOrig, StringComparison.OrdinalIgnoreCase))
                {
                    if (!byNormalizedPath.TryGetValue(normFull, out var list))
                    {
                        list = new List<SerializedFile>(1);
                        byNormalizedPath[normFull] = list;
                    }
                    list.Add(file);
                }
            }

            var arena = assetsManager.ProjectIndex?.Arena;
            for (var j = 0; j < batch.Count; j++)
            {
                var normBatchPath = NormalizeSemanticFullPath(batch[j]);
                if (!string.IsNullOrEmpty(normBatchPath)
                    && byNormalizedPath.TryGetValue(normBatchPath, out var matched))
                {
                    results[j] = matched;
                    continue;
                }

                // Fallback: match against loaded files whose indexed origin matches batch[j] (O(batchSize), never scanning all 2M handles).
                List<SerializedFile>? fallbackMatches = null;
                if (arena != null && !string.IsNullOrEmpty(normBatchPath))
                {
                    for (var f = 0; f < loadedSnapshot.Count; f++)
                    {
                        var file = loadedSnapshot[f];
                        if (file == null || string.IsNullOrEmpty(file.fileName))
                        {
                            continue;
                        }

                        var indices = arena.GetIndicesForFile(file.fileName);
                        if (indices.Length > 0)
                        {
                            var origin = arena.GetOrigin(indices[0]);
                            if (string.Equals(NormalizeSemanticFullPath(origin.OriginalPath), normBatchPath, StringComparison.OrdinalIgnoreCase))
                            {
                                (fallbackMatches ??= new List<SerializedFile>(1)).Add(file);
                            }
                        }
                    }
                }

                results[j] = fallbackMatches ?? new List<SerializedFile>(0);
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

            public static RendererMeshSemanticBinding FromSkinned(SerializedFile sourceFile, SkinnedMeshSemanticBinding binding)
            {
                var isActive = IsRendererActiveInHierarchy(sourceFile, binding.Renderer, binding.GameObject);
                return new RendererMeshSemanticBinding(
                    binding.Renderer,
                    GetSemanticAssetId(binding.Renderer),
                    "SkinnedMeshRenderer",
                    binding.Mesh,
                    binding.MeshId,
                    binding.GameObject,
                    binding.MeshFileId,
                    binding.MeshPathId,
                    isActive ? 100 : 40,
                    isActive ? "SkinnedMeshRenderer hierarchy" : "SkinnedMeshRenderer hierarchy (inactive)");
            }
        }

        internal readonly struct ResolvedSemanticPPtr
        {
            public ResolvedSemanticPPtr(ClassIDType type, string uniqueId)
            {
                Type = type;
                UniqueId = uniqueId;
            }

            public ClassIDType Type { get; }
            public string UniqueId { get; }
            public bool IsValid => !string.IsNullOrEmpty(UniqueId);
        }

        private sealed class FileSemanticLookupContext
        {
            private readonly SerializedFile?[]? _loadedExternals;
            private readonly (string? Cand1, string? Cand2, string? Cand3)[]? _externalNameCandidates;
            private Dictionary<(int FileId, long PathId), (ResolvedSemanticPPtr Resolved, int ArenaIndex)>? _externalPPtrCache;
            private AssetHandleOrigin? _localOrigin;
            private string? _localUniqueIdPrefix;

            public FileSemanticLookupContext(SerializedFile sourceFile)
            {
                SourceFile = sourceFile;

                var externals = sourceFile.m_Externals;
                if (externals != null && externals.Count > 0)
                {
                    _externalNameCandidates = new (string?, string?, string?)[externals.Count];
                    for (var i = 0; i < externals.Count; i++)
                    {
                        var ext = externals[i];
                        if (ext != null)
                        {
                            GetExternalFileNameCandidates(ext, out var c1, out var c2, out var c3);
                            _externalNameCandidates[i] = (c1, c2, c3);
                        }
                    }

                    var assetsManager = sourceFile.assetsManager;
                    if (assetsManager != null)
                    {
                        _loadedExternals = new SerializedFile?[externals.Count];
                        lock (assetsManager.loadLock)
                        {
                            var assetsFileList = assetsManager.assetsFileList;
                            if (assetsFileList.Count > 0)
                            {
                                var byFileName = new Dictionary<string, SerializedFile>(assetsFileList.Count * 2, StringComparer.OrdinalIgnoreCase);
                                for (var f = 0; f < assetsFileList.Count; f++)
                                {
                                    var candidate = assetsFileList[f];
                                    if (candidate == null)
                                    {
                                        continue;
                                    }

                                    var fn = GetSemanticFileName(candidate.fileName);
                                    if (!string.IsNullOrEmpty(fn))
                                    {
                                        byFileName.TryAdd(fn, candidate);
                                    }

                                    var origFn = GetSemanticFileName(candidate.originalPath);
                                    if (!string.IsNullOrEmpty(origFn))
                                    {
                                        byFileName.TryAdd(origFn, candidate);
                                    }

                                    var fullFn = GetSemanticFileName(candidate.fullName);
                                    if (!string.IsNullOrEmpty(fullFn))
                                    {
                                        byFileName.TryAdd(fullFn, candidate);
                                    }
                                }

                                for (var i = 0; i < externals.Count; i++)
                                {
                                    var ext = externals[i];
                                    if (ext == null)
                                    {
                                        continue;
                                    }

                                    var extFile = GetSemanticFileName(ext.fileName);
                                    if (!string.IsNullOrEmpty(extFile) && byFileName.TryGetValue(extFile, out var matched))
                                    {
                                        _loadedExternals[i] = matched;
                                        continue;
                                    }

                                    var extPathFile = GetSemanticFileName(ext.pathName);
                                    if (!string.IsNullOrEmpty(extPathFile) && byFileName.TryGetValue(extPathFile, out matched))
                                    {
                                        _loadedExternals[i] = matched;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            public SerializedFile SourceFile { get; }

            public AssetHandleOrigin LocalOrigin => _localOrigin ??= AssetHandleOrigin.Intern(SourceFile);

            public string BuildLocalUniqueId(long pathId)
            {
                _localUniqueIdPrefix ??= LocalOrigin.UniqueIdPrefix + "#";
                return string.Concat(_localUniqueIdPrefix, pathId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            public SerializedFile? GetLoadedExternalFile(int fileId)
            {
                var idx = fileId - 1;
                return _loadedExternals != null && (uint)idx < (uint)_loadedExternals.Length
                    ? _loadedExternals[idx]
                    : null;
            }

            public (string? Cand1, string? Cand2, string? Cand3) GetExternalNameCandidates(int fileId)
            {
                var idx = fileId - 1;
                return _externalNameCandidates != null && (uint)idx < (uint)_externalNameCandidates.Length
                    ? _externalNameCandidates[idx]
                    : default;
            }

            public bool TryGetCachedExternalPPtr(int fileId, long pathId, out (ResolvedSemanticPPtr Resolved, int ArenaIndex) entry)
            {
                if (_externalPPtrCache != null && _externalPPtrCache.TryGetValue((fileId, pathId), out entry))
                {
                    return true;
                }

                entry = default;
                return false;
            }

            public void CacheExternalPPtr(int fileId, long pathId, ResolvedSemanticPPtr resolved, int arenaIndex)
            {
                (_externalPPtrCache ??= new Dictionary<(int, long), (ResolvedSemanticPPtr, int)>())[(fileId, pathId)] = (resolved, arenaIndex);
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

            private List<RendererMeshSemanticBinding> ConfidenceSource
            {
                get
                {
                    var active = Bindings
                        .Where(binding => string.IsNullOrEmpty(binding.ConfidenceReason)
                            || !binding.ConfidenceReason.Contains("(inactive)", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    return active.Count > 0 ? active : Bindings;
                }
            }

            public int Confidence
            {
                get
                {
                    var source = ConfidenceSource;
                    return source.Count == 0 ? 0 : source.Min(binding => binding.Confidence);
                }
            }

            public string ConfidenceReason
            {
                get
                {
                    var source = ConfidenceSource;
                    return source.Count == 0
                        ? string.Empty
                        : source.All(binding => binding.ConfidenceReason == source[0].ConfidenceReason)
                            ? source[0].ConfidenceReason
                            : "Mixed renderer hierarchy evidence";
                }
            }

            public void Add(RendererMeshSemanticBinding binding)
            {
                Bindings.Add(binding);
            }
        }
    }
}
