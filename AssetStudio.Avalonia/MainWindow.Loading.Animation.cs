using System;
using System.Collections.Generic;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static void AddAnimatorAvatarMeshRelations(
            SerializedFile sourceFile,
            Animator animator,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            int animatorCount,
            SemanticAssetRelations relations)
        {
            if (animator == null)
            {
                return;
            }

            var avatarId = string.Empty;
            var avatarFileId = 0;
            var avatarPathId = 0L;
            if (animator.m_Avatar != null && !animator.m_Avatar.IsNull)
            {
                var avatar = ResolveAvatarBackground(sourceFile, animator.m_Avatar);
                avatarId = GetSemanticAssetId(avatar);
                if (string.IsNullOrEmpty(avatarId))
                {
                    avatarId = GetSemanticAssetIdFromPPtr(sourceFile, animator.m_Avatar, ClassIDType.Avatar);
                }

                avatarFileId = animator.m_Avatar.m_FileID;
                avatarPathId = animator.m_Avatar.m_PathID;

                if (!string.IsNullOrEmpty(avatarId))
                {
                    AddAssetEdge(
                        relations,
                        animator,
                        "Avatar",
                        "m_Avatar",
                        0,
                        avatarId,
                        avatarFileId,
                        avatarPathId);
                }
            }

            var animatorGameObject = ResolveGameObjectBackground(sourceFile, animator.m_GameObject);
            var animatorId = GetSemanticAssetId(animator);
            var rootGameObjectId = animatorGameObject != null
                ? GetSemanticGameObjectId(sourceFile, animator.m_GameObject, animatorGameObject)
                : string.Empty;
            var controller = ResolveRuntimeAnimatorControllerBackground(sourceFile, animator.m_Controller);
            var controllerId = GetSemanticAssetId(controller);
            if (string.IsNullOrEmpty(controllerId) && animator.m_Controller != null && !animator.m_Controller.IsNull)
            {
                controllerId = GetSemanticAssetIdFromPPtr(sourceFile, animator.m_Controller, ClassIDType.RuntimeAnimatorController);
            }

            var meshMatches = FindSkinnedMeshesForAnimator(sourceFile, animatorGameObject, skinnedMeshBindings, animatorCount).ToList();
            AddAnimatorModelGroupRelations(
                sourceFile,
                relations,
                animator,
                animatorId,
                animatorGameObject,
                rootGameObjectId,
                avatarId,
                controllerId,
                meshMatches);

            var meshSlotIndex = 0;
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
                if (string.IsNullOrEmpty(binding.MeshId) || !seenMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                AddAssetEdge(
                    relations,
                    animator,
                    "Mesh",
                    "AnimatorMesh",
                    meshSlotIndex,
                    binding.MeshId,
                    binding.MeshFileId,
                    binding.MeshPathId);

                if (!string.IsNullOrEmpty(avatarId))
                {
                    AddAssetEdgeById(
                        relations,
                        avatarId,
                        "Mesh",
                        "AnimatorMesh",
                        meshSlotIndex,
                        binding.MeshId,
                        avatarFileId,
                        avatarPathId,
                        binding.MeshFileId,
                        binding.MeshPathId,
                        isResolved: true);
                }

                meshSlotIndex++;
            }

            if (controller != null)
            {
                AddAnimatorControllerClipRelations(
                    sourceFile,
                    relations,
                    controller,
                    animator,
                    avatarId,
                    avatarFileId,
                    avatarPathId,
                    depth: 0);
            }
        }

        private static void AddAnimatorModelGroupRelations(
            SerializedFile sourceFile,
            SemanticAssetRelations relations,
            Animator animator,
            string animatorId,
            GameObject? animatorGameObject,
            string rootGameObjectId,
            string avatarId,
            string controllerId,
            IReadOnlyList<SkinnedMeshSemanticMatch> meshMatches)
        {
            if (meshMatches.Count == 0)
            {
                return;
            }

            var groupId = BuildAnimatorModelGroupId(sourceFile, animator, animatorId, rootGameObjectId, avatarId, controllerId);
            if (string.IsNullOrEmpty(groupId))
            {
                return;
            }

            var groupName = !string.IsNullOrWhiteSpace(animatorGameObject?.m_Name)
                ? animatorGameObject!.m_Name
                : "Animator Model";
            var confidence = meshMatches.Min(match => match.Confidence);
            var confidenceReason = meshMatches.All(match => match.ConfidenceReason == meshMatches[0].ConfidenceReason)
                ? meshMatches[0].ConfidenceReason
                : "Mixed animator mesh evidence";

            relations.ModelGroups.Add(new SemanticModelGroupRelation(
                groupId,
                "Animator",
                groupName,
                rootGameObjectId,
                animatorGameObject?.m_Name ?? string.Empty,
                animatorId,
                avatarId,
                controllerId,
                sourceFile.fileName ?? string.Empty,
                confidence,
                confidenceReason));

            var slotIndex = 0;
            var seenParts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
                var rendererId = GetSemanticAssetId(binding.Renderer);
                if (string.IsNullOrEmpty(binding.MeshId) || string.IsNullOrEmpty(rendererId))
                {
                    continue;
                }

                var partKey = $"{binding.MeshId}\u001f{rendererId}";
                if (!seenParts.Add(partKey))
                {
                    continue;
                }

                var gameObjectId = binding.GameObject != null
                    ? GetSemanticAssetId(binding.GameObject)
                    : string.Empty;
                relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId,
                    binding.MeshId,
                    rendererId,
                    "SkinnedMeshRenderer",
                    gameObjectId,
                    binding.GameObject?.m_Name ?? string.Empty,
                    slotIndex,
                    BuildLocalToGroupRootMatrix(sourceFile, binding.GameObject, rootGameObjectId),
                    match.Confidence,
                    match.ConfidenceReason));
                slotIndex++;
            }
        }

        private static string BuildAnimatorModelGroupId(
            SerializedFile sourceFile,
            Animator animator,
            string animatorId,
            string rootGameObjectId,
            string avatarId,
            string controllerId)
        {
            if (!string.IsNullOrEmpty(animatorId))
            {
                return $"modelgroup:animator:{animatorId}";
            }

            if (!string.IsNullOrEmpty(rootGameObjectId))
            {
                return $"modelgroup:root:{rootGameObjectId}";
            }

            var fallbackId = !string.IsNullOrEmpty(avatarId)
                ? avatarId
                : !string.IsNullOrEmpty(controllerId)
                    ? controllerId
                    : sourceFile.fileName ?? string.Empty;
            return string.IsNullOrEmpty(fallbackId)
                ? string.Empty
                : $"modelgroup:fallback:{fallbackId}:{animator.m_PathID}";
        }

        private static IEnumerable<SkinnedMeshSemanticMatch> FindSkinnedMeshesForAnimator(
            SerializedFile sourceFile,
            GameObject? animatorGameObject,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            int animatorCount)
        {
            if (skinnedMeshBindings.Count == 0)
            {
                yield break;
            }

            if (animatorGameObject != null)
            {
                var matchedAny = false;
                foreach (var binding in skinnedMeshBindings)
                {
                    if (binding.GameObject != null
                        && IsSameOrDescendantGameObject(sourceFile, binding.GameObject, animatorGameObject))
                    {
                        matchedAny = true;
                        yield return new SkinnedMeshSemanticMatch(binding, 100, "Animator hierarchy descendant");
                    }
                }

                if (matchedAny)
                {
                    yield break;
                }
            }

            if (animatorCount == 1)
            {
                foreach (var binding in skinnedMeshBindings)
                {
                    yield return new SkinnedMeshSemanticMatch(binding, 20, "Single animator file fallback");
                }
            }
        }

        private static void AddAnimatorControllerClipRelations(
            SerializedFile fallbackSourceFile,
            SemanticAssetRelations relations,
            RuntimeAnimatorController controller,
            Animator animator,
            string avatarId,
            int avatarFileId,
            long avatarPathId,
            int depth)
        {
            if (depth > 4 || controller == null)
            {
                return;
            }

            var controllerSourceFile = controller.assetsFile ?? fallbackSourceFile;
            var controllerId = GetSemanticAssetId(controller);
            if (string.IsNullOrEmpty(controllerId))
            {
                controllerId = GetSemanticAssetIdFromPPtr(fallbackSourceFile, animator.m_Controller, ClassIDType.RuntimeAnimatorController);
            }

            var slotIndex = 0;
            foreach (var clipRef in EnumerateAnimationClipPointers(controller))
            {
                if (clipRef == null || clipRef.IsNull)
                {
                    slotIndex++;
                    continue;
                }

                var clipId = GetSemanticAssetIdFromPPtr(controllerSourceFile, clipRef, ClassIDType.AnimationClip);
                if (string.IsNullOrEmpty(clipId))
                {
                    slotIndex++;
                    continue;
                }

                AddAssetEdge(
                    relations,
                    animator,
                    "AnimationClip",
                    "AnimatorControllerClip",
                    slotIndex,
                    clipId,
                    clipRef.m_FileID,
                    clipRef.m_PathID);

                if (!string.IsNullOrEmpty(controllerId))
                {
                    AddAssetEdgeById(
                        relations,
                        controllerId,
                        "AnimationClip",
                        "m_AnimationClips",
                        slotIndex,
                        clipId,
                        0,
                        controller.m_PathID,
                        clipRef.m_FileID,
                        clipRef.m_PathID,
                        isResolved: true);
                }

                if (!string.IsNullOrEmpty(avatarId))
                {
                    AddAssetEdgeById(
                        relations,
                        clipId,
                        "Avatar",
                        "AnimatorAvatar",
                        slotIndex,
                        avatarId,
                        clipRef.m_FileID,
                        clipRef.m_PathID,
                        avatarFileId,
                        avatarPathId,
                        isResolved: true);
                }

                slotIndex++;
            }

            if (controller is AnimatorOverrideController overrideController
                && overrideController.m_Controller != null
                && !overrideController.m_Controller.IsNull)
            {
                var baseController = ResolveRuntimeAnimatorControllerBackground(controllerSourceFile, overrideController.m_Controller);
                if (baseController != null && !ReferenceEquals(baseController, controller))
                {
                    AddAnimatorControllerClipRelations(
                        controllerSourceFile,
                        relations,
                        baseController,
                        animator,
                        avatarId,
                        avatarFileId,
                        avatarPathId,
                        depth + 1);
                }
            }
        }

        private static IEnumerable<PPtr<AnimationClip>> EnumerateAnimationClipPointers(RuntimeAnimatorController controller)
        {
            if (controller is AnimatorController animatorController)
            {
                foreach (var clip in animatorController.m_AnimationClips ?? Array.Empty<PPtr<AnimationClip>>())
                {
                    yield return clip;
                }
            }
            else if (controller is AnimatorOverrideController overrideController)
            {
                foreach (var clipOverride in overrideController.m_Clips ?? Array.Empty<AnimationClipOverride>())
                {
                    if (clipOverride.m_OverrideClip != null && !clipOverride.m_OverrideClip.IsNull)
                    {
                        yield return clipOverride.m_OverrideClip;
                    }
                    else if (clipOverride.m_OriginalClip != null && !clipOverride.m_OriginalClip.IsNull)
                    {
                        yield return clipOverride.m_OriginalClip;
                    }
                }
            }
        }

        private readonly struct SkinnedMeshSemanticMatch
        {
            public SkinnedMeshSemanticMatch(SkinnedMeshSemanticBinding binding, int confidence, string confidenceReason)
            {
                Binding = binding;
                Confidence = confidence;
                ConfidenceReason = confidenceReason;
            }

            public SkinnedMeshSemanticBinding Binding { get; }
            public int Confidence { get; }
            public string ConfidenceReason { get; }
        }

        private static void BuildAnimationPreviewIndexesBackground(
            List<SerializedFile> assetsFileList,
            out Dictionary<AnimationClip, Avatar?> animationClipAvatarCacheOut,
            out Dictionary<Avatar, Mesh?> avatarMeshCacheOut,
            out Dictionary<Mesh, Avatar?> meshAvatarCacheOut,
            out Dictionary<AnimationClip, HashSet<uint>> animationClipTransformBindingsCacheOut)
        {
            var clips = assetsFileList.SelectMany(f => f.Objects).OfType<AnimationClip>().ToArray();
            var avatars = assetsFileList.SelectMany(f => f.Objects).OfType<Avatar>().ToArray();
            var meshes = assetsFileList.SelectMany(f => f.Objects).OfType<Mesh>()
                .Where(m => m.m_BoneNameHashes != null && m.m_BoneNameHashes.Length > 0
                    && m.m_BindPose != null && m.m_BindPose.Length > 0)
                .ToArray();

            var animationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache[clip] = GetTransformBindingPathsBackground(clip);
            }

            var avatarMeshCache = new Dictionary<Avatar, Mesh?>(avatars.Length);
            foreach (var avatar in avatars)
            {
                avatarMeshCache[avatar] = FindBestMeshForAvatarBackground(avatar, meshes);
            }

            var meshAvatarCache = new Dictionary<Mesh, Avatar?>(meshes.Length);
            foreach (var mesh in meshes)
            {
                meshAvatarCache[mesh] = FindBestAvatarForMeshBackground(mesh, avatars);
            }

            var animationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths);
                animationClipAvatarCache[clip] = FindBestAvatarForAnimationClipBackground(clip, bindingPaths ?? new HashSet<uint>(), avatars);
            }

            animationClipAvatarCacheOut = animationClipAvatarCache;
            avatarMeshCacheOut = avatarMeshCache;
            meshAvatarCacheOut = meshAvatarCache;
            animationClipTransformBindingsCacheOut = animationClipTransformBindingsCache;
        }

        private static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip)
        {
            var result = new HashSet<uint>();
            var bindings = clip.m_ClipBindingConstant;
            if (bindings == null && clip.m_MuscleClip?.m_Clip != null)
            {
                bindings = clip.m_MuscleClip.m_Clip.ConvertValueArrayToGenericBinding();
            }

            if (bindings?.genericBindings != null)
            {
                foreach (var binding in bindings.genericBindings)
                {
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        result.Add(binding.path);
                    }
                }
            }

            return result;
        }

        private static Avatar? FindBestAvatarForAnimationClipBackground(AnimationClip clip, HashSet<uint> bindingPaths, Avatar[] avatars)
        {
            if (bindingPaths.Count == 0)
            {
                return null;
            }

            Avatar? bestAvatar = null;
            int bestScore = 0;
            var clipName = NormalizeAnimatorSearchKey(clip.m_Name);

            foreach (var avatar in avatars)
            {
                if (avatar.m_TOS == null || avatar.m_TOS.Length == 0)
                {
                    continue;
                }

                var avatarPathHashes = new HashSet<uint>(avatar.m_TOS.Select(x => x.Key));
                var overlap = bindingPaths.Count(avatarPathHashes.Contains);
                if (!IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap))
                {
                    continue;
                }

                var score = overlap * 100;
                if (avatar.assetsFile == clip.assetsFile) score += 20;

                var avatarName = NormalizeAnimatorSearchKey(avatar.m_Name.Replace("Avatar", string.Empty));
                if (!string.IsNullOrEmpty(avatarName) && clipName.Contains(avatarName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAvatar = avatar;
                }
            }

            return bestAvatar;
        }

        private static Mesh? FindBestMeshForAvatarBackground(Avatar avatar, Mesh[] meshes)
        {
            var avatarBoneIds = avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null
                ? new HashSet<uint>(avatar.m_Avatar.m_AvatarSkeleton.m_ID)
                : new HashSet<uint>();
            if (avatarBoneIds.Count == 0)
            {
                return null;
            }

            Mesh? bestMesh = null;
            int bestScore = 0;
            var avatarName = avatar.m_Name.Replace("Avatar", string.Empty).Trim();

            foreach (var mesh in meshes)
            {
                var overlap = mesh.m_BoneNameHashes.Count(avatarBoneIds.Contains);
                if (!IsStrongMeshAvatarMatch(mesh.m_BoneNameHashes.Length, overlap))
                {
                    continue;
                }

                var score = overlap * 100 + Math.Min(mesh.m_BoneNameHashes.Length, 40);
                if (mesh.assetsFile == avatar.assetsFile) score += 20;
                if (!string.IsNullOrEmpty(avatarName) && mesh.m_Name.Contains(avatarName, StringComparison.OrdinalIgnoreCase)) score += 15;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMesh = mesh;
                }
            }

            return bestMesh;
        }

        private static Avatar? FindBestAvatarForMeshBackground(Mesh mesh, Avatar[] avatars)
        {
            var meshBoneHashes = mesh.m_BoneNameHashes != null
                ? new HashSet<uint>(mesh.m_BoneNameHashes)
                : new HashSet<uint>();
            if (meshBoneHashes.Count == 0)
            {
                return null;
            }

            Avatar? bestAvatar = null;
            int bestScore = 0;
            var meshName = mesh.m_Name.ToLowerInvariant();

            foreach (var avatar in avatars)
            {
                if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID == null)
                {
                    continue;
                }

                var overlap = avatar.m_Avatar.m_AvatarSkeleton.m_ID.Count(meshBoneHashes.Contains);
                if (!IsStrongMeshAvatarMatch(meshBoneHashes.Count, overlap))
                {
                    continue;
                }

                var score = overlap * 100;
                if (avatar.assetsFile == mesh.assetsFile) score += 20;

                var avatarName = avatar.m_Name.Replace("Avatar", string.Empty).Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(avatarName) && meshName.Contains(avatarName)) score += 15;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAvatar = avatar;
                }
            }

            return bestAvatar;
        }

        private static bool IsStrongAnimationAvatarMatch(int bindingCount, int overlap)
        {
            if (bindingCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = bindingCount < 6 ? bindingCount : Math.Max(6, bindingCount / 20);
            return overlap >= minimum;
        }

        private static bool IsStrongMeshAvatarMatch(int boneCount, int overlap)
        {
            if (boneCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = boneCount < 6 ? boneCount : Math.Max(6, boneCount / 10);
            return overlap >= minimum;
        }
    }
}
