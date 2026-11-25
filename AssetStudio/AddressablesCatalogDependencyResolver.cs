#nullable enable
using K4os.Compression.LZ4;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AssetStudio
{
    public sealed class CatalogBundleNode
    {
        public string InternalId { get; init; } = string.Empty;
        public string BundleFileName { get; init; } = string.Empty;
        public string BundleName { get; set; } = string.Empty;
        public string PrimaryKey { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Crc { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public List<string> DirectDependencyInternalIds { get; } = new List<string>();
    }

    public sealed class CatalogAssetEntry
    {
        public string PrimaryKey { get; init; } = string.Empty;
        public string InternalId { get; init; } = string.Empty;
        public string ProviderId { get; init; } = string.Empty;
        public string PrimaryBundleInternalId { get; init; } = string.Empty;
        public string PrimaryBundleFileName { get; init; } = string.Empty;
        public List<string> DependencyBundleInternalIds { get; } = new List<string>();
        public List<string> Keys { get; } = new List<string>();
        public List<string> Guids { get; } = new List<string>();
        public bool IsScene { get; init; }
    }

    public sealed class ParsedAddressablesCatalog
    {
        public string CatalogPath { get; init; } = string.Empty;
        public string CatalogDirectory { get; init; } = string.Empty;
        public string Format { get; init; } = string.Empty;
        public string LocatorId { get; set; } = string.Empty;
        public List<CatalogBundleNode> Bundles { get; } = new List<CatalogBundleNode>();
        public List<string> Providers { get; } = new List<string>();
        public List<CatalogAssetEntry> AssetEntries { get; } = new List<CatalogAssetEntry>();
        public List<CatalogAssetEntry> SceneEntries { get; } = new List<CatalogAssetEntry>();

        public Dictionary<string, List<CatalogAssetEntry>> ByKey { get; } =
            new Dictionary<string, List<CatalogAssetEntry>>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<CatalogAssetEntry>> ByGuid { get; } =
            new Dictionary<string, List<CatalogAssetEntry>>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, CatalogBundleNode> ByInternalId { get; } =
            new Dictionary<string, CatalogBundleNode>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, List<CatalogBundleNode>> ByFileName { get; } =
            new Dictionary<string, List<CatalogBundleNode>>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, List<CatalogBundleNode>> ByBundleKey { get; } =
            new Dictionary<string, List<CatalogBundleNode>>(StringComparer.OrdinalIgnoreCase);

        internal string[] ImmediateSubdirectories { get; set; } = Array.Empty<string>();

        private readonly HashSet<CatalogAssetEntry> _indexedAssetEntries =
            new HashSet<CatalogAssetEntry>(ReferenceEqualityComparer.Instance);

        public bool TryGetEntriesByGuid(string? assetGuid, out IReadOnlyList<CatalogAssetEntry> entries)
        {
            if (AddressablesCatalogDependencyResolver.TryNormalizeAssetGuid(assetGuid, out var normalized)
                && ByGuid.TryGetValue(normalized, out var list)
                && list.Count > 0)
            {
                entries = list;
                return true;
            }

            entries = Array.Empty<CatalogAssetEntry>();
            return false;
        }

        public bool TryGetEntriesByKey(string? key, out IReadOnlyList<CatalogAssetEntry> entries)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && ByKey.TryGetValue(key.Trim(), out var list)
                && list.Count > 0)
            {
                entries = list;
                return true;
            }

            entries = Array.Empty<CatalogAssetEntry>();
            return false;
        }

        public void IndexAssetEntry(CatalogAssetEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            // O(1) duplicate guard: when indexing a fresh entry for the first time, any duplicate key
            // within this entry can only match list[^1], avoiding O(N^2) scans on large shared label buckets.
            var isFirstTime = _indexedAssetEntries.Add(entry);
            if (isFirstTime)
            {
                AssetEntries.Add(entry);
                if (entry.IsScene)
                {
                    SceneEntries.Add(entry);
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.PrimaryKey))
            {
                AddKeyOrGuid(entry, entry.PrimaryKey, isFirstTime);
            }

            if (!string.IsNullOrWhiteSpace(entry.InternalId))
            {
                AddAssetMultiLookup(ByKey, entry.InternalId, entry, isFirstTime);
            }

            for (int i = 0; i < entry.Keys.Count; i++)
            {
                AddKeyOrGuid(entry, entry.Keys[i], isFirstTime);
            }

            for (int i = 0; i < entry.Guids.Count; i++)
            {
                if (AddressablesCatalogDependencyResolver.TryNormalizeAssetGuid(entry.Guids[i], out var normalizedGuid))
                {
                    AddAssetMultiLookup(ByGuid, normalizedGuid, entry, isFirstTime);
                    AddAssetMultiLookup(ByKey, normalizedGuid, entry, isFirstTime);
                }
            }
        }

        private void AddKeyOrGuid(CatalogAssetEntry entry, string rawKey, bool isFirstTime)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return;
            }

            var trimmed = rawKey.Trim();
            AddAssetMultiLookup(ByKey, trimmed, entry, isFirstTime);
            if (AddressablesCatalogDependencyResolver.TryNormalizeAssetGuid(trimmed, out var normalizedGuid))
            {
                if (!entry.Guids.Contains(normalizedGuid, StringComparer.OrdinalIgnoreCase))
                {
                    entry.Guids.Add(normalizedGuid);
                }
                AddAssetMultiLookup(ByGuid, normalizedGuid, entry, isFirstTime);
            }
        }

        private static void AddAssetMultiLookup(
            Dictionary<string, List<CatalogAssetEntry>> dict,
            string key,
            CatalogAssetEntry entry,
            bool isFirstTime)
        {
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<CatalogAssetEntry>(1);
                dict[key] = list;
                list.Add(entry);
                return;
            }

            if (isFirstTime)
            {
                if (list.Count == 0 || !ReferenceEquals(list[list.Count - 1], entry))
                {
                    list.Add(entry);
                }
            }
            else if (!list.Contains(entry))
            {
                list.Add(entry);
            }
        }

        internal void IndexNode(CatalogBundleNode node)
        {
            if (string.IsNullOrWhiteSpace(node.InternalId))
            {
                return;
            }

            if (!ByInternalId.ContainsKey(node.InternalId))
            {
                ByInternalId[node.InternalId] = node;
                Bundles.Add(node);
            }

            if (!string.IsNullOrWhiteSpace(node.BundleFileName))
            {
                AddMultiLookup(ByFileName, node.BundleFileName, node);
            }

            if (!string.IsNullOrWhiteSpace(node.BundleName))
            {
                AddMultiLookup(ByBundleKey, node.BundleName, node);
                if (!node.BundleName.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase))
                {
                    AddMultiLookup(ByBundleKey, "CAB-" + node.BundleName, node);
                }
            }

            if (!string.IsNullOrWhiteSpace(node.PrimaryKey))
            {
                AddMultiLookup(ByBundleKey, node.PrimaryKey, node);
                if (!node.PrimaryKey.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase))
                {
                    AddMultiLookup(ByBundleKey, "CAB-" + node.PrimaryKey, node);
                }
            }
        }

        private static void AddMultiLookup(
            Dictionary<string, List<CatalogBundleNode>> dict,
            string key,
            CatalogBundleNode node)
        {
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<CatalogBundleNode>(1);
                dict[key] = list;
            }

            if (!list.Contains(node))
            {
                list.Add(node);
            }
        }
    }

    public static class AddressablesCatalogDependencyResolver
    {
        public const int DefaultMaxDependencyBundles = 64;
        public const uint BinaryCatalogMagic = 0x0DE38942u;
        public const long UnreferencedLargeDependencyThresholdBytes = 32L * 1024 * 1024;

        private const uint UnicodeStringFlag = 0x80000000u;
        private const uint DynamicStringFlag = 0x40000000u;
        private const uint ClearStringFlagsMask = 0x3FFFFFFFu;
        private const int MaxParentDirectoryWalkDepth = 4;
        private const int MaxFallbackHeaderProbeBundles = 48;
        private const long MaxCatalogFileBytes = 256L * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, ParsedAddressablesCatalog Catalog)> CatalogCache =
            new ConcurrentDictionary<string, (DateTime, long, ParsedAddressablesCatalog)>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, string[] NodeNames)> BundleNodeCache =
            new ConcurrentDictionary<string, (DateTime, long, string[])>(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            CatalogCache.Clear();
            BundleNodeCache.Clear();
        }

        public static List<string> ResolveDependencies(
            IEnumerable<string> inputFilePaths,
            int maxDependencies = DefaultMaxDependencyBundles,
            string? projectRoot = null)
        {
            var result = new List<string>();
            if (inputFilePaths == null || maxDependencies <= 0)
            {
                return result;
            }

            var initialPaths = new List<string>();
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in inputFilePaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(rawPath);
                }
                catch
                {
                    continue;
                }

                if (visitedPaths.Add(fullPath))
                {
                    initialPaths.Add(fullPath);
                    var fileName = Path.GetFileName(fullPath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        visitedFileNames.Add(fileName);
                    }
                }
            }

            if (initialPaths.Count == 0)
            {
                return result;
            }

            var catalogs = LoadCatalogsForFiles(initialPaths, projectRoot);
            var queue = new Queue<string>(initialPaths);

            while (queue.Count > 0 && result.Count < maxDependencies)
            {
                var currentPath = queue.Dequeue();
                var currentDir = Path.GetDirectoryName(currentPath) ?? string.Empty;
                var currentFileName = Path.GetFileName(currentPath);

                foreach (var catalog in catalogs)
                {
                    var matchingNodes = FindMatchingNodes(catalog, currentPath, currentFileName, currentDir);
                    foreach (var node in matchingNodes)
                    {
                        foreach (var depInternalId in node.DirectDependencyInternalIds)
                        {
                            if (result.Count >= maxDependencies)
                            {
                                break;
                            }

                            var resolvedDepPath = TryResolveLocalBundlePath(catalog, depInternalId, currentDir);
                            if (string.IsNullOrEmpty(resolvedDepPath))
                            {
                                continue;
                            }

                            string fullDepPath;
                            try
                            {
                                fullDepPath = Path.GetFullPath(resolvedDepPath);
                            }
                            catch
                            {
                                continue;
                            }

                            var depFileName = Path.GetFileName(fullDepPath);
                            if (visitedPaths.Contains(fullDepPath)
                                || (!string.IsNullOrEmpty(depFileName) && visitedFileNames.Contains(depFileName)))
                            {
                                continue;
                            }

                            visitedPaths.Add(fullDepPath);
                            if (!string.IsNullOrEmpty(depFileName))
                            {
                                visitedFileNames.Add(depFileName);
                            }

                            result.Add(fullDepPath);
                            queue.Enqueue(fullDepPath);
                        }
                    }
                }

                // Also check Unity .manifest sidecar files if present
                if (result.Count < maxDependencies)
                {
                    foreach (var manifestDepPath in ResolveManifestDependencies(currentPath, currentDir))
                    {
                        if (result.Count >= maxDependencies)
                        {
                            break;
                        }

                        var depFileName = Path.GetFileName(manifestDepPath);
                        if (visitedPaths.Contains(manifestDepPath)
                            || (!string.IsNullOrEmpty(depFileName) && visitedFileNames.Contains(depFileName)))
                        {
                            continue;
                        }

                        visitedPaths.Add(manifestDepPath);
                        if (!string.IsNullOrEmpty(depFileName))
                        {
                            visitedFileNames.Add(depFileName);
                        }

                        result.Add(manifestDepPath);
                        queue.Enqueue(manifestDepPath);
                    }
                }
            }

            return result;
        }

        public static List<string> ResolveDependenciesForLoadedFiles(
            IReadOnlyList<string> initialFilePaths,
            IReadOnlyList<SerializedFile> loadedSerializedFiles,
            ISet<string> loadedSerializedFileNames,
            int maxDependencies = DefaultMaxDependencyBundles,
            string? projectRoot = null)
        {
            var finalToLoad = new List<string>();
            if (initialFilePaths == null || initialFilePaths.Count == 0 || maxDependencies <= 0)
            {
                return finalToLoad;
            }

            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in initialFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    var full = Path.GetFullPath(path);
                    visitedPaths.Add(full);
                    var fn = Path.GetFileName(full);
                    if (!string.IsNullOrEmpty(fn))
                    {
                        visitedFileNames.Add(fn);
                    }
                }
                catch
                {
                }
            }

            var unresolvedExternals = CollectUnresolvedExternalNames(loadedSerializedFiles, loadedSerializedFileNames);
            var catalogCandidates = ResolveDependencies(initialFilePaths, maxDependencies, projectRoot);

            if (catalogCandidates.Count > 0)
            {
                if (unresolvedExternals.Count > 0)
                {
                    // Check which catalog candidates directly match the loaded files' unresolved m_Externals
                    var directExternalMatches = new List<string>();
                    var nonExternalCandidates = new List<string>();
                    var matchedAnyExternalByCab = false;

                    foreach (var candidatePath in catalogCandidates)
                    {
                        if (DoesBundleMatchAnyExternal(candidatePath, unresolvedExternals, out var readCabNodes))
                        {
                            directExternalMatches.Add(candidatePath);
                            if (readCabNodes)
                            {
                                matchedAnyExternalByCab = true;
                            }
                        }
                        else
                        {
                            nonExternalCandidates.Add(candidatePath);
                        }
                    }

                    foreach (var match in directExternalMatches)
                    {
                        if (finalToLoad.Count >= maxDependencies)
                        {
                            break;
                        }

                        AddUniqueCandidate(match, visitedPaths, visitedFileNames, finalToLoad);
                    }

                    foreach (var candidatePath in nonExternalCandidates)
                    {
                        if (finalToLoad.Count >= maxDependencies)
                        {
                            break;
                        }

                        // If at least one catalog candidate matched the initial bundle's exact CAB m_Externals,
                        // skip unreferenced sibling bundles that exceed the large-bundle size threshold
                        // (avoids pulling in 400+ MB unreferenced group-cycle bundles).
                        if (matchedAnyExternalByCab && TryGetFileLength(candidatePath, out var fileLength)
                            && fileLength > UnreferencedLargeDependencyThresholdBytes)
                        {
                            continue;
                        }

                        AddUniqueCandidate(candidatePath, visitedPaths, visitedFileNames, finalToLoad);
                    }
                }
                else
                {
                    foreach (var candidatePath in catalogCandidates)
                    {
                        if (finalToLoad.Count >= maxDependencies)
                        {
                            break;
                        }

                        AddUniqueCandidate(candidatePath, visitedPaths, visitedFileNames, finalToLoad);
                    }
                }
            }

            // Fallback resolution for any remaining unresolved m_Externals (when no catalog exists or an external wasn't in the dependency bucket)
            if (unresolvedExternals.Count > 0 && finalToLoad.Count < maxDependencies)
            {
                var remainingExternals = new HashSet<string>(unresolvedExternals, StringComparer.OrdinalIgnoreCase);
                foreach (var loadedCandidate in finalToLoad)
                {
                    if (TryReadUnityFsDirectoryNodeNames(loadedCandidate, out var nodeNames))
                    {
                        foreach (var nodeName in nodeNames)
                        {
                            remainingExternals.Remove(nodeName);
                        }
                    }
                }

                if (remainingExternals.Count > 0)
                {
                    var fallbackMatches = ResolveFallbackExternalDependencies(
                        initialFilePaths,
                        remainingExternals,
                        visitedPaths: null,
                        visitedFileNames: null,
                        maxDependencies - finalToLoad.Count,
                        projectRoot);

                    foreach (var match in fallbackMatches)
                    {
                        if (finalToLoad.Count >= maxDependencies)
                        {
                            break;
                        }

                        AddUniqueCandidate(match, visitedPaths, visitedFileNames, finalToLoad);
                    }
                }
            }

            return finalToLoad;
        }

        public static List<string> ResolveFallbackExternalDependencies(
            IEnumerable<string> referenceBundlePaths,
            ISet<string> unresolvedExternalNames,
            ISet<string>? visitedPaths = null,
            ISet<string>? visitedFileNames = null,
            int maxResults = DefaultMaxDependencyBundles,
            string? projectRoot = null)
        {
            var results = new List<string>();
            if (referenceBundlePaths == null || unresolvedExternalNames == null || unresolvedExternalNames.Count == 0 || maxResults <= 0)
            {
                return results;
            }

            visitedPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            visitedFileNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var refPaths = referenceBundlePaths
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .Select(static p =>
                {
                    try { return Path.GetFullPath(p); }
                    catch { return string.Empty; }
                })
                .Where(static p => !string.IsNullOrEmpty(p))
                .ToList();

            if (refPaths.Count == 0)
            {
                return results;
            }

            var firstDir = Path.GetDirectoryName(refPaths[0]) ?? string.Empty;

            // 1. Check catalog BundleName / PrimaryKey map if a catalog exists
            var catalogs = LoadCatalogsForFiles(refPaths, projectRoot);
            foreach (var catalog in catalogs)
            {
                foreach (var externalName in unresolvedExternalNames)
                {
                    if (results.Count >= maxResults)
                    {
                        return results;
                    }

                    var strippedCab = StripCabPrefix(externalName);
                    if (catalog.ByBundleKey.TryGetValue(externalName, out var nodes)
                        || (!string.IsNullOrEmpty(strippedCab) && catalog.ByBundleKey.TryGetValue(strippedCab, out nodes)))
                    {
                        foreach (var node in nodes)
                        {
                            var resolved = TryResolveLocalBundlePath(catalog, node.InternalId, firstDir);
                            if (!string.IsNullOrEmpty(resolved))
                            {
                                AddUniqueCandidate(resolved, visitedPaths, visitedFileNames, results);
                                if (results.Count >= maxResults)
                                {
                                    return results;
                                }
                            }
                        }
                    }
                }
            }

            // 2. Check sibling files in the same directory by filename / hash match, then bounded header probe
            var directories = refPaths
                .Select(static p => Path.GetDirectoryName(p) ?? string.Empty)
                .Where(static d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in directories)
            {
                string[] siblingFiles;
                try
                {
                    siblingFiles = Directory.GetFiles(dir);
                }
                catch
                {
                    continue;
                }

                // Fast filename / hash token match (O(1) disk reads)
                foreach (var ext in unresolvedExternalNames)
                {
                    if (results.Count >= maxResults)
                    {
                        return results;
                    }

                    var stripped = StripCabPrefix(ext);
                    foreach (var sibling in siblingFiles)
                    {
                        if (!IsLikelyBundleFile(sibling))
                        {
                            continue;
                        }

                        var siblingName = Path.GetFileName(sibling);
                        var siblingNoExt = Path.GetFileNameWithoutExtension(sibling);
                        if (string.Equals(siblingName, ext, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(siblingNoExt, ext, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(stripped) && stripped.Length >= 16
                                && siblingName.Contains(stripped, StringComparison.OrdinalIgnoreCase)))
                        {
                            AddUniqueCandidate(sibling, visitedPaths, visitedFileNames, results);
                            if (results.Count >= maxResults)
                            {
                                return results;
                            }
                        }
                    }
                }

                // Bounded UnityFS directory-node header probe when directory has a modest number of bundles
                // (never does an O(N) brute-force scan across thousands of bundles)
                var bundleSiblings = siblingFiles.Where(IsLikelyBundleFile).ToArray();
                if (bundleSiblings.Length <= MaxFallbackHeaderProbeBundles)
                {
                    foreach (var sibling in bundleSiblings)
                    {
                        if (results.Count >= maxResults)
                        {
                            return results;
                        }

                        var fullSibling = Path.GetFullPath(sibling);
                        if (visitedPaths.Contains(fullSibling))
                        {
                            continue;
                        }

                        if (DoesBundleMatchAnyExternal(fullSibling, unresolvedExternalNames, out _))
                        {
                            AddUniqueCandidate(fullSibling, visitedPaths, visitedFileNames, results);
                        }
                    }
                }
            }

            return results;
        }

        public static HashSet<string> CollectUnresolvedExternalNames(
            IEnumerable<SerializedFile> loadedFiles,
            ISet<string>? loadedFileNames = null)
        {
            var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (loadedFiles == null)
            {
                return unresolved;
            }

            var knownLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (loadedFileNames != null)
            {
                foreach (var name in loadedFileNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        knownLoaded.Add(name);
                    }
                }
            }

            var filesList = loadedFiles as IList<SerializedFile> ?? loadedFiles.ToList();
            foreach (var file in filesList)
            {
                if (file == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(file.fileName))
                {
                    knownLoaded.Add(file.fileName);
                }
            }

            foreach (var file in filesList)
            {
                if (file?.m_Externals == null)
                {
                    continue;
                }

                foreach (var external in file.m_Externals)
                {
                    if (external == null)
                    {
                        continue;
                    }

                    var extName = NormalizeExternalFileName(external.fileName, external.pathName);
                    if (string.IsNullOrWhiteSpace(extName)
                        || extName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                        || extName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!knownLoaded.Contains(extName))
                    {
                        unresolved.Add(extName);
                    }
                }
            }

            return unresolved;
        }

        public static bool TryReadUnityFsDirectoryNodeNames(string bundlePath, out string[] nodeNames)
        {
            nodeNames = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                return false;
            }

            string fullPath;
            FileInfo fi;
            try
            {
                fullPath = Path.GetFullPath(bundlePath);
                fi = new FileInfo(fullPath);
            }
            catch
            {
                return false;
            }

            if (BundleNodeCache.TryGetValue(fullPath, out var cached)
                && cached.LastWriteUtc == fi.LastWriteTimeUtc
                && cached.Length == fi.Length)
            {
                nodeNames = cached.NodeNames;
                return nodeNames.Length > 0;
            }

            try
            {
                using var reader = new FileReader(fullPath);
                if (reader.FileType != FileType.BundleFile)
                {
                    return false;
                }

                var signature = reader.ReadStringToNull(32);
                var version = reader.ReadUInt32();
                _ = reader.ReadStringToNull(64); // unityVersion
                _ = reader.ReadStringToNull(64); // unityRevision

                if (signature != "UnityFS" && !(version == 6 && (signature == "UnityWeb" || signature == "UnityRaw")))
                {
                    return false;
                }

                _ = reader.ReadInt64(); // bundleSize
                var compressedBlocksInfoSize = reader.ReadUInt32();
                var uncompressedBlocksInfoSize = reader.ReadUInt32();
                var flags = (ArchiveFlags)reader.ReadUInt32();
                if (signature != "UnityFS")
                {
                    reader.ReadByte();
                }

                if (version >= 7)
                {
                    reader.AlignStream(16);
                }

                if (compressedBlocksInfoSize == 0
                    || compressedBlocksInfoSize > 16 * 1024 * 1024
                    || compressedBlocksInfoSize > reader.BaseStream.Length
                    || uncompressedBlocksInfoSize == 0
                    || uncompressedBlocksInfoSize > 32 * 1024 * 1024)
                {
                    return false;
                }

                var compSize = (int)compressedBlocksInfoSize;
                byte[] blocksInfoBytes;
                if ((flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                {
                    reader.Position = reader.BaseStream.Length - compSize;
                    blocksInfoBytes = reader.ReadBytes(compSize);
                }
                else
                {
                    if (compSize > reader.BaseStream.Length - reader.Position)
                    {
                        return false;
                    }
                    blocksInfoBytes = reader.ReadBytes(compSize);
                }

                if (blocksInfoBytes.Length != compSize)
                {
                    return false;
                }

                byte[] uncompressedBytes;
                var compressionType = (CompressionType)(flags & ArchiveFlags.CompressionTypeMask);
                switch (compressionType)
                {
                    case CompressionType.None:
                        uncompressedBytes = blocksInfoBytes;
                        break;
                    case CompressionType.Lz4:
                    case CompressionType.Lz4HC:
                        uncompressedBytes = new byte[uncompressedBlocksInfoSize];
                        var decoded = LZ4Codec.Decode(blocksInfoBytes, uncompressedBytes);
                        if (decoded != (int)uncompressedBlocksInfoSize)
                        {
                            return false;
                        }
                        break;
                    case CompressionType.Lzma:
                        using (var inMs = new MemoryStream(blocksInfoBytes))
                        using (var outMs = new MemoryStream((int)uncompressedBlocksInfoSize))
                        {
                            SevenZipHelper.StreamDecompress(inMs, outMs, compressedBlocksInfoSize, uncompressedBlocksInfoSize);
                            uncompressedBytes = outMs.ToArray();
                        }
                        break;
                    default:
                        return false;
                }

                using var infoStream = new MemoryStream(uncompressedBytes, writable: false);
                using var infoReader = new EndianBinaryReader(infoStream);
                if (infoStream.Length < 24)
                {
                    return false;
                }

                infoReader.Position = 16; // skip 16-byte uncompressedDataHash
                var blocksCount = infoReader.ReadInt32();
                if (blocksCount < 0 || (long)blocksCount * 10 > infoStream.Length - infoStream.Position)
                {
                    return false;
                }

                infoReader.Position += (long)blocksCount * 10;
                if (infoStream.Length - infoStream.Position < 4)
                {
                    return false;
                }

                var nodesCount = infoReader.ReadInt32();
                if (nodesCount < 0 || nodesCount > 65536 || (long)nodesCount * 21 > infoStream.Length - infoStream.Position)
                {
                    return false;
                }

                var names = new List<string>(nodesCount);
                for (int i = 0; i < nodesCount; i++)
                {
                    _ = infoReader.ReadInt64(); // offset
                    _ = infoReader.ReadInt64(); // size
                    _ = infoReader.ReadUInt32(); // flags
                    var path = infoReader.ReadStringToNull(1024);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var leaf = Path.GetFileName(path.Replace('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(leaf)
                        && !leaf.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                        && !leaf.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(leaf);
                    }
                }

                nodeNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                BundleNodeCache[fullPath] = (fi.LastWriteTimeUtc, fi.Length, nodeNames);
                return nodeNames.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static List<string> FindCatalogPathsForFiles(IEnumerable<string> filePaths, string? projectRoot = null)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ProbeDirectoryHierarchy(string? startPath)
            {
                if (string.IsNullOrWhiteSpace(startPath))
                {
                    return;
                }

                string? currentDir;
                try
                {
                    var full = Path.GetFullPath(startPath);
                    currentDir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                }
                catch
                {
                    return;
                }

                for (int depth = 0; depth <= MaxParentDirectoryWalkDepth && !string.IsNullOrEmpty(currentDir); depth++)
                {
                    if (visitedDirs.Add(currentDir))
                    {
                        ProbeSingleDirectoryForCatalogs(currentDir, found, seen);

                        var aaDir = Path.Combine(currentDir, "StreamingAssets", "aa");
                        if (Directory.Exists(aaDir) && visitedDirs.Add(aaDir))
                        {
                            ProbeSingleDirectoryForCatalogs(aaDir, found, seen);
                        }

                        // When opening a Unity game's root folder (parent of *_Data), also probe *_Data/StreamingAssets/aa.
                        if (depth == 0)
                        {
                            try
                            {
                                foreach (var dataDir in Directory.EnumerateDirectories(currentDir, "*_Data", SearchOption.TopDirectoryOnly))
                                {
                                    if (visitedDirs.Add(dataDir))
                                    {
                                        ProbeSingleDirectoryForCatalogs(dataDir, found, seen);
                                    }

                                    var dataAaDir = Path.Combine(dataDir, "StreamingAssets", "aa");
                                    if (Directory.Exists(dataAaDir) && visitedDirs.Add(dataAaDir))
                                    {
                                        ProbeSingleDirectoryForCatalogs(dataAaDir, found, seen);
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }

                    try
                    {
                        currentDir = Path.GetDirectoryName(currentDir);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            if (filePaths != null)
            {
                foreach (var file in filePaths)
                {
                    ProbeDirectoryHierarchy(file);
                }
            }

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                ProbeDirectoryHierarchy(projectRoot);
            }

            return found;
        }

        private static void ProbeSingleDirectoryForCatalogs(
            string directory,
            List<string> found,
            HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var jsonExact = Path.Combine(directory, "catalog.json");
            if (File.Exists(jsonExact) && seen.Add(jsonExact))
            {
                found.Add(jsonExact);
            }

            var binExact = Path.Combine(directory, "catalog.bin");
            if (File.Exists(binExact) && seen.Add(binExact))
            {
                found.Add(binExact);
            }

            try
            {
                foreach (var candidate in Directory.EnumerateFiles(directory, "catalog*.json", SearchOption.TopDirectoryOnly))
                {
                    if (seen.Add(candidate))
                    {
                        found.Add(candidate);
                    }
                }

                foreach (var candidate in Directory.EnumerateFiles(directory, "catalog*.bin", SearchOption.TopDirectoryOnly))
                {
                    if (seen.Add(candidate))
                    {
                        found.Add(candidate);
                    }
                }
            }
            catch
            {
            }
        }

        public static List<ParsedAddressablesCatalog> LoadCatalogsForFiles(IEnumerable<string> filePaths, string? projectRoot = null)
        {
            var catalogPaths = FindCatalogPathsForFiles(filePaths, projectRoot);
            var parsedList = new List<ParsedAddressablesCatalog>(catalogPaths.Count);
            foreach (var catalogPath in catalogPaths)
            {
                if (TryLoadCatalog(catalogPath, out var parsed) && parsed != null)
                {
                    parsedList.Add(parsed);
                }
            }

            return parsedList;
        }

        public static bool TryLoadCatalog(string catalogPath, out ParsedAddressablesCatalog? catalog)
        {
            catalog = null;
            if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
            {
                return false;
            }

            string fullPath;
            FileInfo fileInfo;
            try
            {
                fullPath = Path.GetFullPath(catalogPath);
                fileInfo = new FileInfo(fullPath);
            }
            catch
            {
                return false;
            }

            if (fileInfo.Length <= 0 || fileInfo.Length > MaxCatalogFileBytes)
            {
                return false;
            }

            if (CatalogCache.TryGetValue(fullPath, out var cached)
                && cached.LastWriteUtc == fileInfo.LastWriteTimeUtc
                && cached.Length == fileInfo.Length)
            {
                catalog = cached.Catalog;
                return true;
            }

            bool ok = string.Equals(Path.GetExtension(fullPath), ".bin", StringComparison.OrdinalIgnoreCase)
                ? TryParseBinaryCatalog(fullPath, out catalog)
                : TryParseJsonCatalog(fullPath, out catalog);

            if (ok && catalog != null)
            {
                CatalogCache[fullPath] = (fileInfo.LastWriteTimeUtc, fileInfo.Length, catalog);
                return true;
            }

            return false;
        }

        internal static int TryExtractBlock512BundleMetadata(
            string catalogPath,
            Action<string, string, string, string> onBundle)
        {
            if (string.IsNullOrWhiteSpace(catalogPath) || onBundle == null || !File.Exists(catalogPath))
            {
                return 0;
            }

            string fullPath;
            FileInfo fileInfo;
            try
            {
                fullPath = Path.GetFullPath(catalogPath);
                fileInfo = new FileInfo(fullPath);
            }
            catch
            {
                return 0;
            }

            if (fileInfo.Length <= 0 || fileInfo.Length > MaxCatalogFileBytes)
            {
                return 0;
            }

            // If full catalog is already cached and fresh, reuse it in <1ms.
            if (CatalogCache.TryGetValue(fullPath, out var cached)
                && cached.LastWriteUtc == fileInfo.LastWriteTimeUtc
                && cached.Length == fileInfo.Length)
            {
                int fromCache = 0;
                var bundles = cached.Catalog.Bundles;
                for (int i = 0; i < bundles.Count; i++)
                {
                    var node = bundles[i];
                    if (!string.IsNullOrEmpty(node.BundleFileName)
                        && !string.IsNullOrEmpty(node.BundleName)
                        && !string.IsNullOrEmpty(node.Hash))
                    {
                        onBundle(node.BundleFileName, node.BundleName, node.Hash, string.IsNullOrEmpty(node.Crc) ? "0" : node.Crc);
                        fromCache++;
                    }
                }
                return fromCache;
            }

            // Binary catalogs: use standard parser (already fast and cached).
            if (string.Equals(Path.GetExtension(fullPath), ".bin", StringComparison.OrdinalIgnoreCase))
            {
                int fromBin = 0;
                if (TryLoadCatalog(fullPath, out var parsed) && parsed != null)
                {
                    for (int i = 0; i < parsed.Bundles.Count; i++)
                    {
                        var node = parsed.Bundles[i];
                        if (!string.IsNullOrEmpty(node.BundleFileName)
                            && !string.IsNullOrEmpty(node.BundleName)
                            && !string.IsNullOrEmpty(node.Hash))
                        {
                            onBundle(node.BundleFileName, node.BundleName, node.Hash, string.IsNullOrEmpty(node.Crc) ? "0" : node.Crc);
                            fromBin++;
                        }
                    }
                }
                return fromBin;
            }

            // JSON catalogs: lightweight bundle-token extraction skipping m_KeyDataString, m_BucketDataString,
            // dependency edges, and tens of thousands of non-bundle CatalogAssetEntry records.
            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                });

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return 0;
                }

                var root = doc.RootElement;
                var entryBytes = TryDecodeBase64Property(root, "m_EntryDataString");
                var extraBytes = TryDecodeBase64Property(root, "m_ExtraDataString");
                if (entryBytes == null || entryBytes.Length < 4 || extraBytes == null || extraBytes.Length == 0)
                {
                    return 0;
                }

                var internalIds = ReadJsonStringArrayProperty(root, "m_InternalIds");
                if (internalIds.Count == 0)
                {
                    return 0;
                }

                var prefixes = ReadJsonStringArrayProperty(root, "m_InternalIdPrefixes");
                int entryCount = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(0, 4));
                int maxEntries = (entryBytes.Length - 4) / 28;
                int count = Math.Min(entryCount, maxEntries);
                int added = 0;

                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * 28;
                    int dataIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 16, 4));
                    if (dataIdx < 0)
                    {
                        continue;
                    }

                    int internalIdIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset, 4));
                    if (internalIdIdx < 0 || internalIdIdx >= internalIds.Count)
                    {
                        continue;
                    }

                    var extraJson = ReadSerializedExtraJson(extraBytes, dataIdx);
                    if (string.IsNullOrWhiteSpace(extraJson))
                    {
                        continue;
                    }

                    TryParseExtraBundleOptions(extraJson, out var bundleName, out var hash, out var crc);
                    if (string.IsNullOrEmpty(bundleName) || string.IsNullOrEmpty(hash))
                    {
                        continue;
                    }

                    var expandedId = ExpandInternalId(internalIds[internalIdIdx], prefixes);
                    var fileName = ExtractBundleFileName(expandedId);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        continue;
                    }

                    onBundle(fileName, bundleName, hash, string.IsNullOrEmpty(crc) ? "0" : crc);
                    added++;
                }

                return added;
            }
            catch
            {
                return 0;
            }
        }

        public static bool TryParseJsonCatalog(string catalogPath, out ParsedAddressablesCatalog? catalog)
        {
            catalog = null;
            if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(catalogPath);
                var catalogDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                });

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var root = doc.RootElement;
                var parsed = new ParsedAddressablesCatalog
                {
                    CatalogPath = fullPath,
                    CatalogDirectory = catalogDir,
                    Format = "json",
                    LocatorId = ReadJsonStringProperty(root, "m_LocatorId") ?? string.Empty,
                    ImmediateSubdirectories = GetImmediateSubdirectoriesSafe(catalogDir)
                };

                var providerIds = ReadJsonStringArrayProperty(root, "m_ProviderIds");
                var internalIds = ReadJsonStringArrayProperty(root, "m_InternalIds");
                var prefixes = ReadJsonStringArrayProperty(root, "m_InternalIdPrefixes");

                foreach (var provider in providerIds)
                {
                    if (!string.IsNullOrWhiteSpace(provider) && !parsed.Providers.Contains(provider, StringComparer.Ordinal))
                    {
                        parsed.Providers.Add(provider);
                    }
                }

                if (internalIds.Count == 0)
                {
                    catalog = parsed;
                    return true;
                }

                var expandedInternalIds = new string[internalIds.Count];
                for (int i = 0; i < internalIds.Count; i++)
                {
                    expandedInternalIds[i] = ExpandInternalId(internalIds[i], prefixes);
                }

                var keyBytes = TryDecodeBase64Property(root, "m_KeyDataString");
                var bucketBytes = TryDecodeBase64Property(root, "m_BucketDataString");
                var entryBytes = TryDecodeBase64Property(root, "m_EntryDataString");
                var extraBytes = TryDecodeBase64Property(root, "m_ExtraDataString");

                var buckets = ParseBuckets(bucketBytes, keyBytes);
                var entries = ParseJsonEntries(entryBytes, extraBytes, expandedInternalIds, providerIds, buckets);

                if (entries.Count == 0)
                {
                    // Fallback if catalog only lists m_InternalIds without m_EntryDataString
                    for (int i = 0; i < expandedInternalIds.Length; i++)
                    {
                        var id = expandedInternalIds[i];
                        if (IsBundlePath(id))
                        {
                            parsed.IndexNode(new CatalogBundleNode
                            {
                                InternalId = id,
                                BundleFileName = ExtractBundleFileName(id)
                            });
                        }
                    }

                    catalog = parsed;
                    return true;
                }

                // Mark which entries represent bundles: either explicit bundle provider/path/metadata
                // or referenced inside a dependency bucket.
                var isReferencedAsDependency = new bool[entries.Count];
                for (int i = 0; i < entries.Count; i++)
                {
                    var depBucketIdx = entries[i].DependencyKeyIndex;
                    if (depBucketIdx >= 0 && depBucketIdx < buckets.Count)
                    {
                        var depEntryIndices = buckets[depBucketIdx].Entries;
                        for (int j = 0; j < depEntryIndices.Length; j++)
                        {
                            var targetIdx = depEntryIndices[j];
                            if (targetIdx >= 0 && targetIdx < entries.Count)
                            {
                                isReferencedAsDependency[targetIdx] = true;
                            }
                        }
                    }
                }

                var bundleNodeByEntryIndex = new CatalogBundleNode?[entries.Count];
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.InternalIdIndex < 0 || e.InternalIdIndex >= expandedInternalIds.Length)
                    {
                        continue;
                    }

                    var expandedId = expandedInternalIds[e.InternalIdIndex];
                    var isBundle = IsBundleProvider(e.ProviderId)
                        || IsBundlePath(expandedId)
                        || !string.IsNullOrEmpty(e.BundleName)
                        || (isReferencedAsDependency[i] && LooksLikeResolvableBundleId(expandedId));

                    if (!isBundle)
                    {
                        continue;
                    }

                    if (!parsed.ByInternalId.TryGetValue(expandedId, out var node))
                    {
                        node = new CatalogBundleNode
                        {
                            InternalId = expandedId,
                            BundleFileName = ExtractBundleFileName(expandedId),
                            BundleName = e.BundleName,
                            PrimaryKey = e.PrimaryKey,
                            Hash = e.Hash,
                            Crc = e.Crc,
                            ProviderId = e.ProviderId
                        };
                        parsed.IndexNode(node);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(node.BundleName) && !string.IsNullOrEmpty(e.BundleName))
                        {
                            node.BundleName = e.BundleName;
                        }
                        if (string.IsNullOrEmpty(node.PrimaryKey) && !string.IsNullOrEmpty(e.PrimaryKey))
                        {
                            node.PrimaryKey = e.PrimaryKey;
                        }
                        if (string.IsNullOrEmpty(node.Hash) && !string.IsNullOrEmpty(e.Hash))
                        {
                            node.Hash = e.Hash;
                        }
                        if (string.IsNullOrEmpty(node.Crc) && !string.IsNullOrEmpty(e.Crc))
                        {
                            node.Crc = e.Crc;
                        }
                        if (string.IsNullOrEmpty(node.ProviderId) && !string.IsNullOrEmpty(e.ProviderId))
                        {
                            node.ProviderId = e.ProviderId;
                        }
                        parsed.IndexNode(node);
                    }

                    bundleNodeByEntryIndex[i] = node;
                }

                // Wire dependency edges AND retain non-bundle asset/scene entries (ByKey, ByGuid, SceneEntries):
                // 1) For non-bundle asset/scene entries, depBucket[0] is the primary bundle and depBucket[1..k] are its dependency bundles.
                // 2) For bundle entries that directly carry a dependencyKeyIndex, depBucket entries (!= i) are direct dependencies of bundle i.
                var keysByEntryIndex = new List<string>?[entries.Count];
                for (int b = 0; b < buckets.Count; b++)
                {
                    var bucket = buckets[b];
                    if (string.IsNullOrWhiteSpace(bucket.KeyString) || bucket.Entries.Length == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < bucket.Entries.Length; j++)
                    {
                        var targetIdx = bucket.Entries[j];
                        if (targetIdx < 0 || targetIdx >= entries.Count || bundleNodeByEntryIndex[targetIdx] != null)
                        {
                            continue;
                        }

                        var keyList = keysByEntryIndex[targetIdx];
                        if (keyList == null)
                        {
                            keyList = new List<string>(2);
                            keysByEntryIndex[targetIdx] = keyList;
                            keyList.Add(bucket.KeyString);
                        }
                        else if (!ContainsIgnoreCaseSmall(keyList, bucket.KeyString))
                        {
                            keyList.Add(bucket.KeyString);
                        }
                    }
                }

                var depBucketResolved = new (bool Resolved, CatalogBundleNode? Primary, string[] DepIds)[buckets.Count];
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    var depBucketIdx = e.DependencyKeyIndex;
                    var hasDepBucket = depBucketIdx >= 0 && depBucketIdx < buckets.Count;
                    var depEntries = hasDepBucket ? buckets[depBucketIdx].Entries : Array.Empty<int>();

                    var selfBundleNode = bundleNodeByEntryIndex[i];
                    if (selfBundleNode != null)
                    {
                        if (depEntries.Length == 0)
                        {
                            continue;
                        }

                        int startIdx = (depEntries[0] == i) ? 1 : 0;
                        for (int j = startIdx; j < depEntries.Length; j++)
                        {
                            var targetIdx = depEntries[j];
                            if (targetIdx == i || targetIdx < 0 || targetIdx >= entries.Count)
                            {
                                continue;
                            }

                            var depNode = bundleNodeByEntryIndex[targetIdx];
                            if (depNode != null)
                            {
                                AddDependencyEdge(selfBundleNode, depNode.InternalId);
                            }
                        }
                    }
                    else
                    {
                        CatalogBundleNode? primaryNode = null;
                        string[] depBundleIds = Array.Empty<string>();
                        if (hasDepBucket && depEntries.Length > 0)
                        {
                            ref var cachedDep = ref depBucketResolved[depBucketIdx];
                            if (!cachedDep.Resolved)
                            {
                                CatalogBundleNode? resolvedPrimary = null;
                                var primaryIdx = depEntries[0];
                                if (primaryIdx >= 0 && primaryIdx < entries.Count)
                                {
                                    resolvedPrimary = bundleNodeByEntryIndex[primaryIdx];
                                }

                                List<string>? depList = null;
                                HashSet<string>? depSet = null;
                                for (int j = 1; j < depEntries.Length; j++)
                                {
                                    var depIdx = depEntries[j];
                                    if (depIdx < 0 || depIdx >= entries.Count || depIdx == primaryIdx)
                                    {
                                        continue;
                                    }

                                    var depNode = bundleNodeByEntryIndex[depIdx];
                                    if (depNode != null)
                                    {
                                        depList ??= new List<string>(depEntries.Length - 1);
                                        bool isNewDep;
                                        if (depList.Count >= 8)
                                        {
                                            depSet ??= new HashSet<string>(depList, StringComparer.OrdinalIgnoreCase);
                                            isNewDep = depSet.Add(depNode.InternalId);
                                        }
                                        else
                                        {
                                            isNewDep = !ContainsIgnoreCaseSmall(depList, depNode.InternalId);
                                        }

                                        if (isNewDep)
                                        {
                                            depList.Add(depNode.InternalId);
                                        }

                                        if (resolvedPrimary != null)
                                        {
                                            AddDependencyEdge(resolvedPrimary, depNode.InternalId);
                                        }
                                    }
                                }

                                cachedDep = (true, resolvedPrimary, depList != null ? depList.ToArray() : Array.Empty<string>());
                            }

                            primaryNode = cachedDep.Primary;
                            depBundleIds = cachedDep.DepIds;
                        }

                        if (e.InternalIdIndex >= 0 && e.InternalIdIndex < expandedInternalIds.Length)
                        {
                            var expandedId = expandedInternalIds[e.InternalIdIndex];
                            var isScene = IsSceneProvider(e.ProviderId)
                                || IsScenePath(expandedId)
                                || IsScenePath(e.PrimaryKey);

                            var assetEntry = new CatalogAssetEntry
                            {
                                PrimaryKey = e.PrimaryKey,
                                InternalId = expandedId,
                                ProviderId = e.ProviderId,
                                PrimaryBundleInternalId = primaryNode?.InternalId ?? string.Empty,
                                PrimaryBundleFileName = primaryNode?.BundleFileName ?? string.Empty,
                                IsScene = isScene
                            };

                            if (depBundleIds.Length > 0)
                            {
                                assetEntry.DependencyBundleInternalIds.AddRange(depBundleIds);
                            }

                            var entryKeys = keysByEntryIndex[i];
                            if (entryKeys != null && entryKeys.Count > 0)
                            {
                                assetEntry.Keys.AddRange(entryKeys);
                            }

                            parsed.IndexAssetEntry(assetEntry);
                        }
                    }
                }

                catalog = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseBinaryCatalog(string catalogPath, out ParsedAddressablesCatalog? catalog)
        {
            catalog = null;
            if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(catalogPath);
                var catalogDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
                var bytes = File.ReadAllBytes(fullPath);
                if (!TryParseBinaryCatalogBytes(bytes, fullPath, catalogDir, out catalog))
                {
                    return false;
                }

                return catalog != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseBinaryCatalogBytes(
            ReadOnlySpan<byte> data,
            string catalogPath,
            string catalogDirectory,
            out ParsedAddressablesCatalog? catalog)
        {
            catalog = null;
            if (data.Length < 32)
            {
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));
            if (magic != BinaryCatalogMagic)
            {
                return false;
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
            if (version < 1 || version > 10)
            {
                return false;
            }

            var keysOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
            var idOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));

            if (keysOffset == uint.MaxValue || keysOffset < 4 || keysOffset > data.Length)
            {
                return false;
            }

            var keysByteSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice((int)keysOffset - 4, 4));
            if (keysByteSize < 0 || (long)keysOffset + keysByteSize > data.Length || (keysByteSize % 8) != 0)
            {
                return false;
            }

            var parsed = new ParsedAddressablesCatalog
            {
                CatalogPath = catalogPath,
                CatalogDirectory = catalogDirectory,
                Format = "bin",
                LocatorId = ReadBinaryCatalogString(data, idOffset, '\0') ?? string.Empty,
                ImmediateSubdirectories = GetImmediateSubdirectoriesSafe(catalogDirectory)
            };

            var keyCount = keysByteSize / 8;
            var allLocationOffsets = new List<uint>();
            var seenLocationOffsets = new HashSet<uint>();
            var locationQueue = new Queue<uint>();
            var keysByLocationOffset = new Dictionary<uint, List<string>>();

            for (int k = 0; k < keyCount; k++)
            {
                var keyEntryOffset = (int)keysOffset + k * 8;
                var keyObjectOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(keyEntryOffset, 4));
                var keyString = TryReadBinaryKeyString(data, keyObjectOffset);
                var locationSetOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(keyEntryOffset + 4, 4));
                var locOffsets = ReadBinaryUInt32Array(data, locationSetOffset);
                for (int i = 0; i < locOffsets.Length; i++)
                {
                    var locOff = locOffsets[i];
                    if (locOff != uint.MaxValue && (long)locOff + 28 <= data.Length)
                    {
                        if (seenLocationOffsets.Add(locOff))
                        {
                            allLocationOffsets.Add(locOff);
                            locationQueue.Enqueue(locOff);
                        }

                        if (!string.IsNullOrWhiteSpace(keyString))
                        {
                            if (!keysByLocationOffset.TryGetValue(locOff, out var locKeys))
                            {
                                locKeys = new List<string>(2);
                                keysByLocationOffset[locOff] = locKeys;
                            }

                            if (!locKeys.Contains(keyString, StringComparer.OrdinalIgnoreCase))
                            {
                                locKeys.Add(keyString);
                            }
                        }
                    }
                }
            }

            // Follow dependencySetOffset links to discover any bundle locations not directly in KeyData
            var depSetsByOffset = new Dictionary<uint, uint[]>();
            while (locationQueue.Count > 0)
            {
                var locOff = locationQueue.Dequeue();
                var depSetOff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)locOff + 12, 4));
                if (depSetOff == uint.MaxValue || depSetOff < 4 || depSetOff > data.Length)
                {
                    continue;
                }

                if (!depSetsByOffset.TryGetValue(depSetOff, out var depLocs))
                {
                    depLocs = ReadBinaryUInt32Array(data, depSetOff);
                    depSetsByOffset[depSetOff] = depLocs;
                }

                for (int i = 0; i < depLocs.Length; i++)
                {
                    var targetOff = depLocs[i];
                    if (targetOff != uint.MaxValue && (long)targetOff + 28 <= data.Length && seenLocationOffsets.Add(targetOff))
                    {
                        allLocationOffsets.Add(targetOff);
                        locationQueue.Enqueue(targetOff);
                    }
                }
            }

            var isReferencedInDepSet = new HashSet<uint>();
            foreach (var depLocs in depSetsByOffset.Values)
            {
                for (int i = 0; i < depLocs.Length; i++)
                {
                    isReferencedInDepSet.Add(depLocs[i]);
                }
            }

            var locationsByOffset = new Dictionary<uint, BinaryLocationRecord>(allLocationOffsets.Count);
            var bundleNodeByLocOffset = new Dictionary<uint, CatalogBundleNode>();
            var providerSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var locOff in allLocationOffsets)
            {
                var primaryKeyOff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)locOff, 4));
                var internalIdOff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)locOff + 4, 4));
                var providerOff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)locOff + 8, 4));
                var depSetOff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)locOff + 12, 4));
                var extraDataOff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)locOff + 20, 4));

                var primaryKey = ReadBinaryCatalogString(data, primaryKeyOff, '/') ?? string.Empty;
                var internalId = ReadBinaryCatalogString(data, internalIdOff, '/') ?? string.Empty;
                var providerId = NormalizeBinaryProviderId(ReadBinaryCatalogString(data, providerOff, '.') ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(providerId) && providerSet.Add(providerId))
                {
                    parsed.Providers.Add(providerId);
                }

                TryReadBinaryBundleRequestOptions(data, extraDataOff, out var bundleName, out var hash, out var crc);

                var record = new BinaryLocationRecord(locOff, primaryKey, internalId, providerId, depSetOff, bundleName, hash, crc);
                locationsByOffset[locOff] = record;

                var isBundle = IsBundleProvider(providerId)
                    || IsBundlePath(internalId)
                    || !string.IsNullOrEmpty(bundleName)
                    || (isReferencedInDepSet.Contains(locOff) && LooksLikeResolvableBundleId(internalId));

                if (!isBundle || string.IsNullOrWhiteSpace(internalId))
                {
                    continue;
                }

                if (!parsed.ByInternalId.TryGetValue(internalId, out var node))
                {
                    node = new CatalogBundleNode
                    {
                        InternalId = internalId,
                        BundleFileName = ExtractBundleFileName(internalId),
                        BundleName = bundleName,
                        PrimaryKey = primaryKey,
                        Hash = hash,
                        Crc = crc,
                        ProviderId = providerId
                    };
                    parsed.IndexNode(node);
                }
                else
                {
                    if (string.IsNullOrEmpty(node.BundleName) && !string.IsNullOrEmpty(bundleName))
                    {
                        node.BundleName = bundleName;
                    }
                    if (string.IsNullOrEmpty(node.PrimaryKey) && !string.IsNullOrEmpty(primaryKey))
                    {
                        node.PrimaryKey = primaryKey;
                    }
                    if (string.IsNullOrEmpty(node.Hash) && !string.IsNullOrEmpty(hash))
                    {
                        node.Hash = hash;
                    }
                    if (string.IsNullOrEmpty(node.Crc) && !string.IsNullOrEmpty(crc))
                    {
                        node.Crc = crc;
                    }
                    if (string.IsNullOrEmpty(node.ProviderId) && !string.IsNullOrEmpty(providerId))
                    {
                        node.ProviderId = providerId;
                    }
                    parsed.IndexNode(node);
                }

                bundleNodeByLocOffset[locOff] = node;
            }

            // Wire dependency edges across locations AND retain non-bundle asset/scene entries
            var processedAssetDepSets = new HashSet<uint>();
            foreach (var record in locationsByOffset.Values)
            {
                uint[]? depLocs = null;
                var hasDepLocs = record.DependencySetOffset != uint.MaxValue
                    && depSetsByOffset.TryGetValue(record.DependencySetOffset, out depLocs)
                    && depLocs != null
                    && depLocs.Length > 0;
                if (!hasDepLocs)
                {
                    depLocs = Array.Empty<uint>();
                }

                if (bundleNodeByLocOffset.TryGetValue(record.Offset, out var selfBundleNode))
                {
                    if (depLocs!.Length == 0)
                    {
                        continue;
                    }

                    int startIdx = (depLocs[0] == record.Offset) ? 1 : 0;
                    for (int j = startIdx; j < depLocs.Length; j++)
                    {
                        var targetOff = depLocs[j];
                        if (targetOff != record.Offset && bundleNodeByLocOffset.TryGetValue(targetOff, out var depNode))
                        {
                            AddDependencyEdge(selfBundleNode, depNode.InternalId);
                        }
                    }
                }
                else
                {
                    CatalogBundleNode? primaryNode = null;
                    var depBundleIds = new List<string>();
                    if (depLocs!.Length > 0)
                    {
                        var primaryOff = depLocs[0];
                        bundleNodeByLocOffset.TryGetValue(primaryOff, out primaryNode);
                        bool wireBundleDeps = depLocs.Length >= 2 && primaryNode != null && processedAssetDepSets.Add(record.DependencySetOffset);
                        for (int j = 1; j < depLocs.Length; j++)
                        {
                            var depOff = depLocs[j];
                            if (depOff != primaryOff && bundleNodeByLocOffset.TryGetValue(depOff, out var depNode))
                            {
                                if (!depBundleIds.Contains(depNode.InternalId, StringComparer.OrdinalIgnoreCase))
                                {
                                    depBundleIds.Add(depNode.InternalId);
                                }

                                if (wireBundleDeps && primaryNode != null)
                                {
                                    AddDependencyEdge(primaryNode, depNode.InternalId);
                                }
                            }
                        }
                    }

                    var isScene = IsSceneProvider(record.ProviderId)
                        || IsScenePath(record.InternalId)
                        || IsScenePath(record.PrimaryKey);

                    var assetEntry = new CatalogAssetEntry
                    {
                        PrimaryKey = record.PrimaryKey,
                        InternalId = record.InternalId,
                        ProviderId = record.ProviderId,
                        PrimaryBundleInternalId = primaryNode?.InternalId ?? string.Empty,
                        PrimaryBundleFileName = primaryNode?.BundleFileName ?? string.Empty,
                        IsScene = isScene
                    };

                    if (depBundleIds.Count > 0)
                    {
                        assetEntry.DependencyBundleInternalIds.AddRange(depBundleIds);
                    }

                    if (keysByLocationOffset.TryGetValue(record.Offset, out var locKeys))
                    {
                        for (int k = 0; k < locKeys.Count; k++)
                        {
                            if (!assetEntry.Keys.Contains(locKeys[k], StringComparer.OrdinalIgnoreCase))
                            {
                                assetEntry.Keys.Add(locKeys[k]);
                            }
                        }
                    }

                    parsed.IndexAssetEntry(assetEntry);
                }
            }

            catalog = parsed;
            return true;
        }

        public static string? TryResolveLocalBundlePath(
            ParsedAddressablesCatalog catalog,
            string internalId,
            string? sourceBundleDirectory = null)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(internalId))
            {
                return null;
            }

            var cleanId = StripQueryString(internalId.Trim());
            if (string.IsNullOrWhiteSpace(cleanId) || IsRemoteUrl(cleanId))
            {
                return null;
            }

            var catalogDir = catalog.CatalogDirectory;
            var substituted = SubstitutePlaceholders(cleanId, catalogDir);

            // 1. Direct placeholder-substituted path (or relative to catalogDirectory)
            if (!string.IsNullOrWhiteSpace(substituted))
            {
                var candidate = NormalizeLocalPathCandidate(substituted, catalogDir);
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var fileName = ExtractBundleFileName(cleanId);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            // 2. Sibling in the opened bundle's directory
            if (!string.IsNullOrWhiteSpace(sourceBundleDirectory))
            {
                var inSourceDir = Path.Combine(sourceBundleDirectory, fileName);
                if (File.Exists(inSourceDir))
                {
                    return inSourceDir;
                }
            }

            // 3. Direct or Embed/ under catalogDirectory
            if (!string.IsNullOrWhiteSpace(catalogDir))
            {
                var direct = Path.Combine(catalogDir, fileName);
                if (File.Exists(direct))
                {
                    return direct;
                }

                var embed = Path.Combine(catalogDir, "Embed", fileName);
                if (File.Exists(embed))
                {
                    return embed;
                }

                // 4. Cached immediate subdirectories of catalogDirectory (e.g. StandaloneWindows64, Windows, Android)
                foreach (var subDir in catalog.ImmediateSubdirectories)
                {
                    var inSubDir = Path.Combine(subDir, fileName);
                    if (File.Exists(inSubDir))
                    {
                        return inSubDir;
                    }
                }
            }

            return null;
        }

        public static string SubstitutePlaceholders(string internalId, string catalogDirectory)
        {
            if (string.IsNullOrWhiteSpace(internalId))
            {
                return string.Empty;
            }

            var trimmed = internalId.Trim();
            var openBrace = trimmed.IndexOf('{');
            if (openBrace < 0)
            {
                return trimmed;
            }

            var closeBrace = trimmed.IndexOf('}', openBrace + 1);
            if (closeBrace < 0)
            {
                return trimmed;
            }

            var prefix = trimmed.Substring(0, openBrace);
            var suffix = trimmed.Substring(closeBrace + 1).TrimStart('/', '\\');
            if (string.IsNullOrWhiteSpace(catalogDirectory))
            {
                return suffix;
            }

            if (string.IsNullOrEmpty(prefix))
            {
                return string.IsNullOrEmpty(suffix)
                    ? catalogDirectory
                    : Path.Combine(catalogDirectory, NormalizeRelativeSeparators(suffix));
            }

            return prefix + Path.Combine(catalogDirectory, NormalizeRelativeSeparators(suffix));
        }

        private static string NormalizeBinaryProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return string.Empty;
            }

            if (providerId.EndsWith(".UnityEngine", StringComparison.Ordinal)
                && !providerId.StartsWith("UnityEngine.", StringComparison.Ordinal))
            {
                var segments = providerId.Split('.');
                Array.Reverse(segments);
                return string.Join('.', segments);
            }

            return providerId;
        }

        public static string? ReadBinaryCatalogString(ReadOnlySpan<byte> data, uint id, char separator)
        {
            if (id == uint.MaxValue)
            {
                return null;
            }

            if (separator != '\0' && (id & DynamicStringFlag) != 0)
            {
                var parts = new Stack<uint>();
                var currentId = id;
                int guard = 0;
                while (currentId != uint.MaxValue && guard++ < 256)
                {
                    var offset = (int)(currentId & ClearStringFlagsMask);
                    if (offset < 0 || offset + 8 > data.Length)
                    {
                        break;
                    }

                    var partStringId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                    var nextId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
                    parts.Push(partStringId);
                    currentId = nextId;
                }

                if (parts.Count == 0)
                {
                    return string.Empty;
                }

                var sb = new StringBuilder();
                while (parts.Count > 0)
                {
                    var partId = parts.Pop();
                    var seg = ReadBinaryAutoEncodedString(data, partId);
                    if (!string.IsNullOrEmpty(seg))
                    {
                        sb.Append(seg);
                    }

                    if (parts.Count > 0)
                    {
                        sb.Append(separator);
                    }
                }

                return sb.ToString();
            }

            return ReadBinaryAutoEncodedString(data, id);
        }

        private static string? ReadBinaryAutoEncodedString(ReadOnlySpan<byte> data, uint id)
        {
            if (id == uint.MaxValue)
            {
                return null;
            }

            var isUnicode = (id & UnicodeStringFlag) != 0;
            var offset = (int)(id & ClearStringFlagsMask);
            if (offset < 4 || offset > data.Length)
            {
                return null;
            }

            var byteLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset - 4, 4));
            if (byteLength < 0 || offset + byteLength > data.Length)
            {
                return null;
            }

            if (byteLength == 0)
            {
                return string.Empty;
            }

            var slice = data.Slice(offset, byteLength);
            return isUnicode
                ? Encoding.Unicode.GetString(slice)
                : Encoding.ASCII.GetString(slice);
        }

        private static uint[] ReadBinaryUInt32Array(ReadOnlySpan<byte> data, uint offset)
        {
            if (offset == uint.MaxValue || offset < 4 || offset > data.Length)
            {
                return Array.Empty<uint>();
            }

            var byteSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice((int)offset - 4, 4));
            if (byteSize <= 0 || (long)offset + byteSize > data.Length || (byteSize % 4) != 0)
            {
                return Array.Empty<uint>();
            }

            var count = byteSize / 4;
            var result = new uint[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)offset + i * 4, 4));
            }

            return result;
        }

        private static void TryReadBinaryBundleRequestOptions(
            ReadOnlySpan<byte> data,
            uint extraDataOffset,
            out string bundleName,
            out string hash,
            out string crc)
        {
            bundleName = string.Empty;
            hash = string.Empty;
            crc = string.Empty;

            if (extraDataOffset == uint.MaxValue || (long)extraDataOffset + 8 > data.Length)
            {
                return;
            }

            // ObjectTypeData: { uint32 typeId; uint32 objectId; }
            var objectId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)extraDataOffset + 4, 4));
            if (objectId == uint.MaxValue || (long)objectId + 20 > data.Length)
            {
                return;
            }

            // AssetBundleRequestOptionsSerializationAdapter.SerializedData:
            // { uint32 hashId; uint32 bundleNameId; uint32 crc; uint32 bundleSize; uint32 commonId; }
            var hashId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)objectId, 4));
            var bundleNameId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)objectId + 4, 4));
            var crcVal = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)objectId + 8, 4));

            var decodedName = ReadBinaryCatalogString(data, bundleNameId, '_');
            if (!string.IsNullOrWhiteSpace(decodedName) && IsPrintableToken(decodedName))
            {
                bundleName = decodedName;
                crc = crcVal.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (hashId != uint.MaxValue && (long)hashId + 16 <= data.Length)
                {
                    hash = Convert.ToHexString(data.Slice((int)hashId, 16)).ToLowerInvariant();
                }
            }
        }

        private static bool IsPrintableToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 512)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c < 0x20 || c == 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<CatalogBundleNode> FindMatchingNodes(
            ParsedAddressablesCatalog catalog,
            string fullBundlePath,
            string bundleFileName,
            string sourceBundleDir)
        {
            var matches = new List<CatalogBundleNode>();
            if (string.IsNullOrWhiteSpace(bundleFileName))
            {
                return matches;
            }

            if (catalog.ByFileName.TryGetValue(bundleFileName, out var byNameList))
            {
                foreach (var node in byNameList)
                {
                    var resolved = TryResolveLocalBundlePath(catalog, node.InternalId, sourceBundleDir);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        try
                        {
                            if (string.Equals(Path.GetFullPath(resolved), fullBundlePath, StringComparison.OrdinalIgnoreCase))
                            {
                                matches.Add(node);
                                continue;
                            }
                        }
                        catch
                        {
                        }
                    }

                    matches.Add(node);
                }
            }

            return matches;
        }

        private static void AddDependencyEdge(CatalogBundleNode sourceNode, string dependencyInternalId)
        {
            if (string.IsNullOrWhiteSpace(dependencyInternalId)
                || string.Equals(sourceNode.InternalId, dependencyInternalId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!sourceNode.DirectDependencyInternalIds.Contains(dependencyInternalId, StringComparer.OrdinalIgnoreCase))
            {
                sourceNode.DirectDependencyInternalIds.Add(dependencyInternalId);
            }
        }

        private static bool DoesBundleMatchAnyExternal(
            string bundlePath,
            ISet<string> unresolvedExternals,
            out bool readCabNodes)
        {
            readCabNodes = false;
            var fileName = Path.GetFileName(bundlePath);
            var fileNameNoExt = Path.GetFileNameWithoutExtension(bundlePath);

            if ((!string.IsNullOrEmpty(fileName) && unresolvedExternals.Contains(fileName))
                || (!string.IsNullOrEmpty(fileNameNoExt) && unresolvedExternals.Contains(fileNameNoExt)))
            {
                return true;
            }

            if (TryReadUnityFsDirectoryNodeNames(bundlePath, out var nodeNames))
            {
                readCabNodes = true;
                for (int i = 0; i < nodeNames.Length; i++)
                {
                    var nodeName = nodeNames[i];
                    if (unresolvedExternals.Contains(nodeName))
                    {
                        return true;
                    }

                    var stripped = StripCabPrefix(nodeName);
                    if (!string.IsNullOrEmpty(stripped) && unresolvedExternals.Contains(stripped))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void AddUniqueCandidate(
            string candidatePath,
            ISet<string> visitedPaths,
            ISet<string> visitedFileNames,
            List<string> targetList)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidatePath);
            }
            catch
            {
                return;
            }

            var fileName = Path.GetFileName(fullPath);
            if (visitedPaths.Contains(fullPath)
                || (!string.IsNullOrEmpty(fileName) && visitedFileNames.Contains(fileName)))
            {
                return;
            }

            visitedPaths.Add(fullPath);
            if (!string.IsNullOrEmpty(fileName))
            {
                visitedFileNames.Add(fileName);
            }

            targetList.Add(fullPath);
        }

        private static bool TryGetFileLength(string path, out long length)
        {
            length = 0;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                {
                    length = fi.Length;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static IEnumerable<string> ResolveManifestDependencies(string bundlePath, string bundleDir)
        {
            var results = new List<string>();
            var manifestPath = bundlePath + ".manifest";
            if (!File.Exists(manifestPath))
            {
                return results;
            }

            try
            {
                var inDepsSection = false;
                foreach (var rawLine in File.ReadLines(manifestPath))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("Dependencies:", StringComparison.OrdinalIgnoreCase))
                    {
                        inDepsSection = true;
                        continue;
                    }

                    if (!inDepsSection)
                    {
                        continue;
                    }

                    if (!line.StartsWith("-", StringComparison.Ordinal))
                    {
                        if (line.Contains(':', StringComparison.Ordinal))
                        {
                            break;
                        }
                        continue;
                    }

                    var depToken = line.Substring(1).Trim().Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(depToken))
                    {
                        continue;
                    }

                    var depFileName = Path.GetFileName(depToken.Replace('\\', '/'));
                    if (string.IsNullOrWhiteSpace(depFileName))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(bundleDir, depFileName);
                    if (File.Exists(candidate))
                    {
                        results.Add(Path.GetFullPath(candidate));
                    }
                    else if (File.Exists(candidate + ".bundle"))
                    {
                        results.Add(Path.GetFullPath(candidate + ".bundle"));
                    }
                }
            }
            catch
            {
            }

            return results;
        }

        private static List<CatalogBucket> ParseBuckets(byte[]? bucketBytes, byte[]? keyBytes)
        {
            var buckets = new List<CatalogBucket>();
            if (bucketBytes == null || bucketBytes.Length < 4)
            {
                return buckets;
            }

            var bucketCount = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(0, 4));
            if (bucketCount <= 0 || bucketCount > 1_000_000)
            {
                return buckets;
            }

            int pos = 4;
            for (int b = 0; b < bucketCount; b++)
            {
                if (pos + 8 > bucketBytes.Length)
                {
                    break;
                }

                var keyOffset = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(pos, 4));
                var entryCount = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(pos + 4, 4));
                pos += 8;

                if (entryCount < 0 || pos + (long)entryCount * 4 > bucketBytes.Length)
                {
                    break;
                }

                var entries = new int[entryCount];
                for (int e = 0; e < entryCount; e++)
                {
                    entries[e] = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(pos, 4));
                    pos += 4;
                }

                var keyStr = keyBytes != null ? ReadSerializedObjectKeyString(keyBytes, keyOffset) : string.Empty;
                buckets.Add(new CatalogBucket(keyOffset, keyStr, entries));
            }

            return buckets;
        }

        private static List<JsonEntryRecord> ParseJsonEntries(
            byte[]? entryBytes,
            byte[]? extraBytes,
            string[] expandedInternalIds,
            List<string> providerIds,
            List<CatalogBucket> buckets)
        {
            var entries = new List<JsonEntryRecord>();
            if (entryBytes == null || entryBytes.Length < 4)
            {
                return entries;
            }

            var entryCount = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(0, 4));
            if (entryCount <= 0)
            {
                return entries;
            }

            var maxEntries = (entryBytes.Length - 4) / 28;
            var count = Math.Min(entryCount, maxEntries);
            entries.Capacity = count;

            for (int i = 0; i < count; i++)
            {
                var offset = 4 + i * 28;
                var internalIdIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset, 4));
                var providerIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 4, 4));
                var dependencyKeyIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 8, 4));
                var dataIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 16, 4));
                var primaryKeyIdx = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 20, 4));

                var providerId = (providerIdx >= 0 && providerIdx < providerIds.Count)
                    ? providerIds[providerIdx]
                    : string.Empty;

                var primaryKey = (primaryKeyIdx >= 0 && primaryKeyIdx < buckets.Count)
                    ? buckets[primaryKeyIdx].KeyString
                    : string.Empty;

                var bundleName = string.Empty;
                var hash = string.Empty;
                var crc = string.Empty;

                if (extraBytes != null && dataIdx >= 0)
                {
                    var extraJson = ReadSerializedExtraJson(extraBytes, dataIdx);
                    if (!string.IsNullOrWhiteSpace(extraJson))
                    {
                        TryParseExtraBundleOptions(extraJson, out bundleName, out hash, out crc);
                    }
                }

                entries.Add(new JsonEntryRecord(
                    internalIdIdx,
                    providerId,
                    dependencyKeyIdx,
                    primaryKey,
                    bundleName,
                    hash,
                    crc));
            }

            return entries;
        }

        private static string ReadSerializedObjectKeyString(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return string.Empty;
            }

            var objType = data[offset++];
            switch (objType)
            {
                case 0: // AsciiString
                    if (offset + 4 > data.Length) return string.Empty;
                    var asciiLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                    offset += 4;
                    if (asciiLen < 0 || offset + asciiLen > data.Length) return string.Empty;
                    return Encoding.ASCII.GetString(data, offset, asciiLen);

                case 1: // UnicodeString
                    if (offset + 4 > data.Length) return string.Empty;
                    var uniLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                    offset += 4;
                    if (uniLen < 0 || offset + uniLen > data.Length) return string.Empty;
                    return Encoding.Unicode.GetString(data, offset, uniLen);

                case 4: // Int32
                    if (offset + 4 > data.Length) return string.Empty;
                    return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture);

                case 5: // Hash128
                    if (offset >= data.Length) return string.Empty;
                    var hashLen = data[offset++];
                    if (offset + hashLen > data.Length) return string.Empty;
                    return Encoding.ASCII.GetString(data, offset, hashLen);

                default:
                    return string.Empty;
            }
        }

        private static string? ReadSerializedExtraJson(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return null;
            }

            var objType = data[offset++];
            if (objType == 0) // AsciiString
            {
                if (offset + 4 > data.Length) return null;
                var len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (len < 0 || offset + len > data.Length) return null;
                return Encoding.ASCII.GetString(data, offset, len);
            }

            if (objType == 1) // UnicodeString
            {
                if (offset + 4 > data.Length) return null;
                var len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (len < 0 || offset + len > data.Length) return null;
                return Encoding.Unicode.GetString(data, offset, len);
            }

            if (objType == 7) // JsonObject
            {
                if (offset >= data.Length) return null;
                var asmLen = data[offset++];
                offset += asmLen;
                if (offset >= data.Length) return null;
                var clsLen = data[offset++];
                offset += clsLen;
                if (offset + 4 > data.Length) return null;
                var jsonLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (jsonLen < 0 || offset + jsonLen > data.Length) return null;
                return Encoding.Unicode.GetString(data, offset, jsonLen);
            }

            return null;
        }

        private static bool ContainsIgnoreCaseSmall(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryExtractSimpleJsonStringField(string json, string fieldToken, out string value)
        {
            value = string.Empty;
            int idx = json.IndexOf(fieldToken, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            int pos = idx + fieldToken.Length;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
            {
                pos++;
            }

            if (pos >= json.Length || json[pos] != ':')
            {
                return false;
            }

            pos++;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
            {
                pos++;
            }

            if (pos >= json.Length || json[pos] != '"')
            {
                return false;
            }

            int start = ++pos;
            while (pos < json.Length)
            {
                char ch = json[pos];
                if (ch == '\\')
                {
                    return false; // Fall back to JsonDocument for escaped strings
                }
                if (ch == '"')
                {
                    value = json.Substring(start, pos - start);
                    return true;
                }
                pos++;
            }

            return false;
        }

        private static bool TryExtractSimpleJsonScalarField(string json, string fieldToken, out string value)
        {
            value = string.Empty;
            int idx = json.IndexOf(fieldToken, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            int pos = idx + fieldToken.Length;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
            {
                pos++;
            }

            if (pos >= json.Length || json[pos] != ':')
            {
                return false;
            }

            pos++;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
            {
                pos++;
            }

            if (pos >= json.Length)
            {
                return false;
            }

            if (json[pos] == '"')
            {
                int start = ++pos;
                while (pos < json.Length)
                {
                    char ch = json[pos];
                    if (ch == '\\') return false;
                    if (ch == '"')
                    {
                        value = json.Substring(start, pos - start);
                        return true;
                    }
                    pos++;
                }
                return false;
            }

            int numStart = pos;
            while (pos < json.Length)
            {
                char ch = json[pos];
                if ((ch >= '0' && ch <= '9') || ch == '-')
                {
                    pos++;
                }
                else
                {
                    break;
                }
            }

            if (pos > numStart)
            {
                value = json.Substring(numStart, pos - numStart);
                return true;
            }

            return false;
        }

        private static void TryParseExtraBundleOptions(
            string json,
            out string bundleName,
            out string hash,
            out string crc)
        {
            bundleName = string.Empty;
            hash = string.Empty;
            crc = string.Empty;

            // Fast zero-JsonDocument path for standard Unity AssetBundleRequestOptions JSON.
            if (TryExtractSimpleJsonStringField(json, "\"m_BundleName\"", out var fastName)
                && TryExtractSimpleJsonStringField(json, "\"m_Hash\"", out var fastHash))
            {
                bundleName = fastName;
                hash = fastHash;
                if (TryExtractSimpleJsonScalarField(json, "\"m_Crc\"", out var fastCrc))
                {
                    crc = fastCrc;
                }
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var root = doc.RootElement;
                bundleName = ReadJsonStringProperty(root, "m_BundleName") ?? string.Empty;
                hash = ReadJsonStringProperty(root, "m_Hash") ?? string.Empty;
                if (TryGetJsonPropertyCaseInsensitive(root, "m_Crc", out var crcProp))
                {
                    crc = crcProp.ValueKind switch
                    {
                        JsonValueKind.Number => crcProp.GetRawText(),
                        JsonValueKind.String => crcProp.GetString() ?? string.Empty,
                        _ => string.Empty
                    };
                }
            }
            catch
            {
            }
        }

        private static string ExpandInternalId(string value, List<string> prefixes)
        {
            if (prefixes == null || prefixes.Count == 0 || string.IsNullOrEmpty(value))
            {
                return value;
            }

            var hashIdx = value.IndexOf('#');
            if (hashIdx <= 0)
            {
                return value;
            }

            if (int.TryParse(value.AsSpan(0, hashIdx), out var idx) && idx >= 0 && idx < prefixes.Count)
            {
                return prefixes[idx] + value.Substring(hashIdx + 1);
            }

            return value;
        }

        public static bool TryNormalizeAssetGuid(string? candidate, out string normalizedGuid)
        {
            normalizedGuid = string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var trimmed = candidate.Trim();
            if (trimmed.Length < 32 || trimmed.Length > 38)
            {
                return false;
            }

            Span<char> buffer = stackalloc char[32];
            int count = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch is '-' or '{' or '}')
                {
                    continue;
                }

                if (count >= 32)
                {
                    return false;
                }

                if (ch is >= '0' and <= '9')
                {
                    buffer[count++] = ch;
                }
                else if (ch is >= 'a' and <= 'f')
                {
                    buffer[count++] = ch;
                }
                else if (ch is >= 'A' and <= 'F')
                {
                    buffer[count++] = (char)(ch + 32);
                }
                else
                {
                    return false;
                }
            }

            if (count != 32)
            {
                return false;
            }

            normalizedGuid = new string(buffer);
            return true;
        }

        public static bool TryResolveGuidToBundlePaths(
            IEnumerable<ParsedAddressablesCatalog>? catalogs,
            string? assetGuid,
            out List<(CatalogAssetEntry Entry, string? PrimaryBundlePath, List<string> DependencyBundlePaths)> matches,
            string? sourceBundleDirectory = null)
        {
            matches = new List<(CatalogAssetEntry, string?, List<string>)>();
            if (catalogs == null || !TryNormalizeAssetGuid(assetGuid, out var normalizedGuid))
            {
                return false;
            }

            foreach (var catalog in catalogs)
            {
                if (catalog == null || !catalog.TryGetEntriesByGuid(normalizedGuid, out var entries))
                {
                    continue;
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var primaryPath = !string.IsNullOrWhiteSpace(entry.PrimaryBundleInternalId)
                        ? TryResolveLocalBundlePath(catalog, entry.PrimaryBundleInternalId, sourceBundleDirectory)
                        : null;

                    var depPaths = new List<string>();
                    for (int d = 0; d < entry.DependencyBundleInternalIds.Count; d++)
                    {
                        var depPath = TryResolveLocalBundlePath(catalog, entry.DependencyBundleInternalIds[d], sourceBundleDirectory);
                        if (!string.IsNullOrEmpty(depPath) && !depPaths.Contains(depPath, StringComparer.OrdinalIgnoreCase))
                        {
                            depPaths.Add(depPath);
                        }
                    }

                    matches.Add((entry, primaryPath, depPaths));
                }
            }

            return matches.Count > 0;
        }

        private static string? TryReadBinaryKeyString(ReadOnlySpan<byte> data, uint keyOffset)
        {
            if (keyOffset == uint.MaxValue)
            {
                return null;
            }

            // 1. Check ObjectTypeData layout: { uint32 typeId; uint32 objectId; } where typeId points to { uint32 asmId; uint32 classId; }
            if ((keyOffset & (UnicodeStringFlag | DynamicStringFlag)) == 0 && (long)keyOffset + 8 <= data.Length)
            {
                var typeId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)keyOffset, 4));
                var objectId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)keyOffset + 4, 4));
                if (typeId != uint.MaxValue
                    && (typeId & (UnicodeStringFlag | DynamicStringFlag)) == 0
                    && (long)typeId + 8 <= data.Length)
                {
                    var classNameId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)typeId + 4, 4));
                    var className = ReadBinaryCatalogString(data, classNameId, '.');
                    if (!string.IsNullOrWhiteSpace(className)
                        && IsPrintableToken(className)
                        && (className.Contains("String", StringComparison.Ordinal) || className.Contains("Guid", StringComparison.Ordinal)))
                    {
                        var decodedObjectString = ReadBinaryCatalogString(data, objectId, '/');
                        if (!string.IsNullOrWhiteSpace(decodedObjectString) && IsPrintableToken(decodedObjectString))
                        {
                            return decodedObjectString;
                        }
                    }
                }
            }

            // 2. Direct string ID (ASCII, Unicode, or dynamic path string)
            var direct = ReadBinaryCatalogString(data, keyOffset, '/');
            if (!string.IsNullOrWhiteSpace(direct) && IsPrintableToken(direct))
            {
                return direct;
            }

            return null;
        }

        private static bool IsSceneProvider(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            return providerId.Contains("SceneProvider", StringComparison.OrdinalIgnoreCase)
                || providerId.Contains("BundledScene", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsScenePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return StripQueryString(value.Trim()).EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBundleProvider(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            if (providerId.Contains("BundledAsset", StringComparison.OrdinalIgnoreCase)
                || providerId.Contains("BundledScene", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return providerId.Contains("AssetBundle", StringComparison.OrdinalIgnoreCase)
                || providerId.EndsWith("BundleProvider", StringComparison.OrdinalIgnoreCase)
                || providerId.EndsWith("BundleResource", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBundlePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var clean = StripQueryString(value);
            return clean.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                || clean.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)
                || clean.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)
                || clean.Contains("/Embed/", StringComparison.OrdinalIgnoreCase)
                || clean.Contains("\\Embed\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeResolvableBundleId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsRemoteUrl(value))
            {
                return false;
            }

            return value.Contains('{')
                || value.Contains('/', StringComparison.Ordinal)
                || value.Contains('\\', StringComparison.Ordinal)
                || IsBundlePath(value);
        }

        private static bool IsLikelyBundleFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            var ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".hash", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".manifest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".meta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool IsRemoteUrl(string value)
        {
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripQueryString(string value)
        {
            var q = value.IndexOf('?');
            return q >= 0 ? value.Substring(0, q) : value;
        }

        private static string ExtractBundleFileName(string internalId)
        {
            if (string.IsNullOrWhiteSpace(internalId))
            {
                return string.Empty;
            }

            var clean = StripQueryString(internalId.Trim());
            var lastSlash = Math.Max(clean.LastIndexOf('/'), clean.LastIndexOf('\\'));
            return lastSlash >= 0 ? clean.Substring(lastSlash + 1) : clean;
        }

        private static string NormalizeRelativeSeparators(string path)
        {
            return path
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
        }

        private static string NormalizeLocalPathCandidate(string path, string baseDirectory)
        {
            try
            {
                var normalized = NormalizeRelativeSeparators(path);
                if (Path.IsPathRooted(normalized))
                {
                    return Path.GetFullPath(normalized);
                }

                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return Path.GetFullPath(Path.Combine(baseDirectory, normalized));
                }

                return Path.GetFullPath(normalized);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeExternalFileName(string? fileName, string? pathName)
        {
            var candidate = !string.IsNullOrWhiteSpace(fileName) ? fileName! : (pathName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            var normalized = candidate.Replace('\\', '/').TrimEnd('/');
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;
        }

        private static string StripCabPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(4);
            }

            var dotIdx = trimmed.IndexOf('.');
            if (dotIdx > 0)
            {
                trimmed = trimmed.Substring(0, dotIdx);
            }

            return trimmed;
        }

        private static string[] GetImmediateSubdirectoriesSafe(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static byte[]? TryDecodeBase64Property(JsonElement obj, string propertyName)
        {
            var base64 = ReadJsonStringProperty(obj, propertyName);
            if (string.IsNullOrWhiteSpace(base64))
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(base64);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetJsonPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string? ReadJsonStringProperty(JsonElement obj, string name)
        {
            if (TryGetJsonPropertyCaseInsensitive(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            return null;
        }

        private static List<string> ReadJsonStringArrayProperty(JsonElement obj, string name)
        {
            var items = new List<string>();
            if (!TryGetJsonPropertyCaseInsensitive(obj, name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            {
                return items;
            }

            foreach (var element in prop.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var str = element.GetString();
                    if (str != null)
                    {
                        items.Add(str);
                    }
                }
            }

            return items;
        }

        private readonly record struct CatalogBucket(int KeyDataOffset, string KeyString, int[] Entries);

        private readonly record struct JsonEntryRecord(
            int InternalIdIndex,
            string ProviderId,
            int DependencyKeyIndex,
            string PrimaryKey,
            string BundleName,
            string Hash,
            string Crc);

        private readonly record struct BinaryLocationRecord(
            uint Offset,
            string PrimaryKey,
            string InternalId,
            string ProviderId,
            uint DependencySetOffset,
            string BundleName,
            string Hash,
            string Crc);
    }
}
