using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace AssetStudio.Avalonia.Services.Scripting;

public sealed class Il2CppDumpTypeSpan
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string BaseName { get; init; }
    public int? TypeDefIndex { get; init; }
    public long StartByteOffset { get; init; }
    public long EndByteOffset { get; init; }

    public int ByteLength => (int)Math.Clamp(EndByteOffset - StartByteOffset, 0, int.MaxValue);
}

public sealed class GhidraSymbolStreamEntry
{
    public string Name { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public string TypeSignature { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public ulong Address { get; init; }
}

public sealed class Il2CppDumpScriptService
{
    private const int ReadChunkSize = 64 * 1024;
    private const int MaxJsonBufferSize = 16 * 1024 * 1024;
    private const int MaxExtractBytes = 4 * 1024 * 1024;

    private static readonly Regex RvaCommentRegex = new(
        @"//\s*RVA:\s*0x(?<rva>[0-9A-Fa-f]+)(?:\s+Offset:\s*0x(?<off>[0-9A-Fa-f]+))?(?:\s+VA:\s*0x(?<va>[0-9A-Fa-f]+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class CachedDumpIndex
    {
        public required string FilePath { get; init; }
        public long FileLength { get; init; }
        public long LastWriteTicks { get; init; }
        public Dictionary<string, List<Il2CppDumpTypeSpan>> ByFullName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<Il2CppDumpTypeSpan>> BySimpleName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalTypes { get; set; }
    }

    private readonly object _cacheLock = new();
    private CachedDumpIndex? _cachedDumpIndex;

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedDumpIndex = null;
        }
    }

    public ScriptDecompilationResult? TryExtractFromDumpCs(
        string dumpCsPath,
        ScriptLookupRequest request,
        string? scriptJsonPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dumpCsPath) || !File.Exists(dumpCsPath))
        {
            return null;
        }

        var className = request.ClassName?.Trim();
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        var index = GetOrBuildDumpIndex(dumpCsPath, cancellationToken);
        var span = FindTypeSpan(index, request.Namespace, className);
        if (span == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rawBlock = ReadSliceUtf8(dumpCsPath, span.StartByteOffset, span.ByteLength);
        var dumpMethods = ExtractMethodsFromDumpBlock(rawBlock, span.Name);

        var scriptJsonMethods = !string.IsNullOrWhiteSpace(scriptJsonPath) && File.Exists(scriptJsonPath)
            ? QueryScriptJsonMethodsForClass(scriptJsonPath, span.Namespace, span.Name, cancellationToken)
            : new List<ScriptMethodSymbol>();

        var mergedMethods = MergeMethodSymbols(dumpMethods, scriptJsonMethods);
        var formattedSource = FormatDumpSource(dumpCsPath, span, rawBlock, scriptJsonMethods);

        var fullTitle = string.IsNullOrWhiteSpace(span.Namespace)
            ? span.Name
            : $"{span.Namespace}.{span.Name}";

        var summaryParts = new List<string> { Path.GetFileName(dumpCsPath) };
        if (span.TypeDefIndex.HasValue)
        {
            summaryParts.Add($"TypeDefIndex: {span.TypeDefIndex.Value}");
        }
        if (mergedMethods.Count > 0)
        {
            summaryParts.Add($"{mergedMethods.Count} method RVA{(mergedMethods.Count == 1 ? string.Empty : "s")}");
        }
        summaryParts.Add("IL2CPP (dump.cs)");

        return new ScriptDecompilationResult
        {
            Success = true,
            SourceKind = ScriptSourceKind.Il2CppDumpCs,
            BackendBadge = "IL2CPP (dump.cs)",
            Title = fullTitle,
            AssemblyName = request.AssemblyName,
            Namespace = span.Namespace,
            ClassName = span.Name,
            SourceFilePath = Path.GetFullPath(dumpCsPath),
            SourceCode = formattedSource,
            SummaryText = string.Join(" • ", summaryParts),
            TypeDefIndex = span.TypeDefIndex,
            MethodSymbols = mergedMethods
        };
    }

