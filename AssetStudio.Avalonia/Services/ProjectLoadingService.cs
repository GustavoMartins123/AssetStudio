using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using SkiaSharp;

namespace AssetStudio.Avalonia.Services
{
    public sealed class ProjectLoadingService
    {
        public const int AssetPreloadWindowSize = 50;
        public const int ParallelFbxContainerThreshold = 8192;

        public delegate bool PreviewImageLimiter(ref SKBitmap image, int maxDimension);

        public static ParallelOptions CreateStructureBuildParallelOptions()
        {
            return new ParallelOptions
            {
                MaxDegreeOfParallelism = AssetsManager.GetConfiguredThreadCount("ASSETSTUDIO_STRUCTURE_THREADS", 0.4)
            };
        }

        public static bool IsLazyExportableType(ClassIDType type)
        {
            return type
                is ClassIDType.Texture2D
                or ClassIDType.Texture2DArray
                or ClassIDType.AudioClip
                or ClassIDType.VideoClip
                or ClassIDType.VideoPlayer
                or ClassIDType.Shader
                or ClassIDType.Mesh
                or ClassIDType.Material
                or ClassIDType.TextAsset
                or ClassIDType.MonoScript
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

        public static void BuildLazyAssetItemsBackground(
            IReadOnlyList<AssetHandle> handles,
            bool displayAllChecked,
            Dictionary<string, AssetItem> pathIDAssetItemDic,
            Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic,
            List<AssetItem> exportableAssets,
            List<AssetItem> newExportableAssets,
            bool populatePathIdDictionary = true)
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
                    if (!assetItem.HasOrdinalOrUniqueId)
                    {
                        assetItem.SetOrdinal(index);
                    }
                }
                else
                {
                    assetItem = new AssetItem(handle, index);
                    handle.Tag = assetItem;
                    isNewHandle = true;
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

                if (populatePathIdDictionary)
                {
                    pathIDAssetItemDic[handle.UniqueID] = assetItem;
                }
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

        public static void BuildExportableAssetIndexesBackground(
            IEnumerable<AssetItem> exportableAssets,
            out Dictionary<AssetHandleKey, AssetItem> lazyItemsByHandleId,
            out HashSet<AssetHandleKey> exportableHandleIds,
            out HashSet<ClassIDType> exportableTypes)
        {
            lazyItemsByHandleId = new Dictionary<AssetHandleKey, AssetItem>();
            exportableHandleIds = new HashSet<AssetHandleKey>();
            exportableTypes = new HashSet<ClassIDType>();

            foreach (var item in exportableAssets)
            {
                if (item == null)
                {
                    continue;
                }

                if (item.Handle != null && item.Handle.TryGetKey(out var handleKey))
                {
                    lazyItemsByHandleId[handleKey] = item;
                    exportableHandleIds.Add(handleKey);
                }

                exportableTypes.Add(item.Type);
            }
        }

        public static string GetSerializedTypeName(SerializedType type)
        {
            if (!string.IsNullOrEmpty(type.m_KlassName))
            {
                return type.m_KlassName;
            }

            var rootNode = type.m_Type?.m_Nodes?.FirstOrDefault();
            if (!string.IsNullOrEmpty(rootNode?.m_Type))
            {
                return rootNode.m_Type;
            }

            return ClassIDTypeHelper.IsDefined(type.classID)
                ? ClassIDTypeHelper.FromClassId(type.classID).ToString()
                : $"Class {type.classID}";
        }

        public static void AddSerializedTypesBackground(
            SerializedFile assetsFile,
            IEnumerable<SerializedType>? types,
            string sourceKind,
            Dictionary<(string UnityVersion, int ClassID), int> objectCounts,
            HashSet<string> seen,
            List<AssetClassItem> localAssetClassItems)
        {
            if (types == null)
            {
                return;
            }

            foreach (var type in types)
            {
                var name = GetSerializedTypeName(type);
                var ns = type.m_NameSpace ?? string.Empty;
                var asm = type.m_AsmName ?? string.Empty;
                var key = string.Join("\u001f", assetsFile.unityVersion, type.classID.ToString(CultureInfo.InvariantCulture), name, ns, asm, sourceKind);
                if (!seen.Add(key))
                {
                    continue;
                }

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

        // --- Eager Asset Building & FBX Scene Node Linking ---

        public static void BuildEagerAssetItemsBackground(
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
            var assetItem = new AssetItem(asset);
            assetItem.SetOrdinal(workItem.Index);
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
                case Texture2DArray m_Texture2DArray:
                    if (!string.IsNullOrEmpty(m_Texture2DArray.m_StreamData?.path))
                    {
                        assetItem.FullSize = asset.byteSize + m_Texture2DArray.m_StreamData.size;
                    }
                    assetItem.Name = m_Texture2DArray.m_Name;
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

        public static List<(PPtr<AssetStudio.Object>, string)> CollectAssetBundleContainers(AssetBundle assetBundle)
        {
            var containers = new List<(PPtr<AssetStudio.Object>, string)>();
            CollectSafeAssetBundleContainers(assetBundle, containers);
            return containers;
        }

        public static void CollectSafeAssetBundleContainers(
            AssetBundle? assetBundle,
            List<(PPtr<AssetStudio.Object>, string)> containers)
        {
            ArgumentNullException.ThrowIfNull(containers);
            if (assetBundle?.m_Container == null)
            {
                return;
            }

            var preloadTable = assetBundle.m_PreloadTable;
            foreach (var m_Container in assetBundle.m_Container)
            {
                var assetInfo = m_Container.Value;
                if (assetInfo == null || string.IsNullOrEmpty(m_Container.Key))
                {
                    continue;
                }

                var directAsset = assetInfo.asset;
                var addedDirectAsset = false;

                if (preloadTable != null && preloadTable.Length > 0)
                {
                    var preloadIndex = assetInfo.preloadIndex;
                    var preloadSize = assetInfo.preloadSize;
                    if (preloadIndex >= 0 && preloadSize > 0 && preloadIndex < preloadTable.Length)
                    {
                        long preloadEnd = Math.Min((long)preloadIndex + preloadSize, preloadTable.Length);
                        for (int k = preloadIndex; k < preloadEnd; k++)
                        {
                            var preloadEntry = preloadTable[k];
                            if (preloadEntry == null || preloadEntry.IsNull)
                            {
                                continue;
                            }

                            containers.Add((preloadEntry, m_Container.Key));
                            if (directAsset != null
                                && !directAsset.IsNull
                                && preloadEntry.m_FileID == directAsset.m_FileID
                                && preloadEntry.m_PathID == directAsset.m_PathID)
                            {
                                addedDirectAsset = true;
                            }
                        }
                    }
                }

                if (!addedDirectAsset && directAsset != null && !directAsset.IsNull)
                {
                    containers.Add((directAsset, m_Container.Key));
                }
            }
        }

        public static List<(PPtr<AssetStudio.Object>, string)> CollectResourceManagerContainers(ResourceManager resourceManager)
        {
            var containers = new List<(PPtr<AssetStudio.Object>, string)>();
            foreach (var container in resourceManager.m_Container)
            {
                containers.Add((container.Value, container.Key));
            }

            return containers;
        }

        public static bool TryResolveLocalObject<T>(SerializedFile file, PPtr<T> pointer, out T? result)
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

        public static void LinkAssetItemsToSceneNodesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            Dictionary<GameObject, GameObjectNode> treeNodeDictionary,
            Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic)
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

        public static void LinkFbxSubAssetsToSceneNodesBackground(
            IReadOnlyList<AssetItem> localExportableAssets,
            IReadOnlyList<GameObjectNode> localSceneTreeNodes)
        {
            var count = localExportableAssets.Count;
            if (count == 0)
            {
                return;
            }

            var fbxContainers = new string?[count];
            var treeNodes = new GameObjectNode?[count];
            var anyFbx = 0;
            if (count >= ParallelFbxContainerThreshold)
            {
                Parallel.For(0, count, CreateStructureBuildParallelOptions(), index =>
                {
                    var item = localExportableAssets[index];
                    treeNodes[index] = item.TreeNode;
                    var container = ResolveFbxContainerFast(item.Container);
                    fbxContainers[index] = container;
                    if (container != null)
                    {
                        Volatile.Write(ref anyFbx, 1);
                    }
                });
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    var item = localExportableAssets[index];
                    treeNodes[index] = item.TreeNode;
                    var container = ResolveFbxContainerFast(item.Container);
                    fbxContainers[index] = container;
                    if (container != null)
                    {
                        anyFbx = 1;
                    }
                }
            }

            if (anyFbx == 0)
            {
                return;
            }

            var fbxNodes = ResolveFbxNodes(treeNodes, fbxContainers, localSceneTreeNodes);
            if (fbxNodes.Count == 0)
            {
                return;
            }

            for (int index = 0; index < count; index++)
            {
                var fbxContainer = fbxContainers[index];
                if (fbxContainer == null || !fbxNodes.TryGetValue(fbxContainer, out var node))
                {
                    continue;
                }

                var item = localExportableAssets[index];
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

        public static string? ResolveFbxContainerFast(string? container)
        {
            if (string.IsNullOrEmpty(container) || !container.Contains(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return AssetExportService.GetFbxContainerPath(container);
        }

        public static Dictionary<string, GameObjectNode> BuildFbxSceneNameIndex(IReadOnlyList<GameObjectNode> roots)
        {
            var index = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<GameObjectNode>();
            for (int r = 0; r < roots.Count; r++)
            {
                IndexSubtree(roots[r], index, stack);
            }

            return index;
        }

        public static Dictionary<string, GameObjectNode> BuildFbxSceneSubtreeIndex(GameObjectNode root)
        {
            var index = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);
            IndexSubtree(root, index, new Stack<GameObjectNode>());
            return index;
        }

        private static void IndexSubtree(GameObjectNode root, Dictionary<string, GameObjectNode> index, Stack<GameObjectNode> stack)
        {
            stack.Clear();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.GameObject != null && node.Name is { } name)
                {
                    index.TryAdd(name, node);
                }

                var children = node.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }

        public static Dictionary<string, GameObjectNode> ResolveFbxNodes(
            IReadOnlyList<GameObjectNode?> itemTreeNodes,
            IReadOnlyList<string?> fbxContainers,
            IReadOnlyList<GameObjectNode> roots)
        {
            if (itemTreeNodes.Count != fbxContainers.Count)
            {
                throw new ArgumentException("Tree node and container lists must have the same length.", nameof(fbxContainers));
            }

            var fbxNodes = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);

            Dictionary<GameObjectNode, Dictionary<string, GameObjectNode>>? subtreeIndices = null;
            for (int i = 0; i < itemTreeNodes.Count; i++)
            {
                var treeNode = itemTreeNodes[i];
                if (treeNode?.GameObject == null)
                {
                    continue;
                }

                var fbxContainer = fbxContainers[i];
                if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
                {
                    continue;
                }

                var root = treeNode;
                while (root.Parent?.GameObject != null)
                {
                    root = root.Parent;
                }

                subtreeIndices ??= new Dictionary<GameObjectNode, Dictionary<string, GameObjectNode>>(ReferenceEqualityComparer.Instance);
                if (!subtreeIndices.TryGetValue(root, out var subtreeIndex))
                {
                    subtreeIndex = BuildFbxSceneSubtreeIndex(root);
                    subtreeIndices.Add(root, subtreeIndex);
                }

                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                fbxNodes.Add(fbxContainer, subtreeIndex.TryGetValue(fbxName, out var namedRoot) ? namedRoot : root);
            }

            Dictionary<string, GameObjectNode>? sceneIndex = null;
            for (int i = 0; i < fbxContainers.Count; i++)
            {
                var fbxContainer = fbxContainers[i];
                if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
                {
                    continue;
                }

                sceneIndex ??= BuildFbxSceneNameIndex(roots);
                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                if (sceneIndex.TryGetValue(fbxName, out var node))
                {
                    fbxNodes[fbxContainer] = node;
                }
            }

            return fbxNodes;
        }

        // --- Project Index Cache, Preview Cache & Indexing State ---

        public static string GetFolderCacheKey(string folderPath)
        {
            return SQLiteProjectIndexCache.GetFolderCacheKey(folderPath);
        }

        internal static string GetFolderCacheKey(string folderPath, bool caseSensitive)
        {
            return SQLiteProjectIndexCache.GetFolderCacheKey(folderPath, caseSensitive);
        }

        public static string GetDecompressedBundleScopeKey(string folderPath)
        {
            return GetDecompressedBundleScopeKey(folderPath, caseSensitive: OperatingSystem.IsLinux());
        }

        internal static string GetDecompressedBundleScopeKey(string folderPath, bool caseSensitive)
        {
            string normalized;
            try
            {
                normalized = SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(Path.GetFullPath(folderPath));
            }
            catch
            {
                normalized = SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(folderPath ?? string.Empty);
            }

            if (!caseSensitive)
            {
                normalized = normalized.ToLowerInvariant();
            }

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            var hex = Convert.ToHexStringLower(hashBytes);
            return hex.Length > 16 ? hex.Substring(0, 16) : hex;
        }

        public static string ResolveCacheFolderPath(string? loadFolderPath, string? projectRoot)
        {
            if (!string.IsNullOrWhiteSpace(loadFolderPath) && Directory.Exists(loadFolderPath))
            {
                return loadFolderPath;
            }

            if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            {
                return projectRoot;
            }

            return string.Empty;
        }

        public static void DeleteDecompressedCacheFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var cacheRoot = DecompressedBundleCache.GetDefaultCacheBaseDirectory();
            var legacyDirectory = Path.Combine(cacheRoot, GetFolderCacheKey(folderPath));
            DeleteDirectoryInsideRoot(cacheRoot, legacyDirectory);

            var scopedDirectory = Path.Combine(cacheRoot, GetDecompressedBundleScopeKey(folderPath));
            DeleteDirectoryInsideRoot(cacheRoot, scopedDirectory);
        }

        public static void DeletePreviewCacheFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "PreviewCache");
            var targetDirectory = Path.Combine(cacheRoot, GetFolderCacheKey(folderPath));
            DeleteDirectoryInsideRoot(cacheRoot, targetDirectory);
        }

        public static void ClearProjectCache(SQLiteProjectIndexCache sqliteCache, string folderPath)
        {
            ArgumentNullException.ThrowIfNull(sqliteCache);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("Folder path must not be empty.", nameof(folderPath));
            }

            sqliteCache.EvictPreviewCache(folderPath, maxTotalBytes: 0, maxEntryCount: 0);
            sqliteCache.DeleteIndexCache(folderPath);
            DeleteDecompressedCacheFolder(folderPath);
            DeletePreviewCacheFolder(folderPath);
            SpriteHelper.ClearAtlasCache();
            AssetExportHelper.ClearShaderGuidCache();
        }

