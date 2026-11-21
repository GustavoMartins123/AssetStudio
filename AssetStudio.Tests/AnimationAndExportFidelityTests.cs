using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using Xunit;
using ASQuat = AssetStudio.Quaternion;
using ASVec3 = AssetStudio.Vector3;

namespace AssetStudio.Tests
{
    public class AnimationAndExportFidelityTests
    {
        [Fact]
        public void HumanoidMuscleDecoder_BodyAndFingerBindings_NoDuplicateIds_AndExactDofAxes()
        {
            Assert.Equal(24, HumanoidMuscleDecoder.BodyBoneDefs.Length);

            var body56Set = new HashSet<int>();
            var muscleSet = new HashSet<int>();
            foreach (var def in HumanoidMuscleDecoder.BodyBoneDefs)
            {
                Assert.True(def.BodyIndex56 >= 1 && def.BodyIndex56 <= 24);
                Assert.True(body56Set.Add(def.BodyIndex56), $"Duplicate BodyIndex56: {def.BodyIndex56}");
                for (int i = 0; i < def.DofAxes.Length; i++)
                {
                    int m = def.MuscleStart56 + i;
                    Assert.True(muscleSet.Add(m), $"Duplicate muscle slot: {m}");
                    Assert.InRange(def.DofAxes[i], 0, 2);
                }
            }

            // Confirm UpperChest (9), Eyes (22, 23), Jaw (24), and exact hinge axes (Toes Z-only, Knees/Elbows Z+X)
            var upperChest = Assert.Single(HumanoidMuscleDecoder.BodyBoneDefs, d => d.BodyIndex56 == 9);
            Assert.Equal("UpperChest", upperChest.UnityName);
            Assert.Equal(-1, upperChest.BodyIndexLegacy);
            Assert.Equal(6, upperChest.MuscleStart56);
            Assert.Equal(new[] { 2, 1, 0 }, upperChest.DofAxes);

            var leftEye = Assert.Single(HumanoidMuscleDecoder.BodyBoneDefs, d => d.BodyIndex56 == 22);
            var rightEye = Assert.Single(HumanoidMuscleDecoder.BodyBoneDefs, d => d.BodyIndex56 == 23);
            var jaw = Assert.Single(HumanoidMuscleDecoder.BodyBoneDefs, d => d.BodyIndex56 == 24);
            Assert.Equal(15, leftEye.MuscleStart56);
            Assert.Equal(17, rightEye.MuscleStart56);
            Assert.Equal(19, jaw.MuscleStart56);

            var leftLowerArm = Assert.Single(HumanoidMuscleDecoder.BodyBoneDefs, d => d.UnityName == "LeftLowerArm");
            Assert.Equal(new[] { 2, 0 }, leftLowerArm.DofAxes);

            var leftToes = Assert.Single(HumanoidMuscleDecoder.BodyBoneDefs, d => d.UnityName == "LeftToes");
            Assert.Equal(new[] { 2 }, leftToes.DofAxes);

            Assert.Equal(55, muscleSet.Count); // All 55 body muscles 0..54 covered exactly once
        }

        [Fact]
        public void HumanoidMuscleDecoder_MecanimZYXOrder_MatchesUnityFormulaAndTPoseIdentity()
        {
            var preQ = HumanoidMuscleDecoder.NormalizeQuat(new ASQuat(0.2f, -0.4f, 0.3f, 0.8f));
            var postQ = HumanoidMuscleDecoder.NormalizeQuat(new ASQuat(-0.5f, 0.5f, -0.5f, 0.5f));

            // 1. Zero muscles -> exact preQ * Conjugate(postQ) T-pose identity (0.0000 deg error)
            var qTPose = HumanoidMuscleDecoder.ComputeMecanimMuscleLocalRotation(preQ, postQ, 0f, 0f, 0f);
            var expectedTPose = HumanoidMuscleDecoder.NormalizeQuat(
                HumanoidMuscleDecoder.MultiplyQuat(preQ, HumanoidMuscleDecoder.ConjugateQuat(postQ)));
            Assert.True(QuatAngleErrorDeg(qTPose, expectedTPose) < 1e-4);

            // 2. Non-zero 3-DoF rotation (30, 40, 50) deg -> exact ZYX (qZ * qY * qX), NOT YXZ!
            float rx = Deg2Rad(30f), ry = Deg2Rad(40f), rz = Deg2Rad(50f);
            var qx = new ASQuat(MathF.Sin(rx * 0.5f), 0f, 0f, MathF.Cos(rx * 0.5f));
            var qy = new ASQuat(0f, MathF.Sin(ry * 0.5f), 0f, MathF.Cos(ry * 0.5f));
            var qz = new ASQuat(0f, 0f, MathF.Sin(rz * 0.5f), MathF.Cos(rz * 0.5f));

            var qZYX = HumanoidMuscleDecoder.MultiplyQuat(HumanoidMuscleDecoder.MultiplyQuat(qz, qy), qx);
            var qYXZ = HumanoidMuscleDecoder.MultiplyQuat(HumanoidMuscleDecoder.MultiplyQuat(qy, qx), qz);
            var expectedLocalZYX = HumanoidMuscleDecoder.NormalizeQuat(
                HumanoidMuscleDecoder.MultiplyQuat(HumanoidMuscleDecoder.MultiplyQuat(preQ, qZYX), HumanoidMuscleDecoder.ConjugateQuat(postQ)));
            var wrongLocalYXZ = HumanoidMuscleDecoder.NormalizeQuat(
                HumanoidMuscleDecoder.MultiplyQuat(HumanoidMuscleDecoder.MultiplyQuat(preQ, qYXZ), HumanoidMuscleDecoder.ConjugateQuat(postQ)));

            var actual = HumanoidMuscleDecoder.ComputeMecanimMuscleLocalRotation(preQ, postQ, rx, ry, rz);
            Assert.True(QuatAngleErrorDeg(actual, expectedLocalZYX) < 1e-4);
            Assert.True(QuatAngleErrorDeg(actual, wrongLocalYXZ) > 35.0, "Must use Unity ZYX order, not broken YXZ.");
        }

