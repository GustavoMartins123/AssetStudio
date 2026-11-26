using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis
{
    public enum BuildDiffChangeKind
    {
        Unchanged = 0,
        Added = 1,
        Removed = 2,
        Modified = 3,
        Moved = 4
    }

    public sealed class BuildDiffFileEntry
    {
        public string RelativePath { get; init; } = string.Empty;
        public BuildDiffChangeKind ChangeKind { get; init; }
        public ProjectFileKind FileKind { get; init; }
        public long BaselineSize { get; init; }
        public long TargetSize { get; init; }
        public long SizeDelta => TargetSize - BaselineSize;
        public long BaselineLastWriteUtcTicks { get; init; }
        public long TargetLastWriteUtcTicks { get; init; }
        public ulong BaselineSampleHash { get; init; }
        public ulong TargetSampleHash { get; init; }
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class BuildDiffAssetEntry
    {
        public BuildDiffChangeKind ChangeKind { get; init; }
        public string AssetUniqueId { get; init; } = string.Empty;
        public string BaselineAssetUniqueId { get; init; } = string.Empty;
        public string TargetAssetUniqueId { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string BaselineName { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public ClassIDType AssetType { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public ClassIDType BaselineType { get; init; }
        public ClassIDType TargetType { get; init; }
        public string SerializedFileName { get; init; } = string.Empty;
        public string BaselineSerializedFileName { get; init; } = string.Empty;
        public string TargetSerializedFileName { get; init; } = string.Empty;
        public string BaselineOriginalPath { get; init; } = string.Empty;
        public string TargetOriginalPath { get; init; } = string.Empty;
        public string BaselineContainer { get; init; } = string.Empty;
        public string TargetContainer { get; init; } = string.Empty;
        public long BaselineByteSize { get; init; }
        public long TargetByteSize { get; init; }
        public long SizeDelta => TargetByteSize - BaselineByteSize;
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class BuildDiffReport
    {
        public string BaselineLabel { get; init; } = string.Empty;
        public string TargetLabel { get; init; } = string.Empty;
        public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
        public long ElapsedMs { get; init; }
        public bool HasAssetLevelDiff { get; init; }
        public string AssetDiffNote { get; init; } = string.Empty;

        public int AddedFiles { get; init; }
        public int RemovedFiles { get; init; }
        public int ModifiedFiles { get; init; }
        public int UnchangedFiles { get; init; }
        public int TotalBaselineFiles { get; init; }
        public int TotalTargetFiles { get; init; }
        public long TotalBaselineBytes { get; init; }
        public long TotalTargetBytes { get; init; }
        public long NetFileSizeDeltaBytes => TotalTargetBytes - TotalBaselineBytes;

        public int AddedAssets { get; init; }
        public int RemovedAssets { get; init; }
        public int ModifiedAssets { get; init; }
        public int MovedAssets { get; init; }
        public int UnchangedAssets { get; init; }
        public int TotalBaselineAssets { get; init; }
        public int TotalTargetAssets { get; init; }

        public IReadOnlyList<BuildDiffFileEntry> FileEntries { get; init; } = Array.Empty<BuildDiffFileEntry>();
        public IReadOnlyList<BuildDiffAssetEntry> AssetEntries { get; init; } = Array.Empty<BuildDiffAssetEntry>();
    }

    public static class BuildDiffService
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static BuildDiffReport CompareFolders(
            string baselineFolder,
            string targetFolder,
            SQLiteProjectIndexCache? sqliteCache = null,
            ProjectIndex? loadedBaselineIndex = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(baselineFolder))
            {
                throw new ArgumentException("Baseline folder path must not be empty.", nameof(baselineFolder));
            }

            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                throw new ArgumentException("Target folder path must not be empty.", nameof(targetFolder));
            }

            if (!Directory.Exists(baselineFolder))
            {
                throw new DirectoryNotFoundException($"Baseline folder not found: {baselineFolder}");
            }

            if (!Directory.Exists(targetFolder))
            {
                throw new DirectoryNotFoundException($"Target folder not found: {targetFolder}");
            }

            var sw = Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();

            var baselineScan = ProjectScanner.ScanFolder(baselineFolder, ct);
            ct.ThrowIfCancellationRequested();
            var targetScan = ProjectScanner.ScanFolder(targetFolder, ct);
            ct.ThrowIfCancellationRequested();

            var caseSensitive = sqliteCache?.CaseSensitivePaths ?? OperatingSystem.IsLinux();
            var filteredBaselineFiles = FilterRelevantScanFiles(baselineScan.Files, targetScan.Files);
            var filteredTargetFiles = FilterRelevantScanFiles(targetScan.Files, baselineScan.Files);

            var fileEntries = DiffScannedFiles(
                baselineScan.RootPath,
                filteredBaselineFiles,
                targetScan.RootPath,
                filteredTargetFiles,
                caseSensitive,
                ct,
                out var fileCounts,
                out var baselineBytes,
                out var targetBytes);

            // Attempt asset-level diff from loadedBaselineIndex / .idx sidecar / SQLite cache.
            BuildDiffSnapshotLoadResult baselineSnapshots;
            if (loadedBaselineIndex != null && loadedBaselineIndex.Count > 0)
            {
                var extracted = ExtractSnapshotsFromProjectIndex(loadedBaselineIndex, ct);
                baselineSnapshots = new BuildDiffSnapshotLoadResult
                {
                    HasSnapshots = extracted.Count > 0,
                    Source = "LoadedIndex",
                    DiagnosticNote = $"Loaded {extracted.Count:N0} baseline asset(s) from active in-memory index.",
                    Snapshots = extracted
                };
            }
            else if (sqliteCache != null)
            {
                var baselineSig = sqliteCache.GetFolderSignature(baselineScan);
                baselineSnapshots = sqliteCache.LoadAssetDiffSnapshots(baselineFolder, baselineSig, ct);
            }
            else
            {
                baselineSnapshots = BuildDiffSnapshotLoadResult.Unavailable(
                    "No SQLite index cache or loaded ProjectIndex was provided for baseline folder.");
            }

            ct.ThrowIfCancellationRequested();

            BuildDiffSnapshotLoadResult targetSnapshots;
            if (sqliteCache != null)
            {
                var targetSig = sqliteCache.GetFolderSignature(targetScan);
                targetSnapshots = sqliteCache.LoadAssetDiffSnapshots(targetFolder, targetSig, ct);
            }
            else
            {
                targetSnapshots = BuildDiffSnapshotLoadResult.Unavailable(
                    "No SQLite index cache was provided for target folder.");
            }

            var hasAssetDiff = baselineSnapshots.HasSnapshots && targetSnapshots.HasSnapshots;
            IReadOnlyList<BuildDiffAssetEntry> assetEntries = Array.Empty<BuildDiffAssetEntry>();
            AssetDiffCounts assetCounts = default;
            string assetDiffNote;

            if (hasAssetDiff)
            {
                assetEntries = DiffAssetSnapshotsCore(baselineSnapshots.Snapshots, targetSnapshots.Snapshots, ct, out assetCounts);
                assetDiffNote = $"Asset-level diff computed: {baselineSnapshots.Snapshots.Count:N0} baseline ({baselineSnapshots.Source}) vs {targetSnapshots.Snapshots.Count:N0} target ({targetSnapshots.Source}).";
            }
            else
            {
                var missingReasons = new List<string>(2);
                if (!baselineSnapshots.HasSnapshots)
                {
                    missingReasons.Add($"Baseline: {baselineSnapshots.DiagnosticNote}");
                }
                if (!targetSnapshots.HasSnapshots)
                {
                    missingReasons.Add($"Target: {targetSnapshots.DiagnosticNote}");
                }

                assetDiffNote = "File-level diff only (open and index both folders to enable asset-level diff). " + string.Join(" ", missingReasons);
            }

            sw.Stop();
            return new BuildDiffReport
            {
                BaselineLabel = baselineFolder,
                TargetLabel = targetFolder,
                GeneratedUtc = DateTime.UtcNow,
                ElapsedMs = sw.ElapsedMilliseconds,
                HasAssetLevelDiff = hasAssetDiff,
                AssetDiffNote = assetDiffNote,
                AddedFiles = fileCounts.Added,
                RemovedFiles = fileCounts.Removed,
                ModifiedFiles = fileCounts.Modified,
                UnchangedFiles = fileCounts.Unchanged,
                TotalBaselineFiles = filteredBaselineFiles.Count,
                TotalTargetFiles = filteredTargetFiles.Count,
                TotalBaselineBytes = baselineBytes,
                TotalTargetBytes = targetBytes,
                AddedAssets = assetCounts.Added,
                RemovedAssets = assetCounts.Removed,
                ModifiedAssets = assetCounts.Modified,
                MovedAssets = assetCounts.Moved,
                UnchangedAssets = assetCounts.Unchanged,
                TotalBaselineAssets = hasAssetDiff ? baselineSnapshots.Snapshots.Count : 0,
                TotalTargetAssets = hasAssetDiff ? targetSnapshots.Snapshots.Count : 0,
                FileEntries = fileEntries,
                AssetEntries = assetEntries
            };
        }

        public static BuildDiffReport CompareAssetSnapshots(
            string baselineLabel,
            IReadOnlyList<BuildDiffAssetSnapshot> baseline,
            string targetLabel,
            IReadOnlyList<BuildDiffAssetSnapshot> target,
            IReadOnlyList<ScannedFileInfo>? baselineFiles = null,
            IReadOnlyList<ScannedFileInfo>? targetFiles = null)
        {
            return CompareAssetSnapshots(
                baselineLabel,
                baseline,
                targetLabel,
                target,
                baselineFiles,
                targetFiles,
                CancellationToken.None);
        }

        public static BuildDiffReport CompareAssetSnapshots(
            string baselineLabel,
            IReadOnlyList<BuildDiffAssetSnapshot> baseline,
            string targetLabel,
            IReadOnlyList<BuildDiffAssetSnapshot> target,
            IReadOnlyList<ScannedFileInfo>? baselineFiles,
            IReadOnlyList<ScannedFileInfo>? targetFiles,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(target);

            var sw = Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<BuildDiffFileEntry> fileEntries = Array.Empty<BuildDiffFileEntry>();
            FileDiffCounts fileCounts = default;
            long baselineBytes = 0;
            long targetBytes = 0;
            var totalBaselineFiles = 0;
            var totalTargetFiles = 0;

            if (baselineFiles != null || targetFiles != null)
            {
                var safeBaseFiles = baselineFiles ?? Array.Empty<ScannedFileInfo>();
                var safeTargetFiles = targetFiles ?? Array.Empty<ScannedFileInfo>();
                var filteredBase = FilterRelevantScanFiles(safeBaseFiles, safeTargetFiles);
                var filteredTarget = FilterRelevantScanFiles(safeTargetFiles, safeBaseFiles);
                totalBaselineFiles = filteredBase.Count;
                totalTargetFiles = filteredTarget.Count;

                var baseRoot = Directory.Exists(baselineLabel) ? baselineLabel : string.Empty;
                var targetRoot = Directory.Exists(targetLabel) ? targetLabel : string.Empty;
                fileEntries = DiffScannedFiles(
                    baseRoot,
                    filteredBase,
                    targetRoot,
                    filteredTarget,
                    OperatingSystem.IsLinux(),
                    ct,
                    out fileCounts,
                    out baselineBytes,
                    out targetBytes);
            }

            var assetEntries = DiffAssetSnapshotsCore(baseline, target, ct, out var assetCounts);
            sw.Stop();

            return new BuildDiffReport
            {
                BaselineLabel = baselineLabel ?? string.Empty,
                TargetLabel = targetLabel ?? string.Empty,
                GeneratedUtc = DateTime.UtcNow,
                ElapsedMs = sw.ElapsedMilliseconds,
                HasAssetLevelDiff = true,
                AssetDiffNote = $"Compared {baseline.Count:N0} baseline asset(s) vs {target.Count:N0} target asset(s).",
                AddedFiles = fileCounts.Added,
                RemovedFiles = fileCounts.Removed,
                ModifiedFiles = fileCounts.Modified,
                UnchangedFiles = fileCounts.Unchanged,
                TotalBaselineFiles = totalBaselineFiles,
                TotalTargetFiles = totalTargetFiles,
                TotalBaselineBytes = baselineBytes,
                TotalTargetBytes = targetBytes,
                AddedAssets = assetCounts.Added,
                RemovedAssets = assetCounts.Removed,
                ModifiedAssets = assetCounts.Modified,
                MovedAssets = assetCounts.Moved,
                UnchangedAssets = assetCounts.Unchanged,
                TotalBaselineAssets = baseline.Count,
                TotalTargetAssets = target.Count,
                FileEntries = fileEntries,
                AssetEntries = assetEntries
            };
        }

        public static List<BuildDiffAssetSnapshot> ExtractSnapshotsFromProjectIndex(
            ProjectIndex projectIndex,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(projectIndex);

            var arena = projectIndex.Arena;
            var count = arena.Count;
            var result = new List<BuildDiffAssetSnapshot>(count);
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var origin = arena.GetOrigin(i);
                result.Add(new BuildDiffAssetSnapshot(
                    AssetUniqueId: arena.GetUniqueId(i),
                    SerializedFileName: origin.SerializedFileName,
                    OriginalPath: origin.OriginalPath,
                    PathId: arena.GetPathId(i),
                    Name: arena.GetName(i),
                    Type: arena.GetType(i),
                    Container: arena.GetContainer(i),
                    ByteStart: arena.GetByteStart(i),
                    ByteSize: arena.GetByteSize(i)));
            }

            return result;
        }

        public static void ExportJson(BuildDiffReport report, string outputPath)
        {
            ArgumentNullException.ThrowIfNull(report);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path must not be empty.", nameof(outputPath));
            }

            EnsureParentDirectory(outputPath);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(outputPath, json, Utf8NoBom);
        }

        public static void ExportCsv(BuildDiffReport report, string outputPath)
        {
            ArgumentNullException.ThrowIfNull(report);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path must not be empty.", nameof(outputPath));
            }

            EnsureParentDirectory(outputPath);
            var sb = new StringBuilder(4096);
            sb.AppendLine("Scope,ChangeKind,Type,NameOrPath,PathID,BaselineSource,TargetSource,BaselineBytes,TargetBytes,SizeDelta,Detail");

            for (var i = 0; i < report.FileEntries.Count; i++)
            {
                var f = report.FileEntries[i];
                AppendCsvRow(
                    sb,
                    "File",
                    f.ChangeKind.ToString(),
                    f.FileKind.ToString(),
                    f.RelativePath,
                    string.Empty,
                    f.ChangeKind == BuildDiffChangeKind.Added ? string.Empty : f.RelativePath,
                    f.ChangeKind == BuildDiffChangeKind.Removed ? string.Empty : f.RelativePath,
                    f.BaselineSize.ToString(CultureInfo.InvariantCulture),
                    f.TargetSize.ToString(CultureInfo.InvariantCulture),
                    f.SizeDelta.ToString(CultureInfo.InvariantCulture),
                    f.Detail);
            }

            for (var i = 0; i < report.AssetEntries.Count; i++)
            {
                var a = report.AssetEntries[i];
                AppendCsvRow(
                    sb,
                    "Asset",
                    a.ChangeKind.ToString(),
                    a.TypeName,
                    a.Name,
                    a.PathId.ToString(CultureInfo.InvariantCulture),
                    a.BaselineSerializedFileName,
                    a.TargetSerializedFileName,
                    a.BaselineByteSize.ToString(CultureInfo.InvariantCulture),
                    a.TargetByteSize.ToString(CultureInfo.InvariantCulture),
                    a.SizeDelta.ToString(CultureInfo.InvariantCulture),
                    a.Detail);
            }

            File.WriteAllText(outputPath, sb.ToString(), Utf8NoBom);
        }

        public static string FormatSummaryMarkdown(BuildDiffReport report)
        {
            ArgumentNullException.ThrowIfNull(report);

            var sb = new StringBuilder(1024);
            sb.AppendLine("# Build Diff Report");
            sb.AppendLine();
            sb.AppendLine($"- **Baseline**: `{report.BaselineLabel}`");
            sb.AppendLine($"- **Target**: `{report.TargetLabel}`");
            sb.AppendLine($"- **Generated (UTC)**: `{report.GeneratedUtc:O}`");
            sb.AppendLine($"- **Elapsed**: {report.ElapsedMs:N0} ms");
            sb.AppendLine();

            sb.AppendLine("## File Diff Summary");
            sb.AppendLine();
            sb.AppendLine("| Metric | Count |");
            sb.AppendLine("| --- | ---: |");
            sb.AppendLine($"| Added | {report.AddedFiles:N0} |");
            sb.AppendLine($"| Removed | {report.RemovedFiles:N0} |");
            sb.AppendLine($"| Modified | {report.ModifiedFiles:N0} |");
            sb.AppendLine($"| Unchanged | {report.UnchangedFiles:N0} |");
            sb.AppendLine($"| Net Size Delta (bytes) | {FormatSignedBytes(report.NetFileSizeDeltaBytes)} |");
            sb.AppendLine();

            sb.AppendLine("## Asset Diff Summary");
            sb.AppendLine();
            if (!report.HasAssetLevelDiff)
            {
                sb.AppendLine($"> {report.AssetDiffNote}");
            }
            else
            {
                sb.AppendLine("| Metric | Count |");
                sb.AppendLine("| --- | ---: |");
                sb.AppendLine($"| Added | {report.AddedAssets:N0} |");
                sb.AppendLine($"| Removed | {report.RemovedAssets:N0} |");
                sb.AppendLine($"| Modified | {report.ModifiedAssets:N0} |");
                sb.AppendLine($"| Moved | {report.MovedAssets:N0} |");
                sb.AppendLine($"| Unchanged | {report.UnchangedAssets:N0} |");
            }

            var changedAssets = report.AssetEntries
                .Where(static a => a.ChangeKind != BuildDiffChangeKind.Unchanged)
                .Take(25)
                .ToList();
            if (changedAssets.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Top Asset Changes");
                sb.AppendLine();
                sb.AppendLine("| Change | Type | Name | PathID | Source | Detail |");
                sb.AppendLine("| --- | --- | --- | ---: | --- | --- |");
                foreach (var a in changedAssets)
                {
                    sb.AppendLine($"| {a.ChangeKind} | {EscapeMarkdownCell(a.TypeName)} | {EscapeMarkdownCell(a.Name)} | {a.PathId} | {EscapeMarkdownCell(a.SerializedFileName)} | {EscapeMarkdownCell(a.Detail)} |");
                }
            }

            return sb.ToString();
        }

        private static IReadOnlyList<ScannedFileInfo> FilterRelevantScanFiles(
            IReadOnlyList<ScannedFileInfo> files,
            IReadOnlyList<ScannedFileInfo> peerFiles)
        {
            var anyUnityAssets = files.Any(SQLiteProjectIndexCache.IsIndexRelevantScanFile)
                || peerFiles.Any(SQLiteProjectIndexCache.IsIndexRelevantScanFile);
            if (!anyUnityAssets)
            {
                return files;
            }

            var filtered = new List<ScannedFileInfo>(files.Count);
            for (var i = 0; i < files.Count; i++)
            {
                if (SQLiteProjectIndexCache.IsIndexRelevantScanFile(files[i]))
                {
                    filtered.Add(files[i]);
                }
            }

            return filtered;
        }

        private static List<BuildDiffFileEntry> DiffScannedFiles(
            string baselineRoot,
            IReadOnlyList<ScannedFileInfo> baselineFiles,
            string targetRoot,
            IReadOnlyList<ScannedFileInfo> targetFiles,
            bool caseSensitive,
            CancellationToken ct,
            out FileDiffCounts counts,
            out long totalBaselineBytes,
            out long totalTargetBytes)
        {
            counts = default;
            totalBaselineBytes = 0;
            totalTargetBytes = 0;

            var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var baselineMap = new Dictionary<string, (string DisplayPath, ScannedFileInfo Info)>(baselineFiles.Count, comparer);
            var targetMap = new Dictionary<string, (string DisplayPath, ScannedFileInfo Info)>(targetFiles.Count, comparer);

            var baseRootNorm = string.IsNullOrWhiteSpace(baselineRoot)
                ? string.Empty
                : SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(baselineRoot);
            var targetRootNorm = string.IsNullOrWhiteSpace(targetRoot)
                ? string.Empty
                : SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(targetRoot);

            for (var i = 0; i < baselineFiles.Count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var f = baselineFiles[i];
                if (f == null) continue;
                totalBaselineBytes += f.Size;
                var displayRel = GetDisplayRelativePath(baseRootNorm, f.Path);
                var key = SQLiteProjectIndexCache.NormalizeRelativeManifestPath(baseRootNorm, f.Path, caseSensitive);
                baselineMap[key] = (displayRel, f);
            }

            for (var i = 0; i < targetFiles.Count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var f = targetFiles[i];
                if (f == null) continue;
                totalTargetBytes += f.Size;
                var displayRel = GetDisplayRelativePath(targetRootNorm, f.Path);
                var key = SQLiteProjectIndexCache.NormalizeRelativeManifestPath(targetRootNorm, f.Path, caseSensitive);
                targetMap[key] = (displayRel, f);
            }

            var entries = new List<BuildDiffFileEntry>(baselineMap.Count + targetMap.Count);

            foreach (var kvp in baselineMap)
            {
                ct.ThrowIfCancellationRequested();
                var (baseRel, baseInfo) = kvp.Value;
                if (!targetMap.TryGetValue(kvp.Key, out var targetPair))
                {
                    counts.Removed++;
                    entries.Add(new BuildDiffFileEntry
                    {
                        RelativePath = baseRel,
                        ChangeKind = BuildDiffChangeKind.Removed,
                        FileKind = baseInfo.Kind,
                        BaselineSize = baseInfo.Size,
                        TargetSize = 0,
                        BaselineLastWriteUtcTicks = baseInfo.LastWriteTimeUtcTicks,
                        BaselineSampleHash = baseInfo.ContentSampleHash,
                        Detail = $"Removed ({baseInfo.Size:N0} B)"
                    });
                    continue;
                }

                var (_, targetInfo) = targetPair;
                var diffs = new List<string>(4);
                if (baseInfo.Size != targetInfo.Size)
                {
                    diffs.Add($"Size {baseInfo.Size:N0} -> {targetInfo.Size:N0} ({FormatSignedBytes(targetInfo.Size - baseInfo.Size)})");
                }
                if (baseInfo.ContentSampleHash != targetInfo.ContentSampleHash)
                {
                    diffs.Add($"Hash {baseInfo.ContentSampleHash:x16} -> {targetInfo.ContentSampleHash:x16}");
                }
                if (baseInfo.LastWriteTimeUtcTicks != targetInfo.LastWriteTimeUtcTicks)
                {
                    diffs.Add("Timestamp changed");
                }
                if (baseInfo.Kind != targetInfo.Kind)
                {
                    diffs.Add($"Kind {baseInfo.Kind} -> {targetInfo.Kind}");
                }

                if (diffs.Count > 0)
                {
                    counts.Modified++;
                    entries.Add(new BuildDiffFileEntry
                    {
                        RelativePath = targetPair.DisplayPath,
                        ChangeKind = BuildDiffChangeKind.Modified,
                        FileKind = targetInfo.Kind,
                        BaselineSize = baseInfo.Size,
                        TargetSize = targetInfo.Size,
                        BaselineLastWriteUtcTicks = baseInfo.LastWriteTimeUtcTicks,
                        TargetLastWriteUtcTicks = targetInfo.LastWriteTimeUtcTicks,
                        BaselineSampleHash = baseInfo.ContentSampleHash,
                        TargetSampleHash = targetInfo.ContentSampleHash,
                        Detail = string.Join("; ", diffs)
                    });
                }
                else
                {
                    counts.Unchanged++;
                    entries.Add(new BuildDiffFileEntry
                    {
                        RelativePath = targetPair.DisplayPath,
                        ChangeKind = BuildDiffChangeKind.Unchanged,
                        FileKind = targetInfo.Kind,
                        BaselineSize = baseInfo.Size,
                        TargetSize = targetInfo.Size,
                        BaselineLastWriteUtcTicks = baseInfo.LastWriteTimeUtcTicks,
                        TargetLastWriteUtcTicks = targetInfo.LastWriteTimeUtcTicks,
                        BaselineSampleHash = baseInfo.ContentSampleHash,
                        TargetSampleHash = targetInfo.ContentSampleHash,
                        Detail = "Unchanged"
                    });
                }
            }

            foreach (var kvp in targetMap)
            {
                ct.ThrowIfCancellationRequested();
                if (baselineMap.ContainsKey(kvp.Key))
                {
                    continue;
                }

                var (targetRel, targetInfo) = kvp.Value;
                counts.Added++;
                entries.Add(new BuildDiffFileEntry
                {
                    RelativePath = targetRel,
                    ChangeKind = BuildDiffChangeKind.Added,
                    FileKind = targetInfo.Kind,
                    BaselineSize = 0,
                    TargetSize = targetInfo.Size,
                    TargetLastWriteUtcTicks = targetInfo.LastWriteTimeUtcTicks,
                    TargetSampleHash = targetInfo.ContentSampleHash,
                    Detail = $"Added (+{targetInfo.Size:N0} B)"
                });
            }

            entries.Sort(static (a, b) =>
            {
                var cmp = GetChangeSortRank(a.ChangeKind).CompareTo(GetChangeSortRank(b.ChangeKind));
                if (cmp != 0) return cmp;
                return string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
            });

            return entries;
        }

        private static List<BuildDiffAssetEntry> DiffAssetSnapshotsCore(
            IReadOnlyList<BuildDiffAssetSnapshot> baseline,
            IReadOnlyList<BuildDiffAssetSnapshot> target,
            CancellationToken ct,
            out AssetDiffCounts counts)
        {
            counts = default;
            var entries = new List<BuildDiffAssetEntry>(Math.Max(baseline.Count, target.Count));

            // Step 1: Index target assets by (SerializedFileName, PathID)
            var targetByFileAndPath = new Dictionary<(string FileName, long PathId), Queue<BuildDiffAssetSnapshot>>(target.Count);
            for (var i = 0; i < target.Count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var t = target[i];
                var key = (NormalizeKey(t.SerializedFileName), t.PathId);
                if (!targetByFileAndPath.TryGetValue(key, out var queue))
                {
                    queue = new Queue<BuildDiffAssetSnapshot>(1);
                    targetByFileAndPath[key] = queue;
                }

                queue.Enqueue(t);
            }

            var unmatchedBaseline = new List<BuildDiffAssetSnapshot>();

            for (var i = 0; i < baseline.Count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var b = baseline[i];
                var key = (NormalizeKey(b.SerializedFileName), b.PathId);
                if (targetByFileAndPath.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    var changes = DescribeAssetFieldChanges(b, t);
                    if (changes.Length > 0)
                    {
                        counts.Modified++;
                        entries.Add(BuildAssetEntry(BuildDiffChangeKind.Modified, b, t, changes));
                    }
                    else
                    {
                        counts.Unchanged++;
                        entries.Add(BuildAssetEntry(BuildDiffChangeKind.Unchanged, b, t, "Unchanged"));
                    }
                }
                else
                {
                    unmatchedBaseline.Add(b);
                }
            }

            // Collect remaining unmatched target assets and index by (PathID, Type, Name) to detect Moved assets!
            var unmatchedTargetByMoveKey = new Dictionary<(long PathId, ClassIDType Type, string Name), Queue<BuildDiffAssetSnapshot>>();
            foreach (var queue in targetByFileAndPath.Values)
            {
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    var moveKey = (t.PathId, t.Type, NormalizeKey(t.Name));
                    if (!unmatchedTargetByMoveKey.TryGetValue(moveKey, out var mq))
                    {
                        mq = new Queue<BuildDiffAssetSnapshot>(1);
                        unmatchedTargetByMoveKey[moveKey] = mq;
                    }

                    mq.Enqueue(t);
                }
            }

            for (var i = 0; i < unmatchedBaseline.Count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var b = unmatchedBaseline[i];
                var moveKey = (b.PathId, b.Type, NormalizeKey(b.Name));
                if (unmatchedTargetByMoveKey.TryGetValue(moveKey, out var mq) && mq.Count > 0)
                {
                    var t = mq.Dequeue();
                    counts.Moved++;
                    var extraChanges = DescribeAssetFieldChanges(b, t);
                    var moveDetail = $"Moved: {b.SerializedFileName} -> {t.SerializedFileName}";
                    if (extraChanges.Length > 0)
                    {
                        moveDetail = $"{moveDetail}; {extraChanges}";
                    }

                    entries.Add(BuildAssetEntry(BuildDiffChangeKind.Moved, b, t, moveDetail));
                }
                else
                {
                    counts.Removed++;
                    entries.Add(BuildAssetEntry(BuildDiffChangeKind.Removed, b, target: null, $"Removed ({b.ByteSize:N0} B)"));
                }
            }

            foreach (var mq in unmatchedTargetByMoveKey.Values)
            {
                while (mq.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = mq.Dequeue();
                    counts.Added++;
                    entries.Add(BuildAssetEntry(BuildDiffChangeKind.Added, baseline: null, t, $"Added (+{t.ByteSize:N0} B)"));
                }
            }

            entries.Sort(static (a, b) =>
            {
                var cmp = GetChangeSortRank(a.ChangeKind).CompareTo(GetChangeSortRank(b.ChangeKind));
                if (cmp != 0) return cmp;
                cmp = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return a.PathId.CompareTo(b.PathId);
            });

            return entries;
        }

        private static BuildDiffAssetEntry BuildAssetEntry(
            BuildDiffChangeKind kind,
            BuildDiffAssetSnapshot? baseline,
            BuildDiffAssetSnapshot? target,
            string detail)
        {
            var effective = target ?? baseline!;
            var typeName = AssetItem.GetCachedTypeString(effective.Type);
            return new BuildDiffAssetEntry
            {
                ChangeKind = kind,
                AssetUniqueId = effective.AssetUniqueId,
                BaselineAssetUniqueId = baseline?.AssetUniqueId ?? string.Empty,
                TargetAssetUniqueId = target?.AssetUniqueId ?? string.Empty,
                PathId = effective.PathId,
                Name = string.IsNullOrWhiteSpace(effective.Name) ? $"{typeName} ({effective.PathId})" : effective.Name,
                BaselineName = baseline?.Name ?? string.Empty,
                TargetName = target?.Name ?? string.Empty,
                AssetType = effective.Type,
                TypeName = typeName,
                BaselineType = baseline?.Type ?? ClassIDType.UnknownType,
                TargetType = target?.Type ?? ClassIDType.UnknownType,
                SerializedFileName = effective.SerializedFileName,
                BaselineSerializedFileName = baseline?.SerializedFileName ?? string.Empty,
                TargetSerializedFileName = target?.SerializedFileName ?? string.Empty,
                BaselineOriginalPath = baseline?.OriginalPath ?? string.Empty,
                TargetOriginalPath = target?.OriginalPath ?? string.Empty,
                BaselineContainer = baseline?.Container ?? string.Empty,
                TargetContainer = target?.Container ?? string.Empty,
                BaselineByteSize = baseline?.ByteSize ?? 0,
                TargetByteSize = target?.ByteSize ?? 0,
                Detail = detail
            };
        }

        private static string DescribeAssetFieldChanges(BuildDiffAssetSnapshot baseline, BuildDiffAssetSnapshot target)
        {
            var parts = new List<string>(4);
            if (baseline.ByteSize != target.ByteSize)
            {
                parts.Add($"Size: {baseline.ByteSize:N0} -> {target.ByteSize:N0} ({FormatSignedBytes(target.ByteSize - baseline.ByteSize)})");
            }
            if (!string.Equals(baseline.Name, target.Name, StringComparison.Ordinal))
            {
                parts.Add($"Name: '{baseline.Name}' -> '{target.Name}'");
            }
            if (baseline.Type != target.Type)
            {
                parts.Add($"Type: {baseline.Type} -> {target.Type}");
            }
            if (!string.Equals(baseline.Container, target.Container, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"Container: '{baseline.Container}' -> '{target.Container}'");
            }

            return parts.Count == 0 ? string.Empty : string.Join("; ", parts);
        }

        private static string GetDisplayRelativePath(string rootNorm, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(rootNorm))
            {
                return filePath.Replace('\\', '/');
            }

            try
            {
                return Path.GetRelativePath(rootNorm, Path.GetFullPath(filePath)).Replace('\\', '/');
            }
            catch
            {
                return filePath.Replace('\\', '/');
            }
        }

        private static string NormalizeKey(string? value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        private static int GetChangeSortRank(BuildDiffChangeKind kind) => kind switch
        {
            BuildDiffChangeKind.Added => 0,
            BuildDiffChangeKind.Removed => 1,
            BuildDiffChangeKind.Modified => 2,
            BuildDiffChangeKind.Moved => 3,
            _ => 4
        };

        private static string FormatSignedBytes(long delta)
        {
            return delta >= 0 ? $"+{delta:N0} B" : $"{delta:N0} B";
        }

        private static void EnsureParentDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private static void AppendCsvRow(StringBuilder sb, params string[] fields)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendCsvField(sb, fields[i]);
            }

            sb.AppendLine();
        }

        private static void AppendCsvField(StringBuilder sb, string? value)
        {
            var text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                sb.Append(text);
                return;
            }

            sb.Append('"');
            sb.Append(text.Replace("\"", "\"\"", StringComparison.Ordinal));
            sb.Append('"');
        }

        private static string EscapeMarkdownCell(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("|", "\\|", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        }

        private struct FileDiffCounts
        {
            public int Added;
            public int Removed;
            public int Modified;
            public int Unchanged;
        }

        private struct AssetDiffCounts
        {
            public int Added;
            public int Removed;
            public int Modified;
            public int Moved;
            public int Unchanged;
        }
    }
}
