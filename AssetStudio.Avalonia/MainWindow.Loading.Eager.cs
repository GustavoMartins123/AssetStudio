using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
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

        private static List<(PPtr<AssetStudio.Object>, string)> CollectAssetBundleContainers(AssetBundle assetBundle)
        {
            var containers = new List<(PPtr<AssetStudio.Object>, string)>();
            CollectSafeAssetBundleContainers(assetBundle, containers);
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

        private static void LinkFbxSubAssetsToSceneNodesBackground(
            List<AssetItem> localExportableAssets,
            List<GameObjectNode> localSceneTreeNodes)
        {
            var count = localExportableAssets.Count;
            if (count == 0)
            {
                return;
            }

            // Resolve each item's FBX container exactly once (the legacy code re-split every
            // container path three times). Pure per-item work, so it parallelizes safely.
            var fbxContainers = new string?[count];
            var treeNodes = new GameObjectNode?[count];
            var anyFbx = 0;
            if (count >= FbxSceneNodeIndex.ParallelContainerThreshold)
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

            var fbxNodes = FbxSceneNodeIndex.ResolveFbxNodes(treeNodes, fbxContainers, localSceneTreeNodes);
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

        /// <summary>
        /// Same result as <see cref="GetFbxContainerPath"/>, with an allocation-free reject for
        /// containers that cannot contain an ".fbx" segment (the overwhelming majority).
        /// </summary>
        internal static string? ResolveFbxContainerFast(string? container)
        {
            if (string.IsNullOrEmpty(container) || !container.Contains(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return GetFbxContainerPath(container);
        }
    }

    /// <summary>
    /// Precomputed name lookups over the scene hierarchy used to link FBX sub-assets to scene nodes.
    /// Replaces the legacy per-asset recursive DFS (O(N_assets * N_nodes)) with single-pass indices
    /// that preserve the exact legacy semantics:
    /// <list type="bullet">
    /// <item>only nodes backed by a <see cref="GameObject"/> are matchable;</item>
    /// <item>names compare with <see cref="StringComparer.OrdinalIgnoreCase"/>;</item>
    /// <item>ties resolve to the first node in pre-order (roots in list order, children in order),
    /// which is exactly the node the legacy early-exit DFS returned.</item>
    /// </list>
    /// </summary>
    internal static class FbxSceneNodeIndex
    {
        internal const int ParallelContainerThreshold = 8192;

        /// <summary>Builds a first-pre-order-wins name index over every root.</summary>
        internal static Dictionary<string, GameObjectNode> BuildNameIndex(IReadOnlyList<GameObjectNode> roots)
        {
            var index = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<GameObjectNode>();
            for (int r = 0; r < roots.Count; r++)
            {
                IndexSubtree(roots[r], index, stack);
            }

            return index;
        }

        /// <summary>Builds a first-pre-order-wins name index over a single subtree (root included).</summary>
        internal static Dictionary<string, GameObjectNode> BuildSubtreeIndex(GameObjectNode root)
        {
            var index = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);
            IndexSubtree(root, index, new Stack<GameObjectNode>());
            return index;
        }

        /// <summary>
        /// Iterative (stack-safe) pre-order walk; <c>TryAdd</c> keeps the first visited node per name.
        /// </summary>
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

        /// <summary>
        /// Maps FBX container paths to the scene node every sub-asset of that container links to.
        /// <paramref name="itemTreeNodes"/> and <paramref name="fbxContainers"/> are parallel arrays
        /// (one entry per exportable asset, container already resolved via GetFbxContainerPath).
        /// </summary>
        internal static Dictionary<string, GameObjectNode> ResolveFbxNodes(
            IReadOnlyList<GameObjectNode?> itemTreeNodes,
            IReadOnlyList<string?> fbxContainers,
            IReadOnlyList<GameObjectNode> roots)
        {
            if (itemTreeNodes.Count != fbxContainers.Count)
            {
                throw new ArgumentException("Tree node and container lists must have the same length.", nameof(fbxContainers));
            }

            var fbxNodes = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: assets already attached to a GameObject node anchor their container to the
            // node named like the FBX inside their top-level GameObject subtree (or that root).
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
                    subtreeIndex = BuildSubtreeIndex(root);
                    subtreeIndices.Add(root, subtreeIndex);
                }

                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                fbxNodes.Add(fbxContainer, subtreeIndex.TryGetValue(fbxName, out var namedRoot) ? namedRoot : root);
            }

            // Pass 2: remaining containers fall back to the first scene-wide node named like the FBX.
            Dictionary<string, GameObjectNode>? sceneIndex = null;
            for (int i = 0; i < fbxContainers.Count; i++)
            {
                var fbxContainer = fbxContainers[i];
                if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
                {
                    continue;
                }

                sceneIndex ??= BuildNameIndex(roots);
                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                if (sceneIndex.TryGetValue(fbxName, out var node))
                {
                    fbxNodes[fbxContainer] = node;
                }
            }

            return fbxNodes;
        }
    }
}