        [Fact]
        public void HumanoidMuscleDecoder_Modern95AndLegacy92Clips_DecodesRootTQHermiteAndFingersWithoutSentinels()
        {
            var avatar = BuildSyntheticHumanoidAvatar(scale: 1.5f);

            // Build a StreamedClip-only Humanoid clip with sentinel frames 0 (-3.4e38) and Count-1 (+Infinity):
            // Curves:
            //   0..2: RootT (idx[7..9]) = linear/cubic moving Y from 0.8 -> 1.0 -> 0.8 (scaled by 1.5 => 1.2 -> 1.5 -> 1.2)
            //   3..6: RootQ (idx[10..13]) = identity
            //   7: LeftLowerArm Stretch (idx[42+42=84]) = cubic Hermite curve (0 at t=0, peak 0.75 at t=0.5, 0 at t=1.0)
            //   8: LeftEye Down-Up (idx[42+15=57]) = 0.5 constant
            //   9: LeftThumb1 Stretched (idx[42+55=97]) = -0.5 constant
            var indexArray = new int[200];
            Array.Fill(indexArray, -1);
            indexArray[7] = 0;  // RootT.x
            indexArray[8] = 1;  // RootT.y
            indexArray[9] = 2;  // RootT.z
            indexArray[10] = 3; // RootQ.x
            indexArray[11] = 4; // RootQ.y
            indexArray[12] = 5; // RootQ.z
            indexArray[13] = 6; // RootQ.w
            indexArray[42 + 42] = 7; // LeftLowerArm Stretch (Z)
            indexArray[42 + 15] = 8; // LeftEye Down-Up (Z)
            indexArray[42 + 55] = 9; // LeftThumb1 Stretched (Z)

            // Hermite polynomial for curve 7 on [0, 1]: p(dt) = -3*dt^2 + 3*dt + 0 => p(0)=0, p(0.5)=0.75, p(1)=0!
            var streamedData = BuildStreamedClipData(new (float time, (int idx, float c0, float c1, float c2, float c3)[] keys)[]
            {
                (-3.4028235e38f, new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,0.8f), (2,0f,0f,0f,0f), (3,0f,0f,0f,0f), (4,0f,0f,0f,0f), (5,0f,0f,0f,0f), (6,0f,0f,0f,1f), (7,0f,0f,0f,0f), (8,0f,0f,0f,0.5f), (9,0f,0f,0f,-0.5f) }),
                (0.0f,           new[] { (0,0f,0f,0f,0f), (1,0f,0f,0.2f,0.8f), (2,0f,0f,0f,0f), (3,0f,0f,0f,0f), (4,0f,0f,0f,0f), (5,0f,0f,0f,0f), (6,0f,0f,0f,1f), (7,0f,-3f,3f,0f), (8,0f,0f,0f,0.5f), (9,0f,0f,0f,-0.5f) }),
                (1.0f,           new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,1.0f), (2,0f,0f,0f,0f), (3,0f,0f,0f,0f), (4,0f,0f,0f,0f), (5,0f,0f,0f,0f), (6,0f,0f,0f,1f), (7,0f,0f,0f,0f), (8,0f,0f,0f,0.5f), (9,0f,0f,0f,-0.5f) }),
                (float.PositiveInfinity, new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,1.0f), (2,0f,0f,0f,0f), (3,0f,0f,0f,0f), (4,0f,0f,0f,0f), (5,0f,0f,0f,0f), (6,0f,0f,0f,1f), (7,0f,0f,0f,0f), (8,0f,0f,0f,0.5f), (9,0f,0f,0f,-0.5f) }),
            });

            var clip = BuildSyntheticMuscleClip("HumanoidHermiteTest", new[] { 6000, 0, 30, 1 }, 10f, 0f, 1.0f,
                indexArray, streamedData, streamCurveCount: 10, denseSamples: null, denseFrameCount: 0, denseCurveCount: 0, denseRate: 0f, constData: null, bindings: Array.Empty<GenericBinding>());

            var decoded = HumanoidMuscleDecoder.DecodeHumanoidClip(clip, avatar);
            Assert.NotEmpty(decoded);

            // 1. No sentinel / infinite / negative timestamps anywhere
            foreach (var tr in decoded)
            {
                foreach (var k in tr.Translations) Assert.InRange(k.time, 0f, 1.0001f);
                foreach (var k in tr.Rotations) Assert.InRange(k.time, 0f, 1.0001f);
            }

            // 2. Pelvis RootT scaled by m_Scale=1.5 AND rotated into Pelvis parent-local space!
            // Parent "Root" has rotation -90 deg around X, so Avatar +Y maps to Parent +Z!
            var pelvisTrack = Assert.Single(decoded, t => t.BonePath == "Root/Hips");
            Assert.NotEmpty(pelvisTrack.Translations);
            var t0 = pelvisTrack.Translations.First(k => MathF.Abs(k.time - 0f) < 1e-4f).value;
            var t1 = pelvisTrack.Translations.First(k => MathF.Abs(k.time - 1f) < 1e-4f).value;
            // At t=0: Avatar Y = 0.8 * 1.5 = 1.2m -> in parent (-90 deg X), local Z = +1.2m, local Y = 0
            Assert.InRange(t0.Z, 1.19f, 1.21f);
            Assert.InRange(t1.Z, 1.49f, 1.51f);

            // 3. LeftLowerArm cubic Hermite midpoint at t=0.5 => muscle value = 0.75 => angle = 0.75 * 80 deg = 60 deg around Z!
            var forearmTrack = Assert.Single(decoded, t => t.BonePath == "Root/Hips/Spine/LeftLowerArm");
            var midKf = forearmTrack.Rotations.OrderBy(k => MathF.Abs(k.time - 0.5f)).First();
            Assert.InRange(midKf.time, 0.49f, 0.51f);
            float expectedAngleRad = 0.75f * Deg2Rad(80f);
            var expectedForearmQ = new ASQuat(0f, 0f, MathF.Sin(expectedAngleRad * 0.5f), MathF.Cos(expectedAngleRad * 0.5f));
            Assert.True(QuatAngleErrorDeg(midKf.value, expectedForearmQ) < 0.1,
                $"Expected 60 deg Hermite peak on LeftLowerArm, got error {QuatAngleErrorDeg(midKf.value, expectedForearmQ):F3} deg");

            // 4. LeftEye (m15) and LeftThumb1 (m55) both decoded!
            var eyeTrack = Assert.Single(decoded, t => t.BonePath == "Root/Hips/Spine/LeftEye");
            float expectedEyeRad = 0.5f * Deg2Rad(15f);
            Assert.True(QuatAngleErrorDeg(eyeTrack.Rotations[0].value, new ASQuat(0f, 0f, MathF.Sin(expectedEyeRad * 0.5f), MathF.Cos(expectedEyeRad * 0.5f))) < 0.1);

            var thumbTrack = Assert.Single(decoded, t => t.BonePath == "Root/Hips/Spine/LeftLowerArm/LeftThumb1");
            float expectedThumbRad = 0.5f * Deg2Rad(-20f); // val=-0.5 => -(-0.5) * min(-20) = -10 deg
            Assert.True(QuatAngleErrorDeg(thumbTrack.Rotations[0].value, new ASQuat(0f, 0f, MathF.Sin(expectedThumbRad * 0.5f), MathF.Cos(expectedThumbRad * 0.5f))) < 0.1);

            // 5. Legacy Unity 5.3 92-muscle clip (LeftLowerArm shifted by -3 -> idx[42+39=81], LeftThumb1 shifted by -3 -> idx[42+52=94])
            var legacyIdx = new int[155];
            Array.Fill(legacyIdx, -1);
            legacyIdx[42 + 39] = 0; // LeftLowerArm Stretch in 92-muscle clip
            legacyIdx[42 + 52] = 1; // LeftThumb1 Stretched in 92-muscle clip
            var legacyClip = BuildSyntheticMuscleClip("Legacy53Humanoid", new[] { 5, 3, 1, 2 }, 30f, 0f, 0.5f,
                legacyIdx, null, 0, null, 0, 0, 0f, new[] { 0.5f, -0.5f }, Array.Empty<GenericBinding>());
            var legacyDecoded = HumanoidMuscleDecoder.DecodeHumanoidClip(legacyClip, avatar);
            var legacyForearm = Assert.Single(legacyDecoded, t => t.BonePath == "Root/Hips/Spine/LeftLowerArm");
            float legAng = 0.5f * Deg2Rad(80f);
            Assert.True(QuatAngleErrorDeg(legacyForearm.Rotations[0].value, new ASQuat(0f, 0f, MathF.Sin(legAng * 0.5f), MathF.Cos(legAng * 0.5f))) < 0.1);
            Assert.Contains(legacyDecoded, t => t.BonePath == "Root/Hips/Spine/LeftLowerArm/LeftThumb1");
        }

        [Fact]
        public void MeshPreviewAnimationEvaluator_HumanoidClipAndUnityZxyEuler_ProduceExactTracks()
        {
            // 1. Unity ZXY Euler in MeshPreviewAnimationEvaluator (attr=4 & legacy m_EulerCurves)
            var trackSet = new AnimationTrackSet();
            float ex = 35f, ey = -50f, ez = 25f;
            trackSet.AddGenericKeyframe(0, 4u, 0.5f, new[] { ex, ey, ez }, 0);
            var expectedQ = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(ex, ey, ez, 4);
            var actualOtk = trackSet.RotTracks[0][0].value;
            Assert.True(QuatAngleErrorDeg(new ASQuat(actualOtk.X, actualOtk.Y, actualOtk.Z, actualOtk.W), expectedQ) < 1e-4);

            // 2. Pure Humanoid clip produces animated tracks in 3D preview evaluator (previously 0 tracks!)
            var avatar = BuildSyntheticHumanoidAvatar(scale: 1.0f);
            var indexArray = new int[200];
            Array.Fill(indexArray, -1);
            indexArray[42 + 42] = 0; // LeftLowerArm Stretch
            var clip = BuildSyntheticMuscleClip("PureHumanoidPreview", new[] { 2022, 3, 62, 3 }, 30f, 0f, 1.0f,
                indexArray, null, 0, new[] { 0.0f, 0.5f, 1.0f }, 3, 1, 2f, null, Array.Empty<GenericBinding>());

            var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
            var boneCount = skelIds.Length;
            var hashToIdx = new Dictionary<uint, int>();
            var boneToSkel = new int[boneCount];
            var boneNames = new string[boneCount];
            var parentIndices = new int[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                hashToIdx[skelIds[i]] = i;
                boneToSkel[i] = i;
                boneNames[i] = avatar.FindBonePath(skelIds[i]) ?? string.Empty;
                parentIndices[i] = avatar.m_Avatar.m_AvatarSkeleton.m_Node[i].m_ParentId;
            }
            var skelData = new AvatarMeshSkeletonData
            {
                MeshBoneCount = boneCount,
                RestBonePositions = new global::OpenTK.Mathematics.Vector3[boneCount],
                BindPoseInverses = Enumerable.Repeat(global::OpenTK.Mathematics.Matrix4.Identity, boneCount).ToArray(),
                MeshBoneHashToIdx = hashToIdx,
                MeshBoneToSkelNode = boneToSkel,
                MeshBoneNames = boneNames,
                MeshParentIndices = parentIndices
            };

            var previewTracks = new AnimationTrackSet();
            int added = MeshPreviewAnimationEvaluator.ExtractHumanoidMuscleTracks(
                clip,
                avatar,
                skelData,
                MeshPreviewSkeletonBuilder.BuildBonePathAliasMap(boneNames, boneCount),
                _ => true,
                previewTracks);

            Assert.True(added >= 1);
            Assert.True(previewTracks.RotTracks.Count >= 1);
            Assert.Equal(3, previewTracks.RotTracks.Values.First().Count);
        }

        [Fact]
        public void BlendShapeAnimations_MultiChannelModernNameHashAndCombine_Survive100PercentIntoBinaryFbx()
        {
            var root = new ImportedFrame { Name = "Nova_SN", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            var face = new ImportedFrame { Name = "face_LOD0", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            root.AddChild(face);

            var conv = new TestableModelConverter(root);
            var mesh = new ImportedMesh
            {
                Path = face.Path,
                hasNormal = true,
                VertexList = new List<ImportedVertex>
                {
                    new() { Vertex = new ASVec3(0, 0, 0), Normal = new ASVec3(0, 0, 1) },
                    new() { Vertex = new ASVec3(1, 0, 0), Normal = new ASVec3(0, 0, 1) },
                    new() { Vertex = new ASVec3(0, 1, 0), Normal = new ASVec3(0, 0, 1) },
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "FaceMat", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                }
            };
            conv.MeshList.Add(mesh);
            conv.MaterialList.Add(new ImportedMaterial { Name = "FaceMat" });

            string[] channels = { "Blink.Left", "Smile", "JawOpen" };
            var morph = new ImportedMorph { Path = face.Path };
            foreach (var ch in channels)
            {
                var shapeCh = (MeshBlendShapeChannel)RuntimeHelpers.GetUninitializedObject(typeof(MeshBlendShapeChannel));
                shapeCh.name = ch;
                shapeCh.nameHash = Crc32(ch); // Modern Unity 5.6-6000.4 hash = CRC32(name), NOT CRC32("blendShape." + name)!
                conv.RegisterMorphChannel(shapeCh, ch);

                morph.Channels.Add(new ImportedMorphChannel
                {
                    Name = ch,
                    KeyframeList = new List<ImportedMorphKeyframe>
                    {
                        new()
                        {
                            Weight = 100f,
                            VertexList = new List<ImportedMorphVertex>
                            {
                                new() { Index = 2, Vertex = new ImportedVertex { Vertex = new ASVec3(0, 1.1f, 0) } }
                            }
                        }
                    }
                });
            }
            conv.MorphList.Add(morph);
            conv.RegisterBonePath(Crc32("face_LOD0"), face.Path);

            // Build a modern Unity AnimationClip with 3 SkinnedMeshRenderer customType=20 curves (2 in Dense, 1 in Constant)
            // using channel.nameHash AND a legacy "blendShape." fallback hash check!
            var bindings = new[]
            {
                new GenericBinding { path = Crc32("face_LOD0"), attribute = Crc32("Blink.Left"), typeID = ClassIDType.SkinnedMeshRenderer, customType = 20 },
                new GenericBinding { path = Crc32("face_LOD0"), attribute = Crc32("Smile"), typeID = ClassIDType.SkinnedMeshRenderer, customType = 20 },
                new GenericBinding { path = Crc32("face_LOD0"), attribute = Crc32("blendShape.JawOpen"), typeID = ClassIDType.SkinnedMeshRenderer, customType = 20 },
            };
            var denseSamples = new float[]
            {
                0f, 10f,
                50f, 40f,
                100f, 80f,
                25f, 60f
            };
            var clip = BuildSyntheticMuscleClip("FaceStandBy", new[] { 6000, 0, 30, 1 }, 30f, 0f, 0.3f,
                Array.Empty<int>(), null, 0, denseSamples, 4, 2, 10f, new[] { 75f }, bindings);

            conv.AddClipAndConvert(clip);

            var anim = Assert.Single(conv.AnimationList);
            var faceTrack = Assert.Single(anim.TrackList, t => t.Path == face.Path);
            Assert.Equal(3, faceTrack.BlendShapes.Count);
            Assert.Equal(4, Assert.Single(faceTrack.BlendShapes, b => b.ChannelName == "Blink.Left").Keyframes.Count);
            Assert.Equal(4, Assert.Single(faceTrack.BlendShapes, b => b.ChannelName == "Smile").Keyframes.Count);
            Assert.Equal(2, Assert.Single(faceTrack.BlendShapes, b => b.ChannelName == "JawOpen").Keyframes.Count);

            // Export ASCII FBX and verify all 3 DeformPercent curves are connected!
            var tempDir = Path.Combine(Path.GetTempPath(), "as_f3_bs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var asciiPath = Path.Combine(tempDir, "face_ascii.fbx");
                var binPath = Path.Combine(tempDir, "face_bin.fbx");
                new FbxSharpieExporter(asciiPath, 1f, 0, isAscii: true, is60Fps: false).Export(conv, asciiPath);
                new FbxSharpieExporter(binPath, 1f, 0, isAscii: false, is60Fps: false).Export(conv, binPath);

                var asciiTxt = File.ReadAllText(asciiPath);
                // 3 channels -> 3 OP connections ("DeformPercent" on SubDeformer) + 3 OP connections ("d|DeformPercent" on AnimCurveNode)
                Assert.Equal(3, Regex.Matches(asciiTxt, "C:\\s*\"OP\",\\s*\\d+,\\s*\\d+,\\s*\"DeformPercent\"").Count);
                Assert.Equal(3, Regex.Matches(asciiTxt, "C:\\s*\"OP\",\\s*\\d+,\\s*\\d+,\\s*\"d\\|DeformPercent\"").Count);
                Assert.True(new FileInfo(binPath).Length > 1000);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void EulerZxyCurves_AndContinuousEulerUnwrapping_ZeroOrderErrorAndZeroMidpointSpins()
        {
            // 1. Unity ZXY Euler (attr=4) -> FBX eEulerXYZ round-trip across 200 random triples (< 1e-3 deg!)
            var q2e = typeof(ModelConverter).GetMethod("QuaternionToEuler", BindingFlags.NonPublic | BindingFlags.Static)!;
            var rng = new Random(12345);
            double maxErr = 0.0;
            for (int i = 0; i < 200; i++)
            {
                float ex = (float)(rng.NextDouble() * 340.0 - 170.0);
                float ey = (float)(rng.NextDouble() * 340.0 - 170.0);
                float ez = (float)(rng.NextDouble() * 340.0 - 170.0);

                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(ex, ey, ez, 4);
                var qF = new ASQuat(qU.X, -qU.Y, -qU.Z, qU.W);
                var fbxEuler = (ASVec3)q2e.Invoke(null, new object[] { qF })!;

                var expectedMat = ReflectX(UnityEulerZXYToColMatrix(ex, ey, ez));
                var actualMat = FbxEulerXYZToColMatrix(fbxEuler.X, fbxEuler.Y, fbxEuler.Z);
                double err = RotationMatrixAngleErrorDeg(expectedMat, actualMat);
                if (err > maxErr) maxErr = err;
            }
            Assert.True(maxErr < 0.05, $"Unity ZXY -> FBX XYZ max round-trip error {maxErr:F4} deg exceeded tolerance.");

            // 2. Continuous Euler unwrapping across BOTH 360-wrap AND gimbal-dual branch flips!
            var rotKeys = new List<ImportedKeyframe<ASVec3>>
            {
                new(0.0f, new ASVec3(175f, 10f, -175f)),
                new(0.1f, new ASVec3(-175f, 12f, 175f)),      // 360 wrap on X & Z (true delta = +10 deg, -10 deg)
                new(0.2f, new ASVec3(170f, 88f, 170f)),       // near gimbal
                new(0.3f, new ASVec3(-10f, 92f, -10f)),       // exact gimbal-dual flip!
            };
            var origMatrices = rotKeys.Select(k => FbxEulerXYZToColMatrix(k.value.X, k.value.Y, k.value.Z)).ToArray();

            HumanoidMuscleDecoder.UnwrapEulerKeyframes(rotKeys);

            // Every keyframe must represent the EXACT same 3D rotation matrix (< 1e-2 deg)
            for (int i = 0; i < rotKeys.Count; i++)
            {
                var unwrappedMat = FbxEulerXYZToColMatrix(rotKeys[i].value.X, rotKeys[i].value.Y, rotKeys[i].value.Z);
                double err = RotationMatrixAngleErrorDeg(origMatrices[i], unwrappedMat);
                Assert.True(err < 0.05, $"Key {i}: unwrapped=({rotKeys[i].value.X:F2},{rotKeys[i].value.Y:F2},{rotKeys[i].value.Z:F2}), err={err:F4} deg");
            }

            // Frame 0 -> 1 must unwrap continuously (+10 deg X, -10 deg Z), NOT jump by 350 deg!
            Assert.True(MathF.Abs(rotKeys[1].value.X - rotKeys[0].value.X) < 15f);
            Assert.True(MathF.Abs(rotKeys[1].value.Z - rotKeys[0].value.Z) < 15f);

            // Frame 2 -> 3 gimbal-dual flip must unwrap smoothly (midpoint spin error < 5 deg, NOT 180 deg!)
            var eMid = new ASVec3(
                0.5f * (rotKeys[2].value.X + rotKeys[3].value.X),
                0.5f * (rotKeys[2].value.Y + rotKeys[3].value.Y),
                0.5f * (rotKeys[2].value.Z + rotKeys[3].value.Z));
            var rMid = FbxEulerXYZToColMatrix(eMid.X, eMid.Y, eMid.Z);
            var qMidTrue = System.Numerics.Quaternion.Slerp(ColMatrixToQuat(origMatrices[2]), ColMatrixToQuat(origMatrices[3]), 0.5f);
            double midSpinErr = RotationMatrixAngleErrorDeg(rMid, QuatToColMatrix(qMidTrue));
            Assert.True(midSpinErr < 5.0, $"Expected smooth unwrapped midpoint (<5 deg), got {midSpinErr:F2} deg spin.");
        }

        [Fact]
        public void StreamedStepped_DenseZeroRate_ConstantNoDup_Path0_FindAvatarNoHijack_AndVisibilityFbx()
        {
            var root = new ImportedFrame { Name = "PropRoot", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            var child = new ImportedFrame { Name = "Door", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            root.AddChild(child);

            var conv = new TestableModelConverter(root);
            conv.RegisterBonePath(Crc32("Door"), child.Path);

            // 1. Path == 0 with avatar == null resolves to RootFrame path, NEVER "Bone_0"!
            Assert.Equal("PropRoot", conv.CallGetPathFromHash(0u));

            // 2. Streamed stepped position + GameObject.m_IsActive visibility (attr=2086281974) + DenseClip with m_SampleRate=0 + ConstantClip with m_StopTime=0
            var bindings = new[]
            {
                new GenericBinding { path = 0u, attribute = 1u, typeID = ClassIDType.Transform },                     // slots 0..2 (Streamed stepped!)
                new GenericBinding { path = Crc32("Door"), attribute = 2086281974u, typeID = ClassIDType.GameObject }, // slot 3 (Streamed visibility!)
                new GenericBinding { path = Crc32("Door"), attribute = 3u, typeID = ClassIDType.Transform },           // slots 4..6 (Dense with sr=0!)
                new GenericBinding { path = Crc32("Door"), attribute = 1u, typeID = ClassIDType.Transform },           // slots 7..9 (Constant with stopTime=0!)
            };
            var streamed = BuildStreamedClipData(new (float time, (int idx, float c0, float c1, float c2, float c3)[] keys)[]
            {
                (-3.4e38f, new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,0f), (2,0f,0f,0f,0f), (3,0f,0f,0f,1f) }),
                (0.0f,     new[] { (0,0f,0f,0f,1f), (1,0f,0f,0f,2f), (2,0f,0f,0f,3f), (3,0f,0f,0f,1f) }),
                (0.5f,     new[] { (0,0f,0f,0f,5f), (1,0f,0f,0f,2f), (2,0f,0f,0f,3f), (3,0f,0f,0f,0f) }),
                (float.PositiveInfinity, new[] { (0,0f,0f,0f,5f), (1,0f,0f,0f,2f), (2,0f,0f,0f,3f), (3,0f,0f,0f,0f) }),
            });
            var clip = BuildSyntheticMuscleClip("HygieneAndVis", new[] { 2022, 3, 1, 1 }, 30f, 0f, 0f,
                Array.Empty<int>(), streamed, 4,
                new[] { 1f, 1f, 1f, 2f, 2f, 2f }, 2, 3, 0f, // Dense m_SampleRate == 0!
                new[] { 4f, 5f, 6f },                        // Constant with m_StopTime == 0!
                bindings);

            conv.AddClipAndConvert(clip);
            var anim = Assert.Single(conv.AnimationList);

            // Root track resolved to "PropRoot" (NOT "Bone_0") with stepped keys!
            Assert.DoesNotContain(anim.TrackList, t => t.Path == "Bone_0");
            var rootTrack = Assert.Single(anim.TrackList, t => t.Path == "PropRoot");
            Assert.Equal(2, rootTrack.Translations.Count);
            Assert.True(rootTrack.Translations[0].isStepped);

            // Door track: finite Dense scale times (zero-sample-rate guarded!), single Constant pos key (no dup t=0!), and 2 stepped Visibility keys!
            var doorTrack = Assert.Single(anim.TrackList, t => t.Path == child.Path);
            Assert.Equal(2, doorTrack.Scalings.Count);
            Assert.All(doorTrack.Scalings, k => Assert.True(float.IsFinite(k.time)));
            Assert.Single(doorTrack.Translations); // ConstantClip with stopTime==0 produced 1 key, not 2 duplicate t=0 keys!
            Assert.Equal(2, doorTrack.Visibilities.Count);
            Assert.Equal(1.0f, doorTrack.Visibilities[0].value);
            Assert.Equal(0.0f, doorTrack.Visibilities[1].value);
            Assert.True(doorTrack.Visibilities[0].isStepped);

            // Verify FBX writes KeyAttrFlags = 2 (eInterpolationConstant) and Visibility property curve!
            var tempDir = Path.Combine(Path.GetTempPath(), "as_f3_vis_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fbxPath = Path.Combine(tempDir, "vis_stepped.fbx");
                new FbxSharpieExporter(fbxPath, 1f, 0, isAscii: true, is60Fps: false).Export(conv, fbxPath);
                var fbxTxt = File.ReadAllText(fbxPath);
                Assert.Contains("\"Visibility\"", fbxTxt);
                Assert.Contains("\"d|Visibility\"", fbxTxt);
                // Constant interpolation flag 2 emitted for stepped curves!
                Assert.Matches(@"KeyAttrFlags:\s*\*1\s*\{\s*a:\s*2\s*\}", fbxTxt);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            // 3. FindAvatar must NEVER hijack an unrelated Avatar from assetsFileList!
            var unrelatedAvatar = BuildSyntheticHumanoidAvatar(scale: 1.0f, avatarName: "UnrelatedHeroAvatar");
            var am = new AssetsManager();
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = am;
            sf.Objects = new List<AssetStudio.Object> { unrelatedAvatar };
            am.assetsFileList.Add(sf);

            var propGo = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            propGo.m_Name = "WoodenCrate";
            propGo.assetsFile = sf;
            conv.RegisterBonePath(Crc32("WoodenCrate/Lid"), "WoodenCrate/Lid");
            Assert.Null(conv.CallFindAvatar(propGo));
        }

        [Fact]
        public void UnityAnimYamlAndMetaExport_ConvertsToLeftHanded_PopulatesBindings_ModelTypes_AndTextureColorSpace()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "as_f3_unity_export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 1. ExportUnityAnimationClip: FBX RH -> Unity LH, strip RootFrame prefix, BlendShape + Visibility m_FloatCurves, genericBindings!
                var qOrigUnity = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(25f, -60f, 15f, 4);
                var qFbx = new ASQuat(qOrigUnity.X, -qOrigUnity.Y, -qOrigUnity.Z, qOrigUnity.W);
                var q2e = typeof(ModelConverter).GetMethod("QuaternionToEuler", BindingFlags.NonPublic | BindingFlags.Static)!;
                var fbxEuler = (ASVec3)q2e.Invoke(null, new object[] { qFbx })!;

                var track = new ImportedAnimationKeyframedTrack { Path = "HeroRoot/Hips/Spine" };
                track.Translations.Add(new ImportedKeyframe<ASVec3>(0f, new ASVec3(-2.5f, 1.0f, 3.0f))); // FBX RH X=-2.5 -> Unity LH X=+2.5
                track.Rotations.Add(new ImportedKeyframe<ASVec3>(0f, fbxEuler));
                track.Scalings.Add(new ImportedKeyframe<ASVec3>(0f, new ASVec3(1f, 1f, 1f)));
                track.Visibilities.Add(new ImportedKeyframe<float>(0f, 1f, isStepped: true));
                track.FindOrCreateBlendShape("Smile").Keyframes.Add(new ImportedKeyframe<float>(0.5f, 80f));

                var anim = new ImportedKeyframedAnimation
                {
                    Name = "TestClip",
                    SampleRate = 60f,
                    TrackList = new List<ImportedAnimationKeyframedTrack> { track }
                };

                var animPath = Path.Combine(tempDir, "TestClip.anim");
                AssetExportHelper.ExportUnityAnimationClip(anim, animPath, rootFrameName: "HeroRoot");
                var yaml = File.ReadAllText(animPath);

                // RootFrame prefix stripped -> path is "Hips/Spine", NOT "HeroRoot/Hips/Spine"!
                Assert.DoesNotContain("HeroRoot/Hips/Spine", yaml);
                Assert.Contains("path: Hips/Spine", yaml);
                // Position X negated back to Unity LH (+2.5)!
                Assert.Contains("value: {x: 2.5, y: 1, z: 3}", yaml);
                // Rotations exported to m_RotationCurves (Unity LH quaternion matching qOrigUnity!)
                Assert.Contains("m_RotationCurves:\n  - curve:", yaml.Replace("\r\n", "\n"));
                var qMatch = Regex.Match(yaml, @"value:\s*\{x:\s*([-\d\.eE]+),\s*y:\s*([-\d\.eE]+),\s*z:\s*([-\d\.eE]+),\s*w:\s*([-\d\.eE]+)\}");
                Assert.True(qMatch.Success);
                var parsedQ = new ASQuat(
                    float.Parse(qMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                    float.Parse(qMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                    float.Parse(qMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                    float.Parse(qMatch.Groups[4].Value, CultureInfo.InvariantCulture));
                Assert.True(QuatAngleErrorDeg(parsedQ, qOrigUnity) < 1e-2);
                // BlendShape + Visibility in m_FloatCurves & genericBindings populated!
                Assert.Contains("attribute: blendShape.Smile", yaml);
                Assert.Contains("attribute: m_IsActive", yaml);
                Assert.DoesNotContain("genericBindings: []", yaml);
                Assert.Contains($"path: {Crc32("Hips/Spine")}", yaml);
                Assert.Contains($"attribute: {Crc32("Smile")}", yaml);
                Assert.Contains("attribute: 2086281974", yaml);

                // 2. WriteModelMetaIfMissing: Static (0), Generic (2), Humanoid (3 with real bones, NO hardcoded Bip001!) + globalScale: 1
                var staticConv = new TestableModelConverter(new ImportedFrame { Name = "Crate" });
                staticConv.MeshList.Add(new ImportedMesh { Path = "Crate" });
                var staticFbx = Path.Combine(tempDir, "crate.fbx");
                AssetExportHelper.WriteModelMetaIfMissing(staticFbx, null, staticConv, null);
                var staticMeta = File.ReadAllText(staticFbx + ".meta");
                Assert.Contains("animationType: 0", staticMeta);
                Assert.Contains("globalScale: 1", staticMeta);
                Assert.DoesNotContain("Bip001", staticMeta);

                var genericConv = new TestableModelConverter(new ImportedFrame { Name = "Monster" });
                genericConv.MeshList.Add(new ImportedMesh { Path = "Monster", BoneList = new List<ImportedBone> { new() { Path = "Monster/BoneA" } } });
                var genericFbx = Path.Combine(tempDir, "monster.fbx");
                AssetExportHelper.WriteModelMetaIfMissing(genericFbx, null, genericConv, null);
                var genericMeta = File.ReadAllText(genericFbx + ".meta");
                Assert.Contains("animationType: 2", genericMeta);
                Assert.Contains("globalScale: 1", genericMeta);
                Assert.DoesNotContain("Bip001", genericMeta);

                var humAvatar = BuildSyntheticHumanoidAvatar(scale: 1.2f, avatarName: "CustomHeroAvatar");
                var humFbx = Path.Combine(tempDir, "hero.fbx");
                AssetExportHelper.WriteModelMetaIfMissing(humFbx, null, genericConv, humAvatar);
                var humMeta = File.ReadAllText(humFbx + ".meta");
                Assert.Contains("animationType: 3", humMeta);
                Assert.Contains("globalScale: 1", humMeta);
                Assert.Contains("boneName: Hips", humMeta);
                Assert.Contains("humanName: LeftLowerArm", humMeta);
                Assert.DoesNotContain("Bip001", humMeta);

                // 3. WriteTextureMetaIfMissing: m_ColorSpace (0=linear, 1=sRGB), NormalMap (textureType: 1), Sprite (textureType: 8)
                var linearTex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
                linearTex.m_Name = "MetalRoughMask";
                linearTex.m_ColorSpace = 0;
                linearTex.m_TextureFormat = TextureFormat.RGBA32;
                var linPath = Path.Combine(tempDir, "MetalRoughMask.png");
                AssetExportHelper.WriteTextureMetaIfMissing(linPath, linearTex, isNormalMap: false);
                var linMeta = File.ReadAllText(linPath + ".meta");
                Assert.Contains("sRGBTexture: 0", linMeta);
                Assert.Contains("linearTexture: 1", linMeta);
                Assert.Contains("textureType: 0", linMeta);

                var srgbTex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
                srgbTex.m_Name = "AlbedoDiff";
                srgbTex.m_ColorSpace = 1;
                srgbTex.m_TextureFormat = TextureFormat.DXT1;
                var srgbPath = Path.Combine(tempDir, "AlbedoDiff.png");
                AssetExportHelper.WriteTextureMetaIfMissing(srgbPath, srgbTex, isNormalMap: false);
                var srgbMeta = File.ReadAllText(srgbPath + ".meta");
                Assert.Contains("sRGBTexture: 1", srgbMeta);
                Assert.Contains("linearTexture: 0", srgbMeta);
                Assert.Contains("textureType: 0", srgbMeta);

                var nrmTex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
                nrmTex.m_Name = "Wall_Normal";
                nrmTex.m_ColorSpace = 1;
                nrmTex.m_TextureFormat = TextureFormat.DXT5;
                var nrmPath = Path.Combine(tempDir, "Wall_Normal.png");
                AssetExportHelper.WriteTextureMetaIfMissing(nrmPath, nrmTex, isNormalMap: true);
                var nrmMeta = File.ReadAllText(nrmPath + ".meta");
                Assert.Contains("textureType: 1", nrmMeta);
                Assert.Contains("sRGBTexture: 0", nrmMeta);

                var spr = (Sprite)RuntimeHelpers.GetUninitializedObject(typeof(Sprite));
                spr.m_Name = "IconSword";
                var sprPath = Path.Combine(tempDir, "IconSword.png");
                AssetExportHelper.WriteTextureMetaIfMissing(sprPath, spr);
                Assert.Contains("textureType: 8", File.ReadAllText(sprPath + ".meta"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Theory]
        [InlineData(0)] // XYZ
        [InlineData(1)] // XZY
        [InlineData(2)] // YZX
        [InlineData(3)] // YXZ
        [InlineData(4)] // ZXY (Unity default)
        [InlineData(5)] // ZYX
        public void UnityEulerDegreesToQuaternion_AllSixRotationOrders_MatchExactMatrixProduct(int rotationOrder)
        {
            float ex = 31f, ey = -47f, ez = 22f;
            var q = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(ex, ey, ez, rotationOrder);
            var qMat = QuatToColMatrix(new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W));

            var rx = RotXCol(ex);
            var ry = RotYCol(ey);
            var rz = RotZCol(ez);
            var expected = rotationOrder switch
            {
                0 => rz * ry * rx,
                1 => ry * rz * rx,
                2 => rx * rz * ry,
                3 => rz * rx * ry,
                5 => rx * ry * rz,
                _ => ry * rx * rz,
            };

            Assert.True(RotationMatrixAngleErrorDeg(qMat, expected) < 0.05);
        }

        [Fact]
        public void StreamedClip_HighCurvatureHermiteSpan_AdaptivelySubdividesAndMatchesCubicMidpoint()
        {
            var root = new ImportedFrame { Name = "Mover", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            var conv = new TestableModelConverter(root);

            // Cubic Hermite on [0, 1]: y(dt) = -4*dt^2 + 4*dt + 0 -> y(0)=0, y(0.5)=1.0, y(1)=0 (strong arch!)
            var streamed = BuildStreamedClipData(new (float time, (int idx, float c0, float c1, float c2, float c3)[] keys)[]
            {
                (-3.4e38f, new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,0f), (2,0f,0f,0f,0f) }),
                (0.0f,     new[] { (0,0f,0f,0f,0f), (1,0f,-4f,4f,0f), (2,0f,0f,0f,0f) }),
                (1.0f,     new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,0f), (2,0f,0f,0f,0f) }),
                (float.PositiveInfinity, new[] { (0,0f,0f,0f,0f), (1,0f,0f,0f,0f), (2,0f,0f,0f,0f) }),
            });
            var bindings = new[]
            {
                new GenericBinding { path = 0u, attribute = 1u, typeID = ClassIDType.Transform }
            };
            var clip = BuildSyntheticMuscleClip("ArchJump", new[] { 2022, 3, 1, 1 }, 30f, 0f, 1.0f,
                Array.Empty<int>(), streamed, 3, null, 0, 0, 0f, null, bindings);

            conv.AddClipAndConvert(clip);
            var tr = Assert.Single(conv.AnimationList[0].TrackList, t => t.Path == "Mover");
            Assert.True(tr.Translations.Count >= 10, $"Expected adaptive subdivision of curved Hermite arc, got {tr.Translations.Count} keys.");

            var midKey = tr.Translations.OrderBy(k => MathF.Abs(k.time - 0.5f)).First();
            Assert.InRange(midKey.value.Y, 0.98f, 1.02f);
        }

        [Fact]
        public void CombineMeshes_RemapsMultipleBlendShapeChannelsFromMultiplePartsOntoCombinedTrack()
        {
            var root = new ImportedFrame { Name = "Hero", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            var head = new ImportedFrame { Name = "Head", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            var jaw = new ImportedFrame { Name = "Jaw", LocalPosition = ASVec3.Zero, LocalRotation = ASVec3.Zero, LocalScale = ASVec3.One };
            root.AddChild(head);
            root.AddChild(jaw);

            var conv = new TestableModelConverter(root);
            conv.MeshList.Add(new ImportedMesh
            {
                Path = head.Path,
                VertexList = new List<ImportedVertex> { new() { Vertex = new ASVec3(0, 1, 0) }, new() { Vertex = new ASVec3(1, 1, 0) }, new() { Vertex = new ASVec3(0, 2, 0) } },
                SubmeshList = new List<ImportedSubmesh> { new() { Material = "M", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } } }
            });
            conv.MeshList.Add(new ImportedMesh
            {
                Path = jaw.Path,
                VertexList = new List<ImportedVertex> { new() { Vertex = new ASVec3(0, 0, 0) }, new() { Vertex = new ASVec3(1, 0, 0) }, new() { Vertex = new ASVec3(0, 0.5f, 0) } },
                SubmeshList = new List<ImportedSubmesh> { new() { Material = "M", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } } }
            });
            conv.MaterialList.Add(new ImportedMaterial { Name = "M" });

            var headTrack = new ImportedAnimationKeyframedTrack { Path = head.Path };
            headTrack.FindOrCreateBlendShape("BlinkL").Keyframes.Add(new ImportedKeyframe<float>(0f, 100f));
            headTrack.FindOrCreateBlendShape("BlinkR").Keyframes.Add(new ImportedKeyframe<float>(0f, 80f));
            var jawTrack = new ImportedAnimationKeyframedTrack { Path = jaw.Path };
            jawTrack.FindOrCreateBlendShape("Open").Keyframes.Add(new ImportedKeyframe<float>(0.5f, 60f));

            conv.AnimationList.Add(new ImportedKeyframedAnimation
            {
                Name = "Talk",
                SampleRate = 30f,
                TrackList = new List<ImportedAnimationKeyframedTrack> { headTrack, jawTrack }
            });

            conv.CombineMeshes();

            var combinedTrack = Assert.Single(conv.AnimationList[0].TrackList, t => t.Path == "Hero");
            Assert.Equal(3, combinedTrack.BlendShapes.Count);
            Assert.Contains(combinedTrack.BlendShapes, b => b.ChannelName == "BlinkL");
            Assert.Contains(combinedTrack.BlendShapes, b => b.ChannelName == "BlinkR");
            Assert.Contains(combinedTrack.BlendShapes, b => b.ChannelName == "Open");
        }

        [Fact]
        public void AssetExportHelper_WriteAnimationMetaAndBuildMorphChannelNames_MatchModernUnityHashesAndTypes()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "as_f3_anim_meta_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var genericAnimFbx = Path.Combine(tempDir, "walk_generic.fbx");
                AssetExportHelper.WriteAnimationMetaIfMissing(genericAnimFbx, null, imported: null, avatar: null);
                var genericMeta = File.ReadAllText(genericAnimFbx + ".meta");
                Assert.Contains("animationType: 2", genericMeta);
                Assert.Contains("importAnimation: 1", genericMeta);
                Assert.Contains("globalScale: 1", genericMeta);

                var humAvatar = BuildSyntheticHumanoidAvatar(1.1f);
                var humAnimFbx = Path.Combine(tempDir, "walk_humanoid.fbx");
                AssetExportHelper.WriteAnimationMetaIfMissing(humAnimFbx, null, imported: null, avatar: humAvatar);
                var humMeta = File.ReadAllText(humAnimFbx + ".meta");
                Assert.Contains("animationType: 3", humMeta);
                Assert.Contains("humanName: LeftLowerArm", humMeta);

                var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
                var ch = (MeshBlendShapeChannel)RuntimeHelpers.GetUninitializedObject(typeof(MeshBlendShapeChannel));
                ch.name = "Eye.Blink";
                ch.nameHash = Crc32("Eye.Blink");
                var shapes = (BlendShapeData)RuntimeHelpers.GetUninitializedObject(typeof(BlendShapeData));
                shapes.channels = new[] { ch };
                mesh.m_Shapes = shapes;
                var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
                sf.Objects = new List<AssetStudio.Object> { mesh };

                var morphMap = AssetExportHelper.BuildMorphChannelNames(new List<SerializedFile> { sf });
                Assert.Equal("blendShape.Eye.Blink", morphMap[ch.nameHash]);
                Assert.Equal("blendShape.Eye.Blink", morphMap[Crc32("blendShape.Eye.Blink")]);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ===================== Helpers =====================

        private sealed class TestableModelConverter : ModelConverter
        {
            private static readonly FieldInfo MorphNamesField = typeof(ModelConverter).GetField("morphChannelNames", BindingFlags.NonPublic | BindingFlags.Instance)!;
            private static readonly FieldInfo BoneHashField = typeof(ModelConverter).GetField("bonePathHash", BindingFlags.NonPublic | BindingFlags.Instance)!;
            private static readonly FieldInfo ClipsField = typeof(ModelConverter).GetField("animationClipHashSet", BindingFlags.NonPublic | BindingFlags.Instance)!;
            private static readonly MethodInfo RegMorphMethod = typeof(ModelConverter).GetMethod("RegisterMorphChannelHashes", BindingFlags.NonPublic | BindingFlags.Instance)!;
            private static readonly MethodInfo ConvertAnimsMethod = typeof(ModelConverter).GetMethod("ConvertAnimations", BindingFlags.NonPublic | BindingFlags.Instance)!;
            private static readonly MethodInfo GetPathHashMethod = typeof(ModelConverter).GetMethod("GetPathFromHash", BindingFlags.NonPublic | BindingFlags.Instance)!;
            private static readonly MethodInfo FindAvatarMethod = typeof(ModelConverter).GetMethod("FindAvatar", BindingFlags.NonPublic | BindingFlags.Instance)!;

            public TestableModelConverter(ImportedFrame rootFrame) : base(ImageFormat.Png)
            {
                RootFrame = rootFrame;
            }

            public void RegisterMorphChannel(MeshBlendShapeChannel ch, string cleanName) => RegMorphMethod.Invoke(this, new object[] { ch, cleanName });
            public void RegisterBonePath(uint hash, string path) => ((Dictionary<uint, string>)BoneHashField.GetValue(this)!)[hash] = path;
            public void AddClipAndConvert(AnimationClip clip)
            {
                ((HashSet<AnimationClip>)ClipsField.GetValue(this)!).Add(clip);
                ConvertAnimsMethod.Invoke(this, null);
            }
            public string CallGetPathFromHash(uint hash) => (string)GetPathHashMethod.Invoke(this, new object[] { hash })!;
            public Avatar? CallFindAvatar(GameObject go) => (Avatar?)FindAvatarMethod.Invoke(this, new object[] { go });
        }

        private static Avatar BuildSyntheticHumanoidAvatar(float scale, string avatarName = "SyntheticHumanoidAvatar")
        {
            // AvatarSkeleton: 0="", 1="Root" (-90 deg X), 2="Root/Hips", 3="Root/Hips/Spine",
            //                 4="Root/Hips/Spine/LeftLowerArm", 5="Root/Hips/Spine/LeftEye",
            //                 6="Root/Hips/Spine/LeftLowerArm/LeftThumb1"
            string[] paths =
            {
                "",
                "Root",
                "Root/Hips",
                "Root/Hips/Spine",
                "Root/Hips/Spine/LeftLowerArm",
                "Root/Hips/Spine/LeftEye",
                "Root/Hips/Spine/LeftLowerArm/LeftThumb1",
            };
            int[] avParents = { -1, 0, 1, 2, 3, 3, 4 };
            var avIds = paths.Select(p => string.IsNullOrEmpty(p) ? 0u : Crc32(p)).ToArray();

            var avSkel = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            avSkel.m_Node = avParents.Select(p => MakeNode(p, -1)).ToArray();
            avSkel.m_ID = avIds;
            avSkel.m_AxesArray = Array.Empty<Axes>();

            var rotNeg90X = new ASQuat(-MathF.Sqrt(0.5f), 0f, 0f, MathF.Sqrt(0.5f));
            var rotPos90X = new ASQuat(MathF.Sqrt(0.5f), 0f, 0f, MathF.Sqrt(0.5f));
            var identityQ = new ASQuat(0f, 0f, 0f, 1f);
            var defPose = (SkeletonPose)RuntimeHelpers.GetUninitializedObject(typeof(SkeletonPose));
            defPose.m_X = new[]
            {
                MakeXForm(ASVec3.Zero, identityQ, ASVec3.One),
                MakeXForm(ASVec3.Zero, rotNeg90X, ASVec3.One), // Parent "Root" rotated -90 deg X
                MakeXForm(ASVec3.Zero, rotPos90X, ASVec3.One), // Pelvis cancels it -> Avatar Identity!
                MakeXForm(new ASVec3(0, 0.2f, 0), identityQ, ASVec3.One),
                MakeXForm(new ASVec3(-0.3f, 0.2f, 0), identityQ, ASVec3.One),
                MakeXForm(new ASVec3(-0.05f, 0.5f, 0.1f), identityQ, ASVec3.One),
                MakeXForm(new ASVec3(-0.2f, 0f, 0f), identityQ, ASVec3.One),
            };

            // HumanSkeleton: 0=root(av0), 1=Hips(av2), 2=Spine(av3), 3=LeftLowerArm(av4), 4=LeftEye(av5), 5=LeftThumb1(av6)
            var hSkel = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            hSkel.m_Node = new[]
            {
                MakeNode(-1, -1),
                MakeNode(0, 0), // Hips
                MakeNode(1, 1), // Spine
                MakeNode(2, 2), // LeftLowerArm
                MakeNode(2, 3), // LeftEye
                MakeNode(3, 4), // LeftThumb1
            };
            hSkel.m_ID = new uint[6];
            hSkel.m_AxesArray = new[]
            {
                MakeAxes(identityQ, identityQ, new ASVec3(-40, -40, -40), new ASVec3(40, 40, 40)), // Hips
                MakeAxes(identityQ, identityQ, new ASVec3(-40, -40, -40), new ASVec3(40, 40, 40)), // Spine
                MakeAxes(identityQ, identityQ, new ASVec3(-90, 0, -80),   new ASVec3(90, 0, 80)),  // LeftLowerArm
                MakeAxes(identityQ, identityQ, new ASVec3(0, -20, -10),   new ASVec3(0, 20, 15)),  // LeftEye
                MakeAxes(identityQ, identityQ, new ASVec3(0, -25, -20),   new ASVec3(0, 25, 20)),  // LeftThumb1
            };

            var hPose = (SkeletonPose)RuntimeHelpers.GetUninitializedObject(typeof(SkeletonPose));
            hPose.m_X = Enumerable.Range(0, 6).Select(_ => MakeXForm(ASVec3.Zero, identityQ, ASVec3.One)).ToArray();

            var bodyIdx = new int[25];
            Array.Fill(bodyIdx, -1);
            bodyIdx[0] = 1;  // Hips
            bodyIdx[7] = 2;  // Spine
            bodyIdx[16] = 3; // LeftLowerArm
            bodyIdx[22] = 4; // LeftEye

            var lHand = (Hand)RuntimeHelpers.GetUninitializedObject(typeof(Hand));
            lHand.m_HandBoneIndex = new int[15];
            Array.Fill(lHand.m_HandBoneIndex, -1);
            lHand.m_HandBoneIndex[0] = 5; // LeftThumb1

            var rHand = (Hand)RuntimeHelpers.GetUninitializedObject(typeof(Hand));
            rHand.m_HandBoneIndex = new int[15];
            Array.Fill(rHand.m_HandBoneIndex, -1);

            var human = (Human)RuntimeHelpers.GetUninitializedObject(typeof(Human));
            human.m_RootX = MakeXForm(ASVec3.Zero, identityQ, ASVec3.One);
            human.m_Skeleton = hSkel;
            human.m_SkeletonPose = hPose;
            human.m_LeftHand = lHand;
            human.m_RightHand = rHand;
            human.m_HumanBoneIndex = bodyIdx;
            human.m_HumanBoneMass = Array.Empty<float>();
            human.m_Scale = scale;

            var avConst = (AvatarConstant)RuntimeHelpers.GetUninitializedObject(typeof(AvatarConstant));
            avConst.m_AvatarSkeleton = avSkel;
            avConst.m_AvatarSkeletonPose = defPose;
            avConst.m_DefaultPose = defPose;
            avConst.m_Human = human;
            avConst.m_HumanSkeletonIndexArray = new[] { 0, 2, 3, 4, 5, 6 };

            var avatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            avatar.m_Name = avatarName;
            avatar.m_Avatar = avConst;
            avatar.m_TOS = paths.Select((p, i) => new KeyValuePair<uint, string>(avIds[i], p)).ToArray();
            return avatar;
        }

        private static AnimationClip BuildSyntheticMuscleClip(
            string name,
            int[] version,
            float sampleRate,
            float startTime,
            float stopTime,
            int[] indexArray,
            uint[]? streamedWords,
            uint streamCurveCount,
            float[]? denseSamples,
            int denseFrameCount,
            uint denseCurveCount,
            float denseRate,
            float[]? constData,
            GenericBinding[] bindings)
        {
            var sClip = (StreamedClip)RuntimeHelpers.GetUninitializedObject(typeof(StreamedClip));
            sClip.data = streamedWords ?? Array.Empty<uint>();
            sClip.curveCount = streamCurveCount;

            var dClip = (DenseClip)RuntimeHelpers.GetUninitializedObject(typeof(DenseClip));
            dClip.m_FrameCount = denseFrameCount;
            dClip.m_CurveCount = denseCurveCount;
            dClip.m_SampleRate = denseRate;
            dClip.m_BeginTime = startTime;
            dClip.m_SampleArray = denseSamples ?? Array.Empty<float>();

            var cClip = (ConstantClip)RuntimeHelpers.GetUninitializedObject(typeof(ConstantClip));
            cClip.data = constData ?? Array.Empty<float>();

            var inner = (Clip)RuntimeHelpers.GetUninitializedObject(typeof(Clip));
            inner.m_StreamedClip = sClip;
            inner.m_DenseClip = dClip;
            inner.m_ConstantClip = cClip;

            var mc = (ClipMuscleConstant)RuntimeHelpers.GetUninitializedObject(typeof(ClipMuscleConstant));
            mc.m_Clip = inner;
            mc.m_StartTime = startTime;
            mc.m_StopTime = stopTime;
            mc.m_IndexArray = indexArray;

            var clip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            clip.m_Name = name;
            clip.version = version;
            clip.m_Legacy = false;
            clip.m_SampleRate = sampleRate;
            clip.m_MuscleClip = mc;
            clip.m_ClipBindingConstant = new AnimationClipBindingConstant { genericBindings = bindings };
            return clip;
        }

        private static uint[] BuildStreamedClipData((float time, (int idx, float c0, float c1, float c2, float c3)[] keys)[] frames)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var (time, keys) in frames)
                {
                    bw.Write(time);
                    bw.Write(keys.Length);
                    foreach (var (idx, c0, c1, c2, c3) in keys)
                    {
                        bw.Write(idx);
                        bw.Write(c0);
                        bw.Write(c1);
                        bw.Write(c2);
                        bw.Write(c3);
                    }
                }
            }
            var bytes = ms.ToArray();
            var words = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
            return words;
        }

        private static Node MakeNode(int parentId, int axesId)
        {
            var n = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
            n.m_ParentId = parentId;
            n.m_AxesId = axesId;
            return n;
        }

        private static XForm MakeXForm(ASVec3 t, ASQuat q, ASVec3 s)
        {
            var xf = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
            xf.t = t;
            xf.q = q;
            xf.s = s;
            return xf;
        }

        private static Axes MakeAxes(ASQuat preQ, ASQuat postQ, ASVec3 minDeg, ASVec3 maxDeg)
        {
            var lim = (Limit)RuntimeHelpers.GetUninitializedObject(typeof(Limit));
            lim.m_Min = new ASVec3(Deg2Rad(minDeg.X), Deg2Rad(minDeg.Y), Deg2Rad(minDeg.Z));
            lim.m_Max = new ASVec3(Deg2Rad(maxDeg.X), Deg2Rad(maxDeg.Y), Deg2Rad(maxDeg.Z));

            var ax = (Axes)RuntimeHelpers.GetUninitializedObject(typeof(Axes));
            ax.m_PreQ = new AssetStudio.Vector4(preQ.X, preQ.Y, preQ.Z, preQ.W);
            ax.m_PostQ = new AssetStudio.Vector4(postQ.X, postQ.Y, postQ.Z, postQ.W);
            ax.m_Sgn = new ASVec3(1f, 1f, 1f);
            ax.m_Limit = lim;
            ax.m_Length = 0.2f;
            ax.m_Type = 1;
            return ax;
        }

        private static uint Crc32(string s)
        {
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(s);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
        }

        private static float Deg2Rad(float d) => (float)(d * Math.PI / 180.0);

        private static double QuatAngleErrorDeg(ASQuat a, ASQuat b)
        {
            a = HumanoidMuscleDecoder.NormalizeQuat(a);
            b = HumanoidMuscleDecoder.NormalizeQuat(b);
            return 2.0 * Math.Acos(Math.Clamp(Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W), 0.0, 1.0)) * 180.0 / Math.PI;
        }

        private static System.Numerics.Matrix4x4 RotXCol(float deg) { float r = Deg2Rad(deg), c = MathF.Cos(r), s = MathF.Sin(r); return new(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1); }
        private static System.Numerics.Matrix4x4 RotYCol(float deg) { float r = Deg2Rad(deg), c = MathF.Cos(r), s = MathF.Sin(r); return new(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1); }
        private static System.Numerics.Matrix4x4 RotZCol(float deg) { float r = Deg2Rad(deg), c = MathF.Cos(r), s = MathF.Sin(r); return new(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1); }
        private static System.Numerics.Matrix4x4 FbxEulerXYZToColMatrix(float rx, float ry, float rz) => RotZCol(rz) * RotYCol(ry) * RotXCol(rx);
        private static System.Numerics.Matrix4x4 UnityEulerZXYToColMatrix(float rx, float ry, float rz) => RotYCol(ry) * RotXCol(rx) * RotZCol(rz);
        private static System.Numerics.Matrix4x4 ReflectX(System.Numerics.Matrix4x4 r) { var m = System.Numerics.Matrix4x4.CreateScale(-1f, 1f, 1f); return m * r * m; }

        private static System.Numerics.Matrix4x4 QuatToColMatrix(System.Numerics.Quaternion q)
        {
            q = System.Numerics.Quaternion.Normalize(q);
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            return new System.Numerics.Matrix4x4(
                1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y), 0,
                2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x), 0,
                2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y), 0,
                0, 0, 0, 1);
        }

        private static System.Numerics.Quaternion ColMatrixToQuat(System.Numerics.Matrix4x4 rCol)
            => System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(System.Numerics.Matrix4x4.Transpose(rCol)));

        private static double RotationMatrixAngleErrorDeg(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
        {
            double tr =
                (a.M11 * b.M11 + a.M21 * b.M21 + a.M31 * b.M31) +
                (a.M12 * b.M12 + a.M22 * b.M22 + a.M32 * b.M32) +
                (a.M13 * b.M13 + a.M23 * b.M23 + a.M33 * b.M33);
            return Math.Acos(Math.Clamp((tr - 1.0) * 0.5, -1.0, 1.0)) * 180.0 / Math.PI;
        }
    }
}
