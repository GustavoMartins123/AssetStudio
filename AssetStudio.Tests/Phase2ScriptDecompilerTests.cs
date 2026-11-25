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
    public partial class Phase2ScriptDecompilerTests
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
                Assert.Equal("Mono", result.BackendBadge);
                Assert.Equal("AssetStudio.MonoScript", result.Title);
                Assert.DoesNotContain("ICSharpCode.Decompiler", result.SourceCode, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ILSpy", result.SourceCode, StringComparison.OrdinalIgnoreCase);
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
                Assert.Contains("Scripts Folder...", unavailable.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
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
                BackendBadge = "Mono",
                Title = "Game.Player",
                AssemblyName = "Assembly-CSharp.dll",
                Namespace = "Game",
                ClassName = "Player",
                SourceCode = "public class Player\n{\n    public int Health;\n}",
                SummaryText = "Assembly-CSharp.dll • 4 lines • Mono"
            };

            Assert.False(session.CompleteScriptRequest(req1, sampleResult));
            Assert.True(session.CompleteScriptRequest(req2, sampleResult));
            Assert.True(session.HasScriptSource);
            Assert.True(session.CanSaveOrCopyScript);
            Assert.True(vm.CopyScriptCodeCommand.CanExecute());
            Assert.True(vm.SaveScriptCodeCommand.CanExecute());
            Assert.Equal("Mono", session.ScriptBackendBadge);
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
                Assert.Contains($"IL2CPP Metadata v{metadataVersion}", classResult.SourceCode, StringComparison.Ordinal);
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

        [Fact]
        public void Il2CppMetadataReader_AutoUnmasksShiftedAndRepeatingXorMetadata_WithoutHardcodedKeys()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var plainPath = Path.Combine(tempRoot, "plain.dat");
                WriteSyntheticIl2CppMetadata(plainPath, version: 31);
                var plainBytes = File.ReadAllBytes(plainPath);
                var headerSize = BinaryPrimitives.ReadInt32LittleEndian(plainBytes.AsSpan(8, 4));

                // Construct a synthetic shifted (+25 bytes) + repeating 16-byte XOR-masked metadata file
                const int extraPrefixLen = 25;
                var syntheticMask = new byte[]
                {
                    0x5A, 0xC3, 0x19, 0x8E, 0x42, 0xF1, 0x77, 0x2D,
                    0xB4, 0x61, 0x0E, 0x9C, 0x35, 0xD8, 0x83, 0x4F
                };

                var bodyLen = plainBytes.Length - headerSize;
                var obfuscated = new byte[headerSize + extraPrefixLen + bodyLen];
                Buffer.BlockCopy(plainBytes, 0, obfuscated, 0, headerSize);
                for (var i = 0; i < extraPrefixLen; i++)
                {
                    obfuscated[headerSize + i] = (byte)(0xA0 + i);
                }
                Buffer.BlockCopy(plainBytes, headerSize, obfuscated, headerSize + extraPrefixLen, bodyLen);

                // XOR the entire body after the unencrypted header using file-offset modulo 16
                for (var i = headerSize; i < obfuscated.Length; i++)
                {
                    obfuscated[i] ^= syntheticMask[i % syntheticMask.Length];
                }

                var maskedPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(maskedPath, obfuscated);

                var reader = new Il2CppMetadataReader();
                var inspect = reader.InspectMetadata(maskedPath);
                Assert.True(inspect.Success, inspect.DiagnosticMessage);
                Assert.Equal(31, inspect.MetadataVersion);
                Assert.Equal(2, inspect.ImageCount);

                // Verify generic class, methods, and TypeTree field-type enrichment work on unmasked metadata
                var enrichTree = new TypeTree
                {
                    m_Nodes = new List<TypeTreeNode>
                    {
                        new("MonoBehaviour", "Base", 0, false),
                        new("PPtr<GameObject>", "m_GameObject", 1, false),
                        new("UInt8", "m_Enabled", 1, true),
                        new("PPtr<MonoScript>", "m_Script", 1, false),
                        new("string", "m_Name", 1, true),
                        new("SInt32", "Capacity", 1, false)
                    }
                };

                var cls = reader.TryExtractType(
                    maskedPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "InventoryBag`1",
                        FallbackTypeTree = enrichTree
                    });

                Assert.NotNull(cls);
                Assert.True(cls!.Success);
                Assert.Equal(ScriptSourceKind.Il2CppGlobalMetadata, cls.SourceKind);
                Assert.Contains("public class InventoryBag<TItem> : BaseController", cls.SourceCode, StringComparison.Ordinal);
                Assert.Contains("int Capacity;", cls.SourceCode, StringComparison.Ordinal);
                Assert.Contains("AddItem(int slotIndex, string itemKey);", cls.SourceCode, StringComparison.Ordinal);

                // Verify enum default values (binary search over FieldDefaultValues + DefaultValueData) also unmask properly!
                var enumRes = reader.TryExtractType(
                    maskedPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "ActorState"
                    });
                Assert.NotNull(enumRes);
                Assert.True(enumRes!.Success);
                Assert.Contains("Idle = 0", enumRes.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Moving = 1", enumRes.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Attacking = 250", enumRes.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void TypeTreeScriptReconstructor_ReconstructsFieldsCollectionsNestedTypes_AndActsAsViewerFallback()
        {
            var tree = new TypeTree
            {
                m_Nodes = new List<TypeTreeNode>
                {
                    new("MonoBehaviour", "Base", 0, false),
                    new("PPtr<GameObject>", "m_GameObject", 1, false),
                    new("int", "m_FileID", 2, false),
                    new("SInt64", "m_PathID", 2, false),
                    new("UInt8", "m_Enabled", 1, true),
                    new("PPtr<MonoScript>", "m_Script", 1, false),
                    new("int", "m_FileID", 2, false),
                    new("SInt64", "m_PathID", 2, false),
                    new("string", "m_Name", 1, true),
                    new("float", "m_MoveSpeed", 1, false),
                    new("Vector3f", "m_Offset", 1, false),
                    new("float", "x", 2, false),
                    new("float", "y", 2, false),
                    new("float", "z", 2, false),
                    new("ColorRGBA", "m_Tint", 1, false),
                    new("PPtr<$Texture2D>", "m_MainTex", 1, false),
                    new("int", "m_FileID", 2, false),
                    new("SInt64", "m_PathID", 2, false),
                    new("vector", "m_Renderers", 1, false),
                    new("Array", "Array", 2, false) { m_TypeFlags = 1 },
                    new("int", "size", 3, false),
                    new("PPtr<$MeshRenderer>", "data", 3, false),
                    new("vector", "m_Entries", 1, false),
                    new("Array", "Array", 2, false) { m_TypeFlags = 1 },
                    new("int", "size", 3, false),
                    new("DecalEntry", "data", 3, false),
                    new("string", "m_Key", 4, true),
                    new("float", "m_Opacity", 4, false),
                    new("map", "m_Weights", 1, false),
                    new("Array", "Array", 2, false) { m_TypeFlags = 1 },
                    new("int", "size", 3, false),
                    new("pair", "data", 3, false),
                    new("string", "first", 4, true),
                    new("SInt32", "second", 4, false)
                }
            };

            var tempRoot = CreateTempDirectory();
            try
            {
                // Write a strongly-corrupted global-metadata.dat so metadata parsing fails and TypeTree fallback kicks in!
                var metaDir = Path.Combine(tempRoot, "Demo_Data", "il2cpp_data", "Metadata");
                Directory.CreateDirectory(metaDir);
                var badMeta = Path.Combine(metaDir, "global-metadata.dat");
                File.WriteAllBytes(badMeta, Enumerable.Repeat((byte)0x99, 512).ToArray());

                using var viewer = new ScriptViewerService();
                var result = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Demo.Framework.dll",
                    Namespace = "Demo.Visuals",
                    ClassName = "DecalController",
                    ProjectRoot = tempRoot,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                    FallbackTypeTree = tree,
                    FallbackTypeTreeSourceFile = "CAB-sample"
                });

                Assert.True(result.Success);
                Assert.Equal(ScriptSourceKind.UnityTypeTree, result.SourceKind);
                Assert.Equal("Unity TypeTree", result.BackendBadge);
                Assert.Contains("namespace Demo.Visuals", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public class DecalController : MonoBehaviour", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public float m_MoveSpeed;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public Vector3 m_Offset;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public Color m_Tint;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public Texture2D m_MainTex;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public List<MeshRenderer> m_Renderers;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public List<DecalEntry> m_Entries;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public Dictionary<string, int> m_Weights;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("[Serializable]", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public class DecalEntry", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public string m_Key;", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public float m_Opacity;", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("m_GameObject", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("m_Script", result.SourceCode, StringComparison.Ordinal);
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

        [Fact]
        public void Il2CppBinaryAndMetadata_Pe64V31_ResolvesAllTypeKindsModifiersOffsetsRvasAliasesAndGhidraExport()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var gameRoot = Path.Combine(tempRoot, "SyntheticV31Game");
                var metaDir = Path.Combine(gameRoot, "SyntheticV31Game_Data", "il2cpp_data", "Metadata");
                Directory.CreateDirectory(metaDir);

                var metadataPath = Path.Combine(metaDir, "global-metadata.dat");
                var binaryPath = Path.Combine(gameRoot, "GameAssembly.dll");
                WriteRichSyntheticIl2CppFixture(metadataPath, binaryPath, SyntheticBinaryKind.Pe64V31);

                var typeTree = new TypeTree
                {
                    m_Nodes = new List<TypeTreeNode>
                    {
                        new("MonoBehaviour", "Base", 0, false),
                        new("PPtr<GameObject>", "m_GameObject", 1, false),
                        new("UInt8", "m_Enabled", 1, true),
                        new("PPtr<MonoScript>", "m_Script", 1, false),
                        new("string", "m_Name", 1, true),
                        new("vector", "m_boneHideList", 1, false),
                        new("Array", "Array", 2, false) { m_TypeFlags = 1 },
                        new("int", "size", 3, false),
                        new("string", "data", 3, true)
                    }
                };

                using var viewer = new ScriptViewerService();
                var result = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Gameplay.Framework.dll",
                    Namespace = string.Empty,
                    ClassName = "RigScaleControl`1",
                    ProjectRoot = gameRoot,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                    FallbackTypeTree = typeTree
                });

                Assert.True(result.Success, result.DiagnosticMessage);
                Assert.Equal(ScriptSourceKind.Il2CppGlobalMetadata, result.SourceKind);
                Assert.Equal("IL2CPP (metadata v31 + binary)", result.BackendBadge);
                Assert.Contains("// Source: IL2CPP Metadata v31 + Native Binary", result.SourceCode, StringComparison.Ordinal);

                // Zero unresolved placeholders
                Assert.DoesNotContain("/* field */", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("/* prop */", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("/* param */", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("/* return */", result.SourceCode, StringComparison.Ordinal);

                // Field types, modifiers, offsets, custom attributes, TypeTree & backing-field correlation
                Assert.Contains("private const int MAX_BONES = 300; // Token: 0x04000001", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("static const", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("[SerializeField]", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public List<string> m_boneHideList; // Offset: 0x20 | Token: 0x04000002 | Serialized (TypeTree)", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private Dictionary<string, int> <HIFGJNOCGMD>k__BackingField; // Offset: 0x28 | Token: 0x04000003 | Backing field for BoneNameList", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private bool[] m_flags; // Offset: 0x30 | Token: 0x04000004", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private static readonly float[,] s_matrix; // Offset: 0x0 | Token: 0x04000005", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("[NonSerialized]", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("protected internal byte* m_rawPtr; // Offset: 0x38 | Token: 0x04000006", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private Dictionary<string, List<int>> m_nestedMap; // Offset: 0x40 | Token: 0x04000007", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private Label.UnitTypeID m_unitId; // Offset: 0x48 | Token: 0x04000008", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private int? m_optCount; // Offset: 0x50 | Token: 0x04000009", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private TState m_state; // Offset: 0x58 | Token: 0x0400000A", result.SourceCode, StringComparison.Ordinal);

                // Property accessor correlation for obfuscated property name
                Assert.Contains("public Dictionary<string, int> LIGFLDKFOLA { get; set; } // Token: 0x17000001 | Accessors: get_BoneNameList, set_BoneNameList", result.SourceCode, StringComparison.Ordinal);

                // Methods: RVAs, VAR/MVAR, BYREF (out / ref / in), CMOD_OPT stripping, abstract zero-RVA omission
                Assert.Contains("// RVA: 0x00001020 | Offset: 0x00000420 | VA: 0x180001020", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public Dictionary<string, int> get_BoneNameList();", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public bool TryLookup<TResult>(string key, out int value, ref float scale, in string tag, TResult fallback);", result.SourceCode, StringComparison.Ordinal);

                // Shared-RVA alias canonicalization: set_BoneNameList wins over earlier obfuscated aliases!
                Assert.Contains("// RVA: 0x00001060 | Offset: 0x00000460 | VA: 0x180001060", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// Aliases (2): ECLJAGIGDDM, KGPKONNLLPK", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// RVA: 0x00001060 (alias of set_BoneNameList)", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public void set_BoneNameList(Dictionary<string, int> value);", result.SourceCode, StringComparison.Ordinal);

                // Abstract method with 0 pointer emits NO RVA comment above it
                var abstractIdx = result.SourceCode.IndexOf("public abstract void AbstractHook();", StringComparison.Ordinal);
                Assert.True(abstractIdx > 0);
                var lineBeforeAbstract = result.SourceCode.Substring(Math.Max(0, abstractIdx - 80), Math.Min(80, abstractIdx));
                Assert.DoesNotContain("RVA: 0x00000000", lineBeforeAbstract, StringComparison.Ordinal);

                // MethodSymbols populated directly without external script.json, canonical readable symbol wins!
                Assert.NotEmpty(result.MethodSymbols);
                var sharedSym = Assert.Single(result.MethodSymbols, m => m.Address == 0x1060);
                Assert.Equal("set_BoneNameList", sharedSym.MethodName);
                Assert.Equal("RigScaleControl`1$$set_BoneNameList", sharedSym.FullName);

                // GhidraExportService also picks canonical readable symbol even if aliases arrive first
                var ghidraDir = Path.Combine(tempRoot, "ghidra_out");
                var exporter = new GhidraExportService();
                var ghidraRes = exporter.Export(new GhidraExportOptions
                {
                    OutputDirectory = ghidraDir,
                    Scope = GhidraExportScope.SelectedClass,
                    BinaryName = "GameAssembly.dll",
                    BackendLabel = result.BackendBadge,
                    ClassTitle = result.Title,
                    SelectedClassMethods = new[]
                    {
                        new ScriptMethodSymbol { ClassName = "RigScaleControl", MethodName = "ECLJAGIGDDM", FullName = "RigScaleControl$$ECLJAGIGDDM", Address = 0x1060 },
                        new ScriptMethodSymbol { ClassName = "RigScaleControl", MethodName = "set_BoneNameList", FullName = "RigScaleControl$$set_BoneNameList", Address = 0x1060 }
                    }
                });
                Assert.True(ghidraRes.Success, ghidraRes.ErrorMessage);
                Assert.Equal(1, ghidraRes.MethodCount);
                using var doc = JsonDocument.Parse(File.ReadAllText(ghidraRes.SymbolsJsonPath));
                Assert.Equal("RigScaleControl$$set_BoneNameList", doc.RootElement.GetProperty("methods")[0].GetProperty("name").GetString());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Il2CppBinaryAndMetadata_Pe32V24Legacy_AutoDetectsBit30ByrefV24GenericClassAndFlatMethodPointers()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                var binaryPath = Path.Combine(tempRoot, "GameAssembly.dll");
                WriteRichSyntheticIl2CppFixture(metadataPath, binaryPath, SyntheticBinaryKind.Pe32V24Legacy);

                var reader = new Il2CppMetadataReader();
                var result = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Gameplay.Framework.dll",
                        ClassName = "RigScaleControl`1",
                        GameAssemblyPath = binaryPath
                    });

                Assert.NotNull(result);
                Assert.True(result!.Success);
                Assert.Equal("IL2CPP (metadata v24 + binary)", result.BackendBadge);
                Assert.Contains("public List<string> m_boneHideList; // Offset: 0x14 | Token: 0x04000002", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private Dictionary<string, int> <HIFGJNOCGMD>k__BackingField; // Offset: 0x18 | Token: 0x04000003", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public bool TryLookup<TResult>(string key, out int value, ref float scale, in string tag, TResult fallback);", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// RVA: 0x00001020 | Offset: 0x00000420 | VA: 0x10001020", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// RVA: 0x00001060 | Offset: 0x00000460 | VA: 0x10001060", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains(result.MethodSymbols, m => m.MethodName == "set_BoneNameList" && m.Address == 0x1060);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Il2CppBinaryAndMetadata_Elf64AArch64WithRelaRelocations_ResolvesTypesOffsetsAndRvas(bool stripSectionHeaders)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                // Android APK-style layout: assets/bin/Data/Managed/Metadata/global-metadata.dat + lib/arm64-v8a/libil2cpp.so
                var apkRoot = Path.Combine(tempRoot, "AndroidSample");
                var metaDir = Path.Combine(apkRoot, "assets", "bin", "Data", "Managed", "Metadata");
                var libDir = Path.Combine(apkRoot, "lib", "arm64-v8a");
                Directory.CreateDirectory(metaDir);
                Directory.CreateDirectory(libDir);

                var metadataPath = Path.Combine(metaDir, "global-metadata.dat");
                var binaryPath = Path.Combine(libDir, "libil2cpp.so");
                WriteRichSyntheticIl2CppFixture(
                    metadataPath,
                    binaryPath,
                    stripSectionHeaders ? SyntheticBinaryKind.Elf64V31Stripped : SyntheticBinaryKind.Elf64V31);

                // Verify ancestor discovery from global-metadata.dat locates lib/arm64-v8a/libil2cpp.so automatically!
                var discovered = ScriptSourceDiscovery.Discover(projectRoot: null, globalMetadataPath: metadataPath);
                Assert.Equal(Path.GetFullPath(binaryPath), discovered.GameAssemblyPath);

                using var viewer = new ScriptViewerService();
                var result = viewer.ResolveScript(new ScriptLookupRequest
                {
                    AssemblyName = "Gameplay.Framework.dll",
                    ClassName = "RigScaleControl`1",
                    GlobalMetadataPath = metadataPath,
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp
                });

                Assert.True(result.Success, result.DiagnosticMessage);
                Assert.Equal("IL2CPP (metadata v31 + binary)", result.BackendBadge);
                Assert.DoesNotContain("/* field */", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("/* prop */", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public List<string> m_boneHideList; // Offset: 0x20 | Token: 0x04000002", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private Dictionary<string, List<int>> m_nestedMap; // Offset: 0x40 | Token: 0x04000007", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public bool TryLookup<TResult>(string key, out int value, ref float scale, in string tag, TResult fallback);", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// RVA: 0x00001060 | Offset: 0x00001060 | VA: 0x1060", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// Aliases (2): ECLJAGIGDDM, KGPKONNLLPK", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains(result.MethodSymbols, m => m.MethodName == "set_BoneNameList" && m.Address == 0x1060);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Il2CppMetadataReader_FallsBackGracefully_WhenNativeBinaryIsMissingOrCorrupt()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                WriteSyntheticIl2CppMetadata(metadataPath, version: 31);

                var corruptBin = Path.Combine(tempRoot, "GameAssembly.dll");
                File.WriteAllBytes(corruptBin, Encoding.ASCII.GetBytes("MZ_NOT_A_VALID_PE_OR_ELF_BINARY_BYTES_0123456789ABCDEF0123456789ABCDEF"));

                var reader = new Il2CppMetadataReader();
                var withCorrupt = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "InventoryBag`1",
                        GameAssemblyPath = corruptBin
                    });

                Assert.NotNull(withCorrupt);
                Assert.True(withCorrupt!.Success);
                Assert.Equal("IL2CPP (metadata v31)", withCorrupt.BackendBadge);
                Assert.Contains("// Source: IL2CPP Metadata v31", withCorrupt.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("+ Native Binary", withCorrupt.SourceCode, StringComparison.Ordinal);
                Assert.Empty(withCorrupt.MethodSymbols);

                var withMissing = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "InventoryBag`1",
                        GameAssemblyPath = Path.Combine(tempRoot, "MissingBinary.dll")
                    });

                Assert.NotNull(withMissing);
                Assert.True(withMissing!.Success);
                Assert.Equal("IL2CPP (metadata v31)", withMissing.BackendBadge);

                // Nested build layout: closest GameAssembly.dll to global-metadata.dat wins over stale root binary
                var nestedRoot = Path.Combine(tempRoot, "MultiBuildRoot");
                var subBuild = Path.Combine(nestedRoot, "BuildV2");
                var subMetaDir = Path.Combine(subBuild, "Demo_Data", "il2cpp_data", "Metadata");
                Directory.CreateDirectory(subMetaDir);
                WriteMinimalNativePeDll(Path.Combine(nestedRoot, "GameAssembly.dll"));
                var subBin = Path.Combine(subBuild, "GameAssembly.dll");
                WriteMinimalNativePeDll(subBin);
                var subMeta = Path.Combine(subMetaDir, "global-metadata.dat");
                WriteSyntheticIl2CppMetadata(subMeta, version: 31);

                var nestedDiscovered = ScriptSourceDiscovery.Discover(projectRoot: nestedRoot);
                Assert.Equal(Path.GetFullPath(subBin), nestedDiscovered.GameAssemblyPath);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private enum SyntheticBinaryKind
        {
            Pe64V31,
            Pe32V24Legacy,
            Elf64V31,
            Elf64V31Stripped
        }

        private static void WriteRichSyntheticIl2CppFixture(string metadataPath, string binaryPath, SyntheticBinaryKind kind)
        {
            var isV24 = kind == SyntheticBinaryKind.Pe32V24Legacy;
            var is64 = kind != SyntheticBinaryKind.Pe32V24Legacy;
            var isElf = kind is SyntheticBinaryKind.Elf64V31 or SyntheticBinaryKind.Elf64V31Stripped;
            var stripSh = kind == SyntheticBinaryKind.Elf64V31Stripped;
            var version = isV24 ? 24 : 31;

            // --- 1. Build synthetic global-metadata.dat ---
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
            var sMscorlibDll = AddStr("mscorlib.dll");
            var sGameDll = AddStr("Gameplay.Framework.dll");
            var sSystem = AddStr("System");
            var sScg = AddStr("System.Collections.Generic");
            var sCompilerSvc = AddStr("System.Runtime.CompilerServices");
            var sUnityEngine = AddStr("UnityEngine");
            var sObject = AddStr("Object");
            var sVoid = AddStr("Void");
            var sBoolean = AddStr("Boolean");
            var sByte = AddStr("Byte");
            var sInt32 = AddStr("Int32");
            var sSingle = AddStr("Single");
            var sString = AddStr("String");
            var sList1 = AddStr("List`1");
            var sDict2 = AddStr("Dictionary`2");
            var sNullable1 = AddStr("Nullable`1");
            var sSerializeField = AddStr("SerializeField");
            var sIsReadOnlyAttr = AddStr("IsReadOnlyAttribute");
            var sMonoBehaviour = AddStr("MonoBehaviour");
            var sLabel = AddStr("Label");
            var sUnitTypeID = AddStr("UnitTypeID");
            var sRigCtrl = AddStr("RigScaleControl`1");
            var sTState = AddStr("TState");
            var sTResult = AddStr("TResult");
            var sCtor = AddStr(".ctor");

            var sMaxBones = AddStr("MAX_BONES");
            var sBoneHideList = AddStr("m_boneHideList");
            var sBackingDict = AddStr("<HIFGJNOCGMD>k__BackingField");
            var sFlags = AddStr("m_flags");
            var sMatrix = AddStr("s_matrix");
            var sRawPtr = AddStr("m_rawPtr");
            var sNestedMap = AddStr("m_nestedMap");
            var sUnitId = AddStr("m_unitId");
            var sOptCount = AddStr("m_optCount");
            var sStateField = AddStr("m_state");

            var sObfProp = AddStr("LIGFLDKFOLA");
            var sGetBoneNameList = AddStr("get_BoneNameList");
            var sObfAlias1 = AddStr("ECLJAGIGDDM");
            var sObfAlias2 = AddStr("KGPKONNLLPK");
            var sSetBoneNameList = AddStr("set_BoneNameList");
            var sTryLookup = AddStr("TryLookup");
            var sAbstractHook = AddStr("AbstractHook");

            var sPValue = AddStr("value");
            var sPKey = AddStr("key");
            var sPScale = AddStr("scale");
            var sPTag = AddStr("tag");
            var sPFallback = AddStr("fallback");

            // TypeDefs (16 total):
            // 0:<Module>, 1:System.Object, 2:System.Void, 3:System.Boolean, 4:System.Byte,
            // 5:System.Int32, 6:System.Single, 7:System.String,
            // 8:System.Collections.Generic.List`1, 9:System.Collections.Generic.Dictionary`2,
            // 10:System.Nullable`1, 11:UnityEngine.SerializeField, 12:System.Runtime.CompilerServices.IsReadOnlyAttribute,
            // 13:UnityEngine.MonoBehaviour, 14:Label, 15:Label.UnitTypeID, 16:RigScaleControl`1
            const int TotalTypes = 17;
            // Compact byvalTypeIndex = TypeDefIndex (0..16), plus extra complex/field/param type indices 17..35
            const int TyObject = 1;
            const int TyVoid = 2;
            const int TyBool = 3;
            const int TyByte = 4;
            const int TyInt32 = 5;
            const int TySingle = 6;
            const int TyString = 7;
            const int TyList1 = 8;
            const int TyDict2 = 9;
            const int TyNullable1 = 10;
            const int TySerializeField = 11;
            const int TyIsReadOnlyAttr = 12;
            const int TyMonoBehaviour = 13;
            const int TyLabel = 14;
            const int TyUnitTypeID = 15;
            const int TyRigCtrl = 16;

            // Extra Il2CppType table indices (17..35) resolved ONLY from native binary:
            const int TyConstPrivateInt = 17;        // I4, attrs=0x8051 (private static literal hasDefault)
            const int TyPublicListString = 18;       // GENERICINST List<string>, attrs=0x0006 (public)
            const int TyPrivateDictStrInt = 19;      // GENERICINST Dict<string,int>, attrs=0x0001 (private)
            const int TyPrivateBoolArray = 20;       // SZARRAY bool[], attrs=0x0001 (private)
            const int TyPrivStatRoFloat2D = 21;      // ARRAY float[,], attrs=0x0031 (private static readonly)
            const int TyProtIntNotSerBytePtr = 22;   // PTR byte*, attrs=0x0085 (protected internal NotSerialized)
            const int TyPrivateNestedDict = 23;      // GENERICINST Dict<string, List<int>>, attrs=0x0001
            const int TyPrivateUnitTypeID = 24;      // VALUETYPE Label.UnitTypeID, attrs=0x0001
            const int TyPrivateNullableInt = 25;     // GENERICINST Nullable<int>, attrs=0x0001
            const int TyPrivateVarTState = 26;       // VAR TState (gp=0), attrs=0x0001
            const int TyDictStrIntParam = 27;        // GENERICINST Dict<string,int>, attrs=0
            const int TyOutIntParam = 28;            // I4, byref=1, attrs=0x0002 (Out)
            const int TyRefFloatParam = 29;          // R4, byref=1, attrs=0x0000
            const int TyInStringParam = 30;          // STRING, byref=1, attrs=0x0001 (+ IsReadOnlyAttribute on param)
            const int TyMvarTResult = 31;            // MVAR TResult (gp=1), attrs=0
            const int TyCmodStringParam = 32;        // CMOD_OPT -> STRING
            const int TotalBinaryTypes = 33;

            var tdSize = isV24 ? 104 : 88;
            var methodSize = isV24 ? 56 : 36;
            var fieldSize = isV24 ? 16 : 12;
            var paramSize = isV24 ? 16 : 12;
            var propSize = isV24 ? 24 : 20;
            var imgSize = 40;
            var pairCount = isV24 ? 33 : 31;
            var headerSize = 8 + pairCount * 8;

            // DefaultValueData: 300 for MAX_BONES
            var defDataMs = new MemoryStream();
            if (version >= 29)
            {
                uint enc = 300u << 1; // 600 = 0x0258 -> 2-byte compressed uint32: 0x82, 0x58
                defDataMs.WriteByte((byte)(0x80 | (enc >> 8)));
                defDataMs.WriteByte((byte)(enc & 0xFF));
            }
            else
            {
                Span<byte> tmp = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(tmp, 300);
                defDataMs.Write(tmp);
            }

            // Fields on RigScaleControl`1 (10 fields):
            var fieldsMs = new MemoryStream();
            void WriteField(int nameIdx, int typeIdx, uint token)
            {
                var buf = new byte[fieldSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), typeIdx);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fieldSize - 4, 4), token);
                fieldsMs.Write(buf);
            }
            WriteField(sMaxBones, TyConstPrivateInt, 0x04000001);
            WriteField(sBoneHideList, TyPublicListString, 0x04000002);
            WriteField(sBackingDict, TyPrivateDictStrInt, 0x04000003);
            WriteField(sFlags, TyPrivateBoolArray, 0x04000004);
            WriteField(sMatrix, TyPrivStatRoFloat2D, 0x04000005);
            WriteField(sRawPtr, TyProtIntNotSerBytePtr, 0x04000006);
            WriteField(sNestedMap, TyPrivateNestedDict, 0x04000007);
            WriteField(sUnitId, TyPrivateUnitTypeID, 0x04000008);
            WriteField(sOptCount, TyPrivateNullableInt, 0x04000009);
            WriteField(sStateField, TyPrivateVarTState, 0x0400000A);

            var fdvMs = new MemoryStream();
            {
                Span<byte> buf = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), 0); // field 0
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), TyInt32);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(8, 4), 0);
                fdvMs.Write(buf);
            }

            // Parameters:
            // [0] value (Dict<string,int>) for ECLJAGIGDDM
            // [1] value (Dict<string,int>) for KGPKONNLLPK
            // [2] value (Dict<string,int>) for set_BoneNameList
            // [3] key (CMOD_OPT->string), [4] value (out int), [5] scale (ref float), [6] tag (in string), [7] fallback (TResult) for TryLookup
            var paramsMs = new MemoryStream();
            void WriteParam(int nameIdx, uint token, int typeIdx)
            {
                var buf = new byte[paramSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), token);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(paramSize - 4, 4), typeIdx);
                paramsMs.Write(buf);
            }
            WriteParam(sPValue, 0x08000001, TyDictStrIntParam);
            WriteParam(sPValue, 0x08000002, TyDictStrIntParam);
            WriteParam(sPValue, 0x08000003, TyDictStrIntParam);
            WriteParam(sPKey, 0x08000004, TyCmodStringParam);
            WriteParam(sPValue, 0x08000005, TyOutIntParam);
            WriteParam(sPScale, 0x08000006, TyRefFloatParam);
            WriteParam(sPTag, 0x08000007, TyInStringParam); // has IsReadOnlyAttribute!
            WriteParam(sPFallback, 0x08000008, TyMvarTResult);

            // GenericContainers:
            // [0] owner=TypeDef 16 (RigScaleControl`1), argc=1, isMethod=0, paramStart=0 (TState)
            // [1] owner=Method 6 (TryLookup), argc=1, isMethod=1, paramStart=1 (TResult)
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
            WriteGc(TyRigCtrl, 1, 0, 0);
            WriteGc(6, 1, 1, 1);

            // GenericParameters:
            var gpMs = new MemoryStream();
            void WriteGp(int owner, int nameIdx, ushort num)
            {
                Span<byte> buf = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), owner);
                BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), nameIdx);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(12, 2), num);
                gpMs.Write(buf);
            }
            WriteGp(0, sTState, 0);
            WriteGp(1, sTResult, 0);

            // Methods (8 total):
            // [0] SerializeField..ctor (declaringType = 11, mscorlib)
            // [1] IsReadOnlyAttribute..ctor (declaringType = 12, mscorlib)
            // On RigScaleControl`1 (methodStart = 2, count = 6, Gameplay.Framework.dll):
            // [2] get_BoneNameList() -> Dict<string,int>           token=0x06000001, methodIdx=0, RVA=0x1020
            // [3] ECLJAGIGDDM(Dict<string,int> value)              token=0x06000002, methodIdx=1, RVA=0x1060 (alias!)
            // [4] KGPKONNLLPK(Dict<string,int> value)              token=0x06000003, methodIdx=2, RVA=0x1060 (alias!)
            // [5] set_BoneNameList(Dict<string,int> value)         token=0x06000004, methodIdx=3, RVA=0x1060 (canonical!)
            // [6] TryLookup<TResult>(string, out int, ref float, in string, TResult) -> bool  token=0x06000005, methodIdx=4, RVA=0x10A0
            // [7] AbstractHook() -> void                           token=0x06000006, methodIdx=-1, RVA=0x0 (abstract!)
            var methodsMs = new MemoryStream();
            void WriteMethod(int nameIdx, int declaringTd, int retType, int paramStart, int genContIdx, int methodIdx, uint token, ushort flags, ushort paramCount)
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
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), paramStart);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), -1);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), genContIdx);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24, 4), methodIdx);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(methodSize - 12, 4), token);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(methodSize - 8, 2), flags);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(methodSize - 2, 2), paramCount);
                methodsMs.Write(buf);
            }
            WriteMethod(sCtor, TySerializeField, TyVoid, -1, -1, -1, 0x06000001, 0x1886, 0);
            WriteMethod(sCtor, TyIsReadOnlyAttr, TyVoid, -1, -1, -1, 0x06000002, 0x1886, 0);
            WriteMethod(sGetBoneNameList, TyRigCtrl, TyDictStrIntParam, -1, -1, 0, 0x06000001, 0x0886, 0);
            WriteMethod(sObfAlias1, TyRigCtrl, TyVoid, 0, -1, 1, 0x06000002, 0x0086, 1);
            WriteMethod(sObfAlias2, TyRigCtrl, TyVoid, 1, -1, 2, 0x06000003, 0x0086, 1);
            WriteMethod(sSetBoneNameList, TyRigCtrl, TyVoid, 2, -1, 3, 0x06000004, 0x0886, 1);
            WriteMethod(sTryLookup, TyRigCtrl, TyBool, 3, 1, 4, 0x06000005, 0x0086, 5);
            WriteMethod(sAbstractHook, TyRigCtrl, TyVoid, -1, -1, -1, 0x06000006, 0x05C6, 0); // public abstract (0x400 | 0x100 | 0x80 | 0x40 | 0x6)

            // Property on RigScaleControl`1:
            // [0] LIGFLDKFOLA { get = local method 0 (get_BoneNameList), set = local method 3 (set_BoneNameList) }
            var propsMs = new MemoryStream();
            {
                var buf = new byte[propSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), sObfProp);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), 3);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(propSize - 4, 4), 0x17000001);
                propsMs.Write(buf);
            }

            // Custom attributes for Gameplay.Framework.dll:
            // - field 0x04000002 (m_boneHideList) -> [SerializeField]
            // - param 0x08000007 (tag) -> [IsReadOnlyAttribute]
            var attrDataOrInfoMs = new MemoryStream();
            var attrRangeOrTypesMs = new MemoryStream();
            if (!isV24)
            {
                // v29+ attributeData + attributeDataRange
                // Entry 0: token=0x04000002, startOffset=0 -> count=1, ctorMethodIdx=0 (SerializeField..ctor)
                // Entry 1: token=0x08000007, startOffset=5 -> count=1, ctorMethodIdx=1 (IsReadOnlyAttribute..ctor)
                // Entry 2 (sentinel): token=0, startOffset=10
                Span<byte> tmp4 = stackalloc byte[4];
                attrDataOrInfoMs.WriteByte(1);
                BinaryPrimitives.WriteInt32LittleEndian(tmp4, 0);
                attrDataOrInfoMs.Write(tmp4);
                attrDataOrInfoMs.WriteByte(1);
                BinaryPrimitives.WriteInt32LittleEndian(tmp4, 1);
                attrDataOrInfoMs.Write(tmp4);

                void WriteRange(uint token, uint startOff)
                {
                    Span<byte> r = stackalloc byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(r.Slice(0, 4), token);
                    BinaryPrimitives.WriteUInt32LittleEndian(r.Slice(4, 4), startOff);
                    attrRangeOrTypesMs.Write(r);
                }
                WriteRange(0x04000002, 0);
                WriteRange(0x08000007, 5);
                WriteRange(0, 10);
            }
            else
            {
                // Legacy v24: attributesInfo (12B: token, start, count) + attributeTypes (int32[])
                void WriteInfo(uint token, int start, int count)
                {
                    Span<byte> r = stackalloc byte[12];
                    BinaryPrimitives.WriteUInt32LittleEndian(r.Slice(0, 4), token);
                    BinaryPrimitives.WriteInt32LittleEndian(r.Slice(4, 4), start);
                    BinaryPrimitives.WriteInt32LittleEndian(r.Slice(8, 4), count);
                    attrDataOrInfoMs.Write(r);
                }
                WriteInfo(0x04000002, 0, 1);
                WriteInfo(0x08000007, 1, 1);

                Span<byte> tmp4 = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(tmp4, TySerializeField);
                attrRangeOrTypesMs.Write(tmp4);
                BinaryPrimitives.WriteInt32LittleEndian(tmp4, TyIsReadOnlyAttr);
                attrRangeOrTypesMs.Write(tmp4);
            }

            // TypeDefinitions (17 total)
            var tdMs = new MemoryStream();
            void WriteTd(
                int nameIdx, int nsIdx, int byvalIdx, int declaringIdx, int parentIdx, int elementIdx, int genContIdx,
                uint flags, int fStart, int mStart, int pStart,
                ushort mCount, ushort pCount, ushort fCount, uint bitfield, uint token)
            {
                var buf = new byte[tdSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), nsIdx);

                int byvalOff = isV24 ? 12 : 8;
                int declOff = isV24 ? 20 : 12;
                int parentOff = declOff + 4;
                int elemOff = declOff + 8;
                int gcOff = isV24 ? 40 : 24;
                int flagsOff = gcOff + 4;
                int fStartOff = gcOff + 8;
                int mStartOff = gcOff + 12;
                int eStartOff = gcOff + 16;
                int pStartOff = gcOff + 20;
                int nStartOff = gcOff + 24;
                int iStartOff = gcOff + 28;

                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(byvalOff, 4), byvalIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(declOff, 4), declaringIdx);
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

            WriteTd(sModule, sEmpty, 0, -1, -1, 0, -1, 0, -1, -1, -1, 0, 0, 0, 0, 0x02000001);
            WriteTd(sObject, sSystem, TyObject, -1, -1, TyObject, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x02000002);
            WriteTd(sVoid, sSystem, TyVoid, -1, TyObject, TyVoid, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000003);
            WriteTd(sBoolean, sSystem, TyBool, -1, TyObject, TyBool, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000004);
            WriteTd(sByte, sSystem, TyByte, -1, TyObject, TyByte, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000005);
            WriteTd(sInt32, sSystem, TyInt32, -1, TyObject, TyInt32, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000006);
            WriteTd(sSingle, sSystem, TySingle, -1, TyObject, TySingle, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000007);
            WriteTd(sString, sSystem, TyString, -1, TyObject, TyString, -1, 0x101, -1, -1, -1, 0, 0, 0, 0, 0x02000008);
            WriteTd(sList1, sScg, TyList1, -1, TyObject, TyList1, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x02000009);
            WriteTd(sDict2, sScg, TyDict2, -1, TyObject, TyDict2, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x0200000A);
            WriteTd(sNullable1, sSystem, TyNullable1, -1, TyObject, TyNullable1, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x0200000B);
            WriteTd(sSerializeField, sUnityEngine, TySerializeField, -1, TyObject, TySerializeField, -1, 1, -1, 0, -1, 1, 0, 0, 0, 0x0200000C);
            WriteTd(sIsReadOnlyAttr, sCompilerSvc, TyIsReadOnlyAttr, -1, TyObject, TyIsReadOnlyAttr, -1, 1, -1, 1, -1, 1, 0, 0, 0, 0x0200000D);
            WriteTd(sMonoBehaviour, sUnityEngine, TyMonoBehaviour, -1, TyObject, TyMonoBehaviour, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x0200000E);
            WriteTd(sLabel, sEmpty, TyLabel, -1, TyObject, TyLabel, -1, 1, -1, -1, -1, 0, 0, 0, 0, 0x0200000F);
            WriteTd(sUnitTypeID, sEmpty, TyUnitTypeID, TyLabel, TyObject, TyUnitTypeID, -1, 1, -1, -1, -1, 0, 0, 0, 1, 0x02000010);
            WriteTd(sRigCtrl, sEmpty, TyRigCtrl, -1, TyMonoBehaviour, TyRigCtrl, 0, 1, 0, 2, 0, 6, 1, 10, 0, 0x02000011);

            // Images: [0] mscorlib.dll (0..14), [1] Gameplay.Framework.dll (14..17, customAttrs [0..2))
            var imgMs = new MemoryStream();
            void WriteImg(int nameIdx, int asmIdx, int typeStart, int typeCount, int caStart, int caCount)
            {
                var buf = new byte[imgSize];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), nameIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), asmIdx);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), typeStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), typeCount);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(32, 4), caStart);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(36, 4), caCount);
                imgMs.Write(buf);
            }
            WriteImg(sMscorlibDll, 0, 0, 14, -1, 0);
            WriteImg(sGameDll, 1, 14, 3, 0, 2);

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
            if (isV24)
            {
                sections[21] = imgMs.ToArray();
                sections[27] = attrDataOrInfoMs.ToArray();
                sections[28] = attrRangeOrTypesMs.ToArray();
            }
            else
            {
                sections[20] = imgMs.ToArray();
                sections[24] = attrDataOrInfoMs.ToArray();
                sections[25] = attrRangeOrTypesMs.ToArray();
            }

            using (var outMs = new MemoryStream())
            {
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
                    if (sections[i].Length > 0) outMs.Write(sections[i]);
                }
                File.WriteAllBytes(metadataPath, outMs.ToArray());
            }

            // --- 2. Build matching synthetic native binary (PE64, PE32, or ELF64 with RELA) ---
            var ptrSize = is64 ? 8 : 4;
            ulong imageBase = isElf ? 0UL : (is64 ? 0x180000000UL : 0x10000000UL);
            ulong textRva = 0x1000UL;
            ulong dataRva = 0x2000UL;
            var dataBuf = new byte[0x4000];
            var relocs = new List<(ulong TargetVa, ulong ValueVa)>();
            var dataPos = 0x40;

            int Alloc(int size, int align = 8)
            {
                dataPos = (dataPos + align - 1) & ~(align - 1);
                var start = dataPos;
                dataPos += size;
                return start;
            }

            ulong DataVa(int offset) => imageBase + dataRva + (ulong)offset;

            void WritePtr(int offset, ulong targetVa)
            {
                if (isElf && targetVa != 0)
                {
                    // Leave on-disk bytes ZERO so tests prove RELA relocation overlay resolves every pointer!
                    relocs.Add((DataVa(offset), targetVa));
                    return;
                }

                if (is64) BinaryPrimitives.WriteUInt64LittleEndian(dataBuf.AsSpan(offset, 8), targetVa);
                else BinaryPrimitives.WriteUInt32LittleEndian(dataBuf.AsSpan(offset, 4), (uint)targetVa);
            }

            void WriteCount(int offset, int val)
            {
                BinaryPrimitives.WriteInt32LittleEndian(dataBuf.AsSpan(offset, 4), val);
            }

            uint MakeTypeBits(ushort attrs, byte typeEnum, bool byref = false, bool valuetypeFlag = false)
            {
                uint bits = attrs | ((uint)typeEnum << 16);
                if (!isV24)
                {
                    if (byref) bits |= 1u << 29;
                    if (valuetypeFlag) bits |= 1u << 31;
                }
                else
                {
                    if (byref) bits |= 1u << 30;
                }
                return bits;
            }

            ulong WriteIl2CppType(ulong dataWord, bool dataIsPointer, ushort attrs, byte typeEnum, bool byref = false, bool valuetypeFlag = false)
            {
                var off = Alloc(ptrSize + 8, ptrSize);
                if (dataIsPointer)
                {
                    WritePtr(off, dataWord);
                }
                else
                {
                    if (is64) BinaryPrimitives.WriteUInt64LittleEndian(dataBuf.AsSpan(off, 8), dataWord);
                    else BinaryPrimitives.WriteUInt32LittleEndian(dataBuf.AsSpan(off, 4), (uint)dataWord);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(dataBuf.AsSpan(off + ptrSize, 4), MakeTypeBits(attrs, typeEnum, byref, valuetypeFlag));
                return DataVa(off);
            }

            ulong WriteGenericClass(int genericDefTd, params ulong[] argTypeVas)
            {
                var argvOff = Alloc(argTypeVas.Length * ptrSize, ptrSize);
                for (var i = 0; i < argTypeVas.Length; i++)
                {
                    WritePtr(argvOff + i * ptrSize, argTypeVas[i]);
                }

                var instOff = Alloc(2 * ptrSize, ptrSize);
                WriteCount(instOff, argTypeVas.Length);
                WritePtr(instOff + ptrSize, DataVa(argvOff));

                var gcOff = Alloc(4 * ptrSize, ptrSize);
                if (isV24)
                {
                    WriteCount(gcOff, genericDefTd);
                }
                else
                {
                    var defTypeVa = WriteIl2CppType((ulong)genericDefTd, false, 0, genericDefTd == TyNullable1 ? (byte)0x11 : (byte)0x12);
                    WritePtr(gcOff, defTypeVa);
                }
                WritePtr(gcOff + ptrSize, DataVa(instOff));
                return DataVa(gcOff);
            }

            // Build all Il2CppType instances (0..32)
            var typeVas = new ulong[TotalBinaryTypes];
            typeVas[0] = WriteIl2CppType(0, false, 0, 0x12);
            typeVas[TyObject] = WriteIl2CppType(TyObject, false, 0, 0x1C);
            typeVas[TyVoid] = WriteIl2CppType(TyVoid, false, 0, 0x01, valuetypeFlag: true);
            typeVas[TyBool] = WriteIl2CppType(TyBool, false, 0, 0x02, valuetypeFlag: true);
            typeVas[TyByte] = WriteIl2CppType(TyByte, false, 0, 0x05, valuetypeFlag: true);
            typeVas[TyInt32] = WriteIl2CppType(TyInt32, false, 0, 0x08, valuetypeFlag: true); // bit 31 = 1 on v31, 0 on v24!
            typeVas[TySingle] = WriteIl2CppType(TySingle, false, 0, 0x0C, valuetypeFlag: true);
            typeVas[TyString] = WriteIl2CppType(TyString, false, 0, 0x0E);
            typeVas[TyList1] = WriteIl2CppType(TyList1, false, 0, 0x12);
            typeVas[TyDict2] = WriteIl2CppType(TyDict2, false, 0, 0x12);
            typeVas[TyNullable1] = WriteIl2CppType(TyNullable1, false, 0, 0x11, valuetypeFlag: true);
            typeVas[TySerializeField] = WriteIl2CppType(TySerializeField, false, 0, 0x12);
            typeVas[TyIsReadOnlyAttr] = WriteIl2CppType(TyIsReadOnlyAttr, false, 0, 0x12);
            typeVas[TyMonoBehaviour] = WriteIl2CppType(TyMonoBehaviour, false, 0, 0x12);
            typeVas[TyLabel] = WriteIl2CppType(TyLabel, false, 0, 0x12);
            typeVas[TyUnitTypeID] = WriteIl2CppType(TyUnitTypeID, false, 0, 0x11, valuetypeFlag: true);
            typeVas[TyRigCtrl] = WriteIl2CppType(TyRigCtrl, false, 0, 0x12);

            var gcListStr = WriteGenericClass(TyList1, typeVas[TyString]);
            var gcDictStrInt = WriteGenericClass(TyDict2, typeVas[TyString], typeVas[TyInt32]);
            var gcListInt = WriteGenericClass(TyList1, typeVas[TyInt32]);
            var typeListInt = WriteIl2CppType(gcListInt, true, 0, 0x15);
            var gcDictStrListInt = WriteGenericClass(TyDict2, typeVas[TyString], typeListInt);
            var gcNullableInt = WriteGenericClass(TyNullable1, typeVas[TyInt32]);

            var arrFloat2DOff = Alloc(ptrSize + 8, ptrSize);
            WritePtr(arrFloat2DOff, typeVas[TySingle]);
            dataBuf[arrFloat2DOff + ptrSize] = 2; // rank 2 -> float[,]

            typeVas[TyConstPrivateInt] = WriteIl2CppType(TyInt32, false, 0x8051, 0x08, valuetypeFlag: true);
            typeVas[TyPublicListString] = WriteIl2CppType(gcListStr, true, 0x0006, 0x15);
            typeVas[TyPrivateDictStrInt] = WriteIl2CppType(gcDictStrInt, true, 0x0001, 0x15);
            typeVas[TyPrivateBoolArray] = WriteIl2CppType(typeVas[TyBool], true, 0x0001, 0x1D);
            typeVas[TyPrivStatRoFloat2D] = WriteIl2CppType(DataVa(arrFloat2DOff), true, 0x0031, 0x14);
            typeVas[TyProtIntNotSerBytePtr] = WriteIl2CppType(typeVas[TyByte], true, 0x0085, 0x0F);
            typeVas[TyPrivateNestedDict] = WriteIl2CppType(gcDictStrListInt, true, 0x0001, 0x15);
            typeVas[TyPrivateUnitTypeID] = WriteIl2CppType(TyUnitTypeID, false, 0x0001, 0x11, valuetypeFlag: true);
            typeVas[TyPrivateNullableInt] = WriteIl2CppType(gcNullableInt, true, 0x0001, 0x15, valuetypeFlag: true);
            typeVas[TyPrivateVarTState] = WriteIl2CppType(0, false, 0x0001, 0x13);
            typeVas[TyDictStrIntParam] = WriteIl2CppType(gcDictStrInt, true, 0x0000, 0x15);
            typeVas[TyOutIntParam] = WriteIl2CppType(TyInt32, false, 0x0002, 0x08, byref: true, valuetypeFlag: true);
            typeVas[TyRefFloatParam] = WriteIl2CppType(TySingle, false, 0x0000, 0x0C, byref: true, valuetypeFlag: true);
            typeVas[TyInStringParam] = WriteIl2CppType(TyString, false, 0x0001, 0x0E, byref: true);
            typeVas[TyMvarTResult] = WriteIl2CppType(1, false, 0x0000, 0x1E);
            typeVas[TyCmodStringParam] = WriteIl2CppType(typeVas[TyString], true, 0x0000, 0x20);

            var typesTableOff = Alloc(TotalBinaryTypes * ptrSize, ptrSize);
            for (var i = 0; i < TotalBinaryTypes; i++)
            {
                WritePtr(typesTableOff + i * ptrSize, typeVas[i]);
            }

            // Field offsets for RigScaleControl`1 (TypeDef 16)
            var rigFieldOffsets = is64
                ? new[] { 0, 0x20, 0x28, 0x30, 0x0, 0x38, 0x40, 0x48, 0x50, 0x58 }
                : new[] { 0, 0x14, 0x18, 0x1C, 0x0, 0x20, 0x24, 0x28, 0x2C, 0x30 };
            var rigFoOff = Alloc(rigFieldOffsets.Length * 4, 4);
            for (var i = 0; i < rigFieldOffsets.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(dataBuf.AsSpan(rigFoOff + i * 4, 4), rigFieldOffsets[i]);
            }

            var fieldOffsetsTableOff = Alloc(TotalTypes * ptrSize, ptrSize);
            WritePtr(fieldOffsetsTableOff + TyRigCtrl * ptrSize, DataVa(rigFoOff));

            var tdSizesTableOff = Alloc(TotalTypes * ptrSize, ptrSize);

            // Il2CppMetadataRegistration (16 words)
            var metaRegOff = Alloc(16 * ptrSize, ptrSize);
            WriteCount(metaRegOff + 6 * ptrSize, TotalBinaryTypes);
            WritePtr(metaRegOff + 7 * ptrSize, DataVa(typesTableOff));
            WriteCount(metaRegOff + 10 * ptrSize, TotalTypes);
            WritePtr(metaRegOff + 11 * ptrSize, DataVa(fieldOffsetsTableOff));
            WriteCount(metaRegOff + 12 * ptrSize, TotalTypes);
            WritePtr(metaRegOff + 13 * ptrSize, DataVa(tdSizesTableOff));

            // CodeRegistration / CodeGenModules
            var methodRvas = new ulong[] { 0x1020, 0x1060, 0x1060, 0x1060, 0x10A0, 0 };
            if (!isV24)
            {
                ulong WriteCString(string text)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var off = Alloc(bytes.Length + 1, 4);
                    Buffer.BlockCopy(bytes, 0, dataBuf, off, bytes.Length);
                    dataBuf[off + bytes.Length] = 0;
                    return DataVa(off);
                }

                var mscorlibNameVa = WriteCString("mscorlib.dll");
                var gameDllNameVa = WriteCString("Gameplay.Framework.dll");

                var gameMethodPtrsOff = Alloc(methodRvas.Length * ptrSize, ptrSize);
                for (var i = 0; i < methodRvas.Length; i++)
                {
                    WritePtr(gameMethodPtrsOff + i * ptrSize, methodRvas[i] == 0 ? 0 : imageBase + methodRvas[i]);
                }

                var mod0Off = Alloc(4 * ptrSize, ptrSize);
                WritePtr(mod0Off, mscorlibNameVa);
                WriteCount(mod0Off + ptrSize, 0);

                var mod1Off = Alloc(4 * ptrSize, ptrSize);
                WritePtr(mod1Off, gameDllNameVa);
                WriteCount(mod1Off + ptrSize, methodRvas.Length);
                WritePtr(mod1Off + 2 * ptrSize, DataVa(gameMethodPtrsOff));

                var modsArrayOff = Alloc(2 * ptrSize, ptrSize);
                WritePtr(modsArrayOff, DataVa(mod0Off));
                WritePtr(modsArrayOff + ptrSize, DataVa(mod1Off));

                var codeRegTailOff = Alloc(2 * ptrSize, ptrSize);
                WriteCount(codeRegTailOff, 2);
                WritePtr(codeRegTailOff + ptrSize, DataVa(modsArrayOff));
            }
            else
            {
                // Pre-24.2 flat Il2CppCodeRegistration
                var flatMethodPtrsOff = Alloc(5 * ptrSize, ptrSize);
                for (var i = 0; i < 5; i++)
                {
                    WritePtr(flatMethodPtrsOff + i * ptrSize, imageBase + methodRvas[i]);
                }
                var invokersOff = Alloc(2 * ptrSize, ptrSize);
                WritePtr(invokersOff, imageBase + textRva + 0x200);
                WritePtr(invokersOff + ptrSize, imageBase + textRva + 0x220);

                var codeRegOff = Alloc(12 * ptrSize, ptrSize);
                WriteCount(codeRegOff + 0 * ptrSize, 5);
                WritePtr(codeRegOff + 1 * ptrSize, DataVa(flatMethodPtrsOff));
                WriteCount(codeRegOff + 6 * ptrSize, 2);
                WritePtr(codeRegOff + 7 * ptrSize, DataVa(invokersOff));
            }

            if (!isElf)
            {
                WriteSyntheticPeBinary(binaryPath, is64, imageBase, (uint)textRva, (uint)dataRva, dataBuf);
            }
            else
            {
                WriteSyntheticElf64AArch64Binary(binaryPath, textRva, dataRva, dataBuf, relocs, stripSectionHeaders: stripSh);
            }
        }

        private static void WriteSyntheticPeBinary(string path, bool is64, ulong imageBase, uint textRva, uint dataRva, byte[] dataSection)
        {
            const uint fileAlign = 0x200;
            const uint sectionAlign = 0x1000;
            const uint textFileOff = 0x400;
            const uint textSize = 0x400;
            uint dataFileOff = textFileOff + textSize;
            uint dataSize = (uint)((dataSection.Length + fileAlign - 1) & ~(fileAlign - 1));
            var totalBytes = new byte[dataFileOff + dataSize];

            // DOS header
            totalBytes[0] = (byte)'M';
            totalBytes[1] = (byte)'Z';
            const int peOffset = 0x80;
            BinaryPrimitives.WriteInt32LittleEndian(totalBytes.AsSpan(0x3C, 4), peOffset);

            // PE signature
            totalBytes[peOffset] = (byte)'P';
            totalBytes[peOffset + 1] = (byte)'E';

            var optHeaderSize = is64 ? (ushort)0xF0 : (ushort)0xE0;
            BinaryPrimitives.WriteUInt16LittleEndian(totalBytes.AsSpan(peOffset + 4, 2), is64 ? (ushort)0x8664 : (ushort)0x014C);
            BinaryPrimitives.WriteUInt16LittleEndian(totalBytes.AsSpan(peOffset + 6, 2), 2); // 2 sections (.text, .data)
            BinaryPrimitives.WriteUInt16LittleEndian(totalBytes.AsSpan(peOffset + 20, 2), optHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(totalBytes.AsSpan(peOffset + 22, 2), 0x2022);

            var optOff = peOffset + 24;
            BinaryPrimitives.WriteUInt16LittleEndian(totalBytes.AsSpan(optOff, 2), is64 ? (ushort)0x020B : (ushort)0x010B);
            if (is64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(totalBytes.AsSpan(optOff + 24, 8), imageBase);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 108, 4), 16);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 28, 4), (uint)imageBase);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 92, 4), 16);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 32, 4), sectionAlign);
            BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 36, 4), fileAlign);
            var sizeOfImage = dataRva + ((dataSize + sectionAlign - 1) & ~(sectionAlign - 1));
            BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 56, 4), sizeOfImage);
            BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(optOff + 60, 4), textFileOff);

            void WriteSectionHeader(int idx, string name, uint virtSize, uint virtAddr, uint rawSize, uint rawPtr, uint chars)
            {
                var off = optOff + optHeaderSize + idx * 40;
                Encoding.ASCII.GetBytes(name).CopyTo(totalBytes, off);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(off + 8, 4), virtSize);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(off + 12, 4), virtAddr);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(off + 16, 4), rawSize);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(off + 20, 4), rawPtr);
                BinaryPrimitives.WriteUInt32LittleEndian(totalBytes.AsSpan(off + 36, 4), chars);
            }

            WriteSectionHeader(0, ".text", textSize, textRva, textSize, textFileOff, 0x60000020);
            WriteSectionHeader(1, ".data", (uint)dataSection.Length, dataRva, dataSize, dataFileOff, 0xC0000040);

            // Populate minimal RET instructions in .text and copy .data payload
            Array.Fill(totalBytes, (byte)0xC3, (int)textFileOff, (int)textSize);
            Buffer.BlockCopy(dataSection, 0, totalBytes, (int)dataFileOff, dataSection.Length);
            File.WriteAllBytes(path, totalBytes);
        }

        private static void WriteSyntheticElf64AArch64Binary(
            string path,
            ulong textVaddr,
            ulong dataVaddr,
            byte[] dataSection,
            List<(ulong TargetVa, ulong ValueVa)> relocs,
            bool stripSectionHeaders)
        {
            // Layout:
            // 0x0000..0x0FFF: ELF64 header (64B) + 3 Phdrs (3*56B) + .rela.dyn + .dynamic
            // 0x1000..0x1FFF: PT_LOAD[0] RX (.text)
            // 0x2000..0x5FFF: PT_LOAD[1] RW (.data.rel.ro + .data)
            // 0x6000..      : optional Shdrs (when !stripSectionHeaders)
            const ulong relaOff = 0x200;
            var relaSize = (ulong)(relocs.Count * 24);
            var dynOff = (relaOff + relaSize + 15UL) & ~15UL;
            const ulong dynSize = 4 * 16; // DT_RELA, DT_RELASZ, DT_RELAENT, DT_NULL
            const ulong textOff = 0x1000;
            const ulong textSize = 0x1000;
            const ulong dataOff = 0x2000;
            var shOff = dataOff + (ulong)dataSection.Length;
            var totalLen = stripSectionHeaders ? shOff : shOff + 2 * 64;
            var buf = new byte[totalLen];

            // ELF64 header
            buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
            buf[4] = 2; // ELFCLASS64
            buf[5] = 1; // ELFDATA2LSB
            buf[6] = 1; // EV_CURRENT
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), 3);   // ET_DYN
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18, 2), 183); // EM_AARCH64
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 1);   // EV_CURRENT
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(32, 8), 64);  // e_phoff
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(40, 8), stripSectionHeaders ? 0UL : shOff);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(52, 2), 64);  // e_ehsize
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(54, 2), 56);  // e_phentsize
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(56, 2), 3);   // e_phnum (LOAD header+text, LOAD data, DYNAMIC)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(58, 2), 64);  // e_shentsize
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(60, 2), stripSectionHeaders ? (ushort)0 : (ushort)2);

            void WritePhdr(int idx, uint type, uint flags, ulong offset, ulong vaddr, ulong filesz, ulong memsz)
            {
                var off = 64 + idx * 56;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), type);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4, 4), flags);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 8, 8), offset);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 16, 8), vaddr);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 24, 8), vaddr);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 32, 8), filesz);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 40, 8), memsz);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 48, 8), 0x1000);
            }

            // PT_LOAD[0] RX covers 0x0000..0x2000 (header + .rela.dyn + .dynamic + .text at 0x1000)
            WritePhdr(0, 1, 5, 0, 0, textOff + textSize, textVaddr + textSize);
            // PT_LOAD[1] RW covers .data at 0x2000
            WritePhdr(1, 1, 6, dataOff, dataVaddr, (ulong)dataSection.Length, (ulong)dataSection.Length);
            // PT_DYNAMIC
            WritePhdr(2, 2, 6, dynOff, dynOff, dynSize, dynSize);

            // .rela.dyn entries (R_AARCH64_RELATIVE = 1027)
            for (var i = 0; i < relocs.Count; i++)
            {
                var off = (int)relaOff + i * 24;
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off, 8), relocs[i].TargetVa);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 8, 8), 1027UL);
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 16, 8), (long)relocs[i].ValueVa);
            }

            // .dynamic entries
            void WriteDyn(int idx, long tag, ulong val)
            {
                var off = (int)dynOff + idx * 16;
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off, 8), tag);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 8, 8), val);
            }
            WriteDyn(0, 7, relaOff);  // DT_RELA
            WriteDyn(1, 8, relaSize); // DT_RELASZ
            WriteDyn(2, 9, 24);       // DT_RELAENT
            WriteDyn(3, 0, 0);        // DT_NULL

            Buffer.BlockCopy(dataSection, 0, buf, (int)dataOff, dataSection.Length);

            if (!stripSectionHeaders)
            {
                // Section 0 = NULL, Section 1 = SHT_RELA (type 4)
                var s1 = (int)shOff + 64;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(s1 + 4, 4), 4);       // SHT_RELA
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s1 + 16, 8), relaOff);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s1 + 24, 8), relaOff);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s1 + 32, 8), relaSize);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s1 + 56, 8), 24);
            }

            File.WriteAllBytes(path, buf);
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
