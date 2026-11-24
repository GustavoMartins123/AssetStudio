using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class ProjectFingerprintTests
    {
        [Fact]
        public void Perf01_SameSizeSameCountModification_NormalAndPreservedMtime_InvalidatesSignatureAndCache()
        {
            var tempRoot = CreateTempDirectory("perf01_samesize");
            try
            {
                var projectDir = Path.Combine(tempRoot, "GameProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var fileA = Path.Combine(projectDir, "bundle_a.bundle");
                var fileB = Path.Combine(projectDir, "bundle_b.bundle");

                // > 16 KiB so head, middle, and tail windows are all distinct.
                var payloadA = new byte[20_000];
                var payloadB = new byte[20_000];
                Array.Fill(payloadA, (byte)0x11);
                Array.Fill(payloadB, (byte)0x22);
                File.WriteAllBytes(fileA, payloadA);
                File.WriteAllBytes(fileB, payloadB);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);

                Assert.StartsWith("v2:", sig1, StringComparison.Ordinal);
                Assert.DoesNotContain("_e", sig1, StringComparison.Ordinal);
                Assert.Equal(3 + 64, sig1.Length);

                var handles1 = new List<AssetHandle>
                {
                    CreateHandle("cab_a", fileA, 100, "MeshA_v1", ClassIDType.Mesh),
                    CreateHandle("cab_b", fileB, 200, "MeshB_v1", ClassIDType.Mesh)
                };
                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.20f1", handles1));
                Assert.Equal(2, cache.LoadIndexCache(projectDir, sig1)!.Count);

                // 1) Normal same-size, same-count write
                var modifiedNormal = (byte[])payloadA.Clone();
                modifiedNormal[16] ^= 0xFF;
                File.WriteAllBytes(fileA, modifiedNormal);
                File.SetLastWriteTimeUtc(fileA, File.GetLastWriteTimeUtc(fileA).AddSeconds(5));

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.Equal(scan1.TotalFiles, scan2.TotalFiles);
                Assert.Equal(scan1.TotalBytes, scan2.TotalBytes);
                Assert.NotEqual(sig1, sig2);
                Assert.Null(cache.LoadIndexCache(projectDir, sig2));
                Assert.False(MmapProjectIndexSidecar.TryLoadHandles(
                    cache.GetIdxPath(projectDir),
                    sig2,
                    SQLiteProjectIndexCache.SemanticSchemaVersion,
                    out _));

                var handles2 = new List<AssetHandle>
                {
                    CreateHandle("cab_a", fileA, 100, "MeshA_v2", ClassIDType.Mesh),
                    CreateHandle("cab_b", fileB, 200, "MeshB_v1", ClassIDType.Mesh)
                };
                Assert.True(cache.SaveIndexCache(projectDir, sig2, scan2, "2022.3.20f1", handles2));
                var reloaded2 = cache.LoadIndexCache(projectDir, sig2);
                Assert.NotNull(reloaded2);
                Assert.Contains(reloaded2!, h => h.Name == "MeshA_v2");

                // 2) Same-size, same-count write WITH explicitly preserved LastWriteTimeUtc (header window — zero-seek scan detects it)
                var preservedUtc = File.GetLastWriteTimeUtc(fileA);
                var modifiedHead = (byte[])modifiedNormal.Clone();
                modifiedHead[64] ^= 0x5A;
                File.WriteAllBytes(fileA, modifiedHead);
                File.SetLastWriteTimeUtc(fileA, preservedUtc);

                var scan3 = ProjectScanner.ScanFolder(projectDir);
                var sig3 = cache.GetFolderSignature(scan3);
                Assert.Equal(scan2.TotalFiles, scan3.TotalFiles);
                Assert.Equal(scan2.TotalBytes, scan3.TotalBytes);
                Assert.NotEqual(sig2, sig3);
                Assert.Null(cache.LoadIndexCache(projectDir, sig3));

                // 3) Same-size, same-count write WITH preserved mtime in mid/tail windows detected by bounded <=12 KiB helper
                var boundedBefore = XxHash64.ComputeFileSampleHash(fileA, modifiedHead.Length, out _);
                var modifiedMid = (byte[])modifiedHead.Clone();
                modifiedMid[modifiedMid.Length / 2] ^= 0xA5;
                File.WriteAllBytes(fileA, modifiedMid);
                File.SetLastWriteTimeUtc(fileA, preservedUtc);
                var boundedMid = XxHash64.ComputeFileSampleHash(fileA, modifiedMid.Length, out _);
                Assert.NotEqual(boundedBefore, boundedMid);

                var modifiedTail = (byte[])modifiedMid.Clone();
                modifiedTail[modifiedTail.Length - 32] ^= 0x3C;
                File.WriteAllBytes(fileA, modifiedTail);
                File.SetLastWriteTimeUtc(fileA, preservedUtc);
                var boundedTail = XxHash64.ComputeFileSampleHash(fileA, modifiedTail.Length, out _);
                Assert.NotEqual(boundedMid, boundedTail);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf01_RenameSameSize_AndDecryptionContextChange_InvalidateSignature()
        {
            var tempRoot = CreateTempDirectory("perf01_rename_decryption");
            try
            {
                var projectDir = Path.Combine(tempRoot, "GameProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var fileA = Path.Combine(projectDir, "alpha.bundle");
                var fileB = Path.Combine(projectDir, "beta.bundle");
                var content = new byte[512];
                Array.Fill(content, (byte)0x42);
                File.WriteAllBytes(fileA, content);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scanBefore = ProjectScanner.ScanFolder(projectDir);
                var sigBefore = cache.GetFolderSignature(scanBefore);

                // Rename with identical size, identical bytes, and preserved timestamp
                var preservedUtc = File.GetLastWriteTimeUtc(fileA);
                File.Move(fileA, fileB);
                File.SetLastWriteTimeUtc(fileB, preservedUtc);

                var scanAfterRename = ProjectScanner.ScanFolder(projectDir);
                var sigAfterRename = cache.GetFolderSignature(scanAfterRename);
                Assert.Equal(scanBefore.TotalFiles, scanAfterRename.TotalFiles);
                Assert.Equal(scanBefore.TotalBytes, scanAfterRename.TotalBytes);
                Assert.NotEqual(sigBefore, sigAfterRename);

                // Registering an active decryption profile invalidates signature even when EncryptedFileCount stays 0.
                try
                {
                    DecryptionHelper.RegisterProfile(new DecryptionProfile
                    {
                        Id = "test-fingerprint-profile",
                        Algorithm = "xor",
                        FileNamePattern = "*.customenc",
                        XorKeyHex = "AA55"
                    });

                    var scanWithProfile = ProjectScanner.ScanFolder(projectDir);
                    var sigWithProfile = cache.GetFolderSignature(scanWithProfile);
                    Assert.Equal(0, scanWithProfile.EncryptedFileCount);
                    Assert.False(string.IsNullOrWhiteSpace(scanWithProfile.DecryptionContextDigest));
                    Assert.NotEqual(sigAfterRename, sigWithProfile);
                }
                finally
                {
                    DecryptionHelper.Clear();
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf01_Large50MbSparseFile_UsesBoundedSampleIoWithoutReadingFullPayload()
        {
            var tempRoot = CreateTempDirectory("perf01_bounded_50mb");
            try
            {
                var projectDir = Path.Combine(tempRoot, "LargeProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                const long fiftyMegabytes = 50L * 1024 * 1024;
                var largeFile = Path.Combine(projectDir, "huge_asset.bin");
                using (var fs = new FileStream(largeFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.SetLength(fiftyMegabytes);
                    fs.Position = 0;
                    fs.Write(new byte[] { 1, 2, 3, 4 });
                    fs.Position = fiftyMegabytes / 2;
                    fs.Write(new byte[] { 5, 6, 7, 8 });
                    fs.Position = fiftyMegabytes - 16;
                    fs.Write(new byte[] { 9, 10, 11, 12 });
                }

                var sampleHash1 = XxHash64.ComputeFileSampleHash(largeFile, fiftyMegabytes, out var bytesSampled);
                Assert.Equal(XxHash64.MaxBoundedSampleBytes, bytesSampled);
                Assert.True(bytesSampled <= 12 * 1024, $"Expected bounded sample <= 12 KiB, got {bytesSampled} bytes.");

                var sw = Stopwatch.StartNew();
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                sw.Stop();

                Assert.Single(scan1.Files);
                Assert.Equal(fiftyMegabytes, scan1.Files[0].Size);
                Assert.Equal(XxHash64.ComputeHeadFileSampleHash(largeFile, fiftyMegabytes), scan1.Files[0].ContentSampleHash);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig1 = cache.GetFolderSignature(scan1);

                // Flip 1 byte in the header of the 50 MB file while preserving exact size & timestamp
                var originalUtc = File.GetLastWriteTimeUtc(largeFile);
                using (var fs = new FileStream(largeFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.Position = 16;
                    fs.WriteByte(0xEE);
                }
                File.SetLastWriteTimeUtc(largeFile, originalUtc);

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.NotEqual(scan1.Files[0].ContentSampleHash, scan2.Files[0].ContentSampleHash);
                Assert.NotEqual(sig1, sig2);

                // Flip 1 byte in the middle of the 50 MB file — bounded sample catches it in <= 12 KiB I/O
                using (var fs = new FileStream(largeFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.Position = fiftyMegabytes / 2;
                    fs.WriteByte(0xCC);
                }
                File.SetLastWriteTimeUtc(largeFile, originalUtc);
                var sampleHashAfterMid = XxHash64.ComputeFileSampleHash(largeFile, fiftyMegabytes, out var midBytesSampled);
                Assert.True(midBytesSampled <= 12 * 1024);
                Assert.NotEqual(sampleHash1, sampleHashAfterMid);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf02_LinuxCaseSensitivity_DistinctSourceHashesFolderKeysSqliteAndSidecar()
        {
            var tempRoot = CreateTempDirectory("perf02_linux_case");
            try
            {
                var lowerBundle = Path.Combine(tempRoot, "a.bundle");
                var upperBundle = Path.Combine(tempRoot, "A.bundle");

                // 1) AssetHandle.BuildSourceHash: distinct on Linux (caseSensitive: true), folded on Windows (caseSensitive: false)
                var linuxHashLower = AssetHandle.BuildSourceHash("CAB-1", lowerBundle, caseSensitive: true);
                var linuxHashUpper = AssetHandle.BuildSourceHash("CAB-1", upperBundle, caseSensitive: true);
                Assert.NotEqual(linuxHashLower, linuxHashUpper);

                var winHashLower = AssetHandle.BuildSourceHash("CAB-1", lowerBundle, caseSensitive: false);
                var winHashUpper = AssetHandle.BuildSourceHash("CAB-1", upperBundle, caseSensitive: false);
                Assert.Equal(winHashLower, winHashUpper);

                // 2) Unified GetFolderCacheKey across SQLiteProjectIndexCache & ProjectLoadingService + root "/" guard
                var projLower = Path.Combine(tempRoot, "game");
                var projUpper = Path.Combine(tempRoot, "Game");
                Assert.Equal(
                    SQLiteProjectIndexCache.GetFolderCacheKey(projLower),
                    ProjectLoadingService.GetFolderCacheKey(projLower));
                Assert.Equal(
                    SQLiteProjectIndexCache.GetFolderCacheKey(projLower + Path.DirectorySeparatorChar),
                    SQLiteProjectIndexCache.GetFolderCacheKey(projLower));
                Assert.NotEqual(
                    SQLiteProjectIndexCache.GetFolderCacheKey(projLower, caseSensitive: true),
                    SQLiteProjectIndexCache.GetFolderCacheKey(projUpper, caseSensitive: true));
                Assert.Equal(
                    SQLiteProjectIndexCache.GetFolderCacheKey(projLower, caseSensitive: false),
                    SQLiteProjectIndexCache.GetFolderCacheKey(projUpper, caseSensitive: false));
                Assert.Equal("/", SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot("/"));

                // 3) ProjectLoadingService.IsPathInsideDirectory respects caseSensitive
                var childLower = Path.Combine(projLower, "sub");
                var childUpper = Path.Combine(projUpper, "sub");
                Assert.True(ProjectLoadingService.IsPathInsideDirectory(projLower, childLower, caseSensitive: true));
                Assert.False(ProjectLoadingService.IsPathInsideDirectory(projLower, childUpper, caseSensitive: true));
                Assert.True(ProjectLoadingService.IsPathInsideDirectory(projLower, childUpper, caseSensitive: false));

                // 4) SQLite + Mmap sidecar in Linux case-sensitive mode: distinct SourceFiles for a.bundle vs A.bundle,
                //    and distinct Projects rows for /game vs /Game without COLLATE NOCASE aliasing.
                var cacheDir = Path.Combine(tempRoot, "CacheLinuxMode");
                using var linuxCache = new SQLiteProjectIndexCache(cacheDir, caseSensitivePaths: true);

                var scanLower = new ProjectScanResult(
                    projLower,
                    new[]
                    {
                        new ScannedFileInfo(lowerBundle, 64, ProjectFileKind.UnityBundle, lastWriteTimeUtcTicks: 100, contentSampleHash: 1),
                        new ScannedFileInfo(upperBundle, 64, ProjectFileKind.UnityBundle, lastWriteTimeUtcTicks: 100, contentSampleHash: 2)
                    });
                var sigLinux = linuxCache.GetFolderSignature(scanLower);

                var uidLower = $"CAB-1#{linuxHashLower}#1";
                var uidUpper = $"CAB-1#{linuxHashUpper}#1";
                var linuxHandles = new List<AssetHandle>
                {
                    new AssetHandle
                    {
                        UniqueID = uidLower,
                        Name = "LowerAsset",
                        Type = ClassIDType.Mesh,
                        OriginalPath = lowerBundle,
                        SerializedFileName = "CAB-1",
                        PathID = 1,
                        ByteStart = 0,
                        ByteSize = 64
                    },
                    new AssetHandle
                    {
                        UniqueID = uidUpper,
                        Name = "UpperAsset",
                        Type = ClassIDType.Mesh,
                        OriginalPath = upperBundle,
                        SerializedFileName = "CAB-1",
                        PathID = 1,
                        ByteStart = 0,
                        ByteSize = 64
                    }
                };

                Assert.True(linuxCache.SaveIndexCache(projLower, sigLinux, scanLower, "2022.3.20f1", linuxHandles));

                // Lookup using different casing on projUpper must MISS in Linux case-sensitive mode.
                Assert.Null(linuxCache.LoadIndexCache(projUpper, sigLinux));

                // Both SQLite and mmap sidecar preserve both distinct source paths without case-colliding.
                var loadedFromMmap = linuxCache.LoadIndexCache(projLower, sigLinux);
                var loadedFromSqlite = linuxCache.LoadIndexCacheFromSqliteOnly(projLower, sigLinux);
                Assert.NotNull(loadedFromMmap);
                Assert.NotNull(loadedFromSqlite);
                Assert.Equal(2, loadedFromMmap!.Count);
                Assert.Equal(2, loadedFromSqlite!.Count);
                Assert.Contains(loadedFromMmap, h => h.OriginalPath == lowerBundle && h.Name == "LowerAsset");
                Assert.Contains(loadedFromMmap, h => h.OriginalPath == upperBundle && h.Name == "UpperAsset");

                using (var conn = linuxCache.CreateReadConnection(projLower))
                {
                    Assert.Equal(2L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM SourceFiles;"));
                    using var idxCmd = conn.CreateCommand();
                    idxCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'idx_projects_lookup' LIMIT 1;";
                    var idxSql = idxCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                    Assert.DoesNotContain("NOCASE", idxSql, StringComparison.OrdinalIgnoreCase);
                }

                // 5) ProjectRuntimeDetector.ComputeSignature case-sensitivity
                var runtimeDir = Path.Combine(tempRoot, "RuntimeCase");
                Directory.CreateDirectory(runtimeDir);
                var asmPath = Path.Combine(runtimeDir, "Assembly.dll");
                File.WriteAllBytes(asmPath, new byte[] { 1, 2, 3, 4 });
                var sigCaseTrue = ProjectRuntimeDetector.ComputeSignature(runtimeDir, new[] { asmPath }, caseSensitive: true);
                var sigCaseFalse = ProjectRuntimeDetector.ComputeSignature(runtimeDir, new[] { asmPath }, caseSensitive: false);
                Assert.NotEqual(sigCaseTrue, sigCaseFalse);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf05_AtomicSidecarPublish_TruncatedTempPreservesPriorIdx_AndCommitFailureDeletesStaleIdx()
        {
            var tempRoot = CreateTempDirectory("perf05_atomic_publish");
            try
            {
                var projectDir = Path.Combine(tempRoot, "GameProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var bundlePath = Path.Combine(projectDir, "level0");
                File.WriteAllBytes(bundlePath, new byte[128]);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);
                var handles1 = new List<AssetHandle>
                {
                    CreateHandle("level0", bundlePath, 10, "AssetV1", ClassIDType.Mesh)
                };

                // 1) Transactional SaveIndexCache via ProjectLoadingService folds "completed" into same transaction
                //    and keeps PRAGMA synchronous = NORMAL (1).
                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.20f1", handles1, markIndexingCompleted: true));
                var idxPath = cache.GetIdxPath(projectDir);
                Assert.True(File.Exists(idxPath));
                var stateAfterSave = cache.LoadIndexingState(projectDir, sig1);
                Assert.NotNull(stateAfterSave);
                Assert.Equal("completed", stateAfterSave!.Status);
                using (var conn = cache.CreateConnection(projectDir))
                {
                    Assert.Equal(1L, ReadPragmaInt64(conn, "synchronous"));
                }

                // 2) Truncated/corrupted .tmp during TryWriteSidecar MUST fail pre-move validation,
                //    preserve the prior valid .idx intact, and leave zero .tmp files behind!
                var priorIdxBytes = File.ReadAllBytes(idxPath);
                var handlesCorrupt = new List<AssetHandle>
                {
                    CreateHandle("level0", bundlePath, 11, "AssetCorrupt", ClassIDType.Mesh)
                };
                var writeResult = MmapProjectIndexSidecar.TryWriteSidecar(
                    idxPath,
                    sig1 + "_corrupt",
                    SQLiteProjectIndexCache.SemanticSchemaVersion,
                    handlesCorrupt,
                    caseSensitivePaths: OperatingSystem.IsLinux(),
                    beforeValidateHook: tempPath =>
                    {
                        // Simulate a truncated/corrupt write right before validation
                        var bytes = File.ReadAllBytes(tempPath);
                        File.WriteAllBytes(tempPath, bytes.AsSpan(0, bytes.Length / 2).ToArray());
                    });

                Assert.False(writeResult);
                Assert.Equal(priorIdxBytes, File.ReadAllBytes(idxPath));
                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp"));

                // 3) If sidecar staging fails during SaveIndexCache for a new signature,
                //    SQLite commits durably AND the stale .idx from sig1 is DELETED (never left mismatched!).
                File.WriteAllBytes(bundlePath, Enumerable.Repeat((byte)0x77, 128).ToArray());
                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.NotEqual(sig1, sig2);

                var handles2 = new List<AssetHandle>
                {
                    CreateHandle("level0", bundlePath, 10, "AssetV2", ClassIDType.Mesh)
                };
                Assert.True(cache.SaveIndexCacheCore(
                    projectDir,
                    sig2,
                    scan2,
                    "2022.3.20f1",
                    handles2,
                    preserveSemanticRelations: false,
                    progress: null,
                    markIndexingCompleted: true,
                    beforeSidecarValidateHook: tempPath =>
                    {
                        var bytes = File.ReadAllBytes(tempPath);
                        bytes[^1] ^= 0xFF;
                        File.WriteAllBytes(tempPath, bytes);
                    }));

                Assert.False(File.Exists(idxPath));
                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp"));

                // Warm load transparently falls back to SQLite and returns V2 handles.
                var loadedV2 = cache.LoadIndexCache(projectDir, sig2);
                Assert.NotNull(loadedV2);
                Assert.Equal("AssetV2", Assert.Single(loadedV2!).Name);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task Perf11_MidIndexMutation_NeverStampsStaleT0Signature_AndGuardsSemanticCommit()
        {
            var tempRoot = CreateTempDirectory("perf11_mid_index_mutation");
            try
            {
                var projectDir = Path.Combine(tempRoot, "GameProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var fileA = Path.Combine(projectDir, "a.bundle");
                var fileB = Path.Combine(projectDir, "b.bundle");
                File.WriteAllBytes(fileA, new byte[4096]);
                File.WriteAllBytes(fileB, new byte[4096]);

                using var cache = new SQLiteProjectIndexCache(cacheDir);

                // Scan at t0
                var scanT0 = ProjectScanner.ScanFolder(projectDir);
                var sigT0 = cache.GetFolderSignature(scanT0);

                // Mutate fileA between t0 and t2 (identical byte size!)
                var mutated = new byte[4096];
                Array.Fill(mutated, (byte)0x99);
                File.WriteAllBytes(fileA, mutated);

                // 1) RevalidateFolder detects the mid-index mutation immediately
                var revalidation = ProjectScanner.RevalidateFolder(projectDir, scanT0);
                Assert.False(revalidation.IsUnchanged);
                Assert.Single(revalidation.ModifiedFiles);
                Assert.Empty(revalidation.AddedFiles);
                Assert.Empty(revalidation.DeletedFiles);
                var sigT2 = cache.GetFolderSignature(revalidation.CurrentScanResult);
                Assert.NotEqual(sigT0, sigT2);

                // 2) ReconcileMidIndexMutationsAsync with maxRetries=0 MUST abort instead of stamping t0
                var assetsManager = new AssetsManager { LazyLoading = true };
                assetsManager.ProjectIndex.AddHandle(CreateHandle("cab_a", fileA, 1, "OldA", ClassIDType.Mesh));
                assetsManager.ProjectIndex.AddHandle(CreateHandle("cab_b", fileB, 2, "KeepB", ClassIDType.Mesh));

                var aborted = await AssetLoadingService.ReconcileMidIndexMutationsAsync(
                    assetsManager,
                    projectDir,
                    scanT0,
                    inFlightDirtyPaths: new[] { fileA },
                    CancellationToken.None,
                    maxRetries: 0);
                Assert.False(aborted.CanCommit);
                Assert.Null(cache.LoadIndexCache(projectDir, sigT0));

                // 3) ReconcileMidIndexMutationsAsync with retries enabled reconciles under t2 (never t0),
                //    dropping stale handles for mutated fileA while preserving unaffected fileB handles.
                var reconciled = await AssetLoadingService.ReconcileMidIndexMutationsAsync(
                    assetsManager,
                    projectDir,
                    scanT0,
                    inFlightDirtyPaths: new[] { fileA },
                    CancellationToken.None,
                    maxRetries: AssetLoadingService.MaxMidIndexRevalidationRetries);
                Assert.True(reconciled.CanCommit);
                Assert.Equal(1, reconciled.RetryCount);
                Assert.Equal(sigT2, cache.GetFolderSignature(reconciled.ValidatedScanResult));
                Assert.NotNull(reconciled.ReconciledHandles);
                Assert.DoesNotContain(reconciled.ReconciledHandles!, h => h.Name == "OldA");
                Assert.Contains(reconciled.ReconciledHandles!, h => h.Name == "KeepB");

                // Save reconciled cache under sigT2 -> sigT0 must NEVER exist in cache
                Assert.True(cache.SaveIndexCache(projectDir, sigT2, reconciled.ValidatedScanResult, "2022.3.20f1", reconciled.ReconciledHandles!, markIndexingCompleted: true));
                Assert.Null(cache.LoadIndexCache(projectDir, sigT0));
                Assert.NotNull(cache.LoadIndexCache(projectDir, sigT2));

                // 4) Semantic staging commit guard: staging session opened for sigT0 MUST abort cleanly when scan is now sigT2
                using var staleSession = cache.BeginSemanticStagingSession(projectDir, sigT0);
                var dummyRelations = new SemanticAssetRelations();
                dummyRelations.AssetEdges.Add(new SemanticAssetEdgeRelation("src", "ref", "slot", 0, "dst", 0, 1, 0, 2, true));
                staleSession.FlushBatch(dummyRelations);

                var committed = ProjectLoadingService.TryCommitSemanticStagingSession(
                    cache,
                    projectDir,
                    reconciled.ValidatedScanResult,
                    staleSession);
                Assert.False(committed);
                Assert.True(staleSession.IsAborted);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf03_ThreeBundleIncrementalDelta_PreservesProjectIdUnchangedSourcesAssetsAndPreviews()
        {
            var tempRoot = CreateTempDirectory("perf03_incremental_delta");
            try
            {
                var projectDir = Path.Combine(tempRoot, "ThreeBundleProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var bundleA = Path.Combine(projectDir, "bundle_a.bundle");
                var bundleB = Path.Combine(projectDir, "bundle_b.bundle");
                var bundleC = Path.Combine(projectDir, "bundle_c.bundle");
                File.WriteAllBytes(bundleA, Enumerable.Repeat((byte)0x10, 8192).ToArray());
                File.WriteAllBytes(bundleB, Enumerable.Repeat((byte)0x20, 8192).ToArray());
                File.WriteAllBytes(bundleC, Enumerable.Repeat((byte)0x30, 8192).ToArray());

                // Set mtimes slightly in the past so racy-clean doesn't blur unchanged files
                var baseTime = DateTime.UtcNow.AddMinutes(-10);
                File.SetLastWriteTimeUtc(bundleA, baseTime);
                File.SetLastWriteTimeUtc(bundleB, baseTime);
                File.SetLastWriteTimeUtc(bundleC, baseTime);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);

                var handleA = CreateHandle("CAB-A", bundleA, 101, "MeshA", ClassIDType.Mesh);
                var handleB = CreateHandle("CAB-B", bundleB, 202, "MeshB", ClassIDType.Mesh);
                var handleC1 = CreateHandle("CAB-C", bundleC, 303, "MeshC_Old", ClassIDType.Mesh);

                Assert.True(cache.SaveIndexCache(
                    projectDir,
                    sig1,
                    scan1,
                    "2022.3.20f1",
                    new[] { handleA, handleB, handleC1 },
                    markIndexingCompleted: true));

                // Save PreviewCacheEntries for all 3 bundles
                cache.SavePreviewCachePayload(projectDir, sig1, handleA.UniqueID, "mesh-geometry", 1, "d=100", new byte[] { 1, 2, 3, 4 });
                cache.SavePreviewCachePayload(projectDir, sig1, handleB.UniqueID, "mesh-geometry", 1, "d=100", new byte[] { 5, 6, 7, 8 });
                cache.SavePreviewCachePayload(projectDir, sig1, handleC1.UniqueID, "mesh-geometry", 1, "d=100", new byte[] { 9, 10, 11, 12 });

                // Capture initial ProjectId and SourceFiles IDs + seed SourceFileExternals, Scenes, AssetEdges
                long initialProjectId;
                long srcIdA, srcIdB, srcIdC;
                using (var conn = cache.CreateConnection(projectDir))
                {
                    initialProjectId = ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;");
                    srcIdA = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-A';");
                    srcIdB = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-B';");
                    srcIdC = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-C';");

                    using var seedCmd = conn.CreateCommand();
                    seedCmd.CommandText = @"
                        INSERT INTO SourceFileExternals (ProjectId, SourceFileId, ExternalIndex, ExternalFileName, ExternalPathName, ExternalGuid)
                        VALUES (@pid, @srcA, 0, 'CAB-B', '', ''),
                               (@pid, @srcC, 0, 'CAB-A', '', '');

                        INSERT INTO Scenes (ProjectId, SceneId, Name, ScenePath, BuildIndex, EntrySourceFileId, ContainerKey, SourceKind)
                        VALUES (@pid, 1, 'SceneA', 'Assets/SceneA.unity', 0, @srcA, '', 'BuildSettings'),
                               (@pid, 2, 'SceneC', 'Assets/SceneC.unity', 1, @srcC, '', 'BuildSettings');

                        INSERT INTO AssetEdges (ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID, SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                        VALUES (@pid, @uidA, 'mat', '', 0, @uidB, 0, 101, 0, 202, 1),
                               (@pid, @uidC, 'mat', '', 0, @uidA, 0, 303, 0, 101, 1);";
                    seedCmd.Parameters.AddWithValue("@pid", initialProjectId);
                    seedCmd.Parameters.AddWithValue("@srcA", srcIdA);
                    seedCmd.Parameters.AddWithValue("@srcC", srcIdC);
                    seedCmd.Parameters.AddWithValue("@uidA", handleA.UniqueID);
                    seedCmd.Parameters.AddWithValue("@uidB", handleB.UniqueID);
                    seedCmd.Parameters.AddWithValue("@uidC", handleC1.UniqueID);
                    seedCmd.ExecuteNonQuery();
                }

                // Mutate ONLY bundleC (same size, updated content & timestamp)
                File.WriteAllBytes(bundleC, Enumerable.Repeat((byte)0x3F, 8192).ToArray());
                File.SetLastWriteTimeUtc(bundleC, baseTime.AddMinutes(5));

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.NotEqual(sig1, sig2);

                // Phase 2 selective delta plan: only bundleC needs re-indexing; bundleA & bundleB load straight from SQLite
                var targetIndex = new ProjectIndex();
                var filesToReindex = ProjectLoadingService.ResolveSelectiveColdIndexFiles(
                    cache,
                    projectDir,
                    scan2,
                    targetIndex,
                    out var deltaPlan,
                    out var unchangedSources);

                Assert.NotNull(deltaPlan);
                Assert.True(deltaPlan!.CanApplyIncremental);
                Assert.Equal(initialProjectId, deltaPlan.ProjectId);
                Assert.Equal(2, deltaPlan.UnchangedSourceFileIds.Count);
                Assert.Contains(srcIdA, deltaPlan.UnchangedSourceFileIds);
                Assert.Contains(srcIdB, deltaPlan.UnchangedSourceFileIds);
                Assert.Single(deltaPlan.DirtySourceFileIds, srcIdC);
                Assert.Single(filesToReindex, bundleC);
                Assert.NotNull(unchangedSources);
                Assert.Equal(2, unchangedSources!.Count);
                Assert.Equal(2, targetIndex.Count);

                // Simulate parsing ONLY bundleC and committing the incremental delta
                var handleC2 = CreateHandle("CAB-C", bundleC, 304, "MeshC_New", ClassIDType.Mesh);
                targetIndex.AddHandle(handleC2);

                Assert.True(cache.SaveIncrementalIndexDelta(
                    projectDir,
                    sig2,
                    scan2,
                    "2022.3.20f1",
                    deltaPlan,
                    updatedHandles: new[] { handleC2 },
                    allMergedHandles: targetIndex.GetHandles().ToList(),
                    markIndexingCompleted: true));

                // Verify ProjectId, unchanged SourceFiles.Id, unchanged Assets, unchanged Scenes/Externals,
                // and unchanged PreviewCacheEntries are ALL preserved while dirty bundleC rows were purged!
                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Projects;"));
                    Assert.Equal(initialProjectId, ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;"));
                    Assert.Equal(srcIdA, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-A';"));
                    Assert.Equal(srcIdB, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-B';"));

                    var newSrcIdC = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-C';");
                    Assert.NotEqual(srcIdC, newSrcIdC);

                    // Scenes: SceneA preserved, dirty SceneC deleted
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Scenes;"));
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Scenes WHERE Name = 'SceneA';"));

                    // SourceFileExternals: CAB-A external preserved, dirty CAB-C external deleted
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM SourceFileExternals;"));
                    Assert.Equal(1L, ExecuteScalarInt64(conn, $"SELECT COUNT(*) FROM SourceFileExternals WHERE SourceFileId = {srcIdA};"));

                    // PreviewCacheEntries: bundleA & bundleB preserved, dirty bundleC preview deleted!
                    Assert.Equal(2L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM PreviewCacheEntries;"));
                }

                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, sig2, handleA.UniqueID, "mesh-geometry", 1, "d=100"));
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, sig2, handleB.UniqueID, "mesh-geometry", 1, "d=100"));
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, sig2, handleC1.UniqueID, "mesh-geometry", 1, "d=100"));

                // Both mmap .idx and SQLite return the exact 3 merged handles (MeshA, MeshB, MeshC_New) under sig2
                var loadedMmap = cache.LoadIndexCache(projectDir, sig2);
                var loadedSqlite = cache.LoadIndexCacheFromSqliteOnly(projectDir, sig2);
                Assert.NotNull(loadedMmap);
                Assert.NotNull(loadedSqlite);
                Assert.Equal(3, loadedMmap!.Count);
                Assert.Equal(3, loadedSqlite!.Count);
                Assert.Contains(loadedMmap, h => h.Name == "MeshA");
                Assert.Contains(loadedMmap, h => h.Name == "MeshB");
                Assert.Contains(loadedMmap, h => h.Name == "MeshC_New");
                Assert.DoesNotContain(loadedMmap, h => h.Name == "MeshC_Old");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf03_SaveIndexCache_Under25PercentDirty_PreservesUnrelatedAssetEdgesAndPurgesCrossFileExternalsDependent()
        {
            var tempRoot = CreateTempDirectory("perf03_selective_edges");
            try
            {
                var projectDir = Path.Combine(tempRoot, "FiveBundleProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var baseTime = DateTime.UtcNow.AddMinutes(-15);
                var bundlePaths = new string[5];
                var handles = new List<AssetHandle>(5);
                for (var i = 0; i < 5; i++)
                {
                    var p = Path.Combine(projectDir, $"bundle_{i}.bundle");
                    File.WriteAllBytes(p, Enumerable.Repeat((byte)(0x10 + i), 8192).ToArray());
                    File.SetLastWriteTimeUtc(p, baseTime);
                    bundlePaths[i] = p;
                    handles.Add(CreateHandle($"CAB-{i}", p, 100 + i, $"Mesh_{i}", ClassIDType.Mesh));
                }

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);
                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.20f1", handles, markIndexingCompleted: true));

                long initialProjectId;
                long src0, src1, src4;
                using (var conn = cache.CreateConnection(projectDir))
                {
                    initialProjectId = ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;");
                    src0 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-0';");
                    src1 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-1';");
                    src4 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-4';");

                    // CAB-1 declares an external dependency on CAB-4; CAB-0 -> CAB-2 is completely unrelated.
                    using var seedCmd = conn.CreateCommand();
                    seedCmd.CommandText = @"
                        INSERT INTO SourceFileExternals (ProjectId, SourceFileId, ExternalIndex, ExternalFileName, ExternalPathName, ExternalGuid)
                        VALUES (@pid, @src1, 0, 'CAB-4', '', '');

                        INSERT INTO AssetEdges (ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID, SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                        VALUES (@pid, @u0, 'mat', '', 0, @u2, 0, 100, 0, 102, 1),
                               (@pid, @u1, 'mat', '', 0, @u4, 0, 101, 0, 104, 1),
                               (@pid, @u1, 'tex', '', 0, @u3, 0, 101, 0, 103, 1);";
                    seedCmd.Parameters.AddWithValue("@pid", initialProjectId);
                    seedCmd.Parameters.AddWithValue("@src1", src1);
                    seedCmd.Parameters.AddWithValue("@u0", handles[0].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u1", handles[1].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u2", handles[2].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u3", handles[3].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u4", handles[4].UniqueID);
                    seedCmd.ExecuteNonQuery();
                }

                // Mutate ONLY bundle_4 (1/5 = 20% <= 25% threshold) and save via SaveIndexCache directly
                File.WriteAllBytes(bundlePaths[4], Enumerable.Repeat((byte)0x99, 8192).ToArray());
                File.SetLastWriteTimeUtc(bundlePaths[4], baseTime.AddMinutes(5));

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.NotEqual(sig1, sig2);

                handles[4] = CreateHandle("CAB-4", bundlePaths[4], 999, "Mesh_4_Updated", ClassIDType.Mesh);
                Assert.True(cache.SaveIndexCache(projectDir, sig2, scan2, "2022.3.20f1", handles, markIndexingCompleted: true));

                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Equal(initialProjectId, ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;"));
                    Assert.Equal(src0, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-0';"));
                    Assert.Equal(src1, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-1';"));
                    Assert.NotEqual(src4, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-4';"));

                    // Unrelated edge (CAB-0 -> CAB-2) MUST survive; dependent CAB-1 edges MUST be purged!
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AssetEdges;"));
                    Assert.Equal(1L, ExecuteScalarInt64(conn, $"SELECT COUNT(*) FROM AssetEdges WHERE SourceAssetUniqueID = '{handles[0].UniqueID}';"));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Perf01_GetFolderSignature_CachesRepeatedLookups_AndInvalidatesOnFileListChange()
        {
            var root = Path.Combine(Path.GetTempPath(), "SigCacheProject");
            var entries = new List<ScannedFileInfo>(500);
            for (var i = 0; i < 500; i++)
            {
                entries.Add(new ScannedFileInfo(
                    Path.Combine(root, "Bundles", $"Asset_{i:D4}.bundle"),
                    4096,
                    ProjectFileKind.UnityBundle,
                    638000000000000000L + i,
                    (ulong)(i + 1)));
            }

            var scan = new ProjectScanResult(root, entries);
            var ciFirst = SQLiteProjectIndexCache.ComputeFolderSignature(scan, caseSensitive: false);
            var csFirst = SQLiteProjectIndexCache.ComputeFolderSignature(scan, caseSensitive: true);
            Assert.NotEqual(ciFirst, csFirst);

            // Repeated lookups on the same ProjectScanResult must hit the cache and return identical strings in O(1).
            var sw = Stopwatch.StartNew();
            for (var r = 0; r < 500; r++)
            {
                Assert.Same(ciFirst, SQLiteProjectIndexCache.ComputeFolderSignature(scan, caseSensitive: false));
                Assert.Same(csFirst, SQLiteProjectIndexCache.ComputeFolderSignature(scan, caseSensitive: true));
            }
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 250, $"Expected cached GetFolderSignature x1000 < 250ms, took {sw.ElapsedMilliseconds}ms.");

            // Appending a file to Files must invalidate the cached entry automatically.
            scan.Files.Add(new ScannedFileInfo(
                Path.Combine(root, "Bundles", "Asset_0500.bundle"),
                4096,
                ProjectFileKind.UnityBundle,
                638000000000000500L,
                501UL));

            var ciAfter = SQLiteProjectIndexCache.ComputeFolderSignature(scan, caseSensitive: false);
            Assert.NotEqual(ciFirst, ciAfter);
        }

        private static AssetHandle CreateHandle(string serializedFileName, string originalPath, long pathId, string name, ClassIDType type)
        {
            return new AssetHandle
            {
                UniqueID = AssetHandle.BuildUniqueID(serializedFileName, originalPath, pathId),
                Name = name,
                Type = type,
                Container = $"assets/{name.ToLowerInvariant()}.prefab",
                OriginalPath = originalPath,
                SerializedFileName = serializedFileName,
                PathID = pathId,
                ByteStart = pathId * 64,
                ByteSize = 1024
            };
        }

        private static long ExecuteScalarInt64(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static long ReadPragmaInt64(SqliteConnection conn, string pragmaName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragmaName};";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudioTests_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
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
