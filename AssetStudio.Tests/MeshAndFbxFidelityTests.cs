using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.ViewModels;
using UkooLabs.FbxSharpie;
using UkooLabs.FbxSharpie.Tokens;
using UkooLabs.FbxSharpie.Tokens.Value;
using Xunit;
using NumMat4 = System.Numerics.Matrix4x4;
using NumQuat = System.Numerics.Quaternion;
using NumVec3 = System.Numerics.Vector3;

namespace AssetStudio.Tests
{
    public class MeshAndFbxFidelityTests
    {
        private sealed class SyntheticImported : IImported
        {
            public ImportedFrame RootFrame { get; set; } = new();
            public List<ImportedMesh> MeshList { get; set; } = new();
            public List<ImportedMaterial> MaterialList { get; set; } = new();
            public List<ImportedTexture> TextureList { get; set; } = new();
            public List<ImportedKeyframedAnimation> AnimationList { get; set; } = new();
            public List<ImportedMorph> MorphList { get; set; } = new();
        }

        private sealed class TestModelConverter : ModelConverter
        {
            public TestModelConverter(ImportedFrame rootFrame, bool markHasRootFrame = true) : base(ImageFormat.Png)
            {
                RootFrame = rootFrame;
                if (markHasRootFrame)
                {
                    typeof(ModelConverter)
                        .GetField("hasRootFrame", BindingFlags.Instance | BindingFlags.NonPublic)?
                        .SetValue(this, true);
                }
            }
        }

        [Fact]
        public void DefaultFbxFormat_IsBinaryZero_InExportOptionsStateAndViewModel()
        {
            var state = new ExportOptionsState();
            Assert.Equal(0, state.FbxFormat);

            var vm = new ExportOptionsViewModel();
            Assert.Equal(0, vm.FbxFormat);
            Assert.Equal("Binary", vm.FbxFormatOptions[vm.FbxFormat]);
        }

        [Fact]
        public void FbxSharpieExporter_BinaryAndAscii_ObjectNaming_Scale100_Takes_Definitions_TimeMode_MultiUvLayers_AndBinormal()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"as_f2_fbx_fidelity_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            try
            {
                var imported = BuildSynthetic18mRigWith3UvsAndPlus02mMorph(sampleRate: 60f);

                var binPath = Path.Combine(tempRoot, "synthetic_18m_bin.fbx");
                var ascPath = Path.Combine(tempRoot, "synthetic_18m_asc.fbx");

                ModelExporter.ExportFbx(
                    binPath,
                    imported,
                    skins: true,
                    animation: true,
                    blendShape: true,
                    castToBone: false,
                    boneSize: 10f,
                    scaleFactor: 1f,
                    versionIndex: 4,
                    isAscii: false,
                    combineMeshes: false);

                ModelExporter.ExportFbx(
                    ascPath,
                    imported,
                    skins: true,
                    animation: true,
                    blendShape: true,
                    castToBone: false,
                    boneSize: 10f,
                    scaleFactor: 1f,
                    versionIndex: 4,
                    isAscii: true,
                    combineMeshes: false);

                // 1. ASCII FBX checks: zero \0\x01 binary separator bytes, canonical Class::Name object headers, Takes section present
                byte[] ascBytes = File.ReadAllBytes(ascPath);
                Assert.DoesNotContain((byte)0, ascBytes);
                string ascText = Encoding.UTF8.GetString(ascBytes);
                Assert.Contains("\"Model::SyntheticHero\"", ascText, StringComparison.Ordinal);
                Assert.Contains("\"Material::MatLower\"", ascText, StringComparison.Ordinal);
                Assert.Contains("\"AnimStack::WaveAndGrow\"", ascText, StringComparison.Ordinal);
                Assert.Contains("\"SceneInfo::GlobalInfo\"", ascText, StringComparison.Ordinal);
                Assert.Contains("\"Document::Scene\"", ascText, StringComparison.Ordinal);
                Assert.Contains("Takes:", ascText, StringComparison.Ordinal);
                Assert.Contains("LayerElementBinormal:", ascText, StringComparison.Ordinal);

                // 2. Binary FBX checks: canonical Name\0\x01Class on all objects, zero Class::Name leaks
                var binInfo = ParseBinaryFbx(binPath);
                Assert.Equal(7400, binInfo.Version);
                Assert.Equal(0, binInfo.ObjectsWithDoubleColonCount);
                Assert.True(binInfo.ObjectsWithNulSohCount >= 10);
                Assert.Contains("SyntheticHero\0\u0001Model", binInfo.ObjectNames);
                Assert.Contains("MatLower\0\u0001Material", binInfo.ObjectNames);
                Assert.Contains("WaveAndGrow\0\u0001AnimStack", binInfo.ObjectNames);
                Assert.Contains("GlobalInfo\0\u0001SceneInfo", binInfo.AllStrings);
                Assert.Contains("Scene\0\u0001Document", binInfo.AllStrings);

                // 3. UnitScaleFactor = 100.0 at default scaleFactor = 1.0, TimeMode = 14 (60fps), CustomFrameRate = 60.0
                Assert.Equal(100.0, binInfo.UnitScaleFactor, 6);
                Assert.Equal(14, binInfo.TimeMode);
                Assert.Equal(60.0, binInfo.CustomFrameRate, 6);

                // 4. Takes node attached and Definitions counts accurate (> 0 for active types)
                Assert.True(binInfo.HasTakesNode);
                Assert.True(binInfo.DefinitionsTotalCount > 10);
                Assert.True(binInfo.DefinitionCounts.GetValueOrDefault("Model") >= 4);
                Assert.True(binInfo.DefinitionCounts.GetValueOrDefault("Geometry") >= 2); // 1 Mesh + 1 Shape
                Assert.True(binInfo.DefinitionCounts.GetValueOrDefault("Material") == 2);
                Assert.True(binInfo.DefinitionCounts.GetValueOrDefault("Deformer") >= 4); // Skin + 2 Clusters + BlendShape + Channel
                Assert.True(binInfo.DefinitionCounts.GetValueOrDefault("Pose") == 1);
                Assert.True(binInfo.DefinitionCounts.GetValueOrDefault("AnimationStack") == 1);

                // 5. Separate Layer 0, Layer 1, Layer 2 for 3 UV channels (1 UV per Layer) + LayerElementBinormal
                Assert.Equal(new[] { 0, 1, 2 }, binInfo.LayerIndices);
                Assert.Equal(new[] { 0 }, binInfo.UvTypedIndicesByLayer[0]);
                Assert.Equal(new[] { 1 }, binInfo.UvTypedIndicesByLayer[1]);
                Assert.Equal(new[] { 2 }, binInfo.UvTypedIndicesByLayer[2]);
                Assert.True(binInfo.HasLayerElementBinormal);

                // 6. Skin cluster Mode == "Total1" (not "TotalOne") and Creator == "AssetStudio"
                Assert.Contains("Total1", binInfo.ClusterModes);
                Assert.DoesNotContain("TotalOne", binInfo.ClusterModes);
                Assert.Contains("AssetStudio", binInfo.AllStrings);
                Assert.DoesNotContain("ModelExtractor - Neon Divide", binInfo.AllStrings);

                // 7. BlendShape Shape writes pure deltas (+0.2m, NOT +2.0m absolute target!)
                Assert.NotEmpty(binInfo.ShapeVertices);
                double[] shapeVerts = binInfo.ShapeVertices[0];
                Assert.Equal(12, shapeVerts.Length); // 4 top vertices * 3 doubles
                for (int i = 0; i < 4; i++)
                {
                    Assert.Equal(0.0, shapeVerts[i * 3], 5);
                    Assert.Equal(0.2, shapeVerts[i * 3 + 1], 5); // delta = +0.2m (NOT 1.8 + 0.2 = 2.0m!)
                    Assert.Equal(0.0, shapeVerts[i * 3 + 2], 5);
                }
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Fact]
        public void AutodeskBinaryWriter_NormalizesLegacyAsciiObjectNames_AndSupportsFbx7500_64BitOffsets()
        {
            var doc = new FbxDocument { Version = (FbxVersion)7500 };
            var objects = new FbxNode(new IdentifierToken("Objects"));
            var model = new FbxNode(new IdentifierToken("Model"));
            model.AddProperty(new LongToken(1001L));
            model.AddProperty(new StringToken("Model::TestNode"));
            model.AddProperty(new StringToken("Mesh"));
            objects.AddNode(model);
            doc.AddNode(objects);

            using var ms = new MemoryStream();
            AutodeskBinaryWriter.Write(doc, ms);
            byte[] bytes = ms.ToArray();

            using var br = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            string magic = Encoding.ASCII.GetString(br.ReadBytes(23));
            Assert.Equal("Kaydara FBX Binary  \0\u001a\0", magic);
            Assert.Equal(7500, br.ReadInt32());

            // 64-bit node header: ulong EndOffset (8), ulong PropCount (8), ulong PropLen (8), byte NameLen (1)
            ulong endOffset = br.ReadUInt64();
            ulong propCount = br.ReadUInt64();
            ulong propLen = br.ReadUInt64();
            byte nameLen = br.ReadByte();
            Assert.Equal("Objects", Encoding.UTF8.GetString(br.ReadBytes(nameLen)));
            Assert.Equal(0UL, propCount);
            Assert.Equal(0UL, propLen);
            Assert.True(endOffset > (ulong)br.BaseStream.Position);

            // Child Model node (64-bit framing)
            ulong childEnd = br.ReadUInt64();
            ulong childProps = br.ReadUInt64();
            ulong childPropLen = br.ReadUInt64();
            byte childNameLen = br.ReadByte();
            Assert.Equal("Model", Encoding.UTF8.GetString(br.ReadBytes(childNameLen)));
            Assert.Equal(3UL, childProps);
            Assert.True(childPropLen > 0);

            Assert.Equal((byte)'L', br.ReadByte());
            Assert.Equal(1001L, br.ReadInt64());
            Assert.Equal((byte)'S', br.ReadByte());
            int sLen = br.ReadInt32();
            string normalizedName = Encoding.UTF8.GetString(br.ReadBytes(sLen));
            Assert.Equal("TestNode\0\u0001Model", normalizedName);
            Assert.Equal((byte)'S', br.ReadByte());
            int typeLen = br.ReadInt32();
            Assert.Equal("Mesh", Encoding.UTF8.GetString(br.ReadBytes(typeLen)));
            Assert.Equal(childEnd, (ulong)br.BaseStream.Position);
        }

