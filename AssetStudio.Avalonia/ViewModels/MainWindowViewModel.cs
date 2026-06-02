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

        public MainWindowViewModel(AssetLoadingService loadingService)
        {
            _loadingService = loadingService;
        }

        public AssetLoadingService LoadingService => _loadingService;

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
    }
}
