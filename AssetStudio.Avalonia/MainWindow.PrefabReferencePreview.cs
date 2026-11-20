using AssetStudio;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Threading;
using Object = AssetStudio.Object;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private static Object? ResolvePPtr(object? pptrObj, SerializedFile file)
    {
        if (pptrObj is OrderedDictionary dict)
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

    private static void FindAllPPtrs(object? obj, List<OrderedDictionary> pptrs, CancellationToken cancellationToken)
    {
        if (obj == null) return;
        cancellationToken.ThrowIfCancellationRequested();

        if (obj is OrderedDictionary dict)
        {
            if (dict.Contains("m_FileID") && dict.Contains("m_PathID"))
            {
                pptrs.Add(dict);
            }
            else
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    FindAllPPtrs(entry.Value, pptrs, cancellationToken);
                }
            }
        }
        else if (obj is System.Collections.IEnumerable list && obj is not string)
        {
            foreach (var item in list)
            {
                FindAllPPtrs(item, pptrs, cancellationToken);
            }
        }
    }

    internal static void TraverseGameObjectHierarchy(
        GameObject? root,
        List<GameObject> gameObjects,
        HashSet<GameObject> visitedGameObjects,
        List<Component> components,
        HashSet<Object> hierarchyObjects,
        CancellationToken cancellationToken = default)
    {
        if (root == null)
        {
            return;
        }

        var stack = new Stack<GameObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var go = stack.Pop();
            if (go == null || !visitedGameObjects.Add(go))
            {
                continue;
            }

            gameObjects.Add(go);
            hierarchyObjects.Add(go);

            if (go.m_Components == null)
            {
                continue;
            }

            var childGameObjects = new List<GameObject>();
            foreach (var pptrComp in go.m_Components)
            {
                if (pptrComp.TryGet(out var comp) && comp != null)
                {
                    components.Add(comp);
                    hierarchyObjects.Add(comp);

                    if (comp is Transform t && t.m_Children != null)
                    {
                        foreach (var childPtr in t.m_Children)
                        {
                            if (childPtr.TryGet(out var childTransform)
                                && childTransform.m_GameObject.TryGet(out var childGo)
                                && childGo != null)
                            {
                                childGameObjects.Add(childGo);
                            }
                        }
                    }
                }
            }

            // Push children in reverse so iterative traversal visits in the exact same preorder as recursion.
            for (int i = childGameObjects.Count - 1; i >= 0; i--)
            {
                stack.Push(childGameObjects[i]);
            }
        }
    }

    internal static (List<Object> UniqueResolvedObjects, int TotalResolvedCount, List<string> UniqueUnresolvedPPtrs, int TotalUnresolvedCount)
        DeduplicatePrefabReferences(
            IEnumerable<Object?> resolvedCandidates,
            IEnumerable<string> unresolvedCandidates,
            IReadOnlySet<Object> excludedObjects,
            CancellationToken cancellationToken = default)
    {
        var uniqueResolved = new List<Object>();
        var seenResolved = new HashSet<Object>();
        int totalResolved = 0;

        foreach (var resolved in resolvedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved == null || excludedObjects.Contains(resolved))
            {
                continue;
            }

            totalResolved++;
            if (seenResolved.Add(resolved))
            {
                uniqueResolved.Add(resolved);
            }
        }

        var uniqueUnresolved = new List<string>();
        var seenUnresolved = new HashSet<string>(StringComparer.Ordinal);
        int totalUnresolved = 0;

        foreach (var unresolved in unresolvedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(unresolved))
            {
                continue;
            }

            totalUnresolved++;
            if (seenUnresolved.Add(unresolved))
            {
                uniqueUnresolved.Add(unresolved);
            }
        }

        return (uniqueResolved, totalResolved, uniqueUnresolved, totalUnresolved);
    }

    private string FormatPrefab(Object prefab)
    {
        return FormatPrefabCore(prefab, CancellationToken.None);
    }

    internal static string FormatPrefabCore(Object prefab, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        if (rootGameObject is GameObject rootGoHeader)
        {
            sb.AppendLine($"Root GameObject: {rootGoHeader.m_Name} (PathID: {rootGameObject.m_PathID})");
        }
        else if (rootGameObject != null)
        {
            sb.AppendLine($"Root GameObject: {rootGameObject.type} (PathID: {rootGameObject.m_PathID})");
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
        var visitedGameObjects = new HashSet<GameObject>();
        var components = new List<Component>();
        var hierarchyObjects = new HashSet<Object> { prefab };

        if (rootGameObject is GameObject rootGo)
        {
            TraverseGameObjectHierarchy(rootGo, gameObjects, visitedGameObjects, components, hierarchyObjects, cancellationToken);
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

        var allPPtrDicts = new List<OrderedDictionary>();
        FindAllPPtrs(dict, allPPtrDicts, cancellationToken);

        var uniqueResolvedObjects = new List<Object>();
        var seenResolvedObjects = new HashSet<Object>();
        int totalResolvedCount = 0;

        var uniqueUnresolvedPPtrs = new List<string>();
        var seenUnresolvedPPtrs = new HashSet<string>(StringComparer.Ordinal);
        int totalUnresolvedCount = 0;

        foreach (var pptrDict in allPPtrDicts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = ResolvePPtr(pptrDict, prefab.assetsFile);
            if (resolved != null)
            {
                if (!hierarchyObjects.Contains(resolved))
                {
                    totalResolvedCount++;
                    if (seenResolvedObjects.Add(resolved))
                    {
                        uniqueResolvedObjects.Add(resolved);
                    }
                }
            }
            else
            {
                var fileID = pptrDict["m_FileID"];
                var pathID = pptrDict["m_PathID"];
                if (pathID != null && Convert.ToInt64(pathID) != 0)
                {
                    var key = $"FileID: {fileID}, PathID: {pathID}";
                    totalUnresolvedCount++;
                    if (seenUnresolvedPPtrs.Add(key))
                    {
                        uniqueUnresolvedPPtrs.Add(key);
                    }
                }
            }
        }

        if (totalResolvedCount > 0)
        {
            sb.AppendLine($"Other Resolved Referenced Assets ({totalResolvedCount}):");
            foreach (var resObj in uniqueResolvedObjects)
            {
                sb.AppendLine($"  - Type: {resObj.type} (PathID: {resObj.m_PathID})");
            }
            sb.AppendLine();
        }

        if (totalUnresolvedCount > 0)
        {
            sb.AppendLine($"Unresolved PPtr References ({totalUnresolvedCount}):");
            foreach (var unres in uniqueUnresolvedPPtrs)
            {
                sb.AppendLine($"  - {unres}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
