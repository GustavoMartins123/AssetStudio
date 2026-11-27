#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.CLI;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Tests
{
    public sealed class SyntheticBenchmarkOptions
    {
        public int SerializedFileCount { get; init; } = 8;
        public int TextAssetsPerFile { get; init; } = 25;
        public int PayloadBytesPerTextAsset { get; init; } = 256;
        public int SyntheticMeshCount { get; init; } = 8;
        public int MeshGridSubdivisions { get; init; } = 16;
        public int WarmupIterations { get; init; } = 1;
        public int MeasuredIterations { get; init; } = 5;
        public bool IncludeBatchExport { get; init; } = true;
        public string? WorkingRootDirectory { get; init; }
    }

    public sealed class BenchmarkMetricStats
    {
        public string Name { get; init; } = string.Empty;
        public int Iterations { get; init; }
        public double MinMs { get; init; }
        public double MedianMs { get; init; }
        public double MeanMs { get; init; }
        public double P95Ms { get; init; }
        public double MaxMs { get; init; }
        public double IqrMs { get; init; }
        public double[] SamplesMs { get; init; } = Array.Empty<double>();

        public static BenchmarkMetricStats Compute(string name, IReadOnlyList<double> samplesMs)
        {
            if (samplesMs == null || samplesMs.Count == 0)
            {
                return new BenchmarkMetricStats { Name = name };
            }

            var sorted = samplesMs.OrderBy(static x => x).ToArray();
            var median = Percentile(sorted, 0.50);
            var q1 = Percentile(sorted, 0.25);
            var q3 = Percentile(sorted, 0.75);
            var p95 = Percentile(sorted, 0.95);
            var mean = sorted.Average();

            return new BenchmarkMetricStats
            {
                Name = name,
                Iterations = sorted.Length,
                MinMs = Math.Round(sorted[0], 3),
                MedianMs = Math.Round(median, 3),
                MeanMs = Math.Round(mean, 3),
                P95Ms = Math.Round(p95, 3),
                MaxMs = Math.Round(sorted[^1], 3),
                IqrMs = Math.Round(Math.Max(0.0, q3 - q1), 3),
                SamplesMs = samplesMs.Select(static s => Math.Round(s, 3)).ToArray()
            };
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 1)
            {
                return sorted[0];
            }

            var rank = p * (sorted.Length - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            if (lower == upper)
            {
                return sorted[lower];
            }

            var weight = rank - lower;
            return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
        }
    }

    public sealed class BenchmarkEnvironmentInfo
    {
        public string OperatingSystem { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public int ProcessorCount { get; init; }
        public string DotNetRuntime { get; init; } = string.Empty;
        public string AppVersion { get; init; } = string.Empty;
        public string DisplayVersion { get; init; } = string.Empty;
        public string BuildChannel { get; init; } = string.Empty;
        public string CommitHash { get; init; } = string.Empty;
    }

    public sealed class BenchmarkDatasetSpec
    {
        public int SerializedFileCount { get; init; }
        public int TextAssetsPerFile { get; init; }
        public int TotalTextAssets { get; init; }
        public int PayloadBytesPerTextAsset { get; init; }
        public long DatasetTotalBytes { get; init; }
        public int SyntheticMeshCount { get; init; }
        public int VerticesPerMesh { get; init; }
        public int TrianglesPerMesh { get; init; }
        public int WarmupIterations { get; init; }
        public int MeasuredIterations { get; init; }
    }

    public sealed class BenchmarkResourceUsage
    {
        public long PeakWorkingSetBytes { get; init; }
        public long MaxObservedWorkingSetBytes { get; init; }
        public long ManagedHeapBytesBeforeWarmOpen { get; init; }
        public long ManagedHeapBytesAfterWarmOpen { get; init; }
        public long ManagedHeapDeltaBytes { get; init; }
        public long SqliteDatabaseBytes { get; init; }
        public long MmapSidecarBytes { get; init; }
        public long TotalCacheBytes { get; init; }
        public long ExportedOutputBytes { get; init; }
    }

    public sealed class BenchmarkIntegritySummary
    {
        public bool IntegrityVerified { get; init; }
        public int ExpectedIndexedAssets { get; init; }
        public int ColdIndexedAssets { get; init; }
        public int WarmReopenedAssets { get; init; }
        public int VerifiedPreviewMeshes { get; init; }
        public int ExpectedExportedAssets { get; init; }
        public int VerifiedExportedAssets { get; init; }
        public int ByteExactVerifiedFiles { get; init; }
        public int FailedExports { get; init; }
        public string? FailureReason { get; init; }
    }

    public sealed class SyntheticBenchmarkReport
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public int SchemaVersion { get; init; } = 1;
        public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        public BenchmarkEnvironmentInfo Environment { get; init; } = new();
        public BenchmarkDatasetSpec Dataset { get; init; } = new();
        public BenchmarkMetricStats ColdScanAndIndex { get; init; } = new();
        public BenchmarkMetricStats WarmReopenEndToEnd { get; init; } = new();
        public BenchmarkMetricStats WarmCacheLoadOnly { get; init; } = new();
        public BenchmarkMetricStats FirstPreviewGeometry { get; init; } = new();
        public BenchmarkMetricStats? BatchExportCli { get; init; }
        public double ColdToWarmSpeedup { get; init; }
        public BenchmarkResourceUsage Resources { get; init; } = new();
        public BenchmarkIntegritySummary Integrity { get; init; } = new();

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public void WriteJson(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, ToJson(), Utf8NoBom);
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# AssetStudio Synthetic Benchmark Report");
            sb.AppendLine();
            sb.AppendLine("## Environment");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp (UTC):** {TimestampUtc}");
            sb.AppendLine($"- **Build:** `{Environment.DisplayVersion}` (`{Environment.BuildChannel}`" +
                (string.IsNullOrEmpty(Environment.CommitHash) ? ")" : $", commit `{Environment.CommitHash}`)"));
            sb.AppendLine($"- **Runtime:** {Environment.DotNetRuntime}");
            sb.AppendLine($"- **OS / Arch:** {Environment.OperatingSystem} ({Environment.Architecture})");
            sb.AppendLine($"- **Logical CPUs:** {Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("## Synthetic Dataset");
            sb.AppendLine();
            sb.AppendLine($"- **Serialized files:** {Dataset.SerializedFileCount.ToString(CultureInfo.InvariantCulture)} ({CacheObservabilityService.FormatBytes(Dataset.DatasetTotalBytes)})");
            sb.AppendLine($"- **TextAssets:** {Dataset.TotalTextAssets.ToString(CultureInfo.InvariantCulture)} ({Dataset.TextAssetsPerFile.ToString(CultureInfo.InvariantCulture)}/file, {Dataset.PayloadBytesPerTextAsset.ToString(CultureInfo.InvariantCulture)} B each)");
            sb.AppendLine($"- **Synthetic meshes:** {Dataset.SyntheticMeshCount.ToString(CultureInfo.InvariantCulture)} ({Dataset.VerticesPerMesh.ToString(CultureInfo.InvariantCulture)} vertices, {Dataset.TrianglesPerMesh.ToString(CultureInfo.InvariantCulture)} triangles each)");
            sb.AppendLine($"- **Iterations:** {Dataset.WarmupIterations.ToString(CultureInfo.InvariantCulture)} warm-up + {Dataset.MeasuredIterations.ToString(CultureInfo.InvariantCulture)} measured");
            sb.AppendLine();
            sb.AppendLine("## Latency Summary (ms)");
            sb.AppendLine();
            sb.AppendLine("| Stage | Median (ms) | Min (ms) | P95 (ms) | Max (ms) | IQR (ms) |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
            AppendMetricRow(sb, ColdScanAndIndex);
            AppendMetricRow(sb, WarmReopenEndToEnd);
            AppendMetricRow(sb, WarmCacheLoadOnly);
            AppendMetricRow(sb, FirstPreviewGeometry);
            if (BatchExportCli != null)
            {
                AppendMetricRow(sb, BatchExportCli);
            }

            sb.AppendLine();
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- **Cold vs. Warm Reopen Speedup:** {ColdToWarmSpeedup:0.00}x"));
            sb.AppendLine();
            sb.AppendLine("## Memory & Disk Usage");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("| --- | ---: |");
            sb.AppendLine($"| Peak working set | {CacheObservabilityService.FormatBytes(Resources.PeakWorkingSetBytes)} |");
            sb.AppendLine($"| Managed heap delta (warm open) | {CacheObservabilityService.FormatBytes(Resources.ManagedHeapDeltaBytes)} |");
            sb.AppendLine($"| SQLite index DB bytes | {CacheObservabilityService.FormatBytes(Resources.SqliteDatabaseBytes)} |");
            sb.AppendLine($"| Mmap .idx sidecar bytes | {CacheObservabilityService.FormatBytes(Resources.MmapSidecarBytes)} |");
            sb.AppendLine($"| Total cache bytes | {CacheObservabilityService.FormatBytes(Resources.TotalCacheBytes)} |");
            sb.AppendLine($"| Exported output bytes | {CacheObservabilityService.FormatBytes(Resources.ExportedOutputBytes)} |");
            sb.AppendLine();
            sb.AppendLine("## Output Integrity Verification");
            sb.AppendLine();
            sb.AppendLine($"- **Integrity verified:** {(Integrity.IntegrityVerified ? "PASS" : "FAIL")}");
            sb.AppendLine($"- **Indexed assets (cold / warm / expected):** {Integrity.ColdIndexedAssets} / {Integrity.WarmReopenedAssets} / {Integrity.ExpectedIndexedAssets}");
            sb.AppendLine($"- **Preview meshes verified:** {Integrity.VerifiedPreviewMeshes}");
            sb.AppendLine($"- **Batch export verified (files / byte-exact / expected / failed):** {Integrity.VerifiedExportedAssets} / {Integrity.ByteExactVerifiedFiles} / {Integrity.ExpectedExportedAssets} / {Integrity.FailedExports}");
            if (!string.IsNullOrWhiteSpace(Integrity.FailureReason))
            {
                sb.AppendLine($"- **Failure detail:** {Integrity.FailureReason}");
            }

            return sb.ToString();
        }

        public void WriteMarkdown(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, ToMarkdown(), Utf8NoBom);
        }

        private static void AppendMetricRow(StringBuilder sb, BenchmarkMetricStats metric)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {metric.Name} | {metric.MedianMs:0.###} | {metric.MinMs:0.###} | {metric.P95Ms:0.###} | {metric.MaxMs:0.###} | {metric.IqrMs:0.###} |"));
        }
    }

    /// <summary>
    /// 100% synthetic, deterministic, portable benchmark harness measuring cold project indexing,
    /// warm reopen from persistent SQLite + mmap sidecar cache, first preview geometry extraction,
    /// CLI batch export with byte-exact integrity verification, memory footprint, and cache disk usage.
    /// </summary>
    public static class SyntheticBenchmarkHarness
    {
        public static SyntheticBenchmarkReport Run(SyntheticBenchmarkOptions? options = null)
        {
            var opts = options ?? new SyntheticBenchmarkOptions();
            var serializedFileCount = Math.Max(1, opts.SerializedFileCount);
            var textAssetsPerFile = Math.Max(1, opts.TextAssetsPerFile);
            var payloadBytesPerAsset = Math.Max(16, opts.PayloadBytesPerTextAsset);
            var meshCount = Math.Max(1, opts.SyntheticMeshCount);
            var meshSubdivisions = Math.Clamp(opts.MeshGridSubdivisions, 2, 64);
            var warmupIterations = Math.Max(0, opts.WarmupIterations);
            var measuredIterations = Math.Max(1, opts.MeasuredIterations);

            var baseRoot = string.IsNullOrWhiteSpace(opts.WorkingRootDirectory)
                ? Path.GetTempPath()
                : opts.WorkingRootDirectory;
            var tempRoot = Path.Combine(baseRoot, "AssetStudio_SyntheticBench_" + Guid.NewGuid().ToString("N"));
            var datasetDir = Path.Combine(tempRoot, "SyntheticDataset");
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var previewCacheDir = Path.Combine(tempRoot, "PreviewCache");
            var decompressedCacheDir = Path.Combine(tempRoot, "DecompressedCache");

            Directory.CreateDirectory(datasetDir);
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(previewCacheDir);
            Directory.CreateDirectory(decompressedCacheDir);

            try
            {
                // 1. Build deterministic synthetic Unity serialized dataset on disk (0 proprietary assets)
                var expectedPayloadsByFileName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                long datasetTotalBytes = 0;

                for (var fileIdx = 0; fileIdx < serializedFileCount; fileIdx++)
                {
                    var assets = new (long PathId, string Name, string Script)[textAssetsPerFile];
                    for (var assetIdx = 0; assetIdx < textAssetsPerFile; assetIdx++)
                    {
                        var pathId = (fileIdx * 10_000L) + assetIdx + 100L;
                        var name = $"asset_f{fileIdx:D3}_t{assetIdx:D4}";
                        var script = BuildDeterministicTextPayload(fileIdx, assetIdx, payloadBytesPerAsset);
                        assets[assetIdx] = (pathId, name, script);
                        expectedPayloadsByFileName[name + ".txt"] = Encoding.UTF8.GetBytes(script);
                    }

                    var serializedBytes = SyntheticSerializedFile.BuildTextAssets(assets, out _);
                    var filePath = Path.Combine(datasetDir, $"level{fileIdx}");
                    File.WriteAllBytes(filePath, serializedBytes);
                    datasetTotalBytes += serializedBytes.LongLength;
                }

                var expectedTotalAssets = serializedFileCount * textAssetsPerFile;

                // Build deterministic synthetic Meshes in memory for preview geometry extraction benchmarking
                var syntheticMeshes = new Mesh[meshCount];
                for (var m = 0; m < meshCount; m++)
                {
                    syntheticMeshes[m] = BuildSyntheticGridMesh($"SyntheticMesh_{m:D2}", meshSubdivisions, seedOffset: m * 0.25f);
                }

                var verticesPerMesh = syntheticMeshes[0].m_VertexCount;
                var trianglesPerMesh = syntheticMeshes[0].m_Indices.Count / 3;

                var coldSamples = new List<double>(measuredIterations);
                var warmEndToEndSamples = new List<double>(measuredIterations);
                var warmCacheOnlySamples = new List<double>(measuredIterations);
                var previewSamples = new List<double>(measuredIterations);
                var exportSamples = opts.IncludeBatchExport ? new List<double>(measuredIterations) : null;

                var coldIndexedAssets = 0;
                var warmReopenedAssets = 0;
                var verifiedPreviewMeshes = 0;
                var verifiedExportedAssets = 0;
                var byteExactVerifiedFiles = 0;
                var failedExports = 0;
                string? integrityFailure = null;

                long maxObservedWorkingSet = 0;
                long managedBeforeWarm = 0;
                long managedAfterWarm = 0;
                long managedDeltaBytes = 0;
                long sqliteDbBytes = 0;
                long sidecarBytes = 0;
                long totalCacheBytes = 0;
                long exportedOutputBytes = 0;

                var totalRounds = warmupIterations + measuredIterations;
                var observability = new CacheObservabilityService(cacheDir, previewCacheDir, decompressedCacheDir);

                for (var round = 0; round < totalRounds; round++)
                {
                    var isMeasured = round >= warmupIterations;

                    // --- Stage 1: Cold scan + lazy index + SQLite & mmap .idx persistence ---
                    SqliteConnection.ClearAllPools();
                    observability.SafeClearCache(clearIndexCache: true, clearPreviewCache: true, clearDecompressedCache: true);

                    var swCold = Stopwatch.StartNew();
                    var coldScan = ProjectScanner.ScanFolder(datasetDir);
                    int coldCount;
                    string coldSignature;
                    using (var manager = new AssetsManager { LazyLoading = true, ProjectRoot = datasetDir })
                    using (var cache = new SQLiteProjectIndexCache(cacheDir))
                    {
                        manager.LoadFolder(datasetDir);
                        var handles = manager.ProjectIndex.GetHandles().ToList();
                        coldCount = handles.Count;
                        coldSignature = cache.GetFolderSignature(coldScan);
                        var saved = cache.SaveIndexCache(
                            datasetDir,
                            coldSignature,
                            coldScan,
                            "2022.3.20f1",
                            handles,
                            preserveSemanticRelations: false,
                            markIndexingCompleted: true);
                        if (!saved)
                        {
                            integrityFailure ??= "SQLiteProjectIndexCache.SaveIndexCache returned false during cold indexing.";
                        }
                    }
                    swCold.Stop();

                    coldIndexedAssets = coldCount;
                    if (coldCount != expectedTotalAssets)
                    {
                        integrityFailure ??= $"Cold indexed asset count mismatch: expected {expectedTotalAssets}, got {coldCount}.";
                    }

                    if (isMeasured)
                    {
                        coldSamples.Add(swCold.Elapsed.TotalMilliseconds);
                    }

                    // Capture cache disk usage after cold save
                    if (round == totalRounds - 1)
                    {
                        var diskReport = observability.InspectDiskUsageAndHealth();
                        sqliteDbBytes = diskReport.SqliteIndexUsage.TotalBytes;
                        sidecarBytes = diskReport.MmapSidecarUsage.TotalBytes;
                        totalCacheBytes = diskReport.TotalBytes;
                    }

                    // --- Stage 2: Warm reopen from persistent cache (fresh cache instance & fresh ProjectIndex) ---
                    SqliteConnection.ClearAllPools();
                    if (round == totalRounds - 1)
                    {
                        ForceFullGc();
                        managedBeforeWarm = GC.GetTotalMemory(forceFullCollection: true);
                    }

                    var swWarmEndToEnd = Stopwatch.StartNew();
                    var warmScan = ProjectScanner.ScanFolder(datasetDir);
                    int warmCount;
                    double warmLoadOnlyMs;
                    ProjectIndex? retainedWarmIndex = null;
                    using (var warmCache = new SQLiteProjectIndexCache(cacheDir))
                    {
                        var warmSig = warmCache.GetFolderSignature(warmScan);
                        var targetIndex = new ProjectIndex();

                        var swWarmLoadOnly = Stopwatch.StartNew();
                        var loaded = warmCache.LoadIndexCacheIntoProjectIndex(datasetDir, warmSig, targetIndex, out var loadedSources);
                        swWarmLoadOnly.Stop();
                        warmLoadOnlyMs = swWarmLoadOnly.Elapsed.TotalMilliseconds;

                        warmCount = targetIndex.Count;
                        if (!loaded || loadedSources == null || loadedSources.Count != serializedFileCount)
                        {
                            integrityFailure ??= "Warm cache reopen failed to hydrate expected source files from SQLite/mmap sidecar.";
                        }

                        retainedWarmIndex = targetIndex;
                    }
                    swWarmEndToEnd.Stop();

                    if (round == totalRounds - 1)
                    {
                        ForceFullGc();
                        managedAfterWarm = GC.GetTotalMemory(forceFullCollection: true);
                        managedDeltaBytes = Math.Max(0L, managedAfterWarm - managedBeforeWarm);
                        GC.KeepAlive(retainedWarmIndex);
                    }

                    retainedWarmIndex?.Clear();
                    warmReopenedAssets = warmCount;
                    if (warmCount != expectedTotalAssets)
                    {
                        integrityFailure ??= $"Warm reopened asset count mismatch: expected {expectedTotalAssets}, got {warmCount}.";
                    }

                    if (isMeasured)
                    {
                        warmEndToEndSamples.Add(swWarmEndToEnd.Elapsed.TotalMilliseconds);
                        warmCacheOnlySamples.Add(warmLoadOnlyMs);
                    }

                    // --- Stage 3: First preview geometry extraction + geometry cache round-trip ---
                    var swPreview = Stopwatch.StartNew();
                    var previewOkCount = 0;
                    for (var m = 0; m < syntheticMeshes.Length; m++)
                    {
                        var snapshot = MeshPreviewControl.BuildPreviewGeometrySnapshot(syntheticMeshes[m]);
                        var serializedPreview = MeshPreviewGeometryCacheSerializer.Serialize(new MeshPreviewGeometryCache
                        {
                            ModelMatrix = OpenTK.Mathematics.Matrix4.Identity,
                            Vertices = snapshot.Vertices,
                            Indices = snapshot.Indices,
                            Normals = snapshot.Normals,
                            Colors = snapshot.Colors,
                            Uvs = snapshot.Uvs,
                            SubMeshIndexCounts = snapshot.SubMeshIndexCounts
                        });
                        var deserializedPreview = MeshPreviewGeometryCacheSerializer.Deserialize(serializedPreview);
                        if (deserializedPreview.Vertices.Length == verticesPerMesh
                            && deserializedPreview.Indices.Length == trianglesPerMesh * 3
                            && deserializedPreview.Normals != null
                            && deserializedPreview.Uvs != null)
                        {
                            previewOkCount++;
                        }
                    }
                    swPreview.Stop();

                    verifiedPreviewMeshes = previewOkCount;
                    if (previewOkCount != syntheticMeshes.Length)
                    {
                        integrityFailure ??= $"Preview geometry extraction verified {previewOkCount}/{syntheticMeshes.Length} synthetic meshes.";
                    }

                    if (isMeasured)
                    {
                        previewSamples.Add(swPreview.Elapsed.TotalMilliseconds);
                    }

                    // --- Stage 4: Batch export via AssetStudio.CLI export --report + byte-exact verification ---
                    if (opts.IncludeBatchExport)
                    {
                        var exportOutDir = Path.Combine(tempRoot, $"ExportOut_{round}");
                        var reportPath = Path.Combine(tempRoot, $"ExportReport_{round}.json");

                        var swExport = Stopwatch.StartNew();
                        int exitCode;
                        string stderrText;
                        using (var stdoutWriter = new StringWriter())
                        using (var stderrWriter = new StringWriter())
                        {
                            exitCode = CliRunner.Run(
                                new[]
                                {
                                    "export",
                                    "--no-local-profiles",
                                    "--no-decompression-cache",
                                    datasetDir,
                                    "--output",
                                    exportOutDir,
                                    "--type",
                                    "TextAsset",
                                    "--report",
                                    reportPath
                                },
                                stdoutWriter,
                                stderrWriter);
                            stderrText = stderrWriter.ToString();
                        }
                        swExport.Stop();

                        if (isMeasured)
                        {
                            exportSamples!.Add(swExport.Elapsed.TotalMilliseconds);
                        }

                        VerifyBatchExportOutputs(
                            exitCode,
                            stderrText,
                            exportOutDir,
                            reportPath,
                            expectedPayloadsByFileName,
                            out var exportedCount,
                            out var byteExactCount,
                            out var failedCount,
                            out var totalOutBytes,
                            out var exportError);

                        verifiedExportedAssets = exportedCount;
                        byteExactVerifiedFiles = byteExactCount;
                        failedExports = failedCount;
                        exportedOutputBytes = totalOutBytes;
                        if (!string.IsNullOrEmpty(exportError))
                        {
                            integrityFailure ??= exportError;
                        }

                        TryDeleteDirectory(exportOutDir);
                        TryDeleteFile(reportPath);
                    }

                    using (var proc = Process.GetCurrentProcess())
                    {
                        proc.Refresh();
                        if (proc.WorkingSet64 > maxObservedWorkingSet)
                        {
                            maxObservedWorkingSet = proc.WorkingSet64;
                        }
                    }
                }

                long peakWorkingSetBytes;
                using (var proc = Process.GetCurrentProcess())
                {
                    proc.Refresh();
                    peakWorkingSetBytes = Math.Max(proc.PeakWorkingSet64, maxObservedWorkingSet);
                }

                var identity = ExportBuildIdentity.Resolve(typeof(AppVersionInfo).Assembly);
                var coldStats = BenchmarkMetricStats.Compute("Cold scan + index + cache write", coldSamples);
                var warmE2eStats = BenchmarkMetricStats.Compute("Warm reopen (scan + cache load)", warmEndToEndSamples);
                var warmOnlyStats = BenchmarkMetricStats.Compute("Warm cache load only (SQLite + .idx)", warmCacheOnlySamples);
                var previewStats = BenchmarkMetricStats.Compute("First preview geometry extraction", previewSamples);
                var exportStats = exportSamples != null
                    ? BenchmarkMetricStats.Compute("Batch export (CLI export --report)", exportSamples)
                    : null;

                var speedup = warmE2eStats.MedianMs > 0.0001
                    ? Math.Round(coldStats.MedianMs / warmE2eStats.MedianMs, 2)
                    : 0.0;

                var expectedExported = opts.IncludeBatchExport ? expectedTotalAssets : 0;
                var integrityOk = string.IsNullOrEmpty(integrityFailure)
                    && coldIndexedAssets == expectedTotalAssets
                    && warmReopenedAssets == expectedTotalAssets
                    && verifiedPreviewMeshes == meshCount
                    && (!opts.IncludeBatchExport
                        || (verifiedExportedAssets == expectedTotalAssets
                            && byteExactVerifiedFiles == expectedTotalAssets
                            && failedExports == 0));

                return new SyntheticBenchmarkReport
                {
                    TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Environment = new BenchmarkEnvironmentInfo
                    {
                        OperatingSystem = RuntimeInformation.OSDescription,
                        Architecture = RuntimeInformation.OSArchitecture.ToString(),
                        ProcessorCount = Environment.ProcessorCount,
                        DotNetRuntime = RuntimeInformation.FrameworkDescription,
                        AppVersion = identity.Version,
                        DisplayVersion = identity.DisplayVersion,
                        BuildChannel = identity.BuildChannel,
                        CommitHash = identity.CommitHash
                    },
                    Dataset = new BenchmarkDatasetSpec
                    {
                        SerializedFileCount = serializedFileCount,
                        TextAssetsPerFile = textAssetsPerFile,
                        TotalTextAssets = expectedTotalAssets,
                        PayloadBytesPerTextAsset = payloadBytesPerAsset,
                        DatasetTotalBytes = datasetTotalBytes,
                        SyntheticMeshCount = meshCount,
                        VerticesPerMesh = verticesPerMesh,
                        TrianglesPerMesh = trianglesPerMesh,
                        WarmupIterations = warmupIterations,
                        MeasuredIterations = measuredIterations
                    },
                    ColdScanAndIndex = coldStats,
                    WarmReopenEndToEnd = warmE2eStats,
                    WarmCacheLoadOnly = warmOnlyStats,
                    FirstPreviewGeometry = previewStats,
                    BatchExportCli = exportStats,
                    ColdToWarmSpeedup = speedup,
                    Resources = new BenchmarkResourceUsage
                    {
                        PeakWorkingSetBytes = peakWorkingSetBytes,
                        MaxObservedWorkingSetBytes = maxObservedWorkingSet,
                        ManagedHeapBytesBeforeWarmOpen = managedBeforeWarm,
                        ManagedHeapBytesAfterWarmOpen = managedAfterWarm,
                        ManagedHeapDeltaBytes = managedDeltaBytes,
                        SqliteDatabaseBytes = sqliteDbBytes,
                        MmapSidecarBytes = sidecarBytes,
                        TotalCacheBytes = totalCacheBytes,
                        ExportedOutputBytes = exportedOutputBytes
                    },
                    Integrity = new BenchmarkIntegritySummary
                    {
                        IntegrityVerified = integrityOk,
                        ExpectedIndexedAssets = expectedTotalAssets,
                        ColdIndexedAssets = coldIndexedAssets,
                        WarmReopenedAssets = warmReopenedAssets,
                        VerifiedPreviewMeshes = verifiedPreviewMeshes,
                        ExpectedExportedAssets = expectedExported,
                        VerifiedExportedAssets = verifiedExportedAssets,
                        ByteExactVerifiedFiles = byteExactVerifiedFiles,
                        FailedExports = failedExports,
                        FailureReason = integrityFailure
                    }
                };
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void VerifyBatchExportOutputs(
            int exitCode,
            string stderrText,
            string exportOutDir,
            string reportPath,
            Dictionary<string, byte[]> expectedPayloadsByFileName,
            out int verifiedExportedCount,
            out int byteExactCount,
            out int failedCount,
            out long totalOutputBytes,
            out string? error)
        {
            verifiedExportedCount = 0;
            byteExactCount = 0;
            failedCount = 0;
            totalOutputBytes = 0;
            error = null;

            if (exitCode != CliRunner.ExitSuccess)
            {
                error = $"CLI batch export exited with code {exitCode}: {stderrText}";
                return;
            }

            if (!File.Exists(reportPath))
            {
                error = "CLI batch export did not produce the expected JSON report file.";
                return;
            }

            using (var doc = JsonDocument.Parse(File.ReadAllText(reportPath, Encoding.UTF8)))
            {
                var root = doc.RootElement;
                var status = root.GetProperty("status").GetString();
                var reportExported = root.GetProperty("exportedCount").GetInt32();
                failedCount = root.GetProperty("failedCount").GetInt32();
                if (!string.Equals(status, "Completed", StringComparison.Ordinal))
                {
                    error = $"Export report status was '{status}' instead of 'Completed'.";
                    return;
                }

                if (reportExported != expectedPayloadsByFileName.Count || failedCount != 0)
                {
                    error = $"Export report counts mismatch: exported={reportExported}, expected={expectedPayloadsByFileName.Count}, failed={failedCount}.";
                    return;
                }
            }

            var exportedFiles = Directory.Exists(exportOutDir)
                ? Directory.GetFiles(exportOutDir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            verifiedExportedCount = exportedFiles.Length;

            if (verifiedExportedCount != expectedPayloadsByFileName.Count)
            {
                error = $"Exported file count mismatch: disk has {verifiedExportedCount}, expected {expectedPayloadsByFileName.Count}.";
                return;
            }

            foreach (var file in exportedFiles)
            {
                var fileName = Path.GetFileName(file);
                var actualBytes = File.ReadAllBytes(file);
                totalOutputBytes += actualBytes.LongLength;

                if (!expectedPayloadsByFileName.TryGetValue(fileName, out var expectedBytes))
                {
                    error = $"Unexpected exported file '{fileName}'.";
                    return;
                }

                if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
                {
                    error = $"Byte mismatch in exported file '{fileName}'.";
                    return;
                }

                byteExactCount++;
            }
        }

        private static string BuildDeterministicTextPayload(int fileIndex, int assetIndex, int targetLength)
        {
            var sb = new StringBuilder(targetLength + 32);
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"file={fileIndex:D3};asset={assetIndex:D4};"));
            var seq = 0;
            while (sb.Length < targetLength)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"k{seq:D2}={(fileIndex * 31 + assetIndex * 17 + seq) % 997:D3};"));
                seq++;
            }

            return sb.Length > targetLength ? sb.ToString(0, targetLength) : sb.ToString();
        }

        private static Mesh BuildSyntheticGridMesh(string name, int subdivisions, float seedOffset)
        {
            var vertsPerSide = subdivisions + 1;
            var vertexCount = vertsPerSide * vertsPerSide;
            var vertices = new float[vertexCount * 3];
            var normals = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];

            for (var z = 0; z < vertsPerSide; z++)
            {
                for (var x = 0; x < vertsPerSide; x++)
                {
                    var idx = (z * vertsPerSide) + x;
                    var u = (float)x / subdivisions;
                    var v = (float)z / subdivisions;
                    vertices[idx * 3] = (u - 0.5f) * 4.0f + seedOffset;
                    vertices[idx * 3 + 1] = MathF.Sin((u + v + seedOffset) * MathF.PI) * 0.5f;
                    vertices[idx * 3 + 2] = (v - 0.5f) * 4.0f;

                    normals[idx * 3] = 0f;
                    normals[idx * 3 + 1] = 1f;
                    normals[idx * 3 + 2] = 0f;

                    uvs[idx * 2] = u;
                    uvs[idx * 2 + 1] = v;
                }
            }

            var indices = new List<uint>(subdivisions * subdivisions * 6);
            for (var z = 0; z < subdivisions; z++)
            {
                for (var x = 0; x < subdivisions; x++)
                {
                    var topLeft = (uint)((z * vertsPerSide) + x);
                    var topRight = topLeft + 1;
                    var bottomLeft = (uint)(((z + 1) * vertsPerSide) + x);
                    var bottomRight = bottomLeft + 1;

                    indices.Add(topLeft);
                    indices.Add(bottomLeft);
                    indices.Add(topRight);

                    indices.Add(topRight);
                    indices.Add(bottomLeft);
                    indices.Add(bottomRight);
                }
            }

            var subMesh = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            subMesh.firstByte = 0;
            subMesh.indexCount = (uint)indices.Count;
            subMesh.topology = GfxPrimitiveType.Triangles;

            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            typeof(Mesh)
                .GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(mesh, true);
            mesh.m_Name = name;
            mesh.m_VertexCount = vertexCount;
            mesh.m_Vertices = vertices;
            mesh.m_Normals = normals;
            mesh.m_UV0 = uvs;
            mesh.m_Indices = indices;
            mesh.m_SubMeshes = new[] { subMesh };
            return mesh;
        }

        private static void ForceFullGc()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
