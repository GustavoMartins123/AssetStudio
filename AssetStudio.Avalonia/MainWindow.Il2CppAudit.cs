using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private readonly Il2CppMetadataAuditService _il2CppMetadataAuditService = new();

    private async Task AuditIl2CppMetadataCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (RightTabControl.SelectedIndex != 3)
        {
            RightTabControl.SelectedIndex = 3;
        }

        var projectRoot = ResolveEffectiveScriptProjectRoot();
        var analysisReport = CurrentProjectAnalysisReport;
        var projectAnalysisCacheDir = !string.IsNullOrWhiteSpace(projectRoot)
            ? ProjectAnalysisService.Shared.GetProjectCacheDirectory(projectRoot)
            : null;
        var customScriptsDir = _customScriptsDirectory;
        var loadedAsmDir = _lastLoadedAssemblyDirectory;
        var loadedFilesSnapshot = assetsManager.assetsFileList.ToArray();

        StatusStripUpdate("Auditing IL2CPP metadata & obfuscation...");

        try
        {
            var auditReport = await Task.Run(() =>
            {
                var discovered = ScriptSourceDiscovery.Discover(
                    projectRoot: projectRoot,
                    customScriptsDirectory: customScriptsDir,
                    knownIl2CppArtifacts: analysisReport?.Il2CppDumperArtifactPaths,
                    gameAssemblyPath: analysisReport?.GameAssemblyPath,
                    loadedAssemblyDirectory: loadedAsmDir,
                    globalMetadataPath: analysisReport?.GlobalMetadataPath,
                    projectAnalysisCacheDirectory: projectAnalysisCacheDir);

                var backupDirs = !string.IsNullOrWhiteSpace(projectRoot)
                    ? ProjectRuntimeDetector.DiscoverBackupManagedDirectories(projectRoot)
                    : null;
                var hasBackupManaged = backupDirs is { Count: > 0 };

                var loadedMonoScriptCount = 0;
                var loadedTypeTreeCount = 0;
                for (var f = 0; f < loadedFilesSnapshot.Length; f++)
                {
                    var file = loadedFilesSnapshot[f];
                    if (file == null)
                    {
                        continue;
                    }

                    if (file.m_Types != null)
                    {
                        for (var t = 0; t < file.m_Types.Count; t++)
                        {
                            var st = file.m_Types[t];
                            if (st?.m_Type?.m_Nodes is { Count: > 1 })
                            {
                                loadedTypeTreeCount++;
                            }
                        }
                    }

                    lock (file)
                    {
                        for (var o = 0; o < file.Objects.Count; o++)
                        {
                            if (file.Objects[o] is MonoScript)
                            {
                                loadedMonoScriptCount++;
                            }
                        }
                    }
                }

                if (loadedMonoScriptCount == 0 && assetsManager.ProjectIndex != null)
                {
                    loadedMonoScriptCount = assetsManager.ProjectIndex.GetHandles()
                        .Count(static h => h.Type == ClassIDType.MonoScript);
                }

                var request = new Il2CppMetadataAuditRequest
                {
                    MetadataPath = discovered.GlobalMetadataPath,
                    GameAssemblyPath = discovered.GameAssemblyPath,
                    ProjectRoot = projectRoot,
                    HasBackupManagedDlls = hasBackupManaged,
                    LoadedMonoScriptCount = loadedMonoScriptCount,
                    LoadedTypeTreeCount = loadedTypeTreeCount
                };

                return _il2CppMetadataAuditService.Audit(request);
            });

            if (IsWindowClosingOrClosed)
            {
                return;
            }

            PreviewSession.ShowScriptAuditReport(auditReport);
            StatusStripUpdate(string.Create(
                CultureInfo.InvariantCulture,
                $"IL2CPP audit complete ({auditReport.ElapsedMs:N0} ms): {auditReport.ShortStatusBadge}"));
        }
        catch (Exception ex)
        {
            if (IsWindowClosingOrClosed)
            {
                return;
            }

            StatusStripUpdate($"IL2CPP audit failed: {ex.Message}");
            await ViewModel.DialogService.ShowErrorAsync("IL2CPP Audit Failed", ex.Message);
        }
    }
}
