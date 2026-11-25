using System;

namespace AssetStudio.Avalonia.ViewModels
{
    /// <summary>
    /// Slice selection for the Texture2DArray preview: whether the selector is shown, the current slice,
    /// and previous/next commands. Raises <see cref="SliceChanged"/> only for user-driven changes.
    /// </summary>
    public sealed class TextureArraySliceViewModel : ViewModelBase
    {
        private bool _isVisible;
        private int _sliceCount = 1;
        private int _sliceIndex;

        public TextureArraySliceViewModel()
        {
            PreviousSliceCommand = new RelayCommand(() => SliceIndex = _sliceIndex - 1, () => _sliceIndex > 0);
            NextSliceCommand = new RelayCommand(() => SliceIndex = _sliceIndex + 1, () => _sliceIndex < _sliceCount - 1);
        }

        public event Action<int>? SliceChanged;

        public RelayCommand PreviousSliceCommand { get; }

        public RelayCommand NextSliceCommand { get; }

        public bool IsVisible
        {
            get => _isVisible;
            private set => SetProperty(ref _isVisible, value);
        }

        public int SliceCount => _sliceCount;

        public int MaxSliceIndex => _sliceCount - 1;

        public bool HasMultipleSlices => _sliceCount > 1;

        public int SliceIndex
        {
            get => _sliceIndex;
            set
            {
                var clamped = Math.Clamp(value, 0, _sliceCount - 1);
                if (SetProperty(ref _sliceIndex, clamped))
                {
                    OnPropertyChanged(nameof(SliceText));
                    NotifyCommands();
                    SliceChanged?.Invoke(clamped);
                }
            }
        }

        public string SliceText => $"Slice {_sliceIndex + 1} / {_sliceCount}";

        public void Show(int sliceCount)
        {
            _sliceCount = Math.Max(1, sliceCount);
            _sliceIndex = 0;
            OnPropertyChanged(nameof(SliceCount));
            OnPropertyChanged(nameof(MaxSliceIndex));
            OnPropertyChanged(nameof(HasMultipleSlices));
            OnPropertyChanged(nameof(SliceIndex));
            OnPropertyChanged(nameof(SliceText));
            NotifyCommands();
            IsVisible = true;
        }

        public void Hide()
        {
            IsVisible = false;
        }

        private void NotifyCommands()
        {
            PreviousSliceCommand.NotifyCanExecuteChanged();
            NextSliceCommand.NotifyCanExecuteChanged();
        }
    }
}
