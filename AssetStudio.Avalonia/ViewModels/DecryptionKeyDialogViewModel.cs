using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;

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
        IReadOnlyList<StreamEncryptionBarrier> DetectedBarriers);

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

    public sealed class DecryptionProfileListItemViewModel : ViewModelBase
    {
        public DecryptionProfileListItemViewModel(DecryptionProfile profile)
        {
            Profile = profile.Clone();
        }

        public DecryptionProfile Profile { get; }
        public string Id => Profile.Id;
        public string AlgorithmLabel => string.IsNullOrWhiteSpace(Profile.Algorithm)
            ? (!string.IsNullOrWhiteSpace(Profile.XorKeyHex) ? "xor" : "none")
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
        private string _validationError = string.Empty;
        private string _statusMessage = string.Empty;
        private string _pluginsSummaryText = string.Empty;

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
                "Offset Skip (None)"
            };
            KeyFormatOptions = new[] { "Hex", "UTF-8 Text" };
            IvModeOptions = new[] { "Hex", "UTF-8 Text", "Read from Stream Prefix", "None (All Zeros)" };
            PaddingOptions = new[] { "None", "PKCS7" };
            KdfOptions = new[] { "None", "SHA-256", "MD5", "PBKDF2-SHA256", "PBKDF2-SHA1" };
            SaltFormatOptions = new[] { "UTF-8 Text", "Hex" };
            DerivedKeyLengthOptions = new[] { "Auto (32 bytes)", "16 bytes (128-bit)", "24 bytes (192-bit)", "32 bytes (256-bit)" };

            Profiles = new ObservableCollection<DecryptionProfileListItemViewModel>();
            BarrierSamples = new ObservableCollection<DecryptionBarrierSampleItem>();

            if (_context.DetectedBarriers != null && _context.DetectedBarriers.Count > 0)
            {
                foreach (var barrier in _context.DetectedBarriers.Take(6))
                {
                    BarrierSamples.Add(new DecryptionBarrierSampleItem(barrier));
                }

                var detectedPatterns = _context.DetectedBarriers
                    .Select(b => Path.GetExtension(b.FilePath))
                    .Where(ext => !string.IsNullOrWhiteSpace(ext))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(ext => "*" + ext)
                    .ToArray();
                if (detectedPatterns.Length > 0)
                {
                    _fileNamePattern = string.Join(";", detectedPatterns);
                }
            }

            NewProfileCommand = new RelayCommand(ResetEditorForm);
            LoadSelectedProfileCommand = new RelayCommand(LoadSelectedProfileIntoEditor, () => SelectedProfile != null);
            SaveProfileCommand = new RelayCommand(ExecuteSaveProfile);
            DeleteSelectedProfileCommand = new RelayCommand(ExecuteDeleteSelectedProfile, () => SelectedProfile != null);
            ImportJsonCommand = new AsyncRelayCommand(ExecuteImportJsonAsync);
            OpenProfilesFolderCommand = new AsyncRelayCommand(ExecuteOpenProfilesFolderAsync);
            OpenPluginsFolderCommand = new AsyncRelayCommand(ExecuteOpenPluginsFolderAsync);
            ReloadPluginsCommand = new RelayCommand(ExecuteReloadPlugins);
            ApplyAndReanalyzeCommand = new RelayCommand(ExecuteApplyAndReanalyze);
            SecondaryCloseCommand = new RelayCommand(ExecuteSecondaryClose);

            RefreshProfilesAndPluginsList();
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

        public bool IsInitialLoadPrompt => _context.IsInitialLoadPrompt;
        public bool HasEncryptedFiles => _context.EncryptedFileCount > 0;
        public bool HasBarrierSamples => BarrierSamples.Count > 0;
        public bool HasProfiles => Profiles.Count > 0;

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
                    OnPropertyChanged(nameof(IsKeySectionVisible));
                    OnPropertyChanged(nameof(IsIvSectionVisible));
                    OnPropertyChanged(nameof(IsIvTextInputEnabled));
                    OnPropertyChanged(nameof(IsPaddingVisible));
                    OnPropertyChanged(nameof(IsCtrOptionsVisible));
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

        public bool IsKeySectionVisible => SelectedAlgorithmIndex != 5;
        public bool IsIvSectionVisible => SelectedAlgorithmIndex is 0 or 1 or 3;
        public bool IsIvTextInputEnabled => IsIvSectionVisible && SelectedIvModeIndex is 0 or 1;
        public bool IsPaddingVisible => SelectedAlgorithmIndex is 0 or 2;
        public bool IsCtrOptionsVisible => SelectedAlgorithmIndex is 1 or 3;
        public bool IsKdfSaltVisible => SelectedKdfIndex is 3 or 4;
        public bool IsKdfLengthVisible => SelectedKdfIndex > 0;

        public RelayCommand NewProfileCommand { get; }
        public RelayCommand LoadSelectedProfileCommand { get; }
        public RelayCommand SaveProfileCommand { get; }
        public RelayCommand DeleteSelectedProfileCommand { get; }
        public AsyncRelayCommand ImportJsonCommand { get; }
        public AsyncRelayCommand OpenProfilesFolderCommand { get; }
        public AsyncRelayCommand OpenPluginsFolderCommand { get; }
        public RelayCommand ReloadPluginsCommand { get; }
        public RelayCommand ApplyAndReanalyzeCommand { get; }
        public RelayCommand SecondaryCloseCommand { get; }

        public bool HasPendingEditorInput()
        {
            if (SelectedAlgorithmIndex == 5)
            {
                return long.TryParse(SkipBytesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skip) && skip > 0;
            }

            return !string.IsNullOrWhiteSpace(KeyInput);
        }

        public bool TryBuildProfileFromEditor(out DecryptionProfile? profile, out string? error)
        {
            profile = null;
            error = null;

            string algoName = SelectedAlgorithmIndex switch
            {
                0 => "aes-cbc",
                1 => "aes-ctr",
                2 => "aes-ecb",
                3 => "chacha20",
                4 => "xor",
                5 => "none",
                _ => "aes-cbc"
            };

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
                Padding = IsPaddingVisible && SelectedPaddingIndex == 1 ? "pkcs7" : "none",
                CounterStart = counterStart,
                CounterLittleEndian = CounterLittleEndian
            };

            if (IsKeySectionVisible)
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
                    if (IsKdfSaltVisible && !string.IsNullOrEmpty(SaltInput))
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

            if (IsIvSectionVisible)
            {
                switch (SelectedIvModeIndex)
                {
                    case 0:
                        if (!string.IsNullOrWhiteSpace(IvInput))
                        {
                            if (SelectedAlgorithmIndex == 3)
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
                _ => 0
            };

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
    }
}
