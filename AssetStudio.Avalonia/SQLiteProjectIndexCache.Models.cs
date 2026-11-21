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

    internal sealed class ResolvedMeshSubMeshMaterial
    {
        public ResolvedMeshSubMeshMaterial(
            string meshAssetId,
            int subMeshIndex,
            string materialAssetId,
            string rendererAssetId,
            string rendererType,
            string materialName,
            string mainTextureAssetId,
            string mainTextureSlotName,
            float baseColorR,
            float baseColorG,
            float baseColorB,
            float baseColorA,
            string resolutionSource)
        {
            MeshAssetId = meshAssetId ?? string.Empty;
            SubMeshIndex = subMeshIndex;
            MaterialAssetId = materialAssetId ?? string.Empty;
            RendererAssetId = rendererAssetId ?? string.Empty;
            RendererType = rendererType ?? string.Empty;
            MaterialName = materialName ?? string.Empty;
            MainTextureAssetId = mainTextureAssetId ?? string.Empty;
            MainTextureSlotName = mainTextureSlotName ?? string.Empty;
            BaseColorR = baseColorR;
            BaseColorG = baseColorG;
            BaseColorB = baseColorB;
            BaseColorA = baseColorA;
            ResolutionSource = resolutionSource ?? "preparation";
        }

        public string MeshAssetId { get; }
        public int SubMeshIndex { get; }
        public string MaterialAssetId { get; }
        public string RendererAssetId { get; }
        public string RendererType { get; }
        public string MaterialName { get; }
        public string MainTextureAssetId { get; }
        public string MainTextureSlotName { get; }
        public float BaseColorR { get; }
        public float BaseColorG { get; }
        public float BaseColorB { get; }
        public float BaseColorA { get; }
        public string ResolutionSource { get; }
    }

    internal sealed class MaterialCacheEntryInfo
    {
        public MaterialCacheEntryInfo(
            string materialAssetId,
            string materialName,
            string parentMaterialAssetId,
            string mainTextureAssetId,
            string mainTextureSlotName,
            float baseColorR,
            float baseColorG,
            float baseColorB,
            float baseColorA,
            string shaderName,
            int textureSlotCount,
            string resolutionSource)
        {
            MaterialAssetId = materialAssetId ?? string.Empty;
            MaterialName = materialName ?? string.Empty;
            ParentMaterialAssetId = parentMaterialAssetId ?? string.Empty;
            MainTextureAssetId = mainTextureAssetId ?? string.Empty;
            MainTextureSlotName = mainTextureSlotName ?? string.Empty;
            BaseColorR = baseColorR;
            BaseColorG = baseColorG;
            BaseColorB = baseColorB;
            BaseColorA = baseColorA;
            ShaderName = shaderName ?? string.Empty;
            TextureSlotCount = textureSlotCount;
            ResolutionSource = resolutionSource ?? "preparation";
        }

        public string MaterialAssetId { get; }
        public string MaterialName { get; }
        public string ParentMaterialAssetId { get; }
        public string MainTextureAssetId { get; }
        public string MainTextureSlotName { get; }
        public float BaseColorR { get; }
        public float BaseColorG { get; }
        public float BaseColorB { get; }
        public float BaseColorA { get; }
        public string ShaderName { get; }
        public int TextureSlotCount { get; }
        public string ResolutionSource { get; }
    }

    internal sealed class MaterialTextureSlotInfo
    {
        public MaterialTextureSlotInfo(
            string materialAssetId,
            string previewMaterialAssetId,
            string slotName,
            int slotIndex,
            string textureAssetId,
            int textureFileId,
            long texturePathId,
            bool isResolved,
            bool isMainTexture,
            float scaleX,
            float scaleY,
            float offsetX,
            float offsetY,
            string resolutionSource)
        {
            MaterialAssetId = materialAssetId ?? string.Empty;
            PreviewMaterialAssetId = previewMaterialAssetId ?? string.Empty;
            SlotName = slotName ?? string.Empty;
            SlotIndex = slotIndex;
            TextureAssetId = textureAssetId ?? string.Empty;
            TextureFileId = textureFileId;
            TexturePathId = texturePathId;
            IsResolved = isResolved;
            IsMainTexture = isMainTexture;
            ScaleX = scaleX;
            ScaleY = scaleY;
            OffsetX = offsetX;
            OffsetY = offsetY;
            ResolutionSource = resolutionSource ?? "preparation";
        }

        public string MaterialAssetId { get; }
        public string PreviewMaterialAssetId { get; }
        public string SlotName { get; }
        public int SlotIndex { get; }
        public string TextureAssetId { get; }
        public int TextureFileId { get; }
        public long TexturePathId { get; }
        public bool IsResolved { get; }
        public bool IsMainTexture { get; }
        public float ScaleX { get; }
        public float ScaleY { get; }
        public float OffsetX { get; }
        public float OffsetY { get; }
        public string ResolutionSource { get; }
    }

    internal sealed class CompositeAssetPartInfo
    {
        public CompositeAssetPartInfo(
            string compositeAssetId,
            int partIndex,
            string meshAssetId,
            string rendererAssetId,
            string rendererType,
            string gameObjectAssetId,
            string gameObjectName,
            float[]? transformMatrix,
            string primaryMaterialAssetId,
            int confidence,
            string meshName,
            long meshPathId,
            long meshByteSize)
        {
            CompositeAssetId = compositeAssetId ?? string.Empty;
            PartIndex = partIndex;
            MeshAssetId = meshAssetId ?? string.Empty;
            RendererAssetId = rendererAssetId ?? string.Empty;
            RendererType = rendererType ?? string.Empty;
            GameObjectAssetId = gameObjectAssetId ?? string.Empty;
            GameObjectName = gameObjectName ?? string.Empty;
            TransformMatrix = transformMatrix;
            PrimaryMaterialAssetId = primaryMaterialAssetId ?? string.Empty;
            Confidence = confidence;
            MeshName = meshName ?? string.Empty;
            MeshPathId = meshPathId;
            MeshByteSize = meshByteSize;
        }

        public string CompositeAssetId { get; }
        public int PartIndex { get; }
        public string MeshAssetId { get; }
        public string RendererAssetId { get; }
        public string RendererType { get; }
        public string GameObjectAssetId { get; }
        public string GameObjectName { get; }
        public float[]? TransformMatrix { get; }
        public string PrimaryMaterialAssetId { get; }
        public int Confidence { get; }
        public string MeshName { get; }
        public long MeshPathId { get; }
        public long MeshByteSize { get; }
    }

    internal sealed class CompositeAssetInfo
    {
        public CompositeAssetInfo(
            string compositeAssetId,
            string compositeKind,
            string displayName,
            string rootGameObjectAssetId,
            string animatorAssetId,
            string avatarAssetId,
            string controllerAssetId,
            string sourceFileName,
            int meshPartCount,
            int distinctMeshCount,
            int distinctMaterialCount,
            int confidence,
            string resolutionSource)
        {
            CompositeAssetId = compositeAssetId ?? string.Empty;
            CompositeKind = compositeKind ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            RootGameObjectAssetId = rootGameObjectAssetId ?? string.Empty;
            AnimatorAssetId = animatorAssetId ?? string.Empty;
            AvatarAssetId = avatarAssetId ?? string.Empty;
            ControllerAssetId = controllerAssetId ?? string.Empty;
            SourceFileName = sourceFileName ?? string.Empty;
            MeshPartCount = meshPartCount;
            DistinctMeshCount = distinctMeshCount;
            DistinctMaterialCount = distinctMaterialCount;
            Confidence = confidence;
            ResolutionSource = resolutionSource ?? "preparation";
        }

        public string CompositeAssetId { get; }
        public string CompositeKind { get; }
        public string DisplayName { get; }
        public string RootGameObjectAssetId { get; }
        public string AnimatorAssetId { get; }
        public string AvatarAssetId { get; }
        public string ControllerAssetId { get; }
        public string SourceFileName { get; }
        public int MeshPartCount { get; }
        public int DistinctMeshCount { get; }
        public int DistinctMaterialCount { get; }
        public int Confidence { get; }
        public string ResolutionSource { get; }
        public List<CompositeAssetPartInfo> Parts { get; } = new();
        public List<string> DistinctMeshIds { get; } = new();
        public List<string> DistinctMaterialIds { get; } = new();
        public List<string> DistinctTextureIds { get; } = new();
    }
}
