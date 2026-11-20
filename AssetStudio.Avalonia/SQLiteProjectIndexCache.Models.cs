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
}
