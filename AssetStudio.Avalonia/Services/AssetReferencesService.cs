using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public enum AssetReferenceDirection
    {
        ReferencedBy = 0,
        References = 1
    }

    public sealed class AssetReferenceItem
    {
        public string AssetUniqueId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ClassIDType AssetType { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public string Relation { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string SerializedFileName { get; init; } = string.Empty;
        public AssetReferenceDirection Direction { get; init; }

        public string PathIdText => PathId != 0
            ? PathId.ToString(CultureInfo.InvariantCulture)
            : "-";

        public bool IsNavigable => !string.IsNullOrWhiteSpace(AssetUniqueId) || PathId != 0;

        public string DetailLine
        {
            get
            {
                var typeLabel = !string.IsNullOrWhiteSpace(TypeName)
                    ? TypeName
                    : AssetItem.GetCachedTypeString(AssetType);
                var rel = string.IsNullOrWhiteSpace(Relation) ? "Reference" : Relation;
                return $"{typeLabel} • {rel} • PathID: {PathIdText}";
            }
        }

        public bool MatchesFilter(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var needle = query.Trim();
            return (!string.IsNullOrEmpty(Name) && Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(TypeName) && TypeName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(Relation) && Relation.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || PathIdText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(SerializedFileName) && SerializedFileName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class AssetReferencesSnapshot
    {
        public static readonly AssetReferencesSnapshot Empty = new();

        public string TargetUniqueId { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public ClassIDType TargetType { get; init; }
        public IReadOnlyList<AssetReferenceItem> ReferencedBy { get; init; } = Array.Empty<AssetReferenceItem>();
        public IReadOnlyList<AssetReferenceItem> References { get; init; } = Array.Empty<AssetReferenceItem>();
    }

    public sealed class AssetReferencesQueryContext
    {
        public IReadOnlyList<SerializedFile> LoadedFiles { get; init; } = Array.Empty<SerializedFile>();
        public ProjectIndex? ProjectIndex { get; init; }
        public Action<string, List<AssetReferenceItem>, List<AssetReferenceItem>, int>? SemanticCacheLoader { get; init; }
        public int MaxItemsPerDirection { get; init; } = 500;
    }

    public static class AssetReferencesService
    {
        private const int MaxGenericPPtrDepth = 6;
        private const int MaxGenericPPtrsPerObject = 128;

        public static AssetReferencesSnapshot BuildReferences(
            AssetItem? selectedItem,
            AssetStudio.Object? resolvedObject,
            AssetReferencesQueryContext? context = null,
            CancellationToken cancellationToken = default)
        {
            if (selectedItem == null && resolvedObject == null)
            {
                return AssetReferencesSnapshot.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var maxItems = Math.Max(1, context?.MaxItemsPerDirection ?? 500);
            var targetUniqueId = ResolveTargetUniqueId(selectedItem, resolvedObject);
            var targetPathId = resolvedObject?.m_PathID ?? selectedItem?.PathID ?? 0L;
            var targetFile = resolvedObject?.assetsFile ?? selectedItem?.SourceFile;
            var targetFileName = targetFile?.fileName ?? selectedItem?.Handle?.SerializedFileName ?? string.Empty;
            var targetName = !string.IsNullOrWhiteSpace(selectedItem?.Name)
                ? selectedItem!.Name
                : GetObjectDisplayName(resolvedObject);
            var targetType = resolvedObject?.type ?? selectedItem?.Type ?? ClassIDType.UnknownType;

            var rawReferencedBy = new List<AssetReferenceItem>();
            var rawReferences = new List<AssetReferenceItem>();

            // 1) SQLite v15 semantic graph cache (O(log N) indexed queries; safe in Lazy Mode without loading files).
            if (context?.SemanticCacheLoader != null && !string.IsNullOrWhiteSpace(targetUniqueId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    context.SemanticCacheLoader(targetUniqueId, rawReferencedBy, rawReferences, maxItems);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Cache query is best-effort; still return in-memory references cleanly.
                }
            }

            // 2) Direct outgoing references from the currently materialized object in memory.
            if (resolvedObject != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CollectOutgoingReferencesFromObject(
                    resolvedObject,
                    targetUniqueId,
                    context?.ProjectIndex,
                    rawReferences,
                    cancellationToken);
            }

            // 3) Incoming back-references from currently loaded SerializedFiles in memory
            // (covers Eager Mode completely and enriches Lazy Mode without triggering any disk loads).
            var loadedFiles = context?.LoadedFiles;
            if (loadedFiles != null && loadedFiles.Count > 0 && targetPathId != 0)
            {
                CollectIncomingReferencesFromLoadedFiles(
                    loadedFiles,
                    targetUniqueId,
                    targetFile,
                    targetFileName,
                    targetPathId,
                    resolvedObject,
                    context?.ProjectIndex,
                    rawReferencedBy,
                    rawReferences,
                    maxItems,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var dedupedReferencedBy = DeduplicateAndSort(rawReferencedBy, targetUniqueId, maxItems);
            var dedupedReferences = DeduplicateAndSort(rawReferences, targetUniqueId, maxItems);

            return new AssetReferencesSnapshot
            {
                TargetUniqueId = targetUniqueId,
                TargetName = targetName,
                TargetType = targetType,
                ReferencedBy = dedupedReferencedBy,
                References = dedupedReferences
            };
        }

        private static string ResolveTargetUniqueId(AssetItem? selectedItem, AssetStudio.Object? resolvedObject)
        {
            if (resolvedObject?.assetsFile != null)
            {
                return AssetHandle.BuildUniqueID(resolvedObject.assetsFile, resolvedObject.m_PathID);
            }

            if (selectedItem?.Handle != null && !string.IsNullOrWhiteSpace(selectedItem.Handle.UniqueID))
            {
                return selectedItem.Handle.UniqueID;
            }

            if (selectedItem != null && !string.IsNullOrWhiteSpace(selectedItem.UniqueID))
            {
                return selectedItem.UniqueID;
            }

            if (resolvedObject != null)
            {
                return $"runtime:{resolvedObject.type}:{resolvedObject.m_PathID.ToString(CultureInfo.InvariantCulture)}";
            }

            return string.Empty;
        }

        private static void CollectOutgoingReferencesFromObject(
            AssetStudio.Object obj,
            string selfUniqueId,
            ProjectIndex? projectIndex,
            List<AssetReferenceItem> referencesOut,
            CancellationToken cancellationToken)
        {
            var sourceFile = obj.assetsFile;

            void AddPPtr<T>(PPtr<T>? pptr, string relation, ClassIDType fallbackType = ClassIDType.UnknownType) where T : AssetStudio.Object
            {
                if (pptr == null || pptr.IsNull)
                {
                    return;
                }

                var item = BuildReferenceItemFromPPtr(
                    sourceFile,
                    pptr.m_FileID,
                    pptr.m_PathID,
                    relation,
                    AssetReferenceDirection.References,
                    fallbackType,
                    projectIndex);
                if (item != null && !string.Equals(item.AssetUniqueId, selfUniqueId, StringComparison.Ordinal))
                {
                    referencesOut.Add(item);
                }
            }

            switch (obj)
            {
                case Material mat:
                    AddPPtr(mat.m_Shader, "Shader", ClassIDType.Shader);
                    AddPPtr(mat.m_Parent, "Parent Material", ClassIDType.Material);
                    if (mat.m_SavedProperties?.m_TexEnvs != null)
                    {
                        foreach (var kv in mat.m_SavedProperties.m_TexEnvs)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var slot = string.IsNullOrWhiteSpace(kv.Key) ? "Texture" : $"Texture ({kv.Key})";
                            AddPPtr(kv.Value?.m_Texture, slot, ClassIDType.Texture2D);
                        }
                    }
                    break;

                case Sprite sprite:
                    AddPPtr(sprite.m_SpriteAtlas, "SpriteAtlas", ClassIDType.SpriteAtlas);
                    if (sprite.m_RD != null)
                    {
                        AddPPtr(sprite.m_RD.texture, "Texture", ClassIDType.Texture2D);
                        AddPPtr(sprite.m_RD.alphaTexture, "Alpha Texture", ClassIDType.Texture2D);
                        if (sprite.m_RD.secondaryTextures != null)
                        {
                            foreach (var sec in sprite.m_RD.secondaryTextures)
                            {
                                var label = string.IsNullOrWhiteSpace(sec?.name)
                                    ? "Secondary Texture"
                                    : $"Secondary Texture ({sec!.name})";
                                AddPPtr(sec?.texture, label, ClassIDType.Texture2D);
                            }
                        }
                    }
                    break;

                case SpriteAtlas atlas:
                    if (atlas.m_PackedSprites != null)
                    {
                        var limit = Math.Min(atlas.m_PackedSprites.Length, 256);
                        for (var i = 0; i < limit; i++)
                        {
                            AddPPtr(atlas.m_PackedSprites[i], "Packed Sprite", ClassIDType.Sprite);
                        }
                    }
                    if (atlas.m_RenderDataMap != null)
                    {
                        foreach (var data in atlas.m_RenderDataMap.Values)
                        {
                            AddPPtr(data.texture, "Atlas Texture", ClassIDType.Texture2D);
                            AddPPtr(data.alphaTexture, "Atlas Alpha Texture", ClassIDType.Texture2D);
                        }
                    }
                    break;

                case AssetStudio.Font font:
                    AddPPtr(font.m_Texture, "Font Atlas Texture", ClassIDType.Texture2D);
                    AddPPtr(font.m_DefaultMaterial, "Default Material", ClassIDType.Material);
                    break;

                case MeshFilter mf:
                    AddPPtr(mf.m_Mesh, "Mesh", ClassIDType.Mesh);
                    AddPPtr(mf.m_GameObject, "GameObject", ClassIDType.GameObject);
                    break;

                case SkinnedMeshRenderer smr:
                    AddPPtr(smr.m_Mesh, "Skinned Mesh", ClassIDType.Mesh);
                    AddPPtr(smr.m_GameObject, "GameObject", ClassIDType.GameObject);
                    AddPPtr(smr.m_RootBone, "Root Bone", ClassIDType.Transform);
                    if (smr.m_Materials != null)
                    {
                        for (var i = 0; i < smr.m_Materials.Length; i++)
                        {
                            AddPPtr(smr.m_Materials[i], $"Material[{i}]", ClassIDType.Material);
                        }
                    }
                    break;

                case Renderer renderer:
                    AddPPtr(renderer.m_GameObject, "GameObject", ClassIDType.GameObject);
                    if (renderer.m_Materials != null)
                    {
                        for (var i = 0; i < renderer.m_Materials.Length; i++)
                        {
                            AddPPtr(renderer.m_Materials[i], $"Material[{i}]", ClassIDType.Material);
                        }
                    }
                    break;

                case Animator animator:
                    AddPPtr(animator.m_Avatar, "Avatar", ClassIDType.Avatar);
                    AddPPtr(animator.m_Controller, "Animator Controller", ClassIDType.RuntimeAnimatorController);
                    AddPPtr(animator.m_GameObject, "GameObject", ClassIDType.GameObject);
                    break;

                case AnimatorController controller:
                    if (controller.m_AnimationClips != null)
                    {
                        for (var i = 0; i < controller.m_AnimationClips.Length; i++)
                        {
                            AddPPtr(controller.m_AnimationClips[i], "AnimationClip", ClassIDType.AnimationClip);
                        }
                    }
                    if (controller.m_StateMachineBehaviours != null)
                    {
                        for (var i = 0; i < controller.m_StateMachineBehaviours.Length; i++)
                        {
                            AddPPtr(controller.m_StateMachineBehaviours[i], "StateMachineBehaviour", ClassIDType.MonoBehaviour);
                        }
                    }
                    break;

                case AnimatorOverrideController overrideController:
                    AddPPtr(overrideController.m_Controller, "Base Controller", ClassIDType.RuntimeAnimatorController);
                    if (overrideController.m_Clips != null)
                    {
                        foreach (var pair in overrideController.m_Clips)
                        {
                            AddPPtr(pair?.m_OriginalClip, "Original Clip", ClassIDType.AnimationClip);
                            AddPPtr(pair?.m_OverrideClip, "Override Clip", ClassIDType.AnimationClip);
                        }
                    }
                    break;

                case Animation legacyAnim:
                    AddPPtr(legacyAnim.m_GameObject, "GameObject", ClassIDType.GameObject);
                    if (legacyAnim.m_Animations != null)
                    {
                        for (var i = 0; i < legacyAnim.m_Animations.Length; i++)
                        {
                            AddPPtr(legacyAnim.m_Animations[i], "AnimationClip", ClassIDType.AnimationClip);
                        }
                    }
                    break;

                case AnimationClip animClip:
                    if (animClip.m_PPtrCurves != null)
                    {
                        foreach (var curve in animClip.m_PPtrCurves)
                        {
                            if (curve?.curve == null) continue;
                            var slot = string.IsNullOrWhiteSpace(curve.attribute) ? "PPtr Curve" : $"PPtr Curve ({curve.attribute})";
                            foreach (var keyframe in curve.curve)
                            {
                                AddPPtr(keyframe?.value, slot);
                            }
                        }
                    }
                    break;

                case GameObject go:
                    if (go.m_Components != null)
                    {
                        for (var i = 0; i < go.m_Components.Length; i++)
                        {
                            AddPPtr(go.m_Components[i], "Component");
                        }
                    }
                    break;

                case Transform tr:
                    AddPPtr(tr.m_GameObject, "GameObject", ClassIDType.GameObject);
                    AddPPtr(tr.m_Father, "Parent Transform", ClassIDType.Transform);
                    if (tr.m_Children != null)
                    {
                        var limit = Math.Min(tr.m_Children.Length, 128);
                        for (var i = 0; i < limit; i++)
                        {
                            AddPPtr(tr.m_Children[i], "Child Transform", ClassIDType.Transform);
                        }
                    }
                    break;

                case VideoPlayer videoPlayer:
                    AddPPtr(videoPlayer.m_VideoClip, "VideoClip", ClassIDType.VideoClip);
                    AddPPtr(videoPlayer.m_GameObject, "GameObject", ClassIDType.GameObject);
                    break;

                case MonoBehaviour mb:
                    AddPPtr(mb.m_Script, "MonoScript", ClassIDType.MonoScript);
                    AddPPtr(mb.m_GameObject, "GameObject", ClassIDType.GameObject);
                    // Extract embedded TypeTree PPtrs safely without ever prompting for assembly loading!
                    if (mb.serializedType?.m_Type != null && sourceFile != null)
                    {
                        try
                        {
                            var fields = mb.ToType();
                            if (fields != null)
                            {
                                var foundCount = 0;
                                ExtractGenericPPtrsFromDictionary(
                                    fields,
                                    prefix: string.Empty,
                                    depth: 0,
                                    sourceFile,
                                    selfUniqueId,
                                    projectIndex,
                                    referencesOut,
                                    ref foundCount,
                                    cancellationToken);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Ignore malformed TypeTree dumps safely.
                        }
                    }
                    break;

                case AssetBundle ab:
                    if (ab.m_PreloadTable != null)
                    {
                        var limit = Math.Min(ab.m_PreloadTable.Length, 200);
                        for (var i = 0; i < limit; i++)
                        {
                            AddPPtr(ab.m_PreloadTable[i], "Preload Asset");
                        }
                    }
                    break;

                case Component comp:
                    AddPPtr(comp.m_GameObject, "GameObject", ClassIDType.GameObject);
                    break;
            }
        }

        private static void CollectIncomingReferencesFromLoadedFiles(
            IReadOnlyList<SerializedFile> loadedFiles,
            string targetUniqueId,
            SerializedFile? targetFile,
            string targetFileName,
            long targetPathId,
            AssetStudio.Object? targetObj,
            ProjectIndex? projectIndex,
            List<AssetReferenceItem> referencedByOut,
            List<AssetReferenceItem> referencesOut,
            int maxItems,
            CancellationToken cancellationToken)
        {
            var meshTarget = targetObj as Mesh;
            var avatarTarget = targetObj as Avatar;
            var clipTarget = targetObj as AnimationClip;

            for (var f = 0; f < loadedFiles.Count; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = loadedFiles[f];
                if (file == null)
                {
                    continue;
                }

                AssetStudio.Object[] objectsSnapshot;
                lock (file)
                {
                    objectsSnapshot = file.Objects.ToArray();
                }

                for (var i = 0; i < objectsSnapshot.Length; i++)
                {
                    if ((i & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var candidate = objectsSnapshot[i];
                    if (candidate == null || ReferenceEquals(candidate, targetObj))
                    {
                        continue;
                    }

                    var candidateUniqueId = candidate.assetsFile != null
                        ? AssetHandle.BuildUniqueID(candidate.assetsFile, candidate.m_PathID)
                        : $"runtime:{candidate.type}:{candidate.m_PathID.ToString(CultureInfo.InvariantCulture)}";
                    if (string.Equals(candidateUniqueId, targetUniqueId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool MatchesPPtr<T>(PPtr<T>? pptr) where T : AssetStudio.Object
                    {
                        return PPtrMatchesTarget(pptr, candidate.assetsFile, targetFile, targetFileName, targetPathId, targetUniqueId);
                    }

                    void AddUsedBy(AssetStudio.Object source, string relation)
                    {
                        if (referencedByOut.Count >= maxItems * 2)
                        {
                            return;
                        }

                        var item = BuildReferenceItemFromObject(source, relation, AssetReferenceDirection.ReferencedBy);
                        if (!string.Equals(item.AssetUniqueId, targetUniqueId, StringComparison.Ordinal))
                        {
                            referencedByOut.Add(item);
                        }
                    }

                    void AddUses(AssetStudio.Object target, string relation)
                    {
                        if (referencesOut.Count >= maxItems * 2)
                        {
                            return;
                        }

                        var item = BuildReferenceItemFromObject(target, relation, AssetReferenceDirection.References);
                        if (!string.Equals(item.AssetUniqueId, targetUniqueId, StringComparison.Ordinal))
                        {
                            referencesOut.Add(item);
                        }
                    }

                    switch (candidate)
                    {
                        case Material mat:
                            if (MatchesPPtr(mat.m_Shader))
                            {
                                AddUsedBy(mat, "Shader");
                            }
                            if (MatchesPPtr(mat.m_Parent))
                            {
                                AddUsedBy(mat, "Child Material");
                            }
                            if (mat.m_SavedProperties?.m_TexEnvs != null)
                            {
                                foreach (var kv in mat.m_SavedProperties.m_TexEnvs)
                                {
                                    if (MatchesPPtr(kv.Value?.m_Texture))
                                    {
                                        var slot = string.IsNullOrWhiteSpace(kv.Key) ? "Texture" : kv.Key;
                                        AddUsedBy(mat, $"Material ({slot})");
                                    }
                                }
                            }
                            break;

                        case Sprite sprite:
                            if (MatchesPPtr(sprite.m_SpriteAtlas))
                            {
                                AddUsedBy(sprite, "SpriteAtlas");
                            }
                            if (sprite.m_RD != null)
                            {
                                if (MatchesPPtr(sprite.m_RD.texture))
                                {
                                    AddUsedBy(sprite, "Sprite Texture");
                                }
                                if (MatchesPPtr(sprite.m_RD.alphaTexture))
                                {
                                    AddUsedBy(sprite, "Sprite Alpha Texture");
                                }
                                if (sprite.m_RD.secondaryTextures != null)
                                {
                                    foreach (var sec in sprite.m_RD.secondaryTextures)
                                    {
                                        if (MatchesPPtr(sec?.texture))
                                        {
                                            AddUsedBy(sprite, string.IsNullOrWhiteSpace(sec?.name) ? "Sprite Secondary Texture" : $"Sprite ({sec!.name})");
                                        }
                                    }
                                }
                            }
                            break;

                        case SpriteAtlas atlas:
                            if (atlas.m_PackedSprites != null)
                            {
                                for (var s = 0; s < atlas.m_PackedSprites.Length; s++)
                                {
                                    if (MatchesPPtr(atlas.m_PackedSprites[s]))
                                    {
                                        AddUsedBy(atlas, "Packed in SpriteAtlas");
                                        break;
                                    }
                                }
                            }
                            if (atlas.m_RenderDataMap != null)
                            {
                                foreach (var data in atlas.m_RenderDataMap.Values)
                                {
                                    if (MatchesPPtr(data.texture) || MatchesPPtr(data.alphaTexture))
                                    {
                                        AddUsedBy(atlas, "SpriteAtlas Texture");
                                        break;
                                    }
                                }
                            }
                            break;

                        case AssetStudio.Font font:
                            if (MatchesPPtr(font.m_Texture))
                            {
                                AddUsedBy(font, "Font Atlas");
                            }
                            if (MatchesPPtr(font.m_DefaultMaterial))
                            {
                                AddUsedBy(font, "Font Default Material");
                            }
                            break;

                        case MeshFilter mf:
                            if (MatchesPPtr(mf.m_Mesh))
                            {
                                AddUsedBy(mf, "MeshFilter");
                                if (TryGetLoadedGameObject(mf.m_GameObject, out var go))
                                {
                                    AddUsedBy(go, "GameObject (via MeshFilter)");
                                    if (meshTarget != null && go.m_MeshRenderer?.m_Materials != null)
                                    {
                                        for (var m = 0; m < go.m_MeshRenderer.m_Materials.Length; m++)
                                        {
                                            var matPtr = go.m_MeshRenderer.m_Materials[m];
                                            var matItem = BuildReferenceItemFromPPtr(
                                                go.m_MeshRenderer.assetsFile,
                                                matPtr.m_FileID,
                                                matPtr.m_PathID,
                                                $"SubMesh {m} Material",
                                                AssetReferenceDirection.References,
                                                ClassIDType.Material,
                                                projectIndex);
                                            if (matItem != null)
                                            {
                                                referencesOut.Add(matItem);
                                            }
                                        }
                                    }
                                }
                            }
                            if (MatchesPPtr(mf.m_GameObject))
                            {
                                AddUsedBy(mf, "Component");
                            }
                            break;

                        case SkinnedMeshRenderer smr:
                            if (MatchesPPtr(smr.m_Mesh))
                            {
                                AddUsedBy(smr, "SkinnedMeshRenderer");
                                if (TryGetLoadedGameObject(smr.m_GameObject, out var go))
                                {
                                    AddUsedBy(go, "GameObject (via SkinnedMeshRenderer)");
                                }
                                if (meshTarget != null && smr.m_Materials != null)
                                {
                                    for (var m = 0; m < smr.m_Materials.Length; m++)
                                    {
                                        var matPtr = smr.m_Materials[m];
                                        var matItem = BuildReferenceItemFromPPtr(
                                            smr.assetsFile,
                                            matPtr.m_FileID,
                                            matPtr.m_PathID,
                                            $"SubMesh {m} Material",
                                            AssetReferenceDirection.References,
                                            ClassIDType.Material,
                                            projectIndex);
                                        if (matItem != null)
                                        {
                                            referencesOut.Add(matItem);
                                        }
                                    }
                                }
                            }
                            if (smr.m_Materials != null)
                            {
                                for (var m = 0; m < smr.m_Materials.Length; m++)
                                {
                                    if (MatchesPPtr(smr.m_Materials[m]))
                                    {
                                        AddUsedBy(smr, $"Material[{m}]");
                                        if (TryGetLoadedObject(smr.m_Mesh, out var smrMesh))
                                        {
                                            AddUsedBy(smrMesh, $"Mesh (SubMesh {m})");
                                        }
                                        break;
                                    }
                                }
                            }
                            if (MatchesPPtr(smr.m_GameObject))
                            {
                                AddUsedBy(smr, "Component");
                            }
                            break;

                        case MeshRenderer mr:
                            if (mr.m_Materials != null)
                            {
                                for (var m = 0; m < mr.m_Materials.Length; m++)
                                {
                                    if (MatchesPPtr(mr.m_Materials[m]))
                                    {
                                        AddUsedBy(mr, $"Material[{m}]");
                                        if (TryGetLoadedGameObject(mr.m_GameObject, out var mrGo)
                                            && mrGo.m_MeshFilter != null
                                            && TryGetLoadedObject(mrGo.m_MeshFilter.m_Mesh, out var mrMesh))
                                        {
                                            AddUsedBy(mrMesh, $"Mesh (SubMesh {m})");
                                        }
                                        break;
                                    }
                                }
                            }
                            if (MatchesPPtr(mr.m_GameObject))
                            {
                                AddUsedBy(mr, "Component");
                            }
                            break;

                        case Animator animator:
                            if (MatchesPPtr(animator.m_Avatar))
                            {
                                AddUsedBy(animator, "Animator Avatar");
                                if (TryGetLoadedGameObject(animator.m_GameObject, out var animGo))
                                {
                                    AddUsedBy(animGo, "Animator GameObject");
                                    if (avatarTarget != null)
                                    {
                                        var subtreeMeshes = new List<Mesh>();
                                        var subtreeRenderers = new List<Renderer>();
                                        AssetExportService.CollectSubtreeMeshesAndRenderers(animGo, subtreeMeshes, subtreeRenderers);
                                        foreach (var subMesh in subtreeMeshes)
                                        {
                                            AddUses(subMesh, "Rig Mesh");
                                        }
                                    }
                                }
                            }
                            if (MatchesPPtr(animator.m_Controller))
                            {
                                AddUsedBy(animator, "Animator Controller");
                            }
                            if (MatchesPPtr(animator.m_GameObject))
                            {
                                AddUsedBy(animator, "Component");
                            }
                            break;

                        case AnimatorController controller:
                            if (controller.m_AnimationClips != null)
                            {
                                for (var c = 0; c < controller.m_AnimationClips.Length; c++)
                                {
                                    if (MatchesPPtr(controller.m_AnimationClips[c]))
                                    {
                                        AddUsedBy(controller, "AnimatorController Clip");
                                        break;
                                    }
                                }
                            }
                            if (controller.m_StateMachineBehaviours != null)
                            {
                                for (var b = 0; b < controller.m_StateMachineBehaviours.Length; b++)
                                {
                                    if (MatchesPPtr(controller.m_StateMachineBehaviours[b]))
                                    {
                                        AddUsedBy(controller, "StateMachineBehaviour");
                                        break;
                                    }
                                }
                            }
                            break;

                        case AnimatorOverrideController overrideController:
                            if (MatchesPPtr(overrideController.m_Controller))
                            {
                                AddUsedBy(overrideController, "Override Controller");
                            }
                            if (overrideController.m_Clips != null)
                            {
                                foreach (var pair in overrideController.m_Clips)
                                {
                                    if (MatchesPPtr(pair?.m_OriginalClip))
                                    {
                                        AddUsedBy(overrideController, "Original Clip");
                                        break;
                                    }
                                    if (MatchesPPtr(pair?.m_OverrideClip))
                                    {
                                        AddUsedBy(overrideController, "Override Clip");
                                        break;
                                    }
                                }
                            }
                            break;

                        case Animation legacyAnim:
                            if (legacyAnim.m_Animations != null)
                            {
                                for (var c = 0; c < legacyAnim.m_Animations.Length; c++)
                                {
                                    if (MatchesPPtr(legacyAnim.m_Animations[c]))
                                    {
                                        AddUsedBy(legacyAnim, "Legacy Animation");
                                        break;
                                    }
                                }
                            }
                            break;

                        case AnimationClip otherClip:
                            if (otherClip.m_PPtrCurves != null)
                            {
                                var matched = false;
                                foreach (var curve in otherClip.m_PPtrCurves)
                                {
                                    if (curve?.curve == null) continue;
                                    foreach (var keyframe in curve.curve)
                                    {
                                        if (MatchesPPtr(keyframe?.value))
                                        {
                                            AddUsedBy(otherClip, string.IsNullOrWhiteSpace(curve.attribute) ? "PPtr Curve" : $"Animation ({curve.attribute})");
                                            matched = true;
                                            break;
                                        }
                                    }
                                    if (matched) break;
                                }
                            }
                            break;

                        case GameObject go:
                            if (go.m_Components != null)
                            {
                                for (var c = 0; c < go.m_Components.Length; c++)
                                {
                                    if (MatchesPPtr(go.m_Components[c]))
                                    {
                                        AddUsedBy(go, "Owning GameObject");
                                        break;
                                    }
                                }
                            }
                            break;

                        case VideoPlayer vp:
                            if (MatchesPPtr(vp.m_VideoClip))
                            {
                                AddUsedBy(vp, "VideoPlayer");
                            }
                            break;

                        case MonoBehaviour mb:
                            if (MatchesPPtr(mb.m_Script))
                            {
                                AddUsedBy(mb, "Script Instance");
                            }
                            if (MatchesPPtr(mb.m_GameObject))
                            {
                                AddUsedBy(mb, "Component");
                            }
                            break;
                    }
                }
            }

            // Eager enrichment: if target is an AnimationClip, also discover Avatars via AnimatorController -> Animator in loaded files.
            if (clipTarget != null && loadedFiles.Count > 0)
            {
                var referencingControllerIds = new HashSet<string>(
                    referencedByOut
                        .Where(r => r.AssetType is ClassIDType.AnimatorController or ClassIDType.AnimatorOverrideController)
                        .Select(r => r.AssetUniqueId),
                    StringComparer.Ordinal);
                if (referencingControllerIds.Count > 0)
                {
                    for (var f = 0; f < loadedFiles.Count; f++)
                    {
                        var file = loadedFiles[f];
                        if (file == null) continue;
                        AssetStudio.Object[] objs;
                        lock (file)
                        {
                            objs = file.Objects.ToArray();
                        }

                        for (var i = 0; i < objs.Length; i++)
                        {
                            if (objs[i] is Animator anim
                                && anim.m_Controller != null
                                && !anim.m_Controller.IsNull
                                && TryResolvePPtrKey(anim.assetsFile, anim.m_Controller.m_FileID, anim.m_Controller.m_PathID, out var ctrlId)
                                && referencingControllerIds.Contains(ctrlId))
                            {
                                if (anim.m_Avatar != null && !anim.m_Avatar.IsNull)
                                {
                                    var avatarItem = BuildReferenceItemFromPPtr(
                                        anim.assetsFile,
                                        anim.m_Avatar.m_FileID,
                                        anim.m_Avatar.m_PathID,
                                        "Target Avatar (via Controller)",
                                        AssetReferenceDirection.References,
                                        ClassIDType.Avatar,
                                        projectIndex);
                                    if (avatarItem != null)
                                    {
                                        referencesOut.Add(avatarItem);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void ExtractGenericPPtrsFromDictionary(
            IDictionary dict,
            string prefix,
            int depth,
            SerializedFile sourceFile,
            string selfUniqueId,
            ProjectIndex? projectIndex,
            List<AssetReferenceItem> referencesOut,
            ref int foundCount,
            CancellationToken cancellationToken)
        {
            if (depth > MaxGenericPPtrDepth || foundCount >= MaxGenericPPtrsPerObject)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadPPtrFields(dict, out var fileId, out var pathId))
            {
                if (pathId != 0)
                {
                    var label = string.IsNullOrWhiteSpace(prefix) ? "PPtr" : $"PPtr ({prefix})";
                    var item = BuildReferenceItemFromPPtr(
                        sourceFile,
                        fileId,
                        pathId,
                        label,
                        AssetReferenceDirection.References,
                        ClassIDType.UnknownType,
                        projectIndex);
                    if (item != null && !string.Equals(item.AssetUniqueId, selfUniqueId, StringComparison.Ordinal))
                    {
                        referencesOut.Add(item);
                        foundCount++;
                    }
                }

                return;
            }

            foreach (DictionaryEntry entry in dict)
            {
                if (foundCount >= MaxGenericPPtrsPerObject)
                {
                    return;
                }

                var key = entry.Key?.ToString() ?? string.Empty;
                if (key is "m_Script" or "m_GameObject")
                {
                    continue;
                }

                var nextPrefix = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
                var value = entry.Value;
                if (value is IDictionary childDict)
                {
                    ExtractGenericPPtrsFromDictionary(
                        childDict,
                        nextPrefix,
                        depth + 1,
                        sourceFile,
                        selfUniqueId,
                        projectIndex,
                        referencesOut,
                        ref foundCount,
                        cancellationToken);
                }
                else if (value is IEnumerable enumerable and not string)
                {
                    var elemIdx = 0;
                    foreach (var elem in enumerable)
                    {
                        if (foundCount >= MaxGenericPPtrsPerObject || elemIdx >= 64)
                        {
                            break;
                        }

                        if (elem is IDictionary elemDict)
                        {
                            ExtractGenericPPtrsFromDictionary(
                                elemDict,
                                $"{nextPrefix}[{elemIdx.ToString(CultureInfo.InvariantCulture)}]",
                                depth + 1,
                                sourceFile,
                                selfUniqueId,
                                projectIndex,
                                referencesOut,
                                ref foundCount,
                                cancellationToken);
                        }

                        elemIdx++;
                    }
                }
            }
        }

        private static bool TryReadPPtrFields(IDictionary dict, out int fileId, out long pathId)
        {
            fileId = 0;
            pathId = 0;
            if (dict.Count != 2)
            {
                return false;
            }

            if (!dict.Contains("m_FileID") || !dict.Contains("m_PathID"))
            {
                return false;
            }

            var rawFile = dict["m_FileID"];
            var rawPath = dict["m_PathID"];
            fileId = rawFile switch
            {
                int i => i,
                long l => (int)l,
                uint ui => unchecked((int)ui),
                _ => 0
            };
            pathId = rawPath switch
            {
                long l => l,
                int i => i,
                uint ui => ui,
                ulong ul => unchecked((long)ul),
                _ => 0L
            };
            return true;
        }

        private static bool PPtrMatchesTarget<T>(
            PPtr<T>? pptr,
            SerializedFile? ownerFile,
            SerializedFile? targetFile,
            string targetFileName,
            long targetPathId,
            string targetUniqueId) where T : AssetStudio.Object
        {
            if (pptr == null || pptr.IsNull || pptr.m_PathID != targetPathId)
            {
                return false;
            }

            if (ownerFile == null)
            {
                return targetFile == null;
            }

            if (pptr.m_FileID == 0)
            {
                if (targetFile != null)
                {
                    return ReferenceEquals(ownerFile, targetFile)
                        || string.Equals(ownerFile.fileName, targetFile.fileName, StringComparison.OrdinalIgnoreCase);
                }

                return !string.IsNullOrWhiteSpace(targetFileName)
                    && string.Equals(ownerFile.fileName, targetFileName, StringComparison.OrdinalIgnoreCase);
            }

            if (pptr.m_FileID > 0 && ownerFile.m_Externals != null && pptr.m_FileID - 1 < ownerFile.m_Externals.Count)
            {
                var ext = ownerFile.m_Externals[pptr.m_FileID - 1];
                var extFileName = NormalizeExternalFileName(ext?.fileName, ext?.pathName);
                if (!string.IsNullOrWhiteSpace(extFileName) && !string.IsNullOrWhiteSpace(targetFileName))
                {
                    return string.Equals(extFileName, targetFileName, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (TryResolvePPtrKey(ownerFile, pptr.m_FileID, pptr.m_PathID, out var resolvedKey))
            {
                return string.Equals(resolvedKey, targetUniqueId, StringComparison.Ordinal);
            }

            return false;
        }

        private static bool TryResolvePPtrKey(SerializedFile? ownerFile, int fileId, long pathId, out string uniqueId)
        {
            uniqueId = string.Empty;
            if (ownerFile == null || pathId == 0)
            {
                return false;
            }

            if (fileId == 0)
            {
                uniqueId = AssetHandle.BuildUniqueID(ownerFile, pathId);
                return true;
            }

            if (fileId > 0 && ownerFile.m_Externals != null && fileId - 1 < ownerFile.m_Externals.Count)
            {
                var ext = ownerFile.m_Externals[fileId - 1];
                if (ownerFile.assetsManager != null
                    && ownerFile.assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile)
                    && extFile != null)
                {
                    uniqueId = AssetHandle.BuildUniqueID(extFile, pathId);
                    return true;
                }

                var extName = NormalizeExternalFileName(ext?.fileName, ext?.pathName);
                if (!string.IsNullOrWhiteSpace(extName))
                {
                    uniqueId = $"{extName}#{pathId.ToString(CultureInfo.InvariantCulture)}";
                    return true;
                }
            }

            return false;
        }

        private static AssetReferenceItem? BuildReferenceItemFromPPtr(
            SerializedFile? sourceFile,
            int fileId,
            long pathId,
            string relation,
            AssetReferenceDirection direction,
            ClassIDType fallbackType,
            ProjectIndex? projectIndex)
        {
            if (pathId == 0)
            {
                return null;
            }

            // 1) Try already-loaded object in memory (NEVER materialize from disk during reference listing).
            AssetStudio.Object? loadedObj = null;
            string serializedFileName = string.Empty;
            string uniqueId = string.Empty;

            if (sourceFile != null)
            {
                if (fileId == 0)
                {
                    serializedFileName = sourceFile.fileName ?? string.Empty;
                    uniqueId = AssetHandle.BuildUniqueID(sourceFile, pathId);
                    sourceFile.TryGetObject(pathId, out loadedObj);
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
                        extFile.TryGetObject(pathId, out loadedObj);
                    }
                    else if (!string.IsNullOrWhiteSpace(serializedFileName))
                    {
                        uniqueId = $"{serializedFileName}#{pathId.ToString(CultureInfo.InvariantCulture)}";
                    }
                }
            }

            if (loadedObj != null)
            {
                return BuildReferenceItemFromObject(loadedObj, relation, direction);
            }

            // 2) Try ProjectIndex handle lookup (O(1) in-memory lookup, zero disk I/O).
            AssetHandle? handle = !string.IsNullOrWhiteSpace(uniqueId)
                ? projectIndex?.GetHandle(uniqueId)
                : null;
            if (handle == null && projectIndex != null && !string.IsNullOrWhiteSpace(serializedFileName))
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
                    Direction = direction
                };
            }

            // 3) Fallback metadata from ObjectInfo in sourceFile if local.
            var resolvedType = fallbackType;
            if (fileId == 0 && sourceFile != null && sourceFile.ObjectInfoDic.TryGetValue(pathId, out var objInfo))
            {
                resolvedType = (ClassIDType)objInfo.classID;
            }

            var typeName = resolvedType != ClassIDType.UnknownType
                ? AssetItem.GetCachedTypeString(resolvedType)
                : "Object";
            return new AssetReferenceItem
            {
                AssetUniqueId = uniqueId,
                Name = $"{typeName} ({pathId.ToString(CultureInfo.InvariantCulture)})",
                AssetType = resolvedType,
                TypeName = typeName,
                Relation = relation,
                PathId = pathId,
                SerializedFileName = serializedFileName,
                Direction = direction
            };
        }

        private static AssetReferenceItem BuildReferenceItemFromObject(
            AssetStudio.Object obj,
            string relation,
            AssetReferenceDirection direction)
        {
            var uniqueId = obj.assetsFile != null
                ? AssetHandle.BuildUniqueID(obj.assetsFile, obj.m_PathID)
                : $"runtime:{obj.type}:{obj.m_PathID.ToString(CultureInfo.InvariantCulture)}";

            return new AssetReferenceItem
            {
                AssetUniqueId = uniqueId,
                Name = GetObjectDisplayName(obj),
                AssetType = obj.type,
                TypeName = AssetItem.GetCachedTypeString(obj.type),
                Relation = relation,
                PathId = obj.m_PathID,
                SerializedFileName = obj.assetsFile?.fileName ?? string.Empty,
                Direction = direction
            };
        }

        private static bool TryGetLoadedGameObject(PPtr<GameObject>? pptr, out GameObject gameObject)
        {
            return TryGetLoadedObject(pptr, out gameObject);
        }

        private static bool TryGetLoadedObject<T>(PPtr<T>? pptr, out T result) where T : AssetStudio.Object
        {
            result = null!;
            if (pptr == null || pptr.IsNull)
            {
                return false;
            }

            if (pptr.TryGetAssetsFile(out var sourceFile)
                && sourceFile != null
                && sourceFile.TryGetObject(pptr.m_PathID, out var existing)
                && existing is T typed)
            {
                result = typed;
                return true;
            }

            return false;
        }

        private static string GetObjectDisplayName(AssetStudio.Object? obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            switch (obj)
            {
                case NamedObject named when !string.IsNullOrWhiteSpace(named.m_Name):
                    return named.m_Name;
                case GameObject go when !string.IsNullOrWhiteSpace(go.m_Name):
                    return go.m_Name;
                case MonoBehaviour mb when !string.IsNullOrWhiteSpace(mb.m_Name):
                    return mb.m_Name;
                case MonoBehaviour mb when TryGetLoadedObject(mb.m_Script, out var script) && !string.IsNullOrWhiteSpace(script.m_ClassName):
                    return script.m_ClassName;
                case Component comp when TryGetLoadedGameObject(comp.m_GameObject, out var ownerGo) && !string.IsNullOrWhiteSpace(ownerGo.m_Name):
                    return $"{ownerGo.m_Name} ({obj.type})";
                default:
                    return $"{obj.type} ({obj.m_PathID.ToString(CultureInfo.InvariantCulture)})";
            }
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

        private static List<AssetReferenceItem> DeduplicateAndSort(
            List<AssetReferenceItem> items,
            string selfUniqueId,
            int maxItems)
        {
            if (items.Count == 0)
            {
                return new List<AssetReferenceItem>();
            }

            // Group identical targets so e.g. a Material using a Texture in both _MainTex and _EmissionMap
            // appears once with merged relation "_MainTex, _EmissionMap".
            var mergedByKey = new Dictionary<string, (AssetReferenceItem Best, List<string> Relations)>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(selfUniqueId)
                    && string.Equals(item.AssetUniqueId, selfUniqueId, StringComparison.Ordinal))
                {
                    continue;
                }

                var key = !string.IsNullOrWhiteSpace(item.AssetUniqueId)
                    ? item.AssetUniqueId
                    : $"{(int)item.AssetType}:{item.PathId.ToString(CultureInfo.InvariantCulture)}";

                if (!mergedByKey.TryGetValue(key, out var entry))
                {
                    var relations = new List<string>(2);
                    if (!string.IsNullOrWhiteSpace(item.Relation))
                    {
                        relations.Add(item.Relation);
                    }
                    mergedByKey[key] = (item, relations);
                }
                else
                {
                    // Prefer richer name / non-unknown type if duplicate arrived from a secondary source.
                    var best = entry.Best;
                    var replaceBest = (best.AssetType == ClassIDType.UnknownType && item.AssetType != ClassIDType.UnknownType)
                        || (string.IsNullOrWhiteSpace(best.Name) && !string.IsNullOrWhiteSpace(item.Name));
                    if (replaceBest)
                    {
                        best = item;
                    }

                    if (!string.IsNullOrWhiteSpace(item.Relation)
                        && !entry.Relations.Contains(item.Relation, StringComparer.OrdinalIgnoreCase)
                        && entry.Relations.Count < 4)
                    {
                        entry.Relations.Add(item.Relation);
                    }

                    mergedByKey[key] = (best, entry.Relations);
                }
            }

            var result = new List<AssetReferenceItem>(mergedByKey.Count);
            foreach (var entry in mergedByKey.Values)
            {
                var best = entry.Best;
                var relation = entry.Relations.Count > 0 ? string.Join(", ", entry.Relations) : best.Relation;
                result.Add(new AssetReferenceItem
                {
                    AssetUniqueId = best.AssetUniqueId,
                    Name = best.Name,
                    AssetType = best.AssetType,
                    TypeName = !string.IsNullOrWhiteSpace(best.TypeName)
                        ? best.TypeName
                        : AssetItem.GetCachedTypeString(best.AssetType),
                    Relation = relation,
                    PathId = best.PathId,
                    SerializedFileName = best.SerializedFileName,
                    Direction = best.Direction
                });
            }

            result.Sort((a, b) =>
            {
                var typeCmp = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
                if (typeCmp != 0) return typeCmp;
                var nameCmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (nameCmp != 0) return nameCmp;
                return a.PathId.CompareTo(b.PathId);
            });

            if (result.Count > maxItems)
            {
                result.RemoveRange(maxItems, result.Count - maxItems);
            }

            return result;
        }
    }
}
