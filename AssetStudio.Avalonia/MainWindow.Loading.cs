using System;
using System.Collections.Generic;
using System.Globalization;
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
            public Dictionary<AssetHandleKey, AssetItem> LazyAssetItemsByHandleId;
            public HashSet<AssetHandleKey> ExportableAssetHandleIds;
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

        private static void BuildLazyAssetItemsBackground(
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

        private static void BuildExportableAssetIndexesBackground(
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

        private static void AddSerializedTypesBackground(
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
    }
}
