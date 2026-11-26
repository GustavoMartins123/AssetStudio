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
        public void GameMemoryCapture_FindsManagedKeyArray_ByPadding_AndPlainBundleHeader()
        {
            var random = new Random(11);
            byte[] key = new byte[32];
            random.NextBytes(key);
            byte[] plain = BuildLz4Bundle("CAB-scan", 2400, 5);
            byte[] first = EncryptWithKey(new byte[2000], key, random);
            byte[] second = EncryptWithKey(new byte[5000], key, random);
            string folder = CreateTempDirectory();
            try
            {
                string firstPath = Path.Combine(folder, "a.enc");
                string secondPath = Path.Combine(folder, "b.enc");
                File.WriteAllBytes(firstPath, first);
                File.WriteAllBytes(secondPath, second);
                List<byte[]> tails = GameMemoryCapture.LoadTails(new[] { firstPath, secondPath });
                Assert.Equal(2, tails.Count);
                Assert.Equal(2, GameMemoryCapture.CountTailMatches(key, tails));
                Assert.Equal(0, GameMemoryCapture.CountTailMatches(new byte[32], tails));
                Assert.True(GameMemoryCapture.IsKeyAccepted(2, 2));
                Assert.False(GameMemoryCapture.IsKeyAccepted(1, 2));
                Assert.True(GameMemoryCapture.IsKeyAccepted(1, 1));

                var decoy = new byte[32];
                random.NextBytes(decoy);
                byte[] image = BuildMemoryImage(new[] { key, new byte[16], new byte[24], decoy }, MakeDump(plain, plain.Length));
                var keys = new Dictionary<string, byte[]>();
                var dumps = new Dictionary<string, PlaintextBundleDump>();
                GameMemoryCapture.ScanBuffer(image, tails, 65536, keys, dumps);
                Assert.Single(keys);
                Assert.Equal(key, keys[Convert.ToHexString(key)]);
                PlaintextBundleDump dump = Assert.Single(dumps.Values);
                Assert.Equal(plain.Length, dump.DeclaredSize);
                Assert.Equal(Revision, dump.Revision);
                Assert.Equal(plain, dump.Data.AsSpan(0, plain.Length).ToArray());

                GameMemoryCapture.ScanBuffer(new byte[8192], tails, 65536, keys, dumps);
                Assert.Single(keys);
                Assert.Single(dumps);
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        [Fact]
        public void FixedIvCapture_RecoversKeyIvAndMask_ThenDecryptsThroughThePipeline()
        {
            var random = new Random(21);
            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            byte[] mask = new byte[256];
            random.NextBytes(key);
            random.NextBytes(iv);
            random.NextBytes(mask);

            byte[] smallPlain = BuildLz4Bundle("CAB-small", 1600, 6);
            byte[] bigPlain = BuildLz4Bundle("CAB-big", 20000, 7);
            string root = CreateTempDirectory();
            string bundleDir = CreateGameLayout(root);
            string? profileId = null;
            try
            {
                string smallPath = Path.Combine(bundleDir, "small_0000000000000000000000000000.bundle.enc");
                string bigPath = Path.Combine(bundleDir, "big_1111111111111111111111111111.bundle.enc");
                File.WriteAllBytes(smallPath, Encrypt(smallPlain, key, iv, mask, false));
                File.WriteAllBytes(bigPath, Encrypt(bigPlain, key, iv, mask, false));
                string[] files = { smallPath, bigPath };

                var dumps = new List<PlaintextBundleDump>
                {
                    Wrap(MakeDump(bigPlain, 8184)),
                    Wrap(MakeDump(smallPlain, smallPlain.Length)),
                };

                Assert.True(RuntimeCipherRecovery.TryRecover(key, files, dumps, out CapturedCipher? cipher));
                Assert.NotNull(cipher);
                Assert.False(cipher!.IvFromStreamPrefix);
                Assert.Equal(256, cipher.Mask.Length);
                Assert.Equal(new byte[16], cipher.Mask.Take(16).ToArray());
                Assert.Equal(mask.Skip(16).ToArray(), cipher.Mask.Skip(16).ToArray());
                Assert.Equal(Xor(iv, mask.Take(16).ToArray()), cipher.FixedIv);
                Assert.True(RuntimeCipherRecovery.IsMaskConfirmed(cipher));
                Assert.Equal(8184 - 256, cipher.VerifiedBytes);
                Assert.Equal(2, RuntimeCipherRecovery.ValidateOnFiles(cipher, files, out int checkedCount));
                Assert.Equal(2, checkedCount);

                Assert.False(RuntimeCipherRecovery.TryRecover(new byte[32], files, dumps, out _));

                GameCaptureTarget? target = GameCaptureTarget.TryResolve(smallPath);
                Assert.NotNull(target);
                Assert.Equal(Path.Combine(root, GameName, GameName + ".exe"), target!.ExecutablePath);
                Assert.Equal(bigPath, target.SampleFiles[0]);
                Assert.Equal("Sample Game", target.GameName);

                DecryptionProfile profile = GameKeyCapture.BuildProfile(target, cipher);
                Assert.True(profile.Validate(out string? error), error);
                Assert.Equal("*.enc", profile.FileNamePattern);
                Assert.Equal("*/Sample Game/Sample Game_Data/StreamingAssets/EncryptedBundles/*", profile.PathPattern);
                Assert.Equal("raw", profile.HeaderMask!.Type);
                Assert.Equal(256, profile.HeaderMask.Length);

                DecryptionHelper.RegisterProfile(profile);
                profileId = profile.Id;
                AssertDecrypted(smallPath, smallPlain);
                AssertDecrypted(bigPath, bigPlain);

                string otherDir = Path.Combine(root, "Other Game", "Other Game_Data");
                Directory.CreateDirectory(otherDir);
                File.WriteAllBytes(Path.Combine(root, "Other Game", "Other Game.exe"), new byte[] { 1 });
                string foreignPath = Path.Combine(otherDir, "big_1111111111111111111111111111.bundle.enc");
                File.WriteAllBytes(foreignPath, File.ReadAllBytes(bigPath));
                using (var stream = File.OpenRead(foreignPath))
                {
                    using Stream? decoded = DecryptionHelper.CheckAndDecryptStream(stream, foreignPath, out string? providerId, out _);
                    Assert.NotEqual("DeclarativeProfile", providerId);
                }
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
        public void IvPrefixCapture_RecoversLargeMask_AndFullyMaskedShortFileLoads()
        {
            var random = new Random(31);
            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            byte[] mask = new byte[4096];
            random.NextBytes(key);
            random.NextBytes(iv);
            random.NextBytes(mask);

            byte[] bigPlain = BuildLz4Bundle("CAB-wide", 40000, 8);
            byte[] smallPlain = BuildBundleWithPartialTailBlock(2800);
            string root = CreateTempDirectory();
            string bundleDir = CreateGameLayout(root);
            string? profileId = null;
            try
            {
                string bigPath = Path.Combine(bundleDir, "wide.enc");
                string smallPath = Path.Combine(bundleDir, "narrow.enc");
                File.WriteAllBytes(bigPath, Encrypt(bigPlain, key, iv, mask, true));
                File.WriteAllBytes(smallPath, Encrypt(smallPlain, key, iv, mask, true));
                string[] files = { smallPath, bigPath };
                PlaintextBundleDump bigDump = Wrap(MakeDump(bigPlain, 32760));
                PlaintextBundleDump smallDump = Wrap(MakeDump(smallPlain, smallPlain.Length));
                var dumps = new List<PlaintextBundleDump> { bigDump, smallDump };

                Assert.True(RuntimeCipherRecovery.TryRecover(key, files, dumps, out CapturedCipher? cipher));
                Assert.NotNull(cipher);
                Assert.True(cipher!.IvFromStreamPrefix);
                Assert.Equal(4096, cipher.Mask.Length);
                Assert.Equal(mask, cipher.Mask);
                Assert.Equal(32760 - 4096, cipher.VerifiedBytes);
                Assert.True(RuntimeCipherRecovery.IsMaskConfirmed(cipher));
                Assert.Equal(2, RuntimeCipherRecovery.ValidateOnFiles(cipher, files, out int checkedCount));
                Assert.Equal(2, checkedCount);

                Assert.False(RuntimeCipherRecovery.TryRecover(key, new[] { smallPath }, new[] { smallDump }, out _));

                DecryptionProfile profile = GameKeyCapture.BuildProfile(GameCaptureTarget.TryResolve(bigPath)!, cipher);
                Assert.True(profile.Validate(out string? error), error);
                Assert.True(profile.IvFromStreamPrefix);
                Assert.Equal("raw", profile.HeaderMask!.Type);
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
            }
            finally
            {
                TryDeleteDirectory(root);
            }
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

        private static PlaintextBundleDump Wrap(byte[] data)
        {
            PlaintextBundleDump? dump = PlaintextBundleDump.FromMemory(data);
            Assert.NotNull(dump);
            return dump!;
        }

        private static byte[] MakeDump(byte[] plain, int faithfulBytes)
        {
            var dump = new byte[65536];
            Array.Copy(plain, 0, dump, 0, Math.Min(plain.Length, faithfulBytes));
            return dump;
        }

        private static byte[] BuildMemoryImage(byte[][] arrays, byte[] dump)
        {
            var image = new byte[1 << 17];
            new Random(17).NextBytes(image);
            int cursor = 64;
            foreach (byte[] item in arrays)
            {
                BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(cursor, 8), 0x00007FF123450000L);
                Array.Clear(image, cursor + 8, 16);
                BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(cursor + 24, 8), item.Length);
                item.CopyTo(image, cursor + 32);
                cursor += 256;
            }

            dump.CopyTo(image, 32768);
            return image;
        }

        private static byte[] EncryptWithKey(byte[] plain, byte[] key, Random random)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            var iv = new byte[16];
            random.NextBytes(iv);
            aes.IV = iv;
            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plain, 0, plain.Length);
        }

        private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv, byte[] mask, bool ivPrefix)
        {
            int masked = Math.Min(mask.Length, plain.Length);
            byte[] padded = Pad(plain, 16);
            for (int i = 0; i < masked; i++)
            {
                padded[i] ^= mask[i];
            }

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor();
            byte[] cipher = encryptor.TransformFinalBlock(padded, 0, padded.Length);
            return ivPrefix ? iv.Concat(cipher).ToArray() : cipher;
        }

        private static byte[] Pad(byte[] data, int blockSize)
        {
            int pad = blockSize - data.Length % blockSize;
            var padded = new byte[data.Length + pad];
            data.CopyTo(padded, 0);
            Array.Fill(padded, (byte)pad, data.Length, pad);
            return padded;
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

        private static string CreateGameLayout(string root)
        {
            string gameDirectory = Path.Combine(root, GameName);
            string bundleDirectory = Path.Combine(gameDirectory, GameName + "_Data", "StreamingAssets", "EncryptedBundles");
            Directory.CreateDirectory(bundleDirectory);
            File.WriteAllBytes(Path.Combine(gameDirectory, GameName + ".exe"), new byte[] { 1, 2, 3 });
            return bundleDirectory;
        }

        private static byte[] BuildBundleWithPartialTailBlock(int startLength)
        {
            for (int length = startLength; length < startLength + 128; length++)
            {
                byte[] bundle = BuildLz4Bundle("CAB-narrow", length, 9);
                if (bundle.Length % 64 == 40)
                {
                    return bundle;
                }
            }

            throw new InvalidOperationException("No bundle with a partial 32-byte tail block was found.");
        }

        private static byte[] BuildLz4Bundle(string cabName, int payloadLength, int seed)
        {
            var payload = new byte[payloadLength];
            new Random(seed).NextBytes(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 22u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 128u);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24, 8), payload.Length);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32, 8), 48L);

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

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "AssetStudioRuntimeCaptureTests-" + Guid.NewGuid().ToString("N"));
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
