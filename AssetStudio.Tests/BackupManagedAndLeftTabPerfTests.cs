using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.Services.Scripting;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class BackupManagedAndLeftTabPerfTests
    {
        [Fact]
        public void ProjectRuntimeDetector_Il2CppWithBackupManagedFolder_DetectsBackupDllsAndFormatsBanner()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"AssetStudio_BackupManaged_{Guid.NewGuid():N}");
            try
            {
                var dataDir = Path.Combine(tempRoot, "SampleGame_Data");
                var metadataDir = Path.Combine(dataDir, "il2cpp_data", "Metadata");
                var backupManagedDir = Path.Combine(tempRoot, "SampleGame_BackUpThisFolder_ButDontShipItWithYourGame", "Managed");
                Directory.CreateDirectory(metadataDir);
                Directory.CreateDirectory(backupManagedDir);

                File.WriteAllBytes(Path.Combine(tempRoot, "GameAssembly.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x00 });

                // Write valid IL2CPP v31 global-metadata.dat header
                var metaHeader = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(metaHeader.AsSpan(0, 4), ProjectRuntimeDetector.Il2CppMetadataSanity);
                BinaryPrimitives.WriteInt32LittleEndian(metaHeader.AsSpan(4, 4), 31);
                File.WriteAllBytes(Path.Combine(metadataDir, "global-metadata.dat"), metaHeader);

                // Copy a real managed assembly into the backup Managed folder
                var sampleManagedAsm = typeof(MonoScript).Assembly.Location;
                File.Copy(sampleManagedAsm, Path.Combine(backupManagedDir, "Assembly-CSharp.dll"));
                File.Copy(sampleManagedAsm, Path.Combine(backupManagedDir, "Assembly-CSharp-firstpass.dll"));

                var detector = new ProjectRuntimeDetector();
                var detection = detector.Detect(tempRoot);

                Assert.Equal(UnityScriptingBackend.Il2Cpp, detection.ScriptingBackend);
                Assert.Equal(31, detection.Il2CppMetadataVersion);
                Assert.True(detection.HasManagedAssemblies);
                Assert.True(detection.HasBackupManagedAssemblies);
                Assert.Equal(2, detection.ManagedAssemblyCount);
                Assert.Equal(Path.GetFullPath(backupManagedDir), Path.GetFullPath(detection.BackupManagedDirectory));

                var report = new ProjectAnalysisReport
                {
                    Status = ProjectAnalysisStatus.Ready,
                    ScriptingBackend = detection.ScriptingBackend,
                    Il2CppMetadataVersion = detection.Il2CppMetadataVersion,
                    HasManagedAssemblies = detection.HasManagedAssemblies,
                    ManagedAssemblyCount = detection.ManagedAssemblyCount,
                    HasBackupManagedAssemblies = detection.HasBackupManagedAssemblies,
                    BackupManagedDirectory = detection.BackupManagedDirectory
                };

                var badge = report.BuildRuntimeBadge();
                Assert.Contains("IL2CPP v31 + Backup Managed DLLs (2)", badge);

                // Verify sibling discovery also works when projectRoot is pointed directly at SampleGame_Data
                var fromDataDir = ProjectRuntimeDetector.DiscoverBackupManagedDirectories(dataDir);
                Assert.Contains(Path.GetFullPath(backupManagedDir), fromDataDir.Select(Path.GetFullPath),
                    OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

                // Verify ScriptSourceDiscovery registers backup Managed DLLs as full ManagedDirectories (prioritized above global-metadata.dat)
                var scriptSources = ScriptSourceDiscovery.Discover(tempRoot);
                Assert.Contains(Path.GetFullPath(backupManagedDir), scriptSources.ManagedDirectories.Select(Path.GetFullPath),
                    OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(Path.GetFullPath(backupManagedDir), scriptSources.DummyDllDirectories.Select(Path.GetFullPath),
                    OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

                // Verify ScriptViewerService decompiles full C# from backup Managed DLLs before falling back to global-metadata.dat
                using var viewer = new ScriptViewerService();
                var res = viewer.ResolveScript(new ScriptLookupRequest
                {
                    ProjectRoot = tempRoot,
                    AssemblyName = "Assembly-CSharp.dll",
                    Namespace = typeof(MonoScript).Namespace ?? string.Empty,
                    ClassName = nameof(MonoScript),
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                    GlobalMetadataPath = Path.Combine(metadataDir, "global-metadata.dat")
                });

                Assert.True(res.Success, res.DiagnosticMessage);
                Assert.Equal(ScriptSourceKind.MonoAssemblyDecompiled, res.SourceKind);
                Assert.Contains("class MonoScript", res.SourceCode);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void MainWindowAxaml_UsesPersistentTabStripAndVirtualizedSceneTreeViews()
        {
            var repoRoot = FindRepoRoot();
            var axaml = File.ReadAllText(Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml"));

            Assert.Contains("<TabStrip x:Name=\"LeftTabControl\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"SceneHierarchyTabContent\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"AssetListTabContent\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"AssetClassesTabContent\"", axaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<TabControl x:Name=\"LeftTabControl\"", axaml, StringComparison.Ordinal);

            // Both SceneTreeView and ContainersTreeView must use VirtualizingStackPanel so large trees never stall UI thread.
            var virtualizingCount = axaml.Split("<VirtualizingStackPanel />", StringSplitOptions.None).Length - 1;
            Assert.True(virtualizingCount >= 2, $"Expected at least 2 VirtualizingStackPanel declarations for SceneTreeView & ContainersTreeView, found {virtualizingCount}.");
        }

        private static string FindRepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "AssetStudio.sln")))
                {
                    return dir;
                }
                dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
            }

            throw new DirectoryNotFoundException("Could not locate AssetStudio.sln.");
        }
    }
}
