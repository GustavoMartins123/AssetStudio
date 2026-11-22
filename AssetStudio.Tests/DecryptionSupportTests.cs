using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.ViewModels;
using K4os.Compression.LZ4;
using Xunit;

namespace AssetStudio.Tests
{
    [Collection("ResourceReaderXorHeuristic")]
    public class DecryptionSupportTests
    {
        [Fact]
        public void CipherTransformStream_AesCbc_Ecb_Ctr_And_ChaCha20_Roundtrip_RandomSeek_AndClone()
        {
            byte[] plain = CreatePatternPayload(1000);
            byte[] key256 = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
            byte[] key128 = key256.Take(16).ToArray();
            byte[] iv16 = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

            // 1. AES-256-CBC + PKCS7 + 12-byte custom prefix header
            byte[] cbcCipher = EncryptAes(plain, key256, iv16, CipherMode.CBC, PaddingMode.PKCS7);
            byte[] prefixedCbc = new byte[12 + cbcCipher.Length];
            Buffer.BlockCopy(cbcCipher, 0, prefixedCbc, 12, cbcCipher.Length);

            using (var cbcStream = new CipherTransformStream(
                new MemoryStream(prefixedCbc),
                StreamCipherAlgorithm.AesCbc,
                key256,
                iv16,
                headerSkipBytes: 12,
                pkcs7Padding: true))
            {
                Assert.Equal(plain.Length, cbcStream.Length);
                AssertRandomSeekMatches(cbcStream, plain);

                using var cloned = cbcStream.Clone(new MemoryStream(prefixedCbc));
                Assert.Equal(plain.Length, cloned.Length);
                Assert.Equal(plain, ReadAllBytes(cloned));
            }

            // 2. AES-128-ECB + PaddingMode.None over aligned payload
            byte[] alignedPlain = CreatePatternPayload(512);
            byte[] ecbCipher = EncryptAes(alignedPlain, key128, new byte[16], CipherMode.ECB, PaddingMode.None);
            using (var ecbStream = new CipherTransformStream(
                new MemoryStream(ecbCipher),
                StreamCipherAlgorithm.AesEcb,
                key128))
            {
                Assert.Equal(alignedPlain.Length, ecbStream.Length);
                AssertRandomSeekMatches(ecbStream, alignedPlain);
            }

            // 3. AES-128-CTR (Big-Endian & Little-Endian, non-zero counterStart, bounded prefix range)
            byte[] ctrPayload = CreatePatternPayload(600);
            byte[] ctrRangeCipher = EncryptAesCtr(ctrPayload, key128, iv16, counterStart: 5, littleEndian: false, rangeStart: 0, rangeLength: 192);
            using (var ctrStream = new CipherTransformStream(
                new MemoryStream(ctrRangeCipher),
                StreamCipherAlgorithm.AesCtr,
                key128,
                iv16,
                rangeStart: 0,
                rangeLength: 192,
                counterStart: 5,
                counterLittleEndian: false))
            {
                Assert.Equal(ctrPayload.Length, ctrStream.Length);
                AssertRandomSeekMatches(ctrStream, ctrPayload);
            }

            byte[] ctrLeCipher = EncryptAesCtr(ctrPayload, key128, iv16, counterStart: 3, littleEndian: true, rangeStart: 32, rangeLength: 256);
            using (var ctrLeStream = new CipherTransformStream(
                new MemoryStream(ctrLeCipher),
                StreamCipherAlgorithm.AesCtr,
                key128,
                iv16,
                rangeStart: 32,
                rangeLength: 256,
                counterStart: 3,
                counterLittleEndian: true))
            {
                Assert.Equal(ctrPayload, ReadAllBytes(ctrLeStream));
            }

            // 4. ChaCha20 RFC 7539 Section 2.3.2 official test vector
            byte[] rfcKey = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
            byte[] rfcNonce = Convert.FromHexString("000000090000004a00000000");
            byte[] expectedBlock = Convert.FromHexString(
                "10f1e7e4d13b5915500fdd1fa32071c4" +
                "c7d1f4c733c068030422aa9ac3d46c4e" +
                "d2826446079faa0914c2d705d98b02a2" +
                "b5129cd1de164eb9cbd083e8a2503c4e");
            byte[] actualBlock = new byte[64];
            using (var rfcStream = new CipherTransformStream(
                new MemoryStream(new byte[64]),
                StreamCipherAlgorithm.ChaCha20,
                rfcKey,
                rfcNonce,
                counterStart: 1))
            {
                Assert.Equal(64, rfcStream.Read(actualBlock, 0, 64));
            }
            Assert.Equal(expectedBlock, actualBlock);

            // ChaCha20 streaming roundtrip + seek + clone
            byte[] chachaCipher = ApplyChaCha20(plain, rfcKey, rfcNonce, counterStart: 1);
            using (var chachaStream = new CipherTransformStream(
                new MemoryStream(chachaCipher),
                StreamCipherAlgorithm.ChaCha20,
                rfcKey,
                rfcNonce,
                counterStart: 1))
            {
                AssertRandomSeekMatches(chachaStream, plain);
                using var cloned = chachaStream.Clone(new MemoryStream(chachaCipher));
                Assert.Equal(plain, ReadAllBytes(cloned));
            }
        }

