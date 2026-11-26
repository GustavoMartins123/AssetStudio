using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests;

public class CSharpExportDialogAndHardeningTests
{
    private static readonly Regex AvaloniaUsingRegex = new(
        @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex AxamlClickAttributeRegex = new(
        @"\bClick\s*=",
        RegexOptions.CultureInvariant);

    #region CSharpProjectExportDialogViewModel & Pure-MVVM Tests

    [Fact]
    public void DialogViewModel_InitialSelectionFilteringCommandsAndResult_WorkEndToEnd()
    {
        var assemblies = new[]
        {
            MakeAssembly("mscorlib", ScriptAssemblyCategory.Framework, typeCount: 1800, sizeBytes: 4_500_000),
            MakeAssembly("UnityEngine.CoreModule", ScriptAssemblyCategory.UnityEngine, typeCount: 900, sizeBytes: 1_900_000),
            MakeAssembly("DOTween", ScriptAssemblyCategory.ThirdParty, typeCount: 220, sizeBytes: 420_000),
            MakeAssembly("Assembly-UnityScript", ScriptAssemblyCategory.Game, typeCount: 40, sizeBytes: 95_000),
            MakeAssembly("SampleCore", ScriptAssemblyCategory.Game, typeCount: 150, sizeBytes: 310_000),
            MakeAssembly("Assembly-CSharp", ScriptAssemblyCategory.Game, typeCount: 1200, sizeBytes: 2_100_000)
        };

        var request = new CSharpProjectExportDialogRequest(
            assemblies: assemblies,
            sourceDescription: "Mono Managed (6 assemblies)",
            defaultProjectName: "SampleProject",
            isIl2CppStubMode: false);

        var vm = new CSharpProjectExportDialogViewModel(request);

        // 1. Default state: only C# Game assemblies selected (Assembly-CSharp + SampleCore); Legacy UnityScript unchecked
        Assert.False(vm.ShowEngineAndThirdParty);
        Assert.Equal(3, vm.FilteredAssemblies.Count); // Game assemblies visible (including Legacy UnityScript)
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(1350, vm.SelectedTypeCount);
        Assert.True(vm.CanConfirm);
        Assert.True(vm.ConfirmCommand.CanExecute());
        Assert.Equal(CSharpProjectLayoutMode.StandaloneDotNetSolution, vm.LayoutMode);
        Assert.True(vm.GenerateUnityMetaFiles);
        Assert.True(vm.GenerateAsmdefFiles);
        Assert.True(vm.CopyReferenceDlls);
        Assert.True(vm.EnableConservativeDecompilerFallback);
        Assert.True(vm.SanitizeStrippedEditorAttributes);
        Assert.Contains("2 of 3 game assemblies", vm.SelectionSummaryText, StringComparison.Ordinal);
        Assert.Contains("1,350 types", vm.SelectionSummaryText, StringComparison.Ordinal);

        // Verify category labels and sort order (C# Game before Legacy before Third-Party before UnityEngine before Framework)
        Assert.Equal("Game", vm.AllAssemblies.First(a => a.AssemblyName == "Assembly-CSharp").CategoryLabel);
        Assert.Equal("Legacy", vm.AllAssemblies.First(a => a.AssemblyName == "Assembly-UnityScript").CategoryLabel);
        Assert.Equal("Third-Party", vm.AllAssemblies.First(a => a.AssemblyName == "DOTween").CategoryLabel);
        Assert.Equal("UnityEngine", vm.AllAssemblies.First(a => a.AssemblyName == "UnityEngine.CoreModule").CategoryLabel);
        Assert.Equal("Framework", vm.AllAssemblies.First(a => a.AssemblyName == "mscorlib").CategoryLabel);
        Assert.False(vm.AllAssemblies.First(a => a.AssemblyName == "Assembly-UnityScript").IsSelected);

        // 2. Search filtering preserves hidden checked items!
        vm.SearchText = "SampleCore";
        Assert.Single(vm.FilteredAssemblies);
        Assert.Equal("SampleCore", vm.FilteredAssemblies[0].AssemblyName);
        Assert.Equal(2, vm.SelectedCount); // Assembly-CSharp remains checked while hidden
        Assert.Contains("hidden by filter", vm.SelectionSummaryText, StringComparison.Ordinal);

        // 3. Toggle ShowEngineAndThirdParty on and search for DOTween
        vm.SearchText = string.Empty;
        vm.ShowEngineAndThirdParty = true;
        Assert.Equal(6, vm.FilteredAssemblies.Count);

        vm.SearchText = "DOTween";
        Assert.Single(vm.FilteredAssemblies);
        vm.SelectAllVisibleCommand.Execute(); // checks DOTween without touching hidden items
        Assert.Equal(3, vm.SelectedCount);
        Assert.True(vm.AllAssemblies.First(a => a.AssemblyName == "DOTween").IsSelected);

        // Hide engine/third-party again -> DOTween stays selected!
        vm.SearchText = string.Empty;
        vm.ShowEngineAndThirdParty = false;
        Assert.Equal(3, vm.FilteredAssemblies.Count);
        Assert.Equal(3, vm.SelectedCount);

        // 4. SelectGameOnly restores C# game-only selection (unchecks DOTween & Legacy UnityScript)
        vm.SelectGameOnlyCommand.Execute();
        Assert.Equal(2, vm.SelectedCount);
        Assert.False(vm.AllAssemblies.First(a => a.AssemblyName == "DOTween").IsSelected);
        Assert.False(vm.AllAssemblies.First(a => a.AssemblyName == "Assembly-UnityScript").IsSelected);

        // 5. DeselectAll disables ConfirmCommand
        vm.DeselectAllCommand.Execute();
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanConfirm);
        Assert.False(vm.ConfirmCommand.CanExecute());

        // Attempting to execute ConfirmCommand when 0 selected does not close
        CSharpProjectExportDialogResult? closedResult = null;
        var closeCount = 0;
        vm.RequestClose += r =>
        {
            closeCount++;
            closedResult = r;
        };
        vm.ConfirmCommand.Execute();
        Assert.Equal(0, closeCount);

        // 6. Switch LayoutMode to UnityAssetsScripts -> SanitizeStrippedEditorAttributes defaults to false
        vm.LayoutMode = CSharpProjectLayoutMode.UnityAssetsScripts;
        Assert.False(vm.SanitizeStrippedEditorAttributes);
        Assert.False(vm.CopyReferenceDlls);
        Assert.True(vm.IsUnityAssetsLayout);
        Assert.False(vm.IsStandaloneLayout);

        // Switch back to Standalone -> SanitizeStrippedEditorAttributes defaults to true
        vm.SelectedLayoutModeIndex = 0;
        Assert.Equal(CSharpProjectLayoutMode.StandaloneDotNetSolution, vm.LayoutMode);
        Assert.True(vm.SanitizeStrippedEditorAttributes);
        Assert.True(vm.CopyReferenceDlls);

        // 7. Re-select Game Only + DOTween and Confirm
        vm.SelectGameOnlyCommand.Execute();
        vm.ShowEngineAndThirdParty = true;
        vm.AllAssemblies.First(a => a.AssemblyName == "DOTween").IsSelected = true;
        vm.ProjectName = "CustomExport";
        vm.EnableConservativeDecompilerFallback = true;

        vm.ConfirmCommand.Execute();
        Assert.Equal(1, closeCount);
        Assert.NotNull(closedResult);
        Assert.Same(closedResult, vm.Result);
        Assert.Equal("CustomExport", closedResult!.ProjectName);
        Assert.Equal(3, closedResult.SelectedAssemblyNames.Count);
        Assert.Contains("Assembly-CSharp", closedResult.SelectedAssemblyNames);
        Assert.Contains("SampleCore", closedResult.SelectedAssemblyNames);
        Assert.Contains("DOTween", closedResult.SelectedAssemblyNames);
        Assert.True(closedResult.IncludeEngineAndThirdPartyAssemblies);
        Assert.True(closedResult.EnableConservativeDecompilerFallback);
        Assert.True(closedResult.SanitizeStrippedEditorAttributes);

        // 8. Cancel fires RequestClose(null)
        vm.CancelCommand.Execute();
        Assert.Equal(2, closeCount);
        Assert.Null(closedResult);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void DialogAxamlAndViewModel_FollowPureMvvmRules()
    {
        var repoRoot = LocateRepositoryRoot();
        var axamlPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "CSharpProjectExportDialog.axaml");
        var vmPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels", "CSharpProjectExportDialogViewModel.cs");

        Assert.True(File.Exists(axamlPath), $"Missing dialog AXAML: {axamlPath}");
        Assert.True(File.Exists(vmPath), $"Missing dialog ViewModel: {vmPath}");

        var axaml = File.ReadAllText(axamlPath);
        Assert.Contains("x:CompileBindings=\"True\"", axaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:CSharpProjectExportDialogViewModel\"", axaml, StringComparison.Ordinal);
        Assert.False(AxamlClickAttributeRegex.IsMatch(axaml), "CSharpProjectExportDialog.axaml must have zero Click= attributes.");

        var vmSource = File.ReadAllText(vmPath);
        Assert.False(AvaloniaUsingRegex.IsMatch(vmSource), "CSharpProjectExportDialogViewModel.cs must have zero using Avalonia imports.");

        // Reflection check: zero Avalonia types exposed on ViewModel / RowViewModel / Request / Result
        var typesToCheck = new[]
        {
            typeof(CSharpProjectExportDialogViewModel),
            typeof(CSharpExportAssemblyRowViewModel),
            typeof(CSharpProjectExportDialogRequest),
            typeof(CSharpProjectExportDialogResult)
        };
        foreach (var type in typesToCheck)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotAvaloniaType(prop.PropertyType, $"{type.Name}.{prop.Name}");
            }
        }
    }

    #endregion

    #region Classifier & Decompiler Syntax Anomaly Validator Tests

    [Theory]
    [InlineData("nunit.framework", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("nunit.framework.dll", ScriptAssemblyCategory.ThirdParty, false)]
    [InlineData("Assembly-UnityScript", ScriptAssemblyCategory.Game, true)]
    [InlineData("Assembly-UnityScript-firstpass.dll", ScriptAssemblyCategory.Game, true)]
    [InlineData("Assembly-CSharp", ScriptAssemblyCategory.Game, false)]
    [InlineData("SampleCore", ScriptAssemblyCategory.Game, false)]
    public void Classifier_HandlesNUnitAndLegacyUnityScriptHelper(
        string assemblyName,
        ScriptAssemblyCategory expectedCategory,
        bool expectedLegacyUnityScript)
    {
        Assert.Equal(expectedCategory, ScriptProjectAssemblyClassifier.Classify(assemblyName));
        Assert.Equal(expectedLegacyUnityScript, ScriptProjectAssemblyClassifier.IsLegacyUnityScriptAssembly(assemblyName));
    }

    [Fact]
    public void HasLikelyDecompilerSyntaxAnomaly_AcceptsHealthyCodeWithStringsCommentsOpenGenericsAndValidInterfaces()
    {
        const string healthyCode = """
            using System;
            using System.Collections.Generic;

            namespace Sample.Clean
            {
                public interface IHealthTarget
                {
                    int Armor { set; }
                }

                public abstract class BaseActor
                {
                    public abstract int AbstractScore { set; }
                }

                public sealed class CleanActor : BaseActor
                {
                    private int _score;
                    public int Health { get; set; }
                    public override int AbstractScore
                    {
                        set { _score = value; }
                    }
                    public int ExpressionSetter { set => _score = value; }

                    public void Run()
                    {
                        // Comment with unbalanced braces { { { and <>c token
                        /* Block comment with } } and <Run>d__1 */
                        var s1 = "String with { unbalanced brace and <>c";
                        var s2 = @"Verbatim with } brace and <Prop>k__BackingField";
                        var ch = '{';
                        var interp = $"Interpolated {Health} and literal {{ brace";
                        var openGeneric1 = typeof(List<>);
                        var openGeneric2 = typeof(Dictionary<,>);
                        if (Health < 10 && _score > 0)
                        {
                            Console.WriteLine(s1 + s2 + ch + interp + openGeneric1 + openGeneric2);
                        }
                    }
                }
            }
            """;

        Assert.False(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(healthyCode));
        Assert.Equal(0, CSharpProjectExporterService.CountLikelyDecompilerSyntaxAnomalies(healthyCode));
    }

    [Fact]
    public void HasLikelyDecompilerSyntaxAnomaly_DetectsUnbalancedBracesCompilerGenTokensAndSetOnlyAutoProperties()
    {
        // 1. Missing closing brace (CS1513)
        const string missingBrace = """
            namespace Sample.Broken
            {
                public class MissingClosingBrace
                {
                    public void Tick()
                    {
                        var x = 1;
                }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(missingBrace));

        // 2. Extra closing brace (CS1022)
        const string extraBrace = """
            namespace Sample.Broken
            {
                public class ExtraClosingBrace { }
            }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(extraBrace));

        // 3. Unescaped compiler-generated <> token (CS1525)
        const string compilerGenAngle = """
            namespace Sample.Broken
            {
                public class LambdaLeak
                {
                    public void Run()
                    {
                        var cb = <>c.<>9__0_0;
                    }
                }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(compilerGenAngle));

        // 4. Unescaped compiler-generated <Name>suffix identifier
        const string compilerGenNamed = """
            namespace Sample.Broken
            {
                public class BackingFieldLeak
                {
                    private int <Health>k__BackingField;
                }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(compilerGenNamed));

        // 5. Invalid C# set;-only auto-property in class (CS8051)
        const string setOnlyAutoProp = """
            namespace Sample.Broken
            {
                public class SetOnlyAutoProp
                {
                    public int Speed { set; }
                }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(setOnlyAutoProp));

        // 6. Multi-line private set;-only auto-property with attribute (CS8051)
        const string setOnlyPrivateAutoProp = """
            namespace Sample.Broken
            {
                public class SetOnlyPrivateAutoProp
                {
                    public float Gravity
                    {
                        [System.Runtime.CompilerServices.CompilerGenerated]
                        private set;
                    }
                }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(setOnlyPrivateAutoProp));

        // 7. ILSpy unresolved tuple/type placeholder ('? item = ...;')
        const string unresolvedTypePlaceholder = """
            namespace Sample.Broken
            {
                public class TupleBug
                {
                    public void Run()
                    {
                        ? item = (1, 2);
                    }
                }
            }
            """;
        Assert.True(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(unresolvedTypePlaceholder));
    }

    [Fact]
    public void RepairSetOnlyAutoProperties_FixesClassSetOnlyAndPreservesInterfacesAndAbstract()
    {
        const string input = """
            namespace Sample.Props
            {
                public interface IWritable
                {
                    int Target { set; }
                }

                public abstract class BaseHolder
                {
                    public abstract int AbstractVal { set; }
                }

                public class ConcreteHolder : BaseHolder
                {
                    public string Sender { set; }
                    public float Speed { private set; }
                    public override int AbstractVal { set; }
                }
            }
            """;

        var repaired = CSharpProjectExporterService.RepairSetOnlyAutoProperties(input, out var count);
        Assert.Equal(3, count);
        Assert.Contains("int Target { set; }", repaired, StringComparison.Ordinal);
        Assert.Contains("public abstract int AbstractVal { set; }", repaired, StringComparison.Ordinal);
        Assert.Contains("public string Sender { get; set; }", repaired, StringComparison.Ordinal);
        Assert.Contains("public float Speed { get; private set; }", repaired, StringComparison.Ordinal);
        Assert.Contains("public override int AbstractVal { set { } }", repaired, StringComparison.Ordinal);
        Assert.False(CSharpProjectExporterService.HasLikelyDecompilerSyntaxAnomaly(repaired));
    }

    #endregion

    #region SanitizeStrippedEditorAttributes & End-to-End Subset Export Tests

    [Fact]
    public void SanitizeStrippedCreateAssetMenuAttributes_CommentsOutAttributeLinesAndPreservesComments()
    {
        const string source = """
            using UnityEngine;

            namespace Sample.Items
            {
                [CreateAssetMenu(fileName = "NewItem", menuName = "Sample/New Item", order = 1)]
                public class ItemConfig : ScriptableObject
                {
                    public string DisplayName;
                }

                // [CreateAssetMenu(fileName = "AlreadyCommented", menuName = "Sample/Commented")]
                [UnityEngine.CreateAssetMenu(menuName = "Sample/Weapon")]
                public class WeaponData : ScriptableObject
                {
                }
            }
            """;

        var sanitized = CSharpProjectExporterService.SanitizeStrippedCreateAssetMenuAttributes(source, out var modified);
        Assert.True(modified);
        Assert.Contains(
            "    // [CreateAssetMenu(fileName = \"NewItem\", menuName = \"Sample/New Item\", order = 1)] // Stripped in IL2CPP CoreModule; uncomment in Unity Editor",
            sanitized,
            StringComparison.Ordinal);
        Assert.Contains(
            "    // [UnityEngine.CreateAssetMenu(menuName = \"Sample/Weapon\")] // Stripped in IL2CPP CoreModule; uncomment in Unity Editor",
            sanitized,
            StringComparison.Ordinal);
        // Already-commented line must NOT be double-commented
        Assert.DoesNotContain("// // [CreateAssetMenu", sanitized, StringComparison.Ordinal);

        // Healthy source without CreateAssetMenu returns unmodified with modified == false
        const string plain = "namespace Sample { public class Plain { } }";
        var untouched = CSharpProjectExporterService.SanitizeStrippedCreateAssetMenuAttributes(plain, out var plainModified);
        Assert.False(plainModified);
        Assert.Equal(plain, untouched);
    }

    [Fact]
    public async Task Exporter_SanitizesStrippedCreateAssetMenuOnlyWhenEnabledAndStripped_AndBuildsSubsetCleanly()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            // 1. Build a complete (unstripped) UnityEngine.CoreModule + two game assemblies (SampleCore, SampleApp)
            var fixtureSrcDir = Path.Combine(tempRoot, "FixtureSrc");
            var unstrippedManagedDir = Path.Combine(tempRoot, "UnstrippedManaged");
            var strippedManagedDir = Path.Combine(tempRoot, "StrippedManaged");
            Directory.CreateDirectory(unstrippedManagedDir);
            Directory.CreateDirectory(strippedManagedDir);

            await BuildCreateAssetMenuFixtureAsync(fixtureSrcDir, unstrippedManagedDir, strippedManagedDir);

            var unstrippedCorePath = Path.Combine(unstrippedManagedDir, "UnityEngine.CoreModule.dll");
            var strippedCorePath = Path.Combine(strippedManagedDir, "UnityEngine.CoreModule.dll");
            Assert.False(CSharpProjectExporterService.AreCreateAssetMenuPropertiesStripped(unstrippedCorePath));
            Assert.True(CSharpProjectExporterService.AreCreateAssetMenuPropertiesStripped(strippedCorePath));

            var exporter = new CSharpProjectExporterService();

            // 2. Export from stripped CoreModule WITH SanitizeStrippedEditorAttributes = true and SelectedAssemblyNames subset (SampleCore only!)
            var sanitizedOutDir = Path.Combine(tempRoot, "OutSanitizedSubset");
            var sanitizedResult = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = sanitizedOutDir,
                ProjectName = "SubsetExport",
                LayoutMode = CSharpProjectLayoutMode.StandaloneDotNetSolution,
                SelectedAssemblyNames = new[] { "SampleCore" },
                CopyReferenceDlls = true,
                EnableConservativeDecompilerFallback = true,
                SanitizeStrippedEditorAttributes = true,
                ManagedDirectories = new[] { strippedManagedDir }
            });

            Assert.True(sanitizedResult.Success, sanitizedResult.ErrorMessage);
            Assert.Equal(1, sanitizedResult.GameAssembliesCount);
            Assert.True(Directory.Exists(Path.Combine(sanitizedOutDir, "src", "SampleCore")));
            Assert.False(Directory.Exists(Path.Combine(sanitizedOutDir, "src", "SampleApp")));
            Assert.Equal(1, sanitizedResult.SanitizedAttributeFilesCount);

            var itemConfigCs = File.ReadAllText(Path.Combine(sanitizedOutDir, "src", "SampleCore", "Sample", "Core", "ItemConfig.cs"));
            Assert.Contains("// [CreateAssetMenu(", itemConfigCs, StringComparison.Ordinal);
            Assert.Contains("Stripped in IL2CPP CoreModule; uncomment in Unity Editor", itemConfigCs, StringComparison.Ordinal);

            // Verify export-report.json exposes conservativeFallbackCount and sanitizedAttributeFilesCount
            using (var reportDoc = JsonDocument.Parse(File.ReadAllText(sanitizedResult.ReportPath)))
            {
                Assert.True(reportDoc.RootElement.TryGetProperty("conservativeFallbackCount", out var fallbackProp));
                Assert.Equal(sanitizedResult.ConservativeFallbackCount, fallbackProp.GetInt32());
                Assert.True(reportDoc.RootElement.TryGetProperty("sanitizedAttributeFilesCount", out var sanitizedProp));
                Assert.Equal(1, sanitizedProp.GetInt32());
            }

            // Real `dotnet build` against the stripped UnityEngine.CoreModule.dll MUST succeed with zero CS0617 errors!
            var (buildExit, buildStdout, buildStderr) = await RunProcessAsync(
                "dotnet",
                $"build \"{sanitizedResult.SolutionPath}\" --nologo -v minimal",
                sanitizedOutDir);
            Assert.True(buildExit == 0, $"Sanitized subset solution failed `dotnet build` (exit {buildExit}):\nSTDOUT:\n{buildStdout}\nSTDERR:\n{buildStderr}");

            // 3. Export from stripped CoreModule WITH SanitizeStrippedEditorAttributes = false -> attribute left untouched
            var unsanitizedOutDir = Path.Combine(tempRoot, "OutUnsanitized");
            var unsanitizedResult = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = unsanitizedOutDir,
                ProjectName = "UnsanitizedExport",
                SelectedAssemblyNames = new[] { "SampleCore" },
                SanitizeStrippedEditorAttributes = false,
                ManagedDirectories = new[] { strippedManagedDir }
            });

            Assert.True(unsanitizedResult.Success, unsanitizedResult.ErrorMessage);
            Assert.Equal(0, unsanitizedResult.SanitizedAttributeFilesCount);
            var unsanitizedCs = File.ReadAllText(Path.Combine(unsanitizedOutDir, "src", "SampleCore", "Sample", "Core", "ItemConfig.cs"));
            Assert.DoesNotContain("// [CreateAssetMenu(", unsanitizedCs, StringComparison.Ordinal);
            Assert.Contains("[CreateAssetMenu(", unsanitizedCs, StringComparison.Ordinal);

            // 4. Export from UNSTRIPPED CoreModule WITH SanitizeStrippedEditorAttributes = true -> attribute left untouched!
            var unstrippedOutDir = Path.Combine(tempRoot, "OutUnstripped");
            var unstrippedResult = await exporter.ExportAsync(new CSharpProjectExportOptions
            {
                OutputDirectory = unstrippedOutDir,
                ProjectName = "UnstrippedExport",
                SelectedAssemblyNames = new[] { "SampleCore" },
                SanitizeStrippedEditorAttributes = true,
                ManagedDirectories = new[] { unstrippedManagedDir }
            });

            Assert.True(unstrippedResult.Success, unstrippedResult.ErrorMessage);
            Assert.Equal(0, unstrippedResult.SanitizedAttributeFilesCount);
            var unstrippedCs = File.ReadAllText(Path.Combine(unstrippedOutDir, "src", "SampleCore", "Sample", "Core", "ItemConfig.cs"));
            Assert.DoesNotContain("// [CreateAssetMenu(", unstrippedCs, StringComparison.Ordinal);
            Assert.Contains("[CreateAssetMenu(", unstrippedCs, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    #endregion

    #region Helpers

    private static AssemblyInfoEntry MakeAssembly(
        string name,
        ScriptAssemblyCategory category,
        int typeCount,
        long sizeBytes)
    {
        return new AssemblyInfoEntry
        {
            AssemblyName = name,
            FileName = name + ".dll",
            FilePath = Path.Combine("Managed", name + ".dll"),
            Category = category,
            TypeCount = typeCount,
            FileSizeBytes = sizeBytes,
            SourceKind = ScriptSourceKind.MonoAssemblyDecompiled
        };
    }

    private static async Task BuildCreateAssetMenuFixtureAsync(
        string fixtureSrcDir,
        string unstrippedManagedDir,
        string strippedManagedDir)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // 1. Full unstripped UnityEngine.CoreModule (has writable fileName & menuName properties)
        var coreFullDir = Path.Combine(fixtureSrcDir, "UnityEngine.CoreModule.Full");
        Directory.CreateDirectory(coreFullDir);
        File.WriteAllText(Path.Combine(coreFullDir, "UnityEngine.CoreModule.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.1</TargetFramework>
                <AssemblyName>UnityEngine.CoreModule</AssemblyName>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """, utf8);
        File.WriteAllText(Path.Combine(coreFullDir, "CoreTypes.cs"), """
            using System;
            namespace UnityEngine
            {
                public class Object { public string name; }
                public class ScriptableObject : Object { }

                [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class CreateAssetMenuAttribute : Attribute
                {
                    public string menuName { get; set; }
                    public string fileName { get; set; }
                    public int order { get; set; }
                }
            }
            """, utf8);

        var (exitFull, outFull, errFull) = await RunProcessAsync("dotnet", "build -c Release --nologo -v quiet", coreFullDir);
        Assert.True(exitFull == 0, $"Full CoreModule build failed:\n{outFull}\n{errFull}");
        var fullCoreDll = Path.Combine(coreFullDir, "bin", "Release", "netstandard2.1", "UnityEngine.CoreModule.dll");
        File.Copy(fullCoreDll, Path.Combine(unstrippedManagedDir, "UnityEngine.CoreModule.dll"), overwrite: true);

        // 2. SampleCore (uses [CreateAssetMenu(fileName = ..., menuName = ...)])
        var sampleCoreDir = Path.Combine(fixtureSrcDir, "SampleCore");
        Directory.CreateDirectory(sampleCoreDir);
        File.WriteAllText(Path.Combine(sampleCoreDir, "SampleCore.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.1</TargetFramework>
                <AssemblyName>SampleCore</AssemblyName>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="UnityEngine.CoreModule">
                  <HintPath>{fullCoreDll}</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """, utf8);
        File.WriteAllText(Path.Combine(sampleCoreDir, "ItemConfig.cs"), """
            using UnityEngine;
            namespace Sample.Core
            {
                [CreateAssetMenu(fileName = "SampleItem", menuName = "Sample/Item Config", order = 2)]
                public class ItemConfig : ScriptableObject
                {
                    public int ItemId;
                    public string Title;
                }
            }
            """, utf8);

        var (exitCore, outCore, errCore) = await RunProcessAsync("dotnet", "build -c Release --nologo -v quiet", sampleCoreDir);
        Assert.True(exitCore == 0, $"SampleCore build failed:\n{outCore}\n{errCore}");
        var sampleCoreDll = Path.Combine(sampleCoreDir, "bin", "Release", "netstandard2.1", "SampleCore.dll");
        File.Copy(sampleCoreDll, Path.Combine(unstrippedManagedDir, "SampleCore.dll"), overwrite: true);
        File.Copy(sampleCoreDll, Path.Combine(strippedManagedDir, "SampleCore.dll"), overwrite: true);

        // 3. SampleApp (second game assembly to verify subset exclusion)
        var sampleAppDir = Path.Combine(fixtureSrcDir, "SampleApp");
        Directory.CreateDirectory(sampleAppDir);
        File.WriteAllText(Path.Combine(sampleAppDir, "SampleApp.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.1</TargetFramework>
                <AssemblyName>SampleApp</AssemblyName>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="SampleCore">
                  <HintPath>{sampleCoreDll}</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """, utf8);
        File.WriteAllText(Path.Combine(sampleAppDir, "AppBoot.cs"), """
            using Sample.Core;
            namespace Sample.App
            {
                public sealed class AppBoot
                {
                    public ItemConfig ActiveConfig;
                }
            }
            """, utf8);

        var (exitApp, outApp, errApp) = await RunProcessAsync("dotnet", "build -c Release --nologo -v quiet", sampleAppDir);
        Assert.True(exitApp == 0, $"SampleApp build failed:\n{outApp}\n{errApp}");
        var sampleAppDll = Path.Combine(sampleAppDir, "bin", "Release", "netstandard2.1", "SampleApp.dll");
        File.Copy(sampleAppDll, Path.Combine(unstrippedManagedDir, "SampleApp.dll"), overwrite: true);
        File.Copy(sampleAppDll, Path.Combine(strippedManagedDir, "SampleApp.dll"), overwrite: true);

        // 4. Stripped UnityEngine.CoreModule (simulates real IL2CPP UnityLinker stripping CreateAssetMenuAttribute getters, leaving write-only setters -> CS0617)
        var coreStrippedDir = Path.Combine(fixtureSrcDir, "UnityEngine.CoreModule.Stripped");
        Directory.CreateDirectory(coreStrippedDir);
        File.WriteAllText(Path.Combine(coreStrippedDir, "UnityEngine.CoreModule.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.1</TargetFramework>
                <AssemblyName>UnityEngine.CoreModule</AssemblyName>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """, utf8);
        File.WriteAllText(Path.Combine(coreStrippedDir, "CoreTypes.cs"), """
            using System;
            namespace UnityEngine
            {
                public class Object { public string name; }
                public class ScriptableObject : Object { }

                [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class CreateAssetMenuAttribute : Attribute
                {
                    public string menuName { set { } }
                    public string fileName { set { } }
                    public int order { set { } }
                }
            }
            """, utf8);

        var (exitStripped, outStripped, errStripped) = await RunProcessAsync("dotnet", "build -c Release --nologo -v quiet", coreStrippedDir);
        Assert.True(exitStripped == 0, $"Stripped CoreModule build failed:\n{outStripped}\n{errStripped}");
        var strippedCoreDll = Path.Combine(coreStrippedDir, "bin", "Release", "netstandard2.1", "UnityEngine.CoreModule.dll");
        File.Copy(strippedCoreDll, Path.Combine(strippedManagedDir, "UnityEngine.CoreModule.dll"), overwrite: true);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory)
        => await TestProcessRunner.RunAsync(fileName, arguments, workingDirectory);

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

    private static void AssertNotAvaloniaType(Type type, string memberName)
    {
        var ns = type.Namespace ?? string.Empty;
        Assert.False(
            string.Equals(ns, "Avalonia", StringComparison.Ordinal) || ns.StartsWith("Avalonia.", StringComparison.Ordinal),
            $"ViewModel member '{memberName}' must not reference Avalonia type '{type.FullName}'.");

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                AssertNotAvaloniaType(arg, memberName);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AssetStudio_CSharpExportHardening_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
