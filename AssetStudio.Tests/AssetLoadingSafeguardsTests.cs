using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class AssetLoadingSafeguardsTests
    {
        // =========================================================================
        // 1. Large legitimate Unity 6 objects (> 512 MiB) in BinaryReaderExtensions
        // =========================================================================

        [Fact]
        public void BinaryReaderExtensions_SeekableUnityReaders_AllowOver512MiBWithinBounds_WhileGuardingUntrustedAndTruncatedStreams()
        {
            const uint objectByteSize = 760_000_000u;
            const int validUintCount = 189_052_886; // ~756 MB (756,211,544 bytes)
            const int validByteCount = 600_000_000; // ~572 MiB
            const long virtualLength = 800_000_000L;

            var sf = CreateMinimalSerializedFile("6000.0.28f1");
            var objInfo = new ObjectInfo
            {
                m_PathID = 101,
                byteStart = 128,
                byteSize = objectByteSize,
                typeID = (int)ClassIDType.AnimationClip,
                classID = (int)ClassIDType.AnimationClip
            };

            // ObjectReader: allows > 512 MiB when within byteSize and stream length
            using (var stream = new VirtualStream(virtualLength, canSeek: true))
            using (var endian = new EndianBinaryReader(stream, EndianType.LittleEndian))
            using (var objReader = new ObjectReader(endian, sf, objInfo))
            {
                objReader.Position = objInfo.byteStart + 64;
                objReader.CheckArrayLength(validUintCount, sizeof(uint));
                Assert.Throws<EndOfStreamException>(() => objReader.CheckArrayLength(195_000_000, sizeof(uint)));
                Assert.Throws<OverflowException>(() => objReader.CheckArrayLength(int.MaxValue, sizeof(uint)));
            }

            // Seekable EndianBinaryReader: allows > 512 MiB when within remaining stream length
            using (var stream = new VirtualStream(virtualLength, canSeek: true))
            using (var endian = new EndianBinaryReader(stream, EndianType.LittleEndian))
            {
                endian.CheckArrayLength(validByteCount, sizeof(byte));
                endian.CheckArrayLength(validUintCount, sizeof(uint));
                Assert.Throws<OverflowException>(() => endian.CheckArrayLength(int.MaxValue, sizeof(uint)));
            }

            // Truncated seekable EndianBinaryReader: huge corrupt count throws OverflowException, small over-EOF throws EndOfStreamException
            using (var shortStream = new VirtualStream(4096, canSeek: true))
            using (var shortEndian = new EndianBinaryReader(shortStream, EndianType.LittleEndian))
            {
                Assert.Throws<OverflowException>(() => shortEndian.CheckArrayLength(validByteCount, sizeof(byte)));
                Assert.Throws<EndOfStreamException>(() => shortEndian.CheckArrayLength(8192, sizeof(byte)));
            }

            // Unseekable EndianBinaryReader: retains 512 MiB OverflowException guard
            using (var unseekable = new VirtualStream(virtualLength, canSeek: false))
            using (var unseekableEndian = new EndianBinaryReader(unseekable, EndianType.LittleEndian))
            {
                Assert.Throws<OverflowException>(() => unseekableEndian.CheckArrayLength(validByteCount, sizeof(byte)));
            }

            // Plain BinaryReader (untrusted generic reader): retains 512 MiB OverflowException guard
            using (var stream = new VirtualStream(virtualLength, canSeek: true))
            using (var rawReader = new BinaryReader(stream))
            {
                Assert.Throws<OverflowException>(() => rawReader.CheckArrayLength(validByteCount, sizeof(byte)));
            }
        }

        // =========================================================================
        // 2. External .assets (m_Externals) cascade control in AssetsManager
        // =========================================================================

        [Fact]
        public void AssetsManager_ExternalCascadeControls_RespectToggleCapPromptAndLoadFolder()
        {
            var tempDir = CreateTempDirectory("external_cascade");
            try
            {
                // Chain: part0.assets -> part1.assets -> part2.assets -> part3.assets -> part4.assets
                // Plus a missing built-in reference in part0 that must NOT be counted as skipped.
                var part0 = Path.Combine(tempDir, "part0.assets");
                var part1 = Path.Combine(tempDir, "part1.assets");
                var part2 = Path.Combine(tempDir, "part2.assets");
                var part3 = Path.Combine(tempDir, "part3.assets");
                var part4 = Path.Combine(tempDir, "part4.assets");

                File.WriteAllBytes(part0, BuildSerializedFileWithExternals("2022.3.40f1", "part1.assets", "unity default resources"));
                File.WriteAllBytes(part1, BuildSerializedFileWithExternals("2022.3.40f1", "part2.assets"));
                File.WriteAllBytes(part2, BuildSerializedFileWithExternals("2022.3.40f1", "part3.assets"));
                File.WriteAllBytes(part3, BuildSerializedFileWithExternals("2022.3.40f1", "part4.assets"));
                File.WriteAllBytes(part4, BuildSerializedFileWithExternals("2022.3.40f1"));

                // (a) AutoLoadExternalDependencies = false -> loads ONLY part0, skips 1 real sibling external
                using (var am = new AssetsManager { AutoLoadExternalDependencies = false })
                {
                    am.LoadFiles(part0);
                    Assert.Single(am.assetsFileList);
                    Assert.Equal("part0.assets", am.assetsFileList[0].fileName);
                    Assert.Equal(1, am.LastSkippedExternalDependencyCount);
                }

                // (b) MaxAutoLoadExternalDependencies = 2 -> loads part0 + part1 + part2 (3 files total)
                using (var am = new AssetsManager { AutoLoadExternalDependencies = true, MaxAutoLoadExternalDependencies = 2 })
                {
                    am.LoadFiles(part0);
                    Assert.Equal(3, am.assetsFileList.Count);
                    Assert.True(am.LastSkippedExternalDependencyCount >= 1);
                }

                // (c) Interactive cascade prompt returning false at threshold = 1 -> prompted once, stops at 2 files
                int declinePrompts = 0;
                using (var am = new AssetsManager
                {
                    AutoLoadExternalDependencies = true,
                    ExternalCascadePromptThreshold = 1,
                    ConfirmExternalCascadeCallback = (enqueued, _) =>
                    {
                        declinePrompts++;
                        Assert.Equal(1, enqueued);
                        return false;
                    }
                })
                {
                    am.LoadFiles(part0);
                    Assert.Equal(1, declinePrompts);
                    Assert.Equal(2, am.assetsFileList.Count);
                    Assert.True(am.LastSkippedExternalDependencyCount >= 1);
                }

                // (d) Interactive cascade prompt returning true at threshold = 1 -> prompted once, loads all 5 files
                int approvePrompts = 0;
                using (var am = new AssetsManager
                {
                    AutoLoadExternalDependencies = true,
                    ExternalCascadePromptThreshold = 1,
                    ConfirmExternalCascadeCallback = (_, _) =>
                    {
                        approvePrompts++;
                        return true;
                    }
                })
                {
                    am.LoadFiles(part0);
                    Assert.Equal(1, approvePrompts);
                    Assert.Equal(5, am.assetsFileList.Count);
                    Assert.Equal(0, am.LastSkippedExternalDependencyCount);
                }

                // (e) LoadFolder never invokes the Load-file cascade confirmation callback
                int folderPrompts = 0;
                using (var am = new AssetsManager
                {
                    AutoLoadExternalDependencies = true,
                    ExternalCascadePromptThreshold = 1,
                    ConfirmExternalCascadeCallback = (_, _) =>
                    {
                        folderPrompts++;
                        return false;
                    }
                })
                {
                    am.LoadFolder(tempDir);
                    Assert.Equal(0, folderPrompts);
                    Assert.Equal(5, am.assetsFileList.Count);
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void WindowOptionsViewModel_PersistsAutoLoadExternalDependenciesSetting()
        {
            var settings = new AvaloniaAppSettings();
            Assert.True(settings.AutoLoadExternalDependencies);

            var vm = new WindowOptionsViewModel();
            vm.LoadFromSettings(settings);
            Assert.True(vm.AutoLoadExternalDependencies);

            bool? observed = null;
            vm.AutoLoadExternalDependenciesChanged += value => observed = value;

            vm.AutoLoadExternalDependencies = false;
            Assert.Equal(false, observed);

            vm.ApplyToSettings(settings);
            Assert.False(settings.AutoLoadExternalDependencies);

            var vmReloaded = new WindowOptionsViewModel();
            vmReloaded.LoadFromSettings(settings);
            Assert.False(vmReloaded.AutoLoadExternalDependencies);
        }

        // =========================================================================
        // 3. UnityPlayer.dll version fallback hardening (plain Data\, stripped data.unity3d, nested subdirs)
        // =========================================================================

        [Fact]
        public void AssetsManager_PlainDataLayoutWithStrippedDataUnity3dAndEncryptedGlobalGameManagers_FallsBackToUnityPlayerDll()
        {
            var tempDir = CreateTempDirectory("unityplayer_plain_data");
            try
            {
                var gameRoot = Path.Combine(tempDir, "SampleTitle");
                var dataDir = Path.Combine(gameRoot, "Data");
                var bundleDir = Path.Combine(dataDir, "StreamingAssets", "aa");
                Directory.CreateDirectory(bundleDir);

                // Encrypted/unreadable globalgamemanagers
                File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), new byte[] { 0xFA, 0xCE, 0xB0, 0x0C, 0x99, 0x88, 0x77, 0x66 });

                // Stripped data.unity3d bundle (version 0.0.0) that must NOT short-circuit fallback
                var strippedInnerSf = BuildSerializedFileWithExternals("0.0.0");
                File.WriteAllBytes(Path.Combine(dataDir, "data.unity3d"), BuildUncompressedUnityFsBundle("CAB-data01", strippedInnerSf, "0.0.0"));

                // Valid PE UnityPlayer.dll stamped with 2022.3.41f1
                File.WriteAllBytes(
                    Path.Combine(gameRoot, "UnityPlayer.dll"),
                    BuildMinimalVersionedPeDll(2022, 3, 41, 1001, "2022.3.41f1 (0123456789ab)", "2022.3.41.1001"));

                var bundlePath = Path.Combine(bundleDir, "content.bundle");
                File.WriteAllBytes(bundlePath, BuildUncompressedUnityFsBundle("CAB-content01", strippedInnerSf, "0.0.0"));

                using var am = new AssetsManager();
                am.LoadFiles(bundlePath);

                var sf = Assert.Single(am.assetsFileList);
                Assert.Equal("2022.3.41f1", sf.unityVersion);
                Assert.Equal(new[] { 2022, 3, 41, 1 }, sf.version.Take(4).ToArray());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void AssetsManager_NestedSubdirectoryUnityPlayerDll_ResolvesStrippedBundleVersionViaProjectRoot()
        {
            var tempDir = CreateTempDirectory("unityplayer_nested");
            try
            {
                var outerRoot = Path.Combine(tempDir, "BundleContainer");
                var nestedBuild = Path.Combine(outerRoot, "ClientBuild");
                var dataDir = Path.Combine(nestedBuild, "App_Data");
                var externalBundles = Path.Combine(outerRoot, "LooseBundles");
                Directory.CreateDirectory(dataDir);
                Directory.CreateDirectory(externalBundles);

                File.WriteAllBytes(Path.Combine(dataDir, "globalgamemanagers"), new byte[] { 0x13, 0x37, 0x42, 0x99, 0xAA, 0xBB, 0xCC, 0xDD });
                File.WriteAllBytes(
                    Path.Combine(nestedBuild, "UnityPlayer.dll"),
                    BuildMinimalVersionedPeDll(6000, 0, 34, 2025, "6000.0.34f1 (fedcba987654)", "6000.0.34.2025"));

                var strippedSf = BuildSerializedFileWithExternals("0.0.0");
                var looseBundlePath = Path.Combine(externalBundles, "loose_stripped.bundle");
                File.WriteAllBytes(looseBundlePath, BuildUncompressedUnityFsBundle("CAB-loose01", strippedSf, "0.0.0"));

                using var am = new AssetsManager { ProjectRoot = outerRoot };
                am.LoadFiles(looseBundlePath);

                var sf = Assert.Single(am.assetsFileList);
                Assert.Equal("6000.0.34f1", sf.unityVersion);
                Assert.Equal(new[] { 6000, 0, 34, 1 }, sf.version.Take(4).ToArray());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static SerializedFile CreateMinimalSerializedFile(string unityVersion)
        {
            var bytes = BuildSerializedFileWithExternals(unityVersion);
            var ms = new MemoryStream(bytes);
            var reader = new FileReader("minimal.assets", ms);
            return new SerializedFile(reader, new AssetsManager());
        }

        private static byte[] BuildSerializedFileWithExternals(string unityVersion, params string[] externalFileNames)
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
                metaBw.Write(externalFileNames.Length); // externalsCount
                foreach (var ext in externalFileNames)
                {
                    metaBw.Write((byte)0); // tempEmpty (string to null)
                    metaBw.Write(new byte[16]); // guid
                    metaBw.Write(0); // type
                    metaBw.Write(Encoding.UTF8.GetBytes(ext));
                    metaBw.Write((byte)0); // pathName
                }
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
                biMs.Write(new byte[16]);
                WriteInt32BE(biMs, 1);
                WriteUInt32BE(biMs, (uint)nodePayload.Length);
                WriteUInt32BE(biMs, (uint)nodePayload.Length);
                WriteUInt16BE(biMs, 0);
                WriteInt32BE(biMs, 1);
                WriteInt64BE(biMs, 0L);
                WriteInt64BE(biMs, nodePayload.Length);
                WriteUInt32BE(biMs, 4u);
                biMs.Write(Encoding.UTF8.GetBytes(nodePath));
                biMs.WriteByte(0);
                blocksInfo = biMs.ToArray();
            }

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            WriteUInt32BE(ms, 6u);
            ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            ms.Write(Encoding.ASCII.GetBytes(unityRevision + "\0"));
            var sizePos = ms.Position;
            WriteInt64BE(ms, 0L);
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

            const uint rsrcRva = 0x1000;
            const int dataEntryOffset = 0x48;
            const int blobOffset = 0x58;
            var rsrcLen = blobOffset + versionBlob.Length;
            var rsrcAlignedLen = (rsrcLen + 0x1FF) & ~0x1FF;
            var rsrc = new byte[rsrcAlignedLen];

            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(0x0E, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x10, 4), 16u);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x14, 4), 0x80000018u);

            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(0x26, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x28, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x2C, 4), 0x80000030u);

            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(0x3E, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x40, 4), 0x0409u);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x44, 4), (uint)dataEntryOffset);

            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataEntryOffset, 4), rsrcRva + (uint)blobOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataEntryOffset + 4, 4), (uint)versionBlob.Length);
            Buffer.BlockCopy(versionBlob, 0, rsrc, blobOffset, versionBlob.Length);

            var pe = new byte[0x200 + rsrcAlignedLen];
            pe[0] = (byte)'M';
            pe[1] = (byte)'Z';
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x3C, 4), 0x80u);

            pe[0x80] = (byte)'P';
            pe[0x81] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84, 2), 0x014C);
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x86, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x94, 2), 0x00E0);
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x96, 2), 0x2102);

            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x98, 2), 0x010B);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xB8, 4), 0x1000u);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xBC, 4), 0x0200u);
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0xC0, 2), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0xC8, 2), 4);
            uint sizeOfImage = rsrcRva + (uint)((rsrcAlignedLen + 0xFFF) & ~0xFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xD0, 4), sizeOfImage);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xD4, 4), 0x0200u);
            BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0xDC, 2), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0xF4, 4), 16u);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x108, 4), rsrcRva);
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x10C, 4), (uint)rsrcLen);

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
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(0, 4), 0xFEEF04BDu);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(4, 4), 0x00010000u);
            uint msVer = ((uint)major << 16) | minor;
            uint lsVer = ((uint)build << 16) | priv;
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(8, 4), msVer);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(12, 4), lsVer);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(16, 4), msVer);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(20, 4), lsVer);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(24, 4), 0x3Fu);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(32, 4), 4u);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(36, 4), 2u);

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
            bw.Write((ushort)0);
            bw.Write((ushort)(value.Length + 1));
            bw.Write((ushort)1);
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
            bw.Write((ushort)0);
            bw.Write((ushort)valueBytes.Length);
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
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_Safeguards_{prefix}_{Guid.NewGuid():N}");
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

        private sealed class VirtualStream : Stream
        {
            private readonly long _length;
            private readonly bool _canSeek;
            private long _position;

            public VirtualStream(long length, bool canSeek)
            {
                _length = length;
                _canSeek = canSeek;
            }

            public override bool CanRead => true;
            public override bool CanSeek => _canSeek;
            public override bool CanWrite => false;
            public override long Length => _canSeek ? _length : throw new NotSupportedException();
            public override long Position
            {
                get => _canSeek ? _position : throw new NotSupportedException();
                set => _position = _canSeek ? value : throw new NotSupportedException();
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
                if (!_canSeek) throw new NotSupportedException();
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
