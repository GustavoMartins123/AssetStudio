using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using System.Runtime.InteropServices;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Diagnostics;
using AssetStudio;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpegVideoPlayer.Core;
using AssetStudio.Avalonia.ViewModels;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    private AssetsManager assetsManager = new AssetsManager();
    private List<AssetItem> _eagerExportableAssets
    {
        get => ViewModel.AssetList.EagerExportableAssets;
        set => ViewModel.AssetList.EagerExportableAssets = value;
    }
    private List<AssetItem> exportableAssets
    {
        get => ViewModel.AssetList.GetExportableAssetsSnapshot();
        set => ViewModel.AssetList.EagerExportableAssets = value;
    }
    private Texture2D? currentPreviewTexture;
    private Sprite? currentPreviewSprite;
    private Mesh? currentPreviewMesh;
    private Avatar? currentPreviewAvatar;
    private VideoClip? currentPreviewVideoClip;
    private bool useGpuTexturePreview = true;
    private readonly bool[] textureChannels = new bool[4] { true, true, true, true };
    private long texturePreviewIdCounter;
    private CancellationTokenSource? previewDebounce;
    private const int PreviewDebounceMilliseconds = 180;
    private const int MaxInlinePreviewTextureDimension = 1024;
    private const int MaxCachedPreviewTextureDimension = 512;
    private const int TexturePreviewThumbnailAlgorithmVersion = 2;
    private const int ProgressiveIndexingUiThrottleMilliseconds = 500;
    private const int CachedIndexLoadBatchSize = 2500;
    private const int UserInteractionPriorityMilliseconds = 1200;
    private const int UserPreviewPriorityMilliseconds = 1800;
    private const int UserInteractionYieldDelayMilliseconds = 40;
    private long userInteractionPriorityUntilTimestamp;
    private List<AssetItem> _eagerVisibleAssets => ViewModel.AssetList.EagerVisibleAssets;
    private IReadOnlyList<AssetItem> visibleAssets => ViewModel.AssetList.GetVisibleAssetsView();
    private VirtualAssetList visibleAssetItems => ViewModel.AssetList.VisibleItems;
    private List<AssetClassItem> assetClassItems
    {
        get => ViewModel.AssetList.AllAssetClasses.ToList();
        set => ViewModel.AssetList.SetAssetClasses(value);
    }
    private BulkObservableCollection<AssetClassItem> visibleAssetClassItems => ViewModel.AssetList.AssetClasses;

    private AssetClassItem? classFilterOverride
    {
        get => ViewModel.AssetList.ClassFilterOverride;
        set => ViewModel.AssetList.ClassFilterOverride = value;
    }
    private List<GameObjectNode> sceneTreeNodes = new List<GameObjectNode>();
    private readonly List<GameObjectNode> treeSearchResults = new List<GameObjectNode>();
    private readonly ExportOptionsState exportOptions = new();
    private readonly AvaloniaAppSettings appSettings;
    private readonly ProjectLaunchContext? projectContext;
    private readonly AssemblyLoader assemblyLoader = new AssemblyLoader();
    private readonly GUILogger logger;
    private string? assetListSortMember
    {
        get => ViewModel.AssetList.SortColumn;
        set => ViewModel.AssetList.SortColumn = value;
    }
    private string assetContextCellText = string.Empty;
    private AssetItem? assetContextItem;
    private int nextGameObjectSearchIndex;
    private bool assetListSortDescending
    {
        get => ViewModel.AssetList.SortDescending;
        set => ViewModel.AssetList.SortDescending = value;
    }
    private bool updatingFilterTypeMenu;
    private Dictionary<Mesh, List<Material?>>? meshToMaterialsCache;
    private Dictionary<Mesh, List<string>>? meshAssociatedRenderersCache;
    private Dictionary<Mesh, HashSet<string>>? meshSourceTypesCache;
    private Dictionary<Material, Texture2D?>? materialMainTextureCache;
    private Dictionary<Material, Material?>? materialPreviewMaterialCache;
    private Dictionary<Material, Dictionary<string, Texture2D?>>? materialTextureSlotsCache;
    private Dictionary<AssetStudio.Object, AssetItem>? objectToAssetItemCache;
    private Dictionary<AnimationClip, Avatar?>? animationClipAvatarCache;
    private Dictionary<Avatar, Mesh?>? avatarMeshCache;
    private Dictionary<Avatar, List<Mesh>>? avatarMeshesCache;
    private Dictionary<Mesh, Avatar?>? meshAvatarCache;
    private Dictionary<string, List<SkinnedMeshRenderer>>? meshSkinnedRenderersCache;
    private Dictionary<AnimationClip, HashSet<uint>>? animationClipTransformBindingsCache;
    private readonly object previewCacheLock = new object();

    private readonly SQLiteProjectIndexCache _sqliteCache = new();
    private ProjectScanResult? currentScanResult;
    private bool isBuildingAssetStructures;
    private bool isBuildingLazyConnections;
    private long lastConnectionDbWriteTicks;
    private readonly ConcurrentDictionary<string, string> lazySourcePathBySerializedFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> lazySourceFileSearchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> resolvedSourcePathCache = new(StringComparer.OrdinalIgnoreCase);

    private string? _pendingStatusText;
    private string? _currentlySelectedUniqueID;
    private bool isRefreshingClassesList
    {
        get => ViewModel.AssetList.IsRefreshingClasses;
        set => ViewModel.AssetList.IsRefreshingClasses = value;
    }
    private bool _statusUpdatePending;
    private Dictionary<AssetHandleKey, AssetItem> lazyAssetItemsByHandleId
    {
        get => ViewModel.AssetList.LazyAssetItemsByHandleId;
        set => ViewModel.AssetList.LazyAssetItemsByHandleId = value;
    }
    private HashSet<AssetHandleKey> exportableAssetHandleIds
    {
        get => ViewModel.AssetList.ExportableAssetHandleIds;
        set => ViewModel.AssetList.ExportableAssetHandleIds = value;
    }
    private HashSet<ClassIDType> exportableAssetTypes
    {
        get => ViewModel.AssetList.ExportableAssetTypes;
        set => ViewModel.AssetList.ExportableAssetTypes = value;
    }
    private long lazyIndexingSessionId;
    private bool projectAutoIndexStarted;
    private int foregroundLazyLoadCount;
    private int _projectSessionTornDown;
    private bool _isReturningToProjectManager;
    private CancellationTokenSource? _progressiveScanCts;
    private bool IsWindowClosingOrClosed => Volatile.Read(ref _projectSessionTornDown) != 0;
    private readonly Func<bool> _shouldYieldForUserInteractionDelegate;
    private readonly Func<string, int, int, MemoryPressureResult> _memoryPressureCallback;
    private readonly IProgress<int> _progressReporter;
    private readonly EventHandler<SizeChangedEventArgs> _previewContentHostSizeChangedHandler;
    private readonly EventHandler<RoutedEventArgs> _specifyUnityVersionLostFocusHandler;
    private readonly Action<string> _textureGlGpuErrorHandler;
    private readonly Action<string> _glGpuErrorHandler;
    private readonly Action<int, int> _glAnimationFrameChangedHandler;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(ProjectLaunchContext? projectContext)
    {
        var loadingService = new AssetLoadingService();
        var dialogService = new AvaloniaDialogService(() => this);
        ViewModel = new MainWindowViewModel(
            loadingService,
            dialogService: dialogService,
            dispatchToUi: static action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    action();
                }
                else
                {
                    Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
                }
            });
        DataContext = ViewModel;

        this.projectContext = projectContext;
        appSettings = projectContext?.Settings ?? ProjectManagerStore.Shared.LoadGlobalSettings();
        ViewModel.Options.LoadFromSettings(appSettings);
        if (projectContext?.Project != null)
        {
            ViewModel.ProjectSession.SetLoadedProject(projectContext.Project, isLazyMode: assetsManager.LazyLoading);
        }
        else
        {
            ViewModel.ProjectSession.UpdateWindowTitle(null);
        }
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        InitializeComponent();
        AssetListDataGrid.ItemsSource = visibleAssetItems;
        AssetClassesDataGrid.ItemsSource = visibleAssetClassItems;
        GLPreviewControl.LoadMeshPreviewGeometryCache = LoadMeshPreviewGeometryCache;
        GLPreviewControl.SaveMeshPreviewGeometryCache = SaveMeshPreviewGeometryCache;
        _previewContentHostSizeChangedHandler = (_, _) => Update2DAnimationImageLayout();
        PreviewContentHost.SizeChanged += _previewContentHostSizeChangedHandler;
        PreviewContentHost.PointerWheelChanged += ImagePreviewBox_PointerWheelChanged;
        ImagePreviewBox.PointerWheelChanged += ImagePreviewBox_PointerWheelChanged;
        InitializeTheme();
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://AssetStudio.Avalonia/Assets/as.png"));
            Icon = new WindowIcon(new Bitmap(iconStream));
        }
        catch { }
        logger = new GUILogger(StatusStripUpdate, this);
        logger.ShowErrorMessage = ViewModel.Options.ShowErrorMessage;
        Logger.Default = logger;
        exportOptions.CopyFrom(appSettings.ExportOptions);
        _specifyUnityVersionLostFocusHandler = (_, _) => ApplyUnityVersionOption();
        SpecifyUnityVersionTextBox.LostFocus += _specifyUnityVersionLostFocusHandler;
        ApplyUnityVersionOption();
        WireViewModelCommandsAndOptions();
        assetsManager.ProjectRoot = appSettings.ProjectRoot;
        _shouldYieldForUserInteractionDelegate = ShouldPauseBackgroundWork;
        AssetsManager.ShouldYieldForUserInteraction = _shouldYieldForUserInteractionDelegate;
        assetsManager.ShouldKeepFileCallback = fileName =>
        {
            lock (preloaderLock)
            {
                return preloadedUniqueIds.Any(uid => uid.StartsWith(fileName + "#")) 
                    || (_currentlySelectedUniqueID != null && _currentlySelectedUniqueID.StartsWith(fileName + "#"));
            }
        };
        _progressReporter = new Progress<int>(SetProgressBarValue);
        Progress.Default = _progressReporter;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);

        _memoryPressureCallback = (operation, loadPercent, limitPercent) =>
        {
            if (IsWindowClosingOrClosed)
            {
                return MemoryPressureResult.StopAndKeep;
            }
            if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                return MemoryPressureResult.Continue;
            }
            return global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (IsWindowClosingOrClosed)
                {
                    return MemoryPressureResult.StopAndKeep;
                }
                var msg = $"Memory usage has reached {loadPercent}% (limit: {limitPercent}%) during {operation}.\n\n" +
                          "Continuing may slow down your system or cause it to run out of memory.\n\n" +
                          "What would you like to do?";
                return await ShowMemoryPressureWarningDialog(msg);
            }).GetAwaiter().GetResult();
        };
        AssetsManager.MemoryPressureCallback = _memoryPressureCallback;

        VideoProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, VideoProgressBar_DragStarted);
        VideoProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, VideoProgressBar_DragCompleted);
        InitializeVideoPlayer();
        FfmpegVideoPlayer.MediaEnded += FfmpegVideoPlayer_MediaEnded;
        FfmpegVideoPlayer.IsVisible = true;

        try
        {
            _audioMediaPlayer = CreateAudioMediaPlayer();
            _audioTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _audioTimer.Tick += AudioTimer_Tick;
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to initialize FFmpeg audio player: {ex.Message}");
        }

        // Detect GPU support
        try
        {
            var locatorType = typeof(global::Avalonia.Application).Assembly.GetType("Avalonia.AvaloniaLocator");
            var currentProp = locatorType?.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var currentLocator = currentProp?.GetValue(null);
            if (currentLocator != null)
            {
                var getServiceMethod = currentLocator.GetType().GetMethod("GetService");
                var openGlInterfaceType = typeof(global::Avalonia.Application).Assembly.GetType("Avalonia.OpenGL.IPlatformOpenGlInterface");
                if (getServiceMethod != null && openGlInterfaceType != null)
                {
                    var glInterface = getServiceMethod.MakeGenericMethod(openGlInterfaceType).Invoke(currentLocator, null);
                    if (glInterface == null)
                    {
                        useGpuTexturePreview = false;
                        logger.Log(LoggerEvent.Info, "GPU acceleration (OpenGL) not supported: platform OpenGl interface is null. Falling back to CPU.");
                    }
                    else
                    {
                        useGpuTexturePreview = true;
                        logger.Log(LoggerEvent.Info, "GPU acceleration (OpenGL) detected.");
                    }
                }
                else
                {
                    useGpuTexturePreview = true;
                }
            }
            else
            {
                useGpuTexturePreview = true;
            }
        }
        catch (Exception ex)
        {
            useGpuTexturePreview = true;
            logger.Log(LoggerEvent.Warning, $"GPU acceleration detection failed: {ex.Message}. Defaulting to GPU preview.");
        }

        _textureGlGpuErrorHandler = (errMsg) =>
        {
            if (IsWindowClosingOrClosed)
            {
                return;
            }
            logger.Log(LoggerEvent.Warning, $"GPU texture preview error: {errMsg}. Falling back to CPU.");
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsWindowClosingOrClosed)
                {
                    return;
                }
                if (errMsg.Contains("initialization") || errMsg.Contains("link error") || errMsg.Contains("compile error"))
                {
                    useGpuTexturePreview = false;
                }
                UpdateImagePreview(forceCpu: true);
            });
        };
        if (TextureGLPreview != null)
        {
            TextureGLPreview.GpuErrorOccurred += _textureGlGpuErrorHandler;
        }

        _glGpuErrorHandler = (errMsg) =>
        {
            if (!IsWindowClosingOrClosed)
            {
                logger.Log(LoggerEvent.Warning, $"GPU mesh preview error: {errMsg}.");
            }
        };
        _glAnimationFrameChangedHandler = (currentFrame, totalFrames) =>
        {
            if (IsWindowClosingOrClosed)
            {
                return;
            }
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsWindowClosingOrClosed)
                {
                    UpdateAnimationPlaybackFrame(currentFrame, totalFrames);
                }
            });
        };
        if (GLPreviewControl != null)
        {
            GLPreviewControl.GpuErrorOccurred += _glGpuErrorHandler;
            GLPreviewControl.AnimationFrameChanged += _glAnimationFrameChangedHandler;
            GLPreviewControl.MeshGeometryLoaded += OnMeshPreviewGeometryLoaded;
        }

        ApplyAvatarPreviewControlSettings();
    }

    private void WireViewModelCommandsAndOptions()
    {
        var handlers = ViewModel.CommandHandlers;

        handlers.LoadFileAsync = LoadFileCommandHandlerAsync;
        handlers.LoadFolderAsync = LoadFolderCommandHandlerAsync;
        handlers.LoadFolderEagerAsync = LoadFolderEagerCommandHandlerAsync;
        handlers.ExtractFileAsync = ExtractFileCommandHandlerAsync;
        handlers.ExtractFolderAsync = ExtractFolderCommandHandlerAsync;
        handlers.OpenProjectManagerAsync = ReturnToProjectManagerCommandHandlerAsync;

        handlers.SetProjectRootAsync = SetProjectRootCommandHandlerAsync;
        handlers.ShowExportOptionsAsync = ShowExportOptionsCommandHandlerAsync;
        handlers.ClearProjectCacheAsync = ClearCurrentProjectCacheCommandHandlerAsync;

        handlers.ExportAllObjectsSplitAsync = ExportAllObjectsSplitCommandHandlerAsync;
        handlers.ExportSelectedObjectsSplitAsync = () => ExportSelectedObjectsSplit(false);
        handlers.ExportSelectedObjectsSplitWithAnimationClipAsync = () => ExportSelectedObjectsSplit(true);
        handlers.ExportSelectedObjectsMergeAsync = () => ExportSelectedObjectsMerge(false);
        handlers.ExportSelectedObjectsMergeWithAnimationClipAsync = () => ExportSelectedObjectsMerge(true);

        handlers.ExportAllAssetsAsync = () => ExportAllAssetsAsync(ExportMode.Convert);
        handlers.ExportSelectedAssetsAsync = () => ExportAssets(GetSelectedAssets(), ExportMode.Convert);
        handlers.ExportFilteredAssetsAsync = () => ExportFilteredAssetsAsync(ExportMode.Convert);
        handlers.ExportModelGroupFbxAsync = ExportSelectedModelGroupFbxAsync;
        handlers.ExportAnimatorWithSelectedAnimationClipAsync = () => ExportAnimatorWithSelectedAnimationClips(GetSelectedAssets());
        handlers.ExportAllAssetsRawAsync = () => ExportAllAssetsAsync(ExportMode.Raw);
        handlers.ExportSelectedAssetsRawAsync = () => ExportAssets(GetSelectedAssets(), ExportMode.Raw);
        handlers.ExportFilteredAssetsRawAsync = () => ExportFilteredAssetsAsync(ExportMode.Raw);
        handlers.ExportAllAssetsDumpAsync = () => ExportAllAssetsAsync(ExportMode.Dump);
        handlers.ExportSelectedAssetsDumpAsync = () => ExportAssets(GetSelectedAssets(), ExportMode.Dump);
        handlers.ExportFilteredAssetsDumpAsync = () => ExportFilteredAssetsAsync(ExportMode.Dump);
        handlers.ExportAllAssetsXmlAsync = ExportAllAssetsListAsync;
        handlers.ExportSelectedAssetsXmlAsync = () => ExportAssetsList(GetSelectedAssets());
        handlers.ExportFilteredAssetsXmlAsync = ExportFilteredAssetsListAsync;
        handlers.ExportErrorLogAsync = ExportErrorLogCommandHandlerAsync;

        handlers.FilterTypeAll = ExecuteFilterTypeAllFromUi;
        handlers.ToggleFilterType = ExecuteToggleFilterTypeFromUi;
        handlers.ExportClassStructuresAsync = ExportClassStructuresCommandHandlerAsync;

        handlers.PauseIndexing = () => BuildAssetStructures(incremental: true);

        handlers.ClearClassFilter = ExecuteClearClassFilter;
        handlers.CopyAssetCellTextAsync = CopyAssetCellTextCommandHandlerAsync;
        handlers.GoToSceneHierarchy = ExecuteGoToSceneHierarchy;
        handlers.ShowOriginalFileAsync = ShowOriginalFileCommandHandlerAsync;
        handlers.ShowClassInstances = ExecuteShowClassInstances;

        handlers.SetTextAssetDisplayMode = ExecuteSetTextAssetDisplayMode;
        handlers.ToggleAnimationPlayPause = ExecuteToggleAnimationPlayPause;
        handlers.RestartAnimation = ExecuteRestartAnimation;
        handlers.ResetMeshView = ExecuteResetMeshView;
        handlers.RotateMeshLeft = ExecuteRotateMeshLeft;
        handlers.RotateMeshRight = ExecuteRotateMeshRight;
        handlers.RotateMeshUp = ExecuteRotateMeshUp;
        handlers.RotateMeshDown = ExecuteRotateMeshDown;
        handlers.ResetMeshMaterialColor = ExecuteResetMeshMaterialColor;

        handlers.VideoPlayPauseAsync = ExecuteVideoPlayPauseAsync;
        handlers.VideoStopAsync = VideoStopAsync;
        handlers.VideoExportAsync = ExecuteVideoExportAsync;
        handlers.AudioPlayAsync = ExecuteAudioPlayAsync;
        handlers.AudioPause = ExecuteAudioPause;
        handlers.AudioStop = AudioStop;

        handlers.WireframeModeChanged = mode =>
        {
            if (GLPreviewControl != null)
            {
                GLPreviewControl.WireframeMode = mode;
            }
        };
        handlers.ShadeModeChanged = mode =>
        {
            if (GLPreviewControl != null)
            {
                GLPreviewControl.ShadeMode = mode;
            }
        };
        handlers.NormalModeChanged = mode =>
        {
            if (GLPreviewControl != null)
            {
                GLPreviewControl.NormalMode = mode;
            }
        };
        handlers.TextureChannelToggled = _ =>
        {
            PreviewSession.CopyChannelsTo(textureChannels);
            UpdateImagePreview();
        };

        ViewModel.Options.DisplayAllChanged += OnDisplayAllOptionChanged;
        ViewModel.Options.EnablePreviewChanged += OnEnablePreviewOptionChanged;
        ViewModel.Options.DisplayInfoChanged += OnDisplayInfoOptionChanged;
        ViewModel.Options.UseDiskDecompressionCacheChanged += OnUseDiskDecompressionCacheOptionChanged;
        ViewModel.Options.ShowErrorMessageChanged += OnShowErrorMessageOptionChanged;
        ViewModel.Options.ThemeChanged += OnThemeOptionChanged;
    }

    private void UnwireViewModelOptions()
    {
        ViewModel.Options.DisplayAllChanged -= OnDisplayAllOptionChanged;
        ViewModel.Options.EnablePreviewChanged -= OnEnablePreviewOptionChanged;
        ViewModel.Options.DisplayInfoChanged -= OnDisplayInfoOptionChanged;
        ViewModel.Options.UseDiskDecompressionCacheChanged -= OnUseDiskDecompressionCacheOptionChanged;
        ViewModel.Options.ShowErrorMessageChanged -= OnShowErrorMessageOptionChanged;
        ViewModel.Options.ThemeChanged -= OnThemeOptionChanged;
    }

    private async Task LoadFileCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        var files = await PickOpenFilesDialogAsync("Select Game File", true);
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        if (files.Count > 0)
        {
            var filePaths = files.ToArray();
            currentScanResult = null;
            SaveLoadFolder(filePaths[0]);
            ResetForm();
            StatusStripUpdate("Loading files...");
            assetsManager.Clear();
            assetsManager.LazyLoading = false;
            assetsManager.DecompressionCache = null;
            ApplyUnityVersionOption();
            try
            {
                await Task.Run(() => assetsManager.LoadFiles(filePaths));
            }
            catch (MemoryPressureException ex)
            {
                if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
                ShowMemoryPressureError(ex);
                return;
            }
            catch (Exception ex)
            {
                if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
                await ViewModel.DialogService.ShowErrorAsync("Load failed", $"Error loading files:\n{ex.Message}");
                StatusStripUpdate("Load failed.");
                return;
            }

            if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
            BuildAssetStructures();
        }
    }

    private async Task LoadFolderCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        var folderPath = await PickLoadFolderDialogAsync("Select Game Folder");
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            await BeginProgressiveLoadAsync(new[] { folderPath }, "Indexing folder");
        }
    }

    private async Task LoadFolderEagerCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        var folderPath = await PickLoadFolderDialogAsync("Select Game Folder (Full / Scene Hierarchy)");
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            await LoadFolderEagerAsync(folderPath);
        }
    }

    private async Task LoadFolderEagerAsync(string folderPath)
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        var choice = await ConfirmFolderLoadIfRisky(folderPath);
        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;

        if (choice == RiskyLoadChoice.Cancel)
        {
            StatusStripUpdate("Folder load cancelled.");
            return;
        }

        if (choice == RiskyLoadChoice.LazyLoad)
        {
            LoadPathsProgressiveAsync(new[] { folderPath });
            return;
        }

        SaveLoadFolder(folderPath);
        ResetForm();
        StatusStripUpdate("Loading folder (Full / Scene Hierarchy)...");
        assetsManager.Clear();
        assetsManager.LazyLoading = false;
        assetsManager.DecompressionCache = null;
        assetsManager.ProjectRoot = !string.IsNullOrWhiteSpace(appSettings.ProjectRoot)
            ? appSettings.ProjectRoot
            : folderPath;
        ApplyUnityVersionOption();
        StartProjectAnalysisAsync(folderPath);
        try
        {
            await Task.Run(() => assetsManager.LoadFolderAsync(folderPath));
        }
        catch (MemoryPressureException ex)
        {
            if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
            ShowMemoryPressureError(ex);
            return;
        }
        catch (Exception ex)
        {
            if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
            MessageBox.Show(this, $"Error loading folder:\n{ex.Message}", "Load failed");
            StatusStripUpdate("Folder load failed.");
            return;
        }

        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
        await BuildAssetStructuresAsync();
    }

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(x => x.Path.LocalPath)
            .Where(x => File.Exists(x) || Directory.Exists(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths == null || paths.Length == 0) return;

        await LoadDroppedPaths(paths);
    }

    private async Task LoadDroppedPaths(string[] paths)
    {
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            await BeginProgressiveLoadAsync(paths, "Indexing dropped folder");
            return;
        }

        if (paths.Any(Directory.Exists))
        {
            await BeginProgressiveLoadAsync(paths, "Indexing dropped paths");
            return;
        }

        currentScanResult = null;
        ResetForm();
        assetsManager.Clear();
        assetsManager.LazyLoading = false;
        assetsManager.DecompressionCache = null;
        ApplyUnityVersionOption();
        StatusStripUpdate("Loading dropped files...");

        try
        {
            await Task.Run(() => assetsManager.LoadFiles(paths));
        }
        catch (MemoryPressureException ex)
        {
            if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
            ShowMemoryPressureError(ex);
            return;
        }

        if (IsWindowClosingOrClosed || _isReturningToProjectManager) return;
        BuildAssetStructures();
    }

    private void CancelProgressiveScan()
    {
        var previous = Interlocked.Exchange(ref _progressiveScanCts, null);
        if (previous == null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch
        {
        }

        previous.Dispose();
    }

    private async Task BeginProgressiveLoadAsync(string[] paths, string statusPrefix)
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        currentScanResult = null;
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            currentScanResult = await ScanFolderForProgressiveIndexAsync(paths[0], statusPrefix);
        }

        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        LoadPathsProgressiveAsync(paths);
    }

    private async Task<ProjectScanResult?> ScanFolderForProgressiveIndexAsync(string folderPath, string statusPrefix)
    {
        CancelProgressiveScan();
        var scanCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _progressiveScanCts, scanCts);
        previousCts?.Dispose();
        var token = scanCts.Token;

        StatusStripUpdate($"{statusPrefix}: scanning folder...");
        var scanProgress = new Progress<ScanProgress>(p =>
        {
            if (token.IsCancellationRequested || IsWindowClosingOrClosed)
            {
                return;
            }
            if (p.TotalFiles > 0)
            {
                StatusStripUpdate($"{statusPrefix}: scanning... {p.ScannedFiles:N0}/{p.TotalFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
            else
            {
                StatusStripUpdate($"{statusPrefix}: scanning... {p.ScannedFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
        });

        try
        {
            var scanResult = await Task.Run(() => ProjectScanner.ScanFolder(folderPath, token, scanProgress), token);
            if (!token.IsCancellationRequested && !IsWindowClosingOrClosed)
            {
                StatusStripUpdate($"{statusPrefix}: scan complete. {scanResult.TotalFiles:N0} files, {FormatBytes(scanResult.TotalBytes)}, {scanResult.UnityBundleCount:N0} bundles.");
            }
            return scanResult;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!IsWindowClosingOrClosed)
            {
                Logger.Warning($"{statusPrefix}: folder scan failed; continuing without SQLite cache validation. {ex.Message}");
                StatusStripUpdate($"{statusPrefix}: scan failed; indexing without cache validation.");
            }
            return null;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _progressiveScanCts, null, scanCts), scanCts))
            {
                scanCts.Dispose();
            }
        }
    }

    internal static List<string> ResolveColdIndexFiles(
        string folderPath,
        ProjectScanResult? scanResult,
        Action<string>? mergeSplitAssets = null,
        Func<string, string[]>? enumerateFiles = null)
    {
        var mergeAction = mergeSplitAssets ?? (static path => ImportHelper.MergeSplitAssets(path, true));
        var enumerateFunc = enumerateFiles ?? (static path => ImportHelper.GetFilesSafe(path, "*.*", true));

        if (scanResult != null && scanResult.Files.Count > 0)
        {
            var hasSplitFiles = false;
            var scannedPaths = new string[scanResult.Files.Count];
            for (var i = 0; i < scanResult.Files.Count; i++)
            {
                var filePath = scanResult.Files[i].Path;
                scannedPaths[i] = filePath;
                if (!hasSplitFiles && filePath.EndsWith(".split0", StringComparison.OrdinalIgnoreCase))
                {
                    hasSplitFiles = true;
                }
            }

            if (!hasSplitFiles)
            {
                return ImportHelper.ProcessingSplitFiles(scannedPaths).ToList();
            }
        }

        mergeAction(folderPath);
        var enumerated = enumerateFunc(folderPath);
        return ImportHelper.ProcessingSplitFiles(enumerated).ToList();
    }

    private async void LoadPathsProgressiveAsync(string[] paths)
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        ResetForm();
        assetsManager.Clear();
        assetsManager.LazyLoading = true;
        _isArenaAssetListMode = true;
        ApplyUnityVersionOption();
        StatusStripUpdate("Indexing assets with SQLite cache...");

        string cacheTargetFolder = "";
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            cacheTargetFolder = paths[0];
        }
        else if (paths.Length > 0)
        {
            cacheTargetFolder = Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? string.Empty;
        }

        if (!string.IsNullOrEmpty(cacheTargetFolder))
        {
            BundleFile.CacheDirectory = string.Empty;
            assetsManager.DecompressionCache = appSettings.UseDiskDecompressionCache
                ? new DecompressedBundleCache(gameScopePath: cacheTargetFolder)
                : null;
            StartProjectAnalysisAsync(cacheTargetFolder);
        }
        else
        {
            BundleFile.CacheDirectory = string.Empty;
            assetsManager.DecompressionCache = null;
        }

        var sessionId = lazyIndexingSessionId;
        try
        {
            if (paths.Length == 1 && Directory.Exists(paths[0]))
            {
                var folderPath = paths[0];
                SaveLoadFolder(folderPath);

                if (currentScanResult != null)
                {
                    var previousIndexingState = TryLoadIndexingProgress(folderPath, currentScanResult);
                    if (previousIndexingState != null
                        && !IsCompletedLazyIndexingStatus(previousIndexingState.Status))
                    {
                        ViewModel.IndexingProgress.SetIncompletePreviousSession(previousIndexingState);
                        StatusStripUpdate(ViewModel.IndexingProgress.IncompleteSessionSummary);
                    }

                    // Warm-open fast path: check mmap/.idx & SQLite cache directly into ProjectIndex.Arena BEFORE any second disk walk.
                    var displayAllChecked = ViewModel.Options.DisplayAll;
                    var filterText = listSearch?.Text?.Trim();
                    var classFilter = classFilterOverride;
                    var filterTypeChecked = filterTypeAll.IsChecked != true;
                    var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
                        .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
                        .Select(x => (ClassIDType)x.Tag!)
                        .ToHashSet() : null;
                    var sortMember = assetListSortMember;
                    var sortDescending = assetListSortDescending;
                    var scanResultSnapshot = currentScanResult;
                    var signature = _sqliteCache.GetFolderSignature(scanResultSnapshot);

                    StatusStripUpdate("Loading project index from cache...");
                    var warmResult = await Task.Run(() =>
                    {
                        if (!_sqliteCache.LoadIndexCacheIntoProjectIndex(
                            folderPath,
                            signature,
                            assetsManager.ProjectIndex,
                            out var loadedSources,
                            out var missingPaths))
                        {
                            return (Loaded: false, MissingPaths: false, ExportableIndices: (List<int>?)null, ExportableBitset: (ulong[]?)null, ExportableTypes: (HashSet<ClassIDType>?)null, ClassItems: (List<AssetClassItem>?)null, VisibleIndices: (int[]?)null);
                        }

                        if (missingPaths)
                        {
                            assetsManager.ProjectIndex.Clear();
                            return (Loaded: false, MissingPaths: true, ExportableIndices: (List<int>?)null, ExportableBitset: (ulong[]?)null, ExportableTypes: (HashSet<ClassIDType>?)null, ClassItems: (List<AssetClassItem>?)null, VisibleIndices: (int[]?)null);
                        }

                        if (loadedSources != null)
                        {
                            for (var i = 0; i < loadedSources.Count; i++)
                            {
                                var src = loadedSources[i];
                                if (!string.IsNullOrWhiteSpace(src.OriginalPath))
                                {
                                    RememberLazySourcePathForSerializedFile(src.SerializedFileName, src.OriginalPath);
                                    RememberLazySourcePathForSerializedFile(GetSafeFileName(src.OriginalPath), src.OriginalPath);
                                }
                            }
                        }

                        for (var i = 0; i < scanResultSnapshot.Files.Count; i++)
                        {
                            RememberLazySourcePath(scanResultSnapshot.Files[i].Path);
                        }

                        // Drain pending indices enqueued during bulk arena load so later preview loads only see new handles.
                        _ = assetsManager.ProjectIndex.DrainPendingIndices();

                        var arena = assetsManager.ProjectIndex.Arena;
                        var totalCount = arena.Count;
                        var localExportable = new List<int>(totalCount);
                        var bitset = new ulong[(totalCount + 63) >> 6];
                        var localTypes = new HashSet<ClassIDType>();

                        for (var idx = 0; idx < totalCount; idx++)
                        {
                            var type = arena.GetType(idx);
                            if (!displayAllChecked && !IsLazyExportableType(type))
                            {
                                continue;
                            }

                            localExportable.Add(idx);
                            bitset[idx >> 6] |= 1UL << (idx & 63);
                            localTypes.Add(type);
                        }

                        var classItems = AssetListQuery.BuildArenaAssetClassItems(arena);
                        var exportableSnapshot = localExportable.ToArray();
                        var visible = AssetListQuery.FilterArena(arena, exportableSnapshot, exportableSnapshot.Length, classFilter, selectedTypes, filterText, CancellationToken.None);
                        AssetListQuery.SortArenaInPlace(arena, visible, visible.Length, sortMember, sortDescending);

                        return (Loaded: true, MissingPaths: false, ExportableIndices: localExportable, ExportableBitset: bitset, ExportableTypes: localTypes, ClassItems: classItems, VisibleIndices: visible);
                    });

                    if (sessionId != lazyIndexingSessionId)
                    {
                        return;
                    }

                    if (warmResult.MissingPaths)
                    {
                        Logger.Info("SQLite index cache is missing source paths; rebuilding it once.");
                    }
                    else if (warmResult.Loaded && warmResult.ExportableIndices != null && warmResult.VisibleIndices != null)
                    {
                        ViewModel.IsIndexingActive = true;
                        ViewModel.IsPauseEnabled = false;
                        ViewModel.IsResumeEnabled = false;
                        ViewModel.IsStopEnabled = false;

                        try
                        {
                            var isSorted = !string.IsNullOrEmpty(sortMember);
                            var isIdentity = !isSorted && IsIdentityArenaIndices(warmResult.VisibleIndices, warmResult.VisibleIndices.Length);
                            var isStrictlyIncreasing = !isSorted && (isIdentity || IsStrictlyIncreasingIndices(warmResult.VisibleIndices, warmResult.VisibleIndices.Length));

                            ViewModel.AssetList.ApplyWarmArenaState(
                                assetsManager.ProjectIndex,
                                warmResult.ExportableIndices,
                                warmResult.ExportableBitset,
                                warmResult.ExportableTypes,
                                warmResult.ClassItems,
                                warmResult.VisibleIndices,
                                isStrictlyIncreasing,
                                isIdentity);

                            BuildFilterTypeMenu();
                            EnsureAssetListItemsSource();
                            FilterAssetClasses();

                            ViewModel.LoadingProgress = 100;
                            StatusStripUpdate($"Loaded from cache. Showing {VisibleAssetCount:N0} assets.");

                            await BuildLazyConnectionsIfNeededAsync(paths);
                            if (sessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                            {
                                return;
                            }

                            if (ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(paths))
                            {
                                return;
                            }

                            if (HasCompletedLazyStructureBuild(folderPath, scanResultSnapshot))
                            {
                                var completedStructureState = TryLoadIndexingProgress(folderPath, scanResultSnapshot);
                                if (completedStructureState != null)
                                {
                                    ShowIndexingProgressPanel(completedStructureState);
                                }
                                SaveCurrentProjectAfterLoad(null, assetsManager.ProjectIndex.Count, ExportableAssetCount);
                                return;
                            }

                            await BuildAssetStructuresAsync(incremental: true, showStructureProgress: true);
                            return;
                        }
                        finally
                        {
                            if (!IsWindowClosingOrClosed)
                            {
                                ViewModel.IsIndexingActive = false;
                                ViewModel.IsPauseEnabled = false;
                                ViewModel.IsResumeEnabled = false;
                                ViewModel.IsStopEnabled = false;
                            }
                        }
                    }
                }

                // Cold indexing path: reuse scanResult.Files whenever there are no .split0 files to merge.
                var coldFiles = await Task.Run(() => ResolveColdIndexFiles(folderPath, currentScanResult));
                if (sessionId != lazyIndexingSessionId)
                {
                    return;
                }

                if (coldFiles.Count == 0)
                {
                    StatusStripUpdate("No Unity files found.");
                    return;
                }

                foreach (var file in coldFiles)
                {
                    RememberLazySourcePath(file);
                }

                StartProgressiveIndexing(coldFiles, paths);
                return;
            }

            var targetFolder = Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? string.Empty;
            if (!string.IsNullOrEmpty(targetFolder))
            {
                SaveLoadFolder(targetFolder);
                await Task.Run(() => ImportHelper.MergeSplitAssets(targetFolder, false));
            }

            var list = paths
                .SelectMany(path => Directory.Exists(path)
                    ? ImportHelper.GetFilesSafe(path, "*.*", true)
                    : new[] { path })
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var files = await Task.Run(() => ImportHelper.ProcessingSplitFiles(list).ToList());

            if (sessionId != lazyIndexingSessionId)
            {
                return;
            }

            if (files.Count == 0)
            {
                StatusStripUpdate("No Unity files found.");
                return;
            }

            foreach (var file in files)
            {
                RememberLazySourcePath(file);
            }

            StartProgressiveIndexing(files, paths);
        }
        catch (Exception ex)
        {
            if (IsWindowClosingOrClosed || sessionId != lazyIndexingSessionId)
            {
                return;
            }
            MessageBox.Show(this, $"Error during progressive load:\n{ex.Message}", "Load failed");
            StatusStripUpdate("Progressive load failed.");
        }
    }

    private void StartProgressiveIndexing(List<string> files, string[] paths)
    {
        ViewModel.IsIndexingActive = true;
        ViewModel.IsPauseEnabled = true;
        ViewModel.IsResumeEnabled = false;
        ViewModel.IsStopEnabled = true;
        ShowIndexingProgressPanel("running", 0, files.Count, files.Count, 0, string.Empty, string.Empty, null);

        var indexingSessionId = lazyIndexingSessionId;
        var activeFilters = filterTypeAll.IsChecked != true
            ? GetFilterTypeItems()
                .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
                .Select(x => (ClassIDType)x.Tag!)
                .ToList()
            : new List<ClassIDType>();

        ViewModel.LoadingService.StartProgressiveIndexing(
            assetsManager,
            files,
            paths,
            currentScanResult,
            activeFilters,
            ShouldPauseBackgroundWork,
            WaitForUserInteractionPriorityToClearAsync,
            (progressPercent, statusText) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (indexingSessionId != lazyIndexingSessionId)
                    {
                        return;
                    }

                    ViewModel.LoadingProgress = progressPercent;
                    var addedAssets = AppendNewLazyAssetsFromProjectIndex();
                    StatusStripUpdate(addedAssets > 0
                        ? $"{statusText} | Showing {VisibleAssetCount:N0} assets (+{addedAssets:N0})"
                        : $"{statusText} | Showing {VisibleAssetCount:N0} assets");
                });
            },
            (ex) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed)
                    {
                        return;
                    }
                    ShowMemoryPressureError(ex);
                    ViewModel.IsPauseEnabled = false;
                    ViewModel.IsResumeEnabled = true;
                    StatusStripUpdate("Indexing paused due to high memory pressure.");
                });
            },
            () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (indexingSessionId == lazyIndexingSessionId)
                    {
                        AppendNewLazyAssetsFromProjectIndex();
                    }
                });
            },
            (folderPath, scanResult) =>
            {
                if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                {
                    return;
                }
                SaveIndexCache(folderPath, scanResult);
            },
            update =>
            {
                if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                {
                    return;
                }
                TrySaveIndexingProgress(paths, update);
                ShowIndexingProgressPanel(update);
            },
            (wasCancelled, finalLoadedCount, originalTotal) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                    {
                        return;
                    }

                    try
                    {
                        ViewModel.IsIndexingActive = false;
                        var finalPercent = originalTotal == 0 ? 100 : (int)((double)finalLoadedCount / originalTotal * 100);
                        ViewModel.LoadingProgress = wasCancelled ? finalPercent : 100;
                        StatusStripUpdate(wasCancelled
                            ? $"Indexing cancelled. Indexed: {finalLoadedCount:N0} / {originalTotal:N0} files ({finalPercent}%)"
                            : $"Indexing finished. Total files: {originalTotal:N0}");
                        AppendNewLazyAssetsFromProjectIndex();

                        if (!wasCancelled)
                        {
                            await BuildLazyConnectionsIfNeededAsync(paths, force: true);
                            if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                            {
                                return;
                            }
                            if (ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(paths))
                            {
                                return;
                            }
                        }

                        if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                        {
                            return;
                        }

                        await BuildAssetStructuresAsync(incremental: true, showStructureProgress: !wasCancelled);
                    }
                    catch (Exception ex)
                    {
                        if (indexingSessionId != lazyIndexingSessionId || IsWindowClosingOrClosed || _isReturningToProjectManager)
                        {
                            return;
                        }
                        Logger.Error($"Failed to finish lazy indexing: {ex}", ex);
                        StatusStripUpdate("Lazy indexing finalization failed.");
                    }
                });
            },
            onHandleBatchReady: batch =>
            {
                if (batch == null || batch.Count == 0)
                {
                    return;
                }

                var arenaIndices = new int[batch.Count];
                var validCount = 0;
                string? lastOriginalPath = null;
                string? lastSerializedFile = null;
                for (var i = 0; i < batch.Count; i++)
                {
                    var handle = batch[i];
                    if (handle == null)
                    {
                        continue;
                    }

                    var originalPath = handle.OriginalPath;
                    var serializedFile = handle.SerializedFileName;
                    if (!string.Equals(originalPath, lastOriginalPath, StringComparison.Ordinal)
                        || !string.Equals(serializedFile, lastSerializedFile, StringComparison.Ordinal))
                    {
                        RememberLazyHandleSourcePath(handle);
                        lastOriginalPath = originalPath;
                        lastSerializedFile = serializedFile;
                    }

                    var arenaIndex = handle.ArenaIndex;
                    if (arenaIndex < 0 && handle.TryGetKey(out var key))
                    {
                        assetsManager.ProjectIndex.TryGetIndex(key, out arenaIndex);
                    }

                    if (arenaIndex >= 0)
                    {
                        arenaIndices[validCount++] = arenaIndex;
                    }
                }

                if (validCount == 0)
                {
                    return;
                }

                if (validCount < arenaIndices.Length)
                {
                    Array.Resize(ref arenaIndices, validCount);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (indexingSessionId == lazyIndexingSessionId)
                    {
                        AppendLazyArenaIndices(arenaIndices);
                    }
                });
            },
            handleFlushBatchSize: AssetLoadingService.DefaultHandleFlushBatchSize);
    }

    private int AppendNewLazyAssetsFromProjectIndex(int? maxHandlesToDrain = null)
    {
        if (!assetsManager.LazyLoading)
        {
            return 0;
        }

        var pendingIndices = maxHandlesToDrain.HasValue
            ? assetsManager.ProjectIndex.DrainPendingIndices(maxHandlesToDrain.Value)
            : assetsManager.ProjectIndex.DrainPendingIndices();

        return AppendLazyArenaIndices(pendingIndices);
    }

    private int AppendLazyArenaIndices(ReadOnlySpan<int> pendingIndices)
    {
        if (!assetsManager.LazyLoading || pendingIndices.IsEmpty)
        {
            return 0;
        }

        EnsureAssetListItemsSource();
        SyncFilterInputsToAssetListViewModel();
        var addedCount = ViewModel.AssetList.AppendLazyArenaIndices(
            pendingIndices,
            ViewModel.Options.DisplayAll,
            IsLazyExportableType,
            (serializedFileName, originalPath) =>
            {
                RememberLazySourcePathForSerializedFile(serializedFileName, originalPath);
                RememberLazySourcePathForSerializedFile(GetSafeFileName(originalPath), originalPath);
            },
            out var filterTypesChanged);

        if (filterTypesChanged)
        {
            BuildFilterTypeMenu();
        }

        if (addedCount > 0)
        {
            StatusStripUpdate(ViewModel.AssetList.StatusSummary);
        }

        return addedCount;
    }

    private static void UpdateLazyAssetItemHandle(AssetItem assetItem, AssetHandle handle)
    {
        assetItem.Handle = handle;
        assetItem.SourceFile = handle.SourceFile;
        assetItem.Type = handle.Type;
        assetItem.PathID = handle.PathID;
        assetItem.Size = handle.ByteSize;
        assetItem.FullSize = handle.ByteSize;
        assetItem.Name = handle.Name ?? string.Empty;
        assetItem.Container = handle.Container ?? string.Empty;
    }

    private async void BuildAssetStructures(bool incremental = false)
    {
        await BuildAssetStructuresAsync(incremental);
    }

    private async Task BuildAssetStructuresAsync(bool incremental = false, bool showStructureProgress = false)
    {
        if (isBuildingAssetStructures || IsWindowClosingOrClosed || _isReturningToProjectManager) return;
        isBuildingAssetStructures = true;
        var structureSessionId = lazyIndexingSessionId;
        var isLazyStructure = assetsManager.LazyLoading;
        var publishStructureProgress = showStructureProgress && isLazyStructure && currentScanResult != null;
        const int structureBuildSteps = 6;

        bool IsStructureAborted() =>
            IsWindowClosingOrClosed
            || _isReturningToProjectManager
            || (isLazyStructure && structureSessionId != lazyIndexingSessionId);

        void PublishStructureStage(int processedSteps, string stage)
        {
            if (publishStructureProgress && !IsStructureAborted())
            {
                PublishStructureBuildProgress("building_structure", processedSteps, structureBuildSteps, stage);
            }
        }

        try
        {
            if (assetsManager.assetsFileList.Count == 0 && (!assetsManager.LazyLoading || assetsManager.ProjectIndex.Count == 0))
            {
                StatusStripUpdate("No Unity file can be loaded.");
                return;
            }

            StatusStripUpdate("Building asset structures...");
            PublishStructureStage(0, "Preparing asset structure build");

        // Capture required UI states on the UI thread
        bool displayAllChecked = ViewModel.Options.DisplayAll;
        List<SerializedFile> filesListSnapshot;
        lock (assetsManager.loadLock)
        {
            filesListSnapshot = assetsManager.assetsFileList.ToList();
        }

        if (assetsManager.LazyLoading)
        {
            var lazyResult = await Task.Run(() =>
            {
                PublishStructureStage(1, "Collecting containers and arena indices");
                var localContainers = new List<(PPtr<Object>, string)>();
                foreach (var assetsFile in filesListSnapshot)
                {
                    YieldBackgroundWorkForUserInteraction();

                    foreach (var asset in assetsFile.Objects)
                    {
                        if (asset is AssetBundle m_AssetBundle)
                        {
                            CollectSafeAssetBundleContainers(m_AssetBundle, localContainers);
                        }
                        else if (asset is ResourceManager m_ResourceManager)
                        {
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                localContainers.Add((m_Container.Value, m_Container.Key));
                            }
                        }
                    }
                }

                PublishStructureStage(2, "Linking container paths");
                var projectIndex = assetsManager.ProjectIndex;
                var arena = projectIndex.Arena;
                foreach ((var pptr, var container) in localContainers)
                {
                    if (pptr.TryGetAssetsFile(out var targetFile))
                    {
                        var targetKey = AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID);
                        if (projectIndex.TryGetIndexByUniqueId(targetKey, out var targetIdx))
                        {
                            var handle = arena.MaterializeHandle(targetIdx);
                            handle.Container = container;
                            if (handle.Type == ClassIDType.Material && string.IsNullOrEmpty(handle.Name))
                            {
                                var name = Path.GetFileNameWithoutExtension(container);
                                if (!string.IsNullOrEmpty(name))
                                {
                                    handle.Name = name;
                                }
                            }
                        }
                    }
                }
                localContainers.Clear();

                PublishStructureStage(3, "Indexing arena records");
                _ = projectIndex.DrainPendingIndices();
                var totalCount = arena.Count;
                var localExportable = new List<int>(totalCount);
                var localBitset = new ulong[(totalCount + 63) >> 6];
                var localTypes = new HashSet<ClassIDType>();

                for (var idx = 0; idx < totalCount; idx++)
                {
                    var type = arena.GetType(idx);
                    if (!displayAllChecked && !IsLazyExportableType(type))
                    {
                        continue;
                    }

                    localExportable.Add(idx);
                    localBitset[idx >> 6] |= 1UL << (idx & 63);
                    localTypes.Add(type);
                }

                PublishStructureStage(4, "Building class structures");
                List<AssetClassItem> localAssetClassItems;
                if (filesListSnapshot.Count > 0)
                {
                    localAssetClassItems = new List<AssetClassItem>();
                    var objectCounts = filesListSnapshot
                        .SelectMany(file => file.m_Objects.Select(obj => new { file.unityVersion, ClassID = (int)obj.classID }))
                        .GroupBy(x => (x.unityVersion, x.ClassID))
                        .ToDictionary(x => x.Key, x => x.Count());

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var assetsFile in filesListSnapshot)
                    {
                        YieldBackgroundWorkForUserInteraction();
                        AddSerializedTypesBackground(assetsFile, assetsFile.m_Types, "Native", objectCounts, seen, localAssetClassItems);
                        AddSerializedTypesBackground(assetsFile, assetsFile.m_RefTypes, "Reference", objectCounts, seen, localAssetClassItems);
                    }

                    localAssetClassItems = localAssetClassItems
                        .OrderBy(x => x.UnityVersion, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.ClassID)
                        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    localAssetClassItems = AssetListQuery.BuildArenaAssetClassItems(arena);
                }

                return (
                    ExportableIndices: localExportable,
                    ExportableBitset: localBitset,
                    ExportableTypes: localTypes,
                    AssetClassItems: localAssetClassItems);
            });

            if (IsStructureAborted()) return;
            await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
            if (IsStructureAborted()) return;
            PublishStructureStage(5, "Applying asset structure to UI");

            var previousExportableCount = ViewModel.AssetList.TotalAssetCount;
            var needsFullRefresh = !incremental
                || previousExportableCount == 0
                || lazyResult.ExportableIndices.Count != previousExportableCount
                || ViewModel.AssetList.HasPendingArenaCoalescedSort;

            ViewModel.AssetList.SetArenaAssets(
                assetsManager.ProjectIndex,
                lazyResult.ExportableIndices,
                lazyResult.ExportableBitset,
                lazyResult.ExportableTypes,
                lazyResult.AssetClassItems,
                applyFilterImmediately: false);

            BuildFilterTypeMenu();
            if (needsFullRefresh)
            {
                await FilterAssetListAsync(CancellationToken.None);
                if (IsStructureAborted()) return;
                FilterAssetClasses();
            }
            else
            {
                UpdateAssetClassesIncremental(lazyResult.AssetClassItems);
            }

            if (IsStructureAborted()) return;
            var lazyLog = $"Finished loading {assetsManager.assetsFileList.Count} files with {ExportableAssetCount:N0} exportable assets";
            var lazyObjCount = assetsManager.ProjectIndex.Count;
            if (lazyObjCount > 0)
            {
                lazyLog += $" ({lazyObjCount:N0} indexed in arena)";
            }
            StatusStripUpdate(lazyLog);

            if (assetsManager.assetsFileList.Count > 0)
            {
                ViewModel.ProjectSession.UpdateWindowTitle(null, assetsManager.assetsFileList[0]);
            }
            else
            {
                ViewModel.ProjectSession.UpdateWindowTitle(projectContext?.Project.DisplayName);
            }

            PublishStructureStage(6, "Saving project metadata");
            var finalExportableCount = ExportableAssetCount;
            await Task.Run(() => SaveCurrentProjectAfterLoad(null, lazyObjCount, finalExportableCount));
            if (IsStructureAborted()) return;

            if (publishStructureProgress)
            {
                var folderPath = GetCurrentCacheFolderPath();
                if (currentScanResult != null && HasSavedSemanticRelations(folderPath, currentScanResult))
                {
                    PublishStructureBuildProgress("structure_completed", structureBuildSteps, structureBuildSteps, "Asset structure ready");
                }
                else
                {
                    PublishStructureBuildProgress("structure_failed", structureBuildSteps, structureBuildSteps, "Asset structure built without saved connections");
                }
            }

            return;
        }

        var result = await Task.Run(() =>
        {
            PublishStructureStage(1, "Collecting containers and asset items");
            string? localProductName = null;
            var localExportableAssets = new List<AssetItem>();
            var localSceneTreeNodes = new List<GameObjectNode>();
            
            var localTreeNodeDictionary = new Dictionary<GameObject, GameObjectNode>();
            var localObjectAssetItemDic = new Dictionary<Object, AssetItem>();
            var localPathIDAssetItemDic = new Dictionary<string, AssetItem>();
            var localContainers = new List<(PPtr<Object>, string)>();
            var localNewExportableAssets = new List<AssetItem>();

            BuildEagerAssetItemsBackground(
                filesListSnapshot,
                displayAllChecked,
                localTreeNodeDictionary,
                localObjectAssetItemDic,
                localPathIDAssetItemDic,
                localContainers,
                localExportableAssets,
                localSceneTreeNodes,
                out localProductName);

            PublishStructureStage(2, "Linking asset items");
            LinkAssetItemsToSceneNodesBackground(filesListSnapshot, localTreeNodeDictionary, localObjectAssetItemDic);

            foreach ((var pptr, var container) in localContainers)
            {
                if (pptr.TryGetAssetsFile(out var targetFile))
                {
                    var targetKey = AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID);
                    if (localPathIDAssetItemDic.TryGetValue(targetKey, out var item))
                    {
                        item.Container = container;
                        if (item.Handle != null)
                        {
                            item.Handle.Container = container;
                        }

                        if (item.Type == ClassIDType.Material && string.IsNullOrEmpty(item.Name))
                        {
                            var name = Path.GetFileNameWithoutExtension(container);
                            if (!string.IsNullOrEmpty(name))
                            {
                                item.Name = name;
                                if (item.Handle != null)
                                {
                                    item.Handle.Name = name;
                                }
                            }
                        }
                    }
                }
            }

            LinkFbxSubAssetsToSceneNodesBackground(localExportableAssets, localSceneTreeNodes);
            localContainers.Clear();

            var localObjectToAssetItemCache = new Dictionary<AssetStudio.Object, AssetItem>();
            var localMeshToMaterialsCache = new Dictionary<Mesh, List<Material?>>();
            var localMeshAssociatedRenderersCache = new Dictionary<Mesh, List<string>>();
            var localMeshSourceTypesCache = new Dictionary<Mesh, HashSet<string>>();
            var localMaterialMainTextureCache = new Dictionary<Material, Texture2D?>();
            var localMaterialPreviewMaterialCache = new Dictionary<Material, Material?>();
            var localMaterialTextureSlotsCache = new Dictionary<Material, Dictionary<string, Texture2D?>>();
            var localSemanticRelations = new SemanticAssetRelations();

            var localAnimationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>();
            var localAvatarMeshCache = new Dictionary<Avatar, Mesh?>();
            var localMeshAvatarCache = new Dictionary<Mesh, Avatar?>();
            var localAnimationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>();

            PublishStructureStage(3, "Building reference indexes");
            Parallel.Invoke(
                CreateStructureBuildParallelOptions(),
                () => BuildAssetReferenceIndexesBackground(
                    filesListSnapshot,
                    localExportableAssets,
                    out localObjectToAssetItemCache,
                    out localMeshToMaterialsCache,
                    out localMeshAssociatedRenderersCache,
                    out localMeshSourceTypesCache,
                    out localMaterialMainTextureCache,
                    out localMaterialPreviewMaterialCache,
                    out localMaterialTextureSlotsCache,
                    out localSemanticRelations),
                () => BuildAnimationPreviewIndexesBackground(
                    filesListSnapshot,
                    out localAnimationClipAvatarCache,
                    out localAvatarMeshCache,
                    out localMeshAvatarCache,
                    out localAnimationClipTransformBindingsCache));

            PublishStructureStage(4, "Building class structures");
            var localAssetClassItems = new List<AssetClassItem>();
            var objectCounts = filesListSnapshot
                .SelectMany(file => file.m_Objects.Select(obj => new { file.unityVersion, ClassID = (int)obj.classID }))
                .GroupBy(x => (x.unityVersion, x.ClassID))
                .ToDictionary(x => x.Key, x => x.Count());

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assetsFile in filesListSnapshot)
            {
                YieldBackgroundWorkForUserInteraction();
                AddSerializedTypesBackground(assetsFile, assetsFile.m_Types, "Native", objectCounts, seen, localAssetClassItems);
                AddSerializedTypesBackground(assetsFile, assetsFile.m_RefTypes, "Reference", objectCounts, seen, localAssetClassItems);
            }

            localAssetClassItems = localAssetClassItems
                .OrderBy(x => x.UnityVersion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ClassID)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            BuildExportableAssetIndexesBackground(
                localExportableAssets,
                out var localLazyAssetItemsByHandleId,
                out var localExportableAssetHandleIds,
                out var localExportableAssetTypes);

            return new BuildAssetStructuresResult
            {
                ProductName = localProductName,
                ExportableAssets = localExportableAssets,
                NewExportableAssets = localNewExportableAssets,
                SceneTreeNodes = localSceneTreeNodes,
                ObjectToAssetItemCache = localObjectToAssetItemCache,
                MeshToMaterialsCache = localMeshToMaterialsCache,
                MeshAssociatedRenderersCache = localMeshAssociatedRenderersCache,
                MeshSourceTypesCache = localMeshSourceTypesCache,
                MaterialMainTextureCache = localMaterialMainTextureCache,
                MaterialPreviewMaterialCache = localMaterialPreviewMaterialCache,
                MaterialTextureSlotsCache = localMaterialTextureSlotsCache,
                SemanticRelations = localSemanticRelations,
                AnimationClipAvatarCache = localAnimationClipAvatarCache,
                AvatarMeshCache = localAvatarMeshCache,
                MeshAvatarCache = localMeshAvatarCache,
                AnimationClipTransformBindingsCache = localAnimationClipTransformBindingsCache,
                AssetClassItems = localAssetClassItems,
                LazyAssetItemsByHandleId = localLazyAssetItemsByHandleId,
                ExportableAssetHandleIds = localExportableAssetHandleIds,
                ExportableAssetTypes = localExportableAssetTypes
            };
        });

        if (IsStructureAborted()) return;
        await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
        if (IsStructureAborted()) return;
        PublishStructureStage(5, "Applying asset structure to UI");

        // Apply results back on the UI thread
        _isArenaAssetListMode = false;
        var newExportableAssets = result.NewExportableAssets;
        bool useIncrementalPath = incremental && _eagerExportableAssets.Count > 0 && newExportableAssets != null;

        if (useIncrementalPath && newExportableAssets!.Count == 0)
        {
            _eagerExportableAssets = result.ExportableAssets;
            ApplyExportableAssetIndexes(result);
            assetClassItems = result.AssetClassItems;
            BuildFilterTypeMenu();
            UpdateAssetClassesIncremental(result.AssetClassItems);
        }
        else if (useIncrementalPath)
        {
            // Incremental path: only append new items to avoid O(n) DataGrid notifications
            _eagerExportableAssets = result.ExportableAssets;
            ApplyExportableAssetIndexes(result);
            assetClassItems = result.AssetClassItems;

            BuildFilterTypeMenu();
            AppendFilteredAssetsToVisible(newExportableAssets!);
            UpdateAssetClassesIncremental(result.AssetClassItems);
        }
        else
        {
            // Full rebuild path (initial load, display all toggle, etc.)
            _eagerExportableAssets = result.ExportableAssets;
            ApplyExportableAssetIndexes(result);
            sceneTreeNodes = result.SceneTreeNodes;
            treeSearchSession.Invalidate();
            appliedTreeSearchMatches = null;
            treeSearchResults.Clear();
            nextGameObjectSearchIndex = 0;
            objectToAssetItemCache = result.ObjectToAssetItemCache;
            meshToMaterialsCache = result.MeshToMaterialsCache;
            meshAssociatedRenderersCache = result.MeshAssociatedRenderersCache;
            meshSourceTypesCache = result.MeshSourceTypesCache;
            materialMainTextureCache = result.MaterialMainTextureCache;
            materialPreviewMaterialCache = result.MaterialPreviewMaterialCache;
            materialTextureSlotsCache = result.MaterialTextureSlotsCache;
            animationClipAvatarCache = result.AnimationClipAvatarCache;
            avatarMeshCache = result.AvatarMeshCache;
            avatarMeshesCache = BuildAvatarMeshListCache(result.AvatarMeshCache);
            meshAvatarCache = result.MeshAvatarCache;
            animationClipTransformBindingsCache = result.AnimationClipTransformBindingsCache;
            assetClassItems = result.AssetClassItems;

            BuildFilterTypeMenu();
            _ = FilterAssetListAsync(CancellationToken.None);
            FilterAssetClasses();
            SceneTreeView.ItemsSource = sceneTreeNodes;
        }

        var log = $"Finished loading {assetsManager.assetsFileList.Count} files with {ExportableAssetCount} exportable assets";
        var m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.Count);
        var objectsCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
        if (m_ObjectsCount != objectsCount)
        {
            var deferredCount = m_ObjectsCount - objectsCount;
            log += $" and {deferredCount:N0} assets failed to read";
        }
        StatusStripUpdate(log);

        if (assetsManager.assetsFileList.Count > 0)
        {
            ViewModel.ProjectSession.UpdateWindowTitle(result.ProductName, assetsManager.assetsFileList[0]);
        }
        else
        {
            ViewModel.ProjectSession.UpdateWindowTitle(projectContext?.Project.DisplayName);
        }

        PublishStructureStage(6, "Saving project metadata");
        var eagerExportableCount = ExportableAssetCount;
        await Task.Run(() => SaveCurrentProjectAfterLoad(result.ProductName, m_ObjectsCount, eagerExportableCount));
        if (IsStructureAborted()) return;
        await Task.Run(() => TrySaveSemanticRelations(result.SemanticRelations));
        if (IsStructureAborted()) return;
        if (publishStructureProgress)
        {
            PublishStructureBuildProgress("structure_completed", structureBuildSteps, structureBuildSteps, "Asset structure ready");
        }
        }
        catch
        {
            if (IsStructureAborted())
            {
                return;
            }
            if (publishStructureProgress)
            {
                PublishStructureBuildProgress("structure_failed", structureBuildSteps, structureBuildSteps, "Asset structure build failed");
            }
            throw;
        }
        finally
        {
            isBuildingAssetStructures = false;
        }
    }

    private void SaveCurrentProjectAfterLoad(string? productName, int assetCount, int exportableAssetCount)
    {
        if (projectContext == null)
        {
            return;
        }

        try
        {
            var scanSignature = currentScanResult == null ? null : _sqliteCache.GetFolderSignature(currentScanResult);
            projectContext.Store.UpdateProjectAfterLoad(
                projectContext.Project.Id,
                currentScanResult,
                scanSignature,
                productName,
                assetCount,
                exportableAssetCount);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to update project metadata: {ex.Message}");
        }
    }

    internal static void CollectSafeAssetBundleContainers(
        AssetBundle? assetBundle,
        List<(PPtr<Object>, string)> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        if (assetBundle?.m_Container == null || assetBundle.m_PreloadTable == null)
        {
            return;
        }

        var preloadTable = assetBundle.m_PreloadTable;
        foreach (var m_Container in assetBundle.m_Container)
        {
            var assetInfo = m_Container.Value;
            if (assetInfo == null)
            {
                continue;
            }

            var preloadIndex = assetInfo.preloadIndex;
            var preloadSize = assetInfo.preloadSize;
            if (preloadIndex < 0 || preloadSize <= 0 || preloadIndex >= preloadTable.Length)
            {
                continue;
            }

            long preloadEnd = Math.Min((long)preloadIndex + preloadSize, preloadTable.Length);
            for (int k = preloadIndex; k < preloadEnd; k++)
            {
                containers.Add((preloadTable[k], m_Container.Key));
            }
        }
    }

    internal static string BuildSafeClassStructurePath(string savePath, string? unityVersion, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            throw new ArgumentException("Save path must not be empty.", nameof(savePath));
        }

        var rootFullPath = Path.GetFullPath(savePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var safeVersion = FixFileName(unityVersion);
        if (string.IsNullOrWhiteSpace(safeVersion) || safeVersion is "." or "..")
        {
            throw new IOException($"Blocked invalid Unity version directory segment: '{unityVersion}'");
        }

        var safeTypeName = FixFileName(typeName);
        if (string.IsNullOrWhiteSpace(safeTypeName) || safeTypeName is "." or "..")
        {
            throw new IOException($"Blocked invalid class structure file name: '{typeName}'");
        }

        var fileName = safeTypeName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? safeTypeName
            : safeTypeName + ".txt";

        var versionDirectory = Path.GetFullPath(Path.Combine(rootFullPath, safeVersion));
        var versionDirectoryWithSeparator = versionDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!versionDirectoryWithSeparator.StartsWith(rootFullPath, comparison)
            || string.Equals(versionDirectoryWithSeparator, rootFullPath, comparison))
        {
            throw new IOException($"Blocked class structure version directory outside destination root: '{unityVersion}'");
        }

        var fullPath = Path.GetFullPath(Path.Combine(versionDirectory, fileName));
        if (!fullPath.StartsWith(versionDirectoryWithSeparator, comparison)
            || !fullPath.StartsWith(rootFullPath, comparison))
        {
            throw new IOException($"Blocked class structure output path outside destination root: '{unityVersion}/{typeName}'");
        }

        return fullPath;
    }

    internal static int ExportClassStructuresToDisk(
        string savePath,
        IEnumerable<(string UnityVersion, string TypeName, string Content)> items,
        Action<int, int>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            throw new ArgumentException("Save path must not be empty.", nameof(savePath));
        }

        ArgumentNullException.ThrowIfNull(items);

        var itemList = items as IReadOnlyList<(string UnityVersion, string TypeName, string Content)> ?? items.ToList();
        var totalCount = itemList.Count;
        var exportedCount = 0;

        foreach (var item in itemList)
        {
            var saveFile = BuildSafeClassStructurePath(savePath, item.UnityVersion, item.TypeName);
            var versionPath = Path.GetDirectoryName(saveFile);
            if (!string.IsNullOrEmpty(versionPath))
            {
                Directory.CreateDirectory(versionPath);
            }

            WriteTextExport(saveFile, item.Content ?? string.Empty);
            exportedCount++;
            progressCallback?.Invoke(exportedCount, totalCount);
        }

        return exportedCount;
    }

    private async Task ExportClassStructuresCommandHandlerAsync()
    {
        if (assetClassItems.Count == 0)
        {
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select folder to export class structures");
        if (string.IsNullOrWhiteSpace(savePath)) return;

        SaveExportFolder(savePath);
        var itemsSnapshot = assetClassItems.ToList();
        StatusStripUpdate("Exporting class structures...");
        Progress.Reset();

        try
        {
            var exportedCount = await Task.Run(() =>
            {
                var exportItems = itemsSnapshot.Select(item => (
                    item.UnityVersion,
                    TypeName: $"{item.ClassID.ToString(CultureInfo.InvariantCulture)} {item.Name}",
                    Content: FormatAssetClass(item)));
                return ExportClassStructuresToDisk(savePath, exportItems, Progress.Report);
            });

            StatusStripUpdate($"Finished exporting {exportedCount} class structures");
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting class structures", ex);
            StatusStripUpdate($"Error exporting class structures: {ex.Message}");
        }
    }

    private void HideProjectAnalysisBanner()
    {
        ProjectSession.ApplyProjectAnalysis((ProjectAnalysisReport?)null);
    }

    private async Task ReturnToProjectManagerCommandHandlerAsync()
    {
        if (_isReturningToProjectManager || IsWindowClosingOrClosed)
        {
            return;
        }

        _isReturningToProjectManager = true;
        var currentProjectId = projectContext?.Project.Id
            ?? ViewModel.ProjectSession.SelectedProjectRecord?.Id;

        try
        {
            await StopActiveBackgroundWorkForNavigationAsync();

            if (IsWindowClosingOrClosed)
            {
                return;
            }

            if (global::Avalonia.Application.Current is App app)
            {
                if (!app.NavigateToProjectManager(this, currentProjectId))
                {
                    _isReturningToProjectManager = false;
                }
                return;
            }

            var projectManager = new ProjectManagerWindow(initialSelectedProjectId: currentProjectId, autoRefresh: false);
            if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = projectManager;
            }

            projectManager.Show();
            projectManager.Activate();
            TeardownProjectSession();
            Close();
            projectManager.BeginRefreshProjects(currentProjectId);
        }
        catch
        {
            _isReturningToProjectManager = false;
            throw;
        }
    }

    private async Task StopActiveBackgroundWorkForNavigationAsync()
    {
        lazyIndexingSessionId++;
        CancelProgressiveScan();
        ViewModel.LoadingService.StopIndexing();
        CancelProjectAnalysis();
        CancelPendingArenaCoalescedSort();
        CancelPendingPreview();
        _previewRequestCoordinator.CancelCurrent();
        _dumpRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        lock (preloaderLock)
        {
            preloaderCts?.Cancel();
            preloaderCts?.Dispose();
            preloaderCts = null;
            preloadedUniqueIds.Clear();
        }
        Stop2DAnimation();
        AudioReset(recreateAudioPlayer: false);
        VideoReset();
        FlushAvatarPreviewSettingsSave();

        var activeIndexingTask = ViewModel.LoadingService.ActiveIndexingTask;
        if (activeIndexingTask != null && !activeIndexingTask.IsCompleted)
        {
            try
            {
                await Task.WhenAny(activeIndexingTask, Task.Delay(TimeSpan.FromMilliseconds(1500)));
            }
            catch
            {
            }
        }
    }

    internal void TeardownProjectSession()
    {
        if (Interlocked.Exchange(ref _projectSessionTornDown, 1) != 0)
        {
            return;
        }

        lazyIndexingSessionId++;
        CancelProgressiveScan();
        ViewModel.LoadingService.StopIndexing();
        CancelProjectAnalysis();
        CancelPendingArenaCoalescedSort();
        listSearchDebounce?.Cancel();
        listSearchDebounce?.Dispose();
        listSearchDebounce = null;
        lock (preloaderLock)
        {
            preloaderCts?.Cancel();
            preloaderCts?.Dispose();
            preloaderCts = null;
            preloadedUniqueIds.Clear();
        }
        previewCandidateSelectionCts?.Cancel();
        previewCandidateSelectionCts?.Dispose();
        previewCandidateSelectionCts = null;
        _videoPreviewLoadCts?.Cancel();
        _videoPreviewLoadCts?.Dispose();
        _videoPreviewLoadCts = null;
        _previewRequestCoordinator.CancelCurrent();
        _dumpRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        _animationRigPreviewCache.Clear();
        treeSearchSession.Invalidate();
        appliedTreeSearchMatches = null;
        FlushAvatarPreviewSettingsSave();
        ClearPreview();
        ReleaseGlPreviewResources();

        PreviewContentHost.SizeChanged -= _previewContentHostSizeChangedHandler;
        PreviewContentHost.PointerWheelChanged -= ImagePreviewBox_PointerWheelChanged;
        ImagePreviewBox.PointerWheelChanged -= ImagePreviewBox_PointerWheelChanged;
        SpecifyUnityVersionTextBox.LostFocus -= _specifyUnityVersionLostFocusHandler;
        RemoveHandler(DragDrop.DragOverEvent, Window_DragOver);
        RemoveHandler(DragDrop.DropEvent, Window_Drop);
        VideoProgressBar.RemoveHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, VideoProgressBar_DragStarted);
        VideoProgressBar.RemoveHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, VideoProgressBar_DragCompleted);
        if (_audioProgressHandlersAttached && AudioProgressBar != null)
        {
            AudioProgressBar.RemoveHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, AudioProgressBar_DragStarted);
            AudioProgressBar.RemoveHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, AudioProgressBar_DragCompleted);
            _audioProgressHandlersAttached = false;
        }
        FfmpegVideoPlayer.MediaEnded -= FfmpegVideoPlayer_MediaEnded;

        if (TextureGLPreview != null)
        {
            TextureGLPreview.GpuErrorOccurred -= _textureGlGpuErrorHandler;
        }
        if (GLPreviewControl != null)
        {
            GLPreviewControl.GpuErrorOccurred -= _glGpuErrorHandler;
            GLPreviewControl.AnimationFrameChanged -= _glAnimationFrameChangedHandler;
            GLPreviewControl.MeshGeometryLoaded -= OnMeshPreviewGeometryLoaded;
            GLPreviewControl.LoadMeshPreviewGeometryCache = null;
            GLPreviewControl.SaveMeshPreviewGeometryCache = null;
        }

        if (_audioTimer != null)
        {
            _audioTimer.Stop();
            _audioTimer.Tick -= AudioTimer_Tick;
            _audioTimer = null;
        }
        if (_ffmpegVideoTimer != null)
        {
            _ffmpegVideoTimer.Stop();
            _ffmpegVideoTimer.Tick -= FfmpegVideoTimer_Tick;
            _ffmpegVideoTimer = null;
        }
        if (avatarPreviewSettingsSaveTimer != null)
        {
            avatarPreviewSettingsSaveTimer.Stop();
            avatarPreviewSettingsSaveTimer.Tick -= AvatarPreviewSettingsSaveTimer_Tick;
            avatarPreviewSettingsSaveTimer = null;
        }

        UnwireViewModelOptions();
        ViewModel.CommandHandlers.Clear();
        AudioReset(recreateAudioPlayer: false);
        VideoReset();
        _audioPlaybackService?.Dispose();
        _audioPlaybackService = null;
        _pcmWavePreviewPlayer?.Dispose();
        _pcmWavePreviewPlayer = null;

        lock (previewCacheLock)
        {
            meshToMaterialsCache = null;
            meshAssociatedRenderersCache = null;
            meshSourceTypesCache = null;
            materialMainTextureCache = null;
            materialPreviewMaterialCache = null;
            materialTextureSlotsCache = null;
            objectToAssetItemCache = null;
            animationClipAvatarCache = null;
            avatarMeshCache = null;
            avatarMeshesCache = null;
            meshAvatarCache = null;
            meshSkinnedRenderersCache = null;
            animationClipTransformBindingsCache = null;
        }

        sceneTreeNodes.Clear();
        treeSearchResults.Clear();
        SceneTreeView.ItemsSource = null;
        AssetListDataGrid.ItemsSource = null;
        AssetClassesDataGrid.ItemsSource = null;
        assetContextItem = null;
        assetContextCellText = string.Empty;
        _currentlySelectedUniqueID = null;
        currentScanResult = null;
        resolvedSourcePathCache.Clear();
        lazySourcePathBySerializedFile.Clear();
        lazySourceFileSearchCache.Clear();

        if (ReferenceEquals(Logger.Default, logger))
        {
            Logger.Default = new DummyLogger();
        }
        if (ReferenceEquals(Progress.Default, _progressReporter))
        {
            Progress.Default = new Progress<int>();
        }
        if (ReferenceEquals(AssetsManager.ShouldYieldForUserInteraction, _shouldYieldForUserInteractionDelegate))
        {
            AssetsManager.ShouldYieldForUserInteraction = null;
        }
        if (ReferenceEquals(AssetsManager.MemoryPressureCallback, _memoryPressureCallback))
        {
            AssetsManager.MemoryPressureCallback = null;
        }

        assetsManager.ShouldKeepFileCallback = null;
        assetsManager.DecompressionCache = null;
        BundleFile.CacheDirectory = string.Empty;
        Volatile.Write(ref _spriteDependenciesLoadedFor, null);
        SpriteHelper.ClearAtlasCache();
        AssetExportHelper.ClearShaderGuidCache();
        lock (assemblyLoader)
        {
            assemblyLoader.Clear();
        }

        ViewModel.ResetSession();
        ViewModel.Dispose();
        _sqliteCache.Dispose();
        assetsManager.Dispose();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        TeardownProjectSession();
        base.OnClosing(e);
    }


}
