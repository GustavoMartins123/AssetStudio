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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using FFmpegVideoPlayer.Core;
using AssetStudio.Avalonia.ViewModels;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    private AssetsManager assetsManager = new AssetsManager();
    private List<AssetItem> exportableAssets = new List<AssetItem>();
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
    private List<AssetItem> visibleAssets = new();
    private BulkObservableCollection<AssetItem> visibleAssetItems = new();
    private List<AssetClassItem> assetClassItems = new List<AssetClassItem>();
    private System.Collections.ObjectModel.ObservableCollection<AssetClassItem> visibleAssetClassItems = new();

    private AssetClassItem? classFilterOverride;
    private List<GameObjectNode> sceneTreeNodes = new List<GameObjectNode>();
    private readonly List<GameObjectNode> treeSearchResults = new List<GameObjectNode>();
    private readonly ExportOptionsState exportOptions = new();
    private readonly AvaloniaAppSettings appSettings;
    private readonly ProjectLaunchContext? projectContext;
    private readonly AssemblyLoader assemblyLoader = new AssemblyLoader();
    private readonly GUILogger logger;
    private CancellationTokenSource? listSearchDebounce;
    private string? assetListSortMember;
    private string assetContextCellText = string.Empty;
    private AssetItem? assetContextItem;
    private int nextGameObjectSearchIndex;
    private bool assetListSortDescending;
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

    private string? _currentTempVideoPath;
    private string? _currentTempVideoAssetId;
    private CancellationTokenSource? _videoPreviewLoadCts;
    private bool _isUpdatingVideoProgress = false;
    private bool _isVideoDragging = false;
    private long _videoLengthMs = 0;
    private volatile int _targetVolume = 80;
    private DispatcherTimer? _ffmpegVideoTimer;
    private DispatcherTimer? _2dAnimTimer;
    private List<(float time, AssetStudio.Object asset)> _2dAnimFrames = new();
    private Dictionary<AssetStudio.Object, global::Avalonia.Media.Imaging.Bitmap> _2dAnimBitmaps = new();
    private global::Avalonia.Media.Imaging.Bitmap? _2dAnimCurrentBitmap;
    private DateTime _2dAnimStartTime;
    private float _2dAnimDuration;
    private float _2dAnimPausedElapsedSeconds;
    private int _2dAnimCurrentFrameIndex;
    private bool _2dAnimPaused;
    private bool _is2dAnimationPreviewActive;
    private double _2dAnimPreviewFill = TwoDAnimationDefaultPreviewFill;
    private const double TwoDAnimationMinPreviewFill = 0.025;
    private const double TwoDAnimationDefaultPreviewFill = 0.10;
    private const double TwoDAnimationMaxPreviewFill = 1.0;
    private const double TwoDAnimationZoomFactor = 1.12;

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
    private bool isRefreshingFilterList;
    private bool isRefreshingClassesList;
    private bool _statusUpdatePending;
    private Dictionary<string, AssetItem> lazyAssetItemsByHandleId = new(StringComparer.Ordinal);
    private HashSet<string> exportableAssetHandleIds = new(StringComparer.Ordinal);
    private HashSet<ClassIDType> exportableAssetTypes = new();
    private int lazyAssetItemOrdinal;
    private bool projectAutoIndexStarted;
    private int foregroundLazyLoadCount;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(ProjectLaunchContext? projectContext)
    {
        var loadingService = new AssetLoadingService();
        ViewModel = new MainWindowViewModel(loadingService);
        DataContext = ViewModel;

        this.projectContext = projectContext;
        appSettings = projectContext?.Settings ?? ProjectManagerStore.Shared.LoadGlobalSettings();
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        InitializeComponent();
        GLPreviewControl.LoadMeshPreviewGeometryCache = LoadMeshPreviewGeometryCache;
        GLPreviewControl.SaveMeshPreviewGeometryCache = SaveMeshPreviewGeometryCache;
        PreviewContentHost.SizeChanged += (_, _) => Update2DAnimationImageLayout();
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
        logger.ShowErrorMessage = appSettings.ShowErrorMessage;
        Logger.Default = logger;
        showErrorMessageMenu.IsChecked = appSettings.ShowErrorMessage;
        exportOptions.CopyFrom(appSettings.ExportOptions);
        displayAll.IsChecked = appSettings.DisplayAll;
        displayInfo.IsChecked = appSettings.DisplayInfo;
        enablePreview.IsChecked = appSettings.EnablePreview;
        ViewModel.SpecifyUnityVersion = appSettings.SpecifyUnityVersion ?? string.Empty;
        SpecifyUnityVersionTextBox.LostFocus += (s, e) => ApplyUnityVersionOption();
        ApplyUnityVersionOption();
        assetsManager.ProjectRoot = appSettings.ProjectRoot;
        AssetsManager.ShouldYieldForUserInteraction = ShouldPauseBackgroundWork;
        assetsManager.ShouldKeepFileCallback = fileName =>
        {
            lock (preloaderLock)
            {
                return preloadedUniqueIds.Any(uid => uid.StartsWith(fileName + "#")) 
                    || (_currentlySelectedUniqueID != null && _currentlySelectedUniqueID.StartsWith(fileName + "#"));
            }
        };
        Progress.Default = new Progress<int>(SetProgressBarValue);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);

        AssetsManager.MemoryPressureCallback = (operation, loadPercent, limitPercent) =>
        {
            if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                return MemoryPressureResult.Continue;
            }
            return global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var msg = $"Memory usage has reached {loadPercent}% (limit: {limitPercent}%) during {operation}.\n\n" +
                          "Continuing may slow down your system or cause it to run out of memory.\n\n" +
                          "What would you like to do?";
                return await ShowMemoryPressureWarningDialog(msg);
            }).GetAwaiter().GetResult();
        };

        VideoProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, VideoProgressBar_DragStarted);
        VideoProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, VideoProgressBar_DragCompleted);
        _ffmpegVideoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _ffmpegVideoTimer.Tick += FfmpegVideoTimer_Tick;
        FfmpegVideoPlayer.MediaEnded += FfmpegVideoPlayer_MediaEnded;
        FfmpegVideoPlayer.IsVisible = true;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Title = $"AssetStudio v{version}";
        if (projectContext != null)
        {
            Title = $"AssetStudio v{version} - {projectContext.Project.DisplayName}";
        }

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

        if (TextureGLPreview != null)
        {
            TextureGLPreview.GpuErrorOccurred += (errMsg) =>
            {
                logger.Log(LoggerEvent.Warning, $"GPU texture preview error: {errMsg}. Falling back to CPU.");
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (errMsg.Contains("initialization") || errMsg.Contains("link error") || errMsg.Contains("compile error"))
                    {
                        useGpuTexturePreview = false;
                    }
                    UpdateImagePreview(forceCpu: true);
                });
            };
        }

        if (GLPreviewControl != null)
        {
            GLPreviewControl.GpuErrorOccurred += (errMsg) =>
            {
                logger.Log(LoggerEvent.Warning, $"GPU mesh preview error: {errMsg}.");
            };
            GLPreviewControl.AnimationFrameChanged += (currentFrame, totalFrames) =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    UpdateAnimationPlaybackFrame(currentFrame, totalFrames);
                });
            };
        }

        ApplyAvatarPreviewControlSettings();
    }

    public void StartProjectIndexingOnOpen()
    {
        if (projectContext == null || projectAutoIndexStarted)
        {
            return;
        }

        projectAutoIndexStarted = true;
        Dispatcher.UIThread.Post(async () => await StartProjectIndexingOnOpenAsync(), DispatcherPriority.Background);
    }

    private async Task StartProjectIndexingOnOpenAsync()
    {
        var root = projectContext?.Project.ProjectRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusStripUpdate("Project opened. Set a project root to index assets.");
            return;
        }

        if (!Directory.Exists(root))
        {
            StatusStripUpdate($"Project root not found: {root}");
            return;
        }

        assetsManager.ProjectRoot = root;
        appSettings.ProjectRoot = root;
        SaveAppSettings();

        await BeginProgressiveLoadAsync(new[] { root }, "Opening project");
    }

    private void StatusStripUpdate(string text)
    {
        _pendingStatusText = text;
        if (!_statusUpdatePending)
        {
            _statusUpdatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                ViewModel.StatusText = _pendingStatusText ?? string.Empty;
                _statusUpdatePending = false;
            }, DispatcherPriority.Background);
        }
    }

    private void SetProgressBarValue(int value)
    {
        Dispatcher.UIThread.Post(() => ViewModel.LoadingProgress = value);
    }

    private void ShowIndexingProgressPanel(IndexingProgressUpdate update, int percentDecimals = 1)
    {
        if (update == null)
        {
            return;
        }

        ShowIndexingProgressPanel(
            update.Status,
            update.ProcessedFiles,
            update.TotalFiles,
            update.PendingFiles,
            update.PercentComplete,
            update.CurrentFile,
            update.LastReadFile,
            null,
            percentDecimals);
    }

    private void ShowIndexingProgressPanel(ProjectIndexingState state, int percentDecimals = 1)
    {
        if (state == null)
        {
            return;
        }

        ShowIndexingProgressPanel(
            state.Status,
            state.ProcessedFiles,
            state.TotalFiles,
            state.PendingFiles,
            state.PercentComplete,
            state.CurrentFile,
            state.LastReadFile,
            state.UpdatedAt,
            percentDecimals);
    }

    private void ShowIndexingProgressPanel(
        string status,
        int processedFiles,
        int totalFiles,
        int pendingFiles,
        double percentComplete,
        string currentFile,
        string lastReadFile,
        DateTime? updatedAt,
        int percentDecimals = 1)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowIndexingProgressPanel(
                status,
                processedFiles,
                totalFiles,
                pendingFiles,
                percentComplete,
                currentFile,
                lastReadFile,
                updatedAt,
                percentDecimals));
            return;
        }

        var percent = Math.Clamp(percentComplete, 0, 100);
        var isStageProgress = IsStageProgressStatus(status);
        var progressDetail = !string.IsNullOrWhiteSpace(currentFile) ? currentFile : lastReadFile;
        var fileName = string.IsNullOrWhiteSpace(progressDetail) || isStageProgress ? string.Empty : Path.GetFileName(progressDetail);
        var unitLabel = isStageProgress ? "steps" : "files";
        var countText = totalFiles > 0
            ? $"{processedFiles:N0}/{totalFiles:N0} {unitLabel}"
            : $"{processedFiles:N0} {unitLabel}";
        var pendingText = pendingFiles > 0 ? $" | {pendingFiles:N0} pending" : string.Empty;
        var fileText = isStageProgress
            ? (string.IsNullOrWhiteSpace(progressDetail) ? string.Empty : $" | {progressDetail}")
            : (string.IsNullOrWhiteSpace(fileName)
                ? string.Empty
                : $" | {(string.IsNullOrWhiteSpace(currentFile) ? "Last" : "Now")}: {fileName}");
        var updatedText = updatedAt.HasValue
            ? $" | Updated {updatedAt.Value.ToLocalTime():HH:mm:ss}"
            : string.Empty;

        IndexingProgressPanel.IsVisible = true;
        IndexingProgressText.Text = $"{BuildIndexingProgressTitle(status)}: {countText}{pendingText}{fileText}{updatedText}";
        IndexingProgressPercentText.Text = percent.ToString("0." + new string('#', Math.Max(0, percentDecimals)), CultureInfo.InvariantCulture) + "%";
        IndexingProgressBar.Value = percent;
    }

    private void HideIndexingProgressPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideIndexingProgressPanel);
            return;
        }

        IndexingProgressPanel.IsVisible = false;
        IndexingProgressText.Text = string.Empty;
        IndexingProgressPercentText.Text = "0%";
        IndexingProgressBar.Value = 0;
    }

    private static string BuildIndexingProgressTitle(string status)
    {
        return (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "paused" => "Indexing paused",
            "cancelling" => "Stopping indexing",
            "cancelled" => "Indexing cancelled",
            "saving_index" => "Saving index cache",
            "saving_connections" => "Saving connections",
            "connecting" => "Building connections",
            "connections_completed" => "Connections complete",
            "building_structure" => "Building asset structure",
            "structure_completed" => "Asset structure complete",
            "structure_failed" => "Asset structure failed",
            "completed" => "Indexing complete",
            "failed" => "Indexing failed",
            "connections_failed" => "Connections build failed",
            _ => "Indexing"
        };
    }

    private static bool IsStageProgressStatus(string? status)
    {
        return string.Equals(status, "saving_index", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "saving_connections", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
    }

    private void PrioritizeUserInteraction(int milliseconds = UserInteractionPriorityMilliseconds)
    {
        var now = Stopwatch.GetTimestamp();
        var extensionTicks = (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        var until = now + Math.Max(extensionTicks, 1);

        while (true)
        {
            var current = Interlocked.Read(ref userInteractionPriorityUntilTimestamp);
            if (current >= until)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref userInteractionPriorityUntilTimestamp, until, current) == current)
            {
                return;
            }
        }
    }

    private bool IsUserInteractionPriorityActive()
    {
        return Interlocked.Read(ref userInteractionPriorityUntilTimestamp) > Stopwatch.GetTimestamp();
    }

    private bool ShouldPauseBackgroundWork()
    {
        return IsUserInteractionPriorityActive() || Volatile.Read(ref foregroundLazyLoadCount) > 0;
    }

    private bool IsProgressiveIndexingActive()
    {
        return ViewModel.LoadingService.IsIndexingActive;
    }



    private async Task WaitForUserInteractionPriorityToClearAsync(CancellationToken token)
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        while (!token.IsCancellationRequested && ShouldPauseBackgroundWork())
        {
            await Task.Delay(UserInteractionYieldDelayMilliseconds);
        }
    }

    private void YieldBackgroundWorkForUserInteraction()
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        while (ShouldPauseBackgroundWork())
        {
            Thread.Sleep(UserInteractionYieldDelayMilliseconds);
        }
    }

    private void ResetForm()
    {
        BundleFile.CacheDirectory = "";
        lock (preloaderLock)
        {
            preloaderCts?.Cancel();
            preloaderCts?.Dispose();
            preloaderCts = null;
            preloadedUniqueIds.Clear();
        }
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
        logger.ClearErrors();
        exportableAssets.Clear();
        visibleAssets.Clear();
        visibleAssetItems.Clear();
        lazyAssetItemsByHandleId.Clear();
        exportableAssetHandleIds.Clear();
        exportableAssetTypes.Clear();
        lazySourcePathBySerializedFile.Clear();
        lazySourceFileSearchCache.Clear();
        lazyAssetItemOrdinal = 0;
        assetClassItems.Clear();
        visibleAssetClassItems.Clear();
        classFilterOverride = null;
        if (ClearClassFilterButton != null)
        {
            ClearClassFilterButton.IsVisible = false;
        }
        AssetListDataGrid.SelectedItem = null;
        AssetListDataGrid.ItemsSource = visibleAssetItems;
        AssetClassesDataGrid.ItemsSource = null;
        sceneTreeNodes.Clear();
        treeSearchResults.Clear();
        listSearchDebounce?.Cancel();
        assetListSortMember = null;
        assetListSortDescending = false;
        assetContextCellText = string.Empty;
        assetContextItem = null;
        assemblyLoader.Clear();
        nextGameObjectSearchIndex = 0;
        SceneTreeView.ItemsSource = null;
        DumpTextBox.Text = string.Empty;
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        ClearTextAssetPreview();
        classSearch.Text = string.Empty;
        PreviewLabel.IsVisible = true;
        PreviewLabel.Text = "[Preview Panel]";
        ViewModel.LoadingService.StopIndexing();
        ViewModel.IsIndexingActive = false;
        ViewModel.LoadingProgress = 0;
        ResetFilterTypeMenu();
        StatusStripUpdate("Ready");

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Title = projectContext == null
            ? $"AssetStudio v{version}"
            : $"AssetStudio v{version} - {projectContext.Project.DisplayName}";

        _currentlySelectedUniqueID = null;
        isRefreshingFilterList = false;
        isRefreshingClassesList = false;
    }

    private static T? FindVisualChild<T>(global::Avalonia.Visual? visual) where T : class
    {
        if (visual == null) return null;
        if (visual is T target) return target;
        foreach (var child in global::Avalonia.VisualTree.VisualExtensions.GetVisualChildren(visual))
        {
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void ApplyUnityVersionOption()
    {
        assetsManager.SpecifyUnityVersion = ViewModel.SpecifyUnityVersion?.Trim() ?? string.Empty;
        if (appSettings.SpecifyUnityVersion != assetsManager.SpecifyUnityVersion)
        {
            appSettings.SpecifyUnityVersion = assetsManager.SpecifyUnityVersion;
            SaveAppSettings();
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            if (projectContext != null)
            {
                projectContext.Store.SaveProjectSettings(projectContext.Project.Id, appSettings);
            }
            else
            {
                ProjectManagerStore.Shared.SaveGlobalSettings(appSettings);
            }
        }
        catch
        {
            appSettings.Save();
        }
    }

    private void ClearPreview(string message = "[Preview Panel]")
    {
        Stop2DAnimation();
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        TextPreviewBox.FontFamily = global::Avalonia.Media.FontFamily.Default;
        TextPreviewBox.FontSize = 14;
        ClearTextAssetPreview();
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.Source = null;
            ImagePreviewBox.IsVisible = false;
        }
        if (GLPreviewControl != null)
        {
            GLPreviewControl.StopAnimation();
            GLPreviewControl.IsVisible = false;
        }
        ApplyAvatarPreviewControlSettings();
        HidePreviewGeometryControls();
        ClearMeshMaterialControls();
        ClearPreviewCandidateControls();
        if (AnimationPlaybackPanel != null)
        {
            AnimationPlaybackPanel.IsVisible = false;
        }
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }
        if (PreviewInfoBorder != null)
        {
            PreviewInfoBorder.IsVisible = false;
        }
        if (PreviewInfoOverlay != null)
        {
            PreviewInfoOverlay.Text = string.Empty;
        }
        if (AudioPanel != null)
        {
            AudioPanel.IsVisible = false;
            AudioReset();
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            VideoReset();
        }
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        currentPreviewAudioClip = null;
        currentPreviewVideoClip = null;
        texturePreviewIdCounter++; // Cancel any running background image decoding task
        previewDebounce?.Cancel();
        previewDebounce?.Dispose();
        previewDebounce = null;
        for (int i = 0; i < 4; i++)
        {
            textureChannels[i] = true;
        }
        PreviewLabel.Text = message;
        PreviewLabel.IsVisible = true;
    }

    private void HideAnimationPlayback()
    {
        if (AnimationPlaybackPanel != null)
        {
            AnimationPlaybackPanel.IsVisible = false;
        }

        if (AnimFrameLabel != null)
        {
            AnimFrameLabel.Text = "Frame: 0/0";
        }

        if (AnimPlayPauseBtn != null)
        {
            AnimPlayPauseBtn.Content = "Pause";
        }
    }

    private void ShowAnimationPlayback(int totalFrames)
    {
        if (AnimationPlaybackPanel != null)
        {
            AnimationPlaybackPanel.IsVisible = true;
        }

        SetAnimationPlaybackPaused(false);
        UpdateAnimationPlaybackFrame(0, totalFrames);
    }

    private void SetAnimationPlaybackPaused(bool paused)
    {
        if (AnimPlayPauseBtn != null)
        {
            AnimPlayPauseBtn.Content = paused ? "Play" : "Pause";
        }
    }

    private void UpdateAnimationPlaybackFrame(int currentFrame, int totalFrames)
    {
        if (AnimFrameLabel != null)
        {
            AnimFrameLabel.Text = $"Frame: {currentFrame}/{Math.Max(0, totalFrames)}";
        }
    }

    private async Task<IStorageFolder?> TryGetFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return null;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        try
        {
            var absolutePath = Path.GetFullPath(path).Replace('\\', '/');
            if (!absolutePath.StartsWith("/"))
            {
                absolutePath = "/" + absolutePath;
            }
            var uri = new Uri("file://" + absolutePath);
            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(uri);
        }
        catch
        {
            return null;
        }
    }

    private async Task<FilePickerOpenOptions> CreateOpenFileOptions(string title, bool allowMultiple)
    {
        return new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await TryGetFolder(appSettings.LoadFolderPath)
        };
    }

    private async Task<FolderPickerOpenOptions> CreateLoadFolderOptions(string title, bool allowMultiple = false)
    {
        return new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await TryGetFolder(appSettings.LoadFolderPath)
        };
    }

    private async Task<FolderPickerOpenOptions> CreateExportFolderOptions(string title, bool allowMultiple = false)
    {
        return new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await TryGetFolder(appSettings.ExportFolderPath)
        };
    }

    private async Task<FilePickerSaveOptions> CreateFbxSaveOptions(string title, string suggestedFileName)
    {
        return new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "fbx",
            SuggestedStartLocation = await TryGetFolder(appSettings.ExportFolderPath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Fbx file")
                {
                    Patterns = new[] { "*.fbx" }
                }
            }
        };
    }

    private void SaveLoadFolder(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        appSettings.LoadFolderPath = folder;
        SaveAppSettings();
    }

    private void SaveExportFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        appSettings.ExportFolderPath = path;
        SaveAppSettings();
    }

    private void TreeSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        treeSearchResults.Clear();
        nextGameObjectSearchIndex = 0;
    }

    private void TreeSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var searchText = treeSearch.Text?.Trim();
        if (string.IsNullOrEmpty(searchText))
        {
            return;
        }

        if (treeSearchResults.Count == 0)
        {
            foreach (var node in sceneTreeNodes)
            {
                TreeNodeSearch(node, searchText);
            }
        }

        if (treeSearchResults.Count == 0)
        {
            StatusStripUpdate($"No scene hierarchy match for '{searchText}'.");
            return;
        }

        if (nextGameObjectSearchIndex >= treeSearchResults.Count)
        {
            nextGameObjectSearchIndex = 0;
        }

        var selectedNode = treeSearchResults[nextGameObjectSearchIndex];
        selectedNode.ExpandAncestors();
        SceneTreeView.SelectedItem = selectedNode;
        nextGameObjectSearchIndex++;
        StatusStripUpdate($"Scene hierarchy match {nextGameObjectSearchIndex}/{treeSearchResults.Count}: {selectedNode.Name}");
    }

    private void TreeNodeSearch(GameObjectNode node, string searchText)
    {
        if (node.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
        {
            treeSearchResults.Add(node);
        }

        foreach (var child in node.Children)
        {
            TreeNodeSearch(child, searchText);
        }
    }

    private void DisplayAll_Click(object? sender, RoutedEventArgs e)
    {
        appSettings.DisplayAll = displayAll.IsChecked == true;
        SaveAppSettings();
        if (assetsManager.assetsFileList.Count > 0)
        {
            BuildAssetStructures();
        }
    }

    private void EnablePreview_Click(object? sender, RoutedEventArgs e)
    {
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        appSettings.EnablePreview = enablePreview.IsChecked == true;
        SaveAppSettings();
        if (enablePreview.IsChecked != true)
        {
            ClearPreview("Preview disabled");
        }
        else if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            QueuePreviewAsset(selected, immediate: true);
        }
    }

    private void DisplayInfo_Click(object? sender, RoutedEventArgs e)
    {
        appSettings.DisplayInfo = displayInfo.IsChecked == true;
        SaveAppSettings();
        if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            PreviewLabel.Text = displayInfo.IsChecked == true
                ? $"{selected.TypeString}: {selected.Name}"
                : string.Empty;
            PreviewLabel.IsVisible = displayInfo.IsChecked == true && !TextPreviewBox.IsVisible && !TextAssetPreviewPanel.IsVisible && (ImagePreviewBox == null || !ImagePreviewBox.IsVisible);
            if (PreviewInfoBorder != null)
            {
                PreviewInfoBorder.IsVisible = displayInfo.IsChecked == true && (currentPreviewTexture != null || currentPreviewSprite != null);
            }
        }
    }

    private void TogglePreviewInfoBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (PreviewInfoScroll != null && TogglePreviewInfoBtn != null)
        {
            bool isVisible = PreviewInfoScroll.IsVisible;
            PreviewInfoScroll.IsVisible = !isVisible;
            TogglePreviewInfoBtn.Content = !isVisible ? "▼" : "▲";
        }
    }

    private async void SetProjectRoot_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select project root"));
        if (folders == null || folders.Count == 0) return;

        assetsManager.ProjectRoot = folders[0].Path.LocalPath;
        SaveLoadFolder(assetsManager.ProjectRoot);
        appSettings.ProjectRoot = assetsManager.ProjectRoot;
        SaveAppSettings();
        StatusStripUpdate($"Project root set to: {assetsManager.ProjectRoot}");
    }

    private async void ShowExportOptions_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ExportOptionsWindow(exportOptions.Clone());
        var result = await dialog.ShowDialog<ExportOptionsState?>(this);
        if (result == null) return;

        exportOptions.CopyFrom(result);
        appSettings.ExportOptions.CopyFrom(result);
        SaveAppSettings();
        StatusStripUpdate("Export options updated.");
    }

    private void ResetFilterTypeMenu()
    {
        updatingFilterTypeMenu = true;
        while (filterTypeMenu.Items.Count > 1)
        {
            filterTypeMenu.Items.RemoveAt(1);
        }
        filterTypeAll.IsChecked = true;
        updatingFilterTypeMenu = false;
    }

    private void BuildFilterTypeMenu()
    {
        updatingFilterTypeMenu = true;
        var checkedTypes = GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet();
        bool wasAllChecked = filterTypeAll.IsChecked == true;

        while (filterTypeMenu.Items.Count > 1)
        {
            filterTypeMenu.Items.RemoveAt(1);
        }

        var types = exportableAssetTypes
            .OrderBy(x => x.ToString());

        foreach (var type in types)
        {
            var item = new MenuItem
            {
                Header = type.ToString(),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = checkedTypes.Contains(type),
                Tag = type
            };
            item.Click += FilterType_Click;
            filterTypeMenu.Items.Add(item);
        }

        if (wasAllChecked)
        {
            filterTypeAll.IsChecked = true;
        }
        else
        {
            filterTypeAll.IsChecked = !GetFilterTypeItems().Any(x => x.IsChecked == true);
        }
        updatingFilterTypeMenu = false;
    }

    private void FilterTypeAll_Click(object? sender, RoutedEventArgs e)
    {
        if (updatingFilterTypeMenu) return;

        PrioritizeUserInteraction();
        updatingFilterTypeMenu = true;
        if (filterTypeAll.IsChecked == true)
        {
            foreach (var item in GetFilterTypeItems())
            {
                item.IsChecked = false;
            }
        }
        updatingFilterTypeMenu = false;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void FilterType_Click(object? sender, RoutedEventArgs e)
    {
        if (updatingFilterTypeMenu) return;

        PrioritizeUserInteraction();
        updatingFilterTypeMenu = true;
        filterTypeAll.IsChecked = !GetFilterTypeItems().Any(x => x.IsChecked == true);
        updatingFilterTypeMenu = false;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void ClassSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        PrioritizeUserInteraction();
        FilterAssetClasses();
    }

    private void BuildAssetClasses()
    {
        assetClassItems.Clear();

        var objectCounts = assetsManager.assetsFileList
            .SelectMany(file => file.Objects.Select(obj => new { file.unityVersion, ClassID = (int)obj.type }))
            .GroupBy(x => (x.unityVersion, x.ClassID))
            .ToDictionary(x => x.Key, x => x.Count());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assetsFile in assetsManager.assetsFileList)
        {
            AddSerializedTypes(assetsFile, assetsFile.m_Types, "Native", objectCounts, seen);
            AddSerializedTypes(assetsFile, assetsFile.m_RefTypes, "Reference", objectCounts, seen);
        }

        assetClassItems = assetClassItems
            .OrderBy(x => x.UnityVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ClassID)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FilterAssetClasses();
    }

    private void AddSerializedTypes(SerializedFile assetsFile, IEnumerable<SerializedType>? types, string sourceKind,
        Dictionary<(string UnityVersion, int ClassID), int> objectCounts, HashSet<string> seen)
    {
        if (types == null)
            return;

        foreach (var type in types)
        {
            var name = GetSerializedTypeName(type);
            var ns = type.m_NameSpace ?? string.Empty;
            var asm = type.m_AsmName ?? string.Empty;
            var key = string.Join("\u001f", assetsFile.unityVersion, type.classID.ToString(CultureInfo.InvariantCulture), name, ns, asm, sourceKind);
            if (!seen.Add(key))
                continue;

            objectCounts.TryGetValue((assetsFile.unityVersion, type.classID), out var objectCount);
            assetClassItems.Add(new AssetClassItem
            {
                ClassID = type.classID,
                Name = name,
                Namespace = ns,
                Assembly = asm,
                UnityVersion = assetsFile.unityVersion,
                SourceFile = assetsFile.fileName,
                ObjectCount = objectCount,
                SourceKind = type.m_IsStrippedType ? $"{sourceKind} stripped" : sourceKind,
                SerializedType = type
            });
        }
    }

    private static string GetSerializedTypeName(SerializedType type)
    {
        if (!string.IsNullOrEmpty(type.m_KlassName))
            return type.m_KlassName;

        var rootNode = type.m_Type?.m_Nodes?.FirstOrDefault();
        if (!string.IsNullOrEmpty(rootNode?.m_Type))
            return rootNode.m_Type;

        return ClassIDTypeHelper.IsDefined(type.classID)
            ? ClassIDTypeHelper.FromClassId(type.classID).ToString()
            : $"Class {type.classID}";
    }

    private void FilterAssetClasses()
    {
        var filter = classSearch.Text?.Trim();
        IEnumerable<AssetClassItem> classes = assetClassItems;
        if (!string.IsNullOrEmpty(filter))
        {
            classes = classes.Where(x =>
                x.ClassID.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Namespace.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.Assembly.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || x.SourceKind.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        // Save selection and scroll state
        var selectedItem = AssetClassesDataGrid.SelectedItem as AssetClassItem;
        (int ClassID, string Name, string Namespace, string Assembly, string UnityVersion, string SourceFile, string SourceKind)? selectedKey = selectedItem != null
            ? (selectedItem.ClassID, selectedItem.Name, selectedItem.Namespace, selectedItem.Assembly, selectedItem.UnityVersion, selectedItem.SourceFile, selectedItem.SourceKind)
            : null;

        var scrollViewer = FindVisualChild<ScrollViewer>(AssetClassesDataGrid);
        var scrollOffset = scrollViewer?.Offset ?? default;

        SyncObservableCollection(visibleAssetClassItems, classes.ToList());

        isRefreshingClassesList = true;
        try
        {
            if (AssetClassesDataGrid.ItemsSource != visibleAssetClassItems)
            {
                AssetClassesDataGrid.ItemsSource = visibleAssetClassItems;
            }

            // Restore selection
            if (selectedKey != null)
            {
                var newSelected = visibleAssetClassItems.FirstOrDefault(x =>
                    x.ClassID == selectedKey.Value.ClassID &&
                    x.Name == selectedKey.Value.Name &&
                    x.Namespace == selectedKey.Value.Namespace &&
                    x.Assembly == selectedKey.Value.Assembly &&
                    x.UnityVersion == selectedKey.Value.UnityVersion &&
                    x.SourceFile == selectedKey.Value.SourceFile &&
                    x.SourceKind == selectedKey.Value.SourceKind);

                if (newSelected != null)
                {
                    AssetClassesDataGrid.SelectedItem = newSelected;
                }
            }
        }
        finally
        {
            isRefreshingClassesList = false;
        }

        // Restore scroll position
        if (scrollViewer != null && scrollOffset != default)
        {
            Dispatcher.UIThread.Post(() =>
            {
                scrollViewer.Offset = scrollOffset;
            }, DispatcherPriority.Background);
        }
    }

    private void AssetClassesDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingClassesList) return;

        PrioritizeUserInteraction();
        var selectedItem = sender is DataGrid grid ? grid.SelectedItem : AssetClassesDataGrid.SelectedItem;
        if (selectedItem is not AssetClassItem item)
        {
            return;
        }

        ShowAssetClassPreview(item);
    }

    private void ShowAssetClassPreview(AssetClassItem item)
    {
        RightTabControl.SelectedIndex = 0;
        ClearTextAssetPreview();
        TextPreviewBox.Text = FormatAssetClass(item);
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Asset class {item.ClassID}: {item.Name} ({item.ObjectCount} objects)");
    }

    private static string FormatAssetClass(AssetClassItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ClassID: {item.ClassID}");
        sb.AppendLine($"Name: {item.Name}");
        if (!string.IsNullOrEmpty(item.Namespace))
            sb.AppendLine($"Namespace: {item.Namespace}");
        if (!string.IsNullOrEmpty(item.Assembly))
            sb.AppendLine($"Assembly: {item.Assembly}");
        sb.AppendLine($"Unity version: {item.UnityVersion}");
        sb.AppendLine($"Source file: {item.SourceFile}");
        sb.AppendLine($"Source kind: {item.SourceKind}");
        sb.AppendLine($"Loaded objects: {item.ObjectCount}");
        sb.AppendLine();

        sb.AppendLine("──────────────────────────────────────────────────");
        sb.AppendLine("NOTE: Serialization Class vs Composite Asset");
        sb.AppendLine("- Serialization Class: Defines the raw binary layout structure (TypeTree)");
        sb.AppendLine("  for a Unity ClassIDType. It represents how objects are serialized in files.");
        sb.AppendLine("- Composite Asset (e.g. Prefab, FBX): These are logical assemblies of multiple");
        sb.AppendLine("  assets/GameObjects. A Prefab contains references (PPtrs) to other components");
        sb.AppendLine("  and structures; it is not a simple asset (like a Mesh or Texture) but a graph.");
        sb.AppendLine("──────────────────────────────────────────────────");
        sb.AppendLine();

        var nodes = item.SerializedType.m_Type?.m_Nodes;
        if (nodes == null || nodes.Count == 0)
        {
            sb.AppendLine("No TypeTree available for this class.");
            return sb.ToString();
        }

        sb.AppendLine($"TypeTree nodes: {nodes.Count}");
        sb.AppendLine("Level  Type  Name  ByteSize  Index  Version  MetaFlag");
        foreach (var node in nodes)
        {
            var indent = new string(' ', Math.Max(0, node.m_Level) * 2);
            sb.Append(indent);
            sb.Append(node.m_Type);
            if (!string.IsNullOrEmpty(node.m_Name))
            {
                sb.Append(' ');
                sb.Append(node.m_Name);
            }
            sb.Append("  [");
            sb.Append("size=");
            sb.Append(node.m_ByteSize.ToString(CultureInfo.InvariantCulture));
            sb.Append(", index=");
            sb.Append(node.m_Index.ToString(CultureInfo.InvariantCulture));
            sb.Append(", version=");
            sb.Append(node.m_Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(", meta=0x");
            sb.Append(node.m_MetaFlag.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine("]");
        }

        return sb.ToString();
    }

    private IEnumerable<MenuItem> GetFilterTypeItems()
    {
        return filterTypeMenu.Items
            .OfType<MenuItem>()
            .Where(x => x.Tag is ClassIDType);
    }

    private async void LoadFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(await CreateOpenFileOptions("Select Game File", true));

        if (files != null && files.Count > 0)
        {
            var filePaths = files.Select(f => f.Path.LocalPath).ToArray();
            currentScanResult = null;
            SaveLoadFolder(filePaths[0]);
            ResetForm();
            StatusStripUpdate("Loading files...");
            assetsManager.Clear();
            ApplyUnityVersionOption();
            try
            {
                await Task.Run(() => assetsManager.LoadFiles(filePaths));
            }
            catch (MemoryPressureException ex)
            {
                ShowMemoryPressureError(ex);
                return;
            }
            BuildAssetStructures();
        }
    }

    private async void LoadFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select Game Folder"));

        if (folders != null && folders.Count > 0)
        {
            var folderPath = folders[0].Path.LocalPath;
            await BeginProgressiveLoadAsync(new[] { folderPath }, "Indexing folder");
        }
    }

    private async void ExtractFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(await CreateOpenFileOptions("Select bundle or web file", true));
        if (files == null || files.Count == 0) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var filePaths = files.Select(x => x.Path.LocalPath).Where(File.Exists).ToArray();
        SaveLoadFolder(filePaths.FirstOrDefault() ?? string.Empty);
        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        StatusStripUpdate("Extracting files...");
        var extractedCount = await Task.Run(() => ExtractFiles(filePaths, savePath));
        StatusStripUpdate($"Finished extracting {extractedCount} files.");
    }

    private async void ExtractFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var sourceFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select folder to extract"));
        if (sourceFolders == null || sourceFolders.Count == 0) return;

        var saveFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (saveFolders == null || saveFolders.Count == 0) return;

        var sourcePath = sourceFolders[0].Path.LocalPath;
        SaveLoadFolder(sourcePath);
        var savePath = saveFolders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        StatusStripUpdate("Extracting folder...");
        var extractedCount = await Task.Run(() => ExtractFolder(sourcePath, savePath));
        StatusStripUpdate($"Finished extracting {extractedCount} files.");
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
        ApplyUnityVersionOption();
        StatusStripUpdate("Loading dropped files...");

        try
        {
            await Task.Run(() => assetsManager.LoadFiles(paths));
        }
        catch (MemoryPressureException ex)
        {
            ShowMemoryPressureError(ex);
            return;
        }

        BuildAssetStructures();
    }

    private async Task BeginProgressiveLoadAsync(string[] paths, string statusPrefix)
    {
        currentScanResult = null;
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            currentScanResult = await ScanFolderForProgressiveIndexAsync(paths[0], statusPrefix);
        }

        LoadPathsProgressiveAsync(paths);
    }

    private async Task<ProjectScanResult?> ScanFolderForProgressiveIndexAsync(string folderPath, string statusPrefix)
    {
        StatusStripUpdate($"{statusPrefix}: scanning folder...");
        var scanProgress = new Progress<ScanProgress>(p =>
        {
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
            var scanResult = await Task.Run(() => ProjectScanner.ScanFolder(folderPath, CancellationToken.None, scanProgress));
            StatusStripUpdate($"{statusPrefix}: scan complete. {scanResult.TotalFiles:N0} files, {FormatBytes(scanResult.TotalBytes)}, {scanResult.UnityBundleCount:N0} bundles.");
            return scanResult;
        }
        catch (Exception ex)
        {
            Logger.Warning($"{statusPrefix}: folder scan failed; continuing without SQLite cache validation. {ex.Message}");
            StatusStripUpdate($"{statusPrefix}: scan failed; indexing without cache validation.");
            return null;
        }
    }

    private async Task<RiskyLoadChoice> ConfirmFolderLoadIfRisky(string folderPath)
    {
        StatusStripUpdate("Scanning folder...");
        ProjectScanResult scanResult;
        using var scanCts = new CancellationTokenSource();
        var scanProgress = new Progress<ScanProgress>(p =>
        {
            if (p.TotalFiles > 0)
            {
                StatusStripUpdate($"Scanning folder... {p.ScannedFiles:N0}/{p.TotalFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
            else
            {
                StatusStripUpdate($"Scanning folder... {p.ScannedFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
        });
        try
        {
            scanResult = await Task.Run(() => ProjectScanner.ScanFolder(folderPath, scanCts.Token, scanProgress));
        }
        catch (OperationCanceledException)
        {
            StatusStripUpdate("Folder scan cancelled.");
            return RiskyLoadChoice.Cancel;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to scan folder before loading:\n{ex.Message}", "Folder scan failed");
            return RiskyLoadChoice.EagerLoad;
        }

        StatusStripUpdate($"Scan complete: {scanResult.TotalFiles:N0} files, {FormatBytes(scanResult.TotalBytes)}, {scanResult.UnityBundleCount:N0} bundles.");
        currentScanResult = scanResult;

        if (!scanResult.IsRisky)
        {
            return RiskyLoadChoice.EagerLoad;
        }

        var message = BuildRiskyProjectMessage(scanResult);
        return await ShowRiskyProjectDialog(message);
    }

    private async void LoadPathsProgressiveAsync(string[] paths)
    {
        ResetForm();
        assetsManager.Clear();
        assetsManager.LazyLoading = true;
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
            var folderCacheKey = GetFolderCacheKey(cacheTargetFolder);
            BundleFile.CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "DecompressedCache", folderCacheKey);
            Directory.CreateDirectory(BundleFile.CacheDirectory);
        }

        try
        {
            var files = new List<string>();
            if (paths.Length == 1 && Directory.Exists(paths[0]))
            {
                var folderPath = paths[0];
                SaveLoadFolder(folderPath);
                await Task.Run(() => ImportHelper.MergeSplitAssets(folderPath, true));
                var enumerated = await Task.Run(() => ImportHelper.GetFilesSafe(folderPath, "*.*", true));
                files = await Task.Run(() => ImportHelper.ProcessingSplitFiles(enumerated).ToList());
            }
            else
            {
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
                files = await Task.Run(() => ImportHelper.ProcessingSplitFiles(list).ToList());
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

            if (currentScanResult != null && paths.Length == 1 && Directory.Exists(paths[0]))
            {
                var previousIndexingState = TryLoadIndexingProgress(paths[0], currentScanResult);
                if (previousIndexingState != null
                    && !IsCompletedLazyIndexingStatus(previousIndexingState.Status))
                {
                    var lastReadFile = string.IsNullOrWhiteSpace(previousIndexingState.LastReadFile)
                        ? "none"
                        : Path.GetFileName(previousIndexingState.LastReadFile);
                    StatusStripUpdate(
                        $"Previous indexing state: {previousIndexingState.Status}, {previousIndexingState.PercentComplete:0.##}% complete, last file: {lastReadFile}");
                    ShowIndexingProgressPanel(previousIndexingState);
                }
            }

            // Check cache
            List<AssetHandle>? cachedHandles = null;
            if (currentScanResult != null && paths.Length == 1 && Directory.Exists(paths[0]))
            {
                cachedHandles = await Task.Run(() => LoadIndexCache(paths[0], currentScanResult));
            }

            if (cachedHandles != null && cachedHandles.Any(handle => string.IsNullOrWhiteSpace(handle.OriginalPath)))
            {
                Logger.Info("SQLite index cache is missing source paths; rebuilding it once.");
                cachedHandles = null;
            }

            if (cachedHandles != null)
            {
                ViewModel.IsIndexingActive = true;
                ViewModel.IsPauseEnabled = false;
                ViewModel.IsResumeEnabled = false;
                ViewModel.IsStopEnabled = false;
                ShowIndexingProgressPanel("running", 0, cachedHandles.Count, cachedHandles.Count, 0, string.Empty, string.Empty, null);

                try
                {
                    StatusStripUpdate("Loading project index from SQLite cache...");
                    var count = await LoadCachedHandlesIntoProjectIndexSmoothlyAsync(cachedHandles);
                    progressBar.Value = 100;
                    ViewModel.LoadingProgress = 100;
                    StatusStripUpdate($"Loaded from cache. Showing {visibleAssets.Count:N0} assets ({count:N0} newly visible).");

                    await BuildLazyConnectionsIfNeededAsync(paths);
                    if (ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(paths))
                    {
                        return;
                    }

                    if (currentScanResult != null
                        && paths.Length == 1
                        && Directory.Exists(paths[0])
                        && HasCompletedLazyStructureBuild(paths[0], currentScanResult))
                    {
                        var completedStructureState = TryLoadIndexingProgress(paths[0], currentScanResult);
                        if (completedStructureState != null)
                        {
                            ShowIndexingProgressPanel(completedStructureState);
                        }
                        SaveCurrentProjectAfterLoad(null, assetsManager.ProjectIndex.Count, exportableAssets.Count);
                        return;
                    }

                    await BuildAssetStructuresAsync(incremental: true, showStructureProgress: true);
                    return;
                }
                finally
                {
                    ViewModel.IsIndexingActive = false;
                    ViewModel.IsPauseEnabled = false;
                    ViewModel.IsResumeEnabled = false;
                    ViewModel.IsStopEnabled = false;
                }
            }

            StartProgressiveIndexing(files, paths);
        }
        catch (Exception ex)
        {
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
                    ViewModel.LoadingProgress = progressPercent;
                    var addedAssets = AppendNewLazyAssetsFromProjectIndex();
                    StatusStripUpdate($"{statusText} | Showing {visibleAssets.Count:N0} assets (+{addedAssets:N0})");
                });
            },
            (ex) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
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
                    AppendNewLazyAssetsFromProjectIndex();
                });
            },
            (folderPath, scanResult) =>
            {
                SaveIndexCache(folderPath, scanResult);
            },
            update =>
            {
                TrySaveIndexingProgress(paths, update);
                ShowIndexingProgressPanel(update);
            },
            (wasCancelled, finalLoadedCount, originalTotal) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
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
                            if (ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(paths))
                            {
                                return;
                            }
                        }

                        await BuildAssetStructuresAsync(incremental: true, showStructureProgress: !wasCancelled);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to finish lazy indexing: {ex}", ex);
                        StatusStripUpdate("Lazy indexing finalization failed.");
                    }
                });
            });
    }

    private async Task<int> LoadCachedHandlesIntoProjectIndexSmoothlyAsync(IReadOnlyList<AssetHandle> cachedHandles)
    {
        if (cachedHandles == null || cachedHandles.Count == 0)
        {
            return 0;
        }

        var totalVisible = 0;
        var totalHandles = cachedHandles.Count;
        for (var offset = 0; offset < totalHandles; offset += CachedIndexLoadBatchSize)
        {
            var start = offset;
            var count = Math.Min(CachedIndexLoadBatchSize, totalHandles - start);
            await Task.Run(() =>
            {
                for (var i = start; i < start + count; i++)
                {
                    var handle = cachedHandles[i];
                    RememberLazyHandleSourcePath(handle);
                    assetsManager.ProjectIndex.AddHandle(handle);
                }
            });

            totalVisible += AppendNewLazyAssetsFromProjectIndex(count);
            var processed = start + count;
            var percent = totalHandles == 0 ? 100 : Math.Min(100, processed * 100.0 / totalHandles);
            progressBar.Value = percent;
            ViewModel.LoadingProgress = (int)percent;
            ShowIndexingProgressPanel("running", processed, totalHandles, totalHandles - processed, percent, string.Empty, string.Empty, null);
            StatusStripUpdate($"Loading project index from SQLite cache... {processed:N0}/{totalHandles:N0} handles | Showing {visibleAssets.Count:N0} assets");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(1);
        }

        return totalVisible;
    }

    private int AppendNewLazyAssetsFromProjectIndex(int? maxHandlesToDrain = null)
    {
        if (!assetsManager.LazyLoading)
        {
            return 0;
        }

        var displayAllChecked = displayAll.IsChecked == true;
        var filterTypesChanged = false;
        var newExportableItems = new List<AssetItem>();

        var pendingHandles = maxHandlesToDrain.HasValue
            ? assetsManager.ProjectIndex.DrainPendingHandles(maxHandlesToDrain.Value)
            : assetsManager.ProjectIndex.DrainPendingHandles();

        foreach (var handle in pendingHandles)
        {
            if (handle == null || string.IsNullOrEmpty(handle.UniqueID))
            {
                continue;
            }

            RememberLazyHandleSourcePath(handle);

            if (!lazyAssetItemsByHandleId.TryGetValue(handle.UniqueID, out var assetItem))
            {
                assetItem = handle.Tag as AssetItem;
                if (assetItem == null)
                {
                    assetItem = new AssetItem(handle)
                    {
                        UniqueID = " #" + lazyAssetItemOrdinal.ToString(CultureInfo.InvariantCulture)
                    };
                    lazyAssetItemOrdinal++;
                }

                lazyAssetItemsByHandleId[handle.UniqueID] = assetItem;
            }
            else if (string.IsNullOrEmpty(assetItem.UniqueID))
            {
                assetItem.UniqueID = " #" + lazyAssetItemOrdinal.ToString(CultureInfo.InvariantCulture);
                lazyAssetItemOrdinal++;
            }

            UpdateLazyAssetItemHandle(assetItem, handle);
            handle.Tag = assetItem;

            if (!displayAllChecked && !IsLazyExportableType(handle.Type))
            {
                continue;
            }

            if (!exportableAssetHandleIds.Add(handle.UniqueID))
            {
                continue;
            }

            exportableAssets.Add(assetItem);
            newExportableItems.Add(assetItem);
            if (exportableAssetTypes.Add(assetItem.Type))
            {
                filterTypesChanged = true;
            }
        }

        if (filterTypesChanged)
        {
            BuildFilterTypeMenu();
        }

        AppendFilteredAssetsToVisible(newExportableItems);
        return newExportableItems.Count;
    }

    private static void UpdateLazyAssetItemHandle(AssetItem assetItem, AssetHandle handle)
    {
        assetItem.Handle = handle;
        assetItem.SourceFile = handle.SourceFile;
        assetItem.TypeString = handle.Type.ToString() ?? string.Empty;
        assetItem.Type = handle.Type;
        assetItem.PathID = handle.PathID;
        assetItem.PathIDString = handle.PathID.ToString(CultureInfo.InvariantCulture);
        assetItem.Size = handle.ByteSize;
        assetItem.FullSize = handle.ByteSize;
        assetItem.Name = handle.Name ?? string.Empty;
        assetItem.Container = handle.Container ?? string.Empty;
    }

    private void PauseIndexing_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.LoadingService.PauseIndexing();
        ViewModel.IsPauseEnabled = false;
        ViewModel.IsResumeEnabled = true;
        StatusStripUpdate("Indexing paused.");
        BuildAssetStructures(incremental: true);
    }

    private void ResumeIndexing_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.LoadingService.ResumeIndexing();
        ViewModel.IsPauseEnabled = true;
        ViewModel.IsResumeEnabled = false;
        StatusStripUpdate("Resuming indexing...");
    }

    private void StopIndexing_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.LoadingService.StopIndexing();
        ViewModel.IsIndexingActive = false;
        StatusStripUpdate("Stopping/cancelling indexing...");
    }

    private static string GetFolderCacheKey(string folderPath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folderPath)));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private void ClearCurrentProjectCache_Click(object? sender, RoutedEventArgs e)
    {
        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            MessageBox.Show(this, "No loaded project folder was found to clear.", "Clear project cache");
            return;
        }

        try
        {
            _sqliteCache.DeleteIndexCache(folderPath);
            DeleteDecompressedCacheFolder(folderPath);
            DeletePreviewCacheFolder(folderPath);

            lock (previewCacheLock)
            {
                meshToMaterialsCache = null;
                meshAssociatedRenderersCache = null;
                meshSourceTypesCache = null;
                materialMainTextureCache = null;
                materialPreviewMaterialCache = null;
                materialTextureSlotsCache = null;
            }

            StatusStripUpdate($"Cleared project cache for: {folderPath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to clear project cache:\n{ex.Message}", "Clear project cache");
            StatusStripUpdate("Failed to clear project cache.");
        }
    }

    private string GetCurrentCacheFolderPath()
    {
        if (!string.IsNullOrWhiteSpace(appSettings.LoadFolderPath) && Directory.Exists(appSettings.LoadFolderPath))
        {
            return appSettings.LoadFolderPath;
        }

        if (!string.IsNullOrWhiteSpace(assetsManager.ProjectRoot) && Directory.Exists(assetsManager.ProjectRoot))
        {
            return assetsManager.ProjectRoot;
        }

        return string.Empty;
    }

    private static void DeleteDecompressedCacheFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "DecompressedCache");
        var targetDirectory = Path.Combine(cacheRoot, GetFolderCacheKey(folderPath));
        DeleteDirectoryInsideRoot(cacheRoot, targetDirectory);
    }

    private static void DeletePreviewCacheFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "PreviewCache");
        var targetDirectory = Path.Combine(cacheRoot, GetFolderCacheKey(folderPath));
        DeleteDirectoryInsideRoot(cacheRoot, targetDirectory);
    }

    private MeshPreviewGeometryCache? LoadMeshPreviewGeometryCache(Mesh mesh, float densityPercent)
    {
        if (mesh.assetsFile == null || currentScanResult == null)
        {
            return null;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        try
        {
            var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            var parameters = BuildMeshPreviewCacheParameters(mesh, densityPercent);
            var entry = _sqliteCache.LoadPreviewCacheEntry(
                folderPath,
                signature,
                assetId,
                "mesh-geometry",
                MeshPreviewGeometryCache.AlgorithmVersion,
                parameters);

            if (entry == null || string.IsNullOrWhiteSpace(entry.PayloadPath) || !File.Exists(entry.PayloadPath))
            {
                return null;
            }

            if (!IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(entry.PayloadPath);
            if (!string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return MeshPreviewGeometryCacheSerializer.Deserialize(bytes);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load mesh preview geometry cache: {ex.Message}");
            return null;
        }
    }

    private void SaveMeshPreviewGeometryCache(Mesh mesh, float densityPercent, MeshPreviewGeometryCache cache)
    {
        if (mesh.assetsFile == null || currentScanResult == null || cache == null)
        {
            return;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            var bytes = MeshPreviewGeometryCacheSerializer.Serialize(cache);
            var hash = ComputeSha256Hex(bytes);
            var payloadPath = GetPreviewCachePayloadPath(folderPath, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

            if (!File.Exists(payloadPath))
            {
                var tempPath = payloadPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, payloadPath, overwrite: true);
            }

            var assetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            _sqliteCache.SavePreviewCacheEntry(
                folderPath,
                signature,
                assetId,
                "mesh-geometry",
                MeshPreviewGeometryCache.AlgorithmVersion,
                BuildMeshPreviewCacheParameters(mesh, densityPercent),
                hash,
                payloadPath,
                bytes.LongLength);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save mesh preview geometry cache: {ex.Message}");
        }
    }

    private TexturePreviewImageResult? LoadTexturePreviewThumbnail(Texture2D texture, int maxDimension)
    {
        if (texture == null)
        {
            return null;
        }

        var sourceWidth = texture.m_Width;
        var sourceHeight = texture.m_Height;
        var folderPath = GetCurrentCacheFolderPath();
        var canUsePersistentCache = texture.assetsFile != null
            && currentScanResult != null
            && !string.IsNullOrWhiteSpace(folderPath)
            && Directory.Exists(folderPath);

        if (canUsePersistentCache)
        {
            try
            {
                var assetId = AssetHandle.BuildUniqueID(texture.assetsFile!, texture.m_PathID);
                var signature = _sqliteCache.GetFolderSignature(currentScanResult!);
                var parameters = BuildTexturePreviewCacheParameters(texture, maxDimension);
                var entry = _sqliteCache.LoadPreviewCacheEntry(
                    folderPath,
                    signature,
                    assetId,
                    "texture-thumbnail-png",
                    TexturePreviewThumbnailAlgorithmVersion,
                    parameters);

                if (entry != null
                    && !string.IsNullOrWhiteSpace(entry.PayloadPath)
                    && File.Exists(entry.PayloadPath)
                    && IsPathInsideDirectory(GetPreviewCacheRoot(folderPath), entry.PayloadPath))
                {
                    var bytes = File.ReadAllBytes(entry.PayloadPath);
                    if (string.Equals(ComputeSha256Hex(bytes), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var cachedImage = SixLabors.ImageSharp.Image.Load<Bgra32>(bytes);
                        return new TexturePreviewImageResult(
                            cachedImage,
                            fromCache: true,
                            downscaled: sourceWidth > cachedImage.Width || sourceHeight > cachedImage.Height,
                            sourceWidth,
                            sourceHeight);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load texture thumbnail cache: {ex.Message}");
            }
        }

        var image = texture.ConvertToImage(true);
        if (image == null)
        {
            return null;
        }

        var downscaled = LimitPreviewImage(image, maxDimension);
        if (canUsePersistentCache)
        {
            try
            {
                using var stream = new MemoryStream();
                image.SaveAsPng(stream);
                var bytes = stream.ToArray();
                var hash = ComputeSha256Hex(bytes);
                var payloadPath = GetPreviewCachePayloadPath(folderPath, hash, ".texturepreview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

                if (!File.Exists(payloadPath))
                {
                    var tempPath = payloadPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(tempPath, bytes);
                    File.Move(tempPath, payloadPath, overwrite: true);
                }

                var assetId = AssetHandle.BuildUniqueID(texture.assetsFile!, texture.m_PathID);
                var signature = _sqliteCache.GetFolderSignature(currentScanResult!);
                _sqliteCache.SavePreviewCacheEntry(
                    folderPath,
                    signature,
                    assetId,
                    "texture-thumbnail-png",
                    TexturePreviewThumbnailAlgorithmVersion,
                    BuildTexturePreviewCacheParameters(texture, maxDimension),
                    hash,
                    payloadPath,
                    bytes.LongLength);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save texture thumbnail cache: {ex.Message}");
            }
        }

        return new TexturePreviewImageResult(image, fromCache: false, downscaled, sourceWidth, sourceHeight);
    }

    private static string BuildMeshPreviewCacheParameters(Mesh mesh, float densityPercent)
    {
        return string.Join("|",
            "density=" + densityPercent.ToString("0.###", CultureInfo.InvariantCulture),
            "vertices=" + mesh.m_VertexCount.ToString(CultureInfo.InvariantCulture),
            "indices=" + (mesh.m_Indices?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            "submeshes=" + (mesh.m_SubMeshes?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
            "bytes=" + mesh.byteSize.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildTexturePreviewCacheParameters(Texture2D texture, int maxDimension)
    {
        return string.Join("|",
            "max=" + maxDimension.ToString(CultureInfo.InvariantCulture),
            "width=" + texture.m_Width.ToString(CultureInfo.InvariantCulture),
            "height=" + texture.m_Height.ToString(CultureInfo.InvariantCulture),
            "format=" + texture.m_TextureFormat,
            "mips=" + texture.m_MipCount.ToString(CultureInfo.InvariantCulture),
            "bytes=" + texture.byteSize.ToString(CultureInfo.InvariantCulture),
            "streamSize=" + (texture.m_StreamData?.size ?? 0).ToString(CultureInfo.InvariantCulture),
            "streamOffset=" + (texture.m_StreamData?.offset ?? 0).ToString(CultureInfo.InvariantCulture));
    }

    private static string GetPreviewCacheRoot(string folderPath)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "PreviewCache",
            GetFolderCacheKey(folderPath));
    }

    private static string GetPreviewCachePayloadPath(string folderPath, string hash)
    {
        return GetPreviewCachePayloadPath(folderPath, hash, ".meshpreview");
    }

    private static string GetPreviewCachePayloadPath(string folderPath, string hash, string extension)
    {
        var safeHash = string.IsNullOrWhiteSpace(hash) ? "empty" : hash.ToLowerInvariant();
        var prefix = safeHash.Length >= 2 ? safeHash.Substring(0, 2) : "00";
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".cache" : extension;
        if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
        {
            safeExtension = "." + safeExtension;
        }

        return Path.Combine(GetPreviewCacheRoot(folderPath), prefix, safeHash + safeExtension);
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static bool IsPathInsideDirectory(string root, string path)
    {
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetFullPath = Path.GetFullPath(path);
        return targetFullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteDirectoryInsideRoot(string root, string targetDirectory)
    {
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetFullPath = Path.GetFullPath(targetDirectory);

        if (!targetFullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a cache directory outside the AssetStudio cache root.");
        }

        if (Directory.Exists(targetFullPath))
        {
            Directory.Delete(targetFullPath, recursive: true);
        }
    }

    private void SaveIndexCache(
        string folderPath,
        ProjectScanResult scanResult,
        bool preserveSemanticRelations = false,
        Action<int, int, string>? progress = null)
    {
        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            var unityVersion = assetsManager.SpecifyUnityVersion;
            var handles = assetsManager.ProjectIndex.GetHandles();
            PublishDatabaseWriteProgress(
                folderPath,
                scanResult,
                "saving_index",
                0,
                1,
                "Waiting for SQLite writer",
                persist: true);

            void ReportSaveProgress(int processed, int total, string stage)
            {
                progress?.Invoke(processed, total, stage);
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "saving_index",
                    processed,
                    total,
                    stage,
                    persist: false);
            }

            var saved = _sqliteCache.SaveIndexCache(folderPath, signature, scanResult, unityVersion, handles, preserveSemanticRelations, ReportSaveProgress);

            if (saved)
            {
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "completed",
                    1,
                    1,
                    "SQLite index cache saved",
                    persist: true);
            }
            else
            {
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "failed",
                    1,
                    1,
                    "SQLite index cache save failed",
                    persist: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save index cache: {ex.Message}");
        }
    }

    private void PublishDatabaseWriteProgress(
        string folderPath,
        ProjectScanResult scanResult,
        string status,
        int processed,
        int total,
        string stage,
        bool persist)
    {
        var safeTotal = Math.Max(1, total);
        var safeProcessed = Math.Clamp(processed, 0, safeTotal);
        var percent = Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal));
        var update = new IndexingProgressUpdate
        {
            Status = status,
            TotalFiles = safeTotal,
            ProcessedFiles = safeProcessed,
            PendingFiles = Math.Max(0, safeTotal - safeProcessed),
            PercentComplete = percent,
            CurrentFile = stage ?? string.Empty,
            LastReadFile = stage ?? string.Empty,
            NewlyReadFiles = Array.Empty<string>()
        };

        if (persist)
        {
            try
            {
                var signature = _sqliteCache.GetFolderSignature(scanResult);
                _sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to persist database write progress: {ex.Message}");
            }
        }

        ShowIndexingProgressPanel(update, 3);
    }

    private bool TrySaveSemanticRelations(SemanticAssetRelations relations)
    {
        if (currentScanResult == null)
        {
            return false;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        return TrySaveSemanticRelations(folderPath, currentScanResult, relations);
    }

    private bool TrySaveSemanticRelations(string folderPath, ProjectScanResult scanResult, SemanticAssetRelations relations, bool replaceExisting = false)
    {
        if (relations == null || (!relations.HasRelations && relations.SourceFiles.Count == 0) || scanResult == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            PublishDatabaseWriteProgress(
                folderPath,
                scanResult,
                "saving_connections",
                0,
                1,
                "Waiting for SQLite writer",
                persist: true);

            var saved = _sqliteCache.SaveSemanticRelations(
                folderPath,
                signature,
                relations,
                replaceExisting,
                (processed, total, stage) => PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "saving_connections",
                    processed,
                    total,
                    stage,
                    persist: false));

            if (saved)
            {
                PublishDatabaseWriteProgress(
                    folderPath,
                    scanResult,
                    "saving_connections",
                    1,
                    1,
                    "Semantic relations saved",
                    persist: false);
            }

            return saved;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save semantic relation cache: {ex.Message}");
            return false;
        }
    }

    private void TrySaveIndexingProgress(string[] paths, IndexingProgressUpdate update)
    {
        if (update == null || currentScanResult == null || paths.Length != 1 || !Directory.Exists(paths[0]))
        {
            return;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            _sqliteCache.SaveIndexingProgress(paths[0], signature, currentScanResult, update);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to persist indexing progress: {ex.Message}");
        }
    }

    private ProjectIndexingState? TryLoadIndexingProgress(string folderPath, ProjectScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            return _sqliteCache.LoadIndexingState(folderPath, signature);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load indexing progress: {ex.Message}");
            return null;
        }
    }

    private List<AssetHandle>? LoadIndexCache(string folderPath, ProjectScanResult scanResult)
    {
        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            return _sqliteCache.LoadIndexCache(folderPath, signature);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load index cache: {ex.Message}");
            return null;
        }
    }

    private bool HasCompletedLazyConnectionBuild(string folderPath, ProjectScanResult scanResult)
    {
        var state = TryLoadIndexingProgress(folderPath, scanResult);
        if (state == null || !IsConnectionReadyStatus(state.Status))
        {
            return false;
        }

        return HasSavedSemanticRelations(folderPath, scanResult);
    }

    private bool HasCompletedLazyStructureBuild(string folderPath, ProjectScanResult scanResult)
    {
        var state = TryLoadIndexingProgress(folderPath, scanResult);
        return string.Equals(state?.Status, "structure_completed", StringComparison.OrdinalIgnoreCase)
            && HasSavedSemanticRelations(folderPath, scanResult);
    }

    private bool HasSavedSemanticRelations(string folderPath, ProjectScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var signature = _sqliteCache.GetFolderSignature(scanResult);
            return _sqliteCache.HasSemanticRelations(folderPath, signature);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to inspect saved semantic relations: {ex.Message}");
            return false;
        }
    }

    private static bool IsConnectionReadyStatus(string? status)
    {
        return string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompletedLazyIndexingStatus(string? status)
    {
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanUseLazySemanticRelationCache(string folderPath)
    {
        return currentScanResult != null
            && !string.IsNullOrWhiteSpace(folderPath)
            && Directory.Exists(folderPath)
            && HasCompletedLazyConnectionBuild(folderPath, currentScanResult);
    }

    private async Task BuildLazyConnectionsIfNeededAsync(string[] paths, bool force = false)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || paths.Length != 1 || !Directory.Exists(paths[0]))
        {
            return;
        }

        var folderPath = paths[0];
        if (!force && HasCompletedLazyConnectionBuild(folderPath, currentScanResult))
        {
            StatusStripUpdate("Connections already built for this index.");
            return;
        }

        await BuildLazyConnectionsAsync(folderPath, currentScanResult);
    }

    private bool ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(string[] paths)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || paths.Length != 1 || !Directory.Exists(paths[0]))
        {
            return false;
        }

        var folderPath = paths[0];
        if (HasCompletedLazyConnectionBuild(folderPath, currentScanResult))
        {
            return false;
        }

        var state = TryLoadIndexingProgress(folderPath, currentScanResult);
        if (state != null)
        {
            ShowIndexingProgressPanel(state);
        }

        StatusStripUpdate("Connections did not produce saved relations; asset structure finalization was skipped.");
        return true;
    }

    private async Task BuildLazyConnectionsAsync(string folderPath, ProjectScanResult scanResult)
    {
        if (isBuildingLazyConnections)
        {
            return;
        }

        isBuildingLazyConnections = true;
        ViewModel.IsIndexingActive = true;
        ViewModel.IsPauseEnabled = false;
        ViewModel.IsResumeEnabled = false;
        ViewModel.IsStopEnabled = false;
        ViewModel.LoadingProgress = 0;
        
        // Show connecting progress panel immediately on the UI thread before background scan starts
        ShowIndexingProgressPanel("connecting", 0, 100, 100, 0, string.Empty, string.Empty, null);

        var lastSourcePath = string.Empty;
        var lastProcessedFiles = 0;
        var sourcePaths = new List<string>();

        try
        {
            StatusStripUpdate("Preparing connection build...");
            sourcePaths = await Task.Run(GetLazyConnectionSourcePaths);
            if (sourcePaths.Count == 0)
            {
                Logger.Warning("Unable to build lazy connections: no source files could be resolved from the project index.");
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", 0, 0, string.Empty, string.Empty);
                StatusStripUpdate("Connections build skipped: no source files resolved.");
                return;
            }

            StatusStripUpdate($"Building connections... 0/{sourcePaths.Count:N0} files");
            PublishLazyConnectionProgress(folderPath, scanResult, "connecting", 0, sourcePaths.Count, string.Empty, string.Empty);

            var buildResult = await Task.Run(async () => await BuildLazySemanticRelationsForSourcesAsync(
                sourcePaths,
                (processedFiles, currentSourcePath) =>
                {
                    lastSourcePath = currentSourcePath;
                    lastProcessedFiles = processedFiles;
                    PublishLazyConnectionProgress(
                        folderPath,
                        scanResult,
                        "connecting",
                        processedFiles,
                        sourcePaths.Count,
                        currentSourcePath,
                        currentSourcePath);

                    var percent = sourcePaths.Count == 0
                        ? 100
                        : Math.Min(100, Math.Max(0, processedFiles * 100.0 / sourcePaths.Count));
                    Dispatcher.UIThread.Post(() =>
                    {
                        ViewModel.LoadingProgress = (int)percent;
                        StatusStripUpdate($"Building connections... {processedFiles:N0}/{sourcePaths.Count:N0} files ({percent:0.#}%)");
                    }, DispatcherPriority.Background);
                }));
            var relations = buildResult.Relations;
            var diagnostics = buildResult.Diagnostics;
            var diagnosticSummary = diagnostics.FormatStatusSummary(sourcePaths.Count, relations);
            Logger.Info($"Lazy connection diagnostics: {diagnostics.FormatDetailedSummary(sourcePaths.Count, relations)}");

            if (!relations.HasMaterialRelations)
            {
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", sourcePaths.Count, sourcePaths.Count, diagnosticSummary, lastSourcePath);
                StatusStripUpdate($"Connections build produced no mesh/material/texture relations. {diagnosticSummary}");
                return;
            }

            var saved = await Task.Run(() => TrySaveSemanticRelations(folderPath, scanResult, relations, replaceExisting: true));
            if (!saved)
            {
                PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", sourcePaths.Count, sourcePaths.Count, diagnosticSummary, lastSourcePath);
                StatusStripUpdate($"Connections build finished, but saving to SQLite failed. {diagnosticSummary}");
                return;
            }

            PublishLazyConnectionProgress(folderPath, scanResult, "connections_completed", sourcePaths.Count, sourcePaths.Count, string.Empty, lastSourcePath);
            ViewModel.LoadingProgress = 100;
            StatusStripUpdate(
                $"Connections complete. Model groups: {relations.ModelGroups.Count:N0}, group meshes: {relations.ModelGroupMeshes.Count:N0}, edges: {relations.AssetEdges.Count:N0}, mesh materials: {relations.MeshMaterials.Count:N0}, material textures: {relations.MaterialTextures.Count:N0}.");
        }
        catch (MemoryPressureException ex)
        {
            PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", lastProcessedFiles, sourcePaths.Count, string.Empty, lastSourcePath);
            ShowMemoryPressureError(ex);
        }
        catch (Exception ex)
        {
            PublishLazyConnectionProgress(folderPath, scanResult, "connections_failed", lastProcessedFiles, sourcePaths.Count, string.Empty, lastSourcePath);
            Logger.Error($"Failed to build lazy asset connections: {ex}", ex);
            StatusStripUpdate("Connections build failed.");
        }
        finally
        {
            ViewModel.IsIndexingActive = false;
            ViewModel.IsPauseEnabled = false;
            ViewModel.IsResumeEnabled = false;
            ViewModel.IsStopEnabled = false;
            isBuildingLazyConnections = false;
        }
    }

    private void PublishLazyConnectionProgress(
        string folderPath,
        ProjectScanResult scanResult,
        string status,
        int processedFiles,
        int totalFiles,
        string currentFile,
        string lastReadFile)
    {
        var safeTotal = Math.Max(0, totalFiles);
        var safeProcessed = Math.Clamp(processedFiles, 0, safeTotal == 0 ? processedFiles : safeTotal);
        var update = new IndexingProgressUpdate
        {
            Status = status,
            TotalFiles = safeTotal,
            ProcessedFiles = safeProcessed,
            PendingFiles = Math.Max(0, safeTotal - safeProcessed),
            PercentComplete = safeTotal <= 0 ? 100 : Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal)),
            CurrentFile = currentFile ?? string.Empty,
            LastReadFile = lastReadFile ?? string.Empty,
            NewlyReadFiles = Array.Empty<string>()
        };

        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMs = (nowTicks - lastConnectionDbWriteTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        bool isTerminal = string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "connections_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

        if (processedFiles == 0 || processedFiles == safeTotal || isTerminal || elapsedMs >= 2000)
        {
            lastConnectionDbWriteTicks = nowTicks;
            try
            {
                var signature = _sqliteCache.GetFolderSignature(scanResult);
                _sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to persist connection build progress: {ex.Message}");
            }
        }

        ShowIndexingProgressPanel(update);
    }

    private void PublishStructureBuildProgress(
        string status,
        int processedSteps,
        int totalSteps,
        string currentStage,
        bool persist = true,
        int percentDecimals = 1)
    {
        var safeTotal = Math.Max(1, totalSteps);
        var safeProcessed = Math.Clamp(processedSteps, 0, safeTotal);
        var percentComplete = Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal));
        var update = new IndexingProgressUpdate
        {
            Status = status,
            TotalFiles = safeTotal,
            ProcessedFiles = safeProcessed,
            PendingFiles = Math.Max(0, safeTotal - safeProcessed),
            PercentComplete = percentComplete,
            CurrentFile = currentStage ?? string.Empty,
            LastReadFile = currentStage ?? string.Empty,
            NewlyReadFiles = Array.Empty<string>()
        };

        if (persist && currentScanResult != null)
        {
            var folderPath = GetCurrentCacheFolderPath();
            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                var scanResult = currentScanResult;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var signature = _sqliteCache.GetFolderSignature(scanResult);
                        _sqliteCache.SaveIndexingProgress(folderPath, signature, scanResult, update);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to persist structure build progress: {ex.Message}");
                    }
                });
            }
        }

        ShowIndexingProgressPanel(update, percentDecimals);
    }

    private List<string> GetLazyConnectionSourcePaths()
    {
        var sourcePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handle in assetsManager.ProjectIndex.GetHandles())
        {
            if (handle == null)
            {
                continue;
            }

            RememberLazyHandleSourcePath(handle);

            if (!LazyConnectionReferenceTypes.Contains(handle.Type))
            {
                continue;
            }

            var sourcePath = ResolveLazyHandleSourcePath(handle);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            if (seen.Contains(sourcePath))
            {
                continue;
            }

            if (failedPaths.Contains(sourcePath))
            {
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                failedPaths.Add(sourcePath);
                continue;
            }

            var fullPath = Path.GetFullPath(sourcePath);
            if (seen.Add(fullPath))
            {
                sourcePaths.Add(fullPath);
            }
        }

        sourcePaths.Sort(StringComparer.OrdinalIgnoreCase);
        return sourcePaths;
    }

    private async Task<LazyConnectionBuildResult> BuildLazySemanticRelationsForSourcesAsync(
        IReadOnlyList<string> sourcePaths,
        Action<int, string> reportProgress)
    {
        var mergedRelations = new SemanticAssetRelations();
        var diagnostics = new LazyConnectionBuildDiagnostics();
        diagnostics.SourceCount = sourcePaths.Count;
        var failedSources = new List<string>();
        const int batchSize = 32;

        for (var i = 0; i < sourcePaths.Count; i += batchSize)
        {
            var batch = sourcePaths.Skip(i).Take(batchSize).ToList();
            diagnostics.BatchCount++;
            try
            {
                await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
                AssetsManager.ThrowIfMemoryPressureTooHigh("building lazy connections");
                await assetsManager.LoadFilesAsync(batch.ToArray());

                for (var j = 0; j < batch.Count; j++)
                {
                    var sourcePath = batch[j];
                    var overallIndex = i + j;
                    try
                    {
                        var filesForSource = GetLoadedFilesForConnectionSource(sourcePath);
                        diagnostics.RecordLoadedFiles(sourcePath, filesForSource.Count);
                        if (filesForSource.Count == 0)
                        {
                            continue;
                        }

                        using (assetsManager.LruCache.SuspendEviction())
                        {
                            var objectsBefore = filesForSource.Sum(file => file.Objects.Count);
                            foreach (var file in filesForSource)
                            {
                                MaterializeReferenceObjects(file, LazyConnectionReferenceTypes, diagnostics);
                            }
                            var objectsAfter = filesForSource.Sum(file => file.Objects.Count);
                            diagnostics.RecordMaterializedSource(sourcePath, objectsBefore, objectsAfter);

                            BuildAssetReferenceIndexesBackground(
                                filesForSource,
                                new List<AssetItem>(),
                                out _,
                                out _,
                                out _,
                                out _,
                                out _,
                                out _,
                                out _,
                                out var semanticRelations);

                            mergedRelations.Merge(semanticRelations);
                            diagnostics.RecordRelationsPass(semanticRelations);
                        }
                        UnloadConnectionBuildObjects(filesForSource);
                    }
                    catch (Exception ex)
                    {
                        failedSources.Add(sourcePath);
                        diagnostics.FailedSources++;
                        Logger.Warning($"Failed to build connections for {Path.GetFileName(sourcePath)}: {ex.Message}");
                    }
                    finally
                    {
                        reportProgress(overallIndex + 1, sourcePath);
                    }
                }
            }
            catch (MemoryPressureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                foreach (var sourcePath in batch)
                {
                    failedSources.Add(sourcePath);
                    diagnostics.FailedSources++;
                    Logger.Warning($"Failed to load/build connections for {Path.GetFileName(sourcePath)} in batch: {ex.Message}");
                }
                for (var j = 0; j < batch.Count; j++)
                {
                    var sourcePath = batch[j];
                    var overallIndex = i + j;
                    reportProgress(overallIndex + 1, sourcePath);
                }
            }
            finally
            {
                await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
                assetsManager.ClearLoadedFilesKeepIndex();
            }
        }

        if (failedSources.Count > 0)
        {
            Logger.Warning($"Connections build completed with {failedSources.Count:N0} failed source file(s). First failure: {Path.GetFileName(failedSources[0])}");
        }

        return new LazyConnectionBuildResult(mergedRelations, diagnostics);
    }

    private List<SerializedFile> GetLoadedFilesForConnectionSource(string sourcePath)
    {
        lock (assetsManager.loadLock)
        {
            var directMatches = assetsManager.assetsFileList
                .Where(file => file != null
                    && (IsSameLazySource(file.originalPath, sourcePath)
                        || IsSameLazySource(file.fullName, sourcePath)))
                .ToList();
            if (directMatches.Count > 0)
            {
                return directMatches;
            }

            var expectedSerializedFileNames = assetsManager.ProjectIndex.GetHandles()
                .Where(handle => handle != null && IsSameLazySource(handle.OriginalPath, sourcePath))
                .Select(handle => handle.SerializedFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expectedSerializedFileNames.Count == 0)
            {
                return directMatches;
            }

            return assetsManager.assetsFileList
                .Where(file => file != null && expectedSerializedFileNames.Contains(file.fileName))
                .ToList();
        }
    }

    private void UnloadConnectionBuildObjects(IReadOnlyList<SerializedFile> files)
    {
        foreach (var file in files)
        {
            if (file == null)
            {
                continue;
            }

            foreach (var handle in assetsManager.ProjectIndex.GetHandlesForFile(file.fileName).ToList())
            {
                if (handle == null
                    || !ReferenceEquals(handle.SourceFile, file)
                    || !LazyConnectionReferenceTypes.Contains(handle.Type)
                    || IsLazyHandlePinnedForPreview(handle))
                {
                    continue;
                }

                UnloadAsset(handle);
            }
        }
    }

    private bool IsLazyHandlePinnedForPreview(AssetHandle handle)
    {
        if (handle == null || string.IsNullOrEmpty(handle.UniqueID))
        {
            return false;
        }

        lock (preloaderLock)
        {
            return preloadedUniqueIds.Contains(handle.UniqueID)
                || string.Equals(_currentlySelectedUniqueID, handle.UniqueID, StringComparison.Ordinal);
        }
    }

    private static string BuildRiskyProjectMessage(ProjectScanResult scanResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This folder contains a very large number of Unity bundles.");
        sb.AppendLine();
        sb.AppendLine($"Files: {scanResult.TotalFiles:N0}");
        sb.AppendLine($"Size on disk: {FormatBytes(scanResult.TotalBytes)}");
        sb.AppendLine($"Unity bundles: {scanResult.UnityBundleCount:N0}");
        sb.AppendLine($"Serialized files: {scanResult.SerializedFileCount:N0}");
        sb.AppendLine($"Resource files: {scanResult.ResourceFileCount:N0}");
        if (scanResult.ErrorCount > 0)
        {
            sb.AppendLine($"Scan errors: {scanResult.ErrorCount:N0}");
        }
        sb.AppendLine();
        sb.AppendLine($"Estimated RAM to load: {FormatBytes(scanResult.EstimatedMemoryBytes)}");
        if (scanResult.AvailableMemoryBytes > 0)
        {
            sb.AppendLine($"Available RAM: {FormatBytes(scanResult.AvailableMemoryBytes)}");
        }
        if (scanResult.IsMemoryRisky)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ The estimated memory exceeds available RAM. Loading may freeze the system or trigger the OOM killer.");
        }
        sb.AppendLine();
        sb.AppendLine("Loading all bundles at once can use far more memory than the project size on disk and may push Linux into swap.");
        sb.AppendLine("The safer alternative is Safe/Lazy Mode, which index-scans all files and only materializes assets on demand.");
        return sb.ToString();
    }

    private async Task<RiskyLoadChoice> ShowRiskyProjectDialog(string message)
    {
        var dialog = new Window
        {
            Title = "Large Unity project detected",
            Width = 640,
            Height = 440,
            MinWidth = 540,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new global::Avalonia.Thickness(16),
            RowSpacing = 12
        };

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock
        };

        var buttonPanel = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90
        };
        cancelButton.Click += (_, _) => dialog.Close(RiskyLoadChoice.Cancel);

        var lazyButton = new Button
        {
            Content = "Load in Safe/Lazy Mode (Recommended)",
            MinWidth = 240,
            FontWeight = global::Avalonia.Media.FontWeight.Bold
        };
        lazyButton.Click += (_, _) => dialog.Close(RiskyLoadChoice.LazyLoad);

        var loadButton = new Button
        {
            Content = "Load anyway (Eager)",
            MinWidth = 150
        };
        loadButton.Click += (_, _) => dialog.Close(RiskyLoadChoice.EagerLoad);

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(lazyButton);
        buttonPanel.Children.Add(loadButton);

        Grid.SetRow(scrollViewer, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(buttonPanel);
        dialog.Content = grid;

        return await dialog.ShowDialog<RiskyLoadChoice>(this);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private async Task<MemoryPressureResult> ShowMemoryPressureWarningDialog(string message)
    {
        var dialog = new Window
        {
            Title = "Memory pressure warning",
            Width = 600,
            Height = 240,
            MinWidth = 450,
            MinHeight = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new global::Avalonia.Thickness(16),
            RowSpacing = 12
        };

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock
        };

        var buttonPanel = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };

        var cancelButton = new Button
        {
            Content = "Cancel loading",
            MinWidth = 120
        };
        cancelButton.Click += (_, _) => dialog.Close(MemoryPressureResult.Cancel);

        var stopButton = new Button
        {
            Content = "Stop and keep loaded",
            MinWidth = 150
        };
        stopButton.Click += (_, _) => dialog.Close(MemoryPressureResult.StopAndKeep);

        var continueButton = new Button
        {
            Content = "Ignore and continue",
            MinWidth = 150,
            FontWeight = global::Avalonia.Media.FontWeight.Bold
        };
        continueButton.Click += (_, _) => dialog.Close(MemoryPressureResult.Continue);

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(stopButton);
        buttonPanel.Children.Add(continueButton);

        Grid.SetRow(scrollViewer, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(buttonPanel);
        dialog.Content = grid;

        return await dialog.ShowDialog<MemoryPressureResult>(this);
    }

    private void ShowMemoryPressureError(MemoryPressureException ex)
    {
        var msg = $"Loading was stopped because system memory usage reached {ex.MemoryLoadPercent}% (limit: {ex.LimitPercent}%).\n\n" +
                  $"Operation: {ex.Operation}\n\n" +
                  "Options:\n" +
                  "• Load fewer bundles at a time\n" +
                  "• Close other applications to free RAM\n" +
                  "• Raise the limit with ASSETSTUDIO_MEMORY_LIMIT_PERCENT (current: " + ex.LimitPercent + ")";
        StatusStripUpdate($"Loading stopped: memory pressure at {ex.MemoryLoadPercent}%.");
        MessageBox.Show(this, msg, "Memory pressure — loading stopped");
    }

    private int ExtractFolder(string path, string savePath)
    {
        var files = ImportHelper.GetFilesSafe(path, "*.*", true);
        var extractedCount = 0;
        Progress.Reset();

        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            var fileDirectory = Path.GetDirectoryName(file) ?? path;
            var fileSavePath = fileDirectory.Replace(path, savePath);
            extractedCount += ExtractFile(file, fileSavePath);
            Progress.Report(i + 1, files.Length);
        }

        return extractedCount;
    }

    private int ExtractFiles(string[] fileNames, string savePath)
    {
        var extractedCount = 0;
        Progress.Reset();

        for (var i = 0; i < fileNames.Length; i++)
        {
            extractedCount += ExtractFile(fileNames[i], savePath);
            Progress.Report(i + 1, fileNames.Length);
        }

        return extractedCount;
    }

    private int ExtractFile(string fileName, string savePath)
    {
        using var reader = new FileReader(fileName);
        return reader.FileType switch
        {
            FileType.BundleFile => ExtractBundleFile(reader, savePath),
            FileType.WebFile => ExtractWebDataFile(reader, savePath),
            _ => 0
        };
    }

    private int ExtractBundleFile(FileReader reader, string savePath)
    {
        StatusStripUpdate($"Decompressing {reader.FileName} ...");
        var fileName = reader.FileName;
        using var bundleFile = new BundleFile(reader);
        if (bundleFile.fileList.Length == 0) return 0;

        var extractPath = Path.Combine(savePath, fileName + "_unpacked");
        return ExtractStreamFiles(extractPath, bundleFile.fileList);
    }

    private int ExtractWebDataFile(FileReader reader, string savePath)
    {
        StatusStripUpdate($"Decompressing {reader.FileName} ...");
        var fileName = reader.FileName;
        var webFile = new WebFile(reader);
        if (webFile.fileList.Length == 0) return 0;

        var extractPath = Path.Combine(savePath, fileName + "_unpacked");
        return ExtractStreamFiles(extractPath, webFile.fileList);
    }

    private static int ExtractStreamFiles(string extractPath, StreamFile[] fileList)
    {
        var extractedCount = 0;
        foreach (var file in fileList)
        {
            var filePath = Path.Combine(extractPath, file.path);
            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            if (!File.Exists(filePath))
            {
                using var fileStream = File.Create(filePath);
                file.stream.CopyTo(fileStream);
                extractedCount++;
            }
            file.stream.Dispose();
        }
        return extractedCount;
    }

    private async void BuildAssetStructures(bool incremental = false)
    {
        await BuildAssetStructuresAsync(incremental);
    }

    private async Task BuildAssetStructuresAsync(bool incremental = false, bool showStructureProgress = false)
    {
        if (isBuildingAssetStructures) return;
        isBuildingAssetStructures = true;
        var publishStructureProgress = showStructureProgress && assetsManager.LazyLoading && currentScanResult != null;
        const int structureBuildSteps = 6;

        void PublishStructureStage(int processedSteps, string stage)
        {
            if (publishStructureProgress)
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
        bool displayAllChecked = displayAll.IsChecked == true;
        List<SerializedFile> filesListSnapshot;
        lock (assetsManager.loadLock)
        {
            filesListSnapshot = assetsManager.assetsFileList.ToList();
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

            int i = 0;

            if (assetsManager.LazyLoading)
            {
                foreach (var assetsFile in filesListSnapshot)
                {
                    YieldBackgroundWorkForUserInteraction();

                    foreach (var asset in assetsFile.Objects)
                    {
                        if (asset is AssetBundle m_AssetBundle)
                        {
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (int k = preloadIndex; k < preloadEnd; k++)
                                {
                                    localContainers.Add((m_AssetBundle.m_PreloadTable[k], m_Container.Key));
                                }
                            }
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

                var handles = assetsManager.ProjectIndex.GetHandles().ToArray();
                BuildLazyAssetItemsBackground(
                    handles,
                    displayAllChecked,
                    localPathIDAssetItemDic,
                    localObjectAssetItemDic,
                    localExportableAssets,
                    localNewExportableAssets);
                i += handles.Length;
            }
            else
            {
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
            }

            PublishStructureStage(2, "Linking asset items");
            if (!assetsManager.LazyLoading)
            {
                LinkAssetItemsToSceneNodesBackground(filesListSnapshot, localTreeNodeDictionary, localObjectAssetItemDic);
            }

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

            if (!assetsManager.LazyLoading)
            {
                LinkFbxSubAssetsToSceneNodesBackground(localExportableAssets, localSceneTreeNodes);
            }
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
            if (!assetsManager.LazyLoading)
            {
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
            }

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

        await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);
        PublishStructureStage(5, "Applying asset structure to UI");

        // Apply results back on the UI thread
        var newExportableAssets = result.NewExportableAssets;
        bool useIncrementalPath = incremental && exportableAssets.Count > 0 && newExportableAssets != null;

        if (useIncrementalPath && newExportableAssets!.Count == 0)
        {
            exportableAssets = result.ExportableAssets;
            ApplyExportableAssetIndexes(result);
            assetClassItems = result.AssetClassItems;
            BuildFilterTypeMenu();
            UpdateAssetClassesIncremental(result.AssetClassItems);
        }
        else if (useIncrementalPath)
        {
            // Incremental path: only append new items to avoid O(n) DataGrid notifications
            exportableAssets = result.ExportableAssets;
            ApplyExportableAssetIndexes(result);
            assetClassItems = result.AssetClassItems;

            BuildFilterTypeMenu();
            AppendFilteredAssetsToVisible(newExportableAssets!);
            UpdateAssetClassesIncremental(result.AssetClassItems);
        }
        else
        {
            // Full rebuild path (initial load, display all toggle, etc.)
            exportableAssets = result.ExportableAssets;
            ApplyExportableAssetIndexes(result);
            sceneTreeNodes = result.SceneTreeNodes;
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

        var log = $"Finished loading {assetsManager.assetsFileList.Count} files with {exportableAssets.Count} exportable assets";
        var m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.Count);
        var objectsCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
        if (m_ObjectsCount != objectsCount)
        {
            var deferredCount = m_ObjectsCount - objectsCount;
            log += assetsManager.LazyLoading
                ? $" and {deferredCount:N0} objects indexed for lazy loading"
                : $" and {deferredCount:N0} assets failed to read";
        }
        StatusStripUpdate(log);

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        if (assetsManager.assetsFileList.Count > 0)
        {
            var firstFile = assetsManager.assetsFileList[0];
            if (!string.IsNullOrEmpty(result.ProductName))
            {
                Title = $"AssetStudio v{version} - {result.ProductName} - {firstFile.unityVersion} - {firstFile.m_TargetPlatform}";
            }
            else
            {
                Title = $"AssetStudio v{version} - no productName - {firstFile.unityVersion} - {firstFile.m_TargetPlatform}";
            }
        }
        else
        {
            Title = projectContext == null
                ? $"AssetStudio v{version}"
                : $"AssetStudio v{version} - {projectContext.Project.DisplayName}";
        }

        var assetCountForStats = assetsManager.LazyLoading
            ? assetsManager.ProjectIndex.Count
            : m_ObjectsCount;
        PublishStructureStage(6, "Saving project metadata");
        await Task.Run(() => SaveCurrentProjectAfterLoad(result.ProductName, assetCountForStats, exportableAssets.Count));

        if (!assetsManager.LazyLoading)
        {
            await Task.Run(() => TrySaveSemanticRelations(result.SemanticRelations));
        }
        if (publishStructureProgress)
        {
            var folderPath = GetCurrentCacheFolderPath();
            if (!assetsManager.LazyLoading || (currentScanResult != null && HasSavedSemanticRelations(folderPath, currentScanResult)))
            {
                PublishStructureBuildProgress("structure_completed", structureBuildSteps, structureBuildSteps, "Asset structure ready");
            }
            else
            {
                PublishStructureBuildProgress("structure_failed", structureBuildSteps, structureBuildSteps, "Asset structure built without saved connections");
            }
        }
        }
        catch
        {
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

    private void LinkAssetItemsToSceneNodes(Dictionary<GameObject, GameObjectNode> treeNodeDictionary, Dictionary<Object, AssetItem> objectAssetItemDic)
    {
        LinkAssetItemsToSceneNodesBackground(assetsManager.assetsFileList, treeNodeDictionary, objectAssetItemDic);
    }

    private async void AssetListDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingFilterList) return;

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var selectedItem = sender is DataGrid grid ? grid.SelectedItem : AssetListDataGrid.SelectedItem;
        if (selectedItem is AssetItem assetItem)
        {
            var id = assetItem.Handle != null ? assetItem.Handle.UniqueID : assetItem.UniqueID;
            _currentlySelectedUniqueID = id;

            if (RightTabControl.SelectedIndex == 1)
            {
                await UpdateDumpForSelectedAsset();
            }
            UpdatePreloadWindow(assetItem);
            QueuePreviewAsset(assetItem);
        }
        else
        {
            _currentlySelectedUniqueID = null;
            DumpTextBox.Text = string.Empty;
            previewDebounce?.Cancel();
            ClearPreview("Preview Panel");
            lock (preloaderLock)
            {
                preloaderCts?.Cancel();
            }
        }
    }

    private async void RightTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == RightTabControl)
        {
            PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
            if (RightTabControl.SelectedIndex == 1)
            {
                CancelPendingPreview();
                await UpdateDumpForSelectedAsset();
            }
            else if (RightTabControl.SelectedIndex == 0)
            {
                PreviewSelectedAssetImmediately();
            }
        }
    }

    private void CancelPendingPreview()
    {
        previewDebounce?.Cancel();
    }

    private void PreviewSelectedAssetImmediately()
    {
        if (AssetListDataGrid.SelectedItem is not AssetItem assetItem)
        {
            ClearPreview("Preview Panel");
            return;
        }

        CancelPendingPreview();
        QueuePreviewAsset(assetItem, immediate: true);
    }

    private void QueuePreviewAsset(AssetItem assetItem, bool immediate = false)
    {
        if (RightTabControl.SelectedIndex != 0)
        {
            return;
        }

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        previewDebounce?.Cancel();
        previewDebounce?.Dispose();
        previewDebounce = new CancellationTokenSource();
        var token = previewDebounce.Token;
        var previewId = ++texturePreviewIdCounter;

        if (PreviewLabel != null)
        {
            PreviewLabel.IsVisible = displayInfo.IsChecked == true;
            PreviewLabel.Text = displayInfo.IsChecked == true ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate)
                {
                    await Task.Delay(PreviewDebounceMilliseconds, token);
                }

                PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
                var resolvedAsset = await Task.Run(() => ResolveAssetForPreview(assetItem), token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested
                        && previewId == texturePreviewIdCounter
                        && RightTabControl.SelectedIndex == 0
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        PreviewAsset(assetItem, resolvedAsset, assetResolutionAttempted: true);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Preview queue failed for {assetItem.Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        StatusStripUpdate($"Preview error: {ex.Message}");
                    }
                });
            }
        }, token);
    }

    private void SetTextWithTruncation(TextBox textBox, string? text, string fallbackText = "")
    {
        if (text == null)
        {
            textBox.Text = fallbackText;
            return;
        }

        const int maxChars = 100000;
        if (text.Length > maxChars)
        {
            textBox.Text = text.Substring(0, maxChars) + 
                $"{Environment.NewLine}...{Environment.NewLine}[Preview truncated: content is too large ({text.Length:N0} characters). Please export the asset to view full content]";
        }
        else
        {
            textBox.Text = text;
        }
    }

    private async Task UpdateDumpForSelectedAsset()
    {
        if (AssetListDataGrid.SelectedItem is not AssetItem assetItem)
        {
            DumpTextBox.Text = string.Empty;
            return;
        }

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        DumpTextBox.Text = "Loading dump...";
        var shouldPauseIndexing = assetsManager.LazyLoading && assetItem.Handle != null;
        if (shouldPauseIndexing)
        {
            Interlocked.Increment(ref foregroundLazyLoadCount);
        }
        try
        {
            var asset = await Task.Run(() =>
            {
                if (assetsManager.LazyLoading && assetItem.Handle != null)
                {
                    EnsureLazyAssetReadyForPreview(assetItem);
                }

                return assetItem.Asset;
            });
            if (!ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }

            if (asset == null)
            {
                DumpTextBox.Text = "No Dump Available";
                return;
            }

            var dump = await DumpAsset(asset);
            if (!ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }
            SetTextWithTruncation(DumpTextBox, dump, "No Dump Available");
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }
            DumpTextBox.Text = $"Dump {assetItem.Type}:{assetItem.Name} error{Environment.NewLine}{ex.Message}{Environment.NewLine}{ex.StackTrace}";
        }
        finally
        {
            if (shouldPauseIndexing)
            {
                Interlocked.Decrement(ref foregroundLazyLoadCount);
            }
        }
    }

    private async Task<string?> DumpAsset(Object asset)
    {
        if (asset is MonoBehaviour monoBehaviour)
        {
            var dump = await Task.Run(() => monoBehaviour.Dump());
            if (dump == null)
            {
                var typeTree = await MonoBehaviourToTypeTree(monoBehaviour);
                dump = await Task.Run(() => monoBehaviour.Dump(typeTree));
            }
            return dump;
        }
        else
        {
            return await Task.Run(() => asset.Dump());
        }
    }

    private async Task<TypeTree> MonoBehaviourToTypeTree(MonoBehaviour monoBehaviour)
    {
        if (!assemblyLoader.Loaded)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select Assembly Folder"));

                if (folders != null && folders.Count > 0)
                {
                    SaveLoadFolder(folders[0].Path.LocalPath);
                    assemblyLoader.Load(folders[0].Path.LocalPath);
                }
                else
                {
                    assemblyLoader.Loaded = true;
                }
            }
            else
            {
                assemblyLoader.Loaded = true;
            }
        }

        return monoBehaviour.ConvertToTypeTree(assemblyLoader);
    }

    private AssetStudio.Object? ResolveAssetForPreview(AssetItem assetItem)
    {
        var shouldPauseIndexing = assetsManager.LazyLoading && assetItem.Handle != null;
        if (shouldPauseIndexing)
        {
            Interlocked.Increment(ref foregroundLazyLoadCount);
        }

        try
        {
            if (assetsManager.LazyLoading && assetItem.Handle != null)
            {
                EnsureLazyAssetReadyForPreview(assetItem);
            }

            return assetItem.Asset;
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Error resolving asset for preview {assetItem.Name}: {ex}");
            return null;
        }
        finally
        {
            if (shouldPauseIndexing)
            {
                Interlocked.Decrement(ref foregroundLazyLoadCount);
            }
        }
    }

    private void EnsureLazyAssetReadyForPreview(AssetItem assetItem)
    {
        var handle = assetItem.Handle;
        if (handle == null || handle.RealObject != null)
        {
            return;
        }

        lock (handle)
        {
            if (handle.RealObject != null)
            {
                return;
            }

            if (handle.SourceFile?.reader != null)
            {
                UpdateLazyAssetItemHandle(assetItem, handle);
                return;
            }

            TryAttachLoadedSourceFile(handle);
            if (handle.SourceFile?.reader != null)
            {
                UpdateLazyAssetItemHandle(assetItem, handle);
                return;
            }

            var sourcePath = ResolveLazyHandleSourcePath(handle);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                logger.Log(LoggerEvent.Warning, $"Lazy preview source file not found for {handle.TypeString} PathID={handle.PathID}: {handle.OriginalPath}");
                return;
            }

            RemovePendingFileFromProgressiveQueue(sourcePath);
            Dispatcher.UIThread.Post(() => StatusStripUpdate($"Loading preview source: {Path.GetFileName(sourcePath)}"));

            TryAttachLoadedSourceFile(handle);
            if (handle.SourceFile?.reader == null)
            {
                assetsManager.LoadFilesForPreview(sourcePath);
                assetsManager.WaitForAssetsFileLoaded(handle.SerializedFileName, 5000);
                TryAttachLoadedSourceFile(handle);
            }
            UpdateLazyAssetItemHandle(assetItem, handle);

            var preserveSemanticPreviewCaches = CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath());
            lock (previewCacheLock)
            {
                if (!preserveSemanticPreviewCaches)
                {
                    meshToMaterialsCache = null;
                    materialMainTextureCache = null;
                    materialPreviewMaterialCache = null;
                    materialTextureSlotsCache = null;
                }

                meshAssociatedRenderersCache = null;
                meshSourceTypesCache = null;
                objectToAssetItemCache = null;
                animationClipAvatarCache = null;
                avatarMeshCache = null;
                avatarMeshesCache = null;
                meshAvatarCache = null;
                meshSkinnedRenderersCache = null;
                animationClipTransformBindingsCache = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                var addedAssets = AppendNewLazyAssetsFromProjectIndex();
                if (addedAssets > 0)
                {
                    StatusStripUpdate($"Loaded preview source: {Path.GetFileName(sourcePath)} | Showing {visibleAssets.Count:N0} assets (+{addedAssets:N0})");
                }
            });
        }
    }

    private AssetStudio.Object? ResolveSemanticRelationHandleForPreview(AssetHandle handle)
    {
        if (handle == null)
        {
            return null;
        }

        if (handle.Tag is AssetItem assetItem)
        {
            EnsureLazyAssetReadyForPreview(assetItem);
            return assetItem.Asset;
        }

        if (!assetsManager.LazyLoading)
        {
            return assetsManager.ResolveHandle(handle);
        }

        lock (handle)
        {
            if (handle.RealObject != null)
            {
                return handle.RealObject;
            }

            TryAttachLoadedSourceFile(handle);
            if (handle.SourceFile?.reader == null)
            {
                var sourcePath = ResolveLazyHandleSourcePath(handle);
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    try
                    {
                        RemovePendingFileFromProgressiveQueue(sourcePath);
                        assetsManager.LoadFilesForPreview(sourcePath);
                        assetsManager.WaitForAssetsFileLoaded(handle.SerializedFileName, 5000);
                        TryAttachLoadedSourceFile(handle);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to load semantic relation source {Path.GetFileName(sourcePath)}: {ex.Message}");
                    }
                }
            }

            return assetsManager.ResolveHandle(handle);
        }
    }

    private void RememberLazySourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var fileName = GetSafeFileName(sourcePath);
        RememberLazySourcePathForSerializedFile(fileName, sourcePath);
    }

    private void RememberLazyHandleSourcePath(AssetHandle handle)
    {
        if (handle == null || string.IsNullOrWhiteSpace(handle.OriginalPath))
        {
            return;
        }

        RememberLazySourcePathForSerializedFile(handle.SerializedFileName, handle.OriginalPath);
        RememberLazySourcePathForSerializedFile(GetSafeFileName(handle.OriginalPath), handle.OriginalPath);
    }

    private void RememberLazySourcePathForSerializedFile(string? serializedFileName, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(serializedFileName) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        lazySourcePathBySerializedFile[serializedFileName] = sourcePath;
    }

    private static string? GetSafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private string? FindSourceFileByNameInProjectRoot(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(assetsManager.ProjectRoot))
        {
            return null;
        }

        var cached = lazySourceFileSearchCache.GetOrAdd(fileName, key =>
        {
            var direct = TryResolveExistingPath(Path.Combine(assetsManager.ProjectRoot, key));
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }

            try
            {
                return ImportHelper.GetFilesSafe(assetsManager.ProjectRoot, key, true)
                    .Select(TryResolveExistingPath)
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        });

        return string.IsNullOrEmpty(cached) ? null : cached;
    }

    private string? ResolvePathBySuffix(string originalPath, string projectRoot)
    {
        if (string.IsNullOrEmpty(originalPath) || string.IsNullOrEmpty(projectRoot))
        {
            return null;
        }

        string cleanOriginal;
        string cleanRoot;
        try
        {
            cleanOriginal = Path.GetFullPath(originalPath).Replace('/', '\\');
            cleanRoot = Path.GetFullPath(projectRoot).Replace('/', '\\');
        }
        catch
        {
            return null;
        }

        var parts = cleanOriginal.Split('\\');
        for (int i = 1; i < parts.Length; i++)
        {
            var suffix = string.Join("\\", parts.Skip(i));
            var candidate = Path.Combine(cleanRoot, suffix);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private string? ResolveLazyHandleSourcePath(AssetHandle handle)
    {
        if (handle == null)
        {
            return null;
        }
        var cacheKey = $"{handle.SerializedFileName}|{handle.OriginalPath}";
        if (resolvedSourcePathCache.TryGetValue(cacheKey, out var cachedPath))
        {
            return string.IsNullOrEmpty(cachedPath) ? null : cachedPath;
        }

        var resolved = ResolveLazyHandleSourcePathInternal(handle);
        resolvedSourcePathCache[cacheKey] = resolved ?? string.Empty;
        return resolved;
    }

    private string? ResolveLazyHandleSourcePathInternal(AssetHandle handle)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(handle.SourceFile?.originalPath))
        {
            candidates.Add(handle.SourceFile.originalPath);
        }

        if (!string.IsNullOrWhiteSpace(handle.SourceFile?.fullName))
        {
            candidates.Add(handle.SourceFile.fullName);
        }

        if (!string.IsNullOrWhiteSpace(handle.OriginalPath))
        {
            candidates.Add(handle.OriginalPath);
            if (!Path.IsPathRooted(handle.OriginalPath) && !string.IsNullOrEmpty(assetsManager.ProjectRoot))
            {
                candidates.Add(Path.Combine(assetsManager.ProjectRoot, handle.OriginalPath));
            }
        }

        if (!string.IsNullOrWhiteSpace(handle.SerializedFileName)
            && lazySourcePathBySerializedFile.TryGetValue(handle.SerializedFileName, out var mappedSourcePath))
        {
            candidates.Add(mappedSourcePath);
        }

        var originalFileName = GetSafeFileName(handle.OriginalPath);
        if (!string.IsNullOrWhiteSpace(originalFileName)
            && lazySourcePathBySerializedFile.TryGetValue(originalFileName, out var mappedOriginalFilePath))
        {
            candidates.Add(mappedOriginalFilePath);
        }

        if (!string.IsNullOrWhiteSpace(handle.SerializedFileName) && !string.IsNullOrEmpty(assetsManager.ProjectRoot))
        {
            candidates.Add(Path.Combine(assetsManager.ProjectRoot, handle.SerializedFileName));
        }

        foreach (var pending in ViewModel.LoadingService.PendingFilesToIndex)
        {
            var pendingFileName = GetSafeFileName(pending);
            if (string.Equals(pending, handle.OriginalPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pendingFileName, originalFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pendingFileName, handle.SerializedFileName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(pending);
            }
        }

        foreach (var candidate in candidates)
        {
            var resolved = TryResolveExistingPath(candidate);
            if (resolved != null)
            {
                return resolved;
            }
        }

        if (!string.IsNullOrEmpty(handle.OriginalPath) && !string.IsNullOrEmpty(assetsManager.ProjectRoot))
        {
            var suffixMatch = ResolvePathBySuffix(handle.OriginalPath, assetsManager.ProjectRoot);
            if (suffixMatch != null)
            {
                return suffixMatch;
            }
        }

        var foundByOriginalName = FindSourceFileByNameInProjectRoot(originalFileName);
        if (foundByOriginalName != null)
        {
            return foundByOriginalName;
        }

        var foundBySerializedName = FindSourceFileByNameInProjectRoot(handle.SerializedFileName);
        if (foundBySerializedName != null)
        {
            return foundBySerializedName;
        }

        return null;
    }

    private void RemovePendingFileFromProgressiveQueue(string sourcePath)
    {
        ViewModel.LoadingService.RemovePendingFile(sourcePath);
    }

    private void TryAttachLoadedSourceFile(AssetHandle handle)
    {
        if (handle.SourceFile?.reader != null)
        {
            return;
        }

        if (assetsManager.TryFindSerializedFile(handle.SerializedFileName, handle.OriginalPath, out var sourceFile) && sourceFile != null)
        {
            handle.SourceFile = sourceFile;
            if (string.IsNullOrEmpty(handle.OriginalPath) && !string.IsNullOrEmpty(sourceFile.originalPath))
            {
                handle.OriginalPath = sourceFile.originalPath;
            }
            RememberLazyHandleSourcePath(handle);
        }
    }

    private void EnsureLazyAssetsLoadedForExport(List<AssetItem> items)
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        var unloadedItems = items
            .Where(x => x.Handle != null && x.Handle.SourceFile?.reader == null)
            .ToList();

        if (unloadedItems.Count == 0)
        {
            return;
        }

        var uniqueSourcePaths = unloadedItems
            .Select(x => ResolveLazyHandleSourcePath(x.Handle!))
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniqueSourcePaths.Count > 0)
        {
            Dispatcher.UIThread.Post(() => StatusStripUpdate($"Loading {uniqueSourcePaths.Count} source file(s) for export..."));
            foreach (var path in uniqueSourcePaths)
            {
                try
                {
                    assetsManager.LoadFilesForPreview(path);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load source file {path} for export: {ex.Message}");
                }
            }

            foreach (var item in unloadedItems)
            {
                if (item.Handle != null)
                {
                    assetsManager.WaitForAssetsFileLoaded(item.Handle.SerializedFileName, 5000);
                    TryAttachLoadedSourceFile(item.Handle);
                    UpdateLazyAssetItemHandle(item, item.Handle);
                }
            }
        }
    }

    private void PreviewAsset(AssetItem assetItem, AssetStudio.Object? resolvedAsset = null, bool assetResolutionAttempted = false)
    {
        ++texturePreviewIdCounter;
        if (enablePreview.IsChecked != true)
        {
            ClearPreview("Preview disabled");
            return;
        }

        var asset = assetResolutionAttempted ? resolvedAsset : (resolvedAsset ?? ResolveAssetForPreview(assetItem));
        if (asset == null)
        {
            ClearPreview("Preview Panel");
            SetTextWithTruncation(TextPreviewBox,
                $"Preview could not load this asset on demand.{Environment.NewLine}" +
                $"Asset: {assetItem.Name}{Environment.NewLine}" +
                $"Type: {assetItem.DisplayType}{Environment.NewLine}" +
                $"PathID: {assetItem.PathID}");
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate($"Preview unavailable for {assetItem.Name}.");
            return;
        }

        TextPreviewBox.IsVisible = false;
        TextPreviewBox.FontFamily = global::Avalonia.Media.FontFamily.Default;
        TextPreviewBox.FontSize = 14;
        ClearTextAssetPreview();
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.Source = null;
            ImagePreviewBox.IsVisible = false;
        }
        if (GLPreviewControl != null && asset is not Material)
        {
            GLPreviewControl.IsVisible = false;
        }
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }
        if (PreviewInfoBorder != null)
        {
            PreviewInfoBorder.IsVisible = false;
        }
        if (AudioPanel != null)
        {
            AudioPanel.IsVisible = false;
            if (asset is not AudioClip)
            {
                AudioReset();
            }
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            if (asset is not VideoClip && asset is not VideoPlayer)
            {
                VideoReset();
            }
        }
        ClearMeshMaterialControls();
        ClearPreviewCandidateControls();
        if (asset is not AnimationClip)
        {
            Stop2DAnimation();
            HideAnimationPlayback();
            GLPreviewControl?.StopAnimation();
            currentPreviewMesh = null;
            currentPreviewAvatar = null;
        }
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        currentPreviewAudioClip = null;
        currentPreviewVideoClip = null;

        PreviewLabel.IsVisible = displayInfo.IsChecked == true;
        PreviewLabel.Text = displayInfo.IsChecked == true ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;

        string fbxHeader = string.Empty;
        if (assetItem.DisplayType.Contains("FBX sub-asset"))
        {
            var fbxNodeName = assetItem.TreeNode != null ? assetItem.TreeNode.Name : "[None]";
            fbxHeader = $"[FBX Sub-Asset Container: {Path.GetFileName(assetItem.Container)}]" + Environment.NewLine +
                        $"Associated Scene Hierarchy Node: {fbxNodeName}" + Environment.NewLine +
                        $"(Right-click this item and choose 'Go to scene hierarchy' to view context)" + Environment.NewLine +
                        $"--------------------------------------------------" + Environment.NewLine + Environment.NewLine;
        }

        try
        {
            switch (asset)
            {
                case AudioClip m_AudioClip:
                    PreviewAudioClip(assetItem, m_AudioClip);
                    break;
                case Texture2D m_Texture2D:
                    PreviewTexture2D(assetItem, m_Texture2D);
                    break;
                case Sprite m_Sprite:
                    PreviewSprite(assetItem, m_Sprite);
                    break;
                case AssetStudio.Font m_Font:
                    PreviewFont(assetItem, m_Font);
                    break;
                case Material m_Material:
                    PreviewMaterial(assetItem, m_Material);
                    break;
                case TextAsset m_TextAsset:
                    PreviewTextAsset(assetItem, m_TextAsset, fbxHeader);
                    break;
                case Shader m_Shader:
                    SetTextWithTruncation(TextPreviewBox, fbxHeader + (m_Shader.Convert() ?? "Serialized Shader can't be read"));
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    string? dumpStr = null;
                    try
                    {
                        dumpStr = m_MonoBehaviour.Dump();
                    }
                    catch (Exception dumpEx)
                    {
                        dumpStr = $"Failed to dump MonoBehaviour: {dumpEx.Message}";
                    }
                    PreviewMonoBehaviour(assetItem, m_MonoBehaviour, fbxHeader, dumpStr);
                    break;
                case MonoScript m_MonoScript:
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Assembly: {m_MonoScript.m_AssemblyName}");
                        sb.AppendLine($"Namespace: {m_MonoScript.m_Namespace}");
                        sb.AppendLine($"Class: {m_MonoScript.m_ClassName}");
                        SetTextWithTruncation(TextPreviewBox, sb.ToString());
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                    }
                    break;
                case Mesh m_Mesh:
                    PreviewMesh(assetItem, m_Mesh);
                    break;
                case Object obj when obj.type == ClassIDType.PrefabInstance:
                    SetTextWithTruncation(TextPreviewBox, fbxHeader + FormatPrefab(obj));
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    break;
                case VideoClip m_VideoClip:
                    PreviewVideoClip(assetItem, m_VideoClip);
                    break;
                case VideoPlayer m_VideoPlayer:
                    PreviewVideoPlayer(assetItem, m_VideoPlayer);
                    break;
                case MovieTexture _:
                    StatusStripUpdate("Only supported export.");
                    break;
                case Animator m_Animator:
                    PreviewAnimatorGraph(m_Animator);
                    break;
                case AnimatorController m_AnimatorController:
                    PreviewAnimatorGraph(m_AnimatorController);
                    break;
                case AnimatorOverrideController m_AnimatorOverrideController:
                    PreviewAnimatorGraph(m_AnimatorOverrideController);
                    break;
                case Avatar m_Avatar:
                    PreviewAvatar(m_Avatar);
                    break;
                case AnimationClip m_AnimationClip:
                    PreviewAnimationClip(m_AnimationClip);
                    break;
                default:
                    string? rawDump = null;
                    try
                    {
                        rawDump = asset.Dump();
                    }
                    catch (Exception dumpEx)
                    {
                        rawDump = $"Failed to dump asset: {dumpEx.Message}";
                    }
                    if (rawDump != null)
                    {
                        SetTextWithTruncation(TextPreviewBox, fbxHeader + rawDump);
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Error displaying preview for {assetItem.Name}: {ex.Message}");
            StatusStripUpdate($"Preview error: {ex.Message}");

            if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
            if (TextureGLPreview != null) TextureGLPreview.IsVisible = false;
            if (ImagePreviewBox != null)
            {
                ImagePreviewBox.Source = null;
                ImagePreviewBox.IsVisible = false;
            }
            ClearTextAssetPreview();

            var sb = new StringBuilder();
            sb.AppendLine($"Failed to load preview for asset: {assetItem.Name}");
            sb.AppendLine($"Type: {assetItem.DisplayType}");
            sb.AppendLine($"PathID: {assetItem.PathID}");
            sb.AppendLine();
            sb.AppendLine("Error details:");
            sb.AppendLine(ex.ToString());

            SetTextWithTruncation(TextPreviewBox, sb.ToString());
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
        }
    }

    private void PreviewAnimatorGraph(Object asset)
    {
        AnimatorController? controller = null;
        AnimatorOverrideController? overrideController = null;
        string header = "";

        if (asset is Animator animator)
        {
            header = $"ANIMATOR: {((animator.m_GameObject.TryGet(out var go)) ? go.m_Name : "Animator")}\n";
            if (animator.m_Controller.TryGet(out var rac))
            {
                if (rac is AnimatorController ac)
                {
                    controller = ac;
                }
                else if (rac is AnimatorOverrideController aoc)
                {
                    overrideController = aoc;
                }
            }
            else
            {
                var globalController = assetsManager.assetsFileList
                    .SelectMany(x => x.Objects)
                    .FirstOrDefault(x => x.m_PathID == animator.m_Controller.m_PathID && x is RuntimeAnimatorController);
                if (globalController is AnimatorController ac)
                {
                    controller = ac;
                }
                else if (globalController is AnimatorOverrideController aoc)
                {
                    overrideController = aoc;
                }
            }

            if (controller == null && overrideController == null)
            {
                var animName = animator.m_GameObject.TryGet(out var goObj) ? goObj.m_Name : "Animator";
                var matchingController = assetsManager.assetsFileList
                    .SelectMany(x => x.Objects)
                    .OfType<AnimatorController>()
                    .FirstOrDefault(ac => ac.m_Name.Contains(animName, StringComparison.OrdinalIgnoreCase) || 
                                          animName.Contains(ac.m_Name, StringComparison.OrdinalIgnoreCase));
                if (matchingController != null)
                {
                    controller = matchingController;
                }
                else
                {
                    var fallbackSb = new StringBuilder();
                    fallbackSb.AppendLine(header);
                    fallbackSb.AppendLine("=========================================");
                    fallbackSb.AppendLine("ANIMATOR COMPONENT (No Controller Referenced)");
                    fallbackSb.AppendLine("=========================================");
                    fallbackSb.AppendLine();
                    fallbackSb.AppendLine("Properties:");
                    fallbackSb.AppendLine($"  - Enabled: True");
                    fallbackSb.AppendLine($"  - Apply Root Motion: True");
                    fallbackSb.AppendLine($"  - Has Transform Hierarchy: {animator.m_HasTransformHierarchy}");
                    fallbackSb.AppendLine();

                    Avatar? avatar = null;
                    if (animator.m_Avatar.TryGet(out var av))
                    {
                        avatar = av;
                    }
                    else
                    {
                        avatar = assetsManager.assetsFileList
                            .SelectMany(x => x.Objects)
                            .FirstOrDefault(x => x.m_PathID == animator.m_Avatar.m_PathID) as Avatar;
                    }

                    if (avatar != null)
                    {
                        fallbackSb.AppendLine($"Referenced Avatar: {avatar.m_Name} (Size: {avatar.m_AvatarSize} bytes)");
                        fallbackSb.AppendLine();
                        if (avatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
                        {
                            fallbackSb.AppendLine("Avatar Skeleton Nodes:");
                            var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
                            for (int i = 0; i < skeleton.m_Node.Length; i++)
                            {
                                var node = skeleton.m_Node[i];
                                string name = "Unknown";
                                if (skeleton.m_ID != null && i < skeleton.m_ID.Length)
                                {
                                    name = avatar.FindBonePath(skeleton.m_ID[i]);
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        name = $"Hash_{skeleton.m_ID[i]}";
                                    }
                                }
                                fallbackSb.AppendLine($"  [{i}] Node: \"{name}\" (Parent ID: {node.m_ParentId}, Axes ID: {node.m_AxesId})");
                            }
                            fallbackSb.AppendLine();
                        }
                    }
                    else
                    {
                        fallbackSb.AppendLine("Referenced Avatar: None or unresolved.");
                        fallbackSb.AppendLine();
                    }

                    var siblingClips = FindLikelyAnimatorClips(animator, animName, avatar).ToList();

                    if (siblingClips.Count > 0)
                    {
                        AppendGeneratedAnimatorController(fallbackSb, animName, siblingClips);
                    }
                    else
                    {
                        fallbackSb.AppendLine("No matching sibling Animation Clips found in loaded files.");
                        fallbackSb.AppendLine("Generated controller was not created because no likely clips were found.");
                    }

                    SetTextWithTruncation(TextPreviewBox, fallbackSb.ToString());
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    return;
                }
            }
        }
        else if (asset is AnimatorController ac)
        {
            controller = ac;
        }
        else if (asset is AnimatorOverrideController aoc)
        {
            overrideController = aoc;
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
        {
            sb.AppendLine(header);
        }

        if (controller != null)
        {
            sb.AppendLine("=========================================");
            sb.AppendLine($"ANIMATOR CONTROLLER: {controller.m_Name}");
            sb.AppendLine("=========================================");
            sb.AppendLine();

            var m_Controller = controller.m_Controller;
            if (m_Controller == null)
            {
                sb.AppendLine("Animator Controller state machine constant is empty.");
            }
            else
            {
                sb.AppendLine($"Layers count: {m_Controller.m_LayerArray?.Length ?? 0}");
                sb.AppendLine();

                if (m_Controller.m_LayerArray != null)
                {
                    for (int layerIdx = 0; layerIdx < m_Controller.m_LayerArray.Length; layerIdx++)
                    {
                        var layer = m_Controller.m_LayerArray[layerIdx];
                        sb.AppendLine("-----------------------------------------");
                        sb.AppendLine($"Layer {layerIdx}: State Machine Index: {layer.m_StateMachineIndex}");
                        sb.AppendLine("-----------------------------------------");

                        if (m_Controller.m_StateMachineArray != null && layer.m_StateMachineIndex < m_Controller.m_StateMachineArray.Length)
                        {
                            var sm = m_Controller.m_StateMachineArray[layer.m_StateMachineIndex];
                            
                            string defaultStateName = "None";
                            if (sm.m_StateConstantArray != null && sm.m_DefaultState < sm.m_StateConstantArray.Length)
                            {
                                var ds = sm.m_StateConstantArray[sm.m_DefaultState];
                                defaultStateName = GetNameFromTOS(controller.m_TOS, ds.m_NameID);
                            }
                            sb.AppendLine($"Default State: {defaultStateName}");
                            sb.AppendLine();

                            if (sm.m_StateConstantArray == null || sm.m_StateConstantArray.Length == 0)
                            {
                                sb.AppendLine("  (No states found in this layer)");
                            }
                            else
                            {
                                sb.AppendLine("States & Transitions:");
                                var states = sm.m_StateConstantArray!;
                                for (int stateIdx = 0; stateIdx < states.Length; stateIdx++)
                                {
                                    var state = states[stateIdx];
                                    var stateName = GetNameFromTOS(controller.m_TOS, state.m_NameID);
                                    
                                    var clips = new List<string>();
                                    if (state.m_BlendTreeConstantArray != null)
                                    {
                                        foreach (var bt in state.m_BlendTreeConstantArray)
                                        {
                                            if (bt.m_NodeArray != null)
                                            {
                                                foreach (var node in bt.m_NodeArray)
                                                {
                                                    if (node.m_ClipID != 0xFFFFFFFF)
                                                    {
                                                        clips.Add(GetClipName(controller, node.m_ClipID));
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    string clipInfo = clips.Count > 0 ? string.Join(", ", clips) : "None";
                                    bool isDefault = (stateIdx == sm.m_DefaultState);
                                    string prefix = isDefault ? "▶ [DEFAULT] " : "  * ";

                                    sb.AppendLine($"{prefix}{stateName} (Motion: {clipInfo})");

                                    if (state.m_TransitionConstantArray != null && state.m_TransitionConstantArray.Length > 0)
                                    {
                                        for (int transIdx = 0; transIdx < state.m_TransitionConstantArray.Length; transIdx++)
                                        {
                                            var trans = state.m_TransitionConstantArray[transIdx];
                                            string destName = "Unknown";
                                            var statesList = sm.m_StateConstantArray;
                                            var selectorStates = sm.m_SelectorStateConstantArray;
                                            if (statesList != null && trans.m_DestinationState < statesList.Length)
                                            {
                                                var destState = statesList[trans.m_DestinationState];
                                                destName = GetNameFromTOS(controller.m_TOS, destState.m_NameID);
                                            }
                                            else if (selectorStates != null && statesList != null && trans.m_DestinationState >= statesList.Length && (trans.m_DestinationState - statesList.Length) < selectorStates.Length)
                                            {
                                                destName = $"SelectorState_{trans.m_DestinationState - statesList.Length}";
                                            }

                                            string lineChar = (transIdx == state.m_TransitionConstantArray.Length - 1) ? "└──" : "├──";
                                            sb.AppendLine($"    {lineChar} transition ──> {destName}");
                                        }
                                    }
                                    sb.AppendLine();
                                }
                            }
                        }
                        else
                        {
                            sb.AppendLine("  (State machine not found or index out of range)");
                        }
                        sb.AppendLine();
                    }
                }
            }
        }
        else if (overrideController != null)
        {
            sb.AppendLine("=========================================");
            sb.AppendLine($"ANIMATOR OVERRIDE CONTROLLER: {overrideController.m_Name}");
            sb.AppendLine("=========================================");
            sb.AppendLine();

            string baseName = "None";
            if (overrideController.m_Controller.TryGet(out var baseC))
            {
                baseName = baseC.m_Name;
            }
            sb.AppendLine($"Base Controller: {baseName}");
            sb.AppendLine();

            sb.AppendLine("Animation Clip Overrides:");
            if (overrideController.m_Clips == null || overrideController.m_Clips.Length == 0)
            {
                sb.AppendLine("  (No clip overrides defined)");
            }
            else
            {
                foreach (var clipOverride in overrideController.m_Clips)
                {
                    string origName = "None";
                    if (clipOverride.m_OriginalClip.TryGet(out var origClip))
                    {
                        origName = origClip.m_Name;
                    }
                    string overrideName = "None";
                    if (clipOverride.m_OverrideClip.TryGet(out var overClip))
                    {
                        overrideName = overClip.m_Name;
                    }
                    sb.AppendLine($"  * {origName} ──(overridden by)──> {overrideName}");
                }
            }
        }

        SetTextWithTruncation(TextPreviewBox, sb.ToString());
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
    }

    private IEnumerable<AnimationClip> FindLikelyAnimatorClips(Animator animator, string animatorName, Avatar? avatar)
    {
        var keys = BuildAnimatorClipSearchKeys(animatorName, avatar?.m_Name, animator.assetsFile.originalPath);
        var animatorPath = animator.assetsFile.originalPath ?? string.Empty;

        return assetsManager.assetsFileList
            .SelectMany(x => x.Objects)
            .OfType<AnimationClip>()
            .Select(clip => new
            {
                Clip = clip,
                Score = ScoreAnimatorClipMatch(clip, keys, animatorPath)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Clip.m_Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Clip);
    }

    private static List<string> BuildAnimatorClipSearchKeys(string animatorName, string? avatarName, string? originalPath)
    {
        var keys = new List<string>();

        void AddKey(string? raw)
        {
            var key = NormalizeAnimatorSearchKey(raw);
            if (key.Length >= 4 && !keys.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(key);
            }

            var trimmed = StripAnimatorNameSuffixes(key);
            if (trimmed.Length >= 4 && !keys.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(trimmed);
            }
        }

        AddKey(animatorName);
        AddKey(avatarName);
        AddKey(Path.GetFileNameWithoutExtension(originalPath ?? string.Empty));

        foreach (var key in keys.ToArray())
        {
            var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                AddKey(string.Join("_", parts.Take(3)));
            }
        }

        return keys;
    }

    private static int ScoreAnimatorClipMatch(AnimationClip clip, List<string> keys, string animatorPath)
    {
        int score = 0;
        var clipName = NormalizeAnimatorSearchKey(clip.m_Name);
        var clipPath = clip.assetsFile.originalPath ?? string.Empty;

        if (!string.IsNullOrEmpty(animatorPath)
            && !string.IsNullOrEmpty(clipPath)
            && string.Equals(animatorPath, clipPath, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        foreach (var key in keys)
        {
            if (clipName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
            else if (clipName.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase)
                || clipName.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
            else if (clipName.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }
        }

        return score;
    }

    private static string NormalizeAnimatorSearchKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Path.GetFileNameWithoutExtension(value)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Trim()
            .ToLowerInvariant() ?? string.Empty;
    }

    private static string StripAnimatorNameSuffixes(string value)
    {
        var suffixes = new[]
        {
            "_avatar", "avatar", "_skin", "_body", "_mesh", "_model", "_prefab", "_animator", "animator"
        };

        string result = value;
        bool changed;
        do
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && result.Length > suffix.Length)
                {
                    result = result[..^suffix.Length].TrimEnd('_', '-', ' ');
                    changed = true;
                }
            }
        } while (changed);

        return result;
    }

    private static void AppendGeneratedAnimatorController(StringBuilder sb, string animatorName, List<AnimationClip> clips)
    {
        var defaultClip = clips.FirstOrDefault(IsDefaultAnimatorClip) ?? clips.First();

        sb.AppendLine("=========================================");
        sb.AppendLine($"GENERATED ANIMATOR CONTROLLER: {animatorName}");
        sb.AppendLine("=========================================");
        sb.AppendLine("Source: inferred from matching AnimationClip assets.");
        sb.AppendLine("Parameters: Unknown (not present in loaded Animator data).");
        sb.AppendLine("Transitions: Unknown (states are listed without real conditions).");
        sb.AppendLine();
        sb.AppendLine("Layer 0: Base Layer");
        sb.AppendLine($"Default State: {defaultClip.m_Name}");
        sb.AppendLine();
        sb.AppendLine("States:");

        foreach (var clip in clips)
        {
            string prefix = ReferenceEquals(clip, defaultClip) ? "> [DEFAULT] " : "  * ";
            sb.AppendLine($"{prefix}{clip.m_Name} (Motion: {clip.m_Name}, PathID: {clip.m_PathID}, Size: {clip.byteSize} bytes)");
        }

        sb.AppendLine();
        sb.AppendLine("Matching Animation Clips:");
        foreach (var clip in clips)
        {
            var path = string.IsNullOrEmpty(clip.assetsFile.originalPath) ? "[loaded asset]" : clip.assetsFile.originalPath;
            sb.AppendLine($"  * {clip.m_Name} - {path}");
        }
    }

    private static bool IsDefaultAnimatorClip(AnimationClip clip)
    {
        var name = NormalizeAnimatorSearchKey(clip.m_Name);
        return name.Contains("idle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("stand", StringComparison.OrdinalIgnoreCase)
            || name.Contains("wait", StringComparison.OrdinalIgnoreCase)
            || name.Contains("weak", StringComparison.OrdinalIgnoreCase);
    }

    private string GetNameFromTOS(KeyValuePair<uint, string>[]? tos, uint hash)
    {
        if (tos != null)
        {
            foreach (var kv in tos)
            {
                if (kv.Key == hash) return kv.Value;
            }
        }
        return $"Hash_{hash}";
    }

    private string GetClipName(AnimatorController controller, uint clipID)
    {
        if (controller.m_AnimationClips != null && clipID < controller.m_AnimationClips.Length)
        {
            var pptr = controller.m_AnimationClips[clipID];
            if (pptr.TryGet(out var clip))
            {
                return clip.m_Name;
            }
        }
        return $"Clip_{clipID}";
    }

    private void PreviewMesh(
        AssetItem assetItem,
        Mesh m_Mesh,
        bool rebuildCandidateControls = true,
        PreviewCandidateItem? selectedCandidate = null,
        bool preferModelGroup = true)
    {
        if (rebuildCandidateControls)
        {
            var candidates = BuildMeshPreviewCandidates(m_Mesh);
            var activeCandidate = SelectMeshPreviewCandidate(m_Mesh, candidates, selectedCandidate, preferModelGroup);
            BuildPreviewCandidateControls(candidates, activeCandidate, "Model Group");
            if (activeCandidate?.IsModelGroup == true)
            {
                QueuePreviewCandidateSelection(activeCandidate);
                return;
            }
        }

        var meshPreviewId = texturePreviewIdCounter;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate("Preparing mesh preview...");
        if (displayInfo.IsChecked == true && PreviewInfoBorder != null && PreviewInfoOverlay != null)
        {
            PreviewInfoOverlay.Text = "Loading details...";
            PreviewInfoBorder.IsVisible = true;
        }

        var localAssetItem = assetItem;
        var includeMeshInfo = displayInfo.IsChecked == true;
        Task.Run(() =>
        {
            try
            {
                m_Mesh.EnsureProcessed();
                if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
                {
                    throw new Exception("Mesh contains no vertex data. Companion resource file might be missing or failed to decompress.");
                }

                var uvs = BuildMeshPreviewUvs(m_Mesh);
                var quickInfoText = includeMeshInfo
                    ? FormatMeshPreviewSummary(m_Mesh, localAssetItem) + Environment.NewLine + "Loading material details..."
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = m_Mesh;
                        GLPreviewControl.SetMesh(m_Mesh, uvs);
                        GLPreviewControl.IsVisible = true;
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = quickInfoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

                    StatusStripUpdate("OpenGL Preview | Mesh loaded | Loading materials...");
                });

                if (meshPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                var subMeshTextures = new List<byte[]?>();
                var subMeshTexWidths = new List<int>();
                var subMeshTexHeights = new List<int>();
                var allMaterials = FindMaterialsForMeshPreview(m_Mesh);
                if (allMaterials.Count == 0 && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                {
                    EnsureMeshPreviewDependenciesLoaded(m_Mesh);
                    allMaterials = FindMaterialsForMeshPreview(m_Mesh);
                }

                AddPreviewTextureSlotsForMesh(
                    m_Mesh,
                    allMaterials,
                    subMeshTextures,
                    subMeshTexWidths,
                    subMeshTexHeights);

                var infoText = includeMeshInfo
                    ? assetsManager.LazyLoading
                        ? FormatLazyMeshPreview(m_Mesh, localAssetItem, allMaterials)
                        : FormatMeshPreview(m_Mesh, localAssetItem)
                    : string.Empty;
                var hasTextures = subMeshTextures.Any(t => t != null);

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = m_Mesh;
                        GLPreviewControl.ApplyMeshTextures(m_Mesh, subMeshTextures, subMeshTexWidths, subMeshTexHeights);
                        GLPreviewControl.IsVisible = true;
                        BuildMeshMaterialControls(m_Mesh, allMaterials);
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = infoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

                    StatusStripUpdate(hasTextures
                        ? "OpenGL Preview | 'Ctrl W'=Wireframe | 'Ctrl N'=ReNormal | 'Ctrl S'=Textured/Shaded"
                        : "OpenGL Preview | No texture found for this mesh | 'Ctrl W'=Wireframe | 'Ctrl N'=ReNormal");
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Mesh preview failed for {localAssetItem.Name}: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        StatusStripUpdate($"Mesh preview error: {ex.Message}");
                        if (PreviewInfoOverlay != null)
                        {
                            PreviewInfoOverlay.Text = $"Failed to load mesh: {ex.Message}";
                        }
                    }
                });
            }
        });
    }

    private List<PreviewCandidateItem> BuildMeshPreviewCandidates(Mesh mesh)
    {
        var candidates = new List<PreviewCandidateItem>();
        var meshId = GetPreviewObjectKey(mesh);
        var currentLabel = !string.IsNullOrWhiteSpace(mesh.m_Name)
            ? mesh.m_Name
            : GetPreviewHandleName(meshId, "Mesh");
        candidates.Add(new PreviewCandidateItem
        {
            Mesh = mesh,
            MeshId = meshId,
            Label = $"Current mesh: {currentLabel} (Mesh PathID: {mesh.m_PathID})"
        });

        if (string.IsNullOrWhiteSpace(meshId))
        {
            return candidates;
        }

        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenGroupPartSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in LoadModelGroupsForMeshAssetIdForPreview(meshId))
        {
            if (string.IsNullOrWhiteSpace(group.GroupId) || !seenGroups.Add(group.GroupId))
            {
                continue;
            }

            var groupMeshes = LoadModelGroupMeshesForPreview(group.GroupId);
            var meshIds = groupMeshes
                .Select(item => item.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            if (meshIds.Length < 2)
            {
                continue;
            }

            var partSignature = string.Join("\u001f", groupMeshes.Select(part => $"{part.MeshAssetId}:{part.RendererAssetId}:{part.SlotIndex}"));
            if (!seenGroupPartSignatures.Add(partSignature))
            {
                continue;
            }

            var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
            var representativeMeshId = representative?.MeshAssetId ?? meshId;
            var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
                ? group.GroupName
                : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                    ? group.RootGameObjectName
                    : GetPreviewHandleName(representativeMeshId, "Model");
            var representativeName = !string.IsNullOrWhiteSpace(representative?.MeshName)
                ? representative!.MeshName
                : GetPreviewHandleName(representativeMeshId, "Mesh");

            candidates.Add(new PreviewCandidateItem
            {
                Mesh = string.Equals(representativeMeshId, meshId, StringComparison.Ordinal) ? mesh : null,
                MeshId = representativeMeshId,
                ModelGroupId = group.GroupId,
                ModelGroupName = groupName,
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = groupMeshes.ToArray(),
                ModelGroupTransforms = groupMeshes.Select(part => part.TransformMatrix).ToArray(),
                ModelGroupMeshCount = meshIds.Length,
                ModelGroupConfidence = group.Confidence,
                Label = $"Model group: {groupName} ({meshIds.Length:N0} parts) -> {representativeName}"
            });
        }

        return candidates;
    }

    private PreviewCandidateItem? SelectMeshPreviewCandidate(
        Mesh mesh,
        IReadOnlyList<PreviewCandidateItem> candidates,
        PreviewCandidateItem? selectedCandidate,
        bool preferModelGroup)
    {
        if (selectedCandidate != null)
        {
            var selected = candidates.FirstOrDefault(candidate =>
                (!string.IsNullOrWhiteSpace(selectedCandidate.ModelGroupId)
                    && string.Equals(candidate.ModelGroupId, selectedCandidate.ModelGroupId, StringComparison.Ordinal))
                || AreSamePreviewObject(candidate.Mesh, selectedCandidate.Mesh)
                || (!string.IsNullOrWhiteSpace(selectedCandidate.MeshId)
                    && string.Equals(candidate.MeshId, selectedCandidate.MeshId, StringComparison.Ordinal)));
            if (selected != null)
            {
                return selected;
            }
        }

        if (preferModelGroup)
        {
            var group = candidates.FirstOrDefault(candidate => candidate.IsModelGroup && candidate.ModelGroupMeshIds.Count > 1);
            if (group != null)
            {
                return group;
            }
        }

        return candidates.FirstOrDefault(candidate => AreSamePreviewObject(candidate.Mesh, mesh))
            ?? candidates.FirstOrDefault();
    }

    private List<ModelGroupInfo> LoadModelGroupsForMeshAssetIdForPreview(string meshId)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || string.IsNullOrWhiteSpace(meshId))
        {
            return new List<ModelGroupInfo>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<ModelGroupInfo>();
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadModelGroupsForMeshAssetId(folderPath, signature, meshId);
    }

    private List<Mesh> ResolveModelGroupMeshesForPreview(PreviewCandidateItem candidate, CancellationToken token)
    {
        if (candidate.ModelGroupMeshes.Count > 0)
        {
            return candidate.ModelGroupMeshes.ToList();
        }

        var meshIds = candidate.ModelGroupMeshInfos.Count > 0
            ? candidate.ModelGroupMeshInfos
                .Select(mesh => mesh.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray()
            : candidate.ModelGroupMeshIds;
        if (meshIds.Count == 0 && !string.IsNullOrWhiteSpace(candidate.ModelGroupId))
        {
            meshIds = LoadModelGroupMeshesForPreview(candidate.ModelGroupId)
                .Select(mesh => mesh.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
        }

        var result = new List<Mesh>();
        foreach (var meshId in meshIds)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(meshId))
            {
                continue;
            }

            var mesh = ResolveMeshPreviewCandidate(meshId);
            if (mesh != null)
            {
                result.Add(mesh);
            }
        }

        return result;
    }

    private IReadOnlyList<float[]?> ResolveModelGroupTransformsForPreview(PreviewCandidateItem candidate, int expectedCount)
    {
        if (candidate.ModelGroupTransforms.Count > 0)
        {
            return candidate.ModelGroupTransforms;
        }

        if (candidate.ModelGroupMeshInfos.Count > 0)
        {
            return candidate.ModelGroupMeshInfos.Select(mesh => mesh.TransformMatrix).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(candidate.ModelGroupId))
        {
            var groupMeshes = LoadModelGroupMeshesForPreview(candidate.ModelGroupId);
            if (groupMeshes.Count > 0)
            {
                return groupMeshes.Select(mesh => mesh.TransformMatrix).ToArray();
            }
        }

        return expectedCount > 0
            ? Enumerable.Repeat<float[]?>(null, expectedCount).ToArray()
            : Array.Empty<float[]?>();
    }

    private void PreviewModelGroupCandidate(PreviewCandidateItem candidate)
    {
        var meshes = candidate.ModelGroupMeshes.Count > 0
            ? candidate.ModelGroupMeshes.ToList()
            : ResolveModelGroupMeshesForPreview(candidate, CancellationToken.None);
        var transforms = ResolveModelGroupTransformsForPreview(candidate, meshes.Count);
        if (meshes.Count == 0 && candidate.Mesh != null)
        {
            meshes.Add(candidate.Mesh);
            transforms = new float[]?[] { null };
        }

        if (meshes.Count == 0)
        {
            StatusStripUpdate("Model group preview: no mesh parts were resolved from saved connections.");
            return;
        }

        var meshPreviewId = texturePreviewIdCounter;
        var localAssetItem = AssetListDataGrid.SelectedItem as AssetItem;
        var includeMeshInfo = displayInfo.IsChecked == true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Preparing model group preview ({meshes.Count:N0} parts)...");
        if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
        {
            PreviewInfoOverlay.Text = "Loading model group details...";
            PreviewInfoBorder.IsVisible = true;
        }

        Task.Run(() =>
        {
            try
            {
                foreach (var mesh in meshes)
                {
                    mesh.EnsureProcessed();
                }

                var uvs = meshes.Select(BuildMeshPreviewUvs).ToArray();
                var quickInfoText = includeMeshInfo
                    ? FormatModelGroupPreviewSummary(candidate, meshes, localAssetItem, null) + Environment.NewLine + "Loading material details..."
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = candidate.Mesh ?? meshes[0];
                        GLPreviewControl.SetMeshGroup(meshes, uvs, transforms);
                        GLPreviewControl.IsVisible = true;
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = quickInfoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

                    StatusStripUpdate($"OpenGL Preview | Model group loaded ({meshes.Count:N0} parts) | Loading materials...");
                });

                var allMaterials = new List<Material?>();
                var subMeshTextures = new List<byte[]?>();
                var subMeshTexWidths = new List<int>();
                var subMeshTexHeights = new List<int>();
                foreach (var mesh in meshes)
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    var materials = FindMaterialsForMeshPreview(mesh);
                    if (materials.Count == 0 && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                    {
                        EnsureMeshPreviewDependenciesLoaded(mesh);
                        materials = FindMaterialsForMeshPreview(mesh);
                    }

                    var slotCountBefore = subMeshTextures.Count;
                    AddPreviewTextureSlotsForMesh(
                        mesh,
                        materials,
                        subMeshTextures,
                        subMeshTexWidths,
                        subMeshTexHeights);

                    var slotCount = subMeshTextures.Count - slotCountBefore;
                    for (var i = 0; i < slotCount; i++)
                    {
                        allMaterials.Add(i < materials.Count ? materials[i] : null);
                    }
                }

                var hasTextures = subMeshTextures.Any(t => t != null);
                var infoText = includeMeshInfo
                    ? FormatModelGroupPreviewSummary(candidate, meshes, localAssetItem, allMaterials)
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        GLPreviewControl.ApplyMeshTextures(subMeshTextures, subMeshTexWidths, subMeshTexHeights);
                        GLPreviewControl.IsVisible = true;
                        BuildMeshMaterialControlsForSlots(allMaterials);
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = infoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

                    StatusStripUpdate(hasTextures
                        ? $"OpenGL Preview | Model group: {candidate.ModelGroupName} | Parts: {meshes.Count:N0} | Textured"
                        : $"OpenGL Preview | Model group: {candidate.ModelGroupName} | Parts: {meshes.Count:N0} | No textures found");
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Model group preview failed for {candidate.ModelGroupName}: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId == texturePreviewIdCounter)
                    {
                        StatusStripUpdate($"Model group preview error: {ex.Message}");
                    }
                });
            }
        });
    }

    private void AddPreviewTextureSlotsForMesh(
        Mesh mesh,
        IReadOnlyList<Material?> materials,
        List<byte[]?> subMeshTextures,
        List<int> subMeshTexWidths,
        List<int> subMeshTexHeights)
    {
        var slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, materials.Count);
        if (slotCount <= 0 && mesh.m_Indices?.Count > 0)
        {
            slotCount = 1;
        }

        for (int i = 0; i < slotCount; i++)
        {
            byte[]? tb = null;
            int tw = 0, th = 0;

            if (i < materials.Count && materials[i] != null)
            {
                var tex = FindTextureForMaterial(materials[i]!);
                if (tex != null)
                {
                    try
                    {
                        using (var previewImage = LoadTexturePreviewThumbnail(tex, MaxCachedPreviewTextureDimension))
                        {
                            var image = previewImage?.Image;
                            if (image != null)
                            {
                                tw = image.Width;
                                th = image.Height;
                                tb = new byte[tw * th * 4];
                                image.CopyPixelDataTo(tb);
                                for (int p = 0; p < tb.Length; p += 4)
                                {
                                    byte temp = tb[p];
                                    tb[p] = tb[p + 2];
                                    tb[p + 2] = temp;
                                    tb[p + 3] = byte.MaxValue;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            subMeshTextures.Add(tb);
            subMeshTexWidths.Add(tw);
            subMeshTexHeights.Add(th);
        }
    }

    private string FormatModelGroupPreviewSummary(
        PreviewCandidateItem candidate,
        IReadOnlyList<Mesh> meshes,
        AssetItem? item,
        IReadOnlyList<Material?>? materials)
    {
        var sb = new StringBuilder();
        var groupName = !string.IsNullOrWhiteSpace(candidate.ModelGroupName)
            ? candidate.ModelGroupName
            : "Model group";
        sb.AppendLine($"Model Group: {groupName}");
        sb.AppendLine("==================================================");
        if (item != null)
        {
            sb.AppendLine($"Selected Asset: {item.Name} (PathID: {item.PathID})");
        }
        sb.AppendLine($"Group Kind: {(candidate.ModelGroupId.Contains(":sceneobject:", StringComparison.OrdinalIgnoreCase) ? "SceneObject" : "Animator")}");
        sb.AppendLine($"Parts: {meshes.Count}");
        sb.AppendLine($"Confidence: {candidate.ModelGroupConfidence}");

        var vertexTotal = meshes.Sum(mesh => Math.Max(0, mesh.m_VertexCount));
        var indexTotal = meshes.Sum(mesh => mesh.m_Indices?.Count ?? 0);
        sb.AppendLine($"Vertex Count: {vertexTotal:N0}");
        sb.AppendLine($"Index Count: {indexTotal:N0}");

        if (materials != null)
        {
            sb.AppendLine($"Material Slots: {materials.Count}");
            foreach (var material in materials.Where(material => material != null).Take(16))
            {
                sb.AppendLine($"  - {material!.m_Name} (PathID: {material.m_PathID})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Mesh Parts:");
        foreach (var mesh in meshes.Take(24))
        {
            sb.AppendLine($"  - {mesh.m_Name} (PathID: {mesh.m_PathID}, Submeshes: {mesh.m_SubMeshes?.Length ?? 0})");
        }
        if (meshes.Count > 24)
        {
            sb.AppendLine($"  ... {meshes.Count - 24:N0} more");
        }

        return sb.ToString();
    }

    private static string GetPreviewObjectKey(AssetStudio.Object? asset)
    {
        return asset?.assetsFile != null
            ? AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID)
            : asset != null
                ? $"runtime:{asset.GetType().Name}:{asset.m_PathID}"
                : string.Empty;
    }

    private static bool AreSamePreviewObject(AssetStudio.Object? left, AssetStudio.Object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        var leftKey = GetPreviewObjectKey(left);
        var rightKey = GetPreviewObjectKey(right);
        return !string.IsNullOrEmpty(leftKey)
            && !string.IsNullOrEmpty(rightKey)
            && string.Equals(leftKey, rightKey, StringComparison.Ordinal);
    }

    private AssetStudio.Object? ResolvePreviewCandidateAsset(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var handle = assetsManager.ProjectIndex.GetHandle(assetId);
        return handle == null ? null : ResolveSemanticRelationHandleForPreview(handle);
    }

    private Mesh? ResolveMeshPreviewCandidate(string? meshId)
    {
        return ResolvePreviewCandidateAsset(meshId) as Mesh;
    }

    private Avatar? ResolveAvatarPreviewCandidate(string? avatarId)
    {
        return ResolvePreviewCandidateAsset(avatarId) as Avatar;
    }

    private string GetPreviewHandleName(string assetId, string fallbackName)
    {
        var handle = string.IsNullOrWhiteSpace(assetId) ? null : assetsManager.ProjectIndex.GetHandle(assetId);
        return !string.IsNullOrWhiteSpace(handle?.Name) ? handle!.Name : fallbackName;
    }

    private List<string> LoadAvatarMeshAssetIdsForPreview(Avatar avatar)
    {
        if (!assetsManager.LazyLoading || avatar.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var avatarAssetId = AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAvatarMeshAssetIds(folderPath, signature, avatarAssetId);
    }

    private void CacheAvatarMeshRelation(Avatar avatar, Mesh mesh)
    {
        avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
        avatarMeshCache[avatar] = mesh;

        avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
        if (!avatarMeshesCache.TryGetValue(avatar, out var meshes))
        {
            meshes = new List<Mesh>();
            avatarMeshesCache[avatar] = meshes;
        }

        if (!meshes.Any(existing => AreSamePreviewObject(existing, mesh)))
        {
            meshes.Add(mesh);
        }

        meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
        meshAvatarCache[mesh] = avatar;
    }

    private Mesh? ResolveFirstMeshForAvatar(Avatar avatar)
    {
        if (avatarMeshesCache != null
            && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes)
            && cachedMeshes.Count > 0)
        {
            return cachedMeshes[0];
        }

        if (avatarMeshCache != null
            && avatarMeshCache.TryGetValue(avatar, out var cachedMesh)
            && cachedMesh != null)
        {
            return cachedMesh;
        }

        foreach (var meshId in LoadAvatarMeshAssetIdsForPreview(avatar))
        {
            var mesh = ResolveMeshPreviewCandidate(meshId);
            if (mesh == null)
            {
                continue;
            }

            CacheAvatarMeshRelation(avatar, mesh);
            return mesh;
        }

        return assetsManager.LazyLoading ? null : FindMeshesForAvatar(avatar).FirstOrDefault();
    }

    private List<string> LoadAnimationClipAvatarAssetIdsForPreview(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAnimationClipAvatarAssetIds(folderPath, signature, clipAssetId);
    }

    private List<string> LoadAnimationClipMeshAssetIdsForPreview(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAnimationClipMeshAssetIds(folderPath, signature, clipAssetId);
    }

    private Dictionary<string, List<string>> LoadAvatarMeshAssetIdsByAvatarIdsForPreview(IReadOnlyList<string> avatarIds)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || avatarIds.Count == 0)
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAvatarMeshAssetIdsByAvatarIds(folderPath, signature, avatarIds);
    }

    private Dictionary<string, List<ModelGroupInfo>> LoadModelGroupsByAvatarIdsForPreview(IReadOnlyList<string> avatarIds)
    {
        var result = new Dictionary<string, List<ModelGroupInfo>>(StringComparer.Ordinal);
        if (!assetsManager.LazyLoading || currentScanResult == null || avatarIds.Count == 0)
        {
            return result;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return result;
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        foreach (var avatarId in avatarIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var groups = _sqliteCache.LoadModelGroupsForAvatarAssetId(folderPath, signature, avatarId);
            if (groups.Count > 0)
            {
                result[avatarId] = groups;
            }
        }

        return result;
    }

    private List<ModelGroupMeshInfo> LoadModelGroupMeshesForPreview(string groupId)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || string.IsNullOrWhiteSpace(groupId))
        {
            return new List<ModelGroupMeshInfo>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<ModelGroupMeshInfo>();
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadModelGroupMeshes(folderPath, signature, groupId);
    }

    private static ModelGroupMeshInfo? SelectRepresentativeModelGroupMesh(IReadOnlyList<ModelGroupMeshInfo> meshes)
    {
        return meshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.MeshAssetId))
            .OrderByDescending(mesh => mesh.Confidence)
            .ThenByDescending(mesh => mesh.MeshByteSize)
            .ThenBy(mesh => mesh.SlotIndex)
            .FirstOrDefault();
    }

    private List<PreviewCandidateItem> BuildAvatarPreviewCandidates(Avatar avatar, Mesh? preferredMesh = null, string? preferredMeshId = null)
    {
        var candidates = new List<PreviewCandidateItem>();
        var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
        var seenModelGroupIds = new HashSet<string>(StringComparer.Ordinal);
        var avatarId = GetPreviewObjectKey(avatar);

        void AddModelGroup(ModelGroupInfo group, IReadOnlyList<ModelGroupMeshInfo> groupMeshes, string source)
        {
            if (string.IsNullOrWhiteSpace(group.GroupId) || !seenModelGroupIds.Add(group.GroupId))
            {
                return;
            }

            var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
            if (representative == null)
            {
                return;
            }

            var meshIds = groupMeshes
                .Select(mesh => mesh.MeshAssetId)
                .Where(meshId => !string.IsNullOrWhiteSpace(meshId))
                .ToArray();
            var displayName = !string.IsNullOrWhiteSpace(group.GroupName)
                ? group.GroupName
                : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                    ? group.RootGameObjectName
                    : GetPreviewHandleName(representative.MeshAssetId, "Model");
            var representativeName = !string.IsNullOrWhiteSpace(representative.MeshName)
                ? representative.MeshName
                : GetPreviewHandleName(representative.MeshAssetId, "Mesh");
            var pathId = representative.MeshPathId != 0
                ? representative.MeshPathId.ToString(CultureInfo.InvariantCulture)
                : assetsManager.ProjectIndex.GetHandle(representative.MeshAssetId)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?";

            seenMeshIds.Add(representative.MeshAssetId);
            candidates.Add(new PreviewCandidateItem
            {
                Avatar = avatar,
                MeshId = representative.MeshAssetId,
                AvatarId = avatarId,
                ModelGroupId = group.GroupId,
                ModelGroupName = displayName,
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = groupMeshes.ToArray(),
                ModelGroupTransforms = groupMeshes.Select(mesh => mesh.TransformMatrix).ToArray(),
                ModelGroupMeshCount = meshIds.Length,
                ModelGroupConfidence = group.Confidence,
                Label = $"{source}: {displayName} ({meshIds.Length:N0} parts) -> {representativeName} (Mesh PathID: {pathId})"
            });
        }

        void AddMesh(Mesh? mesh, string? meshId, string source, string? name = null)
        {
            meshId = !string.IsNullOrWhiteSpace(meshId) ? meshId : GetPreviewObjectKey(mesh);
            if (string.IsNullOrWhiteSpace(meshId) || !seenMeshIds.Add(meshId))
            {
                return;
            }

            var displayName = !string.IsNullOrWhiteSpace(name)
                ? name!
                : !string.IsNullOrWhiteSpace(mesh?.m_Name)
                    ? mesh!.m_Name
                    : GetPreviewHandleName(meshId, "Mesh");

            candidates.Add(new PreviewCandidateItem
            {
                Avatar = avatar,
                Mesh = mesh,
                MeshId = meshId,
                Label = $"{source}: {displayName} (Mesh PathID: {mesh?.m_PathID.ToString(CultureInfo.InvariantCulture) ?? assetsManager.ProjectIndex.GetHandle(meshId)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?"})"
            });
        }

        AddMesh(preferredMesh, preferredMeshId, "Selected");

        if (!string.IsNullOrWhiteSpace(avatarId))
        {
            foreach (var group in LoadModelGroupsByAvatarIdsForPreview(new[] { avatarId }).GetValueOrDefault(avatarId) ?? new List<ModelGroupInfo>())
            {
                AddModelGroup(group, LoadModelGroupMeshesForPreview(group.GroupId), "Model group");
            }
        }

        if (avatarMeshCache != null && avatarMeshCache.TryGetValue(avatar, out var cachedMesh))
        {
            AddMesh(cachedMesh, null, "Loaded");
        }

        if (avatarMeshesCache != null && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes))
        {
            foreach (var mesh in cachedMeshes)
            {
                AddMesh(mesh, null, "Loaded");
            }
        }

        foreach (var meshId in LoadAvatarMeshAssetIdsForPreview(avatar))
        {
            AddMesh(null, meshId, "Cached relation", GetPreviewHandleName(meshId, "Mesh"));
        }

        if (candidates.Count == 0 && !assetsManager.LazyLoading)
        {
            foreach (var mesh in FindMeshesForAvatar(avatar))
            {
                AddMesh(mesh, null, "Loaded");
            }
        }

        return candidates;
    }

    private Mesh? SelectAvatarPreviewMesh(IReadOnlyList<PreviewCandidateItem> candidates, Mesh? preferredMesh, string? preferredMeshId)
    {
        if (preferredMesh != null)
        {
            return preferredMesh;
        }

        if (!string.IsNullOrWhiteSpace(preferredMeshId))
        {
            return ResolveMeshPreviewCandidate(preferredMeshId);
        }

        var loadedCandidate = candidates.FirstOrDefault(x => x.Mesh != null);
        if (loadedCandidate?.Mesh != null)
        {
            return loadedCandidate.Mesh;
        }

        var idCandidate = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.MeshId));
        if (idCandidate != null)
        {
            var mesh = ResolveMeshPreviewCandidate(idCandidate.MeshId);
            if (mesh != null)
            {
                return mesh;
            }
        }

        return null;
    }

    private void PreviewAvatar(Avatar avatar, Mesh? preferredMesh = null, string? preferredMeshId = null, PreviewCandidateItem? selectedCandidateOverride = null)
    {
        currentPreviewAvatar = avatar;
        var avatarCandidates = BuildAvatarPreviewCandidates(avatar, preferredMesh, preferredMeshId);
        Mesh? avatarMesh = SelectAvatarPreviewMesh(avatarCandidates, preferredMesh, preferredMeshId);
        var selectedMeshId = GetPreviewObjectKey(avatarMesh);
        var selectedCandidate = selectedCandidateOverride == null
            ? null
            : avatarCandidates.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(selectedCandidateOverride.ModelGroupId)
                    && string.Equals(x.ModelGroupId, selectedCandidateOverride.ModelGroupId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(selectedCandidateOverride.MeshId)
                    && string.Equals(x.MeshId, selectedCandidateOverride.MeshId, StringComparison.Ordinal))
                || AreSamePreviewObject(x.Mesh, selectedCandidateOverride.Mesh));
        selectedCandidate ??= avatarCandidates.FirstOrDefault(x =>
            AreSamePreviewObject(x.Mesh, avatarMesh)
            || (!string.IsNullOrWhiteSpace(x.MeshId)
                && !string.IsNullOrWhiteSpace(selectedMeshId)
                && string.Equals(x.MeshId, selectedMeshId, StringComparison.Ordinal)));
        BuildPreviewCandidateControls(avatarCandidates, selectedCandidate, "Avatar Mesh");
        avatarMesh?.EnsureProcessed();
        if (avatarMesh != null)
        {
            CacheAvatarMeshRelation(avatar, avatarMesh);
            EnsureMeshPreviewDependenciesLoaded(avatarMesh);
        }
        currentPreviewMesh = avatarMesh;

        global::OpenTK.Mathematics.Vector3[]? bonePositions = null;
        int[]? parentIndices = null;
        string[]? boneNames = null;

        var skinnedRenderer = avatarMesh != null ? FindSkinnedRendererForAvatarMesh(avatar, avatarMesh) : null;
        if (avatarMesh != null
            && skinnedRenderer != null
            && TryBuildSkeletonFromSkinnedRenderer(avatar, avatarMesh, skinnedRenderer, out var rendererBonePositions, out var rendererParentIndices, out var rendererBoneNames))
        {
            bonePositions = rendererBonePositions;
            parentIndices = rendererParentIndices;
            boneNames = rendererBoneNames;
        }

        if (bonePositions == null && avatarMesh != null && avatarMesh.m_BindPose != null && avatarMesh.m_BindPose.Length > 0
            && avatarMesh.m_BoneNameHashes != null && avatarMesh.m_BoneNameHashes.Length > 0
            && avatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
        {
            int meshBoneCount = avatarMesh.m_BindPose.Length;
            var nodes = avatar.m_Avatar.m_AvatarSkeleton.m_Node;
            var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
            int skelCount = nodes.Length;

            var meshBonePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            for (int i = 0; i < meshBoneCount; i++)
            {
                var bp = avatarMesh.m_BindPose[i];
                var otkMat = new global::OpenTK.Mathematics.Matrix4(
                    bp.M00, bp.M01, bp.M02, bp.M03,
                    bp.M10, bp.M11, bp.M12, bp.M13,
                    bp.M20, bp.M21, bp.M22, bp.M23,
                    bp.M30, bp.M31, bp.M32, bp.M33
                );
                try
                {
                    var inv = otkMat.Inverted();
                    meshBonePositions[i] = inv.ExtractTranslation();
                }
                catch
                {
                    meshBonePositions[i] = global::OpenTK.Mathematics.Vector3.Zero;
                }
            }

            var meshBoneHashToIdx = new Dictionary<uint, int>();
            for (int j = 0; j < avatarMesh.m_BoneNameHashes.Length; j++)
            {
                meshBoneHashToIdx[avatarMesh.m_BoneNameHashes[j]] = j;
            }

            var skelNodeToMeshBone = new int[skelCount];
            for (int i = 0; i < skelCount; i++)
            {
                skelNodeToMeshBone[i] = -1;
                if (skelIds != null && i < skelIds.Length)
                {
                    if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                    {
                        skelNodeToMeshBone[i] = mbIdx;
                    }
                }
            }

            var meshBoneToSkelNode = new int[meshBoneCount];
            for (int i = 0; i < meshBoneCount; i++) meshBoneToSkelNode[i] = -1;
            for (int i = 0; i < skelCount; i++)
            {
                if (skelNodeToMeshBone[i] >= 0)
                {
                    meshBoneToSkelNode[skelNodeToMeshBone[i]] = i;
                }
            }

            var meshBoneNames = new string[meshBoneCount];
            for (int mb = 0; mb < meshBoneCount; mb++)
            {
                int skelIdx = meshBoneToSkelNode[mb];
                if (skelIds != null && skelIdx >= 0 && skelIdx < skelIds.Length)
                {
                    meshBoneNames[mb] = avatar.FindBonePath(skelIds[skelIdx]) ?? string.Empty;
                }
                else
                {
                    meshBoneNames[mb] = string.Empty;
                }
            }

            var meshParentIndices = new int[meshBoneCount];
            for (int mb = 0; mb < meshBoneCount; mb++)
            {
                meshParentIndices[mb] = -1;
                int skelIdx = meshBoneToSkelNode[mb];
                if (skelIdx < 0) continue;

                int current = nodes[skelIdx].m_ParentId;
                while (current >= 0 && current < skelCount)
                {
                    if (skelNodeToMeshBone[current] >= 0)
                    {
                        meshParentIndices[mb] = skelNodeToMeshBone[current];
                        break;
                    }
                    current = nodes[current].m_ParentId;
                }
            }

            bonePositions = meshBonePositions;
            parentIndices = meshParentIndices;
            boneNames = meshBoneNames;
        }

        if (avatarMesh != null && bonePositions != null && parentIndices != null && GLPreviewControl != null)
        {
            GLPreviewControl.SetAvatar(avatarMesh, bonePositions, parentIndices, boneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            if (displayInfo.IsChecked == true && PreviewInfoBorder != null && PreviewInfoOverlay != null)
            {
                PreviewInfoOverlay.Text = BuildAvatarPreviewInfoText(avatar, avatarCandidates, avatarMesh);
                PreviewInfoBorder.IsVisible = true;
            }
            StatusStripUpdate($"OpenGL Avatar Preview | Mesh: {avatarMesh.m_Name} | Related meshes: {avatarCandidates.Count} | Skeleton Joints: {bonePositions.Length}");
            return;
        }

        if (GLPreviewControl != null)
        {
            GLPreviewControl.IsVisible = false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("=========================================");
        sb.AppendLine($"AVATAR: {avatar.m_Name}");
        sb.AppendLine("=========================================");
        sb.AppendLine();
        sb.AppendLine($"Avatar Size: {avatar.m_AvatarSize} bytes");
        sb.AppendLine();
        if (avatarCandidates.Count > 0)
        {
            sb.AppendLine($"Related Meshes ({avatarCandidates.Count}):");
            foreach (var candidate in avatarCandidates.Take(50))
            {
                sb.AppendLine($"  - {candidate.Label}");
            }

            if (avatarCandidates.Count > 50)
            {
                sb.AppendLine($"  ... {avatarCandidates.Count - 50} more");
            }

            sb.AppendLine();
        }

        if (avatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
        {
            sb.AppendLine("Skeleton Nodes Hierarchy:");
            var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
            for (int i = 0; i < skeleton.m_Node.Length; i++)
            {
                var node = skeleton.m_Node[i];
                string name = "Unknown";
                if (skeleton.m_ID != null && i < skeleton.m_ID.Length)
                {
                    name = avatar.FindBonePath(skeleton.m_ID[i]);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = $"Hash_{skeleton.m_ID[i]}";
                    }
                }
                sb.AppendLine($"  [{i}] Node: \"{name}\" (Parent ID: {node.m_ParentId}, Axes ID: {node.m_AxesId})");
            }
        }
        else
        {
            sb.AppendLine("Skeleton nodes are not defined or parsed.");
        }

        SetTextWithTruncation(TextPreviewBox, sb.ToString());
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
    }

    private static string BuildAvatarPreviewInfoText(Avatar avatar, IReadOnlyList<PreviewCandidateItem> relatedMeshes, Mesh? selectedMesh)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Avatar: {avatar.m_Name} (PathID: {avatar.m_PathID})");
        sb.AppendLine(new string('=', 42));
        sb.AppendLine($"Avatar Size: {avatar.m_AvatarSize} bytes");
        if (selectedMesh != null)
        {
            sb.AppendLine($"Preview Mesh: {selectedMesh.m_Name} (PathID: {selectedMesh.m_PathID})");
        }

        sb.AppendLine($"Related Meshes ({relatedMeshes.Count}):");
        foreach (var candidate in relatedMeshes.Take(12))
        {
            sb.AppendLine($"- {candidate.Label}");
        }

        if (relatedMeshes.Count > 12)
        {
            sb.AppendLine($"... {relatedMeshes.Count - 12} more");
        }

        return sb.ToString();
    }

    private SkinnedMeshRenderer? FindSkinnedRendererForAvatarMesh(Avatar? avatar, Mesh mesh)
    {
        if (mesh.assetsFile == null)
        {
            return null;
        }

        var semanticRenderers = TryLoadSkinnedRenderersForMeshFromSemanticCache(mesh);
        if (semanticRenderers.Count > 0)
        {
            return semanticRenderers
                .OrderByDescending(renderer => ScoreSkinnedRendererForMesh(renderer, avatar, mesh))
                .FirstOrDefault();
        }

        return null;
    }

    private List<SkinnedMeshRenderer> TryLoadSkinnedRenderersForMeshFromSemanticCache(Mesh mesh)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return new List<SkinnedMeshRenderer>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<SkinnedMeshRenderer>();
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        if (string.IsNullOrWhiteSpace(meshAssetId))
        {
            return new List<SkinnedMeshRenderer>();
        }

        meshSkinnedRenderersCache ??= new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
        if (meshSkinnedRenderersCache.TryGetValue(meshAssetId, out var cachedRenderers))
        {
            return new List<SkinnedMeshRenderer>(cachedRenderers);
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var rendererIds = _sqliteCache.LoadMeshRendererAssetIds(folderPath, signature, meshAssetId, "SkinnedMeshRenderer");
        var renderers = new List<SkinnedMeshRenderer>();
        var seenRendererIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rendererId in rendererIds)
        {
            if (string.IsNullOrWhiteSpace(rendererId) || !seenRendererIds.Add(rendererId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(rendererId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is SkinnedMeshRenderer renderer)
            {
                renderers.Add(renderer);
            }
        }

        meshSkinnedRenderersCache[meshAssetId] = renderers;
        return new List<SkinnedMeshRenderer>(renderers);
    }

    private static int ScoreSkinnedRendererForMesh(SkinnedMeshRenderer renderer, Avatar? avatar, Mesh mesh)
    {
        var score = 1000;
        if (renderer.m_Bones != null && mesh.m_BindPose != null)
        {
            if (renderer.m_Bones.Length == mesh.m_BindPose.Length)
            {
                score += 150;
            }
            else if (renderer.m_Bones.Length > 0)
            {
                score += 25;
            }
        }

        if (avatar != null && renderer.assetsFile == avatar.assetsFile)
        {
            score += 20;
        }

        return score;
    }

    private static bool TryBuildSkeletonFromSkinnedRenderer(
        Avatar? avatar,
        Mesh mesh,
        SkinnedMeshRenderer renderer,
        out global::OpenTK.Mathematics.Vector3[] bonePositions,
        out int[] parentIndices,
        out string[] boneNames)
    {
        bonePositions = Array.Empty<global::OpenTK.Mathematics.Vector3>();
        parentIndices = Array.Empty<int>();
        boneNames = Array.Empty<string>();

        if (renderer.assetsFile == null
            || renderer.m_Bones == null
            || renderer.m_Bones.Length == 0
            || mesh.m_BindPose == null
            || mesh.m_BindPose.Length == 0)
        {
            return false;
        }

        var boneCount = Math.Min(renderer.m_Bones.Length, mesh.m_BindPose.Length);
        if (boneCount <= 0)
        {
            return false;
        }

        var boneTransforms = new Transform?[boneCount];
        var transformIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < boneCount; i++)
        {
            var transform = ResolveTransformBackground(renderer.assetsFile, renderer.m_Bones[i]);
            boneTransforms[i] = transform;
            var transformId = GetTransformRelationId(renderer.assetsFile, renderer.m_Bones[i], transform);
            if (!string.IsNullOrEmpty(transformId) && !transformIndexById.ContainsKey(transformId))
            {
                transformIndexById[transformId] = i;
            }
        }

        if (transformIndexById.Count == 0)
        {
            return false;
        }

        bonePositions = new global::OpenTK.Mathematics.Vector3[boneCount];
        parentIndices = new int[boneCount];
        boneNames = new string[boneCount];
        Array.Fill(parentIndices, -1);

        for (var i = 0; i < boneCount; i++)
        {
            bonePositions[i] = GetBindPosePosition(mesh.m_BindPose[i]);
            boneNames[i] = GetTransformDisplayName(renderer.assetsFile, boneTransforms[i])
                ?? (mesh.m_BoneNameHashes != null && i < mesh.m_BoneNameHashes.Length
                    ? avatar?.FindBonePath(mesh.m_BoneNameHashes[i]) ?? string.Empty
                    : string.Empty);
        }

        for (var i = 0; i < boneCount; i++)
        {
            var current = boneTransforms[i];
            if (current?.m_Father == null || current.m_Father.IsNull)
            {
                continue;
            }

            var sourceFile = current.assetsFile ?? renderer.assetsFile;
            var parentTransform = ResolveTransformBackground(sourceFile, current.m_Father);
            if (parentTransform == null)
            {
                continue;
            }

            var parentId = GetSemanticAssetId(parentTransform);
            if (!string.IsNullOrEmpty(parentId) && transformIndexById.TryGetValue(parentId, out var parentIndex))
            {
                parentIndices[i] = parentIndex;
            }
        }

        return parentIndices.Any(index => index >= 0);
    }

    private static string GetTransformRelationId(SerializedFile sourceFile, PPtr<Transform> transformPtr, Transform? transform)
    {
        var transformId = GetSemanticAssetId(transform);
        if (!string.IsNullOrEmpty(transformId))
        {
            return transformId;
        }

        return GetSemanticAssetIdFromPPtr(sourceFile, transformPtr, ClassIDType.Transform);
    }

    private static string? GetTransformDisplayName(SerializedFile sourceFile, Transform? transform)
    {
        if (transform == null)
        {
            return null;
        }

        var transformSource = transform.assetsFile ?? sourceFile;
        var gameObject = ResolveGameObjectBackground(transformSource, transform.m_GameObject);
        return string.IsNullOrWhiteSpace(gameObject?.m_Name) ? null : gameObject.m_Name;
    }

    private static global::OpenTK.Mathematics.Vector3 GetBindPosePosition(AssetStudio.Matrix4x4 bindPose)
    {
        var otkMat = new global::OpenTK.Mathematics.Matrix4(
            bindPose.M00, bindPose.M01, bindPose.M02, bindPose.M03,
            bindPose.M10, bindPose.M11, bindPose.M12, bindPose.M13,
            bindPose.M20, bindPose.M21, bindPose.M22, bindPose.M23,
            bindPose.M30, bindPose.M31, bindPose.M32, bindPose.M33);
        try
        {
            return otkMat.Inverted().ExtractTranslation();
        }
        catch
        {
            return global::OpenTK.Mathematics.Vector3.Zero;
        }
    }

    private List<PreviewCandidateItem> BuildAnimationClipPreviewCandidates(
        AnimationClip clip,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId = null,
        string? preferredMeshId = null)
    {
        var candidates = new List<PreviewCandidateItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(
            Avatar? avatar,
            Mesh? mesh,
            string? avatarId,
            string? meshId,
            string source,
            string? avatarName = null,
            string? meshName = null,
            string? modelGroupId = null,
            string? modelGroupName = null,
            IReadOnlyList<string>? modelGroupMeshIds = null,
            IReadOnlyList<ModelGroupMeshInfo>? modelGroupMeshInfos = null,
            int modelGroupConfidence = 0)
        {
            avatarId = !string.IsNullOrWhiteSpace(avatarId) ? avatarId : GetPreviewObjectKey(avatar);
            meshId = !string.IsNullOrWhiteSpace(meshId) ? meshId : GetPreviewObjectKey(mesh);
            if (avatar == null
                && mesh == null
                && string.IsNullOrWhiteSpace(avatarId)
                && string.IsNullOrWhiteSpace(meshId)
                && string.IsNullOrWhiteSpace(modelGroupId))
            {
                return;
            }

            var key = !string.IsNullOrWhiteSpace(modelGroupId)
                ? $"group:{modelGroupId}|{avatarId}|{meshId}"
                : $"{avatarId}|{meshId}";
            if (!seen.Add(key))
            {
                return;
            }

            avatarName = !string.IsNullOrWhiteSpace(avatarName)
                ? avatarName
                : !string.IsNullOrWhiteSpace(avatar?.m_Name)
                    ? avatar!.m_Name
                    : !string.IsNullOrWhiteSpace(avatarId)
                        ? GetPreviewHandleName(avatarId!, "Avatar")
                        : string.Empty;
            meshName = !string.IsNullOrWhiteSpace(meshName)
                ? meshName
                : !string.IsNullOrWhiteSpace(mesh?.m_Name)
                    ? mesh!.m_Name
                    : !string.IsNullOrWhiteSpace(meshId)
                        ? GetPreviewHandleName(meshId!, "Mesh")
                        : string.Empty;

            var modelGroupPartCount = modelGroupMeshIds?.Count ?? 0;
            var label = !string.IsNullOrWhiteSpace(modelGroupId)
                ? $"{source}: {(!string.IsNullOrWhiteSpace(modelGroupName) ? modelGroupName : "Model")} ({modelGroupPartCount:N0} parts)"
                : !string.IsNullOrWhiteSpace(avatarName) && !string.IsNullOrWhiteSpace(meshName)
                ? $"{source}: {avatarName} -> {meshName}"
                : !string.IsNullOrWhiteSpace(avatarName)
                    ? $"{source}: {avatarName}"
                    : $"{source}: {meshName}";

            if (!string.IsNullOrWhiteSpace(modelGroupId) && !string.IsNullOrWhiteSpace(meshName))
            {
                label += $" -> {meshName}";
            }

            if (mesh != null)
            {
                label += $" (Mesh PathID: {mesh.m_PathID})";
            }
            else if (!string.IsNullOrWhiteSpace(meshId))
            {
                label += $" (Mesh PathID: {assetsManager.ProjectIndex.GetHandle(meshId!)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?"})";
            }
            else if (avatar != null)
            {
                label += $" (Avatar PathID: {avatar.m_PathID})";
            }
            else if (!string.IsNullOrWhiteSpace(avatarId))
            {
                label += $" (Avatar PathID: {assetsManager.ProjectIndex.GetHandle(avatarId!)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?"})";
            }

            candidates.Add(new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = avatar,
                Mesh = mesh,
                AvatarId = avatarId ?? string.Empty,
                MeshId = meshId ?? string.Empty,
                ModelGroupId = modelGroupId ?? string.Empty,
                ModelGroupName = modelGroupName ?? string.Empty,
                ModelGroupMeshIds = modelGroupMeshIds ?? Array.Empty<string>(),
                ModelGroupMeshInfos = modelGroupMeshInfos ?? Array.Empty<ModelGroupMeshInfo>(),
                ModelGroupTransforms = modelGroupMeshInfos?.Select(mesh => mesh.TransformMatrix).ToArray() ?? Array.Empty<float[]?>(),
                ModelGroupMeshCount = modelGroupMeshIds?.Count ?? 0,
                ModelGroupConfidence = modelGroupConfidence,
                Label = label
            });
        }

        if (preferredAvatar != null
            || preferredMesh != null
            || !string.IsNullOrWhiteSpace(preferredAvatarId)
            || !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            AddCandidate(preferredAvatar, preferredMesh, preferredAvatarId, preferredMeshId, "Selected");
        }

        if (currentPreviewMesh != null)
        {
            AddCandidate(currentPreviewAvatar, currentPreviewMesh, null, null, currentPreviewAvatar != null ? "Current" : "Current mesh");
        }

        if (currentPreviewAvatar != null)
        {
            var addedCurrentMesh = false;
            if (avatarMeshesCache != null && avatarMeshesCache.TryGetValue(currentPreviewAvatar, out var currentMeshes))
            {
                foreach (var mesh in currentMeshes)
                {
                    AddCandidate(currentPreviewAvatar, mesh, null, null, "Current avatar");
                    addedCurrentMesh = true;
                }
            }

            if (!addedCurrentMesh
                && avatarMeshCache != null
                && avatarMeshCache.TryGetValue(currentPreviewAvatar, out var cachedMesh)
                && cachedMesh != null)
            {
                AddCandidate(currentPreviewAvatar, cachedMesh, null, null, "Current avatar");
                addedCurrentMesh = true;
            }

            if (!addedCurrentMesh)
            {
                AddCandidate(currentPreviewAvatar, null, null, null, "Current avatar");
            }
        }

        if (assetsManager.LazyLoading)
        {
            var avatarIds = LoadAnimationClipAvatarAssetIdsForPreview(clip);
            var avatarMeshIdsByAvatarId = LoadAvatarMeshAssetIdsByAvatarIdsForPreview(avatarIds);
            var modelGroupsByAvatarId = LoadModelGroupsByAvatarIdsForPreview(avatarIds);
            var avatarIdsCoveredByMesh = new HashSet<string>(StringComparer.Ordinal);
            var avatarIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByAvatar = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);

            foreach (var avatarId in avatarIds)
            {
                if (modelGroupsByAvatarId.TryGetValue(avatarId, out var modelGroups) && modelGroups.Count > 0)
                {
                    foreach (var group in modelGroups)
                    {
                        var groupMeshes = LoadModelGroupMeshesForPreview(group.GroupId);
                        var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
                        if (representative == null)
                        {
                            continue;
                        }

                        var meshIds = groupMeshes
                            .Select(mesh => mesh.MeshAssetId)
                            .Where(meshId => !string.IsNullOrWhiteSpace(meshId))
                            .ToArray();
                        foreach (var meshId in meshIds)
                        {
                            meshIdsCoveredByModelGroup.Add(meshId);
                        }

                        avatarIdsCoveredByModelGroup.Add(avatarId);
                        avatarIdsCoveredByMesh.Add(avatarId);
                        meshIdsCoveredByAvatar.Add(representative.MeshAssetId);

                        var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
                            ? group.GroupName
                            : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                                ? group.RootGameObjectName
                                : GetPreviewHandleName(representative.MeshAssetId, "Model");
                        var meshName = !string.IsNullOrWhiteSpace(representative.MeshName)
                            ? representative.MeshName
                            : GetPreviewHandleName(representative.MeshAssetId, "Mesh");

                        AddCandidate(
                            null,
                            null,
                            avatarId,
                            representative.MeshAssetId,
                            "Model group",
                            GetPreviewHandleName(avatarId, "Avatar"),
                            meshName,
                            group.GroupId,
                            groupName,
                            meshIds,
                            groupMeshes,
                            group.Confidence);
                    }

                    if (avatarIdsCoveredByModelGroup.Contains(avatarId))
                    {
                        continue;
                    }
                }

                if (avatarMeshIdsByAvatarId.TryGetValue(avatarId, out var avatarMeshIds) && avatarMeshIds.Count > 0)
                {
                    foreach (var meshId in avatarMeshIds)
                    {
                        if (string.IsNullOrWhiteSpace(meshId))
                        {
                            continue;
                        }

                        meshIdsCoveredByAvatar.Add(meshId);
                        avatarIdsCoveredByMesh.Add(avatarId);
                        AddCandidate(
                            null,
                            null,
                            avatarId,
                            meshId,
                            "Avatar relation",
                            GetPreviewHandleName(avatarId, "Avatar"),
                            GetPreviewHandleName(meshId, "Mesh"));
                    }

                    continue;
                }

                AddCandidate(null, null, avatarId, null, "Avatar relation", GetPreviewHandleName(avatarId, "Avatar"));
            }

            foreach (var meshId in LoadAnimationClipMeshAssetIdsForPreview(clip))
            {
                if (meshIdsCoveredByAvatar.Contains(meshId) || meshIdsCoveredByModelGroup.Contains(meshId))
                {
                    continue;
                }

                AddCandidate(null, null, null, meshId, "Animator mesh", meshName: GetPreviewHandleName(meshId, "Mesh"));
            }

            if (meshIdsCoveredByAvatar.Count > 0)
            {
                candidates.RemoveAll(candidate =>
                    (string.IsNullOrWhiteSpace(candidate.AvatarId)
                        && candidate.Avatar == null
                        && !string.IsNullOrWhiteSpace(candidate.MeshId)
                        && (meshIdsCoveredByAvatar.Contains(candidate.MeshId)
                            || meshIdsCoveredByModelGroup.Contains(candidate.MeshId)))
                    || (!string.IsNullOrWhiteSpace(candidate.AvatarId)
                        && candidate.Mesh == null
                        && string.IsNullOrWhiteSpace(candidate.MeshId)
                        && avatarIdsCoveredByMesh.Contains(candidate.AvatarId))
                    || (!string.IsNullOrWhiteSpace(candidate.AvatarId)
                        && candidate.Mesh == null
                        && !string.IsNullOrWhiteSpace(candidate.MeshId)
                        && !candidate.IsModelGroup
                        && avatarIdsCoveredByModelGroup.Contains(candidate.AvatarId)
                        && meshIdsCoveredByModelGroup.Contains(candidate.MeshId)));
            }
        }
        else
        {
            foreach (var candidateAvatar in FindCandidateAvatarsForAnimationClip(clip))
            {
                var meshes = FindMeshesForAvatar(candidateAvatar);
                if (meshes.Count == 0)
                {
                    AddCandidate(candidateAvatar, null, null, null, "Avatar relation");
                    continue;
                }

                foreach (var mesh in meshes)
                {
                    AddCandidate(candidateAvatar, mesh, null, null, "Avatar relation");
                }
            }
        }

        return candidates;
    }

    private PreviewCandidateItem? SelectAnimationClipPreviewCandidate(
        AnimationClip clip,
        IReadOnlyList<PreviewCandidateItem> candidates,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId = null,
        string? preferredMeshId = null)
    {
        if (preferredAvatar != null
            || preferredMesh != null
            || !string.IsNullOrWhiteSpace(preferredAvatarId)
            || !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            var preferred = candidates.FirstOrDefault(candidate =>
                (preferredAvatar == null || AreSamePreviewObject(candidate.Avatar, preferredAvatar))
                && (preferredMesh == null || AreSamePreviewObject(candidate.Mesh, preferredMesh))
                && (string.IsNullOrWhiteSpace(preferredAvatarId) || string.Equals(candidate.AvatarId, preferredAvatarId, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(preferredMeshId) || string.Equals(candidate.MeshId, preferredMeshId, StringComparison.Ordinal)));
            if (preferred != null)
            {
                return preferred;
            }
        }

        var compatibleAvatar = candidates.FirstOrDefault(candidate =>
            candidate.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null
            && candidate.Mesh != null
            && IsAnimationClipCompatibleWithAvatar(clip, candidate.Avatar));
        if (compatibleAvatar != null)
        {
            return compatibleAvatar;
        }

        if (assetsManager.LazyLoading)
        {
            var lazyModelGroup = candidates.FirstOrDefault(candidate =>
                candidate.IsModelGroup
                && (!string.IsNullOrWhiteSpace(candidate.MeshId) || candidate.Mesh != null));
            if (lazyModelGroup != null)
            {
                return lazyModelGroup;
            }

            var lazyAvatar = candidates.FirstOrDefault(candidate =>
                candidate.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null
                && candidate.Mesh != null);
            if (lazyAvatar != null)
            {
                return lazyAvatar;
            }
        }

        var animatorMesh = candidates.FirstOrDefault(candidate =>
            candidate.Avatar == null
            && candidate.Mesh != null
            && candidate.Label.StartsWith("Animator mesh:", StringComparison.Ordinal));
        if (animatorMesh != null)
        {
            return animatorMesh;
        }

        return candidates.FirstOrDefault(candidate => candidate.Mesh != null)
            ?? candidates.FirstOrDefault(candidate => candidate.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
            ?? candidates.FirstOrDefault();
    }

    private PreviewCandidateItem? ResolveAnimationClipPreviewCandidate(PreviewCandidateItem? candidate, bool cacheRelation = true)
    {
        if (candidate == null)
        {
            return null;
        }

        var avatar = candidate.Avatar;
        var mesh = candidate.Mesh;
        if (avatar == null && !string.IsNullOrWhiteSpace(candidate.AvatarId))
        {
            avatar = ResolveAvatarPreviewCandidate(candidate.AvatarId);
        }

        if (mesh == null && !string.IsNullOrWhiteSpace(candidate.MeshId))
        {
            mesh = ResolveMeshPreviewCandidate(candidate.MeshId);
        }

        if (mesh == null && avatar != null)
        {
            mesh = ResolveFirstMeshForAvatar(avatar);
        }

        if (cacheRelation && avatar != null && mesh != null)
        {
            CacheAvatarMeshRelation(avatar, mesh);
        }

        return new PreviewCandidateItem
        {
            AnimationClip = candidate.AnimationClip,
            Avatar = avatar,
            Mesh = mesh,
            AvatarId = !string.IsNullOrWhiteSpace(candidate.AvatarId) ? candidate.AvatarId : GetPreviewObjectKey(avatar),
            MeshId = !string.IsNullOrWhiteSpace(candidate.MeshId) ? candidate.MeshId : GetPreviewObjectKey(mesh),
            ModelGroupId = candidate.ModelGroupId,
            ModelGroupName = candidate.ModelGroupName,
            ModelGroupMeshIds = candidate.ModelGroupMeshIds,
            ModelGroupMeshInfos = candidate.ModelGroupMeshInfos,
            ModelGroupMeshes = candidate.ModelGroupMeshes,
            ModelGroupTransforms = candidate.ModelGroupTransforms,
            ModelGroupMeshCount = candidate.ModelGroupMeshCount,
            ModelGroupConfidence = candidate.ModelGroupConfidence,
            Label = candidate.Label
        };
    }

    private void Preview2DAnimationClip(AnimationClip clip)
    {
        Stop2DAnimation();
        EnsureAnimationClip2DPreviewDependenciesLoaded(clip);

        var pptrCurve = clip.m_PPtrCurves?.FirstOrDefault(c => c.curve != null && c.curve.Length > 0);
        if (pptrCurve == null)
        {
            StatusStripUpdate("AnimationClip: No keyframes found in 2D animation curves.");
            return;
        }

        var frames = new List<(float time, AssetStudio.Object asset)>();
        foreach (var kf in pptrCurve.curve)
        {
            if (kf.value != null && !kf.value.IsNull)
            {
                if (kf.value.TryGet<Sprite>(out var sprite))
                {
                    frames.Add((kf.time, sprite));
                }
                else if (kf.value.TryGet<Texture2D>(out var texture))
                {
                    frames.Add((kf.time, texture));
                }
            }
        }

        if (frames.Count == 0)
        {
            StatusStripUpdate("AnimationClip: No Sprites or Textures could be loaded for 2D animation.");
            return;
        }

        _2dAnimFrames = frames.OrderBy(f => f.time).ToList();
        _2dAnimDuration = _2dAnimFrames.Last().time;
        if (_2dAnimDuration <= 0)
        {
            _2dAnimDuration = 1.0f;
        }

        _2dAnimBitmaps.Clear();
        foreach (var f in _2dAnimFrames)
        {
            if (!_2dAnimBitmaps.ContainsKey(f.asset))
            {
                try
                {
                    Image<Bgra32>? img = null;
                    if (f.asset is Sprite sprite)
                    {
                        img = sprite.GetImage();
                    }
                    else if (f.asset is Texture2D texture)
                    {
                        img = texture.ConvertToImage(true);
                    }

                    if (img != null)
                    {
                        using (img)
                        using (var ms = new MemoryStream())
                        {
                            img.SaveAsPng(ms);
                            ms.Position = 0;
                            var bmp = new global::Avalonia.Media.Imaging.Bitmap(ms);
                            _2dAnimBitmaps[f.asset] = bmp;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Log(LoggerEvent.Warning, $"Failed to decode 2D animation frame: {ex.Message}");
                }
            }
        }

        if (_2dAnimBitmaps.Count == 0)
        {
            StatusStripUpdate("AnimationClip: Failed to decode any sprite/texture frames for preview.");
            return;
        }

        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
        if (TextureGLPreview != null) TextureGLPreview.IsVisible = false;
        if (TextPreviewBox != null) TextPreviewBox.IsVisible = false;
        if (PreviewLabel != null) PreviewLabel.IsVisible = false;
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.IsVisible = true;
            ImagePreviewBox.Stretch = global::Avalonia.Media.Stretch.Fill;
            ImagePreviewBox.StretchDirection = global::Avalonia.Media.StretchDirection.Both;
        }

        _is2dAnimationPreviewActive = true;
        _2dAnimPaused = false;
        _2dAnimPausedElapsedSeconds = 0;
        _2dAnimCurrentFrameIndex = 0;
        _2dAnimPreviewFill = TwoDAnimationDefaultPreviewFill;
        _2dAnimStartTime = DateTime.UtcNow;
        ShowAnimationPlayback(_2dAnimFrames.Count);
        Render2DAnimationFrame(0);

        _2dAnimTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _2dAnimTimer.Tick += On2DAnimTimerTick;
        _2dAnimTimer.Start();

        StatusStripUpdate($"AnimationClip: Playing 2D animation '{clip.m_Name}' ({_2dAnimFrames.Count} frames)...");
    }

    private void On2DAnimTimerTick(object? sender, EventArgs e)
    {
        if (_2dAnimFrames.Count == 0 || ImagePreviewBox == null)
        {
            Stop2DAnimation();
            return;
        }

        if (_2dAnimPaused)
        {
            return;
        }

        Render2DAnimationFrame(Get2DAnimationLoopTime());
    }

    private float Get2DAnimationLoopTime()
    {
        var elapsedSeconds = _2dAnimPaused
            ? _2dAnimPausedElapsedSeconds
            : (float)(DateTime.UtcNow - _2dAnimStartTime).TotalSeconds;
        return _2dAnimDuration > 0 ? elapsedSeconds % _2dAnimDuration : 0f;
    }

    private void Render2DAnimationFrame(float loopTime)
    {
        if (_2dAnimFrames.Count == 0)
        {
            return;
        }

        var frameIndex = _2dAnimFrames.FindLastIndex(f => f.time <= loopTime);
        if (frameIndex < 0)
        {
            frameIndex = 0;
        }

        var frame = _2dAnimFrames[frameIndex];
        if (_2dAnimBitmaps.TryGetValue(frame.asset, out var bitmap))
        {
            _2dAnimCurrentFrameIndex = frameIndex;
            Set2DAnimationFrame(bitmap);
            UpdateAnimationPlaybackFrame(_2dAnimCurrentFrameIndex, _2dAnimFrames.Count);
        }
    }

    private void Pause2DAnimation()
    {
        if (!_is2dAnimationPreviewActive || _2dAnimPaused)
        {
            return;
        }

        _2dAnimPausedElapsedSeconds = (float)(DateTime.UtcNow - _2dAnimStartTime).TotalSeconds;
        _2dAnimPaused = true;
        _2dAnimTimer?.Stop();
        SetAnimationPlaybackPaused(true);
    }

    private void Play2DAnimation()
    {
        if (!_is2dAnimationPreviewActive || !_2dAnimPaused)
        {
            return;
        }

        _2dAnimStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(_2dAnimPausedElapsedSeconds);
        _2dAnimPaused = false;
        _2dAnimTimer?.Start();
        SetAnimationPlaybackPaused(false);
    }

    private void Restart2DAnimation()
    {
        if (!_is2dAnimationPreviewActive)
        {
            return;
        }

        _2dAnimPaused = false;
        _2dAnimPausedElapsedSeconds = 0;
        _2dAnimStartTime = DateTime.UtcNow;
        Render2DAnimationFrame(0);
        _2dAnimTimer?.Start();
        SetAnimationPlaybackPaused(false);
    }

    private void Set2DAnimationFrame(global::Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (ImagePreviewBox == null)
        {
            return;
        }

        if (!ReferenceEquals(_2dAnimCurrentBitmap, bitmap))
        {
            _2dAnimCurrentBitmap = bitmap;
            ImagePreviewBox.Source = bitmap;
        }

        Update2DAnimationImageLayout();
    }

    private void Update2DAnimationImageLayout()
    {
        if (!_is2dAnimationPreviewActive || ImagePreviewBox == null || _2dAnimCurrentBitmap == null)
        {
            return;
        }

        var hostWidth = PreviewContentHost.Bounds.Width;
        var hostHeight = PreviewContentHost.Bounds.Height;
        var pixelWidth = Math.Max(1, _2dAnimCurrentBitmap.PixelSize.Width);
        var pixelHeight = Math.Max(1, _2dAnimCurrentBitmap.PixelSize.Height);
        if (hostWidth <= 1 || hostHeight <= 1)
        {
            ImagePreviewBox.Width = pixelWidth;
            ImagePreviewBox.Height = pixelHeight;
            return;
        }

        var fitScale = Math.Min(hostWidth / pixelWidth, hostHeight / pixelHeight);
        var fill = Math.Clamp(_2dAnimPreviewFill, TwoDAnimationMinPreviewFill, TwoDAnimationMaxPreviewFill);
        var scale = Math.Max(0.01, fitScale * fill);
        ImagePreviewBox.Width = pixelWidth * scale;
        ImagePreviewBox.Height = pixelHeight * scale;
    }

    private void ImagePreviewBox_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_is2dAnimationPreviewActive)
        {
            return;
        }

        _2dAnimPreviewFill *= e.Delta.Y > 0 ? TwoDAnimationZoomFactor : 1.0 / TwoDAnimationZoomFactor;
        _2dAnimPreviewFill = Math.Clamp(_2dAnimPreviewFill, TwoDAnimationMinPreviewFill, TwoDAnimationMaxPreviewFill);
        Update2DAnimationImageLayout();
        e.Handled = true;
    }

    private void Stop2DAnimation()
    {
        var wasActive = _is2dAnimationPreviewActive;
        _is2dAnimationPreviewActive = false;
        _2dAnimPaused = false;
        _2dAnimPausedElapsedSeconds = 0;
        if (_2dAnimTimer != null)
        {
            _2dAnimTimer.Stop();
            _2dAnimTimer.Tick -= On2DAnimTimerTick;
            _2dAnimTimer = null;
        }
        if (wasActive && ImagePreviewBox != null)
        {
            ImagePreviewBox.Source = null;
        }
        _2dAnimCurrentBitmap = null;
        _2dAnimCurrentFrameIndex = 0;
        _2dAnimFrames.Clear();
        foreach (var bitmap in _2dAnimBitmaps.Values.Distinct())
        {
            bitmap.Dispose();
        }
        _2dAnimBitmaps.Clear();
        _2dAnimPreviewFill = TwoDAnimationDefaultPreviewFill;
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.Width = double.NaN;
            ImagePreviewBox.Height = double.NaN;
            ImagePreviewBox.Stretch = global::Avalonia.Media.Stretch.Uniform;
            ImagePreviewBox.StretchDirection = global::Avalonia.Media.StretchDirection.DownOnly;
        }
    }

    private void PreviewAnimationClip(
        AnimationClip clip,
        Avatar? preferredAvatar = null,
        Mesh? preferredMesh = null,
        string? preferredAvatarId = null,
        string? preferredMeshId = null,
        bool rebuildCandidateControls = true,
        PreviewCandidateItem? selectedCandidate = null)
    {
        if (clip.m_PPtrCurves != null && clip.m_PPtrCurves.Length > 0)
        {
            Preview2DAnimationClip(clip);
            return;
        }

        Stop2DAnimation();
        PreviewCandidateItem? activeCandidate;
        if (rebuildCandidateControls)
        {
            var previewCandidates = BuildAnimationClipPreviewCandidates(clip, preferredAvatar, preferredMesh, preferredAvatarId, preferredMeshId);
            activeCandidate = SelectAnimationClipPreviewCandidate(clip, previewCandidates, preferredAvatar, preferredMesh, preferredAvatarId, preferredMeshId);
            BuildPreviewCandidateControls(previewCandidates, activeCandidate, "Animation Target");
        }
        else if (selectedCandidate != null)
        {
            activeCandidate = selectedCandidate;
        }
        else
        {
            activeCandidate = new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = preferredAvatar,
                Mesh = preferredMesh,
                AvatarId = preferredAvatarId ?? string.Empty,
                MeshId = preferredMeshId ?? string.Empty
            };
        }

        var resolvedCandidate = ResolveAnimationClipPreviewCandidate(activeCandidate);
        Avatar? avatar = resolvedCandidate?.Avatar;
        Mesh? avatarMesh = resolvedCandidate?.Mesh;

        if (avatar == null && avatarMesh != null)
        {
            if (TryPreviewAnimationClipWithRendererSkeletonFallback(clip, avatarMesh, resolvedCandidate?.MeshId))
            {
                return;
            }

            StatusStripUpdate($"AnimationClip: {clip.m_Name} | Mesh target found, but no usable animation tracks matched {avatarMesh.m_Name}.");
            return;
        }

        if (avatar == null)
        {
            if (TryPreviewAnimationClipWithRendererSkeletonFallback(clip, preferredMesh, preferredMeshId))
            {
                return;
            }

            StatusStripUpdate($"AnimationClip: No compatible Avatar or mesh target found for {clip.m_Name}.");
            return;
        }

        if (avatar == null || avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            StatusStripUpdate("AnimationClip: No Avatar found to preview animation.");
            return;
        }

        if (avatarMesh == null)
        {
            avatarMesh = ResolveFirstMeshForAvatar(avatar);
        }
        avatarMesh?.EnsureProcessed();

        if (avatarMesh == null || avatarMesh.m_BindPose == null || avatarMesh.m_BindPose.Length == 0
            || avatarMesh.m_BoneNameHashes == null || avatarMesh.m_BoneNameHashes.Length == 0)
        {
            StatusStripUpdate("AnimationClip: No suitable mesh with bind poses found.");
            return;
        }

        // Step 3: Build the bind-pose skeleton (same as PreviewAvatar)
        int meshBoneCount = avatarMesh.m_BindPose.Length;
        var nodes = avatar.m_Avatar.m_AvatarSkeleton.m_Node;
        var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
        int skelCount = nodes.Length;

        var restBonePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
        var bindPoseInverses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            var bp = avatarMesh.m_BindPose[i];
            var otkMat = new global::OpenTK.Mathematics.Matrix4(
                bp.M00, bp.M01, bp.M02, bp.M03,
                bp.M10, bp.M11, bp.M12, bp.M13,
                bp.M20, bp.M21, bp.M22, bp.M23,
                bp.M30, bp.M31, bp.M32, bp.M33
            );
            try
            {
                bindPoseInverses[i] = otkMat.Inverted();
                restBonePositions[i] = bindPoseInverses[i].ExtractTranslation();
            }
            catch
            {
                bindPoseInverses[i] = global::OpenTK.Mathematics.Matrix4.Identity;
                restBonePositions[i] = global::OpenTK.Mathematics.Vector3.Zero;
            }
        }

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        for (int j = 0; j < avatarMesh.m_BoneNameHashes.Length; j++)
            meshBoneHashToIdx[avatarMesh.m_BoneNameHashes[j]] = j;

        var skelNodeToMeshBone = new int[skelCount];
        for (int i = 0; i < skelCount; i++)
        {
            skelNodeToMeshBone[i] = -1;
            if (skelIds != null && i < skelIds.Length)
                if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                    skelNodeToMeshBone[i] = mbIdx;
        }

        var meshBoneToSkelNode = new int[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++) meshBoneToSkelNode[i] = -1;
        for (int i = 0; i < skelCount; i++)
            if (skelNodeToMeshBone[i] >= 0)
                meshBoneToSkelNode[skelNodeToMeshBone[i]] = i;

        var meshBoneNames = new string[meshBoneCount];
        for (int mb = 0; mb < meshBoneCount; mb++)
        {
            int skelIdx = meshBoneToSkelNode[mb];
            if (skelIds != null && skelIdx >= 0 && skelIdx < skelIds.Length)
            {
                meshBoneNames[mb] = avatar.FindBonePath(skelIds[skelIdx]) ?? string.Empty;
            }
            else
            {
                meshBoneNames[mb] = string.Empty;
            }
        }

        static string NormalizeAnimationPath(string? path)
        {
            return (path ?? string.Empty)
                .Replace("\\", "/", StringComparison.Ordinal)
                .Trim('/');
        }

        var meshBonePathToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void AddBonePathAlias(string? path, int meshBoneIdx)
        {
            path = NormalizeAnimationPath(path);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int start = 0; start < parts.Length; start++)
            {
                var alias = string.Join("/", parts.Skip(start));
                if (!meshBonePathToIdx.TryGetValue(alias, out var existing))
                {
                    meshBonePathToIdx[alias] = meshBoneIdx;
                }
                else if (existing != meshBoneIdx)
                {
                    meshBonePathToIdx[alias] = -1;
                }
            }
        }

        for (int mb = 0; mb < meshBoneCount; mb++)
        {
            AddBonePathAlias(meshBoneNames[mb], mb);
        }

        string GetPathFromHash(uint hash)
        {
            var path = avatar.FindBonePath(hash);
            return string.IsNullOrEmpty(path) ? string.Empty : NormalizeAnimationPath(path);
        }

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            var path = GetPathFromHash(binding.path);
            if (!string.IsNullOrEmpty(path))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int start = 0; start < parts.Length; start++)
                {
                    var alias = string.Join("/", parts.Skip(start));
                    if (meshBonePathToIdx.TryGetValue(alias, out meshBoneIdx) && meshBoneIdx >= 0)
                    {
                        return true;
                    }
                }
            }

            return meshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        static global::OpenTK.Mathematics.Vector3 ToOtkVector3(AssetStudio.Vector3 value)
        {
            return new global::OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
        }

        static global::OpenTK.Mathematics.Quaternion ToOtkQuaternion(AssetStudio.Quaternion value)
        {
            var q = new global::OpenTK.Mathematics.Quaternion(value.X, value.Y, value.Z, value.W);
            if (q.LengthSquared > 0)
            {
                q.Normalize();
            }
            return q;
        }

        static global::OpenTK.Mathematics.Matrix4 CreateLocalMatrix(
            global::OpenTK.Mathematics.Vector3 position,
            global::OpenTK.Mathematics.Quaternion rotation,
            global::OpenTK.Mathematics.Vector3 scale)
        {
            return global::OpenTK.Mathematics.Matrix4.CreateScale(scale)
                * global::OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rotation)
                * global::OpenTK.Mathematics.Matrix4.CreateTranslation(position);
        }

        var meshParentIndices = new int[meshBoneCount];
        for (int mb = 0; mb < meshBoneCount; mb++)
        {
            meshParentIndices[mb] = -1;
            int skelIdx = meshBoneToSkelNode[mb];
            if (skelIdx < 0) continue;
            int current = nodes[skelIdx].m_ParentId;
            while (current >= 0 && current < skelCount)
            {
                if (skelNodeToMeshBone[current] >= 0)
                {
                    meshParentIndices[mb] = skelNodeToMeshBone[current];
                    break;
                }
                current = nodes[current].m_ParentId;
            }
        }

        var weightedBoneMask = new bool[meshBoneCount];
        if (avatarMesh.m_Skin != null)
        {
            foreach (var skin in avatarMesh.m_Skin)
            {
                if (skin?.boneIndex == null || skin.weight == null)
                {
                    continue;
                }

                for (int i = 0; i < Math.Min(skin.boneIndex.Length, skin.weight.Length); i++)
                {
                    var boneIdx = skin.boneIndex[i];
                    if (skin.weight[i] > 0.0001f && boneIdx >= 0 && boneIdx < meshBoneCount)
                    {
                        weightedBoneMask[boneIdx] = true;
                    }
                }
            }
        }

        var hasWeightedBones = weightedBoneMask.Any(x => x);
        var deformChainMask = new bool[meshBoneCount];
        if (hasWeightedBones)
        {
            for (int i = 0; i < meshBoneCount; i++)
            {
                if (!weightedBoneMask[i])
                {
                    continue;
                }

                var current = i;
                while (current >= 0 && current < meshBoneCount && !deformChainMask[current])
                {
                    deformChainMask[current] = true;
                    current = meshParentIndices[current];
                }
            }
        }
        else
        {
            Array.Fill(deformChainMask, true);
        }

        static bool IsFiniteVector(global::OpenTK.Mathematics.Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        static bool IsAuxiliaryAnimationBone(string? path)
        {
            var normalized = NormalizeAnimationPath(path).ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (segment == "ik" || segment.EndsWith("_ik", StringComparison.Ordinal) || segment.StartsWith("ik_", StringComparison.Ordinal)
                    || segment.Contains("effector", StringComparison.Ordinal)
                    || segment.Contains("target", StringComparison.Ordinal)
                    || segment.Contains("pole", StringComparison.Ordinal)
                    || segment.Contains("hint", StringComparison.Ordinal)
                    || segment.Contains("constraint", StringComparison.Ordinal)
                    || segment.Contains("locator", StringComparison.Ordinal)
                    || segment.Contains("dummy", StringComparison.Ordinal)
                    || segment.Contains("helper", StringComparison.Ordinal)
                    || segment.Contains("control", StringComparison.Ordinal)
                    || segment.Contains("ctrl", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        bool ShouldUseAnimationBinding(int meshBoneIdx)
        {
            if (meshBoneIdx < 0 || meshBoneIdx >= meshBoneCount)
            {
                return false;
            }

            if (!deformChainMask[meshBoneIdx])
            {
                return false;
            }

            return weightedBoneMask[meshBoneIdx] || !IsAuxiliaryAnimationBone(meshBoneNames[meshBoneIdx]);
        }

        var muscleClip = clip.m_MuscleClip;
        if (muscleClip?.m_Clip == null)
        {
            StatusStripUpdate("AnimationClip: No muscle clip data.");
            return;
        }

        var posTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        var rotTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>>();
        var scaleTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        float maxTime = 0f;

        void AddKeyframe(int meshBoneIdx, uint attribute, float time, float[] data, int offset)
        {
            if (time > maxTime) maxTime = time;
            if (offset < 0 || offset >= data.Length)
            {
                return;
            }
            if (attribute == 1) // Position
            {
                if (offset + 2 >= data.Length) return;
                if (!posTracks.TryGetValue(meshBoneIdx, out var list)) posTracks[meshBoneIdx] = list = new();
                list.Add((time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2])));
            }
            else if (attribute == 2) // Rotation
            {
                if (offset + 3 >= data.Length) return;
                if (!rotTracks.TryGetValue(meshBoneIdx, out var list)) rotTracks[meshBoneIdx] = list = new();
                var q = new global::OpenTK.Mathematics.Quaternion(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]);
                if (q.LengthSquared > 0)
                {
                    q.Normalize();
                }
                list.Add((time, q));
            }
            else if (attribute == 3) // Scale
            {
                if (offset + 2 >= data.Length) return;
                if (!scaleTracks.TryGetValue(meshBoneIdx, out var list)) scaleTracks[meshBoneIdx] = list = new();
                list.Add((time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2])));
            }
            else if (attribute == 4) // Euler rotation
            {
                if (offset + 2 >= data.Length) return;
                if (!rotTracks.TryGetValue(meshBoneIdx, out var list)) rotTracks[meshBoneIdx] = list = new();
                var euler = new global::OpenTK.Mathematics.Vector3(
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset]),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 1]),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 2]));
                list.Add((time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler)));
            }
        }

        if (muscleClip?.m_Clip != null)
        {
            var m_Clip = muscleClip.m_Clip;
            var bindings = clip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();

            if (bindings?.genericBindings != null)
            {
                int GetBindingCurveWidth(GenericBinding binding)
                {
                    if (binding.typeID != ClassIDType.Transform)
                    {
                        return 1;
                    }

                    return binding.attribute switch
                    {
                        1 => 3,
                        2 => 4,
                        3 => 3,
                        4 => 3,
                        _ => 1
                    };
                }

                bool TryFindBindingInfo(int index, out GenericBinding binding, out int startIndex, out int width)
                {
                    var curves = 0;
                    foreach (var candidate in bindings.genericBindings)
                    {
                        var candidateWidth = GetBindingCurveWidth(candidate);
                        if (index >= curves && index < curves + candidateWidth)
                        {
                            binding = candidate;
                            startIndex = curves;
                            width = candidateWidth;
                            return true;
                        }
                        curves += candidateWidth;
                    }

                    binding = default!;
                    startIndex = -1;
                    width = 1;
                    return false;
                }

                void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
                {
                    if (!TryFindBindingInfo(curveIndexInStream, out var binding, out var bindingStart, out var bindingWidth)
                        || curveIndexInStream != bindingStart)
                    {
                        currentIdxOut++;
                        return;
                    }
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        if (TryResolveBindingBone(binding, out int meshBoneIdx) && ShouldUseAnimationBinding(meshBoneIdx))
                        {
                            if (binding.attribute == 1 || binding.attribute == 3 || binding.attribute == 4)
                            {
                                AddKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                                currentIdxOut += bindingWidth;
                            }
                            else if (binding.attribute == 2)
                            {
                                AddKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                                currentIdxOut += bindingWidth;
                            }
                            else currentIdxOut++;
                        }
                        else
                        {
                            currentIdxOut += bindingWidth;
                        }
                    }
                    else
                    {
                        currentIdxOut++;
                    }
                }

                void ProcessStreamedFrame(float time, AssetStudio.StreamedClip.StreamedCurveKey[] keyList)
                {
                    var valuesByIndex = keyList.ToDictionary(x => x.index, x => x.value);
                    var processedStarts = new HashSet<int>();

                    foreach (var key in keyList.OrderBy(x => x.index))
                    {
                        if (!TryFindBindingInfo(key.index, out var binding, out var bindingStart, out var bindingWidth)
                            || !processedStarts.Add(bindingStart)
                            || binding.typeID != ClassIDType.Transform
                            || !TryResolveBindingBone(binding, out int meshBoneIdx)
                            || !ShouldUseAnimationBinding(meshBoneIdx))
                        {
                            continue;
                        }

                        var values = new float[bindingWidth];
                        var hasAllComponents = true;
                        for (int i = 0; i < bindingWidth; i++)
                        {
                            if (valuesByIndex.TryGetValue(bindingStart + i, out var value))
                            {
                                values[i] = value;
                            }
                            else
                            {
                                hasAllComponents = false;
                                break;
                            }
                        }

                        if (hasAllComponents)
                        {
                            AddKeyframe(meshBoneIdx, binding.attribute, time, values, 0);
                        }
                    }
                }

                if (m_Clip.m_StreamedClip != null)
                {
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        ProcessStreamedFrame(frame.time, frame.keyList);
                    }
                }

                if (m_Clip.m_DenseClip != null)
                {
                    var dense = m_Clip.m_DenseClip;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    for (int frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
                    {
                        var time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                        var frameOffset = frameIndex * dense.m_CurveCount;
                        for (int cIdx = 0; cIdx < dense.m_CurveCount;)
                        {
                            ProcessCurveData((int)(streamCount + cIdx), time, dense.m_SampleArray, (int)frameOffset, ref cIdx);
                        }
                    }
                }

                if (m_Clip.m_ConstantClip != null)
                {
                    var constant = m_Clip.m_ConstantClip;
                    var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    var time2 = 0.0f;
                    for (int i = 0; i < 2; i++)
                    {
                        for (int cIdx = 0; cIdx < constant.data.Length;)
                        {
                            ProcessCurveData((int)(streamCount + denseCount + cIdx), time2, constant.data, 0, ref cIdx);
                        }
                        time2 = muscleClip.m_StopTime;
                    }
                }
            }
        }

        foreach (var track in posTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in rotTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in scaleTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }

        if (posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            // Fallback: show static bind pose with message
            if (GLPreviewControl != null)
            {
                GLPreviewControl.SetAvatar(avatarMesh, restBonePositions, meshParentIndices, meshBoneNames);
                GLPreviewControl.IsVisible = true;
                ShowPreviewGeometryControls(showBoneControls: true);
                GLPreviewControl.Focus();
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = false;
                StatusStripUpdate($"AnimationClip: {clip.m_Name} | No animation tracks extracted, showing bind pose | Bones: {meshBoneCount}");
            }
            return;
        }

        // Interpolation helpers
        global::OpenTK.Mathematics.Vector3 EvaluatePos(int meshBoneIdx, float t)
        {
            if (!posTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (int i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    float factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Quaternion EvaluateRot(int meshBoneIdx, float t)
        {
            if (!rotTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (int i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    float factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Quaternion.Slerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Vector3 EvaluateScale(int meshBoneIdx, float t)
        {
            if (!scaleTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (int i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    float factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            try { bindPoses[i] = bindPoseInverses[i].Inverted(); }
            catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
        }

        var restLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        var defaultPose = avatar.m_Avatar.m_DefaultPose?.m_X ?? avatar.m_Avatar.m_AvatarSkeletonPose?.m_X;
        for (int i = 0; i < meshBoneCount; i++)
        {
            int skelIdx = meshBoneToSkelNode[i];
            if (defaultPose != null && skelIdx >= 0 && skelIdx < defaultPose.Length)
            {
                var xform = defaultPose[skelIdx];
                restLocals[i] = CreateLocalMatrix(
                    ToOtkVector3(xform.t),
                    ToOtkQuaternion(xform.q),
                    ToOtkVector3(xform.s));
            }
            else
            {
                int pIdx = meshParentIndices[i];
                if (pIdx >= 0 && pIdx < meshBoneCount)
                {
                    restLocals[i] = bindPoses[i] * bindPoseInverses[pIdx];
                }
                else
                {
                    restLocals[i] = bindPoses[i];
                }
            }
        }

        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        foreach (var restPosition in restBonePositions)
        {
            if (!IsFiniteVector(restPosition))
            {
                continue;
            }

            restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, restPosition);
            restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, restPosition);
        }

        var restExtent = (restMax - restMin).Length;
        if (!float.IsFinite(restExtent) || restExtent <= 0)
        {
            restExtent = 1f;
        }

        var localPositionLimit = Math.Max(1f, restExtent * 2f);
        foreach (var item in posTracks.ToArray())
        {
            var boneIdx = item.Key;
            if (boneIdx < 0 || boneIdx >= meshBoneCount || meshParentIndices[boneIdx] < 0)
            {
                continue;
            }

            var restLocalPosition = restLocals[boneIdx].ExtractTranslation();
            if (item.Value.Any(x => !IsFiniteVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(boneIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            if (item.Value.Any(x => !IsFiniteVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > 10f || Math.Abs(x.value.Y) > 10f || Math.Abs(x.value.Z) > 10f))
            {
                scaleTracks.Remove(item.Key);
            }
        }

        // Step 5: Compute per-frame bone positions
        float sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        if (maxTime <= 0) maxTime = muscleClip?.m_StopTime > 0 ? muscleClip.m_StopTime : 1f;
        int frameCount = (int)(maxTime * sampleRate);
        if (frameCount == 0) frameCount = 1;

        var allFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        var allBoneMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];
        for (int f = 0; f < frameCount; f++)
        {
            float t = f / sampleRate;
            var framePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            var modelMatrices = new global::OpenTK.Mathematics.Matrix4?[meshBoneCount];
            
            global::OpenTK.Mathematics.Matrix4 GetModelMatrix(int bIdx)
            {
                if (modelMatrices[bIdx] is global::OpenTK.Mathematics.Matrix4 cached) return cached;

                var localMat = restLocals[bIdx];
                bool hasPos = posTracks.ContainsKey(bIdx);
                bool hasRot = rotTracks.ContainsKey(bIdx);
                bool hasScale = scaleTracks.ContainsKey(bIdx);

                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvaluatePos(bIdx, t) : localMat.ExtractTranslation();
                    var rot = hasRot ? EvaluateRot(bIdx, t) : localMat.ExtractRotation();
                    var scale = hasScale ? EvaluateScale(bIdx, t) : localMat.ExtractScale();
                    
                    localMat = CreateLocalMatrix(pos, rot, scale);
                }

                int pIdx = meshParentIndices[bIdx];
                if (pIdx >= 0 && pIdx != bIdx && pIdx < meshBoneCount)
                {
                    var pMat = GetModelMatrix(pIdx);
                    var worldMat = localMat * pMat;
                    modelMatrices[bIdx] = worldMat;
                    return worldMat;
                }
                else
                {
                    modelMatrices[bIdx] = localMat;
                    return localMat;
                }
            }

            for (int meshBoneIdx = 0; meshBoneIdx < meshBoneCount; meshBoneIdx++)
            {
                var mat = GetModelMatrix(meshBoneIdx);
                framePositions[meshBoneIdx] = mat.ExtractTranslation();
                frameMatrices[meshBoneIdx] = mat;
            }

            allFrames[f] = framePositions;
            allBoneMatrices[f] = frameMatrices;
        }

        var renderParentIndices = (int[])meshParentIndices.Clone();
        var hiddenRenderBones = new bool[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            if (!deformChainMask[i] || (!weightedBoneMask[i] && IsAuxiliaryAnimationBone(meshBoneNames[i])))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (int i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx >= 0 && parentIdx < meshBoneCount
                && IsFiniteVector(restBonePositions[i])
                && IsFiniteVector(restBonePositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (restBonePositions[i] - restBonePositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (int i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx < 0 || parentIdx >= meshBoneCount)
            {
                continue;
            }

            foreach (var frame in allFrames)
            {
                if (!IsFiniteVector(frame[i]) || !IsFiniteVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (int f = 0; f < allFrames.Length; f++)
        {
            for (int i = 0; i < meshBoneCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = meshParentIndices[i];
                allFrames[f][i] = parentIdx >= 0 && parentIdx < meshBoneCount
                    ? allFrames[f][parentIdx]
                    : restBonePositions[i];
            }
        }

        // Step 6: Send to GL preview
        if (GLPreviewControl != null)
        {
            GLPreviewControl.SetAnimatedAvatar(avatarMesh, allFrames, allBoneMatrices, renderParentIndices, sampleRate, meshBoneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;

            ShowAnimationPlayback(frameCount);

            var targetStatus = resolvedCandidate?.IsModelGroup == true
                ? $" | Model group: {(!string.IsNullOrWhiteSpace(resolvedCandidate.ModelGroupName) ? resolvedCandidate.ModelGroupName : resolvedCandidate.ModelGroupId)} ({resolvedCandidate.ModelGroupMeshCount:N0} parts) | Representative mesh: {avatarMesh.m_Name}"
                : $" | Mesh: {avatarMesh.m_Name}";
            StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name}{targetStatus} | Frames: {frameCount} | FPS: {sampleRate} | Tracks: {posTracks.Count + rotTracks.Count + scaleTracks.Count}");
        }
    }

    private Mesh? FindBestMeshForAvatar(Avatar avatar)
    {
        return FindMeshesForAvatar(avatar).FirstOrDefault();
    }

    private Avatar? FindBestAvatarForMesh(Mesh mesh)
    {
        if (meshAvatarCache != null && meshAvatarCache.TryGetValue(mesh, out var avatar) && avatar != null)
        {
            return avatar;
        }

        return TryLoadAvatarsForMeshFromSemanticCache(mesh).FirstOrDefault();
    }

    private Mesh? GetCachedMeshForAvatar(Avatar avatar)
    {
        return ResolveFirstMeshForAvatar(avatar);
    }

    private static Dictionary<Avatar, List<Mesh>> BuildAvatarMeshListCache(Dictionary<Avatar, Mesh?>? singleMeshCache)
    {
        var result = new Dictionary<Avatar, List<Mesh>>();
        if (singleMeshCache == null)
        {
            return result;
        }

        foreach (var entry in singleMeshCache)
        {
            result[entry.Key] = entry.Value != null
                ? new List<Mesh> { entry.Value }
                : new List<Mesh>();
        }

        return result;
    }

    private List<Mesh> FindMeshesForAvatar(Avatar avatar)
    {
        if (avatarMeshesCache != null
            && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes)
            && cachedMeshes.Count > 0)
        {
            return new List<Mesh>(cachedMeshes);
        }

        var meshes = new List<Mesh>();
        if (avatarMeshCache != null
            && avatarMeshCache.TryGetValue(avatar, out var cachedMesh)
            && cachedMesh != null)
        {
            meshes.Add(cachedMesh);
        }

        foreach (var mesh in TryLoadMeshesForAvatarFromSemanticCache(avatar))
        {
            if (!meshes.Any(existing => ReferenceEquals(existing, mesh)))
            {
                meshes.Add(mesh);
            }
        }

        if (meshes.Count > 0)
        {
            avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
            avatarMeshesCache[avatar] = meshes;

            avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
            avatarMeshCache[avatar] = meshes[0];

            meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
            foreach (var mesh in meshes)
            {
                meshAvatarCache[mesh] = avatar;
            }
        }

        return meshes;
    }

    private List<Mesh> TryLoadMeshesForAvatarFromSemanticCache(Avatar avatar)
    {
        var meshes = new List<Mesh>();
        if (!assetsManager.LazyLoading || avatar.assetsFile == null || currentScanResult == null)
        {
            return meshes;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return meshes;
        }

        var avatarAssetId = AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var meshIds = _sqliteCache.LoadAvatarMeshAssetIds(folderPath, signature, avatarAssetId);
        var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meshId in meshIds)
        {
            if (string.IsNullOrWhiteSpace(meshId) || !seenMeshIds.Add(meshId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(meshId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Mesh mesh)
            {
                meshes.Add(mesh);
            }
        }

        return meshes;
    }

    private List<Avatar> TryLoadAvatarsForMeshFromSemanticCache(Mesh mesh)
    {
        var avatars = new List<Avatar>();
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return avatars;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return avatars;
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var avatarIds = _sqliteCache.LoadMeshAvatarAssetIds(folderPath, signature, meshAssetId);
        var seenAvatarIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var avatarId in avatarIds)
        {
            if (string.IsNullOrWhiteSpace(avatarId) || !seenAvatarIds.Add(avatarId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(avatarId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Avatar avatar)
            {
                avatars.Add(avatar);
            }
        }

        if (avatars.Count > 0)
        {
            meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
            meshAvatarCache[mesh] = avatars[0];

            avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
            avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
            foreach (var avatar in avatars)
            {
                avatarMeshCache.TryAdd(avatar, mesh);
                if (!avatarMeshesCache.TryGetValue(avatar, out var meshes))
                {
                    meshes = new List<Mesh>();
                    avatarMeshesCache[avatar] = meshes;
                }

                if (!meshes.Any(existing => ReferenceEquals(existing, mesh)))
                {
                    meshes.Add(mesh);
                }
            }
        }

        return avatars;
    }

    private List<Avatar> FindCandidateAvatarsForAnimationClip(AnimationClip clip)
    {
        var avatars = new List<Avatar>();
        var seenAvatarIds = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(Avatar? avatar)
        {
            if (avatar == null)
            {
                return;
            }

            var avatarId = avatar.assetsFile != null
                ? AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID)
                : $"runtime:{avatar.m_PathID}";
            if (seenAvatarIds.Add(avatarId))
            {
                avatars.Add(avatar);
            }
        }

        foreach (var avatar in TryLoadAvatarsForAnimationClipFromSemanticCache(clip))
        {
            AddCandidate(avatar);
        }

        if (animationClipAvatarCache != null && animationClipAvatarCache.TryGetValue(clip, out var cachedAvatar))
        {
            AddCandidate(cachedAvatar);
        }

        return avatars;
    }

    private List<Avatar> TryLoadAvatarsForAnimationClipFromSemanticCache(AnimationClip clip)
    {
        var avatars = new List<Avatar>();
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return avatars;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return avatars;
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var avatarIds = _sqliteCache.LoadAnimationClipAvatarAssetIds(folderPath, signature, clipAssetId);
        var seenAvatarIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var avatarId in avatarIds)
        {
            if (string.IsNullOrWhiteSpace(avatarId) || !seenAvatarIds.Add(avatarId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(avatarId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Avatar avatar)
            {
                avatars.Add(avatar);
            }
        }

        if (avatars.Count > 0)
        {
            animationClipAvatarCache ??= new Dictionary<AnimationClip, Avatar?>();
            animationClipAvatarCache[clip] = avatars[0];
        }

        return avatars;
    }

    private bool TryPreviewAnimationClipWithRendererSkeletonFallback(AnimationClip clip, Mesh? preferredMesh = null, string? preferredMeshId = null)
    {
        var meshes = new List<Mesh>();
        void AddMesh(Mesh? mesh)
        {
            if (mesh != null && !meshes.Any(existing => AreSamePreviewObject(existing, mesh)))
            {
                meshes.Add(mesh);
            }
        }

        AddMesh(preferredMesh);
        if (preferredMesh == null && !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            AddMesh(ResolveMeshPreviewCandidate(preferredMeshId));
        }

        AddMesh(currentPreviewMesh);
        if (meshes.Count == 0)
        {
            var firstMeshId = LoadAnimationClipMeshAssetIdsForPreview(clip).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstMeshId))
            {
                AddMesh(ResolveMeshPreviewCandidate(firstMeshId));
            }
        }

        Mesh? staticMesh = null;
        global::OpenTK.Mathematics.Vector3[]? staticBonePositions = null;
        int[]? staticParentIndices = null;
        string[]? staticBoneNames = null;
        string? lastReason = null;

        foreach (var mesh in meshes)
        {
            mesh.EnsureProcessed();
            EnsureMeshPreviewDependenciesLoaded(mesh);
            var renderer = FindSkinnedRendererForAvatarMesh(null, mesh);
            if (renderer == null)
            {
                continue;
            }

            if (!TryBuildSkeletonFromSkinnedRenderer(
                    null,
                    mesh,
                    renderer,
                    out var bonePositions,
                    out var parentIndices,
                    out var boneNames))
            {
                continue;
            }

            if (GLPreviewControl == null)
            {
                return false;
            }

            if (staticMesh == null)
            {
                staticMesh = mesh;
                staticBonePositions = bonePositions;
                staticParentIndices = parentIndices;
                staticBoneNames = boneNames;
            }

            if (TryBuildAnimationFramesFromRendererSkeleton(
                    clip,
                    mesh,
                    bonePositions,
                    parentIndices,
                    boneNames,
                    out var allFrames,
                    out var allBoneMatrices,
                    out var renderParentIndices,
                    out var sampleRate,
                    out var trackCount,
                    out lastReason))
            {
                currentPreviewMesh = mesh;
                currentPreviewAvatar = null;
                GLPreviewControl.SetAnimatedAvatar(mesh, allFrames, allBoneMatrices, renderParentIndices, sampleRate, boneNames);
                GLPreviewControl.IsVisible = true;
                ShowPreviewGeometryControls(showBoneControls: true);
                GLPreviewControl.Focus();
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = false;

                ShowAnimationPlayback(allFrames.Length);

                StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name} | Mesh target: {mesh.m_Name} | Frames: {allFrames.Length} | FPS: {sampleRate:0.##} | Tracks: {trackCount}");
                return true;
            }
        }

        if (staticMesh != null && staticBonePositions != null && staticParentIndices != null)
        {
            currentPreviewMesh = staticMesh;
            currentPreviewAvatar = null;
            GLPreviewControl!.SetAvatar(staticMesh, staticBonePositions, staticParentIndices, staticBoneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate($"AnimationClip: {clip.m_Name} | No matching animation tracks for mesh {staticMesh.m_Name}; showing renderer skeleton. {lastReason}");
            return true;
        }

        return false;
    }

    private bool TryBuildAnimationFramesFromRendererSkeleton(
        AnimationClip clip,
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out float sampleRate,
        out int trackCount,
        out string reason)
    {
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        trackCount = 0;
        reason = string.Empty;

        if (mesh.m_BindPose == null || mesh.m_BindPose.Length == 0)
        {
            reason = "Mesh has no bind poses.";
            return false;
        }

        var meshBoneCount = Math.Min(mesh.m_BindPose.Length, Math.Min(restBonePositions.Length, meshParentIndices.Length));
        if (meshBoneCount == 0)
        {
            reason = "Renderer skeleton has no bones.";
            return false;
        }

        var bindPoseInverses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var bp = mesh.m_BindPose[i];
            var otkMat = new global::OpenTK.Mathematics.Matrix4(
                bp.M00, bp.M01, bp.M02, bp.M03,
                bp.M10, bp.M11, bp.M12, bp.M13,
                bp.M20, bp.M21, bp.M22, bp.M23,
                bp.M30, bp.M31, bp.M32, bp.M33);
            try
            {
                bindPoseInverses[i] = otkMat.Inverted();
            }
            catch
            {
                bindPoseInverses[i] = global::OpenTK.Mathematics.Matrix4.Identity;
            }
        }

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        if (mesh.m_BoneNameHashes != null)
        {
            for (var i = 0; i < Math.Min(mesh.m_BoneNameHashes.Length, meshBoneCount); i++)
            {
                meshBoneHashToIdx[mesh.m_BoneNameHashes[i]] = i;
            }
        }

        var meshBonePathToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Min(meshBoneNames.Length, meshBoneCount); i++)
        {
            AddPreviewBonePathAlias(meshBonePathToIdx, meshBoneNames[i], i);
        }

        bool TryResolveCurvePath(string? path, out int meshBoneIdx)
        {
            path = NormalizePreviewAnimationPath(path);
            if (!string.IsNullOrEmpty(path))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var start = 0; start < parts.Length; start++)
                {
                    var alias = string.Join("/", parts.Skip(start));
                    if (meshBonePathToIdx.TryGetValue(alias, out meshBoneIdx) && meshBoneIdx >= 0)
                    {
                        return true;
                    }
                }
            }

            meshBoneIdx = -1;
            return false;
        }

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            return meshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        var weightedBoneMask = new bool[meshBoneCount];
        if (mesh.m_Skin != null)
        {
            foreach (var skin in mesh.m_Skin)
            {
                if (skin?.boneIndex == null || skin.weight == null)
                {
                    continue;
                }

                for (var i = 0; i < Math.Min(skin.boneIndex.Length, skin.weight.Length); i++)
                {
                    var boneIdx = skin.boneIndex[i];
                    if (skin.weight[i] > 0.0001f && boneIdx >= 0 && boneIdx < meshBoneCount)
                    {
                        weightedBoneMask[boneIdx] = true;
                    }
                }
            }
        }

        var hasWeightedBones = weightedBoneMask.Any(x => x);
        var deformChainMask = new bool[meshBoneCount];
        if (hasWeightedBones)
        {
            for (var i = 0; i < meshBoneCount; i++)
            {
                if (!weightedBoneMask[i])
                {
                    continue;
                }

                var current = i;
                while (current >= 0 && current < meshBoneCount && !deformChainMask[current])
                {
                    deformChainMask[current] = true;
                    current = meshParentIndices[current];
                }
            }
        }
        else
        {
            Array.Fill(deformChainMask, true);
        }

        bool ShouldUseAnimationBinding(int meshBoneIdx)
        {
            if (meshBoneIdx < 0 || meshBoneIdx >= meshBoneCount)
            {
                return false;
            }

            if (!deformChainMask[meshBoneIdx])
            {
                return false;
            }

            return weightedBoneMask[meshBoneIdx] || !IsAuxiliaryPreviewAnimationBone(meshBoneNames.ElementAtOrDefault(meshBoneIdx));
        }

        var posTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        var rotTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>>();
        var scaleTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        float maxTime = 0f;

        void AddPosition(int boneIdx, float time, global::OpenTK.Mathematics.Vector3 value)
        {
            if (!ShouldUseAnimationBinding(boneIdx))
            {
                return;
            }

            if (time > maxTime) maxTime = time;
            if (!posTracks.TryGetValue(boneIdx, out var list)) posTracks[boneIdx] = list = new();
            list.Add((time, value));
        }

        void AddRotation(int boneIdx, float time, global::OpenTK.Mathematics.Quaternion value)
        {
            if (!ShouldUseAnimationBinding(boneIdx))
            {
                return;
            }

            if (value.LengthSquared > 0)
            {
                value.Normalize();
            }

            if (time > maxTime) maxTime = time;
            if (!rotTracks.TryGetValue(boneIdx, out var list)) rotTracks[boneIdx] = list = new();
            list.Add((time, value));
        }

        void AddScale(int boneIdx, float time, global::OpenTK.Mathematics.Vector3 value)
        {
            if (!ShouldUseAnimationBinding(boneIdx))
            {
                return;
            }

            if (time > maxTime) maxTime = time;
            if (!scaleTracks.TryGetValue(boneIdx, out var list)) scaleTracks[boneIdx] = list = new();
            list.Add((time, value));
        }

        foreach (var curve in clip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                AddPosition(boneIdx, key.time, ToOtkVector3Preview(key.value));
            }
        }

        foreach (var curve in clip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                AddRotation(boneIdx, key.time, ToOtkQuaternionPreview(key.value));
            }
        }

        foreach (var curve in clip.m_EulerCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                var euler = ToOtkVector3Preview(key.value);
                euler = new global::OpenTK.Mathematics.Vector3(
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.X),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.Y),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.Z));
                AddRotation(boneIdx, key.time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler));
            }
        }

        foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                AddScale(boneIdx, key.time, ToOtkVector3Preview(key.value));
            }
        }

        void AddGenericKeyframe(int meshBoneIdx, uint attribute, float time, float[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return;
            }

            switch (attribute)
            {
                case 1 when offset + 2 < data.Length:
                    AddPosition(meshBoneIdx, time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2]));
                    break;
                case 2 when offset + 3 < data.Length:
                    AddRotation(meshBoneIdx, time, new global::OpenTK.Mathematics.Quaternion(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]));
                    break;
                case 3 when offset + 2 < data.Length:
                    AddScale(meshBoneIdx, time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2]));
                    break;
                case 4 when offset + 2 < data.Length:
                    var euler = new global::OpenTK.Mathematics.Vector3(
                        global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset]),
                        global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 1]),
                        global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 2]));
                    AddRotation(meshBoneIdx, time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler));
                    break;
            }
        }

        var muscleClip = clip.m_MuscleClip;
        var innerClip = muscleClip?.m_Clip;
        AnimationClipBindingConstant? bindings = null;
        if (innerClip != null)
        {
            bindings = clip.m_ClipBindingConstant;
            if (bindings == null && innerClip.m_Binding != null)
            {
                bindings = innerClip.ConvertValueArrayToGenericBinding();
            }
        }

        if (innerClip != null && bindings?.genericBindings != null)
        {
            int GetBindingCurveWidth(GenericBinding binding)
            {
                if (binding.typeID != ClassIDType.Transform)
                {
                    return 1;
                }

                return binding.attribute switch
                {
                    1 => 3,
                    2 => 4,
                    3 => 3,
                    4 => 3,
                    _ => 1
                };
            }

            bool TryFindBindingInfo(int index, out GenericBinding binding, out int startIndex, out int width)
            {
                var curves = 0;
                foreach (var candidate in bindings.genericBindings)
                {
                    var candidateWidth = GetBindingCurveWidth(candidate);
                    if (index >= curves && index < curves + candidateWidth)
                    {
                        binding = candidate;
                        startIndex = curves;
                        width = candidateWidth;
                        return true;
                    }

                    curves += candidateWidth;
                }

                binding = default!;
                startIndex = -1;
                width = 1;
                return false;
            }

            void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
            {
                if (!TryFindBindingInfo(curveIndexInStream, out var binding, out var bindingStart, out var bindingWidth)
                    || curveIndexInStream != bindingStart)
                {
                    currentIdxOut++;
                    return;
                }

                if (binding.typeID == ClassIDType.Transform && TryResolveBindingBone(binding, out var meshBoneIdx))
                {
                    AddGenericKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                }

                currentIdxOut += binding.typeID == ClassIDType.Transform ? bindingWidth : 1;
            }

            void ProcessStreamedFrame(float time, AssetStudio.StreamedClip.StreamedCurveKey[] keyList)
            {
                var valuesByIndex = keyList.ToDictionary(x => x.index, x => x.value);
                var processedStarts = new HashSet<int>();

                foreach (var key in keyList.OrderBy(x => x.index))
                {
                    if (!TryFindBindingInfo(key.index, out var binding, out var bindingStart, out var bindingWidth)
                        || !processedStarts.Add(bindingStart)
                        || binding.typeID != ClassIDType.Transform
                        || !TryResolveBindingBone(binding, out var meshBoneIdx))
                    {
                        continue;
                    }

                    var values = new float[bindingWidth];
                    var hasAllComponents = true;
                    for (var i = 0; i < bindingWidth; i++)
                    {
                        if (valuesByIndex.TryGetValue(bindingStart + i, out var value))
                        {
                            values[i] = value;
                        }
                        else
                        {
                            hasAllComponents = false;
                            break;
                        }
                    }

                    if (hasAllComponents)
                    {
                        AddGenericKeyframe(meshBoneIdx, binding.attribute, time, values, 0);
                    }
                }
            }

            if (innerClip.m_StreamedClip != null)
            {
                var streamedFrames = innerClip.m_StreamedClip.ReadData();
                for (var frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                {
                    var frame = streamedFrames[frameIndex];
                    ProcessStreamedFrame(frame.time, frame.keyList);
                }
            }

            if (innerClip.m_DenseClip != null)
            {
                var dense = innerClip.m_DenseClip;
                var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
                for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
                {
                    var time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                    var frameOffset = frameIndex * dense.m_CurveCount;
                    for (var cIdx = 0; cIdx < dense.m_CurveCount;)
                    {
                        var curveIndex = (int)(streamCount + cIdx);
                        ProcessCurveData(curveIndex, time, dense.m_SampleArray, (int)frameOffset, ref cIdx);
                    }
                }
            }

            if (innerClip.m_ConstantClip != null)
            {
                var constant = innerClip.m_ConstantClip;
                var denseCount = innerClip.m_DenseClip?.m_CurveCount ?? 0;
                var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
                var time = 0.0f;
                for (var i = 0; i < 2; i++)
                {
                    for (var cIdx = 0; cIdx < constant.data.Length;)
                    {
                        var curveIndex = (int)(streamCount + denseCount + cIdx);
                        ProcessCurveData(curveIndex, time, constant.data, 0, ref cIdx);
                    }

                    time = muscleClip?.m_StopTime ?? time;
                }
            }
        }

        foreach (var track in posTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in rotTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in scaleTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }

        trackCount = posTracks.Count + rotTracks.Count + scaleTracks.Count;
        if (trackCount == 0)
        {
            reason = bindings?.genericBindings != null
                ? "Clip bindings did not match renderer bone hashes/names."
                : "Clip has no readable transform binding table.";
            return false;
        }

        return BuildAnimationFramesFromTracks(
            mesh,
            restBonePositions,
            meshParentIndices,
            meshBoneNames,
            bindPoseInverses,
            deformChainMask,
            weightedBoneMask,
            posTracks,
            rotTracks,
            scaleTracks,
            sampleRate,
            maxTime > 0f ? maxTime : muscleClip?.m_StopTime ?? 1f,
            out allFrames,
            out allBoneMatrices,
            out renderParentIndices,
            out reason);
    }

    private static string NormalizePreviewAnimationPath(string? path)
    {
        return (path ?? string.Empty)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Trim('/');
    }

    private static void AddPreviewBonePathAlias(Dictionary<string, int> bonePathToIndex, string? path, int meshBoneIdx)
    {
        path = NormalizePreviewAnimationPath(path);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start < parts.Length; start++)
        {
            var alias = string.Join("/", parts.Skip(start));
            if (!bonePathToIndex.TryGetValue(alias, out var existing))
            {
                bonePathToIndex[alias] = meshBoneIdx;
            }
            else if (existing != meshBoneIdx)
            {
                bonePathToIndex[alias] = -1;
            }
        }
    }

    private static global::OpenTK.Mathematics.Vector3 ToOtkVector3Preview(AssetStudio.Vector3 value)
    {
        return new global::OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
    }

    private static global::OpenTK.Mathematics.Quaternion ToOtkQuaternionPreview(AssetStudio.Quaternion value)
    {
        var q = new global::OpenTK.Mathematics.Quaternion(value.X, value.Y, value.Z, value.W);
        if (q.LengthSquared > 0)
        {
            q.Normalize();
        }
        return q;
    }

    private static global::OpenTK.Mathematics.Matrix4 CreatePreviewLocalMatrix(
        global::OpenTK.Mathematics.Vector3 position,
        global::OpenTK.Mathematics.Quaternion rotation,
        global::OpenTK.Mathematics.Vector3 scale)
    {
        return global::OpenTK.Mathematics.Matrix4.CreateScale(scale)
            * global::OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rotation)
            * global::OpenTK.Mathematics.Matrix4.CreateTranslation(position);
    }

    private static bool IsFinitePreviewVector(global::OpenTK.Mathematics.Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsAuxiliaryPreviewAnimationBone(string? path)
    {
        var normalized = NormalizePreviewAnimationPath(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "ik" || segment.EndsWith("_ik", StringComparison.Ordinal) || segment.StartsWith("ik_", StringComparison.Ordinal)
                || segment.Contains("effector", StringComparison.Ordinal)
                || segment.Contains("target", StringComparison.Ordinal)
                || segment.Contains("pole", StringComparison.Ordinal)
                || segment.Contains("hint", StringComparison.Ordinal)
                || segment.Contains("constraint", StringComparison.Ordinal)
                || segment.Contains("locator", StringComparison.Ordinal)
                || segment.Contains("dummy", StringComparison.Ordinal)
                || segment.Contains("helper", StringComparison.Ordinal)
                || segment.Contains("control", StringComparison.Ordinal)
                || segment.Contains("ctrl", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BuildAnimationFramesFromTracks(
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        global::OpenTK.Mathematics.Matrix4[] bindPoseInverses,
        bool[] deformChainMask,
        bool[] weightedBoneMask,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> posTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> rotTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> scaleTracks,
        float sampleRate,
        float maxTime,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out string reason)
    {
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        reason = string.Empty;

        var meshBoneCount = Math.Min(mesh.m_BindPose?.Length ?? 0, Math.Min(restBonePositions.Length, meshParentIndices.Length));
        if (meshBoneCount == 0)
        {
            reason = "No bones available for frame generation.";
            return false;
        }

        global::OpenTK.Mathematics.Vector3 EvaluatePos(int meshBoneIdx, float t)
        {
            if (!posTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Quaternion EvaluateRot(int meshBoneIdx, float t)
        {
            if (!rotTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Quaternion.Slerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Vector3 EvaluateScale(int meshBoneIdx, float t)
        {
            if (!scaleTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            try { bindPoses[i] = bindPoseInverses[i].Inverted(); }
            catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
        }

        var restLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var pIdx = meshParentIndices[i];
            restLocals[i] = pIdx >= 0 && pIdx < meshBoneCount
                ? bindPoses[i] * bindPoseInverses[pIdx]
                : bindPoses[i];
        }

        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var restPosition = restBonePositions[i];
            if (!IsFinitePreviewVector(restPosition))
            {
                continue;
            }

            restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, restPosition);
            restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, restPosition);
        }

        var restExtent = (restMax - restMin).Length;
        if (!float.IsFinite(restExtent) || restExtent <= 0)
        {
            restExtent = 1f;
        }

        var localPositionLimit = Math.Max(1f, restExtent * 2f);
        foreach (var item in posTracks.ToArray())
        {
            var boneIdx = item.Key;
            if (boneIdx < 0 || boneIdx >= meshBoneCount || meshParentIndices[boneIdx] < 0)
            {
                continue;
            }

            var restLocalPosition = restLocals[boneIdx].ExtractTranslation();
            if (item.Value.Any(x => !IsFinitePreviewVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(boneIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            if (item.Value.Any(x => !IsFinitePreviewVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > 10f || Math.Abs(x.value.Y) > 10f || Math.Abs(x.value.Z) > 10f))
            {
                scaleTracks.Remove(item.Key);
            }
        }

        if (posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            reason = "All extracted tracks were rejected by safety filters.";
            return false;
        }

        if (sampleRate <= 0f || !float.IsFinite(sampleRate))
        {
            sampleRate = 30f;
        }
        if (maxTime <= 0f || !float.IsFinite(maxTime))
        {
            maxTime = 1f;
        }

        var frameCount = Math.Max(1, (int)Math.Ceiling(maxTime * sampleRate));
        allFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        allBoneMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];
        for (var f = 0; f < frameCount; f++)
        {
            var t = f / sampleRate;
            var framePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            var modelMatrices = new global::OpenTK.Mathematics.Matrix4?[meshBoneCount];

            global::OpenTK.Mathematics.Matrix4 GetModelMatrix(int bIdx)
            {
                if (modelMatrices[bIdx] is global::OpenTK.Mathematics.Matrix4 cached)
                {
                    return cached;
                }

                var localMat = restLocals[bIdx];
                var hasPos = posTracks.ContainsKey(bIdx);
                var hasRot = rotTracks.ContainsKey(bIdx);
                var hasScale = scaleTracks.ContainsKey(bIdx);

                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvaluatePos(bIdx, t) : localMat.ExtractTranslation();
                    var rot = hasRot ? EvaluateRot(bIdx, t) : localMat.ExtractRotation();
                    var scale = hasScale ? EvaluateScale(bIdx, t) : localMat.ExtractScale();
                    localMat = CreatePreviewLocalMatrix(pos, rot, scale);
                }

                var pIdx = meshParentIndices[bIdx];
                if (pIdx >= 0 && pIdx != bIdx && pIdx < meshBoneCount)
                {
                    var worldMat = localMat * GetModelMatrix(pIdx);
                    modelMatrices[bIdx] = worldMat;
                    return worldMat;
                }

                modelMatrices[bIdx] = localMat;
                return localMat;
            }

            for (var meshBoneIdx = 0; meshBoneIdx < meshBoneCount; meshBoneIdx++)
            {
                var mat = GetModelMatrix(meshBoneIdx);
                framePositions[meshBoneIdx] = mat.ExtractTranslation();
                frameMatrices[meshBoneIdx] = mat;
            }

            allFrames[f] = framePositions;
            allBoneMatrices[f] = frameMatrices;
        }

        renderParentIndices = meshParentIndices.Take(meshBoneCount).ToArray();
        var hiddenRenderBones = new bool[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var boneName = i < meshBoneNames.Length ? meshBoneNames[i] : string.Empty;
            if (!deformChainMask[i] || (!weightedBoneMask[i] && IsAuxiliaryPreviewAnimationBone(boneName)))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx >= 0 && parentIdx < meshBoneCount
                && IsFinitePreviewVector(restBonePositions[i])
                && IsFinitePreviewVector(restBonePositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (restBonePositions[i] - restBonePositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx < 0 || parentIdx >= meshBoneCount)
            {
                continue;
            }

            foreach (var frame in allFrames)
            {
                if (!IsFinitePreviewVector(frame[i]) || !IsFinitePreviewVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (var f = 0; f < allFrames.Length; f++)
        {
            for (var i = 0; i < meshBoneCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = meshParentIndices[i];
                allFrames[f][i] = parentIdx >= 0 && parentIdx < meshBoneCount
                    ? allFrames[f][parentIdx]
                    : restBonePositions[i];
            }
        }

        return true;
    }

    private List<Mesh> TryLoadMeshesForAnimationClipFromSemanticCache(AnimationClip clip)
    {
        var meshes = new List<Mesh>();
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return meshes;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return meshes;
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var meshIds = _sqliteCache.LoadAnimationClipMeshAssetIds(folderPath, signature, clipAssetId);
        var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meshId in meshIds)
        {
            if (string.IsNullOrWhiteSpace(meshId) || !seenMeshIds.Add(meshId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(meshId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Mesh mesh)
            {
                meshes.Add(mesh);
            }
        }

        return meshes;
    }

    private bool IsAnimationClipCompatibleWithAvatar(AnimationClip clip, Avatar avatar)
    {
        if (animationClipAvatarCache != null && animationClipAvatarCache.TryGetValue(clip, out var cachedAvatar))
        {
            if (ReferenceEquals(cachedAvatar, avatar))
            {
                return true;
            }
        }

        animationClipTransformBindingsCache ??= new Dictionary<AnimationClip, HashSet<uint>>();
        if (!animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths))
        {
            bindingPaths = GetTransformBindingPathsBackground(clip);
            animationClipTransformBindingsCache[clip] = bindingPaths;
        }

        if (bindingPaths.Count == 0 || avatar.m_TOS == null)
        {
            return false;
        }

        var avatarPathHashes = new HashSet<uint>(avatar.m_TOS.Select(x => x.Key));
        var overlap = bindingPaths.Count(avatarPathHashes.Contains);
        return IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap);
    }

    private void PreviewTexture2D(AssetItem assetItem, Texture2D m_Texture2D)
    {
        currentPreviewTexture = m_Texture2D;
        UpdateImagePreview();
    }

    private void PreviewSprite(AssetItem assetItem, Sprite m_Sprite)
    {
        EnsureSpritePreviewDependenciesLoaded(m_Sprite);
        currentPreviewSprite = m_Sprite;
        UpdateImagePreview();
    }

    private void PreviewTextAsset(AssetItem assetItem, TextAsset m_TextAsset, string fbxHeader)
    {
        var data = m_TextAsset.m_Script ?? Array.Empty<byte>();
        var preview = TextAssetPreviewBuilder.BuildPreview(assetItem, data, fbxHeader);
        if (preview.HasDialogueCards)
        {
            ShowTextAssetDialoguePreview(assetItem, preview);
            StatusStripUpdate($"TextAsset localized preview loaded ({preview.DialogueCards.Count:N0} dialogue-like strings, {data.Length:N0} bytes).");
            return;
        }

        TextPreviewBox.FontFamily = new global::Avalonia.Media.FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
        TextPreviewBox.FontSize = 13;
        SetTextWithTruncation(TextPreviewBox, preview.DetailsText);
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        PreviewInfoBorder.IsVisible = false;

        StatusStripUpdate($"TextAsset preview loaded ({data.Length:N0} bytes).");
    }

    private void PreviewFont(AssetItem assetItem, AssetStudio.Font m_Font)
    {
        if (m_Font.m_FontData == null || m_Font.m_FontData.Length == 0)
        {
            StatusStripUpdate("Font has no embedded binary data.");
            var sb = new StringBuilder();
            sb.AppendLine($"Font: {m_Font.m_Name}");
            sb.AppendLine("Format: System or Custom Reference (No embedded data)");
            sb.AppendLine("Data size: 0 bytes");
            sb.AppendLine();
            sb.AppendLine("This font asset does not contain embedded TrueType/OpenType binary data.");
            sb.AppendLine("It may reference a system-installed font or use custom character textures.");
            sb.AppendLine();
            sb.AppendLine("Raw metadata export is still available.");

            SetTextWithTruncation(TextPreviewBox, sb.ToString());
            ImagePreviewBox.IsVisible = false;
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
            if (PreviewInfoBorder != null)
            {
                PreviewInfoBorder.IsVisible = false;
            }
            return;
        }

        long currentId = ++texturePreviewIdCounter;
        StatusStripUpdate("Rendering font preview...");

        Task.Run(() =>
        {
            try
            {
                var fontPreview = FontAssetPreviewRenderer.Render(m_Font.m_Name, m_Font.m_FontData, () => currentId != texturePreviewIdCounter);

                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter)
                    {
                        ImagePreviewBox.Source = fontPreview.Bitmap;
                        ImagePreviewBox.IsVisible = true;
                        TextPreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = false;
                        if (displayInfo.IsChecked == true && PreviewInfoOverlay != null && PreviewInfoBorder != null)
                        {
                            PreviewInfoOverlay.Text = fontPreview.InfoText;
                            PreviewInfoBorder.IsVisible = true;
                        }
                        else if (PreviewInfoBorder != null)
                        {
                            PreviewInfoBorder.IsVisible = false;
                        }
                        StatusStripUpdate($"Font loaded: {m_Font.m_Name}");
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter)
                    {
                        SetTextWithTruncation(TextPreviewBox, FontAssetPreviewRenderer.BuildFallbackText(m_Font, ex.Message));
                        ImagePreviewBox.IsVisible = false;
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                        StatusStripUpdate($"Unsupported font preview: {ex.Message}");
                    }
                });
            }
        });
    }

    private static void MakeAlphaOnlyTextureVisible(Image<Bgra32> image)
    {
        long rgbSignal = 0;
        long alphaSignal = 0;
        int samples = 0;
        byte minAlpha = byte.MaxValue;
        byte maxAlpha = byte.MinValue;
        int stepX = Math.Max(1, image.Width / 128);
        int stepY = Math.Max(1, image.Height / 128);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y += stepY)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x += stepX)
                {
                    var pixel = row[x];
                    rgbSignal += pixel.R + pixel.G + pixel.B;
                    alphaSignal += pixel.A;
                    minAlpha = Math.Min(minAlpha, pixel.A);
                    maxAlpha = Math.Max(maxAlpha, pixel.A);
                    samples++;
                }
            }
        });

        if (samples == 0
            || rgbSignal / (samples * 3) >= 8
            || alphaSignal / samples <= 8
            || maxAlpha - minAlpha <= 16)
        {
            return;
        }

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    ref Bgra32 pixel = ref row[x];
                    byte value = pixel.A;
                    pixel.R = value;
                    pixel.G = value;
                    pixel.B = value;
                    pixel.A = byte.MaxValue;
                }
            }
        });
    }

    private static bool LimitInlinePreviewImage(Image<Bgra32> image)
    {
        return LimitPreviewImage(image, MaxInlinePreviewTextureDimension);
    }

    private static bool LimitPreviewImage(Image<Bgra32> image, int maxDimension)
    {
        var maxSide = Math.Max(image.Width, image.Height);
        if (maxSide <= maxDimension)
        {
            return false;
        }

        var scale = maxDimension / (float)maxSide;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(width, height),
            Mode = ResizeMode.Max
        }));
        return true;
    }

    private Material? ResolveMaterialForPreview(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        return ResolveMaterialForPreviewBackground(material, materialPreviewMaterialCache);
    }

    private Material? ResolveMaterialForPreviewUncached(Material material)
    {
        return ResolveMaterialForPreviewUncachedBackground(material);
    }

    private static readonly string[] PreferredMaterialTextureSlots = MaterialPreviewBuilder.PreferredTextureSlots;

    private Material? FindMaterialForMesh(Mesh mesh)
    {
        return FindMaterialsForMesh(mesh).FirstOrDefault(material => material != null);
    }

    private List<Material?> FindMaterialsForMeshPreview(Mesh mesh)
    {
        if (!assetsManager.LazyLoading)
        {
            return FindMaterialsForMesh(mesh);
        }

        if (meshToMaterialsCache != null && meshToMaterialsCache.TryGetValue(mesh, out var cachedList))
        {
            return new List<Material?>(cachedList);
        }

        var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
        if (semanticMaterials.Count > 0)
        {
            return semanticMaterials;
        }

        if (CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
        {
            return new List<Material?>();
        }

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        if (filesSnapshot.Count > 0)
        {
            using (assetsManager.LruCache.SuspendEviction())
            {
                BuildLazyPreviewReferenceIndexes(filesSnapshot);
            }

            if (meshToMaterialsCache != null && meshToMaterialsCache.TryGetValue(mesh, out cachedList))
            {
                return new List<Material?>(cachedList);
            }
        }

        return new List<Material?>();
    }

    private static readonly HashSet<ClassIDType> MaterialPreviewReferenceTypes = new()
    {
        ClassIDType.Material,
        ClassIDType.Texture2D
    };

    private static readonly HashSet<ClassIDType> MeshPreviewReferenceTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Material
    };

    private static readonly HashSet<ClassIDType> SpritePreviewReferenceTypes = new()
    {
        ClassIDType.Sprite,
        ClassIDType.Texture2D,
        ClassIDType.SpriteAtlas
    };

    private static readonly HashSet<ClassIDType> AnimationClip2DPreviewReferenceTypes = new()
    {
        ClassIDType.AnimationClip,
        ClassIDType.Sprite,
        ClassIDType.Texture2D,
        ClassIDType.SpriteAtlas
    };

    private static readonly HashSet<ClassIDType> LazyConnectionReferenceTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Material,
        ClassIDType.Avatar,
        ClassIDType.Animator,
        ClassIDType.AnimatorController,
        ClassIDType.AnimatorOverrideController,
        ClassIDType.RuntimeAnimatorController
    };

    private static readonly HashSet<string> NonDiffuseSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
        "_Normal", "Normal", "normal",
        "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
        "_EmissionMap", "_ParallaxMap", "_DetailMask",
        "_Cubemap", "_ReflectionTex", "_ShadowMap"
    };

    private Texture2D? FindTextureForMaterial(Material material)
    {
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialMainTextureCache.TryGetValue(material, out var directCachedTexture))
        {
            return directCachedTexture;
        }

        var semanticTexture = TryLoadTextureForMaterialFromSemanticCache(material);
        if (semanticTexture != null)
        {
            return semanticTexture;
        }

        IndexMaterialTextures(material);
        return materialMainTextureCache.TryGetValue(material, out var indexedTexture) ? indexedTexture : null;
    }

    private Texture2D? SelectMainTextureForMaterial(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
    {
        return SelectMainTextureForMaterialBackground(displayMaterial, textureSlots);
    }

    private Texture2D? GetMaterialTextureSlot(Material material, string slotName)
    {
        var cachedTexture = materialTextureSlotsCache != null
            && materialTextureSlotsCache.TryGetValue(material, out var slots)
            && slots.TryGetValue(slotName, out var texture)
            ? texture
            : null;

        if (cachedTexture != null)
        {
            return cachedTexture;
        }

        var semanticTexture = TryLoadTextureForMaterialFromSemanticCache(material, slotName);
        if (semanticTexture != null)
        {
            return semanticTexture;
        }

        IndexMaterialTextures(material);
        return materialTextureSlotsCache != null
               && materialTextureSlotsCache.TryGetValue(material, out slots)
               && slots.TryGetValue(slotName, out texture)
            ? texture
            : null;
    }

    private Texture2D? TryLoadTextureForMaterialFromSemanticCache(Material material, string? slotName = null)
    {
        if (!assetsManager.LazyLoading || material.assetsFile == null || currentScanResult == null)
        {
            return null;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return null;
        }

        var materialAssetId = AssetHandle.BuildUniqueID(material.assetsFile, material.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var texture = TryResolveTextureIdsForMaterialFromSemanticCache(folderPath, signature, material, materialAssetId, slotName);
        if (texture != null)
        {
            return texture;
        }

        var parentMaterialId = GetMaterialParentAssetIdForSemanticCache(material);
        if (!string.IsNullOrEmpty(parentMaterialId)
            && !string.Equals(parentMaterialId, materialAssetId, StringComparison.Ordinal))
        {
            return TryResolveTextureIdsForMaterialFromSemanticCache(folderPath, signature, material, parentMaterialId, slotName);
        }

        return null;
    }

    private Texture2D? TryResolveTextureIdsForMaterialFromSemanticCache(
        string folderPath,
        string signature,
        Material cacheMaterial,
        string materialAssetId,
        string? slotName)
    {
        var textureIds = _sqliteCache.LoadMaterialTextureAssetIds(folderPath, signature, materialAssetId, slotName);
        foreach (var textureId in textureIds)
        {
            var handle = assetsManager.ProjectIndex.GetHandle(textureId);
            if (handle == null)
            {
                continue;
            }

            var asset = ResolveSemanticRelationHandleForPreview(handle);
            if (asset is not Texture2D texture)
            {
                continue;
            }

            if (string.IsNullOrEmpty(slotName))
            {
                materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();
                materialMainTextureCache[cacheMaterial] = texture;
            }
            else
            {
                materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
                if (!materialTextureSlotsCache.TryGetValue(cacheMaterial, out var slots))
                {
                    slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
                    materialTextureSlotsCache[cacheMaterial] = slots;
                }

                slots[slotName] = texture;
            }

            return texture;
        }

        return null;
    }

    private string GetMaterialParentAssetIdForSemanticCache(Material material)
    {
        if (material.assetsFile == null || material.m_Parent == null || material.m_Parent.IsNull)
        {
            return string.Empty;
        }

        if (material.m_Parent.TryGet(out var parentMaterial) && parentMaterial?.assetsFile != null)
        {
            return AssetHandle.BuildUniqueID(parentMaterial.assetsFile, parentMaterial.m_PathID);
        }

        var handle = FindMaterialHandleForPPtr(material, material.m_Parent);
        return handle?.UniqueID ?? string.Empty;
    }

    private void IndexMaterialTextures(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialTextureSlotsCache.ContainsKey(material) && materialMainTextureCache.ContainsKey(material))
        {
            return;
        }

        var displayMaterial = ResolveMaterialForPreview(material) ?? material;
        if (!materialTextureSlotsCache.TryGetValue(displayMaterial, out var slots))
        {
            slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
            foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
            {
                var textureRef = texEnv.Value?.m_Texture;
                slots[texEnv.Key] = textureRef != null && !textureRef.IsNull
                    ? ResolveTexturePPtrForPreview(displayMaterial, textureRef)
                    : null;
            }

            materialTextureSlotsCache[displayMaterial] = slots;
            materialMainTextureCache[displayMaterial] = SelectMainTextureForMaterial(displayMaterial, slots);
        }

        if (!ReferenceEquals(displayMaterial, material))
        {
            materialTextureSlotsCache[material] = slots;
            materialMainTextureCache[material] = materialMainTextureCache[displayMaterial];
        }
    }

    private void EnsureMeshPreviewDependenciesLoaded(Mesh mesh)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(mesh.assetsFile);
        MaterializeReferenceObjects(mesh.assetsFile, MeshPreviewReferenceTypes);

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, MeshPreviewReferenceTypes);
        }

        BuildLazyPreviewReferenceIndexes(filesSnapshot);
    }

    private void EnsureMaterialPreviewDependenciesLoaded(Material material)
    {
        if (!assetsManager.LazyLoading || material.assetsFile == null)
        {
            return;
        }

        EnsureIndexedExternalSourcesLoaded(material.assetsFile);

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, MaterialPreviewReferenceTypes);
        }
    }

    private void EnsureSpritePreviewDependenciesLoaded(Sprite sprite)
    {
        if (!assetsManager.LazyLoading || sprite.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(sprite.assetsFile);
        MaterializeReferenceObjects(sprite.assetsFile, SpritePreviewReferenceTypes);

        var filesSnapshot = GetLoadedFilesSnapshot();
        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, SpritePreviewReferenceTypes);
        }

        PrepareLoadedSpriteAtlasReferencesForPreview(filesSnapshot);
    }

    private void EnsureAnimationClip2DPreviewDependenciesLoaded(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(clip.assetsFile);
        MaterializeReferenceObjects(clip.assetsFile, AnimationClip2DPreviewReferenceTypes);

        var filesSnapshot = GetLoadedFilesSnapshot();
        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, AnimationClip2DPreviewReferenceTypes);
        }

        PrepareLoadedSpriteAtlasReferencesForPreview(filesSnapshot);
    }

    private void EnsureIndexedExternalSourcesLoaded(SerializedFile sourceFile)
    {
        if (sourceFile == null)
        {
            return;
        }

        var pending = new Queue<SerializedFile>();
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedExternals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(sourceFile);

        while (pending.Count > 0)
        {
            var currentFile = pending.Dequeue();
            if (currentFile == null || !processedFiles.Add(GetSerializedFileDependencyKey(currentFile)))
            {
                continue;
            }

            if (currentFile.m_Externals == null || currentFile.m_Externals.Count == 0)
            {
                continue;
            }

            foreach (var external in currentFile.m_Externals)
            {
                if (external == null || (string.IsNullOrWhiteSpace(external.fileName) && string.IsNullOrWhiteSpace(external.pathName)))
                {
                    continue;
                }

                var externalKey = $"{external.fileName}|{external.pathName}";
                if (!processedExternals.Add(externalKey))
                {
                    continue;
                }

                if (TryGetLoadedExternalFile(external, out var loadedExternalFile))
                {
                    pending.Enqueue(loadedExternalFile);
                    continue;
                }

                var sourcePath = ResolveIndexedExternalSourcePath(external);
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                try
                {
                    RemovePendingFileFromProgressiveQueue(sourcePath);
                    assetsManager.LoadFilesForPreview(sourcePath);
                    assetsManager.WaitForAssetsFileLoaded(external.fileName, 5000);
                    if (TryGetLoadedExternalFile(external, out loadedExternalFile))
                    {
                        pending.Enqueue(loadedExternalFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load external preview source {Path.GetFileName(sourcePath)}: {ex.Message}");
                }
            }
        }
    }

    private List<SerializedFile> GetLoadedFilesSnapshot()
    {
        lock (assetsManager.loadLock)
        {
            return assetsManager.assetsFileList.ToList();
        }
    }

    private bool TryGetLoadedExternalFile(FileIdentifier external, out SerializedFile sourceFile)
    {
        sourceFile = null!;
        if (external == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(external.fileName)
            && ((assetsManager.TryFindSerializedFile(external.fileName, null, out sourceFile) && sourceFile != null)
                || (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out sourceFile) && sourceFile != null)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(external.pathName)
            && assetsManager.TryFindSerializedFile(Path.GetFileName(external.pathName), external.pathName, out sourceFile)
            && sourceFile != null)
        {
            return true;
        }

        return false;
    }

    private string? ResolveIndexedExternalSourcePath(FileIdentifier external)
    {
        if (external == null)
        {
            return null;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidateFileName(candidates, external.fileName);
        AddCandidateFileName(candidates, external.pathName);

        foreach (var candidate in candidates)
        {
            var direct = TryResolveExistingPath(candidate);
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }

            if (!string.IsNullOrEmpty(assetsManager.ProjectRoot))
            {
                direct = TryResolveExistingPath(Path.Combine(assetsManager.ProjectRoot, candidate));
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }
            }

            if (lazySourcePathBySerializedFile.TryGetValue(candidate, out var mappedSourcePath))
            {
                direct = TryResolveExistingPath(mappedSourcePath);
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }
            }

            var indexedPath = assetsManager.ProjectIndex
                .GetHandlesForFile(candidate)
                .Select(ResolveLazyHandleSourcePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (!string.IsNullOrEmpty(indexedPath))
            {
                return indexedPath;
            }
        }

        foreach (var candidate in candidates)
        {
            var found = FindSourceFileByNameInProjectRoot(candidate);
            if (!string.IsNullOrEmpty(found))
            {
                return found;
            }
        }

        return null;
    }

    private static string GetSerializedFileDependencyKey(SerializedFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.originalPath))
        {
            return file.originalPath;
        }

        if (!string.IsNullOrWhiteSpace(file.fullName))
        {
            return file.fullName;
        }

        return file.fileName ?? string.Empty;
    }

    private static void PrepareLoadedSpriteAtlasReferencesForPreview(List<SerializedFile> filesSnapshot)
    {
        if (filesSnapshot.Count == 0)
        {
            return;
        }

        var sprites = new List<Sprite>();
        var atlases = new List<SpriteAtlas>();
        foreach (var file in filesSnapshot)
        {
            AssetStudio.Object[] objects;
            lock (file)
            {
                objects = file.Objects.ToArray();
            }

            foreach (var obj in objects)
            {
                if (obj is Sprite sprite)
                {
                    ResetSpriteReferenceCache(sprite);
                    sprites.Add(sprite);
                }
                else if (obj is SpriteAtlas atlas)
                {
                    ResetSpriteAtlasReferenceCache(atlas);
                    atlases.Add(atlas);
                }
            }
        }

        if (sprites.Count == 0 || atlases.Count == 0)
        {
            return;
        }

        var spriteAtlasCache = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();
        foreach (var atlas in atlases)
        {
            if (atlas.m_RenderDataMap != null)
            {
                foreach (var key in atlas.m_RenderDataMap.Keys)
                {
                    spriteAtlasCache[key] = atlas;
                }
            }

            foreach (var packedSprite in atlas.m_PackedSprites ?? Array.Empty<PPtr<Sprite>>())
            {
                if (packedSprite != null && packedSprite.TryGet(out var sprite) && sprite != null)
                {
                    if (sprite.m_SpriteAtlas == null || sprite.m_SpriteAtlas.IsNull)
                    {
                        sprite.m_SpriteAtlas?.Set(atlas);
                    }
                    else if (sprite.m_SpriteAtlas.TryGet(out var oldAtlas) && oldAtlas?.m_IsVariant == true)
                    {
                        sprite.m_SpriteAtlas.Set(atlas);
                    }
                }
            }
        }

        foreach (var sprite in sprites)
        {
            if (sprite.m_SpriteAtlas != null
                && !sprite.m_SpriteAtlas.IsNull
                && sprite.m_SpriteAtlas.TryGet(out _))
            {
                continue;
            }

            if (sprite.m_RenderDataKey.Key != Guid.Empty
                && spriteAtlasCache.TryGetValue(sprite.m_RenderDataKey, out var atlas)
                && sprite.m_SpriteAtlas != null)
            {
                sprite.m_SpriteAtlas.Set(atlas);
            }
        }
    }

    private static void ResetSpriteReferenceCache(Sprite sprite)
    {
        sprite.m_SpriteAtlas?.ResetCache();
        if (sprite.m_RD == null)
        {
            return;
        }

        sprite.m_RD.texture?.ResetCache();
        sprite.m_RD.alphaTexture?.ResetCache();
        foreach (var secondaryTexture in sprite.m_RD.secondaryTextures ?? Array.Empty<SecondarySpriteTexture>())
        {
            secondaryTexture?.texture?.ResetCache();
        }
    }

    private static void ResetSpriteAtlasReferenceCache(SpriteAtlas atlas)
    {
        foreach (var packedSprite in atlas.m_PackedSprites ?? Array.Empty<PPtr<Sprite>>())
        {
            packedSprite?.ResetCache();
        }

        if (atlas.m_RenderDataMap == null)
        {
            return;
        }

        foreach (var data in atlas.m_RenderDataMap.Values)
        {
            data.texture?.ResetCache();
            data.alphaTexture?.ResetCache();
            foreach (var secondaryTexture in data.secondaryTextures ?? Array.Empty<SecondarySpriteTexture>())
            {
                secondaryTexture?.texture?.ResetCache();
            }
        }
    }

    private void MaterializeReferenceObjects(
        SerializedFile sourceFile,
        HashSet<ClassIDType> referenceTypes,
        LazyConnectionBuildDiagnostics? diagnostics = null)
    {
        var handles = assetsManager.ProjectIndex
            .GetHandlesForFile(sourceFile.fileName)
            .Where(handle => referenceTypes.Contains(handle.Type)
                && (ReferenceEquals(handle.SourceFile, sourceFile)
                    || IsSameLazySource(handle.OriginalPath, sourceFile.originalPath)
                    || string.IsNullOrEmpty(handle.OriginalPath)))
            .ToList();

        diagnostics?.RecordMaterializationCandidateCount(handles.Count);
        foreach (var handle in handles)
        {
            if (handle.SourceFile?.reader == null)
            {
                handle.SourceFile = sourceFile;
            }

            var obj = assetsManager.ResolveHandle(handle);
            if (obj == null)
            {
                diagnostics?.RecordFailedObject();
                continue;
            }

            diagnostics?.RecordResolvedObject(handle.Type);
            if (handle.Tag is AssetItem item)
            {
                UpdateLazyAssetItemHandle(item, handle);
            }
        }
    }

    private void BuildLazyPreviewReferenceIndexes(List<SerializedFile> filesSnapshot)
    {
        BuildAssetReferenceIndexesBackground(
            filesSnapshot,
            new List<AssetItem>(),
            out _,
            out var localMeshToMaterialsCache,
            out var localMeshAssociatedRenderersCache,
            out var localMeshSourceTypesCache,
            out var localMaterialMainTextureCache,
            out var localMaterialPreviewMaterialCache,
            out var localMaterialTextureSlotsCache,
            out _);

        lock (previewCacheLock)
        {
            meshToMaterialsCache = localMeshToMaterialsCache;
            meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
            meshSourceTypesCache = localMeshSourceTypesCache;
            materialMainTextureCache = localMaterialMainTextureCache;
            materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
            materialTextureSlotsCache = localMaterialTextureSlotsCache;
        }
    }

    private static bool IsSameLazySource(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private Texture2D? ResolveTexturePPtr(Material material, PPtr<Texture> textureRef)
    {
        return ResolveTexturePPtrForPreview(material, textureRef);
    }

    private Texture2D? ResolveTexturePPtrForPreview(Material material, PPtr<Texture> textureRef)
    {
        if (textureRef == null || textureRef.IsNull)
        {
            return null;
        }

        var directTexture = ResolveTexturePPtrBackground(material, textureRef);
        if (directTexture != null)
        {
            return directTexture;
        }

        if (!assetsManager.LazyLoading || material.assetsFile == null)
        {
            return null;
        }

        var handle = FindTextureHandleForPPtr(material, textureRef);
        if (handle == null)
        {
            return null;
        }

        return ResolveSemanticRelationHandleForPreview(handle) as Texture2D;
    }

    private AssetHandle? FindTextureHandleForPPtr(Material material, PPtr<Texture> textureRef)
    {
        if (material.assetsFile == null || textureRef == null || textureRef.IsNull)
        {
            return null;
        }

        if (textureRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, textureRef.m_PathID));
            if (IsTexture2DHandleForPath(handle, textureRef.m_PathID))
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (textureRef.m_FileID == 0)
        {
            candidateFileNames.Add(material.assetsFile.fileName);
        }
        else if (textureRef.m_FileID > 0
            && material.assetsFile.m_Externals != null
            && textureRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[textureRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, textureRef.m_PathID));
                if (IsTexture2DHandleForPath(handle, textureRef.m_PathID))
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => IsTexture2DHandleForPath(candidate, textureRef.m_PathID));
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private AssetHandle? FindMaterialHandleForPPtr(Material material, PPtr<Material> materialRef)
    {
        if (material.assetsFile == null || materialRef == null || materialRef.IsNull)
        {
            return null;
        }

        if (materialRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, materialRef.m_PathID));
            if (IsMaterialHandleForPath(handle, materialRef.m_PathID))
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (materialRef.m_FileID == 0)
        {
            candidateFileNames.Add(material.assetsFile.fileName);
        }
        else if (materialRef.m_FileID > 0
            && material.assetsFile.m_Externals != null
            && materialRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[materialRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, materialRef.m_PathID));
                if (IsMaterialHandleForPath(handle, materialRef.m_PathID))
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => IsMaterialHandleForPath(candidate, materialRef.m_PathID));
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private static void AddCandidateFileName(HashSet<string> candidates, string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return;
        }

        candidates.Add(fileNameOrPath);
        var safeName = Path.GetFileName(fileNameOrPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(safeName))
        {
            candidates.Add(safeName);
        }
    }

    private static bool IsTexture2DHandleForPath(AssetHandle? handle, long pathId)
    {
        return handle != null && handle.PathID == pathId && handle.Type == ClassIDType.Texture2D;
    }

    private static bool IsMaterialHandleForPath(AssetHandle? handle, long pathId)
    {
        return handle != null && handle.PathID == pathId && handle.Type == ClassIDType.Material;
    }

    private async void PreviewMonoBehaviour(AssetItem assetItem, MonoBehaviour m_MonoBehaviour, string fbxHeader, string? dumpStr)
    {
        try
        {
            object? obj = m_MonoBehaviour.ToType();
            if (obj == null)
            {
                var typeTree = await MonoBehaviourToTypeTree(m_MonoBehaviour);
                if (typeTree != null)
                {
                    obj = m_MonoBehaviour.ToType(typeTree);
                }
            }

            if (obj != null)
            {
                var str = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                SetTextWithTruncation(TextPreviewBox, fbxHeader + str);
                TextPreviewBox.IsVisible = true;
                PreviewLabel.IsVisible = false;
                StatusStripUpdate("MonoBehaviour preview loaded (JSON format).");
                return;
            }
        }
        catch
        {
            // Fallback
        }

        if (dumpStr == null)
        {
            var typeTree = await MonoBehaviourToTypeTree(m_MonoBehaviour);
            if (typeTree != null)
            {
                dumpStr = m_MonoBehaviour.Dump(typeTree);
            }
        }

        if (dumpStr != null)
        {
            SetTextWithTruncation(TextPreviewBox, fbxHeader + dumpStr);
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate("MonoBehaviour loaded (text dump).");
        }
        else
        {
            StatusStripUpdate("MonoBehaviour loaded (no dump/types available).");
        }
    }

    private void UpdateImagePreview(bool forceCpu = false)
    {
        var previewTexture = currentPreviewTexture;
        var previewSprite = currentPreviewSprite;
        if (previewTexture == null && previewSprite == null)
            return;

        long currentId = ++texturePreviewIdCounter;

        if (useGpuTexturePreview && TextureGLPreview != null && !forceCpu)
        {
            StatusStripUpdate("Loading preview (GPU)...");

            Task.Run(() =>
            {
                try
                {
                    Image<Bgra32>? decodedImage = null;
                    int width = 0;
                    int height = 0;
                    string infoText = string.Empty;
                    bool isSprite = previewSprite != null;

                    if (previewTexture != null)
                    {
                        width = previewTexture.m_Width;
                        height = previewTexture.m_Height;

                        infoText = $"Width: {width}\nHeight: {height}\nFormat: {previewTexture.m_TextureFormat}";
                        switch (previewTexture.m_TextureSettings.m_FilterMode)
                        {
                            case 0: infoText += "\nFilter Mode: Point "; break;
                            case 1: infoText += "\nFilter Mode: Bilinear "; break;
                            case 2: infoText += "\nFilter Mode: Trilinear "; break;
                        }
                        infoText += $"\nAnisotropic level: {previewTexture.m_TextureSettings.m_Aniso}\nMip map bias: {previewTexture.m_TextureSettings.m_MipBias}";
                        switch (previewTexture.m_TextureSettings.m_WrapMode)
                        {
                            case 0: infoText += "\nWrap mode: Repeat"; break;
                            case 1: infoText += "\nWrap mode: Clamp"; break;
                        }
                    }
                    else if (previewSprite != null)
                    {
                        decodedImage = previewSprite.GetImage();
                        if (decodedImage == null)
                        {
                            throw new Exception("Failed to decode sprite image on CPU.");
                        }
                        width = decodedImage.Width;
                        height = decodedImage.Height;
                        infoText = $"Width: {width}\nHeight: {height}\n";
                    }

                    int validChannel = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (textureChannels[i]) validChannel++;
                    }

                    infoText += "\nChannels: ";
                    if (validChannel == 0)
                    {
                        infoText += "None";
                    }
                    else
                    {
                        var channelNames = new string[4] { "R", "G", "B", "A" };
                        var activeList = new List<string>();
                        for (int i = 0; i < 4; i++)
                        {
                            if (textureChannels[i])
                                activeList.Add(channelNames[i]);
                        }
                        infoText += string.Join(" ", activeList);
                    }
                    infoText += "\nRender mode: GPU (OpenGL)";

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            try
                            {
                                ImagePreviewBox.IsVisible = false;
                                GLPreviewControl.IsVisible = false;
                                TextPreviewBox.IsVisible = false;
                                PreviewLabel.IsVisible = false;
                                TextureGLPreview.IsVisible = true;
                                TextureGLPreview.Focus();

                                if (isSprite && decodedImage != null)
                                {
                                    TextureGLPreview.SetImage(decodedImage);
                                }
                                else if (previewTexture != null)
                                {
                                    TextureGLPreview.SetTexture(previewTexture);
                                }
                                TextureGLPreview.SetChannels(textureChannels);

                                if (displayInfo.IsChecked == true)
                                {
                                    PreviewInfoOverlay.Text = infoText;
                                    PreviewInfoBorder.IsVisible = true;
                                }
                                else
                                {
                                    PreviewInfoBorder.IsVisible = false;
                                }

                                StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle | Drag to Pan, Scroll to Zoom");
                            }
                            catch (Exception ex)
                            {
                                logger.Log(LoggerEvent.Warning, $"GPU texture preview failed setup: {ex.Message}. Falling back to CPU.");
                                UpdateImagePreview(forceCpu: true);
                            }
                            finally
                            {
                                decodedImage?.Dispose();
                            }
                        }
                        else
                        {
                            decodedImage?.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    logger.Log(LoggerEvent.Warning, $"GPU texture preview failed preparation: {ex.Message}. Falling back to CPU.");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            UpdateImagePreview(forceCpu: true);
                        }
                    });
                }
            });
            return;
        }

        // CPU Fallback path
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }

        StatusStripUpdate("Loading preview (CPU)...");

        Task.Run(() =>
        {
            try
            {
                Image<Bgra32>? image = null;
                string infoText = string.Empty;
                bool isTexture = previewTexture != null;

                if (previewTexture != null)
                {
                    image = previewTexture.ConvertToImage(true);
                    if (image != null)
                    {
                        infoText = $"Width: {previewTexture.m_Width}\nHeight: {previewTexture.m_Height}\nFormat: {previewTexture.m_TextureFormat}";
                        switch (previewTexture.m_TextureSettings.m_FilterMode)
                        {
                            case 0: infoText += "\nFilter Mode: Point "; break;
                            case 1: infoText += "\nFilter Mode: Bilinear "; break;
                            case 2: infoText += "\nFilter Mode: Trilinear "; break;
                        }
                        infoText += $"\nAnisotropic level: {previewTexture.m_TextureSettings.m_Aniso}\nMip map bias: {previewTexture.m_TextureSettings.m_MipBias}";
                        switch (previewTexture.m_TextureSettings.m_WrapMode)
                        {
                            case 0: infoText += "\nWrap mode: Repeat"; break;
                            case 1: infoText += "\nWrap mode: Clamp"; break;
                        }
                    }
                }
                else if (previewSprite != null)
                {
                    image = previewSprite.GetImage();
                    if (image != null)
                    {
                        infoText = $"Width: {image.Width}\nHeight: {image.Height}\n";
                    }
                }

                if (image == null)
                {
                    string failReason = "Unsupported image for preview";
                    if (previewTexture != null)
                    {
                        failReason = $"Unsupported Texture Format: {previewTexture.m_TextureFormat}";
                    }
                    else if (previewSprite != null)
                    {
                        if (previewSprite.m_SpriteAtlas != null && previewSprite.m_SpriteAtlas.TryGet(out var atlas) && atlas.m_RenderDataMap.TryGetValue(previewSprite.m_RenderDataKey, out var atlasData) && atlasData.texture.TryGet(out var tex1))
                            failReason = $"Unsupported Sprite Texture Format: {tex1.m_TextureFormat}";
                        else if (previewSprite.m_RD.texture.TryGet(out var tex2))
                            failReason = $"Unsupported Sprite Texture Format: {tex2.m_TextureFormat}";
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            StatusStripUpdate(failReason);
                            ImagePreviewBox.IsVisible = false;
                            PreviewInfoBorder.IsVisible = false;
                        }
                    });
                    return;
                }

                int validChannel = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (textureChannels[i])
                    {
                        validChannel++;
                    }
                }

                if (validChannel != 4)
                {
                    image.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < accessor.Height; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (int x = 0; x < accessor.Width; x++)
                            {
                                ref Bgra32 pixel = ref row[x];
                                pixel.R = textureChannels[0] ? pixel.R : (validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue);
                                pixel.G = textureChannels[1] ? pixel.G : (validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue);
                                pixel.B = textureChannels[2] ? pixel.B : (validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue);
                                pixel.A = textureChannels[3] ? pixel.A : byte.MaxValue;
                            }
                        }
                    });
                }

                using (var ms = new MemoryStream())
                {
                    image.SaveAsPng(ms);
                    ms.Position = 0;
                    var bitmap = new global::Avalonia.Media.Imaging.Bitmap(ms);

                    infoText += "\nChannels: ";
                    if (validChannel == 0)
                    {
                        infoText += "None";
                    }
                    else
                    {
                        var channelNames = new string[4] { "R", "G", "B", "A" };
                        var activeList = new List<string>();
                        for (int i = 0; i < 4; i++)
                        {
                            if (textureChannels[i])
                                activeList.Add(channelNames[i]);
                        }
                        infoText += string.Join(" ", activeList);
                    }
                    infoText += "\nRender mode: CPU (Fallback)";

                    image.Dispose();

                    string finalInfoText = infoText;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            ImagePreviewBox.Source = bitmap;
                            ImagePreviewBox.IsVisible = true;
                            TextPreviewBox.IsVisible = false;
                            PreviewLabel.IsVisible = false;

                            if (displayInfo.IsChecked == true)
                            {
                                PreviewInfoOverlay.Text = finalInfoText;
                                PreviewInfoBorder.IsVisible = true;
                            }
                            else
                            {
                                PreviewInfoBorder.IsVisible = false;
                            }

                            if (isTexture)
                            {
                                StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle");
                            }
                            else
                            {
                                StatusStripUpdate(string.Empty);
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter)
                    {
                        StatusStripUpdate($"Error generating preview: {ex.Message}");
                        ImagePreviewBox.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                    }
                });
            }
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyModifiers == KeyModifiers.Control && GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            bool handled = false;
            switch (e.Key)
            {
                case Key.W:
                    GLPreviewControl.WireframeMode = (GLPreviewControl.WireframeMode + 1) % 3;
                    handled = true;
                    break;
                case Key.S:
                    GLPreviewControl.ShadeMode = GLPreviewControl.ShadeMode == 0 ? 1 : 0;
                    handled = true;
                    break;
                case Key.N:
                    GLPreviewControl.NormalMode = GLPreviewControl.NormalMode == 0 ? 1 : 0;
                    handled = true;
                    break;
            }
            if (handled)
            {
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Control && (currentPreviewTexture != null || currentPreviewSprite != null))
        {
            bool handled = false;
            switch (e.Key)
            {
                case Key.R:
                    textureChannels[0] = !textureChannels[0];
                    handled = true;
                    break;
                case Key.G:
                    textureChannels[1] = !textureChannels[1];
                    handled = true;
                    break;
                case Key.B:
                    textureChannels[2] = !textureChannels[2];
                    handled = true;
                    break;
                case Key.A:
                    textureChannels[3] = !textureChannels[3];
                    handled = true;
                    break;
            }

            if (handled)
            {
                UpdateImagePreview();
                e.Handled = true;
            }
        }
    }

    private async void ListSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        PrioritizeUserInteraction();
        listSearchDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        listSearchDebounce = debounce;

        try
        {
            await Task.Delay(800, debounce.Token);
            if (!debounce.IsCancellationRequested)
            {
                PrioritizeUserInteraction();
                await FilterAssetListAsync(debounce.Token);
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private bool isSorting;
    private async void AssetListDataGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (isSorting) return;
        PrioritizeUserInteraction();
        isSorting = true;
        try
        {
            var column = e.Column;
            if (column == null) return;

            var sortMember = column.SortMemberPath ?? column.Header?.ToString();
            if (string.IsNullOrEmpty(sortMember)) return;

            if (assetListSortMember == sortMember)
            {
                assetListSortDescending = !assetListSortDescending;
            }
            else
            {
                assetListSortMember = sortMember;
                assetListSortDescending = false;
            }

            await ApplyAssetListSortAsync();
            UpdateAssetListSortHeaderIndicators();
        }
        catch (Exception ex)
        {
            Logger.Error("Error sorting asset list", ex);
            StatusStripUpdate("Error sorting asset list. See error log.");
        }
        finally
        {
            isSorting = false;
        }
    }

    private void AssetListDataGrid_CellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        var row = e.Row;
        if (row?.DataContext is not AssetItem item)
        {
            assetContextItem = null;
            assetContextCellText = string.Empty;
            return;
        }

        assetContextItem = item;
        var column = e.Column;
        assetContextCellText = GetAssetCellText(item, column?.SortMemberPath ?? column?.Header?.ToString());

        var isRightButton = e.PointerPressedEventArgs?
            .GetCurrentPoint(AssetListDataGrid)
            .Properties
            .IsRightButtonPressed == true;
        var selectedItems = AssetListDataGrid.SelectedItems;
        if (isRightButton && selectedItems != null && !selectedItems.Contains(item))
        {
            AssetListDataGrid.SelectedItem = item;
        }
    }

    private void AssetListContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        var selectedAssets = GetSelectedAssets();
        var singleSelected = selectedAssets.Count == 1;
        var hasAnimatorWithClips = selectedAssets.Any(x => x.Type == ClassIDType.Animator)
            && selectedAssets.Any(x => x.Type == ClassIDType.AnimationClip);

        goToSceneHierarchyMenuItem.IsVisible = singleSelected && selectedAssets[0].TreeNode != null;
        showOriginalFileMenuItem.IsVisible = singleSelected;
        exportAnimatorWithSelectedAnimationClipMenuItem.IsVisible = hasAnimatorWithClips;
    }

    private async void CopyAssetCellText_Click(object? sender, RoutedEventArgs e)
    {
        var text = assetContextCellText;
        if (string.IsNullOrEmpty(text) && AssetListDataGrid.SelectedItem is AssetItem item)
        {
            text = item.Name;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            StatusStripUpdate("Copied asset cell text.");
        }
    }

    private async void ExportSelectedAssetsContext_Click(object? sender, RoutedEventArgs e)
    {
        await ExportAssets(GetSelectedAssets(), ExportMode.Convert);
    }

    private void GoToSceneHierarchy_Click(object? sender, RoutedEventArgs e)
    {
        var item = assetContextItem ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (item?.TreeNode == null)
        {
            StatusStripUpdate("Selected asset has no scene hierarchy node.");
            return;
        }

        item.TreeNode.ExpandAncestors();
        LeftTabControl.SelectedIndex = 0;
        SceneTreeView.SelectedItem = item.TreeNode;
        SceneTreeView.Focus();
    }

    private void ShowOriginalFile_Click(object? sender, RoutedEventArgs e)
    {
        var item = assetContextItem ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (item == null) return;
        ShowOriginalFile(item);
    }

    private async void ExportAnimatorWithSelectedAnimationClip_Click(object? sender, RoutedEventArgs e)
    {
        await ExportAnimatorWithSelectedAnimationClips(GetSelectedAssets());
    }

    private async Task FilterAssetListAsync(CancellationToken token)
    {
        var filterText = listSearch?.Text?.Trim();
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var assetsSnapshot = exportableAssets.ToList();

        // Capture selection before filtering
        var selectedUniqueIds = new HashSet<string>();
        string? oldSelectedUniqueId = null;
        if (AssetListDataGrid.SelectedItems != null)
        {
            foreach (var item in AssetListDataGrid.SelectedItems.OfType<AssetItem>())
            {
                var id = item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
                if (!string.IsNullOrEmpty(id))
                {
                    selectedUniqueIds.Add(id);
                }
            }

            var selectedItem = AssetListDataGrid.SelectedItem as AssetItem;
            if (selectedItem != null)
            {
                oldSelectedUniqueId = selectedItem.Handle != null ? selectedItem.Handle.UniqueID : selectedItem.UniqueID;
            }
        }

        // Find scroll viewer and save scroll offset
        var scrollViewer = FindVisualChild<ScrollViewer>(AssetListDataGrid);
        var scrollOffset = scrollViewer?.Offset ?? default;

        try
        {
            var result = await Task.Run(() =>
            {
                var matches = new List<AssetItem>();
                foreach (var x in assetsSnapshot)
                {
                    token.ThrowIfCancellationRequested();
                    if (x == null)
                    {
                        continue;
                    }

                    if (classFilter != null)
                    {
                        if (!AssetMatchesClassFilter(x, classFilter))
                        {
                            continue;
                        }
                    }
                    else if (selectedTypes != null)
                    {
                        if (selectedTypes.Count == 0 || !selectedTypes.Contains(x.Type))
                        {
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(filterText) && !AssetMatchesTextFilter(x, filterText))
                    {
                        continue;
                    }

                    matches.Add(x);
                }

                token.ThrowIfCancellationRequested();
                return SortAssetList(matches, sortMember, sortDescending);
            }, token);

            isRefreshingFilterList = true;
            try
            {
                ReplaceVisibleAssets(result);
                StatusStripUpdate($"Showing {visibleAssets.Count} assets");

                // Restore selection
                if (selectedUniqueIds.Count > 0)
                {
                    var newSelectedItems = new List<AssetItem>();
                    foreach (var item in visibleAssets)
                    {
                        var id = item.Handle != null ? item.Handle.UniqueID : item.UniqueID;
                        if (!string.IsNullOrEmpty(id) && selectedUniqueIds.Contains(id))
                        {
                            newSelectedItems.Add(item);
                        }
                    }

                    var selectedItems = AssetListDataGrid.SelectedItems;
                    if (selectedItems != null)
                    {
                        selectedItems.Clear();
                        foreach (var item in newSelectedItems)
                        {
                            selectedItems.Add(item);
                        }
                    }
                }
            }
            finally
            {
                isRefreshingFilterList = false;
            }

            // Restore scroll position
            if (scrollViewer != null && scrollOffset != default)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    scrollViewer.Offset = scrollOffset;
                }, DispatcherPriority.Background);
            }

            // Trigger selection changed logic if the selected item actually changed
            var newSelectedItem = AssetListDataGrid.SelectedItem as AssetItem;
            if (newSelectedItem != null)
            {
                UpdatePreloadWindow(newSelectedItem);
            }
            else
            {
                lock (preloaderLock)
                {
                    preloaderCts?.Cancel();
                }
            }
            var newSelectedUniqueId = newSelectedItem != null ? (newSelectedItem.Handle != null ? newSelectedItem.Handle.UniqueID : newSelectedItem.UniqueID) : null;
            if (newSelectedUniqueId != oldSelectedUniqueId)
            {
                _currentlySelectedUniqueID = newSelectedUniqueId;
                if (newSelectedItem != null)
                {
                    if (RightTabControl.SelectedIndex == 1)
                    {
                        _ = UpdateDumpForSelectedAsset();
                    }
                    QueuePreviewAsset(newSelectedItem);
                }
                else
                {
                    DumpTextBox.Text = string.Empty;
                    previewDebounce?.Cancel();
                    ClearPreview("Preview Panel");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Task canceled, ignore
        }
        catch (Exception ex)
        {
            Logger.Error("Error filtering asset list", ex);
            StatusStripUpdate("Error filtering asset list. See error log.");
        }
    }

    private async Task ApplyAssetListSortAsync()
    {
        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var currentAssets = visibleAssets.ToList();

        try
        {
            var sorted = await Task.Run(() => SortAssetList(currentAssets, sortMember, sortDescending));
            ReplaceVisibleAssets(sorted);
            StatusStripUpdate($"Showing {visibleAssets.Count} assets");
        }
        catch (Exception ex)
        {
            Logger.Error("Error sorting asset list", ex);
            StatusStripUpdate("Error sorting asset list. See error log.");
        }
    }

    private static bool AssetMatchesClassFilter(AssetItem item, AssetClassItem classFilter)
    {
        return (int)item.Type == classFilter.ClassID
            && string.Equals(
                item.SourceFile?.unityVersion ?? string.Empty,
                classFilter.UnityVersion ?? string.Empty,
                StringComparison.Ordinal);
    }

    private static bool AssetMatchesTextFilter(AssetItem item, string filterText)
    {
        return ContainsIgnoreCase(item.Name, filterText)
            || ContainsIgnoreCase(item.Container, filterText)
            || ContainsIgnoreCase(item.TypeString, filterText)
            || ContainsIgnoreCase(item.DisplayType, filterText)
            || ContainsIgnoreCase(item.PathIDString, filterText);
    }

    private static bool ContainsIgnoreCase(string? value, string filterText)
    {
        return !string.IsNullOrEmpty(value)
            && value.Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    private static List<AssetItem> SortAssetList(IEnumerable<AssetItem>? assets, string? sortMember, bool descending)
    {
        var sorted = assets?
            .Where(static x => x != null)
            .ToList() ?? new List<AssetItem>();

        if (sorted.Count <= 1 || string.IsNullOrEmpty(sortMember))
        {
            return sorted;
        }

        sorted.Sort((left, right) => CompareAssetItems(left, right, sortMember, descending));
        return sorted;
    }

    private static int CompareAssetItems(AssetItem? left, AssetItem? right, string sortMember, bool descending)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return 1;
        }
        if (right == null)
        {
            return -1;
        }

        int result = sortMember switch
        {
            "PathID" => left.PathID.CompareTo(right.PathID),
            "FullSize" or "Size" => left.FullSize.CompareTo(right.FullSize),
            "Container" => CompareNullableText(left.Container, right.Container),
            "DisplayType" or "Type" => CompareNullableText(left.DisplayType, right.DisplayType),
            "Name" => CompareNullableText(left.Name, right.Name),
            _ => 0
        };

        if (descending)
        {
            result = -result;
        }

        if (result != 0)
        {
            return result;
        }

        result = left.PathID.CompareTo(right.PathID);
        if (result != 0)
        {
            return result;
        }

        result = CompareNullableText(left.UniqueID, right.UniqueID);
        if (result != 0)
        {
            return result;
        }

        return CompareNullableText(left.Name, right.Name);
    }

    private static int CompareNullableText(string? left, string? right)
    {
        var leftMissing = string.IsNullOrEmpty(left);
        var rightMissing = string.IsNullOrEmpty(right);
        if (leftMissing && rightMissing)
        {
            return 0;
        }
        if (leftMissing)
        {
            return 1;
        }
        if (rightMissing)
        {
            return -1;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static string GetAssetCellText(AssetItem item, string? member)
    {
        return member switch
        {
            "Container" => item.Container ?? string.Empty,
            "DisplayType" or "Type" => item.DisplayType ?? string.Empty,
            "PathID" => item.PathIDString ?? string.Empty,
            "FullSize" or "Size" => item.FullSize.ToString(CultureInfo.InvariantCulture),
            _ => item.Name ?? string.Empty
        };
    }

    private void ReplaceVisibleAssets(IReadOnlyList<AssetItem>? items)
    {
        visibleAssets = items is { Count: > 0 }
            ? items.Where(static x => x != null).ToList()
            : new List<AssetItem>();
        RefreshAssetListItems();
    }

    private void SyncExportableAssetHandleIds()
    {
        lazyAssetItemsByHandleId.Clear();
        exportableAssetHandleIds.Clear();
        exportableAssetTypes.Clear();
        foreach (var item in exportableAssets)
        {
            if (!string.IsNullOrEmpty(item.Handle?.UniqueID))
            {
                lazyAssetItemsByHandleId[item.Handle.UniqueID] = item;
                exportableAssetHandleIds.Add(item.Handle.UniqueID);
            }
            exportableAssetTypes.Add(item.Type);
        }
    }

    private void ApplyExportableAssetIndexes(BuildAssetStructuresResult result)
    {
        lazyAssetItemsByHandleId = result.LazyAssetItemsByHandleId ?? new Dictionary<string, AssetItem>(StringComparer.Ordinal);
        exportableAssetHandleIds = result.ExportableAssetHandleIds ?? new HashSet<string>(StringComparer.Ordinal);
        exportableAssetTypes = result.ExportableAssetTypes ?? new HashSet<ClassIDType>();
    }

    private void EnsureAssetListItemsSource()
    {
        if (AssetListDataGrid.ItemsSource != visibleAssetItems)
        {
            AssetListDataGrid.ItemsSource = visibleAssetItems;
        }
    }

    private void RefreshAssetListItems()
    {
        visibleAssetItems = new BulkObservableCollection<AssetItem>(visibleAssets);
        EnsureAssetListItemsSource();
    }

    private void UpdateAssetListSortHeaderIndicators()
    {
        foreach (var column in AssetListDataGrid.Columns)
        {
            var sortMember = column.SortMemberPath ?? column.Header?.ToString();
            var baseHeader = sortMember switch
            {
                "Name" => "Name",
                "Container" => "Container",
                "DisplayType" or "Type" => "Type",
                "PathID" => "PathID",
                "FullSize" or "Size" => "Size",
                _ => column.Header?.ToString() ?? string.Empty
            };

            column.Header = sortMember == assetListSortMember
                ? $"{baseHeader} {(assetListSortDescending ? "(desc)" : "(asc)")}"
                : baseHeader;
        }
    }

    private static void SyncObservableCollection<T>(System.Collections.ObjectModel.ObservableCollection<T> collection, IReadOnlyList<T>? targetList)
    {
        if (collection == null) return;
        if (targetList == null)
        {
            collection.Clear();
            return;
        }

        if (targetList.Count == 0)
        {
            collection.Clear();
            return;
        }

        if (collection.Count == 0)
        {
            foreach (var item in targetList)
            {
                collection.Add(item);
            }
            return;
        }

        var targetSet = new HashSet<T>(targetList);

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        for (int i = 0; i < targetList.Count; i++)
        {
            var targetItem = targetList[i];

            if (i < collection.Count)
            {
                if (EqualityComparer<T>.Default.Equals(collection[i], targetItem))
                {
                    SyncCollectionItem(collection[i], targetItem);
                    continue;
                }

                int indexInCollection = -1;
                for (int j = i + 1; j < collection.Count; j++)
                {
                    if (EqualityComparer<T>.Default.Equals(collection[j], targetItem))
                    {
                        indexInCollection = j;
                        break;
                    }
                }

                if (indexInCollection != -1)
                {
                    SyncCollectionItem(collection[indexInCollection], targetItem);
                    collection.Move(indexInCollection, i);
                }
                else
                {
                    collection.Insert(i, targetItem);
                }
            }
            else
            {
                collection.Add(targetItem);
            }
        }

        while (collection.Count > targetList.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    private static void SyncCollectionItem<T>(T existingItem, T targetItem)
    {
        if (existingItem is AssetClassItem existingClass && targetItem is AssetClassItem targetClass)
        {
            existingClass.CopyFrom(targetClass);
        }
    }

    private void AppendFilteredAssetsToVisible(List<AssetItem> newItems)
    {
        if (newItems == null || newItems.Count == 0) return;

        EnsureAssetListItemsSource();
        var filterText = listSearch?.Text?.Trim();
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

        visibleAssetItems.BeginUpdate();
        try
        {
            foreach (var x in newItems)
            {
                if (x == null)
                {
                    continue;
                }

                if (classFilter != null)
                {
                    if (!AssetMatchesClassFilter(x, classFilter))
                    {
                        continue;
                    }
                }
                else if (selectedTypes != null)
                {
                    if (selectedTypes.Count == 0 || !selectedTypes.Contains(x.Type))
                    {
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(filterText) && !AssetMatchesTextFilter(x, filterText))
                {
                    continue;
                }

                AddVisibleAssetItem(x);
            }
        }
        finally
        {
            visibleAssetItems.EndUpdate();
        }

        StatusStripUpdate($"Showing {visibleAssets.Count} assets");
    }

    private void AddVisibleAssetItem(AssetItem item)
    {
        if (string.IsNullOrEmpty(assetListSortMember))
        {
            visibleAssets.Add(item);
            visibleAssetItems.Add(item);
            return;
        }

        var insertIndex = FindSortedInsertIndex(visibleAssets, item, assetListSortMember, assetListSortDescending);
        visibleAssets.Insert(insertIndex, item);
        visibleAssetItems.Insert(insertIndex, item);
    }

    private static int FindSortedInsertIndex(List<AssetItem> items, AssetItem item, string sortMember, bool descending)
    {
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (CompareAssetItems(items[mid], item, sortMember, descending) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>
    /// Incrementally updates the visible class items by updating counts on existing items
    /// and appending only new class entries. Avoids the full SyncObservableCollection diff.
    /// </summary>
    private void UpdateAssetClassesIncremental(List<AssetClassItem> updatedClassItems)
    {
        if (updatedClassItems == null) return;

        // Build lookup from updated class items
        var updatedLookup = new Dictionary<(int ClassID, string Name, string Namespace, string Assembly, string UnityVersion, string SourceFile, string SourceKind), AssetClassItem>();
        foreach (var item in updatedClassItems)
        {
            var key = (item.ClassID, item.Name, item.Namespace, item.Assembly, item.UnityVersion, item.SourceFile, item.SourceKind);
            updatedLookup[key] = item;
        }

        // Update counts for existing visible items
        foreach (var existing in visibleAssetClassItems)
        {
            var key = (existing.ClassID, existing.Name, existing.Namespace, existing.Assembly, existing.UnityVersion, existing.SourceFile, existing.SourceKind);
            if (updatedLookup.TryGetValue(key, out var updated))
            {
                existing.CopyFrom(updated);
                updatedLookup.Remove(key);
            }
        }

        // Append any genuinely new class items (filtered)
        var filter = classSearch.Text?.Trim();
        foreach (var newItem in updatedLookup.Values)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                if (!newItem.ClassID.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !newItem.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !newItem.Namespace.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !newItem.Assembly.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !newItem.SourceKind.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            visibleAssetClassItems.Add(newItem);
        }

        if (AssetClassesDataGrid.ItemsSource != visibleAssetClassItems)
        {
            AssetClassesDataGrid.ItemsSource = visibleAssetClassItems;
        }
    }

    private async void ExportAllAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Convert);
    private async void ExportSelectedAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Convert);
    private async void ExportFilteredAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Convert);
    private async void ExportAllAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets, ExportMode.Raw);
    private async void ExportSelectedAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Raw);
    private async void ExportFilteredAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Raw);
    private async void ExportAllAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets, ExportMode.Dump);
    private async void ExportSelectedAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Dump);
    private async void ExportFilteredAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets.ToList(), ExportMode.Dump);
    private async void ExportAllAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(exportableAssets);
    private async void ExportSelectedAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(GetSelectedAssets());
    private async void ExportFilteredAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(visibleAssets.ToList());

    private async void ExportErrorLog_Click(object? sender, RoutedEventArgs e)
    {
        await ErrorExporter.ExportErrorLog(this, logger, StatusStripUpdate);
    }

    private async Task ExportAssetsList(List<AssetItem> toExport)
    {
        if (toExport.Count == 0)
        {
            StatusStripUpdate("No assets to export.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);

        StatusStripUpdate("Exporting asset list to XML...");
        await Task.Run(() =>
        {
            try
            {
                var filename = Path.Combine(savePath, "assets.xml");
                var doc = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("Assets",
                        new System.Xml.Linq.XAttribute("filename", filename),
                        new System.Xml.Linq.XAttribute("createdAt", DateTime.UtcNow.ToString("s")),
                        toExport.Select(asset => new System.Xml.Linq.XElement("Asset",
                            new System.Xml.Linq.XElement("Name", asset.Name),
                            new System.Xml.Linq.XElement("Container", asset.Container),
                            new System.Xml.Linq.XElement("Type", new System.Xml.Linq.XAttribute("id", (int)asset.Type), asset.DisplayType),
                            new System.Xml.Linq.XElement("PathID", asset.PathID),
                            new System.Xml.Linq.XElement("Source", asset.SourceFile?.fullName ?? ""),
                            new System.Xml.Linq.XElement("Size", asset.FullSize)
                        ))
                    )
                );

                doc.Save(filename);

                StatusStripUpdate($"Finished exporting asset list to XML with {toExport.Count} items.");
                if (exportOptions.OpenAfterExport)
                {
                    OpenFolder(savePath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error exporting asset list to XML", ex);
                StatusStripUpdate($"Error exporting asset list to XML: {ex.Message}");
            }
        });
    }

    private List<AssetItem> GetSelectedAssets()
    {
        var selected = new List<AssetItem>();
        var selectedItems = AssetListDataGrid.SelectedItems;
        if (selectedItems == null)
        {
            return selected;
        }

        foreach (var item in selectedItems)
        {
            if (item is AssetItem assetItem)
            {
                selected.Add(assetItem);
            }
        }
        return selected;
    }

    private async Task ExportAssets(List<AssetItem> toExport, ExportMode mode)
    {
        if (mode == ExportMode.Convert)
        {
            toExport = toExport.Where(x => !ShouldSkipConvertedAsset(x)).ToList();
        }

        if (toExport.Count == 0)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        if (!assemblyLoader.Loaded && (mode == ExportMode.Convert || mode == ExportMode.Dump) && toExport.Any(x => x.Type == ClassIDType.MonoBehaviour))
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select Assembly Folder"));
            if (folders != null && folders.Count > 0)
            {
                SaveLoadFolder(folders[0].Path.LocalPath);
                assemblyLoader.Load(folders[0].Path.LocalPath);
            }
            else
            {
                assemblyLoader.Loaded = true;
            }
        }

        var exportFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));

        if (exportFolders == null || exportFolders.Count == 0) return;

        var savePath = exportFolders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        if (mode == ExportMode.Convert)
        {
            toExport = OrderConvertedAssetsForExport(toExport);
        }

        int total = toExport.Count;
        int exported = 0;
        int failed = 0;
        var exportErrors = new List<string>();

        StatusStripUpdate($"Exporting {total} assets...");

        await Task.Run(() =>
        {
            EnsureLazyAssetsLoadedForExport(toExport);
            var currentExportPath = Path.Combine(savePath, "export-current.txt");
            for (int j = 0; j < total; j++)
            {
                var asset = toExport[j];
                try
                {
                    WriteCurrentExport(currentExportPath, asset, j + 1, total);
                    var exportPath = GetExportPath(savePath, asset);
                    Directory.CreateDirectory(exportPath);
                    var fileName = FixFileName(asset.Name);
                    var filePath = Path.Combine(exportPath, fileName);

                    switch (mode)
                    {
                        case ExportMode.Raw:
                            filePath += GetRawExtension(asset);
                            if (!File.Exists(filePath))
                            {
                                var assetObj = asset.Asset;
                                if (assetObj != null)
                                {
                                    File.WriteAllBytes(filePath, assetObj.GetRawData());
                                    exported++;
                                }
                            }
                            break;
                        case ExportMode.Dump:
                            filePath += ".txt";
                            if (!File.Exists(filePath))
                            {
                                string? dump = null;
                                var assetObj = asset.Asset;
                                if (assetObj is MonoBehaviour m_MonoBehaviour)
                                {
                                    dump = m_MonoBehaviour.Dump();
                                    if (dump == null && assemblyLoader.Loaded)
                                    {
                                        var typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                                        if (typeTree != null)
                                        {
                                            dump = m_MonoBehaviour.Dump(typeTree);
                                        }
                                    }
                                }
                                else if (assetObj != null)
                                {
                                    dump = assetObj.Dump();
                                }
                                File.WriteAllText(filePath, dump ?? "");
                                exported++;
                            }
                            break;
                        case ExportMode.Convert:
                            if (ExportConvertFile(asset, exportPath))
                                exported++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    var error = $"Failed to export {asset.TypeString}: {asset.Name} (PathID: {asset.PathID})";
                    exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                    StatusStripUpdate($"Error exporting {asset.Name}: {ex.Message}");
                }

                var progress = (int)((j + 1.0) / total * 100);
                Dispatcher.UIThread.Post(() => progressBar.Value = progress);
            }
            ClearCurrentExport(savePath);
        });

        var errorReportPath = WriteErrorReport(savePath, exportErrors, logger);

        var status = exported == 0 ? "Nothing exported." : $"Finished exporting {exported} assets.";
        if (failed > 0) status += $" {failed} failed.";
        if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport && exported > 0)
        {
            OpenFolder(savePath);
        }
    }

    private static List<AssetItem> OrderConvertedAssetsForExport(List<AssetItem> assets)
    {
        return assets
            .OrderBy(x => x.Type == ClassIDType.Texture2D ? 0 : x.Type == ClassIDType.Material ? 1 : 2)
            .ToList();
    }

    private async Task ExportAnimatorWithSelectedAnimationClips(List<AssetItem> selectedAssets)
    {
        var animator = selectedAssets.FirstOrDefault(x => x.Type == ClassIDType.Animator);
        var animationList = selectedAssets.Where(x => x.Type == ClassIDType.AnimationClip).ToList();
        if (animator == null || animationList.Count == 0)
        {
            StatusStripUpdate("Select one Animator and one or more AnimationClips.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var selectedExportRoot = folders[0].Path.LocalPath;
        SaveExportFolder(selectedExportRoot);
        var exportPath = Path.Combine(selectedExportRoot, "Animator");
        Directory.CreateDirectory(exportPath);
        var exportFile = Path.Combine(exportPath, FixFileName(animator.Name) + ".fbx");
        var selectedGameObjects = GetTopLevelSelectedGameObjects(selectedAssets
            .Where(x => x.Type != ClassIDType.AnimationClip && x.TreeNode?.GameObject != null)
            .Select(x => x.TreeNode!.GameObject!)
            .Distinct()
            .ToList());

        StatusStripUpdate($"Exporting {animator.Name}...");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportPath, "export-current.txt");
        bool success = false;
        await Task.Run(() =>
        {
            try
            {
                EnsureLazyAssetsLoadedForExport(selectedAssets);
                var clips = animationList.Select(x => (AnimationClip)x.Asset!).ToArray();
                WriteCurrentExport(currentExportPath, animator, 1, 1);
                IImported convert = selectedGameObjects.Count > 0
                    ? new ModelConverter(animator.Name, selectedGameObjects, exportOptions.ConvertTextureFormat, clips)
                    : new ModelConverter((Animator)animator.Asset!, exportOptions.ConvertTextureFormat, clips);
                ExportFbx(convert, exportFile);
                success = true;
            }
            catch (Exception ex)
            {
                var error = $"Export Animator:{animator.Name} error";
                exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                Logger.Error(error, ex);
                StatusStripUpdate($"Error exporting {animator.Name}: {ex.Message}");
            }
            finally
            {
                ClearCurrentExport(exportPath);
            }
        });

        var errorReportPath = WriteErrorReport(exportPath, exportErrors, logger);

        if (success)
        {
            StatusStripUpdate($"Finished exporting {Path.GetFileName(exportFile)}");
            if (exportOptions.OpenAfterExport)
            {
                OpenFolder(exportPath);
            }
        }
        else
        {
            var status = "Animator export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
            StatusStripUpdate(status);
        }
    }

    private async void ExportAllObjectsSplit_Click(object? sender, RoutedEventArgs e)
    {
        if (sceneTreeNodes.Count == 0)
        {
            StatusStripUpdate("No Objects available for export");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        await ExportSplitObjects(savePath, sceneTreeNodes.SelectMany(x => x.Children).ToList(), null, createObjectFolders: true);
    }

    private async void ExportSelectedObjectsSplit_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsSplit(false);
    }

    private async void ExportSelectedObjectsSplitWithAnimationClip_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsSplit(true);
    }

    private async Task ExportSelectedObjectsSplit(bool includeAnimations)
    {
        var selectedNodes = GetSelectedParentNodes();
        if (selectedNodes.Count == 0)
        {
            StatusStripUpdate("No Object selected for export.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        var exportPath = Path.Combine(savePath, "GameObject");
        Directory.CreateDirectory(exportPath);
        await ExportSplitObjects(exportPath, selectedNodes, includeAnimations ? GetSelectedAnimationClips() : null, createObjectFolders: false);
    }

    private async void ExportSelectedObjectsMerge_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsMerge(false);
    }

    private async void ExportSelectedObjectsMergeWithAnimationClip_Click(object? sender, RoutedEventArgs e)
    {
        await ExportSelectedObjectsMerge(true);
    }

    private async Task ExportSelectedObjectsMerge(bool includeAnimations)
    {
        var gameObjects = GetSelectedParentNodes()
            .Where(x => x.GameObject != null)
            .Select(x => x.GameObject!)
            .ToList();
        if (gameObjects.Count == 0)
        {
            StatusStripUpdate("No Object selected for export.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var suggestedName = FixFileName(gameObjects[0].m_Name) + " (merge).fbx";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(await CreateFbxSaveOptions("Save merged FBX", suggestedName));
        if (file == null) return;

        var exportFile = file.Path.LocalPath;
        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var clips = includeAnimations ? GetSelectedAnimationClips() : null;
        StatusStripUpdate($"Exporting {Path.GetFileName(exportFile)}");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportFolder ?? "", "export-current.txt");
        bool success = false;
        await Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(exportFolder))
                {
                    Directory.CreateDirectory(exportFolder);
                    Directory.CreateDirectory(Path.GetDirectoryName(currentExportPath)!);
                    File.WriteAllText(currentExportPath,
                        $"Exporting merged model{Environment.NewLine}" +
                        $"Name: {Path.GetFileName(exportFile)}{Environment.NewLine}" +
                        $"Objects: {gameObjects.Count}{Environment.NewLine}",
                        Encoding.UTF8);
                }

                IImported convert = gameObjects.Count == 1
                    ? CreateModelConverter(gameObjects[0], clips)
                    : CreateModelConverter(Path.GetFileNameWithoutExtension(exportFile), gameObjects, clips);
                ExportFbx(convert, exportFile);
                success = true;
            }
            catch (Exception ex)
            {
                var error = $"Export Model:{Path.GetFileName(exportFile)} error";
                exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                Logger.Error(error, ex);
                StatusStripUpdate($"Error exporting merged model: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(exportFolder))
                {
                    ClearCurrentExport(exportFolder);
                }
            }
        });

        var reportPath = exportFolder ?? "";
        var errorReportPath = WriteErrorReport(reportPath, exportErrors, logger);

        progressBar.Value = 100;
        if (success)
        {
            StatusStripUpdate($"Finished exporting {Path.GetFileName(exportFile)}");
            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
        }
        else
        {
            var status = "Merged model export failed.";
            if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
            StatusStripUpdate(status);
        }
    }

    private async Task ExportSplitObjects(string exportRoot, List<GameObjectNode> nodes, AnimationClip[]? clips, bool createObjectFolders)
    {
        var exportNodes = nodes
            .Where(node => node.GameObject != null)
            .Where(HasModelContent)
            .ToList();
        if (exportNodes.Count == 0)
        {
            StatusStripUpdate("No Objects available for export");
            return;
        }

        Directory.CreateDirectory(exportRoot);
        StatusStripUpdate($"Exporting {exportNodes.Count} objects...");
        var exportErrors = new List<string>();
        var currentExportPath = Path.Combine(exportRoot, "export-current.txt");
        int exported = 0;
        int failed = 0;

        await Task.Run(() =>
        {
            for (var i = 0; i < exportNodes.Count; i++)
            {
                var node = exportNodes[i];
                var gameObject = node.GameObject!;
                var targetFolder = createObjectFolders
                    ? GetUniqueDirectoryPath(Path.Combine(exportRoot, FixFileName(gameObject.m_Name)))
                    : exportRoot;
                Directory.CreateDirectory(targetFolder);

                var exportFile = GetUniqueFilePath(Path.Combine(targetFolder, FixFileName(gameObject.m_Name) + ".fbx"));
                StatusStripUpdate($"Exporting {Path.GetFileName(exportFile)}");

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(currentExportPath)!);
                    File.WriteAllText(currentExportPath,
                        $"Exporting {i + 1}/{exportNodes.Count}{Environment.NewLine}" +
                        $"Type: GameObject{Environment.NewLine}" +
                        $"Name: {gameObject.m_Name}{Environment.NewLine}" +
                        $"PathID: {gameObject.m_PathID}{Environment.NewLine}" +
                        $"Source: {gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fullName ?? gameObject.assetsFile?.fileName}{Environment.NewLine}",
                        Encoding.UTF8);

                    var convert = CreateModelConverter(gameObject, clips);
                    ExportFbx(convert, exportFile);
                    exported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    var error = $"Export GameObject:{gameObject.m_Name} error";
                    exportErrors.Add($"{error}{Environment.NewLine}{ex}");
                    Logger.Error(error, ex);
                    StatusStripUpdate($"Error exporting {gameObject.m_Name}: {ex.Message}");
                }

                var progress = (int)((i + 1.0) / exportNodes.Count * 100);
                Dispatcher.UIThread.Post(() => progressBar.Value = progress);
            }

            ClearCurrentExport(exportRoot);
        });

        var errorReportPath = WriteErrorReport(exportRoot, exportErrors, logger);

        var status = exported == 0 ? "Nothing exported." : $"Finished exporting {exported} objects.";
        if (failed > 0) status += $" {failed} failed.";
        if (errorReportPath != null) status += $" Error report: {Path.GetFileName(errorReportPath)}.";
        StatusStripUpdate(status);

        if (exportOptions.OpenAfterExport)
        {
            OpenFolder(exportRoot);
        }
    }

    private AnimationClip[]? GetSelectedAnimationClips()
    {
        var clips = GetSelectedAssets()
            .Where(x => x.Type == ClassIDType.AnimationClip)
            .Select(x => (AnimationClip)x.Asset!)
            .ToArray();
        return clips.Length == 0 ? null : clips;
    }

    private ModelConverter CreateModelConverter(string rootName, List<GameObject> gameObjects, AnimationClip[]? clips)
    {
        return clips == null
            ? new ModelConverter(rootName, gameObjects, exportOptions.ConvertTextureFormat)
            : new ModelConverter(rootName, gameObjects, exportOptions.ConvertTextureFormat, clips);
    }

    private ModelConverter CreateModelConverter(GameObject gameObject, AnimationClip[]? clips)
    {
        return clips == null
            ? new ModelConverter(gameObject, exportOptions.ConvertTextureFormat)
            : new ModelConverter(gameObject, exportOptions.ConvertTextureFormat, clips);
    }

    private List<GameObjectNode> GetSelectedParentNodes()
    {
        var nodes = new List<GameObjectNode>();
        foreach (var root in sceneTreeNodes)
        {
            CollectSelectedParentNodes(root, nodes);
        }
        return nodes;
    }

    private static void CollectSelectedParentNodes(GameObjectNode node, List<GameObjectNode> nodes)
    {
        if (node.GameObject != null && node.IsChecked)
        {
            nodes.Add(node);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectSelectedParentNodes(child, nodes);
        }
    }

    private static bool HasModelContent(GameObjectNode node)
    {
        var gameObjects = new List<GameObject>();
        CollectGameObjects(node, gameObjects);
        return gameObjects.Any(x => x.m_SkinnedMeshRenderer != null || x.m_MeshFilter != null);
    }

    private static void CollectGameObjects(GameObjectNode node, List<GameObject> gameObjects)
    {
        if (node.GameObject != null)
        {
            gameObjects.Add(node.GameObject);
        }

        foreach (var child in node.Children)
        {
            CollectGameObjects(child, gameObjects);
        }
    }

    private static string GetUniqueDirectoryPath(string directoryPath)
    {
        var candidate = directoryPath;
        for (var i = 1; Directory.Exists(candidate); i++)
        {
            candidate = $"{directoryPath} ({i})";
        }
        return candidate;
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void LinkFbxSubAssetsToSceneNodes()
    {
        LinkFbxSubAssetsToSceneNodesBackground(exportableAssets, sceneTreeNodes);
    }

    private static GameObjectNode GetFbxRootNode(GameObjectNode node, string fbxContainer)
    {
        var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
        var current = node;
        while (current.Parent?.GameObject != null)
        {
            current = current.Parent;
        }

        var namedRoot = FindNodeByName(current, fbxName);
        return namedRoot ?? current;
    }

    private static GameObjectNode? FindNodeByName(GameObjectNode node, string name)
    {
        if (node.GameObject != null && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindNodeByName(child, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private GameObjectNode? FindSceneNodeByName(string name)
    {
        foreach (var root in sceneTreeNodes)
        {
            var match = FindNodeByName(root, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static List<GameObject> GetTopLevelSelectedGameObjects(List<GameObject> gameObjects)
    {
        return gameObjects
            .Where(gameObject => !gameObjects.Any(other => other != gameObject && IsDescendantOf(gameObject, other)))
            .ToList();
    }

    private static bool IsDescendantOf(GameObject child, GameObject possibleParent)
    {
        var transform = child.m_Transform;
        while (transform != null && transform.m_Father.TryGet(out var father))
        {
            if (father.m_GameObject.TryGet(out var fatherGameObject) && fatherGameObject == possibleParent)
            {
                return true;
            }
            transform = father;
        }
        return false;
    }

    private void ShowOriginalFile(AssetItem item)
    {
        if (item.SourceFile == null)
        {
            StatusStripUpdate("Original file path is unavailable.");
            return;
        }

        var sourcePath = !string.IsNullOrEmpty(item.SourceFile.originalPath)
            ? item.SourceFile.originalPath
            : item.SourceFile.fullName;
        if (string.IsNullOrEmpty(sourcePath))
        {
            StatusStripUpdate("Original file path is unavailable.");
            return;
        }

        var resolvedPath = sourcePath;
        if (!File.Exists(resolvedPath) && item.Handle != null)
        {
            var resolved = ResolveLazyHandleSourcePath(item.Handle);
            if (resolved != null)
            {
                resolvedPath = resolved;
            }
        }

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            StatusStripUpdate($"Original file does not exist on disk: {resolvedPath}");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var cleanPath = resolvedPath.Replace("\"", "");
                if (cleanPath.EndsWith("\\"))
                {
                    cleanPath += ".";
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cleanPath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                startInfo.ArgumentList.Add("-R");
                startInfo.ArgumentList.Add(resolvedPath);
                Process.Start(startInfo);
            }
            else
            {
                var folder = Directory.Exists(resolvedPath) ? resolvedPath : Path.GetDirectoryName(resolvedPath);
                if (string.IsNullOrEmpty(folder)) return;
                var startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                startInfo.ArgumentList.Add(folder);
                Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Unable to show original file: {ex.Message}");
        }
    }

    private string GetExportPath(string savePath, AssetItem asset)
    {
        return exportOptions.AssetGrouping switch
        {
            AssetGroupOption.Container when !string.IsNullOrEmpty(asset.Container) => Path.Combine(savePath, Path.GetDirectoryName(asset.Container) ?? string.Empty),
            AssetGroupOption.SourceFile => Path.Combine(savePath, (asset.SourceFile?.fileName ?? "Unknown") + "_export"),
            AssetGroupOption.TypeName => Path.Combine(savePath, asset.TypeString),
            _ => savePath
        };
    }

    private string GetRawExtension(AssetItem asset)
    {
        if (!exportOptions.RestoreExtensionName) return ".dat";
        return asset.Asset switch
        {
            Texture2D => ".tex",
            TextAsset => ".txt",
            Shader => ".shader",
            Font m_Font when m_Font.m_FontData?.Length >= 4 && m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79 => ".otf",
            Font => ".ttf",
            MovieTexture => ".ogv",
            VideoClip m_VideoClip when !string.IsNullOrEmpty(m_VideoClip.m_OriginalPath) => Path.GetExtension(m_VideoClip.m_OriginalPath),
            AudioClip m_AudioClip => new AudioClipConverter(m_AudioClip).GetExtensionName(),
            _ => ".dat"
        };
    }

    private static void WriteCurrentExport(string path, AssetItem asset, int index, int count)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"Exporting {index}/{count}{Environment.NewLine}" +
                $"Type: {asset.TypeString}{Environment.NewLine}" +
                $"Name: {asset.Name}{Environment.NewLine}" +
                $"PathID: {asset.PathID}{Environment.NewLine}" +
                $"Source: {asset.SourceFile?.originalPath ?? asset.SourceFile?.fullName ?? asset.SourceFile?.fileName}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void ClearCurrentExport(string savePath)
    {
        try
        {
            var path = Path.Combine(savePath, "export-current.txt");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string? WriteErrorReport(string savePath, List<string> exportErrors, GUILogger logger)
    {
        var loadErrors = logger.GetMessages(LoggerEvent.Error);
        if (loadErrors.Length == 0 && exportErrors.Count == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(savePath);
            var errorReportPath = Path.Combine(savePath, "errors.txt");
            using (var writer = new StreamWriter(errorReportPath, false, Encoding.UTF8))
            {
                writer.WriteLine("AssetStudio error report");
                writer.WriteLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine();

                if (loadErrors.Length > 0)
                {
                    writer.WriteLine($"Logged errors ({loadErrors.Length})");
                    writer.WriteLine(new string('=', 80));
                    for (int i = 0; i < loadErrors.Length; i++)
                    {
                        writer.WriteLine($"[{i + 1}]");
                        writer.WriteLine(loadErrors[i]);
                        writer.WriteLine();
                    }
                }

                if (exportErrors.Count > 0)
                {
                    writer.WriteLine($"Export errors ({exportErrors.Count})");
                    writer.WriteLine(new string('=', 80));
                    for (int i = 0; i < exportErrors.Count; i++)
                    {
                        writer.WriteLine($"[{i + 1}]");
                        writer.WriteLine(exportErrors[i]);
                        writer.WriteLine();
                    }
                }
            }
            return errorReportPath;
        }
        catch
        {
            return null;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private bool ExportConvertFile(AssetItem item, string exportPath)
    {
        if (ShouldSkipConvertedAsset(item))
        {
            return false;
        }

        var assetObj = item.Asset;
        if (assetObj == null)
        {
            return false;
        }

        Directory.CreateDirectory(exportPath);
        var fileName = FixFileName(GetExportFileName(item));

        switch (assetObj)
        {
            case Animator m_Animator:
            {
                var exportFullPath = Path.Combine(exportPath, fileName + ".fbx");
                if (File.Exists(exportFullPath))
                {
                    exportFullPath = Path.Combine(exportPath, fileName + item.UniqueID + ".fbx");
                }
                var convert = new ModelConverter(m_Animator, exportOptions.ConvertTextureFormat);
                bool exported = false;
                if (convert.MeshList.Count > 0)
                {
                    ExportFbx(convert, exportFullPath);
                    exported = true;
                }
                if (m_Animator.m_Avatar.TryGet(out var avatar))
                {
                    var avatarFileName = FixFileName(avatar.m_Name);
                    var avatarFullPath = Path.Combine(exportPath, avatarFileName + ".asset");
                    AssetExportHelper.ExportAvatar(avatar, avatarFullPath);
                    exported = true;
                }
                return exported;
            }
            case Avatar m_Avatar:
            {
                var avatarFullPath = Path.Combine(exportPath, fileName + ".asset");
                return AssetExportHelper.ExportAvatar(m_Avatar, avatarFullPath);
            }
            case AnimatorController m_AnimatorController:
            {
                var controllerFullPath = Path.Combine(exportPath, fileName + ".controller");
                return AssetExportHelper.ExportAnimatorController(m_AnimatorController, controllerFullPath);
            }
            case AnimatorOverrideController m_AnimatorOverrideController:
            {
                var overrideFullPath = Path.Combine(exportPath, fileName + ".overrideController");
                return AssetExportHelper.ExportAnimatorOverrideController(m_AnimatorOverrideController, overrideFullPath);
            }
            case AnimationClip m_AnimationClip:
            {
                var bonePathHash = AssetExportHelper.BuildBonePathHash(assetsManager.assetsFileList);
                var morphChannelNames = AssetExportHelper.BuildMorphChannelNames(assetsManager.assetsFileList);
                return AssetExportHelper.ExportAnimationClip(m_AnimationClip, fileName, exportPath, bonePathHash, morphChannelNames);
            }
            case Mesh m_Mesh:
            {
                return ExportMesh(item, m_Mesh, exportPath, fileName);
            }
            case Texture2D m_Texture2D:
            {
                if (!exportOptions.ConvertTexture)
                {
                    var rawPath = Path.Combine(exportPath, fileName + ".tex");
                    if (File.Exists(rawPath)) return false;
                    File.WriteAllBytes(rawPath, m_Texture2D.image_data.GetData());
                    AssetExportHelper.WriteTextureMetaIfMissing(rawPath, m_Texture2D);
                    return true;
                }

                var image = m_Texture2D.ConvertToImage(true);
                if (image == null) return false;
                var extension = "." + exportOptions.ConvertTextureFormat.ToString().ToLowerInvariant();
                var filePath = Path.Combine(exportPath, fileName + extension);
                if (File.Exists(filePath)) return false;
                using (image)
                using (var file = File.OpenWrite(filePath))
                {
                    image.WriteToStream(file, exportOptions.ConvertTextureFormat);
                }
                AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Texture2D);
                return true;
            }
            case AudioClip m_AudioClip:
            {
                var m_AudioData = m_AudioClip.m_AudioData.GetData();
                if (m_AudioData == null || m_AudioData.Length == 0) return false;
                var converter = new AudioClipConverter(m_AudioClip);
                if (exportOptions.ConvertAudio && converter.IsSupport)
                {
                    var filePath = Path.Combine(exportPath, fileName + ".wav");
                    if (File.Exists(filePath)) return false;
                    var buffer = converter.ConvertToWav();
                    if (buffer == null) return false;
                    File.WriteAllBytes(filePath, buffer);
                }
                else
                {
                    var filePath = Path.Combine(exportPath, fileName + converter.GetExtensionName());
                    if (File.Exists(filePath)) return false;
                    File.WriteAllBytes(filePath, m_AudioData);
                }
                return true;
            }
            case Material m_Material:
            {
                return AssetExportHelper.ExportMaterial(m_Material, item.Name, exportPath, exportOptions.ConvertTextureFormat);
            }
            case TextAsset m_TextAsset:
            {
                var filePath = Path.Combine(exportPath, fileName + ".txt");
                if (File.Exists(filePath)) return false;
                File.WriteAllBytes(filePath, m_TextAsset.m_Script);
                return true;
            }
            case MonoScript m_MonoScript:
            {
                var filePath = Path.Combine(exportPath, fileName + ".txt");
                if (File.Exists(filePath)) return false;
                var sb = new StringBuilder();
                sb.AppendLine($"Assembly: {m_MonoScript.m_AssemblyName}");
                sb.AppendLine($"Namespace: {m_MonoScript.m_Namespace}");
                sb.AppendLine($"Class: {m_MonoScript.m_ClassName}");
                File.WriteAllText(filePath, sb.ToString());
                return true;
            }
            case Shader m_Shader:
            {
                var filePath = Path.Combine(exportPath, fileName + ".shader");
                if (File.Exists(filePath)) return false;
                var str = m_Shader.Convert();
                File.WriteAllText(filePath, str);
                return true;
            }
            case Font m_Font:
            {
                if (m_Font.m_FontData == null || m_Font.m_FontData.Length == 0) return false;
                var ext = ".ttf";
                if (m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79)
                    ext = ".otf";
                var filePath = Path.Combine(exportPath, fileName + ext);
                if (File.Exists(filePath)) return false;
                File.WriteAllBytes(filePath, m_Font.m_FontData);
                return true;
            }
            case Sprite m_Sprite:
            {
                var image = m_Sprite.GetImage();
                if (image == null) return false;
                var filePath = Path.Combine(exportPath, fileName + ".png");
                if (File.Exists(filePath)) return false;
                using (image)
                using (var file = File.OpenWrite(filePath))
                {
                    image.WriteToStream(file, ImageFormat.Png);
                }
                AssetExportHelper.WriteTextureMetaIfMissing(filePath, m_Sprite);
                return true;
            }
            case VideoClip m_VideoClip:
            {
                if (m_VideoClip.m_ExternalResources.m_Size <= 0) return false;
                var filePath = Path.Combine(exportPath, fileName + Path.GetExtension(m_VideoClip.m_OriginalPath));
                if (File.Exists(filePath)) return false;
                m_VideoClip.m_VideoData.WriteData(filePath);
                return true;
            }
            case VideoPlayer m_VideoPlayer:
            {
                if (m_VideoPlayer.m_VideoClip.TryGet(out var resolvedClip) && resolvedClip != null)
                {
                    if (resolvedClip.m_ExternalResources.m_Size <= 0) return false;
                    var filePath = Path.Combine(exportPath, fileName + Path.GetExtension(resolvedClip.m_OriginalPath));
                    if (File.Exists(filePath)) return false;
                    resolvedClip.m_VideoData.WriteData(filePath);
                    return true;
                }
                else if (m_VideoPlayer.m_Source == 1 && !string.IsNullOrEmpty(m_VideoPlayer.m_Url))
                {
                    var filePath = Path.Combine(exportPath, fileName + "_url.txt");
                    if (File.Exists(filePath)) return false;
                    File.WriteAllText(filePath, m_VideoPlayer.m_Url);
                    return true;
                }
                return false;
            }
            case MovieTexture m_MovieTexture:
            {
                var filePath = Path.Combine(exportPath, fileName + ".ogv");
                if (File.Exists(filePath)) return false;
                File.WriteAllBytes(filePath, m_MovieTexture.m_MovieData);
                return true;
            }
            case MonoBehaviour m_MonoBehaviour:
            {
                var filePath = Path.Combine(exportPath, fileName + ".json");
                if (File.Exists(filePath)) return false;

                object? obj = m_MonoBehaviour.ToType();
                if (obj == null && assemblyLoader.Loaded)
                {
                    var typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                    if (typeTree != null)
                    {
                        obj = m_MonoBehaviour.ToType(typeTree);
                    }
                }

                if (obj != null)
                {
                    var str = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(filePath, str);
                    return true;
                }

                // Fallback to text asset dump
                var dumpStr = m_MonoBehaviour.Dump();
                if (dumpStr == null && assemblyLoader.Loaded)
                {
                    var typeTree = m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
                    if (typeTree != null)
                    {
                        dumpStr = m_MonoBehaviour.Dump(typeTree);
                    }
                }

                if (dumpStr != null)
                {
                    var dumpPath = Path.Combine(exportPath, fileName + ".txt");
                    if (File.Exists(dumpPath)) return false;
                    File.WriteAllText(dumpPath, dumpStr);
                    return true;
                }

                return false;
            }
            case Object obj when obj.type == ClassIDType.PrefabInstance:
            {
                var filePath = Path.Combine(exportPath, fileName + "_prefab_report.txt");
                if (File.Exists(filePath)) return false;
                var report = FormatPrefab(obj);
                File.WriteAllText(filePath, report);
                return true;
            }
            default:
            {
                var filePath = Path.Combine(exportPath, fileName + ".dat");
                if (File.Exists(filePath)) return false;
                File.WriteAllBytes(filePath, assetObj.GetRawData());
                return true;
            }
        }
    }

    private static bool ShouldSkipConvertedAsset(AssetItem item)
    {
        return (item.IsFbxSubAsset() && (item.Asset is Material || item.Asset is Shader));
    }

    private bool ExportMesh(AssetItem item, Mesh mesh, string exportPath, string fileName)
    {
        if (item.TreeNode?.GameObject != null)
        {
            var fbxPath = Path.Combine(exportPath, fileName + ".fbx");
            if (File.Exists(fbxPath))
            {
                fbxPath = Path.Combine(exportPath, fileName + item.UniqueID + ".fbx");
            }

            var animator = FindAnimatorForModelExport(item);
            var convert = animator != null
                ? new ModelConverter(animator, exportOptions.ConvertTextureFormat)
                : new ModelConverter(item.TreeNode.GameObject, exportOptions.ConvertTextureFormat);
            if (convert.MeshList.Count > 0)
            {
                ExportFbx(convert, fbxPath);
                return true;
            }
        }

        mesh.EnsureProcessed();
        if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
        {
            return false;
        }

        var objPath = Path.Combine(exportPath, fileName + ".obj");
        if (File.Exists(objPath)) return false;

        using var writer = new StreamWriter(objPath, false, Encoding.UTF8);
        writer.WriteLine("g " + mesh.m_Name);

        var componentCount = mesh.m_Vertices.Length == mesh.m_VertexCount * 4 ? 4 : 3;
        for (int v = 0; v < mesh.m_VertexCount; v++)
        {
            writer.WriteLine(
                "v {0} {1} {2}",
                CleanFloat(-mesh.m_Vertices[v * componentCount]),
                CleanFloat(mesh.m_Vertices[v * componentCount + 1]),
                CleanFloat(mesh.m_Vertices[v * componentCount + 2]));
        }

        if (mesh.m_UV0?.Length > 0)
        {
            componentCount = mesh.m_UV0.Length == mesh.m_VertexCount * 2
                ? 2
                : mesh.m_UV0.Length == mesh.m_VertexCount * 3
                    ? 3
                    : 4;
            for (int v = 0; v < mesh.m_VertexCount; v++)
            {
                writer.WriteLine("vt {0} {1}", CleanFloat(mesh.m_UV0[v * componentCount]), CleanFloat(mesh.m_UV0[v * componentCount + 1]));
            }
        }

        if (mesh.m_Normals?.Length > 0)
        {
            componentCount = mesh.m_Normals.Length == mesh.m_VertexCount * 4 ? 4 : 3;
            for (int v = 0; v < mesh.m_VertexCount; v++)
            {
                writer.WriteLine(
                    "vn {0} {1} {2}",
                    CleanFloat(-mesh.m_Normals[v * componentCount]),
                    CleanFloat(mesh.m_Normals[v * componentCount + 1]),
                    CleanFloat(mesh.m_Normals[v * componentCount + 2]));
            }
        }

        var firstFace = 0;
        for (var i = 0; i < mesh.m_SubMeshes.Length; i++)
        {
            writer.WriteLine($"g {mesh.m_Name}_{i}");
            var faceCount = (int)mesh.m_SubMeshes[i].indexCount / 3;
            var end = firstFace + faceCount;
            for (int f = firstFace; f < end; f++)
            {
                writer.WriteLine(
                    "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}",
                    mesh.m_Indices[f * 3 + 2] + 1,
                    mesh.m_Indices[f * 3 + 1] + 1,
                    mesh.m_Indices[f * 3] + 1);
            }
            firstFace = end;
        }

        return true;
    }

    private Animator? FindAnimatorForModelExport(AssetItem item)
    {
        for (var node = item.TreeNode; node != null; node = node.Parent)
        {
            if (node.GameObject?.m_Animator != null)
            {
                return node.GameObject.m_Animator;
            }
        }

        return item.TreeNode != null ? FindAnimatorInSceneNode(item.TreeNode) : null;
    }

    private static Animator? FindAnimatorInSceneNode(GameObjectNode node)
    {
        if (node.GameObject?.m_Animator != null)
        {
            return node.GameObject.m_Animator;
        }

        foreach (var child in node.Children)
        {
            var animator = FindAnimatorInSceneNode(child);
            if (animator != null)
            {
                return animator;
            }
        }

        return null;
    }

    private static string GetExportFileName(AssetItem item)
    {
        var fbxContainer = GetFbxContainerPath(item.Container);
        if (fbxContainer != null && item.Asset is Mesh or Animator)
        {
            var name = Path.GetFileNameWithoutExtension(fbxContainer);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return item.Name;
    }

    private static string? GetFbxContainerPath(string container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }

        var parts = container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            path = string.IsNullOrEmpty(path) ? part : Path.Combine(path, part);
            if (string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    private void ExportFbx(IImported convert, string exportFile)
    {
        if (exportOptions.ExportAnimations && exportOptions.ExportAnimationsSplit && convert.AnimationList?.Count > 0)
        {
            // 1. Export main model without animations
            var mainConvert = new ImportedWrapper(convert)
            {
                AnimationList = new List<ImportedKeyframedAnimation>()
            };
            ModelExporter.ExportFbx(exportFile, mainConvert,
                exportOptions.EulerFilter,
                (float)exportOptions.FilterPrecision,
                exportOptions.ExportAllNodes,
                exportOptions.ExportSkins,
                false, // Disable animation for main export
                exportOptions.ExportBlendShape,
                exportOptions.CastToBone,
                (float)exportOptions.BoneSize,
                exportOptions.ExportAllUvsAsDiffuseMaps,
                (float)exportOptions.ScaleFactor,
                exportOptions.FbxVersion,
                exportOptions.FbxFormat == 1);

            // 2. Export each animation clip separately
            foreach (var anim in convert.AnimationList)
            {
                var animFile = Path.Combine(Path.GetDirectoryName(exportFile)!, $"{Path.GetFileNameWithoutExtension(exportFile)}_{FixFileName(anim.Name)}.fbx");
                var animConvert = new ImportedWrapper(convert)
                {
                    AnimationList = new List<ImportedKeyframedAnimation> { anim }
                };
                ModelExporter.ExportFbx(animFile, animConvert,
                    exportOptions.EulerFilter,
                    (float)exportOptions.FilterPrecision,
                    exportOptions.ExportAllNodes,
                    exportOptions.ExportSkins,
                    true,
                    exportOptions.ExportBlendShape,
                    exportOptions.CastToBone,
                    (float)exportOptions.BoneSize,
                    exportOptions.ExportAllUvsAsDiffuseMaps,
                    (float)exportOptions.ScaleFactor,
                    exportOptions.FbxVersion,
                    exportOptions.FbxFormat == 1);
            }
        }
        else
        {
            ModelExporter.ExportFbx(exportFile, convert,
                exportOptions.EulerFilter,
                (float)exportOptions.FilterPrecision,
                exportOptions.ExportAllNodes,
                exportOptions.ExportSkins,
                exportOptions.ExportAnimations,
                exportOptions.ExportBlendShape,
                exportOptions.CastToBone,
                (float)exportOptions.BoneSize,
                exportOptions.ExportAllUvsAsDiffuseMaps,
                (float)exportOptions.ScaleFactor,
                exportOptions.FbxVersion,
                exportOptions.FbxFormat == 1);
        }
    }

    private static string CleanFloat(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? "0"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FixFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unnamed";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private void ShowClassInstances_Click(object? sender, RoutedEventArgs e)
    {
        if (AssetClassesDataGrid.SelectedItem is not AssetClassItem item)
        {
            return;
        }

        PrioritizeUserInteraction();
        classFilterOverride = item;
        ClearClassFilterButton.Content = $"Clear Class Filter ({item.Name} v{item.UnityVersion})";
        ClearClassFilterButton.IsVisible = true;

        LeftTabControl.SelectedIndex = 1;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void ClearClassFilter_Click(object? sender, RoutedEventArgs e)
    {
        PrioritizeUserInteraction();
        classFilterOverride = null;
        ClearClassFilterButton.IsVisible = false;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private Object? ResolvePPtr(object? pptrObj, SerializedFile file)
    {
        if (pptrObj is System.Collections.Specialized.OrderedDictionary dict)
        {
            if (dict.Contains("m_FileID") && dict.Contains("m_PathID"))
            {
                var fileIDObj = dict["m_FileID"];
                var pathIDObj = dict["m_PathID"];
                if (fileIDObj != null && pathIDObj != null)
                {
                    int fileID = Convert.ToInt32(fileIDObj);
                    long pathID = Convert.ToInt64(pathIDObj);
                    if (pathID != 0)
                    {
                        var pptr = new PPtr<Object>(fileID, pathID, file);
                        if (pptr.TryGet(out var target))
                        {
                            return target;
                        }
                    }
                }
            }
        }
        return null;
    }

    private void FindAllPPtrs(object? obj, List<System.Collections.Specialized.OrderedDictionary> pptrs)
    {
        if (obj == null) return;
        if (obj is System.Collections.Specialized.OrderedDictionary dict)
        {
            if (dict.Contains("m_FileID") && dict.Contains("m_PathID"))
            {
                pptrs.Add(dict);
            }
            else
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    FindAllPPtrs(entry.Value, pptrs);
                }
            }
        }
        else if (obj is System.Collections.IEnumerable list && !(obj is string))
        {
            foreach (var item in list)
            {
                FindAllPPtrs(item, pptrs);
            }
        }
    }

    private void TraverseGameObject(GameObject go, List<GameObject> gameObjects, List<Component> components)
    {
        if (go == null || gameObjects.Contains(go)) return;
        gameObjects.Add(go);

        if (go.m_Components != null)
        {
            foreach (var pptrComp in go.m_Components)
            {
                if (pptrComp.TryGet(out var comp))
                {
                    components.Add(comp);
                    if (comp is Transform t)
                    {
                        if (t.m_Children != null)
                        {
                            foreach (var childPtr in t.m_Children)
                            {
                                if (childPtr.TryGet(out var childTransform))
                                {
                                    if (childTransform.m_GameObject.TryGet(out var childGo))
                                    {
                                        TraverseGameObject(childGo, gameObjects, components);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private string FormatPrefab(Object prefab)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Prefab Instance Asset: {prefab.assetsFile.fileName} (PathID: {prefab.m_PathID})");
        sb.AppendLine("NOTE: This is a Composite/Referential Asset (Prefab).");
        sb.AppendLine("It is a logical layout composing GameObjects, Components, and PPtr references.");
        sb.AppendLine("It is not a raw geometry mesh. Its sub-assets (Meshes, Materials, etc.) are");
        sb.AppendLine("represented by their individual items in the hierarchy and asset lists.");
        sb.AppendLine("==================================================");

        Object? rootGameObject = null;
        Object? sourcePrefab = null;
        var dict = prefab.ToType();
        if (dict != null)
        {
            if (dict.Contains("m_RootGameObject"))
            {
                rootGameObject = ResolvePPtr(dict["m_RootGameObject"], prefab.assetsFile);
            }
            if (dict.Contains("m_SourcePrefab"))
            {
                sourcePrefab = ResolvePPtr(dict["m_SourcePrefab"], prefab.assetsFile);
            }
        }

        if (rootGameObject != null)
        {
            sb.AppendLine($"Root GameObject: {((GameObject)rootGameObject).m_Name} (PathID: {rootGameObject.m_PathID})");
        }
        else
        {
            sb.AppendLine("Root GameObject: [Not Resolved]");
        }

        if (sourcePrefab != null)
        {
            sb.AppendLine($"Source Prefab: {sourcePrefab.m_PathID} (Type: {sourcePrefab.type})");
        }

        sb.AppendLine();

        var gameObjects = new List<GameObject>();
        var components = new List<Component>();

        if (rootGameObject is GameObject rootGo)
        {
            TraverseGameObject(rootGo, gameObjects, components);
        }

        sb.AppendLine($"GameObjects in Hierarchy ({gameObjects.Count}):");
        foreach (var go in gameObjects)
        {
            sb.AppendLine($"  - Name: \"{go.m_Name}\" (PathID: {go.m_PathID})");
        }
        sb.AppendLine();

        sb.AppendLine($"Components attached to GameObjects ({components.Count}):");
        foreach (var comp in components)
        {
            var goName = "";
            if (comp.m_GameObject.TryGet(out var compGo))
            {
                goName = $" on GameObject \"{compGo.m_Name}\"";
            }
            sb.AppendLine($"  - Type: {comp.type} (PathID: {comp.m_PathID}){goName}");
        }
        sb.AppendLine();

        var allPPtrDicts = new List<System.Collections.Specialized.OrderedDictionary>();
        FindAllPPtrs(dict, allPPtrDicts);

        var resolvedObjects = new List<Object>();
        var unresolvedPPtrs = new List<string>();
        foreach (var pptrDict in allPPtrDicts)
        {
            var resolved = ResolvePPtr(pptrDict, prefab.assetsFile);
            if (resolved != null)
            {
                if (!gameObjects.Contains(resolved) && !components.Contains(resolved) && resolved != prefab)
                {
                    resolvedObjects.Add(resolved);
                }
            }
            else
            {
                var fileID = pptrDict["m_FileID"];
                var pathID = pptrDict["m_PathID"];
                if (Convert.ToInt64(pathID) != 0)
                {
                    unresolvedPPtrs.Add($"FileID: {fileID}, PathID: {pathID}");
                }
            }
        }

        if (resolvedObjects.Count > 0)
        {
            sb.AppendLine($"Other Resolved Referenced Assets ({resolvedObjects.Count}):");
            foreach (var resObj in resolvedObjects.Distinct())
            {
                sb.AppendLine($"  - Type: {resObj.type} (PathID: {resObj.m_PathID})");
            }
            sb.AppendLine();
        }

        if (unresolvedPPtrs.Count > 0)
        {
            sb.AppendLine($"Unresolved PPtr References ({unresolvedPPtrs.Count}):");
            foreach (var unres in unresolvedPPtrs.Distinct())
            {
                sb.AppendLine($"  - {unres}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void BuildAssetReferenceIndexes()
    {
        BuildAssetReferenceIndexesBackground(
            assetsManager.assetsFileList,
            exportableAssets,
            out var localObjectToAssetItemCache,
            out var localMeshToMaterialsCache,
            out var localMeshAssociatedRenderersCache,
            out var localMeshSourceTypesCache,
            out var localMaterialMainTextureCache,
            out var localMaterialPreviewMaterialCache,
            out var localMaterialTextureSlotsCache,
            out var semanticRelations);

        objectToAssetItemCache = localObjectToAssetItemCache;
        meshToMaterialsCache = localMeshToMaterialsCache;
        meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
        meshSourceTypesCache = localMeshSourceTypesCache;
        materialMainTextureCache = localMaterialMainTextureCache;
        materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
        materialTextureSlotsCache = localMaterialTextureSlotsCache;
        if (!assetsManager.LazyLoading)
        {
            _ = Task.Run(() => TrySaveSemanticRelations(semanticRelations));
        }

        BuildAnimationPreviewIndexesBackground(
            assetsManager.assetsFileList,
            out var localAnimationClipAvatarCache,
            out var localAvatarMeshCache,
            out var localMeshAvatarCache,
            out var localAnimationClipTransformBindingsCache);

        animationClipAvatarCache = localAnimationClipAvatarCache;
        avatarMeshCache = localAvatarMeshCache;
        avatarMeshesCache = BuildAvatarMeshListCache(localAvatarMeshCache);
        meshAvatarCache = localMeshAvatarCache;
        animationClipTransformBindingsCache = localAnimationClipTransformBindingsCache;
    }

    private static int ScoreMaterials(List<Material?> mats)
    {
        return ScoreMaterialsStatic(mats);
    }



    private List<Material?> FindMaterialsForMesh(Mesh mesh)
    {
        if (assetsManager.LazyLoading)
        {
            return FindMaterialsForMeshPreview(mesh);
        }

        if (meshToMaterialsCache == null)
        {
            var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
            if (semanticMaterials.Count > 0)
            {
                return semanticMaterials;
            }

            BuildAssetReferenceIndexes();
        }

        if (meshToMaterialsCache!.TryGetValue(mesh, out var cachedList))
        {
            return new List<Material?>(cachedList);
        }

        return new List<Material?>();
    }

    private List<Material?> TryLoadMaterialsForMeshFromSemanticCache(Mesh mesh)
    {
        var materials = new List<Material?>();
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return materials;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return materials;
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var materialIds = _sqliteCache.LoadMeshMaterialAssetIds(folderPath, signature, meshAssetId);
        if (materialIds.Count == 0)
        {
            return materials;
        }

        foreach (var materialId in materialIds)
        {
            if (string.IsNullOrWhiteSpace(materialId))
            {
                materials.Add(null);
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(materialId);
            if (handle == null)
            {
                materials.Add(null);
                continue;
            }

            var asset = ResolveSemanticRelationHandleForPreview(handle);
            if (asset is Material material)
            {
                materials.Add(material);
            }
            else
            {
                materials.Add(null);
            }
        }

        if (materials.Any(material => material != null))
        {
            meshToMaterialsCache ??= new Dictionary<Mesh, List<Material?>>();
            meshToMaterialsCache[mesh] = materials;
            return materials;
        }

        return new List<Material?>();
    }

    private static HashSet<string> GetPathTokens(string path)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = path.Split(new[] { '/', '\\', '_', '.', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length > 2 && 
                !string.Equals(part, "fbx", StringComparison.OrdinalIgnoreCase) && 
                !string.Equals(part, "mat", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "assets", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "models", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "materials", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(part);
            }
        }
        return tokens;
    }

    private static global::OpenTK.Mathematics.Vector2[]? BuildMeshPreviewUvs(Mesh mesh)
    {
        var componentCount = GetMeshPreviewUvComponentCount(mesh.m_UV0, mesh.m_VertexCount);
        if (componentCount < 2)
        {
            return null;
        }

        var uvs = new global::OpenTK.Mathematics.Vector2[mesh.m_VertexCount];
        for (int i = 0; i < mesh.m_VertexCount; i++)
        {
            var offset = i * componentCount;
            uvs[i] = new global::OpenTK.Mathematics.Vector2(mesh.m_UV0[offset], mesh.m_UV0[offset + 1]);
        }

        return uvs;
    }

    private static int GetMeshPreviewUvComponentCount(float[]? uv, int vertexCount)
    {
        if (uv == null || vertexCount <= 0 || uv.Length < vertexCount * 2 || uv.Length % vertexCount != 0)
        {
            return 0;
        }

        var componentCount = uv.Length / vertexCount;
        return componentCount is >= 2 and <= 4 ? componentCount : 0;
    }

    private string FormatMeshPreviewSummary(Mesh mesh, AssetItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mesh Asset: {mesh.m_Name} (PathID: {mesh.m_PathID})");
        sb.AppendLine("==================================================");
        sb.AppendLine($"Vertex Count: {mesh.m_VertexCount}");
        sb.AppendLine($"Submesh Count: {mesh.m_SubMeshes?.Length ?? 0}");
        sb.AppendLine($"Index Count: {mesh.m_Indices?.Count ?? 0}");

        bool isFbx = item.Container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"From FBX Container: {(isFbx ? "Yes" : "No")}");
        if (isFbx)
        {
            sb.AppendLine($"FBX Path: {item.Container}");
        }

        return sb.ToString();
    }

    private string FormatMeshPreview(Mesh mesh, AssetItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mesh Asset: {mesh.m_Name} (PathID: {mesh.m_PathID})");
        sb.AppendLine("==================================================");
        sb.AppendLine($"Vertex Count: {mesh.m_VertexCount}");
        sb.AppendLine($"Submesh Count: {mesh.m_SubMeshes?.Length ?? 0}");
        sb.AppendLine($"Index Count: {mesh.m_Indices?.Count ?? 0}");

        bool isFbx = item.Container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"From FBX Container: {(isFbx ? "Yes" : "No")}");
        if (isFbx)
        {
            sb.AppendLine($"FBX Path: {item.Container}");
        }

        if (meshToMaterialsCache == null || meshAssociatedRenderersCache == null || meshSourceTypesCache == null)
        {
            BuildAssetReferenceIndexes();
        }

        meshSourceTypesCache!.TryGetValue(mesh, out var cachedSourceTypes);
        var sourceTypes = cachedSourceTypes?.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        sb.AppendLine($"Referenced By: {(sourceTypes.Count > 0 ? string.Join(", ", sourceTypes) : "None (Orphaned Mesh)")}");

        var materials = FindMaterialsForMesh(mesh);
        sb.AppendLine($"Associated Materials ({materials.Count}):");
        foreach (var mat in materials)
        {
            if (mat != null)
            {
                sb.AppendLine($"  - {mat.m_Name} (PathID: {mat.m_PathID})");
            }
        }

        meshAssociatedRenderersCache!.TryGetValue(mesh, out var associatedRenderers);
        associatedRenderers ??= new List<string>();
        if (associatedRenderers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Associated Renderers / Filters:");
            foreach (var ar in associatedRenderers.Distinct())
            {
                sb.AppendLine($"  - {ar}");
            }
        }

        sb.AppendLine();
        var dump = mesh.Dump();
        if (dump != null)
        {
            sb.AppendLine("Mesh Serialization Structure:");
            if (dump.Length > 2000)
            {
                sb.AppendLine(dump.Substring(0, 2000));
                sb.AppendLine("...");
                sb.AppendLine("[Dump truncated: too large for side overlay. View full dump in the 'Dump' tab.]");
            }
            else
            {
                sb.AppendLine(dump);
            }
        }

        return sb.ToString();
    }

    private string FormatLazyMeshPreview(Mesh mesh, AssetItem item, List<Material?> materials)
    {
        var sb = new StringBuilder();
        sb.Append(FormatMeshPreviewSummary(mesh, item));

        var folderPath = GetCurrentCacheFolderPath();
        var connectionsReady = CanUseLazySemanticRelationCache(folderPath);
        sb.AppendLine($"Connections: {(connectionsReady ? "Complete" : "Not built yet")}");

        if (connectionsReady)
        {
            sb.AppendLine($"Associated Materials ({materials.Count}):");
            foreach (var mat in materials)
            {
                if (mat != null)
                {
                    sb.AppendLine($"  - {mat.m_Name} (PathID: {mat.m_PathID})");
                }
            }
        }
        else
        {
            sb.AppendLine("Associated Materials: waiting for final connections build.");
        }

        return sb.ToString();
    }

    private void ShowErrorMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            logger.ShowErrorMessage = menuItem.IsChecked;
            appSettings.ShowErrorMessage = menuItem.IsChecked;
            SaveAppSettings();
        }
    }

    private async void ExportClassStructures_Click(object? sender, RoutedEventArgs e)
    {
        if (assetClassItems.Count > 0)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select folder to export class structures"));
            if (folders != null && folders.Count > 0)
            {
                var savePath = folders[0].Path.LocalPath;
                var count = assetClassItems.Count;
                int i = 0;
                Progress.Reset();
                foreach (var item in assetClassItems)
                {
                    var versionPath = Path.Combine(savePath, item.UnityVersion);
                    Directory.CreateDirectory(versionPath);

                    var cleanClassName = FixFileName(item.Name);
                    var saveFile = Path.Combine(versionPath, $"{item.ClassID} {cleanClassName}.txt");
                    File.WriteAllText(saveFile, FormatAssetClass(item));

                    Progress.Report(++i, count);
                }

                StatusStripUpdate("Finished exporting class structures");
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        AssetsManager.ShouldYieldForUserInteraction = null;
        FlushAvatarPreviewSettingsSave();
        AudioReset(recreateAudioPlayer: false);
        VideoReset();
        _pcmWavePreviewPlayer?.Dispose();
        assetsManager.Dispose();
        base.OnClosing(e);
    }

    private static string FormatMediaTime(long currentMs, long totalMs)
    {
        return $"{currentMs / 1000 / 60}:{currentMs / 1000 % 60:D2}.{currentMs / 10 % 100:D2} / {totalMs / 1000 / 60}:{totalMs / 1000 % 60:D2}.{totalMs / 10 % 100:D2}";
    }

    private static string GetPreviewAssetId(AssetStudio.Object asset)
    {
        var source = asset.assetsFile?.fullName ?? asset.assetsFile?.fileName ?? string.Empty;
        return string.Concat(source, "\u001f", asset.m_PathID.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void SetInitialVideoAudioLabel(VideoClip videoClip)
    {
        if (VideoAudioLabel == null)
        {
            return;
        }

        if (videoClip.HasAudio)
        {
            var sb = new StringBuilder("Audio: yes");
            if (videoClip.m_AudioChannelCount != null)
            {
                for (int i = 0; i < videoClip.m_AudioChannelCount.Length; i++)
                {
                    var ch = videoClip.m_AudioChannelCount[i];
                    var rate = videoClip.m_AudioSampleRate != null && videoClip.m_AudioSampleRate.Length > i ? videoClip.m_AudioSampleRate[i] : 0;
                    sb.Append($" | Track {i + 1}: {ch}ch");
                    if (rate > 0)
                    {
                        sb.Append($" {rate}Hz");
                    }
                }
            }
            sb.Append(" | Unity metadata");
            VideoAudioLabel.Text = sb.ToString();
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#00e676"));
        }
        else
        {
            VideoAudioLabel.Text = "Audio: no playable track found";
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#ff9800"));
        }
    }

    private void VideoStop()
    {
        try
        {
            _ffmpegVideoTimer?.Stop();
            FfmpegVideoPlayer.Stop();
        }
        catch {}

        SetVideoStoppedUi();
    }

    private async Task VideoStopAsync()
    {
        try
        {
            _ffmpegVideoTimer?.Stop();
            await Task.Run(() => FfmpegVideoPlayer.Stop());
        }
        catch {}

        SetVideoStoppedUi();
    }

    private void CancelVideoPreviewLoad()
    {
        try
        {
            _videoPreviewLoadCts?.Cancel();
        }
        catch {}
    }

    private void VideoReset()
    {
        CancelVideoPreviewLoad();
        try
        {
            _ffmpegVideoTimer?.Stop();
            FfmpegVideoPlayer.Stop();
        }
        catch {}

        if (!string.IsNullOrEmpty(_currentTempVideoPath) && File.Exists(_currentTempVideoPath))
        {
            try
            {
                File.Delete(_currentTempVideoPath);
            }
            catch {}
            _currentTempVideoPath = null;
        }

        _currentTempVideoAssetId = null;
        SetVideoStoppedUi();
    }

    private async Task VideoResetAsync()
    {
        CancelVideoPreviewLoad();
        try
        {
            _ffmpegVideoTimer?.Stop();
            await Task.Run(() => FfmpegVideoPlayer.Stop());
        }
        catch {}

        if (!string.IsNullOrEmpty(_currentTempVideoPath))
        {
            var pathToDelete = _currentTempVideoPath;
            _currentTempVideoPath = null;
            await Task.Run(() =>
            {
                try
                {
                    if (File.Exists(pathToDelete))
                    {
                        File.Delete(pathToDelete);
                    }
                }
                catch {}
            });
        }

        _currentTempVideoAssetId = null;
        SetVideoStoppedUi();
    }

    private void SetVideoStoppedUi()
    {
        VideoStatusLabel.Text = "Stopped";
        VideoPlayButton.Content = "Play";
        VideoProgressBar.Value = 0;
        VideoTimerLabel.Text = "0:00.0 / 0:00.0";
    }

    private async void PreviewVideoClip(AssetItem assetItem, VideoClip m_VideoClip)
    {
        var videoAssetId = GetPreviewAssetId(m_VideoClip);
        if (_currentTempVideoAssetId == videoAssetId)
        {
            await VideoStopAsync();
        }
        else
        {
            await VideoResetAsync();
        }

        currentPreviewVideoClip = m_VideoClip;

        VideoTitleLabel.Text = m_VideoClip.m_Name;
        VideoStatusLabel.Text = "Ready";
        VideoResolutionLabel.Text = $"Resolution: {m_VideoClip.m_Width}x{m_VideoClip.m_Height} (Proxy: {m_VideoClip.m_ProxyWidth}x{m_VideoClip.m_ProxyHeight})";
        VideoFrameRateLabel.Text = $"Frame Rate: {m_VideoClip.m_FrameRate:F2} FPS | Frames: {m_VideoClip.m_FrameCount}";
        
        var ext = Path.GetExtension(m_VideoClip.m_OriginalPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        VideoFormatLabel.Text = $"Format: {ext.ToUpperInvariant().TrimStart('.')}";

        SetInitialVideoAudioLabel(m_VideoClip);

        VideoInfoLabel.Text = "Ready. Press Play to prepare embedded preview.";
        VideoPlayButton.IsEnabled = true;
        VideoStopButton.IsEnabled = true;
        VideoExportButton.IsEnabled = true;
        VideoVolumeBar.Value = 80;

        VideoClipPanel.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StartVideoThumbnailPreview(m_VideoClip);
        StatusStripUpdate($"Loaded video clip: {m_VideoClip.m_Name}");
    }

    private void PreviewVideoPlayer(AssetItem assetItem, VideoPlayer m_VideoPlayer)
    {
        if (m_VideoPlayer.m_VideoClip.TryGet(out var m_VideoClip) && m_VideoClip != null)
        {
            StatusStripUpdate($"VideoPlayer references VideoClip: {m_VideoClip.m_Name}");
            PreviewVideoClip(assetItem, m_VideoClip);
            string vpName = m_VideoPlayer.m_GameObject.TryGet(out var go) ? go.m_Name : "VideoPlayer";
            VideoTitleLabel.Text = $"{vpName} (VideoPlayer)";
            
            var sb = new StringBuilder();
            sb.AppendLine(m_VideoPlayer.Dump());
            sb.AppendLine("Ready. Press Play to load embedded native preview.");
            VideoInfoLabel.Text = sb.ToString();
        }
        else if (m_VideoPlayer.m_Source == 1 && !string.IsNullOrEmpty(m_VideoPlayer.m_Url))
        {
            StatusStripUpdate($"VideoPlayer references URL: {m_VideoPlayer.m_Url}");
            SetTextWithTruncation(TextPreviewBox, m_VideoPlayer.Dump());
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
        }
        else
        {
            StatusStripUpdate("VideoPlayer has no loaded VideoClip or URL.");
            SetTextWithTruncation(TextPreviewBox, m_VideoPlayer.Dump());
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
        }
    }

    private async void VideoPlayButton_Click(object? sender, RoutedEventArgs e)
    {
        var videoClip = currentPreviewVideoClip;
        if (videoClip == null) return;

        try
        {
            if (FfmpegVideoPlayer.IsPlaying)
            {
                FfmpegVideoPlayer.Pause();
                _ffmpegVideoTimer?.Stop();
                VideoStatusLabel.Text = "Paused";
                VideoPlayButton.Content = "Play";
            }
            else
            {
                var videoAssetId = GetPreviewAssetId(videoClip);
                var hasLoadedFile = FfmpegVideoPlayer.HasMediaLoaded
                    && _currentTempVideoAssetId == videoAssetId
                    && !string.IsNullOrEmpty(_currentTempVideoPath)
                    && File.Exists(_currentTempVideoPath);

                if (!hasLoadedFile)
                {
                    if (!await EnsureVideoPreviewFileAsync(videoClip))
                    {
                        return;
                    }

                    if (!ReferenceEquals(currentPreviewVideoClip, videoClip))
                    {
                        return;
                    }

                    var path = _currentTempVideoPath!;
                    bool opened = await Task.Run(() =>
                    {
                        try
                        {
                            FfmpegVideoPlayer.Open(path);
                            return FfmpegVideoPlayer.HasMediaLoaded;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!ReferenceEquals(currentPreviewVideoClip, videoClip))
                    {
                        return;
                    }

                    if (!opened)
                    {
                        VideoStatusLabel.Text = "Unsupported";
                        VideoPlayButton.Content = "Play";
                        VideoInfoLabel.Text = "FFmpeg could not open this VideoClip.";
                        StatusStripUpdate("Video preview could not open this VideoClip.");
                        return;
                    }
                }

                FfmpegVideoPlayer.Volume = _targetVolume;
                if (hasLoadedFile && VideoStatusLabel.Text == "Stopped")
                {
                    FfmpegVideoPlayer.Seek(0f);
                }
                FfmpegVideoPlayer.Play();
                _ffmpegVideoTimer?.Start();
                VideoStatusLabel.Text = "Playing";
                VideoPlayButton.Content = "Pause";
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to toggle FFmpeg playback: {ex.Message}");
        }
    }

    private async Task<bool> EnsureVideoPreviewFileAsync(VideoClip videoClip)
    {
        var assetId = GetPreviewAssetId(videoClip);
        if (_currentTempVideoAssetId == assetId
            && !string.IsNullOrEmpty(_currentTempVideoPath)
            && File.Exists(_currentTempVideoPath))
        {
            return true;
        }

        _videoPreviewLoadCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _videoPreviewLoadCts = loadCts;
        var token = loadCts.Token;

        try
        {
            VideoStatusLabel.Text = "Loading";
            VideoPlayButton.IsEnabled = false;
            VideoStopButton.IsEnabled = false;
            VideoInfoLabel.Text = "Preparing embedded preview...";
            StatusStripUpdate($"Preparing video preview: {videoClip.m_Name}");

            var tempPath = await Task.Run(() => PrepareVideoPreviewFile(videoClip, token), token);
            token.ThrowIfCancellationRequested();

            if (!ReferenceEquals(currentPreviewVideoClip, videoClip))
            {
                return false;
            }

            if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
            {
                VideoInfoLabel.Text = "VideoClip data is empty or invalid.";
                VideoStatusLabel.Text = "Missing";
                VideoPlayButton.Content = "Play";
                StatusStripUpdate("VideoClip data is empty or invalid.");
                return false;
            }

            _currentTempVideoPath = tempPath;
            _currentTempVideoAssetId = assetId;
            _videoLengthMs = 0;
            VideoInfoLabel.Text = "Embedded FFmpeg preview loaded.";
            StatusStripUpdate($"Loaded video clip with FFmpeg backend: {videoClip.m_Name}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            VideoInfoLabel.Text = $"Failed to prepare preview: {ex.Message}";
            VideoStatusLabel.Text = "Error";
            StatusStripUpdate($"Failed to prepare video preview: {ex.Message}");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_videoPreviewLoadCts, loadCts))
            {
                _videoPreviewLoadCts = null;
            }
            loadCts.Dispose();
            if (ReferenceEquals(currentPreviewVideoClip, videoClip))
            {
                VideoPlayButton.IsEnabled = true;
                VideoStopButton.IsEnabled = true;
                if (VideoStatusLabel.Text == "Loading")
                {
                    VideoStatusLabel.Text = "Ready";
                }
            }
        }
    }

    private string? PrepareVideoPreviewFile(VideoClip videoClip, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (videoClip.m_ExternalResources.m_Size <= 0)
        {
            return null;
        }

        var ext = Path.GetExtension(videoClip.m_OriginalPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        Directory.CreateDirectory(tempDir);

        token.ThrowIfCancellationRequested();
        var tempPath = Path.Combine(tempDir, $"temp_video_{FixFileName(videoClip.m_Name)}_{videoClip.m_PathID}{ext}");
        videoClip.m_VideoData.WriteData(tempPath);

        token.ThrowIfCancellationRequested();
        return File.Exists(tempPath) && new FileInfo(tempPath).Length > 0 ? tempPath : null;
    }

    private async void StartVideoThumbnailPreview(VideoClip videoClip)
    {
        try
        {
            await Task.Delay(120);
            if (!ReferenceEquals(currentPreviewVideoClip, videoClip))
            {
                return;
            }

            if (!await EnsureVideoPreviewFileAsync(videoClip))
            {
                return;
            }

            if (!ReferenceEquals(currentPreviewVideoClip, videoClip)
                || string.IsNullOrEmpty(_currentTempVideoPath)
                || !File.Exists(_currentTempVideoPath))
            {
                return;
            }

            var path = _currentTempVideoPath;
            bool opened = await Task.Run(() =>
            {
                try
                {
                    FfmpegVideoPlayer.Open(path);
                    return FfmpegVideoPlayer.HasMediaLoaded;
                }
                catch
                {
                    return false;
                }
            });

            if (!ReferenceEquals(currentPreviewVideoClip, videoClip))
            {
                return;
            }

            if (opened)
            {
                VideoStatusLabel.Text = "Ready";
                VideoPlayButton.Content = "Play";
                VideoInfoLabel.Text = "Thumbnail ready. Press Play to start preview.";
                VideoProgressBar.Value = 0;
                VideoTimerLabel.Text = FormatMediaTime(0, Math.Max(0, FfmpegVideoPlayer.Duration));
            }
            else
            {
                VideoStatusLabel.Text = "Unsupported";
                VideoInfoLabel.Text = "FFmpeg could not open this VideoClip.";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(currentPreviewVideoClip, videoClip))
            {
                VideoStatusLabel.Text = "Error";
                VideoInfoLabel.Text = $"Failed to load thumbnail: {ex.Message}";
            }
        }
    }

    private async void VideoStopButton_Click(object? sender, RoutedEventArgs e)
    {
        await VideoStopAsync();
    }

    private void VideoVolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _targetVolume = (int)VideoVolumeBar.Value;
        FfmpegVideoPlayer.Volume = _targetVolume;
    }

    private void FfmpegVideoTimer_Tick(object? sender, EventArgs e)
    {
        if (_isVideoDragging)
        {
            return;
        }

        try
        {
            var duration = Math.Max(0, FfmpegVideoPlayer.Duration);
            var position = Math.Max(0, FfmpegVideoPlayer.Position);
            _videoLengthMs = duration;

            if (duration > 0)
            {
                _isUpdatingVideoProgress = true;
                VideoProgressBar.Value = Math.Clamp(position * 1000.0 / duration, 0, 1000);
                _isUpdatingVideoProgress = false;
            }

            VideoTimerLabel.Text = FormatMediaTime(position, duration);

            if (!FfmpegVideoPlayer.IsPlaying && VideoStatusLabel.Text == "Playing")
            {
                VideoStatusLabel.Text = "Paused";
                VideoPlayButton.Content = "Play";
                _ffmpegVideoTimer?.Stop();
            }
        }
        catch
        {
        }
    }

    private void FfmpegVideoPlayer_MediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _ffmpegVideoTimer?.Stop();

                if (VideoLoopButton.IsChecked == true
                    && !string.IsNullOrEmpty(_currentTempVideoPath)
                    && File.Exists(_currentTempVideoPath))
                {
                    FfmpegVideoPlayer.Open(_currentTempVideoPath);
                    FfmpegVideoPlayer.Volume = _targetVolume;
                    FfmpegVideoPlayer.Play();
                    _ffmpegVideoTimer?.Start();
                    VideoStatusLabel.Text = "Playing";
                    VideoPlayButton.Content = "Pause";
                    return;
                }

                SetVideoStoppedUi();
            }
            catch
            {
                SetVideoStoppedUi();
            }
        });
    }

    private void VideoProgressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingVideoProgress || !FfmpegVideoPlayer.HasMediaLoaded)
            return;

        FfmpegVideoPlayer.Seek((float)(VideoProgressBar.Value / 1000.0));
    }

    private void VideoProgressBar_DragStarted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isVideoDragging = true;
    }

    private void VideoProgressBar_DragCompleted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isVideoDragging = false;
        if (!FfmpegVideoPlayer.HasMediaLoaded)
        {
            return;
        }
        FfmpegVideoPlayer.Seek((float)(VideoProgressBar.Value / 1000.0));
    }

    private async void VideoExportButton_Click(object? sender, RoutedEventArgs e)
    {
        var videoClip = currentPreviewVideoClip;
        if (videoClip == null) return;

        // Pause playback to prevent event loops and potential UI deadlocks while the modal dialog is open
        try
        {
            if (FfmpegVideoPlayer.IsPlaying)
            {
                FfmpegVideoPlayer.Pause();
                _ffmpegVideoTimer?.Stop();
                VideoStatusLabel.Text = "Paused";
                VideoPlayButton.Content = "Play";
            }
        }
        catch {}

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var extension = Path.GetExtension(videoClip.m_OriginalPath);
        if (string.IsNullOrEmpty(extension)) extension = ".mp4";

        var exportFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (exportFolders == null || exportFolders.Count == 0) return;

        var savePath = exportFolders[0].Path.LocalPath;
        var fileName = FixFileName(videoClip.m_Name) + extension;
        var filePath = Path.Combine(savePath, fileName);

        try
        {
            if (videoClip.m_ExternalResources.m_Size <= 0)
            {
                StatusStripUpdate("VideoClip data is empty or invalid.");
                return;
            }

            await Task.Run(() =>
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                videoClip.m_VideoData.WriteData(filePath);
            });
            StatusStripUpdate($"Successfully exported video clip to: {filePath}");
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to export video: {ex.Message}");
        }
    }

    private void AnimPlayPauseBtn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_is2dAnimationPreviewActive)
        {
            if (_2dAnimPaused)
            {
                Play2DAnimation();
            }
            else
            {
                Pause2DAnimation();
            }
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            if (GLPreviewControl.IsPlaying)
            {
                GLPreviewControl.PauseAnimation();
                SetAnimationPlaybackPaused(true);
            }
            else
            {
                GLPreviewControl.PlayAnimation();
                SetAnimationPlaybackPaused(false);
            }
        }
    }

    private void AnimRestartBtn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_is2dAnimationPreviewActive)
        {
            Restart2DAnimation();
            return;
        }

        if (GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            GLPreviewControl.RestartAnimation();
            SetAnimationPlaybackPaused(false);
        }
    }

    private CancellationTokenSource? preloaderCts;
    private readonly object preloaderLock = new();
    private readonly HashSet<string> preloadedUniqueIds = new();
    private const int AssetPreloadWindowSize = 50;

    private void PreprocessPreloadedAssetForPreview(AssetItem item, CancellationToken token)
    {
        if (token.IsCancellationRequested || item.Handle?.RealObject is not Mesh mesh)
        {
            return;
        }

        mesh.EnsureProcessed();
    }

    private void UnloadAsset(AssetHandle handle)
    {
        if (handle == null) return;

        lock (handle)
        {
            var obj = handle.RealObject;
            handle.RealObject = null;

            if (handle.Tag is AssetItem assetItem)
            {
                assetItem.Asset = null;
            }

            if (obj != null)
            {
                var assetsFile = handle.SourceFile;
                if (assetsFile != null)
                {
                    assetsFile.RemoveObject(obj);
                }
            }
        }
    }

    private void UpdatePreloadWindow(AssetItem currentItem)
    {
        if (!assetsManager.LazyLoading) return;
        if (IsProgressiveIndexingActive()) return;

        lock (preloaderLock)
        {
            preloaderCts?.Cancel();
            preloaderCts?.Dispose();
            preloaderCts = new CancellationTokenSource();
            var token = preloaderCts.Token;

            var preloadItems = visibleAssetItems
                .Where(item => item.Type == currentItem.Type)
                .ToList();

            var currentIdx = preloadItems.IndexOf(currentItem);
            if (currentIdx < 0) return;

            int total = preloadItems.Count;
            int windowSize = Math.Min(AssetPreloadWindowSize, total);
            int start = currentIdx - windowSize / 2;
            if (start < 0) start = 0;
            if (start + windowSize > total) start = total - windowSize;
            int end = start + windowSize - 1;

            var itemsToKeep = new HashSet<string>();
            var itemsToLoad = new List<AssetItem>();

            for (int i = start; i <= end; i++)
            {
                var item = preloadItems[i];
                if (item.Handle != null && !string.IsNullOrEmpty(item.Handle.UniqueID))
                {
                    itemsToKeep.Add(item.Handle.UniqueID);
                    if (item.Handle.RealObject == null)
                    {
                        itemsToLoad.Add(item);
                    }
                }
            }

            var toUnload = new List<string>();
            foreach (var uid in preloadedUniqueIds)
            {
                if (!itemsToKeep.Contains(uid))
                {
                    toUnload.Add(uid);
                }
            }

            foreach (var uid in toUnload)
            {
                preloadedUniqueIds.Remove(uid);
                var handle = assetsManager.ProjectIndex.GetHandle(uid);
                if (handle != null)
                {
                    UnloadAsset(handle);
                }
            }

            var sortedItemsToLoad = itemsToLoad
                .OrderBy(item =>
                {
                    var idx = preloadItems.IndexOf(item);
                    return idx >= 0 ? Math.Abs(idx - currentIdx) : int.MaxValue;
                })
                .ToList();

            _ = Task.Run(async () =>
            {
                foreach (var item in sortedItemsToLoad)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        if (token.IsCancellationRequested) break;

                        if (item.Handle?.UniqueID == null) continue;

                        bool alreadyLoaded;
                        lock (preloaderLock)
                        {
                            alreadyLoaded = preloadedUniqueIds.Contains(item.Handle.UniqueID);
                        }

                        if (alreadyLoaded)
                        {
                            continue;
                        }

                        if (item.Handle.RealObject != null)
                        {
                            PreprocessPreloadedAssetForPreview(item, token);
                            lock (preloaderLock)
                            {
                                preloadedUniqueIds.Add(item.Handle.UniqueID);
                            }
                            continue;
                        }

                        EnsureLazyAssetReadyForPreview(item);
                        assetsManager.ResolveHandle(item.Handle);

                        if (item.Handle?.UniqueID != null && item.Handle.RealObject != null)
                        {
                            PreprocessPreloadedAssetForPreview(item, token);
                            lock (preloaderLock)
                            {
                                preloadedUniqueIds.Add(item.Handle.UniqueID);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore individual load errors
                    }

                    await Task.Delay(15, token);
                }
            }, token);
        }
    }
}
