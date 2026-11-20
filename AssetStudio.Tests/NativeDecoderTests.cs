using System;
using System.IO;
using Texture2DDecoder;
using Xunit;

namespace AssetStudio.Tests
{
    public class NativeDecoderTests
    {
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

        [Fact]
        public void DecodeDXT1_Valid4x4Block_ProducesExpectedBgraPixels()
        {
            if (!IsNativeDecoderAvailable())
            {
                return;
            }

            // Valid 4x4 BC1 block:
            //   c0 = 0xF800 (pure red:   R=255, G=0, B=0)   -> BGRA (0, 0, 255, 255)
            //   c1 = 0x001F (pure blue:  R=0,   G=0, B=255) -> BGRA (255, 0, 0, 255)
            //   c0 > c1 => 4-color mode:
            //     c2 = (2*c0 + c1)/3 = (170, 0, 85)         -> BGRA (85, 0, 170, 255)
            //     c3 = (c0 + 2*c1)/3 = (85,  0, 170)        -> BGRA (170, 0, 85, 255)
            //   Row 0 selectors: 0, 1, 2, 3 -> 0xE4
            //   Row 1 selectors: 0, 0, 0, 0 -> 0x00
            //   Row 2 selectors: 1, 1, 1, 1 -> 0x55
            //   Row 3 selectors: 2, 2, 2, 2 -> 0xAA
            byte[] block = { 0x00, 0xF8, 0x1F, 0x00, 0xE4, 0x00, 0x55, 0xAA };
            var output = new byte[4 * 4 * 4];

            bool ok = TextureDecoder.DecodeDXT1(block, 4, 4, output);

            Assert.True(ok);

            // Row 0: c0, c1, c2, c3
            AssertPixelBgra(output, pixelIndex: 0, b: 0, g: 0, r: 255, a: 255);
            AssertPixelBgra(output, pixelIndex: 1, b: 255, g: 0, r: 0, a: 255);
            AssertPixelBgra(output, pixelIndex: 2, b: 85, g: 0, r: 170, a: 255);
            AssertPixelBgra(output, pixelIndex: 3, b: 170, g: 0, r: 85, a: 255);

            // Row 1: all c0 (red)
            for (int i = 4; i < 8; i++)
            {
                AssertPixelBgra(output, i, b: 0, g: 0, r: 255, a: 255);
            }

            // Row 2: all c1 (blue)
            for (int i = 8; i < 12; i++)
            {
                AssertPixelBgra(output, i, b: 255, g: 0, r: 0, a: 255);
            }

            // Row 3: all c2
            for (int i = 12; i < 16; i++)
            {
                AssertPixelBgra(output, i, b: 85, g: 0, r: 170, a: 255);
            }
        }

