using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia;

public class AssetItem : IAssetHandleTag
{
    private sealed class CustomStringOverrides
    {
        public string? TypeString;
        public string? PathIDString;
    }

    private const int SmallTypeCacheSize = 512;
    private const int FbxStateShift = 29;
    private const int FbxStateMask = 0b11 << FbxStateShift;
    private const int HasOrdinalFlag = 1 << 28;
    private const int HasCustomTypeFlag = 1 << 27;
    private const int HasCustomPathIdFlag = 1 << 26;
    private const int MaxPackedOrdinal = (1 << 26) - 1;
    private const int OrdinalMask = MaxPackedOrdinal;

    private static readonly string?[] SmallTypeStrings = BuildSmallTypeStrings();
    private static readonly FrozenDictionary<ClassIDType, string> SparseTypeStrings = BuildSparseTypeStrings();
    private static readonly ConcurrentDictionary<int, string> UndefinedTypeStrings = new();

    private static readonly string?[] SmallNormalDisplayTypes = BuildSmallDisplayTypes(fbx: false);
    private static readonly string?[] SmallFbxDisplayTypes = BuildSmallDisplayTypes(fbx: true);
    private static readonly FrozenDictionary<ClassIDType, (string Normal, string Fbx)> SparseDisplayTypes = BuildSparseDisplayTypes();
    private static readonly ConcurrentDictionary<(int TypeId, bool IsFbx), string> UndefinedDisplayTypes = new();
    private static readonly ConditionalWeakTable<AssetItem, CustomStringOverrides> CustomStrings = new();

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
    private string? _uniqueId;
    public long PathID { get; set; }
    public long Size { get; set; }
    public long FullSize { get; set; }
    private ClassIDType _type;
    private int _state;