        [Fact]
        public void DeclarativeProfile_EndToEnd_AesCbcPrefixIv_CtrKdf_ChaCha20_AndPlaintextSafety()
        {
            DecryptionHelper.Clear();
            try
            {
                byte[] nodePayload = BuildValidSerializedHeaderPayload(2048);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-cipher-test", nodePayload);

                // 1. AES-256-CBC with 16-byte stream-prefix IV, PKCS7 padding, validateUnityHeader=true
                byte[] key256 = Convert.FromHexString("102030405060708090A0B0C0D0E0F001112131415161718191A1B1C1D1E1F102");
                byte[] prefixIv = Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90");
                byte[] encryptedBody = EncryptAes(plainBundle, key256, prefixIv, CipherMode.CBC, PaddingMode.PKCS7);
                byte[] withPrefixIv = new byte[prefixIv.Length + encryptedBody.Length];
                Buffer.BlockCopy(prefixIv, 0, withPrefixIv, 0, prefixIv.Length);
                Buffer.BlockCopy(encryptedBody, 0, withPrefixIv, prefixIv.Length, encryptedBody.Length);

                DecryptionHelper.RegisterProfile(new DecryptionProfile
                {
                    Id = "aes-cbc-prefix",
                    Algorithm = "aes-cbc",
                    FileNamePattern = "*.bundle;*.enc",
                    KeyHex = Convert.ToHexString(key256),
                    IvFromStreamPrefix = true,
                    Padding = "pkcs7",
                    ValidateUnityHeader = true
                });

                // Encrypted bundle decrypts and opens cleanly through FileReader + BundleFile + Clone()
                using (var reader = new FileReader("scene_pack.enc", new MemoryStream(withPrefixIv)))
                {
                    Assert.Equal("DeclarativeProfile", reader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, reader.FileType);
                    Assert.False(reader.EncryptionBarrier.HasValue);

                    using var cloned = reader.Clone();
                    Assert.Equal(FileType.BundleFile, cloned.FileType);

                    using var bundle = new BundleFile(reader);
                    Assert.Single(bundle.fileList);
                    using var ms = new MemoryStream();
                    bundle.fileList[0].stream.CopyTo(ms);
                    Assert.Equal(nodePayload, ms.ToArray());
                }

                // Plaintext bundle matching "*.bundle" is NOT garbled when validateUnityHeader=true
                using (var plainReader = new FileReader("plain_scene.bundle", new MemoryStream(plainBundle)))
                {
                    Assert.Null(plainReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, plainReader.FileType);
                }

                // Wrong-key encrypted bundle does not match profile and reports barrier instead
                byte[] wrongKeyCipher = EncryptAes(plainBundle, Enumerable.Repeat((byte)0x55, 32).ToArray(), prefixIv, CipherMode.CBC, PaddingMode.PKCS7);
                byte[] wrongWithIv = new byte[prefixIv.Length + wrongKeyCipher.Length];
                Buffer.BlockCopy(prefixIv, 0, wrongWithIv, 0, prefixIv.Length);
                Buffer.BlockCopy(wrongKeyCipher, 0, wrongWithIv, prefixIv.Length, wrongKeyCipher.Length);
                using (var wrongReader = new FileReader("wrong_key.enc", new MemoryStream(wrongWithIv)))
                {
                    Assert.Null(wrongReader.AppliedDecryptionProvider);
                    Assert.True(wrongReader.EncryptionBarrier.HasValue);
                }

                // 2. PBKDF2-SHA256 + AES-128-CTR header-only (first 256 bytes)
                DecryptionHelper.Clear();
                byte[] derivedKey16 = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes("synthetic-passphrase"),
                    Encoding.UTF8.GetBytes("synthetic-salt"),
                    iterations: 2000,
                    HashAlgorithmName.SHA256,
                    outputLength: 16);
                byte[] ctrIv = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
                byte[] ctrBundle = EncryptAesCtr(plainBundle, derivedKey16, ctrIv, counterStart: 0, littleEndian: false, rangeStart: 0, rangeLength: 256);

                DecryptionHelper.RegisterProfile(new DecryptionProfile
                {
                    Id = "ctr-pbkdf2-header",
                    Algorithm = "aes-ctr",
                    FileNamePattern = "*.bundle",
                    KeyUtf8 = "synthetic-passphrase",
                    KeyDerivation = "pbkdf2-sha256",
                    SaltUtf8 = "synthetic-salt",
                    KdfIterations = 2000,
                    DerivedKeyLength = 16,
                    IvHex = Convert.ToHexString(ctrIv),
                    RangeStart = 0,
                    RangeLength = 256,
                    ValidateUnityHeader = true
                });

                using (var ctrReader = new FileReader("ctr_header.bundle", new MemoryStream(ctrBundle)))
                {
                    Assert.Equal("DeclarativeProfile", ctrReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, ctrReader.FileType);
                    using var bundle = new BundleFile(ctrReader);
                    Assert.Single(bundle.fileList);
                    using var ms = new MemoryStream();
                    bundle.fileList[0].stream.CopyTo(ms);
                    Assert.Equal(nodePayload, ms.ToArray());
                }

                // 3. AssetsManager.Clear() must preserve registered decryption profiles (uses ClearSessionCaches)
                using (var manager = new AssetsManager())
                {
                    manager.Clear();
                }
                Assert.Single(DecryptionHelper.GetRegisteredProfiles());
            }
            finally
            {
                DecryptionHelper.Clear();
            }
        }

