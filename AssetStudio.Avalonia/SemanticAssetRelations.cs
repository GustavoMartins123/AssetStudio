using System.Collections.Generic;

namespace AssetStudio.Avalonia;

internal sealed class SemanticAssetRelations
{
    public List<SemanticSourceFileEntry> SourceFiles { get; } = new();
    public List<SemanticAssetEdgeRelation> AssetEdges { get; } = new();
    public List<SemanticMeshRendererRelation> MeshRenderers { get; } = new();
    public List<SemanticMeshMaterialRelation> MeshMaterials { get; } = new();
    public List<SemanticMaterialTextureRelation> MaterialTextures { get; } = new();

    public bool HasRelations =>
        AssetEdges.Count > 0 ||
        MeshRenderers.Count > 0 ||
        MeshMaterials.Count > 0 ||
        MaterialTextures.Count > 0;

    public bool HasMaterialRelations =>
        AssetEdges.Count > 0 ||
        MeshMaterials.Count > 0 ||
        MaterialTextures.Count > 0;

    public void Merge(SemanticAssetRelations other)
    {
        if (other == null)
        {
            return;
        }

        SourceFiles.AddRange(other.SourceFiles);
        AssetEdges.AddRange(other.AssetEdges);
        MeshRenderers.AddRange(other.MeshRenderers);
        MeshMaterials.AddRange(other.MeshMaterials);
        MaterialTextures.AddRange(other.MaterialTextures);
    }
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
