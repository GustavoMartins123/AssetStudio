using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia;

public class AssetItem : IAssetHandleTag
{
    private Object? _asset;
    public Object? Asset
    {
        get
        {
            if (_asset == null && Handle != null)
            {
                var sourceFile = Handle.SourceFile ?? SourceFile;
                _asset = sourceFile?.assetsManager?.ResolveHandle(Handle);
            }
            return _asset;
        }
        set => _asset = value;
    }

    public void ClearAsset()
    {
        _asset = null;
    }

    public AssetHandle? Handle { get; set; }
    public SerializedFile? SourceFile { get; set; }
    public GameObjectNode? TreeNode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string TypeString { get; set; }
    public string DisplayType => GetDisplayType();
    public string UniqueID { get; set; } = string.Empty;
    public long PathID { get; set; }
    public string PathIDString { get; set; } = string.Empty;
    public long Size { get; set; }
    public long FullSize { get; set; }
    public ClassIDType Type { get; set; }

    public AssetItem(Object asset)
    {
        Asset = asset;
        SourceFile = asset.assetsFile;
        TypeString = asset.type.ToString() ?? string.Empty;
        Type = asset.type;
        PathID = asset.m_PathID;
        PathIDString = PathID.ToString(CultureInfo.InvariantCulture);
        Size = asset.byteSize;
        FullSize = asset.byteSize;
    }

    public AssetItem(AssetHandle handle)
    {
        Handle = handle;
        SourceFile = handle.SourceFile;
        TypeString = handle.Type.ToString() ?? string.Empty;
        Type = handle.Type;
        PathID = handle.PathID;
        PathIDString = PathID.ToString(CultureInfo.InvariantCulture);
        Size = handle.ByteSize;
        FullSize = handle.ByteSize;
        Name = handle.Name ?? string.Empty;
        Container = handle.Container ?? string.Empty;
    }

    private string GetDisplayType()
    {
        var display = TypeString ?? string.Empty;
        if (Type == ClassIDType.PrefabInstance)
        {
            display = "Prefab (Composite)";
        }
        else if (Type == ClassIDType.GameObject)
        {
            display = "GameObject (Hierarchy Node)";
        }
        else if (Type == ClassIDType.MonoBehaviour)
        {
            display = "MonoBehaviour (Script Instance)";
        }
        else if (Type == ClassIDType.Mesh)
        {
            display = "Mesh (Geometry)";
        }
        else if (Type == ClassIDType.Material)
        {
            display = "Material (Shader Settings)";
        }
        else if (IsComponentType(Type))
        {
            display = $"{TypeString} (Component)";
        }

        if (IsFbxSubAsset())
        {
            return $"{display} (FBX sub-asset)";
        }

        return display ?? string.Empty;
    }

    private bool IsComponentType(ClassIDType type)
    {
        return type == ClassIDType.Transform ||
               type == ClassIDType.MeshRenderer ||
               type == ClassIDType.MeshFilter ||
               type == ClassIDType.SkinnedMeshRenderer ||
               type == ClassIDType.Animator ||
               type == ClassIDType.Animation ||
               type == ClassIDType.Component ||
               type == ClassIDType.RectTransform ||
               type == ClassIDType.Behaviour ||
               type == ClassIDType.MonoBehaviour;
    }

    public bool IsFbxSubAsset()
    {
        if (string.IsNullOrEmpty(Container))
        {
            return false;
        }

        return Container
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
    }
}