        [Fact]
        public void ProjectScanner_ReportsRealPartialCoverage_Never100PercentWhileEncrypted_AndUnlocksAfterKeyAdded()
        {
            DecryptionHelper.Clear();
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_DecryptionScanTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                byte[] nodePayload = BuildValidSerializedHeaderPayload(1024);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-scan-test", nodePayload);
                byte[] key256 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");
                byte[] iv16 = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
                byte[] encryptedBundle = EncryptAes(plainBundle, key256, iv16, CipherMode.CBC, PaddingMode.PKCS7);

                // Create 2 readable bundles + 3 encrypted bundles => real coverage = 40.0%
                File.WriteAllBytes(Path.Combine(tempRoot, "plain_1.bundle"), plainBundle);
                File.WriteAllBytes(Path.Combine(tempRoot, "plain_2.bundle"), plainBundle);
                File.WriteAllBytes(Path.Combine(tempRoot, "locked_1.bundle"), encryptedBundle);
                File.WriteAllBytes(Path.Combine(tempRoot, "locked_2.bundle"), encryptedBundle);
                File.WriteAllBytes(Path.Combine(tempRoot, "locked_3.enc"), encryptedBundle);

                var cache = new SQLiteProjectIndexCache(Path.Combine(tempRoot, "cache"));
                try
                {
                    // Before key: 2 readable, 3 encrypted -> 40% coverage, never 100%
                    var beforeScan = ProjectScanner.ScanFolder(tempRoot);
                    Assert.Equal(2, beforeScan.UnityBundleCount);
                    Assert.Equal(3, beforeScan.EncryptedFileCount);
                    Assert.Equal(2, beforeScan.ReadableAssetFileCount);
                    Assert.Equal(5, beforeScan.TotalAssetFileCount);
                    Assert.True(beforeScan.HasEncryptedFiles);
                    Assert.Equal(40.0, beforeScan.CoveragePercent);
                    Assert.Equal(3, beforeScan.EncryptionBarriers.Count);
                    Assert.True(DecryptionHelper.InspectFileBarrier(Path.Combine(tempRoot, "locked_1.bundle"), out _));

                    var sigBefore = cache.GetFolderSignature(beforeScan);
                    Assert.EndsWith("_e3", sigBefore, StringComparison.Ordinal);

                    // Edge cases: 9999 readable + 1 encrypted must NEVER round up to 100%; 0 readable + 5 encrypted must be 0%
                    Assert.Equal(99.9, ProjectScanResult.ComputeCoveragePercent(9999, 1));
                    Assert.True(ProjectScanResult.ComputeCoveragePercent(9999, 1) < 100.0);
                    Assert.Equal(99.9, ProjectScanResult.ScaleProgressToCoverage(100.0, 9999, 1));
                    Assert.Equal(0.0, ProjectScanResult.ComputeCoveragePercent(0, 5));
                    Assert.Equal(0.0, ProjectScanResult.ScaleProgressToCoverage(100.0, 0, 5));

                    // Register key and re-scan -> all 5 bundles readable, 0 encrypted, 100% coverage!
                    DecryptionHelper.RegisterProfile(new DecryptionProfile
                    {
                        Id = "unlock-test",
                        Algorithm = "aes-cbc",
                        FileNamePattern = "*.bundle;*.enc",
                        KeyHex = Convert.ToHexString(key256),
                        IvHex = Convert.ToHexString(iv16),
                        Padding = "pkcs7",
                        ValidateUnityHeader = true
                    });

                    var afterScan = ProjectScanner.ScanFolder(tempRoot);
                    Assert.Equal(5, afterScan.UnityBundleCount);
                    Assert.Equal(0, afterScan.EncryptedFileCount);
                    Assert.False(afterScan.HasEncryptedFiles);
                    Assert.Equal(100.0, afterScan.CoveragePercent);
                    Assert.Empty(afterScan.EncryptionBarriers);
                    Assert.False(DecryptionHelper.InspectFileBarrier(Path.Combine(tempRoot, "locked_1.bundle"), out _));

                    var sigAfter = cache.GetFolderSignature(afterScan);
                    Assert.NotEqual(sigBefore, sigAfter);
                    Assert.DoesNotContain("_e", sigAfter, StringComparison.Ordinal);
                }
                finally
                {
                    cache.Dispose();
                }
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ViewModels_PartialCoverage_NeverDisplays100Percent_AndDialogSavesProfileCleanly()
        {
            DecryptionHelper.Clear();
            var tempDir = Path.Combine(Path.GetTempPath(), "AssetStudio_DecryptionVmTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. IndexingProgressViewModel never shows 100% while encrypted files remain
                var progressVm = new IndexingProgressViewModel();
                progressVm.SetEncryptionCoverage(readableAssetFiles: 4, encryptedFiles: 6);
                progressVm.ApplySnapshot(new IndexingProgressSnapshot(
                    status: IndexingStatus.Completed,
                    processedFiles: 6,
                    totalFiles: 6,
                    percentComplete: 100.0,
                    rawStatus: "structure_completed"));

                Assert.Equal(40.0, progressVm.ProgressPercent);
                Assert.Equal("40%", progressVm.FormattedPercentText);
                Assert.DoesNotContain("100%", progressVm.FormattedPercentText, StringComparison.Ordinal);
                Assert.Contains("partial", progressVm.PrimaryStatusText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("6 encrypted", progressVm.PrimaryStatusText, StringComparison.OrdinalIgnoreCase);

                progressVm.SetEncryptionCoverage(readableAssetFiles: 999, encryptedFiles: 1);
                progressVm.MarkCompleted(processedFiles: 999, totalFiles: 999);
                Assert.Equal(99.9, progressVm.ProgressPercent);
                Assert.Equal("99.9%", progressVm.FormattedPercentText);
                Assert.NotEqual("100%", progressVm.FormattedPercentText);

                // 2. ProjectSessionViewModel exposes partial coverage banner and inline key button
                var sessionVm = new ProjectSessionViewModel();
                sessionVm.UpdateEncryptionCoverage(readableAssetFiles: 4, encryptedFiles: 6);
                Assert.True(sessionVm.HasEncryptedFiles);
                Assert.True(sessionVm.ShowDecryptionKeyButton);
                Assert.True(sessionVm.HasRuntimeBanner);
                Assert.Equal(40.0, sessionVm.CoveragePercent);
                Assert.Contains("40%", sessionVm.RuntimeBannerText, StringComparison.Ordinal);
                Assert.Contains("6 encrypted", sessionVm.RuntimeBannerText, StringComparison.Ordinal);

                // Clearing encrypted count removes partial load warning
                sessionVm.UpdateEncryptionCoverage(readableAssetFiles: 10, encryptedFiles: 0);
                Assert.False(sessionVm.HasEncryptedFiles);
                Assert.False(sessionVm.ShowDecryptionKeyButton);
                Assert.Equal(100.0, sessionVm.CoveragePercent);

                // 3. DecryptionKeyDialogViewModel validation, save, continue-without-key, and apply-and-reanalyze
                var sampleBarrier = new StreamEncryptionBarrier(
                    "EncryptedBundleContainer",
                    Path.Combine(tempDir, "pack01.enc"),
                    "Encrypted container",
                    0.92,
                    "AA BB CC DD");
                var context = new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempDir,
                    ReadableAssetFileCount: 4,
                    EncryptedFileCount: 6,
                    CoveragePercent: 40.0,
                    DetectedBarriers: new[] { sampleBarrier });

                var dialogVm = new DecryptionKeyDialogViewModel(context, userProfilesDirectoryOverride: tempDir);
                Assert.Equal("Continue Without Key", dialogVm.SecondaryButtonText);
                Assert.Equal("*.enc", dialogVm.FileNamePattern);
                Assert.Contains("40%", dialogVm.EncryptedSummaryTitle, StringComparison.Ordinal);

                DecryptionKeyDialogResult? capturedResult = null;
                dialogVm.RequestClose += r => capturedResult = r;

                // Invalid AES key length -> shows ValidationError and does NOT close
                dialogVm.SelectedAlgorithmIndex = 0; // AES-CBC
                dialogVm.KeyInput = "AABBCC"; // only 3 bytes
                dialogVm.ApplyAndReanalyzeCommand.Execute(null);
                Assert.True(dialogVm.HasValidationError);
                Assert.Null(capturedResult);

                // Valid AES-128 key -> persists profile JSON and requests ApplyAndReanalyze
                dialogVm.ProfileId = "test-user-key";
                dialogVm.KeyInput = "00112233445566778899AABBCCDDEEFF";
                dialogVm.SelectedIvModeIndex = 2; // Read from Stream Prefix
                dialogVm.SelectedPaddingIndex = 1; // PKCS7
                dialogVm.ApplyAndReanalyzeCommand.Execute(null);

                Assert.False(dialogVm.HasValidationError);
                Assert.NotNull(capturedResult);
                Assert.Equal(DecryptionDialogAction.ApplyAndReanalyze, capturedResult!.Action);
                Assert.True(capturedResult.ProfilesModified);
                Assert.True(File.Exists(Path.Combine(tempDir, "test-user-key.json")));
                Assert.Contains(DecryptionHelper.GetRegisteredProfiles(), p => p.Id == "test-user-key");

                // Continue Without Key emits ContinueWithoutKey without requiring a key
                var skipVm = new DecryptionKeyDialogViewModel(context, userProfilesDirectoryOverride: tempDir);
                DecryptionKeyDialogResult? skipResult = null;
                skipVm.RequestClose += r => skipResult = r;
                skipVm.SecondaryCloseCommand.Execute(null);
                Assert.NotNull(skipResult);
                Assert.Equal(DecryptionDialogAction.ContinueWithoutKey, skipResult!.Action);
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ExampleProfilesJson_InRepoRoot_DeserializesCleanly_AndAllExamplesAreDisabledByDefault()
        {
            DecryptionHelper.Clear();
            try
            {
                string? dir = AppContext.BaseDirectory;
                string? examplePath = null;
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    string candidate = Path.Combine(dir, "decryption_profiles.example.json");
                    if (File.Exists(candidate))
                    {
                        examplePath = candidate;
                        break;
                    }
                    dir = Path.GetDirectoryName(dir);
                }

                Assert.NotNull(examplePath);
                int loaded = DecryptionHelper.LoadProfilesFromFile(examplePath!);
                Assert.True(loaded >= 4);
                var profiles = DecryptionHelper.GetRegisteredProfiles();
                Assert.All(profiles, p =>
                {
                    Assert.False(p.Enabled);
                    Assert.True(p.Validate(out var err), $"Example profile '{p.Id}' failed validation: {err}");
                });
            }
            finally
            {
                DecryptionHelper.Clear();
            }
        }

        private static byte[] CreatePatternPayload(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 37 + 19) & 0xFF);
            }
            return data;
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

