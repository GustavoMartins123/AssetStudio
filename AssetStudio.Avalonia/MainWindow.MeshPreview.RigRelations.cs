using AssetStudio;
using AssetStudio.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private List<string> LoadAvatarMeshAssetIdsForPreview(Avatar avatar)
    {
        if (!assetsManager.LazyLoading || avatar.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var avatarAssetId = AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAvatarMeshAssetIds(folderPath, signature, avatarAssetId);
    }

    private void CacheAvatarMeshRelation(Avatar avatar, Mesh mesh)
    {
        lock (previewCacheLock)
        {
            avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
            avatarMeshCache[avatar] = mesh;

            avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
            if (!avatarMeshesCache.TryGetValue(avatar, out var meshes))
            {
                meshes = new List<Mesh>();
                avatarMeshesCache[avatar] = meshes;
            }

            if (!meshes.Any(existing => AreSamePreviewObject(existing, mesh)))
            {
                meshes.Add(mesh);
            }

            meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
            meshAvatarCache[mesh] = avatar;
        }
    }

    private Mesh? ResolveFirstMeshForAvatar(Avatar avatar)
    {
        lock (previewCacheLock)
        {
            if (avatarMeshesCache != null
                && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes)
                && cachedMeshes.Count > 0)
            {
                return cachedMeshes[0];
            }

            if (avatarMeshCache != null
                && avatarMeshCache.TryGetValue(avatar, out var cachedMesh)
                && cachedMesh != null)
            {
                return cachedMesh;
            }
        }

        foreach (var meshId in LoadAvatarMeshAssetIdsForPreview(avatar))
        {
            var mesh = ResolveMeshPreviewCandidate(meshId);
            if (mesh == null)
            {
                continue;
            }

            CacheAvatarMeshRelation(avatar, mesh);
            return mesh;
        }

        return assetsManager.LazyLoading ? null : FindMeshesForAvatar(avatar).FirstOrDefault();
    }

    private Dictionary<string, List<string>> LoadAvatarMeshAssetIdsByAvatarIdsForPreview(IReadOnlyList<string> avatarIds)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || avatarIds.Count == 0)
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAvatarMeshAssetIdsByAvatarIds(folderPath, signature, avatarIds);
    }

    private SkinnedMeshRenderer? FindSkinnedRendererForAvatarMesh(Avatar? avatar, Mesh mesh)
    {
        if (mesh.assetsFile == null)
        {
            return null;
        }

        var semanticRenderers = TryLoadSkinnedRenderersForMeshFromSemanticCache(mesh);
        if (semanticRenderers.Count > 0)
        {
            return semanticRenderers
                .OrderByDescending(renderer => ScoreSkinnedRendererForMesh(renderer, avatar, mesh))
                .FirstOrDefault();
        }

        return null;
    }

    private List<SkinnedMeshRenderer> TryLoadSkinnedRenderersForMeshFromSemanticCache(Mesh mesh)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return new List<SkinnedMeshRenderer>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<SkinnedMeshRenderer>();
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        if (string.IsNullOrWhiteSpace(meshAssetId))
        {
            return new List<SkinnedMeshRenderer>();
        }

        lock (previewCacheLock)
        {
            meshSkinnedRenderersCache ??= new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
            if (meshSkinnedRenderersCache.TryGetValue(meshAssetId, out var cachedRenderers))
            {
                return new List<SkinnedMeshRenderer>(cachedRenderers);
            }
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var rendererIds = _sqliteCache.LoadMeshRendererAssetIds(folderPath, signature, meshAssetId, "SkinnedMeshRenderer");
        var renderers = new List<SkinnedMeshRenderer>();
        var seenRendererIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rendererId in rendererIds)
        {
            if (string.IsNullOrWhiteSpace(rendererId) || !seenRendererIds.Add(rendererId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(rendererId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is SkinnedMeshRenderer renderer)
            {
                renderers.Add(renderer);
            }
        }

        lock (previewCacheLock)
        {
            meshSkinnedRenderersCache ??= new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
            meshSkinnedRenderersCache[meshAssetId] = renderers;
        }
        return new List<SkinnedMeshRenderer>(renderers);
    }

    private static int ScoreSkinnedRendererForMesh(SkinnedMeshRenderer renderer, Avatar? avatar, Mesh mesh)
    {
        var score = 1000;
        if (renderer.m_Bones != null && mesh.m_BindPose != null)
        {
            if (renderer.m_Bones.Length == mesh.m_BindPose.Length)
            {
                score += 150;
            }
            else if (renderer.m_Bones.Length > 0)
            {
                score += 25;
            }
        }

        if (avatar != null && renderer.assetsFile == avatar.assetsFile)
        {
            score += 20;
        }

        return score;
    }

    private static bool TryBuildSkeletonFromSkinnedRenderer(
        Avatar? avatar,
        Mesh mesh,
        SkinnedMeshRenderer renderer,
        out global::OpenTK.Mathematics.Vector3[] bonePositions,
        out int[] parentIndices,
        out string[] boneNames)
    {
        bonePositions = Array.Empty<global::OpenTK.Mathematics.Vector3>();
        parentIndices = Array.Empty<int>();
        boneNames = Array.Empty<string>();

        if (renderer.assetsFile == null
            || renderer.m_Bones == null
            || renderer.m_Bones.Length == 0
            || mesh.m_BindPose == null
            || mesh.m_BindPose.Length == 0)
        {
            return false;
        }

        var boneCount = Math.Min(renderer.m_Bones.Length, mesh.m_BindPose.Length);
        if (boneCount <= 0)
        {
            return false;
        }

        var boneTransforms = new Transform?[boneCount];
        var transformIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < boneCount; i++)
        {
            var transform = ResolveTransformBackground(renderer.assetsFile, renderer.m_Bones[i]);
            boneTransforms[i] = transform;
            var transformId = GetTransformRelationId(renderer.assetsFile, renderer.m_Bones[i], transform);
            if (!string.IsNullOrEmpty(transformId) && !transformIndexById.ContainsKey(transformId))
            {
                transformIndexById[transformId] = i;
            }
        }

        if (transformIndexById.Count == 0)
        {
            return false;
        }

        bonePositions = new global::OpenTK.Mathematics.Vector3[boneCount];
        parentIndices = new int[boneCount];
        boneNames = new string[boneCount];
        Array.Fill(parentIndices, -1);

        for (var i = 0; i < boneCount; i++)
        {
            bonePositions[i] = GetBindPosePosition(mesh.m_BindPose[i]);
            boneNames[i] = GetTransformDisplayName(renderer.assetsFile, boneTransforms[i])
                ?? (mesh.m_BoneNameHashes != null && i < mesh.m_BoneNameHashes.Length
                    ? avatar?.FindBonePath(mesh.m_BoneNameHashes[i]) ?? string.Empty
                    : string.Empty);
        }

        for (var i = 0; i < boneCount; i++)
        {
            var current = boneTransforms[i];
            if (current?.m_Father == null || current.m_Father.IsNull)
            {
                continue;
            }

            // Walk up m_Father past intermediate non-SMR transforms until finding the nearest
            // ancestor bone that belongs to this SkinnedMeshRenderer (prevents detached sub-chains).
            var visitedParents = new HashSet<long> { current.m_PathID };
            var walk = current;
            for (var depth = 0; walk?.m_Father != null && !walk.m_Father.IsNull && depth < 64; depth++)
            {
                var sourceFile = walk.assetsFile ?? renderer.assetsFile;
                var parentTransform = ResolveTransformBackground(sourceFile, walk.m_Father);
                if (parentTransform == null || !visitedParents.Add(parentTransform.m_PathID))
                {
                    break;
                }

                var parentId = GetSemanticAssetId(parentTransform);
                if (!string.IsNullOrEmpty(parentId) && transformIndexById.TryGetValue(parentId, out var parentIndex))
                {
                    parentIndices[i] = parentIndex;
                    break;
                }

                walk = parentTransform;
            }
        }

        return parentIndices.Any(index => index >= 0);
    }

    private static List<Transform> CollectAncestorAndFileAnimatorRoots(
        SerializedFile sourceFile,
        SkinnedMeshRenderer renderer)
    {
        var roots = new List<Transform>();
        var seenIds = new HashSet<long>();

        void TryAddAnimatorTransform(Animator? anim)
        {
            if (anim == null)
            {
                return;
            }

            var animFile = anim.assetsFile ?? sourceFile;
            var animGo = ResolveGameObjectBackground(animFile, anim.m_GameObject);
            var animTr = animGo != null ? Services.SemanticGraphService.ResolveTransformForGameObjectBackground(animFile, animGo) : null;
            if (animTr != null && seenIds.Add(animTr.m_PathID))
            {
                roots.Add(animTr);
            }
        }

        AssetStudio.Object[] objs;
        lock (sourceFile)
        {
            objs = sourceFile.Objects.ToArray();
        }

        foreach (var anim in objs.OfType<Animator>())
        {
            TryAddAnimatorTransform(anim);
        }

        return roots;
    }

    private static bool TryBuildTransformHierarchySkeletonForMesh(
        Mesh mesh,
        SkinnedMeshRenderer renderer,
        HashSet<uint>? preferredClipBindingPaths,
        out Services.AvatarDefaultSkeletonData skeleton,
        out Dictionary<long, int> transformPathIdToNodeIdx)
    {
        skeleton = null!;
        transformPathIdToNodeIdx = new Dictionary<long, int>();

        var sourceFile = renderer?.assetsFile ?? mesh?.assetsFile;
        if (sourceFile == null || renderer?.m_Bones == null || renderer.m_Bones.Length == 0)
        {
            return false;
        }

        var requiredTransforms = new List<Transform>(renderer.m_Bones.Length + 1);
        for (var i = 0; i < renderer.m_Bones.Length; i++)
        {
            var tr = ResolveTransformBackground(sourceFile, renderer.m_Bones[i]);
            if (tr != null)
            {
                requiredTransforms.Add(tr);
            }
        }

        if (renderer.m_RootBone != null && !renderer.m_RootBone.IsNull)
        {
            var rootBoneTr = ResolveTransformBackground(sourceFile, renderer.m_RootBone);
            if (rootBoneTr != null)
            {
                requiredTransforms.Add(rootBoneTr);
            }
        }

        if (requiredTransforms.Count == 0)
        {
            return false;
        }

        var animatorRoots = CollectAncestorAndFileAnimatorRoots(sourceFile, renderer);
        var animatorRootIds = new HashSet<long>(animatorRoots.Select(t => t.m_PathID));

        // Pick the outermost Animator (or top-most Transform root) enclosing this renderer's bones
        // so RestWorldMatrices live in character root space (matching mesh.m_BindPose).
        Transform? skeletonRoot = null;
        var seed = requiredTransforms[0];
        var visited = new HashSet<long>();
        Transform? topTransform = seed;
        Transform? outermostAnimator = null;
        for (var cur = seed; cur != null && visited.Add(cur.m_PathID); )
        {
            topTransform = cur;
            if (animatorRootIds.Contains(cur.m_PathID))
            {
                outermostAnimator = cur;
            }

            if (cur.m_Father == null || cur.m_Father.IsNull)
            {
                break;
            }

            cur = ResolveTransformBackground(cur.assetsFile ?? sourceFile, cur.m_Father);
        }

        skeletonRoot = outermostAnimator ?? topTransform;
        if (skeletonRoot == null)
        {
            return false;
        }

        return Services.MeshPreviewSkeletonBuilder.TryBuildSkeletonFromTransformHierarchy(
            sourceFile,
            skeletonRoot,
            requiredTransforms,
            animatorRoots,
            preferredClipBindingPaths,
            out skeleton,
            out transformPathIdToNodeIdx);
    }

    private static string GetTransformRelationId(SerializedFile sourceFile, PPtr<Transform> transformPtr, Transform? transform)
    {
        var transformId = GetSemanticAssetId(transform);
        if (!string.IsNullOrEmpty(transformId))
        {
            return transformId;
        }

        return GetSemanticAssetIdFromPPtr(sourceFile, transformPtr, ClassIDType.Transform);
    }

    private static string? GetTransformDisplayName(SerializedFile sourceFile, Transform? transform)
    {
        if (transform == null)
        {
            return null;
        }

        var transformSource = transform.assetsFile ?? sourceFile;
        var gameObject = ResolveGameObjectBackground(transformSource, transform.m_GameObject);
        return string.IsNullOrWhiteSpace(gameObject?.m_Name) ? null : gameObject.m_Name;
    }

    private static global::OpenTK.Mathematics.Vector3 GetBindPosePosition(AssetStudio.Matrix4x4 bindPose)
    {
        return MeshPreviewSkeletonBuilder.GetBindPosePosition(bindPose);
    }

    private Mesh? FindBestMeshForAvatar(Avatar avatar)
    {
        return FindMeshesForAvatar(avatar).FirstOrDefault();
    }

    private Avatar? FindBestAvatarForMesh(Mesh mesh)
    {
        lock (previewCacheLock)
        {
            if (meshAvatarCache != null && meshAvatarCache.TryGetValue(mesh, out var avatar) && avatar != null)
            {
                return avatar;
            }
        }

        return TryLoadAvatarsForMeshFromSemanticCache(mesh).FirstOrDefault();
    }

    private Mesh? GetCachedMeshForAvatar(Avatar avatar)
    {
        return ResolveFirstMeshForAvatar(avatar);
    }

    private static Dictionary<Avatar, List<Mesh>> BuildAvatarMeshListCache(Dictionary<Avatar, Mesh?>? singleMeshCache)
    {
        var result = new Dictionary<Avatar, List<Mesh>>();
        if (singleMeshCache == null)
        {
            return result;
        }

        foreach (var entry in singleMeshCache)
        {
            result[entry.Key] = entry.Value != null
                ? new List<Mesh> { entry.Value }
                : new List<Mesh>();
        }

        return result;
    }

    private List<Mesh> FindMeshesForAvatar(Avatar avatar)
    {
        var meshes = new List<Mesh>();
        lock (previewCacheLock)
        {
            if (avatarMeshesCache != null
                && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes)
                && cachedMeshes.Count > 0)
            {
                return new List<Mesh>(cachedMeshes);
            }

            if (avatarMeshCache != null
                && avatarMeshCache.TryGetValue(avatar, out var cachedMesh)
                && cachedMesh != null)
            {
                meshes.Add(cachedMesh);
            }
        }

        foreach (var mesh in TryLoadMeshesForAvatarFromSemanticCache(avatar))
        {
            if (!meshes.Any(existing => ReferenceEquals(existing, mesh)))
            {
                meshes.Add(mesh);
            }
        }

        if (meshes.Count > 0)
        {
            lock (previewCacheLock)
            {
                avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
                avatarMeshesCache[avatar] = meshes;

                avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
                avatarMeshCache[avatar] = meshes[0];

                meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
                foreach (var mesh in meshes)
                {
                    meshAvatarCache[mesh] = avatar;
                }
            }
        }

        return meshes;
    }

    private List<Mesh> TryLoadMeshesForAvatarFromSemanticCache(Avatar avatar)
    {
        var meshes = new List<Mesh>();
        if (!assetsManager.LazyLoading || avatar.assetsFile == null || currentScanResult == null)
        {
            return meshes;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return meshes;
        }

        var avatarAssetId = AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var meshIds = _sqliteCache.LoadAvatarMeshAssetIds(folderPath, signature, avatarAssetId);
        var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meshId in meshIds)
        {
            if (string.IsNullOrWhiteSpace(meshId) || !seenMeshIds.Add(meshId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(meshId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Mesh mesh)
            {
                meshes.Add(mesh);
            }
        }

        return meshes;
    }

    private List<Avatar> TryLoadAvatarsForMeshFromSemanticCache(Mesh mesh)
    {
        var avatars = new List<Avatar>();
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return avatars;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return avatars;
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var avatarIds = _sqliteCache.LoadMeshAvatarAssetIds(folderPath, signature, meshAssetId);
        var seenAvatarIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var avatarId in avatarIds)
        {
            if (string.IsNullOrWhiteSpace(avatarId) || !seenAvatarIds.Add(avatarId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(avatarId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Avatar avatar)
            {
                avatars.Add(avatar);
            }
        }

        if (avatars.Count > 0)
        {
            lock (previewCacheLock)
            {
                meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
                meshAvatarCache[mesh] = avatars[0];

                avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
                avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
                foreach (var avatar in avatars)
                {
                    avatarMeshCache.TryAdd(avatar, mesh);
                    if (!avatarMeshesCache.TryGetValue(avatar, out var meshes))
                    {
                        meshes = new List<Mesh>();
                        avatarMeshesCache[avatar] = meshes;
                    }

                    if (!meshes.Any(existing => ReferenceEquals(existing, mesh)))
                    {
                        meshes.Add(mesh);
                    }
                }
            }
        }

        return avatars;
    }
}