        internal static MeshPreviewGeometryCache? LoadMeshPreviewGeometryCache(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult,
            Mesh mesh,
            float densityPercent)
        {
            if (sqliteCache == null || mesh?.assetsFile == null || currentScanResult == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return null;
            }

            try
            {
                var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
                var signature = sqliteCache.GetFolderSignature(currentScanResult);
                var parameters = BuildMeshPreviewCacheParameters(mesh, densityPercent);
                if (!sqliteCache.TryLoadPreviewCacheBytes(
                    folderPath,
                    signature,
                    assetId,
                    "mesh-geometry",
                    MeshPreviewGeometryCache.AlgorithmVersion,
                    parameters,
                    out var bytes,
                    out var entry)
                    || bytes == null
                    || entry == null)
                {
                    return null;
                }

                if (!entry.IsInlineBlob
                    && (string.IsNullOrWhiteSpace(entry.PayloadPath)
                        || !IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath)))
                {
                    return null;
                }

                if (!string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return MeshPreviewGeometryCacheSerializer.Deserialize(bytes);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load mesh preview geometry cache: {ex.Message}");
                return null;
            }
        }

        internal static void SaveMeshPreviewGeometryCache(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult,
            Mesh mesh,
            float densityPercent,
            MeshPreviewGeometryCache cache)
        {
            if (sqliteCache == null || mesh?.assetsFile == null || currentScanResult == null || cache == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            try
            {
                var bytes = MeshPreviewGeometryCacheSerializer.Serialize(cache);
                var hash = ComputeSha256Hex(bytes);
                var payloadPath = GetPreviewCachePayloadPath(folderPath, hash);
                var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
                var signature = sqliteCache.GetFolderSignature(currentScanResult);
                sqliteCache.SavePreviewCachePayload(
                    folderPath,
                    signature,
                    assetId,
                    "mesh-geometry",
                    MeshPreviewGeometryCache.AlgorithmVersion,
                    BuildMeshPreviewCacheParameters(mesh, densityPercent),
                    hash,
                    bytes,
                    payloadPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save mesh preview geometry cache: {ex.Message}");
            }
        }

        internal static void EnqueueSaveMeshPreviewGeometryCache(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult,
            Mesh mesh,
            float densityPercent,
            MeshPreviewGeometryCache cache)
        {
            if (sqliteCache == null || mesh?.assetsFile == null || currentScanResult == null || cache == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            try
            {
                var bytes = MeshPreviewGeometryCacheSerializer.Serialize(cache);
                var hash = ComputeSha256Hex(bytes);
                var payloadPath = GetPreviewCachePayloadPath(folderPath, hash);
                var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
                var signature = sqliteCache.GetFolderSignature(currentScanResult);
                sqliteCache.EnqueuePreviewCachePayload(
                    folderPath,
                    signature,
                    assetId,
                    "mesh-geometry",
                    MeshPreviewGeometryCache.AlgorithmVersion,
                    BuildMeshPreviewCacheParameters(mesh, densityPercent),
                    hash,
                    bytes,
                    payloadPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to enqueue mesh preview geometry cache: {ex.Message}");
            }
        }

        internal static TexturePreviewImageResult? TryLoadCachedTexturePreviewThumbnailByAssetId(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult,
            string textureAssetId,
            int maxDimension,
            int algorithmVersion)
        {
            if (sqliteCache == null
                || string.IsNullOrWhiteSpace(textureAssetId)
                || currentScanResult == null
                || string.IsNullOrWhiteSpace(folderPath)
                || !Directory.Exists(folderPath))
            {
                return null;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(currentScanResult);
                var parametersPrefix = "max=" + maxDimension.ToString(CultureInfo.InvariantCulture) + "|";
                if (sqliteCache.TryLoadLatestPreviewCacheBytesByAssetId(
                    folderPath,
                    signature,
                    textureAssetId,
                    "texture-thumbnail-png",
                    algorithmVersion,
                    parametersPrefix,
                    out var bytes,
                    out var entry)
                    && bytes != null
                    && entry != null
                    && (entry.IsInlineBlob
                        || (!string.IsNullOrWhiteSpace(entry.PayloadPath)
                            && IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath))))
                {
                    if (string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var cachedImage = ImageExtensions.DecodeBgraUnpremul(bytes);
                        if (cachedImage != null)
                        {
                            return new TexturePreviewImageResult(
                                cachedImage,
                                fromCache: true,
                                downscaled: false,
                                cachedImage.Width,
                                cachedImage.Height);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load cached texture thumbnail by asset id: {ex.Message}");
            }

            return null;
        }

        internal static TexturePreviewImageResult? LoadTexturePreviewThumbnail(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult,
            Texture2D texture,
            int maxDimension,
            int algorithmVersion,
            PreviewImageLimiter limitPreviewImage)
        {
            if (texture == null)
            {
                return null;
            }

            var sourceWidth = texture.m_Width;
            var sourceHeight = texture.m_Height;
            var canUsePersistentCache = sqliteCache != null
                && texture.assetsFile != null
                && currentScanResult != null
                && !string.IsNullOrWhiteSpace(folderPath)
                && Directory.Exists(folderPath);

            if (canUsePersistentCache)
            {
                try
                {
                    var assetId = AssetHandle.BuildUniqueID(texture.assetsFile!, texture.m_PathID);
                    var signature = sqliteCache!.GetFolderSignature(currentScanResult!);
                    var parameters = BuildTexturePreviewCacheParameters(texture, maxDimension);
                    if (sqliteCache.TryLoadPreviewCacheBytes(
                        folderPath,
                        signature,
                        assetId,
                        "texture-thumbnail-png",
                        algorithmVersion,
                        parameters,
                        out var bytes,
                        out var entry)
                        && bytes != null
                        && entry != null
                        && (entry.IsInlineBlob
                            || (!string.IsNullOrWhiteSpace(entry.PayloadPath)
                                && IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath))))
                    {
                        if (string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
                        {
                            var cachedImage = ImageExtensions.DecodeBgraUnpremul(bytes);
                            if (cachedImage != null)
                            {
                                return new TexturePreviewImageResult(
                                    cachedImage,
                                    fromCache: true,
                                    downscaled: sourceWidth > cachedImage.Width || sourceHeight > cachedImage.Height,
                                    sourceWidth,
                                    sourceHeight);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load texture thumbnail cache: {ex.Message}");
                }
            }

            var image = texture.ConvertToImage(true);
            if (image == null)
            {
                return null;
            }

            var downscaled = limitPreviewImage != null && limitPreviewImage(ref image, maxDimension);
            if (canUsePersistentCache)
            {
                // Persist PNG thumbnail asynchronously so rendering never blocks on PNG encoding or SQLite WriteGate.
                var snapshotForCache = image.Copy();
                if (snapshotForCache != null)
                {
                    var assetId = AssetHandle.BuildUniqueID(texture.assetsFile!, texture.m_PathID);
                    var signature = sqliteCache!.GetFolderSignature(currentScanResult!);
                    var parameters = BuildTexturePreviewCacheParameters(texture, maxDimension);
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using (snapshotForCache)
                            {
                                using var stream = new MemoryStream();
                                snapshotForCache.WriteToStream(stream, ImageFormat.Png);
                                var bytes = stream.ToArray();
                                var hash = ComputeSha256Hex(bytes);
                                var payloadPath = GetPreviewCachePayloadPath(folderPath, hash, ".texturepreview.png");
                                sqliteCache.EnqueuePreviewCachePayload(
                                    folderPath,
                                    signature,
                                    assetId,
                                    "texture-thumbnail-png",
                                    algorithmVersion,
                                    parameters,
                                    hash,
                                    bytes,
                                    payloadPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to save texture thumbnail cache: {ex.Message}");
                        }
                    });
                }
            }

            return new TexturePreviewImageResult(image, fromCache: false, downscaled, sourceWidth, sourceHeight);
        }

        public static string BuildMeshPreviewCacheParameters(Mesh mesh, float densityPercent)
        {
            return string.Join("|",
                "density=" + densityPercent.ToString("0.###", CultureInfo.InvariantCulture),
                "vertices=" + mesh.m_VertexCount.ToString(CultureInfo.InvariantCulture),
                "indices=" + (mesh.m_Indices?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                "submeshes=" + (mesh.m_SubMeshes?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                "bytes=" + mesh.byteSize.ToString(CultureInfo.InvariantCulture));
        }

        public static string BuildTexturePreviewCacheParameters(Texture2D texture, int maxDimension)
        {
            return string.Join("|",
                "max=" + maxDimension.ToString(CultureInfo.InvariantCulture),
                "width=" + texture.m_Width.ToString(CultureInfo.InvariantCulture),
                "height=" + texture.m_Height.ToString(CultureInfo.InvariantCulture),
                "format=" + texture.m_TextureFormat,
                "mips=" + texture.m_MipCount.ToString(CultureInfo.InvariantCulture),
                "bytes=" + texture.byteSize.ToString(CultureInfo.InvariantCulture),
                "streamSize=" + (texture.m_StreamData?.size ?? 0).ToString(CultureInfo.InvariantCulture),
                "streamOffset=" + (texture.m_StreamData?.offset ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        public static string GetPreviewCacheRoot(string folderPath)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "PreviewCache",
                GetFolderCacheKey(folderPath));
        }

        public static string GetPreviewCachePayloadPath(string folderPath, string hash)
        {
            return GetPreviewCachePayloadPath(folderPath, hash, ".meshpreview");
        }

        public static string GetPreviewCachePayloadPath(string folderPath, string hash, string extension)
        {
            var safeHash = string.IsNullOrWhiteSpace(hash) ? "empty" : hash.ToLowerInvariant();
            var prefix = safeHash.Length >= 2 ? safeHash.Substring(0, 2) : "00";
            var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".cache" : extension;
            if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
            {
                safeExtension = "." + safeExtension;
            }

            return Path.Combine(GetPreviewCacheRoot(folderPath), prefix, safeHash + safeExtension);
        }

        public static string ComputeSha256Hex(byte[] bytes)
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        public static bool IsPathInsideDirectory(string root, string path)
        {
            return IsPathInsideDirectory(root, path, caseSensitive: OperatingSystem.IsLinux());
        }

        internal static bool IsPathInsideDirectory(string root, string path, bool caseSensitive)
        {
            var rootFullPath = SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(Path.GetFullPath(root));
            if (!rootFullPath.EndsWith(Path.DirectorySeparatorChar) && !rootFullPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                rootFullPath += Path.DirectorySeparatorChar;
            }

            var targetFullPath = Path.GetFullPath(path);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return targetFullPath.StartsWith(rootFullPath, comparison);
        }

        public static void DeleteDirectoryInsideRoot(string root, string targetDirectory)
        {
            DeleteDirectoryInsideRoot(root, targetDirectory, caseSensitive: OperatingSystem.IsLinux());
        }

        internal static void DeleteDirectoryInsideRoot(string root, string targetDirectory, bool caseSensitive)
        {
            if (!IsPathInsideDirectory(root, targetDirectory, caseSensitive))
            {
                throw new InvalidOperationException("Refusing to delete a cache directory outside the AssetStudio cache root.");
            }

            var targetFullPath = Path.GetFullPath(targetDirectory);
            if (Directory.Exists(targetFullPath))
            {
                Directory.Delete(targetFullPath, recursive: true);
            }
        }

        public static IndexingProgressUpdate CreateStepProgressUpdate(
            string status,
            int processed,
            int total,
            string? stage,
            bool allowZeroTotal = false)
        {
            var safeTotal = allowZeroTotal ? Math.Max(0, total) : Math.Max(1, total);
            var safeProcessed = Math.Clamp(processed, 0, safeTotal == 0 ? processed : safeTotal);
            var percent = safeTotal <= 0
                ? 100
                : Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal));

            return new IndexingProgressUpdate
            {
                Status = status,
                TotalFiles = safeTotal,
                ProcessedFiles = safeProcessed,
                PendingFiles = Math.Max(0, safeTotal - safeProcessed),
                PercentComplete = percent,
                CurrentFile = stage ?? string.Empty,
                LastReadFile = stage ?? string.Empty,
                NewlyReadFiles = Array.Empty<string>()
            };
        }

        public static void PublishDatabaseWriteProgress(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult scanResult,
            string status,
            int processed,
            int total,
            string stage,
            bool persist,
            Action<IndexingProgressUpdate, int>? showProgressPanel = null)
        {
            var update = CreateStepProgressUpdate(status, processed, total, stage);

            if (persist && sqliteCache != null && scanResult != null)
            {
                try
                {
                    var signature = sqliteCache.GetFolderSignature(scanResult);
                    sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to persist database write progress: {ex.Message}");
                }
            }

            showProgressPanel?.Invoke(update, 3);
        }

        public static void SaveIndexCache(
            SQLiteProjectIndexCache sqliteCache,
            AssetsManager assetsManager,
            string folderPath,
            ProjectScanResult scanResult,
            bool preserveSemanticRelations = false,
            Action<int, int, string>? progress = null,
            Action<IndexingProgressUpdate, int>? showProgressPanel = null,
            IEnumerable<AssetHandle>? handlesOverride = null)
        {
            if (sqliteCache == null || assetsManager == null || scanResult == null)
            {
                return;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                var unityVersion = assetsManager.SpecifyUnityVersion;
                var rawHandles = handlesOverride ?? assetsManager.ProjectIndex.GetHandles();
                // Materialize once so ProjectIndex.GetHandles() does not allocate millions of handles twice
                // across SaveIndexCache and DiscoverAndPersistScenesAndExternals.
                var materializedHandles = rawHandles as IReadOnlyList<AssetHandle> ?? rawHandles.ToList();
                PublishDatabaseWriteProgress(
                    sqliteCache,
                    folderPath,
                    scanResult,
                    "saving_index",
                    0,
                    1,
                    "Waiting for SQLite writer",
                    persist: false,
                    showProgressPanel);

                void ReportSaveProgress(int processed, int total, string stage)
                {
                    progress?.Invoke(processed, total, stage);
                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "saving_index",
                        processed,
                        total,
                        stage,
                        persist: false,
                        showProgressPanel);
                }

                // PERF-05: Fold the terminal "completed" state into the same ACID SQLite commit transaction.
                var saved = sqliteCache.SaveIndexCache(
                    folderPath,
                    signature,
                    scanResult,
                    unityVersion,
                    materializedHandles,
                    preserveSemanticRelations,
                    ReportSaveProgress,
                    markIndexingCompleted: true);

                if (saved)
                {
                    try
                    {
                        DiscoverAndPersistScenesAndExternals(
                            sqliteCache,
                            assetsManager,
                            folderPath,
                            signature,
                            materializedHandles);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to persist discovered scenes/externals for '{folderPath}': {ex.Message}");
                    }

                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "completed",
                        1,
                        1,
                        "SQLite index cache saved",
                        persist: false,
                        showProgressPanel);
                }
                else
                {
                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "failed",
                        1,
                        1,
                        "SQLite index cache save failed",
                        persist: true,
                        showProgressPanel);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save index cache: {ex.Message}");
            }
        }

        internal static bool SaveIncrementalIndexDelta(
            SQLiteProjectIndexCache sqliteCache,
            AssetsManager assetsManager,
            string folderPath,
            ProjectScanResult scanResult,
            SQLiteProjectIndexCache.IncrementalProjectDeltaPlan deltaPlan,
            IReadOnlyCollection<string>? reindexedFilePaths,
            Action<int, int, string>? progress = null,
            Action<IndexingProgressUpdate, int>? showProgressPanel = null)
        {
            if (sqliteCache == null || assetsManager == null || scanResult == null || deltaPlan == null)
            {
                return false;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                var unityVersion = assetsManager.SpecifyUnityVersion;
                var rawHandles = assetsManager.ProjectIndex.GetHandles();
                var allMergedHandles = rawHandles as IReadOnlyList<AssetHandle> ?? rawHandles.ToList();

                IReadOnlyCollection<AssetHandle> updatedHandles;
                if (reindexedFilePaths == null || reindexedFilePaths.Count == 0)
                {
                    updatedHandles = Array.Empty<AssetHandle>();
                }
                else
                {
                    var pathComparer = sqliteCache.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                    var reindexedSet = new HashSet<string>(pathComparer);
                    foreach (var p in reindexedFilePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                        {
                            reindexedSet.Add(p);
                            try
                            {
                                reindexedSet.Add(Path.GetFullPath(p));
                            }
                            catch
                            {
                                // Keep raw path when Path.GetFullPath cannot normalize synthetic test paths.
                            }
                        }
                    }

                    var filtered = new List<AssetHandle>();
                    for (var i = 0; i < allMergedHandles.Count; i++)
                    {
                        var h = allMergedHandles[i];
                        var orig = h?.OriginalPath;
                        if (!string.IsNullOrEmpty(orig) && reindexedSet.Contains(orig))
                        {
                            filtered.Add(h!);
                        }
                    }

                    updatedHandles = filtered;
                }

                PublishDatabaseWriteProgress(
                    sqliteCache,
                    folderPath,
                    scanResult,
                    "saving_index",
                    0,
                    Math.Max(1, updatedHandles.Count + deltaPlan.DirtySourceFileIds.Count + 2),
                    "Saving incremental index delta",
                    persist: false,
                    showProgressPanel);

                void ReportSaveProgress(int processed, int total, string stage)
                {
                    progress?.Invoke(processed, total, stage);
                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "saving_index",
                        processed,
                        total,
                        stage,
                        persist: false,
                        showProgressPanel);
                }

                var saved = sqliteCache.SaveIncrementalIndexDelta(
                    folderPath,
                    signature,
                    scanResult,
                    unityVersion,
                    deltaPlan,
                    updatedHandles,
                    allMergedHandles,
                    markIndexingCompleted: true,
                    progress: ReportSaveProgress);

                if (saved)
                {
                    if (updatedHandles.Count > 0)
                    {
                        try
                        {
                            DiscoverAndPersistScenesAndExternals(
                                sqliteCache,
                                assetsManager,
                                folderPath,
                                signature,
                                allMergedHandles);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to refresh scenes/externals after incremental index update for '{folderPath}': {ex.Message}");
                        }
                    }

                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "completed",
                        1,
                        1,
                        "Incremental SQLite index cache saved",
                        persist: false,
                        showProgressPanel);
                    return true;
                }

                PublishDatabaseWriteProgress(
                    sqliteCache,
                    folderPath,
                    scanResult,
                    "failed",
                    1,
                    1,
                    "Incremental SQLite index cache save failed",
                    persist: true,
                    showProgressPanel);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save incremental index cache: {ex.Message}");
                return false;
            }
        }

        internal static bool TrySaveSemanticRelations(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult,
            SemanticAssetRelations? relations,
            bool replaceExisting = false,
            Action<IndexingProgressUpdate, int>? showProgressPanel = null)
        {
            if (sqliteCache == null
                || relations == null
                || (!relations.HasRelations && relations.SourceFiles.Count == 0)
                || scanResult == null
                || string.IsNullOrWhiteSpace(folderPath)
                || !Directory.Exists(folderPath))
            {
                return false;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                PublishDatabaseWriteProgress(
                    sqliteCache,
                    folderPath,
                    scanResult,
                    "saving_connections",
                    0,
                    1,
                    "Waiting for SQLite writer",
                    persist: true,
                    showProgressPanel);

                var saved = sqliteCache.SaveSemanticRelations(
                    folderPath,
                    signature,
                    relations,
                    replaceExisting,
                    (processed, total, stage) => PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "saving_connections",
                        processed,
                        total,
                        stage,
                        persist: false,
                        showProgressPanel));

                if (saved)
                {
                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "saving_connections",
                        1,
                        1,
                        "Semantic relations saved",
                        persist: false,
                        showProgressPanel);
                }

                return saved;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save semantic relation cache: {ex.Message}");
                return false;
            }
        }

        internal static bool TryCommitSemanticStagingSession(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult,
            SQLiteProjectIndexCache.SemanticStagingSession? stagingSession,
            Action<IndexingProgressUpdate, int>? showProgressPanel = null,
            bool replaceExisting = true)
        {
            if (sqliteCache == null
                || stagingSession == null
                || scanResult == null
                || string.IsNullOrWhiteSpace(folderPath)
                || !Directory.Exists(folderPath))
            {
                return false;
            }

            try
            {
                // Guard semantic commit against mid-index scan/signature drift.
                var expectedSignature = sqliteCache.GetFolderSignature(scanResult);
                if (!string.Equals(stagingSession.ProjectSignature, expectedSignature, StringComparison.Ordinal))
                {
                    Logger.Warning($"Aborting semantic staging commit for '{folderPath}': staging signature '{stagingSession.ProjectSignature}' does not match current scan signature '{expectedSignature}'.");
                    stagingSession.Abort();
                    return false;
                }

                PublishDatabaseWriteProgress(
                    sqliteCache,
                    folderPath,
                    scanResult,
                    "saving_connections",
                    0,
                    1,
                    "Waiting for SQLite writer",
                    persist: true,
                    showProgressPanel);

                var saved = stagingSession.Commit(
                    (processed, total, stage) => PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "saving_connections",
                        processed,
                        total,
                        stage,
                        persist: false,
                        showProgressPanel),
                    replaceExisting: replaceExisting);

                if (saved)
                {
                    PublishDatabaseWriteProgress(
                        sqliteCache,
                        folderPath,
                        scanResult,
                        "saving_connections",
                        1,
                        1,
                        "Semantic relations saved",
                        persist: false,
                        showProgressPanel);
                }

                return saved;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to commit semantic relation staging cache: {ex.Message}");
                return false;
            }
        }

        public static void TrySaveIndexingProgress(
            SQLiteProjectIndexCache sqliteCache,
            string[]? paths,
            ProjectScanResult? currentScanResult,
            IndexingProgressUpdate? update)
        {
            if (sqliteCache == null
                || update == null
                || currentScanResult == null
                || paths == null
                || paths.Length != 1
                || !Directory.Exists(paths[0]))
            {
                return;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(currentScanResult);
                sqliteCache.SaveIndexingProgress(paths[0], signature, currentScanResult, update);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to persist indexing progress: {ex.Message}");
            }
        }

        internal static ProjectIndexingState? TryLoadIndexingProgress(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult)
        {
            if (sqliteCache == null || scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return null;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                return sqliteCache.LoadIndexingState(folderPath, signature);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load indexing progress: {ex.Message}");
                return null;
            }
        }

        public static List<AssetHandle>? LoadIndexCache(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult scanResult)
        {
            if (sqliteCache == null || scanResult == null)
            {
                return null;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                return sqliteCache.LoadIndexCache(folderPath, signature);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load index cache: {ex.Message}");
                return null;
            }
        }

        public static bool HasCompletedLazyConnectionBuild(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult)
        {
            if (sqliteCache == null || scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                return sqliteCache.CanUseSemanticRelationCache(folderPath, signature);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to inspect lazy connection build state: {ex.Message}");
                return false;
            }
        }

        public static bool HasCompletedLazyStructureBuild(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult)
        {
            if (sqliteCache == null || scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                return sqliteCache.HasCompletedLazyStructureStatus(folderPath, signature);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to inspect lazy structure build state: {ex.Message}");
                return false;
            }
        }

        public static bool HasSavedSemanticRelations(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult)
        {
            if (sqliteCache == null || scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            try
            {
                var signature = sqliteCache.GetFolderSignature(scanResult);
                return sqliteCache.HasSemanticRelations(folderPath, signature);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to inspect saved semantic relations: {ex.Message}");
                return false;
            }
        }

        public static bool IsConnectionReadyStatus(string? status)
        {
            return string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsCompletedLazyIndexingStatus(string? status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanUseLazySemanticRelationCache(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult)
        {
            return currentScanResult != null
                && !string.IsNullOrWhiteSpace(folderPath)
                && Directory.Exists(folderPath)
                && HasCompletedLazyConnectionBuild(sqliteCache, folderPath, currentScanResult);
        }

        internal static bool ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(
            SQLiteProjectIndexCache sqliteCache,
            bool lazyLoading,
            ProjectScanResult? currentScanResult,
            string[]? paths,
            Action<ProjectIndexingState>? showProgressState = null,
            Action<string>? statusUpdate = null)
        {
            if (!lazyLoading || currentScanResult == null || paths == null || paths.Length != 1 || !Directory.Exists(paths[0]))
            {
                return false;
            }

            var folderPath = paths[0];
            if (HasCompletedLazyConnectionBuild(sqliteCache, folderPath, currentScanResult))
            {
                return false;
            }

            var state = TryLoadIndexingProgress(sqliteCache, folderPath, currentScanResult);
            if (state != null)
            {
                showProgressState?.Invoke(state);
            }

            statusUpdate?.Invoke("Connections did not produce saved relations; asset structure finalization was skipped.");
            return true;
        }

        public static void PublishLazyConnectionProgress(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult scanResult,
            string status,
            int processedFiles,
            int totalFiles,
            string currentFile,
            string lastReadFile,
            ref long lastConnectionDbWriteTicks,
            Action<IndexingProgressUpdate>? showProgressPanel = null)
        {
            var safeTotal = Math.Max(0, totalFiles);
            var safeProcessed = Math.Clamp(processedFiles, 0, safeTotal == 0 ? processedFiles : safeTotal);
            var update = new IndexingProgressUpdate
            {
                Status = status,
                TotalFiles = safeTotal,
                ProcessedFiles = safeProcessed,
                PendingFiles = Math.Max(0, safeTotal - safeProcessed),
                PercentComplete = safeTotal <= 0 ? 100 : Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal)),
                CurrentFile = currentFile ?? string.Empty,
                LastReadFile = lastReadFile ?? string.Empty,
                NewlyReadFiles = Array.Empty<string>()
            };

            var nowTicks = Stopwatch.GetTimestamp();
            var elapsedMs = (nowTicks - lastConnectionDbWriteTicks) * 1000.0 / Stopwatch.Frequency;

            bool isTerminal = string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "connections_failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

            if (processedFiles == 0 || processedFiles == safeTotal || isTerminal || elapsedMs >= 5000)
            {
                lastConnectionDbWriteTicks = nowTicks;
                if (sqliteCache != null && scanResult != null)
                {
                    try
                    {
                        var signature = sqliteCache.GetFolderSignature(scanResult);
                        sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to persist connection build progress: {ex.Message}");
                    }
                }
            }

            showProgressPanel?.Invoke(update);
        }

        public static void PublishStructureBuildProgress(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? currentScanResult,
            string status,
            int processedSteps,
            int totalSteps,
            string currentStage,
            bool persist = true,
            int percentDecimals = 1,
            Action<IndexingProgressUpdate, int>? showProgressPanel = null)
        {
            var update = CreateStepProgressUpdate(status, processedSteps, totalSteps, currentStage);

            if (persist && sqliteCache != null && currentScanResult != null)
            {
                if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                {
                    var scanResult = currentScanResult;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var signature = sqliteCache.GetFolderSignature(scanResult);
                            sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to persist structure build progress: {ex.Message}");
                        }
                    });
                }
            }

            showProgressPanel?.Invoke(update, percentDecimals);
        }

