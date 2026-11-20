using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;
using Xunit;

namespace AssetStudio.Tests
{
    public class ShaderFlagsTests
    {
        [Theory]
        [InlineData(SerializedPropertyFlag.None, "")]
        [InlineData(SerializedPropertyFlag.HideInInspector, "[HideInInspector] ")]
        [InlineData(SerializedPropertyFlag.PerRendererData, "[PerRendererData] ")]
        [InlineData(SerializedPropertyFlag.NoScaleOffset, "[NoScaleOffset] ")]
        [InlineData(SerializedPropertyFlag.Normal, "[Normal] ")]
        [InlineData(SerializedPropertyFlag.HDR, "[HDR] ")]
        [InlineData(SerializedPropertyFlag.Gamma, "[Gamma] ")]
        [InlineData(SerializedPropertyFlag.NonModifiableTextureData, "[NonModifiableTextureData] ")]
        [InlineData(SerializedPropertyFlag.MainTexture, "[MainTexture] ")]
        [InlineData(SerializedPropertyFlag.MainColor, "[MainColor] ")]
        [InlineData(SerializedPropertyFlag.Vector2, "[Vector2] ")]
        [InlineData(SerializedPropertyFlag.Vector3, "[Vector3] ")]
        public void FormatPropertyFlags_MapsEveryUnityBitToExpectedAttribute(SerializedPropertyFlag flag, string expectedPrefix)
        {
            Assert.Equal(expectedPrefix, ShaderConverter.FormatPropertyFlags((uint)flag));
        }

        [Fact]
        public void FormatPropertyFlags_CombinesMultipleFlagsAndDeduplicatesExistingAttributes()
        {
            uint combined = (uint)(
                SerializedPropertyFlag.HideInInspector |
                SerializedPropertyFlag.PerRendererData |
                SerializedPropertyFlag.NoScaleOffset |
                SerializedPropertyFlag.Normal |
                SerializedPropertyFlag.HDR |
                SerializedPropertyFlag.Gamma |
                SerializedPropertyFlag.NonModifiableTextureData |
                SerializedPropertyFlag.MainTexture |
                SerializedPropertyFlag.MainColor |
                SerializedPropertyFlag.Vector2 |
                SerializedPropertyFlag.Vector3);

            string formatted = ShaderConverter.FormatPropertyFlags(combined);
            Assert.Equal(
                "[HideInInspector] [PerRendererData] [NoScaleOffset] [Normal] [HDR] [Gamma] [NonModifiableTextureData] [MainTexture] [MainColor] [Vector2] [Vector3] ",
                formatted);

            string deduplicated = ShaderConverter.FormatPropertyFlags(
                (uint)(SerializedPropertyFlag.HideInInspector | SerializedPropertyFlag.HDR),
                new[] { "HideInInspector", "Space" });
            Assert.Equal("[HDR] ", deduplicated);

            // Unknown upper bits must be ignored without throwing
            uint unknownBit = 1u << 28;
            Assert.Equal("[Gamma] ", ShaderConverter.FormatPropertyFlags((uint)SerializedPropertyFlag.Gamma | unknownBit));
        }

