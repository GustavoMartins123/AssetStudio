using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis;

public sealed class ProjectRuntimeDetector
{
    public const uint Il2CppMetadataSanity = 0xFAB11BAF;
    public const int MinExpectedMetadataVersion = 16;
    public const int MaxExpectedMetadataVersion = 100;
    private const string SignatureSchemaVersion = "project-analysis-sig-v1";
    private const int MaxDirectoryVisits = 512;
    private const int MaxSearchDepth = 4;

    private static readonly string[] GameAssemblyNames =
    {
        "GameAssembly.dll",
        "libil2cpp.so",
        "GameAssembly.so",
        "GameAssembly.dylib"
    };

    private static readonly string[] Il2CppDumperFileNames =
    {
        "script.json",
        "dump.cs",
        "il2cpp.h"
    };

    public static string ComputeProjectHash(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return "empty";
        }

        var normalized = NormalizeFullPathForKey(projectRoot);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes, 0, 16);
    }

    public static bool TryReadIl2CppMetadataVersion(string metadataPath, out int version, out string? warning)
    {
        version = 0;
        warning = null;

        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
        {
            warning = "global-metadata.dat file does not exist.";
            return false;
        }

        try
        {
            using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[8];
            var totalRead = 0;
            while (totalRead < header.Length)
            {
                var read = stream.Read(header.Slice(totalRead));
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead < 8)
            {
                warning = $"global-metadata.dat is truncated ({totalRead} bytes).";
                return false;
            }

            var sanity = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
            if (sanity != Il2CppMetadataSanity)
            {
                warning = $"global-metadata.dat magic mismatch (expected 0x{Il2CppMetadataSanity:X8}, got 0x{sanity:X8}); metadata may be encrypted or obfuscated.";
                return false;
            }

            var rawVersion = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));
            if (rawVersion < MinExpectedMetadataVersion || rawVersion > MaxExpectedMetadataVersion)
            {
                var lengthHint = rawVersion == stream.Length ? " (matches file length)" : string.Empty;
                warning = $"global-metadata.dat has magic 0x{Il2CppMetadataSanity:X8} but anomalous metadata version {rawVersion}{lengthHint} outside expected range {MinExpectedMetadataVersion}..{MaxExpectedMetadataVersion}; metadata appears obfuscated or encrypted.";
                return false;
            }

            version = rawVersion;
            return true;
        }
        catch (Exception ex)
        {
            warning = $"Failed to inspect global-metadata.dat header: {ex.Message}";
            return false;
        }
    }

    public ProjectRuntimeDetectionResult Detect(
        string projectRoot,
        IEnumerable<string>? additionalArtifactDirectories = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root must not be empty.", nameof(projectRoot));
        }

        var fullRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Project root directory was not found: {fullRoot}");
        }

        var result = new ProjectRuntimeDetectionResult
        {
            ProjectRoot = fullRoot,
            ProjectHash = ComputeProjectHash(fullRoot)
        };

        var signatureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataDirectories = DiscoverDataDirectories(fullRoot);

        // 1. Detect GameAssembly / libil2cpp
        var gameAssembly = DetectGameAssembly(fullRoot, dataDirectories);
        if (!string.IsNullOrEmpty(gameAssembly))
        {
            result.HasGameAssembly = true;
            result.GameAssemblyPath = gameAssembly;
            signatureFiles.Add(gameAssembly);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Detect global-metadata.dat and parse IL2CPP magic + metadata version
        var metadataPath = DetectGlobalMetadata(fullRoot, dataDirectories);
        if (!string.IsNullOrEmpty(metadataPath))
        {
            result.HasGlobalMetadata = true;
            result.GlobalMetadataPath = metadataPath;
            signatureFiles.Add(metadataPath);

            if (TryReadIl2CppMetadataVersion(metadataPath, out var metadataVersion, out var metadataWarning))
            {
                result.Il2CppMetadataVersion = metadataVersion;
            }
            else
            {
                result.Il2CppMetadataObfuscated = true;
                result.Il2CppMetadataAnomalyReason = metadataWarning;
            }

            if (!string.IsNullOrWhiteSpace(metadataWarning))
            {
                result.Warnings.Add(metadataWarning);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Detect Managed assemblies (*_Data/Managed/*.dll)
        var managedDlls = DetectManagedAssemblies(fullRoot, dataDirectories);
        result.ManagedAssemblyCount = managedDlls.Count;
        result.HasManagedAssemblies = managedDlls.Count > 0;
        foreach (var dll in managedDlls)
        {
            signatureFiles.Add(dll);
        }

        // 4. Determine Unity scripting backend
        if (result.HasGameAssembly || result.HasGlobalMetadata)
        {
            result.ScriptingBackend = UnityScriptingBackend.Il2Cpp;
        }
        else if (result.HasManagedAssemblies)
        {
            result.ScriptingBackend = UnityScriptingBackend.Mono;
        }
        else
        {
            result.ScriptingBackend = UnityScriptingBackend.Unknown;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 5. Detect Unity version (header-only fast reads)
        var (unityVersion, versionSourceFile) = DetectUnityVersion(fullRoot, dataDirectories);
        result.UnityVersion = unityVersion;
        if (!string.IsNullOrEmpty(versionSourceFile))
        {
            signatureFiles.Add(versionSourceFile);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 6. Detect Addressables catalogs (bounded scan)
        var catalogs = DetectAddressablesCatalogs(fullRoot, dataDirectories, cancellationToken);
        result.AddressablesCatalogPaths.AddRange(catalogs);
        result.HasAddressables = catalogs.Count > 0;
        foreach (var catalog in catalogs)
        {
            signatureFiles.Add(catalog);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 7. Detect existing Il2CppDumper artifacts (script.json, dump.cs, il2cpp.h)
        var dumperArtifacts = DetectIl2CppDumperArtifacts(fullRoot, dataDirectories, additionalArtifactDirectories, cancellationToken);
        result.Il2CppDumperArtifactPaths.AddRange(dumperArtifacts);
        foreach (var artifact in dumperArtifacts)
        {
            signatureFiles.Add(artifact);
        }

        // 8. Probe core data files and VFS containers for stream encryption barriers
        result.EncryptionBarriers.AddRange(DetectStreamEncryptionBarriers(fullRoot, dataDirectories, cancellationToken));

        // 8. Compute deterministic SHA-256 signature over detected artifacts
        result.Signature = ComputeSignature(fullRoot, signatureFiles);
        return result;
    }

    public static string ComputeSignature(string projectRoot, IEnumerable<string> artifactFilePaths)
    {
        var fullRoot = Path.GetFullPath(projectRoot);
        var entries = new List<(string Key, long Length, long LastWriteTicks)>();

        foreach (var rawPath in artifactFilePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(rawPath);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var info = new FileInfo(fullPath);
                var relative = Path.GetRelativePath(fullRoot, fullPath);
                var normalizedKey = relative.Replace('\\', '/').ToLowerInvariant();
                entries.Add((normalizedKey, info.Length, info.LastWriteTimeUtc.Ticks));
            }
            catch
            {
            }
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hasher, SignatureSchemaVersion);
        AppendUtf8(hasher, "\n");

        foreach (var entry in entries)
        {
            AppendUtf8(hasher, entry.Key);
            AppendUtf8(hasher, "|");
            AppendUtf8(hasher, entry.Length.ToString(CultureInfo.InvariantCulture));
            AppendUtf8(hasher, "|");
            AppendUtf8(hasher, entry.LastWriteTicks.ToString(CultureInfo.InvariantCulture));
            AppendUtf8(hasher, "\n");
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static void AppendUtf8(IncrementalHash hasher, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        hasher.AppendData(bytes);
    }

    private static List<string> DiscoverDataDirectories(string fullRoot)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string path)
        {
            if (Directory.Exists(path) && seen.Add(path))
            {
                directories.Add(path);
            }
        }

        var rootDirName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (rootDirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
        {
            TryAdd(fullRoot);
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullRoot, "*_Data", SearchOption.TopDirectoryOnly)
                .OrderBy(static d => d, StringComparer.OrdinalIgnoreCase))
            {
                TryAdd(dir);
            }
        }
        catch
        {
        }

        TryAdd(Path.Combine(fullRoot, "Data"));
        TryAdd(Path.Combine(fullRoot, "assets", "bin", "Data"));
        TryAdd(Path.Combine(fullRoot, "Contents", "Resources", "Data"));

        return directories;
    }

    private static string? DetectGameAssembly(string fullRoot, IReadOnlyList<string> dataDirectories)
    {
        var probeDirectories = new List<string>
        {
            fullRoot,
            Path.Combine(fullRoot, "lib", "arm64-v8a"),
            Path.Combine(fullRoot, "lib", "armeabi-v7a"),
            Path.Combine(fullRoot, "lib", "x86_64"),
            Path.Combine(fullRoot, "lib", "x86"),
            Path.Combine(fullRoot, "Contents", "Frameworks")
        };

        foreach (var dataDir in dataDirectories)
        {
            probeDirectories.Add(dataDir);
            probeDirectories.Add(Path.Combine(dataDir, "Native"));
        }

        foreach (var dir in probeDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var fileName in GameAssemblyNames)
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? DetectGlobalMetadata(string fullRoot, IReadOnlyList<string> dataDirectories)
    {
        var candidates = new List<string>();
        foreach (var dataDir in dataDirectories)
        {
            candidates.Add(Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat"));
            candidates.Add(Path.Combine(dataDir, "Managed", "Metadata", "global-metadata.dat"));
            candidates.Add(Path.Combine(dataDir, "Metadata", "global-metadata.dat"));
        }

        candidates.Add(Path.Combine(fullRoot, "il2cpp_data", "Metadata", "global-metadata.dat"));
        candidates.Add(Path.Combine(fullRoot, "Managed", "Metadata", "global-metadata.dat"));
        candidates.Add(Path.Combine(fullRoot, "global-metadata.dat"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<string> DetectManagedAssemblies(string fullRoot, IReadOnlyList<string> dataDirectories)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var managedDirs = new List<string>();
        foreach (var dataDir in dataDirectories)
        {
            managedDirs.Add(Path.Combine(dataDir, "Managed"));
        }

        if (managedDirs.Count == 0)
        {
            managedDirs.Add(Path.Combine(fullRoot, "Managed"));
        }

        foreach (var managedDir in managedDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(managedDir))
            {
                continue;
            }

            try
            {
                foreach (var dll in Directory.EnumerateFiles(managedDir, "*.dll", SearchOption.TopDirectoryOnly)
                    .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (seen.Add(dll))
                    {
                        results.Add(dll);
                    }
                }
            }
            catch
            {
            }
        }

        return results;
    }

    private static (string Version, string? SourcePath) DetectUnityVersion(string fullRoot, IReadOnlyList<string> dataDirectories)
    {
        var headerCandidates = new List<string>();
        foreach (var dataDir in dataDirectories)
        {
            headerCandidates.Add(Path.Combine(dataDir, "globalgamemanagers"));
            headerCandidates.Add(Path.Combine(dataDir, "globalgamemanagers.assets"));
            headerCandidates.Add(Path.Combine(dataDir, "data.unity3d"));
            headerCandidates.Add(Path.Combine(dataDir, "mainData"));
            headerCandidates.Add(Path.Combine(dataDir, "level0"));
            headerCandidates.Add(Path.Combine(dataDir, "sharedassets0.assets"));
        }

        headerCandidates.Add(Path.Combine(fullRoot, "globalgamemanagers"));
        headerCandidates.Add(Path.Combine(fullRoot, "data.unity3d"));
        headerCandidates.Add(Path.Combine(fullRoot, "mainData"));

        foreach (var candidate in headerCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = TryReadUnityVersionFromAssetHeader(candidate);
            if (!string.IsNullOrEmpty(version))
            {
                return (version, candidate);
            }
        }

        var unityPlayerDll = Path.Combine(fullRoot, "UnityPlayer.dll");
        if (File.Exists(unityPlayerDll))
        {
            var version = TryReadUnityVersionFromFileVersionInfo(unityPlayerDll);
            if (!string.IsNullOrEmpty(version))
            {
                return (version, unityPlayerDll);
            }
        }

        return (string.Empty, null);
    }

    internal static string? TryReadUnityVersionFromAssetHeader(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buffer = stackalloc byte[256];
            var read = stream.Read(buffer);
            if (read < 24)
            {
                return null;
            }

            var data = buffer.Slice(0, read);

            // Check UnityFS / UnityWeb / UnityRaw container header (e.g., data.unity3d)
            if (StartsWithAscii(data, "UnityFS\0")
                || StartsWithAscii(data, "UnityWeb\0")
                || StartsWithAscii(data, "UnityRaw\0"))
            {
                var offset = data.IndexOf((byte)0) + 1;
                if (offset + 4 >= data.Length)
                {
                    return null;
                }

                offset += 4; // uint32 format version
                var playerVersion = ReadNullTerminatedAscii(data, ref offset);
                var engineRevision = ReadNullTerminatedAscii(data, ref offset);

                if (LooksLikeConcreteUnityVersion(engineRevision))
                {
                    return engineRevision;
                }

                if (LooksLikeConcreteUnityVersion(playerVersion))
                {
                    return playerVersion;
                }

                return null;
            }

            // Check SerializedFile header (e.g., globalgamemanagers, mainData, level0)
            var formatVersion = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
            if (formatVersion < 7 || formatVersion > 64)
            {
                return null;
            }

            var versionOffset = formatVersion >= 22 ? 48 : (formatVersion >= 9 ? 20 : 16);
            if (versionOffset >= data.Length)
            {
                return null;
            }

            var offsetCopy = versionOffset;
            var unityVersion = ReadNullTerminatedAscii(data, ref offsetCopy);
            return LooksLikeConcreteUnityVersion(unityVersion) ? unityVersion : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadUnityVersionFromFileVersionInfo(string dllPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            var product = info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(product))
            {
                var underscoreIdx = product.IndexOf('_');
                var token = (underscoreIdx > 0 ? product.Substring(0, underscoreIdx) : product).Trim();
                if (LooksLikeConcreteUnityVersion(token))
                {
                    return token;
                }
            }

            if (info.FileMajorPart >= 4)
            {
                var numeric = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
                if (LooksLikeConcreteUnityVersion(numeric))
                {
                    return numeric;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static List<string> DetectAddressablesCatalogs(
        string fullRoot,
        IReadOnlyList<string> dataDirectories,
        CancellationToken cancellationToken)
    {
        var catalogs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectInDirectory(string rootDir, int maxDepth)
        {
            if (!Directory.Exists(rootDir))
            {
                return;
            }

            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((rootDir, 0));
            var visited = 0;

            while (queue.Count > 0 && visited < MaxDirectoryVisits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, depth) = queue.Dequeue();
                visited++;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(current, "catalog*.*", SearchOption.TopDirectoryOnly))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IsAddressablesCatalogFileName(fileName) && seen.Add(file))
                        {
                            catalogs.Add(file);
                        }
                    }

                    if (depth < maxDepth)
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(current))
                        {
                            queue.Enqueue((subDir, depth + 1));
                        }
                    }
                }
                catch
                {
                }
            }
        }

        // Priority: standard *_Data/StreamingAssets/aa locations
        foreach (var dataDir in dataDirectories)
        {
            CollectInDirectory(Path.Combine(dataDir, "StreamingAssets", "aa"), maxDepth: 4);
            CollectInDirectory(Path.Combine(dataDir, "StreamingAssets"), maxDepth: 3);
        }

        CollectInDirectory(Path.Combine(fullRoot, "StreamingAssets", "aa"), maxDepth: 4);
        CollectInDirectory(Path.Combine(fullRoot, "aa"), maxDepth: 3);

        // Fallback bounded shallow scan if no catalog found in standard StreamingAssets/aa paths
        if (catalogs.Count == 0)
        {
            CollectInDirectory(fullRoot, maxDepth: MaxSearchDepth - 1);
        }

        catalogs.Sort(StringComparer.OrdinalIgnoreCase);
        return catalogs;
    }

    private static bool IsAddressablesCatalogFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(stem, "catalog", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("catalog_", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("catalog-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("catalog.", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> DetectIl2CppDumperArtifacts(
        string fullRoot,
        IReadOnlyList<string> dataDirectories,
        IEnumerable<string>? additionalDirectories,
        CancellationToken cancellationToken)
    {
        var probeDirectories = new List<string>
        {
            fullRoot,
            Path.Combine(fullRoot, "Il2CppDumper"),
            Path.Combine(fullRoot, "il2cpp_dump"),
            Path.Combine(fullRoot, "dump"),
            Path.Combine(fullRoot, "DummyDll")
        };

        foreach (var dataDir in dataDirectories)
        {
            probeDirectories.Add(dataDir);
            probeDirectories.Add(Path.Combine(dataDir, "il2cpp_data"));
            probeDirectories.Add(Path.Combine(dataDir, "DummyDll"));
        }

        if (additionalDirectories != null)
        {
            foreach (var dir in additionalDirectories)
            {
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    probeDirectories.Add(dir);
                }
            }
        }

        var artifacts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in probeDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var artifactName in Il2CppDumperFileNames)
            {
                var candidate = Path.Combine(dir, artifactName);
                if (File.Exists(candidate) && seen.Add(candidate))
                {
                    artifacts.Add(candidate);
                }
            }
        }

        artifacts.Sort(StringComparer.OrdinalIgnoreCase);
        return artifacts;
    }

    private static List<StreamEncryptionBarrier> DetectStreamEncryptionBarriers(
        string fullRoot,
        IReadOnlyList<string> dataDirectories,
        CancellationToken cancellationToken)
    {
        var barriers = new List<StreamEncryptionBarrier>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] coreFiles =
        {
            "globalgamemanagers",
            "globalgamemanagers.assets",
            "level0",
            "resources.assets",
            "sharedassets0.assets"
        };

        var searchDirs = dataDirectories.Count > 0 ? dataDirectories : new[] { fullRoot };
        foreach (var dir in searchDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var name in coreFiles)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)
                    && seenFiles.Add(candidate)
                    && DecryptionHelper.InspectFileBarrier(candidate, out var barrier))
                {
                    barriers.Add(barrier);
                }
            }

            string[] vfsRoots =
            {
                Path.Combine(dir, "StreamingAssets", "VFS"),
                Path.Combine(dir, "Persistent", "VFS")
            };
            foreach (var vfsRoot in vfsRoots)
            {
                if (!Directory.Exists(vfsRoot))
                {
                    continue;
                }

                int vfsSamples = 0;
                foreach (var file in Directory.EnumerateFiles(vfsRoot, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (!string.Equals(ext, ".blc", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ext, ".chk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (seenFiles.Add(file) && DecryptionHelper.InspectFileBarrier(file, out var vfsBarrier))
                    {
                        barriers.Add(vfsBarrier);
                        vfsSamples++;
                        if (vfsSamples >= 4)
                        {
                            break;
                        }
                    }
                }
            }
        }

        return barriers;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (data[i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        var start = offset;
        var maxEnd = Math.Min(data.Length, start + 64);
        while (offset < maxEnd && data[offset] != 0)
        {
            offset++;
        }

        var length = offset - start;
        if (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        if (length <= 0)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(data.Slice(start, length));
    }

    private static bool LooksLikeConcreteUnityVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || value.Length > 40)
        {
            return false;
        }

        if (!char.IsDigit(value[0]))
        {
            return false;
        }

        var dotCount = 0;
        var hasWildcard = false;
        foreach (var c in value)
        {
            if (c == '.')
            {
                dotCount++;
            }
            else if (c == 'x' || c == 'X')
            {
                hasWildcard = true;
            }
            else if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return dotCount >= 2 && !hasWildcard;
    }

    private static string NormalizeFullPathForKey(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }
}
