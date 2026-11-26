using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace AssetStudio.Tests
{
    public class Block5SpritesHdrMipmapsTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        #region TEX-06 Split RGB + Alpha Mask Recovery

        [Fact]
        public void SpriteHelper_SplitAlphaTexture_DirectAlpha8AndRgbMask_RecombinesCleanly()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                var assetsFile = CreateMinimalSerializedFile("split_alpha_direct.assets");

                // 4x4 RGB24 color texture (solid R=200, G=100, B=50)
                var rgb24 = new byte[4 * 4 * 3];
                for (int i = 0; i < 16; i++)
                {
                    rgb24[i * 3] = 200;
                    rgb24[i * 3 + 1] = 100;
                    rgb24[i * 3 + 2] = 50;
                }
                var colorTex = CreateSyntheticTexture(4, 4, TextureFormat.RGB24, new ResourceReader(rgb24), pathId: 10, assetsFile: assetsFile);
                assetsFile.AddObject(colorTex);

                // 4x4 Alpha8 texture with per-pixel alpha ramp
                var alpha8 = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    alpha8[i] = (byte)(i * 17);
                }
                var alpha8Tex = CreateSyntheticTexture(4, 4, TextureFormat.Alpha8, new ResourceReader(alpha8), pathId: 11, assetsFile: assetsFile);
                assetsFile.AddObject(alpha8Tex);

                // 4x4 RGB24 alpha mask texture where mask value is stored in R channel
                var alphaRgb = new byte[16 * 3];
                for (int i = 0; i < 16; i++)
                {
                    byte mask = (byte)(255 - i * 15);
                    alphaRgb[i * 3] = mask;
                    alphaRgb[i * 3 + 1] = mask;
                    alphaRgb[i * 3 + 2] = mask;
                }
                var alphaRgbTex = CreateSyntheticTexture(4, 4, TextureFormat.RGB24, new ResourceReader(alphaRgb), pathId: 12, assetsFile: assetsFile);
                assetsFile.AddObject(alphaRgbTex);

                // Case 1: m_RD.alphaTexture points to Alpha8
                var spriteAlpha8 = CreateLinkedSprite(assetsFile, colorTex.m_PathID, 0, 0, 4, 4, name: "BadgeAlpha8");
                spriteAlpha8.m_RD.alphaTexture = new PPtr<Texture2D>(0, alpha8Tex.m_PathID, assetsFile);

                using (var bmp = spriteAlpha8.GetImage())
                {
                    Assert.NotNull(bmp);
                    var pixels = new byte[4 * 4 * 4];
                    bmp!.CopyBgraPixelsTo(pixels);
                    // Vertical flip maps output row y to source row (3 - y)
                    for (int y = 0; y < 4; y++)
                    {
                        int srcY = 3 - y;
                        for (int x = 0; x < 4; x++)
                        {
                            int p = (y * 4 + x) * 4;
                            byte expectedAlpha = alpha8[srcY * 4 + x];
                            Assert.Equal(50, pixels[p]);
                            Assert.Equal(100, pixels[p + 1]);
                            Assert.Equal(200, pixels[p + 2]);
                            Assert.Equal(expectedAlpha, pixels[p + 3]);
                        }
                    }
                }

                // Case 2: m_RD.alphaTexture points to non-Alpha8 RGB24 mask -> reads R channel (channel 2 in BGRA)
                var spriteAlphaRgb = CreateLinkedSprite(assetsFile, colorTex.m_PathID, 0, 0, 4, 4, name: "BadgeAlphaRgb");
                spriteAlphaRgb.m_RD.alphaTexture = new PPtr<Texture2D>(0, alphaRgbTex.m_PathID, assetsFile);

                using (var bmp = spriteAlphaRgb.GetImage())
                {
                    Assert.NotNull(bmp);
                    var pixels = new byte[4 * 4 * 4];
                    bmp!.CopyBgraPixelsTo(pixels);
                    for (int y = 0; y < 4; y++)
                    {
                        int srcY = 3 - y;
                        for (int x = 0; x < 4; x++)
                        {
                            int p = (y * 4 + x) * 4;
                            byte expectedAlpha = (byte)(255 - (srcY * 4 + x) * 15);
                            Assert.Equal(expectedAlpha, pixels[p + 3]);
                        }
                    }
                }

                // Case 3: ApplyAlphaMask = false leaves opaque RGB24 alpha (255)
                using (var bmpNoMask = spriteAlphaRgb.GetImage(new SpriteExtractionOptions { ApplyAlphaMask = false }))
                {
                    Assert.NotNull(bmpNoMask);
                    var pixels = new byte[4 * 4 * 4];
                    bmpNoMask!.CopyBgraPixelsTo(pixels);
                    for (int i = 3; i < pixels.Length; i += 4)
                    {
                        Assert.Equal(255, pixels[i]);
                    }
                }

                GC.KeepAlive(colorTex);
                GC.KeepAlive(alpha8Tex);
                GC.KeepAlive(alphaRgbTex);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        [Fact]
        public void SpriteHelper_SplitAlphaTexture_FallsBackToSecondaryAlphaTexWhenAlphaTextureNull()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                var assetsFile = CreateMinimalSerializedFile("split_alpha_secondary.assets");
                var rgba = CreateSolidRgba(4, 4, r: 180, g: 90, b: 45, a: 255);
                var colorTex = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(rgba), pathId: 20, assetsFile: assetsFile);
                assetsFile.AddObject(colorTex);

                // R8 secondary mask texture with solid mask value 73
                var r8Mask = Enumerable.Repeat((byte)73, 16).ToArray();
                var maskTex = CreateSyntheticTexture(4, 4, TextureFormat.R8, new ResourceReader(r8Mask), pathId: 21, assetsFile: assetsFile);
                assetsFile.AddObject(maskTex);

                var sprite = CreateLinkedSprite(assetsFile, colorTex.m_PathID, 0, 0, 4, 4, name: "SecondaryAlphaSprite");
                sprite.m_RD.alphaTexture = null;
                sprite.m_RD.secondaryTextures = new[]
                {
                    CreateSecondarySpriteTexture("_NormalMap", new PPtr<Texture2D>(0, 999, assetsFile)),
                    CreateSecondarySpriteTexture("_AlphaTex", new PPtr<Texture2D>(0, maskTex.m_PathID, assetsFile))
                };

                using var bmp = sprite.GetImage();
                Assert.NotNull(bmp);
                var pixels = new byte[4 * 4 * 4];
                bmp!.CopyBgraPixelsTo(pixels);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    Assert.Equal(45, pixels[i]);
                    Assert.Equal(90, pixels[i + 1]);
                    Assert.Equal(180, pixels[i + 2]);
                    Assert.Equal(73, pixels[i + 3]);
                }

                GC.KeepAlive(colorTex);
                GC.KeepAlive(maskTex);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        #endregion

        #region TEX-07 Original Rect Padding & 9-Slice Overlay + TEX-05 Tight/Rotated/Malformed Hardening

        [Fact]
        public void SpriteHelper_PreserveOriginalRect_PadsCanvasWithBottomAlignedOffset_AndDrawsNineSliceOverlay()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                var assetsFile = CreateMinimalSerializedFile("preserve_rect.assets");
                var rgba = CreateSolidRgba(8, 8, r: 40, g: 160, b: 220, a: 255);
                var tex = CreateSyntheticTexture(8, 8, TextureFormat.RGBA32, new ResourceReader(rgba), pathId: 30, assetsFile: assetsFile);
                assetsFile.AddObject(tex);

                // Original full rect is 16x12, trimmed sprite is 4x4 positioned at offset (3, 2) from bottom-left.
                // In top-down 16x12 canvas: X in [3..7), Y in [12 - 2 - 4 .. 12 - 2) = [6..10).
                var sprite = CreateLinkedSprite(assetsFile, tex.m_PathID, 2, 2, 4, 4, name: "PaddedButton");
                sprite.m_Rect = CreateRectf(0, 0, 16, 12);
                sprite.m_RD.textureRectOffset = new Vector2(3f, 2f);
                sprite.m_Border = new Vector4(4f, 3f, 4f, 3f); // left=4, bottom=3, right=4, top=3
                sprite.m_Pivot = new Vector2(0.5f, 0.5f);

                using (var padded = sprite.GetImage(new SpriteExtractionOptions { PreserveOriginalRect = true }))
                {
                    Assert.NotNull(padded);
                    Assert.Equal(16, padded!.Width);
                    Assert.Equal(12, padded.Height);

                    var pixels = new byte[16 * 12 * 4];
                    padded.CopyBgraPixelsTo(pixels);

                    for (int y = 0; y < 12; y++)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            int idx = (y * 16 + x) * 4;
                            bool insideSprite = x >= 3 && x < 7 && y >= 6 && y < 10;
                            if (insideSprite)
                            {
                                Assert.Equal(220, pixels[idx]);
                                Assert.Equal(160, pixels[idx + 1]);
                                Assert.Equal(40, pixels[idx + 2]);
                                Assert.Equal(255, pixels[idx + 3]);
                            }
                            else
                            {
                                Assert.Equal(0, pixels[idx + 3]);
                            }
                        }
                    }
                }

                // Overlay draws 9-slice guides & pivot crosshair without crashing (and modifies pixels)
                using (var withOverlay = sprite.GetImage(new SpriteExtractionOptions
                {
                    PreserveOriginalRect = true,
                    DrawBoundsAndNineSliceOverlay = true
                }))
                {
                    Assert.NotNull(withOverlay);
                    Assert.Equal(16, withOverlay!.Width);
                    Assert.Equal(12, withOverlay.Height);

                    // Pivot center (8, 6) should have non-transparent crosshair pixel
                    var pivotPixel = withOverlay.GetPixel(8, 6);
                    Assert.True(pivotPixel.Alpha > 0);
                }

                // Tiny 1x1 sprite must not crash when drawing bounds & 9-slice overlay
                using (var tinyBmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
                {
                    SpriteHelper.DrawBoundsAndNineSliceOverlay(tinyBmp, sprite, preserveOriginalRect: true);
                    Assert.Equal(1, tinyBmp.Width);
                }

                GC.KeepAlive(tex);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        [Fact]
        public void SpriteHelper_TightRotatedAndMalformedRects_HandleGracefullyWithoutThrowing()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                var assetsFile = CreateMinimalSerializedFile("tight_rotated.assets");
                var rgba = new byte[4 * 4 * 4];
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int i = (y * 4 + x) * 4;
                        rgba[i] = (byte)(x * 60 + 10);
                        rgba[i + 1] = (byte)(y * 60 + 10);
                        rgba[i + 2] = 120;
                        rgba[i + 3] = 255;
                    }
                }
                var tex = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(rgba), pathId: 40, assetsFile: assetsFile);
                assetsFile.AddObject(tex);

                // Rotated packed sprite (Rotate90 swaps 4x2 -> 2x4)
                var rotatedSprite = CreateLinkedSprite(assetsFile, tex.m_PathID, 0, 0, 4, 2, name: "RotatedSprite");
                rotatedSprite.m_RD.settingsRaw = CreateSpriteSettings(1, SpritePackingMode.Rectangle, SpritePackingRotation.Rotate90);
                using (var rotBmp = rotatedSprite.GetImage())
                {
                    Assert.NotNull(rotBmp);
                    Assert.Equal(2, rotBmp!.Width);
                    Assert.Equal(4, rotBmp.Height);
                }

                // Tight sprite with valid triangle masks outside pixels to transparent and preserves inside pixels
                var validTightSprite = CreateLinkedSprite(assetsFile, tex.m_PathID, 0, 0, 4, 4, name: "ValidTight");
                validTightSprite.m_Pivot = new Vector2(0f, 0f);
                validTightSprite.m_PixelsToUnits = 1f;
                validTightSprite.m_RD.settingsRaw = CreateSpriteSettings(1, SpritePackingMode.Tight, SpritePackingRotation.None);
                validTightSprite.m_RD.vertices = new[]
                {
                    CreateSpriteVertex(0f, 0f),
                    CreateSpriteVertex(4f, 0f),
                    CreateSpriteVertex(0f, 4f)
                };
                validTightSprite.m_RD.indices = new ushort[] { 0, 1, 2 };
                using (var validTightBmp = validTightSprite.GetImage())
                {
                    Assert.NotNull(validTightBmp);
                    Assert.Equal(4, validTightBmp!.Width);
                    Assert.Equal(4, validTightBmp.Height);
                    // Bottom-left corner in Unity space (row 3 in flipped Skia space, x=0) is inside the triangle
                    Assert.Equal(255, validTightBmp.GetPixel(0, 3).Alpha);
                    // Top-right corner in Unity space (row 0 in flipped Skia space, x=3) is outside the triangle
                    Assert.Equal(0, validTightBmp.GetPixel(3, 0).Alpha);
                }

                // Tight sprite with NaN triangle vertex degrades gracefully instead of throwing or wiping sprite
                var tightSprite = CreateLinkedSprite(assetsFile, tex.m_PathID, 0, 0, 4, 4, name: "MalformedTight");
                tightSprite.m_RD.settingsRaw = CreateSpriteSettings(1, SpritePackingMode.Tight, SpritePackingRotation.None);
                tightSprite.m_RD.vertices = new[]
                {
                    CreateSpriteVertex(float.NaN, 0f),
                    CreateSpriteVertex(4f, 0f),
                    CreateSpriteVertex(0f, 4f)
                };
                tightSprite.m_RD.indices = new ushort[] { 0, 1, 2 };
                using (var tightBmp = tightSprite.GetImage())
                {
                    Assert.NotNull(tightBmp);
                    Assert.Equal(4, tightBmp!.Width);
                    Assert.Equal(4, tightBmp.Height);
                    Assert.Equal(255, tightBmp.GetPixel(1, 1).Alpha);
                }

                // Malformed textureRect (NaN / negative / out of bounds) returns null cleanly
                var nanSprite = CreateLinkedSprite(assetsFile, tex.m_PathID, float.NaN, 0, 4, 4, name: "NanRect");
                Assert.Null(nanSprite.GetImage());

                var negativeSprite = CreateLinkedSprite(assetsFile, tex.m_PathID, 0, 0, -5, 4, name: "NegRect");
                Assert.Null(negativeSprite.GetImage());

                var oobSprite = CreateLinkedSprite(assetsFile, tex.m_PathID, 100, 100, 4, 4, name: "OobRect");
                Assert.Null(oobSprite.GetImage());

                GC.KeepAlive(tex);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        #endregion

        #region TEX-17 SpriteAtlas Metadata & Batch Export

        [Fact]
        public void SpriteAtlasMetadataExporter_ExportsBatchImagesManifestJson_AndBlocksPathTraversal()
        {
            SpriteHelper.ClearAtlasCache();
            var tempRoot = Path.Combine(Path.GetTempPath(), $"as_block5_atlas_{Guid.NewGuid():N}");
            var outDir = Path.Combine(tempRoot, "export");
            try
            {
                var assetsFile = CreateMinimalSerializedFile("ui_atlas_bundle.assets");
                var rgba = CreateSolidRgba(16, 16, r: 90, g: 180, b: 240, a: 255);
                var atlasTex = CreateSyntheticTexture(16, 16, TextureFormat.RGBA32, new ResourceReader(rgba), pathId: 100, assetsFile: assetsFile);
                assetsFile.AddObject(atlasTex);

                var keyA = new KeyValuePair<Guid, long>(Guid.NewGuid(), 1L);
                var keyB = new KeyValuePair<Guid, long>(Guid.NewGuid(), 2L);

                var atlas = (SpriteAtlas)RuntimeHelpers.GetUninitializedObject(typeof(SpriteAtlas));
                atlas.m_Name = "../../EscapedAtlas";
                atlas.m_PathID = 500;
                atlas.assetsFile = assetsFile;
                atlas.m_IsVariant = false;
                atlas.m_RenderDataMap = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlasData>
                {
                    [keyA] = CreateSpriteAtlasData(assetsFile, atlasTex.m_PathID, 0, 0, 8, 8, new Vector2(2f, 1f), new Vector2(0.5f, 0.5f), 1f),
                    [keyB] = CreateSpriteAtlasData(assetsFile, atlasTex.m_PathID, 8, 0, 8, 8, Vector2.Zero, Vector2.Zero, 1f)
                };
                assetsFile.AddObject(atlas);

                // Zip Slip / traversal names & duplicate name collision test
                var spriteA = CreateLinkedSprite(assetsFile, atlasTex.m_PathID, 0, 0, 8, 8, name: @"..\..\evil/HeroIcon");
                spriteA.m_PathID = 501;
                spriteA.m_Rect = CreateRectf(0, 0, 12, 10);
                spriteA.m_Border = new Vector4(1, 2, 3, 4);
                spriteA.m_Pivot = new Vector2(0.25f, 0.75f);
                spriteA.m_PixelsToUnits = 100f;
                spriteA.m_Extrude = 2;
                spriteA.m_RenderDataKey = keyA;
                spriteA.m_SpriteAtlas = new PPtr<SpriteAtlas>(0, atlas.m_PathID, assetsFile);
                spriteA.m_AtlasTags = new[] { "UI", "Icons" };
                spriteA.m_PhysicsShape = new[] { new[] { Vector2.Zero, new Vector2(1, 0), new Vector2(0, 1) } };
                spriteA.m_Bones = Array.Empty<SpriteBone>();
                assetsFile.AddObject(spriteA);

                var spriteB = CreateLinkedSprite(assetsFile, atlasTex.m_PathID, 8, 0, 8, 8, name: @"..\..\evil/HeroIcon");
                spriteB.m_PathID = 502;
                spriteB.m_Rect = CreateRectf(0, 0, 8, 8);
                spriteB.m_RenderDataKey = keyB;
                spriteB.m_SpriteAtlas = new PPtr<SpriteAtlas>(0, atlas.m_PathID, assetsFile);
                spriteB.m_AtlasTags = Array.Empty<string>();
                spriteB.m_PhysicsShape = Array.Empty<Vector2[]>();
                spriteB.m_Bones = Array.Empty<SpriteBone>();
                assetsFile.AddObject(spriteB);

                atlas.m_PackedSprites = new[]
                {
                    new PPtr<Sprite>(0, spriteA.m_PathID, assetsFile),
                    new PPtr<Sprite>(0, spriteB.m_PathID, assetsFile)
                };

                var result = SpriteAtlasMetadataExporter.ExportSpriteAtlasBatch(
                    atlas,
                    outDir,
                    new SpriteAtlasExportOptions
                    {
                        ImageFormat = ImageFormat.Png,
                        PreserveOriginalRect = true
                    });

                Assert.Equal(2, result.TotalSprites);
                Assert.Equal(2, result.ExportedImages);
                Assert.Equal(0, result.FailedImages);
                Assert.NotNull(result.ManifestPath);
                Assert.True(File.Exists(result.ManifestPath));

                string fullOutDir = Path.GetFullPath(outDir) + Path.DirectorySeparatorChar;
                Assert.StartsWith(fullOutDir, Path.GetFullPath(result.ManifestPath!), StringComparison.OrdinalIgnoreCase);

                // Verify all exported images stay strictly inside outDir and have unique filenames
                Assert.Equal(2, result.ExportedFilePaths.Count);
                Assert.NotEqual(result.ExportedFilePaths[0], result.ExportedFilePaths[1]);
                foreach (var exportedPath in result.ExportedFilePaths)
                {
                    Assert.True(File.Exists(exportedPath));
                    Assert.StartsWith(fullOutDir, Path.GetFullPath(exportedPath), StringComparison.OrdinalIgnoreCase);
                }

                // Verify spriteA preserved original 12x10 rect dimensions in exported PNG
                using (var decodedA = SKBitmap.Decode(result.ExportedFilePaths[0]))
                {
                    Assert.NotNull(decodedA);
                    Assert.Equal(12, decodedA!.Width);
                    Assert.Equal(10, decodedA.Height);
                }

                // Verify JSON manifest schema & field fidelity
                string json = File.ReadAllText(result.ManifestPath!, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                Assert.Equal("../../EscapedAtlas", root.GetProperty("atlasName").GetString());
                Assert.Equal(500L, root.GetProperty("pathId").GetInt64());
                var spritesJson = root.GetProperty("sprites");
                Assert.Equal(2, spritesJson.GetArrayLength());

                var first = spritesJson[0];
                Assert.Equal(501L, first.GetProperty("pathId").GetInt64());
                Assert.Equal(100f, first.GetProperty("pixelsPerUnit").GetSingle());
                Assert.Equal(2u, first.GetProperty("extrude").GetUInt32());
                Assert.Equal(1, first.GetProperty("physicsShapeCount").GetInt32());
                Assert.Equal(0, first.GetProperty("boneCount").GetInt32());
                Assert.Equal(1f, first.GetProperty("border").GetProperty("left").GetSingle());
                Assert.Equal(2f, first.GetProperty("border").GetProperty("bottom").GetSingle());
                Assert.Equal(3f, first.GetProperty("border").GetProperty("right").GetSingle());
                Assert.Equal(4f, first.GetProperty("border").GetProperty("top").GetSingle());
                Assert.Equal(0.25f, first.GetProperty("pivot").GetProperty("x").GetSingle());
                Assert.Equal(0.75f, first.GetProperty("pivot").GetProperty("y").GetSingle());
                Assert.Equal(2f, first.GetProperty("textureRectOffset").GetProperty("x").GetSingle());
                Assert.Equal(1f, first.GetProperty("textureRectOffset").GetProperty("y").GetSingle());

                GC.KeepAlive(atlasTex);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        #endregion

        #region TEX-11 HDR Texture Export (OpenEXR + 32-bit Float TIFF + Linear Float Decoders)

        [Fact]
        public void HdrTextureExporter_IsHdrFormat_IdentifiesHdrAndLdrFormatsCorrectly()
        {
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RGBAFloat));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RGBAHalf));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RGBFloat));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.ARGBFloat));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RFloat));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RHalf));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RGFloat));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RGHalf));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.RGB9e5Float));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.BC6H));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.ASTC_HDR_4x4));
            Assert.True(HdrTextureExporter.IsHdrFormat(TextureFormat.ASTC_HDR_12x12));

            Assert.False(HdrTextureExporter.IsHdrFormat(TextureFormat.RGBA32));
            Assert.False(HdrTextureExporter.IsHdrFormat(TextureFormat.DXT1));
            Assert.False(HdrTextureExporter.IsHdrFormat(TextureFormat.BC7));
            Assert.False(HdrTextureExporter.IsHdrFormat(TextureFormat.ASTC_RGBA_4x4));
        }

        [Fact]
        public void HdrTextureExporter_DecodesHdrFormatsPreservingValuesAboveOneAndNegatives()
        {
            // 1) RGBAFloat (2x1): pixel0 has HDR > 1.0 and negative B; pixel1 has bright radiance
            var rgbaFloatBytes = new byte[2 * 16];
            WriteFloatsLE(rgbaFloatBytes, 0, 12.5f, 3.25f, -0.5f, 0.75f, 0.1f, 42.0f, 8.0f, 1.0f);
            var texRgbaFloat = CreateSyntheticTexture(2, 1, TextureFormat.RGBAFloat, new ResourceReader(rgbaFloatBytes), pathId: 201);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRgbaFloat, out var decodedRgba, out int w, out int h, out var meta, flipVertical: false));
            Assert.Equal(2, w);
            Assert.Equal(1, h);
            Assert.NotNull(meta);
            Assert.True(meta!.PreservedTrueFloatRange);
            Assert.Equal(-0.5f, meta.MinValue, precision: 4);
            Assert.Equal(42.0f, meta.MaxValue, precision: 4);
            Assert.True(meta.MaxLuminance > 30f);
            Assert.Equal(12.5f, decodedRgba[0], precision: 5);
            Assert.Equal(3.25f, decodedRgba[1], precision: 5);
            Assert.Equal(-0.5f, decodedRgba[2], precision: 5);
            Assert.Equal(0.75f, decodedRgba[3], precision: 5);

            // 2) RGBAHalf (1x1)
            var rgbaHalfBytes = new byte[8];
            BinaryPrimitives.WriteHalfLittleEndian(rgbaHalfBytes.AsSpan(0, 2), (Half)6.5f);
            BinaryPrimitives.WriteHalfLittleEndian(rgbaHalfBytes.AsSpan(2, 2), (Half)(-1.25f));
            BinaryPrimitives.WriteHalfLittleEndian(rgbaHalfBytes.AsSpan(4, 2), (Half)128.0f);
            BinaryPrimitives.WriteHalfLittleEndian(rgbaHalfBytes.AsSpan(6, 2), (Half)0.5f);
            var texRgbaHalf = CreateSyntheticTexture(1, 1, TextureFormat.RGBAHalf, new ResourceReader(rgbaHalfBytes), pathId: 202);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRgbaHalf, out var decodedHalf, out _, out _, flipVertical: false));
            Assert.Equal(6.5f, decodedHalf[0], precision: 3);
            Assert.Equal(-1.25f, decodedHalf[1], precision: 3);
            Assert.Equal(128.0f, decodedHalf[2], precision: 3);
            Assert.Equal(0.5f, decodedHalf[3], precision: 3);

            // 3) RFloat (1x1)
            var rFloatBytes = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(rFloatBytes, 9.75f);
            var texRFloat = CreateSyntheticTexture(1, 1, TextureFormat.RFloat, new ResourceReader(rFloatBytes), pathId: 203);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRFloat, out var decodedR, out _, out _, flipVertical: false));
            Assert.Equal(9.75f, decodedR[0], precision: 5);
            Assert.Equal(0f, decodedR[1]);
            Assert.Equal(0f, decodedR[2]);
            Assert.Equal(1f, decodedR[3]);

            // 4) RGFloat (1x1)
            var rgFloatBytes = new byte[8];
            WriteFloatsLE(rgFloatBytes, 0, 4.5f, 15.25f);
            var texRgFloat = CreateSyntheticTexture(1, 1, TextureFormat.RGFloat, new ResourceReader(rgFloatBytes), pathId: 204);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRgFloat, out var decodedRg, out _, out _, flipVertical: false));
            Assert.Equal(4.5f, decodedRg[0], precision: 5);
            Assert.Equal(15.25f, decodedRg[1], precision: 5);
            Assert.Equal(0f, decodedRg[2]);
            Assert.Equal(1f, decodedRg[3]);

            // 5) RGB9e5Float (1x1): exp=24 -> scale = 2^(24-24) = 1.0 -> R=300, G=150, B=50 (true HDR > 1.0!)
            uint packed9e5 = (24u << 27) | (50u << 18) | (150u << 9) | 300u;
            var rgb9e5Bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(rgb9e5Bytes, packed9e5);
            var texRgb9e5 = CreateSyntheticTexture(1, 1, TextureFormat.RGB9e5Float, new ResourceReader(rgb9e5Bytes), pathId: 205);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRgb9e5, out var decoded9e5, out _, out _, out var meta9e5, flipVertical: false));
            Assert.True(meta9e5!.PreservedTrueFloatRange);
            Assert.Equal(300.0f, decoded9e5[0], precision: 4);
            Assert.Equal(150.0f, decoded9e5[1], precision: 4);
            Assert.Equal(50.0f, decoded9e5[2], precision: 4);
            Assert.Equal(1.0f, decoded9e5[3], precision: 4);

            // 6) ARGBFloat, RGBFloat, RHalf, RGHalf, and LDR fallback (PreservedTrueFloatRange = false)
            var argbFloatBytes = new byte[16];
            WriteFloatsLE(argbFloatBytes, 0, 0.25f, 20.0f, 10.0f, 5.0f); // A, R, G, B
            var texArgb = CreateSyntheticTexture(1, 1, TextureFormat.ARGBFloat, new ResourceReader(argbFloatBytes), pathId: 207);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texArgb, out var decodedArgb, out _, out _, flipVertical: false));
            Assert.Equal(20.0f, decodedArgb[0], precision: 4);
            Assert.Equal(10.0f, decodedArgb[1], precision: 4);
            Assert.Equal(5.0f, decodedArgb[2], precision: 4);
            Assert.Equal(0.25f, decodedArgb[3], precision: 4);

            var rgbFloatBytes = new byte[12];
            WriteFloatsLE(rgbFloatBytes, 0, 3.5f, 7.25f, 11.5f);
            var texRgb = CreateSyntheticTexture(1, 1, TextureFormat.RGBFloat, new ResourceReader(rgbFloatBytes), pathId: 208);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRgb, out var decodedRgb, out _, out _, flipVertical: false));
            Assert.Equal(3.5f, decodedRgb[0], precision: 4);
            Assert.Equal(7.25f, decodedRgb[1], precision: 4);
            Assert.Equal(11.5f, decodedRgb[2], precision: 4);
            Assert.Equal(1.0f, decodedRgb[3], precision: 4);

            var rHalfBytes = new byte[2];
            BinaryPrimitives.WriteHalfLittleEndian(rHalfBytes, (Half)18.5f);
            var texRHalf = CreateSyntheticTexture(1, 1, TextureFormat.RHalf, new ResourceReader(rHalfBytes), pathId: 210);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRHalf, out var decodedRHalf, out _, out _, flipVertical: false));
            Assert.Equal(18.5f, decodedRHalf[0], precision: 3);

            var rgHalfBytes = new byte[4];
            BinaryPrimitives.WriteHalfLittleEndian(rgHalfBytes.AsSpan(0, 2), (Half)2.5f);
            BinaryPrimitives.WriteHalfLittleEndian(rgHalfBytes.AsSpan(2, 2), (Half)9.0f);
            var texRgHalf = CreateSyntheticTexture(1, 1, TextureFormat.RGHalf, new ResourceReader(rgHalfBytes), pathId: 211);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(texRgHalf, out var decodedRgHalf, out _, out _, flipVertical: false));
            Assert.Equal(2.5f, decodedRgHalf[0], precision: 3);
            Assert.Equal(9.0f, decodedRgHalf[1], precision: 3);

            var ldrTex = CreateSyntheticTexture(1, 1, TextureFormat.RGBA32, new ResourceReader(new byte[] { 255, 128, 0, 200 }), pathId: 212);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(ldrTex, out var decodedLdr, out _, out _, out var metaLdr, flipVertical: false));
            Assert.NotNull(metaLdr);
            Assert.False(metaLdr!.PreservedTrueFloatRange);
            Assert.Equal(1.0f, decodedLdr[0], precision: 3);

            // 7) End-to-end file export helpers (EXR + Float32 TIFF) including mip level 1
            var tempDir = Path.Combine(Path.GetTempPath(), $"as_block5_hdr_{Guid.NewGuid():N}");
            try
            {
                var mip0Hdr = new byte[2 * 2 * 16];
                for (int p = 0; p < 4; p++) WriteFloatsLE(mip0Hdr, p * 16, 5.0f, 2.5f, 1.25f, 1.0f);
                var mip1Hdr = new byte[1 * 1 * 16];
                WriteFloatsLE(mip1Hdr, 0, 88.0f, 44.0f, 22.0f, 0.5f);
                var mipHdrTex = CreateSyntheticTexture(2, 2, TextureFormat.RGBAFloat, new ResourceReader(mip0Hdr.Concat(mip1Hdr).ToArray()), pathId: 213);
                mipHdrTex.m_MipCount = 2;

                string exrPath = Path.Combine(tempDir, "sky_mip1.exr");
                string tiffPath = Path.Combine(tempDir, "sky_mip1.tif");

                var exrResult = HdrTextureExporter.ExportTextureToExr(mipHdrTex, exrPath, ExrPixelFormat.Float32, flipVertical: true, mipLevel: 1);
                Assert.True(exrResult.Success);
                Assert.True(File.Exists(exrPath));
                using (var fs = File.OpenRead(exrPath))
                {
                    Assert.True(HdrTextureExporter.TryReadExrForValidation(fs, out int rw, out int rh, out var exrFloats));
                    Assert.Equal(1, rw);
                    Assert.Equal(1, rh);
                    Assert.Equal(88.0f, exrFloats[0], precision: 4);
                    Assert.Equal(44.0f, exrFloats[1], precision: 4);
                    Assert.Equal(22.0f, exrFloats[2], precision: 4);
                    Assert.Equal(0.5f, exrFloats[3], precision: 4);
                }

                var tiffResult = HdrTextureExporter.ExportTextureToFloat32Tiff(mipHdrTex, tiffPath, flipVertical: true, mipLevel: 1);
                Assert.True(tiffResult.Success);
                Assert.True(File.Exists(tiffPath));
                Assert.True(new FileInfo(tiffPath).Length >= 176 + 16);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }

            // 8) Truncated HDR data fails safely without throwing
            var truncatedTex = CreateSyntheticTexture(4, 4, TextureFormat.RGBAFloat, new ResourceReader(new byte[12]), pathId: 206);
            Assert.False(HdrTextureExporter.TryDecodeLinearRgbaFloat(truncatedTex, out _, out _, out _));
        }

        [Fact]
        public void HdrTextureExporter_WriteExr_HalfAndFloat32_RoundTripsWithHeaderValidation()
        {
            const int width = 3;
            const int height = 2;
            var pixels = new float[]
            {
                // Row 0
                0.0f, 1.0f, 2.5f, 1.0f,
                16.0f, -0.25f, 0.5f, 0.5f,
                250.0f, 12.25f, 3.75f, 0.25f,
                // Row 1
                0.125f, 0.25f, 0.5f, 1.0f,
                512.0f, 64.0f, 8.0f, 0.75f,
                -2.0f, 0.0f, 100.0f, 0.0f
            };

            // HalfFloat OpenEXR round-trip
            using (var msHalf = new MemoryStream())
            {
                HdrTextureExporter.WriteExr(msHalf, pixels, width, height, ExrPixelFormat.HalfFloat);
                var raw = msHalf.ToArray();
                Assert.Equal(HdrTextureExporter.ExrMagicNumber, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)));
                Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)));

                msHalf.Position = 0;
                Assert.True(HdrTextureExporter.TryReadExrForValidation(msHalf, out int readW, out int readH, out var readPixels));
                Assert.Equal(width, readW);
                Assert.Equal(height, readH);
                Assert.Equal(pixels.Length, readPixels.Length);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Assert.Equal((float)(Half)pixels[i], readPixels[i], precision: 3);
                }
            }

            // Float32 OpenEXR bit-exact round-trip
            using (var msFloat = new MemoryStream())
            {
                HdrTextureExporter.WriteExr(msFloat, pixels, width, height, ExrPixelFormat.Float32);
                msFloat.Position = 0;
                Assert.True(HdrTextureExporter.TryReadExrForValidation(msFloat, out int readW, out int readH, out var readPixels));
                Assert.Equal(width, readW);
                Assert.Equal(height, readH);
                Assert.Equal(pixels, readPixels);
            }

            // Truncated EXR stream fails validation cleanly
            using var badMs = new MemoryStream(new byte[] { 0x76, 0x2F, 0x31, 0x01, 0x02, 0x00 });
            Assert.False(HdrTextureExporter.TryReadExrForValidation(badMs, out _, out _, out _));
        }

        [Fact]
        public void HdrTextureExporter_WriteFloat32Tiff_ProducesSpecCompliantIfdAndExactIeeeFloatPixels()
        {
            const int width = 2;
            const int height = 2;
            var pixels = new float[]
            {
                1.5f, 2.25f, 3.75f, 1.0f,
                100.0f, -0.5f, 0.0f, 0.5f,
                0.25f, 12.5f, 64.0f, 0.75f,
                1024.0f, 512.0f, 256.0f, 1.0f
            };

            using var ms = new MemoryStream();
            HdrTextureExporter.WriteFloat32Tiff(ms, pixels, width, height);
            var bytes = ms.ToArray();

            // Verify Little-Endian TIFF header: 'I', 'I', 42, ifdOffset = 8
            Assert.Equal((byte)'I', bytes[0]);
            Assert.Equal((byte)'I', bytes[1]);
            Assert.Equal((ushort)42, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));
            uint ifdOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
            Assert.Equal(8u, ifdOffset);

            // Parse IFD tags and verify strictly ascending order + required HDR float tags
            ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)ifdOffset, 2));
            Assert.Equal(12, entryCount);

            var tags = new Dictionary<ushort, (ushort Type, uint Count, uint ValueOrOffset)>();
            int prevTag = -1;
            for (int i = 0; i < entryCount; i++)
            {
                int entryPos = (int)ifdOffset + 2 + i * 12;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos, 2));
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos + 2, 2));
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryPos + 4, 4));
                uint valOrOff = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryPos + 8, 4));
                Assert.True(tag > prevTag, $"TIFF IFD tags must be strictly ascending: {tag} <= {prevTag}");
                prevTag = tag;
                tags[tag] = (type, count, valOrOff);
            }

            Assert.Equal((uint)width, tags[256].ValueOrOffset);  // ImageWidth
            Assert.Equal((uint)height, tags[257].ValueOrOffset); // ImageLength
            Assert.Equal(1u, tags[259].ValueOrOffset & 0xFFFFu); // Compression = None
            Assert.Equal(2u, tags[262].ValueOrOffset & 0xFFFFu); // Photometric = RGB
            Assert.Equal(4u, tags[277].ValueOrOffset & 0xFFFFu); // SamplesPerPixel = 4
            Assert.Equal(2u, tags[338].ValueOrOffset & 0xFFFFu); // ExtraSamples = Unassociated Alpha

            // BitsPerSample = [32, 32, 32, 32]
            uint bpsOffset = tags[258].ValueOrOffset;
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)bpsOffset + c * 2, 2)));
            }

            // SampleFormat = [3, 3, 3, 3] (IEEE floating point)
            uint sfOffset = tags[339].ValueOrOffset;
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)sfOffset + c * 2, 2)));
            }

            // Verify exact IEEE-754 float pixels in strip
            uint stripOffset = tags[273].ValueOrOffset;
            uint stripBytes = tags[279].ValueOrOffset;
            Assert.Equal((uint)(pixels.Length * 4), stripBytes);
            for (int i = 0; i < pixels.Length; i++)
            {
                float readFloat = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((int)stripOffset + i * 4, 4));
                Assert.Equal(pixels[i], readFloat);
            }
        }

        [Fact]
        public void HdrTextureExporter_Bc6hUf16Mode3_DecodesTrueHdrValuesAboveOne()
        {
            // Construct a valid BC6H UF16 Mode 3 (1-subset, untransformed, 10-bit endpoints) block:
            // Mode bits = 0b00011 (5 bits = 3)
            // epR0 = 800, epG0 = 600, epB0 = 400; epR1 = 800, epG1 = 600, epB1 = 400 (constant HDR color)
            // Unquantize(800, 10) = ((800 << 16) + 0x8000) >> 10 = 51232 -> FinishUnquantize = (51232 * 31) >> 6 = 24815 = 0x60EF (~155.875 in Half!)
            ulong lo = 0;
            ulong hi = 0;
            int bitPos = 0;
            void WriteBits(uint value, int count)
            {
                for (int b = 0; b < count; b++)
                {
                    if (((value >> b) & 1u) != 0)
                    {
                        if (bitPos < 64) lo |= 1UL << bitPos;
                        else hi |= 1UL << (bitPos - 64);
                    }
                    bitPos++;
                }
            }

            WriteBits(3u, 5);    // mode 3
            WriteBits(800u, 10); // epR0
            WriteBits(600u, 10); // epG0
            WriteBits(400u, 10); // epB0
            WriteBits(800u, 10); // epR1
            WriteBits(600u, 10); // epG1
            WriteBits(400u, 10); // epB1
            // Remaining 63 index bits = 0 (weight 0 -> exact endpoint 0)

            var block = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0, 8), lo);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8, 8), hi);

            var tex = CreateSyntheticTexture(4, 4, TextureFormat.BC6H, new ResourceReader(block), pathId: 209);
            Assert.True(HdrTextureExporter.TryDecodeLinearRgbaFloat(tex, out var rgba, out int w, out int h, out var meta, flipVertical: false));
            Assert.Equal(4, w);
            Assert.Equal(4, h);
            Assert.NotNull(meta);
            Assert.True(meta!.PreservedTrueFloatRange);
            Assert.True(rgba[0] > 100f, $"Expected HDR red > 100 from BC6H UF16 block, got {rgba[0]}");
            Assert.True(rgba[1] > 1f, $"Expected HDR green > 1 from BC6H UF16 block, got {rgba[1]}");
            Assert.Equal(1.0f, rgba[3]);
        }

        #endregion

        #region TEX-12 TextureMipmapService & TextureMipmapViewModel

        [Fact]
        public void TextureMipmapService_Rgba32AndDxt1_ComputesOffsetsDecodesEachMipAndHandlesTruncation()
        {
            // 1) RGBA32 8x8 with 4 mips: 8x8 (256B), 4x4 (64B), 2x2 (16B), 1x1 (4B) = 340B total
            var mip0 = CreateSolidRgba(8, 8, r: 255, g: 0, b: 0, a: 255);
            var mip1 = CreateSolidRgba(4, 4, r: 0, g: 255, b: 0, a: 255);
            var mip2 = CreateSolidRgba(2, 2, r: 0, g: 0, b: 255, a: 255);
            var mip3 = CreateSolidRgba(1, 1, r: 255, g: 255, b: 0, a: 255);
            var allRgba = mip0.Concat(mip1).Concat(mip2).Concat(mip3).ToArray();

            var texRgba = CreateSyntheticTexture(8, 8, TextureFormat.RGBA32, new ResourceReader(allRgba), pathId: 301);
            texRgba.m_MipCount = 4;

            var chain = TextureMipmapService.ComputeMipChain(texRgba);
            Assert.Equal(4, chain.Count);
            Assert.Equal((8, 8, 0L, 256L, true), (chain[0].Width, chain[0].Height, chain[0].ByteOffset, chain[0].ByteSize, chain[0].IsAvailable));
            Assert.Equal((4, 4, 256L, 64L, true), (chain[1].Width, chain[1].Height, chain[1].ByteOffset, chain[1].ByteSize, chain[1].IsAvailable));
            Assert.Equal((2, 2, 320L, 16L, true), (chain[2].Width, chain[2].Height, chain[2].ByteOffset, chain[2].ByteSize, chain[2].IsAvailable));
            Assert.Equal((1, 1, 336L, 4L, true), (chain[3].Width, chain[3].Height, chain[3].ByteOffset, chain[3].ByteSize, chain[3].IsAvailable));

            using (var bmp0 = TextureMipmapService.DecodeMipLevel(texRgba, 0))
            using (var bmp1 = TextureMipmapService.DecodeMipLevel(texRgba, 1))
            using (var bmp2 = TextureMipmapService.DecodeMipLevel(texRgba, 2))
            using (var bmp3 = TextureMipmapService.DecodeMipLevel(texRgba, 3))
            {
                Assert.NotNull(bmp0);
                Assert.Equal(new SKColor(255, 0, 0, 255), bmp0!.GetPixel(0, 0));
                Assert.NotNull(bmp1);
                Assert.Equal(4, bmp1!.Width);
                Assert.Equal(new SKColor(0, 255, 0, 255), bmp1.GetPixel(0, 0));
                Assert.NotNull(bmp2);
                Assert.Equal(2, bmp2!.Width);
                Assert.Equal(new SKColor(0, 0, 255, 255), bmp2.GetPixel(0, 0));
                Assert.NotNull(bmp3);
                Assert.Equal(1, bmp3!.Width);
                Assert.Equal(new SKColor(255, 255, 0, 255), bmp3.GetPixel(0, 0));
            }

            // 2) Truncated mip chain: only mip0 (256B) + mip1 (64B) present, m_MipCount claims 4
            var truncatedBytes = mip0.Concat(mip1).ToArray();
            var texTrunc = CreateSyntheticTexture(8, 8, TextureFormat.RGBA32, new ResourceReader(truncatedBytes), pathId: 302);
            texTrunc.m_MipCount = 4;

            var truncChain = TextureMipmapService.ComputeMipChain(texTrunc);
            Assert.Equal(4, truncChain.Count);
            Assert.True(truncChain[0].IsAvailable);
            Assert.True(truncChain[1].IsAvailable);
            Assert.False(truncChain[2].IsAvailable);
            Assert.False(truncChain[3].IsAvailable);
            Assert.Null(TextureMipmapService.DecodeMipLevel(texTrunc, 2));

            // 3) DXT1 8x8 with 3 mips: 8x8 (4 blocks = 32B), 4x4 (1 block = 8B), 2x2 (1 block = 8B) = 48B
            var dxt1Data = new byte[32 + 8 + 8];
            // Mip 0: solid red (RGB565 = 0xF800)
            for (int b = 0; b < 4; b++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dxt1Data.AsSpan(b * 8, 2), 0xF800);
                BinaryPrimitives.WriteUInt16LittleEndian(dxt1Data.AsSpan(b * 8 + 2, 2), 0xF800);
            }
            // Mip 1 (offset 32): solid green (RGB565 = 0x07E0)
            BinaryPrimitives.WriteUInt16LittleEndian(dxt1Data.AsSpan(32, 2), 0x07E0);
            BinaryPrimitives.WriteUInt16LittleEndian(dxt1Data.AsSpan(34, 2), 0x07E0);
            // Mip 2 (offset 40): solid blue (RGB565 = 0x001F)
            BinaryPrimitives.WriteUInt16LittleEndian(dxt1Data.AsSpan(40, 2), 0x001F);
            BinaryPrimitives.WriteUInt16LittleEndian(dxt1Data.AsSpan(42, 2), 0x001F);

            var texDxt1 = CreateSyntheticTexture(8, 8, TextureFormat.DXT1, new ResourceReader(dxt1Data), pathId: 303);
            texDxt1.m_MipCount = 3;

            var dxtChain = TextureMipmapService.ComputeMipChain(texDxt1);
            Assert.Equal(3, dxtChain.Count);
            Assert.Equal((0L, 32L, true), (dxtChain[0].ByteOffset, dxtChain[0].ByteSize, dxtChain[0].IsAvailable));
            Assert.Equal((32L, 8L, true), (dxtChain[1].ByteOffset, dxtChain[1].ByteSize, dxtChain[1].IsAvailable));
            Assert.Equal((40L, 8L, true), (dxtChain[2].ByteOffset, dxtChain[2].ByteSize, dxtChain[2].IsAvailable));

            // Raw byte slicing reads ONLY the exact requested mip slice for DXT1
            var rawDxtMip1 = TextureMipmapService.ReadMipRawBytes(texDxt1, 1, out var rawDxtMip1Info);
            Assert.NotNull(rawDxtMip1);
            Assert.Equal(8, rawDxtMip1!.Length);
            Assert.Equal(4, rawDxtMip1Info.Width);
            Assert.Equal((ushort)0x07E0, BinaryPrimitives.ReadUInt16LittleEndian(rawDxtMip1.AsSpan(0, 2)));

            var rawDxtMip2 = TextureMipmapService.ReadMipRawBytes(texDxt1, 2, out var rawDxtMip2Info);
            Assert.NotNull(rawDxtMip2);
            Assert.Equal(8, rawDxtMip2!.Length);
            Assert.Equal(2, rawDxtMip2Info.Width);
            Assert.Equal((ushort)0x001F, BinaryPrimitives.ReadUInt16LittleEndian(rawDxtMip2.AsSpan(0, 2)));

            if (IsNativeDecoderAvailable())
            {
                using var dxtMip0 = TextureMipmapService.DecodeMipLevel(texDxt1, 0);
                using var dxtMip1 = TextureMipmapService.DecodeMipLevel(texDxt1, 1);
                using var dxtMip2 = TextureMipmapService.DecodeMipLevel(texDxt1, 2);
                Assert.NotNull(dxtMip0);
                Assert.Equal(new SKColor(255, 0, 0, 255), dxtMip0!.GetPixel(0, 0));
                Assert.NotNull(dxtMip1);
                Assert.Equal(new SKColor(0, 255, 0, 255), dxtMip1!.GetPixel(0, 0));
                Assert.NotNull(dxtMip2);
                Assert.Equal(new SKColor(0, 0, 255, 255), dxtMip2!.GetPixel(0, 0));
            }

            // 4) Crunched format exposes level 0 safely without computing invalid per-mip offsets
            var crunchedTex = CreateSyntheticTexture(16, 16, TextureFormat.DXT1Crunched, new ResourceReader(new byte[64]), pathId: 305);
            crunchedTex.m_MipCount = 5;
            var crunchedChain = TextureMipmapService.ComputeMipChain(crunchedTex);
            Assert.Single(crunchedChain);
            Assert.Equal((0, 16, 16, 0L, 64L, true), (crunchedChain[0].Level, crunchedChain[0].Width, crunchedChain[0].Height, crunchedChain[0].ByteOffset, crunchedChain[0].ByteSize, crunchedChain[0].IsAvailable));

            // 5) Legacy Unity (<5.2) m_MipMap=true with m_MipCount=0 computes full mip chain safely
            var legacyTex = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(new byte[4 * 4 * 4 + 2 * 2 * 4 + 4]), pathId: 306);
            legacyTex.m_MipCount = 0;
            legacyTex.m_MipMap = true;
            var legacyChain = TextureMipmapService.ComputeMipChain(legacyTex);
            Assert.Equal(3, legacyChain.Count);
            Assert.All(legacyChain, m => Assert.True(m.IsAvailable));
        }

        [Fact]
        public void TextureMipmapService_ExportMipChain_WritesAllAvailableMipsAndJsonManifest()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"as_block5_mips_{Guid.NewGuid():N}");
            try
            {
                var allRgba = CreateSolidRgba(4, 4, 200, 100, 50, 255)
                    .Concat(CreateSolidRgba(2, 2, 100, 200, 50, 255))
                    .Concat(CreateSolidRgba(1, 1, 50, 100, 200, 255))
                    .ToArray();
                var tex = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(allRgba), pathId: 304);
                tex.m_Name = "../BrickWall";
                tex.m_MipCount = 3;

                var export = TextureMipmapService.ExportMipChain(tex, tempDir, ImageFormat.Png);
                Assert.Equal(3, export.TotalMipLevels);
                Assert.Equal(3, export.ExportedMipLevels);
                Assert.Equal(0, export.SkippedMipLevels);
                Assert.NotNull(export.ManifestPath);
                Assert.True(File.Exists(export.ManifestPath));

                string fullRoot = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
                Assert.StartsWith(fullRoot, Path.GetFullPath(export.ManifestPath!), StringComparison.OrdinalIgnoreCase);

                foreach (var path in export.ExportedFilePaths)
                {
                    Assert.True(File.Exists(path));
                    Assert.StartsWith(fullRoot, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(export.ManifestPath!));
                Assert.Equal(3, doc.RootElement.GetProperty("exportedMipCount").GetInt32());
                Assert.Equal(3, doc.RootElement.GetProperty("levels").GetArrayLength());
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public async Task TextureMipmapViewModel_PureMvvmStateCommandsAndZeroAvaloniaImports()
        {
            var vm = new TextureMipmapViewModel();
            Assert.False(vm.IsVisible);
            Assert.False(vm.CanExportMips);
            Assert.False(vm.CanExportHdr);
            Assert.False(vm.PreviousMipCommand.CanExecute());
            Assert.False(vm.NextMipCommand.CanExecute());

            var mipChanges = new List<int>();
            vm.MipChanged += lvl => mipChanges.Add(lvl);

            int exportMipsCalls = 0;
            int exportExrCalls = 0;
            int exportTiffCalls = 0;
            vm.ExportAllMipsHandler = () => { exportMipsCalls++; return Task.CompletedTask; };
            vm.ExportHdrExrHandler = () => { exportExrCalls++; return Task.CompletedTask; };
            vm.ExportHdrTiffHandler = () => { exportTiffCalls++; return Task.CompletedTask; };

            var hdrData = new byte[4 * 4 * 16 + 2 * 2 * 16 + 1 * 1 * 16];
            var hdrTex = CreateSyntheticTexture(4, 4, TextureFormat.RGBAFloat, new ResourceReader(hdrData), pathId: 401);
            hdrTex.m_MipCount = 3;

            var hdrChain = TextureMipmapService.ComputeMipChain(hdrTex);
            vm.Show(hdrTex, hdrChain);

            Assert.True(vm.IsVisible);
            Assert.Equal(3, vm.MipCount);
            Assert.Equal(2, vm.MaxMipIndex);
            Assert.True(vm.HasMultipleMips);
            Assert.Equal(0, vm.MipIndex);
            Assert.Equal("Mip 0 / 3 (4×4)", vm.MipText);
            Assert.True(vm.IsHdrTexture);
            Assert.Contains("HDR", vm.FormatBadgeText, StringComparison.Ordinal);
            Assert.True(vm.CanExportHdr);
            Assert.True(vm.CanExportMips);
            Assert.Empty(mipChanges); // Show() must not raise MipChanged

            Assert.False(vm.PreviousMipCommand.CanExecute());
            Assert.True(vm.NextMipCommand.CanExecute());

            vm.NextMipCommand.Execute();
            Assert.Equal(1, vm.MipIndex);
            Assert.Equal("Mip 1 / 3 (2×2)", vm.MipText);
            Assert.Equal(new[] { 1 }, mipChanges);
            Assert.True(vm.PreviousMipCommand.CanExecute());

            vm.NextMipCommand.Execute();
            Assert.Equal(2, vm.MipIndex);
            Assert.Equal("Mip 2 / 3 (1×1)", vm.MipText);
            Assert.False(vm.NextMipCommand.CanExecute());

            await vm.ExportAllMipsCommand.ExecuteAsync();
            await vm.ExportHdrExrCommand.ExecuteAsync();
            await vm.ExportHdrTiffCommand.ExecuteAsync();
            Assert.Equal(1, exportMipsCalls);
            Assert.Equal(1, exportExrCalls);
            Assert.Equal(1, exportTiffCalls);

            // Switch to LDR single-mip texture -> CanExportHdr becomes false
            var ldrTex = CreateSyntheticTexture(8, 8, TextureFormat.RGBA32, new ResourceReader(new byte[8 * 8 * 4]), pathId: 402);
            ldrTex.m_MipCount = 1;
            vm.Show(ldrTex, TextureMipmapService.ComputeMipChain(ldrTex));
            Assert.False(vm.IsHdrTexture);
            Assert.False(vm.CanExportHdr);
            Assert.True(vm.CanExportMips);
            Assert.False(vm.HasMultipleMips);
            Assert.Equal("RGBA32", vm.FormatBadgeText);

            vm.Hide();
            Assert.False(vm.IsVisible);
            Assert.False(vm.CanExportMips);
            Assert.False(vm.CanExportHdr);

            // Pure MVVM guard: zero 'using Avalonia' inside TextureMipmapViewModel.cs and AssetStudioUtility new files
            var repoRoot = FindRepoRoot();
            string[] pureFiles =
            {
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels", "TextureMipmapViewModel.cs"),
                Path.Combine(repoRoot, "AssetStudioUtility", "HdrTextureExporter.cs"),
                Path.Combine(repoRoot, "AssetStudioUtility", "TextureMipmapService.cs"),
                Path.Combine(repoRoot, "AssetStudioUtility", "SpriteAtlasMetadataExporter.cs"),
                Path.Combine(repoRoot, "AssetStudioUtility", "SpriteHelper.cs")
            };

            foreach (var filePath in pureFiles)
            {
                Assert.True(File.Exists(filePath), $"Expected source file not found: {filePath}");
                string content = File.ReadAllText(filePath);
                Assert.False(AvaloniaUsingRegex.IsMatch(content), $"Pure MVVM violation: found 'using Avalonia' in {filePath}");
            }
        }

        #endregion

        #region Synthetic Test Helpers

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

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AssetStudio.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate AssetStudio.sln from test base directory.");
        }

        private static void WriteFloatsLE(byte[] dst, int offset, params float[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(dst.AsSpan(offset + i * 4, 4), values[i]);
            }
        }

        private static byte[] CreateSolidRgba(int width, int height, byte r, byte g, byte b, byte a)
        {
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = a;
            }
            return rgba;
        }

        private static Texture2D CreateSyntheticTexture(
            int width,
            int height,
            TextureFormat format,
            ResourceReader imageData,
            long pathId,
            SerializedFile? assetsFile = null)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.m_Name = $"Synthetic_{format}_{pathId}";
            tex.m_PathID = pathId;
            tex.assetsFile = assetsFile;
            tex.version = new[] { 2021, 3, 0, 1 };
            tex.platform = BuildTarget.StandaloneWindows64;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = format;
            tex.m_MipCount = 1;
            tex.image_data = imageData;
            return tex;
        }

        private static Sprite CreateLinkedSprite(
            SerializedFile assetsFile,
            long texturePathId,
            float x,
            float y,
            float w,
            float h,
            string? name = null)
        {
            var sprite = (Sprite)RuntimeHelpers.GetUninitializedObject(typeof(Sprite));
            sprite.m_Name = name ?? $"Sprite_{texturePathId}_{x}_{y}";
            sprite.assetsFile = assetsFile;
            sprite.version = new[] { 2021, 3, 0, 1 };
            sprite.m_Rect = CreateRectf(0, 0, w, h);
            sprite.m_PixelsToUnits = 1f;
            sprite.m_Pivot = new Vector2(0.5f, 0.5f);
            sprite.m_SpriteAtlas = null;
            sprite.m_AtlasTags = Array.Empty<string>();
            sprite.m_PhysicsShape = Array.Empty<Vector2[]>();
            sprite.m_Bones = Array.Empty<SpriteBone>();

            var rd = (SpriteRenderData)RuntimeHelpers.GetUninitializedObject(typeof(SpriteRenderData));
            rd.texture = new PPtr<Texture2D>(0, texturePathId, assetsFile);
            rd.textureRect = CreateRectf(x, y, w, h);
            rd.textureRectOffset = Vector2.Zero;
            rd.atlasRectOffset = Vector2.Zero;
            rd.downscaleMultiplier = 1f;
            rd.settingsRaw = CreateSpriteSettings(0, SpritePackingMode.Rectangle, SpritePackingRotation.None);
            sprite.m_RD = rd;
            return sprite;
        }

        private static SpriteAtlasData CreateSpriteAtlasData(
            SerializedFile assetsFile,
            long texturePathId,
            float x,
            float y,
            float w,
            float h,
            Vector2 textureRectOffset,
            Vector2 atlasRectOffset,
            float downscaleMultiplier)
        {
            var data = (SpriteAtlasData)RuntimeHelpers.GetUninitializedObject(typeof(SpriteAtlasData));
            data.texture = new PPtr<Texture2D>(0, texturePathId, assetsFile);
            data.alphaTexture = new PPtr<Texture2D>(0, 0, assetsFile);
            data.textureRect = CreateRectf(x, y, w, h);
            data.textureRectOffset = textureRectOffset;
            data.atlasRectOffset = atlasRectOffset;
            data.downscaleMultiplier = downscaleMultiplier;
            data.settingsRaw = CreateSpriteSettings(1, SpritePackingMode.Rectangle, SpritePackingRotation.None);
            data.secondaryTextures = Array.Empty<SecondarySpriteTexture>();
            return data;
        }

        private static SecondarySpriteTexture CreateSecondarySpriteTexture(string name, PPtr<Texture2D> ptr)
        {
            var sec = (SecondarySpriteTexture)RuntimeHelpers.GetUninitializedObject(typeof(SecondarySpriteTexture));
            sec.name = name;
            sec.texture = ptr;
            return sec;
        }

        private static SpriteVertex CreateSpriteVertex(float x, float y)
        {
            var v = (SpriteVertex)RuntimeHelpers.GetUninitializedObject(typeof(SpriteVertex));
            v.pos = new Vector3(x, y, 0f);
            return v;
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

        private static SerializedFile CreateMinimalSerializedFile(string fileName)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes("2021.3.16f1"));
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
            var fileReader = new FileReader(fileName, ms);
            return new SerializedFile(fileReader, new AssetsManager());
        }

        #endregion
    }
}
