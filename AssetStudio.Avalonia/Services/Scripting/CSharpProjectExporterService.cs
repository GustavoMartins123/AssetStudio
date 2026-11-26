using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace AssetStudio.Avalonia.Services.Scripting;

public enum ScriptAssemblyCategory
{
    Game = 0,
    UnityEngine = 1,
    Framework = 2,
    ThirdParty = 3
}

/// <summary>
/// Classifies managed assemblies into Game vs UnityEngine vs .NET Framework vs Third-Party libraries
/// so script export and assembly exploration focus cleanly on user/game code by default.
/// </summary>
public static class ScriptProjectAssemblyClassifier
{
    private static readonly string[] FrameworkExact =
    {
        "mscorlib",
        "netstandard",
        "System",
        "Accessibility",
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "Facades",
        "Novell.Directory.Ldap",
        "I18N"
    };

    private static readonly string[] FrameworkPrefixes =
    {
        "System.",
        "Microsoft.",
        "Mono.",
        "Windows.",
        "I18N."
    };

    private static readonly string[] UnityEngineExact =
    {
        "UnityEngine",
        "UnityEditor",
        "UnityEngine.UI",
        "Cinemachine",
        "Unity.TextMeshPro",
        "Boo.Lang",
        "UnityScript.Lang"
    };

    private static readonly string[] UnityEnginePrefixes =
    {
        "UnityEngine.",
        "UnityEditor.",
        "Unity.",
        "com.unity.",
        "UnityScript"
    };

    private static readonly string[] ThirdPartyExact =
    {
        "Il2CppDummyDll",
        "DOTween",
        "DOTweenPro",
        "UniTask",
        "UniRx",
        "TextMeshPro",
        "FMODUnity",
        "FMODUnityResonance",
        "Newtonsoft.Json",
        "LitJson",
        "SimpleJSON",
        "YamlDotNet",
        "ICSharpCode.SharpZipLib",
        "MessagePack",
        "MemoryPack",
        "FlatBuffers",
        "Zenject",
        "Extenject",
        "VContainer",
        "Autofac",
        "DryIoc",
        "Mirror",
        "Rewired_Core",
        "InControl",
        "SRDebugger",
        "BestHTTP",
        "NaughtyAttributes.Core",
        "R3",
        "ZString",
        "ZLogger"
    };

    private static readonly string[] ThirdPartyPrefixes =
    {
        "Newtonsoft.",
        "Ionic.",
        "BouncyCastle",
        "Google.",
        "Firebase.",
        "DOTween",
        "DG.Tweening",
        "Demigiant",
        "FMOD",
        "Spine",
        "Burst",
        "Il2Cpp",
        "Sirenix.",
        "UniTask",
        "UniRx",
        "TextMeshPro",
        "Cysharp.",
        "MessagePack",
        "MemoryPack",
        "Protobuf",
        "FlatBuffers",
        "YamlDotNet",
        "SharpZipLib",
        "ICSharpCode.",
        "NLog",
        "Serilog",
        "log4net",
        "Steamworks",
        "com.rlabrecque.",
        "CriMw.",
        "Facepunch.",
        "Photon",
        "ExitGames.",
        "BehaviorDesigner",
        "NodeCanvas",
        "ParadoxNotion",
        "Cinemachine",
        "Amplify",
        "TextCore",
        "Vuforia",
        "PlayFab",
        "Sentry",
        "AppsFlyer",
        "Adjust",
        "Facebook.",
        "IronSource",
        "MaxSdk",
        "AppLovin",
        "AdMob",
        "EasySave",
        "ES3",
        "AstarPathfindingProject",
        "Pathfinding.",
        "MeshBaker",
        "Wwise",
        "AkSoundEngine",
        "CriWare",
        "Live2D",
        "Cubism",
        "AVPro",
        "RenderHeads.",
        "StompyRobot.",
        "Odin",
        "Zenject",
        "Extenject",
        "Rewired"
    };

    public static bool IsUnityPredefinedAssembly(string? assemblyName)
    {
        var shortName = UnityProjectGuidUtility.NormalizeAssemblyShortName(assemblyName);
        if (string.IsNullOrEmpty(shortName))
        {
            return false;
        }

        return shortName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-CSharp-Editor", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-CSharp-Editor-firstpass", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-UnityScript", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-UnityScript-firstpass", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-UnityScript-Editor", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("Assembly-UnityScript-Editor-firstpass", StringComparison.OrdinalIgnoreCase);
    }

