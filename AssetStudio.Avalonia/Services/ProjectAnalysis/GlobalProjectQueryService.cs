using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services.Scripting;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis
{
    public enum GlobalSearchIndexSource
    {
        None = 0,
        ProjectIndex = 1,
        SqliteCacheHydrated = 2,
        LoadedAssetsFiles = 3
    }

    public enum GlobalSearchCoverageStatus
    {
        Empty = 0,
        CompleteMetadataOnly = 1,
        CompleteWithTextContent = 2,
        PartialResultLimit = 3,
        PartialTextContentLimits = 4,
        Cancelled = 5
    }

    public sealed class GlobalSearchCoverage
    {
        public static readonly GlobalSearchCoverage Empty = new();

        public GlobalSearchIndexSource IndexSource { get; init; } = GlobalSearchIndexSource.None;
        public GlobalSearchCoverageStatus Status { get; init; } = GlobalSearchCoverageStatus.Empty;
        public int IndexedAssetsCount { get; init; }
        public int LoadedAssetsCount { get; init; }
        public int AssetsScannedCount { get; init; }
        public bool TextAssetContentSearchEnabled { get; init; }
        public int TextAssetsEligibleCount { get; init; }
        public int TextAssetsExaminedCount { get; init; }
        public int TextAssetsSkippedBySizeCapCount { get; init; }
        public int TextAssetsSkippedByCountCapCount { get; init; }
        public int TextAssetsUnavailableCount { get; init; }
        public bool ResultLimitReached { get; init; }
        public bool TextAssetCountLimitReached { get; init; }
        public bool WasCancelled { get; init; }
        public IReadOnlyList<string> ExaminedScopes { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> NotExaminedScopes { get; init; } = Array.Empty<string>();
        public string BadgeText { get; init; } = string.Empty;
        public string DetailsText { get; init; } = string.Empty;

        public bool IsPartial =>
            WasCancelled
            || ResultLimitReached
            || TextAssetCountLimitReached
            || TextAssetsSkippedBySizeCapCount > 0
            || TextAssetsSkippedByCountCapCount > 0
            || TextAssetsUnavailableCount > 0;
    }

    public sealed class GlobalSearchQuery
    {
        public const int DefaultMaxResults = 500;
        public const int DefaultMaxTextAssetsScanned = 500;
        public const long DefaultMaxTextAssetBytes = 2L * 1024 * 1024;

        public string QueryText { get; init; } = string.Empty;
        public ClassIDType? TypeFilter { get; init; }
        public bool UseRegex { get; init; }
        public bool SearchTextAssetContents { get; init; }
        public int MaxResults { get; init; } = DefaultMaxResults;
        public int MaxTextAssetsScanned { get; init; } = DefaultMaxTextAssetsScanned;
        public long MaxTextAssetBytes { get; init; } = DefaultMaxTextAssetBytes;
        public bool ReturnPartialOnCancellation { get; init; }
    }

    public sealed class GlobalSearchHit
    {
        public string AssetUniqueId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ClassIDType AssetType { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public string Container { get; init; } = string.Empty;
        public string SerializedFileName { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public long PathId { get; init; }
        public long ByteSize { get; init; }
        public string MatchReason { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public long MatchByteOffset { get; init; } = -1;

        public string PathIdText => PathId != 0
            ? PathId.ToString(CultureInfo.InvariantCulture)
            : "-";

        public bool IsNavigable => !string.IsNullOrWhiteSpace(AssetUniqueId) || PathId != 0;
    }

    public sealed class GlobalSearchResult
    {
        public static readonly GlobalSearchResult Empty = new();

        public IReadOnlyList<GlobalSearchHit> Hits { get; init; } = Array.Empty<GlobalSearchHit>();
        public int TotalScannedAssets { get; init; }
        public int TextAssetsScanned { get; init; }
        public int TextAssetsSkippedBySizeCap { get; init; }
        public bool IsTruncated { get; init; }
        public bool WasCancelled { get; init; }
        public long ElapsedMs { get; init; }
        public string? ErrorMessage { get; init; }
        public string Summary { get; init; } = "Enter a query to search project assets.";
        public GlobalSearchCoverage Coverage { get; init; } = GlobalSearchCoverage.Empty;
    }

    public static class GlobalProjectQueryService
    {
        public const long MaxTextAssetBytes = GlobalSearchQuery.DefaultMaxTextAssetBytes;
        public const int MaxTextAssetsScanned = GlobalSearchQuery.DefaultMaxTextAssetsScanned;
        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

        public static GlobalSearchResult SearchAssets(
            GlobalSearchQuery query,
            ProjectIndex? projectIndex,
            AssetsManager? assetsManager = null,
            SQLiteProjectIndexCache? sqliteCache = null,
            string? folderPath = null,
            string? signature = null,
            CancellationToken ct = default)
        {
            return SearchAssetsCore(query, projectIndex, assetsManager, sqliteCache, folderPath, signature, textContentOverride: null, ct);
        }

        internal static GlobalSearchResult SearchAssetsWithContentReader(
            GlobalSearchQuery query,
            ProjectIndex? projectIndex,
            AssetsManager? assetsManager,
            Func<AssetHandle, byte[]?>? textContentOverride,
            CancellationToken ct = default)
        {
            return SearchAssetsCore(query, projectIndex, assetsManager, null, null, null, textContentOverride, ct);
        }

        public static SemanticGraphUsageResult FindWhoUsesAsset(
            SQLiteProjectIndexCache? sqliteCache,
            string? folderPath,
            string? signature,
            string assetUniqueId,
            ClassIDType assetType,
            string? assetName = null,
            int maxResults = SQLiteProjectIndexCache.DefaultAnalysisGraphMaxResults)
        {
            if (sqliteCache == null
                || string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature))
            {
                return new SemanticGraphUsageResult
                {
                    QueryKind = "Unavailable",
                    TargetAssetUniqueId = assetUniqueId ?? string.Empty,
                    TargetName = string.IsNullOrWhiteSpace(assetName) ? (assetUniqueId ?? string.Empty) : assetName,
                    TargetType = assetType,
                    Summary = "Semantic usage graph requires an indexed project with an active SQLite cache."
                };
            }

            return sqliteCache.GetAssetUsageSummary(
                folderPath,
                signature,
                assetUniqueId ?? string.Empty,
                assetType,
                assetName,
                maxResults);
        }

        private static GlobalSearchResult SearchAssetsCore(
            GlobalSearchQuery? query,
            ProjectIndex? projectIndex,
            AssetsManager? assetsManager,
            SQLiteProjectIndexCache? sqliteCache,
            string? folderPath,
            string? signature,
            Func<AssetHandle, byte[]?>? textContentOverride,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            query ??= new GlobalSearchQuery();
            var maxResults = Math.Clamp(query.MaxResults > 0 ? query.MaxResults : GlobalSearchQuery.DefaultMaxResults, 1, 5000);
            var maxTextBytes = query.MaxTextAssetBytes > 0 ? query.MaxTextAssetBytes : MaxTextAssetBytes;
            var maxTextCount = Math.Clamp(query.MaxTextAssetsScanned > 0 ? query.MaxTextAssetsScanned : MaxTextAssetsScanned, 1, 5000);

            if (!TryParseQuery(query, out var parsed, out var parseError))
            {
                sw.Stop();
                return new GlobalSearchResult
                {
                    ErrorMessage = parseError,
                    Summary = parseError ?? "Invalid search query.",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            if (parsed.IsEmpty && !query.TypeFilter.HasValue)
            {
                sw.Stop();
                return new GlobalSearchResult
                {
                    Summary = "Enter a query term, filter (pathid:, guid:, type:, file:, -exclude), or choose an asset type.",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            // Ensure we have a ProjectIndex to scan; if none was passed, try AssetsManager or SQLite cache.
            var indexSource = GlobalSearchIndexSource.None;
            var effectiveIndex = projectIndex ?? assetsManager?.ProjectIndex;
            if (effectiveIndex != null && effectiveIndex.Count > 0)
            {
                indexSource = GlobalSearchIndexSource.ProjectIndex;
            }
            else if (sqliteCache != null
                && !string.IsNullOrWhiteSpace(folderPath)
                && !string.IsNullOrWhiteSpace(signature))
            {
                var hydrated = new ProjectIndex();
                if (sqliteCache.LoadIndexCacheIntoProjectIndex(folderPath, signature, hydrated, out _))
                {
                    effectiveIndex = hydrated;
                    indexSource = GlobalSearchIndexSource.SqliteCacheHydrated;
                }
            }

            var loadedAssetsCount = CountLoadedAssets(assetsManager);
            var indexedAssetsCount = effectiveIndex?.Count ?? 0;

            var hits = new List<GlobalSearchHit>(Math.Min(64, maxResults));
            var hitUniqueIds = new HashSet<string>(StringComparer.Ordinal);
            var totalScanned = 0;
            var textEligible = 0;
            var textScanned = 0;
            var textSkippedByCap = 0;
            var textSkippedByCountCap = 0;
            var textUnavailable = 0;
            var resultLimitReached = false;
            var textCountLimitReached = false;
            var wasCancelled = false;

            try
            {
                if (effectiveIndex != null && effectiveIndex.Count > 0)
                {
                    ScanArena(
                        effectiveIndex,
                        assetsManager,
                        parsed,
                        query,
                        maxResults,
                        maxTextCount,
                        maxTextBytes,
                        textContentOverride,
                        hits,
                        hitUniqueIds,
                        ref totalScanned,
                        ref textEligible,
                        ref textScanned,
                        ref textSkippedByCap,
                        ref textSkippedByCountCap,
                        ref textUnavailable,
                        ref resultLimitReached,
                        ref textCountLimitReached,
                        ct);
                }
                else if (assetsManager != null && assetsManager.assetsFileList.Count > 0)
                {
                    indexSource = GlobalSearchIndexSource.LoadedAssetsFiles;
                    ScanLoadedAssetsFiles(
                        assetsManager,
                        parsed,
                        query,
                        maxResults,
                        maxTextCount,
                        maxTextBytes,
                        hits,
                        hitUniqueIds,
                        ref totalScanned,
                        ref textEligible,
                        ref textScanned,
                        ref textSkippedByCap,
                        ref textSkippedByCountCap,
                        ref resultLimitReached,
                        ref textCountLimitReached,
                        ct);
                }
            }
            catch (OperationCanceledException) when (query.ReturnPartialOnCancellation)
            {
                wasCancelled = true;
            }

            sw.Stop();

            var isTruncated = resultLimitReached || textCountLimitReached;
            var coverage = BuildCoverage(
                indexSource,
                indexedAssetsCount,
                loadedAssetsCount,
                totalScanned,
                query.SearchTextAssetContents,
                maxResults,
                maxTextCount,
                maxTextBytes,
                textEligible,
                textScanned,
                textSkippedByCap,
                textSkippedByCountCap,
                textUnavailable,
                resultLimitReached,
                textCountLimitReached,
                wasCancelled);

            var summaryParts = new List<string>(4);
            if (wasCancelled)
            {
                summaryParts.Add($"Search cancelled after scanning {totalScanned:N0} asset(s) ({hits.Count:N0} partial match(es) in {sw.ElapsedMilliseconds:N0} ms)");
            }
            else
            {
                summaryParts.Add(isTruncated
                    ? $"Showing {hits.Count:N0} matches (truncated at limit {maxResults:N0}; scanned {totalScanned:N0} assets in {sw.ElapsedMilliseconds:N0} ms)"
                    : $"Found {hits.Count:N0} match(es) across {totalScanned:N0} asset(s) in {sw.ElapsedMilliseconds:N0} ms");
            }

            if (query.SearchTextAssetContents)
            {
                summaryParts.Add($"TextAssets examined: {textScanned:N0}");
                if (textSkippedByCap > 0)
                {
                    summaryParts.Add($"skipped > {FormatByteLimit(maxTextBytes)}: {textSkippedByCap:N0}");
                }
                if (textSkippedByCountCap > 0)
                {
                    summaryParts.Add($"not examined (count cap {maxTextCount:N0}): {textSkippedByCountCap:N0}");
                }
                if (textUnavailable > 0)
                {
                    summaryParts.Add($"unloaded/unavailable: {textUnavailable:N0}");
                }
            }

            return new GlobalSearchResult
            {
                Hits = hits,
                TotalScannedAssets = totalScanned,
                TextAssetsScanned = textScanned,
                TextAssetsSkippedBySizeCap = textSkippedByCap,
                IsTruncated = isTruncated,
                WasCancelled = wasCancelled,
                ElapsedMs = sw.ElapsedMilliseconds,
                Summary = string.Join(" • ", summaryParts),
                Coverage = coverage
            };
        }

        private static int CountLoadedAssets(AssetsManager? assetsManager)
        {
            if (assetsManager?.assetsFileList == null)
            {
                return 0;
            }

            var total = 0;
            for (var i = 0; i < assetsManager.assetsFileList.Count; i++)
            {
                var sf = assetsManager.assetsFileList[i];
                if (sf?.Objects != null)
                {
                    total += sf.Objects.Count;
                }
            }

            return total;
        }

        private static GlobalSearchCoverage BuildCoverage(
            GlobalSearchIndexSource indexSource,
            int indexedAssetsCount,
            int loadedAssetsCount,
            int totalScanned,
            bool searchTextAssetContents,
            int maxResults,
            int maxTextCount,
            long maxTextBytes,
            int textEligible,
            int textScanned,
            int textSkippedBySizeCap,
            int textSkippedByCountCap,
            int textUnavailable,
            bool resultLimitReached,
            bool textCountLimitReached,
            bool wasCancelled)
        {
            var status = wasCancelled
                ? GlobalSearchCoverageStatus.Cancelled
                : resultLimitReached
                    ? GlobalSearchCoverageStatus.PartialResultLimit
                    : (textCountLimitReached || textSkippedBySizeCap > 0 || textSkippedByCountCap > 0 || textUnavailable > 0)
                        ? GlobalSearchCoverageStatus.PartialTextContentLimits
                        : indexSource == GlobalSearchIndexSource.None
                            ? GlobalSearchCoverageStatus.Empty
                            : searchTextAssetContents
                                ? GlobalSearchCoverageStatus.CompleteWithTextContent
                                : GlobalSearchCoverageStatus.CompleteMetadataOnly;

            var sourceLabel = indexSource switch
            {
                GlobalSearchIndexSource.ProjectIndex => $"Indexed metadata ({indexedAssetsCount:N0} indexed, {loadedAssetsCount:N0} loaded in memory)",
                GlobalSearchIndexSource.SqliteCacheHydrated => $"SQLite index cache ({indexedAssetsCount:N0} indexed, {loadedAssetsCount:N0} loaded in memory)",
                GlobalSearchIndexSource.LoadedAssetsFiles => $"Loaded files only ({loadedAssetsCount:N0} loaded; no project index)",
                _ => "No project index or loaded files"
            };

            var examined = new List<string>(3);
            if (indexSource != GlobalSearchIndexSource.None)
            {
                examined.Add($"{sourceLabel} — scanned {totalScanned:N0} asset(s) (name, container, path, type, PathID, GUID)");
            }
            if (searchTextAssetContents)
            {
                examined.Add($"TextAsset UTF-8 bodies: {textScanned:N0} of {textEligible:N0} eligible examined (cap <= {FormatByteLimit(maxTextBytes)}, max {maxTextCount:N0} files)");
            }

            var notExamined = new List<string>(5);
            if (wasCancelled)
            {
                var remaining = Math.Max(0, Math.Max(indexedAssetsCount, loadedAssetsCount) - totalScanned);
                notExamined.Add($"Remaining project assets after cancellation (~{remaining:N0})");
            }
            if (resultLimitReached)
            {
                notExamined.Add($"Additional matches beyond result cap ({maxResults:N0})");
            }
            if (!searchTextAssetContents)
            {
                notExamined.Add("TextAsset contents (enable 'Search TextAsset contents' to scan bodies)");
            }
            else
            {
                if (textSkippedBySizeCap > 0)
                {
                    notExamined.Add($"{textSkippedBySizeCap:N0} TextAsset(s) exceeding {FormatByteLimit(maxTextBytes)} size cap");
                }
                if (textSkippedByCountCap > 0)
                {
                    notExamined.Add($"{textSkippedByCountCap:N0} TextAsset(s) beyond {maxTextCount:N0}-file content cap");
                }
                if (textUnavailable > 0)
                {
                    notExamined.Add($"{textUnavailable:N0} TextAsset(s) whose source files were unavailable on disk");
                }
            }
            notExamined.Add("MonoBehaviour serialized fields");
            notExamined.Add("Decompiled C# script bodies (use Script tab)");

            var badgeParts = new List<string>(4);
            var scopeStateLabel = status switch
            {
                GlobalSearchCoverageStatus.Cancelled => "Cancelled (Partial)",
                GlobalSearchCoverageStatus.PartialResultLimit => $"Partial (result cap {maxResults:N0})",
                GlobalSearchCoverageStatus.PartialTextContentLimits => "Partial (TextAsset limits)",
                GlobalSearchCoverageStatus.CompleteWithTextContent => "Complete (Metadata + TextAsset)",
                GlobalSearchCoverageStatus.CompleteMetadataOnly => "Metadata Only",
                _ => "No Scope"
            };
            badgeParts.Add($"Coverage: {scopeStateLabel}");
            if (indexedAssetsCount > 0)
            {
                badgeParts.Add($"Indexed: {indexedAssetsCount:N0}");
            }
            if (loadedAssetsCount > 0 || indexedAssetsCount == 0)
            {
                badgeParts.Add($"Loaded: {loadedAssetsCount:N0}");
            }
            if (searchTextAssetContents)
            {
                badgeParts.Add($"TextAssets examined: {textScanned:N0}/{textEligible:N0}");
            }

            var detailsSb = new StringBuilder();
            detailsSb.AppendLine($"Status: {scopeStateLabel}");
            detailsSb.AppendLine("Examined:");
            foreach (var item in examined)
            {
                detailsSb.AppendLine($"  • {item}");
            }
            detailsSb.AppendLine("Not examined:");
            foreach (var item in notExamined)
            {
                detailsSb.AppendLine($"  • {item}");
            }

            return new GlobalSearchCoverage
            {
                IndexSource = indexSource,
                Status = status,
                IndexedAssetsCount = indexedAssetsCount,
                LoadedAssetsCount = loadedAssetsCount,
                AssetsScannedCount = totalScanned,
                TextAssetContentSearchEnabled = searchTextAssetContents,
                TextAssetsEligibleCount = textEligible,
                TextAssetsExaminedCount = textScanned,
                TextAssetsSkippedBySizeCapCount = textSkippedBySizeCap,
                TextAssetsSkippedByCountCapCount = textSkippedByCountCap,
                TextAssetsUnavailableCount = textUnavailable,
                ResultLimitReached = resultLimitReached,
                TextAssetCountLimitReached = textCountLimitReached,
                WasCancelled = wasCancelled,
                ExaminedScopes = examined,
                NotExaminedScopes = notExamined,
                BadgeText = string.Join(" • ", badgeParts),
                DetailsText = detailsSb.ToString().TrimEnd()
            };
        }

        private static void ScanArena(
            ProjectIndex projectIndex,
            AssetsManager? assetsManager,
            ParsedQuery parsed,
            GlobalSearchQuery query,
            int maxResults,
            int maxTextCount,
            long maxTextBytes,
            Func<AssetHandle, byte[]?>? textContentOverride,
            List<GlobalSearchHit> hits,
            HashSet<string> hitUniqueIds,
            ref int totalScanned,
            ref int textEligible,
            ref int textScanned,
            ref int textSkippedByCap,
            ref int textSkippedByCountCap,
            ref int textUnavailable,
            ref bool resultLimitReached,
            ref bool textCountLimitReached,
            CancellationToken ct)
        {
            var arena = projectIndex.Arena;
            var count = arena.Count;
            var canSearchTextContent = query.SearchTextAssetContents
                && (parsed.PositiveTerms.Count > 0 || parsed.PositiveRegexes.Count > 0);
            var candidateTextIndices = canSearchTextContent ? new List<int>(Math.Min(maxTextCount, 64)) : null;

            for (var i = 0; i < count; i++)
            {
                if ((i & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                totalScanned++;

                // 1) Cheapest primitive column checks first (zero allocations on misses).
                var assetType = arena.GetType(i);
                if (!MatchesTypeConstraints(assetType, query.TypeFilter, parsed))
                {
                    continue;
                }

                var pathId = arena.GetPathId(i);
                if (!MatchesPathIdConstraints(pathId, parsed))
                {
                    continue;
                }

                var origin = arena.GetOrigin(i);
                var sfName = origin.SerializedFileName;
                var origPath = origin.OriginalPath;
                if (!MatchesFileConstraints(sfName, origPath, parsed))
                {
                    continue;
                }

                // 2) String/metadata checks only after cheap filters pass.
                var name = arena.GetName(i);
                var container = arena.GetContainer(i);
                var typeName = AssetItem.GetCachedTypeString(assetType);

                if (MatchesNegativeTerms(name, container, sfName, typeName, parsed))
                {
                    continue;
                }

                var guidDetail = string.Empty;
                if (parsed.IncludeGuids.Count > 0)
                {
                    var realObjForGuid = assetType == ClassIDType.MonoScript ? arena.GetRealObject(i) : null;
                    if (!MatchesAnyGuid(parsed.IncludeGuids, assetType, typeName, sfName, pathId, name, container, realObjForGuid, out guidDetail))
                    {
                        continue;
                    }
                }

                if (MatchesPositiveMetadata(name, container, sfName, typeName, pathId, parsed, out var matchReason))
                {
                    if (hits.Count >= maxResults)
                    {
                        resultLimitReached = true;
                        break;
                    }

                    var uid = arena.GetUniqueId(i);
                    if (hitUniqueIds.Add(uid))
                    {
                        var reason = !string.IsNullOrEmpty(guidDetail)
                            ? "GUID"
                            : (parsed.IncludePathIds.Count > 0 && parsed.PositiveTerms.Count == 0 && parsed.PositiveRegexes.Count == 0
                                ? "PathID"
                                : matchReason);
                        var detail = !string.IsNullOrEmpty(guidDetail)
                            ? guidDetail
                            : BuildMetadataDetail(container, pathId, arena.GetByteSize(i));

                        hits.Add(new GlobalSearchHit
                        {
                            AssetUniqueId = uid,
                            Name = string.IsNullOrWhiteSpace(name) ? $"{typeName} ({pathId})" : name,
                            AssetType = assetType,
                            TypeName = typeName,
                            Container = container,
                            SerializedFileName = sfName,
                            OriginalPath = origPath,
                            PathId = pathId,
                            ByteSize = arena.GetByteSize(i),
                            MatchReason = reason,
                            Detail = detail
                        });
                    }
                }
                else if (candidateTextIndices != null
                    && assetType == ClassIDType.TextAsset
                    && parsed.IncludeGuids.Count == 0)
                {
                    // Remember for bounded TextAsset content search pass.
                    candidateTextIndices.Add(i);
                }
            }

            // Pass 2: Bounded TextAsset content search (never OOMs, strictly capped by size and count).
            if (candidateTextIndices != null && candidateTextIndices.Count > 0)
            {
                textEligible = candidateTextIndices.Count;
                if (hits.Count < maxResults)
                {
                    var attemptedLazyLoadPaths = new HashSet<string>(
                        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

                    for (var c = 0; c < candidateTextIndices.Count; c++)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (hits.Count >= maxResults)
                        {
                            resultLimitReached = true;
                            break;
                        }

                        if (textScanned >= maxTextCount)
                        {
                            textCountLimitReached = true;
                            textSkippedByCountCap = candidateTextIndices.Count - c;
                            break;
                        }

                        var idx = candidateTextIndices[c];
                        var byteSize = arena.GetByteSize(idx);
                        if (byteSize > maxTextBytes)
                        {
                            textSkippedByCap++;
                            continue;
                        }

                        var bytes = TryReadTextAssetBytesFromArena(projectIndex, idx, assetsManager, textContentOverride, attemptedLazyLoadPaths);
                        if (bytes == null)
                        {
                            textUnavailable++;
                            continue;
                        }

                        if (bytes.LongLength > maxTextBytes)
                        {
                            textSkippedByCap++;
                            continue;
                        }

                        textScanned++;
                        if (bytes.Length == 0)
                        {
                            continue;
                        }

                        if (TryMatchTextContent(bytes, parsed, out var byteOffset, out var snippet))
                        {
                            var uid = arena.GetUniqueId(idx);
                            if (!hitUniqueIds.Add(uid))
                            {
                                continue;
                            }

                            var origin = arena.GetOrigin(idx);
                            var name = arena.GetName(idx);
                            var pathId = arena.GetPathId(idx);
                            var typeName = AssetItem.GetCachedTypeString(ClassIDType.TextAsset);
                            hits.Add(new GlobalSearchHit
                            {
                                AssetUniqueId = uid,
                                Name = string.IsNullOrWhiteSpace(name) ? $"TextAsset ({pathId})" : name,
                                AssetType = ClassIDType.TextAsset,
                                TypeName = typeName,
                                Container = arena.GetContainer(idx),
                                SerializedFileName = origin.SerializedFileName,
                                OriginalPath = origin.OriginalPath,
                                PathId = pathId,
                                ByteSize = byteSize > 0 ? byteSize : bytes.LongLength,
                                MatchReason = "TextAsset Content",
                                Detail = snippet,
                                MatchByteOffset = byteOffset
                            });
                        }
                    }
                }
            }
        }

        private static void ScanLoadedAssetsFiles(
            AssetsManager assetsManager,
            ParsedQuery parsed,
            GlobalSearchQuery query,
            int maxResults,
            int maxTextCount,
            long maxTextBytes,
            List<GlobalSearchHit> hits,
            HashSet<string> hitUniqueIds,
            ref int totalScanned,
            ref int textEligible,
            ref int textScanned,
            ref int textSkippedByCap,
            ref int textSkippedByCountCap,
            ref bool resultLimitReached,
            ref bool textCountLimitReached,
            CancellationToken ct)
        {
            var canSearchTextContent = query.SearchTextAssetContents
                && (parsed.PositiveTerms.Count > 0 || parsed.PositiveRegexes.Count > 0);
            var files = assetsManager.assetsFileList;

            for (var f = 0; f < files.Count; f++)
            {
                var sf = files[f];
                if (sf?.Objects == null)
                {
                    continue;
                }

                var sfName = sf.fileName ?? string.Empty;
                var origPath = sf.originalPath ?? string.Empty;
                if (!MatchesFileConstraints(sfName, origPath, parsed))
                {
                    continue;
                }

                for (var i = 0; i < sf.Objects.Count; i++)
                {
                    if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                    var obj = sf.Objects[i];
                    if (obj == null) continue;

                    totalScanned++;
                    var assetType = obj.type;
                    if (!MatchesTypeConstraints(assetType, query.TypeFilter, parsed))
                    {
                        continue;
                    }

                    var pathId = obj.m_PathID;
                    if (!MatchesPathIdConstraints(pathId, parsed))
                    {
                        continue;
                    }

                    var name = obj is NamedObject named ? (named.m_Name ?? string.Empty) : string.Empty;
                    var typeName = AssetItem.GetCachedTypeString(assetType);
                    if (MatchesNegativeTerms(name, string.Empty, sfName, typeName, parsed))
                    {
                        continue;
                    }

                    var guidDetail = string.Empty;
                    if (parsed.IncludeGuids.Count > 0)
                    {
                        if (!MatchesAnyGuid(parsed.IncludeGuids, assetType, typeName, sfName, pathId, name, string.Empty, obj, out guidDetail))
                        {
                            continue;
                        }
                    }

                    if (MatchesPositiveMetadata(name, string.Empty, sfName, typeName, pathId, parsed, out var matchReason))
                    {
                        if (hits.Count >= maxResults)
                        {
                            resultLimitReached = true;
                            return;
                        }

                        var uid = AssetHandle.BuildUniqueID(sf, pathId);
                        if (hitUniqueIds.Add(uid))
                        {
                            hits.Add(new GlobalSearchHit
                            {
                                AssetUniqueId = uid,
                                Name = string.IsNullOrWhiteSpace(name) ? $"{typeName} ({pathId})" : name,
                                AssetType = assetType,
                                TypeName = typeName,
                                Container = string.Empty,
                                SerializedFileName = sfName,
                                OriginalPath = origPath,
                                PathId = pathId,
                                ByteSize = obj.byteSize,
                                MatchReason = !string.IsNullOrEmpty(guidDetail) ? "GUID" : matchReason,
                                Detail = !string.IsNullOrEmpty(guidDetail) ? guidDetail : BuildMetadataDetail(string.Empty, pathId, obj.byteSize)
                            });
                        }
                    }
                    else if (canSearchTextContent && obj is TextAsset textAsset && parsed.IncludeGuids.Count == 0)
                    {
                        textEligible++;
                        var bytes = textAsset.m_Script;
                        if (bytes == null) continue;
                        if (bytes.LongLength > maxTextBytes)
                        {
                            textSkippedByCap++;
                            continue;
                        }

                        if (textScanned >= maxTextCount)
                        {
                            textCountLimitReached = true;
                            textSkippedByCountCap++;
                            continue;
                        }

                        textScanned++;
                        if (bytes.Length > 0 && TryMatchTextContent(bytes, parsed, out var byteOffset, out var snippet))
                        {
                            if (hits.Count >= maxResults)
                            {
                                resultLimitReached = true;
                                return;
                            }

                            var uid = AssetHandle.BuildUniqueID(sf, pathId);
                            if (hitUniqueIds.Add(uid))
                            {
                                hits.Add(new GlobalSearchHit
                                {
                                    AssetUniqueId = uid,
                                    Name = string.IsNullOrWhiteSpace(name) ? $"TextAsset ({pathId})" : name,
                                    AssetType = ClassIDType.TextAsset,
                                    TypeName = typeName,
                                    Container = string.Empty,
                                    SerializedFileName = sfName,
                                    OriginalPath = origPath,
                                    PathId = pathId,
                                    ByteSize = bytes.LongLength,
                                    MatchReason = "TextAsset Content",
                                    Detail = snippet,
                                    MatchByteOffset = byteOffset
                                });
                            }
                        }
                    }
                }
            }
        }

        private static byte[]? TryReadTextAssetBytesFromArena(
            ProjectIndex projectIndex,
            int arenaIndex,
            AssetsManager? assetsManager,
            Func<AssetHandle, byte[]?>? textContentOverride,
            HashSet<string>? attemptedLazyLoadPaths)
        {
            var arena = projectIndex.Arena;
            if (arena.GetRealObject(arenaIndex) is TextAsset materialized)
            {
                return materialized.m_Script;
            }

            if (textContentOverride != null)
            {
                var handle = projectIndex.MaterializeHandle(arenaIndex);
                return textContentOverride(handle);
            }

            if (assetsManager == null)
            {
                return null;
            }

            var sf = arena.GetSourceFile(arenaIndex);
            if (sf == null)
            {
                var origin = arena.GetOrigin(arenaIndex);
                if (!assetsManager.TryFindSerializedFile(origin.SerializedFileName, origin.OriginalPath, out sf) || sf == null)
                {
                    // In Lazy Mode / hydrated SQLite cache, the source file may not be loaded into assetsFileList yet.
                    // Load it on demand (bounded per unique disk path and capped by MaxTextAssetsScanned).
                    if (!string.IsNullOrWhiteSpace(origin.OriginalPath)
                        && System.IO.File.Exists(origin.OriginalPath)
                        && (attemptedLazyLoadPaths == null || attemptedLazyLoadPaths.Add(origin.OriginalPath)))
                    {
                        try
                        {
                            assetsManager.LoadFilesForPreview(origin.OriginalPath);
                        }
                        catch
                        {
                        }
                    }

                    if (!assetsManager.TryFindSerializedFile(origin.SerializedFileName, origin.OriginalPath, out sf) || sf == null)
                    {
                        return null;
                    }
                }
            }

            var pathId = arena.GetPathId(arenaIndex);
            if (sf.TryGetObject(pathId, out var existingObj) && existingObj is TextAsset existingText)
            {
                return existingText.m_Script;
            }

            var matHandle = projectIndex.MaterializeHandle(arenaIndex);
            if (matHandle.SourceFile == null)
            {
                matHandle.SourceFile = sf;
            }

            return assetsManager.ResolveHandle(matHandle) is TextAsset resolved ? resolved.m_Script : null;
        }

        private static bool TryMatchTextContent(
            byte[] scriptBytes,
            ParsedQuery parsed,
            out long matchByteOffset,
            out string snippet)
        {
            matchByteOffset = -1;
            snippet = string.Empty;

            var text = DecodeTextBestEffort(scriptBytes);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // Respect negative exclusions against content too.
            for (var i = 0; i < parsed.ExcludeTerms.Count; i++)
            {
                if (text.Contains(parsed.ExcludeTerms[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            for (var i = 0; i < parsed.ExcludeRegexes.Count; i++)
            {
                if (IsRegexMatchSafe(parsed.ExcludeRegexes[i], text))
                {
                    return false;
                }
            }

            var firstMatchCharIndex = -1;

            for (var i = 0; i < parsed.PositiveTerms.Count; i++)
            {
                var idx = text.IndexOf(parsed.PositiveTerms[i], StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return false;
                }

                if (firstMatchCharIndex < 0 || idx < firstMatchCharIndex)
                {
                    firstMatchCharIndex = idx;
                }
            }

            for (var i = 0; i < parsed.PositiveRegexes.Count; i++)
            {
                if (!TryRegexMatchSafe(parsed.PositiveRegexes[i], text, out var match) || !match.Success)
                {
                    return false;
                }

                if (firstMatchCharIndex < 0 || match.Index < firstMatchCharIndex)
                {
                    firstMatchCharIndex = match.Index;
                }
            }

            if (firstMatchCharIndex < 0)
            {
                return false;
            }

            matchByteOffset = Encoding.UTF8.GetByteCount(text.AsSpan(0, firstMatchCharIndex));
            snippet = ExtractLineSnippet(text, firstMatchCharIndex, matchByteOffset);
            return true;
        }

        private static string DecodeTextBestEffort(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static string ExtractLineSnippet(string text, int charIndex, long byteOffset)
        {
            var lineNumber = 1;
            var lineStart = 0;
            for (var i = 0; i < charIndex && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lineNumber++;
                    lineStart = i + 1;
                }
            }

            var lineEnd = text.IndexOf('\n', charIndex);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            const int maxSnippetChars = 120;
            var sliceStart = lineStart;
            var sliceLen = lineEnd - lineStart;
            var prefix = string.Empty;
            var suffix = string.Empty;

            if (sliceLen > maxSnippetChars)
            {
                sliceStart = Math.Max(lineStart, charIndex - 40);
                var maxEnd = Math.Min(lineEnd, sliceStart + maxSnippetChars);
                sliceLen = maxEnd - sliceStart;
                if (sliceStart > lineStart) prefix = "...";
                if (maxEnd < lineEnd) suffix = "...";
            }

            var rawLine = text.Substring(sliceStart, Math.Max(0, sliceLen)).Replace('\t', ' ').Trim();
            return $"Line {lineNumber} (offset {byteOffset:N0} B): {prefix}{rawLine}{suffix}";
        }

        private static bool MatchesTypeConstraints(ClassIDType assetType, ClassIDType? explicitFilter, ParsedQuery parsed)
        {
            if (explicitFilter.HasValue && assetType != explicitFilter.Value)
            {
                return false;
            }

            if (parsed.IncludeTypes.Count > 0 && !parsed.IncludeTypes.Contains(assetType))
            {
                return false;
            }

            if (parsed.ExcludeTypes.Count > 0 && parsed.ExcludeTypes.Contains(assetType))
            {
                return false;
            }

            return true;
        }

        private static bool MatchesPathIdConstraints(long pathId, ParsedQuery parsed)
        {
            if (parsed.HasInvalidPathIdFilter)
            {
                return false;
            }

            if (parsed.IncludePathIds.Count > 0 && !parsed.IncludePathIds.Contains(pathId))
            {
                return false;
            }

            if (parsed.ExcludePathIds.Count > 0 && parsed.ExcludePathIds.Contains(pathId))
            {
                return false;
            }

            return true;
        }

        private static bool MatchesFileConstraints(string sfName, string origPath, ParsedQuery parsed)
        {
            for (var i = 0; i < parsed.IncludeFiles.Count; i++)
            {
                var term = parsed.IncludeFiles[i];
                if (!sfName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !origPath.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            for (var i = 0; i < parsed.ExcludeFiles.Count; i++)
            {
                var term = parsed.ExcludeFiles[i];
                if (sfName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || origPath.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesNegativeTerms(string name, string container, string sfName, string typeName, ParsedQuery parsed)
        {
            for (var i = 0; i < parsed.ExcludeTerms.Count; i++)
            {
                var term = parsed.ExcludeTerms[i];
                if (name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || container.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || sfName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            for (var i = 0; i < parsed.ExcludeRegexes.Count; i++)
            {
                var rx = parsed.ExcludeRegexes[i];
                if (IsRegexMatchSafe(rx, name)
                    || IsRegexMatchSafe(rx, container)
                    || IsRegexMatchSafe(rx, sfName)
                    || IsRegexMatchSafe(rx, typeName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesPositiveMetadata(
            string name,
            string container,
            string sfName,
            string typeName,
            long pathId,
            ParsedQuery parsed,
            out string matchReason)
        {
            matchReason = "Filter";
            if (parsed.PositiveTerms.Count == 0 && parsed.PositiveRegexes.Count == 0)
            {
                return true;
            }

            string? pathIdText = null;
            for (var i = 0; i < parsed.PositiveTerms.Count; i++)
            {
                var term = parsed.PositiveTerms[i];
                if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matchReason = "Name";
                    continue;
                }
                if (container.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matchReason = "Container";
                    continue;
                }
                if (sfName.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matchReason = "SourceFile";
                    continue;
                }
                if (typeName.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matchReason = "Type";
                    continue;
                }

                pathIdText ??= pathId.ToString(CultureInfo.InvariantCulture);
                if (pathIdText.Contains(term, StringComparison.Ordinal))
                {
                    matchReason = "PathID";
                    continue;
                }

                return false;
            }

            for (var i = 0; i < parsed.PositiveRegexes.Count; i++)
            {
                var rx = parsed.PositiveRegexes[i];
                if (IsRegexMatchSafe(rx, name))
                {
                    matchReason = "Name (Regex)";
                    continue;
                }
                if (IsRegexMatchSafe(rx, container))
                {
                    matchReason = "Container (Regex)";
                    continue;
                }
                if (IsRegexMatchSafe(rx, sfName))
                {
                    matchReason = "SourceFile (Regex)";
                    continue;
                }
                if (IsRegexMatchSafe(rx, typeName))
                {
                    matchReason = "Type (Regex)";
                    continue;
                }

                pathIdText ??= pathId.ToString(CultureInfo.InvariantCulture);
                if (IsRegexMatchSafe(rx, pathIdText))
                {
                    matchReason = "PathID (Regex)";
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool MatchesAnyGuid(
            IReadOnlyList<string> normalizedGuidQueries,
            ClassIDType assetType,
            string typeName,
            string sfName,
            long pathId,
            string name,
            string container,
            AssetStudio.Object? realObject,
            out string detail)
        {
            detail = string.Empty;

            // Compute deterministic UnityProjectGuidUtility candidates (both with null fallback and with name/sanitized fallback).
            var guidNoFallback = UnityProjectGuidUtility.ComputeAssetGuid(typeName, sfName, pathId, fallbackKey: null);
            var guidWithName = !string.IsNullOrWhiteSpace(name)
                ? UnityProjectGuidUtility.ComputeAssetGuid(typeName, sfName, pathId, name)
                : string.Empty;
            var sanitizedName = !string.IsNullOrWhiteSpace(name)
                ? UnityProjectExporterService.SanitizeFileName(name, typeName)
                : string.Empty;
            var guidWithSanitized = !string.IsNullOrEmpty(sanitizedName) && !string.Equals(sanitizedName, name, StringComparison.Ordinal)
                ? UnityProjectGuidUtility.ComputeAssetGuid(typeName, sfName, pathId, sanitizedName)
                : string.Empty;

            var scriptGuid = string.Empty;
            if (assetType == ClassIDType.MonoScript && realObject is MonoScript ms)
            {
                scriptGuid = UnityProjectGuidUtility.ComputeScriptGuid(ms.m_AssemblyName, ms.m_Namespace, ms.m_ClassName);
            }

            for (var i = 0; i < normalizedGuidQueries.Count; i++)
            {
                var q = normalizedGuidQueries[i];
                if (guidNoFallback.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"Asset GUID: {guidNoFallback}";
                    return true;
                }
                if (!string.IsNullOrEmpty(guidWithName) && guidWithName.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"Asset GUID: {guidWithName}";
                    return true;
                }
                if (!string.IsNullOrEmpty(guidWithSanitized) && guidWithSanitized.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"Asset GUID: {guidWithSanitized}";
                    return true;
                }
                if (!string.IsNullOrEmpty(scriptGuid) && scriptGuid.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"Script GUID: {scriptGuid}";
                    return true;
                }
                if (!string.IsNullOrEmpty(container) && container.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"Container GUID match: {q}";
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseQuery(GlobalSearchQuery query, out ParsedQuery parsed, out string? errorMessage)
        {
            parsed = new ParsedQuery();
            errorMessage = null;

            var tokens = Tokenize(query.QueryText ?? string.Empty);
            for (var i = 0; i < tokens.Count; i++)
            {
                var raw = tokens[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var isNegated = raw.Length > 1 && raw[0] == '-';
                var token = isNegated ? raw.Substring(1) : raw;
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var colonIdx = token.IndexOf(':');
                if (colonIdx > 0 && colonIdx + 1 < token.Length)
                {
                    var key = token.Substring(0, colonIdx);
                    var value = Unquote(token.Substring(colonIdx + 1));

                    if (key.Equals("pathid", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                        {
                            if (isNegated) parsed.ExcludePathIds.Add(pid);
                            else parsed.IncludePathIds.Add(pid);
                        }
                        else if (!isNegated)
                        {
                            parsed.HasInvalidPathIdFilter = true;
                        }
                        continue;
                    }

                    if (key.Equals("guid", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleanGuid = value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(cleanGuid) && !isNegated)
                        {
                            parsed.IncludeGuids.Add(cleanGuid);
                        }
                        continue;
                    }

                    if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParseClassIdType(value, out var classType))
                        {
                            if (isNegated) parsed.ExcludeTypes.Add(classType);
                            else parsed.IncludeTypes.Add(classType);
                        }
                        else if (!isNegated)
                        {
                            // Unknown explicit type filter should yield 0 results instead of silently ignoring it.
                            parsed.IncludeTypes.Add((ClassIDType)(-999999));
                        }
                        continue;
                    }

                    if (key.Equals("file", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            if (isNegated) parsed.ExcludeFiles.Add(value);
                            else parsed.IncludeFiles.Add(value);
                        }
                        continue;
                    }
                }

                var textToken = Unquote(token);
                if (string.IsNullOrEmpty(textToken))
                {
                    continue;
                }

                if (query.UseRegex)
                {
                    try
                    {
                        var rx = new Regex(textToken, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexMatchTimeout);
                        if (isNegated) parsed.ExcludeRegexes.Add(rx);
                        else parsed.PositiveRegexes.Add(rx);
                    }
                    catch (ArgumentException ex)
                    {
                        errorMessage = $"Invalid regular expression '{textToken}': {ex.Message}";
                        return false;
                    }
                }
                else
                {
                    if (isNegated) parsed.ExcludeTerms.Add(textToken);
                    else parsed.PositiveTerms.Add(textToken);
                }
            }

            return true;
        }

        private static List<string> Tokenize(string input)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(input))
            {
                return tokens;
            }

            var sb = new StringBuilder(input.Length);
            var inQuotes = false;

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static bool TryParseClassIdType(string text, out ClassIDType classType)
        {
            if (Enum.TryParse<ClassIDType>(text, ignoreCase: true, out classType))
            {
                return true;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                classType = (ClassIDType)numeric;
                return true;
            }

            classType = ClassIDType.UnknownType;
            return false;
        }

        private static bool IsRegexMatchSafe(Regex regex, string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            try
            {
                return regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static bool TryRegexMatchSafe(Regex regex, string input, out Match match)
        {
            try
            {
                match = regex.Match(input);
                return match.Success;
            }
            catch (RegexMatchTimeoutException)
            {
                match = Match.Empty;
                return false;
            }
        }

        private static string BuildMetadataDetail(string container, long pathId, long byteSize)
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(container))
            {
                parts.Add(container);
            }
            if (pathId != 0)
            {
                parts.Add($"PathID: {pathId.ToString(CultureInfo.InvariantCulture)}");
            }
            if (byteSize > 0)
            {
                parts.Add($"{byteSize:N0} B");
            }

            return string.Join(" • ", parts);
        }

        private static string FormatByteLimit(long bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return $"{bytes / (1024.0 * 1024.0):0.#} MiB";
            }
            if (bytes >= 1024)
            {
                return $"{bytes / 1024.0:0.#} KiB";
            }

            return $"{bytes:N0} B";
        }

        private sealed class ParsedQuery
        {
            public List<string> PositiveTerms { get; } = new();
            public List<string> ExcludeTerms { get; } = new();
            public List<Regex> PositiveRegexes { get; } = new();
            public List<Regex> ExcludeRegexes { get; } = new();
            public HashSet<long> IncludePathIds { get; } = new();
            public HashSet<long> ExcludePathIds { get; } = new();
            public bool HasInvalidPathIdFilter { get; set; }
            public List<string> IncludeGuids { get; } = new();
            public HashSet<ClassIDType> IncludeTypes { get; } = new();
            public HashSet<ClassIDType> ExcludeTypes { get; } = new();
            public List<string> IncludeFiles { get; } = new();
            public List<string> ExcludeFiles { get; } = new();

            public bool IsEmpty =>
                PositiveTerms.Count == 0
                && ExcludeTerms.Count == 0
                && PositiveRegexes.Count == 0
                && ExcludeRegexes.Count == 0
                && IncludePathIds.Count == 0
                && ExcludePathIds.Count == 0
                && !HasInvalidPathIdFilter
                && IncludeGuids.Count == 0
                && IncludeTypes.Count == 0
                && ExcludeTypes.Count == 0
                && IncludeFiles.Count == 0
                && ExcludeFiles.Count == 0;
        }
    }
}
