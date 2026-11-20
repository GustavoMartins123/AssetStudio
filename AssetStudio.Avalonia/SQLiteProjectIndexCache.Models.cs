using System.Collections.Generic;

namespace AssetStudio.Avalonia
{
    internal sealed class ModelGroupInfo
    {
        public ModelGroupInfo(
            string groupId,
            string groupKind,
            string groupName,
            string rootGameObjectAssetId,
            string rootGameObjectName,
            string animatorAssetId,
            string avatarAssetId,
            string controllerAssetId,
            string sourceFileName,
            int confidence,
            string confidenceReason)
        {
            GroupId = groupId;
            GroupKind = groupKind;
            GroupName = groupName;
            RootGameObjectAssetId = rootGameObjectAssetId;
            RootGameObjectName = rootGameObjectName;
            AnimatorAssetId = animatorAssetId;
            AvatarAssetId = avatarAssetId;
            ControllerAssetId = controllerAssetId;
            SourceFileName = sourceFileName;
            Confidence = confidence;
            ConfidenceReason = confidenceReason;
        }

        public string GroupId { get; }
        public string GroupKind { get; }
        public string GroupName { get; }
        public string RootGameObjectAssetId { get; }
        public string RootGameObjectName { get; }
        public string AnimatorAssetId { get; }
        public string AvatarAssetId { get; }
        public string ControllerAssetId { get; }
        public string SourceFileName { get; }
        public int Confidence { get; }
        public string ConfidenceReason { get; }
    }

    internal sealed class ModelGroupMeshInfo
    {
        public ModelGroupMeshInfo(
            string groupId,
            string meshAssetId,
            string rendererAssetId,
            string rendererType,
            string gameObjectAssetId,
            string gameObjectName,
            int slotIndex,
            int confidence,
            string confidenceReason,
            float[]? transformMatrix,
            string meshName,
            long meshPathId,
            long meshByteSize,
            int meshType)
        {
            GroupId = groupId;
            MeshAssetId = meshAssetId;
            RendererAssetId = rendererAssetId;
            RendererType = rendererType;
            GameObjectAssetId = gameObjectAssetId;
            GameObjectName = gameObjectName;
            SlotIndex = slotIndex;
            Confidence = confidence;
            ConfidenceReason = confidenceReason;
            TransformMatrix = transformMatrix;
            MeshName = meshName;
            MeshPathId = meshPathId;
            MeshByteSize = meshByteSize;
            MeshType = meshType;
        }

        public string GroupId { get; }
        public string MeshAssetId { get; }
        public string RendererAssetId { get; }
        public string RendererType { get; }
        public string GameObjectAssetId { get; }
        public string GameObjectName { get; }
        public int SlotIndex { get; }
        public int Confidence { get; }
        public string ConfidenceReason { get; }
        public float[]? TransformMatrix { get; }
        public string MeshName { get; }
        public long MeshPathId { get; }
        public long MeshByteSize { get; }
        public int MeshType { get; }
    }

    internal sealed class ModelGroupCandidateInfo
    {
        public ModelGroupCandidateInfo(ModelGroupInfo group)
        {
            Group = group;
        }

        public ModelGroupInfo Group { get; }
        public List<ModelGroupMeshInfo> Meshes { get; } = new();
    }

    internal sealed class AnimationClipTargetInfo
    {
        public AnimationClipTargetInfo(
            string clipAssetId,
            string animatorAssetId,
            string avatarAssetId,
            string meshAssetId,
            string modelGroupId,
            string relationKind,
            int matchScore,
            int matchedBoneCount = 0,
            int totalClipBoneCount = 0,
            string avatarName = "",
            string meshName = "",
            string modelGroupName = "",
            int modelGroupConfidence = 0)
        {
            ClipAssetId = clipAssetId ?? string.Empty;
            AnimatorAssetId = animatorAssetId ?? string.Empty;
            AvatarAssetId = avatarAssetId ?? string.Empty;
            MeshAssetId = meshAssetId ?? string.Empty;
            ModelGroupId = modelGroupId ?? string.Empty;
            RelationKind = relationKind ?? string.Empty;
            MatchScore = matchScore;
            MatchedBoneCount = matchedBoneCount;
            TotalClipBoneCount = totalClipBoneCount;
            AvatarName = avatarName ?? string.Empty;
            MeshName = meshName ?? string.Empty;
            ModelGroupName = modelGroupName ?? string.Empty;
            ModelGroupConfidence = modelGroupConfidence;
        }

        public string ClipAssetId { get; }
        public string AnimatorAssetId { get; }
        public string AvatarAssetId { get; }
        public string MeshAssetId { get; }
        public string ModelGroupId { get; }
        public string RelationKind { get; }
        public int MatchScore { get; }
        public int MatchedBoneCount { get; }
        public int TotalClipBoneCount { get; }
        public string AvatarName { get; }
        public string MeshName { get; }
        public string ModelGroupName { get; }
        public int ModelGroupConfidence { get; }
    }
}
