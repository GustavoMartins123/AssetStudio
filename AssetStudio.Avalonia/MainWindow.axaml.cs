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
        InitializeVideoPlayer();
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


}
