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
using AssetStudio.PInvoke;
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

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
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
    private List<AssetItem> visibleAssets = new List<AssetItem>();
    private List<AssetClassItem> assetClassItems = new List<AssetClassItem>();
    private List<AssetClassItem> visibleAssetClassItems = new List<AssetClassItem>();
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
    private List<Material>? allMaterialsCache;
    private Dictionary<AssetStudio.Object, AssetItem>? objectToAssetItemCache;

    private FMOD.System? fmodSystem;
    private FMOD.Sound? fmodSound;
    private FMOD.Channel? fmodChannel;
    private FMOD.SoundGroup? fmodMasterSoundGroup;
    private FMOD.MODE fmodLoopMode = FMOD.MODE.LOOP_OFF;
    private uint fmodLenMs;
    private float fmodVolume = 0.8f;
    private DispatcherTimer? fmodTimer;
    private bool fmodIsDragging = false;
    private byte[]? currentAudioData;

    private LibVLCSharp.Shared.LibVLC? _libVLC;
    private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
    private bool _videoIsDragging = false;
    private string? _currentTempVideoPath;

    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr DlOpen([MarshalAs(UnmanagedType.LPStr)] string fileName, int flags);

    [DllImport("libc.so.6", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value, int overwrite);

    private string? _pendingStatusText;
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
        Progress.Default = new Progress<int>(SetProgressBarValue);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Title = $"AssetStudio v{version}";

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64");
                var libVlcPath = Path.Combine(libDir, "libvlc.so");
                var libVlcCorePath = Path.Combine(libDir, "libvlccore.so");
                var pluginsPath = Path.Combine(libDir, "plugins");

                if (File.Exists(libVlcPath) && File.Exists(libVlcCorePath))
                {
                    try
                    {
                        SetEnv("VLC_PLUGIN_PATH", pluginsPath, 1);

                        NativeLibrary.SetDllImportResolver(typeof(LibVLCSharp.Shared.LibVLC).Assembly, (libraryName, assembly, searchPath) =>
                        {
                            if (libraryName == "libvlc")
                            {
                                try
                                {
                                    DlOpen(libVlcCorePath, 0x102); // 0x2 (RTLD_NOW) | 0x100 (RTLD_GLOBAL)
                                    var handle = DlOpen(libVlcPath, 0x102);
                                    if (handle != IntPtr.Zero)
                                    {
                                        return handle;
                                    }
                                }
                                catch { }
                            }
                            return IntPtr.Zero;
                        });
                    }
                    catch (Exception initEx)
                    {
                        logger.Log(LoggerEvent.Warning, $"Failed to setup bundled LibVLC resolver: {initEx.Message}. Falling back to system VLC.");
                    }
                }

                LibVLCSharp.Shared.Core.Initialize();
            }
            else
            {
                LibVLCSharp.Shared.Core.Initialize();
            }

            _libVLC = new LibVLCSharp.Shared.LibVLC();
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
            _mediaPlayer.PositionChanged += MediaPlayer_PositionChanged;
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            VideoPlayerView.MediaPlayer = _mediaPlayer;
        }
        catch (Exception ex)
        {
            var message = $"Failed to initialize LibVLC: {ex.Message}. Embedded video preview will be disabled.";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                message += " Hint: Ensure system VLC is installed using: sudo apt install vlc libvlc-dev";
            }
            logger.Log(LoggerEvent.Error, message);
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                NativeLibrary.SetDllImportResolver(typeof(FMOD.System).Assembly, (libraryName, assembly, searchPath) =>
                {
                    if (libraryName == "fmod" || libraryName == "fmod64")
                    {
                        var libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64");
                        var libFmodPath = Path.Combine(libDir, "libfmod.so");
                        if (File.Exists(libFmodPath))
                        {
                            if (NativeLibrary.TryLoad(libFmodPath, out var handle))
                            {
                                return handle;
                            }
                        }
                    }
                    return IntPtr.Zero;
                });
            }
            FMODinit();
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to initialize FMOD: {ex.Message}");
        }

        // Detect GPU support (OpenGL interface availability)
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

    private void ResetForm()
    {
        meshToMaterialsCache = null;
        allMaterialsCache = null;
        objectToAssetItemCache = null;
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
        progressBar.Value = 0;
        ResetFilterTypeMenu();
        StatusStripUpdate("Ready");

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        Title = $"AssetStudio v{version}";
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
            GLPreviewControl.BoneScale = 1.0f;
        }
        if (BoneSizeSlider != null)
        {
            BoneSizeSlider.Value = 1.0;
        }
        if (BoneSizeLabel != null)
        {
            BoneSizeLabel.Text = "1.0x";
        }
        if (BoneSizeContainer != null)
        {
            BoneSizeContainer.IsVisible = false;
        }
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
            FMODreset();
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            VideoReset();
        }
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        currentPreviewVideoClip = null;
        texturePreviewIdCounter++; // Cancel any running background image decoding task
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
        appSettings.EnablePreview = enablePreview.IsChecked == true;
        appSettings.Save();
        if (enablePreview.IsChecked != true)
        {
            ClearPreview("Preview disabled");
        }
        else if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            PreviewAsset(selected);
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
                IsChecked = false,
                Tag = type
            };
            item.Click += FilterType_Click;
            filterTypeMenu.Items.Add(item);
        }

        filterTypeAll.IsChecked = true;
        updatingFilterTypeMenu = false;
    }

    private void FilterTypeAll_Click(object? sender, RoutedEventArgs e)
    {
        if (updatingFilterTypeMenu) return;

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

        updatingFilterTypeMenu = true;
        filterTypeAll.IsChecked = !GetFilterTypeItems().Any(x => x.IsChecked == true);
        updatingFilterTypeMenu = false;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void ClassSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
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

        visibleAssetClassItems = classes.ToList();
        AssetClassesDataGrid.ItemsSource = visibleAssetClassItems;
    }

    private void AssetClassesDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AssetClassesDataGrid.SelectedItem is not AssetClassItem item)
            return;

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
            await Task.Run(() => assetsManager.LoadFiles(filePaths));
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
            SaveLoadFolder(folderPath);
            ResetForm();
            StatusStripUpdate("Loading folder...");
            assetsManager.Clear();
            ApplyUnityVersionOption();
            await Task.Run(() => assetsManager.LoadFolder(folderPath));
            BuildAssetStructures();
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
        ResetForm();
        assetsManager.Clear();
        ApplyUnityVersionOption();
        StatusStripUpdate("Loading dropped files...");

        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            await Task.Run(() => assetsManager.LoadFolder(paths[0]));
        }
        else
        {
            var files = paths
                .SelectMany(path => Directory.Exists(path)
                    ? Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    : new[] { path })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await Task.Run(() => assetsManager.LoadFiles(files));
        }

        BuildAssetStructures();
    }

    private int ExtractFolder(string path, string savePath)
    {
        var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
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

    private void BuildAssetStructures()
    {
        if (assetsManager.assetsFileList.Count == 0)
        {
            StatusStripUpdate("No Unity file can be loaded.");
            return;
        }

        string? productName = null;
        exportableAssets.Clear();
        sceneTreeNodes = new List<GameObjectNode>();
        treeSearchResults.Clear();
        nextGameObjectSearchIndex = 0;
        var objectCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
        var treeNodeDictionary = new Dictionary<GameObject, GameObjectNode>();
        var objectAssetItemDic = new Dictionary<Object, AssetItem>(objectCount);
        var containers = new List<(PPtr<Object>, string)>();

        int i = 0;

        foreach (var assetsFile in assetsManager.assetsFileList)
        {
            var fileNode = new GameObjectNode { Name = assetsFile.fileName };

            foreach (var asset in assetsFile.Objects)
            {
                var assetItem = new AssetItem(asset);
                assetItem.UniqueID = " #" + i;
                objectAssetItemDic[asset] = assetItem;
                var exportable = false;

                switch (asset)
                {
                    case GameObject m_GameObject:
                        assetItem.Name = m_GameObject.m_Name;

                        if (!treeNodeDictionary.TryGetValue(m_GameObject, out var currentNode))
                        {
                            currentNode = new GameObjectNode { Name = m_GameObject.m_Name, GameObject = m_GameObject };
                            treeNodeDictionary.Add(m_GameObject, currentNode);
                        }

                        var parentNode = fileNode;

                        if (m_GameObject.m_Transform != null && m_GameObject.m_Transform.m_Father.TryGet(out var m_Father))
                        {
                            if (m_Father.m_GameObject.TryGet(out var parentGameObject))
                            {
                                if (!treeNodeDictionary.TryGetValue(parentGameObject, out var parentGameObjectNode))
                                {
                                    parentGameObjectNode = new GameObjectNode { Name = parentGameObject.m_Name, GameObject = parentGameObject };
                                    treeNodeDictionary.Add(parentGameObject, parentGameObjectNode);
                                }
                                parentNode = parentGameObjectNode;
                            }
                        }

                        parentNode.AddChild(currentNode);
                        break;

                    case Texture2D m_Texture2D:
                        if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                            assetItem.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                        assetItem.Name = m_Texture2D.m_Name;
                        exportable = true;
                        break;
                    case AudioClip m_AudioClip:
                        if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                            assetItem.FullSize = asset.byteSize + m_AudioClip.m_Size;
                        assetItem.Name = m_AudioClip.m_Name;
                        exportable = true;
                        break;
                    case VideoClip m_VideoClip:
                        if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                            assetItem.FullSize = asset.byteSize + (long)m_VideoClip.m_ExternalResources.m_Size;
                        assetItem.Name = m_VideoClip.m_Name;
                        exportable = true;
                        break;
                    case Shader m_Shader:
                        assetItem.Name = m_Shader.m_ParsedForm?.m_Name ?? m_Shader.m_Name;
                        exportable = true;
                        break;
                    case Mesh _:
                    case Material _:
                    case TextAsset _:
                    case AnimationClip _:
                    case Font _:
                    case MovieTexture _:
                    case Sprite _:
                    case Avatar _:
                    case RuntimeAnimatorController _:
                        assetItem.Name = ((NamedObject)asset).m_Name;
                        exportable = true;
                        break;
                    case MonoScript m_MonoScript:
                        assetItem.Name = m_MonoScript.m_Name;
                        exportable = true;
                        break;
                    case Animator m_Animator:
                        if (m_Animator.m_GameObject.TryGet(out var gameObject))
                        {
                            assetItem.Name = gameObject.m_Name;
                        }
                        exportable = true;
                        break;
                    case MonoBehaviour m_MonoBehaviour:
                        if (m_MonoBehaviour.m_Name == "" && m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                        {
                            assetItem.Name = m_Script.m_ClassName;
                        }
                        else
                        {
                            assetItem.Name = m_MonoBehaviour.m_Name;
                        }
                        exportable = true;
                        break;
                    case AssetBundle m_AssetBundle:
                        foreach (var m_Container in m_AssetBundle.m_Container)
                        {
                            var preloadIndex = m_Container.Value.preloadIndex;
                            var preloadSize = m_Container.Value.preloadSize;
                            var preloadEnd = preloadIndex + preloadSize;
                            for (int k = preloadIndex; k < preloadEnd; k++)
                            {
                                containers.Add((m_AssetBundle.m_PreloadTable[k], m_Container.Key));
                            }
                        }
                        assetItem.Name = m_AssetBundle.m_Name;
                        break;
                    case ResourceManager m_ResourceManager:
                        foreach (var m_Container in m_ResourceManager.m_Container)
                        {
                            containers.Add((m_Container.Value, m_Container.Key));
                        }
                        break;
                    case PlayerSettings m_PlayerSettings:
                        productName = m_PlayerSettings.productName;
                        break;
                    case NamedObject m_NamedObject:
                        assetItem.Name = m_NamedObject.m_Name;
                        break;
                }

                if (string.IsNullOrEmpty(assetItem.Name))
                {
                    assetItem.Name = assetItem.TypeString + assetItem.UniqueID;
                }

                if (displayAll.IsChecked || exportable)
                {
                    exportableAssets.Add(assetItem);
                }
                i++;
            }

            if (fileNode.ChildCount > 0)
            {
                sceneTreeNodes.Add(fileNode);
            }
        }

        LinkAssetItemsToSceneNodes(treeNodeDictionary, objectAssetItemDic);
        foreach ((var pptr, var container) in containers)
        {
            if (pptr.TryGet(out var obj) && objectAssetItemDic.TryGetValue(obj, out var item))
            {
                item.Container = container;
                if (obj is Material material && string.IsNullOrEmpty(material.m_Name))
                {
                    var name = Path.GetFileNameWithoutExtension(container);
                    if (!string.IsNullOrEmpty(name))
                    {
                        item.Name = name;
                    }
                }
            }
        }
        LinkFbxSubAssetsToSceneNodes();
        containers.Clear();
        objectAssetItemDic.Clear();

        visibleAssets = new List<AssetItem>(exportableAssets);
        BuildFilterTypeMenu();
        _ = FilterAssetListAsync(CancellationToken.None);
        BuildAssetClasses();
        SceneTreeView.ItemsSource = sceneTreeNodes;

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
            if (!string.IsNullOrEmpty(productName))
            {
                Title = $"AssetStudio v{version} - {productName} - {firstFile.unityVersion} - {firstFile.m_TargetPlatform}";
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

    private void LinkAssetItemsToSceneNodes(Dictionary<GameObject, GameObjectNode> treeNodeDictionary, Dictionary<Object, AssetItem> objectAssetItemDic)
    {
        foreach (var assetsFile in assetsManager.assetsFileList)
        {
            foreach (var asset in assetsFile.Objects)
            {
                if (asset is not GameObject gameObject || !treeNodeDictionary.TryGetValue(gameObject, out var node))
                {
                    continue;
                }

                if (objectAssetItemDic.TryGetValue(gameObject, out var gameObjectItem))
                {
                    gameObjectItem.TreeNode = node;
                }

                foreach (var pptr in gameObject.m_Components)
                {
                    if (!pptr.TryGet(out var component))
                    {
                        continue;
                    }

                    if (objectAssetItemDic.TryGetValue(component, out var componentItem))
                    {
                        componentItem.TreeNode = node;
                    }

                    if (component is MeshFilter meshFilter
                        && meshFilter.m_Mesh.TryGet(out var mesh)
                        && objectAssetItemDic.TryGetValue(mesh, out var meshItem))
                    {
                        meshItem.TreeNode = node;
                    }
                    else if (component is SkinnedMeshRenderer skinnedMeshRenderer
                        && skinnedMeshRenderer.m_Mesh.TryGet(out var skinnedMesh)
                        && objectAssetItemDic.TryGetValue(skinnedMesh, out var skinnedMeshItem))
                    {
                        skinnedMeshItem.TreeNode = node;
                    }
                }
            }
        }
    }

    private async void AssetListDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AssetListDataGrid.SelectedItem is AssetItem assetItem)
        {
            if (RightTabControl.SelectedIndex == 1)
            {
                await UpdateDumpForSelectedAsset();
            }
            PreviewAsset(assetItem);
        }
        else
        {
            DumpTextBox.Text = string.Empty;
        }
    }

    private async void RightTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == RightTabControl && RightTabControl.SelectedIndex == 1)
        {
            await UpdateDumpForSelectedAsset();
        }
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

        DumpTextBox.Text = "Loading dump...";
        try
        {
            var dump = await DumpAsset(assetItem.Asset);
            SetTextWithTruncation(DumpTextBox, dump, "No Dump Available");
        }
        catch (Exception ex)
        {
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

    private void PreviewAsset(AssetItem assetItem)
    {
        if (enablePreview.IsChecked != true)
        {
            ClearPreview("Preview disabled");
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
        if (GLPreviewControl != null && assetItem.Asset is not Material)
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
            FMODreset();
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            VideoReset();
        }
        if (assetItem.Asset is not AnimationClip)
        {
            HideAnimationPlayback();
            GLPreviewControl?.StopAnimation();
            currentPreviewMesh = null;
            currentPreviewAvatar = null;
        }
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        currentPreviewVideoClip = null;

        PreviewLabel.IsVisible = displayInfo.IsChecked == true;
        PreviewLabel.Text = displayInfo.IsChecked == true ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;
        var dumpStr = assetItem.Asset.Dump();

        string fbxHeader = string.Empty;
        if (assetItem.DisplayType.Contains("FBX sub-asset"))
        {
            var fbxNodeName = assetItem.TreeNode != null ? assetItem.TreeNode.Name : "[None]";
            fbxHeader = $"[FBX Sub-Asset Container: {Path.GetFileName(assetItem.Container)}]" + Environment.NewLine +
                        $"Associated Scene Hierarchy Node: {fbxNodeName}" + Environment.NewLine +
                        $"(Right-click this item and choose 'Go to scene hierarchy' to view context)" + Environment.NewLine +
                        $"--------------------------------------------------" + Environment.NewLine + Environment.NewLine;
        }

        switch (assetItem.Asset)
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
                Texture2D? meshTexture = null;
                List<byte[]?>? subMeshTextures = null;
                List<int>? subMeshTexWidths = null;
                List<int>? subMeshTexHeights = null;

                if (GLPreviewControl != null)
                {
                    subMeshTextures = new();
                    subMeshTexWidths = new();
                    subMeshTexHeights = new();

                    var allMaterials = FindMaterialsForMesh(m_Mesh);
                    
                    if (m_Mesh.m_SubMeshes != null && m_Mesh.m_SubMeshes.Length > 0)
                    {
                        for (int i = 0; i < m_Mesh.m_SubMeshes.Length; i++)
                        {
                            byte[]? tb = null;
                            int tw = 0, th = 0;

                            if (i < allMaterials.Count && allMaterials[i] != null)
                            {
                                var tex = FindTextureForMaterial(allMaterials[i]!);
                                if (tex != null)
                                {
                                    try
                                    {
                                        var image = tex.ConvertToImage(true);
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
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        StatusStripUpdate($"Texture decode failed for submesh {i}: {ex.Message}");
                                    }
                                }
                            }
                            subMeshTextures.Add(tb);
                            subMeshTexWidths.Add(tw);
                            subMeshTexHeights.Add(th);
                        }
                    }
                    else
                    {
                        // Fallback if no submeshes (shouldn't happen for valid meshes)
                        byte[]? tb = null;
                        int tw = 0, th = 0;
                        if (allMaterials.Count > 0 && allMaterials[0] != null)
                        {
                            var tex = FindTextureForMaterial(allMaterials[0]!);
                            if (tex != null)
                            {
                                try
                                {
                                    var image = tex.ConvertToImage(true);
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

                    global::OpenTK.Mathematics.Vector2[]? uvs = null;

                    if (m_Mesh.m_UV0 != null && m_Mesh.m_UV0.Length >= m_Mesh.m_VertexCount * 2)
                    {
                        uvs = new global::OpenTK.Mathematics.Vector2[m_Mesh.m_VertexCount];
                        for (int i = 0; i < m_Mesh.m_VertexCount; i++)
                        {
                            uvs[i] = new global::OpenTK.Mathematics.Vector2(m_Mesh.m_UV0[i * 2], m_Mesh.m_UV0[i * 2 + 1]);
                        }
                    }

                    currentPreviewMesh = m_Mesh;
                    GLPreviewControl.SetMesh(m_Mesh, uvs, subMeshTextures, subMeshTexWidths, subMeshTexHeights);
                    GLPreviewControl.IsVisible = true;
                    if (BoneSizeContainer != null)
                    {
                        BoneSizeContainer.IsVisible = false;
                    }
                    GLPreviewControl.Focus();
                }
                if (displayInfo.IsChecked == true && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                {
                    PreviewInfoOverlay.Text = "Loading details...";
                    PreviewInfoBorder.IsVisible = true;
                    var localAssetItem = assetItem;
                    var localMesh = m_Mesh;
                    Task.Run(() =>
                    {
                        var infoText = FormatMeshPreview(localMesh, localAssetItem);
                        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (AssetListDataGrid.SelectedItem == localAssetItem && PreviewInfoOverlay != null)
                            {
                                PreviewInfoOverlay.Text = infoText;
                            }
                        });
                    });
                }
                PreviewLabel.IsVisible = false;
                if (subMeshTextures != null && subMeshTextures.Any(t => t != null))
                {
                    StatusStripUpdate("OpenGL Preview | 'Ctrl W'=Wireframe | 'Ctrl N'=ReNormal | 'Ctrl S'=Textured/Shaded");
                }
                else
                {
                    StatusStripUpdate("OpenGL Preview | No texture found for this mesh | 'Ctrl W'=Wireframe | 'Ctrl N'=ReNormal");
                }
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
                if (dumpStr != null)
                {
                    SetTextWithTruncation(TextPreviewBox, fbxHeader + dumpStr);
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                }
                break;
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
            if (BoneSizeContainer != null)
            {
                BoneSizeContainer.IsVisible = true;
            }
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

        if (currentPreviewMesh != null && currentPreviewMesh.m_BindPose != null && currentPreviewMesh.m_BindPose.Length > 0
            && currentPreviewMesh.m_BoneNameHashes != null && currentPreviewMesh.m_BoneNameHashes.Length > 0)
        {
            avatarMesh = currentPreviewMesh;
            avatar = currentPreviewAvatar ?? FindBestAvatarForMesh(avatarMesh);
        }
        else if (currentPreviewAvatar != null && currentPreviewAvatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
        {
            avatar = currentPreviewAvatar;
            avatarMesh = FindBestMeshForAvatar(avatar);
        }

        if (avatar == null)
        {
            avatar = clip.assetsFile.Objects.OfType<Avatar>().FirstOrDefault();
        }
        if (avatar == null)
        {
            var clipNameBase = clip.m_Name.Split('_')[0];
            avatar = assetsManager.assetsFileList
                .SelectMany(f => f.Objects)
                .OfType<Avatar>()
                .FirstOrDefault(a => a.m_Name.Contains(clipNameBase, StringComparison.OrdinalIgnoreCase)
                                  || clipNameBase.Contains(a.m_Name.Replace("Avatar", ""), StringComparison.OrdinalIgnoreCase));
        }
        if (avatar == null)
        {
            avatar = assetsManager.assetsFileList
                .SelectMany(f => f.Objects)
                .OfType<Avatar>()
                .FirstOrDefault();
        }

        if (avatar == null || avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            StatusStripUpdate("AnimationClip: No Avatar found to preview animation.");
            return;
        }

        if (avatarMesh == null)
        {
            avatarMesh = FindBestMeshForAvatar(avatar);
        }

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

        var muscleClip = clip.m_MuscleClip;
        if (muscleClip?.m_Clip == null)
        {
            StatusStripUpdate("AnimationClip: No muscle clip data.");
            return;
        }

        var posTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        var rotTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>>();
        float maxTime = 0f;

        void AddKeyframe(int meshBoneIdx, uint attribute, float time, float[] data, int offset)
        {
            if (time > maxTime) maxTime = time;
            if (attribute == 1) // Position
            {
                if (!posTracks.TryGetValue(meshBoneIdx, out var list)) posTracks[meshBoneIdx] = list = new();
                list.Add((time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2])));
            }
            else if (attribute == 2) // Rotation
            {
                if (!rotTracks.TryGetValue(meshBoneIdx, out var list)) rotTracks[meshBoneIdx] = list = new();
                list.Add((time, new global::OpenTK.Mathematics.Quaternion(data[offset], data[offset + 1], data[offset + 2], data[offset + 3])));
            }
        }

        if (muscleClip?.m_Clip != null)
        {
            var m_Clip = muscleClip.m_Clip;
            var bindings = clip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();

            if (bindings?.genericBindings != null)
            {
                void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
                {
                    var binding = bindings.FindBinding(curveIndexInStream);
                    if (binding == null)
                    {
                        currentIdxOut++;
                        return;
                    }
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        if (meshBoneHashToIdx.TryGetValue(binding.path, out int meshBoneIdx))
                        {
                            if (binding.attribute == 1 || binding.attribute == 3 || binding.attribute == 4)
                            {
                                AddKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                                currentIdxOut += 3;
                            }
                            else if (binding.attribute == 2)
                            {
                                AddKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                                currentIdxOut += 4;
                            }
                            else currentIdxOut++;
                        }
                        else
                        {
                            if (binding.attribute == 2) currentIdxOut += 4;
                            else if (binding.attribute == 1 || binding.attribute == 3 || binding.attribute == 4) currentIdxOut += 3;
                            else currentIdxOut++;
                        }
                    }
                    else
                    {
                        currentIdxOut++;
                    }
                }

                if (m_Clip.m_StreamedClip != null)
                {
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        var streamedValues = frame.keyList.Select(x => x.value).ToArray();
                        for (int cIdx = 0; cIdx < frame.keyList.Length;)
                        {
                            ProcessCurveData(frame.keyList[cIdx].index, frame.time, streamedValues, 0, ref cIdx);
                        }
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

        if (posTracks.Count == 0 && rotTracks.Count == 0)
        {
            // Fallback: show static bind pose with message
            if (GLPreviewControl != null)
            {
                GLPreviewControl.SetAvatar(avatarMesh, restBonePositions, meshParentIndices, meshBoneNames);
                GLPreviewControl.IsVisible = true;
                if (BoneSizeContainer != null)
                {
                    BoneSizeContainer.IsVisible = true;
                }
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

        var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            try { bindPoses[i] = bindPoseInverses[i].Inverted(); }
            catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
        }

        var restLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            int pIdx = meshParentIndices[i];
            if (pIdx >= 0 && pIdx < meshBoneCount)
            {
                // Local = BoneToModel * ModelToParent
                restLocals[i] = bindPoses[i] * bindPoseInverses[pIdx];
            }
            else
            {
                restLocals[i] = bindPoses[i];
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

                if (hasPos || hasRot)
                {
                    var pos = hasPos ? EvaluatePos(bIdx, t) : localMat.ExtractTranslation();
                    var rot = hasRot ? EvaluateRot(bIdx, t) : localMat.ExtractRotation();
                    var scale = localMat.ExtractScale();
                    
                    localMat = global::OpenTK.Mathematics.Matrix4.CreateScale(scale) * 
                               global::OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rot) * 
                               global::OpenTK.Mathematics.Matrix4.CreateTranslation(pos);
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

        // Step 6: Send to GL preview
        if (GLPreviewControl != null)
        {
            GLPreviewControl.SetAnimatedAvatar(avatarMesh, allFrames, allBoneMatrices, meshParentIndices, sampleRate, meshBoneNames);
            GLPreviewControl.IsVisible = true;
            if (BoneSizeContainer != null)
            {
                BoneSizeContainer.IsVisible = true;
            }
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;

            if (AnimationPlaybackPanel != null)
            {
                AnimationPlaybackPanel.IsVisible = true;
                AnimPlayPauseBtn.Content = "Pause";
                AnimFrameLabel.Text = $"Frame: 0/{frameCount}";
            }

            StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name} | Frames: {frameCount} | FPS: {sampleRate} | Tracks: {posTracks.Count + rotTracks.Count}");
        }
    }

    private Mesh? FindBestMeshForAvatar(Avatar avatar)
    {
        Mesh? bestMesh = null;
        int bestScore = 0;
        var avatarName = avatar.m_Name.Replace("Avatar", "").Trim();
        var allMeshes = assetsManager.assetsFileList
            .SelectMany(f => f.Objects)
            .OfType<Mesh>()
            .Where(m => m.m_VertexCount > 0);

        foreach (var mesh in allMeshes)
        {
            int score = 0;
            if (mesh.assetsFile == avatar.assetsFile) score += 20;

            if (mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length > 0
                && mesh.m_BindPose != null && mesh.m_BindPose.Length > 0)
            {
                score += mesh.m_BoneNameHashes.Length;
            }

            if (!string.IsNullOrEmpty(avatarName) && mesh.m_Name.Contains(avatarName, StringComparison.OrdinalIgnoreCase))
                score += 15;

            if (score > bestScore)
            {
                bestScore = score;
                bestMesh = mesh;
            }
        }

        return bestMesh;
    }

    private Avatar? FindBestAvatarForMesh(Mesh mesh)
    {
        Avatar? bestAvatar = null;
        int bestScore = 0;
        var meshName = mesh.m_Name.ToLowerInvariant();
        var allAvatars = assetsManager.assetsFileList
            .SelectMany(f => f.Objects)
            .OfType<Avatar>();

        foreach (var avatar in allAvatars)
        {
            int score = 0;
            if (avatar.assetsFile == mesh.assetsFile) score += 20;

            var avatarName = avatar.m_Name.Replace("Avatar", "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(avatarName) && meshName.Contains(avatarName))
                score += 15;

            if (score > bestScore)
            {
                bestScore = score;
                bestAvatar = avatar;
            }
        }
        return bestAvatar;
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
        if (displayMaterial.m_Shader.TryGet(out var s))
        {
            shader = s;
        }
        else
        {
            shader = assetsManager.assetsFileList
                .SelectMany(x => x.Objects)
                .FirstOrDefault(x => x.m_PathID == displayMaterial.m_Shader.m_PathID) as Shader;
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
            
            Texture2D? texture = null;
            if (texEnvValue != null && textureRef != null && !textureRef.IsNull)
            {
                texture = ResolveTexturePPtr(displayMaterial, textureRef);
            }

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
                        if (currentId == texturePreviewIdCounter)
                        {
                            if (GLPreviewControl != null)
                            {
                                GLPreviewControl.SetMaterialTexture(image);
                                GLPreviewControl.IsVisible = true;
                                if (BoneSizeContainer != null)
                                {
                                    BoneSizeContainer.IsVisible = false;
                                }
                                GLPreviewControl.Focus();
                            }
                            else
                            {
                                image.Dispose();
                            }

                            ImagePreviewBox.IsVisible = false;
                            TextPreviewBox.IsVisible = false;
                            PreviewLabel.IsVisible = false;

                            if (displayInfo.IsChecked == true)
                            {
                                PreviewInfoOverlay.Text = infoText;
                                PreviewInfoBorder.IsVisible = true;
                            }
                            else
                            {
                                PreviewInfoBorder.IsVisible = false;
                            }
                            StatusStripUpdate($"Material preview loaded: {previewTexture.m_Name}");
                        }
                        else
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

    private Material? ResolveMaterialForPreview(Material material)
    {
        var visited = new HashSet<Material>();
        while (material != null && visited.Add(material))
        {
            var hasTextureReference = (material.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                .Any(x => x.Value?.m_Texture != null && !x.Value.m_Texture.IsNull);
            if (hasTextureReference)
            {
                return material;
            }

            if (material.m_Parent != null)
            {
                if (material.m_Parent.TryGet(out var parent))
                {
                    material = parent;
                    continue;
                }
                else
                {
                    var parentGlobal = assetsManager.assetsFileList
                        .SelectMany(x => x.Objects)
                        .FirstOrDefault(x => x.m_PathID == material.m_Parent.m_PathID) as Material;
                    if (parentGlobal != null)
                    {
                        material = parentGlobal;
                        continue;
                    }
                }
            }

            break;
        }

        return null;
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
        var displayMaterial = ResolveMaterialForPreview(material) ?? material;
        if (displayMaterial.m_SavedProperties?.m_TexEnvs == null) return null;

        var slots = new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseColorTexture", "_Diffuse", "_AlbedoMap" };
        foreach (var slot in slots)
        {
            var env = displayMaterial.m_SavedProperties.m_TexEnvs.FirstOrDefault(x => x.Key == slot);
            if (env.Value?.m_Texture != null && !env.Value.m_Texture.IsNull)
            {
                var tex = ResolveTexturePPtr(displayMaterial, env.Value.m_Texture);
                if (tex != null) return tex;
            }
        }

        foreach (var env in displayMaterial.m_SavedProperties.m_TexEnvs)
        {
            if (NonDiffuseSlots.Contains(env.Key)) continue;
            if (env.Value?.m_Texture != null && !env.Value.m_Texture.IsNull)
            {
                var tex = ResolveTexturePPtr(displayMaterial, env.Value.m_Texture);
                if (tex != null) return tex;
            }
        }

        return null;
    }

    private Texture2D? ResolveTexturePPtr(Material material, PPtr<Texture> textureRef)
    {
        if (textureRef.TryGet<Texture2D>(out var directTex))
        {
            return directTex;
        }

        if (material.assetsFile.ObjectsDic.TryGetValue(textureRef.m_PathID, out var localObj) && localObj is Texture2D localTex)
        {
            return localTex;
        }

        if (textureRef.m_FileID > 0 && textureRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[textureRef.m_FileID - 1];
            var externalFileName = external.fileName?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(externalFileName))
            {
                var lastSlash = externalFileName.LastIndexOf('/');
                if (lastSlash >= 0) externalFileName = externalFileName.Substring(lastSlash + 1);

                foreach (var file in assetsManager.assetsFileList)
                {
                    var candidateName = file.fileName?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(candidateName)) continue;
                    var candidateLastSlash = candidateName.LastIndexOf('/');
                    if (candidateLastSlash >= 0) candidateName = candidateName.Substring(candidateLastSlash + 1);

                    if (string.Equals(candidateName, externalFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (file.ObjectsDic.TryGetValue(textureRef.m_PathID, out var obj) && obj is Texture2D tex)
                        {
                            return tex;
                        }
                    }
                }
            }
        }

        Texture2D? candidate = null;
        int bestScore = -1;
        var matTokens = GetPathTokens(material.m_Name);

        foreach (var file in assetsManager.assetsFileList)
        {
            if (file.ObjectsDic.TryGetValue(textureRef.m_PathID, out var obj) && obj is Texture2D tex)
            {
                if (file == material.assetsFile)
                {
                    return tex;
                }
                
                var texTokens = GetPathTokens(tex.m_Name);
                int score = matTokens.Intersect(texTokens, StringComparer.OrdinalIgnoreCase).Count() * 10;
                
                if (score > bestScore)
                {
                    bestScore = score;
                    candidate = tex;
                }
            }
        }
        return candidate;
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
        if (currentPreviewTexture == null && currentPreviewSprite == null)
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
                    bool isSprite = currentPreviewSprite != null;

                    if (currentPreviewTexture != null)
                    {
                        width = currentPreviewTexture.m_Width;
                        height = currentPreviewTexture.m_Height;

                        infoText = $"Width: {width}\nHeight: {height}\nFormat: {currentPreviewTexture.m_TextureFormat}";
                        switch (currentPreviewTexture.m_TextureSettings.m_FilterMode)
                        {
                            case 0: infoText += "\nFilter Mode: Point "; break;
                            case 1: infoText += "\nFilter Mode: Bilinear "; break;
                            case 2: infoText += "\nFilter Mode: Trilinear "; break;
                        }
                        infoText += $"\nAnisotropic level: {currentPreviewTexture.m_TextureSettings.m_Aniso}\nMip map bias: {currentPreviewTexture.m_TextureSettings.m_MipBias}";
                        switch (currentPreviewTexture.m_TextureSettings.m_WrapMode)
                        {
                            case 0: infoText += "\nWrap mode: Repeat"; break;
                            case 1: infoText += "\nWrap mode: Clamp"; break;
                        }
                    }
                    else if (currentPreviewSprite != null)
                    {
                        decodedImage = currentPreviewSprite.GetImage();
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
                                else if (currentPreviewTexture != null)
                                {
                                    TextureGLPreview.SetTexture(currentPreviewTexture);
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
                bool isTexture = currentPreviewTexture != null;

                if (currentPreviewTexture != null)
                {
                    image = currentPreviewTexture.ConvertToImage(true);
                    if (image != null)
                    {
                        infoText = $"Width: {currentPreviewTexture.m_Width}\nHeight: {currentPreviewTexture.m_Height}\nFormat: {currentPreviewTexture.m_TextureFormat}";
                        switch (currentPreviewTexture.m_TextureSettings.m_FilterMode)
                        {
                            case 0: infoText += "\nFilter Mode: Point "; break;
                            case 1: infoText += "\nFilter Mode: Bilinear "; break;
                            case 2: infoText += "\nFilter Mode: Trilinear "; break;
                        }
                        infoText += $"\nAnisotropic level: {currentPreviewTexture.m_TextureSettings.m_Aniso}\nMip map bias: {currentPreviewTexture.m_TextureSettings.m_MipBias}";
                        switch (currentPreviewTexture.m_TextureSettings.m_WrapMode)
                        {
                            case 0: infoText += "\nWrap mode: Repeat"; break;
                            case 1: infoText += "\nWrap mode: Clamp"; break;
                        }
                    }
                }
                else if (currentPreviewSprite != null)
                {
                    image = currentPreviewSprite.GetImage();
                    if (image != null)
                    {
                        infoText = $"Width: {image.Width}\nHeight: {image.Height}\n";
                    }
                }

                if (image == null)
                {
                    string failReason = "Unsupported image for preview";
                    if (currentPreviewTexture != null)
                    {
                        failReason = $"Unsupported Texture Format: {currentPreviewTexture.m_TextureFormat}";
                    }
                    else if (currentPreviewSprite != null)
                    {
                        if (currentPreviewSprite.m_SpriteAtlas != null && currentPreviewSprite.m_SpriteAtlas.TryGet(out var atlas) && atlas.m_RenderDataMap.TryGetValue(currentPreviewSprite.m_RenderDataKey, out var atlasData) && atlasData.texture.TryGet(out var tex1))
                            failReason = $"Unsupported Sprite Texture Format: {tex1.m_TextureFormat}";
                        else if (currentPreviewSprite.m_RD.texture.TryGet(out var tex2))
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
        listSearchDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        listSearchDebounce = debounce;

        try
        {
            await Task.Delay(800, debounce.Token);
            if (!debounce.IsCancellationRequested)
            {
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
        if (isSorting) return;
        isSorting = true;
        try
        {
            var sortMember = e.Column.SortMemberPath ?? e.Column.Header?.ToString();
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

            e.Column.Sort(assetListSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
            await ApplyAssetListSortAsync();
            
            e.Handled = true;
        }
        finally
        {
            isSorting = false;
        }
    }

    private void AssetListDataGrid_CellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.Row.DataContext is not AssetItem item)
        {
            return;
        }

        assetContextItem = item;
        assetContextCellText = GetAssetCellText(item, e.Column.SortMemberPath ?? e.Column.Header?.ToString());

        if (e.PointerPressedEventArgs.GetCurrentPoint(AssetListDataGrid).Properties.IsRightButtonPressed
            && !AssetListDataGrid.SelectedItems.Contains(item))
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

        try
        {
            var result = await Task.Run(() =>
            {
                var matches = new List<AssetItem>();
                foreach (var x in exportableAssets)
                {
                    token.ThrowIfCancellationRequested();

                    if (classFilter != null)
                    {
                        if ((int)x.Type != classFilter.ClassID || x.SourceFile.unityVersion != classFilter.UnityVersion)
                            continue;
                    }
                    else if (selectedTypes != null)
                    {
                        if (selectedTypes.Count == 0 || !selectedTypes.Contains(x.Type))
                            continue;
                    }

                    if (!string.IsNullOrEmpty(filterText))
                    {
                        if (!x.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) &&
                            !x.Container.Contains(filterText, StringComparison.OrdinalIgnoreCase) &&
                            !x.TypeString.Contains(filterText, StringComparison.OrdinalIgnoreCase) &&
                            !x.PathIDString.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    matches.Add(x);
                }

                token.ThrowIfCancellationRequested();
                return SortAssetListAsync(matches, sortMember, sortDescending).ToList();
            }, token);

            visibleAssets = result;
            AssetListDataGrid.ItemsSource = visibleAssets;
            StatusStripUpdate($"Showing {visibleAssets.Count} assets");
        }
        catch (OperationCanceledException)
        {
            // Task canceled, ignore
        }
    }

    private async Task ApplyAssetListSortAsync()
    {
        var sortMember = assetListSortMember;
        var sortDescending = assetListSortDescending;
        var currentAssets = visibleAssets;

        try
        {
            var sorted = await Task.Run(() => SortAssetListAsync(currentAssets, sortMember, sortDescending).ToList());
            visibleAssets = sorted;
            AssetListDataGrid.ItemsSource = visibleAssets;
            StatusStripUpdate($"Showing {visibleAssets.Count} assets");
        }
        catch (Exception ex)
        {
            Logger.Error("Error sorting asset list", ex);
        }
    }

    private IEnumerable<AssetItem> SortAssetListAsync(IEnumerable<AssetItem> assets, string? sortMember, bool descending)
    {
        return sortMember switch
        {
            "PathID" => descending
                ? assets.OrderByDescending(x => x.PathID)
                : assets.OrderBy(x => x.PathID),
            "FullSize" or "Size" => descending
                ? assets.OrderByDescending(x => x.FullSize).ThenBy(x => x.PathID)
                : assets.OrderBy(x => x.FullSize).ThenBy(x => x.PathID),
            "Container" => SortByStringAsync(assets, x => x.Container, descending),
            "DisplayType" or "Type" => SortByStringAsync(assets, x => x.DisplayType, descending),
            "Name" => SortByStringAsync(assets, x => x.Name, descending),
            _ => assets
        };
    }

    private IEnumerable<AssetItem> SortByStringAsync(IEnumerable<AssetItem> assets, Func<AssetItem, string?> selector, bool descending)
    {
        return descending
            ? assets.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.PathID)
            : assets.OrderBy(selector, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.PathID);
    }

    private static string GetAssetCellText(AssetItem item, string? member)
    {
        return member switch
        {
            "Container" => item.Container,
            "DisplayType" or "Type" => item.DisplayType,
            "PathID" => item.PathIDString,
            "FullSize" or "Size" => item.FullSize.ToString(CultureInfo.InvariantCulture),
            _ => item.Name
        };
    }

    private async void ExportAllAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets, ExportMode.Convert);
    private async void ExportSelectedAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Convert);
    private async void ExportFilteredAssets_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets, ExportMode.Convert);
    private async void ExportAllAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets, ExportMode.Raw);
    private async void ExportSelectedAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Raw);
    private async void ExportFilteredAssetsRaw_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets, ExportMode.Raw);
    private async void ExportAllAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(exportableAssets, ExportMode.Dump);
    private async void ExportSelectedAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(GetSelectedAssets(), ExportMode.Dump);
    private async void ExportFilteredAssetsDump_Click(object? sender, RoutedEventArgs e) => await ExportAssets(visibleAssets, ExportMode.Dump);
    private async void ExportAllAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(exportableAssets);
    private async void ExportSelectedAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(GetSelectedAssets());
    private async void ExportFilteredAssetsXML_Click(object? sender, RoutedEventArgs e) => await ExportAssetsList(visibleAssets);

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
        foreach (var item in AssetListDataGrid.SelectedItems)
        {
            if (item is AssetItem assetItem)
                selected.Add(assetItem);
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
        var fbxNodes = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in exportableAssets)
        {
            if (item.TreeNode?.GameObject == null)
            {
                continue;
            }

            var fbxContainer = GetFbxContainerPath(item.Container);
            if (fbxContainer == null)
            {
                continue;
            }

            fbxNodes.TryAdd(fbxContainer, GetFbxRootNode(item.TreeNode, fbxContainer));
        }

        foreach (var item in exportableAssets)
        {
            var fbxContainer = GetFbxContainerPath(item.Container);
            if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
            {
                continue;
            }

            var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
            var node = FindSceneNodeByName(fbxName);
            if (node?.GameObject != null)
            {
                fbxNodes[fbxContainer] = node;
            }
        }

        foreach (var item in exportableAssets)
        {
            var fbxContainer = GetFbxContainerPath(item.Container);
            if (fbxContainer == null || !fbxNodes.TryGetValue(fbxContainer, out var node))
            {
                continue;
            }

            item.TreeNode = node;
            if (item.Asset is Mesh or Animator)
            {
                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                if (!string.IsNullOrEmpty(fbxName))
                {
                    item.Name = fbxName;
                }
            }
        }
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

        classFilterOverride = item;
        ClearClassFilterButton.Content = $"Clear Class Filter ({item.Name} v{item.UnityVersion})";
        ClearClassFilterButton.IsVisible = true;

        LeftTabControl.SelectedIndex = 1;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void ClearClassFilter_Click(object? sender, RoutedEventArgs e)
    {
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

    private Material? ResolveMaterial(long pathID, string meshName)
    {
        Material? bestMat = null;
        int bestScore = -1;

        var meshTokens = GetPathTokens(meshName);

        foreach (var file in assetsManager.assetsFileList)
        {
            if (file.ObjectsDic.TryGetValue(pathID, out var obj) && obj is Material mat)
            {
                var matTokens = GetPathTokens(mat.m_Name);
                int score = meshTokens.Intersect(matTokens, StringComparer.OrdinalIgnoreCase).Count() * 10;
                
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMat = mat;
                }
            }
        }
        
        return bestMat;
    }

    private void BuildMeshToMaterialsCache()
    {
        meshToMaterialsCache = new Dictionary<Mesh, List<Material?>>();
        allMaterialsCache = new List<Material>();
        objectToAssetItemCache = new Dictionary<AssetStudio.Object, AssetItem>();

        foreach (var item in exportableAssets)
        {
            if (item.Asset != null)
            {
                objectToAssetItemCache[item.Asset] = item;
            }
        }

        AssetStudio.Object? ResolveObject(long pathID)
        {
            foreach (var file in assetsManager.assetsFileList)
            {
                if (file.ObjectsDic.TryGetValue(pathID, out var obj))
                {
                    return obj;
                }
            }
            return null;
        }

        foreach (var file in assetsManager.assetsFileList)
        {
            foreach (var obj in file.Objects)
            {
                if (obj is Material mat)
                {
                    allMaterialsCache.Add(mat);
                }
                else if (obj is SkinnedMeshRenderer smr)
                {
                    Mesh? smrMesh = null;
                    if (smr.m_Mesh.TryGet(out var m))
                    {
                        smrMesh = m;
                    }
                    else
                    {
                        smrMesh = ResolveObject(smr.m_Mesh.m_PathID) as Mesh;
                    }

                    if (smrMesh != null && smr.m_Materials != null)
                    {
                        if (!meshToMaterialsCache.ContainsKey(smrMesh))
                        {
                            var list = new List<Material?>();
                            meshToMaterialsCache[smrMesh] = list;
                            foreach (var matPtr in smr.m_Materials)
                            {
                                Material? resolvedMat = null;
                                if (matPtr.TryGet(out var mt))
                                {
                                    resolvedMat = mt;
                                }
                                else
                                {
                                    resolvedMat = ResolveMaterial(matPtr.m_PathID, smrMesh.m_Name);
                                }
                                list.Add(resolvedMat);
                            }
                        }
                    }
                }
                else if (obj is MeshRenderer mr)
                {
                    GameObject? go = null;
                    if (mr.m_GameObject.TryGet(out var g))
                    {
                        go = g;
                    }
                    else
                    {
                        go = ResolveObject(mr.m_GameObject.m_PathID) as GameObject;
                    }

                    if (go != null)
                    {
                        foreach (var compPtr in go.m_Components)
                        {
                            Component? comp = null;
                            if (compPtr.TryGet(out var cp))
                            {
                                comp = cp;
                            }
                            else
                            {
                                comp = ResolveObject(compPtr.m_PathID) as Component;
                            }

                            if (comp is MeshFilter mf)
                            {
                                Mesh? mfMesh = null;
                                if (mf.m_Mesh.TryGet(out var m))
                                {
                                    mfMesh = m;
                                }
                                else
                                {
                                    mfMesh = ResolveObject(mf.m_Mesh.m_PathID) as Mesh;
                                }

                                if (mfMesh != null && mr.m_Materials != null)
                                {
                                    if (!meshToMaterialsCache.ContainsKey(mfMesh))
                                    {
                                        var list = new List<Material?>();
                                        meshToMaterialsCache[mfMesh] = list;
                                        foreach (var matPtr in mr.m_Materials)
                                        {
                                            Material? resolvedMat = null;
                                            if (matPtr.TryGet(out var mt))
                                            {
                                                resolvedMat = mt;
                                            }
                                            else
                                            {
                                                resolvedMat = ResolveMaterial(matPtr.m_PathID, mfMesh.m_Name);
                                            }
                                            list.Add(resolvedMat);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private List<Material?> FindMaterialsForMesh(Mesh mesh)
    {
        if (meshToMaterialsCache == null || allMaterialsCache == null)
        {
            BuildMeshToMaterialsCache();
        }

        var materials = new List<Material?>();
        if (meshToMaterialsCache!.TryGetValue(mesh, out var cachedList))
        {
            materials.AddRange(cachedList);
        }

        if (materials.Count == 0 || (materials.Count == 1 && (materials[0] == null || materials[0]!.m_Name.StartsWith("Material") || materials[0]!.m_Name.Equals("Default", StringComparison.OrdinalIgnoreCase))))
        {
            var meshItem = objectToAssetItemCache!.GetValueOrDefault(mesh);
            var meshContainer = meshItem?.Container;
            var meshTokens = GetPathTokens(!string.IsNullOrEmpty(meshContainer) ? meshContainer : mesh.m_Name);

            Material? bestMat = null;
            int bestScore = 0;

            foreach (var mat in allMaterialsCache!)
            {
                if (mat.m_Name.StartsWith("Material") || mat.m_Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    continue;

                var matItem = objectToAssetItemCache!.GetValueOrDefault(mat);
                var matContainer = matItem?.Container;
                var matTokens = GetPathTokens(!string.IsNullOrEmpty(matContainer) ? matContainer : mat.m_Name);

                var overlap = meshTokens.Intersect(matTokens, StringComparer.OrdinalIgnoreCase).Count();
                int score = overlap * 10;

                // Priority 1: Same assetsFile (CAB) gets a very strong boost
                if (mat.assetsFile == mesh.assetsFile)
                {
                    score += 25;
                }

                // Priority 2: Substring overlap check: find if there's a common word or part (minimum 4 chars)
                bool hasSubstringMatch = false;
                if (mat.m_Name.Length >= 4 && mesh.m_Name.Length >= 4)
                {
                    string shorter = mat.m_Name.Length < mesh.m_Name.Length ? mat.m_Name : mesh.m_Name;
                    string longer = mat.m_Name.Length < mesh.m_Name.Length ? mesh.m_Name : mat.m_Name;
                    for (int len = 8; len >= 4; len--)
                    {
                        if (shorter.Length < len) continue;
                        for (int start = 0; start <= shorter.Length - len; start++)
                        {
                            var sub = shorter.Substring(start, len);
                            if (longer.Contains(sub, StringComparison.OrdinalIgnoreCase))
                            {
                                hasSubstringMatch = true;
                                break;
                            }
                        }
                        if (hasSubstringMatch) break;
                    }
                }

                if (hasSubstringMatch)
                {
                    score += 15;
                }

                if (score > 0)
                {
                    if (!string.IsNullOrEmpty(meshContainer) && !string.IsNullOrEmpty(matContainer))
                    {
                        var meshDir = Path.GetDirectoryName(meshContainer) ?? "";
                        var matDir = Path.GetDirectoryName(matContainer) ?? "";
                        if (string.Equals(meshDir, matDir, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 10;
                        }
                        else if (meshDir.StartsWith(matDir, StringComparison.OrdinalIgnoreCase) || matDir.StartsWith(meshDir, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 5;
                        }
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMat = mat;
                    }
                }
            }

            if (bestMat != null && bestScore > 0)
            {
                materials.Add(bestMat);
            }
        }

        return materials;
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

        bool usedBySkinnedMesh = false;
        bool usedByMeshFilter = false;
        var associatedRenderers = new List<string>();
        foreach (var file in assetsManager.assetsFileList)
        {
            foreach (var obj in file.Objects)
            {
                if (obj is SkinnedMeshRenderer smr)
                {
                    if (smr.m_Mesh.TryGet(out var m) && m == mesh)
                    {
                        usedBySkinnedMesh = true;
                        if (smr.m_GameObject.TryGet(out var go))
                            associatedRenderers.Add($"SkinnedMeshRenderer on GameObject \"{go.m_Name}\" (PathID: {smr.m_PathID})");
                    }
                }
                else if (obj is MeshFilter mf)
                {
                    if (mf.m_Mesh.TryGet(out var m) && m == mesh)
                    {
                        usedByMeshFilter = true;
                        if (mf.m_GameObject.TryGet(out var go))
                            associatedRenderers.Add($"MeshFilter on GameObject \"{go.m_Name}\" (PathID: {mf.m_PathID})");
                    }
                }
            }
        }

        var sourceTypes = new List<string>();
        if (usedBySkinnedMesh) sourceTypes.Add("SkinnedMeshRenderer");
        if (usedByMeshFilter) sourceTypes.Add("MeshFilter");
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
        FMODreset();
        if (fmodSystem != null)
        {
            fmodSystem.release();
            fmodSystem = null;
        }
        VideoReset();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        base.OnClosing(e);
    }

    #region FMOD
    private void FMODinit()
    {
        try
        {
            FMODreset();

            // Preload platform-specific FMOD native library (handling x64/x86 and Linux/Windows/macOS differences)
            DllLoader.PreloadDll("fmod");

            var result = FMOD.Factory.System_Create(out fmodSystem);
            if (ERRCHECK(result)) { return; }

            result = fmodSystem.getVersion(out var version);
            ERRCHECK(result);
            if (version < FMOD.VERSION.number)
            {
                logger.Log(LoggerEvent.Error, $"Error! You are using an old version of FMOD {version:X}. This program requires {FMOD.VERSION.number:X}.");
                return;
            }

            result = fmodSystem.init(2, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
            if (ERRCHECK(result)) { return; }

            result = fmodSystem.getMasterSoundGroup(out fmodMasterSoundGroup);
            if (ERRCHECK(result)) { return; }

            result = fmodMasterSoundGroup.setVolume(fmodVolume);
            if (ERRCHECK(result)) { return; }

            fmodTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            fmodTimer.Tick += FmodTimer_Tick;
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"FMOD could not be initialized: {ex.Message}. Audio preview will be disabled.");
        }
    }

    private void FmodTimer_Tick(object? sender, EventArgs e)
    {
        uint ms = 0;
        bool playing = false;
        bool paused = false;

        if (fmodChannel != null)
        {
            var result = fmodChannel.getPosition(out ms, FMOD.TIMEUNIT.MS);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                ERRCHECK(result);
            }

            result = fmodChannel.isPlaying(out playing);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                ERRCHECK(result);
            }

            result = fmodChannel.getPaused(out paused);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                ERRCHECK(result);
            }
        }

        if (!fmodIsDragging && fmodLenMs > 0)
        {
            FMODtimerLabel.Text = $"{ms / 1000 / 60}:{ms / 1000 % 60:D2}.{ms / 10 % 100:D2} / {fmodLenMs / 1000 / 60}:{fmodLenMs / 1000 % 60:D2}.{fmodLenMs / 10 % 100:D2}";
            FMODprogressBar.Value = (int)(ms * 1000 / fmodLenMs);
        }
        FMODstatusLabel.Text = paused ? "Paused" : playing ? "Playing" : "Stopped";

        if (fmodSystem != null && fmodChannel != null)
        {
            fmodSystem.update();
        }
    }

    private void FMODreset()
    {
        fmodTimer?.Stop();
        if (FMODprogressBar != null) FMODprogressBar.Value = 0;
        if (FMODtimerLabel != null) FMODtimerLabel.Text = "0:00.0 / 0:00.0";
        if (FMODstatusLabel != null) FMODstatusLabel.Text = "Stopped";
        if (FMODinfoLabel != null) FMODinfoLabel.Text = "";

        if (fmodSound != null && fmodSound.isValid())
        {
            var result = fmodSound.release();
            ERRCHECK(result);
            fmodSound = null;
        }
        currentAudioData = null;
    }

    private void FMODplayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (fmodSound != null && fmodChannel != null && fmodSystem != null)
        {
            fmodTimer?.Start();
            var result = fmodChannel.isPlaying(out var playing);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                if (ERRCHECK(result)) { return; }
            }

            if (playing)
            {
                result = fmodChannel.stop();
                if (ERRCHECK(result)) { return; }

                result = fmodSystem.playSound(fmodSound, null, false, out fmodChannel);
                if (ERRCHECK(result)) { return; }

                FMODpauseButton.Content = "Pause";
            }
            else
            {
                result = fmodSystem.playSound(fmodSound, null, false, out fmodChannel);
                if (ERRCHECK(result)) { return; }
                FMODstatusLabel.Text = "Playing";

                if (FMODprogressBar.Value > 0)
                {
                    uint newms = (uint)(fmodLenMs * (FMODprogressBar.Value / 1000.0));
                    result = fmodChannel.setPosition(newms, FMOD.TIMEUNIT.MS);
                    if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                    {
                        if (ERRCHECK(result)) { return; }
                    }
                }
            }
        }
    }

    private void FMODpauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (fmodSound != null && fmodChannel != null)
        {
            var result = fmodChannel.isPlaying(out var playing);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                if (ERRCHECK(result)) { return; }
            }

            if (playing)
            {
                result = fmodChannel.getPaused(out var paused);
                if (ERRCHECK(result)) { return; }
                result = fmodChannel.setPaused(!paused);
                if (ERRCHECK(result)) { return; }

                if (paused)
                {
                    FMODstatusLabel.Text = "Playing";
                    FMODpauseButton.Content = "Pause";
                    fmodTimer?.Start();
                }
                else
                {
                    FMODstatusLabel.Text = "Paused";
                    FMODpauseButton.Content = "Resume";
                    fmodTimer?.Stop();
                }
            }
        }
    }

    private void FMODstopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (fmodChannel != null)
        {
            var result = fmodChannel.isPlaying(out var playing);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                if (ERRCHECK(result)) { return; }
            }

            if (playing)
            {
                result = fmodChannel.stop();
                if (ERRCHECK(result)) { return; }
                fmodTimer?.Stop();
                FMODprogressBar.Value = 0;
                FMODtimerLabel.Text = "0:00.0 / 0:00.0";
                FMODstatusLabel.Text = "Stopped";
                FMODpauseButton.Content = "Pause";
            }
        }
    }

    private void FMODloopButton_Click(object? sender, RoutedEventArgs e)
    {
        fmodLoopMode = FMODloopButton.IsChecked == true ? FMOD.MODE.LOOP_NORMAL : FMOD.MODE.LOOP_OFF;

        if (fmodSound != null)
        {
            var result = fmodSound.setMode(fmodLoopMode);
            if (ERRCHECK(result)) { return; }
        }

        if (fmodChannel != null)
        {
            var result = fmodChannel.isPlaying(out var playing);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                if (ERRCHECK(result)) { return; }
            }

            result = fmodChannel.getPaused(out var paused);
            if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
            {
                if (ERRCHECK(result)) { return; }
            }

            if (playing || paused)
            {
                result = fmodChannel.setMode(fmodLoopMode);
                if (ERRCHECK(result)) { return; }
            }
        }
    }

    private void FMODvolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        fmodVolume = (float)(FMODvolumeBar.Value / 10.0);

        if (fmodMasterSoundGroup != null)
        {
            var result = fmodMasterSoundGroup.setVolume(fmodVolume);
            if (ERRCHECK(result)) { return; }
        }
    }

    private void FMODprogressBar_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        fmodIsDragging = true;
        fmodTimer?.Stop();
    }

    private void FMODprogressBar_PointerReleased(object? sender, global::Avalonia.Input.PointerReleasedEventArgs e)
    {
        fmodIsDragging = false;
        if (fmodChannel != null && fmodLenMs > 0)
        {
            uint newms = (uint)(fmodLenMs * (FMODprogressBar.Value / 1000.0));
            var result = fmodChannel.setPosition(newms, FMOD.TIMEUNIT.MS);
            if (!ERRCHECK(result))
            {
                result = fmodChannel.isPlaying(out var playing);
                if (playing)
                {
                    fmodTimer?.Start();
                }
            }
        }
    }

    private void FMODprogressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (fmodIsDragging && fmodLenMs > 0)
        {
            uint newms = (uint)(fmodLenMs * (FMODprogressBar.Value / 1000.0));
            FMODtimerLabel.Text = $"{newms / 1000 / 60}:{newms / 1000 % 60:D2}.{newms / 10 % 100:D2} / {fmodLenMs / 1000 / 60}:{fmodLenMs / 1000 % 60:D2}.{fmodLenMs / 10 % 100:D2}";
        }
    }

    private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
    {
        FMODreset();

        if (fmodSystem == null)
        {
            StatusStripUpdate("Audio preview is unavailable (FMOD is not loaded).");
            return;
        }

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

        currentAudioData = m_AudioClip.m_AudioData.GetData();
        if (currentAudioData == null || currentAudioData.Length == 0)
        {
            StatusStripUpdate("AudioClip data is empty or invalid.");
            return;
        }

        var exinfo = new FMOD.CREATESOUNDEXINFO();
        exinfo.cbsize = Marshal.SizeOf(exinfo);
        exinfo.length = (uint)m_AudioClip.m_Size;

        var result = fmodSystem.createSound(currentAudioData, FMOD.MODE.OPENMEMORY | fmodLoopMode, ref exinfo, out fmodSound);
        if (ERRCHECK(result)) return;

        fmodSound.getNumSubSounds(out var numsubsounds);
        if (numsubsounds > 0)
        {
            result = fmodSound.getSubSound(0, out var subsound);
            if (result == FMOD.RESULT.OK)
            {
                fmodSound = subsound;
            }
        }

        result = fmodSound.getLength(out fmodLenMs, FMOD.TIMEUNIT.MS);
        if (ERRCHECK(result)) return;

        result = fmodSystem.playSound(fmodSound, null, true, out fmodChannel);
        if (ERRCHECK(result)) return;

        FMODPanel.IsVisible = true;

        result = fmodChannel.getFrequency(out var frequency);
        if (ERRCHECK(result)) return;

        FMODinfoLabel.Text = $"{frequency} Hz | {infoText}";
        FMODtimerLabel.Text = $"0:00.0 / {fmodLenMs / 1000 / 60}:{fmodLenMs / 1000 % 60:D2}.{fmodLenMs / 10 % 100:D2}";
        FMODTitleLabel.Text = m_AudioClip.m_Name;
        FMODpauseButton.Content = "Pause";
        StatusStripUpdate($"Loaded audio: {m_AudioClip.m_Name}");
    }

    private bool ERRCHECK(FMOD.RESULT result)
    {
        if (result != FMOD.RESULT.OK)
        {
            FMODreset();
            StatusStripUpdate($"FMOD error! {result} - {FMOD.Error.String(result)}");
            return true;
        }
        return false;
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => {
            if (VideoLoopButton.IsChecked == true)
            {
                Task.Run(() => {
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.Stop();
                        _mediaPlayer.Play();
                    }
                });
            }
            else
            {
                VideoStop();
            }
        });
    }

    private void MediaPlayer_PositionChanged(object? sender, LibVLCSharp.Shared.MediaPlayerPositionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => {
            if (!_videoIsDragging)
            {
                VideoProgressBar.Value = e.Position * 1000;
            }
        });
    }

    private void MediaPlayer_TimeChanged(object? sender, LibVLCSharp.Shared.MediaPlayerTimeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => {
            if (_mediaPlayer != null)
            {
                long currentMs = e.Time;
                long totalMs = _mediaPlayer.Length;
                if (totalMs < 0) totalMs = 0;
                VideoTimerLabel.Text = $"{currentMs / 1000 / 60}:{currentMs / 1000 % 60:D2}.{currentMs / 10 % 100:D2} / {totalMs / 1000 / 60}:{totalMs / 1000 % 60:D2}.{totalMs / 10 % 100:D2}";
            }
        });
    }

    private void VideoStop()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
            }
            Dispatcher.UIThread.Post(() => {
                VideoStatusLabel.Text = "Stopped";
                VideoPlayButton.Content = "Play";
                VideoProgressBar.Value = 0;
                VideoTimerLabel.Text = "0:00.0 / 0:00.0";
            });
        }
        catch {}
    }

    private void VideoReset()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
                if (_mediaPlayer.Media != null)
                {
                    _mediaPlayer.Media.Dispose();
                    _mediaPlayer.Media = null;
                }
            }
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

        Dispatcher.UIThread.Post(() => {
            VideoStatusLabel.Text = "Stopped";
            VideoPlayButton.Content = "Play";
            VideoProgressBar.Value = 0;
            VideoTimerLabel.Text = "0:00.0 / 0:00.0";
        });
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

        var data = m_VideoClip.m_VideoData.GetData();
        if (data == null || data.Length == 0)
        {
            VideoInfoLabel.Text = "VideoClip data is empty or invalid.";
            VideoPlayButton.IsEnabled = false;
            VideoStopButton.IsEnabled = false;
            VideoExportButton.IsEnabled = false;
            VideoClipPanel.IsVisible = true;
            PreviewLabel.IsVisible = false;
            return;
        }

        VideoInfoLabel.Text = "Playing embedded native preview.";
        VideoPlayButton.IsEnabled = true;
        VideoStopButton.IsEnabled = true;
        VideoExportButton.IsEnabled = true;
        VideoVolumeBar.Value = 80;

        VideoClipPanel.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Loaded video clip: {m_VideoClip.m_Name}");

        try
        {
            VideoReset();

            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            Directory.CreateDirectory(tempDir);

            // Clean up older temp files
            try
            {
                foreach (var oldFile in Directory.GetFiles(tempDir, "temp_video_*"))
                {
                    try { File.Delete(oldFile); } catch {}
                }
            }
            catch {}

            _currentTempVideoPath = Path.Combine(tempDir, $"temp_video_{m_VideoClip.m_Name}_{m_VideoClip.m_PathID}{ext}");
            File.WriteAllBytes(_currentTempVideoPath, data);

            if (_mediaPlayer != null && _libVLC != null)
            {
                var media = new LibVLCSharp.Shared.Media(_libVLC, _currentTempVideoPath, LibVLCSharp.Shared.FromType.FromPath);
                _mediaPlayer.Media = media;
                _mediaPlayer.Volume = (int)VideoVolumeBar.Value;
                _mediaPlayer.Play();
                VideoStatusLabel.Text = "Playing";
                VideoPlayButton.Content = "Pause";
            }
        }
        catch (Exception ex)
        {
            VideoInfoLabel.Text = $"Failed to play video natively: {ex.Message}";
        }
    }

    private void VideoPlayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null || currentPreviewVideoClip == null) return;

        try
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                VideoStatusLabel.Text = "Paused";
                VideoPlayButton.Content = "Play";
            }
            else
            {
                if (_mediaPlayer.Media == null && !string.IsNullOrEmpty(_currentTempVideoPath))
                {
                    var media = new LibVLCSharp.Shared.Media(_libVLC!, _currentTempVideoPath, LibVLCSharp.Shared.FromType.FromPath);
                    _mediaPlayer.Media = media;
                }
                _mediaPlayer.Play();
                VideoStatusLabel.Text = "Playing";
                VideoPlayButton.Content = "Pause";
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to toggle playback: {ex.Message}");
        }
    }

    private void VideoStopButton_Click(object? sender, RoutedEventArgs e)
    {
        VideoStop();
    }

    private void VideoVolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Volume = (int)VideoVolumeBar.Value;
        }
    }

    private void VideoProgressBar_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        _videoIsDragging = true;
    }

    private void VideoProgressBar_PointerReleased(object? sender, global::Avalonia.Input.PointerReleasedEventArgs e)
    {
        _videoIsDragging = false;
        if (_mediaPlayer != null)
        {
            float pos = (float)(VideoProgressBar.Value / 1000.0);
            _mediaPlayer.Position = pos;
        }
    }

    private async void VideoExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPreviewVideoClip == null) return;

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
    public Object Asset { get; set; }
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
        TypeString = asset.type.ToString();
        Type = asset.type;
        PathID = asset.m_PathID;
        PathIDString = PathID.ToString(CultureInfo.InvariantCulture);
        Size = asset.byteSize;
        FullSize = asset.byteSize;
    }

    private string GetDisplayType()
    {
        var display = TypeString;
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

        return display;
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

public class AssetClassItem
{
    public int ClassID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Assembly { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public int ObjectCount { get; set; }
    public SerializedType SerializedType { get; set; } = null!;
}

public enum ExportMode
{
    Convert,
    Raw,
    Dump
}
