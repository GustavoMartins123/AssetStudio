using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using K4os.Compression.LZ4;
using Xunit;

namespace AssetStudio.Tests
{
    public class ModernUnityCompatibilityTests
    {
        // =========================================================================
        // 1. P0 — ShaderConverter on Unity >= 2021.3.10f1
        //    (m_PlayerSubPrograms + m_ParameterBlobIndices)
        // =========================================================================

        [Theory]
        [InlineData("2021.3.10f1")]
        [InlineData("2022.3.39f1")]
        [InlineData("6000.4.12f1")]
        public void ShaderConverter_Unity2021_3_10Plus_SkipsParameterBlobsAndExportsPlayerSubPrograms(string unityVersion)
        {
            const string vertexGlsl = "void main() { gl_Position = vec4(1.0); }";
            const string fragmentGlsl = "void main() { gl_FragColor = vec4(0.5, 0.5, 1.0, 1.0); }";

            var shader = BuildModernPlayerShader(
                unityVersion: unityVersion,
                shaderName: "Custom/ModernPlayerShader",
                platform: ShaderCompilerPlatform.OpenGLCore,
                vertexProgramType: ShaderGpuProgramType.GLCore32,
                fragmentProgramType: ShaderGpuProgramType.GLCore32,
                vertexCode: Encoding.UTF8.GetBytes(vertexGlsl),
                fragmentCode: Encoding.UTF8.GetBytes(fragmentGlsl),
                vertexKeywordsInBlob: new[] { "PIXELSNAP_ON" },
                fragmentKeywordsInBlob: Array.Empty<string>(),
                shaderKeywordNames: new[] { "PIXELSNAP_ON", "_MAIN_LIGHT" },
                vertexKeywordIndices: new ushort[] { 0 },
                fragmentKeywordIndices: new ushort[] { 1 },
                multiTier: false);

            var converted = shader.Convert();

            Assert.Contains("Shader \"Custom/ModernPlayerShader\"", converted);
            Assert.Contains("Program \"vp\" {", converted);
            Assert.Contains("Program \"fp\" {", converted);
            Assert.Contains("SubProgram \"glcore \" {", converted);
            Assert.Contains("Keywords { \"PIXELSNAP_ON\" }", converted);
            Assert.Contains("Keywords { \"_MAIN_LIGHT\" }", converted);
            Assert.Contains(vertexGlsl, converted);
            Assert.Contains(fragmentGlsl, converted);
        }

        [Fact]
        public void ShaderConverter_Unity2022_D3D11MultiTierPlayerSubPrograms_SkipsParameterBlobsAndEmitsHardwareTiers()
        {
            var shader = BuildModernPlayerShader(
                unityVersion: "2022.3.39f1",
                shaderName: "Custom/ModernD3D11MultiTier",
                platform: ShaderCompilerPlatform.D3D11,
                vertexProgramType: ShaderGpuProgramType.DX11VertexSM40,
                fragmentProgramType: ShaderGpuProgramType.DX11PixelSM40,
                vertexCode: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x44, 0x58, 0x42, 0x43 },
                fragmentCode: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x44, 0x58, 0x42, 0x43 },
                vertexKeywordsInBlob: new[] { "INSTANCING_ON" },
                fragmentKeywordsInBlob: new[] { "ETC1_EXTERNAL_ALPHA" },
                shaderKeywordNames: new[] { "INSTANCING_ON", "ETC1_EXTERNAL_ALPHA" },
                vertexKeywordIndices: new ushort[] { 0 },
                fragmentKeywordIndices: new ushort[] { 1 },
                multiTier: true);

            var converted = shader.Convert();