    public ScriptDecompilationResult? TryBuildScriptJsonOutline(
        string scriptJsonPath,
        ScriptLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptJsonPath) || !File.Exists(scriptJsonPath) || string.IsNullOrWhiteSpace(request.ClassName))
        {
            return null;
        }

        var methods = QueryScriptJsonMethodsForClass(scriptJsonPath, request.Namespace, request.ClassName, cancellationToken);
        if (methods.Count == 0)
        {
            return null;
        }

        var fullTitle = string.IsNullOrWhiteSpace(request.Namespace)
            ? request.ClassName
            : $"{request.Namespace}.{request.ClassName}";

        var sb = new StringBuilder();
        sb.AppendLine("// Source: IL2CPP script.json symbol outline (dump.cs not present)");
        if (!string.IsNullOrWhiteSpace(request.AssemblyName))
        {
            sb.AppendLine($"// Assembly: {request.AssemblyName}");
        }
        sb.AppendLine($"// Type: {fullTitle}");
        sb.AppendLine($"// Matched Methods: {methods.Count}");
        sb.AppendLine();

        var indent = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            sb.AppendLine($"namespace {request.Namespace}");
            sb.AppendLine("{");
            indent = "    ";
        }

        sb.AppendLine($"{indent}public partial class {request.ClassName}");
        sb.AppendLine($"{indent}{{");
        foreach (var method in methods)
        {
            sb.AppendLine($"{indent}    // RVA: 0x{method.Address:X} | Symbol: {method.FullName}");
            var decl = !string.IsNullOrWhiteSpace(method.Signature)
                ? method.Signature
                : $"void {method.MethodName}()";
            sb.AppendLine($"{indent}    // {decl}");
            sb.AppendLine();
        }
        sb.AppendLine($"{indent}}}");
        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            sb.AppendLine("}");
        }

        return new ScriptDecompilationResult
        {
            Success = true,
            SourceKind = ScriptSourceKind.Il2CppScriptJsonOutline,
            BackendBadge = "IL2CPP (script.json)",
            Title = fullTitle,
            AssemblyName = request.AssemblyName,
            Namespace = request.Namespace,
            ClassName = request.ClassName,
            SourceFilePath = Path.GetFullPath(scriptJsonPath),
            SourceCode = sb.ToString().TrimEnd(),
            SummaryText = $"{Path.GetFileName(scriptJsonPath)} • {methods.Count} method RVA{(methods.Count == 1 ? string.Empty : "s")} • IL2CPP (script.json)",
            MethodSymbols = methods
        };
    }

    public List<ScriptMethodSymbol> QueryScriptJsonMethodsForClass(
        string scriptJsonPath,
        string? ns,
        string className,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<ScriptMethodSymbol>();
        if (string.IsNullOrWhiteSpace(scriptJsonPath) || !File.Exists(scriptJsonPath) || string.IsNullOrWhiteSpace(className))
        {
            return matches;
        }

        var prefixes = BuildIl2CppMethodPrefixes(ns, className);
        if (prefixes.Count == 0)
        {
            return matches;
        }

        StreamScriptJson(
            scriptJsonPath,
            onMethod: entry =>
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    return;
                }

                var sepIdx = entry.Name.IndexOf("$$", StringComparison.Ordinal);
                if (sepIdx <= 0)
                {
                    return;
                }

                var typePart = entry.Name.Substring(0, sepIdx);
                if (!MatchesAnyClassPrefix(typePart, prefixes, className))
                {
                    return;
                }

                var methodName = entry.Name.Substring(sepIdx + 2);
                matches.Add(new ScriptMethodSymbol
                {
                    ClassName = typePart,
                    MethodName = methodName,
                    FullName = entry.Name,
                    Signature = entry.Signature,
                    TypeSignature = entry.TypeSignature,
                    Address = entry.Address,
                    Source = "script.json"
                });
            },
            onString: null,
            onMetadata: null,
            cancellationToken: cancellationToken);

        return matches;
    }

    public static void StreamScriptJson(
        string scriptJsonPath,
        Action<GhidraSymbolStreamEntry>? onMethod,
        Action<GhidraSymbolStreamEntry>? onString,
        Action<GhidraSymbolStreamEntry>? onMetadata,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptJsonPath) || !File.Exists(scriptJsonPath))
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkSize);
        try
        {
            using var stream = new FileStream(scriptJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadChunkSize);
            var readerState = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            var bytesInBuffer = 0;
            var isFinalBlock = false;
            var checkedBom = false;

            var depth = 0;
            string? activeSection = null;
            var insideSectionArray = false;
            var insideEntryObject = false;
            string? currentProp = null;
            string? pendingName = null;
            string? pendingSignature = null;
            string? pendingTypeSignature = null;
            string? pendingValue = null;
            ulong pendingAddress = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!isFinalBlock && bytesInBuffer < buffer.Length)
                {
                    var read = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                    if (read == 0)
                    {
                        isFinalBlock = true;
                    }
                    else
                    {
                        bytesInBuffer += read;
                    }
                }

                if (!checkedBom && (bytesInBuffer >= 3 || isFinalBlock))
                {
                    checkedBom = true;
                    if (bytesInBuffer >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    {
                        bytesInBuffer -= 3;
                        if (bytesInBuffer > 0)
                        {
                            Buffer.BlockCopy(buffer, 3, buffer, 0, bytesInBuffer);
                        }
                    }
                }

                var consumed = ProcessScriptJsonChunk(
                    buffer.AsSpan(0, bytesInBuffer),
                    isFinalBlock,
                    ref readerState,
                    ref depth,
                    ref activeSection,
                    ref insideSectionArray,
                    ref insideEntryObject,
                    ref currentProp,
                    ref pendingName,
                    ref pendingSignature,
                    ref pendingTypeSignature,
                    ref pendingValue,
                    ref pendingAddress,
                    onMethod,
                    onString,
                    onMetadata);

                if (consumed > 0)
                {
                    var remaining = bytesInBuffer - consumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                    }
                    bytesInBuffer = remaining;
                }
                else if (!isFinalBlock && bytesInBuffer == buffer.Length)
                {
                    if (buffer.Length >= MaxJsonBufferSize)
                    {
                        break;
                    }

                    var nextBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxJsonBufferSize));
                    Buffer.BlockCopy(buffer, 0, nextBuffer, 0, bytesInBuffer);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = nextBuffer;
                }

                if (isFinalBlock && (bytesInBuffer == 0 || consumed == 0))
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ProcessScriptJsonChunk(
        ReadOnlySpan<byte> span,
        bool isFinalBlock,
        ref JsonReaderState readerState,
        ref int depth,
        ref string? activeSection,
        ref bool insideSectionArray,
        ref bool insideEntryObject,
        ref string? currentProp,
        ref string? pendingName,
        ref string? pendingSignature,
        ref string? pendingTypeSignature,
        ref string? pendingValue,
        ref ulong pendingAddress,
        Action<GhidraSymbolStreamEntry>? onMethod,
        Action<GhidraSymbolStreamEntry>? onString,
        Action<GhidraSymbolStreamEntry>? onMetadata)
    {
        var reader = new Utf8JsonReader(span, isFinalBlock, readerState);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    depth++;
                    if (insideSectionArray && depth == 3)
                    {
                        insideEntryObject = true;
                        currentProp = null;
                        pendingName = null;
                        pendingSignature = null;
                        pendingTypeSignature = null;
                        pendingValue = null;
                        pendingAddress = 0;
                    }
                    break;

                case JsonTokenType.EndObject:
                    if (insideEntryObject && depth == 3)
                    {
                        EmitScriptJsonEntry(
                            activeSection,
                            pendingName,
                            pendingSignature,
                            pendingTypeSignature,
                            pendingValue,
                            pendingAddress,
                            onMethod,
                            onString,
                            onMetadata);
                        insideEntryObject = false;
                        currentProp = null;
                        pendingName = null;
                        pendingSignature = null;
                        pendingTypeSignature = null;
                        pendingValue = null;
                        pendingAddress = 0;
                    }
                    depth--;
                    break;

                case JsonTokenType.StartArray:
                    depth++;
                    if (depth == 2 && activeSection != null)
                    {
                        insideSectionArray = true;
                    }
                    break;

                case JsonTokenType.EndArray:
                    if (depth == 2 && insideSectionArray)
                    {
                        insideSectionArray = false;
                        activeSection = null;
                    }
                    depth--;
                    break;

                case JsonTokenType.PropertyName:
                    if (depth == 1)
                    {
                        var section = reader.GetString();
                        activeSection = IsExportableScriptSection(section) ? section : null;
                    }
                    else if (insideEntryObject && depth == 3)
                    {
                        currentProp = reader.GetString();
                    }
                    break;

                case JsonTokenType.String:
                    if (insideEntryObject && depth == 3 && !string.IsNullOrEmpty(currentProp))
                    {
                        if (string.Equals(currentProp, "Name", StringComparison.OrdinalIgnoreCase))
                        {
                            pendingName = reader.GetString();
                        }
                        else if (string.Equals(currentProp, "Signature", StringComparison.OrdinalIgnoreCase))
                        {
                            pendingSignature = reader.GetString();
                        }
                        else if (string.Equals(currentProp, "TypeSignature", StringComparison.OrdinalIgnoreCase))
                        {
                            pendingTypeSignature = reader.GetString();
                        }
                        else if (string.Equals(currentProp, "Value", StringComparison.OrdinalIgnoreCase))
                        {
                            pendingValue = reader.GetString();
                        }
                    }
                    break;

                case JsonTokenType.Number:
                    if (insideEntryObject && depth == 3 && string.Equals(currentProp, "Address", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TryGetUInt64(out var addr))
                        {
                            pendingAddress = addr;
                        }
                    }
                    break;
            }
        }

        readerState = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    private static void EmitScriptJsonEntry(
        string? section,
        string? name,
        string? signature,
        string? typeSignature,
        string? value,
        ulong address,
        Action<GhidraSymbolStreamEntry>? onMethod,
        Action<GhidraSymbolStreamEntry>? onString,
        Action<GhidraSymbolStreamEntry>? onMetadata)
    {
        if (string.IsNullOrEmpty(section) || address == 0)
        {
            return;
        }

        if (string.Equals(section, "ScriptMethod", StringComparison.OrdinalIgnoreCase))
        {
            if (onMethod != null && !string.IsNullOrWhiteSpace(name))
            {
                onMethod(new GhidraSymbolStreamEntry
                {
                    Name = name,
                    Signature = signature ?? string.Empty,
                    TypeSignature = typeSignature ?? string.Empty,
                    Address = address
                });
            }
        }
        else if (string.Equals(section, "ScriptString", StringComparison.OrdinalIgnoreCase))
        {
            if (onString != null && value != null)
            {
                onString(new GhidraSymbolStreamEntry
                {
                    Name = name ?? string.Empty,
                    Value = value,
                    Address = address
                });
            }
        }
        else if (string.Equals(section, "ScriptMetadata", StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, "ScriptMetadataMethod", StringComparison.OrdinalIgnoreCase))
        {
            if (onMetadata != null && !string.IsNullOrWhiteSpace(name))
            {
                onMetadata(new GhidraSymbolStreamEntry
                {
                    Name = name,
                    Signature = signature ?? string.Empty,
                    Address = address
                });
            }
        }
    }

    private static bool IsExportableScriptSection(string? name)
    {
        return string.Equals(name, "ScriptMethod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ScriptString", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ScriptMetadata", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ScriptMetadataMethod", StringComparison.OrdinalIgnoreCase);
    }

    private CachedDumpIndex GetOrBuildDumpIndex(string dumpCsPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(dumpCsPath);
        var info = new FileInfo(fullPath);

        lock (_cacheLock)
        {
            if (_cachedDumpIndex != null
                && string.Equals(_cachedDumpIndex.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)
                && _cachedDumpIndex.FileLength == info.Length
                && _cachedDumpIndex.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                return _cachedDumpIndex;
            }
        }

        var built = BuildDumpIndex(fullPath, info.Length, info.LastWriteTimeUtc.Ticks, cancellationToken);
        lock (_cacheLock)
        {
            _cachedDumpIndex = built;
        }
        return built;
    }

    private static CachedDumpIndex BuildDumpIndex(
        string fullPath,
        long fileLength,
        long lastWriteTicks,
        CancellationToken cancellationToken)
    {
        var index = new CachedDumpIndex
        {
            FilePath = fullPath,
            FileLength = fileLength,
            LastWriteTicks = lastWriteTicks
        };

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadChunkSize);
        var lineReader = new ByteOffsetLineReader(stream);

        var currentNamespace = string.Empty;
        long pendingBlockStartOffset = -1;
        string? activeNs = null;
        string? activeName = null;
        int? activeTypeDefIndex = null;
        long activeStartOffset = -1;
        var braceDepth = 0;
        var activeTypeBraceDepth = -1;
        var lineCount = 0;

        while (lineReader.ReadLine(out var lineText, out var lineStartOffset, out var lineEndOffset))
        {
            if ((++lineCount & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var trimmed = lineText.Trim();
            if (trimmed.Length == 0)
            {
                if (activeName == null)
                {
                    pendingBlockStartOffset = -1;
                }
                continue;
            }

            if (trimmed.StartsWith("// Namespace:", StringComparison.Ordinal))
            {
                currentNamespace = trimmed.Substring("// Namespace:".Length).Trim();
                if (activeName == null)
                {
                    pendingBlockStartOffset = lineStartOffset;
                }
                continue;
            }

            if (activeName == null && trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                if (pendingBlockStartOffset < 0)
                {
                    pendingBlockStartOffset = lineStartOffset;
                }
                continue;
            }

            if (activeName == null && TryParseTypeHeader(trimmed, out var parsedTypeName, out var parsedTypeDefIndex))
            {
                activeNs = currentNamespace;
                activeName = parsedTypeName;
                activeTypeDefIndex = parsedTypeDefIndex;
                activeStartOffset = pendingBlockStartOffset >= 0 ? pendingBlockStartOffset : lineStartOffset;
                activeTypeBraceDepth = braceDepth + 1;
                pendingBlockStartOffset = -1;
            }

            UpdateBraceDepth(trimmed, ref braceDepth);

            if (activeName != null && braceDepth < activeTypeBraceDepth && trimmed.IndexOf('}') >= 0)
            {
                RegisterSpan(index, activeNs ?? string.Empty, activeName, activeTypeDefIndex, activeStartOffset, lineEndOffset);
                activeNs = null;
                activeName = null;
                activeTypeDefIndex = null;
                activeStartOffset = -1;
                activeTypeBraceDepth = -1;
            }
        }

        // Flush unclosed trailing type at EOF if any
        if (activeName != null && activeStartOffset >= 0)
        {
            RegisterSpan(index, activeNs ?? string.Empty, activeName, activeTypeDefIndex, activeStartOffset, fileLength);
        }

        return index;
    }

    private static void RegisterSpan(
        CachedDumpIndex index,
        string ns,
        string rawName,
        int? typeDefIndex,
        long startOffset,
        long endOffset)
    {
        var baseName = StripGenericSuffix(rawName);
        var span = new Il2CppDumpTypeSpan
        {
            Namespace = ns,
            Name = rawName,
            BaseName = baseName,
            TypeDefIndex = typeDefIndex,
            StartByteOffset = startOffset,
            EndByteOffset = endOffset
        };

        index.TotalTypes++;

        var fullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ns))
        {
            fullKeys.Add($"{ns}.{rawName}");
            fullKeys.Add($"{ns}.{baseName}");
        }
        else
        {
            fullKeys.Add(rawName);
            fullKeys.Add(baseName);
        }

        foreach (var key in fullKeys)
        {
            if (!index.ByFullName.TryGetValue(key, out var list))
            {
                list = new List<Il2CppDumpTypeSpan>(1);
                index.ByFullName[key] = list;
            }
            list.Add(span);
        }

        foreach (var simpleKey in new[] { rawName, baseName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!index.BySimpleName.TryGetValue(simpleKey, out var simpleList))
            {
                simpleList = new List<Il2CppDumpTypeSpan>(1);
                index.BySimpleName[simpleKey] = simpleList;
            }
            simpleList.Add(span);
        }
    }

    private static Il2CppDumpTypeSpan? FindTypeSpan(CachedDumpIndex index, string? ns, string className)
    {
        var cleanNs = ns?.Trim() ?? string.Empty;
        var normalizedClass = className.Trim().Replace('/', '.').Replace('+', '.');
        var baseClass = StripGenericSuffix(normalizedClass);

        // 1. Exact full name (Namespace.ClassName)
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(cleanNs))
        {
            candidates.Add($"{cleanNs}.{normalizedClass}");
            candidates.Add($"{cleanNs}.{baseClass}");
        }
        candidates.Add(normalizedClass);
        candidates.Add(baseClass);

        // Also handle nested short name (Outer.Inner -> Inner)
        var lastDot = normalizedClass.LastIndexOf('.');
        var leafName = lastDot >= 0 ? normalizedClass.Substring(lastDot + 1) : normalizedClass;
        var leafBase = StripGenericSuffix(leafName);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (index.ByFullName.TryGetValue(candidate, out var list) && list.Count > 0)
            {
                return list[0];
            }
        }

        // 2. Fallback by simple name, preferring matching namespace if available
        foreach (var simpleCandidate in new[] { normalizedClass, baseClass, leafName, leafBase }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (index.BySimpleName.TryGetValue(simpleCandidate, out var list) && list.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(cleanNs))
                {
                    var nsMatch = list.FirstOrDefault(s => string.Equals(s.Namespace, cleanNs, StringComparison.OrdinalIgnoreCase));
                    if (nsMatch != null)
                    {
                        return nsMatch;
                    }
                }
                return list[0];
            }
        }

        return null;
    }

    private static string ReadSliceUtf8(string filePath, long startOffset, int byteLength)
    {
        var clampedLength = Math.Min(byteLength, MaxExtractBytes);
        if (clampedLength <= 0)
        {
            return string.Empty;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(startOffset, SeekOrigin.Begin);
        var bytes = new byte[clampedLength];
        var totalRead = 0;
        while (totalRead < clampedLength)
        {
            var read = stream.Read(bytes, totalRead, clampedLength - totalRead);
            if (read <= 0)
            {
                break;
            }
            totalRead += read;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, totalRead);
        if (byteLength > MaxExtractBytes)
        {
            text += $"{Environment.NewLine}// [Truncated after {MaxExtractBytes:N0} bytes]";
        }
        return text.TrimEnd();
    }

    internal static List<ScriptMethodSymbol> ExtractMethodsFromDumpBlock(string dumpBlock, string className)
    {
        var methods = new List<ScriptMethodSymbol>();
        if (string.IsNullOrWhiteSpace(dumpBlock))
        {
            return methods;
        }

        var lines = dumpBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = RvaCommentRegex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            if (!ulong.TryParse(match.Groups["rva"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rva) || rva == 0)
            {
                continue;
            }

            ulong offset = 0;
            if (match.Groups["off"].Success)
            {
                ulong.TryParse(match.Groups["off"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
            }

            ulong va = 0;
            if (match.Groups["va"].Success)
            {
                ulong.TryParse(match.Groups["va"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out va);
            }

            var signatureLine = string.Empty;
            for (var j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate.StartsWith("//", StringComparison.Ordinal) || candidate.StartsWith("[", StringComparison.Ordinal))
                {
                    continue;
                }
                signatureLine = candidate;
                break;
            }

            var methodName = ExtractMethodNameFromSignature(signatureLine);
            if (string.IsNullOrWhiteSpace(methodName))
            {
                methodName = $"sub_{rva:X}";
            }

            methods.Add(new ScriptMethodSymbol
            {
                ClassName = className,
                MethodName = methodName,
                FullName = $"{className}$${methodName}",
                Signature = signatureLine,
                Address = rva,
                Offset = offset,
                VirtualAddress = va,
                Source = "dump.cs"
            });
        }

        return methods;
    }

    private static List<ScriptMethodSymbol> MergeMethodSymbols(
        List<ScriptMethodSymbol> dumpMethods,
        List<ScriptMethodSymbol> scriptJsonMethods)
    {
        var merged = new List<ScriptMethodSymbol>(dumpMethods.Count + scriptJsonMethods.Count);
        var seenAddresses = new HashSet<ulong>();

        foreach (var item in scriptJsonMethods)
        {
            if (item.Address > 0 && seenAddresses.Add(item.Address))
            {
                merged.Add(item);
            }
        }

        foreach (var item in dumpMethods)
        {
            if (item.Address > 0 && seenAddresses.Add(item.Address))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static string FormatDumpSource(
        string dumpCsPath,
        Il2CppDumpTypeSpan span,
        string rawBlock,
        List<ScriptMethodSymbol> scriptJsonMethods)
    {
        var sb = new StringBuilder(rawBlock.Length + 512);
        sb.AppendLine($"// Source: {Path.GetFileName(dumpCsPath)} (IL2CPP Dump)");
        if (span.TypeDefIndex.HasValue)
        {
            sb.AppendLine($"// TypeDefIndex: {span.TypeDefIndex.Value}");
        }
        if (scriptJsonMethods.Count > 0)
        {
            sb.AppendLine($"// Correlated script.json symbols: {scriptJsonMethods.Count}");
        }
        sb.AppendLine();
        sb.AppendLine(rawBlock);

        if (scriptJsonMethods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("// --- Matched script.json RVAs (for Ghidra / Native Analysis) ---");
            foreach (var sym in scriptJsonMethods.Take(200))
            {
                var sigSuffix = string.IsNullOrWhiteSpace(sym.Signature) ? string.Empty : $" | {sym.Signature}";
                sb.AppendLine($"// 0x{sym.Address:X8}  {sym.FullName}{sigSuffix}");
            }
            if (scriptJsonMethods.Count > 200)
            {
                sb.AppendLine($"// ... and {scriptJsonMethods.Count - 200} more symbols (included in Ghidra export)");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryParseTypeHeader(string trimmedLine, out string typeName, out int? typeDefIndex)
    {
        typeName = string.Empty;
        typeDefIndex = null;

        if (trimmedLine.StartsWith("//", StringComparison.Ordinal) || trimmedLine.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var commentIndex = trimmedLine.IndexOf("//", StringComparison.Ordinal);
        string codePart;
        if (commentIndex >= 0)
        {
            codePart = trimmedLine.Substring(0, commentIndex).Trim();
            var commentPart = trimmedLine.Substring(commentIndex + 2);
            const string marker = "TypeDefIndex:";
            var markerIdx = commentPart.IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx >= 0)
            {
                var numSpan = commentPart.AsSpan(markerIdx + marker.Length).Trim();
                var endIdx = 0;
                while (endIdx < numSpan.Length && char.IsDigit(numSpan[endIdx]))
                {
                    endIdx++;
                }
                if (endIdx > 0 && int.TryParse(numSpan.Slice(0, endIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    typeDefIndex = parsed;
                }
            }
        }
        else
        {
            codePart = trimmedLine;
        }

        if (codePart.Length == 0 || codePart.IndexOf(';') >= 0 || codePart.IndexOf('(') >= 0)
        {
            return false;
        }

        var keywordIndex = FindTypeKeywordIndex(codePart, out var keywordLength);
        if (keywordIndex < 0)
        {
            return false;
        }

        var afterKeyword = codePart.Substring(keywordIndex + keywordLength).Trim();
        var braceIdx = afterKeyword.IndexOf('{');
        if (braceIdx >= 0)
        {
            afterKeyword = afterKeyword.Substring(0, braceIdx).Trim();
        }
        var whereIdx = afterKeyword.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx >= 0)
        {
            afterKeyword = afterKeyword.Substring(0, whereIdx).Trim();
        }
        var colonIdx = afterKeyword.IndexOf(':');
        if (colonIdx >= 0)
        {
            afterKeyword = afterKeyword.Substring(0, colonIdx).Trim();
        }

        if (string.IsNullOrWhiteSpace(afterKeyword))
        {
            return false;
        }

        typeName = afterKeyword;
        return true;
    }

    private static int FindTypeKeywordIndex(string codePart, out int keywordLength)
    {
        ReadOnlySpan<string> keywords = new[] { "class ", "struct ", "interface ", "enum " };
        foreach (var keyword in keywords)
        {
            var idx = codePart.IndexOf(keyword, StringComparison.Ordinal);
            if (idx == 0 || (idx > 0 && char.IsWhiteSpace(codePart[idx - 1])))
            {
                keywordLength = keyword.Length;
                return idx;
            }
        }

        keywordLength = 0;
        return -1;
    }

    private static string StripGenericSuffix(string name)
    {
        var angleIdx = name.IndexOf('<');
        var tickIdx = name.IndexOf('`');
        var cut = angleIdx >= 0 && tickIdx >= 0
            ? Math.Min(angleIdx, tickIdx)
            : Math.Max(angleIdx, tickIdx);
        return cut > 0 ? name.Substring(0, cut).Trim() : name.Trim();
    }

    private static string ExtractMethodNameFromSignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return string.Empty;
        }

        var parenIdx = signature.IndexOf('(');
        var head = parenIdx > 0 ? signature.Substring(0, parenIdx).Trim() : signature.Trim();
        var genericIdx = head.IndexOf('<');
        if (genericIdx > 0)
        {
            head = head.Substring(0, genericIdx).Trim();
        }

        var lastSpace = head.LastIndexOf(' ');
        return lastSpace >= 0 ? head.Substring(lastSpace + 1).Trim() : head;
    }

    private static List<string> BuildIl2CppMethodPrefixes(string? ns, string className)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanClass = StripGenericSuffix(className).Replace('/', '.').Replace('+', '.');
        var cleanNs = ns?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(cleanNs))
        {
            results.Add($"{cleanNs}.{cleanClass}");
            results.Add($"{cleanNs.Replace('.', '_')}_{cleanClass.Replace('.', '_')}");
        }
        results.Add(cleanClass);
        results.Add(cleanClass.Replace('.', '_'));
        return results.ToList();
    }

    private static bool MatchesAnyClassPrefix(string typePart, List<string> prefixes, string rawClassName)
    {
        var strippedType = StripGenericSuffix(typePart);
        foreach (var prefix in prefixes)
        {
            if (string.Equals(strippedType, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var cleanSimple = StripGenericSuffix(rawClassName);
        if (strippedType.EndsWith("." + cleanSimple, StringComparison.OrdinalIgnoreCase)
            || strippedType.EndsWith("_" + cleanSimple, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void UpdateBraceDepth(string trimmedLine, ref int braceDepth)
    {
        var inString = false;
        var inChar = false;
        var escaped = false;
        for (var i = 0; i < trimmedLine.Length; i++)
        {
            var c = trimmedLine[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && (inString || inChar))
            {
                escaped = true;
                continue;
            }

            if (!inChar && c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && c == '\'')
            {
                inChar = !inChar;
                continue;
            }

            if (inString || inChar)
            {
                continue;
            }

            if (c == '/' && i + 1 < trimmedLine.Length && trimmedLine[i + 1] == '/')
            {
                break;
            }

            if (c == '{')
            {
                braceDepth++;
            }
            else if (c == '}' && braceDepth > 0)
            {
                braceDepth--;
            }
        }
    }

    private sealed class ByteOffsetLineReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[ReadChunkSize];
        private int _pos;
        private int _len;
        private long _bufferFileOffset;
        private readonly MemoryStream _lineBytes = new(512);
        private bool _checkedBom;

        public ByteOffsetLineReader(Stream stream)
        {
            _stream = stream;
        }

        public bool ReadLine(out string line, out long lineStartOffset, out long lineEndOffset)
        {
            _lineBytes.SetLength(0);
            lineStartOffset = -1;
            lineEndOffset = 0;

            while (true)
            {
                if (_pos >= _len)
                {
                    _bufferFileOffset = _stream.Position;
                    _len = _stream.Read(_buffer, 0, _buffer.Length);
                    _pos = 0;

                    if (!_checkedBom)
                    {
                        _checkedBom = true;
                        if (_len >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
                        {
                            _pos = 3;
                        }
                    }

                    if (_len == 0)
                    {
                        if (_lineBytes.Length == 0)
                        {
                            line = string.Empty;
                            return false;
                        }

                        lineEndOffset = _bufferFileOffset;
                        line = DecodeLine(_lineBytes);
                        return true;
                    }
                }

                if (lineStartOffset < 0)
                {
                    lineStartOffset = _bufferFileOffset + _pos;
                }

                var newlineIdx = Array.IndexOf(_buffer, (byte)'\n', _pos, _len - _pos);
                if (newlineIdx >= 0)
                {
                    var count = newlineIdx - _pos;
                    if (count > 0)
                    {
                        _lineBytes.Write(_buffer, _pos, count);
                    }
                    _pos = newlineIdx + 1;
                    lineEndOffset = _bufferFileOffset + _pos;
                    line = DecodeLine(_lineBytes);
                    return true;
                }

                _lineBytes.Write(_buffer, _pos, _len - _pos);
                _pos = _len;
            }
        }

        private static string DecodeLine(MemoryStream lineBytes)
        {
            var raw = lineBytes.GetBuffer();
            var count = (int)lineBytes.Length;
            if (count > 0 && raw[count - 1] == (byte)'\r')
            {
                count--;
            }

            return count == 0 ? string.Empty : Encoding.UTF8.GetString(raw, 0, count);
        }
    }
}
