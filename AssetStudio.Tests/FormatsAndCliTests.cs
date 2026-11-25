using K4os.Compression.LZ4;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed partial class FormatsAndCliTests
    {
        private delegate int CliRunDelegate(string[] args, TextWriter stdout, TextWriter stderr);

        #region 1. TEX-02 WebP Export & Alpha Preservation

        [Fact]
        public void ImageExtensions_Webp_EncodesValidRiffWebpAndPreservesAlpha()
        {
            const int width = 8;
            const int height = 4;
            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    if (x < 2)
                    {
                        // Fully transparent
                        bgra[idx] = 20;
                        bgra[idx + 1] = 40;
                        bgra[idx + 2] = 60;
                        bgra[idx + 3] = 0;
                    }
                    else if (x < 5)
                    {
                        // Semi-transparent
                        bgra[idx] = 200;
                        bgra[idx + 1] = 100;
                        bgra[idx + 2] = 50;
                        bgra[idx + 3] = 128;
                    }
                    else
                    {
                        // Fully opaque
                        bgra[idx] = 30;
                        bgra[idx + 1] = 220;
                        bgra[idx + 2] = 90;
                        bgra[idx + 3] = 255;
                    }
                }
            }

            Assert.Equal(".webp", ImageFormat.Webp.GetFileExtension());

            using var src = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height);
            using var lossyStream = src.ConvertToStream(ImageFormat.Webp);
            var lossyBytes = lossyStream.ToArray();

            AssertWebpHeader(lossyBytes);

            using (var decodedLossy = ImageExtensions.DecodeBgraUnpremul(lossyBytes))
            {
                Assert.NotNull(decodedLossy);
                Assert.Equal(width, decodedLossy!.Width);
                Assert.Equal(height, decodedLossy.Height);

                var decodedPixels = new byte[width * height * 4];
                decodedLossy.CopyBgraPixelsTo(decodedPixels);

                // Transparent pixel (0, 0)
                Assert.Equal(0, decodedPixels[3]);
                // Semi-transparent pixel (3, 1)
                int semiIdx = (1 * width + 3) * 4;
                Assert.InRange(decodedPixels[semiIdx + 3], 120, 136);
                // Opaque pixel (6, 2)
                int opaqueIdx = (2 * width + 6) * 4;
                Assert.Equal(255, decodedPixels[opaqueIdx + 3]);
            }

            // Also verify lossless WebP option preserves exact pixel channels and alpha
            using var losslessStream = src.ConvertToStream(ImageFormat.Webp, webpQuality: 100, webpLossless: true);
            var losslessBytes = losslessStream.ToArray();
            AssertWebpHeader(losslessBytes);

            using var decodedLossless = ImageExtensions.DecodeBgraUnpremul(losslessBytes);
            Assert.NotNull(decodedLossless);
            var losslessPixels = new byte[width * height * 4];
            decodedLossless!.CopyBgraPixelsTo(losslessPixels);
            int opaqueCheck = (0 * width + 6) * 4;
            Assert.Equal(30, losslessPixels[opaqueCheck]);
            Assert.Equal(220, losslessPixels[opaqueCheck + 1]);
            Assert.Equal(90, losslessPixels[opaqueCheck + 2]);
            Assert.Equal(255, losslessPixels[opaqueCheck + 3]);
            int semiCheck = (0 * width + 3) * 4;
            Assert.Equal(128, losslessPixels[semiCheck + 3]);
        }

        [Fact]
        public void Texture2DExtensions_ConvertToStream_Webp_ProducesValidWebpStream()
        {
            var rgba = CreateSolidRgbaPixels(4, 4, 180, 90, 45, 200);
            var texBytes = BuildTexture2DBinary("WebpTex", 4, 4, TextureFormat.RGBA32, rgba);
            var sf = CreateSerializedFileWithSingleObject("webp_tex.assets", "2021.3.16f1", pathId: 11, ClassIDType.Texture2D, texBytes);
            try
            {
                var tex = Assert.IsType<Texture2D>(Assert.Single(sf.Objects));

                using var ms = tex.ConvertToStream(ImageFormat.Webp, flip: true);
                Assert.NotNull(ms);
                var bytes = ms!.ToArray();
                AssertWebpHeader(bytes);

                using var decoded = ImageExtensions.DecodeBgraUnpremul(bytes);
                Assert.NotNull(decoded);
                Assert.Equal(4, decoded!.Width);
                Assert.Equal(4, decoded.Height);
            }
            finally
            {
                sf.assetsManager.Dispose();
            }
        }

        #endregion

        #region 2. TEX-01 Texture2DArray Parsing, Multi-Slice Decode & Truncation Safety

        [Fact]
        public void Texture2DArrayConverter_DecodesAllSlicesAndSingleSlices_IncludingMipStrideAndAlpha()
        {
            const int width = 4;
            const int height = 4;
            const int depth = 3;
            const int mipCount = 2; // mip0 (4x4*4=64) + mip1 (2x2*4=16) = 80 bytes per slice

            var slice0Mip0 = CreateSolidRgbaPixels(width, height, 255, 20, 30, 255);
            var slice0Mip1 = CreateSolidRgbaPixels(2, 2, 255, 20, 30, 255);
            var slice1Mip0 = CreateSolidRgbaPixels(width, height, 40, 230, 50, 128);
            var slice1Mip1 = CreateSolidRgbaPixels(2, 2, 40, 230, 50, 128);
            var slice2Mip0 = CreateSolidRgbaPixels(width, height, 60, 70, 240, 0);
            var slice2Mip1 = CreateSolidRgbaPixels(2, 2, 60, 70, 240, 0);

            var payload = slice0Mip0
                .Concat(slice0Mip1)
                .Concat(slice1Mip0)
                .Concat(slice1Mip1)
                .Concat(slice2Mip0)
                .Concat(slice2Mip1)
                .ToArray();

            // Unity 2021.3 uses GraphicsFormat.R8G8B8A8_UNorm = 8
            var rawObjectBytes = BuildTexture2DArrayBinary(
                name: "TestArray2021",
                unityVersion: "2021.3.16f1",
                formatValue: 8,
                width: width,
                height: height,
                depth: depth,
                mipCount: mipCount,
                dataSize: (uint)payload.Length,
                imageData: payload);

            var sf = CreateSerializedFileWithSingleObject("tex2darray_2021.assets", "2021.3.16f1", pathId: 101, ClassIDType.Texture2DArray, rawObjectBytes);
            try
            {
                var texArray = Assert.IsType<Texture2DArray>(Assert.Single(sf.Objects));

                Assert.False(texArray.IsTruncated);
                Assert.True(texArray.IsFormatSupported);
                Assert.Equal("TestArray2021", texArray.m_Name);
                Assert.Equal(TextureFormat.RGBA32, texArray.m_TextureFormat);
                Assert.Equal(width, texArray.m_Width);
                Assert.Equal(height, texArray.m_Height);
                Assert.Equal(depth, texArray.m_Depth);

                var converter = new Texture2DArrayConverter(texArray);
                Assert.Equal(depth, converter.SliceCount);

                using (var s0 = converter.ConvertSliceToImage(0))
                using (var s1 = converter.ConvertSliceToImage(1))
                using (var s2 = converter.ConvertSliceToImage(2))
                {
                    AssertSolidBgra(s0, width, height, b: 30, g: 20, r: 255, a: 255);
                    AssertSolidBgra(s1, width, height, b: 50, g: 230, r: 40, a: 128);
                    AssertSolidBgra(s2, width, height, b: 240, g: 70, r: 60, a: 0);
                }

                // Out-of-range slices return null cleanly without throwing
                Assert.Null(converter.ConvertSliceToImage(-1));
                Assert.Null(converter.ConvertSliceToImage(depth));

                var all = converter.ConvertAllSlicesToImages();
                try
                {
                    Assert.Equal(3, all.Count);
                    AssertSolidBgra(all[0], width, height, b: 30, g: 20, r: 255, a: 255);
                    AssertSolidBgra(all[1], width, height, b: 50, g: 230, r: 40, a: 128);
                    AssertSolidBgra(all[2], width, height, b: 240, g: 70, r: 60, a: 0);
                }
                finally
                {
                    foreach (var bmp in all) bmp.Dispose();
                }
            }
            finally
            {
                sf.assetsManager.Dispose();
            }
        }

        [Fact]
        public void Texture2DArrayConverter_HandlesLegacyVersionStreamingResSAndTruncatedDataSafely()
        {
            string tempDir = CreateTempDirectory("Tex2DArrayStream");
            try
            {
                // 1) Legacy Unity 2018.4 (TextureFormat.RGBA32 = 4) + streaming .resS file
                var slice0 = CreateSolidRgbaPixels(2, 2, 10, 200, 30, 255);
                var slice1 = CreateSolidRgbaPixels(2, 2, 220, 40, 50, 255);
                var resSBytes = new byte[64].Concat(slice0).Concat(slice1).ToArray();
                string resSPath = Path.Combine(tempDir, "sharedassets0.resS");
                File.WriteAllBytes(resSPath, resSBytes);

                var streamObjBytes = BuildTexture2DArrayBinary(
                    name: "StreamedLegacyArray",
                    unityVersion: "2018.4.36f1",
                    formatValue: (int)TextureFormat.RGBA32,
                    width: 2,
                    height: 2,
                    depth: 2,
                    mipCount: 1,
                    dataSize: (uint)(slice0.Length + slice1.Length),
                    imageData: Array.Empty<byte>(),
                    streamOffset: 64,
                    streamSize: (uint)(slice0.Length + slice1.Length),
                    streamPath: "sharedassets0.resS");

                string assetsDummyPath = Path.Combine(tempDir, "sharedassets0.assets");
                var sf = CreateMinimalSerializedFile(assetsDummyPath, "2018.4.36f1");
                try
                {
                    var reader = CreateObjectReader(sf, pathId: 202, ClassIDType.Texture2DArray, streamObjBytes);
                    var streamedArray = new Texture2DArray(reader);

                    Assert.False(streamedArray.IsTruncated);
                    Assert.True(streamedArray.IsFormatSupported);
                    Assert.Equal(TextureFormat.RGBA32, streamedArray.m_TextureFormat);

                    var streamedSlices = streamedArray.ConvertAllSlicesToImages();
                    try
                    {
                        Assert.Equal(2, streamedSlices.Count);
                        AssertSolidBgra(streamedSlices[0], 2, 2, b: 30, g: 200, r: 10, a: 255);
                        AssertSolidBgra(streamedSlices[1], 2, 2, b: 50, g: 40, r: 220, a: 255);
                    }
                    finally
                    {
                        foreach (var bmp in streamedSlices) bmp.Dispose();
                    }

                    // 2) Truncated image_data (< 1 slice) -> returns null / empty cleanly without throwing
                    var truncatedPayload = new byte[8]; // 4x4 RGBA32 needs 64 bytes per slice
                    var truncatedObjBytes = BuildTexture2DArrayBinary(
                        name: "TruncatedArray",
                        unityVersion: "2021.3.16f1",
                        formatValue: 8,
                        width: 4,
                        height: 4,
                        depth: 2,
                        mipCount: 1,
                        dataSize: 128,
                        imageData: truncatedPayload);
                    var sf2021Truncated = CreateSerializedFileWithSingleObject("truncated_2021.assets", "2021.3.16f1", pathId: 203, ClassIDType.Texture2DArray, truncatedObjBytes);
                    try
                    {
                        var truncatedArray = Assert.IsType<Texture2DArray>(Assert.Single(sf2021Truncated.Objects));
                        var truncatedConverter = new Texture2DArrayConverter(truncatedArray);
                        Assert.Null(truncatedConverter.ConvertSliceToImage(0));
                        Assert.Empty(truncatedConverter.ConvertAllSlicesToImages());
                    }
                    finally
                    {
                        sf2021Truncated.assetsManager.Dispose();
                    }

                    // 3) Invalid dimensions / truncated binary header -> safe degradation without exception
                    var invalidDimObjBytes = BuildTexture2DArrayBinary(
                        name: "ZeroWidthArray",
                        unityVersion: "2021.3.16f1",
                        formatValue: 8,
                        width: 0,
                        height: 4,
                        depth: 1,
                        mipCount: 1,
                        dataSize: 0,
                        imageData: Array.Empty<byte>());
                    var sf2021Invalid = CreateSerializedFileWithSingleObject("invalid_2021.assets", "2021.3.16f1", pathId: 204, ClassIDType.Texture2DArray, invalidDimObjBytes);
                    try
                    {
                        var invalidDimArray = Assert.IsType<Texture2DArray>(Assert.Single(sf2021Invalid.Objects));
                        Assert.Null(invalidDimArray.ConvertSliceToImage(0));
                        Assert.Empty(invalidDimArray.ConvertAllSlicesToImages());
                    }
                    finally
                    {
                        sf2021Invalid.assetsManager.Dispose();
                    }

                    // 16 bytes: enough for NamedObject + Texture base header (2018.4), truncated before Texture2DArray 28-byte header
                    var shortHeaderBytes = new byte[16];
                    var shortHeaderArray = new Texture2DArray(CreateObjectReader(sf, pathId: 205, ClassIDType.Texture2DArray, shortHeaderBytes));
                    Assert.True(shortHeaderArray.IsTruncated);
                    Assert.Null(shortHeaderArray.ConvertSliceToImage(0));
                }
                finally
                {
                    sf.assetsManager.Dispose();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        #endregion

        #region 3. CLI-01 & CLI-02 AssetStudio.CLI End-to-End Tests

        [Fact]
        public void Cli_HelpScanListInspectExportAndInvalidArgs_WorkDeterministically()
        {
            var runCli = LoadCliRunner();
            string tempDir = CreateTempDirectory("CliTests");
            try
            {
                string inputDir = Path.Combine(tempDir, "input");
                Directory.CreateDirectory(inputDir);

                string bundlePath = Path.Combine(inputDir, "test_assets.bundle");
                BuildTestUnityFsBundle(bundlePath);

                // 1) --help -> exit 0
                var (helpExit, helpOut, helpErr) = InvokeCli(runCli, "--help");
                Assert.Equal(0, helpExit);
                Assert.Contains("AssetStudio.CLI", helpOut);
                Assert.Equal(string.Empty, helpErr);

                // 2) scan --format json / csv / jsonl -> exit 0
                var (scanExit, scanJson, scanErr) = InvokeCli(runCli, "scan", inputDir, "--format", "json");
                Assert.Equal(0, scanExit);
                Assert.Equal(string.Empty, scanErr);
                using (var doc = JsonDocument.Parse(scanJson))
                {
                    Assert.Equal(1, doc.RootElement.GetProperty("totalFiles").GetInt32());
                    Assert.Equal(1, doc.RootElement.GetProperty("unityBundleCount").GetInt32());
                }

                var (scanCsvExit, scanCsv, _) = InvokeCli(runCli, "scan", inputDir, "--format", "csv");
                Assert.Equal(0, scanCsvExit);
                Assert.Contains("path,kind,size,bundleSignature,bundleUnityVersion", scanCsv);
                Assert.Contains("UnityBundle", scanCsv);

                // 3) list --type Texture2D --format json & --format jsonl -> exit 0
                var (listExit, listJson, listErr) = InvokeCli(runCli, "list", inputDir, "--type", "Texture2D", "--format", "json");
                Assert.Equal(0, listExit);
                Assert.Equal(string.Empty, listErr);
                using (var doc = JsonDocument.Parse(listJson))
                {
                    Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
                    var first = doc.RootElement.GetProperty("assets")[0];
                    Assert.Equal(10, first.GetProperty("pathId").GetInt64());
                    Assert.Equal("CliTex", first.GetProperty("name").GetString());
                }

                var (listArrExit, listArrJsonl, _) = InvokeCli(runCli, "list", inputDir, "--type", "Texture2DArray", "--filter", "CliArray", "--format", "jsonl");
                Assert.Equal(0, listArrExit);
                Assert.Contains("\"pathId\":20", listArrJsonl);

                // 4) inspect --path-id 30 --format json & text -> exit 0 (with TypeTree dump!)
                var (inspectExit, inspectJson, inspectErr) = InvokeCli(runCli, "inspect", inputDir, "--path-id", "30", "--format", "json");
                Assert.Equal(0, inspectExit);
                Assert.Equal(string.Empty, inspectErr);
                using (var doc = JsonDocument.Parse(inspectJson))
                {
                    Assert.Equal(30, doc.RootElement.GetProperty("pathId").GetInt64());
                    Assert.Equal("TextAsset", doc.RootElement.GetProperty("type").GetString());
                    Assert.True(doc.RootElement.GetProperty("hasTypeTree").GetBoolean());
                    Assert.Contains("CliHello", doc.RootElement.GetProperty("typeTreeDump").GetString());
                }

                // inspect missing path-id -> exit 1
                var (inspectMissingExit, _, inspectMissingErr) = InvokeCli(runCli, "inspect", inputDir, "--path-id", "999999");
                Assert.Equal(1, inspectMissingExit);
                Assert.False(string.IsNullOrWhiteSpace(inspectMissingErr));

                // 5) export --dry-run -> exit 0, creates NO files on disk
                string dryRunOutDir = Path.Combine(tempDir, "dry_run_out");
                var (dryExit, dryOut, dryErr) = InvokeCli(
                    runCli,
                    "export", inputDir,
                    "--output", dryRunOutDir,
                    "--image-format", "webp",
                    "--dry-run");
                Assert.Equal(0, dryExit);
                Assert.Equal(string.Empty, dryErr);
                Assert.Contains("[dry-run]", dryOut);
                Assert.Contains(".webp", dryOut);
                Assert.False(Directory.Exists(dryRunOutDir));

                // 6) full export (Texture2D + Texture2DArray + TextAsset + Mesh) -> exit 0
                string exportOutDir = Path.Combine(tempDir, "export_out");
                var (expExit, expOut, expErr) = InvokeCli(
                    runCli,
                    "export", inputDir,
                    "--output", exportOutDir,
                    "--type", "Texture2D,Texture2DArray,TextAsset,Mesh",
                    "--image-format", "webp");
                Assert.Equal(0, expExit);
                Assert.Equal(string.Empty, expErr);
                Assert.Contains("Export complete:", expOut);

                string texOut = Path.Combine(exportOutDir, "Texture2D", "CliTex.webp");
                Assert.True(File.Exists(texOut));
                AssertWebpHeader(File.ReadAllBytes(texOut));

                string slice0Out = Path.Combine(exportOutDir, "Texture2DArray", "CliArray_slice000.webp");
                string slice1Out = Path.Combine(exportOutDir, "Texture2DArray", "CliArray_slice001.webp");
                Assert.True(File.Exists(slice0Out));
                Assert.True(File.Exists(slice1Out));
                AssertWebpHeader(File.ReadAllBytes(slice0Out));
                AssertWebpHeader(File.ReadAllBytes(slice1Out));

                string txtOut = Path.Combine(exportOutDir, "TextAsset", "CliHello.txt");
                Assert.True(File.Exists(txtOut));
                Assert.Equal("hello from cli test", File.ReadAllText(txtOut, Encoding.UTF8));

                // Path traversal asset ("../../escape/evil") MUST stay inside exportOutDir!
                Assert.False(File.Exists(Path.Combine(tempDir, "escape", "evil.txt")));
                Assert.False(File.Exists(Path.Combine(tempDir, "evil.txt")));
                var exportedTxtFiles = Directory.GetFiles(Path.Combine(exportOutDir, "TextAsset"), "*.txt", SearchOption.AllDirectories);
                Assert.Equal(2, exportedTxtFiles.Length);
                foreach (var f in exportedTxtFiles)
                {
                    Assert.StartsWith(Path.GetFullPath(exportOutDir) + Path.DirectorySeparatorChar, Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase);
                }

                string meshOut = Path.Combine(exportOutDir, "Mesh", "CliTri.obj");
                Assert.True(File.Exists(meshOut));
                string objContent = File.ReadAllText(meshOut, Encoding.UTF8);
                Assert.Contains("v ", objContent);
                Assert.Contains("f ", objContent);

                // 7) Invalid CLI arguments -> exit code 2
                Assert.Equal(2, InvokeCli(runCli).ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "bogus").ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "scan", inputDir, "--format", "xml").ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "inspect", inputDir).ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "inspect", inputDir, "--path-id", "not_a_number").ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "export", inputDir).ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "export", inputDir, "--output", exportOutDir, "--image-format", "gif").ExitCode);
                Assert.Equal(2, InvokeCli(runCli, "export", inputDir, "--output", exportOutDir, "--type", "InvalidType").ExitCode);

                // 8) Nonexistent input path -> exit code 1
                Assert.Equal(1, InvokeCli(runCli, "scan", Path.Combine(tempDir, "missing_folder")).ExitCode);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        #endregion

        #region Helpers

        private static CliRunDelegate LoadCliRunner()
        {
            return AssetStudio.CLI.CliRunner.Run;
        }

        private static (int ExitCode, string StdOut, string StdErr) InvokeCli(CliRunDelegate runCli, params string[] args)
        {
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            int exitCode = runCli(args, outWriter, errWriter);
            return (exitCode, outWriter.ToString(), errWriter.ToString());
        }

        private static void AssertWebpHeader(byte[] bytes)
        {
            Assert.True(bytes.Length >= 12);
            Assert.Equal((byte)'R', bytes[0]);
            Assert.Equal((byte)'I', bytes[1]);
            Assert.Equal((byte)'F', bytes[2]);
            Assert.Equal((byte)'F', bytes[3]);
            Assert.Equal((byte)'W', bytes[8]);
            Assert.Equal((byte)'E', bytes[9]);
            Assert.Equal((byte)'B', bytes[10]);
            Assert.Equal((byte)'P', bytes[11]);
        }

        private static byte[] CreateSolidRgbaPixels(int width, int height, byte r, byte g, byte b, byte a)
        {
            var data = new byte[width * height * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = r;
                data[i + 1] = g;
                data[i + 2] = b;
                data[i + 3] = a;
            }
            return data;
        }

        private static void AssertSolidBgra(SKBitmap? bitmap, int expectedW, int expectedH, byte b, byte g, byte r, byte a)
        {
            Assert.NotNull(bitmap);
            Assert.Equal(expectedW, bitmap!.Width);
            Assert.Equal(expectedH, bitmap.Height);
            var pixels = new byte[expectedW * expectedH * 4];
            bitmap.CopyBgraPixelsTo(pixels);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.Equal(b, pixels[i]);
                Assert.Equal(g, pixels[i + 1]);
                Assert.Equal(r, pixels[i + 2]);
                Assert.Equal(a, pixels[i + 3]);
            }
        }

        private static SerializedFile CreateSerializedFileWithSingleObject(
            string fileName,
            string unityVersion,
            long pathId,
            ClassIDType classId,
            byte[] objectBytes)
        {
            byte[] fullBytes = BuildSerializedFileWithObjects(
                unityVersion,
                new[] { (pathId, (int)classId, objectBytes, false) });
            var ms = new MemoryStream(fullBytes);
            var fileReader = new FileReader(fileName, ms);
            var assetsManager = new AssetsManager();
            var sf = new SerializedFile(fileReader, assetsManager);
            using var localReader = sf.reader.Clone();
            foreach (var objInfo in sf.m_Objects)
            {
                var objReader = new ObjectReader(localReader, sf, objInfo);
                sf.AddObject(AssetsManager.CreateObjectFromReader(objReader));
            }
            return sf;
        }

        private static SerializedFile CreateMinimalSerializedFile(string fileName, string unityVersion)
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
            fullBytes[16] = 0; // LittleEndian
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);

            var ms = new MemoryStream(fullBytes);
            var fileReader = new FileReader(fileName, ms);
            return new SerializedFile(fileReader, new AssetsManager());
        }

        private static ObjectReader CreateObjectReader(SerializedFile sf, long pathId, ClassIDType classId, byte[] objectBytes)
        {
            var ms = new MemoryStream(objectBytes);
            var binReader = new EndianBinaryReader(ms, EndianType.LittleEndian);
            var info = new ObjectInfo
            {
                m_PathID = pathId,
                byteStart = 0,
                byteSize = (uint)objectBytes.Length,
                classID = (int)classId,
                typeID = 0
            };
            return new ObjectReader(binReader, sf, info);
        }

        private static void WriteAlignedString(BinaryWriter bw, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            bw.Write(utf8.Length);
            bw.Write(utf8);
            int pad = (4 - (utf8.Length % 4)) % 4;
            for (int i = 0; i < pad; i++) bw.Write((byte)0);
        }

        private static void AlignWriter4(BinaryWriter bw)
        {
            int mod = (int)(bw.BaseStream.Position % 4);
            if (mod != 0)
            {
                for (int i = 0; i < 4 - mod; i++) bw.Write((byte)0);
            }
        }

        private static void WriteTextureHeader(BinaryWriter bw, string name, string unityVersion)
        {
            WriteAlignedString(bw, name);
            // Texture base (2017.3+: m_ForcedFallbackFormat, m_DownscaleFallback, 2020.2+: m_IsAlphaChannelOptional, align)
            int major = int.Parse(unityVersion.Split('.')[0]);
            int minor = int.Parse(unityVersion.Split('.')[1]);
            if (major > 2017 || (major == 2017 && minor >= 3))
            {
                if (major < 2023 || (major == 2023 && minor < 2))
                {
                    bw.Write(0); // m_ForcedFallbackFormat
                    bw.Write(false); // m_DownscaleFallback
                }
                if (major > 2020 || (major == 2020 && minor >= 2))
                {
                    bw.Write(false); // m_IsAlphaChannelOptional
                }
                AlignWriter4(bw);
            }
        }

        private static byte[] BuildTexture2DBinary(string name, int width, int height, TextureFormat format, byte[] imageData)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            WriteTextureHeader(bw, name, "2021.3.16f1");
            bw.Write(width);
            bw.Write(height);
            bw.Write(imageData.Length); // m_CompleteImageSize
            bw.Write(0); // m_MipsStripped (2020+)
            bw.Write((int)format);
            bw.Write(1); // m_MipCount
            bw.Write(true); // m_IsReadable
            bw.Write(false); // m_IsPreProcessed (2020+)
            bw.Write(false); // m_IgnoreMasterTextureLimit (2019.3-2022.1)
            bw.Write(false); // m_StreamingMipmaps (2018.2+)
            AlignWriter4(bw);
            bw.Write(0); // m_StreamingMipmapsPriority
            bw.Write(1); // m_ImageCount
            bw.Write(2); // m_TextureDimension
            // GLTextureSettings (2017+: filter, aniso, mipBias, wrapU, wrapV, wrapW)
            bw.Write(1);
            bw.Write(1);
            bw.Write(0f);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0); // m_LightmapFormat
            bw.Write(0); // m_ColorSpace
            bw.Write(Array.Empty<byte>().Length); // m_PlatformBlob (2020.2+)
            AlignWriter4(bw);
            bw.Write(imageData.Length);
            bw.Write(imageData);
            AlignWriter4(bw);
            // m_StreamData (2020+: int64 offset, uint32 size, string path)
            bw.Write(0L);
            bw.Write(0u);
            WriteAlignedString(bw, string.Empty);
            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildTexture2DArrayBinary(
            string name,
            string unityVersion,
            int formatValue,
            int width,
            int height,
            int depth,
            int mipCount,
            uint dataSize,
            byte[] imageData,
            long streamOffset = 0,
            uint streamSize = 0,
            string streamPath = "")
        {
            int major = int.Parse(unityVersion.Split('.')[0]);
            int minor = int.Parse(unityVersion.Split('.')[1]);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            WriteTextureHeader(bw, name, unityVersion);
            bw.Write(0); // m_ColorSpace
            bw.Write(formatValue); // m_Format
            bw.Write(width);
            bw.Write(height);
            bw.Write(depth);
            bw.Write(mipCount);
            bw.Write(dataSize);
            // GLTextureSettings (2017+)
            bw.Write(1);
            bw.Write(1);
            bw.Write(0f);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            if (major > 2020 || (major == 2020 && minor >= 2))
            {
                bw.Write(0); // m_UsageMode
            }
            bw.Write(true); // m_IsReadable
            AlignWriter4(bw);
            bw.Write(imageData.Length);
            if (imageData.Length > 0)
            {
                bw.Write(imageData);
                AlignWriter4(bw);
            }
            if (major >= 2020)
            {
                bw.Write(streamOffset);
            }
            else
            {
                bw.Write((uint)streamOffset);
            }
            bw.Write(streamSize);
            WriteAlignedString(bw, streamPath);
            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildTextAssetBinary(string name, string text)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            WriteAlignedString(bw, name);
            WriteAlignedString(bw, text);
            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildMinimalTriangleMeshBinary(string name)
        {
            // Minimal Unity 2021.3 Mesh with 3 float3 vertices and 3 uint16 indices
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            WriteAlignedString(bw, name);
            // m_SubMeshes (1)
            bw.Write(1);
            bw.Write(0u); // firstByte
            bw.Write(3u); // indexCount
            bw.Write(0);  // topology (Triangles)
            bw.Write(0u); // baseVertex
            bw.Write(0u); // firstVertex
            bw.Write(3u); // vertexCount
            // localAABB (center + extent)
            for (int i = 0; i < 6; i++) bw.Write(0f);
            // m_Shapes
            bw.Write(0); // vertices
            bw.Write(0); // shapes
            bw.Write(0); // channels
            bw.Write(0); // fullWeights
            // m_BindPose, m_BoneNameHashes
            bw.Write(0);
            bw.Write(0);
            bw.Write(0u); // m_RootBoneNameHash
            bw.Write(0);  // m_BonesAABB (2019+)
            bw.Write(0);  // m_VariableBoneCountWeights (2019+)
            bw.Write((byte)0); // m_MeshCompression
            bw.Write(true); // m_IsReadable
            bw.Write(true); // m_KeepVertices
            bw.Write(true); // m_KeepIndices
            AlignWriter4(bw);
            bw.Write(0); // m_IndexFormat (0 = UInt16)
            // m_IndexBuffer (6 bytes: 0, 1, 2)
            bw.Write(6);
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)2);
            AlignWriter4(bw);
            // m_VertexData (2018+: m_VertexCount, m_Channels[14], m_DataSize)
            bw.Write(3u); // vertexCount
            bw.Write(1); // 1 channel (position float3)
            bw.Write((byte)0); // stream 0
            bw.Write((byte)0); // offset 0
            bw.Write((byte)0); // format 0 (Float)
            bw.Write((byte)3); // dimension 3
            // 3 vertices * 3 floats = 36 bytes
            bw.Write(36);
            bw.Write(0f); bw.Write(0f); bw.Write(0f);
            bw.Write(1f); bw.Write(0f); bw.Write(0f);
            bw.Write(0f); bw.Write(1f); bw.Write(0f);
            AlignWriter4(bw);
            // m_CompressedMesh (10 packed vectors + m_UVInfo)
            void WritePackedFloat() { bw.Write(0u); bw.Write(0f); bw.Write(0f); bw.Write(0); bw.Write((byte)0); AlignWriter4(bw); }
            void WritePackedInt() { bw.Write(0u); bw.Write(0); bw.Write((byte)0); AlignWriter4(bw); }
            WritePackedFloat(); // Vertices
            WritePackedFloat(); // UV
            WritePackedFloat(); // Normals
            WritePackedFloat(); // Tangents
            WritePackedInt();   // Weights
            WritePackedInt();   // NormalSigns
            WritePackedInt();   // TangentSigns
            WritePackedFloat(); // FloatColors (5.0+)
            WritePackedInt();   // BoneIndices
            WritePackedInt();   // Triangles
            bw.Write(0u);       // m_UVInfo
            // m_LocalAABB
            for (int i = 0; i < 6; i++) bw.Write(0f);
            bw.Write(0); // m_MeshUsageFlags
            bw.Write(0); // m_BakedConvexCollisionMesh
            bw.Write(0); // m_BakedTriangleCollisionMesh
            bw.Write(1f); bw.Write(1f); // m_MeshMetrics[0..1]
            AlignWriter4(bw);
            // m_StreamData (2020+: int64 offset, uint32 size, string path)
            bw.Write(0L);
            bw.Write(0u);
            WriteAlignedString(bw, string.Empty);
            bw.Flush();
            return ms.ToArray();
        }

        private static void BuildTestUnityFsBundle(string bundlePath)
        {
            var texBytes = BuildTexture2DBinary("CliTex", 4, 4, TextureFormat.RGBA32, CreateSolidRgbaPixels(4, 4, 120, 200, 50, 180));
            var arrPayload = CreateSolidRgbaPixels(4, 4, 255, 0, 0, 255).Concat(CreateSolidRgbaPixels(4, 4, 0, 255, 0, 128)).ToArray();
            var arrBytes = BuildTexture2DArrayBinary("CliArray", "2021.3.16f1", 8, 4, 4, 2, 1, (uint)arrPayload.Length, arrPayload);
            var textBytes = BuildTextAssetBinary("CliHello", "hello from cli test");
            var evilTextBytes = BuildTextAssetBinary("../../escape/evil", "safe inside output");
            var meshBytes = BuildMinimalTriangleMeshBinary("CliTri");

            // Build a single SerializedFile v22 (Unity 2021.3.16f1) with TypeTree for TextAsset (so inspect prints TypeTreeDump!)
            byte[] serializedBytes = BuildSerializedFileWithObjects(
                "2021.3.16f1",
                new[]
                {
                    (PathId: 10L, ClassId: (int)ClassIDType.Texture2D, Data: texBytes, IncludeTextAssetTypeTree: false),
                    (PathId: 20L, ClassId: (int)ClassIDType.Texture2DArray, Data: arrBytes, IncludeTextAssetTypeTree: false),
                    (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: textBytes, IncludeTextAssetTypeTree: true),
                    (PathId: 40L, ClassId: (int)ClassIDType.TextAsset, Data: evilTextBytes, IncludeTextAssetTypeTree: true),
                    (PathId: 50L, ClassId: (int)ClassIDType.Mesh, Data: meshBytes, IncludeTextAssetTypeTree: false)
                });

            byte[] bundleBytes = BuildUnityFsV6Bundle("CAB-clitest01/data.assets", serializedBytes);
            File.WriteAllBytes(bundlePath, bundleBytes);
        }

        private static byte[] BuildSerializedFileWithObjects(
            string unityVersion,
            (long PathId, int ClassId, byte[] Data, bool IncludeTextAssetTypeTree)[] entries)
        {
            // Build metadata
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var bw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.UTF8.GetBytes(unityVersion));
                bw.Write((byte)0);
                bw.Write((int)BuildTarget.StandaloneWindows64);
                bw.Write(true); // m_EnableTypeTree

                bw.Write(entries.Length); // typeCount (1 type per entry for simplicity)
                for (int i = 0; i < entries.Length; i++)
                {
                    bw.Write(entries[i].ClassId);
                    bw.Write(false); // m_IsStrippedType
                    bw.Write((short)-1); // m_ScriptTypeIndex
                    bw.Write(new byte[16]); // m_OldTypeHash
                    if (entries[i].IncludeTextAssetTypeTree)
                    {
                        // TypeTree blob with 4 nodes: Base(TextAsset), m_Name(string), m_Script(string)
                        WriteTextAssetTypeTreeBlob(bw);
                    }
                    else
                    {
                        bw.Write(0); // numberOfNodes
                        bw.Write(0); // stringBufferSize
                    }
                    bw.Write(0); // m_TypeDependencies length
                }

                // Objects
                bw.Write(entries.Length);
                long dataOffset = 0;
                for (int i = 0; i < entries.Length; i++)
                {
                    AlignWriter4(bw);
                    bw.Write(entries[i].PathId);
                    bw.Write((ulong)dataOffset);
                    bw.Write((uint)entries[i].Data.Length);
                    bw.Write(i); // typeID (index into m_Types)
                    dataOffset += entries[i].Data.Length;
                    // Pad object data to 8-byte boundary
                    int pad = (8 - (entries[i].Data.Length % 8)) % 8;
                    dataOffset += pad;
                }

                bw.Write(0); // scriptCount
                bw.Write(0); // externalsCount
                bw.Write(0); // refTypesCount
                bw.Write((byte)0); // userInformation
                bw.Flush();
                metadata = metaMs.ToArray();
            }

            // Assemble data block
            byte[] dataBlock;
            using (var dataMs = new MemoryStream())
            {
                foreach (var entry in entries)
                {
                    dataMs.Write(entry.Data, 0, entry.Data.Length);
                    int pad = (8 - (entry.Data.Length % 8)) % 8;
                    for (int p = 0; p < pad; p++) dataMs.WriteByte(0);
                }
                dataBlock = dataMs.ToArray();
            }

            const int headerSize = 48; // LargeFilesSupport (v22)
            long dataStart = (headerSize + metadata.Length + 15) & ~15L;
            long totalFileSize = dataStart + dataBlock.Length;

            var full = new byte[totalFileSize];
            // Legacy 20-byte prefix (0..19), followed by v22 large header (20..47)
            BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(8, 4), (uint)SerializedFileFormatVersion.LargeFilesSupport);
            full[16] = 0; // LittleEndian
            BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(20, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteInt64BigEndian(full.AsSpan(24, 8), totalFileSize);
            BinaryPrimitives.WriteInt64BigEndian(full.AsSpan(32, 8), dataStart);
            BinaryPrimitives.WriteInt64BigEndian(full.AsSpan(40, 8), 0L);

            Buffer.BlockCopy(metadata, 0, full, headerSize, metadata.Length);
            Buffer.BlockCopy(dataBlock, 0, full, (int)dataStart, dataBlock.Length);
            return full;
        }

        private static void WriteTextAssetTypeTreeBlob(BinaryWriter bw)
        {
            byte[] strBuf;
            uint offTextAsset, offBase, offString, offName, offScript;
            using (var sms = new MemoryStream())
            {
                uint AddStr(string s)
                {
                    uint off = (uint)sms.Position;
                    sms.Write(Encoding.UTF8.GetBytes(s));
                    sms.WriteByte(0);
                    return off;
                }
                offTextAsset = AddStr("TextAsset");
                offBase = AddStr("Base");
                offString = AddStr("string");
                offName = AddStr("m_Name");
                offScript = AddStr("m_Script");
                strBuf = sms.ToArray();
            }

            void WriteNode(ushort version, byte level, byte flags, uint typeOff, uint nameOff, int byteSize, int idx, int metaFlag)
            {
                bw.Write(version);
                bw.Write(level);
                bw.Write(flags);
                bw.Write(typeOff);
                bw.Write(nameOff);
                bw.Write(byteSize);
                bw.Write(idx);
                bw.Write(metaFlag);
                bw.Write(0UL); // m_RefTypeHash (TypeTreeNodeWithTypeFlags)
            }

            bw.Write(3); // numberOfNodes
            bw.Write(strBuf.Length);
            WriteNode(1, 0, 0, offTextAsset, offBase, -1, 0, 0);
            WriteNode(1, 1, 0, offString, offName, -1, 1, 0x4000);
            WriteNode(1, 1, 0, offString, offScript, -1, 2, 0x4000);
            bw.Write(strBuf);
        }

        private static byte[] BuildUnityFsV6Bundle(string nodePath, byte[] nodePayload)
        {
            byte[] blocksInfoBytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                Span<byte> big = stackalloc byte[8];
                bw.Write(new byte[16]); // uncompressedDataHash
                BinaryPrimitives.WriteInt32BigEndian(big.Slice(0, 4), 1); // blockCount
                bw.Write(big.Slice(0, 4));
                BinaryPrimitives.WriteUInt32BigEndian(big.Slice(0, 4), (uint)nodePayload.Length);
                bw.Write(big.Slice(0, 4));
                BinaryPrimitives.WriteUInt32BigEndian(big.Slice(0, 4), (uint)nodePayload.Length);
                bw.Write(big.Slice(0, 4));
                BinaryPrimitives.WriteUInt16BigEndian(big.Slice(0, 2), 0); // CompressionType.None
                bw.Write(big.Slice(0, 2));

                BinaryPrimitives.WriteInt32BigEndian(big.Slice(0, 4), 1); // dirCount
                bw.Write(big.Slice(0, 4));
                BinaryPrimitives.WriteInt64BigEndian(big, 0L); // offset
                bw.Write(big);
                BinaryPrimitives.WriteInt64BigEndian(big, nodePayload.Length); // size
                bw.Write(big);
                BinaryPrimitives.WriteUInt32BigEndian(big.Slice(0, 4), 4u); // flags (serialized file)
                bw.Write(big.Slice(0, 4));
                bw.Write(Encoding.UTF8.GetBytes(nodePath));
                bw.Write((byte)0);
                bw.Flush();
                blocksInfoBytes = ms.ToArray();
            }

            using var outMs = new MemoryStream();
            using var outBw = new BinaryWriter(outMs, Encoding.UTF8, leaveOpen: true);
            Span<byte> buf = stackalloc byte[8];

            outBw.Write(Encoding.UTF8.GetBytes("UnityFS")); outBw.Write((byte)0);
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(0, 4), 6u); outBw.Write(buf.Slice(0, 4));
            outBw.Write(Encoding.UTF8.GetBytes("5.x.x")); outBw.Write((byte)0);
            outBw.Write(Encoding.UTF8.GetBytes("2021.3.16f1")); outBw.Write((byte)0);

            long sizePos = outMs.Position;
            outBw.Write(0L); // placeholder total size
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(0, 4), (uint)blocksInfoBytes.Length); outBw.Write(buf.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(0, 4), (uint)blocksInfoBytes.Length); outBw.Write(buf.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(0, 4), (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined); outBw.Write(buf.Slice(0, 4));

            outBw.Write(blocksInfoBytes);
            outBw.Write(nodePayload);
            outBw.Flush();

            long totalSize = outMs.Length;
            outMs.Position = sizePos;
            BinaryPrimitives.WriteInt64BigEndian(buf, totalSize);
            outMs.Write(buf);
            return outMs.ToArray();
        }

        private static string CreateTempDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        #endregion
    }
}
