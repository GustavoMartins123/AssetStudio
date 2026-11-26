using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia.ViewModels
{
    /// <summary>
    /// Pure MVVM view model for inspecting Texture2D mipmap chains and triggering
    /// mipmap / HDR (OpenEXR, 32-bit Float TIFF) exports.
    /// Raises <see cref="MipChanged"/> only on user-driven mip index changes.
    /// </summary>
    public sealed class TextureMipmapViewModel : ViewModelBase
    {
        private IReadOnlyList<TextureMipLevelInfo> _mipLevels = Array.Empty<TextureMipLevelInfo>();
        private bool _isVisible;
        private bool _hasActiveTexture;
        private int _mipCount;
        private int _mipIndex;
        private bool _isHdrTexture;
        private string _formatBadgeText = string.Empty;

        public TextureMipmapViewModel()
        {
            PreviousMipCommand = new RelayCommand(
                () => MipIndex = _mipIndex - 1,
                () => _isVisible && _mipIndex > 0);

            NextMipCommand = new RelayCommand(
                () => MipIndex = _mipIndex + 1,
                () => _isVisible && _mipIndex < _mipCount - 1);

            ExportAllMipsCommand = new AsyncRelayCommand(
                ExecuteExportAllMipsAsync,
                () => CanExportMips);

            ExportHdrExrCommand = new AsyncRelayCommand(
                ExecuteExportHdrExrAsync,
                () => CanExportHdr);

            ExportHdrTiffCommand = new AsyncRelayCommand(
                ExecuteExportHdrTiffAsync,
                () => CanExportHdr);
        }

        public event Action<int>? MipChanged;

        public Func<Task>? ExportAllMipsHandler { get; set; }

        public Func<Task>? ExportHdrExrHandler { get; set; }

        public Func<Task>? ExportHdrTiffHandler { get; set; }

        public RelayCommand PreviousMipCommand { get; }

        public RelayCommand NextMipCommand { get; }

        public AsyncRelayCommand ExportAllMipsCommand { get; }

        public AsyncRelayCommand ExportHdrExrCommand { get; }

        public AsyncRelayCommand ExportHdrTiffCommand { get; }

        public bool IsVisible
        {
            get => _isVisible;
            private set => SetProperty(ref _isVisible, value);
        }

        public int MipCount => _mipCount;

        public int MaxMipIndex => Math.Max(0, _mipCount - 1);

        public bool HasMultipleMips => _mipCount > 1;

        public int CurrentMipWidth => _mipIndex >= 0 && _mipIndex < _mipLevels.Count ? _mipLevels[_mipIndex].Width : 0;

        public int CurrentMipHeight => _mipIndex >= 0 && _mipIndex < _mipLevels.Count ? _mipLevels[_mipIndex].Height : 0;

        public int MipIndex
        {
            get => _mipIndex;
            set
            {
                int clamped = _mipCount > 0 ? Math.Clamp(value, 0, _mipCount - 1) : 0;
                if (SetProperty(ref _mipIndex, clamped))
                {
                    OnPropertyChanged(nameof(CurrentMipWidth));
                    OnPropertyChanged(nameof(CurrentMipHeight));
                    OnPropertyChanged(nameof(MipText));
                    NotifyCommands();
                    MipChanged?.Invoke(clamped);
                }
            }
        }

        public string MipText
        {
            get
            {
                if (_mipCount <= 0)
                {
                    return string.Empty;
                }

                return $"Mip {_mipIndex} / {_mipCount} ({CurrentMipWidth}×{CurrentMipHeight})";
            }
        }

        public string FormatBadgeText => _formatBadgeText;

        public bool IsHdrTexture => _isHdrTexture;

        public bool CanExportHdr => _isVisible && _hasActiveTexture && _isHdrTexture && _mipCount > 0;

        public bool CanExportMips => _isVisible && _hasActiveTexture && _mipCount > 0;

        public void Show(Texture2D texture, IReadOnlyList<TextureMipLevelInfo> mipChain)
        {
            ArgumentNullException.ThrowIfNull(texture);

            var availableLevels = new List<TextureMipLevelInfo>();
            if (mipChain != null)
            {
                for (int i = 0; i < mipChain.Count; i++)
                {
                    if (mipChain[i].IsAvailable)
                    {
                        availableLevels.Add(mipChain[i]);
                    }
                }
            }

            if (availableLevels.Count == 0 && texture.m_Width > 0 && texture.m_Height > 0)
            {
                long size = texture.image_data?.Size ?? 0L;
                availableLevels.Add(new TextureMipLevelInfo(0, texture.m_Width, texture.m_Height, 0, size, size > 0));
            }

            _mipLevels = availableLevels;
            _hasActiveTexture = availableLevels.Count > 0 && availableLevels[0].IsAvailable;
            _mipCount = availableLevels.Count;
            _mipIndex = 0;
            _isHdrTexture = HdrTextureExporter.IsHdrFormat(texture.m_TextureFormat);
            _formatBadgeText = _isHdrTexture
                ? $"HDR · {texture.m_TextureFormat}"
                : texture.m_TextureFormat.ToString();

            IsVisible = true;
            NotifyAllStateChanged();
        }

        public void Hide()
        {
            _mipLevels = Array.Empty<TextureMipLevelInfo>();
            _hasActiveTexture = false;
            _mipCount = 0;
            _mipIndex = 0;
            _isHdrTexture = false;
            _formatBadgeText = string.Empty;

            IsVisible = false;
            NotifyAllStateChanged();
        }

        private Task ExecuteExportAllMipsAsync()
        {
            return ExportAllMipsHandler != null ? ExportAllMipsHandler() : Task.CompletedTask;
        }

        private Task ExecuteExportHdrExrAsync()
        {
            return ExportHdrExrHandler != null ? ExportHdrExrHandler() : Task.CompletedTask;
        }

        private Task ExecuteExportHdrTiffAsync()
        {
            return ExportHdrTiffHandler != null ? ExportHdrTiffHandler() : Task.CompletedTask;
        }

        private void NotifyAllStateChanged()
        {
            OnPropertyChanged(nameof(MipCount));
            OnPropertyChanged(nameof(MaxMipIndex));
            OnPropertyChanged(nameof(HasMultipleMips));
            OnPropertyChanged(nameof(MipIndex));
            OnPropertyChanged(nameof(CurrentMipWidth));
            OnPropertyChanged(nameof(CurrentMipHeight));
            OnPropertyChanged(nameof(MipText));
            OnPropertyChanged(nameof(FormatBadgeText));
            OnPropertyChanged(nameof(IsHdrTexture));
            OnPropertyChanged(nameof(CanExportHdr));
            OnPropertyChanged(nameof(CanExportMips));
            NotifyCommands();
        }

        private void NotifyCommands()
        {
            PreviousMipCommand.NotifyCanExecuteChanged();
            NextMipCommand.NotifyCanExecuteChanged();
            ExportAllMipsCommand.NotifyCanExecuteChanged();
            ExportHdrExrCommand.NotifyCanExecuteChanged();
            ExportHdrTiffCommand.NotifyCanExecuteChanged();
        }
    }
}
