using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class AssetPropertyNode : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; init; } = string.Empty;
        public string ValueText { get; init; } = string.Empty;
        public string TypeHint { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public AssetReferenceItem? PPtrTarget { get; init; }
        public IReadOnlyList<AssetPropertyNode> Children { get; init; } = Array.Empty<AssetPropertyNode>();

        public bool IsPPtr => PPtrTarget != null;
        public bool IsNavigablePPtr => PPtrTarget?.IsNavigable == true;
        public bool HasScalarOrSummaryValue => !IsPPtr && !string.IsNullOrEmpty(ValueText);
        public bool HasTypeHint => !string.IsNullOrEmpty(TypeHint);
        public bool HasChildren => Children.Count > 0;

        public string PPtrDisplayLabel
        {
            get
            {
                if (PPtrTarget == null)
                {
                    return string.Empty;
                }

                var typeLabel = !string.IsNullOrWhiteSpace(PPtrTarget.TypeName)
                    ? PPtrTarget.TypeName
                    : AssetItem.GetCachedTypeString(PPtrTarget.AssetType);
                var nameLabel = !string.IsNullOrWhiteSpace(PPtrTarget.Name)
                    ? PPtrTarget.Name
                    : $"PathID {PPtrTarget.PathIdText}";
                return $"→ [{typeLabel}] {nameLabel}";
            }
        }

        public string PPtrTooltip
        {
            get
            {
                if (PPtrTarget == null)
                {
                    return string.Empty;
                }

                var filePart = !string.IsNullOrWhiteSpace(PPtrTarget.SerializedFileName)
                    ? $" • {PPtrTarget.SerializedFileName}"
                    : string.Empty;
                return $"Jump to {PPtrTarget.TypeName}: {PPtrTarget.Name} (PathID: {PPtrTarget.PathIdText}{filePart})";
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public bool MatchesQuery(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var needle = query.Trim();
            if ((!string.IsNullOrEmpty(Name) && Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(ValueText) && ValueText.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(TypeHint) && TypeHint.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(Path) && Path.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (PPtrTarget != null && PPtrTarget.MatchesFilter(needle))
            {
                return true;
            }

            return false;
        }

        internal AssetPropertyNode CloneWith(IReadOnlyList<AssetPropertyNode> children, bool isExpanded)
        {
            return new AssetPropertyNode
            {
                Name = Name,
                ValueText = ValueText,
                TypeHint = TypeHint,
                Path = Path,
                PPtrTarget = PPtrTarget,
                Children = children,
                IsExpanded = isExpanded
            };
        }
    }

    public sealed class MonoBehaviourTreeBuildOptions
    {
        public static readonly MonoBehaviourTreeBuildOptions Default = new();

        public int MaxDepth { get; init; } = 16;
        public int MaxArrayElements { get; init; } = 256;
        public int MaxTotalNodes { get; init; } = 4096;
        public int MaxStringPreviewLength { get; init; } = 512;
        public int InitialExpandDepth { get; init; } = 1;
    }

    public sealed class MonoBehaviourTreeBuildResult
    {
        public static readonly MonoBehaviourTreeBuildResult Empty = new();

        public IReadOnlyList<AssetPropertyNode> Roots { get; init; } = Array.Empty<AssetPropertyNode>();
        public int TotalNodeCount { get; init; }
        public int PPtrCount { get; init; }
        public bool WasTruncated { get; init; }
        public string ScriptTitle { get; init; } = string.Empty;
        public string SummaryText { get; init; } = string.Empty;
    }

    public static class MonoBehaviourTreeBuilder
    {
        public static MonoBehaviourTreeBuildResult Build(
            OrderedDictionary? fields,
            Func<int, long, AssetReferenceItem?>? pptrResolver = null,
            string? scriptTitle = null,
            MonoBehaviourTreeBuildOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var effectiveOptions = options ?? MonoBehaviourTreeBuildOptions.Default;
            var title = scriptTitle ?? string.Empty;

            if (fields == null || fields.Count == 0)
            {
                return new MonoBehaviourTreeBuildResult
                {
                    Roots = Array.Empty<AssetPropertyNode>(),
                    TotalNodeCount = 0,
                    PPtrCount = 0,
                    WasTruncated = false,
                    ScriptTitle = title,
                    SummaryText = "0 fields"
                };
            }

            var state = new BuildState(effectiveOptions, pptrResolver, cancellationToken);
            var roots = new List<AssetPropertyNode>(Math.Min(fields.Count, effectiveOptions.MaxTotalNodes));

            foreach (DictionaryEntry entry in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.TryConsumeNodeBudget())
                {
                    state.WasTruncated = true;
                    roots.Add(CreateTruncationNoticeNode("... (remaining root fields truncated due to node limit)"));
                    break;
                }

                var key = entry.Key?.ToString() ?? string.Empty;
                roots.Add(BuildNode(key, entry.Value, path: key, depth: 0, state));
            }

            var summary = BuildSummaryText(roots.Count, state.TotalNodes, state.PPtrCount, state.WasTruncated);
            return new MonoBehaviourTreeBuildResult
            {
                Roots = roots,
                TotalNodeCount = state.TotalNodes,
                PPtrCount = state.PPtrCount,
                WasTruncated = state.WasTruncated,
                ScriptTitle = title,
                SummaryText = summary
            };
        }

        public static IReadOnlyList<AssetPropertyNode> FilterTree(
            IReadOnlyList<AssetPropertyNode>? roots,
            string? query,
            CancellationToken cancellationToken = default)
        {
            if (roots == null || roots.Count == 0)
            {
                return Array.Empty<AssetPropertyNode>();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return roots;
            }

            var tokens = SplitSearchTokens(query);
            if (tokens.Length == 0)
            {
                return roots;
            }

            var result = new List<AssetPropertyNode>();
            for (var i = 0; i < roots.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filtered = FilterNodeRecursive(roots[i], tokens, cancellationToken);
                if (filtered != null)
                {
                    result.Add(filtered);
                }
            }

            return result;
        }

        public static void SetExpandedRecursive(IEnumerable<AssetPropertyNode>? nodes, bool isExpanded)
        {
            if (nodes == null)
            {
                return;
            }

            var stack = new Stack<AssetPropertyNode>();
            foreach (var root in nodes)
            {
                if (root != null)
                {
                    stack.Push(root);
                }
            }

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.HasChildren)
                {
                    current.IsExpanded = isExpanded;
                    var children = current.Children;
                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(children[i]);
                    }
                }
            }
        }

        public static bool TryReadPPtr(IDictionary? dict, out int fileId, out long pathId)
        {
            fileId = 0;
            pathId = 0;
            if (dict == null || dict.Count != 2)
            {
                return false;
            }

            if (!dict.Contains("m_FileID") || !dict.Contains("m_PathID"))
            {
                return false;
            }

            if (!TryConvertToInt32(dict["m_FileID"], out fileId) || !TryConvertToInt64(dict["m_PathID"], out pathId))
            {
                return false;
            }

            return true;
        }

        private static AssetPropertyNode BuildNode(
            string name,
            object? value,
            string path,
            int depth,
            BuildState state)
        {
            state.CancellationToken.ThrowIfCancellationRequested();

            if (value == null)
            {
                return new AssetPropertyNode
                {
                    Name = name,
                    ValueText = "null",
                    TypeHint = "null",
                    Path = path
                };
            }

            if (value is IDictionary dict)
            {
                if (TryReadPPtr(dict, out var fileId, out var pathId))
                {
                    return BuildPPtrNode(name, path, fileId, pathId, state);
                }

                if (depth >= state.Options.MaxDepth)
                {
                    state.WasTruncated = true;
                    return new AssetPropertyNode
                    {
                        Name = name,
                        ValueText = $"{{{dict.Count.ToString(CultureInfo.InvariantCulture)} fields - max depth reached}}",
                        TypeHint = "Object",
                        Path = path
                    };
                }

                var children = new List<AssetPropertyNode>(Math.Min(dict.Count, 64));
                var processed = 0;
                foreach (DictionaryEntry entry in dict)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    if (!state.TryConsumeNodeBudget())
                    {
                        state.WasTruncated = true;
                        var remaining = dict.Count - processed;
                        children.Add(CreateTruncationNoticeNode($"... ({remaining.ToString(CultureInfo.InvariantCulture)} more fields truncated)"));
                        break;
                    }

                    var childKey = entry.Key?.ToString() ?? string.Empty;
                    var childPath = string.IsNullOrEmpty(path) ? childKey : $"{path}.{childKey}";
                    children.Add(BuildNode(childKey, entry.Value, childPath, depth + 1, state));
                    processed++;
                }

                var inlineSummary = TryFormatCompactStructSummary(dict)
                    ?? $"{{{dict.Count.ToString(CultureInfo.InvariantCulture)} {(dict.Count == 1 ? "field" : "fields")}}}";

                return new AssetPropertyNode
                {
                    Name = name,
                    ValueText = inlineSummary,
                    TypeHint = "Object",
                    Path = path,
                    Children = children,
                    IsExpanded = depth < state.Options.InitialExpandDepth && children.Count > 0 && children.Count <= 32
                };
            }

            if (value is byte[] bytes)
            {
                return new AssetPropertyNode
                {
                    Name = name,
                    ValueText = FormatByteArrayPreview(bytes),
                    TypeHint = "byte[]",
                    Path = path
                };
            }

            if (value is string str)
            {
                var truncated = str.Length > state.Options.MaxStringPreviewLength
                    ? str.Substring(0, state.Options.MaxStringPreviewLength) + $"... ({str.Length:N0} chars)"
                    : str;
                return new AssetPropertyNode
                {
                    Name = name,
                    ValueText = $"\"{truncated}\"",
                    TypeHint = "string",
                    Path = path
                };
            }

            if (value is IEnumerable enumerable)
            {
                if (depth >= state.Options.MaxDepth)
                {
                    state.WasTruncated = true;
                    return new AssetPropertyNode
                    {
                        Name = name,
                        ValueText = "[... max depth reached]",
                        TypeHint = "Array",
                        Path = path
                    };
                }

                var knownCount = value is ICollection col ? col.Count : (int?)null;
                var children = new List<AssetPropertyNode>(knownCount.HasValue ? Math.Min(knownCount.Value, state.Options.MaxArrayElements) : 16);
                var index = 0;
                var truncatedByCount = false;

                foreach (var elem in enumerable)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();

                    if (index >= state.Options.MaxArrayElements)
                    {
                        truncatedByCount = true;
                        state.WasTruncated = true;
                        var msg = knownCount.HasValue
                            ? $"... ({(knownCount.Value - index).ToString(CultureInfo.InvariantCulture)} more items truncated)"
                            : $"... (array truncated at {state.Options.MaxArrayElements.ToString(CultureInfo.InvariantCulture)} items)";
                        children.Add(CreateTruncationNoticeNode(msg));
                        break;
                    }

                    if (!state.TryConsumeNodeBudget())
                    {
                        truncatedByCount = true;
                        state.WasTruncated = true;
                        children.Add(CreateTruncationNoticeNode("... (remaining array items truncated due to node limit)"));
                        break;
                    }

                    var indexLabel = $"[{index.ToString(CultureInfo.InvariantCulture)}]";
                    var childPath = string.IsNullOrEmpty(path) ? indexLabel : $"{path}{indexLabel}";
                    children.Add(BuildNode(indexLabel, elem, childPath, depth + 1, state));
                    index++;
                }

                var countDisplay = knownCount.HasValue
                    ? knownCount.Value.ToString(CultureInfo.InvariantCulture)
                    : (truncatedByCount ? $"{index.ToString(CultureInfo.InvariantCulture)}+" : index.ToString(CultureInfo.InvariantCulture));

                return new AssetPropertyNode
                {
                    Name = name,
                    ValueText = $"[{countDisplay} {(countDisplay == "1" ? "item" : "items")}]",
                    TypeHint = "Array",
                    Path = path,
                    Children = children,
                    IsExpanded = depth < state.Options.InitialExpandDepth && children.Count > 0 && children.Count <= 16
                };
            }

            var (typeHint, formattedValue) = FormatScalar(value);
            return new AssetPropertyNode
            {
                Name = name,
                ValueText = formattedValue,
                TypeHint = typeHint,
                Path = path
            };
        }

        private static AssetPropertyNode BuildPPtrNode(
            string name,
            string path,
            int fileId,
            long pathId,
            BuildState state)
        {
            if (pathId == 0)
            {
                return new AssetPropertyNode
                {
                    Name = name,
                    ValueText = "None (null)",
                    TypeHint = "PPtr",
                    Path = path
                };
            }

            AssetReferenceItem? resolved = null;
            if (state.PPtrResolver != null)
            {
                try
                {
                    resolved = state.PPtrResolver(fileId, pathId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    resolved = null;
                }
            }

            if (resolved == null)
            {
                resolved = new AssetReferenceItem
                {
                    AssetUniqueId = string.Empty,
                    Name = $"Object ({pathId.ToString(CultureInfo.InvariantCulture)})",
                    AssetType = ClassIDType.UnknownType,
                    TypeName = "Object",
                    Relation = string.IsNullOrWhiteSpace(path) ? "PPtr" : path,
                    PathId = pathId,
                    SerializedFileName = string.Empty,
                    Direction = AssetReferenceDirection.References
                };
            }
            else if (string.IsNullOrWhiteSpace(resolved.Relation) && !string.IsNullOrWhiteSpace(path))
            {
                resolved = new AssetReferenceItem
                {
                    AssetUniqueId = resolved.AssetUniqueId,
                    Name = resolved.Name,
                    AssetType = resolved.AssetType,
                    TypeName = !string.IsNullOrWhiteSpace(resolved.TypeName)
                        ? resolved.TypeName
                        : AssetItem.GetCachedTypeString(resolved.AssetType),
                    Relation = path,
                    PathId = resolved.PathId != 0 ? resolved.PathId : pathId,
                    SerializedFileName = resolved.SerializedFileName,
                    Direction = AssetReferenceDirection.References
                };
            }

            state.PPtrCount++;
            return new AssetPropertyNode
            {
                Name = name,
                ValueText = $"FileID={fileId.ToString(CultureInfo.InvariantCulture)}, PathID={pathId.ToString(CultureInfo.InvariantCulture)}",
                TypeHint = "PPtr",
                Path = path,
                PPtrTarget = resolved
            };
        }

        private static AssetPropertyNode? FilterNodeRecursive(
            AssetPropertyNode node,
            string[] tokens,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nodeMatches = MatchesAllTokens(node, tokens);
            if (!node.HasChildren)
            {
                return nodeMatches ? node : null;
            }

            List<AssetPropertyNode>? matchedChildren = null;
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var filteredChild = FilterNodeRecursive(children[i], tokens, cancellationToken);
                if (filteredChild != null)
                {
                    matchedChildren ??= new List<AssetPropertyNode>();
                    matchedChildren.Add(filteredChild);
                }
            }

            if (matchedChildren != null && matchedChildren.Count > 0)
            {
                return node.CloneWith(matchedChildren, isExpanded: true);
            }

            if (nodeMatches)
            {
                return node.CloneWith(node.Children, isExpanded: node.IsExpanded);
            }

            return null;
        }

        private static bool MatchesAllTokens(AssetPropertyNode node, string[] tokens)
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!node.MatchesQuery(tokens[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] SplitSearchTokens(string query)
        {
            return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static AssetPropertyNode CreateTruncationNoticeNode(string message)
        {
            return new AssetPropertyNode
            {
                Name = "...",
                ValueText = message,
                TypeHint = "truncated",
                Path = string.Empty
            };
        }

        private static string BuildSummaryText(int rootCount, int totalNodes, int pptrCount, bool truncated)
        {
            var sb = new StringBuilder(64);
            sb.Append(rootCount.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(rootCount == 1 ? " root field" : " root fields");
            sb.Append(" • ");
            sb.Append(totalNodes.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(totalNodes == 1 ? " node" : " nodes");
            sb.Append(" • ");
            sb.Append(pptrCount.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(pptrCount == 1 ? " PPtr link" : " PPtr links");
            if (truncated)
            {
                sb.Append(" (capped)");
            }

            return sb.ToString();
        }

        private static string? TryFormatCompactStructSummary(IDictionary dict)
        {
            if (dict.Count is < 2 or > 4)
            {
                return null;
            }

            if (dict.Count == 2 && dict.Contains("x") && dict.Contains("y")
                && IsNumericValue(dict["x"]) && IsNumericValue(dict["y"]))
            {
                return $"({FormatNumber(dict["x"])}, {FormatNumber(dict["y"])})";
            }

            if (dict.Count == 3 && dict.Contains("x") && dict.Contains("y") && dict.Contains("z")
                && IsNumericValue(dict["x"]) && IsNumericValue(dict["y"]) && IsNumericValue(dict["z"]))
            {
                return $"({FormatNumber(dict["x"])}, {FormatNumber(dict["y"])}, {FormatNumber(dict["z"])})";
            }

            if (dict.Count == 4 && dict.Contains("x") && dict.Contains("y") && dict.Contains("z") && dict.Contains("w")
                && IsNumericValue(dict["x"]) && IsNumericValue(dict["y"]) && IsNumericValue(dict["z"]) && IsNumericValue(dict["w"]))
            {
                return $"({FormatNumber(dict["x"])}, {FormatNumber(dict["y"])}, {FormatNumber(dict["z"])}, {FormatNumber(dict["w"])})";
            }

            if (dict.Count == 4 && dict.Contains("r") && dict.Contains("g") && dict.Contains("b") && dict.Contains("a")
                && IsNumericValue(dict["r"]) && IsNumericValue(dict["g"]) && IsNumericValue(dict["b"]) && IsNumericValue(dict["a"]))
            {
                return $"RGBA({FormatNumber(dict["r"])}, {FormatNumber(dict["g"])}, {FormatNumber(dict["b"])}, {FormatNumber(dict["a"])})";
            }

            return null;
        }

        private static bool IsNumericValue(object? val)
        {
            return val is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
        }

        private static string FormatNumber(object? val)
        {
            return val switch
            {
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => val?.ToString() ?? string.Empty
            };
        }

        private static string FormatByteArrayPreview(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return "byte[0]";
            }

            var previewCount = Math.Min(bytes.Length, 16);
            var sb = new StringBuilder(32 + previewCount * 3);
            sb.Append("byte[");
            sb.Append(bytes.Length.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append("] ");
            for (var i = 0; i < previewCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            if (bytes.Length > previewCount)
            {
                sb.Append(" ...");
            }

            return sb.ToString();
        }

        private static (string TypeHint, string Value) FormatScalar(object value)
        {
            return value switch
            {
                bool b => ("bool", b ? "true" : "false"),
                byte b => ("byte", b.ToString(CultureInfo.InvariantCulture)),
                sbyte sb => ("sbyte", sb.ToString(CultureInfo.InvariantCulture)),
                short s => ("short", s.ToString(CultureInfo.InvariantCulture)),
                ushort us => ("ushort", us.ToString(CultureInfo.InvariantCulture)),
                int i => ("int", i.ToString(CultureInfo.InvariantCulture)),
                uint ui => ("uint", ui.ToString(CultureInfo.InvariantCulture)),
                long l => ("long", l.ToString(CultureInfo.InvariantCulture)),
                ulong ul => ("ulong", ul.ToString(CultureInfo.InvariantCulture)),
                float f => ("float", f.ToString("G7", CultureInfo.InvariantCulture)),
                double d => ("double", d.ToString("G15", CultureInfo.InvariantCulture)),
                decimal m => ("decimal", m.ToString(CultureInfo.InvariantCulture)),
                char c => ("char", $"'{c}'"),
                Enum e => (e.GetType().Name, e.ToString()),
                IFormattable formattable => (value.GetType().Name, formattable.ToString(null, CultureInfo.InvariantCulture)),
                _ => (value.GetType().Name, value.ToString() ?? string.Empty)
            };
        }

        private static bool TryConvertToInt32(object? value, out int result)
        {
            switch (value)
            {
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = unchecked((int)l);
                    return true;
                case uint ui:
                    result = unchecked((int)ui);
                    return true;
                case short s:
                    result = s;
                    return true;
                case ushort us:
                    result = us;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case sbyte sb:
                    result = sb;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        private static bool TryConvertToInt64(object? value, out long result)
        {
            switch (value)
            {
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case ulong ul:
                    result = unchecked((long)ul);
                    return true;
                case uint ui:
                    result = ui;
                    return true;
                case short s:
                    result = s;
                    return true;
                case ushort us:
                    result = us;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case sbyte sb:
                    result = sb;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        private sealed class BuildState
        {
            public BuildState(
                MonoBehaviourTreeBuildOptions options,
                Func<int, long, AssetReferenceItem?>? pptrResolver,
                CancellationToken cancellationToken)
            {
                Options = options;
                PPtrResolver = pptrResolver;
                CancellationToken = cancellationToken;
            }

            public MonoBehaviourTreeBuildOptions Options { get; }
            public Func<int, long, AssetReferenceItem?>? PPtrResolver { get; }
            public CancellationToken CancellationToken { get; }
            public int TotalNodes { get; private set; }
            public int PPtrCount { get; set; }
            public bool WasTruncated { get; set; }

            public bool TryConsumeNodeBudget()
            {
                if (TotalNodes >= Options.MaxTotalNodes)
                {
                    return false;
                }

                TotalNodes++;
                return true;
            }
        }
    }
}
