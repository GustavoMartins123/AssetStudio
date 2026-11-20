using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static bool IsSameOrDescendantGameObject(SerializedFile sourceFile, GameObject child, GameObject ancestor) =>
            SemanticGraphService.IsSameOrDescendantGameObject(sourceFile, child, ancestor);

        private static float[]? BuildLocalToWorldMatrix(SerializedFile sourceFile, GameObject? gameObject) =>
            SemanticGraphService.BuildLocalToWorldMatrix(sourceFile, gameObject);

        private static float[]? BuildLocalToGroupRootMatrix(
            SerializedFile sourceFile,
            GameObject? gameObject,
            string rootGameObjectId) =>
            SemanticGraphService.BuildLocalToGroupRootMatrix(sourceFile, gameObject, rootGameObjectId);
    }
}
