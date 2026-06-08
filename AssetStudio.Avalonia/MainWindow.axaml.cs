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


}
