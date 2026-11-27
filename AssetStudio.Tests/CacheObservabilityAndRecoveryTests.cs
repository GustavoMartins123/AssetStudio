#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public sealed class CacheObservabilityAndRecoveryTests
    {
        private readonly ITestOutputHelper _output;

        public CacheObservabilityAndRecoveryTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CorruptSqliteDatabaseAndTruncatedSidecar_AreDetectedRecoveredAndRebuiltCleanly()
        {
            var tempRoot = CreateTempDirectory("corrupt_db_sidecar_recovery");
            try
            {
                var projectDir = Path.Combine(tempRoot, "SyntheticProject");
                var indexCacheDir = Path.Combine(tempRoot, "IndexCache");
                var previewCacheDir = Path.Combine(tempRoot, "PreviewCache");
                var decompressedCacheDir = Path.Combine(tempRoot, "DecompressedCache");
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(indexCacheDir);
                Directory.CreateDirectory(previewCacheDir);
                Directory.CreateDirectory(decompressedCacheDir);

                var sourcePath = Path.Combine(projectDir, "level0");
                var originalSourceBytes = SyntheticSerializedFile.BuildTextAssets(
                    new (long, string, string)[] { (101, "alpha", "payload-alpha"), (102, "beta", "payload-beta") },
                    out _);
                File.WriteAllBytes(sourcePath, originalSourceBytes);

                var scan = ProjectScanner.ScanFolder(projectDir);
                var handles = new List<AssetHandle>
                {
                    CreateHandle("level0", sourcePath, 101, "alpha", ClassIDType.TextAsset),
                    CreateHandle("level0", sourcePath, 102, "beta", ClassIDType.TextAsset)
                };

                var observability = new CacheObservabilityService(indexCacheDir, previewCacheDir, decompressedCacheDir);
                string signature;
                string dbPath;
                string idxPath;

                using (var cache = new SQLiteProjectIndexCache(indexCacheDir))
                {
                    signature = cache.GetFolderSignature(scan);
                    dbPath = cache.GetDbPath(projectDir);
                    idxPath = cache.GetIdxPath(projectDir);

                    Assert.True(cache.SaveIndexCache(projectDir, signature, scan, "2022.3.20f1", handles, markIndexingCompleted: true));
                    Assert.True(File.Exists(dbPath));
                    Assert.True(File.Exists(idxPath));

                    // 1) Truncate .idx sidecar -> health check flags sidecar corruption; loading falls back to SQLite and removes corrupt sidecar.
                    var validIdx = File.ReadAllBytes(idxPath);
                    File.WriteAllBytes(idxPath, validIdx.AsSpan(0, Math.Min(32, validIdx.Length / 2)).ToArray());

                    var sidecarReport = observability.InspectDiskUsageAndHealth(cache);
                    Assert.True(sidecarReport.HasCorruption);
                    Assert.Single(sidecarReport.Databases);
                    Assert.True(sidecarReport.Databases[0].IsSidecarCorrupt);
                    Assert.False(sidecarReport.Databases[0].IsCorrupt);

                    var fallbackIndex = new ProjectIndex();
                    Assert.True(cache.LoadIndexCacheIntoProjectIndex(projectDir, signature, fallbackIndex, out var loadedSources));
                    Assert.Equal(2, fallbackIndex.Count);
                    Assert.NotNull(loadedSources);
                    Assert.False(File.Exists(idxPath));
                    Assert.Contains(cache.GetRecentDiagnostics(), d => d.FailureKind == CacheFailureKind.Corruption && d.Category == "MmapSidecar" && d.RecoveredAutomatically);
                }

                // 2) Overwrite SQLite database bytes with invalid non-SQLite garbage -> fresh cache instance detects & recovers cleanly without crashing!
                SqliteConnection.ClearAllPools();
                File.WriteAllBytes(dbPath, Encoding.ASCII.GetBytes("NOT_A_VALID_SQLITE_DATABASE_IMAGE_CORRUPTED_BYTES"));

                var corruptReport = observability.InspectDiskUsageAndHealth();
                Assert.True(corruptReport.HasCorruption);
                Assert.Equal(CacheHealthStatus.Corrupt, corruptReport.OverallHealth);
                Assert.Single(corruptReport.Databases);
                Assert.True(corruptReport.Databases[0].IsCorrupt);

                using (var recoveredCache = new SQLiteProjectIndexCache(indexCacheDir))
                {
                    // Reading from corrupt DB must recover cleanly (cache miss, never throw) and create a valid fresh DB.
                    var miss = recoveredCache.LoadIndexCache(projectDir, signature);
                    Assert.Null(miss);
                    Assert.Contains(recoveredCache.GetRecentDiagnostics(), d => d.FailureKind == CacheFailureKind.Corruption && d.Category == "SqliteIndex" && d.RecoveredAutomatically);

                    // Re-saving rebuilds both SQLite DB and .idx sidecar cleanly.
                    Assert.True(recoveredCache.SaveIndexCache(projectDir, signature, scan, "2022.3.20f1", handles, markIndexingCompleted: true));
                    Assert.True(File.Exists(idxPath));

                    var rebuilt = recoveredCache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(rebuilt);
                    Assert.Equal(2, rebuilt!.Count);

                    var healthyReport = observability.InspectDiskUsageAndHealth(recoveredCache);
                    Assert.Equal(CacheHealthStatus.Healthy, healthyReport.OverallHealth);
                    Assert.False(healthyReport.HasCorruption);
                }

                // 3) Plant a corrupt decompressed bundle entry (missing .ready) + orphan .tmp + corrupt SQLite DB and recover via InspectAndRecoverCache.
                SqliteConnection.ClearAllPools();
                File.WriteAllBytes(dbPath, new byte[4096]); // Invalid zeroed SQLite page header
                var brokenBundleDir = Path.Combine(decompressedCacheDir, "scope01", "broken_bundle");
                Directory.CreateDirectory(brokenBundleDir);
                File.WriteAllBytes(Path.Combine(brokenBundleDir, "blocks.bin"), new byte[128]);
                File.WriteAllText(Path.Combine(brokenBundleDir, "manifest.json"), "{ invalid json");
                var orphanTmp = Path.Combine(indexCacheDir, "stale_export.tmp");
                File.WriteAllBytes(orphanTmp, new byte[64]);

                var recovery = observability.InspectAndRecoverCache();
                Assert.True(recovery.Success);
                Assert.Equal(1, recovery.RecoveredCorruptDatabases);
                Assert.Equal(1, recovery.RemovedCorruptDecompressedEntries);
                Assert.True(recovery.RemovedOrphanTempFiles >= 1);
                Assert.False(File.Exists(dbPath));
                Assert.False(Directory.Exists(brokenBundleDir));
                Assert.False(File.Exists(orphanTmp));

                // Source project file must NEVER be touched or modified!
                Assert.True(File.Exists(sourcePath));
                Assert.Equal(originalSourceBytes, File.ReadAllBytes(sourcePath));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void BundleReplacementModificationAndRemoval_InvalidateIndexAndPreviewCache()
        {
            var tempRoot = CreateTempDirectory("bundle_invalidation_previews");
            try
            {
                var projectDir = Path.Combine(tempRoot, "MultiBundleProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(cacheDir);

                var baseUtc = DateTime.UtcNow.AddMinutes(-20);
                var bundle1 = Path.Combine(projectDir, "pack_alpha.bundle");
                var bundle2 = Path.Combine(projectDir, "pack_beta.bundle");
                var bundle3 = Path.Combine(projectDir, "pack_gamma.bundle");

                File.WriteAllBytes(bundle1, CreateFilledBytes(4096, 0x11));
                File.WriteAllBytes(bundle2, CreateFilledBytes(4096, 0x22));
                File.WriteAllBytes(bundle3, CreateFilledBytes(4096, 0x33));
                File.SetLastWriteTimeUtc(bundle1, baseUtc);
                File.SetLastWriteTimeUtc(bundle2, baseUtc);
                File.SetLastWriteTimeUtc(bundle3, baseUtc);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);

                var h1 = CreateHandle("CAB-ALPHA", bundle1, 101, "AlphaMesh", ClassIDType.Mesh);
                var h2 = CreateHandle("CAB-BETA", bundle2, 202, "BetaMesh", ClassIDType.Mesh);
                var h3 = CreateHandle("CAB-GAMMA", bundle3, 303, "GammaMesh", ClassIDType.Mesh);

                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.20f1", new[] { h1, h2, h3 }, markIndexingCompleted: true));

                var preview1 = Encoding.UTF8.GetBytes("preview-alpha-v1");
                var preview2 = Encoding.UTF8.GetBytes("preview-beta-v1");
                var preview3 = Encoding.UTF8.GetBytes("preview-gamma-v1");
                cache.SavePreviewCachePayload(projectDir, sig1, h1.UniqueID, "mesh-geometry", 1, "density=100", preview1);
                cache.SavePreviewCachePayload(projectDir, sig1, h2.UniqueID, "mesh-geometry", 1, "density=100", preview2);
                cache.SavePreviewCachePayload(projectDir, sig1, h3.UniqueID, "mesh-geometry", 1, "density=100", preview3);

                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, sig1, h1.UniqueID, "mesh-geometry", 1, "density=100", out var loadedP1));
                Assert.NotNull(loadedP1);
                Assert.Equal(preview1, loadedP1!);

                // Case A: Same-size replacement of bundle1 with IDENTICAL timestamp (content hash changes)
                var replacedBytes = CreateFilledBytes(4096, 0x11);
                replacedBytes[16] ^= 0xFF;
                File.WriteAllBytes(bundle1, replacedBytes);
                File.SetLastWriteTimeUtc(bundle1, baseUtc);

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.NotEqual(sig1, sig2);

                // Before re-indexing, neither index nor preview cache may be served under sig2
                Assert.Null(cache.LoadIndexCache(projectDir, sig2));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig2, h1.UniqueID, "mesh-geometry", 1, "density=100"));
                Assert.False(cache.TryLoadPreviewCacheBytes(projectDir, sig2, h1.UniqueID, "mesh-geometry", 1, "density=100", out _));

                // Even if indexing progress flips signature to sig2 in "running" state before SaveIndexCache,
                // stale previews must be invalidated and never served while running.
                cache.SaveIndexingProgress(projectDir, sig2, scan2, new IndexingProgressUpdate
                {
                    Status = "running",
                    TotalFiles = 3,
                    ProcessedFiles = 1,
                    PendingFiles = 2,
                    PercentComplete = 33.3,
                    CurrentFile = bundle1
                });
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig2, h1.UniqueID, "mesh-geometry", 1, "density=100"));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig2, h2.UniqueID, "mesh-geometry", 1, "density=100"));

                // Finish re-index under sig2 -> bundle1 preview is invalidated, untouched bundle2 & bundle3 previews survive!
                var h1V2 = CreateHandle("CAB-ALPHA", bundle1, 101, "AlphaMesh_V2", ClassIDType.Mesh);
                Assert.True(cache.SaveIndexCache(projectDir, sig2, scan2, "2022.3.20f1", new[] { h1V2, h2, h3 }, markIndexingCompleted: true));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig2, h1.UniqueID, "mesh-geometry", 1, "density=100"));
                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, sig2, h2.UniqueID, "mesh-geometry", 1, "density=100", out var survivedP2));
                Assert.NotNull(survivedP2);
                Assert.Equal(preview2, survivedP2!);
                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, sig2, h3.UniqueID, "mesh-geometry", 1, "density=100", out var survivedP3));
                Assert.NotNull(survivedP3);
                Assert.Equal(preview3, survivedP3!);

                // Old signature sig1 must no longer resolve any cached index or preview
                Assert.Null(cache.LoadIndexCache(projectDir, sig1));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig1, h2.UniqueID, "mesh-geometry", 1, "density=100"));

                // Case B: Different-size modification of bundle2 + updated mtime
                File.WriteAllBytes(bundle2, CreateFilledBytes(6144, 0x2A));
                File.SetLastWriteTimeUtc(bundle2, baseUtc.AddMinutes(5));
                var scan3 = ProjectScanner.ScanFolder(projectDir);
                var sig3 = cache.GetFolderSignature(scan3);
                Assert.NotEqual(sig2, sig3);
                Assert.Null(cache.LoadIndexCache(projectDir, sig3));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig3, h2.UniqueID, "mesh-geometry", 1, "density=100"));

                var h2V2 = CreateHandle("CAB-BETA", bundle2, 202, "BetaMesh_V2", ClassIDType.Mesh);
                Assert.True(cache.SaveIndexCache(projectDir, sig3, scan3, "2022.3.20f1", new[] { h1V2, h2V2, h3 }, markIndexingCompleted: true));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig3, h2.UniqueID, "mesh-geometry", 1, "density=100"));
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, sig3, h3.UniqueID, "mesh-geometry", 1, "density=100"));

                // Case C: Removal of bundle3 -> invalidates index & preview for bundle3 cleanly
                File.Delete(bundle3);
                var scan4 = ProjectScanner.ScanFolder(projectDir);
                var sig4 = cache.GetFolderSignature(scan4);
                Assert.NotEqual(sig3, sig4);
                Assert.Null(cache.LoadIndexCache(projectDir, sig4));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig4, h3.UniqueID, "mesh-geometry", 1, "density=100"));

                Assert.True(cache.SaveIndexCache(projectDir, sig4, scan4, "2022.3.20f1", new[] { h1V2, h2V2 }, markIndexingCompleted: true));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig4, h3.UniqueID, "mesh-geometry", 1, "density=100"));
                var finalHandles = cache.LoadIndexCache(projectDir, sig4);
                Assert.NotNull(finalHandles);
                Assert.Equal(2, finalHandles!.Count);
                Assert.DoesNotContain(finalHandles, h => h.SerializedFileName == "CAB-GAMMA");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void InterruptedIndexingState_IsDetectedAsInconsistentAndRecoversOnReindex()
        {
            var tempRoot = CreateTempDirectory("interrupted_indexing_recovery");
            try
            {
                var projectDir = Path.Combine(tempRoot, "InterruptedProject");
                var indexCacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(indexCacheDir);

                var bundlePath = Path.Combine(projectDir, "scene_part.bundle");
                File.WriteAllBytes(bundlePath, CreateFilledBytes(2048, 0x55));

                var scan = ProjectScanner.ScanFolder(projectDir);
                var handle = CreateHandle("CAB-SCENE", bundlePath, 501, "SceneProp", ClassIDType.Mesh);
                var observability = new CacheObservabilityService(indexCacheDir, Path.Combine(tempRoot, "Preview"), Path.Combine(tempRoot, "Decomp"));

                using var cache = new SQLiteProjectIndexCache(indexCacheDir);
                var signature = cache.GetFolderSignature(scan);

                // Seed initial completed index + preview entry
                Assert.True(cache.SaveIndexCache(projectDir, signature, scan, "2022.3.20f1", new[] { handle }, markIndexingCompleted: true));
                cache.SavePreviewCachePayload(projectDir, signature, handle.UniqueID, "mesh-geometry", 1, "density=100", new byte[] { 1, 2, 3, 4 });
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, signature, handle.UniqueID, "mesh-geometry", 1, "density=100"));

                // Simulate crash/interruption mid-indexing: ProjectIndexingState stuck in "running" + orphaned staging table
                cache.SaveIndexingProgress(projectDir, signature, scan, new IndexingProgressUpdate
                {
                    Status = "running",
                    TotalFiles = 10,
                    ProcessedFiles = 3,
                    PendingFiles = 7,
                    PercentComplete = 30.0,
                    CurrentFile = bundlePath,
                    LastReadFile = bundlePath,
                    NewlyReadFiles = new[] { bundlePath }
                });

                using (var conn = cache.CreateConnection(projectDir))
                {
                    SQLiteProjectIndexCache.CreateSemanticStagingTables(conn);
                }

                // Partial index and stale preview must NEVER be served while indexing is interrupted
                Assert.Null(cache.LoadIndexCache(projectDir, signature));
                var partialTarget = new ProjectIndex();
                Assert.False(cache.LoadIndexCacheIntoProjectIndex(projectDir, signature, partialTarget, out _));
                Assert.Equal(0, partialTarget.Count);
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, signature, handle.UniqueID, "mesh-geometry", 1, "density=100"));
                Assert.False(cache.TryLoadLatestPreviewCacheBytesByAssetId(projectDir, signature, handle.UniqueID, "mesh-geometry", 1, "density=", out _, out _));

                // Detection APIs report the interrupted state cleanly
                Assert.True(cache.HasInterruptedIndexingState(projectDir, signature, out var interruptedState, out var sigMismatch));
                Assert.NotNull(interruptedState);
                Assert.Equal("running", interruptedState!.Status);
                Assert.False(sigMismatch);

                var diskHealth = observability.InspectDiskUsageAndHealth(cache);
                Assert.True(diskHealth.HasInterruptedIndexing);
                Assert.Equal(CacheHealthStatus.Degraded, diskHealth.OverallHealth);

                // Explicit interrupted-indexing recovery cleans partial rows, orphaned staging tables, and stale sidecar
                Assert.True(cache.TryRecoverInterruptedIndexing(projectDir, signature));
                Assert.False(File.Exists(cache.GetIdxPath(projectDir)));
                Assert.Contains(cache.GetRecentDiagnostics(), d => d.FailureKind == CacheFailureKind.InterruptedIndexing && d.RecoveredAutomatically);

                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    using var stageCmd = conn.CreateCommand();
                    stageCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'Stage_%';";
                    Assert.Equal(0L, Convert.ToInt64(stageCmd.ExecuteScalar()));
                }

                // Re-indexing completes cleanly and restores full healthy warm-load + preview functionality
                Assert.True(cache.SaveIndexCache(projectDir, signature, scan, "2022.3.20f1", new[] { handle }, markIndexingCompleted: true));
                Assert.False(cache.HasInterruptedIndexingState(projectDir, signature, out _, out _));
                var recoveredHandles = cache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(recoveredHandles);
                Assert.Single(recoveredHandles!);

                var recoveredHealth = observability.InspectDiskUsageAndHealth(cache);
                Assert.Equal(CacheHealthStatus.Healthy, recoveredHealth.OverallHealth);
                Assert.False(recoveredHealth.HasInterruptedIndexing);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void CacheObservabilityService_ReportsDiskBreakdownDiagnosesErrorsAndConfinesSafeClearToCacheRoot()
        {
            var tempRoot = CreateTempDirectory("cache_observability_confinement");
            try
            {
                var sourceProjectDir = Path.Combine(tempRoot, "SourceProject");
                var indexCacheDir = Path.Combine(tempRoot, "IndexCache");
                var siblingDir = Path.Combine(tempRoot, "IndexCacheSibling");
                var previewCacheDir = Path.Combine(tempRoot, "PreviewCache");
                var decompressedCacheDir = Path.Combine(tempRoot, "DecompressedCache");

                Directory.CreateDirectory(sourceProjectDir);
                Directory.CreateDirectory(indexCacheDir);
                Directory.CreateDirectory(siblingDir);
                Directory.CreateDirectory(previewCacheDir);
                Directory.CreateDirectory(decompressedCacheDir);

                var sourceBundlePath = Path.Combine(sourceProjectDir, "critical_source.bundle");
                var sourceBytes = CreateFilledBytes(4096, 0x7A);
                File.WriteAllBytes(sourceBundlePath, sourceBytes);

                var siblingGuardFile = Path.Combine(siblingDir, "must_not_delete.txt");
                File.WriteAllText(siblingGuardFile, "protected-sibling-content");

                // 1) Populate SQLite index + .idx sidecar + inline preview + external preview file + orphan .tmp + decompressed bundle cache
                var scan = ProjectScanner.ScanFolder(sourceProjectDir);
                using var cache = new SQLiteProjectIndexCache(indexCacheDir);
                var sig = cache.GetFolderSignature(scan);
                var handle = CreateHandle("CAB-SRC", sourceBundlePath, 777, "CriticalMesh", ClassIDType.Mesh);
                Assert.True(cache.SaveIndexCache(sourceProjectDir, sig, scan, "2022.3.20f1", new[] { handle }, markIndexingCompleted: true));

                cache.SavePreviewCachePayload(sourceProjectDir, sig, handle.UniqueID, "mesh-geometry", 1, "density=100", CreateFilledBytes(512, 0x42));
                var previewSubDir = Path.Combine(previewCacheDir, SQLiteProjectIndexCache.GetFolderCacheKey(sourceProjectDir));
                Directory.CreateDirectory(previewSubDir);
                File.WriteAllBytes(Path.Combine(previewSubDir, "thumb_01.png"), CreateFilledBytes(1024, 0x31));

                var orphanTmpPath = Path.Combine(indexCacheDir, "leftover.tmp");
                File.WriteAllBytes(orphanTmpPath, CreateFilledBytes(128, 0x09));

                var decompCache = new DecompressedBundleCache(decompressedCacheDir, maxTotalBytes: 16L * 1024 * 1024, gameScopePath: sourceProjectDir);
                var decompEntry = decompCache.GetOrExtractBlocks(
                    sourceBundlePath,
                    expectedUncompressedSize: 2048,
                    writeDecompressedBlocks: s => s.Write(CreateFilledBytes(2048, 0x61), 0, 2048),
                    directoryNodes: new[] { new CachedBundleNode { Path = "CAB-SRC/data", Offset = 0, Size = 2048, Flags = 4 } },
                    gameScopePath: sourceProjectDir);
                Assert.True(File.Exists(decompEntry.BlocksFilePath));

                var observability = new CacheObservabilityService(indexCacheDir, previewCacheDir, decompressedCacheDir);

                // 2) Verify disk breakdown accuracy
                var report = observability.InspectDiskUsageAndHealth(cache);
                Assert.True(report.SqliteIndexUsage.FileCount >= 1);
                Assert.True(report.SqliteIndexUsage.TotalBytes > 0);
                Assert.Equal(1, report.MmapSidecarUsage.FileCount);
                Assert.True(report.MmapSidecarUsage.TotalBytes > 0);
                Assert.Equal(1, report.PreviewPayloadUsage.FileCount);
                Assert.Equal(1024L, report.PreviewPayloadUsage.TotalBytes);
                Assert.Equal(1, report.PreviewDatabaseEntryCount);
                Assert.True(report.DecompressedBundleUsage.FileCount >= 3); // blocks.bin + manifest.json + .ready
                Assert.True(report.DecompressedBundleUsage.TotalBytes >= 2048L);
                Assert.Equal(1, report.OrphanTempUsage.FileCount);
                Assert.Equal(128L, report.OrphanTempUsage.TotalBytes);

                var expectedTotal = report.SqliteIndexUsage.TotalBytes
                    + report.MmapSidecarUsage.TotalBytes
                    + report.PreviewPayloadUsage.TotalBytes
                    + report.DecompressedBundleUsage.TotalBytes
                    + report.OrphanTempUsage.TotalBytes;
                Assert.Equal(expectedTotal, report.TotalBytes);
                Assert.Contains("SQLite:", report.HumanReadableSummary, StringComparison.Ordinal);
                Assert.Contains("Decompressed bundles:", report.HumanReadableSummary, StringComparison.Ordinal);

                // 3) Verify structured exception diagnostics distinguish DiskFull vs ReadOrAccessFailure vs Corruption
                var diskFullWin1 = CacheObservabilityService.ClassifyException(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));
                Assert.Equal(CacheFailureKind.DiskFull, diskFullWin1.Kind);
                Assert.False(diskFullWin1.IsRecoverableByCacheRebuild);

                var diskFullWin2 = CacheObservabilityService.ClassifyException(new IOException("Handle disk full.", unchecked((int)0x80070027)));
                Assert.Equal(CacheFailureKind.DiskFull, diskFullWin2.Kind);

                var diskFullPosix = CacheObservabilityService.ClassifyException(new IOException("No space left on device", 28));
                Assert.Equal(CacheFailureKind.DiskFull, diskFullPosix.Kind);

                var diskFullSqlite = CacheObservabilityService.ClassifyException(new SqliteException("database or disk is full", 13));
                Assert.Equal(CacheFailureKind.DiskFull, diskFullSqlite.Kind);

                var readAccessUnauthorized = CacheObservabilityService.ClassifyException(new UnauthorizedAccessException("Access to cache denied."));
                Assert.Equal(CacheFailureKind.ReadOrAccessFailure, readAccessUnauthorized.Kind);
                Assert.False(readAccessUnauthorized.IsRecoverableByCacheRebuild);

                var readAccessSqlite = CacheObservabilityService.ClassifyException(new SqliteException("attempt to write a readonly database", 8));
                Assert.Equal(CacheFailureKind.ReadOrAccessFailure, readAccessSqlite.Kind);

                var corruptSqlite = CacheObservabilityService.ClassifyException(new SqliteException("database disk image is malformed", 11));
                Assert.Equal(CacheFailureKind.Corruption, corruptSqlite.Kind);
                Assert.True(corruptSqlite.IsRecoverableByCacheRebuild);

                var corruptNotDb = CacheObservabilityService.ClassifyException(new SqliteException("file is not a database", 26));
                Assert.Equal(CacheFailureKind.Corruption, corruptNotDb.Kind);

                var corruptPayload = CacheObservabilityService.ClassifyException(new InvalidDataException("Truncated sidecar payload."));
                Assert.Equal(CacheFailureKind.Corruption, corruptPayload.Kind);

                // 4) Strict path confinement: passing source paths, traversal paths, or sibling-prefix paths MUST be rejected untouched!
                var traversalToSource = Path.Combine(indexCacheDir, "..", "SourceProject", "critical_source.bundle");
                var unsafeClear = observability.SafeClearPaths(
                    indexCacheDir,
                    new[] { sourceBundlePath, traversalToSource, siblingGuardFile, indexCacheDir },
                    cache);

                Assert.False(unsafeClear.Success);
                Assert.Equal(0, unsafeClear.DeletedFileCount);
                Assert.Equal(4, unsafeClear.RejectedUnsafePathCount);
                Assert.All(unsafeClear.Diagnostics, d => Assert.Equal(CacheFailureKind.BoundaryViolation, d.FailureKind));
                Assert.True(File.Exists(sourceBundlePath));
                Assert.Equal(sourceBytes, File.ReadAllBytes(sourceBundlePath));
                Assert.True(File.Exists(siblingGuardFile));

                // 5) SafeClearCache clears all cache files inside cache roots while leaving SourceProject & sibling untouched!
                var cleared = observability.SafeClearCache(cache, clearIndexCache: true, clearPreviewCache: true, clearDecompressedCache: true);
                Assert.True(cleared.Success);
                Assert.Equal(0, cleared.RejectedUnsafePathCount);
                Assert.True(cleared.DeletedFileCount >= 5);
                Assert.True(cleared.ReclaimedBytes > 0);

                var postClearReport = observability.InspectDiskUsageAndHealth(cache);
                Assert.Equal(CacheHealthStatus.Empty, postClearReport.OverallHealth);
                Assert.Equal(0, postClearReport.TotalFileCount);
                Assert.Equal(0L, postClearReport.TotalBytes);

                // Source files & sibling folder remain 100% intact!
                Assert.True(File.Exists(sourceBundlePath));
                Assert.Equal(sourceBytes, File.ReadAllBytes(sourceBundlePath));
                Assert.Equal("protected-sibling-content", File.ReadAllText(siblingGuardFile));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SyntheticBenchmarkHarness_RunsEndToEndAndVerifiesIntegrity()
        {
            var options = new SyntheticBenchmarkOptions
            {
                SerializedFileCount = ReadEnvInt("ASSETSTUDIO_BENCHMARK_FILES", defaultValue: 4, min: 1, max: 128),
                TextAssetsPerFile = ReadEnvInt("ASSETSTUDIO_BENCHMARK_ASSETS_PER_FILE", defaultValue: 10, min: 1, max: 500),
                PayloadBytesPerTextAsset = ReadEnvInt("ASSETSTUDIO_BENCHMARK_PAYLOAD_BYTES", defaultValue: 256, min: 32, max: 65536),
                SyntheticMeshCount = ReadEnvInt("ASSETSTUDIO_BENCHMARK_MESHES", defaultValue: 4, min: 1, max: 128),
                MeshGridSubdivisions = ReadEnvInt("ASSETSTUDIO_BENCHMARK_MESH_SUBDIV", defaultValue: 12, min: 2, max: 64),
                WarmupIterations = ReadEnvInt("ASSETSTUDIO_BENCHMARK_WARMUP", defaultValue: 1, min: 0, max: 10),
                MeasuredIterations = ReadEnvInt("ASSETSTUDIO_BENCHMARK_ITERATIONS", defaultValue: 3, min: 1, max: 50),
                IncludeBatchExport = true
            };

            var report = SyntheticBenchmarkHarness.Run(options);

            // Verify environment & build identity metadata
            Assert.False(string.IsNullOrWhiteSpace(report.Environment.OperatingSystem));
            Assert.True(report.Environment.ProcessorCount >= 1);
            Assert.False(string.IsNullOrWhiteSpace(report.Environment.DotNetRuntime));
            Assert.False(string.IsNullOrWhiteSpace(report.Environment.AppVersion));
            Assert.False(string.IsNullOrWhiteSpace(report.Environment.DisplayVersion));

            // Verify dataset & measured iterations + dispersion statistics invariants
            var expectedAssets = options.SerializedFileCount * options.TextAssetsPerFile;
            Assert.Equal(expectedAssets, report.Dataset.TotalTextAssets);
            AssertMetricInvariants(report.ColdScanAndIndex, options.MeasuredIterations);
            AssertMetricInvariants(report.WarmReopenEndToEnd, options.MeasuredIterations);
            AssertMetricInvariants(report.WarmCacheLoadOnly, options.MeasuredIterations);
            AssertMetricInvariants(report.FirstPreviewGeometry, options.MeasuredIterations);
            Assert.NotNull(report.BatchExportCli);
            AssertMetricInvariants(report.BatchExportCli!, options.MeasuredIterations);

            // Verify memory & disk counters
            Assert.True(report.Resources.PeakWorkingSetBytes > 0);
            Assert.True(report.Resources.ManagedHeapDeltaBytes >= 0);
            Assert.True(report.Resources.SqliteDatabaseBytes > 0);
            Assert.True(report.Resources.MmapSidecarBytes > 0);
            Assert.True(report.Resources.TotalCacheBytes >= report.Resources.SqliteDatabaseBytes + report.Resources.MmapSidecarBytes);
            Assert.True(report.Resources.ExportedOutputBytes > 0);

            // Verify 100% output integrity (exact counts, byte-exact files, zero failures)
            Assert.True(report.Integrity.IntegrityVerified, report.Integrity.FailureReason ?? "Benchmark integrity failed.");
            Assert.Equal(expectedAssets, report.Integrity.ColdIndexedAssets);
            Assert.Equal(expectedAssets, report.Integrity.WarmReopenedAssets);
            Assert.Equal(options.SyntheticMeshCount, report.Integrity.VerifiedPreviewMeshes);
            Assert.Equal(expectedAssets, report.Integrity.ExpectedExportedAssets);
            Assert.Equal(expectedAssets, report.Integrity.VerifiedExportedAssets);
            Assert.Equal(expectedAssets, report.Integrity.ByteExactVerifiedFiles);
            Assert.Equal(0, report.Integrity.FailedExports);

            // Verify JSON serialization & Markdown summary formatting
            var json = report.ToJson();
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.True(root.GetProperty("integrity").GetProperty("integrityVerified").GetBoolean());
                Assert.Equal(expectedAssets, root.GetProperty("integrity").GetProperty("byteExactVerifiedFiles").GetInt32());
                Assert.Equal(0, root.GetProperty("integrity").GetProperty("failedExports").GetInt32());
            }

            var markdown = report.ToMarkdown();
            Assert.Contains("# AssetStudio Synthetic Benchmark Report", markdown, StringComparison.Ordinal);
            Assert.Contains("Cold scan + index + cache write", markdown, StringComparison.Ordinal);
            Assert.Contains("Warm reopen (scan + cache load)", markdown, StringComparison.Ordinal);
            Assert.Contains("First preview geometry extraction", markdown, StringComparison.Ordinal);
            Assert.Contains("Batch export (CLI export --report)", markdown, StringComparison.Ordinal);
            Assert.Contains("**Integrity verified:** PASS", markdown, StringComparison.Ordinal);

            _output.WriteLine(markdown);

            // When invoked by scripts/run-benchmarks.ps1, persist JSON + Markdown reports to the requested directory.
            var outputDir = Environment.GetEnvironmentVariable("ASSETSTUDIO_BENCHMARK_OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                report.WriteJson(Path.Combine(outputDir, "benchmark-report.json"));
                report.WriteMarkdown(Path.Combine(outputDir, "benchmark-summary.md"));
            }
        }

        [Fact]
        public void LoadLatestIndexingState_IsLightweightReadOnlyWithoutCreatingDbOrMaterializingFilePaths()
        {
            var tempRoot = CreateTempDirectory("lightweight_indexing_state");
            try
            {
                var uncachedProjectDir = Path.Combine(tempRoot, "UncachedProject");
                var cachedProjectDir = Path.Combine(tempRoot, "CachedProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(uncachedProjectDir);
                Directory.CreateDirectory(cachedProjectDir);
                Directory.CreateDirectory(cacheDir);

                var bundlePath = Path.Combine(cachedProjectDir, "pack0.bundle");
                File.WriteAllBytes(bundlePath, CreateFilledBytes(2048, 0x3A));
                var scan = ProjectScanner.ScanFolder(cachedProjectDir);

                using (var writerCache = new SQLiteProjectIndexCache(cacheDir))
                {
                    var uncachedDbPath = writerCache.GetDbPath(uncachedProjectDir);
                    Assert.Null(writerCache.LoadLatestIndexingState(uncachedProjectDir));
                    Assert.False(File.Exists(uncachedDbPath), "LoadLatestIndexingState must never create empty DB files on disk.");

                    var sig = writerCache.GetFolderSignature(scan);
                    var readFiles = Enumerable.Range(0, 40)
                        .Select(i => Path.Combine(cachedProjectDir, $"bundle_{i:D3}.bundle"))
                        .ToList();

                    writerCache.SaveIndexingProgress(cachedProjectDir, sig, scan, new IndexingProgressUpdate
                    {
                        Status = "running",
                        TotalFiles = 40,
                        ProcessedFiles = 40,
                        PendingFiles = 0,
                        PercentComplete = 100.0,
                        CurrentFile = readFiles[^1],
                        LastReadFile = readFiles[^1],
                        NewlyReadFiles = readFiles
                    });
                }

                // Fresh cache instance simulating Project Manager startup: must read summary without WriteGate DDL
                // and must return EffectiveReadFileCount via COUNT(*) without materializing ReadFiles strings.
                using (var readerCache = new SQLiteProjectIndexCache(cacheDir))
                {
                    var state = readerCache.LoadLatestIndexingState(cachedProjectDir);
                    Assert.NotNull(state);
                    Assert.Equal("running", state!.Status);
                    Assert.Equal(40, state.ProcessedFiles);
                    Assert.Equal(40, state.EffectiveReadFileCount);
                    Assert.Empty(state.ReadFiles);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void IncrementalIndexDelta_AndIncrementalSemanticMerge_UpdateOnlyChangedDataCleanly()
        {
            var tempRoot = CreateTempDirectory("incremental_delta_and_semantic_merge");
            try
            {
                var projectDir = Path.Combine(tempRoot, "SyntheticProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(cacheDir);

                const int bundleCount = 40;
                const int assetsPerBundle = 25;
                var baseUtc = DateTime.UtcNow.AddMinutes(-30);
                var bundlePaths = new string[bundleCount];
                var allHandles = new List<AssetHandle>(bundleCount * assetsPerBundle);

                for (var b = 0; b < bundleCount; b++)
                {
                    var path = Path.Combine(projectDir, $"pack_{b:D3}.bundle");
                    bundlePaths[b] = path;
                    File.WriteAllBytes(path, CreateFilledBytes(4096, (byte)(0x10 + (b % 200))));
                    File.SetLastWriteTimeUtc(path, baseUtc);

                    for (var a = 0; a < assetsPerBundle; a++)
                    {
                        allHandles.Add(CreateHandle($"CAB-{b:D3}", path, 1000 + a, $"Mesh_{b:D3}_{a:D2}", ClassIDType.Mesh));
                    }
                }

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);
                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.20f1", allHandles, markIndexingCompleted: true));

                // Save preview entries for bundle 0 (will stay unchanged) and bundle 1 (will be modified).
                var unchangedHandle = allHandles[0];
                var dirtyHandle = allHandles[assetsPerBundle];
                cache.SavePreviewCachePayload(projectDir, sig1, unchangedHandle.UniqueID, "mesh-geometry", 1, "d=100", Encoding.UTF8.GetBytes("keep-me"));
                cache.SavePreviewCachePayload(projectDir, sig1, dirtyHandle.UniqueID, "mesh-geometry", 1, "d=100", Encoding.UTF8.GetBytes("evict-me"));

                // Save initial semantic edges for bundle 0 (unchanged) and bundle 1 (dirty).
                using (var initialStaging = cache.BeginSemanticStagingSession(projectDir, sig1))
                {
                    var rel = new SemanticAssetRelations();
                    rel.AssetEdges.Add(new SemanticAssetEdgeRelation(unchangedHandle.UniqueID, "Material", "m_Materials", 0, "mat-unchanged", 0, 1000, 0, 5000, true));
                    rel.AssetEdges.Add(new SemanticAssetEdgeRelation(dirtyHandle.UniqueID, "Material", "m_Materials", 0, "mat-dirty-v1", 0, 1000, 0, 5001, true));
                    initialStaging.FlushBatch(rel, clearBatch: true);
                    Assert.True(initialStaging.Commit(replaceExisting: true));
                }

                // Case 1: Small delta (modify only 2 bundles out of 40 -> 5% dirty).
                for (var b = 1; b <= 2; b++)
                {
                    File.WriteAllBytes(bundlePaths[b], CreateFilledBytes(5120, (byte)(0xAA + b)));
                    File.SetLastWriteTimeUtc(bundlePaths[b], baseUtc.AddMinutes(5));
                }

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                var plan2 = cache.PlanIncrementalDelta(projectDir, scan2);
                Assert.True(plan2.CanApplyIncremental);
                Assert.False(plan2.RequiresFullSemanticRebuild);
                Assert.Equal(2, plan2.DirtySourceFileIds.Count);
                Assert.Equal(2, plan2.FilesToReindex.Count);
                Assert.Equal(2, plan2.AffectedSemanticFilePaths.Count);
                Assert.DoesNotContain(bundlePaths[0], plan2.AffectedSemanticFilePaths, StringComparer.OrdinalIgnoreCase);

                var updatedHandles2 = new List<AssetHandle>();
                for (var b = 1; b <= 2; b++)
                {
                    for (var a = 0; a < assetsPerBundle; a++)
                    {
                        updatedHandles2.Add(CreateHandle($"CAB-{b:D3}", bundlePaths[b], 1000 + a, $"Mesh_{b:D3}_{a:D2}_V2", ClassIDType.Mesh));
                    }
                }

                var merged2 = allHandles
                    .Where(h => !string.Equals(h.OriginalPath, bundlePaths[1], StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(h.OriginalPath, bundlePaths[2], StringComparison.OrdinalIgnoreCase))
                    .Concat(updatedHandles2)
                    .ToList();

                Assert.True(cache.SaveIncrementalIndexDelta(projectDir, sig2, scan2, "2022.3.20f1", plan2, updatedHandles2, merged2, markIndexingCompleted: true));

                // Unchanged preview survives; dirty preview was evicted; all assets match merged2.
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, sig2, unchangedHandle.UniqueID, "mesh-geometry", 1, "d=100"));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig2, dirtyHandle.UniqueID, "mesh-geometry", 1, "d=100"));
                var reloaded2 = cache.LoadIndexCache(projectDir, sig2);
                Assert.NotNull(reloaded2);
                Assert.Equal(bundleCount * assetsPerBundle, reloaded2!.Count);

                // Merge updated semantic edge ONLY for dirty bundle using replaceExisting: false -> unchanged edge stays intact!
                using (var incStaging = cache.BeginSemanticStagingSession(projectDir, sig2))
                {
                    var incRel = new SemanticAssetRelations();
                    incRel.AssetEdges.Add(new SemanticAssetEdgeRelation(dirtyHandle.UniqueID, "Material", "m_Materials", 0, "mat-dirty-v2", 0, 1000, 0, 5002, true));
                    incStaging.FlushBatch(incRel, clearBatch: true);
                    Assert.True(incStaging.Commit(replaceExisting: false));
                }

                using (var conn = new SqliteConnection($"Data Source={cache.GetDbPath(projectDir)};Mode=ReadOnly;Pooling=False"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT TargetAssetUniqueID FROM AssetEdges ORDER BY TargetAssetUniqueID;";
                    var targets = new List<string>();
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        targets.Add(reader.GetString(0));
                    }

                    Assert.Equal(new[] { "mat-dirty-v2", "mat-unchanged" }, targets);
                }

                // Case 2: Large asset delta (modify 15 bundles out of 40 -> 37.5% dirty, triggers O(1) recreate + ordered bulk insert).
                for (var b = 5; b < 20; b++)
                {
                    File.WriteAllBytes(bundlePaths[b], CreateFilledBytes(6144, (byte)(0xC0 + b)));
                    File.SetLastWriteTimeUtc(bundlePaths[b], baseUtc.AddMinutes(10));
                }

                var scan3 = ProjectScanner.ScanFolder(projectDir);
                var sig3 = cache.GetFolderSignature(scan3);
                var plan3 = cache.PlanIncrementalDelta(projectDir, scan3);
                Assert.True(plan3.CanApplyIncremental);
                Assert.True(plan3.RequiresFullSemanticRebuild);
                Assert.Equal(15, plan3.DirtySourceFileIds.Count);

                var dirtySet3 = new HashSet<string>(plan3.FilesToReindex, StringComparer.OrdinalIgnoreCase);
                var updatedHandles3 = new List<AssetHandle>();
                for (var b = 5; b < 20; b++)
                {
                    for (var a = 0; a < assetsPerBundle; a++)
                    {
                        updatedHandles3.Add(CreateHandle($"CAB-{b:D3}", bundlePaths[b], 1000 + a, $"Mesh_{b:D3}_{a:D2}_V3", ClassIDType.Mesh));
                    }
                }

                var merged3 = merged2
                    .Where(h => !dirtySet3.Contains(h.OriginalPath))
                    .Concat(updatedHandles3)
                    .ToList();

                Assert.True(cache.SaveIncrementalIndexDelta(projectDir, sig3, scan3, "2022.3.20f1", plan3, updatedHandles3, merged3, markIndexingCompleted: true));

                // Untouched bundle 0 preview STILL survives even through the O(1) bulk recreate path!
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, sig3, unchangedHandle.UniqueID, "mesh-geometry", 1, "d=100"));
                var reloaded3 = cache.LoadIndexCache(projectDir, sig3);
                Assert.NotNull(reloaded3);
                Assert.Equal(bundleCount * assetsPerBundle, reloaded3!.Count);
                Assert.Contains(reloaded3, h => h.Name == "Mesh_010_00_V3");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void AssertMetricInvariants(BenchmarkMetricStats metric, int expectedIterations)
        {
            Assert.Equal(expectedIterations, metric.Iterations);
            Assert.Equal(expectedIterations, metric.SamplesMs.Length);
            Assert.True(metric.MinMs >= 0.0);
            Assert.True(metric.MinMs <= metric.MedianMs, $"Min ({metric.MinMs}) > Median ({metric.MedianMs}) for {metric.Name}");
            Assert.True(metric.MedianMs <= metric.P95Ms, $"Median ({metric.MedianMs}) > P95 ({metric.P95Ms}) for {metric.Name}");
            Assert.True(metric.P95Ms <= metric.MaxMs, $"P95 ({metric.P95Ms}) > Max ({metric.MaxMs}) for {metric.Name}");
            Assert.True(metric.IqrMs >= 0.0);
        }

        private static int ReadEnvInt(string variableName, int defaultValue, int min, int max)
        {
            var raw = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Clamp(parsed, min, max);
            }

            return defaultValue;
        }

        private static AssetHandle CreateHandle(string serializedFileName, string originalPath, long pathId, string name, ClassIDType type)
        {
            return new AssetHandle
            {
                UniqueID = AssetHandle.BuildUniqueID(serializedFileName, originalPath, pathId),
                Name = name,
                Type = type,
                Container = $"assets/synthetic/{name}.asset",
                OriginalPath = originalPath,
                SerializedFileName = serializedFileName,
                PathID = pathId,
                ByteStart = 0,
                ByteSize = 256
            };
        }

        private static byte[] CreateFilledBytes(int length, byte fill)
        {
            var data = new byte[length];
            Array.Fill(data, fill);
            return data;
        }

        private static string CreateTempDirectory(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
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