    public static ScriptAssemblyCategory Classify(string? assemblyName)
    {
        var shortName = UnityProjectGuidUtility.NormalizeAssemblyShortName(assemblyName);
        if (string.IsNullOrEmpty(shortName))
        {
            return ScriptAssemblyCategory.Game;
        }

        // Always classify Unity predefined game script assemblies as Game
        if (shortName.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
            || shortName.StartsWith("Assembly-UnityScript", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptAssemblyCategory.Game;
        }

        if (MatchesAnyExactOrPrefix(shortName, FrameworkExact, FrameworkPrefixes))
        {
            return ScriptAssemblyCategory.Framework;
        }

        if (MatchesAnyExactOrPrefix(shortName, UnityEngineExact, UnityEnginePrefixes))
        {
            return ScriptAssemblyCategory.UnityEngine;
        }

        if (MatchesAnyExactOrPrefix(shortName, ThirdPartyExact, ThirdPartyPrefixes))
        {
            return ScriptAssemblyCategory.ThirdParty;
        }

        return ScriptAssemblyCategory.Game;
    }

    public static bool IsGameAssembly(string? assemblyName)
    {
        return Classify(assemblyName) == ScriptAssemblyCategory.Game;
    }

    public static bool IsFrameworkOrBclAssembly(string? assemblyName)
    {
        return Classify(assemblyName) == ScriptAssemblyCategory.Framework;
    }

    private static bool MatchesAnyExactOrPrefix(string shortName, string[] exacts, string[] prefixes)
    {
        for (var i = 0; i < exacts.Length; i++)
        {
            if (shortName.Equals(exacts[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        for (var i = 0; i < prefixes.Length; i++)
        {
            if (shortName.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public enum CSharpProjectLayoutMode
{
    StandaloneDotNetSolution = 0,
    UnityAssetsScripts = 1
}

public enum CSharpProjectExportSourceMode
{
    None = 0,
    MonoManagedAssemblies = 1,
    Il2CppDummyDlls = 2,
    Il2CppDumpCs = 3,
    Il2CppGlobalMetadata = 4
}

public sealed class CSharpProjectExportOptions
{
    public required string OutputDirectory { get; init; }
    public string ProjectName { get; init; } = "ExportedScripts";
    public CSharpProjectLayoutMode LayoutMode { get; init; } = CSharpProjectLayoutMode.StandaloneDotNetSolution;
    public bool IncludeEngineAndThirdPartyAssemblies { get; init; } = false;
    public bool GenerateUnityMetaFiles { get; init; } = true;
    public bool GenerateAsmdefFiles { get; init; } = true;
    public bool CopyReferenceDlls { get; init; } = true;
    public IReadOnlyCollection<string>? SelectedAssemblyNames { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // Explicit sources or project discovery parameters
    public IReadOnlyList<string> ManagedDirectories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DummyDllDirectories { get; init; } = Array.Empty<string>();
    public string? DumpCsPath { get; init; }
    public string? ScriptJsonPath { get; init; }
    public string? GlobalMetadataPath { get; init; }
    public string? GameAssemblyPath { get; init; }
    public string? ProjectRoot { get; init; }
    public string? CustomScriptsDirectory { get; init; }
}

public sealed record CSharpProjectExportProgress(
    int CompletedAssemblies,
    int TotalAssemblies,
    int CompletedTypes,
    int TotalTypes,
    string CurrentAssembly,
    string StatusMessage)
{
    public double Percent => TotalAssemblies > 0
        ? Math.Clamp(CompletedAssemblies * 100.0 / TotalAssemblies, 0.0, 100.0)
        : 0.0;
}

public sealed record BrokenDependencyCycle(
    string FromAssembly,
    string ToAssembly,
    string Reason);

public sealed record FailedTypeExportRecord(
    string AssemblyName,
    string TypeFullName,
    string ErrorMessage);

public sealed class ExportedAssemblyReport
{
    public string AssemblyName { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public string ProjectFilePath { get; init; } = string.Empty;
    public string AsmdefFilePath { get; init; } = string.Empty;
    public int TypesExported { get; init; }
    public int TypesFailed { get; init; }
    public List<string> ProjectReferences { get; init; } = new();
    public List<string> DllReferences { get; init; } = new();
    public List<string> UnresolvedReferences { get; init; } = new();
}

public sealed class ScriptDependencyGraphResolution
{
    public Dictionary<string, List<string>> ProjectReferencesByAssembly { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> CycleFallbackDllReferencesByAssembly { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BrokenDependencyCycle> BrokenCycles { get; } = new();
}

/// <summary>
/// Deterministic DFS cycle detector and DAG resolver for multi-assembly project references.
/// Breaks back-edges cleanly so generated .csproj ProjectReferences are guaranteed acyclic.
/// </summary>
public static class ScriptDependencyGraphResolver
{
    public static ScriptDependencyGraphResolution Resolve(
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedAssemblyDependencies)
    {
        var result = new ScriptDependencyGraphResolution();
        if (exportedAssemblyDependencies == null || exportedAssemblyDependencies.Count == 0)
        {
            return result;
        }

        var canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sortedNodes = exportedAssemblyDependencies.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var node in sortedNodes)
        {
            canonicalNames[node] = node;
            result.ProjectReferencesByAssembly[node] = new List<string>();
            result.CycleFallbackDllReferencesByAssembly[node] = new List<string>();
        }

        // Build canonical sorted adjacency within the exported set
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sortedNodes)
        {
            var rawDeps = exportedAssemblyDependencies[node] ?? Array.Empty<string>();
            var filtered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in rawDeps)
            {
                var norm = UnityProjectGuidUtility.NormalizeAssemblyShortName(dep);
                if (canonicalNames.TryGetValue(norm, out var target) && seen.Add(target))
                {
                    if (string.Equals(target, node, StringComparison.OrdinalIgnoreCase))
                    {
                        result.BrokenCycles.Add(new BrokenDependencyCycle(
                            node,
                            target,
                            $"Self-reference on '{node}' ignored."));
                        continue;
                    }
                    filtered.Add(target);
                }
            }
            filtered.Sort(StringComparer.OrdinalIgnoreCase);
            adjacency[node] = filtered;
        }

        // 0 = unvisited, 1 = visiting (on recursion stack), 2 = visited
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sortedNodes)
        {
            state[node] = 0;
        }

        void Dfs(string u)
        {
            state[u] = 1;
            var deps = adjacency[u];
            for (var i = 0; i < deps.Count; i++)
            {
                var v = deps[i];
                var vState = state[v];
                if (vState == 1)
                {
                    // Back-edge forms a cycle: break by falling back to binary DLL reference
                    result.CycleFallbackDllReferencesByAssembly[u].Add(v);
                    result.BrokenCycles.Add(new BrokenDependencyCycle(
                        u,
                        v,
                        $"Cyclic dependency '{u}' -> '{v}' broken; emitted as binary DLL reference instead of ProjectReference."));
                    continue;
                }

                result.ProjectReferencesByAssembly[u].Add(v);
                if (vState == 0)
                {
                    Dfs(v);
                }
            }
            state[u] = 2;
        }

        foreach (var node in sortedNodes)
        {
            if (state[node] == 0)
            {
                Dfs(node);
            }
        }

        return result;
    }
}

/// <summary>
/// Deterministic cross-platform filename/path sanitizer with Windows reserved device name guards,
/// generic arity rewriting, long-path collapsing, case-insensitive collision dedup, and root confinement.
/// </summary>
public static class ScriptExportPathSanitizer
{
    private const int MaxRelativePathLength = 200;

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeSegment(string? rawSegment, string fallback = "Unnamed")
    {
        if (string.IsNullOrWhiteSpace(rawSegment))
        {
            return fallback;
        }

        var trimmed = rawSegment.Trim();
        // Rewrite generic arity backtick cleanly: Foo`1 -> Foo_1
        var sb = new StringBuilder(trimmed.Length);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '`')
            {
                sb.Append('_');
            }
            else if (c < 32
                || c == '<' || c == '>' || c == ':' || c == '"'
                || c == '/' || c == '\\' || c == '|' || c == '?' || c == '*')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        // Strip trailing dots/spaces (invalid on Windows)
        while (sb.Length > 0 && (sb[sb.Length - 1] == '.' || sb[sb.Length - 1] == ' '))
        {
            sb.Length--;
        }

        var cleaned = sb.ToString().Trim();
        if (string.IsNullOrEmpty(cleaned) || cleaned == "." || cleaned == "..")
        {
            return fallback;
        }

        // Prevent dot-only segments
        var allDots = true;
        for (var i = 0; i < cleaned.Length; i++)
        {
            if (cleaned[i] != '.')
            {
                allDots = false;
                break;
            }
        }
        if (allDots)
        {
            return fallback;
        }

        // Guard Windows reserved names (both exact and prefix before first dot, e.g. CON.txt)
        var dotIdx = cleaned.IndexOf('.');
        var stem = dotIdx > 0 ? cleaned.Substring(0, dotIdx) : cleaned;
        if (WindowsReservedDeviceNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        // Bound individual segment length
        if (cleaned.Length > 100)
        {
            var hashSuffix = ComputeShortHash(trimmed);
            cleaned = cleaned.Substring(0, 91) + "_" + hashSuffix;
        }

        return cleaned;
    }

    public static string BuildUniqueScriptRelativePath(
        string? ns,
        string? className,
        HashSet<string> usedRelativePaths)
    {
        var safeFileBase = SanitizeSegment(className, "Script");
        var nsSegments = new List<string>();
        if (!string.IsNullOrWhiteSpace(ns))
        {
            var parts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                nsSegments.Add(SanitizeSegment(parts[i], "_ns"));
            }
        }

        string BuildCandidate(List<string> dirs, string fileStem, int collisionIndex)
        {
            var suffix = collisionIndex <= 1
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"_{collisionIndex}");
            var fileName = fileStem + suffix + ".cs";
            return dirs.Count == 0
                ? fileName
                : Path.Combine(Path.Combine(dirs.ToArray()), fileName);
        }

        var candidate = BuildCandidate(nsSegments, safeFileBase, 1);

        // Collapse overly deep namespaces if relative path exceeds 200 chars
        if (candidate.Length > MaxRelativePathLength && nsSegments.Count > 1)
        {
            var nsHash = ComputeShortHash(ns ?? string.Empty);
            var collapsedDirs = new List<string> { nsSegments[0], $"_ns_{nsHash}" };
            candidate = BuildCandidate(collapsedDirs, safeFileBase, 1);
            nsSegments = collapsedDirs;
        }

        if (candidate.Length > MaxRelativePathLength)
        {
            var stemHash = ComputeShortHash($"{ns}.{className}");
            safeFileBase = safeFileBase.Substring(0, Math.Min(safeFileBase.Length, 48)) + "_" + stemHash;
            nsSegments.Clear();
            candidate = BuildCandidate(nsSegments, safeFileBase, 1);
        }

        var collision = 1;
        while (!usedRelativePaths.Add(candidate))
        {
            collision++;
            candidate = BuildCandidate(nsSegments, safeFileBase, collision);
        }

        return candidate;
    }

    public static string EnsureConfinedPath(string rootDirectory, string relativePath)
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Export target path escapes output root: '{relativePath}'.");
        }

        return combined;
    }

    private static string ComputeShortHash(string input)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return Convert.ToHexString(hash.Slice(0, 4)).ToLowerInvariant();
    }
}

public sealed class CSharpProjectExportResult
{
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public CSharpProjectExportSourceMode SourceMode { get; init; }
    public string Mode { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string SolutionPath { get; init; } = string.Empty;
    public string ReportPath { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }
    public int GameAssembliesCount { get; init; }
    public int ReferenceAssembliesCount { get; init; }
    public int TotalTypesExported { get; init; }
    public int FailedTypesCount { get; init; }
    public List<BrokenDependencyCycle> BrokenCycles { get; init; } = new();
    public List<string> UnresolvedReferences { get; init; } = new();
    public List<FailedTypeExportRecord> FailedTypes { get; init; } = new();
    public List<ExportedAssemblyReport> Assemblies { get; init; } = new();
    public Dictionary<string, string> ScriptGuidMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string SummaryMessage => Success
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Exported {TotalTypesExported:N0} C# types across {GameAssembliesCount:N0} assembly project(s) [{Mode}] in {ElapsedMs:N0} ms")
            + (FailedTypesCount > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({FailedTypesCount:N0} stub fallback(s))") : string.Empty)
            + (BrokenCycles.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({BrokenCycles.Count:N0} cycle(s) broken)") : string.Empty)
        : $"C# project export failed: {ErrorMessage}";
}

/// <summary>
/// Coherent C# Script Project Exporter (SCRIPT-12).
/// Reconstructs clean SDK-style .sln/.csproj solutions or Unity Assets/Scripts (.asmdef + .cs.meta)
/// from Mono Managed DLLs, IL2CPP backup Managed DLLs, DummyDlls, dump.cs, or global-metadata.dat.
/// </summary>
public sealed class CSharpProjectExporterService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Guid CSharpSdkProjectTypeGuid = new("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC");

    public Task<CSharpProjectExportResult> ExportAsync(
        CSharpProjectExportOptions options,
        IProgress<CSharpProjectExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Export(options, progress, cancellationToken), cancellationToken);
    }

    public CSharpProjectExportResult Export(
        CSharpProjectExportOptions options,
        IProgress<CSharpProjectExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return Fail("Output directory must be specified.", sw.ElapsedMilliseconds);
        }

        var outputRoot = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputRoot);

        // Merge explicit source inputs with ScriptSourceDiscovery when projectRoot/customDir supplied
        var managedDirs = new List<string>(options.ManagedDirectories);
        var dummyDirs = new List<string>(options.DummyDllDirectories);
        var dumpCsPath = options.DumpCsPath;
        var scriptJsonPath = options.ScriptJsonPath;
        var globalMetadataPath = options.GlobalMetadataPath;
        var gameAssemblyPath = options.GameAssemblyPath;

        if (!string.IsNullOrWhiteSpace(options.ProjectRoot) || !string.IsNullOrWhiteSpace(options.CustomScriptsDirectory))
        {
            var discovered = ScriptSourceDiscovery.Discover(
                projectRoot: options.ProjectRoot,
                customScriptsDirectory: options.CustomScriptsDirectory,
                gameAssemblyPath: options.GameAssemblyPath,
                globalMetadataPath: options.GlobalMetadataPath);

            managedDirs.AddRange(discovered.ManagedDirectories);
            dummyDirs.AddRange(discovered.DummyDllDirectories);
            dumpCsPath ??= discovered.DumpCsPath;
            scriptJsonPath ??= discovered.ScriptJsonPath;
            globalMetadataPath ??= discovered.GlobalMetadataPath;
            gameAssemblyPath ??= discovered.GameAssemblyPath;
        }

        var validManagedDirs = managedDirs
            .Where(static d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var validDummyDirs = dummyDirs
            .Where(static d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validManagedDirs.Count > 0)
        {
            return ExportFromManagedDllDirectories(
                options,
                outputRoot,
                validManagedDirs,
                isDummyDll: false,
                scriptJsonPath: scriptJsonPath,
                progress: progress,
                sw: sw,
                cancellationToken: cancellationToken);
        }

        if (validDummyDirs.Count > 0)
        {
            return ExportFromManagedDllDirectories(
                options,
                outputRoot,
                validDummyDirs,
                isDummyDll: true,
                scriptJsonPath: scriptJsonPath,
                progress: progress,
                sw: sw,
                cancellationToken: cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(dumpCsPath) && File.Exists(dumpCsPath))
        {
            return ExportFromDumpCs(
                options,
                outputRoot,
                Path.GetFullPath(dumpCsPath),
                scriptJsonPath,
                progress,
                sw,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(globalMetadataPath) && File.Exists(globalMetadataPath))
        {
            return ExportFromGlobalMetadata(
                options,
                outputRoot,
                Path.GetFullPath(globalMetadataPath),
                gameAssemblyPath,
                scriptJsonPath,
                progress,
                sw,
                cancellationToken);
        }

        return Fail(
            "No managed DLLs, IL2CPP DummyDlls, dump.cs, or readable global-metadata.dat were found to export.",
            sw.ElapsedMilliseconds);
    }

    private CSharpProjectExportResult ExportFromManagedDllDirectories(
        CSharpProjectExportOptions options,
        string outputRoot,
        List<string> dllDirectories,
        bool isDummyDll,
        string? scriptJsonPath,
        IProgress<CSharpProjectExportProgress>? progress,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        // 1. Metadata-only discovery of all managed DLLs across search directories
        var allDiscoveredDlls = new Dictionary<string, AssemblyMetadataSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dllDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var dllPath in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var meta = ReadAssemblyMetadataSummary(dllPath);
                if (meta == null)
                {
                    continue;
                }

                if (!allDiscoveredDlls.ContainsKey(meta.AssemblyShortName))
                {
                    allDiscoveredDlls[meta.AssemblyShortName] = meta;
                }
            }
        }

        if (allDiscoveredDlls.Count == 0)
        {
            return Fail("No valid .NET managed assemblies were found in the discovered directories.", sw.ElapsedMilliseconds);
        }

        // 2. Filter which assemblies to export as source vs treat as binary references
        var selectedFilter = options.SelectedAssemblyNames != null && options.SelectedAssemblyNames.Count > 0
            ? new HashSet<string>(
                options.SelectedAssemblyNames.Select(UnityProjectGuidUtility.NormalizeAssemblyShortName),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var assembliesToExport = new List<AssemblyMetadataSummary>();
        foreach (var kv in allDiscoveredDlls.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var meta = kv.Value;
            if (selectedFilter != null)
            {
                if (selectedFilter.Contains(meta.AssemblyShortName))
                {
                    assembliesToExport.Add(meta);
                }
            }
            else if (options.IncludeEngineAndThirdPartyAssemblies || meta.IsGameAssembly)
            {
                assembliesToExport.Add(meta);
            }
        }

        if (assembliesToExport.Count == 0)
        {
            return Fail(
                "No game assemblies matched the export filter (enable IncludeEngineAndThirdPartyAssemblies or adjust SelectedAssemblyNames).",
                sw.ElapsedMilliseconds);
        }

        // 3. Resolve dependency graph & break cycles deterministically
        var depInput = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assembliesToExport)
        {
            depInput[asm.AssemblyShortName] = asm.ReferencedAssemblies;
        }
        var graphResolution = ScriptDependencyGraphResolver.Resolve(depInput);

        // 4. Determine whether COMPLETE (unstripped) local Unity/Mono BCL DLLs exist in search dirs.
        //    IL2CPP backup Managed/ folders contain UnityLinker-stripped mscorlib.dll files that omit
        //    compile-time attributes/constants (MethodImplAttribute, StructLayoutAttribute, LayoutKind, Math.PI).
        //    When stripped, fall back to the SDK's complete netstandard2.1 BCL so generated .csproj files compile cleanly.
        var exportedNameSet = new HashSet<string>(
            assembliesToExport.Select(static a => a.AssemblyShortName),
            StringComparer.OrdinalIgnoreCase);
        var useLocalBcl = allDiscoveredDlls.TryGetValue("mscorlib", out var mscorlibMeta)
            && IsCompleteUnstrippedBcl(mscorlibMeta.FilePath);

        // Precompute transitive non-BCL (and local-BCL when unstripped) binary references per exported assembly
        // so indirect Unity engine module dependencies (e.g. UnityEngine.UI / UnityEngine.UIElementsModule) never cause CS0012.
        var expandedDllRefsByAssembly = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unresolvedByAssembly = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unresolvedRefsGlobal = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in assembliesToExport)
        {
            var cycleFallbackRefs = graphResolution.CycleFallbackDllReferencesByAssembly.TryGetValue(asm.AssemblyShortName, out var cr)
                ? (IReadOnlyList<string>)cr
                : Array.Empty<string>();
            var asmUnresolved = new List<string>();
            var expanded = ExpandTransitiveBinaryReferences(
                asm.ReferencedAssemblies,
                cycleFallbackRefs,
                exportedNameSet,
                allDiscoveredDlls,
                useLocalBcl,
                asmUnresolved,
                unresolvedRefsGlobal);
            expandedDllRefsByAssembly[asm.AssemblyShortName] = expanded;
            unresolvedByAssembly[asm.AssemblyShortName] = asmUnresolved;
        }

        // 5. Copy reference DLLs to _References/ when in StandaloneDotNetSolution mode
        var referencesDir = Path.Combine(outputRoot, "_References");
        var copiedRefDllNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution && options.CopyReferenceDlls)
        {
            var neededBinaryRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var refsList in expandedDllRefsByAssembly.Values)
            {
                foreach (var refName in refsList)
                {
                    if (allDiscoveredDlls.ContainsKey(refName))
                    {
                        neededBinaryRefs.Add(refName);
                    }
                }
            }

            // Include local mscorlib/netstandard if present and unstripped local BCL mode is active
            if (useLocalBcl)
            {
                if (allDiscoveredDlls.ContainsKey("mscorlib")) neededBinaryRefs.Add("mscorlib");
                if (allDiscoveredDlls.ContainsKey("netstandard")) neededBinaryRefs.Add("netstandard");
            }

            if (neededBinaryRefs.Count > 0)
            {
                Directory.CreateDirectory(referencesDir);
                foreach (var refShort in neededBinaryRefs.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var refMeta = allDiscoveredDlls[refShort];
                    var safeDllName = ScriptExportPathSanitizer.SanitizeSegment(refMeta.AssemblyShortName, "Ref") + ".dll";
                    var destPath = ScriptExportPathSanitizer.EnsureConfinedPath(referencesDir, safeDllName);
                    File.Copy(refMeta.FilePath, destPath, overwrite: true);
                    copiedRefDllNames.Add(refShort);
                }
            }
        }

        // 6. Optional single-pass script.json RVA index for DummyDll mode
        Dictionary<string, List<ScriptMethodSymbol>>? scriptJsonByType = null;
        if (isDummyDll && !string.IsNullOrWhiteSpace(scriptJsonPath) && File.Exists(scriptJsonPath))
        {
            scriptJsonByType = IndexScriptJsonByType(scriptJsonPath, cancellationToken);
        }

        // 7. Decompile each assembly in bounded parallel (each worker owns its own PEFile + CSharpDecompiler)
        var completedAssemblies = 0;
        var completedTypes = 0;
        var estimatedTotalTypes = assembliesToExport.Sum(static a => a.EstimatedTypeCount);
        var failedTypesBag = new ConcurrentBag<FailedTypeExportRecord>();
        var assemblyReportsBag = new ConcurrentBag<ExportedAssemblyReport>();
        var scriptGuidMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(options.MaxDegreeOfParallelism, 1, 4),
            CancellationToken = cancellationToken
        };

        var srcRoot = options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution
            ? Path.Combine(outputRoot, "src")
            : outputRoot;
        Directory.CreateDirectory(srcRoot);

        Parallel.ForEach(assembliesToExport, parallelOptions, asmMeta =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeAsmFolderName = ScriptExportPathSanitizer.SanitizeSegment(asmMeta.AssemblyShortName, "Assembly");
            var asmOutDir = ScriptExportPathSanitizer.EnsureConfinedPath(srcRoot, safeAsmFolderName);
            Directory.CreateDirectory(asmOutDir);

            var projRefs = graphResolution.ProjectReferencesByAssembly.TryGetValue(asmMeta.AssemblyShortName, out var pr)
                ? pr
                : new List<string>();
            var dllRefs = expandedDllRefsByAssembly.TryGetValue(asmMeta.AssemblyShortName, out var er)
                ? er
                : new List<string>();
            var unresolved = unresolvedByAssembly.TryGetValue(asmMeta.AssemblyShortName, out var ur)
                ? ur
                : new List<string>();

            var exportedCount = 0;
            var failedCount = 0;
            var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var peFile = OpenPrefetchedPeFile(asmMeta.FilePath);
            var resolver = CreateResolver(asmMeta.FilePath, dllDirectories);
            var decompilerSettings = CreateUnityFriendlyDecompilerSettings();
            var decompiler = new CSharpDecompiler(peFile, resolver, decompilerSettings);

            var topLevelTypes = decompiler.TypeSystem.MainModule.TopLevelTypeDefinitions
                .Where(static t => !AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName(t.Name))
                .OrderBy(static t => t.Namespace ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static t => t.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var typeDef in topLevelTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ns = typeDef.Namespace ?? string.Empty;
                var className = typeDef.Name ?? "UnnamedType";
                var fullName = string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";

                var relPath = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath(ns, className, usedRelativePaths);
                var targetFile = ScriptExportPathSanitizer.EnsureConfinedPath(asmOutDir, relPath);
                var targetDir = Path.GetDirectoryName(targetFile)!;
                Directory.CreateDirectory(targetDir);
                TrackDirectoriesForMeta(asmOutDir, targetDir, createdDirs);

                string sourceText;
                try
                {
                    var code = decompiler.DecompileTypeAsString(typeDef.FullTypeName);
                    if (isDummyDll)
                    {
                        sourceText = FormatDummyDllSource(asmMeta.AssemblyShortName, fullName, ns, className, code, scriptJsonByType);
                    }
                    else
                    {
                        sourceText = code;
                    }
                    exportedCount++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    failedTypesBag.Add(new FailedTypeExportRecord(asmMeta.AssemblyShortName, fullName, ex.Message));
                    sourceText = BuildFailedTypeStub(asmMeta.AssemblyShortName, ns, className, ex.Message);
                }

                File.WriteAllText(targetFile, sourceText, Utf8NoBom);

                var scriptGuid = UnityProjectGuidUtility.ComputeScriptGuid(asmMeta.AssemblyShortName, ns, className);
                scriptGuidMap[$"{asmMeta.AssemblyShortName}|{fullName}"] = scriptGuid;

                if (options.GenerateUnityMetaFiles)
                {
                    File.WriteAllText(
                        targetFile + ".meta",
                        UnityProjectGuidUtility.BuildMonoScriptMetaContent(scriptGuid),
                        Utf8NoBom);
                }

                Interlocked.Increment(ref completedTypes);
            }

            if (options.GenerateUnityMetaFiles)
            {
                WriteFolderMetaFiles(outputRoot, asmOutDir, createdDirs);
            }

            var csprojPath = string.Empty;
            var asmdefPath = string.Empty;

            if (options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution)
            {
                csprojPath = ScriptExportPathSanitizer.EnsureConfinedPath(asmOutDir, safeAsmFolderName + ".csproj");
                var csprojXml = BuildCsprojXml(
                    asmMeta.AssemblyShortName,
                    asmMeta.DetectedTargetFramework,
                    useLocalBcl,
                    projRefs,
                    dllRefs,
                    allDiscoveredDlls,
                    options.CopyReferenceDlls ? referencesDir : null,
                    asmOutDir);
                File.WriteAllText(csprojPath, csprojXml, Utf8NoBom);
            }
            else if (options.GenerateAsmdefFiles && !ScriptProjectAssemblyClassifier.IsUnityPredefinedAssembly(asmMeta.AssemblyShortName))
            {
                asmdefPath = ScriptExportPathSanitizer.EnsureConfinedPath(asmOutDir, safeAsmFolderName + ".asmdef");
                var asmdefJson = BuildAsmdefJson(asmMeta.AssemblyShortName, projRefs, dllRefs);
                File.WriteAllText(asmdefPath, asmdefJson, Utf8NoBom);
                if (options.GenerateUnityMetaFiles)
                {
                    var asmdefGuid = UnityProjectGuidUtility.ComputeAssetGuid("asmdef", null, 0, asmMeta.AssemblyShortName);
                    File.WriteAllText(
                        asmdefPath + ".meta",
                        UnityProjectGuidUtility.BuildAsmdefMetaContent(asmdefGuid),
                        Utf8NoBom);
                }
            }

            assemblyReportsBag.Add(new ExportedAssemblyReport
            {
                AssemblyName = asmMeta.AssemblyShortName,
                SourceFile = asmMeta.FilePath,
                ProjectFilePath = csprojPath,
                AsmdefFilePath = asmdefPath,
                TypesExported = exportedCount,
                TypesFailed = failedCount,
                ProjectReferences = projRefs.ToList(),
                DllReferences = dllRefs,
                UnresolvedReferences = unresolved
            });

            var doneAsm = Interlocked.Increment(ref completedAssemblies);
            progress?.Report(new CSharpProjectExportProgress(
                doneAsm,
                assembliesToExport.Count,
                Volatile.Read(ref completedTypes),
                estimatedTotalTypes,
                asmMeta.AssemblyShortName,
                $"Exported {asmMeta.AssemblyShortName} ({doneAsm}/{assembliesToExport.Count})"));
        });

