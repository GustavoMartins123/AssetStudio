using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using K4os.Compression.LZ4;
using Xunit;

namespace AssetStudio.Tests
{
    [Collection("ResourceReaderXorHeuristic")]
    public sealed class RuntimeGameCaptureTests
    {
        private const string GameName = "Sample Game";
        private const string Revision = "6000.3.13f1";

        public static TheoryData<string> CipherScenarios => new()
        {
            "cbc-fixed-iv", "cbc-iv-prefix", "cbc-no-padding", "ecb-pkcs7", "ecb-no-padding", "rijndael256-cbc", "ctr-big-endian", "ctr-little-endian",
        };

        [Theory]
        [MemberData(nameof(CipherScenarios))]
        public void Capture_IdentifiesTheScheme_RecoversTheMask_AndDecryptsThroughThePipeline(string scenario)
        {
            bool padding = scenario is "cbc-fixed-iv" or "cbc-iv-prefix" or "ecb-pkcs7" or "rijndael256-cbc";
            bool ivPrefix = scenario is "cbc-iv-prefix" or "ctr-big-endian" or "ctr-little-endian";
            int maskLength = scenario switch
            {
                "cbc-fixed-iv" or "cbc-no-padding" => 256,
                "cbc-iv-prefix" or "ctr-big-endian" => 4096,
                "ecb-pkcs7" => 512,
                "rijndael256-cbc" => 96,
                _ => 0,
            };
            var random = new Random(41);
            byte[] key = new byte[32];
            byte[] iv = new byte[32];
            byte[] mask = new byte[maskLength];
            random.NextBytes(key);
            random.NextBytes(iv);
            random.NextBytes(mask);

            int alignment = padding && scenario != "rijndael256-cbc" ? 1 : 16;
            byte[] smallPlain = BuildBundle(2800, alignment, 9);
            byte[] bigPlain = BuildBundle(20000, alignment, 8);
            string root = CreateTempDirectory();
            string bundleDir = CreateGameLayout(root);
            string? profileId = null;
            try
            {
                string smallPath = Path.Combine(bundleDir, "small.enc");
                string bigPath = Path.Combine(bundleDir, "big.enc");
                File.WriteAllBytes(smallPath, Encrypt(scenario, smallPlain, key, iv, mask));
                File.WriteAllBytes(bigPath, Encrypt(scenario, bigPlain, key, iv, mask));
                string[] files = { smallPath, bigPath };

                List<FileTail> tails = KeyValidationContext.LoadTails(files);
                Assert.Equal(2, tails.Count);
                var image = new ManagedImage(random);
                image.AddArray(key).AddArray(new byte[32]);
                image.AddDump(MakeDump(bigPlain, maskLength >= 4096 ? 32760 : 8184));
                image.AddDump(MakeDump(smallPlain, smallPlain.Length));

                (List<CapturedKey> keys, List<PlaintextBundleDump> dumps) = Scan(image.ToArray(), KeyValidationContext.Create(tails, files, null));
                Assert.Equal(2, dumps.Count);
                if (padding)
                {
                    Assert.Single(keys);
                }
                else
                {
                    Assert.Empty(keys);
                    (keys, _) = Scan(image.ToArray(), KeyValidationContext.Create(tails, files, dumps));
                }

                CapturedKey captured = Assert.Single(keys);
                Assert.Equal(key, captured.Key);
                Assert.Equal(scenario == "rijndael256-cbc" ? CapturedCipherKind.Rijndael256 : CapturedCipherKind.Aes, captured.Kind);
                Assert.Equal(scenario.StartsWith("ecb", StringComparison.Ordinal) ? CapturedCipherMode.Ecb
                    : scenario.StartsWith("ctr", StringComparison.Ordinal) ? CapturedCipherMode.Ctr
                    : CapturedCipherMode.Cbc, captured.Mode);
                Assert.Equal(padding, captured.Padding);
                Assert.Equal(scenario == "ctr-little-endian" ? 1u : 0u, captured.CounterStart);
                Assert.Equal(scenario == "ctr-little-endian", captured.CounterLittleEndian);

                Assert.True(RuntimeCipherRecovery.TryRecover(captured, files, dumps, out CapturedCipher? cipher));
                Assert.NotNull(cipher);
                Assert.Equal(ivPrefix, cipher!.IvFromStreamPrefix);
                Assert.Equal(mask.Length, cipher.Mask.Length);
                int blockSize = captured.BlockSize;
                if (ivPrefix || captured.Mode != CapturedCipherMode.Cbc)
                {
                    Assert.Equal(mask, cipher.Mask);
                }
                else
                {
                    Assert.Equal(new byte[blockSize], cipher.Mask.Take(blockSize).ToArray());
                    Assert.Equal(mask.Skip(blockSize).ToArray(), cipher.Mask.Skip(blockSize).ToArray());
                    Assert.Equal(Xor(iv.AsSpan(0, blockSize).ToArray(), mask.Take(blockSize).ToArray()), cipher.FixedIv);
                }
                Assert.True(RuntimeCipherRecovery.IsMaskConfirmed(cipher));
                Assert.Equal(2, RuntimeCipherRecovery.ValidateOnFiles(cipher, files, out int checkedCount));
                Assert.Equal(2, checkedCount);

                DecryptionProfile profile = GameKeyCapture.BuildProfile(GameCaptureTarget.TryResolve(bigPath)!, cipher);
                Assert.True(profile.Validate(out string? error), error);
                Assert.Equal(scenario == "rijndael256-cbc" ? "rijndael-cbc"
                    : scenario.StartsWith("ctr", StringComparison.Ordinal) ? "aes-ctr"
                    : scenario.StartsWith("ecb", StringComparison.Ordinal) ? "aes-ecb"
                    : "aes-cbc", profile.Algorithm);
                Assert.Equal(padding, profile.Padding == "pkcs7");
                DecryptionHelper.RegisterProfile(profile);
                profileId = profile.Id;
                AssertDecrypted(bigPath, bigPlain);
                AssertDecrypted(smallPath, smallPlain);
            }
            finally
            {
                if (profileId != null)
                {
                    DecryptionHelper.RemoveProfile(profileId);
                }

                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void GameMemoryCapture_FindsKeysStoredAsManagedStrings()
        {
            var random = new Random(51);
            foreach (string format in new[] { "hex", "base64", "passphrase" })
            {
                byte[] key = new byte[32];
                random.NextBytes(key);
                string text = format switch
                {
                    "hex" => Convert.ToHexString(key),
                    "base64" => Convert.ToBase64String(key),
                    _ => RandomText(32, random),
                };
                if (format == "passphrase")
                {
                    key = Encoding.ASCII.GetBytes(text);
                }

                string folder = CreateTempDirectory();
                try
                {
                    string[] files = WriteCbcFiles(folder, key, random);
                    List<FileTail> tails = KeyValidationContext.LoadTails(files);
                    var image = new ManagedImage(random);
                    image.AddString("This sentence is not a key at all, honestly!");
                    image.AddString(Convert.ToHexString(new byte[16]));
                    image.AddString(Convert.ToBase64String(new byte[24]));
                    image.AddString(text);
                    (List<CapturedKey> keys, _) = Scan(image.ToArray(), KeyValidationContext.Create(tails, files, null));
                    CapturedKey captured = Assert.Single(keys);
                    Assert.True(key.SequenceEqual(captured.Key), format);
                }
                finally
                {
                    TryDeleteDirectory(folder);
                }
            }
        }

        [Fact]
        public void GameMemoryCapture_FindsKeyInsideALargerBuffer_AndEncodedInAByteArray()
        {
            var random = new Random(61);
            byte[] key = new byte[32];
            random.NextBytes(key);
            string folder = CreateTempDirectory();
            try
            {
                string[] files = WriteCbcFiles(folder, key, random);
                List<FileTail> tails = KeyValidationContext.LoadTails(files);
                var noise = new byte[48];
                random.NextBytes(noise);
                var largerBuffer = new ManagedImage(random);
                largerBuffer.AddArray(noise);
                largerBuffer.AddArray(key.Concat(new byte[16]).ToArray());
                (List<CapturedKey> fromBuffer, _) = Scan(largerBuffer.ToArray(), KeyValidationContext.Create(tails, files, null));
                CapturedKey inBuffer = Assert.Single(fromBuffer);
                Assert.True(key.SequenceEqual(inBuffer.Key));

                var encodedText = new ManagedImage(random);
                encodedText.AddArray(Encoding.ASCII.GetBytes(Convert.ToHexString(key)));
                (List<CapturedKey> fromText, _) = Scan(encodedText.ToArray(), KeyValidationContext.Create(tails, files, null));
                CapturedKey inText = Assert.Single(fromText);
                Assert.True(key.SequenceEqual(inText.Key));
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        [Fact]
        public void GameMemoryCapture_FindsShortKeys_AfterLargerCandidates()
        {
            var random = new Random(81);
            foreach (int keyLength in new[] { 16, 24 })
            {
                byte[] key = new byte[keyLength];
                random.NextBytes(key);
                string folder = CreateTempDirectory();
                try
                {
                    string[] files = WriteCbcFiles(folder, key, random);
                    List<FileTail> tails = KeyValidationContext.LoadTails(files);
                    var image = new ManagedImage(random);
                    for (int i = 0; i < 50; i++)
                    {
                        var noise = new byte[32];
                        random.NextBytes(noise);
                        image.AddArray(noise);
                    }

                    image.AddArray(key);
                    (List<CapturedKey> keys, _) = Scan(image.ToArray(), KeyValidationContext.Create(tails, files, null));
                    CapturedKey captured = Assert.Single(keys);
                    Assert.True(key.SequenceEqual(captured.Key), "key length " + keyLength);
                    Assert.Equal(CapturedCipherKind.Aes, captured.Kind);
                }
                finally
                {
                    TryDeleteDirectory(folder);
                }
            }
        }

        [Fact]
        public void GameMemoryCapture_IgnoresKeysThatDoNotMatchTheFiles()
        {
            var random = new Random(71);
            byte[] key = new byte[32];
            random.NextBytes(key);
            string folder = CreateTempDirectory();
            try
            {
                string[] files = WriteCbcFiles(folder, key, random);
                List<FileTail> tails = KeyValidationContext.LoadTails(files);
                var image = new ManagedImage(random);
                for (int i = 0; i < 200; i++)
                {
                    var noise = new byte[32];
                    random.NextBytes(noise);
                    image.AddArray(noise);
                }

                (List<CapturedKey> keys, List<PlaintextBundleDump> dumps) = Scan(image.ToArray(), KeyValidationContext.Create(tails, files, null));
                Assert.Empty(keys);
                Assert.Empty(dumps);
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        [Fact]
        public void RawHeaderMask_RoundTrips_AndRequiresMatchingLength()
        {
            var mask = Enumerable.Range(1, 4096).Select(i => (byte)i).ToArray();
            var profile = new HeaderMaskProfile { Type = "raw", Length = mask.Length, DataBase64 = Convert.ToBase64String(mask) };
            Assert.True(profile.Validate(out string? error), error);
            Assert.True(profile.TryCreate(Array.Empty<byte>(), 8192, out byte[] created));
            Assert.Equal(mask, created);
            Assert.True(profile.TryCreate(Array.Empty<byte>(), 3000, out created));
            Assert.Equal(mask.Take(3000).ToArray(), created);

            HeaderMaskProfile? clone = JsonSerializer.Deserialize<HeaderMaskProfile>(JsonSerializer.Serialize(profile));
            Assert.NotNull(clone);
            Assert.Equal("raw", clone!.Type);
            Assert.Equal(4096, clone.Length);
            Assert.Equal(mask, Convert.FromBase64String(clone.DataBase64!));

            var mismatch = new HeaderMaskProfile { Type = "raw", Length = 8, DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
            Assert.False(mismatch.Validate(out error));
            Assert.Contains("length", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(new HeaderMaskProfile { Type = "raw" }.Validate(out error));
            Assert.False(new HeaderMaskProfile { Type = "raw", Length = 3, DataBase64 = "***" }.Validate(out error));
            Assert.False(new HeaderMaskProfile { Type = "raw", Length = 0, DataBase64 = "AQID" }.Validate(out error));
        }

        [Fact]
        public void GameCaptureTarget_RequiresUnityDataFolderAndExecutable()
        {
            string root = CreateTempDirectory();
            try
            {
                string bundleDir = CreateGameLayout(root);
                string path = Path.Combine(bundleDir, "pack.enc");
                File.WriteAllBytes(path, new byte[96]);
                GameCaptureTarget? target = GameCaptureTarget.TryResolve(path);
                Assert.NotNull(target);
                Assert.Single(target!.SampleFiles);

                File.WriteAllBytes(Path.Combine(bundleDir, "notes.txt"), new byte[96]);
                File.WriteAllBytes(Path.Combine(bundleDir, "odd.enc"), new byte[100]);
                target = GameCaptureTarget.TryResolve(path);
                Assert.NotNull(target);
                Assert.Single(target!.SampleFiles);

                string loose = Path.Combine(root, "loose.enc");
                File.WriteAllBytes(loose, new byte[96]);
                Assert.Null(GameCaptureTarget.TryResolve(loose));
                Assert.Null(GameCaptureTarget.TryResolve(Path.Combine(bundleDir, "missing.enc")));
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void AntiCheatDetector_FlagsKnownComponents_AndIgnoresCleanFolders()
        {
            string root = CreateTempDirectory();
            try
            {
                string gameDirectory = Path.Combine(root, GameName);
                Assert.False(AntiCheatDetector.TryDetect(gameDirectory, out string? reason));
                string plugins = Path.Combine(gameDirectory, GameName + "_Data", "Plugins", "x86_64");
                Directory.CreateDirectory(plugins);
                File.WriteAllBytes(Path.Combine(plugins, "BEService.dll"), new byte[] { 1 });
                Assert.True(AntiCheatDetector.TryDetect(gameDirectory, out reason));
                Assert.Contains("BEService.dll", reason, StringComparison.Ordinal);
                File.Delete(Path.Combine(plugins, "BEService.dll"));
                Assert.False(AntiCheatDetector.TryDetect(gameDirectory, out _));
                Directory.CreateDirectory(Path.Combine(gameDirectory, "EasyAntiCheat"));
                Assert.True(AntiCheatDetector.TryDetect(gameDirectory, out reason));
                Assert.Contains("EasyAntiCheat", reason, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        [Fact]
        public void DecryptionKeyDialog_OffersGameCapture_WhenAnEncryptedFileBelongsToAUnityGame()
        {
            string root = CreateTempDirectory();
            try
            {
                string bundleDir = CreateGameLayout(root);
                string path = Path.Combine(bundleDir, "pack.enc");
                File.WriteAllBytes(path, new byte[96]);
                var barrier = new StreamEncryptionBarrier("EncryptedBundleContainer", path, "Encrypted container", 0.9, "AA BB CC DD");
                var context = new DecryptionDialogContext(false, root, 3, 1, 75.0, new[] { barrier });
                var viewModel = new DecryptionKeyDialogViewModel(context, userProfilesDirectoryOverride: root);
                Assert.True(viewModel.ShowGameCaptureSection);
                Assert.Contains(GameName, viewModel.CaptureGameSummaryText, StringComparison.Ordinal);
                Assert.True(viewModel.CaptureFromGameCommand.CanExecute(null));
                Assert.False(viewModel.CancelCaptureCommand.CanExecute(null));
                Assert.True(viewModel.SecondaryCloseCommand.CanExecute(null));

                var bare = new DecryptionDialogContext(false, root, 3, 0, 100.0, Array.Empty<StreamEncryptionBarrier>());
                var plainViewModel = new DecryptionKeyDialogViewModel(bare, userProfilesDirectoryOverride: root);
                Assert.False(plainViewModel.ShowGameCaptureSection);
                Assert.False(plainViewModel.CaptureFromGameCommand.CanExecute(null));

                // When encrypted files were reported (e.g., partial load banner or cached session) without
                // pre-populated barrier objects, the GUI still resolves the game executable from ProjectRoot
                // just like CLI capture-keys <folder>.
                var rootOnlyContext = new DecryptionDialogContext(false, root, 3, 1, 75.0, Array.Empty<StreamEncryptionBarrier>());
                var rootOnlyViewModel = new DecryptionKeyDialogViewModel(rootOnlyContext, userProfilesDirectoryOverride: root);
                Assert.Equal(GameKeyCapture.IsSupported, rootOnlyViewModel.ShowGameCaptureSection);
                if (GameKeyCapture.IsSupported)
                {
                    Assert.Contains(GameName, rootOnlyViewModel.CaptureGameSummaryText, StringComparison.Ordinal);
                    Assert.True(rootOnlyViewModel.CaptureFromGameCommand.CanExecute(null));
                }
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static string[] WriteCbcFiles(string folder, byte[] key, Random random)
        {
            var files = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                var iv = new byte[16];
                random.NextBytes(iv);
                var plain = new byte[2000 * (i + 1)];
                random.NextBytes(plain);
                string path = Path.Combine(folder, $"pack_{i}.enc");
                File.WriteAllBytes(path, EncryptAes(plain, key, iv, Array.Empty<byte>(), CipherMode.CBC, true, false));
                files.Add(path);
            }

            return files.ToArray();
        }

        private static (List<CapturedKey> Keys, List<PlaintextBundleDump> Dumps) Scan(byte[] image, KeyValidationContext context)
        {
            var keys = new Dictionary<string, CapturedKey>(StringComparer.Ordinal);
            var dumps = new Dictionary<string, PlaintextBundleDump>(StringComparer.Ordinal);
            GameMemoryCapture.ScanBuffer(image, context, 65536, keys, dumps);
            return (keys.Values.ToList(), dumps.Values.ToList());
        }

        private static void AssertDecrypted(string path, byte[] expected)
        {
            using var stream = File.OpenRead(path);
            using Stream? decoded = DecryptionHelper.CheckAndDecryptStream(stream, path, out string? providerId, out _);
            Assert.Equal("DeclarativeProfile", providerId);
            Assert.NotNull(decoded);
            using var copy = new MemoryStream();
            decoded!.CopyTo(copy);
            Assert.Equal(expected, copy.ToArray());
        }

        private static string RandomText(int length, Random random)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var chars = new char[length];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            return new string(chars);
        }

        internal static byte[] MakeDump(byte[] plain, int faithfulBytes)
        {
            var dump = new byte[65536];
            Array.Copy(plain, 0, dump, 0, Math.Min(plain.Length, faithfulBytes));
            return dump;
        }

        private static byte[] Encrypt(string scenario, byte[] plain, byte[] key, byte[] iv, byte[] mask)
        {
            return scenario switch
            {
                "cbc-fixed-iv" => EncryptAes(plain, key, iv.AsSpan(0, 16).ToArray(), mask, CipherMode.CBC, true, false),
                "cbc-iv-prefix" => EncryptAes(plain, key, iv.AsSpan(0, 16).ToArray(), mask, CipherMode.CBC, true, true),
                "cbc-no-padding" => EncryptAes(plain, key, iv.AsSpan(0, 16).ToArray(), mask, CipherMode.CBC, false, false),
                "ecb-pkcs7" => EncryptAes(plain, key, iv.AsSpan(0, 16).ToArray(), mask, CipherMode.ECB, true, false),
                "ecb-no-padding" => EncryptAes(plain, key, iv.AsSpan(0, 16).ToArray(), mask, CipherMode.ECB, false, false),
                "rijndael256-cbc" => EncryptRijndael(plain, key, iv, mask),
                _ => EncryptCtr(plain, key, iv.AsSpan(0, 16).ToArray(), mask, scenario == "ctr-little-endian" ? 1u : 0u, scenario == "ctr-little-endian"),
            };
        }

        internal static byte[] EncryptAes(byte[] plain, byte[] key, byte[] iv, byte[] mask, CipherMode mode, bool padding, bool ivPrefix)
        {
            int masked = Math.Min(mask.Length, plain.Length);
            byte[] padded = padding ? Pad(plain, 16) : (byte[])plain.Clone();
            for (int i = 0; i < masked; i++)
            {
                padded[i] ^= mask[i];
            }

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = mode;
            aes.Padding = PaddingMode.None;
            aes.IV = iv;
            using var encryptor = aes.CreateEncryptor();
            byte[] cipher = encryptor.TransformFinalBlock(padded, 0, padded.Length);
            return ivPrefix ? iv.Concat(cipher).ToArray() : cipher;
        }

        private static byte[] EncryptCtr(byte[] plain, byte[] key, byte[] iv, byte[] mask, uint counterStart, bool littleEndian)
        {
            var output = new byte[plain.Length];
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            Span<byte> counter = stackalloc byte[16];
            Span<byte> stream = stackalloc byte[16];
            for (int offset = 0; offset < plain.Length; offset += 16)
            {
                AddCounter(iv, (ulong)(offset / 16) + counterStart, littleEndian, counter);
                aes.EncryptEcb(counter, stream, PaddingMode.None);
                for (int i = 0; i < 16 && offset + i < plain.Length; i++)
                {
                    byte masked = (byte)(plain[offset + i] ^ (offset + i < mask.Length ? mask[offset + i] : 0));
                    output[offset + i] = (byte)(masked ^ stream[i]);
                }
            }

            return iv.Concat(output).ToArray();
        }

        private static byte[] EncryptRijndael(byte[] plain, byte[] key, byte[] iv, byte[] mask)
        {
            int pad = 32 - plain.Length % 32;
            var padded = new byte[plain.Length + pad];
            plain.CopyTo(padded, 0);
            Array.Fill(padded, (byte)pad, plain.Length, pad);
            for (int i = 0; i < Math.Min(mask.Length, plain.Length); i++)
            {
                padded[i] ^= mask[i];
            }

            var cipher = new RijndaelBlockCipher(key, 256);
            var output = new byte[padded.Length];
            byte[] previous = iv;
            var block = new byte[32];
            var encrypted = new byte[32];
            for (int offset = 0; offset < padded.Length; offset += 32)
            {
                for (int i = 0; i < 32; i++)
                {
                    block[i] = (byte)(padded[offset + i] ^ previous[i]);
                }

                cipher.EncryptBlock(block, encrypted);
                encrypted.CopyTo(output, offset);
                previous = encrypted;
            }

            return output;
        }

        private static void AddCounter(byte[] iv, ulong addition, bool littleEndian, Span<byte> target)
        {
            iv.CopyTo(target);
            ulong carry = addition;
            if (littleEndian)
            {
                for (int i = 0; i < 16 && carry != 0; i++)
                {
                    ulong sum = target[i] + (carry & 0xFFUL);
                    target[i] = (byte)sum;
                    carry = (carry >> 8) + (sum >> 8);
                }
            }
            else
            {
                for (int i = 15; i >= 0 && carry != 0; i--)
                {
                    ulong sum = target[i] + (carry & 0xFFUL);
                    target[i] = (byte)sum;
                    carry = (carry >> 8) + (sum >> 8);
                }
            }
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var result = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                result[i] = (byte)(a[i] ^ b[i]);
            }

            return result;
        }

        internal static byte[] Pad(byte[] data, int blockSize)
        {
            int pad = blockSize - data.Length % blockSize;
            var padded = new byte[data.Length + pad];
            data.CopyTo(padded, 0);
            Array.Fill(padded, (byte)pad, data.Length, pad);
            return padded;
        }

        internal sealed class ManagedImage
        {
            internal const int DumpBase = 1 << 17;
            private readonly byte[] data = new byte[1 << 18];
            private int cursor = 64;
            private int dumps;

            internal ManagedImage(Random random) => random.NextBytes(data);

            public ManagedImage AddArray(byte[] bytes)
            {
                cursor = (cursor + 7) & ~7;
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(cursor, 8), 0x00007FF123450000L);
                Array.Clear(data, cursor + 8, 16);
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(cursor + 24, 8), bytes.Length);
                bytes.CopyTo(data, cursor + 32);
                cursor += 48 + bytes.Length;
                return this;
            }

            public ManagedImage AddString(string text)
            {
                cursor = (cursor + 7) & ~7;
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(cursor, 8), 0x00007FF123450000L);
                Array.Clear(data, cursor + 8, 8);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(cursor + 16, 4), text.Length);
                for (int i = 0; i < text.Length; i++)
                {
                    data[cursor + 20 + i * 2] = (byte)text[i];
                    data[cursor + 21 + i * 2] = 0;
                }

                cursor += 36 + text.Length * 2;
                return this;
            }

            public ManagedImage AddDump(byte[] dump)
            {
                Assert.True(dumps < 2);
                dump.CopyTo(data, DumpBase + dumps++ * dump.Length);
                return this;
            }

            public byte[] ToArray() => data;
        }

        internal static string CreateGameLayout(string root)
        {
            string gameDirectory = Path.Combine(root, GameName);
            string bundleDirectory = Path.Combine(gameDirectory, GameName + "_Data", "StreamingAssets", "EncryptedBundles");
            Directory.CreateDirectory(bundleDirectory);
            File.WriteAllBytes(Path.Combine(gameDirectory, GameName + ".exe"), new byte[] { 1, 2, 3 });
            return bundleDirectory;
        }

        internal static byte[] BuildBundle(int startLength, int alignment, int seed)
        {
            for (int length = startLength; length < startLength + 64; length++)
            {
                byte[] bundle = BuildLz4Bundle("CAB-" + seed, length, seed);
                if (alignment == 1 || bundle.Length % alignment == 0)
                {
                    return bundle;
                }
            }

            throw new InvalidOperationException("No bundle with the requested alignment was found.");
        }

        internal static byte[] BuildLz4Bundle(string cabName, int payloadLength, int seed)
        {
            var payload = new byte[payloadLength];
            new Random(seed).NextBytes(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 22u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 128u);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24, 8), payload.Length);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32, 8), 48L);
            return BuildLz4Bundle(cabName, payload);
        }

        internal static byte[] BuildLz4Bundle(string cabName, byte[] payload)
        {
            var chunks = new List<byte[]>();
            for (int offset = 0; offset < payload.Length; offset += 1024)
            {
                int count = Math.Min(1024, payload.Length - offset);
                var chunk = new byte[count];
                Array.Copy(payload, offset, chunk, 0, count);
                var buffer = new byte[LZ4Codec.MaximumOutputSize(count)];
                chunks.Add(buffer.AsSpan(0, LZ4Codec.Encode(chunk, buffer)).ToArray());
            }

            byte[] blocksInfo;
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[16]);
                WriteUInt32BE(ms, (uint)chunks.Count);
                for (int offset = 0, i = 0; i < chunks.Count; offset += 1024, i++)
                {
                    WriteUInt32BE(ms, (uint)Math.Min(1024, payload.Length - offset));
                    WriteUInt32BE(ms, (uint)chunks[i].Length);
                    ms.Write(new byte[] { 0, (byte)CompressionType.Lz4 });
                }

                WriteUInt32BE(ms, 1u);
                WriteInt64BE(ms, 0);
                WriteInt64BE(ms, payload.Length);
                WriteUInt32BE(ms, 4u);
                ms.Write(Encoding.ASCII.GetBytes(cabName));
                ms.WriteByte(0);
                blocksInfo = ms.ToArray();
            }

            var infoBuffer = new byte[LZ4Codec.MaximumOutputSize(blocksInfo.Length)];
            byte[] compressedInfo = infoBuffer.AsSpan(0, LZ4Codec.Encode(blocksInfo, infoBuffer)).ToArray();
            byte[] header;
            using (var ms = new MemoryStream())
            {
                ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
                WriteUInt32BE(ms, 8u);
                ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
                ms.Write(Encoding.ASCII.GetBytes(Revision + "\0"));
                header = ms.ToArray();
            }

            int alignment = (16 - (header.Length + 20) % 16) % 16;
            long totalSize = header.Length + 20 + alignment + compressedInfo.Length + chunks.Sum(c => (long)c.Length);
            using var output = new MemoryStream();
            output.Write(header);
            WriteInt64BE(output, totalSize);
            WriteUInt32BE(output, (uint)compressedInfo.Length);
            WriteUInt32BE(output, (uint)blocksInfo.Length);
            WriteUInt32BE(output, (uint)CompressionType.Lz4);
            output.Write(new byte[alignment]);
            output.Write(compressedInfo);
            foreach (byte[] chunk in chunks)
            {
                output.Write(chunk);
            }

            return output.ToArray();
        }

        internal static void WriteUInt32BE(Stream stream, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes, 0, bytes.Length);
        }

        internal static void WriteInt64BE(Stream stream, long value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes, 0, bytes.Length);
        }

        internal static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "AssetStudioRuntimeCaptureTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static void TryDeleteDirectory(string path)
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
