using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class Il2CppMetadataAuditTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        [Theory]
        [InlineData(24)]
        [InlineData(29)]
        [InlineData(31)]
        public void Audit_StandardClassicMetadata_ClassifiesClassicAndCleanSymbols(int version)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                var raw = BuildSyntheticClassicMetadata(version, CleanGameTypeSpecs());
                File.WriteAllBytes(metadataPath, raw);

                var service = new Il2CppMetadataAuditService();
                var report = service.Audit(metadataPath);

                Assert.Equal(Il2CppMetadataProtectionKind.StandardClassic, report.ProtectionKind);
                Assert.Equal(Il2CppSymbolObfuscationKind.Clean, report.SymbolObfuscationKind);
                Assert.Equal("0xFAB11BAF", report.RawMagicHex);
                Assert.Equal(version, report.RawVersion);
                Assert.Equal(version, report.DetectedMetadataVersion);
                Assert.Equal(0, report.RecoveredMetadataVersion);
                Assert.Empty(report.RecoveredXorKeyHex);
                Assert.True(report.HeaderEntropy < 6.5, $"Expected low classic header entropy, got {report.HeaderEntropy:F2}");
                Assert.True(report.HeaderZeroByteRatio > 0.25, $"Expected significant zero-byte ratio in header, got {report.HeaderZeroByteRatio:P1}");
                Assert.Equal(3, report.AssemblyCount);
                Assert.Equal(1, report.GameAssemblyCount);
                Assert.Equal(CleanGameTypeSpecs().Length, report.GameTypeCount);
                Assert.Equal(0, report.ObfuscatedGameTypeCount);
                Assert.Equal(0.0, report.ObfuscatedTypeRatio, 4);
                Assert.Empty(report.SampleObfuscatedNames);
                Assert.NotEmpty(report.RecommendedNextSteps);

                var formatted = report.FormatSummaryReport();
                Assert.Contains("StandardClassic", formatted, StringComparison.Ordinal);
                Assert.Contains($"v{version}", formatted, StringComparison.Ordinal);
                Assert.Contains("Clean", formatted, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Theory]
        [InlineData(39)]
        [InlineData(107)]
        public void Audit_CompactUnity6Metadata_ClassifiesCompactUnity6AndReadableCounts(int version)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var classic = BuildSyntheticClassicMetadata(31, CleanGameTypeSpecs());
                var compact = RewriteAsCompactMetadata(classic, version, typeIndexWidth: 2);
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, compact);

                var service = new Il2CppMetadataAuditService();
                var report = service.Audit(metadataPath);

                Assert.Equal(Il2CppMetadataProtectionKind.CompactUnity6, report.ProtectionKind);
                Assert.Equal(Il2CppSymbolObfuscationKind.Clean, report.SymbolObfuscationKind);
                Assert.Equal("0xFAB11BAF", report.RawMagicHex);
                Assert.Equal(version, report.DetectedMetadataVersion);
                Assert.Equal(3, report.AssemblyCount);
                Assert.Equal(1, report.GameAssemblyCount);
                Assert.Equal(CleanGameTypeSpecs().Length, report.GameTypeCount);
                Assert.Contains(report.RecommendedNextSteps, s => s.Contains($"v{version}", StringComparison.Ordinal));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Theory]
        [InlineData(29, new byte[] { 0x5A })]
        [InlineData(29, new byte[] { 0x5A, 0x3C })]
        [InlineData(29, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })]
        [InlineData(29, new byte[] { 0x13, 0x37, 0x42, 0x99, 0xA5, 0x5A, 0xC3, 0x7E })]
        [InlineData(29, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x9A, 0xBC, 0xDE, 0xF0, 0x21, 0x43, 0x65, 0x87 })]
        [InlineData(31, new byte[] { 0x0F, 0x1E, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78, 0x87, 0x96, 0xA5, 0xB4, 0xC3, 0xD2, 0xE1, 0xF0, 0x19, 0x28, 0x37, 0x46, 0x55, 0x64, 0x73, 0x82, 0x91, 0xA0, 0xBF, 0xCE, 0xDD, 0xEC, 0xFB, 0x0A })]
        public void Audit_WholeFileRepeatingXorClassicMetadata_RecoversExactKeyAndDecodesTypes(int version, byte[] xorKey)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var raw = BuildSyntheticClassicMetadata(version, CleanGameTypeSpecs());
                var masked = ApplyRepeatingXor(raw, xorKey);
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, masked);

                var reader = new Il2CppMetadataReader();
                // Without XOR key, direct inspection must fail cleanly.
                Assert.False(reader.InspectMetadata(metadataPath).Success);

                var service = new Il2CppMetadataAuditService(reader);
                var report = service.Audit(metadataPath);

                Assert.Equal(Il2CppMetadataProtectionKind.SimpleXorRecoverable, report.ProtectionKind);
                Assert.Equal(Il2CppMetadataAuditService.FormatHexKey(xorKey), report.RecoveredXorKeyHex);
                Assert.Equal(xorKey, report.RecoveredXorKeyBytes);
                Assert.True(report.RecoveredXorAppliesToWholeFile);
                Assert.Equal(version, report.DetectedMetadataVersion);
                Assert.Equal(version, report.RecoveredMetadataVersion);
                Assert.Equal(Il2CppSymbolObfuscationKind.Clean, report.SymbolObfuscationKind);
                Assert.Equal(3, report.AssemblyCount);
                Assert.Equal(CleanGameTypeSpecs().Length, report.GameTypeCount);

                // Verify Il2CppMetadataReader extracts a real game type using the recovered XOR key!
                var extracted = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Sample.Gameplay",
                        ClassName = "PlayerController"
                    },
                    report.RecoveredXorKeyBytes);

                Assert.NotNull(extracted);
                Assert.True(extracted!.Success);
                Assert.Contains("public class PlayerController", extracted.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Health;", extracted.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public void Tick();", extracted.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Audit_WholeFileRepeatingXor16ByteCompactUnity6Metadata_RecoversExactKeyAndDecodesTypes()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var classic = BuildSyntheticClassicMetadata(31, CleanGameTypeSpecs());
                var compact = RewriteAsCompactMetadata(classic, version: 39, typeIndexWidth: 2);
                var xorKey = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18, 0x29, 0x3A, 0x4B, 0x5C, 0x6D, 0x7E, 0x8F, 0x90 };
                var masked = ApplyRepeatingXor(compact, xorKey);

                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, masked);

                var reader = new Il2CppMetadataReader();
                var service = new Il2CppMetadataAuditService(reader);
                var report = service.Audit(metadataPath);

                Assert.Equal(Il2CppMetadataProtectionKind.SimpleXorRecoverable, report.ProtectionKind);
                Assert.Equal(Il2CppMetadataAuditService.FormatHexKey(xorKey), report.RecoveredXorKeyHex);
                Assert.Equal(39, report.DetectedMetadataVersion);
                Assert.Equal(39, report.RecoveredMetadataVersion);

                var extracted = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Sample.Gameplay",
                        ClassName = "PlayerController"
                    },
                    report.RecoveredXorKeyBytes);

                Assert.NotNull(extracted);
                Assert.True(extracted!.Success);
                Assert.Contains("public class PlayerController", extracted.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Audit_PlaintextHeaderWithBodyOnlyRepeatingXor_ClassifiesSimpleXorRecoverable()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var raw = BuildSyntheticClassicMetadata(29, CleanGameTypeSpecs());
                var headerSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8, 4));
                var bodyKey = new byte[] { 0x3C, 0xA5, 0x5A, 0xC3 };
                var masked = (byte[])raw.Clone();
                for (var i = headerSize; i < masked.Length; i++)
                {
                    masked[i] ^= bodyKey[i % bodyKey.Length];
                }

                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, masked);

                var service = new Il2CppMetadataAuditService();
                var report = service.Audit(metadataPath);

                Assert.Equal(Il2CppMetadataProtectionKind.SimpleXorRecoverable, report.ProtectionKind);
                Assert.False(report.RecoveredXorAppliesToWholeFile);
                Assert.Equal(Il2CppMetadataAuditService.FormatHexKey(bodyKey), report.RecoveredXorKeyHex);
                Assert.Equal(29, report.DetectedMetadataVersion);
                Assert.Equal(29, report.RecoveredMetadataVersion);
                Assert.Equal(Il2CppSymbolObfuscationKind.Clean, report.SymbolObfuscationKind);
                Assert.Equal(CleanGameTypeSpecs().Length, report.GameTypeCount);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Audit_LargeEncryptedPayload_CompletesRapidlyWithBoundedSampling()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var rng = new Random(202611);
                var payload = new byte[4 * 1024 * 1024]; // 4 MB encrypted payload
                rng.NextBytes(payload);
                payload[0] = 0x55;
                payload[1] = 0xAA;

                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, payload);

                var service = new Il2CppMetadataAuditService();
                var report = service.Audit(metadataPath);

                Assert.Equal(Il2CppMetadataProtectionKind.HighEntropyEncrypted, report.ProtectionKind);
                Assert.Equal(payload.Length, report.FileSizeBytes);
                Assert.True(report.HeaderEntropy >= 7.4);
                Assert.True(report.BodyEntropy >= 7.4);
                Assert.True(report.ElapsedMs < 500, $"Expected fast bounded audit, took {report.ElapsedMs} ms");
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Audit_HighEntropyEncryptedPayload_ClassifiesEncryptedWithActionableGuidance()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var rng = new Random(133742);
                var encrypted = new byte[128 * 1024];
                rng.NextBytes(encrypted);
                // Ensure raw magic is not accidentally 0xFAB11BAF
                encrypted[0] = 0x12;
                encrypted[1] = 0x34;
                encrypted[2] = 0x56;
                encrypted[3] = 0x78;

                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, encrypted);

                var service = new Il2CppMetadataAuditService();
                var report = service.Audit(metadataPath, loadedTypeTreeCount: 14);

                Assert.Equal(Il2CppMetadataProtectionKind.HighEntropyEncrypted, report.ProtectionKind);
                Assert.Equal(Il2CppSymbolObfuscationKind.Unknown, report.SymbolObfuscationKind);
                Assert.True(report.HeaderEntropy > 7.5, $"Expected header entropy > 7.5, got {report.HeaderEntropy:F3}");
                Assert.True(report.BodyEntropy > 7.5, $"Expected body entropy > 7.5, got {report.BodyEntropy:F3}");
                Assert.True(report.HeaderZeroByteRatio < 0.05);
                Assert.Empty(report.RecoveredXorKeyHex);
                Assert.Contains(report.RecommendedNextSteps, s => s.Contains("MetadataLoader::LoadMetadataFile", StringComparison.Ordinal));
                Assert.Contains(report.RecommendedNextSteps, s => s.Contains("TypeTree", StringComparison.Ordinal));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Audit_ShiftedHeaderTruncatedAndMissingFiles_ClassifyAccurately()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var service = new Il2CppMetadataAuditService();

                // 1. Missing file
                var missing = service.Audit(Path.Combine(tempRoot, "missing-global-metadata.dat"));
                Assert.Equal(Il2CppMetadataProtectionKind.NoneOrMissing, missing.ProtectionKind);
                Assert.Equal(Il2CppSymbolObfuscationKind.Unknown, missing.SymbolObfuscationKind);

                // 2. Truncated file (< 208 bytes)
                var truncatedPath = Path.Combine(tempRoot, "truncated.dat");
                File.WriteAllBytes(truncatedPath, new byte[64]);
                var truncated = service.Audit(truncatedPath);
                Assert.Equal(Il2CppMetadataProtectionKind.InvalidOrTruncated, truncated.ProtectionKind);

                // 3. Shifted header (0xFAB11BAF shifted by +32 bytes)
                var raw = BuildSyntheticClassicMetadata(29, CleanGameTypeSpecs());
                var shifted = new byte[32 + raw.Length];
                Array.Fill(shifted, (byte)0x41, 0, 32);
                Buffer.BlockCopy(raw, 0, shifted, 32, raw.Length);
                var shiftedPath = Path.Combine(tempRoot, "shifted.dat");
                File.WriteAllBytes(shiftedPath, shifted);

                var shiftedReport = service.Audit(shiftedPath);
                Assert.Equal(Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout, shiftedReport.ProtectionKind);
                Assert.Contains("0x20", shiftedReport.DiagnosticDetail, StringComparison.Ordinal);

                // 4. Valid magic but corrupted table header size
                var broken = (byte[])raw.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(8, 4), 999999);
                var brokenPath = Path.Combine(tempRoot, "broken-header.dat");
                File.WriteAllBytes(brokenPath, broken);

                var brokenReport = service.Audit(brokenPath);
                Assert.Equal(Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout, brokenReport.ProtectionKind);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Audit_SymbolObfuscationDetection_DistinguishesHeavilyPartiallyAndCleanGameTypes()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var service = new Il2CppMetadataAuditService();

                // 1. Heavily obfuscated game types (BeeByte 11-char uppercase, homoglyphs, Unicode invisible/private-use, hex/base62)
                var heavySpecs = new (string Ns, string Name)[]
                {
                    ("Sample.Gameplay", "ABCDEFGHIJK"),
                    ("Sample.Gameplay", "ECLJAGIGDDM"),
                    ("Sample.Gameplay", "KGPKONNLLPK"),
                    ("Sample.Gameplay", "LIGFLDKFOLA"),
                    ("", "O0O0O0O0"),
                    ("", "l1l1l1l1"),
                    ("", "I1I1I1I1"),
                    ("", "\u200BHiddenType"),
                    ("", "xK9mP2qR7z"),
                    ("", "a3f90c2e"),
                    ("Sample.Gameplay", "PlayerController") // 1 clean type out of 11 -> 10/11 = 90.9%
                };
                var heavyPath = Path.Combine(tempRoot, "heavy.dat");
                File.WriteAllBytes(heavyPath, BuildSyntheticClassicMetadata(29, heavySpecs));

                var heavyReport = service.Audit(heavyPath);
                Assert.Equal(Il2CppMetadataProtectionKind.StandardClassic, heavyReport.ProtectionKind);
                Assert.Equal(Il2CppSymbolObfuscationKind.HeavilyObfuscated, heavyReport.SymbolObfuscationKind);
                Assert.True(heavyReport.ObfuscatedTypeRatio > 0.80, $"Expected ratio > 0.80, got {heavyReport.ObfuscatedTypeRatio:P1}");
                Assert.Equal(10, heavyReport.ObfuscatedGameTypeCount);
                Assert.Equal(11, heavyReport.GameTypeCount);
                Assert.Equal(10, heavyReport.SampleObfuscatedNames.Count);
                Assert.Contains(heavyReport.SampleObfuscatedNames, n => n.Contains("ECLJAGIGDDM", StringComparison.Ordinal));
                Assert.Contains(heavyReport.SampleObfuscatedNames, n => n.Contains("O0O0O0O0", StringComparison.Ordinal));
                Assert.DoesNotContain(heavyReport.SampleObfuscatedNames, n => n.Contains("PlayerController", StringComparison.Ordinal));

                // 2. Partially obfuscated (2 of 10 = 20%)
                var partialSpecs = new (string Ns, string Name)[]
                {
                    ("Sample.Gameplay", "PlayerController"),
                    ("Sample.Gameplay", "InventoryManager"),
                    ("Sample.Gameplay", "AudioSettings"),
                    ("Sample.Gameplay", "LevelLoader"),
                    ("Sample.Gameplay", "QuestTracker"),
                    ("Sample.Gameplay", "SaveDataSerializer"),
                    ("Sample.Gameplay", "CameraRig"),
                    ("Sample.Gameplay", "InputRouter"),
                    ("Sample.Gameplay", "ECLJAGIGDDM"),
                    ("Sample.Gameplay", "O0O0O0O0")
                };
                var partialPath = Path.Combine(tempRoot, "partial.dat");
                File.WriteAllBytes(partialPath, BuildSyntheticClassicMetadata(29, partialSpecs));

                var partialReport = service.Audit(partialPath);
                Assert.Equal(Il2CppSymbolObfuscationKind.PartiallyObfuscated, partialReport.SymbolObfuscationKind);
                Assert.InRange(partialReport.ObfuscatedTypeRatio, 0.19, 0.21);

                // 3. Short 1-2 char cohort (a, b, c, d, e) -> HeavilyObfuscated
                var shortSpecs = new (string Ns, string Name)[]
                {
                    ("", "a"),
                    ("", "b"),
                    ("", "c"),
                    ("", "d"),
                    ("", "e"),
                    ("Sample.Gameplay", "MainMenu")
                };
                var shortPath = Path.Combine(tempRoot, "short.dat");
                File.WriteAllBytes(shortPath, BuildSyntheticClassicMetadata(29, shortSpecs));

                var shortReport = service.Audit(shortPath);
                Assert.Equal(Il2CppSymbolObfuscationKind.HeavilyObfuscated, shortReport.SymbolObfuscationKind);
                Assert.Equal(5, shortReport.ObfuscatedGameTypeCount);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProbeNativeBinary_SyntheticPe64AndRawBuffer_ExtractsOffsetsSectionsAndMarkersSafely()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                // 1. Synthetic PE64 binary with .rdata (strings) and .vmp0 (high-entropy random section)
                var pePath = Path.Combine(tempRoot, "GameAssembly.dll");
                var expectedMetaStringOffset = WriteSyntheticPe64ForProbe(pePath);

                var peProbe = Il2CppMetadataAuditService.ProbeNativeBinary(pePath);
                Assert.True(peProbe.IsPeBinary);
                Assert.False(peProbe.IsElfBinary);
                Assert.True(peProbe.HasHighEntropySection);
                Assert.Contains(peProbe.Sections, s => s.Name == ".rdata" && !s.IsHighEntropy);
                Assert.Contains(peProbe.Sections, s => s.Name == ".vmp0" && s.IsHighEntropy);

                var hit = Assert.Single(peProbe.MetadataStringOffsets);
                Assert.Equal("global-metadata.dat", hit.Pattern);
                Assert.Equal(expectedMetaStringOffset, hit.FileOffset);
                Assert.Equal(".rdata", hit.SectionName);

                Assert.Contains(peProbe.Indicators, s => s.Contains("global-metadata.dat", StringComparison.Ordinal));
                Assert.Contains(peProbe.Indicators, s => s.Contains("MetadataLoader", StringComparison.Ordinal));
                Assert.Contains(peProbe.Indicators, s => s.Contains("il2cpp_init", StringComparison.Ordinal));
                Assert.Contains(peProbe.Indicators, s => s.Contains("Obfuz", StringComparison.Ordinal));
                Assert.Contains(peProbe.Indicators, s => s.Contains("BeeByte", StringComparison.Ordinal));
                Assert.Contains(peProbe.Indicators, s => s.Contains("HybridCLR", StringComparison.Ordinal));
                Assert.Contains(peProbe.Indicators, s => s.Contains("VMProtect", StringComparison.Ordinal));

                // 2. End-to-end Audit combining metadata + PE binary + backup Managed folder detection
                var projectRoot = Path.Combine(tempRoot, "SampleUnityGame");
                var metaDir = Path.Combine(projectRoot, "SampleUnityGame_Data", "il2cpp_data", "Metadata");
                var backupManaged = Path.Combine(projectRoot, "SampleUnityGame_BackUpThisFolder_ButDontShipItWithYourGame", "Managed");
                Directory.CreateDirectory(metaDir);
                Directory.CreateDirectory(backupManaged);

                var metaPath = Path.Combine(metaDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, BuildSyntheticClassicMetadata(29, CleanGameTypeSpecs()));

                var report = new Il2CppMetadataAuditService().Audit(
                    metadataPath: metaPath,
                    gameAssemblyPath: pePath,
                    projectRoot: projectRoot);

                Assert.True(report.HasBackupManagedDlls);
                Assert.True(report.HasHighEntropyBinarySection);
                Assert.Single(report.MetadataStringOffsetsInBinary);
                Assert.Contains(report.RecommendedNextSteps, s => s.Contains("Backup Managed DLLs", StringComparison.Ordinal));
                Assert.Contains(report.RecommendedNextSteps, s => s.Contains($"0x{expectedMetaStringOffset:X}", StringComparison.Ordinal));

                var formatted = report.FormatSummaryReport();
                Assert.Contains(".rdata", formatted, StringComparison.Ordinal);
                Assert.Contains(".vmp0", formatted, StringComparison.Ordinal);
                Assert.Contains("Backup Managed DLLs", formatted, StringComparison.Ordinal);

                // 3. Synthetic ELF buffer (non-PE fallback streaming scan)
                var elfPath = Path.Combine(tempRoot, "libil2cpp.so");
                var elfBytes = new byte[4096];
                elfBytes[0] = 0x7F;
                elfBytes[1] = (byte)'E';
                elfBytes[2] = (byte)'L';
                elfBytes[3] = (byte)'F';
                Encoding.ASCII.GetBytes("il2cpp::vm::MetadataCache").CopyTo(elfBytes, 256);
                Encoding.ASCII.GetBytes("global-metadata.dat").CopyTo(elfBytes, 1024);
                File.WriteAllBytes(elfPath, elfBytes);

                var elfProbe = Il2CppMetadataAuditService.ProbeNativeBinary(elfPath);
                Assert.False(elfProbe.IsPeBinary);
                Assert.True(elfProbe.IsElfBinary);
                var elfHit = Assert.Single(elfProbe.MetadataStringOffsets);
                Assert.Equal(1024, elfHit.FileOffset);
                Assert.Equal("ELF", elfHit.SectionName);
                Assert.Contains(elfProbe.Indicators, s => s.Contains("MetadataCache", StringComparison.Ordinal));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task PureMvvmIntegration_CommandHandlersViewModelPreviewSessionAndZeroAvaloniaInvariants()
        {
            using var vm = new MainWindowViewModel();

            // 1. Default headless execution switches to Script tab and updates StatusText
            Assert.True(vm.AuditIl2CppMetadataCommand.CanExecute());
            await vm.AuditIl2CppMetadataCommand.ExecuteAsync();
            Assert.Equal(RightPanelTab.Script, vm.PreviewSession.ActiveRightTab);
            Assert.Contains("audit IL2CPP", vm.StatusText, StringComparison.OrdinalIgnoreCase);

            // 2. Command handler override wiring + MainWindowCommandHandlers.Clear()
            var handlerInvoked = 0;
            vm.CommandHandlers.AuditIl2CppMetadataAsync = () =>
            {
                handlerInvoked++;
                return Task.CompletedTask;
            };
            await vm.AuditIl2CppMetadataCommand.ExecuteAsync();
            Assert.Equal(1, handlerInvoked);

            vm.CommandHandlers.Clear();
            Assert.Null(vm.CommandHandlers.AuditIl2CppMetadataAsync);

            // 3. PreviewSessionViewModel.ShowScriptAuditReport populates viewer & audit banner cleanly
            var sampleReport = new Il2CppMetadataAuditReport
            {
                MetadataPath = "global-metadata.dat",
                ProtectionKind = Il2CppMetadataProtectionKind.CompactUnity6,
                SymbolObfuscationKind = Il2CppSymbolObfuscationKind.Clean,
                DetectedMetadataVersion = 39,
                AssemblyCount = 4,
                TypeCount = 120,
                GameAssemblyCount = 1,
                GameTypeCount = 42,
                ElapsedMs = 3,
                RecommendedNextSteps = new[] { "Metadata is readable directly (Unity 6 compact v39)." }
            };
            vm.PreviewSession.ShowScriptAuditReport(sampleReport);
            Assert.True(vm.PreviewSession.HasIl2CppAuditReport);
            Assert.Same(sampleReport, vm.PreviewSession.LastIl2CppAuditReport);
            Assert.True(vm.PreviewSession.HasIl2CppAuditSummaryBanner);
            Assert.Contains("v39", vm.PreviewSession.Il2CppAuditSummaryBanner, StringComparison.Ordinal);
            Assert.Contains("IL2CPP Static Metadata & Obfuscation Audit Report", vm.PreviewSession.ScriptFullSourceCode, StringComparison.Ordinal);
            Assert.True(vm.PreviewSession.CanSaveOrCopyScript);

            vm.PreviewSession.ClearScript();
            Assert.False(vm.PreviewSession.HasIl2CppAuditReport);
            Assert.Empty(vm.PreviewSession.Il2CppAuditSummaryBanner);

            // 4. Dispose disables command CanExecute
            vm.Dispose();
            Assert.False(vm.AuditIl2CppMetadataCommand.CanExecute());

            // 5. Static source invariants: ZERO Click= in MainWindow.axaml, Audit IL2CPP button bound to AuditIl2CppMetadataCommand,
            //    and ZERO `using Avalonia` in Il2CppMetadataAuditService.cs or ViewModels/
            var repoRoot = LocateRepositoryRoot();
            var axamlPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml");
            var axamlContent = File.ReadAllText(axamlPath);
            Assert.False(AxamlClickAttributeRegex.IsMatch(axamlContent), "MainWindow.axaml must contain zero Click= attributes.");
            Assert.Contains("x:Name=\"ScriptAuditIl2CppButton\"", axamlContent, StringComparison.Ordinal);
            Assert.Contains("Command=\"{Binding AuditIl2CppMetadataCommand}\"", axamlContent, StringComparison.Ordinal);

            var auditServicePath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "Scripting", "Il2CppMetadataAuditService.cs");
            Assert.True(File.Exists(auditServicePath));
            Assert.False(AvaloniaUsingRegex.IsMatch(File.ReadAllText(auditServicePath)), "Il2CppMetadataAuditService.cs must not import Avalonia.");

            var viewModelsDir = Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels");
            foreach (var vmFile in Directory.GetFiles(viewModelsDir, "*.cs", SearchOption.AllDirectories))
            {
                Assert.False(
                    AvaloniaUsingRegex.IsMatch(File.ReadAllText(vmFile)),
                    $"{Path.GetFileName(vmFile)} must not import Avalonia.");
            }
        }

        private static (string Ns, string Name)[] CleanGameTypeSpecs() => new[]
        {
            ("Sample.Gameplay", "PlayerController"),
            ("Sample.Gameplay", "InventoryManager"),
            ("Sample.Gameplay", "DamageCalculator"),
            ("Sample.Gameplay", "QuestSystem"),
            ("Sample.UI", "HudPresenter"),
            ("Sample.Audio", "SoundMixer")
        };

        private static byte[] ApplyRepeatingXor(byte[] input, byte[] key)
        {
            var output = new byte[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                output[i] = (byte)(input[i] ^ key[i % key.Length]);
            }
            return output;
        }

        private static long WriteSyntheticPe64ForProbe(string path)
        {
            // PE64 with 2 sections:
            //   .rdata at file offset 0x200, size 0x400 (ASCII markers + global-metadata.dat)
            //   .vmp0  at file offset 0x600, size 0x400 (pseudo-random high-entropy payload)
            const int peOffset = 0x80;
            const int rdataOffset = 0x200;
            const int rdataSize = 0x400;
            const int vmpOffset = 0x600;
            const int vmpSize = 0x400;
            var bytes = new byte[vmpOffset + vmpSize];

            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), peOffset);

            bytes[peOffset + 0] = (byte)'P';
            bytes[peOffset + 1] = (byte)'E';
            bytes[peOffset + 2] = 0;
            bytes[peOffset + 3] = 0;

            // COFF header
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 4, 2), 0x8664); // AMD64
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2), 2);      // NumberOfSections = 2
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2), 0xF0);  // SizeOfOptionalHeader = 240
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 22, 2), 0x2022);// Characteristics = DLL | Executable

            // PE32+ Optional Header
            var optOff = peOffset + 24;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optOff, 2), 0x020B);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 32, 4), 0x1000);  // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 36, 4), 0x200);   // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 56, 4), 0x3000);  // SizeOfImage
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 60, 4), 0x200);   // SizeOfHeaders
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 108, 4), 16);     // NumberOfRvaAndSizes

            // Section 0: .rdata
            var sec0 = optOff + 0xF0;
            Encoding.ASCII.GetBytes(".rdata").CopyTo(bytes, sec0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec0 + 8, 4), rdataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec0 + 12, 4), 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec0 + 16, 4), rdataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec0 + 20, 4), rdataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec0 + 36, 4), 0x40000040);

            // Section 1: .vmp0
            var sec1 = sec0 + 40;
            Encoding.ASCII.GetBytes(".vmp0").CopyTo(bytes, sec1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec1 + 8, 4), vmpSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec1 + 12, 4), 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec1 + 16, 4), vmpSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec1 + 20, 4), vmpOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sec1 + 36, 4), 0xE0000020);

            // Populate .rdata with forensic indicators
            var metaStrOffset = rdataOffset + 0x40;
            Encoding.ASCII.GetBytes("il2cpp_init\0MetadataLoader::LoadMetadataFile\0").CopyTo(bytes, rdataOffset + 0x08);
            Encoding.ASCII.GetBytes("il2cpp_data/Metadata/global-metadata.dat\0").CopyTo(bytes, metaStrOffset - 21);
            Encoding.ASCII.GetBytes("Obfuz\0BeeByte\0HybridCLR\0").CopyTo(bytes, rdataOffset + 0x100);

            // Populate .vmp0 with pseudo-random high-entropy bytes
            var rng = new Random(998877);
            rng.NextBytes(bytes.AsSpan(vmpOffset, vmpSize));

            File.WriteAllBytes(path, bytes);
            return metaStrOffset;
        }

        private static byte[] BuildSyntheticClassicMetadata(int version, (string Ns, string Name)[] gameTypes)
        {
            var stringPool = new MemoryStream();
            var stringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            int AddStr(string s)
            {
                if (stringOffsets.TryGetValue(s, out var existing))
                {
                    return existing;
                }
                var offset = (int)stringPool.Position;
                var utf8 = Encoding.UTF8.GetBytes(s);
                stringPool.Write(utf8, 0, utf8.Length);
                stringPool.WriteByte(0);
                stringOffsets[s] = offset;
                return offset;
            }

            var sEmpty = AddStr(string.Empty);
            var sModule = AddStr("<Module>");
            var sMscorlib = AddStr("mscorlib.dll");
            var sUnityCore = AddStr("UnityEngine.CoreModule.dll");
            var sGameDll = AddStr("Assembly-CSharp.dll");
            var sSystem = AddStr("System");
            var sUnityEngine = AddStr("UnityEngine");
            var sObject = AddStr("Object");
            var sVoid = AddStr("Void");
            var sInt32 = AddStr("Int32");
            var sString = AddStr("String");
            var sMonoBehaviour = AddStr("MonoBehaviour");
            var sHealth = AddStr("Health");
            var sTick = AddStr("Tick");

            var gameNameOffsets = new (int NsOff, int NameOff)[gameTypes.Length];
            for (var i = 0; i < gameTypes.Length; i++)
            {
                gameNameOffsets[i] = (AddStr(gameTypes[i].Ns), AddStr(gameTypes[i].Name));
            }

            const int TyObject = 101;
            const int TyVoid = 102;
            const int TyInt32 = 103;
            const int TyString = 104;
            const int TyMonoBehaviour = 105;

            var tdSize = version >= 27 ? 88 : 92;
            var methodSize = version >= 31 ? 36 : 32;
            const int fieldSize = 12;
            const int imgSize = 40;
            var pairCount = version == 24 ? 32 : 31;
            var headerSize = 8 + pairCount * 8;

            // Single field "Health" on first game type (TypeDef index 6)
            var fieldsMs = new MemoryStream();
            {
                var buf = new byte[fieldSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), sHealth);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), TyInt32);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fieldSize - 4, 4), 0x04000001);
                fieldsMs.Write(buf);
            }

            // Single method "Tick" on first game type (TypeDef index 6)
            var methodsMs = new MemoryStream();
            {
                var buf = new byte[methodSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), sTick);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), 6);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), TyVoid);
                if (methodSize == 36)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), 0x08000000);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), -1);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), -1);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), -1);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), -1);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(methodSize - 12, 4), 0x06000001);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(methodSize - 8, 2), 0x0086); // public
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(methodSize - 2, 2), 0);
                methodsMs.Write(buf);
            }

            var tdMs = new MemoryStream();
            uint nextToken = 0x02000001;
            void WriteTypeDef(int nameIdx, int nsIdx, int byvalIdx, int parentIdx, int fStart, int mStart, ushort mCount, ushort fCount, uint bitfield = 0)
            {
                var buf = new byte[tdSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), nsIdx);

                const int byvalOff = 8;
                var declOff = version >= 27 ? 12 : 16;
                var parentOff = declOff + 4;
                var elemOff = declOff + 8;
                var gcOff = version >= 27 ? 24 : 28;
                var flagsOff = gcOff + 4;
                var fStartOff = gcOff + 8;
                var mStartOff = gcOff + 12;
                var eStartOff = gcOff + 16;
                var pStartOff = gcOff + 20;
                var nStartOff = gcOff + 24;
                var iStartOff = gcOff + 28;

                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(byvalOff, 4), byvalIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(declOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(parentOff, 4), parentIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(elemOff, 4), byvalIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(gcOff, 4), -1);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(flagsOff, 4), 1); // public
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(fStartOff, 4), fStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(mStartOff, 4), mStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(eStartOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pStartOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(nStartOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(iStartOff, 4), -1);

                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tdSize - 24, 2), mCount);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tdSize - 20, 2), fCount);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(tdSize - 8, 4), bitfield);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(tdSize - 4, 4), nextToken++);
                tdMs.Write(buf);
            }

            // Image 0: mscorlib.dll [0..5)
            WriteTypeDef(sModule, sEmpty, 100, -1, -1, -1, 0, 0);
            WriteTypeDef(sObject, sSystem, TyObject, -1, -1, -1, 0, 0);
            WriteTypeDef(sVoid, sSystem, TyVoid, TyObject, -1, -1, 0, 0, bitfield: 1);
            WriteTypeDef(sInt32, sSystem, TyInt32, TyObject, -1, -1, 0, 0, bitfield: 1);
            WriteTypeDef(sString, sSystem, TyString, TyObject, -1, -1, 0, 0);

            // Image 1: UnityEngine.CoreModule.dll [5..6)
            WriteTypeDef(sMonoBehaviour, sUnityEngine, TyMonoBehaviour, TyObject, -1, -1, 0, 0);

            // Image 2: Assembly-CSharp.dll [6..6+gameTypes.Length)
            for (var i = 0; i < gameTypes.Length; i++)
            {
                var (nsOff, nameOff) = gameNameOffsets[i];
                if (i == 0)
                {
                    WriteTypeDef(nameOff, nsOff, 200 + i, TyMonoBehaviour, fStart: 0, mStart: 0, mCount: 1, fCount: 1);
                }
                else
                {
                    WriteTypeDef(nameOff, nsOff, 200 + i, TyObject, fStart: -1, mStart: -1, mCount: 0, fCount: 0);
                }
            }

            // Images
            var imgMs = new MemoryStream();
            void WriteImg(int nameIdx, int asmIdx, int typeStart, int typeCount)
            {
                var buf = new byte[imgSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), asmIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), typeStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), typeCount);
                imgMs.Write(buf);
            }
            WriteImg(sMscorlib, 0, 0, 5);
            WriteImg(sUnityCore, 1, 5, 1);
            WriteImg(sGameDll, 2, 6, gameTypes.Length);

            var sections = new byte[pairCount][];
            for (var i = 0; i < pairCount; i++)
            {
                sections[i] = Array.Empty<byte>();
            }
            sections[2] = stringPool.ToArray();
            sections[5] = methodsMs.ToArray();
            sections[11] = fieldsMs.ToArray();
            sections[19] = tdMs.ToArray();
            sections[20] = imgMs.ToArray();

            using var outMs = new MemoryStream();
            var header = new byte[headerSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Il2CppMetadataReader.MetadataSanity);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), version);

            var cursor = headerSize;
            for (var i = 0; i < pairCount; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + i * 8, 4), cursor);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12 + i * 8, 4), sections[i].Length);
                cursor += sections[i].Length;
            }

            outMs.Write(header);
            for (var i = 0; i < pairCount; i++)
            {
                if (sections[i].Length > 0)
                {
                    outMs.Write(sections[i]);
                }
            }

            return outMs.ToArray();
        }

        private static byte[] RewriteAsCompactMetadata(byte[] classic, int version, int typeIndexWidth)
        {
            const int sectionCount = 31;
            var isV107 = version == 107;
            var tables = new byte[sectionCount][];
            for (var i = 0; i < sectionCount; i++)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(classic.AsSpan(8 + i * 8, 4));
                var size = BinaryPrimitives.ReadInt32LittleEndian(classic.AsSpan(12 + i * 8, 4));
                tables[i] = classic.AsSpan(offset, size).ToArray();
            }

            static int WidthFor(int count) => count <= byte.MaxValue ? 1 : count <= ushort.MaxValue ? 2 : 4;
            int Rows(int section, int classicRowSize) => tables[section].Length / classicRowSize;
            int PerTable(int count) => isV107 ? WidthFor(count) : 4;

            var t = typeIndexWidth;
            var td = WidthFor(Rows(19, 88));
            var gc = WidthFor(Rows(14, 16));
            var p = WidthFor(Rows(10, 12));
            var gp = PerTable(Rows(12, 16));
            var itf = PerTable(Rows(18, 8));
            var e = PerTable(Rows(3, 24));
            var pr = PerTable(Rows(4, 20));
            var n = PerTable(Rows(15, 4));
            var f = PerTable(Rows(11, 12));
            var dvd = PerTable(tables[8].Length);
            var m = PerTable(Rows(5, 36));

            var layouts = new Dictionary<int, (int Src, int Dst)[]>
            {
                [3] = new[] { (4, 4), (4, t), (4, m), (4, m), (4, m), (4, 4) },
                [4] = new[] { (4, 4), (4, m), (4, m), (4, 4), (4, 4) },
                [5] = new[] { (4, 4), (4, td), (4, t), (4, 4), (4, p), (4, gc), (4, 4), (2, 2), (2, 2), (2, 2), (2, 2) },
                [6] = new[] { (4, p), (4, t), (4, dvd) },
                [7] = new[] { (4, f), (4, t), (4, dvd) },
                [9] = new[] { (4, f), (4, t), (4, 4) },
                [10] = new[] { (4, 4), (4, 4), (4, t) },
                [11] = new[] { (4, 4), (4, t), (4, 4) },
                [12] = new[] { (4, gc), (4, 4), (2, 2), (2, 2), (2, 2), (2, 2) },
                [13] = new[] { (4, t) },
                [14] = isV107 ? new[] { (4, 4), (4, 2), (4, 1), (4, gp) } : new[] { (4, 4), (4, 4), (4, 4), (4, 4) },
                [15] = new[] { (4, 4) },
                [16] = new[] { (4, t) },
                [17] = new[] { (4, 4) },
                [18] = new[] { (4, t), (4, 4) },
                [19] = new[]
                {
                    (4, 4), (4, 4), (4, t), (4, t), (4, t), (4, 0), (4, gc), (4, 4),
                    (4, f), (4, m), (4, e), (4, pr), (4, n), (4, itf), (4, 4), (4, itf),
                    (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (4, 4), (4, 4)
                },
                [20] = new[] { (4, 4), (4, 4), (4, td), (4, 4), (4, td), (4, 4), (4, m), (4, 4), (4, 4), (4, 4) },
                [22] = new[] { (4, t), (4, f) },
                [25] = new[] { (4, 4), (4, 4) }
            };

            var sections = new List<(byte[] Data, int Count)>(sectionCount + 1);
            for (var i = 0; i < sectionCount; i++)
            {
                if (layouts.TryGetValue(i, out var layout))
                {
                    var classicRowSize = layout.Sum(static c => c.Src);
                    var rows = tables[i].Length / classicRowSize;
                    sections.Add((NarrowRows(tables[i], rows, layout), rows));
                }
                else
                {
                    sections.Add((tables[i], tables[i].Length));
                }
            }

            if (isV107)
            {
                sections.Insert(20, (Array.Empty<byte>(), 0));
            }

            var headerSize = 8 + sections.Count * 12;
            var output = new byte[headerSize + sections.Sum(static s => s.Data.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), Il2CppMetadataReader.MetadataSanity);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4, 4), version);
            var cursor = headerSize;
            for (var i = 0; i < sections.Count; i++)
            {
                var (data, count) = sections[i];
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8 + i * 12, 4), cursor);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12 + i * 12, 4), data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16 + i * 12, 4), count);
                data.CopyTo(output.AsSpan(cursor));
                cursor += data.Length;
            }

            return output;
        }

        private static byte[] NarrowRows(byte[] classicRows, int rows, (int Src, int Dst)[] layout)
        {
            var srcRowSize = layout.Sum(static c => c.Src);
            var dstRowSize = layout.Sum(static c => c.Dst);
            var output = new byte[rows * dstRowSize];
            for (var r = 0; r < rows; r++)
            {
                var s = r * srcRowSize;
                var d = r * dstRowSize;
                foreach (var (src, dst) in layout)
                {
                    if (src == dst)
                    {
                        classicRows.AsSpan(s, src).CopyTo(output.AsSpan(d, dst));
                    }
                    else if (dst > 0)
                    {
                        var value = BinaryPrimitives.ReadInt32LittleEndian(classicRows.AsSpan(s, 4));
                        var allOnes = dst == 1 ? 0xFFu : dst == 2 ? 0xFFFFu : uint.MaxValue;
                        var encoded = value == -1 ? allOnes : (uint)value;
                        for (var b = 0; b < dst; b++)
                        {
                            output[d + b] = (byte)(encoded >> (8 * b));
                        }
                    }

                    s += src;
                    d += dst;
                }
            }

            return output;
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "AssetStudio_Il2CppAuditTests_" + Guid.NewGuid().ToString("N"));
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

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }
    }
}
