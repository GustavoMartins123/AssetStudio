using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace AssetStudio.Avalonia.Services.Scripting;

public sealed class MonoDecompilerService : IDisposable
{
    private const int MaxCachedAssemblies = 16;

    private sealed class CachedAssemblyEntry : IDisposable
    {
        public required string AssemblyPath { get; init; }
        public required long FileLength { get; init; }
        public required long LastWriteTicks { get; init; }
        public required PEFile PeFile { get; init; }
        public required CSharpDecompiler Decompiler { get; init; }
        public required bool IsDummyAssembly { get; init; }
        public long LastAccessTick { get; set; }

        public void Dispose()
        {
            PeFile.Dispose();
        }
    }

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedAssemblyEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _accessCounter;
    private bool _disposed;

    public ScriptDecompilationResult? TryDecompile(
        ScriptLookupRequest request,
        IReadOnlyList<string> searchDirectories,
        bool treatAsDummyDll = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || searchDirectories == null || searchDirectories.Count == 0)
        {
            return null;
        }

        var className = request.ClassName?.Trim();
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        var validDirs = searchDirectories
            .Where(static d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validDirs.Count == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Try explicit assembly file first (fast path)
        var candidateDlls = ResolveCandidateAssemblies(request.AssemblyName, validDirs);
        foreach (var dllPath in candidateDlls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = TryDecompileTypeFromAssembly(
                dllPath,
                validDirs,
                request,
                treatAsDummyDll,
                cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        // 2. Fallback scan across remaining managed DLLs if m_AssemblyName was empty or relocated
        var alreadyTried = new HashSet<string>(candidateDlls, StringComparer.OrdinalIgnoreCase);
        var fallbackDlls = EnumerateFallbackAssemblies(validDirs, alreadyTried);
        foreach (var dllPath in fallbackDlls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = TryDecompileTypeFromAssembly(
                dllPath,
                validDirs,
                request,
                treatAsDummyDll,
                cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    public ScriptDecompilationResult? TryDecompileFromAssemblyFile(
        string assemblyPath,
        string? ns,
        string className,
        IReadOnlyList<string>? additionalSearchDirectories = null,
        bool treatAsDummyDll = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath) || string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(assemblyPath);
        var dirs = new List<string>();
        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
        {
            dirs.Add(parentDir);
        }

        if (additionalSearchDirectories != null)
        {
            foreach (var dir in additionalSearchDirectories)
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }

        var request = new ScriptLookupRequest
        {
            AssemblyName = Path.GetFileName(fullPath),
            Namespace = ns ?? string.Empty,
            ClassName = className
        };

        return TryDecompileTypeFromAssembly(
            fullPath,
            dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            request,
            treatAsDummyDll,
            cancellationToken);
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            foreach (var entry in _cache.Values)
            {
                try
                {
                    entry.Dispose();
                }
                catch
                {
                }
            }
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearCache();
    }

    private ScriptDecompilationResult? TryDecompileTypeFromAssembly(
        string dllPath,
        IReadOnlyList<string> searchDirectories,
        ScriptLookupRequest request,
        bool treatAsDummyDll,
        CancellationToken cancellationToken)
    {
        CachedAssemblyEntry? entry;
        try
        {
            entry = GetOrOpenAssembly(dllPath, searchDirectories, treatAsDummyDll);
        }
        catch
        {
            return null;
        }

        if (entry == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        string decompiled;
        ITypeDefinition? matchedType;
        string actualAssemblyName;
        bool isDummy;

        lock (entry)
        {
            matchedType = FindMatchingType(entry.Decompiler.TypeSystem.MainModule, request.Namespace, request.ClassName);
            if (matchedType == null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            decompiled = entry.Decompiler.DecompileTypeAsString(matchedType.FullTypeName);
            actualAssemblyName = Path.GetFileName(entry.AssemblyPath);
            isDummy = entry.IsDummyAssembly || treatAsDummyDll;
        }

        var isIl2CppDummy = isDummy || ContainsIl2CppDummyAttribute(decompiled);
        var sourceKind = isIl2CppDummy
            ? ScriptSourceKind.Il2CppDummyAssemblyDecompiled
            : ScriptSourceKind.MonoAssemblyDecompiled;
        var backendBadge = isIl2CppDummy
            ? "IL2CPP (Dummy DLL)"
            : "Mono";

        var displayTitle = !string.IsNullOrWhiteSpace(matchedType.Namespace)
            ? $"{matchedType.Namespace}.{matchedType.Name}"
            : matchedType.Name;

        var header = new StringBuilder();
        header.AppendLine($"// Assembly: {actualAssemblyName}");
        header.AppendLine($"// Type: {matchedType.FullName}");
        header.AppendLine();
        header.Append(decompiled.TrimEnd());

        var fullCode = header.ToString();
        var lineCount = CountLines(fullCode);

        return new ScriptDecompilationResult
        {
            Success = true,
            SourceKind = sourceKind,
            BackendBadge = backendBadge,
            Title = displayTitle,
            AssemblyName = actualAssemblyName,
            Namespace = matchedType.Namespace ?? string.Empty,
            ClassName = matchedType.Name ?? request.ClassName,
            SourceFilePath = entry.AssemblyPath,
            SourceCode = fullCode,
            SummaryText = $"{actualAssemblyName} • {lineCount:N0} lines • {backendBadge}",
            DiagnosticMessage = isIl2CppDummy
                ? "Showing type layout from IL2CPP stub assembly (method implementations are compiled to native code)."
                : string.Empty
        };
    }

    private CachedAssemblyEntry? GetOrOpenAssembly(
        string dllPath,
        IReadOnlyList<string> searchDirectories,
        bool treatAsDummyDll)
    {
        var fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var info = new FileInfo(fullPath);
        var tick = Interlocked.Increment(ref _accessCounter);

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(fullPath, out var cached)
                && cached.FileLength == info.Length
                && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                cached.LastAccessTick = tick;
                return cached;
            }
        }

        PEFile? peFile = null;
        try
        {
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                peFile = new PEFile(
                    fullPath,
                    stream,
                    PEStreamOptions.PrefetchEntireImage,
                    MetadataReaderOptions.None);
            }

            if (!peFile.Reader.HasMetadata)
            {
                peFile.Dispose();
                return null;
            }

            var resolver = new UniversalAssemblyResolver(
                mainAssemblyFileName: fullPath,
                throwOnError: false,
                targetFramework: null,
                runtimePack: null,
                streamOptions: PEStreamOptions.PrefetchEntireImage,
                metadataOptions: MetadataReaderOptions.None);

            var assemblyDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                resolver.AddSearchDirectory(assemblyDir);
            }

            foreach (var dir in searchDirectories)
            {
                resolver.AddSearchDirectory(dir);
            }

            var settings = new DecompilerSettings(LanguageVersion.Latest)
            {
                ThrowOnAssemblyResolveErrors = false,
                UseDebugSymbols = false,
                ShowXmlDocumentation = false
            };

            var decompiler = new CSharpDecompiler(peFile, resolver, settings);
            var isDummy = treatAsDummyDll
                || (!string.IsNullOrWhiteSpace(assemblyDir) && ScriptSourceDiscovery.IsLikelyDummyDllDirectory(assemblyDir))
                || peFile.AssemblyReferences.Any(r => string.Equals(r.Name, "Il2CppDummyDll", StringComparison.OrdinalIgnoreCase));

            var created = new CachedAssemblyEntry
            {
                AssemblyPath = fullPath,
                FileLength = info.Length,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                PeFile = peFile,
                Decompiler = decompiler,
                IsDummyAssembly = isDummy,
                LastAccessTick = tick
            };

            lock (_cacheLock)
            {
                if (_disposed)
                {
                    created.Dispose();
                    return null;
                }

                if (_cache.TryGetValue(fullPath, out var existing))
                {
                    existing.Dispose();
                }

                _cache[fullPath] = created;
                EvictIfNeededLocked();
            }

            return created;
        }
        catch
        {
            peFile?.Dispose();
            return null;
        }
    }

    private void EvictIfNeededLocked()
    {
        while (_cache.Count > MaxCachedAssemblies)
        {
            string? oldestKey = null;
            var oldestTick = long.MaxValue;
            foreach (var pair in _cache)
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

            if (_cache.Remove(oldestKey, out var evicted))
            {
                try
                {
                    evicted.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static ITypeDefinition? FindMatchingType(IModule module, string? ns, string className)
    {
        var cleanNs = ns?.Trim() ?? string.Empty;
        var cleanClass = className.Trim();
        var normalizedNested = cleanClass.Replace('/', '+');
        var dotNested = normalizedNested.Replace('+', '.');

        // 1. Direct lookup via FullTypeName (fastest)
        foreach (var candidate in BuildCandidateFullTypeNames(cleanNs, normalizedNested))
        {
            try
            {
                var found = module.GetTypeDefinition(new FullTypeName(candidate));
                if (found != null)
                {
                    return found;
                }
            }
            catch
            {
            }
        }

        // 2. Scan module definitions for exact / generic-arity / case-insensitive / nested match
        ITypeDefinition? caseInsensitiveExact = null;
        ITypeDefinition? genericMatch = null;
        ITypeDefinition? simpleNameMatch = null;
        var baseTarget = StripGenericArity(dotNested);
        var leafTarget = baseTarget.Contains('.') ? baseTarget[(baseTarget.LastIndexOf('.') + 1)..] : baseTarget;

        foreach (var typeDef in module.TypeDefinitions)
        {
            var typeNs = typeDef.Namespace ?? string.Empty;
            var typeName = typeDef.Name ?? string.Empty;
            var typeBase = StripGenericArity(typeName);
            var nsMatches = string.Equals(typeNs, cleanNs, StringComparison.OrdinalIgnoreCase);

            if (nsMatches && string.Equals(typeName, cleanClass, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitiveExact ??= typeDef;
                break;
            }

            var reflectionFull = typeDef.ReflectionName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cleanNs))
            {
                var expectedNested = $"{cleanNs}.{normalizedNested}";
                if (string.Equals(reflectionFull, expectedNested, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(StripGenericArity(reflectionFull), StripGenericArity(expectedNested), StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitiveExact ??= typeDef;
                    break;
                }
            }

            if (nsMatches && string.Equals(typeBase, baseTarget, StringComparison.OrdinalIgnoreCase))
            {
                genericMatch ??= typeDef;
            }
            else if (string.Equals(typeBase, leafTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, cleanClass, StringComparison.OrdinalIgnoreCase))
            {
                simpleNameMatch ??= typeDef;
            }
        }

        return caseInsensitiveExact ?? genericMatch ?? simpleNameMatch;
    }

    private static IEnumerable<string> BuildCandidateFullTypeNames(string cleanNs, string normalizedClass)
    {
        if (!string.IsNullOrWhiteSpace(cleanNs))
        {
            yield return $"{cleanNs}.{normalizedClass}";
        }
        yield return normalizedClass;
    }

    private static List<string> ResolveCandidateAssemblies(string? assemblyName, IReadOnlyList<string> directories)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            var normalized = assemblyName.Trim();
            var withDll = normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : normalized + ".dll";

            foreach (var dir in directories)
            {
                var direct = Path.Combine(dir, withDll);
                if (File.Exists(direct) && seen.Add(direct))
                {
                    results.Add(direct);
                }
            }
        }

        // Common Unity primary game assemblies if m_AssemblyName was omitted
        if (results.Count == 0)
        {
            foreach (var common in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" })
            {
                foreach (var dir in directories)
                {
                    var candidate = Path.Combine(dir, common);
                    if (File.Exists(candidate) && seen.Add(candidate))
                    {
                        results.Add(candidate);
                    }
                }
            }
        }

        return results;
    }

    private static List<string> EnumerateFallbackAssemblies(IReadOnlyList<string> directories, HashSet<string> exclude)
    {
        var results = new List<string>();
        foreach (var dir in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var full = Path.GetFullPath(file);
                    if (exclude.Add(full) && !IsLowPriorityFrameworkDll(Path.GetFileName(full)))
                    {
                        results.Add(full);
                    }
                }
            }
            catch
            {
            }
        }

        // Keep fallback bounded and prioritized
        return results
            .OrderBy(static p => Path.GetFileName(p).StartsWith("Assembly-", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToList();
    }

    private static bool IsLowPriorityFrameworkDll(string fileName)
    {
        return fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Mono.Security.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Il2CppDummyDll.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIl2CppDummyAttribute(string source)
    {
        return source.Contains("Il2CppDummyDll.Address", StringComparison.Ordinal)
            || source.Contains("Il2CppDummyDll.FieldOffset", StringComparison.Ordinal)
            || source.Contains("[Address(RVA =", StringComparison.Ordinal);
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        var angle = name.IndexOf('<');
        var cut = tick >= 0 && angle >= 0 ? Math.Min(tick, angle) : Math.Max(tick, angle);
        return cut > 0 ? name.Substring(0, cut) : name;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
