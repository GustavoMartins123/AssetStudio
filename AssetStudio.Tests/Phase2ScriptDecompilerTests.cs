using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Media;
using Xunit;

namespace AssetStudio.Tests
{
    public class Phase2ScriptDecompilerTests
    {
        [Fact]
        public void MonoDecompilerService_DecompilesRealAssemblyType_WithoutLockingSourceFile()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var managedDir = Path.Combine(tempRoot, "SampleGame_Data", "Managed");
                Directory.CreateDirectory(managedDir);

                var sourceDll = typeof(MonoScript).Assembly.Location;
                Assert.True(File.Exists(sourceDll), $"Expected AssetStudio assembly at {sourceDll}");
                var copiedDll = Path.Combine(managedDir, "AssetStudio.dll");
                File.Copy(sourceDll, copiedDll, overwrite: true);

                using var decompiler = new MonoDecompilerService();
                var request = new ScriptLookupRequest
                {
                    AssemblyName = "AssetStudio.dll",
                    Namespace = "AssetStudio",
                    ClassName = "MonoScript"
                };

                var result = decompiler.TryDecompile(request, new[] { managedDir });
                Assert.NotNull(result);
                Assert.True(result!.Success);
                Assert.Equal(ScriptSourceKind.MonoAssemblyDecompiled, result.SourceKind);
                Assert.Equal("Mono (ILSpy)", result.BackendBadge);
                Assert.Equal("AssetStudio.MonoScript", result.Title);
                Assert.Contains("class MonoScript", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("m_ClassName", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("m_Namespace", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("m_AssemblyName", result.SourceCode, StringComparison.Ordinal);

                // Second lookup hits cache cleanly.
                var cached = decompiler.TryDecompile(request, new[] { managedDir });
                Assert.NotNull(cached);
                Assert.True(cached!.Success);

                // PEStreamOptions.PrefetchEntireImage must not lock the DLL on Windows even while cached.
                File.Delete(copiedDll);
                Assert.False(File.Exists(copiedDll));

                // Missing class returns null without throwing.
                var missing = decompiler.TryDecompile(
                    new ScriptLookupRequest
                    {
                        AssemblyName = "AssetStudio.dll",
                        Namespace = "Missing.Namespace",
                        ClassName = "NonExistentScript"
                    },
                    new[] { managedDir });
                Assert.Null(missing);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Il2CppDumpScriptService_ExtractsExactTypeSpanByByteOffset_AndCorrelatesScriptJsonRvas()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var dumpCsPath = Path.Combine(tempRoot, "dump.cs");
                var scriptJsonPath = Path.Combine(tempRoot, "script.json");

                var dumpContent = """
// Image 0: Assembly-CSharp.dll - 0

// Namespace: Game.Core
public class UnrelatedFirst // TypeDefIndex: 100
{
    // Fields
    public int FirstValue; // 0x10
}

// Namespace: Game.Gameplay
[Serializable]
public class PlayerController : MonoBehaviour // TypeDefIndex: 4210
{
    // Fields
    public float MoveSpeed; // 0x18
    private string _bannerText; // 0x20 // comment with { brace }

    // Methods

    // RVA: 0x123450 Offset: 0x122850 VA: 0x180123450
    public void Update() { }

    // RVA: 0x1234A0 Offset: 0x1228A0 VA: 0x1801234A0
    public void Jump(float force) { }
}

// Namespace: Game.Gameplay
public class GenericInventory<T> // TypeDefIndex: 4211
{
    // Fields
    public T[] Items; // 0x10
}
""";
                // Write with UTF-8 BOM to verify BOM-aware byte offset slicing.
                File.WriteAllText(dumpCsPath, dumpContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                var scriptJson = """
{
  "ScriptMethod": [
    {
      "Address": 1193040,
      "Name": "Game.Gameplay.PlayerController$$Update",
      "Signature": "void Game_Gameplay_PlayerController__Update (Game_Gameplay_PlayerController_o* __this, const MethodInfo* method);",
      "TypeSignature": "vii"
    },
    {
      "Address": 1193200,
      "Name": "Game.Gameplay.PlayerController$$TakeDamage",
      "Signature": "void Game_Gameplay_PlayerController__TakeDamage (Game_Gameplay_PlayerController_o* __this, int32_t amount, const MethodInfo* method);",
      "TypeSignature": "viii"
    },
    {
      "Address": 2000000,
      "Name": "Game.Core.UnrelatedFirst$$Reset",
      "Signature": "void Game_Core_UnrelatedFirst__Reset (void* __this);",
      "TypeSignature": "vi"
    }
  ],
  "ScriptString": [
    {
      "Address": 3145728,
      "Value": "Player jumped!"
    }
  ],
  "ScriptMetadata": [
    {
      "Address": 4194304,
      "Name": "Game.Gameplay.PlayerController_TypeInfo"
    }
  ]
}
""";
                File.WriteAllText(scriptJsonPath, scriptJson, Encoding.UTF8);

                var service = new Il2CppDumpScriptService();
                var result = service.TryExtractFromDumpCs(
                    dumpCsPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Game.Gameplay",
                        ClassName = "PlayerController"
                    },
                    scriptJsonPath);

                Assert.NotNull(result);
                Assert.True(result!.Success);
                Assert.Equal(ScriptSourceKind.Il2CppDumpCs, result.SourceKind);
                Assert.Equal("IL2CPP (dump.cs)", result.BackendBadge);
                Assert.Equal("Game.Gameplay.PlayerController", result.Title);
                Assert.Equal(4210, result.TypeDefIndex);

                // Extracted slice must include attributes, fields, methods, and correlated script.json RVAs
                Assert.Contains("[Serializable]", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public float MoveSpeed;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public void Jump(float force)", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Game.Gameplay.PlayerController$$TakeDamage", result.SourceCode, StringComparison.Ordinal);

                // Must NOT bleed into adjacent types
                Assert.DoesNotContain("UnrelatedFirst", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("GenericInventory", result.SourceCode, StringComparison.Ordinal);

                // Merged symbols contain Update (0x123450), Jump (0x1234A0), and TakeDamage (0x1234F0 = 1193200)
                Assert.Equal(3, result.MethodSymbols.Count);
                Assert.Contains(result.MethodSymbols, m => m.Address == 0x123450);
                Assert.Contains(result.MethodSymbols, m => m.Address == 0x1234A0);
                Assert.Contains(result.MethodSymbols, m => m.Address == 1193200);

                // Generic backtick name (GenericInventory`1) matches GenericInventory<T>
                var genericResult = service.TryExtractFromDumpCs(
                    dumpCsPath,
                    new ScriptLookupRequest
                    {
                        Namespace = "Game.Gameplay",
                        ClassName = "GenericInventory`1"
                    });
                Assert.NotNull(genericResult);
                Assert.Equal(4211, genericResult!.TypeDefIndex);
                Assert.Contains("public T[] Items;", genericResult.SourceCode, StringComparison.Ordinal);

                // Script.json-only outline works when dump.cs is not used
                var outline = service.TryBuildScriptJsonOutline(
                    scriptJsonPath,
                    new ScriptLookupRequest
                    {
                        Namespace = "Game.Gameplay",
                        ClassName = "PlayerController"
                    });
                Assert.NotNull(outline);
                Assert.Equal(ScriptSourceKind.Il2CppScriptJsonOutline, outline!.SourceKind);
                Assert.Equal(2, outline.MethodSymbols.Count);
                Assert.Contains("0x123450", outline.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ScriptViewerService_ResolvesDummyDllAndUnavailableFallbacksCleanly()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                // 1) Empty IL2CPP project -> Unavailable with helpful guidance
                using var viewer = new ScriptViewerService();
                var unavailable = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Assembly-CSharp.dll",
                    Namespace = "Game",
                    ClassName = "HeroBehaviour",
                    ProjectRoot = tempRoot,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp
                });

                Assert.False(unavailable.Success);
                Assert.Equal(ScriptSourceKind.Unavailable, unavailable.SourceKind);
                Assert.Contains("Il2CppDumper", unavailable.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("class HeroBehaviour", unavailable.SourceCode, StringComparison.Ordinal);

                // 2) DummyDll folder (detected by folder name / Il2CppDummyDll.dll marker)
                var dumperDir = Path.Combine(tempRoot, "Il2CppDumperOut");
                var dummyDllDir = Path.Combine(dumperDir, "DummyDll");
                Directory.CreateDirectory(dummyDllDir);
                File.WriteAllBytes(Path.Combine(dummyDllDir, "Il2CppDummyDll.dll"), Array.Empty<byte>());
                File.Copy(typeof(MonoScript).Assembly.Location, Path.Combine(dummyDllDir, "AssetStudio.dll"), overwrite: true);

                var dummyResult = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "AssetStudio.dll",
                    Namespace = "AssetStudio",
                    ClassName = "MonoScript",
                    CustomScriptsDirectory = dumperDir,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp
                });

                Assert.True(dummyResult.Success);
                Assert.Equal(ScriptSourceKind.Il2CppDummyAssemblyDecompiled, dummyResult.SourceKind);
                Assert.Equal("IL2CPP (Dummy DLL)", dummyResult.BackendBadge);
                Assert.Contains("class MonoScript", dummyResult.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void GhidraExportService_ExportsSelectedClassAndFullProjectBundles_WithValidJsonAndPythonScript()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var scriptJsonPath = Path.Combine(tempRoot, "script.json");
                var headerPath = Path.Combine(tempRoot, "il2cpp.h");
                File.WriteAllText(headerPath, "typedef struct Il2CppObject { void* klass; void* monitor; } Il2CppObject;\n", Encoding.UTF8);
                File.WriteAllText(scriptJsonPath, """
{
  "ScriptMethod": [
    { "Address": 4096, "Name": "Hero$$Attack", "Signature": "void Hero__Attack(void* __this);" },
    { "Address": 8192, "Name": "Hero$$Defend", "Signature": "void Hero__Defend(void* __this);" }
  ],
  "ScriptString": [
    { "Address": 16384, "Value": "Critical Hit!" }
  ],
  "ScriptMetadata": [
    { "Address": 32768, "Name": "Hero_TypeInfo" }
  ]
}
""", Encoding.UTF8);

                var exporter = new GhidraExportService();

                // 1) Full Project export
                var fullOutDir = Path.Combine(tempRoot, "ghidra_full");
                var fullResult = exporter.Export(new GhidraExportOptions
                {
                    OutputDirectory = fullOutDir,
                    Scope = GhidraExportScope.FullProject,
                    BinaryName = "GameAssembly.dll",
                    BackendLabel = "IL2CPP (dump.cs)",
                    ClassTitle = "Game.Hero",
                    ScriptJsonPath = scriptJsonPath,
                    Il2CppHeaderPath = headerPath,
                    DecompiledSourceCode = "public class Hero { public void Attack() {} }",
                    SuggestedSourceFileName = "Hero.cs"
                });

                Assert.True(fullResult.Success);
                Assert.Equal(2, fullResult.MethodCount);
                Assert.Equal(1, fullResult.StringCount);
                Assert.Equal(1, fullResult.MetadataCount);
                Assert.True(File.Exists(fullResult.GhidraScriptPath));
                Assert.True(File.Exists(fullResult.SymbolsJsonPath));
                Assert.True(File.Exists(fullResult.HeaderOutputPath));
                Assert.True(File.Exists(fullResult.SourceOutputPath));
                Assert.True(File.Exists(fullResult.ReadmePath));

                using (var doc = JsonDocument.Parse(File.ReadAllText(fullResult.SymbolsJsonPath)))
                {
                    Assert.Equal("assetstudio-ghidra-v1", doc.RootElement.GetProperty("schemaVersion").GetString());
                    Assert.Equal("GameAssembly.dll", doc.RootElement.GetProperty("binaryName").GetString());
                    Assert.Equal(2, doc.RootElement.GetProperty("methods").GetArrayLength());
                    Assert.Equal("0x1000", doc.RootElement.GetProperty("methods")[0].GetProperty("rvaHex").GetString());
                    Assert.Equal(1, doc.RootElement.GetProperty("strings").GetArrayLength());
                    Assert.Equal(1, doc.RootElement.GetProperty("metadata").GetArrayLength());
                }

                var pyScript = File.ReadAllText(fullResult.GhidraScriptPath);
                Assert.Contains("currentProgram.getImageBase()", pyScript, StringComparison.Ordinal);
                Assert.Contains("assetstudio_symbols.json", pyScript, StringComparison.Ordinal);
                Assert.Contains("createFunction", pyScript, StringComparison.Ordinal);
                Assert.Contains("SourceType.USER_DEFINED", pyScript, StringComparison.Ordinal);
                Assert.Contains("monitor.isCancelled()", pyScript, StringComparison.Ordinal);

                // 2) Selected Class export
                var classOutDir = Path.Combine(tempRoot, "ghidra_class");
                var classResult = exporter.Export(new GhidraExportOptions
                {
                    OutputDirectory = classOutDir,
                    Scope = GhidraExportScope.SelectedClass,
                    BinaryName = "GameAssembly.dll",
                    BackendLabel = "IL2CPP (dump.cs)",
                    ClassTitle = "Game.Hero",
                    SelectedClassMethods = new[]
                    {
                        new ScriptMethodSymbol { ClassName = "Hero", MethodName = "Attack", FullName = "Hero$$Attack", Address = 0x1000, Signature = "void Attack()" },
                        new ScriptMethodSymbol { ClassName = "Hero", MethodName = "AttackDup", FullName = "Hero$$Attack", Address = 0x1000, Signature = "void Attack()" }
                    }
                });

                Assert.True(classResult.Success);
                Assert.Equal(1, classResult.MethodCount); // deduplicated by RVA
                Assert.Equal(0, classResult.StringCount);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ScriptCodeSearchHelper_CountsMatchesAndFiltersLinesWithContext()
        {
            var code = """
namespace Demo
{
    public class CombatSystem
    {
        public int MaxHealth;
        public float AttackPower;
        public void ApplyDamage(int dmg)
        {
            MaxHealth -= dmg;
        }
    }
}
""";
            var countOnly = ScriptCodeSearchHelper.ApplySearch(code, "MaxHealth", filterLinesOnly: false);
            Assert.Equal(2, countOnly.MatchCount);
            Assert.Equal(2, countOnly.MatchingLineCount);
            Assert.Equal("2 matches (2 lines)", countOnly.MatchSummary);
            Assert.Equal(code, countOnly.DisplayText);

            var filtered = ScriptCodeSearchHelper.ApplySearch(code, "MaxHealth", filterLinesOnly: true);
            Assert.Equal(2, filtered.MatchCount);
            Assert.Contains(">L0005:", filtered.DisplayText, StringComparison.Ordinal);
            Assert.Contains(">L0009:", filtered.DisplayText, StringComparison.Ordinal);
            Assert.Contains("...", filtered.DisplayText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PreviewSessionAndMainWindowViewModel_ScriptTabLifecycle_CommandsAndDoubleResetAreClean()
        {
            using var vm = new MainWindowViewModel();
            var session = vm.PreviewSession;

            Assert.Equal(RightPanelTab.Preview, session.ActiveRightTab);
            Assert.False(session.IsScriptTabActive);
            Assert.False(session.CanSaveOrCopyScript);
            Assert.False(vm.CopyScriptCodeCommand.CanExecute());
            Assert.False(vm.SaveScriptCodeCommand.CanExecute());

            // Begin two rapid script requests -> first becomes stale and is rejected
            var token1 = session.BeginScriptRequest(out var req1, "OldScript");
            var token2 = session.BeginScriptRequest(out var req2, "NewScript");
            Assert.True(token1.IsCancellationRequested);
            Assert.False(token2.IsCancellationRequested);

            var sampleResult = new ScriptDecompilationResult
            {
                Success = true,
                SourceKind = ScriptSourceKind.MonoAssemblyDecompiled,
                BackendBadge = "Mono (ILSpy)",
                Title = "Game.Player",
                AssemblyName = "Assembly-CSharp.dll",
                Namespace = "Game",
                ClassName = "Player",
                SourceCode = "public class Player\n{\n    public int Health;\n}",
                SummaryText = "Assembly-CSharp.dll • 4 lines • Mono (ILSpy)"
            };

            Assert.False(session.CompleteScriptRequest(req1, sampleResult));
            Assert.True(session.CompleteScriptRequest(req2, sampleResult));
            Assert.True(session.HasScriptSource);
            Assert.True(session.CanSaveOrCopyScript);
            Assert.True(vm.CopyScriptCodeCommand.CanExecute());
            Assert.True(vm.SaveScriptCodeCommand.CanExecute());
            Assert.Equal("Mono (ILSpy)", session.ScriptBackendBadge);
            Assert.True(session.HasScriptBackendBadge);

            // Search updates match summary and filter-only display
            session.ScriptSearchText = "Health";
            Assert.Equal("1 match (1 line)", session.ScriptSearchMatchSummary);
            session.IsScriptSearchFilterOnly = true;
            Assert.Contains(">L0003:", session.ScriptSourceCode, StringComparison.Ordinal);

            // Word wrap defaults to true, toggles cleanly, and converter maps to Avalonia TextWrapping
            Assert.True(session.IsScriptWordWrapEnabled);
            session.IsScriptWordWrapEnabled = false;
            Assert.False(session.IsScriptWordWrapEnabled);
            var wrapConverter = new BoolToTextWrappingConverter();
            Assert.Equal(TextWrapping.Wrap, wrapConverter.Convert(true, typeof(TextWrapping), null, CultureInfo.InvariantCulture));
            Assert.Equal(TextWrapping.NoWrap, wrapConverter.Convert(false, typeof(TextWrapping), null, CultureInfo.InvariantCulture));

            // ViewSelectedScriptCommand switches active tab headlessly
            await vm.ViewSelectedScriptCommand.ExecuteAsync();
            Assert.Equal(RightPanelTab.Script, session.ActiveRightTab);
            Assert.True(session.IsScriptTabActive);

            // Double Reset() emits zero PropertyChanged events
            session.Reset();
            Assert.True(session.IsScriptWordWrapEnabled);
            var changedAfterReset = new List<string>();
            session.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    changedAfterReset.Add(e.PropertyName);
                }
            };
            session.Reset();
            Assert.Empty(changedAfterReset);
        }

        [Fact]
        public void ScriptSourceDiscovery_RejectsNativePeDlls_AndAutoDiscoversMetadataCacheAndSiblingFolders()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var gameRoot = Path.Combine(tempRoot, "SampleProject");
                var dataDir = Path.Combine(gameRoot, "SampleProject_Data");
                var metadataDir = Path.Combine(dataDir, "il2cpp_data", "Metadata");
                Directory.CreateDirectory(metadataDir);

                // Write minimal valid native x64 PE DLLs (NO CLR metadata header)
                var nativeGameAsm = Path.Combine(gameRoot, "GameAssembly.dll");
                var nativePlayer = Path.Combine(gameRoot, "UnityPlayer.dll");
                var emptyDll = Path.Combine(gameRoot, "empty.dll");
                WriteMinimalNativePeDll(nativeGameAsm);
                WriteMinimalNativePeDll(nativePlayer);
                File.WriteAllBytes(emptyDll, Array.Empty<byte>());

                var metadataPath = Path.Combine(metadataDir, "global-metadata.dat");
                WriteSyntheticIl2CppMetadata(metadataPath, version: 31);

                // Native PE DLLs must NOT be misclassified as managed .NET assemblies
                Assert.False(ScriptSourceDiscovery.IsManagedAssemblyFile(nativeGameAsm));
                Assert.False(ScriptSourceDiscovery.IsManagedAssemblyFile(nativePlayer));
                Assert.False(ScriptSourceDiscovery.IsManagedAssemblyFile(emptyDll));
                Assert.True(ScriptSourceDiscovery.IsManagedAssemblyFile(typeof(MonoScript).Assembly.Location));

                // Auto-discovery on IL2CPP game root: 0 false ManagedDirectories, finds global-metadata.dat & GameAssembly.dll
                var discovered = ScriptSourceDiscovery.Discover(projectRoot: gameRoot);
                Assert.Empty(discovered.ManagedDirectories);
                Assert.Empty(discovered.DummyDllDirectories);
                Assert.Equal(Path.GetFullPath(metadataPath), discovered.GlobalMetadataPath);
                Assert.Equal(Path.GetFullPath(nativeGameAsm), discovered.GameAssemblyPath);
                Assert.True(discovered.HasAnySource);

                // Opening *_Data directly still discovers GameAssembly.dll in parent and global-metadata.dat inside
                var fromDataDir = ScriptSourceDiscovery.Discover(projectRoot: dataDir);
                Assert.Empty(fromDataDir.ManagedDirectories);
                Assert.Equal(Path.GetFullPath(metadataPath), fromDataDir.GlobalMetadataPath);
                Assert.Equal(Path.GetFullPath(nativeGameAsm), fromDataDir.GameAssemblyPath);

                // ProjectAnalysis cache directory's il2cppdumper/ subdir is automatically probed
                var fakeCacheDir = Path.Combine(tempRoot, "AnalysisCache", "projhash");
                var cachedDumperDir = Path.Combine(fakeCacheDir, "il2cppdumper");
                Directory.CreateDirectory(cachedDumperDir);
                var cachedDumpCs = Path.Combine(cachedDumperDir, "dump.cs");
                var cachedScriptJson = Path.Combine(cachedDumperDir, "script.json");
                File.WriteAllText(cachedDumpCs, "// Namespace: Demo\npublic class CachedType { }\n", Encoding.UTF8);
                File.WriteAllText(cachedScriptJson, "{\"ScriptMethod\":[]}", Encoding.UTF8);

                var withCache = ScriptSourceDiscovery.Discover(
                    projectRoot: gameRoot,
                    projectAnalysisCacheDirectory: fakeCacheDir);
                Assert.Equal(Path.GetFullPath(cachedDumpCs), withCache.DumpCsPath);
                Assert.Equal(Path.GetFullPath(cachedScriptJson), withCache.ScriptJsonPath);

                // Sibling dumper folder next to gameRoot is discovered automatically
                var siblingDumper = Path.Combine(tempRoot, "SampleProject_dump");
                Directory.CreateDirectory(siblingDumper);
                var siblingDumpCs = Path.Combine(siblingDumper, "dump.cs");
                File.WriteAllText(siblingDumpCs, "// Namespace: Demo\npublic class SiblingType { }\n", Encoding.UTF8);

                var withSibling = ScriptSourceDiscovery.Discover(projectRoot: gameRoot);
                Assert.Equal(Path.GetFullPath(siblingDumpCs), withSibling.DumpCsPath);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Theory]
        [InlineData(24, false)]
        [InlineData(24, true)]
        [InlineData(27, false)]
        [InlineData(29, false)]
        [InlineData(31, false)]
        public void Il2CppMetadataReader_ReconstructsCSharpClassesGenericsAndEnums_AcrossVersions24To31(
            int metadataVersion,
            bool useLegacyV24Layout)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var metadataPath = Path.Combine(tempRoot, $"global-metadata-v{metadataVersion}.dat");
                WriteSyntheticIl2CppMetadata(metadataPath, metadataVersion, useLegacyV24Layout);

                var reader = new Il2CppMetadataReader();
                var inspection = reader.InspectMetadata(metadataPath);
                Assert.True(inspection.Success, inspection.DiagnosticMessage);
                Assert.Equal(metadataVersion, inspection.MetadataVersion);
                Assert.Equal(2, inspection.ImageCount);
                Assert.True(inspection.TypeCount >= 6);

                // 1) Generic class with base class, fields, properties, methods, parameters, and generic method
                var classResult = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "InventoryBag`1"
                    });

