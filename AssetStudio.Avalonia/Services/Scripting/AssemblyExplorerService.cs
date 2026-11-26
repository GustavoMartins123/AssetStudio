using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services.Scripting;

public enum AssemblyTypeKind
{
    Class = 0,
    Struct = 1,
    Interface = 2,
    Enum = 3,
    Delegate = 4
}

public sealed class AssemblyTypeEntry
{
    public string AssemblyName { get; init; } = string.Empty;
    public string AssemblyFileName { get; init; } = string.Empty;
    public string AssemblyPath { get; init; } = string.Empty;
    public bool IsGameAssembly { get; init; }
    public string Namespace { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public AssemblyTypeKind Kind { get; init; }
    public bool IsMonoBehaviour { get; init; }
    public bool IsScriptableObject { get; init; }
    public string BaseTypeName { get; init; } = string.Empty;
    private string? _scriptGuid;

    public int MethodCount { get; init; }
    public int FieldCount { get; init; }
    public string ScriptGuid
    {
        get => _scriptGuid ??= UnityProjectGuidUtility.ComputeScriptGuid(AssemblyName, Namespace, ClassName);
        init => _scriptGuid = string.IsNullOrWhiteSpace(value) ? null : value;
    }
    public ScriptSourceKind SourceKind { get; init; }

    public string KindBadge => IsMonoBehaviour
        ? "MB"
        : IsScriptableObject
            ? "SO"
            : Kind switch
            {
                AssemblyTypeKind.Struct => "S",
                AssemblyTypeKind.Interface => "I",
                AssemblyTypeKind.Enum => "E",
                AssemblyTypeKind.Delegate => "D",
                _ => "C"
            };

    public string DisplayName => string.IsNullOrWhiteSpace(ClassName) ? FullName : ClassName;

    public string SubtitleLine
    {
        get
        {
            var nsPart = string.IsNullOrWhiteSpace(Namespace) ? "(global)" : Namespace;
            if (MethodCount > 0 || FieldCount > 0)
            {
                return $"{nsPart} • {AssemblyName} • {FieldCount}f/{MethodCount}m";
            }

            return $"{nsPart} • {AssemblyName}";
        }
    }
}

public sealed class AssemblyInfoEntry
{
    public string AssemblyName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public ScriptAssemblyCategory Category { get; init; }
    public bool IsGameAssembly => Category == ScriptAssemblyCategory.Game;
    public long FileSizeBytes { get; init; }
    public int TypeCount { get; init; }
    public IReadOnlyList<string> ReferencedAssemblies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AssemblyTypeEntry> Types { get; init; } = Array.Empty<AssemblyTypeEntry>();
    public ScriptSourceKind SourceKind { get; init; }

    public string DisplayLabel => string.Create(
        CultureInfo.InvariantCulture,
        $"{AssemblyName} ({TypeCount:N0})");
}

public sealed record AssemblyFilterResult(
    IReadOnlyList<AssemblyTypeEntry> Items,
    int TotalMatchCount,
    bool WasTruncated);

public sealed class AssemblyCatalogSnapshot
{
    public static readonly AssemblyCatalogSnapshot Empty = new();

    public IReadOnlyList<AssemblyInfoEntry> Assemblies { get; init; } = Array.Empty<AssemblyInfoEntry>();
    public IReadOnlyList<AssemblyTypeEntry> AllTypes { get; init; } = Array.Empty<AssemblyTypeEntry>();
    public string SourceDescription { get; init; } = "No script assemblies detected.";
    public long ScanElapsedMs { get; init; }
    public ScriptSourceKind SourceKind { get; init; } = ScriptSourceKind.None;

    public bool HasAnyTypes => AllTypes.Count > 0;
    public bool HasAnyAssemblies => Assemblies.Count > 0;

    public AssemblyFilterResult Filter(
        string? query,
        bool showEngineAssemblies = false,
        string? assemblyShortNameFilter = null,
        int maxResults = 10_000)
    {
        if (AllTypes.Count == 0)
        {
            return new AssemblyFilterResult(Array.Empty<AssemblyTypeEntry>(), 0, false);
        }

        var needle = query?.Trim() ?? string.Empty;
        var hasQuery = needle.Length > 0;
        var asmFilter = string.IsNullOrWhiteSpace(assemblyShortNameFilter)
            ? null
            : UnityProjectGuidUtility.NormalizeAssemblyShortName(assemblyShortNameFilter);
        var cap = Math.Max(1, maxResults);

        var matched = new List<AssemblyTypeEntry>(Math.Min(AllTypes.Count, 256));
        var totalMatches = 0;

        for (var i = 0; i < AllTypes.Count; i++)
        {
            var entry = AllTypes[i];
            if (!showEngineAssemblies && !entry.IsGameAssembly)
            {
                continue;
            }

            if (asmFilter != null && !string.Equals(entry.AssemblyName, asmFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hasQuery && !MatchesQuery(entry, needle))
            {
                continue;
            }

            totalMatches++;
            if (matched.Count < cap)
            {
                matched.Add(entry);
            }
        }

        return new AssemblyFilterResult(matched, totalMatches, totalMatches > matched.Count);
    }

    private static bool MatchesQuery(AssemblyTypeEntry entry, string needle)
    {
        return entry.ClassName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Namespace.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.AssemblyName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.BaseTypeName.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Metadata-only fast assembly/type catalog scanner (SCRIPT-01).
/// Uses System.Reflection.PortableExecutable.PEReader + System.Reflection.Metadata.MetadataReader
/// exclusively during scanning (never instantiates CSharpDecompiler) so 100+ DLLs scan in &lt; 100 ms.
/// </summary>
public sealed class AssemblyExplorerService
{
    public AssemblyCatalogSnapshot ScanDiscoveredSources(
        DiscoveredScriptSources discovered,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        if (discovered.ManagedDirectories.Count > 0)
        {
            var managed = ScanDirectories(
                discovered.ManagedDirectories,
                isDummyDll: false,
                cancellationToken: cancellationToken);
            if (managed.HasAnyTypes)
            {
                return managed;
            }
        }

        if (discovered.DummyDllDirectories.Count > 0)
        {
            var dummy = ScanDirectories(
                discovered.DummyDllDirectories,
                isDummyDll: true,
                cancellationToken: cancellationToken);
            if (dummy.HasAnyTypes)
            {
                return dummy;
            }
        }

        if (!string.IsNullOrWhiteSpace(discovered.DumpCsPath) && File.Exists(discovered.DumpCsPath))
        {
            var dumpCatalog = ScanDumpCs(discovered.DumpCsPath, cancellationToken);
            if (dumpCatalog.HasAnyTypes)
            {
                return dumpCatalog;
            }
        }

        if (!string.IsNullOrWhiteSpace(discovered.GlobalMetadataPath) && File.Exists(discovered.GlobalMetadataPath))
        {
            var metadataCatalog = ScanGlobalMetadata(discovered.GlobalMetadataPath, cancellationToken);
            if (metadataCatalog.HasAnyTypes)
            {
                return metadataCatalog;
            }
        }

        return AssemblyCatalogSnapshot.Empty;
    }

    public AssemblyCatalogSnapshot ScanDirectories(
        IReadOnlyList<string> directories,
        bool isDummyDll = false,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (directories == null || directories.Count == 0)
        {
            return AssemblyCatalogSnapshot.Empty;
        }

        var dllFiles = new List<string>();
        var seenShortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var shortName = UnityProjectGuidUtility.NormalizeAssemblyShortName(file);
                    if (!string.IsNullOrWhiteSpace(shortName) && seenShortNames.Add(shortName))
                    {
                        dllFiles.Add(Path.GetFullPath(file));
                    }
                }
            }
            catch
            {
            }
        }

        if (dllFiles.Count == 0)
        {
            return AssemblyCatalogSnapshot.Empty;
        }

        var sourceKind = isDummyDll
            ? ScriptSourceKind.Il2CppDummyAssemblyDecompiled
            : ScriptSourceKind.MonoAssemblyDecompiled;

        var assembliesBag = new ConcurrentBag<AssemblyInfoEntry>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken
        };

        Parallel.ForEach(dllFiles, parallelOptions, dllPath =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = InspectManagedAssemblyMetadataOnly(dllPath, sourceKind, cancellationToken);
            if (info != null)
            {
                assembliesBag.Add(info);
            }
        });

