using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed partial class FormatsAndCliTests
    {
        private const int CustomZstdCompressionId = 5;

        [Fact]
        public void BundleFile_ZstdBlocksAndZstdBlocksInfo_UnderUndefinedCompressionId_LoadAssets()
        {
            var tempDir = CreateTempDirectory("ZstdBundle");
            try
            {
                var text = string.Concat(Enumerable.Repeat("zstd payload line\n", 400));
                var serialized = BuildSerializedFileWithObjects(
                    "2021.3.16f1",
                    new[] { (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: BuildTextAssetBinary("ZstdHello", text), IncludeTextAssetTypeTree: true) });

                var bundlePath = Path.Combine(tempDir, "zstd.bundle");
                File.WriteAllBytes(bundlePath, BuildUnityFsBundle(
                    "CAB-zstd01/data.assets",
                    serialized,
                    blockSize: 1024,
                    blockCompressionId: CustomZstdCompressionId,
                    compressBlock: CompressZstd,
                    blocksInfoCompressionId: CustomZstdCompressionId,
                    compressBlocksInfo: CompressZstd));

                var manager = new AssetsManager();
                manager.LoadFiles(bundlePath);
                var textAsset = Assert.IsType<TextAsset>(Assert.Single(manager.assetsFileList.SelectMany(f => f.Objects).OfType<TextAsset>()));
                Assert.Equal("ZstdHello", textAsset.m_Name);
                Assert.Equal(text, Encoding.UTF8.GetString(textAsset.m_Script));
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Fact]
        public void BundleFile_UndefinedCompressionIdWithoutZstdFrame_FailsWithoutLoadingGarbage()
        {
            var tempDir = CreateTempDirectory("UnknownCodecBundle");
            try
            {
                var serialized = BuildSerializedFileWithObjects(
                    "2021.3.16f1",
                    new[] { (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: BuildTextAssetBinary("Nope", "unreadable"), IncludeTextAssetTypeTree: true) });

                var bundlePath = Path.Combine(tempDir, "unknown.bundle");
                File.WriteAllBytes(bundlePath, BuildUnityFsBundle(
                    "CAB-unknown01/data.assets",
                    serialized,
                    blockSize: 512,
                    blockCompressionId: CustomZstdCompressionId,
                    compressBlock: static block => block.Select(b => (byte)(b ^ 0x5A)).ToArray()));

                var manager = new AssetsManager();
                manager.LoadFiles(bundlePath);
                Assert.Empty(manager.assetsFileList.SelectMany(f => f.Objects).OfType<TextAsset>());

                Assert.False(BundleFile.TryDecompressZstd(new byte[] { 1, 2, 3, 4, 5 }, 16, out _));
                var payload = Encoding.ASCII.GetBytes("round trip through zstd");
                Assert.True(BundleFile.TryDecompressZstd(CompressZstd(payload), payload.Length, out var decoded));
                Assert.Equal(payload, decoded);
                Assert.False(BundleFile.TryDecompressZstd(CompressZstd(payload), payload.Length + 1, out _));
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(16)]
        [InlineData(1000)]
        public void AssetsManager_ConcatenatedBundlesInOneFile_LoadsEverySegmentWithTheRealFilePath(int paddingBetweenBundles)
        {
            var tempDir = CreateTempDirectory("MultiBundle");
            try
            {
                byte[] MakeBundle(string cab, string assetName, string text, bool zstd)
                {
                    var serialized = BuildSerializedFileWithObjects(
                        "2021.3.16f1",
                        new[] { (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: BuildTextAssetBinary(assetName, text), IncludeTextAssetTypeTree: true) });
                    return zstd
                        ? BuildUnityFsBundle(cab, serialized, 700, CustomZstdCompressionId, CompressZstd)
                        : BuildUnityFsBundle(cab, serialized, 700);
                }

                var first = MakeBundle("CAB-multi01", "FirstAsset", "first segment text", zstd: false);
                var second = MakeBundle("CAB-multi02", "SecondAsset", string.Concat(Enumerable.Repeat("second ", 300)), zstd: true);
                var third = MakeBundle("CAB-multi03", "ThirdAsset", "third segment text", zstd: false);

                var combined = new MemoryStream();
                combined.Write(first);
                combined.Write(new byte[paddingBetweenBundles]);
                combined.Write(second);
                combined.Write(third);
                var bundlePath = Path.Combine(tempDir, "packed.bundle");
                File.WriteAllBytes(bundlePath, combined.ToArray());

                var manager = new AssetsManager();
                manager.LoadFiles(bundlePath);

                var texts = manager.assetsFileList
                    .SelectMany(f => f.Objects)
                    .OfType<TextAsset>()
                    .ToDictionary(t => t.m_Name, t => Encoding.UTF8.GetString(t.m_Script));
                Assert.Equal(3, texts.Count);
                Assert.Equal("first segment text", texts["FirstAsset"]);
                Assert.Equal(string.Concat(Enumerable.Repeat("second ", 300)), texts["SecondAsset"]);
                Assert.Equal("third segment text", texts["ThirdAsset"]);

                Assert.Equal(3, manager.assetsFileList.Count);
                Assert.All(manager.assetsFileList, f => Assert.Equal(Path.GetFullPath(bundlePath), Path.GetFullPath(f.originalPath)));
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Fact]
        public void ExtractSingleFile_ConcatenatedBundles_ExtractsNodesOfEveryBundle()
        {
            var tempDir = CreateTempDirectory("MultiBundleExtract");
            try
            {
                var segments = new[] { "CAB-extract01", "CAB-extract02", "CAB-extract03" }
                    .Select((cab, i) => BuildUnityFsBundle(cab, Encoding.ASCII.GetBytes($"payload-{i}-" + new string('x', 900)), 256, i == 1 ? CustomZstdCompressionId : 0, i == 1 ? CompressZstd : null))
                    .ToArray();
                var bundlePath = Path.Combine(tempDir, "packed.bundle");
                File.WriteAllBytes(bundlePath, segments.SelectMany(s => s).ToArray());

                var outDir = Path.Combine(tempDir, "out");
                var extracted = AssetStudio.Avalonia.Services.AssetExportService.ExtractSingleFile(bundlePath, outDir);
                Assert.Equal(3, extracted);

                var unpacked = Path.Combine(outDir, "packed.bundle_unpacked");
                for (var i = 0; i < 3; i++)
                {
                    var file = Path.Combine(unpacked, $"CAB-extract0{i + 1}");
                    Assert.True(File.Exists(file), $"missing {file}");
                    Assert.StartsWith($"payload-{i}-", Encoding.ASCII.GetString(File.ReadAllBytes(file)));
                }
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Fact]
        public void AssetsManager_SingleBundleWithTrailingBytes_LoadsOnlyThatBundle()
        {
            var tempDir = CreateTempDirectory("TrailingBytesBundle");
            try
            {
                var serialized = BuildSerializedFileWithObjects(
                    "2021.3.16f1",
                    new[] { (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: BuildTextAssetBinary("OnlyAsset", "only"), IncludeTextAssetTypeTree: true) });
                var bundle = BuildUnityFsBundle("CAB-single01/data.assets", serialized, 4096);
                var withTrailer = bundle.Concat(Encoding.ASCII.GetBytes("trailing signature block, no bundle here")).ToArray();
                var bundlePath = Path.Combine(tempDir, "single.bundle");
                File.WriteAllBytes(bundlePath, withTrailer);

                var manager = new AssetsManager();
                manager.LoadFiles(bundlePath);
                var textAsset = Assert.Single(manager.assetsFileList.SelectMany(f => f.Objects).OfType<TextAsset>());
                Assert.Equal("OnlyAsset", textAsset.m_Name);
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Fact]
        public void AssetsManager_AndroidPackage_ReadsStoredEntriesInPlace_AndSkipsNonUnityFiles()
        {
            var tempDir = CreateTempDirectory("ApkPackage");
            try
            {
                byte[] TextAssetFile(string name, string text) => BuildSerializedFileWithObjects(
                    "2021.3.16f1",
                    new[] { (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: BuildTextAssetBinary(name, text), IncludeTextAssetTypeTree: true) });

                var apkPath = Path.Combine(tempDir, "game.apk");
                using (var archive = new System.IO.Compression.ZipArchive(File.Create(apkPath), System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string entryName, byte[] data, System.IO.Compression.CompressionLevel level)
                    {
                        using var stream = archive.CreateEntry(entryName, level).Open();
                        stream.Write(data);
                    }

                    var stored = System.IO.Compression.CompressionLevel.NoCompression;
                    var deflated = System.IO.Compression.CompressionLevel.Optimal;
                    Add("assets/bin/Data/CAB-apk01", BuildUnityFsBundle("CAB-apk01", TextAssetFile("ApkStored", "stored bundle"), 512), stored);
                    Add("assets/bin/Data/CAB-apk02", BuildUnityFsBundle("CAB-apk02", TextAssetFile("ApkDeflated", "deflated bundle"), 512), deflated);
                    Add("assets/bin/Data/level0", TextAssetFile("LooseLevel", "loose serialized file"), stored);
                    Add("assets/bin/Data/sharedassets0.assets.resS", Enumerable.Repeat((byte)7, 4096).ToArray(), stored);
                    Add("classes.dex", Encoding.ASCII.GetBytes("dex\n035\0" + new string('d', 5000)), deflated);
                    Add("lib/arm64-v8a/libmain.so", Enumerable.Repeat((byte)0x7F, 8000).ToArray(), deflated);
                    Add("res/drawable/icon.png", Enumerable.Repeat((byte)0x89, 3000).ToArray(), stored);
                    Add("META-INF/MANIFEST.MF", Encoding.ASCII.GetBytes("Manifest-Version: 1.0"), deflated);
                }

                var manager = new AssetsManager();
                manager.LoadFiles(apkPath);

                var texts = manager.assetsFileList.SelectMany(f => f.Objects).OfType<TextAsset>()
                    .ToDictionary(t => t.m_Name, t => Encoding.UTF8.GetString(t.m_Script));
                Assert.Equal("stored bundle", texts["ApkStored"]);
                Assert.Equal("deflated bundle", texts["ApkDeflated"]);
                Assert.Equal("loose serialized file", texts["LooseLevel"]);

                var looseLevel = Assert.Single(manager.assetsFileList, f => f.fileName == "level0");
                Assert.IsType<SubStream>(looseLevel.reader.BaseStream);

                var resourceKeys = ((System.Collections.Concurrent.ConcurrentDictionary<string, BinaryReader>)typeof(AssetsManager)
                    .GetField("resourceFileReaders", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(manager)!).Keys.ToList();
                Assert.Contains(resourceKeys, k => k.EndsWith("sharedassets0.assets.resS", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(resourceKeys, k => k.Contains("classes.dex", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(resourceKeys, k => k.Contains("libmain.so", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(resourceKeys, k => k.Contains("icon.png", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(resourceKeys, k => k.Contains("MANIFEST", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Fact]
        public void AssetsManager_PlainZip_StillLoadsUnityFilesFromAnyFolder()
        {
            var tempDir = CreateTempDirectory("PlainZip");
            try
            {
                var serialized = BuildSerializedFileWithObjects(
                    "2021.3.16f1",
                    new[] { (PathId: 30L, ClassId: (int)ClassIDType.TextAsset, Data: BuildTextAssetBinary("ZipAsset", "from plain zip"), IncludeTextAssetTypeTree: true) });
                var zipPath = Path.Combine(tempDir, "mods.zip");
                using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
                {
                    using var stream = archive.CreateEntry("bundles/CAB-zip01", System.IO.Compression.CompressionLevel.Optimal).Open();
                    stream.Write(BuildUnityFsBundle("CAB-zip01", serialized, 512));
                }

                var manager = new AssetsManager();
                manager.LoadFiles(zipPath);
                var textAsset = Assert.Single(manager.assetsFileList.SelectMany(f => f.Objects).OfType<TextAsset>());
                Assert.Equal("from plain zip", Encoding.UTF8.GetString(textAsset.m_Script));
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }
        }

        [Fact]
        public void ZipStoredEntryIndex_LocatesStoredDataExactly_AndRejectsNonZip()
        {
            using var zipBytes = new MemoryStream();
            var storedPayload = Enumerable.Range(0, 3000).Select(i => (byte)(i * 31)).ToArray();
            using (var archive = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = archive.CreateEntry("dir/stored.bin", System.IO.Compression.CompressionLevel.NoCompression).Open())
                {
                    s.Write(storedPayload);
                }
                using (var s = archive.CreateEntry("dir/deflated.bin", System.IO.Compression.CompressionLevel.Optimal).Open())
                {
                    s.Write(new byte[5000]);
                }
                using (var s = archive.CreateEntry("unicodé/名前.bin", System.IO.Compression.CompressionLevel.NoCompression).Open())
                {
                    s.Write(new byte[] { 1, 2, 3 });
                }
            }

            var index = ZipStoredEntryIndex.TryCreate(zipBytes);
            Assert.NotNull(index);
            Assert.Equal(2, index!.StoredEntryCount);
            Assert.True(index.TryGetStoredData("dir/stored.bin", out var offset, out var size));
            Assert.Equal(storedPayload.Length, size);
            Assert.Equal(storedPayload, zipBytes.ToArray().AsSpan((int)offset, (int)size).ToArray());
            Assert.True(index.TryGetStoredData("unicodé/名前.bin", out var unicodeOffset, out _));
            Assert.Equal(new byte[] { 1, 2, 3 }, zipBytes.ToArray().AsSpan((int)unicodeOffset, 3).ToArray());
            Assert.False(index.TryGetStoredData("dir/deflated.bin", out _, out _));
            Assert.False(index.TryGetStoredData("missing.bin", out _, out _));

            Assert.Null(ZipStoredEntryIndex.TryCreate(new MemoryStream(Enumerable.Repeat((byte)0xAB, 4096).ToArray())));
            Assert.Null(ZipStoredEntryIndex.TryCreate(new MemoryStream(new byte[10])));
        }

        private static byte[] CompressZstd(byte[] data)
        {
            using var compressor = new ZstdSharp.Compressor(3);
            var bound = ZstdSharp.Compressor.GetCompressBound(data.Length);
            var output = new byte[bound];
            var written = compressor.Wrap(data, 0, data.Length, output, 0, output.Length);
            return output.AsSpan(0, written).ToArray();
        }

        /// <summary>
        /// Builds a UnityFS (format 6) bundle with one node, the payload split into blocks of
        /// <paramref name="blockSize"/> bytes, and the header <c>size</c> field set to the bundle length.
        /// </summary>
        private static byte[] BuildUnityFsBundle(
            string nodePath,
            byte[] nodePayload,
            int blockSize,
            int blockCompressionId = 0,
            Func<byte[], byte[]>? compressBlock = null,
            int blocksInfoCompressionId = 0,
            Func<byte[], byte[]>? compressBlocksInfo = null)
        {
            var blocks = new List<(byte[] Data, int UncompressedSize)>();
            for (var offset = 0; offset < nodePayload.Length; offset += blockSize)
            {
                var chunk = nodePayload.AsSpan(offset, Math.Min(blockSize, nodePayload.Length - offset)).ToArray();
                blocks.Add((compressBlock != null ? compressBlock(chunk) : chunk, chunk.Length));
            }

            byte[] blocksInfo;
            using (var ms = new MemoryStream())
            {
                Span<byte> big = stackalloc byte[8];
                ms.Write(new byte[16]);
                BinaryPrimitives.WriteInt32BigEndian(big, blocks.Count);
                ms.Write(big.Slice(0, 4));
                foreach (var (data, uncompressedSize) in blocks)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(big, (uint)uncompressedSize);
                    ms.Write(big.Slice(0, 4));
                    BinaryPrimitives.WriteUInt32BigEndian(big, (uint)data.Length);
                    ms.Write(big.Slice(0, 4));
                    BinaryPrimitives.WriteUInt16BigEndian(big, (ushort)(compressBlock != null ? blockCompressionId : 0));
                    ms.Write(big.Slice(0, 2));
                }

                BinaryPrimitives.WriteInt32BigEndian(big, 1);
                ms.Write(big.Slice(0, 4));
                BinaryPrimitives.WriteInt64BigEndian(big, 0L);
                ms.Write(big);
                BinaryPrimitives.WriteInt64BigEndian(big, nodePayload.Length);
                ms.Write(big);
                BinaryPrimitives.WriteUInt32BigEndian(big, 4u);
                ms.Write(big.Slice(0, 4));
                ms.Write(Encoding.UTF8.GetBytes(nodePath));
                ms.WriteByte(0);
                blocksInfo = ms.ToArray();
            }

            var storedBlocksInfo = compressBlocksInfo != null ? compressBlocksInfo(blocksInfo) : blocksInfo;
            var flags = (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined | (uint)(compressBlocksInfo != null ? blocksInfoCompressionId : 0);

            using var output = new MemoryStream();
            Span<byte> buf = stackalloc byte[8];
            output.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            BinaryPrimitives.WriteUInt32BigEndian(buf, 6u);
            output.Write(buf.Slice(0, 4));
            output.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            output.Write(Encoding.ASCII.GetBytes("2021.3.16f1\0"));
            var sizePosition = output.Position;
            output.Write(new byte[8]);
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)storedBlocksInfo.Length);
            output.Write(buf.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)blocksInfo.Length);
            output.Write(buf.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(buf, flags);
            output.Write(buf.Slice(0, 4));
            output.Write(storedBlocksInfo);
            foreach (var (data, _) in blocks)
            {
                output.Write(data);
            }

            var bytes = output.ToArray();
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan((int)sizePosition, 8), bytes.Length);
            return bytes;
        }

        private static void TryDeleteTempDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
