using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio.Avalonia.Services.Scripting;

/// <summary>
/// Reconstructs idiomatic C# class declarations (fields, Unity built-in value types,
/// PPtr asset references, lists, arrays, dictionaries, and nested serializable types)
/// directly from Unity's serialized <see cref="TypeTree"/>.
/// </summary>
public static class TypeTreeScriptReconstructor
{
    private static readonly HashSet<string> StandardUnityHeaderFields = new(StringComparer.Ordinal)
    {
        "m_GameObject",
        "m_Enabled",
        "m_Script",
        "m_Name",
        "m_EditorHideFlags",
        "m_EditorClassIdentifier"
    };

    private static readonly HashSet<string> KnownLeafUnityTypes = new(StringComparer.Ordinal)
    {
        "Vector2f",
        "Vector3f",
        "Vector4f",
        "Quaternionf",
        "ColorRGBA",
        "Matrix4x4f",
        "Rectf",
        "RectInt",
        "AABB",
        "BoundsInt",
        "Vector2Int",
        "Vector3Int",
        "LayerMask",
        "AnimationCurve",
        "Gradient",
        "GUID",
        "Hash128",
        "ManagedReferencesRegistry",
        "TypelessData"
    };

    private sealed class ReconstructedTypeDef
    {
        public required string Name { get; init; }
        public List<(string TypeName, string FieldName)> Fields { get; } = new();
    }

    public static ScriptDecompilationResult? TryReconstruct(
        ScriptLookupRequest request,
        TypeTree? typeTree,
        string? sourceFileName = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nodes = typeTree?.m_Nodes;
        if (nodes == null || nodes.Count < 2)
        {
            return null;
        }

        var nextSibling = TypeTreeHelper.GetNextSiblingTable(nodes);
        var rootLevel = nodes[0].m_Level;
        var directChildren = CollectDirectChildren(nodes, nextSibling, startIndex: 1, endIndex: nodes.Count, expectedLevel: rootLevel + 1);
        if (directChildren.Count == 0)
        {
            return null;
        }

        var hasGameObject = false;
        var hasScript = false;
        var hasName = false;
        foreach (var childIdx in directChildren)
        {
            var fieldName = nodes[childIdx].m_Name;
            if (string.Equals(fieldName, "m_GameObject", StringComparison.Ordinal)) hasGameObject = true;
            else if (string.Equals(fieldName, "m_Script", StringComparison.Ordinal)) hasScript = true;
            else if (string.Equals(fieldName, "m_Name", StringComparison.Ordinal)) hasName = true;
        }

        var baseClass = hasGameObject && hasScript
            ? "MonoBehaviour"
            : (!hasGameObject && hasScript && hasName ? "ScriptableObject" : string.Empty);

        var nestedOrder = new List<string>();
        var nestedDefs = new Dictionary<string, ReconstructedTypeDef>(StringComparer.Ordinal);
        var fields = new List<(string TypeName, string FieldName)>();

        foreach (var childIdx in directChildren)
        {
            var node = nodes[childIdx];
            if (StandardUnityHeaderFields.Contains(node.m_Name))
            {
                continue;
            }

            if (string.Equals(node.m_Type, "ManagedReferencesRegistry", StringComparison.Ordinal))
            {
                continue;
            }

            var resolvedType = ResolveNodeCSharpType(nodes, nextSibling, childIdx, nestedOrder, nestedDefs, depth: 0);
            var safeFieldName = SanitizeIdentifier(node.m_Name, "field");
            fields.Add((resolvedType, safeFieldName));
        }

        if (fields.Count == 0 && !hasScript)
        {
            return null;
        }

        var className = !string.IsNullOrWhiteSpace(request.ClassName)
            ? SanitizeTypeName(request.ClassName)
            : SanitizeTypeName(nodes[0].m_Type);
        if (string.IsNullOrWhiteSpace(className))
        {
            className = "SerializedBehaviour";
        }

        var ns = request.Namespace?.Trim() ?? string.Empty;
        var fullTitle = !string.IsNullOrWhiteSpace(ns) ? $"{ns}.{className}" : className;
        var assemblyName = request.AssemblyName?.Trim() ?? string.Empty;

        var sb = new StringBuilder(1024);
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            sb.AppendLine($"// Assembly: {assemblyName}");
        }
        sb.AppendLine($"// Type: {fullTitle}");
        sb.AppendLine("// Source: Unity TypeTree");
        if (!string.IsNullOrWhiteSpace(sourceFileName))
        {
            sb.AppendLine($"// File: {sourceFileName}");
        }
        sb.AppendLine();