        [Fact]
        public void ShaderConvert_DecodesAllSerializedPropertyFlagsAndIntTypeEndToEnd()
        {
            var props = new[]
            {
                new TestPropertySpec
                {
                    Name = "_MainTex",
                    Description = "Albedo (RGB)",
                    Attributes = new[] { "Space" },
                    Type = SerializedPropertyType.Texture,
                    Flags = SerializedPropertyFlag.MainTexture | SerializedPropertyFlag.NoScaleOffset | SerializedPropertyFlag.PerRendererData,
                    DefaultTextureName = "white",
                    TexDim = TextureDimension.Tex2D
                },
                new TestPropertySpec
                {
                    Name = "_BumpMap",
                    Description = "Normal Map",
                    Type = SerializedPropertyType.Texture,
                    Flags = SerializedPropertyFlag.Normal | SerializedPropertyFlag.NonModifiableTextureData,
                    DefaultTextureName = "bump",
                    TexDim = TextureDimension.Tex2D
                },
                new TestPropertySpec
                {
                    Name = "_EmissionColor",
                    Description = "Emission",
                    Type = SerializedPropertyType.Color,
                    Flags = SerializedPropertyFlag.HDR | SerializedPropertyFlag.MainColor | SerializedPropertyFlag.Gamma,
                    DefValue = new[] { 1f, 0.5f, 0.25f, 1f }
                },
                new TestPropertySpec
                {
                    Name = "_InternalMode",
                    Description = "Mode",
                    Type = SerializedPropertyType.Int,
                    Flags = SerializedPropertyFlag.HideInInspector,
                    DefValue = new[] { 2f, 0f, 0f, 0f }
                },
                new TestPropertySpec
                {
                    Name = "_CustomVec",
                    Description = "Vec",
                    Type = SerializedPropertyType.Vector,
                    Flags = SerializedPropertyFlag.Vector2 | SerializedPropertyFlag.Vector3,
                    DefValue = new[] { 0.25f, 0.5f, 0.75f, 0f }
                }
            };

            var shader = BuildSyntheticShader(
                unityVersion: "2020.1.0f1",
                shaderName: "Custom/FlagTest",
                properties: props,
                passes: new[] { new TestPassSpec() });

            string output = shader.Convert();

            Assert.Contains("[Space] [PerRendererData] [NoScaleOffset] [MainTexture] _MainTex (\"Albedo (RGB)\", 2D) = \"white\" { }", output);
            Assert.Contains("[Normal] [NonModifiableTextureData] _BumpMap (\"Normal Map\", 2D) = \"bump\" { }", output);
            Assert.Contains("[HDR] [Gamma] [MainColor] _EmissionColor (\"Emission\", Color) = (1,0.5,0.25,1)", output);
            Assert.Contains("[HideInInspector] _InternalMode (\"Mode\", Int) = 2", output);
            Assert.Contains("[Vector2] [Vector3] _CustomVec (\"Vec\", Vector) = (0.25,0.5,0.75,0)", output);
        }

        [Fact]
        public void ShaderConvert_Pre2017_2ShaderWithoutZClip_DoesNotEmitSpuriousZClipOff()
        {
            var shader = BuildSyntheticShader(
                unityVersion: "5.6.0f3",
                shaderName: "Legacy/NoZClip",
                properties: Array.Empty<TestPropertySpec>(),
                passes: new[] { new TestPassSpec() });

            Assert.Null(shader.m_ParsedForm.m_SubShaders[0].m_Passes[0].m_State.zClip);
            Assert.Null(shader.m_ParsedForm.m_SubShaders[0].m_Passes[0].m_State.conservative);

            string output = shader.Convert();

            Assert.DoesNotContain("ZClip Off", output);
            Assert.DoesNotContain("Conservative True", output);
        }

