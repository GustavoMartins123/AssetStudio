using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia.Services.Scripting;

public sealed class Il2CppBinaryMetadataContext
{
    public int MetadataVersion { get; init; }
    public int TotalTypes { get; init; }
    public int TotalMethods { get; init; }
    public int Int32ByvalTypeIndex { get; init; } = -1;
    public int Int32TypeDefIndex { get; init; } = -1;
    public int ObjectByvalTypeIndex { get; init; } = -1;
    public int StringByvalTypeIndex { get; init; } = -1;
    public int VoidByvalTypeIndex { get; init; } = -1;
    public int SampleValueTypeByvalTypeIndex { get; init; } = -1;
    public IReadOnlyList<string> ImageNames { get; init; } = Array.Empty<string>();
}

public readonly record struct Il2CppResolvedType(
    string TypeName,
    ushort Attributes,
    bool IsByRef,
    bool IsPinned);

public sealed class Il2CppBinaryIndex
{
    private const int MaxTypeResolveDepth = 16;

    internal readonly record struct RawIl2CppType(ulong Data, uint Bits)
    {
        public ushort Attributes => (ushort)(Bits & 0xFFFFu);
        public byte TypeEnum => (byte)((Bits >> 16) & 0xFFu);
    }

    internal readonly record struct RawArrayType(ulong ElementTypeVa, byte Rank);

    internal readonly record struct RawGenericClass(int TypeDefIndex, ulong[] ArgTypeVas);

    internal readonly record struct SectionMapping(
        ulong VirtualAddress,
        ulong VirtualSize,
        ulong FileOffset,
        ulong RawSize,
        bool IsExecutable);

    internal Il2CppBinaryIndex()
    {
    }

    public required string FilePath { get; init; }
    public required long FileLength { get; init; }
    public required long LastWriteTicks { get; init; }
    public required int MetadataVersion { get; init; }
    public required int TotalTypes { get; init; }
    public required ulong ImageBase { get; init; }
    public required int PointerSize { get; init; }
    public required bool IsElf { get; init; }
    public required bool UseV272ByrefBit { get; init; }

    internal SectionMapping[] Sections { get; init; } = Array.Empty<SectionMapping>();
    internal RawIl2CppType[] Types { get; init; } = Array.Empty<RawIl2CppType>();
    internal Dictionary<ulong, RawIl2CppType> ExtraTypesByVa { get; init; } = new();
    internal Dictionary<ulong, RawArrayType> ArrayTypesByVa { get; init; } = new();
    internal Dictionary<ulong, RawGenericClass> GenericClassesByVa { get; init; } = new();
    internal long[] FieldOffsetTableFileOffsets { get; init; } = Array.Empty<long>();
    internal Dictionary<string, ulong[]> ModuleMethodRvas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal ulong[] FlatMethodRvas { get; init; } = Array.Empty<ulong>();

    internal long LastAccessTick { get; set; }

    public string FileName => Path.GetFileName(FilePath);
    public bool HasTypes => Types.Length > 0;
    public bool HasFieldOffsets => FieldOffsetTableFileOffsets.Length > 0;
    public bool HasMethodPointers => ModuleMethodRvas.Count > 0 || FlatMethodRvas.Length > 0;
    public int TypeCount => Types.Length;
    public int ModuleCount => ModuleMethodRvas.Count;

    public bool TryResolveType(
        int typeIndex,
        Func<int, (string Namespace, IReadOnlyList<string> TypeChain)?> resolveTypeDefChain,
        Func<int, string?> resolveGenericParamName,
        out Il2CppResolvedType resolved)
    {
        resolved = default;
        if (typeIndex < 0 || typeIndex >= Types.Length)
        {
            return false;
        }

        var raw = Types[typeIndex];
        if (raw.Bits == 0 && raw.Data == 0)
        {
            return false;
        }

        var isByRef = IsByRefBitSet(raw.Bits);
        var isPinned = IsPinnedBitSet(raw.Bits);
        var attrs = raw.Attributes;
        var visited = new HashSet<ulong>();

        var typeName = FormatRawType(
            raw,
            resolveTypeDefChain,
            resolveGenericParamName,
            visited,
            depth: 0,
            ref isByRef);
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        resolved = new Il2CppResolvedType(typeName, attrs, isByRef, isPinned);
        return true;
    }

    public bool TryGetFieldOffset(int typeDefIndex, int fieldIndex, out int offset)
    {
        offset = -1;
        if (typeDefIndex < 0
            || typeDefIndex >= FieldOffsetTableFileOffsets.Length
            || fieldIndex < 0)
        {
            return false;
        }

        var tableFileOff = FieldOffsetTableFileOffsets[typeDefIndex];
        if (tableFileOff < 0)
        {
            return false;
        }

        var entryOffset = tableFileOff + (long)fieldIndex * 4;
        if (entryOffset < 0 || entryOffset + 4 > FileLength)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            if (entryOffset + 4 > stream.Length)
            {
                return false;
            }

            stream.Position = entryOffset;
            Span<byte> buf = stackalloc byte[4];
            if (ReadExact(stream, buf) != 4)
            {
                return false;
            }

            offset = BinaryPrimitives.ReadInt32LittleEndian(buf);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public int[]? TryGetFieldOffsets(int typeDefIndex, int fieldCount)
    {
        if (fieldCount <= 0
            || typeDefIndex < 0
            || typeDefIndex >= FieldOffsetTableFileOffsets.Length)
        {
            return null;
        }

        var tableFileOff = FieldOffsetTableFileOffsets[typeDefIndex];
        if (tableFileOff < 0)
        {
            return null;
        }

        var byteLen = (long)fieldCount * 4;
        if (byteLen <= 0 || byteLen > 1024 * 1024 || tableFileOff + byteLen > FileLength)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            if (tableFileOff + byteLen > stream.Length)
            {
                return null;
            }

            stream.Position = tableFileOff;
            var raw = new byte[byteLen];
            if (ReadExact(stream, raw) != raw.Length)
            {
                return null;
            }

            var offsets = new int[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4, 4));
            }

            return offsets;
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetMethodRva(
        string? imageName,
        uint methodToken,
        int methodIndex,
        out ulong rva,
        out ulong fileOffset,
        out ulong va)
    {
        rva = 0;
        fileOffset = 0;
        va = 0;

        if (!string.IsNullOrWhiteSpace(imageName)
            && ModuleMethodRvas.Count > 0
            && (methodToken >> 24) == 0x06)
        {
            var rid = (int)(methodToken & 0x00FFFFFFu);
            var idx = rid - 1;
            if (idx >= 0
                && TryGetModuleRvas(imageName, out var moduleRvas)
                && idx < moduleRvas.Length)
            {
                var foundRva = moduleRvas[idx];
                if (foundRva != 0)
                {
                    rva = foundRva;
                    va = ImageBase + foundRva;
                    TryRvaToFileOffset(foundRva, out fileOffset);
                    return true;
                }
            }
        }

        if (methodIndex >= 0 && methodIndex < FlatMethodRvas.Length)
        {
            var foundRva = FlatMethodRvas[methodIndex];
            if (foundRva != 0)
            {
                rva = foundRva;
                va = ImageBase + foundRva;
                TryRvaToFileOffset(foundRva, out fileOffset);
                return true;
            }
        }

        return false;
    }

    public bool TryRvaToFileOffset(ulong rva, out ulong fileOffset)
    {
        fileOffset = 0;
        var targetVa = ImageBase + rva;
        for (var i = 0; i < Sections.Length; i++)
        {
            var sec = Sections[i];
            if (targetVa >= sec.VirtualAddress && targetVa < sec.VirtualAddress + sec.RawSize)
            {
                fileOffset = sec.FileOffset + (targetVa - sec.VirtualAddress);
                return true;
            }
        }

        return false;
    }

    private bool TryGetModuleRvas(string imageName, out ulong[] rvas)
    {
        if (ModuleMethodRvas.TryGetValue(imageName, out rvas!))
        {
            return true;
        }

        var fileNameOnly = Path.GetFileName(imageName);
        if (!string.Equals(fileNameOnly, imageName, StringComparison.OrdinalIgnoreCase)
            && ModuleMethodRvas.TryGetValue(fileNameOnly, out rvas!))
        {
            return true;
        }

        rvas = Array.Empty<ulong>();
        return false;
    }

    private bool IsByRefBitSet(uint bits)
    {
        return UseV272ByrefBit
            ? (bits & (1u << 29)) != 0
            : (bits & (1u << 30)) != 0;
    }

    private bool IsPinnedBitSet(uint bits)
    {
        return UseV272ByrefBit
            ? (bits & (1u << 30)) != 0
            : (bits & (1u << 31)) != 0;
    }

