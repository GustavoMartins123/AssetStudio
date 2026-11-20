using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AssetStudio.Avalonia;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;
using Xunit;

namespace AssetStudio.Tests
{
    public class TexturePreviewAndSimdTests
    {
        private static readonly object s_avaloniaInitLock = new();
        private static bool s_avaloniaInitialized;

        private static void EnsureAvaloniaSkiaInitialized()
        {
            if (s_avaloniaInitialized)
            {
                return;
            }

            lock (s_avaloniaInitLock)
            {
                if (!s_avaloniaInitialized)
                {
                    SkiaPlatform.Initialize();
                    s_avaloniaInitialized = true;
                }
            }
        }

        public static IEnumerable<object[]> OddAndBoundaryPixelCounts()
        {
            int[] counts = { 1, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 129 };
            foreach (int count in counts)
            {
                yield return new object[] { count };
            }
        }

        [Theory]
        [MemberData(nameof(OddAndBoundaryPixelCounts))]
        public void SwizzleHelpers_OutOfPlaceAndInPlace_MatchScalarReferenceAcrossOddAndSimdSizes(int pixelCount)
        {
            var src = new byte[pixelCount * 4];
            var rng = new Random(0x51A0 + pixelCount);
            rng.NextBytes(src);

            var expectedRgbaBgra = ScalarSwizzle(src, 2, 1, 0, 3);
            var expectedArgbBgra = ScalarSwizzle(src, 3, 2, 1, 0);
            var expectedAbgrBgra = ScalarSwizzle(src, 1, 2, 3, 0);
            var expectedBgraAbgr = ScalarSwizzle(src, 3, 0, 1, 2);

            // Out-of-place
            var dstRgbaBgra = new byte[src.Length];
            var dstArgbBgra = new byte[src.Length];
            var dstAbgrBgra = new byte[src.Length];
            var dstBgraAbgr = new byte[src.Length];

            Texture2DConverter.SwizzleRgbaBgra(src, dstRgbaBgra);
            Texture2DConverter.SwizzleArgbToBgra(src, dstArgbBgra);
            Texture2DConverter.SwizzleAbgrToBgra(src, dstAbgrBgra);
            Texture2DConverter.SwizzleBgraToAbgr(src, dstBgraAbgr);

            Assert.Equal(expectedRgbaBgra, dstRgbaBgra);
            Assert.Equal(expectedArgbBgra, dstArgbBgra);
            Assert.Equal(expectedAbgrBgra, dstAbgrBgra);
            Assert.Equal(expectedBgraAbgr, dstBgraAbgr);

            // In-place & round-trip involutions
            var inPlaceRgbaBgra = (byte[])src.Clone();
            Texture2DConverter.SwizzleRgbaBgra(inPlaceRgbaBgra, inPlaceRgbaBgra);
            Assert.Equal(expectedRgbaBgra, inPlaceRgbaBgra);
            Texture2DConverter.SwizzleRgbaBgra(inPlaceRgbaBgra, inPlaceRgbaBgra);
            Assert.Equal(src, inPlaceRgbaBgra);

            var inPlaceArgbBgra = (byte[])src.Clone();
            Texture2DConverter.SwizzleArgbToBgra(inPlaceArgbBgra, inPlaceArgbBgra);
            Assert.Equal(expectedArgbBgra, inPlaceArgbBgra);

            // Inverse check: ABGR -> BGRA -> ABGR
            var roundTripAbgr = new byte[src.Length];
            Texture2DConverter.SwizzleBgraToAbgr(dstAbgrBgra, roundTripAbgr);
            Assert.Equal(src, roundTripAbgr);
        }

        [Fact]
        public void SwizzleHelpers_OverlappingSlicesWithOffset_DoNotCorruptData()
        {
            const int pixelCount = 35;
            var buffer = new byte[(pixelCount + 2) * 4];
            new Random(42).NextBytes(buffer);

            var expected = ScalarSwizzle(buffer.AsSpan(0, pixelCount * 4), 2, 1, 0, 3);
            Texture2DConverter.SwizzleRgbaBgra(buffer.AsSpan(0, pixelCount * 4), buffer.AsSpan(4, pixelCount * 4));

            Assert.Equal(expected, buffer.AsSpan(4, pixelCount * 4).ToArray());
        }

