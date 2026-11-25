using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class CacheIntegrityAndTypeTreeFallbackTests
    {
        // =========================================================================
        // 1. PERF-01 — Deterministic folder signature (same-size replacement, order
        //    independence, and cached warm performance)
        // =========================================================================

        [Fact]
        public void Perf01_SameSizeReplacement_OrderIndependence_AndCachedWarmLookup()
        {
            var tempRoot = CreateTempDirectory("perf01_sig_integrity");
            try
            {
                var projectDir = Path.Combine(tempRoot, "Project");
                var cacheDir = Path.Combine(tempRoot, "Cache");
                Directory.CreateDirectory(Path.Combine(projectDir, "sub"));

                var file1 = Path.Combine(projectDir, "sub", "bundle_01.bundle");
                var file2 = Path.Combine(projectDir, "sub", "bundle_02.bundle");
                var payload1 = CreateFilledBytes(16384, 0x11);
                var payload2 = CreateFilledBytes(16384, 0x22);
                File.WriteAllBytes(file1, payload1);
                File.WriteAllBytes(file2, payload2);

                var baseUtc = DateTime.UtcNow.AddMinutes(-20);
                File.SetLastWriteTimeUtc(file1, baseUtc);
                File.SetLastWriteTimeUtc(file2, baseUtc);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);

                // Reversing file enumeration order must produce the exact same SHA-256 signature.
                var reversedScan = new ProjectScanResult(
                    scan1.RootPath,
                    scan1.Files.AsEnumerable().Reverse(),
                    scan1.DecryptionContextDigest);
                Assert.Equal(sig1, cache.GetFolderSignature(reversedScan));

                // Repeated calls on the same scan hit the O(1) signature cache.
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    Assert.Same(sig1, cache.GetFolderSignature(scan1));
                }
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 200, $"Warm signature lookup took {sw.ElapsedMilliseconds}ms.");

                // 1) Replace file1 with DIFFERENT bytes of the EXACT same size and updated timestamp.
                var replacedMid = (byte[])payload1.Clone();
                replacedMid[12000] ^= 0xFF;
                File.WriteAllBytes(file1, replacedMid);
                File.SetLastWriteTimeUtc(file1, baseUtc.AddSeconds(10));

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.Equal(scan1.TotalFiles, scan2.TotalFiles);
                Assert.Equal(scan1.TotalBytes, scan2.TotalBytes);
                Assert.NotEqual(sig1, sig2);

                // 2) Replace header bytes of file1 while preserving EXACT size AND exact timestamp.
                var preservedUtc = File.GetLastWriteTimeUtc(file1);
                var replacedHeader = (byte[])replacedMid.Clone();
                replacedHeader[32] ^= 0xAA;
                File.WriteAllBytes(file1, replacedHeader);
                File.SetLastWriteTimeUtc(file1, preservedUtc);

                var scan3 = ProjectScanner.ScanFolder(projectDir);
                var sig3 = cache.GetFolderSignature(scan3);
                Assert.Equal(scan2.TotalFiles, scan3.TotalFiles);
                Assert.Equal(scan2.TotalBytes, scan3.TotalBytes);
                Assert.NotEqual(sig2, sig3);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 2. PERF-02 — Case-sensitivity correctness on Linux vs Windows
        // =========================================================================

        [Fact]
        public void Perf02_LinuxCaseSensitivity_SignaturesScopeKeysAndSqliteIsolation()
        {
            var tempRoot = CreateTempDirectory("perf02_case_isolation");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "Cache");
                var rootLower = Path.Combine(tempRoot, "game");
                var rootUpper = Path.Combine(tempRoot, "Game");

                // 1) Folder cache keys and decompressed bundle scope keys
                Assert.NotEqual(
                    SQLiteProjectIndexCache.GetFolderCacheKey(rootLower, caseSensitive: true),
                    SQLiteProjectIndexCache.GetFolderCacheKey(rootUpper, caseSensitive: true));
                Assert.Equal(
                    SQLiteProjectIndexCache.GetFolderCacheKey(rootLower, caseSensitive: false),
                    SQLiteProjectIndexCache.GetFolderCacheKey(rootUpper, caseSensitive: false));

                Assert.NotEqual(
                    ProjectLoadingService.GetDecompressedBundleScopeKey(rootLower, caseSensitive: true),
                    ProjectLoadingService.GetDecompressedBundleScopeKey(rootUpper, caseSensitive: true));
                Assert.Equal(
                    ProjectLoadingService.GetDecompressedBundleScopeKey(rootLower, caseSensitive: false),
                    ProjectLoadingService.GetDecompressedBundleScopeKey(rootUpper, caseSensitive: false));
                Assert.Equal(
                    ProjectLoadingService.GetDecompressedBundleScopeKey(rootLower, caseSensitive: true),
                    ProjectLoadingService.GetDecompressedBundleScopeKey(rootLower + Path.DirectorySeparatorChar, caseSensitive: true));
                Assert.Equal("/", SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot("/"));
                Assert.Equal("/", SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot("///"));

                // 2) Folder signature distinguishes BundleA vs bundlea in Linux mode, folds in Windows mode
                var fileUpper = Path.Combine(rootLower, "BundleA.bundle");
                var fileLower = Path.Combine(rootLower, "bundlea.bundle");
                var scanUpper = new ProjectScanResult(rootLower, new[]
                {
                    new ScannedFileInfo(fileUpper, 1024, ProjectFileKind.UnityBundle, lastWriteTimeUtcTicks: 500, contentSampleHash: 77)
                });
                var scanLower = new ProjectScanResult(rootLower, new[]
                {
                    new ScannedFileInfo(fileLower, 1024, ProjectFileKind.UnityBundle, lastWriteTimeUtcTicks: 500, contentSampleHash: 77)
                });

                Assert.NotEqual(
                    SQLiteProjectIndexCache.ComputeFolderSignature(scanUpper, caseSensitive: true),
                    SQLiteProjectIndexCache.ComputeFolderSignature(scanLower, caseSensitive: true));
                Assert.Equal(
                    SQLiteProjectIndexCache.ComputeFolderSignature(scanUpper, caseSensitive: false),
                    SQLiteProjectIndexCache.ComputeFolderSignature(scanLower, caseSensitive: false));

                // 3) SQLiteProjectIndexCache in Linux mode keeps BundleA & bundlea distinct in SourceFiles
                using var linuxCache = new SQLiteProjectIndexCache(cacheDir, caseSensitivePaths: true);
                var combinedScan = new ProjectScanResult(rootLower, new[]
                {
                    new ScannedFileInfo(fileUpper, 1024, ProjectFileKind.UnityBundle, lastWriteTimeUtcTicks: 500, contentSampleHash: 11),
                    new ScannedFileInfo(fileLower, 1024, ProjectFileKind.UnityBundle, lastWriteTimeUtcTicks: 500, contentSampleHash: 22)
                });
                var sig = linuxCache.GetFolderSignature(combinedScan);

                var hashUpper = AssetHandle.BuildSourceHash("CAB-CASE", fileUpper, caseSensitive: true);
                var hashLower = AssetHandle.BuildSourceHash("CAB-CASE", fileLower, caseSensitive: true);
                Assert.NotEqual(hashUpper, hashLower);

                var handles = new List<AssetHandle>
                {
                    new AssetHandle
                    {
                        UniqueID = $"CAB-CASE#{hashUpper}#1",
                        Name = "AssetUpper",
                        Type = ClassIDType.Mesh,
                        OriginalPath = fileUpper,
                        SerializedFileName = "CAB-CASE",
                        PathID = 1,
                        ByteStart = 0,
                        ByteSize = 128
                    },
                    new AssetHandle
                    {
                        UniqueID = $"CAB-CASE#{hashLower}#1",
                        Name = "AssetLower",
                        Type = ClassIDType.Mesh,
                        OriginalPath = fileLower,
                        SerializedFileName = "CAB-CASE",
                        PathID = 1,
                        ByteStart = 128,
                        ByteSize = 128
                    }
                };

                Assert.True(linuxCache.SaveIndexCache(rootLower, sig, combinedScan, "2022.3.20f1", handles, markIndexingCompleted: true));
                Assert.Null(linuxCache.LoadIndexCache(rootUpper, sig));
                var loaded = linuxCache.LoadIndexCacheFromSqliteOnly(rootLower, sig);
                Assert.NotNull(loaded);
                Assert.Equal(2, loaded!.Count);
                Assert.Contains(loaded, h => h.OriginalPath == fileUpper && h.Name == "AssetUpper");
                Assert.Contains(loaded, h => h.OriginalPath == fileLower && h.Name == "AssetLower");

                // 4) RevalidateFolder honors caseSensitivePaths for knownDirtyPaths
                var realDir = Path.Combine(tempRoot, "RevalDir");
                Directory.CreateDirectory(realDir);
                var realFile = Path.Combine(realDir, "RealBundle.bundle");
                File.WriteAllBytes(realFile, CreateFilledBytes(256, 0x33));
                File.SetLastWriteTimeUtc(realFile, DateTime.UtcNow.AddMinutes(-5));

                var baseline = ProjectScanner.ScanFolder(realDir);
                var mismatchedCasePath = Path.Combine(realDir, "realbundle.bundle");
                var csReval = ProjectScanner.RevalidateFolder(
                    realDir,
                    baseline,
                    knownDirtyPaths: new[] { mismatchedCasePath },
                    verifyAllContentSamples: false,
                    caseSensitivePaths: true);
                Assert.True(csReval.IsUnchanged);

                var ciReval = ProjectScanner.RevalidateFolder(
                    realDir,
                    baseline,
                    knownDirtyPaths: new[] { mismatchedCasePath },
                    verifyAllContentSamples: false,
                    caseSensitivePaths: false);
                Assert.False(ciReval.IsUnchanged);
                Assert.Single(ciReval.ModifiedFiles);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 3. PERF-03 — Incremental per-bundle invalidation preserves unchanged bundles
        //    and invalidates only modified/dependent relations
        // =========================================================================

        [Fact]
        public void Perf03_ModifyOneBundleAndDeleteAnother_PreservesUnchangedBundlesAndRelations()
        {
            var tempRoot = CreateTempDirectory("perf03_incremental");
            try
            {
                var projectDir = Path.Combine(tempRoot, "FourBundles");
                var cacheDir = Path.Combine(tempRoot, "Cache");
                Directory.CreateDirectory(projectDir);

                var baseUtc = DateTime.UtcNow.AddMinutes(-15);
                var paths = new string[4];
                var handles = new List<AssetHandle>(4);
                for (int i = 0; i < 4; i++)
                {
                    var p = Path.Combine(projectDir, $"bundle_{i}.bundle");
                    File.WriteAllBytes(p, CreateFilledBytes(4096, (byte)(0x40 + i)));
                    File.SetLastWriteTimeUtc(p, baseUtc);
                    paths[i] = p;
                    handles.Add(CreateHandle($"CAB-{i}", p, 100 + i, $"Asset_{i}", ClassIDType.Mesh));
                }

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);
                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.20f1", handles, markIndexingCompleted: true));

                long projectId;
                long src0, src1, src2, src3;
                using (var conn = cache.CreateConnection(projectDir))
                {
                    projectId = ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;");
                    src0 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-0';");
                    src1 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-1';");
                    src2 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-2';");
                    src3 = ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-3';");

                    // Seed outgoing + incoming relations:
                    // - Unrelated: CAB-0 -> CAB-1 (must survive!)
                    // - Outgoing from modified CAB-3: CAB-3 -> CAB-1 (must be invalidated!)
                    // - Incoming into modified CAB-3: CAB-2 -> CAB-3 (must be invalidated!)
                    using var seedCmd = conn.CreateCommand();
                    seedCmd.CommandText = @"
                        INSERT INTO AssetEdges (ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID, SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                        VALUES (@pid, @u0, 'mat', '', 0, @u1, 0, 100, 0, 101, 1),
                               (@pid, @u3, 'mat', '', 0, @u1, 0, 103, 0, 101, 1),
                               (@pid, @u2, 'mat', '', 0, @u3, 0, 102, 0, 103, 1);";
                    seedCmd.Parameters.AddWithValue("@pid", projectId);
                    seedCmd.Parameters.AddWithValue("@u0", handles[0].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u1", handles[1].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u2", handles[2].UniqueID);
                    seedCmd.Parameters.AddWithValue("@u3", handles[3].UniqueID);
                    seedCmd.ExecuteNonQuery();
                }

                // Mutate ONLY bundle_3 with the exact same byte size (1/4 = 25% -> selective semantic invalidation)
                File.WriteAllBytes(paths[3], CreateFilledBytes(4096, 0xFE));
                File.SetLastWriteTimeUtc(paths[3], baseUtc.AddMinutes(3));

                var scan2 = ProjectScanner.ScanFolder(projectDir);
                var sig2 = cache.GetFolderSignature(scan2);
                Assert.NotEqual(sig1, sig2);

                var incrementalIndex = new ProjectIndex();
                var toReindex = ProjectLoadingService.ResolveSelectiveColdIndexFiles(
                    cache,
                    projectDir,
                    scan2,
                    incrementalIndex,
                    out var plan,
                    out var unchangedSources);

                Assert.NotNull(plan);
                Assert.True(plan!.CanApplyIncremental);
                Assert.False(plan.RequiresFullSemanticRebuild);
                Assert.Single(toReindex, paths[3]);
                Assert.Equal(3, unchangedSources!.Count);
                Assert.Equal(3, incrementalIndex.Count);

                var updatedHandle3 = CreateHandle("CAB-3", paths[3], 903, "Asset_3_V2", ClassIDType.Mesh);
                incrementalIndex.AddHandle(updatedHandle3);

                Assert.True(cache.SaveIncrementalIndexDelta(
                    projectDir,
                    sig2,
                    scan2,
                    "2022.3.20f1",
                    plan,
                    updatedHandles: new[] { updatedHandle3 },
                    allMergedHandles: incrementalIndex.GetHandles().ToList(),
                    markIndexingCompleted: true));

                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Equal(projectId, ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;"));
                    Assert.Equal(src0, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-0';"));
                    Assert.Equal(src1, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-1';"));
                    Assert.Equal(src2, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-2';"));
                    Assert.NotEqual(src3, ExecuteScalarInt64(conn, "SELECT Id FROM SourceFiles WHERE SerializedFileName = 'CAB-3';"));

                    // Unrelated CAB-0 -> CAB-1 edge preserved; outgoing and incoming edges touching CAB-3 purged!
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AssetEdges;"));
                    Assert.Equal(1L, ExecuteScalarInt64(conn, $"SELECT COUNT(*) FROM AssetEdges WHERE SourceAssetUniqueID = '{handles[0].UniqueID}';"));
                }

                // Now DELETE bundle_2 -> FilesToReindex is empty, DeletedFilePaths has bundle_2, remaining 3 stay preserved!
                File.Delete(paths[2]);
                var scan3 = ProjectScanner.ScanFolder(projectDir);
                var sig3 = cache.GetFolderSignature(scan3);
                Assert.NotEqual(sig2, sig3);

                var afterDeleteIndex = new ProjectIndex();
                var toReindexAfterDelete = ProjectLoadingService.ResolveSelectiveColdIndexFiles(
                    cache,
                    projectDir,
                    scan3,
                    afterDeleteIndex,
                    out var deletePlan,
                    out _);

                Assert.NotNull(deletePlan);
                Assert.True(deletePlan!.CanApplyIncremental);
                Assert.Empty(toReindexAfterDelete);
                Assert.Single(deletePlan.DeletedFilePaths, paths[2]);
                Assert.Equal(3, afterDeleteIndex.Count);

                Assert.True(cache.SaveIncrementalIndexDelta(
                    projectDir,
                    sig3,
                    scan3,
                    "2022.3.20f1",
                    deletePlan,
                    updatedHandles: Array.Empty<AssetHandle>(),
                    allMergedHandles: afterDeleteIndex.GetHandles().ToList(),
                    markIndexingCompleted: true));

                var finalLoaded = cache.LoadIndexCache(projectDir, sig3);
                Assert.NotNull(finalLoaded);
                Assert.Equal(3, finalLoaded!.Count);
                Assert.DoesNotContain(finalLoaded, h => h.SerializedFileName == "CAB-2");
                Assert.Contains(finalLoaded, h => h.Name == "Asset_3_V2");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 4. UNITY-04 / UNITY-06 / UNITY-09 — MeshRenderer & SkinnedMeshRenderer
        //    binary fast path, TypeTree fallback on drifted layouts, and graceful
        //    degradation on truncated/corrupt payloads without TypeTree
        // =========================================================================

        [Fact]
        public void Unity09_MeshRenderer_ValidBinary_UsesDirectBinaryPath()
        {
            byte[] payload = BuildStandardMeshRendererBinaryUnity2022(
                gameObjectPathId: 1001,
                enabled: true,
                materials: new[] { (0, 2001L), (1, 2002L) },
                firstSubMesh: 3,
                subMeshCount: 2,
                additionalVertexStreamsPathId: 3001,
                enlightenVertexStreamPathId: 3002);

            var reader = CreateObjectReader(
                payload,
                ClassIDType.MeshRenderer,
                unityVersion: "2022.3.20f1",
                pathId: 500,
                typeTree: null);

            var mr = new MeshRenderer(reader);

            Assert.False(mr.UsedTypeTreeFallback);
            Assert.False(mr.IsTruncated);
            Assert.Equal(0, mr.m_GameObject.m_FileID);
            Assert.Equal(1001L, mr.m_GameObject.m_PathID);
            Assert.True(mr.m_Enabled);
            Assert.Equal(2, mr.m_Materials.Length);
            Assert.Equal(2001L, mr.m_Materials[0].m_PathID);
            Assert.Equal(1, mr.m_Materials[1].m_FileID);
            Assert.Equal(2002L, mr.m_Materials[1].m_PathID);
            Assert.NotNull(mr.m_StaticBatchInfo);
            Assert.Equal((ushort)3, mr.m_StaticBatchInfo!.firstSubMesh);
            Assert.Equal((ushort)2, mr.m_StaticBatchInfo.subMeshCount);
            Assert.NotNull(mr.m_AdditionalVertexStreams);
            Assert.Equal(3001L, mr.m_AdditionalVertexStreams!.m_PathID);
            Assert.NotNull(mr.m_EnlightenVertexStream);
            Assert.Equal(3002L, mr.m_EnlightenVertexStream!.m_PathID);
        }

        [Fact]
        public void Unity09_SkinnedMeshRenderer_ValidBinaryUnity6000_UsesDirectBinaryPath()
        {
            byte[] payload = BuildStandardSkinnedMeshRendererBinaryUnity6000_3(
                gameObjectPathId: 4001,
                enabled: true,
                materials: new[] { (0, 4101L) },
                firstSubMesh: 2,
                subMeshCount: 1,
                meshPathId: 4201,
                bones: new[] { (0, 4301L), (0, 4302L) },
                blendShapeWeights: new[] { 0.5f, 1.0f },
                rootBonePathId: 4301,
                center: new Vector3(1f, 2f, 3f),
                extent: new Vector3(4f, 5f, 6f),
                dirtyAabb: true);

            var reader = CreateObjectReader(
                payload,
                ClassIDType.SkinnedMeshRenderer,
                unityVersion: "6000.3.1f1",
                pathId: 501,
                typeTree: null);

            var smr = new SkinnedMeshRenderer(reader);

            Assert.False(smr.UsedTypeTreeFallback);
            Assert.False(smr.IsTruncated);
            Assert.Equal(4001L, smr.m_GameObject.m_PathID);
            Assert.True(smr.m_Enabled);
            Assert.Single(smr.m_Materials);
            Assert.Equal(4101L, smr.m_Materials[0].m_PathID);
            Assert.Equal((ushort)2, smr.m_StaticBatchInfo!.firstSubMesh);
            Assert.Equal((ushort)1, smr.m_StaticBatchInfo.subMeshCount);
            Assert.Equal(4201L, smr.m_Mesh.m_PathID);
            Assert.Equal(2, smr.m_Bones.Length);
            Assert.Equal(4301L, smr.m_Bones[0].m_PathID);
            Assert.Equal(4302L, smr.m_Bones[1].m_PathID);
            Assert.Equal(new[] { 0.5f, 1.0f }, smr.m_BlendShapeWeights);
            Assert.Equal(4301L, smr.m_RootBone!.m_PathID);
            Assert.Equal(new Vector3(1f, 2f, 3f), smr.m_AABB!.m_Center);
            Assert.Equal(new Vector3(4f, 5f, 6f), smr.m_AABB.m_Extent);
            Assert.True(smr.m_DirtyAABB);
        }

        [Fact]
        public void Unity04_09_MeshRenderer_DriftedLayoutWithTypeTree_RecoversAllFieldsViaTypeTreeFallback()
        {
            // Simulate a drifted/future Unity layout where extra custom fields are inserted BEFORE m_Materials
            // (so the hardcoded binary Renderer constructor would misalign or fail).
            var typeTree = BuildDriftedMeshRendererTypeTree();
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                // m_GameObject (PPtr)
                bw.Write(0);
                bw.Write(4201L);
                // m_Enabled (bool + align4)
                bw.Write((byte)0);
                bw.Write(new byte[3]);
                // Drifted unknown fields (would break binary Renderer parser!)
                bw.Write(0x7FFFFFFF); // m_CustomFutureInt (looks like huge array length if misread!)
                bw.Write(123456789UL); // m_CustomFutureFlags
                // m_Materials (2 items)
                bw.Write(2);
                bw.Write(0); bw.Write(5101L);
                bw.Write(2); bw.Write(5102L);
                // m_StaticBatchInfo
                bw.Write((ushort)7);
                bw.Write((ushort)4);
                // m_AdditionalVertexStreams
                bw.Write(0); bw.Write(6101L);
                // m_EnlightenVertexStream
                bw.Write(1); bw.Write(6102L);
                bw.Flush();
                payload = ms.ToArray();
            }

            var reader = CreateObjectReader(
                payload,
                ClassIDType.MeshRenderer,
                unityVersion: "2022.3.20f1",
                pathId: 700,
                typeTree: typeTree);

            var mr = new MeshRenderer(reader);

            Assert.True(mr.UsedTypeTreeFallback);
            Assert.False(mr.IsTruncated);
            Assert.False(string.IsNullOrWhiteSpace(mr.ParseDiagnostic));
            Assert.Equal(4201L, mr.m_GameObject.m_PathID);
            Assert.False(mr.m_Enabled);
            Assert.Equal(2, mr.m_Materials.Length);
            Assert.Equal(0, mr.m_Materials[0].m_FileID);
            Assert.Equal(5101L, mr.m_Materials[0].m_PathID);
            Assert.Equal(2, mr.m_Materials[1].m_FileID);
            Assert.Equal(5102L, mr.m_Materials[1].m_PathID);
            Assert.NotNull(mr.m_StaticBatchInfo);
            Assert.Equal((ushort)7, mr.m_StaticBatchInfo!.firstSubMesh);
            Assert.Equal((ushort)4, mr.m_StaticBatchInfo.subMeshCount);
            Assert.Equal(6101L, mr.m_AdditionalVertexStreams!.m_PathID);
            Assert.Equal(1, mr.m_EnlightenVertexStream!.m_FileID);
            Assert.Equal(6102L, mr.m_EnlightenVertexStream.m_PathID);
        }

        [Fact]
        public void Unity04_09_SkinnedMeshRenderer_DriftedLayoutWithTypeTree_RecoversMeshBonesRootBoneAndMaterials()
        {
            var typeTree = BuildDriftedSkinnedMeshRendererTypeTree();
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                // m_GameObject
                bw.Write(0); bw.Write(8001L);
                // m_Enabled + align4
                bw.Write((byte)1); bw.Write(new byte[3]);
                // Drifted unknown future block
                bw.Write(0x55AA55AAu);
                bw.Write(3.5f);
                // m_Materials (1 material)
                bw.Write(1);
                bw.Write(3); bw.Write(8101L);
                // m_StaticBatchInfo
                bw.Write((ushort)1);
                bw.Write((ushort)3);
                // m_Mesh
                bw.Write(0); bw.Write(8201L);
                // m_Bones (3 transforms)
                bw.Write(3);
                bw.Write(0); bw.Write(8301L);
                bw.Write(0); bw.Write(8302L);
                bw.Write(1); bw.Write(8303L);
                // m_BlendShapeWeights (2 floats)
                bw.Write(2);
                bw.Write(0.25f);
                bw.Write(0.75f);
                // m_RootBone
                bw.Write(0); bw.Write(8301L);
                // m_AABB (center + extent)
                bw.Write(1.0f); bw.Write(2.0f); bw.Write(3.0f);
                bw.Write(4.0f); bw.Write(5.0f); bw.Write(6.0f);
                // m_DirtyAABB + align4
                bw.Write((byte)1); bw.Write(new byte[3]);
                bw.Flush();
                payload = ms.ToArray();
            }

            var reader = CreateObjectReader(
                payload,
                ClassIDType.SkinnedMeshRenderer,
                unityVersion: "7000.1.0f1",
                pathId: 701,
                typeTree: typeTree);

            var smr = new SkinnedMeshRenderer(reader);

            Assert.True(smr.UsedTypeTreeFallback);
            Assert.False(smr.IsTruncated);
            Assert.Equal(8001L, smr.m_GameObject.m_PathID);
            Assert.True(smr.m_Enabled);
            Assert.Single(smr.m_Materials);
            Assert.Equal(3, smr.m_Materials[0].m_FileID);
            Assert.Equal(8101L, smr.m_Materials[0].m_PathID);
            Assert.NotNull(smr.m_StaticBatchInfo);
            Assert.Equal((ushort)1, smr.m_StaticBatchInfo!.firstSubMesh);
            Assert.Equal((ushort)3, smr.m_StaticBatchInfo.subMeshCount);
            Assert.Equal(8201L, smr.m_Mesh.m_PathID);
            Assert.Equal(3, smr.m_Bones.Length);
            Assert.Equal(8301L, smr.m_Bones[0].m_PathID);
            Assert.Equal(8302L, smr.m_Bones[1].m_PathID);
            Assert.Equal(1, smr.m_Bones[2].m_FileID);
            Assert.Equal(8303L, smr.m_Bones[2].m_PathID);
            Assert.Equal(new[] { 0.25f, 0.75f }, smr.m_BlendShapeWeights);
            Assert.NotNull(smr.m_RootBone);
            Assert.Equal(8301L, smr.m_RootBone!.m_PathID);
            Assert.NotNull(smr.m_AABB);
            Assert.Equal(new Vector3(1f, 2f, 3f), smr.m_AABB!.m_Center);
            Assert.Equal(new Vector3(4f, 5f, 6f), smr.m_AABB.m_Extent);
            Assert.True(smr.m_DirtyAABB);
        }

        [Fact]
        public void Unity06_TruncatedAndCorruptRenderersWithoutTypeTree_FailGracefullyWithoutAbortingSerializedFile()
        {
            // 1) Truncated MeshRenderer (only 12 bytes, no TypeTree)
            var truncatedMrReader = CreateObjectReader(
                new byte[12],
                ClassIDType.MeshRenderer,
                unityVersion: "2022.3.20f1",
                pathId: 901,
                typeTree: null);
            var truncatedMr = new MeshRenderer(truncatedMrReader);
            Assert.True(truncatedMr.IsTruncated);
            Assert.False(truncatedMr.UsedTypeTreeFallback);
            Assert.False(string.IsNullOrWhiteSpace(truncatedMr.ParseDiagnostic));
            Assert.NotNull(truncatedMr.m_Materials);
            Assert.Empty(truncatedMr.m_Materials);
            Assert.Equal(12, truncatedMr.GetRawData().Length);

            // 2) Corrupt SkinnedMeshRenderer with huge m_MaterialsSize (no TypeTree)
            var corruptPayload = BuildStandardMeshRendererBinaryUnity2022(
                gameObjectPathId: 1001,
                enabled: true,
                materials: Array.Empty<(int, long)>(),
                firstSubMesh: 0,
                subMeshCount: 0,
                additionalVertexStreamsPathId: 0,
                enlightenVertexStreamPathId: 0);
            // Overwrite m_MaterialsSize with int.MaxValue (offset: 12 m_GameObject + 12 flags + 4 layerMask + 4 priority + 4 lightmaps + 32 tiling = 68)
            BinaryPrimitives.WriteInt32LittleEndian(corruptPayload.AsSpan(68, 4), int.MaxValue);

            var corruptSmrReader = CreateObjectReader(
                corruptPayload,
                ClassIDType.SkinnedMeshRenderer,
                unityVersion: "2022.3.20f1",
                pathId: 902,
                typeTree: null);
            var corruptSmr = new SkinnedMeshRenderer(corruptSmrReader);
            Assert.True(corruptSmr.IsTruncated);
            Assert.False(corruptSmr.UsedTypeTreeFallback);
            Assert.False(string.IsNullOrWhiteSpace(corruptSmr.ParseDiagnostic));
            Assert.NotNull(corruptSmr.m_Materials);
            Assert.Empty(corruptSmr.m_Materials);
            Assert.NotNull(corruptSmr.m_Bones);
            Assert.Empty(corruptSmr.m_Bones);
            Assert.NotNull(corruptSmr.m_Mesh);

            // 3) Multi-object buffer: truncated object with TypeTree must NOT read past byteSize into neighbor object!
            var multiBuf = new byte[256];
            Array.Fill(multiBuf, (byte)0x01);
            var mrTypeTree = BuildDriftedMeshRendererTypeTree();
            var boundedReader = CreateObjectReader(
                multiBuf,
                ClassIDType.MeshRenderer,
                unityVersion: "2022.3.20f1",
                pathId: 903,
                typeTree: mrTypeTree,
                declaredByteSize: 8); // Only 8 bytes belong to this object; neighbor bytes follow!

            Assert.False(TypeTreeHelper.TryReadType(mrTypeTree, boundedReader, out _, out var ttError));
            Assert.False(string.IsNullOrWhiteSpace(ttError));

            var boundedMr = new MeshRenderer(boundedReader);
            Assert.True(boundedMr.IsTruncated);
            Assert.False(boundedMr.UsedTypeTreeFallback);
            Assert.Contains("TypeTree", boundedMr.ParseDiagnostic!, StringComparison.Ordinal);

            // 4) Full AssetsManager.CreateObjectFromReader across 3 objects in one SerializedFile:
            //    corrupt MeshRenderer + corrupt SkinnedMeshRenderer + valid MeshRenderer all instantiate cleanly!
            var validBytes = BuildStandardMeshRendererBinaryUnity2022(
                gameObjectPathId: 7777,
                enabled: true,
                materials: new[] { (0, 8888L) },
                firstSubMesh: 0,
                subMeshCount: 1,
                additionalVertexStreamsPathId: 0,
                enlightenVertexStreamPathId: 0);

            var combinedBytes = new byte[12 + corruptPayload.Length + validBytes.Length];
            Buffer.BlockCopy(corruptPayload, 0, combinedBytes, 12, corruptPayload.Length);
            Buffer.BlockCopy(validBytes, 0, combinedBytes, 12 + corruptPayload.Length, validBytes.Length);

            var sf = CreateSyntheticSerializedFile(combinedBytes, "2022.3.20f1");
            var info1 = new ObjectInfo { m_PathID = 1, byteStart = 0, byteSize = 12, classID = (int)ClassIDType.MeshRenderer };
            var info2 = new ObjectInfo { m_PathID = 2, byteStart = 12, byteSize = (uint)corruptPayload.Length, classID = (int)ClassIDType.SkinnedMeshRenderer };
            var info3 = new ObjectInfo { m_PathID = 3, byteStart = 12 + corruptPayload.Length, byteSize = (uint)validBytes.Length, classID = (int)ClassIDType.MeshRenderer };

            var obj1 = Assert.IsType<MeshRenderer>(AssetsManager.CreateObjectFromReader(new ObjectReader(sf.reader, sf, info1)));
            var obj2 = Assert.IsType<SkinnedMeshRenderer>(AssetsManager.CreateObjectFromReader(new ObjectReader(sf.reader, sf, info2)));
            var obj3 = Assert.IsType<MeshRenderer>(AssetsManager.CreateObjectFromReader(new ObjectReader(sf.reader, sf, info3)));

            Assert.True(obj1.IsTruncated);
            Assert.True(obj2.IsTruncated);
            Assert.False(obj3.IsTruncated);
            Assert.Equal(7777L, obj3.m_GameObject.m_PathID);
            Assert.Single(obj3.m_Materials);
            Assert.Equal(8888L, obj3.m_Materials[0].m_PathID);
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static byte[] BuildStandardMeshRendererBinaryUnity2022(
            long gameObjectPathId,
            bool enabled,
            (int FileId, long PathId)[] materials,
            ushort firstSubMesh,
            ushort subMeshCount,
            long additionalVertexStreamsPathId,
            long enlightenVertexStreamPathId)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // Component.m_GameObject (PPtr: 4 + 8)
            bw.Write(0);
            bw.Write(gameObjectPathId);

            // Renderer flags for Unity 2022.3 (10 bytes + 2 pad = 12 bytes):
            // m_Enabled, m_CastShadows, m_ReceiveShadows, m_DynamicOccludee, m_StaticShadowCaster,
            // m_MotionVectors, m_LightProbeUsage, m_ReflectionProbeUsage, m_RayTracingMode, m_RayTraceProcedural
            bw.Write((byte)(enabled ? 1 : 0));
            bw.Write((byte)1);
            bw.Write((byte)1);
            bw.Write((byte)1);
            bw.Write((byte)0);
            bw.Write((byte)1);
            bw.Write((byte)1);
            bw.Write((byte)1);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write(new byte[2]); // align4

            bw.Write(1u); // m_RenderingLayerMask
            bw.Write(0);  // m_RendererPriority
            bw.Write((ushort)0xFFFF); // m_LightmapIndex
            bw.Write((ushort)0xFFFF); // m_LightmapIndexDynamic
            for (int i = 0; i < 8; i++) bw.Write(0f); // m_LightmapTilingOffset + Dynamic (32 bytes)

            // m_Materials
            bw.Write(materials.Length);
            foreach (var (fileId, pathId) in materials)
            {
                bw.Write(fileId);
                bw.Write(pathId);
            }

            // m_StaticBatchInfo + m_StaticBatchRoot
            bw.Write(firstSubMesh);
            bw.Write(subMeshCount);
            bw.Write(0); bw.Write(0L);

            // m_ProbeAnchor + m_LightProbeVolumeOverride
            bw.Write(0); bw.Write(0L);
            bw.Write(0); bw.Write(0L);

            // m_SortingLayerID (4) + m_SortingLayer (2) + m_SortingOrder (2)
            bw.Write(0u);
            bw.Write((short)0);
            bw.Write((short)0);

            // MeshRenderer: m_AdditionalVertexStreams + m_EnlightenVertexStream
            bw.Write(0); bw.Write(additionalVertexStreamsPathId);
            bw.Write(0); bw.Write(enlightenVertexStreamPathId);

            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildStandardSkinnedMeshRendererBinaryUnity6000_3(
            long gameObjectPathId,
            bool enabled,
            (int FileId, long PathId)[] materials,
            ushort firstSubMesh,
            ushort subMeshCount,
            long meshPathId,
            (int FileId, long PathId)[] bones,
            float[] blendShapeWeights,
            long rootBonePathId,
            Vector3 center,
            Vector3 extent,
            bool dirtyAabb)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // Component.m_GameObject
            bw.Write(0);
            bw.Write(gameObjectPathId);

            // Renderer flags for Unity 6000.3 (13 bytes + 3 pad = 16 bytes):
            // m_Enabled, m_CastShadows, m_ReceiveShadows, m_DynamicOccludee, m_StaticShadowCaster,
            // m_MotionVectors, m_LightProbeUsage, m_ReflectionProbeUsage, m_RayTracingMode,
            // m_RayTraceProcedural, m_RayTracingAccelStructBuildFlagsOverride, m_RayTracingAccelStructBuildFlags,
            // m_SmallMeshCulling
            bw.Write((byte)(enabled ? 1 : 0));
            bw.Write(new byte[12]);
            bw.Write(new byte[3]); // align4

            // 6000.2+ m_ForceMeshLod (int16 + align4) + m_MeshLodSelectionBias (float)
            bw.Write((short)0);
            bw.Write(new byte[2]);
            bw.Write(0f);

            bw.Write(1u); // m_RenderingLayerMask
            bw.Write(0);  // m_RendererPriority
            bw.Write((ushort)0xFFFF);
            bw.Write((ushort)0xFFFF);
            for (int i = 0; i < 8; i++) bw.Write(0f); // m_LightmapTilingOffset + Dynamic

            bw.Write(materials.Length);
            foreach (var (fileId, pathId) in materials)
            {
                bw.Write(fileId);
                bw.Write(pathId);
            }

            bw.Write(firstSubMesh);
            bw.Write(subMeshCount);
            bw.Write(0); bw.Write(0L); // m_StaticBatchRoot
            bw.Write(0); bw.Write(0L); // m_ProbeAnchor
            bw.Write(0); bw.Write(0L); // m_LightProbeVolumeOverride

            bw.Write(0u);       // m_SortingLayerID
            bw.Write((short)0); // m_SortingLayer
            bw.Write((short)0); // m_SortingOrder
            bw.Write(0);        // 6000.3+ m_MaskInteraction

            // SkinnedMeshRenderer fields
            bw.Write(0); // m_Quality
            bw.Write((byte)0); // m_UpdateWhenOffscreen
            bw.Write((byte)0); // m_SkinNormals
            bw.Write(new byte[2]); // align4

            bw.Write(0); bw.Write(meshPathId);

            bw.Write(bones.Length);
            foreach (var (fileId, pathId) in bones)
            {
                bw.Write(fileId);
                bw.Write(pathId);
            }

            bw.Write(blendShapeWeights.Length);
            foreach (var w in blendShapeWeights)
            {
                bw.Write(w);
            }

            bw.Write(0); bw.Write(rootBonePathId);

            bw.Write(center.X); bw.Write(center.Y); bw.Write(center.Z);
            bw.Write(extent.X); bw.Write(extent.Y); bw.Write(extent.Z);
            bw.Write((byte)(dirtyAabb ? 1 : 0));
            bw.Write(new byte[3]); // align4

            bw.Flush();
            return ms.ToArray();
        }

        private static TypeTree BuildDriftedMeshRendererTypeTree()
        {
            var nodes = new List<TypeTreeNode>
            {
                Node(0, "MeshRenderer", "Base")
            };
            AddPPtrNodes(nodes, 1, "PPtr<GameObject>", "m_GameObject");
            nodes.Add(Node(1, "bool", "m_Enabled", metaFlag: 0x4000));
            nodes.Add(Node(1, "SInt32", "m_CustomFutureInt"));
            nodes.Add(Node(1, "UInt64", "m_CustomFutureFlags"));
            AddPPtrVectorNodes(nodes, 1, "PPtr<Material>", "m_Materials");
            nodes.Add(Node(1, "StaticBatchInfo", "m_StaticBatchInfo"));
            nodes.Add(Node(2, "UInt16", "firstSubMesh"));
            nodes.Add(Node(2, "UInt16", "subMeshCount"));
            AddPPtrNodes(nodes, 1, "PPtr<Mesh>", "m_AdditionalVertexStreams");
            AddPPtrNodes(nodes, 1, "PPtr<Mesh>", "m_EnlightenVertexStream");
            return new TypeTree { m_Nodes = nodes };
        }

        private static TypeTree BuildDriftedSkinnedMeshRendererTypeTree()
        {
            var nodes = new List<TypeTreeNode>
            {
                Node(0, "SkinnedMeshRenderer", "Base")
            };
            AddPPtrNodes(nodes, 1, "PPtr<GameObject>", "m_GameObject");
            nodes.Add(Node(1, "bool", "m_Enabled", metaFlag: 0x4000));
            nodes.Add(Node(1, "UInt32", "m_FutureHeaderField"));
            nodes.Add(Node(1, "float", "m_FutureWeightScale"));
            AddPPtrVectorNodes(nodes, 1, "PPtr<Material>", "m_Materials");
            nodes.Add(Node(1, "StaticBatchInfo", "m_StaticBatchInfo"));
            nodes.Add(Node(2, "UInt16", "firstSubMesh"));
            nodes.Add(Node(2, "UInt16", "subMeshCount"));
            AddPPtrNodes(nodes, 1, "PPtr<Mesh>", "m_Mesh");
            AddPPtrVectorNodes(nodes, 1, "PPtr<Transform>", "m_Bones");
            nodes.Add(Node(1, "vector", "m_BlendShapeWeights"));
            nodes.Add(Node(2, "Array", "Array"));
            nodes.Add(Node(3, "int", "size"));
            nodes.Add(Node(3, "float", "data"));
            AddPPtrNodes(nodes, 1, "PPtr<Transform>", "m_RootBone");
            nodes.Add(Node(1, "AABB", "m_AABB"));
            AddVector3Nodes(nodes, 2, "m_Center");
            AddVector3Nodes(nodes, 2, "m_Extent");
            nodes.Add(Node(1, "bool", "m_DirtyAABB", metaFlag: 0x4000));
            return new TypeTree { m_Nodes = nodes };
        }

        private static void AddPPtrNodes(List<TypeTreeNode> nodes, int level, string typeName, string fieldName)
        {
            nodes.Add(Node(level, typeName, fieldName));
            nodes.Add(Node(level + 1, "int", "m_FileID"));
            nodes.Add(Node(level + 1, "SInt64", "m_PathID"));
        }

        private static void AddPPtrVectorNodes(List<TypeTreeNode> nodes, int level, string elemType, string fieldName)
        {
            nodes.Add(Node(level, "vector", fieldName));
            nodes.Add(Node(level + 1, "Array", "Array"));
            nodes.Add(Node(level + 2, "int", "size"));
            AddPPtrNodes(nodes, level + 2, elemType, "data");
        }

        private static void AddVector3Nodes(List<TypeTreeNode> nodes, int level, string fieldName)
        {
            nodes.Add(Node(level, "Vector3f", fieldName));
            nodes.Add(Node(level + 1, "float", "x"));
            nodes.Add(Node(level + 1, "float", "y"));
            nodes.Add(Node(level + 1, "float", "z"));
        }

        private static TypeTreeNode Node(int level, string type, string name, int metaFlag = 0)
        {
            return new TypeTreeNode
            {
                m_Level = level,
                m_Type = type,
                m_Name = name,
                m_MetaFlag = metaFlag,
                m_ByteSize = -1
            };
        }

        private static SerializedFile CreateSyntheticSerializedFile(byte[] data, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = new AssetsManager();
            sf.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.LargeFilesSupport };
            sf.m_TargetPlatform = BuildTarget.StandaloneWindows64;
            sf.m_Externals = new List<FileIdentifier>();
            sf.m_Types = new List<SerializedType>();
            sf.m_Objects = new List<ObjectInfo>();
            sf.Objects = new List<Object>();
            sf.ObjectsDic = new Dictionary<long, Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            sf.fileName = "synthetic.assets";
            sf.fullName = "synthetic.assets";
            sf.originalPath = "synthetic.assets";
            sf.SetVersion(unityVersion);
            var ms = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
            sf.reader = new FileReader("synthetic.assets", ms);
            sf.reader.Endian = EndianType.LittleEndian;
            return sf;
        }

        private static ObjectReader CreateObjectReader(
            byte[] data,
            ClassIDType classId,
            string unityVersion,
            long pathId,
            TypeTree? typeTree,
            uint? declaredByteSize = null)
        {
            var sf = CreateSyntheticSerializedFile(data, unityVersion);
            SerializedType? serializedType = typeTree != null
                ? new SerializedType { classID = (int)classId, m_Type = typeTree }
                : null;
            var objInfo = new ObjectInfo
            {
                m_PathID = pathId,
                byteStart = 0,
                byteSize = declaredByteSize ?? (uint)data.Length,
                classID = (int)classId,
                serializedType = serializedType
            };
            sf.ObjectInfoDic[pathId] = objInfo;
            return new ObjectReader(sf.reader, sf, objInfo);
        }

        private static byte[] CreateFilledBytes(int length, byte value)
        {
            var bytes = new byte[length];
            Array.Fill(bytes, value);
            return bytes;
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
                ByteSize = 512
            };
        }

        private static long ExecuteScalarInt64(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
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
