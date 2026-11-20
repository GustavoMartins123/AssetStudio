using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private readonly ProjectAnalysisService _projectAnalysisService = ProjectAnalysisService.Shared;
    private readonly object _projectAnalysisLock = new();
    private CancellationTokenSource? _projectAnalysisCts;
    private ProjectAnalysisResult? _currentProjectAnalysis;
    private ProjectAnalysisReport? _currentProjectAnalysisReport;
    private ProjectSessionViewModel? _projectSession;

    internal ProjectSessionViewModel ProjectSession =>
        _projectSession ??= CreateProjectSessionViewModel();

    internal ProjectAnalysisReport? CurrentProjectAnalysisReport => _currentProjectAnalysisReport;
    internal ProjectAnalysisResult? CurrentProjectAnalysisResult => _currentProjectAnalysis;
    internal event Action<ProjectAnalysisReport>? ProjectAnalysisReportUpdated;

    private ProjectSessionViewModel CreateProjectSessionViewModel()
    {
        var vm = new ProjectSessionViewModel(static action =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
            }
        });

        if (projectContext?.Project != null)
        {
            vm.SetLoadedProject(projectContext.Project, isLazyMode: assetsManager.LazyLoading);
        }

        return vm;
    }

    internal void StartProjectAnalysisAsync(string projectRoot, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        ProjectSession.SetLoadedProject(
            projectFolderPath: projectRoot,
            displayName: projectContext?.Project != null && IsSameProjectRoot(projectContext.Project.ProjectRoot, projectRoot)
                ? projectContext.Project.DisplayName
                : null,
            isLazyMode: assetsManager.LazyLoading);

        CancellationTokenSource cts;
        lock (_projectAnalysisLock)
        {
            CancelProjectAnalysisNoLock();
            cts = new CancellationTokenSource();
            _projectAnalysisCts = cts;
        }

        var service = projectContext?.Store.AnalysisService ?? _projectAnalysisService;
        var options = BuildProjectAnalysisOptions(projectRoot, forceRefresh);
        var targetHash = ProjectRuntimeDetector.ComputeProjectHash(projectRoot);

        _ = RunProjectAnalysisBackgroundAsync(service, projectRoot, targetHash, options, cts);
    }

    internal void CancelProjectAnalysis()
    {
        lock (_projectAnalysisLock)
        {
            CancelProjectAnalysisNoLock();
        }
    }

    internal string FormatProjectAnalysisStatusText(ProjectAnalysisReport report)
    {
        return ProjectSessionViewModel.FormatRuntimeBannerText(report);
    }

    internal static string FormatProjectAnalysisBannerText(ProjectAnalysisReport? report)
    {
        return ProjectSessionViewModel.FormatRuntimeBannerText(report);
    }

    private ProjectAnalysisOptions BuildProjectAnalysisOptions(string projectRoot, bool forceRefresh)
    {
        var allowExternalDumper = false;
        string? dumperPath = null;

        if (projectContext?.Project != null && IsSameProjectRoot(projectContext.Project.ProjectRoot, projectRoot))
        {
            allowExternalDumper = projectContext.Project.AllowAutoIl2CppDumper;
            dumperPath = projectContext.Project.Il2CppDumperPath;
        }

        return new ProjectAnalysisOptions
        {
            ForceRefresh = forceRefresh,
            AllowExternalIl2CppDumper = allowExternalDumper,
            Il2CppDumperExecutablePath = dumperPath
        };
    }

    private async Task RunProjectAnalysisBackgroundAsync(
        ProjectAnalysisService service,
        string projectRoot,
        string targetHash,
        ProjectAnalysisOptions options,
        CancellationTokenSource cts)
    {
        void OnServiceAnalysisUpdated(string updatedRoot, ProjectAnalysisReport updatedReport)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!string.Equals(updatedReport.ProjectHash, targetHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            PublishProjectAnalysisReportToUi(updatedReport);
        }

        service.AnalysisUpdated += OnServiceAnalysisUpdated;
        try
        {
            var result = await service.AnalyzeProjectAsync(projectRoot, options, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _currentProjectAnalysis = result;
            PublishProjectAnalysisReportToUi(result.Report, result);

            if (projectContext != null)
            {
                try
                {
                    if (IsSameProjectRoot(projectContext.Project.ProjectRoot, projectRoot))
                    {
                        projectContext.Store.SaveProjectAnalysisReport(projectContext.Project.Id, result.Report);
                        projectContext.Project.AnalysisSummary = ProjectAnalysisSummary.FromReport(result.Report);
                    }
                    else
                    {
                        projectContext.Store.SaveProjectAnalysisReportByRoot(projectRoot, result.Report);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to persist project analysis summary: {ex.Message}");
                }
            }

            if (result.Report.Status == ProjectAnalysisStatus.Ready)
            {
                var cacheLabel = result.FromCache ? "cached" : "fresh";
                Logger.Info($"Project analysis ({cacheLabel}): {FormatProjectAnalysisStatusText(result.Report)}");
            }
            else if (result.Report.Status == ProjectAnalysisStatus.Failed)
            {
                Logger.Warning($"Project analysis failed: {result.Report.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Warning($"Background project analysis failed: {ex.Message}");
        }
        finally
        {
            service.AnalysisUpdated -= OnServiceAnalysisUpdated;
            lock (_projectAnalysisLock)
            {
                if (ReferenceEquals(_projectAnalysisCts, cts))
                {
                    _projectAnalysisCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private void PublishProjectAnalysisReportToUi(ProjectAnalysisReport report, ProjectAnalysisResult? fullResult = null)
    {
        var snapshot = report.Clone();
        _currentProjectAnalysisReport = snapshot;

        Dispatcher.UIThread.Post(() =>
        {
            if (fullResult != null)
            {
                ProjectSession.ApplyProjectAnalysis(fullResult);
            }
            else
            {
                ProjectSession.ApplyProjectAnalysis(snapshot);
            }

            ProjectAnalysisReportUpdated?.Invoke(snapshot);
            if (!IsProgressiveIndexingActive()
                && (snapshot.Status == ProjectAnalysisStatus.Ready || snapshot.Status == ProjectAnalysisStatus.Failed))
            {
                StatusStripUpdate(ProjectSession.RuntimeBannerText);
            }
        }, DispatcherPriority.Background);
    }

    private void CancelProjectAnalysisNoLock()
    {
        if (_projectAnalysisCts == null)
        {
            return;
        }

        try
        {
            _projectAnalysisCts.Cancel();
        }
        catch
        {
        }

        _projectAnalysisCts = null;
    }

    private static bool IsSameProjectRoot(string? left, string? right)
    {
        return ProjectSessionViewModel.IsSameProjectRoot(left, right);
    }
}
