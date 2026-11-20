using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace AssetStudio.Tests
{
    public class ImagingTests
    {
        [Fact]
        public void ImageExtensions_Png_RoundTripsAndPreservesUnpremultipliedAlpha()
        {
            const int width = 4;
            const int height = 2;
            byte[] bgra =
            {
                // Row 0: (A=0 with non-zero RGB), (semi-transparent), (opaque), (pure white)
                50, 100, 200, 0,
                10,  20,  30, 64,
                255, 128,  64, 255,
                255, 255, 255, 255,
                // Row 1: distinct values to verify top-down orientation
                12,  34,  56, 0,
                78,  90, 112, 128,
                134, 156, 178, 200,
                1,   2,   3, 255
            };

            using var src = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height);
            Assert.NotNull(src);
            Assert.Equal(SKColorType.Bgra8888, src.ColorType);
            Assert.Equal(SKAlphaType.Unpremul, src.AlphaType);

            using var pngStream = src.ConvertToStream(ImageFormat.Png);
            var pngBytes = pngStream.ToArray();
            Assert.True(pngBytes.Length > 8);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, pngBytes[..8]);

            using var decoded = ImageExtensions.DecodeBgraUnpremul(pngBytes);
            Assert.NotNull(decoded);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            Assert.Equal(SKColorType.Bgra8888, decoded.ColorType);
            Assert.Equal(SKAlphaType.Unpremul, decoded.AlphaType);

            var decodedPixels = new byte[width * height * 4];
            decoded.CopyBgraPixelsTo(decodedPixels);
            Assert.Equal(bgra, decodedPixels);
        }

        [Fact]
        public void ImageExtensions_Jpeg_EncodesValidDecodableJpegWithoutDarkeningZeroAlphaRgb()
        {
            const int width = 16;
            const int height = 16;
            var bgra = new byte[width * height * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 40;      // B
                bgra[i + 1] = 180; // G
                bgra[i + 2] = 220; // R
                bgra[i + 3] = 0;   // A = 0 (must NOT darken RGB in JPEG!)
            }

            using var src = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height);
            using var jpgStream = src.ConvertToStream(ImageFormat.Jpeg);
            var jpgBytes = jpgStream.ToArray();

            Assert.True(jpgBytes.Length > 4);
            Assert.Equal(0xFF, jpgBytes[0]);
            Assert.Equal(0xD8, jpgBytes[1]);
            Assert.Equal(0xFF, jpgBytes[^2]);
            Assert.Equal(0xD9, jpgBytes[^1]);

            using var decoded = ImageExtensions.DecodeBgraUnpremul(jpgBytes);
            Assert.NotNull(decoded);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);

            var decodedPixels = new byte[width * height * 4];
            decoded.CopyBgraPixelsTo(decodedPixels);
            // Center pixel should retain RGB close to (220, 180, 40) — not darkened to 0
            int center = ((height / 2) * width + (width / 2)) * 4;
            Assert.InRange(decodedPixels[center], 25, 55);      // B ~ 40
            Assert.InRange(decodedPixels[center + 1], 165, 195); // G ~ 180
            Assert.InRange(decodedPixels[center + 2], 205, 235); // R ~ 220
        }

        [Fact]
        public void ImageExtensions_Bmp_WritesStandard54ByteHeaderAndBottomUpBgra32Pixels()
        {
            const int width = 3;
            const int height = 2;
            byte[] bgra =
            {
                // Row 0 (top)
                10, 20, 30, 0,
                40, 50, 60, 128,
                70, 80, 90, 255,
                // Row 1 (bottom)
                110, 120, 130, 0,
                140, 150, 160, 64,
                170, 180, 190, 255
            };

            using var src = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height);
            using var bmpStream = src.ConvertToStream(ImageFormat.Bmp);
            var bmpBytes = bmpStream.ToArray();

            int expectedImageSize = width * height * 4;
            Assert.Equal(54 + expectedImageSize, bmpBytes.Length);
            Assert.Equal((byte)'B', bmpBytes[0]);
            Assert.Equal((byte)'M', bmpBytes[1]);
            Assert.Equal((uint)(54 + expectedImageSize), BinaryPrimitives.ReadUInt32LittleEndian(bmpBytes.AsSpan(2, 4)));
            Assert.Equal(54u, BinaryPrimitives.ReadUInt32LittleEndian(bmpBytes.AsSpan(10, 4)));
            Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(bmpBytes.AsSpan(14, 4)));
            Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.AsSpan(18, 4)));
            Assert.Equal(height, BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.AsSpan(22, 4)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bmpBytes.AsSpan(26, 2)));
            Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(bmpBytes.AsSpan(28, 2)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bmpBytes.AsSpan(30, 4)));
            Assert.Equal((uint)expectedImageSize, BinaryPrimitives.ReadUInt32LittleEndian(bmpBytes.AsSpan(34, 4)));

            // Bottom-up scanline order: row 1 comes first at offset 54, then row 0
            int rowBytes = width * 4;
            Assert.Equal(bgra.AsSpan(rowBytes, rowBytes).ToArray(), bmpBytes.AsSpan(54, rowBytes).ToArray());
            Assert.Equal(bgra.AsSpan(0, rowBytes).ToArray(), bmpBytes.AsSpan(54 + rowBytes, rowBytes).ToArray());

            var bounds = SKBitmap.DecodeBounds(bmpBytes);
            Assert.Equal(width, bounds.Width);
            Assert.Equal(height, bounds.Height);
        }

        [Fact]
        public void ImageExtensions_Tga_WritesStandard18ByteHeaderAndTopDownBgra32Pixels()
        {
            const int width = 3;
            const int height = 2;
            byte[] bgra =
            {
                // Row 0 (top)
                11, 22, 33, 0,
                44, 55, 66, 127,
                77, 88, 99, 255,
                // Row 1 (bottom)
                111, 122, 133, 0,
                144, 155, 166, 200,
                177, 188, 199, 255
            };

            using var src = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height);
            using var tgaStream = src.ConvertToStream(ImageFormat.Tga);
            var tgaBytes = tgaStream.ToArray();

            Assert.Equal(18 + bgra.Length, tgaBytes.Length);
            Assert.Equal(0, tgaBytes[0]);   // ID length
            Assert.Equal(0, tgaBytes[1]);   // Color map type
            Assert.Equal(2, tgaBytes[2]);   // Uncompressed true-color
            Assert.Equal((ushort)width, BinaryPrimitives.ReadUInt16LittleEndian(tgaBytes.AsSpan(12, 2)));
            Assert.Equal((ushort)height, BinaryPrimitives.ReadUInt16LittleEndian(tgaBytes.AsSpan(14, 2)));
            Assert.Equal(32, tgaBytes[16]); // 32bpp BGRA
            Assert.Equal(0x28, tgaBytes[17]); // Top-left origin + 8 alpha bits
            Assert.Equal(bgra, tgaBytes.AsSpan(18).ToArray());
        }

        [Fact]
        public void Texture2DExtensions_ConvertToImage_DecodesRgba32FlipsAndPreservesUnpremulAlpha()
        {
            // Unity RGBA32 byte order: [R, G, B, A]
            byte[] rgba =
            {
                // Row 0: (R=210, G=120, B=30, A=0), (R=10, G=20, B=30, A=255)
                210, 120, 30, 0,
                10,   20, 30, 255,
                // Row 1: (R=40, G=50, B=60, A=128), (R=70, G=80, B=90, A=255)
                40,   50, 60, 128,
                70,   80, 90, 255
            };

            var tex = CreateSyntheticRgba32Texture(2, 2, rgba, pathId: 1001);

            using (var unflipped = tex.ConvertToImage(flip: false))
            {
                Assert.NotNull(unflipped);
                Assert.Equal(SKColorType.Bgra8888, unflipped.ColorType);
                Assert.Equal(SKAlphaType.Unpremul, unflipped.AlphaType);

                var pixels = new byte[16];
                unflipped.CopyBgraPixelsTo(pixels);
                // Pixel (0,0) in BGRA: B=30, G=120, R=210, A=0
                Assert.Equal(new byte[] { 30, 120, 210, 0, 30, 20, 10, 255, 60, 50, 40, 128, 90, 80, 70, 255 }, pixels);
            }

            using (var flipped = tex.ConvertToImage(flip: true))
            {
                Assert.NotNull(flipped);
                var pixels = new byte[16];
                flipped.CopyBgraPixelsTo(pixels);
                // Row 1 becomes row 0, row 0 becomes row 1
                Assert.Equal(new byte[] { 60, 50, 40, 128, 90, 80, 70, 255, 30, 120, 210, 0, 30, 20, 10, 255 }, pixels);
            }

            using var pngStream = tex.ConvertToStream(ImageFormat.Png, flip: true);
            Assert.NotNull(pngStream);
            Assert.True(pngStream.Length > 8);
        }

        [Fact]
        public void SpriteHelper_CutImage_HandlesRectangleTightRotationsDownscaleAndClearAtlasCache()
        {
            var cutImageMethod = typeof(SpriteHelper).GetMethod("CutImage", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(cutImageMethod);

            // Build a 4x4 RGBA32 atlas with unique (R=x*50+10, G=y*50+20, B=x+y*10, A=(x==0&&y==0)?0:255)
            const int atlasW = 4;
            const int atlasH = 4;
            var rgba = new byte[atlasW * atlasH * 4];
            for (int y = 0; y < atlasH; y++)
            {
                for (int x = 0; x < atlasW; x++)
                {
                    int idx = (y * atlasW + x) * 4;
                    rgba[idx] = (byte)(x * 50 + 10);     // R
                    rgba[idx + 1] = (byte)(y * 50 + 20); // G
                    rgba[idx + 2] = (byte)(x + y * 10);  // B
                    rgba[idx + 3] = (x == 0 && y == 0) ? (byte)0 : (byte)255; // A=0 at (0,0)
                }
            }

            var atlas = CreateSyntheticRgba32Texture(atlasW, atlasH, rgba, pathId: 2002);
            SpriteHelper.ClearAtlasCache();

            try
            {
                // 1) Rectangle crop [0,0, 2x2] -> flipped vertically at end of CutImage
                var sprite = CreateSyntheticSprite(rectW: 2, rectH: 2, pixelsToUnits: 1f, pivot: new Vector2(0f, 0f));
                var rect2x2 = CreateRectf(0, 0, 2, 2);
                var rectSettings = CreateSpriteSettings(packed: 0, SpritePackingMode.Rectangle, SpritePackingRotation.None);

                using (var cropped = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { sprite, atlas, rect2x2, Vector2.Zero, 1f, rectSettings }))
                {
                    Assert.NotNull(cropped);
                    Assert.Equal(2, cropped!.Width);
                    Assert.Equal(2, cropped.Height);
                    Assert.Equal(SKAlphaType.Unpremul, cropped.AlphaType);

                    var px = new byte[16];
                    cropped.CopyBgraPixelsTo(px);
                    // After vertical flip: atlas (0,1) is at (0,0), atlas (0,0) with A=0 is at (0,1)
                    Assert.Equal(new byte[] { 10, 70, 10, 255 }, px[0..4]);   // atlas (0,1): B=10, G=70, R=10, A=255
                    Assert.Equal(new byte[] { 0, 20, 10, 0 }, px[8..12]);     // atlas (0,0): B=0,  G=20, R=10, A=0 (unpremul preserved!)
                }

                // 2) Rotate90 (270 CW) on non-square 4x2 crop -> produces 2x4 bitmap
                var rect4x2 = CreateRectf(0, 0, 4, 2);
                var rot90Settings = CreateSpriteSettings(packed: 1, SpritePackingMode.Rectangle, SpritePackingRotation.Rotate90);
                using (var rotated = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { sprite, atlas, rect4x2, Vector2.Zero, 1f, rot90Settings }))
                {
                    Assert.NotNull(rotated);
                    Assert.Equal(2, rotated!.Width);
                    Assert.Equal(4, rotated.Height);
                }

                // 3) FlipHorizontal + Rotate180 sanity checks
                var flipHSettings = CreateSpriteSettings(packed: 1, SpritePackingMode.Rectangle, SpritePackingRotation.FlipHorizontal);
                using (var flippedH = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { sprite, atlas, rect2x2, Vector2.Zero, 1f, flipHSettings }))
                {
                    Assert.NotNull(flippedH);
                    var px = new byte[16];
                    flippedH!.CopyBgraPixelsTo(px);
                    // Atlas (1,1) mirrored X -> (0,1), then flipped Y -> (0,0)
                    Assert.Equal(new byte[] { 11, 70, 60, 255 }, px[0..4]);
                }

                var rot180Settings = CreateSpriteSettings(packed: 1, SpritePackingMode.Rectangle, SpritePackingRotation.Rotate180);
                using (var rot180 = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { sprite, atlas, rect2x2, Vector2.Zero, 1f, rot180Settings }))
                {
                    Assert.NotNull(rot180);
                    Assert.Equal(2, rot180!.Width);
                    Assert.Equal(2, rot180.Height);
                }

                // 4) Tight packing mode polygon clipping: single triangle covering only left half of 4x4 sprite
                var tightSprite = CreateSyntheticSprite(rectW: 4, rectH: 4, pixelsToUnits: 1f, pivot: new Vector2(0f, 0f));
                tightSprite.m_RD.vertices = new[]
                {
                    CreateSpriteVertex(0f, 0f),
                    CreateSpriteVertex(2f, 0f),
                    CreateSpriteVertex(0f, 4f)
                };
                tightSprite.m_RD.indices = new ushort[] { 0, 1, 2 };
                var rect4x4 = CreateRectf(0, 0, 4, 4);
                var tightSettings = CreateSpriteSettings(packed: 0, SpritePackingMode.Tight, SpritePackingRotation.None);

                using (var tightImg = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { tightSprite, atlas, rect4x4, Vector2.Zero, 1f, tightSettings }))
                {
                    Assert.NotNull(tightImg);
                    Assert.Equal(4, tightImg!.Width);
                    Assert.Equal(4, tightImg.Height);

                    var px = new byte[4 * 4 * 4];
                    tightImg.CopyBgraPixelsTo(px);
                    // Pixel at (x=3, y=0) after flip corresponds to pre-flip (3,3), which is OUTSIDE the triangle -> cleared to 0,0,0,0
                    int outsideIdx = (0 * 4 + 3) * 4;
                    Assert.Equal(new byte[] { 0, 0, 0, 0 }, px[outsideIdx..(outsideIdx + 4)]);
                    // Pixel at (x=0, y=3) after flip corresponds to pre-flip (0,0), which is INSIDE the triangle -> keeps unpremul RGB (B=0, G=20, R=10, A=0)
                    int insideIdx = (3 * 4 + 0) * 4;
                    Assert.Equal(new byte[] { 0, 20, 10, 0 }, px[insideIdx..(insideIdx + 4)]);
                }

                // 5) Downscale multiplier = 2f (4x4 atlas -> 2x2)
                using (var downscaled = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { sprite, atlas, rect2x2, Vector2.Zero, 2f, rectSettings }))
                {
                    Assert.NotNull(downscaled);
                    Assert.Equal(2, downscaled!.Width);
                    Assert.Equal(2, downscaled.Height);
                }

                // 6) ClearAtlasCache + re-decode works cleanly
                SpriteHelper.ClearAtlasCache();
                using (var afterClear = (SKBitmap?)cutImageMethod!.Invoke(null, new object[] { sprite, atlas, rect2x2, Vector2.Zero, 1f, rectSettings }))
                {
                    Assert.NotNull(afterClear);
                    Assert.Equal(2, afterClear!.Width);
                    Assert.Equal(2, afterClear.Height);
                }
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        private static Texture2D CreateSyntheticRgba32Texture(int width, int height, byte[] rgbaBytes, long pathId)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.m_Name = "SyntheticRgba32";
            tex.m_PathID = pathId;
            tex.version = new[] { 2021, 3, 0, 1 };
            tex.platform = BuildTarget.StandaloneWindows64;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.image_data = new ResourceReader(rgbaBytes);
            return tex;
        }

        private static Sprite CreateSyntheticSprite(float rectW, float rectH, float pixelsToUnits, Vector2 pivot)
        {
            var sprite = (Sprite)RuntimeHelpers.GetUninitializedObject(typeof(Sprite));
            sprite.m_Name = "SyntheticSprite";
            sprite.m_Rect = CreateRectf(0, 0, rectW, rectH);
            sprite.m_PixelsToUnits = pixelsToUnits;
            sprite.m_Pivot = pivot;
            sprite.m_RD = (SpriteRenderData)RuntimeHelpers.GetUninitializedObject(typeof(SpriteRenderData));
            return sprite;
        }

        private static Rectf CreateRectf(float x, float y, float w, float h)
        {
            var rect = (Rectf)RuntimeHelpers.GetUninitializedObject(typeof(Rectf));
            rect.x = x;
            rect.y = y;
            rect.width = w;
            rect.height = h;
            return rect;
        }

        private static SpriteSettings CreateSpriteSettings(uint packed, SpritePackingMode mode, SpritePackingRotation rotation)
        {
            var settings = (SpriteSettings)RuntimeHelpers.GetUninitializedObject(typeof(SpriteSettings));
            settings.packed = packed;
            settings.packingMode = mode;
            settings.packingRotation = rotation;
            return settings;
        }

        private static SpriteVertex CreateSpriteVertex(float x, float y)
        {
            var v = (SpriteVertex)RuntimeHelpers.GetUninitializedObject(typeof(SpriteVertex));
            v.pos = new Vector3(x, y, 0f);
            return v;
        }
    }
}
