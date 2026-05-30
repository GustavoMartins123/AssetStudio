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
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using FFmpegVideoPlayer.Core;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private AssetsManager assetsManager = new AssetsManager();
    private List<AssetItem> exportableAssets = new List<AssetItem>();
    private Texture2D? currentPreviewTexture;
    private Sprite? currentPreviewSprite;
    private Mesh? currentPreviewMesh;
    private Avatar? currentPreviewAvatar;
    private AudioClip? currentPreviewAudioClip;
    private VideoClip? currentPreviewVideoClip;
    private System.Diagnostics.Process? _linuxAudioProcess;
    private bool _isLinuxAudioPaused;
    private System.Diagnostics.Stopwatch? _linuxAudioStopwatch;
    private bool useGpuTexturePreview = true;
    private readonly bool[] textureChannels = new bool[4] { true, true, true, true };
    private long texturePreviewIdCounter;
    private CancellationTokenSource? previewDebounce;
    private const int PreviewDebounceMilliseconds = 180;
    private const int MaxInlinePreviewTextureDimension = 1024;
    private const int ProgressiveIndexingUiThrottleMilliseconds = 2500;
    private const int UserInteractionPriorityMilliseconds = 1200;
    private const int UserPreviewPriorityMilliseconds = 1800;
    private const int UserInteractionYieldDelayMilliseconds = 40;
    private long userInteractionPriorityUntilTimestamp;
    private List<AssetItem> visibleAssets = new();
    private List<AssetClassItem> assetClassItems = new List<AssetClassItem>();
    private System.Collections.ObjectModel.ObservableCollection<AssetClassItem> visibleAssetClassItems = new();
    private System.Diagnostics.Stopwatch? _indexingUiThrottleStopwatch;
    private AssetClassItem? classFilterOverride;
    private List<GameObjectNode> sceneTreeNodes = new List<GameObjectNode>();
    private readonly List<GameObjectNode> treeSearchResults = new List<GameObjectNode>();
    private readonly ExportOptionsState exportOptions = new();
    private readonly AvaloniaAppSettings appSettings = AvaloniaAppSettings.Load();
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
    private Dictionary<Mesh, Avatar?>? meshAvatarCache;
    private Dictionary<AnimationClip, HashSet<uint>>? animationClipTransformBindingsCache;

    private FFmpegMediaPlayer? _audioMediaPlayer;
    private string? _currentTempAudioPath;
    private long _audioLengthMs;
    private DispatcherTimer? _audioTimer;
    private volatile int _targetAudioVolume = 80;
    private bool _isUpdatingAudioProgress = false;
    private bool _isAudioDragging = false;

    private string? _currentTempVideoPath;
    private bool _isUpdatingVideoProgress = false;
    private bool _isVideoDragging = false;
    private long _videoLengthMs = 0;
    private volatile int _targetVolume = 80;
    private DispatcherTimer? _ffmpegVideoTimer;

    private ProjectScanResult? currentScanResult;
    private bool isBuildingAssetStructures;
    private CancellationTokenSource? indexingCts;
    private bool isIndexingPaused;
    private Task? indexingTask;
    private List<string> pendingFilesToIndex = new List<string>();

    private string? _pendingStatusText;
    private string? _currentlySelectedUniqueID;
    private bool isRefreshingFilterList;
    private bool isRefreshingClassesList;
    private bool _statusUpdatePending;

    public MainWindow()
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        InitializeComponent();
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
        SpecifyUnityVersionTextBox.Text = appSettings.SpecifyUnityVersion;
        SpecifyUnityVersionTextBox.LostFocus += (s, e) => ApplyUnityVersionOption();
        ApplyUnityVersionOption();
        assetsManager.ProjectRoot = appSettings.ProjectRoot;
        AssetsManager.ShouldYieldForUserInteraction = IsUserInteractionPriorityActive;
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

        FMODprogressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, FMODprogressBar_DragStarted);
        FMODprogressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, FMODprogressBar_DragCompleted);
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

        try
        {
            _audioMediaPlayer = new FFmpegMediaPlayer(action =>
            {
                Dispatcher.UIThread.Post(action);
            }, (sr, ch) => {
                try
                {
                    return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                }
                catch
                {
                    return null;
                }
            });

            _audioMediaPlayer.EndReached += AudioMediaPlayer_EndReached;
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
                    AnimFrameLabel.Text = $"Frame: {currentFrame}/{totalFrames}";
                });
            };
        }

        ApplyAvatarPreviewControlSettings();
    }

    private void StatusStripUpdate(string text)
    {
        _pendingStatusText = text;
        if (!_statusUpdatePending)
        {
            _statusUpdatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                statusLabel.Text = _pendingStatusText;
                _statusUpdatePending = false;
            }, DispatcherPriority.Background);
        }
    }

    private void SetProgressBarValue(int value)
    {
        Dispatcher.UIThread.Post(() => progressBar.Value = value);
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

    private async Task WaitForUserInteractionPriorityToClearAsync(CancellationToken token)
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        while (!token.IsCancellationRequested && IsUserInteractionPriorityActive())
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

        while (IsUserInteractionPriorityActive())
        {
            Thread.Sleep(UserInteractionYieldDelayMilliseconds);
        }
    }

    private void ResetForm()
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
        meshAvatarCache = null;
        animationClipTransformBindingsCache = null;
        logger.ClearErrors();
        exportableAssets.Clear();
        visibleAssets.Clear();
        assetClassItems.Clear();
        visibleAssetClassItems.Clear();
        classFilterOverride = null;
        if (ClearClassFilterButton != null)
        {
            ClearClassFilterButton.IsVisible = false;
        }
        AssetListDataGrid.ItemsSource = null;
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
        if (indexingCts != null)
        {
            indexingCts.Cancel();
            indexingCts.Dispose();
            indexingCts = null;
        }
        if (indexingMenu != null)
        {
            indexingMenu.IsVisible = false;
        }
        progressBar.Value = 0;
        ResetFilterTypeMenu();
        StatusStripUpdate("Ready");

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Title = $"AssetStudio v{version}";

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
        assetsManager.SpecifyUnityVersion = SpecifyUnityVersionTextBox.Text?.Trim() ?? string.Empty;
        if (appSettings.SpecifyUnityVersion != assetsManager.SpecifyUnityVersion)
        {
            appSettings.SpecifyUnityVersion = assetsManager.SpecifyUnityVersion;
            appSettings.Save();
        }
    }

    private void ClearPreview(string message = "[Preview Panel]")
    {
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
        if (FMODPanel != null)
        {
            FMODPanel.IsVisible = false;
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
        appSettings.Save();
    }

    private void SaveExportFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        appSettings.ExportFolderPath = path;
        appSettings.Save();
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
        appSettings.Save();
        if (assetsManager.assetsFileList.Count > 0)
        {
            BuildAssetStructures();
        }
    }

    private void EnablePreview_Click(object? sender, RoutedEventArgs e)
    {
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        appSettings.EnablePreview = enablePreview.IsChecked == true;
        appSettings.Save();
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
        appSettings.Save();
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
        appSettings.Save();
        StatusStripUpdate($"Project root set to: {assetsManager.ProjectRoot}");
    }

    private async void ShowExportOptions_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ExportOptionsWindow(exportOptions.Clone());
        var result = await dialog.ShowDialog<ExportOptionsState?>(this);
        if (result == null) return;

        exportOptions.CopyFrom(result);
        appSettings.ExportOptions.CopyFrom(result);
        appSettings.Save();
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

        var types = exportableAssets
            .Select(x => x.Type)
            .Distinct()
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

        return Enum.IsDefined(typeof(ClassIDType), type.classID)
            ? ((ClassIDType)type.classID).ToString()
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
            var loadChoice = await ConfirmFolderLoadIfRisky(folderPath);
            if (loadChoice == RiskyLoadChoice.Cancel)
            {
                StatusStripUpdate("Folder load cancelled.");
                return;
            }

            if (loadChoice == RiskyLoadChoice.LazyLoad)
            {
                LoadPathsProgressiveAsync(new[] { folderPath });
            }
            else
            {
                SaveLoadFolder(folderPath);
                ResetForm();
                StatusStripUpdate("Loading folder...");
                assetsManager.Clear();
                assetsManager.LazyLoading = false;
                ApplyUnityVersionOption();
                try
                {
                    await Task.Run(() => assetsManager.LoadFolder(folderPath));
                }
                catch (MemoryPressureException ex)
                {
                    ShowMemoryPressureError(ex);
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Error loading folder:\n{ex.Message}", "Load failed");
                    StatusStripUpdate("Folder load failed.");
                    return;
                }
                BuildAssetStructures();
            }
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
        var loadChoice = RiskyLoadChoice.EagerLoad;
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            loadChoice = await ConfirmFolderLoadIfRisky(paths[0]);
            if (loadChoice == RiskyLoadChoice.Cancel)
            {
                StatusStripUpdate("Dropped folder load cancelled.");
                return;
            }
        }

        if (loadChoice == RiskyLoadChoice.LazyLoad)
        {
            LoadPathsProgressiveAsync(paths);
        }
        else
        {
            ResetForm();
            assetsManager.Clear();
            assetsManager.LazyLoading = false;
            ApplyUnityVersionOption();
            StatusStripUpdate("Loading dropped files...");

            try
            {
                if (paths.Length == 1 && Directory.Exists(paths[0]))
                {
                    await Task.Run(() => assetsManager.LoadFolder(paths[0]));
                }
                else
                {
                    var files = paths
                        .SelectMany(path => Directory.Exists(path)
                            ? ImportHelper.GetFilesSafe(path, "*.*", true)
                            : new[] { path })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    await Task.Run(() => assetsManager.LoadFiles(files));
                }
            }
            catch (MemoryPressureException ex)
            {
                ShowMemoryPressureError(ex);
                return;
            }

            BuildAssetStructures();
        }
    }

    public enum RiskyLoadChoice
    {
        Cancel,
        EagerLoad,
        LazyLoad
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

        if (!scanResult.IsRisky)
        {
            return RiskyLoadChoice.EagerLoad;
        }

        currentScanResult = scanResult;

        var message = BuildRiskyProjectMessage(scanResult);
        return await ShowRiskyProjectDialog(message);
    }

    private async void LoadPathsProgressiveAsync(string[] paths)
    {
        ResetForm();
        assetsManager.Clear();
        assetsManager.LazyLoading = true;
        ApplyUnityVersionOption();
        StatusStripUpdate("Loading progressively...");

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

            // Check cache
            ProjectIndexCache? cachedIndex = null;
            if (currentScanResult != null && paths.Length == 1 && Directory.Exists(paths[0]))
            {
                cachedIndex = LoadIndexCache(paths[0], currentScanResult);
            }

            if (cachedIndex != null)
            {
                StatusStripUpdate("Loading project index from cache...");
                foreach (var ch in cachedIndex.Handles)
                {
                    var handle = new AssetHandle
                    {
                        UniqueID = ch.UniqueID,
                        Name = ch.Name,
                        Type = (ClassIDType)ch.Type,
                        Container = ch.Container,
                        OriginalPath = ch.OriginalPath,
                        SerializedFileName = ch.SerializedFileName,
                        PathID = ch.PathID,
                        ByteStart = ch.ByteStart,
                        ByteSize = ch.ByteSize
                    };
                    assetsManager.ProjectIndex.AddHandle(handle);
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
        if (indexingCts != null)
        {
            indexingCts.Cancel();
            indexingCts.Dispose();
        }

        indexingCts = new CancellationTokenSource();
        var token = indexingCts.Token;
        isIndexingPaused = false;
        pendingFilesToIndex = files.ToList();
        _indexingUiThrottleStopwatch = System.Diagnostics.Stopwatch.StartNew();

        indexingMenu.IsVisible = true;
        pauseIndexingMenu.IsEnabled = true;
        resumeIndexingMenu.IsEnabled = false;
        stopIndexingMenu.IsEnabled = true;

        var originalTotal = pendingFilesToIndex.Count;

        indexingTask = Task.Run(async () =>
        {
            int batchSize = 100;
            int loadedCount = 0;

            while (pendingFilesToIndex.Count > 0)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                while (isIndexingPaused && !token.IsCancellationRequested)
                {
                    await Task.Delay(200);
                }

                if (token.IsCancellationRequested)
                    break;

                await WaitForUserInteractionPriorityToClearAsync(token);
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    AssetsManager.ThrowIfMemoryPressureTooHigh("progressive indexing");
                }
                catch (MemoryPressureException ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowMemoryPressureError(ex);
                        isIndexingPaused = true;
                        pauseIndexingMenu.IsEnabled = false;
                        resumeIndexingMenu.IsEnabled = true;
                        StatusStripUpdate("Indexing paused due to high memory pressure.");
                    });

                    while (isIndexingPaused && !token.IsCancellationRequested)
                    {
                        await Task.Delay(500);
                    }
                    continue;
                }

                var activeFilters = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (filterTypeAll.IsChecked != true)
                    {
                        return GetFilterTypeItems()
                            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
                            .Select(x => (ClassIDType)x.Tag!)
                            .ToList();
                    }

                    return new List<ClassIDType>();
                });

                var batch = new List<string>();
                lock (pendingFilesToIndex)
                {
                    var keywords = new List<string>();
                    if (activeFilters.Contains(ClassIDType.Texture2D) || activeFilters.Contains(ClassIDType.Sprite))
                        keywords.AddRange(new[] { "texture", "sprite", "atlas", "image", "pic" });
                    if (activeFilters.Contains(ClassIDType.AudioClip))
                        keywords.AddRange(new[] { "audio", "sound", "music", "sfx", "clip" });
                    if (activeFilters.Contains(ClassIDType.Mesh))
                        keywords.AddRange(new[] { "mesh", "model", "geom", "3d" });
                    if (activeFilters.Contains(ClassIDType.AnimationClip) || activeFilters.Contains(ClassIDType.Animator))
                        keywords.AddRange(new[] { "anim", "motion", "controller" });
                    if (activeFilters.Contains(ClassIDType.Shader))
                        keywords.Add("shader");
                    if (activeFilters.Contains(ClassIDType.MonoBehaviour))
                        keywords.AddRange(new[] { "script", "behavior", "mono" });

                    if (keywords.Count > 0)
                    {
                        for (int i = 0; i < pendingFilesToIndex.Count && batch.Count < batchSize; i++)
                        {
                            var file = pendingFilesToIndex[i];
                            var fileName = Path.GetFileName(file).ToLowerInvariant();
                            if (keywords.Any(k => fileName.Contains(k)))
                            {
                                batch.Add(file);
                                pendingFilesToIndex.RemoveAt(i);
                                i--;
                            }
                        }
                    }

                    while (pendingFilesToIndex.Count > 0 && batch.Count < batchSize)
                    {
                        batch.Add(pendingFilesToIndex[0]);
                        pendingFilesToIndex.RemoveAt(0);
                    }
                }

                if (batch.Count == 0)
                    break;

                await WaitForUserInteractionPriorityToClearAsync(token);
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    assetsManager.LoadFiles(batch.ToArray());
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error loading batch of {batch.Count} files", ex);
                }

                loadedCount += batch.Count;
                var currentLoaded = loadedCount;
                var progressPercent = (int)((double)currentLoaded / originalTotal * 100);

                var shouldUpdateUi = currentLoaded < originalTotal && ShouldUpdateProgressiveIndexingUi();

                if (shouldUpdateUi)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        progressBar.Value = progressPercent;
                        StatusStripUpdate($"Indexed: {currentLoaded:N0} / {originalTotal:N0} files ({progressPercent}%)");
                        BuildAssetStructures(incremental: true);
                    });
                }
            }

            var finalLoadedCount = loadedCount;
            var finalProgressPercent = originalTotal == 0 ? 100 : (int)((double)finalLoadedCount / originalTotal * 100);
            var wasCancelled = token.IsCancellationRequested;

            Dispatcher.UIThread.Post(() =>
            {
                _indexingUiThrottleStopwatch?.Stop();
                _indexingUiThrottleStopwatch = null;
                indexingMenu.IsVisible = false;
                progressBar.Value = wasCancelled ? finalProgressPercent : 100;
                StatusStripUpdate(wasCancelled
                    ? $"Indexing cancelled. Indexed: {finalLoadedCount:N0} / {originalTotal:N0} files ({finalProgressPercent}%)"
                    : $"Indexing finished. Total files: {originalTotal:N0}");

                if (currentScanResult != null && paths.Length == 1 && Directory.Exists(paths[0]) && !token.IsCancellationRequested)
                {
                    SaveIndexCache(paths[0], currentScanResult);
                }

                BuildAssetStructures(incremental: true);
            });
        }, token);
    }

    private bool ShouldUpdateProgressiveIndexingUi(bool force = false)
    {
        if (!force && IsUserInteractionPriorityActive())
        {
            return false;
        }

        var stopwatch = _indexingUiThrottleStopwatch;
        if (stopwatch == null)
        {
            return force;
        }

        lock (stopwatch)
        {
            if (!force && stopwatch.ElapsedMilliseconds < ProgressiveIndexingUiThrottleMilliseconds)
            {
                return false;
            }

            stopwatch.Restart();
            return true;
        }
    }

    private void PauseIndexing_Click(object? sender, RoutedEventArgs e)
    {
        isIndexingPaused = true;
        pauseIndexingMenu.IsEnabled = false;
        resumeIndexingMenu.IsEnabled = true;
        StatusStripUpdate("Indexing paused.");
        BuildAssetStructures(incremental: true);
    }

    private void ResumeIndexing_Click(object? sender, RoutedEventArgs e)
    {
        isIndexingPaused = false;
        pauseIndexingMenu.IsEnabled = true;
        resumeIndexingMenu.IsEnabled = false;
        StatusStripUpdate("Resuming indexing...");
    }

    private void StopIndexing_Click(object? sender, RoutedEventArgs e)
    {
        indexingCts?.Cancel();
        indexingMenu.IsVisible = false;
        StatusStripUpdate("Stopping/cancelling indexing...");
    }

    private static string GetFolderCacheKey(string folderPath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folderPath)));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private void SaveIndexCache(string folderPath, ProjectScanResult scanResult)
    {
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "IndexCache");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            var cacheKey = GetFolderCacheKey(folderPath);
            var cachePath = Path.Combine(cacheDir, $"{cacheKey}.json");

            var cache = new ProjectIndexCache
            {
                RootPath = folderPath,
                TotalFiles = scanResult.TotalFiles,
                TotalBytes = scanResult.TotalBytes,
                UnityBundleCount = scanResult.UnityBundleCount,
                Handles = assetsManager.ProjectIndex.GetHandles().Select(h => new CachedAssetHandle
                {
                    UniqueID = h.UniqueID,
                    Name = h.Name,
                    Type = (int)h.Type,
                    Container = h.Container,
                    OriginalPath = h.OriginalPath,
                    SerializedFileName = h.SerializedFileName,
                    PathID = h.PathID,
                    ByteStart = h.ByteStart,
                    ByteSize = h.ByteSize
                }).ToList()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(cache);
            File.WriteAllText(cachePath, json);
            Logger.Info($"Saved index cache to {cachePath}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to save index cache: {ex.Message}");
        }
    }

    private ProjectIndexCache? LoadIndexCache(string folderPath, ProjectScanResult scanResult)
    {
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "IndexCache");
            var cacheKey = GetFolderCacheKey(folderPath);
            var cachePath = Path.Combine(cacheDir, $"{cacheKey}.json");

            if (!File.Exists(cachePath))
            {
                return null;
            }

            var json = File.ReadAllText(cachePath);
            var cache = System.Text.Json.JsonSerializer.Deserialize<ProjectIndexCache>(json);

            if (cache != null &&
                cache.TotalFiles == scanResult.TotalFiles &&
                cache.TotalBytes == scanResult.TotalBytes &&
                cache.UnityBundleCount == scanResult.UnityBundleCount)
            {
                return cache;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load index cache: {ex.Message}");
        }
        return null;
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
        var bundleFile = new BundleFile(reader);
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
        if (isBuildingAssetStructures) return;
        isBuildingAssetStructures = true;
        try
        {
            if (assetsManager.assetsFileList.Count == 0)
            {
                StatusStripUpdate("No Unity file can be loaded.");
                return;
            }

            StatusStripUpdate("Building asset structures...");

        // Capture required UI states on the UI thread
        bool displayAllChecked = displayAll.IsChecked == true;
        List<SerializedFile> filesListSnapshot;
        lock (assetsManager.loadLock)
        {
            filesListSnapshot = assetsManager.assetsFileList.ToList();
        }

        var result = await Task.Run(() =>
        {
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

            if (!assetsManager.LazyLoading)
            {
                LinkAssetItemsToSceneNodesBackground(filesListSnapshot, localTreeNodeDictionary, localObjectAssetItemDic);
            }

            foreach ((var pptr, var container) in localContainers)
            {
                if (pptr.TryGetAssetsFile(out var targetFile))
                {
                    var targetKey = $"{targetFile.fileName}#{pptr.m_PathID}";
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

            var localAnimationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>();
            var localAvatarMeshCache = new Dictionary<Avatar, Mesh?>();
            var localMeshAvatarCache = new Dictionary<Mesh, Avatar?>();
            var localAnimationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>();

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
                        out localMaterialTextureSlotsCache),
                    () => BuildAnimationPreviewIndexesBackground(
                        filesListSnapshot,
                        out localAnimationClipAvatarCache,
                        out localAvatarMeshCache,
                        out localMeshAvatarCache,
                        out localAnimationClipTransformBindingsCache));
            }

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
                AnimationClipAvatarCache = localAnimationClipAvatarCache,
                AvatarMeshCache = localAvatarMeshCache,
                MeshAvatarCache = localMeshAvatarCache,
                AnimationClipTransformBindingsCache = localAnimationClipTransformBindingsCache,
                AssetClassItems = localAssetClassItems
            };
        });

        await WaitForUserInteractionPriorityToClearAsync(CancellationToken.None);

        // Apply results back on the UI thread
        bool useIncrementalPath = incremental && exportableAssets.Count > 0 && result.NewExportableAssets != null;

        if (useIncrementalPath && result.NewExportableAssets.Count == 0)
        {
            // Nothing new to add — skip UI update entirely
        }
        else if (useIncrementalPath)
        {
            // Incremental path: only append new items to avoid O(n) DataGrid notifications
            exportableAssets = result.ExportableAssets;
            assetClassItems = result.AssetClassItems;

            BuildFilterTypeMenu();
            AppendFilteredAssetsToVisible(result.NewExportableAssets);
            UpdateAssetClassesIncremental(result.AssetClassItems);
        }
        else
        {
            // Full rebuild path (initial load, display all toggle, etc.)
            exportableAssets = result.ExportableAssets;
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
            log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
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
            Title = $"AssetStudio v{version}";
        }
        }
        finally
        {
            isBuildingAssetStructures = false;
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
            QueuePreviewAsset(assetItem);
        }
        else
        {
            _currentlySelectedUniqueID = null;
            DumpTextBox.Text = string.Empty;
            previewDebounce?.Cancel();
            ClearPreview("Preview Panel");
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
        try
        {
            var asset = await Task.Run(() => assetItem.Asset);
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
        try
        {
            return assetItem.Asset;
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Error resolving asset for preview {assetItem.Name}: {ex}");
            return null;
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
                $"Preview unavailable while this asset is still loading.{Environment.NewLine}" +
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
        if (FMODPanel != null)
        {
            FMODPanel.IsVisible = false;
            AudioReset();
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            VideoReset();
        }
        ClearMeshMaterialControls();
        if (asset is not AnimationClip)
        {
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
                {
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

                            var subMeshTextures = new List<byte[]?>();
                            var subMeshTexWidths = new List<int>();
                            var subMeshTexHeights = new List<int>();
                            var allMaterials = FindMaterialsForMesh(m_Mesh);

                            if (m_Mesh.m_SubMeshes != null && m_Mesh.m_SubMeshes.Length > 0)
                            {
                                for (int i = 0; i < m_Mesh.m_SubMeshes.Length; i++)
                                {
                                    if (meshPreviewId != texturePreviewIdCounter)
                                    {
                                        return;
                                    }

                                    byte[]? tb = null;
                                    int tw = 0, th = 0;

                                    if (i < allMaterials.Count && allMaterials[i] != null)
                                    {
                                        var tex = FindTextureForMaterial(allMaterials[i]!);
                                        if (tex != null)
                                        {
                                            try
                                            {
                                                using (var image = tex.ConvertToImage(true))
                                                {
                                                    if (image != null)
                                                    {
                                                        LimitInlinePreviewImage(image);
                                                        tw = image.Width;
                                                        th = image.Height;
                                                        tb = new byte[tw * th * 4];
                                                        image.CopyPixelDataTo(tb);
                                                        for (int p = 0; p < tb.Length; p += 4)
                                                        {
                                                            byte temp = tb[p];
                                                            tb[p] = tb[p + 2];
                                                            tb[p + 2] = temp;
                                                        }
                                                    }
                                                }
                                            }
                                            catch {}
                                        }
                                    }
                                    subMeshTextures.Add(tb);
                                    subMeshTexWidths.Add(tw);
                                    subMeshTexHeights.Add(th);
                                }
                            }

                            global::OpenTK.Mathematics.Vector2[]? uvs = null;
                            if (m_Mesh.m_UV0 != null && m_Mesh.m_UV0.Length >= m_Mesh.m_VertexCount * 2)
                            {
                                uvs = new global::OpenTK.Mathematics.Vector2[m_Mesh.m_VertexCount];
                                for (int i = 0; i < m_Mesh.m_VertexCount; i++)
                                {
                                    uvs[i] = new global::OpenTK.Mathematics.Vector2(m_Mesh.m_UV0[i * 2], m_Mesh.m_UV0[i * 2 + 1]);
                                }
                            }

                            var infoText = includeMeshInfo ? FormatMeshPreview(m_Mesh, localAssetItem) : string.Empty;
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
                                    GLPreviewControl.SetMesh(m_Mesh, uvs, subMeshTextures, subMeshTexWidths, subMeshTexHeights);
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
                    break;
                }
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

    private void PreviewAvatar(Avatar avatar)
    {
        currentPreviewAvatar = avatar;
        Mesh? avatarMesh = FindBestMeshForAvatar(avatar);
        avatarMesh?.EnsureProcessed();
        currentPreviewMesh = avatarMesh;

        global::OpenTK.Mathematics.Vector3[]? bonePositions = null;
        int[]? parentIndices = null;
        string[]? boneNames = null;

        if (avatarMesh != null && avatarMesh.m_BindPose != null && avatarMesh.m_BindPose.Length > 0
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
            StatusStripUpdate($"OpenGL Avatar Preview | Mesh: {avatarMesh.m_Name} | Skeleton Joints: {bonePositions.Length}");
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

    private void PreviewAnimationClip(AnimationClip clip)
    {
        // Step 1: Find the Avatar and Mesh to use, preferring the currently visible one
        Avatar? avatar = null;
        Mesh? avatarMesh = null;

        currentPreviewMesh?.EnsureProcessed();
        if (currentPreviewMesh != null && currentPreviewMesh.m_BindPose != null && currentPreviewMesh.m_BindPose.Length > 0
            && currentPreviewMesh.m_BoneNameHashes != null && currentPreviewMesh.m_BoneNameHashes.Length > 0)
        {
            var candidateAvatar = currentPreviewAvatar ?? FindBestAvatarForMesh(currentPreviewMesh);
            if (candidateAvatar != null && IsAnimationClipCompatibleWithAvatar(clip, candidateAvatar))
            {
                avatarMesh = currentPreviewMesh;
                avatar = candidateAvatar;
            }
        }
        if (avatar == null && currentPreviewAvatar != null && currentPreviewAvatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null
            && IsAnimationClipCompatibleWithAvatar(clip, currentPreviewAvatar))
        {
            avatar = currentPreviewAvatar;
            avatarMesh = GetCachedMeshForAvatar(avatar);
        }

        if (avatar == null)
        {
            animationClipAvatarCache?.TryGetValue(clip, out avatar);
            if (avatar == null)
            {
                StatusStripUpdate($"AnimationClip: No compatible Avatar found for {clip.m_Name}. Load/select the matching model first.");
                return;
            }
        }

        if (avatar == null || avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            StatusStripUpdate("AnimationClip: No Avatar found to preview animation.");
            return;
        }

        if (avatarMesh == null)
        {
            avatarMesh = GetCachedMeshForAvatar(avatar);
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

            if (AnimationPlaybackPanel != null)
            {
                AnimationPlaybackPanel.IsVisible = true;
                AnimPlayPauseBtn.Content = "Pause";
                AnimFrameLabel.Text = $"Frame: 0/{frameCount}";
            }

            StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name} | Frames: {frameCount} | FPS: {sampleRate} | Tracks: {posTracks.Count + rotTracks.Count + scaleTracks.Count}");
        }
    }

    private Mesh? FindBestMeshForAvatar(Avatar avatar)
    {
        return GetCachedMeshForAvatar(avatar);
    }

    private Avatar? FindBestAvatarForMesh(Mesh mesh)
    {
        return meshAvatarCache != null && meshAvatarCache.TryGetValue(mesh, out var avatar) ? avatar : null;
    }

    private Mesh? GetCachedMeshForAvatar(Avatar avatar)
    {
        return avatarMeshCache != null && avatarMeshCache.TryGetValue(avatar, out var mesh) ? mesh : null;
    }

    private bool IsAnimationClipCompatibleWithAvatar(AnimationClip clip, Avatar avatar)
    {
        if (animationClipAvatarCache != null && animationClipAvatarCache.TryGetValue(clip, out var cachedAvatar))
        {
            return ReferenceEquals(cachedAvatar, avatar);
        }

        if (animationClipTransformBindingsCache == null
            || !animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths)
            || bindingPaths.Count == 0
            || avatar.m_TOS == null)
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

    private void PreviewMaterial(AssetItem assetItem, Material m_Material)
    {
        HideAnimationPlayback();
        if (GLPreviewControl != null)
        {
            GLPreviewControl.StopAnimation();
            GLPreviewControl.IsVisible = false;
        }

        var displayMaterial = ResolveMaterialForPreview(m_Material) ?? m_Material;
        var sb = new StringBuilder();
        sb.AppendLine($"Material: {m_Material.m_Name}");
        if (!ReferenceEquals(displayMaterial, m_Material))
        {
            sb.AppendLine($"Parent material: {displayMaterial.m_Name}");
        }
        
        Shader? shader = null;
        if (displayMaterial.m_Shader != null && displayMaterial.m_Shader.TryGet(out var s))
        {
            shader = s;
        }

        if (shader != null)
        {
            sb.AppendLine($"Shader: {shader.m_ParsedForm?.m_Name ?? shader.m_Name}");
        }
        sb.AppendLine();
        sb.AppendLine("Texture slots:");

        Texture2D? previewTexture = null;
        foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
        {
            sb.Append($"  {texEnv.Key}: ");
            var texEnvValue = texEnv.Value;
            var textureRef = texEnvValue?.m_Texture;
            
            var texture = texEnvValue != null ? GetMaterialTextureSlot(displayMaterial, texEnv.Key) : null;

            if (texture != null && textureRef != null)
            {
                sb.AppendLine($"{texture.m_Name} ({texture.m_Width}x{texture.m_Height}, {texture.m_TextureFormat})");
                sb.AppendLine($"    FileID: {textureRef.m_FileID}, PathID: {textureRef.m_PathID}");
                sb.AppendLine($"    Scale: {texEnvValue?.m_Scale.X}, {texEnvValue?.m_Scale.Y}");
                sb.AppendLine($"    Offset: {texEnvValue?.m_Offset.X}, {texEnvValue?.m_Offset.Y}");
                if (previewTexture == null && IsPreferredMaterialPreviewSlot(texEnv.Key))
                {
                    previewTexture = texture;
                }
            }
            else
            {
                sb.AppendLine(textureRef == null || textureRef.IsNull
                    ? "null"
                    : $"missing (FileID: {textureRef.m_FileID}, PathID: {textureRef.m_PathID})");
            }
        }

        if (previewTexture == null)
        {
            previewTexture = FindTextureForMaterial(displayMaterial);
        }

        string infoText = sb.ToString();

        if (previewTexture != null)
        {
            currentPreviewTexture = previewTexture;
            long currentId = ++texturePreviewIdCounter;
            StatusStripUpdate("Loading material texture preview...");

            Task.Run(() =>
            {
                try
                {
                    var image = previewTexture.ConvertToImage(true);
                    if (image == null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (currentId == texturePreviewIdCounter)
                            {
                                TextPreviewBox.Text = infoText;
                                TextPreviewBox.IsVisible = true;
                                ImagePreviewBox.IsVisible = false;
                                PreviewInfoBorder.IsVisible = false;
                                if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                                StatusStripUpdate("Material loaded (no preview texture support).");
                            }
                        });
                        return;
                    }
                    var materialPreviewWasDownscaled = LimitInlinePreviewImage(image);
                    var materialPreviewWidth = image.Width;
                    var materialPreviewHeight = image.Height;

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
                    else
                    {
                        MakeAlphaOnlyTextureVisible(image);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (currentId == texturePreviewIdCounter)
                            {
                                if (GLPreviewControl != null)
                                {
                                    GLPreviewControl.SetMaterialTexture(image);
                                    GLPreviewControl.IsVisible = true;
                                    HidePreviewGeometryControls();
                                    GLPreviewControl.Focus();
                                }

                                ImagePreviewBox.IsVisible = false;
                                TextPreviewBox.IsVisible = false;
                                PreviewLabel.IsVisible = false;

                                if (displayInfo.IsChecked == true)
                                {
                                    PreviewInfoOverlay.Text = materialPreviewWasDownscaled
                                        ? infoText + $"\nPreview texture downscaled to {materialPreviewWidth}x{materialPreviewHeight}"
                                        : infoText;
                                    PreviewInfoBorder.IsVisible = true;
                                }
                                else
                                {
                                    PreviewInfoBorder.IsVisible = false;
                                }
                                StatusStripUpdate($"Material preview loaded: {previewTexture.m_Name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Log(LoggerEvent.Error, $"Material preview UI failed for {m_Material.m_Name}: {ex}");
                            if (currentId == texturePreviewIdCounter)
                            {
                                TextPreviewBox.Text = infoText + "\n[Error showing preview texture: " + ex.Message + "]";
                                TextPreviewBox.IsVisible = true;
                                ImagePreviewBox.IsVisible = false;
                                PreviewInfoBorder.IsVisible = false;
                                if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                                StatusStripUpdate("Material preview UI error.");
                            }
                        }
                        finally
                        {
                            image.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            TextPreviewBox.Text = infoText + "\n[Error loading preview texture: " + ex.Message + "]";
                            TextPreviewBox.IsVisible = true;
                            ImagePreviewBox.IsVisible = false;
                            PreviewInfoBorder.IsVisible = false;
                            if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                            StatusStripUpdate($"Material loaded with preview texture error.");
                        }
                    });
                }
            });
        }
        else
        {
            TextPreviewBox.Text = infoText;
            TextPreviewBox.IsVisible = true;
            ImagePreviewBox.IsVisible = false;
            PreviewInfoBorder.IsVisible = false;
            if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
            StatusStripUpdate("Material loaded (no texture).");
        }
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
        var maxSide = Math.Max(image.Width, image.Height);
        if (maxSide <= MaxInlinePreviewTextureDimension)
        {
            return false;
        }

        var scale = MaxInlinePreviewTextureDimension / (float)maxSide;
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

    private static bool IsPreferredMaterialPreviewSlot(string propertyName)
    {
        switch (propertyName)
        {
            case "_BaseMap":
            case "_MainTex":
            case "_BaseColorMap":
            case "_BaseColorTexture":
                return true;
            default:
                return false;
        }
    }

    private Material? FindMaterialForMesh(Mesh mesh)
    {
        return FindMaterialsForMesh(mesh).FirstOrDefault();
    }

    private static readonly HashSet<string> NonDiffuseSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
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

        IndexMaterialTextures(material);
        return materialMainTextureCache.TryGetValue(material, out var indexedTexture) ? indexedTexture : null;
    }

    private Texture2D? SelectMainTextureForMaterial(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
    {
        return SelectMainTextureForMaterialBackground(displayMaterial, textureSlots);
    }

    private Texture2D? GetMaterialTextureSlot(Material material, string slotName)
    {
        IndexMaterialTextures(material);
        return materialTextureSlotsCache != null
            && materialTextureSlotsCache.TryGetValue(material, out var slots)
            && slots.TryGetValue(slotName, out var texture)
            ? texture
            : null;
    }

    private void IndexMaterialTextures(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        IndexMaterialTexturesBackground(material, materialPreviewMaterialCache, materialTextureSlotsCache, materialMainTextureCache);
    }

    private Texture2D? ResolveTexturePPtr(Material material, PPtr<Texture> textureRef)
    {
        return ResolveTexturePPtrBackground(material, textureRef);
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
        ResetAssetListItemsSource();
    }

    private void ResetAssetListItemsSource()
    {
        AssetListDataGrid.ItemsSource = null;
        AssetListDataGrid.ItemsSource = visibleAssets;
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

        var filterText = listSearch?.Text?.Trim();
        var classFilter = classFilterOverride;
        var filterTypeChecked = filterTypeAll.IsChecked != true;
        var selectedTypes = filterTypeChecked ? GetFilterTypeItems()
            .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
            .Select(x => (ClassIDType)x.Tag!)
            .ToHashSet() : null;

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

            visibleAssets.Add(x);
        }

        if (!string.IsNullOrEmpty(assetListSortMember))
        {
            visibleAssets = SortAssetList(visibleAssets, assetListSortMember, assetListSortDescending);
        }

        ResetAssetListItemsSource();
        StatusStripUpdate($"Showing {visibleAssets.Count} assets");
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

        if (!assemblyLoader.Loaded && (mode == ExportMode.Convert || mode == ExportMode.Dump) && toExport.Any(x => x.Asset is MonoBehaviour))
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
                                File.WriteAllBytes(filePath, asset.Asset.GetRawData());
                                exported++;
                            }
                            break;
                        case ExportMode.Dump:
                            filePath += ".txt";
                            if (!File.Exists(filePath))
                            {
                                string? dump = null;
                                if (asset.Asset is MonoBehaviour m_MonoBehaviour)
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
                                else
                                {
                                    dump = asset.Asset.Dump();
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
        var clips = animationList.Select(x => (AnimationClip)x.Asset).ToArray();
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
                WriteCurrentExport(currentExportPath, animator, 1, 1);
                IImported convert = selectedGameObjects.Count > 0
                    ? new ModelConverter(animator.Name, selectedGameObjects, exportOptions.ConvertTextureFormat, clips)
                    : new ModelConverter((Animator)animator.Asset, exportOptions.ConvertTextureFormat, clips);
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
            .Select(x => (AnimationClip)x.Asset)
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
        var sourcePath = !string.IsNullOrEmpty(item.SourceFile.originalPath)
            ? item.SourceFile.originalPath
            : item.SourceFile.fullName;
        if (string.IsNullOrEmpty(sourcePath))
        {
            StatusStripUpdate("Original file path is unavailable.");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{sourcePath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                startInfo.ArgumentList.Add("-R");
                startInfo.ArgumentList.Add(sourcePath);
                Process.Start(startInfo);
            }
            else
            {
                var folder = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);
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
            AssetGroupOption.SourceFile => Path.Combine(savePath, asset.Asset.assetsFile.fileName + "_export"),
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

        Directory.CreateDirectory(exportPath);
        var fileName = FixFileName(GetExportFileName(item));

        switch (item.Asset)
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
                    AssetExportHelper.WriteTextureMetaIfMissing(rawPath);
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
                AssetExportHelper.WriteTextureMetaIfMissing(filePath);
                return true;
            }
            case AudioClip m_AudioClip:
            {
                var m_AudioData = m_AudioClip.m_AudioData.GetData();
                if (m_AudioData == null || m_AudioData.Length == 0) return false;
                var converter = new AudioClipConverter(m_AudioClip);
                var filePath = Path.Combine(exportPath, fileName + converter.GetExtensionName());
                if (File.Exists(filePath)) return false;
                File.WriteAllBytes(filePath, m_AudioData);
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
                File.WriteAllBytes(filePath, item.Asset.GetRawData());
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
            out var localMaterialTextureSlotsCache);

        objectToAssetItemCache = localObjectToAssetItemCache;
        meshToMaterialsCache = localMeshToMaterialsCache;
        meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
        meshSourceTypesCache = localMeshSourceTypesCache;
        materialMainTextureCache = localMaterialMainTextureCache;
        materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
        materialTextureSlotsCache = localMaterialTextureSlotsCache;

        BuildAnimationPreviewIndexesBackground(
            assetsManager.assetsFileList,
            out var localAnimationClipAvatarCache,
            out var localAvatarMeshCache,
            out var localMeshAvatarCache,
            out var localAnimationClipTransformBindingsCache);

        animationClipAvatarCache = localAnimationClipAvatarCache;
        avatarMeshCache = localAvatarMeshCache;
        meshAvatarCache = localMeshAvatarCache;
        animationClipTransformBindingsCache = localAnimationClipTransformBindingsCache;
    }

    private static int ScoreMaterials(List<Material?> mats)
    {
        return ScoreMaterialsStatic(mats);
    }



    private List<Material?> FindMaterialsForMesh(Mesh mesh)
    {
        if (meshToMaterialsCache == null)
        {
            BuildAssetReferenceIndexes();
        }

        if (meshToMaterialsCache!.TryGetValue(mesh, out var cachedList))
        {
            return new List<Material?>(cachedList);
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

    private void ShowErrorMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            logger.ShowErrorMessage = menuItem.IsChecked;
            appSettings.ShowErrorMessage = menuItem.IsChecked;
            appSettings.Save();
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
        AudioReset();
        VideoReset();
        _audioMediaPlayer?.Dispose();
        base.OnClosing(e);
    }

    #region Audio Preview
    private void AudioTimer_Tick(object? sender, EventArgs e)
    {
        if (_audioMediaPlayer == null)
            return;

        long currentMs = 0;
        long totalMs = 0;

        if (useLinuxAudioFallback)
        {
            if (_linuxAudioStopwatch != null)
            {
                currentMs = _linuxAudioStopwatch.ElapsedMilliseconds;
                if (currentMs > _audioLengthMs && _audioLengthMs > 0)
                {
                    currentMs = _audioLengthMs;
                    if (FMODloopButton.IsChecked == true)
                    {
                        _linuxAudioStopwatch.Restart();
                        PlayLinuxAudioFallback(_currentTempAudioPath!);
                    }
                    else
                    {
                        AudioStop();
                        return;
                    }
                }
            }
            totalMs = _audioLengthMs;
        }
        else
        {
            currentMs = (long)(_audioMediaPlayer.Position * (float)_audioMediaPlayer.Length);
            totalMs = _audioLengthMs > 0 ? _audioLengthMs : _audioMediaPlayer.Length;
            _audioLengthMs = totalMs;
        }

        if (FMODprogressBar != null && !_isAudioDragging && totalMs > 0)
        {
            FMODtimerLabel.Text = FormatMediaTime(currentMs, totalMs);
            _isUpdatingAudioProgress = true;
            FMODprogressBar.Value = currentMs * 1000.0 / totalMs;
            _isUpdatingAudioProgress = false;
        }

        if (useLinuxAudioFallback)
        {
            FMODstatusLabel.Text = (_linuxAudioStopwatch != null && _linuxAudioStopwatch.IsRunning) ? "Playing" : "Paused";
        }
        else
        {
            FMODstatusLabel.Text = _audioMediaPlayer.IsPlaying ? "Playing" : "Paused";
        }
    }

    private static string FormatMediaTime(long currentMs, long totalMs)
    {
        return $"{currentMs / 1000 / 60}:{currentMs / 1000 % 60:D2}.{currentMs / 10 % 100:D2} / {totalMs / 1000 / 60}:{totalMs / 1000 % 60:D2}.{totalMs / 10 % 100:D2}";
    }

    private void AudioReset()
    {
        _audioTimer?.Stop();
        try
        {
            if (useLinuxAudioFallback)
            {
                _linuxAudioStopwatch?.Reset();
                StopLinuxAudioFallback();
            }
            else if (_audioMediaPlayer != null)
            {
                _audioMediaPlayer.Stop();
                _audioMediaPlayer.Close();
            }
        }
        catch {}

        if (!string.IsNullOrEmpty(_currentTempAudioPath) && File.Exists(_currentTempAudioPath))
        {
            try
            {
                File.Delete(_currentTempAudioPath);
            }
            catch {}
            _currentTempAudioPath = null;
        }

        _audioLengthMs = 0;
        if (FMODprogressBar != null) FMODprogressBar.Value = 0;
        if (FMODtimerLabel != null) FMODtimerLabel.Text = "0:00.0 / 0:00.0";
        if (FMODstatusLabel != null) FMODstatusLabel.Text = "Stopped";
        if (FMODinfoLabel != null) FMODinfoLabel.Text = "";
        if (FMODpauseButton != null) FMODpauseButton.Content = "Pause";
    }

    private void FMODplayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_audioMediaPlayer == null || currentPreviewAudioClip == null)
            return;

        try
        {
            if (!EnsureAudioPreviewFile(currentPreviewAudioClip))
            {
                return;
            }

            if (useLinuxAudioFallback)
            {
                _linuxAudioStopwatch ??= new System.Diagnostics.Stopwatch();
                _linuxAudioStopwatch.Restart();
                PlayLinuxAudioFallback(_currentTempAudioPath!);
            }
            else
            {
                _audioMediaPlayer.Stop();
                try
                {
                    _audioMediaPlayer.Volume = _targetAudioVolume;
                }
                catch {}
                _audioMediaPlayer.Play();
            }

            FMODstatusLabel.Text = "Playing";
            FMODpauseButton.Content = "Pause";
            _audioTimer?.Start();
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to play audio: {ex.Message}");
        }
    }

    private bool useLinuxAudioFallback => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) && !isOpenAlSupportedCached;
    private static bool? isOpenAlSupportedCachedVal;
    private static bool isOpenAlSupportedCached
    {
        get
        {
            if (!isOpenAlSupportedCachedVal.HasValue)
            {
                isOpenAlSupportedCachedVal = IsOpenAlSupported();
            }
            return isOpenAlSupportedCachedVal.Value;
        }
    }

    private static bool IsOpenAlSupported()
    {
        try
        {
            using var test = FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(44100, 2);
            return test != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetLinuxAudioPlayerCommand()
    {
        string[] candidates = { "/usr/bin/pw-play", "/usr/bin/paplay", "/usr/bin/aplay", "/bin/aplay" };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private bool PlayLinuxAudioFallback(string path)
    {
        try
        {
            StopLinuxAudioFallback();

            var playerCmd = GetLinuxAudioPlayerCommand();
            if (playerCmd == null)
            {
                Logger.Warning("No system audio player (pw-play, paplay, aplay) found on Linux.");
                return false;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = playerCmd,
                Arguments = $"\"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _linuxAudioProcess = System.Diagnostics.Process.Start(startInfo);
            _isLinuxAudioPaused = false;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Linux fallback audio play failed: {ex.Message}");
            return false;
        }
    }

    private void StopLinuxAudioFallback()
    {
        try
        {
            if (_linuxAudioProcess != null && !_linuxAudioProcess.HasExited)
            {
                _linuxAudioProcess.Kill();
            }
            _linuxAudioProcess = null;
            _isLinuxAudioPaused = false;
        }
        catch {}
    }

    private void PauseLinuxAudioFallback()
    {
        try
        {
            if (_linuxAudioProcess != null && !_linuxAudioProcess.HasExited && !_isLinuxAudioPaused)
            {
                System.Diagnostics.Process.Start("kill", $"-STOP {_linuxAudioProcess.Id}")?.WaitForExit();
                _isLinuxAudioPaused = true;
            }
        }
        catch {}
    }

    private void ResumeLinuxAudioFallback()
    {
        try
        {
            if (_linuxAudioProcess != null && !_linuxAudioProcess.HasExited && _isLinuxAudioPaused)
            {
                System.Diagnostics.Process.Start("kill", $"-CONT {_linuxAudioProcess.Id}")?.WaitForExit();
                _isLinuxAudioPaused = false;
            }
        }
        catch {}
    }

    private bool EnsureAudioPreviewFile(AudioClip audioClip)
    {
        if (!string.IsNullOrEmpty(_currentTempAudioPath) && File.Exists(_currentTempAudioPath))
        {
            return true;
        }

        var currentAudioData = audioClip.m_AudioData.GetData();
        if (currentAudioData == null || currentAudioData.Length == 0)
        {
            StatusStripUpdate("AudioClip data is empty or invalid.");
            return false;
        }

        var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var oldFile in Directory.GetFiles(tempDir, "temp_audio_*"))
            {
                try { File.Delete(oldFile); } catch {}
            }
        }
        catch {}

        var converter = new AudioClipConverter(audioClip);
        var extension = converter.GetExtensionName();
        byte[]? audioData = null;

        if (converter.IsSupport)
        {
            try
            {
                audioData = converter.ConvertToWav();
                if (audioData != null && audioData.Length > 4)
                {
                    if (audioData[0] == 0x52 && audioData[1] == 0x49 && audioData[2] == 0x46 && audioData[3] == 0x46)
                    {
                        extension = ".wav";
                    }
                    else if (audioData[0] == 0x4F && audioData[1] == 0x67 && audioData[2] == 0x67 && audioData[3] == 0x53)
                    {
                        extension = ".ogg";
                    }
                    else if (audioData[0] == 0x49 && audioData[1] == 0x44 && audioData[2] == 0x33)
                    {
                        extension = ".mp3";
                    }
                    else if (audioData[0] == 0xFF && (audioData[1] & 0xE0) == 0xE0)
                    {
                        extension = ".mp3";
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Warning, $"Failed to convert AudioClip to standard audio format: {ex.Message}. Falling back to raw audio data.");
            }
        }

        if (audioData == null)
        {
            audioData = currentAudioData;
            if (extension.Equals(".AudioClip", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".bin";
            }
        }

        _currentTempAudioPath = Path.Combine(tempDir, $"temp_audio_{FixFileName(audioClip.m_Name)}_{audioClip.m_PathID}{extension}");
        File.WriteAllBytes(_currentTempAudioPath, audioData);

        // Retrieve duration from FSB bank if available to support simulated progress bar on Linux
        try
        {
            var m_AudioData = audioClip.m_AudioData.GetData();
            if (m_AudioData != null && m_AudioData.Length > 0)
            {
                var bank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(m_AudioData);
                if (bank.Samples != null && bank.Samples.Count > 0)
                {
                    var sample = bank.Samples[0];
                    if (sample.Metadata != null && sample.Metadata.Frequency > 0)
                    {
                        _audioLengthMs = (long)((double)sample.Metadata.SampleCount / sample.Metadata.Frequency * 1000.0);
                    }
                }
            }
        }
        catch {}

        if (_audioMediaPlayer != null && !useLinuxAudioFallback)
        {
            try
            {
                _audioMediaPlayer.Open(_currentTempAudioPath);
            }
            catch {}
        }

        return true;
    }

    private void FMODpauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_audioMediaPlayer == null || currentPreviewAudioClip == null)
            return;

        try
        {
            if (string.IsNullOrEmpty(_currentTempAudioPath) || !File.Exists(_currentTempAudioPath))
            {
                if (!EnsureAudioPreviewFile(currentPreviewAudioClip))
                {
                    return;
                }
            }

            if (useLinuxAudioFallback)
            {
                if (_linuxAudioStopwatch != null && _linuxAudioStopwatch.IsRunning)
                {
                    _linuxAudioStopwatch.Stop();
                    PauseLinuxAudioFallback();
                    FMODstatusLabel.Text = "Paused";
                    FMODpauseButton.Content = "Resume";
                }
                else
                {
                    _linuxAudioStopwatch ??= new System.Diagnostics.Stopwatch();
                    _linuxAudioStopwatch.Start();
                    ResumeLinuxAudioFallback();
                    FMODstatusLabel.Text = "Playing";
                    FMODpauseButton.Content = "Pause";
                    _audioTimer?.Start();
                }
            }
            else
            {
                if (_audioMediaPlayer.IsPlaying)
                {
                    _audioMediaPlayer.Pause();
                    FMODstatusLabel.Text = "Paused";
                    FMODpauseButton.Content = "Resume";
                }
                else
                {
                    try
                    {
                        _audioMediaPlayer.Volume = _targetAudioVolume;
                    }
                    catch {}
                    _audioMediaPlayer.Play();
                    FMODstatusLabel.Text = "Playing";
                    FMODpauseButton.Content = "Pause";
                    _audioTimer?.Start();
                }
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to pause audio: {ex.Message}");
        }
    }

    private void FMODstopButton_Click(object? sender, RoutedEventArgs e)
    {
        AudioStop();
    }

    private void AudioStop()
    {
        try
        {
            if (useLinuxAudioFallback)
            {
                _linuxAudioStopwatch?.Reset();
                StopLinuxAudioFallback();
            }
            else if (_audioMediaPlayer != null)
            {
                _audioMediaPlayer.Stop();
            }
            _audioTimer?.Stop();
            if (FMODprogressBar != null) FMODprogressBar.Value = 0;
            if (FMODtimerLabel != null) FMODtimerLabel.Text = "0:00.0 / 0:00.0";
            if (FMODstatusLabel != null) FMODstatusLabel.Text = "Stopped";
            if (FMODpauseButton != null) FMODpauseButton.Content = "Pause";
        }
        catch {}
    }

    private void FMODloopButton_Click(object? sender, RoutedEventArgs e)
    {
    }

    private void FMODvolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _targetAudioVolume = (int)(FMODvolumeBar.Value * 10);
        if (_audioMediaPlayer != null && !useLinuxAudioFallback)
        {
            try
            {
                _audioMediaPlayer.Volume = _targetAudioVolume;
            }
            catch {}
        }
    }

    private void FMODprogressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingAudioProgress)
            return;

        if (_audioLengthMs > 0 && FMODtimerLabel != null)
        {
            var newMs = (long)(_audioLengthMs * (FMODprogressBar.Value / 1000.0));
            FMODtimerLabel.Text = FormatMediaTime(newMs, _audioLengthMs);

            if (!_isAudioDragging && !useLinuxAudioFallback && _audioMediaPlayer != null)
            {
                try
                {
                    _audioMediaPlayer.Seek(newMs / (float)_audioLengthMs);
                }
                catch {}
            }
        }
    }

    private void AudioMediaPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FMODloopButton.IsChecked == true && !useLinuxAudioFallback && _audioMediaPlayer != null)
            {
                Task.Run(() =>
                {
                    try
                    {
                        _audioMediaPlayer.Stop();
                        _audioMediaPlayer.Play();
                        _audioMediaPlayer.Volume = _targetAudioVolume;
                    }
                    catch {}
                });
            }
            else
            {
                AudioStop();
            }
        });
    }

    private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
    {
        AudioReset();
        currentPreviewAudioClip = m_AudioClip;
        
        var infoText = "Compression format: ";
        if (m_AudioClip.version[0] < 5)
        {
            switch (m_AudioClip.m_Type)
            {
                case FMODSoundType.ACC:
                    infoText += "Acc";
                    break;
                case FMODSoundType.AIFF:
                    infoText += "AIFF";
                    break;
                case FMODSoundType.IT:
                    infoText += "Impulse tracker";
                    break;
                case FMODSoundType.MOD:
                    infoText += "Protracker / Fasttracker MOD";
                    break;
                case FMODSoundType.MPEG:
                    infoText += "MP2/MP3 MPEG";
                    break;
                case FMODSoundType.OGGVORBIS:
                    infoText += "Ogg vorbis";
                    break;
                case FMODSoundType.S3M:
                    infoText += "ScreamTracker 3";
                    break;
                case FMODSoundType.WAV:
                    infoText += "Microsoft WAV";
                    break;
                case FMODSoundType.XM:
                    infoText += "FastTracker 2 XM";
                    break;
                case FMODSoundType.XMA:
                    infoText += "Xbox360 XMA";
                    break;
                case FMODSoundType.VAG:
                    infoText += "PlayStation Portable ADPCM";
                    break;
                case FMODSoundType.AUDIOQUEUE:
                    infoText += "iPhone";
                    break;
                default:
                    infoText += "Unknown";
                    break;
            }
        }
        else
        {
            switch (m_AudioClip.m_CompressionFormat)
            {
                case AudioCompressionFormat.PCM:
                    infoText += "PCM";
                    break;
                case AudioCompressionFormat.Vorbis:
                    infoText += "Vorbis";
                    break;
                case AudioCompressionFormat.ADPCM:
                    infoText += "ADPCM";
                    break;
                case AudioCompressionFormat.MP3:
                    infoText += "MP3";
                    break;
                case AudioCompressionFormat.PSMVAG:
                    infoText += "PlayStation Portable ADPCM";
                    break;
                case AudioCompressionFormat.HEVAG:
                    infoText += "PSVita ADPCM";
                    break;
                case AudioCompressionFormat.XMA:
                    infoText += "Xbox360 XMA";
                    break;
                case AudioCompressionFormat.AAC:
                    infoText += "AAC";
                    break;
                case AudioCompressionFormat.GCADPCM:
                    infoText += "Nintendo 3DS/Wii DSP";
                    break;
                case AudioCompressionFormat.ATRAC9:
                    infoText += "PSVita ATRAC9";
                    break;
                default:
                    infoText += "Unknown";
                    break;
            }
        }

        if (_audioMediaPlayer == null)
        {
            StatusStripUpdate("Audio preview is unavailable (FFmpeg player is not loaded).");
            return;
        }

        FMODPanel.IsVisible = true;
        FMODinfoLabel.Text = infoText;
        FMODtimerLabel.Text = "0:00.0 / 0:00.0";
        FMODTitleLabel.Text = m_AudioClip.m_Name;
        FMODstatusLabel.Text = "Ready";
        FMODpauseButton.Content = "Pause";
        StatusStripUpdate($"Loaded audio metadata: {m_AudioClip.m_Name}");
    }

    private void SetInitialVideoAudioLabel(VideoClip videoClip)
    {
        if (VideoAudioLabel == null)
        {
            return;
        }

        byte[]? data = null;
        try
        {
            data = videoClip.m_VideoData.GetData();
        }
        catch {}

        bool fileHasAudio = VideoAudioProber.HasAudio(data);

        if (videoClip.HasAudio)
        {
            if (!fileHasAudio)
            {
                VideoAudioLabel.Text = "Audio: yes | No audio stream in file";
                VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#ff9800"));
                return;
            }

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
            VideoAudioLabel.Text = sb.ToString();
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#00e676"));
        }
        else
        {
            VideoAudioLabel.Text = "Audio: no playable track found";
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#ff9800"));
        }
    }

    private static class VideoAudioProber
    {
        public static bool HasAudio(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 8)
            {
                return false;
            }

            // 1. Detect format
            // WebM / Matroska magic: 1A 45 DF A3
            if (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            {
                return WebMHasAudio(bytes);
            }

            // MP4 magic: check for 'ftyp' box type in the first 16 bytes
            if (bytes.Length >= 16 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                return Mp4HasAudio(bytes);
            }

            // Fallback: default to true if we don't recognize or support the format
            return true;
        }

        private static bool WebMHasAudio(byte[] bytes)
        {
            // Scan the first 128KB for the TrackType = Audio EBML element
            // TrackType ID is 0x83, size is 0x81 (VINT size 1), audio value is 0x02.
            int scanLength = Math.Min(bytes.Length, 128 * 1024);
            for (int i = 0; i < scanLength - 2; i++)
            {
                if (bytes[i] == 0x83 && bytes[i + 1] == 0x81 && bytes[i + 2] == 0x02)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Mp4HasAudio(byte[] bytes)
        {
            int offset = 0;
            try
            {
                return ScanMp4BoxesForAudio(bytes, ref offset, bytes.Length);
            }
            catch
            {
                return false;
            }
        }

        private static bool ScanMp4BoxesForAudio(byte[] bytes, ref int offset, int endOffset)
        {
            while (offset + 8 <= endOffset)
            {
                int startOffset = offset;
                
                // Read box size (4 bytes)
                long size = (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);
                
                // Read box type (4 bytes)
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                offset += 8;

                if (size == 1)
                {
                    // 64-bit size
                    if (offset + 8 <= endOffset)
                    {
                        size = ((long)bytes[offset] << 56) | ((long)bytes[offset + 1] << 48) |
                               ((long)bytes[offset + 2] << 40) | ((long)bytes[offset + 3] << 32) |
                               ((long)bytes[offset + 4] << 24) | ((long)bytes[offset + 5] << 16) |
                               ((long)bytes[offset + 6] << 8) | bytes[offset + 7];
                        offset += 8;
                    }
                }
                else if (size == 0)
                {
                    // Extends to the end of the file
                    size = endOffset - startOffset;
                }

                long boxContentEnd = startOffset + size;
                if (boxContentEnd > endOffset || boxContentEnd <= startOffset)
                {
                    boxContentEnd = endOffset;
                }

                if (type == "moov" || type == "trak" || type == "mdia")
                {
                    // Recurse into nested container boxes
                    int localOffset = offset;
                    if (ScanMp4BoxesForAudio(bytes, ref localOffset, (int)boxContentEnd))
                    {
                        return true;
                    }
                }
                else if (type == "hdlr")
                {
                    // Handler Reference Box
                    if (offset + 12 <= boxContentEnd)
                    {
                        string handlerType = Encoding.ASCII.GetString(bytes, offset + 8, 4);
                        if (handlerType == "soun")
                        {
                            return true;
                        }
                    }
                }

                offset = (int)boxContentEnd;
            }
            return false;
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

    private void VideoReset()
    {
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

        SetVideoStoppedUi();
    }

    private void SetVideoStoppedUi()
    {
        VideoStatusLabel.Text = "Stopped";
        VideoPlayButton.Content = "Play";
        VideoProgressBar.Value = 0;
        VideoTimerLabel.Text = "0:00.0 / 0:00.0";
    }

    private void PreviewVideoClip(AssetItem assetItem, VideoClip m_VideoClip)
    {
        currentPreviewVideoClip = m_VideoClip;

        VideoTitleLabel.Text = m_VideoClip.m_Name;
        VideoStatusLabel.Text = "Ready";
        VideoResolutionLabel.Text = $"Resolution: {m_VideoClip.m_Width}x{m_VideoClip.m_Height} (Proxy: {m_VideoClip.m_ProxyWidth}x{m_VideoClip.m_ProxyHeight})";
        VideoFrameRateLabel.Text = $"Frame Rate: {m_VideoClip.m_FrameRate:F2} FPS | Frames: {m_VideoClip.m_FrameCount}";
        
        var ext = Path.GetExtension(m_VideoClip.m_OriginalPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        VideoFormatLabel.Text = $"Format: {ext.ToUpperInvariant().TrimStart('.')}";

        SetInitialVideoAudioLabel(m_VideoClip);

        VideoInfoLabel.Text = "Ready. Press Play to load embedded native preview.";
        VideoPlayButton.IsEnabled = true;
        VideoStopButton.IsEnabled = true;
        VideoExportButton.IsEnabled = true;
        VideoVolumeBar.Value = 80;

        VideoClipPanel.IsVisible = true;
        PreviewLabel.IsVisible = false;
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

    private void VideoPlayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPreviewVideoClip == null) return;

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
                var hasLoadedFile = FfmpegVideoPlayer.HasMediaLoaded
                    && !string.IsNullOrEmpty(_currentTempVideoPath)
                    && File.Exists(_currentTempVideoPath);

                if (!hasLoadedFile)
                {
                    if (!EnsureVideoPreviewFile(currentPreviewVideoClip))
                    {
                        return;
                    }

                    FfmpegVideoPlayer.Open(_currentTempVideoPath!);
                }

                FfmpegVideoPlayer.Volume = _targetVolume;
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

    private bool EnsureVideoPreviewFile(VideoClip videoClip)
    {
        if (!string.IsNullOrEmpty(_currentTempVideoPath) && File.Exists(_currentTempVideoPath))
        {
            return true;
        }

        var ext = Path.GetExtension(videoClip.m_OriginalPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        var data = videoClip.m_VideoData.GetData();
        if (data == null || data.Length == 0)
        {
            VideoInfoLabel.Text = "VideoClip data is empty or invalid.";
            VideoStatusLabel.Text = "Missing";
            VideoPlayButton.Content = "Play";
            StatusStripUpdate("VideoClip data is empty or invalid.");
            return false;
        }

        var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var oldFile in Directory.GetFiles(tempDir, "temp_video_*"))
            {
                try { File.Delete(oldFile); } catch {}
            }
        }
        catch {}

        _currentTempVideoPath = Path.Combine(tempDir, $"temp_video_{FixFileName(videoClip.m_Name)}_{videoClip.m_PathID}{ext}");
        File.WriteAllBytes(_currentTempVideoPath, data);

        _videoLengthMs = 0;
        VideoInfoLabel.Text = "Embedded FFmpeg preview loaded.";
        StatusStripUpdate($"Loaded video clip with FFmpeg backend: {videoClip.m_Name}");
        return true;
    }

    private void VideoStopButton_Click(object? sender, RoutedEventArgs e)
    {
        VideoStop();
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
        if (_isUpdatingVideoProgress)
            return;

        FfmpegVideoPlayer.Seek((float)(VideoProgressBar.Value / 1000.0));
    }

    private void FMODprogressBar_DragStarted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isAudioDragging = true;
    }

    private void FMODprogressBar_DragCompleted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isAudioDragging = false;
        if (_audioMediaPlayer != null && _audioLengthMs > 0 && !useLinuxAudioFallback)
        {
            try
            {
                _audioMediaPlayer.Seek((float)(FMODprogressBar.Value / 1000.0));
            }
            catch {}
        }
    }

    private void VideoProgressBar_DragStarted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isVideoDragging = true;
    }

    private void VideoProgressBar_DragCompleted(object? sender, global::Avalonia.Input.VectorEventArgs e)
    {
        _isVideoDragging = false;
        FfmpegVideoPlayer.Seek((float)(VideoProgressBar.Value / 1000.0));
    }

    private async void VideoExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPreviewVideoClip == null) return;

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

        var extension = Path.GetExtension(currentPreviewVideoClip.m_OriginalPath);
        if (string.IsNullOrEmpty(extension)) extension = ".mp4";

        var exportFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));
        if (exportFolders == null || exportFolders.Count == 0) return;

        var savePath = exportFolders[0].Path.LocalPath;
        var fileName = FixFileName(currentPreviewVideoClip.m_Name) + extension;
        var filePath = Path.Combine(savePath, fileName);

        try
        {
            var data = currentPreviewVideoClip.m_VideoData.GetData();
            if (data == null || data.Length == 0)
            {
                StatusStripUpdate("VideoClip data is empty or invalid.");
                return;
            }

            await File.WriteAllBytesAsync(filePath, data);
            StatusStripUpdate($"Successfully exported video clip to: {filePath}");
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to export video: {ex.Message}");
        }
    }

    private void AnimPlayPauseBtn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GLPreviewControl != null)
        {
            if (GLPreviewControl.IsPlaying)
            {
                GLPreviewControl.PauseAnimation();
                AnimPlayPauseBtn.Content = "Play";
            }
            else
            {
                GLPreviewControl.PlayAnimation();
                AnimPlayPauseBtn.Content = "Pause";
            }
        }
    }

    private void AnimRestartBtn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GLPreviewControl != null)
        {
            GLPreviewControl.RestartAnimation();
            AnimPlayPauseBtn.Content = "Pause";
        }
    }
    #endregion
}