        var assemblies = assembliesBag.ToList();
        var totalTypes = assemblies.Sum(static a => a.Types.Count);
        var allTypes = new List<AssemblyTypeEntry>(totalTypes);
        for (var i = 0; i < assemblies.Count; i++)
        {
            if (assemblies[i].Types.Count > 0)
            {
                allTypes.AddRange(assemblies[i].Types);
            }
        }

        assemblies.Sort(CompareAssemblies);
        allTypes.Sort(CompareTypes);
        sw.Stop();

        var modeLabel = isDummyDll ? "IL2CPP DummyDll" : "Managed";
        var gameCount = assemblies.Count(static a => a.IsGameAssembly);
        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"{modeLabel} • {assemblies.Count:N0} assemblies ({gameCount:N0} game) • {allTypes.Count:N0} types • {sw.ElapsedMilliseconds} ms");

        return new AssemblyCatalogSnapshot
        {
            Assemblies = assemblies,
            AllTypes = allTypes,
            SourceDescription = description,
            ScanElapsedMs = sw.ElapsedMilliseconds,
            SourceKind = sourceKind
        };
    }

    public AssemblyCatalogSnapshot ScanDumpCs(string dumpCsPath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(dumpCsPath) || !File.Exists(dumpCsPath))
        {
            return AssemblyCatalogSnapshot.Empty;
        }

        var parsed = Il2CppDumpCatalogHelper.ParseCatalog(dumpCsPath, cancellationToken);
        if (parsed.Count == 0)
        {
            return AssemblyCatalogSnapshot.Empty;
        }

        var fullPath = Path.GetFullPath(dumpCsPath);
        var fileSize = new FileInfo(fullPath).Length;
        var byAssembly = new Dictionary<string, List<AssemblyTypeEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in parsed)
        {
            var asmShort = UnityProjectGuidUtility.NormalizeAssemblyShortName(item.AssemblyName);
            if (string.IsNullOrWhiteSpace(asmShort))
            {
                asmShort = "Assembly-CSharp";
            }

            var isGame = ScriptProjectAssemblyClassifier.IsGameAssembly(asmShort);
            var fullName = string.IsNullOrWhiteSpace(item.Namespace)
                ? item.ClassName
                : $"{item.Namespace}.{item.ClassName}";

            var entry = new AssemblyTypeEntry
            {
                AssemblyName = asmShort,
                AssemblyFileName = asmShort + ".dll",
                AssemblyPath = fullPath,
                IsGameAssembly = isGame,
                Namespace = item.Namespace,
                ClassName = item.ClassName,
                FullName = fullName,
                Kind = item.Kind,
                IsMonoBehaviour = IsKnownMonoBehaviourBase(item.BaseTypeName),
                IsScriptableObject = IsKnownScriptableObjectBase(item.BaseTypeName),
                BaseTypeName = item.BaseTypeName,
                MethodCount = item.MethodCount,
                FieldCount = item.FieldCount,
                ScriptGuid = UnityProjectGuidUtility.ComputeScriptGuid(asmShort, item.Namespace, item.ClassName),
                SourceKind = ScriptSourceKind.Il2CppDumpCs
            };

            if (!byAssembly.TryGetValue(asmShort, out var list))
            {
                list = new List<AssemblyTypeEntry>();
                byAssembly[asmShort] = list;
            }
            list.Add(entry);
        }

        var assemblies = new List<AssemblyInfoEntry>(byAssembly.Count);
        var allTypes = new List<AssemblyTypeEntry>(parsed.Count);
        foreach (var kv in byAssembly)
        {
            kv.Value.Sort(CompareTypes);
            assemblies.Add(new AssemblyInfoEntry
            {
                AssemblyName = kv.Key,
                FileName = kv.Key + ".dll",
                FilePath = fullPath,
                Category = ScriptProjectAssemblyClassifier.Classify(kv.Key),
                FileSizeBytes = fileSize,
                TypeCount = kv.Value.Count,
                ReferencedAssemblies = Array.Empty<string>(),
                Types = kv.Value,
                SourceKind = ScriptSourceKind.Il2CppDumpCs
            });
            allTypes.AddRange(kv.Value);
        }

        assemblies.Sort(CompareAssemblies);
        allTypes.Sort(CompareTypes);
        sw.Stop();

        var gameCount = assemblies.Count(static a => a.IsGameAssembly);
        return new AssemblyCatalogSnapshot
        {
            Assemblies = assemblies,
            AllTypes = allTypes,
            SourceDescription = string.Create(
                CultureInfo.InvariantCulture,
                $"IL2CPP dump.cs • {assemblies.Count:N0} assemblies ({gameCount:N0} game) • {allTypes.Count:N0} types • {sw.ElapsedMilliseconds} ms"),
            ScanElapsedMs = sw.ElapsedMilliseconds,
            SourceKind = ScriptSourceKind.Il2CppDumpCs
        };
    }

    public AssemblyCatalogSnapshot ScanGlobalMetadata(string globalMetadataPath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(globalMetadataPath) || !File.Exists(globalMetadataPath))
        {
            return AssemblyCatalogSnapshot.Empty;
        }

        var catalog = Il2CppMetadataCatalogHelper.TryReadImagesAndTypes(globalMetadataPath, cancellationToken);
        if (catalog == null || catalog.Images.Count == 0)
        {
            return AssemblyCatalogSnapshot.Empty;
        }

        var fullPath = Path.GetFullPath(globalMetadataPath);
        var fileSize = new FileInfo(fullPath).Length;
        var assemblies = new List<AssemblyInfoEntry>(catalog.Images.Count);
        var allTypes = new List<AssemblyTypeEntry>(catalog.TotalTypes);

        foreach (var image in catalog.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asmShort = UnityProjectGuidUtility.NormalizeAssemblyShortName(image.ImageName);
            if (string.IsNullOrWhiteSpace(asmShort))
            {
                continue;
            }

            var category = ScriptProjectAssemblyClassifier.Classify(asmShort);
            var isGame = category == ScriptAssemblyCategory.Game;
            var types = new List<AssemblyTypeEntry>(image.Types.Count);

            foreach (var rawType in image.Types)
            {
                if (IsCompilerGeneratedTopLevelTypeName(rawType.ClassName))
                {
                    continue;
                }

                var fullName = string.IsNullOrWhiteSpace(rawType.Namespace)
                    ? rawType.ClassName
                    : $"{rawType.Namespace}.{rawType.ClassName}";

                types.Add(new AssemblyTypeEntry
                {
                    AssemblyName = asmShort,
                    AssemblyFileName = image.ImageName,
                    AssemblyPath = fullPath,
                    IsGameAssembly = isGame,
                    Namespace = rawType.Namespace,
                    ClassName = rawType.ClassName,
                    FullName = fullName,
                    Kind = rawType.Kind,
                    IsMonoBehaviour = false,
                    IsScriptableObject = false,
                    BaseTypeName = string.Empty,
                    MethodCount = rawType.MethodCount,
                    FieldCount = rawType.FieldCount,
                    ScriptGuid = UnityProjectGuidUtility.ComputeScriptGuid(asmShort, rawType.Namespace, rawType.ClassName),
                    SourceKind = ScriptSourceKind.Il2CppGlobalMetadata
                });
            }

            types.Sort(CompareTypes);
            assemblies.Add(new AssemblyInfoEntry
            {
                AssemblyName = asmShort,
                FileName = image.ImageName,
                FilePath = fullPath,
                Category = category,
                FileSizeBytes = fileSize,
                TypeCount = types.Count,
                ReferencedAssemblies = Array.Empty<string>(),
                Types = types,
                SourceKind = ScriptSourceKind.Il2CppGlobalMetadata
            });
            allTypes.AddRange(types);
        }

        assemblies.Sort(CompareAssemblies);
        allTypes.Sort(CompareTypes);
        sw.Stop();

        var gameCount = assemblies.Count(static a => a.IsGameAssembly);
        return new AssemblyCatalogSnapshot
        {
            Assemblies = assemblies,
            AllTypes = allTypes,
            SourceDescription = string.Create(
                CultureInfo.InvariantCulture,
                $"IL2CPP global-metadata.dat (v{catalog.Version}) • {assemblies.Count:N0} assemblies ({gameCount:N0} game) • {allTypes.Count:N0} types • {sw.ElapsedMilliseconds} ms"),
            ScanElapsedMs = sw.ElapsedMilliseconds,
            SourceKind = ScriptSourceKind.Il2CppGlobalMetadata
        };
    }

    internal static AssemblyInfoEntry? InspectManagedAssemblyMetadataOnly(
        string dllPath,
        ScriptSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(dllPath);
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length < 64)
            {
                return null;
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
            var asmShort = md.GetString(asmDef.Name);
            if (string.IsNullOrWhiteSpace(asmShort))
            {
                asmShort = UnityProjectGuidUtility.NormalizeAssemblyShortName(fullPath);
            }

            var fileName = Path.GetFileName(fullPath);
            var category = ScriptProjectAssemblyClassifier.Classify(asmShort);
            var isGame = category == ScriptAssemblyCategory.Game;

            var references = new List<string>(md.AssemblyReferences.Count);
            var refSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var refHandle in md.AssemblyReferences)
            {
                var aref = md.GetAssemblyReference(refHandle);
                var refName = md.GetString(aref.Name);
                if (!string.IsNullOrWhiteSpace(refName) && refSeen.Add(refName))
                {
                    references.Add(refName);
                }
            }

            // First pass: collect top-level non-compiler-generated types and base links
            var rawCount = md.TypeDefinitions.Count;
            var rawTypes = new List<RawMetadataType>(Math.Max(8, rawCount));
            var baseFullNameByTypeFullName = new Dictionary<string, string>(Math.Max(8, rawCount), StringComparer.Ordinal);

            var idx = 0;
            foreach (var tdHandle in md.TypeDefinitions)
            {
                if ((idx++ & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var td = md.GetTypeDefinition(tdHandle);
                if (td.IsNested)
                {
                    continue;
                }

                var className = md.GetString(td.Name);
                if (string.IsNullOrWhiteSpace(className) || IsCompilerGeneratedTopLevelTypeName(className))
                {
                    continue;
                }

                var ns = md.GetString(td.Namespace) ?? string.Empty;
                var fullName = string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";

                var (baseNs, baseName) = ResolveBaseTypeName(md, td.BaseType);
                var baseFull = string.IsNullOrEmpty(baseNs) ? baseName : $"{baseNs}.{baseName}";

                var kind = ClassifyTypeKind(td.Attributes, baseNs, baseName);
                if (!string.IsNullOrEmpty(baseFull))
                {
                    baseFullNameByTypeFullName[fullName] = baseFull;
                }

                rawTypes.Add(new RawMetadataType(
                    Namespace: ns,
                    ClassName: className,
                    FullName: fullName,
                    Kind: kind,
                    BaseName: baseName,
                    BaseFullName: baseFull,
                    MethodCount: td.GetMethods().Count,
                    FieldCount: td.GetFields().Count));
            }

            // Second pass: fast bounded inheritance propagation for MonoBehaviour / ScriptableObject
            var types = new List<AssemblyTypeEntry>(rawTypes.Count);
            for (var i = 0; i < rawTypes.Count; i++)
            {
                var rt = rawTypes[i];
                var (isMb, isSo) = ResolveUnityObjectInheritance(rt.BaseName, rt.BaseFullName, baseFullNameByTypeFullName);
                types.Add(new AssemblyTypeEntry
                {
                    AssemblyName = asmShort,
                    AssemblyFileName = fileName,
                    AssemblyPath = fullPath,
                    IsGameAssembly = isGame,
                    Namespace = rt.Namespace,
                    ClassName = rt.ClassName,
                    FullName = rt.FullName,
                    Kind = rt.Kind,
                    IsMonoBehaviour = isMb,
                    IsScriptableObject = isSo,
                    BaseTypeName = rt.BaseName,
                    MethodCount = rt.MethodCount,
                    FieldCount = rt.FieldCount,
                    SourceKind = sourceKind
                });
            }

            types.Sort(CompareTypes);

            return new AssemblyInfoEntry
            {
                AssemblyName = asmShort,
                FileName = fileName,
                FilePath = fullPath,
                Category = category,
                FileSizeBytes = fileInfo.Length,
                TypeCount = types.Count,
                ReferencedAssemblies = references,
                Types = types,
                SourceKind = sourceKind
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

    internal static bool IsCompilerGeneratedTopLevelTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return true;
        }

        if (typeName.Equals("<Module>", StringComparison.Ordinal)
            || typeName.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)
            || typeName.StartsWith("<>", StringComparison.Ordinal)
            || typeName.StartsWith("$_", StringComparison.Ordinal)
            || typeName.StartsWith("$ArrayType", StringComparison.Ordinal)
            || typeName.StartsWith("$BurstDirectCallInitializer", StringComparison.Ordinal)
            || typeName.StartsWith("$BurstDirectCallWrapper", StringComparison.Ordinal)
            || typeName.StartsWith("__StaticArrayInitTypeSize", StringComparison.Ordinal)
            || typeName.StartsWith("__JobReflectionRegistrationOutput__", StringComparison.Ordinal)
            || typeName.StartsWith("UnitySourceGeneratedAssemblyMonoScriptTypes", StringComparison.Ordinal)
            || typeName.Contains('<', StringComparison.Ordinal)
            || typeName.Contains('>', StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private readonly record struct RawMetadataType(
        string Namespace,
        string ClassName,
        string FullName,
        AssemblyTypeKind Kind,
        string BaseName,
        string BaseFullName,
        int MethodCount,
        int FieldCount);

    private static (string BaseNamespace, string BaseName) ResolveBaseTypeName(MetadataReader md, EntityHandle baseHandle)
    {
        if (baseHandle.IsNil)
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            switch (baseHandle.Kind)
            {
                case HandleKind.TypeReference:
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)baseHandle);
                    return (md.GetString(tr.Namespace) ?? string.Empty, md.GetString(tr.Name) ?? string.Empty);
                }
                case HandleKind.TypeDefinition:
                {
                    var td = md.GetTypeDefinition((TypeDefinitionHandle)baseHandle);
                    return (md.GetString(td.Namespace) ?? string.Empty, md.GetString(td.Name) ?? string.Empty);
                }
                default:
                    return (string.Empty, string.Empty);
            }
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static AssemblyTypeKind ClassifyTypeKind(TypeAttributes attributes, string baseNs, string baseName)
    {
        if ((attributes & TypeAttributes.Interface) != 0)
        {
            return AssemblyTypeKind.Interface;
        }

        if (string.Equals(baseNs, "System", StringComparison.Ordinal))
        {
            if (string.Equals(baseName, "Enum", StringComparison.Ordinal))
            {
                return AssemblyTypeKind.Enum;
            }
            if (string.Equals(baseName, "ValueType", StringComparison.Ordinal))
            {
                return AssemblyTypeKind.Struct;
            }
            if (string.Equals(baseName, "MulticastDelegate", StringComparison.Ordinal)
                || string.Equals(baseName, "Delegate", StringComparison.Ordinal))
            {
                return AssemblyTypeKind.Delegate;
            }
        }

        return AssemblyTypeKind.Class;
    }

    private static (bool IsMonoBehaviour, bool IsScriptableObject) ResolveUnityObjectInheritance(
        string directBaseName,
        string directBaseFullName,
        Dictionary<string, string> baseMap)
    {
        if (string.IsNullOrEmpty(directBaseName))
        {
            return (false, false);
        }

        if (IsKnownMonoBehaviourBase(directBaseName))
        {
            return (true, false);
        }

        if (IsKnownScriptableObjectBase(directBaseName))
        {
            return (false, true);
        }

        var current = directBaseFullName;
        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
        {
            if (!baseMap.TryGetValue(current, out var parentFull))
            {
                break;
            }

            var dot = parentFull.LastIndexOf('.');
            var parentShort = dot >= 0 ? parentFull.Substring(dot + 1) : parentFull;
            if (IsKnownMonoBehaviourBase(parentShort))
            {
                return (true, false);
            }
            if (IsKnownScriptableObjectBase(parentShort))
            {
                return (false, true);
            }

            current = parentFull;
        }

        return (false, false);
    }

    private static bool IsKnownMonoBehaviourBase(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        var clean = baseName.Trim();
        var dot = clean.LastIndexOf('.');
        if (dot >= 0)
        {
            clean = clean.Substring(dot + 1);
        }
        var tick = clean.IndexOfAny(new[] { '`', '<' });
        if (tick > 0)
        {
            clean = clean.Substring(0, tick);
        }

        return clean.Equals("MonoBehaviour", StringComparison.Ordinal)
            || clean.Equals("NetworkBehaviour", StringComparison.Ordinal)
            || clean.Equals("StateMachineBehaviour", StringComparison.Ordinal)
            || clean.Equals("UIBehaviour", StringComparison.Ordinal);
    }

    private static bool IsKnownScriptableObjectBase(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        var clean = baseName.Trim();
        var dot = clean.LastIndexOf('.');
        if (dot >= 0)
        {
            clean = clean.Substring(dot + 1);
        }
        var tick = clean.IndexOfAny(new[] { '`', '<' });
        if (tick > 0)
        {
            clean = clean.Substring(0, tick);
        }

        return clean.Equals("ScriptableObject", StringComparison.Ordinal);
    }

    private static int CompareAssemblies(AssemblyInfoEntry a, AssemblyInfoEntry b)
    {
        var gameCmp = (a.IsGameAssembly ? 0 : 1).CompareTo(b.IsGameAssembly ? 0 : 1);
        if (gameCmp != 0)
        {
            return gameCmp;
        }

        var primaryA = a.AssemblyName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        var primaryB = b.AssemblyName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        if (primaryA != primaryB)
        {
            return primaryA.CompareTo(primaryB);
        }

        return StringComparer.OrdinalIgnoreCase.Compare(a.AssemblyName, b.AssemblyName);
    }

    private static int CompareTypes(AssemblyTypeEntry a, AssemblyTypeEntry b)
    {
        var gameCmp = (a.IsGameAssembly ? 0 : 1).CompareTo(b.IsGameAssembly ? 0 : 1);
        if (gameCmp != 0)
        {
            return gameCmp;
        }

        var asmCmp = StringComparer.OrdinalIgnoreCase.Compare(a.AssemblyName, b.AssemblyName);
        if (asmCmp != 0)
        {
            return asmCmp;
        }

        var nsCmp = StringComparer.OrdinalIgnoreCase.Compare(a.Namespace, b.Namespace);
        if (nsCmp != 0)
        {
            return nsCmp;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(a.ClassName, b.ClassName);
    }
}

#region Lightweight IL2CPP Catalog Helpers (dump.cs & global-metadata.dat)

internal sealed record DumpCsParsedType(
    string AssemblyName,
    string Namespace,
    string ClassName,
    AssemblyTypeKind Kind,
    string BaseTypeName,
    int FieldCount,
    int MethodCount,
    string SourceBlock);

internal static class Il2CppDumpCatalogHelper
{
    private static readonly Regex ImageHeaderRegex = new(
        @"^//\s*Image\s+\d+:\s*(?<name>.+?)(?:\s+-\s+\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypeDeclRegex = new(
        @"^(?:public|internal|private|protected|\s|sealed|abstract|static|partial|unsafe|readonly|ref)*\b(?<kind>class|struct|interface|enum|delegate)\s+(?:[^\s(]+\s+)?(?<name>[A-Za-z_@][A-Za-z0-9_`<>,.\s]*?)(?:\s*:\s*(?<base>[A-Za-z0-9_.<>,\s]+?))?(?:\s*//\s*TypeDefIndex:\s*\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<DumpCsParsedType> ParseCatalog(
        string dumpCsPath,
        CancellationToken cancellationToken,
        bool captureSourceBlocks = false)
    {
        var results = new List<DumpCsParsedType>();
        if (string.IsNullOrWhiteSpace(dumpCsPath) || !File.Exists(dumpCsPath))
        {
            return results;
        }

        var imageName = "Assembly-CSharp";
        var currentNamespace = string.Empty;
        var pendingAttributes = new List<string>(4);

        using var stream = new FileStream(dumpCsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);

        string? line;
        var lineCounter = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if ((lineCounter++ & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                pendingAttributes.Clear();
                continue;
            }

            if (trimmed.StartsWith("// Image ", StringComparison.Ordinal))
            {
                var imgMatch = ImageHeaderRegex.Match(trimmed);
                if (imgMatch.Success)
                {
                    var parsedImg = imgMatch.Groups["name"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(parsedImg))
                    {
                        imageName = UnityProjectGuidUtility.NormalizeAssemblyShortName(parsedImg);
                    }
                }
                pendingAttributes.Clear();
                continue;
            }

            if (trimmed.StartsWith("// Namespace:", StringComparison.Ordinal))
            {
                currentNamespace = trimmed.Substring("// Namespace:".Length).Trim();
                pendingAttributes.Clear();
                continue;
            }

            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                pendingAttributes.Add(line);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // Detect top-level type declaration (not indented)
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                var match = TypeDeclRegex.Match(trimmed);
                if (match.Success)
                {
                    var kindToken = match.Groups["kind"].Value;
                    var rawName = match.Groups["name"].Value.Trim();
                    // Trim generic parameter list or delegate parens for clean class name
                    var paren = rawName.IndexOf('(');
                    if (paren > 0)
                    {
                        rawName = rawName.Substring(0, paren).Trim();
                    }
                    var angle = rawName.IndexOf('<');
                    if (angle > 0)
                    {
                        rawName = rawName.Substring(0, angle).Trim();
                    }

                    var rawBase = match.Groups["base"].Success ? match.Groups["base"].Value.Trim() : string.Empty;
                    var commaIdx = rawBase.IndexOf(',');
                    if (commaIdx > 0)
                    {
                        rawBase = rawBase.Substring(0, commaIdx).Trim();
                    }

                    var kind = kindToken switch
                    {
                        "struct" => AssemblyTypeKind.Struct,
                        "interface" => AssemblyTypeKind.Interface,
                        "enum" => AssemblyTypeKind.Enum,
                        "delegate" => AssemblyTypeKind.Delegate,
                        _ => AssemblyTypeKind.Class
                    };

                    StringBuilder? blockBuilder = captureSourceBlocks ? new StringBuilder(512) : null;
                    if (blockBuilder != null)
                    {
                        for (var a = 0; a < pendingAttributes.Count; a++)
                        {
                            blockBuilder.AppendLine(pendingAttributes[a]);
                        }
                        blockBuilder.AppendLine(line);
                    }

                    var braceDepth = CountNetBracesOutsideComments(line);
                    var sawOpeningBrace = line.Contains('{', StringComparison.Ordinal);
                    var fields = 0;
                    var methods = 0;
                    var inFieldsSection = false;
                    var inMethodsSection = false;

                    // Delegate single-line declaration ends with ';'
                    if (kind == AssemblyTypeKind.Delegate && trimmed.EndsWith(";", StringComparison.Ordinal) && !sawOpeningBrace)
                    {
                        if (!AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName(rawName))
                        {
                            results.Add(new DumpCsParsedType(
                                imageName,
                                currentNamespace,
                                rawName,
                                kind,
                                rawBase,
                                0,
                                0,
                                blockBuilder?.ToString() ?? string.Empty));
                        }
                        pendingAttributes.Clear();
                        continue;
                    }

                    // Consume body lines until closing top-level brace
                    while (true)
                    {
                        if (sawOpeningBrace && braceDepth <= 0)
                        {
                            break;
                        }

                        var bodyLine = reader.ReadLine();
                        if (bodyLine == null)
                        {
                            break;
                        }

                        blockBuilder?.AppendLine(bodyLine);
                        var bt = bodyLine.Trim();
                        if (bt.Equals("// Fields", StringComparison.Ordinal))
                        {
                            inFieldsSection = true;
                            inMethodsSection = false;
                        }
                        else if (bt.Equals("// Methods", StringComparison.Ordinal))
                        {
                            inFieldsSection = false;
                            inMethodsSection = true;
                        }
                        else if (bt.Equals("// Properties", StringComparison.Ordinal) || bt.Equals("// Events", StringComparison.Ordinal))
                        {
                            inFieldsSection = false;
                            inMethodsSection = false;
                        }
                        else if (bt.Length > 0 && !bt.StartsWith("//", StringComparison.Ordinal) && !bt.StartsWith("{", StringComparison.Ordinal) && !bt.StartsWith("}", StringComparison.Ordinal) && !bt.StartsWith("[", StringComparison.Ordinal))
                        {
                            if (inFieldsSection && bt.Contains(';', StringComparison.Ordinal))
                            {
                                fields++;
                            }
                            else if (inMethodsSection && bt.Contains('(', StringComparison.Ordinal))
                            {
                                methods++;
                            }
                        }

                        if (bodyLine.Contains('{', StringComparison.Ordinal))
                        {
                            sawOpeningBrace = true;
                        }
                        braceDepth += CountNetBracesOutsideComments(bodyLine);
                    }

                    if (!AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName(rawName))
                    {
                        results.Add(new DumpCsParsedType(
                            imageName,
                            currentNamespace,
                            rawName,
                            kind,
                            rawBase,
                            fields,
                            methods,
                            blockBuilder?.ToString() ?? string.Empty));
                    }

                    pendingAttributes.Clear();
                }
            }
        }

        return results;
    }

    private static int CountNetBracesOutsideComments(string line)
    {
        var net = 0;
        var inString = false;
        var inChar = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c == '{') net++;
            else if (c == '}') net--;
        }
        return net;
    }
}

