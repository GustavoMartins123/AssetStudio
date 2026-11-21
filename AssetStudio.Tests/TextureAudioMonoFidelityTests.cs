using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using K4os.Compression.LZ4;
using Mono.Cecil;
using SkiaSharp;
using Xunit;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;

namespace AssetStudio.Tests
{
    public class TextureAudioMonoFidelityTests
    {
        #region 1. HDR Float Clamping & Normal Map Unpacking

        [Fact]
        public void Texture2DConverter_DecodeRFloat_ClampsHdrNegativeAndNonFiniteValues()
        {
            float[] values = { -0.5f, 0f, 0.5f, 1.0f, 2.5f, 100.0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            byte[] expectedR = { 0, 0, 128, 255, 255, 255, 0, 255, 0 };

            var raw = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), values[i]);
            }

            var tex = CreateSyntheticTexture(values.Length, 1, TextureFormat.RFloat, raw);
            var converter = new Texture2DConverter(tex);
            var bgra = new byte[values.Length * 4];
            Assert.True(converter.DecodeTexture2D(bgra));

            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(0, bgra[i * 4]);
                Assert.Equal(0, bgra[i * 4 + 1]);
                Assert.Equal(expectedR[i], bgra[i * 4 + 2]);
                Assert.Equal(255, bgra[i * 4 + 3]);
            }
        }

        [Fact]
        public void Texture2DConverter_DecodeRGFloat_ClampsHdrNegativeAndNonFiniteValues()
        {
            (float r, float g, byte expR, byte expG)[] pairs =
            {
                (-0.5f, 2.5f, 0, 255),
                (0.0f, 0.5f, 0, 128),
                (1.0f, 100.0f, 255, 255),
                (float.NaN, float.PositiveInfinity, 0, 255),
                (float.NegativeInfinity, 1.01f, 0, 255),
            };

            var raw = new byte[pairs.Length * 8];
            for (int i = 0; i < pairs.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 8, 4), pairs[i].r);
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 8 + 4, 4), pairs[i].g);
            }

            var tex = CreateSyntheticTexture(pairs.Length, 1, TextureFormat.RGFloat, raw);
            var converter = new Texture2DConverter(tex);
            var bgra = new byte[pairs.Length * 4];
            Assert.True(converter.DecodeTexture2D(bgra));

            for (int i = 0; i < pairs.Length; i++)
            {
                Assert.Equal(0, bgra[i * 4]);
                Assert.Equal(pairs[i].expG, bgra[i * 4 + 1]);
                Assert.Equal(pairs[i].expR, bgra[i * 4 + 2]);
                Assert.Equal(255, bgra[i * 4 + 3]);
            }
        }

        [Fact]
        public void Texture2DConverter_DecodeRGB9e5Float_ClampsHdrWithoutModuloWrapAround()
        {
            // RGB9E5 layout: bits 0..8 = R mantissa, 9..17 = G, 18..26 = B, 27..31 = shared exponent (bias 15, shift -24).
            static uint PackRgb9e5(uint r, uint g, uint b, uint exp) =>
                (r & 0x1ffu) | ((g & 0x1ffu) << 9) | ((b & 0x1ffu) << 18) | ((exp & 0x1fu) << 27);

            // Pixel 0: 0.0 -> (0, 0, 0)
            // Pixel 1: exp=15 -> scale=2^-9; r=256 (0.5), g=511 (~0.998), b=0 (0.0)
            // Pixel 2: exp=17 -> scale=2^-7; r=320 (2.5), g=511 (3.99), b=128 (1.0) -> HDR clamps to 255!
            // Pixel 3: exp=31 (max, 2^7=128); r=511 (65408), g=200 (25600), b=1 (128) -> all clamp to 255!
            uint[] packed =
            {
                PackRgb9e5(0, 0, 0, 0),
                PackRgb9e5(256, 511, 0, 15),
                PackRgb9e5(320, 511, 128, 17),
                PackRgb9e5(511, 200, 1, 31)
            };

            var raw = new byte[packed.Length * 4];
            for (int i = 0; i < packed.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i * 4, 4), packed[i]);
            }

            var tex = CreateSyntheticTexture(packed.Length, 1, TextureFormat.RGB9e5Float, raw);
            var converter = new Texture2DConverter(tex);
            var bgra = new byte[packed.Length * 4];
            Assert.True(converter.DecodeTexture2D(bgra));

            // Pixel 0
            Assert.Equal((byte)0, bgra[0]);
            Assert.Equal((byte)0, bgra[1]);
            Assert.Equal((byte)0, bgra[2]);
            Assert.Equal((byte)255, bgra[3]);

            // Pixel 1: r=0.5 -> 128, g=511/512 -> 255 (254.501 -> 255), b=0 -> 0
            Assert.Equal((byte)0, bgra[4]);
            Assert.InRange(bgra[5], (byte)254, (byte)255);
            Assert.Equal((byte)128, bgra[6]);
            Assert.Equal((byte)255, bgra[7]);

            // Pixel 2 (HDR 2.5 / 3.99 / 1.0 -> all 255, zero wrap)
            Assert.Equal((byte)255, bgra[8]);
            Assert.Equal((byte)255, bgra[9]);
            Assert.Equal((byte)255, bgra[10]);
            Assert.Equal((byte)255, bgra[11]);

            // Pixel 3 (extreme HDR 65408 / 25600 / 128 -> all 255, zero wrap)
            Assert.Equal((byte)255, bgra[12]);
            Assert.Equal((byte)255, bgra[13]);
            Assert.Equal((byte)255, bgra[14]);
            Assert.Equal((byte)255, bgra[15]);
        }

        [Fact]
        public void Texture2DConverter_UnpackNormalMapBgra32_Dxt5nmRoundTripIsAccurate()
        {
            (float nx, float ny)[] normals =
            {
                (0f, 0f),
                (0.6f, -0.4f),
                (-0.5f, 0.5f),
                (0.8f, 0.2f),
                (-0.3f, -0.7f),
                (0.1f, 0.9f)
            };

            var bgra = new byte[normals.Length * 4];
            for (int i = 0; i < normals.Length; i++)
            {
                byte encX = Texture2DConverter.FloatToByte(normals[i].nx * 0.5f + 0.5f);
                byte encY = Texture2DConverter.FloatToByte(normals[i].ny * 0.5f + 0.5f);
                bgra[i * 4] = encY;    // B (Unity DXT5nm mirrors G or 0)
                bgra[i * 4 + 1] = encY; // G = ny
                bgra[i * 4 + 2] = 255;  // R = 255
                bgra[i * 4 + 3] = encX; // A = nx
            }

            Assert.True(Texture2DConverter.IsLikelyDxt5NormalMap(bgra));
            Assert.True(Texture2DConverter.UnpackNormalMapBgra32(bgra, TextureFormat.DXT5));

            for (int i = 0; i < normals.Length; i++)
            {
                byte b = bgra[i * 4];
                byte g = bgra[i * 4 + 1];
                byte r = bgra[i * 4 + 2];
                byte a = bgra[i * 4 + 3];

                Assert.Equal((byte)255, a);
                float outX = (r / 255f) * 2f - 1f;
                float outY = (g / 255f) * 2f - 1f;
                float outZ = (b / 255f) * 2f - 1f;
                float expectedZ = MathF.Sqrt(MathF.Max(0f, 1f - normals[i].nx * normals[i].nx - normals[i].ny * normals[i].ny));

                Assert.True(MathF.Abs(outX - normals[i].nx) < 0.01f, $"nx mismatch at {i}: got {outX}, expected {normals[i].nx}");
                Assert.True(MathF.Abs(outY - normals[i].ny) < 0.01f, $"ny mismatch at {i}: got {outY}, expected {normals[i].ny}");
                Assert.True(MathF.Abs(outZ - expectedZ) < 0.01f, $"nz mismatch at {i}: got {outZ}, expected {expectedZ}");
            }
        }

        [Fact]
        public void Texture2DConverter_UnpackNormalMapBgra32_Bc5ReconstructsZOnlyWhenForced()
        {
            (float nx, float ny)[] normals = { (0f, 0f), (0.6f, -0.4f), (-0.7f, 0.3f), (0.25f, 0.25f) };
            var bgra = new byte[normals.Length * 4];
            for (int i = 0; i < normals.Length; i++)
            {
                bgra[i * 4] = 0; // BC5 decoded B = 0
                bgra[i * 4 + 1] = Texture2DConverter.FloatToByte(normals[i].ny * 0.5f + 0.5f);
                bgra[i * 4 + 2] = Texture2DConverter.FloatToByte(normals[i].nx * 0.5f + 0.5f);
                bgra[i * 4 + 3] = 255;
            }

            var original = (byte[])bgra.Clone();
            Assert.False(Texture2DConverter.UnpackNormalMapBgra32(bgra, TextureFormat.BC5, forceUnpack: false));
            Assert.Equal(original, bgra);

            Assert.True(Texture2DConverter.UnpackNormalMapBgra32(bgra, TextureFormat.BC5, forceUnpack: true));
            for (int i = 0; i < normals.Length; i++)
            {
                Assert.Equal((byte)255, bgra[i * 4 + 3]);
                float outZ = (bgra[i * 4] / 255f) * 2f - 1f;
                float expectedZ = MathF.Sqrt(MathF.Max(0f, 1f - normals[i].nx * normals[i].nx - normals[i].ny * normals[i].ny));
                Assert.True(MathF.Abs(outZ - expectedZ) < 0.01f);
            }
        }

        [Fact]
        public void Texture2DConverter_IsLikelyDxt5NormalMap_RejectsNonNormalRgbaAndUiMasks()
        {
            // 1) Colorful RGBA albedo
            var albedo = new byte[16 * 4];
            for (int i = 0; i < 16; i++)
            {
                albedo[i * 4] = (byte)(i * 15);
                albedo[i * 4 + 1] = (byte)(200 - i * 10);
                albedo[i * 4 + 2] = (byte)(50 + i * 8);
                albedo[i * 4 + 3] = 255;
            }
            var albedoCopy = (byte[])albedo.Clone();
            Assert.False(Texture2DConverter.IsLikelyDxt5NormalMap(albedo));
            Assert.False(Texture2DConverter.UnpackNormalMapBgra32(albedo, TextureFormat.DXT5));
            Assert.Equal(albedoCopy, albedo);

            // 2) White UI alpha mask (RGB = 255, varying A)
            var whiteMask = new byte[16 * 4];
            for (int i = 0; i < 16; i++)
            {
                whiteMask[i * 4] = 255;
                whiteMask[i * 4 + 1] = 255;
                whiteMask[i * 4 + 2] = 255;
                whiteMask[i * 4 + 3] = (byte)(i * 16);
            }
            Assert.False(Texture2DConverter.IsLikelyDxt5NormalMap(whiteMask));
            Assert.False(Texture2DConverter.UnpackNormalMapBgra32(whiteMask, TextureFormat.DXT5));

            // 3) Opaque solid red (R=255, G=128, B=0, A=255)
            var opaqueRed = new byte[16 * 4];
            for (int i = 0; i < 16; i++)
            {
                opaqueRed[i * 4] = 0;
                opaqueRed[i * 4 + 1] = 128;
                opaqueRed[i * 4 + 2] = 255;
                opaqueRed[i * 4 + 3] = 255;
            }
            Assert.False(Texture2DConverter.IsLikelyDxt5NormalMap(opaqueRed));
        }

        [Fact]
        public void ExportConversionContext_IdentifiesMaterialBumpAndNormalMaps()
        {
            var sf = CreateMinimalSerializedFile("2022.3.0f1");
            var normalTex = CreateSyntheticTexture(4, 4, TextureFormat.BC5, new byte[16], pathId: 10, assetsFile: sf);
            var albedoTex = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new byte[64], pathId: 11, assetsFile: sf);
            sf.AddObject(normalTex);
            sf.AddObject(albedoTex);

            var mat = CreateSyntheticMaterial(sf, pathId: 20, new[]
            {
                ("_MainTex", albedoTex.m_PathID),
                ("_BumpMap", normalTex.m_PathID)
            });
            sf.AddObject(mat);

            var ctx = new ExportConversionContext(new List<SerializedFile> { sf });
            Assert.True(ctx.IsKnownNormalMap(normalTex));
            Assert.False(ctx.IsKnownNormalMap(albedoTex));
        }

        #endregion

        #region 2. AudioClip PCM, SubsoundIndex, ADPCM Partial/Multichannel, PCM24/Float, Vorbis Fallback

        [Fact]
        public void AudioClipConverter_SupportsPcmAndPassesSubsoundIndex()
        {
            // Multi-subsound FSB5 PCM16 with a FREQUENCY chunk on subsound 0 (tests wire-order chunk parsing too!)
            short[] sub0Samples = { 1000, -1000, 2000, -2000 };
            short[] sub1Samples = { 12345, -23456, 30000, -30000, 42, -42 };
            byte[] fsb = BuildMultiSubsoundFsb5Pcm16(sub0Samples, freq0: 22050, sub1Samples, freq1: 48000);

            var clip0 = CreateSyntheticAudioClip(AudioCompressionFormat.PCM, subsoundIndex: 0, fsb);
            var clip1 = CreateSyntheticAudioClip(AudioCompressionFormat.PCM, subsoundIndex: 1, fsb);
            var vorbisClip = CreateSyntheticAudioClip(AudioCompressionFormat.Vorbis, subsoundIndex: 0, fsb);

            var conv0 = new AudioClipConverter(clip0);
            var conv1 = new AudioClipConverter(clip1);
            var convVorbis = new AudioClipConverter(vorbisClip);

            Assert.True(conv0.IsSupport);
            Assert.True(conv1.IsSupport);
            Assert.False(convVorbis.IsSupport);

            var wav0 = conv0.ConvertToWav();
            var wav1 = conv1.ConvertToWav();
            Assert.NotNull(wav0);
            Assert.NotNull(wav1);

            AssertWavMatchesPcm16(wav0!, expectedRate: 22050, expectedChannels: 1, sub0Samples);
            AssertWavMatchesPcm16(wav1!, expectedRate: 48000, expectedChannels: 1, sub1Samples);
            Assert.Null(UnityAudioDecoder.ConvertToWav(fsb, subsoundIndex: 2));
            Assert.Null(UnityAudioDecoder.ConvertToWav(fsb, subsoundIndex: -1));
        }

        [Fact]
        public void UnityAudioDecoder_ImaAdpcm_DecodesTrailingPartialBlocksExactSampleCount()
        {
            // Mono: 65 samples = 1 full 64-sample block + 1 trailing sample in block 2 (total 2 blocks = 72 bytes)
            const int monoSamples = 65;
            var monoPayload = new byte[36 * 2];
            // Block 0: hist = 100, stepIndex = 0
            BinaryPrimitives.WriteInt16LittleEndian(monoPayload.AsSpan(0, 2), 100);
            monoPayload[2] = 0;
            // Block 1 (trailing partial block): hist = 12345, stepIndex = 5
            BinaryPrimitives.WriteInt16LittleEndian(monoPayload.AsSpan(36, 2), 12345);
            monoPayload[38] = 5;

            var monoFsb = BuildSingleSampleFsb5(UnityAudioCodec.ImaAdpcm, freqId: 8u, channels: 1, sampleCount: monoSamples, monoPayload);
            var monoWav = UnityAudioDecoder.ConvertToWav(monoFsb);
            Assert.NotNull(monoWav);

            int monoDataLen = BinaryPrimitives.ReadInt32LittleEndian(monoWav!.AsSpan(40, 4));
            Assert.Equal(monoSamples * 2, monoDataLen);
            short trailingSample = BinaryPrimitives.ReadInt16LittleEndian(monoWav.AsSpan(44 + 64 * 2, 2));
            Assert.Equal((short)12345, trailingSample);

            // Stereo: 70 samples/channel = 1 full block + 6 trailing samples in block 2 (2 * 72 = 144 bytes)
            const int stereoSamples = 70;
            var stereoPayload = new byte[36 * 2 * 2];
            // Block 1 headers at offset 72: ch0 hist = 5000, ch1 hist = -6000
            BinaryPrimitives.WriteInt16LittleEndian(stereoPayload.AsSpan(72, 2), 5000);
            BinaryPrimitives.WriteInt16LittleEndian(stereoPayload.AsSpan(76, 2), -6000);

            var stereoFsb = BuildSingleSampleFsb5(UnityAudioCodec.ImaAdpcm, freqId: 9u, channels: 2, sampleCount: stereoSamples, stereoPayload);
            var stereoWav = UnityAudioDecoder.ConvertToWav(stereoFsb);
            Assert.NotNull(stereoWav);

            int stereoDataLen = BinaryPrimitives.ReadInt32LittleEndian(stereoWav!.AsSpan(40, 4));
            Assert.Equal(stereoSamples * 2 * 2, stereoDataLen);
            short ch0Block1First = BinaryPrimitives.ReadInt16LittleEndian(stereoWav.AsSpan(44 + (64 * 2 + 0) * 2, 2));
            short ch1Block1First = BinaryPrimitives.ReadInt16LittleEndian(stereoWav.AsSpan(44 + (64 * 2 + 1) * 2, 2));
            Assert.Equal((short)5000, ch0Block1First);
            Assert.Equal((short)-6000, ch1Block1First);
        }

        [Fact]
        public void UnityAudioDecoder_ImaAdpcm_DecodesMultichannel4ChAnd6ChProperly()
        {
            const int channels = 6;
            const int sampleCount = 64;
            var payload = new byte[36 * channels];
            short[] expectedHist = { 1000, -2000, 3000, -4000, 5000, -6000 };
            for (int c = 0; c < channels; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(c * 4, 2), expectedHist[c]);
                payload[c * 4 + 2] = 20; // stepIndex = 20 -> step = 50
            }
            // Set first nibble (k=1) of channel 3 to 4 (+step) -> byte offset = 4*6 + 4*3 + 0 = 36
            payload[4 * channels + 4 * 3] = 0x04;

            var fsb = BuildSingleSampleFsb5(UnityAudioCodec.ImaAdpcm, freqId: 9u, channels: channels, sampleCount: sampleCount, payload);
            var wav = UnityAudioDecoder.ConvertToWav(fsb);
            Assert.NotNull(wav);

            Assert.Equal((short)channels, BinaryPrimitives.ReadInt16LittleEndian(wav!.AsSpan(22, 2)));
            Assert.Equal(sampleCount * channels * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));

            // Verify sample 0 of every channel matches its own header predictor (proving multichannel decoding, not mono!)
            for (int c = 0; c < channels; c++)
            {
                short s0 = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + c * 2, 2));
                Assert.Equal(expectedHist[c], s0);
            }

            // Verify channel 3 sample 1 applied delta from nibble 4 (step=50 -> delta = 50/8 + 50 = 56)
            short ch3s1 = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (1 * channels + 3) * 2, 2));
            Assert.Equal((short)(expectedHist[3] + 56), ch3s1);
        }

        [Fact]
        public void UnityAudioDecoder_DecodesPcm24AndPcmFloat()
        {
            // Pcm24: 4 samples -> 12 bytes
            byte[] pcm24 =
            {
                0xFF, 0xFF, 0x7F, // +8388607 -> +32767
                0x00, 0x00, 0x80, // -8388608 -> -32768
                0x56, 0x34, 0x12, // 0x123456 -> 0x1234
                0xFF, 0xFF, 0xFF  // -1 -> -1
            };
            var fsb24 = BuildSingleSampleFsb5(UnityAudioCodec.Pcm24, freqId: 8u, channels: 1, sampleCount: 4, pcm24);
            var wav24 = UnityAudioDecoder.ConvertToWav(fsb24);
            Assert.NotNull(wav24);
            AssertWavMatchesPcm16(wav24!, 44100, 1, new short[] { 32767, -32768, 0x1234, -1 });

            // PcmFloat: 6 samples -> 24 bytes
            float[] floats = { 0f, 0.5f, -0.5f, 1.0f, -2.5f, float.NaN };
            var pcmFloat = new byte[floats.Length * 4];
            for (int i = 0; i < floats.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(pcmFloat.AsSpan(i * 4, 4), floats[i]);
            }
            var fsbFloat = BuildSingleSampleFsb5(UnityAudioCodec.PcmFloat, freqId: 9u, channels: 1, sampleCount: (uint)floats.Length, pcmFloat);
            var wavFloat = UnityAudioDecoder.ConvertToWav(fsbFloat);
            Assert.NotNull(wavFloat);
            AssertWavMatchesPcm16(wavFloat!, 48000, 1, new short[] { 0, 16384, -16384, 32767, -32768, 0 });
        }

        [Fact]
        public void AssetExportService_TryConvertAudioClipToWav_FallsBackToTranscoderForFsb5VorbisAndCleansTempFiles()
        {
            var vorbisFsb = BuildSingleSampleFsb5(UnityAudioCodec.Vorbis, freqId: 8u, channels: 1, sampleCount: 128, new byte[64]);
            var clip = CreateSyntheticAudioClip(AudioCompressionFormat.Vorbis, subsoundIndex: 0, vorbisFsb);
            var tempDir = Path.Combine(Path.GetTempPath(), "as_f4_aud_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fakeWav = UnityAudioDecoder.ConvertToWav(BuildSingleSampleFsb5(UnityAudioCodec.Pcm16, 8u, 1, 4, new byte[8]))!;

                // 1) Successful transcoder returns WAV bytes and leaves no orphan temp files
                var converted = AssetExportService.TryConvertAudioClipToWav(
                    clip,
                    vorbisFsb,
                    transcoder: (inPath, outPath, _) =>
                    {
                        Assert.True(File.Exists(inPath));
                        File.WriteAllBytes(outPath, fakeWav);
                        return true;
                    },
                    tempDirectory: tempDir);

                Assert.NotNull(converted);
                Assert.Equal(fakeWav, converted);
                Assert.Empty(Directory.GetFiles(tempDir));

                // 2) Failing transcoder returns null cleanly and leaves no orphan temp files
                var failed = AssetExportService.TryConvertAudioClipToWav(
                    clip,
                    vorbisFsb,
                    transcoder: (_, _, _) => false,
                    tempDirectory: tempDir);

                Assert.Null(failed);
                Assert.Empty(Directory.GetFiles(tempDir));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        #endregion

        #region 3. Sprite & Shader Export Fidelity

        [Fact]
        public void SpriteHelper_ComposesAlphaTextureAndWritesSpriteMetaTextureType8()
        {
            SpriteHelper.ClearAtlasCache();
            var tempDir = Path.Combine(Path.GetTempPath(), "as_f4_sprite_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var sf = CreateMinimalSerializedFile("2022.3.0f1");

                // 4x4 opaque red RGB texture (A=255)
                var colorRgba = new byte[4 * 4 * 4];
                for (int i = 0; i < 16; i++)
                {
                    colorRgba[i * 4] = 200;     // R
                    colorRgba[i * 4 + 1] = 100; // G
                    colorRgba[i * 4 + 2] = 50;  // B
                    colorRgba[i * 4 + 3] = 255; // A (opaque)
                }
                var colorTex = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, colorRgba, pathId: 100, assetsFile: sf);
                sf.AddObject(colorTex);

                // 4x4 Alpha8 split alpha texture with alpha = 77
                var alphaBytes = Enumerable.Repeat((byte)77, 16).ToArray();
                var alphaTex = CreateSyntheticTexture(4, 4, TextureFormat.Alpha8, alphaBytes, pathId: 101, assetsFile: sf);
                sf.AddObject(alphaTex);

                var sprite = CreateSyntheticSprite(sf, colorTex.m_PathID, alphaTex.m_PathID, width: 4, height: 4);
                sf.AddObject(sprite);

                using var img = sprite.GetImage();
                Assert.NotNull(img);
                Assert.Equal(4, img!.Width);
                Assert.Equal(4, img.Height);

                var px = img.GetPixelSpan();
                Assert.Equal((byte)50, px[0]);  // B
                Assert.Equal((byte)100, px[1]); // G
                Assert.Equal((byte)200, px[2]); // R
                Assert.Equal((byte)77, px[3]);  // A composed from alphaTexture!

                // Also verify Sprite .meta emitted by AssetExportService uses textureType: 8
                var item = new AssetItem(sprite);
                var ctx = new ExportConversionContext(new List<SerializedFile> { sf });
                var opts = new ExportOptionsState { ConvertTextureFormat = ImageFormat.Png };
                Assert.True(AssetExportService.ExportStandardConvertedAsset(item, tempDir, ctx, opts));

                var pngPath = Directory.GetFiles(tempDir, "*.png").Single();
                var metaPath = pngPath + ".meta";
                Assert.True(File.Exists(metaPath));
                var metaText = File.ReadAllText(metaPath);
                Assert.Contains("textureType: 8", metaText);
                Assert.Contains("spriteMode: 1", metaText);
                Assert.Contains("enableMipMap: 0", metaText);
                Assert.Contains("spritePixelsToUnits: 100", metaText);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SpriteHelper_GetTriangles_HandlesArbitraryStrideFloat16And32BitIndices()
        {
            // 3 vertices with stride = 16, channel offset = 4, format = Float16 (2 bytes/component, dim = 3)
            const int stride = 16;
            const int channelOffset = 4;
            var vertexBytes = new byte[3 * stride];
            (float x, float y)[] expectedVerts = { (0.25f, 0.5f), (1.5f, -0.25f), (-0.75f, 2.0f) };
            for (int v = 0; v < 3; v++)
            {
                int o = v * stride + channelOffset;
                BinaryPrimitives.WriteHalfLittleEndian(vertexBytes.AsSpan(o, 2), (Half)expectedVerts[v].x);
                BinaryPrimitives.WriteHalfLittleEndian(vertexBytes.AsSpan(o + 2, 2), (Half)expectedVerts[v].y);
                BinaryPrimitives.WriteHalfLittleEndian(vertexBytes.AsSpan(o + 4, 2), (Half)0f);
            }

            // 32-bit index buffer: [0u, 1u, 2u] = 12 bytes
            var indexBytes = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(0, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(4, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(8, 4), 2u);

            var rd = (SpriteRenderData)RuntimeHelpers.GetUninitializedObject(typeof(SpriteRenderData));
            rd.m_VertexData = (VertexData)RuntimeHelpers.GetUninitializedObject(typeof(VertexData));
            rd.m_VertexData.m_VertexCount = 3;
            rd.m_VertexData.m_Channels = new[]
            {
                new ChannelInfo { stream = 0, offset = channelOffset, format = (byte)MeshHelper.VertexFormat.Float16, dimension = 3 }
            };
            rd.m_VertexData.m_Streams = new[]
            {
                new StreamInfo { offset = 0, stride = stride }
            };
            rd.m_VertexData.m_DataSize = vertexBytes;
            rd.m_IndexBuffer = indexBytes;
            var subMesh = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            subMesh.firstByte = 0;
            subMesh.indexCount = 3;
            subMesh.firstVertex = 0;
            subMesh.vertexCount = 3;
            subMesh.baseVertex = 0;
            rd.m_SubMeshes = new[] { subMesh };

            var triangles = SpriteHelper.GetTriangles(rd, new[] { 2022, 3, 0, 0 });
            Assert.Single(triangles);
            Assert.Equal(3, triangles[0].Length);
            for (int i = 0; i < 3; i++)
            {
                Assert.True(MathF.Abs(triangles[0][i].X - expectedVerts[i].x) < 0.01f);
                Assert.True(MathF.Abs(triangles[0][i].Y - expectedVerts[i].y) < 0.01f);
            }
        }

        [Fact]
        public void ShaderConverter_OmitsNonInitPropertyNamesAcrossAllRenderStates()
        {
            var shader = BuildSyntheticShaderWithNonInitStates();
            var text = shader.Convert();

            Assert.DoesNotContain("<noninit>", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Blend SrcAlpha OneMinusSrcAlpha", text);
            Assert.Contains("ZWrite [_ValidZWrite]", text);
            Assert.Contains("Cull Front", text);
        }

        #endregion

        #region 4. Stripped MonoBehaviour Fidelity

        [Fact]
        public void SerializedTypeHelper_AddGradient_UsesCompactLayoutForUnity2022_2PlusAndLegacyForOlder()
        {
            var nodes2022_3 = new List<TypeTreeNode>();
            new SerializedTypeHelper(new[] { 2022, 3, 62, 0 }).AddGradient(nodes2022_3, "grad", 1);

            var mode22 = nodes2022_3.Single(n => n.m_Name == "m_Mode");
            var cs22 = nodes2022_3.Single(n => n.m_Name == "m_ColorSpace");
            Assert.Equal("UInt8", mode22.m_Type);
            Assert.Equal("SInt8", cs22.m_Type);

            var nodes2021 = new List<TypeTreeNode>();
            new SerializedTypeHelper(new[] { 2021, 3, 0, 0 }).AddGradient(nodes2021, "grad", 1);
            var mode21 = nodes2021.Single(n => n.m_Name == "m_Mode");
            Assert.Equal("int", mode21.m_Type);
            Assert.DoesNotContain(nodes2021, n => n.m_Name == "m_ColorSpace");

            // Verify binary byte size difference: 8*16 (128) + 16*2 (32) + 4 = 164 bytes for 2022.2+, 168 bytes for 2021.3
            Assert.Equal(164, MeasureTypeTreeReadBytes(nodes2022_3, new byte[200]));
            Assert.Equal(168, MeasureTypeTreeReadBytes(nodes2021, new byte[200]));
        }

        [Fact]
        public void TypeDefinitionConverter_EmitsExactTypesAndAlignmentForNon32BitEnums()
        {
            using var asm = CreateSyntheticUnityAssembly(out var unityTypes);
            var mod = asm.MainModule;

            var byteEnum = CreateEnum(mod, "ByteEnum", mod.TypeSystem.Byte);
            var sbyteEnum = CreateEnum(mod, "SByteEnum", mod.TypeSystem.SByte);
            var shortEnum = CreateEnum(mod, "ShortEnum", mod.TypeSystem.Int16);
            var ushortEnum = CreateEnum(mod, "UShortEnum", mod.TypeSystem.UInt16);
            var uintEnum = CreateEnum(mod, "UIntEnum", mod.TypeSystem.UInt32);
            var ulongEnum = CreateEnum(mod, "ULongEnum", mod.TypeSystem.UInt64);

            var host = CreateClass(mod, "EnumHost", unityTypes.MonoBehaviour);
            host.Fields.Add(new FieldDefinition("fByte", CecilFieldAttributes.Public, byteEnum));
            host.Fields.Add(new FieldDefinition("fSByte", CecilFieldAttributes.Public, sbyteEnum));
            host.Fields.Add(new FieldDefinition("fShort", CecilFieldAttributes.Public, shortEnum));
            host.Fields.Add(new FieldDefinition("fUShort", CecilFieldAttributes.Public, ushortEnum));
            host.Fields.Add(new FieldDefinition("fUInt", CecilFieldAttributes.Public, uintEnum));
            host.Fields.Add(new FieldDefinition("fULong", CecilFieldAttributes.Public, ulongEnum));
            host.Fields.Add(new FieldDefinition("arrByte", CecilFieldAttributes.Public, new ArrayType(byteEnum)));

            var helper = new SerializedTypeHelper(new[] { 2022, 3, 0, 0 });
            var nodes = new TypeDefinitionConverter(host, helper, 1).ConvertToTypeTreeNodes();

            AssertNode(nodes, "fByte", "UInt8", align: true);
            AssertNode(nodes, "fSByte", "SInt8", align: true);
            AssertNode(nodes, "fShort", "SInt16", align: true);
            AssertNode(nodes, "fUShort", "UInt16", align: true);
            AssertNode(nodes, "fUInt", "UInt32", align: false);
            AssertNode(nodes, "fULong", "UInt64", align: false);

            // Packed byte-enum array: Array [ALIGN] + data (align=false)
            int arrIdx = nodes.FindIndex(n => n.m_Name == "arrByte");
            Assert.True(arrIdx >= 0);
            Assert.Equal("Array", nodes[arrIdx + 1].m_Type);
            Assert.NotEqual(0, nodes[arrIdx + 1].m_MetaFlag & 0x4000);
            Assert.Equal("UInt8", nodes[arrIdx + 3].m_Type);
            Assert.Equal(0, nodes[arrIdx + 3].m_MetaFlag & 0x4000);

            // Binary round-trip: 4 + 4 + 4 + 4 + 4 + 8 + (4 + 3 + 1 pad = 8) = 36 bytes
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)7); bw.Write(new byte[3]);         // fByte + align
            bw.Write((sbyte)-3); bw.Write(new byte[3]);       // fSByte + align
            bw.Write((short)-1234); bw.Write(new byte[2]);    // fShort + align
            bw.Write((ushort)54321); bw.Write(new byte[2]);   // fUShort + align
            bw.Write(987654321u);                             // fUInt
            bw.Write(0x1122334455667788UL);                   // fULong
            bw.Write(3);                                      // arrByte.size = 3
            bw.Write(new byte[] { 10, 20, 30, 0 });           // 3 packed bytes + 1 pad byte

            var payload = ms.ToArray();
            Assert.Equal(36, MeasureTypeTreeReadBytes(nodes, payload));
        }

        [Fact]
        public void TypeDefinitionConverter_EmitsMissingUnityBuiltInStructs()
        {
            using var asm = CreateSyntheticUnityAssembly(out var unityTypes);
            var mod = asm.MainModule;

            var host = CreateClass(mod, "BuiltInStructHost", unityTypes.MonoBehaviour);
            host.Fields.Add(new FieldDefinition("myHash", CecilFieldAttributes.Public, unityTypes.Hash128));
            host.Fields.Add(new FieldDefinition("myLayer", CecilFieldAttributes.Public, unityTypes.RenderingLayerMask));
            var lazyTex = new GenericInstanceType(unityTypes.LazyLoadReference);
            lazyTex.GenericArguments.Add(unityTypes.Texture2D);
            host.Fields.Add(new FieldDefinition("myLazy", CecilFieldAttributes.Public, lazyTex));
            host.Fields.Add(new FieldDefinition("myProp", CecilFieldAttributes.Public, unityTypes.PropertyName));

            var helper = new SerializedTypeHelper(new[] { 6000, 0, 0, 0 });
            var nodes = new TypeDefinitionConverter(host, helper, 1).ConvertToTypeTreeNodes();

            Assert.Contains(nodes, n => n.m_Type == "Hash128" && n.m_Name == "myHash");
            Assert.Equal(16, nodes.Count(n => n.m_Type == "UInt8" && n.m_Name.StartsWith("bytes[")));

            Assert.Contains(nodes, n => n.m_Type == "RenderingLayerMask" && n.m_Name == "myLayer");
            Assert.Contains(nodes, n => n.m_Type == "UInt32" && n.m_Name == "m_Bits");

            Assert.Contains(nodes, n => n.m_Type == "PPtr<Texture2D>" && n.m_Name == "myLazy");

            int propIdx = nodes.FindIndex(n => n.m_Type == "PropertyName" && n.m_Name == "myProp");
            Assert.True(propIdx >= 0);
            Assert.Equal("int", nodes[propIdx + 1].m_Type);
            Assert.Equal("id", nodes[propIdx + 1].m_Name);

            // Total binary bytes: Hash128(16) + RenderingLayerMask(4) + LazyLoadReference/PPtr(12) + PropertyName(4) = 36 bytes
            Assert.Equal(36, MeasureTypeTreeReadBytes(nodes, new byte[64]));
        }

        [Fact]
        public void TypeDefinitionConverter_SupportsDeepNonCyclicStructsAndTerminatesCyclesWithoutBrokenArrays()
        {
            using var asm = CreateSyntheticUnityAssembly(out var unityTypes);
            var mod = asm.MainModule;

            // 1) Deep non-cyclic chain of 14 distinct structs with an int[] at the bottom (level 15 > old MaxIndentDepth=10!)
            var leaf = CreateSerializableClass(mod, "Deep14");
            leaf.Fields.Add(new FieldDefinition("values", CecilFieldAttributes.Public, new ArrayType(mod.TypeSystem.Int32)));
            TypeDefinition current = leaf;
            for (int depth = 13; depth >= 1; depth--)
            {
                var wrapper = CreateSerializableClass(mod, $"Deep{depth}");
                wrapper.Fields.Add(new FieldDefinition("child", CecilFieldAttributes.Public, current));
                current = wrapper;
            }
            var deepHost = CreateClass(mod, "DeepHost", unityTypes.MonoBehaviour);
            deepHost.Fields.Add(new FieldDefinition("root", CecilFieldAttributes.Public, current));

            var helper = new SerializedTypeHelper(new[] { 2022, 3, 0, 0 });
            var deepNodes = new TypeDefinitionConverter(deepHost, helper, 1).ConvertToTypeTreeNodes();

            AssertNoBrokenArrays(deepNodes);
            Assert.Contains(deepNodes, n => n.m_Name == "values");
            Assert.Contains(deepNodes, n => n.m_Type == "SInt32" && n.m_Name == "data");

            // Binary payload: int size = 2, data = [11, 22] -> 12 bytes read without throwing!
            var deepPayload = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(deepPayload.AsSpan(0, 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(deepPayload.AsSpan(4, 4), 11);
            BinaryPrimitives.WriteInt32LittleEndian(deepPayload.AsSpan(8, 4), 22);
            Assert.Equal(12, MeasureTypeTreeReadBytes(deepNodes, deepPayload));

            // 2) Cyclic mutual recursion with array: CycleA -> CycleB[] -> CycleA -> ...
            var cycleA = CreateSerializableClass(mod, "CycleA");
            var cycleB = CreateSerializableClass(mod, "CycleB");
            cycleA.Fields.Add(new FieldDefinition("id", CecilFieldAttributes.Public, mod.TypeSystem.Int32));
            cycleA.Fields.Add(new FieldDefinition("items", CecilFieldAttributes.Public, new ArrayType(cycleB)));
            cycleB.Fields.Add(new FieldDefinition("parent", CecilFieldAttributes.Public, cycleA));

            var cycleHost = CreateClass(mod, "CycleHost", unityTypes.MonoBehaviour);
            cycleHost.Fields.Add(new FieldDefinition("entry", CecilFieldAttributes.Public, cycleA));

            var cycleNodes = new TypeDefinitionConverter(cycleHost, helper, 1).ConvertToTypeTreeNodes();
            Assert.NotEmpty(cycleNodes);
            AssertNoBrokenArrays(cycleNodes);
        }

        [Fact]
        public void TypeDefinitionConverter_ResolvesGenericSerializableFieldsAndSerializeReference()
        {
            using var asm = CreateSyntheticUnityAssembly(out var unityTypes);
            var mod = asm.MainModule;

            // Generic [Serializable] class Pair<TKey, TVal> { public TKey key; public TVal val; }
            var pairDef = CreateSerializableClass(mod, "SerializablePair`2");
            var tKey = new GenericParameter("TKey", pairDef);
            var tVal = new GenericParameter("TVal", pairDef);
            pairDef.GenericParameters.Add(tKey);
            pairDef.GenericParameters.Add(tVal);
            pairDef.Fields.Add(new FieldDefinition("key", CecilFieldAttributes.Public, tKey));
            pairDef.Fields.Add(new FieldDefinition("val", CecilFieldAttributes.Public, tVal));

            var closedPair = new GenericInstanceType(pairDef);
            closedPair.GenericArguments.Add(mod.TypeSystem.Int32);
            closedPair.GenericArguments.Add(mod.TypeSystem.Single);

            // Interface for [SerializeReference]
            var iFace = new TypeDefinition("TestGame", "IPolymorphic", CecilTypeAttributes.Public | CecilTypeAttributes.Interface | CecilTypeAttributes.Abstract, null);
            mod.Types.Add(iFace);

            // Nested container holding a [SerializeReference] array
            var holder = CreateSerializableClass(mod, "RefHolder");
            var listRefField = new FieldDefinition("polyArray", CecilFieldAttributes.Private, new ArrayType(iFace));
            listRefField.CustomAttributes.Add(new CustomAttribute(unityTypes.SerializeReferenceCtor));
            holder.Fields.Add(listRefField);

            var host = CreateClass(mod, "GenericAndRefHost", unityTypes.MonoBehaviour);
            host.Fields.Add(new FieldDefinition("pair", CecilFieldAttributes.Public, closedPair));
            host.Fields.Add(new FieldDefinition("pairArray", CecilFieldAttributes.Public, new ArrayType(closedPair)));
            var singleRefField = new FieldDefinition("singlePoly", CecilFieldAttributes.Private, iFace);
            singleRefField.CustomAttributes.Add(new CustomAttribute(unityTypes.SerializeReferenceCtor));
            host.Fields.Add(singleRefField);
            host.Fields.Add(new FieldDefinition("holder", CecilFieldAttributes.Public, holder));

            var helper = new SerializedTypeHelper(new[] { 2022, 3, 0, 0 });
            var nodes = new TypeDefinitionConverter(host, helper, 1).ConvertToTypeTreeNodes();

            // Generic fields (direct AND inside array!) resolved to concrete SInt32 + float
            Assert.Equal(2, nodes.Count(n => n.m_Name == "key" && n.m_Type == "SInt32"));
            Assert.Equal(2, nodes.Count(n => n.m_Name == "val" && n.m_Type == "float"));

            // [SerializeReference] single field emits ManagedReference -> SInt64 rid
            int singleIdx = nodes.FindIndex(n => n.m_Type == "ManagedReference" && n.m_Name == "singlePoly");
            Assert.True(singleIdx >= 0);
            Assert.Equal("SInt64", nodes[singleIdx + 1].m_Type);
            Assert.Equal("rid", nodes[singleIdx + 1].m_Name);

            // [SerializeReference] array inside nested struct emits Array -> ManagedReference data -> SInt64 rid
            int polyArrIdx = nodes.FindIndex(n => n.m_Name == "polyArray");
            Assert.True(polyArrIdx >= 0);
            Assert.Equal("Array", nodes[polyArrIdx + 1].m_Type);
            Assert.Equal("ManagedReference", nodes[polyArrIdx + 3].m_Type);
            Assert.Equal("SInt64", nodes[polyArrIdx + 4].m_Type);

            // Exactly ONE trailing ManagedReferencesRegistry at root level (indent = 1)
            var registries = nodes.Where(n => n.m_Type == "ManagedReferencesRegistry").ToList();
            Assert.Single(registries);
            Assert.Equal(1, registries[0].m_Level);
            Assert.Equal("references", registries[0].m_Name);
            AssertNoBrokenArrays(nodes);
        }

        #endregion

        #region Helpers

        private static Texture2D CreateSyntheticTexture(int width, int height, TextureFormat format, byte[] data, long pathId = 1, SerializedFile? assetsFile = null)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.m_Name = $"Tex_{format}_{pathId}";
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = format;
            tex.version = new[] { 2022, 3, 0, 0 };
            tex.platform = BuildTarget.StandaloneWindows64;
            tex.image_data = new ResourceReader(data);
            tex.m_PathID = pathId;
            tex.type = ClassIDType.Texture2D;
            if (assetsFile != null)
            {
                tex.assetsFile = assetsFile;
            }
            return tex;
        }

        private static Material CreateSyntheticMaterial(SerializedFile sf, long pathId, (string propName, long texPathId)[] texSlots)
        {
            var mat = (Material)RuntimeHelpers.GetUninitializedObject(typeof(Material));
            mat.m_Name = "TestMat";
            mat.m_PathID = pathId;
            mat.assetsFile = sf;
            mat.type = ClassIDType.Material;
            mat.m_SavedProperties = (UnityPropertySheet)RuntimeHelpers.GetUninitializedObject(typeof(UnityPropertySheet));
            mat.m_SavedProperties.m_TexEnvs = texSlots.Select(slot =>
            {
                var texEnv = (UnityTexEnv)RuntimeHelpers.GetUninitializedObject(typeof(UnityTexEnv));
                texEnv.m_Texture = CreatePPtr<Texture>(sf, slot.texPathId);
                return new KeyValuePair<string, UnityTexEnv>(slot.propName, texEnv);
            }).ToArray();
            return mat;
        }

        private static Sprite CreateSyntheticSprite(SerializedFile sf, long colorTexPathId, long alphaTexPathId, int width, int height)
        {
            var sprite = (Sprite)RuntimeHelpers.GetUninitializedObject(typeof(Sprite));
            sprite.m_Name = "TestSprite";
            sprite.m_PathID = 500;
            sprite.assetsFile = sf;
            sprite.type = ClassIDType.Sprite;
            sprite.version = new[] { 2022, 3, 0, 0 };
            sprite.m_PixelsToUnits = 100f;
            sprite.m_Pivot = new Vector2(0.5f, 0.5f);
            sprite.m_Border = new Vector4(1f, 2f, 3f, 4f);
            sprite.m_Rect = CreateRectf(0, 0, width, height);

            var rd = (SpriteRenderData)RuntimeHelpers.GetUninitializedObject(typeof(SpriteRenderData));
            rd.texture = CreatePPtr<Texture2D>(sf, colorTexPathId);
            rd.alphaTexture = CreatePPtr<Texture2D>(sf, alphaTexPathId);
            rd.textureRect = CreateRectf(0, 0, width, height);
            rd.textureRectOffset = Vector2.Zero;
            rd.downscaleMultiplier = 1f;
            rd.settingsRaw = CreateSpriteSettings((uint)SpritePackingMode.Rectangle << 1);
            sprite.m_RD = rd;
            return sprite;
        }

        private static Rectf CreateRectf(float x, float y, float w, float h)
        {
            var r = (Rectf)RuntimeHelpers.GetUninitializedObject(typeof(Rectf));
            r.x = x; r.y = y; r.width = w; r.height = h;
            return r;
        }

        private static SpriteSettings CreateSpriteSettings(uint raw)
        {
            using var ms = new MemoryStream(BitConverter.GetBytes(raw));
            using var br = new BinaryReader(ms);
            return new SpriteSettings(br);
        }

        private static PPtr<T> CreatePPtr<T>(SerializedFile sf, long pathId) where T : Object
        {
            var pptr = (PPtr<T>)RuntimeHelpers.GetUninitializedObject(typeof(PPtr<T>));
            pptr.m_FileID = 0;
            pptr.m_PathID = pathId;
            typeof(PPtr<T>).GetField("assetsFile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(pptr, sf);
            return pptr;
        }

        private static AudioClip CreateSyntheticAudioClip(AudioCompressionFormat format, int subsoundIndex, byte[] audioData)
        {
            var clip = (AudioClip)RuntimeHelpers.GetUninitializedObject(typeof(AudioClip));
            clip.m_Name = "TestClip";
            clip.version = new[] { 2022, 3, 0, 0 };
            clip.m_CompressionFormat = format;
            clip.m_SubsoundIndex = subsoundIndex;
            clip.m_AudioData = new ResourceReader(audioData);
            clip.type = ClassIDType.AudioClip;
            return clip;
        }

        private static ulong PackFsb5Meta(bool hasChunks, uint freqId, uint chBits, uint dataOffsetBlocks32, uint sampleCount)
        {
            return (hasChunks ? 1UL : 0UL)
                | ((ulong)(freqId & 0xFu) << 1)
                | ((ulong)(chBits & 0x3u) << 5)
                | ((ulong)(dataOffsetBlocks32 & 0x7FFFFFFu) << 7)
                | ((ulong)(sampleCount & 0x3FFFFFFFu) << 34);
        }

        private static byte[] BuildFsb5Chunk(uint chunkType, bool moreChunks, byte[] payload)
        {
            uint raw = (moreChunks ? 1u : 0u) | (((uint)payload.Length & 0xFFFFFFu) << 1) | ((chunkType & 0x7Fu) << 25);
            var bytes = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), raw);
            Buffer.BlockCopy(payload, 0, bytes, 4, payload.Length);
            return bytes;
        }

        private static byte[] BuildSingleSampleFsb5(UnityAudioCodec codec, uint freqId, int channels, uint sampleCount, byte[] payload)
        {
            uint chBits = channels switch { 1 => 0u, 2 => 1u, 6 => 2u, 8 => 3u, _ => 0u };
            byte[] extraChunk = (channels != 1 && channels != 2 && channels != 6 && channels != 8)
                ? BuildFsb5Chunk(chunkType: 1u, moreChunks: false, payload: new[] { (byte)channels })
                : Array.Empty<byte>();

            uint sampleHeadersSize = (uint)(8 + extraChunk.Length);
            var data = new byte[60 + sampleHeadersSize + payload.Length];
            Encoding.ASCII.GetBytes("FSB5").CopyTo(data, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), sampleHeadersSize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)codec);

            ulong meta = PackFsb5Meta(extraChunk.Length > 0, freqId, chBits, 0u, sampleCount);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(60, 8), meta);
            if (extraChunk.Length > 0)
            {
                Buffer.BlockCopy(extraChunk, 0, data, 68, extraChunk.Length);
            }
            Buffer.BlockCopy(payload, 0, data, 60 + (int)sampleHeadersSize, payload.Length);
            return data;
        }

        private static byte[] BuildMultiSubsoundFsb5Pcm16(short[] sub0, uint freq0, short[] sub1, uint freq1)
        {
            // Pad sub0 payload to 32-byte boundary as required by FSB5 DataOffset (blocks of 32 bytes)
            int sub0RawBytes = sub0.Length * 2;
            int sub0Padded = ((sub0RawBytes + 31) / 32) * 32;
            int sub1RawBytes = sub1.Length * 2;
            var payload = new byte[sub0Padded + sub1RawBytes];
            for (int i = 0; i < sub0.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(i * 2, 2), sub0[i]);
            for (int i = 0; i < sub1.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(sub0Padded + i * 2, 2), sub1[i]);

            // Attach FREQUENCY chunks immediately after each sample header (real FSB5 wire order)
            byte[] freq0Bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(freq0Bytes, freq0);
            byte[] chunk0 = BuildFsb5Chunk(chunkType: 2u, moreChunks: false, payload: freq0Bytes);

            byte[] freq1Bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(freq1Bytes, freq1);
            byte[] chunk1 = BuildFsb5Chunk(chunkType: 2u, moreChunks: false, payload: freq1Bytes);

            int sampleHeadersSize = 8 + chunk0.Length + 8 + chunk1.Length;
            var data = new byte[60 + sampleHeadersSize + payload.Length];
            Encoding.ASCII.GetBytes("FSB5").CopyTo(data, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 2u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), (uint)sampleHeadersSize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)UnityAudioCodec.Pcm16);

            int pos = 60;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(pos, 8), PackFsb5Meta(true, 0u, 0u, 0u, (uint)sub0.Length));
            pos += 8;
            Buffer.BlockCopy(chunk0, 0, data, pos, chunk0.Length);
            pos += chunk0.Length;

            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(pos, 8), PackFsb5Meta(true, 0u, 0u, (uint)(sub0Padded / 32), (uint)sub1.Length));
            pos += 8;
            Buffer.BlockCopy(chunk1, 0, data, pos, chunk1.Length);
            pos += chunk1.Length;

            Buffer.BlockCopy(payload, 0, data, pos, payload.Length);
            return data;
        }

        private static void AssertWavMatchesPcm16(byte[] wav, int expectedRate, int expectedChannels, short[] expectedSamples)
        {
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal((short)expectedChannels, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)));
            Assert.Equal(expectedRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)));
            Assert.Equal(expectedSamples.Length * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));
            for (int i = 0; i < expectedSamples.Length; i++)
            {
                Assert.Equal(expectedSamples[i], BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2, 2)));
            }
        }

        private static Shader BuildSyntheticShaderWithNonInitStates()
        {
            var shader = (Shader)RuntimeHelpers.GetUninitializedObject(typeof(Shader));
            shader.version = new[] { 2022, 3, 0, 0 };
            shader.platforms = Array.Empty<ShaderCompilerPlatform>();
            shader.offsets = Array.Empty<uint[]>();
            shader.compressedLengths = Array.Empty<uint[]>();
            shader.decompressedLengths = Array.Empty<uint[]>();
            shader.compressedBlob = Array.Empty<byte>();

            static SerializedShaderFloatValue MakeVal(float v, string name)
            {
                var f = (SerializedShaderFloatValue)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderFloatValue));
                f.val = v;
                f.name = name;
                return f;
            }

            static SerializedStencilOp MakeStencil()
            {
                var s = (SerializedStencilOp)RuntimeHelpers.GetUninitializedObject(typeof(SerializedStencilOp));
                s.pass = MakeVal(0f, "<noninit>");
                s.fail = MakeVal(0f, "<noninit>");
                s.zFail = MakeVal(0f, "<noninit>");
                s.comp = MakeVal(8f, "<noninit>");
                return s;
            }

            var rtBlend = new SerializedShaderRTBlendState[8];
            for (int i = 0; i < 8; i++)
            {
                var rt = (SerializedShaderRTBlendState)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderRTBlendState));
                rt.srcBlend = MakeVal(i == 0 ? 5f : 1f, "<noninit>");
                rt.destBlend = MakeVal(i == 0 ? 10f : 0f, "<noninit>");
                rt.srcBlendAlpha = MakeVal(1f, "<noninit>");
                rt.destBlendAlpha = MakeVal(0f, "<noninit>");
                rt.blendOp = MakeVal(0f, "<noninit>");
                rt.blendOpAlpha = MakeVal(0f, "<noninit>");
                rt.colMask = MakeVal(0xf, "<noninit>");
                rtBlend[i] = rt;
            }

            var state = (SerializedShaderState)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderState));
            state.m_Name = "ForwardLit";
            state.rtBlend = rtBlend;
            state.rtSeparateBlend = false;
            state.alphaToMask = MakeVal(0f, "<noninit>");
            state.zClip = MakeVal(1f, "<noninit>");
            state.zTest = MakeVal(4f, "<noninit>");
            state.zWrite = MakeVal(0f, "_ValidZWrite");
            state.culling = MakeVal(1f, "<noninit>"); // Front
            state.conservative = MakeVal(0f, "<noninit>");
            state.offsetFactor = MakeVal(0f, "<noninit>");
            state.offsetUnits = MakeVal(0f, "<noninit>");
            state.stencilRef = MakeVal(0f, "<noninit>");
            state.stencilReadMask = MakeVal(255f, "<noninit>");
            state.stencilWriteMask = MakeVal(255f, "<noninit>");
            state.stencilOp = MakeStencil();
            state.stencilOpFront = MakeStencil();
            state.stencilOpBack = MakeStencil();
            state.fogMode = FogMode.Unknown;
            state.fogStart = MakeVal(0f, "<noninit>");
            state.fogEnd = MakeVal(0f, "<noninit>");
            state.fogDensity = MakeVal(0f, "<noninit>");
            state.fogColor = (SerializedShaderVectorValue)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderVectorValue));
            state.fogColor.x = MakeVal(0f, "");
            state.fogColor.y = MakeVal(0f, "");
            state.fogColor.z = MakeVal(0f, "");
            state.fogColor.w = MakeVal(0f, "");
            state.m_Tags = (SerializedTagMap)RuntimeHelpers.GetUninitializedObject(typeof(SerializedTagMap));
            state.m_Tags.tags = Array.Empty<KeyValuePair<string, string>>();

            static SerializedProgram EmptyProg()
            {
                var p = (SerializedProgram)RuntimeHelpers.GetUninitializedObject(typeof(SerializedProgram));
                p.m_SubPrograms = Array.Empty<SerializedSubProgram>();
                return p;
            }

            var pass = (SerializedPass)RuntimeHelpers.GetUninitializedObject(typeof(SerializedPass));
            pass.m_Type = PassType.Normal;
            pass.m_State = state;
            pass.m_Tags = state.m_Tags;
            pass.progVertex = EmptyProg();
            pass.progFragment = EmptyProg();
            pass.progGeometry = EmptyProg();
            pass.progHull = EmptyProg();
            pass.progDomain = EmptyProg();
            pass.progRayTracing = EmptyProg();

            var sub = (SerializedSubShader)RuntimeHelpers.GetUninitializedObject(typeof(SerializedSubShader));
            sub.m_Passes = new[] { pass };
            sub.m_Tags = state.m_Tags;

            var parsed = (SerializedShader)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShader));
            parsed.m_Name = "Custom/NonInitClean";
            parsed.m_PropInfo = (SerializedProperties)RuntimeHelpers.GetUninitializedObject(typeof(SerializedProperties));
            parsed.m_PropInfo.m_Props = Array.Empty<SerializedProperty>();
            parsed.m_SubShaders = new[] { sub };
            shader.m_ParsedForm = parsed;
            return shader;
        }

        private sealed class SyntheticUnityTypes
        {
            public required TypeDefinition UnityObject;
            public required TypeDefinition MonoBehaviour;
            public required TypeDefinition Texture2D;
            public required TypeDefinition Hash128;
            public required TypeDefinition RenderingLayerMask;
            public required TypeDefinition LazyLoadReference;
            public required TypeDefinition PropertyName;
            public required MethodDefinition SerializeReferenceCtor;
        }

        private static AssemblyDefinition CreateSyntheticUnityAssembly(out SyntheticUnityTypes unity)
        {
            var asm = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("SyntheticF4Tests", new Version(1, 0, 0, 0)),
                "SyntheticF4Tests.dll",
                ModuleKind.Dll);
            var mod = asm.MainModule;
            var objRef = mod.TypeSystem.Object;
            var valueTypeRef = new TypeReference("System", "ValueType", mod, mod.TypeSystem.CoreLibrary);
            var enumRef = new TypeReference("System", "Enum", mod, mod.TypeSystem.CoreLibrary);
            var attrRef = new TypeReference("System", "Attribute", mod, mod.TypeSystem.CoreLibrary);
            _ = enumRef;

            var unityObj = new TypeDefinition("UnityEngine", "Object", CecilTypeAttributes.Public | CecilTypeAttributes.Class, objRef);
            var mono = new TypeDefinition("UnityEngine", "MonoBehaviour", CecilTypeAttributes.Public | CecilTypeAttributes.Class, unityObj);
            var tex2d = new TypeDefinition("UnityEngine", "Texture2D", CecilTypeAttributes.Public | CecilTypeAttributes.Class, unityObj);
            var hash128 = new TypeDefinition("UnityEngine", "Hash128", CecilTypeAttributes.Public | CecilTypeAttributes.SequentialLayout | CecilTypeAttributes.Sealed | CecilTypeAttributes.Serializable, valueTypeRef);
            var rlm = new TypeDefinition("UnityEngine", "RenderingLayerMask", CecilTypeAttributes.Public | CecilTypeAttributes.SequentialLayout | CecilTypeAttributes.Sealed, valueTypeRef);
            var propName = new TypeDefinition("UnityEngine", "PropertyName", CecilTypeAttributes.Public | CecilTypeAttributes.SequentialLayout | CecilTypeAttributes.Sealed, valueTypeRef);
            var lazy = new TypeDefinition("UnityEngine", "LazyLoadReference`1", CecilTypeAttributes.Public | CecilTypeAttributes.SequentialLayout | CecilTypeAttributes.Sealed | CecilTypeAttributes.Serializable, valueTypeRef);
            lazy.GenericParameters.Add(new GenericParameter("T", lazy));

            var serRefAttr = new TypeDefinition("UnityEngine", "SerializeReference", CecilTypeAttributes.Public | CecilTypeAttributes.Class, attrRef);
            var serRefCtor = new MethodDefinition(".ctor", CecilMethodAttributes.Public | CecilMethodAttributes.SpecialName | CecilMethodAttributes.RTSpecialName, mod.TypeSystem.Void);
            serRefAttr.Methods.Add(serRefCtor);

            mod.Types.Add(unityObj);
            mod.Types.Add(mono);
            mod.Types.Add(tex2d);
            mod.Types.Add(hash128);
            mod.Types.Add(rlm);
            mod.Types.Add(propName);
            mod.Types.Add(lazy);
            mod.Types.Add(serRefAttr);

            unity = new SyntheticUnityTypes
            {
                UnityObject = unityObj,
                MonoBehaviour = mono,
                Texture2D = tex2d,
                Hash128 = hash128,
                RenderingLayerMask = rlm,
                LazyLoadReference = lazy,
                PropertyName = propName,
                SerializeReferenceCtor = serRefCtor
            };
            return asm;
        }

        private static TypeDefinition CreateClass(ModuleDefinition mod, string name, TypeReference baseType)
        {
            var td = new TypeDefinition("TestGame", name, CecilTypeAttributes.Public | CecilTypeAttributes.Class, baseType);
            mod.Types.Add(td);
            return td;
        }

        private static TypeDefinition CreateSerializableClass(ModuleDefinition mod, string name)
        {
            var td = new TypeDefinition("TestGame", name, CecilTypeAttributes.Public | CecilTypeAttributes.Class | CecilTypeAttributes.Serializable, mod.TypeSystem.Object);
            mod.Types.Add(td);
            return td;
        }

        private static TypeDefinition CreateEnum(ModuleDefinition mod, string name, TypeReference underlyingType)
        {
            var enumBase = new TypeReference("System", "Enum", mod, mod.TypeSystem.CoreLibrary);
            var td = new TypeDefinition("TestGame", name, CecilTypeAttributes.Public | CecilTypeAttributes.Sealed, enumBase);
            td.Fields.Add(new FieldDefinition("value__", CecilFieldAttributes.Public | CecilFieldAttributes.SpecialName | CecilFieldAttributes.RTSpecialName, underlyingType));
            mod.Types.Add(td);
            return td;
        }

        private static void AssertNode(List<TypeTreeNode> nodes, string name, string expectedType, bool align)
        {
            var node = nodes.Single(n => n.m_Name == name);
            Assert.Equal(expectedType, node.m_Type);
            Assert.Equal(align, (node.m_MetaFlag & 0x4000) != 0);
        }

        private static void AssertNoBrokenArrays(List<TypeTreeNode> nodes)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].m_Type == "Array")
                {
                    int arrLevel = nodes[i].m_Level;
                    Assert.True(i + 2 < nodes.Count, $"Truncated Array at index {i}");
                    Assert.Equal(arrLevel + 1, nodes[i + 1].m_Level);
                    Assert.Equal("size", nodes[i + 1].m_Name);
                    Assert.Equal(arrLevel + 1, nodes[i + 2].m_Level);
                }
            }
        }

        private static long MeasureTypeTreeReadBytes(List<TypeTreeNode> fieldNodes, byte[] payload)
        {
            var allNodes = new List<TypeTreeNode>(fieldNodes.Count + 1)
            {
                new TypeTreeNode("MonoBehaviour", "Base", 0, false)
            };
            allNodes.AddRange(fieldNodes);
            var tt = new TypeTree { m_Nodes = allNodes };

            var sf = CreateMinimalSerializedFile("2022.3.0f1");
            var oi = new ObjectInfo { byteStart = 0, byteSize = (uint)payload.Length, classID = (int)ClassIDType.MonoBehaviour, m_PathID = 1 };
            using var ms = new MemoryStream(payload);
            using var br = new EndianBinaryReader(ms, EndianType.LittleEndian);
            var reader = new ObjectReader(br, sf, oi);
            TypeTreeHelper.ReadType(tt, reader);
            return reader.Position - reader.byteStart;
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
            var fileReader = new FileReader("f4_test.assets", ms);
            return new SerializedFile(fileReader, new AssetsManager());
        }

        #endregion
    }
}
