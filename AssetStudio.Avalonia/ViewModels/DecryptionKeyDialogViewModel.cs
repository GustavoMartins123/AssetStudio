using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum DecryptionDialogAction
    {
        Close = 0,
        ContinueWithoutKey = 1,
        ApplyAndReanalyze = 2
    }

    public sealed record DecryptionKeyDialogResult(
        DecryptionDialogAction Action,
        bool ProfilesModified);

    public sealed record DecryptionDialogContext(
        bool IsInitialLoadPrompt,
        string? ProjectRoot,
        int ReadableAssetFileCount,
        int EncryptedFileCount,
        double CoveragePercent,
        IReadOnlyList<StreamEncryptionBarrier> DetectedBarriers,
        IReadOnlyList<DecryptionHintEntry>? CatalogHints = null);

    public sealed class DecryptionBarrierSampleItem
    {
        public DecryptionBarrierSampleItem(StreamEncryptionBarrier barrier)
        {
            FileName = Path.GetFileName(barrier.FilePath);
            ProviderId = barrier.ProviderId;
            HeaderPrefixHex = barrier.HeaderPrefixHex;
            Reason = barrier.Reason;
        }

        public string FileName { get; }
        public string ProviderId { get; }
        public string HeaderPrefixHex { get; }
        public string Reason { get; }
        public string DisplayLine => string.IsNullOrWhiteSpace(HeaderPrefixHex)
            ? $"{FileName} ({ProviderId})"
            : $"{FileName} ({ProviderId}) — {HeaderPrefixHex}";
    }

    public sealed class DetectedAlgorithmCandidateItem : ViewModelBase
    {
        private bool _isSelected;
        private bool _isChecked = true;

        public DetectedAlgorithmCandidateItem(
            string schemeId,
            int algorithmIndex,
            string displayName,
            double confidence,
            int matchedFileCount,
            string filePattern,
            string evidenceSummary,
            long suggestedSkipBytes,
            long suggestedRangeStart,
            long suggestedRangeLength,
            int suggestedPaddingIndex,
            int suggestedIvModeIndex,
            bool suggestedIvFromPrefix,
            string? suggestedCatalogPath,
            Action<DetectedAlgorithmCandidateItem> onSelect)
        {
            SchemeId = schemeId;
            AlgorithmIndex = algorithmIndex;
            DisplayName = displayName;
            Confidence = confidence;
            MatchedFileCount = matchedFileCount;
            FilePattern = filePattern;
            EvidenceSummary = evidenceSummary;
            SuggestedSkipBytes = suggestedSkipBytes;
            SuggestedRangeStart = suggestedRangeStart;
            SuggestedRangeLength = suggestedRangeLength;
            SuggestedPaddingIndex = suggestedPaddingIndex;
            SuggestedIvModeIndex = suggestedIvModeIndex;
            SuggestedIvFromPrefix = suggestedIvFromPrefix;
            SuggestedCatalogPath = suggestedCatalogPath;
            SelectCommand = new RelayCommand(() => onSelect(this));
        }

        public string SchemeId { get; }
        public int AlgorithmIndex { get; }
        public string DisplayName { get; }
        public double Confidence { get; }
        public string ConfidencePercentText => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Confidence * 100.0):0}%");
        public int MatchedFileCount { get; }
        public string FilePattern { get; }
        public string EvidenceSummary { get; }
        public long SuggestedSkipBytes { get; }
        public long SuggestedRangeStart { get; }
        public long SuggestedRangeLength { get; }
        public int SuggestedPaddingIndex { get; }
        public int SuggestedIvModeIndex { get; }
        public bool SuggestedIvFromPrefix { get; }
        public string? SuggestedCatalogPath { get; }
        public bool IsDetected => true;

        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            internal set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    OnPropertyChanged(nameof(ChipLabel));
                }
            }
        }

        public string ChipLabel
        {
            get
            {
                var marker = IsSelected ? "\u25CF " : string.Empty;
                var scope = !string.IsNullOrWhiteSpace(FilePattern) ? $" \u00B7 {FilePattern}" : string.Empty;
                var skip = SuggestedSkipBytes > 0 ? $" \u00B7 +{SuggestedSkipBytes}B" : string.Empty;
                var range = SuggestedRangeLength > 0 ? $" \u00B7 0x{SuggestedRangeLength:X}B" : string.Empty;
                return $"{marker}{DisplayName} ({ConfidencePercentText}){scope}{skip}{range}";
            }
        }

        public RelayCommand SelectCommand { get; }
    }

    public sealed class DecryptionProfileListItemViewModel : ViewModelBase
    {
        public DecryptionProfileListItemViewModel(DecryptionProfile profile)
        {
            Profile = profile.Clone();
        }

        public DecryptionProfile Profile { get; }
        public string Id => Profile.Id;
        public string AlgorithmLabel => string.IsNullOrWhiteSpace(Profile.Algorithm)
            ? (!string.IsNullOrWhiteSpace(Profile.CatalogPath) || !string.IsNullOrWhiteSpace(Profile.Block512Token)
                ? "block512"
                : (!string.IsNullOrWhiteSpace(Profile.XorKeyHex) ? "xor" : "none"))
            : Profile.Algorithm!;
        public string PatternLabel => string.IsNullOrWhiteSpace(Profile.FileNamePattern)
            ? (string.IsNullOrWhiteSpace(Profile.PathPattern) ? "*" : Profile.PathPattern!)
            : Profile.FileNamePattern!;
        public string SummaryText
        {
            get
            {
                var status = Profile.Enabled ? string.Empty : " [disabled]";
                var range = Profile.RangeLength > 0 ? $", range {Profile.RangeStart}..{Profile.RangeStart + Profile.RangeLength}" : string.Empty;
                var skip = Profile.SkipBytes > 0 ? $", skip {Profile.SkipBytes}B" : string.Empty;
                return $"{AlgorithmLabel.ToUpperInvariant()} | {PatternLabel}{skip}{range}{status}";
            }
        }
    }

    public sealed class DecryptionKeyDialogViewModel : ViewModelBase
    {
        private readonly DecryptionDialogContext _context;
        private readonly IDialogService? _dialogService;
        private readonly string? _userProfilesDirectoryOverride;
        private bool _profilesModified;

        private DecryptionProfileListItemViewModel? _selectedProfile;
        private string _profileId = string.Empty;
        private int _selectedAlgorithmIndex;
        private string _keyInput = string.Empty;
        private int _selectedKeyFormatIndex;
        private string _ivInput = string.Empty;
        private int _selectedIvModeIndex;
        private int _selectedPaddingIndex;
        private string _fileNamePattern = "*.bundle;*.enc";
        private string _pathPattern = string.Empty;
        private bool _validateUnityHeader = true;
        private string _skipBytesText = "0";
        private string _rangeStartText = "0";
        private string _rangeLengthText = "-1";
        private string _magicHex = string.Empty;
        private string _magicOffsetText = "0";
        private int _selectedKdfIndex;
        private string _saltInput = string.Empty;
        private int _selectedSaltFormatIndex;
        private string _kdfIterationsText = "1000";
        private int _selectedDerivedKeyLengthIndex;
        private string _counterStartText = "0";
        private bool _counterLittleEndian;
        private string _block512CatalogPath = string.Empty;
        private string _block512Token = string.Empty;
        private bool _isAdvancedOptionsExpanded;
        private string _validationError = string.Empty;
        private string _statusMessage = string.Empty;
        private string _pluginsSummaryText = string.Empty;
        private readonly string? _captureSourceFile;
        private GameCaptureTarget? _captureTarget;
        private bool _isCapturing;
        private string _captureStatusText = string.Empty;
        private string _captureAntiCheatText = string.Empty;
        private double _captureProgressPercent;

        public DecryptionKeyDialogViewModel(
            DecryptionDialogContext context,
            IDialogService? dialogService = null,
            string? userProfilesDirectoryOverride = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dialogService = dialogService;
            _userProfilesDirectoryOverride = userProfilesDirectoryOverride;

            AlgorithmOptions = new[]
            {
                "AES-CBC",
                "AES-CTR",
                "AES-ECB",
                "ChaCha20",
                "XOR",
                "Offset Skip (None)",
                "Custom ChaCha (512B Block XOR)",
                "Rijndael-CBC"
            };
            KeyFormatOptions = new[] { "Hex", "UTF-8 Text" };
            IvModeOptions = new[] { "Hex", "UTF-8 Text", "Read from Stream Prefix", "None (All Zeros)", "Derive from Filename (MD5)" };
            PaddingOptions = new[] { "None", "PKCS7" };
            KdfOptions = new[] { "None", "SHA-256", "MD5", "PBKDF2-SHA256", "PBKDF2-SHA1", "PBKDF1-SHA1" };
            SaltFormatOptions = new[] { "UTF-8 Text", "Hex" };
            DerivedKeyLengthOptions = new[] { "Auto (32 bytes)", "16 bytes (128-bit)", "24 bytes (192-bit)", "32 bytes (256-bit)" };

            Profiles = new ObservableCollection<DecryptionProfileListItemViewModel>();
            BarrierSamples = new ObservableCollection<DecryptionBarrierSampleItem>();
            DetectedAlgorithms = new ObservableCollection<DetectedAlgorithmCandidateItem>();

            var defaultPattern = DeriveFilePatternFromPaths(
                _context.DetectedBarriers?
                    .Where(static b => !UnityHeaderProbe.IsNonStreamDiagnosticHint(b.ProviderId))
                    .Select(static b => b.FilePath));
            if (!string.IsNullOrWhiteSpace(defaultPattern))
            {
                _fileNamePattern = defaultPattern;
            }

            if (_context.DetectedBarriers != null && _context.DetectedBarriers.Count > 0)
            {
                foreach (var barrier in _context.DetectedBarriers.Take(6))
                {
                    BarrierSamples.Add(new DecryptionBarrierSampleItem(barrier));
                }
            }

            _captureSourceFile = (_context.DetectedBarriers ?? Array.Empty<StreamEncryptionBarrier>())
                .Select(static barrier => barrier.FilePath)
                .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            _captureTarget = _captureSourceFile == null ? null : GameCaptureTarget.TryResolve(_captureSourceFile);

            NewProfileCommand = new RelayCommand(ResetEditorForm);
            LoadSelectedProfileCommand = new RelayCommand(LoadSelectedProfileIntoEditor, () => SelectedProfile != null);
            SaveProfileCommand = new RelayCommand(ExecuteSaveProfile);
            TestKeyCommand = new RelayCommand(ExecuteTestKey);
            BrowseBlock512CatalogCommand = new AsyncRelayCommand(ExecuteBrowseBlock512CatalogAsync);
            DeleteSelectedProfileCommand = new RelayCommand(ExecuteDeleteSelectedProfile, () => SelectedProfile != null);
            ImportJsonCommand = new AsyncRelayCommand(ExecuteImportJsonAsync);
            OpenProfilesFolderCommand = new AsyncRelayCommand(ExecuteOpenProfilesFolderAsync);
            OpenPluginsFolderCommand = new AsyncRelayCommand(ExecuteOpenPluginsFolderAsync);
            ReloadPluginsCommand = new RelayCommand(ExecuteReloadPlugins);
            ApplyAndReanalyzeCommand = new RelayCommand(ExecuteApplyAndReanalyze, () => !IsCapturing);
            SecondaryCloseCommand = new RelayCommand(ExecuteSecondaryClose, () => !IsCapturing);
            CaptureFromGameCommand = new AsyncRelayCommand(ExecuteCaptureFromGameAsync, () => !IsCapturing && _captureTarget != null, onException: ex => ValidationError = ex.Message);
            CancelCaptureCommand = new RelayCommand(() => CaptureFromGameCommand.Cancel(), () => IsCapturing);
            ChooseCaptureExecutableCommand = new AsyncRelayCommand(ExecuteChooseCaptureExecutableAsync, () => !IsCapturing && _captureSourceFile != null, onException: ex => ValidationError = ex.Message);

            PopulateDetectedAlgorithmCandidates(defaultPattern);
            if (DetectedAlgorithms.Count > 0)
            {
                ApplyDetectedCandidate(DetectedAlgorithms[0], preserveUserInputs: false);
            }

            RefreshProfilesAndPluginsList();
            TryHighlightMatchingExistingProfile();
        }

        public event Action<DecryptionKeyDialogResult>? RequestClose;

        public IReadOnlyList<string> AlgorithmOptions { get; }
        public IReadOnlyList<string> KeyFormatOptions { get; }
        public IReadOnlyList<string> IvModeOptions { get; }
        public IReadOnlyList<string> PaddingOptions { get; }
        public IReadOnlyList<string> KdfOptions { get; }
        public IReadOnlyList<string> SaltFormatOptions { get; }
        public IReadOnlyList<string> DerivedKeyLengthOptions { get; }

        public ObservableCollection<DecryptionProfileListItemViewModel> Profiles { get; }
        public ObservableCollection<DecryptionBarrierSampleItem> BarrierSamples { get; }
        public ObservableCollection<DetectedAlgorithmCandidateItem> DetectedAlgorithms { get; }

        public bool IsInitialLoadPrompt => _context.IsInitialLoadPrompt;
        public bool HasEncryptedFiles => _context.EncryptedFileCount > 0;
        public bool HasBarrierSamples => BarrierSamples.Count > 0;
        public bool HasDetectedAlgorithms => DetectedAlgorithms.Count > 0;
        public bool HasMultipleDetectedAlgorithms => DetectedAlgorithms.Count > 1;
        public bool HasProfiles => Profiles.Count > 0;

        public bool IsSelectedAlgorithmAutoDetected =>
            DetectedAlgorithms.Any(c => c.AlgorithmIndex == _selectedAlgorithmIndex);

        public string DetectedAlgorithmsBannerText
        {
            get
            {
                if (DetectedAlgorithms.Count == 0)
                {
                    return string.Empty;
                }

                if (DetectedAlgorithms.Count == 1)
                {
                    var only = DetectedAlgorithms[0];
                    return $"Auto-detected 1 candidate: {only.DisplayName} ({only.ConfidencePercentText}) — pre-selected below";
                }

                var active = DetectedAlgorithms.FirstOrDefault(static c => c.IsSelected) ?? DetectedAlgorithms[0];
                return $"Auto-detected {DetectedAlgorithms.Count} candidates ({active.DisplayName} pre-selected; click any chip to switch or use Test Key to auto-match)";
            }
        }

        public string DialogTitle => _context.IsInitialLoadPrompt && HasEncryptedFiles
            ? "Decryption Key Optional — Encrypted Files Detected"
            : "Decryption Keys & Plugins";

        public string EncryptedSummaryTitle
        {
            get
            {
                if (!HasEncryptedFiles)
                {
                    return "Configure local decryption profiles or plugins for encrypted Unity assets.";
                }

                var total = Math.Max(0, _context.ReadableAssetFileCount) + Math.Max(0, _context.EncryptedFileCount);
                var pct = ProjectScanResult.ComputeCoveragePercent(_context.ReadableAssetFileCount, _context.EncryptedFileCount);
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_context.EncryptedFileCount:N0} encrypted file(s) detected — {_context.ReadableAssetFileCount:N0}/{total:N0} readable ({pct:0.#}%)");
            }
        }

        public string EncryptedSummaryDetail => _context.IsInitialLoadPrompt && HasEncryptedFiles
            ? "Enter a decryption key below to unlock encrypted files, or choose \"Continue Without Key\" to load only readable assets. You can enter or change keys at any time later to re-analyze."
            : "Profiles are saved locally in your user folder (never inside the project or repository). You can apply a key or load a custom IStreamTransformProvider plugin DLL and re-analyze immediately.";

        public string PrimaryButtonText => HasEncryptedFiles || !string.IsNullOrWhiteSpace(_context.ProjectRoot)
            ? "Apply & Re-analyze"
            : "Save & Close";

        public string SecondaryButtonText => _context.IsInitialLoadPrompt
            ? "Continue Without Key"
            : "Close";

        public string PluginsSummaryText
        {
            get => _pluginsSummaryText;
            private set => SetProperty(ref _pluginsSummaryText, value ?? string.Empty);
        }

        public DecryptionProfileListItemViewModel? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (SetProperty(ref _selectedProfile, value))
                {
                    LoadSelectedProfileCommand.NotifyCanExecuteChanged();
                    DeleteSelectedProfileCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string ProfileId
        {
            get => _profileId;
            set => SetProperty(ref _profileId, value ?? string.Empty);
        }

        public int SelectedAlgorithmIndex
        {
            get => _selectedAlgorithmIndex;
            set
            {
                if (SetProperty(ref _selectedAlgorithmIndex, value))
                {
                    SyncSelectedCandidateHighlight();
                    OnPropertyChanged(nameof(IsBlock512Selected));
                    OnPropertyChanged(nameof(IsKeySectionVisible));
                    OnPropertyChanged(nameof(IsIvSectionVisible));
                    OnPropertyChanged(nameof(IsIvTextInputEnabled));
                    OnPropertyChanged(nameof(IsPaddingVisible));
                    OnPropertyChanged(nameof(IsCtrOptionsVisible));
                    OnPropertyChanged(nameof(IsKdfSaltVisible));
                    OnPropertyChanged(nameof(IsKdfLengthVisible));
                    OnPropertyChanged(nameof(IsSelectedAlgorithmAutoDetected));
                    OnPropertyChanged(nameof(DetectedAlgorithmsBannerText));
                    ClearFeedback();
                }
            }
        }

        public string KeyInput
        {
            get => _keyInput;
            set
            {
                if (SetProperty(ref _keyInput, value ?? string.Empty))
                {
                    ClearFeedback();
                }
            }
        }

        public int SelectedKeyFormatIndex
        {
            get => _selectedKeyFormatIndex;
            set
            {
                if (SetProperty(ref _selectedKeyFormatIndex, value))
                {
                    ClearFeedback();
                }
            }
        }

        public string IvInput
        {
            get => _ivInput;
            set
            {
                if (SetProperty(ref _ivInput, value ?? string.Empty))
                {
                    ClearFeedback();
                }
            }
        }

        public int SelectedIvModeIndex
        {
            get => _selectedIvModeIndex;
            set
            {
                if (SetProperty(ref _selectedIvModeIndex, value))
                {
                    OnPropertyChanged(nameof(IsIvTextInputEnabled));
                    ClearFeedback();
                }
            }
        }

        public int SelectedPaddingIndex
        {
            get => _selectedPaddingIndex;
            set => SetProperty(ref _selectedPaddingIndex, value);
        }

        public string FileNamePattern
        {
            get => _fileNamePattern;
            set => SetProperty(ref _fileNamePattern, value ?? string.Empty);
        }

        public string PathPattern
        {
            get => _pathPattern;
            set => SetProperty(ref _pathPattern, value ?? string.Empty);
        }

        public bool ValidateUnityHeader
        {
            get => _validateUnityHeader;
            set => SetProperty(ref _validateUnityHeader, value);
        }

        public string SkipBytesText
        {
            get => _skipBytesText;
            set => SetProperty(ref _skipBytesText, value ?? string.Empty);
        }

        public string RangeStartText
        {
            get => _rangeStartText;
            set => SetProperty(ref _rangeStartText, value ?? string.Empty);
        }

        public string RangeLengthText
        {
            get => _rangeLengthText;
            set => SetProperty(ref _rangeLengthText, value ?? string.Empty);
        }

        public string MagicHex
        {
            get => _magicHex;
            set => SetProperty(ref _magicHex, value ?? string.Empty);
        }

        public string MagicOffsetText
        {
            get => _magicOffsetText;
            set => SetProperty(ref _magicOffsetText, value ?? string.Empty);
        }

        public int SelectedKdfIndex
        {
            get => _selectedKdfIndex;
            set
            {
                if (SetProperty(ref _selectedKdfIndex, value))
                {
                    OnPropertyChanged(nameof(IsKdfSaltVisible));
                    OnPropertyChanged(nameof(IsKdfLengthVisible));
                    ClearFeedback();
                }
            }
        }

        public string SaltInput
        {
            get => _saltInput;
            set => SetProperty(ref _saltInput, value ?? string.Empty);
        }

        public int SelectedSaltFormatIndex
        {
            get => _selectedSaltFormatIndex;
            set => SetProperty(ref _selectedSaltFormatIndex, value);
        }

        public string KdfIterationsText
        {
            get => _kdfIterationsText;
            set => SetProperty(ref _kdfIterationsText, value ?? string.Empty);
        }

        public int SelectedDerivedKeyLengthIndex
        {
            get => _selectedDerivedKeyLengthIndex;
            set => SetProperty(ref _selectedDerivedKeyLengthIndex, value);
        }

        public string CounterStartText
        {
            get => _counterStartText;
            set => SetProperty(ref _counterStartText, value ?? string.Empty);
        }

        public bool CounterLittleEndian
        {
            get => _counterLittleEndian;
            set => SetProperty(ref _counterLittleEndian, value);
        }

        public string Block512CatalogPath
        {
            get => _block512CatalogPath;
            set
            {
                if (SetProperty(ref _block512CatalogPath, value ?? string.Empty))
                {
                    ClearFeedback();
                }
            }
        }

        public string Block512Token
        {
            get => _block512Token;
            set
            {
                if (SetProperty(ref _block512Token, value ?? string.Empty))
                {
                    ClearFeedback();
                }
            }
        }

        public bool IsAdvancedOptionsExpanded
        {
            get => _isAdvancedOptionsExpanded;
            set => SetProperty(ref _isAdvancedOptionsExpanded, value);
        }

        public string ValidationError
        {
            get => _validationError;
            private set
            {
                if (SetProperty(ref _validationError, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasValidationError));
                }
            }
        }

        public bool HasValidationError => !string.IsNullOrWhiteSpace(_validationError);

        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (SetProperty(ref _statusMessage, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasStatusMessage));
                }
            }
        }

        public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

        public bool IsBlock512Selected => SelectedAlgorithmIndex == 6;
        public bool IsKeySectionVisible => SelectedAlgorithmIndex != 5 && !IsBlock512Selected;
        public bool IsIvSectionVisible => SelectedAlgorithmIndex is 0 or 1 or 3 or 7;
        public bool IsIvTextInputEnabled => IsIvSectionVisible && SelectedIvModeIndex is 0 or 1;
        public bool IsPaddingVisible => SelectedAlgorithmIndex is 0 or 2 or 7;
        public bool IsCtrOptionsVisible => SelectedAlgorithmIndex is 1 or 3;
        public bool IsKdfSaltVisible => IsKeySectionVisible && SelectedKdfIndex is 3 or 4 or 5;
        public bool IsKdfLengthVisible => IsKeySectionVisible && SelectedKdfIndex > 0;

        public RelayCommand NewProfileCommand { get; }
        public RelayCommand LoadSelectedProfileCommand { get; }
        public RelayCommand SaveProfileCommand { get; }
        public RelayCommand TestKeyCommand { get; }
        public AsyncRelayCommand BrowseBlock512CatalogCommand { get; }
        public RelayCommand DeleteSelectedProfileCommand { get; }
        public AsyncRelayCommand ImportJsonCommand { get; }
        public AsyncRelayCommand OpenProfilesFolderCommand { get; }
        public AsyncRelayCommand OpenPluginsFolderCommand { get; }
        public RelayCommand ReloadPluginsCommand { get; }
        public RelayCommand ApplyAndReanalyzeCommand { get; }
        public RelayCommand SecondaryCloseCommand { get; }
        public AsyncRelayCommand CaptureFromGameCommand { get; }
        public RelayCommand CancelCaptureCommand { get; }
        public AsyncRelayCommand ChooseCaptureExecutableCommand { get; }

        public bool ShowGameCaptureSection => _captureSourceFile != null;

        public bool HasCaptureTarget => _captureTarget != null;

        public string CaptureGameSummaryText => _captureTarget != null
            ? $"Detected game: {_captureTarget.GameName}. AssetStudio launches it, reads the key while it starts up, and closes it again. The game folder is never modified."
            : "No game was found next to the encrypted files. Choose the game executable: AssetStudio launches it, reads the key while it starts up, and closes it again. The game folder is never modified.";

        public bool IsCapturing
        {
            get => _isCapturing;
            private set
            {
                if (SetProperty(ref _isCapturing, value))
                {
                    OnPropertyChanged(nameof(HasCaptureStatusText));
                    OnPropertyChanged(nameof(HasCaptureAntiCheatText));
                    ApplyAndReanalyzeCommand.NotifyCanExecuteChanged();
                    SecondaryCloseCommand.NotifyCanExecuteChanged();
                    CaptureFromGameCommand.NotifyCanExecuteChanged();
                    CancelCaptureCommand.NotifyCanExecuteChanged();
                    ChooseCaptureExecutableCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string CaptureStatusText
        {
            get => _captureStatusText;
            private set
            {
                if (SetProperty(ref _captureStatusText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasCaptureStatusText));
                }
            }
        }

        public bool HasCaptureStatusText => !string.IsNullOrWhiteSpace(_captureStatusText);

        public string CaptureAntiCheatText
        {
            get => _captureAntiCheatText;
            private set
            {
                if (SetProperty(ref _captureAntiCheatText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasCaptureAntiCheatText));
                }
            }
        }

        public bool HasCaptureAntiCheatText => !string.IsNullOrWhiteSpace(_captureAntiCheatText);

        public double CaptureProgressPercent
        {
            get => _captureProgressPercent;
            private set => SetProperty(ref _captureProgressPercent, value);
        }

        public void SetCaptureExecutable(string? executablePath)
        {
            ClearFeedback();
            GameCaptureTarget? target = _captureSourceFile == null ? null : GameCaptureTarget.FromExecutable(executablePath, _captureSourceFile);
            if (target == null)
            {
                ValidationError = executablePath == null
                    ? "No executable was selected."
                    : "The selected file cannot be used as the game executable for these encrypted files.";
                return;
            }

            _captureTarget = target;
            OnPropertyChanged(nameof(HasCaptureTarget));
            OnPropertyChanged(nameof(CaptureGameSummaryText));
            CaptureFromGameCommand.NotifyCanExecuteChanged();
        }

        private async Task ExecuteChooseCaptureExecutableAsync(CancellationToken cancellationToken)
        {
            if (_dialogService == null || IsCapturing)
            {
                return;
            }

            IReadOnlyList<string> files = await _dialogService.PickOpenFilesAsync(new FilePickerRequest(
                title: "Choose the game executable",
                allowMultiple: false,
                suggestedStartDirectory: Path.GetDirectoryName(_captureSourceFile),
                fileTypeFilters: OperatingSystem.IsWindows() ? new[] { new FileTypeFilterSpec("Game executable", new[] { "*.exe" }) } : null));
            cancellationToken.ThrowIfCancellationRequested();
            SetCaptureExecutable(files.Count > 0 ? files[0] : null);
        }

        private async Task ExecuteCaptureFromGameAsync(CancellationToken cancellationToken)
        {
            if (_captureTarget == null || IsCapturing)
            {
                return;
            }

            ClearFeedback();
            CaptureAntiCheatText = string.Empty;
            CaptureStatusText = "Preparing…";
            CaptureProgressPercent = 0;
            IsCapturing = true;
            try
            {
                var progress = new Progress<GameCaptureProgress>(UpdateCaptureProgress);
                GameCaptureResult result = await GameKeyCapture.RunAsync(_captureTarget, progress, cancellationToken: cancellationToken);
                CaptureProgressPercent = result.Status == GameCaptureStatus.Succeeded ? 100 : CaptureProgressPercent;
                if (result.Status == GameCaptureStatus.Succeeded && result.Profile != null)
                {
                    CaptureStatusText = string.Empty;
                    try
                    {
                        GameKeyCapture.SaveProfile(result.Profile, _userProfilesDirectoryOverride);
                        _profilesModified = true;
                        RefreshProfilesAndPluginsList();
                        StatusMessage = $"Captured the key from {_captureTarget.GameName} and saved profile '{result.Profile.Id}' ({result.ValidatedFiles} of {result.CheckedFiles} files validated).";
                    }
                    catch (Exception ex)
                    {
                        ValidationError = $"The key was captured but the profile could not be saved: {ex.Message}";
                    }
                }
                else if (result.Status == GameCaptureStatus.AntiCheatDetected)
                {
                    CaptureAntiCheatText = result.Message;
                    CaptureStatusText = "Capture aborted: the game is protected by anti-cheat.";
                }
                else
                {
                    CaptureStatusText = result.Message;
                }
            }
            finally
            {
                IsCapturing = false;
            }
        }

        private void UpdateCaptureProgress(GameCaptureProgress value)
        {
            if (!IsCapturing)
            {
                return;
            }

            CaptureStatusText = value.Message;
            CaptureProgressPercent = value.Percent;
        }

        public bool HasPendingEditorInput()
        {
            if (SelectedAlgorithmIndex == 5)
            {
                return long.TryParse(SkipBytesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skip) && skip > 0;
            }

            if (IsBlock512Selected)
            {
                return !string.IsNullOrWhiteSpace(Block512CatalogPath) || !string.IsNullOrWhiteSpace(Block512Token);
            }

            return !string.IsNullOrWhiteSpace(KeyInput);
        }

        public bool TryBuildProfileFromEditor(out DecryptionProfile? profile, out string? error)
        {
            profile = null;
            error = null;

            if (!long.TryParse(string.IsNullOrWhiteSpace(SkipBytesText) ? "0" : SkipBytesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skipBytes) || skipBytes < 0)
            {
                error = "Skip Bytes must be a non-negative integer.";
                return false;
            }

            if (!long.TryParse(string.IsNullOrWhiteSpace(RangeStartText) ? "0" : RangeStartText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rangeStart) || rangeStart < 0)
            {
                error = "Range Start must be a non-negative integer.";
                return false;
            }

            if (!long.TryParse(string.IsNullOrWhiteSpace(RangeLengthText) ? "-1" : RangeLengthText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rangeLength) || rangeLength < -1)
            {
                error = "Range Length must be -1 (full file) or a non-negative integer.";
                return false;
            }

            return TryBuildProfileForSettings(
                SelectedAlgorithmIndex,
                skipBytes,
                rangeStart,
                rangeLength,
                SelectedPaddingIndex,
                SelectedIvModeIndex,
                out profile,
                out error);
        }

        private bool TryBuildProfileForSettings(
            int algorithmIndex,
            long skipBytes,
            long rangeStart,
            long rangeLength,
            int paddingIndex,
            int ivModeIndex,
            out DecryptionProfile? profile,
            out string? error)
        {
            profile = null;
            error = null;

            string algoName = AlgorithmIndexToSchemeId(algorithmIndex);

            if (!int.TryParse(string.IsNullOrWhiteSpace(MagicOffsetText) ? "0" : MagicOffsetText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var magicOffset) || magicOffset < 0)
            {
                error = "Magic Offset must be a non-negative integer.";
                return false;
            }

            if (!uint.TryParse(string.IsNullOrWhiteSpace(CounterStartText) ? "0" : CounterStartText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var counterStart))
            {
                error = "Counter Start must be a non-negative 32-bit integer.";
                return false;
            }

            if (!int.TryParse(string.IsNullOrWhiteSpace(KdfIterationsText) ? "1000" : KdfIterationsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kdfIterations) || kdfIterations <= 0)
            {
                error = "KDF Iterations must be a positive integer.";
                return false;
            }

            bool isBlock512 = algorithmIndex == 6;
            bool usesKey = algorithmIndex != 5 && !isBlock512;
            bool usesIv = algorithmIndex is 0 or 1 or 3 or 7;
            bool usesPadding = algorithmIndex is 0 or 2 or 7;
            bool usesKdfSalt = usesKey && SelectedKdfIndex is 3 or 4 or 5;

            var candidate = new DecryptionProfile
            {
                Id = string.IsNullOrWhiteSpace(ProfileId)
                    ? $"{algoName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                    : ProfileId.Trim(),
                Enabled = true,
                Algorithm = algoName,
                FileNamePattern = string.IsNullOrWhiteSpace(FileNamePattern) ? null : FileNamePattern.Trim(),
                PathPattern = string.IsNullOrWhiteSpace(PathPattern) ? null : PathPattern.Trim(),
                MagicHex = string.IsNullOrWhiteSpace(MagicHex) ? null : MagicHex.Trim(),
                MagicOffset = magicOffset,
                SkipBytes = skipBytes,
                RangeStart = rangeStart,
                RangeLength = rangeLength,
                ValidateUnityHeader = ValidateUnityHeader,
                Padding = usesPadding && paddingIndex == 1 ? "pkcs7" : "none",
                CounterStart = counterStart,
                CounterLittleEndian = CounterLittleEndian
            };

            DecryptionProfile? registered = DecryptionHelper.GetRegisteredProfiles()
                .FirstOrDefault(p => string.Equals(p.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
            if (registered != null)
            {
                candidate.BlockSizeBits = registered.BlockSizeBits;
                candidate.HeaderMask = registered.HeaderMask?.Clone();
            }

            if (isBlock512)
            {
                candidate.CatalogPath = string.IsNullOrWhiteSpace(Block512CatalogPath) ? null : Block512CatalogPath.Trim();
                candidate.Block512Token = string.IsNullOrWhiteSpace(Block512Token) ? null : Block512Token.Trim();
                if (candidate.SkipBytes == 0)
                {
                    candidate.SkipBytes = 26;
                }
            }

            if (usesKey)
            {
                if (SelectedKeyFormatIndex == 0)
                {
                    candidate.KeyHex = KeyInput.Trim();
                }
                else
                {
                    candidate.KeyUtf8 = KeyInput;
                }

                string? kdf = SelectedKdfIndex switch
                {
                    1 => "sha256",
                    2 => "md5",
                    3 => "pbkdf2-sha256",
                    4 => "pbkdf2-sha1",
                    5 => "pbkdf1-sha1",
                    _ => null
                };
                if (kdf != null)
                {
                    candidate.KeyDerivation = kdf;
                    candidate.KdfIterations = kdfIterations;
                    candidate.DerivedKeyLength = SelectedDerivedKeyLengthIndex switch
                    {
                        1 => 16,
                        2 => 24,
                        3 => 32,
                        _ => 0
                    };
                    if (usesKdfSalt && !string.IsNullOrEmpty(SaltInput))
                    {
                        if (SelectedSaltFormatIndex == 1)
                        {
                            candidate.SaltHex = SaltInput.Trim();
                        }
                        else
                        {
                            candidate.SaltUtf8 = SaltInput;
                        }
                    }
                }
            }

            if (usesIv)
            {
                switch (ivModeIndex)
                {
                    case 0:
                        if (!string.IsNullOrWhiteSpace(IvInput))
                        {
                            if (algorithmIndex == 3)
                            {
                                candidate.NonceHex = IvInput.Trim();
                            }
                            else
                            {
                                candidate.IvHex = IvInput.Trim();
                            }
                        }
                        break;
                    case 1:
                        if (!string.IsNullOrEmpty(IvInput))
                        {
                            candidate.IvUtf8 = IvInput;
                        }
                        break;
                    case 2:
                        candidate.IvFromStreamPrefix = true;
                        break;
                    case 3:
                        // All zeros
                        break;
                    case 4:
                        candidate.IvFromFileNameHash = "md5";
                        break;
                }
            }

            if (!candidate.Validate(out error))
            {
                return false;
            }

            profile = candidate;
            return true;
        }

        private void RefreshProfilesAndPluginsList()
        {
            Profiles.Clear();
            foreach (var p in DecryptionHelper.GetRegisteredProfiles())
            {
                Profiles.Add(new DecryptionProfileListItemViewModel(p));
            }
            OnPropertyChanged(nameof(HasProfiles));

            var customProviders = DecryptionHelper.GetCustomProviders();
            PluginsSummaryText = customProviders.Count == 0
                ? "External plugins: none loaded (drop IStreamTransformProvider DLLs in Plugins/Decryption)"
                : $"External plugins loaded ({customProviders.Count}): {string.Join(", ", customProviders.Select(static p => p.Id))}";
        }

        private void ResetEditorForm()
        {
            ProfileId = string.Empty;
            SelectedAlgorithmIndex = 0;
            KeyInput = string.Empty;
            SelectedKeyFormatIndex = 0;
            IvInput = string.Empty;
            SelectedIvModeIndex = 0;
            SelectedPaddingIndex = 0;
            PathPattern = string.Empty;
            ValidateUnityHeader = true;
            SkipBytesText = "0";
            RangeStartText = "0";
            RangeLengthText = "-1";
            MagicHex = string.Empty;
            MagicOffsetText = "0";
            SelectedKdfIndex = 0;
            SaltInput = string.Empty;
            SelectedSaltFormatIndex = 0;
            KdfIterationsText = "1000";
            SelectedDerivedKeyLengthIndex = 0;
            CounterStartText = "0";
            CounterLittleEndian = false;
            Block512CatalogPath = string.Empty;
            Block512Token = string.Empty;
            IsAdvancedOptionsExpanded = false;
            SyncSelectedCandidateHighlight();
            OnPropertyChanged(nameof(IsSelectedAlgorithmAutoDetected));
            OnPropertyChanged(nameof(DetectedAlgorithmsBannerText));
            ClearFeedback();
        }

        private void LoadSelectedProfileIntoEditor()
        {
            if (SelectedProfile == null)
            {
                return;
            }

            var p = SelectedProfile.Profile;
            ProfileId = p.Id;
            StreamCipherAlgorithm algo;
            try
            {
                algo = p.ResolveAlgorithm();
            }
            catch
            {
                algo = StreamCipherAlgorithm.AesCbc;
            }

            SelectedAlgorithmIndex = algo switch
            {
                StreamCipherAlgorithm.AesCbc => 0,
                StreamCipherAlgorithm.AesCtr => 1,
                StreamCipherAlgorithm.AesEcb => 2,
                StreamCipherAlgorithm.ChaCha20 => 3,
                StreamCipherAlgorithm.Xor => 4,
                StreamCipherAlgorithm.None => 5,
                StreamCipherAlgorithm.Block512 => 6,
                StreamCipherAlgorithm.RijndaelCbc => 7,
                _ => 0
            };

            Block512CatalogPath = p.CatalogPath ?? string.Empty;
            Block512Token = p.Block512Token ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(p.KeyHex))
            {
                SelectedKeyFormatIndex = 0;
                KeyInput = p.KeyHex!;
            }
            else if (!string.IsNullOrEmpty(p.KeyUtf8))
            {
                SelectedKeyFormatIndex = 1;
                KeyInput = p.KeyUtf8!;
            }
            else if (!string.IsNullOrWhiteSpace(p.XorKeyHex))
            {
                SelectedKeyFormatIndex = 0;
                KeyInput = p.XorKeyHex!;
            }
            else
            {
                SelectedKeyFormatIndex = 0;
                KeyInput = string.Empty;
            }

            if (p.IvFromStreamPrefix)
            {
                SelectedIvModeIndex = 2;
                IvInput = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(p.IvFromFileNameHash))
            {
                SelectedIvModeIndex = 4;
                IvInput = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(p.IvHex) || !string.IsNullOrWhiteSpace(p.NonceHex))
            {
                SelectedIvModeIndex = 0;
                IvInput = p.IvHex ?? p.NonceHex ?? string.Empty;
            }
            else if (!string.IsNullOrEmpty(p.IvUtf8))
            {
                SelectedIvModeIndex = 1;
                IvInput = p.IvUtf8!;
            }
            else
            {
                SelectedIvModeIndex = 3;
                IvInput = string.Empty;
            }

            SelectedPaddingIndex = string.Equals(p.Padding?.Trim(), "pkcs7", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            FileNamePattern = p.FileNamePattern ?? string.Empty;
            PathPattern = p.PathPattern ?? string.Empty;
            ValidateUnityHeader = p.ValidateUnityHeader;
            SkipBytesText = p.SkipBytes.ToString(CultureInfo.InvariantCulture);
            RangeStartText = p.RangeStart.ToString(CultureInfo.InvariantCulture);
            RangeLengthText = p.RangeLength.ToString(CultureInfo.InvariantCulture);
            MagicHex = p.MagicHex ?? string.Empty;
            MagicOffsetText = p.MagicOffset.ToString(CultureInfo.InvariantCulture);
            CounterStartText = p.CounterStart.ToString(CultureInfo.InvariantCulture);
            CounterLittleEndian = p.CounterLittleEndian;

            SelectedKdfIndex = (p.KeyDerivation ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "sha256" or "sha-256" => 1,
                "md5" => 2,
                "pbkdf2" or "pbkdf2-sha256" => 3,
                "pbkdf2-sha1" => 4,
                "pbkdf1" or "pbkdf1-sha1" or "password-derive-bytes" => 5,
                _ => 0
            };

            if (!string.IsNullOrWhiteSpace(p.SaltHex))
            {
                SelectedSaltFormatIndex = 1;
                SaltInput = p.SaltHex!;
            }
            else
            {
                SelectedSaltFormatIndex = 0;
                SaltInput = p.SaltUtf8 ?? string.Empty;
            }

            KdfIterationsText = (p.KdfIterations > 0 ? p.KdfIterations : 1000).ToString(CultureInfo.InvariantCulture);
            SelectedDerivedKeyLengthIndex = p.DerivedKeyLength switch
            {
                16 => 1,
                24 => 2,
                32 => 3,
                _ => 0
            };

            ClearFeedback();
            StatusMessage = $"Loaded profile '{p.Id}' for editing.";
        }

        private void ExecuteSaveProfile()
        {
            ClearFeedback();
            if (!TryBuildProfileFromEditor(out var profile, out var error) || profile == null)
            {
                ValidationError = error ?? "Invalid decryption profile.";
                return;
            }

            try
            {
                DecryptionHelper.SaveProfileToUserDirectory(profile, _userProfilesDirectoryOverride);
                _profilesModified = true;
                RefreshProfilesAndPluginsList();
                StatusMessage = $"Saved local profile '{profile.Id}'.";
            }
            catch (Exception ex)
            {
                ValidationError = $"Failed to save profile: {ex.Message}";
            }
        }

        private void ExecuteDeleteSelectedProfile()
        {
            if (SelectedProfile == null)
            {
                return;
            }

            ClearFeedback();
            var id = SelectedProfile.Id;
            if (DecryptionHelper.RemoveProfile(id, deleteUserFile: _userProfilesDirectoryOverride == null))
            {
                if (!string.IsNullOrWhiteSpace(_userProfilesDirectoryOverride))
                {
                    var expected = Path.Combine(_userProfilesDirectoryOverride, id + ".json");
                    if (File.Exists(expected))
                    {
                        try { File.Delete(expected); } catch { }
                    }
                }
                _profilesModified = true;
                RefreshProfilesAndPluginsList();
                StatusMessage = $"Removed profile '{id}'.";
            }
        }

        private async Task ExecuteImportJsonAsync()
        {
            if (_dialogService == null)
            {
                return;
            }

            ClearFeedback();
            var files = await _dialogService.PickOpenFilesAsync(new FilePickerRequest(
                title: "Import Decryption Profiles JSON",
                allowMultiple: true,
                fileTypeFilters: new[] { new FileTypeFilterSpec("JSON profiles", "*.json") }));
            if (files.Count == 0)
            {
                return;
            }

            int totalImported = 0;
            foreach (var file in files)
            {
                totalImported += DecryptionHelper.ImportProfilesFromFile(file, saveToUserDirectory: true, userDirectory: _userProfilesDirectoryOverride);
            }

            if (totalImported > 0)
            {
                _profilesModified = true;
                RefreshProfilesAndPluginsList();
                StatusMessage = $"Imported {totalImported} decryption profile(s).";
            }
            else
            {
                ValidationError = "No valid decryption profiles were found in the selected JSON file(s).";
            }
        }

        private async Task ExecuteOpenProfilesFolderAsync()
        {
            var dir = string.IsNullOrWhiteSpace(_userProfilesDirectoryOverride)
                ? DecryptionHelper.GetUserDecryptionProfilesDirectory()
                : _userProfilesDirectoryOverride;
            Directory.CreateDirectory(dir);
            if (_dialogService != null)
            {
                await _dialogService.OpenPathInExplorerAsync(dir);
            }
        }

        private async Task ExecuteOpenPluginsFolderAsync()
        {
            var dir = DecryptionHelper.GetUserDecryptionPluginsDirectory();
            Directory.CreateDirectory(dir);
            if (_dialogService != null)
            {
                await _dialogService.OpenPathInExplorerAsync(dir);
            }
        }

        private void ExecuteReloadPlugins()
        {
            ClearFeedback();
            var (profilesCount, pluginsCount) = DecryptionHelper.ReloadLocalProfilesAndPlugins(_context.ProjectRoot);
            _profilesModified = true;
            RefreshProfilesAndPluginsList();
            StatusMessage = $"Reloaded {profilesCount} profile(s) and {pluginsCount} plugin provider(s).";
        }

        private void ExecuteApplyAndReanalyze()
        {
            ClearFeedback();
            if (HasPendingEditorInput())
            {
                if (!TryBuildProfileFromEditor(out var profile, out var error) || profile == null)
                {
                    ValidationError = error ?? "Please fix the decryption key parameters before applying.";
                    return;
                }

                try
                {
                    DecryptionHelper.SaveProfileToUserDirectory(profile, _userProfilesDirectoryOverride);
                    _profilesModified = true;
                    RefreshProfilesAndPluginsList();
                }
                catch (Exception ex)
                {
                    ValidationError = $"Failed to save profile: {ex.Message}";
                    return;
                }
            }

            RequestClose?.Invoke(new DecryptionKeyDialogResult(
                DecryptionDialogAction.ApplyAndReanalyze,
                _profilesModified));
        }

        private void ExecuteSecondaryClose()
        {
            var action = _context.IsInitialLoadPrompt
                ? DecryptionDialogAction.ContinueWithoutKey
                : DecryptionDialogAction.Close;
            RequestClose?.Invoke(new DecryptionKeyDialogResult(action, _profilesModified));
        }

        private void ClearFeedback()
        {
            ValidationError = string.Empty;
            StatusMessage = string.Empty;
        }

        private sealed class CandidateAccumulator
        {
            public CandidateAccumulator(DetectedCipherCandidate initial, int algorithmIndex)
            {
                BestCandidate = initial;
                AlgorithmIndex = algorithmIndex;
            }

            public DetectedCipherCandidate BestCandidate { get; set; }
            public int AlgorithmIndex { get; }
            public HashSet<string> MatchedFilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> PrimaryVariantFilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string? SuggestedCatalogPath { get; set; }
            public int HitCount { get; set; }
        }

        private void PopulateDetectedAlgorithmCandidates(string defaultBarrierPattern)
        {
            DetectedAlgorithms.Clear();

            var accumulators = new Dictionary<string, CandidateAccumulator>(StringComparer.OrdinalIgnoreCase);

            void RecordCandidate(DetectedCipherCandidate candidate, string? sourceFilePath, string? catalogPath)
            {
                int algoIndex = SchemeIdToAlgorithmIndex(candidate.SchemeId);
                if (algoIndex < 0)
                {
                    return;
                }

                if (!accumulators.TryGetValue(candidate.SchemeId, out var acc))
                {
                    acc = new CandidateAccumulator(candidate, algoIndex);
                    accumulators[candidate.SchemeId] = acc;
                }
                else
                {
                    bool replaceBest = candidate.Confidence > acc.BestCandidate.Confidence
                        || (Math.Abs(candidate.Confidence - acc.BestCandidate.Confidence) < 0.0001
                            && HasMoreSpecificStructure(candidate, acc.BestCandidate));
                    if (replaceBest)
                    {
                        acc.BestCandidate = candidate;
                        acc.PrimaryVariantFilePaths.Clear();
                    }
                }

                acc.HitCount++;
                if (!string.IsNullOrWhiteSpace(sourceFilePath))
                {
                    acc.MatchedFilePaths.Add(sourceFilePath);
                    if (candidate.SuggestedSkipBytes == acc.BestCandidate.SuggestedSkipBytes
                        && candidate.SuggestedRangeStart == acc.BestCandidate.SuggestedRangeStart
                        && candidate.SuggestedRangeLength == acc.BestCandidate.SuggestedRangeLength)
                    {
                        acc.PrimaryVariantFilePaths.Add(sourceFilePath);
                    }
                }

                if (string.IsNullOrWhiteSpace(acc.SuggestedCatalogPath)
                    && !string.IsNullOrWhiteSpace(catalogPath)
                    && File.Exists(catalogPath))
                {
                    acc.SuggestedCatalogPath = catalogPath;
                }
            }

            var streamBarriers = (_context.DetectedBarriers ?? Array.Empty<StreamEncryptionBarrier>())
                .Where(static b => !UnityHeaderProbe.IsNonStreamDiagnosticHint(b.ProviderId))
                .ToList();
            var hasFileBarriers = streamBarriers.Count > 0;
            if (hasFileBarriers)
            {
                foreach (var barrier in streamBarriers)
                {
                    var barrierCandidates = barrier.CandidateAlgorithms;
                    if (barrierCandidates == null || barrierCandidates.Count == 0)
                    {
                        barrierCandidates = AnalyzeBarrierFallback(barrier);
                    }

                    if (barrierCandidates != null)
                    {
                        foreach (var candidate in barrierCandidates)
                        {
                            RecordCandidate(candidate, barrier.FilePath, catalogPath: null);
                        }
                    }
                }
            }

            if (_context.CatalogHints != null && _context.CatalogHints.Count > 0)
            {
                var streamCatalogHints = _context.CatalogHints
                    .Where(static h => !UnityHeaderProbe.IsNonStreamDiagnosticHint(h.Provider))
                    .ToList();

                bool anyExplicitCatalogHint = false;
                foreach (var hint in streamCatalogHints)
                {
                    var explicitCandidates = UnityHeaderProbe.AnalyzeExplicitProviderHint(hint.Provider, hint.Reason);
                    if (explicitCandidates.Count > 0)
                    {
                        anyExplicitCatalogHint = true;
                        var sourcePath = !string.IsNullOrWhiteSpace(hint.Bundle) ? hint.Bundle : null;
                        foreach (var candidate in explicitCandidates)
                        {
                            RecordCandidate(candidate, sourcePath, hint.CatalogPath);
                        }
                    }
                }

                if (!hasFileBarriers && !anyExplicitCatalogHint)
                {
                    foreach (var hint in streamCatalogHints)
                    {
                        var sourcePath = !string.IsNullOrWhiteSpace(hint.Bundle) ? hint.Bundle : hint.CatalogPath;
                        var fallbackCandidates = UnityHeaderProbe.AnalyzeEncryptionCandidates(
                            ReadOnlySpan<byte>.Empty,
                            totalStreamLength: 0,
                            filePath: sourcePath,
                            providerHint: hint.Provider,
                            reasonHint: hint.Reason);
                        foreach (var candidate in fallbackCandidates)
                        {
                            RecordCandidate(candidate, !string.IsNullOrWhiteSpace(hint.Bundle) ? hint.Bundle : null, hint.CatalogPath);
                        }
                    }
                }
            }

            var ordered = accumulators.Values
                .OrderByDescending(static a => a.BestCandidate.Confidence)
                .ThenByDescending(static a => Math.Max(a.MatchedFilePaths.Count, a.HitCount))
                .ThenBy(static a => a.AlgorithmIndex)
                .ToList();

            foreach (var acc in ordered)
            {
                var best = acc.BestCandidate;
                var patternPaths = acc.PrimaryVariantFilePaths.Count > 0
                    ? acc.PrimaryVariantFilePaths
                    : acc.MatchedFilePaths;
                var candidatePattern = DeriveFilePatternFromPaths(patternPaths);
                if (string.IsNullOrWhiteSpace(candidatePattern))
                {
                    candidatePattern = defaultBarrierPattern;
                }

                int suggestedPaddingIndex = best.SuggestedPkcs7.HasValue
                    ? (best.SuggestedPkcs7.Value ? 1 : 0)
                    : -1;
                int suggestedIvModeIndex = best.SuggestedIvFromPrefix ? 2 : -1;

                var item = new DetectedAlgorithmCandidateItem(
                    schemeId: best.SchemeId,
                    algorithmIndex: acc.AlgorithmIndex,
                    displayName: best.DisplayName,
                    confidence: best.Confidence,
                    matchedFileCount: Math.Max(acc.MatchedFilePaths.Count, 1),
                    filePattern: candidatePattern,
                    evidenceSummary: best.Evidence,
                    suggestedSkipBytes: best.SuggestedSkipBytes,
                    suggestedRangeStart: best.SuggestedRangeStart,
                    suggestedRangeLength: best.SuggestedRangeLength,
                    suggestedPaddingIndex: suggestedPaddingIndex,
                    suggestedIvModeIndex: suggestedIvModeIndex,
                    suggestedIvFromPrefix: best.SuggestedIvFromPrefix,
                    suggestedCatalogPath: acc.SuggestedCatalogPath,
                    onSelect: selected => ApplyDetectedCandidate(selected, preserveUserInputs: true));

                DetectedAlgorithms.Add(item);
            }

            OnPropertyChanged(nameof(HasDetectedAlgorithms));
            OnPropertyChanged(nameof(HasMultipleDetectedAlgorithms));
            OnPropertyChanged(nameof(IsSelectedAlgorithmAutoDetected));
            OnPropertyChanged(nameof(DetectedAlgorithmsBannerText));
        }

        private static IReadOnlyList<DetectedCipherCandidate> AnalyzeBarrierFallback(StreamEncryptionBarrier barrier)
        {
            if (!string.IsNullOrWhiteSpace(barrier.FilePath) && File.Exists(barrier.FilePath))
            {
                try
                {
                    using var fs = new FileStream(barrier.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    int probeLen = (int)Math.Min(UnityHeaderProbe.DefaultProbeBufferSize, fs.Length);
                    if (probeLen >= 26)
                    {
                        var buf = new byte[probeLen];
                        int read = 0;
                        while (read < probeLen)
                        {
                            int n = fs.Read(buf, read, probeLen - read);
                            if (n <= 0) break;
                            read += n;
                        }

                        return UnityHeaderProbe.AnalyzeEncryptionCandidates(
                            buf.AsSpan(0, read),
                            fs.Length,
                            barrier.FilePath,
                            barrier.ProviderId,
                            barrier.Reason);
                    }
                }
                catch
                {
                }
            }

            return UnityHeaderProbe.AnalyzeEncryptionCandidates(
                ReadOnlySpan<byte>.Empty,
                totalStreamLength: 0,
                filePath: barrier.FilePath,
                providerHint: barrier.ProviderId,
                reasonHint: barrier.Reason);
        }

        private void ApplyDetectedCandidate(DetectedAlgorithmCandidateItem candidate, bool preserveUserInputs)
        {
            if (candidate == null)
            {
                return;
            }

            SelectedAlgorithmIndex = candidate.AlgorithmIndex;
            if (!string.IsNullOrWhiteSpace(candidate.FilePattern))
            {
                FileNamePattern = candidate.FilePattern;
            }

            SkipBytesText = candidate.SuggestedSkipBytes.ToString(CultureInfo.InvariantCulture);
            RangeStartText = candidate.SuggestedRangeStart.ToString(CultureInfo.InvariantCulture);
            RangeLengthText = candidate.SuggestedRangeLength.ToString(CultureInfo.InvariantCulture);

            if (candidate.SuggestedPaddingIndex >= 0)
            {
                SelectedPaddingIndex = candidate.SuggestedPaddingIndex;
            }
            else if (!preserveUserInputs)
            {
                SelectedPaddingIndex = 0;
            }

            if (candidate.SuggestedIvModeIndex >= 0)
            {
                SelectedIvModeIndex = candidate.SuggestedIvModeIndex;
            }
            else if (!preserveUserInputs)
            {
                SelectedIvModeIndex = 0;
            }

            if (candidate.AlgorithmIndex == 6
                && !string.IsNullOrWhiteSpace(candidate.SuggestedCatalogPath)
                && string.IsNullOrWhiteSpace(Block512CatalogPath))
            {
                Block512CatalogPath = candidate.SuggestedCatalogPath;
            }

            if ((candidate.AlgorithmIndex != 6 && candidate.SuggestedSkipBytes > 0)
                || candidate.SuggestedRangeStart > 0
                || candidate.SuggestedRangeLength > 0)
            {
                IsAdvancedOptionsExpanded = true;
            }

            SyncSelectedCandidateHighlight(candidate);
            OnPropertyChanged(nameof(IsSelectedAlgorithmAutoDetected));
            OnPropertyChanged(nameof(DetectedAlgorithmsBannerText));
        }

        private void SyncSelectedCandidateHighlight(DetectedAlgorithmCandidateItem? explicitSelected = null)
        {
            for (int i = 0; i < DetectedAlgorithms.Count; i++)
            {
                var item = DetectedAlgorithms[i];
                item.IsSelected = explicitSelected != null
                    ? ReferenceEquals(item, explicitSelected)
                    : item.AlgorithmIndex == _selectedAlgorithmIndex;
            }
        }

        private void TryHighlightMatchingExistingProfile()
        {
            if (Profiles.Count == 0 || DetectedAlgorithms.Count == 0)
            {
                return;
            }

            var primary = DetectedAlgorithms[0];
            foreach (var item in Profiles)
            {
                if (!item.Profile.Enabled)
                {
                    continue;
                }

                try
                {
                    var algo = item.Profile.ResolveAlgorithm();
                    int idx = algo switch
                    {
                        StreamCipherAlgorithm.AesCbc => 0,
                        StreamCipherAlgorithm.AesCtr => 1,
                        StreamCipherAlgorithm.AesEcb => 2,
                        StreamCipherAlgorithm.ChaCha20 => 3,
                        StreamCipherAlgorithm.Xor => 4,
                        StreamCipherAlgorithm.None => 5,
                        StreamCipherAlgorithm.Block512 => 6,
                        _ => -1
                    };
                    if (idx == primary.AlgorithmIndex)
                    {
                        SelectedProfile = item;
                        return;
                    }
                }
                catch
                {
                }
            }
        }

        private async Task ExecuteBrowseBlock512CatalogAsync()
        {
            if (_dialogService == null)
            {
                return;
            }

            var files = await _dialogService.PickOpenFilesAsync(new FilePickerRequest(
                title: "Select Addressables Catalog (JSON or BIN)",
                allowMultiple: false,
                suggestedStartDirectory: _context.ProjectRoot,
                fileTypeFilters: new[]
                {
                    new FileTypeFilterSpec("Addressables Catalogs", "*.json", "*.bin")
                }));

            if (files.Count > 0 && !string.IsNullOrWhiteSpace(files[0]))
            {
                Block512CatalogPath = files[0];
            }
        }

        private void ExecuteTestKey()
        {
            ClearFeedback();

            var sampleEntries = SelectRepresentativeSampleBarriers(maxSamples: 8);

            if (sampleEntries.Length == 0)
            {
                if (!TryBuildProfileFromEditor(out _, out var syntaxError))
                {
                    ValidationError = syntaxError ?? "Invalid decryption profile.";
                    return;
                }

                StatusMessage = "Parameters are valid. No accessible sample encrypted file on disk to test against — click Apply & Re-analyze to test on load.";
                return;
            }

            int testedAttempts = 0;

            // 1. Test the currently selected algorithm first (prioritizing files matching current FileNamePattern)
            bool currentValid = TryBuildProfileFromEditor(out var currentProfile, out var currentError);
            if (currentValid && currentProfile != null)
            {
                testedAttempts++;
                var orderedForCurrent = OrderSampleBarriersForPattern(sampleEntries, FileNamePattern);
                foreach (var entry in orderedForCurrent)
                {
                    if (DecryptionHelper.TryTestProfileOnFile(currentProfile, entry.FilePath, out var unlockedHeader))
                    {
                        StatusMessage = $"Key verified! Unlocked '{Path.GetFileName(entry.FilePath)}' as {unlockedHeader} using {AlgorithmOptions[SelectedAlgorithmIndex]}.";
                        return;
                    }
                }

                // If IV was left blank in Hex mode for AES-CBC/AES-CTR/ChaCha20, also probe stream-prefix IV
                if (SelectedAlgorithmIndex is 0 or 1 or 3 or 7
                    && SelectedIvModeIndex == 0
                    && string.IsNullOrWhiteSpace(IvInput))
                {
                    var prefixProbe = currentProfile.Clone();
                    prefixProbe.IvFromStreamPrefix = true;
                    foreach (var entry in orderedForCurrent)
                    {
                        if (DecryptionHelper.TryTestProfileOnFile(prefixProbe, entry.FilePath, out var unlockedHeader))
                        {
                            SelectedIvModeIndex = 2; // Read from Stream Prefix
                            StatusMessage = $"Key verified (auto-enabled Stream Prefix IV)! Unlocked '{Path.GetFileName(entry.FilePath)}' as {unlockedHeader} using {AlgorithmOptions[SelectedAlgorithmIndex]}.";
                            return;
                        }
                    }
                }
            }

            // 2. Automatically test remaining checked detected candidates and switch to the winning algorithm.
            //    When a heterogeneous folder mixes different barrier structures (e.g. partial-header .assets + full-stream .bundle),
            //    also honor each sample barrier's own structural candidate parameters for that scheme.
            var otherCandidates = DetectedAlgorithms
                .Where(c => c.IsChecked && c.AlgorithmIndex != SelectedAlgorithmIndex)
                .ToList();

            foreach (var candidate in otherCandidates)
            {
                var orderedForCandidate = OrderSampleBarriersForPattern(sampleEntries, candidate.FilePattern);
                foreach (var entry in orderedForCandidate)
                {
                    var barrierMatch = FindMatchingCandidateForBarrier(entry, candidate.SchemeId);
                    long trialSkip = barrierMatch?.SuggestedSkipBytes ?? candidate.SuggestedSkipBytes;
                    long trialRangeStart = barrierMatch?.SuggestedRangeStart ?? candidate.SuggestedRangeStart;
                    long trialRangeLength = barrierMatch?.SuggestedRangeLength ?? candidate.SuggestedRangeLength;
                    int trialPadding = barrierMatch?.SuggestedPkcs7.HasValue == true
                        ? (barrierMatch.SuggestedPkcs7.Value ? 1 : 0)
                        : (candidate.SuggestedPaddingIndex >= 0 ? candidate.SuggestedPaddingIndex : SelectedPaddingIndex);
                    int trialIvMode = barrierMatch?.SuggestedIvFromPrefix == true
                        ? 2
                        : (candidate.SuggestedIvModeIndex >= 0 ? candidate.SuggestedIvModeIndex : SelectedIvModeIndex);

                    if (!TryBuildProfileForSettings(
                        candidate.AlgorithmIndex,
                        trialSkip,
                        trialRangeStart,
                        trialRangeLength,
                        trialPadding,
                        trialIvMode,
                        out var trialProfile,
                        out _) || trialProfile == null)
                    {
                        continue;
                    }

                    testedAttempts++;
                    if (DecryptionHelper.TryTestProfileOnFile(trialProfile, entry.FilePath, out var winnerHeader))
                    {
                        ApplyVerifiedCandidateWin(candidate, entry.FilePath, trialSkip, trialRangeStart, trialRangeLength, trialPadding, trialIvMode);
                        StatusMessage = $"Auto-selected {candidate.DisplayName}! Key verified on '{Path.GetFileName(entry.FilePath)}' ({winnerHeader}).";
                        return;
                    }

                    if (candidate.AlgorithmIndex is 0 or 1 or 3 or 7
                        && trialIvMode == 0
                        && string.IsNullOrWhiteSpace(IvInput))
                    {
                        var prefixTrial = trialProfile.Clone();
                        prefixTrial.IvFromStreamPrefix = true;
                        if (DecryptionHelper.TryTestProfileOnFile(prefixTrial, entry.FilePath, out var prefixHeader))
                        {
                            ApplyVerifiedCandidateWin(candidate, entry.FilePath, trialSkip, trialRangeStart, trialRangeLength, trialPadding, ivModeIndex: 2);
                            StatusMessage = $"Auto-selected {candidate.DisplayName} (Stream Prefix IV)! Key verified on '{Path.GetFileName(entry.FilePath)}' ({prefixHeader}).";
                            return;
                        }
                    }
                }
            }

            if (!currentValid && !string.IsNullOrWhiteSpace(currentError))
            {
                ValidationError = currentError;
                return;
            }

            ValidationError = $"Key did not unlock a valid Unity header on '{Path.GetFileName(sampleEntries[0].FilePath)}' (tested {Math.Max(1, testedAttempts)} algorithm(s)). Check key, IV/nonce, range, or padding.";
        }

        private void ApplyVerifiedCandidateWin(
            DetectedAlgorithmCandidateItem candidate,
            string matchedFilePath,
            long skipBytes,
            long rangeStart,
            long rangeLength,
            int paddingIndex,
            int ivModeIndex)
        {
            ApplyDetectedCandidate(candidate, preserveUserInputs: true);
            var matchedPattern = DeriveFilePatternFromPaths(new[] { matchedFilePath });
            if (!string.IsNullOrWhiteSpace(matchedPattern))
            {
                FileNamePattern = matchedPattern;
            }
            SkipBytesText = skipBytes.ToString(CultureInfo.InvariantCulture);
            RangeStartText = rangeStart.ToString(CultureInfo.InvariantCulture);
            RangeLengthText = rangeLength.ToString(CultureInfo.InvariantCulture);
            if (paddingIndex >= 0)
            {
                SelectedPaddingIndex = paddingIndex;
            }
            if (ivModeIndex >= 0)
            {
                SelectedIvModeIndex = ivModeIndex;
            }
            IsAdvancedOptionsExpanded = (candidate.AlgorithmIndex != 6 && skipBytes > 0) || rangeStart > 0 || rangeLength > 0;
        }

        private static DetectedCipherCandidate? FindMatchingCandidateForBarrier(StreamEncryptionBarrier barrier, string schemeId)
        {
            var list = barrier.CandidateAlgorithms;
            if (list == null || list.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].SchemeId, schemeId, StringComparison.OrdinalIgnoreCase))
                {
                    return list[i];
                }
            }

            return null;
        }

        private StreamEncryptionBarrier[] SelectRepresentativeSampleBarriers(int maxSamples)
        {
            var accessibleBarriers = new List<StreamEncryptionBarrier>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var barrier in _context.DetectedBarriers ?? Array.Empty<StreamEncryptionBarrier>())
            {
                if (UnityHeaderProbe.IsNonStreamDiagnosticHint(barrier.ProviderId)
                    || string.IsNullOrWhiteSpace(barrier.FilePath)
                    || !File.Exists(barrier.FilePath))
                {
                    continue;
                }

                if (seenPaths.Add(barrier.FilePath))
                {
                    accessibleBarriers.Add(barrier);
                }
            }

            if (accessibleBarriers.Count == 0)
            {
                return Array.Empty<StreamEncryptionBarrier>();
            }

            var selected = new List<StreamEncryptionBarrier>(Math.Min(maxSamples, accessibleBarriers.Count));
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredGroups = new HashSet<(string ProviderId, string ExtensionKey)>();

            static (string ProviderId, string ExtensionKey) BuildGroupKey(StreamEncryptionBarrier b)
            {
                var fileName = Path.GetFileName(b.FilePath.Replace('\\', '/'));
                var ext = Path.GetExtension(fileName);
                var extKey = string.IsNullOrWhiteSpace(ext) ? "(no-ext)" : ext.ToLowerInvariant();
                return ((b.ProviderId ?? string.Empty).ToLowerInvariant(), extKey);
            }

            // Pass 1: 1 representative per distinct (ProviderId, Extension) matching the active FileNamePattern
            if (!string.IsNullOrWhiteSpace(FileNamePattern))
            {
                foreach (var barrier in accessibleBarriers)
                {
                    if (selected.Count >= maxSamples)
                    {
                        break;
                    }

                    var fileName = Path.GetFileName(barrier.FilePath.Replace('\\', '/'));
                    if (!MatchesPatternList(fileName, FileNamePattern))
                    {
                        continue;
                    }

                    var groupKey = BuildGroupKey(barrier);
                    if (coveredGroups.Add(groupKey) && selectedPaths.Add(barrier.FilePath))
                    {
                        selected.Add(barrier);
                    }
                }
            }

            // Pass 2: 1 representative per remaining distinct (ProviderId, Extension) across all barrier types
            foreach (var barrier in accessibleBarriers)
            {
                if (selected.Count >= maxSamples)
                {
                    break;
                }

                var groupKey = BuildGroupKey(barrier);
                if (coveredGroups.Add(groupKey) && selectedPaths.Add(barrier.FilePath))
                {
                    selected.Add(barrier);
                }
            }

            // Pass 3: Fill remaining slots up to maxSamples
            foreach (var barrier in accessibleBarriers)
            {
                if (selected.Count >= maxSamples)
                {
                    break;
                }

                if (selectedPaths.Add(barrier.FilePath))
                {
                    selected.Add(barrier);
                }
            }

            return selected.ToArray();
        }

        private static StreamEncryptionBarrier[] OrderSampleBarriersForPattern(StreamEncryptionBarrier[] samples, string? patternList)
        {
            if (samples.Length <= 1 || string.IsNullOrWhiteSpace(patternList))
            {
                return samples;
            }

            var matching = new List<StreamEncryptionBarrier>(samples.Length);
            var others = new List<StreamEncryptionBarrier>(samples.Length);
            foreach (var barrier in samples)
            {
                var fileName = Path.GetFileName(barrier.FilePath.Replace('\\', '/'));
                if (MatchesPatternList(fileName, patternList!))
                {
                    matching.Add(barrier);
                }
                else
                {
                    others.Add(barrier);
                }
            }

            if (matching.Count == 0 || others.Count == 0)
            {
                return samples;
            }

            matching.AddRange(others);
            return matching.ToArray();
        }

        private static bool MatchesPatternList(string fileName, string patternList)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(patternList))
            {
                return false;
            }

            var parts = patternList.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var pattern = parts[i].Trim();
                if (pattern.Length > 0 && FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasMoreSpecificStructure(DetectedCipherCandidate candidate, DetectedCipherCandidate existing)
        {
            int Score(DetectedCipherCandidate c)
                => (c.SuggestedSkipBytes > 0 ? 1 : 0)
                   + (c.SuggestedRangeLength > 0 ? 2 : 0)
                   + (c.SuggestedRangeStart > 0 ? 1 : 0)
                   + (c.SuggestedIvFromPrefix ? 1 : 0);

            return Score(candidate) > Score(existing);
        }

        private static string DeriveFilePatternFromPaths(IEnumerable<string>? paths)
        {
            if (paths == null)
            {
                return string.Empty;
            }

            var patterns = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                var fileName = Path.GetFileName(rawPath.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var ext = Path.GetExtension(fileName);
                string pattern = !string.IsNullOrWhiteSpace(ext) ? "*" + ext : fileName;
                if (seen.Add(pattern))
                {
                    patterns.Add(pattern);
                }
            }

            return string.Join(";", patterns);
        }

        private static int SchemeIdToAlgorithmIndex(string? schemeId)
        {
            return (schemeId ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "aes-cbc" => 0,
                "aes-ctr" => 1,
                "aes-ecb" => 2,
                "chacha20" => 3,
                "xor" => 4,
                "none" => 5,
                "block512" or "block-512" or "block_512" or "chacha-512b-xor" or "custom-chacha-512b" => 6,
                "rijndael" or "rijndael-cbc" or "rijndaelcbc" or "rijndael_cbc" => 7,
                _ => -1
            };
        }

        private static string AlgorithmIndexToSchemeId(int algorithmIndex)
        {
            return algorithmIndex switch
            {
                0 => "aes-cbc",
                1 => "aes-ctr",
                2 => "aes-ecb",
                3 => "chacha20",
                4 => "xor",
                5 => "none",
                6 => "block512",
                7 => "rijndael-cbc",
                _ => "aes-cbc"
            };
        }
    }
}
