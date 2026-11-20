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

            // Stream-backed ResourceReader with >50% 0x01 XOR trigger must only transform [0..toRead)
            var xorPayload = new byte[64];
            Array.Fill(xorPayload, (byte)0x01);
            xorPayload[0] = 0x42;

            using var ms = new MemoryStream(xorPayload);
            using var br = new BinaryReader(ms);
            var streamReader = new ResourceReader(br, 0, xorPayload.Length);
            var poolBuffer = new byte[256];
            Array.Fill(poolBuffer, (byte)0x55);

            streamReader.GetData(poolBuffer);

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