        [Theory]
        [MemberData(nameof(OddAndBoundaryPixelCounts))]
        public void Decode4444_ArgbAndRgba_MatchReferenceAcrossAllBoundaryNibblesAndSizes(int pixelCount)
        {
            ushort[] boundaryNibbles = { 0x0, 0x1, 0x5, 0x8, 0xA, 0xE, 0xF };
            var src = new byte[pixelCount * 2];
            for (int i = 0; i < pixelCount; i++)
            {
                ushort n0 = boundaryNibbles[(i + 0) % boundaryNibbles.Length];
                ushort n1 = boundaryNibbles[(i + 1) % boundaryNibbles.Length];
                ushort n2 = boundaryNibbles[(i + 2) % boundaryNibbles.Length];
                ushort n3 = boundaryNibbles[(i + 3) % boundaryNibbles.Length];
                ushort packed = (ushort)(n0 | (n1 << 4) | (n2 << 8) | (n3 << 12));
                BinaryPrimitives.WriteUInt16LittleEndian(src.AsSpan(i * 2, 2), packed);
            }

            var expectedArgb = ScalarDecodeArgb4444(src, pixelCount);
            var expectedRgba = ScalarDecodeRgba4444(src, pixelCount);

            var actualArgb = new byte[pixelCount * 4];
            var actualRgba = new byte[pixelCount * 4];

            Texture2DConverter.DecodeArgb4444(src, actualArgb, pixelCount);
            Texture2DConverter.DecodeRgba4444(src, actualRgba, pixelCount);

            Assert.Equal(expectedArgb, actualArgb);
            Assert.Equal(expectedRgba, actualRgba);

            // Verify through Texture2DConverter.DecodeTexture2D as well
            var texArgb = CreateSyntheticTexture(pixelCount, 1, TextureFormat.ARGB4444, src, pathId: 100 + pixelCount);
            var texRgba = CreateSyntheticTexture(pixelCount, 1, TextureFormat.RGBA4444, src, pathId: 200 + pixelCount);

            var convArgbOut = new byte[pixelCount * 4];
            var convRgbaOut = new byte[pixelCount * 4];
            Assert.True(new Texture2DConverter(texArgb).DecodeTexture2D(convArgbOut));
            Assert.True(new Texture2DConverter(texRgba).DecodeTexture2D(convRgbaOut));

            Assert.Equal(expectedArgb, convArgbOut);
            Assert.Equal(expectedRgba, convRgbaOut);
        }

        [Fact]
        public void Decode4444_AllNibbleValues0To15_ExpandToExact8BitMultiples()
        {
            byte[] src = new byte[16 * 2];
            for (int n = 0; n < 16; n++)
            {
                ushort packed = (ushort)(n | (n << 4) | (n << 8) | (n << 12));
                BinaryPrimitives.WriteUInt16LittleEndian(src.AsSpan(n * 2, 2), packed);
            }

            byte[] dstArgb = new byte[16 * 4];
            byte[] dstRgba = new byte[16 * 4];
            Texture2DConverter.DecodeArgb4444(src, dstArgb, 16);
            Texture2DConverter.DecodeRgba4444(src, dstRgba, 16);

            for (int n = 0; n < 16; n++)
            {
                byte expected = (byte)((n << 4) | n);
                for (int c = 0; c < 4; c++)
                {
                    Assert.Equal(expected, dstArgb[n * 4 + c]);
                    Assert.Equal(expected, dstRgba[n * 4 + c]);
                }
            }
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 5)]
        [InlineData(7, 7)]
        [InlineData(15, 3)]
        [InlineData(16, 4)]
        [InlineData(17, 9)]
        [InlineData(63, 2)]
        [InlineData(64, 3)]
        [InlineData(65, 5)]
        public void Texture2DExtensions_ConvertToImage_DirectDecodeAndSimdFlipMatchExpectedBgraAcrossFormats(int width, int height)
        {
            int pixelCount = width * height;
            var raw = new byte[pixelCount * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    raw[idx + 0] = (byte)((x * 17 + y * 3) & 0xFF);
                    raw[idx + 1] = (byte)((x * 5 + y * 29) & 0xFF);
                    raw[idx + 2] = (byte)((x * 41 + y * 7) & 0xFF);
                    raw[idx + 3] = (x == 0 && y == 0) ? (byte)0 : (byte)((x * 13 + y * 19) | 1);
                }
            }

