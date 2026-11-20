using System.Collections.Generic;
using AssetStudio;
using AssetStudio.Avalonia.Services;

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
            out string? productName) =>
            ProjectLoadingService.BuildEagerAssetItemsBackground(
                assetsFileList,
                displayAllChecked,
                treeNodeDictionary,
                objectAssetItemDic,
                pathIDAssetItemDic,
                containers,
                exportableAssets,
                sceneTreeNodes,
                out productName);

        private static void LinkAssetItemsToSceneNodesBackground(
            List<SerializedFile> assetsFileList,
            Dictionary<GameObject, GameObjectNode> treeNodeDictionary,
            Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic) =>
            ProjectLoadingService.LinkAssetItemsToSceneNodesBackground(
                assetsFileList,
                treeNodeDictionary,
                objectAssetItemDic);

        private static void LinkFbxSubAssetsToSceneNodesBackground(
            List<AssetItem> localExportableAssets,
            List<GameObjectNode> localSceneTreeNodes) =>
            ProjectLoadingService.LinkFbxSubAssetsToSceneNodesBackground(
                localExportableAssets,
                localSceneTreeNodes);

        internal static string? ResolveFbxContainerFast(string? container) =>
            ProjectLoadingService.ResolveFbxContainerFast(container);
    }

    internal static class FbxSceneNodeIndex
    {
        internal const int ParallelContainerThreshold = ProjectLoadingService.ParallelFbxContainerThreshold;

        internal static Dictionary<string, GameObjectNode> BuildNameIndex(IReadOnlyList<GameObjectNode> roots) =>
            ProjectLoadingService.BuildFbxSceneNameIndex(roots);

        internal static Dictionary<string, GameObjectNode> BuildSubtreeIndex(GameObjectNode root) =>
            ProjectLoadingService.BuildFbxSceneSubtreeIndex(root);

        internal static Dictionary<string, GameObjectNode> ResolveFbxNodes(
            IReadOnlyList<GameObjectNode?> itemTreeNodes,
            IReadOnlyList<string?> fbxContainers,
            IReadOnlyList<GameObjectNode> roots) =>
            ProjectLoadingService.ResolveFbxNodes(itemTreeNodes, fbxContainers, roots);
    }
}
