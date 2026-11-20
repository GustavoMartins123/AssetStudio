using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.ViewModels
{
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
        private readonly AnimationPreviewLoadCoordinator _animationCoordinator = new();

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
        private bool _isDialogueCardsVisible;
        private TextAssetDisplayMode _dialogueDisplayMode = TextAssetDisplayMode.Cards;
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

        public PreviewSessionViewModel(Action<Action>? dispatchToUi = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            DialogueCards = new BulkObservableCollection<TextAssetDialogueCardViewModel>();
            PreviewCandidates = new BulkObservableCollection<PreviewCandidateItem>();
        }

        internal AsyncPreviewRequestCoordinator PreviewCoordinator => _previewCoordinator;

        internal AsyncPreviewRequestCoordinator DumpCoordinator => _dumpCoordinator;

        internal AnimationPreviewLoadCoordinator AnimationCoordinator => _animationCoordinator;

        public long CurrentPreviewRequestId => _previewCoordinator.CurrentRequestId;

        public long CancelledPreviewRequestCount => _previewCoordinator.CancelledRequestCount;

        public long CurrentDumpRequestId => _dumpCoordinator.CurrentRequestId;

        public long CancelledDumpRequestCount => _dumpCoordinator.CancelledRequestCount;

        public long CurrentAnimationRequestId => _animationCoordinator.CurrentRequestId;

        public long CancelledAnimationRequestCount => _animationCoordinator.CancelledRequestCount;

        public long PreviewVersion => Interlocked.Read(ref _previewVersion);

        public BulkObservableCollection<TextAssetDialogueCardViewModel> DialogueCards { get; }

        public BulkObservableCollection<PreviewCandidateItem> PreviewCandidates { get; }

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
                or PreviewSurfaceKind.Shader
                or PreviewSurfaceKind.MonoBehaviour
                or PreviewSurfaceKind.AnimatorGraph
                or PreviewSurfaceKind.Prefab
            && !_isDialogueCardsVisible;

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
            set => SetProperty(ref _isPreviewEnabled, value);
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

        public bool IsDialogueCardsVisible
        {
            get => _isDialogueCardsVisible;
            set
            {
                if (SetProperty(ref _isDialogueCardsVisible, value))
                {
                    OnPropertyChanged(nameof(IsTextSurfaceVisible));
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

        public void CancelPending()
        {
            _previewCoordinator.CancelCurrent();
            _dumpCoordinator.CancelCurrent();
            _animationCoordinator.CancelCurrent();
            Interlocked.Increment(ref _previewVersion);
            _dispatchToUi(() =>
            {
                IsLoading = false;
                IsDumpLoading = false;
            });
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
                    _dialoguePlainTextScript = string.Empty;
                    _dialogueDetailsText = string.Empty;
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
            _previewCoordinator.Dispose();
            _dumpCoordinator.Dispose();
            _animationCoordinator.Dispose();
        }
    }
}