        var sortedReports = assemblyReportsBag
            .OrderBy(static r => r.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var solutionPath = string.Empty;
        if (options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution)
        {
            var safeProjName = ScriptExportPathSanitizer.SanitizeSegment(options.ProjectName, "ExportedScripts");
            solutionPath = ScriptExportPathSanitizer.EnsureConfinedPath(outputRoot, safeProjName + ".sln");
            var slnText = BuildSolutionFile(outputRoot, sortedReports);
            File.WriteAllText(solutionPath, slnText, Utf8NoBom);
        }

        sw.Stop();
        var modeLabel = isDummyDll ? "Il2CppStubs" : "MonoManaged";
        var sourceMode = isDummyDll
            ? CSharpProjectExportSourceMode.Il2CppDummyDlls
            : CSharpProjectExportSourceMode.MonoManagedAssemblies;

        var result = new CSharpProjectExportResult
        {
            Success = true,
            SourceMode = sourceMode,
            Mode = modeLabel,
            OutputDirectory = outputRoot,
            SolutionPath = solutionPath,
            ReportPath = Path.Combine(outputRoot, "export-report.json"),
            ElapsedMs = sw.ElapsedMilliseconds,
            GameAssembliesCount = sortedReports.Count,
            ReferenceAssembliesCount = copiedRefDllNames.Count,
            TotalTypesExported = sortedReports.Sum(static r => r.TypesExported),
            FailedTypesCount = failedTypesBag.Count,
            BrokenCycles = graphResolution.BrokenCycles,
            UnresolvedReferences = unresolvedRefsGlobal.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            FailedTypes = failedTypesBag.OrderBy(static f => f.AssemblyName, StringComparer.OrdinalIgnoreCase).ThenBy(static f => f.TypeFullName, StringComparer.OrdinalIgnoreCase).ToList(),
            Assemblies = sortedReports,
            ScriptGuidMap = new Dictionary<string, string>(scriptGuidMap, StringComparer.OrdinalIgnoreCase)
        };

        WriteExportReportJson(result);
        return result;
    }

