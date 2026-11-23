using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
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
    Unavailable = 5,
    Il2CppGlobalMetadata = 6
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
    public string? GlobalMetadataPath { get; init; }
    public string? ProjectAnalysisCacheDirectory { get; init; }
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
            sb.AppendLine("        // Source assembly or IL2CPP metadata was not found.");
            sb.AppendLine("        // Use 'Scripts Folder...' for Managed/ or Il2CppDumper output.");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine($"public partial class {safeClassName}");
            sb.AppendLine("{");
            sb.AppendLine("    // Source assembly or IL2CPP metadata was not found.");
            sb.AppendLine("    // Use 'Scripts Folder...' for Managed/ or Il2CppDumper output.");
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

        parts.Add("No script source found");
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
    public string? GlobalMetadataPath { get; set; }

    public bool HasAnySource =>
        ManagedDirectories.Count > 0
        || DummyDllDirectories.Count > 0
        || !string.IsNullOrWhiteSpace(DumpCsPath)
        || !string.IsNullOrWhiteSpace(ScriptJsonPath)
        || !string.IsNullOrWhiteSpace(GlobalMetadataPath);
}

public static class ScriptSourceDiscovery
{
    private const int MaxDllFilesToProbePerDirectory = 64;

    public static DiscoveredScriptSources Discover(
        string? projectRoot,
        string? customScriptsDirectory = null,
        IReadOnlyList<string>? knownIl2CppArtifacts = null,
        string? gameAssemblyPath = null,
        string? loadedAssemblyDirectory = null,
        string? globalMetadataPath = null,
        string? projectAnalysisCacheDirectory = null)
    {
        var result = new DiscoveredScriptSources
        {
            GameAssemblyPath = !string.IsNullOrWhiteSpace(gameAssemblyPath) && File.Exists(gameAssemblyPath)
                ? Path.GetFullPath(gameAssemblyPath)
                : null,
            GlobalMetadataPath = !string.IsNullOrWhiteSpace(globalMetadataPath) && File.Exists(globalMetadataPath)
                ? Path.GetFullPath(globalMetadataPath)
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

            if (!HasAnyManagedDlls(fullDir))
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

        void TryAddGlobalMetadataFile(string? path)
        {
            if (!string.IsNullOrWhiteSpace(result.GlobalMetadataPath)
                || string.IsNullOrWhiteSpace(path)
                || !File.Exists(path))
            {
                return;
            }

            try
            {
                result.GlobalMetadataPath = Path.GetFullPath(path);
            }
            catch
            {
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
            else if (string.Equals(fileName, "global-metadata.dat", StringComparison.OrdinalIgnoreCase))
            {
                TryAddGlobalMetadataFile(fullPath);
            }
        }

        void ProbeDumperDirectory(string? dir, bool includeSelfAsDllDir = true)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return;
            }

            TryAddArtifactFile(Path.Combine(dir, "dump.cs"));
            TryAddArtifactFile(Path.Combine(dir, "script.json"));
            TryAddArtifactFile(Path.Combine(dir, "il2cpp.h"));
            TryAddGlobalMetadataFile(Path.Combine(dir, "global-metadata.dat"));
            TryAddGlobalMetadataFile(Path.Combine(dir, "Metadata", "global-metadata.dat"));
            TryAddDllDirectory(Path.Combine(dir, "DummyDll"), preferDummy: true);
            TryAddDllDirectory(Path.Combine(dir, "Managed"), preferDummy: false);
            if (includeSelfAsDllDir)
            {
                TryAddDllDirectory(dir);
            }
        }

        void TryDetectGameAssemblyInDirectory(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(result.GameAssemblyPath)
                || string.IsNullOrWhiteSpace(dir)
                || !Directory.Exists(dir))
            {
                return;
            }

            foreach (var candidateName in new[] { "GameAssembly.dll", "libil2cpp.so", "GameAssembly.so", "GameAssembly.dylib" })
            {
                var candidatePath = Path.Combine(dir, candidateName);
                if (File.Exists(candidatePath))
                {
                    result.GameAssemblyPath = Path.GetFullPath(candidatePath);
                    break;
                }
            }
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

        // 3. Explicit ProjectAnalysis cache directory (and its il2cppdumper output subdir)
        if (!string.IsNullOrWhiteSpace(projectAnalysisCacheDirectory) && Directory.Exists(projectAnalysisCacheDirectory))
        {
            ProbeDumperDirectory(projectAnalysisCacheDirectory, includeSelfAsDllDir: false);
            ProbeDumperDirectory(Path.Combine(projectAnalysisCacheDirectory, "il2cppdumper"));
        }

        // 4. Previously loaded assembly folder (AssemblyLoader)
        if (!string.IsNullOrWhiteSpace(loadedAssemblyDirectory) && Directory.Exists(loadedAssemblyDirectory))
        {
            ProbeDumperDirectory(loadedAssemblyDirectory);
        }

        // 5. Project root discovery (*_Data/Managed, *_Data/il2cpp_data/Metadata/global-metadata.dat, cache, Dumper folders)
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

            // Probe ProjectAnalysisService cache directory for this project automatically
            try
            {
                var sharedCacheDir = ProjectAnalysisService.Shared.GetProjectCacheDirectory(fullRoot);
                if (!string.Equals(sharedCacheDir, projectAnalysisCacheDirectory, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(sharedCacheDir))
                {
                    ProbeDumperDirectory(sharedCacheDir, includeSelfAsDllDir: false);
                    ProbeDumperDirectory(Path.Combine(sharedCacheDir, "il2cppdumper"));
                }
            }
            catch
            {
            }

            ProbeDumperDirectory(fullRoot);

            var dataCandidates = new List<string>();
            var trimmedRootName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (trimmedRootName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmedRootName, "Data", StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(Path.Combine(fullRoot, "il2cpp_data"))
                || File.Exists(Path.Combine(fullRoot, "globalgamemanagers")))
            {
                dataCandidates.Add(fullRoot);
            }

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

            // Bounded depth-2 fallback when Unity build is nested inside a subfolder
            if (!dataCandidates.Any(Directory.Exists))
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly).Take(32))
                    {
                        foreach (var nestedData in Directory.EnumerateDirectories(sub, "*_Data", SearchOption.TopDirectoryOnly))
                        {
                            dataCandidates.Add(nestedData);
                        }

                        var nestedPlainData = Path.Combine(sub, "Data");
                        if (Directory.Exists(nestedPlainData))
                        {
                            dataCandidates.Add(nestedPlainData);
                        }
                    }
                }
                catch
                {
                }
            }

            foreach (var dataDir in dataCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dataDir))
                {
                    continue;
                }

                TryAddDllDirectory(Path.Combine(dataDir, "Managed"), preferDummy: false);
                TryAddDllDirectory(Path.Combine(dataDir, "DummyDll"), preferDummy: true);
                ProbeDumperDirectory(Path.Combine(dataDir, "il2cpp_data"));
                TryAddGlobalMetadataFile(Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat"));
                TryAddGlobalMetadataFile(Path.Combine(dataDir, "Managed", "Metadata", "global-metadata.dat"));
                TryAddGlobalMetadataFile(Path.Combine(dataDir, "Metadata", "global-metadata.dat"));
            }

            TryAddGlobalMetadataFile(Path.Combine(fullRoot, "il2cpp_data", "Metadata", "global-metadata.dat"));
            TryAddGlobalMetadataFile(Path.Combine(fullRoot, "Managed", "Metadata", "global-metadata.dat"));
            TryAddGlobalMetadataFile(Path.Combine(fullRoot, "global-metadata.dat"));

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

            // Bounded parent/sibling folder discovery (handles opening *_Data directly or placing dumper output alongside project)
            try
            {
                var parentDir = Path.GetDirectoryName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parentDir)
                    && Directory.Exists(parentDir)
                    && !string.Equals(parentDir, Path.GetPathRoot(parentDir), StringComparison.OrdinalIgnoreCase))
                {
                    var isRootDataFolder = trimmedRootName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(trimmedRootName, "Data", StringComparison.OrdinalIgnoreCase);

                    // Direct artifacts in parent directory
                    TryAddArtifactFile(Path.Combine(parentDir, "dump.cs"));
                    TryAddArtifactFile(Path.Combine(parentDir, "script.json"));
                    TryAddArtifactFile(Path.Combine(parentDir, "il2cpp.h"));
                    if (isRootDataFolder)
                    {
                        TryDetectGameAssemblyInDirectory(parentDir);
                        TryAddDllDirectory(Path.Combine(parentDir, "DummyDll"), preferDummy: true);
                    }

                    // Sibling directories that look like dumper outputs for this project
                    foreach (var siblingDir in Directory.EnumerateDirectories(parentDir, "*", SearchOption.TopDirectoryOnly).Take(64))
                    {
                        if (string.Equals(siblingDir, fullRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var sibName = Path.GetFileName(siblingDir);
                        var isExplicitDumperName = string.Equals(sibName, "Il2CppDumper", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(sibName, "il2cpp_dump", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(sibName, "DummyDll", StringComparison.OrdinalIgnoreCase);
                        var isProjectRelatedDumper = !string.IsNullOrWhiteSpace(trimmedRootName)
                            && sibName.Contains(trimmedRootName, StringComparison.OrdinalIgnoreCase)
                            && (sibName.Contains("dump", StringComparison.OrdinalIgnoreCase)
                                || sibName.Contains("il2cpp", StringComparison.OrdinalIgnoreCase)
                                || sibName.Contains("dummy", StringComparison.OrdinalIgnoreCase));

                        if (isExplicitDumperName || isProjectRelatedDumper)
                        {
                            ProbeDumperDirectory(siblingDir);
                        }
                    }
                }
            }
            catch
            {
            }

            // Detect GameAssembly if not already supplied
            TryDetectGameAssemblyInDirectory(fullRoot);
            TryDetectGameAssemblyInDirectory(Path.Combine(fullRoot, "lib", "arm64-v8a"));
            TryDetectGameAssemblyInDirectory(Path.Combine(fullRoot, "lib", "armeabi-v7a"));
            TryDetectGameAssemblyInDirectory(Path.Combine(fullRoot, "lib", "x86_64"));
            TryDetectGameAssemblyInDirectory(Path.Combine(fullRoot, "Contents", "Frameworks"));
        }

        return result;
    }

    /// <summary>
    /// Fast header-only check verifying that a DLL file is a genuine .NET managed PE assembly
    /// (has a valid CLR/CLI metadata header), rejecting native C++ DLLs such as GameAssembly.dll
    /// or UnityPlayer.dll without reading the full file body.
    /// </summary>
    public static bool IsManagedAssemblyFile(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 64)
            {
                return false;
            }

            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return peReader.HasMetadata;
        }
        catch
        {
            return false;
        }
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

    private static bool HasAnyManagedDlls(string directoryPath)
    {
        try
        {
            var checkedCount = 0;
            foreach (var dllPath in Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (IsManagedAssemblyFile(dllPath))
                {
                    return true;
                }

                if (++checkedCount >= MaxDllFilesToProbePerDirectory)
                {
                    break;
                }
            }

            return false;
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
