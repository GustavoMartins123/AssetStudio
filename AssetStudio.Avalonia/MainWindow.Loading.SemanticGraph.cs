using System.Collections.Generic;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
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
            out SemanticAssetRelations semanticRelationsOut) =>
            SemanticGraphService.BuildAssetReferenceIndexesBackground(
                assetsFileList,
                localExportableAssets,
                out objectToAssetItemCacheOut,
                out meshToMaterialsCacheOut,
                out meshAssociatedRenderersCacheOut,
                out meshSourceTypesCacheOut,
                out materialMainTextureCacheOut,
                out materialPreviewMaterialCacheOut,
                out materialTextureSlotsCacheOut,
                out semanticRelationsOut);

        internal static int GetSemanticGraphMaxDegreeOfParallelism(int? processorCountOverride = null, string? environmentValueOverride = null) =>
            SemanticGraphService.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride, environmentValueOverride);

        internal static ParallelOptions CreateSemanticGraphParallelOptions(int maxDegreeOfParallelism = 0) =>
            SemanticGraphService.CreateSemanticGraphParallelOptions(maxDegreeOfParallelism);

        internal static void BuildSemanticRelationsForFilesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            SemanticAssetRelations targetRelations) =>
            SemanticGraphService.BuildSemanticRelationsForFilesBackground(assetsFileList, targetRelations);

        internal static void BuildSemanticRelationsForFilesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
            SemanticAssetRelations targetRelations,
            int maxDegreeOfParallelism) =>
            SemanticGraphService.BuildSemanticRelationsForFilesBackground(assetsFileList, targetRelations, maxDegreeOfParallelism);
    }
}
