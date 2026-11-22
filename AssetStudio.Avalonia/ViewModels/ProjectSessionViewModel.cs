using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class ProjectSessionViewModel : ViewModelBase
    {
        private readonly Action<Action> _dispatchToUi;

        private string _projectFolderPath = string.Empty;
        private string _projectDisplayName = string.Empty;
        private bool _hasLoadedProject;
        private bool _isLazyMode;
        private string _unityVersion = string.Empty;
        private UnityScriptingBackend _scriptingBackend = UnityScriptingBackend.Unknown;
        private string _runtimeBannerText = string.Empty;
        private bool _hasRuntimeBanner;
        private string _cacheStatusSummary = string.Empty;
        private string _windowTitle;
        private ManagedProject? _selectedProjectRecord;
        private bool _isClosingProject;
        private ProjectAnalysisStatus _analysisStatus = ProjectAnalysisStatus.Pending;
        private ProjectAnalysisResult? _currentAnalysisResult;
        private ProjectAnalysisReport? _currentAnalysisReport;
        private string _productName = string.Empty;
        private string _targetPlatform = string.Empty;
        private int? _il2CppMetadataVersion;
        private bool _il2CppMetadataObfuscated;
        private bool _hasAddressables;
        private int _il2CppScriptCount;
        private int _addressablesBundleCount;
        private int _decryptionHintCount;
        private int _encryptedFileCount;
        private int _readableAssetFileCount;
        private double _coveragePercent = 100.0;

        public ProjectSessionViewModel(Action<Action>? dispatchToUi = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            _windowTitle = FormatWindowTitle(null);
            SavedProjects = new BulkObservableCollection<ManagedProject>();
        }

        public BulkObservableCollection<ManagedProject> SavedProjects { get; }

        public string ProjectFolderPath
        {
            get => _projectFolderPath;
            private set => SetProperty(ref _projectFolderPath, value ?? string.Empty);
        }

        public string ProjectDisplayName
        {
            get => _projectDisplayName;
            private set => SetProperty(ref _projectDisplayName, value ?? string.Empty);
        }

        public bool HasLoadedProject
        {
            get => _hasLoadedProject;
            private set => SetProperty(ref _hasLoadedProject, value);
        }

        public bool IsLazyMode
        {
            get => _isLazyMode;
            set => SetProperty(ref _isLazyMode, value);
        }

        public string UnityVersion
        {
            get => _unityVersion;
            private set => SetProperty(ref _unityVersion, value ?? string.Empty);
        }

        public UnityScriptingBackend ScriptingBackend
        {
            get => _scriptingBackend;
            private set => SetProperty(ref _scriptingBackend, value);
        }

        public string RuntimeBannerText
        {
            get => _runtimeBannerText;
            private set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _runtimeBannerText, normalized))
                {
                    HasRuntimeBanner = !string.IsNullOrWhiteSpace(normalized);
                }
            }
        }

        public bool HasRuntimeBanner
        {
            get => _hasRuntimeBanner;
            private set => SetProperty(ref _hasRuntimeBanner, value);
        }

        public string CacheStatusSummary
        {
            get => _cacheStatusSummary;
            set => SetProperty(ref _cacheStatusSummary, value ?? string.Empty);
        }

        public string WindowTitle
        {
            get => _windowTitle;
            private set => SetProperty(ref _windowTitle, value ?? FormatWindowTitle(null));
        }

        public ManagedProject? SelectedProjectRecord
        {
            get => _selectedProjectRecord;
            set
            {
                if (SetProperty(ref _selectedProjectRecord, value))
                {
                    OnPropertyChanged(nameof(ActiveProjectId));
                }
            }
        }

        public string ActiveProjectId => _selectedProjectRecord?.Id ?? string.Empty;

        public bool IsClosingProject
        {
            get => _isClosingProject;
            private set => SetProperty(ref _isClosingProject, value);
        }

        public ProjectAnalysisStatus AnalysisStatus
        {
            get => _analysisStatus;
            private set => SetProperty(ref _analysisStatus, value);
        }

        public ProjectAnalysisResult? CurrentAnalysisResult
        {
            get => _currentAnalysisResult;
            private set => SetProperty(ref _currentAnalysisResult, value);
        }

        public ProjectAnalysisReport? CurrentAnalysisReport
        {
            get => _currentAnalysisReport;
            private set => SetProperty(ref _currentAnalysisReport, value);
        }

        public string ProductName
        {
            get => _productName;
            private set => SetProperty(ref _productName, value ?? string.Empty);
        }

        public string TargetPlatform
        {
            get => _targetPlatform;
            private set => SetProperty(ref _targetPlatform, value ?? string.Empty);
        }

        public int? Il2CppMetadataVersion
        {
            get => _il2CppMetadataVersion;
            private set => SetProperty(ref _il2CppMetadataVersion, value);
        }

        public bool Il2CppMetadataObfuscated
        {
            get => _il2CppMetadataObfuscated;
            private set => SetProperty(ref _il2CppMetadataObfuscated, value);
        }

        public bool HasAddressables
        {
            get => _hasAddressables;
            private set => SetProperty(ref _hasAddressables, value);
        }

        public int Il2CppScriptCount
        {
            get => _il2CppScriptCount;
            private set => SetProperty(ref _il2CppScriptCount, value);
        }

        public int AddressablesBundleCount
        {
            get => _addressablesBundleCount;
            private set => SetProperty(ref _addressablesBundleCount, value);
        }

        public int DecryptionHintCount
        {
            get => _decryptionHintCount;
            private set
            {
                if (SetProperty(ref _decryptionHintCount, value))
                {
                    OnPropertyChanged(nameof(ShowDecryptionKeyButton));
                }
            }
        }

        public int EncryptedFileCount
        {
            get => _encryptedFileCount;
            private set
            {
                if (SetProperty(ref _encryptedFileCount, value))
                {
                    OnPropertyChanged(nameof(HasEncryptedFiles));
                    OnPropertyChanged(nameof(ShowDecryptionKeyButton));
                    OnPropertyChanged(nameof(TotalAssetFileCount));
                    OnPropertyChanged(nameof(CoverageSummaryText));
                }
            }
        }

        public int ReadableAssetFileCount
        {
            get => _readableAssetFileCount;
            private set
            {
                if (SetProperty(ref _readableAssetFileCount, value))
                {
                    OnPropertyChanged(nameof(TotalAssetFileCount));
                    OnPropertyChanged(nameof(CoverageSummaryText));
                }
            }
        }

        public int TotalAssetFileCount => _readableAssetFileCount + _encryptedFileCount;

        public double CoveragePercent
        {
            get => _coveragePercent;
            private set
            {
                if (SetProperty(ref _coveragePercent, value))
                {
                    OnPropertyChanged(nameof(CoverageSummaryText));
                }
            }
        }

        public bool HasEncryptedFiles => _encryptedFileCount > 0;

        public bool ShowDecryptionKeyButton => _encryptedFileCount > 0 || _decryptionHintCount > 0;

        public string CoverageSummaryText
        {
            get
            {
                if (_encryptedFileCount <= 0)
                {
                    return string.Empty;
                }

                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Coverage: {_coveragePercent:0.#}% ({_readableAssetFileCount:N0}/{TotalAssetFileCount:N0} files readable, {_encryptedFileCount:N0} encrypted — key needed)");
            }
        }

        public void UpdateEncryptionCoverage(int readableAssetFiles, int encryptedFiles)
        {
            _dispatchToUi(() =>
            {
                int safeReadable = Math.Max(0, readableAssetFiles);
                int safeEncrypted = Math.Max(0, encryptedFiles);
                ReadableAssetFileCount = safeReadable;
                EncryptedFileCount = safeEncrypted;
                CoveragePercent = ProjectScanResult.ComputeCoveragePercent(safeReadable, safeEncrypted);
                RuntimeBannerText = FormatEffectiveRuntimeBannerText(_currentAnalysisReport, safeReadable, safeEncrypted);
            });
        }

        public void SetLoadedProject(
            string? projectFolderPath,
            string? displayName = null,
            bool isLazyMode = false,
            string? unityVersion = null,
            UnityScriptingBackend scriptingBackend = UnityScriptingBackend.Unknown,
            string? productName = null,
            string? targetPlatform = null,
            string? cacheStatusSummary = null)
        {
            _dispatchToUi(() =>
            {
                var folder = projectFolderPath?.Trim() ?? string.Empty;
                var resolvedDisplay = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName.Trim()
                    : ExtractFolderDisplayName(folder);

                ProjectFolderPath = folder;
                ProjectDisplayName = resolvedDisplay;
                HasLoadedProject = !string.IsNullOrWhiteSpace(folder) || !string.IsNullOrWhiteSpace(resolvedDisplay);
                IsLazyMode = isLazyMode;
                UnityVersion = unityVersion?.Trim() ?? string.Empty;
                ScriptingBackend = scriptingBackend;
                ProductName = productName?.Trim() ?? string.Empty;
                TargetPlatform = targetPlatform?.Trim() ?? string.Empty;

                if (cacheStatusSummary != null)
                {
                    CacheStatusSummary = cacheStatusSummary.Trim();
                }

                if (!string.IsNullOrWhiteSpace(_unityVersion) && !string.IsNullOrWhiteSpace(_targetPlatform))
                {
                    WindowTitle = FormatLoadedWindowTitle(
                        !string.IsNullOrWhiteSpace(_productName) ? _productName : null,
                        _unityVersion,
                        _targetPlatform);
                }
                else
                {
                    WindowTitle = FormatWindowTitle(_projectDisplayName);
                }
            });
        }

        public void SetLoadedProject(ManagedProject? project, bool isLazyMode = false, string? cacheStatusSummary = null)
        {
            if (project == null)
            {
                ResetSession();
                return;
            }

            _dispatchToUi(() =>
            {
                SelectedProjectRecord = project;
                var folder = !string.IsNullOrWhiteSpace(project.ProjectRoot)
                    ? project.ProjectRoot.Trim()
                    : (project.LastLoadPath?.Trim() ?? string.Empty);

                ProjectFolderPath = folder;
                ProjectDisplayName = project.DisplayName;
                HasLoadedProject = true;
                IsLazyMode = isLazyMode;
                ProductName = string.Empty;
                TargetPlatform = string.Empty;

                if (cacheStatusSummary != null)
                {
                    CacheStatusSummary = cacheStatusSummary.Trim();
                }

                var summary = project.AnalysisSummary;
                if (summary != null)
                {
                    AnalysisStatus = summary.Status;
                    if (!string.IsNullOrWhiteSpace(summary.UnityVersion))
                    {
                        UnityVersion = summary.UnityVersion;
                    }

                    if (summary.ScriptingBackend != UnityScriptingBackend.Unknown)
                    {
                        ScriptingBackend = summary.ScriptingBackend;
                    }

                    Il2CppMetadataVersion = summary.Il2CppMetadataVersion;
                    HasAddressables = summary.HasAddressables;
                    Il2CppScriptCount = summary.Il2CppScriptCount;
                    AddressablesBundleCount = summary.AddressablesBundleCount;
                    DecryptionHintCount = summary.DecryptionHintCount;
                }

                WindowTitle = FormatWindowTitle(_projectDisplayName);
            });
        }

        public void ApplyProjectAnalysis(ProjectAnalysisResult? result)
        {
            _dispatchToUi(() =>
            {
                CurrentAnalysisResult = result;
                if (result == null)
                {
                    ClearAnalysisCore();
                    return;
                }

                ApplyProjectAnalysisReportCore(result.Report);
                if (string.IsNullOrWhiteSpace(_cacheStatusSummary))
                {
                    CacheStatusSummary = result.FromCache ? "Analysis loaded from cache" : "Analysis computed fresh";
                }
            });
        }

        public void ApplyProjectAnalysis(ProjectAnalysisReport? report)
        {
            _dispatchToUi(() =>
            {
                if (report == null)
                {
                    CurrentAnalysisResult = null;
                    ClearAnalysisCore();
                    return;
                }

                ApplyProjectAnalysisReportCore(report);
            });
        }

        private void ApplyProjectAnalysisReportCore(ProjectAnalysisReport report)
        {
            var snapshot = report.Clone();
            CurrentAnalysisReport = snapshot;
            AnalysisStatus = snapshot.Status;

            if (!string.IsNullOrWhiteSpace(snapshot.ProjectRoot) && string.IsNullOrWhiteSpace(_projectFolderPath))
            {
                ProjectFolderPath = snapshot.ProjectRoot.Trim();
                if (string.IsNullOrWhiteSpace(_projectDisplayName))
                {
                    ProjectDisplayName = ExtractFolderDisplayName(_projectFolderPath);
                }

                HasLoadedProject = true;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.UnityVersion))
            {
                UnityVersion = snapshot.UnityVersion.Trim();
            }

            if (snapshot.ScriptingBackend != UnityScriptingBackend.Unknown || snapshot.Status == ProjectAnalysisStatus.Ready)
            {
                ScriptingBackend = snapshot.ScriptingBackend;
            }

            Il2CppMetadataVersion = snapshot.Il2CppMetadataVersion;
            Il2CppMetadataObfuscated = snapshot.Il2CppMetadataObfuscated;
            HasAddressables = snapshot.HasAddressables;
            Il2CppScriptCount = snapshot.Il2CppScriptCount;
            AddressablesBundleCount = snapshot.AddressablesBundleCount;
            DecryptionHintCount = snapshot.DecryptionHintCount;
            RuntimeBannerText = FormatEffectiveRuntimeBannerText(snapshot, _readableAssetFileCount, _encryptedFileCount);

            if (_selectedProjectRecord != null && IsSameProjectRoot(_selectedProjectRecord.ProjectRoot, snapshot.ProjectRoot))
            {
                _selectedProjectRecord.AnalysisSummary = ProjectAnalysisSummary.FromReport(snapshot);
            }
        }

        private void ClearAnalysisCore()
        {
            CurrentAnalysisReport = null;
            AnalysisStatus = ProjectAnalysisStatus.Pending;
            Il2CppMetadataVersion = null;
            Il2CppMetadataObfuscated = false;
            HasAddressables = false;
            Il2CppScriptCount = 0;
            AddressablesBundleCount = 0;
            DecryptionHintCount = 0;
            RuntimeBannerText = FormatEffectiveRuntimeBannerText(null, _readableAssetFileCount, _encryptedFileCount);
        }

        public string UpdateWindowTitle(string? projectDisplayName = null)
        {
            var title = string.Empty;
            _dispatchToUi(() =>
            {
                if (projectDisplayName != null)
                {
                    ProjectDisplayName = projectDisplayName.Trim();
                }

                var effectiveName = projectDisplayName ?? _projectDisplayName;
                title = FormatWindowTitle(effectiveName);
                WindowTitle = title;
            });
            return title;
        }

        public string UpdateWindowTitle(string? productName, string? unityVersion, string? targetPlatform)
        {
            var title = string.Empty;
            _dispatchToUi(() =>
            {
                if (productName != null)
                {
                    ProductName = productName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(unityVersion))
                {
                    UnityVersion = unityVersion.Trim();
                }

                if (!string.IsNullOrWhiteSpace(targetPlatform))
                {
                    TargetPlatform = targetPlatform.Trim();
                }

                title = FormatLoadedWindowTitle(productName, unityVersion, targetPlatform);
                WindowTitle = title;
            });
            return title;
        }

        public string UpdateWindowTitle(string? productName, SerializedFile? firstFile)
        {
            if (firstFile == null)
            {
                return UpdateWindowTitle(productName ?? _projectDisplayName);
            }

            return UpdateWindowTitle(productName, firstFile.unityVersion, firstFile.m_TargetPlatform.ToString());
        }

        public void RefreshSavedProjects(ProjectManagerStore store, string? selectProjectId = null)
        {
            ArgumentNullException.ThrowIfNull(store);

            var loadedProjects = store.GetProjects()
                .Select(project =>
                {
                    if (project.AnalysisSummary.Status == ProjectAnalysisStatus.Pending
                        && !string.IsNullOrWhiteSpace(project.ProjectRoot))
                    {
                        var cached = store.LoadCachedProjectAnalysisReport(project.ProjectRoot);
                        if (cached != null)
                        {
                            project.AnalysisSummary = ProjectAnalysisSummary.FromReport(cached);
                        }
                    }

                    return project;
                })
                .ToList();

            _dispatchToUi(() =>
            {
                var previousSelectedId = selectProjectId ?? _selectedProjectRecord?.Id;
                if (loadedProjects.Count == 0)
                {
                    if (SavedProjects.Count > 0)
                    {
                        SavedProjects.Clear();
                    }

                    SelectedProjectRecord = null;
                    return;
                }

                SavedProjects.ReplaceAll(loadedProjects);

                if (!string.IsNullOrWhiteSpace(previousSelectedId))
                {
                    var match = loadedProjects.FirstOrDefault(p => string.Equals(p.Id, previousSelectedId, StringComparison.Ordinal));
                    SelectedProjectRecord = match ?? loadedProjects[0];
                }
                else if (_selectedProjectRecord == null)
                {
                    SelectedProjectRecord = loadedProjects[0];
                }
            });
        }

        public void SelectProjectRecord(ManagedProject? project, bool applyToSession = false)
        {
            _dispatchToUi(() =>
            {
                SelectedProjectRecord = project;
                if (applyToSession)
                {
                    if (project == null)
                    {
                        ResetSession(keepSavedProjects: true);
                    }
                    else
                    {
                        SetLoadedProject(project, _isLazyMode);
                    }
                }
            });
        }

        public void BeginClosingProject()
        {
            _dispatchToUi(() =>
            {
                IsClosingProject = true;
            });
        }

        public void CancelClosingProject()
        {
            _dispatchToUi(() =>
            {
                IsClosingProject = false;
            });
        }

        public void ResetSession(bool keepSavedProjects = true)
        {
            _dispatchToUi(() =>
            {
                ProjectFolderPath = string.Empty;
                ProjectDisplayName = string.Empty;
                HasLoadedProject = false;
                IsLazyMode = false;
                IsClosingProject = false;
                UnityVersion = string.Empty;
                ScriptingBackend = UnityScriptingBackend.Unknown;
                CacheStatusSummary = string.Empty;
                ProductName = string.Empty;
                TargetPlatform = string.Empty;
                ReadableAssetFileCount = 0;
                EncryptedFileCount = 0;
                CoveragePercent = 100.0;
                CurrentAnalysisResult = null;
                ClearAnalysisCore();
                SelectedProjectRecord = null;
                WindowTitle = FormatWindowTitle(null);

                if (!keepSavedProjects && SavedProjects.Count > 0)
                {
                    SavedProjects.Clear();
                }
            });
        }

        public static string FormatWindowTitle(string? projectDisplayName = null)
        {
            var version = AppVersionInfo.PrefixedDisplayVersion;
            return string.IsNullOrWhiteSpace(projectDisplayName)
                ? $"AssetStudio {version}"
                : $"AssetStudio {version} - {projectDisplayName.Trim()}";
        }

        public static string FormatLoadedWindowTitle(string? productName, string? unityVersion, string? targetPlatform)
        {
            var version = AppVersionInfo.PrefixedDisplayVersion;
            var label = !string.IsNullOrEmpty(productName) ? productName : "no productName";
            return $"AssetStudio {version} - {label} - {unityVersion} - {targetPlatform}";
        }

        public static string FormatLoadedWindowTitle(string? productName, SerializedFile? firstFile)
        {
            if (firstFile == null)
            {
                return FormatWindowTitle(productName);
            }

            return FormatLoadedWindowTitle(productName, firstFile.unityVersion, firstFile.m_TargetPlatform.ToString());
        }

        public static string FormatRuntimeBannerText(ProjectAnalysisReport? report)
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

        public static string FormatEffectiveRuntimeBannerText(
            ProjectAnalysisReport? report,
            int readableAssetFiles,
            int encryptedFiles)
        {
            var baseText = FormatRuntimeBannerText(report);
            if (encryptedFiles <= 0)
            {
                return baseText;
            }

            int safeReadable = Math.Max(0, readableAssetFiles);
            int total = safeReadable + encryptedFiles;
            double pct = ProjectScanResult.ComputeCoveragePercent(safeReadable, encryptedFiles);
            var coverageText = string.Create(
                CultureInfo.InvariantCulture,
                $"Partial load: {pct:0.#}% ({safeReadable:N0}/{total:N0} files readable, {encryptedFiles:N0} encrypted — key needed)");

            return string.IsNullOrWhiteSpace(baseText)
                ? coverageText
                : $"{baseText} \u2022 {coverageText}";
        }

        internal static bool IsSameProjectRoot(string? left, string? right)
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

        private static string ExtractFolderDisplayName(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            var trimmed = folderPath.AsSpan().Trim().TrimEnd("/\\");
            if (trimmed.IsEmpty)
            {
                return string.Empty;
            }

            var lastSep = trimmed.LastIndexOfAny('/', '\\');
            var segment = lastSep >= 0 ? trimmed[(lastSep + 1)..].Trim() : trimmed;
            return segment.IsEmpty ? trimmed.ToString() : segment.ToString();
        }
    }
}