                Assert.NotNull(classResult);
                Assert.True(classResult!.Success);
                Assert.Equal(ScriptSourceKind.Il2CppGlobalMetadata, classResult.SourceKind);
                Assert.Equal($"IL2CPP (metadata v{metadataVersion})", classResult.BackendBadge);
                Assert.Equal("Demo.Gameplay.InventoryBag`1", classResult.Title);
                Assert.Equal("Assembly-CSharp.dll", classResult.AssemblyName);
                Assert.Contains($"global-metadata.dat (v{metadataVersion})", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("namespace Demo.Gameplay", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public class InventoryBag<TItem> : BaseController", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Capacity;", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public int Count { get; set; }", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public virtual void AddItem(int slotIndex, string itemKey);", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public static int ResolveTag<TTag>(string tagName);", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Token: 0x0200", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Empty(classResult.DiagnosticMessage);

                // 2) Enum with exact constant values (including v29+/v31 compressed Int32 decoding!)
                var enumResult = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "ActorState"
                    });

                Assert.NotNull(enumResult);
                Assert.True(enumResult!.Success);
                Assert.Contains("public enum ActorState", enumResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Idle = 0", enumResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Moving = 1", enumResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Attacking = 250", enumResult.SourceCode, StringComparison.Ordinal);

                // 3) Second lookup hits lightweight cache cleanly
                var cachedLookup = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        Namespace = "Demo.Gameplay",
                        ClassName = "InventoryBag"
                    });
                Assert.NotNull(cachedLookup);
                Assert.Equal(classResult.TypeDefIndex, cachedLookup!.TypeDefIndex);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Il2CppMetadataReader_HandlesCorruptedObfuscatedAndUnsupportedFilesDefensively()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var reader = new Il2CppMetadataReader();

