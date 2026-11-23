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
/// Pure-C# reader for Unity IL2CPP <c>global-metadata.dat</c> (versions 24 through 31).
/// Reconstructs readable C# type declarations, fields, properties, events, methods,
/// parameters, generic parameters, base classes, interfaces, and enum constants without
/// requiring any external tool.
/// </summary>
public sealed class Il2CppMetadataReader
{
    public const uint MetadataSanity = 0xFAB11BAF;
    public const int MinSupportedVersion = 24;
    public const int MaxSupportedVersion = 31;
    private const int MaxCachedFiles = 2;
    private const int MaxHeaderBytes = 1024;
    private const int MaxStringReadBytes = 2048;

    private readonly record struct TableSlice(int Offset, int Size)
    {
        public bool IsValid => Offset >= 0 && Size >= 0;
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
        int GenericContainerIndexOffset)
    {
        public int TokenOffset => StructSize - 12;
        public int FlagsOffset => StructSize - 8;
        public int ParameterCountOffset => StructSize - 2;
    }

    private readonly record struct ImageEntry(
        string Name,
        int TypeStart,
        int TypeCount);

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
        public required TypeDefLayout TypeDefLayout { get; init; }
        public required MethodLayout MethodLayout { get; init; }
        public required int FieldStructSize { get; init; }
        public required int ParameterStructSize { get; init; }
        public required int PropertyStructSize { get; init; }
        public required int EventStructSize { get; init; }
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
    private long _accessCounter;

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _indexCache.Clear();
            _failureCache.Clear();
        }
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
        var fieldTypeOverrides = request.FallbackTypeTree != null
            ? TypeTreeScriptReconstructor.ExtractTopLevelFieldTypes(request.FallbackTypeTree)
            : null;
        return ReconstructTypeResult(index, typeDefIndex, fieldTypeOverrides, cancellationToken);
    }

    public ScriptDecompilationResult? TryExtractType(
        string metadataPath,
        ScriptLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        return TryExtractType(metadataPath, request, out _, cancellationToken);
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
            if (version < MinSupportedVersion || version > MaxSupportedVersion)
            {
                return Fail(
                    $"IL2CPP global-metadata.dat version v{version} is outside the built-in reader's supported range (v{MinSupportedVersion}-v{MaxSupportedVersion}).",
                    isObfuscated: version < 16 || version > 100);
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

            // Build lightweight lookup index
            var types = new TypeSummary[totalTypes];
            var byFullName = new Dictionary<string, List<int>>(totalTypes, StringComparer.OrdinalIgnoreCase);
            var bySimpleName = new Dictionary<string, List<int>>(totalTypes, StringComparer.OrdinalIgnoreCase);
            var byvalMap = new Dictionary<int, int>(totalTypes);

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

                var entryOff = i * typeDefStructSize;
                var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(typeDefBytes.AsSpan(entryOff, 4));
                var nsIdx = BinaryPrimitives.ReadInt32LittleEndian(typeDefBytes.AsSpan(entryOff + 4, 4));
                var byvalIdx = BinaryPrimitives.ReadInt32LittleEndian(typeDefBytes.AsSpan(entryOff + typeDefLayout.ByvalTypeIndexOffset, 4));
                var declaringIdx = BinaryPrimitives.ReadInt32LittleEndian(typeDefBytes.AsSpan(entryOff + typeDefLayout.DeclaringTypeIndexOffset, 4));

                var name = ReadNullTerminatedUtf8(stringBytes, nameIdx) ?? string.Empty;
                var ns = ReadNullTerminatedUtf8(stringBytes, nsIdx) ?? string.Empty;

                types[i] = new TypeSummary(name, ns, typeToImage[i], declaringIdx);

                if (byvalIdx >= 0 && !byvalMap.ContainsKey(byvalIdx))
                {
                    byvalMap[byvalIdx] = i;
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
                TypeDefLayout = typeDefLayout,
                MethodLayout = methodLayout,
                FieldStructSize = fieldStructSize,
                ParameterStructSize = paramStructSize,
                PropertyStructSize = propertyStructSize,
                EventStructSize = eventStructSize,
                Images = images,
                Types = types,
                ByFullName = byFullName,
                BySimpleName = bySimpleName,
                ByvalTypeToTypeDef = byvalMap,
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
        out int totalTypes,
        out int typeDefStructSize,
        out byte[] typeDefBytes)
    {
        bodyOffsetDelta = 0;
        bodyXorKey = null;
        stringBytes = Array.Empty<byte>();
        images = Array.Empty<ImageEntry>();
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
        out int totalTypes,
        out int typeDefStructSize,
        out byte[] typeDefBytes)
    {
        stringBytes = Array.Empty<byte>();
        images = Array.Empty<ImageEntry>();
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
        out int totalTypes,
        out int typeDefStructSize)
    {
        images = Array.Empty<ImageEntry>();
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

                    runningTypes += typeCount;
                    parsed[i] = new ImageEntry(name, typeStart, typeCount);
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
                    return new MethodLayout(36, ParameterStartOffset: 16, GenericContainerIndexOffset: 20);
                }

                if (msz == 56)
                {
                    return new MethodLayout(56, ParameterStartOffset: 12, GenericContainerIndexOffset: 20);
                }

                return new MethodLayout(msz, ParameterStartOffset: 12, GenericContainerIndexOffset: 16);
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

    private static ScriptDecompilationResult ReconstructTypeResult(
        MetadataIndex index,
        int typeDefIndex,
        IReadOnlyDictionary<string, string>? fieldTypeOverrides,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(index.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024);

        var tdLayout = index.TypeDefLayout;
        var tdBytes = ReadBytesAt(stream, index.TypeDefinitions.Offset + index.BodyOffsetDelta + (long)typeDefIndex * tdLayout.StructSize, tdLayout.StructSize, index.BodyXorKey);

        var summary = index.Types[typeDefIndex];
        var assemblyName = summary.ImageIndex >= 0 && summary.ImageIndex < index.Images.Length
            ? index.Images[summary.ImageIndex].Name
            : string.Empty;

        var parentTypeIndex = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.ParentIndexOffset, 4));
        var elementTypeIndex = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.ElementTypeIndexOffset, 4));
        var genericContainerIdx = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.GenericContainerIndexOffset, 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(tdBytes.AsSpan(tdLayout.FlagsOffset, 4));
        var fieldStart = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.FieldStartOffset, 4));
        var methodStart = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.MethodStartOffset, 4));
        var eventStart = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.EventStartOffset, 4));
        var propertyStart = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.PropertyStartOffset, 4));
        var nestedStart = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.NestedTypesStartOffset, 4));
        var interfacesStart = BinaryPrimitives.ReadInt32LittleEndian(tdBytes.AsSpan(tdLayout.InterfacesStartOffset, 4));

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

        // Read methods first (needed for property/event type & modifier inference)
        var parsedMethods = ReadMethods(stream, index, methodStart, methodCount, isInterface, cancellationToken);
        var parsedProperties = ReadProperties(stream, index, propertyStart, propertyCount, parsedMethods);
        var parsedEvents = ReadEvents(stream, index, eventStart, eventCount, parsedMethods);
        var parsedFields = ReadFields(stream, index, fieldStart, fieldCount, isEnum, parsedProperties, fieldTypeOverrides);
        var typeGenericParams = ReadGenericParameterNames(stream, index, genericContainerIdx);
        var interfaceNames = ReadInterfaceNames(stream, index, interfacesStart, interfacesCount);
        var nestedNames = ReadNestedTypeNames(stream, index, nestedStart, nestedCount);

        var parentTypeName = ResolveTypeName(index, parentTypeIndex, includeNamespace: true);
        var underlyingEnumTypeName = isEnum ? ResolveTypeName(index, elementTypeIndex, includeNamespace: false) : null;

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
        sb.AppendLine($"// Source: IL2CPP Metadata v{index.Version}");
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
                    var constPrefix = !string.IsNullOrEmpty(f.DefaultValueLiteral) ? "const " : string.Empty;
                    var typePart = !string.IsNullOrEmpty(f.ResolvedTypeName)
                        ? FormatFriendlyTypeName(f.ResolvedTypeName, summary.Namespace)
                        : "/* field */";
                    var valuePart = !string.IsNullOrEmpty(f.DefaultValueLiteral) ? $" = {f.DefaultValueLiteral}" : string.Empty;
                    sb.AppendLine($"{memberIndent}{constPrefix}{typePart} {f.Name}{valuePart}; // Token: 0x{f.Token:X8}");
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
                    sb.AppendLine($"{memberIndent}{mods}{propType} {prop.Name} {accessors} // Token: 0x{prop.Token:X8}");
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
                foreach (var m in parsedMethods)
                {
                    var paramText = string.Join(", ", m.Parameters.Select(p =>
                    {
                        var pType = !string.IsNullOrEmpty(p.TypeName)
                            ? FormatFriendlyTypeName(p.TypeName, summary.Namespace)
                            : "/* param */";
                        var pName = string.IsNullOrWhiteSpace(p.Name) ? $"arg{p.Index}" : p.Name;
                        return $"{pType} {pName}";
                    }));

                    if (string.Equals(m.Name, ".cctor", StringComparison.Ordinal))
                    {
                        sb.AppendLine($"{memberIndent}static {ctorName}(); // Token: 0x{m.Token:X8}");
                    }
                    else if (string.Equals(m.Name, ".ctor", StringComparison.Ordinal))
                    {
                        var mods = string.IsNullOrWhiteSpace(m.Modifiers) ? "public" : m.Modifiers;
                        sb.AppendLine($"{memberIndent}{mods} {ctorName}({paramText}); // Token: 0x{m.Token:X8}");
                    }
                    else
                    {
                        var retType = !string.IsNullOrEmpty(m.ReturnTypeName)
                            ? FormatFriendlyTypeName(m.ReturnTypeName, summary.Namespace)
                            : "/* return */";
                        var genSuffix = m.GenericParameters.Count > 0
                            ? $"<{string.Join(", ", m.GenericParameters)}>"
                            : string.Empty;
                        var mods = string.IsNullOrWhiteSpace(m.Modifiers) ? string.Empty : m.Modifiers + " ";
                        sb.AppendLine($"{memberIndent}{mods}{retType} {m.Name}{genSuffix}({paramText}); // Token: 0x{m.Token:X8}");
                    }
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

        var badge = $"IL2CPP (metadata v{index.Version})";
        var summaryParts = new List<string> { $"global-metadata.dat (v{index.Version})" };
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            summaryParts.Add(assemblyName);
        }
        summaryParts.Add($"{fieldCount}F / {propertyCount}P / {methodCount}M");

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
            TypeDefIndex = typeDefIndex
        };
    }

    private sealed record ParsedParam(int Index, string Name, string? TypeName);

    private sealed record ParsedMethod(
        string Name,
        string Modifiers,
        string? ReturnTypeName,
        uint Flags,
        uint Token,
        List<string> GenericParameters,
        List<ParsedParam> Parameters);

    private sealed record ParsedProperty(
        string Name,
        string Modifiers,
        string? TypeName,
        bool HasGetter,
        bool HasSetter,
        uint Token);

    private sealed record ParsedEvent(
        string Name,
        string Modifiers,
        string? TypeName,
        uint Token);

    private sealed record ParsedField(
        string Name,
        string? ResolvedTypeName,
        string? DefaultValueLiteral,
        uint Token);

    private static List<ParsedMethod> ReadMethods(
        FileStream stream,
        MetadataIndex index,
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

        for (var i = 0; i < safeCount; i++)
        {
            if ((i & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var off = i * ml.StructSize;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var returnTypeIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 8, 4));
            var paramStart = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + ml.ParameterStartOffset, 4));
            var genContainerIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + ml.GenericContainerIndexOffset, 4));
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + ml.TokenOffset, 4));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + ml.FlagsOffset, 2));
            var paramCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + ml.ParameterCountOffset, 2));

            var methodName = ReadStringAt(stream, index, nameIdx) ?? $"Method_{i}";
            var returnType = ResolveTypeName(index, returnTypeIdx, includeNamespace: true);
            var genParams = ReadGenericParameterNames(stream, index, genContainerIdx);

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
                    var pTypeIdx = BinaryPrimitives.ReadInt32LittleEndian(pBuf.AsSpan(pOff + index.ParameterStructSize - 4, 4));
                    var pName = ReadStringAt(stream, index, pNameIdx) ?? $"arg{p}";
                    var pType = ResolveTypeName(index, pTypeIdx, includeNamespace: true);
                    parameters.Add(new ParsedParam(p, pName, pType));
                }
            }

            var mods = FormatMethodModifiers(flags, declaringIsInterface);
            results.Add(new ParsedMethod(methodName, mods, returnType, flags, token, genParams, parameters));
        }

        return results;
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

        for (var i = 0; i < safeCount; i++)
        {
            var off = i * psz;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var getIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 4, 4));
            var setIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 8, 4));
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + psz - 4, 4));

            var name = ReadStringAt(stream, index, nameIdx) ?? $"Property_{i}";
            var getter = getIdx >= 0 && getIdx < declaringMethods.Count ? declaringMethods[getIdx] : null;
            var setter = setIdx >= 0 && setIdx < declaringMethods.Count ? declaringMethods[setIdx] : null;

            string? propType = getter?.ReturnTypeName;
            if (string.IsNullOrEmpty(propType) && setter != null && setter.Parameters.Count > 0)
            {
                propType = setter.Parameters[^1].TypeName;
            }

            var mods = PickPropertyModifiers(getter, setter);
            results.Add(new ParsedProperty(name, mods, propType, getIdx >= 0, setIdx >= 0, token));
        }

        return results;
    }

    private static List<ParsedEvent> ReadEvents(
        FileStream stream,
        MetadataIndex index,
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

        for (var i = 0; i < safeCount; i++)
        {
            var off = i * esz;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var typeIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 4, 4));
            var addIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 8, 4));
            var removeIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 12, 4));
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + esz - 4, 4));

            var name = ReadStringAt(stream, index, nameIdx) ?? $"Event_{i}";
            var addMethod = addIdx >= 0 && addIdx < declaringMethods.Count ? declaringMethods[addIdx] : null;
            var removeMethod = removeIdx >= 0 && removeIdx < declaringMethods.Count ? declaringMethods[removeIdx] : null;

            var evType = ResolveTypeName(index, typeIdx, includeNamespace: true);
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
        int fieldStart,
        int fieldCount,
        bool isEnum,
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

        Dictionary<string, string>? propTypes = null;
        if (!isEnum && properties.Count > 0)
        {
            propTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in properties)
            {
                if (!string.IsNullOrEmpty(p.TypeName))
                {
                    propTypes.TryAdd(p.Name, p.TypeName);
                }
            }
        }

        for (var i = 0; i < safeCount; i++)
        {
            var globalFieldIndex = fieldStart + i;
            var off = i * fsz;
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4));
            var typeIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 4, 4));
            var token = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + fsz - 4, 4));

            var name = ReadStringAt(stream, index, nameIdx) ?? $"field_{i}";
            var resolvedType = ResolveTypeName(index, typeIdx, includeNamespace: true);
            string? defaultLiteral = null;

            if (TryFindFieldDefaultValue(stream, index, globalFieldIndex, out var defTypeIdx, out var defDataIdx))
            {
                var defTypeName = ResolveTypeName(index, defTypeIdx, includeNamespace: true);
                if (string.IsNullOrEmpty(resolvedType) && !string.IsNullOrEmpty(defTypeName))
                {
                    resolvedType = defTypeName;
                }

                defaultLiteral = TryReadDefaultValueLiteral(stream, index, defTypeName, defDataIdx);
            }

            // Enrich unresolved serialized field types using Unity's TypeTree when available
            if (string.IsNullOrEmpty(resolvedType)
                && fieldTypeOverrides != null
                && fieldTypeOverrides.TryGetValue(name, out var typeTreeFieldType)
                && !string.IsNullOrWhiteSpace(typeTreeFieldType))
            {
                resolvedType = typeTreeFieldType;
            }

            // Infer compiler-generated auto-property backing field type when available
            if (string.IsNullOrEmpty(resolvedType)
                && propTypes != null
                && name.StartsWith('<')
                && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            {
                var propName = name.Substring(1, name.Length - ">k__BackingField".Length - 1);
                if (propTypes.TryGetValue(propName, out var backingType))
                {
                    resolvedType = backingType;
                }
            }

            results.Add(new ParsedField(name, resolvedType, defaultLiteral, token));
        }

        return results;
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
        const int entrySize = 12;
        var count = index.FieldDefaultValues.Size / entrySize;
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

            var fieldIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
            if (fieldIdx == targetFieldIndex)
            {
                typeIndex = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
                dataIndex = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
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
        const int gcSize = 16;
        const int gpSize = 16;
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
        var typeArgCount = BinaryPrimitives.ReadInt32LittleEndian(gcBuf.AsSpan(4, 4));
        var paramStart = BinaryPrimitives.ReadInt32LittleEndian(gcBuf.AsSpan(12, 4));
        var totalParams = index.GenericParameters.Size / gpSize;
        if (typeArgCount <= 0 || typeArgCount > 32 || paramStart < 0 || paramStart >= totalParams)
        {
            return names;
        }

        var safeCount = Math.Min(typeArgCount, totalParams - paramStart);
        var gpBuf = ReadBytesAt(stream, index.GenericParameters.Offset + index.BodyOffsetDelta + (long)paramStart * gpSize, safeCount * gpSize, index.BodyXorKey);
        for (var i = 0; i < safeCount; i++)
        {
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(gpBuf.AsSpan(i * gpSize + 4, 4));
            var name = ReadStringAt(stream, index, nameIdx);
            names.Add(!string.IsNullOrWhiteSpace(name) ? name : $"T{i}");
        }

        return names;
    }

    private static List<string> ReadInterfaceNames(
        FileStream stream,
        MetadataIndex index,
        int interfacesStart,
        int interfacesCount)
    {
        var results = new List<string>();
        if (interfacesCount <= 0 || interfacesStart < 0 || index.Interfaces.Size < 4)
        {
            return results;
        }

        var totalEntries = index.Interfaces.Size / 4;
        if (interfacesStart >= totalEntries)
        {
            return results;
        }

        var safeCount = Math.Min(interfacesCount, totalEntries - interfacesStart);
        var buf = ReadBytesAt(stream, index.Interfaces.Offset + index.BodyOffsetDelta + (long)interfacesStart * 4, safeCount * 4, index.BodyXorKey);
        for (var i = 0; i < safeCount; i++)
        {
            var ifaceTypeIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i * 4, 4));
            var name = ResolveTypeName(index, ifaceTypeIdx, includeNamespace: true);
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

    private static string? ResolveTypeName(MetadataIndex index, int typeIndex, bool includeNamespace)
    {
        if (typeIndex < 0 || !index.ByvalTypeToTypeDef.TryGetValue(typeIndex, out var tdIdx))
        {
            return null;
        }

        var summary = index.Types[tdIdx];
        var fullName = !string.IsNullOrWhiteSpace(summary.Namespace)
            ? $"{summary.Namespace}.{summary.Name}"
            : summary.Name;

        return includeNamespace ? fullName : summary.Name;
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
            _ => StripMatchingNamespace(fullName, currentNamespace)
        };
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
