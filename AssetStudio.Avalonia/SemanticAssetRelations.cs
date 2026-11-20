using System.Collections.Generic;

namespace AssetStudio.Avalonia;

internal sealed class SemanticAssetRelations
{
    public List<SemanticSourceFileEntry> SourceFiles { get; } = new();
    public List<SemanticAssetEdgeRelation> AssetEdges { get; } = new();
    public List<SemanticModelGroupRelation> ModelGroups { get; } = new();
    public List<SemanticModelGroupMeshRelation> ModelGroupMeshes { get; } = new();
    public List<SemanticMeshRendererRelation> MeshRenderers { get; } = new();
    public List<SemanticMeshMaterialRelation> MeshMaterials { get; } = new();
    public List<SemanticMaterialTextureRelation> MaterialTextures { get; } = new();
    public List<SemanticAnimationClipTargetRelation> AnimationClipTargets { get; } = new();
    public List<SemanticRigBoneHashEntry> RigBoneHashes { get; } = new();
    public List<SemanticClipBoneHashEntry> ClipBoneHashes { get; } = new();

    public bool HasRelations =>
        AssetEdges.Count > 0 ||
        ModelGroups.Count > 0 ||
        ModelGroupMeshes.Count > 0 ||
        MeshRenderers.Count > 0 ||
        MeshMaterials.Count > 0 ||
        MaterialTextures.Count > 0 ||
        AnimationClipTargets.Count > 0 ||
        RigBoneHashes.Count > 0 ||
        ClipBoneHashes.Count > 0;

    public bool HasMaterialRelations =>
        AssetEdges.Count > 0 ||
        MeshMaterials.Count > 0 ||
        MaterialTextures.Count > 0 ||
        AnimationClipTargets.Count > 0 ||
        RigBoneHashes.Count > 0 ||
        ClipBoneHashes.Count > 0;

    public void Merge(SemanticAssetRelations other)
    {
        if (other == null)
        {
            return;
        }

        SourceFiles.AddRange(other.SourceFiles);
        AssetEdges.AddRange(other.AssetEdges);
        ModelGroups.AddRange(other.ModelGroups);
        ModelGroupMeshes.AddRange(other.ModelGroupMeshes);
        MeshRenderers.AddRange(other.MeshRenderers);
        MeshMaterials.AddRange(other.MeshMaterials);
        MaterialTextures.AddRange(other.MaterialTextures);
        AnimationClipTargets.AddRange(other.AnimationClipTargets);
        RigBoneHashes.AddRange(other.RigBoneHashes);
        ClipBoneHashes.AddRange(other.ClipBoneHashes);
    }
}

internal sealed class SemanticAnimationClipTargetRelation
{
    public SemanticAnimationClipTargetRelation(
        string clipAssetId,
        string animatorAssetId,
        string avatarAssetId,
        string meshAssetId,
        string modelGroupId,
        string relationKind,
        int matchScore,
        int matchedBoneCount = 0,
        int totalClipBoneCount = 0)
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
}

internal sealed class SemanticRigBoneHashEntry
{
    public SemanticRigBoneHashEntry(
        string avatarAssetId,
        string animatorAssetId,
        string meshAssetId,
        string modelGroupId,
        uint boneHash)
    {
        AvatarAssetId = avatarAssetId ?? string.Empty;
        AnimatorAssetId = animatorAssetId ?? string.Empty;
        MeshAssetId = meshAssetId ?? string.Empty;
        ModelGroupId = modelGroupId ?? string.Empty;
        BoneHash = boneHash;
    }

    public string AvatarAssetId { get; }
    public string AnimatorAssetId { get; }
    public string MeshAssetId { get; }
    public string ModelGroupId { get; }
    public uint BoneHash { get; }
}

internal sealed class SemanticClipBoneHashEntry
{
    public SemanticClipBoneHashEntry(
        string clipAssetId,
        uint boneHash,
        int totalClipBoneCount)
    {
        ClipAssetId = clipAssetId ?? string.Empty;
        BoneHash = boneHash;
        TotalClipBoneCount = totalClipBoneCount;
    }

    public string ClipAssetId { get; }
    public uint BoneHash { get; }
    public int TotalClipBoneCount { get; }
}

internal sealed class SemanticAssetEdgeRelation
{
    public SemanticAssetEdgeRelation(
        string sourceAssetId,
        string edgeKind,
        string slotName,
        int slotIndex,
        string targetAssetId,
        int sourceFileId,
        long sourcePathId,
        int targetFileId,
        long targetPathId,
        bool isResolved)
    {
        SourceAssetId = sourceAssetId;
        EdgeKind = edgeKind;
        SlotName = slotName;
        SlotIndex = slotIndex;
        TargetAssetId = targetAssetId;
        SourceFileId = sourceFileId;
        SourcePathId = sourcePathId;
        TargetFileId = targetFileId;
        TargetPathId = targetPathId;
        IsResolved = isResolved;
    }

