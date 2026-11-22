using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using K4os.Compression.LZ4;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class ConnectionsAndGenericRigTests
    {
        // =========================================================================
        // 1. Step 26 C# bone-hash matching + Step 25 temp-table synthesis (zero loss, sub-second)
        // =========================================================================

        [Fact]
        public void SQLiteSemanticCommit_LargeRigBoneHashMatchingAndSharedControllerFanout_CompletesSubSecondWithZeroDataLoss()
        {
            var tempRoot = CreateTempDirectory("sqlite_fast_connections");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "SampleGame");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                File.WriteAllBytes(Path.Combine(projectDir, "level0"), new byte[32]);

                var scan = ProjectScanner.ScanFolder(projectDir);
                var cache = new SQLiteProjectIndexCache(cacheDir);
                var signature = cache.GetFolderSignature(scan);

                // Seed Assets so Assets joins and SourceFileId bonuses resolve accurately.
                var projectIndex = new ProjectIndex();
                const int rigCount = 120;
                const int clipCount = 80;
                const string sharedControllerId = "ctrl.assets#1";
                projectIndex.AddHandle(new AssetHandle
                {
                    UniqueID = sharedControllerId,
                    Name = "SharedController",
                    Type = ClassIDType.AnimatorController,
                    PathID = 1,
                    SerializedFileName = "ctrl.assets",
                    OriginalPath = Path.Combine(projectDir, "ctrl.assets")
                });

                for (int r = 0; r < rigCount; r++)
                {
                    projectIndex.AddHandle(new AssetHandle
                    {
                        UniqueID = $"rig_{r}.assets#100",
                        Name = $"CharacterMesh_{r}",
                        Type = ClassIDType.Mesh,
                        PathID = 100,
                        ByteSize = 500_000 + r,
                        SerializedFileName = $"rig_{r}.assets",
                        OriginalPath = Path.Combine(projectDir, $"rig_{r}.assets")
                    });
                    projectIndex.AddHandle(new AssetHandle
                    {
                        UniqueID = $"rig_{r}.assets#200",
                        Name = $"CharacterAnimator_{r}",
                        Type = ClassIDType.Animator,
                        PathID = 200,
                        SerializedFileName = $"rig_{r}.assets",
                        OriginalPath = Path.Combine(projectDir, $"rig_{r}.assets")
                    });
                }

                for (int c = 0; c < clipCount; c++)
                {
                    projectIndex.AddHandle(new AssetHandle
                    {
                        UniqueID = $"clip_{c}.assets#300",
                        Name = $"AnimClip_{c}",
                        Type = ClassIDType.AnimationClip,
                        PathID = 300,
                        SerializedFileName = $"clip_{c}.assets",
                        OriginalPath = Path.Combine(projectDir, $"clip_{c}.assets")
                    });
                }

                Assert.True(cache.SaveIndexCache(projectDir, signature, scan, "2022.3.40f1", projectIndex.GetHandles(), preserveSemanticRelations: false));

                // Stage semantic relations simulating Avatar-less rigs (RawAvatarId = '') + shared controller fanout.
                var relations = new SemanticAssetRelations();
                var commonHashes = Enumerable.Range(0, 12)
                    .Select(i => SemanticGraphService.ComputeUnityBonePathHash($"Root/Skeleton/Bone_{i}"))
                    .ToArray();
                var faceHashes = Enumerable.Range(0, 6)
                    .Select(i => SemanticGraphService.ComputeUnityBonePathHash($"Face/EyeRig/Joint_{i}"))
                    .ToArray();

                for (int r = 0; r < rigCount; r++)
                {
                    var groupId = $"modelgroup:animator:rig_{r}.assets#200";
                    var meshId = $"rig_{r}.assets#100";
                    var animId = $"rig_{r}.assets#200";
                    relations.ModelGroups.Add(new SemanticModelGroupRelation(
                        groupId, "Animator", $"Rig_{r}", string.Empty, $"Rig_{r}",
                        animId, string.Empty, r < 25 ? sharedControllerId : string.Empty,
                        $"rig_{r}.assets", 100, "Animator hierarchy descendant"));
                    relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                        groupId, meshId, $"rig_{r}.assets#150", "SkinnedMeshRenderer",
                        string.Empty, "Body", 0, null, 100, "Animator hierarchy descendant"));

                    foreach (var h in commonHashes)
                    {
                        relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                            string.Empty, animId, meshId, groupId, h));
                    }

                    if (r == 7)
                    {
                        foreach (var fh in faceHashes)
                        {
                            relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                                string.Empty, animId, meshId, groupId, fh));
                        }
                    }
                }

                // Shared controller links to 10 clips -> 25 groups * 10 clips = 250 direct rows (all must be preserved!).
                for (int c = 0; c < 10; c++)
                {
                    relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                        sharedControllerId, "AnimationClip", "m_AnimationClips", c,
                        $"clip_{c}.assets#300", 0, 1, 0, 300, true));
                }

                // Decoupled clips matched via bone hashes (including a 1-probe clip and a facial-rig clip).
                for (int c = 0; c < clipCount; c++)
                {
                    var clipId = $"clip_{c}.assets#300";
                    if (c == clipCount - 1)
                    {
                        foreach (var fh in faceHashes)
                        {
                            relations.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(clipId, fh, faceHashes.Length));
                        }
                    }
                    else if (c == clipCount - 2)
                    {
                        // 1-probe clip must still match cleanly (zero loss!).
                        relations.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(clipId, faceHashes[0], 1));
                    }
                    else
                    {
                        foreach (var h in commonHashes)
                        {
                            relations.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(clipId, h, commonHashes.Length));
                        }
                    }
                }

                var sw = Stopwatch.StartNew();
                using (var session = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    session.FlushBatch(relations, clearBatch: true);
                    Assert.True(session.Commit());
                }
                sw.Stop();

                Assert.True(sw.Elapsed.TotalSeconds < 10.0, $"Expected fast commit, took {sw.Elapsed.TotalSeconds:F2}s");

                // Verify facial-rig clip matched rig_7 specifically with its ModelGroupId preserved!
                var faceTargets = cache.LoadAnimationClipTargets(projectDir, signature, $"clip_{clipCount - 1}.assets#300");
                var faceMatch = Assert.Single(faceTargets);
                Assert.Equal("rig_7.assets#100", faceMatch.MeshAssetId);
                Assert.Equal("modelgroup:animator:rig_7.assets#200", faceMatch.ModelGroupId);
                Assert.Equal(faceHashes.Length, faceMatch.MatchedBoneCount);

                // Verify 1-probe clip was NOT dropped!
                var singleProbeTargets = cache.LoadAnimationClipTargets(projectDir, signature, $"clip_{clipCount - 2}.assets#300");
                Assert.Contains(singleProbeTargets, t => t.MeshAssetId == "rig_7.assets#100");

                // Verify shared controller synthesized all 25 groups for clip_0 without arbitrary cap loss!
                var sharedTargets = cache.LoadAnimationClipTargets(projectDir, signature, "clip_0.assets#300", maxResults: 100);
                var controllerTargetGroups = sharedTargets
                    .Where(t => t.RelationKind == "AnimatorController")
                    .Select(t => t.ModelGroupId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                Assert.Equal(25, controllerTargetGroups);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 2. GetLazyConnectionSourcePaths zero-loss filter & LoadFilesForConnectionsAsync
        // =========================================================================

        [Fact]
        public void SemanticGraphService_GetLazyConnectionSourcePaths_SkipsPureMediaLeafBundles_WhileKeepingMonoBehaviourMeshAndSemanticTypes()
        {
            var tempRoot = CreateTempDirectory("lazy_conn_filter");
            try
            {
                var texOnlyBundle = Path.Combine(tempRoot, "tex_only.bundle");
                var monoBundle = Path.Combine(tempRoot, "mono_script.bundle");
                var meshBundle = Path.Combine(tempRoot, "mesh_only.bundle");
                var matBundle = Path.Combine(tempRoot, "mat.bundle");
                File.WriteAllBytes(texOnlyBundle, new byte[16]);
                File.WriteAllBytes(monoBundle, new byte[16]);
                File.WriteAllBytes(meshBundle, new byte[16]);
                File.WriteAllBytes(matBundle, new byte[16]);

                var index = new ProjectIndex();
                index.AddHandle(new AssetHandle { UniqueID = "a#1", Type = ClassIDType.AssetBundle, OriginalPath = texOnlyBundle, SerializedFileName = "a" });
                index.AddHandle(new AssetHandle { UniqueID = "a#2", Type = ClassIDType.Texture2D, OriginalPath = texOnlyBundle, SerializedFileName = "a" });
                index.AddHandle(new AssetHandle { UniqueID = "a#3", Type = ClassIDType.AudioClip, OriginalPath = texOnlyBundle, SerializedFileName = "a" });

                index.AddHandle(new AssetHandle { UniqueID = "b#1", Type = ClassIDType.AssetBundle, OriginalPath = monoBundle, SerializedFileName = "b" });
                index.AddHandle(new AssetHandle { UniqueID = "b#2", Type = ClassIDType.MonoBehaviour, OriginalPath = monoBundle, SerializedFileName = "b" });

                index.AddHandle(new AssetHandle { UniqueID = "c#1", Type = ClassIDType.AssetBundle, OriginalPath = meshBundle, SerializedFileName = "c" });
                index.AddHandle(new AssetHandle { UniqueID = "c#2", Type = ClassIDType.Mesh, OriginalPath = meshBundle, SerializedFileName = "c" });

                index.AddHandle(new AssetHandle { UniqueID = "d#1", Type = ClassIDType.Material, OriginalPath = matBundle, SerializedFileName = "d" });

                var sources = SemanticGraphService.GetLazyConnectionSourcePaths(
                    index,
                    rememberHandleSourcePath: null,
                    resolveHandleSourcePath: h => h.OriginalPath);

                Assert.DoesNotContain(Path.GetFullPath(texOnlyBundle), sources, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(Path.GetFullPath(monoBundle), sources, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(Path.GetFullPath(meshBundle), sources, StringComparer.OrdinalIgnoreCase);
                Assert.Contains(Path.GetFullPath(matBundle), sources, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task AssetsManager_LoadFilesForConnectionsAsync_BypassesDiskCacheAndSkipsResSBlocks()
        {
            var tempRoot = CreateTempDirectory("conn_load_cab_only");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var sfBytes = BuildMinimalSerializedFileBytes("2022.3.40f1");
                var resSBytes = new byte[64 * 1024];
                for (int i = 0; i < resSBytes.Length; i++) resSBytes[i] = (byte)(i / 16);

                var bundlePath = Path.Combine(gameDir, "rig_test.bundle");
                File.WriteAllBytes(bundlePath, BuildUnityFsBundle(
                    new[] { ("CAB-rig01", sfBytes, 4u), ("CAB-rig01.resS", resSBytes, 0u) }));

                var cache = new DecompressedBundleCache(cacheDir, gameScopePath: gameDir);
                using var am = new AssetsManager
                {
                    LazyLoading = true,
                    DecompressionCache = cache
                };

                await am.LoadFilesForConnectionsAsync(bundlePath);

                Assert.Single(am.assetsFileList);
                Assert.Equal(0L, cache.CacheMisses);
                Assert.Equal(0, cache.GetStats().EntryCount);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 3. Active-part ModelGroup preview filtering (zero loss in DB)
        // =========================================================================

        [Fact]
        public void MainWindow_FilterActivePreviewModelGroupParts_HidesInactivePropsByDefault_WhileShowingExplicitlySelectedInactivePart()
        {
            var parts = new List<ModelGroupMeshInfo>
            {
                new("grp1", "f#1", "f#10", "SkinnedMeshRenderer", "f#100", "Body", 0, 100, "Animator hierarchy descendant", null, "Hero_Combined", 1, 2_000_000, (int)ClassIDType.Mesh),
                new("grp1", "f#2", "f#11", "SkinnedMeshRenderer", "f#101", "Jacket", 1, 100, "Animator hierarchy descendant", null, "Jacket_FR", 2, 500_000, (int)ClassIDType.Mesh),
                new("grp1", "f#3", "f#12", "SkinnedMeshRenderer", "f#102", "Tears_L", 2, 40, "Animator hierarchy descendant (inactive)", null, "Tears_L", 3, 12_000, (int)ClassIDType.Mesh),
                new("grp1", "f#4", "f#13", "SkinnedMeshRenderer", "f#103", "Sweat_Drop", 3, 40, string.Empty, null, "Sweat_Drop", 4, 8_000, (int)ClassIDType.Mesh)
            };

            // Selecting active mesh -> hides inactive tear/sweat parts cleanly (both explicit reason and CompositeAsset confidence<=40).
            var filteredForBody = MainWindow.FilterActivePreviewModelGroupParts(parts, selectedMeshId: "f#1");
            Assert.Equal(2, filteredForBody.Count);
            Assert.Contains(filteredForBody, p => p.MeshAssetId == "f#1");
            Assert.Contains(filteredForBody, p => p.MeshAssetId == "f#2");

            // Explicitly selecting an inactive part -> keeps it visible!
            var filteredForTears = MainWindow.FilterActivePreviewModelGroupParts(parts, selectedMeshId: "f#3");
            Assert.Equal(3, filteredForTears.Count);
            Assert.Contains(filteredForTears, p => p.MeshAssetId == "f#3");
        }

        // =========================================================================
        // 4. Generic Transform-hierarchy skeleton builder (zeroed m_BoneNameHashes + nested Face Animator)
        // =========================================================================

        [Fact]
        public void MeshPreviewSkeletonBuilder_TryBuildSkeletonFromTransformHierarchy_ResolvesBodyAndNestedFaceAnimatorClipPaths()
        {
            var sf = CreateMinimalSerializedFile("2022.3.40f1");

            // Build hierarchy:
            // HeroRoot (Body Animator) -> SklRoot -> Hips -> Spine -> Head (Nested Face Animator) -> FaceRoot -> LeftEye
            var (goRoot, trRoot) = AddTransformNode(sf, goId: 10, trId: 100, name: "HeroRoot", parent: null, new Vector3(0, 0, 0));
            var (_, trSkl) = AddTransformNode(sf, goId: 11, trId: 101, name: "SklRoot", parent: trRoot, new Vector3(0, 0.1f, 0));
            var (_, trHips) = AddTransformNode(sf, goId: 12, trId: 102, name: "Hips", parent: trSkl, new Vector3(0, 0.9f, 0));
            var (_, trSpine) = AddTransformNode(sf, goId: 13, trId: 103, name: "Spine", parent: trHips, new Vector3(0, 0.3f, 0));
            var (_, trHead) = AddTransformNode(sf, goId: 14, trId: 104, name: "Head", parent: trSpine, new Vector3(0, 0.4f, 0));
            var (_, trFaceRoot) = AddTransformNode(sf, goId: 15, trId: 105, name: "FaceRoot", parent: trHead, new Vector3(0, 0.05f, 0.1f));
            var (_, trLeftEye) = AddTransformNode(sf, goId: 16, trId: 106, name: "LeftEye", parent: trFaceRoot, new Vector3(-0.03f, 0.02f, 0.05f));

            var requiredBones = new List<Transform> { trHips, trSpine, trHead, trLeftEye };
            var animatorRoots = new List<Transform> { trRoot, trHead };

            // 1) Body clip: bindings relative to HeroRoot (including intermediate non-SMR node "SklRoot"!)
            var bodyClipHashes = new HashSet<uint>
            {
                SemanticGraphService.ComputeUnityBonePathHash("SklRoot"),
                SemanticGraphService.ComputeUnityBonePathHash("SklRoot/Hips"),
                SemanticGraphService.ComputeUnityBonePathHash("SklRoot/Hips/Spine"),
                SemanticGraphService.ComputeUnityBonePathHash("SklRoot/Hips/Spine/Head")
            };

            Assert.True(MeshPreviewSkeletonBuilder.TryBuildSkeletonFromTransformHierarchy(
                sf,
                trRoot,
                requiredBones,
                animatorRoots,
                bodyClipHashes,
                out var bodySkel,
                out var bodyPathIds));

            Assert.Equal(7, bodySkel.NodeCount);
            foreach (var h in bodyClipHashes)
            {
                Assert.True(bodySkel.DirectBindingPathToNodeIdx.ContainsKey(h), $"Missing body hash {h}");
            }
            // Verify unbroken parent chain: LeftEye -> FaceRoot -> Head -> Spine -> Hips -> SklRoot -> HeroRoot
            Assert.Equal(bodyPathIds[trFaceRoot.m_PathID], bodySkel.ParentIndices[bodyPathIds[trLeftEye.m_PathID]]);
            Assert.Equal(bodyPathIds[trSkl.m_PathID], bodySkel.ParentIndices[bodyPathIds[trHips.m_PathID]]);

            // 2) Nested Face clip: bindings relative to Head ("FaceRoot", "FaceRoot/LeftEye")!
            var faceClipHashes = new HashSet<uint>
            {
                SemanticGraphService.ComputeUnityBonePathHash("FaceRoot"),
                SemanticGraphService.ComputeUnityBonePathHash("FaceRoot/LeftEye")
            };

            Assert.True(MeshPreviewSkeletonBuilder.TryBuildSkeletonFromTransformHierarchy(
                sf,
                trRoot,
                requiredBones,
                animatorRoots,
                faceClipHashes,
                out var faceSkel,
                out var facePathIds));

            Assert.Equal(7, faceSkel.NodeCount);
            foreach (var h in faceClipHashes)
            {
                Assert.True(faceSkel.DirectBindingPathToNodeIdx.ContainsKey(h), $"Missing nested face hash {h}");
            }
            Assert.Equal(facePathIds[trLeftEye.m_PathID], faceSkel.DirectBindingPathToNodeIdx[SemanticGraphService.ComputeUnityBonePathHash("FaceRoot/LeftEye")]);
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static (GameObject Go, Transform Tr) AddTransformNode(
            SerializedFile sf,
            long goId,
            long trId,
            string name,
            Transform? parent,
            Vector3 localPos)
        {
            var go = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            go.assetsFile = sf;
            go.m_PathID = goId;
            go.type = ClassIDType.GameObject;
            go.m_Name = name;
            go.m_IsActive = true;

            var tr = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            tr.assetsFile = sf;
            tr.m_PathID = trId;
            tr.type = ClassIDType.Transform;
            tr.m_GameObject = new PPtr<GameObject>(0, goId, sf);
            tr.m_LocalPosition = localPos;
            tr.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            tr.m_LocalScale = new Vector3(1, 1, 1);
            tr.m_Father = parent != null ? new PPtr<Transform>(0, parent.m_PathID, sf) : new PPtr<Transform>(0, 0, sf);
            tr.m_Children = Array.Empty<PPtr<Transform>>();

            go.m_Components = new[] { new PPtr<Component>(0, trId, sf) };
            sf.AddObject(go);
            sf.AddObject(tr);

            if (parent != null)
            {
                var existing = parent.m_Children?.ToList() ?? new List<PPtr<Transform>>();
                existing.Add(new PPtr<Transform>(0, trId, sf));
                parent.m_Children = existing.ToArray();
            }

            return (go, tr);
        }

        private static SerializedFile CreateMinimalSerializedFile(string unityVersion)
        {
            var bytes = BuildMinimalSerializedFileBytes(unityVersion);
            var ms = new MemoryStream(bytes);
            var reader = new FileReader("synthetic.assets", ms);
            return new SerializedFile(reader, new AssetsManager());
        }

        private static byte[] BuildMinimalSerializedFileBytes(string unityVersion)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes(unityVersion));
                metaBw.Write((byte)0);
                metaBw.Write((int)BuildTarget.StandaloneWindows64);
                metaBw.Write(false);
                metaBw.Write(0);
                metaBw.Write(0);
                metaBw.Write(0);
                metaBw.Write(0);
                metaBw.Write((byte)0);
                metaBw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            uint totalSize = (uint)(headerSize + metadata.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), totalSize);
            fullBytes[16] = 0;
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            return fullBytes;
        }

        private static byte[] BuildUnityFsBundle((string Path, byte[] Payload, uint Flags)[] nodes)
        {
            using var combined = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Length);
            foreach (var n in nodes)
            {
                long off = combined.Position;
                combined.Write(n.Payload, 0, n.Payload.Length);
                dirEntries.Add((off, n.Payload.Length, n.Flags, n.Path));
            }
            var raw = combined.ToArray();

            const int splitBlockSize = 4096;
            var blocks = new List<(uint uncomp, uint comp, StorageBlockFlags flags)>();
            using var compStream = new MemoryStream();
            int cursor = 0;
            while (cursor < raw.Length || blocks.Count == 0)
            {
                int chunkLen = Math.Min(splitBlockSize, raw.Length - cursor);
                var slice = raw.AsSpan(cursor, chunkLen);
                var dest = new byte[LZ4Codec.MaximumOutputSize(chunkLen)];
                int encoded = LZ4Codec.Encode(slice, dest, LZ4Level.L09_HC);
                if (encoded > 0 && encoded < chunkLen)
                {
                    compStream.Write(dest, 0, encoded);
                    blocks.Add(((uint)chunkLen, (uint)encoded, (StorageBlockFlags)CompressionType.Lz4HC));
                }
                else
                {
                    compStream.Write(slice);
                    blocks.Add(((uint)chunkLen, (uint)chunkLen, (StorageBlockFlags)CompressionType.None));
                }
                cursor += chunkLen;
            }
            var compData = compStream.ToArray();

            byte[] biUncomp;
            using (var biMs = new MemoryStream())
            {
                biMs.Write(new byte[16]);
                WriteInt32BE(biMs, blocks.Count);
                foreach (var b in blocks)
                {
                    WriteUInt32BE(biMs, b.uncomp);
                    WriteUInt32BE(biMs, b.comp);
                    WriteUInt16BE(biMs, (ushort)b.flags);
                }
                WriteInt32BE(biMs, dirEntries.Count);
                foreach (var d in dirEntries)
                {
                    WriteInt64BE(biMs, d.offset);
                    WriteInt64BE(biMs, d.size);
                    WriteUInt32BE(biMs, d.flags);
                    biMs.Write(Encoding.UTF8.GetBytes(d.path));
                    biMs.WriteByte(0);
                }
                biUncomp = biMs.ToArray();
            }

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            WriteUInt32BE(ms, 6u);
            ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            ms.Write(Encoding.ASCII.GetBytes("2022.3.40f1\0"));
            var sizePos = ms.Position;
            WriteInt64BE(ms, 0L);
            WriteUInt32BE(ms, (uint)biUncomp.Length);
            WriteUInt32BE(ms, (uint)biUncomp.Length);
            WriteUInt32BE(ms, (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined);
            ms.Write(biUncomp);
            ms.Write(compData);
            var res = ms.ToArray();
            BinaryPrimitives.WriteInt64BigEndian(res.AsSpan((int)sizePos, 8), res.Length);
            return res;
        }

        private static void WriteUInt16BE(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
        private static void WriteInt32BE(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
        private static void WriteUInt32BE(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
        private static void WriteInt64BE(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_ConnRig_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
