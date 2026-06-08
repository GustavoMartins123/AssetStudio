using AssetStudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Object = AssetStudio.Object;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
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

}