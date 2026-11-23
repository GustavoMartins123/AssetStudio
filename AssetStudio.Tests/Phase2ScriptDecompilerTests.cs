using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
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

            // ViewSelectedScriptCommand switches active tab headlessly
            await vm.ViewSelectedScriptCommand.ExecuteAsync();
            Assert.Equal(RightPanelTab.Script, session.ActiveRightTab);
            Assert.True(session.IsScriptTabActive);

            // Double Reset() emits zero PropertyChanged events
            session.Reset();
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
