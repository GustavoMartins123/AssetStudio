using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.Services.Scripting;

public enum ScriptSourceKind
{
    None = 0,
    MonoAssemblyDecompiled = 1,
    Il2CppDumpCs = 2,
    Il2CppDummyAssemblyDecompiled = 3,
    Il2CppScriptJsonOutline = 4,
    Unavailable = 5
}

public sealed class ScriptMethodSymbol
{
    public string ClassName { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public string TypeSignature { get; init; } = string.Empty;
    public ulong Address { get; init; }
    public ulong Offset { get; init; }
    public ulong VirtualAddress { get; init; }
    public string Source { get; init; } = string.Empty;

    public string RvaHex => Address > 0 ? $"0x{Address:X}" : string.Empty;
}

public sealed class ScriptLookupRequest
{
    public string AssemblyName { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string AssetName { get; init; } = string.Empty;
    public string? ProjectRoot { get; init; }
    public string? CustomScriptsDirectory { get; init; }
    public IReadOnlyList<string> ManagedDirectories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DummyDllDirectories { get; init; } = Array.Empty<string>();
    public string? DumpCsPath { get; init; }
    public string? ScriptJsonPath { get; init; }
    public string? Il2CppHeaderPath { get; init; }
    public string? GameAssemblyPath { get; init; }
    public UnityScriptingBackend ScriptingBackend { get; init; } = UnityScriptingBackend.Unknown;

    public string FullTypeName =>
        !string.IsNullOrWhiteSpace(Namespace) && !string.IsNullOrWhiteSpace(ClassName)
            ? $"{Namespace}.{ClassName}"
            : (!string.IsNullOrWhiteSpace(ClassName) ? ClassName : AssetName);
}

public sealed class ScriptDecompilationResult
{
    public bool Success { get; init; }
    public ScriptSourceKind SourceKind { get; init; } = ScriptSourceKind.None;
    public string BackendBadge { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string SourceFilePath { get; init; } = string.Empty;
    public string SourceCode { get; init; } = string.Empty;
    public string SummaryText { get; init; } = string.Empty;
    public string DiagnosticMessage { get; init; } = string.Empty;
    public int? TypeDefIndex { get; init; }
    public IReadOnlyList<ScriptMethodSymbol> MethodSymbols { get; init; } = Array.Empty<ScriptMethodSymbol>();

    public static ScriptDecompilationResult Unavailable(
        ScriptLookupRequest request,
        string diagnosticMessage,
        string? fallbackCode = null)
    {
        var title = string.IsNullOrWhiteSpace(request.FullTypeName)
            ? "Unknown Script"
            : request.FullTypeName;
        return new ScriptDecompilationResult
        {
            Success = false,
            SourceKind = ScriptSourceKind.Unavailable,
            BackendBadge = "Unavailable",
            Title = title,
            AssemblyName = request.AssemblyName,
            Namespace = request.Namespace,
            ClassName = request.ClassName,
            SourceCode = fallbackCode ?? BuildMetadataStub(request, diagnosticMessage),
            SummaryText = BuildUnavailableSummary(request),
            DiagnosticMessage = diagnosticMessage
        };
    }

    public static string BuildMetadataStub(ScriptLookupRequest request, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// AssetStudio Script Viewer");
        if (!string.IsNullOrWhiteSpace(request.AssemblyName))
        {
            sb.AppendLine($"// Assembly: {request.AssemblyName}");
        }
        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            sb.AppendLine($"// Namespace: {request.Namespace}");
        }
        if (!string.IsNullOrWhiteSpace(request.ClassName))
        {
            sb.AppendLine($"// Class: {request.ClassName}");
        }
        sb.AppendLine($"// Status: {reason}");
        sb.AppendLine();

        var safeClassName = string.IsNullOrWhiteSpace(request.ClassName) ? "UnknownScript" : request.ClassName;
        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            sb.AppendLine($"namespace {request.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {safeClassName}");
            sb.AppendLine("    {");
            sb.AppendLine("        // Source assembly or IL2CPP dump was not found.");
            sb.AppendLine("        // Use 'Scripts Folder...' to select a Managed/ folder (Mono) or an Il2CppDumper output folder (dump.cs / script.json / DummyDll).");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine($"public partial class {safeClassName}");
            sb.AppendLine("{");
            sb.AppendLine("    // Source assembly or IL2CPP dump was not found.");
            sb.AppendLine("    // Use 'Scripts Folder...' to select a Managed/ folder (Mono) or an Il2CppDumper output folder (dump.cs / script.json / DummyDll).");
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static string BuildUnavailableSummary(ScriptLookupRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.AssemblyName))
        {
            parts.Add(request.AssemblyName);
        }

