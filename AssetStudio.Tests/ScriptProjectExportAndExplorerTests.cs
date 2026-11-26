using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests;

public class ScriptProjectExportAndExplorerTests
{
    #region UnityProjectGuidUtility Tests

    [Fact]
    public void UnityProjectGuidUtility_ComputeScriptGuid_MatchesExactSha256ContractAndNormalizesDllSuffix()
    {
        var guidWithDll = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp.dll", "Game.Combat", "PlayerController");
        var guidWithoutDll = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp", "Game.Combat", "PlayerController");
        var guidWithPath = UnityProjectGuidUtility.ComputeScriptGuid(@"C:\Game\Managed\ASSEMBLY-CSHARP.DLL", " Game.Combat ", " PlayerController ");

        Assert.Equal(32, guidWithDll.Length);
        Assert.Equal(guidWithDll, guidWithDll.ToLowerInvariant());
        Assert.Equal(guidWithDll, guidWithoutDll);
        Assert.Equal(guidWithDll, guidWithPath);

        // Verify bit-for-bit against independent SHA-256 contract
        var expectedPayload = "unity-script|assembly-csharp|Game.Combat|PlayerController";
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedPayload));
        var expectedHex = Convert.ToHexString(expectedHash.AsSpan(0, 16)).ToLowerInvariant();
        Assert.Equal(expectedHex, guidWithDll);

        // Different namespace or class produces distinct GUID
        var otherGuid = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp", "Game.UI", "PlayerController");
        Assert.NotEqual(guidWithDll, otherGuid);
    }

    [Fact]
    public void UnityProjectGuidUtility_ComputeAssetGuid_AndVisualStudioProjectGuid_AreDeterministic()
    {
        var assetGuid1 = UnityProjectGuidUtility.ComputeAssetGuid("Prefab", "level0", -42424242L, "Hero");
        var assetGuid2 = UnityProjectGuidUtility.ComputeAssetGuid(" prefab ", "LEVEL0", -42424242L, " Hero ");
        Assert.Equal(32, assetGuid1.Length);
        Assert.Equal(assetGuid1, assetGuid2);

        var expectedPayload = "unity-asset|prefab|level0|-42424242|Hero";
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedPayload));
        var expectedHex = Convert.ToHexString(expectedHash.AsSpan(0, 16)).ToLowerInvariant();
        Assert.Equal(expectedHex, assetGuid1);

        var projGuid1 = UnityProjectGuidUtility.ComputeVisualStudioProjectGuid("Assembly-CSharp.dll");
        var projGuid2 = UnityProjectGuidUtility.ComputeVisualStudioProjectGuid("assembly-csharp");
        Assert.Equal(projGuid1, projGuid2);
        var formatted = UnityProjectGuidUtility.FormatVisualStudioGuid(projGuid1);
        Assert.StartsWith("{", formatted, StringComparison.Ordinal);
        Assert.EndsWith("}", formatted, StringComparison.Ordinal);
        Assert.Equal(formatted, formatted.ToUpperInvariant());

        var meta = UnityProjectGuidUtility.BuildMonoScriptMetaContent(assetGuid1);
        Assert.Contains($"guid: {assetGuid1}", meta, StringComparison.Ordinal);
        Assert.Contains("MonoImporter:", meta, StringComparison.Ordinal);
    }

    #endregion

    #region ScriptProjectAssemblyClassifier Tests

    [Theory]
    [InlineData("Assembly-CSharp", ScriptAssemblyCategory.Game, true)]
    [InlineData("Assembly-CSharp.dll", ScriptAssemblyCategory.Game, true)]
    [InlineData("Assembly-CSharp-firstpass", ScriptAssemblyCategory.Game, true)]
    [InlineData("Assembly-UnityScript", ScriptAssemblyCategory.Game, true)]
    [InlineData("Game.Combat.dll", ScriptAssemblyCategory.Game, false)]
    [InlineData("mscorlib.dll", ScriptAssemblyCategory.Framework, false)]
    [InlineData("netstandard", ScriptAssemblyCategory.Framework, false)]
    [InlineData("System.Core.dll", ScriptAssemblyCategory.Framework, false)]
    [InlineData("Microsoft.CSharp", ScriptAssemblyCategory.Framework, false)]
    [InlineData("Mono.Security", ScriptAssemblyCategory.Framework, false)]
    [InlineData("UnityEngine", ScriptAssemblyCategory.UnityEngine, false)]
    [InlineData("UnityEngine.CoreModule.dll", ScriptAssemblyCategory.UnityEngine, false)]
    [InlineData("UnityEngine.UI", ScriptAssemblyCategory.UnityEngine, false)]
    [InlineData("Unity.TextMeshPro", ScriptAssemblyCategory.UnityEngine, false)]
    [InlineData("UnityEditor.CoreModule", ScriptAssemblyCategory.UnityEngine, false)]
    [InlineData("com.unity.cinemachine", ScriptAssemblyCategory.UnityEngine, false)]
    [InlineData("Newtonsoft.Json.dll", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("DOTween", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("UniTask.dll", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("Il2CppDummyDll", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("Sirenix.OdinInspector.Attributes", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("FMODUnity", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("Firebase.App", ScriptAssemblyCategory.ThirdParty, false)]
    public void ScriptProjectAssemblyClassifier_ClassifiesGameEngineFrameworkAndThirdPartyAccurately(
        string assemblyName,
        ScriptAssemblyCategory expectedCategory,
        bool expectedPredefined)
    {
        Assert.Equal(expectedCategory, ScriptProjectAssemblyClassifier.Classify(assemblyName));
        Assert.Equal(expectedCategory == ScriptAssemblyCategory.Game, ScriptProjectAssemblyClassifier.IsGameAssembly(assemblyName));
        Assert.Equal(expectedPredefined, ScriptProjectAssemblyClassifier.IsUnityPredefinedAssembly(assemblyName));
    }

    #endregion

    #region Dependency Graph & Path Sanitizer Tests

    [Fact]
    public void ScriptDependencyGraphResolver_BreaksCyclesDeterministically_AndPreservesDiamondDag()
    {
        // Cyclic graph: A -> B -> C -> A, plus self-reference A -> A, plus acyclic leaf D
        var cyclicInput = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AssemblyA"] = new[] { "AssemblyA", "AssemblyB", "AssemblyD" },
            ["AssemblyB"] = new[] { "AssemblyC" },
            ["AssemblyC"] = new[] { "AssemblyA", "AssemblyD" },
            ["AssemblyD"] = Array.Empty<string>()
        };

        var resolved = ScriptDependencyGraphResolver.Resolve(cyclicInput);
        Assert.True(resolved.BrokenCycles.Count >= 2); // self-ref + back-edge C -> A
        Assert.Contains(resolved.BrokenCycles, c => c.FromAssembly == "AssemblyC" && c.ToAssembly == "AssemblyA");
        Assert.DoesNotContain("AssemblyA", resolved.ProjectReferencesByAssembly["AssemblyC"]);
        Assert.Contains("AssemblyA", resolved.CycleFallbackDllReferencesByAssembly["AssemblyC"]);
        Assert.Contains("AssemblyB", resolved.ProjectReferencesByAssembly["AssemblyA"]);
        Assert.Contains("AssemblyC", resolved.ProjectReferencesByAssembly["AssemblyB"]);

        // Verify output ProjectReferences graph is strictly a DAG (Kahn's topological sort succeeds)
        AssertIsAcyclicDag(resolved.ProjectReferencesByAssembly);

        // Diamond DAG: Top -> Left, Top -> Right, Left -> Base, Right -> Base (ZERO broken cycles!)
        var diamondInput = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Top"] = new[] { "Left", "Right" },
            ["Left"] = new[] { "Base" },
            ["Right"] = new[] { "Base" },
            ["Base"] = Array.Empty<string>()
        };

        var diamond = ScriptDependencyGraphResolver.Resolve(diamondInput);
        Assert.Empty(diamond.BrokenCycles);
        Assert.Equal(2, diamond.ProjectReferencesByAssembly["Top"].Count);
        AssertIsAcyclicDag(diamond.ProjectReferencesByAssembly);
    }

    [Fact]
    public void ScriptExportPathSanitizer_HandlesReservedNamesGenericArityCollisionsLongPathsAndTraversal()
    {
        // Reserved Windows device names
        Assert.Equal("_CON", ScriptExportPathSanitizer.SanitizeSegment("CON"));
        Assert.Equal("_aux", ScriptExportPathSanitizer.SanitizeSegment("aux"));
        Assert.Equal("_NUL.foo", ScriptExportPathSanitizer.SanitizeSegment("NUL.foo"));
        Assert.Equal("_COM1", ScriptExportPathSanitizer.SanitizeSegment("COM1"));
        Assert.Equal("_LPT9", ScriptExportPathSanitizer.SanitizeSegment("LPT9"));

        // Generic arity and invalid chars
        Assert.Equal("Repository_1", ScriptExportPathSanitizer.SanitizeSegment("Repository`1"));
        Assert.Equal("A_B_C", ScriptExportPathSanitizer.SanitizeSegment("A:B*C"));

        // Case-insensitive collision dedup
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var p1 = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath("Game.Items", "Item", used);
        var p2 = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath("Game.Items", "item", used);
        var p3 = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath("Game.Items", "ITEM", used);
        Assert.Equal(Path.Combine("Game", "Items", "Item.cs"), p1);
        Assert.Equal(Path.Combine("Game", "Items", "item_2.cs"), p2);
        Assert.Equal(Path.Combine("Game", "Items", "ITEM_3.cs"), p3);

        // Deep long namespace collapsed safely <= 200 chars
        var deepNs = string.Join(".", Enumerable.Range(0, 20).Select(i => $"VeryLongNamespaceSegmentNumber{i:D2}"));
        var longRel = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath(deepNs, "DeepComponent", used);
        Assert.True(longRel.Length <= 200, $"Expected collapsed relative path <= 200 chars, got {longRel.Length}: {longRel}");
        Assert.Contains("_ns_", longRel, StringComparison.Ordinal);

        // Traversal segments sanitized and root confinement enforced
        var tempRoot = CreateTempDirectory();
        try
        {
            var traversalRel = ScriptExportPathSanitizer.BuildUniqueScriptRelativePath("..\\.\\Evil", "../Escape", used);
            var confined = ScriptExportPathSanitizer.EnsureConfinedPath(tempRoot, traversalRel);
            Assert.StartsWith(Path.GetFullPath(tempRoot), confined, StringComparison.OrdinalIgnoreCase);

            Assert.Throws<SecurityException>(() =>
                ScriptExportPathSanitizer.EnsureConfinedPath(tempRoot, Path.Combine("..", "..", "outside.cs")));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    #endregion

    #region End-to-End Multi-Assembly Export + Real `dotnet build` Coherence Test

    [Fact]
    public async Task CSharpProjectExporterService_ExportsRealMultiAssemblyFixture_AndGeneratedSolutionBuildsCleanlyWithDotnetBuild()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            // 1. Compile a real 3-assembly fixture (UnityEngine.CoreModule + GameCore + GameApp -> GameCore & UnityEngine.CoreModule)
            var fixtureDir = Path.Combine(tempRoot, "FixtureSrc");
            var managedDir = Path.Combine(tempRoot, "SampleGame_Data", "Managed");
            Directory.CreateDirectory(managedDir);

            await BuildSyntheticMultiAssemblyFixtureAsync(fixtureDir, managedDir);

            Assert.True(File.Exists(Path.Combine(managedDir, "UnityEngine.CoreModule.dll")));
            Assert.True(File.Exists(Path.Combine(managedDir, "GameCore.dll")));
            Assert.True(File.Exists(Path.Combine(managedDir, "GameApp.dll")));

            // 2. Export in StandaloneDotNetSolution mode (excluding engine DLLs -> moved to _References)
            var slnOutDir = Path.Combine(tempRoot, "ExportedSolution");
            var exporter = new CSharpProjectExporterService();
            var progressReports = new List<CSharpProjectExportProgress>();
            var progress = new Progress<CSharpProjectExportProgress>(p =>
            {
                lock (progressReports) { progressReports.Add(p); }
            });

            var result = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = slnOutDir,
                ProjectName = "SampleGameScripts",
                LayoutMode = CSharpProjectLayoutMode.StandaloneDotNetSolution,
                IncludeEngineAndThirdPartyAssemblies = false,
                GenerateUnityMetaFiles = true,
                CopyReferenceDlls = true,
                ManagedDirectories = new[] { managedDir }
            }, progress);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("MonoManaged", result.Mode);
            Assert.Equal(CSharpProjectExportSourceMode.MonoManagedAssemblies, result.SourceMode);
            Assert.Equal(2, result.GameAssembliesCount);
            Assert.Equal(1, result.ReferenceAssembliesCount); // UnityEngine.CoreModule copied to _References
            Assert.Equal(0, result.FailedTypesCount);
            Assert.Empty(result.BrokenCycles);
            Assert.True(File.Exists(result.SolutionPath));
            Assert.True(File.Exists(result.ReportPath));

            // UnityEngine.CoreModule must NOT be decompiled into src/, MUST be copied into _References/
            Assert.False(Directory.Exists(Path.Combine(slnOutDir, "src", "UnityEngine.CoreModule")));
            Assert.True(File.Exists(Path.Combine(slnOutDir, "_References", "UnityEngine.CoreModule.dll")));

            // GameApp.csproj must have ProjectReference to GameCore.csproj and Reference to _References\UnityEngine.CoreModule.dll
            var gameAppCsproj = File.ReadAllText(Path.Combine(slnOutDir, "src", "GameApp", "GameApp.csproj"));
            Assert.Contains(@"<ProjectReference Include=""..\GameCore\GameCore.csproj"" />", gameAppCsproj, StringComparison.Ordinal);
            Assert.Contains("UnityEngine.CoreModule", gameAppCsproj, StringComparison.Ordinal);
            Assert.Contains("_References", gameAppCsproj, StringComparison.Ordinal);

            // Nested type inside PlayerController must NOT be emitted as a standalone file
            var allCsFiles = Directory.GetFiles(Path.Combine(slnOutDir, "src"), "*.cs", SearchOption.AllDirectories);
            Assert.DoesNotContain(allCsFiles, f => Path.GetFileName(f).Contains("NestedLoadout", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(allCsFiles, f => Path.GetFileName(f).Equals("PlayerController.cs", StringComparison.OrdinalIgnoreCase));

            // Verify .cs.meta GUID matches UnityProjectGuidUtility bit-for-bit AND matches export-report.json
            var playerCsPath = allCsFiles.First(f => Path.GetFileName(f).Equals("PlayerController.cs", StringComparison.OrdinalIgnoreCase));
            var playerMetaPath = playerCsPath + ".meta";
            Assert.True(File.Exists(playerMetaPath));
            var expectedPlayerGuid = UnityProjectGuidUtility.ComputeScriptGuid("GameApp", "Game.App", "PlayerController");
            Assert.Contains($"guid: {expectedPlayerGuid}", File.ReadAllText(playerMetaPath), StringComparison.Ordinal);
            Assert.Equal(expectedPlayerGuid, result.ScriptGuidMap["GameApp|Game.App.PlayerController"]);

            using (var reportDoc = JsonDocument.Parse(File.ReadAllText(result.ReportPath)))
            {
                Assert.Equal("MonoManaged", reportDoc.RootElement.GetProperty("mode").GetString());
                Assert.Equal(2, reportDoc.RootElement.GetProperty("gameAssembliesCount").GetInt32());
                Assert.Equal(expectedPlayerGuid, reportDoc.RootElement.GetProperty("scriptGuidMap").GetProperty("GameApp|Game.App.PlayerController").GetString());
            }

            // 3. REAL `dotnet build` on the exported solution!
            var (buildExit, buildStdout, buildStderr) = await RunProcessAsync("dotnet", $"build \"{result.SolutionPath}\" --nologo -v minimal", slnOutDir);
            Assert.True(buildExit == 0, $"Exported solution failed `dotnet build` (exit {buildExit}):\nSTDOUT:\n{buildStdout}\nSTDERR:\n{buildStderr}");

            // 4. Also verify UnityAssetsScripts layout mode (.asmdef + .cs.meta, skipping .asmdef for Assembly-CSharp!)
            // Copy GameApp.dll as Assembly-CSharp.dll in a second folder to test predefined assembly handling
            var unityManagedDir = Path.Combine(tempRoot, "UnityManaged");
            Directory.CreateDirectory(unityManagedDir);
            File.Copy(Path.Combine(managedDir, "UnityEngine.CoreModule.dll"), Path.Combine(unityManagedDir, "UnityEngine.CoreModule.dll"));
            File.Copy(Path.Combine(managedDir, "GameCore.dll"), Path.Combine(unityManagedDir, "GameCore.dll"));
            await BuildAssemblyCSharpStubAsync(Path.Combine(tempRoot, "AsmCSharpSrc"), unityManagedDir);

            var unityOutDir = Path.Combine(tempRoot, "UnityAssetsScripts");
            var unityResult = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = unityOutDir,
                LayoutMode = CSharpProjectLayoutMode.UnityAssetsScripts,
                IncludeEngineAndThirdPartyAssemblies = false,
                GenerateAsmdefFiles = true,
                GenerateUnityMetaFiles = true,
                ManagedDirectories = new[] { unityManagedDir }
            });

            Assert.True(unityResult.Success, unityResult.ErrorMessage);
            Assert.Empty(unityResult.SolutionPath);
            Assert.Empty(Directory.GetFiles(unityOutDir, "*.csproj", SearchOption.AllDirectories));

            // GameCore gets .asmdef + .asmdef.meta, Assembly-CSharp does NOT get .asmdef
            Assert.True(File.Exists(Path.Combine(unityOutDir, "GameCore", "GameCore.asmdef")));
            Assert.True(File.Exists(Path.Combine(unityOutDir, "GameCore", "GameCore.asmdef.meta")));
            Assert.True(File.Exists(Path.Combine(unityOutDir, "GameCore.meta")));
            Assert.False(File.Exists(Path.Combine(unityOutDir, "Assembly-CSharp", "Assembly-CSharp.asmdef")));
            Assert.True(File.Exists(Path.Combine(unityOutDir, "Assembly-CSharp", "Game", "Entry", "BootRunner.cs")));
            Assert.True(File.Exists(Path.Combine(unityOutDir, "Assembly-CSharp", "Game", "Entry", "BootRunner.cs.meta")));

            var expectedBootGuid = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp", "Game.Entry", "BootRunner");
            Assert.Contains(
                $"guid: {expectedBootGuid}",
                File.ReadAllText(Path.Combine(unityOutDir, "Assembly-CSharp", "Game", "Entry", "BootRunner.cs.meta")),
                StringComparison.Ordinal);

            // 5. Verify AssemblyExplorerService scans the real multi-assembly directory fast & accurately
            var explorer = new AssemblyExplorerService();
            var catalog = explorer.ScanDirectories(new[] { managedDir });
            Assert.Equal(3, catalog.Assemblies.Count);

            var playerType = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "PlayerController");
            Assert.NotNull(playerType);
            Assert.True(playerType!.IsGameAssembly);
            Assert.True(playerType.IsMonoBehaviour);
            Assert.False(playerType.IsScriptableObject);
            Assert.Equal("MB", playerType.KindBadge);
            Assert.Equal(expectedPlayerGuid, playerType.ScriptGuid);

            var indirectMbType = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "BossController");
            Assert.NotNull(indirectMbType);
            Assert.True(indirectMbType!.IsMonoBehaviour); // inherited via PlayerController -> MonoBehaviour

            var soType = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "WeaponConfig");
            Assert.NotNull(soType);
            Assert.True(soType!.IsScriptableObject);
            Assert.Equal("SO", soType.KindBadge);

            var enumType = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "DamageKind");
            Assert.NotNull(enumType);
            Assert.Equal(AssemblyTypeKind.Enum, enumType!.Kind);

            var structType = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "DamagePacket");
            Assert.NotNull(structType);
            Assert.Equal(AssemblyTypeKind.Struct, structType!.Kind);

            var ifaceType = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "IDamageable");
            Assert.NotNull(ifaceType);
            Assert.Equal(AssemblyTypeKind.Interface, ifaceType!.Kind);

            // Nested type & compiler-generated types excluded from Explorer
            Assert.DoesNotContain(catalog.AllTypes, t => t.ClassName.Contains("NestedLoadout", StringComparison.Ordinal));
            Assert.DoesNotContain(catalog.AllTypes, t => t.ClassName == "<Module>");

            // Filter by game-only hides UnityEngine.CoreModule; showEngineAssemblies=true includes it
            var gameOnly = catalog.Filter(query: null, showEngineAssemblies: false);
            Assert.DoesNotContain(gameOnly.Items, t => t.AssemblyName == "UnityEngine.CoreModule");
            var withEngine = catalog.Filter(query: "MonoBehaviour", showEngineAssemblies: true);
            Assert.Contains(withEngine.Items, t => t.ClassName == "MonoBehaviour" && t.AssemblyName == "UnityEngine.CoreModule");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    #endregion

    #region IL2CPP Fallback Tests (dump.cs, DummyDll + script.json, global-metadata.dat)

    [Fact]
    public async Task CSharpProjectExporterService_Il2CppDumpCsAndScriptJsonFallback_EmitsIl2CppStubsWithRvas()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var dumpPath = Path.Combine(tempRoot, "dump.cs");
            var scriptJsonPath = Path.Combine(tempRoot, "script.json");

            var dumpContent = """
// Image 0: UnityEngine.CoreModule.dll - 0
// Namespace: UnityEngine
public class MonoBehaviour // TypeDefIndex: 1
{
    // Methods
    public void StopAllCoroutines() { }
}

// Image 1: Assembly-CSharp.dll - 100
// Namespace: Game.Combat
[Serializable]
public class CombatActor : MonoBehaviour // TypeDefIndex: 101
{
    // Fields
    public int MaxHealth; // 0x18

    // Methods
    // RVA: 0x104000 Offset: 0x103400 VA: 0x180104000
    public void ApplyHit(int damage) { }
}
""";
            File.WriteAllText(dumpPath, dumpContent, new UTF8Encoding(false));

            var scriptJson = """
{
  "ScriptMethod": [
    {
      "Address": 1064960,
      "Name": "Game.Combat.CombatActor$$ApplyHit",
      "Signature": "void Game_Combat_CombatActor__ApplyHit(void* __this, int32_t damage);"
    }
  ]
}
""";
            File.WriteAllText(scriptJsonPath, scriptJson, new UTF8Encoding(false));

            // Explorer scans dump.cs cleanly
            var explorer = new AssemblyExplorerService();
            var catalog = explorer.ScanDumpCs(dumpPath);
            Assert.Equal(2, catalog.Assemblies.Count);
            var combatActor = catalog.AllTypes.FirstOrDefault(t => t.ClassName == "CombatActor");
            Assert.NotNull(combatActor);
            Assert.True(combatActor!.IsGameAssembly);
            Assert.True(combatActor.IsMonoBehaviour);
            Assert.Equal(1, combatActor.FieldCount);
            Assert.Equal(1, combatActor.MethodCount);

            // Exporter falls back cleanly to dump.cs + script.json RVA comments
            var outDir = Path.Combine(tempRoot, "OutDump");
            var exporter = new CSharpProjectExporterService();
            var result = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = outDir,
                ProjectName = "Il2CppDumpExport",
                DumpCsPath = dumpPath,
                ScriptJsonPath = scriptJsonPath,
                IncludeEngineAndThirdPartyAssemblies = false,
                GenerateUnityMetaFiles = true
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("Il2CppStubs", result.Mode);
            Assert.Equal(CSharpProjectExportSourceMode.Il2CppDumpCs, result.SourceMode);
            Assert.Equal(1, result.GameAssembliesCount); // UnityEngine.CoreModule excluded
            Assert.Equal(1, result.TotalTypesExported);
            Assert.True(File.Exists(result.SolutionPath));

            var exportedCs = Path.Combine(outDir, "src", "Assembly-CSharp", "Game", "Combat", "CombatActor.cs");
            Assert.True(File.Exists(exportedCs));
            var content = File.ReadAllText(exportedCs);
            Assert.Contains("IL2CPP dump.cs stub", content, StringComparison.Ordinal);
            Assert.Contains("0x00104000", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Game.Combat.CombatActor$$ApplyHit", content, StringComparison.Ordinal);

            using var reportDoc = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
            Assert.Equal("Il2CppStubs", reportDoc.RootElement.GetProperty("mode").GetString());
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task CSharpProjectExporterService_Il2CppDummyDllWithScriptJson_EmitsDummyHeaderAndRvas()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var dummyDir = Path.Combine(tempRoot, "DummyDll");
            Directory.CreateDirectory(dummyDir);
            File.Copy(typeof(MonoScript).Assembly.Location, Path.Combine(dummyDir, "AssetStudio.dll"));

            var scriptJsonPath = Path.Combine(tempRoot, "script.json");
            File.WriteAllText(scriptJsonPath, """
{
  "ScriptMethod": [
    {
      "Address": 4660,
      "Name": "AssetStudio.MonoScript$$.ctor",
      "Signature": "void MonoScript_ctor(void* __this);"
    }
  ]
}
""", new UTF8Encoding(false));

            var outDir = Path.Combine(tempRoot, "OutDummy");
            var exporter = new CSharpProjectExporterService();
            var result = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = outDir,
                ProjectName = "DummyExport",
                DummyDllDirectories = new[] { dummyDir },
                ScriptJsonPath = scriptJsonPath,
                SelectedAssemblyNames = new[] { "AssetStudio" },
                GenerateUnityMetaFiles = false
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("Il2CppStubs", result.Mode);
            Assert.Equal(CSharpProjectExportSourceMode.Il2CppDummyDlls, result.SourceMode);

            var monoScriptCs = Path.Combine(outDir, "src", "AssetStudio", "AssetStudio", "MonoScript.cs");
            Assert.True(File.Exists(monoScriptCs));
            var text = File.ReadAllText(monoScriptCs);
            Assert.Contains("IL2CPP DummyDll stub", text, StringComparison.Ordinal);
            Assert.Contains("0x00001234", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AssemblyExplorerAndExporter_Il2CppGlobalMetadataV29_EnumeratesAndExportsStubs()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
            WriteMinimalClassicV29Metadata(metadataPath);

            var explorer = new AssemblyExplorerService();
            var catalog = explorer.ScanGlobalMetadata(metadataPath);
            Assert.True(catalog.HasAnyTypes);
            Assert.Contains(catalog.Assemblies, a => a.AssemblyName == "Assembly-CSharp" && a.IsGameAssembly);
            Assert.Contains(catalog.AllTypes, t => t.AssemblyName == "Assembly-CSharp" && t.Namespace == "Game.Meta" && t.ClassName == "QuestManager");

            var outDir = Path.Combine(tempRoot, "OutMetadata");
            var exporter = new CSharpProjectExporterService();
            var result = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = outDir,
                ProjectName = "MetadataExport",
                GlobalMetadataPath = metadataPath,
                IncludeEngineAndThirdPartyAssemblies = false,
                GenerateUnityMetaFiles = true
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("Il2CppStubs", result.Mode);
            Assert.Equal(CSharpProjectExportSourceMode.Il2CppGlobalMetadata, result.SourceMode);
            Assert.Equal(1, result.GameAssembliesCount);
            Assert.True(File.Exists(Path.Combine(outDir, "src", "Assembly-CSharp", "Game", "Meta", "QuestManager.cs")));
            Assert.True(File.Exists(Path.Combine(outDir, "src", "Assembly-CSharp", "Game", "Meta", "QuestManager.cs.meta")));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task CSharpProjectExporterService_SkipsNativeCorruptDllsAndLogsUnresolvedReferencesWithoutAborting()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var managedDir = Path.Combine(tempRoot, "Managed");
            Directory.CreateDirectory(managedDir);

            // Drop a native/corrupted fake DLL alongside a real managed assembly with missing external refs
            File.WriteAllBytes(Path.Combine(managedDir, "CorruptedNative.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0xFF, 0x10, 0x20 });
            File.Copy(typeof(AssetStudio.Avalonia.Services.Scripting.UnityProjectGuidUtility).Assembly.Location, Path.Combine(managedDir, "AssetStudio.Avalonia.dll"));

            var outDir = Path.Combine(tempRoot, "OutPartial");
            var exporter = new CSharpProjectExporterService();
            var result = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = outDir,
                ProjectName = "ResilientExport",
                ManagedDirectories = new[] { managedDir },
                SelectedAssemblyNames = new[] { "AssetStudio.Avalonia" },
                GenerateUnityMetaFiles = false
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.GameAssembliesCount);
            Assert.True(result.TotalTypesExported > 20);
            // Referenced assemblies (Avalonia, AssetStudio, etc.) were not in Managed/, so unresolvedReferences is populated
            Assert.NotEmpty(result.UnresolvedReferences);
            Assert.True(File.Exists(result.ReportPath));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    #endregion

    #region AssemblyExplorer Performance & Non-Locking Benchmark Test

    [Fact]
    public void AssemblyExplorerService_Scans100PlusManagedAssemblies_FastMetadataOnly_WithoutLockingFiles()
    {
        // 1. Benchmark metadata-only scan across 100+ real managed assemblies in the .NET runtime directory
        // (avoids Windows Defender first-drop AV scan penalty on freshly copied temp DLLs)
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var explorer = new AssemblyExplorerService();

        // Warm JIT once on the directory, then measure steady-state scan performance
        _ = explorer.ScanDirectories(new[] { runtimeDir });

        var sw = Stopwatch.StartNew();
        var runtimeCatalog = explorer.ScanDirectories(new[] { runtimeDir });
        sw.Stop();

        Assert.True(runtimeCatalog.Assemblies.Count >= 100, $"Expected >= 100 managed assemblies in runtime dir, found {runtimeCatalog.Assemblies.Count}");
        Assert.True(runtimeCatalog.AllTypes.Count >= 2000);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Metadata-only scan of {runtimeCatalog.Assemblies.Count} DLLs took {sw.ElapsedMilliseconds} ms");

        // 2. Verify zero file locking on temp DLLs + in-memory filtering after file deletion
        var tempRoot = CreateTempDirectory();
        try
        {
            var managedDir = Path.Combine(tempRoot, "Managed");
            Directory.CreateDirectory(managedDir);

            var sourceDll = typeof(AssetStudio.Object).Assembly.Location;
            var copiedPath = Path.Combine(managedDir, "GameModule.dll");
            File.Copy(sourceDll, copiedPath);

            var catalog = explorer.ScanDirectories(new[] { managedDir });
            Assert.Single(catalog.Assemblies);
            Assert.True(catalog.AllTypes.Count > 50);

            // File must NOT be locked after scan
            File.Delete(copiedPath);
            Assert.False(File.Exists(copiedPath));

            // In-memory filter must still work cleanly with zero disk reads
            var filtered = catalog.Filter("MonoScript", showEngineAssemblies: true);
            Assert.True(filtered.TotalMatchCount >= 1);
            Assert.Equal(32, filtered.Items[0].ScriptGuid.Length);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    #endregion

    #region SCRIPT-04 Usage Lookup + ViewModel State Tests

    [Fact]
    public void ScriptUsageLookupService_FindsUsagesViaProjectIndexAndSemanticCache_WithBoundedCap()
    {
        var projectIndex = new ProjectIndex();
        projectIndex.AddRecord(
            serializedFileName: "globalgamemanagers.assets",
            originalPath: "globalgamemanagers.assets",
            pathId: 777,
            name: "PlayerController",
            type: ClassIDType.MonoScript);

        var scriptUid = projectIndex.GetHandles().First(h => h.Type == ClassIDType.MonoScript).UniqueID;

        // Simulate 310 referencing MonoBehaviours in SQLite semantic cache (exceeding maxResults = 250)
        Action<string, List<AssetReferenceItem>, List<AssetReferenceItem>, int> fakeSemanticLoader =
            (uid, usedByOut, _, limit) =>
            {
                if (!string.Equals(uid, scriptUid, StringComparison.Ordinal))
                {
                    return;
                }

                for (var i = 0; i < Math.Min(310, limit); i++)
                {
                    usedByOut.Add(new AssetReferenceItem
                    {
                        AssetUniqueId = $"level0#{1000 + i}",
                        Name = $"PlayerActor_{i:D3}",
                        AssetType = ClassIDType.MonoBehaviour,
                        TypeName = "MonoBehaviour",
                        Relation = "m_Script",
                        PathId = 1000 + i,
                        SerializedFileName = "level0",
                        Direction = AssetReferenceDirection.ReferencedBy
                    });
                }
            };

        var ctx = new ScriptUsageLookupContext
        {
            ProjectIndex = projectIndex,
            SemanticCacheLoader = fakeSemanticLoader,
            MaxResults = 250
        };

        var result = ScriptUsageLookupService.FindUsages(
            assemblyName: "Assembly-CSharp",
            ns: "Game.Combat",
            className: "PlayerController",
            selectedMonoScriptHandle: null,
            selectedMonoScript: null,
            context: ctx);

        Assert.Equal(250, result.Items.Count);
        Assert.True(result.TotalCount > 250);
        Assert.True(result.WasTruncated);
        Assert.Contains("250+", result.HeaderText, StringComparison.Ordinal);

        var first = result.Items[0];
        Assert.True(first.IsNavigable);
        Assert.Contains("level0", first.DetailLine, StringComparison.Ordinal);
        var asRef = first.ToAssetReferenceItem();
        Assert.Equal(first.TargetAssetUniqueId, asRef.AssetUniqueId);
        Assert.Equal(ClassIDType.MonoBehaviour, asRef.AssetType);
        Assert.True(asRef.IsNavigable);
    }

    [Fact]
    public async Task PreviewSessionAndMainWindowViewModel_AssemblyExplorerAndUsedByCommands_WorkHeadlessly()
    {
        using var vm = new MainWindowViewModel();
        var session = vm.PreviewSession;

        // Initial state: Explorer hidden until loaded or toggled
        Assert.False(session.IsAssemblyExplorerVisible);
        Assert.False(session.HasAssemblyExplorerCatalog);
        Assert.False(session.HasScriptUsages);

        var gameType = new AssemblyTypeEntry
        {
            AssemblyName = "Assembly-CSharp",
            AssemblyFileName = "Assembly-CSharp.dll",
            IsGameAssembly = true,
            Namespace = "Game.Combat",
            ClassName = "PlayerController",
            FullName = "Game.Combat.PlayerController",
            Kind = AssemblyTypeKind.Class,
            IsMonoBehaviour = true,
            ScriptGuid = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp", "Game.Combat", "PlayerController"),
            SourceKind = ScriptSourceKind.MonoAssemblyDecompiled
        };
        var engineType = new AssemblyTypeEntry
        {
            AssemblyName = "UnityEngine.CoreModule",
            AssemblyFileName = "UnityEngine.CoreModule.dll",
            IsGameAssembly = false,
            Namespace = "UnityEngine",
            ClassName = "Transform",
            FullName = "UnityEngine.Transform",
            Kind = AssemblyTypeKind.Class,
            SourceKind = ScriptSourceKind.MonoAssemblyDecompiled
        };

        var catalog = new AssemblyCatalogSnapshot
        {
            Assemblies = new[]
            {
                new AssemblyInfoEntry { AssemblyName = "Assembly-CSharp", Category = ScriptAssemblyCategory.Game, TypeCount = 1, Types = new[] { gameType } },
                new AssemblyInfoEntry { AssemblyName = "UnityEngine.CoreModule", Category = ScriptAssemblyCategory.UnityEngine, TypeCount = 1, Types = new[] { engineType } }
            },
            AllTypes = new[] { gameType, engineType },
            SourceDescription = "Managed • 2 assemblies (1 game) • 2 types • 1 ms",
            SourceKind = ScriptSourceKind.MonoAssemblyDecompiled
        };

        // Stale request must be ignored
        session.BeginAssemblyExplorerLoad(out var staleReq);
        session.BeginAssemblyExplorerLoad(out var currentReq);
        Assert.False(session.CompleteAssemblyExplorerLoad(staleReq, catalog));
        Assert.True(session.CompleteAssemblyExplorerLoad(currentReq, catalog));

        Assert.True(session.HasAssemblyExplorerCatalog);
        Assert.True(session.IsAssemblyExplorerVisible); // auto-shown on first non-empty catalog
        Assert.Single(session.FilteredAssemblyTypes); // Game only default hides UnityEngine
        Assert.Equal("PlayerController", session.FilteredAssemblyTypes[0].ClassName);

        // Uncheck Game only -> both types visible
        session.ShowGameAssembliesOnly = false;
        Assert.Equal(2, session.FilteredAssemblyTypes.Count);

        // Search filter works in-memory
        session.AssemblyExplorerSearchText = "Transform";
        Assert.Single(session.FilteredAssemblyTypes);
        Assert.Equal("Transform", session.FilteredAssemblyTypes[0].ClassName);

        // Toggle command hides & shows explorer
        vm.ToggleAssemblyExplorerCommand.Execute();
        Assert.False(session.IsAssemblyExplorerVisible);
        vm.ToggleAssemblyExplorerCommand.Execute();
        Assert.True(session.IsAssemblyExplorerVisible);

        // SelectAssemblyExplorerTypeCommand works headlessly
        await vm.SelectAssemblyExplorerTypeCommand.ExecuteAsync(gameType);
        Assert.Same(gameType, session.SelectedAssemblyType);

        // Used By state + NavigateToScriptUsageCommand pushes NavigationHistory
        var usage = new ScriptUsageItem
        {
            DisplayName = "HeroObject",
            DetailLine = "MonoBehaviour • GameObject 'HeroObject' • level0 • PathID 42",
            TargetAssetUniqueId = "level0#42",
            TargetAssetType = ClassIDType.MonoBehaviour,
            PathId = 42,
            SerializedFileName = "level0",
            GameObjectName = "HeroObject"
        };
        session.ShowScriptUsages(new ScriptUsageLookupResult
        {
            Items = new[] { usage },
            TotalCount = 1,
            MatchedMonoScriptCount = 1
        });
        Assert.True(session.HasScriptUsages);
        Assert.True(session.IsScriptUsagePanelVisible);
        Assert.Equal("Used By (1)", session.ScriptUsageHeaderText);

        await vm.NavigateToScriptUsageCommand.ExecuteAsync(usage);
        Assert.Equal(RightPanelTab.Preview, session.ActiveRightTab);
        Assert.Equal(1, session.NavigationHistory.Count);

        // ClearScript clears Used By but preserves loaded Assembly Explorer catalog!
        session.ClearScript();
        Assert.False(session.HasScriptUsages);
        Assert.True(session.HasAssemblyExplorerCatalog);

        // ExportCSharpProjectCommand delegates to CommandHandlers cleanly
        var exportHandlerCalled = false;
        vm.CommandHandlers.ExportCSharpProjectAsync = () =>
        {
            exportHandlerCalled = true;
            return Task.CompletedTask;
        };
        await vm.ExportCSharpProjectCommand.ExecuteAsync();
        Assert.True(exportHandlerCalled);

        // Reset clears both Script and Assembly Explorer
        session.Reset();
        Assert.False(session.HasAssemblyExplorerCatalog);
        Assert.False(session.IsAssemblyExplorerVisible);
        Assert.Empty(session.FilteredAssemblyTypes);
    }

    #endregion

    #region Synthetic Fixture Helpers

    private static async Task BuildSyntheticMultiAssemblyFixtureAsync(string srcRoot, string managedOutDir)
    {
        var unityDir = Path.Combine(srcRoot, "UnityEngine.CoreModule");
        var coreDir = Path.Combine(srcRoot, "GameCore");
        var appDir = Path.Combine(srcRoot, "GameApp");
        Directory.CreateDirectory(unityDir);
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(unityDir, "UnityEngine.CoreModule.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>UnityEngine.CoreModule</AssemblyName>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(unityDir, "UnityTypes.cs"), """
namespace UnityEngine
{
    public class Object { public string name; }
    public class Component : Object { }
    public class Behaviour : Component { public bool enabled; }
    public class MonoBehaviour : Behaviour { }
    public class ScriptableObject : Object { }
}
""");

        File.WriteAllText(Path.Combine(coreDir, "GameCore.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>GameCore</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\UnityEngine.CoreModule\UnityEngine.CoreModule.csproj" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(coreDir, "CoreTypes.cs"), """
using UnityEngine;

namespace Game.Core
{
    public enum DamageKind
    {
        Physical = 0,
        Elemental = 1
    }

    public struct DamagePacket
    {
        public int Amount;
        public DamageKind Kind;
    }

    public interface IDamageable
    {
        void ApplyDamage(DamagePacket packet);
    }

    public class WeaponConfig : ScriptableObject
    {
        public int BaseDamage = 25;
    }

    public class Repository<T>
    {
        public T LastItem;
        public void Store(T item) => LastItem = item;
    }
}
""");

        File.WriteAllText(Path.Combine(appDir, "GameApp.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>GameApp</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\UnityEngine.CoreModule\UnityEngine.CoreModule.csproj" />
    <ProjectReference Include="..\GameCore\GameCore.csproj" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(appDir, "AppTypes.cs"), """
using Game.Core;
using UnityEngine;

namespace Game.App
{
    public class PlayerController : MonoBehaviour, IDamageable
    {
        public class NestedLoadout
        {
            public int SlotIndex;
        }

        public int Health = 100;
        public WeaponConfig EquippedWeapon;
        public NestedLoadout Loadout = new NestedLoadout();

        public void ApplyDamage(DamagePacket packet)
        {
            Health -= packet.Amount;
        }
    }

    public class BossController : PlayerController
    {
        public int Phase = 1;
    }
}
""");

        var (exit, stdout, stderr) = await RunProcessAsync(
            "dotnet",
            $"build \"{Path.Combine(appDir, "GameApp.csproj")}\" -c Release --nologo -v quiet",
            srcRoot);
        Assert.True(exit == 0, $"Synthetic fixture build failed (exit {exit}):\n{stdout}\n{stderr}");

        var binDir = Path.Combine(appDir, "bin", "Release", "netstandard2.1");
        File.Copy(Path.Combine(binDir, "UnityEngine.CoreModule.dll"), Path.Combine(managedOutDir, "UnityEngine.CoreModule.dll"), overwrite: true);
        File.Copy(Path.Combine(binDir, "GameCore.dll"), Path.Combine(managedOutDir, "GameCore.dll"), overwrite: true);
        File.Copy(Path.Combine(binDir, "GameApp.dll"), Path.Combine(managedOutDir, "GameApp.dll"), overwrite: true);
    }

    private static async Task BuildAssemblyCSharpStubAsync(string srcDir, string managedOutDir)
    {
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Assembly-CSharp.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>Assembly-CSharp</AssemblyName>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(srcDir, "BootRunner.cs"), """
namespace Game.Entry
{
    public class BootRunner
    {
        public int StartupCode => 42;
    }
}
""");

        var (exit, stdout, stderr) = await RunProcessAsync(
            "dotnet",
            $"build \"{Path.Combine(srcDir, "Assembly-CSharp.csproj")}\" -c Release --nologo -v quiet",
            srcDir);
        Assert.True(exit == 0, $"Assembly-CSharp fixture build failed:\n{stdout}\n{stderr}");
        File.Copy(
            Path.Combine(srcDir, "bin", "Release", "netstandard2.1", "Assembly-CSharp.dll"),
            Path.Combine(managedOutDir, "Assembly-CSharp.dll"),
            overwrite: true);
    }

    private static void WriteMinimalClassicV29Metadata(string outputPath)
    {
        // Classic IL2CPP v29 header: sanity(4) + version(4) + 32 (offset,size) pairs = 8 + 256 = 264 bytes
        const int pairCount = 32;
        const int headerSize = 8 + pairCount * 8;
        const int typeDefStructSize = 88;
        const int imageStructSize = 40;

        // Build string table
        var strBuf = new List<byte> { 0 }; // index 0 = ""
        int AddString(string s)
        {
            var idx = strBuf.Count;
            strBuf.AddRange(Encoding.UTF8.GetBytes(s));
            strBuf.Add(0);
            return idx;
        }

        var asmCSharpIdx = AddString("Assembly-CSharp.dll");
        var unityEngineIdx = AddString("UnityEngine.CoreModule.dll");
        var nsGameMetaIdx = AddString("Game.Meta");
        var nsUnityIdx = AddString("UnityEngine");
        var questManagerIdx = AddString("QuestManager");
        var monoBehaviourIdx = AddString("MonoBehaviour");

        // Build 2 TypeDefinitions (QuestManager in Assembly-CSharp, MonoBehaviour in UnityEngine.CoreModule)
        var tdBytes = new byte[2 * typeDefStructSize];
        void WriteTypeDef(int slot, int nameIdx, int nsIdx, uint token)
        {
            var span = tdBytes.AsSpan(slot * typeDefStructSize, typeDefStructSize);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), nameIdx);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), nsIdx);
            // declaringTypeIndex at offset 12 (v29) = -1 (top-level)
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12, 4), -1);
            // parentIndex at offset 16 = -1
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), -1);
            // genericContainerIndex = -1
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), -1);
            // token at end-4 with TypeDef table 0x02
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(typeDefStructSize - 4, 4), token);
        }

        WriteTypeDef(0, questManagerIdx, nsGameMetaIdx, 0x02000001u);
        WriteTypeDef(1, monoBehaviourIdx, nsUnityIdx, 0x02000002u);

        // Build 2 Images
        var imgBytes = new byte[2 * imageStructSize];
        void WriteImage(int slot, int nameIdx, int typeStart, int typeCount)
        {
            var span = imgBytes.AsSpan(slot * imageStructSize, imageStructSize);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), nameIdx);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), slot);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), typeStart);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12, 4), typeCount);
        }

        WriteImage(0, asmCSharpIdx, 0, 1);
        WriteImage(1, unityEngineIdx, 1, 1);

        var strBytes = strBuf.ToArray();
        var strOffset = headerSize;
        var tdOffset = strOffset + strBytes.Length;
        var imgOffset = tdOffset + tdBytes.Length;

        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0xFAB11BAFu);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 29);

        // Pair 0 = stringLiteralOffset (headerSize)
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + 0 * 8, 4), headerSize);
        // Pair 2 = strings
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + 2 * 8, 4), strOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12 + 2 * 8, 4), strBytes.Length);
        // Pair 19 = typeDefinitions
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + 19 * 8, 4), tdOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12 + 19 * 8, 4), tdBytes.Length);
        // Pair 20 = images
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + 20 * 8, 4), imgOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12 + 20 * 8, 4), imgBytes.Length);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(header);
        fs.Write(strBytes);
        fs.Write(tdBytes);
        fs.Write(imgBytes);
    }

    [Fact]
    public void CompilerGeneratedFilter_SkipsUnityAndBurstGeneratedTypes()
    {
        Assert.True(AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName("UnitySourceGeneratedAssemblyMonoScriptTypes_v1"));
        Assert.True(AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName("__JobReflectionRegistrationOutput__13630703774553276063"));
        Assert.True(AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName("$BurstDirectCallInitializer"));
        Assert.True(AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName("$BurstDirectCallWrapper"));
        Assert.False(AssemblyExplorerService.IsCompilerGeneratedTopLevelTypeName("PlayerController"));
    }

    [Fact]
    public async Task CSharpProjectExporter_StrippedMscorlibAndTransitiveEngineRefs_FallsBackToSdkBclAndResolvesIndirectDlls()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var fixtureSrc = Path.Combine(tempRoot, "Fixture");
            var managedDir = Path.Combine(tempRoot, "Managed");
            var exportOut = Path.Combine(tempRoot, "Exported");
            Directory.CreateDirectory(managedDir);

            // 1) Synthetic stripped mscorlib.dll (lacks MethodImplAttribute / StructLayoutAttribute,
            //    mimicking Unity IL2CPP Managed Code Stripping).
            var strippedBclDir = Path.Combine(fixtureSrc, "mscorlib");
            Directory.CreateDirectory(strippedBclDir);
            File.WriteAllText(Path.Combine(strippedBclDir, "mscorlib.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.1</TargetFramework>
                    <AssemblyName>mscorlib</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(strippedBclDir, "Stub.cs"), "namespace StrippedPlaceholder { public class Marker {} }");

            // 2) Two engine-classified DLLs where Unity.Mid inherits from UnityEngine.Base
            var engineBaseDir = Path.Combine(fixtureSrc, "UnityEngine.BaseModule");
            Directory.CreateDirectory(engineBaseDir);
            File.WriteAllText(Path.Combine(engineBaseDir, "UnityEngine.BaseModule.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.1</TargetFramework>
                    <AssemblyName>UnityEngine.BaseModule</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(engineBaseDir, "BaseGraphic.cs"), """
                namespace UnityEngine.UI
                {
                    public class BaseGraphic { public int Order => 1; }
                }
                """);

            var engineMidDir = Path.Combine(fixtureSrc, "Unity.MidPackage");
            Directory.CreateDirectory(engineMidDir);
            File.WriteAllText(Path.Combine(engineMidDir, "Unity.MidPackage.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.1</TargetFramework>
                    <AssemblyName>Unity.MidPackage</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\UnityEngine.BaseModule\UnityEngine.BaseModule.csproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(engineMidDir, "RichLabel.cs"), """
                using UnityEngine.UI;
                namespace Unity.Mid
                {
                    public class RichLabel : BaseGraphic { public string Text { get; set; } = ""; }
                }
                """);

            // 3) Game assembly directly references ONLY Unity.MidPackage (NOT UnityEngine.BaseModule)
            //    and also contains a Unity-generated class that must be filtered out.
            var gameDir = Path.Combine(fixtureSrc, "GameGameplay");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "GameGameplay.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.1</TargetFramework>
                    <AssemblyName>GameGameplay</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Unity.MidPackage\Unity.MidPackage.csproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(gameDir, "HudWidget.cs"), """
                using System;
                using System.Runtime.CompilerServices;
                using Unity.Mid;
                namespace Game.Hud
                {
                    public class HudWidget
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public double Compute(RichLabel label) => label.Order * Math.PI;
                    }
                }
                internal class UnitySourceGeneratedAssemblyMonoScriptTypes_v1 {}
                """);

            var b1 = await RunProcessAsync("dotnet", $"build \"{Path.Combine(strippedBclDir, "mscorlib.csproj")}\" -c Release -v:q", fixtureSrc);
            Assert.True(b1.ExitCode == 0, b1.StdOut + "\n" + b1.StdErr);
            var b2 = await RunProcessAsync("dotnet", $"build \"{Path.Combine(gameDir, "GameGameplay.csproj")}\" -c Release -v:q", fixtureSrc);
            Assert.True(b2.ExitCode == 0, b2.StdOut + "\n" + b2.StdErr);

            File.Copy(Path.Combine(strippedBclDir, "bin", "Release", "netstandard2.1", "mscorlib.dll"), Path.Combine(managedDir, "mscorlib.dll"), overwrite: true);
            File.Copy(Path.Combine(engineBaseDir, "bin", "Release", "netstandard2.1", "UnityEngine.BaseModule.dll"), Path.Combine(managedDir, "UnityEngine.BaseModule.dll"), overwrite: true);
            File.Copy(Path.Combine(engineMidDir, "bin", "Release", "netstandard2.1", "Unity.MidPackage.dll"), Path.Combine(managedDir, "Unity.MidPackage.dll"), overwrite: true);
            File.Copy(Path.Combine(gameDir, "bin", "Release", "netstandard2.1", "GameGameplay.dll"), Path.Combine(managedDir, "GameGameplay.dll"), overwrite: true);

            Assert.False(CSharpProjectExporterService.IsCompleteUnstrippedBcl(Path.Combine(managedDir, "mscorlib.dll")));

            var exporter = new CSharpProjectExporterService();
            var result = exporter.Export(new CSharpProjectExportOptions
            {
                OutputDirectory = exportOut,
                ProjectName = "StrippedBclFixture",
                LayoutMode = CSharpProjectLayoutMode.StandaloneDotNetSolution,
                IncludeEngineAndThirdPartyAssemblies = false,
                GenerateUnityMetaFiles = false,
                CopyReferenceDlls = true,
                ManagedDirectories = new[] { managedDir }
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.GameAssembliesCount);
            Assert.Equal(1, result.TotalTypesExported); // UnitySourceGeneratedAssemblyMonoScriptTypes_v1 filtered!

            var exportedCsproj = File.ReadAllText(Path.Combine(exportOut, "src", "GameGameplay", "GameGameplay.csproj"));
            Assert.DoesNotContain("<NoStdLib>true</NoStdLib>", exportedCsproj);
            Assert.Contains("<GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>", exportedCsproj);
            Assert.Contains("Unity.MidPackage", exportedCsproj);
            Assert.Contains("UnityEngine.BaseModule", exportedCsproj); // Transitive engine DLL included!
            Assert.DoesNotContain("Include=\"mscorlib\"", exportedCsproj);
            Assert.True(File.Exists(Path.Combine(exportOut, "_References", "UnityEngine.BaseModule.dll")));

            // Prove the exported solution compiles cleanly with zero errors!
            var buildExported = await RunProcessAsync("dotnet", $"build \"{result.SolutionPath}\" -v:q", exportOut);
            Assert.True(buildExported.ExitCode == 0, $"Exported solution failed to build:\n{buildExported.StdOut}\n{buildExported.StdErr}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void AssertIsAcyclicDag(Dictionary<string, List<string>> adjacency)
    {
        var inDegree = adjacency.Keys.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in adjacency)
        {
            foreach (var dep in kv.Value)
            {
                inDegree[dep] = inDegree.GetValueOrDefault(dep) + 1;
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            visited++;
            foreach (var next in adjacency[cur])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        Assert.Equal(adjacency.Count, visited);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AssetStudio_ScriptBlock1Tests_" + Guid.NewGuid().ToString("N"));
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

    #endregion
}