    public string Container
    {
        get => _container;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_container, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _container = normalized;
            InvalidateFbxState();
        }
    }

    public string TypeString
    {
        get
        {
            if ((Volatile.Read(ref _state) & HasCustomTypeFlag) != 0 &&
                CustomStrings.TryGetValue(this, out var custom) &&
                custom.TypeString != null)
            {
                return custom.TypeString;
            }

            return GetCachedTypeString(_type);
        }
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(normalized, GetCachedTypeString(_type), StringComparison.Ordinal))
            {
                if ((Volatile.Read(ref _state) & HasCustomTypeFlag) != 0)
                {
                    if (CustomStrings.TryGetValue(this, out var custom))
                    {
                        custom.TypeString = null;
                    }
                    ClearStateBits(HasCustomTypeFlag);
                }
                return;
            }

            CustomStrings.GetOrCreateValue(this).TypeString = normalized;
            SetStateBits(HasCustomTypeFlag);
        }
    }

    public string DisplayType
    {
        get
        {
            var isFbx = IsFbxSubAsset();
            if ((Volatile.Read(ref _state) & HasCustomTypeFlag) != 0 &&
                CustomStrings.TryGetValue(this, out var custom) &&
                custom.TypeString != null)
            {
                return FormatDisplayType(_type, custom.TypeString, isFbx);
            }

            return GetCachedDisplayType(_type, isFbx);
        }
    }

    public string UniqueID
    {
        get
        {
            var existing = Volatile.Read(ref _uniqueId);
            if (existing != null)
            {
                return existing;
            }

            var s = Volatile.Read(ref _state);
            if ((s & HasOrdinalFlag) == 0)
            {
                return string.Empty;
            }

            var formatted = " #" + (s & OrdinalMask).ToString(CultureInfo.InvariantCulture);
            return Interlocked.CompareExchange(ref _uniqueId, formatted, null) ?? formatted;
        }
        set
        {
            ClearStateBits(HasOrdinalFlag | OrdinalMask);
            Volatile.Write(ref _uniqueId, value ?? string.Empty);
        }
    }

    public string PathIDString
    {
        get
        {
            if ((Volatile.Read(ref _state) & HasCustomPathIdFlag) != 0 &&
                CustomStrings.TryGetValue(this, out var custom) &&
                custom.PathIDString != null)
            {
                return custom.PathIDString;
            }

            return PathID.ToString(CultureInfo.InvariantCulture);
        }
        set
        {
            var normalized = value ?? string.Empty;
            Span<char> buffer = stackalloc char[32];
            if (PathID.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture) &&
                normalized.AsSpan().SequenceEqual(buffer.Slice(0, written)))
            {
                if ((Volatile.Read(ref _state) & HasCustomPathIdFlag) != 0)
                {
                    if (CustomStrings.TryGetValue(this, out var custom))
                    {
                        custom.PathIDString = null;
                    }
                    ClearStateBits(HasCustomPathIdFlag);
                }
                return;
            }

            CustomStrings.GetOrCreateValue(this).PathIDString = normalized;
            SetStateBits(HasCustomPathIdFlag);
        }
    }

    public ClassIDType Type
    {
        get => _type;
        set => _type = value;
    }

    internal bool HasOrdinalOrUniqueId =>
        Volatile.Read(ref _uniqueId) != null || (Volatile.Read(ref _state) & HasOrdinalFlag) != 0;

    internal void SetOrdinal(int ordinal)
    {
        if ((uint)ordinal <= MaxPackedOrdinal)
        {
            Volatile.Write(ref _uniqueId, null);
            UpdateStateBits(HasOrdinalFlag | OrdinalMask, HasOrdinalFlag | ordinal);
        }
        else
        {
            ClearStateBits(HasOrdinalFlag | OrdinalMask);
            Volatile.Write(ref _uniqueId, " #" + ordinal.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal bool TryGetPathIDSearchSpan(Span<char> scratch, out ReadOnlySpan<char> span)
    {
        if ((Volatile.Read(ref _state) & HasCustomPathIdFlag) != 0 &&
            CustomStrings.TryGetValue(this, out var custom) &&
            custom.PathIDString != null)
        {
            span = custom.PathIDString.AsSpan();
            return true;
        }

        if (PathID.TryFormat(scratch, out var written, provider: CultureInfo.InvariantCulture))
        {
            span = scratch.Slice(0, written);
            return true;
        }

        span = PathIDString.AsSpan();
        return true;
    }

    internal static int CompareUniqueId(AssetItem left, AssetItem right)
    {
        if (Volatile.Read(ref left._uniqueId) == null &&
            Volatile.Read(ref right._uniqueId) == null)
        {
            var leftState = Volatile.Read(ref left._state);
            var rightState = Volatile.Read(ref right._state);
            var leftHasOrdinal = (leftState & HasOrdinalFlag) != 0;
            var rightHasOrdinal = (rightState & HasOrdinalFlag) != 0;

            if (!leftHasOrdinal && !rightHasOrdinal)
            {
                return 0;
            }
            if (!leftHasOrdinal)
            {
                return 1;
            }
            if (!rightHasOrdinal)
            {
                return -1;
            }

            Span<char> leftBuf = stackalloc char[16];
            Span<char> rightBuf = stackalloc char[16];
            if (TryFormatOrdinal(leftState & OrdinalMask, leftBuf, out var leftLen) &&
                TryFormatOrdinal(rightState & OrdinalMask, rightBuf, out var rightLen))
            {
                return leftBuf.Slice(0, leftLen).CompareTo(rightBuf.Slice(0, rightLen), StringComparison.OrdinalIgnoreCase);
            }
        }

        return AssetListQuery.CompareNullableText(left.UniqueID, right.UniqueID);
    }

    private static bool TryFormatOrdinal(int ordinal, Span<char> destination, out int length)
    {
        if (destination.Length < 3)
        {
            length = 0;
            return false;
        }

        destination[0] = ' ';
        destination[1] = '#';
        if (ordinal.TryFormat(destination.Slice(2), out var written, provider: CultureInfo.InvariantCulture))
        {
            length = 2 + written;
            return true;
        }

        length = 0;
        return false;
    }

    public AssetItem(Object asset)
    {
        _asset = asset;
        SourceFile = asset.assetsFile;
        _type = asset.type;
        PathID = asset.m_PathID;
        Size = asset.byteSize;
        FullSize = asset.byteSize;
    }

    public AssetItem(AssetHandle handle)
    {
        Handle = handle;
        SourceFile = handle.SourceFile;
        _type = handle.Type;
        PathID = handle.PathID;
        Size = handle.ByteSize;
        FullSize = handle.ByteSize;
        Name = handle.Name ?? string.Empty;
        _container = handle.Container ?? string.Empty;
    }

    internal AssetItem(AssetHandle handle, int ordinal)
        : this(handle)
    {
        if (ordinal >= 0)
        {
            SetOrdinal(ordinal);
        }
    }

    public static string GetCachedTypeString(ClassIDType type)
    {
        var id = (int)type;
        if ((uint)id < SmallTypeCacheSize)
        {
            var cached = SmallTypeStrings[id];
            if (cached != null)
            {
                return cached;
            }
        }
        else if (SparseTypeStrings.TryGetValue(type, out var sparse))
        {
            return sparse;
        }

        return UndefinedTypeStrings.GetOrAdd(id, static k => ((ClassIDType)k).ToString());
    }

    private static string GetCachedDisplayType(ClassIDType type, bool isFbx)
    {
        var id = (int)type;
        if ((uint)id < SmallTypeCacheSize)
        {
            var table = isFbx ? SmallFbxDisplayTypes : SmallNormalDisplayTypes;
            var cached = table[id];
            if (cached != null)
            {
                return cached;
            }
        }
        else if (SparseDisplayTypes.TryGetValue(type, out var pair))
        {
            return isFbx ? pair.Fbx : pair.Normal;
        }

        return UndefinedDisplayTypes.GetOrAdd(
            (id, isFbx),
            static key =>
            {
                var t = (ClassIDType)key.TypeId;
                var typeStr = GetCachedTypeString(t);
                return FormatDisplayType(t, typeStr, key.IsFbx);
            });
    }

    private static string FormatDisplayType(ClassIDType type, string typeString, bool isFbx)
    {
        var display = type switch
        {
            ClassIDType.PrefabInstance => "Prefab (Composite)",
            ClassIDType.GameObject => "GameObject (Hierarchy Node)",
            ClassIDType.MonoBehaviour => "MonoBehaviour (Script Instance)",
            ClassIDType.Mesh => "Mesh (Geometry)",
            ClassIDType.Material => "Material (Shader Settings)",
            _ when IsComponentType(type) => $"{typeString} (Component)",
            _ => typeString
        };

        return isFbx ? $"{display} (FBX sub-asset)" : display;
    }

    private static string?[] BuildSmallTypeStrings()
    {
        var table = new string?[SmallTypeCacheSize];
        foreach (ClassIDType value in Enum.GetValues<ClassIDType>())
        {
            var id = (int)value;
            if ((uint)id < SmallTypeCacheSize)
            {
                table[id] ??= value.ToString();
            }
        }
        return table;
    }

    private static FrozenDictionary<ClassIDType, string> BuildSparseTypeStrings()
    {
        var map = new Dictionary<ClassIDType, string>();
        foreach (ClassIDType value in Enum.GetValues<ClassIDType>())
        {
            var id = (int)value;
            if ((uint)id >= SmallTypeCacheSize)
            {
                map.TryAdd(value, value.ToString());
            }
        }
        return map.ToFrozenDictionary();
    }

    private static string?[] BuildSmallDisplayTypes(bool fbx)
    {
        var table = new string?[SmallTypeCacheSize];
        foreach (ClassIDType value in Enum.GetValues<ClassIDType>())
        {
            var id = (int)value;
            if ((uint)id < SmallTypeCacheSize && table[id] == null)
            {
                var baseTypeStr = SmallTypeStrings[id] ?? value.ToString();
                table[id] = FormatDisplayType(value, baseTypeStr, fbx);
            }
        }
        return table;
    }

    private static FrozenDictionary<ClassIDType, (string Normal, string Fbx)> BuildSparseDisplayTypes()
    {
        var map = new Dictionary<ClassIDType, (string Normal, string Fbx)>();
        foreach (ClassIDType value in Enum.GetValues<ClassIDType>())
        {
            var id = (int)value;
            if ((uint)id >= SmallTypeCacheSize && !map.ContainsKey(value))
            {
                var baseTypeStr = SparseTypeStrings[value];
                map[value] = (
                    FormatDisplayType(value, baseTypeStr, isFbx: false),
                    FormatDisplayType(value, baseTypeStr, isFbx: true));
            }
        }
        return map.ToFrozenDictionary();
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
        var s = Volatile.Read(ref _state);
        var fbxBits = (s & FbxStateMask) >> FbxStateShift;
        if (fbxBits == 0)
        {
            var computed = ContainsFbxSegment(_container) ? 2 : 1;
            UpdateStateBits(FbxStateMask, computed << FbxStateShift);
            fbxBits = computed;
        }

        return fbxBits == 2;
    }

    private void InvalidateFbxState()
    {
        ClearStateBits(FbxStateMask);
    }

    private void SetStateBits(int bits)
    {
        int current;
        int next;
        do
        {
            current = Volatile.Read(ref _state);
            next = current | bits;
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
    }

    private void ClearStateBits(int mask)
    {
        int current;
        int next;
        do
        {
            current = Volatile.Read(ref _state);
            next = current & ~mask;
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
    }

    private void UpdateStateBits(int mask, int valueBits)
    {
        int current;
        int next;
        do
        {
            current = Volatile.Read(ref _state);
            next = (current & ~mask) | (valueBits & mask);
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
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
