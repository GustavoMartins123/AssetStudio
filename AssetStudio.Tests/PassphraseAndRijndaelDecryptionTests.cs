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
    public sealed class PassphraseAndRijndaelDecryptionTests
    {
        private const string SampleKey = "0123456789ABCDEF0123456789ABCDEF";
        private const string SampleIv = "FEDCBA9876543210FEDCBA9876543210";

        [Fact]
        public void LegacyKeyDerivation_Pbkdf1Sha1_MatchesPasswordDeriveBytes()
        {
            var vectors = new (string Password, string Salt)[]
            {
                ("passphrase", "bundle-name"),
                ("Zeta7-passphrase_42", "x"),
                ("password with unicode \u00e3\u00e9\u00ea", "salt\u4e2d\u6587"),
                ("", "salt-only")
            };

#pragma warning disable SYSLIB0021
            foreach (var (password, salt) in vectors)
            {
                using var reference = new PasswordDeriveBytes(password, Encoding.UTF8.GetBytes(salt));
                byte[] expected = reference.GetBytes(16);
                byte[] actual = LegacyKeyDerivation.Pbkdf1Sha1(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), 100, 16);
                Assert.Equal(expected, actual);
            }
#pragma warning restore SYSLIB0021

            Assert.Equal(20, LegacyKeyDerivation.Pbkdf1Sha1("k"u8, "s"u8, 100, 20).Length);
            Assert.Equal(4, LegacyKeyDerivation.Pbkdf1Sha1("k"u8, "s"u8, 250, 4).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyKeyDerivation.Pbkdf1Sha1("k"u8, "s"u8, 100, 21));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyKeyDerivation.Pbkdf1Sha1("k"u8, "s"u8, 0, 16));
        }

        [Fact]
        public void RijndaelBlockCipher_Fips197Vectors_RoundTrips_AndReferenceImplementation()
        {
            byte[] plain = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

            var aes128 = new RijndaelBlockCipher(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), 128);
            Assert.Equal(Convert.FromHexString("69C4E0D86A7B0430D8CDB78070B4C55A"), Transform(aes128, plain, true));
            Assert.Equal(Convert.FromHexString("69C4E0D86A7B0430D8CDB78070B4C55A"), ReferenceEncrypt(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), 128, plain));
            Assert.Equal(plain, Transform(aes128, Convert.FromHexString("69C4E0D86A7B0430D8CDB78070B4C55A"), false));
            Assert.Equal(10, aes128.Rounds);

            var aes192 = new RijndaelBlockCipher(Convert.FromHexString("000102030405060708090A0B0C0D0E0F1011121314151617"), 128);
            Assert.Equal(Convert.FromHexString("DDA97CA4864CDFE06EAF70A0EC0D7191"), Transform(aes192, plain, true));
            Assert.Equal(12, aes192.Rounds);

            var aes256 = new RijndaelBlockCipher(Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"), 128);
            Assert.Equal(Convert.FromHexString("8EA2B7CA516745BFEAFC49904B496089"), Transform(aes256, plain, true));
            Assert.Equal(14, aes256.Rounds);

            foreach (int blockBits in new[] { 128, 192, 256 })
            {
                foreach (int keyBytes in new[] { 16, 24, 32 })
                {
                    var random = new Random(blockBits * 100 + keyBytes);
                    var key = new byte[keyBytes];
                    var block = new byte[blockBits / 8];
                    random.NextBytes(key);
                    random.NextBytes(block);
                    var rijndael = new RijndaelBlockCipher(key, blockBits);
                    byte[] expected = ReferenceEncrypt(key, blockBits, block);
                    Assert.Equal(blockBits / 8, rijndael.BlockSize);
                    Assert.Equal(keyBytes, rijndael.KeySize);
                    Assert.Equal(Math.Max(keyBytes / 4, blockBits / 32) + 6, rijndael.Rounds);
                    Assert.Equal(expected, Transform(rijndael, block, true));
                    Assert.Equal(block, Transform(rijndael, expected, false));
                }
            }

            var wide = new RijndaelBlockCipher(Encoding.ASCII.GetBytes(SampleKey), 256);
            Assert.Throws<ArgumentException>(() => new RijndaelBlockCipher(new byte[15], 128));
            Assert.Throws<ArgumentException>(() => new RijndaelBlockCipher(new byte[16], 160));
            Assert.Throws<ArgumentException>(() => wide.EncryptBlock(new byte[31], new byte[32]));
            Assert.Throws<ArgumentException>(() => wide.DecryptBlock(new byte[32], new byte[31]));
        }

        [Fact]
        public void RijndaelCbcTransformStream_RoundTripsSeeksCloneAndPkcs7Padding()
        {
            byte[] plain = CreatePatternPayload(1000);
            var cipher = new RijndaelBlockCipher(Encoding.ASCII.GetBytes(SampleKey), 256);
            byte[] iv = Encoding.ASCII.GetBytes(SampleIv);
            byte[] encrypted = EncryptCbc(cipher, Pad(plain, 32), iv);

            using (var stream = new RijndaelCbcTransformStream(new MemoryStream(encrypted), cipher, iv, 0, pkcs7Padding: true))
            {
                Assert.Equal(plain.Length, stream.Length);
                AssertRandomSeekMatches(stream, plain);

                using var cloned = stream.Clone(new MemoryStream(encrypted));
                Assert.Equal(plain, ReadAllBytes(cloned));
            }

            using (var unpadded = new RijndaelCbcTransformStream(new MemoryStream(encrypted), cipher, iv, 0, pkcs7Padding: false))
            {
                Assert.Equal(encrypted.Length, unpadded.Length);
                Assert.Equal(Pad(plain, 32), ReadAllBytes(unpadded));
            }

            byte[] prefixed = new byte[40 + encrypted.Length];
            encrypted.CopyTo(prefixed, 40);
            using (var skipped = new RijndaelCbcTransformStream(new MemoryStream(prefixed), cipher, iv, 40, pkcs7Padding: true))
            {
                Assert.Equal(plain, ReadAllBytes(skipped));
            }

            Assert.Throws<ArgumentException>(() => new RijndaelCbcTransformStream(new MemoryStream(encrypted), cipher, new byte[16]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RijndaelCbcTransformStream(new MemoryStream(encrypted), cipher, iv, -1));
        }

        [Fact]
        public void HeaderMasks_AndHeaderMaskStream()
        {
            var xorShiftExpected = new byte[32];
            uint xorShiftState = 0x12345678;
            for (int i = 0; i < xorShiftExpected.Length; i++)
            {
                xorShiftState ^= xorShiftState << 13;
                xorShiftState ^= xorShiftState >> 17;
                xorShiftState ^= xorShiftState << 5;
                xorShiftExpected[i] = (byte)(xorShiftState >> 11);
            }

            Assert.Equal(xorShiftExpected, HeaderMasks.XorShift32(0x12345678, 32));
            Assert.Equal(xorShiftExpected, HeaderMasks.XorShift32(0x12345678, 64).Take(32));

            byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i * 11)).ToArray();
            using (var hmac = new HMACSHA256(key))
            {
                byte[] expected = hmac.ComputeHash(new byte[4]);
                byte[] actual = HeaderMasks.HmacSha256Counter(key, 64);
                Assert.Equal(expected, actual.Take(32).ToArray());
                Assert.Equal(hmac.ComputeHash(new byte[] { 1, 0, 0, 0 }), actual.Skip(32).ToArray());
            }

            Assert.Equal(37, HeaderMasks.HmacSha256Counter(key, 37).Length);
            Assert.Empty(HeaderMasks.XorShift32(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeaderMasks.HmacSha256Counter(key, 64, 33));

            using (var masked = new HeaderMaskStream(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }), new byte[] { 0xFF, 0x0F }))
            {
                Assert.Equal(new byte[] { 0xFE, 0x0D, 3, 4, 5 }, ReadAllBytes(masked));
                masked.Position = 1;
                Assert.Equal(0x0D, masked.ReadByte());
                masked.Seek(-1, SeekOrigin.End);
                Assert.Equal(5, masked.ReadByte());
            }

            var xorShift = new HeaderMaskProfile { Seed = "0x12345678", Length = 32 };
            Assert.True(xorShift.Validate(out _));
            Assert.Equal(HeaderMasks.XorShift32(0x12345678, 32), CreateMask(xorShift, key, 4096, 32));

            var counter = new HeaderMaskProfile { Type = "Hmac-Sha256-Counter", Length = 4096 };
            Assert.True(counter.Validate(out _));
            Assert.Equal(HeaderMasks.HmacSha256Counter(key, 4096), CreateMask(counter, key, 10000, 4096));

            Assert.False(new HeaderMaskProfile { Seed = "not-a-number" }.Validate(out _));
            Assert.False(new HeaderMaskProfile { LeftShift1 = 32 }.Validate(out _));
            Assert.False(new HeaderMaskProfile { Type = "unknown" }.Validate(out _));
            Assert.False(new HeaderMaskProfile { Length = 0 }.Validate(out _));
            Assert.False(new HeaderMaskProfile { Type = "hmac-sha256-counter", BlockLength = 33 }.Validate(out _));
            Assert.Equal(HeaderMasks.XorShift32(255, 3), CreateMask(new HeaderMaskProfile { Seed = "FF", Length = 3 }, key, 16, 3));
            Assert.Equal(HeaderMasks.XorShift32(16, 3), CreateMask(new HeaderMaskProfile { Seed = "16", Length = 3 }, key, 16, 3));
        }

        [Fact]
        public void PassphraseProvider_DiscoversPassphrase_FromManagedAssemblies_AndDetectsPrefixRange()
        {
            PassphraseCtrTransformProvider.ClearCache();
            string root = CreateTempDirectory();
            try
            {
                string dataDir = Path.Combine(root, "Sample_Data");
                string bundleDir = Path.Combine(dataDir, "StreamingAssets", "packs");
                Directory.CreateDirectory(Path.Combine(dataDir, "Managed"));
                Directory.CreateDirectory(bundleDir);
                File.WriteAllBytes(
                    Path.Combine(dataDir, "Managed", "lib.dll"),
                    BuildFakeAssembly("Zeta7-passphrase_42", 2500));

                var provider = new PassphraseCtrTransformProvider();
                byte[] plain = BuildLz4Bundle("CAB-alpha", CreateSerializedNodePayload(CreateStructuredPayload(3000, 1)));
                string firstPath = Path.Combine(bundleDir, "pack_one_0123456789.bundle");
                byte[] firstCipher = XorAesCtrLittleEndian(plain, DeriveKey("Zeta7-passphrase_42", "pack_one_0123456789"), 128);
                Assert.NotEqual(plain, firstCipher);
                Assert.Equal(plain.Skip(128).ToArray(), firstCipher.Skip(128).ToArray());

                using (var transformed = Assert.IsType<CipherTransformStream>(provider.TryTransform(new MemoryStream(firstCipher), firstPath, firstCipher)))
                {
                    Assert.Equal(128, transformed.RangeLength);
                    Assert.Equal(1u, transformed.CounterStart);
                    Assert.True(transformed.CounterLittleEndian);
                    AssertRandomSeekMatches(transformed, plain);
                }

                byte[] secondPlain = BuildLz4Bundle("CAB-beta", CreateSerializedNodePayload(CreateStructuredPayload(2000, 2)));
                string secondPath = Path.Combine(bundleDir, "pack_two_fedcba9876.bundle");
                byte[] secondCipher = XorAesCtrLittleEndian(secondPlain, DeriveKey("Zeta7-passphrase_42", "pack_two_fedcba9876"), 128);
                using (var transformed = Assert.IsType<CipherTransformStream>(provider.TryTransform(new MemoryStream(secondCipher), secondPath, secondCipher)))
                {
                    Assert.Equal(128, transformed.RangeLength);
                    Assert.Equal(secondPlain, ReadAllBytes(transformed));
                }

                Assert.Null(provider.TryTransform(new MemoryStream(plain), firstPath, plain));
                byte[] foreign = new byte[4096];
                Assert.Null(provider.TryTransform(new MemoryStream(foreign), Path.Combine(bundleDir, "other.bundle"), foreign));
                Assert.Null(provider.TryTransform(new MemoryStream(firstCipher), Path.Combine(root, "nowhere.bundle"), firstCipher));

                File.WriteAllBytes(firstPath, firstCipher);
                PassphraseCtrTransformProvider.ClearCache();
                using (var reader = new FileReader(firstPath))
                {
                    Assert.Equal("PassphraseCtr", reader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, reader.FileType);
                    Assert.Equal(plain, ReadAllBytes(reader.BaseStream));
                }
            }
            finally
            {
                PassphraseCtrTransformProvider.ClearCache();
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void PassphraseProvider_DiscoversPassphrase_FromIl2CppMetadata_AndDetectsWholeFileRange()
        {
            PassphraseCtrTransformProvider.ClearCache();
            string root = CreateTempDirectory();
            try
            {
                string dataDir = Path.Combine(root, "Sample_Data");
                string bundleDir = Path.Combine(dataDir, "StreamingAssets", "packs");
                string metadataDir = Path.Combine(dataDir, "il2cpp_data", "Metadata");
                Directory.CreateDirectory(metadataDir);
                Directory.CreateDirectory(bundleDir);
                string metadataPath = Path.Combine(metadataDir, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, BuildMetadata(31, "some literal", "BetaPass-99", "another literal"));

                var provider = new PassphraseCtrTransformProvider();
                Assert.Equal(
                    new[] { "some literal", "BetaPass-99", "another literal" },
                    PassphraseCtrTransformProvider.ReadMetadataLiterals(File.ReadAllBytes(metadataPath)));

                byte[] smallPlain = BuildLz4Bundle("CAB-small", CreateSerializedNodePayload(CreateStructuredPayload(3000, 3)));
                string smallPath = Path.Combine(bundleDir, "small_pack.bundle");
                byte[] smallCipher = XorAesCtrLittleEndian(smallPlain, DeriveKey("BetaPass-99", "small_pack"), 65536);
                using (var transformed = Assert.IsType<CipherTransformStream>(provider.TryTransform(new MemoryStream(smallCipher), smallPath, smallCipher)))
                {
                    Assert.Equal(-1, transformed.RangeLength);
                    Assert.Equal(smallPlain, ReadAllBytes(transformed));
                }

                byte[] opaquePlain = BuildLz4Bundle("CAB-opaque", CreateSerializedNodePayload(CreateRandomPayload(150_000, 777)));
                string opaquePath = Path.Combine(bundleDir, "opaque_pack.bundle");
                byte[] opaqueCipher = XorAesCtrLittleEndian(opaquePlain, DeriveKey("BetaPass-99", "opaque_pack"), 65536);
                Assert.Null(provider.TryTransform(new MemoryStream(opaqueCipher), opaquePath, opaqueCipher));

                byte[] multiPlain = BuildLz4Bundle("CAB-multi", CreateSerializedNodePayload(CreateStructuredPayload(400_000, 4242)), 131072);
                string multiPath = Path.Combine(bundleDir, "multi_pack.bundle");
                byte[] multiCipher = XorAesCtrLittleEndian(multiPlain, DeriveKey("BetaPass-99", "multi_pack"), 65536);
                Assert.True(multiCipher.Length > 65536 * 2);
                using (var transformed = Assert.IsType<CipherTransformStream>(provider.TryTransform(new MemoryStream(multiCipher), multiPath, multiCipher)))
                {
                    Assert.Equal(65536, transformed.RangeLength);
                    AssertRandomSeekMatches(transformed, multiPlain);
                }

                using (var transformed = Assert.IsType<CipherTransformStream>(provider.TryTransform(new MemoryStream(opaqueCipher), opaquePath, opaqueCipher)))
                {
                    Assert.Equal(65536, transformed.RangeLength);
                    AssertRandomSeekMatches(transformed, opaquePlain);
                }

                File.WriteAllBytes(metadataPath, BuildMetadata(39, "x", "GammaPass", "y"));
                Assert.Equal(new[] { "x", "GammaPass", "y" }, PassphraseCtrTransformProvider.ReadMetadataLiterals(File.ReadAllBytes(metadataPath)));
                Assert.Empty(PassphraseCtrTransformProvider.ReadMetadataLiterals(new byte[64]));
            }
            finally
            {
                PassphraseCtrTransformProvider.ClearCache();
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void DeclarativeProfiles_RijndaelCbc_HeaderMasks_AndPbkdf1Ctr()
        {
            DecryptionHelper.Clear();
            try
            {
                byte[] plain = BuildHeaderOnlyBundle("6000.3.13f1", 5000);
                string tempDir = CreateTempDirectory();
                try
                {
                    var rijndaelProfile = new DecryptionProfile
                    {
                        Id = "test-rijndael",
                        Algorithm = "rijndael-cbc",
                        FileNamePattern = "*.bundle",
                        BlockSizeBits = 256,
                        KeyUtf8 = SampleKey,
                        IvUtf8 = SampleIv,
                        Padding = "pkcs7",
                        ValidateUnityHeader = true
                    };
                    Assert.True(rijndaelProfile.Validate(out var error), error);
                    DecryptionHelper.RegisterProfile(rijndaelProfile);

                    var cipher = new RijndaelBlockCipher(Encoding.ASCII.GetBytes(SampleKey), 256);
                    byte[] encrypted = EncryptCbc(cipher, Pad(plain, 32), Encoding.ASCII.GetBytes(SampleIv));
                    string filePath = Path.Combine(tempDir, "rijndael.bundle");
                    using (var decrypted = Assert.IsType<RijndaelCbcTransformStream>(DecryptionHelper.CheckAndDecryptStream(new MemoryStream(encrypted), filePath, out string? providerId, out _)))
                    {
                        Assert.Equal("DeclarativeProfile", providerId);
                        AssertRandomSeekMatches(decrypted, plain);
                    }

                    var wrongBlockSize = rijndaelProfile.Clone();
                    wrongBlockSize.BlockSizeBits = 160;
                    Assert.False(wrongBlockSize.Validate(out _));
                    var wrongIv = rijndaelProfile.Clone();
                    wrongIv.IvUtf8 = "short";
                    Assert.False(wrongIv.Validate(out _));

                    byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i * 5 + 1)).ToArray();
                    byte[] iv = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();
                    var maskedProfile = new DecryptionProfile
                    {
                        Id = "test-masked-aes",
                        Algorithm = "aes-cbc",
                        FileNamePattern = "*.enc",
                        KeyHex = Convert.ToHexString(key),
                        IvHex = Convert.ToHexString(iv),
                        Padding = "pkcs7",
                        HeaderMask = new HeaderMaskProfile { Type = "xorshift32", Seed = "0x12345678", Length = 256 },
                        ValidateUnityHeader = true
                    };
                    Assert.True(maskedProfile.Validate(out error), error);
                    Assert.NotSame(maskedProfile.HeaderMask, maskedProfile.Clone().HeaderMask);
                    DecryptionHelper.RegisterProfile(maskedProfile);

                    byte[] masked = (byte[])plain.Clone();
                    XorInPlace(masked, HeaderMasks.XorShift32(0x12345678, 256));
                    byte[] maskedCipher = EncryptAesCbc(masked, key, iv);
                    using (var decrypted = Assert.IsType<HeaderMaskStream>(DecryptionHelper.CheckAndDecryptStream(new MemoryStream(maskedCipher), Path.Combine(tempDir, "masked.enc"), out _, out _)))
                    {
                        AssertRandomSeekMatches(decrypted, plain);
                    }

                    var prefixIvProfile = maskedProfile.Clone();
                    prefixIvProfile.Id = "test-masked-aes-prefix-iv";
                    prefixIvProfile.HeaderMask = new HeaderMaskProfile { Type = "hmac-sha256-counter", Length = 4096 };
                    prefixIvProfile.IvHex = null;
                    prefixIvProfile.IvFromStreamPrefix = true;
                    Assert.True(prefixIvProfile.Validate(out error), error);
                    DecryptionHelper.RegisterProfile(prefixIvProfile);

                    byte[] hmacMasked = (byte[])plain.Clone();
                    XorInPlace(hmacMasked, HeaderMasks.HmacSha256Counter(key, Math.Min(4096, plain.Length)));
                    byte[] prefixCipher = iv.Concat(EncryptAesCbc(hmacMasked, key, iv)).ToArray();
                    using (var decrypted = Assert.IsType<HeaderMaskStream>(DecryptionHelper.CheckAndDecryptStream(new MemoryStream(prefixCipher), Path.Combine(tempDir, "prefix.enc"), out _, out _)))
                    {
                        Assert.Equal(16, Assert.IsType<CipherTransformStream>(decrypted.InnerStream).HeaderSkipBytes);
                        Assert.Equal(plain.Length, decrypted.Length);
                        AssertRandomSeekMatches(decrypted, plain);
                    }

                    var passphraseProfile = new DecryptionProfile
                    {
                        Id = "test-passphrase-ctr",
                        Algorithm = "aes-ctr",
                        FileNamePattern = "*.bundle",
                        KeyUtf8 = "Zeta7-passphrase_42",
                        KeyDerivation = "pbkdf1-sha1",
                        UseFileNameAsSalt = true,
                        UseFileNameWithoutExtension = true,
                        CounterStart = 1,
                        CounterLittleEndian = true,
                        RangeLength = 128,
                        ValidateUnityHeader = true
                    };
                    Assert.True(passphraseProfile.Validate(out error), error);
                    Assert.Equal(StreamCipherAlgorithm.RijndaelCbc, rijndaelProfile.ResolveAlgorithm());
                    DecryptionHelper.RegisterProfile(passphraseProfile);

                    byte[] ctrCipher = XorAesCtrLittleEndian(plain, DeriveKey("Zeta7-passphrase_42", "salted_name"), 128);
                    using (var decrypted = Assert.IsType<CipherTransformStream>(DecryptionHelper.CheckAndDecryptStream(new MemoryStream(ctrCipher), Path.Combine(tempDir, "salted_name.bundle"), out _, out _)))
                    {
                        AssertRandomSeekMatches(decrypted, plain);
                    }
                    using (Stream? renamed = DecryptionHelper.CheckAndDecryptStream(new MemoryStream(ctrCipher), Path.Combine(tempDir, "renamed.bundle"), out string? renamedProvider, out _))
                    {
                        Assert.Null(renamedProvider);
                        Assert.Equal(ctrCipher, ReadAllBytes(renamed!));
                    }

                    var tooLong = passphraseProfile.Clone();
                    tooLong.DerivedKeyLength = 32;
                    Assert.False(tooLong.Validate(out _));
                    var badMask = maskedProfile.Clone();
                    badMask.HeaderMask = new HeaderMaskProfile { Type = "nope" };
                    Assert.False(badMask.Validate(out _));
                    Assert.False(new DecryptionProfile { Algorithm = "rijndael-cbc", KeyUtf8 = SampleKey, IvUtf8 = SampleIv }.Validate(out _));
                    Assert.True(new DecryptionProfile { Algorithm = "aes-ctr", KeyUtf8 = SampleKey, Padding = "pkcs7", RangeLength = -1 }.Validate(out error), error);
                }
                finally
                {
                    TryDeleteDirectory(tempDir);
                }
            }
            finally
            {
                DecryptionHelper.Clear();
            }
        }

        private static byte[] CreateMask(HeaderMaskProfile profile, byte[] key, long cipherLength, int expectedLength)
        {
            Assert.True(profile.TryCreate(key, cipherLength, out byte[] mask));
            Assert.Equal(expectedLength, mask.Length);
            return mask;
        }

        private static byte[] DeriveKey(string passphrase, string salt)
            => LegacyKeyDerivation.Pbkdf1Sha1(Encoding.UTF8.GetBytes(passphrase), Encoding.UTF8.GetBytes(salt), 100, 16);

        private static byte[] BuildFakeAssembly(string passphrase, int extraStringCount)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[1024]);
            foreach (string text in new[] { "System.ArgumentException", "Some resource message", passphrase, "mscorlib" })
            {
                ms.Write(Encoding.Unicode.GetBytes(text));
                ms.Write(new byte[2]);
            }

            var random = new Random(12345);
            var buffer = new byte[4096];
            for (int i = 0; i < extraStringCount; i++)
            {
                random.NextBytes(buffer);
                ms.Write(buffer);
                ms.Write(Encoding.Unicode.GetBytes("token_" + i.ToString("X8")));
                ms.Write(new byte[2]);
            }

            return ms.ToArray();
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

        private static byte[] Transform(RijndaelBlockCipher cipher, byte[] input, bool encrypt)
        {
            var output = new byte[cipher.BlockSize];
            if (encrypt)
            {
                cipher.EncryptBlock(input, output);
            }
            else
            {
                cipher.DecryptBlock(input, output);
            }

            return output;
        }

        private static byte[] Pad(byte[] data, int blockSize)
        {
            int pad = blockSize - data.Length % blockSize;
            var padded = new byte[data.Length + pad];
            data.CopyTo(padded, 0);
            Array.Fill(padded, (byte)pad, data.Length, pad);
            return padded;
        }

        private static byte[] EncryptCbc(RijndaelBlockCipher cipher, byte[] plaintext, byte[] iv)
        {
            var ciphertext = new byte[plaintext.Length];
            byte[] previous = iv;
            for (int offset = 0; offset < plaintext.Length; offset += cipher.BlockSize)
            {
                var block = new byte[cipher.BlockSize];
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] = (byte)(plaintext[offset + i] ^ previous[i]);
                }

                cipher.EncryptBlock(block, block);
                block.CopyTo(ciphertext, offset);
                previous = block;
            }

            return ciphertext;
        }

        private static byte[] ReferenceEncrypt(byte[] key, int blockBits, byte[] input)
        {
            int nb = blockBits / 32;
            int nk = key.Length / 4;
            int nr = Math.Max(nk, nb) + 6;
            byte[] sbox = BuildReferenceSBox();
            int[] shifts = nb == 8 ? new[] { 0, 1, 3, 4 } : new[] { 0, 1, 2, 3 };

            var words = new byte[nb * (nr + 1)][];
            for (int i = 0; i < nk; i++)
            {
                words[i] = key.AsSpan(i * 4, 4).ToArray();
            }

            byte rcon = 1;
            for (int i = nk; i < words.Length; i++)
            {
                byte[] temp = (byte[])words[i - 1].Clone();
                if (i % nk == 0)
                {
                    temp = new[] { temp[1], temp[2], temp[3], temp[0] };
                    for (int b = 0; b < 4; b++)
                    {
                        temp[b] = sbox[temp[b]];
                    }

                    temp[0] ^= rcon;
                    rcon = ReferenceXTime(rcon);
                }
                else if (nk > 6 && i % nk == 4)
                {
                    for (int b = 0; b < 4; b++)
                    {
                        temp[b] = sbox[temp[b]];
                    }
                }

                words[i] = new byte[4];
                for (int b = 0; b < 4; b++)
                {
                    words[i][b] = (byte)(words[i - nk][b] ^ temp[b]);
                }
            }

            byte[] state = (byte[])input.Clone();
            void AddRoundKey(int round)
            {
                for (int c = 0; c < nb; c++)
                {
                    for (int r = 0; r < 4; r++)
                    {
                        state[c * 4 + r] ^= words[round * nb + c][r];
                    }
                }
            }

            void SubBytes()
            {
                for (int i = 0; i < state.Length; i++)
                {
                    state[i] = sbox[state[i]];
                }
            }

            void ShiftRows()
            {
                var next = new byte[state.Length];
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < nb; c++)
                    {
                        next[c * 4 + r] = state[((c + shifts[r]) % nb) * 4 + r];
                    }
                }

                Array.Copy(next, state, state.Length);
            }

            void MixColumns()
            {
                for (int c = 0; c < nb; c++)
                {
                    byte a0 = state[c * 4], a1 = state[c * 4 + 1], a2 = state[c * 4 + 2], a3 = state[c * 4 + 3];
                    state[c * 4] = (byte)(ReferenceMul(a0, 2) ^ ReferenceMul(a1, 3) ^ a2 ^ a3);
                    state[c * 4 + 1] = (byte)(a0 ^ ReferenceMul(a1, 2) ^ ReferenceMul(a2, 3) ^ a3);
                    state[c * 4 + 2] = (byte)(a0 ^ a1 ^ ReferenceMul(a2, 2) ^ ReferenceMul(a3, 3));
                    state[c * 4 + 3] = (byte)(ReferenceMul(a0, 3) ^ a1 ^ a2 ^ ReferenceMul(a3, 2));
                }
            }

            AddRoundKey(0);
            for (int round = 1; round < nr; round++)
            {
                SubBytes();
                ShiftRows();
                MixColumns();
                AddRoundKey(round);
            }

            SubBytes();
            ShiftRows();
            AddRoundKey(nr);
            return state;
        }

        private static byte[] BuildReferenceSBox()
        {
            var sbox = new byte[256];
            byte p = 1, q = 1;
            do
            {
                p ^= ReferenceXTime(p);
                q ^= (byte)(q << 1);
                q ^= (byte)(q << 2);
                q ^= (byte)(q << 4);
                if ((q & 0x80) != 0)
                {
                    q ^= 0x09;
                }

                byte x = (byte)(q ^ RotateLeft8(q, 1) ^ RotateLeft8(q, 2) ^ RotateLeft8(q, 3) ^ RotateLeft8(q, 4));
                sbox[p] = (byte)(x ^ 0x63);
            } while (p != 1);

            sbox[0] = 0x63;
            return sbox;
        }

        private static byte RotateLeft8(byte value, int bits) => (byte)((value << bits) | (value >> (8 - bits)));

        private static byte ReferenceXTime(byte value)
        {
            int doubled = value << 1;
            if ((doubled & 0x100) != 0)
            {
                doubled ^= 0x1B;
            }

            return (byte)doubled;
        }

        private static byte ReferenceMul(byte value, int factor)
        {
            int result = 0;
            int v = value;
            for (int f = factor; f != 0; f >>= 1)
            {
                if ((f & 1) != 0)
                {
                    result ^= v;
                }

                v = ReferenceXTime((byte)v);
            }

            return (byte)result;
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

        private static byte[] XorAesCtrLittleEndian(byte[] data, byte[] key, long length)
        {
            var output = (byte[])data.Clone();
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            var counter = new byte[16];
            int affected = (int)Math.Min(length, data.Length);
            for (int offset = 0; offset < affected; offset += 16)
            {
                Array.Clear(counter);
                BinaryPrimitives.WriteUInt64LittleEndian(counter, (ulong)(offset / 16) + 1);
                byte[] keystream = aes.EncryptEcb(counter, PaddingMode.None);
                int count = Math.Min(16, affected - offset);
                for (int i = 0; i < count; i++)
                {
                    output[offset + i] ^= keystream[i];
                }
            }

            return output;
        }

        private static void XorInPlace(byte[] data, byte[] mask)
        {
            for (int i = 0; i < mask.Length && i < data.Length; i++)
            {
                data[i] ^= mask[i];
            }
        }

        private static byte[] CreatePatternPayload(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(i * 37 + 19);
            }

            return data;
        }

        private static byte[] CreateRandomPayload(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static byte[] CreateStructuredPayload(int length, int seed)
        {
            var data = new byte[length];
            var random = new Random(seed);
            for (int i = 0; i < length; i += 8)
            {
                data[i] = (byte)random.Next(1, 256);
            }

            return data;
        }

        private static byte[] CreateSemiCompressiblePayload(int length, int seed)
        {
            var data = new byte[length];
            var random = new Random(seed);
            var page = new byte[2048];
            for (int offset = 0; offset < length; offset += page.Length * 4)
            {
                random.NextBytes(page);
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    Array.Copy(page, 0, data, offset + repeat * page.Length, Math.Min(page.Length, length - offset - repeat * page.Length));
                }
            }

            return data;
        }

        private static byte[] CreateSerializedNodePayload(byte[] payload)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 22u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 128u);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24, 8), payload.Length);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32, 8), 48L);
            return payload;
        }

        private static byte[] BuildHeaderOnlyBundle(string unityVersion, int totalLength)
        {
            byte[] bundle = CreatePatternPayload(totalLength);
            using var ms = new MemoryStream(bundle);
            ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            WriteUInt32BE(ms, 8u);
            ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            ms.Write(Encoding.ASCII.GetBytes(unityVersion + "\0"));
            WriteInt64BE(ms, totalLength);
            return bundle;
        }

        private static byte[] BuildLz4Bundle(string cabName, byte[] nodeData, int blockSize = 0)
        {
            if (blockSize <= 0)
            {
                blockSize = nodeData.Length;
            }

            var chunks = new List<byte[]>();
            for (int offset = 0; offset < nodeData.Length; offset += blockSize)
            {
                int count = Math.Min(blockSize, nodeData.Length - offset);
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
                for (int offset = 0, i = 0; i < chunks.Count; offset += blockSize, i++)
                {
                    WriteUInt32BE(ms, (uint)Math.Min(blockSize, nodeData.Length - offset));
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
                WriteUInt32BE(ms, 6u);
                ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
                ms.Write(Encoding.ASCII.GetBytes("2022.3.62f2\0"));
                header = ms.ToArray();
            }

            long totalSize = header.Length + 20 + compBlocksInfo.Length + chunks.Sum(c => (long)c.Length);
            using var finalMs = new MemoryStream();
            finalMs.Write(header);
            WriteInt64BE(finalMs, totalSize);
            WriteUInt32BE(finalMs, (uint)compBlocksInfo.Length);
            WriteUInt32BE(finalMs, (uint)uncompBlocksInfo.Length);
            WriteUInt32BE(finalMs, (uint)CompressionType.Lz4);
            finalMs.Write(compBlocksInfo);
            foreach (byte[] chunk in chunks)
            {
                finalMs.Write(chunk);
            }

            return finalMs.ToArray();
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            stream.Position = 0;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static void AssertRandomSeekMatches(Stream stream, byte[] expected)
        {
            Assert.Equal(expected, ReadAllBytes(stream));
            int[] offsets = { 0, 1, 15, 16, 31, 32, 33, 127, 128, 129, 255, 256, 257, expected.Length / 2, expected.Length - 1 };
            foreach (int offset in offsets)
            {
                if (offset >= expected.Length)
                {
                    continue;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                int len = Math.Min(45, expected.Length - offset);
                var buffer = new byte[len];
                Assert.Equal(len, stream.Read(buffer, 0, len));
                Assert.Equal(expected.AsSpan(offset, len).ToArray(), buffer);
            }
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
            string path = Path.Combine(Path.GetTempPath(), "AssetStudioPassphraseTests-" + Guid.NewGuid().ToString("N"));
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
