using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class AnimatorGraphCanvasNode : ViewModelBase
    {
        private bool _isSelected;
        private bool _isDimmed;

        public int StateIndex { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public string BadgeText { get; init; } = string.Empty;
        public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; } = AnimatorGraphService.DefaultNodeWidth;
        public double Height { get; init; } = AnimatorGraphService.DefaultNodeHeight;
        public bool IsSpecialNode { get; init; }
        public bool IsEntry { get; init; }
        public bool IsAnyState { get; init; }
        public bool IsDefault { get; init; }
        public bool IsOverridden { get; init; }
        public bool IsBlendTree { get; init; }
        public RelayCommand? SelectCommand { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsDimmed
        {
            get => _isDimmed;
            set
            {
                if (SetProperty(ref _isDimmed, value))
                {
                    OnPropertyChanged(nameof(NodeOpacity));
                }
            }
        }

        public double NodeOpacity => _isDimmed ? 0.35 : 1.0;
    }

    public sealed class AnimatorGraphCanvasEdge
    {
        public int SourceStateIndex { get; init; }
        public int DestinationStateIndex { get; init; }
        public double StartX { get; init; }
        public double StartY { get; init; }
        public double EndX { get; init; }
        public double EndY { get; init; }
        public double ArrowX => EndX - 4.0;
        public double ArrowY => EndY - 4.0;
        public double LabelX { get; init; }
        public double LabelY { get; init; }
        public string Label { get; init; } = string.Empty;
        public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
        public bool IsEntryEdge { get; init; }
        public bool IsAnyStateEdge { get; init; }
        public bool IsStandardEdge => !IsEntryEdge && !IsAnyStateEdge;
    }

    public sealed class AnimatorGraphViewModel : ViewModelBase
    {
        private const double SpecialNodeX = 24.0;
        private const double EntryNodeY = 56.0;
        private const double AnyStateNodeY = 176.0;
        private const double SpecialNodeWidth = 148.0;
        private const double SpecialNodeHeight = 56.0;
        private const double MinCanvasWidth = 760.0;
        private const double MinCanvasHeight = 460.0;
        private const double CanvasPaddingRight = 96.0;
        private const double CanvasPaddingBottom = 96.0;

        private AnimatorGraphLayer? _selectedLayer;
        private string _filterText = string.Empty;
        private AnimatorGraphState? _selectedState;
        private string _selectedStateBlendTreeSummary = string.Empty;
        private double _canvasWidth = MinCanvasWidth;
        private double _canvasHeight = MinCanvasHeight;
        private string _statusText = "Ready.";

        public AnimatorGraphViewModel()
            : this(new AnimatorGraphDocument
            {
                ControllerName = "(Empty)",
                AssetKind = "AnimatorController"
            })
        {
        }

        public AnimatorGraphViewModel(AnimatorGraphDocument document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));

            FilteredStates = new ObservableCollection<AnimatorGraphState>();
            SelectedStateOutgoingTransitions = new ObservableCollection<AnimatorGraphTransition>();
            SelectedStateIncomingTransitions = new ObservableCollection<AnimatorGraphTransition>();
            CanvasNodes = new ObservableCollection<AnimatorGraphCanvasNode>();
            CanvasEdges = new ObservableCollection<AnimatorGraphCanvasEdge>();

            SelectStateCommand = new RelayCommand<AnimatorGraphState>(state =>
            {
                if (state != null)
                {
                    SelectedState = state;
                }
            });

            SelectStateByIndexCommand = new RelayCommand<int>(SelectStateByIndex);
            JumpToClipCommand = new AsyncRelayCommand<AnimatorGraphClipRef>(JumpToClipAsync, clip => clip != null && clip.CanNavigate);
            ExportJsonCommand = new AsyncRelayCommand(ExportJsonAsync);
            ExportDotCommand = new AsyncRelayCommand(ExportDotAsync);
            CopySummaryCommand = new AsyncRelayCommand(CopySummaryAsync);
            ResetFilterCommand = new RelayCommand(ResetFilter, () => !string.IsNullOrEmpty(FilterText));
            CloseCommand = new RelayCommand(() => RequestClose?.Invoke());

            WindowTitle = $"Animator Graph — {Document.ControllerName} ({Document.AssetKind})";
            HeaderSummary = BuildHeaderSummary(Document);
            NotesText = Document.Notes.Count > 0 ? string.Join(" | ", Document.Notes) : string.Empty;

            if (Document.Layers.Count > 0)
            {
                SelectedLayer = Document.Layers[0];
            }
            else
            {
                StatusText = Document.Notes.Count > 0
                    ? Document.Notes[0]
                    : "No layers or state machines found in this controller.";
            }
        }

        public event Action? RequestClose;

        public Func<string, Task>? SaveJsonAsync { get; set; }
        public Func<string, Task>? SaveDotAsync { get; set; }
        public Func<string, Task>? CopyTextAsync { get; set; }
        public Func<AnimatorGraphClipRef, Task>? NavigateToClipAsync { get; set; }

        public AnimatorGraphDocument Document { get; }

        public string WindowTitle { get; }

        public string HeaderSummary { get; }

        public IReadOnlyList<AnimatorGraphLayer> Layers => Document.Layers;

        public IReadOnlyList<AnimatorGraphParameter> Parameters => Document.Parameters;

        public bool HasParameters => Document.Parameters.Count > 0;

        public IReadOnlyList<AnimatorClipOverrideEntry> ClipOverrides => Document.ClipOverrides;

        public bool HasClipOverrides => Document.ClipOverrides.Count > 0;

        public bool HasNotes => !string.IsNullOrWhiteSpace(NotesText);

        public string NotesText { get; }

        public ObservableCollection<AnimatorGraphState> FilteredStates { get; }

        public ObservableCollection<AnimatorGraphTransition> SelectedStateOutgoingTransitions { get; }

        public ObservableCollection<AnimatorGraphTransition> SelectedStateIncomingTransitions { get; }

        public ObservableCollection<AnimatorGraphCanvasNode> CanvasNodes { get; }

        public ObservableCollection<AnimatorGraphCanvasEdge> CanvasEdges { get; }

        public RelayCommand<AnimatorGraphState> SelectStateCommand { get; }
        public RelayCommand<int> SelectStateByIndexCommand { get; }
        public AsyncRelayCommand<AnimatorGraphClipRef> JumpToClipCommand { get; }
        public AsyncRelayCommand ExportJsonCommand { get; }
        public AsyncRelayCommand ExportDotCommand { get; }
        public AsyncRelayCommand CopySummaryCommand { get; }
        public RelayCommand ResetFilterCommand { get; }
        public RelayCommand CloseCommand { get; }

        public AnimatorGraphLayer? SelectedLayer
        {
            get => _selectedLayer;
            set
            {
                if (SetProperty(ref _selectedLayer, value))
                {
                    RebuildLayerView();
                }
            }
        }

        public string FilterText
        {
            get => _filterText;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _filterText, normalized))
                {
                    ResetFilterCommand.NotifyCanExecuteChanged();
                    RebuildFilteredStates();
                }
            }
        }

        public AnimatorGraphState? SelectedState
        {
            get => _selectedState;
            set
            {
                if (SetProperty(ref _selectedState, value))
                {
                    OnPropertyChanged(nameof(HasSelectedState));
                    OnPropertyChanged(nameof(HasSelectedStateClips));
                    OnPropertyChanged(nameof(HasSelectedStateTag));
                    RebuildSelectedStateDetails();
                    SyncCanvasNodeSelection();
                }
            }
        }

        public bool HasSelectedState => _selectedState != null;

        public bool HasSelectedStateClips => _selectedState != null && _selectedState.Clips.Count > 0;

        public bool HasSelectedStateTag => _selectedState != null && !string.IsNullOrWhiteSpace(_selectedState.Tag);

        public bool HasSelectedStateBlendTree => !string.IsNullOrWhiteSpace(_selectedStateBlendTreeSummary);

        public string SelectedStateBlendTreeSummary
        {
            get => _selectedStateBlendTreeSummary;
            private set
            {
                if (SetProperty(ref _selectedStateBlendTreeSummary, value))
                {
                    OnPropertyChanged(nameof(HasSelectedStateBlendTree));
                }
            }
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            private set => SetProperty(ref _canvasWidth, value);
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            private set => SetProperty(ref _canvasHeight, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        private void SelectStateByIndex(int stateIndex)
        {
            var layer = _selectedLayer;
            if (layer == null || stateIndex < 0)
            {
                return;
            }

            var match = layer.States.FirstOrDefault(s => s.StateIndex == stateIndex);
            if (match != null)
            {
                SelectedState = match;
                StatusText = $"Selected state '{match.Name}' ({match.MotionBadge}).";
            }
        }

        private void ResetFilter()
        {
            FilterText = string.Empty;
        }

        private async Task JumpToClipAsync(AnimatorGraphClipRef? clip)
        {
            if (clip == null || !clip.CanNavigate)
            {
                return;
            }

            if (NavigateToClipAsync == null)
            {
                StatusText = $"Navigation callback is not attached for clip '{clip.Name}'.";
                return;
            }

            try
            {
                await NavigateToClipAsync(clip);
                StatusText = $"Navigated to AnimationClip '{clip.Name}' (PathID: {clip.PathId}).";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to navigate to clip '{clip.Name}': {ex.Message}";
            }
        }

        private async Task ExportJsonAsync()
        {
            try
            {
                var json = AnimatorGraphService.SerializeToJson(Document);
                if (SaveJsonAsync == null)
                {
                    StatusText = $"Generated Animator Graph JSON ({json.Length:N0} chars).";
                    return;
                }

                await SaveJsonAsync(json);
                StatusText = $"Exported Animator Graph JSON for '{Document.ControllerName}'.";
            }
            catch (Exception ex)
            {
                StatusText = $"JSON export failed: {ex.Message}";
            }
        }

        private async Task ExportDotAsync()
        {
            try
            {
                var dot = AnimatorGraphService.ExportToGraphvizDot(Document, SelectedLayer?.LayerIndex);
                if (SaveDotAsync == null)
                {
                    StatusText = $"Generated Graphviz DOT ({dot.Length:N0} chars).";
                    return;
                }

                await SaveDotAsync(dot);
                StatusText = $"Exported Graphviz DOT for '{Document.ControllerName}'.";
            }
            catch (Exception ex)
            {
                StatusText = $"DOT export failed: {ex.Message}";
            }
        }

        private async Task CopySummaryAsync()
        {
            try
            {
                var summary = AnimatorGraphService.FormatReadableSummary(Document);
                if (CopyTextAsync == null)
                {
                    StatusText = $"Formatted summary ({summary.Length:N0} chars).";
                    return;
                }

                await CopyTextAsync(summary);
                StatusText = $"Copied Animator Graph summary for '{Document.ControllerName}' to clipboard.";
            }
            catch (Exception ex)
            {
                StatusText = $"Copy summary failed: {ex.Message}";
            }
        }

        private void RebuildLayerView()
        {
            RebuildCanvasGeometry();
            RebuildFilteredStates();

            var layer = _selectedLayer;
            if (layer == null || layer.States.Count == 0)
            {
                SelectedState = null;
                StatusText = layer == null
                    ? "No layer selected."
                    : $"Layer '{layer.Name}' has no states.";
                return;
            }

            var defaultState = layer.States.FirstOrDefault(s => s.IsDefault) ?? layer.States[0];
            SelectedState = defaultState;
            StatusText = string.Format(
                CultureInfo.InvariantCulture,
                "Layer '{0}': {1} state(s), {2} any-state transition(s), {3} entry transition(s).",
                layer.Name,
                layer.States.Count,
                layer.AnyStateTransitions.Count,
                layer.EntryTransitions.Count);
        }

        private void RebuildFilteredStates()
        {
            FilteredStates.Clear();
            var layer = _selectedLayer;
            if (layer == null)
            {
                return;
            }

            var needle = _filterText.Trim();
            var matchedIndices = new HashSet<int>();

            foreach (var state in layer.States)
            {
                if (MatchesFilter(state, needle))
                {
                    FilteredStates.Add(state);
                    matchedIndices.Add(state.StateIndex);
                }
            }

            bool hasFilter = !string.IsNullOrEmpty(needle);
            foreach (var node in CanvasNodes)
            {
                if (node.IsSpecialNode)
                {
                    node.IsDimmed = false;
                }
                else
                {
                    node.IsDimmed = hasFilter && !matchedIndices.Contains(node.StateIndex);
                }
            }
        }

        private void RebuildSelectedStateDetails()
        {
            SelectedStateOutgoingTransitions.Clear();
            SelectedStateIncomingTransitions.Clear();

            var state = _selectedState;
            var layer = _selectedLayer;
            if (state == null || layer == null)
            {
                SelectedStateBlendTreeSummary = string.Empty;
                return;
            }

            foreach (var tr in state.Transitions)
            {
                SelectedStateOutgoingTransitions.Add(tr);
            }

            foreach (var tr in layer.EntryTransitions)
            {
                if (tr.DestinationStateIndex == state.StateIndex)
                {
                    SelectedStateIncomingTransitions.Add(tr);
                }
            }

            foreach (var tr in layer.AnyStateTransitions)
            {
                if (tr.DestinationStateIndex == state.StateIndex)
                {
                    SelectedStateIncomingTransitions.Add(tr);
                }
            }

            foreach (var other in layer.States)
            {
                foreach (var tr in other.Transitions)
                {
                    if (tr.DestinationStateIndex == state.StateIndex)
                    {
                        SelectedStateIncomingTransitions.Add(tr);
                    }
                }
            }

            SelectedStateBlendTreeSummary = state.BlendTree != null
                ? AnimatorGraphService.FormatBlendTreeOutline(state.BlendTree).TrimEnd()
                : string.Empty;
        }

        private void RebuildCanvasGeometry()
        {
            CanvasNodes.Clear();
            CanvasEdges.Clear();

            var layer = _selectedLayer;
            if (layer == null)
            {
                CanvasWidth = MinCanvasWidth;
                CanvasHeight = MinCanvasHeight;
                return;
            }

            var nodeByStateIndex = new Dictionary<int, AnimatorGraphCanvasNode>();
            double maxX = MinCanvasWidth;
            double maxY = MinCanvasHeight;

            AnimatorGraphCanvasNode? entryNode = null;
            if (layer.States.Count > 0 || layer.EntryTransitions.Count > 0)
            {
                entryNode = new AnimatorGraphCanvasNode
                {
                    StateIndex = AnimatorGraphTransition.EntrySourceIndex,
                    Title = "Entry",
                    Subtitle = layer.DefaultStateIndex >= 0 ? $"Default: {layer.DefaultStateName}" : "State Machine Start",
                    BadgeText = "ENTRY",
                    X = SpecialNodeX,
                    Y = EntryNodeY,
                    Width = SpecialNodeWidth,
                    Height = SpecialNodeHeight,
                    IsSpecialNode = true,
                    IsEntry = true,
                    SelectCommand = new RelayCommand(() => { }, () => false)
                };
                CanvasNodes.Add(entryNode);
            }

            AnimatorGraphCanvasNode? anyStateNode = null;
            if (layer.AnyStateTransitions.Count > 0)
            {
                anyStateNode = new AnimatorGraphCanvasNode
                {
                    StateIndex = AnimatorGraphTransition.AnyStateSourceIndex,
                    Title = "Any State",
                    Subtitle = $"{layer.AnyStateTransitions.Count} transition(s)",
                    BadgeText = "ANY",
                    X = SpecialNodeX,
                    Y = AnyStateNodeY,
                    Width = SpecialNodeWidth,
                    Height = SpecialNodeHeight,
                    IsSpecialNode = true,
                    IsAnyState = true,
                    SelectCommand = new RelayCommand(() => { }, () => false)
                };
                CanvasNodes.Add(anyStateNode);
            }

            foreach (var state in layer.States)
            {
                string badge = state.IsDefault
                    ? "DEFAULT"
                    : (state.IsOverriddenClip
                        ? "OVERRIDE"
                        : (state.MotionKind == AnimatorMotionKind.BlendTree ? "BLEND TREE" : string.Empty));

                var capturedIndex = state.StateIndex;
                var node = new AnimatorGraphCanvasNode
                {
                    StateIndex = state.StateIndex,
                    Title = state.Name,
                    Subtitle = state.MotionBadge,
                    BadgeText = badge,
                    X = state.LayoutX,
                    Y = state.LayoutY,
                    Width = AnimatorGraphService.DefaultNodeWidth,
                    Height = AnimatorGraphService.DefaultNodeHeight,
                    IsSpecialNode = false,
                    IsDefault = state.IsDefault,
                    IsOverridden = state.IsOverriddenClip,
                    IsBlendTree = state.MotionKind == AnimatorMotionKind.BlendTree,
                    IsSelected = _selectedState?.StateIndex == state.StateIndex,
                    SelectCommand = new RelayCommand(() => SelectStateByIndex(capturedIndex))
                };

                CanvasNodes.Add(node);
                nodeByStateIndex[state.StateIndex] = node;

                maxX = Math.Max(maxX, node.X + node.Width + CanvasPaddingRight);
                maxY = Math.Max(maxY, node.Y + node.Height + CanvasPaddingBottom);
            }

            // Entry edges
            if (entryNode != null)
            {
                if (layer.EntryTransitions.Count > 0)
                {
                    foreach (var tr in layer.EntryTransitions)
                    {
                        if (nodeByStateIndex.TryGetValue(tr.DestinationStateIndex, out var destNode))
                        {
                            CanvasEdges.Add(CreateEdge(entryNode, destNode, TruncateEdgeLabel(tr.SummaryLabel), isEntry: true, isAnyState: false, offsetIndex: 0));
                        }
                    }
                }
                else if (layer.DefaultStateIndex >= 0 && nodeByStateIndex.TryGetValue(layer.DefaultStateIndex, out var defaultNode))
                {
                    CanvasEdges.Add(CreateEdge(entryNode, defaultNode, "Default", isEntry: true, isAnyState: false, offsetIndex: 0));
                }
            }

            // AnyState edges
            if (anyStateNode != null)
            {
                for (int i = 0; i < layer.AnyStateTransitions.Count; i++)
                {
                    var tr = layer.AnyStateTransitions[i];
                    if (nodeByStateIndex.TryGetValue(tr.DestinationStateIndex, out var destNode))
                    {
                        CanvasEdges.Add(CreateEdge(anyStateNode, destNode, TruncateEdgeLabel(tr.SummaryLabel), isEntry: false, isAnyState: true, offsetIndex: i));
                    }
                }
            }

            // Track directed edge multiplicity to offset parallel/bidirectional lines cleanly
            var pairCount = new Dictionary<(int, int), int>();
            foreach (var state in layer.States)
            {
                if (!nodeByStateIndex.TryGetValue(state.StateIndex, out var srcNode))
                {
                    continue;
                }

                foreach (var tr in state.Transitions)
                {
                    if (!nodeByStateIndex.TryGetValue(tr.DestinationStateIndex, out var dstNode))
                    {
                        continue;
                    }

                    var undirectedKey = srcNode.StateIndex <= dstNode.StateIndex
                        ? (srcNode.StateIndex, dstNode.StateIndex)
                        : (dstNode.StateIndex, srcNode.StateIndex);

                    pairCount.TryGetValue(undirectedKey, out int count);
                    pairCount[undirectedKey] = count + 1;

                    CanvasEdges.Add(CreateEdge(srcNode, dstNode, TruncateEdgeLabel(tr.SummaryLabel), isEntry: false, isAnyState: false, offsetIndex: count));
                }
            }

            CanvasWidth = maxX;
            CanvasHeight = maxY;
        }

        private static AnimatorGraphCanvasEdge CreateEdge(
            AnimatorGraphCanvasNode source,
            AnimatorGraphCanvasNode dest,
            string label,
            bool isEntry,
            bool isAnyState,
            int offsetIndex)
        {
            double verticalOffset = offsetIndex == 0
                ? 0.0
                : ((offsetIndex % 2 == 1 ? 1 : -1) * ((offsetIndex + 1) / 2) * 14.0);

            double startX;
            double startY;
            double endX;
            double endY;

            if (source.StateIndex == dest.StateIndex)
            {
                // Self-transition indicator along top edge of card
                startX = source.X + source.Width * 0.25;
                startY = source.Y - 2.0;
                endX = source.X + source.Width * 0.75;
                endY = source.Y - 14.0;
            }
            else if (dest.X > source.X + source.Width * 0.5)
            {
                // Left-to-right flow
                startX = source.X + source.Width;
                startY = source.Y + source.Height * 0.5 + verticalOffset;
                endX = dest.X;
                endY = dest.Y + dest.Height * 0.5 + verticalOffset;
            }
            else if (source.X > dest.X + dest.Width * 0.5)
            {
                // Right-to-left return flow
                startX = source.X;
                startY = source.Y + source.Height * 0.5 + verticalOffset;
                endX = dest.X + dest.Width;
                endY = dest.Y + dest.Height * 0.5 + verticalOffset;
            }
            else
            {
                // Same column: connect bottom/top edges
                bool goingDown = dest.Y >= source.Y;
                startX = source.X + source.Width * 0.5 + verticalOffset;
                startY = goingDown ? source.Y + source.Height : source.Y;
                endX = dest.X + dest.Width * 0.5 + verticalOffset;
                endY = goingDown ? dest.Y : dest.Y + dest.Height;
            }

            double midX = (startX + endX) * 0.5;
            double midY = (startY + endY) * 0.5;

            return new AnimatorGraphCanvasEdge
            {
                SourceStateIndex = source.StateIndex,
                DestinationStateIndex = dest.StateIndex,
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                LabelX = Math.Max(4.0, midX - 48.0),
                LabelY = Math.Max(4.0, midY - 11.0),
                Label = label,
                IsEntryEdge = isEntry,
                IsAnyStateEdge = isAnyState
            };
        }

        private void SyncCanvasNodeSelection()
        {
            int selectedIdx = _selectedState?.StateIndex ?? int.MinValue;
            foreach (var node in CanvasNodes)
            {
                node.IsSelected = !node.IsSpecialNode && node.StateIndex == selectedIdx;
            }
        }

        private static bool MatchesFilter(AnimatorGraphState state, string needle)
        {
            if (string.IsNullOrEmpty(needle))
            {
                return true;
            }

            if (state.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || state.FullPath.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || state.Tag.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || state.PrimaryClipName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || state.MotionKind.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (int i = 0; i < state.Clips.Count; i++)
            {
                var c = state.Clips[i];
                if (c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || c.OriginalClipName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string TruncateEdgeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            return label.Length <= 28 ? label : label[..25] + "...";
        }

        private static string BuildHeaderSummary(AnimatorGraphDocument doc)
        {
            int totalStates = doc.Layers.Sum(l => l.States.Count);
            var parts = new List<string>
            {
                doc.ControllerName,
                $"{doc.Layers.Count} layer(s)",
                $"{totalStates} state(s)",
                $"{doc.Parameters.Count} param(s)"
            };

            if (doc.ClipOverrides.Count > 0)
            {
                parts.Add($"{doc.ClipOverrides.Count} override(s)");
            }

            if (!string.IsNullOrWhiteSpace(doc.BaseControllerName))
            {
                parts.Add($"Base: {doc.BaseControllerName}");
            }

            return string.Join("  •  ", parts);
        }
    }
}
