using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetStudio;
using UnityObject = AssetStudio.Object;

namespace AssetStudio.Avalonia.Services
{
    public enum AnimatorMotionKind
    {
        Empty = 0,
        Clip = 1,
        BlendTree = 2
    }

    public sealed class AnimatorGraphDocument
    {
        public string ControllerName { get; init; } = string.Empty;
        public string AssetKind { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string BaseControllerName { get; init; } = string.Empty;
        public long BaseControllerPathId { get; init; }
        public bool MultiThreadedStateMachine { get; init; }
        public IReadOnlyList<AnimatorGraphParameter> Parameters { get; init; } = Array.Empty<AnimatorGraphParameter>();
        public IReadOnlyList<AnimatorClipOverrideEntry> ClipOverrides { get; init; } = Array.Empty<AnimatorClipOverrideEntry>();
        public IReadOnlyList<AnimatorGraphLayer> Layers { get; init; } = Array.Empty<AnimatorGraphLayer>();
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    public sealed class AnimatorGraphParameter
    {
        public int Index { get; init; }
        public uint HashId { get; init; }
        public string Name { get; init; } = string.Empty;
        public uint TypeCode { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public uint ValueIndex { get; init; }
        public float? DefaultFloat { get; init; }
        public int? DefaultInt { get; init; }
        public bool? DefaultBool { get; init; }
        public string DefaultValueDisplay { get; init; } = string.Empty;

        [JsonIgnore]
        public string DisplayLine => $"{Name} ({TypeName}) = {DefaultValueDisplay}";
    }

    public sealed class AnimatorClipOverrideEntry
    {
        public string OriginalClipName { get; init; } = string.Empty;
        public long OriginalClipPathId { get; init; }
        public string OriginalClipUniqueId { get; init; } = string.Empty;
        public string OverrideClipName { get; init; } = string.Empty;
        public long OverrideClipPathId { get; init; }
        public string OverrideClipUniqueId { get; init; } = string.Empty;

        [JsonIgnore]
        public string DisplayLine => $"{OriginalClipName} -> {OverrideClipName}";
    }

    public sealed class AnimatorGraphLayer
    {
        public int LayerIndex { get; init; }
        public string Name { get; init; } = string.Empty;
        public uint StateMachineIndex { get; init; }
        public float DefaultWeight { get; init; }
        public string BlendingMode { get; init; } = "Override";
        public bool IKPass { get; init; }
        public bool SyncedLayerAffectsTiming { get; init; }
        public int DefaultStateIndex { get; init; } = -1;
        public string DefaultStateName { get; init; } = string.Empty;
        public IReadOnlyList<AnimatorGraphState> States { get; init; } = Array.Empty<AnimatorGraphState>();
        public IReadOnlyList<AnimatorGraphTransition> AnyStateTransitions { get; init; } = Array.Empty<AnimatorGraphTransition>();
        public IReadOnlyList<AnimatorGraphTransition> EntryTransitions { get; init; } = Array.Empty<AnimatorGraphTransition>();

        [JsonIgnore]
        public string DisplayHeader => string.Format(
            CultureInfo.InvariantCulture,
            "[{0}] {1} ({2} states, {3}, w={4:0.##})",
            LayerIndex,
            Name,
            States.Count,
            BlendingMode,
            DefaultWeight);

        public override string ToString() => DisplayHeader;
    }

    public sealed class AnimatorGraphState
    {
        public int StateIndex { get; init; }
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public uint HashId { get; init; }
        public bool IsDefault { get; init; }
        public float Speed { get; init; } = 1f;
        public float CycleOffset { get; init; }
        public bool Loop { get; init; }
        public bool Mirror { get; init; }
        public bool IKOnFeet { get; init; }
        public bool WriteDefaultValues { get; init; }
        public AnimatorMotionKind MotionKind { get; init; }
        public string PrimaryClipName { get; init; } = string.Empty;
        public long PrimaryClipPathId { get; init; }
        public string PrimaryClipUniqueId { get; init; } = string.Empty;
        public bool IsOverriddenClip { get; init; }
        public IReadOnlyList<AnimatorGraphClipRef> Clips { get; init; } = Array.Empty<AnimatorGraphClipRef>();
        public AnimatorBlendTreeNode? BlendTree { get; init; }
        public IReadOnlyList<AnimatorGraphTransition> Transitions { get; init; } = Array.Empty<AnimatorGraphTransition>();
        public double LayoutX { get; init; }
        public double LayoutY { get; init; }
        public int LayoutColumn { get; init; }
        public int LayoutRow { get; init; }

        [JsonIgnore]
        public string MotionBadge
        {
            get
            {
                var baseText = MotionKind switch
                {
                    AnimatorMotionKind.BlendTree => $"BlendTree ({Clips.Count} clip{(Clips.Count == 1 ? "" : "s")})",
                    AnimatorMotionKind.Clip => string.IsNullOrWhiteSpace(PrimaryClipName) ? "Clip" : PrimaryClipName,
                    _ => "Empty"
                };

                return IsOverriddenClip ? $"{baseText} [Override]" : baseText;
            }
        }

        [JsonIgnore]
        public string ListDisplayName => IsDefault ? $"★ {Name}" : Name;

        [JsonIgnore]
        public string SpeedDisplay => string.Format(CultureInfo.InvariantCulture, "{0:0.###}x", Speed);

        [JsonIgnore]
        public string CycleOffsetDisplay => string.Format(CultureInfo.InvariantCulture, "{0:0.###}", CycleOffset);
    }

    public sealed class AnimatorGraphClipRef
    {
        public uint ClipIndex { get; init; }
        public string Name { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string UniqueId { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
        public bool IsOverridden { get; init; }
        public string OriginalClipName { get; init; } = string.Empty;
        public long OriginalClipPathId { get; init; }

        [JsonIgnore]
        public bool CanNavigate => !string.IsNullOrWhiteSpace(UniqueId) || PathId != 0;

        [JsonIgnore]
        public string DisplayLabel => IsOverridden && !string.IsNullOrWhiteSpace(OriginalClipName)
            ? $"{Name} (overrides {OriginalClipName})"
            : Name;
    }

    public sealed class AnimatorBlendTreeNode
    {
        public int NodeIndex { get; init; }
        public uint BlendTypeCode { get; init; }
        public string BlendTypeName { get; init; } = string.Empty;
        public bool IsLeafClip { get; init; }
        public string ParameterXName { get; init; } = string.Empty;
        public string ParameterYName { get; init; } = string.Empty;
        public AnimatorGraphClipRef? Clip { get; init; }
        public float Duration { get; init; }
        public float CycleOffset { get; init; }
        public bool Mirror { get; init; }
        public bool NormalizedDirectBlendValues { get; init; }
        public IReadOnlyList<AnimatorBlendTreeChild> Children { get; init; } = Array.Empty<AnimatorBlendTreeChild>();
    }

    public sealed class AnimatorBlendTreeChild
    {
        public int ChildIndex { get; init; }
        public float? Threshold { get; init; }
        public float? PositionX { get; init; }
        public float? PositionY { get; init; }
        public string DirectBlendParameter { get; init; } = string.Empty;
        public AnimatorBlendTreeNode Node { get; init; } = new();
    }

    public sealed class AnimatorGraphTransition
    {
        public const int AnyStateSourceIndex = -1;
        public const int EntrySourceIndex = -2;

        public int SourceStateIndex { get; init; }
        public string SourceStateName { get; init; } = string.Empty;
        public int DestinationStateIndex { get; init; } = -1;
        public string DestinationStateName { get; init; } = string.Empty;
        public bool HasExitTime { get; init; }
        public float ExitTime { get; init; }
        public float Duration { get; init; }
        public float Offset { get; init; }
        public bool HasFixedDuration { get; init; }
        public bool CanTransitionToSelf { get; init; }
        public IReadOnlyList<AnimatorGraphCondition> Conditions { get; init; } = Array.Empty<AnimatorGraphCondition>();

        [JsonIgnore]
        public bool CanNavigateToDestination => DestinationStateIndex >= 0;

        [JsonIgnore]
        public bool CanNavigateToSource => SourceStateIndex >= 0;

        [JsonIgnore]
        public string SummaryLabel
        {
            get
            {
                if (Conditions.Count > 0)
                {
                    return string.Join(" && ", Conditions.Select(c => c.FormattedExpression));
                }

                if (HasExitTime)
                {
                    return string.Format(CultureInfo.InvariantCulture, "ExitTime {0:0.##}", ExitTime);
                }

                return "Unconditional";
            }
        }

        [JsonIgnore]
        public string TimingSummary
        {
            get
            {
                var parts = new List<string>(4);
                if (HasExitTime)
                {
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "exit={0:0.##}", ExitTime));
                }

                parts.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "dur={0:0.##}{1}",
                    Duration,
                    HasFixedDuration ? "s" : "n"));

                if (Math.Abs(Offset) > 0.0001f)
                {
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "off={0:0.##}", Offset));
                }

                if (CanTransitionToSelf)
                {
                    parts.Add("self");
                }

                return string.Join(", ", parts);
            }
        }

