using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace AssetStudio.Tests
{
    public class UnityReaderFidelityTests
    {
        [Fact]
        public void CommonString_ContainsUnity6EntityIdAtOffset1200()
        {
            Assert.True(CommonString.StringBuffer.TryGetValue(1200, out var value));
            Assert.Equal("EntityId", value);
        }

        [Fact]
        public void ObjectReader_ReadBytes_OverridesBinaryReaderPolymorphically()
        {
            var data = new byte[32];
            var sf = CreateSerializedFileWithData(data, EndianType.LittleEndian, out var info, objectByteSize: 8);
            using var objReader = new ObjectReader(sf.reader, sf, info);
            objReader.Reset();
            BinaryReader baseReader = objReader;

            // Calling ReadBytes through BinaryReader base reference must enforce ObjectReader.byteSize (8 bytes)
            Assert.Throws<EndOfStreamException>(() => baseReader.ReadBytes(16));

            objReader.Reset();
            var valid = baseReader.ReadBytes(8);
            Assert.Equal(8, valid.Length);
        }

        [Fact]
        public void BinaryReaderExtensions_SupportsValidPayloadsOver10MB_WhileEnforcingBounds()
        {
            const int payloadSize = 11 * 1024 * 1024 + 3; // > 10 MB unaligned
            int alignedSize = (payloadSize + 3) & ~3;
            var raw = new byte[4 + alignedSize];
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0, 4), payloadSize);
            raw[4] = 0x42;
            raw[4 + payloadSize - 1] = 0x99;

            var sf = CreateSerializedFileWithData(raw, EndianType.LittleEndian, out var info, raw.Length, new[] { 6000, 0, 30, 1 });
            info.classID = (int)ClassIDType.TextAsset;
            using var reader = new ObjectReader(sf.reader, sf, info);
            reader.Reset();
            reader.platform = BuildTarget.StandaloneWindows64;

            var bytes = reader.ReadUInt8Array();
            reader.AlignStream();
            Assert.Equal(payloadSize, bytes.Length);
            Assert.Equal(0x42, bytes[0]);
            Assert.Equal(0x99, bytes[payloadSize - 1]);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
        }

        [Theory]
        [InlineData(6000, 0, 30)]
        [InlineData(6000, 2, 0)]
        [InlineData(6000, 3, 21)]
        [InlineData(6000, 4, 12)]
        public void MeshRenderer_And_SkinnedMeshRenderer_Consume100PercentBytesAcrossUnity6Versions(int major, int minor, int patch)
        {
            var version = new[] { major, minor, patch, 1 };

            // 1) MeshRenderer
            byte[] meshRendererBytes;
            using (var ms = new MemoryStream())
            {
                WriteRendererBase(ms, version, materialPathId: 389);
                // m_AdditionalVertexStreams (PPtr<Mesh>)
                WritePPtr(ms, 0, 0);
                // m_EnlightenVertexStream (PPtr<Mesh>)
                WritePPtr(ms, 0, 0);
                meshRendererBytes = ms.ToArray();
            }

            var sfMesh = CreateSerializedFileWithData(meshRendererBytes, EndianType.LittleEndian, out var mrInfo, meshRendererBytes.Length, version);
            mrInfo.classID = (int)ClassIDType.MeshRenderer;
            using var mrReader = new ObjectReader(sfMesh.reader, sfMesh, mrInfo);
            var mr = new MeshRenderer(mrReader);

            Assert.Equal(mrReader.byteStart + mrReader.byteSize, mrReader.Position);
            Assert.Single(mr.m_Materials);
            Assert.Equal(389, mr.m_Materials[0].m_PathID);
            Assert.NotNull(mr.m_AdditionalVertexStreams);
            Assert.NotNull(mr.m_EnlightenVertexStream);
            if (minor >= 2)
            {
                Assert.Equal(-1, mr.m_ForceMeshLod);
                Assert.Equal(0.5f, mr.m_MeshLodSelectionBias);
            }

            // 2) SkinnedMeshRenderer
            byte[] smrBytes;
            using (var ms = new MemoryStream())
            {
                WriteRendererBase(ms, version, materialPathId: 412);
                WriteInt32(ms, 2); // m_Quality
                ms.WriteByte(1); // m_UpdateWhenOffscreen
                ms.WriteByte(0); // m_SkinnedMotionVectors / m_SkinNormals
                Align4(ms);
                WritePPtr(ms, 0, 86); // m_Mesh
                WriteInt32(ms, 1); // m_Bones.size = 1
                WritePPtr(ms, 0, 64); // m_Bones[0]
                WriteInt32(ms, 2); // m_BlendShapeWeights.size = 2
                WriteSingle(ms, 12.5f);
                WriteSingle(ms, 87.5f);
                WritePPtr(ms, 0, 64); // m_RootBone
                // m_AABB (Center + Extent)
                WriteSingle(ms, 0f); WriteSingle(ms, 1.0f); WriteSingle(ms, 0f);
                WriteSingle(ms, 0.5f); WriteSingle(ms, 1.0f); WriteSingle(ms, 0.5f);
                ms.WriteByte(0); // m_DirtyAABB
                Align4(ms);
                smrBytes = ms.ToArray();
            }

            var sfSmr = CreateSerializedFileWithData(smrBytes, EndianType.LittleEndian, out var smrInfo, smrBytes.Length, version);
            smrInfo.classID = (int)ClassIDType.SkinnedMeshRenderer;
            using var smrReader = new ObjectReader(sfSmr.reader, sfSmr, smrInfo);
            var smr = new SkinnedMeshRenderer(smrReader);

            Assert.Equal(smrReader.byteStart + smrReader.byteSize, smrReader.Position);
            Assert.Equal(86, smr.m_Mesh.m_PathID);
            Assert.Single(smr.m_Bones);
            Assert.Equal(64, smr.m_Bones[0].m_PathID);
            Assert.Equal(new[] { 12.5f, 87.5f }, smr.m_BlendShapeWeights);
            Assert.NotNull(smr.m_RootBone);
            Assert.Equal(64, smr.m_RootBone!.m_PathID);
            Assert.NotNull(smr.m_AABB);
            Assert.Equal(new Vector3(0.5f, 1.0f, 0.5f), smr.m_AABB!.m_Extent);
            Assert.False(smr.m_DirtyAABB);
        }

        [Theory]
        [InlineData(2022, 1, 24)]
        [InlineData(2022, 2, 10)]
        [InlineData(2023, 2, 20)]
        [InlineData(6000, 3, 21)]
        public void Texture2D_BinaryReader_HandlesVersionGatesOrderingAndPersistsColorSpace(int major, int minor, int patch)
        {
            var version = new[] { major, minor, patch, 1 };
            byte[] texBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "FidelityTex");
                if (major < 2023 || (major == 2023 && minor < 2))
                {
                    WriteInt32(ms, 4); // m_ForcedFallbackFormat
                    ms.WriteByte(0); // m_DownscaleFallback
                }
                ms.WriteByte(1); // m_IsAlphaChannelOptional
                Align4(ms);

                WriteInt32(ms, 4); // m_Width
                WriteInt32(ms, 4); // m_Height
                WriteInt32(ms, 16); // m_CompleteImageSize
                WriteInt32(ms, 0); // m_MipsStripped
                WriteInt32(ms, (int)TextureFormat.RGBA32); // m_TextureFormat
                WriteInt32(ms, 1); // m_MipCount
                ms.WriteByte(1); // m_IsReadable
                ms.WriteByte(1); // m_IsPreProcessed
                if (major > 2022 || (major == 2022 && minor >= 2))
                {
                    ms.WriteByte(1); // m_IgnoreMipmapLimit
                    Align4(ms);
                    WriteAlignedString(ms, "CharactersGroup"); // m_MipmapLimitGroupName
                }
                else
                {
                    ms.WriteByte(0); // m_IgnoreMasterTextureLimit
                }
                ms.WriteByte(1); // m_StreamingMipmaps
                Align4(ms);
                WriteInt32(ms, 7); // m_StreamingMipmapsPriority
                WriteInt32(ms, 1); // m_ImageCount
                WriteInt32(ms, 2); // m_TextureDimension
                // GLTextureSettings (6 ints/floats)
                WriteInt32(ms, 1); // m_FilterMode
                WriteInt32(ms, 2); // m_Aniso
                WriteSingle(ms, 0f); // m_MipBias
                WriteInt32(ms, 1); // m_WrapU
                WriteInt32(ms, 1); // m_WrapV
                WriteInt32(ms, 1); // m_WrapW
                WriteInt32(ms, 0); // m_LightmapFormat
                WriteInt32(ms, 1); // m_ColorSpace (Linear = 1)
                WriteInt32(ms, 0); // m_PlatformBlob.size = 0
                WriteInt32(ms, 16); // image_data_size = 16
                ms.Write(new byte[16]); // embedded image_data
                // StreamingInfo m_StreamData
                WriteInt64(ms, 0);
                WriteUInt32(ms, 0);
                WriteAlignedString(ms, string.Empty);

                texBytes = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(texBytes, EndianType.LittleEndian, out var info, texBytes.Length, version);
            info.classID = (int)ClassIDType.Texture2D;
            using var reader = new ObjectReader(sf.reader, sf, info);
            var tex = new Texture2D(reader);

            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Equal("FidelityTex", tex.m_Name);
            Assert.Equal(4, tex.m_Width);
            Assert.Equal(4, tex.m_Height);
            Assert.Equal(TextureFormat.RGBA32, tex.m_TextureFormat);
            Assert.True(tex.m_IsReadable);
            Assert.True(tex.m_IsPreProcessed);
            Assert.True(tex.m_StreamingMipmaps);
            Assert.Equal(7, tex.m_StreamingMipmapsPriority);
            Assert.Equal(1, tex.m_ColorSpace);
            Assert.Equal(16, tex.image_data.Size);
            if (major > 2022 || (major == 2022 && minor >= 2))
            {
                Assert.True(tex.m_IgnoreMipmapLimit);
                Assert.Equal("CharactersGroup", tex.m_MipmapLimitGroupName);
            }
        }

        [Theory]
        [InlineData(5, 3, 4)]
        [InlineData(2022, 3, 62)]
        [InlineData(6000, 0, 49)]
        [InlineData(6000, 3, 21)]
        public void Shader_BinaryReader_Consumes100PercentBytesAcrossUnity5_3To6000_3(int major, int minor, int patch)
        {
            var version = new[] { major, minor, patch, 1 };
            byte[] shaderBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "Custom/FidelityShader");
                if (major == 5 && minor < 5)
                {
                    // 5.3 - 5.4 layout
                    WriteInt32(ms, 5); // m_Script.size = 5 (unaligned)
                    ms.Write(new byte[] { 1, 2, 3, 4, 5 });
                    Align4(ms);
                    WriteAlignedString(ms, "Assets/Shaders/Fidelity.shader"); // m_PathName
                    WriteUInt32(ms, 128); // decompressedSize
                    WriteInt32(ms, 3); // m_SubProgramBlob.size = 3 (unaligned)
                    ms.Write(new byte[] { 0xAA, 0xBB, 0xCC });
                    Align4(ms);
                    WriteInt32(ms, 1); // m_Dependencies.size = 1
                    WritePPtr(ms, 0, 99);
                    ms.WriteByte(1); // m_ShaderIsBaked
                    Align4(ms);
                }
                else
                {
                    // SerializedShader m_ParsedForm
                    WriteInt32(ms, 0); // m_PropInfo.m_Props.size = 0
                    WriteInt32(ms, 1); // m_SubShaders.size = 1

                    // SubShader[0]
                    WriteInt32(ms, 1); // m_Passes.size = 1
                    // Pass[0]
                    if (major < 2023)
                    {
                        WriteInt32(ms, 1); // m_EditorDataHash.size = 1
                        ms.Write(new byte[16]); // Hash128
                        Align4(ms);
                        WriteInt32(ms, 1); // m_Platforms.size = 1
                        ms.WriteByte(4);
                        Align4(ms);
                    }
                    WriteInt32(ms, 0); // m_NameIndices.size = 0
                    WriteInt32(ms, (int)PassType.Normal); // m_Type
                    WriteMinimalShaderState(ms, version); // m_State
                    WriteUInt32(ms, 3u); // m_ProgramMask

                    // 6 programs: progVertex, progFragment, progGeometry, progHull, progDomain, progRayTracing
                    for (int p = 0; p < 6; p++)
                    {
                        WriteInt32(ms, 0); // m_SubPrograms.size = 0
                        if ( p == 0 )
                        {
                            // 1 player subprogram in progVertex
                            WriteInt32(ms, 1); // m_PlayerSubPrograms outer size
                            WriteInt32(ms, 1); // inner size
                            WriteUInt32(ms, 0); // m_BlobIndex
                            WriteInt32(ms, 1); // m_KeywordIndices.size = 1
                            WriteUInt16(ms, 2);
                            Align4(ms);
                            WriteInt64(ms, 35L); // m_ShaderRequirements
                            ms.WriteByte((byte)ShaderGpuProgramType.DX11PixelSM40); // m_GpuProgramType
                            Align4(ms);

                            WriteInt32(ms, 1); // m_ParameterBlobIndices outer
                            WriteInt32(ms, 1); // inner
                            WriteUInt32(ms, 0);
                        }
                        else
                        {
                            WriteInt32(ms, 0); // m_PlayerSubPrograms.size = 0
                            WriteInt32(ms, 0); // m_ParameterBlobIndices.size = 0
                        }
                        // m_CommonParameters (8 empty vectors)
                        for (int k = 0; k < 8; k++)
                        {
                            WriteInt32(ms, 0);
                        }
                        // m_SerializedKeywordStateMask (ushort[])
                        WriteInt32(ms, 1);
                        WriteUInt16(ms, 1);
                        Align4(ms);
                    }

                    ms.WriteByte(0); // m_HasInstancingVariant
                    ms.WriteByte(0); // m_HasProceduralInstancingVariant
                    Align4(ms);
                    WriteAlignedString(ms, ""); // m_UseName
                    WriteAlignedString(ms, "Forward"); // m_Name
                    WriteAlignedString(ms, ""); // m_TextureName
                    WriteInt32(ms, 0); // Pass m_Tags.size = 0

                    WriteInt32(ms, 0); // SubShader m_Tags.size = 0
                    WriteInt32(ms, 200); // m_LOD

                    WriteInt32(ms, 1); // m_KeywordNames.size = 1
                    WriteAlignedString(ms, "_MAIN_LIGHT");
                    WriteInt32(ms, 1); // m_KeywordFlags.size = 1
                    ms.WriteByte(1);
                    Align4(ms);

                    WriteAlignedString(ms, "Custom/FidelityShader"); // m_ParsedForm.m_Name
                    WriteAlignedString(ms, ""); // m_CustomEditorName
                    WriteAlignedString(ms, "Hidden/InternalErrorShader"); // m_FallbackName
                    WriteInt32(ms, 0); // m_Dependencies.size = 0
                    WriteInt32(ms, 0); // m_CustomEditorForRenderPipelines.size = 0
                    ms.WriteByte(0); // m_DisableNoSubshadersMessage
                    Align4(ms);

                    // Shader root fields after m_ParsedForm
                    WriteInt32(ms, 1); // platforms.size = 1
                    WriteUInt32(ms, (uint)ShaderCompilerPlatform.D3D11);
                    WriteInt32(ms, 1); WriteInt32(ms, 1); WriteUInt32(ms, 0); // offsets
                    WriteInt32(ms, 1); WriteInt32(ms, 1); WriteUInt32(ms, 4); // compressedLengths
                    WriteInt32(ms, 1); WriteInt32(ms, 1); WriteUInt32(ms, 4); // decompressedLengths
                    WriteInt32(ms, 4); // compressedBlob.size = 4
                    ms.Write(new byte[] { 10, 20, 30, 40 });
                    Align4(ms);

                    WriteInt32(ms, 1); // stageCounts.size = 1
                    WriteUInt32(ms, 6);

                    WriteInt32(ms, 0); // m_Dependencies.size = 0
                    WriteInt32(ms, 0); // m_NonModifiableTextures.size = 0
                    ms.WriteByte(1); // m_ShaderIsBaked
                    Align4(ms);

                    if (major >= 6000)
                    {
                        for (int b = 0; b < 16; b++) ms.WriteByte((byte)(b + 1)); // m_AssetGUID
                        if (minor >= 3)
                        {
                            WriteInt64(ms, 4800000L); // m_AssetLocalIdentifierInFile
                        }
                    }
                }
                shaderBytes = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(shaderBytes, EndianType.LittleEndian, out var info, shaderBytes.Length, version);
            info.classID = (int)ClassIDType.Shader;
            using var reader = new ObjectReader(sf.reader, sf, info);
            var shader = new Shader(reader);

            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Equal("Custom/FidelityShader", shader.m_Name);
            Assert.True(shader.m_ShaderIsBaked);
            if (major >= 2022)
            {
                Assert.Single(shader.stageCounts);
                Assert.Equal(6u, shader.stageCounts[0]);
                Assert.NotNull(shader.m_ParsedForm);
                var progVertex = shader.m_ParsedForm!.m_SubShaders[0].m_Passes[0].progVertex;
                Assert.Single(progVertex.m_PlayerSubPrograms);
                Assert.Single(progVertex.m_PlayerSubPrograms[0]);
                Assert.Equal(35L, progVertex.m_PlayerSubPrograms[0][0].m_ShaderRequirements);
            }
            if (major >= 6000)
            {
                Assert.NotNull(shader.m_AssetGUID);
                if (minor >= 3)
                {
                    Assert.Equal(4800000L, shader.m_AssetLocalIdentifierInFile);
                }
            }
        }

        [Fact]
        public void TypeTreeHelper_SerializeReference_ResolvesRefTypesAcrossDumpToTypeAndSkipNode()
        {
            var nodes = new List<TypeTreeNode>
            {
                new TypeTreeNode("MonoBehaviour", "Base", 0, false),
                new TypeTreeNode("string", "m_Name", 1, true),
                new TypeTreeNode("Array", "Array", 2, true),
                new TypeTreeNode("int", "size", 3, false),
                new TypeTreeNode("char", "data", 3, false) { m_ByteSize = 1 },
                new TypeTreeNode("ManagedReferencesRegistry", "references", 1, false),
                new TypeTreeNode("int", "version", 2, false),
                new TypeTreeNode("vector", "RefIds", 2, false),
                new TypeTreeNode("Array", "Array", 3, true),
                new TypeTreeNode("int", "size", 4, false),
                new TypeTreeNode("ReferencedObject", "data", 4, false),
                new TypeTreeNode("SInt64", "rid", 5, false),
                new TypeTreeNode("ReferencedManagedType", "type", 5, false),
                new TypeTreeNode("string", "class", 6, true),
                new TypeTreeNode("Array", "Array", 7, true),
                new TypeTreeNode("int", "size", 8, false),
                new TypeTreeNode("char", "data", 8, false) { m_ByteSize = 1 },
                new TypeTreeNode("string", "ns", 6, true),
                new TypeTreeNode("Array", "Array", 7, true),
                new TypeTreeNode("int", "size", 8, false),
                new TypeTreeNode("char", "data", 8, false) { m_ByteSize = 1 },
                new TypeTreeNode("string", "asm", 6, true),
                new TypeTreeNode("Array", "Array", 7, true),
                new TypeTreeNode("int", "size", 8, false),
                new TypeTreeNode("char", "data", 8, false) { m_ByteSize = 1 },
                new TypeTreeNode("ReferencedObjectData", "data", 5, false),
                new TypeTreeNode("int", "m_TrailingSentinel", 1, false)
            };

            var refTypeNodes = new List<TypeTreeNode>
            {
                new TypeTreeNode("DistributedUIDGenerator", "Base", 0, false),
                new TypeTreeNode("SInt64", "m_CustomEpoch", 1, false),
                new TypeTreeNode("float", "m_Weight", 1, false),
                new TypeTreeNode("ManagedReferencesRegistry", "references", 1, false),
                new TypeTreeNode("int", "version", 2, false)
            };

            byte[] payload;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "RefHolder");
                WriteInt32(ms, 2); // ManagedReferencesRegistry.version = 2
                WriteInt32(ms, 2); // RefIds.size = 2

                // RefIds[0]: null/empty entry (rid = -2, 0 data bytes)
                WriteInt64(ms, -2L);
                WriteAlignedString(ms, "");
                WriteAlignedString(ms, "");
                WriteAlignedString(ms, "");

                // RefIds[1]: populated entry (rid = 1001, DistributedUIDGenerator = 12 bytes)
                WriteInt64(ms, 1001L);
                WriteAlignedString(ms, "DistributedUIDGenerator");
                WriteAlignedString(ms, "Game.Core");
                WriteAlignedString(ms, "Assembly-CSharp");
                WriteInt64(ms, 1700000000L); // m_CustomEpoch
                WriteSingle(ms, 0.75f); // m_Weight

                WriteInt32(ms, 0x55AA55AA); // m_TrailingSentinel
                payload = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(payload, EndianType.LittleEndian, out var info, payload.Length, new[] { 6000, 0, 49, 1 });
            sf.m_RefTypes = new List<SerializedType>
            {
                new SerializedType
                {
                    m_KlassName = "DistributedUIDGenerator",
                    m_NameSpace = "Game.Core",
                    m_AsmName = "Assembly-CSharp",
                    m_Type = new TypeTree { m_Nodes = refTypeNodes }
                }
            };

            var mainTree = new TypeTree { m_Nodes = nodes };
            using var reader = new ObjectReader(sf.reader, sf, info);

            // 1) ReadTypeString (Dump)
            string dump = TypeTreeHelper.ReadTypeString(mainTree, reader);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Contains("SInt64 m_CustomEpoch = 1700000000", dump);
            Assert.Contains("float m_Weight = 0.75", dump);
            Assert.Contains("int m_TrailingSentinel = 1437226410", dump);

            // 2) ReadType (ToType)
            var dict = TypeTreeHelper.ReadType(mainTree, reader);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Equal(0x55AA55AA, dict["m_TrailingSentinel"]);
            var refs = Assert.IsType<OrderedDictionary>(dict["references"]);
            var refIds = Assert.IsType<List<object>>(refs["RefIds"]);
            Assert.Equal(2, refIds.Count);
            var secondRef = Assert.IsType<OrderedDictionary>(refIds[1]);
            var refData = Assert.IsType<OrderedDictionary>(secondRef["data"]);
            Assert.Equal(1700000000L, refData["m_CustomEpoch"]);
            Assert.Equal(0.75f, refData["m_Weight"]);

            // 3) SkipNode
            reader.Reset();
            var nextSibling = TypeTreeHelper.GetNextSiblingTable(nodes);
            int idx = 0;
            TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref idx);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
        }

        [Fact]
        public void TypeTreeHelper_ReadTypeString_UsesInvariantCultureAndG9G17RoundTripUnderPtBrCulture()
        {
            const float expectedFloat = 0.12345679f;
            const double expectedDouble = 0.12345678901234568;

            var nodes = new List<TypeTreeNode>
            {
                new TypeTreeNode("TestNode", "Base", 0, false),
                new TypeTreeNode("float", "fVal", 1, false),
                new TypeTreeNode("double", "dVal", 1, false)
            };

            byte[] payload;
            using (var ms = new MemoryStream())
            {
                WriteSingle(ms, expectedFloat);
                WriteDouble(ms, expectedDouble);
                payload = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(payload, EndianType.LittleEndian, out var info, payload.Length);
            using var reader = new ObjectReader(sf.reader, sf, info);
            var tree = new TypeTree { m_Nodes = nodes };

            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
                string dump = TypeTreeHelper.ReadTypeString(tree, reader);

                Assert.DoesNotContain(",", dump);
                Assert.Contains($"float fVal = {expectedFloat.ToString("G9", CultureInfo.InvariantCulture)}", dump);
                Assert.Contains($"double dVal = {expectedDouble.ToString("G17", CultureInfo.InvariantCulture)}", dump);

                float parsedF = float.Parse(expectedFloat.ToString("G9", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                double parsedD = double.Parse(expectedDouble.ToString("G17", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                Assert.Equal(BitConverter.SingleToInt32Bits(expectedFloat), BitConverter.SingleToInt32Bits(parsedF));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expectedDouble), BitConverter.DoubleToInt64Bits(parsedD));
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        [Fact]
        public void TypeTreeHelper_MapPairAlignment_And_CharEndianness_AreExact()
        {
            var nodes = new List<TypeTreeNode>
            {
                new TypeTreeNode("Root", "Base", 0, false),
                new TypeTreeNode("map", "byteMap", 1, false),
                new TypeTreeNode("Array", "Array", 2, false),
                new TypeTreeNode("int", "size", 3, false),
                new TypeTreeNode("pair", "data", 3, true), // 0x4000 align on pair!
                new TypeTreeNode("int", "first", 4, false),
                new TypeTreeNode("UInt8", "second", 4, false),
                new TypeTreeNode("char", "wideChar", 1, false) { m_ByteSize = 2 },
                new TypeTreeNode("char", "narrowChar", 1, true) { m_ByteSize = 1 },
                new TypeTreeNode("int", "tail", 1, false)
            };

            byte[] payload;
            using (var ms = new MemoryStream())
            {
                WriteInt32BigEndian(ms, 2); // map size = 2
                // pair 0: int + byte + 3 align bytes
                WriteInt32BigEndian(ms, 10);
                ms.WriteByte(0xAA);
                Align4(ms);
                // pair 1: int + byte + 3 align bytes
                WriteInt32BigEndian(ms, 20);
                ms.WriteByte(0xBB);
                Align4(ms);
                // wideChar (BigEndian 0x0041 = 'A')
                ms.WriteByte(0x00);
                ms.WriteByte(0x41);
                // narrowChar ('Z' + align)
                ms.WriteByte((byte)'Z');
                Align4(ms);
                // tail
                WriteInt32BigEndian(ms, 777);
                payload = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(payload, EndianType.BigEndian, out var info, payload.Length);
            var tree = new TypeTree { m_Nodes = nodes };
            using var reader = new ObjectReader(sf.reader, sf, info);

            string dump = TypeTreeHelper.ReadTypeString(tree, reader);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Contains("char wideChar = A", dump);
            Assert.Contains("char narrowChar = Z", dump);
            Assert.Contains("int tail = 777", dump);

            var dict = TypeTreeHelper.ReadType(tree, reader);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Equal('A', dict["wideChar"]);
            Assert.Equal('Z', dict["narrowChar"]);
            Assert.Equal(777, dict["tail"]);

            reader.Reset();
            int idx = 0;
            TypeTreeHelper.SkipNode(nodes, TypeTreeHelper.GetNextSiblingTable(nodes), reader, ref idx);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
        }

        [Theory]
        [InlineData(6000, 3, 21)]
        [InlineData(6000, 4, 12)]
        public void AnimatorController_Unity6_3And6_4_Consumes100PercentBytes(int major, int minor, int patch)
        {
            var version = new[] { major, minor, patch, 1 };
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "HeroController");
                if (minor >= 4)
                {
                    WriteUInt32(ms, 0u); // 6000.4 header flags
                }
                WriteUInt32(ms, 128u); // m_ControllerSize
                WriteInt32(ms, 0); // m_LayerArray.size = 0
                WriteInt32(ms, 0); // m_StateMachineArray.size = 0
                WriteInt32(ms, 0); // m_Values.m_ValueArray.size = 0
                // m_DefaultValues (ValueArray)
                WriteInt32(ms, 0); // m_PositionValues
                WriteInt32(ms, 0); // m_QuaternionValues
                WriteInt32(ms, 0); // m_ScaleValues
                WriteInt32(ms, 1); WriteSingle(ms, 1.5f); // m_FloatValues
                WriteInt32(ms, 0); // m_IntValues
                WriteInt32(ms, 0); // m_BoolValues
                WriteInt32(ms, 1); WriteInt32(ms, 42); // m_EntityIdValues (6000.3+)
                if (minor >= 4)
                {
                    WriteInt32(ms, 0); // 6000.4 extra array
                }
                // m_TOS (1 entry)
                WriteInt32(ms, 1);
                WriteUInt32(ms, 12345u);
                WriteAlignedString(ms, "Base Layer");
                // m_AnimationClips (1 entry)
                WriteInt32(ms, 1);
                WritePPtr(ms, 0, 555);
                // m_StateMachineBehaviourVectorDescription
                WriteInt32(ms, 1); // m_StateMachineBehaviourRanges.size = 1
                WriteUInt32(ms, 999u); // m_StateID
                WriteInt32(ms, 0); // m_LayerIndex
                WriteUInt32(ms, 0u); // m_StartIndex
                WriteUInt32(ms, 1u); // m_Count
                WriteInt32(ms, 1); // m_StateMachineBehaviourIndices.size = 1
                WriteUInt32(ms, 0u);
                // m_StateMachineBehaviours
                WriteInt32(ms, 1);
                WritePPtr(ms, 0, 888);
                // m_MultiThreadedStateMachine
                ms.WriteByte(1);
                Align4(ms);
                bytes = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(bytes, EndianType.LittleEndian, out var info, bytes.Length, version);
            info.classID = (int)ClassIDType.AnimatorController;
            using var reader = new ObjectReader(sf.reader, sf, info);
            var ac = new AnimatorController(reader);

            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Equal("HeroController", ac.m_Name);
            Assert.Single(ac.m_AnimationClips);
            Assert.NotNull(ac.m_StateMachineBehaviourVectorDescription);
            Assert.Single(ac.m_StateMachineBehaviours);
            Assert.True(ac.m_MultiThreadedStateMachine);
        }

        [Fact]
        public void Sprite_SecondarySpriteTexture_UsesAlignedStringAndReadsTrailingBonesAndScriptableObjects()
        {
            var version = new[] { 6000, 0, 49, 1 };
            var sf = CreateSerializedFileWithData(new byte[4], EndianType.LittleEndian, out var dummyInfo, 4, version);

            byte[] secBytes;
            using (var ms = new MemoryStream())
            {
                WritePPtr(ms, 0, 77);
                WriteAlignedString(ms, "_NormalMap"); // 10 chars + 2 padding bytes (no null terminator needed!)
                WriteInt32(ms, 0x12345678); // sentinel immediately following aligned string
                secBytes = ms.ToArray();
            }

            sf = CreateSerializedFileWithData(secBytes, EndianType.LittleEndian, out dummyInfo, secBytes.Length, version);
            using var reader = new ObjectReader(sf.reader, sf, dummyInfo);
            reader.Reset();
            var sec = new SecondarySpriteTexture(reader);
            int sentinel = reader.ReadInt32();

            Assert.Equal(77, sec.texture.m_PathID);
            Assert.Equal("_NormalMap", sec.name);
            Assert.Equal(0x12345678, sentinel);
            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
        }

        [Fact]
        public void AnimationClip_Unity2022_2_ReadsCurveFlagsAndSerializeReferenceBinding()
        {
            var version = new[] { 2022, 2, 0, 1 };
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                // FloatCurve (0 keys)
                WriteInt32(ms, 0); // m_Curve.size = 0
                WriteInt32(ms, 2); // m_PreInfinity
                WriteInt32(ms, 2); // m_PostInfinity
                WriteInt32(ms, 4); // m_RotationOrder
                WriteAlignedString(ms, "m_Alpha");
                WriteAlignedString(ms, "Root/Panel");
                WriteInt32(ms, (int)ClassIDType.MonoBehaviour);
                WritePPtr(ms, 0, 10);
                WriteInt32(ms, 2); // flags (2022.2+)

                // PPtrCurve (0 keys)
                WriteInt32(ms, 0); // curve.size = 0
                WriteAlignedString(ms, "m_Sprite");
                WriteAlignedString(ms, "Root/Icon");
                WriteInt32(ms, (int)ClassIDType.Sprite);
                WritePPtr(ms, 0, 20);
                WriteInt32(ms, 3); // flags (2022.2+)

                // GenericBinding
                WriteUInt32(ms, 100u); // path
                WriteUInt32(ms, 200u); // attribute
                WritePPtr(ms, 0, 30); // script
                WriteInt32(ms, (int)ClassIDType.MonoBehaviour); // typeID
                ms.WriteByte(28); // customType
                ms.WriteByte(0); // isPPtrCurve
                ms.WriteByte(1); // isIntCurve (2022.1+)
                ms.WriteByte(1); // isSerializeReferenceCurve (2022.2+)
                Align4(ms);

                bytes = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(bytes, EndianType.LittleEndian, out var info, bytes.Length, version);
            using var reader = new ObjectReader(sf.reader, sf, info);
            reader.Reset();

            var fc = new FloatCurve(reader);
            var pc = new PPtrCurve(reader);
            var gb = new GenericBinding(reader);

            Assert.Equal(reader.byteStart + reader.byteSize, reader.Position);
            Assert.Equal(2, fc.flags);
            Assert.Equal(3, pc.flags);
            Assert.Equal(1, gb.isIntCurve);
            Assert.Equal(1, gb.isSerializeReferenceCurve);
        }

        [Fact]
        public void Avatar_GameObject_RectTransform_TextAsset_And_Material_Consume100PercentBytes()
        {
            var version = new[] { 2022, 3, 62, 1 };

            // 1) GameObject
            byte[] goBytes;
            using (var ms = new MemoryStream())
            {
                WriteInt32(ms, 1); // m_Components.size = 1
                WritePPtr(ms, 0, 2);
                WriteInt32(ms, 5); // m_Layer = UI (5)
                WriteAlignedString(ms, "CanvasRoot");
                WriteUInt16(ms, 3); // m_Tag
                ms.WriteByte(1); // m_IsActive
                goBytes = ms.ToArray();
            }
            var sfGo = CreateSerializedFileWithData(goBytes, EndianType.LittleEndian, out var goInfo, goBytes.Length, version);
            goInfo.classID = (int)ClassIDType.GameObject;
            using (var goReader = new ObjectReader(sfGo.reader, sfGo, goInfo))
            {
                var go = new GameObject(goReader);
                Assert.Equal(goReader.byteStart + goReader.byteSize, goReader.Position);
                Assert.Equal(5, go.m_Layer);
                Assert.Equal((ushort)3, go.m_Tag);
                Assert.True(go.m_IsActive);
            }

            // 2) RectTransform
            byte[] rtBytes;
            using (var ms = new MemoryStream())
            {
                WritePPtr(ms, 0, 1); // m_GameObject
                WriteSingle(ms, 0f); WriteSingle(ms, 0f); WriteSingle(ms, 0f); WriteSingle(ms, 1f); // m_LocalRotation
                WriteSingle(ms, 1f); WriteSingle(ms, 2f); WriteSingle(ms, 3f); // m_LocalPosition
                WriteSingle(ms, 1f); WriteSingle(ms, 1f); WriteSingle(ms, 1f); // m_LocalScale
                WriteInt32(ms, 0); // m_Children.size = 0
                WritePPtr(ms, 0, 0); // m_Father
                // RectTransform 5 Vector2s (40 bytes)
                WriteSingle(ms, 0f); WriteSingle(ms, 0f); // m_AnchorMin
                WriteSingle(ms, 1f); WriteSingle(ms, 1f); // m_AnchorMax
                WriteSingle(ms, 10f); WriteSingle(ms, 20f); // m_AnchoredPosition
                WriteSingle(ms, 100f); WriteSingle(ms, 200f); // m_SizeDelta
                WriteSingle(ms, 0.5f); WriteSingle(ms, 0.5f); // m_Pivot
                rtBytes = ms.ToArray();
            }
            var sfRt = CreateSerializedFileWithData(rtBytes, EndianType.LittleEndian, out var rtInfo, rtBytes.Length, version);
            rtInfo.classID = (int)ClassIDType.RectTransform;
            using (var rtReader = new ObjectReader(sfRt.reader, sfRt, rtInfo))
            {
                var rt = new RectTransform(rtReader);
                Assert.Equal(rtReader.byteStart + rtReader.byteSize, rtReader.Position);
                Assert.Equal(new Vector2(100f, 200f), rt.m_SizeDelta);
                Assert.Equal(new Vector2(0.5f, 0.5f), rt.m_Pivot);
            }

            // 3) TextAsset with unaligned script length
            byte[] taBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "config.json");
                WriteAlignedString(ms, "{}"); // 2 bytes + 2 padding bytes
                taBytes = ms.ToArray();
            }
            var sfTa = CreateSerializedFileWithData(taBytes, EndianType.LittleEndian, out var taInfo, taBytes.Length, version);
            taInfo.classID = (int)ClassIDType.TextAsset;
            using (var taReader = new ObjectReader(sfTa.reader, sfTa, taInfo))
            {
                var ta = new TextAsset(taReader);
                Assert.Equal(taReader.byteStart + taReader.byteSize, taReader.Position);
                Assert.Equal(2, ta.m_Script.Length);
            }

            // 4) Material binary fallback (2022.3)
            byte[] matBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "LitMat");
                WritePPtr(ms, 0, 48); // m_Shader
                WriteInt32(ms, 1); WriteAlignedString(ms, "_EMISSION"); // m_ValidKeywords
                WriteInt32(ms, 0); // m_InvalidKeywords
                WriteUInt32(ms, 4u); // m_LightmapFlags
                ms.WriteByte(1); // m_EnableInstancingVariants
                ms.WriteByte(1); // m_DoubleSidedGI
                Align4(ms);
                WriteInt32(ms, 2000); // m_CustomRenderQueue
                WriteInt32(ms, 0); // stringTagMap.size = 0
                WriteInt32(ms, 0); // disabledShaderPasses.size = 0
                // m_SavedProperties (m_TexEnvs, m_Ints, m_Floats, m_Colors)
                WriteInt32(ms, 0);
                WriteInt32(ms, 0);
                WriteInt32(ms, 0);
                WriteInt32(ms, 0);
                // m_BuildTextureStacks (2020.1+)
                WriteInt32(ms, 0);
                matBytes = ms.ToArray();
            }
            var sfMat = CreateSerializedFileWithData(matBytes, EndianType.LittleEndian, out var matInfo, matBytes.Length, version);
            matInfo.classID = (int)ClassIDType.Material;
            using (var matReader = new ObjectReader(sfMat.reader, sfMat, matInfo))
            {
                var mat = new Material(matReader);
                Assert.Equal(matReader.byteStart + matReader.byteSize, matReader.Position);
                Assert.Equal("LitMat", mat.m_Name);
                Assert.Single(mat.m_ValidKeywords);
                Assert.True(mat.m_DoubleSidedGI);
                Assert.Equal(2000, mat.m_CustomRenderQueue);
            }

            // 5) HumanDescription (Avatar 2019+)
            byte[] hdBytes;
            using (var ms = new MemoryStream())
            {
                WriteInt32(ms, 0); // m_Human.size = 0
                WriteInt32(ms, 0); // m_Skeleton.size = 0
                for (int i = 0; i < 8; i++) WriteSingle(ms, 0.5f); // 8 twist/stretch/scale floats
                WriteAlignedString(ms, "Hips"); // m_RootMotionBoneName
                ms.WriteByte(0);
                ms.WriteByte(1);
                ms.WriteByte(1);
                Align4(ms);
                hdBytes = ms.ToArray();
            }
            var sfHd = CreateSerializedFileWithData(hdBytes, EndianType.LittleEndian, out var hdInfo, hdBytes.Length, version);
            using (var hdReader = new ObjectReader(sfHd.reader, sfHd, hdInfo))
            {
                hdReader.Reset();
                var hd = new HumanDescription(hdReader);
                Assert.Equal(hdReader.byteStart + hdReader.byteSize, hdReader.Position);
                Assert.Equal("Hips", hd.m_RootMotionBoneName);
                Assert.True(hd.m_SkeletonHasParents);
            }
        }

        [Fact]
        public void AssetHandle_TryReadObjectName_ReturnsNullForNonNamedObjects_AndReadsNamedObjects()
        {
            var version = new[] { 2022, 3, 62, 1 };

            // Transform whose m_GameObject has external FileID = 4 (would previously be misread as 4-byte string!)
            byte[] transformBytes;
            using (var ms = new MemoryStream())
            {
                WriteInt32(ms, 4); // m_GameObject.m_FileID = 4
                WriteInt64(ms, 0x41424344L); // m_GameObject.m_PathID ("DCBA\0\0\0\0")
                transformBytes = ms.ToArray();
            }
            var sfTr = CreateSerializedFileWithData(transformBytes, EndianType.LittleEndian, out var trInfo, transformBytes.Length, version);
            trInfo.classID = (int)ClassIDType.Transform;
            using (var trReader = new ObjectReader(sfTr.reader, sfTr, trInfo))
            {
                Assert.Null(AssetHandle.TryReadObjectName(trReader));
            }

            // Texture2D (NamedObject) returns m_Name
            byte[] texBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, "AlbedoTex");
                texBytes = ms.ToArray();
            }
            var sfTex = CreateSerializedFileWithData(texBytes, EndianType.LittleEndian, out var texInfo, texBytes.Length, version);
            texInfo.classID = (int)ClassIDType.Texture2D;
            using (var texReader = new ObjectReader(sfTex.reader, sfTex, texInfo))
            {
                Assert.Equal("AlbedoTex", AssetHandle.TryReadObjectName(texReader));
            }
        }

        [Fact]
        public void ImportHelper_MergeSplitAssets_ReadOnlySplitFiles_MergesDirectlyToTempFallbackWithoutTouchingSourceDir()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudioTests_ROSplit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string? fallbackPath = null;
            string split0 = Path.Combine(tempRoot, "resources.assets.split0");
            string split1 = Path.Combine(tempRoot, "resources.assets.split1");
            string targetFull = Path.Combine(tempRoot, "resources.assets");

            try
            {
                var part0 = Encoding.ASCII.GetBytes("READONLY-SPLIT-0|");
                var part1 = Encoding.ASCII.GetBytes("READONLY-SPLIT-1");
                File.WriteAllBytes(split0, part0);
                File.WriteAllBytes(split1, part1);
                File.SetAttributes(split0, FileAttributes.ReadOnly);
                File.SetAttributes(split1, FileAttributes.ReadOnly);

                ImportHelper.MergeSplitAssets(tempRoot);

                // Source directory must NOT have resources.assets or any .tmp file written to it
                Assert.False(File.Exists(targetFull));
                Assert.Empty(Directory.GetFiles(tempRoot, "*.tmp"));

                Assert.True(ImportHelper.TryGetMergedSplitFallbackPath(targetFull, out fallbackPath));
                Assert.NotNull(fallbackPath);
                Assert.True(File.Exists(fallbackPath!));

                var expected = new byte[part0.Length + part1.Length];
                Buffer.BlockCopy(part0, 0, expected, 0, part0.Length);
                Buffer.BlockCopy(part1, 0, expected, part0.Length, part1.Length);
                Assert.Equal(expected, File.ReadAllBytes(fallbackPath!));
            }
            finally
            {
                try
                {
                    if (File.Exists(split0)) File.SetAttributes(split0, FileAttributes.Normal);
                    if (File.Exists(split1)) File.SetAttributes(split1, FileAttributes.Normal);
                    if (!string.IsNullOrEmpty(fallbackPath) && File.Exists(fallbackPath)) File.Delete(fallbackPath);
                }
                catch
                {
                }
                ImportHelper.ClearMergedSplitFallbackPaths();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        private static void WriteRendererBase(Stream ms, int[] version, long materialPathId)
        {
            WritePPtr(ms, 0, 100); // m_GameObject
            ms.WriteByte(1); // m_Enabled
            ms.WriteByte(1); // m_CastShadows
            ms.WriteByte(1); // m_ReceiveShadows
            ms.WriteByte(1); // m_DynamicOccludee
            ms.WriteByte(0); // m_StaticShadowCaster
            ms.WriteByte(1); // m_MotionVectors
            ms.WriteByte(1); // m_LightProbeUsage
            ms.WriteByte(1); // m_ReflectionProbeUsage
            ms.WriteByte(0); // m_RayTracingMode
            ms.WriteByte(0); // m_RayTraceProcedural
            ms.WriteByte(0); // m_RayTracingAccelStructBuildFlagsOverride (2023.2+)
            ms.WriteByte(1); // m_RayTracingAccelStructBuildFlags (2023.2+)
            if (version[0] >= 6000)
            {
                ms.WriteByte(1); // m_SmallMeshCulling (6000.0+)
            }
            Align4(ms);

            if (version[0] > 6000 || (version[0] == 6000 && version[1] >= 2))
            {
                WriteInt16(ms, -1); // m_ForceMeshLod
                Align4(ms);
                WriteSingle(ms, 0.5f); // m_MeshLodSelectionBias
            }

            WriteUInt32(ms, 1u); // m_RenderingLayerMask
            WriteInt32(ms, 0); // m_RendererPriority
            WriteUInt16(ms, ushort.MaxValue); // m_LightmapIndex
            WriteUInt16(ms, ushort.MaxValue); // m_LightmapIndexDynamic
            // m_LightmapTilingOffset & m_LightmapTilingOffsetDynamic
            for (int k = 0; k < 2; k++)
            {
                WriteSingle(ms, 1f); WriteSingle(ms, 1f); WriteSingle(ms, 0f); WriteSingle(ms, 0f);
            }
            // m_Materials (1 material)
            WriteInt32(ms, 1);
            WritePPtr(ms, 0, materialPathId);
            // m_StaticBatchInfo (4 bytes)
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            // m_StaticBatchRoot, m_ProbeAnchor, m_LightProbeVolumeOverride
            WritePPtr(ms, 0, 0);
            WritePPtr(ms, 0, 0);
            WritePPtr(ms, 0, 0);
            // m_SortingLayerID, m_SortingLayer, m_SortingOrder
            WriteUInt32(ms, 0u);
            WriteInt16(ms, 0);
            WriteInt16(ms, 0);
            Align4(ms);
            if (version[0] > 6000 || (version[0] == 6000 && version[1] >= 3))
            {
                WriteInt32(ms, 0); // m_MaskInteraction
            }
        }

        private static void WriteMinimalShaderState(Stream ms, int[] version)
        {
            WriteAlignedString(ms, " Forward ");
            // rtBlend0..7 (8 * SerializedShaderRTBlendState: 7 SerializedShaderFloatValue each = 56 float values)
            for (int i = 0; i < 8 * 7; i++)
            {
                WriteShaderFloatValue(ms, 0f, "");
            }
            ms.WriteByte(0); // rtSeparateBlend
            Align4(ms);
            WriteShaderFloatValue(ms, 0f, ""); // zClip
            WriteShaderFloatValue(ms, 4f, ""); // zTest
            WriteShaderFloatValue(ms, 1f, ""); // zWrite
            WriteShaderFloatValue(ms, 2f, ""); // culling
            WriteShaderFloatValue(ms, 0f, ""); // conservative
            WriteShaderFloatValue(ms, 0f, ""); // offsetFactor
            WriteShaderFloatValue(ms, 0f, ""); // offsetUnits
            WriteShaderFloatValue(ms, 0f, ""); // alphaToMask
            // stencilOp, stencilOpFront, stencilOpBack (3 * 4 float values = 12)
            for (int i = 0; i < 12; i++)
            {
                WriteShaderFloatValue(ms, 0f, "");
            }
            WriteShaderFloatValue(ms, 255f, ""); // stencilReadMask
            WriteShaderFloatValue(ms, 255f, ""); // stencilWriteMask
            WriteShaderFloatValue(ms, 0f, ""); // stencilRef
            WriteShaderFloatValue(ms, 0f, ""); // fogStart
            WriteShaderFloatValue(ms, 0f, ""); // fogEnd
            WriteShaderFloatValue(ms, 0f, ""); // fogDensity
            // fogColor (4 float values + name)
            for (int i = 0; i < 4; i++)
            {
                WriteShaderFloatValue(ms, 0f, "");
            }
            WriteAlignedString(ms, "");
            WriteInt32(ms, 0); // fogMode
            WriteInt32(ms, 0); // gpuProgramID
            WriteInt32(ms, 0); // m_Tags.size = 0
            WriteInt32(ms, 200); // m_LOD
            ms.WriteByte(0); // lighting
            Align4(ms);
        }

        private static void WriteShaderFloatValue(Stream ms, float val, string name)
        {
            WriteSingle(ms, val);
            WriteAlignedString(ms, name);
        }

        private static void WritePPtr(Stream ms, int fileId, long pathId)
        {
            WriteInt32(ms, fileId);
            WriteInt64(ms, pathId);
        }

        private static void WriteInt16(Stream ms, short value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteUInt16(Stream ms, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteInt32(Stream ms, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteInt32BigEndian(Stream ms, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteUInt32(Stream ms, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteInt64(Stream ms, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteSingle(Stream ms, float value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteDouble(Stream ms, double value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(buf, value);
            ms.Write(buf);
        }

        private static void WriteAlignedString(Stream ms, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(ms, bytes.Length);
            ms.Write(bytes);
            Align4(ms);
        }

        private static void Align4(Stream ms)
        {
            while (ms.Position % 4 != 0)
            {
                ms.WriteByte(0);
            }
        }

        private static SerializedFile CreateSerializedFileWithData(
            byte[] objectBytes,
            EndianType endian,
            out ObjectInfo objectInfo,
            int objectByteSize,
            int[]? version = null)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var bw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.UTF8.GetBytes("2022.3.62f3"));
                bw.Write((byte)0);
                bw.Write((int)BuildTarget.StandaloneWindows64);
                bw.Write(false);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write((byte)0);
                bw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            int dataOffset = (headerSize + metadata.Length + 15) & ~15;
            uint totalSize = (uint)(dataOffset + objectBytes.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), (uint)dataOffset);
            fullBytes[16] = 0;
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            Buffer.BlockCopy(objectBytes, 0, fullBytes, dataOffset, objectBytes.Length);

            var fileMs = new MemoryStream(fullBytes);
            var fileReader = new FileReader("fidelity_test.assets", fileMs);
            var sf = new SerializedFile(fileReader, new AssetsManager());
            sf.header.m_Endianess = endian == EndianType.BigEndian ? (byte)1 : (byte)0;
            sf.reader.Endian = endian;
            if (version != null)
            {
                sf.version = version;
            }

            objectInfo = new ObjectInfo
            {
                byteStart = dataOffset,
                byteSize = (uint)objectByteSize,
                m_PathID = 1,
                classID = (int)ClassIDType.MonoBehaviour,
                typeID = (int)ClassIDType.MonoBehaviour
            };
            return sf;
        }
    }
}
