using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class MainWindowCommandHandlers
    {
        public Func<Task>? LoadFileAsync { get; set; }
        public Func<Task>? LoadFolderAsync { get; set; }
        public Func<Task>? LoadFolderEagerAsync { get; set; }
        public Func<Task>? ExtractFileAsync { get; set; }
        public Func<Task>? ExtractFolderAsync { get; set; }
        public Func<Task>? OpenProjectManagerAsync { get; set; }

        public Func<Task>? SetProjectRootAsync { get; set; }
        public Func<Task>? ShowExportOptionsAsync { get; set; }
        public Func<Task>? ClearProjectCacheAsync { get; set; }

        public Func<Task>? ExportAllObjectsSplitAsync { get; set; }
        public Func<Task>? ExportSelectedObjectsSplitAsync { get; set; }
        public Func<Task>? ExportSelectedObjectsSplitWithAnimationClipAsync { get; set; }
        public Func<Task>? ExportSelectedObjectsMergeAsync { get; set; }
        public Func<Task>? ExportSelectedObjectsMergeWithAnimationClipAsync { get; set; }

        public Func<Task>? ExportAllAssetsAsync { get; set; }
        public Func<Task>? ExportSelectedAssetsAsync { get; set; }
        public Func<Task>? ExportFilteredAssetsAsync { get; set; }
        public Func<Task>? ExportModelGroupFbxAsync { get; set; }
        public Func<Task>? ExportAnimatorWithSelectedAnimationClipAsync { get; set; }
        public Func<Task>? ExportAllAssetsRawAsync { get; set; }
        public Func<Task>? ExportSelectedAssetsRawAsync { get; set; }
        public Func<Task>? ExportFilteredAssetsRawAsync { get; set; }
        public Func<Task>? ExportAllAssetsDumpAsync { get; set; }
        public Func<Task>? ExportSelectedAssetsDumpAsync { get; set; }
        public Func<Task>? ExportFilteredAssetsDumpAsync { get; set; }
        public Func<Task>? ExportAllAssetsXmlAsync { get; set; }
        public Func<Task>? ExportSelectedAssetsXmlAsync { get; set; }
        public Func<Task>? ExportFilteredAssetsXmlAsync { get; set; }
        public Func<Task>? ExportErrorLogAsync { get; set; }

        public Action? FilterTypeAll { get; set; }
        public Action<ClassIDType>? ToggleFilterType { get; set; }
        public Func<Task>? ExportClassStructuresAsync { get; set; }

        public Action? PauseIndexing { get; set; }
        public Action? ResumeIndexing { get; set; }
        public Action? StopIndexing { get; set; }

        public Action? ClearClassFilter { get; set; }
        public Func<Task>? CopyAssetCellTextAsync { get; set; }
        public Action? GoToSceneHierarchy { get; set; }
        public Func<Task>? ShowOriginalFileAsync { get; set; }
        public Action? ShowClassInstances { get; set; }

        public Action<TextAssetDisplayMode>? SetTextAssetDisplayMode { get; set; }
        public Action? ToggleAnimationPlayPause { get; set; }
        public Action? RestartAnimation { get; set; }
        public Action? ResetMeshView { get; set; }
        public Action? RotateMeshLeft { get; set; }
        public Action? RotateMeshRight { get; set; }
        public Action? RotateMeshUp { get; set; }
        public Action? RotateMeshDown { get; set; }
        public Action? ResetMeshMaterialColor { get; set; }
        public Func<Task>? ExportPreviewModelAsync { get; set; }
        public Func<AssetReferenceItem?, Task>? NavigateToReferencedAssetAsync { get; set; }

        public Func<Task>? VideoPlayPauseAsync { get; set; }
        public Func<Task>? VideoStopAsync { get; set; }
        public Func<Task>? VideoExportAsync { get; set; }
        public Func<Task>? AudioPlayAsync { get; set; }
        public Action? AudioPause { get; set; }
        public Action? AudioStop { get; set; }

        public Action<int>? WireframeModeChanged { get; set; }
        public Action<int>? ShadeModeChanged { get; set; }
        public Action<int>? NormalModeChanged { get; set; }
        public Action<bool>? ShowSkeletonChanged { get; set; }
        public Action<PreviewChannelMask>? TextureChannelToggled { get; set; }

        public void Clear()
        {
            LoadFileAsync = null;
            LoadFolderAsync = null;
            LoadFolderEagerAsync = null;
            ExtractFileAsync = null;
            ExtractFolderAsync = null;
            OpenProjectManagerAsync = null;

            SetProjectRootAsync = null;
            ShowExportOptionsAsync = null;
            ClearProjectCacheAsync = null;

            ExportAllObjectsSplitAsync = null;
            ExportSelectedObjectsSplitAsync = null;
            ExportSelectedObjectsSplitWithAnimationClipAsync = null;
            ExportSelectedObjectsMergeAsync = null;
            ExportSelectedObjectsMergeWithAnimationClipAsync = null;

            ExportAllAssetsAsync = null;
            ExportSelectedAssetsAsync = null;
            ExportFilteredAssetsAsync = null;
            ExportModelGroupFbxAsync = null;
            ExportAnimatorWithSelectedAnimationClipAsync = null;
            ExportAllAssetsRawAsync = null;
            ExportSelectedAssetsRawAsync = null;
            ExportFilteredAssetsRawAsync = null;
            ExportAllAssetsDumpAsync = null;
            ExportSelectedAssetsDumpAsync = null;
            ExportFilteredAssetsDumpAsync = null;
            ExportAllAssetsXmlAsync = null;
            ExportSelectedAssetsXmlAsync = null;
            ExportFilteredAssetsXmlAsync = null;
            ExportErrorLogAsync = null;

            FilterTypeAll = null;
            ToggleFilterType = null;
            ExportClassStructuresAsync = null;

            PauseIndexing = null;
            ResumeIndexing = null;
            StopIndexing = null;

            ClearClassFilter = null;
            CopyAssetCellTextAsync = null;
            GoToSceneHierarchy = null;
            ShowOriginalFileAsync = null;
            ShowClassInstances = null;

            SetTextAssetDisplayMode = null;
            ToggleAnimationPlayPause = null;
            RestartAnimation = null;
            ResetMeshView = null;
            RotateMeshLeft = null;
            RotateMeshRight = null;
            RotateMeshUp = null;
            RotateMeshDown = null;
            ResetMeshMaterialColor = null;
            ExportPreviewModelAsync = null;
            NavigateToReferencedAssetAsync = null;

            VideoPlayPauseAsync = null;
            VideoStopAsync = null;
            VideoExportAsync = null;
            AudioPlayAsync = null;
            AudioPause = null;
            AudioStop = null;

            WireframeModeChanged = null;
            ShadeModeChanged = null;
            NormalModeChanged = null;
            ShowSkeletonChanged = null;
            TextureChannelToggled = null;
        }
    }

    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly AssetLoadingService _loadingService;
        private readonly IDialogService _dialogService;
        private bool _disposed;
        private string _statusText = "Ready";
        private double _loadingProgress;
        private bool _isIndexingActive;
        private bool _isPauseEnabled;
        private bool _isResumeEnabled;
        private bool _isStopEnabled;
        private bool _isReturningToProjectManager;

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
            WindowOptionsViewModel? options = null,
            IDialogService? dialogService = null,
            Action<Action>? dispatchToUi = null)
        {
            var effectiveDispatch = dispatchToUi ?? (static action => action());
            _loadingService = loadingService ?? new AssetLoadingService();
            _dialogService = dialogService ?? NoOpDialogService.Instance;

            AssetList = assetList ?? new AssetListViewModel(effectiveDispatch);
            IndexingProgress = indexingProgress ?? new IndexingProgressViewModel();
            PreviewSession = previewSession ?? new PreviewSessionViewModel(effectiveDispatch);
            MeshPreview = meshPreview ?? new MeshPreviewViewModel(effectiveDispatch);
            Export = export ?? new ExportViewModel(effectiveDispatch);
            SceneHierarchy = sceneHierarchy ?? new SceneHierarchyViewModel(effectiveDispatch);
            ProjectSession = projectSession ?? new ProjectSessionViewModel(effectiveDispatch);
            MediaPlayback = mediaPlayback ?? new MediaPlaybackViewModel(effectiveDispatch);
            Options = options ?? new WindowOptionsViewModel();
            CommandHandlers = new MainWindowCommandHandlers();

            PreviewSession.IsPreviewEnabled = Options.EnablePreview;
            PreviewSession.IsDisplayInfoEnabled = Options.DisplayInfo;

            IndexingProgress.PropertyChanged += OnIndexingProgressPropertyChanged;
            PreviewSession.PropertyChanged += OnPreviewSessionPropertyChanged;
            MeshPreview.PropertyChanged += OnMeshPreviewPropertyChanged;
            Options.EnablePreviewChanged += OnOptionsEnablePreviewChanged;
            Options.DisplayInfoChanged += OnOptionsDisplayInfoChanged;
            Options.SpecifyUnityVersionChanged += OnOptionsSpecifyUnityVersionChanged;

            LoadFileCommand = new AsyncRelayCommand(ExecuteLoadFileAsync, onException: HandleCommandException);
            LoadFolderCommand = new AsyncRelayCommand(ExecuteLoadFolderAsync, onException: HandleCommandException);
            LoadFolderEagerCommand = new AsyncRelayCommand(ExecuteLoadFolderEagerAsync, onException: HandleCommandException);
            ExtractFileCommand = new AsyncRelayCommand(ExecuteExtractFileAsync, onException: HandleCommandException);
            ExtractFolderCommand = new AsyncRelayCommand(ExecuteExtractFolderAsync, onException: HandleCommandException);
            OpenProjectManagerCommand = new AsyncRelayCommand(
                ExecuteOpenProjectManagerAsync,
                canExecute: () => CanOpenProjectManager,
                onException: HandleCommandException);

            SetProjectRootCommand = new AsyncRelayCommand(ExecuteSetProjectRootAsync, onException: HandleCommandException);
            ShowExportOptionsCommand = new AsyncRelayCommand(ExecuteShowExportOptionsAsync, onException: HandleCommandException);
            ClearProjectCacheCommand = new AsyncRelayCommand(ExecuteClearProjectCacheAsync, onException: HandleCommandException);

            ExportAllObjectsSplitCommand = new AsyncRelayCommand(ExecuteExportAllObjectsSplitAsync, onException: HandleCommandException);
            ExportSelectedObjectsSplitCommand = new AsyncRelayCommand(ExecuteExportSelectedObjectsSplitAsync, onException: HandleCommandException);
            ExportSelectedObjectsSplitWithAnimationClipCommand = new AsyncRelayCommand(ExecuteExportSelectedObjectsSplitWithAnimationClipAsync, onException: HandleCommandException);
            ExportSelectedObjectsMergeCommand = new AsyncRelayCommand(ExecuteExportSelectedObjectsMergeAsync, onException: HandleCommandException);
            ExportSelectedObjectsMergeWithAnimationClipCommand = new AsyncRelayCommand(ExecuteExportSelectedObjectsMergeWithAnimationClipAsync, onException: HandleCommandException);

            ExportAllAssetsCommand = new AsyncRelayCommand(ExecuteExportAllAssetsAsync, onException: HandleCommandException);
            ExportSelectedAssetsCommand = new AsyncRelayCommand(ExecuteExportSelectedAssetsAsync, onException: HandleCommandException);
            ExportFilteredAssetsCommand = new AsyncRelayCommand(ExecuteExportFilteredAssetsAsync, onException: HandleCommandException);
            ExportModelGroupFbxCommand = new AsyncRelayCommand(ExecuteExportModelGroupFbxAsync, onException: HandleCommandException);
            ExportAnimatorWithSelectedAnimationClipCommand = new AsyncRelayCommand(ExecuteExportAnimatorWithSelectedAnimationClipAsync, onException: HandleCommandException);
            ExportAllAssetsRawCommand = new AsyncRelayCommand(ExecuteExportAllAssetsRawAsync, onException: HandleCommandException);
            ExportSelectedAssetsRawCommand = new AsyncRelayCommand(ExecuteExportSelectedAssetsRawAsync, onException: HandleCommandException);
            ExportFilteredAssetsRawCommand = new AsyncRelayCommand(ExecuteExportFilteredAssetsRawAsync, onException: HandleCommandException);
            ExportAllAssetsDumpCommand = new AsyncRelayCommand(ExecuteExportAllAssetsDumpAsync, onException: HandleCommandException);
            ExportSelectedAssetsDumpCommand = new AsyncRelayCommand(ExecuteExportSelectedAssetsDumpAsync, onException: HandleCommandException);
            ExportFilteredAssetsDumpCommand = new AsyncRelayCommand(ExecuteExportFilteredAssetsDumpAsync, onException: HandleCommandException);
            ExportAllAssetsXmlCommand = new AsyncRelayCommand(ExecuteExportAllAssetsXmlAsync, onException: HandleCommandException);
            ExportSelectedAssetsXmlCommand = new AsyncRelayCommand(ExecuteExportSelectedAssetsXmlAsync, onException: HandleCommandException);
            ExportFilteredAssetsXmlCommand = new AsyncRelayCommand(ExecuteExportFilteredAssetsXmlAsync, onException: HandleCommandException);
            ExportErrorLogCommand = new AsyncRelayCommand(ExecuteExportErrorLogAsync, onException: HandleCommandException);

            FilterTypeAllCommand = new RelayCommand(ExecuteFilterTypeAll);
            ToggleFilterTypeCommand = new RelayCommand<ClassIDType>(ExecuteToggleFilterType);
            ExportClassStructuresCommand = new AsyncRelayCommand(ExecuteExportClassStructuresAsync, onException: HandleCommandException);

            PauseIndexingCommand = new RelayCommand(ExecutePauseIndexing, () => IsPauseEnabled);
            ResumeIndexingCommand = new RelayCommand(ExecuteResumeIndexing, () => IsResumeEnabled);
            StopIndexingCommand = new RelayCommand(ExecuteStopIndexing, () => IsStopEnabled);

            ClearClassFilterCommand = new RelayCommand(ExecuteClearClassFilter);
            CopyAssetCellTextCommand = new AsyncRelayCommand(ExecuteCopyAssetCellTextAsync, onException: HandleCommandException);
            GoToSceneHierarchyCommand = new RelayCommand(ExecuteGoToSceneHierarchy);
            ShowOriginalFileCommand = new AsyncRelayCommand(ExecuteShowOriginalFileAsync, onException: HandleCommandException);
            ShowClassInstancesCommand = new RelayCommand(ExecuteShowClassInstances);

            ShowTextAssetCardsCommand = new RelayCommand(() => ExecuteSetTextAssetDisplayMode(TextAssetDisplayMode.Cards));
            ShowTextAssetDetailsCommand = new RelayCommand(() => ExecuteSetTextAssetDisplayMode(TextAssetDisplayMode.Text));
            ShowTextAssetInfoCommand = new RelayCommand(() => ExecuteSetTextAssetDisplayMode(TextAssetDisplayMode.Info));
            TogglePreviewInfoCommand = new RelayCommand(() => PreviewSession.ToggleInfoExpanded());
            ToggleAnimationPlayPauseCommand = new RelayCommand(ExecuteToggleAnimationPlayPause);
            RestartAnimationCommand = new RelayCommand(ExecuteRestartAnimation);
            ToggleMeshControlsCommand = new RelayCommand(() => MeshPreview.ToggleControlsExpanded());
            ResetMeshViewCommand = new RelayCommand(ExecuteResetMeshView);
            RotateMeshLeftCommand = new RelayCommand(ExecuteRotateMeshLeft);
            RotateMeshRightCommand = new RelayCommand(ExecuteRotateMeshRight);
            RotateMeshUpCommand = new RelayCommand(ExecuteRotateMeshUp);
            RotateMeshDownCommand = new RelayCommand(ExecuteRotateMeshDown);
            ResetMeshMaterialColorCommand = new RelayCommand(ExecuteResetMeshMaterialColor);
            ExportPreviewModelCommand = new AsyncRelayCommand(
                ExecuteExportPreviewModelAsync,
                canExecute: () => CanExportPreviewModel,
                onException: HandleCommandException);
            NavigateToReferencedAssetCommand = new AsyncRelayCommand<AssetReferenceItem>(
                ExecuteNavigateToReferencedAssetAsync,
                canExecute: item => !_disposed && item?.IsNavigable == true,
                onException: HandleCommandException);
            NavigateToSelectedReferenceCommand = new AsyncRelayCommand(
                () => ExecuteNavigateToReferencedAssetAsync(PreviewSession.SelectedReferenceItem),
                canExecute: () => CanNavigateToSelectedReference,
                onException: HandleCommandException);

            VideoPlayPauseCommand = new AsyncRelayCommand(ExecuteVideoPlayPauseAsync, onException: HandleCommandException);
            VideoStopCommand = new AsyncRelayCommand(ExecuteVideoStopAsync, onException: HandleCommandException);
            VideoExportCommand = new AsyncRelayCommand(ExecuteVideoExportAsync, onException: HandleCommandException);
            AudioPlayCommand = new AsyncRelayCommand(ExecuteAudioPlayAsync, onException: HandleCommandException);
            AudioPauseCommand = new RelayCommand(ExecuteAudioPause);
            AudioStopCommand = new RelayCommand(ExecuteAudioStop);

            CycleWireframeModeCommand = new RelayCommand(ExecuteCycleWireframeMode);
            ToggleShadeModeCommand = new RelayCommand(ExecuteToggleShadeMode);
            ToggleNormalModeCommand = new RelayCommand(ExecuteToggleNormalMode);
            ToggleShowSkeletonCommand = new RelayCommand(ExecuteToggleShowSkeleton);
            ToggleTextureChannelCommand = new RelayCommand<PreviewChannelMask>(ExecuteToggleTextureChannel);
        }

        public AssetLoadingService LoadingService => _loadingService;

        public IDialogService DialogService => _dialogService;

        public MainWindowCommandHandlers CommandHandlers { get; }

        public AssetListViewModel AssetList { get; }

        public IndexingProgressViewModel IndexingProgress { get; }

        public PreviewSessionViewModel PreviewSession { get; }

        public MeshPreviewViewModel MeshPreview { get; }

        public ExportViewModel Export { get; }

        public SceneHierarchyViewModel SceneHierarchy { get; }

        public ProjectSessionViewModel ProjectSession { get; }

        public MediaPlaybackViewModel MediaPlayback { get; }

        public WindowOptionsViewModel Options { get; }

        public AsyncRelayCommand LoadFileCommand { get; }
        public AsyncRelayCommand LoadFolderCommand { get; }
        public AsyncRelayCommand LoadFolderEagerCommand { get; }
        public AsyncRelayCommand ExtractFileCommand { get; }
        public AsyncRelayCommand ExtractFolderCommand { get; }
        public AsyncRelayCommand OpenProjectManagerCommand { get; }

        public AsyncRelayCommand SetProjectRootCommand { get; }
        public AsyncRelayCommand ShowExportOptionsCommand { get; }
        public AsyncRelayCommand ClearProjectCacheCommand { get; }

        public AsyncRelayCommand ExportAllObjectsSplitCommand { get; }
        public AsyncRelayCommand ExportSelectedObjectsSplitCommand { get; }
        public AsyncRelayCommand ExportSelectedObjectsSplitWithAnimationClipCommand { get; }
        public AsyncRelayCommand ExportSelectedObjectsMergeCommand { get; }
        public AsyncRelayCommand ExportSelectedObjectsMergeWithAnimationClipCommand { get; }

        public AsyncRelayCommand ExportAllAssetsCommand { get; }
        public AsyncRelayCommand ExportSelectedAssetsCommand { get; }
        public AsyncRelayCommand ExportFilteredAssetsCommand { get; }
        public AsyncRelayCommand ExportModelGroupFbxCommand { get; }
        public AsyncRelayCommand ExportAnimatorWithSelectedAnimationClipCommand { get; }
        public AsyncRelayCommand ExportAllAssetsRawCommand { get; }
        public AsyncRelayCommand ExportSelectedAssetsRawCommand { get; }
        public AsyncRelayCommand ExportFilteredAssetsRawCommand { get; }
        public AsyncRelayCommand ExportAllAssetsDumpCommand { get; }
        public AsyncRelayCommand ExportSelectedAssetsDumpCommand { get; }
        public AsyncRelayCommand ExportFilteredAssetsDumpCommand { get; }
        public AsyncRelayCommand ExportAllAssetsXmlCommand { get; }
        public AsyncRelayCommand ExportSelectedAssetsXmlCommand { get; }
        public AsyncRelayCommand ExportFilteredAssetsXmlCommand { get; }
        public AsyncRelayCommand ExportErrorLogCommand { get; }

        public RelayCommand FilterTypeAllCommand { get; }
        public RelayCommand<ClassIDType> ToggleFilterTypeCommand { get; }
        public AsyncRelayCommand ExportClassStructuresCommand { get; }

        public RelayCommand PauseIndexingCommand { get; }
        public RelayCommand ResumeIndexingCommand { get; }
        public RelayCommand StopIndexingCommand { get; }

        public RelayCommand ClearClassFilterCommand { get; }
        public AsyncRelayCommand CopyAssetCellTextCommand { get; }
        public RelayCommand GoToSceneHierarchyCommand { get; }
        public AsyncRelayCommand ShowOriginalFileCommand { get; }
        public RelayCommand ShowClassInstancesCommand { get; }

        public RelayCommand ShowTextAssetCardsCommand { get; }
        public RelayCommand ShowTextAssetDetailsCommand { get; }
        public RelayCommand ShowTextAssetInfoCommand { get; }
        public RelayCommand TogglePreviewInfoCommand { get; }
        public RelayCommand ToggleAnimationPlayPauseCommand { get; }
        public RelayCommand RestartAnimationCommand { get; }
        public RelayCommand ToggleMeshControlsCommand { get; }
        public RelayCommand ResetMeshViewCommand { get; }
        public RelayCommand RotateMeshLeftCommand { get; }
        public RelayCommand RotateMeshRightCommand { get; }
        public RelayCommand RotateMeshUpCommand { get; }
        public RelayCommand RotateMeshDownCommand { get; }
        public RelayCommand ResetMeshMaterialColorCommand { get; }
        public AsyncRelayCommand ExportPreviewModelCommand { get; }
        public AsyncRelayCommand<AssetReferenceItem> NavigateToReferencedAssetCommand { get; }
        public AsyncRelayCommand NavigateToSelectedReferenceCommand { get; }

        public AsyncRelayCommand VideoPlayPauseCommand { get; }
        public AsyncRelayCommand VideoStopCommand { get; }
        public AsyncRelayCommand VideoExportCommand { get; }
        public AsyncRelayCommand AudioPlayCommand { get; }
        public RelayCommand AudioPauseCommand { get; }
        public RelayCommand AudioStopCommand { get; }

        public RelayCommand CycleWireframeModeCommand { get; }
        public RelayCommand ToggleShadeModeCommand { get; }
        public RelayCommand ToggleNormalModeCommand { get; }
        public RelayCommand ToggleShowSkeletonCommand { get; }
        public RelayCommand<PreviewChannelMask> ToggleTextureChannelCommand { get; }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value ?? string.Empty);
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
            set
            {
                if (SetProperty(ref _isPauseEnabled, value))
                {
                    PauseIndexingCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsResumeEnabled
        {
            get => _isResumeEnabled;
            set
            {
                if (SetProperty(ref _isResumeEnabled, value))
                {
                    ResumeIndexingCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsStopEnabled
        {
            get => _isStopEnabled;
            set
            {
                if (SetProperty(ref _isStopEnabled, value))
                {
                    StopIndexingCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string SpecifyUnityVersion
        {
            get => Options.SpecifyUnityVersion;
            set => Options.SpecifyUnityVersion = value ?? string.Empty;
        }

        public event Action<string?>? ReturnToProjectManagerRequested;

        public bool IsReturningToProjectManager
        {
            get => _isReturningToProjectManager;
            private set
            {
                if (SetProperty(ref _isReturningToProjectManager, value))
                {
                    OnPropertyChanged(nameof(CanOpenProjectManager));
                    OpenProjectManagerCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool CanOpenProjectManager => !_disposed && !_isReturningToProjectManager;

        public bool CanExportPreviewModel =>
            !_disposed && PreviewModelExportHelper.CanExportSurfaceKind(
                PreviewSession.ActiveKind,
                MeshPreview.IsGeometryControlsVisible);

        public bool CanNavigateToSelectedReference =>
            !_disposed && PreviewSession.SelectedReferenceItem?.IsNavigable == true;

        private void OnPreviewSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PreviewSessionViewModel.ActiveKind):
                    OnPropertyChanged(nameof(CanExportPreviewModel));
                    ExportPreviewModelCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(PreviewSessionViewModel.SelectedReferenceItem):
                    OnPropertyChanged(nameof(CanNavigateToSelectedReference));
                    NavigateToSelectedReferenceCommand.NotifyCanExecuteChanged();
                    break;
            }
        }

        private void OnMeshPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MeshPreviewViewModel.IsGeometryControlsVisible))
            {
                OnPropertyChanged(nameof(CanExportPreviewModel));
                ExportPreviewModelCommand.NotifyCanExecuteChanged();
            }
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

        private void OnOptionsEnablePreviewChanged(bool enabled)
        {
            PreviewSession.IsPreviewEnabled = enabled;
        }

        private void OnOptionsDisplayInfoChanged(bool enabled)
        {
            PreviewSession.IsDisplayInfoEnabled = enabled;
        }

        private void OnOptionsSpecifyUnityVersionChanged(string _)
        {
            OnPropertyChanged(nameof(SpecifyUnityVersion));
        }

        private void HandleCommandException(Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }

        private async Task ExecuteLoadFileAsync()
        {
            if (CommandHandlers.LoadFileAsync != null)
            {
                await CommandHandlers.LoadFileAsync();
                return;
            }

            var files = await _dialogService.PickOpenFilesAsync(new FilePickerRequest("Select Game File", allowMultiple: true));
            if (files.Count > 0)
            {
                StatusText = $"Selected {files.Count} file(s) to load.";
            }
        }

        private async Task ExecuteLoadFolderAsync()
        {
            if (CommandHandlers.LoadFolderAsync != null)
            {
                await CommandHandlers.LoadFolderAsync();
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select Game Folder"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ProjectSession.SetLoadedProject(folder, isLazyMode: true);
                StatusText = $"Indexing folder: {folder}";
            }
        }

        private async Task ExecuteLoadFolderEagerAsync()
        {
            if (CommandHandlers.LoadFolderEagerAsync != null)
            {
                await CommandHandlers.LoadFolderEagerAsync();
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select Game Folder (Full / Scene Hierarchy)"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ProjectSession.SetLoadedProject(folder, isLazyMode: false);
                StatusText = $"Loading folder: {folder}";
            }
        }

        private async Task ExecuteExtractFileAsync()
        {
            if (CommandHandlers.ExtractFileAsync != null)
            {
                await CommandHandlers.ExtractFileAsync();
                return;
            }

            var files = await _dialogService.PickOpenFilesAsync(new FilePickerRequest("Select bundle or web file", allowMultiple: true));
            if (files.Count == 0)
            {
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select the save folder"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                StatusText = $"Ready to extract {files.Count} file(s) to {folder}.";
            }
        }

        private async Task ExecuteExtractFolderAsync()
        {
            if (CommandHandlers.ExtractFolderAsync != null)
            {
                await CommandHandlers.ExtractFolderAsync();
                return;
            }

            var sourceFolder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select folder to extract"));
            if (string.IsNullOrWhiteSpace(sourceFolder))
            {
                return;
            }

            var saveFolder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select the save folder"));
            if (!string.IsNullOrWhiteSpace(saveFolder))
            {
                StatusText = $"Ready to extract {sourceFolder} to {saveFolder}.";
            }
        }

        private async Task ExecuteOpenProjectManagerAsync()
        {
            if (!CanOpenProjectManager)
            {
                return;
            }

            var previousProjectId = ProjectSession.SelectedProjectRecord?.Id;
            var previousProjectPath = ProjectSession.ProjectFolderPath;

            IsReturningToProjectManager = true;
            ProjectSession.BeginClosingProject();
            try
            {
                if (CommandHandlers.OpenProjectManagerAsync != null)
                {
                    StatusText = "Returning to Projects...";
                    await CommandHandlers.OpenProjectManagerAsync();
                    return;
                }

                var result = await _dialogService.ShowProjectManagerAsync(previousProjectPath);
                if (result != null)
                {
                    if (!string.IsNullOrWhiteSpace(result.ProjectPath))
                    {
                        ProjectSession.SetLoadedProject(result.ProjectPath, displayName: result.ProjectName, isLazyMode: true);
                    }
                    else if (result.OpenedWithoutProject)
                    {
                        ResetSession();
                    }
                }
                else
                {
                    ReturnToProjectManagerRequested?.Invoke(previousProjectId);
                }
            }
            finally
            {
                if (!_disposed)
                {
                    ProjectSession.CancelClosingProject();
                    IsReturningToProjectManager = false;
                }
            }
        }

        private async Task ExecuteSetProjectRootAsync()
        {
            if (CommandHandlers.SetProjectRootAsync != null)
            {
                await CommandHandlers.SetProjectRootAsync();
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select project root"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ProjectSession.SetLoadedProject(folder, isLazyMode: ProjectSession.IsLazyMode);
                StatusText = $"Project root set to: {folder}";
            }
        }

        private async Task ExecuteShowExportOptionsAsync()
        {
            if (CommandHandlers.ShowExportOptionsAsync != null)
            {
                await CommandHandlers.ShowExportOptionsAsync();
                return;
            }

            var settings = new AvaloniaAppSettings();
            Options.ApplyToSettings(settings);
            if (await _dialogService.ShowExportOptionsAsync(settings))
            {
                StatusText = "Export options updated.";
            }
        }

        private async Task ExecuteClearProjectCacheAsync()
        {
            if (CommandHandlers.ClearProjectCacheAsync != null)
            {
                await CommandHandlers.ClearProjectCacheAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(ProjectSession.ProjectFolderPath))
            {
                await _dialogService.ShowMessageAsync("Clear project cache", "No loaded project folder was found to clear.");
                return;
            }

            StatusText = $"Cleared project cache for: {ProjectSession.ProjectFolderPath}";
        }

        private Task ExecuteExportAllObjectsSplitAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportAllObjectsSplitAsync, "Exporting all objects (split)...");

        private Task ExecuteExportSelectedObjectsSplitAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportSelectedObjectsSplitAsync, "Exporting selected objects (split)...");

        private Task ExecuteExportSelectedObjectsSplitWithAnimationClipAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportSelectedObjectsSplitWithAnimationClipAsync, "Exporting selected objects (split) + AnimationClips...");

        private Task ExecuteExportSelectedObjectsMergeAsync() =>
            InvokeExportHandlerOrSavePickAsync(CommandHandlers.ExportSelectedObjectsMergeAsync, "Save merged FBX", "merged.fbx");

        private Task ExecuteExportSelectedObjectsMergeWithAnimationClipAsync() =>
            InvokeExportHandlerOrSavePickAsync(CommandHandlers.ExportSelectedObjectsMergeWithAnimationClipAsync, "Save merged FBX", "merged.fbx");

        private Task ExecuteExportAllAssetsAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportAllAssetsAsync, "Exporting all assets...");

        private Task ExecuteExportSelectedAssetsAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportSelectedAssetsAsync, "Exporting selected assets...");

        private Task ExecuteExportFilteredAssetsAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportFilteredAssetsAsync, "Exporting filtered assets...");

        private Task ExecuteExportModelGroupFbxAsync() =>
            InvokeExportHandlerOrSavePickAsync(CommandHandlers.ExportModelGroupFbxAsync, "Export Model Group (FBX)", "model.fbx");

        private Task ExecuteExportAnimatorWithSelectedAnimationClipAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportAnimatorWithSelectedAnimationClipAsync, "Exporting Animator + AnimationClips...");

        private Task ExecuteExportAllAssetsRawAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportAllAssetsRawAsync, "Exporting all raw assets...");

        private Task ExecuteExportSelectedAssetsRawAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportSelectedAssetsRawAsync, "Exporting selected raw assets...");

        private Task ExecuteExportFilteredAssetsRawAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportFilteredAssetsRawAsync, "Exporting filtered raw assets...");

        private Task ExecuteExportAllAssetsDumpAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportAllAssetsDumpAsync, "Exporting all asset dumps...");

        private Task ExecuteExportSelectedAssetsDumpAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportSelectedAssetsDumpAsync, "Exporting selected asset dumps...");

        private Task ExecuteExportFilteredAssetsDumpAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportFilteredAssetsDumpAsync, "Exporting filtered asset dumps...");

        private Task ExecuteExportAllAssetsXmlAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportAllAssetsXmlAsync, "Exporting all assets to XML...");

        private Task ExecuteExportSelectedAssetsXmlAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportSelectedAssetsXmlAsync, "Exporting selected assets to XML...");

        private Task ExecuteExportFilteredAssetsXmlAsync() =>
            InvokeExportHandlerOrFolderPickAsync(CommandHandlers.ExportFilteredAssetsXmlAsync, "Exporting filtered assets to XML...");

        private async Task ExecuteExportErrorLogAsync()
        {
            if (CommandHandlers.ExportErrorLogAsync != null)
            {
                await CommandHandlers.ExportErrorLogAsync();
                return;
            }

            var path = await _dialogService.PickSaveFileAsync(new SaveFilePickerRequest("Save error log", "error.log", "log"));
            if (!string.IsNullOrWhiteSpace(path))
            {
                StatusText = $"Exported error log to: {path}";
            }
        }

        private async Task ExecuteExportClassStructuresAsync()
        {
            if (CommandHandlers.ExportClassStructuresAsync != null)
            {
                await CommandHandlers.ExportClassStructuresAsync();
                return;
            }

            if (AssetList.AllAssetClasses.Count == 0)
            {
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select folder to export class structures"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                StatusText = $"Exported class structures to: {folder}";
            }
        }

        private async Task InvokeExportHandlerOrFolderPickAsync(Func<Task>? handler, string statusWhenPicked)
        {
            if (handler != null)
            {
                await handler();
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select the save folder"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                StatusText = statusWhenPicked;
            }
        }

        private async Task InvokeExportHandlerOrSavePickAsync(Func<Task>? handler, string title, string suggestedFileName)
        {
            if (handler != null)
            {
                await handler();
                return;
            }

            var path = await _dialogService.PickSaveFileAsync(new SaveFilePickerRequest(title, suggestedFileName, "fbx"));
            if (!string.IsNullOrWhiteSpace(path))
            {
                StatusText = $"Selected export target: {path}";
            }
        }

        private void ExecuteFilterTypeAll()
        {
            if (CommandHandlers.FilterTypeAll != null)
            {
                CommandHandlers.FilterTypeAll();
                return;
            }

            AssetList.SelectAllClassTypes();
        }

        private void ExecuteToggleFilterType(ClassIDType type)
        {
            if (CommandHandlers.ToggleFilterType != null)
            {
                CommandHandlers.ToggleFilterType(type);
                return;
            }

            AssetList.ToggleClassType(type);
        }

        private void ExecutePauseIndexing()
        {
            _loadingService.PauseIndexing();
            if (IndexingProgress.CanPause)
            {
                IndexingProgress.Pause();
            }
            IsPauseEnabled = false;
            IsResumeEnabled = true;
            StatusText = "Indexing paused.";
            CommandHandlers.PauseIndexing?.Invoke();
        }

        private void ExecuteResumeIndexing()
        {
            _loadingService.ResumeIndexing();
            if (IndexingProgress.CanResume)
            {
                IndexingProgress.Resume();
            }
            IsPauseEnabled = true;
            IsResumeEnabled = false;
            StatusText = "Resuming indexing...";
            CommandHandlers.ResumeIndexing?.Invoke();
        }

        private void ExecuteStopIndexing()
        {
            _loadingService.StopIndexing();
            if (IndexingProgress.CanCancel)
            {
                IndexingProgress.BeginCancelling();
            }
            IsIndexingActive = false;
            IsPauseEnabled = false;
            IsResumeEnabled = false;
            IsStopEnabled = false;
            StatusText = "Stopping/cancelling indexing...";
            CommandHandlers.StopIndexing?.Invoke();
        }

        private void ExecuteClearClassFilter()
        {
            if (CommandHandlers.ClearClassFilter != null)
            {
                CommandHandlers.ClearClassFilter();
                return;
            }

            AssetList.ClearClassFilterOverride();
        }

        private async Task ExecuteCopyAssetCellTextAsync()
        {
            if (CommandHandlers.CopyAssetCellTextAsync != null)
            {
                await CommandHandlers.CopyAssetCellTextAsync();
                return;
            }

            var text = AssetList.PrimarySelectedItem?.Name ?? string.Empty;
            if (await _dialogService.CopyTextToClipboardAsync(text))
            {
                StatusText = "Copied asset cell text.";
            }
        }

        private void ExecuteGoToSceneHierarchy()
        {
            if (CommandHandlers.GoToSceneHierarchy != null)
            {
                CommandHandlers.GoToSceneHierarchy();
                return;
            }

            if (!SceneHierarchy.TryNavigateToAssetNode(AssetList.PrimarySelectedItem))
            {
                StatusText = SceneHierarchy.SearchStatusText;
            }
        }

        private async Task ExecuteShowOriginalFileAsync()
        {
            if (CommandHandlers.ShowOriginalFileAsync != null)
            {
                await CommandHandlers.ShowOriginalFileAsync();
                return;
            }

            var path = AssetList.PrimarySelectedItem?.SourceFile?.originalPath
                ?? AssetList.PrimarySelectedItem?.Handle?.OriginalPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                await _dialogService.RevealFileInExplorerAsync(path);
            }
        }

        private void ExecuteShowClassInstances()
        {
            CommandHandlers.ShowClassInstances?.Invoke();
        }

        private void ExecuteSetTextAssetDisplayMode(TextAssetDisplayMode mode)
        {
            if (CommandHandlers.SetTextAssetDisplayMode != null)
            {
                CommandHandlers.SetTextAssetDisplayMode(mode);
                return;
            }

            PreviewSession.SetDialogueDisplayMode(mode);
        }

        private void ExecuteToggleAnimationPlayPause()
        {
            if (CommandHandlers.ToggleAnimationPlayPause != null)
            {
                CommandHandlers.ToggleAnimationPlayPause();
                return;
            }

            MeshPreview.TogglePlayPause();
        }

        private void ExecuteRestartAnimation()
        {
            if (CommandHandlers.RestartAnimation != null)
            {
                CommandHandlers.RestartAnimation();
                return;
            }

            MeshPreview.Restart();
        }

        private void ExecuteResetMeshView()
        {
            MeshPreview.ViewPreset = MeshPreviewViewPreset.Auto;
            CommandHandlers.ResetMeshView?.Invoke();
        }

        private void ExecuteRotateMeshLeft() => CommandHandlers.RotateMeshLeft?.Invoke();

        private void ExecuteRotateMeshRight() => CommandHandlers.RotateMeshRight?.Invoke();

        private void ExecuteRotateMeshUp() => CommandHandlers.RotateMeshUp?.Invoke();

        private void ExecuteRotateMeshDown() => CommandHandlers.RotateMeshDown?.Invoke();

        private void ExecuteResetMeshMaterialColor()
        {
            if (CommandHandlers.ResetMeshMaterialColor != null)
            {
                CommandHandlers.ResetMeshMaterialColor();
                return;
            }

            var slot = MeshPreview.SelectedMaterialSlot;
            if (slot != null)
            {
                slot.CurrentColor = new global::OpenTK.Mathematics.Vector4(
                    slot.BaseColor.X,
                    slot.BaseColor.Y,
                    slot.BaseColor.Z,
                    slot.CurrentColor.W);
            }
        }

        private async Task ExecuteExportPreviewModelAsync()
        {
            if (CommandHandlers.ExportPreviewModelAsync != null)
            {
                await CommandHandlers.ExportPreviewModelAsync();
                return;
            }

            if (!CanExportPreviewModel)
            {
                return;
            }

            var baseName = !string.IsNullOrWhiteSpace(MeshPreview.ActiveMeshName)
                ? MeshPreview.ActiveMeshName
                : !string.IsNullOrWhiteSpace(MeshPreview.ActiveAvatarName)
                    ? MeshPreview.ActiveAvatarName
                    : PreviewSession.ActiveAsset?.Name ?? "model";
            var suggestedFileName = AssetExportService.FixFileName(baseName) + ".fbx";
            var path = await _dialogService.PickSaveFileAsync(new SaveFilePickerRequest("Export Model", suggestedFileName, "fbx"));
            if (!string.IsNullOrWhiteSpace(path))
            {
                StatusText = $"Selected export target: {path}";
            }
        }

        private async Task ExecuteNavigateToReferencedAssetAsync(AssetReferenceItem? target)
        {
            if (target == null || !target.IsNavigable || _disposed)
            {
                return;
            }

            if (CommandHandlers.NavigateToReferencedAssetAsync != null)
            {
                await CommandHandlers.NavigateToReferencedAssetAsync(target);
                return;
            }

            PreviewSession.ActiveRightTab = RightPanelTab.Preview;
            StatusText = $"Navigating to {target.TypeName}: {target.Name}";
        }

        private async Task ExecuteVideoPlayPauseAsync()
        {
            if (CommandHandlers.VideoPlayPauseAsync != null)
            {
                await CommandHandlers.VideoPlayPauseAsync();
                return;
            }

            if (MediaPlayback.IsPlaying)
            {
                MediaPlayback.SetPaused();
            }
            else if (MediaPlayback.IsVideoActive)
            {
                MediaPlayback.SetPlaying();
            }
        }

        private async Task ExecuteVideoStopAsync()
        {
            if (CommandHandlers.VideoStopAsync != null)
            {
                await CommandHandlers.VideoStopAsync();
                return;
            }

            if (MediaPlayback.IsVideoActive)
            {
                MediaPlayback.SetStopped();
            }
        }

        private async Task ExecuteVideoExportAsync()
        {
            if (CommandHandlers.VideoExportAsync != null)
            {
                await CommandHandlers.VideoExportAsync();
                return;
            }

            if (!MediaPlayback.IsVideoActive)
            {
                return;
            }

            var folder = await _dialogService.PickOpenFolderAsync(new FolderPickerRequest("Select the save folder"));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                StatusText = $"Ready to export video to: {folder}";
            }
        }

        private async Task ExecuteAudioPlayAsync()
        {
            if (CommandHandlers.AudioPlayAsync != null)
            {
                await CommandHandlers.AudioPlayAsync();
                return;
            }

            if (MediaPlayback.IsAudioActive)
            {
                MediaPlayback.SetPlaying();
            }
        }

        private void ExecuteAudioPause()
        {
            if (CommandHandlers.AudioPause != null)
            {
                CommandHandlers.AudioPause();
                return;
            }

            if (!MediaPlayback.IsAudioActive)
            {
                return;
            }

            if (MediaPlayback.IsPlaying)
            {
                MediaPlayback.SetPaused();
            }
            else if (MediaPlayback.IsPaused)
            {
                MediaPlayback.SetPlaying();
            }
        }

        private void ExecuteAudioStop()
        {
            if (CommandHandlers.AudioStop != null)
            {
                CommandHandlers.AudioStop();
                return;
            }

            if (MediaPlayback.IsAudioActive)
            {
                MediaPlayback.SetStopped();
            }
        }

        private void ExecuteCycleWireframeMode()
        {
            var mode = MeshPreview.ToggleWireframe();
            CommandHandlers.WireframeModeChanged?.Invoke(mode);
        }

        private void ExecuteToggleShadeMode()
        {
            var mode = MeshPreview.ToggleShadeMode();
            CommandHandlers.ShadeModeChanged?.Invoke(mode);
        }

        private void ExecuteToggleNormalMode()
        {
            MeshPreview.ToggleNormals();
            CommandHandlers.NormalModeChanged?.Invoke(MeshPreview.NormalMode);
        }

        private void ExecuteToggleShowSkeleton()
        {
            var visible = MeshPreview.ToggleShowSkeleton();
            CommandHandlers.ShowSkeletonChanged?.Invoke(visible);
        }

        private void ExecuteToggleTextureChannel(PreviewChannelMask channel)
        {
            PreviewSession.ToggleChannel(channel);
            CommandHandlers.TextureChannelToggled?.Invoke(channel);
        }

        public void ResetSession(bool keepSavedProjects = true)
        {
            _loadingService.StopIndexing();
            AssetList.Reset();
            IndexingProgress.Reset();
            PreviewSession.Reset();
            PreviewSession.IsPreviewEnabled = Options.EnablePreview;
            PreviewSession.IsDisplayInfoEnabled = Options.DisplayInfo;
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
            IsReturningToProjectManager = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IndexingProgress.PropertyChanged -= OnIndexingProgressPropertyChanged;
            PreviewSession.PropertyChanged -= OnPreviewSessionPropertyChanged;
            MeshPreview.PropertyChanged -= OnMeshPreviewPropertyChanged;
            Options.EnablePreviewChanged -= OnOptionsEnablePreviewChanged;
            Options.DisplayInfoChanged -= OnOptionsDisplayInfoChanged;
            Options.SpecifyUnityVersionChanged -= OnOptionsSpecifyUnityVersionChanged;
            CommandHandlers.Clear();
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
            _isReturningToProjectManager = false;
            OnPropertyChanged(nameof(IsReturningToProjectManager));
            OnPropertyChanged(nameof(CanOpenProjectManager));
            OnPropertyChanged(nameof(CanExportPreviewModel));
            OnPropertyChanged(nameof(CanNavigateToSelectedReference));
            OpenProjectManagerCommand.NotifyCanExecuteChanged();
            ExportPreviewModelCommand.NotifyCanExecuteChanged();
            NavigateToSelectedReferenceCommand.NotifyCanExecuteChanged();
            NavigateToReferencedAssetCommand.NotifyCanExecuteChanged();
        }

        private sealed class NoOpDialogService : IDialogService
        {
            public static readonly NoOpDialogService Instance = new();

            public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerRequest request)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            public Task<string?> PickOpenFolderAsync(FolderPickerRequest request)
                => Task.FromResult<string?>(null);

            public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request)
                => Task.FromResult<string?>(null);

            public Task ShowMessageAsync(string title, string message)
                => Task.CompletedTask;

            public Task ShowErrorAsync(string title, string message)
                => Task.CompletedTask;

            public Task<bool> ConfirmAsync(string title, string message)
                => Task.FromResult(false);

            public Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message)
                => Task.FromResult(RiskyLoadChoice.Cancel);

            public Task<MemoryPressureResult> ShowMemoryPressureDialogAsync(string title, string message)
                => Task.FromResult(MemoryPressureResult.Continue);

            public Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings)
                => Task.FromResult(false);

            public Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath)
                => Task.FromResult<ProjectManagerDialogResult?>(null);

            public Task<bool> CopyTextToClipboardAsync(string text)
                => Task.FromResult(false);

            public Task<bool> OpenPathInExplorerAsync(string path)
                => Task.FromResult(false);

            public Task<bool> OpenUrlAsync(string url)
                => Task.FromResult(false);
        }
    }
}