internal sealed record MetadataCatalogType(
    string Namespace,
    string ClassName,
    AssemblyTypeKind Kind,
    int FieldCount,
    int MethodCount);

internal sealed record MetadataCatalogImage(
    string ImageName,
    IReadOnlyList<MetadataCatalogType> Types);

internal sealed record MetadataCatalogResult(
    int Version,
    IReadOnlyList<MetadataCatalogImage> Images,
    int TotalTypes);

/// <summary>
/// Lightweight header-and-table enumerator for Unity IL2CPP global-metadata.dat
/// (classic v24-31 and compact v39/v107) to populate AssemblyExplorer and IL2CPP project export
/// without touching Il2CppMetadataReader.cs.
/// </summary>
internal static class Il2CppMetadataCatalogHelper
{
    private const uint MetadataSanity = 0xFAB11BAF;

    public static MetadataCatalogResult? TryReadImagesAndTypes(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        try
        {
            // Prefer the full multi-version Il2CppMetadataReader (supports v24..v31 classic layouts,
            // shifted/obfuscated tables, and Unity 6000.3+ compact metadata v39/v107).
            var reader = new Il2CppMetadataReader();
            var fullCatalog = reader.TryReadCatalog(metadataPath, cancellationToken);
            if (fullCatalog != null && fullCatalog.Images.Count > 0)
            {
                return fullCatalog;
            }

            var info = new FileInfo(metadataPath);
            if (!info.Exists || info.Length < 208)
            {
                return null;
            }

            using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            var headerLen = (int)Math.Min(info.Length, 1024);
            var header = new byte[headerLen];
            stream.ReadExactly(header, 0, headerLen);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != MetadataSanity)
            {
                return null;
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (version >= 24 && version <= 31)
            {
                return TryReadClassic(stream, info.Length, header, version, cancellationToken);
            }

            return TryReadCompact(stream, info.Length, header, version, cancellationToken);
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

    private static MetadataCatalogResult? TryReadClassic(
        FileStream stream,
        long fileLength,
        byte[] header,
        int version,
        CancellationToken cancellationToken)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (headerSize < 200 || headerSize > header.Length || (headerSize - 8) % 8 != 0)
        {
            return null;
        }

        var pairCount = (headerSize - 8) / 8;
        if (pairCount < 21)
        {
            return null;
        }

        var strOff = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8 + 2 * 8, 4));
        var strSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12 + 2 * 8, 4));
        var tdOff = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8 + 19 * 8, 4));
        var tdSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12 + 19 * 8, 4));

        if (!IsSliceValid(strOff, strSize, fileLength) || !IsSliceValid(tdOff, tdSize, fileLength))
        {
            return null;
        }

        var stringBytes = ReadSlice(stream, strOff, strSize);
        var tdBytes = ReadSlice(stream, tdOff, tdSize);

        var tdStructSize = DetectTypeDefStructSize(tdBytes);
        if (tdStructSize <= 0)
        {
            return null;
        }

        var totalTypes = tdSize / tdStructSize;
        if (totalTypes <= 0)
        {
            return null;
        }

        // Probe pair 20 and pair 21 for Images table
        List<(string Name, int TypeStart, int TypeCount)>? parsedImages = null;
        foreach (var imgPairIdx in new[] { 20, 21 })
        {
            if (imgPairIdx >= pairCount)
            {
                continue;
            }

            var imgOff = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8 + imgPairIdx * 8, 4));
            var imgSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12 + imgPairIdx * 8, 4));
            if (!IsSliceValid(imgOff, imgSize, fileLength))
            {
                continue;
            }

            var imgBytes = ReadSlice(stream, imgOff, imgSize);
            parsedImages = TryParseImages(imgBytes, stringBytes, totalTypes);
            if (parsedImages != null && parsedImages.Count > 0)
            {
                break;
            }
        }

        if (parsedImages == null || parsedImages.Count == 0)
        {
            return null;
        }

        // Build per-image type lists
        var declaringOffset = version >= 27 ? 12 : 16;
        if (declaringOffset + 4 > tdStructSize)
        {
            declaringOffset = 12;
        }
        var methodCountOffset = tdStructSize - 24;
        var fieldCountOffset = tdStructSize - 20;

        var imagesOut = new List<MetadataCatalogImage>(parsedImages.Count);
        var totalAdded = 0;

        foreach (var (imgName, typeStart, typeCount) in parsedImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = new List<MetadataCatalogType>(Math.Min(typeCount, 1024));
            var end = Math.Min(totalTypes, typeStart + typeCount);
            for (var i = Math.Max(0, typeStart); i < end; i++)
            {
                var entry = tdBytes.AsSpan(i * tdStructSize, tdStructSize);
                var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
                var nsIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
                var declIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(declaringOffset, 4));
                if (declIdx >= 0)
                {
                    continue; // skip nested types
                }

                var className = ReadNullTerminatedUtf8(stringBytes, nameIdx);
                if (string.IsNullOrWhiteSpace(className) || AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName(className))
                {
                    continue;
                }

                var ns = ReadNullTerminatedUtf8(stringBytes, nsIdx) ?? string.Empty;
                var mCount = methodCountOffset >= 0 ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(methodCountOffset, 2)) : 0;
                var fCount = fieldCountOffset >= 0 ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(fieldCountOffset, 2)) : 0;
                var bitfield = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(tdStructSize - 8, 4));
                var kind = (bitfield & 0x2u) != 0
                    ? AssemblyTypeKind.Enum
                    : (bitfield & 0x1u) != 0
                        ? AssemblyTypeKind.Struct
                        : AssemblyTypeKind.Class;

                list.Add(new MetadataCatalogType(ns, className, kind, fCount, mCount));
                totalAdded++;
            }

            imagesOut.Add(new MetadataCatalogImage(imgName, list));
        }

        return new MetadataCatalogResult(version, imagesOut, totalAdded);
    }

    private static MetadataCatalogResult? TryReadCompact(
        FileStream stream,
        long fileLength,
        byte[] header,
        int version,
        CancellationToken cancellationToken)
    {
        if (header.Length < 12)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (headerSize < 8 + 31 * 12 || headerSize > header.Length || (headerSize - 8) % 12 != 0)
        {
            return null;
        }

        var sectionCount = (headerSize - 8) / 12;
        if (sectionCount <= 20)
        {
            return null;
        }

        (int Offset, int Size, int Count) ReadSection(int idx)
        {
            var s = header.AsSpan(8 + idx * 12, 12);
            return (
                BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(s.Slice(4, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(s.Slice(8, 4)));
        }

        var strSec = ReadSection(2);
        var tdSec = ReadSection(19);
        var imgSec = ReadSection(20);

        if (!IsSliceValid(strSec.Offset, strSec.Size, fileLength)
            || !IsSliceValid(tdSec.Offset, tdSec.Size, fileLength)
            || !IsSliceValid(imgSec.Offset, imgSec.Size, fileLength)
            || tdSec.Count <= 0
            || imgSec.Count <= 0
            || tdSec.Size % tdSec.Count != 0
            || imgSec.Size % imgSec.Count != 0)
        {
            return null;
        }

        var stringBytes = ReadSlice(stream, strSec.Offset, strSec.Size);
        var tdBytes = ReadSlice(stream, tdSec.Offset, tdSec.Size);
        var imgBytes = ReadSlice(stream, imgSec.Offset, imgSec.Size);

        var tdStructSize = tdSec.Size / tdSec.Count;
        var imgStructSize = imgSec.Size / imgSec.Count;
        if (tdStructSize < 16 || imgStructSize < 16)
        {
            return null;
        }

        var imagesOut = new List<MetadataCatalogImage>(imgSec.Count);
        var totalAdded = 0;

        for (var i = 0; i < imgSec.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = imgBytes.AsSpan(i * imgStructSize, imgStructSize);
            var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
            var imgName = ReadNullTerminatedUtf8(stringBytes, nameIdx);
            if (string.IsNullOrWhiteSpace(imgName))
            {
                continue;
            }

            var typeStart = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
            var typeCount = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(12, 4));
            if (typeStart < 0 || typeCount < 0 || typeStart + typeCount > tdSec.Count)
            {
                continue;
            }

            var list = new List<MetadataCatalogType>(Math.Min(typeCount, 1024));
            for (var t = typeStart; t < typeStart + typeCount; t++)
            {
                var tdEntry = tdBytes.AsSpan(t * tdStructSize, tdStructSize);
                var tdNameIdx = BinaryPrimitives.ReadInt32LittleEndian(tdEntry.Slice(0, 4));
                var tdNsIdx = BinaryPrimitives.ReadInt32LittleEndian(tdEntry.Slice(4, 4));
                var className = ReadNullTerminatedUtf8(stringBytes, tdNameIdx);
                if (string.IsNullOrWhiteSpace(className) || AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName(className))
                {
                    continue;
                }

                var ns = ReadNullTerminatedUtf8(stringBytes, tdNsIdx) ?? string.Empty;
                list.Add(new MetadataCatalogType(ns, className, AssemblyTypeKind.Class, 0, 0));
                totalAdded++;
            }

            imagesOut.Add(new MetadataCatalogImage(imgName, list));
        }

        return new MetadataCatalogResult(version, imagesOut, totalAdded);
    }

    private static int DetectTypeDefStructSize(byte[] tdBytes)
    {
        foreach (var candidate in new[] { 88, 92, 96, 100, 104, 108, 112 })
        {
            if (tdBytes.Length == 0 || tdBytes.Length % candidate != 0)
            {
                continue;
            }

            var count = tdBytes.Length / candidate;
            var checkCount = Math.Min(count, 32);
            var validTokens = 0;
            for (var i = 0; i < checkCount; i++)
            {
                var token = BinaryPrimitives.ReadUInt32LittleEndian(tdBytes.AsSpan(i * candidate + candidate - 4, 4));
                if ((token >> 24) == 0x02)
                {
                    validTokens++;
                }
            }

            if (validTokens >= Math.Max(1, checkCount * 3 / 4))
            {
                return candidate;
            }
        }

        return -1;
    }

    private static List<(string Name, int TypeStart, int TypeCount)>? TryParseImages(
        byte[] imgBytes,
        byte[] stringBytes,
        int totalTypes)
    {
        foreach (var structSize in new[] { 40, 36, 32, 24, 20 })
        {
            if (imgBytes.Length == 0 || imgBytes.Length % structSize != 0)
            {
                continue;
            }

            var count = imgBytes.Length / structSize;
            var list = new List<(string, int, int)>(count);
            var valid = true;
            var dllLikeCount = 0;

            for (var i = 0; i < count; i++)
            {
                var entry = imgBytes.AsSpan(i * structSize, structSize);
                var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
                var typeStart = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
                var typeCount = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(12, 4));

                var name = ReadNullTerminatedUtf8(stringBytes, nameIdx);
                if (string.IsNullOrWhiteSpace(name) || typeStart < 0 || typeCount < 0 || typeStart + typeCount > totalTypes)
                {
                    valid = false;
                    break;
                }

                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    dllLikeCount++;
                }

                list.Add((name, typeStart, typeCount));
            }

            if (valid && list.Count > 0 && dllLikeCount > 0)
            {
                return list;
            }
        }

        return null;
    }

    private static bool IsSliceValid(int offset, int size, long fileLength)
    {
        return offset >= 0 && size > 0 && (long)offset + size <= fileLength;
    }

    private static byte[] ReadSlice(FileStream stream, int offset, int size)
    {
        var buffer = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer, 0, size);
        return buffer;
    }

    private static string? ReadNullTerminatedUtf8(byte[] stringBytes, int index)
    {
        if (index < 0 || index >= stringBytes.Length)
        {
            return null;
        }

        var maxEnd = Math.Min(stringBytes.Length, index + 1024);
        var end = index;
        while (end < maxEnd && stringBytes[end] != 0)
        {
            end++;
        }

        if (end <= index)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(stringBytes, index, end - index);
    }
}