public sealed class AvaloniaAppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AssetStudio",
        "avalonia-settings.json");

    public string LoadFolderPath { get; set; } = string.Empty;
    public string ExportFolderPath { get; set; } = string.Empty;
    public string ProjectRoot { get; set; } = string.Empty;
    public string SpecifyUnityVersion { get; set; } = string.Empty;
    public bool ShowErrorMessage { get; set; } = false;
    public bool DisplayAll { get; set; } = false;
    public bool DisplayInfo { get; set; } = true;
    public bool EnablePreview { get; set; } = true;
    public double AvatarPreviewBoneScale { get; set; } = 1.0;
    public double AvatarPreviewMeshDensityPercent { get; set; } = 15.0;
    public ExportOptionsState ExportOptions { get; set; } = new();
    public string SelectedTheme { get; set; } = "Default";

    public static AvaloniaAppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AvaloniaAppSettings>(File.ReadAllText(SettingsPath)) ?? new AvaloniaAppSettings();
            }
        }
        catch
        {
        }

        return new AvaloniaAppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}

public class GameObjectNode : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<GameObjectNode> EmptyChildren = Array.Empty<GameObjectNode>();
    private List<GameObjectNode>? children;
    private bool isChecked;
    private bool isExpanded;
    private bool updatingChildren;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = string.Empty;
    public GameObject? GameObject { get; set; }
    public GameObjectNode? Parent { get; private set; }
    public IReadOnlyList<GameObjectNode> Children => children ?? EmptyChildren;
    public int ChildCount => children?.Count ?? 0;

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (updatingChildren || children == null) return;
            foreach (var child in children)
            {
                child.SetCheckedFromParent(value);
            }
        }
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public void AddChild(GameObjectNode child)
    {
        children ??= new List<GameObjectNode>();
        child.Parent = this;
        children.Add(child);
    }

    public void ExpandAncestors()
    {
        var node = Parent;
        while (node != null)
        {
            node.IsExpanded = true;
            node = node.Parent;
        }
    }

    private void SetCheckedFromParent(bool value)
    {
        updatingChildren = true;
        IsChecked = value;
        updatingChildren = false;

        if (children == null) return;
        foreach (var child in children)
        {
            child.SetCheckedFromParent(value);
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AssetItem
{
    private Object? _asset;
    public Object? Asset
    {
        get
        {
            if (_asset == null && Handle != null)
            {
                _asset = SourceFile?.assetsManager?.ResolveHandle(Handle);
            }
            return _asset;
        }
        set => _asset = value;
    }

    public AssetHandle? Handle { get; set; }
    public SerializedFile SourceFile { get; set; }
    public GameObjectNode? TreeNode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string TypeString { get; set; }
    public string DisplayType => GetDisplayType();
    public string UniqueID { get; set; } = string.Empty;
    public long PathID { get; set; }
    public string PathIDString { get; set; } = string.Empty;
    public long Size { get; set; }
    public long FullSize { get; set; }
    public ClassIDType Type { get; set; }

    public AssetItem(Object asset)
    {
        Asset = asset;
        SourceFile = asset.assetsFile;
        TypeString = asset.type.ToString() ?? string.Empty;
        Type = asset.type;
        PathID = asset.m_PathID;
        PathIDString = PathID.ToString(CultureInfo.InvariantCulture);
        Size = asset.byteSize;
        FullSize = asset.byteSize;
    }

    public AssetItem(AssetHandle handle)
    {
        Handle = handle;
        SourceFile = handle.SourceFile;
        TypeString = handle.Type.ToString() ?? string.Empty;
        Type = handle.Type;
        PathID = handle.PathID;
        PathIDString = PathID.ToString(CultureInfo.InvariantCulture);
        Size = handle.ByteSize;
        FullSize = handle.ByteSize;
        Name = handle.Name ?? string.Empty;
        Container = handle.Container ?? string.Empty;
    }

    private string GetDisplayType()
    {
        var display = TypeString ?? string.Empty;
        if (Type == ClassIDType.PrefabInstance)
        {
            display = "Prefab (Composite)";
        }
        else if (Type == ClassIDType.GameObject)
        {
            display = "GameObject (Hierarchy Node)";
        }
        else if (Type == ClassIDType.MonoBehaviour)
        {
            display = "MonoBehaviour (Script Instance)";
        }
        else if (Type == ClassIDType.Mesh)
        {
            display = "Mesh (Geometry)";
        }
        else if (Type == ClassIDType.Material)
        {
            display = "Material (Shader Settings)";
        }
        else if (IsComponentType(Type))
        {
            display = $"{TypeString} (Component)";
        }

        if (IsFbxSubAsset())
        {
            return $"{display} (FBX sub-asset)";
        }

        return display ?? string.Empty;
    }

    private bool IsComponentType(ClassIDType type)
    {
        return type == ClassIDType.Transform ||
               type == ClassIDType.MeshRenderer ||
               type == ClassIDType.MeshFilter ||
               type == ClassIDType.SkinnedMeshRenderer ||
               type == ClassIDType.Animator ||
               type == ClassIDType.Animation ||
               type == ClassIDType.Component ||
               type == ClassIDType.RectTransform ||
               type == ClassIDType.Behaviour ||
               type == ClassIDType.MonoBehaviour;
    }

    public bool IsFbxSubAsset()
    {
        if (string.IsNullOrEmpty(Container))
        {
            return false;
        }

        return Container
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
    }
}

public class AssetClassItem : INotifyPropertyChanged, IEquatable<AssetClassItem>
{
    private int _classID;
    private string _name = string.Empty;
    private string _namespace = string.Empty;
    private string _assembly = string.Empty;
    private string _unityVersion = string.Empty;
    private string _sourceFile = string.Empty;
    private string _sourceKind = string.Empty;
    private int _objectCount;
    private SerializedType _serializedType = null!;

    public int ClassID
    {
        get => _classID;
        set => SetProperty(ref _classID, value, nameof(ClassID));
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty, nameof(Name));
    }

    public string Namespace
    {
        get => _namespace;
        set => SetProperty(ref _namespace, value ?? string.Empty, nameof(Namespace));
    }

    public string Assembly
    {
        get => _assembly;
        set => SetProperty(ref _assembly, value ?? string.Empty, nameof(Assembly));
    }

    public string UnityVersion
    {
        get => _unityVersion;
        set => SetProperty(ref _unityVersion, value ?? string.Empty, nameof(UnityVersion));
    }

    public string SourceFile
    {
        get => _sourceFile;
        set => SetProperty(ref _sourceFile, value ?? string.Empty, nameof(SourceFile));
    }

    public string SourceKind
    {
        get => _sourceKind;
        set => SetProperty(ref _sourceKind, value ?? string.Empty, nameof(SourceKind));
    }

    public int ObjectCount
    {
        get => _objectCount;
        set => SetProperty(ref _objectCount, value, nameof(ObjectCount));
    }

    public SerializedType SerializedType
    {
        get => _serializedType;
        set => SetProperty(ref _serializedType, value, nameof(SerializedType));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CopyFrom(AssetClassItem other)
    {
        ClassID = other.ClassID;
        Name = other.Name;
        Namespace = other.Namespace;
        Assembly = other.Assembly;
        UnityVersion = other.UnityVersion;
        SourceFile = other.SourceFile;
        SourceKind = other.SourceKind;
        ObjectCount = other.ObjectCount;
        SerializedType = other.SerializedType;
    }

    public bool Equals(AssetClassItem? other)
    {
        return other != null
            && ClassID == other.ClassID
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
            && string.Equals(Assembly, other.Assembly, StringComparison.Ordinal)
            && string.Equals(UnityVersion, other.UnityVersion, StringComparison.Ordinal)
            && string.Equals(SourceFile, other.SourceFile, StringComparison.Ordinal)
            && string.Equals(SourceKind, other.SourceKind, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as AssetClassItem);

    public override int GetHashCode()
    {
        return HashCode.Combine(ClassID, Name, Namespace, Assembly, UnityVersion, SourceFile, SourceKind);
    }

    private bool SetProperty<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum ExportMode
{
    Convert,
    Raw,
    Dump
}

public class ProjectIndexCache
{
    public string RootPath { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public int UnityBundleCount { get; set; }
    public List<CachedAssetHandle> Handles { get; set; } = new List<CachedAssetHandle>();
}

public class CachedAssetHandle
{
    public string UniqueID { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Type { get; set; }
    public string Container { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string SerializedFileName { get; set; } = string.Empty;
    public long PathID { get; set; }
    public long ByteStart { get; set; }
    public long ByteSize { get; set; }
}