                // Wrong magic / encrypted bytes
                var encryptedPath = Path.Combine(tempRoot, "encrypted.dat");
                File.WriteAllBytes(encryptedPath, Enumerable.Repeat((byte)0xA5, 512).ToArray());
                var encOutcome = reader.InspectMetadata(encryptedPath);
                Assert.False(encOutcome.Success);
                Assert.True(encOutcome.IsObfuscatedOrUnsupported);
                Assert.Contains("magic mismatch", encOutcome.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);

                // Truncated file
                var truncatedPath = Path.Combine(tempRoot, "truncated.dat");
                File.WriteAllBytes(truncatedPath, new byte[] { 0xAF, 0x1B, 0xB1, 0xFA, 31, 0, 0, 0 });
                var truncOutcome = reader.InspectMetadata(truncatedPath);
                Assert.False(truncOutcome.Success);

                // Unsupported future metadata version
                var futurePath = Path.Combine(tempRoot, "future.dat");
                var futureBytes = new byte[512];
                BinaryPrimitives.WriteUInt32LittleEndian(futureBytes.AsSpan(0, 4), Il2CppMetadataReader.MetadataSanity);
                BinaryPrimitives.WriteInt32LittleEndian(futureBytes.AsSpan(4, 4), 99);
                File.WriteAllBytes(futurePath, futureBytes);
                var futureOutcome = reader.InspectMetadata(futurePath);
                Assert.False(futureOutcome.Success);
                Assert.Contains("v99", futureOutcome.DiagnosticMessage, StringComparison.Ordinal);