#endregion

#region SCRIPT-04: Bidirectional Script Usage Lookup (MonoScript <-> MonoBehaviour <-> GameObject)

public sealed class ScriptUsageItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string DetailLine { get; init; } = string.Empty;
    public string TargetAssetUniqueId { get; init; } = string.Empty;
    public ClassIDType TargetAssetType { get; init; } = ClassIDType.MonoBehaviour;
    public long PathId { get; init; }
    public string SerializedFileName { get; init; } = string.Empty;
    public string GameObjectName { get; init; } = string.Empty;
    public string GameObjectUniqueId { get; init; } = string.Empty;

    public bool IsNavigable => !string.IsNullOrWhiteSpace(TargetAssetUniqueId) || PathId != 0;

    public AssetReferenceItem ToAssetReferenceItem()
    {
        return new AssetReferenceItem
        {
            AssetUniqueId = TargetAssetUniqueId,
            Name = DisplayName,
            AssetType = TargetAssetType,
            TypeName = AssetItem.GetCachedTypeString(TargetAssetType),
            Relation = string.IsNullOrWhiteSpace(GameObjectName)
                ? "Uses Script"
                : $"GameObject '{GameObjectName}'",
            PathId = PathId,
            SerializedFileName = SerializedFileName,
            Direction = AssetReferenceDirection.ReferencedBy
        };
    }
}

