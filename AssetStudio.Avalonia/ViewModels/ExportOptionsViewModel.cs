using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using AssetStudio;
using AssetStudio.Avalonia;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class ExportOptionsViewModel : ViewModelBase
    {
        private static readonly string[] AssetGroupingChoices =
        {
            "Type name",
            "Container path",
            "Source file",
            "Do not group"
        };

        private static readonly string[] TextureFormatChoices = Enum.GetNames<ImageFormat>();

        private static readonly string[] ModelFormatChoices =
        {
            "FBX (.fbx)",
            "glTF Binary (.glb)",
            "glTF Separate (.gltf)"
        };

        private static readonly string[] FbxVersionChoices =
        {
            "FBX 6.1",
            "FBX 7.1",
            "FBX 7.2",
            "FBX 7.3",
            "FBX 7.4",
            "FBX 7.5"
        };

        private static readonly string[] FbxFormatChoices =
        {
            "Binary",
            "ASCII"
        };

        private readonly ExportOptionsState _targetState;
        private readonly RelayCommand _saveCommand;
        private readonly RelayCommand _cancelCommand;
        private readonly RelayCommand _resetDefaultsCommand;

        private AssetGroupOption _assetGrouping;
        private bool _convertTexture;
        private ImageFormat _convertTextureFormat;
        private bool _convertAudio;
        private bool _restoreExtensionName;
        private bool _openAfterExport;
        private bool _exportSkins;
        private bool _exportAnimations;
        private bool _exportAnimationsSplit;
        private bool _exportBlendShape;
        private bool _castToBone;
        private bool _combineMeshes;
        private decimal _boneSize;
        private string _boneSizeText = string.Empty;
        private decimal _scaleFactor;
        private string _scaleFactorText = string.Empty;
        private ModelExportFormat _modelFormat;
        private int _fbxVersion;
        private int _fbxFormat;
        private bool _exportUnityMeta;
        private bool _exportUnityAnimClips;
        private string _validationMessage = string.Empty;
        private bool _canSave = true;

        public ExportOptionsViewModel()
            : this(new ExportOptionsState())
        {
        }

        public ExportOptionsViewModel(AvaloniaAppSettings settings)
            : this((settings ?? throw new ArgumentNullException(nameof(settings))).ExportOptions ??= new ExportOptionsState())
        {
        }

        public ExportOptionsViewModel(ExportOptionsState state)
        {
            _targetState = state ?? throw new ArgumentNullException(nameof(state));
            _saveCommand = new RelayCommand(Save, () => CanSave);
            _cancelCommand = new RelayCommand(Cancel);
            _resetDefaultsCommand = new RelayCommand(ResetDefaults);

            LoadFrom(_targetState);
        }

        public event Action<bool>? RequestClose;

        public ExportOptionsState TargetState => _targetState;

        public ExportOptionsState? Result { get; private set; }

        public IReadOnlyList<string> AssetGroupingOptions => AssetGroupingChoices;

        public IReadOnlyList<string> TextureFormatOptions => TextureFormatChoices;

        public IReadOnlyList<string> ModelFormatOptions => ModelFormatChoices;

        public IReadOnlyList<string> FbxVersionOptions => FbxVersionChoices;

        public IReadOnlyList<string> FbxFormatOptions => FbxFormatChoices;

        public AssetGroupOption AssetGrouping
        {
            get => _assetGrouping;
            set
            {
                var clamped = (AssetGroupOption)Math.Clamp((int)value, 0, AssetGroupingChoices.Length - 1);
                if (SetProperty(ref _assetGrouping, clamped))
                {
                    OnPropertyChanged(nameof(AssetGroupingIndex));
                }
            }
        }

        public int AssetGroupingIndex
        {
            get => Math.Clamp((int)_assetGrouping, 0, AssetGroupingChoices.Length - 1);
            set => AssetGrouping = (AssetGroupOption)Math.Clamp(value, 0, AssetGroupingChoices.Length - 1);
        }

        public bool ConvertTexture
        {
            get => _convertTexture;
            set => SetProperty(ref _convertTexture, value);
        }

        public ImageFormat ConvertTextureFormat
        {
            get => _convertTextureFormat;
            set
            {
                if (SetProperty(ref _convertTextureFormat, value))
                {
                    OnPropertyChanged(nameof(TextureFormatIndex));
                    OnPropertyChanged(nameof(SelectedTextureFormatName));
                }
            }
        }

        public int TextureFormatIndex
        {
            get
            {
                var index = Array.IndexOf(TextureFormatChoices, _convertTextureFormat.ToString());
                return index >= 0 ? index : 0;
            }
            set
            {
                if (value < 0 || value >= TextureFormatChoices.Length)
                {
                    return;
                }

                if (Enum.TryParse<ImageFormat>(TextureFormatChoices[value], out var parsed))
                {
                    ConvertTextureFormat = parsed;
                }
            }
        }

        public string SelectedTextureFormatName
        {
            get => _convertTextureFormat.ToString();
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse<ImageFormat>(value, out var parsed))
                {
                    ConvertTextureFormat = parsed;
                }
            }
        }

        public bool ConvertAudio
        {
            get => _convertAudio;
            set => SetProperty(ref _convertAudio, value);
        }

        public bool RestoreExtensionName
        {
            get => _restoreExtensionName;
            set => SetProperty(ref _restoreExtensionName, value);
        }

        public bool OpenAfterExport
        {
            get => _openAfterExport;
            set => SetProperty(ref _openAfterExport, value);
        }

        public bool ExportSkins
        {
            get => _exportSkins;
            set => SetProperty(ref _exportSkins, value);
        }

        public bool ExportAnimations
        {
            get => _exportAnimations;
            set => SetProperty(ref _exportAnimations, value);
        }

        public bool ExportAnimationsSplit
        {
            get => _exportAnimationsSplit;
            set => SetProperty(ref _exportAnimationsSplit, value);
        }

        public bool ExportBlendShape
        {
            get => _exportBlendShape;
            set => SetProperty(ref _exportBlendShape, value);
        }

        public bool CastToBone
        {
            get => _castToBone;
            set => SetProperty(ref _castToBone, value);
        }

        public bool CombineMeshes
        {
            get => _combineMeshes;
            set => SetProperty(ref _combineMeshes, value);
        }

        public decimal BoneSize
        {
            get => _boneSize;
            set
            {
                if (SetProperty(ref _boneSize, value))
                {
                    var formatted = value.ToString(CultureInfo.InvariantCulture);
                    if (!string.Equals(_boneSizeText, formatted, StringComparison.Ordinal))
                    {
                        _boneSizeText = formatted;
                        OnPropertyChanged(nameof(BoneSizeText));
                    }
                    Validate();
                }
            }
        }

        public string BoneSizeText
        {
            get => _boneSizeText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _boneSizeText, normalized))
                {
                    if (TryParseNonNegativeDecimal(normalized, out var parsed))
                    {
                        if (_boneSize != parsed)
                        {
                            _boneSize = parsed;
                            OnPropertyChanged(nameof(BoneSize));
                        }
                    }

                    Validate();
                }
            }
        }

        public decimal ScaleFactor
        {
            get => _scaleFactor;
            set
            {
                if (SetProperty(ref _scaleFactor, value))
                {
                    var formatted = value.ToString(CultureInfo.InvariantCulture);
                    if (!string.Equals(_scaleFactorText, formatted, StringComparison.Ordinal))
                    {
                        _scaleFactorText = formatted;
                        OnPropertyChanged(nameof(ScaleFactorText));
                    }
                    Validate();
                }
            }
        }

        public string ScaleFactorText
        {
            get => _scaleFactorText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _scaleFactorText, normalized))
                {
                    if (TryParsePositiveDecimal(normalized, out var parsed))
                    {
                        if (_scaleFactor != parsed)
                        {
                            _scaleFactor = parsed;
                            OnPropertyChanged(nameof(ScaleFactor));
                        }
                    }

                    Validate();
                }
            }
        }

        public ModelExportFormat ModelFormat
        {
            get => _modelFormat;
            set
            {
                var clamped = (ModelExportFormat)Math.Clamp((int)value, 0, ModelFormatChoices.Length - 1);
                if (SetProperty(ref _modelFormat, clamped))
                {
                    OnPropertyChanged(nameof(ModelFormatIndex));
                    OnPropertyChanged(nameof(IsFbxFormatSelected));
                }
            }
        }

        public int ModelFormatIndex
        {
            get => Math.Clamp((int)_modelFormat, 0, ModelFormatChoices.Length - 1);
            set => ModelFormat = (ModelExportFormat)Math.Clamp(value, 0, ModelFormatChoices.Length - 1);
        }

        public bool IsFbxFormatSelected => _modelFormat == ModelExportFormat.Fbx;

        public int FbxVersion
        {
            get => _fbxVersion;
            set => SetProperty(ref _fbxVersion, Math.Clamp(value, 0, FbxVersionChoices.Length - 1));
        }

        public int FbxFormat
        {
            get => _fbxFormat;
            set => SetProperty(ref _fbxFormat, Math.Clamp(value, 0, FbxFormatChoices.Length - 1));
        }

        public bool ExportUnityMeta
        {
            get => _exportUnityMeta;
            set => SetProperty(ref _exportUnityMeta, value);
        }

        public bool ExportUnityAnimClips
        {
            get => _exportUnityAnimClips;
            set => SetProperty(ref _exportUnityAnimClips, value);
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            private set
            {
                if (SetProperty(ref _validationMessage, value))
                {
                    OnPropertyChanged(nameof(HasValidationMessage));
                }
            }
        }

        public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

        public bool CanSave
        {
            get => _canSave;
            private set
            {
                if (SetProperty(ref _canSave, value))
                {
                    _saveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public ICommand SaveCommand => _saveCommand;

        public ICommand CancelCommand => _cancelCommand;

        public ICommand ResetDefaultsCommand => _resetDefaultsCommand;

        public void LoadFrom(ExportOptionsState source)
        {
            ArgumentNullException.ThrowIfNull(source);

            AssetGrouping = source.AssetGrouping;
            ConvertTexture = source.ConvertTexture;
            ConvertTextureFormat = source.ConvertTextureFormat;
            ConvertAudio = source.ConvertAudio;
            RestoreExtensionName = source.RestoreExtensionName;
            OpenAfterExport = source.OpenAfterExport;
            ExportSkins = source.ExportSkins;
            ExportAnimations = source.ExportAnimations;
            ExportAnimationsSplit = source.ExportAnimationsSplit;
            ExportBlendShape = source.ExportBlendShape;
            CastToBone = source.CastToBone;
            CombineMeshes = source.CombineMeshes;
            _boneSize = source.BoneSize;
            _boneSizeText = source.BoneSize.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(BoneSize));
            OnPropertyChanged(nameof(BoneSizeText));
            _scaleFactor = source.ScaleFactor;
            _scaleFactorText = source.ScaleFactor.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(ScaleFactor));
            OnPropertyChanged(nameof(ScaleFactorText));
            ModelFormat = source.ModelFormat;
            FbxVersion = source.FbxVersion;
            FbxFormat = source.FbxFormat;
            ExportUnityMeta = source.ExportUnityMeta;
            ExportUnityAnimClips = source.ExportUnityAnimClips;

            Validate();
        }

        public void ApplyTo(ExportOptionsState destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            destination.AssetGrouping = AssetGrouping;
            destination.ConvertTexture = ConvertTexture;
            destination.ConvertTextureFormat = ConvertTextureFormat;
            destination.ConvertAudio = ConvertAudio;
            destination.RestoreExtensionName = RestoreExtensionName;
            destination.OpenAfterExport = OpenAfterExport;
            destination.ExportSkins = ExportSkins;
            destination.ExportAnimations = ExportAnimations;
            destination.ExportAnimationsSplit = ExportAnimationsSplit;
            destination.ExportBlendShape = ExportBlendShape;
            destination.CastToBone = CastToBone;
            destination.CombineMeshes = CombineMeshes;
            destination.BoneSize = TryParseNonNegativeDecimal(BoneSizeText, out var bone) ? bone : BoneSize;
            destination.ScaleFactor = TryParsePositiveDecimal(ScaleFactorText, out var scale) ? scale : ScaleFactor;
            destination.ModelFormat = ModelFormat;
            destination.FbxVersion = Math.Clamp(FbxVersion, 0, FbxVersionChoices.Length - 1);
            destination.FbxFormat = Math.Clamp(FbxFormat, 0, FbxFormatChoices.Length - 1);
            destination.ExportUnityMeta = ExportUnityMeta;
            destination.ExportUnityAnimClips = ExportUnityAnimClips;
        }

        public void ResetDefaults()
        {
            LoadFrom(new ExportOptionsState());
        }

        private void Save()
        {
            Validate();
            if (!CanSave)
            {
                return;
            }

            ApplyTo(_targetState);
            Result = _targetState;
            RequestClose?.Invoke(true);
        }

        private void Cancel()
        {
            Result = null;
            RequestClose?.Invoke(false);
        }

        private void Validate()
        {
            if (!TryParseNonNegativeDecimal(_boneSizeText, out _))
            {
                ValidationMessage = "Bone size must be a valid non-negative number.";
                CanSave = false;
                return;
            }

            if (!TryParsePositiveDecimal(_scaleFactorText, out _))
            {
                ValidationMessage = "Scale factor must be a valid positive number.";
                CanSave = false;
                return;
            }

            ValidationMessage = string.Empty;
            CanSave = true;
        }

        private static bool TryParseNonNegativeDecimal(string? text, out decimal value)
        {
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0m)
            {
                return true;
            }

            value = 0m;
            return false;
        }

        private static bool TryParsePositiveDecimal(string? text, out decimal value)
        {
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0m)
            {
                return true;
            }

            value = 0m;
            return false;
        }
    }
}
