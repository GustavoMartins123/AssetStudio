using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AssetStudio;
using K4os.Compression.LZ4;
using Xunit;

namespace AssetStudio.Tests
{
    [Collection("ResourceReaderXorHeuristic")]
    public sealed class CompositeStringKeyDecryptionTests
    {
        private const string AssetKeyHalf = "As7Half" + "0123456789";
        private const string LiteralKeyHalf = "Lc3Const" + "0001234";
        private const string AssetIvHalf = "Iv9Asset" + "00000001";
        private const string LiteralIvHalf = "Iv3Const" + "00000001";

        [Fact]
        public void CompositeKeyProvider_DiscoversRijndael256KeyAndIv_FromAssetStringsAndMetadataLiterals()
        {
            ClearCaches();
            string root = CreateTempDirectory();
            try
            {
                string bundleDir = CreateProject(root, new[] { LiteralKeyHalf, LiteralIvHalf }, new[] { AssetKeyHalf, AssetIvHalf }.Concat(CreateNoise(400, 11)));
                var cipher = new RijndaelBlockCipher(Encoding.ASCII.GetBytes(AssetKeyHalf + LiteralKeyHalf), 256);
                byte[] iv = Encoding.ASCII.GetBytes(AssetIvHalf + LiteralIvHalf);
                byte[] firstPlain = BuildLz4Bundle("CAB-alpha", CreateSerializedNodePayload(3000, 1), 8, "2022.3.62f2");
                byte[] secondPlain = BuildLz4Bundle("CAB-beta", CreateSerializedNodePayload(2000, 2), 8, "2022.3.62f2");
                byte[] firstCipher = EncryptCbc(cipher, firstPlain, iv);
                byte[] secondCipher = EncryptCbc(cipher, secondPlain, iv);
                string firstPath = Path.Combine(bundleDir, "pack_one.bundle");
                string secondPath = Path.Combine(bundleDir, "pack_two.bundle");
                File.WriteAllBytes(firstPath, firstCipher);
                File.WriteAllBytes(secondPath, secondCipher);

                var provider = new CompositeStringKeyTransformProvider();
                Assert.Equal("CompositeStringKey", provider.Id);
                using (var transformed = Assert.IsType<RijndaelCbcTransformStream>(provider.TryTransform(new MemoryStream(firstCipher), firstPath, firstCipher)))
                {
                    Assert.Equal(32, transformed.BlockSize);
                    Assert.Equal(32, transformed.Cipher.KeySize);
                    Assert.True(transformed.Pkcs7Padding);
                    byte[] decoded = ReadAllBytes(transformed);
                    Assert.Equal(firstPlain, decoded);
                    Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(8, 4)));
                    Assert.True(decoded.AsSpan(18).StartsWith(Encoding.ASCII.GetBytes("2022.3.62f2\0")));
                }

                using (var transformed = Assert.IsType<RijndaelCbcTransformStream>(provider.TryTransform(new MemoryStream(secondCipher), secondPath, secondCipher)))
                {
                    Assert.Equal(secondPlain, ReadAllBytes(transformed));
                }

                Assert.Null(provider.TryTransform(new MemoryStream(firstPlain), Path.Combine(bundleDir, "plain.bundle"), firstPlain));
                Assert.Null(provider.TryTransform(new MemoryStream(firstCipher), Path.Combine(root, "nowhere.bundle"), firstCipher));

