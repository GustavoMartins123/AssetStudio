using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AssetStudio.Tests
{
    public class SpriteAndStreamingTests
    {
        private static readonly FieldInfo SpriteCacheLockField =
            typeof(SpriteHelper).GetField("_cacheLock", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SpriteHelper._cacheLock field not found.");

        private static readonly FieldInfo SpriteLruListField =
            typeof(SpriteHelper).GetField("_lruList", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SpriteHelper._lruList field not found.");

        private static readonly FieldInfo SpriteCacheByKeyField =
            typeof(SpriteHelper).GetField("_cacheByKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SpriteHelper._cacheByKey field not found.");

        private static readonly FieldInfo SpriteInFlightField =
            typeof(SpriteHelper).GetField("_inFlight", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SpriteHelper._inFlight field not found.");

        private static readonly MethodInfo DecodeTexture2DStreamingMethod =
            typeof(Texture2DConverter).GetMethod("DecodeTexture2DStreaming", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Texture2DConverter.DecodeTexture2DStreaming method not found.");

        [Fact]
        public void SpriteHelper_AtlasCache_HitsAcrossMultipleSpritesAndPreservesPixelAccuracy_RegressionPERF04()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                // 4x4 RGBA32 atlas divided into four 2x2 quadrants with distinct solid colors
                var rgba = new byte[4 * 4 * 4];
                FillRgbaRect(rgba, atlasWidth: 4, x: 0, y: 0, w: 2, h: 2, r: 220, g: 10, b: 20, a: 255);  // bottom-left
                FillRgbaRect(rgba, atlasWidth: 4, x: 2, y: 0, w: 2, h: 2, r: 30, g: 210, b: 40, a: 200);  // bottom-right
                FillRgbaRect(rgba, atlasWidth: 4, x: 0, y: 2, w: 2, h: 2, r: 50, g: 60, b: 230, a: 128);  // top-left
                FillRgbaRect(rgba, atlasWidth: 4, x: 2, y: 2, w: 2, h: 2, r: 240, g: 200, b: 15, a: 0);   // top-right (A=0 unpremul)

                var assetsFile = CreateMinimalSerializedFile("atlas_cache_hit.assets");
                var texture = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(rgba), pathId: 100, assetsFile: assetsFile);
                assetsFile.AddObject(texture);

                var spriteBL = CreateLinkedSprite(assetsFile, texture.m_PathID, 0, 0, 2, 2);
                var spriteBR = CreateLinkedSprite(assetsFile, texture.m_PathID, 2, 0, 2, 2);
                var spriteTL = CreateLinkedSprite(assetsFile, texture.m_PathID, 0, 2, 2, 2);
                var spriteTR = CreateLinkedSprite(assetsFile, texture.m_PathID, 2, 2, 2, 2);

                // First crop populates the atlas cache (1 entry)
                using (var imgBL = spriteBL.GetImage())
                {
                    Assert.NotNull(imgBL);
                    AssertSolidBgra(imgBL!, expectedW: 2, expectedH: 2, b: 20, g: 10, r: 220, a: 255);
                }
                Assert.Equal(1, GetSpriteCacheCount());

                // Mutate underlying RGBA bytes in place: subsequent crops MUST hit the cached atlas
                // and still return the originally decoded quadrant pixels instead of re-decoding.
                FillRgbaRect(rgba, atlasWidth: 4, x: 0, y: 0, w: 4, h: 4, r: 99, g: 88, b: 77, a: 255);

                using (var imgBR = spriteBR.GetImage())
                {
                    Assert.NotNull(imgBR);
                    AssertSolidBgra(imgBR!, expectedW: 2, expectedH: 2, b: 40, g: 210, r: 30, a: 200);
                }
                using (var imgTL = spriteTL.GetImage())
                {
                    Assert.NotNull(imgTL);
                    AssertSolidBgra(imgTL!, expectedW: 2, expectedH: 2, b: 230, g: 60, r: 50, a: 128);
                }
                using (var imgTR = spriteTR.GetImage())
                {
                    Assert.NotNull(imgTR);
                    AssertSolidBgra(imgTR!, expectedW: 2, expectedH: 2, b: 15, g: 200, r: 240, a: 0);
                }
                Assert.Equal(1, GetSpriteCacheCount());

                // After clearing the cache, re-decoding picks up the mutated pixel buffer
                SpriteHelper.ClearAtlasCache();
                Assert.Equal(0, GetSpriteCacheCount());
                using (var imgAfterClear = spriteBL.GetImage())
                {
                    Assert.NotNull(imgAfterClear);
                    AssertSolidBgra(imgAfterClear!, expectedW: 2, expectedH: 2, b: 77, g: 88, r: 99, a: 255);
                }

                GC.KeepAlive(texture);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        [Fact]
        public void SpriteHelper_AtlasCache_DistinguishesDistinctTextureInstancesWithSamePathIdAndFullName_RegressionPERF04()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                const long sharedPathId = 77777;
                const string sharedFileName = "reloaded_atlas.assets";

                var fileA = CreateMinimalSerializedFile(sharedFileName);
                var fileB = CreateMinimalSerializedFile(sharedFileName);
                Assert.Equal(fileA.fullName, fileB.fullName);

                var rgbaRed = CreateSolidRgba(4, 4, r: 230, g: 25, b: 35, a: 255);
                var rgbaGreen = CreateSolidRgba(4, 4, r: 20, g: 215, b: 45, a: 255);

                var texA = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(rgbaRed), pathId: sharedPathId, assetsFile: fileA);
                var texB = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(rgbaGreen), pathId: sharedPathId, assetsFile: fileB);
                fileA.AddObject(texA);
                fileB.AddObject(texB);

                var spriteA = CreateLinkedSprite(fileA, sharedPathId, 0, 0, 2, 2);
                var spriteB = CreateLinkedSprite(fileB, sharedPathId, 0, 0, 2, 2);

                using (var imgA1 = spriteA.GetImage())
                {
                    Assert.NotNull(imgA1);
                    AssertSolidBgra(imgA1!, 2, 2, b: 35, g: 25, r: 230, a: 255);
                }

                // Second distinct Texture2D with identical fullName & m_PathID must NOT receive stale pixels from texA
                using (var imgB = spriteB.GetImage())
                {
                    Assert.NotNull(imgB);
                    AssertSolidBgra(imgB!, 2, 2, b: 45, g: 215, r: 20, a: 255);
                }

                // Original instance still resolves cleanly to its own pixels
                using (var imgA2 = spriteA.GetImage())
                {
                    Assert.NotNull(imgA2);
                    AssertSolidBgra(imgA2!, 2, 2, b: 35, g: 25, r: 230, a: 255);
                }

                GC.KeepAlive(texA);
                GC.KeepAlive(texB);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        [Fact]
        public void SpriteHelper_AtlasCache_EvictsLeastRecentlyUsedEntriesAtMax4Bound_RegressionPERF04()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                const int textureCount = 6;
                var assetsFile = CreateMinimalSerializedFile("lru_eviction.assets");
                var textures = new Texture2D[textureCount];
                var buffers = new byte[textureCount][];
                var sprites = new Sprite[textureCount];

                for (int i = 0; i < textureCount; i++)
                {
                    buffers[i] = CreateSolidRgba(4, 4, r: (byte)(20 + i * 35), g: (byte)(40 + i * 25), b: (byte)(60 + i * 20), a: 255);
                    textures[i] = CreateSyntheticTexture(4, 4, TextureFormat.RGBA32, new ResourceReader(buffers[i]), pathId: 500 + i, assetsFile: assetsFile);
                    assetsFile.AddObject(textures[i]);
                    sprites[i] = CreateLinkedSprite(assetsFile, textures[i].m_PathID, 0, 0, 2, 2);
                }

                for (int i = 0; i < textureCount; i++)
                {
                    using var img = sprites[i].GetImage();
                    Assert.NotNull(img);
                    AssertSolidBgra(img!, 2, 2, b: (byte)(60 + i * 20), g: (byte)(40 + i * 25), r: (byte)(20 + i * 35), a: 255);

                    int cacheCount = GetSpriteCacheCount();
                    Assert.InRange(cacheCount, 1, 4);
                    Assert.Equal(Math.Min(i + 1, 4), cacheCount);
                }

                // Textures 0 and 1 were evicted; textures 2..5 remain cached.
                // Mutate underlying buffers for tex0 (evicted) and tex5 (MRU cached) to a sentinel color.
                FillRgbaRect(buffers[0], 4, 0, 0, 4, 4, r: 1, g: 2, b: 3, a: 255);
                FillRgbaRect(buffers[5], 4, 0, 0, 4, 4, r: 1, g: 2, b: 3, a: 255);

                // tex5 is still cached -> returns original color
                using (var img5 = sprites[5].GetImage())
                {
                    Assert.NotNull(img5);
                    AssertSolidBgra(img5!, 2, 2, b: (byte)(60 + 5 * 20), g: (byte)(40 + 5 * 25), r: (byte)(20 + 5 * 35), a: 255);
                }

                // tex0 was evicted -> re-decodes and observes sentinel color, keeping count bounded at 4
                using (var img0 = sprites[0].GetImage())
                {
                    Assert.NotNull(img0);
                    AssertSolidBgra(img0!, 2, 2, b: 3, g: 2, r: 1, a: 255);
                }
                Assert.Equal(4, GetSpriteCacheCount());

                GC.KeepAlive(textures);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        [Fact]
        public async Task SpriteHelper_AtlasCache_ConcurrentCropsWithRepeatedClearAtlasCache_ZeroDisposedErrors_RegressionPERF04()
        {
            SpriteHelper.ClearAtlasCache();
            try
            {
                const int atlasCount = 6;
                const int workerCount = 16;
                const int cropsPerWorker = 25; // 16 * 25 = 400 crops total

                var assetsFile = CreateMinimalSerializedFile("stress_atlas.assets");
                var textures = new Texture2D[atlasCount];
                var spriteSpecs = new (Sprite sprite, int atlasIndex, int cropX, int cropY)[atlasCount * 4];

                for (int a = 0; a < atlasCount; a++)
                {
                    var rgba = new byte[16 * 16 * 4];
                    for (int y = 0; y < 16; y++)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            int idx = (y * 16 + x) * 4;
                            rgba[idx] = (byte)(a * 40 + x);       // R
                            rgba[idx + 1] = (byte)(y * 15);       // G
                            rgba[idx + 2] = (byte)((x + y) * 7);  // B
                            rgba[idx + 3] = 255;                  // A
                        }
                    }

                    textures[a] = CreateSyntheticTexture(16, 16, TextureFormat.RGBA32, new ResourceReader(rgba), pathId: 1000 + a, assetsFile: assetsFile);
                    assetsFile.AddObject(textures[a]);

                    for (int q = 0; q < 4; q++)
                    {
                        int cx = (q % 2) * 8;
                        int cy = (q / 2) * 8;
                        spriteSpecs[a * 4 + q] = (CreateLinkedSprite(assetsFile, textures[a].m_PathID, cx, cy, 4, 4), a, cx, cy);
                    }
                }

                using var cts = new CancellationTokenSource();
                using var clearerStarted = new ManualResetEventSlim(false);
                int clearCount = 0;
                var exceptions = new ConcurrentBag<Exception>();
                int completedCrops = 0;

                var clearerTask = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            SpriteHelper.ClearAtlasCache();
                            if (Interlocked.Increment(ref clearCount) == 1)
                            {
                                clearerStarted.Set();
                            }
                            Thread.Yield();
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                        clearerStarted.Set();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                Assert.True(clearerStarted.Wait(TimeSpan.FromSeconds(5)));

                var workerTasks = new Task[workerCount];
                for (int w = 0; w < workerCount; w++)
                {
                    int workerId = w;
                    workerTasks[w] = Task.Run(() =>
                    {
                        var pixelBuf = new byte[4 * 4 * 4];
                        try
                        {
                            for (int iter = 0; iter < cropsPerWorker; iter++)
                            {
                                var spec = spriteSpecs[(workerId * 7 + iter * 5) % spriteSpecs.Length];
                                using var bmp = spec.sprite.GetImage();
                                Assert.NotNull(bmp);
                                Assert.Equal(4, bmp!.Width);
                                Assert.Equal(4, bmp.Height);
                                bmp.CopyBgraPixelsTo(pixelBuf);

                                // CutImage flips vertically, so output row y corresponds to atlas y = cropY + (3 - y)
                                for (int y = 0; y < 4; y++)
                                {
                                    int atlasY = spec.cropY + (3 - y);
                                    for (int x = 0; x < 4; x++)
                                    {
                                        int atlasX = spec.cropX + x;
                                        int p = (y * 4 + x) * 4;
                                        byte expectedB = (byte)((atlasX + atlasY) * 7);
                                        byte expectedG = (byte)(atlasY * 15);
                                        byte expectedR = (byte)(spec.atlasIndex * 40 + atlasX);
                                        if (pixelBuf[p] != expectedB
                                            || pixelBuf[p + 1] != expectedG
                                            || pixelBuf[p + 2] != expectedR
                                            || pixelBuf[p + 3] != 255)
                                        {
                                            throw new InvalidDataException($"Corrupted pixel in concurrent crop (atlas {spec.atlasIndex}, x={atlasX}, y={atlasY}).");
                                        }
                                    }
                                }

                                Interlocked.Increment(ref completedCrops);
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    });
                }

                await Task.WhenAll(workerTasks);
                cts.Cancel();
                await clearerTask;

                Assert.Empty(exceptions);
                Assert.Equal(workerCount * cropsPerWorker, completedCrops);
                Assert.True(clearCount > 0);
                Assert.InRange(GetSpriteCacheCount(), 0, 4);

                SpriteHelper.ClearAtlasCache();
                Assert.Equal(0, GetSpriteCacheCount());
                Assert.Equal(0, GetSpriteInFlightCount());

                GC.KeepAlive(textures);
            }
            finally
            {
                SpriteHelper.ClearAtlasCache();
            }
        }

        [Fact]
        public void ResourceReader_OpenReadSession_SequentialAndRandomReads_ByteArrayBacked_RegressionPERF03()
        {
            var payload = CreateDeterministicBytes(4096, seed: 17);
            var reader = new ResourceReader(payload);

            using (var session = reader.OpenReadSession())
            {
                Assert.Equal(payload.Length, session.Size);
                VerifySequentialAndRandomReads(session, payload);

                // Bounds validation
                Assert.Throws<ArgumentNullException>(() => session.ReadSegment(0, null!, 0, 4));
                Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadSegment(0, new byte[8], -1, 4));
                Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadSegment(0, new byte[8], 0, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadSegment(0, new byte[8], 5, 4));
                Assert.Throws<EndOfStreamException>(() => session.ReadSegment(-1, new byte[8], 0, 4));
                Assert.Throws<EndOfStreamException>(() => session.ReadSegment(payload.Length - 2, new byte[8], 0, 4));
            }

            // Disposed session throws ObjectDisposedException and supports idempotent Dispose()
            var disposedSession = reader.OpenReadSession();
            disposedSession.Dispose();
            disposedSession.Dispose();
            Assert.Throws<ObjectDisposedException>(() => disposedSession.ReadSegment(0, new byte[4], 0, 4));
        }

        [Fact]
        public void ResourceReader_OpenReadSession_SequentialAndRandomReads_FileReaderBackedAndZeroHandleLeaks_RegressionPERF03()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"as_test_readsession_{Guid.NewGuid():N}.resS");
            try
            {
                const int baseOffset = 512;
                const int sliceSize = 4096;
                var fullFileBytes = CreateDeterministicBytes(baseOffset + sliceSize + 256, seed: 31);
                var expectedSlice = fullFileBytes.AsSpan(baseOffset, sliceSize).ToArray();
                File.WriteAllBytes(tempPath, fullFileBytes);

                using (var fileReader = new FileReader(tempPath))
                {
                    var resourceReader = new ResourceReader(fileReader, baseOffset, sliceSize);

                    using (var session = resourceReader.OpenReadSession())
                    {
                        Assert.Equal(sliceSize, session.Size);
                        VerifySequentialAndRandomReads(session, expectedSlice);
                        Assert.Throws<EndOfStreamException>(() => session.ReadSegment(sliceSize - 3, new byte[8], 0, 4));
                    }

                    // Two simultaneous sessions from the same FileReader must keep independent positions
                    using (var sessionA = resourceReader.OpenReadSession())
                    using (var sessionB = resourceReader.OpenReadSession())
                    {
                        var bufA = new byte[128];
                        var bufB = new byte[128];
                        Assert.Equal(128, sessionA.ReadSegment(64, bufA, 0, 128));
                        Assert.Equal(128, sessionB.ReadSegment(2048, bufB, 0, 128));
                        Assert.Equal(expectedSlice.AsSpan(64, 128).ToArray(), bufA);
                        Assert.Equal(expectedSlice.AsSpan(2048, 128).ToArray(), bufB);

                        Assert.Equal(128, sessionA.ReadSegment(192, bufA, 0, 128));
                        Assert.Equal(expectedSlice.AsSpan(192, 128).ToArray(), bufA);
                    }
                }

                // Opening with FileShare.None proves all cloned FileStream handles were closed without leaking
                using (var exclusive = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(fullFileBytes.Length, exclusive.Length);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        [Fact]
        public void Texture2DConverter_DecodeTexture2DStreaming_Rgba32AndBgra32_MultiChunkMatchesExactBgraAndZeroHandleLeaks_RegressionPERF03()
        {
            // 512 x 200 x 4 = 409,600 bytes (> 256 KiB chunk size) -> exercises multi-chunk streaming loop
            const int width = 512;
            const int height = 200;
            const int byteCount = width * height * 4;

            var rgbaTempPath = Path.Combine(Path.GetTempPath(), $"as_test_stream_rgba_{Guid.NewGuid():N}.resS");
            var bgraTempPath = Path.Combine(Path.GetTempPath(), $"as_test_stream_bgra_{Guid.NewGuid():N}.resS");
            try
            {
                var srcRgba = new byte[byteCount];
                var expectedBgra = new byte[byteCount];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        byte r = (byte)((x * 3 + y) & 0xFF);
                        byte g = (byte)((x + y * 5) & 0xFF);
                        byte b = (byte)((x ^ y) & 0xFF);
                        byte a = (byte)((x + y) & 0xFF);

                        srcRgba[i] = r;
                        srcRgba[i + 1] = g;
                        srcRgba[i + 2] = b;
                        srcRgba[i + 3] = a;

                        expectedBgra[i] = b;
                        expectedBgra[i + 1] = g;
                        expectedBgra[i + 2] = r;
                        expectedBgra[i + 3] = a;
                    }
                }

                File.WriteAllBytes(rgbaTempPath, srcRgba);
                File.WriteAllBytes(bgraTempPath, expectedBgra);

                // 1) Streaming RGBA32 from FileReader-backed ResourceReader
                using (var rgbaReader = new FileReader(rgbaTempPath))
                {
                    var rgbaTex = CreateSyntheticTexture(width, height, TextureFormat.RGBA32, new ResourceReader(rgbaReader, 0, srcRgba.Length), pathId: 2001);
                    var converter = new Texture2DConverter(rgbaTex);
                    var output = new byte[byteCount];

                    bool ok = InvokeDecodeTexture2DStreaming(converter, output);
                    Assert.True(ok);
                    Assert.Equal(expectedBgra, output);
                }

                using (var exclusive = new FileStream(rgbaTempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(srcRgba.Length, exclusive.Length);
                }

                // 2) Streaming BGRA32 fast-path from FileReader-backed ResourceReader
                using (var bgraReader = new FileReader(bgraTempPath))
                {
                    var bgraTex = CreateSyntheticTexture(width, height, TextureFormat.BGRA32, new ResourceReader(bgraReader, 0, expectedBgra.Length), pathId: 2002);
                    var converter = new Texture2DConverter(bgraTex);
                    var output = new byte[byteCount];

                    bool ok = InvokeDecodeTexture2DStreaming(converter, output);
                    Assert.True(ok);
                    Assert.Equal(expectedBgra, output);
                }

                using (var exclusive = new FileStream(bgraTempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(expectedBgra.Length, exclusive.Length);
                }
            }
            finally
            {
                if (File.Exists(rgbaTempPath))
                {
                    File.Delete(rgbaTempPath);
                }
                if (File.Exists(bgraTempPath))
                {
                    File.Delete(bgraTempPath);
                }
            }
        }

        [Fact]
        public void Texture2DConverter_DecodeTexture2DStreaming_NonMultipleOf4Dxt3_MatchesExactPixelsAndLargeAutoStreamingPath_RegressionPERF03()
        {
            // Part 1: Direct streaming decode on 13x9 (non-multiple-of-4 in both X and Y) vs non-streaming reference
            const int smallW = 13;
            const int smallH = 9;
            var smallDxt3 = BuildSyntheticDxt3Image(smallW, smallH);
            var smallStreamingOutput = new byte[smallW * smallH * 4];
            var smallStandardOutput = new byte[smallW * smallH * 4];

            var smallTempPath = Path.Combine(Path.GetTempPath(), $"as_test_dxt3_small_{Guid.NewGuid():N}.resS");
            var largeTempPath = Path.Combine(Path.GetTempPath(), $"as_test_dxt3_large_{Guid.NewGuid():N}.resS");
            try
            {
                File.WriteAllBytes(smallTempPath, smallDxt3);
                using (var smallFileReader = new FileReader(smallTempPath))
                {
                    var texStreaming = CreateSyntheticTexture(smallW, smallH, TextureFormat.DXT3, new ResourceReader(smallFileReader, 0, smallDxt3.Length), pathId: 3001);
                    var texStandard = CreateSyntheticTexture(smallW, smallH, TextureFormat.DXT3, new ResourceReader(smallDxt3), pathId: 3002);

                    Assert.True(InvokeDecodeTexture2DStreaming(new Texture2DConverter(texStreaming), smallStreamingOutput));
                    Assert.True(new Texture2DConverter(texStandard).DecodeTexture2D(smallStandardOutput));

                    Assert.Equal(smallStandardOutput, smallStreamingOutput);
                    VerifySyntheticDxt3Pixels(smallStreamingOutput, smallW, smallH);
                }

                using (var exclusive = new FileStream(smallTempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(smallDxt3.Length, exclusive.Length);
                }

                // Part 2: Large 2049x2049 DXT3 (output = 16,793,604 bytes >= 16 MiB threshold)
                // Automatically triggers DecodeTexture2DStreaming through the PUBLIC DecodeTexture2D API
                // across 17 strips with a non-multiple-of-4 trailing strip height (65 rows) and width (2049 px).
                const int largeW = 2049;
                const int largeH = 2049;
                var largeDxt3 = BuildSyntheticDxt3Image(largeW, largeH);
                File.WriteAllBytes(largeTempPath, largeDxt3);

                using (var largeFileReader = new FileReader(largeTempPath))
                {
                    var largeTex = CreateSyntheticTexture(largeW, largeH, TextureFormat.DXT3, new ResourceReader(largeFileReader, 0, largeDxt3.Length), pathId: 3003);
                    var largeOutput = new byte[largeW * largeH * 4];

                    bool ok = new Texture2DConverter(largeTex).DecodeTexture2D(largeOutput);
                    Assert.True(ok);

                    // Verify exact BGRA pixels at corners, interior, strip boundaries (stripBlocksY = 31 -> y=124), and final partial row/column
                    int[] sampleYs = { 0, 1, 3, 4, 123, 124, 125, 1024, 1983, 1984, largeH - 2, largeH - 1 };
                    int[] sampleXs = { 0, 1, 2, 3, 4, 513, 1024, largeW - 2, largeW - 1 };
                    foreach (int y in sampleYs)
                    {
                        foreach (int x in sampleXs)
                        {
                            AssertSyntheticDxt3Pixel(largeOutput, largeW, x, y);
                        }
                    }
                }

                // Confirm zero leaked file handles after public streaming decode
                using (var exclusive = new FileStream(largeTempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(largeDxt3.Length, exclusive.Length);
                }
            }
            finally
            {
                if (File.Exists(smallTempPath))
                {
                    File.Delete(smallTempPath);
                }
                if (File.Exists(largeTempPath))
                {
                    File.Delete(largeTempPath);
                }
            }
        }

        private static void VerifySequentialAndRandomReads(ResourceReader.ReadSession session, byte[] expected)
        {
            // 1) Sequential sweep with non-power-of-2 chunk size and guard bytes
            var assembled = new byte[expected.Length];
            int pos = 0;
            int chunkStep = 257;
            while (pos < expected.Length)
            {
                int toRead = Math.Min(chunkStep, expected.Length - pos);
                var guarded = new byte[16 + toRead + 16];
                Array.Fill(guarded, (byte)0xA5);

                int read = session.ReadSegment(pos, guarded, 16, toRead);
                Assert.Equal(toRead, read);

                for (int i = 0; i < 16; i++)
                {
                    Assert.Equal(0xA5, guarded[i]);
                    Assert.Equal(0xA5, guarded[16 + toRead + i]);
                }

                Buffer.BlockCopy(guarded, 16, assembled, pos, toRead);
                pos += toRead;
            }
            Assert.Equal(expected, assembled);

            // 2) Zero-length read at boundary
            Assert.Equal(0, session.ReadSegment(expected.Length, new byte[4], 0, 0));

            // 3) Random-offset reads (forward, backward, start, end)
            var rng = new Random(2026);
            var scratch = new byte[300];
            for (int iter = 0; iter < 64; iter++)
            {
                int len = rng.Next(1, 257);
                int offset = rng.Next(0, expected.Length - len + 1);
                int dstOffset = rng.Next(0, 32);

                int read = session.ReadSegment(offset, scratch, dstOffset, len);
                Assert.Equal(len, read);
                Assert.Equal(expected.AsSpan(offset, len).ToArray(), scratch.AsSpan(dstOffset, len).ToArray());
            }
        }

        private static byte[] BuildSyntheticDxt3Image(int width, int height)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var data = new byte[blocksX * blocksY * 16];

            // Fixed RGB565 endpoints: c0 = 0xF800 (R=255,G=0,B=0), c1 = 0x001F (R=0,G=0,B=255)
            // Interpolated colors: c2 = (170,0,85), c3 = (85,0,170)
            const ushort c0 = 0xF800;
            const ushort c1 = 0x001F;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int blockOffset = (by * blocksX + bx) * 16;

                    // Distinct deterministic 4-bit alpha per pixel based on block & pixel coordinates
                    ulong alphaData = 0;
                    uint colorIndices = 0;
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int pixelInBlock = py * 4 + px;
                            ulong a4 = (ulong)((bx + by * 3 + pixelInBlock) & 0x0F);
                            alphaData |= a4 << (pixelInBlock * 4);

                            uint ci = (uint)((bx + by + px + py) & 0x03);
                            colorIndices |= ci << (pixelInBlock * 2);
                        }
                    }

                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(blockOffset, 8), alphaData);
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(blockOffset + 8, 2), c0);
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(blockOffset + 10, 2), c1);
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(blockOffset + 12, 4), colorIndices);
                }
            }

            return data;
        }

        private static void VerifySyntheticDxt3Pixels(byte[] bgra, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    AssertSyntheticDxt3Pixel(bgra, width, x, y);
                }
            }
        }

        private static void AssertSyntheticDxt3Pixel(byte[] bgra, int width, int x, int y)
        {
            int bx = x / 4;
            int by = y / 4;
            int px = x % 4;
            int py = y % 4;
            int pixelInBlock = py * 4 + px;

            byte a4 = (byte)((bx + by * 3 + pixelInBlock) & 0x0F);
            byte expectedA = (byte)((a4 << 4) | a4);

            int ci = (bx + by + px + py) & 0x03;
            (byte expectedB, byte expectedG, byte expectedR) = ci switch
            {
                0 => ((byte)0, (byte)0, (byte)255),
                1 => ((byte)255, (byte)0, (byte)0),
                2 => ((byte)85, (byte)0, (byte)170),
                _ => ((byte)170, (byte)0, (byte)85)
            };

            int idx = (y * width + x) * 4;
            Assert.Equal(expectedB, bgra[idx]);
            Assert.Equal(expectedG, bgra[idx + 1]);
            Assert.Equal(expectedR, bgra[idx + 2]);
            Assert.Equal(expectedA, bgra[idx + 3]);
        }

        private static bool InvokeDecodeTexture2DStreaming(Texture2DConverter converter, byte[] output)
        {
            return (bool)DecodeTexture2DStreamingMethod.Invoke(converter, new object[] { output })!;
        }

        private static int GetSpriteCacheCount()
        {
            var cacheLock = SpriteCacheLockField.GetValue(null)!;
            lock (cacheLock)
            {
                var lru = (ICollection)SpriteLruListField.GetValue(null)!;
                var map = (ICollection)SpriteCacheByKeyField.GetValue(null)!;
                Assert.Equal(lru.Count, map.Count);
                return lru.Count;
            }
        }

        private static int GetSpriteInFlightCount()
        {
            var cacheLock = SpriteCacheLockField.GetValue(null)!;
            lock (cacheLock)
            {
                var inFlight = (ICollection)SpriteInFlightField.GetValue(null)!;
                return inFlight.Count;
            }
        }

        private static void AssertSolidBgra(SKBitmap bmp, int expectedW, int expectedH, byte b, byte g, byte r, byte a)
        {
            Assert.Equal(expectedW, bmp.Width);
            Assert.Equal(expectedH, bmp.Height);
            Assert.Equal(SKColorType.Bgra8888, bmp.ColorType);
            Assert.Equal(SKAlphaType.Unpremul, bmp.AlphaType);

            var pixels = new byte[expectedW * expectedH * 4];
            bmp.CopyBgraPixelsTo(pixels);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.Equal(b, pixels[i]);
                Assert.Equal(g, pixels[i + 1]);
                Assert.Equal(r, pixels[i + 2]);
                Assert.Equal(a, pixels[i + 3]);
            }
        }

        private static byte[] CreateSolidRgba(int width, int height, byte r, byte g, byte b, byte a)
        {
            var rgba = new byte[width * height * 4];
            FillRgbaRect(rgba, width, 0, 0, width, height, r, g, b, a);
            return rgba;
        }

        private static void FillRgbaRect(byte[] rgba, int atlasWidth, int x, int y, int w, int h, byte r, byte g, byte b, byte a)
        {
            for (int py = y; py < y + h; py++)
            {
                for (int px = x; px < x + w; px++)
                {
                    int i = (py * atlasWidth + px) * 4;
                    rgba[i] = r;
                    rgba[i + 1] = g;
                    rgba[i + 2] = b;
                    rgba[i + 3] = a;
                }
            }
        }

        private static byte[] CreateDeterministicBytes(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 29 + seed * 17 + 3) & 0xFF);
            }
            return data;
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
            tex.image_data = imageData;
            return tex;
        }

        private static Sprite CreateLinkedSprite(SerializedFile assetsFile, long texturePathId, float x, float y, float w, float h)
        {
            var sprite = (Sprite)RuntimeHelpers.GetUninitializedObject(typeof(Sprite));
            sprite.m_Name = $"Sprite_{texturePathId}_{x}_{y}";
            sprite.assetsFile = assetsFile;
            sprite.m_Rect = CreateRectf(0, 0, w, h);
            sprite.m_PixelsToUnits = 1f;
            sprite.m_Pivot = new Vector2(0f, 0f);
            sprite.m_SpriteAtlas = null;

            var rd = (SpriteRenderData)RuntimeHelpers.GetUninitializedObject(typeof(SpriteRenderData));
            rd.texture = new PPtr<Texture2D>(0, texturePathId, assetsFile);
            rd.textureRect = CreateRectf(x, y, w, h);
            rd.textureRectOffset = Vector2.Zero;
            rd.downscaleMultiplier = 1f;
            rd.settingsRaw = CreateSpriteSettings(0, SpritePackingMode.Rectangle, SpritePackingRotation.None);
            sprite.m_RD = rd;
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

        private static SerializedFile CreateMinimalSerializedFile(string fileName)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes("2021.3.16f1"));
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
            fullBytes[16] = 0; // LittleEndian metadata
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);

            var ms = new MemoryStream(fullBytes);
            var fileReader = new FileReader(fileName, ms);
            return new SerializedFile(fileReader, new AssetsManager());
        }
    }
}