        public static List<string> ResolveColdIndexFiles(
            string folderPath,
            ProjectScanResult? scanResult,
            Action<string>? mergeSplitAssets = null,
            Func<string, string[]>? enumerateFiles = null)
        {
            var mergeAction = mergeSplitAssets ?? (static path => ImportHelper.MergeSplitAssets(path, true));
            var enumerateFunc = enumerateFiles ?? (static path => ImportHelper.GetFilesSafe(path, "*.*", true));

            if (scanResult != null && scanResult.Files.Count > 0)
            {
                var hasSplitFiles = false;
                var scannedPaths = new string[scanResult.Files.Count];
                for (var i = 0; i < scanResult.Files.Count; i++)
                {
                    var filePath = scanResult.Files[i].Path;
                    scannedPaths[i] = filePath;
                    if (!hasSplitFiles && filePath.EndsWith(".split0", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSplitFiles = true;
                    }
                }

                if (!hasSplitFiles)
                {
                    return ImportHelper.ProcessingSplitFiles(scannedPaths).ToList();
                }
            }

            mergeAction(folderPath);
            var enumerated = enumerateFunc(folderPath);
            return ImportHelper.ProcessingSplitFiles(enumerated).ToList();
        }

        /// <summary>
        /// PERF-03 selective cold-index planner: when an existing v16 cache has unchanged bundles,
        /// hydrates unchanged handles directly from SQLite into <paramref name="targetIndex"/> and returns
        /// ONLY added/modified files to parse. Falls back to full <see cref="ResolveColdIndexFiles"/> otherwise.
        /// </summary>
        internal static List<string> ResolveSelectiveColdIndexFiles(
            SQLiteProjectIndexCache sqliteCache,
            string folderPath,
            ProjectScanResult? scanResult,
            ProjectIndex targetIndex,
            out SQLiteProjectIndexCache.IncrementalProjectDeltaPlan? deltaPlan,
            out List<SourceFileMetadata>? unchangedSourceFiles,
            double maxDirtyRatio = 1.0)
        {
            deltaPlan = null;
            unchangedSourceFiles = null;

            if (sqliteCache != null
                && scanResult != null
                && scanResult.Files.Count > 0
                && !string.IsNullOrWhiteSpace(folderPath)
                && targetIndex != null)
            {
                var hasSplitFiles = false;
                for (var i = 0; i < scanResult.Files.Count; i++)
                {
                    if (scanResult.Files[i].Path.EndsWith(".split0", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSplitFiles = true;
                        break;
                    }
                }

                if (!hasSplitFiles)
                {
                    var plan = sqliteCache.PlanIncrementalDelta(folderPath, scanResult);
                    if (plan.CanApplyIncremental
                        && plan.DirtyRatio <= maxDirtyRatio
                        && sqliteCache.TryLoadUnchangedAssetsForIncrementalUpdate(folderPath, plan, targetIndex, out var loadedSources))
                    {
                        deltaPlan = plan;
                        unchangedSourceFiles = loadedSources;
                        return plan.FilesToReindex.ToList();
                    }
                }
            }

            return ResolveColdIndexFiles(folderPath, scanResult);
        }

        // --- Scene & External Dependency Discovery (Schema v16) ---

        private const long MaxUncachedLzmaHeaderProbeBytes = 16L * 1024 * 1024;

        private static readonly ConditionalWeakTable<AssetsManager, SceneIndexingMetadataCollector> SceneMetadataCollectors = new();

        public sealed class SceneDiscoveryResult
        {
            public List<SourceFileExternalRecord> Externals { get; } = new();
            public List<IndexedSceneRecord> Scenes { get; } = new();
            public Dictionary<string, (int GameObjectCount, int RendererCount)> SceneFileStats { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<(string SerializedFileName, string OriginalPath)>> CompanionStageFilesBySceneSf { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class SceneIndexingMetadataCollector
        {
            private static readonly StringComparer PathComparer =
                OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

            private sealed class FileEntry
            {
                public string OriginalPath { get; init; } = string.Empty;
                public List<SourceFileExternalRecord> Externals { get; } = new();
                public List<IndexedSceneRecord> BundleScenes { get; } = new();
                public List<IndexedSceneRecord> HeuristicScenes { get; } = new();
                public List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)> BuildSettingsMappings { get; } = new();
            }

            private readonly ConcurrentDictionary<string, FileEntry> _entriesByOriginalPath = new(PathComparer);

            public void Clear()
            {
                _entriesByOriginalPath.Clear();
            }

            public void RemoveByOriginalPath(string? originalPath)
            {
                var key = NormalizeCollectorPathKey(originalPath);
                if (!string.IsNullOrEmpty(key))
                {
                    _entriesByOriginalPath.TryRemove(key, out _);
                }
            }

            internal void RecordFile(
                string originalPath,
                List<SourceFileExternalRecord> externals,
                List<IndexedSceneRecord> bundleScenes,
                List<IndexedSceneRecord> heuristicScenes,
                List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)> buildSettingsMappings)
            {
                var key = NormalizeCollectorPathKey(originalPath);
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }

                var entry = new FileEntry { OriginalPath = originalPath };
                entry.Externals.AddRange(externals);
                entry.BundleScenes.AddRange(bundleScenes);
                entry.HeuristicScenes.AddRange(heuristicScenes);
                entry.BuildSettingsMappings.AddRange(buildSettingsMappings);
                _entriesByOriginalPath[key] = entry;
            }

            internal void AppendFile(
                string originalPath,
                List<SourceFileExternalRecord>? externals,
                List<IndexedSceneRecord>? bundleScenes,
                List<IndexedSceneRecord>? heuristicScenes,
                List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)>? buildSettingsMappings)
            {
                var key = NormalizeCollectorPathKey(originalPath);
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }

                var entry = _entriesByOriginalPath.GetOrAdd(key, _ => new FileEntry { OriginalPath = originalPath });
                lock (entry)
                {
                    if (externals != null && externals.Count > 0)
                    {
                        entry.Externals.AddRange(externals);
                    }
                    if (bundleScenes != null && bundleScenes.Count > 0)
                    {
                        entry.BundleScenes.AddRange(bundleScenes);
                    }
                    if (heuristicScenes != null && heuristicScenes.Count > 0)
                    {
                        entry.HeuristicScenes.AddRange(heuristicScenes);
                    }
                    if (buildSettingsMappings != null && buildSettingsMappings.Count > 0)
                    {
                        entry.BuildSettingsMappings.AddRange(buildSettingsMappings);
                    }
                }
            }

            internal void CopyTo(
                List<SourceFileExternalRecord> externalsOut,
                List<IndexedSceneRecord> bundleScenesOut,
                List<IndexedSceneRecord> heuristicScenesOut,
                List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)> buildSettingsMappingsOut)
            {
                foreach (var entry in _entriesByOriginalPath.Values)
                {
                    externalsOut.AddRange(entry.Externals);
                    bundleScenesOut.AddRange(entry.BundleScenes);
                    heuristicScenesOut.AddRange(entry.HeuristicScenes);
                    buildSettingsMappingsOut.AddRange(entry.BuildSettingsMappings);
                }
            }