public sealed class ScriptUsageLookupResult
{
    public static readonly ScriptUsageLookupResult Empty = new();

    public IReadOnlyList<ScriptUsageItem> Items { get; init; } = Array.Empty<ScriptUsageItem>();
    public int TotalCount { get; init; }
    public int MatchedMonoScriptCount { get; init; }
    public bool WasTruncated { get; init; }

    public string HeaderText => WasTruncated
        ? string.Create(CultureInfo.InvariantCulture, $"Used By ({Items.Count:N0}+ of {TotalCount:N0})")
        : string.Create(CultureInfo.InvariantCulture, $"Used By ({TotalCount:N0})");
}

public sealed class ScriptUsageLookupContext
{
    public ProjectIndex? ProjectIndex { get; init; }
    public IReadOnlyList<SerializedFile> LoadedFiles { get; init; } = Array.Empty<SerializedFile>();
    public Action<string, List<AssetReferenceItem>, List<AssetReferenceItem>, int>? SemanticCacheLoader { get; init; }
    public Func<AssetHandle, MonoScript?>? BoundedLazyMonoScriptLoader { get; init; }
    public int MaxResults { get; init; } = 250;
}

public static class ScriptUsageLookupService
{
    private const int MaxSemanticScriptQueries = 16;
    private const int MaxLazyScriptVerifications = 8;

