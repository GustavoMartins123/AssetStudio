using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class AnimationRelationAndAsyncPreviewTests
    {
        private readonly ITestOutputHelper _output;

        public AnimationRelationAndAsyncPreviewTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void DirectRelations_ControllerOverrideAndLegacyAnimation_PersistViaStagingV14_AndQueryUnder5msWithZeroScans()
        {
            var tempRoot = CreateTempDirectory("anim_v14_direct_relations");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                using var _cache = cache;

                var bundleA = sourcePath;
                var bundleB = Path.Combine(projectDir, "controllers.bundle");
                var bundleC = Path.Combine(projectDir, "clips.bundle");
                File.WriteAllBytes(bundleB, new byte[64]);
                File.WriteAllBytes(bundleC, new byte[64]);
                scanResult = ProjectScanner.ScanFolder(projectDir);
                signature = cache.GetFolderSignature(scanResult);

                var animatorId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 101);
                var avatarId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 102);
                var meshBodyId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 103);
                var meshWeaponId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 104);
                var rendererBodyId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 105);
                var rendererWeaponId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 106);
                var rootGoId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 107);
                var groupId = $"modelgroup:animator:{animatorId}";

                var baseControllerId = AssetHandle.BuildUniqueID("ctrl.assets", bundleB, 201);
                var overrideControllerId = AssetHandle.BuildUniqueID("ctrl.assets", bundleB, 202);
                var legacyAnimCompId = AssetHandle.BuildUniqueID("rig.assets", bundleA, 203);

                var clipDirectId = AssetHandle.BuildUniqueID("clips.assets", bundleC, 301);
                var clipOverrideId = AssetHandle.BuildUniqueID("clips.assets", bundleC, 302);
                var clipLegacyId = AssetHandle.BuildUniqueID("clips.assets", bundleC, 303);

                var handles = new List<AssetHandle>
                {
                    CreateHandle(animatorId, "HeroAnimator", ClassIDType.Animator, "rig.assets", bundleA, 101),
                    CreateHandle(avatarId, "HeroAvatar", ClassIDType.Avatar, "rig.assets", bundleA, 102),
                    CreateHandle(meshBodyId, "HeroBodyMesh", ClassIDType.Mesh, "rig.assets", bundleA, 103, byteSize: 65536),
                    CreateHandle(meshWeaponId, "HeroSwordMesh", ClassIDType.Mesh, "rig.assets", bundleA, 104, byteSize: 16384),
                    CreateHandle(rendererBodyId, "HeroBodySMR", ClassIDType.SkinnedMeshRenderer, "rig.assets", bundleA, 105),
                    CreateHandle(rendererWeaponId, "HeroSwordSMR", ClassIDType.SkinnedMeshRenderer, "rig.assets", bundleA, 106),
                    CreateHandle(baseControllerId, "HeroBaseController", ClassIDType.AnimatorController, "ctrl.assets", bundleB, 201),
                    CreateHandle(overrideControllerId, "HeroEliteOverride", ClassIDType.AnimatorOverrideController, "ctrl.assets", bundleB, 202),
                    CreateHandle(clipDirectId, "Hero_Idle", ClassIDType.AnimationClip, "clips.assets", bundleC, 301),
                    CreateHandle(clipOverrideId, "Hero_EliteAttack", ClassIDType.AnimationClip, "clips.assets", bundleC, 302),
                    CreateHandle(clipLegacyId, "Hero_LegacyIntro", ClassIDType.AnimationClip, "clips.assets", bundleC, 303)
                };

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                // Batch 1 (Bundle A): Rig (Animator + Avatar + Meshes + ModelGroup + Legacy Animation)
                var batch1 = new SemanticAssetRelations();
                batch1.SourceFiles.Add(new SemanticSourceFileEntry("rig.assets", bundleA, "2022.3.10f1", 8));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(animatorId, "Avatar", "m_Avatar", 0, avatarId, 0, 101, 0, 102, isResolved: true));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(animatorId, "AnimatorController", "m_Controller", 0, overrideControllerId, 0, 101, 0, 202, isResolved: true));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(animatorId, "Mesh", "AnimatorMesh", 0, meshBodyId, 0, 101, 0, 103, isResolved: true));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(avatarId, "Mesh", "AnimatorMesh", 0, meshBodyId, 0, 102, 0, 103, isResolved: true));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(avatarId, "Mesh", "AnimatorMesh", 1, meshWeaponId, 0, 102, 0, 104, isResolved: true));
                batch1.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", "HeroModel", rootGoId, "HeroRoot", animatorId, avatarId, overrideControllerId, "rig.assets", 100, "Animator hierarchy descendant"));
                batch1.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshBodyId, rendererBodyId, "SkinnedMeshRenderer", rootGoId, "HeroBody", 0, null, 100, "Animator hierarchy descendant"));
                batch1.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshWeaponId, rendererWeaponId, "SkinnedMeshRenderer", rootGoId, "HeroSword", 1, null, 100, "Animator hierarchy descendant"));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(legacyAnimCompId, "Mesh", "AnimatorMesh", 0, meshBodyId, 0, 203, 0, 103, isResolved: true));
                batch1.AssetEdges.Add(new SemanticAssetEdgeRelation(legacyAnimCompId, "AnimationClip", "LegacyAnimationClip", 0, clipLegacyId, 0, 203, 0, 303, isResolved: true));
                batch1.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                    clipLegacyId, legacyAnimCompId, avatarId, meshBodyId, groupId, "LegacyAnimation", 9700));

                // Batch 2 (Bundle B): Base AnimatorController & OverrideController referencing clips in Bundle C
                var batch2 = new SemanticAssetRelations();
                batch2.SourceFiles.Add(new SemanticSourceFileEntry("ctrl.assets", bundleB, "2022.3.10f1", 2));
                batch2.AssetEdges.Add(new SemanticAssetEdgeRelation(overrideControllerId, "AnimatorController", "m_Controller", 0, baseControllerId, 0, 202, 0, 201, isResolved: true));
                batch2.AssetEdges.Add(new SemanticAssetEdgeRelation(baseControllerId, "AnimationClip", "m_AnimationClips", 0, clipDirectId, 0, 201, 0, 301, isResolved: true));
                batch2.AssetEdges.Add(new SemanticAssetEdgeRelation(overrideControllerId, "AnimationClip", "m_AnimationClips", 0, clipOverrideId, 0, 202, 0, 302, isResolved: true));

                using (var session = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    session.FlushBatch(batch1, clearBatch: true);
                    session.FlushBatch(batch2, clearBatch: true);
                    Assert.True(session.Commit());
                }

                var sw = Stopwatch.StartNew();
                var directTargets = cache.LoadAnimationClipTargets(projectDir, signature, clipDirectId);
                var overrideTargets = cache.LoadAnimationClipTargets(projectDir, signature, clipOverrideId);
                var legacyTargets = cache.LoadAnimationClipTargets(projectDir, signature, clipLegacyId);
                sw.Stop();

                Assert.True(sw.Elapsed.TotalMilliseconds < 5.0, $"Expected < 5 ms for 3 direct relation queries, actual {sw.Elapsed.TotalMilliseconds:0.###} ms.");

                var topDirect = Assert.Single(directTargets);
                Assert.Equal(animatorId, topDirect.AnimatorAssetId);
                Assert.Equal(avatarId, topDirect.AvatarAssetId);
                Assert.Equal(meshBodyId, topDirect.MeshAssetId);
                Assert.Equal(groupId, topDirect.ModelGroupId);
                Assert.Equal("HeroAvatar", topDirect.AvatarName);
                Assert.Equal("HeroBodyMesh", topDirect.MeshName);
                Assert.Equal("HeroModel", topDirect.ModelGroupName);

                var topOverride = Assert.Single(overrideTargets);
                Assert.Equal(animatorId, topOverride.AnimatorAssetId);
                Assert.Equal(avatarId, topOverride.AvatarAssetId);
                Assert.Equal(meshBodyId, topOverride.MeshAssetId);
                Assert.Equal(groupId, topOverride.ModelGroupId);
                Assert.True(topOverride.MatchScore >= 9800);

                Assert.NotEmpty(legacyTargets);
                var topLegacy = legacyTargets[0];
                Assert.Equal("LegacyAnimation", topLegacy.RelationKind);
                Assert.Equal(avatarId, topLegacy.AvatarAssetId);
                Assert.Equal(meshBodyId, topLegacy.MeshAssetId);
                Assert.Equal(groupId, topLegacy.ModelGroupId);

                Assert.Contains(avatarId, cache.LoadAnimationClipAvatarAssetIds(projectDir, signature, clipDirectId));
                Assert.Contains(meshBodyId, cache.LoadAnimationClipMeshAssetIds(projectDir, signature, clipDirectId));

                using var conn = cache.CreateReadConnection(projectDir);
                Assert.True(cache.TryGetCachedProjectId(projectDir, signature, out var projectId));
                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadAnimationClipTargets",
                    @"
                    SELECT
                        t.ClipAssetUniqueID,
                        t.AnimatorAssetUniqueID,
                        t.AvatarAssetUniqueID,
                        t.MeshAssetUniqueID,
                        t.ModelGroupId,
                        t.RelationKind,
                        t.MatchScore
                    FROM AnimationClipTargets t
                    LEFT JOIN Assets avatarAsset
                      ON avatarAsset.ProjectId = t.ProjectId
                     AND t.AvatarAssetUniqueID <> ''
                     AND avatarAsset.UniqueID = t.AvatarAssetUniqueID
                    LEFT JOIN Assets meshAsset
                      ON meshAsset.ProjectId = t.ProjectId
                     AND t.MeshAssetUniqueID <> ''
                     AND meshAsset.UniqueID = t.MeshAssetUniqueID
                    LEFT JOIN ModelGroups mg
                      ON mg.ProjectId = t.ProjectId
                     AND t.ModelGroupId <> ''
                     AND mg.GroupAssetUniqueID = t.ModelGroupId
                    WHERE t.ProjectId = @projectId
                      AND t.ClipAssetUniqueID = @clipAssetId
                    ORDER BY t.MatchScore DESC
                    LIMIT 32",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@clipAssetId", clipDirectId);
                    });
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void IndirectBoneHashMatching_DecoupledAddressableClip_RanksExactRigFirstAcrossCompetingSkeletons()
        {
            var tempRoot = CreateTempDirectory("anim_v14_bone_hash_matching");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                using var _cache = cache;

                var rig1AvatarId = AssetHandle.BuildUniqueID("char_sister.bundle", sourcePath, 1001);
                var rig1MeshId = AssetHandle.BuildUniqueID("char_sister.bundle", sourcePath, 1002);
                var rig1GroupId = $"modelgroup:avatar:{rig1AvatarId}";

                var rig2AvatarId = AssetHandle.BuildUniqueID("char_boss.bundle", sourcePath, 2001);
                var rig2MeshId = AssetHandle.BuildUniqueID("char_boss.bundle", sourcePath, 2002);
                var rig2GroupId = $"modelgroup:avatar:{rig2AvatarId}";

                var rig3AvatarId = AssetHandle.BuildUniqueID("char_dog.bundle", sourcePath, 3001);
                var rig3MeshId = AssetHandle.BuildUniqueID("char_dog.bundle", sourcePath, 3002);
                var rig3GroupId = $"modelgroup:avatar:{rig3AvatarId}";

                var decoupledClipId = AssetHandle.BuildUniqueID("anim_sister_slash.bundle", sourcePath, 9001);

                var handles = new List<AssetHandle>
                {
                    CreateHandle(rig1AvatarId, "SisterAvatar", ClassIDType.Avatar, "char_sister.bundle", sourcePath, 1001),
                    CreateHandle(rig1MeshId, "SisterBodyMesh", ClassIDType.Mesh, "char_sister.bundle", sourcePath, 1002, byteSize: 120000),
                    CreateHandle(rig2AvatarId, "BossAvatar", ClassIDType.Avatar, "char_boss.bundle", sourcePath, 2001),
                    CreateHandle(rig2MeshId, "BossBodyMesh", ClassIDType.Mesh, "char_boss.bundle", sourcePath, 2002, byteSize: 150000),
                    CreateHandle(rig3AvatarId, "HoundAvatar", ClassIDType.Avatar, "char_dog.bundle", sourcePath, 3001),
                    CreateHandle(rig3MeshId, "HoundBodyMesh", ClassIDType.Mesh, "char_dog.bundle", sourcePath, 3002, byteSize: 80000),
                    CreateHandle(decoupledClipId, "Sister_Slash_Combo01", ClassIDType.AnimationClip, "anim_sister_slash.bundle", sourcePath, 9001)
                };

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                var sharedHumanoidBones = new[]
                {
                    "Root/Pelvis",
                    "Root/Pelvis/Spine_01",
                    "Root/Pelvis/Spine_01/Spine_02",
                    "Root/Pelvis/Spine_01/Spine_02/Neck/Head"
                };

                var sisterUniqueBones = new[]
                {
                    "Root/Pelvis/Spine_01/Spine_02/Clavicle_R/UpperArm_R/LowerArm_R/Hand_R/Scythe_Mount_R",
                    "Root/Pelvis/Spine_01/Spine_02/Scarf_01",
                    "Root/Pelvis/Spine_01/Spine_02/Scarf_02",
                    "Root/Pelvis/Spine_01/Spine_02/Scarf_03",
                    "Root/Pelvis/CoatTail_L_01",
                    "Root/Pelvis/CoatTail_L_02",
                    "Root/Pelvis/CoatTail_R_01",
                    "Root/Pelvis/CoatTail_R_02",
                    "Root/Pelvis/Thigh_L/Calf_L/Foot_L",
                    "Root/Pelvis/Thigh_R/Calf_R/Foot_R",
                    "Root/Pelvis/Spine_01/Spine_02/Clavicle_L/UpperArm_L/LowerArm_L/Hand_L",
                    "Root/Pelvis/Hair_Braid_01"
                };

                var bossUniqueBones = new[]
                {
                    "Root/Pelvis/Spine_01/Spine_02/Wing_L_01",
                    "Root/Pelvis/Spine_01/Spine_02/Wing_L_02",
                    "Root/Pelvis/Spine_01/Spine_02/Wing_R_01",
                    "Root/Pelvis/Spine_01/Spine_02/Wing_R_02",
                    "Root/Pelvis/Tail_01",
                    "Root/Pelvis/Tail_02",
                    "Root/Pelvis/Horn_L",
                    "Root/Pelvis/Horn_R"
                };

                var dogBones = new[]
                {
                    "QuadRoot/Hips/Spine/FrontLeg_L",
                    "QuadRoot/Hips/Spine/FrontLeg_R",
                    "QuadRoot/Hips/BackLeg_L",
                    "QuadRoot/Hips/BackLeg_R",
                    "QuadRoot/Hips/Tail_01"
                };

                // Batch 1: Rigs only
                var rigBatch = new SemanticAssetRelations();
                rigBatch.ModelGroups.Add(new SemanticModelGroupRelation(rig1GroupId, "Animator", "SisterRig", "", "SisterRoot", "", rig1AvatarId, "", "char_sister.bundle", 95, "Avatar rig"));
                rigBatch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(rig1GroupId, rig1MeshId, "smr1", "SkinnedMeshRenderer", "", "SisterBody", 0, null, 95, "Avatar rig"));
                rigBatch.ModelGroups.Add(new SemanticModelGroupRelation(rig2GroupId, "Animator", "BossRig", "", "BossRoot", "", rig2AvatarId, "", "char_boss.bundle", 95, "Avatar rig"));
                rigBatch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(rig2GroupId, rig2MeshId, "smr2", "SkinnedMeshRenderer", "", "BossBody", 0, null, 95, "Avatar rig"));
                rigBatch.ModelGroups.Add(new SemanticModelGroupRelation(rig3GroupId, "Animator", "HoundRig", "", "HoundRoot", "", rig3AvatarId, "", "char_dog.bundle", 95, "Avatar rig"));
                rigBatch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(rig3GroupId, rig3MeshId, "smr3", "SkinnedMeshRenderer", "", "HoundBody", 0, null, 95, "Avatar rig"));

                foreach (var path in sharedHumanoidBones.Concat(sisterUniqueBones))
                {
                    rigBatch.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                        rig1AvatarId, string.Empty, rig1MeshId, rig1GroupId, MainWindow.ComputeUnityBonePathHash(path)));
                }

                foreach (var path in sharedHumanoidBones.Concat(bossUniqueBones))
                {
                    rigBatch.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                        rig2AvatarId, string.Empty, rig2MeshId, rig2GroupId, MainWindow.ComputeUnityBonePathHash(path)));
                }

                foreach (var path in dogBones)
                {
                    rigBatch.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                        rig3AvatarId, string.Empty, rig3MeshId, rig3GroupId, MainWindow.ComputeUnityBonePathHash(path)));
                }

                // Batch 2: Decoupled AnimationClip in a separate bundle with 0 AnimatorController edges
                var clipBatch = new SemanticAssetRelations();
                var clipPaths = sharedHumanoidBones.Concat(sisterUniqueBones).ToArray();
                var probes = MainWindow.SelectDeterministicBoneHashProbes(
                    clipPaths.Select(MainWindow.ComputeUnityBonePathHash).ToArray(),
                    MainWindow.MaxClipBoneHashProbes);
                foreach (var probe in probes)
                {
                    clipBatch.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(decoupledClipId, probe, clipPaths.Length));
                }

                using (var session = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    session.FlushBatch(rigBatch, clearBatch: true);
                    session.FlushBatch(clipBatch, clearBatch: true);
                    Assert.True(session.Commit());
                }

                var targets = cache.LoadAnimationClipTargets(projectDir, signature, decoupledClipId);
                Assert.NotEmpty(targets);

                // SisterRig (16/16 matched bones) must rank #1!
                var rank1 = targets[0];
                Assert.Equal(rig1AvatarId, rank1.AvatarAssetId);
                Assert.Equal(rig1MeshId, rank1.MeshAssetId);
                Assert.Equal(rig1GroupId, rank1.ModelGroupId);
                Assert.Equal("BoneHashMatch", rank1.RelationKind);
                Assert.Equal(16, rank1.MatchedBoneCount);
                Assert.Equal(16, rank1.TotalClipBoneCount);

                // HoundRig (0/16 overlap) must be rejected completely
                Assert.DoesNotContain(targets, t => t.AvatarAssetId == rig3AvatarId);

                // If BossRig (4/16 overlap) met minimum threshold, its score must be strictly lower than SisterRig
                foreach (var other in targets.Skip(1))
                {
                    Assert.True(rank1.MatchScore > other.MatchScore, $"Expected rank1 score ({rank1.MatchScore}) > other score ({other.MatchScore})");
                }

                // CompatibleAvatar synthesis into AssetEdges must also make LoadAnimationClipAvatarAssetIds and LoadAnimationClipMeshAssetIds return SisterRig first
                var avatarIds = cache.LoadAnimationClipAvatarAssetIds(projectDir, signature, decoupledClipId);
                Assert.NotEmpty(avatarIds);
                Assert.Equal(rig1AvatarId, avatarIds[0]);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SchemaV13ToV14Upgrade_PreservesProjectsAssetsAndPreviewCache_AndAddsAnimationClipTargetsTable()
        {
            var tempRoot = CreateTempDirectory("sqlite_v13_to_v14_upgrade");
            try
            {
                var (bootstrapCache, projectDir, signature, _, sourcePath) = CreateSampleProject(tempRoot);
                var dbPath = bootstrapCache.GetDbPath(projectDir);
                bootstrapCache.Dispose();

                var meshId = AssetHandle.BuildUniqueID("level0", sourcePath, 100);
                var clipId = AssetHandle.BuildUniqueID("level0", sourcePath, 200);
                CreateLegacyV13Database(dbPath, projectDir, signature, sourcePath, meshId, clipId);
                SqliteConnection.ClearAllPools();

                using var upgradedCache = new SQLiteProjectIndexCache(Path.GetDirectoryName(dbPath)!);
                var loadedHandles = upgradedCache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(loadedHandles);
                Assert.Equal(2, loadedHandles!.Count);
                Assert.Contains(loadedHandles, h => h.UniqueID == meshId && h.Name == "HeroMesh");
                Assert.Contains(loadedHandles, h => h.UniqueID == clipId && h.Name == "HeroRunClip");

                var preservedPreview = upgradedCache.LoadPreviewCacheEntry(projectDir, signature, "preserved_preview_v13", "png", 1, "256");
                Assert.NotNull(preservedPreview);
                Assert.Equal("hash_v13_thumb", preservedPreview!.PayloadHash);

                var indexingState = upgradedCache.LoadIndexingState(projectDir, signature);
                Assert.NotNull(indexingState);
                Assert.Equal("completed", indexingState!.Status);

                using var conn = upgradedCache.CreateReadConnection(projectDir);
                using (var verCmd = conn.CreateCommand())
                {
                    verCmd.CommandText = "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;";
                    Assert.Equal("16", verCmd.ExecuteScalar()?.ToString());
                }

                using (var ddlCmd = conn.CreateCommand())
                {
                    ddlCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'AnimationClipTargets' LIMIT 1;";
                    var ddl = ddlCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                    Assert.Contains("WITHOUT ROWID", ddl, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task AsyncPreviewCoordinator_RapidConcurrentSwitching_CancelsStaleRequestsAndLatestWinsWithoutDeadlock()
        {
            using var coordinator = new AnimationPreviewLoadCoordinator();
            var appliedRequests = new ConcurrentQueue<(long RequestId, string ClipName)>();
            var startedCount = 0;

            const int totalRequests = 12;
            var tasks = new Task<(bool Applied, long RequestId, string? Result)>[totalRequests];

            for (var i = 0; i < totalRequests; i++)
            {
                var clipIndex = i;
                var isLast = i == totalRequests - 1;
                tasks[i] = coordinator.RunLatestAsync<string>(
                    async (reqId, ct) =>
                    {
                        Interlocked.Increment(ref startedCount);
                        if (!isLast)
                        {
                            // Stale requests simulate heavy bundle/curve work and wait until cancelled by the next click
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(15, ct).ConfigureAwait(false);
                        }

                        ct.ThrowIfCancellationRequested();
                        return $"Clip_{clipIndex:D2}";
                    },
                    (reqId, clipName) =>
                    {
                        appliedRequests.Enqueue((reqId, clipName));
                    });

                if (!isLast)
                {
                    await Task.Delay(5);
                }
            }

            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(startedCount >= 1);
            Assert.True(coordinator.CancelledRequestCount >= totalRequests - 1);

            var winning = Assert.Single(appliedRequests);
            Assert.Equal($"Clip_{totalRequests - 1:D2}", winning.ClipName);
            Assert.Equal(coordinator.CurrentRequestId, winning.RequestId);
            Assert.True(results[^1].Applied);
            Assert.All(results.Take(totalRequests - 1), r => Assert.False(r.Applied));
        }

        [Fact]
        public void SameRigAnimationSwitchCache_ReusesPreparedSkeletonAndReferenceMeshUnder5ms()
        {
            var rigCache = new AnimationRigPreviewCache(maxRigEntries: 4, maxClipEntries: 16);
            var (avatar, mesh) = CreateSyntheticAvatarAndSkinnedMesh(boneCount: 32, vertexCount: 240);
            var rigKey = "avatar:hero|mesh:hero_body";

            var buildCalls = 0;
            var firstRig = rigCache.GetOrAddRig(
                rigKey,
                () =>
                {
                    Interlocked.Increment(ref buildCalls);
                    return PreparedAnimationRigData.TryBuild(rigKey, avatar, mesh);
                },
                out var firstFromCache);

            Assert.NotNull(firstRig);
            Assert.False(firstFromCache);
            Assert.Equal(1, buildCalls);
            Assert.Equal(0, rigCache.HitCount);
            Assert.Equal(1, rigCache.MissCount);

            // Prime reference mesh geometry cache at 100% density
            var refMesh1 = firstRig!.GetOrBuildReferenceMesh(100f);
            Assert.NotNull(refMesh1);

            // Switch from Clip A to Clip B on the same character rig
            rigCache.RememberClipTarget("clip:idle", rigKey);
            rigCache.RememberClipTarget("clip:run", rigKey);

            var sw = Stopwatch.StartNew();
            Assert.True(rigCache.TryGetClipTarget("clip:run", out var resolvedRigKey));
            Assert.Equal(rigKey, resolvedRigKey);

            var secondRig = rigCache.GetOrAddRig(
                resolvedRigKey,
                () =>
                {
                    Interlocked.Increment(ref buildCalls);
                    return PreparedAnimationRigData.TryBuild(resolvedRigKey, avatar, mesh);
                },
                out var secondFromCache);
            var refMesh2 = secondRig!.GetOrBuildReferenceMesh(100f);

            // Verify O(1) direct bone path hash resolution on the cached rig
            var sampleHash = avatar.m_TOS[5].Key;
            var binding = new GenericBinding
            {
                path = sampleHash,
                attribute = 1,
                typeID = ClassIDType.Transform
            };
            var resolvedBone = secondRig.TryResolveBindingBone(binding, out var boneIdx);
            sw.Stop();

            Assert.True(secondFromCache);
            Assert.Same(firstRig, secondRig);
            Assert.Same(refMesh1, refMesh2);
            Assert.Equal(1, buildCalls);
            Assert.Equal(1, rigCache.HitCount);
            Assert.True(resolvedBone);
            Assert.Equal(5, boneIdx);
            Assert.True(sw.Elapsed.TotalMilliseconds < 5.0, $"Expected < 5 ms rig cache switch, actual {sw.Elapsed.TotalMilliseconds:0.###} ms.");
        }

        [Fact]
        public void SyntheticBenchmark_500RigsAnd5000Clips_PreIndexesAndQueriesUnder1msPerClip()
        {
            var tempRoot = CreateTempDirectory("anim_v14_benchmark_500_5000");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                using var _cache = cache;

                const int rigCount = 500;
                const int clipCount = 5_000;
                const int bonesPerRig = 12;

                var handles = new List<AssetHandle>(rigCount * 2 + clipCount);
                var rigAvatarIds = new string[rigCount];
                var rigMeshIds = new string[rigCount];
                var rigGroupIds = new string[rigCount];
                var rigBoneHashes = new uint[rigCount][];

                for (var r = 0; r < rigCount; r++)
                {
                    var avatarPathId = 10_000L + r * 2;
                    var meshPathId = 10_000L + r * 2 + 1;
                    var avatarId = AssetHandle.BuildUniqueID("rigs.assets", sourcePath, avatarPathId);
                    var meshId = AssetHandle.BuildUniqueID("rigs.assets", sourcePath, meshPathId);
                    var groupId = $"modelgroup:animator:{r:D4}";

                    rigAvatarIds[r] = avatarId;
                    rigMeshIds[r] = meshId;
                    rigGroupIds[r] = groupId;

                    handles.Add(CreateHandle(avatarId, $"Avatar_{r:D4}", ClassIDType.Avatar, "rigs.assets", sourcePath, avatarPathId));
                    handles.Add(CreateHandle(meshId, $"Mesh_{r:D4}", ClassIDType.Mesh, "rigs.assets", sourcePath, meshPathId, byteSize: 32768));

                    var hashes = new uint[bonesPerRig];
                    for (var b = 0; b < bonesPerRig; b++)
                    {
                        hashes[b] = MainWindow.ComputeUnityBonePathHash($"Rig_{r:D4}/Root/Bone_{b:D2}");
                    }
                    rigBoneHashes[r] = hashes;
                }

                var clipIds = new string[clipCount];
                for (var c = 0; c < clipCount; c++)
                {
                    var clipPathId = 100_000L + c;
                    var clipId = AssetHandle.BuildUniqueID("clips.assets", sourcePath, clipPathId);
                    clipIds[c] = clipId;
                    handles.Add(CreateHandle(clipId, $"Clip_{c:D5}", ClassIDType.AnimationClip, "clips.assets", sourcePath, clipPathId));
                }

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                var buildSw = Stopwatch.StartNew();
                using (var session = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    var batch = new SemanticAssetRelations();
                    batch.SourceFiles.Add(new SemanticSourceFileEntry("rigs.assets", sourcePath, "2022.3.10f1", rigCount * 2));
                    batch.SourceFiles.Add(new SemanticSourceFileEntry("clips.assets", sourcePath, "2022.3.10f1", clipCount));

                    for (var r = 0; r < rigCount; r++)
                    {
                        batch.ModelGroups.Add(new SemanticModelGroupRelation(
                            rigGroupIds[r], "Animator", $"Character_{r:D4}", "", $"Root_{r:D4}", "", rigAvatarIds[r], "", "rigs.assets", 95, "Animator rig"));
                        batch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                            rigGroupIds[r], rigMeshIds[r], $"smr_{r:D4}", "SkinnedMeshRenderer", "", $"Body_{r:D4}", 0, null, 95, "Animator rig"));

                        for (var b = 0; b < bonesPerRig; b++)
                        {
                            batch.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                                rigAvatarIds[r], string.Empty, rigMeshIds[r], rigGroupIds[r], rigBoneHashes[r][b]));
                        }
                    }

                    session.FlushBatch(batch, clearBatch: true);

                    for (var c = 0; c < clipCount; c++)
                    {
                        var targetRig = c % rigCount;
                        if (c % 2 == 0)
                        {
                            // Direct controller relation
                            batch.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                                clipIds[c], string.Empty, rigAvatarIds[targetRig], rigMeshIds[targetRig], rigGroupIds[targetRig], "AnimatorController", 10000, bonesPerRig, bonesPerRig));
                        }
                        else
                        {
                            // Indirect bone-hash relation (6 probe hashes per clip)
                            for (var b = 0; b < 6; b++)
                            {
                                batch.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(
                                    clipIds[c], rigBoneHashes[targetRig][b], bonesPerRig));
                            }
                        }

                        if (MainWindow.CountBufferedSemanticRelationRows(batch) >= 10_000)
                        {
                            session.FlushBatch(batch, clearBatch: true);
                        }
                    }

                    session.FlushBatch(batch, clearBatch: true);
                    Assert.True(session.Commit());
                }
                buildSw.Stop();

                // Warm up read connection pool
                _ = cache.LoadAnimationClipTargets(projectDir, signature, clipIds[0]);

                const int querySampleCount = 100;
                var querySw = Stopwatch.StartNew();
                for (var i = 0; i < querySampleCount; i++)
                {
                    var sampleIdx = (i * 49) % clipCount;
                    var expectedRig = sampleIdx % rigCount;
                    var targets = cache.LoadAnimationClipTargets(projectDir, signature, clipIds[sampleIdx]);
                    Assert.NotEmpty(targets);
                    Assert.Equal(rigAvatarIds[expectedRig], targets[0].AvatarAssetId);
                    Assert.Equal(rigMeshIds[expectedRig], targets[0].MeshAssetId);
                }
                querySw.Stop();

                var avgQueryMs = querySw.Elapsed.TotalMilliseconds / querySampleCount;
                _output.WriteLine($"500-rig / 5,000-clip staging+promotion time: {buildSw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms | Avg LoadAnimationClipTargets latency: {avgQueryMs.ToString("0.0000", CultureInfo.InvariantCulture)} ms ({querySw.Elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms for {querySampleCount} queries)");
                Assert.True(avgQueryMs < 1.0, $"Expected average query latency < 1.0 ms, actual {avgQueryMs:0.0000} ms");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static (Avatar Avatar, Mesh Mesh) CreateSyntheticAvatarAndSkinnedMesh(int boneCount, int vertexCount)
        {
            var avatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            avatar.m_Name = "HeroAvatar";
            avatar.m_PathID = 5001;

            var nodes = new Node[boneCount];
            var ids = new uint[boneCount];
            var tos = new KeyValuePair<uint, string>[boneCount];
            var poseX = new XForm[boneCount];

            for (var i = 0; i < boneCount; i++)
            {
                var path = i == 0 ? "Root" : $"Root/Bone_{i:D2}";
                var hash = MainWindow.ComputeUnityBonePathHash(path);
                ids[i] = hash;
                tos[i] = new KeyValuePair<uint, string>(hash, path);

                var node = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
                node.m_ParentId = i - 1;
                node.m_AxesId = -1;
                nodes[i] = node;

                var xf = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
                xf.t = new Vector3(0f, i * 0.1f, 0f);
                xf.q = new Quaternion(0f, 0f, 0f, 1f);
                xf.s = new Vector3(1f, 1f, 1f);
                poseX[i] = xf;
            }

            var skeleton = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            skeleton.m_Node = nodes;
            skeleton.m_ID = ids;
            skeleton.m_AxesArray = Array.Empty<Axes>();

            var skeletonPose = (SkeletonPose)RuntimeHelpers.GetUninitializedObject(typeof(SkeletonPose));
            skeletonPose.m_X = poseX;

            var avatarConstant = (AvatarConstant)RuntimeHelpers.GetUninitializedObject(typeof(AvatarConstant));
            avatarConstant.m_AvatarSkeleton = skeleton;
            avatarConstant.m_AvatarSkeletonPose = skeletonPose;
            avatarConstant.m_DefaultPose = skeletonPose;

            avatar.m_Avatar = avatarConstant;
            avatar.m_TOS = tos;

            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            typeof(Mesh)
                .GetField("m_ProcessLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(mesh, new object());
            typeof(Mesh)
                .GetField("m_DataProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.SetValue(mesh, true);
            mesh.m_Name = "HeroBodyMesh";
            mesh.m_PathID = 5002;
            mesh.m_VertexCount = vertexCount;
            mesh.m_Vertices = new float[vertexCount * 3];
            for (var v = 0; v < vertexCount; v++)
            {
                mesh.m_Vertices[v * 3] = (v % 10) * 0.1f;
                mesh.m_Vertices[v * 3 + 1] = (v / 10) * 0.1f;
                mesh.m_Vertices[v * 3 + 2] = 0f;
            }

            mesh.m_Indices = new List<uint>();
            for (uint v = 0; v + 2 < (uint)vertexCount; v += 3)
            {
                mesh.m_Indices.Add(v);
                mesh.m_Indices.Add(v + 1);
                mesh.m_Indices.Add(v + 2);
            }

            mesh.m_BoneNameHashes = ids.ToArray();
            mesh.m_BindPose = new Matrix4x4[boneCount];
            for (var i = 0; i < boneCount; i++)
            {
                mesh.m_BindPose[i] = new Matrix4x4(new float[]
                {
                    1f, 0f, 0f, 0f,
                    0f, 1f, 0f, -i * 0.1f,
                    0f, 0f, 1f, 0f,
                    0f, 0f, 0f, 1f
                });
            }

            mesh.m_Skin = new BoneWeights4[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var bw = (BoneWeights4)RuntimeHelpers.GetUninitializedObject(typeof(BoneWeights4));
                bw.boneIndex = new[] { v % boneCount, 0, 0, 0 };
                bw.weight = new[] { 1f, 0f, 0f, 0f };
                mesh.m_Skin[v] = bw;
            }

            return (avatar, mesh);
        }

        private static void CreateLegacyV13Database(
            string dbPath,
            string projectDir,
            string signature,
            string sourcePath,
            string meshId,
            string clipId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE CacheSchema (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO CacheSchema (Key, Value) VALUES ('SemanticSchemaVersion', '13');

                CREATE TABLE Projects (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FolderPath TEXT NOT NULL,
                    SignatureHash TEXT NOT NULL,
                    TotalFiles INTEGER NOT NULL,
                    TotalBytes INTEGER NOT NULL,
                    UnityBundleCount INTEGER NOT NULL,
                    LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UnityVersion TEXT
                );
                CREATE INDEX idx_projects_lookup ON Projects(FolderPath COLLATE NOCASE, SignatureHash, LastIndexed DESC);
                INSERT INTO Projects (FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                VALUES (@folder, @sig, 1, 64, 0, '2022.3.10f1');

                CREATE TABLE SourceFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    SerializedFileName TEXT NOT NULL,
                    OriginalPath TEXT NOT NULL DEFAULT '',
                    UnityVersion TEXT NOT NULL DEFAULT '',
                    ObjectCount INTEGER NOT NULL DEFAULT 0,
                    LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE UNIQUE INDEX idx_source_files_unique ON SourceFiles(ProjectId, SerializedFileName, OriginalPath);

                CREATE TABLE Assets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    UniqueID TEXT NOT NULL,
                    SourceFileId INTEGER REFERENCES SourceFiles(Id) ON DELETE SET NULL,
                    Name TEXT NOT NULL,
                    Type INTEGER NOT NULL,
                    Container TEXT NOT NULL DEFAULT '',
                    PathID INTEGER NOT NULL,
                    ByteStart INTEGER NOT NULL,
                    ByteSize INTEGER NOT NULL,
                    PRIMARY KEY (ProjectId, UniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE ProjectIndexingState (
                    ProjectId INTEGER PRIMARY KEY REFERENCES Projects(Id) ON DELETE CASCADE,
                    Status TEXT NOT NULL DEFAULT 'not_started',
                    TotalFiles INTEGER NOT NULL DEFAULT 0,
                    ProcessedFiles INTEGER NOT NULL DEFAULT 0,
                    PendingFiles INTEGER NOT NULL DEFAULT 0,
                    PercentComplete REAL NOT NULL DEFAULT 0,
                    CurrentFile TEXT NOT NULL DEFAULT '',
                    LastReadFile TEXT NOT NULL DEFAULT '',
                    StartedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    CompletedAt DATETIME
                );
                INSERT INTO ProjectIndexingState (ProjectId, Status, TotalFiles, ProcessedFiles, PendingFiles, PercentComplete)
                VALUES (1, 'connections_completed', 1, 1, 0, 100);

                CREATE TABLE PreviewCacheEntries (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    AssetUniqueID TEXT NOT NULL,
                    PreviewKind TEXT NOT NULL,
                    AlgorithmVersion INTEGER NOT NULL,
                    Parameters TEXT NOT NULL DEFAULT '',
                    PayloadHash TEXT NOT NULL,
                    PayloadPath TEXT,
                    ByteSize INTEGER NOT NULL DEFAULT 0,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000Z',
                    LastAccessedUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000Z',
                    PayloadBlob BLOB,
                    PayloadCompression INTEGER NOT NULL DEFAULT 0,
                    UncompressedByteSize INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX idx_preview_cache_unique ON PreviewCacheEntries(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters);
                INSERT INTO PreviewCacheEntries (ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize)
                VALUES (1, 'preserved_preview_v13', 'png', 1, '256', 'hash_v13_thumb', '/tmp/p13.png', 1024);

                INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                VALUES (1, 'level0', @sourcePath, '2022.3.10f1', 2);

                INSERT INTO Assets (ProjectId, UniqueID, SourceFileId, Name, Type, Container, PathID, ByteStart, ByteSize)
                VALUES (1, @meshId, 1, 'HeroMesh', @meshType, 'assets/hero.fbx', 100, 4096, 2048),
                       (1, @clipId, 1, 'HeroRunClip', @clipType, 'assets/hero_run.anim', 200, 8192, 1024);";
            cmd.Parameters.AddWithValue("@folder", projectDir);
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@sourcePath", sourcePath);
            cmd.Parameters.AddWithValue("@meshId", meshId);
            cmd.Parameters.AddWithValue("@clipId", clipId);
            cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
            cmd.Parameters.AddWithValue("@clipType", (int)ClassIDType.AnimationClip);
            cmd.ExecuteNonQuery();
        }

        private static void AssertQueryPlanUsesSearchWithoutPhysicalScans(
            SqliteConnection conn,
            string queryName,
            string sql,
            Action<SqliteCommand> bindParameters)
        {
            var planLines = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                bindParameters(cmd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    planLines.Add(reader.GetString(3));
                }
            }

            Assert.NotEmpty(planLines);
            var formattedPlan = string.Join(Environment.NewLine, planLines);
            Assert.True(
                planLines.Any(line => line.Contains("SEARCH ", StringComparison.OrdinalIgnoreCase)),
                $"Expected query '{queryName}' to use SEARCH, but plan was:{Environment.NewLine}{formattedPlan}");
            Assert.False(
                planLines.Any(line => line.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase)),
                $"Query '{queryName}' performed an unexpected SCAN:{Environment.NewLine}{formattedPlan}");
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, ProjectScanResult ScanResult, string SourcePath)
            CreateSampleProject(string tempRoot)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = Path.Combine(tempRoot, "SampleProject");
            Directory.CreateDirectory(projectDir);
            var sourcePath = Path.Combine(projectDir, "level0");
            File.WriteAllBytes(sourcePath, new byte[64]);

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePath);
        }

        private static AssetHandle CreateHandle(
            string uniqueId,
            string name,
            ClassIDType type,
            string serializedFileName,
            string sourcePath,
            long pathId,
            long byteSize = 1024)
        {
            return new AssetHandle
            {
                UniqueID = uniqueId,
                Name = name,
                Type = type,
                Container = $"assets/{name.ToLowerInvariant()}",
                OriginalPath = sourcePath,
                SerializedFileName = serializedFileName,
                PathID = pathId,
                ByteStart = pathId * 64,
                ByteSize = byteSize
            };
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
