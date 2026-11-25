using System;
using System.Collections.Generic;
using System.Globalization;
using AssetStudio;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum SceneHierarchyViewMode
    {
        Scenes = 0,
        Containers = 1
    }

    public sealed class LazySceneDescriptor
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ScenePath { get; init; } = string.Empty;
        public int BuildIndex { get; init; } = -1;
        public string SourceKind { get; init; } = string.Empty;
        public string SerializedFileName { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public string ContainerKey { get; init; } = string.Empty;
        public int GameObjectCount { get; init; } = -1;
        public int RendererCount { get; init; } = -1;
        public IReadOnlyList<(string SerializedFileName, string OriginalPath)> CompanionStageFiles { get; init; }
            = Array.Empty<(string SerializedFileName, string OriginalPath)>();

        public string FormatStatsSubtitle()
        {
            if (GameObjectCount >= 0 || RendererCount >= 0)
            {
                var goCount = Math.Max(0, GameObjectCount);
                var rendererCount = Math.Max(0, RendererCount);
                var goLabel = goCount == 1 ? "object" : "objects";
                var rendLabel = rendererCount == 1 ? "renderer" : "renderers";
                return rendererCount == 0 && GameObjectCount >= 0
                    ? string.Create(CultureInfo.InvariantCulture, $"({goCount:N0} {goLabel}, 0 {rendLabel} • shell)")
                    : string.Create(CultureInfo.InvariantCulture, $"({goCount:N0} {goLabel}, {rendererCount:N0} {rendLabel})");
            }

            return !string.IsNullOrWhiteSpace(SourceKind) ? $"({SourceKind})" : string.Empty;
        }
    }

    public sealed class ContainerTreeNode : ViewModelBase
    {
        private bool _isExpanded;
        private string _subtitle;

        public ContainerTreeNode(
            string name,
            string fullPath,
            bool isFolder,
            int totalAssetCount = 0,
            string? subtitle = null,
            string? typeBadge = null,
            bool isPlaceholder = false,
            bool isLoadMoreNode = false,
            int arenaIndex = -1,
            AssetHandle? handle = null,
            AssetItem? eagerAssetItem = null,
            ClassIDType assetType = ClassIDType.UnknownType,
            long pathId = 0,
            string? serializedFileName = null,
            string? uniqueId = null,
            int depth = 0,
            int nextPageStartIndex = 0)
        {
            Name = name ?? string.Empty;
            FullPath = fullPath ?? string.Empty;
            IsFolder = isFolder;
            TotalAssetCount = totalAssetCount;
            _subtitle = subtitle ?? string.Empty;
            TypeBadge = typeBadge ?? string.Empty;
            IsPlaceholder = isPlaceholder;
            IsLoadMoreNode = isLoadMoreNode;
            ArenaIndex = arenaIndex;
            Handle = handle;
            EagerAssetItem = eagerAssetItem;
            AssetType = assetType;
            PathId = pathId;
            SerializedFileName = serializedFileName ?? string.Empty;
            UniqueId = uniqueId ?? string.Empty;
            Depth = depth;
            NextPageStartIndex = nextPageStartIndex;
            Children = new BulkObservableCollection<ContainerTreeNode>();
        }

        public event Action<ContainerTreeNode>? Expanded;

        public string Name { get; }
        public string FullPath { get; }
        public bool IsFolder { get; }
        public bool IsPlaceholder { get; }
        public bool IsLoadMoreNode { get; }
        public bool IsAssetLeaf => !IsFolder && !IsPlaceholder && !IsLoadMoreNode;
        public int TotalAssetCount { get; }
        public int ArenaIndex { get; }
        public AssetHandle? Handle { get; }
        public AssetItem? EagerAssetItem { get; }
        public ClassIDType AssetType { get; }
        public long PathId { get; }
        public string SerializedFileName { get; }
        public string UniqueId { get; }
        public string TypeBadge { get; }
        public int Depth { get; }
        internal int NextPageStartIndex { get; }
        internal bool ChildrenMaterialized { get; set; }
        internal int[]? EntryIndices { get; set; }
        internal bool IsFallbackRootFolder { get; set; }

        public ContainerTreeNode? Parent { get; internal set; }

        public BulkObservableCollection<ContainerTreeNode> Children { get; }

        public string Subtitle
        {
            get => _subtitle;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _subtitle, normalized))
                {
                    OnPropertyChanged(nameof(HasSubtitle));
                }
            }
        }

        public bool HasSubtitle => !string.IsNullOrEmpty(_subtitle);
        public bool HasTypeBadge => !string.IsNullOrEmpty(TypeBadge);
        public string NodeGlyph => IsLoadMoreNode ? "…" : (IsFolder ? "📁" : (IsPlaceholder ? "·" : "▪"));
        public double NodeTextOpacity => (IsPlaceholder || IsLoadMoreNode) ? 0.65 : 1.0;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value) && value)
                {
                    Expanded?.Invoke(this);
                }
            }
        }

        public void ReplaceChildren(IReadOnlyList<ContainerTreeNode> newChildren)
        {
            ArgumentNullException.ThrowIfNull(newChildren);

            for (var i = 0; i < Children.Count; i++)
            {
                if (ReferenceEquals(Children[i].Parent, this))
                {
                    Children[i].Parent = null;
                }
            }

            for (var i = 0; i < newChildren.Count; i++)
            {
                newChildren[i].Parent = this;
            }

            Children.ReplaceAll(newChildren);
        }

        public void ExpandAncestors()
        {
            var current = Parent;
            while (current != null)
            {
                current.IsExpanded = true;
                current = current.Parent;
            }
        }
    }
}