    public static ScriptUsageLookupResult FindUsages(
        string? assemblyName,
        string? ns,
        string? className,
        AssetHandle? selectedMonoScriptHandle,
        MonoScript? selectedMonoScript,
        ScriptUsageLookupContext? context,
        CancellationToken cancellationToken = default)
    {
        var cleanClass = className?.Trim() ?? string.Empty;
        var cleanNs = ns?.Trim() ?? string.Empty;
        var cleanAsm = UnityProjectGuidUtility.NormalizeAssemblyShortName(assemblyName);

        if (string.IsNullOrWhiteSpace(cleanClass) && selectedMonoScript == null && selectedMonoScriptHandle == null)
        {
            return ScriptUsageLookupResult.Empty;
        }

        if (selectedMonoScript != null)
        {
            if (string.IsNullOrWhiteSpace(cleanClass)) cleanClass = selectedMonoScript.m_ClassName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanNs)) cleanNs = selectedMonoScript.m_Namespace ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanAsm)) cleanAsm = UnityProjectGuidUtility.NormalizeAssemblyShortName(selectedMonoScript.m_AssemblyName);
        }

        var maxResults = Math.Max(1, context?.MaxResults ?? 250);
        var projectIndex = context?.ProjectIndex;
        var loadedFiles = context?.LoadedFiles ?? Array.Empty<SerializedFile>();

        // 1. Collect matching MonoScript identities (UniqueID + (FileName, PathID))
        var matchedScriptUids = new HashSet<string>(StringComparer.Ordinal);
        var matchedScriptKeys = new HashSet<(string FileName, long PathId)>(FilePathIdComparer.Instance);

        void AddMatchedScript(string? uid, string? fileName, long pathId)
        {
            if (!string.IsNullOrWhiteSpace(uid))
            {
                matchedScriptUids.Add(uid);
            }
            if (pathId != 0)
            {
                matchedScriptKeys.Add((fileName ?? string.Empty, pathId));
            }
        }

        if (selectedMonoScript != null && selectedMonoScript.assetsFile != null)
        {
            var uid = AssetHandle.BuildUniqueID(selectedMonoScript.assetsFile, selectedMonoScript.m_PathID);
            AddMatchedScript(uid, selectedMonoScript.assetsFile.fileName, selectedMonoScript.m_PathID);
        }
        if (selectedMonoScriptHandle != null && selectedMonoScriptHandle.Type == ClassIDType.MonoScript)
        {
            AddMatchedScript(selectedMonoScriptHandle.UniqueID, selectedMonoScriptHandle.SerializedFileName, selectedMonoScriptHandle.PathID);
        }

        // 1a. Scan already-loaded SerializedFiles for matching MonoScript objects
        for (var f = 0; f < loadedFiles.Count; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = loadedFiles[f];
            if (file == null) continue;

            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] is MonoScript ms && MatchesScriptIdentity(ms, cleanAsm, cleanNs, cleanClass))
                {
                    AddMatchedScript(AssetHandle.BuildUniqueID(file, ms.m_PathID), file.fileName, ms.m_PathID);
                }
            }
        }

        // 1b. Fast columnar scan of ProjectIndex.Arena for ClassIDType.MonoScript handles (zero bulk load!)
        if (projectIndex != null && !string.IsNullOrWhiteSpace(cleanClass))
        {
            var arena = projectIndex.Arena;
            var arenaCount = arena.Count;
            var lazyCandidates = new List<AssetHandle>();
            var simpleTarget = StripGenericArity(cleanClass);
            var fullTarget = string.IsNullOrWhiteSpace(cleanNs) ? simpleTarget : $"{cleanNs}.{simpleTarget}";

            for (var i = 0; i < arenaCount; i++)
            {
                if ((i & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (arena.GetType(i) != ClassIDType.MonoScript)
                {
                    continue;
                }

                var handleName = arena.GetName(i);
                if (string.IsNullOrWhiteSpace(handleName))
                {
                    continue;
                }

                if (!string.Equals(handleName, cleanClass, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(handleName, simpleTarget, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(handleName, fullTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var existingReal = arena.GetRealObject(i);
                if (existingReal is MonoScript realMs)
                {
                    if (MatchesScriptIdentity(realMs, cleanAsm, cleanNs, cleanClass))
                    {
                        AddMatchedScript(arena.GetUniqueId(i), arena.GetSerializedFileName(i), arena.GetPathId(i));
                    }
                    continue;
                }

                var uid = arena.GetUniqueId(i);
                if (matchedScriptUids.Contains(uid))
                {
                    continue;
                }

                var handle = arena.MaterializeHandle(i);
                lazyCandidates.Add(handle);
            }

            // Optionally verify up to a tiny bounded number of candidate MonoScripts lazily when ambiguous
            var verifiedCount = 0;
            for (var c = 0; c < lazyCandidates.Count; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = lazyCandidates[c];
                if (context?.BoundedLazyMonoScriptLoader != null && verifiedCount < MaxLazyScriptVerifications)
                {
                    verifiedCount++;
                    try
                    {
                        var loaded = context.BoundedLazyMonoScriptLoader(candidate);
                        if (loaded != null)
                        {
                            if (MatchesScriptIdentity(loaded, cleanAsm, cleanNs, cleanClass))
                            {
                                AddMatchedScript(candidate.UniqueID, candidate.SerializedFileName, candidate.PathID);
                            }
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                    }
                }

                // Fallback: accept name-matched MonoScript handle without loading disk
                AddMatchedScript(candidate.UniqueID, candidate.SerializedFileName, candidate.PathID);
            }
        }

        // 2. Collect referencing MonoBehaviours (deduplicated)
        var dedupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usages = new List<ScriptUsageItem>(Math.Min(maxResults, 64));
        var totalCount = 0;

        void TryAddUsage(ScriptUsageItem item)
        {
            var dedupKey = !string.IsNullOrWhiteSpace(item.TargetAssetUniqueId)
                ? item.TargetAssetUniqueId
                : $"{item.SerializedFileName}#{item.PathId}";
            if (!dedupKeys.Add(dedupKey))
            {
                return;
            }

            totalCount++;
            if (usages.Count < maxResults)
            {
                usages.Add(item);
            }
        }

        // 2a. SQLite semantic cache (fast indexed lookup, safe in Lazy Mode)
        if (context?.SemanticCacheLoader != null && matchedScriptUids.Count > 0)
        {
            var queried = 0;
            foreach (var scriptUid in matchedScriptUids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (queried++ >= MaxSemanticScriptQueries)
                {
                    break;
                }

                var usedByOut = new List<AssetReferenceItem>();
                var usesOut = new List<AssetReferenceItem>();
                try
                {
                    context.SemanticCacheLoader(scriptUid, usedByOut, usesOut, maxResults + 1);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                for (var i = 0; i < usedByOut.Count; i++)
                {
                    var refItem = usedByOut[i];
                    if (refItem.AssetType != ClassIDType.MonoBehaviour && refItem.AssetType != ClassIDType.GameObject)
                    {
                        continue;
                    }

                    var goName = string.Empty;
                    var goUid = string.Empty;

                    // Enrich with GameObject name in-memory if already materialized (zero disk I/O)
                    if (refItem.AssetType == ClassIDType.MonoBehaviour
                        && projectIndex != null
                        && !string.IsNullOrWhiteSpace(refItem.AssetUniqueId))
                    {
                        var mbHandle = projectIndex.GetHandle(refItem.AssetUniqueId);
                        if (mbHandle?.RealObject is MonoBehaviour loadedMb)
                        {
                            (goName, goUid) = TryResolveGameObjectInfoInMemory(loadedMb, projectIndex);
                        }
                    }

                    var displayName = !string.IsNullOrWhiteSpace(goName)
                        ? goName
                        : (!string.IsNullOrWhiteSpace(refItem.Name) ? refItem.Name : $"{refItem.TypeName} ({refItem.PathIdText})");

                    var detail = BuildDetailLine(
                        refItem.AssetType,
                        goName,
                        refItem.SerializedFileName,
                        refItem.PathId);

                    TryAddUsage(new ScriptUsageItem
                    {
                        DisplayName = displayName,
                        DetailLine = detail,
                        TargetAssetUniqueId = refItem.AssetUniqueId,
                        TargetAssetType = refItem.AssetType,
                        PathId = refItem.PathId,
                        SerializedFileName = refItem.SerializedFileName,
                        GameObjectName = goName,
                        GameObjectUniqueId = goUid
                    });
                }
            }
        }

        // 2b. Already-loaded SerializedFiles in memory (Eager Mode + loaded Lazy Mode files, zero disk I/O)
        for (var f = 0; f < loadedFiles.Count; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = loadedFiles[f];
            if (file == null) continue;

            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] is not MonoBehaviour mb || mb.m_Script == null || mb.m_Script.IsNull)
                {
                    continue;
                }

                if (!DoesMonoBehaviourUseScript(mb, file, matchedScriptKeys, cleanAsm, cleanNs, cleanClass))
                {
                    continue;
                }

                var mbUid = AssetHandle.BuildUniqueID(file, mb.m_PathID);
                var (goName, goUid) = TryResolveGameObjectInfoInMemory(mb, projectIndex);
                var displayName = !string.IsNullOrWhiteSpace(goName)
                    ? goName
                    : (!string.IsNullOrWhiteSpace(mb.m_Name) ? mb.m_Name : $"MonoBehaviour ({mb.m_PathID})");

                var detail = BuildDetailLine(ClassIDType.MonoBehaviour, goName, file.fileName ?? string.Empty, mb.m_PathID);

                TryAddUsage(new ScriptUsageItem
                {
                    DisplayName = displayName,
                    DetailLine = detail,
                    TargetAssetUniqueId = mbUid,
                    TargetAssetType = ClassIDType.MonoBehaviour,
                    PathId = mb.m_PathID,
                    SerializedFileName = file.fileName ?? string.Empty,
                    GameObjectName = goName,
                    GameObjectUniqueId = goUid
                });
            }
        }

        usages.Sort(static (a, b) =>
        {
            var nameCmp = StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            if (nameCmp != 0) return nameCmp;
            var fileCmp = StringComparer.OrdinalIgnoreCase.Compare(a.SerializedFileName, b.SerializedFileName);
            if (fileCmp != 0) return fileCmp;
            return a.PathId.CompareTo(b.PathId);
        });

        return new ScriptUsageLookupResult
        {
            Items = usages,
            TotalCount = totalCount,
            MatchedMonoScriptCount = Math.Max(matchedScriptUids.Count, matchedScriptKeys.Count),
            WasTruncated = totalCount > usages.Count
        };
    }

    private static bool MatchesScriptIdentity(MonoScript ms, string cleanAsm, string cleanNs, string cleanClass)
    {
        if (string.IsNullOrWhiteSpace(cleanClass))
        {
            return false;
        }

        var msClass = ms.m_ClassName?.Trim() ?? string.Empty;
        if (!string.Equals(msClass, cleanClass, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(StripGenericArity(msClass), StripGenericArity(cleanClass), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(cleanNs))
        {
            var msNs = ms.m_Namespace?.Trim() ?? string.Empty;
            if (!string.Equals(msNs, cleanNs, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(cleanAsm))
        {
            var msAsm = UnityProjectGuidUtility.NormalizeAssemblyShortName(ms.m_AssemblyName);
            if (!string.IsNullOrWhiteSpace(msAsm)
                && !string.Equals(msAsm, cleanAsm, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DoesMonoBehaviourUseScript(
        MonoBehaviour mb,
        SerializedFile ownerFile,
        HashSet<(string FileName, long PathId)> matchedScriptKeys,
        string cleanAsm,
        string cleanNs,
        string cleanClass)
    {
        var pptr = mb.m_Script;
        if (pptr == null || pptr.IsNull)
        {
            return false;
        }

        string targetFileName;
        AssetStudio.Object? resolvedScriptObj = null;

        if (pptr.m_FileID == 0)
        {
            targetFileName = ownerFile.fileName ?? string.Empty;
            ownerFile.TryGetObject(pptr.m_PathID, out resolvedScriptObj);
        }
        else if (pptr.m_FileID > 0 && ownerFile.m_Externals != null && pptr.m_FileID - 1 < ownerFile.m_Externals.Count)
        {
            var ext = ownerFile.m_Externals[pptr.m_FileID - 1];
            targetFileName = NormalizeExternalFileName(ext?.fileName, ext?.pathName);
        }
        else
        {
            targetFileName = string.Empty;
        }

        if (matchedScriptKeys.Contains((targetFileName, pptr.m_PathID)))
        {
            return true;
        }

        if (resolvedScriptObj is MonoScript inMemMs)
        {
            return MatchesScriptIdentity(inMemMs, cleanAsm, cleanNs, cleanClass);
        }

        // Also check SerializedType metadata on the MonoBehaviour itself
        var st = mb.serializedType;
        if (st != null && !string.IsNullOrWhiteSpace(st.m_KlassName)
            && string.Equals(st.m_KlassName, cleanClass, StringComparison.OrdinalIgnoreCase))
        {
            var nsOk = string.IsNullOrWhiteSpace(cleanNs)
                || string.Equals(st.m_NameSpace ?? string.Empty, cleanNs, StringComparison.OrdinalIgnoreCase);
            var asmOk = string.IsNullOrWhiteSpace(cleanAsm)
                || string.IsNullOrWhiteSpace(st.m_AsmName)
                || string.Equals(UnityProjectGuidUtility.NormalizeAssemblyShortName(st.m_AsmName), cleanAsm, StringComparison.OrdinalIgnoreCase);
            if (nsOk && asmOk)
            {
                return true;
            }
        }

        return false;
    }

    private static (string GameObjectName, string GameObjectUniqueId) TryResolveGameObjectInfoInMemory(
        MonoBehaviour mb,
        ProjectIndex? projectIndex)
    {
        var goPtr = mb.m_GameObject;
        var ownerFile = mb.assetsFile;
        if (goPtr == null || goPtr.IsNull || ownerFile == null)
        {
            return (string.Empty, string.Empty);
        }

        if (goPtr.m_FileID == 0)
        {
            var goUid = AssetHandle.BuildUniqueID(ownerFile, goPtr.m_PathID);
            if (ownerFile.TryGetObject(goPtr.m_PathID, out var obj) && obj is GameObject go)
            {
                return (go.m_Name ?? string.Empty, goUid);
            }

            if (projectIndex != null)
            {
                var handle = projectIndex.GetHandle(goUid);
                if (handle != null && !string.IsNullOrWhiteSpace(handle.Name))
                {
                    return (handle.Name, goUid);
                }
            }

            return (string.Empty, goUid);
        }

        if (goPtr.m_FileID > 0 && ownerFile.m_Externals != null && goPtr.m_FileID - 1 < ownerFile.m_Externals.Count)
        {
            var ext = ownerFile.m_Externals[goPtr.m_FileID - 1];
            var extName = NormalizeExternalFileName(ext?.fileName, ext?.pathName);
            if (!string.IsNullOrWhiteSpace(extName) && projectIndex != null)
            {
                var goUid = $"{extName}#{goPtr.m_PathID}";
                var handle = projectIndex.GetHandle(goUid);
                if (handle != null && !string.IsNullOrWhiteSpace(handle.Name))
                {
                    return (handle.Name, handle.UniqueID);
                }
            }
        }

        return (string.Empty, string.Empty);
    }

    private static string BuildDetailLine(ClassIDType type, string goName, string fileName, long pathId)
    {
        var parts = new List<string>(4) { AssetItem.GetCachedTypeString(type) };
        if (!string.IsNullOrWhiteSpace(goName))
        {
            parts.Add($"GameObject '{goName}'");
        }
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            parts.Add(fileName);
        }
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"PathID {pathId}"));
        return string.Join(" • ", parts);
    }

    private static string NormalizeExternalFileName(string? fileName, string? pathName)
    {
        var candidate = !string.IsNullOrWhiteSpace(fileName) ? fileName : pathName;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        return Path.GetFileName(candidate.Trim().Replace('\\', '/'));
    }

    private static string StripGenericArity(string name)
    {
        var idx = name.IndexOfAny(new[] { '`', '<' });
        return idx > 0 ? name.Substring(0, idx) : name;
    }

    private sealed class FilePathIdComparer : IEqualityComparer<(string FileName, long PathId)>
    {
        public static readonly FilePathIdComparer Instance = new();

        public bool Equals((string FileName, long PathId) x, (string FileName, long PathId) y)
        {
            return x.PathId == y.PathId
                && string.Equals(x.FileName, y.FileName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string FileName, long PathId) obj)
        {
            var hc = new HashCode();
            hc.Add(obj.FileName, StringComparer.OrdinalIgnoreCase);
            hc.Add(obj.PathId);
            return hc.ToHashCode();
        }
    }
}

#endregion