                // Valid header magic & v31 version, but garbled/obfuscated table contents
                var garbledPath = Path.Combine(tempRoot, "garbled.dat");
                var garbledBytes = new byte[2048];
                BinaryPrimitives.WriteUInt32LittleEndian(garbledBytes.AsSpan(0, 4), Il2CppMetadataReader.MetadataSanity);
                BinaryPrimitives.WriteInt32LittleEndian(garbledBytes.AsSpan(4, 4), 31);
                BinaryPrimitives.WriteInt32LittleEndian(garbledBytes.AsSpan(8, 4), 256);
                for (var i = 0; i < 31; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(garbledBytes.AsSpan(8 + i * 8, 4), 256);
                    BinaryPrimitives.WriteInt32LittleEndian(garbledBytes.AsSpan(12 + i * 8, 4), 256);
                }
                for (var i = 256; i < garbledBytes.Length; i++)
                {
                    garbledBytes[i] = (byte)((i * 37) ^ 0xCC);
                }
                File.WriteAllBytes(garbledPath, garbledBytes);
                var garbledResult = reader.TryExtractType(
                    garbledPath,
                    new ScriptLookupRequest { ClassName = "AnyType" },
                    out var diag);
                Assert.Null(garbledResult);
                Assert.False(string.IsNullOrWhiteSpace(diag));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ScriptViewerService_AutoResolvesIl2CppGlobalMetadata_WithoutManualFolderSelection_AndRespectsDumpPriority()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var gameRoot = Path.Combine(tempRoot, "AutoIl2CppProject");
                var metadataDir = Path.Combine(gameRoot, "AutoIl2CppProject_Data", "il2cpp_data", "Metadata");
                Directory.CreateDirectory(metadataDir);

