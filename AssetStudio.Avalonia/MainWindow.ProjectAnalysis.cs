using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    internal ProjectAnalysisReport? CurrentProjectAnalysisReport => _currentProjectAnalysisReport;
    internal ProjectAnalysisResult? CurrentProjectAnalysisResult => _currentProjectAnalysis;
    internal event Action<ProjectAnalysisReport>? ProjectAnalysisReportUpdated;

    internal void StartProjectAnalysisAsync(string projectRoot, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

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
        return FormatProjectAnalysisBannerText(report);
    }

    internal static string FormatProjectAnalysisBannerText(ProjectAnalysisReport? report)
    {
        if (report == null)
        {
            return string.Empty;
        }

        switch (report.Status)
        {
            case ProjectAnalysisStatus.Pending:
                return "Analysis: Pending";

            case ProjectAnalysisStatus.Running:
                return "Analysis: Running...";

            case ProjectAnalysisStatus.Failed:
                return string.IsNullOrWhiteSpace(report.ErrorMessage)
                    ? "Analysis: Failed"
                    : $"Analysis: Failed ({report.ErrorMessage})";

            case ProjectAnalysisStatus.Ready:
            {
                var parts = new List<string> { "Analysis: Ready", report.BuildRuntimeBadge() };
                if (report.Il2CppScriptCount > 0)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{report.Il2CppScriptCount:N0} scripts"));
                }

                if (report.AddressablesBundleCount > 0)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{report.AddressablesBundleCount:N0} bundles"));
                }

                if (report.DecryptionHintCount > 0)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{report.DecryptionHintCount:N0} decryption hint{(report.DecryptionHintCount == 1 ? string.Empty : "s")}"));
                }

                return string.Join(" \u2022 ", parts);
            }

            default:
                return "Analysis: Unknown";
        }
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
            PublishProjectAnalysisReportToUi(result.Report);

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

    private void PublishProjectAnalysisReportToUi(ProjectAnalysisReport report)
    {
        var snapshot = report.Clone();
        _currentProjectAnalysisReport = snapshot;

        Dispatcher.UIThread.Post(() =>
        {
            ProjectAnalysisReportUpdated?.Invoke(snapshot);
            if (!IsProgressiveIndexingActive()
                && (snapshot.Status == ProjectAnalysisStatus.Ready || snapshot.Status == ProjectAnalysisStatus.Failed))
            {
                StatusStripUpdate(FormatProjectAnalysisStatusText(snapshot));
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
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(fullLeft, fullRight, comparison);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
