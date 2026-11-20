using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    }
}
