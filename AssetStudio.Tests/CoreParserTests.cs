using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace AssetStudio.Tests
{
    [Collection("ResourceReaderXorHeuristic")]
    public class CoreParserTests
    {
        [Fact]
        public void EndianBinaryReader_BigEndian_ReadsAllPrimitiveTypes()
        {
            const short expectedI16 = -0x1234;
            const ushort expectedU16 = 0xABCD;
            const int expectedI32 = -0x12345678;
            const uint expectedU32 = 0x89ABCDEFu;
            const long expectedI64 = -0x0102030405060708L;
            const ulong expectedU64 = 0xFEDCBA9876543210UL;
            const float expectedF32 = 3.1415927f;
            const double expectedF64 = -123456.789012345;

            var buffer = new byte[2 + 2 + 4 + 4 + 8 + 8 + 4 + 8];
            int offset = 0;
            BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(offset, 2), expectedI16); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), expectedU16); offset += 2;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), expectedI32); offset += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), expectedU32); offset += 4;
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), expectedI64); offset += 8;
            BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), expectedU64); offset += 8;
            BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(offset, 4), expectedF32); offset += 4;
            BinaryPrimitives.WriteDoubleBigEndian(buffer.AsSpan(offset, 8), expectedF64);

            using var ms = new MemoryStream(buffer);
            using var reader = new EndianBinaryReader(ms, EndianType.BigEndian);

            Assert.Equal(expectedI16, reader.ReadInt16());
            Assert.Equal(expectedU16, reader.ReadUInt16());
            Assert.Equal(expectedI32, reader.ReadInt32());
            Assert.Equal(expectedU32, reader.ReadUInt32());
            Assert.Equal(expectedI64, reader.ReadInt64());
            Assert.Equal(expectedU64, reader.ReadUInt64());
            Assert.Equal(expectedF32, reader.ReadSingle());
            Assert.Equal(expectedF64, reader.ReadDouble());
            Assert.Equal(buffer.Length, reader.Position);
        }

        [Fact]
        public void EndianBinaryReader_LittleEndian_ReadsAllPrimitiveTypes()
        {
            const short expectedI16 = -0x2345;
            const ushort expectedU16 = 0xBEEF;
            const int expectedI32 = -0x76543210;
            const uint expectedU32 = 0xCAFEBABEu;
            const long expectedI64 = 0x1122334455667788L;
            const ulong expectedU64 = 0xDEADBEEFCAFEBABEUL;
            const float expectedF32 = -42.625f;
            const double expectedF64 = 987654321.1234567;

            var buffer = new byte[2 + 2 + 4 + 4 + 8 + 8 + 4 + 8];
            int offset = 0;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), expectedI16); offset += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), expectedU16); offset += 2;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), expectedI32); offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), expectedU32); offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), expectedI64); offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), expectedU64); offset += 8;
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), expectedF32); offset += 4;
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset, 8), expectedF64);

            using var ms = new MemoryStream(buffer);
            using var reader = new EndianBinaryReader(ms, EndianType.LittleEndian);

            Assert.Equal(expectedI16, reader.ReadInt16());
            Assert.Equal(expectedU16, reader.ReadUInt16());
            Assert.Equal(expectedI32, reader.ReadInt32());
            Assert.Equal(expectedU32, reader.ReadUInt32());
            Assert.Equal(expectedI64, reader.ReadInt64());
            Assert.Equal(expectedU64, reader.ReadUInt64());
            Assert.Equal(expectedF32, reader.ReadSingle());
            Assert.Equal(expectedF64, reader.ReadDouble());
            Assert.Equal(buffer.Length, reader.Position);
        }

        [Fact]
        public void BinaryReaderExtensions_ReadStringToNull_HandlesNormalEofAndPastEof()
        {
            // Normal null-terminated string followed by another token
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("Hello, Unity!\0Trailing")))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal("Hello, Unity!", br.ReadStringToNull());
                Assert.Equal(14, ms.Position);
                Assert.Equal("Trailing", br.ReadStringToNull());
                Assert.Equal(ms.Length, ms.Position);
            }

            // Empty null-terminated string
            using (var ms = new MemoryStream(new byte[] { 0x00, (byte)'A' }))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(string.Empty, br.ReadStringToNull());
                Assert.Equal(1, ms.Position);
            }

            // EOF without null terminator
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("NoNullHere")))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal("NoNullHere", br.ReadStringToNull());
                Assert.Equal(ms.Length, ms.Position);
            }

            // Position at EOF and past EOF must return empty string without hanging
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("abc")))
            using (var br = new BinaryReader(ms))
            {
                ms.Position = ms.Length;
                Assert.Equal(string.Empty, br.ReadStringToNull());

                ms.Position = ms.Length + 64;
                Assert.Equal(string.Empty, br.ReadStringToNull());
            }

            // Respects maxLength bound
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("abcdef\0")))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal("abc", br.ReadStringToNull(maxLength: 3));
            }
        }

        [Theory]
        [InlineData("2021.3.16f1", 2021, 3, 16, 1, false, false)]
        [InlineData("2021.3.16", 2021, 3, 16, 0, false, false)]
        [InlineData("2021.3", 2021, 3, 0, 0, false, false)]
        [InlineData("", 0, 0, 0, 0, false, false)]
        [InlineData("2020.3.1p4", 2020, 3, 1, 4, false, true)]
        [InlineData("2023.1.0a5", 2023, 1, 0, 5, true, false)]
        public void SerializedFile_SetVersion_HandlesStandardAndMalformedVersionsWithoutCrashing(
            string versionString,
            int expectedMajor,
            int expectedMinor,
            int expectedPatch,
            int expectedBuild,
            bool expectedIsAlpha,
            bool expectedIsPatch)
        {
            var serializedFile = CreateMinimalSerializedFile("2019.4.0f1");
            serializedFile.SetVersion(versionString);

            Assert.Equal(versionString, serializedFile.unityVersion);
            Assert.NotNull(serializedFile.version);
            Assert.True(serializedFile.version.Length >= 4);
            Assert.Equal(expectedMajor, serializedFile.version[0]);
            Assert.Equal(expectedMinor, serializedFile.version[1]);
            Assert.Equal(expectedPatch, serializedFile.version[2]);
            Assert.Equal(expectedBuild, serializedFile.version[3]);
            Assert.NotNull(serializedFile.buildType);
            Assert.Equal(expectedIsAlpha, serializedFile.buildType.IsAlpha);
            Assert.Equal(expectedIsPatch, serializedFile.buildType.IsPatch);
        }

        [Fact]
        public void SevenZipHelper_RejectsMaliciousDictionarySizeAndOversizedOutSize()
        {
            // Dictionary size > 128 MB in 1-arg StreamDecompress(MemoryStream)
            using (var maliciousDictStream = BuildLzmaHeaderStream(SevenZipHelper.MaxDictionarySize + 1, outSize: 64))
            {
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(maliciousDictStream));
            }

            using (var maxUIntDictStream = BuildLzmaHeaderStream(uint.MaxValue, outSize: 64))
            {
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(maxUIntDictStream));
            }

            // Oversized outSize (> int.MaxValue) and negative outSize
            using (var hugeOutSizeStream = BuildLzmaHeaderStream(dictionarySize: 1u << 16, outSize: (long)int.MaxValue + 1))
            {
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(hugeOutSizeStream));
            }

            using (var negativeOutSizeStream = BuildLzmaHeaderStream(dictionarySize: 1u << 16, outSize: -1L))
            {
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(negativeOutSizeStream));
            }

            // 4-arg StreamDecompress overload rejects dictionary size > 128 MB
            var propsOnly = new byte[16];
            propsOnly[0] = 0x5D; // lc=3, lp=0, pb=2
            BinaryPrimitives.WriteUInt32LittleEndian(propsOnly.AsSpan(1, 4), SevenZipHelper.MaxDictionarySize + 4096);
            using (var inStream = new MemoryStream(propsOnly))
            using (var outStream = new MemoryStream())
            {
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(inStream, outStream, propsOnly.Length, 64));
            }

            // 4-arg overload rejects invalid sizes
            using (var inStream = new MemoryStream(new byte[4]))
            using (var outStream = new MemoryStream())
            {
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(inStream, outStream, 4, 16));
                Assert.Throws<InvalidDataException>(() => SevenZipHelper.StreamDecompress(inStream, outStream, 10, -1));
            }
        }

        [Fact]
        public void ImportHelper_DecompressGZip_RoundTripsPayloadAccurately()
        {
            var original = CreateDeterministicPayload(4096);
            var compressedStream = new MemoryStream();
            using (var gzip = new GZipStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(original, 0, original.Length);
            }
            compressedStream.Position = 0;

            var inputReader = new FileReader("sample_asset.gz", compressedStream);
            Assert.Equal(FileType.GZipFile, inputReader.FileType);

            using var decompressedReader = ImportHelper.DecompressGZip(inputReader);
            var result = decompressedReader.ReadBytes((int)decompressedReader.BaseStream.Length);
            Assert.Equal(original, result);
        }

        [Fact]
        public void ImportHelper_DecompressBrotli_RoundTripsPayloadAccurately()
        {
            var original = CreateDeterministicPayload(4096);
            var compressedStream = new MemoryStream();
            using (var brotli = new BrotliStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            {
                brotli.Write(original, 0, original.Length);
            }
            compressedStream.Position = 0;

            var inputReader = new FileReader("sample_asset.br", compressedStream);
            using var decompressedReader = ImportHelper.DecompressBrotli(inputReader);
            var result = decompressedReader.ReadBytes((int)decompressedReader.BaseStream.Length);
            Assert.Equal(original, result);
        }

        [Fact]
        public void ResourceReader_ConcurrentReadSegment_IsThreadSafeAndExact()
        {
            var sourceData = CreateDeterministicPayload(8192);
            var reader = new ResourceReader(sourceData);
            var failures = new ConcurrentBag<string>();

            Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 16 }, iteration =>
            {
                int offset = (iteration * 137) % (sourceData.Length - 256);
                int length = 32 + (iteration * 29) % 224;
                int dstOffset = iteration % 16;
                var buffer = new byte[dstOffset + length + 16];
                Array.Fill(buffer, (byte)0xCC);

                reader.ReadSegment(offset, buffer, dstOffset, length);

                for (int i = 0; i < dstOffset; i++)
                {
                    if (buffer[i] != 0xCC)
                    {
                        failures.Add($"Leading guard byte corrupted at iteration {iteration}");
                        return;
                    }
                }
                for (int i = 0; i < length; i++)
                {
                    if (buffer[dstOffset + i] != sourceData[offset + i])
                    {
                        failures.Add($"Mismatch at iteration {iteration}, index {i}");
                        return;
                    }
                }
                for (int i = dstOffset + length; i < buffer.Length; i++)
                {
                    if (buffer[i] != 0xCC)
                    {
                        failures.Add($"Trailing guard byte corrupted at iteration {iteration}");
                        return;
                    }
                }
            });

            Assert.Empty(failures);
            Assert.Throws<EndOfStreamException>(() => reader.ReadSegment(sourceData.Length - 4, new byte[16], 0, 16));
            Assert.Throws<EndOfStreamException>(() => reader.ReadSegment(-1, new byte[16], 0, 4));
        }

        [Fact]
        public void ResourceReader_GetData_WithOversizedPoolBuffer_DoesNotCorruptTrailingBytes()
        {
            // Raw byte[] constructor
            var payload = CreateDeterministicPayload(128);
            var rawReader = new ResourceReader(payload);
            var oversizedBuffer = new byte[512];
            Array.Fill(oversizedBuffer, (byte)0xAA);

            rawReader.GetData(oversizedBuffer);

            Assert.Equal(payload, oversizedBuffer.AsSpan(0, payload.Length).ToArray());
            for (int i = payload.Length; i < oversizedBuffer.Length; i++)
            {
                Assert.Equal(0xAA, oversizedBuffer[i]);
            }

            // Stream-backed ResourceReader with >50% 0x01 XOR trigger (when opt-in heuristic is enabled) must only transform [0..toRead)
            var xorPayload = new byte[64];
            Array.Fill(xorPayload, (byte)0x01);
            xorPayload[0] = 0x42;

            using var ms = new MemoryStream(xorPayload);
            using var br = new BinaryReader(ms);
            var streamReader = new ResourceReader(br, 0, xorPayload.Length);
            var poolBuffer = new byte[256];
            Array.Fill(poolBuffer, (byte)0x55);

            bool previousXorSetting = ResourceReader.EnableXorInversionHeuristic;
            ResourceReader.EnableXorInversionHeuristic = true;
            try
            {
                streamReader.GetData(poolBuffer);
            }
            finally
            {
                ResourceReader.EnableXorInversionHeuristic = previousXorSetting;
            }

            Assert.Equal((byte)(0x42 ^ 0xFF), poolBuffer[0]);
            for (int i = 1; i < xorPayload.Length; i++)
            {
                Assert.Equal((byte)(0x01 ^ 0xFF), poolBuffer[i]);
            }
            for (int i = xorPayload.Length; i < poolBuffer.Length; i++)
            {
                Assert.Equal(0x55, poolBuffer[i]);
            }

            // Undersized buffer must throw ArgumentOutOfRangeException
            Assert.Throws<ArgumentOutOfRangeException>(() => rawReader.GetData(new byte[payload.Length - 1]));
        }

        [Fact]
        public void DecryptionHelper_ConcurrentCallsAndClear_AreStableAndDecryptMatchingCatalogEntry()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudioTests_Decryption_" + Guid.NewGuid().ToString("N"));
            var embedDir = Path.Combine(tempRoot, "Embed");
            Directory.CreateDirectory(embedDir);

            try
            {
                const string bundleFileName = "sample_test.bundle";
                const string bundleName = "testbundle";
                const string hash = "abcdef01";
                const string crc = "12345";
                string catalogPath = Path.Combine(tempRoot, "catalog.json");
                string fakeBundlePath = Path.Combine(embedDir, bundleFileName);

                File.WriteAllText(catalogPath, BuildSyntheticAddressablesCatalog(bundleFileName, bundleName, hash, crc), Encoding.UTF8);

                var encryptedBytes = new byte[64];
                Encoding.ASCII.GetBytes("Encryption").CopyTo(encryptedBytes, 0);

                var plainBytes = new byte[64];
                Encoding.ASCII.GetBytes("UnityFS\0").CopyTo(plainBytes, 0);

                DecryptionHelper.Clear();

                // Concurrent lookup + Clear stress test
                var exceptions = new ConcurrentBag<Exception>();
                Parallel.For(0, 128, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    try
                    {
                        if (i % 17 == 0)
                        {
                            DecryptionHelper.Clear();
                        }

                        using var encStream = new MemoryStream(encryptedBytes);
                        var resolved = DecryptionHelper.CheckAndDecryptStream(encStream, fakeBundlePath);
                        Assert.NotNull(resolved);
                        if (resolved is DecryptedStream ds)
                        {
                            Assert.Equal($"{bundleName}_{hash}-{crc}", ds.Token);
                        }

                        using var plainStream = new MemoryStream(plainBytes);
                        var untouched = DecryptionHelper.CheckAndDecryptStream(plainStream, fakeBundlePath);
                        Assert.Same(plainStream, untouched);
                        Assert.Equal(0, plainStream.Position);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Assert.Empty(exceptions);

                // Deterministic post-Clear verification
                DecryptionHelper.Clear();
                using (var encStream = new MemoryStream(encryptedBytes))
                {
                    var result = DecryptionHelper.CheckAndDecryptStream(encStream, fakeBundlePath);
                    var decrypted = Assert.IsType<DecryptedStream>(result);
                    Assert.Equal($"{bundleName}_{hash}-{crc}", decrypted.Token);
                }

                // Null/short stream edge cases
                Assert.Null(DecryptionHelper.CheckAndDecryptStream(null, fakeBundlePath));
                using (var shortStream = new MemoryStream(new byte[10]))
                {
                    Assert.Same(shortStream, DecryptionHelper.CheckAndDecryptStream(shortStream, fakeBundlePath));
                }
            }
            finally
            {
                DecryptionHelper.Clear();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Fact]
        public void DecryptionPipeline_OffsetSkip_KnownPlaintextXor_DeclarativeProfile_AndBarrierDetection_WorkAndCloneCleanly()
        {
            DecryptionHelper.Clear();
            try
            {
                byte[] validUnityFsBundle = BuildSyntheticUnityFsHeaderPayload(totalSize: 128);

                // 1. OffsetHeaderSkipTransformProvider: 96-byte fake prefix before valid UnityFS header
                byte[] prefixedBytes = new byte[96 + validUnityFsBundle.Length];
                for (int i = 0; i < 96; i++)
                {
                    prefixedBytes[i] = (byte)(0xA0 + (i % 23));
                }
                Buffer.BlockCopy(validUnityFsBundle, 0, prefixedBytes, 96, validUnityFsBundle.Length);

                using (var prefixedReader = new FileReader("prefixed_sample.bundle", new MemoryStream(prefixedBytes)))
                {
                    Assert.Equal("OffsetHeaderSkip", prefixedReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, prefixedReader.FileType);
                    Assert.Equal(validUnityFsBundle.Length, prefixedReader.BaseStream.Length);

                    using var clonedReader = prefixedReader.Clone();
                    Assert.Equal(FileType.BundleFile, clonedReader.FileType);
                    Assert.Equal("UnityFS", clonedReader.ReadStringToNull());
                }

                // 2. KnownPlaintextXorTransformProvider: repeating 4-byte XOR key over valid UnityFS header
                byte[] xorKey4 = { 0x12, 0x34, 0x56, 0x78 };
                byte[] xorBundle = (byte[])validUnityFsBundle.Clone();
                UnityHeaderProbe.XorBytesRepeating(xorBundle, 0, xorKey4);

                using (var xorReader = new FileReader("xor_sample.bundle", new MemoryStream(xorBundle)))
                {
                    Assert.Equal("KnownPlaintextXor", xorReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, xorReader.FileType);

                    using var clonedXor = xorReader.Clone();
                    Assert.Equal(FileType.BundleFile, clonedXor.FileType);
                    byte[] roundtrip = clonedXor.ReadBytes(validUnityFsBundle.Length);
                    Assert.Equal(validUnityFsBundle, roundtrip);
                }

                // 3. DeclarativeProfileTransformProvider: range-limited XOR on first 64 bytes
                byte[] rangeXorKey = { 0x4E, 0x50, 0x91, 0x0F };
                byte[] rangeEncrypted = (byte[])validUnityFsBundle.Clone();
                UnityHeaderProbe.XorBytesRepeating(rangeEncrypted.AsSpan(0, 64), 0, rangeXorKey);

                DecryptionHelper.RegisterProfile(new DecryptionProfile
                {
                    Id = "range-header-xor-64",
                    FileNamePattern = "range_*.bundle",
                    XorKeyHex = "4E50910F",
                    RangeStart = 0,
                    RangeLength = 64
                });

                using (var profileReader = new FileReader("range_test.bundle", new MemoryStream(rangeEncrypted)))
                {
                    Assert.Equal("DeclarativeProfile", profileReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, profileReader.FileType);
                    byte[] roundtrip = profileReader.ReadBytes(validUnityFsBundle.Length);
                    Assert.Equal(validUnityFsBundle, roundtrip);
                }

                // 4. Barrier detection: PartialHeaderEncryptedSerializedFile & VfsChunkContainer
                byte[] partialHeaderAsset = new byte[0x1200];
                for (int i = 0; i < 0x400; i++)
                {
                    partialHeaderAsset[i] = (byte)(((i * 73) + 0x4E) | 1); // non-zero high-density encrypted header
                }
                // 0x400..0x11FF remains zero-heavy structured table data
                partialHeaderAsset[0x400] = 0x0F;
                partialHeaderAsset[0x404] = 0x19;

                using (var barrierReader = new FileReader("globalgamemanagers", new MemoryStream(partialHeaderAsset)))
                {
                    Assert.Null(barrierReader.AppliedDecryptionProvider);
                    Assert.True(barrierReader.EncryptionBarrier.HasValue);
                    Assert.Equal("PartialHeaderEncryptedSerializedFile", barrierReader.EncryptionBarrier!.Value.ProviderId);
                }

                byte[] vfsChunk = CreateDeterministicPayload(256);
                using (var vfsReader = new FileReader("00ADE886AE9CC1BC.chk", new MemoryStream(vfsChunk)))
                {
                    Assert.True(vfsReader.EncryptionBarrier.HasValue);
                    Assert.Equal("VfsChunkContainer", vfsReader.EncryptionBarrier!.Value.ProviderId);
                }
            }
            finally
            {
                DecryptionHelper.Clear();
            }
        }

        [Fact]
        public void StreamTransformPipeline_DetectsMultiByteAndPrefixXorUnityFs_AndEncryptedBundleBarriers()
        {
            DecryptionHelper.Clear();
            try
            {
                byte[] nodePayload = CreateDeterministicPayload(4096);
                // Stamp a valid SerializedFile v22 header at offset 0 (matching real UnityFS serialized nodes with flags=4)
                BinaryPrimitives.WriteUInt32BigEndian(nodePayload.AsSpan(0, 4), 0u);
                BinaryPrimitives.WriteUInt32BigEndian(nodePayload.AsSpan(4, 4), 0u);
                BinaryPrimitives.WriteUInt32BigEndian(nodePayload.AsSpan(8, 4), 22u);
                BinaryPrimitives.WriteUInt32BigEndian(nodePayload.AsSpan(12, 4), 0u);
                nodePayload[16] = 0; // little endian
                BinaryPrimitives.WriteUInt32BigEndian(nodePayload.AsSpan(20, 4), 128u); // metadataSize
                BinaryPrimitives.WriteInt64BigEndian(nodePayload.AsSpan(24, 8), nodePayload.Length); // fileSize
                BinaryPrimitives.WriteInt64BigEndian(nodePayload.AsSpan(32, 8), 256L); // dataOffset
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-synthetic-xor-test", nodePayload);

                // 1. 4-byte repeating XOR over first 128 bytes only
                byte[] key4 = { 0x3A, 0x91, 0xC4, 0x0D };
                byte[] xor4Prefix128 = (byte[])plainBundle.Clone();
                UnityHeaderProbe.XorBytesRepeating(xor4Prefix128.AsSpan(0, 128), 0, key4);

                using (var r1 = new FileReader("prefix128.bundle", new MemoryStream(xor4Prefix128)))
                {
                    Assert.Equal("KnownPlaintextXor", r1.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, r1.FileType);
                    using var bundle1 = new BundleFile(r1);
                    Assert.Single(bundle1.fileList);
                    using var ms1 = new MemoryStream();
                    bundle1.fileList[0].stream.CopyTo(ms1);
                    Assert.Equal(nodePayload, ms1.ToArray());
                }

                // 2. 16-byte repeating XOR over first 256 bytes only
                byte[] key16 = { 0x5B, 0xE2, 0x17, 0x89, 0x44, 0xAD, 0x31, 0xF0, 0x62, 0x9E, 0x05, 0xC7, 0x19, 0x74, 0xB8, 0x2F };
                byte[] xor16Prefix256 = (byte[])plainBundle.Clone();
                UnityHeaderProbe.XorBytesRepeating(xor16Prefix256.AsSpan(0, 256), 0, key16);

                using (var r2 = new FileReader("prefix256.bundle", new MemoryStream(xor16Prefix256)))
                {
                    Assert.Equal("KnownPlaintextXor", r2.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, r2.FileType);
                    using var bundle2 = new BundleFile(r2);
                    Assert.Single(bundle2.fileList);
                    using var ms2 = new MemoryStream();
                    bundle2.fileList[0].stream.CopyTo(ms2);
                    Assert.Equal(nodePayload, ms2.ToArray());
                }

                // 3. 8-byte repeating XOR across full stream
                byte[] key8 = { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF1 };
                byte[] xor8Full = (byte[])plainBundle.Clone();
                UnityHeaderProbe.XorBytesRepeating(xor8Full, 0, key8);

                using (var r3 = new FileReader("full8.bundle", new MemoryStream(xor8Full)))
                {
                    Assert.Equal("KnownPlaintextXor", r3.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, r3.FileType);
                    using var bundle3 = new BundleFile(r3);
                    Assert.Single(bundle3.fileList);
                    using var ms3 = new MemoryStream();
                    bundle3.fileList[0].stream.CopyTo(ms3);
                    Assert.Equal(nodePayload, ms3.ToArray());
                }

                // 4. Barrier detection: high-entropy .bundle.enc and cipher .bundle vs plain CRID .enc
                var rng = new Random(1337);
                byte[] highEntropyEnc = new byte[1024];
                rng.NextBytes(highEntropyEnc);
                for (int i = 0; i < 256; i++)
                {
                    if (highEntropyEnc[i] == 0) highEntropyEnc[i] = 1;
                }

                using (var rEnc = new FileReader("packed.bundle.enc", new MemoryStream(highEntropyEnc)))
                {
                    Assert.Null(rEnc.AppliedDecryptionProvider);
                    Assert.True(rEnc.EncryptionBarrier.HasValue);
                    Assert.Equal("EncryptedBundleContainer", rEnc.EncryptionBarrier!.Value.ProviderId);
                }

                using (var rBundle = new FileReader("cipher_header.bundle", new MemoryStream(highEntropyEnc)))
                {
                    Assert.Null(rBundle.AppliedDecryptionProvider);
                    Assert.True(rBundle.EncryptionBarrier.HasValue);
                    Assert.Equal("EncryptedUnityBundle", rBundle.EncryptionBarrier!.Value.ProviderId);
                }

                byte[] plainCrid = new byte[512];
                rng.NextBytes(plainCrid);
                plainCrid[0] = (byte)'C'; plainCrid[1] = (byte)'R'; plainCrid[2] = (byte)'I'; plainCrid[3] = (byte)'D';
                using (var rCrid = new FileReader("movie.enc", new MemoryStream(plainCrid)))
                {
                    Assert.False(rCrid.EncryptionBarrier.HasValue);
                }
            }
            finally
            {
                DecryptionHelper.Clear();
            }
        }

        [Fact]
        public void StreamedClip_ReadData_LinearSlopePass_MatchesReferenceAndHandlesLargeFrameCountsQuickly()
        {
            // Build 400 streamed frames x 40 curves = 16,000 keys (would be slow under O(F^2 * K), <50ms under O(totalKeys))
            const int frameCount = 400;
            const int curvesPerFrame = 40;
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                for (int f = 0; f < frameCount; f++)
                {
                    float t = f == 0 ? float.NegativeInfinity : (f == frameCount - 1 ? float.PositiveInfinity : (f - 1) * 0.033f);
                    bw.Write(t);
                    bw.Write(curvesPerFrame);
                    for (int c = 0; c < curvesPerFrame; c++)
                    {
                        bw.Write(c); // curve index
                        bw.Write(0.1f); // coeff0
                        bw.Write(0.2f); // coeff1
                        bw.Write(0.3f); // coeff2
                        bw.Write(f * 0.5f + c); // coeff3 (value)
                    }
                }
            }

            byte[] rawBytes = ms.ToArray();
            uint[] words = new uint[rawBytes.Length / 4];
            Buffer.BlockCopy(rawBytes, 0, words, 0, rawBytes.Length);

            var streamed = (StreamedClip)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(StreamedClip));
            streamed.data = words;
            streamed.curveCount = curvesPerFrame;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var frames = streamed.ReadData();
            sw.Stop();

            Assert.Equal(frameCount, frames.Count);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"StreamedClip.ReadData took {sw.ElapsedMilliseconds}ms");

            // Verify frame[2] curve[0] inSlope matches CalculateNextInSlope from frame[1]
            var prevKey = frames[1].keyList[0];
            var curKey = frames[2].keyList[0];
            float expectedInSlope = prevKey.CalculateNextInSlope(frames[2].time - frames[1].time, curKey);
            Assert.Equal(expectedInSlope, curKey.inSlope, precision: 5);
        }

        private static byte[] BuildValidLz4UnityFsBundle(string nodePath, byte[] nodePayload)
        {
            // Split payload into 2 LZ4 blocks so prefix-cutoff boundary validation exercises multiple blocks
            int half = nodePayload.Length / 2;
            byte[] part1 = nodePayload.AsSpan(0, half).ToArray();
            byte[] part2 = nodePayload.AsSpan(half).ToArray();

            byte[] comp1 = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(part1.Length)];
            int comp1Len = K4os.Compression.LZ4.LZ4Codec.Encode(part1, comp1);
            Array.Resize(ref comp1, comp1Len);

            byte[] comp2 = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(part2.Length)];
            int comp2Len = K4os.Compression.LZ4.LZ4Codec.Encode(part2, comp2);
            Array.Resize(ref comp2, comp2Len);

            // Build uncompressed blocksInfo (16B hash + 2 blocks + 1 directory node)
            byte[] blocksInfoRaw;
            using (var biMs = new MemoryStream())
            using (var biBw = new BinaryWriter(biMs, Encoding.ASCII, leaveOpen: true))
            {
                biBw.Write(new byte[16]); // hash
                Span<byte> be32 = stackalloc byte[4];
                Span<byte> be16 = stackalloc byte[2];
                Span<byte> be64 = stackalloc byte[8];

                BinaryPrimitives.WriteInt32BigEndian(be32, 2); biBw.Write(be32); // blockCount
                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)part1.Length); biBw.Write(be32);
                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)comp1.Length); biBw.Write(be32);
                BinaryPrimitives.WriteUInt16BigEndian(be16, (ushort)CompressionType.Lz4); biBw.Write(be16);

                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)part2.Length); biBw.Write(be32);
                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)comp2.Length); biBw.Write(be32);
                BinaryPrimitives.WriteUInt16BigEndian(be16, (ushort)CompressionType.Lz4); biBw.Write(be16);

                BinaryPrimitives.WriteInt32BigEndian(be32, 1); biBw.Write(be32); // nodeCount
                BinaryPrimitives.WriteInt64BigEndian(be64, 0L); biBw.Write(be64); // offset
                BinaryPrimitives.WriteInt64BigEndian(be64, nodePayload.Length); biBw.Write(be64); // size
                BinaryPrimitives.WriteUInt32BigEndian(be32, 4u); biBw.Write(be32); // flags
                biBw.Write(Encoding.ASCII.GetBytes(nodePath));
                biBw.Write((byte)0);
                blocksInfoRaw = biMs.ToArray();
            }

            byte[] compBlocksInfo = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(blocksInfoRaw.Length)];
            int compBlocksInfoLen = K4os.Compression.LZ4.LZ4Codec.Encode(blocksInfoRaw, compBlocksInfo);
            Array.Resize(ref compBlocksInfo, compBlocksInfoLen);

            // Header prefix before size/blocksInfo/flags
            byte[] prefix;
            using (var pMs = new MemoryStream())
            using (var pBw = new BinaryWriter(pMs, Encoding.ASCII, leaveOpen: true))
            {
                pBw.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
                Span<byte> be32 = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(be32, 8u); pBw.Write(be32);
                pBw.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
                pBw.Write(Encoding.ASCII.GetBytes("0.0.0\0"));
                prefix = pMs.ToArray();
            }

            int rawHeaderEnd = prefix.Length + 8 + 4 + 4 + 4;
            int alignedHeaderEnd = (rawHeaderEnd + 15) & ~15;
            long totalSize = alignedHeaderEnd + compBlocksInfo.Length + comp1.Length + comp2.Length;

            using var outMs = new MemoryStream((int)totalSize);
            using (var bw = new BinaryWriter(outMs, Encoding.ASCII, leaveOpen: true))
            {
                Span<byte> be32 = stackalloc byte[4];
                Span<byte> be64 = stackalloc byte[8];
                bw.Write(prefix);
                BinaryPrimitives.WriteInt64BigEndian(be64, totalSize); bw.Write(be64);
                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)compBlocksInfo.Length); bw.Write(be32);
                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)blocksInfoRaw.Length); bw.Write(be32);
                BinaryPrimitives.WriteUInt32BigEndian(be32, (uint)(ArchiveFlags.BlocksAndDirectoryInfoCombined | (ArchiveFlags)CompressionType.Lz4)); bw.Write(be32);
                while (outMs.Position < alignedHeaderEnd) bw.Write((byte)0);
                bw.Write(compBlocksInfo);
                bw.Write(comp1);
                bw.Write(comp2);
            }
            return outMs.ToArray();
        }

        private static byte[] BuildSyntheticUnityFsHeaderPayload(int totalSize)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
                Span<byte> be32 = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(be32, 7u);
                bw.Write(be32);
                bw.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
                bw.Write(Encoding.ASCII.GetBytes("2021.3.34f1\0"));
                Span<byte> be64 = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(be64, totalSize);
                bw.Write(be64);
                BinaryPrimitives.WriteUInt32BigEndian(be32, 32u); // compressedBlocksInfoSize
                bw.Write(be32);
                BinaryPrimitives.WriteUInt32BigEndian(be32, 32u); // uncompressedBlocksInfoSize
                bw.Write(be32);
                BinaryPrimitives.WriteUInt32BigEndian(be32, 0x40u); // flags
                bw.Write(be32);
            }

            byte[] result = new byte[totalSize];
            byte[] headerBytes = ms.ToArray();
            Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
            for (int i = headerBytes.Length; i < totalSize; i++)
            {
                result[i] = (byte)(i & 0xFF);
            }
            return result;
        }

        private static SerializedFile CreateMinimalSerializedFile(string initialUnityVersion)
        {
            // Build metadata (LittleEndian, format version 17 = RefactorTypeData)
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes(initialUnityVersion));
                metaBw.Write((byte)0); // null terminator
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
            var fileReader = new FileReader("minimal.assets", ms);
            return new SerializedFile(fileReader, new AssetsManager());
        }

        private static MemoryStream BuildLzmaHeaderStream(uint dictionarySize, long outSize)
        {
            var data = new byte[5 + 8 + 16];
            data[0] = 0x5D; // lc=3, lp=0, pb=2
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1, 4), dictionarySize);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(5, 8), outSize);
            return new MemoryStream(data);
        }

        private static byte[] CreateDeterministicPayload(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 31 + 17) & 0xFF);
            }
            return data;
        }

        private static string BuildSyntheticAddressablesCatalog(string bundleFileName, string bundleName, string hash, string crc)
        {
            // EntryData: 4-byte entryCount (1) + 28-byte entry (internalIdIdx=0 at +0, dataIdx=0 at +16)
            var entryBytes = new byte[4 + 28];
            BinaryPrimitives.WriteInt32LittleEndian(entryBytes.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(entryBytes.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(entryBytes.AsSpan(4 + 16, 4), 0);

            // ExtraData: objType=0 (AsciiString), 4-byte length, ASCII JSON
            string extraJson = $"{{\"m_BundleName\":\"{bundleName}\",\"m_Hash\":\"{hash}\",\"m_Crc\":{crc}}}";
            var extraJsonBytes = Encoding.ASCII.GetBytes(extraJson);
            var extraBytes = new byte[1 + 4 + extraJsonBytes.Length];
            extraBytes[0] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(extraBytes.AsSpan(1, 4), extraJsonBytes.Length);
            Buffer.BlockCopy(extraJsonBytes, 0, extraBytes, 5, extraJsonBytes.Length);

            return "{\n"
                + $"  \"m_InternalIds\": [\"0#Embed/{bundleFileName}\"],\n"
                + "  \"m_InternalIdPrefixes\": [\"aa/\"],\n"
                + $"  \"m_EntryDataString\": \"{Convert.ToBase64String(entryBytes)}\",\n"
                + $"  \"m_ExtraDataString\": \"{Convert.ToBase64String(extraBytes)}\"\n"
                + "}";
        }
    }
}
