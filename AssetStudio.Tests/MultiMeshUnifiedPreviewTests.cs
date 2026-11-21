using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class MultiMeshUnifiedPreviewTests
    {
        private const string AnimaGameDataDir =
            @"F:\SteamLibrary\steamapps\common\Anima Gate of Memories\AnimaGateOfMemories_Data";

        private readonly ITestOutputHelper _output;

        public MultiMeshUnifiedPreviewTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CompositeAvatarAndAnimation_CombinesTwoSkinnedPartsWithDistinctBindPosesAndOneRigidAttachment_WithAlignedNormalsAndBounds()
        {
            // Build a 4-node Avatar hierarchy:
            //   [0] Root  @ (0, 0, 0)
            //   [1] Spine @ (0, 1, 0) (parent 0)
            //   [2] ArmR  @ (1, 1, 0) (parent 1 -> local (1,0,0))
            //   [3] HandR @ (2, 1, 0) (parent 2 -> local (1,0,0))
            var nodePaths = new[]
            {
                "Root",
                "Root/Spine",
                "Root/Spine/ArmR",
                "Root/Spine/ArmR/HandR"
            };
            var parents = new[] { -1, 0, 1, 2 };
            var localPositions = new[]
            {
                new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
                new OpenTK.Mathematics.Vector3(0f, 1f, 0f),
                new OpenTK.Mathematics.Vector3(1f, 0f, 0f),
                new OpenTK.Mathematics.Vector3(1f, 0f, 0f)
            };
            var avatar = CreateSyntheticAvatar("HeroCompositeAvatar", nodePaths, parents, localPositions);

            Assert.True(MeshPreviewSkeletonBuilder.TryBuildAvatarSkeletonFromDefaultPose(avatar, out var defaultSkeleton));
            Assert.Equal(4, defaultSkeleton.NodeCount);
            AssertVectorClose(new OpenTK.Mathematics.Vector3(2f, 1f, 0f), defaultSkeleton.RestWorldPositions[3], 1e-4f);

            // Skinned Part 1 ("BodyMesh"): 2 submeshes, bound to [Root, Spine] (bone hashes in order [Root, Spine])
            var bodyMesh = CreateSyntheticMesh(
                name: "BodyMesh",
                pathId: 1001,
                vertices: new[]
                {
                    new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
                    new OpenTK.Mathematics.Vector3(0.4f, 0f, 0f),
                    new OpenTK.Mathematics.Vector3(0f, 0.5f, 0f),
                    new OpenTK.Mathematics.Vector3(0f, 1.0f, 0f),
                    new OpenTK.Mathematics.Vector3(0.4f, 1.0f, 0f),
                    new OpenTK.Mathematics.Vector3(0f, 1.5f, 0f)
                },
                indices: new uint[] { 0, 1, 2, 3, 4, 5 },
                subMeshIndexCounts: new[] { 3, 3 },
                normals: Enumerable.Repeat(OpenTK.Mathematics.Vector3.UnitZ, 6).ToArray(),
                uvs: Enumerable.Repeat(new OpenTK.Mathematics.Vector2(0.5f, 0.5f), 6).ToArray(),
                boneHashes: new[]
                {
                    SemanticGraphService.ComputeUnityBonePathHash("Root"),
                    SemanticGraphService.ComputeUnityBonePathHash("Root/Spine")
                },
                bindPoses: new[]
                {
                    ToAssetMatrix(defaultSkeleton.RestWorldMatrices[0].Inverted()),
                    ToAssetMatrix(defaultSkeleton.RestWorldMatrices[1].Inverted())
                },
                skinBoneIndices: new[] { 0, 0, 0, 1, 1, 1 });

            // Skinned Part 2 ("GauntletMesh"): 1 submesh, DIFFERENT m_BindPose order ([HandR, ArmR]) & local origin mesh
            var gauntletMesh = CreateSyntheticMesh(
                name: "GauntletMesh",
                pathId: 1002,
                vertices: new[]
                {
                    new OpenTK.Mathematics.Vector3(1.5f, 1.0f, 0f),
                    new OpenTK.Mathematics.Vector3(2.0f, 1.0f, 0f),
                    new OpenTK.Mathematics.Vector3(1.5f, 1.3f, 0f)
                },
                indices: new uint[] { 0, 1, 2 },
                subMeshIndexCounts: new[] { 3 },
                normals: Enumerable.Repeat(OpenTK.Mathematics.Vector3.UnitZ, 3).ToArray(),
                uvs: Enumerable.Repeat(new OpenTK.Mathematics.Vector2(0.25f, 0.75f), 3).ToArray(),
                boneHashes: new[]
                {
                    SemanticGraphService.ComputeUnityBonePathHash("Root/Spine/ArmR/HandR"),
                    SemanticGraphService.ComputeUnityBonePathHash("Root/Spine/ArmR")
                },
                bindPoses: new[]
                {
                    ToAssetMatrix(defaultSkeleton.RestWorldMatrices[3].Inverted()),
                    ToAssetMatrix(defaultSkeleton.RestWorldMatrices[2].Inverted())
                },
                skinBoneIndices: new[] { 0, 0, 0 });

            // Rigid Part 3 ("SwordMesh"): MeshRenderer (NO m_BindPose, NO m_Skin) child of HandR with local offset (0, 0.5, 0)
            var swordMesh = CreateSyntheticMesh(
                name: "SwordMesh",
                pathId: 1003,
                vertices: new[]
                {
                    new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
                    new OpenTK.Mathematics.Vector3(0.2f, 0f, 0f),
                    new OpenTK.Mathematics.Vector3(0f, 1.0f, 0f)
                },
                indices: new uint[] { 0, 1, 2 },
                subMeshIndexCounts: new[] { 3 },
                normals: Enumerable.Repeat(OpenTK.Mathematics.Vector3.UnitZ, 3).ToArray(),
                uvs: Enumerable.Repeat(new OpenTK.Mathematics.Vector2(0.8f, 0.2f), 3).ToArray(),
                boneHashes: null,
                bindPoses: null,
                skinBoneIndices: null);

            var bodyBoneMap = MeshPreviewSkeletonBuilder.MapSkinnedMeshBonesToAvatarNodes(bodyMesh, avatar, defaultSkeleton);
            var gauntletBoneMap = MeshPreviewSkeletonBuilder.MapSkinnedMeshBonesToAvatarNodes(gauntletMesh, avatar, defaultSkeleton);
            Assert.Equal(new[] { 0, 1 }, bodyBoneMap);
            Assert.Equal(new[] { 3, 2 }, gauntletBoneMap);

            // Rigid Sword is a child GameObject of HandR ("Root/Spine/ArmR/HandR/SwordProp") at world (2, 1.5, 0)
            Assert.True(MeshPreviewSkeletonBuilder.TryResolveRigidPartAvatarNode(
                defaultSkeleton,
                relativeTransformPath: "Root/Spine/ArmR/HandR/SwordProp",
                gameObjectName: "SwordProp",
                meshName: "SwordMesh",
                out var swordAttachNode,
                out var swordExactMatch));
            Assert.Equal(3, swordAttachNode);
            Assert.False(swordExactMatch);

            var swordStaticTransform = OpenTK.Mathematics.Matrix4.CreateTranslation(2f, 1.5f, 0f);
            var swordLocalOffset = MeshPreviewSkeletonBuilder.ComputeRigidPartLocalOffset(
                swordStaticTransform,
                swordAttachNode,
                swordExactMatch,
                defaultSkeleton.RestWorldMatrices);
            AssertVectorClose(new OpenTK.Mathematics.Vector3(0f, 0.5f, 0f), swordLocalOffset.ExtractTranslation(), 1e-4f);

            var partSpecs = new[]
            {
                new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = bodyMesh,
                    MeshAssetId = "hero#1001",
                    RendererAssetId = "smr#1",
                    IsSkinned = true,
                    BoneToSkeletonNodeMap = bodyBoneMap
                },
                // Duplicate (MeshAssetId, RendererAssetId) must be deduplicated cleanly!
                new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = bodyMesh,
                    MeshAssetId = "hero#1001",
                    RendererAssetId = "smr#1",
                    IsSkinned = true,
                    BoneToSkeletonNodeMap = bodyBoneMap
                },
                new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = gauntletMesh,
                    MeshAssetId = "hero#1002",
                    RendererAssetId = "smr#2",
                    IsSkinned = true,
                    BoneToSkeletonNodeMap = gauntletBoneMap
                },
                new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = swordMesh,
                    MeshAssetId = "hero#1003",
                    RendererAssetId = "mr#3",
                    IsSkinned = false,
                    RigidAttachNodeIndex = swordAttachNode,
                    RigidLocalOffset = swordLocalOffset
                }
            };

            // 1. Verify Static Composite Avatar GL preparation
            var staticGl = MeshPreviewControl.PrepareCompositeStaticAvatarGLData(
                partSpecs,
                defaultSkeleton.RestWorldPositions,
                defaultSkeleton.RestWorldMatrices,
                defaultSkeleton.ParentIndices,
                defaultSkeleton.NodeNames,
                densityPercent: 100f,
                boneScale: 1.0f);

            Assert.NotNull(staticGl);
            Assert.True(staticGl!.IsComposite);
            Assert.False(staticGl.IsSkeletonOnly);
            Assert.Equal(3, staticGl.PartCount); // 4 specs deduplicated to 3
            Assert.Equal(12, staticGl.ReferenceMesh.Vertices.Length); // 6 + 3 + 3
            Assert.Equal(12, staticGl.ReferenceMesh.Indices.Length);
            Assert.Equal(new[] { 3, 3, 3, 3 }, staticGl.SubMeshIndexCounts); // 2 + 1 + 1 submeshes
            Assert.NotNull(staticGl.Uvs);
            Assert.Equal(12, staticGl.Uvs!.Length);
            // Rigid sword vertex 0 (local 0,0,0) must sit at (2, 1.5, 0) in Avatar rest pose!
            var swordPartStatic = staticGl.CompositeParts![2];
            AssertVectorClose(
                new OpenTK.Mathematics.Vector3(2f, 1.5f, 0f),
                staticGl.ReferenceMesh.Vertices[swordPartStatic.VertexOffset],
                1e-4f);

            // 2. Verify Animated Composite Avatar GL preparation (rotate ArmR by +90 deg around X -> Y becomes Z, Z becomes -Y)
            MeshPreviewSkeletonBuilder.BuildCompositeAvatarDeformChainMasks(
                defaultSkeleton,
                new[] { (bodyMesh, bodyBoneMap), (gauntletMesh, gauntletBoneMap) },
                new[] { swordAttachNode },
                out var activeNodeMask,
                out var deformChainMask);
            Assert.True(activeNodeMask[0] && activeNodeMask[1] && activeNodeMask[3]);
            Assert.All(deformChainMask, b => Assert.True(b));

            var rot90X = OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitX, MathF.PI * 0.5f);
            var rotTracks = new Dictionary<int, List<(float time, OpenTK.Mathematics.Quaternion value)>>
            {
                [2] = new()
                {
                    (0.0f, OpenTK.Mathematics.Quaternion.Identity),
                    (1.0f / 30f, rot90X)
                }
            };

            var evalOk = MeshPreviewAnimationEvaluator.EvaluateAvatarSkeletonFrames(
                defaultSkeleton,
                activeNodeMask,
                deformChainMask,
                posTracks: new Dictionary<int, List<(float, OpenTK.Mathematics.Vector3)>>(),
                rotTracks: rotTracks,
                scaleTracks: new Dictionary<int, List<(float, OpenTK.Mathematics.Vector3)>>(),
                sampleRate: 30f,
                maxTime: 2f / 30f,
                useCeilingFrameCount: false,
                failIfAllTracksFiltered: true,
                out var allNodeFrames,
                out var allNodeWorldMatrices,
                out var renderParentIndices,
                out var resolvedFps,
                out var evalReason);

            Assert.True(evalOk, evalReason);
            Assert.Equal(2, allNodeFrames.Length);

            var animGl = MeshPreviewControl.PrepareCompositeAnimatedAvatarGLData(
                partSpecs,
                allNodeFrames,
                allNodeWorldMatrices,
                renderParentIndices,
                resolvedFps,
                defaultSkeleton.NodeNames,
                densityPercent: 100f,
                boneScale: 1.0f);

            Assert.NotNull(animGl);
            Assert.True(animGl!.IsAnimated);
            Assert.True(animGl.IsComposite);
            Assert.Equal(3, animGl.PartCount);
            Assert.NotNull(animGl.AnimatedMeshVertices);
            Assert.NotNull(animGl.AnimatedMeshNormals);
            Assert.Equal(2, animGl.AnimatedMeshVertices!.Length);
            Assert.Equal(2, animGl.AnimatedMeshNormals!.Length);

            var f0Verts = animGl.AnimatedMeshVertices[0];
            var f1Verts = animGl.AnimatedMeshVertices[1];
            var f1Normals = animGl.AnimatedMeshNormals[1];

            // BodyMesh (on Root & Spine) stays stationary at frame 1
            AssertVectorClose(f0Verts[0], f1Verts[0], 1e-4f);
            AssertVectorClose(OpenTK.Mathematics.Vector3.UnitZ, f1Normals[0], 1e-4f);

            // GauntletMesh (skinned on HandR, child of ArmR) rotates 90 deg around X -> normal (0,0,1) becomes (0,-1,0)
            var gauntletPart = animGl.CompositeParts![1];
            AssertVectorClose(new OpenTK.Mathematics.Vector3(0f, -1f, 0f), f1Normals[gauntletPart.VertexOffset], 1e-3f);
            var gv0 = f1Verts[gauntletPart.VertexOffset];
            var gv1 = f1Verts[gauntletPart.VertexOffset + 1];
            var gv2 = f1Verts[gauntletPart.VertexOffset + 2];
            Assert.True(MathF.Abs(OpenTK.Mathematics.Vector3.Dot(gv1 - gv0, f1Normals[gauntletPart.VertexOffset])) < 1e-4f);
            Assert.True(MathF.Abs(OpenTK.Mathematics.Vector3.Dot(gv2 - gv0, f1Normals[gauntletPart.VertexOffset])) < 1e-4f);

            // Rigid SwordMesh (attached to HandR with local offset (0, 0.5, 0)):
            // Rotating ArmR +90 deg around X turns local +Y into +Z -> sword base moves from (2, 1.5, 0) to (2, 1.0, 0.5)!
            var swordPart = animGl.CompositeParts[2];
            AssertVectorClose(new OpenTK.Mathematics.Vector3(2f, 1.0f, 0.5f), f1Verts[swordPart.VertexOffset], 1e-3f);
            AssertVectorClose(new OpenTK.Mathematics.Vector3(2f, 1.0f, 1.5f), f1Verts[swordPart.VertexOffset + 2], 1e-3f);
            // Sword normal (0,0,1) also rotates to (0,-1,0)!
            AssertVectorClose(new OpenTK.Mathematics.Vector3(0f, -1f, 0f), f1Normals[swordPart.VertexOffset], 1e-3f);

            // Dynamic per-tick SkinCompositeFrameNormals MUST match precomputed AnimatedMeshNormals for EVERY vertex across all 3 parts!
            var onDemandNormals = MeshPreviewControl.SkinCompositeFrameNormals(
                animGl.CompositeParts,
                f1Verts.Length,
                allNodeWorldMatrices[1]);
            Assert.NotNull(onDemandNormals);
            Assert.Equal(f1Normals.Length, onDemandNormals!.Length);
            for (var v = 0; v < f1Normals.Length; v++)
            {
                AssertVectorClose(f1Normals[v], onDemandNormals[v], 1e-4f);
            }
        }

        [Fact]
        public void SubMeshIndexCounts_AndTextureDeduplicationPlan_AlignAcrossCombinedMultiPartSlots()
        {
            // Simulate a 3-part composite with submesh counts [2, 1, 2] = 5 total slots sharing 3 unique textures + 1 untextured slot
            var subMeshTextureKeys = new string?[]
            {
                "tex_armor_diffuse", // part 0, submesh 0
                "tex_cloth_diffuse", // part 0, submesh 1
                "tex_armor_diffuse", // part 1, submesh 0 (shared with part 0!)
                null,                // part 2, submesh 0 (untextured)
                "tex_blade_diffuse"  // part 2, submesh 1
            };

            MainWindow.BuildSubMeshTextureDeduplicationPlan(
                slotCount: 5,
                subMeshTextureKeys,
                out var uniqueKeys,
                out var subMeshToUnique);

            Assert.Equal(3, uniqueKeys.Count);
            Assert.Equal(new[] { 0, 1, 0, -1, 2 }, subMeshToUnique);

            var fakeGlIds = new[] { 101, 102, 103 };
            Assert.Equal(101, MeshPreviewControl.ResolveSubMeshTextureId(fakeGlIds, subMeshToUnique, 0));
            Assert.Equal(102, MeshPreviewControl.ResolveSubMeshTextureId(fakeGlIds, subMeshToUnique, 1));
            Assert.Equal(101, MeshPreviewControl.ResolveSubMeshTextureId(fakeGlIds, subMeshToUnique, 2));
            Assert.Equal(0, MeshPreviewControl.ResolveSubMeshTextureId(fakeGlIds, subMeshToUnique, 3));
            Assert.Equal(103, MeshPreviewControl.ResolveSubMeshTextureId(fakeGlIds, subMeshToUnique, 4));
        }

        [Fact]
        public void RigidOnlyMultiLinkChainAvatar_63LinksZeroBindPoses_AndSkeletonOnlyZeroMeshAvatar_BothRenderInOpenGL()
        {
            // Model chainAvatar-style hierarchy: 1 root + 63 rigid chain links (total 64 nodes), ZERO m_BindPose!
            const int linkCount = 63;
            const int nodeCount = linkCount + 1;
            var nodePaths = new string[nodeCount];
            var parents = new int[nodeCount];
            var localPositions = new OpenTK.Mathematics.Vector3[nodeCount];
            nodePaths[0] = "ChainRoot";
            parents[0] = -1;
            localPositions[0] = OpenTK.Mathematics.Vector3.Zero;

            var runningPath = "ChainRoot";
            for (var i = 1; i <= linkCount; i++)
            {
                var linkName = $"Object{i:D3}";
                runningPath = $"{runningPath}/{linkName}";
                nodePaths[i] = runningPath;
                parents[i] = i - 1;
                localPositions[i] = new OpenTK.Mathematics.Vector3(0f, -1.0f, 0f);
            }

            var chainAvatar = CreateSyntheticAvatar("chainAvatar", nodePaths, parents, localPositions);
            Assert.True(MeshPreviewSkeletonBuilder.TryBuildAvatarSkeletonFromDefaultPose(chainAvatar, out var skeleton));
            Assert.Equal(nodeCount, skeleton.NodeCount);
            AssertVectorClose(new OpenTK.Mathematics.Vector3(0f, -linkCount, 0f), skeleton.RestWorldPositions[linkCount], 1e-3f);

            // (a) Skeleton-only Avatar with 0 meshes MUST produce valid OpenGL skeleton data (not null / not text fallback)!
            var skeletonOnlyGl = MeshPreviewControl.PrepareSkeletonOnlyAvatarGLData(
                skeleton.RestWorldPositions,
                skeleton.ParentIndices,
                skeleton.NodeNames,
                boneScale: 1.0f);
            Assert.NotNull(skeletonOnlyGl);
            Assert.True(skeletonOnlyGl!.IsSkeletonOnly);
            Assert.Null(skeletonOnlyGl.Mesh);
            Assert.Equal(0, skeletonOnlyGl.PartCount);
            Assert.True(skeletonOnlyGl.BoneLinesCount > 0);
            Assert.True(skeletonOnlyGl.JointPointsCount > 0);
            Assert.True(skeletonOnlyGl.SkeletonVertices.Length > 0);

            // Also verify PrepareCompositeStaticAvatarGLData falls back cleanly to skeleton-only when given 0 parts
            var emptyPartsFallbackGl = MeshPreviewControl.PrepareCompositeStaticAvatarGLData(
                Array.Empty<MeshPreviewControl.AvatarPreviewPartSpec>(),
                skeleton.RestWorldPositions,
                skeleton.RestWorldMatrices,
                skeleton.ParentIndices,
                skeleton.NodeNames,
                densityPercent: 100f,
                boneScale: 1.0f);
            Assert.NotNull(emptyPartsFallbackGl);
            Assert.True(emptyPartsFallbackGl!.IsSkeletonOnly);

            // (b) 63 rigid MeshRenderer links (sharing 1 instanced link mesh with 63 distinct RendererAssetIds!)
            // MUST NOT be capped at 32 — ALL 63 links must render and animate!
            var sharedLinkMesh = CreateSyntheticMesh(
                name: "ChainLinkMesh",
                pathId: 5001,
                vertices: new[]
                {
                    new OpenTK.Mathematics.Vector3(-0.1f, 0f, 0f),
                    new OpenTK.Mathematics.Vector3(0.1f, 0f, 0f),
                    new OpenTK.Mathematics.Vector3(0f, -0.5f, 0f)
                },
                indices: new uint[] { 0, 1, 2 },
                subMeshIndexCounts: new[] { 3 },
                normals: Enumerable.Repeat(OpenTK.Mathematics.Vector3.UnitZ, 3).ToArray(),
                uvs: null,
                boneHashes: null,
                bindPoses: null,
                skinBoneIndices: null);

            var partSpecs = new List<MeshPreviewControl.AvatarPreviewPartSpec>(linkCount);
            var rigidNodes = new List<int>(linkCount);
            for (var i = 1; i <= linkCount; i++)
            {
                var goName = $"Object{i:D3}";
                Assert.True(MeshPreviewSkeletonBuilder.TryResolveRigidPartAvatarNode(
                    skeleton,
                    relativeTransformPath: null,
                    gameObjectName: goName,
                    meshName: sharedLinkMesh.m_Name,
                    out var attachNode,
                    out var exactMatch));
                Assert.Equal(i, attachNode);
                Assert.True(exactMatch);
                rigidNodes.Add(attachNode);
                partSpecs.Add(new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = sharedLinkMesh,
                    MeshAssetId = "level77#5001",
                    RendererAssetId = $"level77#mr_{i}",
                    GameObjectName = goName,
                    IsSkinned = false,
                    RigidAttachNodeIndex = attachNode,
                    RigidLocalOffset = OpenTK.Mathematics.Matrix4.Identity
                });
            }

            var staticChainGl = MeshPreviewControl.PrepareCompositeStaticAvatarGLData(
                partSpecs,
                skeleton.RestWorldPositions,
                skeleton.RestWorldMatrices,
                skeleton.ParentIndices,
                skeleton.NodeNames,
                densityPercent: 100f,
                boneScale: 1.0f);
            Assert.NotNull(staticChainGl);
            Assert.Equal(63, staticChainGl!.PartCount);
            Assert.Equal(63 * 3, staticChainGl.ReferenceMesh.Vertices.Length);
            Assert.Equal(63, staticChainGl.SubMeshIndexCounts!.Length);
            Assert.True(staticChainGl.SourceMeshMin.Y <= -63.4f);

            // Animate link 1 rotating 45 deg around Z -> bottom link 63 swings strongly in +X!
            MeshPreviewSkeletonBuilder.BuildCompositeAvatarDeformChainMasks(
                skeleton,
                Array.Empty<(Mesh, int[])>(),
                rigidNodes,
                out var activeMask,
                out var deformMask);
            var swingRot = OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitZ, MathF.PI * 0.25f);
            var rotTracks = new Dictionary<int, List<(float time, OpenTK.Mathematics.Quaternion value)>>
            {
                [1] = new()
                {
                    (0f, OpenTK.Mathematics.Quaternion.Identity),
                    (1f / 30f, swingRot)
                }
            };
            var ok = MeshPreviewAnimationEvaluator.EvaluateAvatarSkeletonFrames(
                skeleton,
                activeMask,
                deformMask,
                new Dictionary<int, List<(float, OpenTK.Mathematics.Vector3)>>(),
                rotTracks,
                new Dictionary<int, List<(float, OpenTK.Mathematics.Vector3)>>(),
                sampleRate: 30f,
                maxTime: 2f / 30f,
                useCeilingFrameCount: false,
                failIfAllTracksFiltered: true,
                out var frames,
                out var mats,
                out var renderParents,
                out var fps,
                out var reason);
            Assert.True(ok, reason);

            var animChainGl = MeshPreviewControl.PrepareCompositeAnimatedAvatarGLData(
                partSpecs,
                frames,
                mats,
                renderParents,
                fps,
                skeleton.NodeNames,
                densityPercent: 100f,
                boneScale: 1.0f);
            Assert.NotNull(animChainGl);
            Assert.Equal(63, animChainGl!.PartCount);
            Assert.Equal(2, animChainGl.AnimatedMeshVertices!.Length);
            var lastLinkOffset = animChainGl.CompositeParts![^1].VertexOffset;
            var displacement = (animChainGl.AnimatedMeshVertices[1][lastLinkOffset] - animChainGl.AnimatedMeshVertices[0][lastLinkOffset]).Length;
            Assert.True(displacement > 20f, $"Expected swinging 63-link chain tip to move significantly, got {displacement:F2}");
            Assert.All(animChainGl.AnimatedMeshVertices[1], v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
        }

        [Fact]
        public void SelectBestMeshPreviewCandidate_AutoSelectsCompactSceneObjectAndAnimatorGroups_WhileSkippingCombinedMeshAndGiantSceneContainers()
        {
            var propMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            propMesh.m_Name = "KnightPauldron";
            propMesh.m_PathID = 201;

            var singleProp = new PreviewCandidateItem { Mesh = propMesh, MeshId = "res#201", Label = "Current mesh" };
            var compactArmorSceneGroup = new PreviewCandidateItem
            {
                MeshId = "res#201",
                ModelGroupId = "modelgroup:sceneobject:res#900",
                ModelGroupName = "KnightArmorSet",
                ModelGroupKind = "SceneObject",
                ModelGroupMeshIds = new[] { "res#201", "res#202", "res#203", "res#204", "res#205" },
                ModelGroupMeshCount = 5,
                ModelGroupConfidence = 95,
                Label = "Model group: KnightArmorSet (5 parts)"
            };

            // 1. Compact high-confidence SceneObject (5 parts) SHOULD auto-select when preferModelGroup == true
            var selectedCompactScene = MainWindow.SelectBestMeshPreviewCandidate(
                propMesh,
                new[] { singleProp, compactArmorSceneGroup },
                selectedCandidate: null,
                preferModelGroup: true);
            Assert.Same(compactArmorSceneGroup, selectedCompactScene);

            // 2. When preferModelGroup == false, single mesh MUST be kept
            var selectedSingleWhenDisabled = MainWindow.SelectBestMeshPreviewCandidate(
                propMesh,
                new[] { singleProp, compactArmorSceneGroup },
                selectedCandidate: null,
                preferModelGroup: false);
            Assert.Same(singleProp, selectedSingleWhenDisabled);

            // 3. Giant SceneObject (> 16 parts) must NOT auto-select
            var bigSceneGroup = new PreviewCandidateItem
            {
                MeshId = "res#201",
                ModelGroupId = "modelgroup:sceneobject:res#901",
                ModelGroupName = "CathedralHall",
                ModelGroupKind = "SceneObject",
                ModelGroupMeshIds = Enumerable.Range(1, 24).Select(i => $"res#{200 + i}").ToArray(),
                ModelGroupMeshCount = 24,
                ModelGroupConfidence = 100
            };
            Assert.Same(singleProp, MainWindow.SelectBestMeshPreviewCandidate(
                propMesh, new[] { singleProp, bigSceneGroup }, null, preferModelGroup: true));

            // 4. Root/Environment/SceneRoot container even with <= 16 parts must NOT auto-select
            var rootContainerGroup = new PreviewCandidateItem
            {
                MeshId = "res#201",
                ModelGroupId = "modelgroup:sceneobject:res#902",
                ModelGroupName = "SceneRoot",
                ModelGroupKind = "SceneObject",
                ModelGroupMeshIds = new[] { "res#201", "res#202", "res#203" },
                ModelGroupMeshCount = 3,
                ModelGroupConfidence = 100
            };
            Assert.Same(singleProp, MainWindow.SelectBestMeshPreviewCandidate(
                propMesh, new[] { singleProp, rootContainerGroup }, null, preferModelGroup: true));

            // 5. Combined Mesh* must NEVER auto-hijack even into a 3-part SceneObject
            var combinedMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            combinedMesh.m_Name = "Combined Mesh (root: scene)";
            combinedMesh.m_PathID = 301;
            var singleCombined = new PreviewCandidateItem { Mesh = combinedMesh, MeshId = "lvl#301", Label = "Current mesh" };
            var smallGroupWithCombined = new PreviewCandidateItem
            {
                MeshId = "lvl#301",
                ModelGroupId = "modelgroup:sceneobject:lvl#903",
                ModelGroupName = "SmallPropGroup",
                ModelGroupKind = "SceneObject",
                ModelGroupMeshIds = new[] { "lvl#301", "lvl#302", "lvl#303" },
                ModelGroupMeshCount = 3,
                ModelGroupConfidence = 100
            };
            Assert.Same(singleCombined, MainWindow.SelectBestMeshPreviewCandidate(
                combinedMesh, new[] { singleCombined, smallGroupWithCombined }, null, preferModelGroup: true));

            // 6. Multi-link Animator group (e.g. 63 parts like chainAvatar) SHOULD auto-select!
            var chainAnimatorGroup = new PreviewCandidateItem
            {
                MeshId = "res#201",
                ModelGroupId = "modelgroup:animator:lvl77#400",
                ModelGroupName = "chain",
                ModelGroupKind = "Animator",
                ModelGroupMeshIds = Enumerable.Range(1, 63).Select(i => $"lvl77#{i}").ToArray(),
                ModelGroupMeshCount = 63,
                ModelGroupConfidence = 80
            };
            Assert.Same(chainAnimatorGroup, MainWindow.SelectBestMeshPreviewCandidate(
                propMesh, new[] { singleProp, chainAnimatorGroup }, null, preferModelGroup: true));
        }

        [Fact]
        public void MeshPreviewViewModel_FullModelGroupToggle_Notifications_ResetDeduplication_AndPureMvvmInvariants()
        {
            var vm = new MeshPreviewViewModel();
            Assert.True(vm.ShowFullModelGroup);
            Assert.False(vm.HasMultiPartModelGroup);
            Assert.Equal(0, vm.ModelGroupPartCount);
            Assert.Equal("Full Object", vm.FullModelGroupToggleLabel);

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    changed.Add(e.PropertyName);
                }
            };

            vm.SetModelGroupContext(hasMultiPartGroup: true, partCount: 6);
            Assert.True(vm.HasMultiPartModelGroup);
            Assert.Equal(6, vm.ModelGroupPartCount);
            Assert.Equal("Full Object (6 parts)", vm.FullModelGroupToggleLabel);
            Assert.Contains(nameof(MeshPreviewViewModel.HasMultiPartModelGroup), changed);
            Assert.Contains(nameof(MeshPreviewViewModel.ModelGroupPartCount), changed);
            Assert.Contains(nameof(MeshPreviewViewModel.FullModelGroupToggleLabel), changed);

            Assert.False(vm.ToggleShowFullModelGroup());
            Assert.False(vm.ShowFullModelGroup);
            Assert.True(vm.ToggleShowFullModelGroup());
            Assert.True(vm.ShowFullModelGroup);

            vm.ClearModelGroupContext();
            Assert.False(vm.HasMultiPartModelGroup);
            Assert.Equal(0, vm.ModelGroupPartCount);
            Assert.Equal("Full Object", vm.FullModelGroupToggleLabel);

            // Double Reset must fire 0 PropertyChanged events on second call
            vm.Reset();
            changed.Clear();
            vm.Reset();
            Assert.Empty(changed);

            // Verify pure-MVVM invariants across files touched in Track 1
            var repoRoot = FindRepoRoot();
            var axamlText = File.ReadAllText(Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml"));
            Assert.DoesNotMatch(new Regex(@"\bClick\s*=", RegexOptions.CultureInvariant), axamlText);
            Assert.Contains("x:CompileBindings=\"True\"", axamlText, StringComparison.Ordinal);
            Assert.Contains("ShowFullModelGroupCheckBox", axamlText, StringComparison.Ordinal);

            foreach (var relPath in new[]
            {
                Path.Combine("AssetStudio.Avalonia", "ViewModels", "MeshPreviewViewModel.cs"),
                Path.Combine("AssetStudio.Avalonia", "Services", "MeshPreviewSkeletonBuilder.cs"),
                Path.Combine("AssetStudio.Avalonia", "Services", "MeshPreviewAnimationEvaluator.cs")
            })
            {
                var content = File.ReadAllText(Path.Combine(repoRoot, relPath));
                Assert.DoesNotMatch(new Regex(@"^\s*using\s+Avalonia\b", RegexOptions.Multiline), content);
            }
        }

        [Fact]
        public void RealGameReadOnly_AnimaGateOfMemories_MultiPartSkinnedMarionette_AndRigid63LinkChainAvatar_AssembleAndAnimateCleanly()
        {
            if (!Directory.Exists(AnimaGameDataDir))
            {
                _output.WriteLine($"Skipping real-game test: '{AnimaGameDataDir}' not present.");
                return;
            }

            var resourcesPath = Path.Combine(AnimaGameDataDir, "resources.assets");
            var level77Path = Path.Combine(AnimaGameDataDir, "level77");
            var shared77Path = Path.Combine(AnimaGameDataDir, "sharedassets77.assets");

            // 1. Multi-part Skinned Character: 01MarionetteLv1Abierta (4 SkinnedMeshRenderers with 19/44/1/1 bindPoses)
            if (File.Exists(resourcesPath))
            {
                var am = new AssetsManager();
                am.LoadFiles(resourcesPath);
                var file = am.assetsFileList.FirstOrDefault();
                Assert.NotNull(file);

                var anim = file!.Objects.OfType<Animator>()
                    .FirstOrDefault(a => a.m_GameObject.TryGet(out var g) && g.m_Name == "01MarionetteLv1Abierta");
                if (anim != null && anim.m_Avatar.TryGet(out var marionetteAvatar) && anim.m_GameObject.TryGet(out var animGo))
                {
                    Assert.True(MeshPreviewSkeletonBuilder.TryBuildAvatarSkeletonFromDefaultPose(marionetteAvatar, out var skel));
                    var smrs = file.Objects.OfType<SkinnedMeshRenderer>()
                        .Where(s => s.m_GameObject.TryGet(out var g)
                            && SemanticGraphService.IsSameOrDescendantGameObject(file, g, animGo))
                        .ToList();
                    Assert.True(smrs.Count >= 4, $"Expected >= 4 SMRs on 01MarionetteLv1Abierta, found {smrs.Count}");

                    var partSpecs = new List<MeshPreviewControl.AvatarPreviewPartSpec>();
                    var skinnedPairs = new List<(Mesh, int[])>();
                    foreach (var smr in smrs)
                    {
                        if (!smr.m_Mesh.TryGet(out var m) || m == null) continue;
                        m.EnsureProcessed();
                        var map = MeshPreviewSkeletonBuilder.MapSkinnedMeshBonesToAvatarNodes(m, marionetteAvatar, skel);
                        Assert.Contains(map, idx => idx >= 0);
                        skinnedPairs.Add((m, map));
                        partSpecs.Add(new MeshPreviewControl.AvatarPreviewPartSpec
                        {
                            Mesh = m,
                            MeshAssetId = $"res#{m.m_PathID}",
                            RendererAssetId = $"res#{smr.m_PathID}",
                            IsSkinned = true,
                            BoneToSkeletonNodeMap = map
                        });
                    }

                    var staticGl = MeshPreviewControl.PrepareCompositeStaticAvatarGLData(
                        partSpecs,
                        skel.RestWorldPositions,
                        skel.RestWorldMatrices,
                        skel.ParentIndices,
                        skel.NodeNames,
                        densityPercent: 100f,
                        boneScale: 1.0f);
                    Assert.NotNull(staticGl);
                    Assert.Equal(partSpecs.Count, staticGl!.PartCount);
                    Assert.All(staticGl.ReferenceMesh.Vertices, v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
                    // Head part must be positioned above torso (Y > 2.5), aligned with skeleton!
                    Assert.True(staticGl.SourceMeshMax.Y > 2.8f, $"Expected full standing marionette height > 2.8, got {staticGl.SourceMeshMax.Y:F2}");

                    // Find a real AnimationClip matching marionetteAvatar and animate ALL 4 parts!
                    var avatarHashes = SemanticGraphService.ExtractAvatarBindingPathHashes(marionetteAvatar);
                    var matchingClip = file.Objects.OfType<AnimationClip>()
                        .Select(c => (Clip: c, Paths: SemanticGraphService.GetTransformBindingPathsBackground(c)))
                        .Where(x => SemanticGraphService.IsStrongAnimationAvatarMatch(x.Paths.Count, x.Paths.Count(avatarHashes.Contains)))
                        .OrderByDescending(x => x.Paths.Count(avatarHashes.Contains))
                        .Select(x => x.Clip)
                        .FirstOrDefault();
                    if (matchingClip?.m_MuscleClip?.m_Clip != null)
                    {
                        MeshPreviewSkeletonBuilder.BuildCompositeAvatarDeformChainMasks(
                            skel, skinnedPairs, Array.Empty<int>(), out var activeMask, out var deformMask);
                        var inner = matchingClip.m_MuscleClip.m_Clip;
                        var bindings = matchingClip.m_ClipBindingConstant ?? inner.ConvertValueArrayToGenericBinding();
                        var tracks = new AnimationTrackSet();
                        MeshPreviewAnimationEvaluator.ExtractMuscleClipTracks(
                            inner,
                            bindings,
                            matchingClip.m_MuscleClip.m_StopTime,
                            (GenericBinding b, out int n) => skel.TryResolveBindingNode(marionetteAvatar, b, out n),
                            n => n >= 0 && n < deformMask.Length && deformMask[n],
                            tracks,
                            updateMaxTimeBeforeBoundsCheck: true);
                        tracks.SortAll();
                        Assert.True(tracks.TotalTrackCount > 0);

                        var ok = MeshPreviewAnimationEvaluator.EvaluateAvatarSkeletonFrames(
                            skel,
                            activeMask,
                            deformMask,
                            tracks.PosTracks,
                            tracks.RotTracks,
                            tracks.ScaleTracks,
                            matchingClip.m_SampleRate > 0 ? matchingClip.m_SampleRate : 30f,
                            tracks.MaxTime > 0 ? tracks.MaxTime : matchingClip.m_MuscleClip.m_StopTime,
                            useCeilingFrameCount: false,
                            failIfAllTracksFiltered: false,
                            out var frames,
                            out var mats,
                            out var renderParents,
                            out var fps,
                            out var reason);
                        Assert.True(ok, reason);

                        var animGl = MeshPreviewControl.PrepareCompositeAnimatedAvatarGLData(
                            partSpecs, frames, mats, renderParents, fps, skel.NodeNames, densityPercent: 70f, boneScale: 1.0f);
                        Assert.NotNull(animGl);
                        Assert.Equal(partSpecs.Count, animGl!.PartCount);
                        Assert.Equal(frames.Length, animGl.AnimatedMeshVertices!.Length);
                        Assert.All(animGl.AnimatedMeshVertices[frames.Length / 2], v =>
                            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
                        _output.WriteLine($"[Anima Marionette] {partSpecs.Count} skinned parts animated over {frames.Length} frames (clip='{matchingClip.m_Name}', tracks={tracks.TotalTrackCount}).");
                    }
                }
            }

            // 2. Rigid-only 63-link chainAvatar from level77 + sharedassets77.assets (0 SkinnedMeshRenderers, 0 m_BindPose!)
            if (File.Exists(level77Path) && File.Exists(shared77Path))
            {
                var am77 = new AssetsManager();
                am77.LoadFiles(level77Path, shared77Path);
                var chainAnim = am77.assetsFileList
                    .SelectMany(f => f.Objects.OfType<Animator>().Select(a => (File: f, Anim: a)))
                    .FirstOrDefault(x => x.Anim.m_Avatar.TryGet(out var av) && string.Equals(av.m_Name, "chainAvatar", StringComparison.OrdinalIgnoreCase));

                Assert.NotNull(chainAnim.Anim);
                Assert.True(chainAnim.Anim.m_Avatar.TryGet(out var chainAvatar));
                Assert.True(chainAnim.Anim.m_GameObject.TryGet(out var chainGo));

                // Pure Avatar FK skeleton works with 0 m_BindPose!
                Assert.True(MeshPreviewSkeletonBuilder.TryBuildAvatarSkeletonFromDefaultPose(chainAvatar!, out var chainSkel));
                Assert.Equal(62, chainSkel.NodeCount);

                // Skeleton-only preview works even with 0 meshes!
                var chainSkelOnly = MeshPreviewControl.PrepareSkeletonOnlyAvatarGLData(
                    chainSkel.RestWorldPositions, chainSkel.ParentIndices, chainSkel.NodeNames, boneScale: 1.0f);
                Assert.NotNull(chainSkelOnly);
                Assert.True(chainSkelOnly!.IsSkeletonOnly);
                Assert.True(chainSkelOnly.BoneLinesCount > 0);

                // All 63 rigid MeshRenderer links attach cleanly to chainAvatar!
                var descMrs = chainAnim.File.Objects.OfType<MeshRenderer>()
                    .Where(r => r.m_GameObject.TryGet(out var rg)
                        && SemanticGraphService.IsSameOrDescendantGameObject(chainAnim.File, rg, chainGo!))
                    .ToList();
                Assert.True(descMrs.Count >= 60, $"Expected >= 60 rigid links on chainAvatar, found {descMrs.Count}");

                var rootGoId = AssetHandle.BuildUniqueID(chainAnim.File, chainGo!.m_PathID);
                var rigidSpecs = new List<MeshPreviewControl.AvatarPreviewPartSpec>();
                var attachedCount = 0;
                foreach (var mr in descMrs)
                {
                    if (!mr.m_GameObject.TryGet(out var rg) || rg == null) continue;
                    Mesh? linkMesh = null;
                    if (rg.m_Components != null)
                    {
                        foreach (var cp in rg.m_Components)
                        {
                            if (cp.TryGet(out var c) && c is MeshFilter mf && mf.m_Mesh.TryGet(out var mm) && mm != null)
                            {
                                linkMesh = mm;
                                break;
                            }
                        }
                    }

                    if (linkMesh == null) continue;
                    linkMesh.EnsureProcessed();
                    var matched = MeshPreviewSkeletonBuilder.TryResolveRigidPartAvatarNode(
                        chainSkel, null, rg.m_Name, linkMesh.m_Name, out var nodeIdx, out var exactMatch);
                    if (matched) attachedCount++;
                    var rawTf = SemanticGraphService.BuildLocalToGroupRootMatrix(chainAnim.File, rg, rootGoId);
                    OpenTK.Mathematics.Matrix4? staticTf = MeshPreviewSkeletonBuilder.TryConvertRowMajorTransformMatrix(rawTf, out var parsed)
                        ? parsed
                        : null;
                    var offset = MeshPreviewSkeletonBuilder.ComputeRigidPartLocalOffset(
                        staticTf, nodeIdx, exactMatch, chainSkel.RestWorldMatrices);

                    rigidSpecs.Add(new MeshPreviewControl.AvatarPreviewPartSpec
                    {
                        Mesh = linkMesh,
                        MeshAssetId = $"l77#{linkMesh.m_PathID}",
                        RendererAssetId = $"l77#{mr.m_PathID}",
                        GameObjectName = rg.m_Name,
                        IsSkinned = false,
                        RigidAttachNodeIndex = nodeIdx,
                        RigidLocalOffset = offset
                    });
                }

                Assert.True(attachedCount >= 55, $"Expected >= 55 chain links matched to AvatarNodes, got {attachedCount}");
                var chainCompositeGl = MeshPreviewControl.PrepareCompositeStaticAvatarGLData(
                    rigidSpecs,
                    chainSkel.RestWorldPositions,
                    chainSkel.RestWorldMatrices,
                    chainSkel.ParentIndices,
                    chainSkel.NodeNames,
                    densityPercent: 100f,
                    boneScale: 1.0f);
                Assert.NotNull(chainCompositeGl);
                Assert.Equal(rigidSpecs.Count, chainCompositeGl!.PartCount);
                Assert.All(chainCompositeGl.ReferenceMesh.Vertices, v =>
                    Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
                _output.WriteLine($"[Anima chainAvatar] {chainCompositeGl.PartCount} rigid links ({chainCompositeGl.ReferenceMesh.Vertices.Length:N0} verts, {attachedCount} bound to 62 AvatarNodes) prepared cleanly!");
            }
        }

        private static Avatar CreateSyntheticAvatar(
            string name,
            string[] nodePaths,
            int[] parentIndices,
            OpenTK.Mathematics.Vector3[] localPositions)
        {
            var count = nodePaths.Length;
            var avatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            avatar.m_Name = name;
            avatar.m_PathID = 9000;
            avatar.m_AvatarSize = (uint)(count * 64);

            var nodes = new Node[count];
            var ids = new uint[count];
            var xforms = new XForm[count];
            var tos = new KeyValuePair<uint, string>[count];

            for (var i = 0; i < count; i++)
            {
                var node = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
                node.m_ParentId = parentIndices[i];
                node.m_AxesId = -1;
                nodes[i] = node;

                var hash = SemanticGraphService.ComputeUnityBonePathHash(nodePaths[i]);
                ids[i] = hash;
                tos[i] = new KeyValuePair<uint, string>(hash, nodePaths[i]);

                var xf = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
                xf.t = new AssetStudio.Vector3(localPositions[i].X, localPositions[i].Y, localPositions[i].Z);
                xf.q = new AssetStudio.Quaternion(0f, 0f, 0f, 1f);
                xf.s = new AssetStudio.Vector3(1f, 1f, 1f);
                xforms[i] = xf;
            }

            var skel = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            skel.m_Node = nodes;
            skel.m_ID = ids;
            skel.m_AxesArray = Array.Empty<Axes>();

            var pose = (SkeletonPose)RuntimeHelpers.GetUninitializedObject(typeof(SkeletonPose));
            pose.m_X = xforms;

            var constant = (AvatarConstant)RuntimeHelpers.GetUninitializedObject(typeof(AvatarConstant));
            constant.m_AvatarSkeleton = skel;
            constant.m_AvatarSkeletonPose = pose;
            constant.m_DefaultPose = pose;

            avatar.m_Avatar = constant;
            avatar.m_TOS = tos;
            return avatar;
        }

        private static Mesh CreateSyntheticMesh(
            string name,
            long pathId,
            OpenTK.Mathematics.Vector3[] vertices,
            uint[] indices,
            int[] subMeshIndexCounts,
            OpenTK.Mathematics.Vector3[]? normals,
            OpenTK.Mathematics.Vector2[]? uvs,
            uint[]? boneHashes,
            AssetStudio.Matrix4x4[]? bindPoses,
            int[]? skinBoneIndices)
        {
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            typeof(Mesh).GetField("m_ProcessLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(mesh, new object());
            typeof(Mesh).GetField("m_DataProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(mesh, true);
            mesh.m_Name = name;
            mesh.m_PathID = pathId;
            mesh.m_VertexCount = vertices.Length;
            mesh.m_UV0 = Array.Empty<float>();
            mesh.m_VariableBoneCountWeights = Array.Empty<uint>();

            var flatVerts = new float[vertices.Length * 3];
            for (var i = 0; i < vertices.Length; i++)
            {
                flatVerts[i * 3] = vertices[i].X;
                flatVerts[i * 3 + 1] = vertices[i].Y;
                flatVerts[i * 3 + 2] = vertices[i].Z;
            }
            mesh.m_Vertices = flatVerts;
            mesh.m_Indices = indices.ToList();

            if (normals != null)
            {
                var flatNormals = new float[normals.Length * 3];
                for (var i = 0; i < normals.Length; i++)
                {
                    flatNormals[i * 3] = normals[i].X;
                    flatNormals[i * 3 + 1] = normals[i].Y;
                    flatNormals[i * 3 + 2] = normals[i].Z;
                }
                mesh.m_Normals = flatNormals;
            }

            if (uvs != null)
            {
                var flatUvs = new float[uvs.Length * 2];
                for (var i = 0; i < uvs.Length; i++)
                {
                    flatUvs[i * 2] = uvs[i].X;
                    flatUvs[i * 2 + 1] = uvs[i].Y;
                }
                mesh.m_UV0 = flatUvs;
            }

            var subMeshes = new SubMesh[subMeshIndexCounts.Length];
            uint firstByte = 0;
            for (var i = 0; i < subMeshIndexCounts.Length; i++)
            {
                var sm = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
                sm.firstByte = firstByte;
                sm.indexCount = (uint)subMeshIndexCounts[i];
                sm.topology = GfxPrimitiveType.Triangles;
                subMeshes[i] = sm;
                firstByte += (uint)(subMeshIndexCounts[i] * 2);
            }
            mesh.m_SubMeshes = subMeshes;
            mesh.m_BoneNameHashes = boneHashes ?? Array.Empty<uint>();
            mesh.m_BindPose = bindPoses;

            if (skinBoneIndices != null)
            {
                var skin = new BoneWeights4[vertices.Length];
                for (var i = 0; i < vertices.Length; i++)
                {
                    var bw = new BoneWeights4
                    {
                        weight = new[] { 1f, 0f, 0f, 0f },
                        boneIndex = new[] { skinBoneIndices[i], 0, 0, 0 }
                    };
                    skin[i] = bw;
                }
                mesh.m_Skin = skin;
            }

            return mesh;
        }

        private static AssetStudio.Matrix4x4 ToAssetMatrix(OpenTK.Mathematics.Matrix4 m)
        {
            return new AssetStudio.Matrix4x4
            {
                M00 = m.M11, M01 = m.M12, M02 = m.M13, M03 = m.M14,
                M10 = m.M21, M11 = m.M22, M12 = m.M23, M13 = m.M24,
                M20 = m.M31, M21 = m.M32, M22 = m.M33, M23 = m.M34,
                M30 = m.M41, M31 = m.M42, M32 = m.M43, M33 = m.M44
            };
        }

        private static void AssertVectorClose(OpenTK.Mathematics.Vector3 expected, OpenTK.Mathematics.Vector3 actual, float tol)
        {
            var dist = (expected - actual).Length;
            Assert.True(dist <= tol, $"Expected {expected} (±{tol}), got {actual} (dist={dist:F6})");
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AssetStudio.Avalonia", "MainWindow.axaml")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }
    }
}