            Assert.Contains("Shader \"Custom/ModernD3D11MultiTier\"", converted);
            Assert.Contains("Program \"vp\" {", converted);
            Assert.Contains("Program \"fp\" {", converted);
            Assert.Contains("SubProgram \"d3d11 hw_tier01 \" {", converted);
            Assert.Contains("SubProgram \"d3d11 hw_tier03 \" {", converted);
            Assert.Contains("Keywords { \"INSTANCING_ON\" }", converted);
            Assert.Contains("Keywords { \"ETC1_EXTERNAL_ALPHA\" }", converted);
            Assert.Contains("// shader disassembly not supported on DXBC", converted);
        }

        // =========================================================================
        // 2. P1 — AssetsManager UnityPlayer.dll / .exe Version Fallback
        // =========================================================================

        [Theory]
        [InlineData("2019.4.40.16762411", "2019.4.40.16762411", 2019, 4, 40, "2019.4.40f1")]
        [InlineData("6000.4.12f1 (3ca267ce8005)", "6000.4.12.3973735", 6000, 4, 12, "6000.4.12f1")]
        [InlineData("2021.3.35f1_157b46ce122a", "2021.3.35.1407814", 2021, 3, 35, "2021.3.35f1")]
        [InlineData("2022.3.39b4 (deadbeef)", "", 2022, 3, 39, "2022.3.39b4")]
        [InlineData("", "5.6.7.12345", 5, 6, 7, "5.6.7f1")]
        [InlineData("", "", 2019, 4, 40, "2019.4.40f1")]
        [InlineData("0.0.0", "0.0.0", 0, 0, 0, "")]
        [InlineData("1.0.0.0", "1.0.0.0", 1, 0, 0, "")]
        [InlineData("5.x.x", "", 0, 0, 0, "")]
        public void AssetsManager_TryNormalizeUnityVersionFromFileVersionInfo_HandlesAllUnityFormats(
            string productVersion,
            string fileVersion,
            int major,
            int minor,
            int build,
            string expected)
        {
            var actual = AssetsManager.TryNormalizeUnityVersionFromFileVersionInfo(
                productVersion,
                fileVersion,
                major,
                minor,
                build);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void AssetsManager_StrippedUnityFsBundle_InfersVersionFromUnityPlayerDllWhenGlobalGameManagersEncrypted()
        {
            var tempRoot = CreateTempDirectory("unityplayer_fallback");
            try
            {
                var gameRoot = Path.Combine(tempRoot, "ExiliumLike");
                var dataDir = Path.Combine(gameRoot, "ExiliumLike_Data");
                var bundleDir = Path.Combine(dataDir, "StreamingAssets", "aa", "StandaloneWindows64");
                Directory.CreateDirectory(bundleDir);

                // Encrypted/unreadable globalgamemanagers (matching GF2_Exilium partial header encryption)
                var encryptedGgm = new byte[1024];
                encryptedGgm[0] = 0x4E;
                encryptedGgm[1] = 0x50;
                encryptedGgm[2] = 0x91;
                encryptedGgm[3] = 0x0F;
                File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), encryptedGgm);

                // Minimal valid PE UnityPlayer.dll with VS_VERSION_INFO stamped as 2019.4.40.16762411
                var unityPlayerPath = Path.Combine(gameRoot, "UnityPlayer.dll");
                File.WriteAllBytes(unityPlayerPath, BuildMinimalVersionedPeDll(
                    major: 2019,
                    minor: 4,
                    build: 40,
                    priv: 50731,
                    productVersion: "2019.4.40.16762411",
                    fileVersion: "2019.4.40.16762411"));

                // Sanity-check that both FileVersionInfo and cross-platform PE resource parser read the synthetic PE cleanly
                Assert.Equal("2019.4.40f1", AssetsManager.TryReadUnityVersionFromFileVersionInfo(unityPlayerPath));
                Assert.Equal("2019.4.40f1", AssetsManager.TryReadUnityVersionFromPeResource(unityPlayerPath));

                // Stripped UnityFS (0.0.0) bundle containing a stripped SerializedFile
                var bundlePath = Path.Combine(bundleDir, "stripped_test.bundle");
                var strippedSfBytes = BuildMinimalSerializedFileBytes("0.0.0");
                var bundleBytes = BuildUncompressedUnityFsBundle("CAB-stripped01", strippedSfBytes, unityRevision: "0.0.0");
                File.WriteAllBytes(bundlePath, bundleBytes);

                using var am = new AssetsManager();
                // Explicitly leave SpecifyUnityVersion empty
                Assert.Equal(string.Empty, am.SpecifyUnityVersion);

                am.LoadFiles(bundlePath);

                var sf = Assert.Single(am.assetsFileList);
                Assert.Equal("2019.4.40f1", sf.unityVersion);
                Assert.Equal(new[] { 2019, 4, 40, 1 }, sf.version.Take(4).ToArray());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetsManager_StrippedUnityFsBundle_FallsBackToMainExeWhenUnityPlayerDllMissing()
        {
            var tempRoot = CreateTempDirectory("exe_fallback");
            try
            {
                var gameRoot = Path.Combine(tempRoot, "MyGame");
                var dataDir = Path.Combine(gameRoot, "MyGame_Data");
                var bundleDir = Path.Combine(dataDir, "StreamingAssets");
                Directory.CreateDirectory(bundleDir);

                File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });

                // No UnityPlayer.dll; place versioned main executable MyGame.exe next to MyGame_Data
                var exePath = Path.Combine(gameRoot, "MyGame.exe");
                File.WriteAllBytes(exePath, BuildMinimalVersionedPeDll(
                    major: 6000,
                    minor: 3,
                    build: 2,
                    priv: 30623,
                    productVersion: "6000.3.2f1 (a9779f353c9b)",
                    fileVersion: "6000.3.2.11106207"));
                Assert.Equal("6000.3.2f1", AssetsManager.TryReadUnityVersionFromPeResource(exePath));

                var bundlePath = Path.Combine(bundleDir, "stripped_exe.bundle");
                var strippedSfBytes = BuildMinimalSerializedFileBytes("0.0.0");
                File.WriteAllBytes(bundlePath, BuildUncompressedUnityFsBundle("CAB-stripped02", strippedSfBytes, unityRevision: "0.0.0"));

                using var am = new AssetsManager();
                am.LoadFiles(bundlePath);

                var sf = Assert.Single(am.assetsFileList);
                Assert.Equal("6000.3.2f1", sf.unityVersion);
                Assert.Equal(new[] { 6000, 3, 2, 1 }, sf.version.Take(4).ToArray());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 3. P1 — BinaryReaderExtensions Allows Legitimate >512 MiB ObjectReader Arrays
        // =========================================================================

        [Fact]
        public void BinaryReaderExtensions_ObjectReader_AllowsValidOver512MiBArrayWithoutAllocatingInTest_WhileGuardingCorruptAndUnboundedStreams()
        {
            // Mirrors Homura Hime sharedassets74.assets AnimationClip PathID 738:
            // byteSize = 756,744,572 (~721.7 MiB), StreamedClip.data = 189,052,886 uint32s (756,211,544 bytes)
            const uint objectByteSize = 756_744_572u;
            const int validUintCount = 189_052_886;
            const long virtualStreamLength = 800_000_000L;

            var sf = CreateMinimalSerializedFile("6000.0.28f1");
            var objInfo = new ObjectInfo
            {
                m_PathID = 738,
                byteStart = 128,
                byteSize = objectByteSize,
                typeID = (int)ClassIDType.AnimationClip,
                classID = (int)ClassIDType.AnimationClip
            };

            using (var virtualStream = new VirtualSeekableStream(virtualStreamLength))
            using (var endianReader = new EndianBinaryReader(virtualStream, EndianType.LittleEndian))
            using (var objReader = new ObjectReader(endianReader, sf, objInfo))
            {
                objReader.Position = objInfo.byteStart + 64;

                // 1) Valid >512 MiB array bounded by ObjectReader.byteSize and stream length MUST pass!
                objReader.CheckArrayLength(validUintCount, sizeof(uint));

                // 2) Exceeding remaining object size MUST throw EndOfStreamException
                const int overObjectUintCount = 189_186_144; // 756,744,576 bytes > 756,744,508 remainingObj
                Assert.Throws<EndOfStreamException>(() => objReader.CheckArrayLength(overObjectUintCount, sizeof(uint)));

                // 3) Exceeding Array.MaxLength bytes (e.g. int.MaxValue * 4) MUST throw OverflowException
                Assert.Throws<OverflowException>(() => objReader.CheckArrayLength(int.MaxValue, sizeof(uint)));

                // 4) Negative length MUST throw OverflowException
                Assert.Throws<OverflowException>(() => objReader.CheckArrayLength(-1, sizeof(uint)));
            }

            // 5) Truncated stream (smaller than ObjectReader.byteSize) MUST still throw EndOfStreamException
            using (var shortStream = new VirtualSeekableStream(4096))
            using (var shortEndian = new EndianBinaryReader(shortStream, EndianType.LittleEndian))
            using (var shortObjReader = new ObjectReader(shortEndian, sf, objInfo))
            {
                shortObjReader.Position = objInfo.byteStart;
                Assert.Throws<EndOfStreamException>(() => shortObjReader.CheckArrayLength(validUintCount, sizeof(uint)));
            }

            // 6) Non-ObjectReader (generic BinaryReader) MUST retain the 512 MiB untrusted-stream OverflowException guard!
            using (var virtualStream = new VirtualSeekableStream(virtualStreamLength))
            using (var rawReader = new BinaryReader(virtualStream))
            {
                Assert.Throws<OverflowException>(() => rawReader.CheckArrayLength(validUintCount, sizeof(uint)));
            }
        }

        // =========================================================================
        // 4. P2 — AddressablesCatalogAnalyzer Binary Catalog String Boundaries
        // =========================================================================

        [Fact]
        public void AddressablesCatalogAnalyzer_BinaryCatalog_ExtractsExactProviderAndBundleNamesWithoutTrailingGarbage()
        {
            var tempRoot = CreateTempDirectory("catalog_bin_boundaries");
            try
            {
                var aaDir = Path.Combine(tempRoot, "Game_Data", "StreamingAssets", "aa");
                Directory.CreateDirectory(aaDir);

                // 1) Real Unity Addressables 2.x BinaryStorageBuffer (magic 0x0DE38942) layout reproducing
                //    the exact GF2_Exilium byte sequence where next int32 length 0x4C ('L') or 0x74 ('t')
                //    or Hash128 byte 0x41 ('A') immediately follows the string payload.
                var magicBinPath = Path.Combine(aaDir, "catalog.bin");
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
                {
                    bw.Write(0x0DE38942u); // BinaryStorageBuffer magic
                    bw.Write(1u);          // version 1
                    WriteLengthPrefixedAscii(bw, "AddressablesMainContentCatalog");
                    WriteLengthPrefixedAscii(bw, "UnityEngine.ResourceManagement.ResourceProviders.InstanceProvider"); // len 0x41 ('A')
                    WriteLengthPrefixedAscii(bw, "Unity.ResourceManagement, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"); // len 0x4C ('L')!
                    WriteLengthPrefixedAscii(bw, "UnityEngine.ResourceManagement.ResourceProviders.SceneProvider");
                    WriteLengthPrefixedAscii(bw, "SceneProvider");
                    bw.Write(0x00001567u); // trailing offset starting with 0x67 ('g')
                    WriteLengthPrefixedAscii(bw, "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider");
                    WriteLengthPrefixedAscii(bw, "97d94d143d5e7e57ece1b6760333965b.bundle"); // len 0x27
                    // Next string len is 0x74 ('t')! Raw ASCII scanner used to emit ".bundlet"
                    WriteLengthPrefixedAscii(bw, "{UnityEngine.AddressableAssets.Addressables.RuntimePath}\\StandaloneWindows64\\97d94d143d5e7e57ece1b6760333965b.bundle");
                    bw.Write((byte)0x41); // Hash128 starting with 'A'
                    bw.Write((byte)0x8D);
                    bw.Flush();
                    File.WriteAllBytes(magicBinPath, ms.ToArray());
                }

                // 2) Generic non-magic length-prefixed binary catalog (no 0x0DE38942 header)
                var genericLenBinPath = Path.Combine(aaDir, "catalog_generic.bin");
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
                {
                    bw.Write(Encoding.ASCII.GetBytes("CUST\0\0\0\0"));
                    WriteLengthPrefixedAscii(bw, "UnityEngine.ResourceManagement.ResourceProviders.InstanceProvider");
                    WriteLengthPrefixedAscii(bw, "Unity.ResourceManagement, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"); // len 0x4C ('L')
                    bw.Flush();
                    File.WriteAllBytes(genericLenBinPath, ms.ToArray());
                }

                var warnings = new List<string>();
                var analyzer = new AddressablesCatalogAnalyzer();
                var (index, hints) = analyzer.AnalyzeCatalogs(tempRoot, new[] { magicBinPath, genericLenBinPath }, warnings);

                Assert.Empty(warnings);
                Assert.Contains("UnityEngine.ResourceManagement.ResourceProviders.InstanceProvider", index.Providers);
                Assert.Contains("UnityEngine.ResourceManagement.ResourceProviders.SceneProvider", index.Providers);
                Assert.Contains("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider", index.Providers);
                Assert.DoesNotContain(index.Providers, p => p.EndsWith("InstanceProviderL", StringComparison.Ordinal));
                Assert.DoesNotContain(index.Providers, p => p.EndsWith("ProviderL", StringComparison.Ordinal));
                Assert.Empty(hints.Hints); // All providers are standard Unity providers; no bogus hints

                Assert.Contains(index.Bundles, b => b.BundleFileName == "97d94d143d5e7e57ece1b6760333965b.bundle");
                Assert.DoesNotContain(index.Bundles, b => b.BundleFileName.EndsWith(".bundlet", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(index.Bundles, b => b.BundleFileName.EndsWith(".bundleA", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 5. Read-Only Real-Game Integration Tests (Gracefully Skip When Absent on CI)
        // =========================================================================

        // =========================================================================
        // Helpers
        // =========================================================================

        private static Shader BuildModernPlayerShader(
            string unityVersion,
            string shaderName,
            ShaderCompilerPlatform platform,
            ShaderGpuProgramType vertexProgramType,
            ShaderGpuProgramType fragmentProgramType,
            byte[] vertexCode,
            byte[] fragmentCode,
            string[] vertexKeywordsInBlob,
            string[] fragmentKeywordsInBlob,
            string[] shaderKeywordNames,
            ushort[] vertexKeywordIndices,
            ushort[] fragmentKeywordIndices,
            bool multiTier)
        {
            var sf = CreateMinimalSerializedFile(unityVersion);
            var version = sf.version;

            // Blob entries:
            // [0] = parameter reflection blob ("$Globals" + bogus huge ints that would crash ShaderSubProgram)
            // [1] = executable vertex ShaderSubProgram
            // [2] = parameter reflection blob ("UnityPerDraw" + bogus huge ints)
            // [3] = executable fragment ShaderSubProgram
            var paramBlob0 = BuildParameterReflectionBlob("$Globals");
            var vertexBlob = BuildSubProgram202012090Blob(vertexProgramType, vertexKeywordsInBlob, vertexCode);
            var paramBlob2 = BuildParameterReflectionBlob("UnityPerDraw");
            var fragmentBlob = BuildSubProgram202012090Blob(fragmentProgramType, fragmentKeywordsInBlob, fragmentCode);

            var rawProgramBlob = PackShaderProgramSegment0(paramBlob0, vertexBlob, paramBlob2, fragmentBlob);
            var maxOut = LZ4Codec.MaximumOutputSize(rawProgramBlob.Length);
            var compressedBuf = new byte[maxOut];
            var compressedLen = LZ4Codec.Encode(rawProgramBlob, 0, rawProgramBlob.Length, compressedBuf, 0, compressedBuf.Length);
            var compressedBlob = new byte[compressedLen];
            Buffer.BlockCopy(compressedBuf, 0, compressedBlob, 0, compressedLen);

            byte[] shaderBytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteAlignedString(bw, shaderName);

                // SerializedProperties (0)
                bw.Write(0);

                // SerializedSubShaders (1)
                bw.Write(1);
                bw.Write(1); // 1 pass

                // SerializedPass
                if ((version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) && version[0] < 2023)
                {
                    bw.Write(0); // m_EditorDataHash
                    AlignStream(bw);
                    bw.Write(0); // m_Platforms
                    AlignStream(bw);
                }

                bw.Write(0); // m_NameIndices
                bw.Write((int)PassType.Normal);
                WriteMinimalShaderState2021Plus(bw);
                bw.Write(3u); // m_ProgramMask

                // progVertex (with m_PlayerSubPrograms + m_ParameterBlobIndices, empty m_SubPrograms)
                WriteModernSerializedProgram(bw, version, blobIndex: 1u, paramBlobIndex: 0u, vertexProgramType, vertexKeywordIndices, multiTier);
                // progFragment
                WriteModernSerializedProgram(bw, version, blobIndex: 3u, paramBlobIndex: 2u, fragmentProgramType, fragmentKeywordIndices, multiTier);
                // progGeometry, progHull, progDomain, progRayTracing (empty)
                for (int s = 0; s < 4; s++)
                {
                    WriteEmptyModernSerializedProgram(bw, version);
                }

                bw.Write(false); // m_HasInstancingVariant
                bw.Write(false); // m_HasProceduralInstancingVariant
                AlignStream(bw);
                WriteAlignedString(bw, ""); // m_UseName
                WriteAlignedString(bw, "Forward"); // m_Name
                WriteAlignedString(bw, ""); // m_TextureName
                bw.Write(0); // Pass m_Tags
                if (version[0] == 2021 && version[1] >= 2)
                {
                    bw.Write(0); // m_SerializedKeywordStateMask
                    AlignStream(bw);
                }

                bw.Write(0);   // SubShader m_Tags
                bw.Write(200); // SubShader m_LOD

                // SerializedShader 2021.2+ keywords
                bw.Write(shaderKeywordNames.Length);
                foreach (var kw in shaderKeywordNames)
                {
                    WriteAlignedString(bw, kw);
                }
                bw.Write(shaderKeywordNames.Length);
                for (int i = 0; i < shaderKeywordNames.Length; i++)
                {
                    bw.Write((byte)0);
                }
                AlignStream(bw);

                WriteAlignedString(bw, shaderName);
                WriteAlignedString(bw, ""); // m_CustomEditorName
                WriteAlignedString(bw, ""); // m_FallbackName
                bw.Write(0); // m_Dependencies
                bw.Write(0); // m_CustomEditorForRenderPipelines
                bw.Write(false); // m_DisableNoSubshadersMessage
                AlignStream(bw);

                // Shader root fields
                bw.Write(1); // platforms.Length
                bw.Write((uint)platform);
                WriteUInt32ArrayArray(bw, new[] { new[] { 0u } });
                WriteUInt32ArrayArray(bw, new[] { new[] { (uint)compressedBlob.Length } });
                WriteUInt32ArrayArray(bw, new[] { new[] { (uint)rawProgramBlob.Length } });
                bw.Write(compressedBlob.Length);
                bw.Write(compressedBlob);
                AlignStream(bw);

                if ((version[0] == 2021 && version[1] > 3)
                    || (version[0] == 2021 && version[1] == 3 && version[2] >= 12)
                    || version[0] >= 2022)
                {
                    bw.Write(1); // stageCounts.Length
                    bw.Write(2u);
                }

                bw.Write(0); // m_Dependencies
                bw.Write(0); // m_NonModifiableTextures
                bw.Write(true); // m_ShaderIsBaked
                AlignStream(bw);

                bw.Flush();
                shaderBytes = ms.ToArray();
            }

            var objInfo = new ObjectInfo
            {
                m_PathID = 1,
                byteStart = 0,
                byteSize = (uint)shaderBytes.Length,
                typeID = (int)ClassIDType.Shader,
                classID = (int)ClassIDType.Shader
            };
            using var shaderStream = new MemoryStream(shaderBytes);
            using var endianReader = new EndianBinaryReader(shaderStream, EndianType.LittleEndian);
            var objReader = new ObjectReader(endianReader, sf, objInfo);
            return new Shader(objReader);
        }

        private static byte[] BuildParameterReflectionBlob(string cbName)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(202012090); // m_Version (same header version as ShaderSubProgram!)
            bw.Write(5);         // parameter block type / count (not a valid program code header)
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            // Put bogus huge count where ShaderSubProgram would read m_KeywordsSize / ReadUInt8Array length
            bw.Write(1953066581); // ASCII "u..." / huge length that overflows if misparsed as ShaderSubProgram
            WriteAlignedString(bw, cbName);
            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildSubProgram202012090Blob(ShaderGpuProgramType programType, string[] keywords, byte[] code)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(202012090);
            bw.Write((int)programType);
            bw.Write(new byte[16]); // stats
            bw.Write(keywords.Length);
            foreach (var kw in keywords)
            {
                WriteAlignedString(bw, kw);
            }
            bw.Write(code.Length);
            bw.Write(code);
            AlignStream(bw);
            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] PackShaderProgramSegment0(params byte[][] entries)
        {
            var headerSize = 4 + entries.Length * 12; // count + (Offset, Length, Segment) per entry
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(entries.Length);
            var offset = headerSize;
            foreach (var entry in entries)
            {
                bw.Write(offset);
                bw.Write(entry.Length);
                bw.Write(0); // segment 0
                offset += entry.Length;
            }
            foreach (var entry in entries)
            {
                bw.Write(entry);
            }
            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteModernSerializedProgram(
            BinaryWriter bw,
            int[] version,
            uint blobIndex,
            uint paramBlobIndex,
            ShaderGpuProgramType gpuProgramType,
            ushort[] keywordIndices,
            bool multiTier)
        {
            bw.Write(0); // m_SubPrograms = empty (player build!)

            // m_PlayerSubPrograms[4][] (4 graphics tiers)
            bw.Write(4);
            for (int tier = 0; tier < 4; tier++)
            {
                var populated = tier == 3 || (multiTier && tier == 1);
                bw.Write(populated ? 1 : 0);
                if (populated)
                {
                    bw.Write(blobIndex);
                    bw.Write(keywordIndices.Length);
                    foreach (var kw in keywordIndices)
                    {
                        bw.Write(kw);
                    }
                    AlignStream(bw);
                    bw.Write(0L); // m_ShaderRequirements
                    bw.Write((sbyte)gpuProgramType);
                    AlignStream(bw);
                }
            }

            // m_ParameterBlobIndices[4][]
            bw.Write(4);
            for (int tier = 0; tier < 4; tier++)
            {
                var populated = tier == 3 || (multiTier && tier == 1);
                bw.Write(populated ? 1 : 0);
                if (populated)
                {
                    bw.Write(paramBlobIndex);
                }
            }

            // m_CommonParameters (8 empty lists)
            for (int k = 0; k < 8; k++)
            {
                bw.Write(0);
            }

            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 1))
            {
                bw.Write(0); // m_SerializedKeywordStateMask
                AlignStream(bw);
            }
        }

        private static void WriteEmptyModernSerializedProgram(BinaryWriter bw, int[] version)
        {
            bw.Write(0); // m_SubPrograms
            bw.Write(0); // m_PlayerSubPrograms
            bw.Write(0); // m_ParameterBlobIndices
            for (int k = 0; k < 8; k++)
            {
                bw.Write(0); // m_CommonParameters
            }
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 1))
            {
                bw.Write(0);
                AlignStream(bw);
            }
        }

        private static void WriteMinimalShaderState2021Plus(BinaryWriter bw)
        {
            WriteAlignedString(bw, ""); // m_Name
            for (int rt = 0; rt < 8; rt++)
            {
                WriteFloatVal(bw, 1f);   // srcBlend
                WriteFloatVal(bw, 0f);   // destBlend
                WriteFloatVal(bw, 1f);   // srcBlendAlpha
                WriteFloatVal(bw, 0f);   // destBlendAlpha
                WriteFloatVal(bw, 0f);   // blendOp
                WriteFloatVal(bw, 0f);   // blendOpAlpha
                WriteFloatVal(bw, 0xf);  // colMask
            }
            bw.Write(false); // rtSeparateBlend
            AlignStream(bw);

            WriteFloatVal(bw, 1f); // zClip
            WriteFloatVal(bw, 4f); // zTest
            WriteFloatVal(bw, 1f); // zWrite
            WriteFloatVal(bw, 2f); // culling
            WriteFloatVal(bw, 0f); // conservative
            WriteFloatVal(bw, 0f); // offsetFactor
            WriteFloatVal(bw, 0f); // offsetUnits
            WriteFloatVal(bw, 0f); // alphaToMask

            for (int s = 0; s < 3; s++)
            {
                WriteFloatVal(bw, 0f);
                WriteFloatVal(bw, 0f);
                WriteFloatVal(bw, 0f);
                WriteFloatVal(bw, 8f);
            }
            WriteFloatVal(bw, 255f); // stencilReadMask
            WriteFloatVal(bw, 255f); // stencilWriteMask
            WriteFloatVal(bw, 0f);   // stencilRef

            WriteFloatVal(bw, 0f); // fogStart
            WriteFloatVal(bw, 0f); // fogEnd
            WriteFloatVal(bw, 0f); // fogDensity
            for (int c = 0; c < 4; c++)
            {
                WriteFloatVal(bw, 0f);
            }
            WriteAlignedString(bw, ""); // fogColor.name
            bw.Write((int)FogMode.Unknown);
            bw.Write(0); // gpuProgramID
            bw.Write(0); // m_Tags
            bw.Write(0); // m_LOD
            bw.Write(false); // lighting
            AlignStream(bw);
        }

        private static void WriteFloatVal(BinaryWriter bw, float val)
        {
            bw.Write(val);
            WriteAlignedString(bw, "");
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

        private static void WriteLengthPrefixedAscii(BinaryWriter bw, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            bw.Write(bytes.Length);
            bw.Write(bytes);
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
            var bytes = BuildMinimalSerializedFileBytes(unityVersion);
            var ms = new MemoryStream(bytes);
            var fileReader = new FileReader("minimal.assets", ms);
            return new SerializedFile(fileReader, new AssetsManager());
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
                metaBw.Write(false); // m_EnableTypeTree
                metaBw.Write(0); // typeCount
                metaBw.Write(0); // objectCount
                metaBw.Write(0); // scriptCount
                metaBw.Write(0); // externalsCount
                metaBw.Write((byte)0); // userInformation
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
            fullBytes[16] = 0; // little endian
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            return fullBytes;
        }

        private static byte[] BuildUncompressedUnityFsBundle(string nodePath, byte[] nodePayload, string unityRevision)
        {
            byte[] blocksInfo;
            using (var biMs = new MemoryStream())
            {
                biMs.Write(new byte[16]); // uncompressedDataHash
                WriteInt32BE(biMs, 1); // 1 block
                WriteUInt32BE(biMs, (uint)nodePayload.Length);
                WriteUInt32BE(biMs, (uint)nodePayload.Length);
                WriteUInt16BE(biMs, 0); // CompressionType.None
                WriteInt32BE(biMs, 1); // 1 directory node
                WriteInt64BE(biMs, 0L); // offset
                WriteInt64BE(biMs, nodePayload.Length); // size
                WriteUInt32BE(biMs, 4u); // flags = serialized file
                biMs.Write(Encoding.UTF8.GetBytes(nodePath));
                biMs.WriteByte(0);
                blocksInfo = biMs.ToArray();
            }

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            WriteUInt32BE(ms, 6u); // version 6
            ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            ms.Write(Encoding.ASCII.GetBytes(unityRevision + "\0"));
            var sizePos = ms.Position;
            WriteInt64BE(ms, 0L); // placeholder total size
            WriteUInt32BE(ms, (uint)blocksInfo.Length);
            WriteUInt32BE(ms, (uint)blocksInfo.Length);
            WriteUInt32BE(ms, (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined);
            ms.Write(blocksInfo);
            ms.Write(nodePayload);

            var result = ms.ToArray();
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizePos, 8), result.Length);
            return result;
        }

        private static byte[] BuildMinimalVersionedPeDll(
            ushort major,
            ushort minor,
            ushort build,
            ushort priv,
            string productVersion,
            string fileVersion)
        {
            var versionBlob = BuildVsVersionInfoBlob(major, minor, build, priv, productVersion, fileVersion);

            // Build 3-level Win32 .rsrc section: RT_VERSION(16) -> ID(1) -> Lang(0x0409)
            const uint rsrcRva = 0x1000;
            const int dataEntryOffset = 0x48;
            const int blobOffset = 0x58;
            var rsrcLen = blobOffset + versionBlob.Length;
            var rsrcAlignedLen = (rsrcLen + 0x1FF) & ~0x1FF;
            var rsrc = new byte[rsrcAlignedLen];

            // Level 1 directory @ 0x00
            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(0x0E, 2), 1); // NumberOfIdEntries
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x10, 4), 16u); // RT_VERSION
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x14, 4), 0x80000018u); // -> Level 2 @ 0x18

            // Level 2 directory @ 0x18
            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(0x26, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x28, 4), 1u); // ID 1
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x2C, 4), 0x80000030u); // -> Level 3 @ 0x30

            // Level 3 directory @ 0x30
            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(0x3E, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x40, 4), 0x0409u); // en-US
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x44, 4), (uint)dataEntryOffset);

            // IMAGE_RESOURCE_DATA_ENTRY @ 0x48
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataEntryOffset, 4), rsrcRva + (uint)blobOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataEntryOffset + 4, 4), (uint)versionBlob.Length);

            Buffer.BlockCopy(versionBlob, 0, rsrc, blobOffset, versionBlob.Length);

            var pe = new byte[0x200 + rsrcAlignedLen];
            // DOS header
            pe[0] = (byte)'M';
            pe[1] = (byte)'Z';
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x3C, 4), 0x80u); // e_lfanew

            // PE signature + COFF header @ 0x80
            pe[0x80] = (byte)'P';
            pe[0x81] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84, 2), 0x014C);  // x86
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x86, 2), 1);       // NumberOfSections
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x94, 2), 0x00E0);  // SizeOfOptionalHeader
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x96, 2), 0x2102);  // Characteristics (DLL | EXECUTABLE)

            // PE32 Optional Header @ 0x98
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x98, 2), 0x010B);  // Magic PE32
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xB8, 4), 0x1000u); // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xBC, 4), 0x0200u); // FileAlignment
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0xC0, 2), 4);       // MajorOSVersion
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0xC8, 2), 4);       // MajorSubsystemVersion
            uint sizeOfImage = rsrcRva + (uint)((rsrcAlignedLen + 0xFFF) & ~0xFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xD0, 4), sizeOfImage); // SizeOfImage
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xD4, 4), 0x0200u);     // SizeOfHeaders
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0xDC, 2), 3);           // Subsystem CUI
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xF4, 4), 16u);         // NumberOfRvaAndSizes
            // Resource DataDirectory (index 2 -> 0x98 + 0x60 + 2*8 = 0x108)
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x108, 4), rsrcRva);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x10C, 4), (uint)rsrcLen);

            // Section header ".rsrc" @ 0x178
            Encoding.ASCII.GetBytes(".rsrc").CopyTo(pe.AsSpan(0x178, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x180, 4), (uint)rsrcLen);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x184, 4), rsrcRva);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x188, 4), (uint)rsrcAlignedLen);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x18C, 4), 0x0200u);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x19C, 4), 0x40000040u);

            Buffer.BlockCopy(rsrc, 0, pe, 0x200, rsrcAlignedLen);
            return pe;
        }

        private static byte[] BuildVsVersionInfoBlob(
            ushort major,
            ushort minor,
            ushort build,
            ushort priv,
            string productVersion,
            string fileVersion)
        {
            var fixedInfo = new byte[52];
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(0, 4), 0xFEEF04BDu); // dwSignature
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(4, 4), 0x00010000u); // dwStrucVersion
            uint msVer = ((uint)major << 16) | minor;
            uint lsVer = ((uint)build << 16) | priv;
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(8, 4), msVer);  // dwFileVersionMS
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(12, 4), lsVer); // dwFileVersionLS
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(16, 4), msVer); // dwProductVersionMS
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(20, 4), lsVer); // dwProductVersionLS
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(24, 4), 0x3Fu); // dwFileFlagsMask
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(32, 4), 4u);    // VOS__WINDOWS32
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(36, 4), 2u);    // VFT_DLL

            var strEntries = new[]
            {
                BuildVersionStringNode("ProductVersion", productVersion),
                BuildVersionStringNode("FileVersion", fileVersion)
            };
            var stringTable = BuildVersionContainerNode("040904b0", 1, Array.Empty<byte>(), strEntries);
            var stringFileInfo = BuildVersionContainerNode("StringFileInfo", 1, Array.Empty<byte>(), new[] { stringTable });

            var translationBytes = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(translationBytes.AsSpan(0, 2), 0x0409);
            BinaryPrimitives.WriteUInt16LittleEndian(translationBytes.AsSpan(2, 2), 0x04B0);
            var varNode = BuildVersionContainerNode("Translation", 0, translationBytes, Array.Empty<byte[]>());
            var varFileInfo = BuildVersionContainerNode("VarFileInfo", 1, Array.Empty<byte>(), new[] { varNode });

            return BuildVersionContainerNode("VS_VERSION_INFO", 0, fixedInfo, new[] { stringFileInfo, varFileInfo });
        }

        private static byte[] BuildVersionStringNode(string key, string value)
        {
            var valueBytes = Encoding.Unicode.GetBytes(value + "\0");
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
            bw.Write((ushort)0); // wLength placeholder
            bw.Write((ushort)(value.Length + 1)); // wValueLength in WCHARs
            bw.Write((ushort)1); // wType = text
            bw.Write(Encoding.Unicode.GetBytes(key + "\0"));
            AlignStream(bw);
            bw.Write(valueBytes);
            bw.Flush();
            var data = ms.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), (ushort)data.Length);
            return data;
        }

        private static byte[] BuildVersionContainerNode(string key, ushort type, byte[] valueBytes, byte[][] children)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
            bw.Write((ushort)0); // wLength placeholder
            bw.Write((ushort)valueBytes.Length); // wValueLength in bytes for binary / 0 for containers
            bw.Write(type);
            bw.Write(Encoding.Unicode.GetBytes(key + "\0"));
            AlignStream(bw);
            if (valueBytes.Length > 0)
            {
                bw.Write(valueBytes);
                AlignStream(bw);
            }
            foreach (var child in children)
            {
                AlignStream(bw);
                bw.Write(child);
            }
            bw.Flush();
            var data = ms.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), (ushort)data.Length);
            return data;
        }

        private static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, v);
            s.Write(buf);
        }

        private static void WriteInt32BE(Stream s, int v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, v);
            s.Write(buf);
        }

        private static void WriteUInt32BE(Stream s, uint v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, v);
            s.Write(buf);
        }

        private static void WriteInt64BE(Stream s, long v)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, v);
            s.Write(buf);
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_ModernCompat_{prefix}_{Guid.NewGuid():N}");
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

        /// <summary>
        /// Zero-allocation seekable virtual stream used to validate >512 MiB ObjectReader array bounds
        /// without allocating hundreds of megabytes in CI unit tests.
        /// </summary>
        private sealed class VirtualSeekableStream : Stream
        {
            private readonly long _length;
            private long _position;

            public VirtualSeekableStream(long length) => _length = length;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position
            {
                get => _position;
                set => _position = value;
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _length - _position;
                if (remaining <= 0) return 0;
                var toRead = (int)Math.Min(count, remaining);
                Array.Clear(buffer, offset, toRead);
                _position += toRead;
                return toRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                return _position;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