        var indent = string.Empty;
        if (!string.IsNullOrWhiteSpace(ns))
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            indent = "    ";
        }

        var inheritance = string.IsNullOrEmpty(baseClass) ? string.Empty : $" : {baseClass}";
        sb.AppendLine($"{indent}public class {className}{inheritance}");
        sb.AppendLine($"{indent}{{");

        var memberIndent = indent + "    ";
        if (fields.Count > 0)
        {
            foreach (var (typeName, fieldName) in fields)
            {
                sb.AppendLine($"{memberIndent}public {typeName} {fieldName};");
            }
        }
        else
        {
            sb.AppendLine($"{memberIndent}// No custom serialized fields.");
        }

        foreach (var nestedName in nestedOrder)
        {
            if (string.Equals(nestedName, className, StringComparison.Ordinal)
                || !nestedDefs.TryGetValue(nestedName, out var def))
            {
                continue;
            }

            sb.AppendLine();
            sb.AppendLine($"{memberIndent}[Serializable]");
            sb.AppendLine($"{memberIndent}public class {def.Name}");
            sb.AppendLine($"{memberIndent}{{");
            var innerIndent = memberIndent + "    ";
            foreach (var (nestedFieldType, nestedFieldName) in def.Fields)
            {
                sb.AppendLine($"{innerIndent}public {nestedFieldType} {nestedFieldName};");
            }
            sb.AppendLine($"{memberIndent}}}");
        }

        sb.AppendLine($"{indent}}}");
        if (!string.IsNullOrWhiteSpace(ns))
        {
            sb.AppendLine("}");
        }

        var summaryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            summaryParts.Add(assemblyName);
        }
        summaryParts.Add($"{fields.Count} serialized field{(fields.Count == 1 ? string.Empty : "s")}");
        if (nestedOrder.Count > 0)
        {
            summaryParts.Add($"{nestedOrder.Count} nested type{(nestedOrder.Count == 1 ? string.Empty : "s")}");
        }
        summaryParts.Add("Unity TypeTree");

        return new ScriptDecompilationResult
        {
            Success = true,
            SourceKind = ScriptSourceKind.UnityTypeTree,
            BackendBadge = "Unity TypeTree",
            Title = fullTitle,
            AssemblyName = assemblyName,
            Namespace = ns,
            ClassName = className,
            SourceFilePath = sourceFileName ?? string.Empty,
            SourceCode = sb.ToString().TrimEnd(),
            SummaryText = string.Join(" • ", summaryParts),
            DiagnosticMessage = string.Empty
        };
    }

    /// <summary>
    /// Extracts top-level serialized field name -> C# type mappings from a <see cref="TypeTree"/>,
    /// allowing metadata-based views to display concrete field types when available.
    /// </summary>
    public static Dictionary<string, string> ExtractTopLevelFieldTypes(TypeTree? typeTree)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = typeTree?.m_Nodes;
        if (nodes == null || nodes.Count < 2)
        {
            return map;
        }

        var nextSibling = TypeTreeHelper.GetNextSiblingTable(nodes);
        var rootLevel = nodes[0].m_Level;
        var directChildren = CollectDirectChildren(nodes, nextSibling, 1, nodes.Count, rootLevel + 1);
        var nestedOrder = new List<string>();
        var nestedDefs = new Dictionary<string, ReconstructedTypeDef>(StringComparer.Ordinal);

        foreach (var childIdx in directChildren)
        {
            var node = nodes[childIdx];
            if (string.IsNullOrWhiteSpace(node.m_Name)
                || StandardUnityHeaderFields.Contains(node.m_Name)
                || string.Equals(node.m_Type, "ManagedReferencesRegistry", StringComparison.Ordinal))
            {
                continue;
            }

            var csType = ResolveNodeCSharpType(nodes, nextSibling, childIdx, nestedOrder, nestedDefs, depth: 0);
            if (!string.IsNullOrWhiteSpace(csType))
            {
                map.TryAdd(node.m_Name, csType);
            }
        }

        return map;
    }

    private static string ResolveNodeCSharpType(
        List<TypeTreeNode> nodes,
        int[] nextSibling,
        int nodeIdx,
        List<string> nestedOrder,
        Dictionary<string, ReconstructedTypeDef> nestedDefs,
        int depth)
    {
        if (depth > 24 || nodeIdx < 0 || nodeIdx >= nodes.Count)
        {
            return "object";
        }

        var node = nodes[nodeIdx];
        var rawType = node.m_Type ?? string.Empty;
        var subtreeEnd = nextSibling[nodeIdx];

        if (TryMapPrimitiveOrUnityLeaf(rawType, node.m_ByteSize, out var leafType))
        {
            return leafType;
        }

        if (TryMapPPtrType(rawType, out var pptrType))
        {
            return pptrType;
        }

        // Map -> Dictionary<TKey, TValue>
        if (string.Equals(rawType, "map", StringComparison.Ordinal))
        {
            var arrayIdx = FindDirectChildByTypeOrName(nodes, nextSibling, nodeIdx + 1, subtreeEnd, node.m_Level + 1, "Array", "Array");
            if (arrayIdx >= 0)
            {
                var pairIdx = FindDirectChildByName(nodes, nextSibling, arrayIdx + 1, nextSibling[arrayIdx], nodes[arrayIdx].m_Level + 1, "data");
                if (pairIdx >= 0)
                {
                    var pairChildren = CollectDirectChildren(nodes, nextSibling, pairIdx + 1, nextSibling[pairIdx], nodes[pairIdx].m_Level + 1);
                    if (pairChildren.Count >= 2)
                    {
                        var keyType = ResolveNodeCSharpType(nodes, nextSibling, pairChildren[0], nestedOrder, nestedDefs, depth + 1);
                        var valType = ResolveNodeCSharpType(nodes, nextSibling, pairChildren[1], nestedOrder, nestedDefs, depth + 1);
                        return $"Dictionary<{keyType}, {valType}>";
                    }
                }
            }
            return "Dictionary<object, object>";
        }

        // Vector / Set / StaticVector -> List<T>
        if (string.Equals(rawType, "vector", StringComparison.Ordinal)
            || string.Equals(rawType, "set", StringComparison.Ordinal)
            || string.Equals(rawType, "staticvector", StringComparison.Ordinal))
        {
            var arrayIdx = FindDirectChildByTypeOrName(nodes, nextSibling, nodeIdx + 1, subtreeEnd, node.m_Level + 1, "Array", "Array");
            if (arrayIdx >= 0)
            {
                var elemIdx = FindDirectChildByName(nodes, nextSibling, arrayIdx + 1, nextSibling[arrayIdx], nodes[arrayIdx].m_Level + 1, "data");
                if (elemIdx >= 0)
                {
                    var elemType = ResolveNodeCSharpType(nodes, nextSibling, elemIdx, nestedOrder, nestedDefs, depth + 1);
                    return $"List<{elemType}>";
                }
            }
            return "List<object>";
        }

        // Direct Array node -> T[]
        if (string.Equals(rawType, "Array", StringComparison.Ordinal) || (node.m_TypeFlags & 1) != 0)
        {
            var elemIdx = FindDirectChildByName(nodes, nextSibling, nodeIdx + 1, subtreeEnd, node.m_Level + 1, "data");
            if (elemIdx >= 0)
            {
                var elemType = ResolveNodeCSharpType(nodes, nextSibling, elemIdx, nestedOrder, nestedDefs, depth + 1);
                return $"{elemType}[]";
            }
            return "object[]";
        }

        // Pair -> KeyValuePair<TFirst, TSecond>
        if (string.Equals(rawType, "pair", StringComparison.Ordinal))
        {
            var pairChildren = CollectDirectChildren(nodes, nextSibling, nodeIdx + 1, subtreeEnd, node.m_Level + 1);
            if (pairChildren.Count >= 2)
            {
                var firstType = ResolveNodeCSharpType(nodes, nextSibling, pairChildren[0], nestedOrder, nestedDefs, depth + 1);
                var secondType = ResolveNodeCSharpType(nodes, nextSibling, pairChildren[1], nestedOrder, nestedDefs, depth + 1);
                return $"KeyValuePair<{firstType}, {secondType}>";
            }
        }

        // Wrapper around a single Array child (e.g. custom array wrapper node)
        var children = CollectDirectChildren(nodes, nextSibling, nodeIdx + 1, subtreeEnd, node.m_Level + 1);
        if (children.Count == 1 && (string.Equals(nodes[children[0]].m_Type, "Array", StringComparison.Ordinal) || (nodes[children[0]].m_TypeFlags & 1) != 0))
        {
            var arrIdx = children[0];
            var elemIdx = FindDirectChildByName(nodes, nextSibling, arrIdx + 1, nextSibling[arrIdx], nodes[arrIdx].m_Level + 1, "data");
            if (elemIdx >= 0)
            {
                var elemType = ResolveNodeCSharpType(nodes, nextSibling, elemIdx, nestedOrder, nestedDefs, depth + 1);
                return $"List<{elemType}>";
            }
        }

        var cleanTypeName = SanitizeTypeName(rawType);
        if (string.IsNullOrWhiteSpace(cleanTypeName))
        {
            cleanTypeName = "SerializableData";
        }

        // Register composite nested serializable type if it has fields
        if (children.Count > 0 && !KnownLeafUnityTypes.Contains(rawType) && !nestedDefs.ContainsKey(cleanTypeName))
        {
            var def = new ReconstructedTypeDef { Name = cleanTypeName };
            nestedDefs[cleanTypeName] = def;
            nestedOrder.Add(cleanTypeName);

            foreach (var childIdx in children)
            {
                var childNode = nodes[childIdx];
                var childType = ResolveNodeCSharpType(nodes, nextSibling, childIdx, nestedOrder, nestedDefs, depth + 1);
                var childName = SanitizeIdentifier(childNode.m_Name, "value");
                def.Fields.Add((childType, childName));
            }
        }

        return cleanTypeName;
    }

    private static bool TryMapPrimitiveOrUnityLeaf(string rawType, int byteSize, out string csType)
    {
        csType = rawType switch
        {
            "SInt8" => "sbyte",
            "UInt8" => "byte",
            "char" => byteSize == 2 ? "char" : "byte",
            "SInt16" or "short" => "short",
            "UInt16" or "unsigned short" => "ushort",
            "SInt32" or "int" => "int",
            "UInt32" or "unsigned int" or "Type*" => "uint",
            "SInt64" or "long long" => "long",
            "UInt64" or "unsigned long long" or "FileSize" => "ulong",
            "float" => "float",
            "double" => "double",
            "bool" => "bool",
            "string" => "string",
            "TypelessData" => "byte[]",
            "Vector2f" => "Vector2",
            "Vector3f" => "Vector3",
            "Vector4f" => "Vector4",
            "Quaternionf" => "Quaternion",
            "ColorRGBA" => "Color",
            "Matrix4x4f" => "Matrix4x4",
            "Rectf" => "Rect",
            "RectInt" => "RectInt",
            "AABB" => "Bounds",
            "BoundsInt" => "BoundsInt",
            "Vector2Int" => "Vector2Int",
            "Vector3Int" => "Vector3Int",
            "LayerMask" => "LayerMask",
            "AnimationCurve" => "AnimationCurve",
            "Gradient" => "Gradient",
            "GUID" => "string",
            "Hash128" => "Hash128",
            "ManagedReferencesRegistry" => "object",
            _ => string.Empty
        };

        return csType.Length > 0;
    }

    private static bool TryMapPPtrType(string rawType, out string csType)
    {
        csType = string.Empty;
        if (!rawType.StartsWith("PPtr<", StringComparison.Ordinal) || !rawType.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var inner = rawType.Substring(5, rawType.Length - 6).Trim();
        if (inner.StartsWith('$'))
        {
            inner = inner.Substring(1);
        }

        if (string.IsNullOrWhiteSpace(inner))
        {
            csType = "UnityEngine.Object";
            return true;
        }

        csType = string.Equals(inner, "Object", StringComparison.Ordinal)
            ? "UnityEngine.Object"
            : SanitizeTypeName(inner);
        return true;
    }

    private static List<int> CollectDirectChildren(
        List<TypeTreeNode> nodes,
        int[] nextSibling,
        int startIndex,
        int endIndex,
        int expectedLevel)
    {
        var results = new List<int>();
        var idx = startIndex;
        while (idx < endIndex && idx < nodes.Count)
        {
            if (nodes[idx].m_Level == expectedLevel)
            {
                results.Add(idx);
                idx = nextSibling[idx];
            }
            else if (nodes[idx].m_Level < expectedLevel)
            {
                break;
            }
            else
            {
                idx++;
            }
        }
        return results;
    }

    private static int FindDirectChildByTypeOrName(
        List<TypeTreeNode> nodes,
        int[] nextSibling,
        int startIndex,
        int endIndex,
        int expectedLevel,
        string expectedType,
        string expectedName)
    {
        var idx = startIndex;
        while (idx < endIndex && idx < nodes.Count)
        {
            var n = nodes[idx];
            if (n.m_Level == expectedLevel)
            {
                if (string.Equals(n.m_Type, expectedType, StringComparison.Ordinal)
                    || string.Equals(n.m_Name, expectedName, StringComparison.Ordinal))
                {
                    return idx;
                }
                idx = nextSibling[idx];
            }
            else if (n.m_Level < expectedLevel)
            {
                break;
            }
            else
            {
                idx++;
            }
        }
        return -1;
    }

    private static int FindDirectChildByName(
        List<TypeTreeNode> nodes,
        int[] nextSibling,
        int startIndex,
        int endIndex,
        int expectedLevel,
        string expectedName)
    {
        var idx = startIndex;
        while (idx < endIndex && idx < nodes.Count)
        {
            var n = nodes[idx];
            if (n.m_Level == expectedLevel)
            {
                if (string.Equals(n.m_Name, expectedName, StringComparison.Ordinal))
                {
                    return idx;
                }
                idx = nextSibling[idx];
            }
            else if (n.m_Level < expectedLevel)
            {
                break;
            }
            else
            {
                idx++;
            }
        }
        return -1;
    }

    private static string SanitizeTypeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        var tick = trimmed.IndexOf('`');
        if (tick > 0)
        {
            trimmed = trimmed.Substring(0, tick);
        }

        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
            {
                sb.Append(ch);
            }
            else if (ch == '+' || ch == '/')
            {
                sb.Append('.');
            }
        }

        var cleaned = sb.ToString().Trim('.');
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        if (char.IsDigit(cleaned[0]))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    private static string SanitizeIdentifier(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        var cleaned = sb.ToString();
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        if (char.IsDigit(cleaned[0]))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }
}
