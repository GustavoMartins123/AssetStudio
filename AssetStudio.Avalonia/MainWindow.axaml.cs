using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private AssetsManager assetsManager = new AssetsManager();
    private List<AssetItem> exportableAssets = new List<AssetItem>();
    private List<AssetItem> visibleAssets = new List<AssetItem>();

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
        SceneTreeView.ItemsSource = null;
        DumpTextBox.Text = string.Empty;
        TextPreviewBox.Text = string.Empty;
        TextPreviewBox.IsVisible = false;
        PreviewLabel.IsVisible = true;
        PreviewLabel.Text = "[Preview Panel]";
        progressBar.Value = 0;
        StatusStripUpdate("Ready");
    }

    private async void LoadFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Game File",
            AllowMultiple = true
        });

        if (files != null && files.Count > 0)
        {
            var filePaths = files.Select(f => f.Path.LocalPath).ToArray();
            ResetForm();
            StatusStripUpdate("Loading files...");
            assetsManager.Clear();
            await Task.Run(() => assetsManager.LoadFiles(filePaths));
            BuildAssetStructures();
        }
    }

    private async void LoadFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Game Folder",
            AllowMultiple = false
        });

        if (folders != null && folders.Count > 0)
        {
            var folderPath = folders[0].Path.LocalPath;
            ResetForm();
            StatusStripUpdate("Loading folder...");
            assetsManager.Clear();
            await Task.Run(() => assetsManager.LoadFolder(folderPath));
            BuildAssetStructures();
        }
    }

    private async void ExtractFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select bundle or web file",
            AllowMultiple = true
        });
        if (files == null || files.Count == 0) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the save folder",
            AllowMultiple = false
        });
        if (folders == null || folders.Count == 0) return;

        var filePaths = files.Select(x => x.Path.LocalPath).Where(File.Exists).ToArray();
        var savePath = folders[0].Path.LocalPath;
        StatusStripUpdate("Extracting files...");
        var extractedCount = await Task.Run(() => ExtractFiles(filePaths, savePath));
        StatusStripUpdate($"Finished extracting {extractedCount} files.");
    }

    private async void ExtractFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var sourceFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder to extract",
            AllowMultiple = false
        });
        if (sourceFolders == null || sourceFolders.Count == 0) return;

        var saveFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the save folder",
            AllowMultiple = false
        });
        if (saveFolders == null || saveFolders.Count == 0) return;

        var sourcePath = sourceFolders[0].Path.LocalPath;
        var savePath = saveFolders[0].Path.LocalPath;
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
        var treeNodeCollection = new ObservableCollection<GameObjectNode>();
        var treeNodeDictionary = new Dictionary<GameObject, GameObjectNode>();
        var containers = new List<(PPtr<Object>, string)>();

        int i = 0;
        var objectCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);

        foreach (var assetsFile in assetsManager.assetsFileList)
        {
            var fileNode = new GameObjectNode { Name = assetsFile.fileName };

            foreach (var asset in assetsFile.Objects)
            {
                var assetItem = new AssetItem(asset);
                assetItem.UniqueID = " #" + i;
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

                        parentNode.Children.Add(currentNode);
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

            if (fileNode.Children.Count > 0)
            {
                treeNodeCollection.Add(fileNode);
            }
        }

        var objectAssetItemDic = exportableAssets.ToDictionary(x => x.Asset);
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
        AssetListDataGrid.ItemsSource = visibleAssets;
        SceneTreeView.ItemsSource = treeNodeCollection;

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

    private void AssetListDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AssetListDataGrid.SelectedItem is AssetItem assetItem)
        {
            var dumpStr = assetItem.Asset.Dump();
            DumpTextBox.Text = dumpStr ?? "No Dump Available";

            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = true;
            PreviewLabel.Text = $"{assetItem.TypeString}: {assetItem.Name}";

            switch (assetItem.Asset)
            {
                case TextAsset m_TextAsset:
                    TextPreviewBox.Text = Encoding.UTF8.GetString(m_TextAsset.m_Script);
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    break;
                case Shader m_Shader:
                    TextPreviewBox.Text = m_Shader.m_Script != null ? Encoding.UTF8.GetString(m_Shader.m_Script) : "No shader script";
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
    }

    private void ListSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        FilterAssetList();
    }

    private void FilterAssetList()
    {
        var filterText = listSearch?.Text?.Trim();
        if (string.IsNullOrEmpty(filterText))
        {
            visibleAssets = new List<AssetItem>(exportableAssets);
        }
        else
        {
            visibleAssets = exportableAssets.Where(x =>
                x.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                x.Container.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                x.TypeString.Contains(filterText, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
        AssetListDataGrid.ItemsSource = visibleAssets;
        StatusStripUpdate($"Showing {visibleAssets.Count} assets");
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
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the save folder"
        });

        if (folders == null || folders.Count == 0) return;

        var savePath = folders[0].Path.LocalPath;
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
                    var typePath = Path.Combine(savePath, asset.TypeString);
                    Directory.CreateDirectory(typePath);
                    var fileName = FixFileName(asset.Name);
                    var filePath = Path.Combine(typePath, fileName);

                    switch (mode)
                    {
                        case ExportMode.Raw:
                            filePath += ".dat";
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
                            if (ExportConvertFile(asset, typePath))
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
    }

    private bool ExportConvertFile(AssetItem item, string exportPath)
    {
        Directory.CreateDirectory(exportPath);
        var fileName = FixFileName(item.Name);

        switch (item.Asset)
        {
            case Texture2D m_Texture2D:
            {
                var image = m_Texture2D.ConvertToImage(true);
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
            case AudioClip m_AudioClip:
            {
                var m_AudioData = m_AudioClip.m_AudioData.GetData();
                if (m_AudioData == null || m_AudioData.Length == 0) return false;
                var converter = new AudioClipConverter(m_AudioClip);
                if (converter.IsSupport)
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

public class GameObjectNode
{
    public string Name { get; set; } = string.Empty;
    public GameObject? GameObject { get; set; }
    public ObservableCollection<GameObjectNode> Children { get; } = new ObservableCollection<GameObjectNode>();
}

public class AssetItem
{
    public Object Asset { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string TypeString { get; set; }
    public string UniqueID { get; set; } = string.Empty;
    public long PathID { get; set; }
    public long Size { get; set; }
    public long FullSize { get; set; }
    public ClassIDType Type { get; set; }

    public AssetItem(Object asset)
    {
        Asset = asset;
        TypeString = asset.type.ToString();
        Type = asset.type;
        PathID = asset.m_PathID;
        Size = asset.byteSize;
        FullSize = asset.byteSize;
    }
}

public enum ExportMode
{
    Convert,
    Raw,
    Dump
}