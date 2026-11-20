using System.Collections.Generic;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        internal const int MaxClipBoneHashProbes = SemanticGraphService.MaxClipBoneHashProbes;
        internal const int MaxRigBoneHashProbes = SemanticGraphService.MaxRigBoneHashProbes;

        internal static uint[] SelectDeterministicBoneHashProbes(IReadOnlyCollection<uint> boneHashes, int maxProbes = MaxClipBoneHashProbes) =>
            SemanticGraphService.SelectDeterministicBoneHashProbes(boneHashes, maxProbes);

        internal static uint ComputeUnityBonePathHash(string? path) =>
            SemanticGraphService.ComputeUnityBonePathHash(path);

        private static void BuildAnimationPreviewIndexesBackground(
            List<SerializedFile> assetsFileList,
            out Dictionary<AnimationClip, Avatar?> animationClipAvatarCacheOut,
            out Dictionary<Avatar, Mesh?> avatarMeshCacheOut,
            out Dictionary<Mesh, Avatar?> meshAvatarCacheOut,
            out Dictionary<AnimationClip, HashSet<uint>> animationClipTransformBindingsCacheOut) =>
            SemanticGraphService.BuildAnimationPreviewIndexesBackground(
                assetsFileList,
                out animationClipAvatarCacheOut,
                out avatarMeshCacheOut,
                out meshAvatarCacheOut,
                out animationClipTransformBindingsCacheOut);

        internal static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip) =>
            SemanticGraphService.GetTransformBindingPathsBackground(clip);

        private static bool IsStrongAnimationAvatarMatch(int bindingCount, int overlap) =>
            SemanticGraphService.IsStrongAnimationAvatarMatch(bindingCount, overlap);

        private static bool IsStrongMeshAvatarMatch(int boneCount, int overlap) =>
            SemanticGraphService.IsStrongMeshAvatarMatch(boneCount, overlap);
    }
}
