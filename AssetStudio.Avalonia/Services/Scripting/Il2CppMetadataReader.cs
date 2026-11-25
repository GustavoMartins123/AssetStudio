using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia.Services.Scripting;

public sealed class Il2CppMetadataLoadOutcome
{
    public bool Success { get; init; }
    public int MetadataVersion { get; init; }
    public int TypeCount { get; init; }
    public int ImageCount { get; init; }
    public bool IsObfuscatedOrUnsupported { get; init; }
    public string DiagnosticMessage { get; init; } = string.Empty;
}

/// <summary>
/// Pure-C# reader for Unity IL2CPP <c>global-metadata.dat</c>: the classic layout (versions 24 through 31)
/// and the compact layout of Unity 6000.3 (v39) and Unity 6000.5 (v107).
/// Reconstructs readable C# type declarations, fields, properties, events, methods,
/// parameters, generic parameters, base classes, interfaces, and enum constants without
/// requiring any external tool.
/// </summary>
/// <remarks>
/// The compact layout follows libil2cpp's <c>vm/GlobalMetadataFileInternals.h</c> and
/// <c>vm/MetadataDeserialization.cpp</c> shipped with the Unity Editor: the header stores
/// <c>(offset, size, count)</c> triples, and index columns take 1, 2 or 4 bytes depending on the element
/// count of the table they point into (all-ones meaning -1).
/// </remarks>
public sealed class Il2CppMetadataReader
{
    public const uint MetadataSanity = 0xFAB11BAF;
    public const int MinLegacyVersion = 24;
    public const int MaxLegacyVersion = 31;
    private const int MaxCachedFiles = 2;
    private const int MaxHeaderBytes = 1024;
    private const int MaxStringReadBytes = 2048;
    private const int MinCompactSectionCount = 31;
    private const int MaxCompactSectionCount = 64;
    private const int Unity6000_3SectionCount = 31;
    private const int Unity6000_5SectionCount = 32;

    private readonly record struct TableSlice(int Offset, int Size)
    {
        public bool IsValid => Offset >= 0 && Size >= 0;
    }

    /// <summary>
    /// Serialized byte width of each index kind. Classic metadata always uses 4 bytes; compact metadata sizes
    /// them from table counts (<c>SerializedIndexSizes</c> in libil2cpp).
    /// </summary>
    private sealed record IndexWidths
    {
        public static readonly IndexWidths Legacy = new();

        public int Type { get; init; } = 4;
        public int TypeDefinition { get; init; } = 4;
        public int GenericContainer { get; init; } = 4;
        public int Parameter { get; init; } = 4;
        public int GenericParameter { get; init; } = 4;
        public int Interfaces { get; init; } = 4;
        public int Event { get; init; } = 4;
        public int Property { get; init; } = 4;
        public int NestedType { get; init; } = 4;
        public int Field { get; init; } = 4;
        public int DefaultValueData { get; init; } = 4;
        public int Method { get; init; } = 4;
    }

    private readonly record struct TypeDefLayout(
        int StructSize,
        int ByvalTypeIndexOffset,
        int DeclaringTypeIndexOffset,
        int ParentIndexOffset,
        int ElementTypeIndexOffset,
        int GenericContainerIndexOffset,
        int FlagsOffset,
        int FieldStartOffset,
        int MethodStartOffset,
        int EventStartOffset,
        int PropertyStartOffset,
        int NestedTypesStartOffset,
        int InterfacesStartOffset)
    {
        public int MethodCountOffset => StructSize - 24;
        public int PropertyCountOffset => StructSize - 22;
        public int FieldCountOffset => StructSize - 20;
        public int EventCountOffset => StructSize - 18;
        public int NestedTypeCountOffset => StructSize - 16;
        public int InterfacesCountOffset => StructSize - 12;
        public int BitfieldOffset => StructSize - 8;
        public int TokenOffset => StructSize - 4;
    }

    private readonly record struct MethodLayout(
        int StructSize,
        int ParameterStartOffset,
        int GenericContainerIndexOffset,
        int MethodIndexOffset = -1,
        int DeclaringTypeOffset = 4,
        int ReturnTypeOffset = 8)
    {
        public int TokenOffset => StructSize - 12;
        public int FlagsOffset => StructSize - 8;
        public int ParameterCountOffset => StructSize - 2;
    }

    private readonly record struct ImageEntry(
        string Name,
        int TypeStart,
        int TypeCount,
        int CustomAttributeStart = -1,
        int CustomAttributeCount = 0);

    private readonly record struct TypeSummary(
        string Name,
        string Namespace,
        int ImageIndex,
        int DeclaringTypeIndex);

    private sealed class MetadataIndex
    {
        public required string FilePath { get; init; }
        public required long FileLength { get; init; }
        public required long LastWriteTicks { get; init; }
        public required int Version { get; init; }
        public long BodyOffsetDelta { get; init; }
        public byte[]? BodyXorKey { get; init; }
        public required TableSlice Strings { get; init; }
        public required TableSlice Events { get; init; }
        public required TableSlice Properties { get; init; }
        public required TableSlice Methods { get; init; }
        public required TableSlice FieldDefaultValues { get; init; }
        public required TableSlice DefaultValueData { get; init; }
        public required TableSlice Parameters { get; init; }
        public required TableSlice Fields { get; init; }
        public required TableSlice GenericParameters { get; init; }
        public required TableSlice GenericContainers { get; init; }
        public required TableSlice NestedTypes { get; init; }
        public required TableSlice Interfaces { get; init; }
        public required TableSlice TypeDefinitions { get; init; }
        public TableSlice AttributeDataOrInfo { get; init; }
        public TableSlice AttributeDataRangeOrTypes { get; init; }
        public bool UsesV29CustomAttributeFormat { get; init; }
        public required TypeDefLayout TypeDefLayout { get; init; }
        public required MethodLayout MethodLayout { get; init; }
        public required int FieldStructSize { get; init; }
        public required int ParameterStructSize { get; init; }
        public required int PropertyStructSize { get; init; }
        public required int EventStructSize { get; init; }
        public IndexWidths Widths { get; init; } = IndexWidths.Legacy;
        public int FieldDefaultValueStructSize { get; init; } = 12;
        public int GenericParameterStructSize { get; init; } = 16;
        public int GenericContainerStructSize { get; init; } = 16;
        public int GenericContainerArgCountWidth { get; init; } = 4;
        public int GenericContainerParameterStartOffset { get; init; } = 12;
        public bool EncodedAttributeConstructors { get; init; }
        public required int TotalMethods { get; init; }
        public int Int32ByvalTypeIndex { get; init; } = -1;
        public int Int32TypeDefIndex { get; init; } = -1;
        public int ObjectByvalTypeIndex { get; init; } = -1;
        public int StringByvalTypeIndex { get; init; } = -1;
        public int VoidByvalTypeIndex { get; init; } = -1;
        public int SampleValueTypeByvalTypeIndex { get; init; } = -1;
        public required ImageEntry[] Images { get; init; }
        public required TypeSummary[] Types { get; init; }
        public required Dictionary<string, List<int>> ByFullName { get; init; }
        public required Dictionary<string, List<int>> BySimpleName { get; init; }
        public required Dictionary<int, int> ByvalTypeToTypeDef { get; init; }
        public long LastAccessTick { get; set; }
    }

    private sealed class CachedFailure
    {
        public required string FilePath { get; init; }
        public required long FileLength { get; init; }
        public required long LastWriteTicks { get; init; }
        public required Il2CppMetadataLoadOutcome Outcome { get; init; }
        public long LastAccessTick { get; set; }
    }

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, MetadataIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedFailure> _failureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Il2CppBinaryReader _binaryReader;
    private long _accessCounter;

