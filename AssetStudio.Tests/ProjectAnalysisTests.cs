using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class ProjectAnalysisTests
    {
        [Theory]
        [InlineData(24)]
        [InlineData(29)]
        [InlineData(31)]
        [InlineData(39)]
        public void ProjectRuntimeDetector_Il2CppMetadataMagicAndVersion_ParsesValidVersions(int expectedVersion)
        {
            var tempRoot = CreateTempDirectory("il2cpp_meta_valid");
            try
            {
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                WriteIl2CppMetadataHeader(metadataPath, ProjectRuntimeDetector.Il2CppMetadataSanity, expectedVersion, totalLength: 64);

                var ok = ProjectRuntimeDetector.TryReadIl2CppMetadataVersion(metadataPath, out var version, out var warning);

                Assert.True(ok);
                Assert.Equal(expectedVersion, version);
                Assert.Null(warning);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectRuntimeDetector_Il2CppMetadata_RejectsInvalidMagicTruncatedAndObfuscatedVersion()
        {
            var tempRoot = CreateTempDirectory("il2cpp_meta_invalid");
            try
            {
                // 1. Invalid magic
                var badMagicPath = Path.Combine(tempRoot, "bad-magic.dat");
                WriteIl2CppMetadataHeader(badMagicPath, 0xDEADBEEF, 29, totalLength: 32);
                Assert.False(ProjectRuntimeDetector.TryReadIl2CppMetadataVersion(badMagicPath, out _, out var badMagicWarn));
                Assert.NotNull(badMagicWarn);
                Assert.Contains("magic mismatch", badMagicWarn!, StringComparison.OrdinalIgnoreCase);

                // 2. Truncated header (< 8 bytes)
                var truncatedPath = Path.Combine(tempRoot, "truncated.dat");
                File.WriteAllBytes(truncatedPath, new byte[] { 0xAF, 0x1B, 0xB1, 0xFA });
                Assert.False(ProjectRuntimeDetector.TryReadIl2CppMetadataVersion(truncatedPath, out _, out var truncWarn));
                Assert.NotNull(truncWarn);
                Assert.Contains("truncated", truncWarn!, StringComparison.OrdinalIgnoreCase);

                // 3. Obfuscated metadata (valid magic 0xFAB11BAF but version == fileLength, e.g. EXILIUM)
                const int obfuscatedLength = 4096;
                var obfuscatedPath = Path.Combine(tempRoot, "obfuscated.dat");
                WriteIl2CppMetadataHeader(obfuscatedPath, ProjectRuntimeDetector.Il2CppMetadataSanity, obfuscatedLength, totalLength: obfuscatedLength);
                Assert.False(ProjectRuntimeDetector.TryReadIl2CppMetadataVersion(obfuscatedPath, out _, out var obfWarn));
                Assert.NotNull(obfWarn);
                Assert.Contains("obfuscated", obfWarn!, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("matches file length", obfWarn!, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task ProjectAnalysisService_ObfuscatedIl2CppMetadata_DoesNotFailAndEmitsDecryptionHint()
        {
            var tempRoot = CreateTempDirectory("il2cpp_obfuscated_e2e");
            try
            {
                var gameRoot = Path.Combine(tempRoot, "ExiliumLikeGame");
                var cacheDir = Path.Combine(tempRoot, "AnalysisCache");
                var metaDir = Path.Combine(gameRoot, "Exilium_Data", "il2cpp_data", "Metadata");
                Directory.CreateDirectory(metaDir);
                File.WriteAllBytes(Path.Combine(gameRoot, "GameAssembly.dll"), new byte[32]);
                WriteIl2CppMetadataHeader(
                    Path.Combine(metaDir, "global-metadata.dat"),
                    ProjectRuntimeDetector.Il2CppMetadataSanity,
                    version: 16384,
                    totalLength: 16384);

                var service = new ProjectAnalysisService(cacheDir);
                var result = await service.AnalyzeProjectAsync(gameRoot);

                Assert.Equal(ProjectAnalysisStatus.Ready, result.Report.Status);
                Assert.Equal(UnityScriptingBackend.Il2Cpp, result.Report.ScriptingBackend);
                Assert.True(result.Report.HasGlobalMetadata);
                Assert.True(result.Report.Il2CppMetadataObfuscated);
                Assert.Null(result.Report.Il2CppMetadataVersion);
                Assert.NotEmpty(result.Report.Warnings);
                Assert.True(result.Report.DecryptionHintCount >= 1);
                Assert.Contains(result.DecryptionHints.Hints, h => h.Provider == "Il2CppGlobalMetadata");
                Assert.Contains("obfuscated", result.Report.BuildRuntimeBadge(), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectRuntimeDetector_DetectsIl2CppMonoAndUnknownBackendsAndUnityVersions()
        {
            var tempRoot = CreateTempDirectory("backend_detection");
            try
            {
                var detector = new ProjectRuntimeDetector();

                // 1. IL2CPP project with SerializedFile v22 header (Unity 6)
                var il2cppRoot = Path.Combine(tempRoot, "Il2CppGame");
                var il2cppData = Path.Combine(il2cppRoot, "Il2CppGame_Data");
                Directory.CreateDirectory(Path.Combine(il2cppData, "il2cpp_data", "Metadata"));
                File.WriteAllBytes(Path.Combine(il2cppRoot, "GameAssembly.dll"), new byte[128]);
                WriteIl2CppMetadataHeader(Path.Combine(il2cppData, "il2cpp_data", "Metadata", "global-metadata.dat"), ProjectRuntimeDetector.Il2CppMetadataSanity, 39, 128);
                WriteSerializedFileVersionHeader(Path.Combine(il2cppData, "globalgamemanagers"), formatVersion: 22, unityVersion: "6000.3.21f1");

                var il2cpp = detector.Detect(il2cppRoot);
                Assert.Equal(UnityScriptingBackend.Il2Cpp, il2cpp.ScriptingBackend);
                Assert.True(il2cpp.HasGameAssembly);
                Assert.True(il2cpp.HasGlobalMetadata);
                Assert.Equal(39, il2cpp.Il2CppMetadataVersion);
                Assert.False(il2cpp.Il2CppMetadataObfuscated);
                Assert.False(il2cpp.HasManagedAssemblies);
                Assert.Equal("6000.3.21f1", il2cpp.UnityVersion);

                // 2. Mono project with UnityFS data.unity3d header
                var monoRoot = Path.Combine(tempRoot, "MonoGame");
                var monoData = Path.Combine(monoRoot, "MonoGame_Data");
                var managedDir = Path.Combine(monoData, "Managed");
                Directory.CreateDirectory(managedDir);
                File.WriteAllBytes(Path.Combine(managedDir, "Assembly-CSharp.dll"), new byte[64]);
                File.WriteAllBytes(Path.Combine(managedDir, "mscorlib.dll"), new byte[64]);
                File.WriteAllBytes(Path.Combine(managedDir, "UnityEngine.CoreModule.dll"), new byte[64]);
                WriteUnityFsVersionHeader(Path.Combine(monoData, "data.unity3d"), "5.x.x", "2022.3.56f1");

                var mono = detector.Detect(monoRoot);
                Assert.Equal(UnityScriptingBackend.Mono, mono.ScriptingBackend);
                Assert.False(mono.HasGameAssembly);
                Assert.False(mono.HasGlobalMetadata);
                Assert.True(mono.HasManagedAssemblies);
                Assert.Equal(3, mono.ManagedAssemblyCount);
                Assert.Equal("2022.3.56f1", mono.UnityVersion);

                // 3. Unknown empty project
                var unknownRoot = Path.Combine(tempRoot, "EmptyFolder");
                Directory.CreateDirectory(unknownRoot);
                var unknown = detector.Detect(unknownRoot);
                Assert.Equal(UnityScriptingBackend.Unknown, unknown.ScriptingBackend);
                Assert.False(unknown.HasGameAssembly);
                Assert.False(unknown.HasGlobalMetadata);
                Assert.False(unknown.HasManagedAssemblies);
                Assert.Equal(string.Empty, unknown.UnityVersion);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AddressablesCatalogAnalyzer_ParsesJsonBinaryAndMultipleCatalogs_AndEmitsDecryptionHints()
        {
            var tempRoot = CreateTempDirectory("addressables_analyzer");
            try
            {
                var aaDir = Path.Combine(tempRoot, "Game_Data", "StreamingAssets", "aa");
                var windowsDir = Path.Combine(aaDir, "Windows");
                var embedDir = Path.Combine(windowsDir, "Embed");
                Directory.CreateDirectory(embedDir);

                // Create local bundle with AssetStudio "Encryption" magic header
                var encryptedBundlePath = Path.Combine(embedDir, "hero_encrypted.bundle");
                var bundlePayload = Encoding.ASCII.GetBytes("Encryption").Concat(new byte[32]).ToArray();
                File.WriteAllBytes(encryptedBundlePath, bundlePayload);

                // 1. JSON catalog with standard + custom encrypted provider, local + remote bundles, and m_ExtraDataString
                var jsonCatalogPath = Path.Combine(windowsDir, "catalog.json");
                File.WriteAllText(jsonCatalogPath, BuildSyntheticCatalogJson(
                    encryptedProvider: "MyGame.Security.EncryptedAssetBundleProvider",
                    bundleFileName: "hero_encrypted.bundle",
                    remoteUrl: "https://cdn.example.com/bundles/dlc_pack.bundle",
                    bundleName: "hero_assets_all",
                    hash: "a1b2c3d4e5f60708090a0b0c0d0e0f10",
                    crc: 987654321u), Encoding.UTF8);

                // 2. Second catalog in parent aa/ directory (like Eternights duplicate catalogs)
                var parentCatalogPath = Path.Combine(aaDir, "catalog_extra.json");
                File.WriteAllText(parentCatalogPath, """
                {
                  "m_LocatorId": "AddressablesMainContentCatalog",
                  "m_ProviderIds": [
                    "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider"
                  ],
                  "m_InternalIds": [
                    "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/ui_shared.bundle"
                  ]
                }
                """, Encoding.UTF8);

                // 3. Binary Addressables 2.x catalog.bin (like ZENONIA 1 / Unity 6)
                var binCatalogPath = Path.Combine(aaDir, "catalog.bin");
                var binBytes = Encoding.ASCII.GetBytes(
                    "ADDR2\0\0\0UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider\0" +
                    "CustomStudio.XorBundleProvider\0" +
                    "StandaloneWindows64/playerani_assets_all_3b399015.bundle\0");
                File.WriteAllBytes(binCatalogPath, binBytes);

                var detector = new ProjectRuntimeDetector();
                var detection = detector.Detect(tempRoot);
                Assert.True(detection.HasAddressables);
                Assert.Equal(3, detection.AddressablesCatalogPaths.Count);

                var warnings = new List<string>();
                var analyzer = new AddressablesCatalogAnalyzer();
                var (index, hints) = analyzer.AnalyzeCatalogs(tempRoot, detection.AddressablesCatalogPaths, warnings);

                Assert.Empty(warnings);
                Assert.Equal(3, index.Catalogs.Count);
                Assert.Contains(index.Catalogs, c => c.Format == "json" && c.LocatorId == "AddressablesMainContentCatalog");
                Assert.Contains(index.Catalogs, c => c.Format == "bin" && c.BundleCount == 1);

                Assert.True(index.TotalBundleCount >= 4);
                Assert.True(index.LocalBundleCount >= 3);
                Assert.Equal(1, index.RemoteBundleCount);
                Assert.Contains(index.Bundles, b => b.BundleFileName == "hero_encrypted.bundle"
                    && b.BundleName == "hero_assets_all"
                    && b.Hash == "a1b2c3d4e5f60708090a0b0c0d0e0f10"
                    && b.Crc == "987654321"
                    && b.UsesNonStandardProvider);
                Assert.Contains(index.Bundles, b => b.BundleFileName == "dlc_pack.bundle" && b.IsRemote);
                Assert.Contains(index.Bundles, b => b.BundleFileName == "playerani_assets_all_3b399015.bundle");

                // Decryption hints emitted for custom encrypted provider, binary XorBundleProvider, and "Encryption" magic header
                Assert.Contains(hints.Hints, h => h.Provider == "MyGame.Security.EncryptedAssetBundleProvider" && h.Confidence >= 0.90);
                Assert.Contains(hints.Hints, h => h.Provider == "CustomStudio.XorBundleProvider" && h.Confidence >= 0.90);
                Assert.Contains(hints.Hints, h => h.Bundle == "hero_encrypted.bundle" && h.Reason.Contains("Encryption", StringComparison.OrdinalIgnoreCase) && h.Confidence >= 0.95);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Il2CppDumperArtifactImporter_StreamsScriptJsonDumpCsAndIl2CppHeader()
        {
            var tempRoot = CreateTempDirectory("il2cpp_dumper_import");
            try
            {
                var scriptJsonPath = Path.Combine(tempRoot, "script.json");
                File.WriteAllText(scriptJsonPath, """
                {
                  "ScriptMethod": [
                    { "Address": 4096, "Name": "EnemyController$$TakeDamage", "Signature": "void EnemyController__TakeDamage(void* __this, int dmg)", "TypeSignature": "vii" },
                    { "Address": 8192, "Name": "PlayerRig$$PlayIdle", "Signature": "void PlayerRig__PlayIdle(void* __this)", "TypeSignature": "vi" }
                  ],
                  "ScriptMetadata": [
                    { "Address": 16384, "Name": "EnemyController_TypeInfo", "Signature": "EnemyController_c*" }
                  ],
                  "ScriptMetadataMethod": [
                    { "Address": 20480, "Name": "Method$List<Item>.Add()", "MethodAddress": 24576 }
                  ],
                  "ScriptString": [
                    { "Address": 32768, "Value": "HeroPrefab" },
                    { "Address": 32800, "Value": "BossArena" }
                  ],
                  "Addresses": [ 4096, 8192, 16384 ]
                }
                """, Encoding.UTF8);

                var dumpCsPath = Path.Combine(tempRoot, "dump.cs");
                File.WriteAllText(dumpCsPath, """
                // Namespace: MyGame.Combat
                public class EnemyController : MonoBehaviour // TypeDefIndex: 4812
                {
                	// Fields
                	public int health; // 0x18
                	private float moveSpeed; // 0x1C
                	[SerializeField]
                	private string enemyId; // 0x20

                	// Properties
                	public bool IsAlive { get; }

                	// Methods
                	public void TakeDamage(int amount) { }
                }

                // Namespace: MyGame.Config
                public class WeaponConfig : ScriptableObject // TypeDefIndex: 4813
                {
                	// Fields
                	public int damage; // 0x18
                }

                // Namespace:
                internal struct DamagePacket // TypeDefIndex: 4814
                {
                	// Fields
                	public int rawValue; // 0x0
                	public bool isCrit; // 0x4
                }
                """, Encoding.UTF8);

                var headerPath = Path.Combine(tempRoot, "il2cpp.h");
                File.WriteAllText(headerPath, """
                typedef uintptr_t il2cpp_array_size_t;
                typedef int32_t il2cpp_array_lower_bound_t;
                struct Il2CppObject {
                    void* klass;
                    void* monitor;
                };
                struct EnemyController_o {
                    Il2CppObject _base;
                    int32_t health;
                };
                """, Encoding.UTF8);

                var warnings = new List<string>();
                var importer = new Il2CppDumperArtifactImporter();
                var index = importer.ImportArtifacts(tempRoot, new[] { scriptJsonPath, dumpCsPath, headerPath }, warnings);

                Assert.Empty(warnings);
                Assert.Equal(3, index.SourceArtifacts.Count);
                Assert.Equal(2, index.ScriptMethodCount);
                Assert.Equal(1, index.ScriptMetadataCount);
                Assert.Equal(1, index.ScriptMetadataMethodCount);
                Assert.Equal(2, index.ScriptStringCount);
                Assert.Equal(3, index.AddressCount);

                Assert.Equal(3, index.DumpTypeCount);
                Assert.Equal(1, index.MonoBehaviourCount);
                Assert.Equal(1, index.ScriptableObjectCount);
                Assert.Equal(2, index.HeaderStructCount);
                Assert.Equal(2, index.HeaderTypedefCount);

                var enemy = Assert.Single(index.Types, t => t.Name == "EnemyController");
                Assert.Equal("MyGame.Combat", enemy.Namespace);
                Assert.Equal("MonoBehaviour", enemy.BaseType);
                Assert.Equal(4812, enemy.TypeDefIndex);
                Assert.Equal(3, enemy.FieldCount);
                Assert.True(enemy.IsMonoBehaviour);
                Assert.False(enemy.IsScriptableObject);

                var weapon = Assert.Single(index.Types, t => t.Name == "WeaponConfig");
                Assert.Equal("MyGame.Config", weapon.Namespace);
                Assert.Equal(1, weapon.FieldCount);
                Assert.True(weapon.IsScriptableObject);

                Assert.Contains(index.SampleScriptSymbols, s => s.Kind == "ScriptMethod" && s.Name == "EnemyController$$TakeDamage" && s.Address == 4096UL);
                Assert.Equal(7, index.ComputeTotalScriptCount());

                // Also verify multi-chunk streaming across > 64 KB script.json with UTF-8 BOM and bounded sample cap
                var bigScriptPath = Path.Combine(tempRoot, "big_script.json");
                using (var sw = new StreamWriter(bigScriptPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                {
                    sw.Write("{\"ScriptMethod\":[");
                    for (var i = 0; i < 1200; i++)
                    {
                        if (i > 0)
                        {
                            sw.Write(',');
                        }

                        sw.Write($"{{\"Address\":{4096 + i * 16},\"Name\":\"GameNamespace.Module{i}$$ExecuteStep\",\"Signature\":\"void f()\"}}");
                    }

                    sw.Write("]}");
                }

                var bigIndex = new Il2CppScriptIndex();
                var bigWarnings = new List<string>();
                var boundedImporter = new Il2CppDumperArtifactImporter(maxIndexedTypes: 100, maxSampleSymbols: 25);
                boundedImporter.ImportScriptJson(bigScriptPath, bigIndex, bigWarnings);

                Assert.Empty(bigWarnings);
                Assert.Equal(1200, bigIndex.ScriptMethodCount);
                Assert.Equal(25, bigIndex.SampleScriptSymbols.Count);
                Assert.True(bigIndex.EntriesTruncated);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task ProjectAnalysisService_CachesInvalidatesPersistsAtomicallyTracksStatusAndHandlesMissingRoot()
        {
            var tempRoot = CreateTempDirectory("analysis_service_e2e");
            try
            {
                var gameRoot = Path.Combine(tempRoot, "SampleUnityGame");
                var dataDir = Path.Combine(gameRoot, "SampleUnityGame_Data");
                var metaDir = Path.Combine(dataDir, "il2cpp_data", "Metadata");
                var aaDir = Path.Combine(dataDir, "StreamingAssets", "aa");
                var cacheBaseDir = Path.Combine(tempRoot, "AnalysisCache");
                Directory.CreateDirectory(metaDir);
                Directory.CreateDirectory(aaDir);

                File.WriteAllBytes(Path.Combine(gameRoot, "GameAssembly.dll"), new byte[256]);
                WriteIl2CppMetadataHeader(Path.Combine(metaDir, "global-metadata.dat"), ProjectRuntimeDetector.Il2CppMetadataSanity, 29, 256);
                WriteSerializedFileVersionHeader(Path.Combine(dataDir, "globalgamemanagers"), 22, "2022.3.10f1");

                var catalogPath = Path.Combine(aaDir, "catalog.json");
                File.WriteAllText(catalogPath, """
                {
                  "m_LocatorId": "AddressablesMainContentCatalog",
                  "m_ProviderIds": [ "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider" ],
                  "m_InternalIds": [ "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/pack_a.bundle" ]
                }
                """, Encoding.UTF8);

                File.WriteAllText(Path.Combine(gameRoot, "dump.cs"), """
                // Namespace: Demo
                public class HeroBehaviour : MonoBehaviour // TypeDefIndex: 10
                {
                    // Fields
                    public int hp; // 0x10
                }
                """, Encoding.UTF8);

                var service = new ProjectAnalysisService(cacheBaseDir);
                var observedStatuses = new List<ProjectAnalysisStatus>();
                service.AnalysisUpdated += (_, report) => observedStatuses.Add(report.Status);

                // 1. First run -> Pending -> Running -> Ready, persists all 4 JSON files atomically
                var first = await service.AnalyzeProjectAsync(gameRoot);
                Assert.False(first.FromCache);
                Assert.Equal(ProjectAnalysisStatus.Ready, first.Report.Status);
                Assert.Equal(UnityScriptingBackend.Il2Cpp, first.Report.ScriptingBackend);
                Assert.Equal(29, first.Report.Il2CppMetadataVersion);
                Assert.Equal("2022.3.10f1", first.Report.UnityVersion);
                Assert.True(first.Report.HasAddressables);
                Assert.Equal(1, first.Report.AddressablesBundleCount);
                Assert.True(first.Report.Il2CppDumperArtifactsDetected);
                Assert.Equal(1, first.Report.Il2CppScriptCount);
                Assert.False(first.Report.ExternalIl2CppDumperExecuted);
                Assert.Equal(new[] { ProjectAnalysisStatus.Pending, ProjectAnalysisStatus.Running, ProjectAnalysisStatus.Ready }, observedStatuses);

                var projectCacheDir = service.GetProjectCacheDirectory(gameRoot);
                Assert.True(File.Exists(Path.Combine(projectCacheDir, ProjectAnalysisService.AnalysisFileName)));
                Assert.True(File.Exists(Path.Combine(projectCacheDir, ProjectAnalysisService.Il2CppScriptIndexFileName)));
                Assert.True(File.Exists(Path.Combine(projectCacheDir, ProjectAnalysisService.AddressablesIndexFileName)));
                Assert.True(File.Exists(Path.Combine(projectCacheDir, ProjectAnalysisService.DecryptionHintsFileName)));
                Assert.Empty(Directory.GetFiles(projectCacheDir, "*.tmp"));

                // 2. Second run on unchanged project -> instant cache hit
                observedStatuses.Clear();
                var second = await service.AnalyzeProjectAsync(gameRoot);
                Assert.True(second.FromCache);
                Assert.Equal(first.Report.Signature, second.Report.Signature);
                Assert.Equal(ProjectAnalysisStatus.Ready, second.Report.Status);
                Assert.Equal(new[] { ProjectAnalysisStatus.Pending, ProjectAnalysisStatus.Ready }, observedStatuses);

                // 3. Modify catalog.json -> signature changes -> cache invalidates automatically
                File.WriteAllText(catalogPath, """
                {
                  "m_LocatorId": "AddressablesMainContentCatalog",
                  "m_ProviderIds": [
                    "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
                    "Custom.EncryptedAssetBundleProvider"
                  ],
                  "m_InternalIds": [
                    "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/pack_a.bundle",
                    "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/pack_b.bundle"
                  ]
                }
                """, Encoding.UTF8);
                File.SetLastWriteTimeUtc(catalogPath, DateTime.UtcNow.AddSeconds(5));

                observedStatuses.Clear();
                var third = await service.AnalyzeProjectAsync(gameRoot);
                Assert.False(third.FromCache);
                Assert.NotEqual(first.Report.Signature, third.Report.Signature);
                Assert.Equal(2, third.Report.AddressablesBundleCount);
                Assert.True(third.Report.DecryptionHintCount >= 1);
                Assert.Equal(new[] { ProjectAnalysisStatus.Pending, ProjectAnalysisStatus.Running, ProjectAnalysisStatus.Ready }, observedStatuses);

                // 4. Non-existent root -> Failed status with clear error message (does not throw)
                observedStatuses.Clear();
                var missingPath = Path.Combine(tempRoot, "DoesNotExist");
                var failed = await service.AnalyzeProjectAsync(missingPath);
                Assert.False(failed.FromCache);
                Assert.Equal(ProjectAnalysisStatus.Failed, failed.Report.Status);
                Assert.False(string.IsNullOrWhiteSpace(failed.Report.ErrorMessage));
                Assert.Equal(new[] { ProjectAnalysisStatus.Failed }, observedStatuses);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectManagerStore_PersistsAnalysisPreferencesAndSummary_WithBackwardsCompatibilityAndBannerFormatting()
        {
            var tempRoot = CreateTempDirectory("store_analysis_roundtrip");
            try
            {
                var storeDir = Path.Combine(tempRoot, "Store");
                var gameRoot = Path.Combine(tempRoot, "GameRoot");
                Directory.CreateDirectory(gameRoot);

                var store = new ProjectManagerStore(storeDir);

                // 1. Default project starts with AllowAutoIl2CppDumper == false and Pending status
                var project = new ManagedProject
                {
                    Name = "Analysis Test Game",
                    ProjectRoot = gameRoot
                };
                store.SaveProject(project);

                var loadedDefault = store.GetProject(project.Id);
                Assert.NotNull(loadedDefault);
                Assert.False(loadedDefault!.AllowAutoIl2CppDumper);
                Assert.Null(loadedDefault.Il2CppDumperPath);
                Assert.Equal(ProjectAnalysisStatus.Pending, loadedDefault.AnalysisSummary.Status);

                // 2. Persist preferences + analysis report and verify round-trip
                store.SaveProjectAnalysisPreferences(project.Id, allowAutoIl2CppDumper: true, il2CppDumperPath: @"C:\Tools\Il2CppDumper.exe");

                var readyReport = new ProjectAnalysisReport
                {
                    ProjectRoot = gameRoot,
                    ProjectHash = ProjectRuntimeDetector.ComputeProjectHash(gameRoot),
                    Status = ProjectAnalysisStatus.Ready,
                    AnalyzedAtUtc = DateTime.UtcNow,
                    UnityVersion = "2022.3.39f1",
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                    Il2CppMetadataVersion = 31,
                    HasAddressables = true,
                    Il2CppScriptCount = 420,
                    AddressablesBundleCount = 1960,
                    DecryptionHintCount = 2
                };
                store.SaveProjectAnalysisReport(project.Id, readyReport);

                var reloaded = store.GetProject(project.Id);
                Assert.NotNull(reloaded);
                Assert.True(reloaded!.AllowAutoIl2CppDumper);
                Assert.Equal(@"C:\Tools\Il2CppDumper.exe", reloaded.Il2CppDumperPath);
                Assert.Equal(ProjectAnalysisStatus.Ready, reloaded.AnalysisSummary.Status);
                Assert.Equal("2022.3.39f1", reloaded.AnalysisSummary.UnityVersion);
                Assert.Equal(UnityScriptingBackend.Il2Cpp, reloaded.AnalysisSummary.ScriptingBackend);
                Assert.Equal(31, reloaded.AnalysisSummary.Il2CppMetadataVersion);
                Assert.True(reloaded.AnalysisSummary.HasAddressables);
                Assert.Equal(420, reloaded.AnalysisSummary.Il2CppScriptCount);
                Assert.Equal(1960, reloaded.AnalysisSummary.AddressablesBundleCount);
                Assert.Equal(2, reloaded.AnalysisSummary.DecryptionHintCount);
                Assert.Contains("IL2CPP v31", reloaded.AnalysisSummary.RuntimeBadge);
                Assert.Contains("Addressables", reloaded.AnalysisSummary.RuntimeBadge);

                // 3. Backwards compatibility when legacy/partial JSON exists in ManagedProjectSettings
                using (var conn = new SqliteConnection($"Data Source={store.DatabasePath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE ManagedProjectSettings SET Value = '{}' WHERE ProjectId = @id AND Key = 'project_analysis_json'";
                    cmd.Parameters.AddWithValue("@id", project.Id);
                    cmd.ExecuteNonQuery();
                }

                var legacyLoaded = store.GetProject(project.Id);
                Assert.NotNull(legacyLoaded);
                Assert.False(legacyLoaded!.AllowAutoIl2CppDumper);
                Assert.Null(legacyLoaded.Il2CppDumperPath);
                Assert.Equal(ProjectAnalysisStatus.Pending, legacyLoaded.AnalysisSummary.Status);

                // 4. MainWindow banner formatting across all statuses
                Assert.Equal("Analysis: Pending", MainWindow.FormatProjectAnalysisBannerText(new ProjectAnalysisReport { Status = ProjectAnalysisStatus.Pending }));
                Assert.Equal("Analysis: Running...", MainWindow.FormatProjectAnalysisBannerText(new ProjectAnalysisReport { Status = ProjectAnalysisStatus.Running }));
                Assert.Equal("Analysis: Failed (root missing)", MainWindow.FormatProjectAnalysisBannerText(new ProjectAnalysisReport { Status = ProjectAnalysisStatus.Failed, ErrorMessage = "root missing" }));

                var readyBanner = MainWindow.FormatProjectAnalysisBannerText(readyReport);
                Assert.Contains("Analysis: Ready", readyBanner);
                Assert.Contains("IL2CPP v31", readyBanner);
                Assert.Contains("Addressables", readyBanner);
                Assert.Contains("420 scripts", readyBanner);
                Assert.Contains("1,960 bundles", readyBanner);
                Assert.Contains("2 decryption hints", readyBanner);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectRuntimeDetector_DetectsPlainDataDirectory_FromRootAndDirectDataFolder()
        {
            var tempRoot = CreateTempDirectory("PlainDataDir");
            try
            {
                var dataDir = Path.Combine(tempRoot, "Data");
                var managedDir = Path.Combine(dataDir, "Managed");
                Directory.CreateDirectory(managedDir);
                WriteSerializedFileVersionHeader(Path.Combine(dataDir, "globalgamemanagers"), formatVersion: 22, unityVersion: "6000.0.61f1");
                File.WriteAllBytes(Path.Combine(managedDir, "Assembly-CSharp.dll"), new byte[64]);

                var detector = new ProjectRuntimeDetector();
                var fromRoot = detector.Detect(tempRoot);
                Assert.Equal(UnityScriptingBackend.Mono, fromRoot.ScriptingBackend);
                Assert.Equal("6000.0.61f1", fromRoot.UnityVersion);
                Assert.True(fromRoot.HasManagedAssemblies);
                Assert.Equal(1, fromRoot.ManagedAssemblyCount);

                var fromDataDir = detector.Detect(dataDir);
                Assert.Equal(UnityScriptingBackend.Mono, fromDataDir.ScriptingBackend);
                Assert.Equal("6000.0.61f1", fromDataDir.UnityVersion);
                Assert.True(fromDataDir.HasManagedAssemblies);
                Assert.Equal(1, fromDataDir.ManagedAssemblyCount);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void WriteIl2CppMetadataHeader(string path, uint magic, int version, int totalLength)
        {
            var data = new byte[Math.Max(8, totalLength)];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), magic);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), version);
            File.WriteAllBytes(path, data);
        }

        private static void WriteSerializedFileVersionHeader(string path, uint formatVersion, string unityVersion)
        {
            var data = new byte[128];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), formatVersion);
            var versionOffset = formatVersion >= 22 ? 48 : (formatVersion >= 9 ? 20 : 16);
            var versionBytes = Encoding.ASCII.GetBytes(unityVersion);
            versionBytes.CopyTo(data.AsSpan(versionOffset));
            data[versionOffset + versionBytes.Length] = 0;
            File.WriteAllBytes(path, data);
        }

        private static void WriteUnityFsVersionHeader(string path, string playerVersion, string engineRevision)
        {
            using var ms = new MemoryStream();
            void WriteNullString(string s)
            {
                var bytes = Encoding.ASCII.GetBytes(s);
                ms.Write(bytes, 0, bytes.Length);
                ms.WriteByte(0);
            }

            WriteNullString("UnityFS");
            Span<byte> ver = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ver, 8u);
            ms.Write(ver);
            WriteNullString(playerVersion);
            WriteNullString(engineRevision);
            ms.Write(new byte[32]);
            File.WriteAllBytes(path, ms.ToArray());
        }

        private static string BuildSyntheticCatalogJson(
            string encryptedProvider,
            string bundleFileName,
            string remoteUrl,
            string bundleName,
            string hash,
            uint crc)
        {
            // Build m_ExtraDataString containing a type-7 JsonObject for AssetBundleRequestOptions
            var optionsJson = JsonSerializer.Serialize(new
            {
                m_Hash = hash,
                m_Crc = crc,
                m_BundleName = bundleName
            });
            byte[] extraBytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write((byte)7); // JsonObject
                var asmBytes = Encoding.ASCII.GetBytes("Unity.ResourceManager");
                bw.Write((byte)asmBytes.Length);
                bw.Write(asmBytes);
                var clsBytes = Encoding.ASCII.GetBytes("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions");
                bw.Write((byte)clsBytes.Length);
                bw.Write(clsBytes);
                var jsonBytes = Encoding.Unicode.GetBytes(optionsJson);
                bw.Write(jsonBytes.Length);
                bw.Write(jsonBytes);
                extraBytes = ms.ToArray();
            }

            // Build m_EntryDataString with 2 entries (28 bytes each): entry 0 -> internalId 0, provider 1 (encrypted), dataIdx 0
            byte[] entryBytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(2); // entryCount

                // Entry 0: local encrypted bundle
                bw.Write(0); // internalIdIdx
                bw.Write(1); // providerIdx -> encryptedProvider
                bw.Write(-1); // dependencyKeyIdx
                bw.Write(0); // depHash
                bw.Write(0); // dataIdx -> extraBytes offset 0
                bw.Write(0); // primaryKey
                bw.Write(0); // resourceType

                // Entry 1: remote standard bundle
                bw.Write(1); // internalIdIdx
                bw.Write(0); // providerIdx -> standard AssetBundleProvider
                bw.Write(-1);
                bw.Write(0);
                bw.Write(-1); // no extra data
                bw.Write(1);
                bw.Write(0);

                entryBytes = ms.ToArray();
            }

            var catalog = new
            {
                m_LocatorId = "AddressablesMainContentCatalog",
                m_ProviderIds = new[]
                {
                    "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
                    encryptedProvider
                },
                m_InternalIdPrefixes = new[]
                {
                    "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/Embed/"
                },
                m_InternalIds = new[]
                {
                    "0#" + bundleFileName,
                    remoteUrl
                },
                m_KeyDataString = Convert.ToBase64String(new byte[] { 0, 0, 0, 0 }),
                m_BucketDataString = Convert.ToBase64String(new byte[] { 0, 0, 0, 0 }),
                m_EntryDataString = Convert.ToBase64String(entryBytes),
                m_ExtraDataString = Convert.ToBase64String(extraBytes)
            };

            return JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), "AssetStudioTests_" + prefix + "_" + Guid.NewGuid().ToString("N"));
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
    }
}
