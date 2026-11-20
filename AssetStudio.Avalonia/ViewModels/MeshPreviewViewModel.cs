using System;
using System.Collections.Generic;
using System.Globalization;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class MeshPreviewViewModel : ViewModelBase
    {
        public const int WireframeModeCount = 3;
        public const double MinBoneScale = 0.1;
        public const double MaxBoneScale = 5.0;
        public const double DefaultBoneScale = 1.0;
        public const double MinMeshDensityPercent = 1.0;
        public const double MaxMeshDensityPercent = 100.0;
        public const double DefaultMeshDensityPercent = 100.0;
        public const double MinPlaybackSpeed = 0.1;
        public const double MaxPlaybackSpeed = 4.0;
        public const double DefaultPlaybackSpeed = 1.0;
        public const double DefaultSampleRate = 30.0;
        public const string DefaultFormattedTimeText = "00:00.00 / 00:00.00";
        public const string DefaultFormattedFrameText = "Frame: 0/0";

        private readonly Action<Action> _dispatchToUi;
        private readonly object _stateLock = new();
        private readonly AnimationRigPreviewCache _rigCache = new();

        private int _wireframeMode;
        private int _shadeMode;
        private int _normalMode;
        private bool _showNormals;
        private bool _showAxes = true;
        private bool _showSkeleton = true;
        private bool _showMaterials = true;
        private bool _useTextures = true;
        private MeshPreviewViewPreset _viewPreset = MeshPreviewViewPreset.Auto;
        private double _boneScale = DefaultBoneScale;
        private double _meshDensityPercent = DefaultMeshDensityPercent;
        private bool _isGeometryControlsVisible;
        private bool _isBoneControlsVisible;
        private string _meshSummaryText = string.Empty;
        private string _associatedMaterialsSummary = string.Empty;
        private string _rigSummaryText = string.Empty;
        private bool _isAnimationLoaded;
        private bool _isPlaying;
        private bool _isLooping = true;
        private double _currentTimeSeconds;
        private double _durationSeconds;
        private double _playbackSpeed = DefaultPlaybackSpeed;
        private double _sampleRate = DefaultSampleRate;
        private int _currentFrame;
        private int _totalFrames;
        private string _formattedTimeText = DefaultFormattedTimeText;
        private string _formattedFrameText = DefaultFormattedFrameText;
        private string _activeClipName = string.Empty;
        private string _activeAvatarName = string.Empty;
        private string _activeMeshName = string.Empty;
        private PreviewCandidateItem? _selectedAnimation;
        private MeshMaterialPreviewSlot? _selectedMaterialSlot;

        public MeshPreviewViewModel(Action<Action>? dispatchToUi = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            AvailableAnimations = new BulkObservableCollection<PreviewCandidateItem>();
            MaterialSlots = new BulkObservableCollection<MeshMaterialPreviewSlot>();
        }

        internal AnimationRigPreviewCache RigCache => _rigCache;

        public BulkObservableCollection<PreviewCandidateItem> AvailableAnimations { get; }

        public BulkObservableCollection<MeshMaterialPreviewSlot> MaterialSlots { get; }

        public int WireframeMode
        {
            get => _wireframeMode;
            set
            {
                var normalized = NormalizeWireframeMode(value);
                if (SetProperty(ref _wireframeMode, normalized))
                {
                    OnPropertyChanged(nameof(IsWireframeEnabled));
                }
            }
        }

        public bool IsWireframeEnabled => _wireframeMode != 0;

        public int ShadeMode
        {
            get => _shadeMode;
            set
            {
                var normalized = value <= 0 ? 0 : 1;
                SetProperty(ref _shadeMode, normalized);
            }
        }

        public int NormalMode
        {
            get => _normalMode;
            set
            {
                var normalized = value <= 0 ? 0 : 1;
                if (SetProperty(ref _normalMode, normalized))
                {
                    SetProperty(ref _showNormals, normalized != 0, nameof(ShowNormals));
                }
            }
        }

        public bool ShowNormals
        {
            get => _showNormals;
            set
            {
                if (SetProperty(ref _showNormals, value))
                {
                    SetProperty(ref _normalMode, value ? 1 : 0, nameof(NormalMode));
                }
            }
        }

        public bool ShowAxes
        {
            get => _showAxes;
            set => SetProperty(ref _showAxes, value);
        }

        public bool ShowSkeleton
        {
            get => _showSkeleton;
            set => SetProperty(ref _showSkeleton, value);
        }

        public bool ShowMaterials
        {
            get => _showMaterials;
            set => SetProperty(ref _showMaterials, value);
        }

        public bool UseTextures
        {
            get => _useTextures;
            set => SetProperty(ref _useTextures, value);
        }

        public MeshPreviewViewPreset ViewPreset
        {
            get => _viewPreset;
            set
            {
                var safePreset = Enum.IsDefined(value) ? value : MeshPreviewViewPreset.Auto;
                SetProperty(ref _viewPreset, safePreset);
            }
        }

        public double BoneScale
        {
            get => _boneScale;
            set
            {
                var clamped = ClampBoneScale(value);
                if (SetProperty(ref _boneScale, clamped))
                {
                    OnPropertyChanged(nameof(FormattedBoneScaleText));
                }
            }
        }

        public string FormattedBoneScaleText =>
            string.Create(CultureInfo.InvariantCulture, $"{_boneScale:0.0}x");

        public double MeshDensityPercent
        {
            get => _meshDensityPercent;
            set
            {
                var clamped = ClampMeshDensityPercent(value);
                if (SetProperty(ref _meshDensityPercent, clamped))
                {
                    OnPropertyChanged(nameof(FormattedMeshDensityText));
                }
            }
        }

        public string FormattedMeshDensityText =>
            _meshDensityPercent >= MaxMeshDensityPercent
                ? "Original"
                : string.Create(CultureInfo.InvariantCulture, $"{_meshDensityPercent:0}%");

        public bool IsGeometryControlsVisible
        {
            get => _isGeometryControlsVisible;
            set => SetProperty(ref _isGeometryControlsVisible, value);
        }

        public bool IsBoneControlsVisible
        {
            get => _isBoneControlsVisible;
            set => SetProperty(ref _isBoneControlsVisible, value);
        }

        public string MeshSummaryText
        {
            get => _meshSummaryText;
            set => SetProperty(ref _meshSummaryText, value ?? string.Empty);
        }

        public string AssociatedMaterialsSummary
        {
            get => _associatedMaterialsSummary;
            set => SetProperty(ref _associatedMaterialsSummary, value ?? string.Empty);
        }

        public string RigSummaryText
        {
            get => _rigSummaryText;
            set => SetProperty(ref _rigSummaryText, value ?? string.Empty);
        }

        public bool IsAnimationLoaded
        {
            get => _isAnimationLoaded;
            set
            {
                if (SetProperty(ref _isAnimationLoaded, value))
                {
                    if (!value && _isPlaying)
                    {
                        SetProperty(ref _isPlaying, false, nameof(IsPlaying));
                        OnPropertyChanged(nameof(PlayPauseButtonText));
                    }

                    OnPropertyChanged(nameof(IsPaused));
                }
            }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                var effective = _isAnimationLoaded && value;
                if (SetProperty(ref _isPlaying, effective))
                {
                    OnPropertyChanged(nameof(IsPaused));
                    OnPropertyChanged(nameof(PlayPauseButtonText));
                }
            }
        }

        public bool IsPaused => _isAnimationLoaded && !_isPlaying;

        public string PlayPauseButtonText => _isPlaying ? "Pause" : "Play";

        public bool IsLooping
        {
            get => _isLooping;
            set => SetProperty(ref _isLooping, value);
        }

        public double CurrentTimeSeconds
        {
            get => _currentTimeSeconds;
            set => ApplyCurrentTimeCore(value, updateFrameFromTime: true);
        }

        public double DurationSeconds
        {
            get => _durationSeconds;
            set
            {
                var clampedDuration = SanitizeNonNegative(value);
                if (SetProperty(ref _durationSeconds, clampedDuration))
                {
                    if (_currentTimeSeconds > clampedDuration)
                    {
                        SetProperty(ref _currentTimeSeconds, clampedDuration, nameof(CurrentTimeSeconds));
                    }

                    UpdateFormattedTimeAndFrameCore(updateFrameFromTime: _totalFrames > 0);
                }
            }
        }

        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                var clamped = ClampPlaybackSpeed(value);
                SetProperty(ref _playbackSpeed, clamped);
            }
        }

        public double SampleRate
        {
            get => _sampleRate;
            set
            {
                var safeRate = double.IsFinite(value) && value > 0.0 ? value : DefaultSampleRate;
                if (SetProperty(ref _sampleRate, safeRate))
                {
                    UpdateFormattedTimeAndFrameCore(updateFrameFromTime: _totalFrames > 0);
                }
            }
        }

        public int CurrentFrame
        {
            get => _currentFrame;
            set
            {
                var maxFrame = Math.Max(0, _totalFrames);
                var clampedFrame = Math.Clamp(value, 0, maxFrame);
                if (SetProperty(ref _currentFrame, clampedFrame))
                {
                    FormattedFrameText = FormatFrameDisplay(_currentFrame, _totalFrames);
                }
            }
        }

        public int TotalFrames
        {
            get => _totalFrames;
            set
            {
                var safeTotal = Math.Max(0, value);
                if (SetProperty(ref _totalFrames, safeTotal))
                {
                    if (_currentFrame > safeTotal)
                    {
                        SetProperty(ref _currentFrame, safeTotal, nameof(CurrentFrame));
                    }

                    FormattedFrameText = FormatFrameDisplay(_currentFrame, _totalFrames);
                }
            }
        }

        public string FormattedTimeText
        {
            get => _formattedTimeText;
            private set => SetProperty(ref _formattedTimeText, value ?? DefaultFormattedTimeText);
        }

        public string FormattedFrameText
        {
            get => _formattedFrameText;
            private set => SetProperty(ref _formattedFrameText, value ?? DefaultFormattedFrameText);
        }

        public string ActiveClipName
        {
            get => _activeClipName;
            set => SetProperty(ref _activeClipName, value ?? string.Empty);
        }

        public string ActiveAvatarName
        {
            get => _activeAvatarName;
            set => SetProperty(ref _activeAvatarName, value ?? string.Empty);
        }

        public string ActiveMeshName
        {
            get => _activeMeshName;
            set => SetProperty(ref _activeMeshName, value ?? string.Empty);
        }

        public PreviewCandidateItem? SelectedAnimation
        {
            get => _selectedAnimation;
            set => SetProperty(ref _selectedAnimation, value);
        }

        public bool HasAvailableAnimations => AvailableAnimations.Count > 0;

        public MeshMaterialPreviewSlot? SelectedMaterialSlot
        {
            get => _selectedMaterialSlot;
            set => SetProperty(ref _selectedMaterialSlot, value);
        }

        public bool HasMaterialSlots => MaterialSlots.Count > 0;

        public static string FormatTimeComponent(double seconds)
        {
            var safeSeconds = SanitizeNonNegative(seconds);
            var totalCentiseconds = (long)Math.Round(safeSeconds * 100.0, MidpointRounding.AwayFromZero);
            var minutes = totalCentiseconds / 6000;
            var sec = (totalCentiseconds / 100) % 60;
            var centiseconds = totalCentiseconds % 100;
            return string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{sec:D2}.{centiseconds:D2}");
        }

        public static string FormatAnimationTime(double currentSeconds, double durationSeconds)
        {
            return $"{FormatTimeComponent(currentSeconds)} / {FormatTimeComponent(durationSeconds)}";
        }

        public static string FormatFrameDisplay(int currentFrame, int totalFrames)
        {
            var safeTotal = Math.Max(0, totalFrames);
            var safeCurrent = Math.Clamp(currentFrame, 0, safeTotal);
            return string.Create(CultureInfo.InvariantCulture, $"Frame: {safeCurrent}/{safeTotal}");
        }

        public int ToggleWireframe()
        {
            WireframeMode = (_wireframeMode + 1) % WireframeModeCount;
            return _wireframeMode;
        }

        public int ToggleShadeMode()
        {
            ShadeMode = _shadeMode == 0 ? 1 : 0;
            UseTextures = _shadeMode == 0;
            return _shadeMode;
        }

        public bool ToggleNormals()
        {
            ShowNormals = !_showNormals;
            return _showNormals;
        }

        public bool ToggleAxes()
        {
            ShowAxes = !_showAxes;
            return _showAxes;
        }

        public bool ToggleSkeleton()
        {
            ShowSkeleton = !_showSkeleton;
            return _showSkeleton;
        }

        public bool ToggleMaterials()
        {
            ShowMaterials = !_showMaterials;
            return _showMaterials;
        }

        public bool ToggleUseTextures()
        {
            UseTextures = !_useTextures;
            ShadeMode = _useTextures ? 0 : 1;
            return _useTextures;
        }

        public bool TogglePlayPause()
        {
            if (!_isAnimationLoaded)
            {
                return false;
            }

            if (_isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }

            return _isPlaying;
        }

        public bool Play()
        {
            if (!_isAnimationLoaded)
            {
                return false;
            }

            if (!_isLooping && _durationSeconds > 0.0 && _currentTimeSeconds >= _durationSeconds)
            {
                ApplyCurrentTimeCore(0.0, updateFrameFromTime: true);
            }

            IsPlaying = true;
            return true;
        }

        public void Pause()
        {
            IsPlaying = false;
        }

        public void Restart()
        {
            if (!_isAnimationLoaded)
            {
                return;
            }

            ApplyCurrentTimeCore(0.0, updateFrameFromTime: true);
            IsPlaying = true;
        }

        public double Seek(double seconds)
        {
            double target;
            if (double.IsNaN(seconds) || double.IsNegativeInfinity(seconds))
            {
                target = 0.0;
            }
            else if (double.IsPositiveInfinity(seconds))
            {
                target = _durationSeconds;
            }
            else
            {
                target = Math.Clamp(seconds, 0.0, _durationSeconds);
            }

            ApplyCurrentTimeCore(target, updateFrameFromTime: true);
            return _currentTimeSeconds;
        }

        public void SeekFrame(int currentFrame, int? totalFrames = null)
        {
            _dispatchToUi(() =>
            {
                if (totalFrames.HasValue)
                {
                    var safeTotal = Math.Max(0, totalFrames.Value);
                    SetProperty(ref _totalFrames, safeTotal, nameof(TotalFrames));
                    if (_sampleRate > 0.0 && safeTotal > 0 && _durationSeconds <= 0.0)
                    {
                        SetProperty(ref _durationSeconds, safeTotal / _sampleRate, nameof(DurationSeconds));
                    }
                }

                var maxFrame = Math.Max(0, _totalFrames);
                var clampedFrame = Math.Clamp(currentFrame, 0, maxFrame);
                SetProperty(ref _currentFrame, clampedFrame, nameof(CurrentFrame));

                if (_sampleRate > 0.0)
                {
                    var timeFromFrame = _durationSeconds > 0.0
                        ? Math.Clamp(clampedFrame / _sampleRate, 0.0, _durationSeconds)
                        : clampedFrame / _sampleRate;
                    SetProperty(ref _currentTimeSeconds, timeFromFrame, nameof(CurrentTimeSeconds));
                }

                FormattedFrameText = FormatFrameDisplay(_currentFrame, _totalFrames);
                FormattedTimeText = FormatAnimationTime(_currentTimeSeconds, _durationSeconds);
            });
        }

        public double AdvanceTime(double deltaSeconds)
        {
            if (!_isAnimationLoaded || !_isPlaying || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            {
                return _currentTimeSeconds;
            }

            if (_durationSeconds <= 0.0)
            {
                ApplyCurrentTimeCore(0.0, updateFrameFromTime: true);
                return _currentTimeSeconds;
            }

            var scaledDelta = deltaSeconds * _playbackSpeed;
            var nextTime = _currentTimeSeconds + scaledDelta;

            if (_isLooping)
            {
                nextTime %= _durationSeconds;
                if (nextTime < 0.0)
                {
                    nextTime += _durationSeconds;
                }

                ApplyCurrentTimeCore(nextTime, updateFrameFromTime: true);
            }
            else
            {
                if (nextTime >= _durationSeconds)
                {
                    ApplyCurrentTimeCore(_durationSeconds, updateFrameFromTime: true);
                    IsPlaying = false;
                }
                else
                {
                    ApplyCurrentTimeCore(nextTime, updateFrameFromTime: true);
                }
            }

            return _currentTimeSeconds;
        }

        public double SetPlaybackSpeed(double speed)
        {
            PlaybackSpeed = speed;
            return _playbackSpeed;
        }

        public void SetAnimationState(
            bool isLoaded,
            string? clipName = null,
            string? avatarName = null,
            double durationSeconds = 0.0,
            double currentTimeSeconds = 0.0,
            bool isPlaying = false,
            bool? isLooping = null,
            double? playbackSpeed = null,
            int totalFrames = 0,
            double sampleRate = DefaultSampleRate,
            IReadOnlyList<PreviewCandidateItem>? availableAnimations = null,
            PreviewCandidateItem? selectedAnimation = null)
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    var safeRate = double.IsFinite(sampleRate) && sampleRate > 0.0 ? sampleRate : DefaultSampleRate;
                    SetProperty(ref _sampleRate, safeRate, nameof(SampleRate));

                    if (!isLoaded)
                    {
                        IsAnimationLoaded = false;
                        IsPlaying = false;
                        SetProperty(ref _durationSeconds, 0.0, nameof(DurationSeconds));
                        SetProperty(ref _currentTimeSeconds, 0.0, nameof(CurrentTimeSeconds));
                        SetProperty(ref _totalFrames, 0, nameof(TotalFrames));
                        SetProperty(ref _currentFrame, 0, nameof(CurrentFrame));
                        ActiveClipName = string.Empty;
                        ActiveAvatarName = string.Empty;
                        FormattedTimeText = DefaultFormattedTimeText;
                        FormattedFrameText = DefaultFormattedFrameText;
                        if (availableAnimations != null)
                        {
                            SetAvailableAnimationsCore(availableAnimations, selectedAnimation);
                        }
                        return;
                    }

                    var safeFrames = Math.Max(0, totalFrames);
                    var safeDuration = SanitizeNonNegative(durationSeconds);
                    if (safeDuration <= 0.0 && safeFrames > 0)
                    {
                        safeDuration = safeFrames / safeRate;
                    }
                    else if (safeFrames == 0 && safeDuration > 0.0)
                    {
                        safeFrames = Math.Max(1, (int)Math.Round(safeDuration * safeRate, MidpointRounding.AwayFromZero));
                    }

                    if (isLooping.HasValue)
                    {
                        IsLooping = isLooping.Value;
                    }

                    if (playbackSpeed.HasValue)
                    {
                        PlaybackSpeed = playbackSpeed.Value;
                    }

                    ActiveClipName = clipName ?? string.Empty;
                    if (avatarName != null)
                    {
                        ActiveAvatarName = avatarName;
                    }

                    SetProperty(ref _totalFrames, safeFrames, nameof(TotalFrames));
                    SetProperty(ref _durationSeconds, safeDuration, nameof(DurationSeconds));
                    IsAnimationLoaded = true;
                    ApplyCurrentTimeCore(currentTimeSeconds, updateFrameFromTime: true);
                    IsPlaying = isPlaying;

                    if (availableAnimations != null)
                    {
                        SetAvailableAnimationsCore(availableAnimations, selectedAnimation);
                    }
                    else if (selectedAnimation != null)
                    {
                        SelectedAnimation = selectedAnimation;
                    }
                }
            });
        }

        public void SetAvailableAnimations(
            IReadOnlyList<PreviewCandidateItem>? animations,
            PreviewCandidateItem? selectedAnimation = null)
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    SetAvailableAnimationsCore(animations, selectedAnimation);
                }
            });
        }

        private void SetAvailableAnimationsCore(
            IReadOnlyList<PreviewCandidateItem>? animations,
            PreviewCandidateItem? selectedAnimation)
        {
            var prevHas = HasAvailableAnimations;
            var items = animations ?? Array.Empty<PreviewCandidateItem>();
            if (items.Count == 0)
            {
                SelectedAnimation = null;
                if (AvailableAnimations.Count > 0)
                {
                    AvailableAnimations.Clear();
                }
            }
            else
            {
                AvailableAnimations.ReplaceAll(items);
                PreviewCandidateItem? effective = null;
                if (selectedAnimation != null)
                {
                    for (var i = 0; i < AvailableAnimations.Count; i++)
                    {
                        if (ReferenceEquals(AvailableAnimations[i], selectedAnimation))
                        {
                            effective = AvailableAnimations[i];
                            break;
                        }
                    }
                }

                effective ??= AvailableAnimations[0];
                SelectedAnimation = effective;
            }

            if (HasAvailableAnimations != prevHas)
            {
                OnPropertyChanged(nameof(HasAvailableAnimations));
            }
        }

        public void SetMeshAndRigSummary(
            string? meshSummary,
            string? associatedMaterialsSummary = null,
            string? rigSummary = null,
            string? meshName = null,
            string? avatarName = null)
        {
            _dispatchToUi(() =>
            {
                if (meshSummary != null)
                {
                    MeshSummaryText = meshSummary;
                }

                if (associatedMaterialsSummary != null)
                {
                    AssociatedMaterialsSummary = associatedMaterialsSummary;
                }

                if (rigSummary != null)
                {
                    RigSummaryText = rigSummary;
                }

                if (meshName != null)
                {
                    ActiveMeshName = meshName;
                }

                if (avatarName != null)
                {
                    ActiveAvatarName = avatarName;
                }
            });
        }

        public void SetMaterialSlots(IReadOnlyList<MeshMaterialPreviewSlot>? slots, int selectedIndex = 0)
        {
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    var prevHas = HasMaterialSlots;
                    var items = slots ?? Array.Empty<MeshMaterialPreviewSlot>();
                    if (items.Count == 0)
                    {
                        SelectedMaterialSlot = null;
                        if (MaterialSlots.Count > 0)
                        {
                            MaterialSlots.Clear();
                        }
                    }
                    else
                    {
                        MaterialSlots.ReplaceAll(items);
                        var idx = Math.Clamp(selectedIndex, 0, MaterialSlots.Count - 1);
                        SelectedMaterialSlot = MaterialSlots[idx];
                    }

                    if (HasMaterialSlots != prevHas)
                    {
                        OnPropertyChanged(nameof(HasMaterialSlots));
                    }
                }
            });
        }

        public void ClearMaterialSlots()
        {
            SetMaterialSlots(null);
        }

        public void Reset()
        {
            _rigCache.Clear();
            _dispatchToUi(() =>
            {
                lock (_stateLock)
                {
                    WireframeMode = 0;
                    ShadeMode = 0;
                    ShowNormals = false;
                    ShowAxes = true;
                    ShowSkeleton = true;
                    ShowMaterials = true;
                    UseTextures = true;
                    ViewPreset = MeshPreviewViewPreset.Auto;
                    BoneScale = DefaultBoneScale;
                    MeshDensityPercent = DefaultMeshDensityPercent;
                    IsGeometryControlsVisible = false;
                    IsBoneControlsVisible = false;
                    MeshSummaryText = string.Empty;
                    AssociatedMaterialsSummary = string.Empty;
                    RigSummaryText = string.Empty;
                    IsPlaying = false;
                    IsAnimationLoaded = false;
                    IsLooping = true;
                    SetProperty(ref _durationSeconds, 0.0, nameof(DurationSeconds));
                    SetProperty(ref _currentTimeSeconds, 0.0, nameof(CurrentTimeSeconds));
                    PlaybackSpeed = DefaultPlaybackSpeed;
                    SetProperty(ref _sampleRate, DefaultSampleRate, nameof(SampleRate));
                    SetProperty(ref _totalFrames, 0, nameof(TotalFrames));
                    SetProperty(ref _currentFrame, 0, nameof(CurrentFrame));
                    FormattedTimeText = DefaultFormattedTimeText;
                    FormattedFrameText = DefaultFormattedFrameText;
                    ActiveClipName = string.Empty;
                    ActiveAvatarName = string.Empty;
                    ActiveMeshName = string.Empty;
                    SetAvailableAnimationsCore(null, null);
                    SetMaterialSlots(null);
                }
            });
        }

        private void ApplyCurrentTimeCore(double rawSeconds, bool updateFrameFromTime)
        {
            var clamped = Math.Clamp(SanitizeNonNegative(rawSeconds), 0.0, _durationSeconds);
            SetProperty(ref _currentTimeSeconds, clamped, nameof(CurrentTimeSeconds));
            UpdateFormattedTimeAndFrameCore(updateFrameFromTime);
        }

        private void UpdateFormattedTimeAndFrameCore(bool updateFrameFromTime)
        {
            FormattedTimeText = FormatAnimationTime(_currentTimeSeconds, _durationSeconds);
            if (updateFrameFromTime && _totalFrames > 0 && _sampleRate > 0.0)
            {
                var computedFrame = (int)Math.Round(_currentTimeSeconds * _sampleRate, MidpointRounding.AwayFromZero);
                var clampedFrame = Math.Clamp(computedFrame, 0, _totalFrames);
                SetProperty(ref _currentFrame, clampedFrame, nameof(CurrentFrame));
            }

            FormattedFrameText = FormatFrameDisplay(_currentFrame, _totalFrames);
        }

        private static int NormalizeWireframeMode(int mode)
        {
            var mod = mode % WireframeModeCount;
            return mod < 0 ? mod + WireframeModeCount : mod;
        }

        private static double ClampBoneScale(double scale)
        {
            if (!double.IsFinite(scale))
            {
                return DefaultBoneScale;
            }

            return Math.Clamp(scale, MinBoneScale, MaxBoneScale);
        }

        private static double ClampMeshDensityPercent(double density)
        {
            if (!double.IsFinite(density))
            {
                return DefaultMeshDensityPercent;
            }

            return Math.Clamp(density, MinMeshDensityPercent, MaxMeshDensityPercent);
        }

        private static double ClampPlaybackSpeed(double speed)
        {
            if (double.IsNaN(speed))
            {
                return DefaultPlaybackSpeed;
            }

            if (double.IsNegativeInfinity(speed))
            {
                return MinPlaybackSpeed;
            }

            if (double.IsPositiveInfinity(speed))
            {
                return MaxPlaybackSpeed;
            }

            return Math.Clamp(speed, MinPlaybackSpeed, MaxPlaybackSpeed);
        }

        private static double SanitizeNonNegative(double value)
        {
            if (!double.IsFinite(value) || value < 0.0)
            {
                return 0.0;
            }

            return value;
        }
    }
}
