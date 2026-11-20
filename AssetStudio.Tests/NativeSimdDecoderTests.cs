using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Texture2DDecoder;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class NativeSimdDecoderTests
    {
        private readonly ITestOutputHelper _output;

        public NativeSimdDecoderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static bool IsNativeDecoderAvailable()
        {
            string baseDir = AppContext.BaseDirectory;
            string archDir = Environment.Is64BitProcess ? "x64" : "x86";
            string fileName = OperatingSystem.IsWindows()
                ? "Texture2DDecoderNative.dll"
                : OperatingSystem.IsLinux()
                    ? "libTexture2DDecoderNative.so"
                    : "libTexture2DDecoderNative.dylib";

            return File.Exists(Path.Combine(baseDir, archDir, fileName))
                || File.Exists(Path.Combine(baseDir, fileName));
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(5, 7)]
        [InlineData(13, 9)]
        [InlineData(32, 32)]
        public void DecodeDXT1_SimdAndSpanOverload_MatchesScalarReference(int width, int height)
        {
            if (!IsNativeDecoderAvailable())
                return;

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var data = new byte[blocksX * blocksY * 8];
            var rng = new Random(1001 + width * 31 + height);
            rng.NextBytes(data);

            // Force both q0 > q1 (4-color) and q0 <= q1 (3-color + transparent black) blocks
            if (data.Length >= 16)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), 0xF800);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), 0x001F);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 0x001F);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10, 2), 0xF800);
            }

            var expected = ReferenceDecodeBc1(data, width, height);
            var actualArray = new byte[width * height * 4];
            var actualSpan = new byte[width * height * 4];

            Assert.True(TextureDecoder.DecodeDXT1(data, width, height, actualArray));
            Assert.True(TextureDecoder.DecodeDXT1((ReadOnlySpan<byte>)data, width, height, actualSpan.AsSpan()));

            Assert.Equal(expected, actualArray);
            Assert.Equal(expected, actualSpan);
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(5, 7)]
        [InlineData(13, 9)]
        [InlineData(32, 32)]
        public void DecodeDXT3_SimdAndSpanOverload_MatchesScalarReference(int width, int height)
        {
            if (!IsNativeDecoderAvailable())
                return;

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var data = new byte[blocksX * blocksY * 16];
            var rng = new Random(2002 + width * 31 + height);
            rng.NextBytes(data);

            var expected = ReferenceDecodeDxt3(data, width, height);
            var actualArray = new byte[width * height * 4];
            var actualSpan = new byte[width * height * 4];

            Assert.True(TextureDecoder.DecodeDXT3(data, width, height, actualArray));
            Assert.True(TextureDecoder.DecodeDXT3((ReadOnlySpan<byte>)data, width, height, actualSpan.AsSpan()));

            Assert.Equal(expected, actualArray);
            Assert.Equal(expected, actualSpan);
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(5, 7)]
        [InlineData(13, 9)]
        [InlineData(32, 32)]
        public void DecodeDXT5_BC4_BC5_SimdAndSpanOverloads_MatchScalarReference(int width, int height)
        {
            if (!IsNativeDecoderAvailable())
                return;

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var data16 = new byte[blocksX * blocksY * 16];
            var data8 = new byte[blocksX * blocksY * 8];
            var rng = new Random(3003 + width * 31 + height);
            rng.NextBytes(data16);
            rng.NextBytes(data8);

            // Ensure both a0 > a1 and a0 <= a1 modes are exercised
            if (data8.Length >= 16)
            {
                data8[0] = 240; data8[1] = 10;
                data8[8] = 10;  data8[9] = 240;
            }
            if (data16.Length >= 32)
            {
                data16[0] = 240; data16[1] = 10;
                data16[16] = 10; data16[17] = 240;
            }

            // DXT5 (BC3)
            var expectedBc3 = ReferenceDecodeBc3(data16, width, height);
            var actualBc3Arr = new byte[width * height * 4];
            var actualBc3Span = new byte[width * height * 4];
            Assert.True(TextureDecoder.DecodeDXT5(data16, width, height, actualBc3Arr));
            Assert.True(TextureDecoder.DecodeDXT5((ReadOnlySpan<byte>)data16, width, height, actualBc3Span.AsSpan()));
            Assert.Equal(expectedBc3, actualBc3Arr);
            Assert.Equal(expectedBc3, actualBc3Span);

            // BC4
            var expectedBc4 = ReferenceDecodeBc4(data8, width, height);
            var actualBc4Arr = new byte[width * height * 4];
            var actualBc4Span = new byte[width * height * 4];
            Assert.True(TextureDecoder.DecodeBC4(data8, width, height, actualBc4Arr));
            Assert.True(TextureDecoder.DecodeBC4((ReadOnlySpan<byte>)data8, width, height, actualBc4Span.AsSpan()));
            Assert.Equal(expectedBc4, actualBc4Arr);
            Assert.Equal(expectedBc4, actualBc4Span);

            // BC5
            var expectedBc5 = ReferenceDecodeBc5(data16, width, height);
            var actualBc5Arr = new byte[width * height * 4];
            var actualBc5Span = new byte[width * height * 4];
            Assert.True(TextureDecoder.DecodeBC5(data16, width, height, actualBc5Arr));
            Assert.True(TextureDecoder.DecodeBC5((ReadOnlySpan<byte>)data16, width, height, actualBc5Span.AsSpan()));
            Assert.Equal(expectedBc5, actualBc5Arr);
            Assert.Equal(expectedBc5, actualBc5Span);
        }

        [Fact]
        public void DecodeBC6_And_DecodeBC7_ParityAndSpanOverloads()
        {
            if (!IsNativeDecoderAvailable())
                return;

            // BC6H Mode 3 (5-bit mode 00011 = 0x03: 1-subset, 10-bit endpoints R0,G0,B0, R1,G1,B1)
            // Construct a block where R0=1023 (max -> 1.0f), G0=0, B0=0 and R1=1023, G1=0, B1=0, all weights=0 -> pure red (BGRA = 0, 0, 255, 255)
            var bc6Block = new byte[16];
            ulong bc6Lo = 0x03UL | (1023UL << 5) | (0UL << 15) | (0UL << 25) | (1023UL << 35);
            BinaryPrimitives.WriteUInt64LittleEndian(bc6Block.AsSpan(0, 8), bc6Lo);
            var bc6OutArr = new byte[4 * 4 * 4];
            var bc6OutSpan = new byte[4 * 4 * 4];
            Assert.True(TextureDecoder.DecodeBC6(bc6Block, 4, 4, bc6OutArr));
            Assert.True(TextureDecoder.DecodeBC6((ReadOnlySpan<byte>)bc6Block, 4, 4, bc6OutSpan.AsSpan()));
            Assert.Equal(bc6OutArr, bc6OutSpan);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(0, bc6OutArr[i * 4 + 0]);   // B
                Assert.Equal(0, bc6OutArr[i * 4 + 1]);   // G
                Assert.Equal(255, bc6OutArr[i * 4 + 2]); // R
                Assert.Equal(255, bc6OutArr[i * 4 + 3]); // A
            }

            // BC7 Mode 6 (bit 6 set = 0x40: 1 subset, 7-bit RGBA + 1 p-bit per endpoint, 4-bit indices)
            // If all endpoints and indices are 0 except mode bit 6 (0x40), all RGBA endpoints are 0 -> (0,0,0,0)
            var bc7ZeroBlock = new byte[16];
            bc7ZeroBlock[0] = 0x40;
            var bc7OutArr = new byte[4 * 4 * 4];
            var bc7OutSpan = new byte[4 * 4 * 4];
            Assert.True(TextureDecoder.DecodeBC7(bc7ZeroBlock, 4, 4, bc7OutArr));
            Assert.True(TextureDecoder.DecodeBC7((ReadOnlySpan<byte>)bc7ZeroBlock, 4, 4, bc7OutSpan.AsSpan()));
            Assert.Equal(bc7OutArr, bc7OutSpan);
            Assert.All(bc7OutArr, b => Assert.Equal(0, b));

            // Non-block-aligned random BC6 and BC7 parity between byte[] and Span overloads across all modes
            const int w = 13, h = 9;
            var rng = new Random(6007);
            var randomBlocks = new byte[((w + 3) / 4) * ((h + 3) / 4) * 16];
            rng.NextBytes(randomBlocks);

            var bc6A = new byte[w * h * 4];
            var bc6S = new byte[w * h * 4];
            Assert.True(TextureDecoder.DecodeBC6(randomBlocks, w, h, bc6A));
            Assert.True(TextureDecoder.DecodeBC6((ReadOnlySpan<byte>)randomBlocks, w, h, bc6S.AsSpan()));
            Assert.Equal(bc6A, bc6S);

            var bc7A = new byte[w * h * 4];
            var bc7S = new byte[w * h * 4];
            Assert.True(TextureDecoder.DecodeBC7(randomBlocks, w, h, bc7A));
            Assert.True(TextureDecoder.DecodeBC7((ReadOnlySpan<byte>)randomBlocks, w, h, bc7S.AsSpan()));
            Assert.Equal(bc7A, bc7S);
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(5, 7)]
        [InlineData(13, 9)]
        public void DecodeETC1_ETC2_ETC2A1_ETC2A8_EAC_SimdAndSpanOverloads_MatchReference(int width, int height)
        {
            if (!IsNativeDecoderAvailable())
                return;

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var data8 = new byte[blocksX * blocksY * 8];
            var data16 = new byte[blocksX * blocksY * 16];
            var rng = new Random(4004 + width * 31 + height);
            rng.NextBytes(data8);
            rng.NextBytes(data16);

            // Ensure all ETC2 modes (individual, differential, T, H, planar) are exercised
            if (data8.Length >= 40)
            {
                data8[3] &= 0xFD; // block 0: individual (diff=0)
                data8[8 + 3] |= 2; data8[8 + 0] = 0x80; data8[8 + 1] = 0x80; data8[8 + 2] = 0x80; // block 1: differential
                data8[16 + 3] |= 2; data8[16 + 0] = 0x04; // block 2: r=0, dr=-4 -> T-mode
                data8[24 + 3] |= 2; data8[24 + 0] = 0x80; data8[24 + 1] = 0x04; // block 3: g=0, dg=-4 -> H-mode
                data8[32 + 3] |= 2; data8[32 + 0] = 0x80; data8[32 + 1] = 0x80; data8[32 + 2] = 0x04; // block 4: b=0, db=-4 -> Planar mode
            }

            // ETC1 parity vs scalar reference
            var expectedEtc1 = ReferenceDecodeEtc1(data8, width, height);
            var actualEtc1Arr = new byte[width * height * 4];
            var actualEtc1Span = new byte[width * height * 4];
            Assert.True(TextureDecoder.DecodeETC1(data8, width, height, actualEtc1Arr));
            Assert.True(TextureDecoder.DecodeETC1((ReadOnlySpan<byte>)data8, width, height, actualEtc1Span.AsSpan()));
            Assert.Equal(expectedEtc1, actualEtc1Arr);
            Assert.Equal(expectedEtc1, actualEtc1Span);

            // ETC2, ETC2A1, ETC2A8, EACR, EACRSigned, EACRG, EACRGSigned array vs span parity
            VerifyArrayAndSpanParity(data8, width, height, TextureDecoder.DecodeETC2, (d, w, h, o) => TextureDecoder.DecodeETC2(d, w, h, o));
            VerifyArrayAndSpanParity(data8, width, height, TextureDecoder.DecodeETC2A1, (d, w, h, o) => TextureDecoder.DecodeETC2A1(d, w, h, o));
            VerifyArrayAndSpanParity(data16, width, height, TextureDecoder.DecodeETC2A8, (d, w, h, o) => TextureDecoder.DecodeETC2A8(d, w, h, o));
            VerifyArrayAndSpanParity(data8, width, height, TextureDecoder.DecodeEACR, (d, w, h, o) => TextureDecoder.DecodeEACR(d, w, h, o));
            VerifyArrayAndSpanParity(data8, width, height, TextureDecoder.DecodeEACRSigned, (d, w, h, o) => TextureDecoder.DecodeEACRSigned(d, w, h, o));
            VerifyArrayAndSpanParity(data16, width, height, TextureDecoder.DecodeEACRG, (d, w, h, o) => TextureDecoder.DecodeEACRG(d, w, h, o));
            VerifyArrayAndSpanParity(data16, width, height, TextureDecoder.DecodeEACRGSigned, (d, w, h, o) => TextureDecoder.DecodeEACRGSigned(d, w, h, o));
        }

        [Theory]
        [InlineData(4, 4, 4, 4)]
        [InlineData(4, 4, 5, 7)]
        [InlineData(6, 6, 12, 12)]
        [InlineData(6, 6, 13, 9)]
        [InlineData(8, 8, 16, 16)]
        [InlineData(12, 12, 12, 12)]
        [InlineData(12, 12, 17, 15)]
        public void DecodeASTC_AllBlockSizesAndNonAligned_SimdAndSpanParity(int bw, int bh, int width, int height)
        {
            if (!IsNativeDecoderAvailable())
                return;

            int blocksX = (width + bw - 1) / bw;
            int blocksY = (height + bh - 1) / bh;
            var data = new byte[blocksX * blocksY * 16];
            var rng = new Random(5005 + bw * 100 + width * 17 + height);
            rng.NextBytes(data);

            // Force Block 0 to be a valid 1-partition 1-plane LDR block (4x4 weight grid, weight_range=2 -> 1 bit/weight, 24 bits)
            // mode: low 2 bits = 01, bits 2..3 = 00 (width = ((buf>>7)&3)+4 = 4, height = ((buf>>5)&3)+2 = 4 -> bits 5..6 = 2 (0x40), bit 4 = 0, bit 1..2 = 1 -> buf[0] = 0x43, buf[1] = 0x00)
            if (data.Length >= 16)
            {
                data[0] = 0x43; // low2=3, weight_range=2 (1-bit weights), height=4, width=4 (16 weights = 16 bits? Wait: weight_range=4 is 2-bit weights -> 32 bits in [24,96]!)
                // For weight_range=4: bit 4 of buf[0] = 0, bits 1..2 of buf[0] = 2 (0b100 = 0x04), bit 0 = 1 -> low4 = 0x05; height=4 -> bits 5..6 = 2 (0x40) -> buf[0] = 0x45, buf[1] = 0x00
                data[0] = 0x45;
                data[1] = 0x00; // part_num=1, dual_plane=0, cem=0 (LDR Luminance)
            }

            var outArr = new byte[width * height * 4];
            var outSpan = new byte[width * height * 4];
            Assert.True(TextureDecoder.DecodeASTC(data, width, height, bw, bh, outArr));
            Assert.True(TextureDecoder.DecodeASTC((ReadOnlySpan<byte>)data, width, height, bw, bh, outSpan.AsSpan()));
            Assert.Equal(outArr, outSpan);
        }

        [Fact]
        public void SpanOverloads_InvalidAndAdversarialBuffers_ReturnFalseSafely_RegressionSEC03()
        {
            if (!IsNativeDecoderAvailable())
                return;

            ReadOnlySpan<byte> emptyIn = ReadOnlySpan<byte>.Empty;
            Span<byte> validOut = stackalloc byte[64];
            ReadOnlySpan<byte> validIn16 = stackalloc byte[16];
            Span<byte> undersizedOut = stackalloc byte[60];

            Assert.False(TextureDecoder.DecodeDXT1(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeDXT3(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeDXT5(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeBC4(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeBC5(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeBC6(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeBC7(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeETC1(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeETC2(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeETC2A1(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeETC2A8(emptyIn, 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeASTC(emptyIn, 4, 4, 4, 4, validOut));

            Assert.False(TextureDecoder.DecodeDXT3(validIn16.Slice(0, 15), 4, 4, validOut));
            Assert.False(TextureDecoder.DecodeDXT3(validIn16, 4, 4, undersizedOut));
            Assert.False(TextureDecoder.DecodeDXT3(validIn16, 0, 4, validOut));
            Assert.False(TextureDecoder.DecodeDXT3(validIn16, 4, -1, validOut));

            Assert.Null(TextureDecoder.UnpackCrunch(emptyIn));
            Assert.Null(TextureDecoder.UnpackUnityCrunch(emptyIn));

            // Fuzz all decoders with all-0x00, all-0xFF, and 250 random blocks
            var rng = new Random(9001);
            var block16 = new byte[16];
            var out12x12 = new byte[12 * 12 * 4];
            for (int iter = 0; iter < 252; iter++)
            {
                if (iter == 0)
                    Array.Clear(block16, 0, 16);
                else if (iter == 1)
                    Array.Fill(block16, (byte)0xFF);
                else
                    rng.NextBytes(block16);

                ReadOnlySpan<byte> s16 = block16;
                ReadOnlySpan<byte> s8 = s16.Slice(0, 8);
                Span<byte> sOut4x4 = out12x12.AsSpan(0, 64);

                Assert.True(TextureDecoder.DecodeDXT1(s8, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeDXT3(s16, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeDXT5(s16, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeBC4(s8, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeBC5(s16, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeBC6(s16, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeBC7(s16, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeETC1(s8, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeETC2(s8, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeETC2A1(s8, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeETC2A8(s16, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeASTC(s16, 4, 4, 4, 4, sOut4x4));
                Assert.True(TextureDecoder.DecodeASTC(s16, 12, 12, 12, 12, out12x12.AsSpan()));
            }
        }

        [Fact]
        public void NativeSimdThroughput_Benchmark_LogsMegapixelsPerSecond()
        {
            if (!IsNativeDecoderAvailable())
                return;

            const int width = 512;
            const int height = 512;
            const int iterations = 20;
            int numBlocks = (width / 4) * (height / 4);

            var data8 = new byte[numBlocks * 8];
            var data16 = new byte[numBlocks * 16];
            var rng = new Random(42);
            rng.NextBytes(data8);
            rng.NextBytes(data16);
            // Set valid ASTC 4x4 LDR header on each 16-byte block for ASTC benchmark
            var astc16 = (byte[])data16.Clone();
            for (int i = 0; i < astc16.Length; i += 16)
            {
                astc16[i] = 0x45;
                astc16[i + 1] = 0x00;
            }

            var output = new byte[width * height * 4];
            double totalMpix = (double)width * height * iterations / 1_000_000.0;

            double Measure(Action action)
            {
                action(); // warmup
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                    action();
                sw.Stop();
                return totalMpix / sw.Elapsed.TotalSeconds;
            }

            double dxt1Mpix = Measure(() => TextureDecoder.DecodeDXT1((ReadOnlySpan<byte>)data8, width, height, output));
            double dxt3NativeMpix = Measure(() => TextureDecoder.DecodeDXT3((ReadOnlySpan<byte>)data16, width, height, output));
            double dxt3ManagedMpix = Measure(() => ReferenceDecodeDxt3InPlace(data16, width, height, output));
            double dxt5Mpix = Measure(() => TextureDecoder.DecodeDXT5((ReadOnlySpan<byte>)data16, width, height, output));
            double bc4Mpix = Measure(() => TextureDecoder.DecodeBC4((ReadOnlySpan<byte>)data8, width, height, output));
            double bc5Mpix = Measure(() => TextureDecoder.DecodeBC5((ReadOnlySpan<byte>)data16, width, height, output));
            double bc6Mpix = Measure(() => TextureDecoder.DecodeBC6((ReadOnlySpan<byte>)data16, width, height, output));
            double bc7Mpix = Measure(() => TextureDecoder.DecodeBC7((ReadOnlySpan<byte>)data16, width, height, output));
            double etc1Mpix = Measure(() => TextureDecoder.DecodeETC1((ReadOnlySpan<byte>)data8, width, height, output));
            double etc2Mpix = Measure(() => TextureDecoder.DecodeETC2((ReadOnlySpan<byte>)data8, width, height, output));
            double etc2a8Mpix = Measure(() => TextureDecoder.DecodeETC2A8((ReadOnlySpan<byte>)data16, width, height, output));
            double astc4x4Mpix = Measure(() => TextureDecoder.DecodeASTC((ReadOnlySpan<byte>)astc16, width, height, 4, 4, output));

            _output.WriteLine($"[SOTA-05 Benchmark 512x512 x{iterations}] " +
                $"DXT1={dxt1Mpix:F1} MPix/s | DXT3(NativeSIMD)={dxt3NativeMpix:F1} MPix/s vs DXT3(ManagedScalar)={dxt3ManagedMpix:F1} MPix/s ({dxt3NativeMpix / Math.Max(0.1, dxt3ManagedMpix):F1}x) | " +
                $"DXT5={dxt5Mpix:F1} MPix/s | BC4={bc4Mpix:F1} MPix/s | BC5={bc5Mpix:F1} MPix/s | BC6H={bc6Mpix:F1} MPix/s | BC7={bc7Mpix:F1} MPix/s | " +
                $"ETC1={etc1Mpix:F1} MPix/s | ETC2={etc2Mpix:F1} MPix/s | ETC2A8={etc2a8Mpix:F1} MPix/s | ASTC_4x4={astc4x4Mpix:F1} MPix/s");

            Assert.True(dxt1Mpix > 0 && dxt3NativeMpix > 0 && dxt5Mpix > 0 && astc4x4Mpix > 0);
        }

        private delegate bool SpanDecodeFunc(ReadOnlySpan<byte> data, int width, int height, Span<byte> image);

        private static void VerifyArrayAndSpanParity(
            byte[] data,
            int width,
            int height,
            Func<byte[], int, int, byte[], bool> arrayFunc,
            SpanDecodeFunc spanFunc)
        {
            var outArr = new byte[width * height * 4];
            var outSpan = new byte[width * height * 4];
            Assert.True(arrayFunc(data, width, height, outArr));
            Assert.True(spanFunc(data, width, height, outSpan));
            Assert.Equal(outArr, outSpan);
        }

        private static void UnpackRgb565(ushort v, out byte r, out byte g, out byte b)
        {
            r = (byte)(((v >> 8) & 0xF8) | (v >> 13));
            g = (byte)(((v >> 3) & 0xFC) | ((v >> 9) & 0x03));
            b = (byte)((v << 3) | ((v >> 2) & 0x07));
        }

        private static void DecodeBc1BlockScalar(ReadOnlySpan<byte> block, Span<uint> out16)
        {
            ushort q0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(0, 2));
            ushort q1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(2, 2));
            UnpackRgb565(q0, out byte r0, out byte g0, out byte b0);
            UnpackRgb565(q1, out byte r1, out byte g1, out byte b1);

            Span<uint> c = stackalloc uint[4];
            c[0] = (uint)(b0 | (g0 << 8) | (r0 << 16) | (255 << 24));
            c[1] = (uint)(b1 | (g1 << 8) | (r1 << 16) | (255 << 24));
            if (q0 > q1)
            {
                c[2] = (uint)(((b0 * 2 + b1) / 3) | (((g0 * 2 + g1) / 3) << 8) | (((r0 * 2 + r1) / 3) << 16) | (255 << 24));
                c[3] = (uint)(((b0 + b1 * 2) / 3) | (((g0 + g1 * 2) / 3) << 8) | (((r0 + r1 * 2) / 3) << 16) | (255 << 24));
            }
            else
            {
                c[2] = (uint)(((b0 + b1) / 2) | (((g0 + g1) / 2) << 8) | (((r0 + r1) / 2) << 16) | (255 << 24));
                c[3] = 0xFF000000u;
            }

            uint d = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4, 4));
            for (int i = 0; i < 16; i++, d >>= 2)
            {
                out16[i] = c[(int)(d & 3)];
            }
        }

        private static void DecodeBc3AlphaScalar(ReadOnlySpan<byte> block, Span<byte> out64, int channel)
        {
            Span<byte> a = stackalloc byte[8];
            a[0] = block[0];
            a[1] = block[1];
            if (a[0] > a[1])
            {
                a[2] = (byte)((a[0] * 6 + a[1]) / 7);
                a[3] = (byte)((a[0] * 5 + a[1] * 2) / 7);
                a[4] = (byte)((a[0] * 4 + a[1] * 3) / 7);
                a[5] = (byte)((a[0] * 3 + a[1] * 4) / 7);
                a[6] = (byte)((a[0] * 2 + a[1] * 5) / 7);
                a[7] = (byte)((a[0] + a[1] * 6) / 7);
            }
            else
            {
                a[2] = (byte)((a[0] * 4 + a[1]) / 5);
                a[3] = (byte)((a[0] * 3 + a[1] * 2) / 5);
                a[4] = (byte)((a[0] * 2 + a[1] * 3) / 5);
                a[5] = (byte)((a[0] + a[1] * 4) / 5);
                a[6] = 0;
                a[7] = 255;
            }

            ulong d = BinaryPrimitives.ReadUInt64LittleEndian(block) >> 16;
            for (int i = 0; i < 16; i++, d >>= 3)
            {
                out64[i * 4 + channel] = a[(int)(d & 7)];
            }
        }

        private static void CopyBlock4x4ToImage(int bx, int by, int w, int h, ReadOnlySpan<uint> block16, byte[] image)
        {
            for (int py = 0; py < 4; py++)
            {
                int y = by * 4 + py;
                if (y >= h) break;
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    if (x >= w) break;
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((y * w + x) * 4, 4), block16[py * 4 + px]);
                }
            }
        }

        private static byte[] ReferenceDecodeBc1(byte[] data, int w, int h)
        {
            var image = new byte[w * h * 4];
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<uint> buf = stackalloc uint[16];
            int offset = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++, offset += 8)
                {
                    DecodeBc1BlockScalar(data.AsSpan(offset, 8), buf);
                    CopyBlock4x4ToImage(bx, by, w, h, buf, image);
                }
            }
            return image;
        }

        private static byte[] ReferenceDecodeDxt3(byte[] data, int w, int h)
        {
            var buff = new byte[w * h * 4];
            ReferenceDecodeDxt3InPlace(data, w, h, buff);
            return buff;
        }

        private static void ReferenceDecodeDxt3InPlace(byte[] image_data, int m_Width, int m_Height, byte[] buff)
        {
            int blockCountX = (m_Width + 3) / 4;
            int blockCountY = (m_Height + 3) / 4;
            for (int y = 0; y < blockCountY; y++)
            {
                for (int x = 0; x < blockCountX; x++)
                {
                    int blockIndex = (y * blockCountX + x) * 16;
                    ulong alphaData = BitConverter.ToUInt64(image_data, blockIndex);
                    ushort c0 = BitConverter.ToUInt16(image_data, blockIndex + 8);
                    ushort c1 = BitConverter.ToUInt16(image_data, blockIndex + 10);
                    uint colorIndices = BitConverter.ToUInt32(image_data, blockIndex + 12);

                    UnpackRgb565(c0, out byte r0, out byte g0, out byte b0);
                    UnpackRgb565(c1, out byte r1, out byte g1, out byte b1);
                    byte r2 = (byte)((2 * r0 + r1) / 3);
                    byte g2 = (byte)((2 * g0 + g1) / 3);
                    byte b2 = (byte)((2 * b0 + b1) / 3);
                    byte r3 = (byte)((r0 + 2 * r1) / 3);
                    byte g3 = (byte)((g0 + 2 * g1) / 3);
                    byte b3 = (byte)((b0 + 2 * b1) / 3);

                    for (int py = 0; py < 4; py++)
                    {
                        int pixelY = y * 4 + py;
                        if (pixelY >= m_Height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int pixelX = x * 4 + px;
                            if (pixelX >= m_Width) continue;
                            int pixelIndexInBlock = py * 4 + px;
                            byte alpha4 = (byte)((alphaData >> (pixelIndexInBlock * 4)) & 0x0F);
                            byte alpha = (byte)((alpha4 << 4) | alpha4);
                            uint colorIndex = (colorIndices >> (pixelIndexInBlock * 2)) & 0x03;
                            byte r = 0, g = 0, b = 0;
                            switch (colorIndex)
                            {
                                case 0: r = r0; g = g0; b = b0; break;
                                case 1: r = r1; g = g1; b = b1; break;
                                case 2: r = r2; g = g2; b = b2; break;
                                case 3: r = r3; g = g3; b = b3; break;
                            }
                            int outIndex = (pixelY * m_Width + pixelX) * 4;
                            buff[outIndex] = b;
                            buff[outIndex + 1] = g;
                            buff[outIndex + 2] = r;
                            buff[outIndex + 3] = alpha;
                        }
                    }
                }
            }
        }

        private static byte[] ReferenceDecodeBc3(byte[] data, int w, int h)
        {
            var image = new byte[w * h * 4];
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<uint> buf = stackalloc uint[16];
            int offset = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++, offset += 16)
                {
                    DecodeBc1BlockScalar(data.AsSpan(offset + 8, 8), buf);
                    DecodeBc3AlphaScalar(data.AsSpan(offset, 8), System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf), 3);
                    CopyBlock4x4ToImage(bx, by, w, h, buf, image);
                }
            }
            return image;
        }

        private static byte[] ReferenceDecodeBc4(byte[] data, int w, int h)
        {
            var image = new byte[w * h * 4];
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<uint> buf = stackalloc uint[16];
            int offset = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++, offset += 8)
                {
                    buf.Fill(0xFF000000u);
                    DecodeBc3AlphaScalar(data.AsSpan(offset, 8), System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf), 2);
                    CopyBlock4x4ToImage(bx, by, w, h, buf, image);
                }
            }
            return image;
        }

        private static byte[] ReferenceDecodeBc5(byte[] data, int w, int h)
        {
            var image = new byte[w * h * 4];
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<uint> buf = stackalloc uint[16];
            int offset = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++, offset += 16)
                {
                    buf.Fill(0xFF000000u);
                    DecodeBc3AlphaScalar(data.AsSpan(offset, 8), System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf), 2);
                    DecodeBc3AlphaScalar(data.AsSpan(offset + 8, 8), System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf), 1);
                    CopyBlock4x4ToImage(bx, by, w, h, buf, image);
                }
            }
            return image;
        }

        private static byte[] ReferenceDecodeEtc1(byte[] data, int w, int h)
        {
            int[] writeOrder = { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
            int[,] modTable = { { 2, 8 }, { 5, 17 }, { 9, 29 }, { 13, 42 }, { 18, 60 }, { 24, 80 }, { 33, 106 }, { 47, 183 } };
            int[,] subblockTable = {
                { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1 },
                { 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1 }
            };

            var image = new byte[w * h * 4];
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<uint> buf = stackalloc uint[16];
            int offset = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++, offset += 8)
                {
                    ReadOnlySpan<byte> b = data.AsSpan(offset, 8);
                    int code0 = b[3] >> 5;
                    int code1 = (b[3] >> 2) & 7;
                    int flip = b[3] & 1;
                    int[,] c = new int[2, 3];
                    if ((b[3] & 2) != 0)
                    {
                        c[0, 0] = b[0] & 0xf8;
                        c[0, 1] = b[1] & 0xf8;
                        c[0, 2] = b[2] & 0xf8;
                        c[1, 0] = (byte)(c[0, 0] + ((b[0] << 3) & 0x18) - ((b[0] << 3) & 0x20));
                        c[1, 1] = (byte)(c[0, 1] + ((b[1] << 3) & 0x18) - ((b[1] << 3) & 0x20));
                        c[1, 2] = (byte)(c[0, 2] + ((b[2] << 3) & 0x18) - ((b[2] << 3) & 0x20));
                        c[0, 0] |= c[0, 0] >> 5;
                        c[0, 1] |= c[0, 1] >> 5;
                        c[0, 2] |= c[0, 2] >> 5;
                        c[1, 0] |= c[1, 0] >> 5;
                        c[1, 1] |= c[1, 1] >> 5;
                        c[1, 2] |= c[1, 2] >> 5;
                    }
                    else
                    {
                        c[0, 0] = (b[0] & 0xf0) | (b[0] >> 4);
                        c[1, 0] = (byte)((b[0] & 0x0f) | (b[0] << 4));
                        c[0, 1] = (b[1] & 0xf0) | (b[1] >> 4);
                        c[1, 1] = (byte)((b[1] & 0x0f) | (b[1] << 4));
                        c[0, 2] = (b[2] & 0xf0) | (b[2] >> 4);
                        c[1, 2] = (byte)((b[2] & 0x0f) | (b[2] << 4));
                    }

                    int j = (b[6] << 8) | b[7];
                    int k = (b[4] << 8) | b[5];
                    for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                    {
                        int s = subblockTable[flip, i];
                        int code = s == 0 ? code0 : code1;
                        int m = modTable[code, j & 1];
                        int delta = (k & 1) != 0 ? -m : m;
                        byte rr = (byte)Math.Clamp(c[s, 0] + delta, 0, 255);
                        byte gg = (byte)Math.Clamp(c[s, 1] + delta, 0, 255);
                        byte bb = (byte)Math.Clamp(c[s, 2] + delta, 0, 255);
                        buf[writeOrder[i]] = (uint)(bb | (gg << 8) | (rr << 16) | (255 << 24));
                    }
                    CopyBlock4x4ToImage(bx, by, w, h, buf, image);
                }
            }
            return image;
        }
    }
}
