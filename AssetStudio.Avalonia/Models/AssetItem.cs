using System;
using System.Globalization;
using System.Threading;
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
    private string _container = string.Empty;
    private string _typeString = string.Empty;
    private ClassIDType _type;
    private string? _displayType;
    private byte _fbxState; // 0 = not computed, 1 = false, 2 = true (races are benign: value is idempotent)

    public string Container
    {
        get => _container;
        set
        {
            _container = value;
            InvalidateDerivedState();
        }
    }

    public string TypeString
    {
        get => _typeString;
        set
        {
            _typeString = value;
            InvalidateDerivedState();
        }
    }

    public string DisplayType => Volatile.Read(ref _displayType) ?? CacheDisplayType();
    public string UniqueID { get; set; } = string.Empty;
    public long PathID { get; set; }
    public string PathIDString { get; set; } = string.Empty;
    public long Size { get; set; }
    public long FullSize { get; set; }
    public ClassIDType Type
    {
        get => _type;
        set
        {
            _type = value;
            InvalidateDerivedState();
        }
    }

    private void InvalidateDerivedState()
    {
        Volatile.Write(ref _displayType, null);
        Volatile.Write(ref _fbxState, 0);
    }

    private string CacheDisplayType()
    {
        var computed = GetDisplayType();
        Volatile.Write(ref _displayType, computed);
        return computed;
    }

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

    private static bool IsComponentType(ClassIDType type)
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
        var state = Volatile.Read(ref _fbxState);
        if (state == 0)
        {
            state = ContainsFbxSegment(_container) ? (byte)2 : (byte)1;
            Volatile.Write(ref _fbxState, state);
        }

        return state == 2;
    }

    /// <summary>
    /// Zero-allocation equivalent of splitting on '/' and '\' (dropping empty segments) and checking
    /// whether any segment has a ".fbx" extension (case-insensitive).
    /// </summary>
    internal static bool ContainsFbxSegment(string? container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return false;
        }

        ReadOnlySpan<char> remaining = container.AsSpan();
        while (!remaining.IsEmpty)
        {
            var sep = remaining.IndexOfAny('/', '\\');
            ReadOnlySpan<char> segment;
            if (sep < 0)
            {
                segment = remaining;
                remaining = default;
            }
            else
            {
                segment = remaining.Slice(0, sep);
                remaining = remaining.Slice(sep + 1);
            }

            if (segment.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}