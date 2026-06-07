using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private struct BuildAssetStructuresResult
        {
            public string? ProductName;
            public List<AssetItem> ExportableAssets;
            public List<AssetItem> NewExportableAssets;
            public List<GameObjectNode> SceneTreeNodes;
            public Dictionary<AssetStudio.Object, AssetItem> ObjectToAssetItemCache;
            public Dictionary<Mesh, List<Material?>> MeshToMaterialsCache;
            public Dictionary<Mesh, List<string>> MeshAssociatedRenderersCache;
            public Dictionary<Mesh, HashSet<string>> MeshSourceTypesCache;
            public Dictionary<Material, Texture2D?> MaterialMainTextureCache;
            public Dictionary<Material, Material?> MaterialPreviewMaterialCache;
            public Dictionary<Material, Dictionary<string, Texture2D?>> MaterialTextureSlotsCache;
            public SemanticAssetRelations SemanticRelations;
            public Dictionary<AnimationClip, Avatar?> AnimationClipAvatarCache;
            public Dictionary<Avatar, Mesh?> AvatarMeshCache;
            public Dictionary<Mesh, Avatar?> MeshAvatarCache;
            public Dictionary<AnimationClip, HashSet<uint>> AnimationClipTransformBindingsCache;
            public List<AssetClassItem> AssetClassItems;
            public Dictionary<string, AssetItem> LazyAssetItemsByHandleId;
            public HashSet<string> ExportableAssetHandleIds;
            public HashSet<ClassIDType> ExportableAssetTypes;
        }

        private static ParallelOptions CreateStructureBuildParallelOptions()
        {
            return new ParallelOptions
            {
                MaxDegreeOfParallelism = AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_STRUCTURE_THREADS", 0.4)
            };
        }

        private static bool IsLazyExportableType(ClassIDType type)
        {
            return type
                is ClassIDType.Texture2D
                or ClassIDType.AudioClip
                or ClassIDType.VideoClip
                or ClassIDType.VideoPlayer
                or ClassIDType.Shader
                or ClassIDType.Mesh
                or ClassIDType.Material
                or ClassIDType.TextAsset
                or ClassIDType.MonoBehaviour
                or ClassIDType.Font
                or ClassIDType.Sprite
                or ClassIDType.MovieTexture
                or ClassIDType.AnimationClip
                or ClassIDType.Animator
                or ClassIDType.Avatar
                or ClassIDType.AnimatorController
                or ClassIDType.AnimatorOverrideController
                or ClassIDType.RuntimeAnimatorController;
        }

        private static void BuildLazyAssetItemsBackground(
            IReadOnlyList<AssetHandle> handles,
            bool displayAllChecked,
            Dictionary<string, AssetItem> pathIDAssetItemDic,
            Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic,
            List<AssetItem> exportableAssets,
            List<AssetItem> newExportableAssets)
        {
            var assetItems = new AssetItem?[handles.Count];
            var includeItems = new bool[handles.Count];
            var newItems = new bool[handles.Count];

            Parallel.For(0, handles.Count, CreateStructureBuildParallelOptions(), index =>
            {
                var handle = handles[index];
                AssetItem assetItem;
                bool isNewHandle = false;
                if (handle.Tag is AssetItem existingItem)
                {
                    assetItem = existingItem;
                }
                else
                {
                    assetItem = new AssetItem(handle);
                    handle.Tag = assetItem;
                    isNewHandle = true;
                }

                if (string.IsNullOrEmpty(assetItem.UniqueID))
                {
                    assetItem.UniqueID = " #" + index.ToString(CultureInfo.InvariantCulture);
                }
                assetItems[index] = assetItem;
                includeItems[index] = displayAllChecked || IsLazyExportableType(handle.Type);
                newItems[index] = isNewHandle;
            });

            for (int index = 0; index < handles.Count; index++)
            {
                var handle = handles[index];
                var assetItem = assetItems[index];
                if (assetItem == null)
                {
                    continue;
                }

                pathIDAssetItemDic[handle.UniqueID] = assetItem;
                if (handle.RealObject != null)
                {
                    objectAssetItemDic[handle.RealObject] = assetItem;
                }

                if (!includeItems[index])
                {
                    continue;
                }

                exportableAssets.Add(assetItem);
                if (newItems[index])
                {
                    newExportableAssets.Add(assetItem);
                }
            }
        }

        private static void BuildEagerAssetItemsBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            bool displayAllChecked,
            Dictionary<GameObject, GameObjectNode> treeNodeDictionary,
            Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic,
            Dictionary<string, AssetItem> pathIDAssetItemDic,
            List<(PPtr<AssetStudio.Object>, string)> containers,
            List<AssetItem> exportableAssets,
            List<GameObjectNode> sceneTreeNodes,
            out string? productName)
        {
            var workItems = new List<EagerAssetWorkItem>();
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    workItems.Add(new EagerAssetWorkItem(assetsFile, asset, workItems.Count));
                }
            }

            var records = new EagerAssetRecord[workItems.Count];
            Parallel.For(0, workItems.Count, CreateStructureBuildParallelOptions(), index =>
            {
                records[index] = BuildEagerAssetRecord(workItems[index], displayAllChecked);
            });

            productName = null;
            for (int index = 0; index < records.Length; index++)
            {
                var record = records[index];
                var item = record.Item;
                objectAssetItemDic[record.Asset] = item;
                pathIDAssetItemDic[AssetHandle.BuildUniqueID(record.File, record.Asset.m_PathID)] = item;

                if (record.GameObject != null && !treeNodeDictionary.ContainsKey(record.GameObject))
                {
                    treeNodeDictionary[record.GameObject] = new GameObjectNode
                    {
                        Name = record.GameObject.m_Name,
                        GameObject = record.GameObject
                    };
                }

                if (record.Containers != null)
                {
                    containers.AddRange(record.Containers);
                }

                if (productName == null && !string.IsNullOrEmpty(record.ProductName))
                {
                    productName = record.ProductName;
                }

                if (record.IncludeInExportable)
                {
                    exportableAssets.Add(item);
                }
            }

            SerializedFile? currentFile = null;
            GameObjectNode? fileNode = null;
            for (int index = 0; index < records.Length; index++)
            {
                var record = records[index];
                if (!ReferenceEquals(currentFile, record.File))
                {
                    if (fileNode?.ChildCount > 0)
                    {
                        sceneTreeNodes.Add(fileNode);
                    }

                    currentFile = record.File;
                    fileNode = new GameObjectNode { Name = record.File.fileName };
                }

                if (record.GameObject == null || fileNode == null)
                {
                    continue;
                }

                var currentNode = treeNodeDictionary[record.GameObject];
                var parentNode = fileNode;

                if (record.GameObject.m_Transform != null
                    && record.GameObject.m_Transform.m_Father.TryGet(out var father)
                    && father.m_GameObject.TryGet(out var parentGameObject)
                    && treeNodeDictionary.TryGetValue(parentGameObject, out var parentGameObjectNode))
                {
                    parentNode = parentGameObjectNode;
                }

                parentNode.AddChild(currentNode);
            }

            if (fileNode?.ChildCount > 0)
            {
                sceneTreeNodes.Add(fileNode);
            }
        }

        private static EagerAssetRecord BuildEagerAssetRecord(EagerAssetWorkItem workItem, bool displayAllChecked)
        {
            var asset = workItem.Asset;
            var assetItem = new AssetItem(asset)
            {
                UniqueID = " #" + workItem.Index.ToString(CultureInfo.InvariantCulture)
            };
            var exportable = false;
            string? productName = null;
            GameObject? gameObject = null;
            List<(PPtr<AssetStudio.Object>, string)>? containers = null;

            switch (asset)
            {
                case GameObject m_GameObject:
                    assetItem.Name = m_GameObject.m_Name;
                    gameObject = m_GameObject;
                    break;
                case Texture2D m_Texture2D:
                    if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                    {
                        assetItem.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                    }
                    assetItem.Name = m_Texture2D.m_Name;
                    exportable = true;
                    break;
                case AudioClip m_AudioClip:
                    if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                    {
                        assetItem.FullSize = asset.byteSize + m_AudioClip.m_Size;
                    }
                    assetItem.Name = m_AudioClip.m_Name;
                    exportable = true;
                    break;
                case VideoClip m_VideoClip:
                    if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                    {
                        assetItem.FullSize = asset.byteSize + (long)m_VideoClip.m_ExternalResources.m_Size;
                    }
                    assetItem.Name = m_VideoClip.m_Name;
                    exportable = true;
                    break;
                case VideoPlayer m_VideoPlayer:
                    if (TryResolveLocalObject(workItem.File, m_VideoPlayer.m_VideoClip, out VideoClip? resolvedClip) && resolvedClip != null)
                    {
                        if (!string.IsNullOrEmpty(resolvedClip.m_OriginalPath))
                        {
                            assetItem.FullSize = asset.byteSize + (long)resolvedClip.m_ExternalResources.m_Size;
                        }
                        assetItem.Name = $"{(TryResolveLocalObject(workItem.File, m_VideoPlayer.m_GameObject, out GameObject? go) && go != null ? go.m_Name : "VideoPlayer")} (Clip: {resolvedClip.m_Name})";
                    }
                    else
                    {
                        assetItem.Name = TryResolveLocalObject(workItem.File, m_VideoPlayer.m_GameObject, out GameObject? go) && go != null
                            ? go.m_Name
                            : "VideoPlayer";
                    }
                    exportable = true;
                    break;
                case Shader m_Shader:
                    assetItem.Name = m_Shader.m_ParsedForm?.m_Name ?? m_Shader.m_Name;
                    exportable = true;
                    break;
                case Mesh _:
                case Material _:
                case TextAsset _:
                case AnimationClip _:
                case Font _:
                case MovieTexture _:
                case Sprite _:
                case Avatar _:
                case RuntimeAnimatorController _:
                    assetItem.Name = ((NamedObject)asset).m_Name;
                    exportable = true;
                    break;
                case MonoScript m_MonoScript:
                    assetItem.Name = m_MonoScript.m_Name;
                    exportable = true;
                    break;
                case Animator m_Animator:
                    if (TryResolveLocalObject(workItem.File, m_Animator.m_GameObject, out GameObject? animatorGameObject) && animatorGameObject != null)
                    {
                        assetItem.Name = animatorGameObject.m_Name;
                    }
                    exportable = true;
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    if (m_MonoBehaviour.m_Name == ""
                        && TryResolveLocalObject(workItem.File, m_MonoBehaviour.m_Script, out MonoScript? script)
                        && script != null)
                    {
                        assetItem.Name = script.m_ClassName;
                    }
                    else
                    {
                        assetItem.Name = m_MonoBehaviour.m_Name;
                    }
                    exportable = true;
                    break;
                case AssetBundle m_AssetBundle:
                    containers = CollectAssetBundleContainers(m_AssetBundle);
                    assetItem.Name = m_AssetBundle.m_Name;
                    break;
                case ResourceManager m_ResourceManager:
                    containers = CollectResourceManagerContainers(m_ResourceManager);
                    break;
                case PlayerSettings m_PlayerSettings:
                    productName = m_PlayerSettings.productName;
                    break;
                case NamedObject m_NamedObject:
                    assetItem.Name = m_NamedObject.m_Name;
                    break;
            }

            if (string.IsNullOrEmpty(assetItem.Name))
            {
                assetItem.Name = assetItem.TypeString + assetItem.UniqueID;
            }

            return new EagerAssetRecord(
                workItem.File,
                asset,
                assetItem,
                gameObject,
                containers,
                productName,
                displayAllChecked || exportable);
        }

        private static List<(PPtr<AssetStudio.Object>, string)> CollectAssetBundleContainers(AssetBundle assetBundle)
        {
            var containers = new List<(PPtr<AssetStudio.Object>, string)>();
            foreach (var container in assetBundle.m_Container)
            {
                var preloadIndex = container.Value.preloadIndex;
                var preloadSize = container.Value.preloadSize;
                var preloadEnd = preloadIndex + preloadSize;
                for (int k = preloadIndex; k < preloadEnd; k++)
                {
                    containers.Add((assetBundle.m_PreloadTable[k], container.Key));
                }
            }

            return containers;
        }

        private static List<(PPtr<AssetStudio.Object>, string)> CollectResourceManagerContainers(ResourceManager resourceManager)
        {
            var containers = new List<(PPtr<AssetStudio.Object>, string)>();
            foreach (var container in resourceManager.m_Container)
            {
                containers.Add((container.Value, container.Key));
            }

            return containers;
        }

        private static bool TryResolveLocalObject<T>(SerializedFile file, PPtr<T> pointer, out T? result)
            where T : AssetStudio.Object
        {
            result = null;
            if (pointer.m_FileID != 0)
            {
                return false;
            }

            if (file.ObjectsDic.TryGetValue(pointer.m_PathID, out var obj) && obj is T typed)
            {
                result = typed;
                return true;
            }

            return false;
        }

        private readonly record struct EagerAssetWorkItem(SerializedFile File, AssetStudio.Object Asset, int Index);

        private sealed record EagerAssetRecord(
            SerializedFile File,
            AssetStudio.Object Asset,
            AssetItem Item,
            GameObject? GameObject,
            List<(PPtr<AssetStudio.Object>, string)>? Containers,
            string? ProductName,
            bool IncludeInExportable);

        private static void LinkAssetItemsToSceneNodesBackground(
            List<SerializedFile> assetsFileList,
            Dictionary<GameObject, GameObjectNode> treeNodeDictionary,
            Dictionary<Object, AssetItem> objectAssetItemDic)
        {
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    if (asset is not GameObject gameObject || !treeNodeDictionary.TryGetValue(gameObject, out var node))
                    {
                        continue;
                    }

                    if (objectAssetItemDic.TryGetValue(gameObject, out var gameObjectItem))
                    {
                        gameObjectItem.TreeNode = node;
                    }

                    foreach (var pptr in gameObject.m_Components)
                    {
                        if (!pptr.TryGet(out var component))
                        {
                            continue;
                        }

                        if (objectAssetItemDic.TryGetValue(component, out var componentItem))
                        {
                            componentItem.TreeNode = node;
                        }

                        if (component is MeshFilter meshFilter
                            && meshFilter.m_Mesh.TryGet(out var mesh)
                            && objectAssetItemDic.TryGetValue(mesh, out var meshItem))
                        {
                            meshItem.TreeNode = node;
                        }
                        else if (component is SkinnedMeshRenderer skinnedMeshRenderer
                            && skinnedMeshRenderer.m_Mesh.TryGet(out var skinnedMesh)
                            && objectAssetItemDic.TryGetValue(skinnedMesh, out var skinnedMeshItem))
                        {
                            skinnedMeshItem.TreeNode = node;
                        }
                    }
                }
            }
        }

        private static void LinkFbxSubAssetsToSceneNodesBackground(
            List<AssetItem> localExportableAssets,
            List<GameObjectNode> localSceneTreeNodes)
        {
            var fbxNodes = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in localExportableAssets)
            {
                if (item.TreeNode?.GameObject == null)
                {
                    continue;
                }

                var fbxContainer = GetFbxContainerPath(item.Container);
                if (fbxContainer == null)
                {
                    continue;
                }

                fbxNodes.TryAdd(fbxContainer, GetFbxRootNode(item.TreeNode, fbxContainer));
            }

            foreach (var item in localExportableAssets)
            {
                var fbxContainer = GetFbxContainerPath(item.Container);
                if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
                {
                    continue;
                }

                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                var node = FindSceneNodeByNameBackground(localSceneTreeNodes, fbxName);
                if (node?.GameObject != null)
                {
                    fbxNodes[fbxContainer] = node;
                }
            }

            foreach (var item in localExportableAssets)
            {
                var fbxContainer = GetFbxContainerPath(item.Container);
                if (fbxContainer == null || !fbxNodes.TryGetValue(fbxContainer, out var node))
                {
                    continue;
                }

                item.TreeNode = node;
                if (item.Asset is Mesh or Animator)
                {
                    var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                    if (!string.IsNullOrEmpty(fbxName))
                    {
                        item.Name = fbxName;
                    }
                }
            }
        }

        private static GameObjectNode? FindSceneNodeByNameBackground(List<GameObjectNode> localSceneTreeNodes, string name)
        {
            foreach (var root in localSceneTreeNodes)
            {
                var match = FindNodeByName(root, name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

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

            void AddMeshMaterials(Mesh mesh, List<Material?> materials)
            {
                localMeshToMaterialsCache[mesh] = localMeshToMaterialsCache.TryGetValue(mesh, out var existingList)
                    ? MergeMeshMaterialLists(existingList, materials)
                    : new List<Material?>(materials);
            }

            void AddMeshAssociation(Mesh mesh, string sourceType, string? description)
            {
                if (!localMeshSourceTypesCache.TryGetValue(mesh, out var sourceTypes))
                {
                    sourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    localMeshSourceTypesCache[mesh] = sourceTypes;
                }
                sourceTypes.Add(sourceType);

                if (string.IsNullOrEmpty(description))
                {
                    return;
                }

                if (!localMeshAssociatedRenderersCache.TryGetValue(mesh, out var renderers))
                {
                    renderers = new List<string>();
                    localMeshAssociatedRenderersCache[mesh] = renderers;
                }

                renderers.Add(description);
            }

            var fileResults = new AssetReferenceIndexBuildResult?[assetsFileList.Count];
            Parallel.For(0, assetsFileList.Count, CreateStructureBuildParallelOptions(), index =>
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
                    AddMeshMaterials(entry.Key, entry.Value);
                }

                foreach (var entry in fileResult.MeshSourceTypesCache)
                {
                    foreach (var sourceType in entry.Value)
                    {
                        AddMeshAssociation(entry.Key, sourceType, null);
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

        private static AssetReferenceIndexBuildResult BuildAssetReferenceIndexForFile(SerializedFile file)
        {
            var result = new AssetReferenceIndexBuildResult();

            void AddMeshMaterials(Mesh mesh, List<Material?> materials)
            {
                result.MeshToMaterialsCache[mesh] = result.MeshToMaterialsCache.TryGetValue(mesh, out var existingList)
                    ? MergeMeshMaterialLists(existingList, materials)
                    : new List<Material?>(materials);
            }

            void AddMeshAssociation(Mesh mesh, string sourceType, string? description)
            {
                if (!result.MeshSourceTypesCache.TryGetValue(mesh, out var sourceTypes))
                {
                    sourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result.MeshSourceTypesCache[mesh] = sourceTypes;
                }
                sourceTypes.Add(sourceType);

                if (string.IsNullOrEmpty(description))
                {
                    return;
                }

                if (!result.MeshAssociatedRenderersCache.TryGetValue(mesh, out var renderers))
                {
                    renderers = new List<string>();
                    result.MeshAssociatedRenderersCache[mesh] = renderers;
                }

                renderers.Add(description);
            }

            void AddMaterialTextureRelationsForBindings(IEnumerable<ResolvedRendererMaterialBinding> bindings)
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

            bool AddMeshRendererRelations(
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
                    AddMeshAssociation(mesh, "MeshFilter", description);
                }

                if (renderer.m_Materials != null)
                {
                    var resolvedBindings = ResolveRendererMaterialBindings(file, renderer);
                    AddMaterialTextureRelationsForBindings(resolvedBindings);
                    var materialIds = resolvedBindings.Select(binding => binding.MaterialId).ToList();
                    foreach (var binding in resolvedBindings)
                    {
                        AddMeshMaterialRelation(
                            result.SemanticRelations,
                            meshId,
                            binding.MaterialId,
                            renderer,
                            "MeshRenderer",
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
                        AddMeshMaterials(mesh, BuildSubMeshMaterialList(resolvedBindings));
                    }
                }

                return true;
            }

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
                    IndexMaterialTexturesBackground(material, result.MaterialPreviewMaterialCache, result.MaterialTextureSlotsCache, result.MaterialMainTextureCache);
                    AddMaterialTextureRelations(result.SemanticRelations, material, result.MaterialPreviewMaterialCache, result.MaterialTextureSlotsCache, result.MaterialMainTextureCache);
                }
                else if (obj is SkinnedMeshRenderer smr)
                {
                    var smrMesh = ResolveMeshBackground(file, smr.m_Mesh);
                    var smrMeshId = GetSemanticAssetId(smrMesh);
                    if (string.IsNullOrEmpty(smrMeshId))
                    {
                        smrMeshId = GetSemanticAssetIdFromPPtr(file, smr.m_Mesh, ClassIDType.Mesh);
                    }

                    if (!string.IsNullOrEmpty(smrMeshId))
                    {
                        var go = ResolveGameObjectBackground(file, smr.m_GameObject);
                        AddRendererMeshRelation(result.SemanticRelations, smrMeshId, smr, "SkinnedMeshRenderer", go,
                            go != null ? $"SkinnedMeshRenderer on GameObject \"{go.m_Name}\" (PathID: {smr.m_PathID})" : string.Empty);
                        AddAssetEdge(result.SemanticRelations, smr, "Mesh", "m_Mesh", 0, smrMeshId, smr.m_Mesh.m_FileID, smr.m_Mesh.m_PathID);

                        if (smrMesh != null)
                        {
                            AddMeshAssociation(
                                smrMesh,
                                "SkinnedMeshRenderer",
                                go != null ? $"SkinnedMeshRenderer on GameObject \"{go.m_Name}\" (PathID: {smr.m_PathID})" : null);
                        }

                        if (smr.m_Materials != null)
                        {
                            var resolvedBindings = ResolveRendererMaterialBindings(file, smr);
                            AddMaterialTextureRelationsForBindings(resolvedBindings);
                            var materialIds = resolvedBindings.Select(binding => binding.MaterialId).ToList();
                            foreach (var binding in resolvedBindings)
                            {
                                AddMeshMaterialRelation(
                                    result.SemanticRelations,
                                    smrMeshId,
                                    binding.MaterialId,
                                    smr,
                                    "SkinnedMeshRenderer",
                                    binding.Binding.SubMeshIndex,
                                    binding.Binding.MaterialSlotIndex,
                                    materialIds);
                                AddAssetEdge(
                                    result.SemanticRelations,
                                    smr,
                                    "Material",
                                    "m_Materials",
                                    binding.Binding.MaterialSlotIndex,
                                    binding.MaterialId,
                                    binding.Binding.MaterialPointer?.m_FileID ?? 0,
                                    binding.Binding.MaterialPointer?.m_PathID ?? 0);
                            }

                            if (smrMesh != null)
                            {
                                AddMeshMaterials(smrMesh, BuildSubMeshMaterialList(resolvedBindings));
                            }
                        }
                    }
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
                else if (obj is MeshRenderer mr)
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
            }

            AddSceneObjectModelGroupRelations(
                file,
                result.SemanticRelations,
                skinnedMeshBindings.Select(binding => RendererMeshSemanticBinding.FromSkinned(binding))
                    .Concat(meshRendererBindings));
            AddContainerMaterialRelations(result.SemanticRelations, file, containerReferences);
            return result;
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

                var preloadStart = Math.Max(0, assetInfo?.preloadIndex ?? 0);
                var preloadEnd = Math.Min(assetBundle.m_PreloadTable.Length, preloadStart + Math.Max(0, assetInfo?.preloadSize ?? 0));
                for (var i = preloadStart; i < preloadEnd; i++)
                {
                    var reference = assetBundle.m_PreloadTable[i];
                    if (reference != null && !reference.IsNull)
                    {
                        references.Add(reference);
                    }
                }

                if (references.Count > 0)
                {
                    containerReferences.Add((entry.Key ?? string.Empty, references));
                }
            }
        }

        private static void BuildExportableAssetIndexesBackground(
            IEnumerable<AssetItem> exportableAssets,
            out Dictionary<string, AssetItem> lazyItemsByHandleId,
            out HashSet<string> exportableHandleIds,
            out HashSet<ClassIDType> exportableTypes)
        {
            lazyItemsByHandleId = new Dictionary<string, AssetItem>(StringComparer.Ordinal);
            exportableHandleIds = new HashSet<string>(StringComparer.Ordinal);
            exportableTypes = new HashSet<ClassIDType>();

            foreach (var item in exportableAssets)
            {
                if (item == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(item.Handle?.UniqueID))
                {
                    lazyItemsByHandleId[item.Handle.UniqueID] = item;
                    exportableHandleIds.Add(item.Handle.UniqueID);
                }

                exportableTypes.Add(item.Type);
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

        private static AssetHandle? GetSemanticHandleFromPPtr(SerializedFile sourceFile, PPtr<AssetStudio.Object> pptr)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null || pptr == null || pptr.IsNull)
            {
                return null;
            }

            if (pptr.TryGetAssetsFile(out var targetFile))
            {
                var handle = sourceFile.assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID));
                if (handle != null)
                {
                    return handle;
                }
            }

            if (pptr.m_FileID == 0)
            {
                return sourceFile.assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, pptr.m_PathID));
            }

            return FindSemanticHandleForPPtr(sourceFile, pptr.m_FileID, pptr.m_PathID, expectedType: null);
        }

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

            var meshMatches = FindSkinnedMeshesForAnimator(sourceFile, animatorGameObject, skinnedMeshBindings, animatorCount).ToList();
            AddAnimatorModelGroupRelations(
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
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
                if (string.IsNullOrEmpty(binding.MeshId) || !seenMeshes.Add(binding.MeshId))
                {
                    continue;
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
                    avatarId,
                    avatarFileId,
                    avatarPathId,
                    depth: 0);
            }
        }

        private static void AddAnimatorModelGroupRelations(
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
                return;
            }

            var groupId = BuildAnimatorModelGroupId(sourceFile, animator, animatorId, rootGameObjectId, avatarId, controllerId);
            if (string.IsNullOrEmpty(groupId))
            {
                return;
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
                    match.Confidence,
                    match.ConfidenceReason));
                slotIndex++;
            }
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

        private static string BuildAnimatorModelGroupId(
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

        private static bool IsSameOrDescendantGameObject(SerializedFile sourceFile, GameObject child, GameObject ancestor)
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

        private static Transform? ResolveTransformForGameObjectBackground(SerializedFile sourceFile, GameObject gameObject)
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

        private static Transform? ResolveTransformBackground(SerializedFile sourceFile, PPtr<Transform> pptr)
        {
            if (pptr.TryGet(out var transform))
            {
                return transform;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Transform : null;
        }

        private static Avatar? ResolveAvatarBackground(SerializedFile sourceFile, PPtr<Avatar> pptr)
        {
            if (pptr.TryGet(out var avatar))
            {
                return avatar;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Avatar : null;
        }

        private static RuntimeAnimatorController? ResolveRuntimeAnimatorControllerBackground(
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

        private static void AddAnimatorControllerClipRelations(
            SerializedFile fallbackSourceFile,
            SemanticAssetRelations relations,
            RuntimeAnimatorController controller,
            Animator animator,
            string avatarId,
            int avatarFileId,
            long avatarPathId,
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

                slotIndex++;
            }

            if (controller is AnimatorOverrideController overrideController
                && overrideController.m_Controller != null
                && !overrideController.m_Controller.IsNull)
            {
                var baseController = ResolveRuntimeAnimatorControllerBackground(controllerSourceFile, overrideController.m_Controller);
                if (baseController != null && !ReferenceEquals(baseController, controller))
                {
                    AddAnimatorControllerClipRelations(
                        controllerSourceFile,
                        relations,
                        baseController,
                        animator,
                        avatarId,
                        avatarFileId,
                        avatarPathId,
                        depth + 1);
                }
            }
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

        private static string GetSemanticGameObjectId(
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

        private static GameObject? ResolveGameObjectBackground(SerializedFile sourceFile, PPtr<GameObject> pptr)
        {
            if (pptr.TryGet(out var go))
            {
                return go;
            }
            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as GameObject : null;
        }

        private static Mesh? ResolveMeshBackground(SerializedFile sourceFile, PPtr<Mesh> pptr)
        {
            if (pptr.TryGet(out var mesh))
            {
                return mesh;
            }
            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Mesh : null;
        }

        private static Material? ResolveRendererMaterialBackground(PPtr<Material> pptr)
        {
            if (pptr.TryGet(out var material))
            {
                return material;
            }
            return null;
        }

        private static AssetStudio.Object? ResolveObjectBackground(SerializedFile sourceFile, long pathID)
        {
            lock (sourceFile)
            {
                if (sourceFile.ObjectsDic.TryGetValue(pathID, out var obj))
                {
                    return obj;
                }
            }

            return null;
        }

        private static string GetSemanticAssetId(AssetStudio.Object? asset)
        {
            return asset?.assetsFile != null
                ? AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID)
                : string.Empty;
        }

        private static string GetSemanticAssetIdFromPPtr<T>(SerializedFile sourceFile, PPtr<T> pptr, ClassIDType expectedType)
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

        private static AssetHandle? FindSemanticHandleForPPtr(SerializedFile sourceFile, int fileId, long pathId, ClassIDType? expectedType)
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

        private static bool IsSameSemanticSource(string? left, string? right)
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

        private readonly struct RendererMaterialBinding
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

        private readonly struct MeshFilterSemanticBinding
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

        private readonly struct SkinnedMeshSemanticBinding
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

        private readonly struct RendererMeshSemanticBinding
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

        private readonly struct GameObjectHierarchyEntry
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

        private readonly struct SkinnedMeshSemanticMatch
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

        private readonly struct ResolvedRendererMaterialBinding
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

        private static int ScoreMaterialIds(List<string> materialIds)
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

        private static string? SelectMainTextureSlotNameForRelations(Material displayMaterial)
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

        private static void BuildAnimationPreviewIndexesBackground(
            List<SerializedFile> assetsFileList,
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

            var animationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache[clip] = GetTransformBindingPathsBackground(clip);
            }

            var avatarMeshCache = new Dictionary<Avatar, Mesh?>(avatars.Length);
            foreach (var avatar in avatars)
            {
                avatarMeshCache[avatar] = FindBestMeshForAvatarBackground(avatar, meshes);
            }

            var meshAvatarCache = new Dictionary<Mesh, Avatar?>(meshes.Length);
            foreach (var mesh in meshes)
            {
                meshAvatarCache[mesh] = FindBestAvatarForMeshBackground(mesh, avatars);
            }

            var animationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths);
                animationClipAvatarCache[clip] = FindBestAvatarForAnimationClipBackground(clip, bindingPaths ?? new HashSet<uint>(), avatars);
            }

            animationClipAvatarCacheOut = animationClipAvatarCache;
            avatarMeshCacheOut = avatarMeshCache;
            meshAvatarCacheOut = meshAvatarCache;
            animationClipTransformBindingsCacheOut = animationClipTransformBindingsCache;
        }

        private static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip)
        {
            var result = new HashSet<uint>();
            var bindings = clip.m_ClipBindingConstant;
            if (bindings == null && clip.m_MuscleClip?.m_Clip != null)
            {
                bindings = clip.m_MuscleClip.m_Clip.ConvertValueArrayToGenericBinding();
            }

            if (bindings?.genericBindings != null)
            {
                foreach (var binding in bindings.genericBindings)
                {
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        result.Add(binding.path);
                    }
                }
            }

            return result;
        }

        private static Avatar? FindBestAvatarForAnimationClipBackground(AnimationClip clip, HashSet<uint> bindingPaths, Avatar[] avatars)
        {
            if (bindingPaths.Count == 0)
            {
                return null;
            }

            Avatar? bestAvatar = null;
            int bestScore = 0;
            var clipName = NormalizeAnimatorSearchKey(clip.m_Name);

            foreach (var avatar in avatars)
            {
                if (avatar.m_TOS == null || avatar.m_TOS.Length == 0)
                {
                    continue;
                }

                var avatarPathHashes = new HashSet<uint>(avatar.m_TOS.Select(x => x.Key));
                var overlap = bindingPaths.Count(avatarPathHashes.Contains);
                if (!IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap))
                {
                    continue;
                }

                var score = overlap * 100;
                if (avatar.assetsFile == clip.assetsFile) score += 20;

                var avatarName = NormalizeAnimatorSearchKey(avatar.m_Name.Replace("Avatar", string.Empty));
                if (!string.IsNullOrEmpty(avatarName) && clipName.Contains(avatarName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAvatar = avatar;
                }
            }

            return bestAvatar;
        }

        private static Mesh? FindBestMeshForAvatarBackground(Avatar avatar, Mesh[] meshes)
        {
            var avatarBoneIds = avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null
                ? new HashSet<uint>(avatar.m_Avatar.m_AvatarSkeleton.m_ID)
                : new HashSet<uint>();
            if (avatarBoneIds.Count == 0)
            {
                return null;
            }

            Mesh? bestMesh = null;
            int bestScore = 0;
            var avatarName = avatar.m_Name.Replace("Avatar", string.Empty).Trim();

            foreach (var mesh in meshes)
            {
                var overlap = mesh.m_BoneNameHashes.Count(avatarBoneIds.Contains);
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

            return bestMesh;
        }

        private static Avatar? FindBestAvatarForMeshBackground(Mesh mesh, Avatar[] avatars)
        {
            var meshBoneHashes = mesh.m_BoneNameHashes != null
                ? new HashSet<uint>(mesh.m_BoneNameHashes)
                : new HashSet<uint>();
            if (meshBoneHashes.Count == 0)
            {
                return null;
            }

            Avatar? bestAvatar = null;
            int bestScore = 0;
            var meshName = mesh.m_Name.ToLowerInvariant();

            foreach (var avatar in avatars)
            {
                if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID == null)
                {
                    continue;
                }

                var overlap = avatar.m_Avatar.m_AvatarSkeleton.m_ID.Count(meshBoneHashes.Contains);
                if (!IsStrongMeshAvatarMatch(meshBoneHashes.Count, overlap))
                {
                    continue;
                }

                var score = overlap * 100;
                if (avatar.assetsFile == mesh.assetsFile) score += 20;

                var avatarName = avatar.m_Name.Replace("Avatar", string.Empty).Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(avatarName) && meshName.Contains(avatarName)) score += 15;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAvatar = avatar;
                }
            }

            return bestAvatar;
        }

        private static bool IsStrongAnimationAvatarMatch(int bindingCount, int overlap)
        {
            if (bindingCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = bindingCount < 6 ? bindingCount : Math.Max(6, bindingCount / 20);
            return overlap >= minimum;
        }

        private static bool IsStrongMeshAvatarMatch(int boneCount, int overlap)
        {
            if (boneCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = boneCount < 6 ? boneCount : Math.Max(6, boneCount / 10);
            return overlap >= minimum;
        }

        private static void IndexMaterialTexturesBackground(
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

        private static Material? ResolveMaterialForPreviewBackground(
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

        private static Material? ResolveMaterialForPreviewUncachedBackground(Material material)
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

                if (material.m_Parent != null)
                {
                    if (material.m_Parent.TryGet(out var parent))
                    {
                        material = parent;
                        continue;
                    }
                }

                break;
            }

            return null;
        }

        private static Texture2D? ResolveTexturePPtrBackground(Material material, PPtr<Texture> textureRef)
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

        private static Texture2D? SelectMainTextureForMaterialBackground(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
        {
            if (displayMaterial.m_SavedProperties?.m_TexEnvs == null) return null;

            foreach (var slot in PreferredMaterialTextureSlots)
            {
                if (textureSlots.TryGetValue(slot, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            foreach (var env in displayMaterial.m_SavedProperties.m_TexEnvs)
            {
                if (NonDiffuseSlots.Contains(env.Key)) continue;
                if (textureSlots.TryGetValue(env.Key, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            return null;
        }

        private static void AddSerializedTypesBackground(SerializedFile assetsFile, IEnumerable<SerializedType>? types, string sourceKind,
            Dictionary<(string UnityVersion, int ClassID), int> objectCounts, HashSet<string> seen, List<AssetClassItem> localAssetClassItems)
        {
            if (types == null)
                return;

            foreach (var type in types)
            {
                var name = GetSerializedTypeName(type);
                var ns = type.m_NameSpace ?? string.Empty;
                var asm = type.m_AsmName ?? string.Empty;
                var key = string.Join("\u001f", assetsFile.unityVersion, type.classID.ToString(CultureInfo.InvariantCulture), name, ns, asm, sourceKind);
                if (!seen.Add(key))
                    continue;

                objectCounts.TryGetValue((assetsFile.unityVersion, type.classID), out var objectCount);

                var item = new AssetClassItem
                {
                    ClassID = type.classID,
                    Name = name,
                    Namespace = ns,
                    Assembly = asm,
                    UnityVersion = assetsFile.unityVersion,
                    SourceFile = assetsFile.fileName,
                    ObjectCount = objectCount,
                    SourceKind = type.m_IsStrippedType ? $"{sourceKind} stripped" : sourceKind,
                    SerializedType = type
                };
                localAssetClassItems.Add(item);
            }
        }

        private static int ScoreMaterialsStatic(List<Material?> mats)
        {
            if (mats == null || mats.Count == 0) return 0;
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

        private static List<Material?> MergeMeshMaterialLists(List<Material?> existing, List<Material?> incoming)
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
