using System;
using System.ComponentModel;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly AssetLoadingService _loadingService;
        private bool _disposed;
        private string _statusText = "Ready";
        private double _loadingProgress;
        private bool _isIndexingActive;
        private bool _isPauseEnabled;
        private bool _isResumeEnabled;
        private bool _isStopEnabled;
        private string _specifyUnityVersion = string.Empty;

        public MainWindowViewModel(Action<Action>? dispatchToUi)
            : this(loadingService: null, dispatchToUi: dispatchToUi)
        {
        }

        public MainWindowViewModel(
            AssetLoadingService? loadingService = null,
            AssetListViewModel? assetList = null,
            IndexingProgressViewModel? indexingProgress = null,
            PreviewSessionViewModel? previewSession = null,
            MeshPreviewViewModel? meshPreview = null,
            ExportViewModel? export = null,
            SceneHierarchyViewModel? sceneHierarchy = null,
            ProjectSessionViewModel? projectSession = null,
            MediaPlaybackViewModel? mediaPlayback = null,
            Action<Action>? dispatchToUi = null)
        {
            var effectiveDispatch = dispatchToUi ?? (static action => action());
            _loadingService = loadingService ?? new AssetLoadingService();
            AssetList = assetList ?? new AssetListViewModel(effectiveDispatch);
            IndexingProgress = indexingProgress ?? new IndexingProgressViewModel();
            PreviewSession = previewSession ?? new PreviewSessionViewModel(effectiveDispatch);
            MeshPreview = meshPreview ?? new MeshPreviewViewModel(effectiveDispatch);
            Export = export ?? new ExportViewModel(effectiveDispatch);
            SceneHierarchy = sceneHierarchy ?? new SceneHierarchyViewModel(effectiveDispatch);
            ProjectSession = projectSession ?? new ProjectSessionViewModel(effectiveDispatch);
            MediaPlayback = mediaPlayback ?? new MediaPlaybackViewModel(effectiveDispatch);
            IndexingProgress.PropertyChanged += OnIndexingProgressPropertyChanged;
        }

        public AssetLoadingService LoadingService => _loadingService;

        public AssetListViewModel AssetList { get; }

        public IndexingProgressViewModel IndexingProgress { get; }

        public PreviewSessionViewModel PreviewSession { get; }

        public MeshPreviewViewModel MeshPreview { get; }

        public ExportViewModel Export { get; }

        public SceneHierarchyViewModel SceneHierarchy { get; }

        public ProjectSessionViewModel ProjectSession { get; }

        public MediaPlaybackViewModel MediaPlayback { get; }

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

        public void ResetSession(bool keepSavedProjects = true)
        {
            _loadingService.StopIndexing();
            AssetList.Reset();
            IndexingProgress.Reset();
            PreviewSession.Reset();
            MeshPreview.Reset();
            Export.Reset();
            SceneHierarchy.Reset();
            ProjectSession.ResetSession(keepSavedProjects);
            MediaPlayback.Reset();
            StatusText = "Ready";
            LoadingProgress = 0;
            IsIndexingActive = false;
            IsPauseEnabled = false;
            IsResumeEnabled = false;
            IsStopEnabled = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IndexingProgress.PropertyChanged -= OnIndexingProgressPropertyChanged;
            _loadingService.StopIndexing();
            PreviewSession.Dispose();
            Export.Dispose();
            AssetList.Reset();
            IndexingProgress.Reset();
            MeshPreview.Reset();
            SceneHierarchy.Reset();
            ProjectSession.ResetSession();
            MediaPlayback.Reset();
            StatusText = "Ready";
            LoadingProgress = 0;
            IsIndexingActive = false;
            IsPauseEnabled = false;
            IsResumeEnabled = false;
            IsStopEnabled = false;
        }
    }
}