        [JsonIgnore]
        public string DetailLine => $"{SourceStateName} -> {DestinationStateName} | {SummaryLabel} ({TimingSummary})";
    }

    public sealed class AnimatorGraphCondition
    {
        public uint ModeCode { get; init; }
        public string ModeName { get; init; } = string.Empty;
        public uint ParameterHashId { get; init; }
        public string ParameterName { get; init; } = string.Empty;
        public float Threshold { get; init; }
        public string FormattedExpression { get; init; } = string.Empty;
    }

    public static class AnimatorGraphService
    {
        public const double DefaultNodeWidth = 210.0;
        public const double DefaultNodeHeight = 84.0;
        public const double LayoutStartX = 230.0;
        public const double LayoutStartY = 48.0;
        public const double LayoutColumnSpacing = 280.0;
        public const double LayoutRowSpacing = 124.0;

        private const int MaxBlendTreeRecursionDepth = 32;
        private const int MaxOverrideChainDepth = 4;
        private const uint InvalidClipId = uint.MaxValue;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AnimatorGraphDocument BuildDocument(
            UnityObject? asset,
            Func<PPtr<RuntimeAnimatorController>, RuntimeAnimatorController?>? controllerResolver = null,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver = null)
        {
            if (asset == null)
            {
                return new AnimatorGraphDocument
                {
                    ControllerName = "(None)",
                    AssetKind = "Unknown",
                    Notes = new[] { "No animator asset was provided." }
                };
            }

            if (asset is AnimatorController controller)
            {
                return BuildFromAnimatorController(controller, clipResolver);
            }

            if (asset is AnimatorOverrideController overrideController)
            {
                return BuildFromAnimatorOverrideController(overrideController, controllerResolver, clipResolver);
            }

            if (asset is Animator animator)
            {
                return BuildFromAnimator(animator, controllerResolver, clipResolver);
            }

            return new AnimatorGraphDocument
            {
                ControllerName = asset is NamedObject named && !string.IsNullOrWhiteSpace(named.m_Name)
                    ? named.m_Name
                    : $"{asset.type}_{asset.m_PathID}",
                AssetKind = asset.type.ToString(),
                SourceFile = asset.assetsFile?.fileName ?? string.Empty,
                PathId = asset.m_PathID,
                Notes = new[] { $"Asset type '{asset.type}' does not contain an Animator state machine." }
            };
        }

        public static AnimatorGraphDocument BuildFromAnimatorController(
            AnimatorController controller,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver = null)
        {
            if (controller == null)
            {
                return new AnimatorGraphDocument
                {
                    ControllerName = "(None)",
                    AssetKind = "AnimatorController",
                    Notes = new[] { "AnimatorController instance was null." }
                };
            }

            return BuildControllerDocumentCore(
                controller: controller,
                displayName: SafeName(controller.m_Name, $"AnimatorController_{controller.m_PathID}"),
                assetKind: "AnimatorController",
                sourceFile: controller.assetsFile?.fileName ?? string.Empty,
                pathId: controller.m_PathID,
                baseControllerName: string.Empty,
                baseControllerPathId: 0,
                clipOverrides: Array.Empty<AnimatorClipOverrideEntry>(),
                overrideLookup: null,
                initialNotes: null,
                clipResolver: clipResolver);
        }

        public static AnimatorGraphDocument BuildFromAnimatorOverrideController(
            AnimatorOverrideController overrideController,
            Func<PPtr<RuntimeAnimatorController>, RuntimeAnimatorController?>? controllerResolver = null,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver = null)
        {
            if (overrideController == null)
            {
                return new AnimatorGraphDocument
                {
                    ControllerName = "(None)",
                    AssetKind = "AnimatorOverrideController",
                    Notes = new[] { "AnimatorOverrideController instance was null." }
                };
            }

            var displayName = SafeName(overrideController.m_Name, $"AnimatorOverrideController_{overrideController.m_PathID}");
            var sourceFile = overrideController.assetsFile?.fileName ?? string.Empty;
            var notes = new List<string>();

            var clipOverrides = ExtractClipOverrides(overrideController, clipResolver, out var overrideLookup);
            var baseController = ResolveBaseAnimatorController(
                overrideController,
                controllerResolver,
                clipOverrides,
                overrideLookup,
                notes,
                clipResolver);

            var basePathId = baseController?.m_PathID ?? overrideController.m_Controller?.m_PathID ?? 0;
            var baseName = baseController != null
                ? SafeName(baseController.m_Name, $"AnimatorController_{baseController.m_PathID}")
                : (basePathId != 0 ? $"Unresolved_Controller_{basePathId}" : string.Empty);

            if (baseController == null)
            {
                notes.Add(basePathId != 0
                    ? $"Base AnimatorController (PathID {basePathId}) is not loaded; showing clip overrides only."
                    : "AnimatorOverrideController has no base AnimatorController assigned.");

                return new AnimatorGraphDocument
                {
                    ControllerName = displayName,
                    AssetKind = "AnimatorOverrideController",
                    SourceFile = sourceFile,
                    PathId = overrideController.m_PathID,
                    BaseControllerName = baseName,
                    BaseControllerPathId = basePathId,
                    MultiThreadedStateMachine = false,
                    Parameters = Array.Empty<AnimatorGraphParameter>(),
                    ClipOverrides = clipOverrides,
                    Layers = Array.Empty<AnimatorGraphLayer>(),
                    Notes = notes
                };
            }

            return BuildControllerDocumentCore(
                controller: baseController,
                displayName: displayName,
                assetKind: "AnimatorOverrideController",
                sourceFile: sourceFile,
                pathId: overrideController.m_PathID,
                baseControllerName: baseName,
                baseControllerPathId: baseController.m_PathID,
                clipOverrides: clipOverrides,
                overrideLookup: overrideLookup,
                initialNotes: notes,
                clipResolver: clipResolver);
        }

        public static AnimatorGraphDocument BuildFromAnimator(
            Animator animator,
            Func<PPtr<RuntimeAnimatorController>, RuntimeAnimatorController?>? controllerResolver = null,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver = null)
        {
            if (animator == null)
            {
                return new AnimatorGraphDocument
                {
                    ControllerName = "(None)",
                    AssetKind = "Animator",
                    Notes = new[] { "Animator instance was null." }
                };
            }

            string animatorName = $"Animator_{animator.m_PathID}";
            if (TryResolvePPtr(animator.m_GameObject, out var go) && go != null && !string.IsNullOrWhiteSpace(go.m_Name))
            {
                animatorName = go.m_Name;
            }

            var sourceFile = animator.assetsFile?.fileName ?? string.Empty;
            var resolvedController = ResolveRuntimeController(animator.m_Controller, controllerResolver);

            if (resolvedController is AnimatorController ac)
            {
                var doc = BuildControllerDocumentCore(
                    controller: ac,
                    displayName: animatorName,
                    assetKind: "Animator",
                    sourceFile: sourceFile,
                    pathId: animator.m_PathID,
                    baseControllerName: SafeName(ac.m_Name, $"AnimatorController_{ac.m_PathID}"),
                    baseControllerPathId: ac.m_PathID,
                    clipOverrides: Array.Empty<AnimatorClipOverrideEntry>(),
                    overrideLookup: null,
                    initialNotes: null,
                    clipResolver: clipResolver);
                return doc;
            }

            if (resolvedController is AnimatorOverrideController aoc)
            {
                var notes = new List<string>
                {
                    $"Resolved via AnimatorOverrideController '{SafeName(aoc.m_Name, $"AnimatorOverrideController_{aoc.m_PathID}")}'."
                };

                var clipOverrides = ExtractClipOverrides(aoc, clipResolver, out var overrideLookup);
                var baseAc = ResolveBaseAnimatorController(aoc, controllerResolver, clipOverrides, overrideLookup, notes, clipResolver);
                var basePathId = baseAc?.m_PathID ?? aoc.m_Controller?.m_PathID ?? 0;
                var baseName = baseAc != null
                    ? SafeName(baseAc.m_Name, $"AnimatorController_{baseAc.m_PathID}")
                    : SafeName(aoc.m_Name, $"AnimatorOverrideController_{aoc.m_PathID}");

                if (baseAc == null)
                {
                    notes.Add("Base AnimatorController behind AnimatorOverrideController is not loaded; showing clip overrides only.");
                    return new AnimatorGraphDocument
                    {
                        ControllerName = animatorName,
                        AssetKind = "Animator",
                        SourceFile = sourceFile,
                        PathId = animator.m_PathID,
                        BaseControllerName = baseName,
                        BaseControllerPathId = basePathId,
                        Parameters = Array.Empty<AnimatorGraphParameter>(),
                        ClipOverrides = clipOverrides,
                        Layers = Array.Empty<AnimatorGraphLayer>(),
                        Notes = notes
                    };
                }

                return BuildControllerDocumentCore(
                    controller: baseAc,
                    displayName: animatorName,
                    assetKind: "Animator",
                    sourceFile: sourceFile,
                    pathId: animator.m_PathID,
                    baseControllerName: baseName,
                    baseControllerPathId: baseAc.m_PathID,
                    clipOverrides: clipOverrides,
                    overrideLookup: overrideLookup,
                    initialNotes: notes,
                    clipResolver: clipResolver);
            }

            var controllerPathId = animator.m_Controller?.m_PathID ?? 0;
            return new AnimatorGraphDocument
            {
                ControllerName = animatorName,
                AssetKind = "Animator",
                SourceFile = sourceFile,
                PathId = animator.m_PathID,
                BaseControllerPathId = controllerPathId,
                Notes = new[]
                {
                    controllerPathId != 0
                        ? $"Animator references controller PathID {controllerPathId}, which is not currently loaded."
                        : "Animator has no RuntimeAnimatorController assigned."
                }
            };
        }

        public static string SerializeToJson(AnimatorGraphDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);
            return JsonSerializer.Serialize(doc, JsonOptions);
        }

        public static void ExportToJson(AnimatorGraphDocument doc, string outputPath)
        {
            ArgumentNullException.ThrowIfNull(doc);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
            }

            var json = SerializeToJson(doc);
            WriteUtf8FileAtomic(outputPath, json);
        }

        public static string ExportToGraphvizDot(AnimatorGraphDocument doc, int? layerIndex = null)
        {
            ArgumentNullException.ThrowIfNull(doc);

            var sb = new StringBuilder(2048);
            var graphName = EscapeDotString(string.IsNullOrWhiteSpace(doc.ControllerName) ? "AnimatorController" : doc.ControllerName);
            sb.Append("digraph \"").Append(graphName).AppendLine("\" {");
            sb.AppendLine("  rankdir=LR;");
            sb.AppendLine("  graph [fontname=\"Segoe UI\", fontsize=11, pad=\"0.4\", nodesep=\"0.5\", ranksep=\"0.7\"];");
            sb.AppendLine("  node [fontname=\"Segoe UI\", fontsize=10];");
            sb.AppendLine("  edge [fontname=\"Segoe UI\", fontsize=9];");

            var layersToExport = layerIndex.HasValue
                ? doc.Layers.Where(l => l.LayerIndex == layerIndex.Value).ToList()
                : doc.Layers.ToList();

            bool useSubgraphs = layersToExport.Count > 1;
            foreach (var layer in layersToExport)
            {
                string indent = useSubgraphs ? "    " : "  ";
                string prefix = $"L{layer.LayerIndex}_";

                if (useSubgraphs)
                {
                    sb.Append("  subgraph cluster_layer_").Append(layer.LayerIndex).AppendLine(" {");
                    sb.Append("    label=\"").Append(EscapeDotString(layer.DisplayHeader)).AppendLine("\";");
                    sb.AppendLine("    style=\"rounded,dashed\";");
                    sb.AppendLine("    color=\"#607D8B\";");
                }

                string entryId = $"{prefix}ENTRY";
                string anyId = $"{prefix}ANY";

                if (layer.States.Count > 0 || layer.EntryTransitions.Count > 0)
                {
                    sb.Append(indent)
                        .Append(entryId)
                        .AppendLine(" [label=\"Entry\", shape=ellipse, style=filled, fillcolor=\"#2E7D32\", fontcolor=\"white\"];");
                }

                if (layer.AnyStateTransitions.Count > 0)
                {
                    sb.Append(indent)
                        .Append(anyId)
                        .AppendLine(" [label=\"Any State\", shape=diamond, style=filled, fillcolor=\"#00838F\", fontcolor=\"white\"];");
                }

                foreach (var state in layer.States)
                {
                    string nodeId = $"{prefix}S{state.StateIndex}";
                    var labelLines = new List<string> { state.Name };
                    if (state.MotionKind != AnimatorMotionKind.Empty)
                    {
                        labelLines.Add(state.MotionBadge);
                    }
                    else
                    {
                        labelLines.Add("(Empty)");
                    }

                    string fillColor;
                    string style;
                    if (state.IsDefault)
                    {
                        fillColor = "#E65100";
                        style = "rounded,filled,bold";
                    }
                    else if (state.IsOverriddenClip)
                    {
                        fillColor = "#6A1B9A";
                        style = "rounded,filled";
                    }
                    else
                    {
                        fillColor = "#37474F";
                        style = "rounded,filled";
                    }

                    var escapedLabel = EscapeDotLabelMultiline(labelLines);
                    sb.Append(indent)
                        .Append(nodeId)
                        .Append(" [label=\"")
                        .Append(escapedLabel)
                        .Append("\", shape=box, style=\"")
                        .Append(style)
                        .Append("\", fillcolor=\"")
                        .Append(fillColor)
                        .AppendLine("\", fontcolor=\"white\"];");
                }

                var externalNodes = new HashSet<string>(StringComparer.Ordinal);

                void AppendTransitionEdge(string fromId, AnimatorGraphTransition tr, string? edgeStyle = null)
                {
                    string toId;
                    if (tr.DestinationStateIndex >= 0)
                    {
                        toId = $"{prefix}S{tr.DestinationStateIndex}";
                    }
                    else
                    {
                        toId = $"{prefix}EXT_{SanitizeDotIdentifier(tr.DestinationStateName)}";
                        if (externalNodes.Add(toId))
                        {
                            sb.Append(indent)
                                .Append(toId)
                                .Append(" [label=\"")
                                .Append(EscapeDotString(tr.DestinationStateName))
                                .AppendLine("\", shape=ellipse, style=filled, fillcolor=\"#B71C1C\", fontcolor=\"white\"];");
                        }
                    }

                    var attrs = new List<string>();
                    if (!string.IsNullOrWhiteSpace(tr.SummaryLabel))
                    {
                        attrs.Add($"label=\"{EscapeDotString(tr.SummaryLabel)}\"");
                    }

                    if (!string.IsNullOrWhiteSpace(edgeStyle))
                    {
                        attrs.Add(edgeStyle);
                    }

                    sb.Append(indent).Append(fromId).Append(" -> ").Append(toId);
                    if (attrs.Count > 0)
                    {
                        sb.Append(" [").Append(string.Join(", ", attrs)).Append(']');
                    }

                    sb.AppendLine(";");
                }

                if (layer.EntryTransitions.Count > 0)
                {
                    foreach (var tr in layer.EntryTransitions)
                    {
                        AppendTransitionEdge(entryId, tr, "color=\"#2E7D32\", style=bold");
                    }
                }
                else if (layer.DefaultStateIndex >= 0)
                {
                    sb.Append(indent)
                        .Append(entryId)
                        .Append(" -> ")
                        .Append(prefix).Append('S').Append(layer.DefaultStateIndex)
                        .AppendLine(" [label=\"Default\", color=\"#2E7D32\", style=bold];");
                }

                foreach (var tr in layer.AnyStateTransitions)
                {
                    AppendTransitionEdge(anyId, tr, "color=\"#00838F\", style=dashed");
                }

                foreach (var state in layer.States)
                {
                    string fromId = $"{prefix}S{state.StateIndex}";
                    foreach (var tr in state.Transitions)
                    {
                        AppendTransitionEdge(fromId, tr);
                    }
                }

                if (useSubgraphs)
                {
                    sb.AppendLine("  }");
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        public static void WriteGraphvizDotFile(AnimatorGraphDocument doc, string outputPath, int? layerIndex = null)
        {
            ArgumentNullException.ThrowIfNull(doc);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
            }

            var dot = ExportToGraphvizDot(doc, layerIndex);
            WriteUtf8FileAtomic(outputPath, dot);
        }

        public static string FormatReadableSummary(AnimatorGraphDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);

            var sb = new StringBuilder(1024);
            sb.AppendLine("============================================================");
            sb.AppendLine($"{doc.AssetKind.ToUpperInvariant()}: {doc.ControllerName}");
            sb.AppendLine("============================================================");
            if (!string.IsNullOrWhiteSpace(doc.SourceFile))
            {
                sb.AppendLine($"Source File : {doc.SourceFile} (PathID: {doc.PathId})");
            }
            else if (doc.PathId != 0)
            {
                sb.AppendLine($"PathID      : {doc.PathId}");
            }

            if (!string.IsNullOrWhiteSpace(doc.BaseControllerName))
            {
                sb.AppendLine($"Base Ctrl   : {doc.BaseControllerName} (PathID: {doc.BaseControllerPathId})");
            }

            sb.AppendLine($"MultiThread : {doc.MultiThreadedStateMachine}");
            sb.AppendLine($"Summary     : {doc.Layers.Count} layer(s), {doc.Parameters.Count} parameter(s), {doc.ClipOverrides.Count} override(s)");

            if (doc.Notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Notes:");
                foreach (var note in doc.Notes)
                {
                    sb.AppendLine($"  - {note}");
                }
            }

            if (doc.Parameters.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Parameters ({doc.Parameters.Count}):");
                foreach (var param in doc.Parameters)
                {
                    sb.AppendLine($"  - {param.Name} : {param.TypeName} = {param.DefaultValueDisplay}");
                }
            }

            if (doc.ClipOverrides.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Clip Overrides ({doc.ClipOverrides.Count}):");
                foreach (var ov in doc.ClipOverrides)
                {
                    sb.AppendLine($"  - {ov.OriginalClipName} -> {ov.OverrideClipName}");
                }
            }

            foreach (var layer in doc.Layers)
            {
                sb.AppendLine();
                sb.AppendLine($"Layer {layer.LayerIndex}: {layer.Name} ({layer.BlendingMode}, Weight={layer.DefaultWeight.ToString("0.##", CultureInfo.InvariantCulture)}, Default={layer.DefaultStateName})");
                if (layer.EntryTransitions.Count > 0)
                {
                    sb.AppendLine("  Entry Transitions:");
                    foreach (var tr in layer.EntryTransitions)
                    {
                        sb.AppendLine($"    Entry -> {tr.DestinationStateName} [{tr.SummaryLabel}]");
                    }
                }

                if (layer.AnyStateTransitions.Count > 0)
                {
                    sb.AppendLine("  Any State Transitions:");
                    foreach (var tr in layer.AnyStateTransitions)
                    {
                        sb.AppendLine($"    Any State -> {tr.DestinationStateName} [{tr.SummaryLabel}] ({tr.TimingSummary})");
                    }
                }

                foreach (var state in layer.States)
                {
                    var marker = state.IsDefault ? "* [DEFAULT]" : "  -";
                    sb.AppendLine($"  {marker} {state.Name} (Motion: {state.MotionBadge}, Speed: {state.SpeedDisplay}, Loop: {state.Loop})");
                    if (state.BlendTree != null)
                    {
                        var outline = FormatBlendTreeOutline(state.BlendTree, "      ");
                        if (!string.IsNullOrWhiteSpace(outline))
                        {
                            sb.Append(outline);
                        }
                    }

                    foreach (var tr in state.Transitions)
                    {
                        sb.AppendLine($"      -> {tr.DestinationStateName} [{tr.SummaryLabel}] ({tr.TimingSummary})");
                    }
                }
            }

            return sb.ToString();
        }

        public static string FormatBlendTreeOutline(AnimatorBlendTreeNode? root, string baseIndent = "")
        {
            if (root == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            AppendBlendTreeNodeLines(sb, root, baseIndent, depth: 0, edgePrefix: string.Empty);
            return sb.ToString();
        }

        private static void AppendBlendTreeNodeLines(
            StringBuilder sb,
            AnimatorBlendTreeNode node,
            string baseIndent,
            int depth,
            string edgePrefix)
        {
            if (depth > MaxBlendTreeRecursionDepth)
            {
                return;
            }

            var indent = baseIndent + new string(' ', depth * 2);
            if (node.IsLeafClip)
            {
                var clipLabel = node.Clip?.DisplayLabel ?? "(Missing Clip)";
                sb.Append(indent).Append(edgePrefix).Append("Clip: ").AppendLine(clipLabel);
                return;
            }

            var paramInfo = node.BlendTypeCode switch
            {
                0 => string.IsNullOrWhiteSpace(node.ParameterXName) ? string.Empty : $" [{node.ParameterXName}]",
                1 or 2 or 3 => $" [{node.ParameterXName}, {node.ParameterYName}]",
                4 => node.NormalizedDirectBlendValues ? " [Normalized]" : string.Empty,
                _ => string.Empty
            };

            sb.Append(indent).Append(edgePrefix).Append(node.BlendTypeName).AppendLine(paramInfo);
            foreach (var child in node.Children)
            {
                string childPrefix;
                if (child.PositionX.HasValue && child.PositionY.HasValue)
                {
                    childPrefix = string.Format(
                        CultureInfo.InvariantCulture,
                        "({0:0.##}, {1:0.##}) -> ",
                        child.PositionX.Value,
                        child.PositionY.Value);
                }
                else if (child.Threshold.HasValue)
                {
                    childPrefix = string.Format(
                        CultureInfo.InvariantCulture,
                        "[t={0:0.##}] -> ",
                        child.Threshold.Value);
                }
                else if (!string.IsNullOrWhiteSpace(child.DirectBlendParameter))
                {
                    childPrefix = $"[{child.DirectBlendParameter}] -> ";
                }
                else
                {
                    childPrefix = $"[{child.ChildIndex}] -> ";
                }

                AppendBlendTreeNodeLines(sb, child.Node, baseIndent, depth + 1, childPrefix);
            }
        }

        private static AnimatorGraphDocument BuildControllerDocumentCore(
            AnimatorController controller,
            string displayName,
            string assetKind,
            string sourceFile,
            long pathId,
            string baseControllerName,
            long baseControllerPathId,
            IReadOnlyList<AnimatorClipOverrideEntry> clipOverrides,
            ClipOverrideLookup? overrideLookup,
            List<string>? initialNotes,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver)
        {
            var notes = initialNotes != null ? new List<string>(initialNotes) : new List<string>();
            var tos = BuildTosDictionary(controller.m_TOS);
            var controllerConst = controller.m_Controller;

            if (controllerConst == null)
            {
                notes.Add("AnimatorController has no ControllerConstant state machine data.");
                return new AnimatorGraphDocument
                {
                    ControllerName = displayName,
                    AssetKind = assetKind,
                    SourceFile = sourceFile,
                    PathId = pathId,
                    BaseControllerName = baseControllerName,
                    BaseControllerPathId = baseControllerPathId,
                    MultiThreadedStateMachine = controller.m_MultiThreadedStateMachine,
                    Parameters = Array.Empty<AnimatorGraphParameter>(),
                    ClipOverrides = clipOverrides,
                    Layers = Array.Empty<AnimatorGraphLayer>(),
                    Notes = notes
                };
            }

            var parameters = ExtractParameters(controllerConst, tos, out var paramByHash);
            var layers = ExtractLayers(controller, controllerConst, tos, paramByHash, overrideLookup, clipResolver, notes);

            return new AnimatorGraphDocument
            {
                ControllerName = displayName,
                AssetKind = assetKind,
                SourceFile = sourceFile,
                PathId = pathId,
                BaseControllerName = baseControllerName,
                BaseControllerPathId = baseControllerPathId,
                MultiThreadedStateMachine = controller.m_MultiThreadedStateMachine,
                Parameters = parameters,
                ClipOverrides = clipOverrides,
                Layers = layers,
                Notes = notes
            };
        }

        private static Dictionary<uint, string> BuildTosDictionary(KeyValuePair<uint, string>[]? tosEntries)
        {
            var dict = new Dictionary<uint, string>();
            if (tosEntries == null || tosEntries.Length == 0)
            {
                return dict;
            }

            for (int i = 0; i < tosEntries.Length; i++)
            {
                var entry = tosEntries[i];
                if (!string.IsNullOrEmpty(entry.Value))
                {
                    dict.TryAdd(entry.Key, entry.Value);
                }
            }

            return dict;
        }

        private static IReadOnlyList<AnimatorGraphParameter> ExtractParameters(
            ControllerConstant controllerConst,
            Dictionary<uint, string> tos,
            out Dictionary<uint, AnimatorGraphParameter> paramByHash)
        {
            paramByHash = new Dictionary<uint, AnimatorGraphParameter>();
            var values = controllerConst.m_Values?.m_ValueArray;
            if (values == null || values.Length == 0)
            {
                return Array.Empty<AnimatorGraphParameter>();
            }

            var defaults = controllerConst.m_DefaultValues;
            var floatVals = defaults?.m_FloatValues;
            var intVals = defaults?.m_IntValues;
            var boolVals = defaults?.m_BoolValues;

            var list = new List<AnimatorGraphParameter>(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                var vc = values[i];
                if (vc == null)
                {
                    continue;
                }

                string name = ResolveTos(tos, vc.m_ID, $"Param_{vc.m_ID}");
                string typeName;
                float? defaultFloat = null;
                int? defaultInt = null;
                bool? defaultBool = null;
                string displayVal;

                switch (vc.m_Type)
                {
                    case 1: // Float
                        typeName = "Float";
                        if (floatVals != null && vc.m_Index < (uint)floatVals.Length)
                        {
                            defaultFloat = SanitizeFloat(floatVals[vc.m_Index]);
                            displayVal = defaultFloat.Value.ToString("0.###", CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            defaultFloat = 0f;
                            displayVal = "0";
                        }
                        break;

                    case 3: // Int
                        typeName = "Int";
                        if (intVals != null && vc.m_Index < (uint)intVals.Length)
                        {
                            defaultInt = intVals[vc.m_Index];
                            displayVal = defaultInt.Value.ToString(CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            defaultInt = 0;
                            displayVal = "0";
                        }
                        break;

                    case 4: // Bool
                        typeName = "Bool";
                        if (boolVals != null && vc.m_Index < (uint)boolVals.Length)
                        {
                            defaultBool = boolVals[vc.m_Index];
                            displayVal = defaultBool.Value ? "true" : "false";
                        }
                        else
                        {
                            defaultBool = false;
                            displayVal = "false";
                        }
                        break;

                    case 9: // Trigger
                        typeName = "Trigger";
                        if (boolVals != null && vc.m_Index < (uint)boolVals.Length)
                        {
                            defaultBool = boolVals[vc.m_Index];
                            displayVal = defaultBool.Value ? "armed" : "idle";
                        }
                        else
                        {
                            defaultBool = false;
                            displayVal = "idle";
                        }
                        break;

                    default:
                        typeName = $"Unknown({vc.m_Type})";
                        displayVal = "-";
                        break;
                }

                var param = new AnimatorGraphParameter
                {
                    Index = i,
                    HashId = vc.m_ID,
                    Name = name,
                    TypeCode = vc.m_Type,
                    TypeName = typeName,
                    ValueIndex = vc.m_Index,
                    DefaultFloat = defaultFloat,
                    DefaultInt = defaultInt,
                    DefaultBool = defaultBool,
                    DefaultValueDisplay = displayVal
                };

                list.Add(param);
                paramByHash.TryAdd(vc.m_ID, param);
            }

            return list;
        }

        private static IReadOnlyList<AnimatorGraphLayer> ExtractLayers(
            AnimatorController controller,
            ControllerConstant controllerConst,
            Dictionary<uint, string> tos,
            Dictionary<uint, AnimatorGraphParameter> paramByHash,
            ClipOverrideLookup? overrideLookup,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver,
            List<string> notes)
        {
            var layerArray = controllerConst.m_LayerArray;
            if (layerArray == null || layerArray.Length == 0)
            {
                return Array.Empty<AnimatorGraphLayer>();
            }

            var stateMachines = controllerConst.m_StateMachineArray;
            var result = new List<AnimatorGraphLayer>(layerArray.Length);

            for (int layerIdx = 0; layerIdx < layerArray.Length; layerIdx++)
            {
                var layerConst = layerArray[layerIdx];
                if (layerConst == null)
                {
                    continue;
                }

                string layerName = ResolveTos(tos, layerConst.m_Binding, $"Layer {layerIdx}");
                string blendingMode = layerConst.m_LayerBlendingMode switch
                {
                    0 => "Override",
                    1 => "Additive",
                    _ => $"Mode_{layerConst.m_LayerBlendingMode}"
                };

                StateMachineConstant? sm = null;
                if (stateMachines != null && layerConst.m_StateMachineIndex < (uint)stateMachines.Length)
                {
                    sm = stateMachines[layerConst.m_StateMachineIndex];
                }
                else
                {
                    notes.Add($"Layer '{layerName}' references out-of-range StateMachine index {layerConst.m_StateMachineIndex}.");
                }

                if (sm == null)
                {
                    result.Add(new AnimatorGraphLayer
                    {
                        LayerIndex = layerIdx,
                        Name = layerName,
                        StateMachineIndex = layerConst.m_StateMachineIndex,
                        DefaultWeight = SanitizeFloat(layerConst.m_DefaultWeight),
                        BlendingMode = blendingMode,
                        IKPass = layerConst.m_IKPass,
                        SyncedLayerAffectsTiming = layerConst.m_SyncedLayerAffectsTiming,
                        DefaultStateIndex = -1,
                        DefaultStateName = "None"
                    });
                    continue;
                }

                var rawStates = sm.m_StateConstantArray ?? Array.Empty<StateConstant>();
                var rawSelectors = sm.m_SelectorStateConstantArray;
                int stateCount = rawStates.Length;
                int defaultStateIdx = stateCount > 0 && sm.m_DefaultState < (uint)stateCount
                    ? (int)sm.m_DefaultState
                    : -1;

                var stateNames = new string[stateCount];
                for (int s = 0; s < stateCount; s++)
                {
                    var st = rawStates[s];
                    if (st == null)
                    {
                        stateNames[s] = $"State_{s}";
                    }
                    else
                    {
                        var byName = TryGetTos(tos, st.m_NameID);
                        var byFull = TryGetTos(tos, st.m_FullPathID);
                        stateNames[s] = !string.IsNullOrWhiteSpace(byName)
                            ? byName
                            : (!string.IsNullOrWhiteSpace(byFull) ? byFull : $"State_{s}");
                    }
                }

                string defaultStateName = defaultStateIdx >= 0 ? stateNames[defaultStateIdx] : "None";

                // Build AnyState transitions
                var anyStateTransitions = new List<AnimatorGraphTransition>();
                if (sm.m_AnyStateTransitionConstantArray != null)
                {
                    for (int t = 0; t < sm.m_AnyStateTransitionConstantArray.Length; t++)
                    {
                        var tr = sm.m_AnyStateTransitionConstantArray[t];
                        if (tr == null)
                        {
                            continue;
                        }

                        anyStateTransitions.Add(BuildTransition(
                            tr,
                            AnimatorGraphTransition.AnyStateSourceIndex,
                            "Any State",
                            stateNames,
                            rawSelectors,
                            tos,
                            paramByHash));
                    }
                }

                // Build Entry transitions from SelectorStateConstantArray where m_isEntry == true
                var entryTransitions = new List<AnimatorGraphTransition>();
                if (rawSelectors != null)
                {
                    for (int selIdx = 0; selIdx < rawSelectors.Length; selIdx++)
                    {
                        var selector = rawSelectors[selIdx];
                        if (selector == null || !selector.m_isEntry || selector.m_TransitionConstantArray == null)
                        {
                            continue;
                        }

                        for (int t = 0; t < selector.m_TransitionConstantArray.Length; t++)
                        {
                            var selTr = selector.m_TransitionConstantArray[t];
                            if (selTr == null)
                            {
                                continue;
                            }

                            ResolveDestination(selTr.m_Destination, stateNames, rawSelectors, tos, out int destIdx, out string destName);
                            var conditions = ExtractConditions(selTr.m_ConditionConstantArray, tos, paramByHash);

                            entryTransitions.Add(new AnimatorGraphTransition
                            {
                                SourceStateIndex = AnimatorGraphTransition.EntrySourceIndex,
                                SourceStateName = "Entry",
                                DestinationStateIndex = destIdx,
                                DestinationStateName = destName,
                                HasExitTime = false,
                                ExitTime = 0f,
                                Duration = 0f,
                                Offset = 0f,
                                HasFixedDuration = true,
                                CanTransitionToSelf = false,
                                Conditions = conditions
                            });
                        }
                    }
                }

                // Build per-state transitions first so layout BFS can read exact outgoing edges
                var perStateTransitions = new IReadOnlyList<AnimatorGraphTransition>[stateCount];
                for (int s = 0; s < stateCount; s++)
                {
                    var st = rawStates[s];
                    if (st?.m_TransitionConstantArray == null || st.m_TransitionConstantArray.Length == 0)
                    {
                        perStateTransitions[s] = Array.Empty<AnimatorGraphTransition>();
                        continue;
                    }

                    var trList = new List<AnimatorGraphTransition>(st.m_TransitionConstantArray.Length);
                    for (int t = 0; t < st.m_TransitionConstantArray.Length; t++)
                    {
                        var tr = st.m_TransitionConstantArray[t];
                        if (tr == null)
                        {
                            continue;
                        }

                        trList.Add(BuildTransition(
                            tr,
                            s,
                            stateNames[s],
                            stateNames,
                            rawSelectors,
                            tos,
                            paramByHash));
                    }

                    perStateTransitions[s] = trList;
                }

                // Deterministic BFS depth layout so no two states ever overlap
                var layout = ComputeBfsStateLayout(
                    stateCount,
                    defaultStateIdx,
                    perStateTransitions,
                    entryTransitions,
                    anyStateTransitions);

                // Build final state objects
                var states = new List<AnimatorGraphState>(stateCount);
                for (int s = 0; s < stateCount; s++)
                {
                    var st = rawStates[s];
                    var pos = layout[s];
                    if (st == null)
                    {
                        states.Add(new AnimatorGraphState
                        {
                            StateIndex = s,
                            Name = stateNames[s],
                            FullPath = $"{layerName}.{stateNames[s]}",
                            IsDefault = s == defaultStateIdx,
                            Transitions = perStateTransitions[s],
                            LayoutX = pos.X,
                            LayoutY = pos.Y,
                            LayoutColumn = pos.Column,
                            LayoutRow = pos.Row
                        });
                        continue;
                    }

                    var fullPath = TryGetTos(tos, st.m_FullPathID)
                        ?? TryGetTos(tos, st.m_PathID)
                        ?? $"{layerName}.{stateNames[s]}";
                    var tag = st.m_TagID != 0 ? (TryGetTos(tos, st.m_TagID) ?? $"Tag_{st.m_TagID}") : string.Empty;

                    ExtractStateMotion(
                        controller,
                        layerConst,
                        st,
                        tos,
                        overrideLookup,
                        clipResolver,
                        out var motionKind,
                        out var clips,
                        out var blendTree);

                    var primaryClip = clips.Count > 0 ? clips[0] : null;
                    bool isOverridden = clips.Any(c => c.IsOverridden);

                    states.Add(new AnimatorGraphState
                    {
                        StateIndex = s,
                        Name = stateNames[s],
                        FullPath = fullPath,
                        Tag = tag,
                        HashId = st.m_NameID,
                        IsDefault = s == defaultStateIdx,
                        Speed = SanitizeFloat(st.m_Speed),
                        CycleOffset = SanitizeFloat(st.m_CycleOffset),
                        Loop = st.m_Loop,
                        Mirror = st.m_Mirror,
                        IKOnFeet = st.m_IKOnFeet,
                        WriteDefaultValues = st.m_WriteDefaultValues,
                        MotionKind = motionKind,
                        PrimaryClipName = primaryClip?.Name ?? string.Empty,
                        PrimaryClipPathId = primaryClip?.PathId ?? 0,
                        PrimaryClipUniqueId = primaryClip?.UniqueId ?? string.Empty,
                        IsOverriddenClip = isOverridden,
                        Clips = clips,
                        BlendTree = blendTree,
                        Transitions = perStateTransitions[s],
                        LayoutX = pos.X,
                        LayoutY = pos.Y,
                        LayoutColumn = pos.Column,
                        LayoutRow = pos.Row
                    });
                }

                result.Add(new AnimatorGraphLayer
                {
                    LayerIndex = layerIdx,
                    Name = layerName,
                    StateMachineIndex = layerConst.m_StateMachineIndex,
                    DefaultWeight = SanitizeFloat(layerConst.m_DefaultWeight),
                    BlendingMode = blendingMode,
                    IKPass = layerConst.m_IKPass,
                    SyncedLayerAffectsTiming = layerConst.m_SyncedLayerAffectsTiming,
                    DefaultStateIndex = defaultStateIdx,
                    DefaultStateName = defaultStateName,
                    States = states,
                    AnyStateTransitions = anyStateTransitions,
                    EntryTransitions = entryTransitions
                });
            }

            return result;
        }

        private readonly record struct StateLayoutPosition(double X, double Y, int Column, int Row);

        private static StateLayoutPosition[] ComputeBfsStateLayout(
            int stateCount,
            int defaultStateIndex,
            IReadOnlyList<AnimatorGraphTransition>[] stateTransitions,
            IReadOnlyList<AnimatorGraphTransition> entryTransitions,
            IReadOnlyList<AnimatorGraphTransition> anyStateTransitions)
        {
            var result = new StateLayoutPosition[stateCount];
            if (stateCount == 0)
            {
                return result;
            }

            var columns = new int[stateCount];
            Array.Fill(columns, -1);

            var queue = new Queue<int>();

            void EnqueueIfUnvisited(int stateIdx, int column)
            {
                if (stateIdx >= 0 && stateIdx < stateCount && columns[stateIdx] < 0)
                {
                    columns[stateIdx] = column;
                    queue.Enqueue(stateIdx);
                }
            }

            void DrainBfsQueue()
            {
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    int nextCol = columns[current] + 1;
                    var outgoing = stateTransitions[current];
                    for (int i = 0; i < outgoing.Count; i++)
                    {
                        int dest = outgoing[i].DestinationStateIndex;
                        if (dest >= 0 && dest < stateCount && columns[dest] < 0)
                        {
                            columns[dest] = nextCol;
                            queue.Enqueue(dest);
                        }
                    }
                }
            }

            // Seed from Default state first, then Entry targets, then AnyState targets
            if (defaultStateIndex >= 0 && defaultStateIndex < stateCount)
            {
                EnqueueIfUnvisited(defaultStateIndex, 0);
            }

            for (int i = 0; i < entryTransitions.Count; i++)
            {
                EnqueueIfUnvisited(entryTransitions[i].DestinationStateIndex, 0);
            }

            DrainBfsQueue();

            for (int i = 0; i < anyStateTransitions.Count; i++)
            {
                EnqueueIfUnvisited(anyStateTransitions[i].DestinationStateIndex, 1);
            }

            DrainBfsQueue();

            // Place any unreachable components deterministically starting at column 0
            for (int s = 0; s < stateCount; s++)
            {
                if (columns[s] < 0)
                {
                    EnqueueIfUnvisited(s, 0);
                    DrainBfsQueue();
                }
            }

            // Pack each column vertically in deterministic order (Default first, then StateIndex asc)
            var byColumn = new SortedDictionary<int, List<int>>();
            for (int s = 0; s < stateCount; s++)
            {
                int col = Math.Max(0, columns[s]);
                if (!byColumn.TryGetValue(col, out var list))
                {
                    list = new List<int>();
                    byColumn[col] = list;
                }

                list.Add(s);
            }

            foreach (var kvp in byColumn)
            {
                int col = kvp.Key;
                var colStates = kvp.Value;
                colStates.Sort((a, b) =>
                {
                    bool aDef = a == defaultStateIndex;
                    bool bDef = b == defaultStateIndex;
                    if (aDef != bDef)
                    {
                        return aDef ? -1 : 1;
                    }

                    return a.CompareTo(b);
                });

                for (int row = 0; row < colStates.Count; row++)
                {
                    int stateIdx = colStates[row];
                    double x = LayoutStartX + col * LayoutColumnSpacing;
                    double y = LayoutStartY + row * LayoutRowSpacing;
                    result[stateIdx] = new StateLayoutPosition(x, y, col, row);
                }
            }

            return result;
        }

        private static void ExtractStateMotion(
            AnimatorController controller,
            LayerConstant layer,
            StateConstant state,
            Dictionary<uint, string> tos,
            ClipOverrideLookup? overrideLookup,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver,
            out AnimatorMotionKind motionKind,
            out IReadOnlyList<AnimatorGraphClipRef> clips,
            out AnimatorBlendTreeNode? blendTree)
        {
            motionKind = AnimatorMotionKind.Empty;
            clips = Array.Empty<AnimatorGraphClipRef>();
            blendTree = null;

            var btArray = state.m_BlendTreeConstantArray;
            if (btArray == null || btArray.Length == 0)
            {
                return;
            }

            BlendTreeConstant? selectedBt = null;
            if (state.m_BlendTreeConstantIndexArray != null && state.m_BlendTreeConstantIndexArray.Length > 0)
            {
                int motionSetIdx = layer.m_StateMachineMotionSetIndex < (uint)state.m_BlendTreeConstantIndexArray.Length
                    ? (int)layer.m_StateMachineMotionSetIndex
                    : 0;
                int btIndex = state.m_BlendTreeConstantIndexArray[motionSetIdx];
                if (btIndex < 0)
                {
                    return;
                }

                if (btIndex < btArray.Length)
                {
                    selectedBt = btArray[btIndex];
                }
            }
            else
            {
                selectedBt = btArray[0];
            }

            if (selectedBt?.m_NodeArray == null || selectedBt.m_NodeArray.Length == 0)
            {
                return;
            }

            var nodes = selectedBt.m_NodeArray;
            var rootNodeConst = nodes[0];
            if (rootNodeConst == null)
            {
                return;
            }

            bool hasChildren = rootNodeConst.m_ChildIndices != null && rootNodeConst.m_ChildIndices.Length > 0;
            if (!hasChildren)
            {
                var clipRef = ResolveClipFromNode(controller, rootNodeConst, overrideLookup, clipResolver);
                if (clipRef != null)
                {
                    motionKind = AnimatorMotionKind.Clip;
                    clips = new[] { clipRef };
                }

                return;
            }

            var collectedClips = new List<AnimatorGraphClipRef>();
            var seenClipKeys = new HashSet<string>(StringComparer.Ordinal);
            var activePath = new HashSet<int>();

            blendTree = BuildBlendTreeNodeRecursive(
                controller,
                nodes,
                nodeIndex: 0,
                tos,
                overrideLookup,
                clipResolver,
                collectedClips,
                seenClipKeys,
                activePath,
                depth: 0);

            motionKind = AnimatorMotionKind.BlendTree;
            clips = collectedClips;
        }

        private static AnimatorBlendTreeNode BuildBlendTreeNodeRecursive(
            AnimatorController controller,
            BlendTreeNodeConstant[] nodes,
            int nodeIndex,
            Dictionary<uint, string> tos,
            ClipOverrideLookup? overrideLookup,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver,
            List<AnimatorGraphClipRef> collectedClips,
            HashSet<string> seenClipKeys,
            HashSet<int> activePath,
            int depth)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length || nodes[nodeIndex] == null)
            {
                return new AnimatorBlendTreeNode
                {
                    NodeIndex = nodeIndex,
                    BlendTypeName = "Invalid",
                    IsLeafClip = true
                };
            }

            var node = nodes[nodeIndex];
            var childIndices = node.m_ChildIndices;
            bool isLeaf = childIndices == null || childIndices.Length == 0;

            if (isLeaf)
            {
                var clipRef = ResolveClipFromNode(controller, node, overrideLookup, clipResolver);
                if (clipRef != null)
                {
                    var key = !string.IsNullOrWhiteSpace(clipRef.UniqueId)
                        ? clipRef.UniqueId
                        : $"{clipRef.ClipIndex}:{clipRef.PathId}:{clipRef.Name}";
                    if (seenClipKeys.Add(key))
                    {
                        collectedClips.Add(clipRef);
                    }
                }

                return new AnimatorBlendTreeNode
                {
                    NodeIndex = nodeIndex,
                    BlendTypeCode = node.m_BlendType,
                    BlendTypeName = "Clip",
                    IsLeafClip = true,
                    Clip = clipRef,
                    Duration = SanitizeFloat(node.m_Duration),
                    CycleOffset = SanitizeFloat(node.m_CycleOffset),
                    Mirror = node.m_Mirror
                };
            }

            string blendTypeName = node.m_BlendType switch
            {
                0 => "1D",
                1 => "2D Simple Directional",
                2 => "2D Freeform Directional",
                3 => "2D Freeform Cartesian",
                4 => "Direct",
                _ => $"BlendType_{node.m_BlendType}"
            };

            string paramX = node.m_BlendEventID != 0 && node.m_BlendEventID != uint.MaxValue
                ? ResolveTos(tos, node.m_BlendEventID, $"Param_{node.m_BlendEventID}")
                : string.Empty;
            string paramY = node.m_BlendEventYID != 0 && node.m_BlendEventYID != uint.MaxValue
                ? ResolveTos(tos, node.m_BlendEventYID, $"Param_{node.m_BlendEventYID}")
                : string.Empty;

            var children = new List<AnimatorBlendTreeChild>();
            if (depth < MaxBlendTreeRecursionDepth && activePath.Add(nodeIndex))
            {
                try
                {
                    var thresholds = node.m_Blend1dData?.m_ChildThresholdArray ?? node.m_ChildThresholdArray;
                    var positions = node.m_Blend2dData?.m_ChildPositionArray;
                    var directParams = node.m_BlendDirectData?.m_ChildBlendEventIDArray;

                    for (int i = 0; i < childIndices!.Length; i++)
                    {
                        uint rawChildIdx = childIndices[i];
                        if (rawChildIdx >= (uint)nodes.Length)
                        {
                            continue;
                        }

                        int childIdx = (int)rawChildIdx;
                        if (activePath.Contains(childIdx))
                        {
                            continue;
                        }

                        float? threshold = null;
                        float? posX = null;
                        float? posY = null;
                        string directParam = string.Empty;

                        if (node.m_BlendType == 0 && thresholds != null && i < thresholds.Length)
                        {
                            threshold = SanitizeFloat(thresholds[i]);
                        }
                        else if (node.m_BlendType is 1 or 2 or 3 && positions != null && i < positions.Length)
                        {
                            posX = SanitizeFloat(positions[i].X);
                            posY = SanitizeFloat(positions[i].Y);
                        }
                        else if (node.m_BlendType == 4 && directParams != null && i < directParams.Length)
                        {
                            directParam = ResolveTos(tos, directParams[i], $"Param_{directParams[i]}");
                        }

                        var childNode = BuildBlendTreeNodeRecursive(
                            controller,
                            nodes,
                            childIdx,
                            tos,
                            overrideLookup,
                            clipResolver,
                            collectedClips,
                            seenClipKeys,
                            activePath,
                            depth + 1);

                        children.Add(new AnimatorBlendTreeChild
                        {
                            ChildIndex = i,
                            Threshold = threshold,
                            PositionX = posX,
                            PositionY = posY,
                            DirectBlendParameter = directParam,
                            Node = childNode
                        });
                    }
                }
                finally
                {
                    activePath.Remove(nodeIndex);
                }
            }

            return new AnimatorBlendTreeNode
            {
                NodeIndex = nodeIndex,
                BlendTypeCode = node.m_BlendType,
                BlendTypeName = blendTypeName,
                IsLeafClip = false,
                ParameterXName = paramX,
                ParameterYName = paramY,
                Duration = SanitizeFloat(node.m_Duration),
                CycleOffset = SanitizeFloat(node.m_CycleOffset),
                Mirror = node.m_Mirror,
                NormalizedDirectBlendValues = node.m_BlendDirectData?.m_NormalizedBlendValues ?? false,
                Children = children
            };
        }

        private static AnimatorGraphClipRef? ResolveClipFromNode(
            AnimatorController controller,
            BlendTreeNodeConstant node,
            ClipOverrideLookup? overrideLookup,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver)
        {
            if (node.m_ClipID == InvalidClipId)
            {
                return null;
            }

            var clipsArray = controller.m_AnimationClips;
            uint resolvedIndex = node.m_ClipID;
            PPtr<AnimationClip>? pptr = null;

            if (clipsArray != null)
            {
                if (node.m_ClipID < (uint)clipsArray.Length)
                {
                    pptr = clipsArray[node.m_ClipID];
                }
                else if (node.m_ClipIndex < (uint)clipsArray.Length)
                {
                    resolvedIndex = node.m_ClipIndex;
                    pptr = clipsArray[node.m_ClipIndex];
                }
            }

            var resolvedClip = ResolveAnimationClip(pptr, clipResolver);
            string origName = resolvedClip != null
                ? SafeName(resolvedClip.m_Name, $"AnimationClip_{resolvedClip.m_PathID}")
                : (pptr != null && pptr.m_PathID != 0 ? $"Clip_PathID_{pptr.m_PathID}" : $"Clip_{resolvedIndex}");
            long origPathId = resolvedClip?.m_PathID ?? pptr?.m_PathID ?? 0;
            string origSource = resolvedClip?.assetsFile?.fileName ?? TryGetPPtrFileName(pptr);
            string origUniqueId = BuildClipUniqueId(resolvedClip, pptr);

            if (overrideLookup != null && overrideLookup.TryFindOverride(origUniqueId, origPathId, origName, out var ov))
            {
                return new AnimatorGraphClipRef
                {
                    ClipIndex = resolvedIndex,
                    Name = ov.OverrideName,
                    PathId = ov.OverridePathId,
                    UniqueId = ov.OverrideUniqueId,
                    SourceFile = ov.OverrideSourceFile,
                    IsOverridden = true,
                    OriginalClipName = origName,
                    OriginalClipPathId = origPathId
                };
            }

            return new AnimatorGraphClipRef
            {
                ClipIndex = resolvedIndex,
                Name = origName,
                PathId = origPathId,
                UniqueId = origUniqueId,
                SourceFile = origSource,
                IsOverridden = false
            };
        }

        private static AnimatorGraphTransition BuildTransition(
            TransitionConstant tr,
            int sourceStateIndex,
            string sourceStateName,
            string[] stateNames,
            SelectorStateConstant[]? selectorStates,
            Dictionary<uint, string> tos,
            Dictionary<uint, AnimatorGraphParameter> paramByHash)
        {
            ResolveDestination(tr.m_DestinationState, stateNames, selectorStates, tos, out int destIdx, out string destName);
            var conditions = ExtractConditions(tr.m_ConditionConstantArray, tos, paramByHash);

            return new AnimatorGraphTransition
            {
                SourceStateIndex = sourceStateIndex,
                SourceStateName = sourceStateName,
                DestinationStateIndex = destIdx,
                DestinationStateName = destName,
                HasExitTime = tr.m_HasExitTime,
                ExitTime = SanitizeFloat(tr.m_ExitTime),
                Duration = SanitizeFloat(tr.m_TransitionDuration),
                Offset = SanitizeFloat(tr.m_TransitionOffset),
                HasFixedDuration = tr.m_HasFixedDuration,
                CanTransitionToSelf = tr.m_CanTransitionToSelf,
                Conditions = conditions
            };
        }

        private static void ResolveDestination(
            uint destinationRaw,
            string[] stateNames,
            SelectorStateConstant[]? selectorStates,
            Dictionary<uint, string> tos,
            out int destinationIndex,
            out string destinationName)
        {
            if (destinationRaw < (uint)stateNames.Length)
            {
                destinationIndex = (int)destinationRaw;
                destinationName = stateNames[destinationRaw];
                return;
            }

            destinationIndex = -1;
            if (destinationRaw == uint.MaxValue)
            {
                destinationName = "Exit";
                return;
            }

            if (selectorStates != null && selectorStates.Length > 0)
            {
                int selectorIdx = -1;
                if (destinationRaw >= 30000 && (destinationRaw - 30000) < (uint)selectorStates.Length)
                {
                    selectorIdx = (int)(destinationRaw - 30000);
                }
                else if (destinationRaw >= (uint)stateNames.Length && (destinationRaw - (uint)stateNames.Length) < (uint)selectorStates.Length)
                {
                    selectorIdx = (int)(destinationRaw - (uint)stateNames.Length);
                }

                if (selectorIdx >= 0)
                {
                    var sel = selectorStates[selectorIdx];
                    if (sel != null)
                    {
                        var tosName = TryGetTos(tos, sel.m_FullPathID);
                        if (!string.IsNullOrWhiteSpace(tosName))
                        {
                            destinationName = sel.m_isEntry ? $"{tosName} (Entry)" : $"{tosName} (Exit)";
                            return;
                        }

                        destinationName = sel.m_isEntry ? $"EntrySelector_{selectorIdx}" : "Exit";
                        return;
                    }
                }
            }

            destinationName = $"State_{destinationRaw}";
        }

        private static IReadOnlyList<AnimatorGraphCondition> ExtractConditions(
            ConditionConstant[]? conditionArray,
            Dictionary<uint, string> tos,
            Dictionary<uint, AnimatorGraphParameter> paramByHash)
        {
            if (conditionArray == null || conditionArray.Length == 0)
            {
                return Array.Empty<AnimatorGraphCondition>();
            }

            var list = new List<AnimatorGraphCondition>(conditionArray.Length);
            for (int i = 0; i < conditionArray.Length; i++)
            {
                var cond = conditionArray[i];
                if (cond == null)
                {
                    continue;
                }

                paramByHash.TryGetValue(cond.m_EventID, out var param);
                string paramName = param?.Name ?? ResolveTos(tos, cond.m_EventID, $"Param_{cond.m_EventID}");
                float threshold = SanitizeFloat(cond.m_EventThreshold);

                string modeName = cond.m_ConditionMode switch
                {
                    1 => "If",
                    2 => "IfNot",
                    3 => "Greater",
                    4 => "Less",
                    6 => "Equals",
                    7 => "NotEqual",
                    _ => $"Mode_{cond.m_ConditionMode}"
                };

                string thresholdStr = param?.TypeCode == 3
                    ? ((int)Math.Round(threshold)).ToString(CultureInfo.InvariantCulture)
                    : threshold.ToString("0.###", CultureInfo.InvariantCulture);

                string expr = cond.m_ConditionMode switch
                {
                    1 => param?.TypeCode == 9 ? $"{paramName} (Trigger)" : $"{paramName} == true",
                    2 => $"{paramName} == false",
                    3 => $"{paramName} > {thresholdStr}",
                    4 => $"{paramName} < {thresholdStr}",
                    6 => $"{paramName} == {thresholdStr}",
                    7 => $"{paramName} != {thresholdStr}",
                    _ => $"{paramName} [{modeName} {thresholdStr}]"
                };

                list.Add(new AnimatorGraphCondition
                {
                    ModeCode = cond.m_ConditionMode,
                    ModeName = modeName,
                    ParameterHashId = cond.m_EventID,
                    ParameterName = paramName,
                    Threshold = threshold,
                    FormattedExpression = expr
                });
            }

            return list;
        }

        private sealed record ResolvedOverrideTarget(
            string OverrideName,
            long OverridePathId,
            string OverrideUniqueId,
            string OverrideSourceFile);

        private sealed class ClipOverrideLookup
        {
            private readonly Dictionary<string, ResolvedOverrideTarget> _byUniqueId = new(StringComparer.Ordinal);
            private readonly Dictionary<long, ResolvedOverrideTarget> _byPathId = new();
            private readonly Dictionary<string, ResolvedOverrideTarget> _byName = new(StringComparer.OrdinalIgnoreCase);

            public void Add(
                string origUniqueId,
                long origPathId,
                string origName,
                ResolvedOverrideTarget target)
            {
                if (!string.IsNullOrWhiteSpace(origUniqueId))
                {
                    _byUniqueId.TryAdd(origUniqueId, target);
                }

                if (origPathId != 0)
                {
                    _byPathId.TryAdd(origPathId, target);
                }

                if (!string.IsNullOrWhiteSpace(origName))
                {
                    _byName.TryAdd(origName, target);
                }
            }

            public bool TryFindOverride(string uniqueId, long pathId, string name, out ResolvedOverrideTarget target)
            {
                if (!string.IsNullOrWhiteSpace(uniqueId) && _byUniqueId.TryGetValue(uniqueId, out target!))
                {
                    return true;
                }

                if (pathId != 0 && _byPathId.TryGetValue(pathId, out target!))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out target!))
                {
                    return true;
                }

                target = null!;
                return false;
            }
        }

        private static List<AnimatorClipOverrideEntry> ExtractClipOverrides(
            AnimatorOverrideController overrideController,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver,
            out ClipOverrideLookup lookup)
        {
            var entries = new List<AnimatorClipOverrideEntry>();
            lookup = new ClipOverrideLookup();
            AppendOverridesInto(overrideController, clipResolver, entries, lookup);
            return entries;
        }

        private static void AppendOverridesInto(
            AnimatorOverrideController overrideController,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver,
            List<AnimatorClipOverrideEntry> entries,
            ClipOverrideLookup lookup)
        {
            var rawClips = overrideController.m_Clips;
            if (rawClips == null || rawClips.Length == 0)
            {
                return;
            }

            for (int i = 0; i < rawClips.Length; i++)
            {
                var ov = rawClips[i];
                if (ov == null)
                {
                    continue;
                }

                var origClip = ResolveAnimationClip(ov.m_OriginalClip, clipResolver);
                var newClip = ResolveAnimationClip(ov.m_OverrideClip, clipResolver);

                long origPathId = origClip?.m_PathID ?? ov.m_OriginalClip?.m_PathID ?? 0;
                long newPathId = newClip?.m_PathID ?? ov.m_OverrideClip?.m_PathID ?? 0;

                string origName = origClip != null
                    ? SafeName(origClip.m_Name, $"AnimationClip_{origClip.m_PathID}")
                    : (origPathId != 0 ? $"Clip_PathID_{origPathId}" : "(None)");
                string newName = newClip != null
                    ? SafeName(newClip.m_Name, $"AnimationClip_{newClip.m_PathID}")
                    : (newPathId != 0 ? $"Clip_PathID_{newPathId}" : "(None)");

                string origUniqueId = BuildClipUniqueId(origClip, ov.m_OriginalClip);
                string newUniqueId = BuildClipUniqueId(newClip, ov.m_OverrideClip);
                string newSource = newClip?.assetsFile?.fileName ?? TryGetPPtrFileName(ov.m_OverrideClip);

                entries.Add(new AnimatorClipOverrideEntry
                {
                    OriginalClipName = origName,
                    OriginalClipPathId = origPathId,
                    OriginalClipUniqueId = origUniqueId,
                    OverrideClipName = newName,
                    OverrideClipPathId = newPathId,
                    OverrideClipUniqueId = newUniqueId
                });

                if (newClip != null || newPathId != 0)
                {
                    lookup.Add(
                        origUniqueId,
                        origPathId,
                        origClip != null ? origName : string.Empty,
                        new ResolvedOverrideTarget(newName, newPathId, newUniqueId, newSource));
                }
            }
        }

        private static AnimatorController? ResolveBaseAnimatorController(
            AnimatorOverrideController overrideController,
            Func<PPtr<RuntimeAnimatorController>, RuntimeAnimatorController?>? controllerResolver,
            List<AnimatorClipOverrideEntry> clipOverrides,
            ClipOverrideLookup overrideLookup,
            List<string> notes,
            Func<PPtr<AnimationClip>, AnimationClip?>? clipResolver)
        {
            var visited = new HashSet<RuntimeAnimatorController> { overrideController };
            RuntimeAnimatorController? current = ResolveRuntimeController(overrideController.m_Controller, controllerResolver);
            int depth = 0;

            while (current is AnimatorOverrideController chained && depth < MaxOverrideChainDepth)
            {
                if (!visited.Add(chained))
                {
                    notes.Add("Detected cyclic AnimatorOverrideController reference chain; stopped resolution.");
                    return null;
                }

                AppendOverridesInto(chained, clipResolver, clipOverrides, overrideLookup);
                current = ResolveRuntimeController(chained.m_Controller, controllerResolver);
                depth++;
            }

            return current as AnimatorController;
        }

        private static RuntimeAnimatorController? ResolveRuntimeController(
            PPtr<RuntimeAnimatorController>? pptr,
            Func<PPtr<RuntimeAnimatorController>, RuntimeAnimatorController?>? customResolver)
        {
            if (pptr == null || pptr.IsNull)
            {
                return null;
            }

            if (TryResolvePPtr(pptr, out var resolved) && resolved != null)
            {
                return resolved;
            }

            if (customResolver != null)
            {
                try
                {
                    return customResolver(pptr);
                }
                catch
                {
                }
            }

            return null;
        }

        private static AnimationClip? ResolveAnimationClip(
            PPtr<AnimationClip>? pptr,
            Func<PPtr<AnimationClip>, AnimationClip?>? customResolver)
        {
            if (pptr == null || pptr.IsNull)
            {
                return null;
            }

            if (TryResolvePPtr(pptr, out var resolved) && resolved != null)
            {
                return resolved;
            }

            if (customResolver != null)
            {
                try
                {
                    return customResolver(pptr);
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool TryResolvePPtr<T>(PPtr<T>? pptr, out T? result) where T : UnityObject
        {
            result = null;
            if (pptr == null || pptr.IsNull)
            {
                return false;
            }

            try
            {
                if (pptr.TryGet(out var resolved))
                {
                    result = resolved;
                    return true;
                }
            }
            catch
            {
                // Guard against synthetic or unattached PPtrs whose SerializedFile is null.
            }

            return false;
        }

        private static string TryGetPPtrFileName<T>(PPtr<T>? pptr) where T : UnityObject
        {
            if (pptr == null || pptr.IsNull)
            {
                return string.Empty;
            }

            try
            {
                if (pptr.TryGetAssetsFile(out var file) && file != null)
                {
                    return file.fileName ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string BuildClipUniqueId(AnimationClip? clip, PPtr<AnimationClip>? pptr)
        {
            if (clip != null && clip.assetsFile != null)
            {
                return AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
            }

            if (pptr != null && !pptr.IsNull)
            {
                try
                {
                    if (pptr.TryGetAssetsFile(out var file) && file != null)
                    {
                        return AssetHandle.BuildUniqueID(file, pptr.m_PathID);
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private static string? TryGetTos(Dictionary<uint, string> tos, uint hash)
        {
            if (hash == 0)
            {
                return null;
            }

            return tos.TryGetValue(hash, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        private static string ResolveTos(Dictionary<uint, string> tos, uint hash, string fallback)
        {
            return TryGetTos(tos, hash) ?? fallback;
        }

        private static string SafeName(string? name, string fallback)
        {
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }

        private static float SanitizeFloat(float value)
        {
            return float.IsFinite(value) ? value : 0f;
        }

        private static string EscapeDotString(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(input.Length + 8);
            for (int i = 0; i < input.Length; i++)
            {
                char ch = input[i];
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append(' ');
                        break;
                    default:
                        if (!char.IsControl(ch))
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }

            return sb.ToString();
        }

        private static string EscapeDotLabelMultiline(IReadOnlyList<string> lines)
        {
            var escaped = new string[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                escaped[i] = EscapeDotString(lines[i]);
            }

            return string.Join("\\n", escaped);
        }

        private static string SanitizeDotIdentifier(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "Unknown";
            }

            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return sb.ToString();
        }

        private static void WriteUtf8FileAtomic(string outputPath, string content)
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, fullPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                throw;
            }
        }
    }
}