    private CSharpProjectExportResult ExportFromDumpCs(
        CSharpProjectExportOptions options,
        string outputRoot,
        string dumpCsPath,
        string? scriptJsonPath,
        IProgress<CSharpProjectExportProgress>? progress,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var parsedTypes = Il2CppDumpCatalogHelper.ParseCatalog(dumpCsPath, cancellationToken, captureSourceBlocks: true);
        if (parsedTypes.Count == 0)
        {
            return Fail("dump.cs did not contain any parseable type definitions.", sw.ElapsedMilliseconds);
        }

        var scriptJsonByType = !string.IsNullOrWhiteSpace(scriptJsonPath) && File.Exists(scriptJsonPath)
            ? IndexScriptJsonByType(scriptJsonPath, cancellationToken)
            : null;

        var selectedFilter = options.SelectedAssemblyNames != null && options.SelectedAssemblyNames.Count > 0
            ? new HashSet<string>(
                options.SelectedAssemblyNames.Select(UnityProjectGuidUtility.NormalizeAssemblyShortName),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var grouped = parsedTypes
            .GroupBy(static t => string.IsNullOrWhiteSpace(t.AssemblyName) ? "Assembly-CSharp" : t.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .Where(g => selectedFilter != null
                ? selectedFilter.Contains(g.Key)
                : (options.IncludeEngineAndThirdPartyAssemblies || ScriptProjectAssemblyClassifier.IsGameAssembly(g.Key)))
            .OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grouped.Count == 0)
        {
            return Fail("No game assemblies in dump.cs matched the export filter.", sw.ElapsedMilliseconds);
        }

        return WriteIl2CppStubProject(
            options,
            outputRoot,
            CSharpProjectExportSourceMode.Il2CppDumpCs,
            dumpCsPath,
            grouped.Select(g => (
                AssemblyName: g.Key,
                Types: (IReadOnlyList<(string Namespace, string ClassName, string SourceCode)>)g.Select(t => (
                    t.Namespace,
                    t.ClassName,
                    FormatIl2CppDumpStub(g.Key, t.Namespace, t.ClassName, t.SourceBlock, scriptJsonByType))).ToList()
            )).ToList(),
            progress,
            sw,
            cancellationToken);
    }

    private CSharpProjectExportResult ExportFromGlobalMetadata(
        CSharpProjectExportOptions options,
        string outputRoot,
        string globalMetadataPath,
        string? gameAssemblyPath,
        string? scriptJsonPath,
        IProgress<CSharpProjectExportProgress>? progress,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var catalog = Il2CppMetadataCatalogHelper.TryReadImagesAndTypes(globalMetadataPath, cancellationToken);
        if (catalog == null || catalog.Images.Count == 0)
        {
            return Fail("global-metadata.dat could not be read or appears obfuscated/encrypted.", sw.ElapsedMilliseconds);
        }

        var scriptJsonByType = !string.IsNullOrWhiteSpace(scriptJsonPath) && File.Exists(scriptJsonPath)
            ? IndexScriptJsonByType(scriptJsonPath, cancellationToken)
            : null;

        var selectedFilter = options.SelectedAssemblyNames != null && options.SelectedAssemblyNames.Count > 0
            ? new HashSet<string>(
                options.SelectedAssemblyNames.Select(UnityProjectGuidUtility.NormalizeAssemblyShortName),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var metadataReader = new Il2CppMetadataReader();
        var assemblyBatches = new List<(string AssemblyName, IReadOnlyList<(string Namespace, string ClassName, string SourceCode)> Types)>();

        foreach (var image in catalog.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asmShort = UnityProjectGuidUtility.NormalizeAssemblyShortName(image.ImageName);
            if (string.IsNullOrWhiteSpace(asmShort))
            {
                continue;
            }

            var include = selectedFilter != null
                ? selectedFilter.Contains(asmShort)
                : (options.IncludeEngineAndThirdPartyAssemblies || ScriptProjectAssemblyClassifier.IsGameAssembly(asmShort));
            if (!include || image.Types.Count == 0)
            {
                continue;
            }

            var typeEntries = new List<(string Namespace, string ClassName, string SourceCode)>(image.Types.Count);
            foreach (var rawType in image.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var req = new ScriptLookupRequest
                {
                    AssemblyName = image.ImageName,
                    Namespace = rawType.Namespace,
                    ClassName = rawType.ClassName,
                    GameAssemblyPath = gameAssemblyPath,
                    GlobalMetadataPath = globalMetadataPath
                };

                var extracted = metadataReader.TryExtractType(globalMetadataPath, req, gameAssemblyPath, out _, cancellationToken);
                var code = extracted != null && extracted.Success
                    ? extracted.SourceCode
                    : BuildSimpleIl2CppStub(asmShort, rawType.Namespace, rawType.ClassName, rawType.Kind);
                code = AppendScriptJsonRvaComments(code, rawType.Namespace, rawType.ClassName, scriptJsonByType);
                typeEntries.Add((rawType.Namespace, rawType.ClassName, code));
            }

            assemblyBatches.Add((asmShort, typeEntries));
        }

        if (assemblyBatches.Count == 0)
        {
            return Fail("No game assemblies in global-metadata.dat matched the export filter.", sw.ElapsedMilliseconds);
        }

        return WriteIl2CppStubProject(
            options,
            outputRoot,
            CSharpProjectExportSourceMode.Il2CppGlobalMetadata,
            globalMetadataPath,
            assemblyBatches,
            progress,
            sw,
            cancellationToken);
    }

    private CSharpProjectExportResult WriteIl2CppStubProject(
        CSharpProjectExportOptions options,
        string outputRoot,
        CSharpProjectExportSourceMode sourceMode,
        string sourceArtifactPath,
        List<(string AssemblyName, IReadOnlyList<(string Namespace, string ClassName, string SourceCode)> Types)> batches,
        IProgress<CSharpProjectExportProgress>? progress,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var srcRoot = options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution
            ? Path.Combine(outputRoot, "src")
            : outputRoot;
        Directory.CreateDirectory(srcRoot);

        var reports = new List<ExportedAssemblyReport>(batches.Count);
        var scriptGuidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalTypes = batches.Sum(static b => b.Types.Count);
        var completedTypes = 0;

        for (var b = 0; b < batches.Count; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (asmName, types) = batches[b];
            var safeAsmFolder = ScriptExportPathSanitizer.SanitizeSegment(asmName, "Assembly");
            var asmOutDir = ScriptExportPathSanitizer.EnsureConfinedPath(srcRoot, safeAsmFolder);
            Directory.CreateDirectory(asmOutDir);

            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (ns, className, sourceCode) in types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relPath = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath(ns, className, usedPaths);
                var targetPath = ScriptExportPathSanitizer.EnsureConfinedPath(asmOutDir, relPath);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDir);
                TrackDirectoriesForMeta(asmOutDir, targetDir, createdDirs);

                File.WriteAllText(targetPath, sourceCode, Utf8NoBom);

                var fullName = string.IsNullOrWhiteSpace(ns) ? className : $"{ns}.{className}";
                var guid = UnityProjectGuidUtility.ComputeScriptGuid(asmName, ns, className);
                scriptGuidMap[$"{asmName}|{fullName}"] = guid;

                if (options.GenerateUnityMetaFiles)
                {
                    File.WriteAllText(
                        targetPath + ".meta",
                        UnityProjectGuidUtility.BuildMonoScriptMetaContent(guid),
                        Utf8NoBom);
                }

                completedTypes++;
            }

            if (options.GenerateUnityMetaFiles)
            {
                WriteFolderMetaFiles(outputRoot, asmOutDir, createdDirs);
            }

            var csprojPath = string.Empty;
            var asmdefPath = string.Empty;
            if (options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution)
            {
                csprojPath = ScriptExportPathSanitizer.EnsureConfinedPath(asmOutDir, safeAsmFolder + ".csproj");
                var csprojXml = BuildCsprojXml(
                    asmName,
                    detectedTargetFramework: "netstandard2.1",
                    hasLocalBcl: false,
                    projectReferences: Array.Empty<string>(),
                    dllReferences: Array.Empty<string>(),
                    allDiscoveredDlls: new Dictionary<string, AssemblyMetadataSummary>(StringComparer.OrdinalIgnoreCase),
                    referencesDir: null,
                    projectDir: asmOutDir);
                File.WriteAllText(csprojPath, csprojXml, Utf8NoBom);
            }
            else if (options.GenerateAsmdefFiles && !ScriptProjectAssemblyClassifier.IsUnityPredefinedAssembly(asmName))
            {
                asmdefPath = ScriptExportPathSanitizer.EnsureConfinedPath(asmOutDir, safeAsmFolder + ".asmdef");
                File.WriteAllText(asmdefPath, BuildAsmdefJson(asmName, Array.Empty<string>(), Array.Empty<string>()), Utf8NoBom);
                if (options.GenerateUnityMetaFiles)
                {
                    var asmdefGuid = UnityProjectGuidUtility.ComputeAssetGuid("asmdef", null, 0, asmName);
                    File.WriteAllText(asmdefPath + ".meta", UnityProjectGuidUtility.BuildAsmdefMetaContent(asmdefGuid), Utf8NoBom);
                }
            }

            reports.Add(new ExportedAssemblyReport
            {
                AssemblyName = asmName,
                SourceFile = sourceArtifactPath,
                ProjectFilePath = csprojPath,
                AsmdefFilePath = asmdefPath,
                TypesExported = types.Count,
                TypesFailed = 0
            });

            progress?.Report(new CSharpProjectExportProgress(
                b + 1,
                batches.Count,
                completedTypes,
                totalTypes,
                asmName,
                $"Exported IL2CPP stubs for {asmName} ({b + 1}/{batches.Count})"));
        }

        var solutionPath = string.Empty;
        if (options.LayoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution)
        {
            var safeProjName = ScriptExportPathSanitizer.SanitizeSegment(options.ProjectName, "ExportedScripts");
            solutionPath = ScriptExportPathSanitizer.EnsureConfinedPath(outputRoot, safeProjName + ".sln");
            File.WriteAllText(solutionPath, BuildSolutionFile(outputRoot, reports), Utf8NoBom);
        }

        sw.Stop();
        var result = new CSharpProjectExportResult
        {
            Success = true,
            SourceMode = sourceMode,
            Mode = "Il2CppStubs",
            OutputDirectory = outputRoot,
            SolutionPath = solutionPath,
            ReportPath = Path.Combine(outputRoot, "export-report.json"),
            ElapsedMs = sw.ElapsedMilliseconds,
            GameAssembliesCount = reports.Count,
            ReferenceAssembliesCount = 0,
            TotalTypesExported = reports.Sum(static r => r.TypesExported),
            FailedTypesCount = 0,
            Assemblies = reports,
            ScriptGuidMap = scriptGuidMap
        };

        WriteExportReportJson(result);
        return result;
    }

