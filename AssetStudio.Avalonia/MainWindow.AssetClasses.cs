using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void ClassSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        PrioritizeUserInteraction();
        FilterAssetClasses();
    }

    private void BuildAssetClasses()
    {
        var items = new List<AssetClassItem>();

        var objectCounts = assetsManager.assetsFileList
            .SelectMany(file => file.Objects.Select(obj => new { file.unityVersion, ClassID = (int)obj.type }))
            .GroupBy(x => (x.unityVersion, x.ClassID))
            .ToDictionary(x => x.Key, x => x.Count());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assetsFile in assetsManager.assetsFileList)
        {
            AddSerializedTypes(assetsFile, assetsFile.m_Types, "Native", objectCounts, seen, items);
            AddSerializedTypes(assetsFile, assetsFile.m_RefTypes, "Reference", objectCounts, seen, items);
        }

        items = items
            .OrderBy(x => x.UnityVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ClassID)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ViewModel.AssetList.SetAssetClasses(items);
        FilterAssetClasses();
    }

    private void AddSerializedTypes(SerializedFile assetsFile, IEnumerable<SerializedType>? types, string sourceKind,
        Dictionary<(string UnityVersion, int ClassID), int> objectCounts, HashSet<string> seen, List<AssetClassItem> targetList)
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
            targetList.Add(new AssetClassItem
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
        // Save selection and scroll state
        var selectedItem = AssetClassesDataGrid.SelectedItem as AssetClassItem;
        (int ClassID, string Name, string Namespace, string Assembly, string UnityVersion, string SourceFile, string SourceKind)? selectedKey = selectedItem != null
            ? (selectedItem.ClassID, selectedItem.Name, selectedItem.Namespace, selectedItem.Assembly, selectedItem.UnityVersion, selectedItem.SourceFile, selectedItem.SourceKind)
            : null;

        var scrollViewer = FindVisualChild<ScrollViewer>(AssetClassesDataGrid);
        var scrollOffset = scrollViewer?.Offset ?? default;

        ViewModel.AssetList.ClassSearchText = classSearch.Text?.Trim() ?? string.Empty;
        ViewModel.AssetList.FilterAssetClasses();

        isRefreshingClassesList = true;
        try
        {
            if (!ReferenceEquals(AssetClassesDataGrid.ItemsSource, visibleAssetClassItems))
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
        _lastPreviewedAssetUniqueId = null;
        if (RightTabControl.SelectedIndex != 0)
        {
            _suppressNextRightTabSelectionPreview = true;
            RightTabControl.SelectedIndex = 0;
        }
        PreviewSession.ActiveRightTab = ViewModels.RightPanelTab.Preview;
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

    private static void SyncAssetClassCollection(System.Collections.ObjectModel.ObservableCollection<AssetClassItem> collection, IReadOnlyList<AssetClassItem>? targetList)
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

        var targetSet = new HashSet<AssetClassItem>(targetList);

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
                if (EqualityComparer<AssetClassItem>.Default.Equals(collection[i], targetItem))
                {
                    collection[i].CopyFrom(targetItem);
                    continue;
                }

                int indexInCollection = -1;
                for (int j = i + 1; j < collection.Count; j++)
                {
                    if (EqualityComparer<AssetClassItem>.Default.Equals(collection[j], targetItem))
                    {
                        indexInCollection = j;
                        break;
                    }
                }

                if (indexInCollection != -1)
                {
                    collection[indexInCollection].CopyFrom(targetItem);
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

    /// <summary>
    /// Incrementally updates the visible class items by updating counts on existing items
    /// and appending only new class entries. Avoids the full SyncAssetClassCollection diff.
    /// </summary>
    private void UpdateAssetClassesIncremental(List<AssetClassItem> updatedClassItems)
    {
        if (updatedClassItems == null) return;

        ViewModel.AssetList.ClassSearchText = classSearch.Text?.Trim() ?? string.Empty;
        ViewModel.AssetList.UpdateAssetClassesIncremental(updatedClassItems);

        if (!ReferenceEquals(AssetClassesDataGrid.ItemsSource, visibleAssetClassItems))
        {
            AssetClassesDataGrid.ItemsSource = visibleAssetClassItems;
        }
    }

    private void ExecuteShowClassInstances()
    {
        if (AssetClassesDataGrid.SelectedItem is not AssetClassItem item)
        {
            return;
        }

        PrioritizeUserInteraction();
        ViewModel.AssetList.SetClassFilterOverride(item);

        LeftTabControl.SelectedIndex = 1;
        _ = FilterAssetListAsync(CancellationToken.None);
    }

    private void ExecuteClearClassFilter()
    {
        PrioritizeUserInteraction();
        ViewModel.AssetList.ClearClassFilterOverride();
        _ = FilterAssetListAsync(CancellationToken.None);
    }
}
