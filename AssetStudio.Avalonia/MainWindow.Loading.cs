using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        internal ProjectSessionCoordinatorService ProjectSessionCoordinator => _projectSessionCoordinator;

        internal PreviewResolutionCoordinatorService PreviewResolutionCoordinator => _previewResolutionCoordinator;

        private ProjectSessionActivation BeginProjectSessionTransition(string? projectKey = null)
        {
            var activation = _projectSessionCoordinator.BeginSessionTransition(projectKey);
            lazyIndexingSessionId = activation.Generation;
            return activation;
        }

        private void TeardownActiveProjectSession()
        {
            _projectSessionCoordinator.TeardownCurrentSession();
            lazyIndexingSessionId = _projectSessionCoordinator.ActiveGeneration;
        }

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

        private static ParallelOptions CreateStructureBuildParallelOptions() =>
            ProjectLoadingService.CreateStructureBuildParallelOptions();

        private static bool IsLazyExportableType(ClassIDType type) =>
            ProjectLoadingService.IsLazyExportableType(type);

        private static void BuildLazyAssetItemsBackground(
            IReadOnlyList<AssetHandle> handles,
            bool displayAllChecked,
            Dictionary<string, AssetItem> pathIDAssetItemDic,
            Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic,
            List<AssetItem> exportableAssets,
            List<AssetItem> newExportableAssets,
            bool populatePathIdDictionary = true) =>
            ProjectLoadingService.BuildLazyAssetItemsBackground(
                handles,
                displayAllChecked,
                pathIDAssetItemDic,
                objectAssetItemDic,
                exportableAssets,
                newExportableAssets,
                populatePathIdDictionary);

        private static void BuildExportableAssetIndexesBackground(
            IEnumerable<AssetItem> exportableAssets,
            out Dictionary<AssetHandleKey, AssetItem> lazyItemsByHandleId,
            out HashSet<AssetHandleKey> exportableHandleIds,
            out HashSet<ClassIDType> exportableTypes) =>
            ProjectLoadingService.BuildExportableAssetIndexesBackground(
                exportableAssets,
                out lazyItemsByHandleId,
                out exportableHandleIds,
                out exportableTypes);

        private static void AddSerializedTypesBackground(
            SerializedFile assetsFile,
            IEnumerable<SerializedType>? types,
            string sourceKind,
            Dictionary<(string UnityVersion, int ClassID), int> objectCounts,
            HashSet<string> seen,
            List<AssetClassItem> localAssetClassItems) =>
            ProjectLoadingService.AddSerializedTypesBackground(
                assetsFile,
                types,
                sourceKind,
                objectCounts,
                seen,
                localAssetClassItems);
    }
}