    private string? FormatRawType(
        RawIl2CppType raw,
        Func<int, (string Namespace, IReadOnlyList<string> TypeChain)?> resolveTypeDefChain,
        Func<int, string?> resolveGenericParamName,
        HashSet<ulong> visitedVas,
        int depth,
        ref bool isByRef)
    {
        if (depth > MaxTypeResolveDepth)
        {
            return null;
        }

        switch (raw.TypeEnum)
        {
            case 0x01: return "System.Void";
            case 0x02: return "System.Boolean";
            case 0x03: return "System.Char";
            case 0x04: return "System.SByte";
            case 0x05: return "System.Byte";
            case 0x06: return "System.Int16";
            case 0x07: return "System.UInt16";
            case 0x08: return "System.Int32";
            case 0x09: return "System.UInt32";
            case 0x0A: return "System.Int64";
            case 0x0B: return "System.UInt64";
            case 0x0C: return "System.Single";
            case 0x0D: return "System.Double";
            case 0x0E: return "System.String";
            case 0x16: return "System.TypedReference";
            case 0x18: return "System.IntPtr";
            case 0x19: return "System.UIntPtr";
            case 0x1B: return "System.IntPtr";
            case 0x1C: return "System.Object";

            case 0x11: // VALUETYPE
            case 0x12: // CLASS
            case 0x55: // ENUM
            {
                var tdIdx = (int)(raw.Data & 0xFFFFFFFFUL);
                var chain = resolveTypeDefChain(tdIdx);
                if (chain == null || chain.Value.TypeChain.Count == 0)
                {
                    return null;
                }

                return JoinTypeDefChain(chain.Value.Namespace, chain.Value.TypeChain, genericArgs: null);
            }

            case 0x13: // VAR
            case 0x1E: // MVAR
            {
                var gpIdx = (int)(raw.Data & 0xFFFFFFFFUL);
                var gpName = resolveGenericParamName(gpIdx);
                return !string.IsNullOrWhiteSpace(gpName) ? gpName : $"T{gpIdx}";
            }

            case 0x0F: // PTR
            {
                var elem = FormatReferencedType(raw.Data, resolveTypeDefChain, resolveGenericParamName, visitedVas, depth + 1, out _);
                return !string.IsNullOrEmpty(elem) ? $"{elem}*" : null;
            }

            case 0x10: // BYREF
            {
                isByRef = true;
                return FormatReferencedType(raw.Data, resolveTypeDefChain, resolveGenericParamName, visitedVas, depth + 1, out _);
            }

            case 0x1D: // SZARRAY
            {
                var elem = FormatReferencedType(raw.Data, resolveTypeDefChain, resolveGenericParamName, visitedVas, depth + 1, out _);
                return !string.IsNullOrEmpty(elem) ? $"{elem}[]" : null;
            }

            case 0x14: // ARRAY
            {
                if (!ArrayTypesByVa.TryGetValue(raw.Data, out var arr))
                {
                    return null;
                }

                var elem = FormatReferencedType(arr.ElementTypeVa, resolveTypeDefChain, resolveGenericParamName, visitedVas, depth + 1, out _);
                if (string.IsNullOrEmpty(elem))
                {
                    return null;
                }

                var rank = Math.Clamp((int)arr.Rank, 1, 32);
                return rank == 1
                    ? $"{elem}[]"
                    : $"{elem}[{new string(',', rank - 1)}]";
            }

            case 0x15: // GENERICINST
            {
                if (!GenericClassesByVa.TryGetValue(raw.Data, out var genClass))
                {
                    return null;
                }

                var chain = resolveTypeDefChain(genClass.TypeDefIndex);
                if (chain == null || chain.Value.TypeChain.Count == 0)
                {
                    return null;
                }

                var formattedArgs = new string[genClass.ArgTypeVas.Length];
                for (var i = 0; i < genClass.ArgTypeVas.Length; i++)
                {
                    var arg = FormatReferencedType(genClass.ArgTypeVas[i], resolveTypeDefChain, resolveGenericParamName, visitedVas, depth + 1, out _);
                    formattedArgs[i] = !string.IsNullOrEmpty(arg) ? arg : "object";
                }

                return JoinTypeDefChain(chain.Value.Namespace, chain.Value.TypeChain, formattedArgs);
            }

            case 0x1F: // CMOD_REQD
            case 0x20: // CMOD_OPT
            case 0x45: // PINNED
            {
                return FormatReferencedType(raw.Data, resolveTypeDefChain, resolveGenericParamName, visitedVas, depth + 1, out var innerByRef) is { Length: > 0 } unwrapped
                    ? SetByRefIfInner(unwrapped, innerByRef, ref isByRef)
                    : null;
            }

            default:
                return null;
        }
    }

    private static string SetByRefIfInner(string text, bool innerByRef, ref bool isByRef)
    {
        if (innerByRef)
        {
            isByRef = true;
        }
        return text;
    }

    private string? FormatReferencedType(
        ulong va,
        Func<int, (string Namespace, IReadOnlyList<string> TypeChain)?> resolveTypeDefChain,
        Func<int, string?> resolveGenericParamName,
        HashSet<ulong> visitedVas,
        int depth,
        out bool innerByRef)
    {
        innerByRef = false;
        if (va == 0 || depth > MaxTypeResolveDepth || !ExtraTypesByVa.TryGetValue(va, out var raw))
        {
            return null;
        }

        if (!visitedVas.Add(va))
        {
            return null;
        }

        try
        {
            innerByRef = IsByRefBitSet(raw.Bits);
            return FormatRawType(raw, resolveTypeDefChain, resolveGenericParamName, visitedVas, depth, ref innerByRef);
        }
        finally
        {
            visitedVas.Remove(va);
        }
    }

    private static string JoinTypeDefChain(
        string ns,
        IReadOnlyList<string> typeChain,
        string[]? genericArgs)
    {
        if (genericArgs == null || genericArgs.Length == 0)
        {
            var joined = string.Join(".", typeChain);
            return !string.IsNullOrWhiteSpace(ns) ? $"{ns}.{joined}" : joined;
        }

        var arities = new int[typeChain.Count];
        var baseNames = new string[typeChain.Count];
        var totalArity = 0;
        for (var i = 0; i < typeChain.Count; i++)
        {
            var seg = typeChain[i];
            var tick = seg.IndexOf('`');
            if (tick > 0 && tick + 1 < seg.Length && int.TryParse(seg.AsSpan(tick + 1), out var parsedArity) && parsedArity > 0)
            {
                baseNames[i] = seg.Substring(0, tick);
                arities[i] = parsedArity;
                totalArity += parsedArity;
            }
            else
            {
                baseNames[i] = tick > 0 ? seg.Substring(0, tick) : seg;
                arities[i] = 0;
            }
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ns))
        {
            sb.Append(ns);
            sb.Append('.');
        }

        if (totalArity == genericArgs.Length)
        {
            var argCursor = 0;
            for (var i = 0; i < baseNames.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('.');
                }
                sb.Append(baseNames[i]);
                if (arities[i] > 0)
                {
                    sb.Append('<');
                    for (var a = 0; a < arities[i]; a++)
                    {
                        if (a > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(genericArgs[argCursor++]);
                    }
                    sb.Append('>');
                }
            }
        }
        else
        {
            for (var i = 0; i < baseNames.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('.');
                }
                sb.Append(baseNames[i]);
            }

            sb.Append('<');
            sb.Append(string.Join(", ", genericArgs));
            sb.Append('>');
        }

        return sb.ToString();
    }

    private static int ReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination.Slice(total));
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}

/// <summary>
/// Pure-C# reader for native IL2CPP binaries (<c>GameAssembly.dll</c> PE64/PE32 and
/// <c>libil2cpp.so</c> ELF64/ELF32). Structurally locates <c>Il2CppMetadataRegistration</c>
/// and <c>Il2CppCodeRegistration</c> / <c>Il2CppCodeGenModule</c> tables to resolve full
/// <c>Il2CppType</c> signatures, field attributes, field memory offsets, and method RVAs
/// without requiring any external packages or holding large file streams open.
/// </summary>
public sealed class Il2CppBinaryReader
{
    private const int MaxCachedBinaries = 2;
    private const int MaxCachedFailures = 4;
    private const ulong MaxBufferedSectionBytes = 256UL * 1024 * 1024;
    private const ulong MaxTotalBufferedBytes = 512UL * 1024 * 1024;
    private const int MaxTypeGraphDepth = 16;