        [Fact]
        public void ShaderConvert_RenderStateFlagsAndConservativeAndNamedBindings_AreFormattedCorrectly()
        {
            var literalPass = new TestPassSpec
            {
                PassTags = new[] { ("LightMode", "ForwardBase") },
                AlphaToMask = 1f,
                ZClip = 0f,
                ZTest = 7f, // GEqual
                ZWrite = 0f,
                Culling = 1f, // Front
                Conservative = 1f,
                OffsetFactor = -1.5f,
                OffsetUnits = -2f,
                Lighting = true,
                SrcBlend = 5f, // SrcAlpha
                DestBlend = 10f, // OneMinusSrcAlpha
                ColorMask = 0x2 | 0x4, // RG
                GpuProgramID = 42,
                SubProgramGlslCode = "void main() {}"
            };

            var propertyDrivenPass = new TestPassSpec
            {
                AlphaToMaskName = "_AlphaToMask",
                ZClipName = "_ZClip",
                ZTestName = "_ZTest",
                ZWriteName = "_ZWrite",
                CullingName = "_Cull",
                ConservativeName = "_Conservative",
                OffsetFactorName = "_OffsetFactor",
                OffsetUnitsName = "_OffsetUnits",
                SrcBlendName = "_SrcBlend",
                DestBlendName = "_DstBlend",
                ColorMaskName = "_ColorMask",
                GpuProgramID = 99
            };

            var shader = BuildSyntheticShader(
                unityVersion: "2020.1.0f1",
                shaderName: "Custom/StateFlags",
                properties: Array.Empty<TestPropertySpec>(),
                passes: new[] { literalPass, propertyDrivenPass },
                dependencies: new[] { ("ShadowCaster", "Hidden/ShadowCaster") },
                fallbackName: "Diffuse",
                customEditorName: "CustomShaderGUI");

            string output = shader.Convert();

            Assert.Contains("Tags { \"LightMode\" = \"ForwardBase\" }", output);
            Assert.Contains("  AlphaToMask On\n", output);
            Assert.Contains("  ZClip Off\n", output);
            Assert.Contains("  ZTest GEqual\n", output);
            Assert.Contains("  ZWrite Off\n", output);
            Assert.Contains("  Cull Front\n", output);
            Assert.Contains("  Conservative True\n", output);
            Assert.Contains("  Offset -1.5, -2\n", output);
            Assert.Contains("  Lighting On\n", output);
            Assert.Contains("  Blend SrcAlpha OneMinusSrcAlpha\n", output);
            Assert.Contains("  ColorMask RG 0\n", output);
            Assert.Contains("  GpuProgramID 42\n", output);
            Assert.Contains("void main() {}", output);

            Assert.Contains("  AlphaToMask [_AlphaToMask]\n", output);
            Assert.Contains("  ZClip [_ZClip]\n", output);
            Assert.Contains("  ZTest [_ZTest]\n", output);
            Assert.Contains("  ZWrite [_ZWrite]\n", output);
            Assert.Contains("  Cull [_Cull]\n", output);
            Assert.Contains("  Conservative [_Conservative]\n", output);
            Assert.Contains("  Offset [_OffsetFactor], [_OffsetUnits]\n", output);
            Assert.Contains("  Blend [_SrcBlend] [_DstBlend]\n", output);
            Assert.Contains("  ColorMask [_ColorMask] 0\n", output);

            Assert.Contains("Fallback \"Diffuse\"", output);
            Assert.Contains("CustomEditor \"CustomShaderGUI\"", output);
            Assert.Contains("Dependency \"ShadowCaster\" = \"Hidden/ShadowCaster\"", output);
        }

        private sealed class TestPropertySpec
        {
            public string Name = "_Prop";
            public string Description = "Prop";
            public string[] Attributes = Array.Empty<string>();
            public SerializedPropertyType Type = SerializedPropertyType.Float;
            public SerializedPropertyFlag Flags = SerializedPropertyFlag.None;
            public float[] DefValue = { 0f, 0f, 0f, 0f };
            public string DefaultTextureName = "";
            public TextureDimension TexDim = TextureDimension.Tex2D;
        }

        private sealed class TestPassSpec
        {
            public (string Key, string Value)[] PassTags = Array.Empty<(string, string)>();
            public float AlphaToMask = 0f;
            public string AlphaToMaskName = "";
            public float ZClip = 1f;
            public string ZClipName = "";
            public float ZTest = 4f; // LEqual
            public string ZTestName = "";
            public float ZWrite = 1f;
            public string ZWriteName = "";
            public float Culling = 2f; // Back
            public string CullingName = "";
            public float Conservative = 0f;
            public string ConservativeName = "";
            public float OffsetFactor = 0f;
            public string OffsetFactorName = "";
            public float OffsetUnits = 0f;
            public string OffsetUnitsName = "";
            public float SrcBlend = 1f;
            public string SrcBlendName = "";
            public float DestBlend = 0f;
            public string DestBlendName = "";
            public float ColorMask = 0xf;
            public string ColorMaskName = "";
            public bool Lighting = false;
            public int GpuProgramID = 0;
            public string? SubProgramGlslCode;
        }

