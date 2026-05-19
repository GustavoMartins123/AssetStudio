using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private AssetsManager assetsManager = new AssetsManager();
    private List<AssetItem> exportableAssets = new List<AssetItem>();
    private List<AssetItem> visibleAssets = new List<AssetItem>();
    private List<GameObjectNode> sceneTreeNodes = new List<GameObjectNode>();
    private readonly List<GameObjectNode> treeSearchResults = new List<GameObjectNode>();
    private readonly ExportOptionsState exportOptions = new();
    private readonly AvaloniaAppSettings appSettings = AvaloniaAppSettings.Load();
    private readonly AssemblyLoader assemblyLoader = new AssemblyLoader();
    private CancellationTokenSource? listSearchDebounce;
    private string? assetListSortMember;
    private string assetContextCellText = string.Empty;
    private AssetItem? assetContextItem;
    private int nextGameObjectSearchIndex;
    private bool assetListSortDescending;
    private bool updatingFilterTypeMenu;

    public MainWindow()
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        InitializeComponent();
        Progress.Default = new Progress<int>(SetProgressBarValue);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
    }

    private void StatusStripUpdate(string text)
    {
        Dispatcher.UIThread.Post(() => statusLabel.Text = text);
    }

    private void SetProgressBarValue(int value)
    {
        Dispatcher.UIThread.Post(() => progressBar.Value = value);
    }

    private void ResetForm()
    {
        exportableAssets.Clear();
        visibleAssets.Clear();
        AssetListDataGrid.ItemsSource = null;
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
        PreviewLabel.IsVisible = true;
        PreviewLabel.Text = "[Preview Panel]";
        progressBar.Value = 0;
        ResetFilterTypeMenu();
        StatusStripUpdate("Ready");
    }

    private void ApplyUnityVersionOption()
    {
        assetsManager.SpecifyUnityVersion = SpecifyUnityVersionTextBox.Text?.Trim() ?? string.Empty;
    }

    private void ClearPreview(string message = "[Preview Panel]")
    {
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        PreviewLabel.Text = message;
        PreviewLabel.IsVisible = true;
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
            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(path));
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
        if (assetsManager.assetsFileList.Count > 0)
        {
            BuildAssetStructures();
        }
    }

    private void EnablePreview_Click(object? sender, RoutedEventArgs e)
    {
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
        if (AssetListDataGrid.SelectedItem is AssetItem selected)
        {
            PreviewLabel.Text = displayInfo.IsChecked == true
                ? $"{selected.TypeString}: {selected.Name}"
                : string.Empty;
            PreviewLabel.IsVisible = displayInfo.IsChecked == true && !TextPreviewBox.IsVisible;
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
        StatusStripUpdate($"Project root set to: {assetsManager.ProjectRoot}");
    }

    private async void ShowExportOptions_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ExportOptionsWindow(exportOptions.Clone());
        var result = await dialog.ShowDialog<ExportOptionsState?>(this);
        if (result == null) return;

        exportOptions.CopyFrom(result);
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
        FilterAssetList();
    }

    private void FilterType_Click(object? sender, RoutedEventArgs e)
    {
        if (updatingFilterTypeMenu) return;

        updatingFilterTypeMenu = true;
        filterTypeAll.IsChecked = !GetFilterTypeItems().Any(x => x.IsChecked == true);
        updatingFilterTypeMenu = false;
        FilterAssetList();
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
                        assetItem.Name = ((NamedObject)asset).m_Name;
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
            }
        }
        containers.Clear();
        objectAssetItemDic.Clear();

        visibleAssets = new List<AssetItem>(exportableAssets);
        BuildFilterTypeMenu();
        FilterAssetList();
        SceneTreeView.ItemsSource = sceneTreeNodes;

        var log = $"Finished loading {assetsManager.assetsFileList.Count} files with {exportableAssets.Count} exportable assets";
        var m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.Count);
        var objectsCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
        if (m_ObjectsCount != objectsCount)
        {
            log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
        }
        StatusStripUpdate(log);

        Title = $"AssetStudio - {assetsManager.assetsFileList[0].unityVersion} - {assetsManager.assetsFileList[0].m_TargetPlatform}";
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
            DumpTextBox.Text = dump ?? "No Dump Available";
        }
        catch (Exception ex)
        {
            DumpTextBox.Text = $"Dump {assetItem.Type}:{assetItem.Name} error{Environment.NewLine}{ex.Message}{Environment.NewLine}{ex.StackTrace}";
        }
    }

    private async Task<string?> DumpAsset(Object asset)
    {
        var dump = asset.Dump();
        if (dump == null && asset is MonoBehaviour monoBehaviour)
        {
            var typeTree = await MonoBehaviourToTypeTree(monoBehaviour);
            dump = monoBehaviour.Dump(typeTree);
        }
        return dump;
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
        PreviewLabel.IsVisible = displayInfo.IsChecked == true;
        PreviewLabel.Text = displayInfo.IsChecked == true ? $"{assetItem.TypeString}: {assetItem.Name}" : string.Empty;
        var dumpStr = assetItem.Asset.Dump();

        switch (assetItem.Asset)
        {
            case TextAsset m_TextAsset:
                TextPreviewBox.Text = Encoding.UTF8.GetString(m_TextAsset.m_Script).Replace("\0", string.Empty);
                TextPreviewBox.IsVisible = true;
                PreviewLabel.IsVisible = false;
                break;
            case Shader m_Shader:
                TextPreviewBox.Text = m_Shader.Convert() ?? "Serialized Shader can't be read";
                TextPreviewBox.IsVisible = true;
                PreviewLabel.IsVisible = false;
                break;
            case MonoBehaviour:
                if (dumpStr != null)
                {
                    TextPreviewBox.Text = dumpStr;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                }
                break;
            case VideoClip _:
            case MovieTexture _:
                StatusStripUpdate("Only supported export.");
                break;
            case Animator _:
                StatusStripUpdate("Can be exported to FBX file.");
                break;
            case AnimationClip _:
                StatusStripUpdate("Can be exported with Animator or Objects");
                break;
            default:
                if (dumpStr != null)
                {
                    TextPreviewBox.Text = dumpStr;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                }
                break;
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
                FilterAssetList();
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void AssetListDataGrid_Sorting(object? sender, DataGridColumnEventArgs e)
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
        ApplyAssetListSort();
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

    private void FilterAssetList()
    {
        IEnumerable<AssetItem> assets = exportableAssets;

        if (filterTypeAll.IsChecked != true)
        {
            var selectedTypes = GetFilterTypeItems()
                .Where(x => x.IsChecked == true && x.Tag is ClassIDType)
                .Select(x => (ClassIDType)x.Tag!)
                .ToHashSet();

            assets = selectedTypes.Count == 0
                ? Enumerable.Empty<AssetItem>()
                : assets.Where(x => selectedTypes.Contains(x.Type));
        }

        var filterText = listSearch?.Text?.Trim();
        if (!string.IsNullOrEmpty(filterText))
        {
            assets = assets.Where(x =>
                x.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                x.Container.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                x.TypeString.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                x.PathID.ToString(CultureInfo.InvariantCulture).Contains(filterText, StringComparison.OrdinalIgnoreCase));
        }

        visibleAssets = assets.ToList();
        ApplyAssetListSort();
    }

    private void ApplyAssetListSort()
    {
        visibleAssets = SortAssetList(visibleAssets).ToList();
        AssetListDataGrid.ItemsSource = visibleAssets;
        StatusStripUpdate($"Showing {visibleAssets.Count} assets");
    }

    private IEnumerable<AssetItem> SortAssetList(IEnumerable<AssetItem> assets)
    {
        return assetListSortMember switch
        {
            "PathID" => assetListSortDescending
                ? assets.OrderByDescending(x => x.PathID)
                : assets.OrderBy(x => x.PathID),
            "FullSize" or "Size" => assetListSortDescending
                ? assets.OrderByDescending(x => x.FullSize)
                : assets.OrderBy(x => x.FullSize),
            "Container" => SortByString(assets, x => x.Container),
            "DisplayType" or "Type" => SortByString(assets, x => x.DisplayType),
            "Name" => SortByString(assets, x => x.Name),
            _ => assets
        };
    }

    private IEnumerable<AssetItem> SortByString(IEnumerable<AssetItem> assets, Func<AssetItem, string> selector)
    {
        return assetListSortDescending
            ? assets.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase)
            : assets.OrderBy(selector, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetAssetCellText(AssetItem item, string? member)
    {
        return member switch
        {
            "Container" => item.Container,
            "DisplayType" or "Type" => item.DisplayType,
            "PathID" => item.PathID.ToString(CultureInfo.InvariantCulture),
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
        if (toExport.Count == 0)
        {
            StatusStripUpdate("No exportable assets loaded");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateExportFolderOptions("Select the save folder"));

        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
        SaveExportFolder(savePath);
        if (mode == ExportMode.Convert)
        {
            toExport = OrderConvertedAssetsForExport(toExport);
        }

        int total = toExport.Count;
        int exported = 0;
        int failed = 0;

        StatusStripUpdate($"Exporting {total} assets...");

        await Task.Run(() =>
        {
            for (int j = 0; j < total; j++)
            {
                var asset = toExport[j];
                try
                {
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
                                var dump = asset.Asset.Dump() ?? "";
                                File.WriteAllText(filePath, dump);
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
                    StatusStripUpdate($"Error exporting {asset.Name}: {ex.Message}");
                }

                var progress = (int)((j + 1.0) / total * 100);
                Dispatcher.UIThread.Post(() => progressBar.Value = progress);
            }
        });

        var status = exported == 0 ? "Nothing exported." : $"Finished exporting {exported} assets.";
        if (failed > 0) status += $" {failed} failed.";
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
        await Task.Run(() =>
        {
            IImported convert = selectedGameObjects.Count > 0
                ? new ModelConverter(animator.Name, selectedGameObjects, exportOptions.ConvertTextureFormat, clips)
                : new ModelConverter((Animator)animator.Asset, exportOptions.ConvertTextureFormat, clips);
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
        });

        if (exportOptions.OpenAfterExport)
        {
            OpenFolder(exportPath);
        }
        StatusStripUpdate($"Finished exporting {Path.GetFileName(exportFile)}");
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
        Directory.CreateDirectory(exportPath);
        var fileName = FixFileName(item.Name);

        switch (item.Asset)
        {
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
            default:
            {
                var filePath = Path.Combine(exportPath, fileName + ".dat");
                if (File.Exists(filePath)) return false;
                File.WriteAllBytes(filePath, item.Asset.GetRawData());
                return true;
            }
        }
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
}

public sealed class AvaloniaAppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AssetStudio",
        "avalonia-settings.json");

    public string LoadFolderPath { get; set; } = string.Empty;
    public string ExportFolderPath { get; set; } = string.Empty;

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
        Size = asset.byteSize;
        FullSize = asset.byteSize;
    }

    private string GetDisplayType()
    {
        if (IsFbxSubAsset())
        {
            return $"{TypeString} (FBX sub-asset)";
        }

        return TypeString;
    }

    private bool IsFbxSubAsset()
    {
        return !string.IsNullOrEmpty(Container)
            && string.Equals(Path.GetExtension(Container), ".fbx", StringComparison.OrdinalIgnoreCase);
    }
}

public enum ExportMode
{
    Convert,
    Raw,
    Dump
}