    public Il2CppMetadataReader(Il2CppBinaryReader? binaryReader = null)
    {
        _binaryReader = binaryReader ?? new Il2CppBinaryReader();
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _indexCache.Clear();
            _failureCache.Clear();
        }
        _binaryReader.ClearCache();
    }

    public Il2CppMetadataLoadOutcome InspectMetadata(string metadataPath, CancellationToken cancellationToken = default)
    {
        var (index, outcome) = GetOrBuildIndex(metadataPath, cancellationToken);
        return index != null
            ? new Il2CppMetadataLoadOutcome
            {
                Success = true,
                MetadataVersion = index.Version,
                TypeCount = index.Types.Length,
                ImageCount = index.Images.Length
            }
            : outcome;
    }

    public ScriptDecompilationResult? TryExtractType(
        string metadataPath,
        ScriptLookupRequest request,
        out string? diagnosticMessage,
        CancellationToken cancellationToken = default)
    {
        return TryExtractType(metadataPath, request, request.GameAssemblyPath, out diagnosticMessage, cancellationToken);
    }

    public ScriptDecompilationResult? TryExtractType(
        string metadataPath,
        ScriptLookupRequest request,
        string? gameAssemblyPath,
        out string? diagnosticMessage,
        CancellationToken cancellationToken = default)
    {
        diagnosticMessage = null;
        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath) || string.IsNullOrWhiteSpace(request.ClassName))
        {
            return null;
        }

        var (index, outcome) = GetOrBuildIndex(metadataPath, cancellationToken);
        if (index == null)
        {
            diagnosticMessage = outcome.DiagnosticMessage;
            return null;
        }

        var typeDefIndex = FindMatchingType(index, request.AssemblyName, request.Namespace, request.ClassName);
        if (typeDefIndex < 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        Il2CppBinaryIndex? binaryIndex = null;
        var effectiveBinaryPath = ResolveEffectiveBinaryPath(gameAssemblyPath, request.GameAssemblyPath, metadataPath);
        if (!string.IsNullOrWhiteSpace(effectiveBinaryPath))
        {
            var binaryCtx = new Il2CppBinaryMetadataContext
            {
                MetadataVersion = index.Version,
                TotalTypes = index.Types.Length,
                TotalMethods = index.TotalMethods,
                Int32ByvalTypeIndex = index.Int32ByvalTypeIndex,
                Int32TypeDefIndex = index.Int32TypeDefIndex,
                ObjectByvalTypeIndex = index.ObjectByvalTypeIndex,
                StringByvalTypeIndex = index.StringByvalTypeIndex,
                VoidByvalTypeIndex = index.VoidByvalTypeIndex,
                SampleValueTypeByvalTypeIndex = index.SampleValueTypeByvalTypeIndex,
                ImageNames = index.Images.Select(static img => img.Name).ToArray()
            };
            binaryIndex = _binaryReader.TryLoad(effectiveBinaryPath, binaryCtx, cancellationToken);
        }

        var fieldTypeOverrides = request.FallbackTypeTree != null
            ? TypeTreeScriptReconstructor.ExtractTopLevelFieldTypes(request.FallbackTypeTree)
            : null;
        return ReconstructTypeResult(index, binaryIndex, typeDefIndex, fieldTypeOverrides, cancellationToken);
    }

    public ScriptDecompilationResult? TryExtractType(
        string metadataPath,
        ScriptLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        return TryExtractType(metadataPath, request, request.GameAssemblyPath, out _, cancellationToken);
    }

    private static string? ResolveEffectiveBinaryPath(
        string? explicitPath,
        string? requestPath,
        string metadataPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(requestPath) && File.Exists(requestPath))
        {
            return Path.GetFullPath(requestPath);
        }

        return ScriptSourceDiscovery.TryFindGameAssemblyNearMetadata(metadataPath);
    }

    private (MetadataIndex? Index, Il2CppMetadataLoadOutcome Outcome) GetOrBuildIndex(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
        {
            return (null, new Il2CppMetadataLoadOutcome
            {
                Success = false,
                DiagnosticMessage = "global-metadata.dat was not found."
            });
        }

        string fullPath;
        FileInfo info;
        try
        {
            fullPath = Path.GetFullPath(metadataPath);
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return (null, new Il2CppMetadataLoadOutcome
                {
                    Success = false,
                    DiagnosticMessage = "global-metadata.dat was not found."
                });
            }
        }
        catch (Exception ex)
        {
            return (null, new Il2CppMetadataLoadOutcome
            {
                Success = false,
                DiagnosticMessage = $"Unable to access global-metadata.dat: {ex.Message}"
            });
        }

        var tick = Interlocked.Increment(ref _accessCounter);
        lock (_cacheLock)
        {
            if (_indexCache.TryGetValue(fullPath, out var cached)
                && cached.FileLength == info.Length
                && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                cached.LastAccessTick = tick;
                return (cached, new Il2CppMetadataLoadOutcome
                {
                    Success = true,
                    MetadataVersion = cached.Version,
                    TypeCount = cached.Types.Length,
                    ImageCount = cached.Images.Length
                });
            }

            if (_failureCache.TryGetValue(fullPath, out var failed)
                && failed.FileLength == info.Length
                && failed.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                failed.LastAccessTick = tick;
                return (null, failed.Outcome);
            }
        }

        var built = BuildIndex(fullPath, info.Length, info.LastWriteTimeUtc.Ticks, tick, cancellationToken);
        lock (_cacheLock)
        {
            if (built.Index != null)
            {
                _failureCache.Remove(fullPath);
                _indexCache[fullPath] = built.Index;
                EvictCacheIfNeededLocked();
            }
            else
            {
                _indexCache.Remove(fullPath);
                _failureCache[fullPath] = new CachedFailure
                {
                    FilePath = fullPath,
                    FileLength = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Outcome = built.Outcome,
                    LastAccessTick = tick
                };
                EvictCacheIfNeededLocked();
            }
        }

        return built;
    }

    private void EvictCacheIfNeededLocked()
    {
        while (_indexCache.Count > MaxCachedFiles)
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

        while (_failureCache.Count > MaxCachedFiles * 2)
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

    private static (MetadataIndex? Index, Il2CppMetadataLoadOutcome Outcome) BuildIndex(
        string fullPath,
        long fileLength,
        long lastWriteTicks,
        long accessTick,
        CancellationToken cancellationToken)
    {
        try
        {
            if (fileLength < 208)
            {
                return Fail("global-metadata.dat is truncated or too small to contain a valid header.", isObfuscated: true);
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            var headerReadLen = (int)Math.Min(fileLength, MaxHeaderBytes);
            var headerBuf = new byte[headerReadLen];
            ReadExactlyOrFail(stream, headerBuf, 0, headerReadLen);

            var sanity = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(0, 4));
            if (sanity != MetadataSanity)
            {
                return Fail(
                    $"global-metadata.dat magic mismatch (0x{sanity:X8}); metadata appears encrypted or obfuscated.",
                    isObfuscated: true);
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(4, 4));
            if (version < MinLegacyVersion || version > MaxLegacyVersion)
            {
                if (TryReadCompactSections(headerBuf, fileLength, out var compactSections))
                {
                    return BuildCompactIndex(
                        stream,
                        fullPath,
                        fileLength,
                        lastWriteTicks,
                        accessTick,
                        version,
                        compactSections,
                        cancellationToken);
                }

                return Fail(
                    $"IL2CPP global-metadata.dat version v{version} is not supported by the built-in reader (supported: v{MinLegacyVersion}-v{MaxLegacyVersion}, v39 from Unity 6000.3 and v107 from Unity 6000.5).",
                    isObfuscated: version < 16 || version > 1000);
            }

            var headerSize = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(8, 4));
            if (headerSize < 200 || headerSize > MaxHeaderBytes || (headerSize - 8) % 8 != 0 || headerSize > fileLength)
            {
                return Fail(
                    $"global-metadata.dat (v{version}) has an invalid or obfuscated header size ({headerSize}).",
                    isObfuscated: true);
            }

            var pairCount = (headerSize - 8) / 8;
            if (pairCount < 21)
            {
                return Fail(
                    $"global-metadata.dat (v{version}) header contains too few metadata tables ({pairCount}).",
                    isObfuscated: true);
            }

            var pairs = new TableSlice[pairCount];
            long maxTableEnd = headerSize;
            for (var i = 0; i < pairCount; i++)
            {
                var off = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(8 + i * 8, 4));
                var sz = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(12 + i * 8, 4));
                if (off < 0 || sz < 0 || (long)off + sz > fileLength)
                {
                    if (i <= 21)
                    {
                        return Fail(
                            $"global-metadata.dat (v{version}) table #{i} exceeds file bounds; metadata may be obfuscated or corrupted.",
                            isObfuscated: true);
                    }

                    pairs[i] = new TableSlice(0, 0);
                }
                else
                {
                    pairs[i] = new TableSlice(off, sz);
                    var tableEnd = (long)off + sz;
                    if (tableEnd > maxTableEnd)
                    {
                        maxTableEnd = tableEnd;
                    }
                }
            }

            var stringsSlice = pairs[2];
            var eventsSlice = pairs[3];
            var propertiesSlice = pairs[4];
            var methodsSlice = pairs[5];
            var fieldDefaultsSlice = pairs[7];
            var defaultDataSlice = pairs[8];
            var parametersSlice = pairs[10];
            var fieldsSlice = pairs[11];
            var genericParamsSlice = pairs[12];
            var genericContainersSlice = pairs[14];
            var nestedTypesSlice = pairs[15];
            var interfacesSlice = pairs[16];
            var typeDefsSlice = pairs[19];

            if (stringsSlice.Size <= 0 || typeDefsSlice.Size <= 0)
            {
                return Fail(
                    $"global-metadata.dat (v{version}) has empty string or type definition tables.",
                    isObfuscated: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Resolve standard plaintext tables first; if those fail validation, probe for a
            // generic post-header body shift (fileLength - maxTableEnd) and/or repeating XOR mask
            // validated strictly against image table layout and TypeDefinition metadata tokens.
            if (!TryResolveTablesAndTransform(
                    stream,
                    fileLength,
                    maxTableEnd,
                    pairs,
                    stringsSlice,
                    typeDefsSlice,
                    cancellationToken,
                    out var bodyOffsetDelta,
                    out var bodyXorKey,
                    out var stringBytes,
                    out var images,
                    out var imagesPairIndex,
                    out var totalTypes,
                    out var typeDefStructSize,
                    out var typeDefBytes))
            {
                return Fail(
                    $"global-metadata.dat (v{version}) tables appear encrypted, obfuscated, or use an unrecognized layout.",
                    isObfuscated: true);
            }

            // Detect method/field/param/property/event struct sizes via metadata token validation
            var methodLayout = DetectMethodLayout(stream, methodsSlice, stringBytes, version, totalTypes, bodyOffsetDelta, bodyXorKey);
            var fieldStructSize = DetectFixedTableStructSize(stream, fieldsSlice, stringBytes, new[] { 12, 16 }, tokenOffsetFromEnd: 4, expectedTokenTable: 0x04, checkNameAt0: true, bodyOffsetDelta, bodyXorKey);
            var paramStructSize = DetectParamStructSize(stream, parametersSlice, bodyOffsetDelta, bodyXorKey);
            var propertyStructSize = DetectFixedTableStructSize(stream, propertiesSlice, stringBytes, new[] { 20, 24 }, tokenOffsetFromEnd: 4, expectedTokenTable: 0x17, checkNameAt0: true, bodyOffsetDelta, bodyXorKey);
            var eventStructSize = DetectFixedTableStructSize(stream, eventsSlice, stringBytes, new[] { 24, 28 }, tokenOffsetFromEnd: 4, expectedTokenTable: 0x14, checkNameAt0: true, bodyOffsetDelta, bodyXorKey);

            var totalMethods = methodLayout.StructSize > 0 ? methodsSlice.Size / methodLayout.StructSize : 0;
            var totalFields = fieldStructSize > 0 ? fieldsSlice.Size / fieldStructSize : 0;

            var typeDefLayout = DetectTypeDefLayout(
                version,
                typeDefStructSize,
                typeDefBytes,
                totalTypes,
                totalMethods,
                totalFields);

            // Resolve custom attribute table slices across v24-v31
            var attrDataOrInfo = new TableSlice(0, 0);
            var attrRangeOrTypes = new TableSlice(0, 0);
            var usesV29CustomAttrs = version >= 29;
            if (version >= 27 && imagesPairIndex == 20 && pairs.Length > 25)
            {
                attrDataOrInfo = pairs[24];
                attrRangeOrTypes = pairs[25];
            }
            else if (imagesPairIndex == 21 && pairs.Length > 28)
            {
                attrDataOrInfo = pairs[27];
                attrRangeOrTypes = pairs[28];
            }
            else if (imagesPairIndex == 20 && pairs.Length > 27)
            {
                attrDataOrInfo = pairs[26];
                attrRangeOrTypes = pairs[27];
            }

            var lookup = BuildTypeLookup(
                typeDefBytes,
                totalTypes,
                typeDefStructSize,
                typeDefLayout,
                IndexWidths.Legacy,
                stringBytes,
                images,
                cancellationToken);

            var index = new MetadataIndex
            {
                FilePath = fullPath,
                FileLength = fileLength,
                LastWriteTicks = lastWriteTicks,
                Version = version,
                BodyOffsetDelta = bodyOffsetDelta,
                BodyXorKey = bodyXorKey,
                Strings = stringsSlice,
                Events = eventsSlice,
                Properties = propertiesSlice,
                Methods = methodsSlice,
                FieldDefaultValues = fieldDefaultsSlice,
                DefaultValueData = defaultDataSlice,
                Parameters = parametersSlice,
                Fields = fieldsSlice,
                GenericParameters = genericParamsSlice,
                GenericContainers = genericContainersSlice,
                NestedTypes = nestedTypesSlice,
                Interfaces = interfacesSlice,
                TypeDefinitions = typeDefsSlice,
                AttributeDataOrInfo = attrDataOrInfo,
                AttributeDataRangeOrTypes = attrRangeOrTypes,
                UsesV29CustomAttributeFormat = usesV29CustomAttrs,
                TypeDefLayout = typeDefLayout,
                MethodLayout = methodLayout,
                FieldStructSize = fieldStructSize,
                ParameterStructSize = paramStructSize,
                PropertyStructSize = propertyStructSize,
                EventStructSize = eventStructSize,
                TotalMethods = totalMethods,
                Int32ByvalTypeIndex = lookup.Int32ByvalTypeIndex,
                Int32TypeDefIndex = lookup.Int32TypeDefIndex,
                ObjectByvalTypeIndex = lookup.ObjectByvalTypeIndex,
                StringByvalTypeIndex = lookup.StringByvalTypeIndex,
                VoidByvalTypeIndex = lookup.VoidByvalTypeIndex,
                SampleValueTypeByvalTypeIndex = lookup.SampleValueTypeByvalTypeIndex,
                Images = images,
                Types = lookup.Types,
                ByFullName = lookup.ByFullName,
                BySimpleName = lookup.BySimpleName,
                ByvalTypeToTypeDef = lookup.ByvalTypeToTypeDef,
                LastAccessTick = accessTick
            };

            return (index, new Il2CppMetadataLoadOutcome
            {
                Success = true,
                MetadataVersion = version,
                TypeCount = totalTypes,
                ImageCount = images.Length
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"Failed to parse global-metadata.dat: {ex.Message}", isObfuscated: false);
        }
    }

    private sealed class TypeLookup
    {
        public required TypeSummary[] Types { get; init; }
        public required Dictionary<string, List<int>> ByFullName { get; init; }
        public required Dictionary<string, List<int>> BySimpleName { get; init; }
        public required Dictionary<int, int> ByvalTypeToTypeDef { get; init; }
        public int Int32ByvalTypeIndex { get; init; } = -1;
        public int Int32TypeDefIndex { get; init; } = -1;
        public int ObjectByvalTypeIndex { get; init; } = -1;
        public int StringByvalTypeIndex { get; init; } = -1;
        public int VoidByvalTypeIndex { get; init; } = -1;
        public int SampleValueTypeByvalTypeIndex { get; init; } = -1;
    }

    private static TypeLookup BuildTypeLookup(
        byte[] typeDefBytes,
        int totalTypes,
        int typeDefStructSize,
        TypeDefLayout typeDefLayout,
        IndexWidths widths,
        byte[] stringBytes,
        ImageEntry[] images,
        CancellationToken cancellationToken)
    {
        var types = new TypeSummary[totalTypes];
        var byFullName = new Dictionary<string, List<int>>(totalTypes, StringComparer.OrdinalIgnoreCase);
        var bySimpleName = new Dictionary<string, List<int>>(totalTypes, StringComparer.OrdinalIgnoreCase);
        var byvalMap = new Dictionary<int, int>(totalTypes);
        var int32Byval = -1;
        var int32Td = -1;
        var objectByval = -1;
        var stringByval = -1;
        var voidByval = -1;
        var sampleValueTypeByval = -1;

        // Map TypeDefIndex -> ImageIndex in O(totalTypes)
        var typeToImage = new int[totalTypes];
        Array.Fill(typeToImage, -1);
        for (var imgIdx = 0; imgIdx < images.Length; imgIdx++)
        {
            var img = images[imgIdx];
            var end = Math.Min(totalTypes, img.TypeStart + img.TypeCount);
            for (var t = Math.Max(0, img.TypeStart); t < end; t++)
            {
                typeToImage[t] = imgIdx;
            }
        }

        for (var i = 0; i < totalTypes; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = typeDefBytes.AsSpan(i * typeDefStructSize, typeDefStructSize);
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
            var nsIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
            var byvalIdx = ReadIndex(entry, typeDefLayout.ByvalTypeIndexOffset, widths.Type);
            var declaringIdx = ReadIndex(entry, typeDefLayout.DeclaringTypeIndexOffset, widths.Type);
            var bitfield = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(typeDefLayout.BitfieldOffset, 4));

            var name = ReadNullTerminatedUtf8(stringBytes, nameIdx) ?? string.Empty;
            var ns = ReadNullTerminatedUtf8(stringBytes, nsIdx) ?? string.Empty;

            types[i] = new TypeSummary(name, ns, typeToImage[i], declaringIdx);

            if (byvalIdx >= 0 && !byvalMap.ContainsKey(byvalIdx))
            {
                byvalMap[byvalIdx] = i;
            }

            if (byvalIdx >= 0)
            {
                if (sampleValueTypeByval < 0 && (bitfield & 0x1u) != 0 && !string.Equals(name, "Void", StringComparison.Ordinal))
                {
                    sampleValueTypeByval = byvalIdx;
                }

                if (string.Equals(ns, "System", StringComparison.Ordinal))
                {
                    if (int32Byval < 0 && string.Equals(name, "Int32", StringComparison.Ordinal))
                    {
                        int32Byval = byvalIdx;
                        int32Td = i;
                    }
                    else if (objectByval < 0 && string.Equals(name, "Object", StringComparison.Ordinal))
                    {
                        objectByval = byvalIdx;
                    }
                    else if (stringByval < 0 && string.Equals(name, "String", StringComparison.Ordinal))
                    {
                        stringByval = byvalIdx;
                    }
                    else if (voidByval < 0 && string.Equals(name, "Void", StringComparison.Ordinal))
                    {
                        voidByval = byvalIdx;
                    }
                }
            }

            if (string.IsNullOrEmpty(name) || string.Equals(name, "<Module>", StringComparison.Ordinal))
            {
                continue;
            }

            var baseName = StripGenericArity(name);
            if (!string.IsNullOrEmpty(ns))
            {
                AddToBucket(byFullName, $"{ns}.{name}", i);
                if (!string.Equals(baseName, name, StringComparison.Ordinal))
                {
                    AddToBucket(byFullName, $"{ns}.{baseName}", i);
                }
            }
            else
            {
                AddToBucket(byFullName, name, i);
                if (!string.Equals(baseName, name, StringComparison.Ordinal))
                {
                    AddToBucket(byFullName, baseName, i);
                }
            }

            AddToBucket(bySimpleName, name, i);
            if (!string.Equals(baseName, name, StringComparison.Ordinal))
            {
                AddToBucket(bySimpleName, baseName, i);
            }
        }

        return new TypeLookup
        {
            Types = types,
            ByFullName = byFullName,
            BySimpleName = bySimpleName,
            ByvalTypeToTypeDef = byvalMap,
            Int32ByvalTypeIndex = int32Byval,
            Int32TypeDefIndex = int32Td,
            ObjectByvalTypeIndex = objectByval,
            StringByvalTypeIndex = stringByval,
            VoidByvalTypeIndex = voidByval,
            SampleValueTypeByvalTypeIndex = sampleValueTypeByval
        };
    }

    private static int ReadIndex(ReadOnlySpan<byte> buffer, int offset, int width)
    {
        switch (width)
        {
            case 1:
                var u8 = buffer[offset];
                return u8 == byte.MaxValue ? -1 : u8;
            case 2:
                var u16 = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
                return u16 == ushort.MaxValue ? -1 : u16;
            default:
                return BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        }
    }

    private static int CompactIndexWidth(int elementCount)
    {
        return elementCount <= byte.MaxValue ? 1 : elementCount <= ushort.MaxValue ? 2 : 4;
    }

    private readonly record struct CompactSection(int Offset, int Size, int Count)
    {
        public TableSlice Slice => new(Offset, Size);
    }

    private enum CompactTable
    {
        Strings = 2,
        Events = 3,
        Properties = 4,
        Methods = 5,
        ParameterDefaultValues = 6,
        FieldDefaultValues = 7,
        FieldAndParameterDefaultValueData = 8,
        FieldMarshaledSizes = 9,
        Parameters = 10,
        Fields = 11,
        GenericParameters = 12,
        GenericParameterConstraints = 13,
        GenericContainers = 14,
        NestedTypes = 15,
        Interfaces = 16,
        InterfaceOffsets = 18,
        TypeDefinitions = 19,
        Images = 20,
        FieldRefs = 22,
        AttributeData = 24,
        AttributeDataRanges = 25
    }

    /// <summary>
    /// Reads the compact header: <c>stringLiterals.offset</c> equals the header size, which is 8 bytes plus one
    /// 12-byte <c>Il2CppSectionMetadata</c> triple per section.
    /// </summary>
    private static bool TryReadCompactSections(byte[] header, long fileLength, out CompactSection[] sections)
    {
        sections = Array.Empty<CompactSection>();
        if (header.Length < 12)
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (headerSize < 8 + MinCompactSectionCount * 12
            || headerSize > 8 + MaxCompactSectionCount * 12
            || (headerSize - 8) % 12 != 0
            || headerSize > header.Length
            || headerSize > fileLength)
        {
            return false;
        }

        var count = (headerSize - 8) / 12;
        var parsed = new CompactSection[count];
        for (var i = 0; i < count; i++)
        {
            var entry = header.AsSpan(8 + i * 12, 12);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
            var rows = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
            if (offset < headerSize || size < 0 || rows < 0 || (long)offset + size > fileLength)
            {
                return false;
            }

            parsed[i] = new CompactSection(offset, size, rows);
        }

        sections = parsed;
        return true;
    }

    /// <summary>
    /// Indexes Unity 6000.3+ compact metadata. Unity 6000.5 (v107) inserts <c>typeInlineArrays</c> after
    /// <c>typeDefinitions</c>, sizes almost every index column from its table count, packs
    /// <c>Il2CppGenericContainer</c> and stores custom attribute constructors as encoded method indexes.
    /// The TypeIndex width comes from the native binary's type count, which the metadata does not record,
    /// so it is inferred from row sizes and accepted only when every table size agrees with it.
    /// </summary>
    private static (MetadataIndex? Index, Il2CppMetadataLoadOutcome Outcome) BuildCompactIndex(
        FileStream stream,
        string fullPath,
        long fileLength,
        long lastWriteTicks,
        long accessTick,
        int version,
        CompactSection[] sections,
        CancellationToken cancellationToken)
    {
        bool isUnity6000_5;
        if (sections.Length == Unity6000_3SectionCount)
        {
            isUnity6000_5 = false;
        }
        else if (sections.Length == Unity6000_5SectionCount)
        {
            isUnity6000_5 = true;
        }
        else
        {
            return Fail(
                $"global-metadata.dat (v{version}) uses a compact header with {sections.Length} sections; only the Unity 6000.3 (v39, 31 sections) and Unity 6000.5 (v107, 32 sections) layouts are supported.",
                isObfuscated: true);
        }

        CompactSection Table(CompactTable table)
        {
            var position = (int)table;
            if (isUnity6000_5 && position >= (int)CompactTable.Images)
            {
                position++;
            }
            return sections[position];
        }

        int PerTableWidth(CompactTable table) => isUnity6000_5 ? CompactIndexWidth(Table(table).Count) : 4;

        var fields = Table(CompactTable.Fields);
        var impliedTypeWidth = fields.Count > 0 && fields.Size % fields.Count == 0 ? fields.Size / fields.Count - 8 : 0;
        var typeWidthCandidates = new[] { impliedTypeWidth, 4, 2, 1 }.Where(static w => w is 1 or 2 or 4).Distinct();

        IndexWidths? widths = null;
        string? mismatch = null;
        foreach (var typeWidth in typeWidthCandidates)
        {
            var candidate = new IndexWidths
            {
                Type = typeWidth,
                TypeDefinition = CompactIndexWidth(Table(CompactTable.TypeDefinitions).Count),
                GenericContainer = CompactIndexWidth(Table(CompactTable.GenericContainers).Count),
                Parameter = CompactIndexWidth(Table(CompactTable.Parameters).Count),
                GenericParameter = PerTableWidth(CompactTable.GenericParameters),
                Interfaces = PerTableWidth(CompactTable.InterfaceOffsets),
                Event = PerTableWidth(CompactTable.Events),
                Property = PerTableWidth(CompactTable.Properties),
                NestedType = PerTableWidth(CompactTable.NestedTypes),
                Field = PerTableWidth(CompactTable.Fields),
                DefaultValueData = PerTableWidth(CompactTable.FieldAndParameterDefaultValueData),
                Method = PerTableWidth(CompactTable.Methods)
            };

            var currentMismatch = FindCompactRowSizeMismatch(Table, candidate, isUnity6000_5);
            if (currentMismatch == null)
            {
                widths = candidate;
                break;
            }
            mismatch ??= currentMismatch;
        }

        if (widths == null)
        {
            return Fail(
                $"global-metadata.dat (v{version}) has a compact header, but its tables do not match the Unity 6000.3/6000.5 row layouts ({mismatch}); metadata may be obfuscated or use an unknown layout.",
                isObfuscated: true);
        }

        var w = widths;
        var stringsSlice = Table(CompactTable.Strings).Slice;
        var typeDefsSection = Table(CompactTable.TypeDefinitions);
        var imagesSection = Table(CompactTable.Images);
        if (stringsSlice.Size <= 0 || typeDefsSection.Count <= 0 || imagesSection.Count <= 0)
        {
            return Fail(
                $"global-metadata.dat (v{version}) has empty string, type definition, or image tables.",
                isObfuscated: true);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stringBytes = ReadTableBytes(stream, stringsSlice, 0, null);
        var totalTypes = typeDefsSection.Count;
        var typeDefStructSize = typeDefsSection.Size / totalTypes;
        var typeDefBytes = ReadTableBytes(stream, typeDefsSection.Slice, 0, null);
        if (!ValidateTypeDefTokens(typeDefBytes, totalTypes, typeDefStructSize))
        {
            return Fail(
                $"global-metadata.dat (v{version}) type definition tokens are invalid; metadata appears encrypted or obfuscated.",
                isObfuscated: true);
        }

        // Il2CppImageDefinition: nameIndex, assemblyIndex, typeStart, typeCount, exportedTypeStart,
        // exportedTypeCount, entryPointIndex, token, customAttributeStart, customAttributeCount
        var imageRowSize = imagesSection.Size / imagesSection.Count;
        var imageTypeCountOffset = 8 + w.TypeDefinition;
        var imageCustomAttributeOffset = 16 + 2 * w.TypeDefinition + w.Method + 4;
        var imageBytes = ReadTableBytes(stream, imagesSection.Slice, 0, null);
        var images = new ImageEntry[imagesSection.Count];
        for (var i = 0; i < images.Length; i++)
        {
            var row = imageBytes.AsSpan(i * imageRowSize, imageRowSize);
            var name = ReadNullTerminatedUtf8(stringBytes, BinaryPrimitives.ReadInt32LittleEndian(row.Slice(0, 4)));
            var typeStart = ReadIndex(row, 8, w.TypeDefinition);
            var typeCount = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(imageTypeCountOffset, 4));
            if (string.IsNullOrWhiteSpace(name)
                || !LooksLikeValidIdentifierOrFileName(name)
                || typeStart < 0
                || typeCount < 0
                || (long)typeStart + typeCount > totalTypes)
            {
                return Fail(
                    $"global-metadata.dat (v{version}) image table #{i} is invalid; metadata appears encrypted or obfuscated.",
                    isObfuscated: true);
            }

            var caStart = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(imageCustomAttributeOffset, 4));
            var caCount = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(imageCustomAttributeOffset + 4, 4));
            if (caStart < 0 || caCount <= 0)
            {
                caStart = -1;
                caCount = 0;
            }

            images[i] = new ImageEntry(name, typeStart, typeCount, caStart, caCount);
        }

        // Il2CppTypeDefinition: nameIndex, namespaceIndex, byvalTypeIndex, declaringTypeIndex, parentIndex,
        // genericContainerIndex, flags, fieldStart, methodStart, eventStart, propertyStart, nestedTypesStart,
        // interfacesStart, vtableStart, interfaceOffsetsStart, eight uint16 counts, bitfield, token.
        // elementTypeIndex no longer exists; enum underlying types come from the value__ field.
        var genericContainerOffset = 8 + 3 * w.Type;
        var fieldStartOffset = genericContainerOffset + w.GenericContainer + 4;
        var methodStartOffset = fieldStartOffset + w.Field;
        var eventStartOffset = methodStartOffset + w.Method;
        var propertyStartOffset = eventStartOffset + w.Event;
        var nestedTypesStartOffset = propertyStartOffset + w.Property;
        var interfacesStartOffset = nestedTypesStartOffset + w.NestedType;
        var typeDefLayout = new TypeDefLayout(
            StructSize: typeDefStructSize,
            ByvalTypeIndexOffset: 8,
            DeclaringTypeIndexOffset: 8 + w.Type,
            ParentIndexOffset: 8 + 2 * w.Type,
            ElementTypeIndexOffset: -1,
            GenericContainerIndexOffset: genericContainerOffset,
            FlagsOffset: genericContainerOffset + w.GenericContainer,
            FieldStartOffset: fieldStartOffset,
            MethodStartOffset: methodStartOffset,
            EventStartOffset: eventStartOffset,
            PropertyStartOffset: propertyStartOffset,
            NestedTypesStartOffset: nestedTypesStartOffset,
            InterfacesStartOffset: interfacesStartOffset);

        // Il2CppMethodDefinition: nameIndex, declaringType, returnType, returnParameterToken, parameterStart,
        // genericContainerIndex, token, flags, iflags, slot, parameterCount
        var methodsSection = Table(CompactTable.Methods);
        var methodLayout = new MethodLayout(
            StructSize: CompactMethodRowSize(w),
            ParameterStartOffset: 8 + w.TypeDefinition + w.Type,
            GenericContainerIndexOffset: 8 + w.TypeDefinition + w.Type + w.Parameter,
            MethodIndexOffset: -1,
            DeclaringTypeOffset: 4,
            ReturnTypeOffset: 4 + w.TypeDefinition);

        var lookup = BuildTypeLookup(
            typeDefBytes,
            totalTypes,
            typeDefStructSize,
            typeDefLayout,
            w,
            stringBytes,
            images,
            cancellationToken);

        var genericContainerRowSize = isUnity6000_5 ? 7 + w.GenericParameter : 16;
        var index = new MetadataIndex
        {
            FilePath = fullPath,
            FileLength = fileLength,
            LastWriteTicks = lastWriteTicks,
            Version = version,
            Strings = stringsSlice,
            Events = Table(CompactTable.Events).Slice,
            Properties = Table(CompactTable.Properties).Slice,
            Methods = methodsSection.Slice,
            FieldDefaultValues = Table(CompactTable.FieldDefaultValues).Slice,
            DefaultValueData = Table(CompactTable.FieldAndParameterDefaultValueData).Slice,
            Parameters = Table(CompactTable.Parameters).Slice,
            Fields = fields.Slice,
            GenericParameters = Table(CompactTable.GenericParameters).Slice,
            GenericContainers = Table(CompactTable.GenericContainers).Slice,
            NestedTypes = Table(CompactTable.NestedTypes).Slice,
            Interfaces = Table(CompactTable.Interfaces).Slice,
            TypeDefinitions = typeDefsSection.Slice,
            AttributeDataOrInfo = Table(CompactTable.AttributeData).Slice,
            AttributeDataRangeOrTypes = Table(CompactTable.AttributeDataRanges).Slice,
            UsesV29CustomAttributeFormat = true,
            EncodedAttributeConstructors = isUnity6000_5,
            TypeDefLayout = typeDefLayout,
            MethodLayout = methodLayout,
            Widths = w,
            FieldStructSize = 8 + w.Type,
            ParameterStructSize = 8 + w.Type,
            PropertyStructSize = 12 + 2 * w.Method,
            EventStructSize = 8 + w.Type + 3 * w.Method,
            FieldDefaultValueStructSize = w.Field + w.Type + w.DefaultValueData,
            GenericParameterStructSize = w.GenericContainer + 12,
            GenericContainerStructSize = genericContainerRowSize,
            GenericContainerArgCountWidth = isUnity6000_5 ? 2 : 4,
            GenericContainerParameterStartOffset = isUnity6000_5 ? 7 : 12,
            TotalMethods = methodsSection.Count,
            Int32ByvalTypeIndex = lookup.Int32ByvalTypeIndex,
            Int32TypeDefIndex = lookup.Int32TypeDefIndex,
            ObjectByvalTypeIndex = lookup.ObjectByvalTypeIndex,
            StringByvalTypeIndex = lookup.StringByvalTypeIndex,
            VoidByvalTypeIndex = lookup.VoidByvalTypeIndex,
            SampleValueTypeByvalTypeIndex = lookup.SampleValueTypeByvalTypeIndex,
            Images = images,
            Types = lookup.Types,
            ByFullName = lookup.ByFullName,
            BySimpleName = lookup.BySimpleName,
            ByvalTypeToTypeDef = lookup.ByvalTypeToTypeDef,
            LastAccessTick = accessTick
        };

        return (index, new Il2CppMetadataLoadOutcome
        {
            Success = true,
            MetadataVersion = version,
            TypeCount = totalTypes,
            ImageCount = images.Length
        });
    }

    private static int CompactMethodRowSize(IndexWidths w)
    {
        return 20 + w.TypeDefinition + w.Type + w.Parameter + w.GenericContainer;
    }

    private static string? FindCompactRowSizeMismatch(Func<CompactTable, CompactSection> table, IndexWidths w, bool isUnity6000_5)
    {
        var expected = new (CompactTable Table, int RowSize)[]
        {
            (CompactTable.Events, 8 + w.Type + 3 * w.Method),
            (CompactTable.Properties, 12 + 2 * w.Method),
            (CompactTable.Methods, CompactMethodRowSize(w)),
            (CompactTable.ParameterDefaultValues, w.Parameter + w.Type + w.DefaultValueData),
            (CompactTable.FieldDefaultValues, w.Field + w.Type + w.DefaultValueData),
            (CompactTable.FieldMarshaledSizes, w.Field + w.Type + 4),
            (CompactTable.Parameters, 8 + w.Type),
            (CompactTable.Fields, 8 + w.Type),
            (CompactTable.GenericParameters, w.GenericContainer + 12),
            (CompactTable.GenericParameterConstraints, w.Type),
            (CompactTable.GenericContainers, isUnity6000_5 ? 7 + w.GenericParameter : 16),
            (CompactTable.NestedTypes, 4),
            (CompactTable.Interfaces, w.Type),
            (CompactTable.InterfaceOffsets, w.Type + 4),
            (CompactTable.TypeDefinitions, 40 + 3 * w.Type + w.GenericContainer + w.Field + w.Method + w.Event + w.Property + w.NestedType + 2 * w.Interfaces),
            (CompactTable.Images, 28 + 2 * w.TypeDefinition + w.Method),
            (CompactTable.FieldRefs, w.Type + w.Field),
            (CompactTable.AttributeDataRanges, 8)
        };

        foreach (var (name, rowSize) in expected)
        {
            var section = table(name);
            if ((long)section.Count * rowSize != section.Size)
            {
                var observed = section.Count > 0
                    ? $"{section.Size / (double)section.Count:0.##} bytes per row"
                    : $"{section.Size} bytes and no rows";
                return $"table '{name}' has {observed}, expected {rowSize} with a {w.Type}-byte TypeIndex";
            }
        }

        return null;
    }

    private static (MetadataIndex? Index, Il2CppMetadataLoadOutcome Outcome) Fail(string message, bool isObfuscated)
    {
        return (null, new Il2CppMetadataLoadOutcome
        {
            Success = false,
            IsObfuscatedOrUnsupported = isObfuscated,
            DiagnosticMessage = message
        });
    }

    private static bool TryResolveTablesAndTransform(
        FileStream stream,
        long fileLength,
        long maxTableEnd,
        TableSlice[] pairs,
        TableSlice stringsSlice,
        TableSlice typeDefsSlice,
        CancellationToken cancellationToken,
        out long bodyOffsetDelta,
        out byte[]? bodyXorKey,
        out byte[] stringBytes,
        out ImageEntry[] images,
        out int imagesPairIndex,
        out int totalTypes,
        out int typeDefStructSize,
        out byte[] typeDefBytes)
    {
        bodyOffsetDelta = 0;
        bodyXorKey = null;
        stringBytes = Array.Empty<byte>();
        images = Array.Empty<ImageEntry>();
        imagesPairIndex = 20;
        totalTypes = 0;
        typeDefStructSize = 0;
        typeDefBytes = Array.Empty<byte>();

        var candidateShifts = new List<long>(2) { 0 };
        var trailingDelta = fileLength - maxTableEnd;
        if (trailingDelta > 0 && trailingDelta <= 4096)
        {
            candidateShifts.Add(trailingDelta);
        }

        // 1. Standard plaintext (and plaintext with post-header shift)
        foreach (var shift in candidateShifts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryValidateTablesWithTransform(
                    stream,
                    fileLength,
                    pairs,
                    stringsSlice,
                    typeDefsSlice,
                    shift,
                    xorKey: null,
                    out stringBytes,
                    out images,
                    out imagesPairIndex,
                    out totalTypes,
                    out typeDefStructSize,
                    out typeDefBytes))
            {
                bodyOffsetDelta = shift;
                bodyXorKey = null;
                return true;
            }
        }

        // 2. Generic repeating-XOR detection (periods 1, 2, 4, 8, 16, 32) derived purely
        //    from UTF-8/ASCII string-table frequency + binary-table zero-byte frequency,
        //    validated strictly by image & TypeDefinition metadata token tables.
        int[] xorPeriods = { 1, 2, 4, 8, 16, 32 };
        var methodsSlice = pairs[5];
        var fieldsSlice = pairs[11];
        foreach (var shift in candidateShifts)
        {
            var strFileOffset = (long)stringsSlice.Offset + shift;
            if (strFileOffset < 0 || strFileOffset + stringsSlice.Size > fileLength)
            {
                continue;
            }

            var sampleLen = Math.Min(stringsSlice.Size, 32 * 1024);
            if (sampleLen < 32)
            {
                continue;
            }

            var sample = ReadBytesAt(stream, strFileOffset, sampleLen, xorKey: null);
            var binarySamples = CollectBinarySamples(stream, fileLength, shift, typeDefsSlice, methodsSlice, fieldsSlice);

            foreach (var period in xorPeriods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryInferRepeatingXorKey(sample, strFileOffset, binarySamples, period, out var candidateKey))
                {
                    continue;
                }

                if (TryValidateTablesWithTransform(
                        stream,
                        fileLength,
                        pairs,
                        stringsSlice,
                        typeDefsSlice,
                        shift,
                        candidateKey,
                        out stringBytes,
                        out images,
                        out imagesPairIndex,
                        out totalTypes,
                        out typeDefStructSize,
                        out typeDefBytes))
                {
                    bodyOffsetDelta = shift;
                    bodyXorKey = candidateKey;
                    return true;
                }
            }
        }

        return false;
    }

    private static List<(long FileOffset, byte[] Bytes)> CollectBinarySamples(
        FileStream stream,
        long fileLength,
        long shift,
        params TableSlice[] slices)
    {
        var results = new List<(long FileOffset, byte[] Bytes)>(slices.Length);
        foreach (var slice in slices)
        {
            if (slice.Size <= 0)
            {
                continue;
            }

            var fileOff = (long)slice.Offset + shift;
            if (fileOff < 0 || fileOff >= fileLength)
            {
                continue;
            }

            var len = (int)Math.Min(Math.Min(slice.Size, 16 * 1024), fileLength - fileOff);
            if (len >= 16)
            {
                results.Add((fileOff, ReadBytesAt(stream, fileOff, len, xorKey: null)));
            }
        }
        return results;
    }

    private static bool TryValidateTablesWithTransform(
        FileStream stream,
        long fileLength,
        TableSlice[] pairs,
        TableSlice stringsSlice,
        TableSlice typeDefsSlice,
        long bodyOffsetDelta,
        byte[]? xorKey,
        out byte[] stringBytes,
        out ImageEntry[] images,
        out int imagesPairIndex,
        out int totalTypes,
        out int typeDefStructSize,
        out byte[] typeDefBytes)
    {
        stringBytes = Array.Empty<byte>();
        images = Array.Empty<ImageEntry>();
        imagesPairIndex = 20;
        totalTypes = 0;
        typeDefStructSize = 0;
        typeDefBytes = Array.Empty<byte>();

        if ((long)stringsSlice.Offset + bodyOffsetDelta + stringsSlice.Size > fileLength
            || (long)typeDefsSlice.Offset + bodyOffsetDelta + typeDefsSlice.Size > fileLength)
        {
            return false;
        }

        stringBytes = ReadTableBytes(stream, stringsSlice, bodyOffsetDelta, xorKey);
        if (!TryDetectImagesAndTypeDefSize(
                stream,
                pairs,
                stringBytes,
                typeDefsSlice,
                bodyOffsetDelta,
                xorKey,
                out images,
                out imagesPairIndex,
                out totalTypes,
                out typeDefStructSize))
        {
            return false;
        }

        typeDefBytes = ReadTableBytes(stream, typeDefsSlice, bodyOffsetDelta, xorKey);
        return ValidateTypeDefTokens(typeDefBytes, totalTypes, typeDefStructSize);
    }

    private static bool TryInferRepeatingXorKey(
        byte[] stringSample,
        long sampleFileOffset,
        List<(long FileOffset, byte[] Bytes)> binarySamples,
        int period,
        out byte[] xorKey)
    {
        xorKey = Array.Empty<byte>();
        if (period <= 0 || stringSample.Length < period * 4)
        {
            return false;
        }

        var key = new byte[period];
        var strHist = new int[256];
        var binHist = new int[256];
        var anyNonZero = false;
        long totalValidBytes = 0;
        long totalEvaluated = 0;

        for (var lane = 0; lane < period; lane++)
        {
            Array.Clear(strHist);
            Array.Clear(binHist);
            var strLaneCount = 0;
            var binLaneCount = 0;

            var firstStrOffset = (int)((period - ((sampleFileOffset - lane) % period + period) % period) % period);
            for (var i = firstStrOffset; i < stringSample.Length; i += period)
            {
                strHist[stringSample[i]]++;
                strLaneCount++;
            }

            if (strLaneCount < 4)
            {
                return false;
            }

            for (var s = 0; s < binarySamples.Count; s++)
            {
                var (bOff, bBuf) = binarySamples[s];
                var firstBinOffset = (int)((period - ((bOff - lane) % period + period) % period) % period);
                for (var i = firstBinOffset; i < bBuf.Length; i += period)
                {
                    binHist[bBuf[i]]++;
                    binLaneCount++;
                }
            }

            var bestK = -1;
            var bestScore = double.NegativeInfinity;
            var bestValidCount = 0;

            for (var k = 0; k < 256; k++)
            {
                var validCount = 0;
                var identCount = 0;
                for (var b = 0; b < 256; b++)
                {
                    var count = strHist[b];
                    if (count == 0)
                    {
                        continue;
                    }

                    var d = b ^ k;
                    if (d == 0
                        || (d >= 'A' && d <= 'Z')
                        || (d >= 'a' && d <= 'z')
                        || (d >= '0' && d <= '9')
                        || d == '_' || d == '.' || d == '<' || d == '>' || d == '`' || d == '$')
                    {
                        validCount += count;
                        identCount += count;
                    }
                    else if (d >= 0x20 && d <= 0x7E)
                    {
                        validCount += count;
                    }
                }

                var validRatio = validCount / (double)strLaneCount;
                if (validRatio < 0.94)
                {
                    continue;
                }

                var identRatio = identCount / (double)strLaneCount;
                var binZeroRatio = binLaneCount > 0 ? binHist[k] / (double)binLaneCount : 0.0;
                var score = validRatio * 10.0 + identRatio * 3.0 + binZeroRatio * 6.0;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestValidCount = validCount;
                    bestK = k;
                }
            }

            if (bestK < 0)
            {
                return false;
            }

            key[lane] = (byte)bestK;
            if (bestK != 0)
            {
                anyNonZero = true;
            }
            totalValidBytes += bestValidCount;
            totalEvaluated += strLaneCount;
        }

        if (!anyNonZero || totalEvaluated <= 0 || totalValidBytes < totalEvaluated * 0.96)
        {
            return false;
        }

        xorKey = key;
        return true;
    }

    private static bool TryDetectImagesAndTypeDefSize(
        FileStream stream,
        TableSlice[] pairs,
        byte[] stringBytes,
        TableSlice typeDefsSlice,
        long bodyOffsetDelta,
        byte[]? xorKey,
        out ImageEntry[] images,
        out int imagesPairIndex,
        out int totalTypes,
        out int typeDefStructSize)
    {
        images = Array.Empty<ImageEntry>();
        imagesPairIndex = 20;
        totalTypes = 0;
        typeDefStructSize = 0;

        int[] pairCandidates = { 20, 21, 22 };
        int[] structSizeCandidates = { 40, 32, 36, 44, 48 };
        int[] validTypeDefSizes = { 88, 92, 104, 100, 96, 84, 108, 112 };

        foreach (var pairIdx in pairCandidates)
        {
            if (pairIdx >= pairs.Length)
            {
                continue;
            }

            var slice = pairs[pairIdx];
            if (slice.Size < 28 || slice.Size > 8 * 1024 * 1024 || (long)slice.Offset + bodyOffsetDelta + slice.Size > stream.Length)
            {
                continue;
            }

            var raw = ReadTableBytes(stream, slice, bodyOffsetDelta, xorKey);
            foreach (var imgStructSize in structSizeCandidates)
            {
                if (slice.Size % imgStructSize != 0)
                {
                    continue;
                }

                var count = slice.Size / imgStructSize;
                if (count < 1 || count > 50_000)
                {
                    continue;
                }

                var valid = true;
                var dllNameHits = 0;
                var runningTypes = 0;
                var parsed = new ImageEntry[count];

                for (var i = 0; i < count; i++)
                {
                    var off = i * imgStructSize;
                    var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off, 4));
                    var typeStart = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off + 8, 4));
                    var typeCount = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off + 12, 4));

                    var name = ReadNullTerminatedUtf8(stringBytes, nameIdx);
                    if (string.IsNullOrWhiteSpace(name) || !LooksLikeValidIdentifierOrFileName(name))
                    {
                        valid = false;
                        break;
                    }

                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        dllNameHits++;
                    }

                    if (typeStart != runningTypes || typeCount < 0 || typeCount > 5_000_000)
                    {
                        valid = false;
                        break;
                    }

                    var caStart = imgStructSize >= 40 ? BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off + 32, 4)) : -1;
                    var caCount = imgStructSize >= 40 ? BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off + 36, 4)) : 0;
                    if (caStart < 0 || caCount <= 0)
                    {
                        caStart = -1;
                        caCount = 0;
                    }

                    runningTypes += typeCount;
                    parsed[i] = new ImageEntry(name, typeStart, typeCount, caStart, caCount);
                }

                if (!valid || runningTypes <= 0 || dllNameHits == 0)
                {
                    continue;
                }

                if (typeDefsSlice.Size % runningTypes != 0)
                {
                    continue;
                }

                var tdSize = typeDefsSlice.Size / runningTypes;
                if (!validTypeDefSizes.Contains(tdSize))
                {
                    continue;
                }

                images = parsed;
                imagesPairIndex = pairIdx;
                totalTypes = runningTypes;
                typeDefStructSize = tdSize;
                return true;
            }
        }

        return false;
    }

    private static bool ValidateTypeDefTokens(byte[] typeDefBytes, int totalTypes, int structSize)
    {
        var checkCount = Math.Min(totalTypes, 8);
        if (checkCount <= 0)
        {
            return false;
        }

        var tokenHits = 0;
        for (var i = 0; i < checkCount; i++)
        {
            var token = BinaryPrimitives.ReadUInt32LittleEndian(typeDefBytes.AsSpan(i * structSize + structSize - 4, 4));
            if ((token >> 24) == 0x02)
            {
                tokenHits++;
            }
        }

        return tokenHits == checkCount;
    }

    private static MethodLayout DetectMethodLayout(
        FileStream stream,
        TableSlice methodsSlice,
        byte[] stringBytes,
        int version,
        int totalTypes,
        long bodyOffsetDelta,
        byte[]? xorKey)
    {
        if (methodsSlice.Size <= 0)
        {
            return new MethodLayout(version >= 31 ? 36 : 32, version >= 31 ? 16 : 12, version >= 31 ? 20 : 16);
        }

        var candidates = version >= 31
            ? new[] { 36, 32, 40, 48, 56 }
            : (version >= 27 ? new[] { 32, 36, 40, 48, 56 } : new[] { 32, 56, 48, 36, 40 });

        var sampleLen = Math.Min(methodsSlice.Size, 56 * 8);
        var sample = ReadBytesAt(stream, methodsSlice.Offset + bodyOffsetDelta, sampleLen, xorKey);

        foreach (var msz in candidates)
        {
            if (methodsSlice.Size % msz != 0)
            {
                continue;
            }

            var checkCount = Math.Min(methodsSlice.Size / msz, sampleLen / msz);
            if (checkCount <= 0)
            {
                continue;
            }

            var ok = true;
            var hasReturnParamTokenAt12 = false;
            for (var i = 0; i < checkCount; i++)
            {
                var off = i * msz;
                var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(sample.AsSpan(off, 4));
                var declaringType = BinaryPrimitives.ReadInt32LittleEndian(sample.AsSpan(off + 4, 4));
                var token = BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(off + msz - 12, 4));

                if ((token >> 24) != 0x06
                    || declaringType < 0
                    || declaringType >= totalTypes
                    || !IsReadableMetadataString(stringBytes, nameIdx))
                {
                    ok = false;
                    break;
                }

                if (msz == 36)
                {
                    var fieldAt12 = BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(off + 12, 4));
                    if ((fieldAt12 >> 24) == 0x08)
                    {
                        hasReturnParamTokenAt12 = true;
                    }
                }
            }

            if (ok)
            {
                if (msz == 36 && (version >= 31 || hasReturnParamTokenAt12))
                {
                    return new MethodLayout(36, ParameterStartOffset: 16, GenericContainerIndexOffset: 20, MethodIndexOffset: -1);
                }

                if (msz == 56)
                {
                    return new MethodLayout(56, ParameterStartOffset: 12, GenericContainerIndexOffset: 20, MethodIndexOffset: 24);
                }

                if (msz == 48)
                {
                    return new MethodLayout(48, ParameterStartOffset: 12, GenericContainerIndexOffset: 16, MethodIndexOffset: 20);
                }

                return new MethodLayout(msz, ParameterStartOffset: 12, GenericContainerIndexOffset: 16, MethodIndexOffset: -1);
            }
        }

        return version >= 31
            ? new MethodLayout(36, 16, 20)
            : new MethodLayout(32, 12, 16);
    }

    private static int DetectFixedTableStructSize(
        FileStream stream,
        TableSlice slice,
        byte[] stringBytes,
        int[] candidates,
        int tokenOffsetFromEnd,
        uint expectedTokenTable,
        bool checkNameAt0,
        long bodyOffsetDelta,
        byte[]? xorKey)
    {
        if (slice.Size <= 0)
        {
            return candidates[0];
        }

        var maxCand = candidates.Max();
        var sampleLen = Math.Min(slice.Size, maxCand * 8);
        var sample = ReadBytesAt(stream, slice.Offset + bodyOffsetDelta, sampleLen, xorKey);

        foreach (var cand in candidates)
        {
            if (slice.Size % cand != 0)
            {
                continue;
            }

            var checkCount = Math.Min(slice.Size / cand, sampleLen / cand);
            if (checkCount <= 0)
            {
                continue;
            }

            var ok = true;
            for (var i = 0; i < checkCount; i++)
            {
                var off = i * cand;
                var token = BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(off + cand - tokenOffsetFromEnd, 4));
                if ((token >> 24) != expectedTokenTable)
                {
                    ok = false;
                    break;
                }

                if (checkNameAt0)
                {
                    var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(sample.AsSpan(off, 4));
                    if (!IsReadableMetadataString(stringBytes, nameIdx))
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (ok)
            {
                return cand;
            }
        }

        return candidates[0];
    }

    private static int DetectParamStructSize(FileStream stream, TableSlice parametersSlice, long bodyOffsetDelta, byte[]? xorKey)
    {
        if (parametersSlice.Size <= 0)
        {
            return 12;
        }

        int[] candidates = { 12, 16 };
        var sampleLen = Math.Min(parametersSlice.Size, 16 * 8);
        var sample = ReadBytesAt(stream, parametersSlice.Offset + bodyOffsetDelta, sampleLen, xorKey);

        foreach (var cand in candidates)
        {
            if (parametersSlice.Size % cand != 0)
            {
                continue;
            }

            var checkCount = Math.Min(parametersSlice.Size / cand, sampleLen / cand);
            if (checkCount <= 0)
            {
                continue;
            }

            var ok = true;
            for (var i = 0; i < checkCount; i++)
            {
                var token = BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(i * cand + 4, 4));
                if ((token >> 24) != 0x08)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return cand;
            }
        }

        return 12;
    }

    private static TypeDefLayout DetectTypeDefLayout(
        int version,
        int structSize,
        byte[] typeDefBytes,
        int totalTypes,
        int totalMethods,
        int totalFields)
    {
        var presetV27Plus = new TypeDefLayout(
            StructSize: structSize,
            ByvalTypeIndexOffset: 8,
            DeclaringTypeIndexOffset: 12,
            ParentIndexOffset: 16,
            ElementTypeIndexOffset: 20,
            GenericContainerIndexOffset: 24,
            FlagsOffset: 28,
            FieldStartOffset: 32,
            MethodStartOffset: 36,
            EventStartOffset: 40,
            PropertyStartOffset: 44,
            NestedTypesStartOffset: 48,
            InterfacesStartOffset: 52);

        var presetV24Modern = new TypeDefLayout(
            StructSize: structSize,
            ByvalTypeIndexOffset: 8,
            DeclaringTypeIndexOffset: 16,
            ParentIndexOffset: 20,
            ElementTypeIndexOffset: 24,
            GenericContainerIndexOffset: 28,
            FlagsOffset: 32,
            FieldStartOffset: 36,
            MethodStartOffset: 40,
            EventStartOffset: 44,
            PropertyStartOffset: 48,
            NestedTypesStartOffset: 52,
            InterfacesStartOffset: 56);

        var presetV24Legacy104 = new TypeDefLayout(
            StructSize: structSize,
            ByvalTypeIndexOffset: 12,
            DeclaringTypeIndexOffset: 20,
            ParentIndexOffset: 24,
            ElementTypeIndexOffset: 28,
            GenericContainerIndexOffset: 40,
            FlagsOffset: 44,
            FieldStartOffset: 48,
            MethodStartOffset: 52,
            EventStartOffset: 56,
            PropertyStartOffset: 60,
            NestedTypesStartOffset: 64,
            InterfacesStartOffset: 68);

        var presetV24Legacy100 = new TypeDefLayout(
            StructSize: structSize,
            ByvalTypeIndexOffset: 8,
            DeclaringTypeIndexOffset: 16,
            ParentIndexOffset: 20,
            ElementTypeIndexOffset: 24,
            GenericContainerIndexOffset: 36,
            FlagsOffset: 40,
            FieldStartOffset: 44,
            MethodStartOffset: 48,
            EventStartOffset: 52,
            PropertyStartOffset: 56,
            NestedTypesStartOffset: 60,
            InterfacesStartOffset: 64);

        var ordered = version >= 27
            ? new[] { presetV27Plus, presetV24Modern, presetV24Legacy104 }
            : (structSize == 104
                ? new[] { presetV24Legacy104, presetV24Modern, presetV27Plus }
                : (structSize == 100
                    ? new[] { presetV24Legacy100, presetV24Modern, presetV24Legacy104 }
                    : new[] { presetV24Modern, presetV27Plus, presetV24Legacy104 }));

        var probeLimit = Math.Min(totalTypes, 64);
        foreach (var candidate in ordered)
        {
            if (candidate.InterfacesStartOffset + 4 > candidate.MethodCountOffset)
            {
                continue;
            }

            var valid = true;
            for (var i = 0; i < probeLimit; i++)
            {
                var off = i * structSize;
                var mCount = BinaryPrimitives.ReadUInt16LittleEndian(typeDefBytes.AsSpan(off + candidate.MethodCountOffset, 2));
                var fCount = BinaryPrimitives.ReadUInt16LittleEndian(typeDefBytes.AsSpan(off + candidate.FieldCountOffset, 2));
                var mStart = BinaryPrimitives.ReadInt32LittleEndian(typeDefBytes.AsSpan(off + candidate.MethodStartOffset, 4));
                var fStart = BinaryPrimitives.ReadInt32LittleEndian(typeDefBytes.AsSpan(off + candidate.FieldStartOffset, 4));

                if (mCount > 0 && (mStart < 0 || (long)mStart + mCount > totalMethods))
                {
                    valid = false;
                    break;
                }

                if (fCount > 0 && (fStart < 0 || (long)fStart + fCount > totalFields))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                return candidate;
            }
        }

        return ordered[0];
    }

    private static int FindMatchingType(
        MetadataIndex index,
        string? assemblyName,
        string? ns,
        string className)
    {
        var cleanNs = ns?.Trim() ?? string.Empty;
        var cleanClass = className.Trim();
        var normalizedClass = cleanClass.Replace('/', '.').Replace('+', '.');
        var baseClass = StripGenericArity(normalizedClass);
        var leafName = normalizedClass.Contains('.')
            ? normalizedClass[(normalizedClass.LastIndexOf('.') + 1)..]
            : normalizedClass;
        var leafBase = StripGenericArity(leafName);
        var cleanAsm = NormalizeAssemblyName(assemblyName);

        // 1. Exact full name (Namespace.ClassName)
        var fullCandidates = new List<string>(4);
        if (!string.IsNullOrEmpty(cleanNs))
        {
            fullCandidates.Add($"{cleanNs}.{cleanClass}");
            if (!string.Equals(normalizedClass, cleanClass, StringComparison.Ordinal))
            {
                fullCandidates.Add($"{cleanNs}.{normalizedClass}");
            }
            fullCandidates.Add($"{cleanNs}.{baseClass}");
        }
        fullCandidates.Add(cleanClass);
        if (!string.Equals(baseClass, cleanClass, StringComparison.Ordinal))
        {
            fullCandidates.Add(baseClass);
        }

        foreach (var key in fullCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (index.ByFullName.TryGetValue(key, out var list) && list.Count > 0)
            {
                return PickBestCandidate(index, list, cleanAsm, cleanNs, normalizedClass);
            }
        }

        // 2. Simple / leaf name match (handles nested types Outer+Inner / Outer.Inner and missing namespace)
        var simpleCandidates = new[] { cleanClass, baseClass, leafName, leafBase }
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var simpleKey in simpleCandidates)
        {
            if (index.BySimpleName.TryGetValue(simpleKey, out var list) && list.Count > 0)
            {
                return PickBestCandidate(index, list, cleanAsm, cleanNs, normalizedClass);
            }
        }

        return -1;
    }

    private static int PickBestCandidate(
        MetadataIndex index,
        List<int> candidates,
        string cleanAsm,
        string cleanNs,
        string normalizedNestedClass)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var hasOuterPrefix = normalizedNestedClass.Contains('.');
        var bestIdx = candidates[0];
        var bestScore = int.MinValue;

        foreach (var idx in candidates)
        {
            var summary = index.Types[idx];
            var score = 0;

            if (!string.IsNullOrEmpty(cleanNs)
                && string.Equals(summary.Namespace, cleanNs, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
            else if (string.IsNullOrEmpty(cleanNs) && string.IsNullOrEmpty(summary.Namespace))
            {
                score += 10;
            }

            if (!string.IsNullOrEmpty(cleanAsm)
                && summary.ImageIndex >= 0
                && summary.ImageIndex < index.Images.Length)
            {
                var imgAsm = NormalizeAssemblyName(index.Images[summary.ImageIndex].Name);
                if (string.Equals(imgAsm, cleanAsm, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
            }

            if (hasOuterPrefix && summary.DeclaringTypeIndex >= 0)
            {
                var nestedChain = BuildNestedTypePath(index, idx);
                if (nestedChain.EndsWith(normalizedNestedClass, StringComparison.OrdinalIgnoreCase))
                {
                    score += 60;
                }
            }
            else if (!hasOuterPrefix && summary.DeclaringTypeIndex < 0)
            {
                score += 15;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = idx;
            }
        }

        return bestIdx;
    }

    private static string BuildNestedTypePath(MetadataIndex index, int typeDefIndex)
    {
        var parts = new Stack<string>();
        var current = typeDefIndex;
        var guard = 0;
        while (current >= 0 && current < index.Types.Length && guard++ < 16)
        {
            var summary = index.Types[current];
            parts.Push(summary.Name);
            if (summary.DeclaringTypeIndex < 0
                || !index.ByvalTypeToTypeDef.TryGetValue(summary.DeclaringTypeIndex, out var parentTd)
                || parentTd == current)
            {
                break;
            }
            current = parentTd;
        }

        return string.Join(".", parts);
    }

    private sealed class TypeResolutionContext
    {
        private readonly FileStream _stream;
        private readonly MetadataIndex _index;
        private readonly Il2CppBinaryIndex? _binary;
        private readonly Dictionary<int, (string Namespace, IReadOnlyList<string> TypeChain)?> _chainCache = new();
        private readonly Dictionary<int, string?> _genericParamCache = new();

        public TypeResolutionContext(FileStream stream, MetadataIndex index, Il2CppBinaryIndex? binary)
        {
            _stream = stream;
            _index = index;
            _binary = binary;
        }

        public bool TryResolveType(int typeIndex, out Il2CppResolvedType resolved)
        {
            if (_binary != null && _binary.TryResolveType(typeIndex, ResolveTypeDefChain, ResolveGenericParamName, out resolved))
            {
                return true;
            }

            if (typeIndex >= 0 && _index.ByvalTypeToTypeDef.TryGetValue(typeIndex, out var tdIdx))
            {
                var chain = ResolveTypeDefChain(tdIdx);
                if (chain != null && chain.Value.TypeChain.Count > 0)
                {
                    var joined = string.Join(".", chain.Value.TypeChain);
                    var fullName = !string.IsNullOrWhiteSpace(chain.Value.Namespace)
                        ? $"{chain.Value.Namespace}.{joined}"
                        : joined;
                    resolved = new Il2CppResolvedType(fullName, 0, IsByRef: false, IsPinned: false);
                    return true;
                }
            }

            resolved = default;
            return false;
        }

        public string? ResolveTypeName(int typeIndex, bool includeNamespace)
        {
            if (!TryResolveType(typeIndex, out var resolved))
            {
                return null;
            }

            if (includeNamespace)
            {
                return resolved.TypeName;
            }

            var dot = resolved.TypeName.LastIndexOf('.');
            return dot >= 0 && dot + 1 < resolved.TypeName.Length
                ? resolved.TypeName.Substring(dot + 1)
                : resolved.TypeName;
        }

        private (string Namespace, IReadOnlyList<string> TypeChain)? ResolveTypeDefChain(int typeDefIndex)
        {
            if (typeDefIndex < 0 || typeDefIndex >= _index.Types.Length)
            {
                return null;
            }

            if (_chainCache.TryGetValue(typeDefIndex, out var cached))
            {
                return cached;
            }

            var parts = new Stack<string>();
            var current = typeDefIndex;
            var outerNamespace = string.Empty;
            var guard = 0;
            while (current >= 0 && current < _index.Types.Length && guard++ < 16)
            {
                var summary = _index.Types[current];
                parts.Push(summary.Name);
                outerNamespace = summary.Namespace;
                if (summary.DeclaringTypeIndex < 0
                    || !_index.ByvalTypeToTypeDef.TryGetValue(summary.DeclaringTypeIndex, out var parentTd)
                    || parentTd == current)
                {
                    break;
                }
                current = parentTd;
            }

            var result = (outerNamespace, (IReadOnlyList<string>)parts.ToArray());
            _chainCache[typeDefIndex] = result;
            return result;
        }

        private string? ResolveGenericParamName(int genericParamIndex)
        {
            var gpSize = _index.GenericParameterStructSize;
            var nameOffset = _index.Widths.GenericContainer;
            if (genericParamIndex < 0 || (long)(genericParamIndex + 1) * gpSize > _index.GenericParameters.Size)
            {
                return null;
            }

            if (_genericParamCache.TryGetValue(genericParamIndex, out var cached))
            {
                return cached;
            }

            var buf = ReadBytesAt(
                _stream,
                _index.GenericParameters.Offset + _index.BodyOffsetDelta + (long)genericParamIndex * gpSize,
                gpSize,
                _index.BodyXorKey);
            if (buf.Length < gpSize)
            {
                _genericParamCache[genericParamIndex] = null;
                return null;
            }

            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(nameOffset, 4));
            var name = ReadStringAt(_stream, _index, nameIdx);
            if (string.IsNullOrWhiteSpace(name))
            {
                var num = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(nameOffset + 8, 2));
                name = $"T{num}";
            }

            _genericParamCache[genericParamIndex] = name;
            return name;
        }
    }

    private static ScriptDecompilationResult ReconstructTypeResult(
        MetadataIndex index,
        Il2CppBinaryIndex? binaryIndex,
        int typeDefIndex,
        IReadOnlyDictionary<string, string>? fieldTypeOverrides,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(index.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024);

        var tdLayout = index.TypeDefLayout;
        var tdBytes = ReadBytesAt(stream, index.TypeDefinitions.Offset + index.BodyOffsetDelta + (long)typeDefIndex * tdLayout.StructSize, tdLayout.StructSize, index.BodyXorKey);

        var summary = index.Types[typeDefIndex];
        var hasImage = summary.ImageIndex >= 0 && summary.ImageIndex < index.Images.Length;
        var imageEntry = hasImage ? index.Images[summary.ImageIndex] : default;
        var assemblyName = hasImage ? imageEntry.Name : string.Empty;

        var w = index.Widths;
        var parentTypeIndex = ReadIndex(tdBytes, tdLayout.ParentIndexOffset, w.Type);
        var elementTypeIndex = tdLayout.ElementTypeIndexOffset >= 0
            ? ReadIndex(tdBytes, tdLayout.ElementTypeIndexOffset, w.Type)
            : -1;
        var genericContainerIdx = ReadIndex(tdBytes, tdLayout.GenericContainerIndexOffset, w.GenericContainer);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(tdBytes.AsSpan(tdLayout.FlagsOffset, 4));
        var fieldStart = ReadIndex(tdBytes, tdLayout.FieldStartOffset, w.Field);
        var methodStart = ReadIndex(tdBytes, tdLayout.MethodStartOffset, w.Method);
        var eventStart = ReadIndex(tdBytes, tdLayout.EventStartOffset, w.Event);
        var propertyStart = ReadIndex(tdBytes, tdLayout.PropertyStartOffset, w.Property);
        var nestedStart = ReadIndex(tdBytes, tdLayout.NestedTypesStartOffset, w.NestedType);
        var interfacesStart = ReadIndex(tdBytes, tdLayout.InterfacesStartOffset, w.Interfaces);

        var methodCount = BinaryPrimitives.ReadUInt16LittleEndian(tdBytes.AsSpan(tdLayout.MethodCountOffset, 2));
        var propertyCount = BinaryPrimitives.ReadUInt16LittleEndian(tdBytes.AsSpan(tdLayout.PropertyCountOffset, 2));
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(tdBytes.AsSpan(tdLayout.FieldCountOffset, 2));
        var eventCount = BinaryPrimitives.ReadUInt16LittleEndian(tdBytes.AsSpan(tdLayout.EventCountOffset, 2));
        var nestedCount = BinaryPrimitives.ReadUInt16LittleEndian(tdBytes.AsSpan(tdLayout.NestedTypeCountOffset, 2));
        var interfacesCount = BinaryPrimitives.ReadUInt16LittleEndian(tdBytes.AsSpan(tdLayout.InterfacesCountOffset, 2));
        var bitfield = BinaryPrimitives.ReadUInt32LittleEndian(tdBytes.AsSpan(tdLayout.BitfieldOffset, 4));
        var typeToken = BinaryPrimitives.ReadUInt32LittleEndian(tdBytes.AsSpan(tdLayout.TokenOffset, 4));

        var isInterface = (flags & 0x00000020) != 0;
        var isValueType = (bitfield & 0x1) != 0;
        var isEnum = (bitfield & 0x2) != 0;
        var hasBinary = binaryIndex != null && (binaryIndex.HasTypes || binaryIndex.HasFieldOffsets || binaryIndex.HasMethodPointers);

        var typeResolver = new TypeResolutionContext(stream, index, binaryIndex);

        // Read methods first (needed for property/event type & modifier inference and RVA alias grouping)
        var parsedMethods = ReadMethods(
            stream,
            index,
            binaryIndex,
            typeResolver,
            imageEntry,
            assemblyName,
            methodStart,
            methodCount,
            isInterface,
            cancellationToken);
        AssignSharedRvaAliases(parsedMethods);

        var parsedProperties = ReadProperties(stream, index, propertyStart, propertyCount, parsedMethods);
        var parsedEvents = ReadEvents(stream, index, typeResolver, eventStart, eventCount, parsedMethods);
        var parsedFields = ReadFields(
            stream,
            index,
            binaryIndex,
            typeResolver,
            imageEntry,
            typeDefIndex,
            fieldStart,
            fieldCount,
            isEnum,
            summary.Namespace,
            parsedProperties,
            fieldTypeOverrides);
        var typeGenericParams = ReadGenericParameterNames(stream, index, genericContainerIdx);
        var interfaceNames = ReadInterfaceNames(stream, index, typeResolver, interfacesStart, interfacesCount);
        var nestedNames = ReadNestedTypeNames(stream, index, nestedStart, nestedCount);
        var typeRawAttrs = ReadCustomAttributesForToken(stream, index, typeResolver, imageEntry, typeToken);
        var typeDisplayAttrs = FormatDisplayAttributes(typeRawAttrs, isNotSerializedFlag: false, isSerializableFlag: (flags & 0x00002000) != 0);

        var parentTypeName = typeResolver.ResolveTypeName(parentTypeIndex, includeNamespace: true);
        string? underlyingEnumTypeName = null;
        if (isEnum)
        {
            underlyingEnumTypeName = tdLayout.ElementTypeIndexOffset >= 0
                ? typeResolver.ResolveTypeName(elementTypeIndex, includeNamespace: true)
                : parsedFields.FirstOrDefault(static f => string.Equals(f.Name, "value__", StringComparison.Ordinal))?.ResolvedTypeName;
        }
        if (!string.IsNullOrWhiteSpace(underlyingEnumTypeName))
        {
            underlyingEnumTypeName = FormatFriendlyTypeName(underlyingEnumTypeName, summary.Namespace);
        }

        var cleanDisplayName = FormatTypeNameWithGenerics(summary.Name, typeGenericParams);
        var fullTitle = !string.IsNullOrWhiteSpace(summary.Namespace)
            ? $"{summary.Namespace}.{summary.Name}"
            : summary.Name;

        var sb = new StringBuilder(2048);
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            sb.AppendLine($"// Assembly: {assemblyName}");
        }
        sb.AppendLine($"// Type: {fullTitle}");
        sb.AppendLine(hasBinary
            ? $"// Source: IL2CPP Metadata v{index.Version} + Native Binary"
            : $"// Source: IL2CPP Metadata v{index.Version}");
        sb.AppendLine($"// TypeDefIndex: {typeDefIndex} | Token: 0x{typeToken:X8}");
        sb.AppendLine();

        var indent = string.Empty;
        var hasNamespace = !string.IsNullOrWhiteSpace(summary.Namespace);
        if (hasNamespace)
        {
            sb.AppendLine($"namespace {summary.Namespace}");
            sb.AppendLine("{");
            indent = "    ";
        }

        foreach (var attr in typeDisplayAttrs)
        {
            sb.AppendLine($"{indent}[{attr}]");
        }

        // Type declaration line
        var typeModifiers = FormatTypeModifiers(flags, isInterface, isValueType, isEnum);
        var typeKeyword = isInterface ? "interface" : (isEnum ? "enum" : (isValueType ? "struct" : "class"));

        var baseList = new List<string>();
        if (isEnum)
        {
            if (!string.IsNullOrWhiteSpace(underlyingEnumTypeName)
                && !string.Equals(underlyingEnumTypeName, "int", StringComparison.Ordinal))
            {
                baseList.Add(underlyingEnumTypeName);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(parentTypeName)
                && !IsImplicitBaseType(parentTypeName, isValueType, isEnum))
            {
                baseList.Add(FormatFriendlyTypeName(parentTypeName, summary.Namespace));
            }

            foreach (var iface in interfaceNames)
            {
                baseList.Add(FormatFriendlyTypeName(iface, summary.Namespace));
            }
        }

        var inheritanceSuffix = baseList.Count > 0 ? " : " + string.Join(", ", baseList) : string.Empty;
        sb.AppendLine($"{indent}{typeModifiers} {typeKeyword} {cleanDisplayName}{inheritanceSuffix}");
        sb.AppendLine($"{indent}{{");

        var memberIndent = indent + "    ";
        var wroteSection = false;
        var methodSymbols = new List<ScriptMethodSymbol>();

        if (isEnum)
        {
            var enumItems = parsedFields.Where(static f => !string.Equals(f.Name, "value__", StringComparison.Ordinal)).ToList();
            for (var i = 0; i < enumItems.Count; i++)
            {
                var ef = enumItems[i];
                var comma = i + 1 < enumItems.Count ? "," : string.Empty;
                var valuePart = !string.IsNullOrEmpty(ef.DefaultValueLiteral) ? $" = {ef.DefaultValueLiteral}" : string.Empty;
                sb.AppendLine($"{memberIndent}{ef.Name}{valuePart}{comma}");
            }
            wroteSection = enumItems.Count > 0;
        }
        else
        {
            if (parsedFields.Count > 0)
            {
                sb.AppendLine($"{memberIndent}// Fields ({parsedFields.Count})");
                foreach (var f in parsedFields)
                {
                    foreach (var attr in f.DisplayAttributes)
                    {
                        sb.AppendLine($"{memberIndent}[{attr}]");
                    }

                    var modsPrefix = !string.IsNullOrWhiteSpace(f.Modifiers)
                        ? f.Modifiers + " "
                        : (f.IsConst ? "const " : string.Empty);
                    var typePart = !string.IsNullOrEmpty(f.ResolvedTypeName)
                        ? FormatFriendlyTypeName(f.ResolvedTypeName, summary.Namespace)
                        : "/* field */";
                    var valuePart = !string.IsNullOrEmpty(f.DefaultValueLiteral) ? $" = {f.DefaultValueLiteral}" : string.Empty;

                    var commentParts = new List<string>(4);
                    if (!f.IsConst && f.FieldOffset >= 0)
                    {
                        commentParts.Add($"Offset: 0x{f.FieldOffset:X}");
                    }
                    commentParts.Add($"Token: 0x{f.Token:X8}");
                    if (f.IsSerializedInTypeTree)
                    {
                        commentParts.Add("Serialized (TypeTree)");
                    }
                    if (!string.IsNullOrWhiteSpace(f.BackingFieldHint))
                    {
                        commentParts.Add(f.BackingFieldHint);
                    }

                    sb.AppendLine($"{memberIndent}{modsPrefix}{typePart} {f.Name}{valuePart}; // {string.Join(" | ", commentParts)}");
                }
                wroteSection = true;
            }

            if (parsedProperties.Count > 0)
            {
                if (wroteSection) sb.AppendLine();
                sb.AppendLine($"{memberIndent}// Properties ({parsedProperties.Count})");
                foreach (var prop in parsedProperties)
                {
                    var propType = !string.IsNullOrEmpty(prop.TypeName)
                        ? FormatFriendlyTypeName(prop.TypeName, summary.Namespace)
                        : "/* prop */";
                    var accessors = (prop.HasGetter, prop.HasSetter) switch
                    {
                        (true, true) => "{ get; set; }",
                        (true, false) => "{ get; }",
                        (false, true) => "{ set; }",
                        _ => "{ }"
                    };
                    var mods = string.IsNullOrWhiteSpace(prop.Modifiers) ? string.Empty : prop.Modifiers + " ";

                    var propComment = $"Token: 0x{prop.Token:X8}";
                    if (!string.IsNullOrWhiteSpace(prop.AccessorsAnnotation))
                    {
                        propComment += $" | Accessors: {prop.AccessorsAnnotation}";
                    }
                    sb.AppendLine($"{memberIndent}{mods}{propType} {prop.Name} {accessors} // {propComment}");
                }
                wroteSection = true;
            }

            if (parsedEvents.Count > 0)
            {
                if (wroteSection) sb.AppendLine();
                sb.AppendLine($"{memberIndent}// Events ({parsedEvents.Count})");
                foreach (var ev in parsedEvents)
                {
                    var evType = !string.IsNullOrEmpty(ev.TypeName)
                        ? FormatFriendlyTypeName(ev.TypeName, summary.Namespace)
                        : "/* delegate */";
                    var mods = string.IsNullOrWhiteSpace(ev.Modifiers) ? string.Empty : ev.Modifiers + " ";
                    sb.AppendLine($"{memberIndent}{mods}event {evType} {ev.Name}; // Token: 0x{ev.Token:X8}");
                }
                wroteSection = true;
            }

            if (parsedMethods.Count > 0)
            {
                if (wroteSection) sb.AppendLine();
                sb.AppendLine($"{memberIndent}// Methods ({parsedMethods.Count})");
                var ctorName = StripGenericArity(summary.Name);
                var emittedSymbolRvas = new HashSet<ulong>();

                foreach (var m in parsedMethods)
                {
                    var paramText = string.Join(", ", m.Parameters.Select(p =>
                    {
                        var pType = !string.IsNullOrEmpty(p.TypeName)
                            ? FormatFriendlyTypeName(p.TypeName, summary.Namespace)
                            : "/* param */";
                        var refPrefix = FormatByRefParameterPrefix(p);
                        var pName = string.IsNullOrWhiteSpace(p.Name) ? $"arg{p.Index}" : p.Name;
                        return $"{refPrefix}{pType} {pName}";
                    }));

                    string signatureWithoutToken;
                    if (string.Equals(m.Name, ".cctor", StringComparison.Ordinal))
                    {
                        signatureWithoutToken = $"static {ctorName}()";
                    }
                    else if (string.Equals(m.Name, ".ctor", StringComparison.Ordinal))
                    {
                        var mods = string.IsNullOrWhiteSpace(m.Modifiers) ? "public" : m.Modifiers;
                        signatureWithoutToken = $"{mods} {ctorName}({paramText})";
                    }
                    else
                    {
                        var retType = !string.IsNullOrEmpty(m.ReturnTypeName)
                            ? FormatFriendlyTypeName(m.ReturnTypeName, summary.Namespace)
                            : "/* return */";
                        if (m.ReturnIsByRef)
                        {
                            retType = "ref " + retType;
                        }
                        var genSuffix = m.GenericParameters.Count > 0
                            ? $"<{string.Join(", ", m.GenericParameters)}>"
                            : string.Empty;
                        var mods = string.IsNullOrWhiteSpace(m.Modifiers) ? string.Empty : m.Modifiers + " ";
                        signatureWithoutToken = $"{mods}{retType} {m.Name}{genSuffix}({paramText})";
                    }

                    if (m.Rva != 0)
                    {
                        if (m.IsAlias && !string.IsNullOrWhiteSpace(m.CanonicalAliasOf))
                        {
                            sb.AppendLine($"{memberIndent}// RVA: 0x{m.Rva:X8} (alias of {m.CanonicalAliasOf})");
                        }
                        else
                        {
                            sb.AppendLine($"{memberIndent}// RVA: 0x{m.Rva:X8} | Offset: 0x{m.FileOffset:X8} | VA: 0x{m.Va:X}");
                            if (m.AliasNames is { Count: > 0 })
                            {
                                sb.AppendLine($"{memberIndent}// Aliases ({m.AliasNames.Count}): {string.Join(", ", m.AliasNames)}");
                            }

                            if (emittedSymbolRvas.Add(m.Rva))
                            {
                                var symbolSig = m.AliasNames is { Count: > 0 }
                                    ? $"{signatureWithoutToken} /* aliases: {string.Join(", ", m.AliasNames)} */"
                                    : signatureWithoutToken;
                                methodSymbols.Add(new ScriptMethodSymbol
                                {
                                    ClassName = fullTitle,
                                    MethodName = m.Name,
                                    FullName = $"{fullTitle}$${m.Name}",
                                    Signature = symbolSig,
                                    Address = m.Rva,
                                    Offset = m.FileOffset,
                                    VirtualAddress = m.Va,
                                    Source = "IL2CPP Binary"
                                });
                            }
                        }
                    }

                    sb.AppendLine($"{memberIndent}{signatureWithoutToken}; // Token: 0x{m.Token:X8}");
                }
                wroteSection = true;
            }

            if (nestedNames.Count > 0)
            {
                if (wroteSection) sb.AppendLine();
                sb.AppendLine($"{memberIndent}// Nested Types ({nestedNames.Count}): {string.Join(", ", nestedNames)}");
            }
        }

        sb.AppendLine($"{indent}}}");
        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        var badge = hasBinary
            ? $"IL2CPP (metadata v{index.Version} + binary)"
            : $"IL2CPP (metadata v{index.Version})";
        var summaryParts = new List<string>
        {
            hasBinary
                ? $"global-metadata.dat (v{index.Version}) + {binaryIndex!.FileName}"
                : $"global-metadata.dat (v{index.Version})"
        };
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            summaryParts.Add(assemblyName);
        }
        summaryParts.Add($"{fieldCount}F / {propertyCount}P / {methodCount}M");
        if (methodSymbols.Count > 0)
        {
            summaryParts.Add($"{methodSymbols.Count} RVA{(methodSymbols.Count == 1 ? string.Empty : "s")}");
        }

        return new ScriptDecompilationResult
        {
            Success = true,
            SourceKind = ScriptSourceKind.Il2CppGlobalMetadata,
            BackendBadge = badge,
            Title = fullTitle,
            AssemblyName = assemblyName,
            Namespace = summary.Namespace,
            ClassName = summary.Name,
            SourceFilePath = index.FilePath,
            SourceCode = sb.ToString().TrimEnd(),
            SummaryText = string.Join(" • ", summaryParts),
            DiagnosticMessage = string.Empty,
            TypeDefIndex = typeDefIndex,
            MethodSymbols = methodSymbols
        };
    }

    private sealed record ParsedParam(
        int Index,
        string Name,
        string? TypeName,
        ushort Attributes,
        bool IsByRef,
        bool IsReadOnly);

    private sealed class ParsedMethod
    {
        public required string Name { get; init; }
        public required string Modifiers { get; init; }
        public string? ReturnTypeName { get; init; }
        public bool ReturnIsByRef { get; init; }
        public uint Flags { get; init; }
        public uint Token { get; init; }
        public int MethodIndex { get; init; } = -1;
        public ulong Rva { get; init; }
        public ulong FileOffset { get; init; }
        public ulong Va { get; init; }
        public required List<string> GenericParameters { get; init; }
        public required List<ParsedParam> Parameters { get; init; }
        public bool IsAlias { get; set; }
        public string? CanonicalAliasOf { get; set; }
        public List<string>? AliasNames { get; set; }
    }

    private sealed record ParsedProperty(
        string Name,
        string Modifiers,
        string? TypeName,
        bool HasGetter,
        bool HasSetter,
        uint Token,
        string? GetterName,
        string? SetterName,
        string? ReadableAccessorName,
        string? AccessorsAnnotation);

    private sealed record ParsedEvent(
        string Name,
        string Modifiers,
        string? TypeName,
        uint Token);

    private sealed class ParsedField
    {
        public required string Name { get; init; }
        public string Modifiers { get; init; } = string.Empty;
        public string? ResolvedTypeName { get; init; }
        public string? DefaultValueLiteral { get; init; }
        public uint Token { get; init; }
        public int FieldOffset { get; init; } = -1;
        public bool IsConst { get; init; }
        public bool IsSerializedInTypeTree { get; init; }
        public required List<string> DisplayAttributes { get; init; }
        public string? BackingFieldHint { get; set; }
    }

    private static List<ParsedMethod> ReadMethods(
        FileStream stream,
        MetadataIndex index,
        Il2CppBinaryIndex? binaryIndex,
        TypeResolutionContext typeResolver,
        ImageEntry imageEntry,
        string assemblyName,
        int methodStart,
        int methodCount,
        bool declaringIsInterface,
        CancellationToken cancellationToken)
    {
        var results = new List<ParsedMethod>(methodCount);
        var ml = index.MethodLayout;
        if (methodCount <= 0 || methodStart < 0 || ml.StructSize <= 0)
        {
            return results;
        }

        var totalMethods = index.Methods.Size / ml.StructSize;
        if (methodStart >= totalMethods)
        {
            return results;
        }

        var safeCount = Math.Min(methodCount, totalMethods - methodStart);
        var buf = ReadBytesAt(stream, index.Methods.Offset + index.BodyOffsetDelta + (long)methodStart * ml.StructSize, safeCount * ml.StructSize, index.BodyXorKey);
        var totalParams = index.ParameterStructSize > 0 ? index.Parameters.Size / index.ParameterStructSize : 0;
        var w = index.Widths;
        var paramTypeOffset = index.ParameterStructSize - w.Type;

        for (var i = 0; i < safeCount; i++)
        {
            if ((i & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var off = i * ml.StructSize;
            var row = buf.AsSpan(off, ml.StructSize);
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(0, 4));
            var returnTypeIdx = ReadIndex(row, ml.ReturnTypeOffset, w.Type);
            var paramStart = ReadIndex(row, ml.ParameterStartOffset, w.Parameter);
            var genContainerIdx = ReadIndex(row, ml.GenericContainerIndexOffset, w.GenericContainer);
            var methodIdx = ml.MethodIndexOffset >= 0 && ml.MethodIndexOffset + 4 <= ml.StructSize
                ? BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + ml.MethodIndexOffset, 4))
                : -1;
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + ml.TokenOffset, 4));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + ml.FlagsOffset, 2));
            var paramCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + ml.ParameterCountOffset, 2));

            var methodName = ReadStringAt(stream, index, nameIdx) ?? $"Method_{i}";
            var hasRet = typeResolver.TryResolveType(returnTypeIdx, out var resolvedRet);
            var returnType = hasRet ? resolvedRet.TypeName : null;
            var returnIsByRef = hasRet && resolvedRet.IsByRef;
            var genParams = ReadGenericParameterNames(stream, index, genContainerIdx);

            ulong rva = 0;
            ulong fileOffset = 0;
            ulong va = 0;
            if (binaryIndex != null)
            {
                binaryIndex.TryGetMethodRva(assemblyName, token, methodIdx, out rva, out fileOffset, out va);
            }

            var parameters = new List<ParsedParam>(paramCount);
            if (paramCount > 0 && paramStart >= 0 && paramStart < totalParams && index.ParameterStructSize > 0)
            {
                var safeParamCount = Math.Min((int)paramCount, totalParams - paramStart);
                var pBuf = ReadBytesAt(
                    stream,
                    index.Parameters.Offset + index.BodyOffsetDelta + (long)paramStart * index.ParameterStructSize,
                    safeParamCount * index.ParameterStructSize,
                    index.BodyXorKey);
                for (var p = 0; p < safeParamCount; p++)
                {
                    var pOff = p * index.ParameterStructSize;
                    var pNameIdx = BinaryPrimitives.ReadInt32LittleEndian(pBuf.AsSpan(pOff, 4));
                    var pToken = BinaryPrimitives.ReadUInt32LittleEndian(pBuf.AsSpan(pOff + 4, 4));
                    var pTypeIdx = ReadIndex(pBuf, pOff + paramTypeOffset, w.Type);
                    var pName = ReadStringAt(stream, index, pNameIdx) ?? $"arg{p}";
                    var hasParamType = typeResolver.TryResolveType(pTypeIdx, out var resolvedParam);
                    var isByRef = hasParamType && resolvedParam.IsByRef;
                    var isReadOnly = false;
                    if (isByRef && pToken != 0)
                    {
                        var pAttrs = ReadCustomAttributesForToken(stream, index, typeResolver, imageEntry, pToken);
                        for (var a = 0; a < pAttrs.Count; a++)
                        {
                            if (string.Equals(pAttrs[a], "IsReadOnlyAttribute", StringComparison.Ordinal))
                            {
                                isReadOnly = true;
                                break;
                            }
                        }
                    }

                    parameters.Add(new ParsedParam(
                        p,
                        pName,
                        hasParamType ? resolvedParam.TypeName : null,
                        hasParamType ? resolvedParam.Attributes : (ushort)0,
                        isByRef,
                        isReadOnly));
                }
            }

            var mods = FormatMethodModifiers(flags, declaringIsInterface);
            results.Add(new ParsedMethod
            {
                Name = methodName,
                Modifiers = mods,
                ReturnTypeName = returnType,
                ReturnIsByRef = returnIsByRef,
                Flags = flags,
                Token = token,
                MethodIndex = methodIdx,
                Rva = rva,
                FileOffset = fileOffset,
                Va = va,
                GenericParameters = genParams,
                Parameters = parameters
            });
        }

        return results;
    }

    private static void AssignSharedRvaAliases(List<ParsedMethod> methods)
    {
        if (methods.Count < 2)
        {
            return;
        }

        var groups = new Dictionary<ulong, List<ParsedMethod>>();
        for (var i = 0; i < methods.Count; i++)
        {
            var m = methods[i];
            if (m.Rva == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(m.Rva, out var list))
            {
                list = new List<ParsedMethod>(2);
                groups[m.Rva] = list;
            }
            list.Add(m);
        }

        foreach (var group in groups.Values)
        {
            if (group.Count <= 1)
            {
                continue;
            }

            var canonical = group[0];
            var bestScore = ScoreMethodNameReadability(canonical.Name);
            for (var i = 1; i < group.Count; i++)
            {
                var score = ScoreMethodNameReadability(group[i].Name);
                if (score > bestScore)
                {
                    bestScore = score;
                    canonical = group[i];
                }
            }

            var aliases = new List<string>(group.Count - 1);
            for (var i = 0; i < group.Count; i++)
            {
                var member = group[i];
                if (ReferenceEquals(member, canonical))
                {
                    continue;
                }

                member.IsAlias = true;
                member.CanonicalAliasOf = canonical.Name;
                aliases.Add(member.Name);
            }

            canonical.AliasNames = aliases;
        }
    }

    internal static int ScoreMethodNameReadability(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -100;
        }

        if (string.Equals(name, ".ctor", StringComparison.Ordinal)
            || string.Equals(name, ".cctor", StringComparison.Ordinal))
        {
            return 100;
        }

        var hasAccessorPrefix = false;
        var core = name;
        if (name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal))
        {
            hasAccessorPrefix = true;
            core = name.Substring(4);
        }
        else if (name.StartsWith("remove_", StringComparison.Ordinal))
        {
            hasAccessorPrefix = true;
            core = name.Substring(7);
        }

        var isObfuscated = IsLikelyObfuscatedIdentifier(core);
        if (hasAccessorPrefix)
        {
            return isObfuscated ? 10 : 90;
        }

        if (isObfuscated)
        {
            return -50;
        }

        if (core is "Awake" or "Start" or "Update" or "FixedUpdate" or "LateUpdate"
            or "OnEnable" or "OnDisable" or "OnDestroy" or "OnValidate" or "Reset"
            or "OnApplicationQuit" or "OnTriggerEnter" or "OnCollisionEnter")
        {
            return 85;
        }

        var hasUpper = false;
        var hasLower = false;
        for (var i = 0; i < core.Length; i++)
        {
            var ch = core[i];
            if (ch >= 'A' && ch <= 'Z') hasUpper = true;
            else if (ch >= 'a' && ch <= 'z') hasLower = true;
        }

        if (hasUpper && hasLower)
        {
            return 70;
        }

        if (hasLower || core.Contains('_'))
        {
            return 50;
        }

        return 40;
    }

    internal static bool IsLikelyObfuscatedIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var allUpperAscii = name.Length >= 8;
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch < 0x20 || ch > 0x7E)
            {
                return true;
            }
            if (ch < 'A' || ch > 'Z')
            {
                allUpperAscii = false;
            }
        }

        return allUpperAscii;
    }

    private static string FormatByRefParameterPrefix(ParsedParam param)
    {
        if (!param.IsByRef)
        {
            return string.Empty;
        }

        if (param.IsReadOnly)
        {
            return "in ";
        }

        return (param.Attributes & 0x0003) == 0x0002 ? "out " : "ref ";
    }

    private static List<ParsedProperty> ReadProperties(
        FileStream stream,
        MetadataIndex index,
        int propertyStart,
        int propertyCount,
        List<ParsedMethod> declaringMethods)
    {
        var results = new List<ParsedProperty>(propertyCount);
        var psz = index.PropertyStructSize;
        if (propertyCount <= 0 || propertyStart < 0 || psz <= 0)
        {
            return results;
        }

        var totalProps = index.Properties.Size / psz;
        if (propertyStart >= totalProps)
        {
            return results;
        }

        var safeCount = Math.Min(propertyCount, totalProps - propertyStart);
        var buf = ReadBytesAt(stream, index.Properties.Offset + index.BodyOffsetDelta + (long)propertyStart * psz, safeCount * psz, index.BodyXorKey);
        var methodWidth = index.Widths.Method;

        for (var i = 0; i < safeCount; i++)
        {
            var off = i * psz;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var getIdx = ReadIndex(buf, off + 4, methodWidth);
            var setIdx = ReadIndex(buf, off + 4 + methodWidth, methodWidth);
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + psz - 4, 4));

            var name = ReadStringAt(stream, index, nameIdx) ?? $"Property_{i}";
            var getter = getIdx >= 0 && getIdx < declaringMethods.Count ? declaringMethods[getIdx] : null;
            var setter = setIdx >= 0 && setIdx < declaringMethods.Count ? declaringMethods[setIdx] : null;

            string? propType = getter?.ReturnTypeName;
            if (string.IsNullOrEmpty(propType) && setter != null && setter.Parameters.Count > 0)
            {
                propType = setter.Parameters[^1].TypeName;
            }

            var getterName = getter?.Name;
            var setterName = setter?.Name;
            var getterSuffix = ExtractAccessorSuffix(getterName, "get_");
            var setterSuffix = ExtractAccessorSuffix(setterName, "set_");

            string? readableAccessorName = null;
            if (!string.IsNullOrWhiteSpace(getterSuffix) && !IsLikelyObfuscatedIdentifier(getterSuffix))
            {
                readableAccessorName = getterSuffix;
            }
            else if (!string.IsNullOrWhiteSpace(setterSuffix) && !IsLikelyObfuscatedIdentifier(setterSuffix))
            {
                readableAccessorName = setterSuffix;
            }

            var accessorMismatch = (!string.IsNullOrWhiteSpace(getterSuffix) && !string.Equals(getterSuffix, name, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(setterSuffix) && !string.Equals(setterSuffix, name, StringComparison.Ordinal));
            string? accessorsAnnotation = null;
            if (accessorMismatch)
            {
                var accList = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(getterName)) accList.Add(getterName);
                if (!string.IsNullOrWhiteSpace(setterName)) accList.Add(setterName);
                if (accList.Count > 0)
                {
                    accessorsAnnotation = string.Join(", ", accList);
                }
            }

            var mods = PickPropertyModifiers(getter, setter);
            results.Add(new ParsedProperty(
                name,
                mods,
                propType,
                getIdx >= 0,
                setIdx >= 0,
                token,
                getterName,
                setterName,
                readableAccessorName,
                accessorsAnnotation));
        }

        return results;
    }

    private static string? ExtractAccessorSuffix(string? accessorName, string prefix)
    {
        if (string.IsNullOrWhiteSpace(accessorName) || !accessorName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var suffix = accessorName.Substring(prefix.Length);
        return suffix.Length > 0 ? suffix : null;
    }

    private static List<ParsedEvent> ReadEvents(
        FileStream stream,
        MetadataIndex index,
        TypeResolutionContext typeResolver,
        int eventStart,
        int eventCount,
        List<ParsedMethod> declaringMethods)
    {
        var results = new List<ParsedEvent>(eventCount);
        var esz = index.EventStructSize;
        if (eventCount <= 0 || eventStart < 0 || esz <= 0)
        {
            return results;
        }

        var totalEvents = index.Events.Size / esz;
        if (eventStart >= totalEvents)
        {
            return results;
        }

        var safeCount = Math.Min(eventCount, totalEvents - eventStart);
        var buf = ReadBytesAt(stream, index.Events.Offset + index.BodyOffsetDelta + (long)eventStart * esz, safeCount * esz, index.BodyXorKey);
        var w = index.Widths;

        for (var i = 0; i < safeCount; i++)
        {
            var off = i * esz;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var typeIdx = ReadIndex(buf, off + 4, w.Type);
            var addIdx = ReadIndex(buf, off + 4 + w.Type, w.Method);
            var removeIdx = ReadIndex(buf, off + 4 + w.Type + w.Method, w.Method);
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + esz - 4, 4));

            var name = ReadStringAt(stream, index, nameIdx) ?? $"Event_{i}";
            var addMethod = addIdx >= 0 && addIdx < declaringMethods.Count ? declaringMethods[addIdx] : null;
            var removeMethod = removeIdx >= 0 && removeIdx < declaringMethods.Count ? declaringMethods[removeIdx] : null;

            var evType = typeResolver.ResolveTypeName(typeIdx, includeNamespace: true);
            if (string.IsNullOrEmpty(evType) && addMethod != null && addMethod.Parameters.Count > 0)
            {
                evType = addMethod.Parameters[0].TypeName;
            }

            var mods = PickPropertyModifiers(addMethod, removeMethod);
            results.Add(new ParsedEvent(name, mods, evType, token));
        }

        return results;
    }

    private static List<ParsedField> ReadFields(
        FileStream stream,
        MetadataIndex index,
        Il2CppBinaryIndex? binaryIndex,
        TypeResolutionContext typeResolver,
        ImageEntry imageEntry,
        int typeDefIndex,
        int fieldStart,
        int fieldCount,
        bool isEnum,
        string currentNamespace,
        List<ParsedProperty> properties,
        IReadOnlyDictionary<string, string>? fieldTypeOverrides)
    {
        var results = new List<ParsedField>(fieldCount);
        var fsz = index.FieldStructSize;
        if (fieldCount <= 0 || fieldStart < 0 || fsz <= 0)
        {
            return results;
        }

        var totalFields = index.Fields.Size / fsz;
        if (fieldStart >= totalFields)
        {
            return results;
        }

        var safeCount = Math.Min(fieldCount, totalFields - fieldStart);
        var buf = ReadBytesAt(stream, index.Fields.Offset + index.BodyOffsetDelta + (long)fieldStart * fsz, safeCount * fsz, index.BodyXorKey);
        var fieldOffsets = !isEnum ? binaryIndex?.TryGetFieldOffsets(typeDefIndex, safeCount) : null;

        Dictionary<string, string>? propTypes = null;
        if (!isEnum && properties.Count > 0)
        {
            propTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in properties)
            {
                if (!string.IsNullOrEmpty(p.TypeName))
                {
                    propTypes.TryAdd(p.Name, p.TypeName);
                    if (!string.IsNullOrWhiteSpace(p.ReadableAccessorName))
                    {
                        propTypes.TryAdd(p.ReadableAccessorName, p.TypeName);
                    }
                }
            }
        }

        for (var i = 0; i < safeCount; i++)
        {
            var globalFieldIndex = fieldStart + i;
            var off = i * fsz;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var typeIdx = ReadIndex(buf, off + 4, index.Widths.Type);
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + fsz - 4, 4));

            var name = ReadStringAt(stream, index, nameIdx) ?? $"field_{i}";
            var hasResolved = typeResolver.TryResolveType(typeIdx, out var resolved);
            var resolvedType = hasResolved ? resolved.TypeName : null;
            var fieldAttrs = hasResolved ? resolved.Attributes : (ushort)0;
            string? defaultLiteral = null;

            if (TryFindFieldDefaultValue(stream, index, globalFieldIndex, out var defTypeIdx, out var defDataIdx))
            {
                var defTypeName = typeResolver.ResolveTypeName(defTypeIdx, includeNamespace: true);
                if (string.IsNullOrEmpty(resolvedType) && !string.IsNullOrEmpty(defTypeName))
                {
                    resolvedType = defTypeName;
                }

                defaultLiteral = TryReadDefaultValueLiteral(stream, index, defTypeName, defDataIdx);
            }

            // Enrich unresolved serialized field types using Unity's TypeTree when available,
            // and flag serialized TypeTree fields even when binary resolved the type.
            var isSerializedInTypeTree = false;
            if (fieldTypeOverrides != null && fieldTypeOverrides.TryGetValue(name, out var typeTreeFieldType))
            {
                isSerializedInTypeTree = true;
                if (string.IsNullOrEmpty(resolvedType) && !string.IsNullOrWhiteSpace(typeTreeFieldType))
                {
                    resolvedType = typeTreeFieldType;
                }
            }

            // Infer compiler-generated auto-property backing field type when available
            if (string.IsNullOrEmpty(resolvedType)
                && propTypes != null
                && TryExtractBackingFieldInnerName(name, out var propName)
                && propTypes.TryGetValue(propName, out var backingType))
            {
                resolvedType = backingType;
            }

            var isConst = (fieldAttrs & 0x0040) != 0 || !string.IsNullOrEmpty(defaultLiteral);
            var isNotSerializedFlag = (fieldAttrs & 0x0080) != 0;
            var modifiers = FormatFieldModifiers(fieldAttrs, isConst);
            var fieldOffset = fieldOffsets != null && i < fieldOffsets.Length ? fieldOffsets[i] : -1;

            IReadOnlyList<string> rawAttrs = !isEnum
                ? ReadCustomAttributesForToken(stream, index, typeResolver, imageEntry, token)
                : Array.Empty<string>();
            var displayAttrs = FormatDisplayAttributes(rawAttrs, isNotSerializedFlag, isSerializableFlag: false);

            results.Add(new ParsedField
            {
                Name = name,
                Modifiers = modifiers,
                ResolvedTypeName = resolvedType,
                DefaultValueLiteral = defaultLiteral,
                Token = token,
                FieldOffset = fieldOffset,
                IsConst = isConst,
                IsSerializedInTypeTree = isSerializedInTypeTree,
                DisplayAttributes = displayAttrs
            });
        }

        if (!isEnum && results.Count > 0 && properties.Count > 0)
        {
            CorrelateUnambiguousBackingFields(results, properties, currentNamespace);
        }

        return results;
    }

    private static bool TryExtractBackingFieldInnerName(string fieldName, out string innerName)
    {
        const string suffix = ">k__BackingField";
        if (fieldName.StartsWith('<')
            && fieldName.EndsWith(suffix, StringComparison.Ordinal)
            && fieldName.Length > suffix.Length + 1)
        {
            innerName = fieldName.Substring(1, fieldName.Length - suffix.Length - 1);
            return true;
        }

        innerName = string.Empty;
        return false;
    }

    private static void CorrelateUnambiguousBackingFields(
        List<ParsedField> fields,
        List<ParsedProperty> properties,
        string currentNamespace)
    {
        var matchedPropIndices = new HashSet<int>();
        var unmatchedBackingByType = new Dictionary<string, List<ParsedField>>(StringComparer.Ordinal);

        for (var f = 0; f < fields.Count; f++)
        {
            var field = fields[f];
            if (!TryExtractBackingFieldInnerName(field.Name, out var innerName))
            {
                continue;
            }

            var directMatched = false;
            for (var p = 0; p < properties.Count; p++)
            {
                var prop = properties[p];
                if (string.Equals(innerName, prop.Name, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(prop.ReadableAccessorName)
                        && string.Equals(innerName, prop.ReadableAccessorName, StringComparison.Ordinal)))
                {
                    matchedPropIndices.Add(p);
                    directMatched = true;
                    break;
                }
            }

            if (directMatched || string.IsNullOrWhiteSpace(field.ResolvedTypeName))
            {
                continue;
            }

            var normType = FormatFriendlyTypeName(field.ResolvedTypeName, currentNamespace);
            if (!unmatchedBackingByType.TryGetValue(normType, out var list))
            {
                list = new List<ParsedField>(1);
                unmatchedBackingByType[normType] = list;
            }
            list.Add(field);
        }

        if (unmatchedBackingByType.Count == 0)
        {
            return;
        }

        var unmatchedPropsByType = new Dictionary<string, List<ParsedProperty>>(StringComparer.Ordinal);
        for (var p = 0; p < properties.Count; p++)
        {
            if (matchedPropIndices.Contains(p))
            {
                continue;
            }

            var prop = properties[p];
            if (string.IsNullOrWhiteSpace(prop.TypeName))
            {
                continue;
            }

            var normType = FormatFriendlyTypeName(prop.TypeName, currentNamespace);
            if (!unmatchedPropsByType.TryGetValue(normType, out var list))
            {
                list = new List<ParsedProperty>(1);
                unmatchedPropsByType[normType] = list;
            }
            list.Add(prop);
        }

        foreach (var (typeKey, backingFields) in unmatchedBackingByType)
        {
            if (backingFields.Count != 1
                || !unmatchedPropsByType.TryGetValue(typeKey, out var matchingProps)
                || matchingProps.Count != 1)
            {
                continue;
            }

            var prop = matchingProps[0];
            var label = !string.IsNullOrWhiteSpace(prop.ReadableAccessorName)
                ? prop.ReadableAccessorName
                : prop.Name;
            if (!string.IsNullOrWhiteSpace(label))
            {
                backingFields[0].BackingFieldHint = $"Backing field for {label}";
            }
        }
    }

    private static string FormatFieldModifiers(ushort attrs, bool isConst)
    {
        var parts = new List<string>(3);
        var vis = (attrs & 0x0007) switch
        {
            6 => "public",
            1 => "private",
            4 => "protected",
            3 => "internal",
            5 => "protected internal",
            2 => "private protected",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(vis))
        {
            parts.Add(vis);
        }

        if (isConst)
        {
            parts.Add("const");
            return string.Join(" ", parts);
        }

        if ((attrs & 0x0010) != 0)
        {
            parts.Add("static");
        }

        if ((attrs & 0x0020) != 0)
        {
            parts.Add("readonly");
        }

        return string.Join(" ", parts);
    }

    private static List<string> ReadCustomAttributesForToken(
        FileStream stream,
        MetadataIndex index,
        TypeResolutionContext typeResolver,
        ImageEntry imageEntry,
        uint targetToken)
    {
        var result = new List<string>();
        if (targetToken == 0
            || imageEntry.CustomAttributeStart < 0
            || imageEntry.CustomAttributeCount <= 0
            || index.AttributeDataOrInfo.Size <= 0
            || index.AttributeDataRangeOrTypes.Size <= 0)
        {
            return result;
        }

        try
        {
            if (index.UsesV29CustomAttributeFormat)
            {
                ReadCustomAttributesV29(stream, index, imageEntry, targetToken, result);
            }
            else
            {
                ReadCustomAttributesLegacy(stream, index, typeResolver, imageEntry, targetToken, result);
            }
        }
        catch
        {
            result.Clear();
        }

        return result;
    }

    private static void ReadCustomAttributesV29(
        FileStream stream,
        MetadataIndex index,
        ImageEntry imageEntry,
        uint targetToken,
        List<string> results)
    {
        const int rangeEntrySize = 8;
        var rangeSlice = index.AttributeDataRangeOrTypes;
        var dataSlice = index.AttributeDataOrInfo;
        var totalRanges = rangeSlice.Size / rangeEntrySize;
        if (totalRanges <= 0 || imageEntry.CustomAttributeStart >= totalRanges)
        {
            return;
        }

        var lo = imageEntry.CustomAttributeStart;
        var hi = Math.Min(totalRanges - 1, imageEntry.CustomAttributeStart + imageEntry.CustomAttributeCount - 1);
        var foundIdx = -1;
        uint startOffset = 0;

        Span<byte> entry = stackalloc byte[rangeEntrySize];
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var fileOff = rangeSlice.Offset + index.BodyOffsetDelta + (long)mid * rangeEntrySize;
            if (fileOff < 0 || fileOff + rangeEntrySize > stream.Length)
            {
                return;
            }

            stream.Position = fileOff;
            if (stream.Read(entry) != rangeEntrySize)
            {
                return;
            }
            ApplyXorInPlace(entry, fileOff, index.BodyXorKey);

            var token = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0, 4));
            if (token == targetToken)
            {
                foundIdx = mid;
                startOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                break;
            }

            if (token < targetToken)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (foundIdx < 0 || startOffset >= (uint)dataSlice.Size)
        {
            return;
        }

        uint endOffset = (uint)dataSlice.Size;
        if (foundIdx + 1 < totalRanges)
        {
            var nextFileOff = rangeSlice.Offset + index.BodyOffsetDelta + (long)(foundIdx + 1) * rangeEntrySize;
            if (nextFileOff >= 0 && nextFileOff + rangeEntrySize <= stream.Length)
            {
                stream.Position = nextFileOff;
                if (stream.Read(entry) == rangeEntrySize)
                {
                    ApplyXorInPlace(entry, nextFileOff, index.BodyXorKey);
                    var nextStart = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                    if (nextStart >= startOffset && nextStart <= (uint)dataSlice.Size)
                    {
                        endOffset = nextStart;
                    }
                }
            }
        }

        if (endOffset <= startOffset)
        {
            return;
        }

        var readLen = (int)Math.Min(endOffset - startOffset, 256u);
        var blob = ReadBytesAt(stream, dataSlice.Offset + index.BodyOffsetDelta + startOffset, readLen, index.BodyXorKey);
        var pos = 0;
        if (!TryReadCompressedUInt32(blob, ref pos, out var attrCount)
            || attrCount == 0
            || attrCount > 32
            || pos + (int)attrCount * 4 > blob.Length)
        {
            return;
        }

        var ml = index.MethodLayout;
        if (ml.StructSize <= 0 || index.TotalMethods <= 0)
        {
            return;
        }

        var declaringTypeWidth = index.Widths.TypeDefinition;
        Span<byte> declBuf = stackalloc byte[declaringTypeWidth];
        for (var i = 0; i < (int)attrCount; i++)
        {
            var rawCtor = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + i * 4, 4));
            var ctorMethodIdx = index.EncodedAttributeConstructors
                ? DecodeMethodDefinitionIndex(rawCtor)
                : unchecked((int)rawCtor);
            if (ctorMethodIdx < 0 || ctorMethodIdx >= index.TotalMethods)
            {
                continue;
            }

            var methodDeclOff = index.Methods.Offset + index.BodyOffsetDelta + (long)ctorMethodIdx * ml.StructSize + ml.DeclaringTypeOffset;
            if (methodDeclOff < 0 || methodDeclOff + declaringTypeWidth > stream.Length)
            {
                continue;
            }

            stream.Position = methodDeclOff;
            if (stream.Read(declBuf) != declaringTypeWidth)
            {
                continue;
            }
            ApplyXorInPlace(declBuf, methodDeclOff, index.BodyXorKey);

            var declaringTd = ReadIndex(declBuf, 0, declaringTypeWidth);
            if (declaringTd >= 0 && declaringTd < index.Types.Length)
            {
                var attrName = index.Types[declaringTd].Name;
                if (!string.IsNullOrWhiteSpace(attrName))
                {
                    results.Add(attrName);
                }
            }
        }
    }

    /// <summary>
    /// Decodes an <c>EncodedMethodIndex</c> (Unity 6000.5+ attribute constructors): the top three bits hold the
    /// metadata usage kind and bits 1..28 the index. Only method definitions map to a declaring type;
    /// generic method specs live in the native binary and yield -1.
    /// </summary>
    private static int DecodeMethodDefinitionIndex(uint encoded)
    {
        const uint methodDefinitionUsage = 3;
        return encoded >> 29 == methodDefinitionUsage
            ? (int)((encoded & 0x1FFFFFFEu) >> 1)
            : -1;
    }

    private static void ReadCustomAttributesLegacy(
        FileStream stream,
        MetadataIndex index,
        TypeResolutionContext typeResolver,
        ImageEntry imageEntry,
        uint targetToken,
        List<string> results)
    {
        const int infoEntrySize = 12;
        var infoSlice = index.AttributeDataOrInfo;
        var typesSlice = index.AttributeDataRangeOrTypes;
        var totalEntries = infoSlice.Size / infoEntrySize;
        var totalAttrTypes = typesSlice.Size / 4;
        if (totalEntries <= 0 || totalAttrTypes <= 0 || imageEntry.CustomAttributeStart >= totalEntries)
        {
            return;
        }

        var lo = imageEntry.CustomAttributeStart;
        var hi = Math.Min(totalEntries - 1, imageEntry.CustomAttributeStart + imageEntry.CustomAttributeCount - 1);
        var attrStart = -1;
        var attrCount = 0;

        Span<byte> entry = stackalloc byte[infoEntrySize];
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var fileOff = infoSlice.Offset + index.BodyOffsetDelta + (long)mid * infoEntrySize;
            if (fileOff < 0 || fileOff + infoEntrySize > stream.Length)
            {
                return;
            }

            stream.Position = fileOff;
            if (stream.Read(entry) != infoEntrySize)
            {
                return;
            }
            ApplyXorInPlace(entry, fileOff, index.BodyXorKey);

            var token = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0, 4));
            if (token == targetToken)
            {
                attrStart = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
                attrCount = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
                break;
            }

            if (token < targetToken)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (attrStart < 0 || attrCount <= 0 || attrCount > 32 || attrStart + attrCount > totalAttrTypes)
        {
            return;
        }

        var rawTypes = ReadBytesAt(stream, typesSlice.Offset + index.BodyOffsetDelta + (long)attrStart * 4, attrCount * 4, index.BodyXorKey);
        for (var i = 0; i < attrCount; i++)
        {
            var typeIdx = BinaryPrimitives.ReadInt32LittleEndian(rawTypes.AsSpan(i * 4, 4));
            var simpleName = typeResolver.ResolveTypeName(typeIdx, includeNamespace: false);
            if (!string.IsNullOrWhiteSpace(simpleName))
            {
                results.Add(simpleName);
            }
        }
    }

    private static List<string> FormatDisplayAttributes(
        IReadOnlyList<string> rawAttributes,
        bool isNotSerializedFlag,
        bool isSerializableFlag)
    {
        var formatted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(string displayName)
        {
            if (formatted.Count < 8 && !string.IsNullOrWhiteSpace(displayName) && seen.Add(displayName))
            {
                formatted.Add(displayName);
            }
        }

        if (isSerializableFlag)
        {
            TryAdd("Serializable");
        }

        for (var i = 0; i < rawAttributes.Count; i++)
        {
            var raw = rawAttributes[i];
            if (string.IsNullOrWhiteSpace(raw) || IsNoisyCompilerAttribute(raw))
            {
                continue;
            }

            var clean = raw.EndsWith("Attribute", StringComparison.Ordinal) && raw.Length > "Attribute".Length
                ? raw.Substring(0, raw.Length - "Attribute".Length)
                : raw;
            TryAdd(clean);
        }

        if (isNotSerializedFlag)
        {
            TryAdd("NonSerialized");
        }

        return formatted;
    }

    private static bool IsNoisyCompilerAttribute(string rawName)
    {
        return rawName is "NullableAttribute"
            or "NullableContextAttribute"
            or "IsReadOnlyAttribute"
            or "IteratorStateMachineAttribute"
            or "AsyncStateMachineAttribute"
            or "DebuggerHiddenAttribute"
            or "DebuggerBrowsableAttribute"
            or "DebuggerStepThroughAttribute"
            or "ParamArrayAttribute"
            or "DefaultMemberAttribute"
            or "ExtensionAttribute"
            or "RefSafetyRulesAttribute"
            or "EmbeddedAttribute"
            or "TupleElementNamesAttribute"
            or "DynamicAttribute";
    }

    private static bool TryFindFieldDefaultValue(
        FileStream stream,
        MetadataIndex index,
        int targetFieldIndex,
        out int typeIndex,
        out int dataIndex)
    {
        typeIndex = -1;
        dataIndex = -1;
        var w = index.Widths;
        var entrySize = index.FieldDefaultValueStructSize;
        var count = entrySize > 0 ? index.FieldDefaultValues.Size / entrySize : 0;
        if (count <= 0)
        {
            return false;
        }

        var lo = 0;
        var hi = count - 1;
        Span<byte> entry = stackalloc byte[entrySize];
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var fileOffset = index.FieldDefaultValues.Offset + index.BodyOffsetDelta + (long)mid * entrySize;
            stream.Position = fileOffset;
            if (stream.Read(entry) != entrySize)
            {
                return false;
            }
            ApplyXorInPlace(entry, fileOffset, index.BodyXorKey);

            var fieldIdx = ReadIndex(entry, 0, w.Field);
            if (fieldIdx == targetFieldIndex)
            {
                typeIndex = ReadIndex(entry, w.Field, w.Type);
                dataIndex = ReadIndex(entry, w.Field + w.Type, w.DefaultValueData);
                return true;
            }

            if (fieldIdx < targetFieldIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
    }

    private static string? TryReadDefaultValueLiteral(
        FileStream stream,
        MetadataIndex index,
        string? fullTypeName,
        int dataIndex)
    {
        if (string.IsNullOrEmpty(fullTypeName) || dataIndex < 0 || dataIndex >= index.DefaultValueData.Size)
        {
            return null;
        }

        var remaining = index.DefaultValueData.Size - dataIndex;
        var readLen = Math.Min(remaining, 16);
        if (readLen <= 0)
        {
            return null;
        }

        var buf = ReadBytesAt(stream, index.DefaultValueData.Offset + index.BodyOffsetDelta + dataIndex, readLen, index.BodyXorKey);
        var pos = 0;

        return fullTypeName switch
        {
            "System.Boolean" when buf.Length >= 1 => buf[0] != 0 ? "true" : "false",
            "System.Byte" when buf.Length >= 1 => buf[0].ToString(CultureInfo.InvariantCulture),
            "System.SByte" when buf.Length >= 1 => unchecked((sbyte)buf[0]).ToString(CultureInfo.InvariantCulture),
            "System.Int16" when buf.Length >= 2 => BinaryPrimitives.ReadInt16LittleEndian(buf).ToString(CultureInfo.InvariantCulture),
            "System.UInt16" when buf.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(buf).ToString(CultureInfo.InvariantCulture),
            "System.Int32" => index.Version >= 29
                ? (TryReadCompressedInt32(buf, ref pos, out var i32) ? i32.ToString(CultureInfo.InvariantCulture) : null)
                : (buf.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(buf).ToString(CultureInfo.InvariantCulture) : null),
            "System.UInt32" => index.Version >= 29
                ? (TryReadCompressedUInt32(buf, ref pos, out var u32) ? u32.ToString(CultureInfo.InvariantCulture) : null)
                : (buf.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(buf).ToString(CultureInfo.InvariantCulture) : null),
            "System.Int64" when buf.Length >= 8 => BinaryPrimitives.ReadInt64LittleEndian(buf).ToString(CultureInfo.InvariantCulture),
            "System.UInt64" when buf.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(buf).ToString(CultureInfo.InvariantCulture),
            "System.Single" when buf.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(buf).ToString("G", CultureInfo.InvariantCulture) + "f",
            "System.Double" when buf.Length >= 8 => BinaryPrimitives.ReadDoubleLittleEndian(buf).ToString("G", CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static bool TryReadCompressedUInt32(ReadOnlySpan<byte> buf, ref int pos, out uint value)
    {
        value = 0;
        if (pos >= buf.Length) return false;
        var b0 = buf[pos++];
        if ((b0 & 0x80) == 0)
        {
            value = b0;
            return true;
        }
        if ((b0 & 0xC0) == 0x80)
        {
            if (pos >= buf.Length) return false;
            value = ((uint)(b0 & ~0x80) << 8) | buf[pos++];
            return true;
        }
        if ((b0 & 0xE0) == 0xC0)
        {
            if (pos + 3 > buf.Length) return false;
            value = ((uint)(b0 & ~0xC0) << 24) | ((uint)buf[pos++] << 16) | ((uint)buf[pos++] << 8) | buf[pos++];
            return true;
        }
        if (b0 == 0xF0)
        {
            if (pos + 4 > buf.Length) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(pos, 4));
            pos += 4;
            return true;
        }
        if (b0 == 0xFE)
        {
            value = uint.MaxValue - 1;
            return true;
        }
        if (b0 == 0xFF)
        {
            value = uint.MaxValue;
            return true;
        }
        return false;
    }

    private static bool TryReadCompressedInt32(ReadOnlySpan<byte> buf, ref int pos, out int value)
    {
        value = 0;
        if (!TryReadCompressedUInt32(buf, ref pos, out var encoded))
        {
            return false;
        }

        if (encoded == uint.MaxValue)
        {
            value = int.MinValue;
            return true;
        }

        var isNegative = (encoded & 1) != 0;
        encoded >>= 1;
        value = isNegative ? -(int)(encoded + 1) : (int)encoded;
        return true;
    }

    private static List<string> ReadGenericParameterNames(
        FileStream stream,
        MetadataIndex index,
        int genericContainerIndex)
    {
        var names = new List<string>();
        var gcSize = index.GenericContainerStructSize;
        var gpSize = index.GenericParameterStructSize;
        if (genericContainerIndex < 0 || index.GenericContainers.Size < gcSize || index.GenericParameters.Size < gpSize)
        {
            return names;
        }

        var totalContainers = index.GenericContainers.Size / gcSize;
        if (genericContainerIndex >= totalContainers)
        {
            return names;
        }

        var gcBuf = ReadBytesAt(stream, index.GenericContainers.Offset + index.BodyOffsetDelta + (long)genericContainerIndex * gcSize, gcSize, index.BodyXorKey);
        var typeArgCount = index.GenericContainerArgCountWidth == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(gcBuf.AsSpan(4, 2))
            : BinaryPrimitives.ReadInt32LittleEndian(gcBuf.AsSpan(4, 4));
        var paramStart = ReadIndex(gcBuf, index.GenericContainerParameterStartOffset, index.Widths.GenericParameter);
        var totalParams = index.GenericParameters.Size / gpSize;
        var gpNameOffset = index.Widths.GenericContainer;
        if (typeArgCount <= 0 || typeArgCount > 32 || paramStart < 0 || paramStart >= totalParams)
        {
            return names;
        }

        var safeCount = Math.Min(typeArgCount, totalParams - paramStart);
        var gpBuf = ReadBytesAt(stream, index.GenericParameters.Offset + index.BodyOffsetDelta + (long)paramStart * gpSize, safeCount * gpSize, index.BodyXorKey);
        for (var i = 0; i < safeCount; i++)
        {
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(gpBuf.AsSpan(i * gpSize + gpNameOffset, 4));
            var name = ReadStringAt(stream, index, nameIdx);
            names.Add(!string.IsNullOrWhiteSpace(name) ? name : $"T{i}");
        }

        return names;
    }

    private static List<string> ReadInterfaceNames(
        FileStream stream,
        MetadataIndex index,
        TypeResolutionContext typeResolver,
        int interfacesStart,
        int interfacesCount)
    {
        var results = new List<string>();
        var entrySize = index.Widths.Type;
        if (interfacesCount <= 0 || interfacesStart < 0 || index.Interfaces.Size < entrySize)
        {
            return results;
        }

        var totalEntries = index.Interfaces.Size / entrySize;
        if (interfacesStart >= totalEntries)
        {
            return results;
        }

        var safeCount = Math.Min(interfacesCount, totalEntries - interfacesStart);
        var buf = ReadBytesAt(stream, index.Interfaces.Offset + index.BodyOffsetDelta + (long)interfacesStart * entrySize, safeCount * entrySize, index.BodyXorKey);
        for (var i = 0; i < safeCount; i++)
        {
            var ifaceTypeIdx = ReadIndex(buf, i * entrySize, entrySize);
            var name = typeResolver.ResolveTypeName(ifaceTypeIdx, includeNamespace: true);
            if (!string.IsNullOrWhiteSpace(name))
            {
                results.Add(name);
            }
        }

        return results;
    }

    private static List<string> ReadNestedTypeNames(
        FileStream stream,
        MetadataIndex index,
        int nestedStart,
        int nestedCount)
    {
        var results = new List<string>();
        if (nestedCount <= 0 || nestedStart < 0 || index.NestedTypes.Size < 4)
        {
            return results;
        }

        var totalEntries = index.NestedTypes.Size / 4;
        if (nestedStart >= totalEntries)
        {
            return results;
        }

        var safeCount = Math.Min(nestedCount, totalEntries - nestedStart);
        var buf = ReadBytesAt(stream, index.NestedTypes.Offset + index.BodyOffsetDelta + (long)nestedStart * 4, safeCount * 4, index.BodyXorKey);
        for (var i = 0; i < safeCount; i++)
        {
            var nestedTdIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i * 4, 4));
            if (nestedTdIdx >= 0 && nestedTdIdx < index.Types.Length)
            {
                var name = index.Types[nestedTdIdx].Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    results.Add(name);
                }
            }
        }

        return results;
    }

    private static string FormatTypeModifiers(uint flags, bool isInterface, bool isValueType, bool isEnum)
    {
        var vis = (flags & 0x7) switch
        {
            1 or 2 => "public",
            3 => "private",
            4 => "protected",
            5 => "internal",
            6 => "private protected",
            7 => "protected internal",
            _ => "internal"
        };

        if (isInterface || isValueType || isEnum)
        {
            return vis;
        }

        var isAbstract = (flags & 0x00000080) != 0;
        var isSealed = (flags & 0x00000100) != 0;
        if (isAbstract && isSealed)
        {
            return $"{vis} static";
        }
        if (isAbstract)
        {
            return $"{vis} abstract";
        }
        if (isSealed)
        {
            return $"{vis} sealed";
        }

        return vis;
    }

    private static string FormatMethodModifiers(uint flags, bool declaringIsInterface)
    {
        if (declaringIsInterface)
        {
            return (flags & 0x0010) != 0 ? "static" : string.Empty;
        }

        var parts = new List<string>(3);
        var vis = (flags & 0x0007) switch
        {
            6 => "public",
            1 => "private",
            4 => "protected",
            3 => "internal",
            5 => "protected internal",
            2 => "private protected",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(vis))
        {
            parts.Add(vis);
        }

        var isStatic = (flags & 0x0010) != 0;
        var isAbstract = (flags & 0x0400) != 0;
        var isVirtual = (flags & 0x0040) != 0;
        var isFinal = (flags & 0x0020) != 0;
        var isNewSlot = (flags & 0x0100) != 0;
        var isPinvoke = (flags & 0x2000) != 0;

        if (isStatic)
        {
            parts.Add("static");
        }
        else if (isAbstract)
        {
            parts.Add("abstract");
        }
        else if (isVirtual && !isFinal)
        {
            parts.Add(isNewSlot ? "virtual" : "override");
        }

        if (isPinvoke)
        {
            parts.Add("extern");
        }

        return string.Join(" ", parts);
    }

    private static string PickPropertyModifiers(ParsedMethod? primary, ParsedMethod? secondary)
    {
        var chosen = primary ?? secondary;
        if (chosen == null)
        {
            return "public";
        }

        if (primary != null && secondary != null)
        {
            if (AccessRank(secondary.Flags) > AccessRank(primary.Flags))
            {
                chosen = secondary;
            }
        }

        var vis = (chosen.Flags & 0x0007) switch
        {
            6 => "public",
            1 => "private",
            4 => "protected",
            3 => "internal",
            5 => "protected internal",
            2 => "private protected",
            _ => "public"
        };

        return (chosen.Flags & 0x0010) != 0 ? $"{vis} static" : vis;
    }

    private static int AccessRank(uint flags) => (flags & 0x0007) switch
    {
        6 => 5, // public
        5 => 4, // protected internal
        4 => 3, // protected
        3 => 2, // internal
        2 => 1, // private protected
        _ => 0  // private
    };

    private static string FormatFriendlyTypeName(string fullName, string currentNamespace)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        // Peel trailing pointer/nullable/array suffixes recursively
        if (fullName.EndsWith('*') || fullName.EndsWith('?'))
        {
            var suffix = fullName[^1];
            return FormatFriendlyTypeName(fullName.Substring(0, fullName.Length - 1), currentNamespace) + suffix;
        }

        if (fullName.EndsWith(']'))
        {
            var openBracket = fullName.LastIndexOf('[');
            if (openBracket > 0)
            {
                var arrSuffix = fullName.Substring(openBracket);
                var validArr = true;
                for (var i = 1; i < arrSuffix.Length - 1; i++)
                {
                    if (arrSuffix[i] != ',')
                    {
                        validArr = false;
                        break;
                    }
                }

                if (validArr)
                {
                    return FormatFriendlyTypeName(fullName.Substring(0, openBracket), currentNamespace) + arrSuffix;
                }
            }
        }

        // Handle generic instantiations recursively, including nested tails like Dictionary<string, int>.Enumerator
        var openAngle = fullName.IndexOf('<');
        if (openAngle > 0)
        {
            var closeAngle = FindMatchingAngleBracket(fullName, openAngle);
            if (closeAngle > openAngle)
            {
                var basePart = fullName.Substring(0, openAngle);
                var argsPart = fullName.Substring(openAngle + 1, closeAngle - openAngle - 1);
                var tailPart = closeAngle + 1 < fullName.Length ? fullName.Substring(closeAngle + 1) : string.Empty;

                var rawArgs = SplitTopLevelGenericArgs(argsPart);
                var formattedArgs = new string[rawArgs.Count];
                for (var i = 0; i < rawArgs.Count; i++)
                {
                    formattedArgs[i] = FormatFriendlyTypeName(rawArgs[i], currentNamespace);
                }

                if (string.IsNullOrEmpty(tailPart)
                    && formattedArgs.Length == 1
                    && string.Equals(basePart, "System.Nullable", StringComparison.Ordinal))
                {
                    return $"{formattedArgs[0]}?";
                }

                var cleanBase = SimplifyGenericBaseName(basePart, currentNamespace);
                var formattedTail = !string.IsNullOrEmpty(tailPart) && tailPart.StartsWith('.') && tailPart.Contains('<')
                    ? "." + FormatFriendlyTypeName(tailPart.Substring(1), currentNamespace)
                    : tailPart;
                return $"{cleanBase}<{string.Join(", ", formattedArgs)}>{formattedTail}";
            }
        }

        return fullName switch
        {
            "System.Void" => "void",
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.Decimal" => "decimal",
            "System.Char" => "char",
            "System.String" => "string",
            "System.Object" => "object",
            "System.Action" => "Action",
            _ => StripMatchingNamespace(fullName, currentNamespace)
        };
    }

    private static string SimplifyGenericBaseName(string baseName, string currentNamespace)
    {
        const string scgPrefix = "System.Collections.Generic.";
        if (baseName.StartsWith(scgPrefix, StringComparison.Ordinal))
        {
            return baseName.Substring(scgPrefix.Length);
        }

        if (baseName is "System.Action" or "System.Func" or "System.EventHandler" or "System.Comparison" or "System.Predicate" or "System.Span" or "System.ReadOnlySpan" or "System.Memory" or "System.ReadOnlyMemory")
        {
            return baseName.Substring("System.".Length);
        }

        if (baseName is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
        {
            return baseName.Substring("System.Threading.Tasks.".Length);
        }

        return StripMatchingNamespace(baseName, currentNamespace);
    }

    private static int FindMatchingAngleBracket(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevelGenericArgs(string argsPart)
    {
        var result = new List<string>(2);
        var depth = 0;
        var start = 0;
        for (var i = 0; i < argsPart.Length; i++)
        {
            var ch = argsPart[i];
            if (ch == '<') depth++;
            else if (ch == '>') depth--;
            else if (ch == ',' && depth == 0)
            {
                result.Add(argsPart.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        if (start < argsPart.Length)
        {
            result.Add(argsPart.Substring(start).Trim());
        }

        return result;
    }

    private static string StripMatchingNamespace(string fullName, string currentNamespace)
    {
        if (!string.IsNullOrEmpty(currentNamespace)
            && fullName.StartsWith(currentNamespace + ".", StringComparison.Ordinal))
        {
            var remainder = fullName.Substring(currentNamespace.Length + 1);
            if (!remainder.Contains('.'))
            {
                return remainder;
            }
        }

        return fullName;
    }

    private static bool IsImplicitBaseType(string parentFullName, bool isValueType, bool isEnum)
    {
        if (string.Equals(parentFullName, "System.Object", StringComparison.Ordinal))
        {
            return true;
        }
        if (isValueType && string.Equals(parentFullName, "System.ValueType", StringComparison.Ordinal))
        {
            return true;
        }
        if (isEnum && string.Equals(parentFullName, "System.Enum", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static string FormatTypeNameWithGenerics(string rawName, List<string> genericParams)
    {
        if (genericParams.Count == 0)
        {
            return rawName;
        }

        var baseName = StripGenericArity(rawName);
        return $"{baseName}<{string.Join(", ", genericParams)}>";
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        var angle = name.IndexOf('<');
        var cut = tick >= 0 && angle >= 0 ? Math.Min(tick, angle) : Math.Max(tick, angle);
        return cut > 0 ? name.Substring(0, cut) : name;
    }

    private static string NormalizeAssemblyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = Path.GetFileName(name.Trim());
        return trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(0, trimmed.Length - 4)
            : trimmed;
    }

    private static void AddToBucket(Dictionary<string, List<int>> map, string key, int value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>(1);
            map[key] = list;
        }

        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }

    private static byte[] ReadTableBytes(FileStream stream, TableSlice slice, long bodyOffsetDelta, byte[]? xorKey)
    {
        return ReadBytesAt(stream, slice.Offset + bodyOffsetDelta, slice.Size, xorKey);
    }

    private static byte[] ReadBytesAt(FileStream stream, long offset, int length, byte[]? xorKey)
    {
        if (length <= 0 || offset < 0 || offset + length > stream.Length)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[length];
        stream.Position = offset;
        ReadExactlyOrFail(stream, buffer, 0, length);
        ApplyXorInPlace(buffer, offset, xorKey);
        return buffer;
    }

    private static void ApplyXorInPlace(Span<byte> buffer, long fileOffset, byte[]? xorKey)
    {
        if (xorKey == null || xorKey.Length == 0 || buffer.IsEmpty)
        {
            return;
        }

        var period = xorKey.Length;
        var lane = (int)(fileOffset % period);
        if (lane < 0)
        {
            lane += period;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] ^= xorKey[lane];
            if (++lane == period)
            {
                lane = 0;
            }
        }
    }

    private static void ReadExactlyOrFail(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of global-metadata.dat.");
            }
            total += read;
        }
    }

    private static string? ReadStringAt(FileStream stream, MetadataIndex index, int nameIndex)
    {
        var stringTable = index.Strings;
        if (nameIndex < 0 || nameIndex >= stringTable.Size)
        {
            return null;
        }

        var maxRead = Math.Min(stringTable.Size - nameIndex, MaxStringReadBytes);
        var buf = new byte[maxRead];
        var fileOffset = stringTable.Offset + index.BodyOffsetDelta + (long)nameIndex;
        if (fileOffset < 0 || fileOffset >= stream.Length)
        {
            return null;
        }

        stream.Position = fileOffset;
        var read = stream.Read(buf, 0, maxRead);
        if (read <= 0)
        {
            return string.Empty;
        }

        ApplyXorInPlace(buf.AsSpan(0, read), fileOffset, index.BodyXorKey);
        var zero = Array.IndexOf(buf, (byte)0, 0, read);
        var len = zero >= 0 ? zero : read;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    private static string? ReadNullTerminatedUtf8(byte[] stringBytes, int index)
    {
        if (index < 0 || index >= stringBytes.Length)
        {
            return null;
        }

        var end = Array.IndexOf(stringBytes, (byte)0, index);
        if (end < 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(stringBytes, index, end - index);
    }

    private static bool IsReadableMetadataString(byte[] stringBytes, int index)
    {
        var text = ReadNullTerminatedUtf8(stringBytes, index);
        return !string.IsNullOrEmpty(text) && LooksLikeValidIdentifierOrFileName(text);
    }

    private static bool LooksLikeValidIdentifierOrFileName(string text)
    {
        if (text.Length == 0 || text.Length > 512)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < 0x20 || ch == 0x7F || ch == '\uFFFD')
            {
                return false;
            }
        }

        return true;
    }
}