        [Fact]
        public void QuaternionToEuler_GimbalLockAtPlusAndMinus90DegY_HasZeroMatrixReconstructionError()
        {
            var quatToEuler = typeof(ModelConverter).GetMethod("QuaternionToEuler", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(quatToEuler);

            float[] testXAngles = { -150f, -135f, -90f, -45f, -15f, 0f, 22.5f, 30f, 45f, 75f, 90f, 135f, 165f };
            float[] testYAngles = { 90f, -90f };

            foreach (float yDeg in testYAngles)
            {
                foreach (float xDeg in testXAngles)
                {
                    var targetMat = NumMat4.CreateRotationX(DegToRad(xDeg)) * NumMat4.CreateRotationY(DegToRad(yDeg));
                    var q = NumQuat.CreateFromRotationMatrix(targetMat);
                    var euler = (Vector3)quatToEuler!.Invoke(null, new object[] { new Quaternion(q.X, q.Y, q.Z, q.W) })!;

                    var reconMat = NumMat4.CreateRotationX(DegToRad(euler.X))
                                 * NumMat4.CreateRotationY(DegToRad(euler.Y))
                                 * NumMat4.CreateRotationZ(DegToRad(euler.Z));

                    double frobErr = MatrixFrobDiff(targetMat, reconMat);
                    Assert.True(frobErr < 1e-4, $"Gimbal lock failed at Y={yDeg}, X={xDeg}: frobErr={frobErr:E3}, euler=({euler.X},{euler.Y},{euler.Z})");
                }
            }
        }

        [Fact]
        public void Mesh_OneBoneSkinWeights_VariableBoneCountWeights_BytesToIntArray_NonTriSubmesh_Unity5ColorStride_AndMeshLodInfo()
        {
            // 1. MeshHelper.BytesToIntArray signed/unsigned accuracy
            int u16 = MeshHelper.BytesToIntArray(BitConverter.GetBytes((ushort)40000), MeshHelper.VertexFormat.UInt16)[0];
            Assert.Equal(40000, u16);
            int s8 = MeshHelper.BytesToIntArray(new byte[] { unchecked((byte)(sbyte)-5) }, MeshHelper.VertexFormat.SInt8)[0];
            Assert.Equal(-5, s8);
            int u32 = MeshHelper.BytesToIntArray(BitConverter.GetBytes(300u), MeshHelper.VertexFormat.UInt32)[0];
            Assert.Equal(300, u32);

            // 2. SkinWeights.OneBone (BlendIndices dimension=1, BlendWeight dimension=0) -> weight[0] == 1.0f
            var oneBoneMesh = CreateUninitializedMesh(new[] { 2020, 3, 0 });
            var vdOne = new VertexData();
            vdOne.m_VertexCount = 2;
            vdOne.m_Channels = new ChannelInfo[14];
            for (int i = 0; i < 14; i++) vdOne.m_Channels[i] = new ChannelInfo();
            // Ch 0: Position (3 floats = 12B), Ch 13: BlendIndices (1 int32 = 4B), stride = 16B
            vdOne.m_Channels[0] = new ChannelInfo { stream = 0, offset = 0, format = 0, dimension = 3 };
            vdOne.m_Channels[13] = new ChannelInfo { stream = 0, offset = 12, format = (byte)MeshHelper.VertexFormat.SInt32, dimension = 1 };
            vdOne.m_Streams = new[]
            {
                new StreamInfo { channelMask = (1u << 0) | (1u << 13), offset = 0, stride = 16 }
            };
            vdOne.m_DataSize = new byte[32];
            Buffer.BlockCopy(BitConverter.GetBytes(5), 0, vdOne.m_DataSize, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(9), 0, vdOne.m_DataSize, 28, 4);

            SetPrivateField(oneBoneMesh, "m_VertexData", vdOne);
            oneBoneMesh.m_SubMeshes = Array.Empty<SubMesh>();
            InvokePrivateMethod(oneBoneMesh, "ReadVertexData");

            Assert.NotNull(oneBoneMesh.m_Skin);
            Assert.Equal(2, oneBoneMesh.m_Skin!.Length);
            Assert.Equal(5, oneBoneMesh.m_Skin[0].boneIndex[0]);
            Assert.Equal(1.0f, oneBoneMesh.m_Skin[0].weight[0]);
            Assert.Equal(9, oneBoneMesh.m_Skin[1].boneIndex[0]);
            Assert.Equal(1.0f, oneBoneMesh.m_Skin[1].weight[0]);

            // 3. Variable bone count weights (>4 weights per vertex via m_VariableBoneCountWeights)
            var varBoneMesh = CreateUninitializedMesh(new[] { 2022, 3, 0 });
            var vdVar = new VertexData();
            vdVar.m_VertexCount = 1;
            vdVar.m_Channels = new ChannelInfo[14];
            for (int i = 0; i < 14; i++) vdVar.m_Channels[i] = new ChannelInfo();
            vdVar.m_Channels[0] = new ChannelInfo { stream = 0, offset = 0, format = 0, dimension = 3 };
            vdVar.m_Channels[13] = new ChannelInfo { stream = 0, offset = 12, format = (byte)MeshHelper.VertexFormat.UInt32, dimension = 2 };
            vdVar.m_Streams = new[]
            {
                new StreamInfo { channelMask = (1u << 0) | (1u << 13), offset = 0, stride = 20 }
            };
            vdVar.m_DataSize = new byte[20];
            // startOffset = 0, count = 6 influences
            Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, vdVar.m_DataSize, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(6u), 0, vdVar.m_DataSize, 16, 4);

            varBoneMesh.m_VariableBoneCountWeights = new uint[12];
            float[] expectedWeights = { 0.30f, 0.25f, 0.15f, 0.12f, 0.10f, 0.08f };
            for (int k = 0; k < 6; k++)
            {
                varBoneMesh.m_VariableBoneCountWeights[k * 2] = (uint)(k + 1);
                varBoneMesh.m_VariableBoneCountWeights[k * 2 + 1] = BitConverter.SingleToUInt32Bits(expectedWeights[k]);
            }
            SetPrivateField(varBoneMesh, "m_VertexData", vdVar);
            varBoneMesh.m_SubMeshes = Array.Empty<SubMesh>();
            InvokePrivateMethod(varBoneMesh, "ReadVertexData");

            Assert.NotNull(varBoneMesh.m_Skin);
            Assert.Equal(6, varBoneMesh.m_Skin![0].boneIndex.Length);
            Assert.Equal(6, varBoneMesh.m_Skin[0].weight.Length);
            for (int k = 0; k < 6; k++)
            {
                Assert.Equal(k + 1, varBoneMesh.m_Skin[0].boneIndex[k]);
                Assert.Equal(expectedWeights[k], varBoneMesh.m_Skin[0].weight[k], 5);
            }

            // 4. Non-polygon submesh (Lines/Points) skipped gracefully without throwing NotSupportedException
            var lineSubMesh = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            lineSubMesh.firstByte = 0;
            lineSubMesh.indexCount = 4;
            lineSubMesh.topology = GfxPrimitiveType.Lines;
            oneBoneMesh.m_SubMeshes = new[] { lineSubMesh };
            InvokePrivateMethod(oneBoneMesh, "GetTriangles");
            Assert.Equal(0u, lineSubMesh.indexCount);

            // 5. Unity 5.x VertexData.GetStreams Color (chn=2, format=2, dimension=1) sets dimension=4 before stride
            var vd5 = (VertexData)RuntimeHelpers.GetUninitializedObject(typeof(VertexData));
            vd5.m_VertexCount = 1;
            vd5.m_Channels = new[]
            {
                new ChannelInfo { stream = 0, offset = 0, format = 0, dimension = 3 }, // 12 bytes
                new ChannelInfo { stream = 0, offset = 12, format = 0, dimension = 0 },
                new ChannelInfo { stream = 0, offset = 12, format = 2, dimension = 1 } // Color format=2 -> 4 bytes
            };
            InvokePrivateMethod(vd5, "GetStreams", new object[] { new[] { 5, 3, 1 } });
            Assert.Equal((byte)4, vd5.m_Channels[2].dimension);
            Assert.Equal(16u, vd5.m_Streams[0].stride);
        }