    public string SourceAssetId { get; }
    public string EdgeKind { get; }
    public string SlotName { get; }
    public int SlotIndex { get; }
    public string TargetAssetId { get; }
    public int SourceFileId { get; }
    public long SourcePathId { get; }
    public int TargetFileId { get; }
    public long TargetPathId { get; }
    public bool IsResolved { get; }
}

internal sealed class SemanticModelGroupRelation
{
    public SemanticModelGroupRelation(
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

internal sealed class SemanticModelGroupMeshRelation
{
    public SemanticModelGroupMeshRelation(
        string groupId,
        string meshAssetId,
        string rendererAssetId,
        string rendererType,
        string gameObjectAssetId,
        string gameObjectName,
        int slotIndex,
        float[]? localToWorldMatrix,
        int confidence,
        string confidenceReason)
    {
        GroupId = groupId;
        MeshAssetId = meshAssetId;
        RendererAssetId = rendererAssetId;
        RendererType = rendererType;
        GameObjectAssetId = gameObjectAssetId;
        GameObjectName = gameObjectName;
        SlotIndex = slotIndex;
        LocalToWorldMatrix = localToWorldMatrix;
        Confidence = confidence;
        ConfidenceReason = confidenceReason;
    }

    public string GroupId { get; }
    public string MeshAssetId { get; }
    public string RendererAssetId { get; }
    public string RendererType { get; }
    public string GameObjectAssetId { get; }
    public string GameObjectName { get; }
    public int SlotIndex { get; }
    public float[]? LocalToWorldMatrix { get; }
    public int Confidence { get; }
    public string ConfidenceReason { get; }
}

internal sealed class SemanticSourceFileEntry
{
    public SemanticSourceFileEntry(string serializedFileName, string originalPath, string unityVersion, int objectCount)
    {
        SerializedFileName = serializedFileName;
        OriginalPath = originalPath;
        UnityVersion = unityVersion;
        ObjectCount = objectCount;
    }

    public string SerializedFileName { get; }
    public string OriginalPath { get; }
    public string UnityVersion { get; }
    public int ObjectCount { get; }
}

internal sealed class SemanticMeshRendererRelation
{
    public SemanticMeshRendererRelation(
        string meshAssetId,
        string rendererAssetId,
        string rendererType,
        string gameObjectAssetId,
        string gameObjectName,
        string description)
    {
        MeshAssetId = meshAssetId;
        RendererAssetId = rendererAssetId;
        RendererType = rendererType;
        GameObjectAssetId = gameObjectAssetId;
        GameObjectName = gameObjectName;
        Description = description;
    }

    public string MeshAssetId { get; }
    public string RendererAssetId { get; }
    public string RendererType { get; }
    public string GameObjectAssetId { get; }
    public string GameObjectName { get; }
    public string Description { get; }
}

internal sealed class SemanticMeshMaterialRelation
{
    public SemanticMeshMaterialRelation(
        string meshAssetId,
        string materialAssetId,
        string rendererAssetId,
        string rendererType,
        int subMeshIndex,
        int materialSlotIndex,
        int materialScore)
    {
        MeshAssetId = meshAssetId;
        MaterialAssetId = materialAssetId;
        RendererAssetId = rendererAssetId;
        RendererType = rendererType;
        SubMeshIndex = subMeshIndex;
        MaterialSlotIndex = materialSlotIndex;
        MaterialScore = materialScore;
    }

    public string MeshAssetId { get; }
    public string MaterialAssetId { get; }
    public string RendererAssetId { get; }
    public string RendererType { get; }
    public int SubMeshIndex { get; }
    public int MaterialSlotIndex { get; }
    public int MaterialScore { get; }
}

internal sealed class SemanticMaterialTextureRelation
{
    public SemanticMaterialTextureRelation(
        string materialAssetId,
        string previewMaterialAssetId,
        string slotName,
        int slotIndex,
        string textureAssetId,
        int textureFileId,
        long texturePathId,
        bool isResolved,
        bool isMainTexture)
    {
        MaterialAssetId = materialAssetId;
        PreviewMaterialAssetId = previewMaterialAssetId;
        SlotName = slotName;
        SlotIndex = slotIndex;
        TextureAssetId = textureAssetId;
        TextureFileId = textureFileId;
        TexturePathId = texturePathId;
        IsResolved = isResolved;
        IsMainTexture = isMainTexture;
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
}
