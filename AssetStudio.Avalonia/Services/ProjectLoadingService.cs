using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
            if (assetBundle?.m_Container == null || assetBundle.m_PreloadTable == null)
            {
                return;
            }

            var preloadTable = assetBundle.m_PreloadTable;
            foreach (var m_Container in assetBundle.m_Container)
            {
                var assetInfo = m_Container.Value;
                if (assetInfo == null)
                {
                    continue;
                }

                var preloadIndex = assetInfo.preloadIndex;
                var preloadSize = assetInfo.preloadSize;
                if (preloadIndex < 0 || preloadSize <= 0 || preloadIndex >= preloadTable.Length)
                {
                    continue;
                }

                long preloadEnd = Math.Min((long)preloadIndex + preloadSize, preloadTable.Length);
                for (int k = preloadIndex; k < preloadEnd; k++)
                {
                    containers.Add((preloadTable[k], m_Container.Key));
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
            string normalized;
            try
            {
                normalized = Path.GetFullPath(folderPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                normalized = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (OperatingSystem.IsWindows())
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
                var handles = handlesOverride ?? assetsManager.ProjectIndex.GetHandles();
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
                    handles,
                    preserveSemanticRelations,
                    ReportSaveProgress,
                    markIndexingCompleted: true);

                if (saved)
                {
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
            Action<IndexingProgressUpdate, int>? showProgressPanel = null)
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
                // PERF-11: Guard semantic commit against mid-index scan/signature drift.
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

            if (processedFiles == 0 || processedFiles == safeTotal || isTerminal || elapsedMs >= 2000)
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
            out List<SourceFileMetadata>? unchangedSourceFiles)
        {
            deltaPlan = null;
            unchangedSourceFiles = null;

            if (sqliteCache != null
                && scanResult != null
                && scanResult.Files.Count > 0
                && !string.IsNullOrWhiteSpace(folderPath)
                && targetIndex != null)
            {
                var plan = sqliteCache.PlanIncrementalDelta(folderPath, scanResult);
                if (plan.CanApplyIncremental
                    && sqliteCache.TryLoadUnchangedAssetsForIncrementalUpdate(folderPath, plan, targetIndex, out var loadedSources))
                {
                    deltaPlan = plan;
                    unchangedSourceFiles = loadedSources;
                    return plan.FilesToReindex.ToList();
                }
            }

            return ResolveColdIndexFiles(folderPath, scanResult);
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