        [Fact]
        public void TangentHandedness_4DUvStride_DuplicateBoneSlotMerge_AndVariableWeightsInFbx()
        {
            var mesh = CreateUninitializedMesh(new[] { 2022, 3, 0 });
            SetPrivateField(mesh, "m_DataProcessed", true);
            mesh.m_Name = "TangentUv4DMesh";
            mesh.m_VertexCount = 3;
            // Triangle in Unity LH space: (0,0,0), (0,1,0), (1,0,0) with Normal (0,0,-1), UV0 (0,0)->(0,1)->(1,0)
            // Tangent in Unity along +X = (1,0,0) with W = -1 -> Bitangent in Unity = Cross((0,0,-1), (1,0,0)) * (-1) = (0,-1,0)*(-1) = (0,1,0) (+V)
            mesh.m_Vertices = new float[]
            {
                0f, 0f, 0f,
                0f, 1f, 0f,
                1f, 0f, 0f
            };
            mesh.m_Normals = new float[]
            {
                0f, 0f, -1f,
                0f, 0f, -1f,
                0f, 0f, -1f
            };
            mesh.m_Tangents = new float[]
            {
                1f, 0f, 0f, -1f,
                1f, 0f, 0f, -1f,
                1f, 0f, 0f, -1f
            };
            // UV0 is 4D (stride 4): [u, v, z, w] per vertex
            mesh.m_UV0 = new float[]
            {
                0.0f, 0.0f, 99.0f, 88.0f,
                0.0f, 1.0f, 77.0f, 66.0f,
                1.0f, 0.0f, 55.0f, 44.0f
            };
            // UV1 is 2D (stride 2)
            mesh.m_UV1 = new float[]
            {
                0.1f, 0.2f,
                0.3f, 0.4f,
                0.5f, 0.6f
            };
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            var sub = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub.firstByte = 0;
            sub.indexCount = 3;
            sub.firstVertex = 0;
            sub.vertexCount = 3;
            sub.baseVertex = 0;
            mesh.m_SubMeshes = new[] { sub };

            // 6 bone influences on vertex 0 with duplicate bone 0 in slots 0 and 2 (0.20 + 0.15 = 0.35), plus bones 1, 2, 3, 4
            mesh.m_Skin = new BoneWeights4[3];
            for (int v = 0; v < 3; v++)
            {
                mesh.m_Skin[v] = new BoneWeights4
                {
                    boneIndex = new[] { 0, 1, 0, 2, 3, 4 },
                    weight = new[] { 0.20f, 0.25f, 0.15f, 0.15f, 0.15f, 0.10f }
                };
            }
            mesh.m_BindPose = new[]
            {
                MakeIdentityMatrix(),
                MakeIdentityMatrix(),
                MakeIdentityMatrix(),
                MakeIdentityMatrix(),
                MakeIdentityMatrix()
            };
            mesh.m_BoneNameHashes = new uint[] { 10, 20, 30, 40, 50 };

            var converter = ModelConverter.CreateFromModelGroup(
                "Root",
                rootGameObject: null,
                meshes: new[] { mesh },
                renderers: null,
                avatar: null,
                animationClips: null,
                imageFormat: ImageFormat.Png,
                combineMeshes: false);

            var iMesh = Assert.Single(converter.MeshList);

            // 1. Verify 4D UV0 and 2D UV1 were extracted with exact per-channel stride
            Assert.Equal(0.0f, iMesh.VertexList[0].UV[0][0], 5);
            Assert.Equal(0.0f, iMesh.VertexList[0].UV[0][1], 5);
            Assert.Equal(0.0f, iMesh.VertexList[1].UV[0][0], 5);
            Assert.Equal(1.0f, iMesh.VertexList[1].UV[0][1], 5);
            Assert.Equal(1.0f, iMesh.VertexList[2].UV[0][0], 5);
            Assert.Equal(0.0f, iMesh.VertexList[2].UV[0][1], 5);

            Assert.Equal(0.3f, iMesh.VertexList[1].UV[1][0], 5);
            Assert.Equal(0.4f, iMesh.VertexList[1].UV[1][1], 5);

            // 2. Verify Tangent.w was negated (-1 -> +1) and Cross(N, T) * T.W matches the RH UV bitangent (+V = (0, 1, 0))
            var v0 = iMesh.VertexList[0];
            Assert.Equal(1.0f, v0.Tangent.W, 5);
            var n = new NumVec3(v0.Normal.X, v0.Normal.Y, v0.Normal.Z);
            var t = new NumVec3(v0.Tangent.X, v0.Tangent.Y, v0.Tangent.Z);
            var bitangent = NumVec3.Cross(n, t) * v0.Tangent.W;
            float dotWithPlusY = NumVec3.Dot(NumVec3.Normalize(bitangent), new NumVec3(0f, 1f, 0f));
            Assert.True(dotWithPlusY > 0.999f, $"Expected bitangent dot > 0.999, got {dotWithPlusY}");

            // 3. Verify all 6 bone weight slots propagated and duplicate bone 0 slots merge cleanly in FBX export
            Assert.Equal(6, v0.BoneIndices.Length);
            Assert.Equal(6, v0.Weights.Length);

            for (int b = 0; b < 5; b++)
            {
                var boneFrame = new ImportedFrame
                {
                    Name = $"Bone_{b}",
                    LocalPosition = Vector3.Zero,
                    LocalRotation = Vector3.Zero,
                    LocalScale = Vector3.One
                };
                converter.RootFrame.AddChild(boneFrame);
                iMesh.BoneList![b].Path = boneFrame.Path;
            }

            var tempFbx = Path.Combine(Path.GetTempPath(), $"as_f2_skin6_{Guid.NewGuid():N}.fbx");
            try
            {
                ModelExporter.ExportFbx(tempFbx, converter, skins: true, animation: false, blendShape: false, castToBone: false, boneSize: 10f, scaleFactor: 1f, versionIndex: 4, isAscii: false);
                var binInfo = ParseBinaryFbx(tempFbx);
                // All 5 bones (>4 weights per vertex!) must have active clusters
                Assert.Equal(5, binInfo.ClusterWeights.Count);
                // Bone 0 had duplicate slots (0.20 + 0.15 = 0.35) on each of the 3 vertices -> deduplicated to 3 indices (NOT 6) with summed weight 0.35!
                Assert.Equal(new[] { 0, 1, 2 }, binInfo.ClusterIndices[0]);
                Assert.All(binInfo.ClusterWeights[0], w => Assert.Equal(0.35, w, 5));
                // 5th bone (index 4, from slot 5 > 4-weight cap!) has weight 0.10 on all 3 vertices
                Assert.Equal(new[] { 0, 1, 2 }, binInfo.ClusterIndices[4]);
                Assert.All(binInfo.ClusterWeights[4], w => Assert.Equal(0.10, w, 5));
            }
            finally
            {
                if (File.Exists(tempFbx)) File.Delete(tempFbx);
                var report = Path.Combine(Path.GetDirectoryName(tempFbx)!, Path.GetFileNameWithoutExtension(tempFbx) + ".fbx-export-report.txt");
                if (File.Exists(report)) File.Delete(report);
            }
        }