        private static Shader BuildSyntheticShader(
            string unityVersion,
            string shaderName,
            TestPropertySpec[] properties,
            TestPassSpec[] passes,
            (string From, string To)[]? dependencies = null,
            string fallbackName = "",
            string customEditorName = "")
        {
            var serializedFile = CreateMinimalSerializedFile(unityVersion);
            int[] version = serializedFile.version;
            bool hasZClip = version[0] > 2017 || (version[0] == 2017 && version[1] >= 2);
            bool hasConservative = version[0] >= 2020;
            bool hasRayTracing = version[0] > 2019 || (version[0] == 2019 && version[1] >= 3);

            byte[]? rawBlob = null;
            string? glsl = passes.Length > 0 ? passes[0].SubProgramGlslCode : null;
            if (glsl != null)
            {
                rawBlob = BuildSingleGlslProgramBlob(glsl, version);
            }

            byte[] compressedBlob = Array.Empty<byte>();
            uint[][] offsets;
            uint[][] compressedLengths;
            uint[][] decompressedLengths;
            ShaderCompilerPlatform[] platforms;

            if (rawBlob != null)
            {
                var compressed = new byte[LZ4Codec.MaximumOutputSize(rawBlob.Length)];
                int encodedLength = LZ4Codec.Encode(rawBlob, 0, rawBlob.Length, compressed, 0, compressed.Length);
                compressedBlob = new byte[encodedLength];
                Buffer.BlockCopy(compressed, 0, compressedBlob, 0, encodedLength);

                platforms = new[] { ShaderCompilerPlatform.OpenGLCore };
                offsets = new[] { new[] { 0u } };
                compressedLengths = new[] { new[] { (uint)encodedLength } };
                decompressedLengths = new[] { new[] { (uint)rawBlob.Length } };
            }
            else
            {
                platforms = Array.Empty<ShaderCompilerPlatform>();
                offsets = Array.Empty<uint[]>();
                compressedLengths = Array.Empty<uint[]>();
                decompressedLengths = Array.Empty<uint[]>();
            }

            byte[] shaderBytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                // NamedObject.m_Name
                WriteAlignedString(bw, shaderName);

                // SerializedProperties
                bw.Write(properties.Length);
                foreach (var prop in properties)
                {
                    WriteAlignedString(bw, prop.Name);
                    WriteAlignedString(bw, prop.Description);
                    bw.Write(prop.Attributes.Length);
                    foreach (var attr in prop.Attributes)
                    {
                        WriteAlignedString(bw, attr);
                    }
                    bw.Write((int)prop.Type);
                    bw.Write((uint)prop.Flags);
                    for (int i = 0; i < 4; i++)
                    {
                        bw.Write(i < prop.DefValue.Length ? prop.DefValue[i] : 0f);
                    }
                    WriteAlignedString(bw, prop.DefaultTextureName);
                    bw.Write((int)prop.TexDim);
                }

                // SerializedSubShaders (1)
                bw.Write(1);
                bw.Write(passes.Length);
                foreach (var pass in passes)
                {
                    // m_NameIndices (0)
                    bw.Write(0);
                    // m_Type = Normal (0)
                    bw.Write((int)PassType.Normal);

                    // SerializedShaderState
                    WriteAlignedString(bw, ""); // m_Name
                    for (int rt = 0; rt < 8; rt++)
                    {
                        if (rt == 0)
                        {
                            WriteFloatVal(bw, pass.SrcBlend, pass.SrcBlendName);
                            WriteFloatVal(bw, pass.DestBlend, pass.DestBlendName);
                            WriteFloatVal(bw, 1f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, pass.ColorMask, pass.ColorMaskName);
                        }
                        else
                        {
                            WriteFloatVal(bw, 1f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, 1f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, 0f, "");
                            WriteFloatVal(bw, 0xf, "");
                        }
                    }
                    bw.Write(false); // rtSeparateBlend
                    AlignStream(bw);

                    if (hasZClip)
                    {
                        WriteFloatVal(bw, pass.ZClip, pass.ZClipName);
                    }
                    WriteFloatVal(bw, pass.ZTest, pass.ZTestName);
                    WriteFloatVal(bw, pass.ZWrite, pass.ZWriteName);
                    WriteFloatVal(bw, pass.Culling, pass.CullingName);
                    if (hasConservative)
                    {
                        WriteFloatVal(bw, pass.Conservative, pass.ConservativeName);
                    }
                    WriteFloatVal(bw, pass.OffsetFactor, pass.OffsetFactorName);
                    WriteFloatVal(bw, pass.OffsetUnits, pass.OffsetUnitsName);
                    WriteFloatVal(bw, pass.AlphaToMask, pass.AlphaToMaskName);

                    // StencilOp, Front, Back
                    for (int s = 0; s < 3; s++)
                    {
                        WriteFloatVal(bw, 0f, ""); // pass
                        WriteFloatVal(bw, 0f, ""); // fail
                        WriteFloatVal(bw, 0f, ""); // zFail
                        WriteFloatVal(bw, 8f, ""); // comp (Always)
                    }
                    WriteFloatVal(bw, 255f, ""); // stencilReadMask
                    WriteFloatVal(bw, 255f, ""); // stencilWriteMask
                    WriteFloatVal(bw, 0f, "");   // stencilRef

                    WriteFloatVal(bw, 0f, ""); // fogStart
                    WriteFloatVal(bw, 0f, ""); // fogEnd
                    WriteFloatVal(bw, 0f, ""); // fogDensity
                    for (int c = 0; c < 4; c++)
                    {
                        WriteFloatVal(bw, 0f, "");
                    }
                    WriteAlignedString(bw, ""); // fogColor.name
                    bw.Write((int)FogMode.Unknown);
                    bw.Write(pass.GpuProgramID);
                    bw.Write(0); // m_State.m_Tags
                    bw.Write(0); // m_LOD
                    bw.Write(pass.Lighting);
                    AlignStream(bw);

                    bw.Write(0u); // m_ProgramMask

                    // progVertex
                    WriteSerializedProgram(bw, pass.SubProgramGlslCode != null ? 1 : 0, hasRayTracing, version);
                    // progFragment, progGeometry, progHull, progDomain
                    WriteSerializedProgram(bw, 0, hasRayTracing, version);
                    WriteSerializedProgram(bw, 0, hasRayTracing, version);
                    WriteSerializedProgram(bw, 0, hasRayTracing, version);
                    WriteSerializedProgram(bw, 0, hasRayTracing, version);
                    if (hasRayTracing)
                    {
                        WriteSerializedProgram(bw, 0, hasRayTracing, version);
                    }

                    bw.Write(false); // m_HasInstancingVariant
                    if (version[0] >= 2018)
                    {
                        bw.Write(false); // m_HasProceduralInstancingVariant
                    }
                    AlignStream(bw);
                    WriteAlignedString(bw, ""); // m_UseName
                    WriteAlignedString(bw, ""); // m_Name
                    WriteAlignedString(bw, ""); // m_TextureName
                    bw.Write(pass.PassTags.Length);
                    foreach (var (key, val) in pass.PassTags)
                    {
                        WriteAlignedString(bw, key);
                        WriteAlignedString(bw, val);
                    }
                }
                bw.Write(0);   // SubShader.m_Tags
                bw.Write(100); // SubShader.m_LOD

                WriteAlignedString(bw, shaderName);
                WriteAlignedString(bw, customEditorName);
                WriteAlignedString(bw, fallbackName);

                var deps = dependencies ?? Array.Empty<(string, string)>();
                bw.Write(deps.Length);
                foreach (var (from, to) in deps)
                {
                    WriteAlignedString(bw, from);
                    WriteAlignedString(bw, to);
                }
                bw.Write(false); // m_DisableNoSubshadersMessage
                AlignStream(bw);

                // Shader fields
                bw.Write(platforms.Length);
                foreach (var p in platforms)
                {
                    bw.Write((uint)p);
                }
                if (hasRayTracing) // 2019.3+
                {
                    WriteUInt32ArrayArray(bw, offsets);
                    WriteUInt32ArrayArray(bw, compressedLengths);
                    WriteUInt32ArrayArray(bw, decompressedLengths);
                }
                else
                {
                    bw.Write(offsets.Length);
                    foreach (var arr in offsets) bw.Write(arr[0]);
                    bw.Write(compressedLengths.Length);
                    foreach (var arr in compressedLengths) bw.Write(arr[0]);
                    bw.Write(decompressedLengths.Length);
                    foreach (var arr in decompressedLengths) bw.Write(arr[0]);
                }

                bw.Write(compressedBlob.Length);
                bw.Write(compressedBlob);
                AlignStream(bw);

                bw.Write(0); // m_DependenciesCount
                if (version[0] >= 2018)
                {
                    bw.Write(0); // m_NonModifiableTexturesCount
                }
                bw.Write(true); // m_ShaderIsBaked
                AlignStream(bw);

                bw.Flush();
                shaderBytes = ms.ToArray();
            }