    private sealed class CachedFailure
    {
        public required long FileLength { get; init; }
        public required long LastWriteTicks { get; init; }
        public required int TotalTypes { get; init; }
        public required int MetadataVersion { get; init; }
        public long LastAccessTick { get; set; }
    }

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Il2CppBinaryIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedFailure> _failureCache = new(StringComparer.OrdinalIgnoreCase);
    private long _accessCounter;

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _indexCache.Clear();
            _failureCache.Clear();
        }
    }

    public Il2CppBinaryIndex? TryLoad(
        string? binaryPath,
        Il2CppBinaryMetadataContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(binaryPath) || context.TotalTypes <= 0 || !File.Exists(binaryPath))
        {
            return null;
        }

        string fullPath;
        FileInfo info;
        try
        {
            fullPath = Path.GetFullPath(binaryPath);
            info = new FileInfo(fullPath);
            if (!info.Exists || info.Length < 64)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var tick = Interlocked.Increment(ref _accessCounter);
        lock (_cacheLock)
        {
            if (_indexCache.TryGetValue(fullPath, out var cached)
                && cached.FileLength == info.Length
                && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                && cached.TotalTypes == context.TotalTypes
                && cached.MetadataVersion == context.MetadataVersion)
            {
                cached.LastAccessTick = tick;
                return cached;
            }

            if (_failureCache.TryGetValue(fullPath, out var failed)
                && failed.FileLength == info.Length
                && failed.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                && failed.TotalTypes == context.TotalTypes
                && failed.MetadataVersion == context.MetadataVersion)
            {
                failed.LastAccessTick = tick;
                return null;
            }
        }

        Il2CppBinaryIndex? built;
        try
        {
            built = BuildBinaryIndex(fullPath, info.Length, info.LastWriteTimeUtc.Ticks, tick, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            built = null;
        }

        lock (_cacheLock)
        {
            if (built != null)
            {
                _failureCache.Remove(fullPath);
                _indexCache[fullPath] = built;
                EvictIfNeededLocked();
            }
            else
            {
                _indexCache.Remove(fullPath);
                _failureCache[fullPath] = new CachedFailure
                {
                    FileLength = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    TotalTypes = context.TotalTypes,
                    MetadataVersion = context.MetadataVersion,
                    LastAccessTick = tick
                };
                EvictIfNeededLocked();
            }
        }

        return built;
    }

    private void EvictIfNeededLocked()
    {
        while (_indexCache.Count > MaxCachedBinaries)
        {
            string? oldestKey = null;
            var oldestTick = long.MaxValue;
            foreach (var pair in _indexCache)
            {
                if (pair.Value.LastAccessTick < oldestTick)
                {
                    oldestTick = pair.Value.LastAccessTick;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey == null)
            {
                break;
            }
            _indexCache.Remove(oldestKey);
        }

        while (_failureCache.Count > MaxCachedFailures)
        {
            string? oldestKey = null;
            var oldestTick = long.MaxValue;
            foreach (var pair in _failureCache)
            {
                if (pair.Value.LastAccessTick < oldestTick)
                {
                    oldestTick = pair.Value.LastAccessTick;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey == null)
            {
                break;
            }
            _failureCache.Remove(oldestKey);
        }
    }

    private sealed class BufferedSegment
    {
        public required ulong VirtualAddress { get; init; }
        public required ulong VirtualSize { get; init; }
        public required ulong FileOffset { get; init; }
        public required ulong RawSize { get; init; }
        public required bool IsExecutable { get; init; }
        public required bool IsWritable { get; init; }
        public required string Name { get; init; }
        public byte[]? Data { get; set; }

        public ulong MappedSize => Math.Max(VirtualSize, RawSize);
    }

    private sealed class BinaryImageView
    {
        public required FileStream Stream { get; init; }
        public required long FileLength { get; init; }
        public required ulong ImageBase { get; init; }
        public required int PointerSize { get; init; }
        public required bool IsElf { get; init; }
        public required bool IsArm32 { get; init; }
        public required List<BufferedSegment> Segments { get; init; }
        public Dictionary<ulong, ulong>? RelocFallback { get; set; }

        public bool IsValidMappedVa(ulong va, ulong length = 1)
        {
            if (va == 0 || length == 0)
            {
                return false;
            }

            for (var i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (va >= seg.VirtualAddress && va - seg.VirtualAddress + length <= seg.MappedSize)
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsValidCodeVa(ulong va)
        {
            if (va == 0)
            {
                return false;
            }

            var effectiveVa = IsArm32 ? (va & ~1UL) : va;
            for (var i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (seg.IsExecutable
                    && effectiveVa >= seg.VirtualAddress
                    && effectiveVa < seg.VirtualAddress + seg.MappedSize)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryVaToFileOffset(ulong va, out long fileOffset)
        {
            fileOffset = -1;
            if (va == 0)
            {
                return false;
            }

            for (var i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (va >= seg.VirtualAddress && va < seg.VirtualAddress + seg.RawSize)
                {
                    var fo = seg.FileOffset + (va - seg.VirtualAddress);
                    if (fo < (ulong)FileLength)
                    {
                        fileOffset = (long)fo;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool TryReadBytesAtVa(ulong va, Span<byte> destination)
        {
            if (va == 0 || destination.IsEmpty)
            {
                return false;
            }

            for (var i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (va < seg.VirtualAddress || va - seg.VirtualAddress + (ulong)destination.Length > seg.MappedSize)
                {
                    continue;
                }

                var rel = va - seg.VirtualAddress;
                if (seg.Data != null)
                {
                    if (rel + (ulong)destination.Length <= (ulong)seg.Data.Length)
                    {
                        seg.Data.AsSpan((int)rel, destination.Length).CopyTo(destination);
                        return true;
                    }
                    return false;
                }

                if (rel < seg.RawSize)
                {
                    var avail = (int)Math.Min((ulong)destination.Length, seg.RawSize - rel);
                    var fileOff = (long)(seg.FileOffset + rel);
                    if (fileOff < 0 || fileOff + avail > FileLength)
                    {
                        return false;
                    }

                    Stream.Position = fileOff;
                    if (ReadExact(Stream, destination.Slice(0, avail)) != avail)
                    {
                        return false;
                    }

                    if (avail < destination.Length)
                    {
                        destination.Slice(avail).Clear();
                    }
                    return true;
                }

                destination.Clear();
                return true;
            }

            return false;
        }

        public bool TryReadPointerAtVa(ulong va, out ulong value)
        {
            if (RelocFallback != null && RelocFallback.TryGetValue(va, out value))
            {
                return true;
            }

            Span<byte> buf = stackalloc byte[8];
            var slice = buf.Slice(0, PointerSize);
            if (!TryReadBytesAtVa(va, slice))
            {
                value = 0;
                return false;
            }

            value = PointerSize == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(slice)
                : BinaryPrimitives.ReadUInt32LittleEndian(slice);
            return true;
        }

        public bool TryReadUInt32AtVa(ulong va, out uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            if (!TryReadBytesAtVa(va, buf))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            return true;
        }

        public bool TryReadByteAtVa(ulong va, out byte value)
        {
            Span<byte> buf = stackalloc byte[1];
            if (!TryReadBytesAtVa(va, buf))
            {
                value = 0;
                return false;
            }

            value = buf[0];
            return true;
        }

        public bool TryReadRawIl2CppType(ulong va, out Il2CppBinaryIndex.RawIl2CppType raw)
        {
            raw = default;
            if (!TryReadPointerAtVa(va, out var data))
            {
                return false;
            }

            if (!TryReadUInt32AtVa(va + (ulong)PointerSize, out var bits))
            {
                return false;
            }

            raw = new Il2CppBinaryIndex.RawIl2CppType(data, bits);
            return true;
        }

        public string? TryReadCStringAtVa(ulong va, int maxLen = 256)
        {
            if (va == 0 || maxLen <= 0)
            {
                return null;
            }

            for (var i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (va < seg.VirtualAddress || va >= seg.VirtualAddress + seg.MappedSize)
                {
                    continue;
                }

                var rel = va - seg.VirtualAddress;
                var avail = (int)Math.Min((ulong)maxLen, seg.MappedSize - rel);
                if (avail <= 0)
                {
                    return null;
                }

                if (seg.Data != null && rel < (ulong)seg.Data.Length)
                {
                    var lenLimit = (int)Math.Min((ulong)avail, (ulong)seg.Data.Length - rel);
                    var span = seg.Data.AsSpan((int)rel, lenLimit);
                    var zeroIdx = span.IndexOf((byte)0);
                    if (zeroIdx <= 0)
                    {
                        return null;
                    }
                    return Encoding.UTF8.GetString(span.Slice(0, zeroIdx));
                }

                if (rel < seg.RawSize)
                {
                    var readLen = (int)Math.Min((ulong)avail, seg.RawSize - rel);
                    var fileOff = (long)(seg.FileOffset + rel);
                    if (fileOff < 0 || fileOff + readLen > FileLength)
                    {
                        return null;
                    }

                    var buf = new byte[readLen];
                    Stream.Position = fileOff;
                    var got = ReadExact(Stream, buf);
                    if (got <= 0)
                    {
                        return null;
                    }

                    var zeroIdx = Array.IndexOf(buf, (byte)0, 0, got);
                    if (zeroIdx <= 0)
                    {
                        return null;
                    }
                    return Encoding.UTF8.GetString(buf, 0, zeroIdx);
                }

                return null;
            }

            return null;
        }

        public ulong NormalizeMethodRva(ulong rawPtr)
        {
            if (rawPtr == 0)
            {
                return 0;
            }

            var effectiveVa = IsArm32 ? (rawPtr & ~1UL) : rawPtr;
            if (effectiveVa < ImageBase || !IsValidMappedVa(effectiveVa))
            {
                return 0;
            }

            return effectiveVa - ImageBase;
        }
    }

    private static Il2CppBinaryIndex? BuildBinaryIndex(
        string fullPath,
        long fileLength,
        long lastWriteTicks,
        long accessTick,
        Il2CppBinaryMetadataContext context,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);

        Span<byte> magic = stackalloc byte[4];
        if (ReadExact(stream, magic) != 4)
        {
            return null;
        }

        stream.Position = 0;

        BinaryImageView? view;
        if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
        {
            view = TryOpenPeView(stream, fileLength, cancellationToken);
        }
        else if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
        {
            view = TryOpenElfView(stream, fileLength, cancellationToken);
        }
        else
        {
            return null;
        }

        if (view == null || view.Segments.Count == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var foundMetaReg = TryFindMetadataRegistration(
            view,
            context,
            cancellationToken,
            out var typesCount,
            out var typesTableVa,
            out var fieldOffsetsTableVa);

        // Il2CppMetadataRegistration is the structural fingerprint proving this binary matches
        // global-metadata.dat (exact totalTypes + System.Int32 Il2CppType validation). Rejecting
        // when absent prevents attaching stale RVAs from a mismatched build.
        if (!foundMetaReg)
        {
            return null;
        }

        var moduleMethodRvas = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase);
        var flatMethodRvas = Array.Empty<ulong>();

        var foundCodeGen = TryFindCodeGenModules(view, context, moduleMethodRvas, cancellationToken);
        if (!foundCodeGen)
        {
            TryFindFlatCodeRegistration(view, context, out flatMethodRvas, cancellationToken);
        }

        var extraTypesByVa = new Dictionary<ulong, Il2CppBinaryIndex.RawIl2CppType>();
        var arrayTypesByVa = new Dictionary<ulong, Il2CppBinaryIndex.RawArrayType>();
        var genericClassesByVa = new Dictionary<ulong, Il2CppBinaryIndex.RawGenericClass>();

        var types = ReadTypesAndBuildGraph(
            view,
            typesCount,
            typesTableVa,
            context,
            extraTypesByVa,
            arrayTypesByVa,
            genericClassesByVa,
            cancellationToken);

        var useV272ByrefBit = DetectByrefBitLayout(types, context);
        var fieldOffsetFileOffsets = ReadFieldOffsetTableFileOffsets(view, fieldOffsetsTableVa, context.TotalTypes, cancellationToken);

        var sections = new Il2CppBinaryIndex.SectionMapping[view.Segments.Count];
        for (var i = 0; i < view.Segments.Count; i++)
        {
            var seg = view.Segments[i];
            sections[i] = new Il2CppBinaryIndex.SectionMapping(
                seg.VirtualAddress,
                seg.VirtualSize,
                seg.FileOffset,
                seg.RawSize,
                seg.IsExecutable);
        }

        return new Il2CppBinaryIndex
        {
            FilePath = fullPath,
            FileLength = fileLength,
            LastWriteTicks = lastWriteTicks,
            MetadataVersion = context.MetadataVersion,
            TotalTypes = context.TotalTypes,
            ImageBase = view.ImageBase,
            PointerSize = view.PointerSize,
            IsElf = view.IsElf,
            UseV272ByrefBit = useV272ByrefBit,
            Sections = sections,
            Types = types,
            ExtraTypesByVa = extraTypesByVa,
            ArrayTypesByVa = arrayTypesByVa,
            GenericClassesByVa = genericClassesByVa,
            FieldOffsetTableFileOffsets = fieldOffsetFileOffsets,
            ModuleMethodRvas = moduleMethodRvas,
            FlatMethodRvas = flatMethodRvas,
            LastAccessTick = accessTick
        };
    }

    private static BinaryImageView? TryOpenPeView(FileStream stream, long fileLength, CancellationToken cancellationToken)
    {
        try
        {
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var peHeaders = peReader.PEHeaders;
            if (peHeaders.PEHeader == null)
            {
                return null;
            }

            var magic = peHeaders.PEHeader.Magic;
            if (magic != PEMagic.PE32 && magic != PEMagic.PE32Plus)
            {
                return null;
            }

            var ptrSize = magic == PEMagic.PE32Plus ? 8 : 4;
            var imageBase = peHeaders.PEHeader.ImageBase;
            var segments = new List<BufferedSegment>(peHeaders.SectionHeaders.Length);
            ulong totalBuffered = 0;

            foreach (var sec in peHeaders.SectionHeaders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawSize = (ulong)(uint)sec.SizeOfRawData;
                var virtSize = (ulong)(uint)sec.VirtualSize;
                if (rawSize == 0 && virtSize == 0)
                {
                    continue;
                }

                var isExec = (sec.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
                var isWrite = (sec.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0;
                var name = sec.Name ?? string.Empty;

                var seg = new BufferedSegment
                {
                    VirtualAddress = imageBase + (ulong)(uint)sec.VirtualAddress,
                    VirtualSize = virtSize > 0 ? virtSize : rawSize,
                    FileOffset = (ulong)(uint)sec.PointerToRawData,
                    RawSize = rawSize,
                    IsExecutable = isExec,
                    IsWritable = isWrite,
                    Name = name
                };

                var skipPreload = isExec
                    || string.Equals(name, ".rsrc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ".reloc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ".pdata", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".debug", StringComparison.OrdinalIgnoreCase);

                if (!skipPreload
                    && rawSize > 0
                    && seg.MappedSize <= MaxBufferedSectionBytes
                    && totalBuffered + seg.MappedSize <= MaxTotalBufferedBytes
                    && seg.FileOffset < (ulong)fileLength)
                {
                    var allocLen = (int)seg.MappedSize;
                    var readLen = (int)Math.Min(rawSize, (ulong)fileLength - seg.FileOffset);
                    var data = new byte[allocLen];
                    stream.Position = (long)seg.FileOffset;
                    if (ReadExact(stream, data.AsSpan(0, readLen)) == readLen)
                    {
                        seg.Data = data;
                        totalBuffered += seg.MappedSize;
                    }
                }

                segments.Add(seg);
            }

            return new BinaryImageView
            {
                Stream = stream,
                FileLength = fileLength,
                ImageBase = imageBase,
                PointerSize = ptrSize,
                IsElf = false,
                IsArm32 = false,
                Segments = segments
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static BinaryImageView? TryOpenElfView(FileStream stream, long fileLength, CancellationToken cancellationToken)
    {
        try
        {
            if (fileLength < 64)
            {
                return null;
            }

            Span<byte> hdr = stackalloc byte[64];
            stream.Position = 0;
            if (ReadExact(stream, hdr) < 52)
            {
                return null;
            }

            var eiClass = hdr[4];
            var eiData = hdr[5];
            if ((eiClass != 1 && eiClass != 2) || eiData != 1)
            {
                return null;
            }

            var is64 = eiClass == 2;
            var ptrSize = is64 ? 8 : 4;
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(18, 2));
            var isArm32 = machine == 40;
            uint relativeRelocType = machine switch
            {
                183 => 1027u, // EM_AARCH64 -> R_AARCH64_RELATIVE
                62 => 8u,     // EM_X86_64 -> R_X86_64_RELATIVE
                40 => 23u,    // EM_ARM -> R_ARM_RELATIVE
                3 => 8u,      // EM_386 -> R_386_RELATIVE
                243 => 3u,    // EM_RISCV -> R_RISCV_RELATIVE
                _ => 0u
            };

            ulong phOff = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(32, 8)) : BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(28, 4));
            ulong shOff = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(40, 8)) : BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(32, 4));
            ushort phEntSize = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(54, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(42, 2));
            ushort phNum = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(56, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(44, 2));
            ushort shEntSize = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(58, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(46, 2));
            ushort shNum = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(60, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(48, 2));

            var minPhEnt = is64 ? 56 : 32;
            if (phNum == 0 || phEntSize < minPhEnt || phOff + (ulong)phNum * phEntSize > (ulong)fileLength)
            {
                return null;
            }

            var phBytes = new byte[phNum * phEntSize];
            stream.Position = (long)phOff;
            if (ReadExact(stream, phBytes) != phBytes.Length)
            {
                return null;
            }

            var segments = new List<BufferedSegment>();
            ulong minVaddr = ulong.MaxValue;
            ulong dynOffset = 0;
            ulong dynSize = 0;
            ulong totalBuffered = 0;

            for (var i = 0; i < phNum; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var off = i * phEntSize;
                var pType = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off, 4));

                uint pFlags;
                ulong pOffset;
                ulong pVaddr;
                ulong pFilesz;
                ulong pMemsz;
                if (is64)
                {
                    pFlags = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off + 4, 4));
                    pOffset = BinaryPrimitives.ReadUInt64LittleEndian(phBytes.AsSpan(off + 8, 8));
                    pVaddr = BinaryPrimitives.ReadUInt64LittleEndian(phBytes.AsSpan(off + 16, 8));
                    pFilesz = BinaryPrimitives.ReadUInt64LittleEndian(phBytes.AsSpan(off + 32, 8));
                    pMemsz = BinaryPrimitives.ReadUInt64LittleEndian(phBytes.AsSpan(off + 40, 8));
                }
                else
                {
                    pOffset = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off + 4, 4));
                    pVaddr = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off + 8, 4));
                    pFilesz = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off + 16, 4));
                    pMemsz = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off + 20, 4));
                    pFlags = BinaryPrimitives.ReadUInt32LittleEndian(phBytes.AsSpan(off + 24, 4));
                }

                if (pType == 2) // PT_DYNAMIC
                {
                    dynOffset = pOffset;
                    dynSize = pFilesz;
                    continue;
                }

                if (pType != 1 || pMemsz == 0) // PT_LOAD
                {
                    continue;
                }

                if (pVaddr < minVaddr)
                {
                    minVaddr = pVaddr;
                }

                var isExec = (pFlags & 1u) != 0;
                var isWrite = (pFlags & 2u) != 0;
                var seg = new BufferedSegment
                {
                    VirtualAddress = pVaddr,
                    VirtualSize = pMemsz,
                    FileOffset = pOffset,
                    RawSize = pFilesz,
                    IsExecutable = isExec,
                    IsWritable = isWrite,
                    Name = isExec ? (isWrite ? "LOAD_RWX" : "LOAD_RX") : (isWrite ? "LOAD_RW" : "LOAD_R")
                };

                // Buffer all non-executable or writable LOAD segments so R_*_RELATIVE relocations
                // can be applied cleanly in-memory without keeping 100MB+ executable code in RAM.
                if ((!isExec || isWrite)
                    && seg.MappedSize <= MaxBufferedSectionBytes
                    && totalBuffered + seg.MappedSize <= MaxTotalBufferedBytes
                    && pOffset <= (ulong)fileLength)
                {
                    var allocLen = (int)seg.MappedSize;
                    var readLen = (int)Math.Min(pFilesz, (ulong)fileLength - pOffset);
                    var data = new byte[allocLen];
                    if (readLen > 0)
                    {
                        stream.Position = (long)pOffset;
                        if (ReadExact(stream, data.AsSpan(0, readLen)) != readLen)
                        {
                            data = null;
                        }
                    }

                    if (data != null)
                    {
                        seg.Data = data;
                        totalBuffered += seg.MappedSize;
                    }
                }

                segments.Add(seg);
            }

            if (segments.Count == 0)
            {
                return null;
            }

            var view = new BinaryImageView
            {
                Stream = stream,
                FileLength = fileLength,
                ImageBase = minVaddr == ulong.MaxValue ? 0 : minVaddr,
                PointerSize = ptrSize,
                IsElf = true,
                IsArm32 = isArm32,
                Segments = segments
            };

            if (relativeRelocType != 0)
            {
                ApplyElfRelocations(
                    view,
                    is64,
                    relativeRelocType,
                    shOff,
                    shEntSize,
                    shNum,
                    dynOffset,
                    dynSize,
                    cancellationToken);
            }

            return view;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyElfRelocations(
        BinaryImageView view,
        bool is64,
        uint relativeRelocType,
        ulong shOff,
        ushort shEntSize,
        ushort shNum,
        ulong dynOffset,
        ulong dynSize,
        CancellationToken cancellationToken)
    {
        var appliedFromSections = false;
        var minShEnt = is64 ? 64 : 40;

        if (shOff > 0 && shNum > 0 && shEntSize >= minShEnt && shOff + (ulong)shNum * shEntSize <= (ulong)view.FileLength)
        {
            var shBytes = new byte[shNum * shEntSize];
            view.Stream.Position = (long)shOff;
            if (ReadExact(view.Stream, shBytes) == shBytes.Length)
            {
                for (var i = 0; i < shNum; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var off = i * shEntSize;
                    var shType = BinaryPrimitives.ReadUInt32LittleEndian(shBytes.AsSpan(off + 4, 4));

                    // SHT_RELA = 4, SHT_REL = 9, SHT_RELR = 19, SHT_ANDROID_RELR = 0x6FFFFF00
                    if (shType != 4 && shType != 9 && shType != 19 && shType != 0x6FFFFF00u)
                    {
                        continue;
                    }

                    ulong secFileOff = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(shBytes.AsSpan(off + 24, 8)) : BinaryPrimitives.ReadUInt32LittleEndian(shBytes.AsSpan(off + 16, 4));
                    ulong secSize = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(shBytes.AsSpan(off + 32, 8)) : BinaryPrimitives.ReadUInt32LittleEndian(shBytes.AsSpan(off + 20, 4));
                    ulong secEntSize = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(shBytes.AsSpan(off + 56, 8)) : BinaryPrimitives.ReadUInt32LittleEndian(shBytes.AsSpan(off + 36, 4));

                    if (shType == 4 && secSize > 0)
                    {
                        var entSize = secEntSize >= (ulong)(is64 ? 24 : 12) ? (int)secEntSize : (is64 ? 24 : 12);
                        if (ApplyElfRelaTable(view, is64, relativeRelocType, secFileOff, secSize, entSize, cancellationToken))
                        {
                            appliedFromSections = true;
                        }
                    }
                    else if (shType == 9 || shType == 19 || shType == 0x6FFFFF00u)
                    {
                        // REL and RELR use in-place addends; with link base B = 0, values are already link-time VAs.
                        appliedFromSections = true;
                    }
                }
            }
        }

        // Fallback to PT_DYNAMIC when section headers are stripped or yielded no relocation table
        if (!appliedFromSections && dynOffset > 0 && dynSize > 0 && dynOffset + dynSize <= (ulong)view.FileLength)
        {
            var dynEntrySize = is64 ? 16 : 8;
            var readSize = (int)Math.Min(dynSize, 1024UL * 1024);
            var dynBuf = new byte[readSize];
            view.Stream.Position = (long)dynOffset;
            if (ReadExact(view.Stream, dynBuf) == readSize)
            {
                ulong relaVa = 0;
                ulong relaSz = 0;
                ulong relaEnt = (ulong)(is64 ? 24 : 12);

                for (var pos = 0; pos + dynEntrySize <= readSize; pos += dynEntrySize)
                {
                    long tag = is64
                        ? BinaryPrimitives.ReadInt64LittleEndian(dynBuf.AsSpan(pos, 8))
                        : BinaryPrimitives.ReadInt32LittleEndian(dynBuf.AsSpan(pos, 4));
                    ulong val = is64
                        ? BinaryPrimitives.ReadUInt64LittleEndian(dynBuf.AsSpan(pos + 8, 8))
                        : BinaryPrimitives.ReadUInt32LittleEndian(dynBuf.AsSpan(pos + 4, 4));

                    if (tag == 0) // DT_NULL
                    {
                        break;
                    }

                    switch (tag)
                    {
                        case 7: relaVa = val; break;     // DT_RELA
                        case 8: relaSz = val; break;     // DT_RELASZ
                        case 9: if (val >= (ulong)(is64 ? 24 : 12)) relaEnt = val; break; // DT_RELAENT
                    }
                }

                if (relaVa != 0 && relaSz > 0 && view.TryVaToFileOffset(relaVa, out var relaFileOff))
                {
                    ApplyElfRelaTable(view, is64, relativeRelocType, (ulong)relaFileOff, relaSz, (int)relaEnt, cancellationToken);
                }
            }
        }
    }

    private static bool ApplyElfRelaTable(
        BinaryImageView view,
        bool is64,
        uint relativeRelocType,
        ulong tableFileOffset,
        ulong tableSize,
        int entrySize,
        CancellationToken cancellationToken)
    {
        if (entrySize <= 0 || tableSize == 0 || tableFileOffset + tableSize > (ulong)view.FileLength)
        {
            return false;
        }

        const int chunkBytes = 64 * 1024;
        var entriesPerChunk = Math.Max(1, chunkBytes / entrySize);
        var buf = new byte[entriesPerChunk * entrySize];
        var remaining = tableSize;
        var curOff = tableFileOffset;
        var appliedAny = false;

        while (remaining >= (ulong)entrySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(remaining, (ulong)buf.Length);
            toRead -= toRead % entrySize;
            if (toRead <= 0)
            {
                break;
            }

            view.Stream.Position = (long)curOff;
            if (ReadExact(view.Stream, buf.AsSpan(0, toRead)) != toRead)
            {
                break;
            }

            for (var pos = 0; pos + entrySize <= toRead; pos += entrySize)
            {
                ulong rOffset;
                uint rType;
                ulong rAddend;

                if (is64)
                {
                    rOffset = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(pos, 8));
                    var rInfo = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(pos + 8, 8));
                    rType = (uint)(rInfo & 0xFFFFFFFFUL);
                    rAddend = (ulong)BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(pos + 16, 8));
                }
                else
                {
                    rOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
                    var rInfo = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 4, 4));
                    rType = rInfo & 0xFFu;
                    rAddend = unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos + 8, 4)));
                }

                if (rType != relativeRelocType)
                {
                    continue;
                }

                WriteRelocatedPointer(view, rOffset, rAddend);
                appliedAny = true;
            }

            curOff += (ulong)toRead;
            remaining -= (ulong)toRead;
        }

        return appliedAny;
    }

    private static void WriteRelocatedPointer(BinaryImageView view, ulong targetVa, ulong value)
    {
        for (var i = 0; i < view.Segments.Count; i++)
        {
            var seg = view.Segments[i];
            if (seg.Data == null
                || targetVa < seg.VirtualAddress
                || targetVa - seg.VirtualAddress + (ulong)view.PointerSize > (ulong)seg.Data.Length)
            {
                continue;
            }

            var rel = (int)(targetVa - seg.VirtualAddress);
            if (view.PointerSize == 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(seg.Data.AsSpan(rel, 8), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(seg.Data.AsSpan(rel, 4), (uint)value);
            }
            return;
        }

        view.RelocFallback ??= new Dictionary<ulong, ulong>();
        view.RelocFallback[targetVa] = value;
    }

    private static bool TryFindMetadataRegistration(
        BinaryImageView view,
        Il2CppBinaryMetadataContext context,
        CancellationToken cancellationToken,
        out int typesCount,
        out ulong typesTableVa,
        out ulong fieldOffsetsTableVa)
    {
        typesCount = 0;
        typesTableVa = 0;
        fieldOffsetsTableVa = 0;

        var ptrSize = view.PointerSize;
        var structBytes = 16 * ptrSize;
        var totalTypes = context.TotalTypes;

        // First scan buffered non-executable data segments, then fall back to any remaining buffered segments
        for (var pass = 0; pass < 2; pass++)
        {
            for (var s = 0; s < view.Segments.Count; s++)
            {
                var seg = view.Segments[s];
                if (seg.Data == null || seg.Data.Length < structBytes)
                {
                    continue;
                }

                if (pass == 0 && seg.IsExecutable)
                {
                    continue;
                }
                if (pass == 1 && !seg.IsExecutable)
                {
                    continue;
                }

                var data = seg.Data;
                var limit = data.Length - structBytes;
                for (var off = 0; off <= limit; off += ptrSize)
                {
                    if ((off & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // Fast filter: word 10 == fieldOffsetsCount == totalTypes, word 12 == typeDefinitionsSizesCount == totalTypes
                    if (ReadCountWord(data, off + 10 * ptrSize, ptrSize) != totalTypes)
                    {
                        continue;
                    }

                    if (ReadCountWord(data, off + 12 * ptrSize, ptrSize) != totalTypes)
                    {
                        continue;
                    }

                    var candTypesCount = ReadCountWord(data, off + 6 * ptrSize, ptrSize);
                    if (candTypesCount < totalTypes || candTypesCount > 20_000_000)
                    {
                        continue;
                    }

                    if (context.Int32ByvalTypeIndex >= 0 && candTypesCount <= context.Int32ByvalTypeIndex)
                    {
                        continue;
                    }

                    var candTypesVa = ReadPtrWord(data, off + 7 * ptrSize, ptrSize);
                    var candFieldOffsetsVa = ReadPtrWord(data, off + 11 * ptrSize, ptrSize);
                    var candTypeDefSizesVa = ReadPtrWord(data, off + 13 * ptrSize, ptrSize);

                    if (!view.IsValidMappedVa(candTypesVa, (ulong)candTypesCount * (ulong)ptrSize)
                        || !view.IsValidMappedVa(candFieldOffsetsVa, (ulong)totalTypes * (ulong)ptrSize)
                        || !view.IsValidMappedVa(candTypeDefSizesVa, (ulong)totalTypes * (ulong)ptrSize))
                    {
                        continue;
                    }

                    var genClassesCount = ReadCountWord(data, off + 0 * ptrSize, ptrSize);
                    var genClassesVa = ReadPtrWord(data, off + 1 * ptrSize, ptrSize);
                    var genInstsCount = ReadCountWord(data, off + 2 * ptrSize, ptrSize);
                    var genInstsVa = ReadPtrWord(data, off + 3 * ptrSize, ptrSize);
                    if (genClassesCount < 0 || genClassesCount > 10_000_000 || (genClassesCount > 0 && !view.IsValidMappedVa(genClassesVa, (ulong)ptrSize)))
                    {
                        continue;
                    }
                    if (genInstsCount < 0 || genInstsCount > 10_000_000 || (genInstsCount > 0 && !view.IsValidMappedVa(genInstsVa, (ulong)ptrSize)))
                    {
                        continue;
                    }

                    if (!ValidateMetadataRegistrationTypes(view, candTypesVa, candTypesCount, candFieldOffsetsVa, context))
                    {
                        continue;
                    }

                    typesCount = candTypesCount;
                    typesTableVa = candTypesVa;
                    fieldOffsetsTableVa = candFieldOffsetsVa;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ValidateMetadataRegistrationTypes(
        BinaryImageView view,
        ulong typesTableVa,
        int typesCount,
        ulong fieldOffsetsTableVa,
        Il2CppBinaryMetadataContext context)
    {
        var ptrSize = (ulong)view.PointerSize;

        bool CheckKnownType(int typeIndex, byte expectedPrimary, byte expectedFallback)
        {
            if (typeIndex < 0 || typeIndex >= typesCount)
            {
                return true;
            }

            if (!view.TryReadPointerAtVa(typesTableVa + (ulong)typeIndex * ptrSize, out var typeVa)
                || typeVa == 0
                || !view.TryReadRawIl2CppType(typeVa, out var raw))
            {
                return false;
            }

            return raw.TypeEnum == expectedPrimary || raw.TypeEnum == expectedFallback;
        }

        if (!CheckKnownType(context.Int32ByvalTypeIndex, 0x08, 0x11))
        {
            return false;
        }

        if (!CheckKnownType(context.ObjectByvalTypeIndex, 0x1C, 0x12))
        {
            return false;
        }

        if (!CheckKnownType(context.StringByvalTypeIndex, 0x0E, 0x12))
        {
            return false;
        }

        if (!CheckKnownType(context.VoidByvalTypeIndex, 0x01, 0x11))
        {
            return false;
        }

        // Ensure fieldOffsets entries are either NULL or valid mapped VAs
        var sampleFo = Math.Min(context.TotalTypes, 16);
        for (var i = 0; i < sampleFo; i++)
        {
            if (!view.TryReadPointerAtVa(fieldOffsetsTableVa + (ulong)i * ptrSize, out var foPtr))
            {
                return false;
            }

            if (foPtr != 0 && !view.IsValidMappedVa(foPtr, 4))
            {
                return false;
            }
        }

        // Spot-check at least one readable entry in types[]
        var checkedAny = false;
        var probeCount = Math.Min(typesCount, 16);
        for (var i = 0; i < probeCount; i++)
        {
            if (!view.TryReadPointerAtVa(typesTableVa + (ulong)i * ptrSize, out var tPtr))
            {
                return false;
            }

            if (tPtr == 0)
            {
                continue;
            }

            if (!view.TryReadRawIl2CppType(tPtr, out var raw) || raw.TypeEnum == 0 || raw.TypeEnum > 0x55)
            {
                return false;
            }

            checkedAny = true;
        }

        return checkedAny || context.Int32ByvalTypeIndex >= 0;
    }

    private static bool TryFindCodeGenModules(
        BinaryImageView view,
        Il2CppBinaryMetadataContext context,
        Dictionary<string, ulong[]> moduleMethodRvas,
        CancellationToken cancellationToken)
    {
        var imageCount = context.ImageNames.Count;
        if (imageCount <= 0)
        {
            return false;
        }

        var knownImages = new HashSet<string>(context.ImageNames, StringComparer.OrdinalIgnoreCase);
        var ptrSize = view.PointerSize;

        for (var pass = 0; pass < 2; pass++)
        {
            for (var s = 0; s < view.Segments.Count; s++)
            {
                var seg = view.Segments[s];
                if (seg.Data == null || seg.Data.Length < ptrSize * 2)
                {
                    continue;
                }

                if (pass == 0 && seg.IsExecutable)
                {
                    continue;
                }
                if (pass == 1 && !seg.IsExecutable)
                {
                    continue;
                }

                var data = seg.Data;
                var limit = data.Length - ptrSize * 2;
                for (var off = 0; off <= limit; off += ptrSize)
                {
                    if ((off & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var candCount = ReadCountWord(data, off, ptrSize);
                    if (candCount != imageCount && candCount != imageCount + 1)
                    {
                        continue;
                    }

                    var modulesArrayVa = ReadPtrWord(data, off + ptrSize, ptrSize);
                    if (!view.IsValidMappedVa(modulesArrayVa, (ulong)candCount * (ulong)ptrSize))
                    {
                        continue;
                    }

                    if (!ValidateAndExtractCodeGenModules(view, modulesArrayVa, candCount, knownImages, moduleMethodRvas, cancellationToken))
                    {
                        moduleMethodRvas.Clear();
                        continue;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private static bool ValidateAndExtractCodeGenModules(
        BinaryImageView view,
        ulong modulesArrayVa,
        int moduleCount,
        HashSet<string> knownImages,
        Dictionary<string, ulong[]> moduleMethodRvas,
        CancellationToken cancellationToken)
    {
        var ptrSize = (ulong)view.PointerSize;
        var probeCount = Math.Min(moduleCount, 8);
        var matchedKnown = 0;

        for (var i = 0; i < probeCount; i++)
        {
            if (!view.TryReadPointerAtVa(modulesArrayVa + (ulong)i * ptrSize, out var modVa)
                || !view.IsValidMappedVa(modVa, ptrSize * 3))
            {
                return false;
            }

            if (!view.TryReadPointerAtVa(modVa, out var nameVa))
            {
                return false;
            }

            var modName = view.TryReadCStringAtVa(nameVa, 256);
            if (string.IsNullOrWhiteSpace(modName))
            {
                return false;
            }

            if (knownImages.Contains(modName))
            {
                matchedKnown++;
            }
            else if (!string.Equals(modName, "__Generated", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!view.TryReadUInt32AtVa(modVa + ptrSize, out var methodPtrCount) || methodPtrCount > 5_000_000)
            {
                return false;
            }

            if (methodPtrCount > 0)
            {
                if (!view.TryReadPointerAtVa(modVa + 2 * ptrSize, out var methodPtrsVa)
                    || !view.IsValidMappedVa(methodPtrsVa, (ulong)methodPtrCount * ptrSize))
                {
                    return false;
                }
            }
        }

        if (matchedKnown == 0)
        {
            return false;
        }

        // Extract all module method RVA tables
        for (var i = 0; i < moduleCount; i++)
        {
            if ((i & 0x1F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!view.TryReadPointerAtVa(modulesArrayVa + (ulong)i * ptrSize, out var modVa)
                || !view.TryReadPointerAtVa(modVa, out var nameVa))
            {
                return false;
            }

            var modName = view.TryReadCStringAtVa(nameVa, 256);
            if (string.IsNullOrWhiteSpace(modName))
            {
                return false;
            }

            if (!view.TryReadUInt32AtVa(modVa + ptrSize, out var methodPtrCount) || methodPtrCount > 5_000_000)
            {
                return false;
            }

            if (methodPtrCount == 0)
            {
                moduleMethodRvas[modName] = Array.Empty<ulong>();
                continue;
            }

            if (!view.TryReadPointerAtVa(modVa + 2 * ptrSize, out var methodPtrsVa)
                || !view.IsValidMappedVa(methodPtrsVa, (ulong)methodPtrCount * ptrSize))
            {
                return false;
            }

            var rawBytes = new byte[methodPtrCount * (int)ptrSize];
            if (!view.TryReadBytesAtVa(methodPtrsVa, rawBytes))
            {
                return false;
            }

            var rvas = new ulong[methodPtrCount];
            for (var m = 0; m < (int)methodPtrCount; m++)
            {
                var rawPtr = ReadPtrWord(rawBytes, m * (int)ptrSize, (int)ptrSize);
                if (rawPtr == 0 && view.RelocFallback != null
                    && view.RelocFallback.TryGetValue(methodPtrsVa + (ulong)m * ptrSize, out var relocPtr))
                {
                    rawPtr = relocPtr;
                }

                rvas[m] = view.NormalizeMethodRva(rawPtr);
            }

            moduleMethodRvas[modName] = rvas;
        }

        return moduleMethodRvas.Count > 0;
    }

    private static bool TryFindFlatCodeRegistration(
        BinaryImageView view,
        Il2CppBinaryMetadataContext context,
        out ulong[] flatMethodRvas,
        CancellationToken cancellationToken)
    {
        flatMethodRvas = Array.Empty<ulong>();
        if (context.TotalMethods <= 0 || context.MetadataVersion >= 27)
        {
            return false;
        }

        var ptrSize = view.PointerSize;
        var minStructBytes = 10 * ptrSize;
        var totalMethods = context.TotalMethods;

        for (var s = 0; s < view.Segments.Count; s++)
        {
            var seg = view.Segments[s];
            if (seg.Data == null || seg.IsExecutable || seg.Data.Length < minStructBytes)
            {
                continue;
            }

            var data = seg.Data;
            var limit = data.Length - minStructBytes;
            for (var off = 0; off <= limit; off += ptrSize)
            {
                if ((off & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var methodCount = ReadCountWord(data, off, ptrSize);
                if (methodCount <= 0 || methodCount > totalMethods + 64)
                {
                    continue;
                }

                var methodPtrsVa = ReadPtrWord(data, off + ptrSize, ptrSize);
                if (!view.IsValidMappedVa(methodPtrsVa, (ulong)methodCount * (ulong)ptrSize))
                {
                    continue;
                }

                var revPinvokeCount = ReadCountWord(data, off + 2 * ptrSize, ptrSize);
                var revPinvokeVa = ReadPtrWord(data, off + 3 * ptrSize, ptrSize);
                var genMethodCount = ReadCountWord(data, off + 4 * ptrSize, ptrSize);
                var genMethodVa = ReadPtrWord(data, off + 5 * ptrSize, ptrSize);
                var invokerCount = ReadCountWord(data, off + 6 * ptrSize, ptrSize);
                var invokerVa = ReadPtrWord(data, off + 7 * ptrSize, ptrSize);

                if (revPinvokeCount < 0 || revPinvokeCount > 500_000
                    || (revPinvokeCount > 0 && !view.IsValidMappedVa(revPinvokeVa, (ulong)ptrSize))
                    || genMethodCount < 0 || genMethodCount > 5_000_000
                    || (genMethodCount > 0 && !view.IsValidMappedVa(genMethodVa, (ulong)ptrSize))
                    || invokerCount <= 0 || invokerCount > 5_000_000
                    || !view.IsValidMappedVa(invokerVa, (ulong)invokerCount * (ulong)ptrSize))
                {
                    continue;
                }

                // Validate sample method pointers point to executable code
                var probe = Math.Min(methodCount, 16);
                var validCodeHits = 0;
                var valid = true;
                for (var i = 0; i < probe; i++)
                {
                    if (!view.TryReadPointerAtVa(methodPtrsVa + (ulong)i * (ulong)ptrSize, out var mPtr))
                    {
                        valid = false;
                        break;
                    }

                    if (mPtr == 0)
                    {
                        continue;
                    }

                    if (!view.IsValidCodeVa(mPtr))
                    {
                        valid = false;
                        break;
                    }

                    validCodeHits++;
                }

                if (!valid || validCodeHits == 0)
                {
                    continue;
                }

                var rawBytes = new byte[methodCount * ptrSize];
                if (!view.TryReadBytesAtVa(methodPtrsVa, rawBytes))
                {
                    continue;
                }

                var rvas = new ulong[methodCount];
                for (var i = 0; i < methodCount; i++)
                {
                    var rawPtr = ReadPtrWord(rawBytes, i * ptrSize, ptrSize);
                    rvas[i] = view.NormalizeMethodRva(rawPtr);
                }

                flatMethodRvas = rvas;
                return true;
            }
        }

        return false;
    }

    private static Il2CppBinaryIndex.RawIl2CppType[] ReadTypesAndBuildGraph(
        BinaryImageView view,
        int typesCount,
        ulong typesTableVa,
        Il2CppBinaryMetadataContext context,
        Dictionary<ulong, Il2CppBinaryIndex.RawIl2CppType> extraTypesByVa,
        Dictionary<ulong, Il2CppBinaryIndex.RawArrayType> arrayTypesByVa,
        Dictionary<ulong, Il2CppBinaryIndex.RawGenericClass> genericClassesByVa,
        CancellationToken cancellationToken)
    {
        var ptrSize = view.PointerSize;
        var types = new Il2CppBinaryIndex.RawIl2CppType[typesCount];
        var tableBytes = new byte[(long)typesCount * ptrSize];
        if (!view.TryReadBytesAtVa(typesTableVa, tableBytes))
        {
            return Array.Empty<Il2CppBinaryIndex.RawIl2CppType>();
        }

        for (var i = 0; i < typesCount; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var typeVa = ReadPtrWord(tableBytes, i * ptrSize, ptrSize);
            if (typeVa == 0 && view.RelocFallback != null
                && view.RelocFallback.TryGetValue(typesTableVa + (ulong)i * (ulong)ptrSize, out var relocVa))
            {
                typeVa = relocVa;
            }

            if (typeVa == 0 || !view.TryReadRawIl2CppType(typeVa, out var raw))
            {
                continue;
            }

            types[i] = raw;
            CollectTypeGraph(view, raw, context, extraTypesByVa, arrayTypesByVa, genericClassesByVa, depth: 0);
        }

        return types;
    }

    private static void CollectTypeGraph(
        BinaryImageView view,
        Il2CppBinaryIndex.RawIl2CppType raw,
        Il2CppBinaryMetadataContext context,
        Dictionary<ulong, Il2CppBinaryIndex.RawIl2CppType> extraTypesByVa,
        Dictionary<ulong, Il2CppBinaryIndex.RawArrayType> arrayTypesByVa,
        Dictionary<ulong, Il2CppBinaryIndex.RawGenericClass> genericClassesByVa,
        int depth)
    {
        if (depth > MaxTypeGraphDepth || raw.Data == 0)
        {
            return;
        }

        switch (raw.TypeEnum)
        {
            case 0x0F: // PTR
            case 0x10: // BYREF
            case 0x1D: // SZARRAY
            case 0x1F: // CMOD_REQD
            case 0x20: // CMOD_OPT
            case 0x45: // PINNED
            {
                var targetVa = raw.Data;
                if (!extraTypesByVa.ContainsKey(targetVa) && view.TryReadRawIl2CppType(targetVa, out var targetRaw))
                {
                    extraTypesByVa[targetVa] = targetRaw;
                    CollectTypeGraph(view, targetRaw, context, extraTypesByVa, arrayTypesByVa, genericClassesByVa, depth + 1);
                }
                break;
            }

            case 0x14: // ARRAY
            {
                var arrVa = raw.Data;
                if (arrayTypesByVa.ContainsKey(arrVa))
                {
                    break;
                }

                if (view.TryReadPointerAtVa(arrVa, out var elemTypeVa)
                    && elemTypeVa != 0
                    && view.TryReadByteAtVa(arrVa + (ulong)view.PointerSize, out var rank))
                {
                    arrayTypesByVa[arrVa] = new Il2CppBinaryIndex.RawArrayType(elemTypeVa, rank);
                    if (!extraTypesByVa.ContainsKey(elemTypeVa) && view.TryReadRawIl2CppType(elemTypeVa, out var elemRaw))
                    {
                        extraTypesByVa[elemTypeVa] = elemRaw;
                        CollectTypeGraph(view, elemRaw, context, extraTypesByVa, arrayTypesByVa, genericClassesByVa, depth + 1);
                    }
                }
                break;
            }

            case 0x15: // GENERICINST
            {
                var gcVa = raw.Data;
                if (genericClassesByVa.ContainsKey(gcVa))
                {
                    break;
                }

                var ptrSize = (ulong)view.PointerSize;
                if (!view.TryReadPointerAtVa(gcVa, out var word0)
                    || !view.TryReadPointerAtVa(gcVa + ptrSize, out var classInstVa)
                    || classInstVa == 0)
                {
                    break;
                }

                var tdIdx = ResolveGenericClassTypeDefIndex(view, word0, context);
                if (tdIdx < 0 || tdIdx >= context.TotalTypes)
                {
                    break;
                }

                if (!view.TryReadUInt32AtVa(classInstVa, out var argc)
                    || argc == 0
                    || argc > 64
                    || !view.TryReadPointerAtVa(classInstVa + ptrSize, out var argvVa)
                    || argvVa == 0)
                {
                    break;
                }

                var argVas = new ulong[argc];
                for (var a = 0; a < (int)argc; a++)
                {
                    if (!view.TryReadPointerAtVa(argvVa + (ulong)a * ptrSize, out var argVa))
                    {
                        argVa = 0;
                    }
                    argVas[a] = argVa;
                }

                genericClassesByVa[gcVa] = new Il2CppBinaryIndex.RawGenericClass(tdIdx, argVas);
                for (var a = 0; a < argVas.Length; a++)
                {
                    var argVa = argVas[a];
                    if (argVa != 0 && !extraTypesByVa.ContainsKey(argVa) && view.TryReadRawIl2CppType(argVa, out var argRaw))
                    {
                        extraTypesByVa[argVa] = argRaw;
                        CollectTypeGraph(view, argRaw, context, extraTypesByVa, arrayTypesByVa, genericClassesByVa, depth + 1);
                    }
                }
                break;
            }
        }
    }

    private static int ResolveGenericClassTypeDefIndex(
        BinaryImageView view,
        ulong word0,
        Il2CppBinaryMetadataContext context)
    {
        bool TryAsV27TypePtr(out int tdIdx)
        {
            tdIdx = -1;
            if (word0 < 0x40 || !view.IsValidMappedVa(word0, (ulong)view.PointerSize + 4))
            {
                return false;
            }

            if (!view.TryReadRawIl2CppType(word0, out var inner))
            {
                return false;
            }

            if (inner.TypeEnum is not (0x11 or 0x12) || inner.Attributes != 0)
            {
                return false;
            }

            var candidate = (int)(inner.Data & 0xFFFFFFFFUL);
            if (candidate < 0 || candidate >= context.TotalTypes)
            {
                return false;
            }

            tdIdx = candidate;
            return true;
        }

        bool TryAsV24TypeDefIndex(out int tdIdx)
        {
            tdIdx = -1;
            if (view.PointerSize == 8 && (word0 >> 32) != 0)
            {
                return false;
            }

            var candidate = (int)(word0 & 0xFFFFFFFFUL);
            if (candidate < 0 || candidate >= context.TotalTypes)
            {
                return false;
            }

            tdIdx = candidate;
            return true;
        }

        if (context.MetadataVersion >= 27)
        {
            if (TryAsV27TypePtr(out var td)) return td;
            if (TryAsV24TypeDefIndex(out td)) return td;
        }
        else
        {
            if (TryAsV24TypeDefIndex(out var td)) return td;
            if (TryAsV27TypePtr(out td)) return td;
        }

        return -1;
    }

    private static bool DetectByrefBitLayout(
        Il2CppBinaryIndex.RawIl2CppType[] types,
        Il2CppBinaryMetadataContext context)
    {
        if (context.Int32ByvalTypeIndex >= 0 && context.Int32ByvalTypeIndex < types.Length)
        {
            var raw = types[context.Int32ByvalTypeIndex];
            if (raw.Bits != 0)
            {
                return (raw.Bits & 0x80000000u) != 0;
            }
        }

        if (context.SampleValueTypeByvalTypeIndex >= 0 && context.SampleValueTypeByvalTypeIndex < types.Length)
        {
            var raw = types[context.SampleValueTypeByvalTypeIndex];
            if (raw.Bits != 0)
            {
                return (raw.Bits & 0x80000000u) != 0;
            }
        }

        return context.MetadataVersion >= 28;
    }

    private static long[] ReadFieldOffsetTableFileOffsets(
        BinaryImageView view,
        ulong fieldOffsetsTableVa,
        int totalTypes,
        CancellationToken cancellationToken)
    {
        var ptrSize = view.PointerSize;
        var tableBytes = new byte[(long)totalTypes * ptrSize];
        if (!view.TryReadBytesAtVa(fieldOffsetsTableVa, tableBytes))
        {
            return Array.Empty<long>();
        }

        var result = new long[totalTypes];
        Array.Fill(result, -1L);
        for (var i = 0; i < totalTypes; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var ptr = ReadPtrWord(tableBytes, i * ptrSize, ptrSize);
            if (ptr == 0 && view.RelocFallback != null
                && view.RelocFallback.TryGetValue(fieldOffsetsTableVa + (ulong)i * (ulong)ptrSize, out var relocPtr))
            {
                ptr = relocPtr;
            }

            if (ptr != 0 && view.TryVaToFileOffset(ptr, out var fo))
            {
                result[i] = fo;
            }
        }

        return result;
    }

    private static int ReadCountWord(byte[] data, int offset, int ptrSize)
    {
        if (ptrSize == 8)
        {
            var upper = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (upper != 0)
            {
                return -1;
            }
        }

        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static ulong ReadPtrWord(byte[] data, int offset, int ptrSize)
    {
        return ptrSize == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static int ReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination.Slice(total));
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