            int[] offsets = { 0, 1, 15, 16, 17, 63, 64, 65, expected.Length / 2, Math.Max(0, expected.Length - 23) };
            foreach (int off in offsets)
            {
                if (off >= expected.Length) continue;
                int len = Math.Min(45, expected.Length - off);
                stream.Seek(off, SeekOrigin.Begin);
                byte[] buf = new byte[len];
                int read = stream.Read(buf, 0, len);
                Assert.Equal(len, read);
                Assert.Equal(expected.AsSpan(off, len).ToArray(), buf);
            }
        }

        private static byte[] EncryptAes(byte[] plain, byte[] key, byte[] iv, CipherMode mode, PaddingMode padding)
        {
            using var aes = Aes.Create();
            aes.KeySize = key.Length * 8;
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = mode;
            aes.Padding = padding;
            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plain, 0, plain.Length);
        }

        private static byte[] EncryptAesCtr(
            byte[] plain,
            byte[] key,
            byte[] iv,
            uint counterStart,
            bool littleEndian,
            long rangeStart,
            long rangeLength)
        {
            byte[] output = (byte[])plain.Clone();
            using var stream = new CipherTransformStream(
                new MemoryStream(output),
                StreamCipherAlgorithm.AesCtr,
                key,
                iv,
                rangeStart: rangeStart,
                rangeLength: rangeLength,
                counterStart: counterStart,
                counterLittleEndian: littleEndian);
            return ReadAllBytes(stream);
        }

        private static byte[] ApplyChaCha20(byte[] data, byte[] key32, byte[] nonce12, uint counterStart)
        {
            using var stream = new CipherTransformStream(
                new MemoryStream((byte[])data.Clone()),
                StreamCipherAlgorithm.ChaCha20,
                key32,
                nonce12,
                counterStart: counterStart);
            return ReadAllBytes(stream);
        }

        private static byte[] BuildValidSerializedHeaderPayload(int length)
        {
            byte[] payload = CreatePatternPayload(length);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 0u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 22u);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), 0u);
            payload[16] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 128u);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24, 8), payload.Length);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32, 8), 256L);
            return payload;
        }

        private static byte[] BuildValidLz4UnityFsBundle(string nodeName, byte[] nodeData)
        {
            byte[] compressedBlock = new byte[LZ4Codec.MaximumOutputSize(nodeData.Length)];
            int compBlockLen = LZ4Codec.Encode(nodeData, compressedBlock);
            Array.Resize(ref compressedBlock, compBlockLen);

            byte[] uncompBlocksInfo;
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[16]); // 16-byte hash
                WriteInt32BE(ms, 1); // 1 block
                WriteUInt32BE(ms, (uint)nodeData.Length);
                WriteUInt32BE(ms, (uint)compressedBlock.Length);
                WriteUInt16BE(ms, (ushort)CompressionType.Lz4);

                WriteInt32BE(ms, 1); // 1 directory node
                WriteInt64BE(ms, 0);
                WriteInt64BE(ms, nodeData.Length);
                WriteUInt32BE(ms, 4u); // SerializedFile flag
                ms.Write(Encoding.ASCII.GetBytes(nodeName));
                ms.WriteByte(0);
                uncompBlocksInfo = ms.ToArray();
            }

            byte[] compBlocksInfo = new byte[LZ4Codec.MaximumOutputSize(uncompBlocksInfo.Length)];
            int compInfoLen = LZ4Codec.Encode(uncompBlocksInfo, compBlocksInfo);
            Array.Resize(ref compBlocksInfo, compInfoLen);

            byte[] headerPrefix;
            using (var ms = new MemoryStream())
            {
                ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
                WriteUInt32BE(ms, 6u);
                ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
                ms.Write(Encoding.ASCII.GetBytes("2022.3.40f1\0"));
                headerPrefix = ms.ToArray();
            }

            long totalSize = headerPrefix.Length + 20 + compBlocksInfo.Length + compressedBlock.Length;
            using var finalMs = new MemoryStream();
            finalMs.Write(headerPrefix);
            WriteInt64BE(finalMs, totalSize);
            WriteUInt32BE(finalMs, (uint)compBlocksInfo.Length);
            WriteUInt32BE(finalMs, (uint)uncompBlocksInfo.Length);
            WriteUInt32BE(finalMs, (uint)CompressionType.Lz4);
            finalMs.Write(compBlocksInfo);
            finalMs.Write(compressedBlock);
            return finalMs.ToArray();
        }

        private static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteUInt32BE(Stream s, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteInt32BE(Stream s, int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteInt64BE(Stream s, long v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            s.Write(b);
        }
    }
}
