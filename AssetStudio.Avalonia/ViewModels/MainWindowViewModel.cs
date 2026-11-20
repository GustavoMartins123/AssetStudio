using System;
using System.ComponentModel;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly AssetLoadingService _loadingService;
        private string _statusText = "Ready";
        private double _loadingProgress;
        private bool _isIndexingActive;
        private bool _isPauseEnabled;
        private bool _isResumeEnabled;
        private bool _isStopEnabled;
        private string _specifyUnityVersion = string.Empty;

        public MainWindowViewModel(
            AssetLoadingService loadingService,
            AssetListViewModel? assetList = null,
            IndexingProgressViewModel? indexingProgress = null,
            Action<Action>? dispatchToUi = null)
        {
            _loadingService = loadingService;
            AssetList = assetList ?? new AssetListViewModel(dispatchToUi);
            IndexingProgress = indexingProgress ?? new IndexingProgressViewModel();
            IndexingProgress.PropertyChanged += OnIndexingProgressPropertyChanged;
        }

        public AssetLoadingService LoadingService => _loadingService;

        public AssetListViewModel AssetList { get; }

        public IndexingProgressViewModel IndexingProgress { get; }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public double LoadingProgress
        {
            get => _loadingProgress;
            set => SetProperty(ref _loadingProgress, value);
        }

        public bool IsIndexingActive
        {
            get => _isIndexingActive;
            set => SetProperty(ref _isIndexingActive, value);
        }

        public bool IsPauseEnabled
        {
            get => _isPauseEnabled;
            set => SetProperty(ref _isPauseEnabled, value);
        }

        public bool IsResumeEnabled
        {
            get => _isResumeEnabled;
            set => SetProperty(ref _isResumeEnabled, value);
        }

        public bool IsStopEnabled
        {
            get => _isStopEnabled;
            set => SetProperty(ref _isStopEnabled, value);
        }

        public string SpecifyUnityVersion
        {
            get => _specifyUnityVersion;
            set => SetProperty(ref _specifyUnityVersion, value);
        }

        private void OnIndexingProgressPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(IndexingProgressViewModel.CanPause):
                    IsPauseEnabled = IndexingProgress.CanPause;
                    break;
                case nameof(IndexingProgressViewModel.CanResume):
                    IsResumeEnabled = IndexingProgress.CanResume;
                    break;
                case nameof(IndexingProgressViewModel.CanCancel):
                    IsStopEnabled = IndexingProgress.CanCancel;
                    break;
            }
        }
    }
}
