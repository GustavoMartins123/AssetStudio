using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis;

public sealed class Il2CppDumperArtifactImporter
{
    public const int DefaultMaxIndexedTypes = 50_000;
    public const int DefaultMaxSampleSymbols = 2_000;
    private const int InitialJsonBufferSize = 64 * 1024;
    private const int MaxJsonBufferSize = 16 * 1024 * 1024;

    private readonly int _maxIndexedTypes;
    private readonly int _maxSampleSymbols;

    public Il2CppDumperArtifactImporter(
        int maxIndexedTypes = DefaultMaxIndexedTypes,
        int maxSampleSymbols = DefaultMaxSampleSymbols)
    {
        _maxIndexedTypes = Math.Max(1, maxIndexedTypes);
        _maxSampleSymbols = Math.Max(1, maxSampleSymbols);
    }

    public Il2CppScriptIndex ImportArtifacts(
        string projectRoot,
        IReadOnlyList<string> artifactPaths,
        List<string> warnings,
        CancellationToken cancellationToken = default)
    {
        var index = new Il2CppScriptIndex
        {
            ProjectRoot = projectRoot,
            GeneratedAtUtc = DateTime.UtcNow
        };

        if (artifactPaths == null || artifactPaths.Count == 0)
        {
            return index;
        }

        foreach (var artifactPath in artifactPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
            {
                continue;
            }

            var fileName = Path.GetFileName(artifactPath);
            if (string.Equals(fileName, "script.json", StringComparison.OrdinalIgnoreCase))
            {
                index.SourceArtifacts.Add(artifactPath);
                ImportScriptJson(artifactPath, index, warnings, cancellationToken);
            }
            else if (string.Equals(fileName, "dump.cs", StringComparison.OrdinalIgnoreCase))
            {
                index.SourceArtifacts.Add(artifactPath);
                ImportDumpCs(artifactPath, index, warnings, cancellationToken);
            }
            else if (string.Equals(fileName, "il2cpp.h", StringComparison.OrdinalIgnoreCase))
            {
                index.SourceArtifacts.Add(artifactPath);
                ImportIl2CppHeader(artifactPath, index, warnings, cancellationToken);
            }
        }

        return index;
    }

    public void ImportScriptJson(
        string scriptJsonPath,
        Il2CppScriptIndex index,
        List<string> warnings,
        CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialJsonBufferSize);
        try
        {
            using var stream = new FileStream(scriptJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, InitialJsonBufferSize);
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
            string? currentEntryProp = null;
            string? pendingEntryName = null;
            ulong pendingEntryAddress = 0;

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
                    if (bytesInBuffer >= 3
                        && buffer[0] == 0xEF
                        && buffer[1] == 0xBB
                        && buffer[2] == 0xBF)
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
                    ref currentEntryProp,
                    ref pendingEntryName,
                    ref pendingEntryAddress,
                    index);

                if (consumed > 0)
                {
                    var remaining = bytesInBuffer - consumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                    }

