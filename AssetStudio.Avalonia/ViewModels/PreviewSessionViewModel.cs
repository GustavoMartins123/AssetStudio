using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.Scripting;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum RightPanelTab
    {
        Preview = 0,
        Dump = 1,
        References = 2,
        Script = 3
    }

    public enum PreviewSurfaceKind
    {
        None = 0,
        Text = 1,
        Texture = 2,
        Sprite = 3,
        Font = 4,
        Mesh3D = 5,
        Animation3D = 6,
        Animation2D = 7,
        Avatar = 8,
        Material = 9,
        Audio = 10,
        Video = 11,
        Shader = 12,
        MonoBehaviour = 13,
        AnimatorGraph = 14,
        Prefab = 15,
    }

    public enum TextAssetDisplayMode
    {
        Cards = 0,
        Text = 1,
        Info = 2,
    }

    [Flags]
    public enum PreviewChannelMask
    {
        None = 0,
        Red = 1 << 0,
        Green = 1 << 1,
        Blue = 1 << 2,
        Alpha = 1 << 3,
        Rgb = Red | Green | Blue,
        All = Red | Green | Blue | Alpha,
    }

    public sealed class PreviewSessionViewModel : ViewModelBase, IDisposable
    {
        public const int MaxPreviewTextChars = 100_000;
        public const string DefaultPlaceholderTitle = "[Preview Panel]";
        public const string DefaultCandidateLabel = "Candidate";

        private readonly Action<Action> _dispatchToUi;
        private readonly object _stateLock = new();
        private readonly AsyncPreviewRequestCoordinator _previewCoordinator = new();
        private readonly AsyncPreviewRequestCoordinator _dumpCoordinator = new();
        private readonly AsyncPreviewRequestCoordinator _referencesCoordinator = new();
        private readonly AsyncPreviewRequestCoordinator _inspectorCoordinator = new();
        private readonly AsyncPreviewRequestCoordinator _scriptCoordinator = new();
        private readonly AnimationPreviewLoadCoordinator _animationCoordinator = new();
        private readonly AssetNavigationHistory _navigationHistory = new();

        private long _previewVersion;
        private PreviewSurfaceKind _activeKind = PreviewSurfaceKind.None;
        private AssetItem? _activeAsset;
        private bool _isPreviewEnabled = true;
        private bool _isDisplayInfoEnabled = true;
        private bool _isLoading;
        private string _loadingStatusText = string.Empty;
        private string _previewTitle = DefaultPlaceholderTitle;
        private string _previewSubtitle = string.Empty;
        private string _previewMetadataSummary = string.Empty;
        private string _textContent = string.Empty;
        private string _dumpTextContent = string.Empty;
        private bool _isDumpLoading;
        private RightPanelTab _activeRightTab = RightPanelTab.Preview;
        private bool _isReferencesLoading;
        private string _referencesFilterText = string.Empty;
        private string _referencesTargetTitle = DefaultReferencesTargetTitle;
        private string _referencesHeaderSummary = DefaultReferencesSummary;
        private string _referencedBySectionHeader = DefaultReferencedByHeader;
        private string _referencesSectionHeader = DefaultReferencesHeader;
        private bool _hasReferencedByItems;
        private bool _hasReferencesItems;
        private bool _hasNoReferences;
        private AssetReferenceItem? _selectedReferenceItem;
        private IReadOnlyList<AssetReferenceItem> _allReferencedBy = Array.Empty<AssetReferenceItem>();
        private IReadOnlyList<AssetReferenceItem> _allReferences = Array.Empty<AssetReferenceItem>();
        private bool _isDialogueCardsVisible;
        private bool _hasDialoguePreview;
        private TextAssetDisplayMode _dialogueDisplayMode = TextAssetDisplayMode.Cards;
        private bool _isInfoExpanded = true;
        private string _dialoguePlainTextScript = string.Empty;
        private string _dialogueDetailsText = string.Empty;
        private PreviewChannelMask _channelMask = PreviewChannelMask.All;
        private bool _showRed = true;
        private bool _showGreen = true;
        private bool _showBlue = true;
        private bool _showAlpha = true;
        private PreviewCandidateItem? _selectedPreviewCandidate;
        private bool _hasCandidates;
        private string _candidateSelectorLabel = DefaultCandidateLabel;
        private string _cacheBadgeText = string.Empty;
        private bool _isCacheBadgeVisible;
        private bool _lastPreviewWasFromCache;
        private string? _lastPreviewSourceKind;
        private bool _lastPreviewMaterialsLoading;

        // Phase 1: MonoBehaviour Searchable Tree / JSON Viewer
        private IReadOnlyList<AssetPropertyNode> _allMonoBehaviourTreeRoots = Array.Empty<AssetPropertyNode>();
        private string _monoBehaviourFilterText = string.Empty;
        private bool _hasMonoBehaviourTree;
        private bool _isMonoBehaviourJsonMode;
        private string _monoBehaviourScriptTitle = string.Empty;
        private string _monoBehaviourSummaryText = string.Empty;
        private int _monoBehaviourPPtrCount;
        private string _monoBehaviourJsonText = string.Empty;
        private string _monoBehaviourFullJsonText = string.Empty;

        // Phase 1: Scene Hierarchy GameObject Component Inspector
        private bool _isGameObjectInspectorVisible;
        private bool _isGameObjectInspectorLoading;
        private GameObjectInspectorSnapshot? _inspectedGameObject;
        private IReadOnlyList<GameObjectComponentInfo> _allInspectorComponents = Array.Empty<GameObjectComponentInfo>();
        private string _inspectorFilterText = string.Empty;
        private string _inspectorComponentsHeader = DefaultInspectorComponentsHeader;

        // Phase 1: Back / Forward Navigation History
        private bool _canNavigateBack;
        private bool _canNavigateForward;
        private string _navigateBackTooltip = DefaultNavigateBackTooltip;
        private string _navigateForwardTooltip = DefaultNavigateForwardTooltip;

        // Phase 2: Integrated Script Decompiler & Ghidra Export
        private bool _isScriptLoading;
        private bool _hasScriptSource;
        private string _scriptTitle = DefaultScriptTitle;
        private string _scriptSummaryText = DefaultScriptSummary;
        private string _scriptBackendBadge = string.Empty;
        private string _scriptSourceCode = string.Empty;
        private string _scriptFullSourceCode = string.Empty;
        private string _scriptSearchText = string.Empty;
        private string _scriptSearchMatchSummary = string.Empty;
        private bool _isScriptSearchFilterOnly;
        private bool _isScriptWordWrapEnabled = true;
        private string _scriptDiagnosticText = string.Empty;
        private string _scriptAssemblyName = string.Empty;
        private string _scriptNamespace = string.Empty;
        private string _scriptClassName = string.Empty;
        private IReadOnlyList<ScriptMethodSymbol> _scriptMethodSymbols = Array.Empty<ScriptMethodSymbol>();

        public const string DefaultReferencesTargetTitle = "No asset selected";
        public const string DefaultReferencesSummary = "Select an asset to inspect references.";
        public const string DefaultReferencedByHeader = "Referenced By / Used By (0)";
        public const string DefaultReferencesHeader = "References / Uses (0)";
        public const string DefaultInspectorComponentsHeader = "Components (0)";
        public const string DefaultNavigateBackTooltip = "Back (Alt+Left)";
        public const string DefaultNavigateForwardTooltip = "Forward (Alt+Right)";
        public const string DefaultScriptTitle = "No script selected";
        public const string DefaultScriptSummary = "Select a MonoScript or MonoBehaviour to view C# code.";

        public PreviewSessionViewModel(Action<Action>? dispatchToUi = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            ShaderPreview = new ShaderPreviewViewModel(_dispatchToUi);
            DialogueCards = new BulkObservableCollection<TextAssetDialogueCardViewModel>();
            PreviewCandidates = new BulkObservableCollection<PreviewCandidateItem>();
            FilteredReferencedByItems = new BulkObservableCollection<AssetReferenceItem>();
            FilteredReferencesItems = new BulkObservableCollection<AssetReferenceItem>();
            MonoBehaviourTreeRoots = new BulkObservableCollection<AssetPropertyNode>();
            InspectorComponents = new BulkObservableCollection<GameObjectComponentInfo>();
        }

        internal AsyncPreviewRequestCoordinator PreviewCoordinator => _previewCoordinator;

        internal AsyncPreviewRequestCoordinator DumpCoordinator => _dumpCoordinator;

        internal AsyncPreviewRequestCoordinator ReferencesCoordinator => _referencesCoordinator;

        internal AsyncPreviewRequestCoordinator InspectorCoordinator => _inspectorCoordinator;

        internal AsyncPreviewRequestCoordinator ScriptCoordinator => _scriptCoordinator;

        internal AnimationPreviewLoadCoordinator AnimationCoordinator => _animationCoordinator;

        public AssetNavigationHistory NavigationHistory => _navigationHistory;

        public long CurrentPreviewRequestId => _previewCoordinator.CurrentRequestId;

        public long CancelledPreviewRequestCount => _previewCoordinator.CancelledRequestCount;

        public long CurrentDumpRequestId => _dumpCoordinator.CurrentRequestId;

        public long CancelledDumpRequestCount => _dumpCoordinator.CancelledRequestCount;

        public long CurrentReferencesRequestId => _referencesCoordinator.CurrentRequestId;

        public long CancelledReferencesRequestCount => _referencesCoordinator.CancelledRequestCount;

        public long CurrentInspectorRequestId => _inspectorCoordinator.CurrentRequestId;

        public long CancelledInspectorRequestCount => _inspectorCoordinator.CancelledRequestCount;

        public long CurrentScriptRequestId => _scriptCoordinator.CurrentRequestId;

        public long CancelledScriptRequestCount => _scriptCoordinator.CancelledRequestCount;

        public long CurrentAnimationRequestId => _animationCoordinator.CurrentRequestId;

        public long CancelledAnimationRequestCount => _animationCoordinator.CancelledRequestCount;

        public long PreviewVersion => Interlocked.Read(ref _previewVersion);

        public BulkObservableCollection<TextAssetDialogueCardViewModel> DialogueCards { get; }

        public BulkObservableCollection<PreviewCandidateItem> PreviewCandidates { get; }

        public BulkObservableCollection<AssetReferenceItem> FilteredReferencedByItems { get; }

        public BulkObservableCollection<AssetReferenceItem> FilteredReferencesItems { get; }

        public BulkObservableCollection<AssetPropertyNode> MonoBehaviourTreeRoots { get; }

        public BulkObservableCollection<GameObjectComponentInfo> InspectorComponents { get; }

        public ShaderPreviewViewModel ShaderPreview { get; }

        public PreviewSurfaceKind ActiveKind
        {
            get => _activeKind;
            set
            {
                if (SetProperty(ref _activeKind, value))
                {
                    OnPropertyChanged(nameof(HasActivePreview));
                    OnPropertyChanged(nameof(IsTextSurfaceVisible));
                    OnPropertyChanged(nameof(IsImageSurfaceVisible));
                    OnPropertyChanged(nameof(Is3DSurfaceVisible));
                    OnPropertyChanged(nameof(IsMediaSurfaceVisible));
                }
            }
        }

        public bool HasActivePreview => _activeKind != PreviewSurfaceKind.None;

        public bool IsTextSurfaceVisible =>
            _activeKind is PreviewSurfaceKind.Text
                or PreviewSurfaceKind.MonoBehaviour
                or PreviewSurfaceKind.AnimatorGraph
                or PreviewSurfaceKind.Prefab
            && !_isDialogueCardsVisible
            && !_hasMonoBehaviourTree
            && !_isGameObjectInspectorVisible;

        public bool IsImageSurfaceVisible =>
            _activeKind is PreviewSurfaceKind.Texture
                or PreviewSurfaceKind.Sprite
                or PreviewSurfaceKind.Font
                or PreviewSurfaceKind.Animation2D;

        public bool Is3DSurfaceVisible =>
            _activeKind is PreviewSurfaceKind.Mesh3D
                or PreviewSurfaceKind.Animation3D
                or PreviewSurfaceKind.Avatar
                or PreviewSurfaceKind.Material;

        public bool IsMediaSurfaceVisible =>
            _activeKind is PreviewSurfaceKind.Audio
                or PreviewSurfaceKind.Video;

        public AssetItem? ActiveAsset
        {
            get => _activeAsset;
            set => SetProperty(ref _activeAsset, value);
        }

        public bool IsPreviewEnabled
        {
            get => _isPreviewEnabled;
            set
            {
                if (SetProperty(ref _isPreviewEnabled, value) && !value)
                {
                    lock (_stateLock)
                    {
                        ClearMonoBehaviourTreeCore();
                        ClearGameObjectInspectorCore();
                    }
                }
            }
        }

        public bool IsDisplayInfoEnabled
        {
            get => _isDisplayInfoEnabled;
            set
            {
                if (SetProperty(ref _isDisplayInfoEnabled, value))
                {
                    OnPropertyChanged(nameof(IsInfoOverlayVisible));
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string LoadingStatusText
        {
            get => _loadingStatusText;
            set => SetProperty(ref _loadingStatusText, value ?? string.Empty);
        }

        public string PreviewTitle
        {
            get => _previewTitle;
            set => SetProperty(ref _previewTitle, value ?? string.Empty);
        }

        public string PreviewSubtitle
        {
            get => _previewSubtitle;
            set => SetProperty(ref _previewSubtitle, value ?? string.Empty);
        }

        public string PreviewMetadataSummary
        {
            get => _previewMetadataSummary;
            set
            {
                if (SetProperty(ref _previewMetadataSummary, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(IsInfoOverlayVisible));
                }
            }
        }

        public bool IsInfoOverlayVisible =>
            _isDisplayInfoEnabled && !string.IsNullOrWhiteSpace(_previewMetadataSummary);

        public bool IsInfoExpanded
        {
            get => _isInfoExpanded;
            set
            {
                if (SetProperty(ref _isInfoExpanded, value))
                {
                    OnPropertyChanged(nameof(InfoToggleGlyph));
                }
            }
        }

        public string InfoToggleGlyph => _isInfoExpanded ? "▼" : "▲";

        public bool ToggleInfoExpanded()
        {
            IsInfoExpanded = !_isInfoExpanded;
            return _isInfoExpanded;
        }

        public bool HasDialoguePreview
        {
            get => _hasDialoguePreview;
            private set
            {
                if (SetProperty(ref _hasDialoguePreview, value))
                {
                    OnPropertyChanged(nameof(IsDialogueDetailsVisible));
                }
            }
        }

        public bool IsDialogueDetailsVisible => _hasDialoguePreview && !_isDialogueCardsVisible;

        public string TextContent
        {
            get => _textContent;
            set => SetProperty(ref _textContent, value ?? string.Empty);
        }

        public string DumpTextContent
        {
            get => _dumpTextContent;
            set => SetProperty(ref _dumpTextContent, value ?? string.Empty);
        }

        public bool IsDumpLoading
        {
            get => _isDumpLoading;
            set => SetProperty(ref _isDumpLoading, value);
        }

        public RightPanelTab ActiveRightTab
        {
            get => _activeRightTab;
            set
            {
                var prevPreview = IsPreviewTabActive;
                var prevDump = IsDumpTabActive;
                var prevReferences = IsReferencesTabActive;
                var prevScript = IsScriptTabActive;
                if (SetProperty(ref _activeRightTab, value))
                {
                    if (IsPreviewTabActive != prevPreview)
                    {
                        OnPropertyChanged(nameof(IsPreviewTabActive));
                        ShaderPreview.SetSuspended(!IsPreviewTabActive);
                    }

                    if (IsDumpTabActive != prevDump)
                    {
                        OnPropertyChanged(nameof(IsDumpTabActive));
                    }

                    if (IsReferencesTabActive != prevReferences)
                    {
                        OnPropertyChanged(nameof(IsReferencesTabActive));
                    }

                    if (IsScriptTabActive != prevScript)
                    {
                        OnPropertyChanged(nameof(IsScriptTabActive));
                    }
                }
            }
        }

        public bool IsPreviewTabActive => _activeRightTab == RightPanelTab.Preview;

        public bool IsDumpTabActive
        {
            get => _activeRightTab == RightPanelTab.Dump;
            set
            {
                if (value)
                {
                    ActiveRightTab = RightPanelTab.Dump;
                }
                else if (_activeRightTab == RightPanelTab.Dump)
                {
                    ActiveRightTab = RightPanelTab.Preview;
                }
            }
        }

        public bool IsReferencesTabActive
        {
            get => _activeRightTab == RightPanelTab.References;
            set
            {
                if (value)
                {
                    ActiveRightTab = RightPanelTab.References;
                }
                else if (_activeRightTab == RightPanelTab.References)
                {
                    ActiveRightTab = RightPanelTab.Preview;
                }
            }
        }

        public bool IsScriptTabActive
        {
            get => _activeRightTab == RightPanelTab.Script;
            set
            {
                if (value)
                {
                    ActiveRightTab = RightPanelTab.Script;
                }
                else if (_activeRightTab == RightPanelTab.Script)
                {
                    ActiveRightTab = RightPanelTab.Preview;
                }
            }
        }

        public bool IsReferencesLoading
        {
            get => _isReferencesLoading;
            set => SetProperty(ref _isReferencesLoading, value);
        }

        public string ReferencesFilterText
        {
            get => _referencesFilterText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _referencesFilterText, normalized))
                {
                    ApplyReferencesFilterCore();
                }
            }
        }

        public string ReferencesTargetTitle
        {
            get => _referencesTargetTitle;
            private set => SetProperty(ref _referencesTargetTitle, value ?? DefaultReferencesTargetTitle);
        }

        public string ReferencesHeaderSummary
        {
            get => _referencesHeaderSummary;
            private set => SetProperty(ref _referencesHeaderSummary, value ?? DefaultReferencesSummary);
        }

        public string ReferencedBySectionHeader
        {
            get => _referencedBySectionHeader;
            private set => SetProperty(ref _referencedBySectionHeader, value ?? DefaultReferencedByHeader);
        }

        public string ReferencesSectionHeader
        {
            get => _referencesSectionHeader;
            private set => SetProperty(ref _referencesSectionHeader, value ?? DefaultReferencesHeader);
        }

        public bool HasReferencedByItems
        {
            get => _hasReferencedByItems;
            private set => SetProperty(ref _hasReferencedByItems, value);
        }

        public bool HasReferencesItems
        {
            get => _hasReferencesItems;
            private set => SetProperty(ref _hasReferencesItems, value);
        }

        public bool HasNoReferences
        {
            get => _hasNoReferences;
            private set => SetProperty(ref _hasNoReferences, value);
        }

        public AssetReferenceItem? SelectedReferenceItem
        {
            get => _selectedReferenceItem;
            set => SetProperty(ref _selectedReferenceItem, value);
        }

        public int TotalReferencedByCount => _allReferencedBy.Count;

        public int TotalReferencesCount => _allReferences.Count;

        public bool IsDialogueCardsVisible
        {
            get => _isDialogueCardsVisible;
            set
            {
                if (SetProperty(ref _isDialogueCardsVisible, value))
                {
                    OnPropertyChanged(nameof(IsTextSurfaceVisible));
                    OnPropertyChanged(nameof(IsDialogueDetailsVisible));
                }
            }
        }

        public TextAssetDisplayMode DialogueDisplayMode
        {
            get => _dialogueDisplayMode;
            set
            {
                if (SetProperty(ref _dialogueDisplayMode, value))
                {
                    ApplyDialogueModeTextCore(value);
                }
            }
        }

        public PreviewChannelMask ChannelMask
        {
            get => _channelMask;
            set => ApplyChannelMaskCore(value & PreviewChannelMask.All);
        }

        public bool ShowRed
        {
            get => _showRed;
            set => SetSingleChannelCore(PreviewChannelMask.Red, value);
        }

        public bool ShowGreen
        {
            get => _showGreen;
            set => SetSingleChannelCore(PreviewChannelMask.Green, value);
        }

        public bool ShowBlue
        {
            get => _showBlue;
            set => SetSingleChannelCore(PreviewChannelMask.Blue, value);
        }

        public bool ShowAlpha
        {
            get => _showAlpha;
            set => SetSingleChannelCore(PreviewChannelMask.Alpha, value);
        }

        public int ActiveChannelCount =>
            (_showRed ? 1 : 0) + (_showGreen ? 1 : 0) + (_showBlue ? 1 : 0) + (_showAlpha ? 1 : 0);

        public string ChannelSummaryText => FormatChannelSummary(_showRed, _showGreen, _showBlue, _showAlpha);

        public PreviewCandidateItem? SelectedPreviewCandidate
        {
            get => _selectedPreviewCandidate;
            set => SetProperty(ref _selectedPreviewCandidate, value);
        }

        public bool HasCandidates
        {
            get => _hasCandidates;
            private set => SetProperty(ref _hasCandidates, value);
        }

        public string CandidateSelectorLabel
        {
            get => _candidateSelectorLabel;
            set => SetProperty(ref _candidateSelectorLabel, string.IsNullOrWhiteSpace(value) ? DefaultCandidateLabel : value);
        }

        public string CacheBadgeText
        {
            get => _cacheBadgeText;
            private set => SetProperty(ref _cacheBadgeText, value ?? string.Empty);
        }

        public bool IsCacheBadgeVisible
        {
            get => _isCacheBadgeVisible;
            private set => SetProperty(ref _isCacheBadgeVisible, value);
        }

        public bool LastPreviewWasFromCache
        {
            get => _lastPreviewWasFromCache;
            private set => SetProperty(ref _lastPreviewWasFromCache, value);
        }

        public string? LastPreviewSourceKind
        {
            get => _lastPreviewSourceKind;
            private set => SetProperty(ref _lastPreviewSourceKind, value);
        }

        public bool LastPreviewMaterialsLoading
        {
            get => _lastPreviewMaterialsLoading;
            private set => SetProperty(ref _lastPreviewMaterialsLoading, value);
        }

        // Phase 1: MonoBehaviour Searchable Tree / JSON Viewer properties

        public string MonoBehaviourFilterText
        {
            get => _monoBehaviourFilterText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _monoBehaviourFilterText, normalized))
                {
                    ApplyMonoBehaviourFilterCore();
                }
            }
        }

        public bool HasMonoBehaviourTree
        {
            get => _hasMonoBehaviourTree;
            private set
            {
                if (SetProperty(ref _hasMonoBehaviourTree, value))
                {
                    OnPropertyChanged(nameof(IsMonoBehaviourPanelVisible));
                    OnPropertyChanged(nameof(IsMonoBehaviourTreeViewVisible));
                    OnPropertyChanged(nameof(IsMonoBehaviourJsonViewVisible));
                    OnPropertyChanged(nameof(IsTextSurfaceVisible));
                }
            }
        }

        public bool IsMonoBehaviourJsonMode
        {
            get => _isMonoBehaviourJsonMode;
            set
            {
                if (SetProperty(ref _isMonoBehaviourJsonMode, value))
                {
                    OnPropertyChanged(nameof(IsMonoBehaviourTreeViewVisible));
                    OnPropertyChanged(nameof(IsMonoBehaviourJsonViewVisible));
                    OnPropertyChanged(nameof(MonoBehaviourModeToggleLabel));
                }
            }
        }

        public bool IsMonoBehaviourPanelVisible => _hasMonoBehaviourTree && !_isGameObjectInspectorVisible;

        public bool IsMonoBehaviourTreeViewVisible => _hasMonoBehaviourTree && !_isMonoBehaviourJsonMode && !_isGameObjectInspectorVisible;

        public bool IsMonoBehaviourJsonViewVisible => _hasMonoBehaviourTree && _isMonoBehaviourJsonMode && !_isGameObjectInspectorVisible;

        public string MonoBehaviourModeToggleLabel => _isMonoBehaviourJsonMode ? "Tree" : "JSON";

        public string MonoBehaviourScriptTitle
        {
            get => _monoBehaviourScriptTitle;
            private set => SetProperty(ref _monoBehaviourScriptTitle, value ?? string.Empty);
        }

        public string MonoBehaviourSummaryText
        {
            get => _monoBehaviourSummaryText;
            private set => SetProperty(ref _monoBehaviourSummaryText, value ?? string.Empty);
        }

        public int MonoBehaviourPPtrCount
        {
            get => _monoBehaviourPPtrCount;
            private set => SetProperty(ref _monoBehaviourPPtrCount, value);
        }

        public string MonoBehaviourJsonText
        {
            get => _monoBehaviourJsonText;
            private set => SetProperty(ref _monoBehaviourJsonText, value ?? string.Empty);
        }

        public string MonoBehaviourFullJsonText
        {
            get => _monoBehaviourFullJsonText;
            private set => SetProperty(ref _monoBehaviourFullJsonText, value ?? string.Empty);
        }

        // Phase 1: Scene Hierarchy GameObject Component Inspector properties

        public bool IsGameObjectInspectorVisible
        {
            get => _isGameObjectInspectorVisible;
            private set
            {
                if (SetProperty(ref _isGameObjectInspectorVisible, value))
                {
                    OnPropertyChanged(nameof(IsMonoBehaviourPanelVisible));
                    OnPropertyChanged(nameof(IsMonoBehaviourTreeViewVisible));
                    OnPropertyChanged(nameof(IsMonoBehaviourJsonViewVisible));
                    OnPropertyChanged(nameof(IsTextSurfaceVisible));
                }
            }
        }

        public bool IsGameObjectInspectorLoading
        {
            get => _isGameObjectInspectorLoading;
            set => SetProperty(ref _isGameObjectInspectorLoading, value);
        }

        public GameObjectInspectorSnapshot? InspectedGameObject
        {
            get => _inspectedGameObject;
            private set => SetProperty(ref _inspectedGameObject, value);
        }

        public string InspectorFilterText
        {
            get => _inspectorFilterText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _inspectorFilterText, normalized))
                {
                    ApplyInspectorFilterCore();
                }
            }
        }

        public string InspectorComponentsHeader
        {
            get => _inspectorComponentsHeader;
            private set => SetProperty(ref _inspectorComponentsHeader, value ?? DefaultInspectorComponentsHeader);
        }

        // Phase 1: Back / Forward Navigation History properties

        public bool CanNavigateBack
        {
            get => _canNavigateBack;
            private set => SetProperty(ref _canNavigateBack, value);
        }

        public bool CanNavigateForward
        {
            get => _canNavigateForward;
            private set => SetProperty(ref _canNavigateForward, value);
        }

        public string NavigateBackTooltip
        {
            get => _navigateBackTooltip;
            private set => SetProperty(ref _navigateBackTooltip, value ?? DefaultNavigateBackTooltip);
        }

        public string NavigateForwardTooltip
        {
            get => _navigateForwardTooltip;
            private set => SetProperty(ref _navigateForwardTooltip, value ?? DefaultNavigateForwardTooltip);
        }

        // Phase 2: Integrated Script Decompiler properties

        public bool IsScriptLoading
        {
            get => _isScriptLoading;
            private set => SetProperty(ref _isScriptLoading, value);
        }

        public bool HasScriptSource
        {
            get => _hasScriptSource;
            private set => SetProperty(ref _hasScriptSource, value);
        }

        public string ScriptTitle
        {
            get => _scriptTitle;
            private set => SetProperty(ref _scriptTitle, value ?? DefaultScriptTitle);
        }

        public string ScriptSummaryText
        {
            get => _scriptSummaryText;
            private set => SetProperty(ref _scriptSummaryText, value ?? DefaultScriptSummary);
        }

        public string ScriptBackendBadge
        {
            get => _scriptBackendBadge;
            private set
            {
                var normalized = value ?? string.Empty;
                var prevHas = HasScriptBackendBadge;
                if (SetProperty(ref _scriptBackendBadge, normalized) && HasScriptBackendBadge != prevHas)
                {
                    OnPropertyChanged(nameof(HasScriptBackendBadge));
                }
            }
        }

        public bool HasScriptBackendBadge => !string.IsNullOrWhiteSpace(_scriptBackendBadge);

        public string ScriptSourceCode
        {
            get => _scriptSourceCode;
            private set => SetProperty(ref _scriptSourceCode, value ?? string.Empty);
        }

        public string ScriptFullSourceCode
        {
            get => _scriptFullSourceCode;
            private set
            {
                var normalized = value ?? string.Empty;
                var prevCanSave = CanSaveOrCopyScript;
                if (SetProperty(ref _scriptFullSourceCode, normalized) && CanSaveOrCopyScript != prevCanSave)
                {
                    OnPropertyChanged(nameof(CanSaveOrCopyScript));
                }
            }
        }

        public bool CanSaveOrCopyScript => !string.IsNullOrWhiteSpace(_scriptFullSourceCode);

        public string ScriptSearchText
        {
            get => _scriptSearchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _scriptSearchText, normalized))
                {
                    ApplyScriptSearchCore();
                }
            }
        }

        public string ScriptSearchMatchSummary
        {
            get => _scriptSearchMatchSummary;
            private set
            {
                var normalized = value ?? string.Empty;
                var prevHas = HasScriptSearchMatchSummary;
                if (SetProperty(ref _scriptSearchMatchSummary, normalized) && HasScriptSearchMatchSummary != prevHas)
                {
                    OnPropertyChanged(nameof(HasScriptSearchMatchSummary));
                }
            }
        }

        public bool HasScriptSearchMatchSummary => !string.IsNullOrWhiteSpace(_scriptSearchMatchSummary);

        public bool IsScriptSearchFilterOnly
        {
            get => _isScriptSearchFilterOnly;
            set
            {
                if (SetProperty(ref _isScriptSearchFilterOnly, value))
                {
                    ApplyScriptSearchCore();
                }
            }
        }

        public bool IsScriptWordWrapEnabled
        {
            get => _isScriptWordWrapEnabled;
            set => SetProperty(ref _isScriptWordWrapEnabled, value);
        }

        public string ScriptDiagnosticText
        {
            get => _scriptDiagnosticText;
            private set
            {
                var normalized = value ?? string.Empty;
                var prevHas = HasScriptDiagnostic;
                if (SetProperty(ref _scriptDiagnosticText, normalized) && HasScriptDiagnostic != prevHas)
                {
                    OnPropertyChanged(nameof(HasScriptDiagnostic));
                }
            }
        }

        public bool HasScriptDiagnostic => !string.IsNullOrWhiteSpace(_scriptDiagnosticText);

        public string ScriptAssemblyName
        {
            get => _scriptAssemblyName;
            private set => SetProperty(ref _scriptAssemblyName, value ?? string.Empty);
        }

        public string ScriptNamespace
        {
            get => _scriptNamespace;
            private set => SetProperty(ref _scriptNamespace, value ?? string.Empty);
        }

        public string ScriptClassName
        {
            get => _scriptClassName;
            private set => SetProperty(ref _scriptClassName, value ?? string.Empty);
        }

        public IReadOnlyList<ScriptMethodSymbol> ScriptMethodSymbols => _scriptMethodSymbols;

        public static string TruncatePreviewText(string? text, string fallbackText = "")
        {
            if (text == null)
            {
                return fallbackText ?? string.Empty;
            }

            if (text.Length > MaxPreviewTextChars)
            {
                return text.Substring(0, MaxPreviewTextChars) +
                    $"{Environment.NewLine}...{Environment.NewLine}[Preview truncated: content is too large ({text.Length:N0} characters). Please export the asset to view full content]";
            }

            return text;
        }

        public static string FormatChannelSummary(bool showRed, bool showGreen, bool showBlue, bool showAlpha)
        {
            if (!showRed && !showGreen && !showBlue && !showAlpha)
            {
                return "None";
            }

            if (showRed && showGreen && showBlue && showAlpha)
            {
                return "R G B A";
            }

            var parts = new List<string>(4);
            if (showRed) parts.Add("R");
            if (showGreen) parts.Add("G");
            if (showBlue) parts.Add("B");
            if (showAlpha) parts.Add("A");
            return string.Join(" ", parts);
        }

        public long NextPreviewVersion()
        {
            return Interlocked.Increment(ref _previewVersion);
        }

        internal void SyncPreviewVersion(long externalVersion)
        {
            Interlocked.Exchange(ref _previewVersion, externalVersion);
        }

        public CancellationToken BeginPreviewRequest(out long requestId)
        {
            return BeginPreviewRequest(out requestId, out _, loadingStatus: null);
        }

        public CancellationToken BeginPreviewRequest(out long requestId, out long previewVersion, string? loadingStatus = null)
        {
            previewVersion = Interlocked.Read(ref _previewVersion);
            var token = _previewCoordinator.BeginNewRequest(out requestId);
            _dispatchToUi(() =>
            {
                IsLoading = true;
                if (loadingStatus != null)
                {
                    LoadingStatusText = loadingStatus;
                }
            });
            return token;
        }

        public bool IsPreviewRequestCurrent(long requestId, CancellationToken cancellationToken = default)
        {
            return !cancellationToken.IsCancellationRequested && _previewCoordinator.IsCurrent(requestId);
        }

        public bool IsPreviewRequestCurrent(long requestId, long previewVersion, CancellationToken cancellationToken = default)
        {
            return !cancellationToken.IsCancellationRequested
                && _previewCoordinator.IsCurrent(requestId)
                && Interlocked.Read(ref _previewVersion) == previewVersion;
        }

        public async Task<(bool Applied, long RequestId, T? Result)> RunLatestPreviewAsync<T>(
            Func<long, CancellationToken, Task<T>> backgroundWork,
            Action<long, T>? onCompleted = null)
        {
            ArgumentNullException.ThrowIfNull(backgroundWork);
            return await _previewCoordinator.RunLatestAsync(
                backgroundWork,
                (requestId, result) =>
                {
                    _dispatchToUi(() =>
                    {
                        if (_previewCoordinator.IsCurrent(requestId))
                        {
                            IsLoading = false;
                            onCompleted?.Invoke(requestId, result);
                        }
                    });
                }).ConfigureAwait(false);
        }

        public CancellationToken BeginDumpRequest(out long requestId, string? loadingText = "Loading dump...")
        {
            var token = _dumpCoordinator.BeginNewRequest(out requestId);
            _dispatchToUi(() =>
            {
                IsDumpLoading = true;
                if (loadingText != null)
                {
                    DumpTextContent = loadingText;
                }
            });
            return token;
        }

        public bool IsDumpRequestCurrent(long requestId, CancellationToken cancellationToken = default)
        {
            return !cancellationToken.IsCancellationRequested && _dumpCoordinator.IsCurrent(requestId);
        }

        public bool CompleteDumpRequest(long requestId, string? dumpText, string fallbackText = "No Dump Available", bool truncate = true)
        {
            if (!_dumpCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            var finalText = truncate
                ? TruncatePreviewText(dumpText, fallbackText)
                : (dumpText ?? fallbackText);

            _dispatchToUi(() =>
            {
                if (_dumpCoordinator.IsCurrent(requestId))
                {
                    DumpTextContent = finalText;
                    IsDumpLoading = false;
                }
            });
            return true;
        }

        public bool FailDumpRequest(long requestId, string errorMessage)
        {
            if (!_dumpCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            var finalText = TruncatePreviewText(errorMessage, "Dump error");
            _dispatchToUi(() =>
            {
                if (_dumpCoordinator.IsCurrent(requestId))
                {
                    DumpTextContent = finalText;
                    IsDumpLoading = false;
                }
            });
            return true;
        }

        public async Task<(bool Applied, long RequestId, T? Result)> RunLatestDumpAsync<T>(
            Func<long, CancellationToken, Task<T>> backgroundWork,
            Action<long, T>? onCompleted = null)
        {
            ArgumentNullException.ThrowIfNull(backgroundWork);
            return await _dumpCoordinator.RunLatestAsync(
                backgroundWork,
                (requestId, result) =>
                {
                    _dispatchToUi(() =>
                    {
                        if (_dumpCoordinator.IsCurrent(requestId))
                        {
                            IsDumpLoading = false;
                            onCompleted?.Invoke(requestId, result);
                        }
                    });
                }).ConfigureAwait(false);
        }

        public void CancelPendingPreview()
        {
            _previewCoordinator.CancelCurrent();
            _animationCoordinator.CancelCurrent();
            Interlocked.Increment(ref _previewVersion);
            _dispatchToUi(() =>
            {
                IsLoading = false;
            });
        }

        public void CancelPendingDump()
        {
            _dumpCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                IsDumpLoading = false;
            });
        }

        public CancellationToken BeginReferencesRequest(out long requestId, AssetItem? targetAsset = null)
        {
            var token = _referencesCoordinator.BeginNewRequest(out requestId);
            var title = targetAsset != null
                ? $"{targetAsset.DisplayType}: {targetAsset.Name}"
                : DefaultReferencesTargetTitle;
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    IsReferencesLoading = true;
                    ReferencesTargetTitle = title;
                    ReferencesHeaderSummary = "Scanning references...";
                    HasNoReferences = false;
                }
            });
            return token;
        }

        public bool IsReferencesRequestCurrent(long requestId, CancellationToken cancellationToken = default)
        {
            return !cancellationToken.IsCancellationRequested && _referencesCoordinator.IsCurrent(requestId);
        }

        public bool CompleteReferencesRequest(long requestId, AssetReferencesSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!_referencesCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            _dispatchToUi(() =>
            {
                if (!_referencesCoordinator.IsCurrent(requestId))
                {
                    return;
                }

                lock (_stateLock)
                {
                    _allReferencedBy = snapshot.ReferencedBy ?? Array.Empty<AssetReferenceItem>();
                    _allReferences = snapshot.References ?? Array.Empty<AssetReferenceItem>();
                    SelectedReferenceItem = null;
                    IsReferencesLoading = false;
                    ReferencesTargetTitle = !string.IsNullOrWhiteSpace(snapshot.TargetName)
                        ? $"{AssetItem.GetCachedTypeString(snapshot.TargetType)}: {snapshot.TargetName}"
                        : DefaultReferencesTargetTitle;
                    ApplyReferencesFilterCore();
                }
            });
            return true;
        }

        public bool FailReferencesRequest(long requestId, string errorMessage)
        {
            if (!_referencesCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            _dispatchToUi(() =>
            {
                if (!_referencesCoordinator.IsCurrent(requestId))
                {
                    return;
                }

                lock (_stateLock)
                {
                    _allReferencedBy = Array.Empty<AssetReferenceItem>();
                    _allReferences = Array.Empty<AssetReferenceItem>();
                    SelectedReferenceItem = null;
                    if (FilteredReferencedByItems.Count > 0) FilteredReferencedByItems.Clear();
                    if (FilteredReferencesItems.Count > 0) FilteredReferencesItems.Clear();
                    HasReferencedByItems = false;
                    HasReferencesItems = false;
                    HasNoReferences = false;
                    IsReferencesLoading = false;
                    ReferencedBySectionHeader = DefaultReferencedByHeader;
                    ReferencesSectionHeader = DefaultReferencesHeader;
                    ReferencesHeaderSummary = $"Error loading references: {errorMessage}";
                }
            });
            return true;
        }

        public void CancelPendingReferences()
        {
            _referencesCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                IsReferencesLoading = false;
            });
        }

        public void ClearReferences()
        {
            _referencesCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearReferencesCore();
                }
            });
        }

        private void ClearReferencesCore()
        {
            _allReferencedBy = Array.Empty<AssetReferenceItem>();
            _allReferences = Array.Empty<AssetReferenceItem>();
            SelectedReferenceItem = null;
            if (FilteredReferencedByItems.Count > 0)
            {
                FilteredReferencedByItems.Clear();
            }

            if (FilteredReferencesItems.Count > 0)
            {
                FilteredReferencesItems.Clear();
            }

            IsReferencesLoading = false;
            SetProperty(ref _referencesFilterText, string.Empty, nameof(ReferencesFilterText));
            ReferencesTargetTitle = DefaultReferencesTargetTitle;
            ReferencesHeaderSummary = DefaultReferencesSummary;
            ReferencedBySectionHeader = DefaultReferencedByHeader;
            ReferencesSectionHeader = DefaultReferencesHeader;
            HasReferencedByItems = false;
            HasReferencesItems = false;
            HasNoReferences = false;
        }

        private void ApplyReferencesFilterCore()
        {
            var filter = _referencesFilterText?.Trim();
            var hasFilter = !string.IsNullOrEmpty(filter);

            List<AssetReferenceItem> filteredUsedBy;
            List<AssetReferenceItem> filteredUses;
            if (!hasFilter)
            {
                filteredUsedBy = _allReferencedBy.Count > 0
                    ? new List<AssetReferenceItem>(_allReferencedBy)
                    : new List<AssetReferenceItem>();
                filteredUses = _allReferences.Count > 0
                    ? new List<AssetReferenceItem>(_allReferences)
                    : new List<AssetReferenceItem>();
            }
            else
            {
                filteredUsedBy = new List<AssetReferenceItem>();
                for (var i = 0; i < _allReferencedBy.Count; i++)
                {
                    if (_allReferencedBy[i].MatchesFilter(filter))
                    {
                        filteredUsedBy.Add(_allReferencedBy[i]);
                    }
                }

                filteredUses = new List<AssetReferenceItem>();
                for (var i = 0; i < _allReferences.Count; i++)
                {
                    if (_allReferences[i].MatchesFilter(filter))
                    {
                        filteredUses.Add(_allReferences[i]);
                    }
                }
            }

            FilteredReferencedByItems.ReplaceAll(filteredUsedBy);
            FilteredReferencesItems.ReplaceAll(filteredUses);
            HasReferencedByItems = filteredUsedBy.Count > 0;
            HasReferencesItems = filteredUses.Count > 0;
            HasNoReferences = !_isReferencesLoading && _allReferencedBy.Count == 0 && _allReferences.Count == 0;

            if (hasFilter)
            {
                ReferencedBySectionHeader = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Referenced By / Used By ({filteredUsedBy.Count:N0} of {_allReferencedBy.Count:N0})");
                ReferencesSectionHeader = string.Create(
                    CultureInfo.InvariantCulture,
                    $"References / Uses ({filteredUses.Count:N0} of {_allReferences.Count:N0})");
                ReferencesHeaderSummary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Showing {filteredUsedBy.Count:N0}/{_allReferencedBy.Count:N0} Used By • {filteredUses.Count:N0}/{_allReferences.Count:N0} Uses");
            }
            else
            {
                ReferencedBySectionHeader = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Referenced By / Used By ({_allReferencedBy.Count:N0})");
                ReferencesSectionHeader = string.Create(
                    CultureInfo.InvariantCulture,
                    $"References / Uses ({_allReferences.Count:N0})");
                ReferencesHeaderSummary = _allReferencedBy.Count == 0 && _allReferences.Count == 0
                    ? "No incoming or outgoing references found."
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"Used by {_allReferencedBy.Count:N0} asset(s) • Uses {_allReferences.Count:N0} asset(s)");
            }
        }

        public void CancelPending()
        {
            _previewCoordinator.CancelCurrent();
            _dumpCoordinator.CancelCurrent();
            _referencesCoordinator.CancelCurrent();
            _inspectorCoordinator.CancelCurrent();
            _scriptCoordinator.CancelCurrent();
            _animationCoordinator.CancelCurrent();
            Interlocked.Increment(ref _previewVersion);
            _dispatchToUi(() =>
            {
                IsLoading = false;
                IsDumpLoading = false;
                IsReferencesLoading = false;
                IsGameObjectInspectorLoading = false;
                IsScriptLoading = false;
            });
        }

        // Phase 2: Integrated Script Decompiler operations

        public CancellationToken BeginScriptRequest(out long requestId, string? title = null)
        {
            var token = _scriptCoordinator.BeginNewRequest(out requestId);
            var effectiveTitle = !string.IsNullOrWhiteSpace(title) ? title! : "Loading script...";
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    IsScriptLoading = true;
                    ScriptTitle = effectiveTitle;
                    ScriptSummaryText = "Decompiling / resolving C# source...";
                    ScriptDiagnosticText = string.Empty;
                }
            });
            return token;
        }

        public bool IsScriptRequestCurrent(long requestId, CancellationToken cancellationToken = default)
        {
            return !cancellationToken.IsCancellationRequested && _scriptCoordinator.IsCurrent(requestId);
        }

        public bool CompleteScriptRequest(long requestId, ScriptDecompilationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (!_scriptCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            _dispatchToUi(() =>
            {
                if (!_scriptCoordinator.IsCurrent(requestId))
                {
                    return;
                }

                lock (_stateLock)
                {
                    ApplyScriptResultCore(result);
                }
            });
            return true;
        }

        public bool FailScriptRequest(long requestId, string errorMessage)
        {
            if (!_scriptCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            _dispatchToUi(() =>
            {
                if (!_scriptCoordinator.IsCurrent(requestId))
                {
                    return;
                }

                lock (_stateLock)
                {
                    IsScriptLoading = false;
                    HasScriptSource = false;
                    ScriptBackendBadge = "Error";
                    ScriptSummaryText = "Failed to load script source.";
                    ScriptDiagnosticText = errorMessage ?? "Unknown script decompilation error.";
                    ScriptFullSourceCode = string.Empty;
                    ScriptSourceCode = string.Empty;
                    ScriptSearchMatchSummary = string.Empty;
                    _scriptMethodSymbols = Array.Empty<ScriptMethodSymbol>();
                }
            });
            return true;
        }

        public void ShowScriptResult(ScriptDecompilationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            _scriptCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ApplyScriptResultCore(result);
                }
            });
        }

        public void CancelPendingScript()
        {
            _scriptCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                IsScriptLoading = false;
            });
        }

        public void ClearScript()
        {
            _scriptCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearScriptCore();
                }
            });
        }

        private void ApplyScriptResultCore(ScriptDecompilationResult result)
        {
            IsScriptLoading = false;
            HasScriptSource = result.Success;
            ScriptTitle = !string.IsNullOrWhiteSpace(result.Title) ? result.Title : DefaultScriptTitle;
            ScriptSummaryText = !string.IsNullOrWhiteSpace(result.SummaryText) ? result.SummaryText : DefaultScriptSummary;
            ScriptBackendBadge = result.BackendBadge ?? string.Empty;
            ScriptDiagnosticText = result.DiagnosticMessage ?? string.Empty;
            ScriptAssemblyName = result.AssemblyName ?? string.Empty;
            ScriptNamespace = result.Namespace ?? string.Empty;
            ScriptClassName = result.ClassName ?? string.Empty;
            _scriptMethodSymbols = result.MethodSymbols ?? Array.Empty<ScriptMethodSymbol>();
            ScriptFullSourceCode = result.SourceCode ?? string.Empty;
            ApplyScriptSearchCore();
        }

        private void ClearScriptCore()
        {
            IsScriptLoading = false;
            HasScriptSource = false;
            ScriptTitle = DefaultScriptTitle;
            ScriptSummaryText = DefaultScriptSummary;
            ScriptBackendBadge = string.Empty;
            ScriptDiagnosticText = string.Empty;
            ScriptAssemblyName = string.Empty;
            ScriptNamespace = string.Empty;
            ScriptClassName = string.Empty;
            _scriptMethodSymbols = Array.Empty<ScriptMethodSymbol>();
            SetProperty(ref _scriptSearchText, string.Empty, nameof(ScriptSearchText));
            SetProperty(ref _isScriptSearchFilterOnly, false, nameof(IsScriptSearchFilterOnly));
            SetProperty(ref _isScriptWordWrapEnabled, true, nameof(IsScriptWordWrapEnabled));
            ScriptSearchMatchSummary = string.Empty;
            ScriptFullSourceCode = string.Empty;
            ScriptSourceCode = string.Empty;
        }

        private void ApplyScriptSearchCore()
        {
            if (string.IsNullOrEmpty(_scriptFullSourceCode))
            {
                ScriptSearchMatchSummary = string.Empty;
                ScriptSourceCode = string.Empty;
                return;
            }

            if (string.IsNullOrWhiteSpace(_scriptSearchText))
            {
                ScriptSearchMatchSummary = string.Empty;
                ScriptSourceCode = TruncatePreviewText(_scriptFullSourceCode);
                return;
            }

            var searchResult = ScriptCodeSearchHelper.ApplySearch(_scriptFullSourceCode, _scriptSearchText, _isScriptSearchFilterOnly);
            ScriptSearchMatchSummary = searchResult.MatchSummary;
            ScriptSourceCode = TruncatePreviewText(searchResult.DisplayText);
        }

        // Phase 1: MonoBehaviour Tree / JSON Viewer operations

        public void ShowMonoBehaviourTree(
            AssetItem? asset,
            MonoBehaviourTreeBuildResult treeResult,
            string? fullJsonText,
            string? displayJsonText = null)
        {
            ArgumentNullException.ThrowIfNull(treeResult);
            var fullJson = fullJsonText ?? string.Empty;
            var displayJson = displayJsonText ?? TruncatePreviewText(fullJson);

            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearGameObjectInspectorCore();
                    ActiveKind = PreviewSurfaceKind.MonoBehaviour;
                    if (asset != null)
                    {
                        ActiveAsset = asset;
                    }

                    var title = !string.IsNullOrWhiteSpace(treeResult.ScriptTitle)
                        ? treeResult.ScriptTitle
                        : (asset?.Name ?? "MonoBehaviour");
                    PreviewTitle = title;
                    MonoBehaviourScriptTitle = title;
                    MonoBehaviourSummaryText = treeResult.SummaryText ?? string.Empty;
                    MonoBehaviourPPtrCount = treeResult.PPtrCount;
                    MonoBehaviourFullJsonText = fullJson;
                    MonoBehaviourJsonText = displayJson;
                    HasDialoguePreview = false;
                    IsDialogueCardsVisible = false;
                    TextContent = displayJson;

                    _allMonoBehaviourTreeRoots = treeResult.Roots ?? Array.Empty<AssetPropertyNode>();
                    SetProperty(ref _monoBehaviourFilterText, string.Empty, nameof(MonoBehaviourFilterText));
                    IsMonoBehaviourJsonMode = false;
                    HasMonoBehaviourTree = true;
                    MonoBehaviourTreeRoots.ReplaceAll(_allMonoBehaviourTreeRoots);
                    IsLoading = false;
                }
            });
        }

        public void SetMonoBehaviourViewMode(bool jsonMode)
        {
            _dispatchToUi(() =>
            {
                IsMonoBehaviourJsonMode = jsonMode;
            });
        }

        public bool ToggleMonoBehaviourViewMode()
        {
            var next = !_isMonoBehaviourJsonMode;
            SetMonoBehaviourViewMode(next);
            return next;
        }

        public void ExpandAllMonoBehaviourNodes()
        {
            _dispatchToUi(() =>
            {
                MonoBehaviourTreeBuilder.SetExpandedRecursive(MonoBehaviourTreeRoots, isExpanded: true);
            });
        }

        public void CollapseAllMonoBehaviourNodes()
        {
            _dispatchToUi(() =>
            {
                MonoBehaviourTreeBuilder.SetExpandedRecursive(MonoBehaviourTreeRoots, isExpanded: false);
            });
        }

        public void ClearMonoBehaviourTree()
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearMonoBehaviourTreeCore();
                }
            });
        }

        private void ClearMonoBehaviourTreeCore()
        {
            _allMonoBehaviourTreeRoots = Array.Empty<AssetPropertyNode>();
            if (MonoBehaviourTreeRoots.Count > 0)
            {
                MonoBehaviourTreeRoots.Clear();
            }

            SetProperty(ref _monoBehaviourFilterText, string.Empty, nameof(MonoBehaviourFilterText));
            HasMonoBehaviourTree = false;
            IsMonoBehaviourJsonMode = false;
            MonoBehaviourScriptTitle = string.Empty;
            MonoBehaviourSummaryText = string.Empty;
            MonoBehaviourPPtrCount = 0;
            MonoBehaviourJsonText = string.Empty;
            MonoBehaviourFullJsonText = string.Empty;
        }

        private void ApplyMonoBehaviourFilterCore()
        {
            if (_allMonoBehaviourTreeRoots.Count == 0)
            {
                if (MonoBehaviourTreeRoots.Count > 0)
                {
                    MonoBehaviourTreeRoots.Clear();
                }
                return;
            }

            var filtered = MonoBehaviourTreeBuilder.FilterTree(_allMonoBehaviourTreeRoots, _monoBehaviourFilterText);
            MonoBehaviourTreeRoots.ReplaceAll(filtered);
        }

        // Phase 1: Scene Hierarchy GameObject Component Inspector operations

        public CancellationToken BeginGameObjectInspectorRequest(out long requestId)
        {
            var token = _inspectorCoordinator.BeginNewRequest(out requestId);
            _dispatchToUi(() =>
            {
                IsGameObjectInspectorLoading = true;
            });
            return token;
        }

        public bool IsGameObjectInspectorRequestCurrent(long requestId, CancellationToken cancellationToken = default)
        {
            return !cancellationToken.IsCancellationRequested && _inspectorCoordinator.IsCurrent(requestId);
        }

        public bool CompleteGameObjectInspectorRequest(long requestId, GameObjectInspectorSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!_inspectorCoordinator.IsCurrent(requestId))
            {
                return false;
            }

            _dispatchToUi(() =>
            {
                if (!_inspectorCoordinator.IsCurrent(requestId))
                {
                    return;
                }

                lock (_stateLock)
                {
                    ApplyGameObjectInspectorCore(snapshot);
                }
            });
            return true;
        }

        public void ShowGameObjectInspector(GameObjectInspectorSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _inspectorCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ApplyGameObjectInspectorCore(snapshot);
                }
            });
        }

        public void ClearGameObjectInspector()
        {
            _inspectorCoordinator.CancelCurrent();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearGameObjectInspectorCore();
                }
            });
        }

        private void ApplyGameObjectInspectorCore(GameObjectInspectorSnapshot snapshot)
        {
            IsGameObjectInspectorLoading = false;
            InspectedGameObject = snapshot;
            _allInspectorComponents = snapshot.Components ?? Array.Empty<GameObjectComponentInfo>();
            SetProperty(ref _inspectorFilterText, string.Empty, nameof(InspectorFilterText));
            ApplyInspectorFilterCore();
            IsGameObjectInspectorVisible = true;
        }

        private void ClearGameObjectInspectorCore()
        {
            IsGameObjectInspectorLoading = false;
            IsGameObjectInspectorVisible = false;
            InspectedGameObject = null;
            _allInspectorComponents = Array.Empty<GameObjectComponentInfo>();
            if (InspectorComponents.Count > 0)
            {
                InspectorComponents.Clear();
            }

            SetProperty(ref _inspectorFilterText, string.Empty, nameof(InspectorFilterText));
            InspectorComponentsHeader = DefaultInspectorComponentsHeader;
        }

        private void ApplyInspectorFilterCore()
        {
            var filter = _inspectorFilterText?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                InspectorComponents.ReplaceAll(_allInspectorComponents);
                InspectorComponentsHeader = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Components ({_allInspectorComponents.Count:N0})");
                return;
            }

            var filtered = new List<GameObjectComponentInfo>();
            for (var i = 0; i < _allInspectorComponents.Count; i++)
            {
                if (_allInspectorComponents[i].MatchesFilter(filter))
                {
                    filtered.Add(_allInspectorComponents[i]);
                }
            }

            InspectorComponents.ReplaceAll(filtered);
            InspectorComponentsHeader = string.Create(
                CultureInfo.InvariantCulture,
                $"Components ({filtered.Count:N0} of {_allInspectorComponents.Count:N0})");
        }

        // Phase 1: Back / Forward Navigation History operations

        public bool RecordNavigation(AssetNavigationEntry? entry)
        {
            bool pushed;
            lock (_stateLock)
            {
                pushed = _navigationHistory.Push(entry);
            }

            if (pushed)
            {
                SyncNavigationHistoryState();
            }

            return pushed;
        }

        public bool TryNavigateBack(out AssetNavigationEntry? entry)
        {
            bool moved;
            lock (_stateLock)
            {
                moved = _navigationHistory.TryGoBack(out entry);
            }

            if (moved)
            {
                SyncNavigationHistoryState();
            }

            return moved;
        }

        public bool TryNavigateForward(out AssetNavigationEntry? entry)
        {
            bool moved;
            lock (_stateLock)
            {
                moved = _navigationHistory.TryGoForward(out entry);
            }

            if (moved)
            {
                SyncNavigationHistoryState();
            }

            return moved;
        }

        public void ClearNavigationHistory()
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearNavigationHistoryCore();
                }
            });
        }

        private void ClearNavigationHistoryCore()
        {
            if (_navigationHistory.Count > 0)
            {
                _navigationHistory.Clear();
            }

            SyncNavigationHistoryStateCore();
        }

        private void SyncNavigationHistoryState()
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    SyncNavigationHistoryStateCore();
                }
            });
        }

        private void SyncNavigationHistoryStateCore()
        {
            CanNavigateBack = _navigationHistory.CanGoBack;
            CanNavigateForward = _navigationHistory.CanGoForward;

            var prev = _navigationHistory.PreviousEntry;
            NavigateBackTooltip = prev != null && !string.IsNullOrWhiteSpace(prev.Name)
                ? $"Back to {prev.Name} (Alt+Left)"
                : DefaultNavigateBackTooltip;

            var next = _navigationHistory.NextEntry;
            NavigateForwardTooltip = next != null && !string.IsNullOrWhiteSpace(next.Name)
                ? $"Forward to {next.Name} (Alt+Right)"
                : DefaultNavigateForwardTooltip;
        }

        public void ActivateSurface(
            PreviewSurfaceKind kind,
            AssetItem? asset = null,
            string? title = null,
            string? metadataSummary = null,
            bool isLoading = false,
            string? loadingStatusText = null)
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearGameObjectInspectorCore();
                    if (kind != PreviewSurfaceKind.MonoBehaviour)
                    {
                        ClearMonoBehaviourTreeCore();
                    }
                }

                ActiveKind = kind;
                ActiveAsset = asset;
                if (title != null)
                {
                    PreviewTitle = title;
                }
                else if (asset != null)
                {
                    PreviewTitle = $"{asset.DisplayType}: {asset.Name}";
                }
                else if (kind == PreviewSurfaceKind.None)
                {
                    PreviewTitle = DefaultPlaceholderTitle;
                }

                if (metadataSummary != null)
                {
                    PreviewMetadataSummary = metadataSummary;
                }

                if (kind != PreviewSurfaceKind.Text)
                {
                    HasDialoguePreview = false;
                    IsDialogueCardsVisible = false;
                }

                IsLoading = isLoading;
                if (loadingStatusText != null)
                {
                    LoadingStatusText = loadingStatusText;
                }
            });
        }

        public void ShowTextPreview(
            string? text,
            PreviewSurfaceKind kind = PreviewSurfaceKind.Text,
            AssetItem? asset = null,
            string? title = null,
            string fallbackText = "",
            bool truncate = true)
        {
            var finalText = truncate
                ? TruncatePreviewText(text, fallbackText)
                : (text ?? fallbackText);

            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearGameObjectInspectorCore();
                    ClearMonoBehaviourTreeCore();
                }

                ActiveKind = kind;
                if (asset != null)
                {
                    ActiveAsset = asset;
                }

                if (title != null)
                {
                    PreviewTitle = title;
                }
                else if (asset != null)
                {
                    PreviewTitle = $"{asset.DisplayType}: {asset.Name}";
                }

                HasDialoguePreview = false;
                IsDialogueCardsVisible = false;
                TextContent = finalText;
                IsLoading = false;
            });
        }

        public void ShowDialogueCards(
            AssetItem? asset,
            IReadOnlyList<TextAssetDialogueCardViewModel>? cards,
            string? formatName = null,
            int parsedStringCount = 0,
            string? plainTextScript = null,
            string? detailsText = null)
        {
            var cardList = cards ?? Array.Empty<TextAssetDialogueCardViewModel>();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearGameObjectInspectorCore();
                    ClearMonoBehaviourTreeCore();
                }

                ActiveKind = PreviewSurfaceKind.Text;
                ActiveAsset = asset;
                PreviewTitle = asset?.Name ?? "TextAsset";
                PreviewSubtitle = !string.IsNullOrWhiteSpace(formatName)
                    ? $"{formatName} | {cardList.Count:N0} dialogue-like strings | {parsedStringCount:N0} parsed strings"
                    : $"{cardList.Count:N0} dialogue-like strings";
                _dialoguePlainTextScript = plainTextScript ?? string.Empty;
                _dialogueDetailsText = detailsText ?? string.Empty;

                DialogueCards.ReplaceAll(cardList);
                _dialogueDisplayMode = TextAssetDisplayMode.Cards;
                OnPropertyChanged(nameof(DialogueDisplayMode));
                HasDialoguePreview = true;
                IsDialogueCardsVisible = true;
                TextContent = string.Empty;
                IsLoading = false;
            });
        }

        internal void ShowDialogueCards(
            AssetItem? asset,
            TextAssetPreviewResult preview,
            IReadOnlyList<TextAssetDialogueCardViewModel>? prebuiltCards = null,
            bool isDarkTheme = true)
        {
            ArgumentNullException.ThrowIfNull(preview);
            var cards = prebuiltCards ?? MainWindow.BuildTextAssetDialogueCardViewModels(preview.DialogueCards, isDarkTheme);
            ShowDialogueCards(
                asset,
                cards,
                preview.FormatName,
                preview.ParsedStringCount,
                preview.PlainTextScript,
                preview.DetailsText);
        }

        public void SetDialogueDisplayMode(TextAssetDisplayMode mode, string? plainTextScript = null, string? detailsText = null)
        {
            _dispatchToUi(() =>
            {
                if (plainTextScript != null)
                {
                    _dialoguePlainTextScript = plainTextScript;
                }

                if (detailsText != null)
                {
                    _dialogueDetailsText = detailsText;
                }

                DialogueDisplayMode = mode;
                ApplyDialogueModeTextCore(mode);
            });
        }

        private void ApplyDialogueModeTextCore(TextAssetDisplayMode mode)
        {
            switch (mode)
            {
                case TextAssetDisplayMode.Cards:
                    if (DialogueCards.Count > 0)
                    {
                        IsDialogueCardsVisible = true;
                    }
                    break;
                case TextAssetDisplayMode.Text:
                    IsDialogueCardsVisible = false;
                    TextContent = TruncatePreviewText(_dialoguePlainTextScript);
                    break;
                case TextAssetDisplayMode.Info:
                    IsDialogueCardsVisible = false;
                    TextContent = TruncatePreviewText(_dialogueDetailsText);
                    break;
            }
        }

        public void ClearDialogueCards()
        {
            _dispatchToUi(() =>
            {
                _dialoguePlainTextScript = string.Empty;
                _dialogueDetailsText = string.Empty;
                HasDialoguePreview = false;
                IsDialogueCardsVisible = false;
                DialogueDisplayMode = TextAssetDisplayMode.Cards;
                if (DialogueCards.Count > 0)
                {
                    DialogueCards.Clear();
                }
            });
        }

        public void ToggleChannel(PreviewChannelMask channel)
        {
            var single = channel & PreviewChannelMask.All;
            if (single == PreviewChannelMask.None)
            {
                return;
            }

            ApplyChannelMaskCore(_channelMask ^ single);
        }

        public bool ToggleRed()
        {
            ShowRed = !ShowRed;
            return ShowRed;
        }

        public bool ToggleGreen()
        {
            ShowGreen = !ShowGreen;
            return ShowGreen;
        }

        public bool ToggleBlue()
        {
            ShowBlue = !ShowBlue;
            return ShowBlue;
        }

        public bool ToggleAlpha()
        {
            ShowAlpha = !ShowAlpha;
            return ShowAlpha;
        }

        public void SetChannels(bool red, bool green, bool blue, bool alpha)
        {
            var mask = PreviewChannelMask.None;
            if (red) mask |= PreviewChannelMask.Red;
            if (green) mask |= PreviewChannelMask.Green;
            if (blue) mask |= PreviewChannelMask.Blue;
            if (alpha) mask |= PreviewChannelMask.Alpha;
            ApplyChannelMaskCore(mask);
        }

        public void SetChannels(ReadOnlySpan<bool> channels)
        {
            var r = channels.Length <= 0 || channels[0];
            var g = channels.Length <= 1 || channels[1];
            var b = channels.Length <= 2 || channels[2];
            var a = channels.Length <= 3 || channels[3];
            SetChannels(r, g, b, a);
        }

        public bool[] GetChannelsArray()
        {
            return new[] { _showRed, _showGreen, _showBlue, _showAlpha };
        }

        public void CopyChannelsTo(Span<bool> destination)
        {
            if (destination.Length > 0) destination[0] = _showRed;
            if (destination.Length > 1) destination[1] = _showGreen;
            if (destination.Length > 2) destination[2] = _showBlue;
            if (destination.Length > 3) destination[3] = _showAlpha;
        }

        public void ResetChannels()
        {
            ApplyChannelMaskCore(PreviewChannelMask.All);
        }

        private void SetSingleChannelCore(PreviewChannelMask channelBit, bool enabled)
        {
            var targetMask = enabled
                ? (_channelMask | channelBit)
                : (_channelMask & ~channelBit);
            ApplyChannelMaskCore(targetMask);
        }

        private void ApplyChannelMaskCore(PreviewChannelMask mask)
        {
            var normalized = mask & PreviewChannelMask.All;
            var newRed = (normalized & PreviewChannelMask.Red) != 0;
            var newGreen = (normalized & PreviewChannelMask.Green) != 0;
            var newBlue = (normalized & PreviewChannelMask.Blue) != 0;
            var newAlpha = (normalized & PreviewChannelMask.Alpha) != 0;

            var prevCount = ActiveChannelCount;
            var prevSummary = ChannelSummaryText;

            var maskChanged = SetProperty(ref _channelMask, normalized, nameof(ChannelMask));
            var rChanged = SetProperty(ref _showRed, newRed, nameof(ShowRed));
            var gChanged = SetProperty(ref _showGreen, newGreen, nameof(ShowGreen));
            var bChanged = SetProperty(ref _showBlue, newBlue, nameof(ShowBlue));
            var aChanged = SetProperty(ref _showAlpha, newAlpha, nameof(ShowAlpha));

            if (maskChanged || rChanged || gChanged || bChanged || aChanged)
            {
                if (ActiveChannelCount != prevCount)
                {
                    OnPropertyChanged(nameof(ActiveChannelCount));
                }

                if (!string.Equals(ChannelSummaryText, prevSummary, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(ChannelSummaryText));
                }
            }
        }

        public void SetCandidates(
            IReadOnlyList<PreviewCandidateItem>? candidates,
            PreviewCandidateItem? selectedCandidate = null,
            string label = DefaultCandidateLabel)
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    var items = candidates ?? Array.Empty<PreviewCandidateItem>();
                    if (items.Count == 0)
                    {
                        ClearCandidatesCore();
                        return;
                    }

                    CandidateSelectorLabel = string.IsNullOrWhiteSpace(label) ? DefaultCandidateLabel : label;
                    PreviewCandidates.ReplaceAll(items);
                    HasCandidates = PreviewCandidates.Count > 0;

                    PreviewCandidateItem? effectiveSelected = null;
                    if (selectedCandidate != null)
                    {
                        for (var i = 0; i < PreviewCandidates.Count; i++)
                        {
                            if (ReferenceEquals(PreviewCandidates[i], selectedCandidate))
                            {
                                effectiveSelected = PreviewCandidates[i];
                                break;
                            }
                        }
                    }

                    effectiveSelected ??= PreviewCandidates.Count > 0 ? PreviewCandidates[0] : null;
                    SelectedPreviewCandidate = effectiveSelected;
                }
            });
        }

        public void AppendCandidates(IReadOnlyList<PreviewCandidateItem>? candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return;
            }

            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    PreviewCandidates.AddRange(candidates);
                    HasCandidates = PreviewCandidates.Count > 0;
                    if (_selectedPreviewCandidate == null && PreviewCandidates.Count > 0)
                    {
                        SelectedPreviewCandidate = PreviewCandidates[0];
                    }
                }
            });
        }

        public bool SelectCandidateAt(int index)
        {
            lock (_stateLock)
            {
                if ((uint)index >= (uint)PreviewCandidates.Count)
                {
                    return false;
                }

                SelectedPreviewCandidate = PreviewCandidates[index];
                return true;
            }
        }

        public void ClearCandidates()
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ClearCandidatesCore();
                }
            });
        }

        private void ClearCandidatesCore()
        {
            SelectedPreviewCandidate = null;
            if (PreviewCandidates.Count > 0)
            {
                PreviewCandidates.Clear();
            }

            HasCandidates = false;
            CandidateSelectorLabel = DefaultCandidateLabel;
        }

        public void UpdateCacheBadge(bool fromCache, string? sourceKind = null, bool materialsLoading = false)
        {
            var badgeText = MainWindow.FormatPreviewCacheBadge(fromCache, sourceKind, materialsLoading);
            var isVisible = !string.IsNullOrWhiteSpace(badgeText);

            _dispatchToUi(() =>
            {
                LastPreviewWasFromCache = fromCache;
                LastPreviewSourceKind = sourceKind;
                LastPreviewMaterialsLoading = materialsLoading;
                CacheBadgeText = badgeText;
                IsCacheBadgeVisible = isVisible;
            });
        }

        public void ClearCacheBadge()
        {
            UpdateCacheBadge(fromCache: false, sourceKind: null, materialsLoading: false);
        }

        public void ClearPreview(string placeholderTitle = DefaultPlaceholderTitle)
        {
            CancelPendingPreview();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ActiveKind = PreviewSurfaceKind.None;
                    ActiveAsset = null;
                    IsLoading = false;
                    LoadingStatusText = string.Empty;
                    PreviewTitle = placeholderTitle ?? DefaultPlaceholderTitle;
                    PreviewSubtitle = string.Empty;
                    PreviewMetadataSummary = string.Empty;
                    TextContent = string.Empty;
                    _dialoguePlainTextScript = string.Empty;
                    _dialogueDetailsText = string.Empty;
                    HasDialoguePreview = false;
                    IsDialogueCardsVisible = false;
                    DialogueDisplayMode = TextAssetDisplayMode.Cards;
                    if (DialogueCards.Count > 0)
                    {
                        DialogueCards.Clear();
                    }

                    ApplyChannelMaskCore(PreviewChannelMask.All);
                    ClearCandidatesCore();
                    ClearMonoBehaviourTreeCore();
                    ClearGameObjectInspectorCore();
                    LastPreviewWasFromCache = false;
                    LastPreviewSourceKind = null;
                    LastPreviewMaterialsLoading = false;
                    CacheBadgeText = string.Empty;
                    IsCacheBadgeVisible = false;
                }
            });
        }

        public void Reset()
        {
            CancelPending();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    ActiveKind = PreviewSurfaceKind.None;
                    ActiveAsset = null;
                    IsPreviewEnabled = true;
                    IsDisplayInfoEnabled = true;
                    IsLoading = false;
                    LoadingStatusText = string.Empty;
                    PreviewTitle = DefaultPlaceholderTitle;
                    PreviewSubtitle = string.Empty;
                    PreviewMetadataSummary = string.Empty;
                    TextContent = string.Empty;
                    DumpTextContent = string.Empty;
                    IsDumpLoading = false;
                    ActiveRightTab = RightPanelTab.Preview;
                    ClearReferencesCore();
                    ClearMonoBehaviourTreeCore();
                    ClearGameObjectInspectorCore();
                    ClearNavigationHistoryCore();
                    ClearScriptCore();
                    _dialoguePlainTextScript = string.Empty;
                    _dialogueDetailsText = string.Empty;
                    HasDialoguePreview = false;
                    IsDialogueCardsVisible = false;
                    DialogueDisplayMode = TextAssetDisplayMode.Cards;
                    if (DialogueCards.Count > 0)
                    {
                        DialogueCards.Clear();
                    }

                    ApplyChannelMaskCore(PreviewChannelMask.All);
                    ClearCandidatesCore();
                    LastPreviewWasFromCache = false;
                    LastPreviewSourceKind = null;
                    LastPreviewMaterialsLoading = false;
                    CacheBadgeText = string.Empty;
                    IsCacheBadgeVisible = false;
                }
            });
        }

        public void Dispose()
        {
            ShaderPreview.Dispose();
            _previewCoordinator.Dispose();
            _dumpCoordinator.Dispose();
            _referencesCoordinator.Dispose();
            _inspectorCoordinator.Dispose();
            _scriptCoordinator.Dispose();
            _animationCoordinator.Dispose();
        }
    }
}
