using System.Collections.Generic;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static void IndexMaterialTexturesBackground(
            Material material,
            Dictionary<Material, Material?> localMaterialPreviewMaterialCache,
            Dictionary<Material, Dictionary<string, Texture2D?>> localMaterialTextureSlotsCache,
            Dictionary<Material, Texture2D?> localMaterialMainTextureCache) =>
            SemanticGraphService.IndexMaterialTexturesBackground(
                material,
                localMaterialPreviewMaterialCache,
                localMaterialTextureSlotsCache,
                localMaterialMainTextureCache);

        private static Material? ResolveMaterialForPreviewBackground(
            Material material,
            Dictionary<Material, Material?> localMaterialPreviewMaterialCache) =>
            SemanticGraphService.ResolveMaterialForPreviewBackground(material, localMaterialPreviewMaterialCache);

        private static Material? ResolveMaterialForPreviewUncachedBackground(Material material) =>
            SemanticGraphService.ResolveMaterialForPreviewUncachedBackground(material);

        private static Texture2D? ResolveTexturePPtrBackground(Material material, PPtr<Texture> textureRef) =>
            SemanticGraphService.ResolveTexturePPtrBackground(material, textureRef);

        private static Texture2D? SelectMainTextureForMaterialBackground(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots) =>
            SemanticGraphService.SelectMainTextureForMaterialBackground(displayMaterial, textureSlots);

        private static int ScoreMaterialsStatic(List<Material?> mats) =>
            SemanticGraphService.ScoreMaterialsStatic(mats);

        private static List<Material?> MergeMeshMaterialLists(List<Material?> existing, List<Material?> incoming) =>
            SemanticGraphService.MergeMeshMaterialLists(existing, incoming);
    }
}
