using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class GameObjectTransformSnapshot
    {
        public static readonly GameObjectTransformSnapshot None = new();

        public bool HasTransform { get; init; }
        public bool IsRectTransform { get; init; }
        public string PositionText { get; init; } = "(0, 0, 0)";
        public string RotationEulerText { get; init; } = "(0°, 0°, 0°)";
        public string RotationQuaternionText { get; init; } = "(0, 0, 0, 1)";
        public string ScaleText { get; init; } = "(1, 1, 1)";
        public string RectSummaryText { get; init; } = string.Empty;
        public int ChildCount { get; init; }
        public string ParentName { get; init; } = "None (Root)";
        public AssetReferenceItem? ParentTarget { get; init; }

        public bool HasParentTarget => ParentTarget?.IsNavigable == true;
        public bool HasRectSummary => !string.IsNullOrWhiteSpace(RectSummaryText);
        public string HeaderTitle => IsRectTransform ? "RectTransform" : "Transform";
    }

    public sealed class GameObjectComponentInfo
    {
        public int Index { get; init; }
        public ClassIDType ComponentType { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public string DisplayTitle { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public bool? IsEnabled { get; init; }
        public long PathId { get; init; }
        public AssetReferenceItem? ComponentTarget { get; init; }
        public IReadOnlyList<AssetReferenceItem> LinkedAssets { get; init; } = Array.Empty<AssetReferenceItem>();

        public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
        public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
        public bool HasEnabledBadge => IsEnabled.HasValue;
        public string EnabledBadge => IsEnabled.HasValue ? (IsEnabled.Value ? "Enabled" : "Disabled") : string.Empty;
        public bool HasComponentTarget => ComponentTarget?.IsNavigable == true;
        public bool HasLinkedAssets => LinkedAssets.Count > 0;
        public string PathIdText => PathId != 0 ? PathId.ToString(CultureInfo.InvariantCulture) : "-";

        public bool MatchesFilter(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var needle = query.Trim();
            if ((!string.IsNullOrEmpty(TypeName) && TypeName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(DisplayTitle) && DisplayTitle.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(Subtitle) && Subtitle.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(Summary) && Summary.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || PathIdText.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ComponentTarget != null && ComponentTarget.MatchesFilter(needle))
            {
                return true;
            }

            for (var i = 0; i < LinkedAssets.Count; i++)
            {
                if (LinkedAssets[i].MatchesFilter(needle))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class GameObjectInspectorSnapshot
    {
        public static readonly GameObjectInspectorSnapshot Empty = new();

        public string Name { get; init; } = string.Empty;
        public bool HasGameObjectData { get; init; }
        public bool IsActive { get; init; }
        public int Layer { get; init; }
        public ushort Tag { get; init; }
        public string HierarchyPath { get; init; } = string.Empty;
        public string SourceFileName { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string AssetUniqueId { get; init; } = string.Empty;
        public AssetReferenceItem? GameObjectTarget { get; init; }
        public int ChildCount { get; init; }
        public string HeaderMetadataLine { get; init; } = string.Empty;
        public string LazyNoticeText { get; init; } = string.Empty;
        public GameObjectTransformSnapshot Transform { get; init; } = GameObjectTransformSnapshot.None;
        public IReadOnlyList<GameObjectComponentInfo> Components { get; init; } = Array.Empty<GameObjectComponentInfo>();

        public string ActiveStateText => HasGameObjectData ? (IsActive ? "Active" : "Inactive") : "Scene Node";
        public string PathIdText => PathId != 0 ? PathId.ToString(CultureInfo.InvariantCulture) : "-";
        public bool HasGameObjectTarget => GameObjectTarget?.IsNavigable == true;
        public bool HasHierarchyPath => !string.IsNullOrWhiteSpace(HierarchyPath);
        public bool HasLazyNotice => !string.IsNullOrWhiteSpace(LazyNoticeText);
        public string ComponentsHeader => $"Components ({Components.Count.ToString(CultureInfo.InvariantCulture)})";
    }

    public static class GameObjectInspectorService
    {
        private const int MaxLinkedMaterialsPerRenderer = 32;
        private const int MaxLinkedClipsPerAnimation = 32;

        public static GameObjectInspectorSnapshot BuildFromNode(
            GameObjectNode? node,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver = null,
            ProjectIndex? projectIndex = null,
            CancellationToken cancellationToken = default)
        {
            if (node == null)
            {
                return GameObjectInspectorSnapshot.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var hierarchyPath = BuildHierarchyPath(node);
            if (node.GameObject == null)
            {
                var childWord = node.ChildCount == 1 ? "child" : "children";
                return new GameObjectInspectorSnapshot
                {
                    Name = !string.IsNullOrWhiteSpace(node.Name) ? node.Name : "(Unnamed Node)",
                    HasGameObjectData = false,
                    IsActive = true,
                    HierarchyPath = hierarchyPath,
                    ChildCount = node.ChildCount,
                    HeaderMetadataLine = $"{node.ChildCount.ToString(CultureInfo.InvariantCulture)} {childWord}",
                    LazyNoticeText = "Scene container node (no GameObject instance attached or not materialized in Lazy Mode).",
                    Transform = GameObjectTransformSnapshot.None,
                    Components = Array.Empty<GameObjectComponentInfo>()
                };
            }

            return BuildFromGameObject(
                node.GameObject,
                hierarchyPath: hierarchyPath,
                nodeChildCount: node.ChildCount,
                pptrResolver: pptrResolver,
                projectIndex: projectIndex,
                cancellationToken: cancellationToken);
        }

        public static GameObjectInspectorSnapshot BuildFromGameObject(
            GameObject? gameObject,
            string? hierarchyPath = null,
            int? nodeChildCount = null,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver = null,
            ProjectIndex? projectIndex = null,
            CancellationToken cancellationToken = default)
        {
            if (gameObject == null)
            {
                return GameObjectInspectorSnapshot.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var sourceFile = gameObject.assetsFile;
            var sourceFileName = sourceFile?.fileName ?? string.Empty;
            var goName = !string.IsNullOrWhiteSpace(gameObject.m_Name)
                ? gameObject.m_Name
                : $"GameObject ({gameObject.m_PathID.ToString(CultureInfo.InvariantCulture)})";
            var uniqueId = sourceFile != null
                ? AssetHandle.BuildUniqueID(sourceFile, gameObject.m_PathID)
                : string.Empty;

            var goTarget = new AssetReferenceItem
            {
                AssetUniqueId = uniqueId,
                Name = goName,
                AssetType = ClassIDType.GameObject,
                TypeName = "GameObject",
                Relation = "GameObject",
                PathId = gameObject.m_PathID,
                SerializedFileName = sourceFileName,
                Direction = AssetReferenceDirection.References
            };

            Transform? resolvedTransform = gameObject.m_Transform;
            var components = new List<GameObjectComponentInfo>();
            var unresolvedComponentCount = 0;

            if (gameObject.m_Components != null)
            {
                for (var i = 0; i < gameObject.m_Components.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var compPtr = gameObject.m_Components[i];
                    if (compPtr == null || compPtr.IsNull)
                    {
                        continue;
                    }

                    var rawComp = TryGetLoadedObjectRaw(sourceFile, compPtr.m_FileID, compPtr.m_PathID);
                    if (rawComp is Transform tr && resolvedTransform == null)
                    {
                        resolvedTransform = tr;
                    }

                    if (rawComp != null)
                    {
                        components.Add(BuildComponentInfo(i, rawComp, sourceFile, pptrResolver, projectIndex));
                    }
                    else
                    {
                        unresolvedComponentCount++;
                        components.Add(BuildUnresolvedComponentInfo(i, sourceFile, compPtr.m_FileID, compPtr.m_PathID, pptrResolver, projectIndex));
                    }
                }
            }
            else if (resolvedTransform != null)
            {
                components.Add(BuildComponentInfo(0, resolvedTransform, sourceFile, pptrResolver, projectIndex));
            }

            var effectiveChildCount = resolvedTransform?.m_Children?.Length ?? nodeChildCount ?? 0;
            var transformSnapshot = BuildTransformSnapshot(resolvedTransform, effectiveChildCount, sourceFile, pptrResolver, projectIndex);

            var metadataParts = new List<string>(5)
            {
                gameObject.m_IsActive ? "Active" : "Inactive",
                $"Layer {gameObject.m_Layer.ToString(CultureInfo.InvariantCulture)}",
                $"Tag {gameObject.m_Tag.ToString(CultureInfo.InvariantCulture)}",
                $"PathID: {gameObject.m_PathID.ToString(CultureInfo.InvariantCulture)}"
            };
            if (!string.IsNullOrWhiteSpace(sourceFileName))
            {
                metadataParts.Add(sourceFileName);
            }

            var lazyNotice = unresolvedComponentCount > 0
                ? $"{unresolvedComponentCount.ToString(CultureInfo.InvariantCulture)} component(s) not materialized in memory (Lazy Mode) — click any link to inspect on demand."
                : string.Empty;

            return new GameObjectInspectorSnapshot
            {
                Name = goName,
                HasGameObjectData = true,
                IsActive = gameObject.m_IsActive,
                Layer = gameObject.m_Layer,
                Tag = gameObject.m_Tag,
                HierarchyPath = !string.IsNullOrWhiteSpace(hierarchyPath) ? hierarchyPath! : goName,
                SourceFileName = sourceFileName,
                PathId = gameObject.m_PathID,
                AssetUniqueId = uniqueId,
                GameObjectTarget = goTarget,
                ChildCount = effectiveChildCount,
                HeaderMetadataLine = string.Join(" • ", metadataParts),
                LazyNoticeText = lazyNotice,
                Transform = transformSnapshot,
                Components = components
            };
        }

        public static string BuildHierarchyPath(GameObjectNode? node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var segments = new Stack<string>();
            var current = node;
            var guard = 0;
            while (current != null && guard < 256)
            {
                if (!string.IsNullOrWhiteSpace(current.Name))
                {
                    segments.Push(current.Name);
                }
                current = current.Parent;
                guard++;
            }

            return string.Join(" / ", segments);
        }

        private static GameObjectTransformSnapshot BuildTransformSnapshot(
            Transform? transform,
            int childCount,
            SerializedFile? fallbackSourceFile,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver,
            ProjectIndex? projectIndex)
        {
            if (transform == null)
            {
                return GameObjectTransformSnapshot.None;
            }

            var pos = transform.m_LocalPosition;
            var rot = transform.m_LocalRotation;
            var scale = transform.m_LocalScale;
            var (ex, ey, ez) = QuaternionToEulerDegrees(rot);

            AssetReferenceItem? parentTarget = null;
            var parentName = "None (Root)";
            if (transform.m_Father != null && !transform.m_Father.IsNull)
            {
                var trFile = transform.assetsFile ?? fallbackSourceFile;
                var parentObj = TryGetLoadedObjectRaw(trFile, transform.m_Father.m_FileID, transform.m_Father.m_PathID);
                if (parentObj is Transform parentTr
                    && parentTr.m_GameObject != null
                    && !parentTr.m_GameObject.IsNull
                    && TryGetLoadedObjectRaw(parentTr.assetsFile ?? trFile, parentTr.m_GameObject.m_FileID, parentTr.m_GameObject.m_PathID) is GameObject parentGo)
                {
                    parentName = !string.IsNullOrWhiteSpace(parentGo.m_Name)
                        ? parentGo.m_Name
                        : $"GameObject ({parentGo.m_PathID.ToString(CultureInfo.InvariantCulture)})";
                    parentTarget = BuildReferenceFromObject(parentGo, "Parent GameObject");
                }
                else
                {
                    parentTarget = ResolvePPtr(trFile, transform.m_Father.m_FileID, transform.m_Father.m_PathID, ClassIDType.Transform, "Parent Transform", pptrResolver, projectIndex);
                    if (parentTarget != null && !string.IsNullOrWhiteSpace(parentTarget.Name))
                    {
                        parentName = parentTarget.Name;
                    }
                }
            }

            var isRect = transform is RectTransform;
            var rectSummary = string.Empty;
            if (transform is RectTransform rt)
            {
                rectSummary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Anchored ({rt.m_AnchoredPosition.X:0.##}, {rt.m_AnchoredPosition.Y:0.##}) • Size ({rt.m_SizeDelta.X:0.##}, {rt.m_SizeDelta.Y:0.##}) • Anchors [({rt.m_AnchorMin.X:0.##}, {rt.m_AnchorMin.Y:0.##}) → ({rt.m_AnchorMax.X:0.##}, {rt.m_AnchorMax.Y:0.##})] • Pivot ({rt.m_Pivot.X:0.##}, {rt.m_Pivot.Y:0.##})");
            }

            return new GameObjectTransformSnapshot
            {
                HasTransform = true,
                IsRectTransform = isRect,
                PositionText = FormatVector3(pos),
                RotationEulerText = string.Create(CultureInfo.InvariantCulture, $"({ex:0.#}°, {ey:0.#}°, {ez:0.#}°)"),
                RotationQuaternionText = string.Create(CultureInfo.InvariantCulture, $"({rot.X:0.###}, {rot.Y:0.###}, {rot.Z:0.###}, {rot.W:0.###})"),
                ScaleText = FormatVector3(scale),
                RectSummaryText = rectSummary,
                ChildCount = childCount,
                ParentName = parentName,
                ParentTarget = parentTarget
            };
        }

        private static GameObjectComponentInfo BuildComponentInfo(
            int index,
            AssetStudio.Object obj,
            SerializedFile? fallbackSourceFile,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver,
            ProjectIndex? projectIndex)
        {
            var file = obj.assetsFile ?? fallbackSourceFile;
            var typeName = AssetItem.GetCachedTypeString(obj.type);
            var displayTitle = typeName;
            var subtitle = string.Empty;
            var summary = string.Empty;
            bool? isEnabled = null;
            var linked = new List<AssetReferenceItem>();

            void AddLink(int fileId, long pathId, ClassIDType fallbackType, string relation)
            {
                if (pathId == 0)
                {
                    return;
                }

                var item = ResolvePPtr(file, fileId, pathId, fallbackType, relation, pptrResolver, projectIndex);
                if (item != null)
                {
                    linked.Add(item);
                }
            }

            switch (obj)
            {
                case RectTransform rt:
                    summary = $"Pos {FormatVector3(rt.m_LocalPosition)} • Size ({rt.m_SizeDelta.X.ToString("0.##", CultureInfo.InvariantCulture)}, {rt.m_SizeDelta.Y.ToString("0.##", CultureInfo.InvariantCulture)}) • {(rt.m_Children?.Length ?? 0).ToString(CultureInfo.InvariantCulture)} children";
                    break;

                case Transform tr:
                    summary = $"Pos {FormatVector3(tr.m_LocalPosition)} • Scale {FormatVector3(tr.m_LocalScale)} • {(tr.m_Children?.Length ?? 0).ToString(CultureInfo.InvariantCulture)} children";
                    break;

                case MeshFilter mf:
                    if (mf.m_Mesh != null && !mf.m_Mesh.IsNull)
                    {
                        AddLink(mf.m_Mesh.m_FileID, mf.m_Mesh.m_PathID, ClassIDType.Mesh, "Mesh");
                        var meshObj = TryGetLoadedObjectRaw(file, mf.m_Mesh.m_FileID, mf.m_Mesh.m_PathID) as Mesh;
                        summary = meshObj != null
                            ? $"Mesh: {meshObj.m_Name} ({meshObj.m_VertexCount:N0} verts, {(meshObj.m_SubMeshes?.Length ?? 0).ToString(CultureInfo.InvariantCulture)} submeshes)"
                            : $"Mesh: {linked.LastOrDefault()?.Name ?? "Assigned"}";
                    }
                    else
                    {
                        summary = "Mesh: None";
                    }
                    break;

                case SkinnedMeshRenderer smr:
                    isEnabled = smr.m_Enabled;
                    if (smr.m_Mesh != null && !smr.m_Mesh.IsNull)
                    {
                        AddLink(smr.m_Mesh.m_FileID, smr.m_Mesh.m_PathID, ClassIDType.Mesh, "Skinned Mesh");
                    }
                    if (smr.m_RootBone != null && !smr.m_RootBone.IsNull)
                    {
                        AddLink(smr.m_RootBone.m_FileID, smr.m_RootBone.m_PathID, ClassIDType.Transform, "Root Bone");
                    }
                    if (smr.m_Materials != null)
                    {
                        var matLimit = Math.Min(smr.m_Materials.Length, MaxLinkedMaterialsPerRenderer);
                        for (var m = 0; m < matLimit; m++)
                        {
                            var mp = smr.m_Materials[m];
                            if (mp != null && !mp.IsNull)
                            {
                                AddLink(mp.m_FileID, mp.m_PathID, ClassIDType.Material, $"Material[{m.ToString(CultureInfo.InvariantCulture)}]");
                            }
                        }
                    }
                    var boneCount = smr.m_Bones?.Length ?? 0;
                    var smrMatCount = smr.m_Materials?.Length ?? 0;
                    var blendCount = smr.m_BlendShapeWeights?.Length ?? 0;
                    summary = $"{smrMatCount.ToString(CultureInfo.InvariantCulture)} material(s) • {boneCount.ToString(CultureInfo.InvariantCulture)} bone(s)"
                        + (blendCount > 0 ? $" • {blendCount.ToString(CultureInfo.InvariantCulture)} blendshape(s)" : string.Empty);
                    break;

                case Renderer renderer:
                    isEnabled = renderer.m_Enabled;
                    if (renderer.m_Materials != null)
                    {
                        var matLimit = Math.Min(renderer.m_Materials.Length, MaxLinkedMaterialsPerRenderer);
                        for (var m = 0; m < matLimit; m++)
                        {
                            var mp = renderer.m_Materials[m];
                            if (mp != null && !mp.IsNull)
                            {
                                AddLink(mp.m_FileID, mp.m_PathID, ClassIDType.Material, $"Material[{m.ToString(CultureInfo.InvariantCulture)}]");
                            }
                        }
                    }
                    var matCount = renderer.m_Materials?.Length ?? 0;
                    summary = $"{matCount.ToString(CultureInfo.InvariantCulture)} material(s) • Sorting Order {renderer.m_SortingOrder.ToString(CultureInfo.InvariantCulture)}";
                    break;

                case Animator animator:
                    isEnabled = animator.m_Enabled != 0;
                    if (animator.m_Avatar != null && !animator.m_Avatar.IsNull)
                    {
                        AddLink(animator.m_Avatar.m_FileID, animator.m_Avatar.m_PathID, ClassIDType.Avatar, "Avatar");
                    }
                    if (animator.m_Controller != null && !animator.m_Controller.IsNull)
                    {
                        AddLink(animator.m_Controller.m_FileID, animator.m_Controller.m_PathID, ClassIDType.RuntimeAnimatorController, "Controller");
                    }
                    summary = linked.Count > 0
                        ? string.Join(" • ", linked.Select(x => $"{x.Relation}: {x.Name}"))
                        : "No Avatar or Controller assigned";
                    break;

                case Animation legacyAnim:
                    isEnabled = legacyAnim.m_Enabled != 0;
                    if (legacyAnim.m_Animations != null)
                    {
                        var clipLimit = Math.Min(legacyAnim.m_Animations.Length, MaxLinkedClipsPerAnimation);
                        for (var c = 0; c < clipLimit; c++)
                        {
                            var cp = legacyAnim.m_Animations[c];
                            if (cp != null && !cp.IsNull)
                            {
                                AddLink(cp.m_FileID, cp.m_PathID, ClassIDType.AnimationClip, $"Clip[{c.ToString(CultureInfo.InvariantCulture)}]");
                            }
                        }
                    }
                    summary = $"{(legacyAnim.m_Animations?.Length ?? 0).ToString(CultureInfo.InvariantCulture)} AnimationClip(s)";
                    break;

                case VideoPlayer vp:
                    isEnabled = vp.m_Enabled != 0;
                    if (vp.m_VideoClip != null && !vp.m_VideoClip.IsNull)
                    {
                        AddLink(vp.m_VideoClip.m_FileID, vp.m_VideoClip.m_PathID, ClassIDType.VideoClip, "VideoClip");
                    }
                    summary = !string.IsNullOrWhiteSpace(vp.m_Url)
                        ? $"URL: {vp.m_Url}"
                        : (linked.Count > 0 ? $"Clip: {linked[0].Name}" : "No VideoClip assigned");
                    break;

                case MonoBehaviour mb:
                    isEnabled = mb.m_Enabled != 0;
                    var scriptDisplay = ResolveMonoScriptDisplay(mb, file, pptrResolver, projectIndex, out var assemblyName, out var scriptLink);
                    if (!string.IsNullOrWhiteSpace(scriptDisplay))
                    {
                        displayTitle = $"{scriptDisplay} (MonoBehaviour)";
                    }
                    if (!string.IsNullOrWhiteSpace(assemblyName))
                    {
                        subtitle = assemblyName;
                    }
                    if (scriptLink != null)
                    {
                        linked.Add(scriptLink);
                    }
                    summary = !string.IsNullOrWhiteSpace(mb.m_Name)
                        ? $"Name: {mb.m_Name}"
                        : "Click Inspect to open MonoBehaviour Tree/JSON view";
                    break;

                case Behaviour behaviour:
                    isEnabled = behaviour.m_Enabled != 0;
                    summary = $"PathID: {obj.m_PathID.ToString(CultureInfo.InvariantCulture)}";
                    break;

                default:
                    // Generic Unity component parsed as Object (e.g., AudioSource, Camera, Light, Collider, Canvas)
                    if (obj.type == ClassIDType.AudioSource && obj.serializedType?.m_Type != null)
                    {
                        TryExtractAudioSourceLinks(obj, file, pptrResolver, projectIndex, linked, out isEnabled, out summary);
                    }
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        summary = $"PathID: {obj.m_PathID.ToString(CultureInfo.InvariantCulture)}";
                    }
                    break;
            }

            var componentTarget = BuildReferenceFromObject(obj, "Component");
            if (obj is MonoBehaviour && !string.IsNullOrWhiteSpace(displayTitle) && componentTarget != null)
            {
                componentTarget = new AssetReferenceItem
                {
                    AssetUniqueId = componentTarget.AssetUniqueId,
                    Name = displayTitle,
                    AssetType = componentTarget.AssetType,
                    TypeName = componentTarget.TypeName,
                    Relation = componentTarget.Relation,
                    PathId = componentTarget.PathId,
                    SerializedFileName = componentTarget.SerializedFileName,
                    Direction = componentTarget.Direction
                };
            }

            return new GameObjectComponentInfo
            {
                Index = index,
                ComponentType = obj.type,
                TypeName = typeName,
                DisplayTitle = displayTitle,
                Subtitle = subtitle,
                Summary = summary,
                IsEnabled = isEnabled,
                PathId = obj.m_PathID,
                ComponentTarget = componentTarget,
                LinkedAssets = linked
            };
        }

        private static GameObjectComponentInfo BuildUnresolvedComponentInfo(
            int index,
            SerializedFile? sourceFile,
            int fileId,
            long pathId,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver,
            ProjectIndex? projectIndex)
        {
            var resolved = ResolvePPtr(sourceFile, fileId, pathId, ClassIDType.Component, "Component", pptrResolver, projectIndex);
            var type = resolved?.AssetType ?? ClassIDType.Component;
            var typeName = !string.IsNullOrWhiteSpace(resolved?.TypeName)
                ? resolved!.TypeName
                : AssetItem.GetCachedTypeString(type);
            var title = !string.IsNullOrWhiteSpace(resolved?.Name) ? resolved!.Name : typeName;

            return new GameObjectComponentInfo
            {
                Index = index,
                ComponentType = type,
                TypeName = typeName,
                DisplayTitle = title,
                Subtitle = "Not loaded in memory",
                Summary = $"FileID={fileId.ToString(CultureInfo.InvariantCulture)}, PathID={pathId.ToString(CultureInfo.InvariantCulture)}",
                IsEnabled = null,
                PathId = pathId,
                ComponentTarget = resolved,
                LinkedAssets = Array.Empty<AssetReferenceItem>()
            };
        }

        private static string ResolveMonoScriptDisplay(
            MonoBehaviour mb,
            SerializedFile? sourceFile,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver,
            ProjectIndex? projectIndex,
            out string assemblyName,
            out AssetReferenceItem? scriptLink)
        {
            assemblyName = string.Empty;
            scriptLink = null;

            if (mb.m_Script == null || mb.m_Script.IsNull)
            {
                return !string.IsNullOrWhiteSpace(mb.m_Name) ? mb.m_Name : string.Empty;
            }

            if (TryGetLoadedObjectRaw(sourceFile, mb.m_Script.m_FileID, mb.m_Script.m_PathID) is MonoScript loadedScript)
            {
                assemblyName = loadedScript.m_AssemblyName ?? string.Empty;
                var fullClass = !string.IsNullOrWhiteSpace(loadedScript.m_Namespace)
                    ? $"{loadedScript.m_Namespace}.{loadedScript.m_ClassName}"
                    : (loadedScript.m_ClassName ?? loadedScript.m_Name ?? string.Empty);

                scriptLink = BuildReferenceFromObject(loadedScript, "MonoScript");
                if (!string.IsNullOrWhiteSpace(fullClass))
                {
                    return fullClass;
                }
            }

            scriptLink = ResolvePPtr(sourceFile, mb.m_Script.m_FileID, mb.m_Script.m_PathID, ClassIDType.MonoScript, "MonoScript", pptrResolver, projectIndex);
            if (scriptLink != null && !string.IsNullOrWhiteSpace(scriptLink.Name))
            {
                return scriptLink.Name;
            }

            return !string.IsNullOrWhiteSpace(mb.m_Name) ? mb.m_Name : string.Empty;
        }

        private static void TryExtractAudioSourceLinks(
            AssetStudio.Object obj,
            SerializedFile? sourceFile,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver,
            ProjectIndex? projectIndex,
            List<AssetReferenceItem> linkedOut,
            out bool? isEnabled,
            out string summary)
        {
            isEnabled = null;
            summary = string.Empty;
            try
            {
                var fields = obj.ToType();
                if (fields == null)
                {
                    return;
                }

                if (fields.Contains("m_Enabled") && fields["m_Enabled"] is byte b)
                {
                    isEnabled = b != 0;
                }
                else if (fields.Contains("m_Enabled") && fields["m_Enabled"] is bool flag)
                {
                    isEnabled = flag;
                }

                if (fields.Contains("m_audioClip") && fields["m_audioClip"] is System.Collections.IDictionary clipDict
                    && MonoBehaviourTreeBuilder.TryReadPPtr(clipDict, out var fileId, out var pathId)
                    && pathId != 0)
                {
                    var clipLink = ResolvePPtr(sourceFile, fileId, pathId, ClassIDType.AudioClip, "AudioClip", pptrResolver, projectIndex);
                    if (clipLink != null)
                    {
                        linkedOut.Add(clipLink);
                        summary = $"AudioClip: {clipLink.Name}";
                    }
                }
            }
            catch
            {
                // Ignore malformed TypeTree dumps safely.
            }
        }

        private static AssetReferenceItem? ResolvePPtr(
            SerializedFile? sourceFile,
            int fileId,
            long pathId,
            ClassIDType fallbackType,
            string relation,
            Func<SerializedFile?, int, long, ClassIDType, string, AssetReferenceItem?>? pptrResolver,
            ProjectIndex? projectIndex)
        {
            if (pathId == 0)
            {
                return null;
            }

            if (pptrResolver != null)
            {
                try
                {
                    var custom = pptrResolver(sourceFile, fileId, pathId, fallbackType, relation);
                    if (custom != null)
                    {
                        return custom;
                    }
                }
                catch
                {
                }
            }

            var loaded = TryGetLoadedObjectRaw(sourceFile, fileId, pathId);
            if (loaded != null)
            {
                return BuildReferenceFromObject(loaded, relation);
            }

            string serializedFileName = string.Empty;
            string uniqueId = string.Empty;
            if (sourceFile != null)
            {
                if (fileId == 0)
                {
                    serializedFileName = sourceFile.fileName ?? string.Empty;
                    uniqueId = AssetHandle.BuildUniqueID(sourceFile, pathId);
                }
                else if (fileId > 0 && sourceFile.m_Externals != null && fileId - 1 < sourceFile.m_Externals.Count)
                {
                    var ext = sourceFile.m_Externals[fileId - 1];
                    serializedFileName = NormalizeExternalFileName(ext?.fileName, ext?.pathName);
                    if (sourceFile.assetsManager != null
                        && sourceFile.assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile)
                        && extFile != null)
                    {
                        serializedFileName = extFile.fileName ?? serializedFileName;
                        uniqueId = AssetHandle.BuildUniqueID(extFile, pathId);
                    }
                    else if (!string.IsNullOrWhiteSpace(serializedFileName))
                    {
                        uniqueId = $"{serializedFileName}#{pathId.ToString(CultureInfo.InvariantCulture)}";
                    }
                }
            }

            if (projectIndex != null)
            {
                AssetHandle? handle = !string.IsNullOrWhiteSpace(uniqueId)
                    ? projectIndex.GetHandle(uniqueId)
                    : null;
                if (handle == null && !string.IsNullOrWhiteSpace(serializedFileName))
                {
                    handle = projectIndex.GetHandlesForFile(serializedFileName)
                        .FirstOrDefault(h => h.PathID == pathId);
                }

                if (handle != null)
                {
                    return new AssetReferenceItem
                    {
                        AssetUniqueId = handle.UniqueID,
                        Name = !string.IsNullOrWhiteSpace(handle.Name) ? handle.Name : $"{handle.Type} ({handle.PathID})",
                        AssetType = handle.Type,
                        TypeName = AssetItem.GetCachedTypeString(handle.Type),
                        Relation = relation,
                        PathId = handle.PathID,
                        SerializedFileName = handle.SerializedFileName ?? serializedFileName,
                        Direction = AssetReferenceDirection.References
                    };
                }
            }

            var resolvedType = fallbackType;
            if (fileId == 0 && sourceFile != null && sourceFile.ObjectInfoDic != null && sourceFile.ObjectInfoDic.TryGetValue(pathId, out var objInfo))
            {
                resolvedType = (ClassIDType)objInfo.classID;
            }

            var typeLabel = resolvedType != ClassIDType.UnknownType
                ? AssetItem.GetCachedTypeString(resolvedType)
                : "Object";

            return new AssetReferenceItem
            {
                AssetUniqueId = uniqueId,
                Name = $"{typeLabel} ({pathId.ToString(CultureInfo.InvariantCulture)})",
                AssetType = resolvedType,
                TypeName = typeLabel,
                Relation = relation,
                PathId = pathId,
                SerializedFileName = serializedFileName,
                Direction = AssetReferenceDirection.References
            };
        }

        private static AssetStudio.Object? TryGetLoadedObjectRaw(SerializedFile? sourceFile, int fileId, long pathId)
        {
            if (sourceFile == null || pathId == 0)
            {
                return null;
            }

            if (fileId == 0)
            {
                return sourceFile.TryGetObject(pathId, out var localObj) ? localObj : null;
            }

            if (fileId > 0 && sourceFile.m_Externals != null && fileId - 1 < sourceFile.m_Externals.Count)
            {
                var ext = sourceFile.m_Externals[fileId - 1];
                if (sourceFile.assetsManager != null
                    && sourceFile.assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile)
                    && extFile != null
                    && extFile.TryGetObject(pathId, out var extObj))
                {
                    return extObj;
                }
            }

            return null;
        }

        private static AssetReferenceItem BuildReferenceFromObject(AssetStudio.Object obj, string relation)
        {
            var uniqueId = obj.assetsFile != null
                ? AssetHandle.BuildUniqueID(obj.assetsFile, obj.m_PathID)
                : string.Empty;
            var name = obj switch
            {
                NamedObject named when !string.IsNullOrWhiteSpace(named.m_Name) => named.m_Name,
                GameObject go when !string.IsNullOrWhiteSpace(go.m_Name) => go.m_Name,
                MonoBehaviour mb when !string.IsNullOrWhiteSpace(mb.m_Name) => mb.m_Name,
                _ => $"{AssetItem.GetCachedTypeString(obj.type)} ({obj.m_PathID.ToString(CultureInfo.InvariantCulture)})"
            };

            return new AssetReferenceItem
            {
                AssetUniqueId = uniqueId,
                Name = name,
                AssetType = obj.type,
                TypeName = AssetItem.GetCachedTypeString(obj.type),
                Relation = relation,
                PathId = obj.m_PathID,
                SerializedFileName = obj.assetsFile?.fileName ?? string.Empty,
                Direction = AssetReferenceDirection.References
            };
        }

        private static string NormalizeExternalFileName(string? fileName, string? pathName)
        {
            var candidate = !string.IsNullOrWhiteSpace(fileName) ? fileName! : (pathName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            var normalized = candidate.Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private static string FormatVector3(Vector3 v)
        {
            return string.Create(CultureInfo.InvariantCulture, $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})");
        }

        internal static (double X, double Y, double Z) QuaternionToEulerDegrees(Quaternion q)
        {
            var sinrCosp = 2.0 * (q.W * q.X + q.Y * q.Z);
            var cosrCosp = 1.0 - 2.0 * (q.X * q.X + q.Y * q.Y);
            var roll = Math.Atan2(sinrCosp, cosrCosp);

            var sinp = 2.0 * (q.W * q.Y - q.Z * q.X);
            var pitch = Math.Abs(sinp) >= 1.0
                ? Math.CopySign(Math.PI / 2.0, sinp)
                : Math.Asin(sinp);

            var sinyCosp = 2.0 * (q.W * q.Z + q.X * q.Y);
            var cosyCosp = 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z);
            var yaw = Math.Atan2(sinyCosp, cosyCosp);

            const double radToDeg = 180.0 / Math.PI;
            return (roll * radToDeg, pitch * radToDeg, yaw * radToDeg);
        }
    }
}