                ClearCaches();
                using (var reader = new FileReader(firstPath))
                {
                    Assert.Equal("CompositeStringKey", reader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, reader.FileType);
                    Assert.Equal(firstPlain, ReadAllBytes(reader.BaseStream));
                }
            }
            finally
            {
                ClearCaches();
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void CompositeKeyProvider_DiscoversAes256Key_WhenLiteralPrecedesAssetString()
        {
            ClearCaches();
            string root = CreateTempDirectory();
            try
            {
                string literalKey = "Lc3Key" + "00000000000";
                string assetKey = "As7Tail" + "11111111";
                string singleIv = "Iv9Single" + "0000001";
                string bundleDir = CreateProject(root, new[] { literalKey }.Concat(CreateNoise(300, 21)), new[] { assetKey, singleIv }.Concat(CreateNoise(300, 22)));
                byte[] key = Encoding.ASCII.GetBytes(literalKey + assetKey);
                byte[] iv = Encoding.ASCII.GetBytes(singleIv);
                byte[] plain = BuildLz4Bundle("CAB-gamma", CreateSerializedNodePayload(5000, 3), 7, "2021.3.40f1");
                byte[] cipher = EncryptAesCbc(plain, key, iv);
                string path = Path.Combine(bundleDir, "aes_pack.bundle");
                File.WriteAllBytes(path, cipher);
                File.WriteAllBytes(Path.Combine(bundleDir, "aes_other.bundle"), EncryptAesCbc(BuildLz4Bundle("CAB-delta", CreateSerializedNodePayload(1500, 4), 7, "2021.3.40f1"), key, iv));

                var provider = new CompositeStringKeyTransformProvider();
                using (var transformed = Assert.IsType<CipherTransformStream>(provider.TryTransform(new MemoryStream(cipher), path, cipher)))
                {
                    Assert.Equal(StreamCipherAlgorithm.AesCbc, transformed.Algorithm);
                    Assert.True(transformed.Pkcs7Padding);
                    Assert.True(key.AsSpan().SequenceEqual(transformed.Key));
                    Assert.True(iv.AsSpan().SequenceEqual(transformed.IvOrNonce));
                    byte[] decoded = ReadAllBytes(transformed);
                    Assert.Equal(plain, decoded);
                    Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(8, 4)));
                    Assert.True(decoded.AsSpan(18).StartsWith(Encoding.ASCII.GetBytes("2021.3.40f1\0")));
                }

                ClearCaches();
                using (var reader = new FileReader(path))
                {
                    Assert.Equal("CompositeStringKey", reader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, reader.FileType);
                    Assert.Equal(plain, ReadAllBytes(reader.BaseStream));
                }
            }
            finally
            {
                ClearCaches();
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void CompositeKeyProvider_SynthesizesIv_WhenIvIsNotBuiltFromKnownStrings()
        {
            ClearCaches();
            string root = CreateTempDirectory();
            try
            {
                string bundleDir = CreateProject(root, new[] { LiteralKeyHalf }, new[] { AssetKeyHalf }.Concat(CreateNoise(200, 31)));
                var cipher = new RijndaelBlockCipher(Encoding.ASCII.GetBytes(AssetKeyHalf + LiteralKeyHalf), 256);
                byte[] iv = CreateRandom(32, 99);
                byte[] plain = BuildLz4Bundle("CAB-epsilon", CreateSerializedNodePayload(4000, 5), 8, "2022.3.62f2");
                byte[] cipherBytes = EncryptCbc(cipher, plain, iv);
                string firstPath = Path.Combine(bundleDir, "opaque_one.bundle");
                string secondPath = Path.Combine(bundleDir, "opaque_two.bundle");
                File.WriteAllBytes(firstPath, cipherBytes);
                File.WriteAllBytes(secondPath, EncryptCbc(cipher, BuildLz4Bundle("CAB-zeta", CreateSerializedNodePayload(2500, 6), 8, "2022.3.62f2"), iv));

                var provider = new CompositeStringKeyTransformProvider();
                using (var transformed = Assert.IsType<RijndaelCbcTransformStream>(provider.TryTransform(new MemoryStream(cipherBytes), firstPath, cipherBytes)))
                {
                    byte[] decoded = ReadAllBytes(transformed);
                    Assert.Equal(plain.AsSpan(0, 8).ToArray(), decoded.AsSpan(0, 8).ToArray());
                    Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(8, 4)));
                    Assert.Equal(plain.AsSpan(12, 6).ToArray(), decoded.AsSpan(12, 6).ToArray());
                    Assert.True(decoded.AsSpan(18).StartsWith(Encoding.ASCII.GetBytes(new string('0', 4) + "0.0.0f1\0")));
                    Assert.Equal(plain.AsSpan(30).ToArray(), decoded.AsSpan(30).ToArray());
                }

                ClearCaches();
                File.WriteAllBytes(Path.Combine(root, "Sample_Data", "globalgamemanagers"), BuildGlobalGameManagers(22, "2019.4.40f1"));
                byte[] thirdPlain = BuildLz4Bundle("CAB-eta", CreateSerializedNodePayload(3200, 7), 6, "2019.4.40f1");
                byte[] thirdCipher = EncryptCbc(cipher, thirdPlain, iv);
                string thirdPath = Path.Combine(bundleDir, "opaque_three.bundle");
                File.WriteAllBytes(thirdPath, thirdCipher);
                File.WriteAllBytes(Path.Combine(bundleDir, "opaque_four.bundle"), EncryptCbc(cipher, BuildLz4Bundle("CAB-theta", CreateSerializedNodePayload(2500, 8), 6, "2019.4.40f1"), iv));
                using (var transformed = Assert.IsType<RijndaelCbcTransformStream>(provider.TryTransform(new MemoryStream(thirdCipher), thirdPath, thirdCipher)))
                {
                    byte[] decoded = ReadAllBytes(transformed);
                    Assert.True(decoded.AsSpan(18).StartsWith(Encoding.ASCII.GetBytes("2019.4.40f1\0")));
                    Assert.Equal(thirdPlain.AsSpan(30).ToArray(), decoded.AsSpan(30).ToArray());
                }
            }
            finally
            {
                ClearCaches();
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void CompositeKeyProvider_RequiresSiblingWithSharedPrefix_AndCachesNegativeResult()
        {
            ClearCaches();
            string root = CreateTempDirectory();
            try
            {
                string bundleDir = CreateProject(root, new[] { LiteralKeyHalf, LiteralIvHalf }.Concat(CreateNoise(80, 41)), new[] { AssetKeyHalf, AssetIvHalf }.Concat(CreateNoise(120, 42)));
                byte[] shared = CreateRandom(16, 7);
                byte[] opaqueA = CreateRandom(96 * 1024, 8);
                byte[] opaqueB = CreateRandom(64 * 1024, 9);
                Array.Copy(shared, opaqueA, shared.Length);
                Array.Copy(shared, opaqueB, shared.Length);
                string pathA = Path.Combine(bundleDir, "opaque_a.bundle");
                string pathB = Path.Combine(bundleDir, "opaque_b.bundle");
                File.WriteAllBytes(pathA, opaqueA);
                File.WriteAllBytes(pathB, opaqueB);

                var provider = new CompositeStringKeyTransformProvider();
                Assert.Null(provider.TryTransform(new MemoryStream(opaqueA), pathA, opaqueA));
                Assert.Null(provider.TryTransform(new MemoryStream(opaqueB), pathB, opaqueB));

                string loneDir = Directory.CreateDirectory(Path.Combine(root, "Sample_Data", "StreamingAssets", "lone")).FullName;
                var cipher = new RijndaelBlockCipher(Encoding.ASCII.GetBytes(AssetKeyHalf + LiteralKeyHalf), 256);
                byte[] iv = Encoding.ASCII.GetBytes(AssetIvHalf + LiteralIvHalf);
                byte[] plain = BuildLz4Bundle("CAB-lone", CreateSerializedNodePayload(2600, 10), 8, "2022.3.62f2");
                byte[] loneCipher = EncryptCbc(cipher, plain, iv);
                string lonePath = Path.Combine(loneDir, "lone.bundle");
                File.WriteAllBytes(lonePath, loneCipher);
                Assert.Null(provider.TryTransform(new MemoryStream(loneCipher), lonePath, loneCipher));

                string pairDir = Directory.CreateDirectory(Path.Combine(root, "Sample_Data", "StreamingAssets", "pair")).FullName;
                byte[] pairCipher = EncryptCbc(cipher, BuildLz4Bundle("CAB-pair", CreateSerializedNodePayload(1800, 11), 8, "2022.3.62f2"), iv);
                string pairPath = Path.Combine(pairDir, "pair_one.dat");
                string pairOtherPath = Path.Combine(pairDir, "pair_two.dat");
                File.WriteAllBytes(pairPath, pairCipher);
                File.WriteAllBytes(pairOtherPath, loneCipher);
                Assert.Null(provider.TryTransform(new MemoryStream(pairCipher), pairPath, pairCipher));
            }
            finally
            {
                ClearCaches();
                TryDeleteDirectory(root);
            }
        }

        private static void ClearCaches()
        {
            CompositeStringKeyTransformProvider.ClearCache();
            PassphraseCtrTransformProvider.ClearCache();
        }

        private static string CreateProject(string root, IEnumerable<string> literals, IEnumerable<string> assetStrings)
        {
            string dataDir = Path.Combine(root, "Sample_Data");
            string bundleDir = Path.Combine(dataDir, "StreamingAssets", "packs");
            Directory.CreateDirectory(Path.Combine(dataDir, "il2cpp_data", "Metadata"));
            Directory.CreateDirectory(bundleDir);
            File.WriteAllBytes(Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat"), BuildMetadata(31, literals.ToArray()));
            File.WriteAllBytes(Path.Combine(dataDir, "resources.assets"), BuildAssetFile(assetStrings));
            return bundleDir;
        }

        private static string[] CreateNoise(int count, int seed)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random(seed);
            var values = new string[count];
            var text = new char[26];
            for (int i = 0; i < count; i++)
            {
                int length = random.Next(4, text.Length + 1);
                for (int c = 0; c < length; c++)
                {
                    text[c] = alphabet[random.Next(alphabet.Length)];
                }

                values[i] = "q" + i.ToString("x") + new string(text, 0, length);
            }

            return values;
        }

        private static byte[] CreateRandom(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static byte[] BuildAssetFile(IEnumerable<string> strings)
        {
            var random = new Random(5150);
            using var ms = new MemoryStream();
            var separator = new byte[32];
            foreach (string value in strings)
            {
                random.NextBytes(separator);
                for (int i = 0; i < separator.Length; i++)
                {
                    if (separator[i] is >= 0x20 and <= 0x7E)
                    {
                        separator[i] = 1;
                    }
                }

                ms.Write(separator);
                ms.Write(Encoding.ASCII.GetBytes(value));
            }

            ms.Write(new byte[32]);
            return ms.ToArray();
        }

        private static byte[] BuildGlobalGameManagers(uint version, string revision)
        {
            var data = new byte[128];
            new Random(77).NextBytes(data);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), version);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12, 4), 48u);
            byte[] text = Encoding.ASCII.GetBytes(revision);
            text.CopyTo(data, version >= 22 ? 24 : 20);
            data[(version >= 22 ? 24 : 20) + text.Length] = 0;
            return data;
        }

        private static byte[] BuildMetadata(uint version, params string[] literals)
        {
            bool triplets = version >= 38;
            int headerSize = triplets ? 0x100 : 0x80;
            int entrySize = triplets ? 4 : 8;
            byte[] data = literals.SelectMany(l => Encoding.UTF8.GetBytes(l)).ToArray();
            using var ms = new MemoryStream();
            WriteInt32LE(ms, unchecked((int)0xFAB11BAFu));
            WriteInt32LE(ms, (int)version);
            WriteInt32LE(ms, headerSize);
            WriteInt32LE(ms, literals.Length * entrySize);
            if (triplets)
            {
                WriteInt32LE(ms, literals.Length);
            }

            WriteInt32LE(ms, headerSize + literals.Length * entrySize);
            WriteInt32LE(ms, data.Length);
            if (triplets)
            {
                WriteInt32LE(ms, data.Length);
            }

            ms.Position = headerSize;
            int index = 0;
            foreach (string literal in literals)
            {
                if (triplets)
                {
                    WriteInt32LE(ms, index);
                }
                else
                {
                    WriteInt32LE(ms, Encoding.UTF8.GetByteCount(literal));
                    WriteInt32LE(ms, index);
                }

                index += Encoding.UTF8.GetByteCount(literal);
            }

            ms.Write(data);
            return ms.ToArray();
        }

        private static byte[] CreateSerializedNodePayload(int length, int seed)
        {
            var payload = new byte[length];
            var random = new Random(seed);
            for (int i = 0; i < length; i += 8)
            {
                payload[i] = (byte)random.Next(1, 256);
            }

            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 22u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 128u);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24, 8), length);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32, 8), 48L);
            return payload;
        }

        private static byte[] BuildLz4Bundle(string cabName, byte[] nodeData, uint formatVersion, string revision)
        {
            var chunks = new List<byte[]>();
            for (int offset = 0; offset < nodeData.Length; offset += 1024)
            {
                int count = Math.Min(1024, nodeData.Length - offset);
                var chunk = new byte[count];
                Array.Copy(nodeData, offset, chunk, 0, count);
                var buffer = new byte[LZ4Codec.MaximumOutputSize(count)];
                chunks.Add(buffer.AsSpan(0, LZ4Codec.Encode(chunk, buffer)).ToArray());
            }

            byte[] uncompBlocksInfo;
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[16]);
                WriteInt32BE(ms, chunks.Count);
                for (int offset = 0, i = 0; i < chunks.Count; offset += 1024, i++)
                {
                    WriteUInt32BE(ms, (uint)Math.Min(1024, nodeData.Length - offset));
                    WriteUInt32BE(ms, (uint)chunks[i].Length);
                    ms.Write(new byte[] { 0, (byte)CompressionType.Lz4 });
                }

                WriteInt32BE(ms, 1);
                WriteInt64BE(ms, 0);
                WriteInt64BE(ms, nodeData.Length);
                WriteUInt32BE(ms, 4u);
                ms.Write(Encoding.ASCII.GetBytes(cabName));
                ms.WriteByte(0);
                uncompBlocksInfo = ms.ToArray();
            }

            var infoBuffer = new byte[LZ4Codec.MaximumOutputSize(uncompBlocksInfo.Length)];
            byte[] compBlocksInfo = infoBuffer.AsSpan(0, LZ4Codec.Encode(uncompBlocksInfo, infoBuffer)).ToArray();
            byte[] header;
            using (var ms = new MemoryStream())
            {
                ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
                WriteUInt32BE(ms, formatVersion);
                ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
                ms.Write(Encoding.ASCII.GetBytes(revision + "\0"));
                header = ms.ToArray();
            }

            int alignment = formatVersion >= 7 ? (16 - (header.Length + 20) % 16) % 16 : 0;
            long totalSize = header.Length + 20 + alignment + compBlocksInfo.Length + chunks.Sum(c => (long)c.Length);
            using var finalMs = new MemoryStream();
            finalMs.Write(header);
            WriteInt64BE(finalMs, totalSize);
            WriteUInt32BE(finalMs, (uint)compBlocksInfo.Length);
            WriteUInt32BE(finalMs, (uint)uncompBlocksInfo.Length);
            WriteUInt32BE(finalMs, (uint)CompressionType.Lz4);
            finalMs.Write(new byte[alignment]);
            finalMs.Write(compBlocksInfo);
            foreach (byte[] chunk in chunks)
            {
                finalMs.Write(chunk);
            }

            return finalMs.ToArray();
        }

        private static byte[] EncryptCbc(RijndaelBlockCipher cipher, byte[] plaintext, byte[] iv)
        {
            int pad = cipher.BlockSize - plaintext.Length % cipher.BlockSize;
            var padded = new byte[plaintext.Length + pad];
            plaintext.CopyTo(padded, 0);
            Array.Fill(padded, (byte)pad, plaintext.Length, pad);
            var ciphertext = new byte[padded.Length];
            byte[] previous = iv;
            for (int offset = 0; offset < padded.Length; offset += cipher.BlockSize)
            {
                var block = new byte[cipher.BlockSize];
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] = (byte)(padded[offset + i] ^ previous[i]);
                }

                cipher.EncryptBlock(block, block);
                block.CopyTo(ciphertext, offset);
                previous = block;
            }

            return ciphertext;
        }

        private static byte[] EncryptAesCbc(byte[] plain, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plain, 0, plain.Length);
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            stream.Position = 0;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static void WriteUInt32BE(Stream s, uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            s.Write(b, 0, b.Length);
        }

        private static void WriteInt64BE(Stream s, long v)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            s.Write(b, 0, b.Length);
        }

        private static void WriteInt32BE(Stream s, int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            s.Write(b, 0, b.Length);
        }

        private static void WriteInt32LE(Stream s, int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            s.Write(b, 0, b.Length);
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "AssetStudioCompositeKeyTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }
    }
}