                    bytesInBuffer = remaining;
                }
                else if (!isFinalBlock)
                {
                    if (bytesInBuffer == buffer.Length)
                    {
                        if (buffer.Length >= MaxJsonBufferSize)
                        {
                            warnings.Add($"Stopped reading script.json ({Path.GetFileName(scriptJsonPath)}): JSON token exceeded {MaxJsonBufferSize:N0} byte streaming limit.");
                            break;
                        }

                        var nextBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxJsonBufferSize));
                        Buffer.BlockCopy(buffer, 0, nextBuffer, 0, bytesInBuffer);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = nextBuffer;
                    }
                }

                if (isFinalBlock && (bytesInBuffer == 0 || consumed == 0))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed while streaming script.json ({Path.GetFileName(scriptJsonPath)}): {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int ProcessScriptJsonChunk(
        ReadOnlySpan<byte> span,
        bool isFinalBlock,
        ref JsonReaderState readerState,
        ref int depth,
        ref string? activeSection,
        ref bool insideSectionArray,
        ref bool insideEntryObject,
        ref string? currentEntryProp,
        ref string? pendingEntryName,
        ref ulong pendingEntryAddress,
        Il2CppScriptIndex index)
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
                        currentEntryProp = null;
                        pendingEntryName = null;
                        pendingEntryAddress = 0;
                    }
                    break;

                case JsonTokenType.EndObject:
                    if (insideEntryObject && depth == 3)
                    {
                        RecordScriptSectionEntry(activeSection, pendingEntryName, pendingEntryAddress, index);
                        insideEntryObject = false;
                        currentEntryProp = null;
                        pendingEntryName = null;
                        pendingEntryAddress = 0;
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
                        var propName = reader.GetString();
                        activeSection = IsKnownScriptSection(propName) ? propName : null;
                    }
                    else if (insideEntryObject && depth == 3)
                    {
                        currentEntryProp = reader.GetString();
                    }
                    break;

                case JsonTokenType.String:
                    if (insideEntryObject && depth == 3 && string.Equals(currentEntryProp, "Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingEntryName = reader.GetString();
                    }
                    break;

                case JsonTokenType.Number:
                    if (insideSectionArray && depth == 2 && string.Equals(activeSection, "Addresses", StringComparison.OrdinalIgnoreCase))
                    {
                        index.AddressCount++;
                    }
                    else if (insideEntryObject && depth == 3 && string.Equals(currentEntryProp, "Address", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TryGetUInt64(out var addr))
                        {
                            pendingEntryAddress = addr;
                        }
                    }
                    break;
            }
        }

        readerState = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    private void RecordScriptSectionEntry(
        string? section,
        string? name,
        ulong address,
        Il2CppScriptIndex index)
    {
        if (string.IsNullOrEmpty(section))
        {
            return;
        }

        if (string.Equals(section, "ScriptMethod", StringComparison.OrdinalIgnoreCase))
        {
            index.ScriptMethodCount++;
            AddSampleSymbol(index, "ScriptMethod", name, address);
        }
        else if (string.Equals(section, "ScriptMetadata", StringComparison.OrdinalIgnoreCase))
        {
            index.ScriptMetadataCount++;
            AddSampleSymbol(index, "ScriptMetadata", name, address);
        }
        else if (string.Equals(section, "ScriptMetadataMethod", StringComparison.OrdinalIgnoreCase))
        {
            index.ScriptMetadataMethodCount++;
            AddSampleSymbol(index, "ScriptMetadataMethod", name, address);
        }
        else if (string.Equals(section, "ScriptString", StringComparison.OrdinalIgnoreCase))
        {
            index.ScriptStringCount++;
        }
    }

    private void AddSampleSymbol(Il2CppScriptIndex index, string kind, string? name, ulong address)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (index.SampleScriptSymbols.Count >= _maxSampleSymbols)
        {
            index.EntriesTruncated = true;
            return;
        }

        index.SampleScriptSymbols.Add(new Il2CppScriptSymbolEntry
        {
            Kind = kind,
            Name = name,
            Address = address
        });
    }

    private static bool IsKnownScriptSection(string? name)
    {
        return string.Equals(name, "ScriptMethod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ScriptMetadata", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ScriptMetadataMethod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ScriptString", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Addresses", StringComparison.OrdinalIgnoreCase);
    }

    public void ImportDumpCs(
        string dumpCsPath,
        Il2CppScriptIndex index,
        List<string> warnings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(dumpCsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);

            var currentNamespace = string.Empty;
            Il2CppTypeEntry? currentType = null;
            var inFieldsSection = false;
            var braceDepth = 0;
            var typeBraceDepth = -1;
            var lineNumber = 0;

            while (reader.ReadLine() is { } rawLine)
            {
                if ((++lineNumber & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var trimmed = rawLine.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.StartsWith("// Namespace:", StringComparison.Ordinal))
                {
                    currentNamespace = trimmed.Substring("// Namespace:".Length).Trim();
                    continue;
                }

                if (currentType != null)
                {
                    if (trimmed.Equals("// Fields", StringComparison.Ordinal))
                    {
                        inFieldsSection = true;
                        continue;
                    }

                    if (trimmed.StartsWith("// Properties", StringComparison.Ordinal)
                        || trimmed.StartsWith("// Methods", StringComparison.Ordinal)
                        || trimmed.StartsWith("// Events", StringComparison.Ordinal))
                    {
                        inFieldsSection = false;
                        continue;
                    }
                }

                if (TryParseDumpCsTypeDeclaration(trimmed, currentNamespace, out var parsedType))
                {
                    index.DumpTypeCount++;
                    if (parsedType.IsMonoBehaviour)
                    {
                        index.MonoBehaviourCount++;
                    }

                    if (parsedType.IsScriptableObject)
                    {
                        index.ScriptableObjectCount++;
                    }

                    if (index.Types.Count < _maxIndexedTypes)
                    {
                        index.Types.Add(parsedType);
                    }
                    else
                    {
                        index.EntriesTruncated = true;
                    }

                    currentType = parsedType;
                    inFieldsSection = false;
                    typeBraceDepth = braceDepth + 1;
                }
                else if (currentType != null
                    && inFieldsSection
                    && braceDepth == typeBraceDepth
                    && !trimmed.StartsWith("//", StringComparison.Ordinal)
                    && !trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.IndexOf(';') >= 0)
                {
                    currentType.FieldCount++;
                }

                UpdateBraceDepth(trimmed, ref braceDepth);
                if (currentType != null && braceDepth < typeBraceDepth && trimmed.IndexOf('}') >= 0)
                {
                    currentType = null;
                    inFieldsSection = false;
                    typeBraceDepth = -1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed while streaming dump.cs ({Path.GetFileName(dumpCsPath)}): {ex.Message}");
        }
    }

    private static bool TryParseDumpCsTypeDeclaration(string trimmedLine, string currentNamespace, out Il2CppTypeEntry entry)
    {
        entry = null!;
        if (trimmedLine.StartsWith("//", StringComparison.Ordinal) || trimmedLine.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var commentIndex = trimmedLine.IndexOf("//", StringComparison.Ordinal);
        int? typeDefIndex = null;
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

                if (endIdx > 0 && int.TryParse(numSpan.Slice(0, endIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIdx))
                {
                    typeDefIndex = parsedIdx;
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
        if (afterKeyword.Length == 0)
        {
            return false;
        }

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

        string typeName;
        var baseType = string.Empty;
        var colonIdx = afterKeyword.IndexOf(':');
        if (colonIdx >= 0)
        {
            typeName = afterKeyword.Substring(0, colonIdx).Trim();
            var basePart = afterKeyword.Substring(colonIdx + 1).Trim();
            var commaIdx = basePart.IndexOf(',');
            baseType = (commaIdx >= 0 ? basePart.Substring(0, commaIdx) : basePart).Trim();
        }
        else
        {
            typeName = afterKeyword.Trim();
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        entry = new Il2CppTypeEntry
        {
            Namespace = currentNamespace,
            Name = typeName,
            BaseType = baseType,
            TypeDefIndex = typeDefIndex,
            IsMonoBehaviour = IsBaseTypeMatch(baseType, "MonoBehaviour"),
            IsScriptableObject = IsBaseTypeMatch(baseType, "ScriptableObject"),
            Source = "dump.cs"
        };
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

    private static bool IsBaseTypeMatch(string baseType, string expectedSimpleName)
    {
        if (string.IsNullOrWhiteSpace(baseType))
        {
            return false;
        }

        var genericIdx = baseType.IndexOf('<');
        var candidate = genericIdx > 0 ? baseType.Substring(0, genericIdx) : baseType;
        return string.Equals(candidate, expectedSimpleName, StringComparison.Ordinal)
            || candidate.EndsWith("." + expectedSimpleName, StringComparison.Ordinal);
    }

    private static void UpdateBraceDepth(string trimmedLine, ref int braceDepth)
    {
        var commentIdx = trimmedLine.IndexOf("//", StringComparison.Ordinal);
        var limit = commentIdx >= 0 ? commentIdx : trimmedLine.Length;
        for (var i = 0; i < limit; i++)
        {
            if (trimmedLine[i] == '{')
            {
                braceDepth++;
            }
            else if (trimmedLine[i] == '}' && braceDepth > 0)
            {
                braceDepth--;
            }
        }
    }

    public void ImportIl2CppHeader(
        string headerPath,
        Il2CppScriptIndex index,
        List<string> warnings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(headerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);

            var lineNumber = 0;
            while (reader.ReadLine() is { } rawLine)
            {
                if ((++lineNumber & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var trimmed = rawLine.TrimStart();
                if (trimmed.StartsWith("struct ", StringComparison.Ordinal)
                    && trimmed.IndexOf(';') < 0)
                {
                    index.HeaderStructCount++;
                }
                else if (trimmed.StartsWith("typedef ", StringComparison.Ordinal))
                {
                    index.HeaderTypedefCount++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed while streaming il2cpp.h ({Path.GetFileName(headerPath)}): {ex.Message}");
        }
    }
}
