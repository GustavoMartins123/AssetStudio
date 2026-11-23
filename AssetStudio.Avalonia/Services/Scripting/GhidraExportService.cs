using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AssetStudio.Avalonia.Services.Scripting;

public enum GhidraExportScope
{
    SelectedClass = 0,
    FullProject = 1
}

public sealed class GhidraExportOptions
{
    public required string OutputDirectory { get; init; }
    public GhidraExportScope Scope { get; init; } = GhidraExportScope.SelectedClass;
    public string BinaryName { get; init; } = "GameAssembly.dll";
    public string BackendLabel { get; init; } = "IL2CPP";
    public string ClassTitle { get; init; } = string.Empty;
    public IReadOnlyList<ScriptMethodSymbol> SelectedClassMethods { get; init; } = Array.Empty<ScriptMethodSymbol>();
    public string? ScriptJsonPath { get; init; }
    public string? Il2CppHeaderPath { get; init; }
    public string? DecompiledSourceCode { get; init; }
    public string? SuggestedSourceFileName { get; init; }
}

public sealed class GhidraExportResult
{
    public bool Success { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public string GhidraScriptPath { get; init; } = string.Empty;
    public string SymbolsJsonPath { get; init; } = string.Empty;
    public string? HeaderOutputPath { get; init; }
    public string? SourceOutputPath { get; init; }
    public string ReadmePath { get; init; } = string.Empty;
    public int MethodCount { get; init; }
    public int StringCount { get; init; }
    public int MetadataCount { get; init; }
    public string SummaryMessage { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

public sealed class GhidraExportService
{
    public GhidraExportResult Export(GhidraExportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return new GhidraExportResult
            {
                Success = false,
                ErrorMessage = "Output directory must not be empty."
            };
        }

        try
        {
            var outDir = Path.GetFullPath(options.OutputDirectory);
            Directory.CreateDirectory(outDir);

            var symbolsPath = Path.Combine(outDir, "assetstudio_symbols.json");
            var scriptPath = Path.Combine(outDir, "assetstudio_ghidra_apply.py");
            var readmePath = Path.Combine(outDir, "README_GHIDRA.txt");

            var (methodCount, stringCount, metadataCount) = WriteSymbolsJson(symbolsPath, options, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(scriptPath, BuildGhidraPythonScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            string? copiedHeaderPath = null;
            if (!string.IsNullOrWhiteSpace(options.Il2CppHeaderPath) && File.Exists(options.Il2CppHeaderPath))
            {
                copiedHeaderPath = Path.Combine(outDir, "assetstudio_types.h");
                File.Copy(options.Il2CppHeaderPath, copiedHeaderPath, overwrite: true);
            }

            string? sourceOutputPath = null;
            if (!string.IsNullOrWhiteSpace(options.DecompiledSourceCode))
            {
                var safeName = SanitizeFileName(
                    !string.IsNullOrWhiteSpace(options.SuggestedSourceFileName)
                        ? options.SuggestedSourceFileName!
                        : (string.IsNullOrWhiteSpace(options.ClassTitle) ? "script.cs" : $"{options.ClassTitle}.cs"));
                if (!safeName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    safeName += ".cs";
                }
                sourceOutputPath = Path.Combine(outDir, safeName);
                File.WriteAllText(sourceOutputPath, options.DecompiledSourceCode, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            var readmeContent = BuildReadme(
                options,
                methodCount,
                stringCount,
                metadataCount,
                hasHeader: copiedHeaderPath != null,
                sourceFileName: sourceOutputPath != null ? Path.GetFileName(sourceOutputPath) : null);
            File.WriteAllText(readmePath, readmeContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Exported Ghidra script + {methodCount:N0} methods, {stringCount:N0} strings, {metadataCount:N0} metadata symbols to {outDir}");

            return new GhidraExportResult
            {
                Success = true,
                OutputDirectory = outDir,
                GhidraScriptPath = scriptPath,
                SymbolsJsonPath = symbolsPath,
                HeaderOutputPath = copiedHeaderPath,
                SourceOutputPath = sourceOutputPath,
                ReadmePath = readmePath,
                MethodCount = methodCount,
                StringCount = stringCount,
                MetadataCount = metadataCount,
                SummaryMessage = summary
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GhidraExportResult
            {
                Success = false,
                OutputDirectory = options.OutputDirectory,
                ErrorMessage = ex.Message
            };
        }
    }

    private static (int Methods, int Strings, int Metadata) WriteSymbolsJson(
        string symbolsPath,
        GhidraExportOptions options,
        CancellationToken cancellationToken)
    {
        var methodCount = 0;
        var stringCount = 0;
        var metadataCount = 0;

        using var stream = new FileStream(symbolsPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("schemaVersion", "assetstudio-ghidra-v1");
        writer.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("scope", options.Scope.ToString());
        writer.WriteString("backend", options.BackendLabel);
        writer.WriteString("binaryName", string.IsNullOrWhiteSpace(options.BinaryName) ? "GameAssembly.dll" : options.BinaryName);
        if (!string.IsNullOrWhiteSpace(options.ClassTitle))
        {
            writer.WriteString("selectedClass", options.ClassTitle);
        }

        var useFullProjectStream = options.Scope == GhidraExportScope.FullProject
            && !string.IsNullOrWhiteSpace(options.ScriptJsonPath)
            && File.Exists(options.ScriptJsonPath);

        if (useFullProjectStream)
        {
            // Two-pass streaming keeps peak memory tiny even on 200+ MB script.json files.
            writer.WriteStartArray("methods");
            Il2CppDumpScriptService.StreamScriptJson(
                options.ScriptJsonPath!,
                onMethod: entry =>
                {
                    WriteMethodObject(writer, entry.Name, entry.Signature, entry.Address);
                    methodCount++;
                },
                onString: null,
                onMetadata: null,
                cancellationToken: cancellationToken);
            writer.WriteEndArray();

            writer.WriteStartArray("strings");
            Il2CppDumpScriptService.StreamScriptJson(
                options.ScriptJsonPath!,
                onMethod: null,
                onString: entry =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("rva", entry.Address);
                    writer.WriteString("rvaHex", $"0x{entry.Address:X}");
                    writer.WriteString("value", entry.Value);
                    writer.WriteEndObject();
                    stringCount++;
                },
                onMetadata: null,
                cancellationToken: cancellationToken);
            writer.WriteEndArray();

            writer.WriteStartArray("metadata");
            Il2CppDumpScriptService.StreamScriptJson(
                options.ScriptJsonPath!,
                onMethod: null,
                onString: null,
                onMetadata: entry =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("rva", entry.Address);
                    writer.WriteString("rvaHex", $"0x{entry.Address:X}");
                    writer.WriteString("name", entry.Name);
                    if (!string.IsNullOrWhiteSpace(entry.Signature))
                    {
                        writer.WriteString("signature", entry.Signature);
                    }
                    writer.WriteEndObject();
                    metadataCount++;
                },
                cancellationToken: cancellationToken);
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStartArray("methods");
            var seenRva = new HashSet<ulong>();
            if (options.SelectedClassMethods != null)
            {
                foreach (var method in options.SelectedClassMethods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (method.Address == 0 || !seenRva.Add(method.Address))
                    {
                        continue;
                    }

                    var symName = !string.IsNullOrWhiteSpace(method.FullName)
                        ? method.FullName
                        : (!string.IsNullOrWhiteSpace(method.ClassName) ? $"{method.ClassName}$${method.MethodName}" : method.MethodName);
                    WriteMethodObject(writer, symName, method.Signature, method.Address);
                    methodCount++;
                }
            }
            writer.WriteEndArray();

            writer.WriteStartArray("strings");
            writer.WriteEndArray();

            writer.WriteStartArray("metadata");
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();

        return (methodCount, stringCount, metadataCount);
    }

    private static void WriteMethodObject(Utf8JsonWriter writer, string name, string signature, ulong address)
    {
        writer.WriteStartObject();
        writer.WriteNumber("rva", address);
        writer.WriteString("rvaHex", $"0x{address:X}");
        writer.WriteString("name", name);
        if (!string.IsNullOrWhiteSpace(signature))
        {
            writer.WriteString("signature", signature);
        }
        writer.WriteEndObject();
    }

    public static string BuildGhidraPythonScript()
    {
        return """
# AssetStudio Ghidra Symbol Importer
# Compatible with both Ghidra Jython (Python 2.7) and PyGhidra (Python 3).
#
# @author AssetStudio
# @category AssetStudio
# @keybinding
# @menupath Tools.AssetStudio.Apply Symbols
# @toolbar

import json
import os
import re

from ghidra.program.model.symbol import SourceType
from ghidra.program.model.listing import CodeUnit

_INVALID_SYMBOL_CHARS = re.compile(r'[^A-Za-z0-9_.$<>]')


def sanitize_symbol_name(raw_name):
    if not raw_name:
        return "unnamed_symbol"
    cleaned = _INVALID_SYMBOL_CHARS.sub("_", str(raw_name)).strip()
    if not cleaned:
        return "unnamed_symbol"
    if cleaned[0].isdigit():
        cleaned = "_" + cleaned
    if len(cleaned) > 220:
        cleaned = cleaned[:220]
    return cleaned


def resolve_symbols_json_path():
    try:
        script_dir = getSourceFile().getParentFile().getAbsolutePath()
        candidate = os.path.join(script_dir, "assetstudio_symbols.json")
        if os.path.isfile(candidate):
            return candidate
    except Exception:
        pass

    picked = askFile("Select assetstudio_symbols.json", "Load Symbols")
    return picked.getAbsolutePath()


def run():
    symbols_path = resolve_symbols_json_path()
    println("[AssetStudio] Loading symbols from: " + symbols_path)

    with open(symbols_path, "r") as handle:
        payload = json.load(handle)

    methods = payload.get("methods", [])
    strings = payload.get("strings", [])
    metadata = payload.get("metadata", [])

    image_base = currentProgram.getImageBase()
    listing = currentProgram.getListing()
    symbol_table = currentProgram.getSymbolTable()

    applied_functions = 0
    applied_labels = 0
    applied_comments = 0
    skipped = 0

    monitor.initialize(len(methods) + len(strings) + len(metadata))

    monitor.setMessage("AssetStudio: Applying method symbols...")
    for entry in methods:
        if monitor.isCancelled():
            println("[AssetStudio] Cancelled by user.")
            return
        monitor.incrementProgress(1)

        rva = entry.get("rva", 0)
        if not rva:
            skipped += 1
            continue

        addr = image_base.add(int(rva))
        name = sanitize_symbol_name(entry.get("name", ""))
        signature = entry.get("signature", "")

        try:
            func = getFunctionAt(addr)
            if func is None:
                func = createFunction(addr, name)
            if func is not None:
                func.setName(name, SourceType.USER_DEFINED)
                applied_functions += 1
            else:
                symbol_table.createLabel(addr, name, SourceType.USER_DEFINED)
                applied_labels += 1

            if signature:
                listing.setComment(addr, CodeUnit.PLATE_COMMENT, str(signature))
                applied_comments += 1
        except Exception:
            skipped += 1

    monitor.setMessage("AssetStudio: Applying string symbols...")
    for entry in strings:
        if monitor.isCancelled():
            println("[AssetStudio] Cancelled by user.")
            return
        monitor.incrementProgress(1)

        rva = entry.get("rva", 0)
        if not rva:
            skipped += 1
            continue

        addr = image_base.add(int(rva))
        value = str(entry.get("value", ""))
        preview = value.replace("\r", " ").replace("\n", " ")[:180]
        label_name = sanitize_symbol_name("str_" + format(int(rva), "X"))
        try:
            symbol_table.createLabel(addr, label_name, SourceType.USER_DEFINED)
            applied_labels += 1
            if preview:
                listing.setComment(addr, CodeUnit.EOL_COMMENT, 'StringLiteral: "' + preview + '"')
                applied_comments += 1
        except Exception:
            skipped += 1

    monitor.setMessage("AssetStudio: Applying metadata symbols...")
    for entry in metadata:
        if monitor.isCancelled():
            println("[AssetStudio] Cancelled by user.")
            return
        monitor.incrementProgress(1)

        rva = entry.get("rva", 0)
        if not rva:
            skipped += 1
            continue

        addr = image_base.add(int(rva))
        name = sanitize_symbol_name(entry.get("name", ""))
        try:
            symbol_table.createLabel(addr, name, SourceType.USER_DEFINED)
            applied_labels += 1
        except Exception:
            skipped += 1

    println(
        "[AssetStudio] Done! Functions: %d | Labels: %d | Comments: %d | Skipped: %d"
        % (applied_functions, applied_labels, applied_comments, skipped)
    )


if __name__ == "__main__":
    run()
""";
    }

    private static string BuildReadme(
        GhidraExportOptions options,
        int methodCount,
        int stringCount,
        int metadataCount,
        bool hasHeader,
        string? sourceFileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AssetStudio — Ghidra Export Bundle");
        sb.AppendLine("==================================");
        sb.AppendLine();
        sb.AppendLine($"Scope:   {options.Scope}");
        sb.AppendLine($"Backend: {options.BackendLabel}");
        if (!string.IsNullOrWhiteSpace(options.ClassTitle))
        {
            sb.AppendLine($"Class:   {options.ClassTitle}");
        }
        sb.AppendLine($"Target:  {options.BinaryName}");
        sb.AppendLine($"Symbols: {methodCount:N0} methods, {stringCount:N0} strings, {metadataCount:N0} metadata entries");
        sb.AppendLine();
        sb.AppendLine("Files in this folder:");
        sb.AppendLine("  - assetstudio_ghidra_apply.py : Ghidra script (Jython & PyGhidra compatible)");
        sb.AppendLine("  - assetstudio_symbols.json    : Symbol addresses (RVAs), names, signatures, and strings");
        if (hasHeader)
        {
            sb.AppendLine("  - assetstudio_types.h         : C header types (copied from il2cpp.h)");
        }
        if (!string.IsNullOrWhiteSpace(sourceFileName))
        {
            sb.AppendLine($"  - {sourceFileName} : Exported C# class source");
        }
        sb.AppendLine();
        sb.AppendLine("How to use in Ghidra:");
        sb.AppendLine($"  1. Import and analyze {options.BinaryName} in a Ghidra project.");
        if (hasHeader)
        {
            sb.AppendLine("  2. (Optional) Parse types via File -> Parse C Source... using assetstudio_types.h.");
        }
        sb.AppendLine("  3. Open Window -> Script Manager in CodeBrowser.");
        sb.AppendLine("  4. Click 'Manage Script Directories' and add this folder.");
        sb.AppendLine("  5. Find 'assetstudio_ghidra_apply.py' under the 'AssetStudio' category and click Run.");
        sb.AppendLine("  6. The script automatically loads assetstudio_symbols.json from the same folder and");
        sb.AppendLine("     rebases every RVA relative to currentProgram.getImageBase().");
        if (methodCount == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Note: This export contains 0 native RVAs (for example, a Mono managed assembly or a");
            sb.AppendLine("project without script.json / dump.cs RVAs). Mono scripts are already decompiled to C#.");
        }
        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var cleaned = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "script.cs" : cleaned;
    }
}