        parts.Add("No source assembly or dump.cs found");
        return string.Join(" • ", parts);
    }
}

public sealed class DiscoveredScriptSources
{
    public List<string> ManagedDirectories { get; } = new();
    public List<string> DummyDllDirectories { get; } = new();
    public string? DumpCsPath { get; set; }
    public string? ScriptJsonPath { get; set; }
    public string? Il2CppHeaderPath { get; set; }
    public string? GameAssemblyPath { get; set; }

    public bool HasAnySource =>
        ManagedDirectories.Count > 0
        || DummyDllDirectories.Count > 0
        || !string.IsNullOrWhiteSpace(DumpCsPath)
        || !string.IsNullOrWhiteSpace(ScriptJsonPath);
}

public static class ScriptSourceDiscovery
{
    public static DiscoveredScriptSources Discover(
        string? projectRoot,
        string? customScriptsDirectory = null,
        IReadOnlyList<string>? knownIl2CppArtifacts = null,
        string? gameAssemblyPath = null,
        string? loadedAssemblyDirectory = null)
    {
        var result = new DiscoveredScriptSources
        {
            GameAssemblyPath = !string.IsNullOrWhiteSpace(gameAssemblyPath) && File.Exists(gameAssemblyPath)
                ? Path.GetFullPath(gameAssemblyPath)
                : null
        };

        var managedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dummySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddDllDirectory(string? dir, bool preferDummy = false)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return;
            }

            string fullDir;
            try
            {
                fullDir = Path.GetFullPath(dir);
            }
            catch
            {
                return;
            }

            if (!HasAnyDlls(fullDir))
            {
                return;
            }

            var isDummy = preferDummy || IsLikelyDummyDllDirectory(fullDir);
            if (isDummy)
            {
                if (dummySeen.Add(fullDir))
                {
                    result.DummyDllDirectories.Add(fullDir);
                }
            }
            else if (managedSeen.Add(fullDir))
            {
                result.ManagedDirectories.Add(fullDir);
            }
        }

        void TryAddArtifactFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            var fileName = Path.GetFileName(fullPath);
            if (string.Equals(fileName, "dump.cs", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(result.DumpCsPath))
            {
                result.DumpCsPath = fullPath;
            }
            else if (string.Equals(fileName, "script.json", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(result.ScriptJsonPath))
            {
                result.ScriptJsonPath = fullPath;
            }
            else if (string.Equals(fileName, "il2cpp.h", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(result.Il2CppHeaderPath))
            {
                result.Il2CppHeaderPath = fullPath;
            }
        }

        void ProbeDumperDirectory(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return;
            }

            TryAddArtifactFile(Path.Combine(dir, "dump.cs"));
            TryAddArtifactFile(Path.Combine(dir, "script.json"));
            TryAddArtifactFile(Path.Combine(dir, "il2cpp.h"));
            TryAddDllDirectory(Path.Combine(dir, "DummyDll"), preferDummy: true);
            TryAddDllDirectory(Path.Combine(dir, "Managed"), preferDummy: false);
            TryAddDllDirectory(dir);
        }

        // 1. Highest priority: user-chosen custom scripts directory
        if (!string.IsNullOrWhiteSpace(customScriptsDirectory) && Directory.Exists(customScriptsDirectory))
        {
            ProbeDumperDirectory(customScriptsDirectory);
        }

        // 2. Known Il2CppDumper artifacts from ProjectAnalysisReport
        if (knownIl2CppArtifacts != null)
        {
            foreach (var artifact in knownIl2CppArtifacts)
            {
                TryAddArtifactFile(artifact);
                var parent = Path.GetDirectoryName(artifact);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    TryAddDllDirectory(Path.Combine(parent, "DummyDll"), preferDummy: true);
                }
            }
        }

        // 3. Previously loaded assembly folder (AssemblyLoader)
        if (!string.IsNullOrWhiteSpace(loadedAssemblyDirectory) && Directory.Exists(loadedAssemblyDirectory))
        {
            ProbeDumperDirectory(loadedAssemblyDirectory);
        }