        [Fact]
        public void DecodeASTC_CraftedMaliciousBlocks_DoNotCrashAndReturnAstcErrorColor_RegressionSEC03()
        {
            if (!IsNativeDecoderAvailable())
            {
                return;
            }

            // 1) All-0x00 block (reserved low-nibble block mode) -> error color (255, 0, 255, 255)
            AssertAstcBlockReturnsErrorColor(new byte[16], blockWidth: 4, blockHeight: 4);
            AssertAstcBlockReturnsErrorColor(new byte[16], blockWidth: 12, blockHeight: 12);

            // 2) All-0xFF block (dual_plane=1, part_num=4) -> error color (255, 0, 255, 255)
            var allFF = new byte[16];
            Array.Fill(allFF, (byte)0xFF);
            AssertAstcBlockReturnsErrorColor(allFF, blockWidth: 4, blockHeight: 4);
            AssertAstcBlockReturnsErrorColor(allFF, blockWidth: 12, blockHeight: 12);

            // 3) Explicit dual_plane=1 + part_num=4 invalid combination
            var dualPlaneFourParts = new byte[16];
            dualPlaneFourParts[0] = 0x25;
            dualPlaneFourParts[1] = 0x1C; // dual_plane=1 (bit 2), part_num=4 (bits 3..4 == 3)
            AssertAstcBlockReturnsErrorColor(dualPlaneFourParts, blockWidth: 4, blockHeight: 4);

            // 4) Crafted weight_num = 120 (> 64) on 12x12 block:
            //    buf[0]=0x64 (low2=0, weight_range=2, height=5), buf[1]=0x04 (width=12, dual_plane=1, part_num=1)
            //    => weight_num = 12 * 5 * 2 = 120
            var weightNum120 = new byte[16];
            weightNum120[0] = 0x64;
            weightNum120[1] = 0x04;
            AssertAstcBlockReturnsErrorColor(weightNum120, blockWidth: 12, blockHeight: 12);

            // 5) Crafted weight_num = 120 AND weight_range = 15 (5 bits/weight => weight_bits = 600) on 12x12 block
            var weightBits600 = new byte[16];
            weightBits600[0] = 0x7C;
            weightBits600[1] = 0x06;
            AssertAstcBlockReturnsErrorColor(weightBits600, blockWidth: 12, blockHeight: 12);

            // 6) Crafted weight_num = 40 (<= 64) AND weight_range = 15 => weight_bits = 200 (> 96) on 12x12 block
            var weightBits200 = new byte[16];
            weightBits200[0] = 0x77;
            weightBits200[1] = 0x02;
            AssertAstcBlockReturnsErrorColor(weightBits200, blockWidth: 12, blockHeight: 12);

            // 7) Valid void-extent constant color ASTC block verifies decoder produces exact color (not always error magenta)
            var voidExtent = new byte[16];
            voidExtent[0] = 0xFC;
            voidExtent[1] = 0xFD; // UNORM16 constant color
            voidExtent[9] = 0x11;  // R
            voidExtent[11] = 0x22; // G
            voidExtent[13] = 0x33; // B
            voidExtent[15] = 0xEE; // A
            var validOutput = new byte[4 * 4 * 4];
            Assert.True(TextureDecoder.DecodeASTC(voidExtent, 4, 4, 4, 4, validOutput));
            for (int i = 0; i < 16; i++)
            {
                AssertPixelBgra(validOutput, i, b: 0x33, g: 0x22, r: 0x11, a: 0xEE);
            }

            // 8) Deterministic fuzz sweep on 12x12 ASTC blocks — must complete without crash or OOB
            var rng = new Random(1337);
            var fuzzBlock = new byte[16];
            var fuzzOutput = new byte[12 * 12 * 4];
            for (int i = 0; i < 100; i++)
            {
                rng.NextBytes(fuzzBlock);
                Assert.True(TextureDecoder.DecodeASTC(fuzzBlock, 12, 12, 12, 12, fuzzOutput));
            }
        }

        [Fact]
        public void DecodeDXT1AndDecodeASTC_WithUndersizedOrInvalidBuffers_ReturnFalseSafely()
        {
            if (!IsNativeDecoderAvailable())
            {
                return;
            }

            // DXT1 undersized data / output / invalid dimensions
            Assert.False(TextureDecoder.DecodeDXT1(new byte[7], 4, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeDXT1(new byte[8], 4, 4, new byte[63]));
            Assert.False(TextureDecoder.DecodeDXT1(null!, 4, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeDXT1(new byte[8], 4, 4, null!));
            Assert.False(TextureDecoder.DecodeDXT1(new byte[8], 0, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeDXT1(new byte[8], 4, -4, new byte[64]));

            // ASTC undersized data / output / invalid block dimensions
            Assert.False(TextureDecoder.DecodeASTC(new byte[15], 4, 4, 4, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeASTC(new byte[16], 4, 4, 4, 4, new byte[63]));
            Assert.False(TextureDecoder.DecodeASTC(null!, 4, 4, 4, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeASTC(new byte[16], 4, 4, 4, 4, null!));
            Assert.False(TextureDecoder.DecodeASTC(new byte[16], 4, 4, 1, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeASTC(new byte[16], 4, 4, 13, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeASTC(new byte[16], 0, 4, 4, 4, new byte[64]));
            Assert.False(TextureDecoder.DecodeASTC(new byte[16], 4, 4, 4, 0, new byte[64]));
        }

        private static void AssertAstcBlockReturnsErrorColor(byte[] block16, int blockWidth, int blockHeight)
        {
            var output = new byte[blockWidth * blockHeight * 4];
            bool ok = TextureDecoder.DecodeASTC(block16, blockWidth, blockHeight, blockWidth, blockHeight, output);
            Assert.True(ok);

            int pixelCount = blockWidth * blockHeight;
            for (int i = 0; i < pixelCount; i++)
            {
                // ASTC error color is magenta: R=255, G=0, B=255, A=255 -> BGRA (255, 0, 255, 255)
                AssertPixelBgra(output, i, b: 255, g: 0, r: 255, a: 255);
            }
        }

        private static void AssertPixelBgra(byte[] bgra, int pixelIndex, byte b, byte g, byte r, byte a)
        {
            int offset = pixelIndex * 4;
            Assert.Equal(b, bgra[offset]);
            Assert.Equal(g, bgra[offset + 1]);
            Assert.Equal(r, bgra[offset + 2]);
            Assert.Equal(a, bgra[offset + 3]);
        }
    }
}