                // Native GameAssembly.dll in root + global-metadata.dat in *_Data/il2cpp_data/Metadata/
                WriteMinimalNativePeDll(Path.Combine(gameRoot, "GameAssembly.dll"));
                var metadataPath = Path.Combine(metadataDir, "global-metadata.dat");
                WriteSyntheticIl2CppMetadata(metadataPath, version: 31);

                using var viewer = new ScriptViewerService();

                // 1) Zero manual folder selection, zero Il2CppDumper -> resolves automatically from global-metadata.dat!
                var autoResult = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Assembly-CSharp.dll",
                    Namespace = "Demo.Gameplay",
                    ClassName = "InventoryBag`1",
                    ProjectRoot = gameRoot,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp
                });

                Assert.True(autoResult.Success, autoResult.DiagnosticMessage);
                Assert.Equal(ScriptSourceKind.Il2CppGlobalMetadata, autoResult.SourceKind);
                Assert.Equal("IL2CPP (metadata v31)", autoResult.BackendBadge);
                Assert.Contains("public class InventoryBag<TItem> : BaseController", autoResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("AddItem(int slotIndex, string itemKey)", autoResult.SourceCode, StringComparison.Ordinal);

                // 2) Missing type in valid metadata does NOT falsely mention "Managed DLLs"
                var missingResult = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Assembly-CSharp.dll",
                    Namespace = "Demo.Gameplay",
                    ClassName = "DoesNotExistAnywhere",
                    ProjectRoot = gameRoot,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp
                });
                Assert.False(missingResult.Success);
                Assert.DoesNotContain("Managed DLLs", missingResult.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("global-metadata.dat", missingResult.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);

                // 3) When dump.cs IS also present, dump.cs takes higher priority than global-metadata.dat
                var dumpDir = Path.Combine(gameRoot, "Il2CppDumper");
                Directory.CreateDirectory(dumpDir);
                File.WriteAllText(
                    Path.Combine(dumpDir, "dump.cs"),
                    "// Namespace: Demo.Gameplay\npublic class InventoryBag<TItem> // TypeDefIndex: 99\n{\n    // RVA: 0x1000\n    public void FromDump() { }\n}\n",
                    Encoding.UTF8);

                var dumpPreferred = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Assembly-CSharp.dll",
                    Namespace = "Demo.Gameplay",
                    ClassName = "InventoryBag`1",
                    ProjectRoot = gameRoot,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp
                });
                Assert.True(dumpPreferred.Success);
                Assert.Equal(ScriptSourceKind.Il2CppDumpCs, dumpPreferred.SourceKind);
                Assert.Contains("FromDump", dumpPreferred.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void WriteMinimalNativePeDll(string path)
        {
            // Minimal valid PE32+ (64-bit native DLL) with 16 zeroed Data Directories (no CLR metadata header)
            var bytes = new byte[512];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            const int peOffset = 0x80;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), peOffset);

            // PE signature
            bytes[peOffset + 0] = (byte)'P';
            bytes[peOffset + 1] = (byte)'E';
            bytes[peOffset + 2] = 0;
            bytes[peOffset + 3] = 0;

            // COFF header (20 bytes)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 4, 2), 0x8664); // AMD64
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2), 1);      // NumberOfSections = 1
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2), 0xF0);  // SizeOfOptionalHeader = 240
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 22, 2), 0x2022);// Characteristics = DLL | Executable

            // Optional header PE32+ (magic 0x020B)
            var optOff = peOffset + 24;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optOff, 2), 0x020B);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 32, 4), 0x1000);  // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 36, 4), 0x200);   // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 56, 4), 0x2000);  // SizeOfImage
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 60, 4), 0x200);   // SizeOfHeaders
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optOff + 108, 4), 16);     // NumberOfRvaAndSizes = 16 (all zero -> native!)

            // Single minimal section header (.text)
            var secOff = optOff + 0xF0;
            Encoding.ASCII.GetBytes(".text").CopyTo(bytes, secOff);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(secOff + 8, 4), 0x100);    // VirtualSize
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(secOff + 12, 4), 0x1000);  // VirtualAddress
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(secOff + 16, 4), 0x200);   // SizeOfRawData
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(secOff + 20, 4), 0x200);   // PointerToRawData

            File.WriteAllBytes(path, bytes);
        }

        private static void WriteSyntheticIl2CppMetadata(string path, int version, bool legacyV24 = false)
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

            // Build strings
            var sEmpty = AddStr(string.Empty);
            var sModule = AddStr("<Module>");
            var sMscorlibDll = AddStr("mscorlib.dll");
            var sGameDll = AddStr("Assembly-CSharp.dll");
            var sSystem = AddStr("System");
            var sObject = AddStr("Object");
            var sVoid = AddStr("Void");
            var sInt32 = AddStr("Int32");
            var sString = AddStr("String");
            var sEnum = AddStr("Enum");
            var sDemoNs = AddStr("Demo.Gameplay");
            var sBaseCtrl = AddStr("BaseController");
            var sInventory = AddStr("InventoryBag`1");
            var sActorState = AddStr("ActorState");
            var sTItem = AddStr("TItem");
            var sTTag = AddStr("TTag");
            var sCapacity = AddStr("Capacity");
            var sCount = AddStr("Count");
            var sGetCount = AddStr("get_Count");
            var sSetCount = AddStr("set_Count");
            var sAddItem = AddStr("AddItem");
            var sResolveTag = AddStr("ResolveTag");
            var sSlotIndex = AddStr("slotIndex");
            var sItemKey = AddStr("itemKey");
            var sValueParam = AddStr("value");
            var sTagName = AddStr("tagName");
            var sValueField = AddStr("value__");
            var sIdle = AddStr("Idle");
            var sMoving = AddStr("Moving");
            var sAttacking = AddStr("Attacking");

            // byvalTypeIndex constants:
            // 0=<Module>, 1=System.Object, 2=System.Void, 3=System.Int32, 4=System.String, 5=System.Enum, 6=BaseController, 7=InventoryBag`1, 8=ActorState
            const int TyObject = 101;
            const int TyVoid = 102;
            const int TyInt32 = 103;
            const int TyString = 104;
            const int TyEnum = 105;
            const int TyBaseCtrl = 106;
            const int TyInventory = 107;
            const int TyActorState = 108;

            var useLegacy = version == 24 && legacyV24;
            var tdSize = version >= 27 ? 88 : (useLegacy ? 104 : 92);
            var methodSize = version >= 31 ? 36 : (useLegacy ? 56 : 32);
            var fieldSize = useLegacy ? 16 : 12;
            var paramSize = useLegacy ? 16 : 12;
            var propSize = useLegacy ? 24 : 20;
            var imgSize = useLegacy ? 32 : 40;
            var hasRgctxPair = useLegacy;
            var pairCount = hasRgctxPair ? 33 : (version == 24 ? 32 : 31);
            var headerSize = 8 + pairCount * 8;

            // Default value data for enum ActorState (Idle=0, Moving=1, Attacking=250)
            var defDataMs = new MemoryStream();
            int WriteInt32Default(int v)
            {
                var pos = (int)defDataMs.Position;
                if (version >= 29)
                {
                    // IL2CPP compressed signed int32: non-negative -> (uint)v << 1
                    uint enc = (uint)v << 1;
                    if (enc < 0x80)
                    {
                        defDataMs.WriteByte((byte)enc);
                    }
                    else
                    {
                        defDataMs.WriteByte((byte)(0x80 | (enc >> 8)));
                        defDataMs.WriteByte((byte)(enc & 0xFF));
                    }
                }
                else
                {
                    Span<byte> tmp = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(tmp, v);
                    defDataMs.Write(tmp);
                }
                return pos;
            }
            var dIdle = WriteInt32Default(0);
            var dMoving = WriteInt32Default(1);
            var dAttacking = WriteInt32Default(250);

            // Fields:
            // [0] Capacity (on InventoryBag`1, unresolved custom field typeIndex 999)
            // [1] value__ (on ActorState)
            // [2] Idle (on ActorState)
            // [3] Moving (on ActorState)
            // [4] Attacking (on ActorState)
            var fieldsMs = new MemoryStream();
            void WriteField(int nameIdx, int typeIdx, uint token)
            {
                var buf = new byte[fieldSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), typeIdx);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fieldSize - 4, 4), token);
                fieldsMs.Write(buf);
            }
            WriteField(sCapacity, 999, 0x04000001);
            WriteField(sValueField, TyInt32, 0x04000002);
            WriteField(sIdle, TyActorState, 0x04000003);
            WriteField(sMoving, TyActorState, 0x04000004);
            WriteField(sAttacking, TyActorState, 0x04000005);

            // FieldDefaultValues (sorted by fieldIndex): fields 2, 3, 4
            var fdvMs = new MemoryStream();
            void WriteFdv(int fieldIdx, int typeIdx, int dataIdx)
            {
                Span<byte> buf = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), fieldIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), typeIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(8, 4), dataIdx);
                fdvMs.Write(buf);
            }
            WriteFdv(2, TyInt32, dIdle);
            WriteFdv(3, TyInt32, dMoving);
            WriteFdv(4, TyInt32, dAttacking);

            // Parameters:
            // [0] value (int) for set_Count
            // [1] slotIndex (int), [2] itemKey (string) for AddItem
            // [3] tagName (string) for ResolveTag<TTag>
            var paramsMs = new MemoryStream();
            void WriteParam(int nameIdx, uint token, int typeIdx)
            {
                var buf = new byte[paramSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), token);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(paramSize - 4, 4), typeIdx);
                paramsMs.Write(buf);
            }
            WriteParam(sValueParam, 0x08000001, TyInt32);
            WriteParam(sSlotIndex, 0x08000002, TyInt32);
            WriteParam(sItemKey, 0x08000003, TyString);
            WriteParam(sTagName, 0x08000004, TyString);

            // GenericContainers (16B each):
            // [0] owner=TypeDef 7 (InventoryBag`1), argc=1, isMethod=0, paramStart=0 (TItem)
            // [1] owner=Method 3 (ResolveTag), argc=1, isMethod=1, paramStart=1 (TTag)
            var gcMs = new MemoryStream();
            void WriteGc(int owner, int argc, int isMethod, int paramStart)
            {
                Span<byte> buf = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), owner);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), argc);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(8, 4), isMethod);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(12, 4), paramStart);
                gcMs.Write(buf);
            }
            WriteGc(7, 1, 0, 0);
            WriteGc(3, 1, 1, 1);

            // GenericParameters (16B each):
            var gpMs = new MemoryStream();
            void WriteGp(int owner, int nameIdx, ushort num)
            {
                Span<byte> buf = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), owner);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), nameIdx);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(12, 2), num);
                gpMs.Write(buf);
            }
            WriteGp(0, sTItem, 0);
            WriteGp(1, sTTag, 0);

            // Methods on InventoryBag`1 (declaringType = 7):
            // [0] public int get_Count()
            // [1] public void set_Count(int value)
            // [2] public virtual void AddItem(int slotIndex, string itemKey)
            // [3] public static int ResolveTag<TTag>(string tagName)
            var methodsMs = new MemoryStream();
            void WriteMethod(int nameIdx, int declaringTd, int retType, int paramStart, int genContIdx, uint token, ushort flags, ushort paramCount)
            {
                var buf = new byte[methodSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), declaringTd);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), retType);
                if (methodSize == 36)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), 0x08000000);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), paramStart);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), genContIdx);
                }
                else if (methodSize == 56)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), paramStart);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), -1);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), genContIdx);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), paramStart);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), genContIdx);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(methodSize - 12, 4), token);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(methodSize - 8, 2), flags);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(methodSize - 2, 2), paramCount);
                methodsMs.Write(buf);
            }
            WriteMethod(sGetCount, 7, TyInt32, -1, -1, 0x06000001, 0x0886, 0);              // public
            WriteMethod(sSetCount, 7, TyVoid, 0, -1, 0x06000002, 0x0886, 1);                // public
            WriteMethod(sAddItem, 7, TyVoid, 1, -1, 0x06000003, 0x01C6, 2);                 // public virtual (0x6 | 0x40 | 0x80 | 0x100)
            WriteMethod(sResolveTag, 7, TyInt32, 3, 1, 0x06000004, 0x0096, 1);              // public static (0x6 | 0x10 | 0x80)

            // Properties on InventoryBag`1:
            // [0] Count { get=0, set=1 }
            var propsMs = new MemoryStream();
            {
                var buf = new byte[propSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), sCount);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(propSize - 4, 4), 0x17000001);
                propsMs.Write(buf);
            }

            // TypeDefinitions (9 total: 0..5 in mscorlib.dll, 6..8 in Assembly-CSharp.dll)
            var tdMs = new MemoryStream();
            void WriteTypeDef(
                int nameIdx, int nsIdx, int byvalIdx, int parentIdx, int elementIdx, int genContIdx,
                uint flags, int fStart, int mStart, int pStart,
                ushort mCount, ushort pCount, ushort fCount, uint bitfield, uint token)
            {
                var buf = new byte[tdSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), nsIdx);

                int byvalOff = useLegacy ? 12 : 8;
                int declOff = version >= 27 ? 12 : (useLegacy ? 20 : 16);
                int parentOff = declOff + 4;
                int elemOff = declOff + 8;
                int gcOff = version >= 27 ? 24 : (useLegacy ? 40 : 28);
                int flagsOff = gcOff + 4;
                int fStartOff = gcOff + 8;
                int mStartOff = gcOff + 12;
                int eStartOff = gcOff + 16;
                int pStartOff = gcOff + 20;
                int nStartOff = gcOff + 24;
                int iStartOff = gcOff + 28;

                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(byvalOff, 4), byvalIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(declOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(parentOff, 4), parentIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(elemOff, 4), elementIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(gcOff, 4), genContIdx);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(flagsOff, 4), flags);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(fStartOff, 4), fStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(mStartOff, 4), mStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(eStartOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pStartOff, 4), pStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(nStartOff, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(iStartOff, 4), -1);

                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tdSize - 24, 2), mCount);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tdSize - 22, 2), pCount);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tdSize - 20, 2), fCount);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(tdSize - 8, 4), bitfield);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(tdSize - 4, 4), token);
                tdMs.Write(buf);
            }

            WriteTypeDef(sModule, sEmpty, 100, -1, 100, -1, 0, -1, -1, -1, 0, 0, 0, 0, 0x02000001);
            WriteTypeDef(sObject, sSystem, TyObject, -1, TyObject, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x02000002);
            WriteTypeDef(sVoid, sSystem, TyVoid, TyObject, TyVoid, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000003);
            WriteTypeDef(sInt32, sSystem, TyInt32, TyObject, TyInt32, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000004);
            WriteTypeDef(sString, sSystem, TyString, TyObject, TyString, -1, 0x101, -1, -1, -1, 0, 0, 0, 0, 0x02000005);
            WriteTypeDef(sEnum, sSystem, TyEnum, TyObject, TyEnum, -1, 0x81, -1, -1, -1, 0, 0, 0, 0, 0x02000006);
            WriteTypeDef(sBaseCtrl, sDemoNs, TyBaseCtrl, TyObject, TyBaseCtrl, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x02000007);
            WriteTypeDef(sInventory, sDemoNs, TyInventory, TyBaseCtrl, TyInventory, 0, 1, 0, 0, 0, 4, 1, 1, 0, 0x02000008);
            WriteTypeDef(sActorState, sDemoNs, TyActorState, TyEnum, TyInt32, -1, 0x101, 1, -1, -1, 0, 0, 4, 0x3, 0x02000009);

            // Images (2 images: mscorlib.dll [0..6), Assembly-CSharp.dll [6..9))
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
            WriteImg(sMscorlibDll, 0, 0, 6);
            WriteImg(sGameDll, 1, 6, 3);

            var sections = new byte[pairCount][];
            for (var i = 0; i < pairCount; i++) sections[i] = Array.Empty<byte>();
            sections[2] = stringPool.ToArray();
            sections[4] = propsMs.ToArray();
            sections[5] = methodsMs.ToArray();
            sections[7] = fdvMs.ToArray();
            sections[8] = defDataMs.ToArray();
            sections[10] = paramsMs.ToArray();
            sections[11] = fieldsMs.ToArray();
            sections[12] = gpMs.ToArray();
            sections[14] = gcMs.ToArray();
            sections[19] = tdMs.ToArray();
            sections[hasRgctxPair ? 21 : 20] = imgMs.ToArray();

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

            File.WriteAllBytes(path, outMs.ToArray());
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "AssetStudio_Phase2Tests_" + Guid.NewGuid().ToString("N"));
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
