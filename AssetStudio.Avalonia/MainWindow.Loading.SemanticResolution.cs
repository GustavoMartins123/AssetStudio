using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static AssetHandle? GetSemanticHandleFromPPtr(SerializedFile sourceFile, PPtr<AssetStudio.Object> pptr) =>
            SemanticGraphService.GetSemanticHandleFromPPtr(sourceFile, pptr);

        private static Transform? ResolveTransformForGameObjectBackground(SerializedFile sourceFile, GameObject gameObject) =>
            SemanticGraphService.ResolveTransformForGameObjectBackground(sourceFile, gameObject);

        private static Transform? ResolveTransformBackground(SerializedFile sourceFile, PPtr<Transform> pptr) =>
            SemanticGraphService.ResolveTransformBackground(sourceFile, pptr);

        private static Avatar? ResolveAvatarBackground(SerializedFile sourceFile, PPtr<Avatar> pptr) =>
            SemanticGraphService.ResolveAvatarBackground(sourceFile, pptr);

        private static RuntimeAnimatorController? ResolveRuntimeAnimatorControllerBackground(
            SerializedFile sourceFile,
            PPtr<RuntimeAnimatorController> pptr) =>
            SemanticGraphService.ResolveRuntimeAnimatorControllerBackground(sourceFile, pptr);

        private static string GetSemanticGameObjectId(
            SerializedFile sourceFile,
            PPtr<GameObject> gameObjectPtr,
            GameObject? gameObject) =>
            SemanticGraphService.GetSemanticGameObjectId(sourceFile, gameObjectPtr, gameObject);

        private static GameObject? ResolveGameObjectBackground(SerializedFile sourceFile, PPtr<GameObject> pptr) =>
            SemanticGraphService.ResolveGameObjectBackground(sourceFile, pptr);

        private static Mesh? ResolveMeshBackground(SerializedFile sourceFile, PPtr<Mesh> pptr) =>
            SemanticGraphService.ResolveMeshBackground(sourceFile, pptr);

        private static Material? ResolveRendererMaterialBackground(PPtr<Material> pptr) =>
            SemanticGraphService.ResolveRendererMaterialBackground(pptr);

        private static AssetStudio.Object? ResolveObjectBackground(SerializedFile sourceFile, long pathID) =>
            SemanticGraphService.ResolveObjectBackground(sourceFile, pathID);

        private static string GetSemanticAssetId(AssetStudio.Object? asset) =>
            SemanticGraphService.GetSemanticAssetId(asset);

        private static string GetSemanticAssetIdFromPPtr<T>(SerializedFile sourceFile, PPtr<T> pptr, ClassIDType expectedType)
            where T : AssetStudio.Object =>
            SemanticGraphService.GetSemanticAssetIdFromPPtr(sourceFile, pptr, expectedType);
    }
}