            foreach (var format in new[] { TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.BGRA32 })
            {
                var tex = CreateSyntheticTexture(width, height, format, raw, pathId: 5000 + (int)format);
                var expectedUnflipped = format switch
                {
                    TextureFormat.RGBA32 => ScalarSwizzle(raw, 2, 1, 0, 3),
                    TextureFormat.ARGB32 => ScalarSwizzle(raw, 3, 2, 1, 0),
                    TextureFormat.BGRA32 => (byte[])raw.Clone(),
                    _ => throw new InvalidOperationException()
                };

                using var unflipped = tex.ConvertToImage(flip: false);
                Assert.NotNull(unflipped);
                Assert.Equal(SKColorType.Bgra8888, unflipped!.ColorType);
                Assert.Equal(SKAlphaType.Unpremul, unflipped.AlphaType);

                var actualUnflipped = new byte[pixelCount * 4];
                unflipped.CopyBgraPixelsTo(actualUnflipped);
                Assert.Equal(expectedUnflipped, actualUnflipped);

                var expectedFlipped = FlipRowsCopy(expectedUnflipped, width * 4, height);
                using var flipped = tex.ConvertToImage(flip: true);
                Assert.NotNull(flipped);

                var actualFlipped = new byte[pixelCount * 4];
                flipped!.CopyBgraPixelsTo(actualFlipped);
                Assert.Equal(expectedFlipped, actualFlipped);
            }
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(5, 7)]
        [InlineData(13, 9)]
        [InlineData(16, 16)]
        public void Texture2DConverter_And_ConvertToImage_DirectSpanNativeDecode_Dxt3AndCompressedFormats_MatchReference(int width, int height)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            int pixelBytes = width * height * 4;

            var data8 = new byte[blocksX * blocksY * 8];
            var data16 = new byte[blocksX * blocksY * 16];
            var rng = new Random(7700 + width * 31 + height);
            rng.NextBytes(data8);
            rng.NextBytes(data16);

            // Force valid ASTC 4x4 LDR header on each 16-byte block for ASTC_RGBA_4x4
            var astc16 = (byte[])data16.Clone();
            for (int i = 0; i < astc16.Length; i += 16)
            {
                astc16[i] = 0x45;
                astc16[i + 1] = 0x00;
            }

            // 1) DXT3 parity against scalar reference (Array, Span, ConvertToImage unflipped & flipped, and Xbox360 endian swap)
            var expectedDxt3 = ScalarDecodeDxt3(data16, width, height);
            var texDxt3 = CreateSyntheticTexture(width, height, TextureFormat.DXT3, data16, pathId: 6100 + width);
            var dxt3Converter = new Texture2DConverter(texDxt3);
            Assert.True(dxt3Converter.SupportsDirectSpanDecode);

            var actualDxt3Array = new byte[pixelBytes];
            var actualDxt3Span = new byte[pixelBytes];
            Assert.True(dxt3Converter.DecodeTexture2D(actualDxt3Array));
            Assert.True(dxt3Converter.DecodeTexture2D(actualDxt3Span.AsSpan()));
            Assert.Equal(expectedDxt3, actualDxt3Array);
            Assert.Equal(expectedDxt3, actualDxt3Span);

            using (var dxt3Img = texDxt3.ConvertToImage(flip: false))
            {
                Assert.NotNull(dxt3Img);
                var imgBytes = new byte[pixelBytes];
                dxt3Img!.CopyBgraPixelsTo(imgBytes);
                Assert.Equal(expectedDxt3, imgBytes);
            }

            using (var dxt3ImgFlipped = texDxt3.ConvertToImage(flip: true))
            {
                Assert.NotNull(dxt3ImgFlipped);
                var imgBytesFlipped = new byte[pixelBytes];
                dxt3ImgFlipped!.CopyBgraPixelsTo(imgBytesFlipped);
                Assert.Equal(FlipRowsCopy(expectedDxt3, width * 4, height), imgBytesFlipped);
            }

            // Verify Xbox360 byte-swap path on DXT3
            var xboxDxt3Bytes = (byte[])data16.Clone();
            var xboxWords = MemoryMarshal.Cast<byte, ushort>(xboxDxt3Bytes.AsSpan());
            for (int i = 0; i < xboxWords.Length; i++)
            {
                xboxWords[i] = BinaryPrimitives.ReverseEndianness(xboxWords[i]);
            }
            var texDxt3Xbox = CreateSyntheticTexture(width, height, TextureFormat.DXT3, xboxDxt3Bytes, pathId: 6200 + width);
            texDxt3Xbox.platform = BuildTarget.XBOX360;
            var actualXboxOut = new byte[pixelBytes];
            Assert.True(new Texture2DConverter(texDxt3Xbox).DecodeTexture2D(actualXboxOut.AsSpan()));
            Assert.Equal(expectedDxt3, actualXboxOut);

            // 2) Direct-span vs byte[] vs ConvertToImage parity across all native compressed families
            var compressedCases = new (TextureFormat Format, byte[] Raw)[]
            {
                (TextureFormat.DXT1, data8),
                (TextureFormat.DXT3, data16),
                (TextureFormat.DXT5, data16),
                (TextureFormat.BC4, data8),
                (TextureFormat.BC5, data16),
                (TextureFormat.BC7, data16),
                (TextureFormat.ETC_RGB4, data8),
                (TextureFormat.ETC2_RGB, data8),
                (TextureFormat.ETC2_RGBA8, data16),
                (TextureFormat.ASTC_RGBA_4x4, astc16),
            };

            foreach (var (format, rawBytes) in compressedCases)
            {
                var tex = CreateSyntheticTexture(width, height, format, rawBytes, pathId: 6300 + (int)format);
                var converter = new Texture2DConverter(tex);
                Assert.True(converter.SupportsDirectSpanDecode);

                var outArray = new byte[pixelBytes];
                var outSpan = new byte[pixelBytes];
                Assert.True(converter.DecodeTexture2D(outArray));
                Assert.True(converter.DecodeTexture2D(outSpan.AsSpan()));
                Assert.Equal(outArray, outSpan);

                using var bitmap = tex.ConvertToImage(flip: false);
                Assert.NotNull(bitmap);
                var outBitmap = new byte[pixelBytes];
                bitmap!.CopyBgraPixelsTo(outBitmap);
                Assert.Equal(outArray, outBitmap);
            }
        }

        [Fact]
        public void ImageExtensions_FlipVerticalInPlace_WithPaddedRowBytes_PreservesPaddingAndFlipsRows()
        {
            const int width = 19; // 76 bytes/row -> exercises Vector256 + Vector128 + ulong + byte tail
            const int height = 7;
            const int rowLength = width * 4;
            const int rowBytes = rowLength + 12; // padded stride

            var buffer = new byte[height * rowBytes];
            Array.Fill(buffer, (byte)0xEE);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < rowLength; x++)
                {
                    buffer[y * rowBytes + x] = (byte)((y * 37 + x * 3) & 0xFF);
                }
            }

            var expected = (byte[])buffer.Clone();
            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y;
                Array.Copy(buffer, srcY * rowBytes, expected, y * rowBytes, rowLength);
            }

            ImageExtensions.FlipVerticalInPlace(buffer, rowBytes, rowLength, height);
            Assert.Equal(expected, buffer);
        }

        [Fact]
        public void MainWindow_CreateWriteableBitmapFromSKBitmap_PreservesBgraUnpremulAndConvertsNonBgra()
        {
            EnsureAvaloniaSkiaInitialized();

            const int width = 7;
            const int height = 5;
            var bgra = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                bgra[i * 4 + 0] = (byte)((i * 11 + 5) & 0xFF);  // B
                bgra[i * 4 + 1] = (byte)((i * 23 + 17) & 0xFF); // G
                bgra[i * 4 + 2] = (byte)((i * 31 + 29) & 0xFF); // R
                bgra[i * 4 + 3] = i == 0 ? (byte)0 : (byte)((i * 19 + 41) & 0xFF); // A=0 at pixel 0
            }

            // 1) Direct Bgra8888 Unpremul -> WriteableBitmap
            using (var srcBgra = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height))
            using (var wb = MainWindow.CreateWriteableBitmapFromSKBitmap(srcBgra))
            {
                Assert.Equal(new PixelSize(width, height), wb.PixelSize);
                Assert.Equal(PixelFormat.Bgra8888, wb.Format);
                Assert.Equal(AlphaFormat.Unpremul, wb.AlphaFormat);

                var roundTripped = ReadWriteableBitmapPixels(wb, width, height);
                Assert.Equal(bgra, roundTripped);
            }

            // 2) Non-Bgra8888 fallback (Rgba8888 Unpremul) -> converts to Bgra8888 and preserves A=0 RGB
            var rgbaInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using (var srcRgba = new SKBitmap(rgbaInfo))
            {
                Texture2DConverter.SwizzleRgbaBgra(bgra, srcRgba.GetWritablePixelSpan());
                GC.KeepAlive(srcRgba);

                using var wb = MainWindow.CreateWriteableBitmapFromSKBitmap(srcRgba);
                var roundTripped = ReadWriteableBitmapPixels(wb, width, height);
                Assert.Equal(bgra, roundTripped);
            }
        }

        [Fact]
        public void MainWindow_CopyBgraBitmapToFramebuffer_RespectsDestinationStrideAndLeavesPaddingUntouched()
        {
            const int width = 5;
            const int height = 3;
            int bytesPerRow = width * 4;
            int dstRowBytes = bytesPerRow + 16;

            var srcBgra = new byte[width * height * 4];
            for (int i = 0; i < srcBgra.Length; i++)
            {
                srcBgra[i] = (byte)((i * 7 + 13) & 0xFF);
            }

            using var src = ImageExtensions.CreateBgraUnpremulBitmap(srcBgra, width, height);
            var dstBuffer = new byte[height * dstRowBytes];
            Array.Fill(dstBuffer, (byte)0xA5);

            var handle = GCHandle.Alloc(dstBuffer, GCHandleType.Pinned);
            try
            {
                MainWindow.CopyBgraBitmapToFramebuffer(src, handle.AddrOfPinnedObject(), dstRowBytes);
            }
            finally
            {
                handle.Free();
            }

            for (int y = 0; y < height; y++)
            {
                Assert.Equal(
                    srcBgra.AsSpan(y * bytesPerRow, bytesPerRow).ToArray(),
                    dstBuffer.AsSpan(y * dstRowBytes, bytesPerRow).ToArray());

                for (int p = bytesPerRow; p < dstRowBytes; p++)
                {
                    Assert.Equal((byte)0xA5, dstBuffer[y * dstRowBytes + p]);
                }
            }
        }

        [Fact]
        public void TextureGLPreviewControl_CapabilitiesAndFormatMapping_GateEtc2AstcBcnAndBgraCorrectly()
        {
            // Desktop GL 3.3 without ARB_ES3_compatibility / ASTC
            var desktopGl33 = TextureGLPreviewControl.GlCapabilities.FromVersionAndExtensions(
                isOpenGles: false,
                majorVersion: 3,
                minorVersion: 3,
                extensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GL_EXT_texture_compression_s3tc" });

            Assert.True(desktopGl33.SupportsBgraUpload);
            Assert.True(desktopGl33.SupportsS3tc);
            Assert.True(desktopGl33.SupportsRgtc);
            Assert.False(desktopGl33.SupportsBptc);
            Assert.False(desktopGl33.SupportsEtc2);
            Assert.False(desktopGl33.SupportsAstcLdr);
            Assert.False(desktopGl33.SupportsAstcHdr);

            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.DXT1, desktopGl33, out int fmt));
            Assert.Equal(0x83F0, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.DXT3, desktopGl33, out fmt));
            Assert.Equal(0x83F2, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.DXT5, desktopGl33, out fmt));
            Assert.Equal(0x83F3, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.BC4, desktopGl33, out fmt));
            Assert.Equal(0x8DBB, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.BC5, desktopGl33, out fmt));
            Assert.Equal(0x8DBD, fmt);
            Assert.False(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.BC7, desktopGl33, out _));
            Assert.False(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC2_RGB, desktopGl33, out _));
            Assert.False(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ASTC_RGBA_4x4, desktopGl33, out _));

            // Desktop GL 4.3+ enables ETC2 & BPTC, plus ASTC LDR when extension present
            var desktopGl43WithAstcLdr = TextureGLPreviewControl.GlCapabilities.FromVersionAndExtensions(
                isOpenGles: false,
                majorVersion: 4,
                minorVersion: 3,
                extensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GL_KHR_texture_compression_astc_ldr" });

            Assert.True(desktopGl43WithAstcLdr.SupportsBgraUpload);
            Assert.True(desktopGl43WithAstcLdr.SupportsBptc);
            Assert.True(desktopGl43WithAstcLdr.SupportsEtc2);
            Assert.True(desktopGl43WithAstcLdr.SupportsAstcLdr);
            Assert.False(desktopGl43WithAstcLdr.SupportsAstcHdr);

            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.BC6H, desktopGl43WithAstcLdr, out fmt));
            Assert.Equal(0x8E8F, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.BC7, desktopGl43WithAstcLdr, out fmt));
            Assert.Equal(0x8E8C, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC_RGB4, desktopGl43WithAstcLdr, out fmt));
            Assert.Equal(0x9274, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC2_RGB, desktopGl43WithAstcLdr, out fmt));
            Assert.Equal(0x9274, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC2_RGB, desktopGl43WithAstcLdr, out fmt, srgb: true));
            Assert.Equal(0x9275, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC2_RGBA1, desktopGl43WithAstcLdr, out fmt));
            Assert.Equal(0x9276, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC2_RGBA8, desktopGl43WithAstcLdr, out fmt));
            Assert.Equal(0x9278, fmt);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ETC2_RGBA8, desktopGl43WithAstcLdr, out fmt, srgb: true));
            Assert.Equal(0x9279, fmt);

            // ASTC LDR 4x4..12x12 formats (RGB + RGBA)
            var astcExpected = new (TextureFormat Format, int LinearGl, int SrgbGl)[]
            {
                (TextureFormat.ASTC_RGB_4x4, 0x93B0, 0x93D0),
                (TextureFormat.ASTC_RGBA_4x4, 0x93B0, 0x93D0),
                (TextureFormat.ASTC_RGB_5x5, 0x93B2, 0x93D2),
                (TextureFormat.ASTC_RGBA_5x5, 0x93B2, 0x93D2),
                (TextureFormat.ASTC_RGB_6x6, 0x93B4, 0x93D4),
                (TextureFormat.ASTC_RGBA_6x6, 0x93B4, 0x93D4),
                (TextureFormat.ASTC_RGB_8x8, 0x93B7, 0x93D7),
                (TextureFormat.ASTC_RGBA_8x8, 0x93B7, 0x93D7),
                (TextureFormat.ASTC_RGB_10x10, 0x93BB, 0x93DB),
                (TextureFormat.ASTC_RGBA_10x10, 0x93BB, 0x93DB),
                (TextureFormat.ASTC_RGB_12x12, 0x93BD, 0x93DD),
                (TextureFormat.ASTC_RGBA_12x12, 0x93BD, 0x93DD),
            };
            foreach (var (format, linearGl, srgbGl) in astcExpected)
            {
                Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(format, desktopGl43WithAstcLdr, out fmt, srgb: false));
                Assert.Equal(linearGl, fmt);
                Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(format, desktopGl43WithAstcLdr, out fmt, srgb: true));
                Assert.Equal(srgbGl, fmt);
            }

            // HDR ASTC requires GL_KHR_texture_compression_astc_hdr
            Assert.False(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ASTC_HDR_4x4, desktopGl43WithAstcLdr, out _));

            // OpenGL ES 3.2: GLES disables BGRA direct upload, enables ETC2 + ASTC LDR core
            var gles32 = TextureGLPreviewControl.GlCapabilities.FromVersionAndExtensions(
                isOpenGles: true,
                majorVersion: 3,
                minorVersion: 2,
                extensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GL_KHR_texture_compression_astc_hdr" });

            Assert.False(gles32.SupportsBgraUpload);
            Assert.True(gles32.SupportsEtc2);
            Assert.True(gles32.SupportsAstcLdr);
            Assert.True(gles32.SupportsAstcHdr);
            Assert.True(TextureGLPreviewControl.TryResolveCompressedInternalFormat(TextureFormat.ASTC_HDR_12x12, gles32, out fmt));
            Assert.Equal(0x93BD, fmt);
        }

        [Fact]
        public void TextureGLPreviewControl_GetMip0Size_ComputesExactBlockCountsForEtc2AndAstc()
        {
            Assert.Equal(8, TextureGLPreviewControl.GetMip0Size(TextureFormat.ETC2_RGB, 1, 1));
            Assert.Equal(16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ETC2_RGBA8, 4, 4));
            Assert.Equal(2 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ETC2_RGBA8, 5, 8));
            Assert.Equal(3 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_4x4, 9, 5));
            Assert.Equal(2 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_5x5, 6, 10));
            Assert.Equal(2 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_6x6, 7, 7));
            Assert.Equal(2 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_8x8, 9, 9));
            Assert.Equal(2 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_10x10, 11, 11));
            Assert.Equal(2 * 2 * 16, TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_12x12, 13, 13));
            Assert.Equal(13 * 7 * 3, TextureGLPreviewControl.GetMip0Size(TextureFormat.RGB24, 13, 7));
            Assert.Equal(13 * 7 * 4, TextureGLPreviewControl.GetMip0Size(TextureFormat.BGRA32, 13, 7));
        }

        private static byte[] ReadWriteableBitmapPixels(global::Avalonia.Media.Imaging.WriteableBitmap wb, int width, int height)
        {
            using var fb = wb.Lock();
            int rowBytes = width * 4;
            var result = new byte[height * rowBytes];
            for (int y = 0; y < height; y++)
            {
                IntPtr rowPtr = IntPtr.Add(fb.Address, y * fb.RowBytes);
                Marshal.Copy(rowPtr, result, y * rowBytes, rowBytes);
            }
            return result;
        }

        private static byte[] ScalarSwizzle(ReadOnlySpan<byte> src, int p0, int p1, int p2, int p3)
        {
            var dst = new byte[src.Length];
            for (int i = 0; i + 3 < src.Length; i += 4)
            {
                dst[i + 0] = src[i + p0];
                dst[i + 1] = src[i + p1];
                dst[i + 2] = src[i + p2];
                dst[i + 3] = src[i + p3];
            }
            return dst;
        }

        private static byte[] ScalarDecodeArgb4444(ReadOnlySpan<byte> src, int pixelCount)
        {
            var dst = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                ushort p = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
                byte c0 = (byte)(p & 0x000F);
                byte c1 = (byte)((p & 0x00F0) >> 4);
                byte c2 = (byte)((p & 0x0F00) >> 8);
                byte c3 = (byte)((p & 0xF000) >> 12);
                dst[i * 4 + 0] = (byte)((c0 << 4) | c0);
                dst[i * 4 + 1] = (byte)((c1 << 4) | c1);
                dst[i * 4 + 2] = (byte)((c2 << 4) | c2);
                dst[i * 4 + 3] = (byte)((c3 << 4) | c3);
            }
            return dst;
        }

        private static byte[] ScalarDecodeRgba4444(ReadOnlySpan<byte> src, int pixelCount)
        {
            var dst = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                ushort p = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
                byte c0 = (byte)((p & 0x00F0) >> 4);
                byte c1 = (byte)((p & 0x0F00) >> 8);
                byte c2 = (byte)((p & 0xF000) >> 12);
                byte c3 = (byte)(p & 0x000F);
                dst[i * 4 + 0] = (byte)((c0 << 4) | c0);
                dst[i * 4 + 1] = (byte)((c1 << 4) | c1);
                dst[i * 4 + 2] = (byte)((c2 << 4) | c2);
                dst[i * 4 + 3] = (byte)((c3 << 4) | c3);
            }
            return dst;
        }

        private static byte[] FlipRowsCopy(byte[] src, int rowBytes, int height)
        {
            var dst = new byte[src.Length];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(src, (height - 1 - y) * rowBytes, dst, y * rowBytes, rowBytes);
            }
            return dst;
        }

        private static byte[] ScalarDecodeDxt3(ReadOnlySpan<byte> imageData, int width, int height)
        {
            var buff = new byte[width * height * 4];
            int blockCountX = (width + 3) / 4;
            int blockCountY = (height + 3) / 4;
            for (int y = 0; y < blockCountY; y++)
            {
                for (int x = 0; x < blockCountX; x++)
                {
                    int blockIndex = (y * blockCountX + x) * 16;
                    ulong alphaData = BinaryPrimitives.ReadUInt64LittleEndian(imageData.Slice(blockIndex, 8));
                    ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(imageData.Slice(blockIndex + 8, 2));
                    ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(imageData.Slice(blockIndex + 10, 2));
                    uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(imageData.Slice(blockIndex + 12, 4));

                    byte r0 = (byte)(((c0 >> 8) & 0xF8) | (c0 >> 13));
                    byte g0 = (byte)(((c0 >> 3) & 0xFC) | ((c0 >> 9) & 0x03));
                    byte b0 = (byte)((c0 << 3) | ((c0 >> 2) & 0x07));

                    byte r1 = (byte)(((c1 >> 8) & 0xF8) | (c1 >> 13));
                    byte g1 = (byte)(((c1 >> 3) & 0xFC) | ((c1 >> 9) & 0x03));
                    byte b1 = (byte)((c1 << 3) | ((c1 >> 2) & 0x07));

                    byte r2 = (byte)((2 * r0 + r1) / 3);
                    byte g2 = (byte)((2 * g0 + g1) / 3);
                    byte b2 = (byte)((2 * b0 + b1) / 3);

                    byte r3 = (byte)((r0 + 2 * r1) / 3);
                    byte g3 = (byte)((g0 + 2 * g1) / 3);
                    byte b3 = (byte)((b0 + 2 * b1) / 3);

                    for (int py = 0; py < 4; py++)
                    {
                        int pixelY = y * 4 + py;
                        if (pixelY >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int pixelX = x * 4 + px;
                            if (pixelX >= width) continue;
                            int idx = py * 4 + px;
                            byte alpha4 = (byte)((alphaData >> (idx * 4)) & 0x0F);
                            byte alpha = (byte)((alpha4 << 4) | alpha4);
                            uint colorIndex = (colorIndices >> (idx * 2)) & 0x03;
                            byte r = 0, g = 0, b = 0;
                            switch (colorIndex)
                            {
                                case 0: r = r0; g = g0; b = b0; break;
                                case 1: r = r1; g = g1; b = b1; break;
                                case 2: r = r2; g = g2; b = b2; break;
                                case 3: r = r3; g = g3; b = b3; break;
                            }
                            int outIndex = (pixelY * width + pixelX) * 4;
                            buff[outIndex] = b;
                            buff[outIndex + 1] = g;
                            buff[outIndex + 2] = r;
                            buff[outIndex + 3] = alpha;
                        }
                    }
                }
            }
            return buff;
        }

        private static Texture2D CreateSyntheticTexture(int width, int height, TextureFormat format, byte[] rawBytes, long pathId)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.m_Name = $"Synthetic_{format}_{width}x{height}";
            tex.m_PathID = pathId;
            tex.version = new[] { 2022, 3, 10, 1 };
            tex.platform = BuildTarget.StandaloneWindows64;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = format;
            tex.image_data = new ResourceReader(rawBytes);
            return tex;
        }
    }
}