        [Fact]
        public void CombineMeshes_AcyclicSkinnedHierarchy_AndRigidChildMeshWeightedToParentBone()
        {
            var root = new ImportedFrame { Name = "BossRoot", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var hips = new ImportedFrame { Name = "Hips", LocalPosition = new Vector3(0f, 1f, 0f), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var hand = new ImportedFrame { Name = "RightHand", LocalPosition = new Vector3(0.8f, 0.5f, 0f), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var swordFrame = new ImportedFrame { Name = "SwordMesh", LocalPosition = new Vector3(0f, 0.2f, 0f), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var bodyFrame = new ImportedFrame { Name = "BodyMesh", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = Vector3.One };

            root.AddChild(hips);
            hips.AddChild(hand);
            hand.AddChild(swordFrame);
            root.AddChild(bodyFrame);

            var bodyMesh = new ImportedMesh
            {
                Path = bodyFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    new() { Vertex = new Vector3(0f, 0f, 0f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][], BoneIndices = new[] { 0, 1, 0, 0 }, Weights = new[] { 0.5f, 0.5f, 0f, 0f } },
                    new() { Vertex = new Vector3(1f, 0f, 0f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][], BoneIndices = new[] { 0, 1, 0, 0 }, Weights = new[] { 0.5f, 0.5f, 0f, 0f } },
                    new() { Vertex = new Vector3(0f, 1f, 0f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][], BoneIndices = new[] { 0, 1, 0, 0 }, Weights = new[] { 0.5f, 0.5f, 0f, 0f } }
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "BodyMat", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                },
                BoneList = new List<ImportedBone>
                {
                    new() { Path = hips.Path, Matrix = MakeTranslationMatrix(0f, -1f, 0f) },
                    new() { Path = hand.Path, Matrix = MakeTranslationMatrix(-0.8f, -1.5f, 0f) }
                }
            };

            // Rigid unskinned Sword mesh parented under RightHand bone
            var swordMesh = new ImportedMesh
            {
                Path = swordFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    new() { Vertex = new Vector3(0f, 0f, 0f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][] },
                    new() { Vertex = new Vector3(0f, 1f, 0f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][] },
                    new() { Vertex = new Vector3(0.1f, 0f, 0f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][] }
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "SwordMat", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                }
            };

            var converter = new TestModelConverter(root, markHasRootFrame: true);
            converter.MeshList.Add(bodyMesh);
            converter.MeshList.Add(swordMesh);

            converter.CombineMeshes();

            var combined = Assert.Single(converter.MeshList);
            // 1. Combined skinned mesh is placed on a dedicated sibling child frame under BossRoot, NOT on BossRoot itself!
            Assert.NotEqual("BossRoot", combined.Path);
            Assert.Equal("BossRoot/CombinedMesh", combined.Path);
            var combinedFrame = root.FindFrameByPath(combined.Path);
            Assert.NotNull(combinedFrame);
            Assert.Equal(0, combinedFrame!.Count); // Acyclic: combined mesh frame has 0 bone children!

            // 2. Rigid child mesh vertices (indices 3..5) are bound with weight 1.0 to RightHand (bone index 1)!
            for (int v = 3; v < 6; v++)
            {
                Assert.Equal(1, combined.VertexList[v].BoneIndices[0]);
                Assert.Equal(1.0f, combined.VertexList[v].Weights[0], 5);
            }
        }

        [Fact]
        public void StaticBatching_UnbakesWorldVerticesWithoutMutatingSharedParentOrSiblingFrames()
        {
            // Scene hierarchy:
            // Background (pos = (100, 50, -25))
            //  ├── BatchedWall (pos = (10, 0, 0)) -> static-batched MeshRenderer (vertices baked in Unity world space)
            //  └── CharacterRig (pos = (5, 2, 0)) -> sibling under Background whose parent transform MUST NOT be mutated!
            var root = new ImportedFrame { Name = "Background", LocalPosition = new Vector3(-100f, 50f, -25f), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var wallFrame = new ImportedFrame { Name = "BatchedWall", LocalPosition = new Vector3(-10f, 0f, 0f), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var siblingFrame = new ImportedFrame { Name = "CharacterRig", LocalPosition = new Vector3(-5f, 2f, 0f), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            root.AddChild(wallFrame);
            root.AddChild(siblingFrame);

            // World-space vertex at (-112, 53, -20) in FBX RH space -> local to BatchedWall (global (-110, 50, -25)) is (-2, 3, 5)
            var batchedMesh = new ImportedMesh
            {
                Path = wallFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    new() { Vertex = new Vector3(-112f, 53f, -20f), Normal = new Vector3(0f, 1f, 0f), UV = new float[8][] }
                },
                SubmeshList = new List<ImportedSubmesh>()
            };

            var converter = new TestModelConverter(root);
            InvokePrivateMethod(converter, "UnbakeStaticBatchedMeshToLocalSpace", new object[] { batchedMesh });

            // 1. Neither Background nor BatchedWall nor CharacterRig had their transforms mutated!
            Assert.Equal(new Vector3(-100f, 50f, -25f), root.LocalPosition);
            Assert.Equal(new Vector3(-10f, 0f, 0f), wallFrame.LocalPosition);
            Assert.Equal(new Vector3(-5f, 2f, 0f), siblingFrame.LocalPosition);

            // 2. Batched vertex was un-baked into BatchedWall's local space (-2, 3, 5), so local + global = (-112, 53, -20)!
            Assert.Equal(-2f, batchedMesh.VertexList[0].Vertex.X, 4);
            Assert.Equal(3f, batchedMesh.VertexList[0].Vertex.Y, 4);
            Assert.Equal(5f, batchedMesh.VertexList[0].Vertex.Z, 4);
        }

        [Fact]
        public void FindFrameByPath_RequiresSlashBoundary_AndBlendShapeChannelNamePreservesDots()
        {
            var root = new ImportedFrame { Name = "Root" };
            var bigA = new ImportedFrame { Name = "BigA" };
            var a = new ImportedFrame { Name = "A" };
            var wrongHip = new ImportedFrame { Name = "Hip" };
            var rightHip = new ImportedFrame { Name = "Hip" };
            bigA.AddChild(wrongHip);
            a.AddChild(rightHip);
            root.AddChild(bigA);
            root.AddChild(a);

            var resolved = root.FindFrameByPath("A/Hip");
            Assert.Same(rightHip, resolved);
            Assert.Equal("Root/A/Hip", resolved!.Path);

            var normMethod = typeof(ModelConverter).GetMethod("NormalizeBlendShapeChannelName", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(normMethod);
            Assert.Equal("Face.Left.Smile", (string)normMethod!.Invoke(null, new object[] { "blendShape.Face.Left.Smile" })!);
            Assert.Equal("Face.Right.Smile", (string)normMethod.Invoke(null, new object[] { "Face.Right.Smile" })!);
        }

        [Fact]
        public void DeoptimizeTransformHierarchy_BuildsExactRestPoseFromDefaultPose_WithoutBogusBone0()
        {
            var root = new ImportedFrame
            {
                Name = "MarionetteRoot",
                LocalPosition = new Vector3(10f, 20f, 30f),
                LocalRotation = Vector3.Zero,
                LocalScale = Vector3.One
            };

            var skel = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            var n0 = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
            n0.m_ParentId = -1;
            var n1 = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
            n1.m_ParentId = 0;
            var n2 = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
            n2.m_ParentId = 1;
            skel.m_Node = new[] { n0, n1, n2 };
            skel.m_ID = new uint[] { 0u, 111u, 222u };

            var pose = (SkeletonPose)RuntimeHelpers.GetUninitializedObject(typeof(SkeletonPose));
            var x0 = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
            x0.t = Vector3.Zero; x0.q = new Quaternion(0f, 0f, 0f, 1f); x0.s = Vector3.One;
            var x1 = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
            x1.t = new Vector3(0f, 1.0f, 0f); x1.q = new Quaternion(0f, 0f, 0f, 1f); x1.s = Vector3.One;
            var x2 = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
            x2.t = new Vector3(0f, 0.5f, 0f); x2.q = new Quaternion(0f, 0f, 0f, 1f); x2.s = Vector3.One;
            pose.m_X = new[] { x0, x1, x2 };

            var converter = new TestModelConverter(root);
            InvokePrivateMethod(converter, "BuildSkeletonFramesFromDefaultPose", new object?[] { skel, pose, null });

            // Node 0 (m_ParentId = -1, id = 0) mapped to RootFrame without creating a bogus "Bone_0" child!
            Assert.Null(root.FindChild("Bone_0", recursive: false));
            var bone1 = root.FindChild("Bone_111", recursive: false);
            Assert.NotNull(bone1);
            Assert.Equal(new Vector3(0f, 1.0f, 0f), bone1!.LocalPosition);
            var bone2 = bone1.FindChild("Bone_222", recursive: false);
            Assert.NotNull(bone2);
            Assert.Equal(new Vector3(0f, 0.5f, 0f), bone2!.LocalPosition);
        }

        private static SyntheticImported BuildSynthetic18mRigWith3UvsAndPlus02mMorph(float sampleRate)
        {
            var root = new ImportedFrame { Name = "SyntheticHero", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var hips = new ImportedFrame { Name = "Hips", LocalPosition = new Vector3(0, 0.9f, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var head = new ImportedFrame { Name = "Head", LocalPosition = new Vector3(0, 0.9f, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var bodyFrame = new ImportedFrame { Name = "Body", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            root.AddChild(hips);
            hips.AddChild(head);
            root.AddChild(bodyFrame);

            var coords = new[]
            {
                new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.25f, 0f, -0.25f),
                new Vector3(0.25f, 0f,  0.25f), new Vector3(-0.25f, 0f,  0.25f),
                new Vector3(-0.25f, 1.8f, -0.25f), new Vector3(0.25f, 1.8f, -0.25f),
                new Vector3(0.25f, 1.8f,  0.25f), new Vector3(-0.25f, 1.8f,  0.25f),
            };
            var verts = new List<ImportedVertex>();
            for (int i = 0; i < 8; i++)
            {
                bool top = i >= 4;
                var uv = new float[8][];
                uv[0] = new[] { (i % 2) * 1.0f, top ? 1.0f : 0.0f };
                uv[1] = new[] { (i % 2) * 0.5f, top ? 0.5f : 0.0f };
                uv[2] = new[] { 0.25f, 0.75f };
                verts.Add(new ImportedVertex
                {
                    Vertex = coords[i],
                    Normal = new Vector3(0, top ? 1 : -1, 0),
                    Tangent = new Vector4(1, 0, 0, 1),
                    Color = new Color(top ? 1f : 0.2f, 0.5f, 0.8f, 1f),
                    UV = uv,
                    BoneIndices = new[] { top ? 1 : 0, 0, 0, 0 },
                    Weights = new[] { 1.0f, 0f, 0f, 0f }
                });
            }

            int[,] tris =
            {
                {0,1,2},{0,2,3}, {4,6,5},{4,7,6},
                {0,4,5},{0,5,1}, {1,5,6},{1,6,2},
                {2,6,7},{2,7,3}, {3,7,4},{3,4,0}
            };
            var faces0 = new List<ImportedFace>();
            var faces1 = new List<ImportedFace>();
            for (int t = 0; t < 12; t++)
            {
                var f = new ImportedFace { VertexIndices = new[] { tris[t, 0], tris[t, 1], tris[t, 2] } };
                if (t < 6) faces0.Add(f); else faces1.Add(f);
            }

            var mesh = new ImportedMesh
            {
                Path = bodyFrame.Path,
                hasNormal = true,
                hasTangent = true,
                hasColor = true,
                hasUV = new[] { true, true, true, false, false, false, false, false },
                VertexList = verts,
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "MatLower", FaceList = faces0 },
                    new() { Material = "MatUpper", FaceList = faces1 }
                },
                BoneList = new List<ImportedBone>
                {
                    new() { Path = hips.Path, Matrix = MakeTranslationMatrix(0, -0.9f, 0) },
                    new() { Path = head.Path, Matrix = MakeTranslationMatrix(0, -1.8f, 0) }
                }
            };

            var morphVerts = new List<ImportedMorphVertex>();
            for (uint i = 4; i < 8; i++)
            {
                var baseV = verts[(int)i].Vertex;
                morphVerts.Add(new ImportedMorphVertex
                {
                    Index = i,
                    Vertex = new ImportedVertex
                    {
                        Vertex = baseV + new Vector3(0f, 0.2f, 0f),
                        Normal = Vector3.Zero
                    }
                });
            }

            var morph = new ImportedMorph
            {
                Path = bodyFrame.Path,
                Channels = new List<ImportedMorphChannel>
                {
                    new()
                    {
                        Name = "GrowTop_Plus02m",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new() { Weight = 100f, hasNormals = true, VertexList = morphVerts }
                        }
                    }
                }
            };

            var anim = new ImportedKeyframedAnimation
            {
                Name = "WaveAndGrow",
                SampleRate = sampleRate,
                TrackList = new List<ImportedAnimationKeyframedTrack>
                {
                    new()
                    {
                        Path = "", // Root track path "" -> maps to SyntheticHero root frame!
                        Translations = new List<ImportedKeyframe<Vector3>>
                        {
                            new(0f, Vector3.Zero),
                            new(1f, Vector3.Zero)
                        }
                    },
                    new()
                    {
                        Path = head.Path,
                        Rotations = new List<ImportedKeyframe<Vector3>>
                        {
                            new(0f, Vector3.Zero),
                            new(0.5f, new Vector3(0f, 0f, 45f)),
                            new(1.0f, Vector3.Zero)
                        }
                    },
                    new()
                    {
                        Path = bodyFrame.Path,
                        BlendShape = new ImportedBlendShape
                        {
                            ChannelName = "GrowTop_Plus02m",
                            Keyframes = new List<ImportedKeyframe<float>>
                            {
                                new(0f, 0f),
                                new(0.5f, 100f),
                                new(1.0f, 0f)
                            }
                        }
                    }
                }
            };

            return new SyntheticImported
            {
                RootFrame = root,
                MeshList = new List<ImportedMesh> { mesh },
                MaterialList = new List<ImportedMaterial>
                {
                    new()
                    {
                        Name = "MatLower",
                        Diffuse = new Color(0.8f, 0.2f, 0.2f, 1f),
                        Ambient = new Color(0.1f, 0.1f, 0.1f, 1f),
                        Specular = new Color(0.2f, 0.2f, 0.2f, 1f),
                        Emissive = new Color(0f, 0f, 0f, 1f),
                        Shininess = 20f,
                        Transparency = 0.25f,
                        Textures = new List<ImportedMaterialTexture>()
                    },
                    new()
                    {
                        Name = "MatUpper",
                        Diffuse = new Color(0.2f, 0.2f, 0.8f, 1f),
                        Ambient = new Color(0.1f, 0.1f, 0.1f, 1f),
                        Specular = new Color(0.2f, 0.2f, 0.2f, 1f),
                        Emissive = new Color(0f, 0f, 0f, 1f),
                        Shininess = 20f,
                        Textures = new List<ImportedMaterialTexture>()
                    }
                },
                TextureList = new List<ImportedTexture>(),
                MorphList = new List<ImportedMorph> { morph },
                AnimationList = new List<ImportedKeyframedAnimation> { anim }
            };
        }

        private sealed class ParsedBinaryFbx
        {
            public int Version { get; set; }
            public int ObjectsWithDoubleColonCount { get; set; }
            public int ObjectsWithNulSohCount { get; set; }
            public List<string> ObjectNames { get; } = new();
            public HashSet<string> AllStrings { get; } = new(StringComparer.Ordinal);
            public double UnitScaleFactor { get; set; }
            public int TimeMode { get; set; }
            public double CustomFrameRate { get; set; }
            public bool HasTakesNode { get; set; }
            public int DefinitionsTotalCount { get; set; }
            public Dictionary<string, int> DefinitionCounts { get; } = new(StringComparer.Ordinal);
            public List<int> LayerIndices { get; } = new();
            public Dictionary<int, List<int>> UvTypedIndicesByLayer { get; } = new();
            public bool HasLayerElementBinormal { get; set; }
            public HashSet<string> ClusterModes { get; } = new(StringComparer.Ordinal);
            public List<double[]> ShapeVertices { get; } = new();
            public List<int[]> ClusterIndices { get; } = new();
            public List<double[]> ClusterWeights { get; } = new();
        }

        private static ParsedBinaryFbx ParseBinaryFbx(string path)
        {
            var info = new ParsedBinaryFbx();
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8);

            string magic = Encoding.ASCII.GetString(br.ReadBytes(23));
            Assert.Equal("Kaydara FBX Binary  \0\u001a\0", magic);
            info.Version = br.ReadInt32();

            while (fs.Position < fs.Length)
            {
                if (!ReadBinaryFbxNode(br, info, parentName: null, depth: 0, contextObjType: null))
                {
                    break;
                }
            }

            return info;
        }

        private static bool ReadBinaryFbxNode(BinaryReader br, ParsedBinaryFbx info, string? parentName, int depth, string? contextObjType)
        {
            uint endOffset = br.ReadUInt32();
            uint numProps = br.ReadUInt32();
            uint propListLen = br.ReadUInt32();
            byte nameLen = br.ReadByte();
            if (endOffset == 0 && numProps == 0 && propListLen == 0 && nameLen == 0)
            {
                return false;
            }

            string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var props = new List<object>();

            for (uint p = 0; p < numProps; p++)
            {
                char tc = (char)br.ReadByte();
                switch (tc)
                {
                    case 'C': props.Add(br.ReadByte() != 0); break;
                    case 'Y': props.Add(br.ReadInt16()); break;
                    case 'I': props.Add(br.ReadInt32()); break;
                    case 'L': props.Add(br.ReadInt64()); break;
                    case 'F': props.Add(br.ReadSingle()); break;
                    case 'D': props.Add(br.ReadDouble()); break;
                    case 'S':
                    {
                        int len = br.ReadInt32();
                        string s = Encoding.UTF8.GetString(br.ReadBytes(len));
                        info.AllStrings.Add(s);
                        props.Add(s);
                        break;
                    }
                    case 'R':
                    {
                        int len = br.ReadInt32();
                        props.Add(br.ReadBytes(len));
                        break;
                    }
                    case 'd':
                    case 'f':
                    case 'i':
                    case 'l':
                    case 'b':
                    {
                        int elemCount = br.ReadInt32();
                        int enc = br.ReadInt32();
                        int compLen = br.ReadInt32();
                        byte[] raw = br.ReadBytes(compLen);
                        if (enc == 1)
                        {
                            using var ms = new MemoryStream(raw);
                            using var z = new ZLibStream(ms, CompressionMode.Decompress);
                            using var outMs = new MemoryStream();
                            z.CopyTo(outMs);
                            raw = outMs.ToArray();
                        }
                        if (tc == 'd')
                        {
                            var arr = new double[elemCount];
                            Buffer.BlockCopy(raw, 0, arr, 0, elemCount * sizeof(double));
                            props.Add(arr);
                        }
                        else if (tc == 'i')
                        {
                            var arr = new int[elemCount];
                            Buffer.BlockCopy(raw, 0, arr, 0, elemCount * sizeof(int));
                            props.Add(arr);
                        }
                        else
                        {
                            props.Add(raw);
                        }
                        break;
                    }
                }
            }

            if (depth == 0 && name == "Takes")
            {
                info.HasTakesNode = true;
            }

            if (parentName == "Definitions" && name == "Count" && props.Count > 0 && props[0] is int defTotal)
            {
                info.DefinitionsTotalCount = defTotal;
            }

            string? childContext = contextObjType;
            if (parentName == "Definitions" && name == "ObjectType" && props.Count > 0 && props[0] is string otName)
            {
                childContext = "Def:" + otName;
            }
            else if (contextObjType != null && contextObjType.StartsWith("Def:", StringComparison.Ordinal) && name == "Count" && props.Count > 0 && props[0] is int otCount)
            {
                info.DefinitionCounts[contextObjType.Substring(4)] = otCount;
            }

            if (parentName == "Objects")
            {
                if (props.Count >= 2 && props[1] is string objName)
                {
                    info.ObjectNames.Add(objName);
                    if (objName.Contains("\0\u0001", StringComparison.Ordinal))
                        info.ObjectsWithNulSohCount++;
                    else if (objName.Contains("::", StringComparison.Ordinal))
                        info.ObjectsWithDoubleColonCount++;
                }
                if (name == "Geometry" && props.Count >= 3 && props[2] as string == "Shape")
                {
                    childContext = "Shape";
                }
                else if (name == "Deformer" && props.Count >= 3 && props[2] as string == "Cluster")
                {
                    childContext = "Cluster";
                }
            }

            if (contextObjType == "Shape" && name == "Vertices" && props.Count > 0 && props[0] is double[] shapeV)
            {
                info.ShapeVertices.Add(shapeV);
            }
            if (contextObjType == "Cluster")
            {
                if (name == "Mode" && props.Count > 0 && props[0] is string mode)
                    info.ClusterModes.Add(mode);
                else if (name == "Indexes" && props.Count > 0 && props[0] is int[] cIdx)
                    info.ClusterIndices.Add(cIdx);
                else if (name == "Weights" && props.Count > 0 && props[0] is double[] cW)
                    info.ClusterWeights.Add(cW);
            }

            if (name == "LayerElementBinormal")
            {
                info.HasLayerElementBinormal = true;
            }

            if (parentName == "Properties70" && name == "P" && props.Count >= 5 && props[0] is string pName)
            {
                if (pName == "UnitScaleFactor" && props[4] is double usf)
                    info.UnitScaleFactor = usf;
                else if (pName == "TimeMode" && props[4] is int tm)
                    info.TimeMode = tm;
                else if (pName == "CustomFrameRate" && props[4] is double cfr)
                    info.CustomFrameRate = cfr;
            }

            int? currentLayerIdx = null;
            if (parentName == "Geometry" && name == "Layer" && props.Count > 0 && props[0] is int lIdx)
            {
                currentLayerIdx = lIdx;
                info.LayerIndices.Add(lIdx);
                info.UvTypedIndicesByLayer[lIdx] = new List<int>();
                childContext = $"Layer:{lIdx}";
            }

            string? lastElemType = null;
            if (br.BaseStream.Position < endOffset)
            {
                while (br.BaseStream.Position < endOffset)
                {
                    long childStart = br.BaseStream.Position;
                    if (!ReadBinaryFbxNode(br, info, name, depth + 1, childContext))
                    {
                        break;
                    }
                    // If inside LayerElement under Layer:k, inspect Type + TypedIndex
                    if (contextObjType != null && contextObjType.StartsWith("Layer:", StringComparison.Ordinal) && name == "LayerElement")
                    {
                        int layerNum = int.Parse(contextObjType.Substring(6));
                        // Re-read child props for Type / TypedIndex
                        long saved = br.BaseStream.Position;
                        br.BaseStream.Position = childStart;
                        uint cEnd = br.ReadUInt32();
                        uint cProps = br.ReadUInt32();
                        uint cPropLen = br.ReadUInt32();
                        byte cNameLen = br.ReadByte();
                        string cName = Encoding.UTF8.GetString(br.ReadBytes(cNameLen));
                        if (cName == "Type" && cProps == 1 && (char)br.ReadByte() == 'S')
                        {
                            int sl = br.ReadInt32();
                            lastElemType = Encoding.UTF8.GetString(br.ReadBytes(sl));
                        }
                        else if (cName == "TypedIndex" && cProps == 1 && (char)br.ReadByte() == 'I' && lastElemType == "LayerElementUV")
                        {
                            info.UvTypedIndicesByLayer[layerNum].Add(br.ReadInt32());
                        }
                        br.BaseStream.Position = saved;
                    }
                }
            }

            Assert.Equal(endOffset, (uint)br.BaseStream.Position);
            return true;
        }

        private static Mesh CreateUninitializedMesh(int[] version)
        {
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            SetPrivateField(mesh, "m_ProcessLock", new object());
            mesh.version = version;
            mesh.m_Indices = new List<uint>();
            mesh.m_UV0 = Array.Empty<float>();
            mesh.m_BoneNameHashes = Array.Empty<uint>();
            mesh.m_VariableBoneCountWeights = Array.Empty<uint>();
            var reader = (ObjectReader)RuntimeHelpers.GetUninitializedObject(typeof(ObjectReader));
            reader.Endian = EndianType.LittleEndian;
            mesh.reader = reader;
            return mesh;
        }

        private static void SetPrivateField(object target, string fieldName, object? value)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
            }
            throw new MissingFieldException(target.GetType().FullName, fieldName);
        }

        private static object? InvokePrivateMethod(object target, string methodName, object?[]? args = null)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (method != null)
                {
                    return method.Invoke(target, args);
                }
            }
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }

        private static Matrix4x4 MakeIdentityMatrix() => MakeTranslationMatrix(0f, 0f, 0f);

        private static Matrix4x4 MakeTranslationMatrix(float tx, float ty, float tz)
        {
            var m = new Matrix4x4();
            m[0, 0] = 1f; m[1, 1] = 1f; m[2, 2] = 1f; m[3, 3] = 1f;
            m[3, 0] = tx; m[3, 1] = ty; m[3, 2] = tz;
            return m;
        }

        private static float DegToRad(float deg) => (float)(deg * Math.PI / 180.0);

        private static double MatrixFrobDiff(NumMat4 a, NumMat4 b)
        {
            double s = 0;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    double d = a[r, c] - b[r, c];
                    s += d * d;
                }
            return Math.Sqrt(s);
        }
    }
}
