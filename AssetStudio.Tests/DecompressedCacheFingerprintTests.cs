using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetStudio.CLI;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class DecompressedCacheFingerprintTests
    {
        [Fact]
        public void Fingerprint_IsNullWithoutDecryption()
        {
            using var source = new MemoryStream(new byte[256]);
            Assert.Null(DecryptionFingerprints.Compute(null, source, "Any"));
            Assert.Null(DecryptionFingerprints.Compute(source, source, null));
            Assert.Null(DecryptionFingerprints.Compute(source, source, string.Empty));

            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            try
            {
                string path = Path.Combine(root, "plain.bundle");
                File.WriteAllBytes(path, RuntimeGameCaptureTests.BuildLz4Bundle("CAB-plain", 3000, 3));
                using var reader = new FileReader(path);
                Assert.Null(reader.AppliedDecryptionProvider);
                Assert.Null(reader.DecryptionFingerprint);
            }
            finally
            {
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Fingerprint_IsStableForEqualTransforms_AndChangesWithEveryParameter()
        {
            byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] otherKey = key.ToArray();
            otherKey[0] ^= 0xFF;
            byte[] iv = new byte[16];
            iv[3] = 7;
            byte[] otherIv = iv.ToArray();
            otherIv[3] = 9;
            byte[] mask = new byte[256];
            mask[0] = 0x55;
            byte[] otherMask = mask.ToArray();
            otherMask[0] = 0x66;

            string aes = Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv));
            Assert.StartsWith("v1:", aes, StringComparison.Ordinal);
            Assert.Equal(67, aes.Length);
            Assert.Equal(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, otherKey, iv)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, otherIv)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesEcb, key, iv)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv, headerSkipBytes: 16)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv, rangeStart: 16)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv, rangeLength: 2048)));
            Assert.NotEqual(aes, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv, pkcs7Padding: true)));

            string ctr = Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCtr, key, iv));
            Assert.NotEqual(ctr, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCtr, key, iv, counterStart: 1)));
            Assert.NotEqual(ctr, Fp(s => new CipherTransformStream(s, StreamCipherAlgorithm.AesCtr, key, iv, counterLittleEndian: true)));

            string xor = Fp(s => new XorTransformStream(s, key));
            Assert.Equal(xor, Fp(s => new XorTransformStream(s, key)));
            Assert.NotEqual(xor, Fp(s => new XorTransformStream(s, otherKey)));
            Assert.NotEqual(xor, Fp(s => new XorTransformStream(s, key, rangeStart: 8)));

            string block512 = Fp(s => new DecryptedStream(s, "token-a"));
            Assert.Equal(block512, Fp(s => new DecryptedStream(s, "token-a")));
            Assert.NotEqual(block512, Fp(s => new DecryptedStream(s, "token-b")));

            string rijndael = Fp(s => new RijndaelCbcTransformStream(s, new RijndaelBlockCipher(key, 256), key));
            Assert.Equal(rijndael, Fp(s => new RijndaelCbcTransformStream(s, new RijndaelBlockCipher(key, 256), key)));
            Assert.NotEqual(rijndael, Fp(s => new RijndaelCbcTransformStream(s, new RijndaelBlockCipher(otherKey, 256), key)));
            Assert.NotEqual(rijndael, Fp(s => new RijndaelCbcTransformStream(s, new RijndaelBlockCipher(key, 256), otherKey)));
            Assert.NotEqual(rijndael, Fp(s => new RijndaelCbcTransformStream(s, new RijndaelBlockCipher(key, 128), iv)));

            string chained = Fp(s => new HeaderMaskStream(new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv), mask));
            Assert.NotEqual(chained, aes);
            Assert.NotEqual(chained, Fp(s => new HeaderMaskStream(new CipherTransformStream(s, StreamCipherAlgorithm.AesCbc, key, iv), otherMask)));
            Assert.NotEqual(chained, Fp(s => new CipherTransformStream(new HeaderMaskStream(s, mask), StreamCipherAlgorithm.AesCbc, key, iv)));

            string skip = Fp(s => new SubStream(s, 16, 2048));
            Assert.Equal(skip, Fp(s => new SubStream(s, 16, 2048)));
            Assert.NotEqual(skip, Fp(s => new SubStream(s, 32, 2048)));
            Assert.NotEqual(skip, Fp(s => new SubStream(s, 16, 1024)));
        }

        [Fact]
        public void Fingerprint_UnrecognisedStream_UsesProviderAndContent_UntilItDeclaresItsOwn()
        {
            byte[] data = new byte[DecryptionFingerprints.UnverifiedPrefixBytes * 2];
            new Random(77).NextBytes(data);

            string plugin = Fp(s => new XorWrapper(s, 0x5A), data, "TestPlugin");
            Assert.StartsWith("v1:", plugin, StringComparison.Ordinal);
            Assert.Equal(plugin, Fp(s => new XorWrapper(s, 0x5A), data, "TestPlugin"));
            Assert.NotEqual(plugin, Fp(s => new XorWrapper(s, 0x5B), data, "TestPlugin"));
            Assert.NotEqual(plugin, Fp(s => new XorWrapper(s, 0x5A), data, "OtherPlugin"));
            Assert.NotEqual(plugin, Fp(s => new XorWrapper(s, 0x5A), Xor(data, data), "TestPlugin"));

            string declared = Fp(s => new DeclaredWrapper(s, 0x5A, "profile-v1"), data, "TestPlugin");
            Assert.Equal(declared, Fp(s => new DeclaredWrapper(s, 0x33, "profile-v1"), Xor(data, data), "TestPlugin"));
            Assert.NotEqual(declared, Fp(s => new DeclaredWrapper(s, 0x5A, "profile-v2"), data, "TestPlugin"));

            string broken = Fp(s => new DeclaredWrapper(s, 0x5A, null), data, "TestPlugin");
            Assert.StartsWith("v1:", broken, StringComparison.Ordinal);
            Assert.Equal(broken, Fp(s => new DeclaredWrapper(s, 0x5A, null), data, "TestPlugin"));
            Assert.NotEqual(declared, broken);
            Assert.NotEqual(broken, Fp(s => new DeclaredWrapper(s, 0x33, null), data, "TestPlugin"));
            Assert.Equal(declared, Fp(s => new DeclaredWrapper(s, 0x33, "profile-v1"), data, "TestPlugin"));

            string unreadable = Fp(s => new ThrowingReadWrapper(s), data, "TestPlugin");
            Assert.StartsWith("v1:", unreadable, StringComparison.Ordinal);
            Assert.Equal(unreadable, Fp(s => new ThrowingReadWrapper(s), Xor(data, data), "TestPlugin"));
            Assert.NotEqual(plugin, unreadable);
        }

        [Fact]
        public void Cache_PlainBundleEntriesStayValid_WithOrWithoutTheNewManifestField()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            try
            {
                string path = Path.Combine(root, "plain.bundle");
                byte[] payload = TextPayload("CliPlain", "plain");
                File.WriteAllBytes(path, RuntimeGameCaptureTests.BuildLz4Bundle("CAB-plain", payload));
                var cache = new DecompressedBundleCache(Path.Combine(root, "cache"));

                (string? fingerprint, bool fromCache) = Load(path, cache, payload);
                Assert.Null(fingerprint);
                Assert.False(fromCache);
                (fingerprint, fromCache) = Load(path, cache, payload);
                Assert.Null(fingerprint);
                Assert.True(fromCache);
                Assert.Equal(1L, cache.CacheMisses);
                Assert.Equal(1L, cache.CacheHits);

                string manifestPath = Directory.GetFiles(Path.Combine(root, "cache"), "manifest.json", SearchOption.AllDirectories).Single();
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
                Assert.True(manifest.Remove("DecryptionFingerprint"));
                File.WriteAllText(manifestPath, manifest.ToJsonString());

                (_, fromCache) = Load(path, cache, payload);
                Assert.True(fromCache);
                Assert.Equal(1L, cache.CacheMisses);
                Assert.True(cache.TryOpenCachedStreamFiles(path, out StreamFile[] direct));
                using Stream node = direct[0].stream!;
                Assert.Equal(payload, ReadAll(node));
            }
            finally
            {
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Cache_DecryptedBundle_IsReextractedWhenTheTransformChanges()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            DecryptionDesign design = BuildTwoValidDecryptions();
            string path = Path.Combine(root, "pack.bundle");
            File.WriteAllBytes(path, design.Source);
            var cache = new DecompressedBundleCache(Path.Combine(root, "cache"));
            DecryptionProfile first = XorProfile("fingerprint-first", design.FirstKey);
            DecryptionProfile second = XorProfile("fingerprint-second", design.SecondKey);
            try
            {
                DecryptionHelper.RegisterProfile(first);
                (string? firstFingerprint, bool fromCache) = Load(path, cache, design.First);
                Assert.NotNull(firstFingerprint);
                Assert.False(fromCache);
                (string? repeated, fromCache) = Load(path, cache, design.First);
                Assert.True(fromCache);
                Assert.Equal(firstFingerprint, repeated);

                DecryptionHelper.RemoveProfile(first.Id);
                DecryptionHelper.RegisterProfile(second);
                (string? secondFingerprint, fromCache) = Load(path, cache, design.Second);
                Assert.NotNull(secondFingerprint);
                Assert.NotEqual(firstFingerprint, secondFingerprint);
                Assert.False(fromCache);
                (_, fromCache) = Load(path, cache, design.Second);
                Assert.True(fromCache);
                Assert.Equal(2L, cache.CacheMisses);
                Assert.Equal(2L, cache.CacheHits);

                Assert.False(cache.TryOpenCachedStreamFiles(path, out _));
                Assert.False(cache.TryOpenCachedStreamFiles(path, out _, decryptionFingerprint: firstFingerprint));
                Assert.True(cache.TryOpenCachedStreamFiles(path, out StreamFile[] direct, decryptionFingerprint: secondFingerprint));
                using Stream node = direct[0].stream!;
                Assert.Equal(design.Second, ReadAll(node));
            }
            finally
            {
                DecryptionHelper.RemoveProfile(first.Id);
                DecryptionHelper.RemoveProfile(second.Id);
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void Cli_ListReflectsTheCurrentProfileInsteadOfStaleCachedBlocks()
        {
            string root = RuntimeGameCaptureTests.CreateTempDirectory();
            string? previous = Environment.GetEnvironmentVariable("ASSETSTUDIO_DECOMPRESSION_CACHE_DIR");
            DecryptionDesign design = BuildTwoValidDecryptions();
            string firstProfile = Path.Combine(root, "first.json");
            string secondProfile = Path.Combine(root, "second.json");
            try
            {
                string input = Path.Combine(root, "input");
                Directory.CreateDirectory(input);
                File.WriteAllBytes(Path.Combine(input, "pack.bundle"), design.Source);
                File.WriteAllText(firstProfile, JsonSerializer.Serialize(XorProfile("fingerprint-first", design.FirstKey)));
                File.WriteAllText(secondProfile, JsonSerializer.Serialize(XorProfile("fingerprint-second", design.SecondKey)));
                Environment.SetEnvironmentVariable("ASSETSTUDIO_DECOMPRESSION_CACHE_DIR", Path.Combine(root, "cache"));

                (int exit, string first, _) = InvokeCli("list", "--no-local-profiles", "--profiles", firstProfile, input);
                Assert.Equal(0, exit);
                Assert.Contains("CliAlpha", first, StringComparison.Ordinal);
                (exit, string repeated, _) = InvokeCli("list", "--no-local-profiles", "--profiles", firstProfile, input);
                Assert.Equal(0, exit);
                Assert.Equal(first, repeated);

                DecryptionHelper.RemoveProfile("fingerprint-first");
                (exit, string second, string stderr) = InvokeCli("list", "--no-local-profiles", "--profiles", secondProfile, input);
                Assert.Equal(0, exit);
                Assert.Equal(string.Empty, stderr);
                Assert.Contains("CliBravo", second, StringComparison.Ordinal);
                Assert.DoesNotContain("CliAlpha", second, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASSETSTUDIO_DECOMPRESSION_CACHE_DIR", previous);
                DecryptionHelper.RemoveProfile("fingerprint-first");
                DecryptionHelper.RemoveProfile("fingerprint-second");
                RuntimeGameCaptureTests.TryDeleteDirectory(root);
            }
        }

        private sealed record DecryptionDesign(byte[] Source, byte[] FirstKey, byte[] SecondKey, byte[] First, byte[] Second);

        private static DecryptionDesign BuildTwoValidDecryptions()
        {
            byte[] first = TextPayload("CliAlpha", "alpha text");
            byte[] second = TextPayload("CliBravo", "bravo text");
            byte[] firstBundle = RuntimeGameCaptureTests.BuildLz4Bundle("CAB-shared", first);
            byte[] secondBundle = RuntimeGameCaptureTests.BuildLz4Bundle("CAB-shared", second);
            Assert.Equal(firstBundle.Length, secondBundle.Length);

            byte[] firstKey = new byte[firstBundle.Length];
            new Random(91).NextBytes(firstKey);
            byte[] source = Xor(firstBundle, firstKey);
            return new DecryptionDesign(source, firstKey, Xor(source, secondBundle), first, second);
        }

        private static byte[] TextPayload(string name, string text)
            => FormatsAndCliTests.BuildSerializedFileWithObjects(
                "2021.3.16f1",
                new[] { (30L, (int)ClassIDType.TextAsset, FormatsAndCliTests.BuildTextAssetBinary(name, text), true) });

        private static DecryptionProfile XorProfile(string id, byte[] key)
            => new()
            {
                Id = id,
                Algorithm = "xor",
                XorKeyHex = Convert.ToHexString(key),
                FileNamePattern = "*.bundle",
                ValidateUnityHeader = true,
            };

        private static (string? Fingerprint, bool FromCache) Load(string path, DecompressedBundleCache cache, byte[] expectedNode)
        {
            using var reader = new FileReader(path);
            using var clone = reader.Clone();
            Assert.Equal(reader.DecryptionFingerprint, clone.DecryptionFingerprint);
            using var bundle = new BundleFile(reader, cache);
            Assert.Single(bundle.fileList);
            using Stream node = bundle.fileList[0].stream!;
            Assert.Equal(expectedNode, ReadAll(node));
            return (reader.DecryptionFingerprint, bundle.LoadedFromDecompressionCache);
        }

        private static byte[] ReadAll(Stream stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static byte[] Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            var result = new byte[left.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(left[i] ^ right[i]);
            }

            return result;
        }

        private static string Fp(Func<Stream, Stream> transform, byte[]? content = null, string providerId = "Test")
        {
            var source = new MemoryStream(content ?? new byte[8192]);
            using Stream transformed = transform(source);
            return DecryptionFingerprints.Compute(transformed, source, providerId)!;
        }

        private static (int ExitCode, string StdOut, string StdErr) InvokeCli(params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliRunner.Run(args, stdout, stderr);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }

        private class XorWrapper : Stream
        {
            private readonly Stream inner;
            private readonly byte key;
            private long position;

            public XorWrapper(Stream inner, byte key)
            {
                this.inner = inner;
                this.key = key;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => inner.Length;

            public override long Position
            {
                get => position;
                set => position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                inner.Position = position;
                int read = inner.Read(buffer, offset, count);
                for (int i = 0; i < read; i++)
                {
                    buffer[offset + i] ^= key;
                }

                position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    _ => inner.Length + offset,
                };
                return position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class DeclaredWrapper : XorWrapper, IDecryptionFingerprintSource
        {
            private readonly string? identity;

            public DeclaredWrapper(Stream inner, byte key, string? identity)
                : base(inner, key)
            {
                this.identity = identity;
            }

            public string GetFingerprint() => identity ?? throw new InvalidOperationException();
        }

        private sealed class ThrowingReadWrapper : XorWrapper
        {
            public ThrowingReadWrapper(Stream inner)
                : base(inner, 0x5A)
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new ApplicationException();
        }
    }
}
