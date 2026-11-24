using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
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

                // 3. Per-file filename-derived key salt + filename-MD5 IV
                DecryptionHelper.Clear();
                string sampleFileName = "pack_0042.bundle";
                string sampleStem = "pack_0042";
                byte[] masterKeyBytes = Encoding.UTF8.GetBytes("master-secret");
                byte[] stemBytes = Encoding.UTF8.GetBytes(sampleStem);
                byte[] combinedForKey = new byte[masterKeyBytes.Length + stemBytes.Length];
                Buffer.BlockCopy(masterKeyBytes, 0, combinedForKey, 0, masterKeyBytes.Length);
                Buffer.BlockCopy(stemBytes, 0, combinedForKey, masterKeyBytes.Length, stemBytes.Length);
                byte[] perFileKey32 = SHA256.HashData(combinedForKey);
                byte[] perFileIv16 = MD5.HashData(stemBytes);
                byte[] perFileEncrypted = EncryptAes(plainBundle, perFileKey32, perFileIv16, CipherMode.CBC, PaddingMode.PKCS7);

                DecryptionHelper.RegisterProfile(new DecryptionProfile
                {
                    Id = "per-file-stem-kdf",
                    Algorithm = "aes-cbc",
                    FileNamePattern = "*.bundle",
                    KeyUtf8 = "master-secret",
                    KeyDerivation = "sha256",
                    UseFileNameAsSalt = true,
                    UseFileNameWithoutExtension = true,
                    IvFromFileNameHash = "md5",
                    Padding = "pkcs7",
                    ValidateUnityHeader = true
                });

                using (var perFileReader = new FileReader(sampleFileName, new MemoryStream(perFileEncrypted)))
                {
                    Assert.Equal("DeclarativeProfile", perFileReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, perFileReader.FileType);
                    using var bundle = new BundleFile(perFileReader);
                    Assert.Single(bundle.fileList);
                    using var ms = new MemoryStream();
                    bundle.fileList[0].stream.CopyTo(ms);
                    Assert.Equal(nodePayload, ms.ToArray());
                }

                // 4. AssetsManager.Clear() must preserve registered decryption profiles (uses ClearSessionCaches)
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

        [Fact]
        public void Block512_Detection_CatalogDiscovery_ScannerBarrier_AndProfileResolution()
        {
            DecryptionHelper.Clear();
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_Block512Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                byte[] nodePayload = BuildValidSerializedHeaderPayload(1024);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-block512-test", nodePayload);

                const string bundleFileName = "pack_block512.bundle";
                const string bundleName = "synthetic_pack";
                const string hashHex = "0123456789abcdef0123456789abcdef";
                const uint crcVal = 424242u;
                string token = $"{bundleName}_{hashHex}-{crcVal}";
                byte[] encryptedBlock512 = EncryptBlock512Bundle(plainBundle, token);

                // 1. Unresolved Custom ChaCha (512B Block XOR) (no catalog): ProjectScanner classifies as Encrypted with Block512MissingCatalog barrier,
                //    and DecryptionKeyDialogViewModel pre-selects Custom ChaCha (512B Block XOR) (index 6, SkipBytes=26)
                var orphanDir = Path.Combine(tempRoot, "orphan");
                Directory.CreateDirectory(orphanDir);
                var orphanBundlePath = Path.Combine(orphanDir, bundleFileName);
                File.WriteAllBytes(orphanBundlePath, encryptedBlock512);

                var orphanScan = ProjectScanner.ScanFolder(orphanDir);
                Assert.Equal(0, orphanScan.UnityBundleCount);
                Assert.Equal(1, orphanScan.EncryptedFileCount);
                Assert.Equal(0.0, orphanScan.CoveragePercent);
                Assert.Single(orphanScan.EncryptionBarriers);
                var blockBarrier = orphanScan.EncryptionBarriers[0];
                Assert.Equal("Block512MissingCatalog", blockBarrier.ProviderId);
                Assert.NotNull(blockBarrier.CandidateAlgorithms);
                Assert.Single(blockBarrier.CandidateAlgorithms!);
                Assert.Equal("block512", blockBarrier.CandidateAlgorithms![0].SchemeId);
                Assert.Equal("Custom ChaCha (512B Block XOR)", blockBarrier.CandidateAlgorithms![0].DisplayName);
                Assert.Contains("Custom chained ChaCha12/8/4 512B-block stream XOR ('Encryption' 26B header)", blockBarrier.CandidateAlgorithms![0].Evidence, StringComparison.Ordinal);
                Assert.Equal(26, blockBarrier.CandidateAlgorithms![0].SuggestedSkipBytes);

                Assert.True(DecryptionHelper.InspectFileBarrier(orphanBundlePath, out var inspectedBarrier));
                Assert.Equal("Block512MissingCatalog", inspectedBarrier.ProviderId);
                Assert.Contains("Custom chained ChaCha12/8/4 512B-block stream XOR ('Encryption' 26B header)", inspectedBarrier.Reason, StringComparison.Ordinal);
                Assert.Equal("Custom ChaCha (512B Block XOR)", inspectedBarrier.CandidateAlgorithms![0].DisplayName);

                var dialogContext = new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: orphanDir,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: orphanScan.EncryptionBarriers);

                var profilesDir = Path.Combine(tempRoot, "profiles");
                var dialogVm = new DecryptionKeyDialogViewModel(dialogContext, userProfilesDirectoryOverride: profilesDir);
                Assert.True(dialogVm.HasDetectedAlgorithms);
                Assert.Single(dialogVm.DetectedAlgorithms);
                Assert.Equal(6, dialogVm.SelectedAlgorithmIndex); // Custom ChaCha (512B Block XOR)
                Assert.Equal("Custom ChaCha (512B Block XOR)", dialogVm.AlgorithmOptions[6]);
                Assert.Equal("Custom ChaCha (512B Block XOR)", dialogVm.DetectedAlgorithms[0].DisplayName);
                Assert.Contains("Custom ChaCha (512B Block XOR)", dialogVm.DetectedAlgorithmsBannerText, StringComparison.Ordinal);
                Assert.True(dialogVm.IsBlock512Selected);
                Assert.False(dialogVm.IsKeySectionVisible);
                Assert.True(dialogVm.IsSelectedAlgorithmAutoDetected);
                Assert.Equal("26", dialogVm.SkipBytesText);
                Assert.Equal("*.bundle", dialogVm.FileNamePattern);
                Assert.True(dialogVm.DetectedAlgorithms[0].IsSelected);
                Assert.True(dialogVm.DetectedAlgorithms[0].IsChecked);

                // 2. Explicit non-adjacent catalog via Custom ChaCha (512B Block XOR) profile + TestKeyCommand verification
                var remoteCatalogDir = Path.Combine(tempRoot, "remote_catalog");
                Directory.CreateDirectory(remoteCatalogDir);
                var remoteCatalogPath = Path.Combine(remoteCatalogDir, "catalog_external.json");
                File.WriteAllText(remoteCatalogPath, BuildSyntheticJsonCatalog(bundleFileName, bundleName, hashHex, crcVal.ToString()), Encoding.UTF8);

                dialogVm.Block512CatalogPath = remoteCatalogPath;
                dialogVm.TestKeyCommand.Execute(null);
                Assert.False(dialogVm.HasValidationError);
                Assert.Contains("verified", dialogVm.StatusMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Custom ChaCha (512B Block XOR)", dialogVm.StatusMessage, StringComparison.Ordinal);

                dialogVm.ProfileId = "block512-remote-catalog";
                dialogVm.SaveProfileCommand.Execute(null);
                Assert.False(dialogVm.HasValidationError);

                using (var reader = new FileReader(orphanBundlePath))
                {
                    Assert.Equal("DeclarativeProfile", reader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, reader.FileType);
                    using var bundle = new BundleFile(reader);
                    Assert.Single(bundle.fileList);
                    using var ms = new MemoryStream();
                    bundle.fileList[0].stream.CopyTo(ms);
                    Assert.Equal(nodePayload, ms.ToArray());
                }

                // 3. Direct Block 512 token profile (no catalog file needed)
                DecryptionHelper.Clear();
                DecryptionHelper.RegisterProfile(new DecryptionProfile
                {
                    Id = "block512-direct-token",
                    Algorithm = "block512",
                    FileNamePattern = "*.bundle",
                    Block512Token = token,
                    ValidateUnityHeader = true
                });

                using (var tokenReader = new FileReader(orphanBundlePath))
                {
                    Assert.Equal("DeclarativeProfile", tokenReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, tokenReader.FileType);
                }

                // 4. Automatic catalog discovery for catalog_*.json, catalog-*.json, and catalog.bin
                DecryptionHelper.Clear();
                foreach (var catalogFileName in new[] { "catalog_2026.01.json", "catalog-patch.json" })
                {
                    DecryptionHelper.Clear();
                    var jsonDir = Path.Combine(tempRoot, "auto_" + Path.GetFileNameWithoutExtension(catalogFileName));
                    var embedDir = Path.Combine(jsonDir, "Embed");
                    Directory.CreateDirectory(embedDir);
                    File.WriteAllText(
                        Path.Combine(jsonDir, catalogFileName),
                        BuildSyntheticJsonCatalog(bundleFileName, bundleName, hashHex, crcVal.ToString()),
                        Encoding.UTF8);
                    var bundlePath = Path.Combine(embedDir, bundleFileName);
                    File.WriteAllBytes(bundlePath, encryptedBlock512);

                    using var autoReader = new FileReader(bundlePath);
                    Assert.Equal("Block512Catalog", autoReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, autoReader.FileType);
                    Assert.False(autoReader.EncryptionBarrier.HasValue);
                }

                // Binary catalog.bin auto-discovery
                DecryptionHelper.Clear();
                var binDir = Path.Combine(tempRoot, "auto_bin");
                var binEmbedDir = Path.Combine(binDir, "Embed");
                Directory.CreateDirectory(binEmbedDir);
                var binCatalogBytes = BuildSyntheticBinaryCatalog(
                    $"aa/Embed/{bundleFileName}",
                    bundleName,
                    Convert.FromHexString(hashHex),
                    crcVal);
                File.WriteAllBytes(Path.Combine(binDir, "catalog.bin"), binCatalogBytes);
                var binBundlePath = Path.Combine(binEmbedDir, bundleFileName);
                File.WriteAllBytes(binBundlePath, encryptedBlock512);

                using (var binReader = new FileReader(binBundlePath))
                {
                    Assert.Equal("Block512Catalog", binReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, binReader.FileType);
                    Assert.False(binReader.EncryptionBarrier.HasValue);
                }
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public void CustomChaCha512B_AlgorithmAliases_DisplayName_And_BackwardCompatibility()
        {
            DecryptionHelper.Clear();
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_ChaCha512BAliasTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                foreach (var alias in new[] { "block512", "block-512", "block_512", "chacha-512b-xor", "custom-chacha-512b", " ChaCha-512B-XOR " })
                {
                    var profile = new DecryptionProfile
                    {
                        Algorithm = alias,
                        Block512Token = "bundle_0123456789abcdef-123"
                    };
                    Assert.Equal(StreamCipherAlgorithm.Block512, profile.ResolveAlgorithm());
                    Assert.True(profile.Validate(out var err), $"Alias '{alias}' failed validation: {err}");
                }

                // Missing token/catalog reports updated technical name without legacy "Block 512" label
                var invalidProfile = new DecryptionProfile { Algorithm = "chacha-512b-xor" };
                Assert.False(invalidProfile.Validate(out var missingErr));
                Assert.NotNull(missingErr);
                Assert.Contains("Custom ChaCha (512B Block XOR)", missingErr!, StringComparison.Ordinal);
                Assert.DoesNotContain("Block 512", missingErr!, StringComparison.OrdinalIgnoreCase);

                // Explicit provider hint routing for both legacy and new aliases
                var explicitAliasCandidates = UnityHeaderProbe.AnalyzeExplicitProviderHint("chacha-512b-xor");
                Assert.Single(explicitAliasCandidates);
                Assert.Equal(StreamCipherAlgorithm.Block512, explicitAliasCandidates[0].CipherAlgorithm);
                Assert.Equal("Custom ChaCha (512B Block XOR)", explicitAliasCandidates[0].DisplayName);
                Assert.Contains("Custom chained ChaCha12/8/4 512B-block stream XOR ('Encryption' 26B header)", explicitAliasCandidates[0].Evidence, StringComparison.Ordinal);

                // UI AlgorithmOptions uses "Custom ChaCha (512B Block XOR)" and zero "Block 512" labels
                var emptyDialogVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: false,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 0,
                    CoveragePercent: 100.0,
                    DetectedBarriers: Array.Empty<StreamEncryptionBarrier>()));
                Assert.Equal("Custom ChaCha (512B Block XOR)", emptyDialogVm.AlgorithmOptions[6]);
                Assert.DoesNotContain(emptyDialogVm.AlgorithmOptions, o => o.Contains("Block 512", StringComparison.OrdinalIgnoreCase));

                // Roundtrip saved legacy JSON profile ("block512" + "block512Token") AND new alias ("chacha-512b-xor" + "block512Token")
                byte[] nodePayload = BuildValidSerializedHeaderPayload(1024);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-chacha512b-alias", nodePayload);
                const string token = "alias_pack_0123456789abcdef0123456789abcdef-777";
                byte[] encryptedBundle = EncryptBlock512Bundle(plainBundle, token);
                var bundlePath = Path.Combine(tempRoot, "alias_pack.bundle");
                File.WriteAllBytes(bundlePath, encryptedBundle);

                var legacyJsonPath = Path.Combine(tempRoot, "legacy_block512.json");
                File.WriteAllText(legacyJsonPath, $$"""
                {
                  "id": "legacy-block512-profile",
                  "enabled": true,
                  "algorithm": "block512",
                  "fileNamePattern": "*.bundle",
                  "block512Token": "{{token}}",
                  "validateUnityHeader": true
                }
                """, Encoding.UTF8);

                Assert.Equal(1, DecryptionHelper.LoadProfilesFromFile(legacyJsonPath));
                using (var legacyReader = new FileReader(bundlePath))
                {
                    Assert.Equal("DeclarativeProfile", legacyReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, legacyReader.FileType);
                }

                DecryptionHelper.Clear();
                var aliasJsonPath = Path.Combine(tempRoot, "chacha_512b_xor.json");
                File.WriteAllText(aliasJsonPath, $$"""
                {
                  "id": "chacha-512b-xor-profile",
                  "enabled": true,
                  "algorithm": "chacha-512b-xor",
                  "fileNamePattern": "*.bundle",
                  "block512Token": "{{token}}",
                  "validateUnityHeader": true
                }
                """, Encoding.UTF8);

                Assert.Equal(1, DecryptionHelper.LoadProfilesFromFile(aliasJsonPath));
                using (var aliasReader = new FileReader(bundlePath))
                {
                    Assert.Equal("DeclarativeProfile", aliasReader.AppliedDecryptionProvider);
                    Assert.Equal(FileType.BundleFile, aliasReader.FileType);
                    using var bundle = new BundleFile(aliasReader);
                    Assert.Single(bundle.fileList);
                    using var ms = new MemoryStream();
                    bundle.fileList[0].stream.CopyTo(ms);
                    Assert.Equal(nodePayload, ms.ToArray());
                }

                // Loading the saved chacha-512b-xor profile in ViewModel selects index 6 ("Custom ChaCha (512B Block XOR)")
                var editVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: false,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 0,
                    CoveragePercent: 100.0,
                    DetectedBarriers: Array.Empty<StreamEncryptionBarrier>()));
                editVm.SelectedProfile = editVm.Profiles.Single(p => p.Id == "chacha-512b-xor-profile");
                editVm.LoadSelectedProfileCommand.Execute(null);
                Assert.Equal(6, editVm.SelectedAlgorithmIndex);
                Assert.True(editVm.IsBlock512Selected);
                Assert.Equal(token, editVm.Block512Token);
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task AutoDetectedAlgorithms_SingleDeterministicMatches_EcbDuplicateBlocks_OffsetSkip_AndCatalogHints()
        {
            DecryptionHelper.Clear();
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_SingleDetectTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                // 1. AES-ECB duplicate 16-byte blocks -> AES-ECB ranked #1 and pre-selected
                byte[] structuredPlain = new byte[1024];
                byte[] repeatedBlock = Encoding.ASCII.GetBytes("REPEATED_16BYTE!");
                for (int off = 0; off < 1024; off += 16)
                {
                    Buffer.BlockCopy(repeatedBlock, 0, structuredPlain, off, 16);
                }
                // Vary first block so header isn't constant, keep blocks 1..63 repeating
                Encoding.ASCII.GetBytes("SYNTHETIC_HDR_01").CopyTo(structuredPlain, 0);
                byte[] ecbKey = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
                byte[] ecbCipher = EncryptAes(structuredPlain, ecbKey, new byte[16], CipherMode.ECB, PaddingMode.None);

                var ecbPath = Path.Combine(tempRoot, "ecb_pack.enc");
                File.WriteAllBytes(ecbPath, ecbCipher);
                Assert.True(DecryptionHelper.InspectFileBarrier(ecbPath, out var ecbBarrier));
                Assert.NotNull(ecbBarrier.CandidateAlgorithms);
                Assert.Equal("aes-ecb", ecbBarrier.CandidateAlgorithms![0].SchemeId);

                var ecbVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[] { ecbBarrier }));
                Assert.Equal(2, ecbVm.SelectedAlgorithmIndex); // AES-ECB
                Assert.True(ecbVm.IsSelectedAlgorithmAutoDetected);
                Assert.Equal("aes-ecb", ecbVm.DetectedAlgorithms[0].SchemeId);

                // 2. Offset skip structural detection -> Offset Skip (None) with SuggestedSkipBytes = 24
                byte[] nodePayload = BuildValidSerializedHeaderPayload(512);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-offset-test", nodePayload);
                byte[] shiftedBundle = new byte[24 + plainBundle.Length];
                for (int i = 0; i < 24; i++) shiftedBundle[i] = (byte)(0xC0 + i);
                Buffer.BlockCopy(plainBundle, 0, shiftedBundle, 24, plainBundle.Length);

                var offsetCandidates = UnityHeaderProbe.AnalyzeEncryptionCandidates(shiftedBundle, shiftedBundle.Length, "shifted.bundle");
                Assert.Single(offsetCandidates);
                Assert.Equal("none", offsetCandidates[0].SchemeId);
                Assert.Equal(24, offsetCandidates[0].SuggestedSkipBytes);

                var offsetVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: false,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[]
                    {
                        new StreamEncryptionBarrier("CustomOffset", "shifted.bundle", "Shifted bundle", 0.96, "C0 C1", offsetCandidates)
                    }));
                Assert.Equal(5, offsetVm.SelectedAlgorithmIndex); // Offset Skip (None)
                Assert.Equal("24", offsetVm.SkipBytesText);
                Assert.True(offsetVm.IsAdvancedOptionsExpanded);

                // 3. Explicit catalog hints preserved alongside file barriers (not discarded!)
                byte[] key256 = Convert.FromHexString("11223344556677889900AABBCCDDEEFF11223344556677889900AABBCCDDEEFF");
                byte[] iv16 = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
                byte[] alignedCipher = EncryptAes(plainBundle, key256, iv16, CipherMode.CBC, PaddingMode.PKCS7);
                var alignedBundlePath = Path.Combine(tempRoot, "catalog_hinted.bundle");
                File.WriteAllBytes(alignedBundlePath, alignedCipher);
                Assert.True(DecryptionHelper.InspectFileBarrier(alignedBundlePath, out var alignedBarrier));

                var chachaHint = new DecryptionHintEntry
                {
                    Provider = "Synthetic.ResourceProviders.ChaCha20BundleProvider",
                    CatalogPath = Path.Combine(tempRoot, "catalog.json"),
                    Bundle = string.Empty,
                    Reason = "Custom ChaCha20 provider detected in catalog.",
                    Confidence = 0.95
                };

                var combinedVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: false,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[] { alignedBarrier },
                    CatalogHints: new[] { chachaHint }));

                Assert.Equal(3, combinedVm.SelectedAlgorithmIndex); // ChaCha20 from catalog hint wins over generic structural CBC
                Assert.True(combinedVm.IsSelectedAlgorithmAutoDetected);
                Assert.Equal("*.bundle", combinedVm.FileNamePattern); // Inherited from file barrier!
                Assert.True(combinedVm.HasMultipleDetectedAlgorithms);
                Assert.Contains(combinedVm.DetectedAlgorithms, c => c.SchemeId == "aes-cbc");

                // 4. MainWindowViewModel fallback forwards ProjectSession.CurrentAnalysisResult.DecryptionHints
                var captureDialogs = new CapturingDecryptionDialogService();
                using var mainVm = new MainWindowViewModel(dialogService: captureDialogs);
                var analysis = new ProjectAnalysisResult
                {
                    Report = new ProjectAnalysisReport
                    {
                        ProjectRoot = tempRoot,
                        Status = ProjectAnalysisStatus.Ready,
                        DecryptionHintCount = 1
                    },
                    DecryptionHints = new DecryptionHintsReport
                    {
                        ProjectRoot = tempRoot,
                        Hints = new List<DecryptionHintEntry>
                        {
                            new()
                            {
                                Provider = "Synthetic.XorAssetBundleProvider",
                                CatalogPath = Path.Combine(tempRoot, "catalog.json"),
                                Bundle = "stage01.bundle",
                                Reason = "Custom XOR provider",
                                Confidence = 0.95
                            }
                        }
                    }
                };
                mainVm.ProjectSession.ApplyProjectAnalysis(analysis);
                await mainVm.OpenDecryptionSettingsCommand.ExecuteAsync();

                Assert.NotNull(captureDialogs.LastDecryptionContext);
                Assert.NotNull(captureDialogs.LastDecryptionContext!.CatalogHints);
                Assert.Single(captureDialogs.LastDecryptionContext.CatalogHints!);
                Assert.Single(captureDialogs.LastDecryptionContext.DetectedBarriers);

                var forwardedVm = new DecryptionKeyDialogViewModel(captureDialogs.LastDecryptionContext);
                Assert.Equal(4, forwardedVm.SelectedAlgorithmIndex); // XOR pre-selected!
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public void AutoDetectedAlgorithms_MultiCandidate_AlignedVsUnaligned_PartialHeader_ChipSwitching_AndTestKeyAutoSelect()
        {
            DecryptionHelper.Clear();
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_MultiDetectTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                byte[] nodePayload = BuildValidSerializedHeaderPayload(1024);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-multi-test", nodePayload);

                // Ensure unaligned payload length (% 16 != 0) for unaligned CTR test,
                // and aligned payload length (% 16 == 0) for 16B-aligned multi-candidate + TestKey auto-switch test.
                byte[] key128 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
                byte[] iv16 = Convert.FromHexString("102030405060708090A0B0C0D0E0F000");

                // 1. 16-byte aligned .bundle encrypted with AES-CTR -> detects 3 candidates (AES-CBC, AES-CTR, ChaCha20),
                //    all pre-checked, AES-CBC initially pre-selected; TestKey auto-switches to AES-CTR!
                byte[] alignedBundlePlain = plainBundle;
                if ((alignedBundlePlain.Length % 16) != 0)
                {
                    int pad = 16 - (alignedBundlePlain.Length % 16);
                    // Pad inside nodePayload so UnityFS declared size matches exact 16B-aligned stream length
                    nodePayload = BuildValidSerializedHeaderPayload(1024 + pad);
                    alignedBundlePlain = BuildValidLz4UnityFsBundle("CAB-multi-aligned", nodePayload);
                    while ((alignedBundlePlain.Length % 16) != 0)
                    {
                        nodePayload = BuildValidSerializedHeaderPayload(nodePayload.Length + 1);
                        alignedBundlePlain = BuildValidLz4UnityFsBundle("CAB-multi-aligned", nodePayload);
                    }
                }
                Assert.Equal(0, alignedBundlePlain.Length % 16);

                byte[] alignedCtrCipher = EncryptAesCtr(alignedBundlePlain, key128, iv16, counterStart: 0, littleEndian: false, rangeStart: 0, rangeLength: -1);
                var alignedPath = Path.Combine(tempRoot, "aligned_ctr.bundle");
                File.WriteAllBytes(alignedPath, alignedCtrCipher);

                Assert.True(DecryptionHelper.InspectFileBarrier(alignedPath, out var alignedBarrier));
                Assert.NotNull(alignedBarrier.CandidateAlgorithms);
                Assert.Equal(3, alignedBarrier.CandidateAlgorithms!.Count);
                Assert.Equal("aes-cbc", alignedBarrier.CandidateAlgorithms[0].SchemeId);
                Assert.Equal("aes-ctr", alignedBarrier.CandidateAlgorithms[1].SchemeId);
                Assert.Equal("chacha20", alignedBarrier.CandidateAlgorithms[2].SchemeId);

                var alignedVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[] { alignedBarrier }));

                Assert.True(alignedVm.HasMultipleDetectedAlgorithms);
                Assert.Equal(3, alignedVm.DetectedAlgorithms.Count);
                Assert.All(alignedVm.DetectedAlgorithms, c => Assert.True(c.IsChecked));
                Assert.Equal(0, alignedVm.SelectedAlgorithmIndex); // AES-CBC initially pre-selected
                Assert.True(alignedVm.DetectedAlgorithms[0].IsSelected);
                Assert.True(alignedVm.IsSelectedAlgorithmAutoDetected);

                // Uncheck AES-CTR -> TestKey should NOT switch to unchecked candidate
                var ctrCandidate = alignedVm.DetectedAlgorithms.Single(c => c.SchemeId == "aes-ctr");
                ctrCandidate.IsChecked = false;
                alignedVm.KeyInput = Convert.ToHexString(key128);
                alignedVm.IvInput = Convert.ToHexString(iv16);
                alignedVm.TestKeyCommand.Execute(null);
                Assert.True(alignedVm.HasValidationError);
                Assert.Equal(0, alignedVm.SelectedAlgorithmIndex);

                // Re-check AES-CTR -> TestKey automatically tries AES-CTR and auto-selects it!
                ctrCandidate.IsChecked = true;
                alignedVm.TestKeyCommand.Execute(null);
                Assert.False(alignedVm.HasValidationError);
                Assert.Equal(1, alignedVm.SelectedAlgorithmIndex); // Auto-switched to AES-CTR!
                Assert.True(ctrCandidate.IsSelected);
                Assert.Contains("AES-CTR", alignedVm.StatusMessage, StringComparison.OrdinalIgnoreCase);

                // 2. Unaligned .enc (length % 16 != 0) -> excludes AES-CBC and AES-ECB, pre-selects AES-CTR
                var unalignedNode = BuildValidSerializedHeaderPayload(1024);
                var unalignedPlain = BuildValidLz4UnityFsBundle("CAB-multi-unaligned", unalignedNode);
                while ((unalignedPlain.Length % 16) == 0)
                {
                    unalignedNode = BuildValidSerializedHeaderPayload(unalignedNode.Length + 1);
                    unalignedPlain = BuildValidLz4UnityFsBundle("CAB-multi-unaligned", unalignedNode);
                }
                Assert.NotEqual(0, unalignedPlain.Length % 16);

                byte[] unalignedCtr = EncryptAesCtr(unalignedPlain, key128, iv16, counterStart: 0, littleEndian: false, rangeStart: 0, rangeLength: -1);
                var unalignedPath = Path.Combine(tempRoot, "unaligned_stream.enc");
                File.WriteAllBytes(unalignedPath, unalignedCtr);

                Assert.True(DecryptionHelper.InspectFileBarrier(unalignedPath, out var unalignedBarrier));
                var unalignedVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[] { unalignedBarrier }));

                Assert.Equal(1, unalignedVm.SelectedAlgorithmIndex); // AES-CTR pre-selected!
                Assert.DoesNotContain(unalignedVm.DetectedAlgorithms, c => c.SchemeId == "aes-cbc");
                Assert.DoesNotContain(unalignedVm.DetectedAlgorithms, c => c.SchemeId == "aes-ecb");
                Assert.Contains(unalignedVm.DetectedAlgorithms, c => c.SchemeId == "aes-ctr");
                Assert.Contains(unalignedVm.DetectedAlgorithms, c => c.SchemeId == "chacha20");

                // 3. Heterogeneous folder: PartialHeaderEncryptedSerializedFile (globalgamemanagers, 0x400) + Block512 bundle
                //    Clicking candidate chips switches algorithm + structural range while preserving user key/IV!
                byte[] partialHeaderAsset = new byte[0x1200];
                for (int i = 0; i < 0x400; i++)
                {
                    partialHeaderAsset[i] = (byte)(((i * 73) + 0x4E) | 1);
                }
                partialHeaderAsset[0x400] = 0x0F;
                partialHeaderAsset[0x404] = 0x19;
                var partialPath = Path.Combine(tempRoot, "globalgamemanagers");
                File.WriteAllBytes(partialPath, partialHeaderAsset);
                Assert.True(DecryptionHelper.InspectFileBarrier(partialPath, out var partialBarrier));

                var block512Path = Path.Combine(tempRoot, "hero.bundle");
                File.WriteAllBytes(block512Path, EncryptBlock512Bundle(plainBundle, "hero_00112233-99"));
                Assert.True(DecryptionHelper.InspectFileBarrier(block512Path, out var heroBlockBarrier));

                var heteroVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 2,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[] { partialBarrier, heroBlockBarrier }));

                Assert.True(heteroVm.DetectedAlgorithms.Count >= 3);
                Assert.Equal(6, heteroVm.SelectedAlgorithmIndex); // Custom ChaCha (512B Block XOR) (98%) highest confidence
                Assert.Equal("Custom ChaCha (512B Block XOR)", heteroVm.DetectedAlgorithms[0].DisplayName);
                Assert.Equal("26", heteroVm.SkipBytesText);

                // Enter user key & IV, then click XOR candidate chip (from partial header barrier)
                heteroVm.KeyInput = "AABBCCDDEEFF00112233445566778899";
                heteroVm.IvInput = "0102030405060708090A0B0C0D0E0F10";
                var xorChip = heteroVm.DetectedAlgorithms.Single(c => c.SchemeId == "xor");
                xorChip.SelectCommand.Execute(null);

                Assert.Equal(4, heteroVm.SelectedAlgorithmIndex); // XOR
                Assert.True(xorChip.IsSelected);
                Assert.Equal("globalgamemanagers", heteroVm.FileNamePattern);
                Assert.Equal("0", heteroVm.SkipBytesText);
                Assert.Equal("0", heteroVm.RangeStartText);
                Assert.Equal("1024", heteroVm.RangeLengthText);
                Assert.True(heteroVm.IsAdvancedOptionsExpanded);
                // User key & IV must be preserved across chip switch!
                Assert.Equal("AABBCCDDEEFF00112233445566778899", heteroVm.KeyInput);
                Assert.Equal("0102030405060708090A0B0C0D0E0F10", heteroVm.IvInput);

                // 4. TestKey auto-detects Stream Prefix IV on AES-CBC when IV is left blank
                byte[] prefixIv = Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90");
                byte[] cbcBody = EncryptAes(plainBundle, key128, prefixIv, CipherMode.CBC, PaddingMode.PKCS7);
                byte[] withPrefixIv = new byte[prefixIv.Length + cbcBody.Length];
                Buffer.BlockCopy(prefixIv, 0, withPrefixIv, 0, prefixIv.Length);
                Buffer.BlockCopy(cbcBody, 0, withPrefixIv, prefixIv.Length, cbcBody.Length);
                var prefixPath = Path.Combine(tempRoot, "prefix_iv.bundle");
                File.WriteAllBytes(prefixPath, withPrefixIv);

                Assert.True(DecryptionHelper.InspectFileBarrier(prefixPath, out var prefixBarrier));
                var prefixVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: 1,
                    CoveragePercent: 0.0,
                    DetectedBarriers: new[] { prefixBarrier }));

                Assert.Equal(0, prefixVm.SelectedAlgorithmIndex); // AES-CBC pre-selected
                prefixVm.KeyInput = Convert.ToHexString(key128);
                prefixVm.IvInput = string.Empty;
                prefixVm.SelectedIvModeIndex = 0;
                prefixVm.TestKeyCommand.Execute(null);

                Assert.False(prefixVm.HasValidationError);
                Assert.Equal(2, prefixVm.SelectedIvModeIndex); // Auto-switched to Read from Stream Prefix!
                Assert.Contains("verified", prefixVm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task NonStreamDiagnosticHints_DoNotFabricateCipherCandidates_And_TestKeySamplesAllBarrierGroups()
        {
            DecryptionHelper.Clear();
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_EdgeCasesTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                // 1. Il2CppGlobalMetadata & CustomCatalogSchema hints must NOT fabricate AES-CBC or *.dat / *.json
                Assert.Empty(UnityHeaderProbe.AnalyzeEncryptionCandidates(
                    ReadOnlySpan<byte>.Empty,
                    0,
                    "global-metadata.dat",
                    providerHint: "Il2CppGlobalMetadata",
                    reasonHint: "IL2CPP global-metadata header does not match standard magic."));

                Assert.Empty(UnityHeaderProbe.AnalyzeEncryptionCandidates(
                    ReadOnlySpan<byte>.Empty,
                    0,
                    "catalog.json",
                    providerHint: "CustomCatalogSchema",
                    reasonHint: "Catalog JSON uses non-standard field names."));

                var diagHints = new[]
                {
                    new DecryptionHintEntry
                    {
                        Provider = "Il2CppGlobalMetadata",
                        CatalogPath = string.Empty,
                        Bundle = "global-metadata.dat",
                        Reason = "IL2CPP metadata obfuscated",
                        Confidence = 0.90
                    },
                    new DecryptionHintEntry
                    {
                        Provider = "CustomCatalogSchema",
                        CatalogPath = Path.Combine(tempRoot, "catalog.json"),
                        Bundle = string.Empty,
                        Reason = "Custom catalog JSON schema",
                        Confidence = 0.65
                    }
                };

                var diagVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: false,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 10,
                    EncryptedFileCount: 0,
                    CoveragePercent: 100.0,
                    DetectedBarriers: Array.Empty<StreamEncryptionBarrier>(),
                    CatalogHints: diagHints));

                Assert.False(diagVm.HasDetectedAlgorithms);
                Assert.Empty(diagVm.DetectedAlgorithms);
                Assert.False(diagVm.IsSelectedAlgorithmAutoDetected);
                Assert.Equal("*.bundle;*.enc", diagVm.FileNamePattern);
                Assert.DoesNotContain(".dat", diagVm.FileNamePattern, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".json", diagVm.FileNamePattern, StringComparison.OrdinalIgnoreCase);

                // MainWindowViewModel fallback also skips converting diagnostic hints into cipher barriers
                var captureDialogs = new CapturingDecryptionDialogService();
                using var mainVm = new MainWindowViewModel(dialogService: captureDialogs);
                mainVm.ProjectSession.ApplyProjectAnalysis(new ProjectAnalysisResult
                {
                    Report = new ProjectAnalysisReport
                    {
                        ProjectRoot = tempRoot,
                        Status = ProjectAnalysisStatus.Ready,
                        DecryptionHintCount = diagHints.Length
                    },
                    DecryptionHints = new DecryptionHintsReport
                    {
                        ProjectRoot = tempRoot,
                        Hints = diagHints.ToList()
                    }
                });
                await mainVm.OpenDecryptionSettingsCommand.ExecuteAsync();

                Assert.NotNull(captureDialogs.LastDecryptionContext);
                Assert.Empty(captureDialogs.LastDecryptionContext!.DetectedBarriers);
                var fromMainVm = new DecryptionKeyDialogViewModel(captureDialogs.LastDecryptionContext);
                Assert.False(fromMainVm.HasDetectedAlgorithms);
                Assert.False(fromMainVm.IsSelectedAlgorithmAutoDetected);
                Assert.Equal("*.bundle;*.enc", fromMainVm.FileNamePattern);

                // 2. Heterogeneous folder with 5 .assets barriers alphabetically BEFORE a 16B-aligned AES-CBC+PKCS7 .bundle barrier
                //    AND an unaligned AES-CTR .enc barrier:
                //    TestKeyCommand must NOT starve on the first 4 .assets files; it must sample across barrier groups,
                //    honor each barrier's structural range/padding, unlock the .bundle/.enc file, and auto-select the winner!
                var rng = new Random(90210);
                byte[] partialAssetsPayload = new byte[0x1200];
                rng.NextBytes(partialAssetsPayload.AsSpan(0, 0x400));
                for (int i = 0; i < 0x400; i++)
                {
                    if (partialAssetsPayload[i] == 0)
                    {
                        partialAssetsPayload[i] = 1;
                    }
                }
                partialAssetsPayload[0x400] = 0x0F;
                partialAssetsPayload[0x404] = 0x19;

                var assetBarriers = new List<StreamEncryptionBarrier>();
                var assetNames = new[] { "a_core0.assets", "b_core1.assets", "c_core2.assets", "d_core3.assets", "e_core4.assets" };
                foreach (var name in assetNames)
                {
                    var p = Path.Combine(tempRoot, name);
                    File.WriteAllBytes(p, partialAssetsPayload);
                    Assert.True(DecryptionHelper.InspectFileBarrier(p, out var assetBarrier));
                    assetBarriers.Add(assetBarrier);
                }

                byte[] nodePayload = BuildValidSerializedHeaderPayload(2048);
                byte[] plainBundle = BuildValidLz4UnityFsBundle("CAB-hetero-bundle", nodePayload);
                byte[] key128 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
                byte[] iv16 = Convert.FromHexString("102030405060708090A0B0C0D0E0F000");

                // 2a. 5 .assets barriers FIRST + 6th barrier is full-stream AES-CBC+PKCS7 .bundle
                byte[] cbcBundleCipher = EncryptAes(plainBundle, key128, iv16, CipherMode.CBC, PaddingMode.PKCS7);
                var cbcBundlePath = Path.Combine(tempRoot, "z_pack.bundle");
                File.WriteAllBytes(cbcBundlePath, cbcBundleCipher);
                Assert.True(DecryptionHelper.InspectFileBarrier(cbcBundlePath, out var cbcBundleBarrier));

                var cbcStarvedBarriers = assetBarriers.Append(cbcBundleBarrier).ToList();
                var cbcStarvedVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: cbcStarvedBarriers.Count,
                    CoveragePercent: 0.0,
                    DetectedBarriers: cbcStarvedBarriers));

                // XOR (0.84, *.assets, range 1024) is pre-selected; user enters AES-CBC key+IV for z_pack.bundle and clicks Test Key!
                Assert.Equal(4, cbcStarvedVm.SelectedAlgorithmIndex); // XOR
                cbcStarvedVm.KeyInput = Convert.ToHexString(key128);
                cbcStarvedVm.IvInput = Convert.ToHexString(iv16);
                cbcStarvedVm.TestKeyCommand.Execute(null);

                Assert.False(cbcStarvedVm.HasValidationError);
                Assert.Equal(0, cbcStarvedVm.SelectedAlgorithmIndex); // Auto-switched to AES-CBC!
                Assert.Equal("*.bundle", cbcStarvedVm.FileNamePattern);
                Assert.Equal("-1", cbcStarvedVm.RangeLengthText);
                Assert.Equal(1, cbcStarvedVm.SelectedPaddingIndex); // PKCS7
                Assert.Contains("z_pack.bundle", cbcStarvedVm.StatusMessage, StringComparison.OrdinalIgnoreCase);

                // 2b. 5 .assets barriers FIRST + 6th barrier is unaligned full-stream AES-CTR .enc
                var unalignedNode = BuildValidSerializedHeaderPayload(2048);
                var unalignedPlain = BuildValidLz4UnityFsBundle("CAB-hetero-unaligned", unalignedNode);
                while ((unalignedPlain.Length % 16) == 0)
                {
                    unalignedNode = BuildValidSerializedHeaderPayload(unalignedNode.Length + 1);
                    unalignedPlain = BuildValidLz4UnityFsBundle("CAB-hetero-unaligned", unalignedNode);
                }
                byte[] unalignedCtrCipher = EncryptAesCtr(unalignedPlain, key128, iv16, counterStart: 0, littleEndian: false, rangeStart: 0, rangeLength: -1);
                var encPath = Path.Combine(tempRoot, "z_stream.enc");
                File.WriteAllBytes(encPath, unalignedCtrCipher);
                Assert.True(DecryptionHelper.InspectFileBarrier(encPath, out var encBarrier));

                var ctrStarvedBarriers = assetBarriers.Append(encBarrier).ToList();
                var ctrStarvedVm = new DecryptionKeyDialogViewModel(new DecryptionDialogContext(
                    IsInitialLoadPrompt: true,
                    ProjectRoot: tempRoot,
                    ReadableAssetFileCount: 0,
                    EncryptedFileCount: ctrStarvedBarriers.Count,
                    CoveragePercent: 0.0,
                    DetectedBarriers: ctrStarvedBarriers));

                Assert.Equal(4, ctrStarvedVm.SelectedAlgorithmIndex); // XOR
                ctrStarvedVm.KeyInput = Convert.ToHexString(key128);
                ctrStarvedVm.IvInput = Convert.ToHexString(iv16);
                ctrStarvedVm.TestKeyCommand.Execute(null);

                Assert.False(ctrStarvedVm.HasValidationError);
                Assert.Equal(1, ctrStarvedVm.SelectedAlgorithmIndex); // Auto-switched to AES-CTR!
                Assert.Equal("*.enc", ctrStarvedVm.FileNamePattern);
                Assert.Equal("-1", ctrStarvedVm.RangeLengthText);
                Assert.Contains("z_stream.enc", ctrStarvedVm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DecryptionHelper.Clear();
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        private sealed class CapturingDecryptionDialogService : IDialogService
        {
            public DecryptionDialogContext? LastDecryptionContext { get; private set; }

            public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerRequest request) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<string?> PickOpenFolderAsync(FolderPickerRequest request) => Task.FromResult<string?>(null);
            public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request) => Task.FromResult<string?>(null);
            public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
            public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
            public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
            public Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message) => Task.FromResult(RiskyLoadChoice.Cancel);
            public Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings) => Task.FromResult(false);
            public Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath) => Task.FromResult<ProjectManagerDialogResult?>(null);
            public Task<bool> CopyTextToClipboardAsync(string text) => Task.FromResult(true);
            public Task<bool> OpenPathInExplorerAsync(string path) => Task.FromResult(true);
            public Task<bool> OpenUrlAsync(string url) => Task.FromResult(true);

            public Task<DecryptionKeyDialogResult?> ShowDecryptionKeyDialogAsync(DecryptionDialogContext context)
            {
                LastDecryptionContext = context;
                return Task.FromResult<DecryptionKeyDialogResult?>(new DecryptionKeyDialogResult(DecryptionDialogAction.Close, false));
            }
        }

        private static byte[] EncryptBlock512Bundle(byte[] plainBundle, string token)
        {
            var output = new byte[26 + plainBundle.Length];
            Encoding.ASCII.GetBytes("Encryption").CopyTo(output, 0);
            Buffer.BlockCopy(plainBundle, 0, output, 26, plainBundle.Length);

            var keyGen = new Block512KeyGenerator(token);
            var keyBlock = new byte[512];
            int offset = 0;
            uint blockIndex = 0;
            while (offset < plainBundle.Length)
            {
                keyGen.FillKey(blockIndex++, keyBlock);
                int count = Math.Min(512, plainBundle.Length - offset);
                UnityHeaderProbe.XorBytes(output, 26 + offset, keyBlock, 0, count);
                offset += count;
            }

            return output;
        }

        private static string BuildSyntheticJsonCatalog(string bundleFileName, string bundleName, string hash, string crc)
        {
            var entryBytes = new byte[4 + 28];
            BinaryPrimitives.WriteInt32LittleEndian(entryBytes.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(entryBytes.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(entryBytes.AsSpan(4 + 16, 4), 0);

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

        private static byte[] BuildSyntheticBinaryCatalog(string internalId, string bundleName, byte[] hash16, uint crc)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            const uint keysOffset = 36u;
            const int keysByteSize = 8;
            bw.Write(new byte[32]);
            bw.Write(keysByteSize);
            bw.Write(new byte[keysByteSize]);

            uint WriteAsciiString(string s)
            {
                var bytes = Encoding.ASCII.GetBytes(s);
                bw.Write(bytes.Length);
                uint offset = (uint)ms.Position;
                bw.Write(bytes);
                return offset;
            }

            uint WriteDynamicPathString(string path, char sep)
            {
                var segments = path.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                uint nextId = uint.MaxValue;
                for (int i = 0; i < segments.Length; i++)
                {
                    uint partStrId = WriteAsciiString(segments[i]);
                    uint nodeOffset = (uint)ms.Position;
                    bw.Write(partStrId);
                    bw.Write(nextId);
                    nextId = nodeOffset | 0x40000000u;
                }
                return nextId;
            }

            uint WriteUInt32Array(uint[] values)
            {
                bw.Write(values.Length * 4);
                uint offset = (uint)ms.Position;
                foreach (var v in values)
                {
                    bw.Write(v);
                }
                return offset;
            }

            uint idOffset = WriteAsciiString("synthetic_locator");
            uint providerOffset = WriteDynamicPathString("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider", '.');
            uint locationOffset = (uint)ms.Position;
            bw.Write(new byte[28]);

            uint primaryKeyOff = WriteAsciiString(bundleName);
            uint internalIdOff = WriteDynamicPathString(internalId.Replace('\\', '/'), '/');

            uint hashOff = (uint)ms.Position;
            bw.Write(hash16);
            uint bundleNameOff = WriteAsciiString(bundleName);
            uint serializedDataOff = (uint)ms.Position;
            bw.Write(hashOff);
            bw.Write(bundleNameOff);
            bw.Write(crc);
            bw.Write(4096u);
            bw.Write(uint.MaxValue);

            uint extraDataOff = (uint)ms.Position;
            bw.Write(uint.MaxValue);
            bw.Write(serializedDataOff);

            long savedPos = ms.Position;
            ms.Position = locationOffset;
            bw.Write(primaryKeyOff);
            bw.Write(internalIdOff);
            bw.Write(providerOffset);
            bw.Write(uint.MaxValue);
            bw.Write(0);
            bw.Write(extraDataOff);
            bw.Write(uint.MaxValue);
            ms.Position = savedPos;

            uint locSetOff = WriteUInt32Array(new[] { locationOffset });
            savedPos = ms.Position;
            ms.Position = keysOffset;
            bw.Write(primaryKeyOff);
            bw.Write(locSetOff);
            ms.Position = savedPos;

            ms.Position = 0;
            bw.Write(AddressablesCatalogDependencyResolver.BinaryCatalogMagic);
            bw.Write(2);
            bw.Write(keysOffset);
            bw.Write(idOffset);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Flush();
            return ms.ToArray();
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