            private static string NormalizeCollectorPathKey(string? path)
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
        }

        internal static SceneIndexingMetadataCollector GetOrCreateSceneMetadataCollector(AssetsManager assetsManager)
        {
            ArgumentNullException.ThrowIfNull(assetsManager);
            return SceneMetadataCollectors.GetOrCreateValue(assetsManager);
        }

        internal static SceneIndexingMetadataCollector ResetSceneMetadataCollector(AssetsManager assetsManager)
        {
            var collector = GetOrCreateSceneMetadataCollector(assetsManager);
            collector.Clear();
            return collector;
        }

        internal static void RecordFromLazyIndexedSerializedFile(
            SerializedFile sf,
            AssetBundle? bundle,
            BuildSettings? buildSettings,
            bool hasGameObjects,
            SceneIndexingMetadataCollector collector)
        {
            if (sf == null || collector == null)
            {
                return;
            }

            var origPath = !string.IsNullOrWhiteSpace(sf.originalPath) ? sf.originalPath : (sf.fullName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(origPath))
            {
                return;
            }

            List<SourceFileExternalRecord>? externals = null;
            if (sf.m_Externals != null && sf.m_Externals.Count > 0)
            {
                externals = new List<SourceFileExternalRecord>(sf.m_Externals.Count);
                RecordSerializedFileExternals(sf, origPath, externals);
            }

            List<IndexedSceneRecord>? bundleScenes = null;
            if (bundle != null
                && ((bundle.m_Container != null && bundle.m_Container.Length > 0)
                    || (bundle.m_SceneHashes != null && bundle.m_SceneHashes.Length > 0)))
            {
                bundleScenes = new List<IndexedSceneRecord>();
                ExtractAssetBundleUnityScenes(bundle, sf.fileName, origPath, bundleScenes);
            }

            List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)>? buildSettingsMappings = null;
            if (buildSettings != null)
            {
                buildSettingsMappings = new List<(string, int, string, string)>();
                foreach (var (levelName, buildIdx, scenePath) in buildSettings.GetLevelSceneMappings())
                {
                    if (!string.IsNullOrWhiteSpace(scenePath))
                    {
                        buildSettingsMappings.Add((levelName, buildIdx, scenePath, origPath));
                    }
                }
            }

            List<IndexedSceneRecord>? heuristicScenes = null;
            var hasRenderSettings = HasRenderSettingsObject(sf);
            if (!IsSharedAssetsCompanionName(sf.fileName))
            {
                if (hasGameObjects && IsFallbackSceneSourceName(sf.fileName, origPath))
                {
                    heuristicScenes = new List<IndexedSceneRecord>(1)
                    {
                        new IndexedSceneRecord
                        {
                            Name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(origPath),
                            ScenePath = sf.fileName ?? string.Empty,
                            BuildIndex = TryParseLevelBuildIndex(sf.fileName),
                            EntrySerializedFileName = sf.fileName ?? string.Empty,
                            EntryOriginalPath = origPath,
                            ContainerKey = string.Empty,
                            SourceKind = "Heuristic"
                        }
                    };
                }
                else if (hasRenderSettings)
                {
                    heuristicScenes = new List<IndexedSceneRecord>(1)
                    {
                        new IndexedSceneRecord
                        {
                            Name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(origPath),
                            ScenePath = sf.fileName ?? string.Empty,
                            BuildIndex = TryParseLevelBuildIndex(sf.fileName),
                            EntrySerializedFileName = sf.fileName ?? string.Empty,
                            EntryOriginalPath = origPath,
                            ContainerKey = string.Empty,
                            SourceKind = "SceneBundle"
                        }
                    };
                }
            }

            if ((externals != null && externals.Count > 0)
                || (bundleScenes != null && bundleScenes.Count > 0)
                || (heuristicScenes != null && heuristicScenes.Count > 0)
                || (buildSettingsMappings != null && buildSettingsMappings.Count > 0))
            {
                collector.AppendFile(origPath, externals, bundleScenes, heuristicScenes, buildSettingsMappings);
            }
        }

        /// <summary>
        /// Reads ONLY <see cref="SerializedFile"/> headers (<c>m_Externals</c>) plus tiny <see cref="BuildSettings"/>
        /// and <see cref="AssetBundle"/> objects (zero full-object deserialization, skipping <c>.resS</c> payloads)
        /// so scene metadata and cross-file dependencies persist into Schema v16 without bloating memory.
        /// </summary>
        public static void TryCollectHeaderExternalsAndScenesFromFile(
            string filePath,
            DecompressedBundleCache? decompressionCache,
            string? specifyUnityVersion,
            SceneIndexingMetadataCollector collector)
        {
            if (collector == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            try
            {
                var externals = new List<SourceFileExternalRecord>();
                var bundleScenes = new List<IndexedSceneRecord>();
                var heuristicScenes = new List<IndexedSceneRecord>();
                var buildSettingsMappings = new List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)>();

                using var reader = new FileReader(filePath);
                if (reader.FileType == FileType.AssetsFile)
                {
                    CollectFromSerializedFileHeader(
                        reader,
                        originalPath: filePath,
                        specifyUnityVersion: specifyUnityVersion,
                        externals: externals,
                        bundleScenes: bundleScenes,
                        heuristicScenes: heuristicScenes,
                        buildSettingsMappings: buildSettingsMappings);
                }
                else if (reader.FileType == FileType.BundleFile)
                {
                    if (decompressionCache == null && ShouldSkipLargeUncachedLzmaBundle(filePath))
                    {
                        return;
                    }

                    using var bundleFile = new BundleFile(
                        reader,
                        decompressionCache,
                        allowParallelDecompression: false,
                        lowMemoryThresholdOverride: null,
                        serializedFilesOnly: true);

                    CollectFromBundleNodes(bundleFile);

                    // Further bundles stored back to back in the same file (BUNDLE-02) belong to the same source file.
                    foreach (var segment in BundleFile.FindFollowingSegments(reader.BaseStream, bundleFile))
                    {
                        using var segmentReader = BundleFile.OpenSegmentReader(reader, segment);
                        if (segmentReader.FileType != FileType.BundleFile)
                        {
                            break;
                        }

                        using var segmentBundle = new BundleFile(
                            segmentReader,
                            decompressionCache: null,
                            allowParallelDecompression: false,
                            lowMemoryThresholdOverride: null,
                            serializedFilesOnly: true);
                        CollectFromBundleNodes(segmentBundle);
                    }
                }
                else
                {
                    return;
                }

                if (externals.Count > 0 || bundleScenes.Count > 0 || heuristicScenes.Count > 0 || buildSettingsMappings.Count > 0)
                {
                    collector.RecordFile(filePath, externals, bundleScenes, heuristicScenes, buildSettingsMappings);
                }

                void CollectFromBundleNodes(BundleFile bundle)
                {
                    if (bundle.fileList != null)
                    {
                        for (var i = 0; i < bundle.fileList.Length; i++)
                        {
                            var streamFile = bundle.fileList[i];
                            if (streamFile?.stream == null || string.IsNullOrWhiteSpace(streamFile.path))
                            {
                                continue;
                            }

                            var leaf = SQLiteProjectIndexCache.NormalizeExternalLeafName(streamFile.path);
                            if (leaf.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                || leaf.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            try
                            {
                                streamFile.stream.Position = 0;
                                using var subReader = new FileReader(streamFile.path, streamFile.stream);
                                if (subReader.FileType == FileType.AssetsFile)
                                {
                                    CollectFromSerializedFileHeader(
                                        subReader,
                                        originalPath: filePath,
                                        specifyUnityVersion: specifyUnityVersion,
                                        externals: externals,
                                        bundleScenes: bundleScenes,
                                        heuristicScenes: heuristicScenes,
                                        buildSettingsMappings: buildSettingsMappings);
                                }
                            }
                            catch
                            {
                                // Skip non-serialized or truncated bundle entries safely.
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Header scene/external metadata scan skipped '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        public static SceneDiscoveryResult DiscoverScenesAndExternals(
            AssetsManager? assetsManager,
            string? folderPath,
            IReadOnlyList<AssetHandle>? handles = null,
            SceneIndexingMetadataCollector? collectorOverride = null,
            IReadOnlyList<ParsedAddressablesCatalog>? catalogsOverride = null)
        {
            var result = new SceneDiscoveryResult();
            var externalsRaw = new List<SourceFileExternalRecord>();
            var bundleScenesRaw = new List<IndexedSceneRecord>();
            var heuristicScenesRaw = new List<IndexedSceneRecord>();
            var buildSettingsMappings = new List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)>();

            // 1. Harvest anything already collected during lazy indexing.
            var collector = collectorOverride ?? (assetsManager != null && SceneMetadataCollectors.TryGetValue(assetsManager, out var attached) ? attached : null);
            collector?.CopyTo(externalsRaw, bundleScenesRaw, heuristicScenesRaw, buildSettingsMappings);

            // 2. Harvest any currently loaded SerializedFiles in AssetsManager (Eager mode or loaded files).
            var loadedFileStats = new Dictionary<string, (int GameObjectCount, int RendererCount, bool HasRenderSettings, string OriginalPath)>(StringComparer.OrdinalIgnoreCase);
            var mbNamesBySfName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            static void RecordCandidatePresetMonoBehaviourName(Dictionary<string, HashSet<string>> targetMap, string? sfName, string? mbName)
            {
                if (string.IsNullOrWhiteSpace(sfName)
                    || string.IsNullOrWhiteSpace(mbName)
                    || mbName.Length < 7
                    || mbName.StartsWith("Unnamed_", StringComparison.OrdinalIgnoreCase)
                    || mbName.IndexOf('_') < 0)
                {
                    return;
                }

                if (!targetMap.TryGetValue(sfName, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    targetMap[sfName] = set;
                }

                if (set.Count < 128)
                {
                    set.Add(mbName);
                }
            }

            if (assetsManager != null)
            {
                SerializedFile[] loadedSnapshot;
                lock (assetsManager.loadLock)
                {
                    loadedSnapshot = assetsManager.assetsFileList.ToArray();
                }

                for (var i = 0; i < loadedSnapshot.Length; i++)
                {
                    var sf = loadedSnapshot[i];
                    if (sf == null)
                    {
                        continue;
                    }

                    CollectFromLoadedSerializedFile(sf, externalsRaw, bundleScenesRaw, heuristicScenesRaw, buildSettingsMappings);

                    if (!string.IsNullOrWhiteSpace(sf.fileName))
                    {
                        var (goCount, rendererCount, hasRs) = CountSceneObjectsInSerializedFile(sf);
                        var orig = !string.IsNullOrWhiteSpace(sf.originalPath) ? sf.originalPath : (sf.fullName ?? string.Empty);
                        loadedFileStats[sf.fileName] = (goCount, rendererCount, hasRs, orig);

                        if (!hasRs && rendererCount > 0 && sf.Objects != null)
                        {
                            AssetStudio.Object[] objs;
                            lock (sf)
                            {
                                objs = sf.Objects.ToArray();
                            }

                            for (var o = 0; o < objs.Length; o++)
                            {
                                if (objs[o] is MonoBehaviour mb && (mb.m_GameObject == null || mb.m_GameObject.IsNull))
                                {
                                    RecordCandidatePresetMonoBehaviourName(mbNamesBySfName, sf.fileName, mb.m_Name);
                                }
                            }
                        }
                    }
                }
            }

            // 3. Inspect handles (BuildSettings handles not yet parsed, .unity containers, RenderSettings streamed scenes, and fallback level*/mainData/*scene*).
            var pathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var sourceByFileName = new Dictionary<string, (string SerializedFileName, string OriginalPath)>(StringComparer.OrdinalIgnoreCase);
            var sourceFilesWithGameObjects = new Dictionary<string, (string SerializedFileName, string OriginalPath)>(StringComparer.OrdinalIgnoreCase);
            var sourceFilesWithRenderSettings = new Dictionary<string, (string SerializedFileName, string OriginalPath)>(StringComparer.OrdinalIgnoreCase);
            var buildSettingsFilesToProbe = new HashSet<string>(pathComparer);
            var bundleContainerFilesByPath = new Dictionary<string, (string SerializedFileName, string OriginalPath)>(StringComparer.OrdinalIgnoreCase);
            var sourceByOrigPath = new Dictionary<string, string>(pathComparer);
            var scenePrimaryFileByOrigPath = new Dictionary<string, (string SerializedFileName, int Priority)>(pathComparer);
            var fileStatsBySfName = new Dictionary<string, (int GameObjectCount, int RendererCount, bool HasRenderSettings, string OriginalPath, string BestGoName, int BestGoScore)>(StringComparer.OrdinalIgnoreCase);
            var seenRawOrigPaths = new HashSet<string>(pathComparer);

            void UpdateScenePrimaryForOrigPath(string origPath, string sfName, int goCount, bool hasRenderSettings)
            {
                if (string.IsNullOrWhiteSpace(origPath) || string.IsNullOrWhiteSpace(sfName))
                {
                    return;
                }

                var isShared = IsSharedAssetsCompanionName(sfName);
                var priority = (hasRenderSettings && !isShared) ? 4
                    : (goCount > 0 && !isShared) ? 3
                    : hasRenderSettings ? 2
                    : goCount > 0 ? 1
                    : 0;
                var normalizedOrig = SafeNormalizePath(origPath);
                if (!scenePrimaryFileByOrigPath.TryGetValue(normalizedOrig, out var existing) || priority > existing.Priority)
                {
                    scenePrimaryFileByOrigPath[normalizedOrig] = (sfName, priority);
                }
            }

            foreach (var kvp in loadedFileStats)
            {
                fileStatsBySfName[kvp.Key] = (kvp.Value.GameObjectCount, kvp.Value.RendererCount, kvp.Value.HasRenderSettings, kvp.Value.OriginalPath, string.Empty, 0);
                UpdateScenePrimaryForOrigPath(kvp.Value.OriginalPath, kvp.Key, kvp.Value.GameObjectCount, kvp.Value.HasRenderSettings);
                if (kvp.Value.HasRenderSettings && !IsSharedAssetsCompanionName(kvp.Key))
                {
                    sourceFilesWithRenderSettings.TryAdd(kvp.Key, (kvp.Key, kvp.Value.OriginalPath));
                }
            }

            var handleSource = handles ?? (assetsManager?.ProjectIndex?.Count > 0 ? assetsManager.ProjectIndex.GetHandles().ToList() : null);
            if (handleSource != null)
            {
                string? lastSfName = null;
                string? lastOrigPath = null;
                var runGoCount = 0;
                var runRendererCount = 0;
                var runHasRenderSettings = false;
                var runHasSceneAsset = false;
                string? runBestGoName = null;
                var runBestGoScore = 0;
                var runBestGoPathRank = ulong.MaxValue;

                static ulong ComputeSceneObjectPathRank(long pathId)
                {
                    if (pathId == 0)
                    {
                        return ulong.MaxValue;
                    }

                    // Unity scene-authored top-level objects use small positive sequential PathIDs,
                    // while nested prefab instances use large 64-bit hashed PathIDs (often negative as signed Int64).
                    return pathId > 0 ? (ulong)pathId : ulong.MaxValue - 1UL;
                }

                void FlushCurrentSfRun()
                {
                    if (string.IsNullOrEmpty(lastSfName))
                    {
                        return;
                    }

                    var orig = lastOrigPath ?? string.Empty;
                    if (fileStatsBySfName.TryGetValue(lastSfName, out var prev))
                    {
                        var totalGo = prev.GameObjectCount + runGoCount;
                        var totalRend = prev.RendererCount + runRendererCount;
                        var totalRs = prev.HasRenderSettings || runHasRenderSettings;
                        var bestName = runBestGoScore > prev.BestGoScore ? (runBestGoName ?? string.Empty) : prev.BestGoName;
                        var bestScore = Math.Max(prev.BestGoScore, runBestGoScore);
                        var bestOrig = !string.IsNullOrEmpty(prev.OriginalPath) ? prev.OriginalPath : orig;
                        fileStatsBySfName[lastSfName] = (totalGo, totalRend, totalRs, bestOrig, bestName, bestScore);
                        UpdateScenePrimaryForOrigPath(bestOrig, lastSfName, totalGo, totalRs);
                        if (totalRs && !IsSharedAssetsCompanionName(lastSfName))
                        {
                            sourceFilesWithRenderSettings.TryAdd(lastSfName, (lastSfName, bestOrig));
                        }
                        if (totalGo > 0 || runHasSceneAsset)
                        {
                            sourceFilesWithGameObjects.TryAdd(lastSfName, (lastSfName, bestOrig));
                        }
                    }
                    else
                    {
                        fileStatsBySfName[lastSfName] = (runGoCount, runRendererCount, runHasRenderSettings, orig, runBestGoName ?? string.Empty, runBestGoScore);
                        UpdateScenePrimaryForOrigPath(orig, lastSfName, runGoCount, runHasRenderSettings);
                        if (runHasRenderSettings && !IsSharedAssetsCompanionName(lastSfName))
                        {
                            sourceFilesWithRenderSettings.TryAdd(lastSfName, (lastSfName, orig));
                        }
                        if (runGoCount > 0 || runHasSceneAsset)
                        {
                            sourceFilesWithGameObjects.TryAdd(lastSfName, (lastSfName, orig));
                        }
                    }

                    runGoCount = 0;
                    runRendererCount = 0;
                    runHasRenderSettings = false;
                    runHasSceneAsset = false;
                    runBestGoName = null;
                    runBestGoScore = 0;
                    runBestGoPathRank = ulong.MaxValue;
                }

                for (var i = 0; i < handleSource.Count; i++)
                {
                    var h = handleSource[i];
                    if (h == null)
                    {
                        continue;
                    }

                    var sfName = h.SerializedFileName ?? string.Empty;
                    var origPath = h.OriginalPath ?? string.Empty;
                    if (!ReferenceEquals(sfName, lastSfName) || !ReferenceEquals(origPath, lastOrigPath))
                    {
                        if (!string.Equals(sfName, lastSfName, StringComparison.OrdinalIgnoreCase))
                        {
                            FlushCurrentSfRun();
                        }

                        lastSfName = sfName;
                        lastOrigPath = origPath;
                        if (sfName.Length > 0 && sourceByFileName.TryAdd(sfName, (sfName, origPath)) && origPath.Length > 0)
                        {
                            var origLeaf = Path.GetFileName(origPath);
                            if (!string.IsNullOrEmpty(origLeaf))
                            {
                                sourceByFileName.TryAdd(origLeaf, (sfName, origPath));
                            }
                        }

                        if (sfName.Length > 0 && origPath.Length > 0 && seenRawOrigPaths.Add(origPath))
                        {
                            sourceByOrigPath.TryAdd(SafeNormalizePath(origPath), sfName);
                        }
                    }

                    var handleType = h.Type;
                    if (handleType == ClassIDType.GameObject)
                    {
                        runGoCount++;
                        var pathRank = ComputeSceneObjectPathRank(h.PathID);
                        // Prefer scene-authored small positive PathIDs and higher naming scores.
                        var candidateScore = ScoreSceneGameObjectName(h.Name);
                        if (candidateScore > 0)
                        {
                            if (pathRank < 1_000_000UL)
                            {
                                candidateScore += 20;
                            }

                            if (candidateScore > runBestGoScore
                                || (candidateScore == runBestGoScore && pathRank < runBestGoPathRank))
                            {
                                runBestGoScore = candidateScore;
                                runBestGoPathRank = pathRank;
                                runBestGoName = h.Name;
                            }
                        }
                    }
                    else if (handleType == ClassIDType.MeshRenderer || handleType == ClassIDType.SkinnedMeshRenderer)
                    {
                        runRendererCount++;
                    }
                    else if (handleType == ClassIDType.RenderSettings)
                    {
                        runHasRenderSettings = true;
                    }
                    else if (handleType == ClassIDType.SceneAsset)
                    {
                        runHasSceneAsset = true;
                    }
                    else if (handleType == ClassIDType.MonoBehaviour)
                    {
                        RecordCandidatePresetMonoBehaviourName(mbNamesBySfName, sfName, h.Name);
                    }
                    else if (handleType == ClassIDType.BuildSettings && origPath.Length > 0 && !buildSettingsFilesToProbe.Contains(origPath) && File.Exists(origPath))
                    {
                        buildSettingsFilesToProbe.Add(origPath);
                    }

                    var container = h.Container;
                    if (container != null
                        && container.Length >= 6
                        && (container[container.Length - 1] == 'y' || container[container.Length - 1] == 'Y')
                        && container.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        var normalizedContainer = NormalizeScenePath(container);
                        bundleContainerFilesByPath.TryAdd(normalizedContainer, (sfName, origPath));
                    }
                }

                FlushCurrentSfRun();
            }

            foreach (var kvp in fileStatsBySfName)
            {
                result.SceneFileStats[kvp.Key] = (kvp.Value.GameObjectCount, kvp.Value.RendererCount);
            }

            // If BuildSettings wasn't captured yet (e.g. direct SaveIndexCache or warm cache without collector), probe just that tiny file!
            if (buildSettingsMappings.Count == 0 && buildSettingsFilesToProbe.Count > 0)
            {
                var tempCollector = new SceneIndexingMetadataCollector();
                foreach (var bsPath in buildSettingsFilesToProbe)
                {
                    TryCollectHeaderExternalsAndScenesFromFile(
                        bsPath,
                        assetsManager?.DecompressionCache,
                        assetsManager?.SpecifyUnityVersion,
                        tempCollector);
                }
                tempCollector.CopyTo(externalsRaw, bundleScenesRaw, heuristicScenesRaw, buildSettingsMappings);
            }

            // Load Addressables catalogs ahead of merge so multi-scene bundles can probe AssetBundle.m_SceneHashes when needed.
            var catalogs = catalogsOverride
                ?? (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath)
                    ? AddressablesCatalogDependencyResolver.LoadCatalogsForFiles(Array.Empty<string>(), folderPath)
                    : (IReadOnlyList<ParsedAddressablesCatalog>)Array.Empty<ParsedAddressablesCatalog>());

            if (catalogs.Count > 0)
            {
                var addressablesScenesPerBundle = new Dictionary<string, int>(pathComparer);
                var existingBundleScenePaths = new HashSet<string>(
                    bundleScenesRaw.Select(static s => NormalizeScenePath(s.ScenePath)).Where(static p => p.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                var multiSceneBundlesToProbe = new HashSet<string>(pathComparer);

                for (var c = 0; c < catalogs.Count; c++)
                {
                    var cat = catalogs[c];
                    if (cat?.SceneEntries == null) continue;
                    for (var s = 0; s < cat.SceneEntries.Count; s++)
                    {
                        var entry = cat.SceneEntries[s];
                        if (entry == null) continue;
                        var resolvedBundle = !string.IsNullOrWhiteSpace(entry.PrimaryBundleInternalId)
                            ? AddressablesCatalogDependencyResolver.TryResolveLocalBundlePath(cat, entry.PrimaryBundleInternalId, folderPath)
                            : null;
                        if (string.IsNullOrEmpty(resolvedBundle) && !string.IsNullOrWhiteSpace(entry.PrimaryBundleFileName))
                        {
                            resolvedBundle = AddressablesCatalogDependencyResolver.TryResolveLocalBundlePath(cat, entry.PrimaryBundleFileName, folderPath);
                        }

                        if (!string.IsNullOrEmpty(resolvedBundle) && File.Exists(resolvedBundle))
                        {
                            var normScene = NormalizeScenePath(!string.IsNullOrWhiteSpace(entry.InternalId) ? entry.InternalId : entry.PrimaryKey);
                            addressablesScenesPerBundle.TryGetValue(resolvedBundle, out var prevCount);
                            var nextCount = prevCount + 1;
                            addressablesScenesPerBundle[resolvedBundle] = nextCount;
                            if (nextCount > 1 && !existingBundleScenePaths.Contains(normScene))
                            {
                                multiSceneBundlesToProbe.Add(resolvedBundle);
                            }
                        }
                    }
                }

                if (multiSceneBundlesToProbe.Count > 0)
                {
                    var multiSceneCollector = new SceneIndexingMetadataCollector();
                    foreach (var multiBundlePath in multiSceneBundlesToProbe)
                    {
                        TryCollectHeaderExternalsAndScenesFromFile(
                            multiBundlePath,
                            assetsManager?.DecompressionCache,
                            assetsManager?.SpecifyUnityVersion,
                            multiSceneCollector);
                    }
                    multiSceneCollector.CopyTo(externalsRaw, bundleScenesRaw, heuristicScenesRaw, buildSettingsMappings);
                }
            }

            // Also include any .unity container paths found on AssetHandles.
            foreach (var kvp in bundleContainerFilesByPath)
            {
                var scenePath = kvp.Key;
                var displayName = BuildSettings.ExtractSceneDisplayName(scenePath);
                bundleScenesRaw.Add(new IndexedSceneRecord
                {
                    Name = string.IsNullOrWhiteSpace(displayName) ? kvp.Value.SerializedFileName : displayName,
                    ScenePath = scenePath,
                    BuildIndex = -1,
                    EntrySerializedFileName = kvp.Value.SerializedFileName,
                    EntryOriginalPath = kvp.Value.OriginalPath,
                    ContainerKey = scenePath,
                    SourceKind = "AssetBundle"
                });
            }

            // Deduplicate SourceFileExternals deterministically.
            var seenExternalKeys = new HashSet<string>(pathComparer);
            for (var i = 0; i < externalsRaw.Count; i++)
            {
                var ext = externalsRaw[i];
                if (ext == null)
                {
                    continue;
                }

                var dedupKey = $"{ext.SourceSerializedFileName}\u001f{ext.SourceOriginalPath}\u001f{ext.ExternalIndex.ToString(CultureInfo.InvariantCulture)}";
                if (seenExternalKeys.Add(dedupKey))
                {
                    result.Externals.Add(ext);
                }
            }

            // Merge & deduplicate discovered scenes by priority: (a) BuildSettings -> (b) AssetBundle -> (c) Addressables -> (d) Streamed SceneBundle (RenderSettings) -> (e) Heuristic.
            var mergedScenes = new List<IndexedSceneRecord>();
            var coveredFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredScenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredOrigPaths = new HashSet<string>(pathComparer);

            string ResolveBestSceneSerializedFileName(string? candidateSfName, string? originalPath)
            {
                var current = candidateSfName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(originalPath))
                {
                    return current;
                }

                var normalizedOrig = SafeNormalizePath(originalPath);
                if (!scenePrimaryFileByOrigPath.TryGetValue(normalizedOrig, out var preferred) || preferred.Priority <= 0)
                {
                    return current;
                }

                // Retarget empty names, .sharedAssets metadata companions, or 0-GameObject metadata CABs
                // to the sibling scene SerializedFile in the same bundle (e.g. CAB-<hash> instead of CAB-<hash>.sharedAssets).
                var shouldRetarget = string.IsNullOrWhiteSpace(current)
                    || IsSharedAssetsCompanionName(current)
                    || (fileStatsBySfName.TryGetValue(current, out var curStats)
                        && curStats.GameObjectCount == 0
                        && !curStats.HasRenderSettings);

                return shouldRetarget ? preferred.SerializedFileName : current;
            }

            void MarkSceneCovered(string? rawSfName, string? resolvedSfName, string? scenePath, string? origPath)
            {
                if (!string.IsNullOrEmpty(rawSfName))
                {
                    coveredFileNames.Add(rawSfName);
                }
                if (!string.IsNullOrEmpty(resolvedSfName))
                {
                    coveredFileNames.Add(resolvedSfName);
                }
                if (!string.IsNullOrEmpty(scenePath))
                {
                    coveredScenePaths.Add(scenePath);
                }
                if (!string.IsNullOrWhiteSpace(origPath))
                {
                    coveredOrigPaths.Add(SafeNormalizePath(origPath));
                }
            }

            // (a) BuildSettings scenes
            var seenBuildIndices = new HashSet<int>();
            foreach (var mapping in buildSettingsMappings.OrderBy(static m => m.BuildIndex))
            {
                if (string.IsNullOrWhiteSpace(mapping.ScenePath) || !seenBuildIndices.Add(mapping.BuildIndex))
                {
                    continue;
                }

                var normalizedPath = NormalizeScenePath(mapping.ScenePath);
                var displayName = BuildSettings.ExtractSceneDisplayName(normalizedPath);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = mapping.LevelFileName;
                }

                var entrySfName = mapping.LevelFileName;
                var entryOrigPath = string.Empty;
                if (sourceByFileName.TryGetValue(mapping.LevelFileName, out var matchedSource))
                {
                    entrySfName = matchedSource.SerializedFileName;
                    entryOrigPath = matchedSource.OriginalPath;
                }
                else if (!string.IsNullOrWhiteSpace(mapping.SourceOriginalPath))
                {
                    var siblingDir = Path.GetDirectoryName(mapping.SourceOriginalPath);
                    if (!string.IsNullOrEmpty(siblingDir))
                    {
                        var candidate = Path.Combine(siblingDir, mapping.LevelFileName);
                        if (File.Exists(candidate))
                        {
                            entryOrigPath = candidate;
                        }
                    }
                }

                mergedScenes.Add(new IndexedSceneRecord
                {
                    Name = displayName,
                    ScenePath = normalizedPath,
                    BuildIndex = mapping.BuildIndex,
                    EntrySerializedFileName = entrySfName,
                    EntryOriginalPath = entryOrigPath,
                    ContainerKey = string.Empty,
                    SourceKind = "BuildSettings"
                });

                MarkSceneCovered(mapping.LevelFileName, entrySfName, normalizedPath, entryOrigPath);
            }

            // (b) AssetBundle .unity container entries
            for (var i = 0; i < bundleScenesRaw.Count; i++)
            {
                var scene = bundleScenesRaw[i];
                if (scene == null)
                {
                    continue;
                }

                var normalizedPath = NormalizeScenePath(scene.ScenePath);
                if (!string.IsNullOrEmpty(normalizedPath) && coveredScenePaths.Contains(normalizedPath))
                {
                    continue;
                }

                var resolvedSfName = ResolveBestSceneSerializedFileName(scene.EntrySerializedFileName, scene.EntryOriginalPath);
                mergedScenes.Add(new IndexedSceneRecord
                {
                    Name = string.IsNullOrWhiteSpace(scene.Name)
                        ? BuildSettings.ExtractSceneDisplayName(normalizedPath)
                        : scene.Name,
                    ScenePath = normalizedPath,
                    BuildIndex = scene.BuildIndex,
                    EntrySerializedFileName = resolvedSfName,
                    EntryOriginalPath = scene.EntryOriginalPath,
                    ContainerKey = string.IsNullOrWhiteSpace(scene.ContainerKey) ? normalizedPath : scene.ContainerKey,
                    SourceKind = "AssetBundle"
                });

                MarkSceneCovered(scene.EntrySerializedFileName, resolvedSfName, normalizedPath, scene.EntryOriginalPath);
            }

            // (c) Addressables SceneEntries (SceneProvider)
            for (var c = 0; c < catalogs.Count; c++)
            {
                var catalog = catalogs[c];
                if (catalog?.SceneEntries == null)
                {
                    continue;
                }

                for (var s = 0; s < catalog.SceneEntries.Count; s++)
                {
                    var entry = catalog.SceneEntries[s];
                    if (entry == null)
                    {
                        continue;
                    }

                    var rawScenePath = !string.IsNullOrWhiteSpace(entry.InternalId) ? entry.InternalId : entry.PrimaryKey;
                    var normalizedPath = NormalizeScenePath(rawScenePath);
                    if (!string.IsNullOrEmpty(normalizedPath) && coveredScenePaths.Contains(normalizedPath))
                    {
                        continue;
                    }

                    var resolvedBundlePath = !string.IsNullOrWhiteSpace(entry.PrimaryBundleInternalId)
                        ? AddressablesCatalogDependencyResolver.TryResolveLocalBundlePath(catalog, entry.PrimaryBundleInternalId, folderPath)
                        : null;
                    if (string.IsNullOrEmpty(resolvedBundlePath) && !string.IsNullOrWhiteSpace(entry.PrimaryBundleFileName))
                    {
                        resolvedBundlePath = AddressablesCatalogDependencyResolver.TryResolveLocalBundlePath(catalog, entry.PrimaryBundleFileName, folderPath);
                    }

                    var entrySfName = string.Empty;
                    if (!string.IsNullOrEmpty(resolvedBundlePath))
                    {
                        var normBundlePath = SafeNormalizePath(resolvedBundlePath);
                        if (scenePrimaryFileByOrigPath.TryGetValue(normBundlePath, out var preferredSf) && preferredSf.Priority > 0)
                        {
                            entrySfName = preferredSf.SerializedFileName;
                        }
                        else if (sourceByOrigPath.TryGetValue(normBundlePath, out var mappedSf))
                        {
                            entrySfName = mappedSf;
                        }
                    }

                    if (string.IsNullOrEmpty(entrySfName) && !string.IsNullOrWhiteSpace(entry.PrimaryBundleFileName))
                    {
                        entrySfName = entry.PrimaryBundleFileName;
                    }

                    var displayName = BuildSettings.ExtractSceneDisplayName(normalizedPath);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = !string.IsNullOrWhiteSpace(entry.PrimaryKey) ? entry.PrimaryKey : entrySfName;
                    }

                    mergedScenes.Add(new IndexedSceneRecord
                    {
                        Name = displayName,
                        ScenePath = normalizedPath,
                        BuildIndex = -1,
                        EntrySerializedFileName = entrySfName,
                        EntryOriginalPath = resolvedBundlePath ?? string.Empty,
                        ContainerKey = !string.IsNullOrWhiteSpace(entry.PrimaryKey) ? entry.PrimaryKey : normalizedPath,
                        SourceKind = "Addressables"
                    });

                    MarkSceneCovered(entry.PrimaryBundleFileName, entrySfName, normalizedPath, resolvedBundlePath);
                }
            }

            // (d) Streamed Unity Scene AssetBundles detected via engine-native RenderSettings (ClassID 104)
            // — catches obfuscated/hashed-key streamed scene bundles that have no .unity suffix or SceneProvider.
            var renderSettingsCountByOrigPath = new Dictionary<string, int>(pathComparer);
            foreach (var kvp in sourceFilesWithRenderSettings)
            {
                var (_, origPath) = kvp.Value;
                if (!string.IsNullOrWhiteSpace(origPath))
                {
                    var normOrig = SafeNormalizePath(origPath);
                    renderSettingsCountByOrigPath.TryGetValue(normOrig, out var c);
                    renderSettingsCountByOrigPath[normOrig] = c + 1;
                }
            }

            foreach (var kvp in sourceFilesWithRenderSettings)
            {
                var (sfName, origPath) = kvp.Value;
                if (string.IsNullOrWhiteSpace(sfName) || IsSharedAssetsCompanionName(sfName))
                {
                    continue;
                }

                var normOrig = !string.IsNullOrWhiteSpace(origPath) ? SafeNormalizePath(origPath) : string.Empty;
                var hasMultipleScenesInSameBundle = normOrig.Length > 0
                    && renderSettingsCountByOrigPath.TryGetValue(normOrig, out var rsCount)
                    && rsCount > 1;

                if (coveredFileNames.Contains(sfName)
                    || (!hasMultipleScenesInSameBundle && normOrig.Length > 0 && coveredOrigPaths.Contains(normOrig)))
                {
                    continue;
                }

                var bestGoName = fileStatsBySfName.TryGetValue(sfName, out var stats) ? stats.BestGoName : string.Empty;
                var buildIdx = TryParseLevelBuildIndex(sfName);
                var isLevelFile = buildIdx >= 0 || string.Equals(sfName, "mainData", StringComparison.OrdinalIgnoreCase);
                var displayName = isLevelFile
                    ? sfName
                    : BuildStreamedSceneDisplayName(sfName, origPath, bestGoName);

                mergedScenes.Add(new IndexedSceneRecord
                {
                    Name = displayName,
                    ScenePath = sfName,
                    BuildIndex = buildIdx,
                    EntrySerializedFileName = sfName,
                    EntryOriginalPath = origPath,
                    ContainerKey = string.Empty,
                    SourceKind = isLevelFile ? "Heuristic" : "SceneBundle"
                });

                MarkSceneCovered(sfName, sfName, sfName, origPath);
            }

            // (e) Fallback level* / mainData / *scene* serialized files not already covered by (a)-(d)
            foreach (var heuristic in heuristicScenesRaw)
            {
                if (heuristic == null)
                {
                    continue;
                }

                var sfName = heuristic.EntrySerializedFileName;
                if (IsSharedAssetsCompanionName(sfName))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(sfName) && coveredFileNames.Contains(sfName))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(heuristic.EntryOriginalPath)
                    && coveredOrigPaths.Contains(SafeNormalizePath(heuristic.EntryOriginalPath)))
                {
                    continue;
                }

                var normalizedPath = NormalizeScenePath(heuristic.ScenePath);
                if (!string.IsNullOrEmpty(normalizedPath) && coveredScenePaths.Contains(normalizedPath))
                {
                    continue;
                }

                var finalRecord = heuristic;
                if (string.Equals(heuristic.SourceKind, "SceneBundle", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(sfName)
                    && string.Equals(heuristic.Name, sfName, StringComparison.OrdinalIgnoreCase)
                    && fileStatsBySfName.TryGetValue(sfName, out var hStats)
                    && !string.IsNullOrWhiteSpace(hStats.BestGoName))
                {
                    finalRecord = new IndexedSceneRecord
                    {
                        Name = BuildStreamedSceneDisplayName(sfName, heuristic.EntryOriginalPath, hStats.BestGoName),
                        ScenePath = heuristic.ScenePath,
                        BuildIndex = heuristic.BuildIndex,
                        EntrySerializedFileName = heuristic.EntrySerializedFileName,
                        EntryOriginalPath = heuristic.EntryOriginalPath,
                        ContainerKey = heuristic.ContainerKey,
                        SourceKind = heuristic.SourceKind
                    };
                }

                mergedScenes.Add(finalRecord);
                MarkSceneCovered(sfName, sfName, normalizedPath, heuristic.EntryOriginalPath);
            }

            foreach (var kvp in sourceFilesWithGameObjects)
            {
                var (sfName, origPath) = kvp.Value;
                if (IsSharedAssetsCompanionName(sfName) || !IsFallbackSceneSourceName(sfName, origPath))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(sfName) && coveredFileNames.Contains(sfName))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(origPath) && coveredOrigPaths.Contains(SafeNormalizePath(origPath)))
                {
                    continue;
                }

                var displayName = !string.IsNullOrWhiteSpace(sfName)
                    ? sfName
                    : Path.GetFileNameWithoutExtension(origPath);
                mergedScenes.Add(new IndexedSceneRecord
                {
                    Name = displayName,
                    ScenePath = sfName,
                    BuildIndex = TryParseLevelBuildIndex(sfName),
                    EntrySerializedFileName = sfName,
                    EntryOriginalPath = origPath,
                    ContainerKey = string.Empty,
                    SourceKind = "Heuristic"
                });

                MarkSceneCovered(sfName, sfName, sfName, origPath);
            }

            // Assign deterministic SceneIds (1..N)
            var orderedScenes = mergedScenes
                .OrderBy(static s => s.BuildIndex >= 0 ? 0 : 1)
                .ThenBy(static s => s.BuildIndex)
                .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ScenePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < orderedScenes.Count; i++)
            {
                var s = orderedScenes[i];
                result.Scenes.Add(new IndexedSceneRecord
                {
                    SceneId = i + 1,
                    Name = s.Name,
                    ScenePath = s.ScenePath,
                    BuildIndex = s.BuildIndex,
                    EntrySourceFileId = s.EntrySourceFileId,
                    EntrySerializedFileName = s.EntrySerializedFileName,
                    EntryOriginalPath = s.EntryOriginalPath,
                    ContainerKey = s.ContainerKey,
                    SourceKind = s.SourceKind
                });
            }

            // Match modular companion stage preset bundles (non-scene SerializedFiles with ScriptableObject presets
            // named "<StageStem>_*" and 3D renderers) in O(N) so modular scenes report complete geometry counts and bind their stage prefabs.
            if (result.Scenes.Count > 0 && mbNamesBySfName.Count > 0)
            {
                var scenesByStageStem = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < result.Scenes.Count; i++)
                {
                    var scene = result.Scenes[i];
                    if (string.IsNullOrWhiteSpace(scene.EntrySerializedFileName))
                    {
                        continue;
                    }

                    var bestGo = fileStatsBySfName.TryGetValue(scene.EntrySerializedFileName, out var st) ? st.BestGoName : null;
                    var stem = ExtractSceneStageStem(scene.Name, bestGo);
                    if (stem.Length < 6)
                    {
                        continue;
                    }

                    if (!scenesByStageStem.TryGetValue(stem, out var sfList))
                    {
                        sfList = new List<string>(1);
                        scenesByStageStem[stem] = sfList;
                    }

                    if (!sfList.Contains(scene.EntrySerializedFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        sfList.Add(scene.EntrySerializedFileName);
                    }
                }

                if (scenesByStageStem.Count > 0)
                {
                    var matchedCountPerStemInComp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (compSfName, mbNames) in mbNamesBySfName)
                    {
                        if (IsSharedAssetsCompanionName(compSfName)
                            || coveredFileNames.Contains(compSfName)
                            || !fileStatsBySfName.TryGetValue(compSfName, out var compStats)
                            || compStats.HasRenderSettings
                            || compStats.RendererCount <= 0)
                        {
                            continue;
                        }

                        matchedCountPerStemInComp.Clear();
                        foreach (var mbName in mbNames)
                        {
                            for (var u = 6; u < mbName.Length - 1; u++)
                            {
                                if (mbName[u] != '_')
                                {
                                    continue;
                                }

                                var candidateStem = mbName.Substring(0, u);
                                if (scenesByStageStem.ContainsKey(candidateStem))
                                {
                                    matchedCountPerStemInComp.TryGetValue(candidateStem, out var prevCnt);
                                    matchedCountPerStemInComp[candidateStem] = prevCnt + 1;
                                }
                            }
                        }

                        foreach (var (matchedStem, presetCount) in matchedCountPerStemInComp)
                        {
                            var estGo = Math.Max(1, compStats.GameObjectCount / Math.Max(1, presetCount));
                            var estRend = Math.Max(1, compStats.RendererCount / Math.Max(1, presetCount));

                            foreach (var sceneSfName in scenesByStageStem[matchedStem])
                            {
                                if (string.Equals(sceneSfName, compSfName, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                if (!result.CompanionStageFilesBySceneSf.TryGetValue(sceneSfName, out var compList))
                                {
                                    compList = new List<(string, string)>();
                                    result.CompanionStageFilesBySceneSf[sceneSfName] = compList;
                                }

                                if (!compList.Any(x => string.Equals(x.SerializedFileName, compSfName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    compList.Add((compSfName, compStats.OriginalPath));
                                    if (result.SceneFileStats.TryGetValue(sceneSfName, out var curSceneStats))
                                    {
                                        result.SceneFileStats[sceneSfName] = (
                                            Math.Max(0, curSceneStats.GameObjectCount) + estGo,
                                            Math.Max(0, curSceneStats.RendererCount) + estRend);
                                    }
                                    else
                                    {
                                        result.SceneFileStats[sceneSfName] = (estGo, estRend);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        public static void DiscoverAndPersistScenesAndExternals(
            SQLiteProjectIndexCache sqliteCache,
            AssetsManager? assetsManager,
            string folderPath,
            string signature,
            IReadOnlyList<AssetHandle>? handles = null)
        {
            if (sqliteCache == null || string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return;
            }

            var discovered = DiscoverScenesAndExternals(assetsManager, folderPath, handles);
            if (discovered.Externals.Count > 0)
            {
                sqliteCache.SaveSourceFileExternals(folderPath, signature, discovered.Externals, replaceExisting: true);
            }

            if (discovered.Scenes.Count > 0)
            {
                sqliteCache.SaveScenes(folderPath, signature, discovered.Scenes, replaceExisting: true);
            }

            if (assetsManager != null && SceneMetadataCollectors.TryGetValue(assetsManager, out var collector))
            {
                collector.Clear();
            }
        }

        private static void CollectFromSerializedFileHeader(
            FileReader reader,
            string originalPath,
            string? specifyUnityVersion,
            List<SourceFileExternalRecord> externals,
            List<IndexedSceneRecord> bundleScenes,
            List<IndexedSceneRecord> heuristicScenes,
            List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)> buildSettingsMappings)
        {
            reader.Position = 0;
            var sf = new SerializedFile(reader, null!)
            {
                originalPath = originalPath
            };

            if (sf.IsVersionStripped && !string.IsNullOrWhiteSpace(specifyUnityVersion))
            {
                sf.SetVersion(specifyUnityVersion);
            }

            RecordSerializedFileExternals(sf, originalPath, externals);

            var hasGameObjects = false;
            var hasRenderSettings = false;
            if (sf.m_Objects != null)
            {
                for (var i = 0; i < sf.m_Objects.Count; i++)
                {
                    var objInfo = sf.m_Objects[i];
                    if (objInfo == null)
                    {
                        continue;
                    }

                    if (objInfo.classID == (int)ClassIDType.GameObject || objInfo.classID == (int)ClassIDType.SceneAsset)
                    {
                        hasGameObjects = true;
                    }
                    else if (objInfo.classID == (int)ClassIDType.RenderSettings)
                    {
                        hasRenderSettings = true;
                    }
                    else if (objInfo.classID == (int)ClassIDType.BuildSettings)
                    {
                        try
                        {
                            var objReader = new ObjectReader(reader, sf, objInfo);
                            var bs = new BuildSettings(objReader);
                            foreach (var (levelName, buildIdx, scenePath) in bs.GetLevelSceneMappings())
                            {
                                if (!string.IsNullOrWhiteSpace(scenePath))
                                {
                                    buildSettingsMappings.Add((levelName, buildIdx, scenePath, originalPath));
                                }
                            }
                        }
                        catch
                        {
                            // Malformed/truncated BuildSettings ignored safely.
                        }
                    }
                    else if (objInfo.classID == (int)ClassIDType.AssetBundle)
                    {
                        try
                        {
                            var objReader = new ObjectReader(reader, sf, objInfo);
                            var bundle = new AssetBundle(objReader);
                            ExtractAssetBundleUnityScenes(bundle, sf.fileName, originalPath, bundleScenes);
                        }
                        catch
                        {
                            // Malformed/truncated AssetBundle ignored safely.
                        }
                    }
                }
            }

            if (!IsSharedAssetsCompanionName(sf.fileName))
            {
                if (hasGameObjects && IsFallbackSceneSourceName(sf.fileName, originalPath))
                {
                    heuristicScenes.Add(new IndexedSceneRecord
                    {
                        Name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(originalPath),
                        ScenePath = sf.fileName ?? string.Empty,
                        BuildIndex = TryParseLevelBuildIndex(sf.fileName),
                        EntrySerializedFileName = sf.fileName ?? string.Empty,
                        EntryOriginalPath = originalPath,
                        ContainerKey = string.Empty,
                        SourceKind = "Heuristic"
                    });
                }
                else if (hasRenderSettings)
                {
                    heuristicScenes.Add(new IndexedSceneRecord
                    {
                        Name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(originalPath),
                        ScenePath = sf.fileName ?? string.Empty,
                        BuildIndex = TryParseLevelBuildIndex(sf.fileName),
                        EntrySerializedFileName = sf.fileName ?? string.Empty,
                        EntryOriginalPath = originalPath,
                        ContainerKey = string.Empty,
                        SourceKind = "SceneBundle"
                    });
                }
            }
        }

        private static void CollectFromLoadedSerializedFile(
            SerializedFile sf,
            List<SourceFileExternalRecord> externals,
            List<IndexedSceneRecord> bundleScenes,
            List<IndexedSceneRecord> heuristicScenes,
            List<(string LevelFileName, int BuildIndex, string ScenePath, string SourceOriginalPath)> buildSettingsMappings)
        {
            var origPath = !string.IsNullOrWhiteSpace(sf.originalPath) ? sf.originalPath : (sf.fullName ?? string.Empty);
            RecordSerializedFileExternals(sf, origPath, externals);

            var hasGameObjects = false;
            var hasRenderSettings = false;
            AssetStudio.Object[] objectsSnapshot;
            lock (sf)
            {
                objectsSnapshot = sf.Objects != null ? sf.Objects.ToArray() : Array.Empty<AssetStudio.Object>();
            }

            for (var i = 0; i < objectsSnapshot.Length; i++)
            {
                var obj = objectsSnapshot[i];
                switch (obj)
                {
                    case BuildSettings bs:
                        foreach (var (levelName, buildIdx, scenePath) in bs.GetLevelSceneMappings())
                        {
                            if (!string.IsNullOrWhiteSpace(scenePath))
                            {
                                buildSettingsMappings.Add((levelName, buildIdx, scenePath, origPath));
                            }
                        }
                        break;
                    case AssetBundle bundle:
                        ExtractAssetBundleUnityScenes(bundle, sf.fileName, origPath, bundleScenes);
                        break;
                    case GameObject:
                        hasGameObjects = true;
                        break;
                    default:
                        if (obj != null && obj.type == ClassIDType.RenderSettings)
                        {
                            hasRenderSettings = true;
                        }
                        break;
                }
            }

            if ((!hasGameObjects || !hasRenderSettings) && sf.m_Objects != null)
            {
                for (var i = 0; i < sf.m_Objects.Count; i++)
                {
                    var info = sf.m_Objects[i];
                    if (info == null)
                    {
                        continue;
                    }

                    if (info.classID == (int)ClassIDType.GameObject || info.classID == (int)ClassIDType.SceneAsset)
                    {
                        hasGameObjects = true;
                    }
                    else if (info.classID == (int)ClassIDType.RenderSettings)
                    {
                        hasRenderSettings = true;
                    }

                    if (hasGameObjects && hasRenderSettings)
                    {
                        break;
                    }
                }
            }

            if (!IsSharedAssetsCompanionName(sf.fileName))
            {
                if (hasGameObjects && IsFallbackSceneSourceName(sf.fileName, origPath))
                {
                    heuristicScenes.Add(new IndexedSceneRecord
                    {
                        Name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(origPath),
                        ScenePath = sf.fileName ?? string.Empty,
                        BuildIndex = TryParseLevelBuildIndex(sf.fileName),
                        EntrySerializedFileName = sf.fileName ?? string.Empty,
                        EntryOriginalPath = origPath,
                        ContainerKey = string.Empty,
                        SourceKind = "Heuristic"
                    });
                }
                else if (hasRenderSettings)
                {
                    heuristicScenes.Add(new IndexedSceneRecord
                    {
                        Name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(origPath),
                        ScenePath = sf.fileName ?? string.Empty,
                        BuildIndex = TryParseLevelBuildIndex(sf.fileName),
                        EntrySerializedFileName = sf.fileName ?? string.Empty,
                        EntryOriginalPath = origPath,
                        ContainerKey = string.Empty,
                        SourceKind = "SceneBundle"
                    });
                }
            }
        }

        private static void RecordSerializedFileExternals(
            SerializedFile sf,
            string originalPath,
            List<SourceFileExternalRecord> externals)
        {
            if (sf?.m_Externals == null || sf.m_Externals.Count == 0)
            {
                return;
            }

            for (var i = 0; i < sf.m_Externals.Count; i++)
            {
                var ext = sf.m_Externals[i];
                if (ext == null)
                {
                    continue;
                }

                var extPath = ext.pathName ?? string.Empty;
                var extFile = !string.IsNullOrWhiteSpace(ext.fileName)
                    ? ext.fileName
                    : SQLiteProjectIndexCache.NormalizeExternalLeafName(extPath);
                if (string.IsNullOrWhiteSpace(extFile) && string.IsNullOrWhiteSpace(extPath))
                {
                    continue;
                }

                externals.Add(new SourceFileExternalRecord
                {
                    SourceSerializedFileName = sf.fileName ?? string.Empty,
                    SourceOriginalPath = originalPath,
                    ExternalIndex = i,
                    ExternalFileName = extFile,
                    ExternalPathName = extPath,
                    ExternalGuid = ext.guid == Guid.Empty ? string.Empty : ext.guid.ToString("N")
                });
            }
        }

        public static string ExtractSceneStageStem(string? sceneName, string? fallbackGoName = null)
        {
            static string CleanCandidateStem(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return string.Empty;
                }

                var clean = raw.Trim();
                var parenIdx = clean.IndexOf(" (", StringComparison.Ordinal);
                if (parenIdx > 0)
                {
                    clean = clean.Substring(0, parenIdx).Trim();
                }

                if (clean.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    clean = Path.GetFileNameWithoutExtension(clean);
                }
                else if (clean.IndexOf('/') >= 0 || clean.IndexOf('\\') >= 0)
                {
                    clean = Path.GetFileNameWithoutExtension(clean);
                }

                foreach (var suffix in new[] { "_Model", "_Light", "_Effect", "_Scene", "_Stage" })
                {
                    if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && clean.Length >= suffix.Length + 6)
                    {
                        clean = clean.Substring(0, clean.Length - suffix.Length);
                        break;
                    }
                }

                return clean.Length >= 6 && clean.IndexOf('_') > 0 && !clean.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase)
                    ? clean
                    : string.Empty;
            }

            var fromScene = CleanCandidateStem(sceneName);
            return fromScene.Length > 0 ? fromScene : CleanCandidateStem(fallbackGoName);
        }

        private static void ExtractAssetBundleUnityScenes(
            AssetBundle bundle,
            string serializedFileName,
            string originalPath,
            List<IndexedSceneRecord> bundleScenes)
        {
            if (bundle == null)
            {
                return;
            }

            var cabByNormalizedPath = new Dictionary<string, (string OriginalScenePath, string CabName)>(StringComparer.OrdinalIgnoreCase);
            var cabByLeafName = new Dictionary<string, (string OriginalScenePath, string CabName)>(StringComparer.OrdinalIgnoreCase);
            if (bundle.m_SceneHashes != null && bundle.m_SceneHashes.Length > 0)
            {
                for (var i = 0; i < bundle.m_SceneHashes.Length; i++)
                {
                    var (hashKey, cabName) = (bundle.m_SceneHashes[i].Key, bundle.m_SceneHashes[i].Value);
                    if (string.IsNullOrWhiteSpace(hashKey) || string.IsNullOrWhiteSpace(cabName))
                    {
                        continue;
                    }

                    var normHashKey = NormalizeScenePath(hashKey);
                    cabByNormalizedPath.TryAdd(normHashKey, (normHashKey, cabName));
                    var leaf = BuildSettings.ExtractSceneDisplayName(normHashKey);
                    if (!string.IsNullOrWhiteSpace(leaf))
                    {
                        cabByLeafName.TryAdd(leaf, (normHashKey, cabName));
                    }
                }
            }

            var addedScenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (bundle.m_Container != null && bundle.m_Container.Length > 0)
            {
                for (var i = 0; i < bundle.m_Container.Length; i++)
                {
                    var key = bundle.m_Container[i].Key;
                    if (string.IsNullOrWhiteSpace(key) || !key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var normalizedPath = NormalizeScenePath(key);
                    var displayName = BuildSettings.ExtractSceneDisplayName(normalizedPath);
                    var targetSfName = serializedFileName ?? string.Empty;
                    if (cabByNormalizedPath.TryGetValue(normalizedPath, out var exactMatch))
                    {
                        targetSfName = exactMatch.CabName;
                        if (exactMatch.OriginalScenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                        {
                            normalizedPath = exactMatch.OriginalScenePath;
                            displayName = BuildSettings.ExtractSceneDisplayName(normalizedPath);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(displayName) && cabByLeafName.TryGetValue(displayName, out var leafMatch))
                    {
                        targetSfName = leafMatch.CabName;
                        if (leafMatch.OriginalScenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                        {
                            normalizedPath = leafMatch.OriginalScenePath;
                            displayName = BuildSettings.ExtractSceneDisplayName(normalizedPath);
                        }
                    }

                    addedScenePaths.Add(normalizedPath);
                    bundleScenes.Add(new IndexedSceneRecord
                    {
                        Name = string.IsNullOrWhiteSpace(displayName) ? targetSfName : displayName,
                        ScenePath = normalizedPath,
                        BuildIndex = -1,
                        EntrySerializedFileName = targetSfName,
                        EntryOriginalPath = originalPath ?? string.Empty,
                        ContainerKey = key,
                        SourceKind = "AssetBundle"
                    });
                }
            }

            if (bundle.m_SceneHashes != null && bundle.m_SceneHashes.Length > 0)
            {
                for (var i = 0; i < bundle.m_SceneHashes.Length; i++)
                {
                    var (hashKey, cabName) = (bundle.m_SceneHashes[i].Key, bundle.m_SceneHashes[i].Value);
                    if (string.IsNullOrWhiteSpace(hashKey)
                        || string.IsNullOrWhiteSpace(cabName)
                        || !hashKey.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var normalizedPath = NormalizeScenePath(hashKey);
                    if (!addedScenePaths.Add(normalizedPath))
                    {
                        continue;
                    }

                    var displayName = BuildSettings.ExtractSceneDisplayName(normalizedPath);
                    bundleScenes.Add(new IndexedSceneRecord
                    {
                        Name = string.IsNullOrWhiteSpace(displayName) ? cabName : displayName,
                        ScenePath = normalizedPath,
                        BuildIndex = -1,
                        EntrySerializedFileName = cabName,
                        EntryOriginalPath = originalPath ?? string.Empty,
                        ContainerKey = hashKey,
                        SourceKind = "AssetBundle"
                    });
                }
            }
        }

        private static bool ShouldSkipLargeUncachedLzmaBundle(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length <= MaxUncachedLzmaHeaderProbeBytes)
                {
                    return false;
                }

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var br = new EndianBinaryReader(fs, EndianType.BigEndian);
                var sig = br.ReadStringToNull(32);
                if (sig == "UnityWeb")
                {
                    return true;
                }

                if (sig != "UnityFS")
                {
                    return false;
                }

                _ = br.ReadUInt32(); // version
                _ = br.ReadStringToNull(64);
                _ = br.ReadStringToNull(64);
                _ = br.ReadInt64(); // size
                _ = br.ReadUInt32(); // compressedBlocksInfoSize
                _ = br.ReadUInt32(); // uncompressedBlocksInfoSize
                var flags = (ArchiveFlags)br.ReadUInt32();
                var compType = (CompressionType)(flags & ArchiveFlags.CompressionTypeMask);
                return compType == CompressionType.Lzma;
            }
            catch
            {
                return false;
            }
        }

        private static readonly HashSet<string> GenericSceneGameObjectNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SceneRoot", "Root", "Scene", "SceneOnly", "Main Camera", "MainCamera", "Camera", "Cameras",
            "Directional Light", "DirectionalLight", "Light", "Lights", "Lighting", "Point Light", "Spot Light",
            "Area Light", "Reflection Probe", "ReflectionProbe", "Light Probe Group", "LightProbeGroup",
            "EventSystem", "Canvas", "UI", "HUD", "Effect", "Effects", "VFX", "FX", "PostProcess",
            "PostProcessVolume", "Global Volume", "Volume", "Skydome", "SkyDome", "Sky", "Skybox",
            "Terrain", "Ground", "Floor", "Water", "River", "Sea", "Ocean", "Wind", "Fog", "Cloud", "Clouds",
            "Navigation", "NavMesh", "NAVIMESH", "Variant", "Model", "Models", "StageModel", "Stage",
            "Sound", "Sounds", "Audio", "BGM", "SE", "Shadow", "Shadows", "AirWall", "Airwall", "Wall",
            "Collision", "Collider", "Colliders", "Trigger", "Triggers", "Cube", "Plane", "Sphere", "Quad",
            "Capsule", "Cylinder", "Empty", "GameObject", "Object", "Objects", "Props", "Environment",
            "Static", "Dynamic", "DungeonObject", "Spline", "SPLINE", "Rock", "Tree", "Grass", "Plant",
            "Ruin", "Cliff", "Temple", "Flag", "Goods", "Far", "Near", "Walk", "Dead", "DeadLeaf", "CLT"
        };

        internal static bool IsSharedAssetsCompanionName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return fileName.EndsWith(".sharedAssets", StringComparison.OrdinalIgnoreCase)
                || (fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".assets", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasRenderSettingsObject(SerializedFile sf)
        {
            if (sf?.m_Objects == null)
            {
                return false;
            }

            for (var i = 0; i < sf.m_Objects.Count; i++)
            {
                var info = sf.m_Objects[i];
                if (info != null && info.classID == (int)ClassIDType.RenderSettings)
                {
                    return true;
                }
            }

            return false;
        }

        private static (int GameObjectCount, int RendererCount, bool HasRenderSettings) CountSceneObjectsInSerializedFile(SerializedFile sf)
        {
            var goCount = 0;
            var rendererCount = 0;
            var hasRenderSettings = false;

            if (sf.m_Objects != null && sf.m_Objects.Count > 0)
            {
                for (var i = 0; i < sf.m_Objects.Count; i++)
                {
                    var info = sf.m_Objects[i];
                    if (info == null)
                    {
                        continue;
                    }

                    var cid = info.classID;
                    if (cid == (int)ClassIDType.GameObject)
                    {
                        goCount++;
                    }
                    else if (cid == (int)ClassIDType.MeshRenderer || cid == (int)ClassIDType.SkinnedMeshRenderer)
                    {
                        rendererCount++;
                    }
                    else if (cid == (int)ClassIDType.RenderSettings)
                    {
                        hasRenderSettings = true;
                    }
                }

                return (goCount, rendererCount, hasRenderSettings);
            }

            AssetStudio.Object[] objs;
            lock (sf)
            {
                objs = sf.Objects != null ? sf.Objects.ToArray() : Array.Empty<AssetStudio.Object>();
            }

            for (var i = 0; i < objs.Length; i++)
            {
                var obj = objs[i];
                if (obj == null)
                {
                    continue;
                }

                if (obj.type == ClassIDType.GameObject)
                {
                    goCount++;
                }
                else if (obj.type == ClassIDType.MeshRenderer || obj.type == ClassIDType.SkinnedMeshRenderer)
                {
                    rendererCount++;
                }
                else if (obj.type == ClassIDType.RenderSettings)
                {
                    hasRenderSettings = true;
                }
            }

            return (goCount, rendererCount, hasRenderSettings);
        }

        private static int ScoreSceneGameObjectName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return 0;
            }

            var trimmed = name.Trim();
            if (trimmed.Length < 4 || trimmed.Length > 64)
            {
                return 0;
            }

            if (trimmed.StartsWith("Unnamed_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("LOD", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Spot Light", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Point Light", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Area Light", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Reflection Probe", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Cube", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Plane", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Cell (", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("STGCollision", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("AirWall", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("AmbientZone", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("set_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("PrefabSet_", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (GenericSceneGameObjectNames.Contains(trimmed))
            {
                return 0;
            }

            // Highest preference: explicit stage/scene root naming conventions (e.g. *_Model, *_Light, *_Effect, *_Scene, *_Stage).
            if (trimmed.EndsWith("_Model", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("_Scene", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("_Stage", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (trimmed.EndsWith("_Light", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("_Effect", StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            // Medium preference: multi-token identifiers without instance numbering like " (1)".
            if (!trimmed.Contains('(') && trimmed.Contains('_') && trimmed.Length >= 8)
            {
                return 40;
            }

            if (!trimmed.Contains('('))
            {
                return 20;
            }

            return 5;
        }

        private static string BuildStreamedSceneDisplayName(string sfName, string? origPath, string? bestGoName)
        {
            var shortId = !string.IsNullOrWhiteSpace(sfName)
                ? (sfName.Length > 12 ? sfName.Substring(0, 12) : sfName)
                : (!string.IsNullOrWhiteSpace(origPath) ? Path.GetFileNameWithoutExtension(origPath) : "Scene");

            if (!string.IsNullOrWhiteSpace(bestGoName))
            {
                var clean = bestGoName.Trim();
                foreach (var suffix in new[] { "_Model", "_Light", "_Effect" })
                {
                    if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && clean.Length > suffix.Length + 2)
                    {
                        clean = clean.Substring(0, clean.Length - suffix.Length);
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(clean) && !string.Equals(clean, shortId, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{clean} ({shortId})";
                }
            }

            return !string.IsNullOrWhiteSpace(sfName) ? sfName : shortId;
        }

        private static bool IsFallbackSceneSourceName(string? serializedFileName, string? originalPath)
        {
            if (!string.IsNullOrWhiteSpace(serializedFileName))
            {
                if (string.Equals(serializedFileName, "mainData", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (serializedFileName.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                    && serializedFileName.Length > 5
                    && char.IsDigit(serializedFileName[5]))
                {
                    return true;
                }

                if (serializedFileName.Contains("scene", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                var fileName = Path.GetFileName(originalPath);
                if (!string.IsNullOrWhiteSpace(fileName)
                    && (string.Equals(fileName, "mainData", StringComparison.OrdinalIgnoreCase)
                        || (fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase) && fileName.Length > 5 && char.IsDigit(fileName[5]))
                        || fileName.Contains("scene", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static int TryParseLevelBuildIndex(string? serializedFileName)
        {
            if (string.IsNullOrWhiteSpace(serializedFileName))
            {
                return -1;
            }

            if (serializedFileName.StartsWith("level", StringComparison.OrdinalIgnoreCase) && serializedFileName.Length > 5)
            {
                var suffix = serializedFileName.Substring(5);
                var dot = suffix.IndexOf('.');
                if (dot >= 0)
                {
                    suffix = suffix.Substring(0, dot);
                }

                if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
                {
                    return idx;
                }
            }

            return -1;
        }

        private static string NormalizeScenePath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return string.Empty;
            }

            return rawPath.Trim().Replace('\\', '/');
        }

        private static string SafeNormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        // --- Preloading Orchestration ---

        public static void PreprocessPreloadedAssetForPreview(AssetItem? item, CancellationToken token)
        {
            if (token.IsCancellationRequested || item?.Handle?.RealObject is not Mesh mesh)
            {
                return;
            }

            mesh.EnsureProcessed();
        }

        public static bool IsLazyHandlePinnedForPreview(
            AssetHandle? handle,
            object preloaderLock,
            HashSet<string> preloadedUniqueIds,
            string? currentlySelectedUniqueId)
        {
            if (handle == null || !handle.HasUniqueId || preloaderLock == null || preloadedUniqueIds == null)
            {
                return false;
            }

            lock (preloaderLock)
            {
                if (preloadedUniqueIds.Count == 0 && string.IsNullOrEmpty(currentlySelectedUniqueId))
                {
                    return false;
                }

                var uniqueId = handle.UniqueID;
                return !string.IsNullOrEmpty(uniqueId)
                    && (preloadedUniqueIds.Contains(uniqueId)
                        || string.Equals(currentlySelectedUniqueId, uniqueId, StringComparison.Ordinal));
            }
        }

        public static (List<AssetItem> SortedItemsToLoad, List<string> UniqueIdsToUnload) ComputePreloadWindowPlan(
            IEnumerable<AssetItem> visibleAssetItems,
            AssetItem currentItem,
            HashSet<string> preloadedUniqueIds,
            int maxWindowSize = AssetPreloadWindowSize)
        {
            if (visibleAssetItems == null || currentItem == null || maxWindowSize <= 0)
            {
                return (new List<AssetItem>(), new List<string>());
            }

            var preloadItems = visibleAssetItems
                .Where(item => item != null && item.Type == currentItem.Type)
                .ToList();

            var currentIdx = preloadItems.IndexOf(currentItem);
            if (currentIdx < 0)
            {
                return (new List<AssetItem>(), new List<string>());
            }

            int total = preloadItems.Count;
            int windowSize = Math.Min(maxWindowSize, total);
            int start = currentIdx - windowSize / 2;
            if (start < 0) start = 0;
            if (start + windowSize > total) start = total - windowSize;
            int end = start + windowSize - 1;

            var itemsToKeep = new HashSet<string>();
            var itemsToLoad = new List<AssetItem>();

            for (int i = start; i <= end; i++)
            {
                var item = preloadItems[i];
                if (item.Handle != null && !string.IsNullOrEmpty(item.Handle.UniqueID))
                {
                    itemsToKeep.Add(item.Handle.UniqueID);
                    if (item.Handle.RealObject == null)
                    {
                        itemsToLoad.Add(item);
                    }
                }
            }

            var toUnload = new List<string>();
            if (preloadedUniqueIds != null)
            {
                foreach (var uid in preloadedUniqueIds)
                {
                    if (!itemsToKeep.Contains(uid))
                    {
                        toUnload.Add(uid);
                    }
                }
            }

            var sortedItemsToLoad = itemsToLoad
                .OrderBy(item =>
                {
                    var idx = preloadItems.IndexOf(item);
                    return idx >= 0 ? Math.Abs(idx - currentIdx) : int.MaxValue;
                })
                .ToList();

            return (sortedItemsToLoad, toUnload);
        }

        public static void UpdatePreloadWindow(
            AssetsManager assetsManager,
            bool isProgressiveIndexingActive,
            IEnumerable<AssetItem> visibleAssetItems,
            AssetItem? currentItem,
            object preloaderLock,
            HashSet<string> preloadedUniqueIds,
            ref CancellationTokenSource? preloaderCts,
            Action<AssetItem> ensureLazyAssetReadyForPreview,
            int maxWindowSize = AssetPreloadWindowSize,
            int delayMilliseconds = 15,
            Func<bool>? shouldPausePreload = null)
        {
            if (assetsManager == null || !assetsManager.LazyLoading || isProgressiveIndexingActive || currentItem == null)
            {
                return;
            }

            lock (preloaderLock)
            {
                preloaderCts?.Cancel();
                preloaderCts?.Dispose();
                if (shouldPausePreload?.Invoke() == true)
                {
                    preloaderCts = null;
                    return;
                }

                preloaderCts = new CancellationTokenSource();
                var token = preloaderCts.Token;

                var (sortedItemsToLoad, toUnload) = ComputePreloadWindowPlan(
                    visibleAssetItems,
                    currentItem,
                    preloadedUniqueIds,
                    maxWindowSize);

                if (sortedItemsToLoad.Count == 0 && toUnload.Count == 0)
                {
                    return;
                }

                foreach (var uid in toUnload)
                {
                    preloadedUniqueIds.Remove(uid);
                    var handle = assetsManager.ProjectIndex.GetHandle(uid);
                    if (handle != null)
                    {
                        LazyAssetResolutionService.UnloadAsset(handle);
                    }
                }

                _ = Task.Run(async () =>
                {
                    foreach (var item in sortedItemsToLoad)
                    {
                        if (token.IsCancellationRequested) break;

                        while (!token.IsCancellationRequested && shouldPausePreload?.Invoke() == true)
                        {
                            await Task.Delay(Math.Max(50, delayMilliseconds), token);
                        }

                        if (token.IsCancellationRequested) break;

                        try
                        {
                            if (token.IsCancellationRequested) break;

                            if (item.Handle?.UniqueID == null) continue;

                            bool alreadyLoaded;
                            lock (preloaderLock)
                            {
                                alreadyLoaded = preloadedUniqueIds.Contains(item.Handle.UniqueID);
                            }

                            if (alreadyLoaded)
                            {
                                continue;
                            }

                            if (item.Handle.RealObject != null)
                            {
                                PreprocessPreloadedAssetForPreview(item, token);
                                lock (preloaderLock)
                                {
                                    preloadedUniqueIds.Add(item.Handle.UniqueID);
                                }
                                continue;
                            }

                            ensureLazyAssetReadyForPreview(item);
                            assetsManager.ResolveHandle(item.Handle);

                            if (item.Handle?.UniqueID != null && item.Handle.RealObject != null)
                            {
                                PreprocessPreloadedAssetForPreview(item, token);
                                lock (preloaderLock)
                                {
                                    preloadedUniqueIds.Add(item.Handle.UniqueID);
                                }
                            }
                        }
                        catch
                        {
                            // Ignore individual load errors
                        }

                        if (delayMilliseconds > 0)
                        {
                            await Task.Delay(delayMilliseconds, token);
                        }
                    }
                }, token);
            }
        }
    }
}