        // 4. Project root discovery (*_Data/Managed, Data/Managed, DummyDll, Il2CppDumper folders)
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(projectRoot);
            }
            catch
            {
                fullRoot = projectRoot;
            }

            ProbeDumperDirectory(fullRoot);

            var dataCandidates = new List<string>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(fullRoot, "*_Data", SearchOption.TopDirectoryOnly))
                {
                    dataCandidates.Add(dir);
                }
            }
            catch
            {
            }

            dataCandidates.Add(Path.Combine(fullRoot, "Data"));
            dataCandidates.Add(Path.Combine(fullRoot, "assets", "bin", "Data"));
            dataCandidates.Add(Path.Combine(fullRoot, "Contents", "Resources", "Data"));

            foreach (var dataDir in dataCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dataDir))
                {
                    continue;
                }

                TryAddDllDirectory(Path.Combine(dataDir, "Managed"), preferDummy: false);
                ProbeDumperDirectory(Path.Combine(dataDir, "il2cpp_data"));
            }

            // Probe direct child directories that look like dumper outputs
            try
            {
                foreach (var childDir in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly).Take(64))
                {
                    var name = Path.GetFileName(childDir);
                    if (name.Contains("dump", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("il2cpp", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "DummyDll", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Managed", StringComparison.OrdinalIgnoreCase))
                    {
                        ProbeDumperDirectory(childDir);
                    }
                }
            }
            catch
            {
            }

            // Detect GameAssembly if not already supplied
            if (string.IsNullOrWhiteSpace(result.GameAssemblyPath))
            {
                foreach (var candidateName in new[] { "GameAssembly.dll", "libil2cpp.so", "GameAssembly.so", "GameAssembly.dylib" })
                {
                    var candidatePath = Path.Combine(fullRoot, candidateName);
                    if (File.Exists(candidatePath))
                    {
                        result.GameAssemblyPath = candidatePath;
                        break;
                    }
                }
            }
        }

        return result;
    }

    public static bool IsLikelyDummyDllDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var dirName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(dirName, "DummyDll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (File.Exists(Path.Combine(directoryPath, "Il2CppDummyDll.dll")))
        {
            return true;
        }

        var parent = Path.GetDirectoryName(directoryPath);
        if (!string.IsNullOrWhiteSpace(parent)
            && (File.Exists(Path.Combine(parent, "dump.cs")) || File.Exists(Path.Combine(parent, "script.json"))))
        {
            return dirName.Contains("dummy", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool HasAnyDlls(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }
}

public sealed record ScriptCodeSearchResult(
    string DisplayText,
    int MatchCount,
    int MatchingLineCount,
    string MatchSummary);

public static class ScriptCodeSearchHelper
{
    public static ScriptCodeSearchResult ApplySearch(string? sourceCode, string? query, bool filterLinesOnly)
    {
        var text = sourceCode ?? string.Empty;
        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedQuery) || string.IsNullOrEmpty(text))
        {
            return new ScriptCodeSearchResult(text, 0, 0, string.Empty);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var totalMatches = 0;
        var matchingLineIndices = new List<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var searchIndex = 0;
            var lineMatches = 0;
            while (searchIndex < line.Length)
            {
                var found = line.IndexOf(trimmedQuery, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }

                lineMatches++;
                searchIndex = found + trimmedQuery.Length;
            }

            if (lineMatches > 0)
            {
                totalMatches += lineMatches;
                matchingLineIndices.Add(i);
            }
        }

        var summary = totalMatches == 0
            ? "No matches"
            : $"{totalMatches:N0} match{(totalMatches == 1 ? string.Empty : "es")} ({matchingLineIndices.Count:N0} line{(matchingLineIndices.Count == 1 ? string.Empty : "s")})";

        if (!filterLinesOnly)
        {
            return new ScriptCodeSearchResult(text, totalMatches, matchingLineIndices.Count, summary);
        }

        if (matchingLineIndices.Count == 0)
        {
            return new ScriptCodeSearchResult(
                $"// No lines matched \"{trimmedQuery}\".",
                0,
                0,
                summary);
        }

        var included = new HashSet<int>();
        foreach (var idx in matchingLineIndices)
        {
            if (idx > 0) included.Add(idx - 1);
            included.Add(idx);
            if (idx + 1 < lines.Length) included.Add(idx + 1);
        }

        var sorted = included.OrderBy(static i => i).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"// Filtered lines matching \"{trimmedQuery}\" ({summary})");
        var previousLine = -2;
        foreach (var lineIdx in sorted)
        {
            if (previousLine >= 0 && lineIdx > previousLine + 1)
            {
                sb.AppendLine("...");
            }

            var marker = matchingLineIndices.BinarySearch(lineIdx) >= 0 ? ">" : " ";
            sb.Append(marker);
            sb.Append('L');
            sb.Append((lineIdx + 1).ToString("D4"));
            sb.Append(": ");
            sb.AppendLine(lines[lineIdx]);
            previousLine = lineIdx;
        }

        return new ScriptCodeSearchResult(sb.ToString().TrimEnd(), totalMatches, matchingLineIndices.Count, summary);
    }
}
