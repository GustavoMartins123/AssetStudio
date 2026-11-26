using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using AssetStudio.Avalonia.Services.Scripting;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class CSharpProjectExportDialogRequest
    {
        public CSharpProjectExportDialogRequest(
            IReadOnlyList<AssemblyInfoEntry> assemblies,
            string? sourceDescription = null,
            string? defaultProjectName = null,
            bool isIl2CppStubMode = false,
            string? dialogTitle = null)
        {
            Assemblies = assemblies ?? Array.Empty<AssemblyInfoEntry>();
            SourceDescription = string.IsNullOrWhiteSpace(sourceDescription)
                ? $"{Assemblies.Count} assemblies detected"
                : sourceDescription.Trim();
            DefaultProjectName = string.IsNullOrWhiteSpace(defaultProjectName)
                ? "ExportedScripts"
                : defaultProjectName.Trim();
            IsIl2CppStubMode = isIl2CppStubMode;
            DialogTitle = string.IsNullOrWhiteSpace(dialogTitle)
                ? "Export C# Script Project"
                : dialogTitle.Trim();
        }

        public IReadOnlyList<AssemblyInfoEntry> Assemblies { get; }
        public string SourceDescription { get; }
        public string DefaultProjectName { get; }
        public bool IsIl2CppStubMode { get; }
        public string DialogTitle { get; }
    }

    public sealed class CSharpProjectExportDialogResult
    {
        public CSharpProjectExportDialogResult(
            IReadOnlyList<string> selectedAssemblyNames,
            string projectName,
            CSharpProjectLayoutMode layoutMode,
            bool includeEngineAndThirdPartyAssemblies,
            bool generateUnityMetaFiles,
            bool generateAsmdefFiles,
            bool copyReferenceDlls,
            bool enableConservativeDecompilerFallback,
            bool sanitizeStrippedEditorAttributes)
        {
            SelectedAssemblyNames = selectedAssemblyNames ?? Array.Empty<string>();
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? "ExportedScripts" : projectName.Trim();
            LayoutMode = layoutMode;
            IncludeEngineAndThirdPartyAssemblies = includeEngineAndThirdPartyAssemblies;
            GenerateUnityMetaFiles = generateUnityMetaFiles;
            GenerateAsmdefFiles = generateAsmdefFiles;
            CopyReferenceDlls = copyReferenceDlls;
            EnableConservativeDecompilerFallback = enableConservativeDecompilerFallback;
            SanitizeStrippedEditorAttributes = sanitizeStrippedEditorAttributes;
        }

        public IReadOnlyList<string> SelectedAssemblyNames { get; }
        public string ProjectName { get; }
        public CSharpProjectLayoutMode LayoutMode { get; }
        public bool IncludeEngineAndThirdPartyAssemblies { get; }
        public bool GenerateUnityMetaFiles { get; }
        public bool GenerateAsmdefFiles { get; }
        public bool CopyReferenceDlls { get; }
        public bool EnableConservativeDecompilerFallback { get; }
        public bool SanitizeStrippedEditorAttributes { get; }
    }

    public sealed class CSharpExportAssemblyRowViewModel : ViewModelBase
    {
        private readonly Action _onSelectionChanged;
        private bool _isSelected;

        public CSharpExportAssemblyRowViewModel(
            AssemblyInfoEntry assembly,
            bool initialSelected,
            Action onSelectionChanged)
        {
            Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            _onSelectionChanged = onSelectionChanged ?? throw new ArgumentNullException(nameof(onSelectionChanged));
            IsLegacyUnityScript = ScriptProjectAssemblyClassifier.IsLegacyUnityScriptAssembly(assembly.AssemblyName);
            _isSelected = initialSelected;
        }

        public AssemblyInfoEntry Assembly { get; }

        public string AssemblyName => Assembly.AssemblyName;

        public ScriptAssemblyCategory Category => Assembly.Category;

        public bool IsGameAssembly => Assembly.IsGameAssembly;

        public bool IsLegacyUnityScript { get; }

        public bool IsDefaultCSharpGameAssembly => IsGameAssembly && !IsLegacyUnityScript;

        public string CategoryLabel => IsLegacyUnityScript
            ? "Legacy"
            : Category switch
            {
                ScriptAssemblyCategory.Game => "Game",
                ScriptAssemblyCategory.ThirdParty => "Third-Party",
                ScriptAssemblyCategory.UnityEngine => "UnityEngine",
                ScriptAssemblyCategory.Framework => "Framework",
                _ => "Game"
            };

        public int TypeCount => Assembly.TypeCount;

        public string TypeCountText => string.Create(
            CultureInfo.InvariantCulture,
            $"{TypeCount:N0} {(TypeCount == 1 ? "type" : "types")}");

        public string FileSizeText => FormatFileSize(Assembly.FileSizeBytes);

        public string SubtitleDisplay => string.Create(
            CultureInfo.InvariantCulture,
            $"{TypeCountText}  •  {FileSizeText}");

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    _onSelectionChanged();
                }
            }
        }

        internal void SetSelectedQuiet(bool selected)
        {
            SetProperty(ref _isSelected, selected, nameof(IsSelected));
        }

        internal static string FormatFileSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "—";
            }

            if (bytes < 1024)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
            }

            var kb = bytes / 1024.0;
            if (kb < 1024.0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{kb:0.0} KB");
            }

            var mb = kb / 1024.0;
            return string.Create(CultureInfo.InvariantCulture, $"{mb:0.00} MB");
        }
    }

    public sealed class CSharpProjectExportDialogViewModel : ViewModelBase
    {
        private static readonly string[] LayoutModeChoices =
        {
            "Standalone .NET Solution (.sln + .csproj)",
            "Unity Assets/Scripts (.asmdef + .cs.meta)"
        };

        private readonly List<CSharpExportAssemblyRowViewModel> _allAssemblies;
        private readonly RelayCommand _selectAllVisibleCommand;
        private readonly RelayCommand _selectGameOnlyCommand;
        private readonly RelayCommand _deselectAllCommand;
        private readonly RelayCommand _clearSearchCommand;
        private readonly RelayCommand _confirmCommand;
        private readonly RelayCommand _cancelCommand;

        private string _searchText = string.Empty;
        private bool _showEngineAndThirdParty;
        private string _projectName;
        private CSharpProjectLayoutMode _layoutMode = CSharpProjectLayoutMode.StandaloneDotNetSolution;
        private bool _generateUnityMetaFiles = true;
        private bool _generateAsmdefFiles = true;
        private bool _copyReferenceDlls = true;
        private bool _enableConservativeDecompilerFallback = true;
        private bool _sanitizeStrippedEditorAttributes = true;

        public CSharpProjectExportDialogViewModel()
            : this(new CSharpProjectExportDialogRequest(Array.Empty<AssemblyInfoEntry>()))
        {
        }

        public CSharpProjectExportDialogViewModel(CSharpProjectExportDialogRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            DialogTitle = request.DialogTitle;
            SourceDescription = request.SourceDescription;
            IsIl2CppStubMode = request.IsIl2CppStubMode;
            _projectName = request.DefaultProjectName;

            var sortedEntries = request.Assemblies
                .Where(static a => a != null && !string.IsNullOrWhiteSpace(a.AssemblyName))
                .OrderBy(GetAssemblySortGroup)
                .ThenBy(static a => a.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _allAssemblies = new List<CSharpExportAssemblyRowViewModel>(sortedEntries.Count);
            for (var i = 0; i < sortedEntries.Count; i++)
            {
                var entry = sortedEntries[i];
                var isDefaultSelected = entry.IsGameAssembly
                    && !ScriptProjectAssemblyClassifier.IsLegacyUnityScriptAssembly(entry.AssemblyName);
                _allAssemblies.Add(new CSharpExportAssemblyRowViewModel(entry, isDefaultSelected, OnRowSelectionChanged));
            }

            FilteredAssemblies = new ObservableCollection<CSharpExportAssemblyRowViewModel>();

            _selectAllVisibleCommand = new RelayCommand(SelectAllVisible, () => FilteredAssemblies.Count > 0);
            _selectGameOnlyCommand = new RelayCommand(SelectGameOnly, () => _allAssemblies.Count > 0);
            _deselectAllCommand = new RelayCommand(DeselectAll, () => SelectedCount > 0);
            _clearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrEmpty(SearchText));
            _confirmCommand = new RelayCommand(Confirm, () => SelectedCount > 0);
            _cancelCommand = new RelayCommand(Cancel);

            ApplyFilter();
        }

        public event Action<CSharpProjectExportDialogResult?>? RequestClose;

        public string DialogTitle { get; }

        public string SourceDescription { get; }

        public bool IsIl2CppStubMode { get; }

        public string Il2CppNoticeText => IsIl2CppStubMode
            ? "IL2CPP stub source detected: method bodies are native code; exported C# files contain metadata signatures and RVA annotations."
            : string.Empty;

        public CSharpProjectExportDialogResult? Result { get; private set; }

        public IReadOnlyList<CSharpExportAssemblyRowViewModel> AllAssemblies => _allAssemblies;

        public ObservableCollection<CSharpExportAssemblyRowViewModel> FilteredAssemblies { get; }

        public IReadOnlyList<string> LayoutModeOptions => LayoutModeChoices;

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _searchText, normalized))
                {
                    ApplyFilter();
                    _clearSearchCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool ShowEngineAndThirdParty
        {
            get => _showEngineAndThirdParty;
            set
            {
                if (SetProperty(ref _showEngineAndThirdParty, value))
                {
                    ApplyFilter();
                    OnPropertyChanged(nameof(SelectionSummaryText));
                }
            }
        }

        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value ?? string.Empty);
        }

        public CSharpProjectLayoutMode LayoutMode
        {
            get => _layoutMode;
            set
            {
                if (SetProperty(ref _layoutMode, value))
                {
                    var isStandalone = value == CSharpProjectLayoutMode.StandaloneDotNetSolution;
                    SanitizeStrippedEditorAttributes = isStandalone;
                    CopyReferenceDlls = isStandalone;
                    OnPropertyChanged(nameof(SelectedLayoutModeIndex));
                    OnPropertyChanged(nameof(IsStandaloneLayout));
                    OnPropertyChanged(nameof(IsUnityAssetsLayout));
                }
            }
        }

        public int SelectedLayoutModeIndex
        {
            get => _layoutMode == CSharpProjectLayoutMode.UnityAssetsScripts ? 1 : 0;
            set => LayoutMode = value == 1
                ? CSharpProjectLayoutMode.UnityAssetsScripts
                : CSharpProjectLayoutMode.StandaloneDotNetSolution;
        }

        public bool IsStandaloneLayout => _layoutMode == CSharpProjectLayoutMode.StandaloneDotNetSolution;

        public bool IsUnityAssetsLayout => _layoutMode == CSharpProjectLayoutMode.UnityAssetsScripts;

        public bool GenerateUnityMetaFiles
        {
            get => _generateUnityMetaFiles;
            set => SetProperty(ref _generateUnityMetaFiles, value);
        }

        public bool GenerateAsmdefFiles
        {
            get => _generateAsmdefFiles;
            set => SetProperty(ref _generateAsmdefFiles, value);
        }

        public bool CopyReferenceDlls
        {
            get => _copyReferenceDlls;
            set => SetProperty(ref _copyReferenceDlls, value);
        }

        public bool EnableConservativeDecompilerFallback
        {
            get => _enableConservativeDecompilerFallback;
            set => SetProperty(ref _enableConservativeDecompilerFallback, value);
        }

        public bool SanitizeStrippedEditorAttributes
        {
            get => _sanitizeStrippedEditorAttributes;
            set => SetProperty(ref _sanitizeStrippedEditorAttributes, value);
        }

        public int TotalAssemblyCount => _allAssemblies.Count;

        public int GameAssemblyCount => _allAssemblies.Count(static a => a.IsGameAssembly);

        public int FilteredCount => FilteredAssemblies.Count;

        public bool HasNoFilteredAssemblies => FilteredAssemblies.Count == 0;

        public int SelectedCount => _allAssemblies.Count(static a => a.IsSelected);

        public int SelectedTypeCount => _allAssemblies.Where(static a => a.IsSelected).Sum(static a => a.TypeCount);

        public bool CanConfirm => SelectedCount > 0;

        public string SelectionSummaryText
        {
            get
            {
                var selected = SelectedCount;
                if (selected == 0)
                {
                    return "Selected: 0 assemblies — select at least one assembly to export.";
                }

                var selectedTypes = SelectedTypeCount;
                var visibleSelected = FilteredAssemblies.Count(static a => a.IsSelected);
                var hiddenSelected = selected - visibleSelected;
                var hiddenSuffix = hiddenSelected > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" ({hiddenSelected:N0} hidden by filter)")
                    : string.Empty;

                var hasNonGameSelected = _allAssemblies.Any(static a => a.IsSelected && !a.IsGameAssembly);
                if (!hasNonGameSelected && GameAssemblyCount > 0)
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"Selected: {selected:N0} of {GameAssemblyCount:N0} game assemblies ({selectedTypes:N0} types){hiddenSuffix}");
                }

                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Selected: {selected:N0} of {TotalAssemblyCount:N0} assemblies ({selectedTypes:N0} types){hiddenSuffix}");
            }
        }

        public RelayCommand SelectAllVisibleCommand => _selectAllVisibleCommand;
        public RelayCommand SelectGameOnlyCommand => _selectGameOnlyCommand;
        public RelayCommand DeselectAllCommand => _deselectAllCommand;
        public RelayCommand ClearSearchCommand => _clearSearchCommand;
        public RelayCommand ConfirmCommand => _confirmCommand;
        public RelayCommand CancelCommand => _cancelCommand;

        public CSharpProjectExportDialogResult BuildResult()
        {
            var selectedNames = _allAssemblies
                .Where(static a => a.IsSelected)
                .Select(static a => a.AssemblyName)
                .ToList();

            var includeNonGame = _allAssemblies.Any(static a => a.IsSelected && !a.IsGameAssembly);
            var safeProjectName = string.IsNullOrWhiteSpace(ProjectName)
                ? "ExportedScripts"
                : ScriptExportPathSanitizer.SanitizeSegment(ProjectName, "ExportedScripts");

            return new CSharpProjectExportDialogResult(
                selectedAssemblyNames: selectedNames,
                projectName: safeProjectName,
                layoutMode: LayoutMode,
                includeEngineAndThirdPartyAssemblies: includeNonGame,
                generateUnityMetaFiles: GenerateUnityMetaFiles,
                generateAsmdefFiles: GenerateAsmdefFiles,
                copyReferenceDlls: CopyReferenceDlls,
                enableConservativeDecompilerFallback: EnableConservativeDecompilerFallback,
                sanitizeStrippedEditorAttributes: SanitizeStrippedEditorAttributes);
        }

        private void ApplyFilter()
        {
            var query = _searchText.Trim();
            FilteredAssemblies.Clear();

            for (var i = 0; i < _allAssemblies.Count; i++)
            {
                var row = _allAssemblies[i];
                if (!_showEngineAndThirdParty && !row.IsGameAssembly)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(query) && !MatchesSearch(row, query))
                {
                    continue;
                }

                FilteredAssemblies.Add(row);
            }

            OnPropertyChanged(nameof(FilteredCount));
            OnPropertyChanged(nameof(HasNoFilteredAssemblies));
            OnPropertyChanged(nameof(SelectionSummaryText));
            _selectAllVisibleCommand.NotifyCanExecuteChanged();
        }

        private static bool MatchesSearch(CSharpExportAssemblyRowViewModel row, string query)
        {
            return row.AssemblyName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Assembly.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void SelectAllVisible()
        {
            for (var i = 0; i < FilteredAssemblies.Count; i++)
            {
                FilteredAssemblies[i].SetSelectedQuiet(true);
            }

            OnRowSelectionChanged();
        }

        private void SelectGameOnly()
        {
            for (var i = 0; i < _allAssemblies.Count; i++)
            {
                var row = _allAssemblies[i];
                row.SetSelectedQuiet(row.IsDefaultCSharpGameAssembly);
            }

            OnRowSelectionChanged();
        }

        private void DeselectAll()
        {
            for (var i = 0; i < _allAssemblies.Count; i++)
            {
                _allAssemblies[i].SetSelectedQuiet(false);
            }

            OnRowSelectionChanged();
        }

        private void OnRowSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectedTypeCount));
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(SelectionSummaryText));
            _confirmCommand.NotifyCanExecuteChanged();
            _deselectAllCommand.NotifyCanExecuteChanged();
        }

        private void Confirm()
        {
            if (SelectedCount == 0)
            {
                return;
            }

            Result = BuildResult();
            RequestClose?.Invoke(Result);
        }

        private void Cancel()
        {
            Result = null;
            RequestClose?.Invoke(null);
        }

        private static int GetAssemblySortGroup(AssemblyInfoEntry entry)
        {
            if (entry.IsGameAssembly)
            {
                return ScriptProjectAssemblyClassifier.IsLegacyUnityScriptAssembly(entry.AssemblyName) ? 1 : 0;
            }

            return entry.Category switch
            {
                ScriptAssemblyCategory.ThirdParty => 2,
                ScriptAssemblyCategory.UnityEngine => 3,
                ScriptAssemblyCategory.Framework => 4,
                _ => 5
            };
        }
    }
}