    private sealed class AssemblyMetadataSummary
    {
        public required string AssemblyShortName { get; init; }
        public required string FilePath { get; init; }
        public required bool IsGameAssembly { get; init; }
        public required int EstimatedTypeCount { get; init; }
        public required string DetectedTargetFramework { get; init; }
        public required List<string> ReferencedAssemblies { get; init; }
    }

    internal static bool IsCompleteUnstrippedBcl(string? mscorlibPath)
    {
        if (string.IsNullOrWhiteSpace(mscorlibPath) || !File.Exists(mscorlibPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(mscorlibPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 64)
            {
                return false;
            }

            using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            var md = peReader.GetMetadataReader(MetadataReaderOptions.None);
            var hasMethodImplAttr = false;
            var hasMethodImplOptions = false;
            var hasStructLayoutAttr = false;
            var hasLayoutKind = false;

            void CheckType(string ns, string name)
            {
                if (ns.Equals("System.Runtime.CompilerServices", StringComparison.Ordinal))
                {
                    if (name.Equals("MethodImplAttribute", StringComparison.Ordinal)) hasMethodImplAttr = true;
                    else if (name.Equals("MethodImplOptions", StringComparison.Ordinal)) hasMethodImplOptions = true;
                }
                else if (ns.Equals("System.Runtime.InteropServices", StringComparison.Ordinal))
                {
                    if (name.Equals("StructLayoutAttribute", StringComparison.Ordinal)) hasStructLayoutAttr = true;
                    else if (name.Equals("LayoutKind", StringComparison.Ordinal)) hasLayoutKind = true;
                }
            }

            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                CheckType(md.GetString(td.Namespace), md.GetString(td.Name));
                if (hasMethodImplAttr && hasMethodImplOptions && hasStructLayoutAttr && hasLayoutKind)
                {
                    return true;
                }
            }

            foreach (var handle in md.ExportedTypes)
            {
                var et = md.GetExportedType(handle);
                CheckType(md.GetString(et.Namespace), md.GetString(et.Name));
                if (hasMethodImplAttr && hasMethodImplOptions && hasStructLayoutAttr && hasLayoutKind)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ExpandTransitiveBinaryReferences(
        IReadOnlyList<string> directRefs,
        IReadOnlyList<string> cycleFallbackRefs,
        HashSet<string> exportedNameSet,
        Dictionary<string, AssemblyMetadataSummary> allDiscoveredDlls,
        bool useLocalBcl,
        List<string> unresolved,
        ConcurrentDictionary<string, byte> unresolvedRefsGlobal)
    {
        var dllRefs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedForBfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        void TryAddBinaryRef(string refName, bool isDirect)
        {
            if (string.IsNullOrWhiteSpace(refName))
            {
                return;
            }

            if (exportedNameSet.Contains(refName))
            {
                if (isDirect && cycleFallbackRefs.Contains(refName, StringComparer.OrdinalIgnoreCase) && seen.Add(refName))
                {
                    dllRefs.Add(refName);
                }

                // Still traverse through exported project references so binary engine modules
                // transitively exposed by their public APIs are included (avoids CS0012).
                if (allDiscoveredDlls.ContainsKey(refName) && visitedForBfs.Add(refName))
                {
                    queue.Enqueue(refName);
                }
                return;
            }

            if (allDiscoveredDlls.ContainsKey(refName))
            {
                if (!useLocalBcl && ScriptProjectAssemblyClassifier.IsFrameworkOrBclAssembly(refName))
                {
                    return;
                }

                if (seen.Add(refName))
                {
                    dllRefs.Add(refName);
                }
                if (visitedForBfs.Add(refName))
                {
                    queue.Enqueue(refName);
                }
            }
            else if (isDirect && !ScriptProjectAssemblyClassifier.IsFrameworkOrBclAssembly(refName))
            {
                unresolved.Add(refName);
                unresolvedRefsGlobal.TryAdd(refName, 0);
            }
        }

        for (var i = 0; i < directRefs.Count; i++)
        {
            TryAddBinaryRef(directRefs[i], isDirect: true);
        }

        for (var i = 0; i < cycleFallbackRefs.Count; i++)
        {
            var cr = cycleFallbackRefs[i];
            if (allDiscoveredDlls.ContainsKey(cr))
            {
                if (seen.Add(cr))
                {
                    dllRefs.Add(cr);
                }
                if (visitedForBfs.Add(cr))
                {
                    queue.Enqueue(cr);
                }
            }
        }

        // BFS transitive closure across non-exported engine/third-party DLLs available on disk.
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!allDiscoveredDlls.TryGetValue(current, out var currentMeta))
            {
                continue;
            }

            for (var i = 0; i < currentMeta.ReferencedAssemblies.Count; i++)
            {
                var transitiveRef = currentMeta.ReferencedAssemblies[i];
                if (exportedNameSet.Contains(transitiveRef))
                {
                    if (allDiscoveredDlls.ContainsKey(transitiveRef) && visitedForBfs.Add(transitiveRef))
                    {
                        queue.Enqueue(transitiveRef);
                    }
                    continue;
                }

                if (!useLocalBcl && ScriptProjectAssemblyClassifier.IsFrameworkOrBclAssembly(transitiveRef))
                {
                    continue;
                }

                if (allDiscoveredDlls.ContainsKey(transitiveRef))
                {
                    if (seen.Add(transitiveRef))
                    {
                        dllRefs.Add(transitiveRef);
                    }
                    if (visitedForBfs.Add(transitiveRef))
                    {
                        queue.Enqueue(transitiveRef);
                    }
                }
            }
        }

        if (useLocalBcl && allDiscoveredDlls.ContainsKey("mscorlib") && seen.Add("mscorlib"))
        {
            dllRefs.Add("mscorlib");
        }

        return dllRefs;
    }

    private static AssemblyMetadataSummary? ReadAssemblyMetadataSummary(string dllPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(dllPath);
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 64)
            {
                return null;
            }

            using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var md = peReader.GetMetadataReader(MetadataReaderOptions.None);
            if (!md.IsAssembly)
            {
                return null;
            }

            var asmDef = md.GetAssemblyDefinition();
            var shortName = md.GetString(asmDef.Name);
            if (string.IsNullOrWhiteSpace(shortName))
            {
                shortName = UnityProjectGuidUtility.NormalizeAssemblyShortName(fullPath);
            }

            var refs = new List<string>(md.AssemblyReferences.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var handle in md.AssemblyReferences)
            {
                var aref = md.GetAssemblyReference(handle);
                var refName = md.GetString(aref.Name);
                if (!string.IsNullOrWhiteSpace(refName) && seen.Add(refName))
                {
                    refs.Add(refName);
                }
            }

            var detectedTfm = DetectTargetFrameworkMoniker(md, refs);

            return new AssemblyMetadataSummary
            {
                AssemblyShortName = shortName,
                FilePath = fullPath,
                IsGameAssembly = ScriptProjectAssemblyClassifier.IsGameAssembly(shortName),
                EstimatedTypeCount = md.TypeDefinitions.Count,
                DetectedTargetFramework = detectedTfm,
                ReferencedAssemblies = refs
            };
        }
        catch
        {
            return null;
        }
    }

    private static string DetectTargetFrameworkMoniker(MetadataReader md, List<string> refs)
    {
        try
        {
            foreach (var caHandle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(caHandle);
                string? attrTypeName = null;
                if (ca.Constructor.Kind == HandleKind.MemberReference)
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        attrTypeName = md.GetString(tr.Name);
                    }
                }

                if (!string.Equals(attrTypeName, "TargetFrameworkAttribute", StringComparison.Ordinal))
                {
                    continue;
                }

                var blobReader = md.GetBlobReader(ca.Value);
                if (blobReader.Length >= 4 && blobReader.ReadUInt16() == 1)
                {
                    var tfmString = blobReader.ReadSerializedString();
                    if (!string.IsNullOrWhiteSpace(tfmString))
                    {
                        return MapTargetFrameworkAttributeToTfm(tfmString);
                    }
                }
            }
        }
        catch
        {
        }

        if (refs.Any(static r => r.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase)))
        {
            return "netstandard2.1";
        }

        return "netstandard2.1";
    }

    private static string MapTargetFrameworkAttributeToTfm(string frameworkName)
    {
        // Examples: ".NETCoreApp,Version=v10.0", ".NETCoreApp,Version=v8.0", ".NETStandard,Version=v2.1", ".NETFramework,Version=v4.7.1"
        var vIdx = frameworkName.IndexOf("Version=v", StringComparison.OrdinalIgnoreCase);
        var ver = vIdx >= 0 ? frameworkName.Substring(vIdx + "Version=v".Length).Trim() : string.Empty;

        if (frameworkName.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ver))
        {
            return "net" + ver;
        }
        if (frameworkName.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ver))
        {
            return "netstandard" + ver;
        }
        if (frameworkName.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ver))
        {
            return "net" + ver.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return "netstandard2.1";
    }

    private static PEFile OpenPrefetchedPeFile(string dllPath)
    {
        using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new PEFile(dllPath, stream, PEStreamOptions.PrefetchEntireImage, MetadataReaderOptions.None);
    }

    private static UniversalAssemblyResolver CreateResolver(string mainAssemblyPath, List<string> searchDirectories)
    {
        var resolver = new UniversalAssemblyResolver(
            mainAssemblyFileName: mainAssemblyPath,
            throwOnError: false,
            targetFramework: null,
            runtimePack: null,
            streamOptions: PEStreamOptions.PrefetchEntireImage,
            metadataOptions: MetadataReaderOptions.None);

        var parentDir = Path.GetDirectoryName(mainAssemblyPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            resolver.AddSearchDirectory(parentDir);
        }

        foreach (var dir in searchDirectories)
        {
            resolver.AddSearchDirectory(dir);
        }

        return resolver;
    }

    private static DecompilerSettings CreateUnityFriendlyDecompilerSettings()
    {
        return new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            UseDebugSymbols = false,
            ShowXmlDocumentation = false,
            FileScopedNamespaces = false,
            RemoveDeadCode = false
        };
    }

    private static string BuildCsprojXml(
        string assemblyName,
        string detectedTargetFramework,
        bool hasLocalBcl,
        IReadOnlyList<string> projectReferences,
        IReadOnlyList<string> dllReferences,
        Dictionary<string, AssemblyMetadataSummary> allDiscoveredDlls,
        string? referencesDir,
        string projectDir)
    {
        var tfm = string.IsNullOrWhiteSpace(detectedTargetFramework) ? "netstandard2.1" : detectedTargetFramework;
        var sb = new StringBuilder(1024);
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{EscapeXml(tfm)}</TargetFramework>");
        sb.AppendLine($"    <AssemblyName>{EscapeXml(assemblyName)}</AssemblyName>");
        sb.AppendLine($"    <RootNamespace>{EscapeXml(assemblyName)}</RootNamespace>");
        sb.AppendLine("    <LangVersion>latest</LangVersion>");
        sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
        sb.AppendLine("    <Nullable>disable</Nullable>");
        sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        sb.AppendLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>");
        sb.AppendLine("    <Deterministic>true</Deterministic>");
        sb.AppendLine("    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>");
        sb.AppendLine("    <WarningLevel>0</WarningLevel>");
        if (hasLocalBcl)
        {
            sb.AppendLine("    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>");
            sb.AppendLine("    <NoStdLib>true</NoStdLib>");
            sb.AppendLine("    <NoConfig>true</NoConfig>");
        }
        sb.AppendLine("  </PropertyGroup>");

        if (projectReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var projRef in projectReferences.OrderBy(static r => r, StringComparer.OrdinalIgnoreCase))
            {
                var safeRef = ScriptExportPathSanitizer.SanitizeSegment(projRef, "Assembly");
                sb.AppendLine($"    <ProjectReference Include=\"..\\{EscapeXml(safeRef)}\\{EscapeXml(safeRef)}.csproj\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        if (dllReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var dllRef in dllReferences.OrderBy(static r => r, StringComparer.OrdinalIgnoreCase))
            {
                string hintPath;
                if (!string.IsNullOrWhiteSpace(referencesDir))
                {
                    var safeDll = ScriptExportPathSanitizer.SanitizeSegment(dllRef, "Ref") + ".dll";
                    hintPath = Path.GetRelativePath(projectDir, Path.Combine(referencesDir, safeDll));
                }
                else if (allDiscoveredDlls.TryGetValue(dllRef, out var refMeta))
                {
                    hintPath = refMeta.FilePath;
                }
                else
                {
                    continue;
                }

                sb.AppendLine($"    <Reference Include=\"{EscapeXml(dllRef)}\">");
                sb.AppendLine($"      <HintPath>{EscapeXml(hintPath)}</HintPath>");
                sb.AppendLine("      <Private>false</Private>");
                sb.AppendLine("    </Reference>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static string BuildSolutionFile(string outputRoot, IReadOnlyList<ExportedAssemblyReport> assemblies)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        var projectGuids = new List<(ExportedAssemblyReport Report, string FormattedGuid)>(assemblies.Count);
        var typeGuidFormatted = UnityProjectGuidUtility.FormatVisualStudioGuid(CSharpSdkProjectTypeGuid);

        foreach (var asm in assemblies)
        {
            if (string.IsNullOrWhiteSpace(asm.ProjectFilePath))
            {
                continue;
            }

            var projGuid = UnityProjectGuidUtility.ComputeVisualStudioProjectGuid(asm.AssemblyName);
            var projGuidStr = UnityProjectGuidUtility.FormatVisualStudioGuid(projGuid);
            var relProjPath = Path.GetRelativePath(outputRoot, asm.ProjectFilePath).Replace('/', '\\');
            projectGuids.Add((asm, projGuidStr));

            sb.AppendLine($"Project(\"{typeGuidFormatted}\") = \"{asm.AssemblyName}\", \"{relProjPath}\", \"{projGuidStr}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var (_, guidStr) in projectGuids)
        {
            sb.AppendLine($"\t\t{guidStr}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{guidStr}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{guidStr}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{guidStr}.Release|Any CPU.Build.0 = Release|Any CPU");
        }
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }

    private static string BuildAsmdefJson(
        string assemblyName,
        IReadOnlyList<string> projectReferences,
        IReadOnlyList<string> dllReferences)
    {
        var refs = new List<string>(projectReferences);
        foreach (var dllRef in dllReferences)
        {
            if (ScriptProjectAssemblyClassifier.Classify(dllRef) == ScriptAssemblyCategory.UnityEngine
                && dllRef.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)
                && !refs.Contains(dllRef, StringComparer.OrdinalIgnoreCase))
            {
                refs.Add(dllRef);
            }
        }
        refs.Sort(StringComparer.OrdinalIgnoreCase);

        var payload = new
        {
            name = assemblyName,
            rootNamespace = assemblyName,
            references = refs,
            includePlatforms = Array.Empty<string>(),
            excludePlatforms = Array.Empty<string>(),
            allowUnsafeCode = true,
            overrideReferences = false,
            precompiledReferences = Array.Empty<string>(),
            autoReferenced = true,
            defineConstraints = Array.Empty<string>(),
            versionDefines = Array.Empty<string>(),
            noEngineReferences = false
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, List<ScriptMethodSymbol>> IndexScriptJsonByType(
        string scriptJsonPath,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<ScriptMethodSymbol>>(StringComparer.OrdinalIgnoreCase);
        Il2CppDumpScriptService.StreamScriptJson(
            scriptJsonPath,
            onMethod: entry =>
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    return;
                }

                var sep = entry.Name.IndexOf("$$", StringComparison.Ordinal);
                if (sep <= 0)
                {
                    return;
                }

                var typeKey = entry.Name.Substring(0, sep);
                var methodName = entry.Name.Substring(sep + 2);
                if (!map.TryGetValue(typeKey, out var list))
                {
                    list = new List<ScriptMethodSymbol>(4);
                    map[typeKey] = list;
                }

                list.Add(new ScriptMethodSymbol
                {
                    ClassName = typeKey,
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
        return map;
    }

    private static string FormatDummyDllSource(
        string assemblyName,
        string fullName,
        string ns,
        string className,
        string decompiledCode,
        Dictionary<string, List<ScriptMethodSymbol>>? scriptJsonByType)
    {
        var sb = new StringBuilder(decompiledCode.Length + 256);
        sb.AppendLine($"// IL2CPP DummyDll stub — Assembly: {assemblyName} | Type: {fullName}");
        sb.AppendLine("// Method bodies are compiled to native code; see RVA comments below if script.json was provided.");
        sb.AppendLine();
        sb.Append(decompiledCode.TrimEnd());
        return AppendScriptJsonRvaComments(sb.ToString(), ns, className, scriptJsonByType);
    }

    private static string FormatIl2CppDumpStub(
        string assemblyName,
        string ns,
        string className,
        string rawBlock,
        Dictionary<string, List<ScriptMethodSymbol>>? scriptJsonByType)
    {
        var fullName = string.IsNullOrWhiteSpace(ns) ? className : $"{ns}.{className}";
        var sb = new StringBuilder(rawBlock.Length + 256);
        sb.AppendLine($"// IL2CPP dump.cs stub — Assembly: {assemblyName} | Type: {fullName}");
        if (!string.IsNullOrWhiteSpace(ns))
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            using var sr = new StringReader(rawBlock.TrimEnd());
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                sb.Append("    ");
                sb.AppendLine(line);
            }
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine(rawBlock.TrimEnd());
        }

        return AppendScriptJsonRvaComments(sb.ToString().TrimEnd(), ns, className, scriptJsonByType);
    }

    private static string BuildSimpleIl2CppStub(string assemblyName, string ns, string className, AssemblyTypeKind kind)
    {
        var keyword = kind switch
        {
            AssemblyTypeKind.Struct => "struct",
            AssemblyTypeKind.Interface => "interface",
            AssemblyTypeKind.Enum => "enum",
            _ => "class"
        };
        var sb = new StringBuilder();
        sb.AppendLine($"// IL2CPP global-metadata.dat stub — Assembly: {assemblyName}");
        if (!string.IsNullOrWhiteSpace(ns))
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial {keyword} {className}");
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine($"public partial {keyword} {className}");
            sb.AppendLine("{");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string AppendScriptJsonRvaComments(
        string baseSource,
        string ns,
        string className,
        Dictionary<string, List<ScriptMethodSymbol>>? scriptJsonByType)
    {
        if (scriptJsonByType == null || scriptJsonByType.Count == 0)
        {
            return baseSource;
        }

        var fullKey = string.IsNullOrWhiteSpace(ns) ? className : $"{ns}.{className}";
        if (!scriptJsonByType.TryGetValue(fullKey, out var methods)
            && !scriptJsonByType.TryGetValue(className, out methods))
        {
            return baseSource;
        }

        if (methods == null || methods.Count == 0)
        {
            return baseSource;
        }

        var sb = new StringBuilder(baseSource.Length + methods.Count * 80);
        sb.AppendLine(baseSource.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("// --- script.json RVA Map ---");
        foreach (var sym in methods.Take(250))
        {
            var sig = string.IsNullOrWhiteSpace(sym.Signature) ? string.Empty : $" | {sym.Signature}";
            sb.AppendLine($"// RVA: 0x{sym.Address:X8} {sym.FullName}{sig}");
        }
        return sb.ToString();
    }

    private static string BuildFailedTypeStub(string assemblyName, string ns, string className, string errorMessage)
    {
        var fullName = string.IsNullOrWhiteSpace(ns) ? className : $"{ns}.{className}";
        var safeError = (errorMessage ?? "Unknown error").Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        sb.AppendLine($"// Decompilation failed for {assemblyName} :: {fullName}");
        sb.AppendLine($"// Reason: {safeError}");
        sb.AppendLine("// Emitted as empty comment stub to preserve project export integrity.");
        return sb.ToString();
    }

    private static void TrackDirectoriesForMeta(string asmOutDir, string leafDir, HashSet<string> createdDirs)
    {
        var current = Path.GetFullPath(leafDir);
        var root = Path.GetFullPath(asmOutDir);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!createdDirs.Add(current))
            {
                break;
            }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }
    }

    private static void WriteFolderMetaFiles(string outputRoot, string asmOutDir, HashSet<string> createdDirs)
    {
        createdDirs.Add(Path.GetFullPath(asmOutDir));
        foreach (var dir in createdDirs)
        {
            var metaPath = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".meta";
            var relKey = Path.GetRelativePath(outputRoot, dir).Replace('\\', '/');
            var guid = UnityProjectGuidUtility.ComputeAssetGuid("folder", null, 0, relKey);
            File.WriteAllText(metaPath, UnityProjectGuidUtility.BuildFolderMetaContent(guid), Utf8NoBom);
        }
    }

    private static void WriteExportReportJson(CSharpProjectExportResult result)
    {
        var sortedGuidMap = new SortedDictionary<string, string>(result.ScriptGuidMap, StringComparer.OrdinalIgnoreCase);
        var reportPayload = new
        {
            mode = result.Mode,
            sourceMode = result.SourceMode.ToString(),
            elapsedMs = result.ElapsedMs,
            gameAssembliesCount = result.GameAssembliesCount,
            referenceAssembliesCount = result.ReferenceAssembliesCount,
            totalTypesExported = result.TotalTypesExported,
            failedTypesCount = result.FailedTypesCount,
            solutionPath = result.SolutionPath,
            brokenCycles = result.BrokenCycles,
            unresolvedReferences = result.UnresolvedReferences,
            failedTypes = result.FailedTypes,
            assemblies = result.Assemblies,
            scriptGuidMap = sortedGuidMap
        };

        var json = JsonSerializer.Serialize(reportPayload, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });
        File.WriteAllText(result.ReportPath, json, Utf8NoBom);
    }

    private static string EscapeXml(string value)
    {
        return SecurityElement.Escape(value) ?? value;
    }

    private static CSharpProjectExportResult Fail(string errorMessage, long elapsedMs)
    {
        return new CSharpProjectExportResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            Mode = "Failed",
            ElapsedMs = elapsedMs
        };
    }
}