            var objectInfo = new ObjectInfo
            {
                byteStart = 0,
                byteSize = (uint)shaderBytes.Length,
                typeID = (int)ClassIDType.Shader,
                classID = (int)ClassIDType.Shader,
                m_PathID = 1
            };
            using var shaderStream = new MemoryStream(shaderBytes);
            using var endianReader = new EndianBinaryReader(shaderStream, EndianType.LittleEndian);
            var objectReader = new ObjectReader(endianReader, serializedFile, objectInfo);
            return new Shader(objectReader);
        }

        private static void WriteSerializedProgram(BinaryWriter bw, int subProgramCount, bool has2019_3, int[] version)
        {
            bw.Write(subProgramCount);
            for (int i = 0; i < subProgramCount; i++)
            {
                bw.Write(0u); // m_BlobIndex
                bw.Write(0);  // m_Channels count
                AlignStream(bw);
                bw.Write(0u); // m_SourceMap
                if (version[0] >= 2019 && version[0] < 2021)
                {
                    bw.Write(0); // m_GlobalKeywordIndices
                    AlignStream(bw);
                    bw.Write(0); // m_LocalKeywordIndices
                    AlignStream(bw);
                }
                else
                {
                    bw.Write(0); // m_KeywordIndices
                    if (version[0] >= 2017)
                    {
                        AlignStream(bw);
                    }
                }
                bw.Write((sbyte)0); // m_ShaderHardwareTier
                bw.Write((sbyte)ShaderGpuProgramType.GLCore32); // m_GpuProgramType
                AlignStream(bw);
                for (int p = 0; p < 7; p++)
                {
                    bw.Write(0); // param counts
                }
                if (version[0] >= 2017)
                {
                    bw.Write(0); // numSamplers
                }
                if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 2))
                {
                    bw.Write(0); // m_ShaderRequirements
                }
            }
        }

        private static byte[] BuildSingleGlslProgramBlob(string glslCode, int[] version)
        {
            byte[] subProgramBytes;
            using (var subMs = new MemoryStream())
            using (var subBw = new BinaryWriter(subMs, Encoding.UTF8, leaveOpen: true))
            {
                subBw.Write(201806140); // Unity 2019.1-2021.1 subprogram magic
                subBw.Write((int)ShaderGpuProgramType.GLCore32);
                subBw.Write(new byte[16]); // ALU, TEX, Flow, TempRegister stats
                subBw.Write(0); // m_KeywordsSize
                subBw.Write(0); // m_LocalKeywordsSize
                var codeBytes = Encoding.UTF8.GetBytes(glslCode);
                subBw.Write(codeBytes.Length);
                subBw.Write(codeBytes);
                AlignStream(subBw);
                subBw.Flush();
                subProgramBytes = subMs.ToArray();
            }

            bool hasSegment = version[0] > 2019 || (version[0] == 2019 && version[1] >= 3);
            int headerSize = 4 + (hasSegment ? 12 : 8);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(1); // entry count
            bw.Write(headerSize);
            bw.Write(subProgramBytes.Length);
            if (hasSegment)
            {
                bw.Write(0); // segment
            }
            bw.Write(subProgramBytes);
            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteFloatVal(BinaryWriter bw, float val, string name)
        {
            bw.Write(val);
            WriteAlignedString(bw, name);
        }

        private static void WriteUInt32ArrayArray(BinaryWriter bw, uint[][] arrays)
        {
            bw.Write(arrays.Length);
            foreach (var inner in arrays)
            {
                bw.Write(inner.Length);
                foreach (var v in inner)
                {
                    bw.Write(v);
                }
            }
        }

        private static void WriteAlignedString(BinaryWriter bw, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            bw.Write(bytes.Length);
            if (bytes.Length > 0)
            {
                bw.Write(bytes);
                AlignStream(bw);
            }
        }

        private static void AlignStream(BinaryWriter bw)
        {
            long mod = bw.BaseStream.Position % 4;
            if (mod != 0)
            {
                for (int i = 0; i < 4 - mod; i++)
                {
                    bw.Write((byte)0);
                }
            }
        }

        private static SerializedFile CreateMinimalSerializedFile(string unityVersion)
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

            var ms = new MemoryStream(fullBytes);
            var fileReader = new FileReader("minimal_shader.assets", ms);
            return new SerializedFile(fileReader, new AssetsManager());
        }
    }
}
